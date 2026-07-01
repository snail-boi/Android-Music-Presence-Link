using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace AndroidMusicPresenceLink
{
    // Severity level, used to pick the accent color on the toast.
    internal enum ToastLevel
    {
        Info,
        Warning,
        Error
    }

    /// <summary>
    /// Central notification toast manager.
    ///
    /// Routing rules (evaluated each time Show is called):
    ///   - Media player open AND MediaPlayerToastMode == InMediaPlayer -> inline stack inside media player
    ///   - Media player open AND MediaPlayerToastMode == Headless       -> headless overlay window
    ///   - Media player open AND MediaPlayerToastMode == Off             -> silent
    ///   - Media player closed AND HeadlessToastEnabled == true          -> headless overlay window
    ///   - Media player closed AND HeadlessToastEnabled == false         -> silent
    ///
    /// Stacking: newest toast appears at the top; older ones shift down.
    /// Auto-dismiss: (totalChars / 25) + 2 seconds.
    /// Click to dismiss early.
    /// </summary>
    internal sealed class NotificationToastManager
    {
        // The App supplies these via property assignment after construction.
        public Func<MusicConfig>? GetConfig { get; set; }
        public Func<bool>? IsMediaPlayerOpen { get; set; }
        // Called to insert/remove a toast panel inside the media player window.
        public Action<UIElement>? AddToMediaPlayer { get; set; }
        public Action<UIElement>? RemoveFromMediaPlayer { get; set; }

        private readonly Dispatcher _dispatcher;
        private HeadlessToastHost? _headlessHost;

        // Tracks all live toasts so we can reposition on add/remove.
        private readonly List<ToastEntry> _headlessEntries = new();
        private readonly List<ToastEntry> _inlineEntries = new();

        private const int ToastWidth = 320;
        private const int ToastMargin = 8;
        private const int ScreenEdgeMargin = 20;

        internal NotificationToastManager(Dispatcher dispatcher)
        {
            _dispatcher = dispatcher;
        }

        // ── Public API ──────────────────────────────────────────────────────────

        public void Show(string message, ToastLevel level = ToastLevel.Info)
        {
            if (!_dispatcher.CheckAccess())
            {
                _dispatcher.BeginInvoke(() => Show(message, level));
                return;
            }

            var config = GetConfig?.Invoke() ?? new MusicConfig();
            bool mediaPlayerOpen = IsMediaPlayerOpen?.Invoke() ?? false;

            if (mediaPlayerOpen)
            {
                switch (config.Toast.MediaPlayerMode)
                {
                    case MediaPlayerToastMode.InMediaPlayer:
                        ShowInline(message, level, config);
                        break;
                    case MediaPlayerToastMode.Headless:
                        ShowHeadless(message, level, config);
                        break;
                    case MediaPlayerToastMode.Off:
                        break;
                }
            }
            else
            {
                if (config.Toast.HeadlessEnabled)
                    ShowHeadless(message, level, config);
            }
        }

        public void UpdateConfig(MusicConfig config)
        {
            // Nothing to update at runtime; routing is re-evaluated on each Show call.
        }

        public void Dispose()
        {
            if (!_dispatcher.CheckAccess())
            {
                _dispatcher.BeginInvoke(Dispose);
                return;
            }

            _headlessHost?.Close();
            _headlessHost = null;
        }

        // ── Headless path ───────────────────────────────────────────────────────

        private void ShowHeadless(string message, ToastLevel level, MusicConfig config)
        {
            EnsureHeadlessHost();

            var entry = BuildToastPanel(message, level, onDismiss: e =>
            {
                _headlessEntries.Remove(e);
                _headlessHost?.RemoveToast(e.Panel);
                RefreshHeadlessPositions(config.Toast.HeadlessPosition);
            });

            _headlessEntries.Insert(0, entry);
            _headlessHost!.AddToast(entry.Panel);
            RefreshHeadlessPositions(config.Toast.HeadlessPosition);
            ScheduleDismiss(entry, () =>
            {
                _headlessEntries.Remove(entry);
                _headlessHost?.RemoveToast(entry.Panel);
                RefreshHeadlessPositions(config.Toast.HeadlessPosition);
            });
        }

        private void EnsureHeadlessHost()
        {
            if (_headlessHost != null && _headlessHost.IsVisible)
                return;

            _headlessHost = new HeadlessToastHost();
            _headlessHost.Show();
        }

        private void RefreshHeadlessPositions(HeadlessToastPosition position)
        {
            if (_headlessHost == null) return;

            var area = SystemParameters.WorkArea;
            bool fromBottom = position == HeadlessToastPosition.BottomLeft
                           || position == HeadlessToastPosition.BottomCenter
                           || position == HeadlessToastPosition.BottomRight;

            double x = position switch
            {
                HeadlessToastPosition.TopLeft    or HeadlessToastPosition.BottomLeft    => area.Left + ScreenEdgeMargin,
                HeadlessToastPosition.TopRight   or HeadlessToastPosition.BottomRight   => area.Right - ToastWidth - ScreenEdgeMargin,
                _                                                                        => area.Left + (area.Width - ToastWidth) / 2.0
            };

            // Re-measure each panel so Height is accurate.
            foreach (var e in _headlessEntries)
            {
                e.Panel.Measure(new Size(ToastWidth, double.PositiveInfinity));
            }

            if (!fromBottom)
            {
                double y = area.Top + ScreenEdgeMargin;
                for (int i = 0; i < _headlessEntries.Count; i++)
                {
                    var panel = _headlessEntries[i].Panel;
                    double h = Math.Max(panel.DesiredSize.Height, 44);
                    _headlessHost.PositionToast(panel, x, y);
                    y += h + ToastMargin;
                }
            }
            else
            {
                double y = area.Bottom - ScreenEdgeMargin;
                for (int i = 0; i < _headlessEntries.Count; i++)
                {
                    var panel = _headlessEntries[i].Panel;
                    double h = Math.Max(panel.DesiredSize.Height, 44);
                    y -= h;
                    _headlessHost.PositionToast(panel, x, y);
                    y -= ToastMargin;
                }
            }

            // Hide the host when no toasts remain.
            if (_headlessEntries.Count == 0)
                _headlessHost.Hide();
        }

        // ── Inline (media player) path ──────────────────────────────────────────

        private void ShowInline(string message, ToastLevel level, MusicConfig config)
        {
            if (AddToMediaPlayer == null || RemoveFromMediaPlayer == null) return;

            var entry = BuildToastPanel(message, level, onDismiss: e =>
            {
                _inlineEntries.Remove(e);
                RemoveFromMediaPlayer?.Invoke(e.Panel);
            });

            _inlineEntries.Insert(0, entry);
            AddToMediaPlayer(entry.Panel);
            ScheduleDismiss(entry, () =>
            {
                _inlineEntries.Remove(entry);
                RemoveFromMediaPlayer?.Invoke(entry.Panel);
            });
        }

        // ── Toast construction ──────────────────────────────────────────────────

        private ToastEntry BuildToastPanel(string message, ToastLevel level, Action<ToastEntry> onDismiss)
        {
            var entry = new ToastEntry();

            var accentColor = level switch
            {
                ToastLevel.Warning => Color.FromRgb(0xE6, 0xA0, 0x20),
                ToastLevel.Error   => Color.FromRgb(0xFF, 0x3B, 0x30),
                _                  => Color.FromRgb(0x34, 0xC9, 0x54)
            };

            var root = new Border
            {
                Width = ToastWidth,
                Background = new SolidColorBrush(Color.FromArgb(220, 28, 28, 30)),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(14, 10, 14, 10),
                BorderBrush = new SolidColorBrush(accentColor) { Opacity = 0.5 },
                BorderThickness = new Thickness(1),
                Cursor = System.Windows.Input.Cursors.Hand,
                Opacity = 0
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var accent = new Border
            {
                Width = 3,
                CornerRadius = new CornerRadius(2),
                Background = new SolidColorBrush(accentColor),
                VerticalAlignment = VerticalAlignment.Stretch
            };
            Grid.SetColumn(accent, 0);

            var text = new TextBlock
            {
                Text = message,
                Foreground = Brushes.White,
                FontSize = 13,
                FontWeight = FontWeights.Normal,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(text, 2);

            grid.Children.Add(accent);
            grid.Children.Add(text);
            root.Child = grid;

            entry.Panel = root;

            // Click to dismiss early.
            root.MouseLeftButtonDown += (_, _) =>
            {
                FadeOut(entry.Panel, () => onDismiss(entry));
            };

            // Fade in.
            FadeIn(root);

            return entry;
        }

        private static void FadeIn(UIElement element)
        {
            var anim = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            element.BeginAnimation(UIElement.OpacityProperty, anim);
        }

        private static void FadeOut(UIElement element, Action onComplete)
        {
            var anim = new DoubleAnimation(element.Opacity, 0, TimeSpan.FromMilliseconds(250))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
            };
            anim.Completed += (_, _) => onComplete();
            element.BeginAnimation(UIElement.OpacityProperty, anim);
        }

        private void ScheduleDismiss(ToastEntry entry, Action onComplete)
        {
            int chars = (entry.Panel.Child is Grid g
                      && g.Children.Count > 1
                      && g.Children[1] is TextBlock tb)
                      ? tb.Text.Length : 20;

            double seconds = (chars / 25.0) + 2.0;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                if (!entry.Dismissed)
                {
                    entry.Dismissed = true;
                    FadeOut(entry.Panel, onComplete);
                }
            };
            entry.Timer = timer;
            timer.Start();
        }

        // ── Inner types ─────────────────────────────────────────────────────────

        private sealed class ToastEntry
        {
            public Border Panel { get; set; } = null!;
            public DispatcherTimer? Timer { get; set; }
            public bool Dismissed { get; set; }
        }
    }

    // ── Headless host window ─────────────────────────────────────────────────────
    // A transparent, click-through, topmost window that parents all headless toasts.
    // Each toast is absolutely positioned inside a Canvas via Left/Top.

    internal sealed class HeadlessToastHost : Window
    {
        private readonly Canvas _canvas;

        public HeadlessToastHost()
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            // Cover the whole work area so toasts can appear anywhere.
            var area = SystemParameters.WorkArea;
            Left = area.Left;
            Top = area.Top;
            Width = area.Width;
            Height = area.Height;

            _canvas = new Canvas();
            Content = _canvas;

            // Make the host window itself click-through; individual toasts
            // opt back in by setting IsHitTestVisible = true on their Border.
            Loaded += (_, _) =>
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                int ex = GetWindowLong(hwnd, -20);
                SetWindowLong(hwnd, -20, ex | 0x20 | 0x80000); // WS_EX_TRANSPARENT | WS_EX_LAYERED
            };
        }

        public void AddToast(UIElement element)
        {
            element.SetValue(UIElement.IsHitTestVisibleProperty, true);
            _canvas.Children.Add(element);
        }

        public void RemoveToast(UIElement element)
        {
            _canvas.Children.Remove(element);
        }

        public void PositionToast(UIElement element, double x, double y)
        {
            Canvas.SetLeft(element, x);
            Canvas.SetTop(element, y);
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hwnd, int index);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);
    }
}
