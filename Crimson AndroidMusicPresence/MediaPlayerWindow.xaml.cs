using Microsoft.Win32;
using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace musicpresense
{
    public partial class MediaPlayerWindow : Window
    {
        private const double CollapsedThreshold = 24;
        private const double DefaultSettingsWidth = 460;
        private readonly Func<Task> _pauseAction;
        private readonly Func<Task> _nextAction;
        private readonly Func<Task> _previousAction;
        private string? _currentCoverPath;
        private string? _lastGradientSourcePath;
        private static readonly Color DefaultTopLeft = Color.FromRgb(52, 52, 52);
        private static readonly Color DefaultTopRight = Color.FromRgb(43, 43, 43);
        private static readonly Color DefaultBottomLeft = Color.FromRgb(36, 36, 36);
        private static readonly Color DefaultBottomRight = Color.FromRgb(28, 28, 28);

        public MediaPlayerWindow(Func<Task> pauseAction, Func<Task> nextAction, Func<Task> previousAction)
        {
            InitializeComponent();
            _pauseAction = pauseAction;
            _nextAction = nextAction;
            _previousAction = previousAction;
            RenderTransportIcons(isPlaying: false);
            RenderSettingsPaneArrowIcon();
            ApplyCoverGradientBackground(null);
        }

        public void SetSettingsContent(object? content)
        {
            SettingsHost.Content = content;
            ShowSettingsPane(restoreDefaultWidth: false);
        }

        public object? TakeSettingsContent()
        {
            var content = SettingsHost.Content;
            SettingsHost.Content = null;
            ShowSettingsPane(restoreDefaultWidth: false);
            return content;
        }

        public void ClearSettingsContent()
        {
            SettingsHost.Content = null;
        }

        private void SettingsSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            if (SettingsColumn.ActualWidth <= CollapsedThreshold)
            {
                CollapseSettingsPane();
            }
            else
            {
                ShowSettingsPane(restoreDefaultWidth: false);
            }
        }

        private void BtnShowSettingsPane_Click(object sender, RoutedEventArgs e)
        {
            ShowSettingsPane(restoreDefaultWidth: true);
        }

        private void CollapseSettingsPane()
        {
            SettingsPaneBorder.Visibility = Visibility.Collapsed;
            SettingsColumn.Width = new GridLength(0, GridUnitType.Pixel);
            SplitterColumn.Width = new GridLength(0, GridUnitType.Pixel);
            BtnShowSettingsPane.Visibility = Visibility.Visible;
            BtnShowSettingsPane.IsEnabled = true;
        }

        private void ShowSettingsPane(bool restoreDefaultWidth)
        {
            var hasSettingsContent = SettingsHost.Content != null;

            if (!hasSettingsContent)
            {
                SettingsPaneBorder.Visibility = Visibility.Collapsed;
                SettingsColumn.Width = new GridLength(0, GridUnitType.Pixel);
                SplitterColumn.Width = new GridLength(0, GridUnitType.Pixel);
                BtnShowSettingsPane.Visibility = Visibility.Collapsed;
                return;
            }

            SettingsPaneBorder.Visibility = Visibility.Visible;
            SplitterColumn.Width = new GridLength(8, GridUnitType.Pixel);

            if (restoreDefaultWidth || SettingsColumn.Width.Value <= CollapsedThreshold)
            {
                SettingsColumn.Width = new GridLength(DefaultSettingsWidth, GridUnitType.Pixel);
            }

            BtnShowSettingsPane.Visibility = Visibility.Collapsed;
        }

        public void UpdateTrack(string? title, string? artist, string? album, string? coverPath, bool isPlaying)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => UpdateTrack(title, artist, album, coverPath, isPlaying));
                return;
            }

            TxtTitle.Text = string.IsNullOrWhiteSpace(title) ? "-" : title.Trim();
            TxtArtist.Text = string.IsNullOrWhiteSpace(artist) ? "-" : artist.Trim();
            TxtAlbum.Text = string.IsNullOrWhiteSpace(album) ? "-" : album.Trim();
            RenderTransportIcons(isPlaying);

            _currentCoverPath = string.IsNullOrWhiteSpace(coverPath) ? null : coverPath;
            SetCoverImage(_currentCoverPath);
            ApplyCoverGradientBackground(_currentCoverPath);
        }

        private void SetCoverImage(string? path)
        {
            ImgCover.Source = null;

            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return;

            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(path, UriKind.Absolute);
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.EndInit();
                bitmap.Freeze();
                ImgCover.Source = bitmap;
            }
            catch
            {
            }
        }

        private void ApplyCoverGradientBackground(string? imagePath)
        {
            if (string.Equals(_lastGradientSourcePath, imagePath, StringComparison.OrdinalIgnoreCase))
                return;

            _lastGradientSourcePath = imagePath;

            var colors = ExtractGradientColors(imagePath);
            PlayerPaneBorder.Background = BuildFourToneCornerBrush(colors.topLeft, colors.topRight, colors.bottomLeft, colors.bottomRight);
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

        private void RenderTransportIcons(bool isPlaying)
        {
            var iconBrush = ResolveIconBrush();

            const double sideIconSize = 30;
            const double centerIconSize = 42;

            BtnPrevious.Content = BuildPreviousIcon(iconBrush, sideIconSize);
            BtnPause.Content = isPlaying ? BuildPauseIcon(iconBrush, centerIconSize) : BuildPlayIcon(iconBrush, centerIconSize);
            BtnNext.Content = BuildNextIcon(iconBrush, sideIconSize);
        }

        private void RenderSettingsPaneArrowIcon()
        {
            var iconBrush = TryFindResource("ThemeControlForegroundBrush") as Brush ?? Brushes.White;
            BtnShowSettingsPane.Content = BuildRevealSettingsArrowIcon(iconBrush);
        }

        private Brush ResolveIconBrush()
        {
            return TryFindResource("ThemeControlForegroundBrush") as Brush ?? Brushes.White;
        }

        private static Viewbox BuildPreviousIcon(Brush brush, double size = 20)
        {
            var canvas = new Canvas { Width = 20, Height = 20 };

            var bar = new Rectangle
            {
                Width = 2.4,
                Height = 12,
                Fill = brush
            };
            Canvas.SetLeft(bar, 2);
            Canvas.SetTop(bar, 4);

            var triangle = new Polygon
            {
                Fill = brush,
                Points = new PointCollection
                {
                    new Point(15, 4),
                    new Point(6, 10),
                    new Point(15, 16)
                }
            };

            canvas.Children.Add(bar);
            canvas.Children.Add(triangle);

            return new Viewbox { Width = size, Height = size, Child = canvas };
        }

        private static Viewbox BuildRevealSettingsArrowIcon(Brush brush)
        {
            var canvas = new Canvas { Width = 14, Height = 20 };

            var chevron = new Polygon
            {
                Fill = brush,
                Points = new PointCollection
                {
                    new Point(3, 3),
                    new Point(11, 10),
                    new Point(3, 17),
                    new Point(6, 17),
                    new Point(14, 10),
                    new Point(6, 3)
                }
            };

            canvas.Children.Add(chevron);
            return new Viewbox { Width = 14, Height = 20, Child = canvas };
        }

        private static Viewbox BuildPlayIcon(Brush brush, double size = 20)
        {
            var canvas = new Canvas { Width = 20, Height = 20 };

            var triangle = new Polygon
            {
                Fill = brush,
                Points = new PointCollection
                {
                    new Point(6, 4),
                    new Point(15, 10),
                    new Point(6, 16)
                }
            };

            canvas.Children.Add(triangle);
            return new Viewbox { Width = size, Height = size, Child = canvas };
        }

        private static Viewbox BuildPauseIcon(Brush brush, double size = 20)
        {
            var canvas = new Canvas { Width = 20, Height = 20 };

            var leftBar = new Rectangle
            {
                Width = 3,
                Height = 12,
                Fill = brush
            };
            Canvas.SetLeft(leftBar, 5);
            Canvas.SetTop(leftBar, 4);

            var rightBar = new Rectangle
            {
                Width = 3,
                Height = 12,
                Fill = brush
            };
            Canvas.SetLeft(rightBar, 12);
            Canvas.SetTop(rightBar, 4);

            canvas.Children.Add(leftBar);
            canvas.Children.Add(rightBar);

            return new Viewbox { Width = size, Height = size, Child = canvas };
        }

        private static Viewbox BuildNextIcon(Brush brush, double size = 20)
        {
            var canvas = new Canvas { Width = 20, Height = 20 };

            var triangle = new Polygon
            {
                Fill = brush,
                Points = new PointCollection
                {
                    new Point(5, 4),
                    new Point(14, 10),
                    new Point(5, 16)
                }
            };

            var bar = new Rectangle
            {
                Width = 2.4,
                Height = 12,
                Fill = brush
            };
            Canvas.SetLeft(bar, 16);
            Canvas.SetTop(bar, 4);

            canvas.Children.Add(triangle);
            canvas.Children.Add(bar);

            return new Viewbox { Width = size, Height = size, Child = canvas };
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
    }
}