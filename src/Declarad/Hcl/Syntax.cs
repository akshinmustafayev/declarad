namespace Declarad.Hcl
{
    public sealed class Body
    {
        public List<Attribute> Attributes { get; } = new();
        public List<Block> Blocks { get; } = new();
        public SourcePos Pos { get; init; }

        public Attribute? GetAttribute(string name) => Attributes.FirstOrDefault(a => a.Name == name);
    }

    public sealed record Attribute(string Name, Expr Expr, SourcePos Pos);

    public sealed record Block(string Type, IReadOnlyList<string> Labels, Body Body, SourcePos Pos);

    public abstract record Expr(SourcePos Pos);

    public sealed record LiteralExpr(Value Value, SourcePos Pos) : Expr(Pos);

    /// <summary>
    /// A string template. Parts are literals, interpolated expressions and directives. When the template is
    /// exactly one interpolation (<c>"${x}"</c>), <see cref="SingleInterpolation"/> is set and the raw value is returned.
    /// </summary>
    public sealed record TemplateExpr(IReadOnlyList<Expr> Parts, SourcePos Pos, bool SingleInterpolation = false) : Expr(Pos);

    /// <summary><c>%{ if cond }...%{ else }...%{ endif }</c></summary>
    public sealed record TemplateIfExpr(Expr Condition, TemplateExpr Then, TemplateExpr? Else, SourcePos Pos) : Expr(Pos);

    /// <summary><c>%{ for k, v in coll }...%{ endfor }</c></summary>
    public sealed record TemplateForExpr(string? KeyVar, string ValueVar, Expr Collection, TemplateExpr Body, SourcePos Pos) : Expr(Pos);

    public abstract record Traverser(SourcePos Pos);
    public sealed record GetAttr(string Name, SourcePos Pos) : Traverser(Pos);
    public sealed record Index(Expr Key, SourcePos Pos) : Traverser(Pos);

    /// <summary>
    /// <c>[*]</c>: the remaining traversal is applied to every element. The legacy <c>.*</c> form only carries
    /// the following attribute accesses into the elements; later indexes apply to the resulting list.
    /// </summary>
    public sealed record Splat(SourcePos Pos, bool Legacy = false) : Traverser(Pos);

    /// <summary>A variable-rooted reference such as <c>var.domain</c> or <c>ou.sales.dn</c>.</summary>
    public sealed record ScopeTraversalExpr(string Root, IReadOnlyList<Traverser> Traversal, SourcePos Pos) : Expr(Pos)
    {
        public override string ToString() => Root + string.Concat(Traversal.Select(t => t switch
        {
            GetAttr g => "." + g.Name,
            Splat => "[*]",
            Index { Key: LiteralExpr { Value: StringValue s } } => $"[{HclWriter.Quote(s.V)}]",
            Index { Key: LiteralExpr { Value: NumberValue n } } => $"[{n.ToHcl()}]",
            _ => "[...]",
        }));
    }

    /// <summary>Attribute/index access applied to an arbitrary expression, e.g. <c>split(",", x)[0]</c>.</summary>
    public sealed record RelativeTraversalExpr(Expr Source, IReadOnlyList<Traverser> Traversal, SourcePos Pos) : Expr(Pos);

    /// <summary>A function call; <see cref="ExpandFinal"/> is set for <c>f(a, list...)</c>.</summary>
    public sealed record FunctionCallExpr(string Name, IReadOnlyList<Expr> Args, SourcePos Pos, bool ExpandFinal = false) : Expr(Pos);

    public sealed record ListExpr(IReadOnlyList<Expr> Items, SourcePos Pos) : Expr(Pos);

    public sealed record ObjectExpr(IReadOnlyList<(Expr Key, Expr Value)> Items, SourcePos Pos) : Expr(Pos);

    public sealed record ConditionalExpr(Expr Condition, Expr True, Expr False, SourcePos Pos) : Expr(Pos);

    /// <summary>Logical, comparison and arithmetic operators.</summary>
    public sealed record BinaryExpr(TokenKind Op, Expr Left, Expr Right, SourcePos Pos) : Expr(Pos);

    public sealed record NotExpr(Expr Operand, SourcePos Pos) : Expr(Pos);

    public sealed record NegateExpr(Expr Operand, SourcePos Pos) : Expr(Pos);

    /// <summary>
    /// <c>[for v in coll : expr if cond]</c> or <c>{for k, v in coll : key => value if cond}</c>;
    /// <see cref="Grouping"/> is set for <c>key => value...</c>, which collects values per key into lists.
    /// </summary>
    public sealed record ForExpr(string? KeyVar, string ValueVar, Expr Collection, Expr? KeyExpr, Expr ValueExpr, Expr? Condition, SourcePos Pos, bool Grouping = false) : Expr(Pos)
    {
        public bool IsObject => KeyExpr != null;
    }
}
