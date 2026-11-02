using Declarad.Directory;
using Declarad.Resources.Dns;

namespace Declarad.Tests.Support
{
    /// <summary>
    /// A small Active Directory emulation for engine tests. It enforces the AD rules the engine relies on:
    /// parents must exist, non-leaf objects cannot be deleted, protected objects cannot be moved or deleted,
    /// user accounts cannot be enabled without a password, renames cascade to children and member values,
    /// memberOf is computed from group membership, and objectClass/objectCategory follow the AD schema.
    /// </summary>
    public sealed class InMemoryDirectory : IDirectory
    {
        public const string DefaultDomainPolicy = "{31B2F340-016D-11D2-945F-00C04FB984F9}";
        public const string BaselinePolicy = "{6AC1786C-016F-11D2-945F-00C04FB984F9}";
        private const string DomainSid = "S-1-5-21-1000-2000-3000";

        private static readonly Dictionary<string, (string[] Classes, string Category)> Schema = new(StringComparer.OrdinalIgnoreCase)
        {
            ["user"] = (new[] { "top", "person", "organizationalPerson", "user" }, "person"),
            ["computer"] = (new[] { "top", "person", "organizationalPerson", "user", "computer" }, "computer"),
            ["msDS-GroupManagedServiceAccount"] = (new[] { "top", "person", "organizationalPerson", "user", "computer", "msDS-GroupManagedServiceAccount" }, "msDS-GroupManagedServiceAccount"),
            ["contact"] = (new[] { "top", "person", "organizationalPerson", "contact" }, "person"),
            ["group"] = (new[] { "top", "group" }, "group"),
        };

        private sealed class Entry
        {
            public required string Dn { get; set; }
            public Guid Guid { get; } = Guid.NewGuid();
            public string? Sid { get; set; }
            public Dictionary<string, List<string>> Attrs { get; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, byte[][]> Binary { get; } = new(StringComparer.OrdinalIgnoreCase);
            public byte[] Sd { get; set; } = DefaultSd();
            public bool HasPassword { get; set; }
            public string StructuralClass => Attrs["objectClass"][^1];
        }

        private readonly List<Entry> _entries = new();
        private int _nextRid = 1100;
        public int WriteCount { get; private set; }

        public string BaseDn { get; } = "DC=corp,DC=local";
        public string DomainName { get; } = "corp.local";

        public InMemoryDirectory()
        {
            AddRaw(BaseDn, "domainDNS").Sid = DomainSid;
            foreach (string container in new[] { "CN=Users", "CN=Computers", "CN=Managed Service Accounts", "CN=System", "CN=Password Settings Container,CN=System", "CN=Policies,CN=System" })
            {
                AddRaw($"{container},{BaseDn}", "container").Attrs["isCriticalSystemObject"] = new() { "TRUE" };
            }
            var admins = AddRaw("CN=Domain Admins,CN=Users," + BaseDn, "group");
            admins.Attrs["sAMAccountName"] = new() { "Domain Admins" };
            admins.Attrs["isCriticalSystemObject"] = new() { "TRUE" };
            admins.Attrs["groupType"] = new() { "-2147483646" };
            admins.Sid = DomainSid + "-512";
            // DNS application partitions and the legacy DNS container.
            AddRaw($"DC=DomainDnsZones,{BaseDn}", "domainDNS");
            AddRaw($"CN=MicrosoftDNS,DC=DomainDnsZones,{BaseDn}", "container");
            AddRaw($"DC=ForestDnsZones,{BaseDn}", "domainDNS");
            AddRaw($"CN=MicrosoftDNS,DC=ForestDnsZones,{BaseDn}", "container");
            AddRaw($"CN=MicrosoftDNS,CN=System,{BaseDn}", "container");
            // An existing zone that declarad does not manage.
            var zone = AddRaw($"DC=corp.local,CN=MicrosoftDNS,DC=DomainDnsZones,{BaseDn}", "dnsZone");
            zone.Attrs["name"] = new() { "corp.local" };
            var apex = AddRaw($"DC=@,DC=corp.local,CN=MicrosoftDNS,DC=DomainDnsZones,{BaseDn}", "dnsNode");
            apex.Binary["dnsRecord"] = new[]
            {
                DnsCodec.Encode(new(6, 3600, 7, DnsCodec.EncodeSoa(new(7, 900, 600, 86400, 3600, "dc01.corp.local.", "hostmaster.corp.local.")))),
                DnsCodec.Encode(new(2, 3600, 7, DnsCodec.EncodeData("NS", "dc01.corp.local."))),
            };

            foreach (var (id, name) in new[] { (DefaultDomainPolicy, "Default Domain Policy"), (BaselinePolicy, "Security Baseline") })
            {
                var gpo = AddRaw($"CN={id},CN=Policies,CN=System,{BaseDn}", "groupPolicyContainer");
                gpo.Attrs["displayName"] = new() { name };
                gpo.Attrs["cn"] = new() { id };
            }
        }

