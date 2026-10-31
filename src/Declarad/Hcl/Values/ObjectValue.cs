using System.Collections.Immutable;
using System.Text.Json.Nodes;

namespace Declarad.Hcl.Values
{
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
