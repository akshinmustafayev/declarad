namespace Declarad.Hcl
{
    public sealed record Token(TokenKind Kind, string Text, SourcePos Pos, IReadOnlyList<TemplatePart>? Parts = null)
    {
        public override string ToString() => Kind switch
        {
            TokenKind.Newline => "newline",
            TokenKind.Eof => "end of file",
            TokenKind.Template => "string",
            _ => $"'{Text}'",
        };
    }
}
