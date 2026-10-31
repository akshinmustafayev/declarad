using Cocona;

namespace Declarad.Cli
{
    /// <summary>Options shared by every command.</summary>
    public sealed class GlobalOptions : ICommandParameterSet
    {
        [Option('C', Description = "Configuration directory (prefer the global form before the subcommand: declarad -chdir=DIR <command>)")]
        [HasDefaultValue]
        public string? Dir { get; set; }

        [Option(Description = "Use a local state file at this path instead of the configured backend")]
        [HasDefaultValue]
        public string? State { get; set; }

        [Option("var", Description = "Set a variable: --var name=value (repeatable)")]
        [HasDefaultValue]
        public string[] Vars { get; set; } = Array.Empty<string>();

        [Option("backend-config", Description = "Backend setting key=value, or a file of settings (repeatable)")]
        [HasDefaultValue]
        public string[] BackendConfig { get; set; } = Array.Empty<string>();

        [Option("lock", Description = "Lock the state while changing it (--lock=false to disable)")]
        [HasDefaultValue]
        public bool Lock { get; set; } = true;

        [Option("lock-timeout", Description = "How long to wait for a state lock, e.g. 30s or 5m (default 0s)")]
        [HasDefaultValue]
        public string LockTimeout { get; set; } = "0s";

        [Option('v', Description = "Verbose (debug) logging")]
        [HasDefaultValue]
        public bool Verbose { get; set; }

        [Option("no-color", Description = "Disable colored output")]
        [HasDefaultValue]
        public bool NoColor { get; set; }

        public string ConfigDir => Path.GetFullPath(Dir ?? Environment.CurrentDirectory);

        /// <summary>Relative paths are relative to -C, like Terraform's -chdir.</summary>
        public string Resolve(string path) => Path.GetFullPath(Path.Combine(ConfigDir, path));
    }
}
