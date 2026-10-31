using Declarad.Directory;
using Declarad.Engine;
using Declarad.Hcl.Values;
using Declarad.Resources.Schema;

namespace Declarad.Resources.Dns
{
    /// <summary>An AD-integrated DNS zone, created as a dnsZone object with an apex (@) node holding SOA and NS records.</summary>
    public sealed class DnsZoneResource : ResourceType
    {
        private static readonly string[] SoaAttributes = { "primary_server", "admin_email", "refresh", "retry", "expire", "minimum_ttl", "ttl", "name_servers" };

        public override string Name => "dns_zone";
        public override string ObjectClass => "dnsZone";
        public override string RdnType => "DC";
        public override string Description => "AD-integrated DNS zone";
        public override string Filter => "(&(objectClass=dnsZone)(!(name=RootDNSServers))(!(name=..TrustAnchors)))";
        protected override IEnumerable<string> ExtraLdapAttributes => new[] { "dNSProperty" };

        public override string DefaultSearchBase(IDirectory directory) => $"CN=MicrosoftDNS,DC=DomainDnsZones,{directory.BaseDn}";

        private static AttributeSpec Seconds(string name, string description, long value) => new(name, AttrType.Number)
        {
            Description = description,
            Default = Value.From(value),
            Validate = v => V.Long(v) is < 0 or > uint.MaxValue ? "must be between 0 and 4294967295." : null,
        };

        public override IReadOnlyList<AttributeSpec> Schema { get; } = new[]
        {
            new AttributeSpec("name", AttrType.String)
            {
                Description = "Zone name, e.g. corp.example or 10.in-addr.arpa. Defaults to the block label.",
                Required = true,
                Immutable = true,
                Normalize = v => Value.From(DnsCodec.NormalizeZone(V.Str(v)!)),
                Validate = v => DnsCodec.ValidateName(V.Str(v)!, false),
            },
            new AttributeSpec("replication_scope", AttrType.String)
            {
                Description = "domain (DomainDnsZones), forest (ForestDnsZones) or legacy (domain partition).",
                Default = Value.From("domain"),
                AllowedValues = new[] { "domain", "forest", "legacy" },
                Immutable = true,
            },
            new AttributeSpec("path", AttrType.String)
            {
                Description = "Container of the zone; derived from replication_scope.",
                IsDn = true,
                Immutable = true,
                DefaultFunc = (get, ctx) => get("replication_scope").IsUnknown ? Value.Unknown : Value.From(DnsCodec.ZoneContainer(V.Str(get("replication_scope")) ?? "domain", ctx)),
                Validate = CommonSpecs.ValidDn,
            },
            new AttributeSpec("dynamic_update", AttrType.String)
            {
                Description = "none, secure or nonsecure_and_secure.",
                Default = Value.From("secure"),
                AllowedValues = new[] { "none", "secure", "nonsecure_and_secure" },
            },
            new AttributeSpec("primary_server", AttrType.String)
            {
                Description = "SOA primary name server. Defaults to the connection's server.",
                Required = true,
                DefaultFunc = (_, ctx) => ctx.Server is { } s ? Value.From(s.TrimEnd('.').ToLowerInvariant() + ".") : Value.Null,
                Normalize = v => Value.From(V.Str(v)!.TrimEnd('.').ToLowerInvariant() + "."),
            },
            new AttributeSpec("admin_email", AttrType.String)
            {
                Description = "SOA responsible person, as an address (hostmaster@corp.example) or DNS name. Defaults to hostmaster.<zone>.",
                DefaultFunc = (get, _) => get("name").IsUnknown ? Value.Unknown : Value.From($"hostmaster.{V.Str(get("name"))}."),
                Normalize = v => Value.From(V.Str(v)!.Replace('@', '.').TrimEnd('.').ToLowerInvariant() + "."),
            },
            Seconds("refresh", "SOA refresh interval in seconds.", 900),
            Seconds("retry", "SOA retry interval in seconds.", 600),
            Seconds("expire", "SOA expire time in seconds.", 86400),
            Seconds("minimum_ttl", "SOA minimum (negative caching) TTL in seconds.", 3600),
            Seconds("ttl", "TTL of the SOA and NS records in seconds.", 3600),
            new AttributeSpec("name_servers", AttrType.StringList)
            {
                Description = "NS records at the zone apex. Defaults to [primary_server].",
                DefaultFunc = (get, _) => get("primary_server") is StringValue s ? Value.FromStrings(new[] { s.V }) : get("primary_server"),
                Normalize = v => Value.FromStrings(V.List(v).Select(n => n.TrimEnd('.').ToLowerInvariant() + ".").Distinct().Order(StringComparer.Ordinal)),
            },
        };

        public override string ComputeDn(Func<string, Value> get, SchemaContext context) =>
            Dn.Combine("DC", V.Str(get("name"))!, V.Str(get("path"))!);

        private static byte AllowUpdate(string mode) => mode switch
        {
            "nonsecure_and_secure" => 1,
            "secure" => 2,
            _ => 0,
        };

