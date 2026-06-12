using SX3_SCANER.Helper;
using SX3_SCANER.Model;
using System;
using System.Collections.Generic;
using System.Windows.Media;
using System.Windows.Threading;

namespace SX3_SCANER.ViewModel
{
    internal partial class MainViewModel
    {
        private readonly OnlineAnnouncementService _onlineAnnouncementService =
            new OnlineAnnouncementService();
        private readonly DispatcherTimer _onlineAnnouncementAutoHideTimer =
            new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
        private readonly DispatcherTimer _onlineAnnouncementRotateTimer =
            new DispatcherTimer();
        private readonly List<AnnouncementMessageInfo> _onlineAnnouncementPlaylist =
            new List<AnnouncementMessageInfo>();

        private AnnouncementInfo _onlineAnnouncementPlaylistSettings;
        private int _onlineAnnouncementPlaylistIndex;
        private bool _onlineAnnouncementWaitingToRepeat;
        private string _onlineAnnouncementText;
        private string _onlineAnnouncementTitle = "THÔNG BÁO HỆ THỐNG";
        private bool _isOnlineAnnouncementVisible;
        private bool _isAnnouncementCountdownVisible;
        private bool _isAnnouncementCloseVisible;
        private int _announcementRemainingSeconds;
        private int _onlineAnnouncementAnimationVersion;
        private bool _isOnlineAnnouncementMarqueeEnabled;
        private string _onlineAnnouncementMarqueeDirection = "rightToLeft";
        private int _onlineAnnouncementMarqueeSpeed = 80;
        private int _onlineAnnouncementMarqueeDelaySeconds = 10;
        private string _announcementCountdownText = string.Empty;
        private string _onlineAnnouncementLevel = "info";
        private string _onlineAnnouncementIcon = "\uD83D\uDCE2";
        private Brush _onlineAnnouncementBackground =
            CreateBrush("#2563EB");
        private Brush _onlineAnnouncementForeground = Brushes.White;
        private Brush _onlineAnnouncementBorderBrush =
            CreateBrush("#93C5FD");

        public string OnlineAnnouncementText
        {
            get { return _onlineAnnouncementText; }
            private set
            {
                if (_onlineAnnouncementText == value) return;
                _onlineAnnouncementText = value;
                OnPropertyChanged();
            }
        }

        public string OnlineAnnouncementTitle
        {
            get { return _onlineAnnouncementTitle; }
            private set
            {
                if (_onlineAnnouncementTitle == value) return;
                _onlineAnnouncementTitle = value;
                OnPropertyChanged();
            }
        }

        public bool IsOnlineAnnouncementVisible
        {
            get { return _isOnlineAnnouncementVisible; }
            private set
            {
                if (_isOnlineAnnouncementVisible == value) return;
                _isOnlineAnnouncementVisible = value;
                OnPropertyChanged();
            }
        }

        public bool IsAnnouncementCountdownVisible
        {
            get { return _isAnnouncementCountdownVisible; }
            private set
            {
                if (_isAnnouncementCountdownVisible == value) return;
                _isAnnouncementCountdownVisible = value;
                OnPropertyChanged();
            }
        }

        public bool IsAnnouncementCloseVisible
        {
            get { return _isAnnouncementCloseVisible; }
            private set
            {
                if (_isAnnouncementCloseVisible == value) return;
                _isAnnouncementCloseVisible = value;
                OnPropertyChanged();
            }
        }

        public int AnnouncementRemainingSeconds
        {
            get { return _announcementRemainingSeconds; }
            private set
            {
                if (_announcementRemainingSeconds == value) return;
                _announcementRemainingSeconds = value;
                OnPropertyChanged();
                AnnouncementCountdownText =
                    value > 0
                        ? "\uD83D\uDD52 Tự ẩn sau: " + value + "s"
                        : string.Empty;
            }
        }

        public string AnnouncementCountdownText
        {
            get { return _announcementCountdownText; }
            private set
            {
                if (_announcementCountdownText == value) return;
                _announcementCountdownText = value;
                OnPropertyChanged();
            }
        }

        public int OnlineAnnouncementAnimationVersion
        {
            get { return _onlineAnnouncementAnimationVersion; }
            private set
            {
                if (_onlineAnnouncementAnimationVersion == value) return;
                _onlineAnnouncementAnimationVersion = value;
                OnPropertyChanged();
            }
        }

        public bool IsOnlineAnnouncementMarqueeEnabled
        {
            get { return _isOnlineAnnouncementMarqueeEnabled; }
            private set
            {
                if (_isOnlineAnnouncementMarqueeEnabled == value) return;
                _isOnlineAnnouncementMarqueeEnabled = value;
                OnPropertyChanged();
            }
        }

