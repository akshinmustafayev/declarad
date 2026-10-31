namespace Declarad.Config
{
    /// <summary>An <c>import</c> block: adopt the object identified by <see cref="Id"/> (DN or GUID) as <see cref="To"/>.</summary>
    public sealed record ImportSpec(string To, string Id, SourcePos Pos);
}
