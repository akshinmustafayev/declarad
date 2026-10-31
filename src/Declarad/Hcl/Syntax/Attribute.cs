namespace Declarad.Hcl.Syntax
{
    public sealed record Attribute(string Name, Expr Expr, SourcePos Pos);
}
