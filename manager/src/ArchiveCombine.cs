using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace SlotsInstaller
{
    // 1.0.18 (the user: "if we are displaying the addons in single view, shouldn't we support install all variants to
    // include them too"): Slotsloader builds the variants of one archive, and an addon archive dropped beside its main
    // mod is a separate mod without the main files. So Install all variants writes one ZIP: the main ZIP's bytes as they
    // are up to its central directory (nothing recompressed), then each addon file (read and checked from its own ZIP,
    // RAR, 7Z or folder like any install, then deflated), then the main's directory records, the new ones and a new end
    // record. A ZIP64 main, more than 65,535 entries or a result of 4 GB are refused with a message.
    // ZIP layout: PKWARE APPNOTE 4.3.7 (local header), 4.3.12 (central directory), 4.3.16 (end record).
    internal static class ArchiveCombine
    {
        internal sealed class Addon
        {
            internal string SourcePath;
            internal List<KeyValuePair<string, string>> Files = new List<KeyValuePair<string, string>>();   // entry in the addon → path in the copy
        }

        internal static bool IsZip(string path) { return !String.IsNullOrEmpty(path) && Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase); }

        private sealed class Tail { internal long Length, CentralOffset, CentralSize; internal int Count; internal byte[] Comment; }

        // Writes `output` (must not exist) from `mainZip` and the addons' files. On any failure `output` is removed.
        internal static void Write(string mainZip, IList<Addon> addons, string output, string sevenZip)
        {
            if (!IsZip(mainZip)) throw new IOException("Add-ons can only be combined with a ZIP mod.");
            string mainName = Path.GetFileName(mainZip);
            Tail tail = ReadTail(mainZip);
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var main = new SourceReader(mainZip, sevenZip)) foreach (string path in main.Entries.Keys) taken.Add(path);
            var added = new List<string>();
            foreach (Addon addon in addons)
                foreach (var file in addon.Files)
                {
                    string target = Safety.ArchivePath(file.Value);
                    if (!taken.Add(target)) throw new IOException(Path.GetFileName(addon.SourcePath) + " and " + mainName + " both have " + target + ", so they can't be combined. Install them separately.");
                    added.Add(target);
                }
            if ((long)tail.Count + added.Count > 0xFFFF) throw TooLarge(mainName);
            string tempRoot = Path.GetFullPath(Path.GetTempPath());
            Safety.NoReparseChain(tempRoot);
            string scratch = Safety.NewFolder(tempRoot, "slm-combine-");
            bool done = false;
            try
            {
                using (var zip = new FileStream(output, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 20))
                {
                    var central = new MemoryStream();
                    byte[] mainCentral;
                    using (var input = new FileStream(mainZip, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
                    {
                        if (input.Length != tail.Length) throw new IOException(mainName + " changed while it was read. Try again.");
                        OperationContext.Report("Copying " + mainName, 0, tail.CentralOffset);
                        CopyRange(input, zip, 0, tail.CentralOffset);
                        mainCentral = new byte[tail.CentralSize];
                        input.Position = tail.CentralOffset;
                        ReadExactly(input, mainCentral);
                    }
                    DateTime now = DateTime.Now;
                    for (int n = 0; n < addons.Count; n++)
                    {
                        Addon addon = addons[n];
                        if (addon.Files.Count == 0) continue;
                        using (var reader = new SourceReader(addon.SourcePath, sevenZip))
                        {
                            string target = Path.Combine(scratch, "a" + n), work = Path.Combine(scratch, "w" + n);
                            Directory.CreateDirectory(target); Directory.CreateDirectory(work);
                            var planned = new List<PlannedFile>();
                            foreach (var file in addon.Files)
                            {
                                SourceEntry entry;
                                if (!reader.Entries.TryGetValue(file.Key, out entry)) throw new IOException(Path.GetFileName(addon.SourcePath) + " changed: " + file.Key + " is gone. Try again.");
                                planned.Add(new PlannedFile { Asset = new NativeAsset { EntryPath = entry.Path, DestinationPath = entry.Path, Size = entry.Size } });
                            }
                            reader.CopyFiles(planned, target, work);
                            for (int i = 0; i < addon.Files.Count; i++)
                            {
                                OperationContext.ThrowIfCancellationRequested();
                                OperationContext.Report("Adding " + Path.GetFileName(addon.SourcePath), i, addon.Files.Count);
                                string extracted = Safety.Under(target, planned[i].Asset.EntryPath);
                                Safety.NoReparseChain(extracted);
                                AppendEntry(zip, central, extracted, Safety.ArchivePath(addon.Files[i].Value), now, mainName);
                            }
                        }
                    }
                    long centralOffset = zip.Position;
                    zip.Write(mainCentral, 0, mainCentral.Length);
                    central.Position = 0; central.CopyTo(zip);
                    long centralSize = zip.Position - centralOffset;
                    if (centralOffset >= UInt32.MaxValue || centralSize >= UInt32.MaxValue || zip.Position + 22 + tail.Comment.Length >= UInt32.MaxValue) throw TooLarge(mainName);
                    using (var w = new BinaryWriter(zip, Encoding.UTF8, true))
                    {
                        int count = tail.Count + added.Count;
                        w.Write(0x06054b50u); w.Write((ushort)0); w.Write((ushort)0);
                        w.Write((ushort)count); w.Write((ushort)count);
                        w.Write((uint)centralSize); w.Write((uint)centralOffset);
                        w.Write((ushort)tail.Comment.Length); w.Write(tail.Comment);
                    }
                    zip.Flush(true);
                }
                Verify(output, tail.Count + added.Count, added, sevenZip);
                done = true;
            }
            finally
            {
                try { Safety.DeleteGenerated(scratch, tempRoot); } catch (Exception ex) { if (ex is OutOfMemoryException) throw; }
                if (!done) try { if (File.Exists(output)) File.Delete(output); } catch (Exception ex) { if (ex is OutOfMemoryException) throw; }
            }
        }

        // The written copy reads back: ZipArchive and the checksum reader agree on the count, no duplicate names, and each
        // added file decompresses to its size with its checksum. The main's entries were copied byte for byte.
        private static void Verify(string output, int count, List<string> added, string sevenZip)
        {
            OperationContext.Report("Checking the combined archive", 0, 1);
            using (ZipArchive archive = ZipFile.OpenRead(output))
            {
                byte[] comment;
                if (archive.Entries.Count != count || ZipIntegrity.ReadChecksums(output, archive, out comment).Length != count) throw new IOException("The combined archive did not read back correctly.");
            }
            using (var reader = new SourceReader(output, sevenZip))
                reader.VerifyFiles(added.Select(p => new PlannedFile { Asset = new NativeAsset { EntryPath = p, DestinationPath = p, Size = reader.Entries[p].Size } }));
        }

        private static Tail ReadTail(string path)
        {
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var reader = new BinaryReader(stream))
                {
                    var tail = new Tail { Length = stream.Length, Comment = ZipIntegrity.EndComment(stream) };
                    long end = stream.Length - tail.Comment.Length - 22;
                    stream.Position = end;
                    if (reader.ReadUInt32() != 0x06054b50) throw Invalid(path);
                    ushort disk = reader.ReadUInt16(), centralDisk = reader.ReadUInt16(), onDisk = reader.ReadUInt16(), total = reader.ReadUInt16();
                    uint size = reader.ReadUInt32(), offset = reader.ReadUInt32();
                    if (disk != 0 || centralDisk != 0 || onDisk != total) throw new IOException(Path.GetFileName(path) + " is a multi-part ZIP, which can't be combined with add-ons.");
                    bool zip64 = total == 0xFFFF || size == UInt32.MaxValue || offset == UInt32.MaxValue;
                    if (!zip64 && end >= 20) { stream.Position = end - 20; zip64 = reader.ReadUInt32() == 0x07064b50; }
                    if (zip64) throw TooLarge(Path.GetFileName(path));
                    if ((long)offset + size > end) throw Invalid(path);
                    tail.Count = total; tail.CentralOffset = offset; tail.CentralSize = size;
                    return tail;
                }
            }
            catch (EndOfStreamException ex) { throw new IOException(Path.GetFileName(path) + " is incomplete or corrupt. Download or recreate it.", ex); }
        }

        // One deflated entry (stored when empty) with a UTF-8 name; its directory record goes to `central`.
        private static void AppendEntry(FileStream zip, Stream central, string file, string name, DateTime when, string mainName)
        {
            long offset = zip.Position;
            if (offset >= UInt32.MaxValue) throw TooLarge(mainName);
            byte[] nameBytes = Encoding.UTF8.GetBytes(name);
            if (nameBytes.Length > 0xFFFF) throw new IOException("A file name is too long for a ZIP: " + name);
            long length = new FileInfo(file).Length;
            ushort method = (ushort)(length == 0 ? 0 : 8);
            ushort time = (ushort)((when.Hour << 11) | (when.Minute << 5) | (when.Second / 2));
            ushort date = (ushort)(((Math.Max(1980, when.Year) - 1980) << 9) | (when.Month << 5) | when.Day);
            var w = new BinaryWriter(zip, Encoding.UTF8, true);
            w.Write(0x04034b50u); w.Write((ushort)20); w.Write((ushort)0x0800); w.Write(method); w.Write(time); w.Write(date);
            long sizes = zip.Position;
            w.Write(0u); w.Write(0u); w.Write(0u);
            w.Write((ushort)nameBytes.Length); w.Write((ushort)0); w.Write(nameBytes); w.Flush();
            long start = zip.Position, read = 0;
            var crc = new ZipCrc32();
            if (length > 0)
                using (var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
                using (var deflate = new DeflateStream(zip, CompressionLevel.Optimal, true))
                {
                    byte[] buffer = new byte[1 << 20]; int count;
                    while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        OperationContext.ThrowIfCancellationRequested();
                        crc.Update(buffer, count); deflate.Write(buffer, 0, count); read += count;
                    }
                }
            if (read != length) throw new IOException("A file changed while it was added: " + name);
            long end = zip.Position, packed = end - start;
            if (length >= UInt32.MaxValue || packed >= UInt32.MaxValue || end >= UInt32.MaxValue) throw TooLarge(mainName);
            uint checksum = length == 0 ? 0u : crc.Value;
            zip.Position = sizes; w.Write(checksum); w.Write((uint)packed); w.Write((uint)length); w.Flush(); zip.Position = end;
            var c = new BinaryWriter(central, Encoding.UTF8, true);
            c.Write(0x02014b50u); c.Write((ushort)20); c.Write((ushort)20); c.Write((ushort)0x0800); c.Write(method); c.Write(time); c.Write(date);
            c.Write(checksum); c.Write((uint)packed); c.Write((uint)length);
            c.Write((ushort)nameBytes.Length); c.Write((ushort)0); c.Write((ushort)0); c.Write((ushort)0); c.Write((ushort)0); c.Write(0u); c.Write((uint)offset);
            c.Write(nameBytes); c.Flush();
        }

        private static void CopyRange(Stream input, Stream output, long start, long count)
        {
            input.Position = start;
            byte[] buffer = new byte[1 << 20]; long left = count, copied = 0;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (left > 0)
            {
                OperationContext.ThrowIfCancellationRequested();
                int n = input.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
                if (n <= 0) throw new EndOfStreamException();
                output.Write(buffer, 0, n); left -= n; copied += n;
                if (watch.ElapsedMilliseconds >= 100) { OperationContext.Report("Copying the main archive", copied, count); watch.Restart(); }
            }
        }
        private static void ReadExactly(Stream input, byte[] buffer)
        {
            int at = 0;
            while (at < buffer.Length) { int n = input.Read(buffer, at, buffer.Length - at); if (n <= 0) throw new EndOfStreamException(); at += n; }
        }
        private static IOException TooLarge(string name) { return new IOException(name + " is too large to combine with its add-ons (4 GB or 65,535 files). Install them separately."); }
        private static IOException Invalid(string path) { return new IOException(Path.GetFileName(path) + " is invalid or corrupt. Download or recreate it."); }
    }
}
