using System.Text.Json;
using System.Text.Json.Nodes;

namespace Declarad.State.Backends
{
    /// <summary>Who holds a state lock. Serialized in Terraform's lock-info format so tooling can read it.</summary>
    public sealed record LockInfo
    {
        public string Id { get; init; } = Guid.NewGuid().ToString();
        public string Operation { get; init; } = "";
        public string Info { get; init; } = "";
        public string Who { get; init; } = $"{Environment.UserName}@{Environment.MachineName}";
        public string Version { get; init; } = typeof(LockInfo).Assembly.GetName().Version?.ToString(3) ?? "";
        public DateTime Created { get; init; } = DateTime.UtcNow;
        public string Path { get; init; } = "";

        public string ToJson() => new JsonObject
        {
            ["ID"] = Id,
            ["Operation"] = Operation,
            ["Info"] = Info,
            ["Who"] = Who,
            ["Version"] = Version,
            ["Created"] = Created.ToString("O"),
            ["Path"] = Path,
        }.ToJsonString();

        public static LockInfo? Parse(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }
            try
            {
                var node = JsonNode.Parse(json);
                return new LockInfo
                {
                    Id = node?["ID"]?.GetValue<string>() ?? "",
                    Operation = node?["Operation"]?.GetValue<string>() ?? "",
                    Info = node?["Info"]?.GetValue<string>() ?? "",
                    Who = node?["Who"]?.GetValue<string>() ?? "",
                    Version = node?["Version"]?.GetValue<string>() ?? "",
                    Created = DateTime.TryParse(node?["Created"]?.GetValue<string>(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var c) ? c : DateTime.MinValue,
                    Path = node?["Path"]?.GetValue<string>() ?? "",
                };
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
            {
                return null;
            }
        }

        public string Describe() =>
            $"  ID:        {Id}\n  Path:      {Path}\n  Operation: {Operation}\n  Who:       {Who}\n  Version:   {Version}\n  Created:   {Created:u}";
    }
}
