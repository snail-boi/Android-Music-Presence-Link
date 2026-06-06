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

    }
}