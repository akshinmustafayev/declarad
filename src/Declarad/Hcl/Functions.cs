using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Declarad.Directory;
using Declarad.Hcl.Values;

namespace Declarad.Hcl
{
    /// <summary>
    /// Built-in functions. Names and behaviour follow Terraform's standard library, plus
    /// <c>env()</c> and distinguished-name helpers.
    /// </summary>
    public sealed class Functions
    {
        private sealed class Args
        {
            private readonly IReadOnlyList<Value> _values;
            public string Name { get; }
            public SourcePos Pos { get; }

            public Args(string name, IReadOnlyList<Value> values, SourcePos pos)
            {
                Name = name;
                _values = values;
                Pos = pos;
            }

            public int Count => _values.Count;
            public IReadOnlyList<Value> All => _values;
            public Value this[int i] => i < _values.Count ? _values[i] : throw Error($"is missing argument {i + 1}.");
            public DeclaradException Error(string message) => new($"{Name}(): {message}", Pos);

            public string Str(int i) => this[i] switch
            {
                StringValue s => s.V,
                NumberValue n => n.ToHcl(),
                BoolValue b => b.ToHcl(),
                var v => throw Error($"argument {i + 1} must be a string, got {v.TypeName}."),
            };

            public decimal Num(int i) => this[i] switch
            {
                NumberValue n => n.V,
                StringValue s when decimal.TryParse(s.V, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
                var v => throw Error($"argument {i + 1} must be a number, got {v.TypeName}."),
            };

            public int Int(int i)
            {
                decimal d = Num(i);
                return decimal.Truncate(d) == d ? (int)d : throw Error($"argument {i + 1} must be a whole number.");
            }

            public bool Bool(int i) => this[i] switch
            {
                BoolValue b => b.V,
                StringValue { V: "true" } => true,
                StringValue { V: "false" } => false,
                var v => throw Error($"argument {i + 1} must be a bool, got {v.TypeName}."),
            };

            public IReadOnlyList<Value> List(int i) => this[i] switch
            {
                ListValue l => l.Items,
                NullValue => Array.Empty<Value>(),
                var v => throw Error($"argument {i + 1} must be a list, got {v.TypeName}."),
            };

            public ObjectValue Obj(int i) => this[i] switch
            {
                ObjectValue o => o,
                NullValue => new ObjectValue(Array.Empty<KeyValuePair<string, Value>>()),
                var v => throw Error($"argument {i + 1} must be an object or map, got {v.TypeName}."),
            };
        }

        private sealed record Fn(int Min, int Max, Func<Args, Value> Impl);

        private readonly string _baseDir;
        private readonly Func<string, string?> _getEnv;
        private readonly Dictionary<string, Fn> _table = new(StringComparer.Ordinal);

        public Functions(string baseDir, Func<string, string?>? getEnv = null)
        {
            _baseDir = baseDir;
            _getEnv = getEnv ?? Environment.GetEnvironmentVariable;
            RegisterStrings();
            RegisterCollections();
            RegisterNumbers();
            RegisterConversions();
            RegisterEncoding();
            RegisterFilesystem();
            RegisterDirectory();
        }

        public IEnumerable<string> Names => _table.Keys.Order(StringComparer.Ordinal);

        private static readonly HashSet<string> StructuralFunctions = new(StringComparer.Ordinal)
        {
            "length", "keys", "values", "merge", "concat", "lookup", "element", "slice", "reverse", "tolist", "tomap", "one", "chunklist", "coalescelist",
        };

        /// <summary>
        /// Functions whose result only depends on the shape of collections, so they can run when some elements are
        /// unknown (e.g. length() of a list with unknown items is known). Any argument that is itself unknown
        /// still makes the result unknown.
        /// </summary>
        public static bool AcceptsUnknown(string name) => StructuralFunctions.Contains(name);

        public Value Call(string name, IReadOnlyList<Value> args, SourcePos pos)
        {
            if (!_table.TryGetValue(name, out var fn))
            {
                throw new DeclaradException($"Unknown function '{name}'. Run 'declarad schema' for the list.", pos);
            }
            if (args.Count < fn.Min || args.Count > fn.Max)
            {
                string expected = fn.Min == fn.Max ? $"{fn.Min}" : fn.Max == int.MaxValue ? $"at least {fn.Min}" : $"{fn.Min}-{fn.Max}";
                throw new DeclaradException($"{name}() takes {expected} argument(s), got {args.Count}.", pos);
            }
            return fn.Impl(new Args(name, args, pos));
        }

        private void Reg(string name, int min, int max, Func<Args, Value> impl) => _table[name] = new Fn(min, max, impl);

