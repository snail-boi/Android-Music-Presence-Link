using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace musicpresense
{
    public partial class AppsManagerWindow : Window
    {
        private readonly MusicConfig _config;
        private readonly Action<MusicConfig> _onSaved;
        private readonly ObservableCollection<AppPackageItem> _items = new();

        public AppsManagerWindow(MusicConfig config, Action<MusicConfig> onSaved)
        {
            InitializeComponent();
            _config = config;
            _onSaved = onSaved;

            LstApps.ItemsSource = _items;
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _ = LoadAppsAsync();
        }

        private async Task LoadAppsAsync()
        {
            TxtLoadStatus.Text = "Loading installed apps...";

            try
            {
                var device = (Application.Current as App)?.GetCurrentDevice() ?? string.Empty;

                List<string> installedPackages = new();

                if (!string.IsNullOrWhiteSpace(device))
                {
                    var output = await AdbHelper.RunAdbCaptureAsync($"-s {device} shell pm list packages")
                        .ConfigureAwait(true);

                    installedPackages = output
                        .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                        .Where(l => l.StartsWith("package:"))
                        .Select(l => l.Substring(8).Trim())
                        .ToList();
                }

                // Build a combined list: installed packages + any already saved in config
                var savedApps = (_config.EligibleApps ?? new List<EligibleAppConfig>())
                    .Where(a => !string.IsNullOrWhiteSpace(a.PackageName))
                    .ToDictionary(a => a.PackageName.Trim(), StringComparer.OrdinalIgnoreCase);

                var allPackages = installedPackages
                    .Union(savedApps.Keys, StringComparer.OrdinalIgnoreCase)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(p => p)
                    .ToList();

                _items.Clear();
                foreach (var pkg in allPackages)
                {
                    if (savedApps.TryGetValue(pkg, out var saved))
                        _items.Add(new AppPackageItem(pkg, saved.PresenceMode, saved.EnableCoverSearch));
                    else
                        _items.Add(new AppPackageItem(pkg, PresenceMode.Off, false));
                }

                TxtLoadStatus.Text = $"{_items.Count} apps";
            }
            catch (Exception ex)
            {
                TxtLoadStatus.Text = "Failed to load apps.";
                Debugger.show($"AppsManagerWindow load failed: {ex.Message}");
            }
        }

        private void BtnPresenceMode_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is AppPackageItem item)
                item.CyclePresenceMode();
        }

        private void BtnCover_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is AppPackageItem item)
                item.ToggleCover();
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            var updatedConfig = new MusicConfig
            {
                EligibleApps = _items
                    .Where(i => i.PresenceMode != PresenceMode.Off || i.EnableCoverSearch)
                    .Select(i => new EligibleAppConfig
                    {
                        PackageName = i.PackageName,
                        PresenceMode = i.PresenceMode,
                        EnableCoverSearch = i.EnableCoverSearch
                    })
                    .ToList(),
                AllowedApps = _items
                    .Where(i => i.PresenceMode != PresenceMode.Off)
                    .Select(i => i.PackageName)
                    .ToList()
            };

            // Merge back any existing config entries that weren't loaded (e.g. no device connected)
            foreach (var existing in _config.EligibleApps ?? new List<EligibleAppConfig>())
            {
                if (!updatedConfig.EligibleApps.Any(a =>
                    string.Equals(a.PackageName, existing.PackageName, StringComparison.OrdinalIgnoreCase)))
                {
                    updatedConfig.EligibleApps.Add(existing);
                }
            }

            _onSaved(updatedConfig);
            DialogResult = true;
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}