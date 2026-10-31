namespace Declarad
{
    /// <summary>A position in a configuration file, used for error reporting.</summary>
    public readonly record struct SourcePos(string File, int Line, int Column)
    {
        public static readonly SourcePos None = new("", 0, 0);

        public override string ToString() => Line == 0 ? File : $"{File}:{Line}:{Column}";
    }
}
