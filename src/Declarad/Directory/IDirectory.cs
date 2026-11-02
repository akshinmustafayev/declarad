namespace Declarad.Directory
{
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

        /// <summary>
        /// Finds a deleted object in the AD Recycle Bin by objectGUID. Returns null when it is not there, when it was
        /// already recycled (its attributes are gone), or when the Recycle Bin is not enabled.
        /// </summary>
        DirectoryObject? GetDeletedByGuid(Guid guid, IReadOnlyCollection<string> attributes);

        /// <summary>Restores an object from the AD Recycle Bin to <paramref name="newDn"/>, with its SID, attributes and memberships.</summary>
        void Restore(string deletedDn, string newDn);
    }
}
