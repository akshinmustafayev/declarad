using System.Collections.Immutable;
using System.Text;

namespace Declarad.Hcl
{
    /// <summary>
    /// Resolves the root of a reference (e.g. <c>var</c>, <c>ou</c>). Returns the resolved value and how many
    /// traversal steps it consumed; the evaluator applies the remaining steps generically.
    /// </summary>
    public interface IReferenceResolver
    {
        (Value Value, int Consumed) Resolve(ScopeTraversalExpr reference, Func<Expr, Value> evaluate);
    }

    /// <summary>A resolver for contexts without references (template files, variable defaults).</summary>
    public sealed class NoReferences : IReferenceResolver
    {
        public static readonly NoReferences Instance = new();

        public (Value Value, int Consumed) Resolve(ScopeTraversalExpr reference, Func<Expr, Value> evaluate) =>
            throw new DeclaradException($"Unknown name '{reference.Root}'; no references are available here.", reference.Pos);
    }

    /// <summary>
    /// Evaluates expressions. Unknown values propagate: any operation on an unknown value yields unknown.
    /// </summary>
    public sealed class Evaluator
    {
        private readonly IReferenceResolver _resolver;
        private readonly Functions _functions;
        private readonly List<Dictionary<string, Value>> _scopes = new();

        public Evaluator(IReferenceResolver resolver, Functions functions, IReadOnlyDictionary<string, Value>? variables = null)
        {
            _resolver = resolver;
            _functions = functions;
            if (variables != null)
            {
                _scopes.Add(new Dictionary<string, Value>(variables, StringComparer.Ordinal));
            }
        }

        public Value Evaluate(Expr expr)
        {
            switch (expr)
            {
                case LiteralExpr lit:
                    return lit.Value;

                case TemplateExpr t:
                    if (t.SingleInterpolation)
                    {
                        return Evaluate(t.Parts[0]);
                    }
                    var sb = new StringBuilder();
                    foreach (var part in t.Parts)
                    {
                        var v = Evaluate(part);
                        if (v.IsUnknown)
                        {
                            return Value.Unknown;
                        }
                        sb.Append(ToStringValue(v, part.Pos, "template interpolation"));
                    }
                    return Value.From(sb.ToString());

                case TemplateIfExpr ti:
                {
                    var cond = Evaluate(ti.Condition);
                    if (cond.IsUnknown)
                    {
                        return Value.Unknown;
                    }
                    return ToBool(cond, ti.Condition.Pos) ? Evaluate(ti.Then) : ti.Else != null ? Evaluate(ti.Else) : Value.From("");
                }

                case TemplateForExpr tf:
                {
                    var collection = Evaluate(tf.Collection);
                    if (collection.IsUnknown)
                    {
                        return Value.Unknown;
                    }
                    var text = new StringBuilder();
                    foreach (var (key, value) in Elements(collection, tf.Collection.Pos))
                    {
                        var rendered = WithScope(tf.KeyVar, key, tf.ValueVar, value, () => Evaluate(tf.Body));
                        if (rendered.IsUnknown)
                        {
                            return Value.Unknown;
                        }
                        text.Append(ToStringValue(rendered, tf.Pos, "template"));
                    }
                    return Value.From(text.ToString());
                }

                case ScopeTraversalExpr s:
                {
                    var local = _scopes.FirstOrDefault(scope => scope.ContainsKey(s.Root));
                    if (local != null)
                    {
                        return ApplyTraversal(local[s.Root], s.Traversal, s.ToString());
                    }
                    var (value, consumed) = _resolver.Resolve(s, Evaluate);
                    return ApplyTraversal(value, s.Traversal.Skip(consumed).ToList(), s.ToString());
                }

                case RelativeTraversalExpr rt:
                    return ApplyTraversal(Evaluate(rt.Source), rt.Traversal, "expression");

                case FunctionCallExpr f:
                    return CallFunction(f);

                case ListExpr l:
                    return Value.From(l.Items.Select(Evaluate));

                case ObjectExpr o:
                {
                    var fields = ImmutableSortedDictionary.CreateBuilder<string, Value>(StringComparer.Ordinal);
                    foreach (var (k, v) in o.Items)
                    {
                        var keyValue = Evaluate(k);
                        if (keyValue.IsUnknown)
                        {
                            return Value.Unknown;
                        }
                        string key = ToStringValue(keyValue, k.Pos, "object key");
                        if (fields.ContainsKey(key))
                        {
                            throw new DeclaradException($"Duplicate object key '{key}'.", k.Pos);
                        }
                        fields[key] = Evaluate(v);
                    }
                    return new ObjectValue(fields.ToImmutable());
                }

                case ConditionalExpr c:
                {
                    var cond = Evaluate(c.Condition);
                    if (cond.IsUnknown)
                    {
                        return Value.Unknown;
                    }
                    return ToBool(cond, c.Condition.Pos) ? Evaluate(c.True) : Evaluate(c.False);
                }

                case BinaryExpr b:
                    return EvaluateBinary(b);

                case NotExpr n:
                {
                    var v = Evaluate(n.Operand);
                    return v.IsUnknown ? Value.Unknown : Value.From(!ToBool(v, n.Pos));
                }

                case NegateExpr neg:
                {
                    var v = Evaluate(neg.Operand);
                    return v.IsUnknown ? Value.Unknown : Value.From(-ToNumber(v, neg.Pos));
                }

                case ForExpr f:
                    return EvaluateFor(f);
            }
            throw new DeclaradException("Unsupported expression.", expr.Pos);
        }

