using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace musicpresense
{
    public partial class MediaPlayerWindow
    {

        // Legacy single-image setter kept in case called directly
        private void SetCoverImage(string? path) => FadeCoverImage(path);

        private void FadeCoverImage(string? path)
        {
            BitmapImage? bitmap = null;

            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                try
                {
                    bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.UriSource = new Uri(path, UriKind.Absolute);
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.EndInit();
                    bitmap.Freeze();
                }
                catch
                {
                    bitmap = null;
                }
            }

            // Determine incoming / outgoing layers
            var incoming = _coverUseLayerA ? ImgCoverA : ImgCoverB;
            var outgoing = _coverUseLayerA ? ImgCoverB : ImgCoverA;

            // Snap directly on first paint
            if (outgoing.Source == null && incoming.Source == null)
            {
                incoming.Source = bitmap;
                incoming.Opacity = 1;
                outgoing.Opacity = 0;
                _coverUseLayerA = !_coverUseLayerA;
                return;
            }

            incoming.BeginAnimation(UIElement.OpacityProperty, null);
            outgoing.BeginAnimation(UIElement.OpacityProperty, null);
            incoming.Source = bitmap;
            incoming.Opacity = 0;

            var fadeIn = new DoubleAnimation(0, 1, CoverFadeDuration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } };
            var fadeOut = new DoubleAnimation(1, 0, CoverFadeDuration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } };

            incoming.BeginAnimation(UIElement.OpacityProperty, fadeIn);
            outgoing.BeginAnimation(UIElement.OpacityProperty, fadeOut);

            _coverUseLayerA = !_coverUseLayerA;
        }
        private void ApplyCoverGradientBackground(string? imagePath)
        {
            // When idle (no path) the brush depends on the active theme, not the path,
            // so the path-equality cache would wrongly skip a rebuild on theme flip.
            // Only honor the cache when we actually have a cover image.
            bool hasImage = !string.IsNullOrWhiteSpace(imagePath) && File.Exists(imagePath);
            if (hasImage && string.Equals(_lastGradientSourcePath, imagePath, StringComparison.OrdinalIgnoreCase))
                return;

            // Idle-to-idle with the same theme: the solid brush is identical to what's
            // already on screen, so a crossfade just produces a visible pulse. Skip it.
            // We still need to update _lastIdleIsDark so a later theme flip is detected.
            bool isDarkNow = IsDarkThemeActive();
            if (!hasImage && _lastGradientSourcePath == null && _lastIdleIsDark == isDarkNow && GradientLayerA.Fill != null)
            {
                return;
            }

            _lastGradientSourcePath = imagePath;
            if (!hasImage)
            {
                _lastIdleIsDark = isDarkNow;
            }

            Brush newBrush;
            if (!hasImage)
            {
                // No song playing: follow the active theme. Dark mode stays near-black,
                // light mode goes near-white so it matches the rest of the UI.
                // Read from live resources rather than App.Config because the dark-mode
                // toggle calls ApplyTheme without updating Config.UseDarkMode.
                var solid = new SolidColorBrush(isDarkNow
                    ? Color.FromRgb(22, 22, 22)
                    : Color.FromRgb(247, 247, 247));
                solid.Freeze();
                newBrush = solid;
            }
            else
            {
                var colors = ExtractGradientColors(imagePath);
                newBrush = BuildFourToneCornerBrush(colors.topLeft, colors.topRight, colors.bottomLeft, colors.bottomRight);
            }

            // First call (initial paint): fill layer A immediately, no fade.
            if (GradientLayerA.Fill == null)
            {
                GradientLayerA.Fill = newBrush;
                GradientLayerA.Opacity = 1;
                GradientLayerB.Opacity = 0;
                _useLayerA = true;
                return;
            }

            var incoming = _useLayerA ? GradientLayerB : GradientLayerA;
            var outgoing = _useLayerA ? GradientLayerA : GradientLayerB;

            incoming.BeginAnimation(UIElement.OpacityProperty, null);
            incoming.Fill = newBrush;
            incoming.Opacity = 0;

            var fadeIn = new DoubleAnimation(0, 1, GradientFadeDuration)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
            };
            var fadeOut = new DoubleAnimation(1, 0, GradientFadeDuration)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
            };

            incoming.BeginAnimation(UIElement.OpacityProperty, fadeIn);
            outgoing.BeginAnimation(UIElement.OpacityProperty, fadeOut);

            _useLayerA = !_useLayerA;
        }
        private static Brush BuildFourToneCornerBrush(Color topLeft, Color topRight, Color bottomLeft, Color bottomRight)
        {
            var bitmap = new WriteableBitmap(2, 2, 96, 96, PixelFormats.Bgra32, null);

            // Row-major BGRA pixels: [top-left, top-right, bottom-left, bottom-right]
            byte[] pixels =
            {
                topLeft.B, topLeft.G, topLeft.R, 255,
                topRight.B, topRight.G, topRight.R, 255,
                bottomLeft.B, bottomLeft.G, bottomLeft.R, 255,
                bottomRight.B, bottomRight.G, bottomRight.R, 255,
            };

            bitmap.WritePixels(new Int32Rect(0, 0, 2, 2), pixels, 8, 0);
            bitmap.Freeze();

            var brush = new ImageBrush(bitmap)
            {
                Stretch = Stretch.Fill
            };
            brush.Freeze();
            return brush;
        }
        private static (Color topLeft, Color topRight, Color bottomLeft, Color bottomRight) ExtractGradientColors(string? imagePath)
        {
            if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
                return (DefaultTopLeft, DefaultTopRight, DefaultBottomLeft, DefaultBottomRight);

            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(imagePath, UriKind.Absolute);
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = 64;
                bitmap.DecodePixelHeight = 64;
                bitmap.EndInit();
                bitmap.Freeze();

                var formatted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
                formatted.Freeze();

                int width = formatted.PixelWidth;
                int height = formatted.PixelHeight;
                if (width <= 0 || height <= 0)
                    return (DefaultTopLeft, DefaultTopRight, DefaultBottomLeft, DefaultBottomRight);

                int stride = width * 4;
                var pixels = new byte[stride * height];
                formatted.CopyPixels(pixels, stride, 0);

                long tlR = 0, tlG = 0, tlB = 0, tlCount = 0;
                long trR = 0, trG = 0, trB = 0, trCount = 0;
                long blR = 0, blG = 0, blB = 0, blCount = 0;
                long brR = 0, brG = 0, brB = 0, brCount = 0;

                for (int y = 0; y < height; y++)
                {
                    int rowStart = y * stride;
                    bool isTop = y < (height / 2);

                    for (int x = 0; x < width; x++)
                    {
                        int i = rowStart + (x * 4);
                        byte b = pixels[i];
                        byte g = pixels[i + 1];
                        byte r = pixels[i + 2];
                        byte a = pixels[i + 3];

                        if (a < 24)
                            continue;

                        int max = Math.Max(r, Math.Max(g, b));
                        int min = Math.Min(r, Math.Min(g, b));
                        int saturation = max - min;

                        if (max < 20 || saturation < 8)
                            continue;

                        bool isLeft = x < (width / 2);
                        if (isTop && isLeft)
                        {
                            tlR += r; tlG += g; tlB += b; tlCount++;
                        }
                        else if (isTop)
                        {
                            trR += r; trG += g; trB += b; trCount++;
                        }
                        else if (isLeft)
                        {
                            blR += r; blG += g; blB += b; blCount++;
                        }
                        else
                        {
                            brR += r; brG += g; brB += b; brCount++;
                        }
                    }
                }

                if (tlCount == 0 && trCount == 0 && blCount == 0 && brCount == 0)
                    return (DefaultTopLeft, DefaultTopRight, DefaultBottomLeft, DefaultBottomRight);

                Color Avg(long r, long g, long b, long count, Color fallback)
                {
                    if (count <= 0) return fallback;
                    return Color.FromRgb((byte)(r / count), (byte)(g / count), (byte)(b / count));
                }

                var tl = Avg(tlR, tlG, tlB, tlCount, DefaultTopLeft);
                var tr = Avg(trR, trG, trB, trCount, DefaultTopRight);
                var bl = Avg(blR, blG, blB, blCount, DefaultBottomLeft);
                var br = Avg(brR, brG, brB, brCount, DefaultBottomRight);

                tl = BlendWith(tl, Color.FromRgb(255, 255, 255), 0.06);
                tr = BlendWith(tr, Color.FromRgb(255, 255, 255), 0.06);
                bl = BlendWith(bl, Color.FromRgb(0, 0, 0), 0.25);
                br = BlendWith(br, Color.FromRgb(0, 0, 0), 0.25);

                return (tl, tr, bl, br);
            }
            catch
            {
                return (DefaultTopLeft, DefaultTopRight, DefaultBottomLeft, DefaultBottomRight);
            }
        }
        private static Color BlendWith(Color source, Color mix, double ratio)
        {
            ratio = Math.Clamp(ratio, 0, 1);
            double inverse = 1 - ratio;

            byte r = (byte)Math.Clamp((source.R * inverse) + (mix.R * ratio), 0, 255);
            byte g = (byte)Math.Clamp((source.G * inverse) + (mix.G * ratio), 0, 255);
            byte b = (byte)Math.Clamp((source.B * inverse) + (mix.B * ratio), 0, 255);
            return Color.FromRgb(r, g, b);
        }
        private void SaveCoverMenuItem_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_currentCoverPath) || !File.Exists(_currentCoverPath))
                {
                    MessageBox.Show(this, "No cover image is available to save right now.", "Save Cover", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var extension = System.IO.Path.GetExtension(_currentCoverPath);
                if (string.IsNullOrWhiteSpace(extension)) extension = ".png";

                var dialog = new SaveFileDialog
                {
                    Title = "Save Cover Image",
                    FileName = "cover" + extension,
                    Filter = "Image Files|*.png;*.jpg;*.jpeg;*.bmp;*.webp|All Files|*.*",
                    DefaultExt = extension
                };

                if (dialog.ShowDialog(this) == true)
                {
                    File.Copy(_currentCoverPath, dialog.FileName, overwrite: true);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Failed to save cover image: " + ex.Message, "Save Cover", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        private void CopyCoverInfoMenuItem_Click(object sender, RoutedEventArgs e)
        {
            string title = TxtTitle.Text == "-" ? "" : TxtTitle.Text;
            string artist = TxtArtist.Text == "-" ? "" : TxtArtist.Text;
            string album = TxtAlbum.Text == "-" ? "" : TxtAlbum.Text;

            // Template: "Artist - Title [Album]"
            string text = $"{artist} - {title} [{album}]".Trim(' ', '-', '[', ']').Trim();
            try { Clipboard.SetText(text); } catch { }
        }
    }
}
