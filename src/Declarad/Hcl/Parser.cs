using Declarad.Hcl.Syntax;
using Declarad.Hcl.Values;
using Attribute = Declarad.Hcl.Syntax.Attribute;
using Index = Declarad.Hcl.Syntax.Index;

namespace Declarad.Hcl
{
    /// <summary>
    /// Recursive-descent parser for HCL native syntax, producing a <see cref="Body"/> of attributes and blocks.
    /// Expressions follow HCL precedence: <c>?:</c>, <c>||</c>, <c>&amp;&amp;</c>, <c>== !=</c>,
    /// <c>&lt; &lt;= &gt; &gt;=</c>, <c>+ -</c>, <c>* / %</c>, unary <c>! -</c>, then traversal and splat.
    /// </summary>
    public sealed class Parser
    {
        private static readonly TokenKind[][] BinaryLevels =
        {
            new[] { TokenKind.OrOr },
            new[] { TokenKind.AndAnd },
            new[] { TokenKind.EqualEqual, TokenKind.NotEqual },
            new[] { TokenKind.Less, TokenKind.LessEqual, TokenKind.Greater, TokenKind.GreaterEqual },
            new[] { TokenKind.Plus, TokenKind.Minus },
            new[] { TokenKind.Star, TokenKind.Slash, TokenKind.Percent },
        };

        private readonly List<Token> _tokens;
        private int _p;

        private Parser(List<Token> tokens)
        {
            _tokens = tokens;
        }

        public static Body ParseFile(string source, string file)
        {
            var parser = new Parser(Lexer.Tokenize(source, file));
            var body = parser.ParseBody(new SourcePos(file, 1, 1), topLevel: true);
            parser.Expect(TokenKind.Eof);
            return body;
        }

        public static Expr ParseExpression(string source, SourcePos pos)
        {
            var parser = new Parser(Lexer.Tokenize(source, pos.File, pos.Line, pos.Column));
            parser._ignoreNewlines.Push(true);
            parser.SkipNewlines();
            var expr = parser.ParseExpr();
            parser.SkipNewlines();
            parser.Expect(TokenKind.Eof);
            return expr;
        }

        /// <summary>Parses a whole file as a template (used by templatefile()).</summary>
        public static Expr ParseTemplate(string source, string file)
        {
            var pos = new SourcePos(file, 1, 1);
            return BuildTemplate(Lexer.SplitTemplate(source, pos), pos);
        }

        // HCL: newlines are ignored inside ( ), [ ] and interpolations, but separate items inside { }.
        private readonly Stack<bool> _ignoreNewlines = new();
        private bool IgnoringNewlines => _ignoreNewlines.Count > 0 && _ignoreNewlines.Peek();

        private int Skip(int index)
        {
            while (IgnoringNewlines && index < _tokens.Count - 1 && _tokens[index].Kind == TokenKind.Newline)
            {
                index++;
            }
            return index;
        }

        private Token Peek(int offset = 0)
        {
            int index = Skip(_p);
            for (int i = 0; i < offset; i++)
            {
                index = Skip(Math.Min(index + 1, _tokens.Count - 1));
            }
            return _tokens[Math.Min(index, _tokens.Count - 1)];
        }

        private Token Advance()
        {
            _p = Skip(_p);
            return _tokens[_p < _tokens.Count - 1 ? _p++ : _p];
        }

        private T Nested<T>(bool ignoreNewlines, Func<T> parse)
        {
            _ignoreNewlines.Push(ignoreNewlines);
            try
            {
                return parse();
            }
            finally
            {
                _ignoreNewlines.Pop();
            }
        }
        private bool Check(TokenKind kind) => Peek().Kind == kind;

        private bool Match(TokenKind kind)
        {
            if (Check(kind))
            {
                Advance();
                return true;
            }
            return false;
        }

        private Token Expect(TokenKind kind, string? what = null)
        {
            if (!Check(kind))
            {
                throw new DeclaradException($"Expected {what ?? Describe(kind)} but found {Peek()}.", Peek().Pos);
            }
            return Advance();
        }

