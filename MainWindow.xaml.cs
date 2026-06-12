using SX3_SCANER.Helper;
using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using SX3_SCANER.ViewModel;

namespace SX3_SCANER
{
    public partial class MainWindow : Window
    {
        private readonly DispatcherTimer timer = new DispatcherTimer();
        private readonly string applicationVersion;

        private readonly UpdateService _updateService = new UpdateService();
        private UpdateInfo availableUpdate;
        private INotifyPropertyChanged _announcementViewModel;
        private CancellationTokenSource _announcementMarqueeCts;
        private int _announcementMarqueeGeneration;

        public MainWindow()
        {
            InitializeComponent();
            DataContextChanged += MainWindow_DataContextChanged;
            StartupManager.StatusChanged += StartupStatus_Changed;
            Closed += MainWindow_Closed;
            AnnouncementMarqueeHost.SizeChanged +=
                AnnouncementMarqueeHost_SizeChanged;
            txtStartupStatus.Text = StartupManager.CurrentStatus;
            AttachAnnouncementViewModel(DataContext as INotifyPropertyChanged);

            applicationVersion = UpdateService.GetCurrentVersionString();

            if (txtAppVersion != null)
            {
                txtAppVersion.Text = "SCANER V" + applicationVersion;
            }

            timer.Interval = TimeSpan.FromSeconds(1);
            timer.Tick += Timer_Tick;
            timer.Start();

            UpdateClock();
            Loaded += MainWindow_Loaded;
        }

        private void Timer_Tick(object sender, EventArgs e)
        {
            UpdateClock();
        }

        private void UpdateClock()
        {
            string now = DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss");

            Title = "Scanner V" + applicationVersion + " | " + now;

            if (txtAppVersion != null)
            {
                txtAppVersion.Text = "SCANER V" + applicationVersion;
            }

            if (txtDateTimeVersion != null)
            {
                txtDateTimeVersion.Text = now;
            }
        }

        private void HideRowIndex_AutoGeneratingColumn(object sender, DataGridAutoGeneratingColumnEventArgs e)
        {
            if (e.PropertyName == "RowIndex" || e.PropertyName == "ID")
            {
                e.Cancel = true;
                return;
            }

            if (e.PropertyName == "ScanTime")
                e.Column.Width = 150;

            if (e.PropertyName == "BoxName")
                e.Column.Width = 150;

            if (e.PropertyName == "ProductPartNumber")
                e.Column.Width = 150;

            if (e.PropertyName == "ProductPartName")
                e.Column.Width = 160;

            if (e.PropertyName == "SealNo")
                e.Column.Width = 100;

            if (e.PropertyName == "LotNo")
                e.Column.Width = 100;

            if (e.PropertyName == "ScanData")
                e.Column.Width = 260;

            if (e.PropertyName == "ScanMessage")
                e.Column.Width = 260;

            if (e.PropertyName == "ScanWorker")
                e.Column.Width = 120;

            if (e.PropertyName == "ResultText")
                e.Column.Width = 100;
        }

        private void DataGrid_LoadingRow(object sender, DataGridRowEventArgs e)
        {
            e.Row.Header = (e.Row.GetIndex() + 1).ToString();
        }

        private void SQLiteTable_LoadingRow(object sender, DataGridRowEventArgs e)
        {
            e.Row.Header = (e.Row.GetIndex() + 1).ToString();
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            RestartAnnouncementMarquee();
            await RefreshUpdateStatusAsync(false);
        }

        private void AnnouncementMarqueeHost_SizeChanged(
            object sender,
            SizeChangedEventArgs e)
        {
            if (!e.WidthChanged || !IsLoaded)
            {
                return;
            }

            RestartAnnouncementMarquee();
        }

        private void StartupStatus_Changed(string message)
        {
            Dispatcher.BeginInvoke(new Action(() => txtStartupStatus.Text = message));
        }

