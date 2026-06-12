using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows;

namespace SX3_SCANER.Helper
{
    internal sealed class UpdateService
    {
        internal const string ReleasesPageUrl =
            "https://github.com/hieuvipro94x/sx3-scanner-release/releases";

        private const string LatestReleaseApiUrl =
            "https://api.github.com/repos/hieuvipro94x/sx3-scanner-release/releases/latest";
        private const string InstallerFileName = "SX3ScannerSetup.exe";
        private const string UserAgent = "SX3Scanner-Updater";
        private const string EnabledSetting = "UpdateCheckOnStartup";
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(10);
        private static readonly object StartupCheckLock = new object();
        private static readonly object LogLock = new object();
        private static readonly string LogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "JBZVN",
            "SX3 Scanner",
            "logs",
            "update.log");

        private static bool _startupCheckStarted;
        private static bool _startupCheckFinished;
        private static UpdateInfo _startupCheckResult;
        private string _verifiedInstallerPath;
        private string _verifiedInstallerSha256;

        internal bool LastCheckSucceeded { get; private set; }
        internal string LastStatusMessage { get; private set; }

        internal async Task<UpdateInfo> CheckForUpdateAsync(bool showErrors)
        {
            if (!showErrors && !IsStartupCheckEnabled())
            {
                LastCheckSucceeded = true;
                LastStatusMessage = "Tự động cập nhật đã tắt.";
                Log("Startup check skipped because UpdateCheckOnStartup=false.");
                return null;
            }

            if (!showErrors && !TryBeginStartupCheck())
            {
                LastCheckSucceeded = true;
                LastStatusMessage = _startupCheckResult == null
                    ? "Đã kiểm tra cập nhật lúc khởi động."
                    : "Có bản mới: V" + _startupCheckResult.Version;
                return _startupCheckResult;
            }

            try
            {
                LastCheckSucceeded = false;
                Log("Update check started. GitHub API URL: " + LatestReleaseApiUrl);

                UpdateInfo update = await GetLatestReleaseAsync();
                LastCheckSucceeded = true;

                if (!update.IsUpdateAvailable)
                {
                    LastStatusMessage = "Không có bản mới.";
                    SaveStartupResult(showErrors, null);
                    Log("No update available. Current version: " +
                        GetCurrentVersion() + ".");
                    return null;
                }

                LastStatusMessage = "Có bản mới: V" + update.Version;
                SaveStartupResult(showErrors, update);
                Log("Update available. Current=" + GetCurrentVersion() +
                    ", Latest=" + update.Version + ".");
                return update;
            }
            catch (TaskCanceledException ex)
            {
                return HandleCheckError(
                    ex.CancellationToken.IsCancellationRequested
                        ? "Đã hủy kiểm tra cập nhật."
                        : "GitHub API phản hồi quá thời gian.",
                    showErrors,
                    ex);
            }
            catch (Exception ex)
            {
                return HandleCheckError(
                    "Không kiểm tra được cập nhật lúc này. Vui lòng thử lại sau.",
                    showErrors,
                    ex);
            }
            finally
            {
                if (!showErrors)
                    FinishStartupCheck();
            }
        }

