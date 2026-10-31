namespace Declarad.Directory
{
    /// <summary>Well-known LDAP result codes used for error handling.</summary>
    public static class LdapResult
    {
        public const int NoSuchObject = 32;
        public const int InvalidCredentials = 49;
        public const int InsufficientAccessRights = 50;
        public const int UnwillingToPerform = 53;
        public const int NotAllowedOnNonLeaf = 66;
        public const int EntryAlreadyExists = 68;
        public const int AttributeOrValueExists = 20;
        public const int NoSuchAttribute = 16;
        public const int ConstraintViolation = 19;
        public const int ServerDown = 81;
    }
}
