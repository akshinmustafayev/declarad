using System.Text.Json.Nodes;

namespace Declarad.Hcl.Values
{
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
}
