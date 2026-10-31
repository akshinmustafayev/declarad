namespace Declarad.Tests.Support
{
    /// <summary>Evaluates the LDAP filters declarad issues: &amp;, |, ! and equality.</summary>
    internal static class FilterParser
    {
        public static Func<IDictionary<string, List<string>>, bool> Parse(string filter)
        {
            int i = 0;
            return ParseFilter(filter, ref i);
        }

        private static Func<IDictionary<string, List<string>>, bool> ParseFilter(string s, ref int i)
        {
            if (s[i] != '(')
            {
                throw new FormatException(s);
            }
            i++;
            char op = s[i];
            if (op is '&' or '|')
            {
                i++;
                var parts = new List<Func<IDictionary<string, List<string>>, bool>>();
                while (s[i] == '(')
                {
                    parts.Add(ParseFilter(s, ref i));
                }
                i++;
                return op == '&' ? a => parts.All(p => p(a)) : a => parts.Any(p => p(a));
            }
            if (op == '!')
            {
                i++;
                var inner = ParseFilter(s, ref i);
                i++;
                return a => !inner(a);
            }
            int end = s.IndexOf(')', i);
            string item = s[i..end];
            i = end + 1;
            int eq = item.IndexOf('=');
            string attr = item[..eq];
            string value = System.Text.RegularExpressions.Regex.Replace(item[(eq + 1)..], @"\\([0-9a-fA-F]{2})", m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());
            if (value == "*")
            {
                return a => a.ContainsKey(attr);
            }
            return a => a.TryGetValue(attr, out var vals) && vals.Any(v => string.Equals(v, value, StringComparison.OrdinalIgnoreCase));
        }
    }
}
