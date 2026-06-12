using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
    AnnouncementServerErrorLog.Write(
        "AppDomain.UnhandledException",
        eventArgs.ExceptionObject as Exception ??
        new Exception(eventArgs.ExceptionObject?.ToString() ?? "Unknown error"));

TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
{
    AnnouncementServerErrorLog.Write(
        "TaskScheduler.UnobservedTaskException",
        eventArgs.Exception);
    eventArgs.SetObserved();
};

try
{
    var builder = WebApplication.CreateBuilder(args);
    builder.Logging.ClearProviders();
    builder.Logging.AddConsole();
    builder.Logging.AddDebug();
    builder.WebHost.UseUrls(
        builder.Configuration["AnnouncementServer:Urls"] ??
        "http://127.0.0.1:5055");
    builder.Services.AddSingleton<AnnouncementBroker>();
    builder.Services.AddHttpClient();
    builder.Services.AddHostedService<AnnouncementGitHubPollingService>();
    builder.Services.AddHostedService<AnnouncementFileWatcherService>();
    builder.Services.AddHostedService<AnnouncementShutdownService>();
    builder.Services.AddHostedService<ParentProcessMonitorService>();

    var app = builder.Build();
    app.Lifetime.ApplicationStarted.Register(
        () => app.Logger.LogInformation("Server started"));
    app.Lifetime.ApplicationStopping.Register(
        () => app.Logger.LogInformation("Server stopping"));
    app.Lifetime.ApplicationStopped.Register(
        () => app.Logger.LogInformation("Server stopped"));

    app.UseWebSockets(new WebSocketOptions
    {
        KeepAliveInterval = TimeSpan.FromSeconds(20)
    });

    app.MapGet("/health", () => Results.Ok(new
    {
        status = "ok",
        utc = DateTimeOffset.UtcNow
    }));

    app.MapGet(
        "/api/announcements/current",
        (AnnouncementBroker broker) =>
            Results.Text(broker.CurrentJson, "application/json", Encoding.UTF8));

    app.MapGet(
        "/api/announcements",
        (AnnouncementBroker broker) =>
            Results.Text(broker.CurrentJson, "application/json", Encoding.UTF8));

    app.MapPost(
        "/api/announcements",
        async (
            HttpRequest request,
            AnnouncementBroker broker,
            IConfiguration configuration,
            CancellationToken token) =>
        {
            string expectedToken =
                Environment.GetEnvironmentVariable(
                    "SX3_ANNOUNCEMENT_ADMIN_TOKEN") ??
                configuration["AnnouncementServer:AdminToken"] ??
                string.Empty;

            if (string.IsNullOrWhiteSpace(expectedToken))
            {
                return Results.Problem(
                    "Announcement publishing is disabled because no admin token is configured.",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            string authorization = request.Headers.Authorization.ToString();
            const string bearerPrefix = "Bearer ";
            if (!authorization.StartsWith(
                    bearerPrefix,
                    StringComparison.OrdinalIgnoreCase) ||
                !TokensEqual(
                    authorization.Substring(bearerPrefix.Length).Trim(),
                    expectedToken))
            {
                return Results.Unauthorized();
            }

            try
            {
                using var reader = new StreamReader(
                    request.Body,
                    new UTF8Encoding(false, true),
                    detectEncodingFromByteOrderMarks: true,
                    bufferSize: 8192,
                    leaveOpen: true);
                string json = await reader.ReadToEndAsync(token);
                string normalized = AnnouncementBroker.ValidateAndNormalize(json);
                await broker.PublishAsync(normalized, token);
                return Results.Ok(new
                {
                    published = true,
                    clients = broker.ConnectedClientCount,
                    utc = DateTimeOffset.UtcNow
                });
            }
            catch (JsonException ex)
            {
                return Results.BadRequest(new
                {
                    error = "Invalid announcement JSON.",
                    detail = ex.Message
                });
            }
            catch (InvalidDataException ex)
            {
                return Results.BadRequest(new
                {
                    error = ex.Message
                });
            }
            catch (DecoderFallbackException)
            {
                return Results.BadRequest(new
                {
                    error = "Announcement payload is not valid UTF-8."
                });
            }
        });

    app.Map(
        "/ws/announcements",
        async (
            HttpContext context,
            AnnouncementBroker broker,
            CancellationToken token) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            using WebSocket socket =
                await context.WebSockets.AcceptWebSocketAsync();
            await broker.RunClientAsync(socket, token);
        });

    app.Run();
}
catch (Exception ex)
{
    AnnouncementServerErrorLog.Write("Startup", ex);
    Environment.ExitCode = 1;
}

static bool TokensEqual(string actual, string expected)
{
    byte[] actualBytes = Encoding.UTF8.GetBytes(actual ?? string.Empty);
    byte[] expectedBytes = Encoding.UTF8.GetBytes(expected ?? string.Empty);
    return actualBytes.Length == expectedBytes.Length &&
        CryptographicOperations.FixedTimeEquals(actualBytes, expectedBytes);
}

internal static class AnnouncementServerErrorLog
{
    private static readonly object SyncRoot = new();

    public static void Write(string source, Exception exception)
    {
        try
        {
            string logDirectory = Path.Combine(AppContext.BaseDirectory, "logs");
            Directory.CreateDirectory(logDirectory);
            string logPath = Path.Combine(
                logDirectory,
                "announcement-server-error.log");

            var message = new StringBuilder()
                .AppendLine("==================================================")
                .AppendLine("Time: " + DateTimeOffset.Now.ToString("O"))
                .AppendLine("Source: " + source);

            AppendException(message, exception, 0);

            lock (SyncRoot)
            {
                File.AppendAllText(
                    logPath,
                    message.ToString(),
                    new UTF8Encoding(false));
            }
        }
        catch
        {
        }
    }

    private static void AppendException(
        StringBuilder message,
        Exception exception,
        int level)
    {
        string label = level == 0
            ? "Exception"
            : "Inner exception " + level;
        message
            .AppendLine(label + " message: " + exception.Message)
            .AppendLine(label + " stack trace:")
            .AppendLine(exception.StackTrace ?? "(no stack trace)");

        if (exception.InnerException != null)
            AppendException(message, exception.InnerException, level + 1);
    }
}

internal sealed class AnnouncementBroker
{
    private const int MaximumAnnouncementBytes = 256 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly ConcurrentDictionary<Guid, AnnouncementClient> _clients =
        new();
    private readonly ILogger<AnnouncementBroker> _logger;
    private readonly SemaphoreSlim _publishLock = new(1, 1);
    private readonly string _announcementPath;
    private string _currentJson;

    public AnnouncementBroker(
        IWebHostEnvironment environment,
        ILogger<AnnouncementBroker> logger)
    {
        _logger = logger;
        _announcementPath = Path.Combine(
            environment.ContentRootPath,
            "announcement.json");

        string initialJson = File.Exists(_announcementPath)
            ? File.ReadAllText(_announcementPath, StrictUtf8)
            : "{\"enabled\":false,\"mode\":\"single\",\"message\":\"\"}";
        _currentJson = ValidateAndNormalize(initialJson);

        if (!File.Exists(_announcementPath))
        {
            File.WriteAllText(
                _announcementPath,
                _currentJson,
                new UTF8Encoding(false));
        }

        _logger.LogInformation(
            "Loaded announcement snapshot from {AnnouncementPath}.",
            _announcementPath);
    }

    public string AnnouncementPath => _announcementPath;

    public string CurrentJson => Volatile.Read(ref _currentJson);

    public int ConnectedClientCount => _clients.Count;

    public async Task RunClientAsync(
        WebSocket socket,
        CancellationToken token)
    {
        Guid clientId = Guid.NewGuid();
        var client = new AnnouncementClient(socket);

        try
        {
            await _publishLock.WaitAsync(token);
            try
            {
                _clients[clientId] = client;
                await client.SendAsync(CurrentJson, token);
                _logger.LogInformation(
                    "Client connected. ClientId={ClientId}, ConnectedClients={ConnectedClients}",
                    clientId,
                    ConnectedClientCount);
            }
            finally
            {
                _publishLock.Release();
            }

            var buffer = new byte[1024];

            while (socket.State == WebSocketState.Open &&
                   !token.IsCancellationRequested)
            {
                WebSocketReceiveResult result = await socket.ReceiveAsync(
                    new ArraySegment<byte>(buffer),
                    token);
                if (result.MessageType == WebSocketMessageType.Close)
                    break;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (WebSocketException)
        {
        }
        finally
        {
            _clients.TryRemove(clientId, out _);
            _logger.LogInformation(
                "Client disconnected. ClientId={ClientId}, ConnectedClients={ConnectedClients}",
                clientId,
                ConnectedClientCount);
            if (socket.State == WebSocketState.Open ||
                socket.State == WebSocketState.CloseReceived)
            {
                try
                {
                    await socket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "Closing",
                        CancellationToken.None);
                }
                catch
                {
                }
            }
        }
    }

    public async Task CloseAllClientsAsync(CancellationToken token)
    {
        KeyValuePair<Guid, AnnouncementClient>[] clients = _clients.ToArray();
        if (clients.Length == 0)
            return;

        _logger.LogInformation(
            "Closing {ConnectedClients} WebSocket client(s).",
            clients.Length);

        Task[] closeTasks = clients
            .Select(client => CloseClientAsync(client, token))
            .ToArray();
        await Task.WhenAll(closeTasks);
    }

    public async Task PublishAsync(string json, CancellationToken token)
    {
        string normalized = ValidateAndNormalize(json);
        await _publishLock.WaitAsync(token);
        try
        {
            await WriteAtomicallyAsync(normalized, token);
            Volatile.Write(ref _currentJson, normalized);
            int sentClientCount = await BroadcastAsync(normalized);
            _logger.LogInformation(
                "PublishAsync completed. SentClients={SentClients}, ConnectedClientCount={ConnectedClientCount}",
                sentClientCount,
                ConnectedClientCount);
        }
        finally
        {
            _publishLock.Release();
        }
    }

    public async Task<bool> ReloadFromDiskAsync(CancellationToken token)
    {
        await _publishLock.WaitAsync(token);
        try
        {
            string json = await ReadAndValidateWithRetryAsync(token);
            if (string.Equals(json, CurrentJson, StringComparison.Ordinal))
                return false;

            Volatile.Write(ref _currentJson, json);
            int sentClientCount = await BroadcastAsync(json);
            _logger.LogInformation(
                "Reloaded and broadcast announcement file {AnnouncementPath}. SentClients={SentClients}, ConnectedClientCount={ConnectedClientCount}",
                _announcementPath,
                sentClientCount,
                ConnectedClientCount);
            return true;
        }
        finally
        {
            _publishLock.Release();
        }
    }

    public static string ValidateAndNormalize(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new InvalidDataException("Announcement JSON is empty.");
        if (Encoding.UTF8.GetByteCount(json) > MaximumAnnouncementBytes)
            throw new InvalidDataException("Announcement JSON is too large.");

        JsonNode? node = JsonNode.Parse(json);
        if (node is not JsonObject root)
            throw new InvalidDataException(
                "Announcement JSON root must be an object.");
        using JsonDocument document = JsonDocument.Parse(json);
        if (ContainsInvalidEncoding(document.RootElement))
            throw new InvalidDataException(
                "Announcement JSON contains invalid cached encoding.");

        NormalizeAnnouncement(root);
        return root.ToJsonString(
            new JsonSerializerOptions
            {
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                WriteIndented = true
            });
    }

    private static void NormalizeAnnouncement(JsonObject root)
    {
        root["enabled"] = ReadBoolean(root, "enabled", false);
        root["mode"] = NormalizeMode(ReadString(root, "mode", "single"));
        root["level"] = NormalizeLevel(ReadString(root, "level", "info"));
        root["updatedAt"] = NormalizeText(
            ReadString(root, "updatedAt", string.Empty));
        root["version"] = NormalizeText(
            ReadString(root, "version", string.Empty));
        root["createdBy"] = NormalizeText(
            ReadString(root, "createdBy", string.Empty));
        root["title"] = NormalizeText(
            ReadString(root, "title", "THÔNG BÁO HỆ THỐNG"),
            "THÔNG BÁO HỆ THỐNG");
        root["message"] = NormalizeText(ReadString(root, "message", string.Empty));
        root["autoHideSeconds"] = Math.Max(0, ReadInt(root, "autoHideSeconds", 0));
        root["showCountdown"] = ReadBoolean(root, "showCountdown", false);
        root["pollSeconds"] = Math.Max(5, ReadInt(root, "pollSeconds", 15));
        root["rotateSeconds"] = Math.Max(3, ReadInt(root, "rotateSeconds", 10));
        root["repeatSeconds"] = Math.Max(0, ReadInt(root, "repeatSeconds", 600));
        root["showPopup"] = ReadBoolean(root, "showPopup", false);
        root["allowClose"] = ReadBoolean(root, "allowClose", false);
        root["forceUpdate"] = ReadBoolean(root, "forceUpdate", false);
        root["priority"] = Math.Max(0, ReadInt(root, "priority", 0));
        root["marqueeEnabled"] = ReadBoolean(
            root,
            "marqueeEnabled",
            false);
        root["marqueeSpeed"] = Math.Max(1, ReadInt(root, "marqueeSpeed", 80));
        root["marqueeDelaySeconds"] = Math.Max(
            0,
            ReadInt(root, "marqueeDelaySeconds", 10));
        root["marqueeDirection"] = string.Equals(
            ReadString(root, "marqueeDirection", "rightToLeft"),
            "leftToRight",
            StringComparison.OrdinalIgnoreCase)
                ? "leftToRight"
                : "rightToLeft";
        root["backgroundColor"] = NormalizeColor(
            ReadString(root, "backgroundColor", string.Empty));
        root["foregroundColor"] = NormalizeColor(
            ReadString(root, "foregroundColor", string.Empty));

        if (root["messages"] is not JsonArray messages)
        {
            root["messages"] = new JsonArray();
            return;
        }

        for (int index = messages.Count - 1; index >= 0; index--)
        {
            if (messages[index] is not JsonObject message)
            {
                messages.RemoveAt(index);
                continue;
            }

            string messageText = NormalizeText(
                ReadString(message, "message", string.Empty));
            if (string.IsNullOrWhiteSpace(messageText))
            {
                messages.RemoveAt(index);
                continue;
            }

            message["message"] = messageText;
            message["level"] = NormalizeLevel(
                ReadString(message, "level", "info"));
            message["title"] = NormalizeText(
                ReadString(message, "title", "THÔNG BÁO HỆ THỐNG"),
                "THÔNG BÁO HỆ THỐNG");
            message["backgroundColor"] = NormalizeColor(
                ReadString(message, "backgroundColor", string.Empty));
            message["foregroundColor"] = NormalizeColor(
                ReadString(message, "foregroundColor", string.Empty));
            if (message.ContainsKey("autoHideSeconds"))
            {
                message["autoHideSeconds"] = Math.Max(
                    0,
                    ReadInt(message, "autoHideSeconds", 0));
            }
        }
    }

    private static string ReadString(
        JsonObject value,
        string propertyName,
        string defaultValue)
    {
        JsonNode? node = value[propertyName];
        if (node == null)
            return defaultValue;
        if (node.GetValueKind() != JsonValueKind.String)
            throw new InvalidDataException(
                $"Announcement property '{propertyName}' must be a string.");
        return node.GetValue<string>();
    }

    private static int ReadInt(
        JsonObject value,
        string propertyName,
        int defaultValue)
    {
        JsonNode? node = value[propertyName];
        if (node == null)
            return defaultValue;
        if (node is not JsonValue jsonValue ||
            !jsonValue.TryGetValue<int>(out int result))
            throw new InvalidDataException(
                $"Announcement property '{propertyName}' must be an integer.");
        return result;
    }

    private static bool ReadBoolean(
        JsonObject value,
        string propertyName,
        bool defaultValue)
    {
        JsonNode? node = value[propertyName];
        if (node == null)
            return defaultValue;
        if (node is not JsonValue jsonValue ||
            !jsonValue.TryGetValue<bool>(out bool result))
            throw new InvalidDataException(
                $"Announcement property '{propertyName}' must be a boolean.");
        return result;
    }

    private static string NormalizeMode(string value)
    {
        return string.Equals(
            value?.Trim(),
            "playlist",
            StringComparison.OrdinalIgnoreCase)
                ? "playlist"
                : "single";
    }

    private static string NormalizeLevel(string value)
    {
        string normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        return normalized is "warning" or "error" or "success"
            ? normalized
            : "info";
    }

    private static string NormalizeText(
        string value,
        string defaultValue = "")
    {
        string normalized = value?.Trim() ?? string.Empty;
        return normalized.Length == 0 ? defaultValue : normalized;
    }

    private static string NormalizeColor(string value)
    {
        string normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length != 7 || normalized[0] != '#')
            return string.Empty;

        for (int index = 1; index < normalized.Length; index++)
        {
            if (!Uri.IsHexDigit(normalized[index]))
                return string.Empty;
        }

        return normalized.ToUpperInvariant();
    }

    private static bool ContainsInvalidEncoding(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    if (ContainsInvalidEncoding(property.Value))
                        return true;
                }
                return false;
            case JsonValueKind.Array:
                foreach (JsonElement item in element.EnumerateArray())
                {
                    if (ContainsInvalidEncoding(item))
                        return true;
                }
                return false;
            case JsonValueKind.String:
                return ContainsInvalidEncoding(element.GetString());
            default:
                return false;
        }
    }

    private static bool ContainsInvalidEncoding(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return false;

        string[] markers =
        {
            "\uFFFD", "THÃ", "Sáº", "áº", "á»", "Ä‘", "Ä\u0090",
            "Æ°", "Æ¡", "Ã´", "Ã¡", "Ã¢", "Ãª", "Ã©", "Ã¨", "ðŸ"
        };
        foreach (char character in value)
        {
            if (character >= '\u0080' && character <= '\u009F')
                return true;
        }

        return markers.Any(
            marker => value.Contains(marker, StringComparison.Ordinal));
    }

    private async Task<string> ReadAndValidateWithRetryAsync(
        CancellationToken token)
    {
        const int maximumAttempts = 6;
        Exception? lastError = null;

        for (int attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            try
            {
                await using var stream = new FileStream(
                    _announcementPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    bufferSize: 8192,
                    options: FileOptions.Asynchronous |
                             FileOptions.SequentialScan);

                if (stream.Length > MaximumAnnouncementBytes)
                    throw new InvalidDataException(
                        "Announcement JSON is too large.");

                using var reader = new StreamReader(
                    stream,
                    StrictUtf8,
                    detectEncodingFromByteOrderMarks: true,
                    bufferSize: 8192,
                    leaveOpen: false);
                string json = await reader.ReadToEndAsync(token);
                return ValidateAndNormalize(json);
            }
            catch (Exception ex) when (
                ex is IOException or
                UnauthorizedAccessException or
                JsonException or
                InvalidDataException or
                DecoderFallbackException)
            {
                lastError = ex;
                if (attempt == maximumAttempts)
                    break;

                await Task.Delay(
                    TimeSpan.FromMilliseconds(100 * attempt),
                    token);
            }
        }

        throw new InvalidDataException(
            $"Could not load a valid announcement file after {maximumAttempts} attempts.",
            lastError);
    }

    private async Task WriteAtomicallyAsync(
        string json,
        CancellationToken token)
    {
        string tempPath = Path.Combine(
            Path.GetDirectoryName(_announcementPath)!,
            $".{Path.GetFileName(_announcementPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await File.WriteAllTextAsync(
                tempPath,
                json,
                new UTF8Encoding(false),
                token);
            File.Move(tempPath, _announcementPath, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(tempPath);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private async Task<int> BroadcastAsync(string json)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task<bool>[] sends = _clients
            .ToArray()
            .Select(client => SendToClientAsync(client, json, timeout.Token))
            .ToArray();
        bool[] results = await Task.WhenAll(sends);
        int sentClientCount = results.Count(sent => sent);
        _logger.LogInformation(
            "BroadcastAsync completed. SentClients={SentClients}, AttemptedClients={AttemptedClients}, ConnectedClientCount={ConnectedClientCount}",
            sentClientCount,
            sends.Length,
            ConnectedClientCount);
        return sentClientCount;
    }

    private async Task<bool> SendToClientAsync(
        KeyValuePair<Guid, AnnouncementClient> client,
        string json,
        CancellationToken token)
    {
        try
        {
            await client.Value.SendAsync(json, token);
            return true;
        }
        catch (Exception ex) when (
            ex is WebSocketException or
            OperationCanceledException or
            ObjectDisposedException or
            InvalidOperationException)
        {
            _clients.TryRemove(client.Key, out _);
            try
            {
                client.Value.Socket.Abort();
            }
            catch
            {
            }
            return false;
        }
    }

    private async Task CloseClientAsync(
        KeyValuePair<Guid, AnnouncementClient> client,
        CancellationToken token)
    {
        try
        {
            if (client.Value.Socket.State == WebSocketState.Open ||
                client.Value.Socket.State == WebSocketState.CloseReceived)
            {
                await client.Value.Socket.CloseAsync(
                    WebSocketCloseStatus.EndpointUnavailable,
                    "Server shutting down",
                    token);
            }
        }
        catch (Exception ex) when (
            ex is WebSocketException or
            OperationCanceledException or
            ObjectDisposedException or
            InvalidOperationException)
        {
            try
            {
                client.Value.Socket.Abort();
            }
            catch
            {
            }
        }
        finally
        {
            _clients.TryRemove(client.Key, out _);
        }
    }

    private sealed class AnnouncementClient
    {
        private readonly SemaphoreSlim _sendLock = new(1, 1);

        public AnnouncementClient(WebSocket socket)
        {
            Socket = socket;
        }

        public WebSocket Socket { get; }

        public async Task SendAsync(string json, CancellationToken token)
        {
            await _sendLock.WaitAsync(token);
            try
            {
                if (Socket.State != WebSocketState.Open)
                    throw new WebSocketException(
                        WebSocketError.InvalidState,
                        "The WebSocket is not open.");

                byte[] payload = Encoding.UTF8.GetBytes(json);
                await Socket.SendAsync(
                    new ArraySegment<byte>(payload),
                    WebSocketMessageType.Text,
                    endOfMessage: true,
                    cancellationToken: token);
            }
            finally
            {
                _sendLock.Release();
            }
        }

    }
}