        public string OnlineAnnouncementMarqueeDirection
        {
            get { return _onlineAnnouncementMarqueeDirection; }
            private set
            {
                if (_onlineAnnouncementMarqueeDirection == value) return;
                _onlineAnnouncementMarqueeDirection = value;
                OnPropertyChanged();
            }
        }

        public int OnlineAnnouncementMarqueeSpeed
        {
            get { return _onlineAnnouncementMarqueeSpeed; }
            private set
            {
                if (_onlineAnnouncementMarqueeSpeed == value) return;
                _onlineAnnouncementMarqueeSpeed = value;
                OnPropertyChanged();
            }
        }

        public int OnlineAnnouncementMarqueeDelaySeconds
        {
            get { return _onlineAnnouncementMarqueeDelaySeconds; }
            private set
            {
                if (_onlineAnnouncementMarqueeDelaySeconds == value) return;
                _onlineAnnouncementMarqueeDelaySeconds = value;
                OnPropertyChanged();
            }
        }

        public string OnlineAnnouncementLevel
        {
            get { return _onlineAnnouncementLevel; }
            private set
            {
                if (_onlineAnnouncementLevel == value) return;
                _onlineAnnouncementLevel = value;
                OnPropertyChanged();
            }
        }

        public string OnlineAnnouncementIcon
        {
            get { return _onlineAnnouncementIcon; }
            private set
            {
                if (_onlineAnnouncementIcon == value) return;
                _onlineAnnouncementIcon = value;
                OnPropertyChanged();
            }
        }

        public Brush OnlineAnnouncementBackground
        {
            get { return _onlineAnnouncementBackground; }
            private set
            {
                if (Equals(_onlineAnnouncementBackground, value)) return;
                _onlineAnnouncementBackground = value;
                OnPropertyChanged();
            }
        }

        public Brush OnlineAnnouncementForeground
        {
            get { return _onlineAnnouncementForeground; }
            private set
            {
                if (Equals(_onlineAnnouncementForeground, value)) return;
                _onlineAnnouncementForeground = value;
                OnPropertyChanged();
            }
        }

        public Brush OnlineAnnouncementBorderBrush
        {
            get { return _onlineAnnouncementBorderBrush; }
            private set
            {
                if (Equals(_onlineAnnouncementBorderBrush, value)) return;
                _onlineAnnouncementBorderBrush = value;
                OnPropertyChanged();
            }
        }

        private void InitializeOnlineAnnouncement()
        {
            _onlineAnnouncementAutoHideTimer.Tick +=
                OnlineAnnouncementAutoHideTimer_Tick;
            _onlineAnnouncementRotateTimer.Tick +=
                OnlineAnnouncementRotateTimer_Tick;
            _onlineAnnouncementService.AnnouncementChanged += OnlineAnnouncement_Changed;
            _onlineAnnouncementService.Start();
        }

        private void OnlineAnnouncement_Changed(object sender, AnnouncementInfo announcement)
        {
            _onlineAnnouncementAutoHideTimer.Stop();
            _onlineAnnouncementRotateTimer.Stop();
            _onlineAnnouncementPlaylist.Clear();
            _onlineAnnouncementPlaylistSettings = null;
            _onlineAnnouncementPlaylistIndex = 0;
            _onlineAnnouncementWaitingToRepeat = false;

            if (!announcement.Enabled)
            {
                HideOnlineAnnouncement();
                return;
            }

            if (announcement.Mode == "playlist" &&
                announcement.Messages != null &&
                announcement.Messages.Count > 0)
            {
                _onlineAnnouncementPlaylist.AddRange(announcement.Messages);
                _onlineAnnouncementPlaylistSettings = announcement;

                ShowOnlineAnnouncementPlaylistItem(0);
                if (!announcement.MarqueeEnabled &&
                    announcement.Messages.Count > 1)
                {
                    _onlineAnnouncementRotateTimer.Interval =
                        TimeSpan.FromSeconds(announcement.RotateSeconds);
                    _onlineAnnouncementRotateTimer.Start();
                }
                System.Diagnostics.Debug.WriteLine(
                    "[Announcement] Playlist restarted.");
                StartupManager.Log(
                    "[Announcement] Playlist restarted.");

                return;
            }

            ShowOnlineAnnouncement(
                announcement.Level,
                announcement.Title,
                announcement.Message,
                announcement.BackgroundColor,
                announcement.ForegroundColor,
                announcement.AutoHideSeconds,
                announcement.ShowCountdown,
                announcement.AllowClose,
                announcement.MarqueeEnabled,
                announcement.MarqueeDirection,
                announcement.MarqueeSpeed,
                announcement.MarqueeDelaySeconds);
        }

