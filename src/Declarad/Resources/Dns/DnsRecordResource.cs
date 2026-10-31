using Declarad.Directory;
using Declarad.Engine;
using Declarad.Hcl.Values;
using Declarad.Resources.Schema;

namespace Declarad.Resources.Dns
{
    /// <summary>
    /// The records of one type at one name of a zone (a record set, like Terraform's dns_a_record_set). The dnsNode
    /// object is shared by all types at that name: it is created when needed and removed when its last record goes.
    /// </summary>
    public sealed class DnsRecordResource : AttachmentType
    {
        public override string Name => "dns_record";
        public override string ObjectClass => "dnsNode";
        public override string Description => "DNS record set (one name and type) in an AD-integrated zone";
        public override string TargetAttribute => "zone";
        public override IReadOnlyList<string> IdentityAttributes => new[] { "zone", "name", "type", "replication_scope" };
        public override IReadOnlyCollection<string> TargetLdapAttributes => new[] { "dnsRecord", "dNSTombstoned" };
        public override bool CreatesTarget => true;

        public override IReadOnlyList<AttributeSpec> Schema { get; } = new[]
        {
            new AttributeSpec("zone", AttrType.String)
            {
                Description = "Zone name, e.g. dns_zone.corp.name.",
                Required = true,
                Deferred = true,
                Normalize = v => Value.From(DnsCodec.NormalizeZone(V.Str(v)!)),
                Validate = v => DnsCodec.ValidateName(V.Str(v)!, false),
            },
            new AttributeSpec("name", AttrType.String)
            {
                Description = "Name relative to the zone (www, _ldap._tcp), or @ for the zone apex.",
                Required = true,
                Deferred = true,
                Normalize = v => Value.From(V.Str(v)!.Trim().ToLowerInvariant()),
                Validate = v => V.Str(v)!.EndsWith('.') ? "must be relative to the zone (no trailing dot)." : DnsCodec.ValidateName(V.Str(v)!, true),
            },
            new AttributeSpec("type", AttrType.String)
            {
                Description = "A, AAAA, CNAME, MX, TXT, SRV, PTR or NS.",
                Required = true,
                Deferred = true,
                Normalize = v => Value.From(V.Str(v)!.ToUpperInvariant()),
                AllowedValues = new[] { "A", "AAAA", "CNAME", "MX", "TXT", "SRV", "PTR", "NS" },
            },
            new AttributeSpec("records", AttrType.StringList)
            {
                Description = "Record data. Host names without a trailing dot are relative to the zone. Authoritative for this name and type.",
                Required = true,
                Deferred = true,
            },
            new AttributeSpec("ttl", AttrType.Number)
            {
                Description = "TTL in seconds.",
                Default = Value.From(3600),
                Deferred = true,
                Validate = v => V.Long(v) is < 0 or > uint.MaxValue ? "must be between 0 and 4294967295." : null,
            },
            new AttributeSpec("replication_scope", AttrType.String)
            {
                Description = "Where the zone is stored: domain, forest or legacy.",
                Default = Value.From("domain"),
                Deferred = true,
                AllowedValues = new[] { "domain", "forest", "legacy" },
            },
        };

        public override void Normalize(Config.ResourceInstance instance)
        {
            string zone = V.Str(instance.Get("zone")) ?? "";
            string type = V.Str(instance.Get("type")) ?? "";
            if (instance.Get("records") is ListValue records && zone.Length > 0 && DnsCodec.Types.ContainsKey(type))
            {
                instance.Attributes["records"] = Value.FromStrings(records.Items.Select(r => DnsCodec.Canonicalize(type, V.Str(r)!, zone)).Distinct().Order(StringComparer.Ordinal));
            }
        }

        public override IEnumerable<string> Validate(Func<string, Value> get)
        {
            if (V.Str(get("type")) == "CNAME" && V.List(get("records")).Count > 1)
            {
                yield return "a CNAME record set can hold only one record.";
            }
            if (V.List(get("records")).Count == 0)
            {
                yield return "records must not be empty; remove the resource to delete the record set.";
            }
        }

        public override string ComputeDn(Func<string, Value> get, SchemaContext context) =>
            Dn.Combine("DC", V.Str(get("name"))!, Dn.Combine("DC", V.Str(get("zone"))!, DnsCodec.ZoneContainer(V.Str(get("replication_scope")) ?? "domain", context)));

