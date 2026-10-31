using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Declarad.Directory;
using Declarad.Engine;
using Declarad.Hcl;

namespace Declarad.Resources
{
    /// <summary>
    /// Binary formats of AD-integrated DNS (MS-DNSP): dnsRecord values on dnsNode objects and dNSProperty values
    /// on dnsZone objects. Multi-byte fields in record data are in network byte order.
    /// </summary>
    public static partial class DnsCodec
    {
        public static readonly IReadOnlyDictionary<string, ushort> Types = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase)
        {
            ["A"] = 1, ["NS"] = 2, ["CNAME"] = 5, ["SOA"] = 6, ["PTR"] = 12, ["MX"] = 15, ["TXT"] = 16, ["AAAA"] = 28, ["SRV"] = 33,
        };

        public const uint PropertyZoneType = 0x01;
        public const uint PropertyAllowUpdate = 0x02;

        /// <param name="Timestamp">Hours since 1601 when a dynamic record was last refreshed; 0 for static records.</param>
        public sealed record Record(ushort Type, uint Ttl, uint Serial, byte[] Data, uint Timestamp = 0)
        {
            public bool IsDynamic => Timestamp != 0;

            public string TypeName => Types.FirstOrDefault(t => t.Value == Type).Key ?? Type.ToString(CultureInfo.InvariantCulture);
        }

        public sealed record Soa(uint Serial, uint Refresh, uint Retry, uint Expire, uint Minimum, string PrimaryServer, string AdminEmail);

        [GeneratedRegex(@"^(\*|[A-Za-z0-9_]([A-Za-z0-9_-]{0,61}[A-Za-z0-9_])?)$")]
        private static partial Regex Label();

        // ---- dnsRecord ---------------------------------------------------------------------------------

