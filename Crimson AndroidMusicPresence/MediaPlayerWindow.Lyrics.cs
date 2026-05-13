using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace musicpresense
{
    public partial class MediaPlayerWindow
    {
        private void BtnLyrics_Click(object sender, RoutedEventArgs e)
        {
            ToggleInlineLyricsView();
        }

        // ── Inline Lyrics View ────────────────────────────────────────────────

        private void ToggleInlineLyricsView()
        {
            _lyricsViewActive = !_lyricsViewActive;
            PersistRuntimeState();
            ApplyLyricsViewVisibility();
            RenderAuxiliaryIcons();

            if (_lyricsViewActive)
            {
                if (_lyricsManager != null)
                {
                    _lyricsManager.PositionChanged += OnLyricsPositionChanged;
                    AdoptLyricsData(_lyricsManager.GetCurrentTrackData());
                }
                else
                {
                    AdoptLyricsData(new LyricsOverlayManager.LyricsTrackData(
                        Array.Empty<LyricsOverlayManager.LyricsLineDto>(), false));
                }
            }
            else
            {
                if (_lyricsManager != null)
                    _lyricsManager.PositionChanged -= OnLyricsPositionChanged;
                StopLyricsTimer();
                StopLyricsScrollLoop();
            }
        }

        private void ApplyLyricsViewVisibility()
        {
            if (_lyricsViewActive)
            {
                LyricsViewHost.Visibility = Visibility.Visible;
                CoverBorder.Visibility = Visibility.Collapsed;
            }
            else
            {
                LyricsViewHost.Visibility = Visibility.Collapsed;
                CoverBorder.Visibility = Visibility.Visible;
            }
        }

        private void OnLyricsLinesChanged(LyricsOverlayManager.LyricsTrackData data)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(() => OnLyricsLinesChanged(data));
                return;
            }

            if (_lyricsViewActive)
                AdoptLyricsData(data);
            else
            {
                _lyricsLines = data.Lines;
                _lyricsAreTimed = data.IsTimed;
                _lyricsHighlightedIndex = -1;
            }
        }

        // Called every poll (including after seeks). Re-applies highlight and reschedules
        // the timer without rebuilding the panel.
        private void OnLyricsPositionChanged()
        {
            if (!_lyricsViewActive || _lyricsLines.Count == 0) return;
            StopLyricsTimer();
            ApplyHighlight(animate: true);
            ScheduleNextLineTick();
        }

        private void AdoptLyricsData(LyricsOverlayManager.LyricsTrackData data)
        {
            StopLyricsTimer();
            StopLyricsScrollLoop();

            _lyricsLines = data.Lines;
            _lyricsAreTimed = data.IsTimed;
            _lyricsHighlightedIndex = -1;

            RebuildLyricsPanel();

            Dispatcher.BeginInvoke(new Action(() =>
            {
                ApplyHighlight(animate: false);
                ScheduleNextLineTick();
            }), DispatcherPriority.Loaded);
        }

        private void RebuildLyricsPanel()
        {
            LyricsItemsHost.Children.Clear();
            _lyricsLineBlocks.Clear();
            _lyricsLineHosts.Clear();

            _lyricsInactiveBrush = ComputeLyricsInactiveBrush();
            _lyricsActiveBrush = ComputeLyricsActiveBrush();
            _lyricsActiveLineBgBrush = ComputeLyricsActiveLineBgBrush();

            if (_lyricsLines.Count == 0)
            {
                LyricsEmptyState.Visibility = Visibility.Visible;
                return;
            }

            LyricsEmptyState.Visibility = Visibility.Collapsed;

            LyricsItemsHost.Children.Add(new Border { Height = 180, Background = Brushes.Transparent, IsHitTestVisible = false });

            for (int i = 0; i < _lyricsLines.Count; i++)
            {
                var line = _lyricsLines[i];

                if (line.Text.Length == 0)
                {
                    LyricsItemsHost.Children.Add(new Border { Height = 18, Background = Brushes.Transparent, IsHitTestVisible = false });
                    _lyricsLineBlocks.Add(null!);
                    _lyricsLineHosts.Add(null!);
                    continue;
                }

                // Untimed lines (section headers) are always visible at reduced size/opacity.
                // Timed lyric lines start dim and brighten when active.
                double initialOpacity = (!_lyricsAreTimed || line.IsUntimed) ? 0.85 : 0.45;

                var tb = new TextBlock
                {
                    Text = line.Text,
                    FontSize = line.IsUntimed ? 14 : 18,
                    FontWeight = FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = TextAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Margin = new Thickness(16, line.IsUntimed ? 4 : 8, 16, line.IsUntimed ? 4 : 8),
                    Foreground = _lyricsInactiveBrush,
                    Opacity = initialOpacity
                };

                var host = new Border
                {
                    Background = Brushes.Transparent,
                    CornerRadius = new CornerRadius(8),
                    Margin = new Thickness(8, 0, 8, 0),
                    Child = tb
                };

                LyricsItemsHost.Children.Add(host);
                _lyricsLineBlocks.Add(tb);
                _lyricsLineHosts.Add(host);
            }

            LyricsItemsHost.Children.Add(new Border { Height = 180, Background = Brushes.Transparent, IsHitTestVisible = false });
        }

        private Brush ComputeLyricsInactiveBrush()
        {
            SolidColorBrush b;
            if (_hasSong)
                b = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF));
            else
                b = IsDarkThemeActive()
                    ? new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF))
                    : new SolidColorBrush(Color.FromArgb(0xCC, 0x00, 0x00, 0x00));
            b.Freeze();
            return b;
        }

        private Brush ComputeLyricsActiveBrush()
        {
            SolidColorBrush b = _hasSong || IsDarkThemeActive()
                ? new SolidColorBrush(Colors.White)
                : new SolidColorBrush(Colors.Black);
            b.Freeze();
            return b;
        }

        private Brush ComputeLyricsActiveLineBgBrush()
        {
            SolidColorBrush b;
            if (_hasSong)
                b = new SolidColorBrush(Color.FromArgb(0x66, 0x00, 0x00, 0x00));
            else
                b = IsDarkThemeActive()
                    ? new SolidColorBrush(Color.FromArgb(0x55, 0x00, 0x00, 0x00))
                    : new SolidColorBrush(Color.FromArgb(0x22, 0x00, 0x00, 0x00));
            b.Freeze();
            return b;
        }

        // ── Event-driven line scheduling ──────────────────────────────────────

        private void StopLyricsTimer()
        {
            if (_lyricsTimer == null) return;
            _lyricsTimer.Stop();
            _lyricsTimer.Tick -= LyricsTimer_Tick;
            _lyricsTimer = null;
        }

        private void ScheduleNextLineTick()
        {
            if (!_lyricsViewActive || !_lyricsAreTimed || _lyricsManager == null || _lyricsLines.Count == 0)
                return;

            double delayMs = _lyricsManager.GetMsUntilNextLine();
            delayMs = Math.Max(50, Math.Min(delayMs, 30_000));

            _lyricsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(delayMs) };
            _lyricsTimer.Tick += LyricsTimer_Tick;
            _lyricsTimer.Start();
        }

        private void LyricsTimer_Tick(object? sender, EventArgs e)
        {
            StopLyricsTimer();
            if (!_lyricsViewActive) return;
            ApplyHighlight(animate: true);
            ScheduleNextLineTick();
        }

        // Applies highlight for the current line. Only writes to the UI when the index changed.
        private void ApplyHighlight(bool animate)
        {
            if (_lyricsLines.Count == 0) return;

            int newIdx = (_lyricsAreTimed && _lyricsManager != null)
                ? _lyricsManager.GetCurrentLineIndex()
                : -1;

            if (newIdx == _lyricsHighlightedIndex)
                return;

            // Deactivate previous line.
            if (_lyricsHighlightedIndex >= 0 && _lyricsHighlightedIndex < _lyricsLineBlocks.Count)
            {
                var prev = _lyricsLineBlocks[_lyricsHighlightedIndex];
                if (prev != null)
                {
                    // Restore to the correct resting opacity for this line type.
                    prev.Opacity = (_lyricsLines[_lyricsHighlightedIndex].IsUntimed) ? 0.85 : 0.45;
                    prev.Foreground = _lyricsInactiveBrush;
                }
                if (_lyricsHighlightedIndex < _lyricsLineHosts.Count)
                {
                    var prevHost = _lyricsLineHosts[_lyricsHighlightedIndex];
                    if (prevHost != null)
                        prevHost.Background = Brushes.Transparent;
                }
            }

            _lyricsHighlightedIndex = newIdx;

            if (newIdx < 0 || newIdx >= _lyricsLineBlocks.Count) return;

            var current = _lyricsLineBlocks[newIdx];
            if (current == null) return;

            current.Opacity = 1.0;
            current.Foreground = _lyricsActiveBrush;

            if (newIdx < _lyricsLineHosts.Count)
            {
                var host = _lyricsLineHosts[newIdx];
                if (host != null)
                    host.Background = _lyricsActiveLineBgBrush;
            }

            ScrollLyricsToCenter(current, animate);
        }

        private void ScrollLyricsToCenter(TextBlock target, bool animate)
        {
            if (LyricsScroller == null) return;

            if (!animate)
                LyricsScroller.UpdateLayout();

            try
            {
                var transform = target.TransformToAncestor(LyricsItemsHost);
                var topInPanel = transform.Transform(new Point(0, 0)).Y;
                var targetCenter = topInPanel + (target.ActualHeight / 2.0);

                var viewportH = LyricsScroller.ViewportHeight;
                if (viewportH <= 0) viewportH = LyricsScroller.ActualHeight;
                if (viewportH <= 0) return;

                double targetOffset = targetCenter - (viewportH / 2.0);
                targetOffset = Math.Max(0, Math.Min(targetOffset, Math.Max(0, LyricsScroller.ExtentHeight - viewportH)));

                if (!animate)
                {
                    StopLyricsScrollLoop();
                    _lyricsTargetScrollOffset = targetOffset;
                    LyricsScroller.ScrollToVerticalOffset(targetOffset);
                    return;
                }

                // Lerp loop handles any distance smoothly, including large jumps from seeks.
                _lyricsTargetScrollOffset = targetOffset;
                StartLyricsScrollLoop();
            }
            catch { }
        }

        // ── Scroll lerp loop ─────────────────────────────────────────────────

        private void StartLyricsScrollLoop()
        {
            if (_lyricsScrollLoopActive) return;
            _lyricsScrollLoopActive = true;
            CompositionTarget.Rendering += LyricsScrollLoop_Tick;
        }

        private void StopLyricsScrollLoop()
        {
            if (!_lyricsScrollLoopActive) return;
            _lyricsScrollLoopActive = false;
            CompositionTarget.Rendering -= LyricsScrollLoop_Tick;
        }

        private void LyricsScrollLoop_Tick(object? sender, EventArgs e)
        {
            if (LyricsScroller == null) { StopLyricsScrollLoop(); return; }

            double current = LyricsScroller.VerticalOffset;
            double delta = _lyricsTargetScrollOffset - current;

            if (Math.Abs(delta) < 0.5)
            {
                LyricsScroller.ScrollToVerticalOffset(_lyricsTargetScrollOffset);
                StopLyricsScrollLoop();
                return;
            }

            LyricsScroller.ScrollToVerticalOffset(current + delta * 0.22);
        }
    }
}