internal sealed class AnnouncementGitHubPollingService : BackgroundService
{
    private const string DefaultGitHubRawUrl =
        "https://raw.githubusercontent.com/hieuvipro94x/sx3-scanner-release/main/announcement.json";
    private readonly AnnouncementBroker _broker;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AnnouncementGitHubPollingService> _logger;
    private string? _lastFingerprint;

    public AnnouncementGitHubPollingService(
        AnnouncementBroker broker,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<AnnouncementGitHubPollingService> logger)
    {
        _broker = broker;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        int configuredSeconds = _configuration.GetValue<int?>(
            "AnnouncementServer:GitHubPollSeconds") ?? 15;
        TimeSpan pollInterval = TimeSpan.FromSeconds(
            Math.Max(5, Math.Min(configuredSeconds, 300)));

        await PollOnceAsync(stoppingToken);
        using var timer = new PeriodicTimer(pollInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await PollOnceAsync(stoppingToken);
    }

    private async Task PollOnceAsync(CancellationToken token)
    {
        string sourceUrl =
            _configuration["AnnouncementServer:GitHubRawUrl"] ??
            DefaultGitHubRawUrl;
        string requestUrl = sourceUrl +
            (sourceUrl.Contains('?') ? "&" : "?") +
            "t=" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        string? oldFingerprint = _lastFingerprint;
        _logger.LogInformation(
            "[Announcement] Poll GitHub. SourceUrl={SourceUrl}, OldFingerprint={OldFingerprint}, ConnectedClientCount={ConnectedClientCount}",
            sourceUrl,
            FormatFingerprint(oldFingerprint),
            _broker.ConnectedClientCount);

        try
        {
            HttpClient client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(10);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "SX3AnnouncementServer");
            client.DefaultRequestHeaders.CacheControl =
                new System.Net.Http.Headers.CacheControlHeaderValue
                {
                    NoCache = true,
                    NoStore = true
                };

            using HttpResponseMessage response = await client.GetAsync(
                requestUrl,
                HttpCompletionOption.ResponseContentRead,
                token);
            _logger.LogInformation(
                "[Announcement] Poll response. SourceUrl={SourceUrl}, HttpStatus={HttpStatus}, ConnectedClientCount={ConnectedClientCount}",
                sourceUrl,
                (int)response.StatusCode,
                _broker.ConnectedClientCount);
            response.EnsureSuccessStatusCode();

            string json = await response.Content.ReadAsStringAsync(token);
            string fingerprint = BuildFingerprint(json);
            (string Version, string UpdatedAt) metadata =
                ReadAnnouncementMetadata(json);
            _logger.LogInformation(
                "[Announcement] Poll result. SourceUrl={SourceUrl}, HttpStatus={HttpStatus}, OldFingerprint={OldFingerprint}, NewFingerprint={NewFingerprint}, Version={Version}, UpdatedAt={UpdatedAt}, ConnectedClientCount={ConnectedClientCount}",
                sourceUrl,
                (int)response.StatusCode,
                FormatFingerprint(oldFingerprint),
                FormatFingerprint(fingerprint),
                metadata.Version,
                metadata.UpdatedAt,
                _broker.ConnectedClientCount);
            if (string.Equals(
                fingerprint,
                _lastFingerprint,
                StringComparison.Ordinal))
            {
                _logger.LogInformation("[Announcement] No changes.");
                return;
            }

            string normalized;
            try
            {
                normalized = AnnouncementBroker.ValidateAndNormalize(json);
            }
            catch (Exception ex) when (
                ex is JsonException or InvalidDataException)
            {
                _logger.LogError(
                    ex,
                    "[Announcement] Invalid JSON, ignored.");
                return;
            }

            _logger.LogInformation(
                "[Announcement] Change detected. SourceUrl={SourceUrl}, OldFingerprint={OldFingerprint}, NewFingerprint={NewFingerprint}, Version={Version}, UpdatedAt={UpdatedAt}, ConnectedClientCount={ConnectedClientCount}. Publishing...",
                sourceUrl,
                FormatFingerprint(oldFingerprint),
                FormatFingerprint(fingerprint),
                metadata.Version,
                metadata.UpdatedAt,
                _broker.ConnectedClientCount);
            await _broker.PublishAsync(normalized, token);
            _lastFingerprint = fingerprint;
            _logger.LogInformation(
                "[Announcement] Publish completed. SourceUrl={SourceUrl}, NewFingerprint={NewFingerprint}, Version={Version}, UpdatedAt={UpdatedAt}, ConnectedClientCount={ConnectedClientCount}",
                sourceUrl,
                FormatFingerprint(fingerprint),
                metadata.Version,
                metadata.UpdatedAt,
                _broker.ConnectedClientCount);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "[Announcement] GitHub poll failed. SourceUrl={SourceUrl}, HttpStatus={HttpStatus}, OldFingerprint={OldFingerprint}, NewFingerprint={NewFingerprint}, Version={Version}, UpdatedAt={UpdatedAt}, ConnectedClientCount={ConnectedClientCount}. Keeping the last valid announcement.",
                sourceUrl,
                "unavailable",
                FormatFingerprint(oldFingerprint),
                "(unavailable)",
                "(unavailable)",
                "(unavailable)",
                _broker.ConnectedClientCount);
        }
    }

    private static string BuildFingerprint(string json)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(json ?? string.Empty));
        return Convert.ToHexString(hash);
    }

    private static (string Version, string UpdatedAt) ReadAnnouncementMetadata(
        string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            return (
                ReadString(root, "version"),
                ReadString(root, "updatedAt"));
        }
        catch (JsonException)
        {
            return (string.Empty, string.Empty);
        }
    }

    private static string ReadString(JsonElement root, string propertyName)
    {
        return root.ValueKind == JsonValueKind.Object &&
               root.TryGetProperty(propertyName, out JsonElement value) &&
               value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
    }

    private static string FormatFingerprint(string? fingerprint)
    {
        return string.IsNullOrWhiteSpace(fingerprint)
            ? "(none)"
            : fingerprint;
    }
}