        private static Value Strings(IEnumerable<string> items) => Value.FromStrings(items);

        // ---- Strings ---------------------------------------------------------------------------------

        private void RegisterStrings()
        {
            Reg("lower", 1, 1, a => Value.From(a.Str(0).ToLowerInvariant()));
            Reg("upper", 1, 1, a => Value.From(a.Str(0).ToUpperInvariant()));
            Reg("title", 1, 1, a => Value.From(Regex.Replace(a.Str(0), @"\b\p{L}", m => m.Value.ToUpperInvariant())));
            Reg("trimspace", 1, 1, a => Value.From(a.Str(0).Trim()));
            Reg("trim", 2, 2, a => Value.From(a.Str(0).Trim(a.Str(1).ToCharArray())));
            Reg("trimprefix", 2, 2, a => Value.From(a.Str(0).StartsWith(a.Str(1), StringComparison.Ordinal) ? a.Str(0)[a.Str(1).Length..] : a.Str(0)));
            Reg("trimsuffix", 2, 2, a => Value.From(a.Str(0).EndsWith(a.Str(1), StringComparison.Ordinal) ? a.Str(0)[..^a.Str(1).Length] : a.Str(0)));
            Reg("chomp", 1, 1, a => Value.From(a.Str(0).TrimEnd('\r', '\n')));
            Reg("strrev", 1, 1, a =>
            {
                var elements = new List<string>();
                var e = System.Globalization.StringInfo.GetTextElementEnumerator(a.Str(0));
                while (e.MoveNext())
                {
                    elements.Add(e.GetTextElement());
                }
                elements.Reverse();
                return Value.From(string.Concat(elements));
            });
            Reg("startswith", 2, 2, a => Value.From(a.Str(0).StartsWith(a.Str(1), StringComparison.Ordinal)));
            Reg("endswith", 2, 2, a => Value.From(a.Str(0).EndsWith(a.Str(1), StringComparison.Ordinal)));
            Reg("strcontains", 2, 2, a => Value.From(a.Str(0).Contains(a.Str(1), StringComparison.Ordinal)));
            Reg("split", 2, 2, a => a.Str(0).Length == 0
                ? Strings(a.Str(1).EnumerateRunes().Select(r => r.ToString()))
                : Strings(a.Str(1).Split(a.Str(0))));
            Reg("join", 2, int.MaxValue, a => Value.From(string.Join(a.Str(0), Enumerable.Range(1, a.Count - 1).SelectMany(a.List).Where(v => !v.IsNull).Select(v => Evaluator.ToStringValue(v, a.Pos, "join element")))));
            Reg("indent", 2, 2, a => Value.From(a.Str(1).Replace("\n", "\n" + new string(' ', a.Int(0)))));
            Reg("substr", 3, 3, a =>
            {
                var runes = a.Str(0).EnumerateRunes().ToArray();
                int offset = a.Int(1), length = a.Int(2);
                if (offset < 0)
                {
                    offset = Math.Max(0, runes.Length + offset);
                }
                if (offset > runes.Length)
                {
                    throw a.Error("offset is beyond the end of the string.");
                }
                if (length < 0 || offset + length > runes.Length)
                {
                    length = runes.Length - offset;
                }
                return Value.From(string.Concat(runes.Skip(offset).Take(length).Select(r => r.ToString())));
            });
            Reg("replace", 3, 3, a =>
            {
                string s = a.Str(0), sub = a.Str(1), repl = a.Str(2);
                if (sub.Length > 1 && sub.StartsWith('/') && sub.EndsWith('/'))
                {
                    return Value.From(MakeRegex(a, sub[1..^1]).Replace(s, repl));
                }
                return sub.Length == 0 ? Value.From(s) : Value.From(s.Replace(sub, repl, StringComparison.Ordinal));
            });
            Reg("regex", 2, 2, a =>
            {
                var m = MakeRegex(a, a.Str(0)).Match(a.Str(1));
                return m.Success ? MatchValue(m) : throw a.Error("pattern did not match.");
            });
            Reg("regexall", 2, 2, a => Value.From(MakeRegex(a, a.Str(0)).Matches(a.Str(1)).Select(MatchValue)));
            Reg("format", 1, int.MaxValue, a => Value.From(Format(a, a.Str(0), a.All.Skip(1).ToList())));
            Reg("formatlist", 1, int.MaxValue, a =>
            {
                var rest = a.All.Skip(1).ToList();
                int n = rest.OfType<ListValue>().Select(l => l.Items.Length).DefaultIfEmpty(1).Max();
                if (rest.OfType<ListValue>().Any(l => l.Items.Length != n))
                {
                    throw a.Error("all list arguments must have the same length.");
                }
                return Value.From(Enumerable.Range(0, n).Select(i =>
                    Value.From(Format(a, a.Str(0), rest.Select(v => v is ListValue l ? l.Items[i] : v).ToList()))));
            });
        }

