namespace Declarad
{
    /// <summary>A position in a configuration file, used for error reporting.</summary>
    public readonly record struct SourcePos(string File, int Line, int Column)
    {
        public static readonly SourcePos None = new("", 0, 0);

        public override string ToString() => Line == 0 ? File : $"{File}:{Line}:{Column}";
    }

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

    /// <summary>Several configuration errors collected in one pass so the user can fix them together.</summary>
    public class ConfigErrorsException : DeclaradException
    {
        public IReadOnlyList<DeclaradException> Errors { get; }

        public ConfigErrorsException(IReadOnlyList<DeclaradException> errors)
            : base(errors.Count == 1 ? errors[0].Message : $"{errors.Count} configuration errors", errors.Count == 1 ? errors[0].Pos : default)
        {
            Errors = errors;
        }
    }
}
