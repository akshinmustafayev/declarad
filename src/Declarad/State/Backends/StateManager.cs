namespace Declarad.State.Backends
{
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
