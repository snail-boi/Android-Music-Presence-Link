using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace musicpresense
{
    /// <summary>
    /// Main settings window. All state and logic live in <see cref="SettingsViewModel"/> (its
    /// partials). This code-behind provides the view seams (dialogs, folder picker, apps manager,
    /// hotkey capture), the window lifecycle (placement, bounds save, unsaved-changes close
    /// prompt), the live theme/debug side effects, the update-status subscription, and the purely
    /// visual scroll indicator and expander animation. The parameterless constructor and the
    /// internal surface (SyncRuntimeConfig, UpdateMediaPlayerModeButton, AllowClose,
    /// FormatPackageName) are unchanged, so App.xaml.cs needs no edits.
    /// </summary>
    public partial class MainWindow : Window, ISettingsInteraction
    {
        private readonly SettingsViewModel _vm;
        private bool _allowClose;

        // Hotkey capture state (window keyboard concern).
        private bool _isRecordingHotkey;
        private Action<int>? _onHotkeyRecorded;

        public MainWindow()
        {
            InitializeComponent();

            _vm = new SettingsViewModel(App.Config);
            _vm.Interaction = this;
            _vm.PickRemoteFolder = ShowRemoteFolderPicker;
            _vm.ShowAppsManager = ShowAppsManagerDialog;
            _vm.StartHotkeyRecording = StartRecordingHotkey;
            RootContent.DataContext = _vm;

            _vm.PropertyChanged += Vm_PropertyChanged;

            // Debug logging follows the toggle and is applied once at load, as before.
            Debugger.IsEnabled = _vm.DebugMode;

            Closing += MainWindow_Closing;
            Loaded += MainWindow_Loaded;

            Updater.UpdateStatusChanged += Updater_UpdateStatusChanged;
            _vm.SetUpdateStatus(Updater.Status, Updater.LatestVersion, Updater.LatestPatchNotes);
            _vm.SetMediaPlayerModeActive((Application.Current as App)?.IsMediaPlayerModeActive() == true);
        }

        private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(SettingsViewModel.UseDarkMode))
                (Application.Current as App)?.ApplyTheme(_vm.UseDarkMode);
            else if (e.PropertyName == nameof(SettingsViewModel.DebugMode))
                Debugger.IsEnabled = _vm.DebugMode;
        }

        // ── Internal surface preserved for App.xaml.cs ───────────────────────

        internal void SyncRuntimeConfig(MusicConfig config)
        {
            _vm.SyncRuntimeConfig(config);
            UpdateMediaPlayerModeButton((Application.Current as App)?.IsMediaPlayerModeActive() == true);
        }

        internal void UpdateMediaPlayerModeButton(bool isMediaPlayerModeActive)
        {
            _vm.SetMediaPlayerModeActive(isMediaPlayerModeActive);
        }

        internal void AllowClose() => _allowClose = true;

        // ── Window lifecycle ─────────────────────────────────────────────────

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

        protected override void OnClosing(CancelEventArgs e)
        {
            base.OnClosing(e);

            if (WindowState == WindowState.Minimized)
                WindowState = WindowState.Normal;

            Config.Current.WindowState = WindowState;
            Config.Current.WindowWidth = double.IsFinite(RestoreBounds.Width) ? RestoreBounds.Width : 900;
            Config.Current.WindowHeight = double.IsFinite(RestoreBounds.Height) ? RestoreBounds.Height : 600;
            Config.Current.WindowTop = double.IsFinite(RestoreBounds.Top) ? RestoreBounds.Top : 100;
            Config.Current.WindowLeft = double.IsFinite(RestoreBounds.Left) ? RestoreBounds.Left : 100;

            Config.Save();
        }

        private void MainWindow_Closing(object? sender, CancelEventArgs e)
        {
            if (_allowClose) return;

            Debugger.show("[SETTINGS] Closing settings window (hiding to tray/taskbar behavior).");

            if (_vm.HasUnsavedChanges())
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
                    _vm.Save(true);
                else if (result == MessageBoxResult.No)
                    _vm.RevertUnsavedChanges();
            }

            e.Cancel = true;
            Hide();
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            UpdateScrollIndicator();
        }

        // ── ISettingsInteraction (dialogs and message boxes) ─────────────────

        public void ShowInfo(string message, string title)
            => MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);

        public void ShowWarning(string message, string title)
            => MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Warning);

        public bool ConfirmYesNo(string message, string title)
            => MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

        public WifiPairResult? ShowWifiPair()
        {
            var dlg = new WifiPairDialog();
            if (IsLoaded && IsVisible)
                dlg.Owner = this;

            if (dlg.ShowDialog() != true)
                return null;

            return new WifiPairResult(dlg.ServiceName ?? string.Empty, dlg.PairAddress ?? string.Empty);
        }

        public string? AskDeviceName()
        {
            var dlg = new NameInputDialogue("Enter a name for this device:", "Device Name");
            if (IsLoaded && IsVisible)
                dlg.Owner = this;

            return dlg.ShowDialog() == true ? dlg.InputText : null;
        }

        // ── Folder picker and apps manager seams ─────────────────────────────

        private string? ShowRemoteFolderPicker(string device)
        {
            var picker = RemoteFolderPicker.Create(device, this);
            return picker.ShowDialog() == true ? picker.SelectedFolder : null;
        }

        private void ShowAppsManagerDialog(MusicConfig config, Action<MusicConfig> onUpdated)
        {
            var window = new AppsManagerWindow(config, updated => onUpdated(updated));

            var owner = Window.GetWindow(this);
            if (owner != null && owner.IsLoaded)
                window.Owner = owner;

            window.ShowDialog();
        }

        // ── Update status ────────────────────────────────────────────────────

        private void Updater_UpdateStatusChanged(UpdateStatus status, string? latestVersion, string? patchNotes)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => _vm.SetUpdateStatus(status, latestVersion, patchNotes)));
                return;
            }

            _vm.SetUpdateStatus(status, latestVersion, patchNotes);
        }

        // ── Hotkey key capture (window keyboard concern) ─────────────────────

        private void StartRecordingHotkey(Action<int> onRecorded)
        {
            if (_isRecordingHotkey)
                return;

            _isRecordingHotkey = true;
            _onHotkeyRecorded = onRecorded;
            Debugger.show("[HOTKEY] Started recording hotkey.");

            Title = "Press a key to record hotkey (Esc to cancel)...";
            Focus();
            PreviewKeyDown += Recording_PreviewKeyDown;
            Deactivated += Recording_Deactivated;
        }

        private void StopRecordingHotkey()
        {
            if (!_isRecordingHotkey)
                return;

            Debugger.show("[HOTKEY] Stopped recording hotkey.");
            _isRecordingHotkey = false;
            _onHotkeyRecorded = null;
            Title = "Music Presence Settings";
            PreviewKeyDown -= Recording_PreviewKeyDown;
            Deactivated -= Recording_Deactivated;
        }

        private void Recording_Deactivated(object? sender, EventArgs e)
        {
            StopRecordingHotkey();
        }

        private void Recording_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            try
            {
                if (!_isRecordingHotkey) return;

                e.Handled = true;

                if (e.Key == Key.Escape)
                {
                    Debugger.show("[HOTKEY] Recording cancelled with Escape.");
                    StopRecordingHotkey();
                    return;
                }

                int vk = KeyToVirtualKey(e);
                Debugger.show($"[HOTKEY] Recorded key 0x{vk:X2}.");

                _onHotkeyRecorded?.Invoke(vk);
                StopRecordingHotkey();
            }
            catch
            {
                StopRecordingHotkey();
            }
        }

        private static int KeyToVirtualKey(KeyEventArgs e)
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            int vk = KeyInterop.VirtualKeyFromKey(key);
            return vk & 0xFF;
        }

        // ── Scroll indicator (visual) ─────────────────────────────────────────

        private void MainScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            UpdateScrollIndicator();
        }

        private void UpdateScrollIndicator()
        {
            if (ScrollIndicator == null || MainScrollViewer == null) return;

            double remaining = MainScrollViewer.ScrollableHeight - MainScrollViewer.VerticalOffset;
            bool atBottom = remaining < 8;

            double targetOpacity = atBottom ? 0 : 1;
            if (Math.Abs(ScrollIndicator.Opacity - targetOpacity) < 0.01) return;

            var anim = new DoubleAnimation(targetOpacity, new Duration(TimeSpan.FromMilliseconds(180)));
            ScrollIndicator.BeginAnimation(UIElement.OpacityProperty, anim);
        }

        // ── Expander reveal animation (visual) ────────────────────────────────

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

        // ── Display helper used by AppPackageItem.DisplayName ────────────────

        /// <summary>
        /// Strips TLD prefix and replaces dots/underscores with spaces for display.
        /// e.g. "com.spotify.music" -> "Spotify Music", "jp.nicovideo.android" -> "Nicovideo Android"
        /// </summary>
        internal static string FormatPackageName(string packageName)
        {
            if (string.IsNullOrWhiteSpace(packageName))
                return packageName;

            var parts = packageName.Split('.');
            var tlds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "com", "org", "net", "io", "jp", "de", "fr", "uk", "app", "me", "co" };

            var meaningful = parts.SkipWhile(p => tlds.Contains(p)).ToList();
            if (meaningful.Count == 0)
                meaningful = parts.ToList();

            var result = string.Join(" ", meaningful.Select(p =>
            {
                var s = p.Replace('_', ' ').Replace('-', ' ');
                if (string.IsNullOrWhiteSpace(s)) return s;
                return char.ToUpper(s[0]) + s.Substring(1);
            }));

            return result.Trim();
        }
    }
}
