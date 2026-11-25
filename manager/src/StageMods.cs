using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace SlotsInstaller
{
    // 25 Nov 2026 (the user: "since we use wael's slotsloader now ... we can install stages as extra"): the suite-line
    // Slots Loader (Wael3rd/SF6-ModSuite, stages/README.md) reads stage mods from reframework\stage_mods\ and makes each
    // option a variant of the stage it changes (UP / DOWN on the stage select) or a stage of its own (a "tile"), with the
    // original kept. Extra stage puts the ticked options of a stage mod there as one folder per mod, one sub-folder per
    // option (a modinfo.ini and its natives tree or .pak each: the loader's collect_options reads a folder holding several
    // options as one option per sub-folder). The folder's ".slotsloader-manager.json" (the loader skips nothing inside a
    // mod's folder, but a dot file is no option) says the Manager made it: only such folders are listed and uninstalled.
    public sealed class StageInstallRecord
    {
        public string Format = StageMods.RecordFormat;
        public string Name, SourcePath, InstalledUtc;
        public List<string> OptionIds = new List<string>(), OptionNames = new List<string>();
        public int Files;
        public long Bytes;
    }

    internal sealed class StageInstall
    {
        internal string Folder;
        internal StageInstallRecord Record;
        internal string Name { get { return String.IsNullOrWhiteSpace(Record.Name) ? Path.GetFileName(Folder) : Record.Name; } }
        internal DateTime InstalledUtc
        {
            get { DateTime t; return DateTime.TryParse(Record.InstalledUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out t) ? t.ToUniversalTime() : DateTime.MinValue; }
        }
    }

    // What an opened mod is to Extra stage: the stages it changes (ModCategories.StageKey, "other"), read from its natives
    // paths or, for a mod that is only .pak files, from the PAK's path hashes against the loader's stage_paths.txt.
    internal sealed class StageInfo
    {
        internal List<string> Keys = new List<string>();
        internal bool FromPak;
    }

    internal static class StageMods
    {
        internal const string RecordFormat = "slotsloader-stage-install-v1";
        internal const string MarkerName = ".slotsloader-manager.json";
        internal const string FolderRelative = @"reframework\stage_mods";
        internal const string StagePathsRelative = @"reframework\data\SF6_StageSlots_Data\loader\stage_paths.txt";
        internal const string LoaderDll = "amd_ags_x64.dll";
        private const long LoaderDllMinimum = 200 * 1024;   // the suite's proxy is ~1 MB; AMD's own is ~60 KB (ToolStatus uses the same line)
        private const int PakReadLimit = 256 * 1024 * 1024;

        // The game folder the library listing classifies PAK mods against (MainForm sets it; null in the command line).
        internal static Func<string> LibraryGameRoot;

        internal static string Root(string gameRoot) { return Path.Combine(gameRoot ?? "", FolderRelative); }

        // The suite-line loader is in the game folder: its stage data and its proxy DLL. Off without them (Extra stage
        // then says why; the fork never reads stage_mods).
        internal static bool LoaderReady(string gameRoot)
        {
            if (String.IsNullOrWhiteSpace(gameRoot)) return false;
            try
            {
                var dll = new FileInfo(Path.Combine(gameRoot, LoaderDll));
                return File.Exists(Path.Combine(gameRoot, StagePathsRelative)) && dll.Exists && dll.Length > LoaderDllMinimum;
            }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; return false; }
        }

        // ---- The loader's stage path list: hash -> stage key
        private static readonly object TableLock = new object();
        private static string tableKey;
        private static Dictionary<ulong, string> table;

        // Null when the game folder has no stage_paths.txt (the suite loader is not installed). Read again when it changes.
        internal static Dictionary<ulong, string> Table(string gameRoot)
        {
            if (String.IsNullOrWhiteSpace(gameRoot)) return null;
            string path = Path.Combine(gameRoot, StagePathsRelative);
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) return null;
                string key = info.FullName + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks;
                lock (TableLock)
                {
                    if (key == tableKey) return table;
                    var read = new Dictionary<ulong, string>();
                    foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
                    {
                        string line = raw.Trim().Replace('\\', '/');
                        if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
                        string after = line.StartsWith("natives/", StringComparison.OrdinalIgnoreCase) ? line.Substring(8) : line;
                        string stage = ModCategories.StageOf(after, false) ?? ModCategories.OtherStage;
                        read[PakHash.PathHash(line.StartsWith("natives/", StringComparison.OrdinalIgnoreCase) ? line : "natives/" + line)] = stage;
                    }
                    table = read; tableKey = key;
                    return table;
                }
            }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); return null; }
        }
        internal static Dictionary<ulong, string> LibraryTable()
        {
            Func<string> root = LibraryGameRoot;
            return root == null ? null : Table(root());
        }

        // The path hashes of a KPKA v4 PAK held in memory (header and entry table only); empty for an encrypted or
        // extended table, which the Manager does not read.
        internal static List<ulong> PakHashes(byte[] pak)
        {
            var result = new List<ulong>();
            if (pak == null || pak.Length < PakIndex.HeaderSize || pak[0] != (byte)'K' || pak[1] != (byte)'P' || pak[2] != (byte)'K' || pak[3] != (byte)'A') return result;
            if (pak[4] != 4 || BitConverter.ToUInt16(pak, 6) != 0) return result;
            long count = BitConverter.ToUInt32(pak, 8);
            if (PakIndex.HeaderSize + count * PakIndex.EntrySize > pak.Length) return result;
            for (int i = 0; i < count; i++)
            {
                ulong h = BitConverter.ToUInt64(pak, PakIndex.HeaderSize + i * PakIndex.EntrySize);
                if (h != 0) result.Add(h);
            }
            return result;
        }
        internal static List<string> PakStages(byte[] pak, Dictionary<ulong, string> stages)
        {
            if (stages == null) return new List<string>();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (ulong h in PakHashes(pak)) { string key; if (stages.TryGetValue(h, out key)) keys.Add(key); }
            return Sorted(keys);
        }
        internal static List<string> Sorted(IEnumerable<string> keys) { return keys.Distinct(StringComparer.Ordinal).OrderBy(s => s == ModCategories.OtherStage ? "~" : s, StringComparer.Ordinal).ToList(); }

        // The stages of the .pak files of a library listing (entries outside natives); empty when none is a stage PAK.
        internal static List<string> PakStagesOf(SourceReader reader, Dictionary<ulong, string> stages)
        {
            var keys = new List<string>();
            if (reader == null || stages == null) return keys;
            foreach (SourceEntry e in reader.Entries.Values.Where(e => e.Path.EndsWith(".pak", StringComparison.OrdinalIgnoreCase) && e.Size <= PakReadLimit && !e.Path.Split('/').Any(p => p.Equals("natives", StringComparison.OrdinalIgnoreCase) || p.Equals("__MACOSX", StringComparison.OrdinalIgnoreCase))).ToList())
            {
                OperationContext.ThrowIfCancellationRequested();
                try { keys.AddRange(PakStages(reader.ReadBytes(e.Path, PakReadLimit), stages)); }
                catch (Exception ex) { if (ex is OutOfMemoryException || ex is OperationCanceledException) throw; Program.Log(ex); }
            }
            return Sorted(keys);
        }

        // An opened mod as a stage mod, else null: no fighter files and stage content (natives paths of a stage, or a mod
        // of .pak files only whose PAKs hold stage paths). A mod shown with its addons is never one. `reader`: needed only
        // for a PAK mod (its PAKs are read); null leaves a PAK mod undecided (null).
        internal static StageInfo Detect(ModPackage p, SourceReader reader, string gameRoot)
        {
            if (p == null || p.MergedFrom != null || (p.CharacterCodes != null && p.CharacterCodes.Count > 0)) return null;
            var real = p.Components.Where(c => !c.IsDummy).ToList();
            List<NativeAsset> natives = real.SelectMany(c => c.Assets).ToList(), roots = real.SelectMany(c => c.RootAssets).ToList();
            if (natives.Count == 0 && roots.Count == 0) return null;
            // Game-folder files other than .pak (a script, a plugin): a tool mod, never a stage.
            if (roots.Any(a => !ReplaceEngine.IsModPak(a.DestinationPath))) return null;
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (NativeAsset a in natives)
            {
                string after = a.DestinationPath.StartsWith("natives/", StringComparison.OrdinalIgnoreCase) ? a.DestinationPath.Substring(8) : a.DestinationPath;
                if (Library.FighterCodes("/" + after).Any() || ModCategories.FighterArtCode(after) != null) return null;
                string stage = ModCategories.StageOf(after, false);
                if (stage != null) keys.Add(stage);
            }
            bool fromPak = false;
            if (roots.Count > 0 && reader != null)
            {
                Dictionary<ulong, string> stages = Table(gameRoot);
                foreach (NativeAsset a in roots)
                {
                    SourceEntry e;
                    if (!reader.Entries.TryGetValue(a.EntryPath, out e) || e.Size > PakReadLimit) continue;
                    try { foreach (string k in PakStages(reader.ReadBytes(a.EntryPath, PakReadLimit), stages)) { keys.Add(k); fromPak = true; } }
                    catch (Exception ex) { if (ex is OutOfMemoryException || ex is OperationCanceledException) throw; Program.Log(ex); }
                }
            }
            return keys.Count == 0 ? null : new StageInfo { Keys = Sorted(keys), FromPak = fromPak };
        }

        // The options an install can take: ticked, not a menu title, with files (natives, or the .pak files of a PAK mod).
        internal static List<ModComponent> Options(ModPackage p, IEnumerable<string> ids)
        {
            var wanted = new HashSet<string>(ids ?? new string[0], StringComparer.OrdinalIgnoreCase);
            return p.Components.Where(c => wanted.Contains(c.Id) && !c.IsDummy && (c.Assets.Count > 0 || c.RootAssets.Any(a => ReplaceEngine.IsModPak(a.DestinationPath)))).ToList();
        }

        // ---- Installed stages
        internal static List<StageInstall> List(string gameRoot)
        {
            var result = new List<StageInstall>();
            if (String.IsNullOrWhiteSpace(gameRoot)) return result;
            string root = Root(gameRoot);
            try
            {
                if (!Directory.Exists(root)) return result;
                foreach (string dir in Directory.EnumerateDirectories(root))
                {
                    string name = Path.GetFileName(dir);
                    if (name.StartsWith(".")) continue;
                    StageInstallRecord record = ReadRecord(dir);
                    if (record != null) result.Add(new StageInstall { Folder = Path.GetFullPath(dir), Record = record });
                }
            }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); }
            return result.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
        private static StageInstallRecord ReadRecord(string folder)
        {
            try
            {
                string marker = Path.Combine(folder, MarkerName);
                if (!File.Exists(marker)) return null;
                Safety.NoReparse(folder);
                var r = Json.Read<StageInstallRecord>(marker);
                if (r == null || r.Format != RecordFormat) return null;
                if (r.OptionIds == null) r.OptionIds = new List<string>();
                if (r.OptionNames == null) r.OptionNames = new List<string>();
                return r;
            }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); return null; }
        }
        internal static List<StageInstall> Of(string gameRoot, string source)
        {
            if (String.IsNullOrWhiteSpace(source)) return new List<StageInstall>();
            return List(gameRoot).Where(s => SamePath(s.Record.SourcePath, source)).ToList();
        }
        private static bool SamePath(string a, string b)
        {
            if (String.IsNullOrWhiteSpace(a) || String.IsNullOrWhiteSpace(b)) return false;
            try { return String.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; return false; }
        }

        // A folder or file name from a mod or option name: what Windows refuses becomes a space, runs of spaces one,
        // no dot or space at the end, at most 60 characters; "Stage mod" when nothing is left.
        internal static string FolderName(string name, string fallback)
        {
            string s = Regex.Replace(new string((name ?? "").Select(ch => ch < 32 || Path.GetInvalidFileNameChars().Contains(ch) ? ' ' : ch).ToArray()), @"\s+", " ").Trim();
            if (s.Length > 60) s = s.Substring(0, 60).Trim();
            s = s.TrimEnd('.', ' ').TrimStart('.', ' ');
            if (s.Length == 0 || Regex.IsMatch(s, @"^(con|prn|aux|nul|com[0-9]|lpt[0-9])(?:\.|$)", RegexOptions.IgnoreCase)) s = fallback;
            return s;
        }
        private static string IniValue(string s) { return Regex.Replace(s ?? "", @"[\r\n]+", " ").Trim(); }

        // Installs the ticked options as stage_mods\<mod name>\<option>\: each option's natives tree or .pak, its preview
        // picture (screenshot=) and a modinfo.ini naming it (a mod of one option is named after the mod). The same source
        // installed before is replaced in its own folder (its variant choices in the game keep their place). Everything
        // is written into a dot folder first (the loader skips those), then moved into place.
        internal static InstallResult Install(ModPackage p, IEnumerable<string> selectedIds, string gameRoot, string sevenZip, string modName)
        {
            if (p == null) throw new IOException("Open a stage mod first.");
            if (!LoaderReady(gameRoot)) throw new IOException("Extra stage needs Wael's Slots Loader in the game folder (its stage data was not found). Install it, or use Replace original.");
            List<ModComponent> options = Options(p, selectedIds);
            if (options.Count == 0) throw new IOException("Tick at least one option to install.");
            string root = Root(gameRoot);
            Directory.CreateDirectory(root);
            Safety.NoReparseChain(root);
            string name = FolderName(String.IsNullOrWhiteSpace(modName) ? p.Name : modName, "Stage mod");
            List<StageInstall> mine = Of(gameRoot, p.SourcePath);
            if (mine.Count > 1) throw new IOException("More than one stage folder came from this mod. Uninstall the extra ones on the Installed page first.");
            StageInstall previous = mine.SingleOrDefault();
            string dest = previous != null ? previous.Folder : Free(root, name);
            string temp = Safety.NewFolder(root, ".stage-");
            string scratch = Safety.NewFolder(Path.GetFullPath(Path.GetTempPath()), "slm-stage-");
            string old = null;
            try
            {
                var planned = new List<PlannedFile>(); var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var infos = new List<KeyValuePair<string, ModComponent>>();
                using (var reader = new SourceReader(p.SourcePath, sevenZip))
                {
                    foreach (ModComponent c in options)
                    {
                        string folder = FolderName(options.Count == 1 ? name : c.Name, "Option");
                        for (int n = 2; !folders.Add(folder); n++) folder = FolderName((options.Count == 1 ? name : c.Name), "Option") + " (" + n + ")";
                        foreach (NativeAsset a in c.Assets)
                            planned.Add(new PlannedFile { Component = c, Asset = new NativeAsset { EntryPath = a.EntryPath, DestinationPath = folder + "/" + a.DestinationPath, Size = a.Size, Crc = a.Crc } });
                        foreach (NativeAsset a in c.RootAssets.Where(a => ReplaceEngine.IsModPak(a.DestinationPath)))
                            planned.Add(new PlannedFile { Component = c, Asset = new NativeAsset { EntryPath = a.EntryPath, DestinationPath = folder + "/" + Path.GetFileName(a.DestinationPath.Replace('/', '\\')), Size = a.Size, Crc = a.Crc } });
                        string picture = null; SourceEntry preview;
                        if (!String.IsNullOrEmpty(c.PreviewEntry) && reader.Entries.TryGetValue(c.PreviewEntry, out preview))
                        {
                            picture = "screenshot" + Path.GetExtension(preview.Path).ToLowerInvariant();
                            planned.Add(new PlannedFile { Component = c, Asset = new NativeAsset { EntryPath = preview.Path, DestinationPath = folder + "/" + picture, Size = preview.Size, Crc = preview.ZipChecksum ?? preview.ListedCrc } });
                        }
                        infos.Add(new KeyValuePair<string, ModComponent>(folder + "|" + (picture ?? ""), c));
                    }
                    // A path two options both write would be one file: refused (never seen; options live in their own folders).
                    var twice = planned.GroupBy(f => f.Asset.DestinationPath, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
                    if (twice != null) throw new IOException("Two files of this mod would land on " + twice.Key + ".");
                    reader.CopyFiles(planned, temp, scratch);
                }
                // 25 Nov 2026 (PakModFixes.cs, the Aokigahara session): documented outdated type signatures in the copied
                // .pak files and loose scenes are moved to the game's current one, only where a game file confirms it.
                // Without readable game files (a fixture, a moved game) nothing is fixed and the install goes on.
                List<FixResult> fixes = new List<FixResult>();
                GameReference reference = null;
                try { reference = PakModFixes.Reference(gameRoot); }
                catch (Exception ex) { if (ex is OutOfMemoryException || ex is OperationCanceledException) throw; Program.Log(ex); }
                if (reference != null) fixes = PakModFixes.FixFolder(temp, reference);
                foreach (var info in infos)
                {
                    string[] parts = info.Key.Split('|'); ModComponent c = info.Value;
                    var ini = new StringBuilder();
                    ini.Append("name=").Append(IniValue(options.Count == 1 ? name : c.Name)).Append("\r\n");
                    if (!String.IsNullOrWhiteSpace(c.Author)) ini.Append("author=").Append(IniValue(c.Author)).Append("\r\n");
                    if (!String.IsNullOrWhiteSpace(c.Version)) ini.Append("version=").Append(IniValue(c.Version)).Append("\r\n");
                    if (!String.IsNullOrWhiteSpace(c.Description)) ini.Append("description=").Append(IniValue(c.Description)).Append("\r\n");
                    if (parts[1].Length > 0) ini.Append("screenshot=").Append(parts[1]).Append("\r\n");
                    ini.Append("nameAsBundle=").Append(IniValue(name)).Append("\r\n");
                    File.WriteAllText(Safety.Under(temp, parts[0] + "/modinfo.ini"), ini.ToString(), new UTF8Encoding(false));
                }
                long bytes = Directory.GetFiles(temp, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
                var record = new StageInstallRecord { Name = name, SourcePath = Path.GetFullPath(p.SourcePath), InstalledUtc = DateTime.UtcNow.ToString("o"),
                    OptionIds = options.Select(c => c.Id).ToList(), OptionNames = options.Select(c => c.Name).ToList(), Files = planned.Count, Bytes = bytes };
                Json.Write(Path.Combine(temp, MarkerName), record);
                OperationContext.ThrowIfCancellationRequested();
                using (OperationContext.NonCancellable())
                {
                    if (previous != null && Directory.Exists(dest))
                    {
                        if (ReadRecord(dest) == null) throw new IOException(Path.GetFileName(dest) + " in stage_mods is no longer the Manager's. Rename it first.");
                        old = Safety.Under(root, ".old-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                        Installer.MoveDirectory(dest, old);
                    }
                    try { Installer.MoveDirectory(temp, dest); }
                    catch { if (old != null && !Directory.Exists(dest)) { Installer.MoveDirectory(old, dest); old = null; } throw; }
                    temp = null;
                }
                string what = options.Count == 1 ? "1 variant" : options.Count + " variants";
                string fixNote = String.Concat(fixes.Where(x => x.Changed || x.Warning).Select(x => " " + x.Message.TrimEnd('.') + "."));
                return new InstallResult { Folder = dest, FileCount = planned.Count, Bytes = bytes, PartsNote = fixNote.Trim(),
                    Message = (previous != null ? "Updated " : "Installed ") + name + " as an extra stage (" + what + ")." + fixNote + " Restart SF6, then pick it with UP / DOWN on the stage select." };
            }
            finally
            {
                if (temp != null) try { Safety.DeleteGenerated(temp, root); } catch (Exception ex) { Program.Log(ex); }
                if (old != null) try { Safety.DeleteGenerated(old, root); } catch (Exception ex) { Program.Log(ex); }
                try { Safety.DeleteGenerated(scratch, Path.GetFullPath(Path.GetTempPath())); } catch { }
            }
        }
        private static string Free(string root, string name)
        {
            for (int n = 1; n < 100; n++)
            {
                string candidate = Safety.Under(root, n == 1 ? name : name + " (" + n + ")");
                if (!Directory.Exists(candidate) && !File.Exists(candidate)) return candidate;
            }
            throw new IOException("stage_mods already has many folders named " + name + ".");
        }

        // Deletes one folder the Manager made (its record must still be there). Nothing else in stage_mods changes.
        internal static string Uninstall(string gameRoot, string folder)
        {
            string root = Root(gameRoot);
            string full = Path.GetFullPath(folder);
            if (!String.Equals(Path.GetDirectoryName(full.TrimEnd(Path.DirectorySeparatorChar)), Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                throw new IOException("That folder is not in the game's stage_mods folder.");
            if (!Directory.Exists(full)) return "It was already gone.";
            StageInstallRecord record = ReadRecord(full);
            if (record == null) throw new IOException(Path.GetFileName(full) + " was not installed by the Manager, so it is left as it is.");
            Safety.DeleteGenerated(full, root);
            return "";
        }
    }
}