        private void OnlineAnnouncementRotateTimer_Tick(
            object sender,
            EventArgs e)
        {
            if (_onlineAnnouncementPlaylist.Count == 0 ||
                _onlineAnnouncementPlaylistSettings == null)
            {
                _onlineAnnouncementRotateTimer.Stop();
                return;
            }

            if (_onlineAnnouncementWaitingToRepeat)
            {
                _onlineAnnouncementRotateTimer.Stop();
                _onlineAnnouncementWaitingToRepeat = false;
                ShowOnlineAnnouncementPlaylistItem(0);
                if (!_onlineAnnouncementPlaylistSettings.MarqueeEnabled &&
                    _onlineAnnouncementPlaylist.Count > 1)
                {
                    _onlineAnnouncementRotateTimer.Interval =
                        TimeSpan.FromSeconds(
                            _onlineAnnouncementPlaylistSettings.RotateSeconds);
                    _onlineAnnouncementRotateTimer.Start();
                }
                System.Diagnostics.Debug.WriteLine(
                    "[Announcement] Playlist restarted.");
                return;
            }

            int nextIndex = _onlineAnnouncementPlaylistIndex + 1;
            if (nextIndex < _onlineAnnouncementPlaylist.Count)
            {
                ShowOnlineAnnouncementPlaylistItem(nextIndex);
                return;
            }

            ScheduleOnlineAnnouncementPlaylistRepeat();
        }

        public bool CompleteOnlineAnnouncementMarqueeCycle()
        {
            if (_onlineAnnouncementPlaylistSettings == null ||
                _onlineAnnouncementPlaylist.Count <= 1)
            {
                return false;
            }

            int nextIndex = _onlineAnnouncementPlaylistIndex + 1;
            if (nextIndex < _onlineAnnouncementPlaylist.Count)
            {
                ShowOnlineAnnouncementPlaylistItem(nextIndex);
                return true;
            }

            ScheduleOnlineAnnouncementPlaylistRepeat();
            return true;
        }

        private void ScheduleOnlineAnnouncementPlaylistRepeat()
        {
            HideOnlineAnnouncement();
            _onlineAnnouncementWaitingToRepeat = true;

            if (_onlineAnnouncementPlaylistSettings.RepeatSeconds == 0)
            {
                _onlineAnnouncementWaitingToRepeat = false;
                ShowOnlineAnnouncementPlaylistItem(0);
                return;
            }

            _onlineAnnouncementRotateTimer.Interval =
                TimeSpan.FromSeconds(
                    _onlineAnnouncementPlaylistSettings.RepeatSeconds);
            _onlineAnnouncementRotateTimer.Start();
        }

        private void ShowOnlineAnnouncementPlaylistItem(int index)
        {
            if (index < 0 || index >= _onlineAnnouncementPlaylist.Count)
            {
                return;
            }

            _onlineAnnouncementPlaylistIndex = index;
            AnnouncementMessageInfo item = _onlineAnnouncementPlaylist[index];
            AnnouncementInfo settings = _onlineAnnouncementPlaylistSettings;
            int autoHideSeconds =
                item.AutoHideSeconds ?? settings.AutoHideSeconds;

            ShowOnlineAnnouncement(
                item.Level,
                item.Title,
                item.Message,
                string.IsNullOrWhiteSpace(item.BackgroundColor)
                    ? settings.BackgroundColor
                    : item.BackgroundColor,
                string.IsNullOrWhiteSpace(item.ForegroundColor)
                    ? settings.ForegroundColor
                    : item.ForegroundColor,
                autoHideSeconds,
                settings.ShowCountdown,
                settings.AllowClose,
                settings.MarqueeEnabled,
                settings.MarqueeDirection,
                settings.MarqueeSpeed,
                settings.MarqueeDelaySeconds);
            System.Diagnostics.Debug.WriteLine(
                "[Announcement] Message changed.");
        }