        private void MainWindow_Closed(object sender, EventArgs e)
        {
            DataContextChanged -= MainWindow_DataContextChanged;
            StartupManager.StatusChanged -= StartupStatus_Changed;
            AnnouncementMarqueeHost.SizeChanged -=
                AnnouncementMarqueeHost_SizeChanged;
            timer.Stop();
            StopOnlineAnnouncementAnimation();
            AttachAnnouncementViewModel(null);

            if (DataContext is MainViewModel viewModel)
            {
                viewModel.StopOnlineAnnouncement();
            }
        }

        private void MainWindow_DataContextChanged(
            object sender,
            DependencyPropertyChangedEventArgs e)
        {
            AttachAnnouncementViewModel(e.NewValue as INotifyPropertyChanged);
        }

        private void AttachAnnouncementViewModel(INotifyPropertyChanged viewModel)
        {
            if (ReferenceEquals(_announcementViewModel, viewModel))
            {
                return;
            }

            if (_announcementViewModel != null)
            {
                _announcementViewModel.PropertyChanged -=
                    AnnouncementViewModel_PropertyChanged;
            }

            _announcementViewModel = viewModel;

            if (_announcementViewModel != null)
            {
                _announcementViewModel.PropertyChanged +=
                    AnnouncementViewModel_PropertyChanged;
            }

            RestartAnnouncementMarquee();
        }

        private void OnlineAnnouncementClose_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (DataContext is MainViewModel viewModel)
            {
                viewModel.CloseOnlineAnnouncement();
            }
        }

        private void AnnouncementViewModel_PropertyChanged(
            object sender,
            PropertyChangedEventArgs e)
        {
            string propertyName = e?.PropertyName ?? string.Empty;

            if (string.IsNullOrEmpty(propertyName) ||
                string.Equals(propertyName, nameof(MainViewModel.OnlineAnnouncementText), StringComparison.Ordinal) ||
                string.Equals(propertyName, nameof(MainViewModel.OnlineAnnouncementAnimationVersion), StringComparison.Ordinal) ||
                string.Equals(propertyName, nameof(MainViewModel.IsOnlineAnnouncementVisible), StringComparison.Ordinal) ||
                string.Equals(propertyName, nameof(MainViewModel.IsOnlineAnnouncementMarqueeEnabled), StringComparison.Ordinal) ||
                string.Equals(propertyName, nameof(MainViewModel.OnlineAnnouncementMarqueeDirection), StringComparison.Ordinal) ||
                string.Equals(propertyName, nameof(MainViewModel.OnlineAnnouncementMarqueeSpeed), StringComparison.Ordinal) ||
                string.Equals(propertyName, nameof(MainViewModel.OnlineAnnouncementMarqueeDelaySeconds), StringComparison.Ordinal))
            {
                RestartAnnouncementMarquee();
            }
        }

