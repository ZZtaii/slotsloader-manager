using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace SlotsInstaller
{
    // ZipArchive on .NET Framework decompresses entries without checking their CRC.
    // Read only the checksum metadata here; ZipArchive remains responsible for ZIP
    // names, compression, local headers and optional data descriptors.
    // Directory layouts: PKWARE APPNOTE sections 4.3.12 and 4.3.14-4.3.16.
    // https://pkware.cachefly.net/webdocs/casestudies/APPNOTE.TXT
    internal static class ZipIntegrity
    {
        internal static uint[] ReadChecksums(string path, ZipArchive archive)
        {
            try
            {
                using (var stream = File.OpenRead(path))
                using (var reader = new BinaryReader(stream))
                {
                    if (stream.Length < 22) throw Invalid();
                    int tailLength = (int)Math.Min(stream.Length, 65535L + 22);
                    stream.Position = stream.Length - tailLength;
                    byte[] tail = reader.ReadBytes(tailLength);
                    int end = -1;
                    for (int i = tail.Length - 22; i >= 0; i--)
                        if (U32(tail, i) == 0x06054b50 && i + 22 + U16(tail, i + 20) == tail.Length) { end = i; break; }
                    if (end < 0) throw Invalid();
                    long endPosition = stream.Length - tailLength + end;
                    if (U16(tail, end + 4) != 0 || U16(tail, end + 6) != 0) throw new IOException("Multi-disk ZIP archives are unsupported.");
                    long count = U16(tail, end + 10), centralSize = U32(tail, end + 12), centralOffset = U32(tail, end + 16);
                    long centralEndLimit = endPosition;
                    if (count == 65535 || centralSize == UInt32.MaxValue || centralOffset == UInt32.MaxValue)
                    {
                        if (endPosition < 20) throw Invalid();
                        stream.Position = endPosition - 20;
                        if (reader.ReadUInt32() != 0x07064b50 || reader.ReadUInt32() != 0) throw Invalid();
                        ulong zip64Offset = reader.ReadUInt64();
                        if (reader.ReadUInt32() != 1 || zip64Offset > (ulong)(endPosition - 20)) throw Invalid();
                        stream.Position = (long)zip64Offset;
                        if (reader.ReadUInt32() != 0x06064b50) throw Invalid();
                        ulong zip64Length = reader.ReadUInt64();
                        if (zip64Length < 44 || zip64Length > (ulong)(endPosition - 20 - stream.Position)) throw Invalid();
                        reader.ReadUInt16(); reader.ReadUInt16();
                        if (reader.ReadUInt32() != 0 || reader.ReadUInt32() != 0) throw new IOException("Multi-disk ZIP archives are unsupported.");
                        ulong diskCount = reader.ReadUInt64(), totalCount = reader.ReadUInt64();
                        if (diskCount != totalCount || totalCount > Int32.MaxValue) throw Invalid();
                        count = (long)totalCount;
                        centralSize = CheckedLong(reader.ReadUInt64());
                        centralOffset = CheckedLong(reader.ReadUInt64());
                        centralEndLimit = (long)zip64Offset;
                    }
                    else if (U16(tail, end + 8) != count) throw Invalid();
                    if (count != archive.Entries.Count || centralOffset < 0 || centralSize < 0 || centralOffset > centralEndLimit || centralSize > centralEndLimit - centralOffset) throw Invalid();
                    long centralEnd = centralOffset + centralSize;
                    stream.Position = centralOffset;
                    var checksums = new uint[(int)count];
                    // ZipArchive.Entries follows central-directory order. Indexing rather
                    // than decoding filenames also preserves its legacy-name encoding.
                    for (int i = 0; i < checksums.Length; i++)
                    {
                        OperationContext.ThrowIfCancellationRequested();
                        if (centralEnd - stream.Position < 46 || reader.ReadUInt32() != 0x02014b50) throw Invalid();
                        reader.ReadUInt16(); reader.ReadUInt16(); reader.ReadUInt16(); reader.ReadUInt16();
                        reader.ReadUInt16(); reader.ReadUInt16();
                        checksums[i] = reader.ReadUInt32();
                        reader.ReadUInt32(); reader.ReadUInt32();
                        int nameLength = reader.ReadUInt16(), extraLength = reader.ReadUInt16(), commentLength = reader.ReadUInt16();
                        reader.ReadUInt16(); reader.ReadUInt16(); reader.ReadUInt32(); reader.ReadUInt32();
                        long skip = (long)nameLength + extraLength + commentLength;
                        if (skip > centralEnd - stream.Position) throw Invalid();
                        stream.Position += skip;
                    }
                    return checksums;
                }
            }
            catch (EndOfStreamException ex) { throw new IOException("This ZIP is incomplete or corrupt. Download or recreate it.", ex); }
        }
        private static long CheckedLong(ulong value) { if (value > Int64.MaxValue) throw Invalid(); return (long)value; }
        private static ushort U16(byte[] bytes, int offset) { return (ushort)(bytes[offset] | bytes[offset + 1] << 8); }
        private static uint U32(byte[] bytes, int offset) { return (uint)(bytes[offset] | bytes[offset + 1] << 8 | bytes[offset + 2] << 16 | bytes[offset + 3] << 24); }
        private static IOException Invalid() { return new IOException("This ZIP is invalid or corrupt. Download or recreate it."); }
    }

    // 1.0.1 (speed): slicing-by-8 (eight tables, 8 bytes per step), several times faster than the byte-at-a-time
    // loop with exactly the same CRC-32 (zip polynomial, reflected).
    internal sealed class ZipCrc32
    {
        private static readonly uint[][] Tables = CreateTables();
        private uint value = UInt32.MaxValue;
        internal uint Value { get { return ~value; } }
        internal void Update(byte[] buffer, int count)
        {
            uint current = value;
            uint[] t0 = Tables[0], t1 = Tables[1], t2 = Tables[2], t3 = Tables[3], t4 = Tables[4], t5 = Tables[5], t6 = Tables[6], t7 = Tables[7];
            int i = 0;
            for (; i + 8 <= count; i += 8)
            {
                uint a = current ^ (uint)(buffer[i] | buffer[i + 1] << 8 | buffer[i + 2] << 16 | buffer[i + 3] << 24);
                uint b = (uint)(buffer[i + 4] | buffer[i + 5] << 8 | buffer[i + 6] << 16 | buffer[i + 7] << 24);
                current = t7[a & 255] ^ t6[(a >> 8) & 255] ^ t5[(a >> 16) & 255] ^ t4[a >> 24] ^ t3[b & 255] ^ t2[(b >> 8) & 255] ^ t1[(b >> 16) & 255] ^ t0[b >> 24];
            }
            for (; i < count; i++) current = t0[(current ^ buffer[i]) & 255] ^ (current >> 8);
            value = current;
        }
        private static uint[][] CreateTables()
        {
            var tables = new uint[8][];
            for (int k = 0; k < 8; k++) tables[k] = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int bit = 0; bit < 8; bit++) c = (c & 1) == 0 ? c >> 1 : 0xedb88320 ^ (c >> 1);
                tables[0][n] = c;
            }
            for (int n = 0; n < 256; n++)
                for (int k = 1; k < 8; k++) tables[k][n] = tables[0][tables[k - 1][n] & 255] ^ (tables[k - 1][n] >> 8);
            return tables;
        }
    }
}
