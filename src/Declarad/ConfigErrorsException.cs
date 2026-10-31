namespace Declarad
{
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
