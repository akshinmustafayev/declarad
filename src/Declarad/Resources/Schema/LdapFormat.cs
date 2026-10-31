namespace Declarad.Resources.Schema
{
    /// <summary>How an attribute value is encoded in LDAP.</summary>
    public enum LdapFormat
    {
        Text,
        /// <summary>LDAP boolean: TRUE / FALSE.</summary>
        Bool,
        /// <summary>Integer.</summary>
        Int,
        /// <summary>Integer flag 1 / 0 (absent means false).</summary>
        IntBool,
        /// <summary>AD time interval (negative 100 ns ticks), written in configuration as e.g. "42d" or "30m".</summary>
        Duration,
    }
}
