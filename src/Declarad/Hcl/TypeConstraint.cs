namespace Declarad.Hcl
{
    /// <summary>
    /// A variable type constraint: <c>string</c>, <c>number</c>, <c>bool</c>, <c>any</c>, <c>list(T)</c>,
    /// <c>set(T)</c>, <c>map(T)</c>, <c>tuple([T...])</c> and <c>object({ name = T, opt = optional(T, default) })</c>.
    /// <see cref="Convert"/> checks a value and converts it (e.g. "5" to 5, applies optional defaults).
    /// </summary>
    public abstract record TypeConstraint
    {
        public static readonly TypeConstraint Any = new Primitive("any");
        public static readonly TypeConstraint String = new Primitive("string");

        public sealed record Primitive(string Name) : TypeConstraint
        {
            public override string ToString() => Name;
        }

        public sealed record Collection(string Kind, TypeConstraint Element) : TypeConstraint
        {
            public override string ToString() => $"{Kind}({Element})";
        }

        public sealed record Tuple(IReadOnlyList<TypeConstraint> Elements) : TypeConstraint
        {
            public override string ToString() => $"tuple([{string.Join(", ", Elements)}])";
        }

        public sealed record ObjectAttr(TypeConstraint Type, bool Optional, Value? Default);

        public sealed record Object(IReadOnlyDictionary<string, ObjectAttr> Attributes) : TypeConstraint
        {
            public override string ToString() => "object({" + string.Join(", ", Attributes.Select(a => $"{a.Key} = {(a.Value.Optional ? $"optional({a.Value.Type})" : a.Value.Type)}")) + "})";
        }

        /// <summary>True when values given as text (e.g. --var) should be parsed as HCL expressions.</summary>
        public bool IsStructured => this is not Primitive { Name: "string" or "any" };

        public static TypeConstraint Parse(Expr expr, Func<Expr, Value> evaluate)
        {
            switch (expr)
            {
                case ScopeTraversalExpr { Traversal.Count: 0 } s when s.Root is "string" or "number" or "bool" or "any":
                    return new Primitive(s.Root);
                case ScopeTraversalExpr { Traversal.Count: 0 } s when s.Root is "list" or "set" or "map":
                    return new Collection(s.Root, Any);
                case LiteralExpr { Value: StringValue sv }:
                    // Legacy quoted form: type = "string" / "list" / "map".
                    return sv.V switch
                    {
                        "string" or "number" or "bool" or "any" => new Primitive(sv.V),
                        "list" or "set" or "map" => new Collection(sv.V, Any),
                        "object" => new Collection("map", Any),
                        _ => throw new DeclaradException($"Unknown type \"{sv.V}\".", expr.Pos),
                    };
                case FunctionCallExpr { Name: "list" or "set" or "map", Args.Count: 1 } f:
                    return new Collection(f.Name, Parse(f.Args[0], evaluate));
                case FunctionCallExpr { Name: "tuple", Args: [ListExpr items] }:
                    return new Tuple(items.Items.Select(i => Parse(i, evaluate)).ToList());
                case FunctionCallExpr { Name: "object", Args: [ObjectExpr attrs] }:
                {
                    var result = new Dictionary<string, ObjectAttr>(StringComparer.Ordinal);
                    foreach (var (k, v) in attrs.Items)
                    {
                        string name = k is LiteralExpr { Value: StringValue key } ? key.V
                            : k is ScopeTraversalExpr { Traversal.Count: 0 } id ? id.Root
                            : throw new DeclaradException("Object type attribute names must be identifiers.", k.Pos);
                        if (v is FunctionCallExpr { Name: "optional" } opt && opt.Args.Count is 1 or 2)
                        {
                            result[name] = new ObjectAttr(Parse(opt.Args[0], evaluate), true, opt.Args.Count == 2 ? evaluate(opt.Args[1]) : null);
                        }
                        else
                        {
                            result[name] = new ObjectAttr(Parse(v, evaluate), false, null);
                        }
                    }
                    return new Object(result);
                }
            }
            throw new DeclaradException("Invalid type constraint. Use string, number, bool, any, list(T), set(T), map(T), tuple([...]) or object({...}).", expr.Pos);
        }

        public Value Convert(Value value, string path)
        {
            if (value.IsNull || value.IsUnknown)
            {
                return value;
            }
            switch (this)
            {
                case Primitive { Name: "any" }:
                    return value;
                case Primitive { Name: "string" }:
                    return value is StringValue or NumberValue or BoolValue
                        ? Value.From(Evaluator.ToStringValue(value, default, path))
                        : throw Mismatch(path, value);
                case Primitive { Name: "number" }:
                    return value is NumberValue ? value
                        : value is StringValue s && decimal.TryParse(s.V, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? Value.From(d)
                        : throw Mismatch(path, value);
                case Primitive { Name: "bool" }:
                    return value is BoolValue ? value
                        : value is StringValue { V: "true" or "false" } b ? Value.From(b.V == "true")
                        : throw Mismatch(path, value);
                case Collection { Kind: "list" or "set" } c:
                {
                    if (value is not ListValue l)
                    {
                        throw Mismatch(path, value);
                    }
                    var items = l.Items.Select((v, i) => c.Element.Convert(v, $"{path}[{i}]")).ToList();
                    if (c.Kind == "set")
                    {
                        items = Functions.SetOrder(items).ToList();
                    }
                    return Value.From(items);
                }
                case Collection { Kind: "map" } m:
                    return value is ObjectValue o
                        ? new ObjectValue(o.Fields.Select(kv => new KeyValuePair<string, Value>(kv.Key, m.Element.Convert(kv.Value, $"{path}[\"{kv.Key}\"]"))))
                        : throw Mismatch(path, value);
                case Tuple t:
                    if (value is not ListValue tl || tl.Items.Length != t.Elements.Count)
                    {
                        throw Mismatch(path, value);
                    }
                    return Value.From(tl.Items.Select((v, i) => t.Elements[i].Convert(v, $"{path}[{i}]")));
                case Object obj:
                {
                    if (value is not ObjectValue ov)
                    {
                        throw Mismatch(path, value);
                    }
                    var fields = new Dictionary<string, Value>(StringComparer.Ordinal);
                    foreach (var (name, attr) in obj.Attributes)
                    {
                        bool present = ov.Fields.TryGetValue(name, out var v);
                        if (present && !(v!.IsNull && attr.Optional && attr.Default != null))
                        {
                            // Present (even if null): converted as is; null is valid for any type.
                            fields[name] = attr.Type.Convert(v, $"{path}.{name}");
                        }
                        else if (attr.Optional)
                        {
                            fields[name] = attr.Default != null ? attr.Type.Convert(attr.Default, $"{path}.{name}") : Value.Null;
                        }
                        else
                        {
                            throw new DeclaradException($"{path}: attribute '{name}' is required.");
                        }
                    }
                    return new ObjectValue(fields);
                }
            }
            return value;
        }

        private DeclaradException Mismatch(string path, Value value) =>
            new($"{path}: expected {this}, got {value.TypeName}{(value is StringValue s ? $" \"{s.V}\"" : "")}.");
    }
}
