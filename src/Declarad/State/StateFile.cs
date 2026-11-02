using System.Text.Json.Nodes;
using Declarad.Hcl.Values;

namespace Declarad.State
{
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
                if (r.LabelDefault != null)
                {
                    obj["label_default"] = r.LabelDefault;
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
                    LabelDefault = node["label_default"]?.GetValue<string>(),
                    Connection = node["connection"]?.GetValue<string>() ?? DefaultConnection,
                });
            }
            return state;
        }
    }
}
