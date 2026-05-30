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
                int samplePoints = App.Config?.PlayerGradientSamplePoints ?? 8;
                var colors = ExtractGradientColors(imagePath, samplePoints);
                newBrush = BuildGradientBrush(colors, samplePoints);
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
        private static Brush BuildGradientBrush(
            (Color topLeft, Color topRight, Color bottomLeft, Color bottomRight,
             Color top, Color bottom, Color left, Color right) c,
            int samplePoints)
        {
            if (samplePoints == 2)
                return BuildFourToneCornerBrush(c.topLeft, c.topLeft, c.bottomRight, c.bottomRight);
            if (samplePoints == 4)
                return BuildFourToneCornerBrush(c.topLeft, c.topRight, c.bottomLeft, c.bottomRight);

            // 6 or 8: 3x3 bitmap so edge-centre samples blend smoothly.
            Color centre = Color.FromRgb(
                (byte)((c.topLeft.R + c.topRight.R + c.bottomLeft.R + c.bottomRight.R) / 4),
                (byte)((c.topLeft.G + c.topRight.G + c.bottomLeft.G + c.bottomRight.G) / 4),
                (byte)((c.topLeft.B + c.topRight.B + c.bottomLeft.B + c.bottomRight.B) / 4));

            Color[] grid =
            {
                c.topLeft,    c.top,    c.topRight,
                c.left,       centre,   c.right,
                c.bottomLeft, c.bottom, c.bottomRight,
            };

            var bmp = new WriteableBitmap(3, 3, 96, 96, PixelFormats.Bgra32, null);
            var px = new byte[3 * 3 * 4];
            for (int i = 0; i < grid.Length; i++)
            {
                px[i * 4 + 0] = grid[i].B; px[i * 4 + 1] = grid[i].G;
                px[i * 4 + 2] = grid[i].R; px[i * 4 + 3] = 255;
            }
            bmp.WritePixels(new Int32Rect(0, 0, 3, 3), px, 3 * 4, 0);
            bmp.Freeze();
            var brush = new ImageBrush(bmp) { Stretch = Stretch.Fill };
            brush.Freeze();
            return brush;
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
        private static (Color topLeft, Color topRight, Color bottomLeft, Color bottomRight,
                        Color top, Color bottom, Color left, Color right)
            ExtractGradientColors(string? imagePath, int samplePoints = 8)
        {
            var tl0 = DefaultTopLeft; var tr0 = DefaultTopRight;
            var bl0 = DefaultBottomLeft; var br0 = DefaultBottomRight;
            var fallback = (tl0, tr0, bl0, br0, tl0, bl0, tl0, tr0);

            if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
                return fallback;

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
                if (width <= 0 || height <= 0) return fallback;

                int stride = width * 4;
                var pixels = new byte[stride * height];
                formatted.CopyPixels(pixels, stride, 0);

                long tlR = 0, tlG = 0, tlB = 0, tlN = 0, trR = 0, trG = 0, trB = 0, trN = 0;
                long blR = 0, blG = 0, blB = 0, blN = 0, brR = 0, brG = 0, brB = 0, brN = 0;
                long tR = 0, tG = 0, tB = 0, tN = 0, boR = 0, boG = 0, boB = 0, boN = 0;
                long lR = 0, lG = 0, lB = 0, lN = 0, rR = 0, rG = 0, rB = 0, rN = 0;

                int hw = width / 2, hh = height / 2;
                int cx0 = width / 4, cx1 = 3 * width / 4;
                int cy0 = height / 4, cy1 = 3 * height / 4;

                for (int y = 0; y < height; y++)
                {
                    int rowStart = y * stride;
                    bool isTop = y < hh;

                    for (int x = 0; x < width; x++)
                    {
                        int i = rowStart + (x * 4);
                        byte b = pixels[i]; byte g = pixels[i + 1]; byte r = pixels[i + 2]; byte a = pixels[i + 3];

                        if (a < 24) continue;
                        int max = Math.Max(r, Math.Max(g, b));
                        int min = Math.Min(r, Math.Min(g, b));
                        if (max < 20 || (max - min) < 8) continue;

                        bool isLeft = x < hw;
                        if (isTop && isLeft) { tlR += r; tlG += g; tlB += b; tlN++; }
                        else if (isTop) { trR += r; trG += g; trB += b; trN++; }
                        else if (isLeft) { blR += r; blG += g; blB += b; blN++; }
                        else { brR += r; brG += g; brB += b; brN++; }

                        if (samplePoints >= 6)
                        {
                            if (y < hh / 2 && x >= cx0 && x < cx1) { tR += r; tG += g; tB += b; tN++; }
                            if (y >= height - hh / 2 && x >= cx0 && x < cx1) { boR += r; boG += g; boB += b; boN++; }
                        }
                        if (samplePoints == 8)
                        {
                            if (x < hw / 2 && y >= cy0 && y < cy1) { lR += r; lG += g; lB += b; lN++; }
                            if (x >= width - hw / 2 && y >= cy0 && y < cy1) { rR += r; rG += g; rB += b; rN++; }
                        }
                    }
                }

                if (tlN == 0 && trN == 0 && blN == 0 && brN == 0) return fallback;

                Color Avg(long r, long g, long b, long n, Color fb)
                    => n <= 0 ? fb : Color.FromRgb((byte)(r / n), (byte)(g / n), (byte)(b / n));

                var tl = Avg(tlR, tlG, tlB, tlN, DefaultTopLeft);
                var tr = Avg(trR, trG, trB, trN, DefaultTopRight);
                var bl = Avg(blR, blG, blB, blN, DefaultBottomLeft);
                var br = Avg(brR, brG, brB, brN, DefaultBottomRight);
                var tc = Avg(tR, tG, tB, tN, Color.FromRgb((byte)((tl.R + tr.R) / 2), (byte)((tl.G + tr.G) / 2), (byte)((tl.B + tr.B) / 2)));
                var bc = Avg(boR, boG, boB, boN, Color.FromRgb((byte)((bl.R + br.R) / 2), (byte)((bl.G + br.G) / 2), (byte)((bl.B + br.B) / 2)));
                var lc = Avg(lR, lG, lB, lN, Color.FromRgb((byte)((tl.R + bl.R) / 2), (byte)((tl.G + bl.G) / 2), (byte)((tl.B + bl.B) / 2)));
                var rc = Avg(rR, rG, rB, rN, Color.FromRgb((byte)((tr.R + br.R) / 2), (byte)((tr.G + br.G) / 2), (byte)((tr.B + br.B) / 2)));

                tl = BlendWith(tl, Color.FromRgb(255, 255, 255), 0.06);
                tr = BlendWith(tr, Color.FromRgb(255, 255, 255), 0.06);
                bl = BlendWith(bl, Color.FromRgb(0, 0, 0), 0.25);
                br = BlendWith(br, Color.FromRgb(0, 0, 0), 0.25);

                return (tl, tr, bl, br, tc, bc, lc, rc);
            }
            catch
            {
                return fallback;
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
            string title = string.IsNullOrWhiteSpace(TxtTitle.Text) || TxtTitle.Text == "-" ? "" : TxtTitle.Text;
            string artist = string.IsNullOrWhiteSpace(TxtArtist.Text) || TxtArtist.Text == "-" ? "" : TxtArtist.Text;
            string album = string.IsNullOrWhiteSpace(TxtAlbum.Text) || TxtAlbum.Text == "-" ? "" : TxtAlbum.Text;

            // Template: "Artist - Title [Album]"
            string text = $"{artist} - {title} [{album}]".Trim(' ', '-', '[', ']').Trim();
            try { Clipboard.SetText(text); } catch { }
        }
    }
}