internal sealed class AnnouncementFileWatcherService : BackgroundService
{
    private static readonly TimeSpan DebounceDelay =
        TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan SafetyPollInterval =
        TimeSpan.FromSeconds(30);

    private readonly AnnouncementBroker _broker;
    private readonly ILogger<AnnouncementFileWatcherService> _logger;
    private readonly Channel<bool> _reloadSignals =
        Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropWrite
        });

    public AnnouncementFileWatcherService(
        AnnouncementBroker broker,
        ILogger<AnnouncementFileWatcherService> logger)
    {
        _broker = broker;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string directory = Path.GetDirectoryName(_broker.AnnouncementPath)!;
        string fileName = Path.GetFileName(_broker.AnnouncementPath);

        using var watcher = new FileSystemWatcher(directory, fileName)
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName |
                           NotifyFilters.LastWrite |
                           NotifyFilters.CreationTime |
                           NotifyFilters.Size
        };

        FileSystemEventHandler onChanged = (_, _) => SignalReload();
        RenamedEventHandler onRenamed = (_, args) =>
        {
            if (string.Equals(
                    args.Name,
                    fileName,
                    StringComparison.OrdinalIgnoreCase))
            {
                SignalReload();
            }
        };
        ErrorEventHandler onError = (_, args) =>
        {
            _logger.LogWarning(
                args.GetException(),
                "Announcement file watcher reported an error; the safety poll remains active.");
            SignalReload();
        };

        watcher.Changed += onChanged;
        watcher.Created += onChanged;
        watcher.Renamed += onRenamed;
        watcher.Error += onError;
        watcher.EnableRaisingEvents = true;
        SignalReload();

        _logger.LogInformation(
            "Watching {AnnouncementPath} for changes.",
            _broker.AnnouncementPath);

        Task pollTask = RunSafetyPollAsync(stoppingToken);
        try
        {
            while (await _reloadSignals.Reader.WaitToReadAsync(stoppingToken))
            {
                while (_reloadSignals.Reader.TryRead(out _))
                {
                }

                await Task.Delay(DebounceDelay, stoppingToken);
                while (_reloadSignals.Reader.TryRead(out _))
                {
                }

                try
                {
                    await _broker.ReloadFromDiskAsync(stoppingToken);
                }
                catch (OperationCanceledException)
                    when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "Could not reload {AnnouncementPath}; the last valid snapshot remains active.",
                        _broker.AnnouncementPath);
                }
            }
        }
        catch (OperationCanceledException)
            when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            watcher.EnableRaisingEvents = false;
            watcher.Changed -= onChanged;
            watcher.Created -= onChanged;
            watcher.Renamed -= onRenamed;
            watcher.Error -= onError;

            try
            {
                await pollTask;
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
            }
        }
    }

    private void SignalReload()
    {
        _reloadSignals.Writer.TryWrite(true);
    }

    private async Task RunSafetyPollAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(SafetyPollInterval);
        while (await timer.WaitForNextTickAsync(token))
            SignalReload();
    }
}