        private static string Describe(TokenKind kind) => kind switch
        {
            TokenKind.Ident => "identifier",
            TokenKind.RBrace => "'}'",
            TokenKind.LBrace => "'{'",
            TokenKind.RBracket => "']'",
            TokenKind.RParen => "')'",
            TokenKind.Colon => "':'",
            TokenKind.Eof => "end of file",
            _ => kind.ToString(),
        };

        private void SkipNewlines()
        {
            while (Check(TokenKind.Newline))
            {
                Advance();
            }
        }

        private Body ParseBody(SourcePos pos, bool topLevel)
        {
            var body = new Body { Pos = pos };
            while (true)
            {
                SkipNewlines();
                if (Check(TokenKind.Eof) || (!topLevel && Check(TokenKind.RBrace)))
                {
                    return body;
                }

                var name = Expect(TokenKind.Ident, "attribute or block name");
                if (Match(TokenKind.Equals))
                {
                    if (body.Attributes.Any(a => a.Name == name.Text))
                    {
                        throw new DeclaradException($"Attribute '{name.Text}' is defined more than once.", name.Pos);
                    }
                    var expr = ParseExpr();
                    body.Attributes.Add(new Attribute(name.Text, expr, name.Pos));
                }
                else
                {
                    var labels = new List<string>();
                    while (!Check(TokenKind.LBrace))
                    {
                        var label = Advance();
                        if (label.Kind == TokenKind.Ident)
                        {
                            labels.Add(label.Text);
                        }
                        else if (label.Kind == TokenKind.Template && label.Parts!.All(p => p is LiteralPart))
                        {
                            labels.Add(string.Concat(label.Parts!.Cast<LiteralPart>().Select(p => p.Text)));
                        }
                        else
                        {
                            throw new DeclaradException($"Expected '=' or block label after '{name.Text}' but found {label}.", label.Pos);
                        }
                    }
                    Expect(TokenKind.LBrace);
                    var inner = Nested(false, () => ParseBody(name.Pos, topLevel: false));
                    Expect(TokenKind.RBrace);
                    body.Blocks.Add(new Block(name.Text, labels, inner, name.Pos));
                }

                if (!Check(TokenKind.Newline) && !Check(TokenKind.Eof) && !(!topLevel && Check(TokenKind.RBrace)))
                {
                    throw new DeclaradException($"Expected newline after '{name.Text}' but found {Peek()}.", Peek().Pos);
                }
            }
        }

        private Expr ParseExpr()
        {
            var cond = ParseBinary(0);
            if (Match(TokenKind.Question))
            {
                SkipNewlines();
                var whenTrue = ParseExpr();
                SkipNewlines();
                Expect(TokenKind.Colon);
                SkipNewlines();
                var whenFalse = ParseExpr();
                return new ConditionalExpr(cond, whenTrue, whenFalse, cond.Pos);
            }
            return cond;
        }

        private Expr ParseBinary(int level)
        {
            if (level == BinaryLevels.Length)
            {
                return ParseUnary();
            }
            var left = ParseBinary(level + 1);
            while (BinaryLevels[level].Contains(Peek().Kind))
            {
                var op = Advance();
                SkipNewlines();
                left = new BinaryExpr(op.Kind, left, ParseBinary(level + 1), op.Pos);
            }
            return left;
        }

        private Expr ParseUnary()
        {
            if (Check(TokenKind.Bang))
            {
                var op = Advance();
                return new NotExpr(ParseUnary(), op.Pos);
            }
            if (Check(TokenKind.Minus))
            {
                var op = Advance();
                var operand = ParseUnary();
                return operand is LiteralExpr { Value: NumberValue n }
                    ? new LiteralExpr(Value.From(-n.V), op.Pos)
                    : new NegateExpr(operand, op.Pos);
            }
            return ParsePostfix();
        }

