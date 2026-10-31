namespace Declarad.Directory
{
    /// <summary>An object read from the directory. Attribute names are case-insensitive.</summary>
    public sealed class DirectoryObject
    {
        public string Dn { get; }
        public Guid Guid { get; }
        public IReadOnlyDictionary<string, string[]> Attributes { get; }
        public byte[]? SecurityDescriptor { get; }

        /// <summary>objectSid in S-1-5-... form, when the object has one.</summary>
        public string? Sid { get; }

        /// <summary>Binary attributes other than objectGUID/objectSid/nTSecurityDescriptor (e.g. msDS-GroupMSAMembership).</summary>
        public IReadOnlyDictionary<string, byte[][]> Binary { get; }

        public DirectoryObject(string dn, Guid guid, IDictionary<string, string[]> attributes, byte[]? securityDescriptor = null,
            string? sid = null, IDictionary<string, byte[][]>? binary = null)
        {
            Dn = dn;
            Guid = guid;
            Attributes = new Dictionary<string, string[]>(attributes, StringComparer.OrdinalIgnoreCase);
            SecurityDescriptor = securityDescriptor;
            Sid = sid;
            Binary = new Dictionary<string, byte[][]>(binary ?? new Dictionary<string, byte[][]>(), StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>The most specific object class (AD lists objectClass from top to the structural class).</summary>
        public string? StructuralClass => GetAll("objectClass").LastOrDefault();

        public bool HasStructuralClass(string objectClass) => string.Equals(StructuralClass, objectClass, StringComparison.OrdinalIgnoreCase);

        public string? Get(string attribute) => Attributes.TryGetValue(attribute, out var v) && v.Length > 0 ? v[0] : null;

        public string[] GetAll(string attribute) => Attributes.TryGetValue(attribute, out var v) ? v : Array.Empty<string>();

        public long? GetLong(string attribute) => long.TryParse(Get(attribute), out long n) ? n : null;

        public bool HasClass(string objectClass) => GetAll("objectClass").Contains(objectClass, StringComparer.OrdinalIgnoreCase);
    }

    public enum ModOp
    {
        Replace,
        Add,
        Delete,
    }

    /// <summary>One attribute modification. Values are <see cref="string"/> or <see cref="byte"/>[]; Delete with no values clears the attribute.</summary>
    public sealed record Modification(string Attribute, ModOp Op, IReadOnlyList<object> Values)
    {
        public static Modification Replace(string attribute, params object[] values) => new(attribute, ModOp.Replace, values);
        public static Modification Clear(string attribute) => new(attribute, ModOp.Delete, Array.Empty<object>());
    }

    public sealed class DirectoryException : DeclaradException
    {
        public int ResultCode { get; }

        public DirectoryException(string message, int resultCode = 0, Exception? inner = null)
            : base(message, default, inner)
        {
            ResultCode = resultCode;
        }
    }

    /// <summary>
    /// The operations declarad needs from Active Directory. Implemented over LDAP by <see cref="LdapDirectory"/>;
    /// tests use an in-memory implementation.
    /// </summary>
    public interface IDirectory : IDisposable
    {
        /// <summary>The domain naming context, e.g. <c>DC=corp,DC=local</c>.</summary>
        string BaseDn { get; }

        /// <summary>The DNS name of the domain, e.g. <c>corp.local</c>.</summary>
        string DomainName { get; }

        DirectoryObject? GetByGuid(Guid guid, IReadOnlyCollection<string> attributes, bool withSecurityDescriptor = false);

        /// <summary>Looks up an object by DN. AD also accepts <c>&lt;GUID=...&gt;</c> and <c>&lt;SID=...&gt;</c> forms.</summary>
        DirectoryObject? GetByDn(string dn, IReadOnlyCollection<string> attributes, bool withSecurityDescriptor = false);

        IReadOnlyList<DirectoryObject> Search(string baseDn, string filter, bool subtree, IReadOnlyCollection<string> attributes);

        void Add(string dn, IReadOnlyDictionary<string, object[]> attributes);

        void Modify(string dn, IReadOnlyList<Modification> modifications);

        /// <summary>Writes the DACL part of a security descriptor.</summary>
        void SetSecurityDescriptor(string dn, byte[] securityDescriptor);

        /// <summary>Renames and/or moves an object. <paramref name="newRdn"/> is e.g. <c>CN=John Doe</c>.</summary>
        void Rename(string dn, string newRdn, string newParentDn);

        void Delete(string dn);
    }

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