internal sealed class AnnouncementShutdownService : IHostedService
{
    private static readonly TimeSpan ClientShutdownTimeout =
        TimeSpan.FromSeconds(3);

    private readonly AnnouncementBroker _broker;
    private readonly ILogger<AnnouncementShutdownService> _logger;

    public AnnouncementShutdownService(
        AnnouncementBroker broker,
        ILogger<AnnouncementShutdownService> logger)
    {
        _broker = broker;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Closing announcement clients.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(ClientShutdownTimeout);

        try
        {
            await _broker.CloseAllClientsAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning(
                "Timed out while closing announcement clients.");
        }
    }
}

internal sealed class ParentProcessMonitorService : BackgroundService
{
    private const string DefaultShutdownEventName =
        @"Local\SX3_AnnouncementServer_Shutdown";
    private static readonly TimeSpan PollInterval =
        TimeSpan.FromMilliseconds(500);

    private readonly IConfiguration _configuration;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<ParentProcessMonitorService> _logger;

    public ParentProcessMonitorService(
        IConfiguration configuration,
        IHostApplicationLifetime lifetime,
        ILogger<ParentProcessMonitorService> logger)
    {
        _configuration = configuration;
        _lifetime = lifetime;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            _logger.LogWarning(
                "Parent process monitoring requires Windows and is disabled.");
            return;
        }

