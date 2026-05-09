using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
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
            ApplyLyricsViewVisibility();
            RenderAuxiliaryIcons();

            if (_lyricsViewActive)
            {
                // Pull current lines from the manager (it may already have them loaded
                // from an earlier OnPlaybackChanged call).
                if (_lyricsManager != null)
                {
                    var data = _lyricsManager.GetCurrentTrackData();
                    AdoptLyricsData(data, scrollToCurrent: true);
                }
                else
                {
                    AdoptLyricsData(new LyricsOverlayManager.LyricsTrackData(Array.Empty<LyricsOverlayManager.LyricsLineDto>(), false), scrollToCurrent: true);
                }
                StartLyricsTimer();
            }
            else
            {
                StopLyricsTimer();
                StopLyricsScrollLoop();
            }
        }
        private void ApplyLyricsViewVisibility()
        {
            // The inline lyrics replace the cover art visually. We collapse the cover
            // Viewbox's parent (CoverBorder is inside a Viewbox) by hiding LyricsViewHost
            // / showing it. The cover layers stay where they are; we just toggle which
            // child of the parent grid is visible.
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

            // Always cache; only re-render the panel when the inline view is open,
            // to avoid wasted layout work.
            if (_lyricsViewActive)
            {
                AdoptLyricsData(data, scrollToCurrent: true);
            }
            else
            {
                _lyricsLines = data.Lines;
                _lyricsAreTimed = data.IsTimed;
                _lyricsHighlightedIndex = -1;
            }
        }
        private void AdoptLyricsData(LyricsOverlayManager.LyricsTrackData data, bool scrollToCurrent)
        {
            _lyricsLines = data.Lines;
            _lyricsAreTimed = data.IsTimed;
            _lyricsHighlightedIndex = -1;

            RebuildLyricsPanel();

            if (scrollToCurrent)
            {
                // Defer the scroll to after layout so ScrollViewer measurements are valid.
                Dispatcher.BeginInvoke(new Action(() => UpdateLyricsHighlightAndScroll(animate: false)), DispatcherPriority.Loaded);
            }
        }
        private void RebuildLyricsPanel()
        {
            LyricsItemsHost.Children.Clear();
            _lyricsLineBlocks.Clear();
            _lyricsLineHosts.Clear();

            // Recompute and freeze the per-line brushes once so highlight updates can
            // reuse the same instances without allocating. Allocating a fresh brush per
            // tick was causing each line transition to flash for a frame as WPF realized
            // the new brush.
            _lyricsInactiveBrush = ComputeLyricsInactiveBrush();
            _lyricsActiveBrush = ComputeLyricsActiveBrush();
            _lyricsActiveLineBgBrush = ComputeLyricsActiveLineBgBrush();

            if (_lyricsLines.Count == 0)
            {
                LyricsEmptyState.Visibility = Visibility.Visible;
                return;
            }

            LyricsEmptyState.Visibility = Visibility.Collapsed;

            // Top spacer so the first line can be vertically centered when scrolled to.
            LyricsItemsHost.Children.Add(new Border
            {
                Height = 180,
                Background = Brushes.Transparent,
                IsHitTestVisible = false
            });

            for (int i = 0; i < _lyricsLines.Count; i++)
            {
                var line = _lyricsLines[i];

                // Treat empty plain-text separator lines as visual gap.
                if (line.Text.Length == 0)
                {
                    LyricsItemsHost.Children.Add(new Border
                    {
                        Height = 18,
                        Background = Brushes.Transparent,
                        IsHitTestVisible = false
                    });
                    _lyricsLineBlocks.Add(null!); // keep index alignment with _lyricsLines
                    _lyricsLineHosts.Add(null!);
                    continue;
                }

                var tb = new TextBlock
                {
                    Text = line.Text,
                    FontSize = 18,
                    // Use one consistent FontWeight for every line - changing weight on
                    // active/inactive transitions remeasures the whole list and causes
                    // a visible flash. Active state is conveyed by Opacity, Foreground,
                    // and the wrapper Border's Background instead.
                    FontWeight = FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = TextAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Margin = new Thickness(16, 8, 16, 8),
                    Foreground = _lyricsInactiveBrush,
                    Opacity = _lyricsAreTimed ? 0.45 : 0.85
                };

                // Wrap the TextBlock in a Border so the active line can paint a darkened
                // pill behind itself. Border background is transparent for inactive lines.
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

            // Bottom spacer so the last line can be centered.
            LyricsItemsHost.Children.Add(new Border
            {
                Height = 180,
                Background = Brushes.Transparent,
                IsHitTestVisible = false
            });
        }
        private Brush ComputeLyricsInactiveBrush()
        {
            SolidColorBrush brush;
            if (_hasSong)
            {
                brush = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF));
            }
            else
            {
                brush = IsDarkThemeActive()
                    ? new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF))
                    : new SolidColorBrush(Color.FromArgb(0xCC, 0x00, 0x00, 0x00));
            }
            brush.Freeze();
            return brush;
        }
        private Brush ComputeLyricsActiveBrush()
        {
            SolidColorBrush brush;
            if (_hasSong)
            {
                brush = new SolidColorBrush(Colors.White);
            }
            else
            {
                brush = IsDarkThemeActive() ? new SolidColorBrush(Colors.White) : new SolidColorBrush(Colors.Black);
            }
            brush.Freeze();
            return brush;
        }
        private Brush ComputeLyricsActiveLineBgBrush()
        {
            // Darkened pill behind the active line. Slightly heavier when a song's gradient
            // is showing through (so it reads against varied colors), lighter on solid theme.
            SolidColorBrush brush;
            if (_hasSong)
            {
                brush = new SolidColorBrush(Color.FromArgb(0x66, 0x00, 0x00, 0x00));
            }
            else
            {
                brush = IsDarkThemeActive()
                    ? new SolidColorBrush(Color.FromArgb(0x55, 0x00, 0x00, 0x00))
                    : new SolidColorBrush(Color.FromArgb(0x22, 0x00, 0x00, 0x00));
            }
            brush.Freeze();
            return brush;
        }
        private void StartLyricsTimer()
        {
            if (_lyricsTimer == null)
            {
                _lyricsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
                _lyricsTimer.Tick += LyricsTimer_Tick;
            }
            if (!_lyricsTimer.IsEnabled)
                _lyricsTimer.Start();
        }
        private void StopLyricsTimer()
        {
            if (_lyricsTimer != null && _lyricsTimer.IsEnabled)
                _lyricsTimer.Stop();
        }
        private void LyricsTimer_Tick(object? sender, EventArgs e)
        {
            if (!_lyricsViewActive) return;
            UpdateLyricsHighlightAndScroll(animate: true);
        }
        private void UpdateLyricsHighlightAndScroll(bool animate)
        {
            if (_lyricsLines.Count == 0) return;

            int newIdx;
            if (_lyricsAreTimed && _lyricsManager != null)
            {
                newIdx = _lyricsManager.GetCurrentLineIndex();
            }
            else
            {
                // Plain-text: no auto-highlight; just leave nothing highlighted.
                newIdx = -1;
            }

            if (newIdx == _lyricsHighlightedIndex)
                return;

            // Restore old block style.
            // NOTE: We deliberately do NOT change FontWeight between active/inactive,
            // because that alters text metrics, forces the StackPanel to remeasure,
            // shifts ExtentHeight, and makes the in-flight scroll animation land on
            // a different target than was computed when it started. The visible result
            // is a flash on every line change. We rely on Opacity, Foreground, and
            // the wrapper Border's Background to distinguish active vs inactive.
            if (_lyricsHighlightedIndex >= 0 && _lyricsHighlightedIndex < _lyricsLineBlocks.Count)
            {
                var prev = _lyricsLineBlocks[_lyricsHighlightedIndex];
                if (prev != null)
                {
                    prev.Opacity = 0.45;
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

            if (newIdx < 0 || newIdx >= _lyricsLineBlocks.Count)
                return;

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

            // For an immediate (non-animated) scroll we may need a layout pass so the
            // target's TransformToAncestor returns a valid offset (e.g. on first paint).
            // During animations we deliberately skip UpdateLayout to avoid frame stalls
            // that would visibly stutter the scroll.
            if (!animate)
            {
                LyricsScroller.UpdateLayout();
            }

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

                // Update the target and let the per-frame loop ease toward it. The loop
                // converges from wherever the scroller currently is, so a target change
                // mid-flight just adjusts the destination - no clock restart, no jump.
                _lyricsTargetScrollOffset = targetOffset;
                StartLyricsScrollLoop();
            }
            catch
            {
                // Layout may not yet be ready - skip silently; next tick will retry.
            }
        }

        // ── Continuous scroll loop ───────────────────────────────────────────
        // CompositionTarget.Rendering fires once per frame on the UI dispatcher.
        // We lerp VerticalOffset toward _lyricsTargetScrollOffset by a fixed
        // fraction each frame (exponential smoothing). When close enough we snap
        // and detach the handler. This produces smooth motion that handles fast
        // line changes gracefully, because the target just shifts and the lerp
        // continues without any restart.
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
            if (LyricsScroller == null)
            {
                StopLyricsScrollLoop();
                return;
            }

            double current = LyricsScroller.VerticalOffset;
            double target = _lyricsTargetScrollOffset;
            double delta = target - current;

            // Snap and stop when close enough; sub-pixel motion isn't visible
            // and would otherwise keep the loop alive forever.
            if (Math.Abs(delta) < 0.5)
            {
                LyricsScroller.ScrollToVerticalOffset(target);
                StopLyricsScrollLoop();
                return;
            }

            // Exponential smoothing: each frame we close ~22% of the remaining gap.
            // Tuned to feel responsive (~6-8 frames at 60fps to settle) without
            // overshooting on a target change. Lower = slower/smoother, higher = snappier.
            const double smoothing = 0.22;
            double next = current + (delta * smoothing);

            LyricsScroller.ScrollToVerticalOffset(next);
        }
    }
}