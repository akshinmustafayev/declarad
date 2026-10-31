using System.Globalization;
using System.Text.Json.Nodes;

namespace Declarad.Hcl.Values
{
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
}
