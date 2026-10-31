using System.Globalization;
using System.Text;

namespace Declarad.Directory
{
    /// <summary>Distinguished-name helpers following RFC 4514 escaping rules.</summary>
    public static class Dn
    {
        public sealed record Rdn(string Type, string Value)
        {
            public override string ToString() => $"{Type}={EscapeValue(Value)}";
        }

        public static string EscapeValue(string value)
        {
            var sb = new StringBuilder(value.Length);
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                bool escape = c is ',' or '+' or '"' or '\\' or '<' or '>' or ';' or '='
                    || (i == 0 && (c == ' ' || c == '#'))
                    || (i == value.Length - 1 && c == ' ');
                if (c == '\0')
                {
                    sb.Append("\\00");
                }
                else
                {
                    if (escape)
                    {
                        sb.Append('\\');
                    }
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }

        /// <summary>Splits a DN into its RDNs, unescaping each value.</summary>
        public static List<Rdn> Parse(string dn)
        {
            var result = new List<Rdn>();
            if (string.IsNullOrWhiteSpace(dn))
            {
                return result;
            }

            int i = 0;
            while (i < dn.Length)
            {
                while (i < dn.Length && dn[i] == ' ')
                {
                    i++;
                }
                int typeStart = i;
                while (i < dn.Length && dn[i] != '=')
                {
                    if (dn[i] == ',')
                    {
                        throw new DeclaradException($"Invalid distinguished name '{dn}': missing '=' in component.");
                    }
                    i++;
                }
                if (i >= dn.Length)
                {
                    throw new DeclaradException($"Invalid distinguished name '{dn}': missing '=' in component.");
                }
                string type = dn[typeStart..i].Trim();
                if (type.Length == 0)
                {
                    throw new DeclaradException($"Invalid distinguished name '{dn}': empty attribute type.");
                }
                i++; // '='
                while (i < dn.Length && dn[i] == ' ')
                {
                    i++; // Unescaped leading spaces are insignificant.
                }

                var value = new List<byte>();
                var raw = new StringBuilder();
                int lastSignificant = 0;
                while (i < dn.Length && dn[i] != ',')
                {
                    char c = dn[i];
                    if (c == '\\')
                    {
                        if (i + 1 >= dn.Length)
                        {
                            throw new DeclaradException($"Invalid distinguished name '{dn}': dangling escape.");
                        }
                        if (i + 2 < dn.Length && char.IsAsciiHexDigit(dn[i + 1]) && char.IsAsciiHexDigit(dn[i + 2]))
                        {
                            // Hex escapes encode UTF-8 bytes; flush pending text first.
                            FlushText(raw, value);
                            value.Add(byte.Parse(dn.AsSpan(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                            i += 3;
                        }
                        else
                        {
                            raw.Append(dn[i + 1]);
                            i += 2;
                        }
                        FlushText(raw, value);
                        lastSignificant = value.Count;
                        continue;
                    }
                    raw.Append(c);
                    i++;
                    if (c != ' ')
                    {
                        FlushText(raw, value);
                        lastSignificant = value.Count;
                    }
                }
                FlushText(raw, value);
                // Unescaped trailing spaces are insignificant.
                string text = Encoding.UTF8.GetString(value.Take(lastSignificant).ToArray());
                result.Add(new Rdn(type, text));
                if (i < dn.Length)
                {
                    i++; // ','
                    if (i >= dn.Length)
                    {
                        throw new DeclaradException($"Invalid distinguished name '{dn}': trailing comma.");
                    }
                }
            }
            return result;
        }

        private static void FlushText(StringBuilder raw, List<byte> bytes)
        {
            if (raw.Length > 0)
            {
                bytes.AddRange(Encoding.UTF8.GetBytes(raw.ToString()));
                raw.Clear();
            }
        }

        public static bool IsValid(string? dn)
        {
            if (string.IsNullOrWhiteSpace(dn))
            {
                return false;
            }
            try
            {
                return Parse(dn).Count > 0;
            }
            catch (DeclaradException)
            {
                return false;
            }
        }

        public static string Format(IEnumerable<Rdn> rdns) => string.Join(",", rdns.Select(r => r.ToString()));

        public static string Combine(string type, string value, string parentDn) =>
            string.IsNullOrEmpty(parentDn) ? $"{type}={EscapeValue(value)}" : $"{type}={EscapeValue(value)},{parentDn}";

        public static string Parent(string dn) => Format(Parse(dn).Skip(1));

        public static Rdn First(string dn) => Parse(dn).FirstOrDefault()
            ?? throw new DeclaradException($"Invalid distinguished name '{dn}'.");

        /// <summary>Canonical form for comparisons: unescaped then re-escaped, upper-case types, lower-case values.</summary>
        public static string Normalize(string dn)
        {
            try
            {
                return string.Join(",", Parse(dn).Select(r => $"{r.Type.ToUpperInvariant()}={EscapeValue(r.Value).ToLowerInvariant()}"));
            }
            catch (DeclaradException)
            {
                return dn.Trim().ToLowerInvariant();
            }
        }

        public static bool AreEqual(string? a, string? b) =>
            a == null || b == null ? a == b : Normalize(a) == Normalize(b);

        public static int Depth(string dn) => Parse(dn).Count;

        public static bool IsDescendantOf(string dn, string ancestorDn)
        {
            string d = Normalize(dn);
            string a = Normalize(ancestorDn);
            return d.Length > a.Length && d.EndsWith("," + a, StringComparison.Ordinal);
        }

        public static string FromDomain(string domain) =>
            string.Join(",", domain.Split('.', StringSplitOptions.RemoveEmptyEntries).Select(p => $"DC={EscapeValue(p)}"));

        public static string? ToDomain(string dn)
        {
            var dcs = Parse(dn).Where(r => r.Type.Equals("DC", StringComparison.OrdinalIgnoreCase)).Select(r => r.Value).ToList();
            return dcs.Count == 0 ? null : string.Join(".", dcs);
        }

        public static string EscapeFilterValue(string value)
        {
            var sb = new StringBuilder(value.Length);
            foreach (char c in value)
            {
                sb.Append(c switch
                {
                    '*' => "\\2a",
                    '(' => "\\28",
                    ')' => "\\29",
                    '\\' => "\\5c",
                    '\0' => "\\00",
                    _ => c.ToString(),
                });
            }
            return sb.ToString();
        }
    }
}
