using Declarad.Directory;
using Declarad.Resources.Schema;

namespace Declarad.Config
{
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
}
