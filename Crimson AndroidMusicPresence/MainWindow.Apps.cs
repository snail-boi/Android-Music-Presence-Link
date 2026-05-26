using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace musicpresense
{
    public partial class MainWindow
    {
        private void BtnPresenceMode_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as System.Windows.Controls.Button)?.Tag is AppPackageItem item)
                item.CyclePresenceMode();
        }

        private void BtnCover_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as System.Windows.Controls.Button)?.Tag is AppPackageItem item)
                item.ToggleCover();
        }

        private void BtnManageApps_Click(object sender, RoutedEventArgs e)
        {
            var window = new AppsManagerWindow(_config, updatedConfig =>
            {
                _config.EligibleApps = updatedConfig.EligibleApps;
                _config.AllowedApps = updatedConfig.AllowedApps;
                RefreshAppsSummary();
            });

            // Only set Owner if this window has been shown; when settings are hosted
            // as a UserControl inside the media player, 'this' may not be a shown Window.
            var ownerWindow = Window.GetWindow(this);
            if (ownerWindow != null && ownerWindow.IsLoaded)
                window.Owner = ownerWindow;

            window.ShowDialog();
        }

        private void BtnClearDisabledApps_Click(object sender, RoutedEventArgs e)
        {
            _config.EligibleApps = _config.EligibleApps
                .Where(a => a.PresenceMode != PresenceMode.Off || a.EnableCoverSearch)
                .ToList();
            RefreshAppsSummary();
        }

        internal void RefreshAppsSummary()
        {
            _appPackages.Clear();
            foreach (var app in _config.EligibleApps ?? new List<EligibleAppConfig>())
            {
                if (string.IsNullOrWhiteSpace(app.PackageName))
                    continue;
                _appPackages.Add(new AppPackageItem(app.PackageName, app.PresenceMode, app.EnableCoverSearch));
            }
        }

        /// <summary>
        /// Strips TLD prefix and replaces dots/underscores with spaces for display.
        /// e.g. "com.spotify.music" -> "Spotify Music", "jp.nicovideo.android" -> "Nicovideo Android"
        /// </summary>
        internal static string FormatPackageName(string packageName)
        {
            if (string.IsNullOrWhiteSpace(packageName))
                return packageName;

            var parts = packageName.Split('.');
            // Drop known TLD prefixes: com, org, net, io, jp, de, fr, uk, app, me, co
            var tlds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "com", "org", "net", "io", "jp", "de", "fr", "uk", "app", "me", "co" };

            var meaningful = parts.SkipWhile(p => tlds.Contains(p)).ToList();
            if (meaningful.Count == 0)
                meaningful = parts.ToList();

            var result = string.Join(" ", meaningful.Select(p =>
            {
                var s = p.Replace('_', ' ').Replace('-', ' ');
                if (string.IsNullOrWhiteSpace(s)) return s;
                return char.ToUpper(s[0]) + s.Substring(1);
            }));

            return result.Trim();
        }

        internal sealed class AppPackageItem : INotifyPropertyChanged
        {
            public event PropertyChangedEventHandler? PropertyChanged;

            public string PackageName { get; }
            public string DisplayName => FormatPackageName(PackageName);

            private PresenceMode _presenceMode;
            public PresenceMode PresenceMode
            {
                get => _presenceMode;
                set
                {
                    if (_presenceMode == value) return;
                    _presenceMode = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PresenceMode)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PresenceModeLabel)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PresenceModeColor)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PresenceModeBrush)));
                }
            }

            private bool _enableCoverSearch;
            public bool EnableCoverSearch
            {
                get => _enableCoverSearch;
                set
                {
                    if (_enableCoverSearch == value) return;
                    _enableCoverSearch = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EnableCoverSearch)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CoverLabel)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CoverColor)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CoverBrush)));
                }
            }

            public string PresenceModeLabel => _presenceMode switch
            {
                PresenceMode.Full => "Full",
                PresenceMode.Half => "Half",
                _ => "Off"
            };

            public string PresenceModeColor => _presenceMode switch
            {
                PresenceMode.Full => "#34C954",
                PresenceMode.Half => "#3E7BFF",
                _ => "#FF3B30"
            };

            public System.Windows.Media.Brush PresenceModeBrush => new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(PresenceModeColor));

            public string CoverLabel => _enableCoverSearch ? "On" : "Off";
            public string CoverColor => _enableCoverSearch ? "#34C954" : "#FF3B30";
            public System.Windows.Media.Brush CoverBrush => new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(CoverColor));

            public AppPackageItem(string packageName, PresenceMode presenceMode, bool enableCoverSearch)
            {
                PackageName = packageName;
                _presenceMode = presenceMode;
                _enableCoverSearch = enableCoverSearch;
            }

            public void CyclePresenceMode()
            {
                PresenceMode = PresenceMode switch
                {
                    PresenceMode.Full => PresenceMode.Half,
                    PresenceMode.Half => PresenceMode.Off,
                    _ => PresenceMode.Full
                };
            }

            public void ToggleCover()
            {
                EnableCoverSearch = !EnableCoverSearch;
            }
        }
    }
}