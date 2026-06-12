using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace SX3_SCANER.Helper
{
    internal sealed class AnnouncementServerRunner : IDisposable
    {
        private const string ExecutableName = "AnnouncementServer.exe";
        private const string ReadyUrl = "http://127.0.0.1:5055/health";
        private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(250);

        private readonly object _syncRoot = new object();
        private readonly HttpClient _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(1)
        };

        private Process _ownedProcess;
        private EventWaitHandle _shutdownEvent;
        private string _shutdownEventName;
        private bool _disposed;

        public async Task<bool> StartAsync(CancellationToken token = default)
        {
            try
            {
                ThrowIfDisposed();

                if (await IsServerReadyAsync(token).ConfigureAwait(false))
                {
                    StartupManager.Log(
                        "[Announcement] Local server is already ready. " +
                        "The scanner will not take ownership of that process.");
                    return true;
                }

                string executablePath = ResolveExecutablePath();
                if (!File.Exists(executablePath))
                {
                    StartupManager.Log(
                        "[Announcement] Server executable was not found. Path=" +
                        executablePath);
                    return false;
                }

                Process process;
                lock (_syncRoot)
                {
                    ThrowIfDisposed();

                    if (_ownedProcess != null && !_ownedProcess.HasExited)
                        process = _ownedProcess;
                    else
                        process = StartOwnedProcess(executablePath);
                }

                bool isReady = await WaitUntilReadyAsync(token)
                    .ConfigureAwait(false);
                if (!isReady)
                {
                    StartupManager.Log(
                        "[Announcement] Server did not become ready within " +
                        StartupTimeout.TotalSeconds + " seconds. PID=" +
                        process.Id);
                    Stop();
                    return false;
                }

                StartupManager.Log(
                    "[Announcement] Server is ready. PID=" + process.Id);
                return true;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                StartupManager.Log("[Announcement] Server startup was cancelled.");
                Stop();
                return false;
            }
            catch (Exception ex)
            {
                StartupManager.Log(
                    "[Announcement] Server startup failed; scanner will continue. " +
                    ex);
                Stop();
                return false;
            }
        }

        public void Stop()
        {
            Process process;
            EventWaitHandle shutdownEvent;

            lock (_syncRoot)
            {
                process = _ownedProcess;
                shutdownEvent = _shutdownEvent;
                _ownedProcess = null;
                _shutdownEvent = null;
                _shutdownEventName = null;
            }

            if (process == null)
            {
                shutdownEvent?.Dispose();
                return;
            }

            try
            {
                if (!process.HasExited)
                {
                    StartupManager.Log(
                        "[Announcement] Stopping owned server. PID=" + process.Id);
                    shutdownEvent?.Set();

                    if (!process.WaitForExit(
                            (int)ShutdownTimeout.TotalMilliseconds))
                    {
                        StartupManager.Log(
                            "[Announcement] Owned server shutdown timed out; " +
                            "terminating PID=" + process.Id);
                        process.Kill();
                        process.WaitForExit(
                            (int)ShutdownTimeout.TotalMilliseconds);
                    }
                }
            }
            catch (InvalidOperationException)
            {
                StartupManager.Log(
                    "[Announcement] Owned server has already stopped.");
            }
            catch (Exception ex)
            {
                StartupManager.Log(
                    "[Announcement] Failed to stop owned server cleanly. " + ex);
            }
            finally
            {
                process.Dispose();
                shutdownEvent?.Dispose();
            }
        }

        public void Dispose()
        {
            lock (_syncRoot)
            {
                if (_disposed)
                    return;

                _disposed = true;
            }

            Stop();
            _httpClient.Dispose();
            GC.SuppressFinalize(this);
        }

        private Process StartOwnedProcess(string executablePath)
        {
            string workingDirectory = Path.GetDirectoryName(executablePath);
            int parentProcessId;
            using (Process currentProcess = Process.GetCurrentProcess())
                parentProcessId = currentProcess.Id;

            _shutdownEventName =
                @"Local\SX3_AnnouncementServer_Shutdown_" +
                parentProcessId + "_" + Guid.NewGuid().ToString("N");
            _shutdownEvent = new EventWaitHandle(
                false,
                EventResetMode.ManualReset,
                _shutdownEventName);

            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                Arguments =
                    "--ParentProcessId " + parentProcessId +
                    " --ShutdownEventName \"" + _shutdownEventName + "\"",
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            StartupManager.Log(
                "[Announcement] Starting owned server: " + executablePath);
            _ownedProcess = Process.Start(startInfo);
            if (_ownedProcess == null)
                throw new InvalidOperationException("Process.Start returned null.");

            return _ownedProcess;
        }

        private async Task<bool> WaitUntilReadyAsync(CancellationToken token)
        {
            DateTime deadlineUtc = DateTime.UtcNow + StartupTimeout;
            while (DateTime.UtcNow < deadlineUtc)
            {
                token.ThrowIfCancellationRequested();

                if (await IsServerReadyAsync(token).ConfigureAwait(false))
                    return true;

                await Task.Delay(RetryDelay, token).ConfigureAwait(false);
            }

            return await IsServerReadyAsync(token).ConfigureAwait(false);
        }

        private async Task<bool> IsServerReadyAsync(CancellationToken token)
        {
            try
            {
                using (var request =
                    new HttpRequestMessage(HttpMethod.Get, ReadyUrl))
                using (HttpResponseMessage response =
                    await _httpClient.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        token).ConfigureAwait(false))
                {
                    return response.IsSuccessStatusCode;
                }
            }
            catch (HttpRequestException)
            {
                return false;
            }
            catch (TaskCanceledException) when (!token.IsCancellationRequested)
            {
                return false;
            }
        }

        private static string ResolveExecutablePath()
        {
            return Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "AnnouncementServer",
                ExecutableName);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(AnnouncementServerRunner));
        }
    }
}
