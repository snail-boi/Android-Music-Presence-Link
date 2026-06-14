using System;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace AndroidMusicPresenceLink
{
    /// <summary>
    /// Modal editor for a track's tags. Caller passes the current TrackMetadata (already
    /// read off the device) and a short label for the file; on a true DialogResult it reads
    /// <see cref="Result"/> back. Cover image loading lives here in code-behind, using
    /// OnLoad caching so the temp preview file is not held open.
    /// </summary>
    public partial class MetadataEditWindow : Window
    {
        private readonly MetadataEditViewModel _vm;

        internal TrackMetadata? Result { get; private set; }

        internal MetadataEditWindow(TrackMetadata initial, string fileLabel)
        {
            InitializeComponent();

            _vm = new MetadataEditViewModel(initial, fileLabel, PickImageFile);
            DataContext = _vm;
            _vm.RequestClose += OnRequestClose;
            _vm.PropertyChanged += OnVmPropertyChanged;

            LoadCoverPreview(_vm.CoverPreviewPath);
        }

        private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MetadataEditViewModel.CoverPreviewPath))
                LoadCoverPreview(_vm.CoverPreviewPath);
        }

        private void LoadCoverPreview(string? path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    ImgCoverPreview.Source = null;
                    return;
                }

                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                bitmap.UriSource = new Uri(path, UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze();
                ImgCoverPreview.Source = bitmap;
            }
            catch
            {
                ImgCoverPreview.Source = null;
            }
        }

        private string? PickImageFile()
        {
            var dialog = new OpenFileDialog
            {
                Title = "Choose cover image",
                Filter = "Image Files|*.png;*.jpg;*.jpeg;*.bmp;*.webp|All Files|*.*",
                CheckFileExists = true
            };

            if (dialog.ShowDialog(this) != true)
                return null;

            string path = dialog.FileName;

            // Square images need no cropping; pass them straight through.
            try
            {
                var probe = new BitmapImage();
                probe.BeginInit();
                probe.CacheOption = BitmapCacheOption.OnLoad;
                probe.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                probe.UriSource = new Uri(path, UriKind.Absolute);
                probe.EndInit();
                if (probe.PixelWidth == probe.PixelHeight)
                    return path;
            }
            catch
            {
                // If it can't be read here, let ffmpeg deal with it later.
                return path;
            }

            // Non-square: let the user crop to a square or keep the original aspect ratio.
            var crop = new CoverCropWindow(path) { Owner = this };
            return crop.ShowDialog() == true ? crop.Result : null;
        }

        private void OnRequestClose(bool result)
        {
            if (result)
                Result = _vm.BuildResult();

            DialogResult = result;
            Close();
        }
    }
}