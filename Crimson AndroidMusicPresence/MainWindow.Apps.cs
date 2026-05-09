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
        private void BtnRefreshApps_Click(object sender, RoutedEventArgs e)
        {
            _ = LoadInstalledAppsAsync();
        }

        private void ApplyAllowedAppsSelection()
        {
            if (_appPackages.Count == 0)
                return;

            var eligible = new Dictionary<string, EligibleAppConfig>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in _config.EligibleApps ?? new List<EligibleAppConfig>())
            {
                if (string.IsNullOrWhiteSpace(item.PackageName))
                    continue;

                eligible[item.PackageName.Trim()] = item;
            }

            foreach (var item in _appPackages)
            {
                if (eligible.TryGetValue(item.PackageName, out var appConfig))
                {
                    item.IsSelected = appConfig.IsEnabled;
                    item.IsCoverSearchEnabled = appConfig.EnableCoverSearch;
                }
                else
                {
                    item.IsSelected = false;
                    item.IsCoverSearchEnabled = false;
                }
            }
        }

        private async Task LoadInstalledAppsAsync()
        {
            if (_isLoadingApps) return;
            _isLoadingApps = true;
            TxtAppsStatus.Text = "Loading...";

            try
            {
                var device = await GetCurrentDeviceForAppsAsync().ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(device))
                {
                    await Dispatcher.InvokeAsync(() => TxtAppsStatus.Text = "No device connected.");
                    return;
                }

                var output = await AdbHelper.RunAdbCaptureAsync($"-s {device} shell pm list packages").ConfigureAwait(false);
                var packages = output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                    .Where(l => l.StartsWith("package:"))
                    .Select(l => l.Substring(8))
                    .OrderBy(l => l)
                    .ToList();

                var eligible = (_config.EligibleApps ?? new List<EligibleAppConfig>())
                    .Where(a => !string.IsNullOrWhiteSpace(a.PackageName))
                    .GroupBy(a => a.PackageName.Trim(), StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        g => g.Key,
                        g => new EligibleAppConfig
                        {
                            PackageName = g.Key,
                            IsEnabled = g.Any(x => x.IsEnabled),
                            EnableCoverSearch = g.Any(x => x.EnableCoverSearch)
                        },
                        StringComparer.OrdinalIgnoreCase);

                foreach (var cfgPkg in eligible.Keys)
                {
                    if (!packages.Contains(cfgPkg, StringComparer.OrdinalIgnoreCase))
                    {
                        packages.Add(cfgPkg);
                    }
                }

                packages = packages
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(p => p)
                    .ToList();

                await Dispatcher.InvokeAsync(() =>
                {
                    _appPackages.Clear();
                    foreach (var pkg in packages)
                    {
                        if (eligible.TryGetValue(pkg, out var appConfig))
                        {
                            _appPackages.Add(new AppPackageItem(pkg, appConfig.IsEnabled, appConfig.EnableCoverSearch));
                        }
                        else
                        {
                            _appPackages.Add(new AppPackageItem(pkg, false, false));
                        }
                    }

                    TxtAppsStatus.Text = $"{_appPackages.Count} apps";
                });
            }
            catch (Exception ex)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    TxtAppsStatus.Text = "Failed to load apps.";
                    MessageBox.Show($"Failed to load installed apps: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                });
            }
            finally
            {
                _isLoadingApps = false;
            }
        }
        private sealed class AppPackageItem : INotifyPropertyChanged
        {
            public event PropertyChangedEventHandler? PropertyChanged;

            public string PackageName { get; }

            private bool _isSelected;
            public bool IsSelected
            {
                get => _isSelected;
                set
                {
                    if (_isSelected == value) return;
                    _isSelected = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
                }
            }

            private bool _isCoverSearchEnabled;
            public bool IsCoverSearchEnabled
            {
                get => _isCoverSearchEnabled;
                set
                {
                    if (_isCoverSearchEnabled == value) return;
                    _isCoverSearchEnabled = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCoverSearchEnabled)));
                }
            }

            public AppPackageItem(string packageName, bool isSelected, bool isCoverSearchEnabled)
            {
                PackageName = packageName;
                _isSelected = isSelected;
                _isCoverSearchEnabled = isCoverSearchEnabled;
            }
        }
    }
}
