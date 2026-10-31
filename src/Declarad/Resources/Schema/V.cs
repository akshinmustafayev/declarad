using Declarad.Hcl.Values;

namespace Declarad.Resources.Schema
{
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
