namespace Declarad.Config
{
    /// <summary>A <c>moved</c> block: state recorded under <see cref="From"/> now belongs to <see cref="To"/>.</summary>
    public sealed record MoveSpec(string From, string To, SourcePos Pos);
}
