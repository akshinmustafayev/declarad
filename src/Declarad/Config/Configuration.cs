using Declarad.Directory;

namespace Declarad.Config
{
    public sealed class Configuration
    {
        public required string Directory { get; init; }
        public required IReadOnlyDictionary<string, ConnectionConfig> Connections { get; init; }
        public ProjectSettings Settings { get; init; } = new(null, null);

        private ConnectionConfig DefaultConnection =>
            Connections.TryGetValue(State.StateFile.DefaultConnection, out var c) ? c : Connections.Values.First();

        public string Domain => DefaultConnection.Domain;
        public string BaseDn => DefaultConnection.BaseDn;
        public List<ResourceInstance> Resources { get; } = new();
        public List<OutputValue> Outputs { get; } = new();
        public List<MoveSpec> Moves { get; } = new();
        public List<ImportSpec> Imports { get; } = new();
        public List<string> Warnings { get; } = new();

        /// <summary>Directory connections opened while loading (for data sources), by connection name.</summary>
        public Dictionary<string, IDirectory> OpenDirectories { get; } = new(StringComparer.Ordinal);

        public IDirectory? OpenDirectory => OpenDirectories.GetValueOrDefault(DefaultConnection.Name);

        public ResourceInstance? Find(string address) => Resources.FirstOrDefault(r => r.Address == address);

        /// <summary>
        /// Evaluates the connection block. Missing username/password fall back to DECLARAD_USERNAME /
        /// DECLARAD_PASSWORD, then to <paramref name="prompt"/> (interactive input), if given.
        /// </summary>
        public ConnectionSettings ResolveConnection(Func<string, string?> getEnv, Func<string, string?>? prompt) =>
            DefaultConnection.Resolver(getEnv, prompt);

        public ConnectionSettings ResolveConnection(string name, Func<string, string?> getEnv, Func<string, string?>? prompt) =>
            (Connections.TryGetValue(name, out var c) ? c : throw new DeclaradException($"Unknown connection '{name}'.")).Resolver(getEnv, prompt);
    }
}
