using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace musicpresense
{
    /// <summary>
    /// Apps group: the eligible-apps list with per-row presence and cover toggles, plus Manage
    /// Apps (opens the manager dialog) and Clear Disabled. The list mirrors config.EligibleApps;
    /// Manage and Clear edit the config and rebuild the list, while the per-row toggles edit the
    /// AppPackageItem rows directly and are written back to config on save.
    /// </summary>
    internal sealed partial class SettingsViewModel
    {
        // Set by the window. Opens the Apps Manager for the given config and calls back with the
        // updated config when the user saves it.
        public Action<MusicConfig, Action<MusicConfig>>? ShowAppsManager { get; set; }

        public ObservableCollection<AppPackageItem> AppPackages { get; } = new();

        public RelayCommand<AppPackageItem> CyclePresenceModeCommand { get; private set; } = null!;
        public RelayCommand<AppPackageItem> ToggleCoverCommand { get; private set; } = null!;
        public RelayCommand ManageAppsCommand { get; private set; } = null!;
        public RelayCommand ClearDisabledAppsCommand { get; private set; } = null!;

        partial void InitApps()
        {
            CyclePresenceModeCommand = new RelayCommand<AppPackageItem>(item => item?.CyclePresenceMode());
            ToggleCoverCommand = new RelayCommand<AppPackageItem>(item => item?.ToggleCover());
            ManageAppsCommand = new RelayCommand(ManageApps);
            ClearDisabledAppsCommand = new RelayCommand(ClearDisabledApps);

            LoadAppsFromConfig();
        }

        // Equivalent to the old RefreshAppsSummary: rebuild the rows from config.EligibleApps.
        partial void LoadAppsFromConfig()
        {
            AppPackages.Clear();
            foreach (var app in _config.EligibleApps ?? new List<EligibleAppConfig>())
            {
                if (string.IsNullOrWhiteSpace(app.PackageName))
                    continue;
                AppPackages.Add(new AppPackageItem(app.PackageName, app.PresenceMode, app.EnableCoverSearch));
            }
        }

        partial void ApplyAppsToConfig(MusicConfig config)
        {
            if (AppPackages.Count > 0)
            {
                config.EligibleApps = AppPackages
                    .Select(item => new EligibleAppConfig
                    {
                        PackageName = item.PackageName,
                        PresenceMode = item.PresenceMode,
                        EnableCoverSearch = item.EnableCoverSearch
                    })
                    .Where(item => !string.IsNullOrWhiteSpace(item.PackageName))
                    .GroupBy(item => item.PackageName, StringComparer.OrdinalIgnoreCase)
                    .Select(g => new EligibleAppConfig
                    {
                        PackageName = g.Key,
                        PresenceMode = g.Max(x => (int)x.PresenceMode) switch { 2 => PresenceMode.Full, 1 => PresenceMode.Half, _ => PresenceMode.Off },
                        EnableCoverSearch = g.Any(x => x.EnableCoverSearch)
                    })
                    .ToList();
            }
            else
            {
                config.EligibleApps = _config.EligibleApps?.Select(a => new EligibleAppConfig
                {
                    PackageName = a.PackageName,
                    PresenceMode = a.PresenceMode,
                    EnableCoverSearch = a.EnableCoverSearch
                }).ToList() ?? new List<EligibleAppConfig>();
            }

            config.AllowedApps = config.EligibleApps
                .Where(a => a.PresenceMode != PresenceMode.Off)
                .Select(a => a.PackageName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private void ManageApps()
        {
            ShowAppsManager?.Invoke(_config, updated =>
            {
                _config.EligibleApps = updated.EligibleApps;
                _config.AllowedApps = updated.AllowedApps;
                LoadAppsFromConfig();
            });
        }

        private void ClearDisabledApps()
        {
            _config.EligibleApps = _config.EligibleApps
                .Where(a => a.PresenceMode != PresenceMode.Off || a.EnableCoverSearch)
                .ToList();
            LoadAppsFromConfig();
        }
    }
}
