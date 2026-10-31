namespace Declarad.Cli
{
    /// <summary>
    /// Terraform's global <c>-chdir=DIR</c> option: given before the subcommand, it switches the working directory
    /// before anything else runs, so configuration, state, and every relative path in other options are found
    /// there. <c>-C DIR</c> is accepted as a short alias in the same position.
    /// </summary>
    public static class Chdir
    {
        public sealed record Result(string? Directory, string[] Args);

        public static Result Parse(string[] args)
        {
            string? directory = null;
            var rest = new List<string>();
            int i = 0;
            for (; i < args.Length && args[i].StartsWith('-'); i++)
            {
                string arg = args[i];
                string? value = null;
                if (arg.StartsWith("-chdir=", StringComparison.Ordinal) || arg.StartsWith("--chdir=", StringComparison.Ordinal) || arg.StartsWith("-C=", StringComparison.Ordinal))
                {
                    value = arg[(arg.IndexOf('=') + 1)..];
                }
                else if (arg is "-chdir" or "--chdir" or "-C")
                {
                    value = i + 1 < args.Length ? args[++i] : throw new DeclaradException($"The {arg} option needs a directory, e.g. -chdir=environments/prod.");
                }
                else
                {
                    rest.Add(arg);
                    continue;
                }
                if (directory != null)
                {
                    throw new DeclaradException("The -chdir option may only be given once.");
                }
                if (value.Length == 0)
                {
                    throw new DeclaradException("The -chdir option needs a directory, e.g. -chdir=environments/prod.");
                }
                directory = value;
            }
            for (; i < args.Length; i++)
            {
                if (args[i].StartsWith("-chdir", StringComparison.Ordinal) || args[i].StartsWith("--chdir", StringComparison.Ordinal))
                {
                    throw new DeclaradException("The -chdir option must be given before the subcommand, e.g. declarad -chdir=environments/prod plan.");
                }
                rest.Add(args[i]);
            }
            return new Result(directory, rest.ToArray());
        }

        /// <summary>Applies -chdir; returns the remaining arguments, or null after printing an error.</summary>
        public static string[]? Apply(string[] args)
        {
            try
            {
                var result = Parse(args);
                if (result.Directory != null)
                {
                    if (!System.IO.Directory.Exists(result.Directory))
                    {
                        throw new DeclaradException($"-chdir: directory '{result.Directory}' does not exist.");
                    }
                    Environment.CurrentDirectory = Path.GetFullPath(result.Directory);
                }
                return result.Args;
            }
            catch (DeclaradException ex)
            {
                new ConsoleUi().Error(ex.Message);
                return null;
            }
        }
    }
}
