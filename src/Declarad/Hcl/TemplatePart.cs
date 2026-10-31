namespace Declarad.Hcl
{
    /// <summary>Part of a string template: literal text, an interpolation <c>${...}</c> or a directive <c>%{...}</c>.</summary>
    public abstract record TemplatePart;

    public sealed record LiteralPart(string Text) : TemplatePart;

    /// <summary>
    /// The source of an interpolation or directive. <paramref name="StripLeft"/>/<paramref name="StripRight"/>
    /// are the <c>~</c> markers that trim whitespace from the adjacent literal text.
    /// </summary>
    public sealed record InterpolationPart(string Source, SourcePos Pos, bool StripLeft = false, bool StripRight = false, bool IsDirective = false) : TemplatePart;
}