        int parentProcessId;
        bool hasParentProcess = int.TryParse(
            _configuration["ParentProcessId"],
            out parentProcessId);
        string shutdownEventName =
            _configuration["ShutdownEventName"] ??
            DefaultShutdownEventName;
        using var shutdownEvent = new EventWaitHandle(
            false,
            EventResetMode.ManualReset,
            shutdownEventName);
        shutdownEvent.Reset();

        try
        {
            await Task.Delay(
                Timeout.InfiniteTimeSpan,
                _lifetime.ApplicationStarted);
        }
        catch (OperationCanceledException)
        {
        }

        if (stoppingToken.IsCancellationRequested)
            return;

        Process? parentProcess = null;
        if (hasParentProcess)
        {
            try
            {
                parentProcess = Process.GetProcessById(parentProcessId);
            }
            catch (ArgumentException)
            {
                _lifetime.StopApplication();
                _logger.LogWarning(
                    "Parent process {ParentProcessId} is no longer running.",
                    parentProcessId);
                return;
            }
        }

        using (parentProcess)
        {
            if (parentProcess != null)
            {
                _logger.LogInformation(
                    "Monitoring parent process {ParentProcessId}.",
                    parentProcessId);
            }
            else
            {
                _logger.LogInformation(
                    "No parent process configured; monitoring shared shutdown event.");
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                if (shutdownEvent.WaitOne(TimeSpan.Zero))
                {
                    _lifetime.StopApplication();
                    _logger.LogInformation(
                        "Shutdown requested by parent process.");
                    return;
                }

                if (parentProcess != null && parentProcess.HasExited)
                {
                    _lifetime.StopApplication();
                    _logger.LogWarning(
                        "Parent process {ParentProcessId} exited; stopping server.",
                        parentProcessId);
                    return;
                }

                await Task.Delay(PollInterval, stoppingToken);
            }
        }
    }
}
