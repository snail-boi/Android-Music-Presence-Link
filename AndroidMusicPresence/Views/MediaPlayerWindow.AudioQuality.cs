using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace AndroidMusicPresenceLink
{
    public partial class MediaPlayerWindow
    {
        // ── Audio Quality Preset ──────────────────────────────────────────────

        /// <summary>
        /// Called by the host whenever the config might have changed (e.g. after the
        /// settings window saves). Re-renders the quick quality button label.
        /// </summary>
        public void RefreshAudioQualityButton()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(RefreshAudioQualityButton);
                return;
            }

            // Defer until the templated controls actually exist.
            if (BtnAudioQuality == null || AudioQualityContent == null)
                return;

            var config = _getConfig?.Invoke();
            string label;
            if (config == null)
            {
                label = AudioQualityPresets.CustomLabel;
            }
            else
            {
                label = AudioQualityPresets.GetShortLabelForConfig(config);
            }

            var brush = ResolveIconBrush();
            AudioQualityContent.Children.Clear();

            var icon = BuildAudioQualityIconForPreset(brush, config, 16);
            AudioQualityContent.Children.Add(icon);

            var text = new TextBlock
            {
                Text = label,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0),
                Foreground = brush
            };
            AudioQualityContent.Children.Add(text);

            BtnAudioQuality.ToolTip = config == null
                ? "Audio quality preset"
                : $"Audio quality: {label}. Click to change.";

            // Subtle border so the pill reads as a button. Match the icon brush so
            // it stays legible over both cover-art gradients and the idle theme.
            var borderColor = (brush is SolidColorBrush scb) ? scb.Color : Colors.White;
            BtnAudioQuality.BorderBrush = new SolidColorBrush(borderColor) { Opacity = 0.45 };
            ApplyPillMode(BtnAudioQuality, App.Config?.MediaPlayer.PillModeQuality ?? 0);
        }
        private void BtnAudioQuality_Click(object sender, RoutedEventArgs e)
        {
            if (AudioQualityPopup == null || AudioQualityMenuItems == null)
                return;

            Debugger.show("[MEDIAPLAYER] Audio quality pill pressed.");
            BuildAudioQualityMenu();
            AudioQualityPopup.IsOpen = !AudioQualityPopup.IsOpen;
        }

        private static Viewbox BuildAudioQualityIconForPreset(Brush brush, MusicConfig? config, double size = 18)
        {
            var preset = config == null ? null : AudioQualityPresets.MatchFromConfig(config);
            var presetName = preset?.ShortName ?? AudioQualityPresets.CustomLabel;

            return presetName switch
            {
                "Data Saver" => BuildLeafIcon(brush, size),
                "Medium" => BuildWaterDropIcon(brush, size),
                "High" => BuildSparkleIcon(brush, size),
                "Lossless" => BuildLosslessWavesIcon(brush, size),
                "Max" => BuildDiamondIcon(brush, size),
                _ => BuildAudioQualityIcon(brush, size),
            };
        }

        private static Viewbox BuildLeafIcon(Brush brush, double size = 18)
        {
            var canvas = new Canvas { Width = 20, Height = 20 };
            canvas.Children.Add(new Path
            {
                Stroke = brush,
                StrokeThickness = 1.7,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                Fill = Brushes.Transparent,
                Data = Geometry.Parse("M 4,11 C 4,5.5 8.5,3 14,4 C 13,8 10.5,12.5 6.5,15.5 C 4.9,16.7 3.6,16.2 3,14.8 C 2.4,13.3 2.8,12 4,11 Z")
            });
            canvas.Children.Add(new Path
            {
                Stroke = brush,
                StrokeThickness = 1.4,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Data = Geometry.Parse("M 5.2,14.2 C 7.4,12.6 9.8,10.6 12.7,5.8")
            });
            return new Viewbox { Width = size, Height = size, Child = canvas };
        }

        private static Viewbox BuildWaterDropIcon(Brush brush, double size = 18)
        {
            var canvas = new Canvas { Width = 20, Height = 20 };
            canvas.Children.Add(new Path
            {
                Fill = brush,
                Stroke = brush,
                StrokeThickness = 1.2,
                StrokeLineJoin = PenLineJoin.Round,
                Data = Geometry.Parse("M 10,2 C 10,2 4.5,8 4.5,12.2 C 4.5,16 7.1,18 10,18 C 12.9,18 15.5,16 15.5,12.2 C 15.5,8 10,2 10,2 Z")
            });
            return new Viewbox { Width = size, Height = size, Child = canvas };
        }

        private static Viewbox BuildSparkleIcon(Brush brush, double size = 18)
        {
            var canvas = new Canvas { Width = 20, Height = 20 };
            canvas.Children.Add(new Path
            {
                Fill = brush,
                Data = Geometry.Parse("M 10,2 L 12,7.8 L 18,10 L 12,12.2 L 10,18 L 8,12.2 L 2,10 L 8,7.8 Z")
            });
            canvas.Children.Add(new Path
            {
                Stroke = brush,
                StrokeThickness = 1.4,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Data = Geometry.Parse("M 10,4.8 L 10,15.2 M 4.8,10 L 15.2,10")
            });
            return new Viewbox { Width = size, Height = size, Child = canvas };
        }

        private static Viewbox BuildLosslessWavesIcon(Brush brush, double size = 18)
        {
            var canvas = new Canvas { Width = 20, Height = 20 };

            void AddWave(double startY)
            {
                var wave = new Path
                {
                    Stroke = brush,
                    StrokeThickness = 1.8,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    Fill = Brushes.Transparent
                };

                var geometry = new PathGeometry();
                var figure = new PathFigure
                {
                    StartPoint = new Point(3, startY + 2)
                };
                figure.Segments.Add(new BezierSegment(new Point(6.2, startY), new Point(9.8, startY + 4), new Point(13, startY + 2), true));
                geometry.Figures.Add(figure);
                wave.Data = geometry;
                canvas.Children.Add(wave);
            }

            AddWave(3);
            AddWave(8);
            AddWave(13);
            return new Viewbox { Width = size, Height = size, Child = canvas };
        }

        private static Viewbox BuildDiamondIcon(Brush brush, double size = 18)
        {
            // Path 1: The outer frame/border from your SVG
            var geometry1 = Geometry.Parse(
                "M 1250.839844 1745.382812 L 1771.550781 1018.660156 L 1524.460938 754.941406 L 977.214844 754.941406 L 730.132812 1018.660156 Z " +
                "M 1250.839844 1672.75 L 1716.941406 1022.238281 L 1506.128906 797.238281 L 995.554688 797.238281 L 784.742188 1022.238281 L 1250.839844 1672.75"
            );

            // Path 2: The internal facets from your SVG
            var geometry2 = Geometry.Parse(
                "M 1215.230469 1695.683594 L 1034.039062 1039.808594 L 745.285156 1039.808594 L 730.132812 1018.660156 L 749.949219 997.5 L 1022.351562 997.5 L 960.320312 772.96875 L 977.214844 754.941406 L 999.09375 754.941406 L 1059.058594 972 L 1224.308594 762.398438 L 1277.371094 762.398438 L 1442.621094 972 L 1502.589844 754.941406 L 1524.460938 754.941406 L 1541.359375 772.96875 L 1479.328125 997.5 L 1751.730469 997.5 L 1771.550781 1018.660156 L 1756.390625 1039.808594 L 1467.640625 1039.808594 L 1286.449219 1695.683594 L 1250.839844 1745.382812 Z " +
                "M 1092.699219 997.5 L 1408.980469 997.5 L 1250.839844 797.238281 Z " +
                "M 1077.789062 1039.808594 L 1250.839844 1666.191406 L 1423.890625 1039.808594 L 1077.789062 1039.808594"
            );

            // Combine bounds to calculate perfect centering and scaling
            var bounds = Rect.Union(geometry1.Bounds, geometry2.Bounds);

            var transform = new TransformGroup();
            transform.Children.Add(new TranslateTransform(-bounds.X, -bounds.Y));
            transform.Children.Add(new ScaleTransform(size / bounds.Width, size / bounds.Height));

            var grid = new Grid();

            // Outer Frame
            grid.Children.Add(new Path
            {
                Data = geometry1,
                Fill = brush,
                Stroke = brush,
                StrokeThickness = 0.6,
                RenderTransform = transform,
                Stretch = Stretch.None
            });

            // Inner Facets
            grid.Children.Add(new Path
            {
                Data = geometry2,
                Fill = brush,
                Stroke = brush,
                StrokeThickness = 0.6,
                RenderTransform = transform,
                Stretch = Stretch.None
            });

            return new Viewbox
            {
                Width = size,
                Height = size,
                Stretch = Stretch.None,
                Child = grid
            };
        }
        private void BuildAudioQualityMenu()
        {
            AudioQualityMenuItems.Children.Clear();

            var config = _getConfig?.Invoke();
            var currentMatch = config != null ? AudioQualityPresets.MatchFromConfig(config) : null;
            bool isCustom = config != null && currentMatch == null;

            // Pop-up background follows the theme, so the text foreground must too.
            // The pill button on the player pane uses _hasSong-driven brushes (which
            // are forced white over the cover gradient), but inside this popup the
            // backdrop is the regular ThemeControlBackgroundBrush, so we want the
            // matching ThemeControlForegroundBrush.
            var fg = ResolveMenuForegroundBrush();

            // Header
            var header = new TextBlock
            {
                Text = "Audio quality preset",
                FontWeight = FontWeights.SemiBold,
                FontSize = 12,
                Margin = new Thickness(8, 4, 8, 6),
                Opacity = 0.85,
                Foreground = fg
            };
            AudioQualityMenuItems.Children.Add(header);

            foreach (var preset in AudioQualityPresets.All)
            {
                bool isSelected = currentMatch != null
                    && currentMatch.Name.Equals(preset.Name, StringComparison.OrdinalIgnoreCase);
                AudioQualityMenuItems.Children.Add(BuildPresetMenuRow(preset, isSelected, fg));
            }

            // If the saved values don't match any preset, show a (selected, disabled)
            // "Custom" row so the user understands why nothing else is highlighted.
            if (isCustom)
            {
                var separator = new Border
                {
                    Height = 1,
                    Background = (Brush)FindResource("ThemeControlBorderBrush"),
                    Opacity = 0.4,
                    Margin = new Thickness(8, 4, 8, 4)
                };
                AudioQualityMenuItems.Children.Add(separator);

                var customRow = new Border
                {
                    Background = Brushes.Transparent,
                    Padding = new Thickness(10, 8, 10, 8),
                    CornerRadius = new CornerRadius(4),
                    Margin = new Thickness(0, 1, 0, 1)
                };
                var stack = new StackPanel { Orientation = Orientation.Vertical };
                stack.Children.Add(new TextBlock
                {
                    Text = "● Custom",
                    FontWeight = FontWeights.SemiBold,
                    FontSize = 13,
                    Foreground = fg
                });
                stack.Children.Add(new TextBlock
                {
                    Text = "Your settings don't match any preset. Edit them in Settings.",
                    FontSize = 11,
                    Opacity = 0.7,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 2, 0, 0),
                    Foreground = fg
                });
                customRow.Child = stack;
                AudioQualityMenuItems.Children.Add(customRow);
            }

            // Separator before "Custom settings..." entry.
            AudioQualityMenuItems.Children.Add(new Border
            {
                Height = 1,
                Background = (Brush)FindResource("ThemeControlBorderBrush"),
                Opacity = 0.4,
                Margin = new Thickness(8, 4, 8, 4)
            });

            // "Custom settings..." — always at the bottom, opens the custom quality window.
            var customSettingsBtn = new Button
            {
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(10, 8, 10, 8),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Cursor = Cursors.Hand,
                Margin = new Thickness(0, 1, 0, 1),
                Foreground = fg
            };
            var customSettingsStack = new StackPanel { Orientation = Orientation.Vertical };
            customSettingsStack.Children.Add(new TextBlock
            {
                Text = "Custom settings...",
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = fg
            });
            customSettingsStack.Children.Add(new TextBlock
            {
                Text = "Manually set codec, bitrate and buffer.",
                FontSize = 11,
                Opacity = 0.7,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0),
                Foreground = fg
            });
            customSettingsBtn.Content = customSettingsStack;
            customSettingsBtn.Click += (s, e) =>
            {
                AudioQualityPopup.IsOpen = false;
                _openCustomQualityWindow?.Invoke();
            };
            AudioQualityMenuItems.Children.Add(customSettingsBtn);
        }
        private UIElement BuildPresetMenuRow(AudioQualityPresets.Preset preset, bool isSelected, Brush foreground)
        {
            // We use a Button so we get hover/press states + a click event for free.
            var btn = new Button
            {
                Background = isSelected
                    ? new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF))
                    : Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(10, 8, 10, 8),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Cursor = Cursors.Hand,
                Margin = new Thickness(0, 1, 0, 1),
                Tag = preset,
                Foreground = foreground
            };

            // Custom rounded template so the row feels like a menu item, not a chunky button.
            var template = new ControlTemplate(typeof(Button));
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
            border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Button.PaddingProperty));
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
            border.AppendChild(presenter);
            template.VisualTree = border;

            // Hover trigger
            var hoverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hoverTrigger.Setters.Add(new Setter(Button.BackgroundProperty,
                isSelected
                    ? (Brush)new SolidColorBrush(Color.FromArgb(0x44, 0xFF, 0xFF, 0xFF))
                    : (Brush)new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF))));
            template.Triggers.Add(hoverTrigger);
            btn.Template = template;

            var stack = new StackPanel { Orientation = Orientation.Vertical };
            var titleLine = new StackPanel { Orientation = Orientation.Horizontal };

            // Selection mark dot
            titleLine.Children.Add(new TextBlock
            {
                Text = isSelected ? "● " : "  ",
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0),
                Opacity = isSelected ? 1.0 : 0.0,
                Foreground = foreground
            });
            titleLine.Children.Add(new TextBlock
            {
                Text = preset.ShortName,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = foreground
            });
            stack.Children.Add(titleLine);

            stack.Children.Add(new TextBlock
            {
                Text = preset.Description,
                FontSize = 11,
                Opacity = 0.7,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0),
                Foreground = foreground
            });

            btn.Content = stack;
            btn.Click += AudioQualityPresetItem_Click;
            return btn;
        }
        private void AudioQualityPresetItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn) return;
            if (btn.Tag is not AudioQualityPresets.Preset preset) return;

            try
            {
                _applyAudioQualityPreset?.Invoke(preset);
            }
            catch (Exception ex)
            {
                Debugger.show("Apply audio quality preset failed: " + ex.Message);
            }

            AudioQualityPopup.IsOpen = false;
            RefreshAudioQualityButton();
        }

        // ── Audio Quality: hotkey entry point ────────────────────────────────

        /// <summary>
        /// Opens the audio quality popup as if the user had clicked the pill button.
        /// Called by the global hotkey path in App when the media player is visible.
        /// Shows all presets plus the "Custom settings..." entry.
        /// </summary>
        public void OpenAudioQualityPopupFromHotkey()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(OpenAudioQualityPopupFromHotkey);
                return;
            }

            if (AudioQualityPopup == null || AudioQualityMenuItems == null)
                return;

            Debugger.show("[HOTKEY] Opening audio quality popup from hotkey.");
            BuildAudioQualityMenu();
            AudioQualityPopup.IsOpen = true;
        }

        // ── Audio Link ────────────────────────────────────────────────────────
        private void BtnAudioLink_Click(object sender, RoutedEventArgs e)
        {
            _audioLinkActive = !_audioLinkActive;
            Debugger.show($"[MEDIAPLAYER] Audio link button pressed. New state: {_audioLinkActive}.");
            _setAudioLink?.Invoke(_audioLinkActive);
            RenderAudioLinkButton();
        }

        /// <summary>
        /// Called by the host to keep the audio-link button in sync when
        /// scrcpy is started or stopped from somewhere else (e.g. the tray menu).
        /// Does NOT invoke the _setAudioLink callback.
        /// </summary>
        public void SetAudioLinkState(bool active)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => SetAudioLinkState(active));
                return;
            }
            if (_audioLinkActive == active) return;
            _audioLinkActive = active;
            RenderAudioLinkButton();
        }
        private void RenderAudioLinkButton()
        {
            if (BtnAudioLink == null || AudioLinkContent == null) return;

            var brush = ResolveIconBrush();
            AudioLinkContent.Children.Clear();

            AudioLinkContent.Children.Add(BuildAudioLinkIcon(brush, _audioLinkActive, 16));
            AudioLinkContent.Children.Add(new TextBlock
            {
                Text = _audioLinkActive ? "Audio on" : "Audio off",
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0),
                Foreground = brush
            });

            BtnAudioLink.ToolTip = _audioLinkActive
                ? "Audio link: sync audio from device (on)"
                : "Audio link: sync audio from device (off)";

            var borderColor = (brush is SolidColorBrush scb) ? scb.Color : Colors.White;
            BtnAudioLink.BorderBrush = new SolidColorBrush(borderColor)
            {
                Opacity = _audioLinkActive ? 0.85 : 0.45
            };

            ApplyPillMode(BtnAudioLink, App.Config?.MediaPlayer.PillModeAudioLink ?? 0);
        }
    }
}