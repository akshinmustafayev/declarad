namespace Declarad.Directory
{
    public sealed record ConnectionSettings
    {
        public required string Server { get; init; }
        public int? Port { get; init; }
        public LdapProtocol Protocol { get; init; } = LdapProtocol.Ldaps;
        public LdapAuth Auth { get; init; } = LdapAuth.Simple;
        public string? Username { get; init; }
        public string? Password { get; init; }
        public string? BaseDn { get; init; }
        public string? Domain { get; init; }
        public bool InsecureSkipVerify { get; init; }
        public string? CaCertDir { get; init; }
        public int TimeoutSeconds { get; init; } = 30;

        public int EffectivePort => Port ?? (Protocol == LdapProtocol.Ldaps ? 636 : 389);
        public bool IsEncrypted => Protocol != LdapProtocol.Ldap;
    }
}
