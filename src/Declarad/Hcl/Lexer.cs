using System.Globalization;
using System.Text;

namespace Declarad.Hcl
{
    /// <summary>
    /// Tokenizer for HCL native syntax: identifiers, numbers, quoted templates and heredocs (with
    /// <c>${}</c> interpolations and <c>%{}</c> directives), operators and the three comment styles.
    /// </summary>
    public sealed class Lexer
    {
        private readonly string _src;
        private readonly string _file;
        private int _i;
        private int _line;
        private int _col;

        public Lexer(string source, string file, int line = 1, int column = 1)
        {
            _src = source;
            _file = file;
            _line = line;
            _col = column;
        }

        public static List<Token> Tokenize(string source, string file, int line = 1, int column = 1)
        {
            var lexer = new Lexer(source, file, line, column);
            var tokens = new List<Token>();
            Token t;
            do
            {
                t = lexer.Next();
                // Collapse runs of newlines; they only matter as attribute terminators.
                if (t.Kind == TokenKind.Newline && tokens.Count > 0 && tokens[^1].Kind == TokenKind.Newline)
                {
                    continue;
                }
                tokens.Add(t);
            } while (t.Kind != TokenKind.Eof);
            return tokens;
        }

        private SourcePos Pos => new(_file, _line, _col);
        private char Peek(int offset = 0) => _i + offset < _src.Length ? _src[_i + offset] : '\0';
        private bool AtEnd => _i >= _src.Length;

        private char Advance()
        {
            char c = _src[_i++];
            if (c == '\n')
            {
                _line++;
                _col = 1;
            }
            else
            {
                _col++;
            }
            return c;
        }

        private DeclaradException Error(string message, SourcePos? pos = null) => new(message, pos ?? Pos);

        public Token Next()
        {
            SkipSpaceAndComments();
            var start = Pos;
            if (AtEnd)
            {
                return new Token(TokenKind.Eof, "", start);
            }

            char c = Peek();
            if (c == '\n')
            {
                Advance();
                return new Token(TokenKind.Newline, "\n", start);
            }
            if (char.IsLetter(c) || c == '_')
            {
                int s = _i;
                while (!AtEnd && (char.IsLetterOrDigit(Peek()) || Peek() == '_' || Peek() == '-'))
                {
                    Advance();
                }
                return new Token(TokenKind.Ident, _src[s.._i], start);
            }
            if (char.IsAsciiDigit(c))
            {
                return ScanNumber(start);
            }
            if (c == '"')
            {
                Advance();
                var parts = ScanQuotedTemplate(start);
                return new Token(TokenKind.Template, "\"", start, parts);
            }
            if (c == '<' && Peek(1) == '<' && (Peek(2) == '-' || char.IsLetter(Peek(2)) || Peek(2) == '_'))
            {
                return ScanHeredoc(start);
            }

            Advance();
            Token Two(char next, TokenKind kind, string text, TokenKind single)
            {
                if (Peek() == next)
                {
                    Advance();
                    return new Token(kind, text, start);
                }
                return new Token(single, c.ToString(), start);
            }

            switch (c)
            {
                case '{': return new Token(TokenKind.LBrace, "{", start);
                case '}': return new Token(TokenKind.RBrace, "}", start);
                case '[': return new Token(TokenKind.LBracket, "[", start);
                case ']': return new Token(TokenKind.RBracket, "]", start);
                case '(': return new Token(TokenKind.LParen, "(", start);
                case ')': return new Token(TokenKind.RParen, ")", start);
                case ',': return new Token(TokenKind.Comma, ",", start);
                case ':': return new Token(TokenKind.Colon, ":", start);
                case '?': return new Token(TokenKind.Question, "?", start);
                case '+': return new Token(TokenKind.Plus, "+", start);
                case '-': return new Token(TokenKind.Minus, "-", start);
                case '*': return new Token(TokenKind.Star, "*", start);
                case '/': return new Token(TokenKind.Slash, "/", start);
                case '%': return new Token(TokenKind.Percent, "%", start);
                case '.':
                    if (Peek() == '.' && Peek(1) == '.')
                    {
                        Advance();
                        Advance();
                        return new Token(TokenKind.Ellipsis, "...", start);
                    }
                    return new Token(TokenKind.Dot, ".", start);
                case '=':
                    if (Peek() == '>')
                    {
                        Advance();
                        return new Token(TokenKind.FatArrow, "=>", start);
                    }
                    return Two('=', TokenKind.EqualEqual, "==", TokenKind.Equals);
                case '!': return Two('=', TokenKind.NotEqual, "!=", TokenKind.Bang);
                case '<': return Two('=', TokenKind.LessEqual, "<=", TokenKind.Less);
                case '>': return Two('=', TokenKind.GreaterEqual, ">=", TokenKind.Greater);
                case '&' when Peek() == '&':
                    Advance();
                    return new Token(TokenKind.AndAnd, "&&", start);
                case '|' when Peek() == '|':
                    Advance();
                    return new Token(TokenKind.OrOr, "||", start);
            }
            throw Error($"Unexpected character '{c}'.", start);
        }

