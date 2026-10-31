using System.Text.Json;
using System.Text.Json.Nodes;

namespace Declarad.Config.Modules
{
    /// <summary>Installed remote modules, keyed by module call path ("team", "team.sub"), stored in .declarad/modules/modules.json.</summary>
    public sealed class ModuleManifest
    {
        public sealed record Entry(string Key, string Source, string Dir);

        public Dictionary<string, Entry> Entries { get; } = new(StringComparer.Ordinal);

        public static string ModulesDir(string rootDir) => Path.Combine(rootDir, ProjectSettings.WorkDirName, "modules");

        private static string FilePath(string rootDir) => Path.Combine(ModulesDir(rootDir), "modules.json");

        public static ModuleManifest Load(string rootDir)
        {
            var manifest = new ModuleManifest();
            string path = FilePath(rootDir);
            if (!File.Exists(path))
            {
                return manifest;
            }
            try
            {
                foreach (var node in JsonNode.Parse(File.ReadAllText(path))?["modules"]?.AsArray() ?? new JsonArray())
                {
                    var entry = new Entry(node!["key"]!.GetValue<string>(), node["source"]!.GetValue<string>(), node["dir"]!.GetValue<string>());
                    manifest.Entries[entry.Key] = entry;
                }
            }
            catch (Exception ex) when (ex is JsonException or NullReferenceException or InvalidOperationException)
            {
                throw new DeclaradException($"{path} is corrupt ({ex.Message}). Delete it and run 'declarad init'.");
            }
            return manifest;
        }

        public void Save(string rootDir)
        {
            System.IO.Directory.CreateDirectory(ModulesDir(rootDir));
            var modules = new JsonArray(Entries.Values.OrderBy(e => e.Key, StringComparer.Ordinal)
                .Select(e => (JsonNode)new JsonObject { ["key"] = e.Key, ["source"] = e.Source, ["dir"] = e.Dir.Replace('\\', '/') }).ToArray());
            File.WriteAllText(FilePath(rootDir), new JsonObject { ["modules"] = modules }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