        private static Regex MakeRegex(Args a, string pattern)
        {
            try
            {
                // RE2 (Terraform) writes named groups as (?P<name>...); .NET uses (?<name>...).
                return new Regex(pattern.Replace("(?P<", "(?<"), RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
            }
            catch (ArgumentException ex)
            {
                throw a.Error($"invalid regular expression: {ex.Message}");
            }
        }

        private static Value MatchValue(Match m)
        {
            var groups = m.Groups.Cast<Group>().Skip(1).ToList();
            if (groups.Count == 0)
            {
                return Value.From(m.Value);
            }
            if (groups.Any(g => !int.TryParse(g.Name, out _)))
            {
                return new ObjectValue(groups.Where(g => !int.TryParse(g.Name, out _))
                    .Select(g => new KeyValuePair<string, Value>(g.Name, g.Success ? Value.From(g.Value) : Value.Null)));
            }
            return Value.From(groups.Select(g => g.Success ? Value.From(g.Value) : Value.Null));
        }

        /// <summary>Implements the format() verbs %s %d %f %v %q %t %x %X %o %b %e %%, with flags, width and precision.</summary>
        private static string Format(Args a, string spec, IReadOnlyList<Value> values)
        {
            var sb = new StringBuilder();
            int next = 0;
            bool explicitIndex = false;
            for (int i = 0; i < spec.Length; i++)
            {
                if (spec[i] != '%')
                {
                    sb.Append(spec[i]);
                    continue;
                }
                if (++i >= spec.Length)
                {
                    throw a.Error("format string ends with '%'.");
                }
                if (spec[i] == '%')
                {
                    sb.Append('%');
                    continue;
                }
                bool left = false, zero = false, plus = false;
                for (; i < spec.Length && "-0+ ".Contains(spec[i]); i++)
                {
                    left |= spec[i] == '-';
                    zero |= spec[i] == '0';
                    plus |= spec[i] == '+';
                }
                int width = 0;
                for (; i < spec.Length && char.IsAsciiDigit(spec[i]); i++)
                {
                    width = width * 10 + (spec[i] - '0');
                }
                int? precision = null;
                if (i < spec.Length && spec[i] == '.')
                {
                    precision = 0;
                    for (i++; i < spec.Length && char.IsAsciiDigit(spec[i]); i++)
                    {
                        precision = precision * 10 + (spec[i] - '0');
                    }
                }
                if (i < spec.Length && spec[i] == '[')
                {
                    int close = spec.IndexOf(']', i);
                    if (close < 0 || !int.TryParse(spec.AsSpan(i + 1, close - i - 1), out int argIndex) || argIndex < 1)
                    {
                        throw a.Error("invalid explicit argument index in format string.");
                    }
                    next = argIndex - 1;
                    explicitIndex = true;
                    i = close + 1;
                }
                if (i >= spec.Length)
                {
                    throw a.Error("incomplete format verb.");
                }
                char verb = spec[i];
                if (next >= values.Count)
                {
                    throw a.Error($"not enough arguments for format string \"{spec}\".");
                }
                var v = values[next++];
                string text = verb switch
                {
                    's' => v is ListValue or ObjectValue ? v.ToHcl() : Evaluator.ToStringValue(v, a.Pos, "format argument"),
                    'v' => v is StringValue sv ? sv.V : v.ToHcl(),
                    'q' => HclWriter.Quote(Evaluator.ToStringValue(v, a.Pos, "format argument")),
                    't' => Evaluator.ToBool(v, a.Pos) ? "true" : "false",
                    'd' => FormatInt(a, v, 10, plus),
                    'x' => FormatInt(a, v, 16, false),
                    'X' => FormatInt(a, v, 16, false).ToUpperInvariant(),
                    'o' => FormatInt(a, v, 8, false),
                    'b' => FormatInt(a, v, 2, false),
                    'f' => (plus && Evaluator.ToNumber(v, a.Pos) >= 0 ? "+" : "") + Evaluator.ToNumber(v, a.Pos).ToString("F" + (precision ?? 6), CultureInfo.InvariantCulture),
                    'e' => ((double)Evaluator.ToNumber(v, a.Pos)).ToString((precision ?? 6) == 0 ? "0e+00" : "0." + new string('0', precision ?? 6) + "e+00", CultureInfo.InvariantCulture),
                    _ => throw a.Error($"unsupported format verb '%{verb}'."),
                };
                if (precision != null && verb is 's' or 'v' && text.Length > precision)
                {
                    text = text[..precision.Value];
                }
                if (text.Length < width)
                {
                    text = left ? text.PadRight(width)
                        : zero && verb is 'd' or 'f' or 'x' or 'X' or 'o' or 'b' ? PadNumber(text, width)
                        : text.PadLeft(width);
                }
                sb.Append(text);
            }
            if (next < values.Count && !explicitIndex)
            {
                throw a.Error($"too many arguments for format string \"{spec}\".");
            }
            return sb.ToString();
        }

        private static string PadNumber(string text, int width) =>
            text.StartsWith('-') || text.StartsWith('+') ? text[0] + text[1..].PadLeft(width - 1, '0') : text.PadLeft(width, '0');

        private static string FormatInt(Args a, Value v, int radix, bool plus)
        {
            decimal d = Evaluator.ToNumber(v, a.Pos);
            if (decimal.Truncate(d) != d)
            {
                throw a.Error($"value {NumberValue.Format(d)} is not a whole number.");
            }
            long n = (long)d;
            string digits = radix == 10 ? Math.Abs(n).ToString(CultureInfo.InvariantCulture) : Convert.ToString(Math.Abs(n), radix);
            return (n < 0 ? "-" : plus ? "+" : "") + digits;
        }

        // ---- Collections -----------------------------------------------------------------------------

        private void RegisterCollections()
        {
            Reg("length", 1, 1, a => a[0] switch
            {
                ListValue l => Value.From(l.Items.Length),
                ObjectValue o => Value.From(o.Fields.Count),
                StringValue s => Value.From(s.V.EnumerateRunes().Count()),
                var v => throw a.Error($"cannot take the length of a {v.TypeName}."),
            });
            Reg("element", 2, 2, a =>
            {
                var l = a.List(0);
                if (l.Count == 0)
                {
                    throw a.Error("cannot use element() on an empty list.");
                }
                int i = a.Int(1);
                return i < 0 ? throw a.Error("index must not be negative.") : l[i % l.Count];
            });
            Reg("index", 2, 2, a =>
            {
                int i = a.List(0).ToList().IndexOf(a[1]);
                return i >= 0 ? Value.From(i) : throw a.Error("item not found in list.");
            });
            Reg("contains", 2, 2, a => Value.From(a.List(0).Contains(a[1])));
            Reg("concat", 0, int.MaxValue, a => Value.From(Enumerable.Range(0, a.Count).SelectMany(a.List)));
            Reg("flatten", 1, 1, a => Value.From(Flatten(a.List(0))));
            Reg("distinct", 1, 1, a => Value.From(a.List(0).Distinct()));
            Reg("compact", 1, 1, a => Value.From(a.List(0).Where(v => !v.IsNull && v is not StringValue { V: "" })));
            Reg("reverse", 1, 1, a => Value.From(a.List(0).Reverse()));
            Reg("sort", 1, 1, a => Strings(a.List(0).Select(v => Evaluator.ToStringValue(v, a.Pos, "sort element")).Order(StringComparer.Ordinal)));
            Reg("slice", 3, 3, a =>
            {
                var l = a.List(0);
                int start = a.Int(1), end = a.Int(2);
                if (start < 0 || end > l.Count || start > end)
                {
                    throw a.Error($"invalid range [{start}, {end}) for a list of {l.Count} elements.");
                }
                return Value.From(l.Skip(start).Take(end - start));
            });
            Reg("range", 1, 3, a =>
            {
                decimal start = a.Count > 1 ? a.Num(0) : 0, end = a.Count > 1 ? a.Num(1) : a.Num(0), step = a.Count > 2 ? a.Num(2) : (start <= end ? 1 : -1);
                if (step == 0)
                {
                    throw a.Error("step must not be zero.");
                }
                var result = new List<Value>();
                for (decimal x = start; step > 0 ? x < end : x > end; x += step)
                {
                    if (result.Count >= 1024)
                    {
                        throw a.Error("more than 1024 elements requested.");
                    }
                    result.Add(Value.From(x));
                }
                return Value.From(result);
            });
            Reg("chunklist", 2, 2, a =>
            {
                int size = a.Int(1);
                var l = a.List(0);
                return size <= 0 ? Value.From(new[] { Value.From(l) }) : Value.From(l.Chunk(size).Select(c => Value.From(c)));
            });
            Reg("keys", 1, 1, a => Strings(a.Obj(0).Fields.Keys));
            Reg("values", 1, 1, a => Value.From(a.Obj(0).Fields.Values));
            Reg("lookup", 2, 3, a =>
            {
                if (a.Obj(0).Fields.TryGetValue(a.Str(1), out var v))
                {
                    return v;
                }
                return a.Count == 3 ? a[2] : throw a.Error($"key '{a.Str(1)}' not found.");
            });
            Reg("merge", 0, int.MaxValue, a =>
            {
                var result = new Dictionary<string, Value>(StringComparer.Ordinal);
                for (int i = 0; i < a.Count; i++)
                {
                    foreach (var (k, v) in a.Obj(i).Fields)
                    {
                        result[k] = v;
                    }
                }
                return new ObjectValue(result);
            });
            Reg("zipmap", 2, 2, a =>
            {
                var keys = a.List(0);
                var values = a.List(1);
                if (keys.Count != values.Count)
                {
                    throw a.Error("the key and value lists must have the same length.");
                }
                var result = new Dictionary<string, Value>(StringComparer.Ordinal);
                for (int i = 0; i < keys.Count; i++)
                {
                    result[Evaluator.ToStringValue(keys[i], a.Pos, "key")] = values[i];
                }
                return new ObjectValue(result);
            });
            Reg("coalesce", 1, int.MaxValue, a => a.All.FirstOrDefault(v => !v.IsNull && v is not StringValue { V: "" }) ?? throw a.Error("all arguments are null or empty."));
            Reg("coalescelist", 1, int.MaxValue, a => a.All.FirstOrDefault(v => v is ListValue { Items.Length: > 0 }) ?? Value.From(Array.Empty<Value>()));
            Reg("one", 1, 1, a => a.List(0).Count switch
            {
                0 => Value.Null,
                1 => a.List(0)[0],
                var n => throw a.Error($"the list has {n} elements; at most one is allowed."),
            });
            Reg("alltrue", 1, 1, a => Value.From(a.List(0).All(v => Evaluator.ToBool(v, a.Pos))));
            Reg("anytrue", 1, 1, a => Value.From(a.List(0).Any(v => Evaluator.ToBool(v, a.Pos))));
            Reg("sum", 1, 1, a => a.List(0).Count == 0 ? throw a.Error("cannot sum an empty list.") : Value.From(a.List(0).Sum(v => Evaluator.ToNumber(v, a.Pos))));
            Reg("toset", 1, 1, a => ToSet(a.List(0)));
            Reg("setunion", 1, int.MaxValue, a => ToSet(Enumerable.Range(0, a.Count).SelectMany(a.List)));
            Reg("setintersection", 1, int.MaxValue, a => ToSet(Enumerable.Range(1, a.Count - 1).Aggregate(a.List(0).AsEnumerable(), (acc, i) => acc.Intersect(a.List(i)))));
            Reg("setsubtract", 2, 2, a => ToSet(a.List(0).Except(a.List(1))));
            Reg("setproduct", 1, int.MaxValue, a =>
            {
                IEnumerable<IEnumerable<Value>> product = new[] { Enumerable.Empty<Value>() };
                for (int i = 0; i < a.Count; i++)
                {
                    var items = a.List(i);
                    product = product.SelectMany(p => items.Select(item => p.Append(item)));
                }
                return Value.From(product.Select(p => Value.From(p)));
            });
            Reg("transpose", 1, 1, a =>
            {
                var result = new SortedDictionary<string, List<Value>>(StringComparer.Ordinal);
                foreach (var (k, v) in a.Obj(0).Fields)
                {
                    foreach (var item in v is ListValue l ? l.Items : throw a.Error("values must be lists of strings."))
                    {
                        string key = Evaluator.ToStringValue(item, a.Pos, "element");
                        (result.TryGetValue(key, out var list) ? list : result[key] = new List<Value>()).Add(Value.From(k));
                    }
                }
                return new ObjectValue(result.Select(kv => new KeyValuePair<string, Value>(kv.Key, Value.From(kv.Value))));
            });
        }

        private static IEnumerable<Value> Flatten(IEnumerable<Value> items) =>
            items.SelectMany(i => i is ListValue l ? Flatten(l.Items) : new[] { i });

        /// <summary>Sets are lists without duplicates; string and number sets are sorted, as in Terraform.</summary>
        private static Value ToSet(IEnumerable<Value> items) => Value.From(SetOrder(items));

        internal static IEnumerable<Value> SetOrder(IEnumerable<Value> items)
        {
            var distinct = items.Distinct().ToList();
            if (distinct.All(v => v is StringValue))
            {
                return distinct.OrderBy(v => ((StringValue)v).V, StringComparer.Ordinal);
            }
            if (distinct.All(v => v is NumberValue))
            {
                return distinct.OrderBy(v => ((NumberValue)v).V);
            }
            return distinct;
        }

        // ---- Numbers ---------------------------------------------------------------------------------

        private void RegisterNumbers()
        {
            Reg("abs", 1, 1, a => Value.From(Math.Abs(a.Num(0))));
            Reg("ceil", 1, 1, a => Value.From(Math.Ceiling(a.Num(0))));
            Reg("floor", 1, 1, a => Value.From(Math.Floor(a.Num(0))));
            Reg("signum", 1, 1, a => Value.From(Math.Sign(a.Num(0))));
            Reg("max", 1, int.MaxValue, a => Value.From(Enumerable.Range(0, a.Count).Select(a.Num).Max()));
            Reg("min", 1, int.MaxValue, a => Value.From(Enumerable.Range(0, a.Count).Select(a.Num).Min()));
            Reg("pow", 2, 2, a => Value.From(Pow(a.Num(0), a.Num(1))));
            Reg("log", 2, 2, a => Value.From((decimal)Math.Log((double)a.Num(0), (double)a.Num(1))));
            Reg("parseint", 2, 2, a => Value.From(ParseInt(a, a.Str(0), a.Int(1))));
        }

        /// <summary>Exact for whole exponents; otherwise via double.</summary>
        private static decimal Pow(decimal x, decimal y)
        {
            if (decimal.Truncate(y) == y && Math.Abs(y) <= 1000)
            {
                decimal result = 1;
                for (int i = 0; i < Math.Abs((int)y); i++)
                {
                    result = checked(result * x);
                }
                return y < 0 ? 1 / result : result;
            }
            return (decimal)Math.Pow((double)x, (double)y);
        }

        /// <summary>Like Terraform's parseint: bases 2-62, optional sign, no prefixes.</summary>
        private static decimal ParseInt(Args a, string text, int radix)
        {
            if (radix is < 2 or > 62)
            {
                throw a.Error("base must be between 2 and 62.");
            }
            string digits = text.StartsWith('-') || text.StartsWith('+') ? text[1..] : text;
            if (digits.Length == 0)
            {
                throw a.Error($"cannot parse '{text}' as a base-{radix} integer.");
            }
            System.Numerics.BigInteger value = 0;
            foreach (char c in digits)
            {
                int digit = c switch
                {
                    >= '0' and <= '9' => c - '0',
                    >= 'a' and <= 'z' => c - 'a' + 10,
                    >= 'A' and <= 'Z' => radix <= 36 ? c - 'A' + 10 : c - 'A' + 36,
                    _ => 99,
                };
                if (digit >= radix)
                {
                    throw a.Error($"cannot parse '{text}' as a base-{radix} integer.");
                }
                value = value * radix + digit;
            }
            if (text.StartsWith('-'))
            {
                value = -value;
            }
            if (value > new System.Numerics.BigInteger(decimal.MaxValue) || value < new System.Numerics.BigInteger(decimal.MinValue))
            {
                throw a.Error($"'{text}' is out of range.");
            }
            return (decimal)value;
        }

        // ---- Type conversions ------------------------------------------------------------------------

        private void RegisterConversions()
        {
            Reg("tostring", 1, 1, a => a[0].IsNull ? Value.Null : Value.From(a.Str(0)));
            Reg("tonumber", 1, 1, a => a[0] switch
            {
                NullValue => Value.Null,
                NumberValue n => n,
                StringValue s when s.V.Trim() == s.V && decimal.TryParse(s.V, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => Value.From(d),
                var v => throw a.Error($"cannot convert {(v is StringValue sv ? $"\"{sv.V}\"" : v.TypeName)} to a number."),
            });
            Reg("tobool", 1, 1, a => a[0].IsNull ? Value.Null : Value.From(a.Bool(0)));
            Reg("tolist", 1, 1, a => Value.From(a.List(0)));
            Reg("tomap", 1, 1, a => a.Obj(0));
            Reg("type", 1, 1, a => Value.From(a[0].TypeName));
        }

        // ---- Encoding and hashing --------------------------------------------------------------------

        private void RegisterEncoding()
        {
            Reg("jsonencode", 1, 1, a => Value.From(JsonEncode(a[0])));
            Reg("jsondecode", 1, 1, a =>
            {
                try
                {
                    using var document = JsonDocument.Parse(a.Str(0));
                    return FromJsonElement(document.RootElement, a);
                }
                catch (JsonException ex)
                {
                    throw a.Error($"invalid JSON: {ex.Message}");
                }
            });
            Reg("csvdecode", 1, 1, a => CsvDecode(a));
            Reg("base64encode", 1, 1, a => Value.From(Convert.ToBase64String(Encoding.UTF8.GetBytes(a.Str(0)))));
            Reg("base64decode", 1, 1, a =>
            {
                try
                {
                    return Value.From(new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(Convert.FromBase64String(a.Str(0))));
                }
                catch (FormatException)
                {
                    throw a.Error("invalid base64 data.");
                }
                catch (DecoderFallbackException)
                {
                    throw a.Error("the decoded data is not valid UTF-8.");
                }
            });
            // Go's url.QueryEscape: spaces become '+'.
            Reg("urlencode", 1, 1, a => Value.From(Uri.EscapeDataString(a.Str(0)).Replace("%20", "+")));
            Reg("md5", 1, 1, a => Value.From(Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(a.Str(0))))));
            Reg("sha1", 1, 1, a => Value.From(Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(a.Str(0))))));
            Reg("sha256", 1, 1, a => Value.From(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(a.Str(0))))));
            Reg("sha512", 1, 1, a => Value.From(Convert.ToHexStringLower(SHA512.HashData(Encoding.UTF8.GetBytes(a.Str(0))))));
            Reg("base64sha256", 1, 1, a => Value.From(Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(a.Str(0))))));
        }

        private static readonly JsonSerializerOptions JsonEncodeOptions = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        /// <summary>Like Terraform: compact JSON, non-ASCII kept, only &lt; &gt; &amp; U+2028 U+2029 escaped.</summary>
        private static string JsonEncode(Value value)
        {
            string json = value.ToJson()?.ToJsonString(JsonEncodeOptions) ?? "null";
            var sb = new StringBuilder(json.Length);
            foreach (char c in json)
            {
                sb.Append(c switch
                {
                    '<' => "\\u003c",
                    '>' => "\\u003e",
                    '&' => "\\u0026",
                    '\u2028' => "\\u2028",
                    '\u2029' => "\\u2029",
                    _ => c.ToString(),
                });
            }
            return sb.ToString();
        }

        /// <summary>JSON to values; duplicate object keys take the last value, as Terraform does.</summary>
        private static Value FromJsonElement(JsonElement element, Args a)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    var fields = new Dictionary<string, Value>(StringComparer.Ordinal);
                    foreach (var property in element.EnumerateObject())
                    {
                        fields[property.Name] = FromJsonElement(property.Value, a);
                    }
                    return new ObjectValue(fields);
                case JsonValueKind.Array:
                    return Value.From(element.EnumerateArray().Select(e => FromJsonElement(e, a)).ToList());
                case JsonValueKind.String:
                    return Value.From(element.GetString());
                case JsonValueKind.Number:
                    return element.TryGetDecimal(out decimal d) ? Value.From(d) : throw a.Error($"number {element.GetRawText()} is out of range.");
                case JsonValueKind.True:
                    return Value.True;
                case JsonValueKind.False:
                    return Value.False;
                default:
                    return Value.Null;
            }
        }

        /// <summary>Parses RFC 4180 CSV with a header row into a list of objects keyed by column name.</summary>
        private static Value CsvDecode(Args a)
        {
            var rows = ParseCsv(a.Str(0), a.Pos);
            if (rows.Count == 0)
            {
                return Value.From(Array.Empty<Value>());
            }
            var header = rows[0];
            if (header.GroupBy(h => h).FirstOrDefault(g => g.Count() > 1) is { } duplicate)
            {
                throw a.Error($"duplicate column name '{duplicate.Key}' in the header.");
            }
            var result = new List<Value>();
            for (int r = 1; r < rows.Count; r++)
            {
                if (rows[r].Count == 1 && rows[r][0].Length == 0)
                {
                    continue;
                }
                if (rows[r].Count != header.Count)
                {
                    throw a.Error($"row {r + 1} has {rows[r].Count} fields but the header has {header.Count}.");
                }
                result.Add(new ObjectValue(header.Select((h, i) => new KeyValuePair<string, Value>(h, Value.From(rows[r][i])))));
            }
            return Value.From(result);
        }

        internal static List<List<string>> ParseCsv(string text, SourcePos pos)
        {
            var rows = new List<List<string>>();
            var row = new List<string>();
            var field = new StringBuilder();
            bool quoted = false;
            int i = 0;
            while (i < text.Length)
            {
                char c = text[i];
                if (quoted)
                {
                    if (c == '"')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '"')
                        {
                            field.Append('"');
                            i += 2;
                            continue;
                        }
                        quoted = false;
                    }
                    else
                    {
                        field.Append(c);
                    }
                    i++;
                    continue;
                }
                switch (c)
                {
                    case '"' when field.Length == 0:
                        quoted = true;
                        break;
                    case ',':
                        row.Add(field.ToString());
                        field.Clear();
                        break;
                    case '\r':
                        break;
                    case '\n':
                        row.Add(field.ToString());
                        field.Clear();
                        rows.Add(row);
                        row = new List<string>();
                        break;
                    default:
                        field.Append(c);
                        break;
                }
                i++;
            }
            if (quoted)
            {
                throw new DeclaradException("csvdecode(): unterminated quoted field.", pos);
            }
            if (field.Length > 0 || row.Count > 0)
            {
                row.Add(field.ToString());
                rows.Add(row);
            }
            return rows;
        }

        // ---- Filesystem and environment --------------------------------------------------------------

        private string ResolvePath(string path) => Path.GetFullPath(Path.Combine(_baseDir, path));

        private void RegisterFilesystem()
        {
            Reg("env", 1, 2, a => _getEnv(a.Str(0)) is { } v ? Value.From(v)
                : a.Count == 2 ? a[1]
                : throw new DeclaradException($"Environment variable '{a.Str(0)}' is not set.", a.Pos));
            Reg("file", 1, 1, a =>
            {
                string path = ResolvePath(a.Str(0));
                return File.Exists(path) ? Value.From(File.ReadAllText(path)) : throw a.Error($"file '{path}' does not exist.");
            });
            Reg("filebase64", 1, 1, a =>
            {
                string path = ResolvePath(a.Str(0));
                return File.Exists(path) ? Value.From(Convert.ToBase64String(File.ReadAllBytes(path))) : throw a.Error($"file '{path}' does not exist.");
            });
            Reg("fileexists", 1, 1, a => Value.From(File.Exists(ResolvePath(a.Str(0)))));
            Reg("fileset", 2, 2, a =>
            {
                string root = ResolvePath(a.Str(0));
                if (!System.IO.Directory.Exists(root))
                {
                    return Value.From(Array.Empty<Value>());
                }
                var regex = GlobRegex(a.Str(1));
                return Strings(System.IO.Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                    .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
                    .Where(f => regex.IsMatch(f))
                    .Order(StringComparer.Ordinal));
            });
            Reg("templatefile", 2, 2, a =>
            {
                string path = ResolvePath(a.Str(0));
                if (!File.Exists(path))
                {
                    throw a.Error($"file '{path}' does not exist.");
                }
                var template = Parser.ParseTemplate(File.ReadAllText(path), Path.GetFileName(path));
                var vars = a.Obj(1).Fields.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
                return new Evaluator(NoReferences.Instance, this, vars).Evaluate(template);
            });
            Reg("abspath", 1, 1, a => Value.From(ResolvePath(a.Str(0)).Replace('\\', '/')));
            Reg("basename", 1, 1, a => Value.From(Path.GetFileName(a.Str(0).TrimEnd('/', '\\'))));
            Reg("dirname", 1, 1, a => Value.From(Path.GetDirectoryName(a.Str(0)) is { Length: > 0 } d ? d : "."));
            Reg("pathexpand", 1, 1, a => Value.From(a.Str(0).StartsWith('~')
                ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + a.Str(0)[1..]
                : a.Str(0)));
        }

        private static Regex GlobRegex(string pattern)
        {
            var sb = new StringBuilder("^");
            for (int i = 0; i < pattern.Length; i++)
            {
                char c = pattern[i];
                if (c == '*' && i + 1 < pattern.Length && pattern[i + 1] == '*')
                {
                    sb.Append(".*");
                    i++;
                    if (i + 1 < pattern.Length && pattern[i + 1] == '/')
                    {
                        i++;
                    }
                }
                else if (c == '*')
                {
                    sb.Append("[^/]*");
                }
                else if (c == '?')
                {
                    sb.Append("[^/]");
                }
                else
                {
                    sb.Append(Regex.Escape(c.ToString()));
                }
            }
            return new Regex(sb.Append('$').ToString(), RegexOptions.CultureInvariant);
        }

        // ---- Directory helpers -----------------------------------------------------------------------

        private void RegisterDirectory()
        {
            Reg("dnescape", 1, 1, a => Value.From(Dn.EscapeValue(a.Str(0))));
            Reg("dnparent", 1, 1, a => Value.From(Dn.Parent(a.Str(0))));
            Reg("dnvalue", 1, 1, a => Value.From(Dn.First(a.Str(0)).Value));
            Reg("dnjoin", 3, 3, a => Value.From(Dn.Combine(a.Str(0), a.Str(1), a.Str(2))));
            Reg("domaindn", 1, 1, a => Value.From(Dn.FromDomain(a.Str(0))));
            Reg("filterescape", 1, 1, a => Value.From(Dn.EscapeFilterValue(a.Str(0))));
        }
    }
}
