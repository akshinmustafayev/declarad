using System.Text.Json.Nodes;

namespace Declarad.Hcl.Values
{
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
}
