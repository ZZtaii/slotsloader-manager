using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SlotsInstaller
{
    // 2.5.4: "Install all variants" copies a library archive unchanged into costume_mods\<character>\, exactly where a
    // manual drop goes, so SlotsLoader builds every variant it finds (with the Installed page's variant on/off). The
    // copy stays an ordinary manual archive to the engine (uninstall, Undo, presets and variants already handle those);
    // Data\archive-installs.json only remembers which library source it came from, matched by path, size and write time.
    public sealed class ArchiveInstallRecord
    {
        public string Id, Path, SourcePath, Character, CreatedUtc, Sha256;
        public long Size, WriteTicks;
    }
    public sealed class ArchiveInstallFile
    {
        public string Format = "slotsloader-archive-installs-v1";
        public List<ArchiveInstallRecord> Records = new List<ArchiveInstallRecord>();
    }

    internal static class ArchiveInstalls
    {
        internal const string FileName = "archive-installs.json";
        private static readonly object Sync = new object();
        private static readonly string[] Extensions = { ".zip", ".rar", ".7z" };
        internal static string DataDirOverride;   // checks: a fixture Data folder
        private static string RecordsPath { get { return System.IO.Path.Combine(DataDirOverride ?? AppConfig.DataDir, FileName); } }

        internal static bool IsArchive(string path) { return !String.IsNullOrEmpty(path) && File.Exists(path) && Extensions.Contains(System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase); }

        private static ArchiveInstallFile Load()
        {
            try
            {
                string file = RecordsPath;
                if (!File.Exists(file)) return new ArchiveInstallFile();
                Safety.NoReparseChain(file);
                var read = Json.Read<ArchiveInstallFile>(file);
                if (read == null || read.Format != "slotsloader-archive-installs-v1" || read.Records == null) return new ArchiveInstallFile();
                read.Records = read.Records.Where(r => r != null && !String.IsNullOrEmpty(r.Path) && !String.IsNullOrEmpty(r.SourcePath)).ToList();
                return read;
            }
            catch (Exception ex) { Program.Log(ex); return new ArchiveInstallFile(); }
        }
        private static void Save(ArchiveInstallFile records)
        {
            string file = RecordsPath; Safety.NoReparseChain(file);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file));
            Json.Write(file, records);
        }
        // Same place, size and write time. A copy put back by Undo or a preset has a new write time: then the content
        // hash decides, and the record takes the new time so the next refresh is cheap again.
        private static bool Matches(ArchiveInstallRecord r, string path, ref bool touched)
        {
            if (!String.Equals(System.IO.Path.GetFullPath(r.Path), System.IO.Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)) return false;
            var info = new FileInfo(path);
            if (!info.Exists || info.Length != r.Size) return false;
            if (info.LastWriteTimeUtc.Ticks == r.WriteTicks) return true;
            if (String.IsNullOrEmpty(r.Sha256) || !String.Equals(Hash(path), r.Sha256, StringComparison.OrdinalIgnoreCase)) return false;
            r.WriteTicks = info.LastWriteTimeUtc.Ticks; touched = true; return true;
        }
        private static bool Matches(ArchiveInstallRecord r, string path) { bool touched = false; return Matches(r, path, ref touched); }
        private static string Hash(string path)
        {
            using (var sha = FastHash.Sha256())
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1 << 20))
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
        }

        // Marks the archives the Manager copied (Installer.ListInstalled). Never throws.
        internal static void Apply(List<InstalledOutfit> outfits)
        {
            try
            {
                List<ArchiveInstallRecord> records; bool touched = false;
                lock (Sync) records = Load().Records;
                if (records.Count == 0) return;
                foreach (InstalledOutfit o in outfits)
                {
                    if (o.Managed || !o.Enabled || !File.Exists(o.Folder)) continue;
                    ArchiveInstallRecord r = null;
                    for (int i = records.Count - 1; i >= 0 && r == null; i--) if (Matches(records[i], o.Folder, ref touched)) r = records[i];
                    if (r == null) continue;
                    o.WholeArchive = true; o.SourcePath = r.SourcePath;
                    DateTime created;
                    if (DateTime.TryParse(r.CreatedUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out created)) o.InstalledUtc = created.ToUniversalTime();
                }
                if (touched) lock (Sync)
                {
                    ArchiveInstallFile now = Load();
                    foreach (ArchiveInstallRecord r in now.Records)
                    {
                        ArchiveInstallRecord seen = records.FirstOrDefault(x => x.Id == r.Id && String.Equals(x.Path, r.Path, StringComparison.OrdinalIgnoreCase));
                        if (seen != null) r.WriteTicks = seen.WriteTicks;
                    }
                    Save(now);
                }
            }
            catch (Exception ex) { Program.Log(ex); }
        }

        // 1.0.5 Rename: the records of a renamed library source name its new path. Returns how many changed; a write
        // failure throws (the rename rolls back).
        internal static int RenameSource(string from, string to)
        {
            lock (Sync)
            {
                ArchiveInstallFile records = Load();
                int changed = 0;
                foreach (ArchiveInstallRecord r in records.Records)
                    if (String.Equals(System.IO.Path.GetFullPath(r.SourcePath), System.IO.Path.GetFullPath(from), StringComparison.OrdinalIgnoreCase)) { r.SourcePath = System.IO.Path.GetFullPath(to); changed++; }
                if (changed > 0) Save(records);
                return changed;
            }
        }

        // The archive's place in costume_mods, as a manual drop would have it. `fileName` (1.0.10, a list preset's saved
        // name): the name in costume_mods; null = the source's.
        internal static string Destination(string costumeRoot, string character, string source) { return Destination(costumeRoot, character, source, null); }
        internal static string Destination(string costumeRoot, string character, string source, string fileName)
        {
            string root = Installer.CostumeRoot(costumeRoot);
            Safety.Segment(character); string name = String.IsNullOrEmpty(fileName) ? System.IO.Path.GetFileName(source) : fileName; Safety.Segment(name);
            if (!String.IsNullOrEmpty(fileName) && (name.StartsWith(".") || !Extensions.Contains(System.IO.Path.GetExtension(name), StringComparer.OrdinalIgnoreCase))) throw new IOException("Install all variants needs a ZIP, RAR or 7Z name.");
            return Safety.Under(Safety.Under(root, character), name);
        }

        private static bool SamePath(string a, string b)
        {
            try { return String.Equals(System.IO.Path.GetFullPath(a), System.IO.Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; return false; }
        }

        // A library rename changes SourcePath, not the installed filename: that filename is also SlotsLoader's
        // variant-choice key. Reinstall its one existing copy in this character's folder. Records from another
        // costume root are unrelated to this install and are ignored without inspecting their files.
        private static ArchiveInstallRecord ExistingCopy(ArchiveInstallFile records, string source, string destination, string character)
        {
            string folder = System.IO.Path.GetDirectoryName(destination);
            string root = System.IO.Path.GetDirectoryName(folder).TrimEnd(System.IO.Path.DirectorySeparatorChar);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var copies = new List<ArchiveInstallRecord>();
            foreach (ArchiveInstallRecord record in records.Records.AsEnumerable().Reverse())
            {
                if (!SamePath(record.SourcePath, source) || !String.Equals(record.Character, character, StringComparison.OrdinalIgnoreCase)) continue;
                string path;
                try { path = System.IO.Path.GetFullPath(record.Path); }
                catch (Exception ex) { if (ex is OutOfMemoryException) throw; throw new IOException("The installed archive's recorded path is not valid. Refresh the Installed page before reinstalling.", ex); }
                if (!System.IO.Path.IsPathRooted(record.Path)) throw new IOException("The installed archive's recorded path is not absolute. Refresh the Installed page before reinstalling.");
                if (!SamePath(path, root) && !path.StartsWith(root + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                if (!SamePath(System.IO.Path.GetDirectoryName(path), folder) || !Extensions.Contains(System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                    throw new IOException("The installed archive's recorded path is outside costume_mods\\" + character + ". It cannot be used for this reinstall.");
                if (!seen.Add(path)) continue;
                Safety.NoReparseChain(path);
                if (Directory.Exists(path)) throw new IOException(System.IO.Path.GetFileName(path) + " is now a folder. Uninstall or rename it first.");
                if (File.Exists(path)) copies.Add(record);
            }
            if (copies.Count > 1) throw new IOException("More than one installed archive is linked to this mod for " + character + ". Remove the extra copies on the Installed page before reinstalling.");
            return copies.SingleOrDefault();
        }

        private static void VerifyCopy(ArchiveInstallRecord record, string path)
        {
            Safety.NoReparseChain(path);
            var info = new FileInfo(path);
            if (!info.Exists || info.Length != record.Size || String.IsNullOrEmpty(record.Sha256) || !String.Equals(Hash(path), record.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException(System.IO.Path.GetFileName(path) + " changed since the Manager installed it. Uninstall or rename it before reinstalling.");
        }

        internal static InstallResult Install(string source, string costumeRoot, string character, string modName) { return Install(source, costumeRoot, character, modName, null); }
        internal static InstallResult Install(string source, string costumeRoot, string character, string modName, string fileName)
        {
            if (!IsArchive(source)) throw new IOException("Install all variants needs a ZIP, RAR or 7Z archive.");
            if (!Characters.Names.Contains(character)) throw new IOException("Choose a known character.");
            using (Installer.AcquireMutation(costumeRoot))
            {
                string dest = Destination(costumeRoot, character, source, fileName);
                ArchiveInstallFile records; lock (Sync) records = Load();
                ArchiveInstallRecord previous = ExistingCopy(records, source, dest, character);
                if (previous != null) { dest = System.IO.Path.GetFullPath(previous.Path); VerifyCopy(previous, dest); }
                string folder = System.IO.Path.GetDirectoryName(dest), name = System.IO.Path.GetFileName(dest);
                Safety.NoReparseChain(dest);
                if (Directory.Exists(dest) || (File.Exists(dest) && previous == null))
                    throw new IOException(name + " is already in costume_mods\\" + character + ". Uninstall or rename it first.");
                Directory.CreateDirectory(folder);
                // SlotsLoader skips names that start with a dot, so a half-copied archive is never read.
                string temp = System.IO.Path.Combine(folder, "." + name + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".partial");
                try
                {
                    File.Copy(source, temp, false);
                    OperationContext.ThrowIfCancellationRequested();
                    using (OperationContext.NonCancellable())
                    {
                        if (previous != null) { VerifyCopy(previous, dest); File.Replace(temp, dest, null); }
                        else File.Move(temp, dest);
                    }
                }
                finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
                var info = new FileInfo(dest);
                var record = new ArchiveInstallRecord { Id = previous != null ? previous.Id : Guid.NewGuid().ToString("N"), Path = info.FullName, SourcePath = System.IO.Path.GetFullPath(source), Character = character, CreatedUtc = DateTime.UtcNow.ToString("o"), Size = info.Length, WriteTicks = info.LastWriteTimeUtc.Ticks, Sha256 = Hash(dest) };
                lock (Sync)
                {
                    records = Load();
                    records.Records.RemoveAll(r => String.Equals(System.IO.Path.GetFullPath(r.Path), record.Path, StringComparison.OrdinalIgnoreCase));
                    records.Records.Add(record);
                    Save(records);
                }
                OutfitHistory.Installed(new InstallManifest { Id = record.Id, Name = name, Character = character, SourcePath = record.SourcePath }, null, modName);   // never throws
                return new InstallResult { Folder = dest, FileCount = 1, Bytes = info.Length, Message = (previous != null ? "Reinstalled " : "Installed ") + "every variant of " + (modName ?? name) + " for " + character + ". Restart SF6 to load them." };
            }
        }
    }
}
