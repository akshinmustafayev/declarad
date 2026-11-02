using Declarad.Directory;

namespace Declarad.Config
{
    public sealed record LoadOptions
    {
        public IReadOnlyDictionary<string, string>? Vars { get; init; }
        public Func<string, string?>? GetEnv { get; init; }

        /// <summary>Opens a directory connection for data sources. When null (offline), data sources are unknown.</summary>
        public Func<ConnectionSettings, IDirectory>? Connect { get; init; }

        public Func<string, string?>? Prompt { get; init; }

        /// <summary>
        /// Labels to compute label-derived defaults from, by resource address, instead of the block label: keeps the
        /// values of renamed blocks (see <see cref="LabelRenames"/>).
        /// </summary>
        public IReadOnlyDictionary<string, string>? LabelDefaults { get; init; }

        /// <summary>Reads the .dad files of a module directory; defaults to the file system.</summary>
        public Func<string, IReadOnlyList<(string File, string Source)>>? ReadModule { get; init; }
    }
}
