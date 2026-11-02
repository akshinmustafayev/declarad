using Declarad.Hcl.Values;

namespace Declarad.State
{
    /// <summary>
    /// What declarad manages: each resource address mapped to the directory object's objectGUID, so renames
    /// and moves are tracked and objects removed from the configuration can be deleted. Holds no secrets.
    /// </summary>
    public sealed class StateResource
    {
        public required string Address { get; init; }
        public required string Type { get; init; }

        /// <summary>Name of the connection (directory) the object lives in.</summary>
        public string Connection { get; init; } = StateFile.DefaultConnection;
        public Guid Guid { get; set; }
        public string Dn { get; set; } = "";

        /// <summary>Last applied values of managed attributes (non-sensitive only).</summary>
        public Dictionary<string, Value> Attributes { get; set; } = new(StringComparer.Ordinal);

        public bool PreventDestroy { get; set; }

        public Value Get(string name) => Attributes.TryGetValue(name, out var v) ? v : Value.Null;
    }
}
