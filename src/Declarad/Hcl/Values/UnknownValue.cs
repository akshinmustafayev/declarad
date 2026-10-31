using System.Text.Json.Nodes;

namespace Declarad.Hcl.Values
{
    public sealed class UnknownValue : Value
    {
        internal UnknownValue() { }
        public override string TypeName => "unknown";
        public override bool Equals(Value? other) => other is UnknownValue;
        public override int GetHashCode() => -1;
        public override string ToHcl() => "(known after reading the directory)";
        public override JsonNode? ToJson() => null;
    }
}
