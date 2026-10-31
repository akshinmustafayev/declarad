namespace Declarad
{
    /// <summary>
    /// A user-facing error. The CLI prints the message (and position, if any) without a stack trace.
    /// </summary>
    public class DeclaradException : Exception
    {
        public SourcePos Pos { get; }

        public DeclaradException(string message, SourcePos pos = default, Exception? inner = null)
            : base(message, inner)
        {
            Pos = pos;
        }

        public string Describe() => Pos.Line > 0 || !string.IsNullOrEmpty(Pos.File) ? $"{Pos}: {Message}" : Message;
    }
}
