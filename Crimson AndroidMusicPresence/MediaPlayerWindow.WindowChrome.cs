using System;
using System.Collections.Generic;
using System.Drawing.Printing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace musicpresense
{
    public partial class MediaPlayerWindow
    {
        // ── Window Chrome Toggle (called "fullscreen" in the UI but really
        // just hides the window's title bar / borders) ────────────────────────
        private void BtnFullscreen_Click(object sender, RoutedEventArgs e)
        {
            Debugger.show("[MEDIAPLAYER] Fullscreen button pressed.");
            if (_isFullscreen)
            {
                ExitFullscreen();
            }
            else
            {
                EnterFullscreen();
            }
            RenderFullscreenButtonIcon();
        }
        private void EnterFullscreen()
        {
            // Snapshot the chrome state so we can restore it byte-for-byte.
            // We deliberately do NOT touch WindowState or Topmost here. The user
            // wanted "no decorations", not actual fullscreen, so the window keeps
            // its current size/position and stays a regular movable, resizable window.
            _prevWindowStyle = WindowStyle;
            _prevResizeMode = ResizeMode;

            WindowStyle = WindowStyle.None;
            // Keep resize available, but switch to the gripless variant since the
            // bottom-right grip lives in the chrome we just hid.
            ResizeMode = ResizeMode.CanResize;

            _isFullscreen = true;
        }
        private void ExitFullscreen()
        {
            WindowStyle = _prevWindowStyle;
            ResizeMode = _prevResizeMode;

            _isFullscreen = false;
        }

        /// <summary>
        /// While chromeless, lets the user drag the window by clicking-and-holding
        /// any background area that didn't otherwise handle the click. Hooked on
        /// the bubbling MouseLeftButtonDown event so controls (buttons, sliders)
        /// still work normally.
        /// </summary>
        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!_isFullscreen) return;
            if (e.ChangedButton != MouseButton.Left) return;
            // If the click was already handled by something else, leave it alone.
            if (e.Handled) return;
            try { DragMove(); } catch { }
        }
        private void RenderFullscreenButtonIcon()
        {
            if (BtnFullscreen == null) return;
            var brush = ResolveIconBrush();
            BtnFullscreen.Content = BuildFullscreenIcon(brush, _isFullscreen, 18);
            BtnFullscreen.ToolTip = _isFullscreen ? "Show window decorations" : "Hide window decorations";
        }

        // ── Always on Top ─────────────────────────────────────────────────────
        private void BtnAlwaysOnTop_Click(object sender, RoutedEventArgs e)
        {
            _alwaysOnTop = !_alwaysOnTop;
            Debugger.show($"[MEDIAPLAYER] Always on top button pressed. New state: {_alwaysOnTop}.");
            // While in fullscreen we still let the user toggle this, but the
            // restore-from-fullscreen path will OR it back in either way.
            Topmost = _alwaysOnTop;
            RefreshAlwaysOnTopButton();
        }
        private void RefreshAlwaysOnTopButton()
        {
            if (BtnAlwaysOnTop == null || AlwaysOnTopContent == null) return;

            var brush = ResolveIconBrush();
            AlwaysOnTopContent.Children.Clear();

            AlwaysOnTopContent.Children.Add(BuildAlwaysOnTopIcon(brush, _alwaysOnTop, 14));
            AlwaysOnTopContent.Children.Add(new TextBlock
            {
                Text = _alwaysOnTop ? "On top" : "Always on top",
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0),
                Foreground = brush
            });

            BtnAlwaysOnTop.ToolTip = _alwaysOnTop
                ? "Always on top is on. Click to turn off."
                : "Keep this window always on top.";

            // Subtle border in the icon brush so the pill stays legible over
            // both gradient and idle backgrounds.
            var borderColor = (brush is SolidColorBrush scb) ? scb.Color : Colors.White;
            BtnAlwaysOnTop.BorderBrush = new SolidColorBrush(borderColor)
            {
                Opacity = _alwaysOnTop ? 0.85 : 0.45
            };
        }

        // ── Connection Info ─────────────────────────────────────────────────────
        private void BtnConnectionInfo_Click(object sender, RoutedEventArgs e)
        {
            Debugger.show($"[CONNECTION] Connection pill clicked. Popup currently {(ConnectionInfoPopup.IsOpen ? "open" : "closed")}.");
            TxtConnectionStatus.Text = _connectionStatusText;
            TxtConnectionDetail.Text = _connectionDetailText;
            ConnectionInfoPopup.IsOpen = !ConnectionInfoPopup.IsOpen;
        }
        private void RefreshConnectionButton()
        {
            ConnectionDot.Fill = new SolidColorBrush(_connectionColor);
            BtnConnectionInfo.BorderBrush = new SolidColorBrush(_connectionColor) { Opacity = 0.7 };
        }

        // ── Help / What's this? ───────────────────────────────────────────────
        private void BtnHelp_Click(object sender, RoutedEventArgs e)
        {
            var window = new MediaPlayerHelpWindow();
            window.Owner = this;
            window.ShowDialog();
        }
        private void RenderHelpButtonIcon()
        {
            if (BtnHelp == null) return;
            var brush = ResolveIconBrush();
            BtnHelp.Content = BuildHelpIcon(brush, 18);
        }


        private void RenderFastSeekIcons()
        {
            var brush = ResolveIconBrush();
            BtnSeekBack.Content = BuildSeekIcon(brush, -30, 30);
            BtnSeekFwd.Content = BuildSeekIcon(brush, 30, 30);
        }
        private void RenderSettingsPaneArrowIcon()
        {
            var iconBrush = TryFindResource("ThemeControlForegroundBrush") as Brush ?? Brushes.White;
            BtnShowSettingsPane.Content = BuildRevealSettingsArrowIcon(iconBrush);
            if (BtnCollapseSettingsPane != null)
                BtnCollapseSettingsPane.Content = BuildCollapseSettingsArrowIcon(iconBrush);
        }
        private void RenderTransportIcons(bool isPlaying)
        {
            var iconBrush = ResolveIconBrush();

            const double sideIconSize = 30;
            const double centerIconSize = 42;

            BtnPrevious.Content = BuildPreviousIcon(iconBrush, sideIconSize);
            BtnPause.Content = isPlaying ? BuildPauseIcon(iconBrush, centerIconSize) : BuildPlayIcon(iconBrush, centerIconSize);
            BtnNext.Content = BuildNextIcon(iconBrush, sideIconSize);
        }
        private void RenderAuxiliaryIcons()
        {
            var iconBrush = ResolveIconBrush();
            const double auxIconSize = 22;

            BtnVolume.Content = BuildVolumeIcon(iconBrush, auxIconSize, VolumeIconLevel.High);
            BtnLyrics.Content = BuildLyricsIcon(iconBrush, auxIconSize, _lyricsViewActive);

            RenderFastSeekIcons();
            RenderAudioLinkButton();
            RenderHelpButtonIcon();
            RenderFullscreenButtonIcon();
            RefreshAudioQualityButton();
            RefreshAlwaysOnTopButton();
        }
    }
}