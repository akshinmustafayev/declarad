namespace Declarad.Hcl
{
    public enum TokenKind
    {
        Ident,
        Number,
        Template,
        LBrace, RBrace, LBracket, RBracket, LParen, RParen,
        Equals, Comma, Dot, Ellipsis, Colon, Question, FatArrow,
        Bang, EqualEqual, NotEqual, AndAnd, OrOr,
        Plus, Minus, Star, Slash, Percent,
        Less, LessEqual, Greater, GreaterEqual,
        Newline,
        Eof,
    }
}
