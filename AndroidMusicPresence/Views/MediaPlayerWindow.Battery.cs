using System;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace AndroidMusicPresenceLink
{
    public partial class MediaPlayerWindow
    {
        private DispatcherTimer? _batteryTimer;

        // Poll rate comes from config (default 150s). Kept long by default to keep
        // ADB chatter minimal; the icon is purely informational.
        private static TimeSpan BatteryPollInterval
            => TimeSpan.FromSeconds(Math.Max(5, App.Config.MediaPlayer.BatteryPollIntervalSeconds));

        // Last known values, kept so we can re-render on theme/song-state changes
        // without having to hit ADB again.
        private int _batteryLevel = -1;       // -1 = unknown yet
        private bool _batteryCharging = false;

        // Re-reads the poll rate from config; called whenever a player setting changes.
        // Setting Interval on a running DispatcherTimer restarts its period.
        private void RefreshBatteryPollInterval()
        {
            if (_batteryTimer == null) return;
            var interval = BatteryPollInterval;
            if (_batteryTimer.Interval != interval)
                _batteryTimer.Interval = interval;
        }

        /// <summary>
        /// Wires up the battery poll timer and triggers an immediate first read
        /// so the icon isn't blank until the first tick.
        /// </summary>
        private void StartBatteryPolling()
        {
            if (_batteryTimer != null) return;

            _batteryTimer = new DispatcherTimer { Interval = BatteryPollInterval };
            _batteryTimer.Tick += async (_, _) => await PollBatteryAsync().ConfigureAwait(false);
            _batteryTimer.Start();

            // Fire-and-forget initial poll. We don't block the constructor / Loaded
            // event on this; the icon stays in its "unknown" state until results come back.
            _ = PollBatteryAsync();
        }

        private void StopBatteryPolling()
        {
            try
            {
                _batteryTimer?.Stop();
                _batteryTimer = null;
            }
            catch { }
        }

        /// <summary>
        /// Runs a single `dumpsys battery` over ADB and parses the level + charging state.
        /// Discards everything else. The Android-side grep keeps the wire payload tiny.
        /// </summary>
        private async Task PollBatteryAsync()
        {
            try
            {
                var device = (Application.Current as App)?.GetCurrentDevice() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(device))
                {
                    // No device connected yet. Leave the icon in its placeholder state.
                    return;
                }

                var output = await AdbHelper.RunAdbCaptureAsync(
                    $"-s {device} shell sh -c \"dumpsys battery | awk -F: '/^  level:/{{l=\\$2}} /^  status:/{{s=\\$2}} /^  AC powered:/{{a=\\$2}} /^  USB powered:/{{u=\\$2}} /^  Wireless powered:/{{w=\\$2}} END{{print l; print s; print a; print u; print w}}'\""
                ).ConfigureAwait(false);

                if (string.IsNullOrWhiteSpace(output))
                {
                    return;
                }

                int level = -1;
                int status = -1;
                bool acPowered = false;
                bool usbPowered = false;
                bool wirelessPowered = false;

                var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length < 5)
                {
                    Debugger.show($"[BATTERY] Unexpected line count: {lines.Length}");
                    return;
                }

                int.TryParse(lines[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out level);
                int.TryParse(lines[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out status);
                acPowered = string.Equals(lines[2].Trim(), "true", StringComparison.OrdinalIgnoreCase);
                usbPowered = string.Equals(lines[3].Trim(), "true", StringComparison.OrdinalIgnoreCase);
                wirelessPowered = string.Equals(lines[4].Trim(), "true", StringComparison.OrdinalIgnoreCase);

                if (level < 0)
                {
                    return;
                }

                // Status codes: 2 = charging, 3 = discharging, 4 = not charging, 5 = full.
                bool charging = (status == 2) || acPowered || usbPowered || wirelessPowered;
                if (status == 5) charging = false;

                if (level > 100) level = 100;

                Debugger.show($"[BATTERY] level={level}% status={status} ac={acPowered} charging={charging}");

                Dispatcher.Invoke(() =>
                {
                    _batteryLevel = level;
                    _batteryCharging = charging;
                    RenderBatteryButtonIcon();
                });
            }
            catch (Exception ex)
            {
                Debugger.show("[BATTERY] Poll failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Re-renders the battery glyph using the last polled values and the current
        /// config-driven style. Safe to call before the first poll completes; in that
        /// case it renders a placeholder.
        /// </summary>
        private void RenderBatteryButtonIcon()
        {
            if (BtnBattery == null) return;

            var cfg = App.Config;
            var opts = new BatteryRenderOptions
            {
                Style = cfg.MediaPlayer.BatteryVisualStyle,
                ShowPercent = cfg.MediaPlayer.BatteryShowPercent,
                PercentInside = cfg.MediaPlayer.BatteryPercentInside,
                ShowBolt = cfg.MediaPlayer.BatteryShowBolt,
                BoltInside = cfg.MediaPlayer.BatteryBoltInside,
                ColorMode = cfg.MediaPlayer.BatteryColorMode
            };

            int level = _batteryLevel;
            bool charging = _batteryLevel >= 0 && _batteryCharging;

            // Widen the button when the percentage or bolt sits outside the glyph, so the
            // uniform Viewbox isn't squeezed into the default icon-only footprint. The button
            // height stays at the standard 32px for every style so it lines up vertically with
            // the fullscreen toggle and the rest of the top-row icons; the tall vertical glyph
            // simply scales down to fit that height via its uniform Viewbox.
            bool percentInside = opts.Style != BatteryVisualStyle.Vertical && opts.PercentInside;
            bool percentOutside = opts.ShowPercent && level >= 0 && !percentInside;
            bool boltOutside = opts.ShowBolt && charging && !opts.BoltInside;

            double width = 56;
            if (opts.Style == BatteryVisualStyle.Vertical)
            {
                width = 30;             // narrow glyph-only footprint
                if (percentOutside) width += 28;
                if (boltOutside) width += 16;
            }
            else
            {
                if (percentOutside) width += 22;
                if (boltOutside) width += 16;
            }
            BtnBattery.Width = width;

            // The vertical glyph is intrinsically tall, so a 32px button height would force it
            // to scale down a lot. Give it a taller button, but keep its CENTER aligned with the
            // 32px fullscreen toggle (center at y=16) by pulling the top margin up by the extra
            // half-height. Horizontal styles keep the standard 32px box anchored at the top.
            const double normalH = 32;
            const double verticalH = 46;
            if (opts.Style == BatteryVisualStyle.Vertical)
            {
                BtnBattery.Height = verticalH;
                double extraTop = (verticalH - normalH) / 2.0;   // 7
                BtnBattery.Margin = new Thickness(0, -extraTop, 32, 0);
            }
            else
            {
                BtnBattery.Height = normalH;
                BtnBattery.Margin = new Thickness(0, 0, 32, 0);
            }

            BtnBattery.Content = BuildBatteryIcon(ResolveIconBrush(), level, charging, opts);

            if (_batteryLevel < 0)
            {
                BtnBattery.ToolTip = "Battery: reading...";
                return;
            }

            BtnBattery.ToolTip = _batteryCharging
                ? $"Battery: {_batteryLevel}% (charging)"
                : $"Battery: {_batteryLevel}%";
        }

        /// <summary>
        /// Bundles the config-driven battery display options so the render method
        /// signature stays manageable.
        /// </summary>
        private struct BatteryRenderOptions
        {
            public BatteryVisualStyle Style;
            public bool ShowPercent;
            public bool PercentInside;   // ignored for Vertical (always outside)
            public bool ShowBolt;
            public bool BoltInside;
            public BatteryColorMode ColorMode;
        }

        // Fixed palette shared by every style. Only the theme-driven brush changes.
        private static readonly Brush BatWhiteFill = Brushes.White;
        private static readonly Brush BatGreenFill = new SolidColorBrush(Color.FromRgb(0x34, 0xC7, 0x59));
        private static readonly Brush BatRedFill = new SolidColorBrush(Color.FromRgb(0xFF, 0x3B, 0x30));
        private static readonly Brush BatLightGrey = new SolidColorBrush(Color.FromRgb(0xD0, 0xD0, 0xD0));
        private static readonly Brush BatDarkGrey = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));

        /// <summary>
        /// Builds the battery glyph for the current style and options. Dispatches to one
        /// of the three style builders, then horizontally stacks any "outside" percentage
        /// or bolt next to the glyph inside a single Viewbox.
        /// </summary>
        private static Viewbox BuildBatteryIcon(Brush themeBrush, int level, bool charging, BatteryRenderOptions opts)
        {
            // Vertical style ignores the inside-percentage flag: percentage is always outside.
            bool percentInside = opts.Style != BatteryVisualStyle.Vertical && opts.PercentInside;
            bool wantPercent = opts.ShowPercent && level >= 0;
            bool wantBolt = opts.ShowBolt && charging;

            bool percentOutside = wantPercent && !percentInside;
            bool boltOutside = wantBolt && !opts.BoltInside;

            // The glyph itself, with whatever lands inside it.
            var glyph = opts.Style switch
            {
                BatteryVisualStyle.Pill => BuildPillGlyph(themeBrush, level, charging, opts, percentInside),
                BatteryVisualStyle.Vertical => BuildVerticalGlyph(themeBrush, level, charging, opts),
                _ => BuildClassicGlyph(themeBrush, level, charging, opts, percentInside),
            };

            // Fast path: nothing sits outside, so the glyph canvas is the whole picture.
            if (!percentOutside && !boltOutside)
                return WrapGlyph(glyph);

            // Otherwise lay the glyph and the outside elements side by side. Each piece is a
            // sized Viewbox so the horizontal StackPanel can measure it reliably (a bare Canvas
            // can report a zero desired size during layout). Heights are matched so the row
            // baseline-aligns; the outer Viewbox then scales the whole row into the button.
            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            // Target on-screen height for the glyph within the row, before the outer scale.
            double glyphH = opts.Style == BatteryVisualStyle.Vertical ? 40 : 22;
            double glyphW = glyphH * (glyph.Width / glyph.Height);

            if (boltOutside)
            {
                Brush boltBrush = ResolveBatteryTextBrush(themeBrush, level, opts.ColorMode);
                row.Children.Add(new Viewbox
                {
                    Width = glyphH * 0.5,
                    Height = glyphH,
                    Margin = new Thickness(0, 0, 2, 0),
                    Child = BuildBoltCanvas(boltBrush)
                });
            }

            row.Children.Add(new Viewbox
            {
                Width = glyphW,
                Height = glyphH,
                Child = glyph
            });

            if (percentOutside)
            {
                Brush textBrush = ResolveBatteryTextBrush(themeBrush, level, opts.ColorMode);
                // Vertical glyph is tall, so a larger number balances it; horizontal stays compact.
                double fontSize = opts.Style == BatteryVisualStyle.Vertical ? 20 : 14;
                row.Children.Add(new TextBlock
                {
                    Text = level + "%",
                    FontSize = fontSize,
                    FontWeight = FontWeights.Bold,
                    Foreground = textBrush,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(4, 0, 0, 0)
                });
            }

            return new Viewbox { Stretch = Stretch.Uniform, Margin = new Thickness(2), Child = row };
        }

        // Wrap a glyph canvas in a Viewbox that scales uniformly to whatever space the
        // host button provides. We deliberately do NOT pin Width/Height here: a fixed size
        // larger than the button would overflow and clip (notably the tall vertical glyph).
        // A small margin keeps the glyph off the hover-highlight edges.
        private static Viewbox WrapGlyph(Canvas glyph)
            => new Viewbox { Stretch = Stretch.Uniform, Margin = new Thickness(2), Child = glyph };

        // ── Color resolution ─────────────────────────────────────────────────

        /// <summary>
        /// Resolves the structural (outline/fill) brush honoring the color mode.
        /// Enabled  -> green at full, red when critical, theme brush otherwise.
        /// TextColor -> always the theme brush.
        /// Disabled -> always the theme brush (no emphasis).
        /// </summary>
        private static Brush ResolveBatteryStructureBrush(Brush themeBrush, int level, BatteryColorMode mode)
        {
            if (level < 0 || mode != BatteryColorMode.Enabled)
                return themeBrush;
            if (level >= 100) return BatGreenFill;
            if (level <= 20) return BatRedFill;
            return themeBrush;
        }

        /// <summary>
        /// Brush for percentage text and outside bolts. Matches the structural color when
        /// Enabled so a critical battery reads red everywhere, otherwise the theme brush.
        /// </summary>
        private static Brush ResolveBatteryTextBrush(Brush themeBrush, int level, BatteryColorMode mode)
            => ResolveBatteryStructureBrush(themeBrush, level, mode);

        // ── Classic horizontal glyph ─────────────────────────────────────────

        private static Canvas BuildClassicGlyph(Brush themeBrush, int level, bool charging, BatteryRenderOptions opts, bool percentInside)
        {
            const double canvasW = 52;
            const double canvasH = 24;
            var canvas = new Canvas { Width = canvasW, Height = canvasH };

            Brush structureBrush = ResolveBatteryStructureBrush(themeBrush, level, opts.ColorMode);

            const double bodyX = 2;
            const double bodyY = 3;
            const double bodyW = 40;
            const double bodyH = 18;
            const double inset = 2.5;

            bool boltInside = opts.ShowBolt && charging && opts.BoltInside;

            if (level >= 0)
            {
                var emptyBg = new Rectangle
                {
                    Width = bodyW - inset * 2,
                    Height = bodyH - inset * 2,
                    Fill = BatLightGrey,
                    RadiusX = 2,
                    RadiusY = 2
                };
                Canvas.SetLeft(emptyBg, bodyX + inset);
                Canvas.SetTop(emptyBg, bodyY + inset);
                canvas.Children.Add(emptyBg);
            }

            if (level > 0)
            {
                double fillMaxW = bodyW - inset * 2;
                double fillW = fillMaxW * (Math.Min(level, 100) / 100.0);

                var fill = new Rectangle
                {
                    Width = fillW,
                    Height = bodyH - inset * 2,
                    Fill = structureBrush,
                    RadiusX = 2,
                    RadiusY = 2
                };
                Canvas.SetLeft(fill, bodyX + inset);
                Canvas.SetTop(fill, bodyY + inset);
                canvas.Children.Add(fill);
            }

            var body = new Rectangle
            {
                Width = bodyW,
                Height = bodyH,
                Stroke = structureBrush,
                StrokeThickness = 1.8,
                RadiusX = 3,
                RadiusY = 3,
                Fill = Brushes.Transparent
            };
            Canvas.SetLeft(body, bodyX);
            Canvas.SetTop(body, bodyY);
            canvas.Children.Add(body);

            var cap = new Rectangle
            {
                Width = 4,
                Height = 8,
                Fill = structureBrush,
                RadiusX = 1,
                RadiusY = 1
            };
            Canvas.SetLeft(cap, bodyX + bodyW + 0.5);
            Canvas.SetTop(cap, bodyY + (bodyH - 8) / 2.0);
            canvas.Children.Add(cap);

            if (boltInside)
            {
                double cx = bodyX + bodyW * 0.22;
                double cy = bodyY + bodyH / 2.0;
                canvas.Children.Add(BuildBoltPolygon(cx, cy, BatDarkGrey));
            }

            if (opts.ShowPercent && percentInside && level >= 0)
            {
                double textX = bodyX + (boltInside ? 10 : 0);
                double textW = bodyW - (boltInside ? 10 : 0);

                var text = new TextBlock
                {
                    Text = level + "%",
                    FontSize = 10,
                    FontWeight = FontWeights.Bold,
                    Foreground = BatDarkGrey,
                    TextAlignment = TextAlignment.Center,
                    Width = textW,
                    Height = bodyH,
                    Padding = new Thickness(0)
                };
                Canvas.SetLeft(text, textX);
                Canvas.SetTop(text, bodyY + 2);
                canvas.Children.Add(text);
            }

            return canvas;
        }

        // ── One UI 7 style solid pill ────────────────────────────────────────

        private static Canvas BuildPillGlyph(Brush themeBrush, int level, bool charging, BatteryRenderOptions opts, bool percentInside)
        {
            const double canvasW = 44;
            const double canvasH = 22;
            var canvas = new Canvas { Width = canvasW, Height = canvasH };

            Brush fillBrush = ResolveBatteryStructureBrush(themeBrush, level, opts.ColorMode);

            const double pillX = 2;
            const double pillY = 2;
            const double pillW = 40;
            const double pillH = 18;
            const double radius = pillH / 2.0;

            // Empty/background pill in grey. The proportional fill is layered on top, so the
            // remaining (uncharged) portion stays grey, matching the classic and vertical styles.
            var emptyPill = new Rectangle
            {
                Width = pillW,
                Height = pillH,
                Fill = BatLightGrey,
                RadiusX = radius,
                RadiusY = radius
            };
            Canvas.SetLeft(emptyPill, pillX);
            Canvas.SetTop(emptyPill, pillY);
            canvas.Children.Add(emptyPill);

            // Proportional fill. We draw a full-width rounded pill but clip it to the charged
            // width so the left cap stays nicely rounded and the right edge is a clean cut.
            if (level > 0)
            {
                double fillW = pillW * (Math.Min(level, 100) / 100.0);

                var fill = new Rectangle
                {
                    Width = pillW,
                    Height = pillH,
                    Fill = fillBrush,
                    RadiusX = radius,
                    RadiusY = radius,
                    Clip = new RectangleGeometry(new Rect(0, 0, fillW, pillH))
                };
                Canvas.SetLeft(fill, pillX);
                Canvas.SetTop(fill, pillY);
                canvas.Children.Add(fill);
            }

            // Content sits on top. Dark grey keeps it legible against both the grey empty
            // portion and the colored fill, matching the classic style's inside text.
            Brush contentBrush = BatDarkGrey;

            bool boltInside = opts.ShowBolt && charging && opts.BoltInside;

            if (boltInside)
            {
                double cx = pillX + pillW * (percentInside && opts.ShowPercent && level >= 0 ? 0.20 : 0.5);
                double cy = pillY + pillH / 2.0;
                canvas.Children.Add(BuildBoltPolygon(cx, cy, contentBrush));
            }

            if (opts.ShowPercent && percentInside && level >= 0)
            {
                double textX = pillX + (boltInside ? 10 : 0);
                double textW = pillW - (boltInside ? 10 : 0);

                // Wrap the text in a pill-tall Grid so it centers vertically regardless of
                // the font's line metrics; a bare TextBlock on a Canvas would top-align.
                var holder = new Grid { Width = textW, Height = pillH };
                holder.Children.Add(new TextBlock
                {
                    Text = level.ToString(),
                    FontSize = 11,
                    FontWeight = FontWeights.Bold,
                    Foreground = contentBrush,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Padding = new Thickness(0)
                });
                Canvas.SetLeft(holder, textX);
                Canvas.SetTop(holder, pillY);
                canvas.Children.Add(holder);
            }

            return canvas;
        }

        // ── Vertical glyph (Classic rotated; percentage always outside) ──────

        private static Canvas BuildVerticalGlyph(Brush themeBrush, int level, bool charging, BatteryRenderOptions opts)
        {
            const double canvasW = 26;
            const double canvasH = 52;
            var canvas = new Canvas { Width = canvasW, Height = canvasH };

            Brush structureBrush = ResolveBatteryStructureBrush(themeBrush, level, opts.ColorMode);

            const double bodyX = 3;
            const double bodyW = 20;
            const double bodyH = 44;
            const double bodyY = 6;   // leave room above for the terminal cap
            const double inset = 2.8;

            bool boltInside = opts.ShowBolt && charging && opts.BoltInside;

            // Empty light-grey interior
            if (level >= 0)
            {
                var emptyBg = new Rectangle
                {
                    Width = bodyW - inset * 2,
                    Height = bodyH - inset * 2,
                    Fill = BatLightGrey,
                    RadiusX = 2,
                    RadiusY = 2
                };
                Canvas.SetLeft(emptyBg, bodyX + inset);
                Canvas.SetTop(emptyBg, bodyY + inset);
                canvas.Children.Add(emptyBg);
            }

            // Proportional fill grows from the bottom up.
            if (level > 0)
            {
                double fillMaxH = bodyH - inset * 2;
                double fillH = fillMaxH * (Math.Min(level, 100) / 100.0);

                var fill = new Rectangle
                {
                    Width = bodyW - inset * 2,
                    Height = fillH,
                    Fill = structureBrush,
                    RadiusX = 2,
                    RadiusY = 2
                };
                Canvas.SetLeft(fill, bodyX + inset);
                Canvas.SetTop(fill, bodyY + (fillMaxH - fillH) + inset);
                canvas.Children.Add(fill);
            }

            var body = new Rectangle
            {
                Width = bodyW,
                Height = bodyH,
                Stroke = structureBrush,
                StrokeThickness = 1.8,
                RadiusX = 3,
                RadiusY = 3,
                Fill = Brushes.Transparent
            };
            Canvas.SetLeft(body, bodyX);
            Canvas.SetTop(body, bodyY);
            canvas.Children.Add(body);

            // Terminal cap on top
            var cap = new Rectangle
            {
                Width = 8,
                Height = 4,
                Fill = structureBrush,
                RadiusX = 1,
                RadiusY = 1
            };
            Canvas.SetLeft(cap, bodyX + (bodyW - 8) / 2.0);
            Canvas.SetTop(cap, bodyY - 3.5);
            canvas.Children.Add(cap);

            if (boltInside)
            {
                double cx = bodyX + bodyW / 2.0;
                double cy = bodyY + bodyH / 2.0;
                canvas.Children.Add(BuildBoltPolygon(cx, cy, BatDarkGrey));
            }

            return canvas;
        }

        // ── Shared bolt geometry ─────────────────────────────────────────────

        private static Polygon BuildBoltPolygon(double cx, double cy, Brush fill)
        {
            return new Polygon
            {
                Fill = fill,
                Stroke = Brushes.Transparent,
                StrokeThickness = 0,
                Points = new PointCollection
                {
                    new Point(cx + 0.5, cy - 7),
                    new Point(cx - 3.5, cy + 0.5),
                    new Point(cx - 0.5, cy + 0.5),
                    new Point(cx - 1.5, cy + 7),
                    new Point(cx + 3.5, cy - 1),
                    new Point(cx + 0.5, cy - 1)
                }
            };
        }

        // Standalone bolt on its own little canvas, for the "outside" placement.
        private static Canvas BuildBoltCanvas(Brush fill)
        {
            var canvas = new Canvas { Width = 9, Height = 16 };
            canvas.Children.Add(BuildBoltPolygon(4.5, 8, fill));
            return canvas;
        }
    }
}