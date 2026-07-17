using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace AndroidMusicPresenceLink
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
        private bool _appsManagerOpen;

        private readonly HotkeyRecorder _hotkeyRecorder = new HotkeyRecorder();

        public MainWindow()
        {
            InitializeComponent();

            _vm = new SettingsViewModel(App.Config);
            _vm.Interaction = this;
            _vm.PickRemoteFolder = ShowRemoteFolderPicker;
            _vm.ShowAppsManager = ShowAppsManagerDialog;
            _vm.StartHotkeyRecording = StartRecordingHotkey;
            RootContent.DataContext = _vm;
            SeedSubsonicPasswordBox();

            _vm.PropertyChanged += Vm_PropertyChanged;

            // Debug logging follows the toggle and is applied once at load, as before.
            Debugger.IsEnabled = _vm.DebugMode;
            Debugger.AdvancedEnabled = _vm.AdvancedDebugMode;

            Closing += MainWindow_Closing;
            Loaded += MainWindow_Loaded;

            Updater.UpdateStatusChanged += Updater_UpdateStatusChanged;
            _vm.SetUpdateStatus(Updater.Status, Updater.LatestVersion, Updater.LatestPatchNotes);
            _vm.SetMediaPlayerModeActive((Application.Current as App)?.IsMediaPlayerModeActive() == true);
        }

        private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            // The VM raises ThemePreviewToken whenever the active theme is selected, cycled,
            // or edited; re-apply the in-progress profile live so the user sees it before save.
            if (e.PropertyName == nameof(SettingsViewModel.ThemePreviewToken))
                (Application.Current as App)?.ApplyThemePreview(_vm.BuildActiveThemeProfile());
            else if (e.PropertyName == nameof(SettingsViewModel.DebugMode))
                Debugger.IsEnabled = _vm.DebugMode;
            else if (e.PropertyName == nameof(SettingsViewModel.AdvancedDebugMode))
                Debugger.AdvancedEnabled = _vm.AdvancedDebugMode;
        }

        // ── Internal surface preserved for App.xaml.cs ───────────────────────

        internal void SyncRuntimeConfig(MusicConfig config)
        {
            _vm.SyncRuntimeConfig(config);
            SeedSubsonicPasswordBox();
            UpdateMediaPlayerModeButton((Application.Current as App)?.IsMediaPlayerModeActive() == true);
        }

        // PasswordBox.Password isn't a bindable DependencyProperty, so it's seeded from the VM's
        // decrypted value here. OnSubsonicPasswordEdited treats an unchanged value as a no-op, so
        // seeding never falsely marks the password dirty.
        private void SeedSubsonicPasswordBox()
        {
            if (SubsonicPasswordBox != null)
                SubsonicPasswordBox.Password = _vm.SubsonicPassword ?? string.Empty;
        }

        private void SubsonicPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (sender is PasswordBox pb)
                _vm.OnSubsonicPasswordEdited(pb.Password);
        }

        internal void UpdateMediaPlayerModeButton(bool isMediaPlayerModeActive)
        {
            _vm.SetMediaPlayerModeActive(isMediaPlayerModeActive);
        }

        internal void AllowClose() => _allowClose = true;

        internal bool HasUnsavedChanges() => _vm.HasUnsavedChanges();

        internal void Save(bool showConfirmation) => _vm.Save(showConfirmation);

        internal void RevertUnsavedChanges()
        {
            _vm.RevertUnsavedChanges();
            SeedSubsonicPasswordBox();
        }

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
            InitExpanderVisibility(this);
        }

        // ── ISettingsInteraction (dialogs and message boxes) ─────────────────

        public void ShowInfo(string message, string title)
            => (Application.Current as App)?.ShowToast(message, ToastLevel.Info);

        public void ShowWarning(string message, string title)
            => (Application.Current as App)?.ShowToast(message, ToastLevel.Warning);

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
            if (_appsManagerOpen)
                return;

            var window = new AppsManagerWindow(config, updated => onUpdated(updated));

            var owner = Window.GetWindow(this);
            if (owner != null && owner.IsLoaded)
                window.Owner = owner;

            _appsManagerOpen = true;
            try
            {
                window.ShowDialog();
            }
            finally
            {
                _appsManagerOpen = false;
            }
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

        private void StartRecordingHotkey(Action<int[]?> onRecorded)
        {
            // In media player mode the settings content is re-hosted inside the media
            // player window, so key events never reach this (hidden) window. Attach the
            // capture to whichever window currently contains the settings content.
            var host = Window.GetWindow(RootContent) ?? this;
            _hotkeyRecorder.Start(host, onRecorded);
        }

        // ── Expander visibility init (no template trigger) ───────────────────

        private static void InitExpanderVisibility(System.Windows.DependencyObject root)
        {
            foreach (var expander in FindVisualChildren<Expander>(root))
            {
                var contentSite = expander.Template?.FindName("ContentSite", expander) as FrameworkElement;
                if (contentSite != null)
                    contentSite.Visibility = expander.IsExpanded ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private static System.Collections.Generic.IEnumerable<T> FindVisualChildren<T>(System.Windows.DependencyObject parent)
            where T : System.Windows.DependencyObject
        {
            int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is T t) yield return t;
                foreach (var desc in FindVisualChildren<T>(child)) yield return desc;
            }
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

            // Reload the custom-covers list on open so covers forced from the media
            // player while settings were already open still show up.
            if (ReferenceEquals(expander, LibraryCachingExpander))
                _vm.RefreshForcedCovers();

            if (expander.Content is not FrameworkElement content)
                return;

            // We own ContentSite visibility (trigger removed from template).
            var contentSite = expander.Template?.FindName("ContentSite", expander) as FrameworkElement;
            if (contentSite != null)
                contentSite.Visibility = Visibility.Visible;

            // Cancel any in-progress collapse animation so we start clean.
            content.BeginAnimation(OpacityProperty, null);
            if (content.RenderTransform is ScaleTransform st)
                st.BeginAnimation(ScaleTransform.ScaleYProperty, null);

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

        private void Expander_Collapsed(object sender, RoutedEventArgs e)
        {
            if (sender is not Expander expander)
                return;

            if (expander.Content is not FrameworkElement content)
                return;

            // We own ContentSite visibility (trigger removed from template), so it
            // is still Visible here. No need to force it.
            var contentSite = expander.Template?.FindName("ContentSite", expander) as FrameworkElement;

            content.RenderTransformOrigin = new Point(0.5, 0);
            if (content.RenderTransform is not ScaleTransform scaleTransform)
            {
                scaleTransform = new ScaleTransform(1, 1);
                content.RenderTransform = scaleTransform;
            }

            var duration = TimeSpan.FromMilliseconds(160);
            var easing = new CubicEase { EasingMode = EasingMode.EaseIn };

            var scaleAnimation = new DoubleAnimation(1, 0.9, duration) { EasingFunction = easing };
            var opacityAnimation = new DoubleAnimation(1, 0, duration) { EasingFunction = easing };

            opacityAnimation.Completed += (_, _) =>
            {
                // If the user re-expanded before we finished, Expander_Expanded already
                // cancelled our animations and took over. Do nothing.
                if (expander.IsExpanded)
                    return;

                if (contentSite != null)
                    contentSite.Visibility = Visibility.Collapsed;

                // Release holds and restore base values so the next expand starts clean.
                content.BeginAnimation(OpacityProperty, null);
                scaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                content.Opacity = 1;
                scaleTransform.ScaleY = 1;
            };

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