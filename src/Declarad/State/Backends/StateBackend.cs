using System.Security.Cryptography;
using System.Text;
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

    public sealed class StateLockedException : DeclaradException
    {
        public LockInfo? Holder { get; }

        public StateLockedException(string location, LockInfo? holder)
            : base($"The state at {location} is locked by another operation.\n{holder?.Describe() ?? "  (no lock information)"}\n" +
                   (holder != null ? $"If that operation is no longer running, unlock with: declarad force-unlock {holder.Id}" : ""))
        {
            Holder = holder;
        }
    }

    /// <summary>Where state is stored and how it is locked.</summary>
    public interface IStateBackend
    {
        string Type { get; }

        /// <summary>Human-readable location, e.g. s3://bucket/key.</summary>
        string Location { get; }

        /// <summary>Reads the current state, or null when none has been written yet.</summary>
        StateFile? Read();

        void Write(StateFile state);

        /// <summary>
        /// Acquires the lock or throws <see cref="StateLockedException"/>. Returns false when this backend is
        /// configured without locking (then nothing was locked).
        /// </summary>
        bool Lock(LockInfo info);

        /// <summary>Releases a lock held under <paramref name="lockId"/>.</summary>
        void Unlock(string lockId);

        /// <summary>Removes a lock regardless of the holder, after the user confirmed the id.</summary>
        void ForceUnlock(string lockId);
    }

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

    /// <summary>
    /// Loads and saves state through a backend. Without a lock, a save first checks that nobody else wrote
    /// newer state since it was loaded (lineage and serial), so concurrent runs cannot silently overwrite each other.
    /// </summary>
    public sealed class StateManager
    {
        private long _loadedSerial;
        private string? _loadedLineage;
        private string? _lockId;

        public IStateBackend Backend { get; }

        public StateManager(IStateBackend backend)
        {
            Backend = backend;
        }

        public StateFile Load()
        {
            var state = Backend.Read() ?? new StateFile();
            _loadedSerial = state.Serial;
            _loadedLineage = state.Lineage;
            return state;
        }

        public void Save(StateFile state)
        {
            if (_lockId == null && Backend.Read() is { } remote)
            {
                if (_loadedLineage != null && remote.Lineage != _loadedLineage)
                {
                    throw new DeclaradException($"The state at {Backend.Location} belongs to a different lineage ({remote.Lineage}); refusing to overwrite it.");
                }
                if (remote.Serial > _loadedSerial)
                {
                    throw new DeclaradException($"The state at {Backend.Location} was changed by another run (serial {remote.Serial} > {_loadedSerial}). Run the command again.");
                }
            }
            state.Serial++;
            Backend.Write(state);
            _loadedSerial = state.Serial;
            _loadedLineage = state.Lineage;
        }

        /// <summary>Locks the state, retrying until <paramref name="timeout"/>. Dispose the result to unlock.</summary>
        public IDisposable Lock(string operation, TimeSpan timeout)
        {
            var info = new LockInfo { Operation = operation, Path = Backend.Location };
            var deadline = DateTime.UtcNow + timeout;
            while (true)
            {
                try
                {
                    if (!Backend.Lock(info))
                    {
                        // No locking available: keep the lineage/serial conflict check on saves.
                        return new Releaser(this, null);
                    }
                    _lockId = info.Id;
                    return new Releaser(this, info.Id);
                }
                catch (StateLockedException) when (DateTime.UtcNow < deadline)
                {
                    Thread.Sleep(TimeSpan.FromMilliseconds(Math.Min(1000, Math.Max(50, (deadline - DateTime.UtcNow).TotalMilliseconds))));
                }
            }
        }

        private sealed class Releaser : IDisposable
        {
            private readonly StateManager _owner;
            private string? _id;

            public Releaser(StateManager owner, string? id)
            {
                _owner = owner;
                _id = id;
            }

            public void Dispose()
            {
                if (_id != null)
                {
                    _owner.Backend.Unlock(_id);
                    _owner._lockId = null;
                    _id = null;
                }
            }
        }
    }
}
