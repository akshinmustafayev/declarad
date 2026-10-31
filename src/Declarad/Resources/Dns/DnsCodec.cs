using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Declarad.Directory;
using Declarad.Resources.Schema;

namespace Declarad.Resources.Dns
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
}
