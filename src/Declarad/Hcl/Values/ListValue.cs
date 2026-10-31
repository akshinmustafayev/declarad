using System.Collections.Immutable;
using System.Text.Json.Nodes;

namespace Declarad.Hcl.Values
{
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
}
