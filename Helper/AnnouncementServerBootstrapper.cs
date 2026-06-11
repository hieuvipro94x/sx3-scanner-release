using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace SX3_SCANER.Helper
{
    internal static class AnnouncementServerBootstrapper
    {
        private const string ProcessName = "SX3.AnnouncementServer";
        private const string ExecutableName = "SX3.AnnouncementServer.exe";
        private const string ReadyUrl =
            "http://127.0.0.1:5088/api/announcements";
        private static readonly TimeSpan StartupTimeout =
            TimeSpan.FromSeconds(10);
        private static readonly TimeSpan RetryDelay =
            TimeSpan.FromMilliseconds(250);

        internal static async Task<bool> StartAnnouncementServer()
        {
            try
            {
                if (await IsServerReadyAsync().ConfigureAwait(false))
                {
                    StartupManager.Log("[Announcement] Server ready.");
                    return true;
                }

                if (!IsAnnouncementServerProcessRunning())
                {
                    string executablePath = Path.Combine(
                        AppDomain.CurrentDomain.BaseDirectory,
                        ExecutableName);

                    if (!File.Exists(executablePath))
                    {
                        throw new FileNotFoundException(
                            "Announcement server executable was not found.",
                            executablePath);
                    }

                    StartupManager.Log("[Announcement] Starting server...");
                    var startInfo = new ProcessStartInfo
                    {
                        FileName = executablePath,
                        WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden
                    };

                    Process process = Process.Start(startInfo);
                    if (process == null)
                    {
                        throw new InvalidOperationException(
                            "Process.Start returned null.");
                    }

                    process.Dispose();
                    StartupManager.Log("[Announcement] Server started.");
                }
                else
                {
                    StartupManager.Log(
                        "[Announcement] Server process is already running.");
                }

                StartupManager.Log(
                    "[Announcement] Waiting for server ready...");

                bool ready = await WaitUntilReadyAsync().ConfigureAwait(false);
                if (!ready)
                {
                    throw new TimeoutException(
                        "Announcement Server did not become ready within 10 seconds.");
                }

                StartupManager.Log("[Announcement] Server ready.");
                return true;
            }
            catch (Exception ex)
            {
                StartupManager.Log(
                    "[Announcement] Failed to start server: " + ex);
                return false;
            }
        }

        private static bool IsAnnouncementServerProcessRunning()
        {
            Process[] processes = Process.GetProcessesByName(ProcessName);
            try
            {
                return processes.Length > 0;
            }
            finally
            {
                foreach (Process process in processes)
                {
                    process.Dispose();
                }
            }
        }

        private static async Task<bool> WaitUntilReadyAsync()
        {
            DateTime deadlineUtc = DateTime.UtcNow + StartupTimeout;
            while (DateTime.UtcNow < deadlineUtc)
            {
                if (await IsServerReadyAsync().ConfigureAwait(false))
                    return true;

                await Task.Delay(RetryDelay).ConfigureAwait(false);
            }

            return await IsServerReadyAsync().ConfigureAwait(false);
        }

        private static async Task<bool> IsServerReadyAsync()
        {
            try
            {
                using (var client = new HttpClient
                {
                    Timeout = TimeSpan.FromSeconds(1)
                })
                using (var request = new HttpRequestMessage(
                    HttpMethod.Get,
                    ReadyUrl))
                using (HttpResponseMessage response =
                    await client.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        CancellationToken.None).ConfigureAwait(false))
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
    }
}
