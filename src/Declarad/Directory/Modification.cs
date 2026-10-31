namespace Declarad.Directory
{
    /// <summary>One attribute modification. Values are <see cref="string"/> or <see cref="byte"/>[]; Delete with no values clears the attribute.</summary>
    public sealed record Modification(string Attribute, ModOp Op, IReadOnlyList<object> Values)
    {
        public static Modification Replace(string attribute, params object[] values) => new(attribute, ModOp.Replace, values);
        public static Modification Clear(string attribute) => new(attribute, ModOp.Delete, Array.Empty<object>());
    }
}