        private Expr ParsePostfix()
        {
            var primary = ParsePrimary();
            var traversal = new List<Traverser>();
            while (true)
            {
                if (Check(TokenKind.Dot))
                {
                    var dot = Advance();
                    var next = Advance();
                    if (next.Kind == TokenKind.Ident)
                    {
                        traversal.Add(new GetAttr(next.Text, dot.Pos));
                    }
                    else if (next.Kind == TokenKind.Number && next.Text.Split('.').All(part => long.TryParse(part, out _)))
                    {
                        // Legacy index syntax: x.0 and x.0.1 (the lexer reads "0.1" as one number).
                        foreach (string part in next.Text.Split('.'))
                        {
                            traversal.Add(new Index(new LiteralExpr(Value.From(long.Parse(part)), next.Pos), dot.Pos));
                        }
                    }
                    else if (next.Kind == TokenKind.Star)
                    {
                        traversal.Add(new Splat(dot.Pos, Legacy: true));
                    }
                    else
                    {
                        throw new DeclaradException($"Expected attribute name after '.' but found {next}.", next.Pos);
                    }
                }
                else if (Check(TokenKind.LBracket))
                {
                    var open = Advance();
                    SkipNewlines();
                    if (Check(TokenKind.Star) && Peek(1).Kind == TokenKind.RBracket)
                    {
                        Advance();
                        Advance();
                        traversal.Add(new Splat(open.Pos));
                        continue;
                    }
                    var key = Nested(true, () =>
                    {
                        var k = ParseExpr();
                        Expect(TokenKind.RBracket);
                        return k;
                    });
                    traversal.Add(new Index(key, open.Pos));
                }
                else
                {
                    break;
                }
            }

            if (traversal.Count == 0)
            {
                return primary;
            }
            if (primary is ScopeTraversalExpr root && root.Traversal.Count == 0)
            {
                return new ScopeTraversalExpr(root.Root, traversal, root.Pos);
            }
            return new RelativeTraversalExpr(primary, traversal, primary.Pos);
        }

        private Expr ParsePrimary()
        {
            var t = Advance();
            switch (t.Kind)
            {
                case TokenKind.Number:
                    return new LiteralExpr(Value.From(Lexer.ParseNumber(t.Text, t.Pos)), t.Pos);

                case TokenKind.Template:
                    return BuildTemplate(t.Parts!, t.Pos);

                case TokenKind.Ident:
                    switch (t.Text)
                    {
                        case "true": return new LiteralExpr(Value.True, t.Pos);
                        case "false": return new LiteralExpr(Value.False, t.Pos);
                        case "null": return new LiteralExpr(Value.Null, t.Pos);
                    }
                    if (Match(TokenKind.LParen))
                    {
                        return Nested(true, () => ParseCall(t));
                    }
                    return new ScopeTraversalExpr(t.Text, Array.Empty<Traverser>(), t.Pos);

                case TokenKind.LParen:
                    return Nested(true, () =>
                    {
                        var inner = ParseExpr();
                        Expect(TokenKind.RParen);
                        return inner;
                    });

                case TokenKind.LBracket:
                    return Nested(true, () => ParseList(t));

                case TokenKind.LBrace:
                    return Nested(false, () => ParseObject(t));
            }
            throw new DeclaradException($"Expected an expression but found {t}.", t.Pos);
        }

        private Expr ParseList(Token t)
        {
                SkipNewlines();
                if (IsForKeyword())
                {
                    return ParseFor(t, TokenKind.RBracket);
                }
                var items = new List<Expr>();
                while (!Check(TokenKind.RBracket))
                {
                    items.Add(ParseExpr());
                    SkipNewlines();
                    if (!Match(TokenKind.Comma))
                    {
                        break;
                    }
                    SkipNewlines();
                }
                SkipNewlines();
                Expect(TokenKind.RBracket);
                return new ListExpr(items, t.Pos);
        }

