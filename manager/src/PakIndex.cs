using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace SlotsInstaller
{
    // RE Engine KPKA v4 path hash: murmur3_32 (seed 0xFFFFFFFF) of the UTF-16LE
    // lower-case path (low 32 bits) and upper-case path (high 32 bits).
    internal static class PakHash
    {
        internal static uint Murmur3(byte[] data, uint seed)
        {
            const uint c1 = 0xCC9E2D51, c2 = 0x1B873593;
            uint h = seed; int blocks = data.Length / 4;
            for (int i = 0; i < blocks; i++)
            {
                uint k = BitConverter.ToUInt32(data, i * 4);
                k *= c1; k = (k << 15) | (k >> 17); k *= c2;
                h ^= k; h = (h << 13) | (h >> 19); h = h * 5 + 0xE6546B64;
            }
            uint t = 0; int tail = blocks * 4;
            switch (data.Length & 3)
            {
                case 3: t ^= (uint)data[tail + 2] << 16; goto case 2;
                case 2: t ^= (uint)data[tail + 1] << 8; goto case 1;
                case 1: t ^= data[tail]; t *= c1; t = (t << 15) | (t >> 17); t *= c2; h ^= t; break;
            }
            h ^= (uint)data.Length;
            h ^= h >> 16; h *= 0x85EBCA6B; h ^= h >> 13; h *= 0xC2B2AE35; h ^= h >> 16;
            return h;
        }
        internal static string Normalize(string path)
        {
            string p = (path ?? "").Trim(' ', '\t', '\r', '\n').Replace('\\', '/');
            while (p.Contains("//")) p = p.Replace("//", "/");
            return p;
        }
        internal static ulong PathHash(string path)
        {
            string p = Normalize(path);
            uint lower = Murmur3(Encoding.Unicode.GetBytes(p.ToLowerInvariant()), 0xFFFFFFFF);
            uint upper = Murmur3(Encoding.Unicode.GetBytes(p.ToUpperInvariant()), 0xFFFFFFFF);
            return ((ulong)upper << 32) | lower;
        }
        internal static uint Lower(ulong hash) { return (uint)(hash & 0xFFFFFFFF); }
        internal static uint Upper(ulong hash) { return (uint)(hash >> 32); }
    }

    internal struct PakEntry
    {
        internal int Index;
        internal uint HashLower, HashUpper;
        internal long Offset, CompressedSize, DecompressedSize, Attributes;
        internal ulong Checksum;
        internal ulong Hash { get { return ((ulong)HashUpper << 32) | HashLower; } }
        internal bool Zeroed { get { return HashLower == 0 && HashUpper == 0; } }
    }

    // A read-only index over one KPKA v4 PAK: header, the complete entry table
    // and a hash lookup. Opening validates the header and every entry's bounds.
    internal sealed class PakIndex
    {
        internal const int HeaderSize = 16, EntrySize = 48;
        internal string Path;
        internal int Major, Minor, Features;
        internal uint Count, Fingerprint;
        internal long Length;
        internal DateTime LastWriteUtc;
        internal byte[] Header, Table;
        private Dictionary<ulong, List<int>> byHash;
        internal int ZeroedCount;

        internal static PakIndex Open(string path) { return Open(path, true); }

        internal static PakIndex Open(string path, bool validateEntries)
        {
            var info = new FileInfo(path);
            if (!info.Exists) throw new FileNotFoundException("PAK not found: " + path);
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20))
            {
                var index = new PakIndex { Path = System.IO.Path.GetFullPath(path), Length = fs.Length, LastWriteUtc = info.LastWriteTimeUtc };
                index.Header = ReadExactly(fs, HeaderSize);
                if (index.Header[0] != (byte)'K' || index.Header[1] != (byte)'P' || index.Header[2] != (byte)'K' || index.Header[3] != (byte)'A') throw new InvalidDataException("Not a KPKA PAK: " + path);
                index.Major = index.Header[4]; index.Minor = index.Header[5]; index.Features = BitConverter.ToUInt16(index.Header, 6);
                index.Count = BitConverter.ToUInt32(index.Header, 8); index.Fingerprint = BitConverter.ToUInt32(index.Header, 12);
                if (index.Major != 4) throw new InvalidDataException("Unsupported PAK version " + index.Major + "." + index.Minor + ": " + path);
                if (index.Features != 0) throw new InvalidDataException("PAK uses features 0x" + index.Features.ToString("X") + " (encrypted or extended table); not supported: " + path);
                long tableBytes = (long)index.Count * EntrySize;
                if (tableBytes > index.Length - HeaderSize || tableBytes > Int32.MaxValue) throw new InvalidDataException("PAK entry table exceeds the file: " + path);
                index.Table = ReadExactly(fs, (int)tableBytes);
                if (validateEntries)
                {
                    long dataStart = HeaderSize + tableBytes;
                    for (int i = 0; i < index.Count; i++)
                    {
                        long offset = BitConverter.ToInt64(index.Table, i * EntrySize + 8), size = BitConverter.ToInt64(index.Table, i * EntrySize + 16), full = BitConverter.ToInt64(index.Table, i * EntrySize + 24);
                        if (offset < dataStart || size < 0 || full < 0 || offset > index.Length - size) throw new InvalidDataException("PAK entry " + i + " lies outside the file; the table does not parse exactly: " + path);
                    }
                }
                return index;
            }
        }

        internal static byte[] ReadExactly(Stream s, int count)
        {
            var buffer = new byte[count]; int read = 0;
            while (read < count) { int n = s.Read(buffer, read, count - read); if (n <= 0) throw new EndOfStreamException("Unexpected end of PAK."); read += n; }
            return buffer;
        }

        private void EnsureLookup()
        {
            if (byHash != null) return;
            var map = new Dictionary<ulong, List<int>>((int)Count);
            int zeroed = 0;
            for (int i = 0; i < Count; i++)
            {
                ulong h = BitConverter.ToUInt64(Table, i * EntrySize);
                if (h == 0) { zeroed++; continue; }
                List<int> list;
                if (!map.TryGetValue(h, out list)) map[h] = list = new List<int>(1);
                list.Add(i);
            }
            ZeroedCount = zeroed; byHash = map;
        }

        internal List<int> Find(ulong hash)
        {
            EnsureLookup();
            List<int> list;
            return byHash.TryGetValue(hash, out list) ? list : new List<int>();
        }
        internal bool Contains(ulong hash) { EnsureLookup(); return byHash.ContainsKey(hash); }
        internal int CountZeroed() { EnsureLookup(); return ZeroedCount; }

        internal PakEntry Entry(int i)
        {
            int o = i * EntrySize;
            return new PakEntry
            {
                Index = i, HashLower = BitConverter.ToUInt32(Table, o), HashUpper = BitConverter.ToUInt32(Table, o + 4),
                Offset = BitConverter.ToInt64(Table, o + 8), CompressedSize = BitConverter.ToInt64(Table, o + 16),
                DecompressedSize = BitConverter.ToInt64(Table, o + 24), Attributes = BitConverter.ToInt64(Table, o + 32),
                Checksum = BitConverter.ToUInt64(Table, o + 40)
            };
        }

        internal string TocSha256()
        {
            using (var sha = FastHash.Sha256())
            {
                sha.TransformBlock(Header, 0, Header.Length, null, 0);
                sha.TransformFinalBlock(Table, 0, Table.Length);
                return BitConverter.ToString(sha.Hash).Replace("-", "");
            }
        }
        // The table identity ignoring the hash fields, which Fluffy-style invalidation zeroes.
        internal string LayoutSha256()
        {
            using (var sha = FastHash.Sha256())
            {
                sha.TransformBlock(Header, 0, Header.Length, null, 0);
                var row = new byte[EntrySize];
                for (int i = 0; i < Count; i++) { Buffer.BlockCopy(Table, i * EntrySize, row, 0, EntrySize); for (int k = 0; k < 8; k++) row[k] = 0; sha.TransformBlock(row, 0, EntrySize, null, 0); }
                sha.TransformFinalBlock(new byte[0], 0, 0);
                return BitConverter.ToString(sha.Hash).Replace("-", "");
            }
        }

        internal byte[] ReadRaw(PakEntry e)
        {
            if (e.CompressedSize > 512L * 1024 * 1024) throw new IOException("PAK entry too large to read.");
            using (var fs = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                fs.Seek(e.Offset, SeekOrigin.Begin);
                return ReadExactly(fs, (int)e.CompressedSize);
            }
        }

        internal byte[] Read(PakEntry e) { return Decompress(ReadRaw(e), e.Attributes, e.DecompressedSize); }

        internal static byte[] Decompress(byte[] raw, long attributes, long decompressedSize)
        {
            int comp = (int)(attributes & 0xFF);
            if (comp == 0 || raw.LongLength == decompressedSize) return raw;
            if (comp == 2 || (comp & 0xF) == 2) return Zstd.Decompress(raw, decompressedSize);
            if (comp == 1 || (comp & 0xF) == 1)
            {
                try { return Inflate(raw, 0, decompressedSize); }
                catch (InvalidDataException) { if (raw.Length > 2) return Inflate(raw, 2, decompressedSize); throw; }
            }
            throw new InvalidDataException("Unsupported PAK compression " + comp + ".");
        }
        private static byte[] Inflate(byte[] raw, int skip, long size)
        {
            var output = new byte[size];
            using (var input = new MemoryStream(raw, skip, raw.Length - skip))
            using (var deflate = new DeflateStream(input, CompressionMode.Decompress))
            {
                int read = 0;
                while (read < output.Length) { int n = deflate.Read(output, read, output.Length - read); if (n <= 0) break; read += n; }
                if (read != output.Length) throw new InvalidDataException("Deflate size mismatch.");
            }
            return output;
        }

        internal byte[] ReadByPath(string path)
        {
            List<int> found = Find(PakHash.PathHash(path));
            return found.Count == 0 ? null : Read(Entry(found[0]));
        }
    }

    // Writes of the 8 hash bytes of entry rows, verified before and after.
    internal sealed class PakHashWrite
    {
        internal int Index;
        internal byte[] Expected, Value;
    }

    internal static class PakWriter
    {
        // Rewrites the hash fields of the given rows in place. Each row must hold
        // `Expected` (or already `Value`). Header, count and length must still
        // match. Flushes to disk and re-reads every row before returning.
        internal static void WriteHashes(string pakPath, long expectedLength, byte[] expectedHeader, IList<PakHashWrite> writes)
        {
            if (writes.Count == 0) return;
            using (var fs = new FileStream(pakPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, 4096, FileOptions.WriteThrough))
            {
                if (fs.Length != expectedLength) throw new IOException("The PAK changed size; refusing to write: " + pakPath);
                byte[] header = PakIndex.ReadExactly(fs, PakIndex.HeaderSize);
                if (!header.SequenceEqual(expectedHeader)) throw new IOException("The PAK header changed; refusing to write: " + pakPath);
                if (BitConverter.ToUInt16(header, 6) != 0) throw new IOException("The PAK uses features; refusing to write: " + pakPath);
                uint count = BitConverter.ToUInt32(header, 8);
                var current = new byte[8];
                foreach (PakHashWrite w in writes)
                {
                    if (w.Index < 0 || w.Index >= count || w.Value == null || w.Value.Length != 8 || w.Expected == null || w.Expected.Length != 8) throw new IOException("Invalid PAK row write.");
                    long at = PakIndex.HeaderSize + (long)w.Index * PakIndex.EntrySize;
                    fs.Seek(at, SeekOrigin.Begin);
                    current = PakIndex.ReadExactly(fs, 8);
                    if (current.SequenceEqual(w.Value)) continue;
                    if (!current.SequenceEqual(w.Expected)) throw new IOException("PAK row " + w.Index + " no longer holds the expected hash; refusing to write: " + pakPath);
                    fs.Seek(at, SeekOrigin.Begin);
                    fs.Write(w.Value, 0, 8);
                }
                fs.Flush(true);
                foreach (PakHashWrite w in writes)
                {
                    fs.Seek(PakIndex.HeaderSize + (long)w.Index * PakIndex.EntrySize, SeekOrigin.Begin);
                    if (!PakIndex.ReadExactly(fs, 8).SequenceEqual(w.Value)) throw new IOException("PAK row " + w.Index + " did not verify after writing: " + pakPath);
                }
            }
        }

        internal static byte[] HashBytes(uint lower, uint upper)
        {
            var b = new byte[8];
            Buffer.BlockCopy(BitConverter.GetBytes(lower), 0, b, 0, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(upper), 0, b, 4, 4);
            return b;
        }

        // Writes a minimal valid KPKA v4 PAK (stored entries) for fixtures and tests.
        internal static void WriteFixture(string path, IList<KeyValuePair<string, byte[]>> files, uint fingerprint, ushort features)
        {
            int n = files.Count; long data = PakIndex.HeaderSize + (long)n * PakIndex.EntrySize;
            using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
            using (var w = new BinaryWriter(fs))
            {
                w.Write(new byte[] { (byte)'K', (byte)'P', (byte)'K', (byte)'A', 4, 0 }); w.Write(features); w.Write((uint)n); w.Write(fingerprint);
                long offset = data;
                foreach (var f in files)
                {
                    ulong h = f.Key.StartsWith("#hash:") ? Convert.ToUInt64(f.Key.Substring(6), 16) : PakHash.PathHash(f.Key);
                    w.Write(PakHash.Lower(h)); w.Write(PakHash.Upper(h)); w.Write(offset); w.Write((long)f.Value.Length); w.Write((long)f.Value.Length); w.Write(0L); w.Write(0L);
                    offset += f.Value.Length;
                }
                foreach (var f in files) w.Write(f.Value);
            }
        }
    }
}
