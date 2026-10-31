using System.Text.Json;
using System.Text.Json.Nodes;
using Declarad.Hcl;

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

    public sealed class StateFile
    {
        public const int CurrentVersion = 1;
        public const string DefaultConnection = "default";

        public int Version { get; set; } = CurrentVersion;
        public long Serial { get; set; }
        public string Lineage { get; set; } = Guid.NewGuid().ToString();
        public List<StateResource> Resources { get; } = new();

        /// <summary>Root module outputs from the last successful apply. Sensitive outputs are not stored.</summary>
        public SortedDictionary<string, Value> Outputs { get; } = new(StringComparer.Ordinal);

        public StateResource? Find(string address) => Resources.FirstOrDefault(r => r.Address == address);

        public void Upsert(StateResource resource)
        {
            Remove(resource.Address);
            Resources.Add(resource);
            Resources.Sort((a, b) => string.CompareOrdinal(a.Address, b.Address));
        }

        public bool Remove(string address) => Resources.RemoveAll(r => r.Address == address) > 0;

        public JsonObject ToJson()
        {
            var resources = new JsonArray();
            foreach (var r in Resources)
            {
                var attrs = new JsonObject();
                foreach (var (k, v) in r.Attributes.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                {
                    attrs[k] = v.ToJson();
                }
                var obj = new JsonObject
                {
                    ["address"] = r.Address,
                    ["type"] = r.Type,
                    ["guid"] = r.Guid.ToString(),
                    ["dn"] = r.Dn,
                    ["attributes"] = attrs,
                };
                if (r.PreventDestroy)
                {
                    obj["prevent_destroy"] = true;
                }
                if (r.Connection != DefaultConnection)
                {
                    obj["connection"] = r.Connection;
                }
                resources.Add(obj);
            }
            var outputs = new JsonObject();
            foreach (var (k, v) in Outputs)
            {
                outputs[k] = v.ToJson();
            }
            return new JsonObject
            {
                ["version"] = Version,
                ["serial"] = Serial,
                ["lineage"] = Lineage,
                ["outputs"] = outputs,
                ["resources"] = resources,
            };
        }

        public static StateFile FromJson(JsonNode root)
        {
            int version = root["version"]?.GetValue<int>() ?? 0;
            if (version != CurrentVersion)
            {
                throw new DeclaradException($"Unsupported state file version {version}; this declarad understands version {CurrentVersion}.");
            }
            var state = new StateFile
            {
                Version = version,
                Serial = root["serial"]?.GetValue<long>() ?? 0,
                Lineage = root["lineage"]?.GetValue<string>() ?? Guid.NewGuid().ToString(),
            };
            foreach (var (k, v) in root["outputs"]?.AsObject() ?? new JsonObject())
            {
                state.Outputs[k] = Value.FromJson(v);
            }
            foreach (var node in root["resources"]?.AsArray() ?? new JsonArray())
            {
                var attrs = new Dictionary<string, Value>(StringComparer.Ordinal);
                foreach (var (k, v) in node!["attributes"]?.AsObject() ?? new JsonObject())
                {
                    attrs[k] = Value.FromJson(v);
                }
                state.Resources.Add(new StateResource
                {
                    Address = node["address"]!.GetValue<string>(),
                    Type = node["type"]!.GetValue<string>(),
                    Guid = Guid.Parse(node["guid"]!.GetValue<string>()),
                    Dn = node["dn"]?.GetValue<string>() ?? "",
                    Attributes = attrs,
                    PreventDestroy = node["prevent_destroy"]?.GetValue<bool>() ?? false,
                    Connection = node["connection"]?.GetValue<string>() ?? DefaultConnection,
                });
            }
            return state;
        }
    }

    public static class StateStore
    {
        public const string DefaultFileName = "declarad.state.json";
        private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

        public static string ToText(StateFile state) => state.ToJson().ToJsonString(WriteOptions) + Environment.NewLine;

        /// <summary>Parses state JSON; empty content means "no state yet".</summary>
        public static StateFile? Parse(string text, string source)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }
            try
            {
                var node = JsonNode.Parse(text) ?? throw new DeclaradException($"State in {source} is empty.");
                return StateFile.FromJson(node);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or KeyNotFoundException
                                           or NullReferenceException or OverflowException or InvalidCastException or ArgumentException)
            {
                throw new DeclaradException($"State in {source} is corrupt: {ex.Message}", default, ex);
            }
        }

        public static StateFile Load(string path) =>
            File.Exists(path) ? Parse(File.ReadAllText(path), path) ?? new StateFile() : new StateFile();

        /// <summary>Writes atomically (temp file + rename) and keeps the previous version as <c>.backup</c>.</summary>
        public static void Save(string path, StateFile state)
        {
            state.Serial++;
            WriteFile(path, state);
        }

        internal static void WriteFile(string path, StateFile state)
        {
            string full = Path.GetFullPath(path);
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            string temp = full + ".tmp";
            File.WriteAllText(temp, ToText(state));
            if (File.Exists(full))
            {
                File.Copy(full, full + ".backup", overwrite: true);
            }
            File.Move(temp, full, overwrite: true);
        }
    }
}
