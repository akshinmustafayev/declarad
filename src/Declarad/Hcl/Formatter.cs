using System.Text.RegularExpressions;

namespace Declarad.Hcl
{
    /// <summary>
    /// Canonical formatting (<c>declarad fmt</c>): two-space indentation by nesting depth, one space around
    /// <c>=</c>, aligned <c>=</c> for consecutive attributes, single blank lines, and no blank lines just inside
    /// braces. Heredoc bodies, block comments and string contents are never touched, and the result is checked
    /// to produce exactly the same tokens as the input.
    /// </summary>
    public static partial class Formatter
    {
        [GeneratedRegex(@"^([A-Za-z_][A-Za-z0-9_-]*)\s*=(?![=>])\s*(.*)$")]
        private static partial Regex AttributeLine();

        [GeneratedRegex(@"^([A-Za-z_][A-Za-z0-9_-]*)((?:\s+(?:""(?:[^""\\]|\\.)*""|[A-Za-z_][A-Za-z0-9_-]*))*)\s*\{\s*$")]
        private static partial Regex BlockHeader();

        [GeneratedRegex(@"""(?:[^""\\]|\\.)*""|[A-Za-z_][A-Za-z0-9_-]*")]
        private static partial Regex LabelToken();

        [GeneratedRegex(@"^([A-Za-z_][A-Za-z0-9_-]*)((?:\s+(?:""(?:[^""\\]|\\.)*""|[A-Za-z_][A-Za-z0-9_-]*))*)\s*\{\s*\}(\s*(?:#.*|//.*)?)$")]
        private static partial Regex EmptyBlock();

        private enum Ctx
        {
            String,
            Interpolation,
        }

        /// <summary>
        /// <paramref name="Depth"/>: indent levels open at the start of the line; <paramref name="Dedent"/>: levels closed by
        /// the line's leading closing brackets; <paramref name="Opens"/>: the line leaves brackets open (a multi-line value).
        /// </summary>
        private sealed record LineInfo(string Text, bool Verbatim, int Depth, int Dedent = 0, bool Opens = false);

        public static string Format(string source, string file)
        {
            // Refuse to format files that do not parse; the error points at the problem.
            Parser.ParseFile(source, file);

            string normalized = source.Replace("\r\n", "\n").TrimStart('﻿');
            var infos = Scan(normalized.Split('\n'));
            var output = new List<(string Text, bool IsAttribute, int Depth, string Name, string Value)>();

            foreach (var info in infos)
            {
                if (info.Verbatim)
                {
                    output.Add((info.Text, false, -1, "", ""));
                    continue;
                }
                string trimmed = info.Text.Trim();
                if (trimmed.Length == 0)
                {
                    output.Add(("", false, -1, "", ""));
                    continue;
                }
                int depth = Math.Max(0, info.Depth - info.Dedent);
                string indent = new(' ', depth * 2);

                var empty = EmptyBlock().Match(trimmed);
                if (empty.Success)
                {
                    string emptyLabels = string.Join(" ", LabelToken().Matches(empty.Groups[2].Value).Select(m => m.Value));
                    output.Add((indent + empty.Groups[1].Value + (emptyLabels.Length > 0 ? " " + emptyLabels : "") + " {}" + empty.Groups[3].Value, false, depth, "", ""));
                    continue;
                }
                var header = BlockHeader().Match(trimmed);
                var attr = AttributeLine().Match(trimmed);
                if (header.Success && !trimmed.Contains('='))
                {
                    // Rebuild the labels token by token: spaces inside quoted labels are significant.
                    string labels = string.Join(" ", LabelToken().Matches(header.Groups[2].Value).Select(m => m.Value));
                    output.Add((indent + header.Groups[1].Value + (labels.Length > 0 ? " " + labels : "") + " {", false, depth, "", ""));
                }
                else if (attr.Success && !info.Opens)
                {
                    output.Add((indent, true, depth, attr.Groups[1].Value, attr.Groups[2].Value));
                }
                else if (attr.Success)
                {
                    // Multi-line values are not aligned and end the alignment group, like hclwrite.
                    output.Add(($"{indent}{attr.Groups[1].Value} = {attr.Groups[2].Value}".TrimEnd(), false, depth, "", ""));
                }
                else
                {
                    output.Add((indent + trimmed, false, depth, "", ""));
                }
            }

            // Align '=' within runs of attribute lines at the same depth.
            var lines = new List<string>();
            for (int i = 0; i < output.Count;)
            {
                if (!output[i].IsAttribute)
                {
                    lines.Add(output[i].Text);
                    i++;
                    continue;
                }
                int j = i;
                while (j < output.Count && output[j].IsAttribute && output[j].Depth == output[i].Depth)
                {
                    j++;
                }
                int width = output.Skip(i).Take(j - i).Max(o => o.Name.Length);
                for (int k = i; k < j; k++)
                {
                    lines.Add($"{output[k].Text}{output[k].Name.PadRight(width)} = {output[k].Value}".TrimEnd());
                }
                i = j;
            }

            var result = TidyBlankLines(lines, infos);
            string formatted = string.Join("\n", result).TrimEnd('\n') + "\n";
            Verify(normalized, formatted, file);
            return formatted;
        }

        private static List<string> TidyBlankLines(List<string> lines, List<LineInfo> infos)
        {
            var result = new List<string>();
            for (int i = 0; i < lines.Count; i++)
            {
                string line = lines[i];
                bool verbatim = infos[i].Verbatim;
                if (!verbatim && line.Length == 0)
                {
                    bool previousBlank = result.Count == 0 || result[^1].Length == 0;
                    bool afterOpen = result.Count > 0 && result[^1].TrimEnd().EndsWith('{');
                    int next = i + 1;
                    while (next < lines.Count && lines[next].Length == 0 && !infos[next].Verbatim)
                    {
                        next++;
                    }
                    bool beforeClose = next < lines.Count && !infos[next].Verbatim && lines[next].TrimStart().StartsWith('}');
                    if (previousBlank || afterOpen || beforeClose)
                    {
                        continue;
                    }
                }
                result.Add(verbatim ? line : line.TrimEnd());
            }
            return result;
        }

        /// <summary>Determines, for each line, the bracket depth at its start and whether it must be kept verbatim.</summary>
        private static List<LineInfo> Scan(string[] lines)
        {
            var infos = new List<LineInfo>();
            var stack = new Stack<(Ctx Kind, int Braces)>();
            // Each entry is one indent level: the number of brackets a line opened and left unclosed.
            var levels = new Stack<int>();
            bool blockComment = false;
            string? heredoc = null;
            string? pendingHeredoc = null;

            foreach (string line in lines)
            {
                if (heredoc != null)
                {
                    infos.Add(new LineInfo(line, true, levels.Count));
                    if (line.Trim() == heredoc)
                    {
                        heredoc = null;
                    }
                    continue;
                }

                bool verbatim = blockComment || stack.Count > 0;
                int depthAtStart = levels.Count;
                int dedent = 0;
                int pending = 0;
                bool leading = true;
                for (int i = 0; i < line.Length; i++)
                {
                    char c = line[i];
                    char next = i + 1 < line.Length ? line[i + 1] : '\0';
                    if (blockComment)
                    {
                        if (c == '*' && next == '/')
                        {
                            blockComment = false;
                            i++;
                        }
                        continue;
                    }
                    if (stack.Count > 0 && stack.Peek().Kind == Ctx.String)
                    {
                        if (c == '\\')
                        {
                            i++;
                        }
                        else if (c == '"')
                        {
                            stack.Pop();
                        }
                        else if ((c == '$' || c == '%') && next == c && i + 2 < line.Length && line[i + 2] == '{')
                        {
                            i += 2; // $${ and %%{ are literal text
                        }
                        else if ((c == '$' || c == '%') && next == '{')
                        {
                            stack.Push((Ctx.Interpolation, 0));
                            i++;
                        }
                        continue;
                    }
                    if (stack.Count > 0)
                    {
                        // Inside an interpolation: track braces and nested strings only.
                        var top = stack.Pop();
                        if (c == '"')
                        {
                            stack.Push(top);
                            stack.Push((Ctx.String, 0));
                        }
                        else if (c == '{')
                        {
                            stack.Push((top.Kind, top.Braces + 1));
                        }
                        else if (c == '}')
                        {
                            if (top.Braces > 0)
                            {
                                stack.Push((top.Kind, top.Braces - 1));
                            }
                        }
                        else
                        {
                            stack.Push(top);
                        }
                        continue;
                    }

                    if (c == '#' || (c == '/' && next == '/'))
                    {
                        break;
                    }
                    if (c == '/' && next == '*')
                    {
                        blockComment = true;
                        i++;
                        continue;
                    }
                    if (!char.IsWhiteSpace(c) && c is not ('}' or ']' or ')'))
                    {
                        leading = false;
                    }
                    if (c == '"')
                    {
                        stack.Push((Ctx.String, 0));
                        continue;
                    }
                    if (c == '<' && next == '<')
                    {
                        var m = Regex.Match(line[(i + 2)..], @"^-?([A-Za-z_][A-Za-z0-9_]*)\s*$");
                        if (m.Success)
                        {
                            pendingHeredoc = m.Groups[1].Value;
                            break;
                        }
                    }
                    if (c is '}' or ']' or ')')
                    {
                        if (pending > 0)
                        {
                            pending--;
                        }
                        else if (levels.Count > 0)
                        {
                            int open = levels.Pop() - 1;
                            if (open > 0)
                            {
                                levels.Push(open);
                            }
                            else if (leading)
                            {
                                dedent++;
                            }
                        }
                        continue;
                    }
                    if (c is '{' or '[' or '(')
                    {
                        pending++;
                    }
                }
                if (pending > 0)
                {
                    levels.Push(pending);
                }
                infos.Add(new LineInfo(line, verbatim, depthAtStart, dedent, pending > 0));
                if (pendingHeredoc != null)
                {
                    heredoc = pendingHeredoc;
                    pendingHeredoc = null;
                }
            }
            return infos;
        }

        private static void Verify(string original, string formatted, string file)
        {
            static IEnumerable<string> Tokens(string source, string file) =>
                Lexer.Tokenize(source, file).Where(t => t.Kind != TokenKind.Newline).Select(t => t.Kind + ":" + t.Text + (t.Parts == null ? "" : ":" + string.Join("|", t.Parts.Select(p => p switch
                {
                    LiteralPart l => "L" + l.Text,
                    InterpolationPart ip => (ip.IsDirective ? "D" : "I") + ip.Source.Trim() + ip.StripLeft + ip.StripRight,
                    _ => "",
                }))));

            if (!Tokens(original, file).SequenceEqual(Tokens(formatted, file)))
            {
                throw new DeclaradException($"Internal formatter error: formatting would change the meaning of {file}. Please report this.");
            }
        }

        /// <summary>A minimal line diff (longest common subsequence) for --diff output.</summary>
        public static IEnumerable<string> Diff(string before, string after)
        {
            var a = before.Replace("\r\n", "\n").Split('\n');
            var b = after.Split('\n');
            var lcs = new int[a.Length + 1, b.Length + 1];
            for (int i = a.Length - 1; i >= 0; i--)
            {
                for (int j = b.Length - 1; j >= 0; j--)
                {
                    lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
                }
            }
            int x = 0, y = 0;
            while (x < a.Length || y < b.Length)
            {
                if (x < a.Length && y < b.Length && a[x] == b[y])
                {
                    x++;
                    y++;
                }
                else if (x < a.Length && (y == b.Length || lcs[x + 1, y] >= lcs[x, y + 1]))
                {
                    yield return $"-{x + 1}: {a[x++]}";
                }
                else
                {
                    yield return $"+{y + 1}: {b[y++]}";
                }
            }
        }
    }
}