        private async void RestartAnnouncementMarquee()
        {
            StopOnlineAnnouncementAnimation();

            if (!IsLoaded ||
                !(DataContext is MainViewModel viewModel) ||
                !viewModel.IsOnlineAnnouncementVisible ||
                !viewModel.IsOnlineAnnouncementMarqueeEnabled ||
                string.IsNullOrWhiteSpace(viewModel.OnlineAnnouncementText))
            {
                if (AnnouncementMarqueeTransform != null)
                {
                    AnnouncementMarqueeTransform.X = 0;
                }

                return;
            }

            int generation = Interlocked.Increment(ref _announcementMarqueeGeneration);
            CancellationTokenSource cts = new CancellationTokenSource();
            CancellationTokenSource previousCts =
                Interlocked.Exchange(ref _announcementMarqueeCts, cts);
            previousCts?.Cancel();
            previousCts?.Dispose();

            try
            {
                await Dispatcher.BeginInvoke(
                    new Action(() => { }),
                    DispatcherPriority.Loaded).Task;
            }
            catch (TaskCanceledException)
            {
                return;
            }

            if (cts.IsCancellationRequested ||
                generation != _announcementMarqueeGeneration ||
                !TryGetAnnouncementMarqueeMetrics(out double hostWidth, out double textWidth))
            {
                if (cts.IsCancellationRequested ||
                    generation != _announcementMarqueeGeneration)
                {
                    return;
                }

                try
                {
                    await Task.Delay(100, cts.Token);
                }
                catch (TaskCanceledException)
                {
                    return;
                }

                if (cts.IsCancellationRequested ||
                    generation != _announcementMarqueeGeneration ||
                    !TryGetAnnouncementMarqueeMetrics(out hostWidth, out textWidth))
                {
                    if (AnnouncementMarqueeTransform != null)
                    {
                        AnnouncementMarqueeTransform.X = 0;
                    }

                    return;
                }
            }

            double from = hostWidth;
            double to = -textWidth;
            double seconds =
                (hostWidth + textWidth) /
                Math.Max(1, viewModel.OnlineAnnouncementMarqueeSpeed);
            var animation = new DoubleAnimation
            {
                From = from,
                To = to,
                Duration = TimeSpan.FromSeconds(seconds),
                FillBehavior = FillBehavior.Stop
            };
            Timeline.SetDesiredFrameRate(animation, 60);

            animation.Completed += async (animationSender, animationArgs) =>
            {
                if (cts.IsCancellationRequested ||
                    generation != _announcementMarqueeGeneration ||
                    !IsLoaded)
                {
                    return;
                }

                if (AnnouncementMarqueeTransform != null)
                {
                    AnnouncementMarqueeTransform.BeginAnimation(
                        TranslateTransform.XProperty,
                        null);
                    AnnouncementMarqueeTransform.X = to;
                }

                int delaySeconds =
                    Math.Max(0, viewModel.OnlineAnnouncementMarqueeDelaySeconds);

                try
                {
                    if (delaySeconds > 0)
                    {
                        await Task.Delay(
                            TimeSpan.FromSeconds(delaySeconds),
                            cts.Token);
                    }
                }
                catch (TaskCanceledException)
                {
                    return;
                }

                if (cts.IsCancellationRequested ||
                    generation != _announcementMarqueeGeneration ||
                    !IsLoaded ||
                    !(DataContext is MainViewModel currentViewModel) ||
                    !currentViewModel.IsOnlineAnnouncementVisible ||
                    !currentViewModel.IsOnlineAnnouncementMarqueeEnabled ||
                    string.IsNullOrWhiteSpace(currentViewModel.OnlineAnnouncementText))
                {
                    return;
                }

                if (!currentViewModel.CompleteOnlineAnnouncementMarqueeCycle())
                {
                    RestartAnnouncementMarquee();
                }
            };

            if (AnnouncementMarqueeTransform != null)
            {
                AnnouncementMarqueeTransform.X = from;
                AnnouncementMarqueeTransform.BeginAnimation(
                    TranslateTransform.XProperty,
                    animation);
            }
        }

        private void StopOnlineAnnouncementAnimation()
        {
            Interlocked.Increment(ref _announcementMarqueeGeneration);

            CancellationTokenSource cts =
                Interlocked.Exchange(ref _announcementMarqueeCts, null);
            if (cts != null)
            {
                cts.Cancel();
                cts.Dispose();
            }

            if (AnnouncementMarqueeTransform != null)
            {
                AnnouncementMarqueeTransform.BeginAnimation(
                    TranslateTransform.XProperty,
                    null);
                AnnouncementMarqueeTransform.X = 0;
            }
        }

        private bool TryGetAnnouncementMarqueeMetrics(
            out double hostWidth,
            out double textWidth)
        {
            hostWidth = AnnouncementMarqueeHost?.ActualWidth ?? 0;
            textWidth = AnnouncementMarqueeText?.ActualWidth ?? 0;

            if (textWidth <= 0 && AnnouncementMarqueeText != null)
            {
                AnnouncementMarqueeText.Measure(
                    new Size(double.PositiveInfinity, double.PositiveInfinity));
                textWidth = AnnouncementMarqueeText.DesiredSize.Width;
            }

            return hostWidth > 0 && textWidth > 0;
        }

