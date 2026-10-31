using System.Buffers.Binary;

namespace Declarad.Directory
{
    /// <summary>
    /// Minimal editor for self-relative binary security descriptors. Only what "protect from accidental deletion"
    /// needs: find, add or remove an explicit "Deny Everyone Delete + Delete Subtree" ACE in the DACL.
    /// Written by hand because System.Security.AccessControl is Windows-only.
    /// </summary>
    public static class SecurityDescriptor
    {
        private const byte AccessDeniedAceType = 0x01;
        private const byte InheritedAceFlag = 0x10;
        private const uint Delete = 0x00010000;
        private const uint DeleteTree = 0x00000040;
        public const uint ProtectionMask = Delete | DeleteTree;
        private const ushort DaclPresent = 0x0004;
        private const ushort SelfRelative = 0x8000;
        private const byte AclRevision = 0x02;

        // S-1-1-0 (Everyone): revision 1, one sub-authority, identifier authority 1, sub-authority 0.
        private static readonly byte[] EveryoneSid = { 1, 1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0 };

        private sealed record Ace(byte Type, byte Flags, byte[] Raw)
        {
            public uint Mask => BinaryPrimitives.ReadUInt32LittleEndian(Raw.AsSpan(4));
            public ReadOnlySpan<byte> Sid => Raw.AsSpan(8);
        }

        private sealed class Parsed
        {
            public byte Revision;
            public ushort Control;
            public byte[]? Owner;
            public byte[]? Group;
            public byte[]? Sacl;
            public byte DaclRevision = AclRevision;
            public List<Ace>? Dacl;
        }

        /// <summary>"Delete child" (ADS_RIGHT_DS_DELETE_CHILD): denied on the parent of a protected object, as ADUC does.</summary>
        public const uint DeleteChild = 0x00000002;

        public static bool IsProtectedFromDeletion(byte[]? sd) => HasDenyEveryone(sd, ProtectionMask);

        public static byte[] SetProtectedFromDeletion(byte[] sd, bool protect) => SetDenyEveryone(sd, ProtectionMask, protect);

        /// <summary>Whether an explicit ACE denies Everyone all rights in <paramref name="mask"/>.</summary>
        public static bool HasDenyEveryone(byte[]? sd, uint mask)
        {
            if (sd == null || sd.Length < 20)
            {
                return false;
            }
            return Parse(sd).Dacl?.Any(ace => ace.Type == AccessDeniedAceType
                                              && (ace.Flags & InheritedAceFlag) == 0
                                              && (ace.Mask & mask) == mask
                                              && ace.Sid.SequenceEqual(EveryoneSid)) == true;
        }

        /// <summary>Adds or removes an explicit "deny Everyone <paramref name="mask"/>" ACE, leaving other rights untouched.</summary>
        public static byte[] SetDenyEveryone(byte[] sd, uint mask, bool deny)
        {
            var parsed = Parse(sd);
            if (parsed.Dacl == null && deny)
            {
                // No DACL means "everyone has full access"; a DACL with only the deny ACE would lock everyone out.
                throw new DeclaradException("The object has no DACL; refusing to add a deny ACE. Fix its permissions first.");
            }
            var result = new List<Ace>();
            foreach (var ace in parsed.Dacl ?? new List<Ace>())
            {
                if (ace.Type == AccessDeniedAceType && (ace.Flags & InheritedAceFlag) == 0 && ace.Sid.SequenceEqual(EveryoneSid) && (ace.Mask & mask) != 0)
                {
                    uint remaining = ace.Mask & ~mask;
                    if (remaining != 0)
                    {
                        result.Add(BuildAce(ace.Flags, remaining, ace.Sid.ToArray()));
                    }
                    continue;
                }
                result.Add(ace);
            }
            if (deny)
            {
                // Explicit deny ACEs go first in canonical order.
                result.Insert(0, BuildAce(0, mask, EveryoneSid));
            }
            parsed.Dacl = result;
            parsed.Control |= DaclPresent;
            return Build(parsed);
        }

        private const byte AccessAllowedAceType = 0x00;

        /// <summary>
        /// Builds a descriptor granting <paramref name="mask"/> to each SID, as used by
        /// msDS-GroupMSAMembership (who may retrieve a gMSA password).
        /// </summary>
        public static byte[] BuildAllowList(string ownerSid, IEnumerable<string> sids, uint mask)
        {
            var owner = Sid.ToBytes(ownerSid);
            var parsed = new Parsed
            {
                Revision = 1,
                Control = DaclPresent,
                Owner = owner,
                DaclRevision = AclRevision,
                Dacl = sids.Select(s =>
                {
                    var ace = BuildAce(0, mask, Sid.ToBytes(s));
                    ace.Raw[0] = AccessAllowedAceType;
                    return ace with { Type = AccessAllowedAceType };
                }).ToList(),
            };
            return Build(parsed);
        }

