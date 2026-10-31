using Declarad.Config;
using Declarad.Directory;

namespace Declarad.Cli
{
    /// <summary>Directory connections by connection name, opened on first use and closed together.</summary>
    internal sealed class DirectoryPool : IDisposable
    {
        private readonly Configuration _config;
        private readonly Func<ConnectionSettings, IDirectory> _connect;
        private readonly Func<string, string?> _prompt;
        private readonly Dictionary<string, IDirectory> _opened = new(StringComparer.Ordinal);

        public DirectoryPool(Configuration config, Func<ConnectionSettings, IDirectory> connect, Func<string, string?> prompt)
        {
            _config = config;
            _connect = connect;
            _prompt = prompt;
            foreach (var (name, directory) in config.OpenDirectories)
            {
                _opened[name] = directory;
            }
        }

        public IDirectory Get(string connection)
        {
            if (!_opened.TryGetValue(connection, out var directory))
            {
                directory = _connect(_config.ResolveConnection(connection, Environment.GetEnvironmentVariable, _prompt));
                _opened[connection] = directory;
            }
            return directory;
        }

        /// <summary>Whether every connection opened so far is encrypted (passwords need LDAPS/StartTLS).</summary>
        public bool Encrypted => _opened.Values.All(d => d is not LdapDirectory ldap || ldap.IsEncrypted);

        public void Dispose()
        {
            foreach (var directory in _opened.Values)
            {
                directory.Dispose();
            }
            _opened.Clear();
        }
    }
}