        private Value EvaluateBinary(BinaryExpr b)
        {
            var left = Evaluate(b.Left);
            // Short-circuit logical operators.
            if (b.Op == TokenKind.AndAnd && left is BoolValue { V: false })
            {
                return Value.False;
            }
            if (b.Op == TokenKind.OrOr && left is BoolValue { V: true })
            {
                return Value.True;
            }
            var right = Evaluate(b.Right);
            // A known side can still decide a logical operator: unknown || true is true, unknown && false is false.
            if (b.Op == TokenKind.OrOr && (left is BoolValue { V: true } || right is BoolValue { V: true }))
            {
                return Value.True;
            }
            if (b.Op == TokenKind.AndAnd && (left is BoolValue { V: false } || right is BoolValue { V: false }))
            {
                return Value.False;
            }
            if (left.IsUnknown || right.IsUnknown)
            {
                return Value.Unknown;
            }
            switch (b.Op)
            {
                case TokenKind.EqualEqual:
                    return Value.From(left.Equals(right));
                case TokenKind.NotEqual:
                    return Value.From(!left.Equals(right));
                case TokenKind.AndAnd:
                    return Value.From(ToBool(left, b.Left.Pos) && ToBool(right, b.Right.Pos));
                case TokenKind.OrOr:
                    return Value.From(ToBool(left, b.Left.Pos) || ToBool(right, b.Right.Pos));
            }

            decimal l = ToNumber(left, b.Left.Pos);
            decimal r = ToNumber(right, b.Right.Pos);
            try
            {
                return Arithmetic(b, l, r);
            }
            catch (OverflowException)
            {
                throw new DeclaradException("Number is out of range.", b.Pos);
            }
        }

        private static Value Arithmetic(BinaryExpr b, decimal l, decimal r)
        {
            switch (b.Op)
            {
                case TokenKind.Less: return Value.From(l < r);
                case TokenKind.LessEqual: return Value.From(l <= r);
                case TokenKind.Greater: return Value.From(l > r);
                case TokenKind.GreaterEqual: return Value.From(l >= r);
                case TokenKind.Plus: return Value.From(l + r);
                case TokenKind.Minus: return Value.From(l - r);
                case TokenKind.Star: return Value.From(l * r);
                case TokenKind.Slash:
                    return r == 0 ? throw new DeclaradException("Division by zero.", b.Pos) : Value.From(l / r);
                case TokenKind.Percent:
                    return r == 0 ? throw new DeclaradException("Modulo by zero.", b.Pos) : Value.From(l % r);
            }
            throw new DeclaradException($"Unsupported operator {b.Op}.", b.Pos);
        }

