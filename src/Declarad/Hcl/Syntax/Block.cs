namespace Declarad.Hcl.Syntax
{
    public sealed record Block(string Type, IReadOnlyList<string> Labels, Body Body, SourcePos Pos);
}
