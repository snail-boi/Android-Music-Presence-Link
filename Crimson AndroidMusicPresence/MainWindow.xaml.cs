
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace musicpresense
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>

    /*
        idea's for future features:
        Now Playing Notifications
        Embedded lyrics fallback (USLT/SYLT tags in FLAC/MP3)

    */
    public partial class MainWindow : Window
    {
        private MusicConfig _config;
        private MusicConfig _savedConfig;
        private bool _isInitializing = true;
        private bool _allowClose;
        private readonly ObservableCollection<AppPackageItem> _appPackages = new();
        private readonly ObservableCollection<string> _remoteRoots = new();
        private bool _isLoadingApps;
        private readonly ObservableCollection<string> _audioCodecs = new();
        private bool _isLoadingCodecs;
        private bool _isAutoGathering;

        #region Window Lifecycle & State
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            Config.Load();

            Width = Config.Current.WindowWidth;
            Height = Config.Current.WindowHeight;
            Top = Config.Current.WindowTop;
            Left = Config.Current.WindowLeft;
            WindowState = Config.Current.WindowState;
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            base.OnClosing(e);

            if (WindowState == WindowState.Minimized)
                WindowState = WindowState.Normal;

            Config.Current.WindowState = WindowState;
            Config.Current.WindowWidth = RestoreBounds.Width;
            Config.Current.WindowHeight = RestoreBounds.Height;
            Config.Current.WindowTop = RestoreBounds.Top;
            Config.Current.WindowLeft = RestoreBounds.Left;

            Config.Save();
        }

        private void MainWindow_Closing(object? sender, CancelEventArgs e)
        {
            if (_allowClose) return;

            Debugger.show("[SETTINGS] Closing settings window (hiding to tray/taskbar behavior).");

            if (HasUnsavedChanges())
            {
                var result = MessageBox.Show(
                    "there are unsaved changes, do you wish to save them?",
                    "Unsaved changes",
                    MessageBoxButton.YesNoCancel,
                    MessageBoxImage.Warning);

                if (result == MessageBoxResult.Cancel)
                {
                    e.Cancel = true;
                    return;
                }

                if (result == MessageBoxResult.Yes)
                {
                    SaveConfigFromUi(true);
                }
                else if (result == MessageBoxResult.No)
                {
                    RevertUnsavedChanges();
                }
            }

            e.Cancel = true;
            Hide();
        }

        internal void AllowClose() => _allowClose = true;
        #endregion

        #region Initialization & UI Setup
        public MainWindow()
        {
            InitializeComponent();

            _config = App.Config;
            _savedConfig = CloneConfig(_config);
            InitializeCmbBoxes();
            InitializeAudioCodecUI();
            ApplyConfigToUI();

            LstAllowedApps.ItemsSource = _appPackages;
            LstAudioCodecs.ItemsSource = _audioCodecs;
            LstRemoteRoots.ItemsSource = _remoteRoots;

            BtnSave.Click += BtnSave_Click;
            BtnRefreshApps.Click += BtnRefreshApps_Click;
            BtnListCodecs.Click += BtnListCodecs_Click;
            BtnAutoGather.Click += BtnAutoGather_Click;
            BtnPickRemoteRoot.Click += BtnPickRemoteRoot_Click;
            BtnClearCoverCache.Click += BtnClearCoverCache_Click;
            BtnOpenCoverCache.Click += BtnOpenCoverCache_Click;
            BtnOpenLogFolder.Click += BtnOpenLogFolder_Click;
            BtnResetDevice.Click += BtnResetDevice_Click;
            LstAudioCodecs.SelectionChanged += LstAudioCodecs_SelectionChanged;
            ChkDarkMode.Checked += ChkDarkMode_CheckedChanged;
            ChkDarkMode.Unchecked += ChkDarkMode_CheckedChanged;
            ChkDebugMode.Checked += ChkDebugMode_CheckedChanged;
            ChkDebugMode.Unchecked += ChkDebugMode_CheckedChanged;
            BtnToggleTheme.Click += BtnToggleTheme_Click;
            BtnRedoOnboarding.Click += BtnRedoOnboarding_Click;
            BtnToggleMediaPlayerView.Click += BtnToggleMediaPlayerView_Click;
            ChkOpenInTaskbar.Checked += ChkOpenInTaskbar_CheckedChanged;
            ChkOpenInTaskbar.Unchecked += ChkOpenInTaskbar_CheckedChanged;
            BtnUpdate.Click += BtnUpdate_Click;
            Updater.UpdateStatusChanged += Updater_UpdateStatusChanged;
            Closing += MainWindow_Closing;
            Loaded += MainWindow_Loaded;
            UpdateUpdateBanner(Updater.IsUpdateAvailable, Updater.LatestVersion, Updater.LatestPatchNotes);
            UpdateMediaPlayerModeButton((Application.Current as App)?.IsMediaPlayerModeActive() == true);
            _isInitializing = false;
        }

        private void InitializeCmbBoxes()
        {
            CmbQualityPresets.ItemsSource = AudioQualityPresets.All
                .Select(p => p.Name)
                .ToArray();
        }


        #endregion

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            _ = LoadInstalledAppsAsync();
        }

        #region Theme & Appearance
        private void ChkDarkMode_CheckedChanged(object sender, RoutedEventArgs e)
        {
            if (_isInitializing)
                return;

            var useDarkMode = ChkDarkMode.IsChecked == true;
            (Application.Current as App)?.ApplyTheme(useDarkMode);
            UpdateThemeToggleText(useDarkMode);
        }

        private void BtnToggleTheme_Click(object sender, RoutedEventArgs e)
        {
            ChkDarkMode.IsChecked = !(ChkDarkMode.IsChecked == true);
        }

        private void BtnUpdate_Click(object sender, RoutedEventArgs e)
        {
            _ = Updater.CheckForUpdateAsync(App.CurrentVersion, showPrompt: true, allowRemindLater: false);
        }

        private void BtnRedoOnboarding_Click(object sender, RoutedEventArgs e)
        {
            SaveConfigFromUi(false);
            (Application.Current as App)?.ShowOnboarding(true);
        }

        private void BtnToggleMediaPlayerView_Click(object sender, RoutedEventArgs e)
        {
            var app = Application.Current as App;
            if (app == null)
                return;

            if (app.IsMediaPlayerModeActive())
            {
                // Switch back to the original settings view and persist that choice.
                _config.ShowMediaPlayerWindow = false;
                MusicConfigManager.Save(_config);
                _savedConfig.ShowMediaPlayerWindow = false;
                app.GoBackToSettingsWindow();
                return;
            }

            // Switch to the media player view and persist that choice.
            _config.ShowMediaPlayerWindow = true;
            MusicConfigManager.Save(_config);
            _savedConfig.ShowMediaPlayerWindow = true;
            app.ShowMediaPlayerWindowNow();
        }

        internal void UpdateMediaPlayerModeButton(bool isMediaPlayerModeActive)
        {
            if (BtnToggleMediaPlayerView == null)
                return;

            BtnToggleMediaPlayerView.Content = isMediaPlayerModeActive
                ? "Switch to settings view"
                : "Switch to media player view";
        }

        private void ChkOpenInTaskbar_CheckedChanged(object sender, RoutedEventArgs e)
        {
            if (_isInitializing)
                return;

            // Checkbox semantics: checked = hide on startup (OpenInTaskbar = true).
            _config.OpenInTaskbar = ChkOpenInTaskbar.IsChecked == true;
            MusicConfigManager.Save(_config);
            _savedConfig.OpenInTaskbar = _config.OpenInTaskbar;
        }

        private void UpdateThemeToggleText(bool useDarkMode)
        {
            if (BtnToggleTheme == null)
                return;

            BtnToggleTheme.Content = useDarkMode ? "Switch to Light" : "Switch to Dark";
        }

        private void Updater_UpdateStatusChanged(bool isUpdateAvailable, string? latestVersion, string? patchNotes)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => UpdateUpdateBanner(isUpdateAvailable, latestVersion, patchNotes)));
                return;
            }

            UpdateUpdateBanner(isUpdateAvailable, latestVersion, patchNotes);
        }

        private void UpdateUpdateBanner(bool isUpdateAvailable, string? latestVersion, string? patchNotes)
        {
            if (TxtVersionInfo != null)
                TxtVersionInfo.Text = $"v{App.CurrentVersion}";

            if (TxtUpdateStatus != null)
                TxtUpdateStatus.Text = isUpdateAvailable ? "· Update available" : "· Up to date";

            if (BtnUpdate != null)
                BtnUpdate.Visibility = isUpdateAvailable ? Visibility.Visible : Visibility.Collapsed;
        }
        #endregion

        #region Miscellaneous
        private void BtnClearCoverCache_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var manager = new CoverCacheManager(_config.Paths.FfmpegPath, _config.Paths.CoverCachePath, _config.CachClearInMB, _config.CoverArtFileNamePatterns);
                manager.ClearCache();
                MessageBox.Show("Cover cache cleared.", "Cover Cache", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to clear cover cache: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnOpenCoverCache_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var cachePath = _config.Paths?.CoverCachePath ?? string.Empty;
                if (string.IsNullOrWhiteSpace(cachePath))
                {
                    MessageBox.Show("Cover cache path is not configured.", "Cover Cache", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                Directory.CreateDirectory(cachePath);
                Process.Start(new ProcessStartInfo
                {
                    FileName = cachePath,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to open cover cache folder: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnOpenLogFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var logPath = Debugger.LogDirectory;
                Directory.CreateDirectory(logPath);
                Process.Start(new ProcessStartInfo
                {
                    FileName = logPath,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to open log folder: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnResetDevice_Click(object sender, RoutedEventArgs e)
        {
            TxtUsbSerial.Text = string.Empty;
            TxtWifi.Text = string.Empty;
            if (TxtMdnsService != null)
                TxtMdnsService.Text = string.Empty;
            TxtDeviceName.Text = string.Empty;

            _config.SelectedDeviceUSB = string.Empty;
            _config.SelectedDeviceWiFi = string.Empty;
            _config.SelectedDeviceName = string.Empty;
            _config.WifiMdnsServiceName = string.Empty;
            _config.IsWifiEnabled = false;
        }

        private void ChkDebugMode_CheckedChanged(object sender, RoutedEventArgs e)
        {
            Debugger.IsEnabled = ChkDebugMode.IsChecked == true;
        }



        private void Expander_Expanded(object sender, RoutedEventArgs e)
        {
            if (sender is not Expander expander)
                return;

            if (expander.Content is not FrameworkElement content)
                return;

            content.RenderTransformOrigin = new Point(0.5, 0);
            if (content.RenderTransform is not ScaleTransform scaleTransform)
            {
                scaleTransform = new ScaleTransform(1, 0.9);
                content.RenderTransform = scaleTransform;
            }

            content.Opacity = 0;
            scaleTransform.ScaleY = 0.9;

            var duration = TimeSpan.FromMilliseconds(200);
            var easing = new CubicEase { EasingMode = EasingMode.EaseOut };

            var scaleAnimation = new DoubleAnimation(0.9, 1, duration) { EasingFunction = easing };
            var opacityAnimation = new DoubleAnimation(0, 1, duration) { EasingFunction = easing };

            scaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnimation);
            content.BeginAnimation(OpacityProperty, opacityAnimation);
        }


        #endregion
    }
}