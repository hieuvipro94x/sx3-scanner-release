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
        private const string ProcessName = "SX3.AnnouncementServer";
        private const string ExecutableName = "SX3.AnnouncementServer.exe";
        private const string ShutdownEventName =
            @"Local\SX3_AnnouncementServer_Shutdown";
        private const string ReadyUrl = "http://127.0.0.1:5088/health";
        private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(250);

        private readonly object _syncRoot = new object();
        private Process _process;
        private EventWaitHandle _shutdownEvent;
        private bool _disposed;

        public bool Start()
        {
            lock (_syncRoot)
            {
                try
                {
                    ThrowIfDisposed();

                    if (_process != null && !_process.HasExited)
                        return true;

                    Process existingProcess = FindExistingProcess();
                    if (existingProcess != null)
                    {
                        try
                        {
                            StartupManager.Log(
                                "[Announcement] Server is already running. PID=" +
                                existingProcess.Id);
                            WaitUntilReady();
                            return true;
                        }
                        finally
                        {
                            existingProcess.Dispose();
                        }
                    }

                    if (IsServerReady())
                    {
                        StartupManager.Log(
                            "[Announcement] Announcement endpoint is already active.");
                        return true;
                    }

                    string executablePath = ResolveExecutablePath();
                    if (!File.Exists(executablePath))
                    {
                        StartupManager.Log(
                            "Không tìm thấy SX3.AnnouncementServer.exe, bỏ qua khởi động Announcement Server. Path=" +
                            executablePath);
                        return false;
                    }

                    string workingDirectory = Path.GetDirectoryName(executablePath);
                    _shutdownEvent = new EventWaitHandle(
                        false,
                        EventResetMode.ManualReset,
                        ShutdownEventName);
                    _shutdownEvent.Reset();

                    int parentProcessId;
                    using (Process currentProcess = Process.GetCurrentProcess())
                        parentProcessId = currentProcess.Id;

                    var startInfo = new ProcessStartInfo
                    {
                        FileName = executablePath,
                        Arguments =
                            "--ParentProcessId " + parentProcessId +
                            " --ShutdownEventName \"" + ShutdownEventName + "\"",
                        WorkingDirectory = workingDirectory,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden
                    };

                    StartupManager.Log(
                        "[Announcement] Starting server: " + executablePath);
                    _process = Process.Start(startInfo);
                    if (_process == null)
                        throw new InvalidOperationException("Process.Start returned null.");

                    WaitUntilReady();
                    StartupManager.Log(
                        "[Announcement] Server started. PID=" + _process.Id);
                    return true;
                }
                catch (Exception ex)
                {
                    try
                    {
                        StopOwnedProcess();
                    }
                    catch (Exception cleanupException)
                    {
                        StartupManager.Log(
                            "[Announcement] Failed to clean up after startup error: " +
                            cleanupException);
                    }

                    StartupManager.Log(
                        "Announcement Server khởi động thất bại, tính năng thông báo online sẽ bị tắt. Chi tiết: " +
                        ex);
                    return false;
                }
            }
        }

        public void Stop()
        {
            lock (_syncRoot)
            {
                if (_disposed)
                    return;

                StopOwnedProcess();
            }
        }

        public void Dispose()
        {
            lock (_syncRoot)
            {
                if (_disposed)
                    return;

                StopOwnedProcess();
                _disposed = true;
            }

            GC.SuppressFinalize(this);
        }

        private void StopOwnedProcess()
        {
            Process process = _process;
            _process = null;

            if (process == null)
            {
                SignalSharedShutdownEvent();
                StopExistingProcesses();
                DisposeShutdownEvent();
                return;
            }

            TryStopProcess(process);
            DisposeShutdownEvent();
        }

        private void TryStopProcess(Process process)
        {
            try
            {
                if (!process.HasExited)
                {
                    StartupManager.Log(
                        "[Announcement] Stopping server. PID=" + process.Id);
                    process.CloseMainWindow();
                    SignalSharedShutdownEvent();

                    if (!process.WaitForExit((int)ShutdownTimeout.TotalMilliseconds))
                    {
                        StartupManager.Log(
                            "[Announcement] Graceful shutdown timed out; killing process.");
                        process.Kill();
                        process.WaitForExit((int)ShutdownTimeout.TotalMilliseconds);
                    }
                }

                StartupManager.Log("[Announcement] Server stopped.");
            }
            catch (InvalidOperationException)
            {
                StartupManager.Log("[Announcement] Server already stopped.");
            }
            catch (Exception ex)
            {
                StartupManager.Log(
                    "[Announcement] Failed to stop server cleanly: " + ex);
                try
                {
                    if (!process.HasExited)
                        process.Kill();
                }
                catch
                {
                }
            }
            finally
            {
                process.Dispose();
            }
        }

        private void StopExistingProcesses()
        {
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(ProcessName);
            }
            catch (Exception ex)
            {
                StartupManager.Log(
                    "[Announcement] Failed to find server processes: " + ex);
                return;
            }

            foreach (Process existingProcess in processes)
                TryStopProcess(existingProcess);
        }

        private void DisposeShutdownEvent()
        {
            _shutdownEvent?.Dispose();
            _shutdownEvent = null;
        }

        private void SignalSharedShutdownEvent()
        {
            try
            {
                if (_shutdownEvent == null)
                {
                    _shutdownEvent = EventWaitHandle.OpenExisting(
                        ShutdownEventName);
                }

                _shutdownEvent.Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                StartupManager.Log(
                    "[Announcement] Shared shutdown event is not available.");
            }
        }

        private static Process FindExistingProcess()
        {
            Process[] processes = Process.GetProcessesByName(ProcessName);
            Process selected = processes.Length > 0 ? processes[0] : null;

            foreach (Process process in processes)
            {
                if (!ReferenceEquals(process, selected))
                    process.Dispose();
            }

            return selected;
        }

        private static string ResolveExecutablePath()
        {
            return Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "AnnouncementServer",
                ExecutableName);
        }

        private static void WaitUntilReady()
        {
            DateTime deadlineUtc = DateTime.UtcNow + StartupTimeout;
            while (DateTime.UtcNow < deadlineUtc)
            {
                if (IsServerReady())
                    return;

                Thread.Sleep(RetryDelay);
            }

            if (!IsServerReady())
            {
                throw new TimeoutException(
                    "Announcement Server did not become ready within " +
                    StartupTimeout.TotalSeconds + " seconds.");
            }
        }

        private static bool IsServerReady()
        {
            try
            {
                using (var client = new HttpClient
                {
                    Timeout = TimeSpan.FromSeconds(1)
                })
                using (var request = new HttpRequestMessage(HttpMethod.Get, ReadyUrl))
                using (HttpResponseMessage response = client
                    .SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        CancellationToken.None)
                    .GetAwaiter()
                    .GetResult())
                {
                    return response.IsSuccessStatusCode;
                }
            }
            catch (HttpRequestException)
            {
                return false;
            }
            catch (TaskCanceledException)
            {
                return false;
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(AnnouncementServerRunner));
        }
    }
}
