namespace Declarad.Resources.Schema
{
    /// <summary>Settings from the connection block that attribute defaults may depend on.</summary>
    public sealed record SchemaContext(string Domain, string BaseDn, string? Server = null, string? ForestBaseDn = null);
}
