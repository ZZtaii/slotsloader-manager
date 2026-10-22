using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;

namespace SlotsInstaller
{
    // 2.2.0: who changed a game file this app installed. A Fluffy mod listing the file first, then a table of
    // known builds (SHA-256 of the SlotsLoader proxy/plugin releases and the game's genuine AMD AGS library,
    // computed from the SlotsLoader packages in the user's Fluffy library and the workspace builds), else
    // "another tool".
    internal static class KnownBuilds
    {
        internal static readonly Dictionary<string, string> Table = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "298B40FB70C142CC61135A125FF6C2A2E474526B9A7045102666A9434E84F459", "the game's own amd_ags_x64.dll" },
            { "DA81B7687F561A30FFF43FD33CEFB9A40EE59356C8D0AAC5AA1C7C129E6ED585", "the Slotsloader 1.9.2 proxy" },
            { "0F9A44FB11EC539B7EADBF95E01C1016A7EE591A1ABCC2D83468DB587D324A9B", "the Slotsloader 1.9.2 plugin" },
            { "3F62C1CB49C4C399378B22BCA46A1A2C1AB63E48CB106C507C5ACE242E65C10C", "the Slotsloader 1.9.3-beta proxy" },
            { "002EA8A115D74BEFCCC783E25B155F48F99D450366240B0BF277680BD2DA02A1", "the Slotsloader 1.9.3-beta plugin" },
            { "0B8E63467D8C212C1EC73B8D571884C54CA864CC42B1F05792E4441E5EC059F5", "the Slotsloader 1.9.4 proxy" },
            { "84A32516F401E6389B4F34213F808630BA05DB811C04F285EE8354A3B7007409", "the Slotsloader 1.9.4 plugin" },
            { "32A737BBFBE26EBBAEBBFFADF336BEA18897C8325C26F6C5B8391ACEC66785A7", "the Slotsloader 1.9.4 Chain-Relocation-Test proxy" },
            { "51C58609EAD6D2457D85C05F86559A6BCA9A587CE08EA3D9F6005A7DFF417C64", "the Slotsloader 1.9.4 Chain-Relocation-Test plugin" },
            { "7A3397EE7CE6AEA23DFF1F2CF983D71FC8D2BE1678B373FB718A93F56F573C96", "the Slotsloader 1.9.5 Game-Update-Test proxy" },
            { "A89C48CC40F7440487F55386D6D404460B076FA20AA537B536C8B9450395E7E6", "the Slotsloader 1.9.5 Game-Update-Test plugin" },
            { "402A68D7D5ED01D6962E56B12D6910064412B44EE9A1FE8519FC149101231909", "the Slotsloader 1.9.6 (New-Costumes-Test) proxy" },
            { "93B82C4340B6D8BF71DA0D6C6BB3CD9B74ABDA6ABD379ABFF3D7694EC008D82B", "the Slotsloader 1.9.6 (New-Costumes-Test) plugin" },
            { "65E4A43B72961EC3C5B2F99350D736FFB056BDE947FE0B7446AC9E3C52A3C133", "the Slotsloader 1.9.7 (game-derived) proxy" },
            { "2F0306C4FE2846777461CA11C5EED505E7F0B2FCC43D2E427E794EE6B16AB230", "the Slotsloader 1.9.7 (game-derived) plugin" },
            { "949569E9AB368169B57620E5D8BCC38D46DF465295B29E4056DF960424867E59", "the Slotsloader 1.9.8 (loose-colours) proxy" },
            { "958F0DFE0D6C50593F9F703F667A74060DFD7550F276DF93556D6850C8FFEAC2", "the Slotsloader 1.9.8 (loose-colours) plugin" },
            { "561145E3C0183121E7CB710971F207C9054805C00402CA7203C421A2D2BDD140", "the SF6 Mod Suite 1.11.2 proxy" },
            { "57035E7A5D12864115E21ED713615FFFDAB7C4A81798EE505D5DDF3948A457D7", "the SF6 Mod Suite 1.11.2 plugin" },
            { "255C6A6D62ED6C59FB5A031ED58D70313966BED4BA5911F67DED949B924C754B", "a Slotsloader development build (coexistence)" },
            { "3E04755FD8D4E79F4C3E133F74403C4E6647019C4E8F22E53490F3058C26AFF2", "a Slotsloader development build (coexistence baseline)" },
        };
        // Checks add fixture hashes here.
        internal static readonly Dictionary<string, string> Extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        internal static string Name(string sha)
        {
            if (String.IsNullOrEmpty(sha)) return null;
            string name;
            if (Extra.TryGetValue(sha, out name) || Table.TryGetValue(sha, out name)) return name;
            return null;
        }

        // "the Fluffy Mod Manager mod 'X' (the Slotsloader 1.9.6 proxy)", a known build, or "another tool".
        internal static string Changer(string path, string sha, FluffyInstalls fluffy)
        {
            FluffyMod mod = fluffy == null ? null : fluffy.Owner(path);
            string build = Name(sha);
            if (mod != null) return "the external mod manager's mod '" + mod.Name + "'" + (build != null ? " (" + build + ")" : "");
            return build ?? "another tool";
        }
    }

    // 2.2.0: watches every Fluffy installed.ini (Fluffy writes installed.ini.new and renames it). Events are
    // debounced; Changed is raised on a thread-pool thread. Missing folders are skipped (re-created on refresh).
    internal sealed class FluffyWatcher : IDisposable
    {
        private readonly List<FileSystemWatcher> watchers = new List<FileSystemWatcher>();
        private readonly Timer timer;
        private readonly int debounce;
        internal readonly string RootsKey;
        internal event EventHandler Changed;
        // 2.4.2: every detected root is watched (each Fluffy instance has its own installed.ini). A root whose folder
        // was missing at start, or a watcher that failed (buffer overflow, folder deleted), makes NeedsRestart true;
        // the window then creates a new watcher (MainForm.CheckFluffyStamp).
        internal readonly List<string> Watched = new List<string>(), Missing = new List<string>();
        private volatile bool failed;
        internal bool NeedsRestart { get { if (failed) return true; foreach (string m in Missing) { try { if (Directory.Exists(m)) return true; } catch { } } return false; } }

        internal FluffyWatcher(IEnumerable<string> roots, int debounceMs)
        {
            debounce = debounceMs;
            var list = (roots ?? new string[0]).Where(r => !String.IsNullOrEmpty(r)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            RootsKey = Key(list);
            timer = new Timer(delegate { EventHandler handler = Changed; if (handler != null) try { handler(this, EventArgs.Empty); } catch (Exception ex) { Program.Log(ex); } }, null, Timeout.Infinite, Timeout.Infinite);
            foreach (string root in list)
            {
                try
                {
                    if (!Directory.Exists(root)) { Missing.Add(root); continue; }
                    var w = new FileSystemWatcher(root, "installed.ini*") { IncludeSubdirectories = false, NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime };
                    FileSystemEventHandler poke = delegate { Poke(); };
                    w.Changed += poke; w.Created += poke; w.Deleted += poke; w.Renamed += delegate { Poke(); }; w.Error += delegate { failed = true; Poke(); };
                    w.EnableRaisingEvents = true;
                    watchers.Add(w); Watched.Add(root);
                }
                catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); }
            }
        }
        internal static string Key(IEnumerable<string> roots) { return String.Join("|", (roots ?? new string[0]).Select(r => (r ?? "").TrimEnd('\\').ToUpperInvariant()).OrderBy(r => r, StringComparer.Ordinal)); }
        internal int Count { get { return watchers.Count; } }
        private void Poke() { try { timer.Change(debounce, Timeout.Infinite); } catch (ObjectDisposedException) { } }
        public void Dispose()
        {
            foreach (FileSystemWatcher w in watchers) { try { w.EnableRaisingEvents = false; w.Dispose(); } catch { } }
            watchers.Clear();
            timer.Dispose();
        }
    }

    // <Data>\fluffy-removed\<section>-<time>-<rand>\record.json + files\: a Fluffy mod uninstalled by the Manager,
    // restorable exactly (its files, Fluffy's backups and its installed.ini section).
    public sealed class FluffyRemovedRecord
    {
        public string Format = "sf6-fluffy-removed-v1";
        public string State = "";                    // prepared, done
        public string GameRoot = "", Root = "", Section = "", ModName = "", InstallType = "", RemovedUtc = "";
        public string SectionBase64 = "", IniShaBefore = "", IniShaAfter = "", IniBackup = "";
        public int SectionOffset;
        public bool CrashRestored, CrashTakenByManager;
        public List<FluffyRemovedFile> Files = new List<FluffyRemovedFile>();
        public List<string> RemovedDirs = new List<string>();
        public List<FluffyRemovedRow> Rows = new List<FluffyRemovedRow>();          // 2.4.2: game entries restored (FluffyPakUninstall.cs)
        public List<FluffyRemovedPatch> Patches = new List<FluffyRemovedPatch>();   // 2.4.2: patch PAKs removed or emptied
    }

    public sealed class FluffyRemovedFile
    {
        // deleted, restored-backup (Fluffy's Backup copy put back), restored-other (another Fluffy mod's copy put
        // back), kept-shared (another Fluffy mod lists it), kept-manager (this app manages it), missing.
        public string Path = "", Action = "", ShaBefore, ShaAfter, Stored, BackupStored, Other = "";
        public bool FmmRenamed;                      // <stem>_FMMBACKUP<ext> renamed back to the file
    }

    internal sealed class FluffyRemoved
    {
        internal string Folder;
        internal FluffyRemovedRecord Record;
        internal long Bytes;
        internal string Name { get { return String.IsNullOrEmpty(Record.ModName) ? Record.Section : Record.ModName; } }
    }

    internal sealed partial class ReplaceEngine
    {
        internal string FluffyRemovedDir { get { return Path.Combine(DataDir, "fluffy-removed"); } }
        internal string FluffyIniBackupDir { get { return Path.Combine(DataDir, "fluffy-ini-backups"); } }
        internal const string FluffyRunningReason = "Close the external mod manager first: it rewrites installed.ini while open.";

        // Folders the game, REFramework or SlotsLoader need: never removed when a Fluffy mod empties them.
        private static readonly string[] KeepFolders = { "natives", "natives/stm", "natives/x64", "reframework", "reframework/autorun", "reframework/plugins", "reframework/data", "reframework/fonts", "reframework/costume_mods", "reframework/images", "dlc" };

        private static bool SameRoot(string a, string b)
        {
            try { return String.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase); } catch { return false; }
        }

        // Byte range of [section] in installed.ini: from its header line to the next header line (or the end).
        internal static bool SectionRange(byte[] bytes, string section, out int start, out int end)
        {
            start = -1; end = -1;
            int bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
            int pos = bom;
            var utf8 = new UTF8Encoding(false);
            while (pos < bytes.Length)
            {
                int nl = Array.IndexOf(bytes, (byte)'\n', pos);
                int lineEnd = nl < 0 ? bytes.Length : nl + 1;
                string line = utf8.GetString(bytes, pos, lineEnd - pos).Trim().Trim('﻿');
                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    string name = line.Substring(1, line.Length - 2);
                    if (start >= 0) { end = pos; return true; }
                    if (String.Equals(name, section, StringComparison.Ordinal)) start = pos;
                }
                pos = lineEnd;
            }
            if (start >= 0) { end = bytes.Length; return true; }
            return false;
        }

        private static string SafeName(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s ?? "") sb.Append(Path.GetInvalidFileNameChars().Contains(c) || c < 32 ? '_' : c);
            string r = sb.ToString().Trim().TrimEnd('.');
            if (r.Length > 40) r = r.Substring(0, 40);
            return r.Length == 0 ? "mod" : r;
        }

        // The copy of `relative` inside a Fluffy mod's source (<root>\Mods\<name> folder or .zip), or null.
        internal static byte[] FluffyModFile(FluffyMod mod, string relative)
        {
            string mods = Path.Combine(mod.Root, "Mods");
            string want = FluffyInstalls.Key(relative);
            foreach (string name in new[] { mod.Section, mod.Name }.Where(n => !String.IsNullOrEmpty(n)).Distinct())
            {
                try
                {
                    Safety.Segment(name);
                    string folder = Path.Combine(mods, name);
                    if (Directory.Exists(folder))
                    {
                        string file = Safety.Under(folder, want);
                        if (File.Exists(file) && new FileInfo(file).Length < 512L * 1024 * 1024) return File.ReadAllBytes(file);
                    }
                    string zip = folder + ".zip";
                    if (File.Exists(zip))
                        using (ZipArchive archive = ZipFile.OpenRead(zip))
                        {
                            var matches = archive.Entries.Where(e => e.Length > 0 && String.Equals(FluffyInstalls.Key(e.FullName), want, StringComparison.OrdinalIgnoreCase)).ToList();
                            if (matches.Count == 1 && matches[0].Length < 512L * 1024 * 1024)
                                using (Stream s = matches[0].Open()) using (var ms = new MemoryStream()) { s.CopyTo(ms); return ms.ToArray(); }
                        }
                }
                catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); }
            }
            return null;
        }

        private static void WriteExact(string full, byte[] data)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(full));
            string tmp = full + ".slotsinstaller-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
            try
            {
                using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.WriteThrough)) { fs.Write(data, 0, data.Length); fs.Flush(true); }
                DurableFile.Publish(tmp, full);
            }
            finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
        }
        private static void CopyExact(string from, string to) { WriteExact(to, File.ReadAllBytes(from)); }

        // Uninstalls a Fluffy mod the way Fluffy does (its Log.txt: "Restored file by copying Games\SF6\Backup\…",
        // "Restored file from other mod (…)", "Deleted file: …"), keeps everything it removes, then removes its
        // installed.ini section (backed up first, written atomically, every other byte unchanged).
        // forVanilla (1.0.1): Restore game to vanilla clears the external mod manager's record too (the user's choice),
        // so the lockout's "uninstall it there" blocker doesn't apply.
        internal string UninstallFluffy(string root, string section, out string removedFolder, bool forVanilla = false)
        {
            removedFolder = null;
            using (Lock())
            {
                GameProcesses.RequireGameClosed("uninstall it");
                if (GameProcesses.Probe.IsFluffyRunning()) throw new IOException(FluffyRunningReason);
                if (JournalPending) Recover();
                string ini = Path.Combine(root, "installed.ini");
                byte[] bytes = FluffyInstalls.ReadShared(ini);
                if (bytes == null) throw new IOException("installed.ini was not found in " + root + ". Refresh the list.");
                List<FluffyMod> mods = FluffyInstalls.Parse(FluffyInstalls.DecodeIni(bytes), root);
                FluffyMod target = mods.FirstOrDefault(m => String.Equals(m.Section, section, StringComparison.Ordinal));
                if (target == null) throw new IOException("'" + section + "' is no longer installed (installed.ini changed). Refresh the list.");
                if (target.UninstallBlocker != null && !forVanilla) throw new IOException(target.UninstallBlocker);
                int start, end;
                if (!SectionRange(bytes, section, out start, out end)) throw new IOException("Could not find the [" + section + "] section in installed.ini.");
                FluffyInstalls others = Fluffy();
                var remaining = mods.Where(m => m != target).Concat(others.Mods.Where(m => !SameRoot(m.Root, root))).ToList();
                ReplaceState state = LoadState();

                var record = new FluffyRemovedRecord { State = "prepared", GameRoot = GameRoot, Root = root, Section = section, ModName = target.Name, InstallType = target.InstallType ?? "", RemovedUtc = DateTime.UtcNow.ToString("o"), SectionBase64 = Convert.ToBase64String(bytes, start, end - start), SectionOffset = start, IniShaBefore = Sha(bytes) };
                string folder = Path.Combine(FluffyRemovedDir, SafeName(target.Name) + "-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6));
                string files = Path.Combine(folder, "files"), backups = Path.Combine(folder, "fluffy-backup");
                var otherCopies = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
                int n = 0;
                // 1. Plan and keep copies of everything that will change (nothing in the game folder changes yet).
                Directory.CreateDirectory(files);
                try
                {
                // 2.4.2: a PAK install also gets its invalidated game entries back (each proven, else nothing changes) and
                // its patch PAKs out of the row (FluffyPakUninstall.cs). Exact copies are kept for Undo.
                // 1.0.1: every install type: a game PAK (re_chunk_000.pak, dlc/…) is never removed, and a listed patch PAK
                // leaves the row as for a PAK install (placeholder below another PAK), never by a plain delete.
                string otherPak = target.Files.Select(FluffyInstalls.Key).FirstOrDefault(p => p.EndsWith(".pak", StringComparison.OrdinalIgnoreCase) && !IsPatchPakPath(p));
                if (otherPak != null) throw new IOException("'" + target.Name + "' lists " + otherPak + ", which the Manager doesn't remove. Uninstall it in the external mod manager instead.");
                if (target.PakInstall) record.Rows = PlanFluffyRows(target, remaining, state, others);
                record.Patches = PlanFluffyPatches(target, remaining);
                foreach (FluffyRemovedPatch patch in record.Patches)
                {
                    string full = GamePath(patch.Name);
                    Directory.CreateDirectory(Path.Combine(folder, "paks"));
                    patch.Stored = "p" + (n++) + ".bin";
                    patch.ShaBefore = Sha(full);
                    File.Copy(full, Path.Combine(folder, "paks", patch.Stored));
                    if (Sha(Path.Combine(folder, "paks", patch.Stored)) != patch.ShaBefore) throw new IOException("Could not keep an exact copy of " + patch.Name + ".");
                }
                // 1.0.1: a patch PAK the row planning took (only this mod lists it) is not a file here. One another Fluffy
                // mod lists too stays a shared file, so the other mod's copy is put back from its source (as before 1.0.1).
                // A PAK install keeps its 2.4.2 rule: its patch PAKs are row planning's only.
                var plannedPaks = new HashSet<string>(record.Patches.Select(p => p.Name), StringComparer.OrdinalIgnoreCase);
                foreach (string rel in target.Files.Select(FluffyInstalls.Key).Distinct(StringComparer.OrdinalIgnoreCase).Where(r => !(IsPatchPakPath(r) && (target.PakInstall || plannedPaks.Contains(r)))))
                {
                    string full = GamePath(rel);
                    Safety.NoReparseChain(full);
                    var file = new FluffyRemovedFile { Path = rel };
                    bool exists = File.Exists(full);
                    file.ShaBefore = exists ? Sha(full) : null;
                    var owners = remaining.Where(m => m.Files.Any(x => String.Equals(FluffyInstalls.Key(x), rel, StringComparison.OrdinalIgnoreCase))).ToList();
                    string fluffyBackup = Safety.Under(Path.Combine(root, "Backup"), rel);
                    string sibling = Path.Combine(Path.GetDirectoryName(full), Path.GetFileNameWithoutExtension(full) + "_FMMBACKUP" + Path.GetExtension(full));
                    if (owners.Count > 0)
                    {
                        // Shared with another Fluffy mod: kept. When this mod's copy is the one in place (installed after
                        // the other), Fluffy puts the other mod's copy back from its source; done the same when readable.
                        FluffyMod latest = owners.Where(m => SameRoot(m.Root, root)).OrderBy(m => m.Index).LastOrDefault() ?? owners[0];
                        file.Other = latest.Name;
                        byte[] copy = SameRoot(latest.Root, root) && target.Index > latest.Index && exists ? FluffyModFile(latest, rel) : null;
                        if (copy != null && Sha(copy) != file.ShaBefore) { file.Action = "restored-other"; otherCopies[rel] = copy; }
                        else file.Action = "kept-shared";
                    }
                    else if (File.Exists(fluffyBackup)) file.Action = "restored-backup";
                    else if (state.Deployment(rel) != null || state.Release(rel) != null) file.Action = "kept-manager";
                    else if (!exists) { file.Action = "missing"; }
                    else { file.Action = "deleted"; file.FmmRenamed = File.Exists(sibling); }
                    if (exists && (file.Action == "deleted" || file.Action == "restored-backup" || file.Action == "restored-other"))
                    {
                        file.Stored = "f" + (n++) + ".bin";
                        File.Copy(full, Path.Combine(files, file.Stored));
                        if (Sha(Path.Combine(files, file.Stored)) != file.ShaBefore) throw new IOException("Could not keep an exact copy of " + rel + ".");
                    }
                    if (file.Action == "restored-backup")
                    {
                        Directory.CreateDirectory(backups);
                        file.BackupStored = "b" + (n++) + ".bin";
                        File.Copy(fluffyBackup, Path.Combine(backups, file.BackupStored));
                    }
                    record.Files.Add(file);
                }
                Directory.CreateDirectory(FluffyIniBackupDir);
                record.IniBackup = Path.Combine(FluffyIniBackupDir, "installed-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".ini");
                File.WriteAllBytes(record.IniBackup, bytes);
                File.WriteAllBytes(Path.Combine(folder, "installed.ini.before"), bytes);
                DurableFile.WriteJson(Path.Combine(folder, "record.json"), record);
                }
                catch { try { Directory.Delete(folder, true); } catch { } throw; }

                // 2. Apply. Any failure puts back what was changed from the copies above.
                var done = new List<FluffyRemovedFile>();
                var patchesDone = new List<FluffyRemovedPatch>();
                bool iniWritten = false, rowsWritten = false;
                try
                {
                    // Entries first, then files: the game never sees an invalidated entry without its loose file.
                    if (record.Rows.Count > 0) { WriteFluffyRows(record.Rows, false); rowsWritten = true; }
                    foreach (FluffyRemovedFile file in record.Files)
                    {
                        OperationContext.ThrowIfCancellationRequested();
                        string full = GamePath(file.Path);
                        string sibling = Path.Combine(Path.GetDirectoryName(full), Path.GetFileNameWithoutExtension(full) + "_FMMBACKUP" + Path.GetExtension(full));
                        if (file.Action == "restored-other") WriteExact(full, otherCopies[file.Path]);
                        else if (file.Action == "restored-backup")
                        {
                            string fluffyBackup = Safety.Under(Path.Combine(root, "Backup"), file.Path);
                            CopyExact(fluffyBackup, full);
                            File.Delete(fluffyBackup);   // Fluffy deletes a backup once it is restored
                        }
                        else if (file.Action == "deleted")
                        {
                            File.Delete(full);
                            if (file.FmmRenamed && File.Exists(sibling)) File.Move(sibling, full);
                            else RemoveEmptyFolders(Path.GetDirectoryName(full), record.RemovedDirs);
                        }
                        done.Add(file);
                        file.ShaAfter = File.Exists(full) ? Sha(full) : null;
                    }
                    foreach (FluffyRemovedPatch patch in record.Patches)
                    {
                        string full = GamePath(patch.Name);
                        if (patch.Action == "placeholder") WriteExact(full, FluffyDummyPak); else if (File.Exists(full)) File.Delete(full);
                        patchesDone.Add(patch);
                        patch.ShaAfter = File.Exists(full) ? Sha(full) : null;
                    }
                    // Crash reporter: Fluffy renames it while its mods are installed. The last Fluffy mod gone: back,
                    // unless this app's own replacements still want it renamed (then this app takes it over).
                    string crash = GamePath(CrashReporter.Name), renamed = GamePath(CrashReporter.RenamedName);
                    if (remaining.Count == 0 && File.Exists(renamed) && !File.Exists(crash) && !state.CrashReporterRenamed)
                    {
                        if (state.Mods.Any(m => m.Enabled) && Settings.RenameCrashReporter) { state.CrashReporterRenamed = true; state.Generation++; DurableFile.WriteJson(StatePath, state); record.CrashTakenByManager = true; }
                        else { File.Move(renamed, crash); record.CrashRestored = true; }
                    }
                    // installed.ini without the section, every other byte unchanged. Re-read first: Fluffy must not
                    // have written it meanwhile.
                    byte[] now = FluffyInstalls.ReadShared(ini);
                    if (now == null || Sha(now) != record.IniShaBefore) throw new IOException("installed.ini changed during the uninstall (is the external mod manager open?). Nothing was removed.");
                    byte[] result = new byte[bytes.Length - (end - start)];
                    Buffer.BlockCopy(bytes, 0, result, 0, start);
                    Buffer.BlockCopy(bytes, end, result, start, bytes.Length - end);
                    // Fluffy has no installed.ini while nothing is installed: the last section gone, the file goes.
                    string rest = FluffyInstalls.DecodeIni(result);
                    if (FluffyInstalls.Parse(rest, root).Count == 0 && rest.Trim().Trim('\uFEFF').Length == 0) { File.Delete(ini); record.IniShaAfter = ""; }
                    else { DurableFile.Write(ini, result); record.IniShaAfter = Sha(result); }
                    iniWritten = true;
                    record.State = "done";
                    DurableFile.WriteJson(Path.Combine(folder, "record.json"), record);
                }
                catch (Exception ex)
                {
                    if (ex is OutOfMemoryException) throw;
                    var problems = new List<string>();
                    if (!iniWritten)
                    {
                        for (int i = done.Count - 1; i >= 0; i--)
                        {
                            try { UndoFluffyFile(record, folder, done[i]); } catch (Exception undo) { if (undo is OutOfMemoryException) throw; problems.Add(undo.Message); }
                        }
                        for (int i = patchesDone.Count - 1; i >= 0; i--)
                        {
                            try { RestoreFluffyPatch(folder, patchesDone[i]); } catch (Exception undo) { if (undo is OutOfMemoryException) throw; problems.Add(undo.Message); }
                        }
                        if (rowsWritten) { try { WriteFluffyRows(record.Rows, true); } catch (Exception undo) { if (undo is OutOfMemoryException) throw; problems.Add(undo.Message); } }
                        try
                        {
                            for (int i = record.RemovedDirs.Count - 1; i >= 0; i--) Directory.CreateDirectory(GamePath(record.RemovedDirs[i]));
                            if (record.CrashRestored && File.Exists(GamePath(CrashReporter.Name)) && !File.Exists(GamePath(CrashReporter.RenamedName))) File.Move(GamePath(CrashReporter.Name), GamePath(CrashReporter.RenamedName));
                            if (record.CrashTakenByManager) { ReplaceState s = LoadState(); s.CrashReporterRenamed = false; s.Generation++; DurableFile.WriteJson(StatePath, s); }
                        }
                        catch (Exception undo) { if (undo is OutOfMemoryException) throw; problems.Add(undo.Message); }
                        if (problems.Count == 0) { try { Directory.Delete(folder, true); } catch { } }
                    }
                    throw new IOException("Couldn't uninstall '" + target.Name + "': " + ex.Message + (problems.Count > 0 ? " Some files could not be put back (" + String.Join(" ", problems) + "); the copies are kept in " + folder + "." : " Every file was put back."), ex);
                }
                removedFolder = folder;
                int deleted = record.Files.Count(f => f.Action == "deleted"), restored = record.Files.Count(f => f.Action == "restored-backup" || f.Action == "restored-other"), kept = record.Files.Count(f => f.Action == "kept-shared" || f.Action == "kept-manager");
                int paks = record.Patches.Count(p => p.Action != "dummy-deleted"), emptied = record.Patches.Count(p => p.Action == "placeholder"), dummies = record.Patches.Count(p => p.Action == "dummy-deleted");
                string pakText = (record.Rows.Count > 0 ? ", " + ReplacePlanner.N(record.Rows.Count, "game entry") + " restored" : "") + (paks > 0 ? ", " + ReplacePlanner.N(paks, "patch PAK") + " removed" + (emptied > 0 ? " (" + emptied + " left as an empty placeholder)" : "") : "") + (dummies > 0 ? ", " + ReplacePlanner.N(dummies, "empty placeholder PAK") + " removed" : "");
                RecordFluffy(HistoryKinds.FluffyUninstall, record, ReplacePlanner.N(deleted, "file") + " deleted, " + restored + " restored" + pakText);   // 2.4.1 history
                return "Removed '" + target.Name + "': " + ReplacePlanner.N(deleted, "file") + " deleted, " + restored + " restored" + pakText + (kept > 0 ? ", " + kept + " kept (still used by another mod)" : "") + (record.RemovedDirs.Count > 0 ? ", " + ReplacePlanner.N(record.RemovedDirs.Count, "empty folder") + " removed" : "") + (record.CrashRestored ? ", crash reporter restored" : "") + ". A copy is kept in gear → Recover removed outfits.";
            }
        }

        private void RemoveEmptyFolders(string dir, List<string> removed)
        {
            string stop = GameRoot.TrimEnd(Path.DirectorySeparatorChar);
            while (!String.IsNullOrEmpty(dir) && dir.Length > stop.Length && IsUnder(dir, stop))
            {
                string rel = RelativeToGame(dir);
                if (KeepFolders.Contains(rel, StringComparer.OrdinalIgnoreCase)) break;
                if (!Directory.Exists(dir) || Directory.EnumerateFileSystemEntries(dir).Any()) break;
                Directory.Delete(dir);
                removed.Add(rel);
                dir = Path.GetDirectoryName(dir);
            }
        }

        // Puts one file of a removed Fluffy mod back as it was before the uninstall.
        private void UndoFluffyFile(FluffyRemovedRecord record, string folder, FluffyRemovedFile file)
        {
            string full = GamePath(file.Path);
            string sibling = Path.Combine(Path.GetDirectoryName(full), Path.GetFileNameWithoutExtension(full) + "_FMMBACKUP" + Path.GetExtension(full));
            string stored = file.Stored == null ? null : Path.Combine(folder, "files", file.Stored);
            if (file.Action == "deleted")
            {
                if (file.FmmRenamed && File.Exists(full) && !File.Exists(sibling)) File.Move(full, sibling);
                Directory.CreateDirectory(Path.GetDirectoryName(full));
                CopyExact(stored, full);
            }
            else if (file.Action == "restored-backup")
            {
                string fluffyBackup = Safety.Under(Path.Combine(record.Root, "Backup"), file.Path);
                if (!File.Exists(fluffyBackup)) CopyExact(Path.Combine(folder, "fluffy-backup", file.BackupStored), fluffyBackup);
                if (stored != null) CopyExact(stored, full); else if (File.Exists(full)) File.Delete(full);
            }
            else if (file.Action == "restored-other") CopyExact(stored, full);
        }

        // Removed Fluffy mods kept by UninstallFluffy, newest first.
        internal List<FluffyRemoved> ListFluffyRemoved()
        {
            var result = new List<FluffyRemoved>();
            if (!Directory.Exists(FluffyRemovedDir)) return result;
            foreach (string folder in Directory.GetDirectories(FluffyRemovedDir))
            {
                try
                {
                    FluffyRemovedRecord r = Json.Read<FluffyRemovedRecord>(Path.Combine(folder, "record.json"));
                    if (r == null || r.Format != "sf6-fluffy-removed-v1" || r.State != "done") continue;
                    if (r.Files == null) r.Files = new List<FluffyRemovedFile>(); if (r.RemovedDirs == null) r.RemovedDirs = new List<string>();
                    long bytes = Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
                    result.Add(new FluffyRemoved { Folder = folder, Record = r, Bytes = bytes });
                }
                catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); }
            }
            return result.OrderByDescending(r => r.Record.RemovedUtc, StringComparer.Ordinal).ToList();
        }

        // Undo: the mod's files, Fluffy's backups, the crash reporter and its installed.ini section come back.
        internal string RestoreFluffyRemoved(string folder)
        {
            using (Lock())
            {
                GameProcesses.RequireGameClosed("restore it");
                if (GameProcesses.Probe.IsFluffyRunning()) throw new IOException(FluffyRunningReason);
                FluffyRemovedRecord record = Json.Read<FluffyRemovedRecord>(Path.Combine(folder, "record.json"));
                if (record == null || record.Format != "sf6-fluffy-removed-v1" || record.State != "done") throw new IOException("That removed mod is incomplete or was already restored.");
                if (record.Files == null) record.Files = new List<FluffyRemovedFile>(); if (record.RemovedDirs == null) record.RemovedDirs = new List<string>();
                if (!String.Equals(Path.GetFullPath(record.GameRoot ?? "").TrimEnd('\\'), GameRoot, StringComparison.OrdinalIgnoreCase)) throw new IOException("'" + record.ModName + "' was removed from another game folder (" + record.GameRoot + ").");
                string ini = Path.Combine(record.Root, "installed.ini");
                byte[] existing = FluffyInstalls.ReadShared(ini);
                byte[] current = existing ?? new byte[0];
                if (FluffyInstalls.Parse(FluffyInstalls.DecodeIni(current), record.Root).Any(m => String.Equals(m.Section, record.Section, StringComparison.Ordinal)))
                    throw new IOException("'" + record.ModName + "' is installed again; nothing was restored.");
                var changed = record.Files.Where(f => f.Action == "deleted" || f.Action == "restored-backup" || f.Action == "restored-other").Where(f => ShaOrNull(GamePath(f.Path)) != f.ShaAfter).Select(f => f.Path).ToList();
                if (changed.Count > 0) throw new IOException("Not restored: these files changed since '" + record.ModName + "' was uninstalled: " + String.Join(", ", changed.Take(5)) + (changed.Count > 5 ? ", …" : "") + ".");
                // 2.4.2: a PAK install's patch PAKs and game entries must be as the uninstall left them.
                if (record.Rows == null) record.Rows = new List<FluffyRemovedRow>(); if (record.Patches == null) record.Patches = new List<FluffyRemovedPatch>();
                var paksChanged = record.Patches.Where(p => ShaOrNull(GamePath(p.Name)) != p.ShaAfter).Select(p => p.Name).ToList();
                if (paksChanged.Count > 0) throw new IOException("Not restored: " + String.Join(", ", paksChanged.Take(3)) + " changed since '" + record.ModName + "' was uninstalled (another mod's PAK may use that number now). Nothing was changed.");
                string rowsChanged = FluffyRowsChanged(record);
                if (rowsChanged != null) throw new IOException("Not restored: " + rowsChanged + " since '" + record.ModName + "' was uninstalled. Nothing was changed; reinstall it in the external mod manager instead.");
                foreach (string dir in record.RemovedDirs.AsEnumerable().Reverse()) Directory.CreateDirectory(GamePath(dir));
                foreach (FluffyRemovedFile file in record.Files) UndoFluffyFile(record, folder, file);
                if (record.CrashRestored && File.Exists(GamePath(CrashReporter.Name)) && !File.Exists(GamePath(CrashReporter.RenamedName))) File.Move(GamePath(CrashReporter.Name), GamePath(CrashReporter.RenamedName));
                if (record.CrashTakenByManager) { ReplaceState s = LoadState(); if (s.CrashReporterRenamed) { s.CrashReporterRenamed = false; s.Generation++; DurableFile.WriteJson(StatePath, s); } }
                // Files first, then the patch PAKs and the entries (zeroed again only once the loose files are back).
                foreach (FluffyRemovedPatch patch in record.Patches) RestoreFluffyPatch(folder, patch);
                if (record.Rows.Count > 0) WriteFluffyRows(record.Rows, true);
                // The section goes back where it was when installed.ini is unchanged since, else at the end.
                byte[] section = Convert.FromBase64String(record.SectionBase64);
                byte[] result;
                string before = Path.Combine(folder, "installed.ini.before");
                if (existing == null && record.IniShaAfter == "" && File.Exists(before) && Sha(File.ReadAllBytes(before)) == record.IniShaBefore) result = File.ReadAllBytes(before);
                else if (current.Length > 0 && Sha(current) == record.IniShaAfter && record.SectionOffset <= current.Length)
                {
                    result = new byte[current.Length + section.Length];
                    Buffer.BlockCopy(current, 0, result, 0, record.SectionOffset);
                    Buffer.BlockCopy(section, 0, result, record.SectionOffset, section.Length);
                    Buffer.BlockCopy(current, record.SectionOffset, result, record.SectionOffset + section.Length, current.Length - record.SectionOffset);
                }
                else
                {
                    string text = new UTF8Encoding(false).GetString(current);
                    byte[] separator = current.Length > 0 && current[current.Length - 1] != (byte)'\n' ? Encoding.ASCII.GetBytes(text.Contains("\r\n") || current.Length == 0 ? "\r\n" : "\n") : new byte[0];
                    result = current.Concat(separator).Concat(section).ToArray();
                }
                if (current.Length > 0)
                {
                    Directory.CreateDirectory(FluffyIniBackupDir);
                    File.WriteAllBytes(Path.Combine(FluffyIniBackupDir, "installed-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".ini"), current);
                }
                DurableFile.Write(ini, result);
                RecordFluffy(HistoryKinds.Recover, record, "files and installed.ini entry");   // 2.4.1 history
                try { Directory.Delete(folder, true); } catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); }
                return "Restored '" + record.ModName + "' (files and installed.ini entry).";
            }
        }

        internal void DeleteFluffyRemoved(string folder)
        {
            string full = Path.GetFullPath(folder);
            if (!IsUnder(full, FluffyRemovedDir)) throw new IOException("Not a removed mod: " + folder);
            if (Directory.Exists(full)) Directory.Delete(full, true);
        }

        // ---- 2.2.0: files kept from other tools and the originals behind them (gear → Recover removed outfits)

        // Puts the genuine game file back for a file kept with "Keep current file" (the other tool's file is kept in Data).
        internal string RestoreReleasedOriginal(string path)
        {
            ReplaceState s = LoadState();
            ReleasedFile r = s.Release(path);
            if (r == null) throw new IOException(path + " is not a kept file any more. Refresh the list.");
            if (r.Original == null) throw new IOException(path + " didn't exist before the Manager installed it: there is no original to restore.");
            ReplaceMod owner = s.Mods.FirstOrDefault(m => m.Enabled && m.Files.Any(f => String.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase)));
            if (owner != null) throw new IOException("'" + owner.Name + "' still installs " + path + ": uninstall it first (or Reapply to put its copy back).");
            return Change("Restore original game file", true, delegate(ReplaceState state) { return state; }, new ChangeOptions { Choice = DriftChoice.Refuse, RestoreReleased = new HashSet<string>(new[] { path }, StringComparer.OrdinalIgnoreCase) }, null, null);
        }

        // Forgets a kept file: the other tool's file stays, the original's backup is deleted.
        internal void DropReleased(string path)
        {
            using (Lock())
            {
                ReplaceState s = LoadState();
                ReleasedFile r = s.Release(path);
                if (r == null) return;
                s.Released.Remove(r); s.Generation++; DurableFile.WriteJson(StatePath, s);
                if (r.Original != null && IsUnder(r.Original.Backup, DataDir)) { try { File.Delete(r.Original.Backup); } catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); } }
            }
        }

        // Puts another tool's kept file back into the game folder (only where this app installs nothing).
        internal string RestoreForeignCopy(string copy)
        {
            using (Lock())
            {
                GameProcesses.RequireGameClosed("put a kept file back");
                ReplaceState s = LoadState();
                ForeignCopy c = s.ForeignCopies.FirstOrDefault(x => String.Equals(x.Copy, copy, StringComparison.OrdinalIgnoreCase));
                if (c == null) throw new IOException("That kept file is no longer listed. Refresh the list.");
                if (s.Deployment(c.Path) != null || s.Release(c.Path) != null) throw new IOException("The Manager tracks " + c.Path + " again: uninstall that replacement first.");
                if (!File.Exists(c.Copy) || Sha(c.Copy) != c.Sha256) throw new IOException("The kept copy of " + c.Path + " is missing or changed: " + c.Copy);
                string full = GamePath(c.Path);
                Safety.NoReparseChain(full);
                string now = ShaOrNull(full);
                if (now != null && now != c.Sha256)
                {
                    string keep = Path.Combine(BackupDir, "changed-" + Guid.NewGuid().ToString("N") + ".bin");
                    Directory.CreateDirectory(BackupDir); File.Copy(full, keep);
                    s.ForeignCopies.Add(new ForeignCopy { Path = c.Path, Copy = keep, Sha256 = now, Size = new FileInfo(keep).Length, Changer = "the file in place before a kept copy was put back", KeptUtc = DateTime.UtcNow.ToString("o") });
                }
                CopyExact(c.Copy, full);
                s.ForeignCopies.Remove(c); s.Generation++; DurableFile.WriteJson(StatePath, s);
                try { File.Delete(c.Copy); } catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); }
                return "Put " + c.Path + " back as " + c.Changer + " left it.";
            }
        }

        internal void DeleteForeignCopy(string copy)
        {
            using (Lock())
            {
                ReplaceState s = LoadState();
                ForeignCopy c = s.ForeignCopies.FirstOrDefault(x => String.Equals(x.Copy, copy, StringComparison.OrdinalIgnoreCase));
                if (c == null) return;
                s.ForeignCopies.Remove(c); s.Generation++; DurableFile.WriteJson(StatePath, s);
                if (IsUnder(c.Copy, DataDir)) { try { File.Delete(c.Copy); } catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); } }
            }
        }
    }
}
