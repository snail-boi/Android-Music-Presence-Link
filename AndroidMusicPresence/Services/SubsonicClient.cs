using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AndroidMusicPresenceLink
{
    /// <summary>
    /// Minimal client for Subsonic-API-compatible servers (Navidrome, Airsonic, Gonic, ...).
    /// Used as a network fallback for cover art + duration when a streamed track has no local
    /// file on the phone. Modeled on Updater.cs: local disposable HttpClient, System.Text.Json
    /// JsonDocument parsing, no DI, every public call wrapped so it returns a "no result"
    /// sentinel instead of throwing. The raw password is never logged.
    /// </summary>
    internal static class SubsonicClient
    {
        private const string ApiVersion = "1.16.1";
        private const string ClientName = "AndroidMusicPresenceLink";
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(8);

        internal sealed record SongMatch(string Id, string? CoverArtId, double? DurationSeconds, string Title, string Artist);

        // Resolves the currently-playing title/artist to the best matching library song.
        // Returns null on any failure (network, auth, malformed response, no match).
        internal static async Task<SongMatch?> Search3Async(
            string serverUrl, string username, string password, string title, string artist,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(serverUrl) || string.IsNullOrWhiteSpace(username)
                || string.IsNullOrEmpty(password) || string.IsNullOrWhiteSpace(title))
                return null;

            try
            {
                // Query by TITLE only. Subsonic search3 does a token-based full-text match, so
                // folding in a messy multi-artist string (commas, "feat.", symbols) makes the
                // server require all those noise tokens and returns nothing. We search the title
                // and disambiguate by artist client-side in ScoreMatch.
                var query = title.Trim();
                string url = BuildUrl(serverUrl, username, password, "search3",
                    $"&query={Uri.EscapeDataString(query)}&songCount=50&artistCount=0&albumCount=0");

                using var client = new HttpClient { Timeout = RequestTimeout };
                client.DefaultRequestHeaders.UserAgent.ParseAdd(ClientName + "/1.0");

                string json = await client.GetStringAsync(url, ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);

                if (!TryGetOkResponse(doc, out var response, out string? error))
                {
                    Debugger.show($"[SUBSONIC] search3 rejected for title '{title}': {error}");
                    return null;
                }

                if (!response.TryGetProperty("searchResult3", out var result)
                    || !result.TryGetProperty("song", out var songs)
                    || songs.ValueKind != JsonValueKind.Array
                    || songs.GetArrayLength() == 0)
                {
                    Debugger.show($"[SUBSONIC] No song match for title '{title}' (artist '{artist}').");
                    return null;
                }

                Debugger.show($"[SUBSONIC] search3 for title '{title}' returned {songs.GetArrayLength()} candidate(s).");

                SongMatch? best = null;
                int bestScore = int.MinValue;
                foreach (var song in songs.EnumerateArray())
                {
                    string id = GetString(song, "id");
                    if (string.IsNullOrEmpty(id))
                        continue;

                    string songTitle = GetString(song, "title");
                    string songArtist = GetString(song, "artist");
                    int score = ScoreMatch(title, artist, songTitle, songArtist);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        double? duration = null;
                        if (song.TryGetProperty("duration", out var dur))
                        {
                            if (dur.ValueKind == JsonValueKind.Number && dur.TryGetDouble(out var d))
                                duration = d;
                            else if (dur.ValueKind == JsonValueKind.String
                                && double.TryParse(dur.GetString(), out var ds))
                                duration = ds;
                        }
                        string? coverArtId = song.TryGetProperty("coverArt", out var ca) ? ca.GetString() : null;
                        best = new SongMatch(id, coverArtId, duration, songTitle, songArtist);
                    }
                }

                if (best != null)
                    Debugger.show($"[SUBSONIC] Matched '{artist} - {title}' -> song id {best.Id} (dur {best.DurationSeconds?.ToString() ?? "?"}s).");
                return best;
            }
            catch (Exception ex)
            {
                Debugger.show($"[SUBSONIC] search3 failed: {ex.Message}");
                return null;
            }
        }

        // Downloads cover art bytes for a coverArt id to destPath. Returns destPath on success,
        // null on failure (including when the server returns a JSON/XML error instead of an image).
        internal static async Task<string?> DownloadCoverArtAsync(
            string serverUrl, string username, string password, string coverArtId, string destPath,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(coverArtId) || string.IsNullOrWhiteSpace(destPath))
                return null;

            try
            {
                string url = BuildUrl(serverUrl, username, password, "getCoverArt",
                    $"&id={Uri.EscapeDataString(coverArtId)}");

                using var client = new HttpClient { Timeout = RequestTimeout };
                client.DefaultRequestHeaders.UserAgent.ParseAdd(ClientName + "/1.0");

                using var resp = await client.GetAsync(url, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    Debugger.show($"[SUBSONIC] getCoverArt HTTP {(int)resp.StatusCode}.");
                    return null;
                }

                var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                if (bytes.Length == 0)
                    return null;

                // On failure the server returns a subsonic-response document instead of image
                // bytes. Rather than trust the content-type header (some servers mislabel images
                // as octet-stream), reject only payloads that actually look like a JSON/XML error.
                if (LooksLikeErrorDocument(bytes))
                {
                    Debugger.show("[SUBSONIC] getCoverArt returned an error document, not an image.");
                    return null;
                }

                await File.WriteAllBytesAsync(destPath, bytes, ct).ConfigureAwait(false);
                return destPath;
            }
            catch (Exception ex)
            {
                Debugger.show($"[SUBSONIC] getCoverArt failed: {ex.Message}");
                return null;
            }
        }

        // Lightweight connectivity/auth check for the "Test connection" button.
        internal static async Task<bool> PingAsync(
            string serverUrl, string username, string password, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(serverUrl) || string.IsNullOrWhiteSpace(username)
                || string.IsNullOrEmpty(password))
                return false;

            try
            {
                string url = BuildUrl(serverUrl, username, password, "ping", string.Empty);
                using var client = new HttpClient { Timeout = RequestTimeout };
                client.DefaultRequestHeaders.UserAgent.ParseAdd(ClientName + "/1.0");

                string json = await client.GetStringAsync(url, ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                bool ok = TryGetOkResponse(doc, out _, out string? error);
                if (!ok)
                    Debugger.show($"[SUBSONIC] ping failed: {error}");
                return ok;
            }
            catch (Exception ex)
            {
                Debugger.show($"[SUBSONIC] ping failed: {ex.Message}");
                return false;
            }
        }

        // Builds "<server>/rest/<method>.view?u=..&t=..&s=..&v=..&c=..&f=json<extra>".
        // Token auth: t = md5(password + salt), fresh random salt per request — the raw
        // password is never placed in the URL.
        private static string BuildUrl(string serverUrl, string username, string password, string method, string extra)
        {
            string baseUri = serverUrl.Trim().TrimEnd('/');
            var (token, salt) = BuildAuthTokenAndSalt(password);
            return $"{baseUri}/rest/{method}.view?u={Uri.EscapeDataString(username)}"
                 + $"&t={token}&s={salt}&v={ApiVersion}&c={ClientName}&f=json{extra}";
        }

        // A Subsonic error reply is a small JSON ({...) or XML (<...) document. Real image bytes
        // start with a binary magic number (JPEG FF D8, PNG 89 50, GIF 47 49, WEBP "RIFF"), none
        // of which begin with '{' or '<' after optional whitespace/BOM.
        private static bool LooksLikeErrorDocument(byte[] bytes)
        {
            int i = 0;
            // Skip a UTF-8 BOM if present.
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                i = 3;
            while (i < bytes.Length && (bytes[i] == (byte)' ' || bytes[i] == (byte)'\t'
                || bytes[i] == (byte)'\r' || bytes[i] == (byte)'\n'))
                i++;
            if (i >= bytes.Length)
                return false;
            return bytes[i] == (byte)'{' || bytes[i] == (byte)'<';
        }

        private static (string token, string salt) BuildAuthTokenAndSalt(string password)
        {
            string salt = Guid.NewGuid().ToString("N").Substring(0, 16);
            byte[] hash = MD5.HashData(Encoding.UTF8.GetBytes(password + salt));
            return (Convert.ToHexString(hash).ToLowerInvariant(), salt);
        }

        // Unwraps the "subsonic-response" envelope. Returns true only when status == "ok".
        private static bool TryGetOkResponse(JsonDocument doc, out JsonElement response, out string? error)
        {
            response = default;
            error = null;

            if (!doc.RootElement.TryGetProperty("subsonic-response", out response))
            {
                error = "missing subsonic-response envelope";
                return false;
            }

            string status = GetString(response, "status");
            if (string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase))
                return true;

            if (response.TryGetProperty("error", out var errElem))
            {
                string code = errElem.TryGetProperty("code", out var c) ? c.ToString() : "?";
                string msg = GetString(errElem, "message");
                error = $"code {code}: {msg}";
            }
            else
            {
                error = "status " + status;
            }
            return false;
        }

        private static int ScoreMatch(string wantTitle, string wantArtist, string songTitle, string songArtist)
        {
            int score = 0;
            string wt = Normalize(wantTitle);
            string wa = Normalize(wantArtist);
            string st = Normalize(songTitle);
            string sa = Normalize(songArtist);

            if (st == wt) score += 4;
            else if (st.Contains(wt) || wt.Contains(st)) score += 2;

            if (!string.IsNullOrEmpty(wa))
            {
                if (sa == wa) score += 3;
                else if (sa.Contains(wa) || wa.Contains(sa)) score += 1;
            }
            return score;
        }

        private static string Normalize(string? s)
            => (s ?? string.Empty).Trim().ToLowerInvariant();

        private static string GetString(JsonElement element, string property)
            => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;
    }
}
