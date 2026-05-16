using System;
using System.Collections.Generic;
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

            var icon = BuildAudioQualityIcon(brush, 16);
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
        }
        private void BtnAudioQuality_Click(object sender, RoutedEventArgs e)
        {
            if (AudioQualityPopup == null || AudioQualityMenuItems == null)
                return;

            Debugger.show("[MEDIAPLAYER] Audio quality pill pressed.");
            BuildAudioQualityMenu();
            AudioQualityPopup.IsOpen = !AudioQualityPopup.IsOpen;
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
            var brush = ResolveIconBrush();
            BtnAudioLink.Content = BuildAudioLinkIcon(brush, _audioLinkActive, 22);
            BtnAudioLink.ToolTip = _audioLinkActive ? "Audio link: sync audio from device (on)" : "Audio link: sync audio from device (off)";
        }
    }
}