using Newtonsoft.Json;
using SX3_SCANER.Model;
using SX3_SCANER.Model.Respository;
using System;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace SX3_SCANER.Helper
{
    internal sealed class OnlineAnnouncementService : IDisposable
    {
        private enum AnnouncementSource
        {
            Cache,
            Snapshot,
            Realtime
        }

        private const string DefaultRealtimeUrl =
            "ws://127.0.0.1:5055/ws/announcements";
        private const string DefaultSnapshotUrl =
            "http://127.0.0.1:5055/api/announcements/current";
        private static readonly TimeSpan ReconnectDelay =
            TimeSpan.FromSeconds(5);

        private readonly HttpClient _httpClient;
        private readonly Dispatcher _dispatcher;
        private readonly CancellationTokenSource _lifetimeCts =
            new CancellationTokenSource();
        private readonly string _realtimeUrl;
        private readonly string _snapshotUrl;
        private readonly string _cachePath;
        private bool _isStarted;
        private bool _isDisposed;
        private string _lastAnnouncementFingerprint;
        private string _lastAnnouncementVersion = string.Empty;
        private string _lastAnnouncementUpdatedAt = string.Empty;
        private bool? _lastAnnouncementEnabled;

        public OnlineAnnouncementService()
        {
            _dispatcher = System.Windows.Application.Current?.Dispatcher ??
                Dispatcher.CurrentDispatcher;
            _realtimeUrl = ReadSetting(
                "AnnouncementRealtimeUrl",
                DefaultRealtimeUrl);
            _snapshotUrl = ReadSetting(
                "AnnouncementSnapshotUrl",
                DefaultSnapshotUrl);
            _cachePath = Path.Combine(
                DatabaseRepository.AppDataDirectory,
                "cache",
                "announcement.json");

            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(8)
            };
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("SX3Scanner");
            _httpClient.DefaultRequestHeaders.CacheControl =
                new CacheControlHeaderValue { NoCache = true };
        }

        public event EventHandler<AnnouncementInfo> AnnouncementChanged;

        public void Start()
        {
            if (_isDisposed || _isStarted)
                return;

            _isStarted = true;
            _ = RunAsync(_lifetimeCts.Token);
        }

        private async Task RunAsync(CancellationToken token)
        {
            try
            {
                await LoadCachedAnnouncementAsync().ConfigureAwait(false);
                await RunRealtimeLoopAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                StartupManager.Log(
                    "[Announcement] Client loop stopped unexpectedly. " + ex);
            }
        }

        private async Task RunRealtimeLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested && !_isDisposed)
            {
                try
                {
                    await LoadSnapshotAsync(token).ConfigureAwait(false);

                    using (var socket = new ClientWebSocket())
                    {
                        socket.Options.KeepAliveInterval =
                            TimeSpan.FromSeconds(20);
                        StartupManager.Log(
                            "[Announcement] Connecting realtime...");
                        await socket.ConnectAsync(
                            new Uri(_realtimeUrl),
                            token).ConfigureAwait(false);

                        StartupManager.Log(
                            "[Announcement] Realtime connected.");

                        await LoadSnapshotAsync(token).ConfigureAwait(false);

                        while (socket.State == WebSocketState.Open &&
                               !token.IsCancellationRequested)
                        {
                            string json = await ReceiveTextAsync(
                                socket,
                                token).ConfigureAwait(false);
                            if (json == null)
                                break;

                            StartupManager.Log(
                                "[Announcement] Realtime payload received.");
                            await ProcessAnnouncementJsonAsync(
                                json,
                                saveCache: true,
                                source: AnnouncementSource.Realtime)
                                .ConfigureAwait(false);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(
                        "[Announcement] Realtime unavailable: " + ex.Message);
                    StartupManager.Log(
                        "[Announcement] Realtime unavailable. " + ex.Message);
                }

                try
                {
                    StartupManager.Log(
                        "[Announcement] Reconnecting in 5 seconds.");
                    await Task.Delay(
                        ReconnectDelay,
                        token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        private async Task LoadCachedAnnouncementAsync()
        {
            try
            {
                if (!File.Exists(_cachePath))
                    return;

                string json = await Task.Run(
                    () => File.ReadAllText(_cachePath, Encoding.UTF8))
                    .ConfigureAwait(false);
                await ProcessAnnouncementJsonAsync(
                    json,
                    saveCache: false,
                    source: AnnouncementSource.Cache).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                StartupManager.Log(
                    "[Announcement] Invalid cached encoding, cache ignored. " +
                    ex.Message);
                TryDeleteCache();
            }
        }

        public Task LoadSnapshotAsync()
        {
            return LoadSnapshotAsync(_lifetimeCts.Token);
        }

        private async Task LoadSnapshotAsync(CancellationToken token)
        {
            if (_isDisposed || string.IsNullOrWhiteSpace(_snapshotUrl))
                return;

            try
            {
                string requestUrl = _snapshotUrl +
                    (_snapshotUrl.Contains("?") ? "&" : "?") +
                    "t=" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                using (HttpResponseMessage response =
                    await _httpClient.GetAsync(requestUrl, token)
                        .ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        StartupManager.Log(
                            "[Announcement] Snapshot HTTP " +
                            (int)response.StatusCode + " from " + _snapshotUrl);
                        return;
                    }

                    byte[] bytes = await response.Content.ReadAsByteArrayAsync()
                        .ConfigureAwait(false);
                    string json = Encoding.UTF8.GetString(bytes);
                    bool updated = await ProcessAnnouncementJsonAsync(
                        json,
                        saveCache: true,
                        source: AnnouncementSource.Snapshot)
                        .ConfigureAwait(false);
                    if (updated)
                    {
                        StartupManager.Log(
                            "[Announcement] Snapshot updated.");
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    "[Announcement] Snapshot unavailable: " + ex.Message);
                StartupManager.Log(
                    "[Announcement] Snapshot unavailable. " + ex.Message);
            }
        }

        private async Task<bool> ProcessAnnouncementJsonAsync(
            string json,
            bool saveCache,
            AnnouncementSource source)
        {
            AnnouncementInfo announcement;
            try
            {
                announcement = ParseAnnouncement(json);
            }
            catch (Exception ex)
            {
                LogInvalidPayload(source, ex.Message);
                return false;
            }

            if (ContainsInvalidEncoding(json) ||
                ContainsInvalidEncoding(announcement))
            {
                LogInvalidPayload(source, null);
                return false;
            }

            string fingerprint = BuildFingerprint(json);

            if (!HasAnnouncementChanged(announcement, fingerprint))
            {
                return false;
            }

            if (saveCache)
            {
                try
                {
                    string directory = Path.GetDirectoryName(_cachePath);
                    Directory.CreateDirectory(directory);
                    File.WriteAllText(_cachePath, json, new UTF8Encoding(false));
                }
                catch (Exception ex)
                {
                    StartupManager.Log(
                        "[Announcement] Could not save cache. " + ex.Message);
                }
            }

            await _dispatcher.InvokeAsync(() =>
            {
                if (_isDisposed)
                    return;

                _lastAnnouncementFingerprint = fingerprint;
                _lastAnnouncementVersion = announcement.Version;
                _lastAnnouncementUpdatedAt = announcement.UpdatedAt;
                _lastAnnouncementEnabled = announcement.Enabled;
                StartupManager.Log(
                    "[Announcement] Applied " +
                    source.ToString().ToLowerInvariant() +
                    " announcement. Enabled=" + announcement.Enabled +
                    ", Version=" + announcement.Version +
                    ", UpdatedAt=" + announcement.UpdatedAt);
                AnnouncementChanged?.Invoke(this, announcement);
            }).Task.ConfigureAwait(false);
            return true;
        }

        private bool HasAnnouncementChanged(
            AnnouncementInfo announcement,
            string fingerprint)
        {
            if (!_lastAnnouncementEnabled.HasValue)
                return true;

            if (_lastAnnouncementEnabled.Value != announcement.Enabled)
                return true;

            if (!announcement.Enabled)
            {
                return !string.Equals(
                    _lastAnnouncementFingerprint,
                    fingerprint,
                    StringComparison.Ordinal);
            }

            bool hasVersionIdentity =
                !string.IsNullOrWhiteSpace(announcement.Version) ||
                !string.IsNullOrWhiteSpace(announcement.UpdatedAt);
            if (hasVersionIdentity)
            {
                return !string.Equals(
                           _lastAnnouncementVersion,
                           announcement.Version,
                           StringComparison.Ordinal) ||
                       !string.Equals(
                           _lastAnnouncementUpdatedAt,
                           announcement.UpdatedAt,
                           StringComparison.Ordinal);
            }

            return !string.Equals(
                _lastAnnouncementFingerprint,
                fingerprint,
                StringComparison.Ordinal);
        }

        private static AnnouncementInfo ParseAnnouncement(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                throw new JsonException("Announcement JSON is empty.");

            string trimmed = json.TrimStart();
            if (trimmed.StartsWith("<") || trimmed.StartsWith("```"))
                throw new JsonException("Announcement response is not JSON.");

            AnnouncementInfo announcement =
                JsonConvert.DeserializeObject<AnnouncementInfo>(json);
            if (announcement == null)
                throw new JsonException("Announcement JSON is empty.");

            NormalizeAnnouncement(announcement);
            return announcement;
        }

        private static async Task<string> ReceiveTextAsync(
            ClientWebSocket socket,
            CancellationToken token)
        {
            var buffer = new byte[8192];
            using (var stream = new MemoryStream())
            {
                while (true)
                {
                    WebSocketReceiveResult result = await socket.ReceiveAsync(
                        new ArraySegment<byte>(buffer),
                        token).ConfigureAwait(false);

                    if (result.MessageType == WebSocketMessageType.Close)
                        return null;
                    if (result.MessageType != WebSocketMessageType.Text)
                        continue;

                    stream.Write(buffer, 0, result.Count);
                    if (result.EndOfMessage)
                        return Encoding.UTF8.GetString(stream.ToArray());
                }
            }
        }

        private static void NormalizeAnnouncement(AnnouncementInfo announcement)
        {
            announcement.Level = NormalizeLevel(announcement.Level);
            announcement.Mode = string.IsNullOrWhiteSpace(announcement.Mode)
                ? "single"
                : announcement.Mode.Trim().ToLowerInvariant();
            announcement.Title = string.IsNullOrWhiteSpace(announcement.Title)
                ? "THÔNG BÁO HỆ THỐNG"
                : announcement.Title.Trim();
            announcement.Message =
                announcement.Message?.Trim() ?? string.Empty;
            announcement.UpdatedAt =
                announcement.UpdatedAt?.Trim() ?? string.Empty;
            announcement.Version =
                announcement.Version?.Trim() ?? string.Empty;
            announcement.BackgroundColor =
                announcement.BackgroundColor?.Trim() ?? string.Empty;
            announcement.ForegroundColor =
                announcement.ForegroundColor?.Trim() ?? string.Empty;
            announcement.CreatedBy =
                announcement.CreatedBy?.Trim() ?? string.Empty;
            announcement.AutoHideSeconds =
                Math.Max(0, announcement.AutoHideSeconds);
            announcement.Priority = Math.Max(0, announcement.Priority);
            announcement.RotateSeconds =
                announcement.RotateSeconds < 3
                    ? 10
                    : announcement.RotateSeconds;
            announcement.RepeatSeconds =
                Math.Max(0, announcement.RepeatSeconds);
            announcement.MarqueeDirection = string.Equals(
                announcement.MarqueeDirection,
                "leftToRight",
                StringComparison.OrdinalIgnoreCase)
                    ? "leftToRight"
                    : "rightToLeft";
            announcement.MarqueeSpeed =
                announcement.MarqueeSpeed <= 0
                    ? 80
                    : announcement.MarqueeSpeed;
            announcement.MarqueeDelaySeconds =
                Math.Max(0, announcement.MarqueeDelaySeconds);
            NormalizeMessages(announcement);
        }

        private static void NormalizeMessages(AnnouncementInfo announcement)
        {
            if (announcement.Messages == null)
            {
                announcement.Messages =
                    new System.Collections.Generic.List<AnnouncementMessageInfo>();
                return;
            }

            for (int index = announcement.Messages.Count - 1;
                 index >= 0;
                 index--)
            {
                AnnouncementMessageInfo message =
                    announcement.Messages[index];
                if (message == null ||
                    string.IsNullOrWhiteSpace(message.Message))
                {
                    announcement.Messages.RemoveAt(index);
                    continue;
                }

                message.Level = NormalizeLevel(message.Level);
                message.Title = string.IsNullOrWhiteSpace(message.Title)
                    ? "THÔNG BÁO HỆ THỐNG"
                    : message.Title.Trim();
                message.Message = message.Message.Trim();
                message.BackgroundColor =
                    message.BackgroundColor?.Trim() ?? string.Empty;
                message.ForegroundColor =
                    message.ForegroundColor?.Trim() ?? string.Empty;
                if (message.AutoHideSeconds.HasValue)
                {
                    message.AutoHideSeconds = Math.Max(
                        0,
                        message.AutoHideSeconds.Value);
                }
            }
        }

        private static string NormalizeLevel(string level)
        {
            string normalized =
                (level ?? string.Empty).Trim().ToLowerInvariant();
            switch (normalized)
            {
                case "warning":
                case "error":
                case "success":
                    return normalized;
                default:
                    return "info";
            }
        }

        private static bool ContainsInvalidEncoding(
            AnnouncementInfo announcement)
        {
            if (announcement == null)
                return true;

            if (ContainsInvalidEncoding(announcement.Title) ||
                ContainsInvalidEncoding(announcement.Message))
            {
                return true;
            }

            if (announcement.Messages == null)
                return false;

            foreach (AnnouncementMessageInfo message in announcement.Messages)
            {
                if (message != null &&
                    (ContainsInvalidEncoding(message.Title) ||
                     ContainsInvalidEncoding(message.Message)))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool ContainsInvalidEncoding(string value)
        {
            if (string.IsNullOrEmpty(value))
                return false;

            string[] markers =
            {
                "\uFFFD",
                "THÃ",
                "Sáº",
                "áº",
                "á»",
                "Ä‘",
                "Ä\u0090",
                "Æ°",
                "Æ¡",
                "Ã´",
                "Ã¡",
                "Ã¢",
                "Ãª",
                "Ã©",
                "Ã¨",
                "ðŸ"
            };

            foreach (char character in value)
            {
                if (character >= '\u0080' && character <= '\u009F')
                    return true;
            }

            foreach (string marker in markers)
            {
                if (value.IndexOf(marker, StringComparison.Ordinal) >= 0)
                    return true;
            }

            return false;
        }

        private void LogInvalidPayload(
            AnnouncementSource source,
            string detail)
        {
            if (source == AnnouncementSource.Cache)
            {
                StartupManager.Log(
                    "[Announcement] Invalid cached encoding, cache ignored." +
                    (string.IsNullOrWhiteSpace(detail)
                        ? string.Empty
                        : " " + detail));
                TryDeleteCache();
                return;
            }

            StartupManager.Log(
                "[Announcement] Invalid " +
                source.ToString().ToLowerInvariant() +
                " encoding, payload ignored." +
                (string.IsNullOrWhiteSpace(detail)
                    ? string.Empty
                    : " " + detail));
        }

        private void TryDeleteCache()
        {
            try
            {
                if (File.Exists(_cachePath))
                    File.Delete(_cachePath);
            }
            catch (Exception ex)
            {
                StartupManager.Log(
                    "[Announcement] Could not delete invalid cache. " +
                    ex.Message);
            }
        }

        private static string BuildFingerprint(string json)
        {
            using (SHA256 sha256 = SHA256.Create())
            {
                byte[] hash = sha256.ComputeHash(
                    Encoding.UTF8.GetBytes(json ?? string.Empty));
                return BitConverter.ToString(hash).Replace("-", string.Empty);
            }
        }

        private static string ReadSetting(string key, string defaultValue)
        {
            string value = ConfigurationManager.AppSettings[key];
            return string.IsNullOrWhiteSpace(value)
                ? defaultValue
                : value.Trim();
        }

        public void Dispose()
        {
            if (_isDisposed)
                return;

            _isDisposed = true;
            _lifetimeCts.Cancel();
            _lifetimeCts.Dispose();
            _httpClient.Dispose();
        }
    }
}