        private Entry AddRaw(string dn, string objectClass)
        {
            var e = new Entry { Dn = dn };
            var (classes, category) = Schema.TryGetValue(objectClass, out var s) ? s : (new[] { "top", objectClass }, objectClass);
            e.Attrs["objectClass"] = classes.ToList();
            e.Attrs["objectCategory"] = new() { category };
            _entries.Add(e);
            return e;
        }

        /// <summary>Creates an object outside declarad (to simulate pre-existing or drifted objects).</summary>
        public void Seed(string dn, string objectClass, params (string Attr, string Value)[] attrs)
        {
            var values = new Dictionary<string, object[]>(StringComparer.OrdinalIgnoreCase) { ["objectClass"] = new object[] { objectClass } };
            foreach (var (attr, value) in attrs)
            {
                values[attr] = values.TryGetValue(attr, out var existing) ? existing.Append(value).ToArray() : new object[] { value };
            }
            Add(dn, values);
            Find(dn)!.HasPassword = true;
        }

        public void SetAttribute(string dn, string attr, string? value)
        {
            var e = Find(dn) ?? throw new InvalidOperationException(dn);
            if (value == null)
            {
                e.Attrs.Remove(attr);
            }
            else
            {
                e.Attrs[attr] = new() { value };
            }
        }

        /// <summary>Deletes an object outside declarad without the Recycle Bin: it is gone for good.</summary>
        public void DeleteOutOfBand(string dn) => _entries.Remove(Find(dn)!);

        /// <summary>Whether deleted objects go to the AD Recycle Bin (restorable with their attributes and memberships).</summary>
        public bool RecycleBinEnabled { get; set; } = true;

        private readonly List<(Entry Entry, List<string> Groups)> _recycleBin = new();

        /// <summary>Deletes an object outside declarad, as ADUC does: it goes to the Recycle Bin.</summary>
        public void DeleteToRecycleBin(string dn)
        {
            var e = Find(dn)!;
            var groups = _entries.Where(g => g.Attrs.TryGetValue("member", out var m) && m.Any(x => Dn.AreEqual(x, dn))).Select(g => g.Dn).ToList();
            foreach (var g in _entries)
            {
                if (g.Attrs.TryGetValue("member", out var members))
                {
                    members.RemoveAll(m => Dn.AreEqual(m, dn));
                }
            }
            _entries.Remove(e);
            e.Attrs["lastKnownParent"] = new() { Dn.Parent(dn) };
            e.Attrs["isDeleted"] = new() { "TRUE" };
            e.Dn = $"CN={Dn.First(dn).Value} DEL:{e.Guid},CN=Deleted Objects,{BaseDn}";
            _recycleBin.Add((e, groups));
        }

        public DirectoryObject? GetDeletedByGuid(Guid guid, IReadOnlyCollection<string> attributes)
        {
            var deleted = _recycleBin.FirstOrDefault(d => d.Entry.Guid == guid).Entry;
            return deleted == null || !RecycleBinEnabled ? null : ToObject(deleted, false);
        }

        public void Restore(string deletedDn, string newDn)
        {
            WriteCount++;
            var (e, groups) = _recycleBin.FirstOrDefault(d => Dn.AreEqual(d.Entry.Dn, deletedDn));
            if (e == null)
            {
                throw Fail(LdapResult.NoSuchObject, $"{deletedDn} is not in the Recycle Bin");
            }
            if (Find(Dn.Parent(newDn)) == null && !Dn.AreEqual(Dn.Parent(newDn), BaseDn))
            {
                throw Fail(LdapResult.NoSuchObject, $"parent of {newDn} does not exist");
            }
            if (Find(newDn) != null)
            {
                throw Fail(LdapResult.EntryAlreadyExists, $"{newDn} already exists");
            }
            _recycleBin.RemoveAll(d => d.Entry == e);
            e.Attrs.Remove("isDeleted");
            e.Attrs.Remove("lastKnownParent");
            e.Dn = newDn;
            _entries.Add(e);
            foreach (var g in groups.Select(Find).OfType<Entry>())
            {
                if (!g.Attrs.TryGetValue("member", out var members))
                {
                    g.Attrs["member"] = members = new();
                }
                members.Add(newDn);
            }
        }

