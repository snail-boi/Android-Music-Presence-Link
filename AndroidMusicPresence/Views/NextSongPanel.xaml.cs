using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AndroidMusicPresenceLink
{
    public partial class NextSongPanel : UserControl
    {
        private string _directionLabel = string.Empty;
        private string _title = string.Empty;
        private string? _coverPath;
        private bool _showCover = true;
        private bool _roundedCorners = true;
        private bool _isStale;
        private bool _isPrevious;
        private bool _textOnlyMode;
        private bool _coverOnly;

        public event Action? PreviousRequested;
        public event Action? NextRequested;

        public event Action? RefreshRequested;

        public NextSongPanel()
        {
            InitializeComponent();
        }

        public void ShowTextOnly(string directionLabel, string title)
        {
            _directionLabel = directionLabel;
            _title = title;
            _coverPath = null;
            _textOnlyMode = true;
            _isStale = false;
            ApplyState();
        }

        public void ShowWithCover(string directionLabel, string title, string? coverPath)
        {
            _directionLabel = directionLabel;
            _title = title;
            _coverPath = coverPath;
            _textOnlyMode = false;
            _isStale = false;
            ApplyState();
        }

        public void SetDirection(bool isPrevious)
        {
            _isPrevious = isPrevious;
        }

        public void SetRoundedCorners(bool rounded)
        {
            _roundedCorners = rounded;
            ApplyState();
        }

        public void SetShowCover(bool showCover)
        {
            _showCover = showCover;
            ApplyState();
        }

        /// <summary>
        /// When true, the direction label and title are hidden so only the cover
        /// thumbnail is shown (used by Kirsten mode). Does not affect any other mode.
        /// </summary>
        public void SetCoverOnly(bool coverOnly)
        {
            _coverOnly = coverOnly;
            ApplyState();
        }

        public void ShowStale()
        {
            _coverPath = null;
            _textOnlyMode = false;
            _isStale = true;
            ApplyState();
        }

        public void Hide()
        {
            RootPanel.Visibility = Visibility.Collapsed;
            CoverImage.Source = null;
            PanelStale.Visibility = Visibility.Collapsed;
        }

        private void BtnRefresh_Click(object sender, RoutedEventArgs e)
        {
            RefreshRequested?.Invoke();
        }

        private void UserControl_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_isStale)
                return;

            if (_isPrevious)
                PreviousRequested?.Invoke();
            else
                NextRequested?.Invoke();
        }

        private void ApplyState()
        {
            if (_isStale)
            {
                CoverBorder.Visibility = Visibility.Collapsed;
                CoverImage.Source = null;
                CoverBorder.Clip = null;
                TxtDirection.Visibility = Visibility.Collapsed;
                TxtTitle.Visibility = Visibility.Collapsed;
                PanelStale.Visibility = Visibility.Visible;
                RootPanel.Visibility = Visibility.Visible;
                return;
            }

            PanelStale.Visibility = Visibility.Collapsed;
            TxtDirection.Text = _directionLabel;
            TxtTitle.Text = _title;
            TxtDirection.Visibility = _coverOnly ? Visibility.Collapsed : Visibility.Visible;
            TxtTitle.Visibility = _coverOnly ? Visibility.Collapsed : Visibility.Visible;

            TitleHost.MinHeight = _textOnlyMode ? 58 : 22;
            TxtTitle.MaxHeight = _textOnlyMode ? 84 : 42;
            TxtTitle.TextWrapping = _textOnlyMode ? TextWrapping.Wrap : TextWrapping.NoWrap;

            var radius = _roundedCorners ? new CornerRadius(6) : new CornerRadius(0);
            CoverBorder.CornerRadius = radius;

            bool showCover = _showCover && !string.IsNullOrWhiteSpace(_coverPath) && System.IO.File.Exists(_coverPath);
            if (showCover)
            {
                try
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.UriSource = new Uri(_coverPath!, UriKind.Absolute);
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.EndInit();
                    bmp.Freeze();
                    CoverImage.Source = bmp;
                    CoverBorder.Visibility = Visibility.Visible;
                    CoverBorder.Clip = _roundedCorners
                        ? new RectangleGeometry(new Rect(0, 0, CoverBorder.Width, CoverBorder.Height), 6, 6)
                        : null;
                }
                catch
                {
                    CoverImage.Source = null;
                    CoverBorder.Visibility = Visibility.Collapsed;
                    CoverBorder.Clip = null;
                }
            }
            else
            {
                CoverImage.Source = null;
                CoverBorder.Visibility = Visibility.Collapsed;
                CoverBorder.Clip = null;
            }

            RootPanel.Visibility = Visibility.Visible;
        }
    }
}