using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Declarad.Hcl.Values;

namespace Declarad.Resources.Schema
{
    /// <summary>
    /// AD stores durations such as password ages as negative 100-nanosecond intervals. Configuration uses
    /// "42d", "1d12h", "30m", "90s", "0" or "never"; values are normalized to the canonical form.
    /// </summary>
    public static partial class AdInterval
    {
        public const string Never = "never";

        [GeneratedRegex(@"^(?:(\d+)d)?(?:(\d+)h)?(?:(\d+)m)?(?:(\d+)s)?$")]
        private static partial Regex Pattern();

        public static bool TryParse(string text, out TimeSpan? span)
        {
            span = null;
            text = text.Trim().ToLowerInvariant();
            if (text == Never)
            {
                return true;
            }
            if (text == "0")
            {
                span = TimeSpan.Zero;
                return true;
            }
            var m = Pattern().Match(text);
            if (!m.Success || text.Length == 0)
            {
                return false;
            }
            try
            {
                long Part(int i) => m.Groups[i].Success ? long.Parse(m.Groups[i].Value, CultureInfo.InvariantCulture) : 0;
                span = TimeSpan.FromDays(Part(1)) + TimeSpan.FromHours(Part(2)) + TimeSpan.FromMinutes(Part(3)) + TimeSpan.FromSeconds(Part(4));
                return true;
            }
            catch (Exception ex) when (ex is OverflowException or ArgumentOutOfRangeException)
            {
                return false;
            }
        }

        public static string Format(TimeSpan? span)
        {
            if (span == null)
            {
                return Never;
            }
            var t = span.Value;
            if (t == TimeSpan.Zero)
            {
                return "0";
            }
            var sb = new StringBuilder();
            if (t.Days > 0)
            {
                sb.Append(CultureInfo.InvariantCulture, $"{(long)t.TotalDays}d");
            }
            if (t.Hours > 0)
            {
                sb.Append(CultureInfo.InvariantCulture, $"{t.Hours}h");
            }
            if (t.Minutes > 0)
            {
                sb.Append(CultureInfo.InvariantCulture, $"{t.Minutes}m");
            }
            if (t.Seconds > 0)
            {
                sb.Append(CultureInfo.InvariantCulture, $"{t.Seconds}s");
            }
            return sb.ToString();
        }

        public static string ToLdap(string text)
        {
            if (!TryParse(text, out var span))
            {
                throw new DeclaradException($"'{text}' is not a valid duration (examples: 42d, 1d12h, 30m, 0, never).");
            }
            return span == null ? long.MinValue.ToString(CultureInfo.InvariantCulture) : (-span.Value.Ticks).ToString(CultureInfo.InvariantCulture);
        }

        public static string FromLdap(string ticks)
        {
            long n = long.Parse(ticks, CultureInfo.InvariantCulture);
            return n == long.MinValue ? Never : Format(TimeSpan.FromTicks(Math.Abs(n)));
        }

        public static Value Normalize(Value v) =>
            v is StringValue s && TryParse(s.V, out var span) ? Value.From(Format(span)) : v;

        public static string? Validate(Value v) =>
            TryParse(V.Str(v) ?? "", out _) ? null : $"'{V.Str(v)}' is not a valid duration (examples: 42d, 1d12h, 30m, 0, never).";
    }
}
