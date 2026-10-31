using System.Text;

namespace Declarad.State.Backends
{
    /// <summary>State in a local file (the default). The lock is a sibling file holding the lock info.</summary>
    public sealed class LocalBackend : IStateBackend
    {
        public string Path { get; }
        private string LockPath => Path + ".lock.info";

        public LocalBackend(string path)
        {
            Path = path;
        }

        public string Type => "local";
        public string Location => Path;

        public StateFile? Read() => File.Exists(Path) ? StateStore.Parse(File.ReadAllText(Path), Path) : null;

        public void Write(StateFile state) => StateStore.WriteFile(Path, state);

        public bool Lock(LockInfo info)
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            try
            {
                using var stream = new FileStream(LockPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                stream.Write(Encoding.UTF8.GetBytes(info.ToJson()));
                return true;
            }
            catch (IOException) when (File.Exists(LockPath))
            {
                throw new StateLockedException(Path, ReadLock());
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new DeclaradException($"Cannot create the lock file {LockPath}: {ex.Message}", default, ex);
            }
        }

        private LockInfo? ReadLock()
        {
            try
            {
                return LockInfo.Parse(File.ReadAllText(LockPath));
            }
            catch (IOException)
            {
                return null;
            }
        }

        public void Unlock(string lockId)
        {
            if (ReadLock() is { } holder && holder.Id != lockId)
            {
                throw new DeclaradException($"Cannot unlock {Path}: it is held by lock {holder.Id}, not {lockId}.");
            }
            File.Delete(LockPath);
        }

        public void ForceUnlock(string lockId)
        {
            var holder = ReadLock();
            if (holder == null && !File.Exists(LockPath))
            {
                throw new DeclaradException($"The state at {Path} is not locked.");
            }
            if (holder != null && holder.Id != lockId)
            {
                throw new DeclaradException($"The lock on {Path} has id {holder.Id}, not {lockId}.");
            }
            File.Delete(LockPath);
        }
    }
}
