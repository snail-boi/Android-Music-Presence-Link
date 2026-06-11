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
        // 5 minutes between polls. The user explicitly asked for a long interval
        // to keep ADB chatter minimal; the icon is purely informational.
        private static readonly TimeSpan BatteryPollInterval = TimeSpan.FromMinutes(2.5);

        private DispatcherTimer? _batteryTimer;

        // Last known values, kept so we can re-render on theme/song-state changes
        // without having to hit ADB again.
        private int _batteryLevel = -1;       // -1 = unknown yet
        private bool _batteryCharging = false;

        /// <summary>
        /// Wires up the 5-minute battery poll and triggers an immediate first read
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
        /// Re-renders the battery glyph using the last polled values. Safe to call
        /// before the first poll completes; in that case it renders a placeholder.
        /// </summary>
        private void RenderBatteryButtonIcon()
        {
            if (BtnBattery == null) return;

            if (_batteryLevel < 0)
            {
                // No data yet. Render a faint outline so the slot isn't visually empty.
                BtnBattery.Content = BuildBatteryIcon(ResolveIconBrush(), level: -1, charging: false);
                BtnBattery.ToolTip = "Battery: reading...";
                return;
            }

            BtnBattery.Content = BuildBatteryIcon(ResolveIconBrush(), _batteryLevel, _batteryCharging);
            BtnBattery.ToolTip = _batteryCharging
                ? $"Battery: {_batteryLevel}% (charging)"
                : $"Battery: {_batteryLevel}%";
        }

        /// <summary>
        /// Horizontal battery glyph with the percentage rendered inside the body and an
        /// optional lightning-bolt overlay when charging.
        /// <para/>
        /// Color rules:
        /// <list type="bullet">
        ///   <item>Outline + cap: adaptive (white in dark mode, dark in light mode), or green/red overrides</item>
        ///   <item>Fill (charge bar): white normally, green at 100%, red at 20% or below</item>
        ///   <item>Empty portion behind the fill: light grey so text/bolt stay legible</item>
        ///   <item>Text + bolt: always dark grey for contrast against both the white fill and the light grey empty area</item>
        /// </list>
        /// </summary>
        private static Viewbox BuildBatteryIcon(Brush themeBrush, int level, bool charging)
        {
            const double canvasW = 52;
            const double canvasH = 24;

            var canvas = new Canvas { Width = canvasW, Height = canvasH };

            // Fixed palette. These don't change with theme; only the outline does.
            Brush whiteFill = Brushes.White;
            Brush greenFill = new SolidColorBrush(Color.FromRgb(0x34, 0xC7, 0x59));
            Brush redFill = new SolidColorBrush(Color.FromRgb(0xFF, 0x3B, 0x30));
            Brush lightGrey = new SolidColorBrush(Color.FromRgb(0xD0, 0xD0, 0xD0));
            Brush darkGrey = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));

            // Decide outline/cap and fill colors based on level
            Brush outlineBrush;
            Brush fillBrush;
            if (level < 0)
            {
                // Unknown state, render as a faint outline with no fill content
                outlineBrush = themeBrush;
                fillBrush = whiteFill;
            }
            else if (level >= 100)
            {
                // Full: whole icon goes green for clear at-a-glance readout
                outlineBrush = greenFill;
                fillBrush = greenFill;
            }
            else if (level <= 20)
            {
                // Critical: whole icon goes red
                outlineBrush = redFill;
                fillBrush = redFill;
            }
            else
            {
                outlineBrush = themeBrush;
                fillBrush = whiteFill;
            }

            const double bodyX = 2;
            const double bodyY = 3;
            const double bodyW = 40;
            const double bodyH = 18;
            const double inset = 2.5;

            // Light grey "empty" background. Sits inside the outline so the text and
            // bolt have a legible surface to land on, regardless of how full the
            // proportional fill bar is. Rendered first so everything else stacks on top.
            if (level >= 0)
            {
                var emptyBg = new Rectangle
                {
                    Width = bodyW - inset * 2,
                    Height = bodyH - inset * 2,
                    Fill = lightGrey,
                    RadiusX = 2,
                    RadiusY = 2
                };
                Canvas.SetLeft(emptyBg, bodyX + inset);
                Canvas.SetTop(emptyBg, bodyY + inset);
                canvas.Children.Add(emptyBg);
            }

            // Proportional fill, overlaid on the light-grey bg
            if (level > 0)
            {
                double fillMaxW = bodyW - inset * 2;
                double fillW = fillMaxW * (Math.Min(level, 100) / 100.0);

                var fill = new Rectangle
                {
                    Width = fillW,
                    Height = bodyH - inset * 2,
                    Fill = fillBrush,
                    RadiusX = 2,
                    RadiusY = 2
                };
                Canvas.SetLeft(fill, bodyX + inset);
                Canvas.SetTop(fill, bodyY + inset);
                canvas.Children.Add(fill);
            }

            // Body outline, drawn after the fills so its stroke sits on top cleanly
            var body = new Rectangle
            {
                Width = bodyW,
                Height = bodyH,
                Stroke = outlineBrush,
                StrokeThickness = 1.8,
                RadiusX = 3,
                RadiusY = 3,
                Fill = Brushes.Transparent
            };
            Canvas.SetLeft(body, bodyX);
            Canvas.SetTop(body, bodyY);
            canvas.Children.Add(body);

            // Positive terminal cap
            var cap = new Rectangle
            {
                Width = 4,
                Height = 8,
                Fill = outlineBrush,
                RadiusX = 1,
                RadiusY = 1
            };
            Canvas.SetLeft(cap, bodyX + bodyW + 0.5);
            Canvas.SetTop(cap, bodyY + (bodyH - 8) / 2.0);
            canvas.Children.Add(cap);

            // Lightning bolt, sits on the left portion of the body
            if (charging)
            {
                double cx = bodyX + bodyW * 0.22;
                double cy = bodyY + bodyH / 2.0;

                var bolt = new Polygon
                {
                    Fill = darkGrey,
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
                canvas.Children.Add(bolt);
            }

            // Percentage text, nudged right when the bolt is visible so they don't overlap
            if (level >= 0)
            {
                double textX = bodyX + (charging ? 10 : 0);
                double textW = bodyW - (charging ? 10 : 0);

                var text = new TextBlock
                {
                    Text = level + "%",
                    FontSize = 10,
                    FontWeight = FontWeights.Bold,
                    Foreground = darkGrey,
                    TextAlignment = TextAlignment.Center,
                    Width = textW,
                    Height = bodyH,
                    Padding = new Thickness(0)
                };
                Canvas.SetLeft(text, textX);
                Canvas.SetTop(text, bodyY + 2);
                canvas.Children.Add(text);
            }

            return new Viewbox { Width = canvasW * 1.1, Height = canvasH * 1.1, Child = canvas };
        }
    }
}