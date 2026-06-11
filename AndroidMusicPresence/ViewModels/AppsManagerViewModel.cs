using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace AndroidMusicPresenceLink
{
    /// <summary>
    /// ViewModel for AppsManagerWindow. It owns the list of app rows, loads installed
    /// packages from the device (merged with whatever is already saved in config), and
    /// builds the updated config on Save.
    ///
    /// It is internal because it exposes AppPackageItem, which is internal and shared with
    /// the other windows. The View (code-behind) hands it a delegate for "which device is
    /// connected" so the VM does not have to reach into App itself.
    /// </summary>
    internal sealed class AppsManagerViewModel : ViewModelBase
    {
        private readonly MusicConfig _config;
        private readonly Action<MusicConfig> _onSaved;
        private readonly Func<string> _getCurrentDevice;

        // Raised when the dialog should close. The bool becomes DialogResult.
        public event Action<bool>? RequestClose;

        // The rows shown in the ListBox. ObservableCollection tells the bound ListBox to
        // add/remove items on its own as we change this list, with no code-behind.
        public ObservableCollection<AppPackageItem> Items { get; } = new();

        private string _loadStatus = string.Empty;
        public string LoadStatus
        {
            get => _loadStatus;
            set => Set(ref _loadStatus, value);
        }

        // Per-row buttons. The clicked row is passed in as the command parameter.
        public RelayCommand<AppPackageItem> CyclePresenceModeCommand { get; }
        public RelayCommand<AppPackageItem> ToggleCoverCommand { get; }
        public RelayCommand SaveCommand { get; }

        public AppsManagerViewModel(MusicConfig config, Action<MusicConfig> onSaved, Func<string> getCurrentDevice)
        {
            _config = config;
            _onSaved = onSaved;
            _getCurrentDevice = getCurrentDevice;

            CyclePresenceModeCommand = new RelayCommand<AppPackageItem>(item => item?.CyclePresenceMode());
            ToggleCoverCommand = new RelayCommand<AppPackageItem>(item => item?.ToggleCover());
            SaveCommand = new RelayCommand(Save);
        }

        public async Task LoadAppsAsync()
        {
            LoadStatus = "Loading installed apps...";

            try
            {
                var device = _getCurrentDevice();

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

                Items.Clear();
                foreach (var pkg in allPackages)
                {
                    if (savedApps.TryGetValue(pkg, out var saved))
                        Items.Add(new AppPackageItem(pkg, saved.PresenceMode, saved.EnableCoverSearch));
                    else
                        Items.Add(new AppPackageItem(pkg, PresenceMode.Off, false));
                }

                LoadStatus = $"{Items.Count} apps";
            }
            catch (Exception ex)
            {
                LoadStatus = "Failed to load apps.";
                Debugger.show($"AppsManagerWindow load failed: {ex.Message}");
            }
        }

        private void Save()
        {
            var updatedConfig = new MusicConfig
            {
                EligibleApps = Items
                    .Where(i => i.PresenceMode != PresenceMode.Off || i.EnableCoverSearch)
                    .Select(i => new EligibleAppConfig
                    {
                        PackageName = i.PackageName,
                        PresenceMode = i.PresenceMode,
                        EnableCoverSearch = i.EnableCoverSearch
                    })
                    .ToList(),
                AllowedApps = Items
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
            RequestClose?.Invoke(true);
        }
    }
}
