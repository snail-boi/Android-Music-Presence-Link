using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AndroidMusicPresenceLink
{
    /// <summary>
    /// Lets the user crop a non-square cover to a square, or keep the original aspect ratio.
    /// All of this is interactive drag/resize/render work, so it lives in code-behind by
    /// convention. On a true DialogResult, <see cref="Result"/> holds the path to use: a
    /// freshly written square PNG when cropping, or the original image when the user opts to
    /// keep it non-square.
    /// </summary>
    public partial class CoverCropWindow : Window
    {
        private readonly string _originalPath;
        private readonly BitmapSource? _source;

        private double _scale, _offX, _offY, _dispW, _dispH;

        // Crop kept as fractions of the displayed image so it survives window resizes. Because
        // the image scales uniformly, equal-pixel (square) crops stay square across resizes.
        private bool _hasCrop;
        private double _fx, _fy, _fw, _fh;

        private bool _moving, _resizing;
        private Point _lastPoint;

        internal string? Result { get; private set; }

        internal CoverCropWindow(string imagePath)
        {
            InitializeComponent();
            _originalPath = imagePath;

            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                bmp.UriSource = new Uri(imagePath, UriKind.Absolute);
                bmp.EndInit();
                bmp.Freeze();
                _source = bmp;
                ImgSource.Source = _source;
            }
            catch
            {
                _source = null;
            }

            Loaded += (s, e) => { ComputeMetrics(); if (!_hasCrop) InitCrop(); Reproject(); };
        }

        private void CropArea_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            ComputeMetrics();
            if (!_hasCrop) InitCrop();
            Reproject();
        }

        private void ComputeMetrics()
        {
            if (_source == null) return;
            double cw = CropArea.ActualWidth, ch = CropArea.ActualHeight;
            if (cw <= 0 || ch <= 0) return;

            double sw = _source.PixelWidth, sh = _source.PixelHeight;
            _scale = Math.Min(cw / sw, ch / sh);
            _dispW = sw * _scale;
            _dispH = sh * _scale;
            _offX = (cw - _dispW) / 2;
            _offY = (ch - _dispH) / 2;

            Overlay.Width = cw;
            Overlay.Height = ch;
        }

        private void InitCrop()
        {
            if (_dispW <= 0 || _dispH <= 0) return;

            // Largest centered square within the displayed image.
            double side = Math.Min(_dispW, _dispH);
            double left = _offX + (_dispW - side) / 2;
            double top = _offY + (_dispH - side) / 2;
            SetCropCanvas(left, top, side, side);
            StoreFractions();
            _hasCrop = true;
        }

        private void Reproject()
        {
            if (!_hasCrop || ChkSquare.IsChecked != true) { UpdateMaskAndHandle(); return; }
            double left = _offX + _fx * _dispW;
            double top = _offY + _fy * _dispH;
            SetCropCanvas(left, top, _fw * _dispW, _fh * _dispH);
        }

        private void SetCropCanvas(double left, double top, double w, double h)
        {
            Canvas.SetLeft(CropRect, left);
            Canvas.SetTop(CropRect, top);
            CropRect.Width = Math.Max(1, w);
            CropRect.Height = Math.Max(1, h);
            UpdateMaskAndHandle();
        }

        private void StoreFractions()
        {
            if (_dispW <= 0 || _dispH <= 0) return;
            _fx = (Canvas.GetLeft(CropRect) - _offX) / _dispW;
            _fy = (Canvas.GetTop(CropRect) - _offY) / _dispH;
            _fw = CropRect.Width / _dispW;
            _fh = CropRect.Height / _dispH;
        }

        private void UpdateMaskAndHandle()
        {
            bool square = ChkSquare.IsChecked == true;
            CropRect.Visibility = square ? Visibility.Visible : Visibility.Collapsed;
            ResizeHandle.Visibility = square ? Visibility.Visible : Visibility.Collapsed;

            if (!square || _source == null)
            {
                MaskPath.Data = null;
                return;
            }

            double left = Canvas.GetLeft(CropRect);
            double top = Canvas.GetTop(CropRect);
            double w = CropRect.Width, h = CropRect.Height;

            var outer = new RectangleGeometry(new Rect(0, 0, Overlay.Width, Overlay.Height));
            var inner = new RectangleGeometry(new Rect(left, top, w, h));
            var group = new GeometryGroup { FillRule = FillRule.EvenOdd };
            group.Children.Add(outer);
            group.Children.Add(inner);
            MaskPath.Data = group;

            Canvas.SetLeft(ResizeHandle, left + w - ResizeHandle.Width / 2);
            Canvas.SetTop(ResizeHandle, top + h - ResizeHandle.Height / 2);
        }

        private void CropRect_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (ChkSquare.IsChecked != true) return;
            _moving = true;
            _lastPoint = e.GetPosition(Overlay);
            Overlay.CaptureMouse();
            e.Handled = true;
        }

        private void ResizeHandle_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (ChkSquare.IsChecked != true) return;
            _resizing = true;
            _lastPoint = e.GetPosition(Overlay);
            Overlay.CaptureMouse();
            e.Handled = true;
        }

        private void Overlay_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_moving && !_resizing) return;

            var p = e.GetPosition(Overlay);
            double dx = p.X - _lastPoint.X, dy = p.Y - _lastPoint.Y;
            _lastPoint = p;

            double left = Canvas.GetLeft(CropRect), top = Canvas.GetTop(CropRect);
            double w = CropRect.Width, h = CropRect.Height;
            double minX = _offX, minY = _offY, maxX = _offX + _dispW, maxY = _offY + _dispH;

            if (_moving)
            {
                Canvas.SetLeft(CropRect, Clamp(left + dx, minX, maxX - w));
                Canvas.SetTop(CropRect, Clamp(top + dy, minY, maxY - h));
            }
            else
            {
                // Resize, keeping it square and inside the image.
                double delta = (dx + dy) / 2;
                double maxSide = Math.Min(maxX - left, maxY - top);
                double newSide = Clamp(w + delta, 32, Math.Max(32, maxSide));
                CropRect.Width = newSide;
                CropRect.Height = newSide;
            }

            UpdateMaskAndHandle();
            StoreFractions();
        }

        private void Overlay_MouseUp(object sender, MouseButtonEventArgs e)
        {
            _moving = _resizing = false;
            Overlay.ReleaseMouseCapture();
        }

        private void ChkSquare_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            if (ChkSquare.IsChecked == true && !_hasCrop) InitCrop();
            Reproject();
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (ChkSquare.IsChecked != true || _source == null)
                    Result = _originalPath;        // keep the original (non-square) image
                else
                    Result = ProduceCrop() ?? _originalPath;

                DialogResult = true;
            }
            catch
            {
                Result = _originalPath;
                DialogResult = true;
            }
            Close();
        }

        private string? ProduceCrop()
        {
            if (_source == null || _scale <= 0) return null;

            double left = Canvas.GetLeft(CropRect), top = Canvas.GetTop(CropRect);
            int x = (int)Math.Round((left - _offX) / _scale);
            int y = (int)Math.Round((top - _offY) / _scale);
            int w = (int)Math.Round(CropRect.Width / _scale);
            int h = (int)Math.Round(CropRect.Height / _scale);

            x = Clamp(x, 0, _source.PixelWidth - 1);
            y = Clamp(y, 0, _source.PixelHeight - 1);
            w = Clamp(w, 1, _source.PixelWidth - x);
            h = Clamp(h, 1, _source.PixelHeight - y);

            var cropped = new CroppedBitmap(_source, new Int32Rect(x, y, w, h));

            string dir = Path.Combine(Path.GetTempPath(), "AMPL_TagEdit");
            Directory.CreateDirectory(dir);
            string outPath = Path.Combine(dir, "crop_" + Guid.NewGuid().ToString("N") + ".png");

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(cropped));
            using (var fs = new FileStream(outPath, FileMode.Create))
                encoder.Save(fs);

            return outPath;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private static double Clamp(double v, double min, double max) => v < min ? min : (v > max ? max : v);
        private static int Clamp(int v, int min, int max) => v < min ? min : (v > max ? max : v);
    }
}