        public bool Exists(string dn) => Find(dn) != null;

        public string? Attr(string dn, string attr) => Get(dn)?.Get(attr);

        public DirectoryObject? Get(string dn) => GetByDn(dn, Array.Empty<string>(), true);

        private Entry? Find(string dn)
        {
            if (dn.StartsWith("<SID=", StringComparison.OrdinalIgnoreCase))
            {
                string sid = dn[5..^1];
                return _entries.FirstOrDefault(e => e.Sid == sid);
            }
            return _entries.FirstOrDefault(e => Dn.AreEqual(e.Dn, dn));
        }

        private static DirectoryException Fail(int code, string message) => new(message, code);

        public DirectoryObject? GetByGuid(Guid guid, IReadOnlyCollection<string> attributes, bool withSecurityDescriptor = false)
        {
            var e = _entries.FirstOrDefault(x => x.Guid == guid);
            return e == null ? null : ToObject(e, withSecurityDescriptor);
        }

        public DirectoryObject? GetByDn(string dn, IReadOnlyCollection<string> attributes, bool withSecurityDescriptor = false)
        {
            var e = Find(dn);
            return e == null ? null : ToObject(e, withSecurityDescriptor);
        }

        private DirectoryObject ToObject(Entry e, bool withSd)
        {
            var attrs = e.Attrs.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
            var memberOf = _entries.Where(g => g.Attrs.TryGetValue("member", out var m) && m.Any(x => Dn.AreEqual(x, e.Dn))).Select(g => g.Dn).ToArray();
            if (memberOf.Length > 0)
            {
                attrs["memberOf"] = memberOf;
            }
            return new DirectoryObject(e.Dn, e.Guid, attrs, withSd ? e.Sd.ToArray() : null, e.Sid, e.Binary);
        }

        public IReadOnlyList<DirectoryObject> Search(string baseDn, string filter, bool subtree, IReadOnlyCollection<string> attributes)
        {
            var f = FilterParser.Parse(filter);
            return _entries
                .Where(e => Dn.AreEqual(e.Dn, baseDn) || (subtree ? Dn.IsDescendantOf(e.Dn, baseDn) : Dn.AreEqual(Dn.Parent(e.Dn), baseDn)))
                .Where(e => f(e.Attrs))
                .Select(e => ToObject(e, false))
                .ToList();
        }

        public void Add(string dn, IReadOnlyDictionary<string, object[]> attributes)
        {
            WriteCount++;
            if (Find(dn) != null)
            {
                throw Fail(LdapResult.EntryAlreadyExists, $"{dn} already exists");
            }
            if (Find(Dn.Parent(dn)) == null)
            {
                throw Fail(LdapResult.NoSuchObject, $"parent of {dn} does not exist");
            }
            var e = AddRaw(dn, attributes["objectClass"][0].ToString()!);
            _entries.Remove(e);
            foreach (var (k, v) in attributes.Where(kv => !kv.Key.Equals("objectClass", StringComparison.OrdinalIgnoreCase)))
            {
                if (v.All(x => x is byte[]))
                {
                    e.Binary[k] = v.Cast<byte[]>().ToArray();
                }
                else
                {
                    e.Attrs[k] = v.Select(x => x.ToString()!).ToList();
                }
            }
            if (e.StructuralClass is "user" or "group" or "computer" or "msDS-GroupManagedServiceAccount")
            {
                e.Sid = $"{DomainSid}-{_nextRid++}";
            }
            // AD keeps sAMAccountName unique in the domain and userPrincipalName unique in the forest.
            foreach (string unique in new[] { "sAMAccountName", "userPrincipalName" })
            {
                if (e.Attrs.TryGetValue(unique, out var v) && _entries.Any(x => x.Attrs.TryGetValue(unique, out var o) && string.Equals(o[0], v[0], StringComparison.OrdinalIgnoreCase)))
                {
                    throw Fail(LdapResult.ConstraintViolation, $"{unique} '{v[0]}' is already in use");
                }
            }
            SetRdnAttributes(e);
            if (e.StructuralClass == "user")
            {
                if (!e.Attrs.ContainsKey("userAccountControl"))
                {
                    e.Attrs["userAccountControl"] = new() { "546" };
                }
                if ((long.Parse(e.Attrs["userAccountControl"][0]) & 2) == 0)
                {
                    throw Fail(LdapResult.UnwillingToPerform, "cannot create an enabled account without a password");
                }
            }
            if (e.StructuralClass == "msDS-PasswordSettings")
            {
                foreach (string required in new[] { "msDS-PasswordSettingsPrecedence", "msDS-MinimumPasswordLength", "msDS-MaximumPasswordAge", "msDS-LockoutDuration" })
                {
                    if (!e.Attrs.ContainsKey(required))
                    {
                        throw Fail(LdapResult.ConstraintViolation, $"{required} is mandatory");
                    }
                }
            }
            _entries.Add(e);
        }

