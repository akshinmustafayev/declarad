using Declarad.Hcl;
using Declarad.Hcl.Values;
using Declarad.Resources;
using Declarad.Resources.Schema;

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
}