        private void ShowOnlineAnnouncement(
            string level,
            string title,
            string message,
            string backgroundColor,
            string foregroundColor,
            int autoHideSeconds,
            bool showCountdown,
            bool allowClose,
            bool marqueeEnabled,
            string marqueeDirection,
            int marqueeSpeed,
            int marqueeDelaySeconds)
        {
            _onlineAnnouncementAutoHideTimer.Stop();

            bool isVisible = !string.IsNullOrWhiteSpace(message);
            bool shouldAutoHide =
                isVisible && autoHideSeconds > 0 && !marqueeEnabled;

            OnlineAnnouncementText = isVisible ? message : string.Empty;
            OnlineAnnouncementTitle = string.IsNullOrWhiteSpace(title)
                ? "THÔNG BÁO HỆ THỐNG"
                : title;
            OnlineAnnouncementLevel = level;
            ApplyOnlineAnnouncementTheme(
                level,
                backgroundColor,
                foregroundColor);
            AnnouncementRemainingSeconds =
                shouldAutoHide ? autoHideSeconds : 0;
            IsAnnouncementCountdownVisible =
                shouldAutoHide && showCountdown;
            IsAnnouncementCloseVisible = isVisible && allowClose;
            IsOnlineAnnouncementVisible = isVisible;
            IsOnlineAnnouncementMarqueeEnabled = marqueeEnabled;
            OnlineAnnouncementMarqueeDirection = marqueeDirection;
            OnlineAnnouncementMarqueeSpeed = marqueeSpeed;
            OnlineAnnouncementMarqueeDelaySeconds = marqueeDelaySeconds;
            OnlineAnnouncementAnimationVersion++;

            if (shouldAutoHide)
            {
                _onlineAnnouncementAutoHideTimer.Start();
            }
        }

        private void OnlineAnnouncementAutoHideTimer_Tick(
            object sender,
            EventArgs e)
        {
            if (AnnouncementRemainingSeconds > 0)
            {
                AnnouncementRemainingSeconds--;
            }

            if (AnnouncementRemainingSeconds > 0)
            {
                return;
            }

            HideOnlineAnnouncement();
        }

        private void ApplyOnlineAnnouncementTheme(
            string level,
            string backgroundColor,
            string foregroundColor)
        {
            switch (level)
            {
                case "warning":
                    OnlineAnnouncementIcon = "\u26A0";
                    OnlineAnnouncementBackground = CreateBrush("#FF8C00");
                    OnlineAnnouncementForeground = CreateBrush("#111111");
                    OnlineAnnouncementBorderBrush = CreateBrush("#FFD08A");
                    break;
                case "error":
                    OnlineAnnouncementIcon = "\u26D4";
                    OnlineAnnouncementBackground = CreateBrush("#D32F2F");
                    OnlineAnnouncementForeground = Brushes.White;
                    OnlineAnnouncementBorderBrush = CreateBrush("#FCA5A5");
                    break;
                case "success":
                    OnlineAnnouncementIcon = "\u2714";
                    OnlineAnnouncementBackground = CreateBrush("#2E7D32");
                    OnlineAnnouncementForeground = Brushes.White;
                    OnlineAnnouncementBorderBrush = CreateBrush("#86EFAC");
                    break;
                default:
                    OnlineAnnouncementIcon = "\uD83D\uDCE2";
                    OnlineAnnouncementBackground = CreateBrush("#1E88E5");
                    OnlineAnnouncementForeground = Brushes.White;
                    OnlineAnnouncementBorderBrush = CreateBrush("#93C5FD");
                    break;
            }

            Brush customBackground = TryCreateBrush(backgroundColor);
            Brush customForeground = TryCreateBrush(foregroundColor);
            if (customBackground != null)
            {
                OnlineAnnouncementBackground = customBackground;
                OnlineAnnouncementBorderBrush = customBackground;
            }

            if (customForeground != null)
            {
                OnlineAnnouncementForeground = customForeground;
            }
        }

        private static Brush CreateBrush(string color)
        {
            var brush = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString(color));
            brush.Freeze();
            return brush;
        }

        private static Brush TryCreateBrush(string color)
        {
            if (string.IsNullOrWhiteSpace(color))
            {
                return null;
            }

            try
            {
                return CreateBrush(color);
            }
            catch (Exception)
            {
                return null;
            }
        }

        public void CloseOnlineAnnouncement()
        {
            _onlineAnnouncementAutoHideTimer.Stop();
            HideOnlineAnnouncement();
        }

        private void HideOnlineAnnouncement()
        {
            _onlineAnnouncementAutoHideTimer.Stop();
            IsAnnouncementCountdownVisible = false;
            IsAnnouncementCloseVisible = false;
            AnnouncementRemainingSeconds = 0;
            IsOnlineAnnouncementVisible = false;
        }

        public void StopOnlineAnnouncement()
        {
            _onlineAnnouncementAutoHideTimer.Stop();
            _onlineAnnouncementRotateTimer.Stop();
            IsAnnouncementCountdownVisible = false;
            IsAnnouncementCloseVisible = false;
            AnnouncementRemainingSeconds = 0;
            _onlineAnnouncementAutoHideTimer.Tick -=
                OnlineAnnouncementAutoHideTimer_Tick;
            _onlineAnnouncementRotateTimer.Tick -=
                OnlineAnnouncementRotateTimer_Tick;
            _onlineAnnouncementService.AnnouncementChanged -= OnlineAnnouncement_Changed;
            _onlineAnnouncementService.Dispose();
        }
    }
}
