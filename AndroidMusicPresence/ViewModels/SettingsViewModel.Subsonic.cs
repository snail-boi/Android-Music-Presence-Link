using System;
using System.Threading.Tasks;

namespace AndroidMusicPresenceLink
{
    /// <summary>
    /// Subsonic group: global server URL / username / password used as a network fallback for
    /// cover art and duration when a streamed track has no local file on the phone. The password
    /// is DPAPI-encrypted (via SecretProtector) into config.Subsonic.EncryptedPassword and never
    /// stored in plaintext. Because PasswordBox.Password can't data-bind, the plaintext lives in
    /// _subsonicPassword, fed from the window's PasswordChanged handler.
    /// </summary>
    internal sealed partial class SettingsViewModel
    {
        private string _subsonicServerUrl = string.Empty;
        public string SubsonicServerUrl { get => _subsonicServerUrl; set => Set(ref _subsonicServerUrl, value); }

        private string _subsonicUsername = string.Empty;
        public string SubsonicUsername { get => _subsonicUsername; set => Set(ref _subsonicUsername, value); }

        // Plaintext password, VM-only. Seeded (decrypted) on load; the window reads this once to
        // populate the PasswordBox, and pushes user edits back via OnSubsonicPasswordEdited.
        private string _subsonicPassword = string.Empty;
        private bool _subsonicPasswordChanged;
        public string SubsonicPassword => _subsonicPassword;

        private string _subsonicTestResultText = string.Empty;
        public string SubsonicTestResultText { get => _subsonicTestResultText; set => Set(ref _subsonicTestResultText, value); }

        public RelayCommand TestSubsonicConnectionCommand { get; private set; } = null!;

        // Called by the window's PasswordChanged handler. Seeding the box with the already-loaded
        // value is a no-op here (equal string), so it never falsely marks the password as changed.
        public void OnSubsonicPasswordEdited(string? password)
        {
            var value = password ?? string.Empty;
            if (string.Equals(_subsonicPassword, value, StringComparison.Ordinal)) return;
            _subsonicPassword = value;
            _subsonicPasswordChanged = true;
        }

        partial void InitSubsonic()
        {
            TestSubsonicConnectionCommand = new RelayCommand(async () => await TestSubsonicConnectionAsync());
            LoadSubsonicFromConfig();
        }

        partial void LoadSubsonicFromConfig()
        {
            _subsonicServerUrl = _config.Subsonic?.ServerUrl ?? string.Empty;
            _subsonicUsername = _config.Subsonic?.Username ?? string.Empty;
            _subsonicPassword = SecretProtector.Unprotect(_config.Subsonic?.EncryptedPassword) ?? string.Empty;
            _subsonicPasswordChanged = false;
        }

        partial void ApplySubsonicToConfig(MusicConfig config)
        {
            config.Subsonic ??= new SubsonicConfig();
            config.Subsonic.ServerUrl = (SubsonicServerUrl ?? string.Empty).Trim();
            config.Subsonic.Username = (SubsonicUsername ?? string.Empty).Trim();

            // Only re-encrypt when the user actually changed the password. DPAPI ciphertext is
            // non-deterministic, so re-encrypting an unchanged password would produce a different
            // blob every BuildConfig() call and register phantom unsaved changes. When unchanged we
            // keep the value already cloned from _config.
            if (_subsonicPasswordChanged)
                config.Subsonic.EncryptedPassword = SecretProtector.Protect(_subsonicPassword);
        }

        // Called from Save() once the built config has been persisted, so later BuildConfig() calls
        // stop re-encrypting and the dirty check settles.
        private void MarkSubsonicPasswordSaved() => _subsonicPasswordChanged = false;

        private async Task TestSubsonicConnectionAsync()
        {
            var url = (SubsonicServerUrl ?? string.Empty).Trim();
            var user = (SubsonicUsername ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(user) || string.IsNullOrEmpty(_subsonicPassword))
            {
                SubsonicTestResultText = "Enter server, username and password first.";
                return;
            }

            SubsonicTestResultText = "Testing...";
            bool ok = await SubsonicClient.PingAsync(url, user, _subsonicPassword).ConfigureAwait(true);
            SubsonicTestResultText = ok ? "Connected" : "Failed to connect";
        }
    }
}
