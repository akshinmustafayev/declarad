namespace Declarad.Hcl.Syntax
{
    public abstract record Traverser(SourcePos Pos);

    public sealed record GetAttr(string Name, SourcePos Pos) : Traverser(Pos);

    public sealed record Index(Expr Key, SourcePos Pos) : Traverser(Pos);

    /// <summary>
    /// <c>[*]</c>: the remaining traversal is applied to every element. The legacy <c>.*</c> form only carries
    /// the following attribute accesses into the elements; later indexes apply to the resulting list.
    /// </summary>
    public sealed record Splat(SourcePos Pos, bool Legacy = false) : Traverser(Pos);
}
