using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace SX3_SCANER.Helper
{
    internal static class StartupManager
    {
        private static readonly object StatusSync = new object();
        private static readonly HashSet<string> LoggedKeys =
            new HashSet<string>(StringComparer.Ordinal);
        private static readonly object LogSync = new object();
        private static readonly string LocalDataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SX3_SCANER");
        private static readonly string StartupErrorLogPath = Path.Combine(
            LocalDataDirectory,
            "logs",
            "startup-error.log");
        private static readonly string StartupLogPath = Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.CommonApplicationData),
            "JBZVN",
            "SX3 Scanner",
            "logs",
            "startup.log");
        private static string _currentStatus = "Đang khởi động ứng dụng...";

        internal static event Action<string> StatusChanged;

        internal static string ErrorLogPath
        {
            get { return StartupErrorLogPath; }
        }

        internal static string CurrentStatus
        {
            get
            {
                lock (StatusSync)
                {
                    return _currentStatus;
                }
            }
        }

        internal static void SetStatus(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            Action<string> handler;
            lock (StatusSync)
            {
                _currentStatus = message.Trim();
                handler = StatusChanged;
            }

            Debug.WriteLine("[StartupStatus] " + _currentStatus);
            handler?.Invoke(_currentStatus);
        }

        internal static bool HasArgument(string[] args, string argument)
        {
            if (args == null)
            {
                return false;
            }

            foreach (string value in args)
            {
                if (string.Equals(value, argument, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        internal static void FocusExistingInstance()
        {
            try
            {
                Process current = Process.GetCurrentProcess();

                foreach (Process process in Process.GetProcessesByName(current.ProcessName))
                {
                    if (process.Id == current.Id)
                    {
                        continue;
                    }

                    IntPtr windowHandle = WaitForMainWindowHandle(process);
                    if (windowHandle == IntPtr.Zero)
                    {
                        continue;
                    }

                    if (IsIconic(windowHandle))
                    {
                        ShowWindowAsync(windowHandle, 9);
                    }

                    SetForegroundWindow(windowHandle);
                    Log("Đã chuyển focus đến ứng dụng đang chạy.");
                    return;
                }

                Log("Đã tồn tại tiến trình khác nhưng không tìm thấy cửa sổ chính.");
            }
            catch (Exception ex)
            {
                Log("Không thể chuyển focus đến ứng dụng đang chạy: " + ex);
            }
        }

        internal static void Log(string message)
        {
            Debug.WriteLine("[StartupManager] " + message);

            try
            {
                lock (LogSync)
                {
                    string directory = Path.GetDirectoryName(StartupLogPath);
                    if (!string.IsNullOrEmpty(directory))
                        Directory.CreateDirectory(directory);

                    File.AppendAllText(
                        StartupLogPath,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") +
                        " [StartupManager] " +
                        (message ?? string.Empty) +
                        Environment.NewLine,
                        new UTF8Encoding(false));
                }
            }
            catch
            {
                // Logging must never prevent the application from starting.
            }
        }

        internal static void LogOnce(string key, string message)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                Log(message);
                return;
            }

            lock (StatusSync)
            {
                if (!LoggedKeys.Add(key))
                {
                    return;
                }
            }

            Log(message);
        }

        internal static void LogStartupError(Exception exception, string databasePath)
        {
            Debug.WriteLine("[StartupError] " + exception);

            try
            {
                lock (LogSync)
                {
                    string directory = Path.GetDirectoryName(StartupErrorLogPath);
                    if (!string.IsNullOrEmpty(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }

                    var builder = new StringBuilder();
                    builder.AppendLine("============================================================");
                    builder.AppendLine("Timestamp: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
                    builder.AppendLine("Database path: " + (databasePath ?? "(unknown)"));
                    builder.AppendLine("Exception type: " +
                        (exception == null ? "(none)" : exception.GetType().FullName));
                    builder.AppendLine("Exception message: " +
                        (exception == null ? "(none)" : exception.Message));
                    builder.AppendLine("SQLite diagnosis: " + GetDatabaseDiagnosis(exception));
                    builder.AppendLine("Stack trace:");
                    builder.AppendLine(
                        exception == null || string.IsNullOrWhiteSpace(exception.StackTrace)
                            ? "(not available)"
                            : exception.StackTrace);
                    builder.AppendLine("Database file state:");

                    foreach (string fileName in new[]
                    {
                        "database.db",
                        "database.db-wal",
                        "database.db-shm",
                        "product.db",
                        "product.db-wal",
                        "product.db-shm"
                    })
                    {
                        string path = Path.Combine(LocalDataDirectory, fileName);
                        builder.AppendLine(
                            "  " + path + " | exists=" + File.Exists(path));
                    }

                    File.AppendAllText(
                        StartupErrorLogPath,
                        builder.ToString(),
                        new UTF8Encoding(false));
                }
            }
            catch
            {
                // Error logging must not hide the original startup exception.
            }
        }

        internal static string GetDatabaseDiagnosis(Exception exception)
        {
            string details = exception == null ? string.Empty : exception.ToString();

            if (details.IndexOf("database is locked", StringComparison.OrdinalIgnoreCase) >= 0)
                return "database is locked";
            if (details.IndexOf("file is not a database", StringComparison.OrdinalIgnoreCase) >= 0)
                return "file is not a database";
            if (details.IndexOf("unable to open database file", StringComparison.OrdinalIgnoreCase) >= 0)
                return "unable to open database file";

            return "other startup/database error";
        }

        private static IntPtr WaitForMainWindowHandle(Process process)
        {
            for (int attempt = 0; attempt < 10; attempt++)
            {
                process.Refresh();

                if (process.MainWindowHandle != IntPtr.Zero)
                {
                    return process.MainWindowHandle;
                }

                System.Threading.Thread.Sleep(100);
            }

            return IntPtr.Zero;
        }

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);
    }
}