        internal async Task<string> DownloadAndVerifyAsync(UpdateInfo info)
        {
            ValidateUpdateInfo(info);

            string directory = Path.Combine(
                Path.GetTempPath(),
                "SX3Scanner",
                "Updates");
            Directory.CreateDirectory(directory);

            string installerPath = Path.Combine(directory, InstallerFileName);
            string temporaryPath = installerPath + "." +
                Guid.NewGuid().ToString("N") + ".download";

            Log("Download URL: " + info.DownloadUrl);
            Log("Expected SHA256: " + info.Sha256);

            try
            {
                using (var client = CreateHttpClient(DownloadTimeout))
                using (HttpResponseMessage response = await client.GetAsync(
                    info.DownloadUrl,
                    HttpCompletionOption.ResponseHeadersRead))
                {
                    ValidateFinalResponseUrl(response, "installer");
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new InvalidOperationException(
                            "Không tải được installer. HTTP " +
                            (int)response.StatusCode + " " + response.ReasonPhrase);
                    }

                    using (Stream source = await response.Content.ReadAsStreamAsync())
                    using (var target = new FileStream(
                        temporaryPath,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None))
                    {
                        await source.CopyToAsync(target);
                    }
                }

                var file = new FileInfo(temporaryPath);
                if (!file.Exists || file.Length <= 0)
                    throw new InvalidOperationException(
                        "Installer tải về không tồn tại hoặc có dung lượng bằng 0.");

                if (info.FileSize > 0 && file.Length != info.FileSize)
                    throw new InvalidOperationException(
                        "Dung lượng installer không khớp dữ liệu GitHub API.");

                string actualSha256 = ComputeSha256(temporaryPath);
                Log("Actual SHA256: " + actualSha256);
                if (!string.Equals(
                    actualSha256,
                    info.Sha256,
                    StringComparison.OrdinalIgnoreCase))
                {
                    Log("SHA256 verification failed. Installer rejected.");
                    throw new InvalidOperationException(
                        "SHA256 không khớp. Installer đã bị từ chối.");
                }

                TryDelete(installerPath);
                File.Move(temporaryPath, installerPath);
                _verifiedInstallerPath = Path.GetFullPath(installerPath);
                _verifiedInstallerSha256 = actualSha256;
                Log("SHA256 verification succeeded. Installer: " + installerPath);
                return installerPath;
            }
            catch (Exception ex)
            {
                TryDelete(temporaryPath);
                Log("Download or verification failed: " + ex);
                throw;
            }
        }

        internal bool TryStartInstallerAndExit(string installerPath)
        {
            try
            {
                ValidateInstallerPath(installerPath);
                string actualPath = Path.GetFullPath(installerPath);
                if (!string.Equals(
                        actualPath,
                        _verifiedInstallerPath,
                        StringComparison.OrdinalIgnoreCase) ||
                    string.IsNullOrWhiteSpace(_verifiedInstallerSha256))
                {
                    throw new InvalidOperationException(
                        "Installer chưa được xác thực trong phiên cập nhật này.");
                }

                string currentSha256 = ComputeSha256(actualPath);
                Log("Pre-launch SHA256: " + currentSha256);
                if (!string.Equals(
                    currentSha256,
                    _verifiedInstallerSha256,
                    StringComparison.OrdinalIgnoreCase))
                {
                    Log("Pre-launch SHA256 verification failed.");
                    throw new InvalidOperationException(
                        "Installer đã thay đổi sau khi xác thực và bị từ chối.");
                }

                Log("User confirmed update. Starting installer: " + installerPath);

                Process process = Process.Start(new ProcessStartInfo
                {
                    FileName = installerPath,
                    UseShellExecute = true
                });
                if (process == null)
                    throw new InvalidOperationException(
                        "Process.Start không khởi động được installer.");

                Log("Installer started. Shutting down SX3 Scanner.");
                _verifiedInstallerPath = null;
                _verifiedInstallerSha256 = null;
                Application.Current.Shutdown();
                return true;
            }
            catch (Win32Exception ex)
            {
                LastStatusMessage = ex.NativeErrorCode == 1223
                    ? "Người dùng đã hủy hoặc không cấp quyền chạy installer."
                    : "Không có quyền chạy installer.";
                Log("Installer start failed: " + ex);
                ShowError(LastStatusMessage);
                return false;
            }
            catch (Exception ex)
            {
                LastStatusMessage = "Không thể khởi động installer cập nhật.";
                Log("Installer start failed: " + ex);
                ShowError(LastStatusMessage);
                return false;
            }
        }

        internal void ReportDownloadError(Exception exception)
        {
            LastStatusMessage = exception is InvalidOperationException
                ? exception.Message
                : exception is TaskCanceledException
                    ? "Tải bản cập nhật quá thời gian."
                    : "Không tải được bản cập nhật lúc này. Vui lòng thử lại sau.";
            Log("Update preparation failed: " + exception);
            ShowError(LastStatusMessage);
        }

        internal static string GetCurrentVersionString()
        {
            return GetCurrentVersion().ToString(3);
        }

        internal static Version GetCurrentVersion()
        {
            string executablePath = Process.GetCurrentProcess().MainModule.FileName;
            FileVersionInfo info = FileVersionInfo.GetVersionInfo(executablePath);
            Version version;
            if (TryParseVersion(info.ProductVersion, out version) ||
                TryParseVersion(info.FileVersion, out version))
                return version;

            throw new InvalidOperationException(
                "Không xác định được phiên bản hiện tại của ứng dụng.");
        }

        internal static bool IsNewerVersion(Version latest, Version current)
        {
            if (latest == null)
                throw new ArgumentNullException("latest");
            if (current == null)
                throw new ArgumentNullException("current");
            return latest.CompareTo(current) > 0;
        }

        private async Task<UpdateInfo> GetLatestReleaseAsync()
        {
            ValidateSecureUri(LatestReleaseApiUrl, "GitHub API URL");

            using (var client = CreateHttpClient(RequestTimeout))
            using (HttpResponseMessage response =
                await client.GetAsync(LatestReleaseApiUrl))
            {
                ValidateFinalResponseUrl(response, "GitHub API");
                if (!response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException(
                        "GitHub API HTTP " + (int)response.StatusCode + " " +
                        response.ReasonPhrase);
                }

                string json = await response.Content.ReadAsStringAsync();
                GitHubRelease release =
                    JsonConvert.DeserializeObject<GitHubRelease>(json);
                return BuildUpdateInfo(release);
            }
        }

        private static UpdateInfo BuildUpdateInfo(GitHubRelease release)
        {
            if (release == null)
                throw new InvalidOperationException(
                    "GitHub API không trả về thông tin release.");

            Version latestVersion;
            if (!TryParseVersion(release.TagName, out latestVersion))
                throw new InvalidOperationException(
                    "GitHub release thiếu tag version hợp lệ.");

            string versionedAssetName =
                "SX3ScannerSetup-" + latestVersion + ".exe";
            GitHubAsset asset = (release.Assets ?? new List<GitHubAsset>())
                .Where(value =>
                    value != null &&
                    string.Equals(
                        value.State,
                        "uploaded",
                        StringComparison.OrdinalIgnoreCase) &&
                    (string.Equals(
                         value.Name,
                         versionedAssetName,
                         StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(
                         value.Name,
                         InstallerFileName,
                         StringComparison.OrdinalIgnoreCase)))
                .OrderBy(value =>
                    string.Equals(
                        value.Name,
                        versionedAssetName,
                        StringComparison.OrdinalIgnoreCase)
                        ? 0
                        : 1)
                .FirstOrDefault();

            if (asset == null)
                throw new InvalidOperationException(
                    "GitHub release không có installer đúng version.");

            Uri downloadUri = ValidateSecureUri(
                asset.BrowserDownloadUrl,
                "GitHub asset URL");
            string sha256 = ParseGitHubDigest(asset.Digest);
            Log("Selected GitHub asset: " + asset.Name);
            Log("GitHub asset URL: " + downloadUri.AbsoluteUri);
            Log("GitHub asset SHA256: " + sha256);

            return new UpdateInfo
            {
                Version = latestVersion.ToString(),
                TagName = release.TagName,
                ReleaseNotes = release.Body ?? string.Empty,
                FileName = asset.Name,
                FileSize = Math.Max(0, asset.Size),
                DownloadUrl = downloadUri.AbsoluteUri,
                Sha256 = sha256,
                IsUpdateAvailable =
                    IsNewerVersion(latestVersion, GetCurrentVersion())
            };
        }

        private static string ParseGitHubDigest(string digest)
        {
            const string prefix = "sha256:";
            if (string.IsNullOrWhiteSpace(digest) ||
                !digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "GitHub API không cung cấp SHA256 cho installer.");

            return NormalizeSha256(digest.Substring(prefix.Length));
        }

        private static void ValidateUpdateInfo(UpdateInfo info)
        {
            if (info == null)
                throw new ArgumentNullException("info");
            ValidateSecureUri(info.DownloadUrl, "GitHub asset URL");
            NormalizeSha256(info.Sha256);
            if (!string.Equals(
                Path.GetExtension(info.FileName),
                ".exe",
                StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "Asset cập nhật không phải file .exe.");
        }

        private static void ValidateInstallerPath(string installerPath)
        {
            if (string.IsNullOrWhiteSpace(installerPath) ||
                !File.Exists(installerPath))
                throw new FileNotFoundException(
                    "Không tìm thấy installer đã xác thực.",
                    installerPath);

            string expectedDirectory = Path.GetFullPath(Path.Combine(
                Path.GetTempPath(),
                "SX3Scanner",
                "Updates"));
            string actualPath = Path.GetFullPath(installerPath);
            if (!string.Equals(
                    Path.GetDirectoryName(actualPath),
                    expectedDirectory,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    Path.GetFileName(actualPath),
                    InstallerFileName,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "Installer không nằm trong thư mục cập nhật an toàn.");

            if (new FileInfo(actualPath).Length <= 0)
                throw new InvalidOperationException(
                    "Installer có dung lượng bằng 0.");
        }

        private static Uri ValidateSecureUri(string value, string fieldName)
        {
            Uri uri;
            if (string.IsNullOrWhiteSpace(value) ||
                !Uri.TryCreate(value, UriKind.Absolute, out uri) ||
                !string.Equals(
                    uri.Scheme,
                    Uri.UriSchemeHttps,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    fieldName + " phải là URL HTTPS hợp lệ.");

            IPAddress address;
            if (IPAddress.TryParse(uri.DnsSafeHost, out address))
                throw new InvalidOperationException(
                    fieldName + " không được dùng địa chỉ IP trực tiếp.");
            return uri;
        }

        private static void ValidateFinalResponseUrl(
            HttpResponseMessage response,
            string fieldName)
        {
            if (response.RequestMessage == null ||
                response.RequestMessage.RequestUri == null)
                throw new InvalidOperationException(
                    "Không xác định được URL phản hồi " + fieldName + ".");
            ValidateSecureUri(
                response.RequestMessage.RequestUri.AbsoluteUri,
                fieldName + " redirect");
        }

        private static string NormalizeSha256(string value)
        {
            string normalized = (value ?? string.Empty).Trim();
            if (normalized.Length != 64 ||
                normalized.Any(character => !Uri.IsHexDigit(character)))
                throw new InvalidOperationException("SHA256 không hợp lệ.");
            return normalized.ToLowerInvariant();
        }

        private static string ComputeSha256(string filePath)
        {
            using (var sha256 = SHA256.Create())
            using (var stream = File.OpenRead(filePath))
            {
                return string.Concat(
                    sha256.ComputeHash(stream).Select(value => value.ToString("x2")));
            }
        }

        private UpdateInfo HandleCheckError(
            string message,
            bool showErrors,
            Exception exception)
        {
            LastCheckSucceeded = false;
            LastStatusMessage = message;
            Log("Update check failed: " + exception);
            if (showErrors)
                ShowError(message);
            return null;
        }

        private static HttpClient CreateHttpClient(TimeSpan timeout)
        {
            var client = new HttpClient { Timeout = timeout };
            client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            client.DefaultRequestHeaders.Accept.ParseAdd(
                "application/vnd.github+json");
            client.DefaultRequestHeaders.Add(
                "X-GitHub-Api-Version",
                "2022-11-28");
            return client;
        }

        private static bool TryParseVersion(string value, out Version version)
        {
            version = null;
            if (string.IsNullOrWhiteSpace(value))
                return false;

            string normalized = value.Trim();
            if (normalized.StartsWith("v", StringComparison.OrdinalIgnoreCase))
                normalized = normalized.Substring(1);
            int suffixIndex = normalized.IndexOfAny(new[] { '-', '+' });
            if (suffixIndex >= 0)
                normalized = normalized.Substring(0, suffixIndex);
            return Version.TryParse(normalized, out version);
        }

        private static bool IsStartupCheckEnabled()
        {
            bool enabled;
            string configured = ConfigurationManager.AppSettings[EnabledSetting];
            return !bool.TryParse(configured, out enabled) || enabled;
        }

        private static bool TryBeginStartupCheck()
        {
            lock (StartupCheckLock)
            {
                if (_startupCheckFinished || _startupCheckStarted)
                    return false;
                _startupCheckStarted = true;
                return true;
            }
        }

        private static void FinishStartupCheck()
        {
            lock (StartupCheckLock)
            {
                _startupCheckStarted = false;
                _startupCheckFinished = true;
            }
        }

        private static void SaveStartupResult(bool showErrors, UpdateInfo info)
        {
            if (showErrors)
                return;
            lock (StartupCheckLock)
                _startupCheckResult = info;
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception ex)
            {
                Log("Could not delete temporary file '" + path + "': " + ex);
            }
        }

        private static void Log(string message)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") +
                " [UpdateService] " + (message ?? string.Empty);
            Debug.WriteLine(line);
            try
            {
                lock (LogLock)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(LogPath));
                    File.AppendAllText(
                        LogPath,
                        line + Environment.NewLine,
                        new UTF8Encoding(false));
                }
            }
            catch
            {
            }
        }

        private static void ShowError(string message)
        {
            MessageBox.Show(
                message,
                "SX3 Scanner - Lỗi cập nhật",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }

        private sealed class GitHubRelease
        {
            [JsonProperty("tag_name")]
            public string TagName { get; set; }

            [JsonProperty("body")]
            public string Body { get; set; }

            [JsonProperty("assets")]
            public List<GitHubAsset> Assets { get; set; }
        }

        private sealed class GitHubAsset
        {
            [JsonProperty("name")]
            public string Name { get; set; }

            [JsonProperty("state")]
            public string State { get; set; }

            [JsonProperty("size")]
            public long Size { get; set; }

            [JsonProperty("digest")]
            public string Digest { get; set; }

            [JsonProperty("browser_download_url")]
            public string BrowserDownloadUrl { get; set; }
        }
    }
}
