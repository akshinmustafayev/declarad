namespace Declarad.State.Backends
{
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
}