        protected override void ReadSpecial(DirectoryObject obj, Dictionary<string, Value> result, IDirectory directory)
        {
            // DNS names are case-insensitive; compare in the normalized (lower-case) form.
            result["name"] = Value.From(DnsCodec.NormalizeZone(V.Str(result["name"])!));
            result["replication_scope"] = Value.From(
                obj.Dn.Contains("DC=ForestDnsZones", StringComparison.OrdinalIgnoreCase) ? "forest"
                : obj.Dn.Contains("DC=DomainDnsZones", StringComparison.OrdinalIgnoreCase) ? "domain"
                : "legacy");
            byte update = 0;
            foreach (var value in obj.Binary.GetValueOrDefault("dNSProperty") ?? Array.Empty<byte[]>())
            {
                if (DnsCodec.DecodeProperty(value) is { Id: DnsCodec.PropertyAllowUpdate, Data.Length: > 0 } p)
                {
                    update = p.Data[0];
                }
            }
            result["dynamic_update"] = Value.From(update switch { 1 => "nonsecure_and_secure", 2 => "secure", _ => "none" });

            if (DnsCodec.ReadApexSoa(directory, obj.Dn) is { } apex)
            {
                result["primary_server"] = Value.From(apex.Soa.PrimaryServer);
                result["admin_email"] = Value.From(apex.Soa.AdminEmail);
                result["refresh"] = Value.From((long)apex.Soa.Refresh);
                result["retry"] = Value.From((long)apex.Soa.Retry);
                result["expire"] = Value.From((long)apex.Soa.Expire);
                result["minimum_ttl"] = Value.From((long)apex.Soa.Minimum);
                result["ttl"] = Value.From((long)apex.Record.Ttl);
                result["name_servers"] = Value.FromStrings(apex.Node.Binary["dnsRecord"].Select(DnsCodec.Decode)
                    .Where(r => r.Type == DnsCodec.Types["NS"]).Select(r => DnsCodec.DecodeData(r.Type, r.Data)).Order(StringComparer.Ordinal));
            }
        }

        protected override void AddCreateAttributes(Config.ResourceInstance desired, Dictionary<string, object[]> attributes)
        {
            attributes["dNSProperty"] = new object[]
            {
                DnsCodec.EncodeProperty(DnsCodec.PropertyZoneType, BitConverter.GetBytes(1u)),
                DnsCodec.EncodeProperty(DnsCodec.PropertyAllowUpdate, new[] { AllowUpdate(V.Str(desired.Get("dynamic_update"))!) }),
            };
        }

        private static IEnumerable<DnsCodec.Record> ApexRecords(Config.ResourceInstance desired, uint serial)
        {
            uint ttl = (uint)V.Long(desired.Get("ttl"))!.Value;
            var soa = new DnsCodec.Soa(serial,
                (uint)V.Long(desired.Get("refresh"))!.Value, (uint)V.Long(desired.Get("retry"))!.Value,
                (uint)V.Long(desired.Get("expire"))!.Value, (uint)V.Long(desired.Get("minimum_ttl"))!.Value,
                V.Str(desired.Get("primary_server"))!, V.Str(desired.Get("admin_email"))!);
            yield return new DnsCodec.Record(DnsCodec.Types["SOA"], ttl, serial, DnsCodec.EncodeSoa(soa));
            foreach (string ns in V.List(desired.Get("name_servers")))
            {
                yield return new DnsCodec.Record(DnsCodec.Types["NS"], ttl, serial, DnsCodec.EncodeData("NS", ns));
            }
        }

        protected override void AfterCreate(ApplyContext ctx, ResourceChange change)
        {
            var desired = change.Desired!;
            ctx.Directory.Add($"DC=@,{desired.Dn}", new Dictionary<string, object[]>
            {
                ["objectClass"] = new object[] { "dnsNode" },
                ["dnsRecord"] = ApexRecords(desired, 1).Select(DnsCodec.Encode).Cast<object>().ToArray(),
            });
        }

        protected override IEnumerable<Modification> SpecialModifications(ResourceChange change)
        {
            if (change.Get("dynamic_update") is null)
            {
                yield break;
            }
            var properties = (change.Actual!.Binary.GetValueOrDefault("dNSProperty") ?? Array.Empty<byte[]>())
                .Where(v => DnsCodec.DecodeProperty(v)?.Id != DnsCodec.PropertyAllowUpdate)
                .Append(DnsCodec.EncodeProperty(DnsCodec.PropertyAllowUpdate, new[] { AllowUpdate(V.Str(change.Desired!.Get("dynamic_update"))!) }))
                .Cast<object>()
                .ToArray();
            yield return new Modification("dNSProperty", ModOp.Replace, properties);
        }

        protected override void AfterUpdate(ApplyContext ctx, ResourceChange change, string dn)
        {
            if (!change.Changes.Any(c => SoaAttributes.Contains(c.Name)))
            {
                return;
            }
            var desired = change.Desired!;
            var apex = DnsCodec.ReadApexSoa(ctx.Directory, dn);
            uint serial = DnsCodec.NextSerial(apex?.Soa.Serial ?? 0);
            var others = apex?.Node.Binary["dnsRecord"].Select(DnsCodec.Decode)
                .Where(r => r.Type != DnsCodec.Types["SOA"] && r.Type != DnsCodec.Types["NS"]) ?? Enumerable.Empty<DnsCodec.Record>();
            var values = others.Concat(ApexRecords(desired, serial)).Select(DnsCodec.Encode).Cast<object>().ToArray();
            if (apex == null)
            {
                ctx.Directory.Add($"DC=@,{dn}", new Dictionary<string, object[]> { ["objectClass"] = new object[] { "dnsNode" }, ["dnsRecord"] = values });
            }
            else
            {
                ctx.Directory.Modify(apex.Value.Node.Dn, new[] { new Modification("dnsRecord", ModOp.Replace, values) });
            }
        }

        /// <summary>A zone is deleted with all its records.</summary>
        public override void Delete(ApplyContext ctx, ResourceChange change)
        {
            foreach (var node in ctx.Directory.Search(change.CurrentDn!, "(objectClass=dnsNode)", subtree: false, Array.Empty<string>()))
            {
                ctx.Directory.Delete(node.Dn);
            }
            ctx.Directory.Delete(change.CurrentDn!);
        }
    }
}
