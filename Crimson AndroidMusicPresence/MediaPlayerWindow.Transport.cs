using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Animation;

namespace musicpresense
{
    public partial class MediaPlayerWindow
    {
        private async void BtnPrevious_Click(object sender, RoutedEventArgs e)
        {
            try { await _previousAction().ConfigureAwait(true); } catch { }
        }
        private async void BtnPause_Click(object sender, RoutedEventArgs e)
        {
            try { await _pauseAction().ConfigureAwait(true); } catch { }
        }
        private async void BtnNext_Click(object sender, RoutedEventArgs e)
        {
            try { await _nextAction().ConfigureAwait(true); } catch { }
        }

        private void RefreshVolumeIcon()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(RefreshVolumeIcon);
                return;
            }

            var iconBrush = ResolveIconBrush();
            const double auxIconSize = 22;

            VolumeIconLevel level;
            if (_isScrcpyAudioAvailable?.Invoke() == true)
            {
                float v = _getVolume?.Invoke() ?? 1f;
                level = LevelFromVolume(v);
            }
            else
            {
                level = VolumeIconLevel.High;
            }

            BtnVolume.Content = BuildVolumeIcon(iconBrush, auxIconSize, level);
        }
        private void BtnVolume_Click(object sender, RoutedEventArgs e)
        {
            // Toggle behavior: clicking the volume icon while the popup is open closes it.
            if (VolumePopup.IsOpen)
            {
                VolumePopup.IsOpen = false;
                return;
            }

            // Pick the variant based on whether scrcpy's audio session is reachable.
            // If it is, we can read+write absolute volume so we show the slider.
            // If not, we fall back to step buttons that go through the same code
            // path the hotkey uses (scrcpy volume if it comes online, else ADB
            // keyevents to the device).
            bool sliderMode = _isScrcpyAudioAvailable?.Invoke() == true;

            if (sliderMode)
            {
                float current = _getVolume?.Invoke() ?? 0f;

                _suppressVolumeSliderEcho = true;
                try
                {
                    VolumeSlider.Value = Math.Clamp(current * 100f, 0, 100);
                }
                finally
                {
                    _suppressVolumeSliderEcho = false;
                }

                TxtVolumePercent.Text = $"{(int)Math.Round(VolumeSlider.Value)}%";
                VolumeSliderHost.Visibility = Visibility.Visible;
                VolumeStepHost.Visibility = Visibility.Collapsed;
            }
            else
            {
                _ = OpenPhoneVolumeSliderAsync();
                return;
            }

            VolumePopup.IsOpen = true;
            RefreshVolumeIcon();
        }

        private async Task OpenPhoneVolumeSliderAsync()
        {
            var (current, max) = _getPhoneVolume != null
                ? await _getPhoneVolume().ConfigureAwait(true)
                : (-1, 15);

            _lastPhoneVolumeMax = max > 0 ? max : 15;
            _lastSentPhoneVolumeIndex = current;

            _suppressVolumeSliderEcho = true;
            try
            {
                VolumeSlider.Value = current >= 0
                    ? Math.Clamp(current / (double)_lastPhoneVolumeMax * 100.0, 0, 100)
                    : 50;
            }
            finally
            {
                _suppressVolumeSliderEcho = false;
            }

            TxtVolumePercent.Text = $"{(int)Math.Round(current / (double)_lastPhoneVolumeMax * 100.0)}%";
            VolumeSliderHost.Visibility = Visibility.Visible;
            VolumeStepHost.Visibility = Visibility.Collapsed;
            VolumePopup.IsOpen = true;
            RefreshVolumeIcon();
        }
        private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressVolumeSliderEcho) return;

            if (_isScrcpyAudioAvailable?.Invoke() == true)
            {
                // Scrcpy audio: continuous 0..1 float.
                float volume = (float)Math.Clamp(e.NewValue / 100.0, 0.0, 1.0);
                _setVolume?.Invoke(volume);
                TxtVolumePercent.Text = $"{(int)Math.Round(e.NewValue)}%";
            }
            else
            {
                // Phone volume: only send ADB command when the mapped integer index changes.
                int newIndex = (int)Math.Round(e.NewValue / 100.0 * _lastPhoneVolumeMax);
                newIndex = Math.Clamp(newIndex, 0, _lastPhoneVolumeMax);
                if (newIndex != _lastSentPhoneVolumeIndex)
                {
                    int previousIndex = _lastSentPhoneVolumeIndex;
                    _lastSentPhoneVolumeIndex = newIndex;
                    _ = _setPhoneVolume?.Invoke(previousIndex, newIndex, _lastPhoneVolumeMax);
                }
                TxtVolumePercent.Text = $"{(int)Math.Round(_lastSentPhoneVolumeIndex / (double)_lastPhoneVolumeMax * 100.0)}%";
            }

            RefreshVolumeIcon();
        }
        private void BtnVolumeDown_Click(object sender, RoutedEventArgs e)
        {
            _stepVolume?.Invoke(false);
            RefreshVolumeIcon();
        }
        private void BtnVolumeUp_Click(object sender, RoutedEventArgs e)
        {
            _stepVolume?.Invoke(true);
            RefreshVolumeIcon();
        }

        private async void BtnSeekBack_Click(object sender, RoutedEventArgs e)
        {
            try { if (_seekRelativeSeconds != null) await _seekRelativeSeconds(-30).ConfigureAwait(true); } catch { }
        }
        private async void BtnSeekFwd_Click(object sender, RoutedEventArgs e)
        {
            try { if (_seekRelativeSeconds != null) await _seekRelativeSeconds(30).ConfigureAwait(true); } catch { }
        }

        private void ProgressSlider_PreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            // Seeking from Windows -> Android isn't supported (Android only
            // reports last scrub location, no arbitrary seek API over ADB).
            // Swallow the click so the thumb doesn't move, then flash a
            // small notice so the user understands why nothing happened.
            e.Handled = true;
            FlashSeekUnsupportedNotice();
        }
        private void FlashSeekUnsupportedNotice()
        {
            if (SeekUnsupportedNotice == null) return;

            // Reset any in-flight animation so repeated clicks restart cleanly.
            SeekUnsupportedNotice.BeginAnimation(UIElement.OpacityProperty, null);

            var fadeIn = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(120)));
            var fadeOut = new DoubleAnimation(1, 0, new Duration(TimeSpan.FromMilliseconds(400)))
            {
                BeginTime = TimeSpan.FromMilliseconds(1400)
            };

            var sb = new Storyboard();
            Storyboard.SetTarget(fadeIn, SeekUnsupportedNotice);
            Storyboard.SetTargetProperty(fadeIn, new PropertyPath(UIElement.OpacityProperty));
            Storyboard.SetTarget(fadeOut, SeekUnsupportedNotice);
            Storyboard.SetTargetProperty(fadeOut, new PropertyPath(UIElement.OpacityProperty));
            sb.Children.Add(fadeIn);
            sb.Children.Add(fadeOut);
            sb.Begin();
        }

        private void BtnPositionLabel_Click(object sender, RoutedEventArgs e)
        {
            _showTimeLeft = !_showTimeLeft;
            RefreshPositionLabel();
        }
        private void RefreshPositionLabel()
        {
            if (_showTimeLeft && _lastDurationMs > 0)
            {
                long left = _lastDurationMs - Math.Min(_lastPositionMs, _lastDurationMs);
                TxtPositionLabel.Text = "-" + FormatMs(left);
            }
            else
            {
                TxtPositionLabel.Text = FormatMs(_lastPositionMs);
            }
        }
        private static string FormatMs(long ms)
        {
            var t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
            return t.TotalHours >= 1
                ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
                : $"{t.Minutes}:{t.Seconds:00}";
        }


    }
}