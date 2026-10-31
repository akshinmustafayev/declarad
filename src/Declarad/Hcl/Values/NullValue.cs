using System.Text.Json.Nodes;

namespace Declarad.Hcl.Values
{
    public sealed class NullValue : Value
    {
        internal NullValue() { }
        public override string TypeName => "null";
        public override bool Equals(Value? other) => other is NullValue;
        public override int GetHashCode() => 0;
        public override string ToHcl() => "null";
        public override JsonNode? ToJson() => null;
    }
}
