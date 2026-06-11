using System;
using System.Collections.ObjectModel;

namespace AndroidMusicPresenceLink
{
    /// <summary>
    /// ViewModel for the onboarding wizard, split into partial-class files by concern:
    ///   OnboardingViewModel.Core      (this file): step navigation, sidebar, finish/commit
    ///   OnboardingViewModel.Device    : USB/Wi-Fi connection, auto-detect, pairing
    ///   OnboardingViewModel.Folders   : music remote roots and lyrics folder
    ///   OnboardingViewModel.Apps      : allowed apps list
    ///   OnboardingViewModel.Hotkeys   : hotkey recording and modifier
    ///
    /// It is internal because it exposes AppPackageItem (an internal shared type).
    ///
    /// The old SaveCurrentStepValues is gone: every step binds directly to this VM, so there
    /// is nothing to scrape when navigating. Values are written into the working config only
    /// at Finish, via the CommitXxxToConfig methods each partial provides.
    /// </summary>
    internal sealed partial class OnboardingViewModel : ViewModelBase
    {
        private readonly MusicConfig _workingConfig;

        private readonly string[] _stepTitles =
        {
            "Welcome",
            "Enable wireless debugging",
            "Connect your phone",
            "Music and lyrics folders",
            "Allowed apps",
            "Hotkeys",
            "Startup options"
        };

        private readonly string[] _stepSubtitles =
        {
            "Welcome to Android Music Presence. Let's get you set up.",
            "Your phone needs Wireless Debugging turned on so we can talk to it.",
            "Plug in your phone, then click Auto Detect and we'll figure out the rest.",
            "Tell us where your music lives so we can fetch cover art and lyrics.",
            "Choose which Android apps may share what they're playing.",
            "Set the keyboard shortcuts for volume, lyrics and more.",
            "Decide how the app launches. You can change everything later in Settings."
        };

        // Read back by the caller after a true DialogResult.
        public MusicConfig UpdatedConfig => _workingConfig;

        // Raised when the dialog should close. The bool becomes DialogResult.
        public event Action<bool>? RequestClose;

        // Bound to the sidebar ListBox. Rebuilt on every step change.
        public ObservableCollection<SidebarStepItem> SidebarSteps { get; } = new();

        public RelayCommand BackCommand { get; }
        public RelayCommand NextCommand { get; }
        public RelayCommand SkipCommand { get; }
        public RelayCommand FinishCommand { get; }
        public RelayCommand CancelCommand { get; }

        public OnboardingViewModel(MusicConfig currentConfig)
        {
            _workingConfig = currentConfig.Clone();

            BackCommand = new RelayCommand(GoBack);
            NextCommand = new RelayCommand(GoNext);
            SkipCommand = new RelayCommand(Skip);
            FinishCommand = new RelayCommand(Finish);
            CancelCommand = new RelayCommand(() => RequestClose?.Invoke(false));

            // Each concern seeds its own bound state from the working config.
            InitDevice();
            InitFolders();
            InitStartup();
            InitHotkeys();
            InitApps(); // also kicks off the async installed-apps load

            RebuildSidebar();
        }

        private int _currentStep;
        public int CurrentStep
        {
            get => _currentStep;
            private set
            {
                if (!Set(ref _currentStep, value)) return;

                RaisePropertyChanged(nameof(StepTitle));
                RaisePropertyChanged(nameof(StepSubtitle));
                RaisePropertyChanged(nameof(StepCounter));
                RaisePropertyChanged(nameof(CanGoBack));
                RaisePropertyChanged(nameof(IsNextVisible));
                RaisePropertyChanged(nameof(IsFinishVisible));
                RaisePropertyChanged(nameof(IsSkipVisible));
                RaisePropertyChanged(nameof(SkipText));
                RaisePropertyChanged(nameof(NextText));

                RebuildSidebar();
            }
        }

        public string StepTitle => _stepTitles[_currentStep];
        public string StepSubtitle => _stepSubtitles[_currentStep];
        public string StepCounter => $"Step {_currentStep + 1} of {_stepTitles.Length}";

        public bool CanGoBack => _currentStep > 0;
        public bool IsNextVisible => _currentStep < _stepTitles.Length - 1;
        public bool IsFinishVisible => _currentStep == _stepTitles.Length - 1;
        public bool IsSkipVisible => _currentStep != 0;
        public string SkipText => IsFinishVisible ? "Skip & Finish" : "Skip";
        public string NextText => _currentStep == 0 ? "Get Started  \u25B6" : "Next  \u25B6";

        private void GoBack()
        {
            if (_currentStep > 0)
                CurrentStep--;
        }

        private void GoNext()
        {
            if (_currentStep < _stepTitles.Length - 1)
                CurrentStep++;
        }

        private void Skip()
        {
            if (IsFinishVisible)
            {
                Finish();
                return;
            }
            GoNext();
        }

        private void Finish()
        {
            // Each partial writes its own slice of state into the working config.
            CommitDeviceToConfig();
            CommitFoldersToConfig();
            CommitStartupToConfig();
            CommitHotkeysToConfig();
            CommitAppsToConfig();
            EnsureEligibleAppsFallback();

            _workingConfig.OnboardingCompleted = true;
            RequestClose?.Invoke(true);
        }

        private void RebuildSidebar()
        {
            SidebarSteps.Clear();
            for (int i = 0; i < _stepTitles.Length; i++)
            {
                bool isCurrent = i == _currentStep;
                bool isDone = i < _currentStep;

                SidebarSteps.Add(new SidebarStepItem
                {
                    Title = _stepTitles[i],
                    NumberText = isDone ? "\u2713" : (i + 1).ToString(),
                    NumberBackground = isCurrent ? "#FFFFFF" : (isDone ? "#66FFFFFF" : "#33FFFFFF"),
                    NumberForeground = isCurrent ? "#2D6CDF" : "#FFFFFF",
                    RowBackground = isCurrent ? "#33FFFFFF" : "#00000000",
                    TitleOpacity = isCurrent ? 1.0 : (isDone ? 0.85 : 0.7),
                    TitleWeight = isCurrent ? "SemiBold" : "Normal"
                });
            }
        }
    }
}