        private Token ScanNumber(SourcePos start)
        {
            int s = _i;
            while (char.IsAsciiDigit(Peek()))
            {
                Advance();
            }
            if (Peek() == '.' && char.IsAsciiDigit(Peek(1)))
            {
                Advance();
                while (char.IsAsciiDigit(Peek()))
                {
                    Advance();
                }
            }
            if ((Peek() == 'e' || Peek() == 'E') && (char.IsAsciiDigit(Peek(1)) || ((Peek(1) == '+' || Peek(1) == '-') && char.IsAsciiDigit(Peek(2)))))
            {
                Advance();
                if (Peek() == '+' || Peek() == '-')
                {
                    Advance();
                }
                while (char.IsAsciiDigit(Peek()))
                {
                    Advance();
                }
            }
            return new Token(TokenKind.Number, _src[s.._i], start);
        }

        public static decimal ParseNumber(string text, SourcePos pos)
        {
            if (decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal d))
            {
                return d;
            }
            throw new DeclaradException($"Number '{text}' is out of range.", pos);
        }

        private void SkipSpaceAndComments()
        {
            while (!AtEnd)
            {
                char c = Peek();
                if (c == ' ' || c == '\t' || c == '\r' || c == '﻿')
                {
                    Advance();
                }
                else if (c == '#' || (c == '/' && Peek(1) == '/'))
                {
                    while (!AtEnd && Peek() != '\n')
                    {
                        Advance();
                    }
                }
                else if (c == '/' && Peek(1) == '*')
                {
                    var start = Pos;
                    Advance();
                    Advance();
                    while (!(Peek() == '*' && Peek(1) == '/'))
                    {
                        if (AtEnd)
                        {
                            throw Error("Unterminated block comment.", start);
                        }
                        Advance();
                    }
                    Advance();
                    Advance();
                }
                else
                {
                    return;
                }
            }
        }

        private List<TemplatePart> ScanQuotedTemplate(SourcePos start)
        {
            var parts = new List<TemplatePart>();
            var literal = new StringBuilder();
            while (true)
            {
                if (AtEnd || Peek() == '\n')
                {
                    throw Error("Unterminated string.", start);
                }
                char c = Advance();
                if (c == '"')
                {
                    break;
                }
                if (c == '\\')
                {
                    literal.Append(ScanEscape());
                }
                else if (!TryTemplateSequence(c, parts, literal))
                {
                    literal.Append(c);
                }
            }
            FlushLiteral(parts, literal);
            return parts;
        }

        /// <summary>Handles <c>$${</c>, <c>%%{</c>, <c>${...}</c> and <c>%{...}</c> after reading <paramref name="c"/>.</summary>
        private bool TryTemplateSequence(char c, List<TemplatePart> parts, StringBuilder literal)
        {
            if (c != '$' && c != '%')
            {
                return false;
            }
            if (Peek() == c && Peek(1) == '{')
            {
                Advance();
                Advance();
                literal.Append(c).Append('{');
                return true;
            }
            if (Peek() == '{')
            {
                FlushLiteral(parts, literal);
                parts.Add(ScanInterpolation(isDirective: c == '%'));
                return true;
            }
            return false;
        }

