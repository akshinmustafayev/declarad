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
}
