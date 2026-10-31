using System.Text;
using System.Text.RegularExpressions;

namespace Declarad.Hcl
{
    /// <summary>Helpers for emitting HCL source text (used by plan rendering and export).</summary>
    public static partial class HclWriter
    {
        [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_-]*$")]
        private static partial Regex IdentifierRegex();

        public static bool IsIdentifier(string s) => IdentifierRegex().IsMatch(s);

        public static string Key(string key) => IsIdentifier(key) ? key : Quote(key);

        public static string Quote(string s)
        {
            var sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '$' when i + 1 < s.Length && s[i + 1] == '{':
                        sb.Append("$$");
                        break;
                    case '%' when i + 1 < s.Length && s[i + 1] == '{':
                        sb.Append("%%");
                        break;
                    default:
                        if (char.IsControl(c))
                        {
                            sb.Append($"\\u{(int)c:X4}");
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        /// <summary>Turns an arbitrary directory name into a valid resource label ("Sales Team" → "sales_team").</summary>
        public static string ToLabel(string name)
        {
            var sb = new StringBuilder();
            foreach (char c in name.ToLowerInvariant())
            {
                if (char.IsAsciiLetterOrDigit(c))
                {
                    sb.Append(c);
                }
                else if (sb.Length > 0 && sb[^1] != '_')
                {
                    sb.Append('_');
                }
            }
            string label = sb.ToString().Trim('_');
            if (label.Length == 0)
            {
                label = "item";
            }
            if (char.IsAsciiDigit(label[0]))
            {
                label = "_" + label;
            }
            return label;
        }
    }
}