        public void Modify(string dn, IReadOnlyList<Modification> modifications)
        {
            WriteCount++;
            var e = Find(dn) ?? throw Fail(LdapResult.NoSuchObject, $"{dn} not found");
            foreach (var m in modifications)
            {
                if (m.Attribute.Equals("unicodePwd", StringComparison.OrdinalIgnoreCase))
                {
                    e.HasPassword = true;
                    continue;
                }
                if (m.Values.Count > 0 && m.Values.All(v => v is byte[]))
                {
                    e.Binary[m.Attribute] = m.Values.Cast<byte[]>().ToArray();
                    continue;
                }
                var values = m.Values.Select(v => v.ToString()!).ToList();
                bool isDn = m.Attribute is "member" or "msDS-PSOAppliesTo";
                bool Same(string a, string b) => isDn ? Dn.AreEqual(a, b) : a == b;
                switch (m.Op)
                {
                    case ModOp.Replace:
                        if (m.Attribute.Equals("userAccountControl", StringComparison.OrdinalIgnoreCase) && e.StructuralClass == "user"
                            && (long.Parse(values[0]) & 2) == 0 && !e.HasPassword)
                        {
                            throw Fail(LdapResult.UnwillingToPerform, "cannot enable an account without a password");
                        }
                        if (m.Attribute.Equals("groupType", StringComparison.OrdinalIgnoreCase) && e.Attrs.TryGetValue("groupType", out var oldType))
                        {
                            // AD refuses global <-> domain local directly; conversions go through universal.
                            long from = long.Parse(oldType[0]) & 0xE, to = long.Parse(values[0]) & 0xE;
                            if ((from == 2 && to == 4) || (from == 4 && to == 2))
                            {
                                throw Fail(LdapResult.UnwillingToPerform, "groupType cannot change directly between global and domain local");
                            }
                        }
                        if (values.Count > 0 && m.Attribute is "sAMAccountName" or "userPrincipalName"
                            && _entries.Any(x => x != e && x.Attrs.TryGetValue(m.Attribute, out var o) && string.Equals(o[0], values[0], StringComparison.OrdinalIgnoreCase)))
                        {
                            throw Fail(LdapResult.ConstraintViolation, $"{m.Attribute} '{values[0]}' is already in use");
                        }
                        if (values.Count == 0)
                        {
                            e.Attrs.Remove(m.Attribute);
                        }
                        else
                        {
                            e.Attrs[m.Attribute] = values;
                        }
                        break;
                    case ModOp.Add:
                        var current = e.Attrs.TryGetValue(m.Attribute, out var c) ? c : e.Attrs[m.Attribute] = new();
                        foreach (var v in values)
                        {
                            if (current.Any(x => Same(x, v)))
                            {
                                throw Fail(LdapResult.EntryAlreadyExists, $"{v} already in {m.Attribute}");
                            }
                            if (isDn && Find(v) == null)
                            {
                                throw Fail(LdapResult.NoSuchObject, $"member {v} does not exist");
                            }
                            current.Add(v);
                        }
                        break;
                    case ModOp.Delete:
                        if (values.Count == 0)
                        {
                            e.Attrs.Remove(m.Attribute);
                            e.Binary.Remove(m.Attribute);
                            break;
                        }
                        var list = e.Attrs.TryGetValue(m.Attribute, out var l) ? l : new List<string>();
                        foreach (var v in values)
                        {
                            if (list.RemoveAll(x => Same(x, v)) == 0)
                            {
                                throw Fail(LdapResult.UnwillingToPerform, $"{v} not in {m.Attribute}");
                            }
                        }
                        if (list.Count == 0)
                        {
                            e.Attrs.Remove(m.Attribute);
                        }
                        break;
                }
            }
        }

