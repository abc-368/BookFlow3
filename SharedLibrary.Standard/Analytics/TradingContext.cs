using System;
using System.Text.RegularExpressions;

namespace BookFlow.Shared.Analytics
{
    public enum TradingSession : byte { Asia = 0, London = 1, US = 2 }

    /// <summary>
    /// Time-of-day session classification (US/Eastern basis) and futures instrument-name helpers.
    /// Sessions are contiguous, non-overlapping, and cover 24h; boundaries are coarse defaults
    /// (tunable later). Pure + deterministic so they're unit-testable.
    /// </summary>
    public static class TradingContext
    {
        /// <summary>Classify by Eastern-time hour: London 03–08, US 08–17, Asia otherwise (17–03).</summary>
        public static TradingSession Classify(DateTime easternTime)
        {
            int h = easternTime.Hour;
            if (h >= 8 && h < 17) return TradingSession.US;
            if (h >= 3 && h < 8) return TradingSession.London;
            return TradingSession.Asia;
        }

        /// <summary>Current session from UtcNow, converted to US/Eastern (falls back to UTC if the TZ is unavailable).</summary>
        public static TradingSession CurrentSession(DateTime utcNow)
        {
            DateTime et;
            try
            {
                var tz = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
                et = TimeZoneInfo.ConvertTimeFromUtc(utcNow, tz);
            }
            catch { et = utcNow; }
            return Classify(et);
        }

        /// <summary>
        /// Root / continuous symbol from a futures contract name: "ES 06-26" → "ES", "MES 12-25" → "MES".
        /// Names without a recognizable expiry suffix are returned unchanged (trimmed).
        /// </summary>
        public static string Root(string fullName)
        {
            if (string.IsNullOrWhiteSpace(fullName)) return fullName ?? string.Empty;
            var s = fullName.Trim();
            var m = Regex.Match(s, @"^(.+?)\s+\d{1,2}-\d{2,4}$");
            return m.Success ? m.Groups[1].Value : s;
        }
    }
}