        private string ScanEscape()
        {
            var pos = Pos;
            if (AtEnd)
            {
                throw Error("Unterminated escape sequence.", pos);
            }
            char e = Advance();
            switch (e)
            {
                case 'n': return "\n";
                case 'r': return "\r";
                case 't': return "\t";
                case '"': return "\"";
                case '\\': return "\\";
                case 'u':
                case 'U':
                    int len = e == 'u' ? 4 : 8;
                    var hex = new StringBuilder();
                    for (int k = 0; k < len; k++)
                    {
                        if (AtEnd || !char.IsAsciiHexDigit(Peek()))
                        {
                            throw Error($"Invalid \\{e} escape sequence.", pos);
                        }
                        hex.Append(Advance());
                    }
                    int code = int.Parse(hex.ToString(), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    if (code > 0x10FFFF || (code >= 0xD800 && code <= 0xDFFF))
                    {
                        throw Error($"\\{e}{hex} is not a valid Unicode code point.", pos);
                    }
                    return char.ConvertFromUtf32(code);
                default:
                    throw Error($"Invalid escape sequence '\\{e}'.", pos);
            }
        }

        /// <summary>Called just after '$' or '%'; consumes "{ ... }" and returns the inner source.</summary>
        private InterpolationPart ScanInterpolation(bool isDirective)
        {
            var open = Pos;
            Advance(); // {
            bool stripLeft = false;
            if (Peek() == '~')
            {
                stripLeft = true;
                Advance();
            }
            var exprPos = Pos;
            int s = _i;
            int depth = 0;
            while (true)
            {
                if (AtEnd)
                {
                    throw Error("Unterminated template sequence.", open);
                }
                char c = Peek();
                if (c == '"')
                {
                    // Skip a nested string literal, which may itself contain braces.
                    var strStart = Pos;
                    Advance();
                    ScanQuotedTemplate(strStart);
                    continue;
                }
                if (c == '{')
                {
                    depth++;
                }
                else if (c == '}')
                {
                    if (depth == 0)
                    {
                        break;
                    }
                    depth--;
                }
                Advance();
            }
            string inner = _src[s.._i];
            Advance(); // }
            bool stripRight = false;
            if (inner.EndsWith('~'))
            {
                stripRight = true;
                inner = inner[..^1];
            }
            return new InterpolationPart(inner, exprPos, stripLeft, stripRight, isDirective);
        }

        private static void FlushLiteral(List<TemplatePart> parts, StringBuilder literal)
        {
            if (literal.Length > 0)
            {
                parts.Add(new LiteralPart(literal.ToString()));
                literal.Clear();
            }
        }

        private Token ScanHeredoc(SourcePos start)
        {
            Advance();
            Advance();
            bool indented = false;
            if (Peek() == '-')
            {
                indented = true;
                Advance();
            }
            int s = _i;
            while (!AtEnd && (char.IsLetterOrDigit(Peek()) || Peek() == '_'))
            {
                Advance();
            }
            string marker = _src[s.._i];
            if (marker.Length == 0)
            {
                throw Error("Expected heredoc marker after '<<'.", start);
            }
            while (Peek() == '\r' || Peek() == ' ' || Peek() == '\t')
            {
                Advance();
            }
            if (Peek() != '\n')
            {
                throw Error("Heredoc marker must be followed by a newline.", start);
            }
            Advance();

            var contentPos = Pos;
            var lines = new List<string>();
            while (true)
            {
                if (AtEnd)
                {
                    throw Error($"Unterminated heredoc; expected closing '{marker}'.", start);
                }
                int ls = _i;
                while (!AtEnd && Peek() != '\n')
                {
                    Advance();
                }
                string line = _src[ls.._i].TrimEnd('\r');
                if (line.Trim() == marker)
                {
                    break;
                }
                lines.Add(line);
                if (!AtEnd)
                {
                    Advance();
                }
            }

            if (indented)
            {
                int indent = lines.Where(l => l.Trim().Length > 0)
                    .Select(l => l.Length - l.TrimStart(' ', '\t').Length)
                    .DefaultIfEmpty(0)
                    .Min();
                lines = lines.Select(l => l.Length >= indent ? l[indent..] : l.TrimStart()).ToList();
            }

            string content = lines.Count == 0 ? "" : string.Join("\n", lines) + "\n";
            var parts = SplitTemplate(content, contentPos);
            return new Token(TokenKind.Template, "<<", start, parts);
        }

        /// <summary>Splits unquoted template text (heredocs, template files) into parts. No escapes apply.</summary>
        public static List<TemplatePart> SplitTemplate(string content, SourcePos pos)
        {
            var inner = new Lexer(content, pos.File, pos.Line, pos.Column);
            var parts = new List<TemplatePart>();
            var literal = new StringBuilder();
            while (!inner.AtEnd)
            {
                char c = inner.Advance();
                if (!inner.TryTemplateSequence(c, parts, literal))
                {
                    literal.Append(c);
                }
            }
            FlushLiteral(parts, literal);
            return parts;
        }
    }
}
