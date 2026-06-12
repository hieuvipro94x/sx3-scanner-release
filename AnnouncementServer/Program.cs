using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
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
        "http://0.0.0.0:5088");
    builder.Services.AddSingleton<AnnouncementBroker>();
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
        await _publishLock.WaitAsync(token);
        try
        {
            await WriteAtomicallyAsync(json, token);
            Volatile.Write(ref _currentJson, json);
            await BroadcastAsync(json);
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
            await BroadcastAsync(json);
            _logger.LogInformation(
                "Reloaded and broadcast announcement file {AnnouncementPath}.",
                _announcementPath);
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

        using JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException(
                "Announcement JSON root must be an object.");
        if (ContainsInvalidEncoding(document.RootElement))
            throw new InvalidDataException(
                "Announcement JSON contains invalid cached encoding.");

        return JsonSerializer.Serialize(
            document.RootElement,
            new JsonSerializerOptions
            {
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                WriteIndented = true
            });
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

    private async Task BroadcastAsync(string json)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task[] sends = _clients
            .ToArray()
            .Select(client => SendToClientAsync(client, json, timeout.Token))
            .ToArray();
        await Task.WhenAll(sends);
    }

    private async Task SendToClientAsync(
        KeyValuePair<Guid, AnnouncementClient> client,
        string json,
        CancellationToken token)
    {
        try
        {
            await client.Value.SendAsync(json, token);
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
