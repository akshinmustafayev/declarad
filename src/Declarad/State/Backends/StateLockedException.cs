namespace Declarad.State.Backends
{
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
}
