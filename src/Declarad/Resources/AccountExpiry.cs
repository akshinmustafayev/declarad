using Declarad.Hcl.Values;

namespace Declarad.Resources
{
    /// <summary>accountExpires is a FILETIME; 0 and 0x7FFFFFFFFFFFFFFF both mean "never".</summary>
    internal static class AccountExpiry
    {
        public const string Never = "never";

        public static bool TryParse(string text, out DateTime? date)
        {
            date = null;
            if (text.Equals(Never, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (DateTime.TryParseExact(text, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var d))
            {
                date = d;
                return true;
            }
            return false;
        }

        public static Value Normalize(Value v) =>
            v is StringValue s && TryParse(s.V, out var d) ? Value.From(d == null ? Never : d.Value.ToString("yyyy-MM-dd")) : v;

        public static string ToLdap(string text) =>
            TryParse(text, out var d) && d != null ? d.Value.ToFileTimeUtc().ToString() : "9223372036854775807";

        public static Value FromLdap(string? raw)
        {
            if (raw == null || !long.TryParse(raw, out long ft) || ft == 0 || ft == long.MaxValue)
            {
                return Value.From(Never);
            }
            return Value.From(DateTime.FromFileTimeUtc(ft).ToString("yyyy-MM-dd"));
        }
    }
}
