using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Declarad.Hcl.Values
{
    /// <summary>
    /// A dynamically typed configuration value: null, string, bool, number, list, object, or unknown
    /// (a value that can only be determined by reading the directory, e.g. a data source during offline
    /// validation). Values compare structurally.
    /// </summary>
    public abstract class Value : IEquatable<Value>
    {
        public static readonly Value Null = new NullValue();
        public static readonly Value True = new BoolValue(true);
        public static readonly Value False = new BoolValue(false);
        public static readonly Value Unknown = new UnknownValue();

        public abstract string TypeName { get; }
        public bool IsNull => this is NullValue;
        public bool IsUnknown => this is UnknownValue;

        public static Value From(string? s) => s == null ? Null : new StringValue(s);
        public static Value From(bool b) => b ? True : False;
        public static Value From(long n) => new NumberValue(n);
        public static Value From(decimal n) => new NumberValue(n);
        public static Value From(IEnumerable<Value> items) => new ListValue(items.ToImmutableArray());
        public static Value FromStrings(IEnumerable<string> items) => new ListValue(items.Select(i => (Value)new StringValue(i)).ToImmutableArray());
        public static Value FromObject(IEnumerable<KeyValuePair<string, Value>> fields) => new ObjectValue(fields);

        /// <summary>True if this value or anything nested in it is unknown.</summary>
        public bool ContainsUnknown() => this switch
        {
            UnknownValue => true,
            ListValue l => l.Items.Any(i => i.ContainsUnknown()),
            ObjectValue o => o.Fields.Values.Any(v => v.ContainsUnknown()),
            _ => false,
        };

        public abstract bool Equals(Value? other);
        public override bool Equals(object? obj) => obj is Value v && Equals(v);
        public abstract override int GetHashCode();

        /// <summary>Renders the value in HCL syntax.</summary>
        public abstract string ToHcl();
        public override string ToString() => ToHcl();

        public abstract JsonNode? ToJson();

        public static Value FromJson(JsonNode? node)
        {
            switch (node)
            {
                case null:
                    return Null;
                case JsonArray arr:
                    return From(arr.Select(FromJson));
                case JsonObject obj:
                    return new ObjectValue(obj.ToImmutableSortedDictionary(kv => kv.Key, kv => FromJson(kv.Value), StringComparer.Ordinal));
                case JsonValue v when v.GetValueKind() is JsonValueKind.True or JsonValueKind.False:
                    return From(v.GetValue<bool>());
                case JsonValue v when v.GetValueKind() == JsonValueKind.Number:
                    return v.TryGetValue(out long l) ? From(l)
                        : v.TryGetValue(out decimal d) ? From(d)
                        : throw new DeclaradException($"Number {node.ToJsonString()} is out of range.");
                case JsonValue v when v.TryGetValue(out string? s):
                    return From(s);
                default:
                    throw new DeclaradException($"Unsupported JSON value '{node.ToJsonString()}'.");
            }
        }
    }
}
