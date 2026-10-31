using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Declarad.Hcl
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

    public sealed class NullValue : Value
    {
        internal NullValue() { }
        public override string TypeName => "null";
        public override bool Equals(Value? other) => other is NullValue;
        public override int GetHashCode() => 0;
        public override string ToHcl() => "null";
        public override JsonNode? ToJson() => null;
    }

    public sealed class UnknownValue : Value
    {
        internal UnknownValue() { }
        public override string TypeName => "unknown";
        public override bool Equals(Value? other) => other is UnknownValue;
        public override int GetHashCode() => -1;
        public override string ToHcl() => "(known after reading the directory)";
        public override JsonNode? ToJson() => null;
    }

    public sealed class StringValue : Value
    {
        public string V { get; }
        public StringValue(string v) { V = v; }
        public override string TypeName => "string";
        public override bool Equals(Value? other) => other is StringValue s && s.V == V;
        public override int GetHashCode() => V.GetHashCode();
        public override string ToHcl() => HclWriter.Quote(V);
        public override JsonNode? ToJson() => JsonValue.Create(V);
    }

    public sealed class BoolValue : Value
    {
        public bool V { get; }
        internal BoolValue(bool v) { V = v; }
        public override string TypeName => "bool";
        public override bool Equals(Value? other) => other is BoolValue b && b.V == V;
        public override int GetHashCode() => V ? 1 : 2;
        public override string ToHcl() => V ? "true" : "false";
        public override JsonNode? ToJson() => JsonValue.Create(V);
    }

    public sealed class NumberValue : Value
    {
        public decimal V { get; }
        public NumberValue(decimal v) { V = v; }
        public override string TypeName => "number";
        public bool IsInteger => decimal.Truncate(V) == V;
        public long AsLong => (long)V;
        public override bool Equals(Value? other) => other is NumberValue n && n.V == V;
        public override int GetHashCode() => V.GetHashCode();
        public override string ToHcl() => Format(V);
        // Normalized so that 1.50 serializes as 1.5.
        public override JsonNode? ToJson() => IsInteger && V >= long.MinValue && V <= long.MaxValue
            ? JsonValue.Create((long)V)
            : JsonValue.Create(decimal.Parse(Format(V), CultureInfo.InvariantCulture));

        /// <summary>Invariant formatting without trailing zeros or exponent.</summary>
        public static string Format(decimal v)
        {
            string s = v.ToString("0.############################", CultureInfo.InvariantCulture);
            return s == "-0" ? "0" : s;
        }
    }

    public sealed class ListValue : Value
    {
        public ImmutableArray<Value> Items { get; }
        public ListValue(ImmutableArray<Value> items) { Items = items; }
        public override string TypeName => "list";
        public override bool Equals(Value? other) => other is ListValue l && l.Items.SequenceEqual(Items);
        public override int GetHashCode() => Items.Aggregate(17, (h, v) => h * 31 + v.GetHashCode());
        public override string ToHcl() => "[" + string.Join(", ", Items.Select(i => i.ToHcl())) + "]";
        public override JsonNode? ToJson() => new JsonArray(Items.Select(i => i.ToJson()).ToArray());
    }

    public sealed class ObjectValue : Value
    {
        public ImmutableSortedDictionary<string, Value> Fields { get; }
        public ObjectValue(ImmutableSortedDictionary<string, Value> fields) { Fields = fields; }
        public ObjectValue(IEnumerable<KeyValuePair<string, Value>> fields)
            : this(fields.ToImmutableSortedDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal)) { }
        public override string TypeName => "object";
        public override bool Equals(Value? other) => other is ObjectValue o && o.Fields.Count == Fields.Count && Fields.All(kv => o.Fields.TryGetValue(kv.Key, out var v) && v.Equals(kv.Value));
        public override int GetHashCode() => Fields.Aggregate(19, (h, kv) => h * 31 + kv.Key.GetHashCode() ^ kv.Value.GetHashCode());
        public override string ToHcl() => "{ " + string.Join(", ", Fields.Select(kv => $"{HclWriter.Key(kv.Key)} = {kv.Value.ToHcl()}")) + " }";
        public override JsonNode? ToJson()
        {
            var obj = new JsonObject();
            foreach (var kv in Fields)
            {
                obj[kv.Key] = kv.Value.ToJson();
            }
            return obj;
        }
    }
}
