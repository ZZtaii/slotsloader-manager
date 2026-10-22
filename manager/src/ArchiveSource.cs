using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Threading;
using System.Web.Script.Serialization;

namespace SlotsInstaller
{
    internal sealed class SourceEntry
    {
        internal string Path, Original; internal long Size; internal uint? ZipChecksum;
        internal uint? ListedCrc;      // 2.4.2: 7-Zip's listed CRC (RAR/7Z): a file left by a cancelled extraction is kept only when it matches
        internal long WriteTicks;      // 2.4.2: folder sources: the file's write time (check cache key)
    }
    internal sealed class SourceReader : IDisposable
    {
        internal readonly string Source, SevenZip;
        internal readonly Dictionary<string,SourceEntry> Entries = new Dictionary<string,SourceEntry>(StringComparer.OrdinalIgnoreCase);
        internal string ListingStamp;
        private ZipArchive zip; private bool folder;
        internal bool UsesSevenZip { get { return !folder && zip == null; } }
        internal static string Stamp(string source) { var f = new FileInfo(source); return f.Length + ":" + f.LastWriteTimeUtc.Ticks; }
        internal SourceReader(string source, string sevenZip) : this(source,sevenZip,null,null) { }
        internal SourceReader(string source, string sevenZip, Dictionary<string,SourceEntry> knownEntries, string stamp)
        {
            Source = Path.GetFullPath(source); folder = Directory.Exists(Source);
            SevenZip = folder || Path.GetExtension(Source).Equals(".zip",StringComparison.OrdinalIgnoreCase) ? sevenZip : SevenZipTool.ResolveOrFetch(sevenZip);   // 1.0.11: fetched when none is usable
            try {
                OperationContext.ThrowIfCancellationRequested();
                if (folder) { Safety.NoReparse(Source); EnumerateFolder(Source); }
                else if (Path.GetExtension(Source).Equals(".zip",StringComparison.OrdinalIgnoreCase))
                {
                    zip = ZipFile.OpenRead(Source);
                    uint[] checksums = ZipIntegrity.ReadChecksums(Source,zip);
                    for (int index = 0; index < zip.Entries.Count; index++)
                    {
                        OperationContext.ThrowIfCancellationRequested();
                        ZipArchiveEntry e = zip.Entries[index];
                        string n = Safety.ArchivePath(e.FullName);
                        int mode = (e.ExternalAttributes >> 16) & 0xF000;
                        if (mode == 0xA000 || (e.ExternalAttributes & 0x400) != 0) throw new IOException("Archive links are unsupported: " + e.FullName);
                        if (e.FullName.EndsWith("/") || e.FullName.EndsWith("\\")) continue;
                        Add(n,e.FullName,e.Length);
                        Entries[n].ZipChecksum = checksums[index];
                    }
                }
                else
                {
                    if (!File.Exists(SevenZip)) throw new IOException("RAR and 7Z archives need 7-Zip, and none is installed. " + (SevenZipTool.FetchFailure != null ? SevenZipTool.FetchFailure.TrimEnd('.') + " (the Manager tries again in a few minutes). " : "") + "Install 7-Zip or choose 7z.exe in Settings.");
                    ListingStamp = Stamp(Source);
                    if (knownEntries != null && stamp == ListingStamp)
                    {
                        foreach (var entry in knownEntries) Entries.Add(entry.Key,entry.Value);
                        return;
                    }
                    string output = ProcessUtil.RunText(SevenZip, "l -slt -ba -sccUTF-8 -- " + ProcessUtil.Quote(Source));
                    Dictionary<string,string> block = new Dictionary<string,string>();
                    foreach (string line in (output + "\n\n").Replace("\r", "").Split('\n'))
                    {
                        if (String.IsNullOrWhiteSpace(line)) { AddBlock(block); block.Clear(); }
                        else { int eq = line.IndexOf(" = ",StringComparison.Ordinal); if (eq >= 0) block[line.Substring(0,eq)] = line.Substring(eq+3); }
                    }
                }
                if (Entries.Count == 0) throw new IOException("This archive or folder is empty.");
            } catch { Dispose(); throw; }
        }
        private void AddBlock(Dictionary<string,string> b)
        {
            string p; if (!b.TryGetValue("Path",out p)) return;
            string normalized = Safety.ArchivePath(p);
            string v; if ((b.TryGetValue("Symbolic Link",out v) && v.Length > 0) || (b.TryGetValue("Hard Link",out v) && v.Length > 0) || (b.TryGetValue("Attributes",out v) && v.StartsWith("l"))) throw new IOException("Archive links are unsupported: " + p);
            if ((b.TryGetValue("Folder",out v) && v == "+") || (b.TryGetValue("Attributes",out v) && v.StartsWith("D"))) return;
            long size; if (!b.TryGetValue("Size",out v) || !Int64.TryParse(v,out size)) return;
            Add(normalized,p,size);
            uint crc; if (b.TryGetValue("CRC",out v) && UInt32.TryParse(v.Trim(),System.Globalization.NumberStyles.HexNumber,System.Globalization.CultureInfo.InvariantCulture,out crc)) Entries[normalized].ListedCrc = crc;
        }
        private void Add(string p, string original, long size)
        {
            if (size < 0) throw new IOException("Invalid source file size: " + p);
            if (Entries.ContainsKey(p)) throw new IOException("Duplicate archive path (case insensitive): " + p);
            Entries.Add(p,new SourceEntry { Path=p,Original=original,Size=size });
        }
        private void EnumerateFolder(string dir)
        {
            foreach (string p in Directory.EnumerateFileSystemEntries(dir))
            {
                OperationContext.ThrowIfCancellationRequested();
                Safety.NoReparse(p);
                if (Directory.Exists(p)) EnumerateFolder(p);
                else { string rel = Safety.ArchivePath(p.Substring(Source.TrimEnd(Path.DirectorySeparatorChar).Length+1).Replace('\\','/')); var info = new FileInfo(p); Add(rel,p,info.Length); Entries[rel].WriteTicks = info.LastWriteTimeUtc.Ticks; }
            }
        }
        internal byte[] ReadBytes(string entry, int limit)
        {
            SourceEntry e;
            if (!Entries.TryGetValue(entry,out e)) throw new IOException("Source entry disappeared: " + entry);
            if (e.Size > limit) throw new IOException("Metadata or preview is too large: " + entry);
            if (folder || zip != null)
                using (Stream input=OpenEntry(e)) using (var output=new MemoryStream())
                { Transfer(input,output,e,limit,false); return output.ToArray(); }
            byte[] bytes=ProcessUtil.RunBytes(SevenZip, "x -so -y -spd -bsp0 -bse2 -- " + ProcessUtil.Quote(Source) + " " + ProcessUtil.Quote(e.Original),(int)Math.Min(limit,e.Size));
            if (bytes.LongLength != e.Size) throw new IOException("Extracted file size mismatch: " + e.Path);
            return bytes;
        }
        internal Dictionary<string,byte[]> ReadManyBytes(IEnumerable<SourceEntry> entries, int limit)
        {
            List<SourceEntry> chosen = entries.GroupBy(e => e.Path,StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
            foreach (SourceEntry e in chosen) if (e.Size > limit) throw new IOException("Metadata or preview is too large: " + e.Path);
            var result = new Dictionary<string,byte[]>(StringComparer.OrdinalIgnoreCase);
            if (!UsesSevenZip || chosen.Count < 2 || chosen.Sum(e => e.Size) > 64L * 1024 * 1024)
            {
                foreach (SourceEntry e in chosen) result[e.Path] = ReadBytes(e.Path,limit);
                return result;
            }
            string tempRoot = Path.GetFullPath(Path.GetTempPath());
            Safety.NoReparseChain(tempRoot);
            string scratch = Safety.Under(tempRoot,"slotsloader-read-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(scratch);
            try
            {
                string raw = Path.Combine(scratch,"raw"); Directory.CreateDirectory(raw);
                string list = Path.Combine(scratch,"entries.txt");
                File.WriteAllLines(list,chosen.Select(e => e.Original),new UTF8Encoding(false));
                OperationContext.Report("Reading mod info and previews",0,chosen.Count);
                ProcessUtil.RunText(SevenZip,"x -y -spd -bso0 -bsp0 -scsUTF-8 " + ProcessUtil.Quote("-o" + raw) + " " + ProcessUtil.Quote("-i@" + list) + " -- " + ProcessUtil.Quote(Source));
                foreach (SourceEntry e in chosen)
                {
                    OperationContext.ThrowIfCancellationRequested();
                    string extracted = Safety.Under(raw,e.Path); Safety.NoReparseChain(extracted);
                    if (!File.Exists(extracted) || new FileInfo(extracted).Length != e.Size) throw new IOException("Extracted metadata or preview size mismatch: " + e.Path);
                    using (Stream input=File.OpenRead(extracted)) using (var output=new MemoryStream())
                    { Transfer(input,output,e,limit,false); result[e.Path]=output.ToArray(); }
                }
                return result;
            }
            finally { try { Safety.DeleteGenerated(scratch,tempRoot); } catch { } }
        }
        internal Dictionary<string,string> CopyFiles(IEnumerable<PlannedFile> files, string target, string scratch)
        {
            var hashes = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            CopyFilesEach(files,target,scratch,delegate(PlannedFile f, string sha) { hashes[f.Asset.DestinationPath]=sha; });
            return hashes;
        }
        // 2.4.2: `done` runs as soon as each file is complete at its destination, so a cancelled or failed copy keeps
        // what it finished (the check cache). With 7-Zip (one batch for speed and solid archives), when the batch
        // stops early and `keepPartial` is set, the files it had fully extracted are kept: each must have its listed
        // size and the CRC 7-Zip listed for it (files without a listed CRC are kept only from a finished batch).
        internal static Func<string,string,string> SevenZipRun;   // tests: a fake 7-Zip (exe, arguments → output)
        internal void CopyFilesEach(IEnumerable<PlannedFile> files, string target, string scratch, Action<PlannedFile,string> done) { CopyFilesEach(files,target,scratch,done,false); }
        internal void CopyFilesEach(IEnumerable<PlannedFile> files, string target, string scratch, Action<PlannedFile,string> done, bool keepPartial)
        {
            var chosen = files.ToList();
            if (!folder && zip == null)
            {
                string raw = Path.Combine(scratch,"raw"); Directory.CreateDirectory(raw);
                string list = Path.Combine(scratch,"entries.txt");
                File.WriteAllLines(list,chosen.Select(f=>Entries[f.Asset.EntryPath].Original),new UTF8Encoding(false));
                OperationContext.Report("Extracting selected files",0,chosen.Count);
                string args = "x -y -spd -bso0 -bsp0 -scsUTF-8 " + ProcessUtil.Quote("-o"+raw) + " " + ProcessUtil.Quote("-i@"+list) + " -- " + ProcessUtil.Quote(Source);
                try { if (SevenZipRun != null) SevenZipRun(SevenZip,args); else ProcessUtil.RunText(SevenZip,args); }
                catch (Exception ex)
                {
                    if (ex is OutOfMemoryException || !keepPartial) throw;
                    foreach (PlannedFile f in chosen) { try { KeepExtracted(raw,target,f,done); } catch (Exception keep) { if (keep is OutOfMemoryException) throw; } }
                    throw;
                }
                // 1.0.1 (speed): the extracted files are moved and hashed by up to 4 workers (7-Zip already decompressed
                // on its own threads); `done` runs under a lock; the first error or a cancel is rethrown as is.
                CancellationToken token = OperationContext.Token; Action<OperationProgress> progress = OperationContext.Progress;
                object gate = new object();
                try
                {
                    Parallel.ForEach(chosen, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Math.Min(4, Environment.ProcessorCount)), CancellationToken = token }, delegate(PlannedFile f)
                    {
                        using (OperationContext.Begin(token, progress))
                        {
                            OperationContext.ThrowIfCancellationRequested();
                            string src = Safety.Under(raw,f.Asset.EntryPath); Safety.NoReparseChain(src);
                            if (new FileInfo(src).Length != f.Asset.Size) throw new IOException("Extracted file size mismatch: " + f.Asset.EntryPath);
                            string dst = Safety.Under(target,f.Asset.DestinationPath);
                            lock (gate) Directory.CreateDirectory(Path.GetDirectoryName(dst));
                            File.Move(src,dst);
                            string sha; using (var input=File.OpenRead(dst)) sha=Transfer(input,Stream.Null,Entries[f.Asset.EntryPath],f.Asset.Size,true);
                            lock (gate) done(f,sha);
                        }
                    });
                }
                catch (AggregateException ex) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerExceptions[0]).Throw(); }
            }
            else if (zip != null && chosen.Count > 1 && Environment.ProcessorCount > 1) CopyZipParallel(chosen,target,done);
            else foreach (PlannedFile f in chosen)
            {
                OperationContext.ThrowIfCancellationRequested();
                SourceEntry e = Entries[f.Asset.EntryPath];
                string dst = Safety.Under(target,f.Asset.DestinationPath); Directory.CreateDirectory(Path.GetDirectoryName(dst));
                string sha;
                using (Stream output = File.Create(dst))
                {
                    using (Stream input=OpenEntry(e)) sha=Transfer(input,output,e,f.Asset.Size,true);
                }
                if (new FileInfo(dst).Length != f.Asset.Size) throw new IOException("Source changed during extraction: " + e.Path);
                done(f,sha);
            }
        }
        // 1.0.1 (speed): ZIP decompression is single-threaded (~200 MB/s), so a health check or install of a big mod
        // spent seconds here. Up to 4 workers each open their own read-only handle on the archive and take files
        // largest first; every file gets the same size/CRC/SHA-256 checks as the sequential path, `done` runs under a
        // lock as each file completes, and the first error (or a cancel) stops the others and is rethrown as is.
        private void CopyZipParallel(List<PlannedFile> chosen, string target, Action<PlannedFile,string> done)
        {
            CancellationToken token = OperationContext.Token; Action<OperationProgress> progress = OperationContext.Progress;
            var queue = new System.Collections.Concurrent.ConcurrentQueue<PlannedFile>(chosen.OrderByDescending(f => f.Asset.Size));
            int workers = Math.Min(Math.Min(4, Environment.ProcessorCount), chosen.Count), finished = 0;
            object gate = new object(); Exception failure = null;
            var threads = new List<Thread>();
            for (int w = 0; w < workers; w++)
            {
                var thread = new Thread(delegate()
                {
                    try
                    {
                        using (OperationContext.Begin(token, progress))
                        using (ZipArchive own = ZipFile.OpenRead(Source))
                        {
                            PlannedFile f;
                            while (Volatile.Read(ref failure) == null && queue.TryDequeue(out f))
                            {
                                OperationContext.ThrowIfCancellationRequested();
                                SourceEntry e = Entries[f.Asset.EntryPath];
                                string dst = Safety.Under(target,f.Asset.DestinationPath);
                                lock (gate) Directory.CreateDirectory(Path.GetDirectoryName(dst));
                                string sha;
                                using (Stream output = File.Create(dst))
                                using (Stream input = own.GetEntry(e.Original).Open()) sha = Transfer(input,output,e,f.Asset.Size,true);
                                if (new FileInfo(dst).Length != f.Asset.Size) throw new IOException("Source changed during extraction: " + e.Path);
                                int count;
                                lock (gate) { done(f,sha); count = ++finished; }
                                OperationContext.Report("Extracting selected files",count,chosen.Count);
                            }
                        }
                    }
                    catch (Exception ex) { lock (gate) if (failure == null) failure = ex; }
                }) { IsBackground = true, Name = "Extract " + w };
                threads.Add(thread); thread.Start();
            }
            foreach (Thread thread in threads) thread.Join();
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
        // A file a stopped 7-Zip batch left: kept only when complete (size and the listed CRC match). No cancellation
        // checks here: this runs because the batch was cancelled.
        private void KeepExtracted(string raw, string target, PlannedFile f, Action<PlannedFile,string> done)
        {
            SourceEntry e = Entries[f.Asset.EntryPath];
            if (!e.ListedCrc.HasValue) return;
            string src = Safety.Under(raw,f.Asset.EntryPath);
            if (!File.Exists(src)) return;
            Safety.NoReparseChain(src);
            if (new FileInfo(src).Length != f.Asset.Size) return;
            var crc = new ZipCrc32(); string sha;
            using (var sha256 = FastHash.Sha256())
            using (var input = new FileStream(src,FileMode.Open,FileAccess.Read,FileShare.Read))
            {
                byte[] buffer = new byte[81920]; int count;
                while ((count = input.Read(buffer,0,buffer.Length)) > 0) { crc.Update(buffer,count); sha256.TransformBlock(buffer,0,count,buffer,0); }
                sha256.TransformFinalBlock(new byte[0],0,0); sha = HashText(sha256.Hash);
            }
            if (crc.Value != e.ListedCrc.Value) return;
            string dst = Safety.Under(target,f.Asset.DestinationPath); Directory.CreateDirectory(Path.GetDirectoryName(dst)); File.Move(src,dst);
            done(f,sha);
        }
        // Used by read-only validation: no costume folder or application Data is written.
        internal Dictionary<string,string> VerifyFiles(IEnumerable<PlannedFile> files)
        {
            var chosen=files.ToList();
            var hashes=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            if (!UsesSevenZip)
            {
                foreach (PlannedFile file in chosen)
                {
                    OperationContext.ThrowIfCancellationRequested();
                    SourceEntry entry=Entries[file.Asset.EntryPath];
                    using (Stream input=OpenEntry(entry)) hashes[file.Asset.DestinationPath]=Transfer(input,Stream.Null,entry,file.Asset.Size,true);
                }
                return hashes;
            }
            if (chosen.Count == 0) return hashes;
            string tempRoot=Path.GetFullPath(Path.GetTempPath()); Safety.NoReparseChain(tempRoot);
            string scratch=Safety.Under(tempRoot,"slotsloader-verify-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(scratch);
            try
            {
                string raw=Path.Combine(scratch,"raw"); Directory.CreateDirectory(raw);
                string list=Path.Combine(scratch,"entries.txt");
                File.WriteAllLines(list,chosen.Select(file=>Entries[file.Asset.EntryPath].Original).Distinct(StringComparer.OrdinalIgnoreCase),new UTF8Encoding(false));
                OperationContext.Report("Verifying selected files",0,chosen.Count);
                ProcessUtil.RunText(SevenZip,"x -y -spd -bso0 -bsp0 -scsUTF-8 "+ProcessUtil.Quote("-o"+raw)+" "+ProcessUtil.Quote("-i@"+list)+" -- "+ProcessUtil.Quote(Source));
                foreach (PlannedFile file in chosen)
                {
                    OperationContext.ThrowIfCancellationRequested();
                    SourceEntry entry=Entries[file.Asset.EntryPath];
                    string extracted=Safety.Under(raw,entry.Path); Safety.NoReparseChain(extracted);
                    using (Stream input=File.OpenRead(extracted)) hashes[file.Asset.DestinationPath]=Transfer(input,Stream.Null,entry,file.Asset.Size,true);
                }
                return hashes;
            }
            finally { Safety.DeleteGenerated(scratch,tempRoot); }
        }
        private Stream OpenEntry(SourceEntry entry)
        {
            if (folder) { Safety.NoReparseChain(entry.Original); return File.OpenRead(entry.Original); }
            return zip.GetEntry(entry.Original).Open();
        }
        private static string HashText(byte[] hash) { return BitConverter.ToString(hash).Replace("-","").ToLowerInvariant(); }
        private static string Transfer(Stream input,Stream output,SourceEntry entry,long limit,bool hash)
        {
            OperationContext.ThrowIfCancellationRequested();
            if (entry.Size < 0 || entry.Size > limit) throw new IOException("Source file exceeds its allowed size: "+entry.Path);
            using (var sha=hash ? FastHash.Sha256() : null)
            {
                var crc=entry.ZipChecksum.HasValue ? new ZipCrc32() : null;
                byte[] buffer=new byte[1024*1024]; long total=0; int count;   // 1.0.1: 1 MB (was 80 KB): fewer passes per file
                var progress=Stopwatch.StartNew();
                try
                {
                    // Read at most one byte beyond the expected length to detect lying
                    // ZIP headers without expanding the rest into RAM or onto disk.
                    while (true)
                    {
                        OperationContext.ThrowIfCancellationRequested();
                        long remaining=entry.Size-total;
                        int request=remaining >= buffer.Length ? buffer.Length : (int)remaining+1;
                        count=input.Read(buffer,0,request);
                        if (count == 0) break;
                        if (count > remaining) throw new IOException("Source file expands beyond its declared size (corrupt archive or changed file): "+entry.Path);
                        total+=count;
                        if (crc != null) crc.Update(buffer,count);
                        if (sha != null) sha.TransformBlock(buffer,0,count,buffer,0);
                        output.Write(buffer,0,count);
                        if (progress.ElapsedMilliseconds >= 100) { OperationContext.Report("Reading "+entry.Path,total,entry.Size); progress.Restart(); }
                    }
                }
                catch (InvalidDataException ex) { throw new IOException("Compressed ZIP data is corrupt: "+entry.Path+". Download or recreate the archive.",ex); }
                if (total != entry.Size) throw new IOException("Source file size mismatch (corrupt archive or changed file): "+entry.Path);
                if (crc != null && crc.Value != entry.ZipChecksum.Value) throw new IOException("ZIP checksum failed: "+entry.Path+". Download or recreate the archive.");
                OperationContext.ThrowIfCancellationRequested();
                OperationContext.Report("Verified "+entry.Path,total,entry.Size);
                if (sha == null) return null;
                sha.TransformFinalBlock(new byte[0],0,0); return HashText(sha.Hash);
            }
        }
        public void Dispose() { if (zip != null) { zip.Dispose(); zip=null; } }
    }
    internal static class ProcessUtil
    {
        internal static string Quote(string value)
        {
            if (value.Contains("\"") || value.Contains("\r") || value.Contains("\n")) throw new IOException("Unsupported quote or newline in path.");
            int trailing=value.Length-value.TrimEnd('\\').Length;
            return "\"" + value + new string('\\',trailing) + "\"";
        }
        internal static string RunText(string exe, string args) { return Encoding.UTF8.GetString(RunBytes(exe,args,64*1024*1024)); }
        internal static byte[] RunBytes(string exe, string args, int limit)
        {
            return RunBytes(exe,args,limit,OperationContext.Token,TimeSpan.FromMinutes(10));
        }
        internal static byte[] RunBytes(string exe,string args,int limit,CancellationToken cancellationToken,TimeSpan timeout)
        {
            if (limit < 0) throw new ArgumentOutOfRangeException("limit");
            if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException("timeout");
            cancellationToken.ThrowIfCancellationRequested();
            string tool=Path.GetFileNameWithoutExtension(exe);
            if (String.Equals(tool,"7z",StringComparison.OrdinalIgnoreCase) || String.Equals(tool,"7za",StringComparison.OrdinalIgnoreCase) || String.Equals(tool,"7zr",StringComparison.OrdinalIgnoreCase)) tool="7-Zip";
            if (String.IsNullOrEmpty(tool)) tool="External tool";
            using (var p = new Process())
            {
                p.StartInfo = new ProcessStartInfo(exe,args) { UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,RedirectStandardInput=true,StandardErrorEncoding=Encoding.UTF8 };
                p.Start(); p.StandardInput.Close(); // Password/overwrite prompts cannot wait for invisible input.
                Task<byte[]> output=ReadBoundedAsync(p.StandardOutput.BaseStream,limit,tool+" output");
                Task<byte[]> error=ReadBoundedAsync(p.StandardError.BaseStream,1024*1024,tool+" error output");
                var watch=Stopwatch.StartNew();
                try
                {
                    while (!p.HasExited || !output.IsCompleted || !error.IsCompleted)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (watch.Elapsed > timeout) throw new IOException(tool+" timed out. Check the files and drive, then try again.");
                        if (output.IsFaulted) throw output.Exception.GetBaseException();
                        if (error.IsFaulted) throw error.Exception.GetBaseException();
                        // 1.0.12: wake as soon as 7-Zip exits or its output ends (a fixed 20 ms sleep added up to 20 ms per run)
                        if (!p.HasExited) p.WaitForExit(20);
                        else try { Task.WaitAll(new Task[] { output, error }, 20); } catch (AggregateException) { }
                    }
                    if (output.IsFaulted) throw output.Exception.GetBaseException();
                    if (error.IsFaulted) throw error.Exception.GetBaseException();
                    if (p.ExitCode != 0) throw new IOException(tool+" failed ("+p.ExitCode+"): "+Encoding.UTF8.GetString(error.Result).Trim());
                    return output.Result;
                }
                catch
                {
                    try { if (!p.HasExited) p.Kill(); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
                    try { p.WaitForExit(5000); } catch (InvalidOperationException) { }
                    try { Task.WaitAll(new Task[] { output,error },5000); } catch (AggregateException) { }
                    throw;
                }
            }
        }
        private static async Task<byte[]> ReadBoundedAsync(Stream input,int limit,string label)
        {
            using (var mem=new MemoryStream())
            {
                var buffer=new byte[65536]; int count;
                while ((count=await input.ReadAsync(buffer,0,(int)Math.Min(buffer.Length,(long)limit-mem.Length+1)).ConfigureAwait(false)) > 0)
                {
                    if (count > limit-mem.Length) throw new IOException(label+" exceeded the size limit.");
                    mem.Write(buffer,0,count);
                }
                return mem.ToArray();
            }
        }
    }
}