        private Expr ParseObject(Token t)
        {
                SkipNewlines();
                if (IsForKeyword())
                {
                    return ParseFor(t, TokenKind.RBrace);
                }
                var items = new List<(Expr, Expr)>();
                while (!Check(TokenKind.RBrace))
                {
                    Expr key;
                    if (Check(TokenKind.Ident) && Peek(1).Kind is TokenKind.Equals or TokenKind.Colon)
                    {
                        var k = Advance();
                        key = new LiteralExpr(Value.From(k.Text), k.Pos);
                    }
                    else
                    {
                        key = ParseExpr();
                    }
                    if (!Match(TokenKind.Equals))
                    {
                        Expect(TokenKind.Colon, "'=' or ':'");
                    }
                    SkipNewlines();
                    items.Add((key, ParseExpr()));
                    // Items are separated by a comma, a newline, or both.
                    bool separated = Match(TokenKind.Comma);
                    separated |= Check(TokenKind.Newline);
                    SkipNewlines();
                    separated |= Match(TokenKind.Comma);
                    SkipNewlines();
                    if (!separated)
                    {
                        break;
                    }
                }
                Expect(TokenKind.RBrace);
                return new ObjectExpr(items, t.Pos);
        }

        private Expr ParseCall(Token name)
        {
            var args = new List<Expr>();
            bool expand = false;
            SkipNewlines();
            while (!Check(TokenKind.RParen))
            {
                args.Add(ParseExpr());
                SkipNewlines();
                if (Match(TokenKind.Ellipsis))
                {
                    expand = true;
                    SkipNewlines();
                    break;
                }
                if (!Match(TokenKind.Comma))
                {
                    break;
                }
                SkipNewlines();
            }
            SkipNewlines();
            Expect(TokenKind.RParen);
            return new FunctionCallExpr(name.Text, args, name.Pos, expand);
        }

        private bool IsForKeyword() => Check(TokenKind.Ident) && Peek().Text == "for" && Peek(1).Kind == TokenKind.Ident;

        private (string? Key, string Value, Expr Collection) ParseForHeader()
        {
            Advance(); // for
            string first = Expect(TokenKind.Ident).Text;
            string? keyVar = null;
            string valueVar = first;
            if (Match(TokenKind.Comma))
            {
                keyVar = first;
                valueVar = Expect(TokenKind.Ident).Text;
            }
            var inToken = Expect(TokenKind.Ident, "'in'");
            if (inToken.Text != "in")
            {
                throw new DeclaradException($"Expected 'in' but found '{inToken.Text}'.", inToken.Pos);
            }
            return (keyVar, valueVar, ParseExpr());
        }

        private Expr ParseFor(Token open, TokenKind close)
        {
            var (keyVar, valueVar, collection) = ParseForHeader();
            SkipNewlines();
            Expect(TokenKind.Colon);
            SkipNewlines();
            Expr? keyExpr = null;
            if (close == TokenKind.RBrace)
            {
                keyExpr = ParseExpr();
                SkipNewlines();
                Expect(TokenKind.FatArrow, "'=>'");
                SkipNewlines();
            }
            var valueExpr = ParseExpr();
            bool grouping = keyExpr != null && Match(TokenKind.Ellipsis);
            SkipNewlines();
            Expr? condition = null;
            if (Check(TokenKind.Ident) && Peek().Text == "if")
            {
                Advance();
                condition = ParseExpr();
                SkipNewlines();
            }
            Expect(close);
            return new ForExpr(keyVar, valueVar, collection, keyExpr, valueExpr, condition, open.Pos, grouping);
        }

        // ---- Templates -------------------------------------------------------------------------------

        private static Expr BuildTemplate(IReadOnlyList<TemplatePart> rawParts, SourcePos pos)
        {
            var parts = ApplyStripMarkers(rawParts);
            if (parts.Count == 0)
            {
                return new LiteralExpr(Value.From(""), pos);
            }
            if (parts.Count == 1 && parts[0] is LiteralPart only)
            {
                return new LiteralExpr(Value.From(only.Text), pos);
            }
            int i = 0;
            var (exprs, end) = ParseTemplateSequence(parts, ref i, pos);
            if (end != null)
            {
                throw new DeclaradException($"Unexpected '%{{ {end.Value.Keyword} }}' without a matching opening directive.", end.Value.Pos);
            }
            // Only an unadorned "${x}" yields the raw value; stripped whitespace still makes it a template.
            bool single = rawParts.Count == 1 && rawParts[0] is InterpolationPart { IsDirective: false };
            return new TemplateExpr(exprs, pos, single);
        }

