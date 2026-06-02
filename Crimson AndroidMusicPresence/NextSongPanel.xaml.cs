using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace musicpresense
{
    public partial class NextSongPanel : UserControl
    {
        public event Action? RefreshRequested;

        public NextSongPanel()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Shows the panel with a song title (text only mode).
        /// </summary>
        public void ShowTextOnly(string directionLabel, string title)
        {
            TxtDirection.Text = directionLabel;
            TxtTitle.Text = title;
            CoverBorder.Visibility = Visibility.Collapsed;
            PanelStale.Visibility = Visibility.Collapsed;
            TxtTitle.Visibility = Visibility.Visible;
            TxtDirection.Visibility = Visibility.Visible;
            RootBorder.IsHitTestVisible = false;
            RootBorder.Visibility = Visibility.Visible;
            RootBorder.Opacity = 1;
        }

        /// <summary>
        /// Shows the panel with cover art and title (full art mode).
        /// </summary>
        public void ShowWithCover(string directionLabel, string title, string? coverPath)
        {
            TxtDirection.Text = directionLabel;
            TxtTitle.Text = title;
            PanelStale.Visibility = Visibility.Collapsed;
            TxtTitle.Visibility = Visibility.Visible;
            TxtDirection.Visibility = Visibility.Visible;

            if (!string.IsNullOrWhiteSpace(coverPath) && System.IO.File.Exists(coverPath))
            {
                try
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.UriSource = new Uri(coverPath, UriKind.Absolute);
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.EndInit();
                    bmp.Freeze();
                    CoverImage.Source = bmp;
                    CoverBorder.Visibility = Visibility.Visible;
                }
                catch
                {
                    CoverBorder.Visibility = Visibility.Collapsed;
                }
            }
            else
            {
                CoverBorder.Visibility = Visibility.Collapsed;
            }

            RootBorder.IsHitTestVisible = false;
            RootBorder.Visibility = Visibility.Visible;
            RootBorder.Opacity = 1;
        }

        /// <summary>
        /// Shows the stale-list indicator with a refresh button instead of song info.
        /// Only shown on one panel (whichever side is configured to host it).
        /// </summary>
        public void ShowStale()
        {
            TxtTitle.Visibility = Visibility.Collapsed;
            TxtDirection.Visibility = Visibility.Collapsed;
            CoverBorder.Visibility = Visibility.Collapsed;
            PanelStale.Visibility = Visibility.Visible;
            RootBorder.IsHitTestVisible = true;
            RootBorder.Visibility = Visibility.Visible;
            RootBorder.Opacity = 1;
        }

        /// <summary>
        /// Hides the panel completely.
        /// </summary>
        public void Hide()
        {
            RootBorder.Visibility = Visibility.Collapsed;
            RootBorder.Opacity = 0;
            CoverImage.Source = null;
        }

        private void BtnRefresh_Click(object sender, RoutedEventArgs e)
        {
            RefreshRequested?.Invoke();
        }
    }
}
