using Declarad.Directory;
using Declarad.Hcl;
using Declarad.Resources;

namespace Declarad.Config
{
    /// <summary>One concrete object the configuration declares (a block, or one for_each/count element of it).</summary>
    public sealed class ResourceInstance
    {
        public required ResourceType Type { get; init; }
        public required string Name { get; init; }

        /// <summary>The for_each key (string) or count index (number); null for single instances.</summary>
        public Value? Key { get; init; }

        /// <summary>"" for the root module, otherwise e.g. <c>module.team["eng"]</c>.</summary>
        public string ModulePath { get; init; } = "";

        /// <summary>The connection (directory) this object lives in.</summary>
        public string Connection { get; init; } = State.StateFile.DefaultConnection;

        public SourcePos Pos { get; init; }

        public string Address => FormatAddress(ModulePath, Type.Name, Name, Key);

        /// <summary>Every schema attribute, with defaults applied; unset attributes are <see cref="Value.Null"/>.</summary>
        public Dictionary<string, Value> Attributes { get; } = new(StringComparer.Ordinal);

        public string Dn { get; set; } = "";

        /// <summary>Some values depend on the directory and were unknown (offline validation only).</summary>
        public bool HasUnknowns { get; set; }

        public bool PreventDestroy { get; set; }
        public HashSet<string> IgnoreChanges { get; } = new(StringComparer.Ordinal);

        /// <summary>Addresses this instance must be applied after (from references and depends_on).</summary>
        public HashSet<string> DependsOn { get; } = new(StringComparer.Ordinal);

        /// <summary>Every resource this instance refers to, including deferred references such as members (used by --target).</summary>
        public HashSet<string> References { get; } = new(StringComparer.Ordinal);

        /// <summary>Attributes computed from sensitive variables; their values are redacted in plans.</summary>
        public HashSet<string> SensitiveAttributes { get; } = new(StringComparer.Ordinal);

        /// <summary>Source positions of explicitly set attributes, for error messages.</summary>
        public Dictionary<string, SourcePos> AttributePos { get; } = new(StringComparer.Ordinal);

        public Value Get(string name) => Attributes.TryGetValue(name, out var v) ? v : Value.Null;

        public static string FormatKey(Value key) => key is NumberValue n ? $"[{n.ToHcl()}]" : $"[{HclWriter.Quote(V.Str(key) ?? "")}]";

        public static string FormatAddress(string modulePath, string type, string name, Value? key) =>
            (modulePath.Length > 0 ? modulePath + "." : "") + $"{type}.{name}" + (key == null ? "" : FormatKey(key));
    }

    public sealed record OutputValue(string Name, Value Value, bool Sensitive, string? Description);

    /// <summary>A <c>moved</c> block: state recorded under <see cref="From"/> now belongs to <see cref="To"/>.</summary>
    public sealed record MoveSpec(string From, string To, SourcePos Pos);

    /// <summary>An <c>import</c> block: adopt the object identified by <see cref="Id"/> (DN or GUID) as <see cref="To"/>.</summary>
    public sealed record ImportSpec(string To, string Id, SourcePos Pos);

    /// <summary>One connection block: a directory declarad can manage objects in.</summary>
    public sealed class ConnectionConfig
    {
        public required string Name { get; init; }
        public required string Domain { get; init; }
        public required string BaseDn { get; init; }
        public string? Server { get; init; }
        public string ForestBaseDn { get; init; } = "";
        public SourcePos Pos { get; init; }
        public required Func<Func<string, string?>, Func<string, string?>?, ConnectionSettings> Resolver { get; init; }

        public SchemaContext Schema => new(Domain, BaseDn, Server, ForestBaseDn.Length > 0 ? ForestBaseDn : BaseDn);
    }

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

    public sealed record LoadOptions
    {
        public IReadOnlyDictionary<string, string>? Vars { get; init; }
        public Func<string, string?>? GetEnv { get; init; }

        /// <summary>Opens a directory connection for data sources. When null (offline), data sources are unknown.</summary>
        public Func<ConnectionSettings, IDirectory>? Connect { get; init; }

        public Func<string, string?>? Prompt { get; init; }

        /// <summary>Reads the .dad files of a module directory; defaults to the file system.</summary>
        public Func<string, IReadOnlyList<(string File, string Source)>>? ReadModule { get; init; }
    }
}