        private Value CallFunction(FunctionCallExpr f)
        {
            // try() and can() evaluate their arguments lazily to catch errors.
            if (f.Name == "try")
            {
                if (f.Args.Count == 0)
                {
                    throw new DeclaradException("try() needs at least one argument.", f.Pos);
                }
                var errors = new List<string>();
                foreach (var arg in f.Args)
                {
                    try
                    {
                        return Evaluate(arg);
                    }
                    catch (DeclaradException ex)
                    {
                        errors.Add(ex.Message);
                    }
                }
                throw new DeclaradException($"try(): no argument could be evaluated: {string.Join("; ", errors)}", f.Pos);
            }
            if (f.Name == "can")
            {
                if (f.Args.Count != 1)
                {
                    throw new DeclaradException("can() takes exactly one argument.", f.Pos);
                }
                try
                {
                    var v = Evaluate(f.Args[0]);
                    return v.IsUnknown ? Value.Unknown : Value.True;
                }
                catch (DeclaradException)
                {
                    return Value.False;
                }
            }

            var args = f.Args.Select(Evaluate).ToList();
            if (f.ExpandFinal)
            {
                var last = args[^1];
                args.RemoveAt(args.Count - 1);
                if (last.IsUnknown)
                {
                    return Value.Unknown;
                }
                if (last is not ListValue spread)
                {
                    throw new DeclaradException($"Only a list can be expanded with '...', got {last.TypeName}.", f.Args[^1].Pos);
                }
                args.AddRange(spread.Items);
            }
            if (args.Any(a => a.IsUnknown) || (args.Any(a => a.ContainsUnknown()) && !Functions.AcceptsUnknown(f.Name)))
            {
                return Value.Unknown;
            }
            try
            {
                return _functions.Call(f.Name, args, f.Pos);
            }
            catch (Exception ex) when (ex is OverflowException or ArgumentException or FormatException or InvalidOperationException or IndexOutOfRangeException or System.Text.Json.JsonException)
            {
                throw new DeclaradException($"{f.Name}(): {ex.Message}", f.Pos);
            }
        }

        private Value WithScope(string? keyVar, Value key, string valueVar, Value value, Func<Value> body)
        {
            var scope = new Dictionary<string, Value>(StringComparer.Ordinal) { [valueVar] = value };
            if (keyVar != null)
            {
                scope[keyVar] = key;
            }
            _scopes.Insert(0, scope);
            try
            {
                return body();
            }
            finally
            {
                _scopes.RemoveAt(0);
            }
        }

        private static IEnumerable<(Value Key, Value Value)> Elements(Value collection, SourcePos pos) => collection switch
        {
            ListValue l => l.Items.Select((v, i) => (Value.From(i), v)),
            ObjectValue o => o.Fields.Select(kv => (Value.From(kv.Key), kv.Value)),
            NullValue => Array.Empty<(Value, Value)>(),
            _ => throw new DeclaradException($"Cannot iterate over a {collection.TypeName}; a list or object is required.", pos),
        };

        private Value EvaluateFor(ForExpr f)
        {
            var collection = Evaluate(f.Collection);
            if (collection.IsUnknown)
            {
                return Value.Unknown;
            }

            var list = new List<Value>();
            var fields = new Dictionary<string, Value>(StringComparer.Ordinal);
            var groups = new Dictionary<string, List<Value>>(StringComparer.Ordinal);
            var order = new List<string>();
            bool unknown = false;
            foreach (var (key, value) in Elements(collection, f.Collection.Pos))
            {
                WithScope(f.KeyVar, key, f.ValueVar, value, () =>
                {
                    if (f.Condition != null)
                    {
                        var cond = Evaluate(f.Condition);
                        if (cond.IsUnknown)
                        {
                            unknown = true;
                            return Value.Null;
                        }
                        if (!ToBool(cond, f.Condition.Pos))
                        {
                            return Value.Null;
                        }
                    }
                    if (!f.IsObject)
                    {
                        list.Add(Evaluate(f.ValueExpr));
                        return Value.Null;
                    }
                    var keyValue = Evaluate(f.KeyExpr!);
                    if (keyValue.IsUnknown)
                    {
                        unknown = true;
                        return Value.Null;
                    }
                    string k = ToStringValue(keyValue, f.KeyExpr!.Pos, "for expression key");
                    var v = Evaluate(f.ValueExpr);
                    if (f.Grouping)
                    {
                        if (!groups.TryGetValue(k, out var g))
                        {
                            groups[k] = g = new List<Value>();
                            order.Add(k);
                        }
                        g.Add(v);
                    }
                    else if (!fields.TryAdd(k, v))
                    {
                        throw new DeclaradException($"Duplicate key '{k}' produced by for expression. Add '...' after the value to group values by key.", f.KeyExpr!.Pos);
                    }
                    return Value.Null;
                });
            }
            if (unknown)
            {
                return Value.Unknown;
            }
            if (!f.IsObject)
            {
                return Value.From(list);
            }
            return f.Grouping
                ? new ObjectValue(groups.Select(g => new KeyValuePair<string, Value>(g.Key, Value.From(g.Value))))
                : new ObjectValue(fields);
        }