        public override Dictionary<string, Value> ReadAttachment(DirectoryObject target, Func<string, Value> identity, IDirectory directory)
        {
            ushort type = DnsCodec.Types[V.Str(identity("type"))!];
            var records = (target.Binary.GetValueOrDefault("dnsRecord") ?? Array.Empty<byte[]>())
                .Select(DnsCodec.Decode).Where(r => r.Type == type).ToList();
            bool tombstoned = string.Equals(target.Get("dNSTombstoned"), "TRUE", StringComparison.OrdinalIgnoreCase);
            return new Dictionary<string, Value>(StringComparer.Ordinal)
            {
                ["zone"] = identity("zone"),
                ["name"] = identity("name"),
                ["type"] = identity("type"),
                ["replication_scope"] = identity("replication_scope"),
                ["records"] = tombstoned ? Value.FromStrings(Array.Empty<string>()) : Value.FromStrings(records.Select(r => DnsCodec.DecodeData(r.Type, r.Data)).Order(StringComparer.Ordinal)),
                // Records with differing TTLs read as "no single TTL", so the plan rewrites them all.
                ["ttl"] = records.Count > 0 && !tombstoned && records.All(r => r.Ttl == records[0].Ttl) ? Value.From((long)records[0].Ttl) : Value.Null,
            };
        }

        private static IEnumerable<DnsCodec.Record> Build(Config.ResourceInstance desired, uint serial)
        {
            string type = V.Str(desired.Get("type"))!;
            uint ttl = (uint)V.Long(desired.Get("ttl"))!.Value;
            return V.List(desired.Get("records")).Select(r => new DnsCodec.Record(DnsCodec.Types[type], ttl, serial, DnsCodec.EncodeData(type, r)));
        }

        public override DirectoryObject CreateTarget(ApplyContext ctx, ResourceChange change)
        {
            var desired = change.Desired!;
            string zoneDn = Dn.Parent(desired.Dn);
            if (ctx.Directory.GetByDn(zoneDn, Array.Empty<string>()) == null)
            {
                throw new DirectoryException($"DNS zone '{V.Str(desired.Get("zone"))}' does not exist at {zoneDn}.");
            }
            uint serial = DnsCodec.ReadApexSoa(ctx.Directory, zoneDn)?.Soa.Serial ?? 1;
            ctx.Directory.Add(desired.Dn, new Dictionary<string, object[]>
            {
                ["objectClass"] = new object[] { "dnsNode" },
                ["dnsRecord"] = Build(desired, serial).Select(DnsCodec.Encode).Cast<object>().ToArray(),
            });
            DnsCodec.BumpSerial(ctx.Directory, zoneDn);
            return ctx.Directory.GetByDn(desired.Dn, TargetLdapAttributes)!;
        }

        public override void Attach(ApplyContext ctx, ResourceChange change, DirectoryObject target)
        {
            var desired = change.Desired!;
            ushort type = DnsCodec.Types[V.Str(desired.Get("type"))!];
            bool tombstoned = string.Equals(target.Get("dNSTombstoned"), "TRUE", StringComparison.OrdinalIgnoreCase);
            var others = tombstoned ? new List<DnsCodec.Record>() : (target.Binary.GetValueOrDefault("dnsRecord") ?? Array.Empty<byte[]>()).Select(DnsCodec.Decode).Where(r => r.Type != type).ToList();
            string zoneDn = Dn.Parent(target.Dn);
            uint serial = DnsCodec.ReadApexSoa(ctx.Directory, zoneDn)?.Soa.Serial ?? 1;
            var values = others.Concat(Build(desired, serial)).Select(DnsCodec.Encode).Cast<object>().ToArray();
            var mods = new List<Modification> { new("dnsRecord", ModOp.Replace, values) };
            if (tombstoned)
            {
                mods.Add(Modification.Replace("dNSTombstoned", "FALSE"));
            }
            ctx.Directory.Modify(target.Dn, mods);
            DnsCodec.BumpSerial(ctx.Directory, zoneDn);
        }

        public override void Detach(ApplyContext ctx, ResourceChange change, DirectoryObject target)
        {
            string? typeName = V.Str(change.Prior?.Get("type") ?? Value.Null);
            if (typeName == null || !DnsCodec.Types.TryGetValue(typeName, out ushort type))
            {
                return;
            }
            var remaining = (target.Binary.GetValueOrDefault("dnsRecord") ?? Array.Empty<byte[]>()).Select(DnsCodec.Decode).Where(r => r.Type != type).ToList();
            if (remaining.Count == 0)
            {
                ctx.Directory.Delete(target.Dn);
            }
            else
            {
                ctx.Directory.Modify(target.Dn, new[] { new Modification("dnsRecord", ModOp.Replace, remaining.Select(DnsCodec.Encode).Cast<object>().ToArray()) });
            }
            DnsCodec.BumpSerial(ctx.Directory, Dn.Parent(target.Dn));
        }
    }
}
