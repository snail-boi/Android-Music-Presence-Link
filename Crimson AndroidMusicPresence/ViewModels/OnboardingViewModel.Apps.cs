using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace musicpresense
{
    /// <summary>
    /// Apps step: the list of installed apps and their presence/cover settings, the same
    /// AppPackageItem rows used by the Apps Manager window. Loading runs on the UI thread via
    /// ConfigureAwait(true), so the bound collection updates without any Dispatcher calls.
    /// </summary>
    internal sealed partial class OnboardingViewModel
    {
        public ObservableCollection<AppPackageItem> AppPackages { get; } = new();

        private bool _isLoadingApps;

        private string _appsStatus = string.Empty;
        public string AppsStatus
        {
            get => _appsStatus;
            set => Set(ref _appsStatus, value);
        }

        public RelayCommand RefreshAppsCommand { get; private set; } = null!;
        public RelayCommand<AppPackageItem> CyclePresenceModeCommand { get; private set; } = null!;
        public RelayCommand<AppPackageItem> ToggleCoverCommand { get; private set; } = null!;

        private void InitApps()
        {
            RefreshAppsCommand = new RelayCommand(() => _ = LoadInstalledAppsAsync());
            CyclePresenceModeCommand = new RelayCommand<AppPackageItem>(item => item?.CyclePresenceMode());
            ToggleCoverCommand = new RelayCommand<AppPackageItem>(item => item?.ToggleCover());

            _ = LoadInstalledAppsAsync();
        }

        private async Task LoadInstalledAppsAsync()
        {
            if (_isLoadingApps) return;
            _isLoadingApps = true;
            AppsStatus = "Loading...";

            try
            {
                var device = await DeviceQuery.ResolveActiveDeviceAsync(_workingConfig).ConfigureAwait(true);
                if (string.IsNullOrWhiteSpace(device))
                {
                    AppsStatus = "No device connected.";
                    return;
                }

                var output = await AdbHelper.RunAdbCaptureAsync($"-s {device} shell pm list packages").ConfigureAwait(true);
                var packages = output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                    .Where(l => l.StartsWith("package:"))
                    .Select(l => l.Substring(8))
                    .OrderBy(l => l)
                    .ToList();

                var eligible = (_workingConfig.EligibleApps ?? new List<EligibleAppConfig>())
                    .Where(a => !string.IsNullOrWhiteSpace(a.PackageName))
                    .GroupBy(a => a.PackageName.Trim(), StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        g => g.Key,
                        g => new EligibleAppConfig
                        {
                            PackageName = g.Key,
                            PresenceMode = g.Max(x => (int)x.PresenceMode) switch { 2 => PresenceMode.Full, 1 => PresenceMode.Half, _ => PresenceMode.Off },
                            IsEnabled = g.Any(x => x.IsEnabled),
                            EnableCoverSearch = g.Any(x => x.EnableCoverSearch)
                        },
                        StringComparer.OrdinalIgnoreCase);

                foreach (var cfgPkg in eligible.Keys)
                {
                    if (!packages.Contains(cfgPkg, StringComparer.OrdinalIgnoreCase))
                        packages.Add(cfgPkg);
                }

                packages = packages.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p).ToList();

                AppPackages.Clear();
                foreach (var pkg in packages)
                {
                    if (eligible.TryGetValue(pkg, out var appConfig))
                        AppPackages.Add(new AppPackageItem(pkg, appConfig.PresenceMode, appConfig.EnableCoverSearch));
                    else
                        AppPackages.Add(new AppPackageItem(pkg, PresenceMode.Off, false));
                }

                AppsStatus = $"{AppPackages.Count} apps";
            }
            catch (Exception ex)
            {
                AppsStatus = "Failed to load apps.";
                Interaction?.ShowWarning($"Failed to load installed apps: {ex.Message}", "Error");
            }
            finally
            {
                _isLoadingApps = false;
            }
        }

        private void CommitAppsToConfig()
        {
            if (AppPackages.Count == 0)
                return;

            _workingConfig.EligibleApps = AppPackages
                .Select(item => new EligibleAppConfig
                {
                    PackageName = item.PackageName,
                    PresenceMode = item.PresenceMode,
                    IsEnabled = item.PresenceMode != PresenceMode.Off,
                    EnableCoverSearch = item.PresenceMode != PresenceMode.Off && item.EnableCoverSearch
                })
                .Where(item => !string.IsNullOrWhiteSpace(item.PackageName))
                .GroupBy(item => item.PackageName, StringComparer.OrdinalIgnoreCase)
                .Select(g => new EligibleAppConfig
                {
                    PackageName = g.Key,
                    PresenceMode = g.Max(x => (int)x.PresenceMode) switch { 2 => PresenceMode.Full, 1 => PresenceMode.Half, _ => PresenceMode.Off },
                    IsEnabled = g.Any(x => x.PresenceMode != PresenceMode.Off || x.IsEnabled),
                    EnableCoverSearch = g.Any(x => x.EnableCoverSearch)
                })
                .ToList();

            _workingConfig.AllowedApps = _workingConfig.EligibleApps
                .Where(a => a.PresenceMode != PresenceMode.Off || a.IsEnabled)
                .Select(a => a.PackageName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private void EnsureEligibleAppsFallback()
        {
            if (!_workingConfig.EligibleApps.Any(a => a.PresenceMode != PresenceMode.Off || a.IsEnabled))
            {
                _workingConfig.EligibleApps.Add(new EligibleAppConfig
                {
                    PackageName = "in.krosbits.musicolet",
                    PresenceMode = PresenceMode.Full,
                    IsEnabled = true,
                    EnableCoverSearch = true
                });
            }

            _workingConfig.AllowedApps = _workingConfig.EligibleApps
                .Where(a => a.PresenceMode != PresenceMode.Off || a.IsEnabled)
                .Select(a => a.PackageName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }
}
