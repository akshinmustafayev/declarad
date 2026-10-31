using System.Text.Json;
using System.Text.Json.Nodes;

namespace Declarad.State
{
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
