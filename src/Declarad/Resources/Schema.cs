using Declarad.Hcl;

namespace Declarad.Resources
{
    public enum AttrType
    {
        String,
        Bool,
        Number,
        StringList,
    }

    /// <summary>How an attribute value is encoded in LDAP.</summary>
    public enum LdapFormat
    {
        Text,
        /// <summary>LDAP boolean: TRUE / FALSE.</summary>
        Bool,
        /// <summary>Integer.</summary>
        Int,
        /// <summary>Integer flag 1 / 0 (absent means false).</summary>
        IntBool,
        /// <summary>AD time interval (negative 100 ns ticks), written in configuration as e.g. "42d" or "30m".</summary>
        Duration,
    }

    /// <summary>Settings from the connection block that attribute defaults may depend on.</summary>
    public sealed record SchemaContext(string Domain, string BaseDn, string? Server = null, string? ForestBaseDn = null);

    /// <summary>Declares one configurable attribute of a resource type and how it maps to the directory.</summary>
    public sealed record AttributeSpec(string Name, AttrType Type)
    {
        public string Description { get; init; } = "";
        public bool Required { get; init; }
        public Value? Default { get; init; }

        /// <summary>Default computed from other attributes (looked up through the function argument).</summary>
        public Func<Func<string, Value>, SchemaContext, Value>? DefaultFunc { get; init; }

        /// <summary>LDAP attribute for a plain single- or multi-valued string mapping; null when the type handles it.</summary>
        public string? Ldap { get; init; }

        public LdapFormat Format { get; init; } = LdapFormat.Text;

        /// <summary>Canonicalizes a configured value (e.g. "1008h" becomes "42d") so comparisons are exact.</summary>
        public Func<Value, Value>? Normalize { get; init; }

        /// <summary>Cannot change after creation (the plan fails instead of updating).</summary>
        public bool Immutable { get; init; }

        /// <summary>Never printed in plans or written to state.</summary>
        public bool Sensitive { get; init; }

        /// <summary>Only applied when the object is created (e.g. initial password); never compared afterwards.</summary>
        public bool CreateOnly { get; init; }

        /// <summary>
        /// Applied after all objects exist (memberships, manager links), so references between objects
        /// never force an ordering or create cycles.
        /// </summary>
        public bool Deferred { get; init; }

        /// <summary>Values are distinguished names; compared after normalization.</summary>
        public bool IsDn { get; init; }

        /// <summary>DN values may also be given as sAMAccountName and are resolved during planning.</summary>
        public bool AcceptsSamAccountName { get; init; }

        /// <summary>
        /// List attribute managed additively: entries are added when listed and removed only if declarad
        /// added them before (tracked in state). Other values are left alone.
        /// </summary>
        public bool Additive { get; init; }

        public IReadOnlyList<string>? AllowedValues { get; init; }

        /// <summary>Returns an error message for an invalid (non-null) value.</summary>
        public Func<Value, string?>? Validate { get; init; }

        public Value ResolveDefault(Func<string, Value> get, SchemaContext ctx) =>
            DefaultFunc != null ? DefaultFunc(get, ctx) : Default ?? Value.Null;
    }

    public static class V
    {
        public static string? Str(Value v) => v switch
        {
            StringValue s => s.V,
            NumberValue n => n.ToHcl(),
            BoolValue b => b.ToHcl(),
            _ => null,
        };

        public static bool? Bool(Value v) => v is BoolValue b ? b.V : null;

        public static long? Long(Value v) => v is NumberValue n && n.V >= long.MinValue && n.V <= long.MaxValue ? (long)n.V : null;

        public static IReadOnlyList<string> List(Value v) => v is ListValue l ? l.Items.Select(i => Str(i) ?? "").ToList() : Array.Empty<string>();

        public static bool IsEmpty(Value v) => v.IsNull || v is StringValue { V: "" };

        /// <summary>Converts an evaluated configuration value to the attribute's declared type.</summary>
        public static Value Coerce(Value v, AttributeSpec spec, SourcePos pos)
        {
            if (v.IsNull || v.IsUnknown)
            {
                return v;
            }
            if (v.ContainsUnknown())
            {
                return Value.Unknown;
            }
            switch (spec.Type)
            {
                case AttrType.String:
                    if (v is StringValue or NumberValue or BoolValue)
                    {
                        return Value.From(Str(v)!);
                    }
                    break;
                case AttrType.Bool:
                    if (v is BoolValue)
                    {
                        return v;
                    }
                    if (v is StringValue s && bool.TryParse(s.V, out bool b))
                    {
                        return Value.From(b);
                    }
                    break;
                case AttrType.Number:
                    // Every number attribute in AD is an integer.
                    decimal? number = v is NumberValue nv ? nv.V
                        : v is StringValue sn && decimal.TryParse(sn.V, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out decimal n) ? n
                        : null;
                    if (number == null)
                    {
                        break;
                    }
                    if (decimal.Truncate(number.Value) != number.Value || number.Value < long.MinValue || number.Value > long.MaxValue)
                    {
                        throw new DeclaradException($"Attribute '{spec.Name}' must be a whole number, got {NumberValue.Format(number.Value)}.", pos);
                    }
                    return Value.From(number.Value);
                case AttrType.StringList:
                    if (v is ListValue list)
                    {
                        var items = new List<Value>();
                        foreach (var item in list.Items)
                        {
                            if (item.IsNull)
                            {
                                continue;
                            }
                            if (item is not (StringValue or NumberValue or BoolValue))
                            {
                                throw new DeclaradException($"Attribute '{spec.Name}' must be a list of strings, but an element is a {item.TypeName}.", pos);
                            }
                            items.Add(Value.From(Str(item)!));
                        }
                        return Value.From(items);
                    }
                    break;
            }
            string expected = spec.Type switch
            {
                AttrType.StringList => "list of strings",
                _ => spec.Type.ToString().ToLowerInvariant(),
            };
            throw new DeclaradException($"Attribute '{spec.Name}' must be a {expected}, got {v.TypeName}.", pos);
        }
    }
}