        private Value ApplyTraversal(Value value, IReadOnlyList<Traverser> traversal, string what)
        {
            for (int i = 0; i < traversal.Count; i++)
            {
                if (value.IsUnknown)
                {
                    return Value.Unknown;
                }
                switch (traversal[i])
                {
                    case Splat splat:
                    {
                        // A non-list value becomes a one-element list, null an empty list (as in HCL).
                        IEnumerable<Value> items = value switch
                        {
                            NullValue => Array.Empty<Value>(),
                            ListValue l => l.Items,
                            _ => new[] { value },
                        };
                        if (!splat.Legacy)
                        {
                            var rest = traversal.Skip(i + 1).ToList();
                            return Value.From(items.Select(item => ApplyTraversal(item, rest, what)).ToList());
                        }
                        var attrs = traversal.Skip(i + 1).TakeWhile(t => t is GetAttr).ToList();
                        var mapped = Value.From(items.Select(item => ApplyTraversal(item, attrs, what)).ToList());
                        return ApplyTraversal(mapped, traversal.Skip(i + 1 + attrs.Count).ToList(), what);
                    }
                    case GetAttr g:
                        value = GetField(value, g.Name, g.Pos, what);
                        break;
                    case Index idx:
                        var key = Evaluate(idx.Key);
                        if (key.IsUnknown)
                        {
                            return Value.Unknown;
                        }
                        if (value is ListValue list)
                        {
                            decimal n = ToNumber(key, idx.Pos);
                            if (decimal.Truncate(n) != n || n < 0 || n >= list.Items.Length)
                            {
                                throw new DeclaradException($"Index {NumberValue.Format(n)} is out of range for a list of {list.Items.Length} elements.", idx.Pos);
                            }
                            value = list.Items[(int)n];
                        }
                        else
                        {
                            value = GetField(value, ToStringValue(key, idx.Pos, "index key"), idx.Pos, what);
                        }
                        break;
                }
            }
            return value;
        }

        private static Value GetField(Value value, string name, SourcePos pos, string what)
        {
            if (value is ObjectValue obj)
            {
                if (obj.Fields.TryGetValue(name, out var v))
                {
                    return v;
                }
                throw new DeclaradException($"'{what}' has no attribute or key '{name}'.", pos);
            }
            if (value.IsNull)
            {
                throw new DeclaradException($"Cannot access '{name}' on a null value in '{what}'.", pos);
            }
            throw new DeclaradException($"Cannot access '{name}' on a {value.TypeName} value in '{what}'.", pos);
        }

        public static string ToStringValue(Value v, SourcePos pos, string context) => v switch
        {
            StringValue s => s.V,
            NumberValue n => n.ToHcl(),
            BoolValue b => b.ToHcl(),
            _ => throw new DeclaradException($"Cannot use a {v.TypeName} value as {context}.", pos),
        };

        public static bool ToBool(Value v, SourcePos pos) => v switch
        {
            BoolValue b => b.V,
            StringValue { V: "true" } => true,
            StringValue { V: "false" } => false,
            _ => throw new DeclaradException($"Expected a bool, got {v.TypeName}.", pos),
        };

        public static decimal ToNumber(Value v, SourcePos pos) => v switch
        {
            NumberValue n => n.V,
            StringValue s when decimal.TryParse(s.V, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) => d,
            _ => throw new DeclaradException($"Expected a number, got {v.TypeName}{(v is StringValue sv ? $" \"{sv.V}\"" : "")}.", pos),
        };
    }
}
