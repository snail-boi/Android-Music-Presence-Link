using System;
using System.Globalization;

namespace AndroidMusicPresenceLink
{
    /// <summary>
    /// Compact timestamp encoding for the persisted library lists. The list sources
    /// (adb `stat %Y`, Subsonic "created") are both second-precision, so storing a full
    /// ISO-8601 round-trip string ("2023-11-04T15:22:31.0000000Z", 28 chars) wastes ~18
    /// bytes per song on a fractional part that is always zero. Epoch seconds (~10 chars)
    /// carry the same information. <see cref="Parse"/> still accepts the old ISO strings so
    /// existing list files keep loading until the next rescan rewrites them.
    /// </summary>
    internal static class LibraryTime
    {
        /// <summary>Formats a timestamp as Unix epoch seconds. DateTime.MinValue becomes "0".</summary>
        public static string ToStorage(DateTime value)
        {
            if (value == DateTime.MinValue) return "0";

            // Treat an unspecified kind as UTC — the list managers already work in UTC.
            var utc = value.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
                : value.ToUniversalTime();

            long seconds = new DateTimeOffset(utc).ToUnixTimeSeconds();
            return seconds.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Parses a stored timestamp. New files hold epoch seconds; legacy files hold an
        /// ISO-8601 string, so fall back to a date parse. Unparseable/empty -> MinValue.
        /// </summary>
        public static DateTime Parse(string? stored)
        {
            if (string.IsNullOrWhiteSpace(stored)) return DateTime.MinValue;
            stored = stored.Trim();

            // Epoch seconds are pure digits; an ISO string never is, so this is unambiguous.
            if (long.TryParse(stored, NumberStyles.Integer, CultureInfo.InvariantCulture, out long epoch))
                return epoch <= 0 ? DateTime.MinValue : DateTimeOffset.FromUnixTimeSeconds(epoch).UtcDateTime;

            if (DateTime.TryParse(stored, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt))
                return dt;

            return DateTime.MinValue;
        }
    }
}
