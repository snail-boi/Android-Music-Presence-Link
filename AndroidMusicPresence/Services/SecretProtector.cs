using System;
using System.Security.Cryptography;
using System.Text;

namespace AndroidMusicPresenceLink
{
    /// <summary>
    /// Thin DPAPI wrapper for encrypting small secrets (currently just the Subsonic password)
    /// at rest inside musicconfig.json. Uses DataProtectionScope.CurrentUser, so the blob is
    /// only decryptable by the same Windows user on the same machine — copying the config to
    /// another profile/PC yields an undecryptable blob, which Unprotect treats as "no secret".
    /// </summary>
    internal static class SecretProtector
    {
        // Base64 of the DPAPI-protected UTF-8 plaintext. Empty in -> empty out.
        internal static string Protect(string? plaintext)
        {
            if (string.IsNullOrEmpty(plaintext))
                return string.Empty;

            try
            {
                var plainBytes = Encoding.UTF8.GetBytes(plaintext);
                var protectedBytes = ProtectedData.Protect(plainBytes, null, DataProtectionScope.CurrentUser);
                return Convert.ToBase64String(protectedBytes);
            }
            catch (Exception ex)
            {
                // Never log the plaintext itself.
                Debugger.show("[SECRET] Protect failed: " + ex.Message);
                return string.Empty;
            }
        }

        // Returns null when there is nothing stored or the blob can't be decrypted (e.g. moved
        // to a different user profile). Callers treat null as "no password configured".
        internal static string? Unprotect(string? encryptedBase64)
        {
            if (string.IsNullOrEmpty(encryptedBase64))
                return null;

            try
            {
                var protectedBytes = Convert.FromBase64String(encryptedBase64);
                var plainBytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plainBytes);
            }
            catch (Exception ex)
            {
                Debugger.show("[SECRET] Unprotect failed: " + ex.Message);
                return null;
            }
        }
    }
}
