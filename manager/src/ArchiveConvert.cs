using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SlotsInstaller
{
    // 1.0.20 (the user: RAR mods open slowly; "a button that offers to convert all RAR mods to zip ... once all have
    // converted, offer the user to delete the original RARs"): Settings → General → Convert RAR to ZIP.
    // Each RAR/7Z mod of the libraries gets a ZIP of the same name beside it, with the same entry paths:
    //   - 7-Zip lists the archive, then extracts all of it to its output (x -so) in listing order, and each file's bytes
    //     go straight into the ZIP entry (no temporary folder, so no long-path or scratch-space trouble). The CRC of
    //     every file is compared with the one 7-Zip listed (RAR5 archives with BLAKE2 instead are checked by 7-Zip
    //     itself, which fails the run on a mismatch).
    //   - The new ZIP is read back before it counts: every entry's size and CRC (central directory and the
    //     decompressed bytes) must equal the archive's, and the Manager's own reader must accept it.
    //   - The ZIP is written as a hidden ".slm-convert-*.zip" in the same folder and renamed only when verified; it
    //     never replaces a file. It takes the archive's created and modified times (the Added sort reads them).
    //   - A RAR/7Z option layout (BundleStore, Data\bundle-layouts) moves into the ZIP's comment, as Reorder bundle
    //     writes it for a ZIP; otherwise the comment stays empty (the RAR's own comment is not carried).
    //   - Then every record naming the archive names the ZIP (ConvertRecords). If that fails, the records are put
    //     back and the ZIP is removed, so a mod is either fully converted or left as it was.
    // The originals are never touched here; the window offers to move them to the Recycle Bin at the end.
    internal static class ArchiveConvert
    {
        internal const CompressionLevel Level = CompressionLevel.Optimal;
        internal const string TempPrefix = ".slm-convert-";
        // A password for 7-Zip to try, so an archive with encrypted names fails at once ("Wrong password?") instead of
        // asking on the closed input. An archive without a password ignores it.
        private const string NoPassword = "-pslm-no-password";
        internal static Func<string, long> FreeSpaceOverride;   // checks: free bytes of a folder's drive

        internal static bool Convertible(string path)
        {
            string ext = Path.GetExtension(path ?? "");
            return String.Equals(ext, ".rar", StringComparison.OrdinalIgnoreCase) || String.Equals(ext, ".7z", StringComparison.OrdinalIgnoreCase);
        }
        internal static string TargetFor(string source) { return Path.ChangeExtension(Path.GetFullPath(source), ".zip"); }

        // ---- What would be converted ------------------------------------------------------------------------------
        internal sealed class Candidate
        {
            internal string Source, Target, Name, Skip;   // Skip: why it is left out (null = it converts)
            internal long Size;
        }

        // The RAR/7Z sources of the listed mods, each once, with the ones left out and why. `skip` gives a reason for a
        // source the caller keeps out (the external mod manager has it installed), or null.
        internal static List<Candidate> Survey(IEnumerable<string> sources, Func<string, string> skip)
        {
            var result = new List<Candidate>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string s in sources ?? new string[0])
            {
                if (String.IsNullOrWhiteSpace(s) || !Convertible(s)) continue;
                string full;
                try { full = Path.GetFullPath(s); } catch (Exception ex) { if (ex is OutOfMemoryException) throw; continue; }
                if (!seen.Add(full)) continue;
                var c = new Candidate { Source = full, Target = TargetFor(full), Name = Path.GetFileName(full) };
                try
                {
                    var info = new FileInfo(full);
                    if (!info.Exists) { c.Skip = "no longer on disk"; result.Add(c); continue; }
                    c.Size = info.Length;
                    string reason = skip == null ? null : skip(full);
                    if (reason != null) c.Skip = reason;
                    else if (File.Exists(c.Target) || Directory.Exists(c.Target)) c.Skip = "a ZIP of the same name is already there";
                    else if (LongPaths.TooLong(new[] { c.Target }) != null) c.Skip = "the ZIP's path would be too long for Windows";
                }
                catch (Exception ex) { if (ex is OutOfMemoryException) throw; c.Skip = ex.Message; }
                result.Add(c);
            }
            return result.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        internal static long FreeSpace(string folder)
        {
            if (FreeSpaceOverride != null) return FreeSpaceOverride(folder);
            try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder))).AvailableFreeSpace; }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; return -1; }
        }

        // ---- One archive ------------------------------------------------------------------------------------------
        internal sealed class Result
        {
            internal string Source, Target, Error;
            internal bool Done;
            internal long SourceBytes, ZipBytes;
            internal int Files, Records;
            internal bool LayoutMoved;
        }

        internal sealed class Item
        {
            internal string Path, Original;   // Path: the ZIP entry name (forward slashes); Original: 7-Zip's
            internal long Size;
            internal uint? Crc;
            internal DateTime? Modified;
            internal bool Folder;
        }

        // Converts one archive and moves its records. progress(done, total) counts the uncompressed bytes written.
        // Never throws for a bad archive: Result.Error says why it was left as it was.
        internal static Result Convert(string source, string sevenZipPath, ConvertRecords.Context records, Action<long, long> progress)
        {
            var r = new Result { Source = Path.GetFullPath(source), Target = TargetFor(source) };
            string temp = null; bool published = false;
            try
            {
                var info = new FileInfo(r.Source);
                if (!info.Exists) throw new IOException("it is no longer on disk.");
                r.SourceBytes = info.Length;
                string stampBefore = SourceReader.Stamp(r.Source);
                DateTime created = info.CreationTimeUtc, written = info.LastWriteTimeUtc;
                if (File.Exists(r.Target) || Directory.Exists(r.Target)) throw new IOException(Path.GetFileName(r.Target) + " is already there.");
                if (LongPaths.TooLong(new[] { r.Target }) != null) throw new IOException("the ZIP's path would be too long for Windows. Turn on long paths, or rename the mod first.");
                string exe = SevenZipTool.ResolveOrFetch(sevenZipPath);
                if (String.IsNullOrEmpty(exe) || !File.Exists(exe)) throw new IOException("RAR and 7Z archives need 7-Zip, and none was found.");

                List<Item> items = List(exe, r.Source);
                long total = items.Where(i => !i.Folder).Sum(i => i.Size);
                string folder = Path.GetDirectoryName(r.Target);
                long free = FreeSpace(folder);
                if (free >= 0 && free < Math.Min(total, r.SourceBytes * 2) + 64L * 1024 * 1024) throw new IOException(NoSpace(folder));

                temp = Path.Combine(folder, TempPrefix + Guid.NewGuid().ToString("N").Substring(0, 8) + ".zip");
                Dictionary<string, uint> crcs = Write(exe, r.Source, temp, items, total, progress);
                r.Files = crcs.Count;
                r.LayoutMoved = CarryLayout(r.Source, temp);
                Verify(temp, items, crcs);
                if (SourceReader.Stamp(r.Source) != stampBefore) throw new IOException("it changed while it was converted. Try again.");

                File.SetCreationTimeUtc(temp, created); File.SetLastWriteTimeUtc(temp, written);
                File.SetAttributes(temp, File.GetAttributes(temp) & ~FileAttributes.Hidden);
                File.Move(temp, r.Target);   // never replaces a file
                published = true; temp = null;
                File.SetCreationTimeUtc(r.Target, created); File.SetLastWriteTimeUtc(r.Target, written);
                r.ZipBytes = new FileInfo(r.Target).Length;

                r.Records = ConvertRecords.Move(records, r.Source, r.Target);   // rolls back and throws on failure
                r.Done = true;
            }
            catch (Exception ex)
            {
                if (ex is OutOfMemoryException) throw;
                r.Error = Reason(ex);
                if (published) { try { File.Delete(r.Target); } catch (Exception again) { if (again is OutOfMemoryException) throw; Program.Log(again); r.Error += " The new ZIP couldn't be removed: " + r.Target; } }
                if (!(ex is IOException) && !(ex is UnauthorizedAccessException) && !(ex is InvalidDataException)) Program.Log(ex);
            }
            finally
            {
                if (temp != null) try { if (File.Exists(temp)) { File.SetAttributes(temp, FileAttributes.Normal); File.Delete(temp); } } catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); }
            }
            return r;
        }

        private static string NoSpace(string folder)
        {
            string root = folder; try { root = Path.GetPathRoot(Path.GetFullPath(folder)); } catch (Exception ex) { if (ex is OutOfMemoryException) throw; }
            return "there isn't enough free space on " + root.TrimEnd('\\') + ".";
        }

        // A short reason for the summary, without the "7-Zip failed (2):" noise where the cause is known.
        private static string Reason(Exception ex)
        {
            string m = (ex.Message ?? "").Trim();
            int hr = ex.HResult & 0xFFFF;
            if (ex is IOException && (hr == 0x70 || hr == 0x27)) return "there isn't enough free space on its drive.";
            if (m.IndexOf("password", StringComparison.OrdinalIgnoreCase) >= 0 || m.IndexOf("encrypted", StringComparison.OrdinalIgnoreCase) >= 0) return "it is password-protected.";
            if (ex is PathTooLongException) return "a path is too long for Windows.";
            if (m.StartsWith("7-Zip failed", StringComparison.Ordinal) || m.IndexOf("Data Error", StringComparison.OrdinalIgnoreCase) >= 0 || m.IndexOf("CRC Failed", StringComparison.OrdinalIgnoreCase) >= 0 || m.IndexOf("Can not open", StringComparison.OrdinalIgnoreCase) >= 0 || m.IndexOf("Unexpected end", StringComparison.OrdinalIgnoreCase) >= 0)
                return "it is damaged or incomplete (" + Tidy(m) + ").";
            if (m.Length > 0 && Char.IsUpper(m[0]) && !m.StartsWith("ZIP", StringComparison.Ordinal) && !m.StartsWith("RAR", StringComparison.Ordinal)) m = Char.ToLowerInvariant(m[0]) + m.Substring(1);
            return m.EndsWith(".") ? m : m + ".";
        }
        private static string Tidy(string m)
        {
            string line = m.Replace("\r", "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).LastOrDefault() ?? m;
            return line.Length > 160 ? line.Substring(0, 160) + "…" : line.TrimEnd('.');
        }

        // ---- Listing ---------------------------------------------------------------------------------------------
        internal static List<Item> List(string exe, string source)
        {
            string output = ProcessUtil.RunText(exe, "l -slt -ba -sccUTF-8 " + NoPassword + " -- " + ProcessUtil.Quote(source));
            var items = new List<Item>();
            var block = new Dictionary<string, string>();
            foreach (string line in (output + "\n\n").Replace("\r", "").Split('\n'))
            {
                if (String.IsNullOrWhiteSpace(line)) { if (block.Count > 0) items.Add(Parse(block)); block.Clear(); }
                else { int eq = line.IndexOf(" = ", StringComparison.Ordinal); if (eq >= 0) block[line.Substring(0, eq)] = line.Substring(eq + 3); else if (line.EndsWith(" =")) block[line.Substring(0, line.Length - 2)] = ""; }
            }
            items.RemoveAll(i => i == null);
            if (!items.Any(i => !i.Folder)) throw new IOException("it has no files.");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Item i in items) if (!names.Add(i.Path + (i.Folder ? "/" : ""))) throw new IOException("it lists " + i.Path + " twice (names differ only in case).");
            // Folders are kept only when empty: a file's path already makes its folders.
            var parents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Item i in items.Where(i => !i.Folder)) for (int s = i.Path.LastIndexOf('/'); s > 0; s = i.Path.LastIndexOf('/', s - 1)) parents.Add(i.Path.Substring(0, s));
            items.RemoveAll(i => i.Folder && parents.Contains(i.Path));
            return items;
        }
        private static Item Parse(Dictionary<string, string> b)
        {
            string p, v;
            if (!b.TryGetValue("Path", out p)) return null;
            if (b.TryGetValue("Encrypted", out v) && v == "+") throw new IOException("it is password-protected.");
            if ((b.TryGetValue("Symbolic Link", out v) && v.Length > 0) || (b.TryGetValue("Hard Link", out v) && v.Length > 0) || (b.TryGetValue("Attributes", out v) && v.StartsWith("l"))) throw new IOException("it holds a link (" + p + "), which ZIP mods can't keep.");
            if (b.TryGetValue("Alternate Stream", out v) && v == "+") throw new IOException("it holds an alternate data stream (" + p + ").");
            var item = new Item { Original = p, Path = Safety.ArchivePath(p) };
            item.Folder = (b.TryGetValue("Folder", out v) && v == "+") || (b.TryGetValue("Attributes", out v) && v.StartsWith("D"));
            if (!item.Folder)
            {
                long size; if (!b.TryGetValue("Size", out v) || !Int64.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out size) || size < 0) throw new IOException("7-Zip gave no size for " + p + ".");
                item.Size = size;
                uint crc; if (b.TryGetValue("CRC", out v) && UInt32.TryParse(v.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out crc)) item.Crc = crc;
            }
            if (b.TryGetValue("Modified", out v)) item.Modified = Time(v);
            return item;
        }
        // "2026-02-03 13:44:17.5689161" (local time), or null.
        private static DateTime? Time(string text)
        {
            string t = (text ?? "").Trim(); int dot = t.IndexOf('.'); if (dot > 0) t = t.Substring(0, dot);
            DateTime d;
            if (!DateTime.TryParseExact(t, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out d)) return null;
            return d.Year < 1980 || d.Year > 2107 ? (DateTime?)null : d;
        }

        // ---- Writing ---------------------------------------------------------------------------------------------
        // 7-Zip writes every file of the archive to its output, in listing order; each file's bytes become its entry.
        // Returns entry path -> CRC-32 of the bytes written.
        private static Dictionary<string, uint> Write(string exe, string source, string temp, List<Item> items, long total, Action<long, long> progress)
        {
            var crcs = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
            long done = 0; var tick = Stopwatch.StartNew();
            using (var p = new Process())
            {
                p.StartInfo = new ProcessStartInfo(exe, "x -so -y -spd -bsp0 -bse2 -sccUTF-8 " + NoPassword + " -- " + ProcessUtil.Quote(source)) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, StandardErrorEncoding = Encoding.UTF8 };
                p.Start(); p.StandardInput.Close();   // a password prompt can't wait for invisible input
                Task<string> error = Task.Run(delegate { var sb = new StringBuilder(); char[] buffer = new char[4096]; int n; while ((n = p.StandardError.Read(buffer, 0, buffer.Length)) > 0) if (sb.Length < 1024 * 1024) sb.Append(buffer, 0, n); return sb.ToString(); });
                bool finished = false;
                try
                {
                    Stream input = p.StandardOutput.BaseStream;
                    byte[] buffer = new byte[1 << 20];
                    using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16))
                    {
                        File.SetAttributes(temp, FileAttributes.Hidden);
                        using (var zip = new ZipArchive(file, ZipArchiveMode.Create, true))
                        {
                            foreach (Item item in items)
                            {
                                if (item.Folder) { ZipArchiveEntry dir = zip.CreateEntry(item.Path + "/"); if (item.Modified.HasValue) dir.LastWriteTime = item.Modified.Value; continue; }
                                ZipArchiveEntry entry = zip.CreateEntry(item.Path, Level);
                                if (item.Modified.HasValue) entry.LastWriteTime = item.Modified.Value;
                                var crc = new ZipCrc32(); long left = item.Size;
                                using (Stream output = entry.Open())
                                    while (left > 0)
                                    {
                                        int n = input.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
                                        if (n <= 0) throw new IOException("7-Zip stopped before the end of " + item.Original + Detail(p, error) + ".");
                                        crc.Update(buffer, n); output.Write(buffer, 0, n);
                                        left -= n; done += n;
                                        if (progress != null && tick.ElapsedMilliseconds >= 100) { progress(done, total); tick.Restart(); }
                                    }
                                if (item.Crc.HasValue && crc.Value != item.Crc.Value) throw new IOException("the bytes of " + item.Original + " don't match its CRC (the archive may be damaged, or 7-Zip gave its files in another order).");
                                crcs[item.Path] = crc.Value;
                            }
                            if (input.Read(buffer, 0, 1) > 0) throw new IOException("7-Zip gave more bytes than the archive lists.");
                        }
                        file.Flush(true);
                    }
                    p.WaitForExit();
                    error.Wait();
                    if (p.ExitCode != 0) throw new IOException("7-Zip failed (" + p.ExitCode + "): " + error.Result.Trim());
                    finished = true;
                }
                finally
                {
                    if (!finished)
                    {
                        try { if (!p.HasExited) p.Kill(); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
                        try { p.WaitForExit(5000); } catch (InvalidOperationException) { }
                    }
                }
            }
            if (progress != null) progress(done, total);
            return crcs;
        }
        // 7-Zip's error text when it has ended (password, damage), else nothing.
        private static string Detail(Process p, Task<string> error)
        {
            try { if (p.WaitForExit(3000) && error.Wait(1000)) { string e = error.Result.Trim(); if (e.Length > 0) return ": " + Tidy(e); } }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; }
            return "";
        }

        // The RAR/7Z's option layout (Data\bundle-layouts) as the ZIP's comment, as Reorder bundle saves it for a ZIP.
        // A stored layout that no longer fits the archive is not carried (the Manager ignores it for the RAR too).
        private static bool CarryLayout(string source, string temp)
        {
            bool damaged; string json = BundleStore.Find(source, out damaged);
            if (json == null) return false;
            List<BundleOption> options;
            try { options = Json.Serializer().Deserialize<List<BundleOption>>(json); BundleLayout.ValidateDraft(temp, options); }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.LogLine(source + ": its option layout was not carried into the ZIP (" + ex.Message + ")"); return false; }
            byte[] comment = BundleLayout.CommentFor(options);
            using (var file = new FileStream(temp, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                if (ZipIntegrity.EndComment(file).Length != 0) throw new IOException("the new ZIP already has a comment.");
                file.Position = file.Length - 2;
                file.WriteByte((byte)comment.Length); file.WriteByte((byte)(comment.Length >> 8)); file.Write(comment, 0, comment.Length);
                file.Flush(true);
            }
            return true;
        }

        // ---- Verifying -------------------------------------------------------------------------------------------
        // The ZIP holds exactly the archive's files: names, sizes, CRCs in its central directory, and decompressed
        // bytes with that CRC. The Manager's reader must accept it too (paths, links, duplicates).
        private static void Verify(string temp, List<Item> items, Dictionary<string, uint> crcs)
        {
            var files = items.Where(i => !i.Folder).ToList();
            using (var reader = new SourceReader(temp, ""))
            {
                if (reader.Entries.Count != files.Count) throw new IOException("the new ZIP has " + reader.Entries.Count + " files instead of " + files.Count + ".");
                foreach (Item i in files)
                {
                    SourceEntry e;
                    if (!reader.Entries.TryGetValue(i.Path, out e) || e.Size != i.Size) throw new IOException("the new ZIP is missing " + i.Original + ".");
                    if (!e.ZipChecksum.HasValue || e.ZipChecksum.Value != crcs[i.Path]) throw new IOException("the new ZIP's CRC of " + i.Original + " is wrong.");
                }
            }
            using (ZipArchive zip = ZipFile.OpenRead(temp))
            {
                byte[] buffer = new byte[1 << 20];
                var byName = zip.Entries.Where(e => !e.FullName.EndsWith("/")).ToDictionary(e => e.FullName, StringComparer.OrdinalIgnoreCase);
                foreach (Item i in files)
                {
                    ZipArchiveEntry entry = byName[i.Path];
                    var crc = new ZipCrc32(); long length = 0; int n;
                    using (Stream s = entry.Open()) while ((n = s.Read(buffer, 0, buffer.Length)) > 0) { crc.Update(buffer, n); length += n; }
                    if (length != i.Size || crc.Value != crcs[i.Path] || (i.Crc.HasValue && crc.Value != i.Crc.Value)) throw new IOException("reading " + i.Original + " back from the new ZIP gave other bytes.");
                }
            }
        }
    }
}