        public static byte[] Encode(Record record)
        {
            var buffer = new byte[24 + record.Data.Length];
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(0), (ushort)record.Data.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(2), record.Type);
            buffer[4] = 5;    // Version
            buffer[5] = 0xF0; // Rank: zone data
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(8), record.Serial);
            BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(12), record.Ttl);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(20), record.Timestamp);
            record.Data.CopyTo(buffer, 24);
            return buffer;
        }

        public static Record Decode(byte[] value)
        {
            try
            {
                return DecodeUnchecked(value);
            }
            catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException)
            {
                throw new DeclaradException("Malformed dnsRecord value in the directory.", default, ex);
            }
        }

        private static Record DecodeUnchecked(byte[] value)
        {
            if (value.Length < 24)
            {
                throw new DeclaradException("Malformed dnsRecord value.");
            }
            int length = BinaryPrimitives.ReadUInt16LittleEndian(value);
            return new Record(
                BinaryPrimitives.ReadUInt16LittleEndian(value.AsSpan(2)),
                BinaryPrimitives.ReadUInt32BigEndian(value.AsSpan(12)),
                BinaryPrimitives.ReadUInt32LittleEndian(value.AsSpan(8)),
                value.AsSpan(24, Math.Min(length, value.Length - 24)).ToArray(),
                BinaryPrimitives.ReadUInt32LittleEndian(value.AsSpan(20)));
        }

        /// <summary>DNS_COUNT_NAME: total length, label count, length-prefixed labels, terminating zero.</summary>
        public static byte[] CountName(string fqdn)
        {
            var labels = fqdn.TrimEnd('.').Split('.', StringSplitOptions.RemoveEmptyEntries);
            var raw = new List<byte>();
            foreach (string label in labels)
            {
                var bytes = Encoding.UTF8.GetBytes(label);
                if (bytes.Length > 63)
                {
                    throw new DeclaradException($"DNS label '{label}' is longer than 63 bytes.");
                }
                raw.Add((byte)bytes.Length);
                raw.AddRange(bytes);
            }
            raw.Add(0);
            if (raw.Count > 255)
            {
                throw new DeclaradException($"DNS name '{fqdn}' is longer than 255 bytes.");
            }
            return new[] { (byte)raw.Count, (byte)labels.Length }.Concat(raw).ToArray();
        }

        public static string ReadCountName(byte[] data, ref int offset)
        {
            int length = data[offset];
            int count = data[offset + 1];
            int p = offset + 2;
            var labels = new List<string>();
            for (int i = 0; i < count && p < data.Length; i++)
            {
                int len = data[p];
                labels.Add(Encoding.UTF8.GetString(data, p + 1, len));
                p += 1 + len;
            }
            offset += 2 + length;
            return string.Join(".", labels).ToLowerInvariant() + ".";
        }

        private static byte[] U16(int v)
        {
            var b = new byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(b, checked((ushort)v));
            return b;
        }

        private static byte[] U32(uint v)
        {
            var b = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(b, v);
            return b;
        }

        /// <summary>Encodes canonical record text (see <see cref="Canonicalize"/>) as record data.</summary>
        public static byte[] EncodeData(string type, string text)
        {
            var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            switch (type)
            {
                case "A":
                case "AAAA":
                    return IPAddress.Parse(text).GetAddressBytes();
                case "CNAME":
                case "NS":
                case "PTR":
                    return CountName(text);
                case "MX":
                    return U16(int.Parse(parts[0], CultureInfo.InvariantCulture)).Concat(CountName(parts[1])).ToArray();
                case "SRV":
                    return U16(int.Parse(parts[0], CultureInfo.InvariantCulture)).Concat(U16(int.Parse(parts[1], CultureInfo.InvariantCulture)))
                        .Concat(U16(int.Parse(parts[2], CultureInfo.InvariantCulture))).Concat(CountName(parts[3])).ToArray();
                case "TXT":
                {
                    var bytes = Encoding.UTF8.GetBytes(text);
                    var result = new List<byte>();
                    for (int i = 0; i < bytes.Length || i == 0; i += 255)
                    {
                        var chunk = bytes.Skip(i).Take(255).ToArray();
                        result.Add((byte)chunk.Length);
                        result.AddRange(chunk);
                        if (bytes.Length == 0)
                        {
                            break;
                        }
                    }
                    return result.ToArray();
                }
            }
            throw new DeclaradException($"Unsupported DNS record type {type}.");
        }

        public static string DecodeData(ushort type, byte[] data)
        {
            try
            {
                return DecodeDataUnchecked(type, data);
            }
            catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException)
            {
                throw new DeclaradException($"Malformed DNS record data (type {type}) in the directory.", default, ex);
            }
        }

        private static string DecodeDataUnchecked(ushort type, byte[] data)
        {
            int offset;
            switch (type)
            {
                case 1:
                case 28:
                    return new IPAddress(data).ToString();
                case 2:
                case 5:
                case 12:
                    offset = 0;
                    return ReadCountName(data, ref offset);
                case 15:
                    offset = 2;
                    return $"{BinaryPrimitives.ReadUInt16BigEndian(data)} {ReadCountName(data, ref offset)}";
                case 33:
                    offset = 6;
                    return $"{BinaryPrimitives.ReadUInt16BigEndian(data)} {BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(2))} {BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(4))} {ReadCountName(data, ref offset)}";
                case 16:
                {
                    var sb = new StringBuilder();
                    for (int p = 0; p < data.Length;)
                    {
                        int len = data[p];
                        sb.Append(Encoding.UTF8.GetString(data, p + 1, Math.Min(len, data.Length - p - 1)));
                        p += 1 + len;
                    }
                    return sb.ToString();
                }
            }
            return Convert.ToHexString(data);
        }

        public static byte[] EncodeSoa(Soa soa) =>
            U32(soa.Serial).Concat(U32(soa.Refresh)).Concat(U32(soa.Retry)).Concat(U32(soa.Expire)).Concat(U32(soa.Minimum))
                .Concat(CountName(soa.PrimaryServer)).Concat(CountName(soa.AdminEmail)).ToArray();

        public static Soa DecodeSoa(byte[] data)
        {
            int offset = 20;
            string primary = ReadCountName(data, ref offset);
            string admin = ReadCountName(data, ref offset);
            return new Soa(
                BinaryPrimitives.ReadUInt32BigEndian(data), BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4)), BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(8)),
                BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(12)), BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(16)), primary, admin);
        }

        // ---- dNSProperty ---------------------------------------------------------------------------------

        public static byte[] EncodeProperty(uint id, byte[] data)
        {
            var buffer = new byte[20 + data.Length + 1];
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0), (uint)data.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4), 1); // NameLength
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(12), 1); // Version
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(16), id);
            data.CopyTo(buffer, 20);
            return buffer;
        }

        public static (uint Id, byte[] Data)? DecodeProperty(byte[] value)
        {
            if (value.Length < 20)
            {
                return null;
            }
            int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(value);
            return (BinaryPrimitives.ReadUInt32LittleEndian(value.AsSpan(16)), value.AsSpan(20, Math.Min(length, value.Length - 20)).ToArray());
        }

        // ---- Names -----------------------------------------------------------------------------------

        public static string? ValidateName(string name, bool allowApex)
        {
            if (allowApex && name == "@")
            {
                return null;
            }
            var labels = name.TrimEnd('.').Split('.');
            if (name.Length > 253 || labels.Any(l => !Label().IsMatch(l)))
            {
                return $"'{name}' is not a valid DNS name.";
            }
            return null;
        }

        /// <summary>Absolute name with trailing dot. Names without a trailing dot are relative to the zone, as in zone files.</summary>
        public static string Fqdn(string name, string zone)
        {
            name = name.Trim().ToLowerInvariant();
            if (name == "@")
            {
                return zone + ".";
            }
            return name.EndsWith('.') ? name : $"{name}.{zone}.";
        }

        /// <summary>Normalizes configured record text to the canonical form produced by decoding.</summary>
        public static string Canonicalize(string type, string value, string zone)
        {
            var parts = value.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            switch (type)
            {
                case "A":
                    // Require a dotted quad: IPAddress.TryParse would also accept "10" as 0.0.0.10.
                    if (value.Split('.').Length != 4 || !IPAddress.TryParse(value, out var v4) || v4.AddressFamily != AddressFamily.InterNetwork)
                    {
                        throw new DeclaradException($"'{value}' is not an IPv4 address.");
                    }
                    return v4.ToString();
                case "AAAA":
                    if (!IPAddress.TryParse(value, out var v6) || v6.AddressFamily != AddressFamily.InterNetworkV6)
                    {
                        throw new DeclaradException($"'{value}' is not an IPv6 address.");
                    }
                    return v6.ToString();
                case "CNAME":
                case "NS":
                case "PTR":
                    if (parts.Length != 1 || ValidateName(parts[0], false) is { } err)
                    {
                        throw new DeclaradException($"'{value}' is not a host name.");
                    }
                    return Fqdn(parts[0], zone);
                case "MX":
                    if (parts.Length != 2 || !ushort.TryParse(parts[0], out ushort pref))
                    {
                        throw new DeclaradException($"MX record '{value}' must be \"<preference> <host>\", e.g. \"10 mail\".");
                    }
                    return $"{pref} {Fqdn(HostName(parts[1], value), zone)}";
                case "SRV":
                    if (parts.Length != 4 || !parts.Take(3).All(p => ushort.TryParse(p, out _)))
                    {
                        throw new DeclaradException($"SRV record '{value}' must be \"<priority> <weight> <port> <target>\", e.g. \"0 100 389 dc01\".");
                    }
                    return $"{ushort.Parse(parts[0])} {ushort.Parse(parts[1])} {ushort.Parse(parts[2])} {Fqdn(HostName(parts[3], value), zone)}";
                case "TXT":
                    return value;
            }
            throw new DeclaradException($"Unsupported DNS record type {type}.");
        }

        private static string HostName(string name, string record) =>
            name == "@" || ValidateName(name, false) == null ? name : throw new DeclaradException($"'{name}' in '{record}' is not a valid host name.");

        /// <summary>RFC 1982 serial arithmetic: wrap around, skipping 0.</summary>
        public static uint NextSerial(uint serial) => serial == uint.MaxValue ? 1 : serial + 1;

        /// <summary>Container holding the zones of a replication scope.</summary>
        public static string ZoneContainer(string scope, SchemaContext ctx) => scope switch
        {
            "forest" => $"CN=MicrosoftDNS,DC=ForestDnsZones,{ctx.ForestBaseDn ?? ctx.BaseDn}",
            "legacy" => $"CN=MicrosoftDNS,CN=System,{ctx.BaseDn}",
            _ => $"CN=MicrosoftDNS,DC=DomainDnsZones,{ctx.BaseDn}",
        };

        public static string NormalizeZone(string zone) => zone.Trim().TrimEnd('.').ToLowerInvariant();

        /// <summary>Reads the SOA record of a zone's apex node, if present.</summary>
        public static (DirectoryObject Node, Record Record, Soa Soa)? ReadApexSoa(IDirectory directory, string zoneDn)
        {
            var apex = directory.GetByDn($"DC=@,{zoneDn}", new[] { "dnsRecord" });
            if (apex == null || !apex.Binary.TryGetValue("dnsRecord", out var values))
            {
                return null;
            }
            foreach (var value in values)
            {
                var record = Decode(value);
                if (record.Type == Types["SOA"])
                {
                    return (apex, record, DecodeSoa(record.Data));
                }
            }
            return null;
        }

        /// <summary>Increments the zone serial so secondary servers notice the change (best effort).</summary>
        public static void BumpSerial(IDirectory directory, string zoneDn)
        {
            if (ReadApexSoa(directory, zoneDn) is not { } apex)
            {
                return;
            }
            var values = apex.Node.Binary["dnsRecord"].Select(Decode).ToList();
            var soa = apex.Soa with { Serial = NextSerial(apex.Soa.Serial) };
            var updated = values.Select(r => r.Type == Types["SOA"] ? r with { Data = EncodeSoa(soa), Serial = soa.Serial } : r).Select(Encode).Cast<object>().ToArray();
            directory.Modify(apex.Node.Dn, new[] { new Modification("dnsRecord", ModOp.Replace, updated) });
        }
    }

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
