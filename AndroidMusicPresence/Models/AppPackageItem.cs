using System.ComponentModel;

namespace AndroidMusicPresenceLink
{
    /// <summary>
    /// View-model row for a single Android package in the allowed-apps lists. Shared by the
    /// main settings window, the apps manager and onboarding. Display formatting is delegated
    /// to MainWindow.FormatPackageName.
    /// </summary>
    internal sealed class AppPackageItem : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        public string PackageName { get; }
        public string DisplayName => MainWindow.FormatPackageName(PackageName);

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
