using System.Buffers.Binary;
using System.Globalization;

namespace Declarad.Directory
{
    /// <summary>Converts security identifiers between binary and S-1-5-... string form (platform independent).</summary>
    public static class Sid
    {
        public static string ToString(byte[] sid) => ToString(sid, 0);

        public static string ToString(byte[] buffer, int offset)
        {
            if (buffer.Length < offset + 8 || buffer.Length < offset + 8 + 4 * buffer[offset + 1])
            {
                throw new DeclaradException("Malformed binary SID.");
            }
            int count = buffer[offset + 1];
            long authority = 0;
            for (int i = 0; i < 6; i++)
            {
                authority = (authority << 8) | buffer[offset + 2 + i];
            }
            // MS-DTYP: identifier authorities of 2^32 and above are written in hexadecimal.
            string authorityText = authority >= 1L << 32 ? "0x" + authority.ToString("X12", CultureInfo.InvariantCulture) : authority.ToString(CultureInfo.InvariantCulture);
            var parts = new List<string> { "S", buffer[offset].ToString(CultureInfo.InvariantCulture), authorityText };
            for (int i = 0; i < count; i++)
            {
                parts.Add(BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset + 8 + 4 * i)).ToString(CultureInfo.InvariantCulture));
            }
            return string.Join("-", parts);
        }

        public static int Length(byte[] buffer, int offset) => 8 + 4 * buffer[offset + 1];

        public static byte[] ToBytes(string sid)
        {
            var parts = sid.Split('-');
            var invalid = new DeclaradException($"'{sid}' is not a valid SID.");
            if (parts.Length < 3 || parts.Length > 3 + 15 || !parts[0].Equals("S", StringComparison.OrdinalIgnoreCase)
                || !byte.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out byte revision))
            {
                throw invalid;
            }
            var subs = new uint[parts.Length - 3];
            for (int i = 0; i < subs.Length; i++)
            {
                if (!uint.TryParse(parts[3 + i], NumberStyles.None, CultureInfo.InvariantCulture, out subs[i]))
                {
                    throw invalid;
                }
            }
            bool hex = parts[2].StartsWith("0x", StringComparison.OrdinalIgnoreCase);
            if (!long.TryParse(hex ? parts[2][2..] : parts[2], hex ? NumberStyles.AllowHexSpecifier : NumberStyles.None, CultureInfo.InvariantCulture, out long authority)
                || authority >= 1L << 48)
            {
                throw invalid;
            }
            var bytes = new byte[8 + 4 * subs.Length];
            bytes[0] = revision;
            bytes[1] = (byte)subs.Length;
            for (int i = 5; i >= 0; i--)
            {
                bytes[2 + i] = (byte)(authority & 0xFF);
                authority >>= 8;
            }
            for (int i = 0; i < subs.Length; i++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8 + 4 * i), subs[i]);
            }
            return bytes;
        }
    }
}
