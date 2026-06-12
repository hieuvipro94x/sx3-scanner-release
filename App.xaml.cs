using SX3_SCANER.Helper;
using SX3_SCANER.Model;
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace SX3_SCANER
{
    public partial class App : Application
    {
        private const string SingleInstanceMutexName = @"Local\SX3_SCANER_SingleInstance";
        private Mutex _singleInstanceMutex;
        private AnnouncementServerRunner _announcementServerRunner;

        private async void Application_Startup(object sender, StartupEventArgs e)
        {
            bool createdNew;
            StartupStatusWindow startupWindow = null;
            _singleInstanceMutex = new Mutex(true, SingleInstanceMutexName, out createdNew);

            if (!createdNew)
            {
                StartupManager.FocusExistingInstance();
                Shutdown();
                return;
            }

            try
            {
                StartupManager.SetStatus("Đang khởi động ứng dụng...");
                startupWindow = new StartupStatusWindow();
                startupWindow.Show();

                await Task.Run(() =>
                {
                    DatabaseInitialize initialize = new DatabaseInitialize();
                    initialize.EnsureCreate();
                });

                _announcementServerRunner = new AnnouncementServerRunner();
                try
                {
                    bool announcementStarted = await Task.Run(
                        () => _announcementServerRunner.Start());
                    if (!announcementStarted)
                    {
                        StartupManager.SetStatus(
                            "Announcement Server không khả dụng; tiếp tục khởi động Scanner.");
                    }
                }
                catch (Exception announcementException)
                {
                    StartupManager.Log(
                        "Announcement Server gặp lỗi ngoài dự kiến; Scanner vẫn tiếp tục khởi động. Chi tiết: " +
                        announcementException);
                }

                StartupManager.SetStatus("Đang tải cấu hình...");
                MainWindow mainWindow = new MainWindow
                {
                    DataContext = new ViewModel.MainViewModel()
                };

                if (StartupManager.HasArgument(e.Args, "--minimized"))
                {
                    mainWindow.WindowState = WindowState.Minimized;
                }

                mainWindow.Show();
                startupWindow.Close();
                StartupManager.SetStatus("Sẵn sàng");
            }
            catch (Exception ex)
            {
                StartupManager.LogStartupError(
                    ex,
                    Model.Respository.DatabaseRepository.DatabasePath +
                    " | " +
                    Model.Respository.DatabaseRepository.ProductDatabasePath);
                StartupManager.Log("Application startup failed: " + ex);
                StartupManager.SetStatus("Không thể khởi động ứng dụng.");
                string diagnosis = StartupManager.GetDatabaseDiagnosis(ex);
                MessageBox.Show(
                    "Không thể khởi động SX3 SCANER." +
                    Environment.NewLine +
                    "Nguyên nhân: " + diagnosis +
                    Environment.NewLine +
                    "Chi tiết: " + ex.Message +
                    Environment.NewLine +
                    "Log: " + StartupManager.ErrorLogPath,
                    "SX3 SCANER",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                startupWindow?.Close();
                Shutdown();
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _announcementServerRunner?.Dispose();
            _announcementServerRunner = null;

            if (_singleInstanceMutex != null)
            {
                try
                {
                    _singleInstanceMutex.ReleaseMutex();
                }
                catch (ApplicationException)
                {
                }

                _singleInstanceMutex.Dispose();
                _singleInstanceMutex = null;
            }

            base.OnExit(e);
        }
    }
}