        public void SetSecurityDescriptor(string dn, byte[] securityDescriptor)
        {
            WriteCount++;
            var e = Find(dn) ?? throw Fail(LdapResult.NoSuchObject, $"{dn} not found");
            e.Sd = securityDescriptor.ToArray();
        }

        public void Rename(string dn, string newRdn, string newParentDn)
        {
            WriteCount++;
            var e = Find(dn) ?? throw Fail(LdapResult.NoSuchObject, $"{dn} not found");
            if (Find(newParentDn) == null)
            {
                throw Fail(LdapResult.NoSuchObject, $"{newParentDn} not found");
            }
            if (!Dn.AreEqual(Dn.Parent(dn), newParentDn) && SecurityDescriptor.IsProtectedFromDeletion(e.Sd))
            {
                throw Fail(LdapResult.InsufficientAccessRights, $"{dn} is protected and cannot be moved");
            }
            string newDn = $"{newRdn},{newParentDn}";
            if (Find(newDn) is { } clash && clash != e)
            {
                throw Fail(LdapResult.EntryAlreadyExists, $"{newDn} exists");
            }
            foreach (var x in _entries)
            {
                if (x == e || Dn.IsDescendantOf(x.Dn, dn))
                {
                    string updated = x == e ? newDn : x.Dn[..(x.Dn.Length - dn.Length)] + newDn;
                    RewriteMemberValues(x.Dn, updated);
                    x.Dn = updated;
                    SetRdnAttributes(x);
                }
            }
        }

        /// <summary>AD maintains the naming attribute (cn/ou/dc) and name from the RDN.</summary>
        private static void SetRdnAttributes(Entry e)
        {
            var rdn = Dn.First(e.Dn);
            e.Attrs[rdn.Type.ToLowerInvariant()] = new() { rdn.Value };
            e.Attrs["name"] = new() { rdn.Value };
        }

        private void RewriteMemberValues(string oldDn, string newDn)
        {
            foreach (var g in _entries)
            {
                if (g.Attrs.TryGetValue("member", out var members))
                {
                    for (int i = 0; i < members.Count; i++)
                    {
                        if (Dn.AreEqual(members[i], oldDn))
                        {
                            members[i] = newDn;
                        }
                    }
                }
            }
        }

        public void Delete(string dn)
        {
            WriteCount++;
            var e = Find(dn) ?? throw Fail(LdapResult.NoSuchObject, $"{dn} not found");
            if (_entries.Any(x => Dn.IsDescendantOf(x.Dn, dn)))
            {
                throw Fail(LdapResult.NotAllowedOnNonLeaf, $"{dn} has children");
            }
            if (SecurityDescriptor.IsProtectedFromDeletion(e.Sd))
            {
                throw Fail(LdapResult.InsufficientAccessRights, $"{dn} is protected");
            }
            _entries.Remove(e);
            foreach (var g in _entries)
            {
                if (g.Attrs.TryGetValue("member", out var members))
                {
                    members.RemoveAll(m => Dn.AreEqual(m, dn));
                }
            }
        }

        public void Dispose()
        {
        }

        /// <summary>Self-relative SD with owner, group and a DACL holding one "allow Authenticated Users read" ACE.</summary>
        public static byte[] DefaultSd()
        {
            byte[] owner = { 1, 1, 0, 0, 0, 0, 0, 5, 18, 0, 0, 0 };                       // S-1-5-18
            byte[] ace = { 0x00, 0x02, 20, 0, 0x94, 0, 2, 0, 1, 1, 0, 0, 0, 0, 0, 5, 11, 0, 0, 0 }; // allow, inherit, S-1-5-11
            var acl = new List<byte> { 4, 0, (byte)(8 + ace.Length), 0, 1, 0, 0, 0 };
            acl.AddRange(ace);
            var sd = new List<byte> { 1, 0, 0x04, 0x94 };                                  // DACL present | auto-inherited | self-relative
            int daclOff = 20, ownerOff = daclOff + acl.Count, groupOff = ownerOff + owner.Length;
            sd.AddRange(BitConverter.GetBytes(ownerOff));
            sd.AddRange(BitConverter.GetBytes(groupOff));
            sd.AddRange(BitConverter.GetBytes(0));
            sd.AddRange(BitConverter.GetBytes(daclOff));
            sd.AddRange(acl);
            sd.AddRange(owner);
            sd.AddRange(owner);
            return sd.ToArray();
        }
    }
}
