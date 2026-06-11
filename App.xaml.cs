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

                await AnnouncementServerBootstrapper.StartAnnouncementServer();

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
                StartupManager.Log("Application startup failed: " + ex);
                StartupManager.SetStatus("Không thể khởi động ứng dụng.");
                MessageBox.Show(
                    "Không thể khởi động SX3 SCANER. Vui lòng kiểm tra database và thử lại.",
                    "SX3 SCANER",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                startupWindow?.Close();
                Shutdown();
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
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