        private static List<TemplatePart> ApplyStripMarkers(IReadOnlyList<TemplatePart> raw)
        {
            var parts = raw.ToList();
            for (int i = 0; i < parts.Count; i++)
            {
                if (parts[i] is not InterpolationPart ip)
                {
                    continue;
                }
                if (ip.StripLeft && i > 0 && parts[i - 1] is LiteralPart before)
                {
                    parts[i - 1] = new LiteralPart(before.Text.TrimEnd());
                }
                if (ip.StripRight && i + 1 < parts.Count && parts[i + 1] is LiteralPart after)
                {
                    parts[i + 1] = new LiteralPart(after.Text.TrimStart());
                }
            }
            return parts.Where(p => p is not LiteralPart { Text: "" }).ToList();
        }

        private readonly record struct DirectiveEnd(string Keyword, SourcePos Pos);

        /// <summary>Parses parts until a closing directive (else/endif/endfor), which is returned.</summary>
        private static (List<Expr>, DirectiveEnd?) ParseTemplateSequence(List<TemplatePart> parts, ref int i, SourcePos pos)
        {
            var exprs = new List<Expr>();
            while (i < parts.Count)
            {
                var part = parts[i++];
                if (part is LiteralPart lit)
                {
                    exprs.Add(new LiteralExpr(Value.From(lit.Text), pos));
                    continue;
                }
                var ip = (InterpolationPart)part;
                if (!ip.IsDirective)
                {
                    exprs.Add(ParseExpression(ip.Source, ip.Pos));
                    continue;
                }

                var header = new Parser(Lexer.Tokenize(ip.Source, ip.Pos.File, ip.Pos.Line, ip.Pos.Column));
                header.SkipNewlines();
                var keyword = header.Expect(TokenKind.Ident, "template directive (if, for, else, endif, endfor)");
                switch (keyword.Text)
                {
                    case "if":
                    {
                        var condition = header.ParseExpr();
                        header.SkipNewlines();
                        header.Expect(TokenKind.Eof);
                        var (thenParts, end) = ParseTemplateSequence(parts, ref i, pos);
                        TemplateExpr? elseBody = null;
                        if (end?.Keyword == "else")
                        {
                            List<Expr> elseParts;
                            (elseParts, end) = ParseTemplateSequence(parts, ref i, pos);
                            elseBody = new TemplateExpr(elseParts, pos);
                        }
                        if (end?.Keyword != "endif")
                        {
                            throw new DeclaradException("Missing '%{ endif }' for this '%{ if }'.", keyword.Pos);
                        }
                        exprs.Add(new TemplateIfExpr(condition, new TemplateExpr(thenParts, pos), elseBody, keyword.Pos));
                        break;
                    }
                    case "for":
                    {
                        header._p--;
                        var (keyVar, valueVar, collection) = header.ParseForHeader();
                        header.SkipNewlines();
                        header.Expect(TokenKind.Eof);
                        var (body, end) = ParseTemplateSequence(parts, ref i, pos);
                        if (end?.Keyword != "endfor")
                        {
                            throw new DeclaradException("Missing '%{ endfor }' for this '%{ for }'.", keyword.Pos);
                        }
                        exprs.Add(new TemplateForExpr(keyVar, valueVar, collection, new TemplateExpr(body, pos), keyword.Pos));
                        break;
                    }
                    case "else":
                    case "endif":
                    case "endfor":
                        header.SkipNewlines();
                        header.Expect(TokenKind.Eof);
                        return (exprs, new DirectiveEnd(keyword.Text, keyword.Pos));
                    default:
                        throw new DeclaradException($"Unknown template directive '{keyword.Text}'.", keyword.Pos);
                }
            }
            return (exprs, null);
        }
    }
}
