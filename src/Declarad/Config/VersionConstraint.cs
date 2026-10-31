namespace Declarad.Config
{
    /// <summary>Terraform-style version constraints: "&gt;= 0.2, &lt; 1.0", "~&gt; 0.2", "= 0.2.1", "!= 0.3".</summary>
    public static class VersionConstraint
    {
        public static bool Satisfied(string constraints, string version)
        {
            var v = Parse(version);
            foreach (string part in constraints.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string op = new string(part.TakeWhile(c => "<>=!~".Contains(c)).ToArray());
                string text = part[op.Length..].Trim();
                var target = Parse(text);
                int cmp = Compare(v, target);
                bool ok = op switch
                {
                    "" or "=" => cmp == 0,
                    "!=" => cmp != 0,
                    ">" => cmp > 0,
                    ">=" => cmp >= 0,
                    "<" => cmp < 0,
                    "<=" => cmp <= 0,
                    // ~> 1.2 allows >= 1.2, < 2.0; ~> 1.2.3 allows >= 1.2.3, < 1.3.0.
                    "~>" => cmp >= 0 && Compare(v, Bump(target, text.Count(c => c == '.'))) < 0,
                    _ => throw new DeclaradException($"Invalid version constraint operator '{op}' in \"{constraints}\"."),
                };
                if (!ok)
                {
                    return false;
                }
            }
            return true;
        }

        private static int[] Parse(string text)
        {
            var parts = text.Split('-')[0].Split('.');
            var result = new int[3];
            for (int i = 0; i < Math.Min(3, parts.Length); i++)
            {
                if (!int.TryParse(parts[i], out result[i]))
                {
                    throw new DeclaradException($"Invalid version '{text}'.");
                }
            }
            return result;
        }

        private static int[] Bump(int[] v, int dots)
        {
            var bumped = new int[3];
            int index = Math.Max(0, dots - 1);
            Array.Copy(v, bumped, index);
            bumped[index] = v[index] + 1;
            return bumped;
        }

        private static int Compare(int[] a, int[] b)
        {
            for (int i = 0; i < 3; i++)
            {
                if (a[i] != b[i])
                {
                    return a[i].CompareTo(b[i]);
                }
            }
            return 0;
        }
    }
}
