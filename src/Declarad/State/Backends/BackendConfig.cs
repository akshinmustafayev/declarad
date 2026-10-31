using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Declarad.State.Backends
{
    /// <summary>A backend block: type and string-valued settings (nested blocks are flattened as "endpoints.s3").</summary>
    public sealed record BackendConfig(string Type, IReadOnlyDictionary<string, string> Settings)
    {
        public static BackendConfig LocalDefault(string dir) =>
            new("local", new Dictionary<string, string> { ["path"] = System.IO.Path.Combine(dir, StateStore.DefaultFileName) });

        public string? Get(string key) => Settings.TryGetValue(key, out var v) ? v : null;

        public bool GetBool(string key, bool defaultValue) => Get(key) is { } v ? v is "true" or "1" : defaultValue;

        public string Require(string key) => Get(key) ?? throw new DeclaradException($"The {Type} backend needs '{key}'.");

        public string Hash()
        {
            var text = Type + "\n" + string.Join("\n", Settings.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}"));
            return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        }

        public JsonObject ToJson()
        {
            var settings = new JsonObject();
            foreach (var (k, v) in Settings.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                settings[k] = v;
            }
            return new JsonObject { ["type"] = Type, ["config"] = settings, ["hash"] = Hash() };
        }

        public static BackendConfig FromJson(JsonNode node) => new(
            node["type"]!.GetValue<string>(),
            (node["config"]?.AsObject() ?? new JsonObject()).ToDictionary(kv => kv.Key, kv => kv.Value!.GetValue<string>(), StringComparer.Ordinal));
    }
}