        private async Task RefreshUpdateStatusAsync(bool showErrorMessage)
        {
            txtUpdateStatus.Text = "Đang kiểm tra bản cập nhật...";
            btnSoftwareUpdate.IsEnabled = false;
            updateNotificationDot.Visibility = Visibility.Collapsed;
            availableUpdate = null;

            UpdateInfo update =
                await _updateService.CheckForUpdateAsync(showErrorMessage);
            availableUpdate = update;

            if (availableUpdate != null)
            {
                txtUpdateStatus.Text = "Có bản mới: V" + availableUpdate.Version;
                updateNotificationDot.Visibility = Visibility.Visible;

                // Có bản mới thì cho bấm cập nhật.
                btnSoftwareUpdate.IsEnabled = true;
                return;
            }

            if (_updateService.LastCheckSucceeded)
            {
                txtUpdateStatus.Text = _updateService.LastStatusMessage;
                updateNotificationDot.Visibility = Visibility.Collapsed;

                // Đã mới nhất thì khóa nút cập nhật.
                btnSoftwareUpdate.IsEnabled = false;
                return;
            }

            txtUpdateStatus.Text = string.Empty;
            updateNotificationDot.Visibility = Visibility.Collapsed;

            // Network/API failures stay in Debug logs; the button remains retryable.
            btnSoftwareUpdate.IsEnabled = true;
        }

        private async void SoftwareUpdate_Click(object sender, RoutedEventArgs e)
        {
            btnSoftwareUpdate.IsEnabled = false;

            // Nếu chưa có thông tin bản cập nhật thì kiểm tra lại GitHub.
            if (availableUpdate == null)
            {
                await RefreshUpdateStatusAsync(true);

                if (availableUpdate == null)
                {
                    // Nếu GitHub lỗi thì vẫn cho bấm thử lại.
                    if (!_updateService.LastCheckSucceeded)
                    {
                        btnSoftwareUpdate.IsEnabled = true;
                    }

                    return;
                }
            }

            txtUpdateStatus.Text = "Đang tải và xác thực bản cập nhật...";
            bool installerStarted = false;

            try
            {
                string installerPath =
                    await _updateService.DownloadAndVerifyAsync(availableUpdate);
                txtUpdateStatus.Text = "Bản cập nhật đã được xác thực.";

                bool accepted = ShowUpdateDetailDialog(availableUpdate);
                if (!accepted)
                {
                    txtUpdateStatus.Text = "Đã hủy cập nhật.";
                    btnSoftwareUpdate.IsEnabled = true;
                    updateNotificationDot.Visibility = Visibility.Visible;
                    return;
                }

                installerStarted =
                    _updateService.TryStartInstallerAndExit(installerPath);

                if (installerStarted)
                {
                    txtUpdateStatus.Text = "Đã khởi động trình cài đặt cập nhật.";
                    updateNotificationDot.Visibility = Visibility.Collapsed;
                    btnSoftwareUpdate.IsEnabled = false;
                    return;
                }

                btnSoftwareUpdate.IsEnabled = true;
            }
            catch (Exception ex)
            {
                _updateService.ReportDownloadError(ex);
                txtUpdateStatus.Text = _updateService.LastStatusMessage;
                btnSoftwareUpdate.IsEnabled = true;
            }
            finally
            {
                if (!installerStarted)
                {
                    if (availableUpdate != null || !_updateService.LastCheckSucceeded)
                    {
                        btnSoftwareUpdate.IsEnabled = true;
                    }
                    else
                    {
                        btnSoftwareUpdate.IsEnabled = false;
                    }
                }
            }
        }

        private bool ShowUpdateDetailDialog(UpdateInfo update)
        {
            var detailWindow = new UpdateReleaseNotesWindow(applicationVersion, update)
            {
                Owner = this
            };

            return detailWindow.ShowDialog() == true;
        }
    }
}