        /// <summary>SIDs granted access by explicit allow ACEs in the DACL.</summary>
        public static IReadOnlyList<string> AllowedSids(byte[]? sd)
        {
            if (sd == null || sd.Length < 20)
            {
                return Array.Empty<string>();
            }
            return (Parse(sd).Dacl ?? new List<Ace>())
                .Where(a => a.Type == AccessAllowedAceType && a.Raw.Length >= 16)
                .Select(a => Sid.ToString(a.Raw, 8))
                .Distinct()
                .ToList();
        }


        private static Ace BuildAce(byte flags, uint mask, byte[] sid)
        {
            var raw = new byte[8 + sid.Length];
            raw[0] = AccessDeniedAceType;
            raw[1] = flags;
            BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(2), (ushort)raw.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(4), mask);
            sid.CopyTo(raw, 8);
            return new Ace(AccessDeniedAceType, flags, raw);
        }

        private static Parsed Parse(byte[] sd)
        {
            try
            {
                return ParseUnchecked(sd);
            }
            catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException or ArgumentException)
            {
                throw new DeclaradException("Malformed security descriptor.", default, ex);
            }
        }

        private static Parsed ParseUnchecked(byte[] sd)
        {
            if (sd.Length < 20)
            {
                throw new DeclaradException("Security descriptor is too short.");
            }
            var span = sd.AsSpan();
            var parsed = new Parsed
            {
                Revision = sd[0],
                Control = BinaryPrimitives.ReadUInt16LittleEndian(span[2..]),
            };
            int ownerOff = (int)BinaryPrimitives.ReadUInt32LittleEndian(span[4..]);
            int groupOff = (int)BinaryPrimitives.ReadUInt32LittleEndian(span[8..]);
            int saclOff = (int)BinaryPrimitives.ReadUInt32LittleEndian(span[12..]);
            int daclOff = (int)BinaryPrimitives.ReadUInt32LittleEndian(span[16..]);

            if (ownerOff != 0)
            {
                parsed.Owner = ReadSid(sd, ownerOff);
            }
            if (groupOff != 0)
            {
                parsed.Group = ReadSid(sd, groupOff);
            }
            if (saclOff != 0)
            {
                int size = BinaryPrimitives.ReadUInt16LittleEndian(span[(saclOff + 2)..]);
                parsed.Sacl = sd.AsSpan(saclOff, size).ToArray();
            }
            if (daclOff != 0)
            {
                parsed.DaclRevision = sd[daclOff];
                int count = BinaryPrimitives.ReadUInt16LittleEndian(span[(daclOff + 4)..]);
                int p = daclOff + 8;
                parsed.Dacl = new List<Ace>(count);
                for (int i = 0; i < count; i++)
                {
                    int aceSize = BinaryPrimitives.ReadUInt16LittleEndian(span[(p + 2)..]);
                    if (aceSize < 4 || p + aceSize > sd.Length)
                    {
                        throw new DeclaradException("Malformed ACE in security descriptor.");
                    }
                    parsed.Dacl.Add(new Ace(sd[p], sd[p + 1], sd.AsSpan(p, aceSize).ToArray()));
                    p += aceSize;
                }
            }
            return parsed;
        }

        private static byte[] ReadSid(byte[] sd, int offset)
        {
            int subAuthorities = sd[offset + 1];
            return sd.AsSpan(offset, 8 + 4 * subAuthorities).ToArray();
        }

        private static byte[] Build(Parsed p)
        {
            using var ms = new MemoryStream();
            ms.Write(new byte[20]);
            int saclOff = 0, daclOff = 0, ownerOff = 0, groupOff = 0;
            if (p.Sacl != null)
            {
                saclOff = (int)ms.Position;
                ms.Write(p.Sacl);
            }
            if (p.Dacl != null)
            {
                daclOff = (int)ms.Position;
                int size = 8 + p.Dacl.Sum(a => a.Raw.Length);
                var header = new byte[8];
                header[0] = p.DaclRevision;
                BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(2), (ushort)size);
                BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), (ushort)p.Dacl.Count);
                ms.Write(header);
                foreach (var ace in p.Dacl)
                {
                    ms.Write(ace.Raw);
                }
            }
            if (p.Owner != null)
            {
                ownerOff = (int)ms.Position;
                ms.Write(p.Owner);
            }
            if (p.Group != null)
            {
                groupOff = (int)ms.Position;
                ms.Write(p.Group);
            }
            var result = ms.ToArray();
            result[0] = p.Revision == 0 ? (byte)1 : p.Revision;
            BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(2), (ushort)(p.Control | SelfRelative));
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), (uint)ownerOff);
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8), (uint)groupOff);
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(12), (uint)saclOff);
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(16), (uint)daclOff);
            return result;
        }
    }
}
