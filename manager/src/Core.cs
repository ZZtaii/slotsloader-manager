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
    public sealed class AppConfig
    {
        // 2.4.3: no compiled-in folders. A first start (no settings.json) detects the game (Steam) and the Fluffy
        // libraries; an empty game folder means Settings asks for it. Saved settings always store both explicitly.
        public string GameRoot = "";
        public List<string> Libraries = new List<string>();
        public string SevenZipPath = "";   // 1.0.11: empty = SevenZipTool finds one (installed, then the bundled copy)
        public string CostumeRoot { get { return String.IsNullOrWhiteSpace(GameRoot) ? "" : Path.Combine(GameRoot, "reframework", "costume_mods"); } }
        public static string DataDir { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data"); } }
        public static AppConfig Load()
        {
            string p = Path.Combine(DataDir, "settings.json");
            if (!File.Exists(p)) return Detected();
            AppConfig c = Json.Read<AppConfig>(p);
            if (c == null) throw new IOException("Settings are unreadable. Move Data/settings.json aside to reset them.");
            if (c.Libraries == null) c.Libraries = new List<string>();
            if (c.GameRoot == null) c.GameRoot = "";
            // 1.0.11 (PR #12 review): up to 1.0.10 this path was the default saved in every settings.json, never a choice
            // of the user's. Read as Automatic, so an installed 7-Zip older than the bundled copy gives way to it (RAR5).
            if (String.Equals((c.SevenZipPath ?? "").Trim(), OldDefaultSevenZip, StringComparison.OrdinalIgnoreCase)) c.SevenZipPath = "";
            if (LinuxPlatform.IsLinux)
            {
                // Register storage and trash scope on every launch, without rewriting the
                // saved paths or silently migrating an existing replacement record.
                foreach (string path in new[] { c.GameRoot }.Concat(c.Libraries))
                {
                    if (String.IsNullOrWhiteSpace(path)) continue;
                    try { LinuxPlatform.CanonicalRoot(path, path == c.GameRoot ? "game" : "library"); }
                    catch (IOException ex) { Program.LogLine(ex.Message); }
                }
            }
            return c;
        }
        internal const string OldDefaultSevenZip = @"C:\Program Files\7-Zip\7z.exe";
        // Set by test.ps1 (SLOTSLOADER_NO_DETECT) so a check never picks up the real machine's folders.
        internal static bool DetectionOff { get { return Environment.GetEnvironmentVariable("SLOTSLOADER_NO_DETECT") == "1"; } }
        internal static AppConfig Detected()
        {
            var c = new AppConfig();
            if (DetectionOff) return c;
            try { c.GameRoot = InstallDetection.FindGame() ?? ""; } catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); }
            try { c.Libraries = InstallDetection.FindLibraries(); } catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); }
            return c;
        }
        public void Save()
        {
            if (LinuxPlatform.IsLinux)
            {
                string game = LinuxPlatform.CanonicalRoot(GameRoot, "game");
                List<string> libraries = Libraries.Select(p => LinuxPlatform.CanonicalRoot(p, "library")).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                GameRoot = game; Libraries = libraries;
            }
            Directory.CreateDirectory(DataDir); Json.Write(Path.Combine(DataDir, "settings.json"), this);
        }
    }
    internal static class Json
    {
        internal static JavaScriptSerializer Serializer() { return new JavaScriptSerializer { MaxJsonLength = 64 * 1024 * 1024 }; }
        internal static T Read<T>(string p) { return Serializer().Deserialize<T>(File.ReadAllText(p, Encoding.UTF8)); }
        internal static void Write(string p, object o)
        {
            string tmp = p + "." + Guid.NewGuid().ToString("N") + ".tmp";
            bool complete = false;
            try
            {
                File.WriteAllText(tmp, Serializer().Serialize(o), new UTF8Encoding(false));
                complete = true;
                // 1.0.11: as DurableFile.Write: a scanner or another reader holding the target without Delete sharing makes the
                // replace fail for a moment ("Unable to remove the file to be replaced"), so it is tried again briefly; a
                // temporary file is no longer left behind (DurableFile.Discard).
                DurableFile.Publish(tmp, p);
            }
            finally { DurableFile.Discard(tmp, p, complete); }
        }
        internal static void WriteNew(string p, object o)
        {
            string tmp = p + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(tmp, Serializer().Serialize(o), new UTF8Encoding(false));
                File.Move(tmp, p); // Atomic publication refuses a concurrently-created report.
            }
            finally { if (File.Exists(tmp)) File.Delete(tmp); }
        }
    }
    public static class Characters
    {
        public static readonly Dictionary<string, string> ByCode = new Dictionary<string, string> {
            {"001","Ryu"},{"002","Luke"},{"003","Kimberly"},{"004","Chun-Li"},{"005","Manon"},{"006","Zangief"},
            {"007","JP"},{"008","Dhalsim"},{"009","Cammy"},{"010","Ken"},{"011","Dee Jay"},{"012","Lily"},
            {"013","A.K.I"},{"014","Rashid"},{"015","Blanka"},{"016","Juri"},{"017","Marisa"},{"018","Guile"},
            {"019","Ed"},{"020","E. Honda"},{"021","Jamie"},{"022","Akuma"},{"025","Sagat"},{"026","M. Bison"},
            {"027","Terry"},{"028","Mai"},{"029","Elena"},{"030","C. Viper"},{"031","Alex"},{"032","Ingrid"},{"033","Yasmine"},
            {"034","Arjun"}
        };
        public static List<string> Names { get { return ByCode.Values.Distinct().OrderBy(x => x).ToList(); } }
        // 1.0.11: the patterns are built once. Built per call (32 fighters, more than the 15 patterns .NET keeps), every
        // regex was parsed again for every library file name: 0.1 ms a name, 300 ms of a 2,700-mod start-up listing.
        // Same order and matches as before: longest name first, whole words; the plain text check only skips regexes
        // that cannot match.
        private static readonly Regex NonAlphanumeric = new Regex("[^a-z0-9]+");
        private static readonly Tuple<string, string, Regex>[] GuessOrder = Names.OrderByDescending(n => n.Length).Select(n =>
        {
            string normalized = NonAlphanumeric.Replace(n.ToLowerInvariant(), " ").Trim();
            return Tuple.Create(n, normalized, new Regex(@"\b" + Regex.Escape(normalized) + @"\b"));
        }).ToArray();
        private static readonly Regex ViperWord = new Regex(@"\b(viper|cviper)\b"), ChunLiWord = new Regex(@"\b(chunli|chun li)\b"), AkiWord = new Regex(@"\b(aki|a k i)\b"), HondaWord = new Regex(@"\bhonda\b"), BisonWord = new Regex(@"\bbison\b");
        public static string Guess(string text)
        {
            string t = NonAlphanumeric.Replace(text.ToLowerInvariant(), " ");
            foreach (var n in GuessOrder) if (t.Contains(n.Item2) && n.Item3.IsMatch(t)) return n.Item1;
            if (ViperWord.IsMatch(t)) return "C. Viper";
            if (ChunLiWord.IsMatch(t)) return "Chun-Li";
            if (AkiWord.IsMatch(t)) return "A.K.I";
            if (HondaWord.IsMatch(t)) return "E. Honda";
            if (BisonWord.IsMatch(t)) return "M. Bison";
            return "";
        }
        // 1.0.2: a folder name that is only a fighter ("Cammy", "chun-li mods", "Viper outfits"): Library.IsGroupFolder.
        internal static bool IsGroupName(string name)
        {
            string t = Regex.Replace((name ?? "").ToLowerInvariant(), "[^a-z0-9]+", " ").Trim();
            t = Regex.Replace(t, @"(^| )(mods?|outfits?|costumes?|skins?)$", "").Trim();
            if (t.Length == 0) return false;
            if (Names.Any(n => Regex.Replace(n.ToLowerInvariant(), "[^a-z0-9]+", " ").Trim() == t)) return true;
            return Regex.IsMatch(t, @"^(viper|cviper|chunli|aki|honda|ehonda|bison|mbison|deejay)$");
        }
    }
    public sealed class LibraryEntry
    {
        public string SourcePath, Name, Character, Kind;
        public long Size;
        public int OptionCount = -1;
        public List<string> OptionIds = new List<string>();
        public List<string> CharacterCodes = new List<string>();
        // Costume folders the mod's native files touch: "009/001" for natives/stm/(streaming/)product/model/esf/esf009/001/...
        // "009/000" is the character's shared parts (used by every outfit).
        public List<string> CostumeKeys = new List<string>();
        // Files outside natives (2.0.3): "script" (reframework/autorun/**.lua), "plugin" (reframework/plugins/**.dll),
        // "dll" (any other DLL) and "reframework" (other REFramework files), in that order. See LibrarySearch.IsToolMod.
        public List<string> ToolKinds = new List<string>();
        public bool? HasPakPayload; // null = older index: backfill without discarding known identity
        // 2.3.0 categories (ModCategories): stage numbers of the native files ("0000" = ess0000, Training Room;
        // "other" = stage folders without a code) and the Misc labels ("UI", "Sound", "VFX", ...).
        public List<string> StageIds = new List<string>();
        public List<string> MiscAreas = new List<string>();
        public string Detection, DetectionError, FilenameCharacter;
        // 2.4.1 "Added" sort: when the mod arrived in the library (Library.AddedTime: the archive/folder creation
        // time, or its last write time when the creation time is missing or later). MinValue = unknown.
        public DateTime AddedUtc;
        // 2.5.9: the author= names of the mod's modinfo.ini files, in path order, without duplicates (empty = none).
        public List<string> Authors = new List<string>();
        // 1.0.2: the configured library the entry was listed from (no trailing separator). A mod inside a group folder
        // (Mods\Cammy\mod.zip, Library.IsGroupFolder) still belongs to Mods. Null for entries made outside a scan.
        public string LibraryRoot;
        internal string LibraryFolder { get { return !String.IsNullOrEmpty(LibraryRoot) ? LibraryRoot : Path.GetDirectoryName(SourcePath); } }
        // 1.0.6: the mod's own name from its modinfo.ini (Library.ModInfoName; "" = none, the file name stands in) and
        // the name= of every modinfo.ini (option names, for deep search).
        public string DisplayName = "";
        public List<string> OptionNames = new List<string>();
        // A name typed in the Name view (LibraryNames, Data\library-names.json); null = none. Not stored in the index.
        internal string CustomName;
        // 1.0.6: listed from the index-less quick scan and not read yet (Library.ScanQuick); the window reads it in the background.
        internal bool Pending;
        // 1.0.6: identity known, modinfo.ini names (display name, option names, authors) not read yet.
        internal bool NamesPending;
        // Name shown in the library: the typed name, else the display name, else the file name; the file name when the
        // list shows file names.
        internal string ShownName(bool fileNames) { return fileNames ? Name : !String.IsNullOrWhiteSpace(CustomName) ? CustomName : String.IsNullOrWhiteSpace(DisplayName) ? Name : DisplayName; }
        // LibrarySearch's compact name/option texts, rebuilt when the strings they came from change.
        internal sealed class SearchCache { internal string Name, DisplayName, CustomName, Source, Character; internal string[] Names, Options, NameTokens, OptionTokens; internal List<string> OptionIds, OptionNames; }
        internal SearchCache Search;
    }
    public sealed class LibraryIdentityCache
    {
        public string Format = Library.CacheFormat;
        public List<LibraryIdentityRecord> Records = new List<LibraryIdentityRecord>();
    }
    public sealed class LibraryIdentityRecord
    {
        public string SourcePath, Stamp;
        public List<string> CharacterCodes = new List<string>();
        public List<string> CostumeKeys, ToolKinds, StageIds, MiscAreas;
        public bool? HasPakPayload;
        public int? OptionCount;
        public List<string> OptionIds;
        // 2.4.1 (still index v4, additive): the entry's Added time, round-trip UTC ("o"). A record without it is not
        // listed again: the next refresh fills it in from the stat the listing already makes.
        public string AddedUtc;
        // 2.5.9 (still index v4, additive): modinfo.ini authors. A record without the list is listed again once.
        public List<string> Authors;
        // 1.0.1 (still index v4, additive): 2 = a file under a fighter folder (/esf/esfNNN/) counts for that fighter only,
        // not for a code in its file name (esf027_*.tex inside esf001 is Ryu's). Multi-fighter records without it are listed again.
        public int CodesVersion;
        // 1.0.6 (still index v4, additive): the modinfo.ini display name ("" = none) and every modinfo.ini name=. A record
        // without them is listed again once.
        public string DisplayName;
        public List<string> OptionNames;
    }
    public enum LibraryInstallState { None, Partial, Full, Disabled }
    public static class Library
    {
        public const int BundleThreshold = 4;
        private static readonly object CacheLock = new object();
        private static readonly Dictionary<string,LibraryIdentityRecord> IdentityCache = new Dictionary<string,LibraryIdentityRecord>(StringComparer.OrdinalIgnoreCase);
        private static readonly Regex NativeCode = new Regex(@"(?:/esf/esf|/esf)(\d{3})(?:/|_)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex CostumeFolder = new Regex(@"/product/model/esf/esf(\d{3})/(\d{3})/", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex FighterFolder = new Regex(@"/esf/esf(\d{3})/", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        internal const int CodesVersion = 3;   // 1.0.5: art for several fighters is UI (relists multi-fighter records once)
        // Fighter codes of one native path ("/stm/..." or "/natives/stm/..."). 1.0.1: a file under a fighter folder
        // (/esf/esfNNN/) is that fighter's only; "esf027_*.tex" inside esf001 no longer adds Terry. Elsewhere a code in
        // a folder or file name counts, as before.
        internal static IEnumerable<string> FighterCodes(string native)
        {
            Match home = FighterFolder.Match(native ?? "");
            if (home.Success) return new[] { home.Groups[1].Value };
            return NativeCode.Matches(native ?? "").Cast<Match>().Select(m => m.Groups[1].Value).ToList();
        }
        private static string cacheDirectory;
        private static bool cacheLoaded, cacheDirty;
        // v2 (2.0.1) adds costume keys, v3 (2.0.3) tool kinds, v4 (2.3.0) stage numbers and Misc labels; older
        // cache files are left for older builds and every archive is listed once again. A current record
        // without one of these lists is also listed again.
        internal const string CacheFormat = "sf6-native-library-index-v4";
        internal const string CacheFilename = "library-native-index-v4.json";
        internal const string ToolScript = "script", ToolPlugin = "plugin", ToolDll = "dll", ToolReframework = "reframework";
        internal static readonly string[] ToolKindOrder = { ToolScript, ToolPlugin, ToolDll, ToolReframework };
        internal static int CacheMisses;

        // Tests and portable hosts can place this optional cache in their own writable data folder.
        public static string IdentityCacheDirectory
        {
            get { lock (CacheLock) return cacheDirectory ?? AppConfig.DataDir; }
            set
            {
                lock (CacheLock)
                {
                    cacheDirectory = String.IsNullOrWhiteSpace(value) ? null : Path.GetFullPath(value);
                    IdentityCache.Clear(); cacheLoaded = false; cacheDirty = false;
                }
            }
        }
        public static void ClearIdentityCache()
        {
            lock (CacheLock)
            {
                IdentityCache.Clear(); cacheLoaded = true; cacheDirty = false;
                string file = Path.Combine(IdentityCacheDirectory, CacheFilename);
                try { Safety.NoReparseChain(file); if (File.Exists(file)) File.Delete(file); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        // 2.4.1 Delete (MainFormDelete.cs): a deleted source leaves the index at once, so no later listing reuses it.
        internal static bool ForgetSource(string path)
        {
            if (String.IsNullOrEmpty(path)) return false;
            EnsureCacheLoaded();
            bool removed;
            lock (CacheLock) { removed = IdentityCache.Remove(path); if (removed) cacheDirty = true; }
            if (removed) SaveIdentityCache();
            return removed;
        }
        private static bool UsableIdentity(LibraryIdentityRecord record, string stamp)
        {
            return record != null && record.Stamp == stamp && record.CharacterCodes != null && record.OptionCount.HasValue && record.OptionCount.Value >= 0
                && record.OptionIds != null && record.OptionIds.Count == record.OptionCount.Value && record.CostumeKeys != null && record.ToolKinds != null
                && record.StageIds != null && record.MiscAreas != null
                && (record.CodesVersion >= CodesVersion || record.CharacterCodes.Count <= 1);
        }
        // 1.0.6: the modinfo.ini names (authors, display name, option names) are read in a second, later pass for RAR/7Z
        // archives; a record can have its identity without them. Records from 1.0.5 have authors but no names.
        internal static bool NamesKnown(LibraryIdentityRecord record) { return record != null && record.Authors != null && record.DisplayName != null && record.OptionNames != null; }
        private static LibraryIdentityRecord CopyIdentity(LibraryIdentityRecord record)
        {
            return new LibraryIdentityRecord { SourcePath = record.SourcePath, Stamp = record.Stamp, CharacterCodes = record.CharacterCodes.ToList(),
                OptionCount = record.OptionCount, OptionIds = record.OptionIds.ToList(), CostumeKeys = record.CostumeKeys.ToList(), ToolKinds = record.ToolKinds.ToList(),
                HasPakPayload = record.HasPakPayload, StageIds = record.StageIds.ToList(), MiscAreas = record.MiscAreas.ToList(), Authors = record.Authors == null ? null : record.Authors.ToList(), AddedUtc = record.AddedUtc, CodesVersion = record.CodesVersion, DisplayName = record.DisplayName, OptionNames = record.OptionNames == null ? null : record.OptionNames.ToList() };
        }
        // Only archive content metadata is reusable. Capture it while the old source still exists; incomplete or
        // older records are conservatively left for the next scan. Folder contents remain uncached.
        internal static LibraryIdentityRecord CaptureRenameIdentity(string source, string sevenZipPath)
        {
            try
            {
                if (Directory.Exists(source)) return null;
                EnsureCacheLoaded(); string stamp = ArchiveStamp(source, sevenZipPath);
                lock (CacheLock)
                {
                    LibraryIdentityRecord record;
                    return IdentityCache.TryGetValue(source, out record) && UsableIdentity(record, stamp) && record.CodesVersion >= CodesVersion ? CopyIdentity(record) : null;
                }
            }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; return null; }
        }
        // Called only after a successful rename. A changed source/tool invalidates the hint rather than assigning
        // stale metadata to the new path. Cache failures never turn a completed file rename into an error.
        internal static void CompleteSourceRename(string from, string to, LibraryIdentityRecord captured, string sevenZipPath)
        {
            try
            {
                EnsureCacheLoaded(); LibraryIdentityRecord moved = null;
                try
                {
                    if (captured != null && String.Equals(captured.SourcePath, from, StringComparison.OrdinalIgnoreCase) && captured.CodesVersion >= CodesVersion
                        && UsableIdentity(captured, ArchiveStamp(to, sevenZipPath)))
                    {
                        moved = CopyIdentity(captured); moved.SourcePath = to;
                        var info = new FileInfo(to); moved.AddedUtc = AddedStamp(AddedTime(info.CreationTimeUtc, info.LastWriteTimeUtc));
                    }
                }
                catch (Exception ex) { if (ex is OutOfMemoryException) throw; }
                lock (CacheLock)
                {
                    bool removed = IdentityCache.Remove(from);
                    removed = IdentityCache.Remove(to) || removed;   // also discard any stale record of an earlier source at this name
                    if (moved != null) IdentityCache[to] = moved;
                    if (removed || moved != null) cacheDirty = true;
                }
                SaveIdentityCache();
            }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); }
        }
        // The UI keeps content metadata after a rename, but guesses based on the outer filename must be rebuilt.
        internal static void RefreshFilenameIdentity(LibraryEntry entry)
        {
            if (entry == null || String.IsNullOrEmpty(entry.SourcePath)) return;
            string name = entry.Kind == "Folder" ? Path.GetFileName(entry.SourcePath.TrimEnd('\\')) : Path.GetFileNameWithoutExtension(entry.SourcePath);
            entry.FilenameCharacter = entry.Detection == "Error" ? "" : Characters.Guess(name);
            if (entry.Detection != "Error" && (entry.CharacterCodes == null || entry.CharacterCodes.Count == 0))
            {
                entry.Character = ModCategories.NameGuessAllowed(entry) ? entry.FilenameCharacter : "";
                entry.Detection = String.IsNullOrWhiteSpace(entry.Character) ? "Unknown" : "Filename";
            }
        }
        public static List<LibraryEntry> Scan(IEnumerable<string> libraries)
        {
            return Scan(libraries, @"C:\Program Files\7-Zip\7z.exe");
        }
        public static List<LibraryEntry> Scan(IEnumerable<string> libraries, string sevenZipPath)
        {
            CancellationToken token = OperationContext.Token;
            Action<OperationProgress> progress = OperationContext.Progress;
            List<LibraryEntry> result = ListEntries(libraries);
            // Only central directories/file names are read. Four independent listings keep a
            // large RAR/7Z library responsive without extracting payloads or parsing every INI.
            int completed = 0; object progressLock = new object(); var progressClock=Stopwatch.StartNew();
            OperationContext.Report("Inspecting mod libraries",0,result.Count);
            Parallel.ForEach(result, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = token }, entry =>
            {
                // OperationContext is thread-local; explicitly carry cancellation into
                // every listing and 7-Zip subprocess without emitting per-file noise.
                using (OperationContext.Begin(token,null))
                {
                    token.ThrowIfCancellationRequested();
                    if (entry.Detection != "Error") DetectIdentity(entry,sevenZipPath);
                }
                lock (progressLock)
                {
                    completed++;
                    if (progress != null && (completed == 1 || completed == result.Count || progressClock.ElapsedMilliseconds >= 100))
                    {
                        progress(new OperationProgress { Stage="Inspecting mod libraries",Completed=completed,Total=result.Count,CanCancel=true });
                        progressClock.Restart();
                    }
                }
            });
            token.ThrowIfCancellationRequested();
            SaveIdentityCache();
            return result.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
        // 1.0.6: the window's quick listing. Every mod comes back at once: the ones the index knows (same archive stamp,
        // complete record) with their identity, the rest (new or changed archives, folders, records from an older Manager)
        // marked Pending with only their file-name guess, listed in `pending` for IndexEntry in the background.
        public static List<LibraryEntry> ScanQuick(IEnumerable<string> libraries, string sevenZipPath, out List<LibraryEntry> pending) { List<LibraryEntry> names; return ScanQuick(libraries, sevenZipPath, out pending, out names); }
        // namesPending: listed with their identity, still without their modinfo.ini names (IndexNames).
        public static List<LibraryEntry> ScanQuick(IEnumerable<string> libraries, string sevenZipPath, out List<LibraryEntry> pending, out List<LibraryEntry> namesPending)
        {
            List<LibraryEntry> result = ListEntries(libraries);
            pending = new List<LibraryEntry>(); namesPending = new List<LibraryEntry>();
            OperationContext.Report("Reading the library index",0,result.Count);
            // 1.0.11: the 7-Zip part of the stamps once per listing (per archive it was most of the index check's time).
            string tool = null;
            try { tool = ToolStamp(sevenZipPath); }
            catch (Exception ex) { if (!IsSourceError(ex)) throw; }
            foreach (LibraryEntry entry in result)
            {
                OperationContext.ThrowIfCancellationRequested();
                if (entry.Detection == "Error") continue;
                if (!ApplyCached(entry, tool)) { MarkPending(entry); pending.Add(entry); }
                else if (!entry.HasPakPayload.HasValue) pending.Add(entry); // retain identity while backfilling the payload fact
                else if (entry.NamesPending) namesPending.Add(entry);
            }
            SaveIdentityCache();   // Added times filled in from this listing
            return result.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
        // The index's record of an archive, when it is still valid for the file on disk. Folders are never cached.
        // toolStamp: ToolStamp's result (null = it failed, nothing is usable).
        private static bool ApplyCached(LibraryEntry entry, string toolStamp)
        {
            if (entry.Kind == "Folder" || toolStamp == null) return false;
            string stamp;
            try { stamp = ArchiveStampWith(entry.SourcePath, toolStamp); }
            catch (Exception ex) { if (!IsSourceError(ex)) throw; return false; }
            LibraryIdentityRecord cached = null;
            lock (CacheLock)
            {
                LibraryIdentityRecord found;
                if (IdentityCache.TryGetValue(entry.SourcePath, out found) && UsableIdentity(found, stamp)) cached = found;
                string added = AddedStamp(entry.AddedUtc);
                if (cached != null && added != null && cached.AddedUtc != added) { cached.AddedUtc = added; cacheDirty = true; }
                if (cached == null) return false;
                entry.OptionCount = cached.OptionCount.Value; entry.OptionIds = new List<string>(cached.OptionIds); entry.CostumeKeys = new List<string>(cached.CostumeKeys); entry.ToolKinds = new List<string>(cached.ToolKinds); entry.StageIds = new List<string>(cached.StageIds); entry.MiscAreas = new List<string>(cached.MiscAreas); entry.HasPakPayload = cached.HasPakPayload;
                if (NamesKnown(cached)) { entry.Authors = new List<string>(cached.Authors); entry.DisplayName = cached.DisplayName; entry.OptionNames = new List<string>(cached.OptionNames); entry.NamesPending = false; }
                else entry.NamesPending = true;
                ApplyCodes(entry, new List<string>(cached.CharacterCodes));
            }
            return true;
        }
        // Until it is read: the file-name fighter (as for a mod without fighter files), no options or kinds yet.
        private static void MarkPending(LibraryEntry entry)
        {
            entry.Pending = true;
            entry.Character = ModCategories.NameGuessAllowed(entry) ? entry.FilenameCharacter : "";
            entry.Detection = "Pending";
        }
        // Reads one mod as a full scan would (the archive listing and modinfo.ini files) and records it in the index.
        // Safe on a pool thread for an entry no other thread uses (the window passes a copy: IndexCopy).
        // ZIPs and folders read their few modinfo.ini files with the listing (cheap); RAR/7Z leave them for IndexNames.
        internal static void IndexEntry(LibraryEntry entry, string sevenZipPath)
        {
            entry.Pending = false;
            if (entry.Detection != "Error") DetectIdentity(entry, sevenZipPath, entry.Kind == "ZIP" || entry.Kind == "Folder");
        }
        // Second pass: the modinfo.ini names of a mod whose identity is known. A failure only costs the names.
        internal static void IndexNames(LibraryEntry entry, string sevenZipPath)
        {
            try
            {
                string stamp = entry.Kind == "Folder" ? null : ArchiveStamp(entry.SourcePath, sevenZipPath);
                ReadNames(entry, sevenZipPath, stamp);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { if (!(ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)) throw; entry.NamesPending = false; }
        }
        // Reads the names and adds them to the index record when it is still the record of this file (same stamp).
        private static void ReadNames(LibraryEntry entry, string sevenZipPath, string stamp)
        {
            ModInfoSummary info;
            using (var reader = new SourceReader(entry.SourcePath, sevenZipPath)) info = ReadModInfo(reader);
            entry.Authors = info.Authors; entry.DisplayName = info.DisplayName; entry.OptionNames = info.OptionNames; entry.NamesPending = false;
            if (entry.Kind == "Folder" || stamp == null) return;
            lock (CacheLock)
            {
                LibraryIdentityRecord record;
                if (IdentityCache.TryGetValue(entry.SourcePath, out record) && record.Stamp == stamp)
                { record.Authors = new List<string>(info.Authors); record.DisplayName = info.DisplayName ?? ""; record.OptionNames = new List<string>(info.OptionNames); cacheDirty = true; }
            }
        }
        internal static LibraryEntry IndexCopy(LibraryEntry e)
        {
            return new LibraryEntry { SourcePath = e.SourcePath, Name = e.Name, Kind = e.Kind, Size = e.Size, AddedUtc = e.AddedUtc, FilenameCharacter = e.FilenameCharacter, LibraryRoot = e.LibraryRoot, Character = "", Detection = "" };
        }
        // Copies what IndexEntry found from `found` (a copy) onto the listed entry.
        internal static void TakeIdentity(LibraryEntry entry, LibraryEntry found)
        {
            entry.CharacterCodes = found.CharacterCodes; entry.Character = found.Character; entry.Detection = found.Detection; entry.DetectionError = found.DetectionError;
            entry.OptionCount = found.OptionCount; entry.OptionIds = found.OptionIds; entry.CostumeKeys = found.CostumeKeys; entry.ToolKinds = found.ToolKinds; entry.StageIds = found.StageIds; entry.MiscAreas = found.MiscAreas; entry.HasPakPayload = found.HasPakPayload;
            entry.Authors = found.Authors; entry.DisplayName = found.DisplayName; entry.OptionNames = found.OptionNames; entry.FilenameCharacter = found.FilenameCharacter;
            entry.Pending = false; entry.NamesPending = found.NamesPending;
        }
        internal static void TakeNames(LibraryEntry entry, LibraryEntry found)
        {
            entry.Authors = found.Authors; entry.DisplayName = found.DisplayName; entry.OptionNames = found.OptionNames; entry.NamesPending = false;
        }
        internal static void SaveIndex() { SaveIdentityCache(); }
        // The library folders' mods with their file stats only (no archive is opened).
        private static List<LibraryEntry> ListEntries(IEnumerable<string> libraries)
        {
            var result = new List<LibraryEntry>(); var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenLibraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            CancellationToken token = OperationContext.Token;
            token.ThrowIfCancellationRequested();
            // 1.0.11: the index (about 1.5 MB of JSON for 2,600 mods) is read while the folders are listed.
            Task indexRead = Task.Run(() => EnsureCacheLoaded());
            try
            {
                foreach (string lib in libraries)
                {
                    token.ThrowIfCancellationRequested();
                    string library = lib;
                    try
                    {
                        library = Path.GetFullPath(lib);
                        if (!seenLibraries.Add(library)) continue;
                        OperationContext.Report("Listing mod libraries",result.Count,0);
                        string root = library.Length > 3 ? library.TrimEnd(Path.DirectorySeparatorChar) : library;   // "F:\" stays as GetDirectoryName gives it
                        foreach (string path in Directory.EnumerateFileSystemEntries(library)) AddListed(path, root, 0, result, seen, token);
                    }
                    catch (Exception ex)
                    {
                        if (!IsSourceError(ex)) throw;
                        // Keep already discovered entries and every other configured library.
                        result.Add(ErrorEntry(library,"Library",ex));
                    }
                }
            }
            finally { indexRead.GetAwaiter().GetResult(); }
            return result;
        }
        // 1.0.2: a group folder's mods are listed one by one (two group levels at most), each still in its library.
        private static void AddListed(string path, string root, int depth, List<LibraryEntry> result, HashSet<string> seen, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (depth < 2 && IsGroupFolder(path))
            {
                try { foreach (string child in Directory.EnumerateFileSystemEntries(path)) AddListed(child, root, depth + 1, result, seen, token); }
                catch (Exception ex) { if (!IsSourceError(ex)) throw; result.Add(ErrorEntry(path, "Source", ex)); }
                return;
            }
            LibraryEntry entry = DescribeEntry(path);
            if (entry != null && seen.Add(entry.SourcePath)) { entry.LibraryRoot = root; result.Add(entry); }
        }
        // 1.0.2: a folder that only sorts mods (Mods\Cammy\<mods>) rather than being one. Nothing at its top makes it a
        // mod itself (natives\, reframework\, modinfo.ini, a .pak or .dll), and it holds an archive (archives inside a
        // folder mod are never installed) or is named after a fighter ("Cammy", "Cammy mods"). A multi-option folder
        // mod (Mod\Option\natives) keeps its options unless it is named only after a fighter.
        internal static bool IsGroupFolder(string path)
        {
            try
            {
                FileAttributes attr = File.GetAttributes(path);
                if ((attr & FileAttributes.Directory) == 0 || (attr & (FileAttributes.ReparsePoint | FileAttributes.Hidden)) != 0) return false;
                bool archive = false;
                foreach (string child in Directory.EnumerateFileSystemEntries(path))
                {
                    string leaf = Path.GetFileName(child);
                    if (Directory.Exists(child))
                    {
                        if (leaf.Equals("natives", StringComparison.OrdinalIgnoreCase) || leaf.Equals("reframework", StringComparison.OrdinalIgnoreCase)) return false;
                        continue;
                    }
                    string extension = Path.GetExtension(leaf).ToLowerInvariant();
                    if (extension == ".zip" || extension == ".rar" || extension == ".7z") archive = true;
                    else if (extension == ".pak" || extension == ".dll" || leaf.Equals("modinfo.ini", StringComparison.OrdinalIgnoreCase)) return false;
                }
                return archive || Characters.IsGroupName(Path.GetFileName(path));
            }
            catch (Exception ex) { if (!IsSourceError(ex)) throw; return false; }
        }
        // A separate boundary permits deterministic disappearing-entry regression checks.
        internal static LibraryEntry DescribeEntry(string path)
        {
            string fullPath = path;
            try
            {
                fullPath = Path.GetFullPath(path);
                FileAttributes attr = File.GetAttributes(fullPath);
                if ((attr & (FileAttributes.ReparsePoint | FileAttributes.Hidden)) != 0) return null;
                bool folder = (attr & FileAttributes.Directory) != 0;
                string extension = Path.GetExtension(fullPath).ToLowerInvariant();
                if (!folder && extension != ".zip" && extension != ".rar" && extension != ".7z") return null;
                string name = folder ? Path.GetFileName(fullPath) : Path.GetFileNameWithoutExtension(fullPath);
                // One stat gives the size and both times (2.4.1 Added sort); no archive is opened for it.
                FileSystemInfo info = folder ? (FileSystemInfo)new DirectoryInfo(fullPath) : new FileInfo(fullPath);
                return new LibraryEntry { SourcePath=fullPath,Name=name,Character="",FilenameCharacter=Characters.Guess(name),Kind=folder ? "Folder" : extension.TrimStart('.').ToUpperInvariant(),Size=folder ? 0 : ((FileInfo)info).Length,AddedUtc=AddedTime(info.CreationTimeUtc,info.LastWriteTimeUtc) };
            }
            catch (Exception ex)
            {
                if (!IsSourceError(ex)) throw;
                return ErrorEntry(fullPath,"Source",ex);
            }
        }
        // 2.4.1 Added: the creation time (when the copy arrived in the library), or the last write time when the
        // creation time is missing (a file system without it reports 1601) or later than the write time (a copy
        // that kept an older write time). MinValue when neither is known.
        internal static DateTime AddedTime(DateTime createdUtc, DateTime writtenUtc)
        {
            bool created = createdUtc.Year > 1601, written = writtenUtc.Year > 1601;
            if (created && (!written || createdUtc <= writtenUtc)) return DateTime.SpecifyKind(createdUtc, DateTimeKind.Utc);
            return written ? DateTime.SpecifyKind(writtenUtc, DateTimeKind.Utc) : DateTime.MinValue;
        }
        internal static string AddedStamp(DateTime addedUtc) { return addedUtc == DateTime.MinValue ? null : addedUtc.ToString("o", System.Globalization.CultureInfo.InvariantCulture); }
        private static bool IsSourceError(Exception error)
        {
            return error is IOException || error is UnauthorizedAccessException || error is ArgumentException || error is NotSupportedException || error is System.Security.SecurityException;
        }
        private static LibraryEntry ErrorEntry(string path,string kind,Exception error)
        {
            string name = path ?? "(invalid path)";
            try { string leaf=Path.GetFileName(name.TrimEnd(Path.DirectorySeparatorChar)); if (!String.IsNullOrEmpty(leaf)) name=leaf; } catch (ArgumentException) { }
            string safePath="";
            try { safePath=Path.GetFullPath(path); }
            catch (Exception ex) { if (!IsSourceError(ex)) throw; }
            if (kind == "Source" && safePath.Length > 0)
            {
                string extension=Path.GetExtension(safePath).ToLowerInvariant();
                if (extension == ".zip" || extension == ".rar" || extension == ".7z") kind=extension.TrimStart('.').ToUpperInvariant();
            }
            ForgetIdentity(path);
            return new LibraryEntry { SourcePath=safePath,Name=name,Kind=kind,Character="",FilenameCharacter="",Detection="Error",DetectionError=error.Message };
        }
        private static void ForgetIdentity(string path)
        {
            if (path == null) return;
            lock (CacheLock) if (IdentityCache.Remove(path)) cacheDirty = true;
        }
        private static void DetectIdentity(LibraryEntry entry, string sevenZipPath) { DetectIdentity(entry, sevenZipPath, true); }
        // readNames false (1.0.6, background first pass): the archive listing only; the modinfo.ini names are left for
        // ReadNames (NamesPending), and the record is saved without them.
        private static void DetectIdentity(LibraryEntry entry, string sevenZipPath, bool readNames)
        {
            try
            {
                OperationContext.ThrowIfCancellationRequested();
                bool folder = entry.Kind == "Folder";
                string stamp = folder ? null : ArchiveStamp(entry.SourcePath, sevenZipPath);
                LibraryIdentityRecord cached = null;
                if (!folder)
                {
                    lock (CacheLock)
                    {
                        LibraryIdentityRecord found;
                        if (IdentityCache.TryGetValue(entry.SourcePath, out found) && UsableIdentity(found, stamp)) cached = found;
                        // 2.4.1: a record from before the Added sort (or with an older time) gets this listing's stat; no relisting.
                        string added = AddedStamp(entry.AddedUtc);
                        if (cached != null && added != null && cached.AddedUtc != added) { cached.AddedUtc = added; cacheDirty = true; }
                    }
                }
                List<string> codes;
                if (cached != null) { codes = new List<string>(cached.CharacterCodes); entry.OptionCount = cached.OptionCount.Value; entry.OptionIds = new List<string>(cached.OptionIds); entry.CostumeKeys = new List<string>(cached.CostumeKeys); entry.ToolKinds = new List<string>(cached.ToolKinds); entry.StageIds = new List<string>(cached.StageIds); entry.MiscAreas = new List<string>(cached.MiscAreas); entry.HasPakPayload = cached.HasPakPayload;
                    if (!entry.HasPakPayload.HasValue)
                    {
                        try { using (var reader = new SourceReader(entry.SourcePath, sevenZipPath)) entry.HasPakPayload = ReadPakPayload(entry.SourcePath, reader); }
                        catch (Exception ex) { if (ex is OutOfMemoryException || ex is OperationCanceledException) throw; Program.Log(ex); } // optional backfill cannot erase known identity
                        lock (CacheLock) { cached.HasPakPayload = entry.HasPakPayload; cacheDirty = true; }
                    }
                    if (NamesKnown(cached)) { entry.Authors = new List<string>(cached.Authors); entry.DisplayName = cached.DisplayName; entry.OptionNames = new List<string>(cached.OptionNames); entry.NamesPending = false; }
                    else if (readNames) IndexNames(entry, sevenZipPath);   // a names failure costs only the names
                    else entry.NamesPending = true;
                }
                else
                {
                    Interlocked.Increment(ref CacheMisses);
                    var identity = new CharacterIdentity();
                    var costumes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var options = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var tools = new HashSet<string>(StringComparer.Ordinal);
                    var categories = new ModCategories.Collector();
                    var artFiles = new List<string>();
                    using (var reader = new SourceReader(entry.SourcePath, sevenZipPath))
                    {
                        foreach (string path in reader.Entries.Keys)
                        {
                            OperationContext.ThrowIfCancellationRequested();
                            string[] parts = path.Split('/');
                            int boundary = Array.FindIndex(parts, part => part.Equals("natives", StringComparison.OrdinalIgnoreCase));
                            if (boundary < 0)
                            {
                                // 2.3.0: REFramework colour data keyed to a fighter is that fighter's (not a tool file).
                                if (identity.AddOutside(path)) { categories.AddFighterData(); continue; }
                                string tool = ToolKind(parts); if (tool != null) tools.Add(tool); else categories.AddOther(path); continue;
                            }
                            options.Add(String.Join("/", parts.Take(boundary)));
                            string native = "/" + String.Join("/", parts.Skip(boundary + 1));
                            CharacterIdentity.NativeFile fileIdentity = identity.AddNative(native);
                            Match costume = CostumeFolder.Match(native);
                            if (costume.Success) costumes.Add(costume.Groups[1].Value + "/" + costume.Groups[2].Value);
                            // 2.3.0: the fighter's own art (gui/data/character_image/NNN/) belongs to that fighter. 1.0.5: only when
                            // the art covers one fighter; art for several fighters (a portrait set) is a UI mod (sorted after the loop).
                            if (fileIdentity.ArtCode != null) { artFiles.Add(native); continue; }
                            categories.AddNative(native, fileIdentity.Fighter);
                        }
                        entry.HasPakPayload = ReadPakPayload(entry.SourcePath, reader);
                        if (readNames) { ModInfoSummary info = ReadModInfo(reader); entry.Authors = info.Authors; entry.DisplayName = info.DisplayName; entry.OptionNames = info.OptionNames; entry.NamesPending = false; }
                        else entry.NamesPending = true;
                    }
                    foreach (string art in artFiles) categories.AddNative(art, identity.SingleFighterArt);
                    codes = identity.Finish();
                    entry.OptionIds = options.OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToList();
                    entry.OptionCount = entry.OptionIds.Count;
                    entry.CostumeKeys = costumes.OrderBy(k => k, StringComparer.Ordinal).ToList();
                    entry.ToolKinds = ToolKindOrder.Where(tools.Contains).ToList();
                    entry.StageIds = categories.StageList();
                    entry.MiscAreas = categories.MiscList(LibrarySearch.IsToolMod(entry));
                    if (!folder)
                    {
                        lock (CacheLock)
                        {
                            IdentityCache[entry.SourcePath] = new LibraryIdentityRecord { SourcePath = entry.SourcePath, Stamp = stamp, HasPakPayload = entry.HasPakPayload, CharacterCodes = new List<string>(codes), OptionCount = entry.OptionCount, OptionIds = new List<string>(entry.OptionIds), CostumeKeys = new List<string>(entry.CostumeKeys), ToolKinds = new List<string>(entry.ToolKinds), StageIds = new List<string>(entry.StageIds), MiscAreas = new List<string>(entry.MiscAreas), AddedUtc = AddedStamp(entry.AddedUtc), Authors = readNames ? new List<string>(entry.Authors) : null, CodesVersion = CodesVersion, DisplayName = readNames ? entry.DisplayName ?? "" : null, OptionNames = readNames ? new List<string>(entry.OptionNames) : null };
                            cacheDirty = true;
                        }
                    }
                }
                ApplyCodes(entry, codes);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // A bad archive remains visible, with no falsely confirmed filename identity.
                // Failed reads are not cached, so Refresh can recover from temporary IO errors.
                ForgetIdentity(entry.SourcePath);
                entry.Character = ""; entry.FilenameCharacter=""; entry.Detection = "Error"; entry.DetectionError = ex.Message;
                entry.HasPakPayload = null; entry.CharacterCodes.Clear(); entry.OptionIds.Clear(); entry.OptionCount=-1; entry.CostumeKeys.Clear(); entry.ToolKinds.Clear(); entry.StageIds.Clear(); entry.MiscAreas.Clear(); entry.Authors.Clear(); entry.DisplayName = ""; entry.OptionNames.Clear(); entry.NamesPending = false;
            }
        }
        // The fighter of a mod from its native codes (one known fighter), else its file-name guess when allowed.
        private static void ApplyCodes(LibraryEntry entry, List<string> codes)
        {
            entry.CharacterCodes = codes;
            entry.DetectionError = ""; entry.Pending = false;
            if (codes.Count > 1) { entry.Character = ""; entry.Detection = "Ambiguous"; }
            else if (codes.Count == 1)
            {
                string name;
                if (Characters.ByCode.TryGetValue(codes[0], out name)) { entry.Character = name; entry.Detection = "Native"; }
                else { entry.Character = ""; entry.Detection = "UnknownNative"; }
            }
            else
            {
                entry.Character = ModCategories.NameGuessAllowed(entry) ? entry.FilenameCharacter : "";
                entry.Detection = String.IsNullOrWhiteSpace(entry.Character) ? "Unknown" : "Filename";
            }
        }
        // Most listings contain no PAK: no metadata or payload read is needed. For
        // candidates, use the actual root-file ownership/ignore rules, including
        // mixed packages, rather than treating an incidental filename as a payload.
        private static bool? ReadPakPayload(string source, SourceReader reader)
        {
            if (!reader.Entries.Keys.Any(path => path.EndsWith(".pak", StringComparison.OrdinalIgnoreCase) && !path.Split('/').Any(part => part.Equals("natives", StringComparison.OrdinalIgnoreCase) || part.Equals("__MACOSX", StringComparison.OrdinalIgnoreCase)))) return false;
            try { return PackageReader.Load(source, reader, false).Components.SelectMany(c => c.RootAssets).Any(a => ReplaceEngine.IsModPak(a.DestinationPath)); }
            catch (Exception ex) { if (ex is OutOfMemoryException || ex is OperationCanceledException) throw; Program.Log(ex); return null; }
        }
        // An unreadable modinfo.ini only costs the byline: the mod stays listed with its identity.
        internal static List<string> ReadAuthors(SourceReader reader) { return ReadModInfo(reader).Authors; }
        internal sealed class ModInfoSummary { internal List<string> Authors = new List<string>(), OptionNames = new List<string>(); internal string DisplayName = ""; }
        // 1.0.6: one read of the modinfo.ini files gives the authors, the display name and the option names.
        internal static ModInfoSummary ReadModInfo(SourceReader reader)
        {
            var summary = new ModInfoSummary();
            List<SourceEntry> infos = reader.Entries.Values.Where(e => Path.GetFileName(e.Path).Equals("modinfo.ini", StringComparison.OrdinalIgnoreCase) && e.Size <= 1024 * 1024).OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase).ToList();
            if (infos.Count == 0) return summary;
            try
            {
                Dictionary<string, byte[]> bytes = reader.ReadManyBytes(infos, 1024 * 1024);
                var parsed = infos.Select(e => { byte[] b; return new KeyValuePair<string, Dictionary<string, string>>(e.Path, bytes.TryGetValue(e.Path, out b) ? PackageReader.ParseIni(b) : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)); }).ToList();
                Func<Dictionary<string, string>, string, string> get = delegate(Dictionary<string, string> meta, string key) { string v; return meta.TryGetValue(key, out v) ? Regex.Replace(v ?? "", @"\s+", " ").Trim() : ""; };
                summary.Authors = CleanAuthors(parsed.Select(p => get(p.Value, "author")));
                summary.OptionNames = parsed.Select(p => get(p.Value, "name")).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                summary.DisplayName = ModInfoName(parsed.Select(p => new KeyValuePair<string, Dictionary<string, string>>(ModInfoFolder(p.Key), p.Value)).ToList(), get);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { if (!(ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException)) throw; return new ModInfoSummary(); }
            return summary;
        }
        private static string ModInfoFolder(string path) { int slash = path.LastIndexOf('/'); return slash < 0 ? "" : path.Substring(0, slash); }
        // The mod's own name, close to the inspector's (PackageReader): the top-level dummy root's name=, else the
        // NameAsBundle= of the top-level options, else the name= of the only top-level modinfo.ini. Options inside
        // another modinfo.ini's folder and add-ons (AddonFor=) are not top level. "" when nothing names the mod (several
        // top-level options without a bundle name are options, not the mod).
        internal static string ModInfoName(List<KeyValuePair<string, Dictionary<string, string>>> infos, Func<Dictionary<string, string>, string, string> get)
        {
            var folders = infos.Select(i => i.Key).ToList();
            Func<string, bool> nested = delegate(string folder) { return folders.Any(other => other != folder && (other.Length == 0 ? folder.Length > 0 : folder.StartsWith(other + "/", StringComparison.OrdinalIgnoreCase))); };
            var top = infos.Where(i => get(i.Value, "addonfor").Length == 0 && !nested(i.Key)).ToList();
            var dummy = top.FirstOrDefault(i => get(i.Value, "dummymod").Equals("true", StringComparison.OrdinalIgnoreCase));
            if (dummy.Value != null) return get(dummy.Value, "name");
            string bundle = top.Select(i => get(i.Value, "nameasbundle")).FirstOrDefault(s => s.Length > 0);
            if (!String.IsNullOrEmpty(bundle)) return bundle;
            return top.Count == 1 ? get(top[0].Value, "name") : "";
        }
        internal static List<string> CleanAuthors(IEnumerable<string> authors)
        {
            return authors.Select(a => Regex.Replace(a ?? "", @"\s+", " ").Trim()).Where(a => a.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        // "A", "A, B", "A, B +2" for the inspector byline and the Installed chips.
        internal static string AuthorText(IList<string> authors)
        {
            if (authors == null || authors.Count == 0) return "";
            return String.Join(", ", authors.Take(2)) + (authors.Count > 2 ? " +" + (authors.Count - 2) : "");
        }
        // Tool kind of one archive/folder file outside natives, or null. Wrapper folders before
        // reframework/ are allowed (modinfo layouts, single wrapper folders). Mac/Explorer
        // leftovers never count.
        internal static string ToolKind(string[] parts)
        {
            if (parts == null || parts.Length == 0) return null;
            string leaf = parts[parts.Length - 1];
            if (leaf.Length == 0 || Regex.IsMatch(leaf, @"^(desktop\.ini|thumbs\.db|\.ds_store)$", RegexOptions.IgnoreCase)) return null;
            if (parts.Any(p => p.Equals("__MACOSX", StringComparison.OrdinalIgnoreCase))) return null;
            bool dll = leaf.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
            int rf = Array.FindIndex(parts, p => p.Equals("reframework", StringComparison.OrdinalIgnoreCase));
            if (rf >= 0 && rf < parts.Length - 1)
            {
                string area = rf + 2 < parts.Length ? parts[rf + 1] : "";
                if (area.Equals("autorun", StringComparison.OrdinalIgnoreCase) && leaf.EndsWith(".lua", StringComparison.OrdinalIgnoreCase)) return ToolScript;
                if (area.Equals("plugins", StringComparison.OrdinalIgnoreCase) && dll) return ToolPlugin;
                return dll ? ToolDll : ToolReframework;
            }
            return dll ? ToolDll : null;
        }
        public static LibraryInstallState InstallState(LibraryEntry entry, IEnumerable<InstalledOutfit> outfits)
        {
            if (entry == null || String.IsNullOrWhiteSpace(entry.SourcePath) || outfits == null) return LibraryInstallState.None;
            List<InstalledOutfit> matches = outfits.Where(o => (o.Managed || o.WholeArchive) && !String.IsNullOrWhiteSpace(o.SourcePath) && String.Equals(o.SourcePath, entry.SourcePath, StringComparison.OrdinalIgnoreCase)).ToList();
            List<InstalledOutfit> active = matches.Where(o => o.Enabled).ToList();
            if (active.Count == 0) return matches.Count > 0 ? LibraryInstallState.Disabled : LibraryInstallState.None;
            if (active.Any(o => o.WholeArchive)) return LibraryInstallState.Full;   // 2.5.4: every variant, as a manual drop
            var selected = new HashSet<string>(active.Where(o => o.SelectedIds != null).SelectMany(o => o.SelectedIds), StringComparer.OrdinalIgnoreCase);
            return entry.OptionIds.Count > 0 && entry.OptionIds.All(selected.Contains) ? LibraryInstallState.Full : LibraryInstallState.Partial;
        }
        private static string ArchiveStamp(string source, string sevenZipPath) { return ArchiveStampWith(source, ToolStamp(sevenZipPath)); }
        private static string ArchiveStampWith(string source, string toolStamp)
        {
            FileInfo archive = new FileInfo(source);
            return archive.Length.ToString() + ":" + archive.LastWriteTimeUtc.Ticks.ToString() + ":" + toolStamp;
        }
        // The 7-Zip that reads RAR/7Z: its path and file stamp (a different 7-Zip may list an archive differently).
        private static string ToolStamp(string sevenZipPath)
        {
            string resolved = SevenZipTool.Resolve(sevenZipPath);   // 1.0.11: the 7z.exe that actually runs
            string tool = String.IsNullOrWhiteSpace(resolved) ? "" : Path.GetFullPath(resolved);
            string toolStamp = "missing";
            if (File.Exists(tool))
            {
                var info = new FileInfo(tool);
                toolStamp = info.Length.ToString() + ":" + info.LastWriteTimeUtc.Ticks.ToString();
            }
            return tool.ToUpperInvariant() + ":" + toolStamp;
        }
        private static void EnsureCacheLoaded()
        {
            lock (CacheLock)
            {
                if (cacheLoaded) return;
                cacheLoaded = true;
                string file = Path.Combine(IdentityCacheDirectory, CacheFilename);
                try
                {
                    Safety.NoReparseChain(file);
                    if (!File.Exists(file)) return;
                    var data = Json.Read<LibraryIdentityCache>(file);
                    if (data == null || data.Format != CacheFormat || data.Records == null) return;
                    foreach (var record in data.Records)
                    {
                        if (record == null || String.IsNullOrWhiteSpace(record.SourcePath) || record.Stamp == null || record.CharacterCodes == null || record.CharacterCodes.Any(c => !Regex.IsMatch(c ?? "", @"^\d{3}$")) || (record.CostumeKeys != null && record.CostumeKeys.Any(k => !Regex.IsMatch(k ?? "", @"^\d{3}/\d{3}$"))) || (record.ToolKinds != null && record.ToolKinds.Any(k => !ToolKindOrder.Contains(k)))
                            || (record.StageIds != null && record.StageIds.Any(k => !Regex.IsMatch(k ?? "", @"^(\d{4}(_\d{2})?|other)$"))) || (record.MiscAreas != null && record.MiscAreas.Any(k => String.IsNullOrWhiteSpace(k) || k.Length > 40))
                            || (record.DisplayName != null && record.DisplayName.Length > 300) || (record.OptionNames != null && (record.OptionNames.Count > 2000 || record.OptionNames.Any(k => k == null || k.Length > 300)))) continue;
                        IdentityCache[Path.GetFullPath(record.SourcePath)] = record;
                    }
                }
                catch (Exception) { IdentityCache.Clear(); }
            }
        }
        private static void SaveIdentityCache()
        {
            lock (CacheLock)
            {
                if (!cacheDirty) return;
                try
                {
                    string directory = IdentityCacheDirectory;
                    Safety.NoReparseChain(directory);
                    Directory.CreateDirectory(directory);
                    string file = Path.Combine(directory, CacheFilename);
                    Safety.NoReparseChain(file);
                    Json.Write(file, new LibraryIdentityCache { Records = IdentityCache.Values.OrderBy(r => r.SourcePath, StringComparer.OrdinalIgnoreCase).ToList() });
                    cacheDirty = false;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
    public sealed class InstallResult { public string Folder,Message; public int FileCount,SwatchFixes; public long Bytes; public string PartsNote=""; }   // 1.0.6: PartsNote = parts kept or dropped on a reinstall
    public sealed class InstalledOutfit
    {
        public string Name,Character,Folder,Id,SourcePath;
        internal string SnapshotStamp;
        public string LoaderOutfitName,LoaderStatus,LoaderGenerated,LoaderBuild,LoaderOutfitDetails;
        public DateTime InstalledUtc;
        public bool Managed,Enabled;
        public List<string> SelectedIds=new List<string>();
        internal ArchiveVariants Variants;      // 2.5.0: SlotsLoader's variants of a manual archive/folder with several (VariantChoices.cs)
        internal bool VariantsUnsupported;      // 2.5.0: several SlotsLoader outfits but no variants.json (an older SlotsLoader)
        internal bool WholeArchive;             // 2.5.4: a manual archive the Manager copied from SourcePath (Install all variants, ArchiveInstalls.cs)
    }
    public sealed class InstallManifest
    {
        public string Format="sf6-slotsloader-installer-v1",Id,Name,Character,SourcePath,CreatedUtc;
        public List<string> SelectedIds=new List<string>();
        public Dictionary<string,string> Sha256=new Dictionary<string,string>();
        // 1.0.6: parts added from other mods (InstallerParts.cs); null or empty = none. Older Managers ignore the field.
        public List<PartGraft> Parts;
    }
    public static partial class Installer
    {
        private const string ManifestName=".slots-installer.json";
        public static InstallResult Install(CompositionPlan plan,string costumeRoot,string character,string outfitName,string sevenZipPath,bool replaceManaged)
        {
            using (AcquireMutation(costumeRoot)) return InstallLocked(plan,costumeRoot,character,outfitName,sevenZipPath,replaceManaged);
        }
        private static InstallResult InstallLocked(CompositionPlan plan,string costumeRoot,string character,string outfitName,string sevenZipPath,bool replaceManaged)
        {
            if (!plan.CanInstall) throw new IOException(String.Join(Environment.NewLine,plan.Errors));
            Safety.Segment(character); Safety.Segment(outfitName);
            if (outfitName.StartsWith(".") || outfitName.Equals("natives",StringComparison.OrdinalIgnoreCase)) throw new IOException("Outfit names can't start with a dot or be 'natives' (Slotsloader reserves them).");
            if (!Characters.Names.Contains(character)) throw new IOException("Choose a known character.");
            var actualCodes=plan.Files.Keys.SelectMany(p=>Regex.Matches(p,@"/esf/esf(\d{3})/",RegexOptions.IgnoreCase).Cast<Match>().Select(m=>m.Groups[1].Value)).Distinct().ToList();
            string expected=Characters.ByCode.First(k=>k.Value==character).Key;
            if (actualCodes.Any(c=>c!=expected)) throw new IOException("These files belong to "+String.Join(", ",actualCodes.Select(c=>Characters.ByCode.ContainsKey(c) ? Characters.ByCode[c] : "esf"+c))+", not "+character+"; the Manager can't retarget mods.");
            string root=ValidateRoot(costumeRoot), parent=Path.GetDirectoryName(root);
            string characterDir=Safety.Under(root,character); string dest=Safety.Under(characterDir,outfitName);
            Safety.NoReparseChain(dest);
            InstallManifest oldManifest=null;
            if (Directory.Exists(dest))
            {
                oldManifest=ManagedManifest(dest,character);
                if (!replaceManaged || oldManifest==null) throw new IOException("An outfit with this name already exists. Choose another name, or update an outfit the Manager installed.");
            }
            string reviewedManifest = oldManifest == null ? null : File.ReadAllText(Path.Combine(dest,ManifestName));
            string transactionRoot=Path.Combine(parent,".slotsloader-installer-staging"); Safety.NoReparseChain(transactionRoot);
            Directory.CreateDirectory(transactionRoot);
            string transaction=Safety.Under(transactionRoot,Guid.NewGuid().ToString("N")); Directory.CreateDirectory(transaction);
            string stage=Path.Combine(transaction,"outfit"); Directory.CreateDirectory(stage);
            string backup=null; bool committed=false;
            try
            {
                // Reinspect before writing: source options and asset sizes must still match the plan.
                ModPackage fresh; CompositionPlan current; Dictionary<string,string> hashes;
                using (var r=new SourceReader(plan.Package.SourcePath,sevenZipPath))
                {
                fresh=PackageReader.Load(plan.Package.SourcePath,r,false);
                current=Composer.Plan(fresh,plan.Layers.Select(c=>c.Id));
                if (!current.CanInstall || current.Files.Count!=plan.Files.Count || current.Files.Any(k=>!plan.Files.ContainsKey(k.Key) || plan.Files[k.Key].Asset.Size!=k.Value.Asset.Size || plan.Files[k.Key].Asset.EntryPath!=k.Value.Asset.EntryPath)) throw new IOException("The mod changed after preview. Reload it and review again.");
                hashes=r.CopyFiles(current.Files.Values,stage,transaction);
                }
                // 1.0.6: parts added from other mods (InstallerParts.cs) stay when the same mod is reinstalled (Edit options,
                // a changed source); another mod in this outfit's place starts without them.
                var droppedParts=new List<string>(); List<PartGraft> keptParts=new List<PartGraft>();
                if (oldManifest!=null && oldManifest.Parts!=null && oldManifest.Parts.Count>0)
                {
                    if (String.Equals(oldManifest.SourcePath,fresh.SourcePath,StringComparison.OrdinalIgnoreCase)) keptParts=ReapplyParts(oldManifest,stage,hashes,transaction,sevenZipPath,droppedParts);
                    else droppedParts.AddRange(oldManifest.Parts.Select(g=>GraftText(g)+" (another mod fills the outfit now)"));
                }
                int swatchFixes=FixSwatches(stage,hashes,root);   // 2.5.5: outdated select-screen swatches (ModFixes.UpgradeSwatches)
                var manifest=new InstallManifest {Id=oldManifest==null ? Guid.NewGuid().ToString("N") : oldManifest.Id,Name=outfitName,Character=character,SourcePath=fresh.SourcePath,CreatedUtc=DateTime.UtcNow.ToString("o"),SelectedIds=current.Layers.Select(c=>c.Id).ToList()};
                manifest.Sha256=hashes; if (keptParts.Count>0) manifest.Parts=keptParts;
                File.WriteAllText(Path.Combine(stage,"modinfo.ini"),"name="+outfitName+"\r\nauthor="+CleanMetadata(String.Join(", ",current.Layers.Select(c=>c.Author).Where(s=>!String.IsNullOrWhiteSpace(s)).Distinct()))+"\r\ndescription="+CleanMetadata("Composed with Slotsloader Manager. "+String.Join(" + ",current.Layers.Select(c=>c.Name)))+"\r\nversion=1.0\r\n",new UTF8Encoding(false));
                Json.Write(Path.Combine(stage,ManifestName),manifest);
                OperationContext.ThrowIfCancellationRequested();
                using (OperationContext.NonCancellable())
                {
                Directory.CreateDirectory(characterDir);
                Safety.NoReparseChain(dest);
                if (Directory.Exists(dest))
                {
                    InstallManifest now=ManagedManifest(dest,character);
                    if (!replaceManaged || oldManifest==null || now==null || now.Id!=oldManifest.Id || File.ReadAllText(Path.Combine(dest,ManifestName))!=reviewedManifest)
                        throw new IOException("The destination changed during install. Refresh and review it.");
                    string backups=Path.Combine(parent,".slotsloader-installer-backups"); Safety.NoReparseChain(backups); Directory.CreateDirectory(backups);
                    backup=Safety.Under(backups,Guid.NewGuid().ToString("N")); MoveDirectory(dest,backup);
                }
                else if (oldManifest!=null) throw new IOException("The destination disappeared during install. Refresh and try again.");
                try { MoveDirectory(stage,dest); committed=true; }
                catch { if (backup!=null && !Directory.Exists(dest)) MoveDirectory(backup,dest); throw; }
                }
                OutfitHistory.Installed(manifest,oldManifest,fresh.Name);   // 2.4.1 history (never throws)
                // 1.0.1: the switched-out copy was only the rollback for this move; nothing is kept once it committed.
                if (backup!=null) { try { Safety.DeleteGenerated(backup,Path.GetDirectoryName(backup)); backup=null; } catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); } }
                string partsNote=(keptParts.Count>0 ? " · kept "+String.Join("; ",keptParts.Select(GraftText)) : "")+(droppedParts.Count>0 ? " · removed "+String.Join("; ",droppedParts) : "");
                return new InstallResult {Folder=dest,FileCount=current.Files.Count,Bytes=current.TotalBytes,SwatchFixes=swatchFixes,PartsNote=partsNote,Message="Installed "+outfitName+" for "+character+(swatchFixes>0 ? " (outdated colour swatches rebuilt)" : "")+partsNote+". Restart SF6 to load it."};
            }
            finally
            {
                // Only a generated transaction inside the validated sibling staging root is removed.
                try { Safety.DeleteGenerated(transaction,transactionRoot); } catch { }
                if (!committed && backup!=null && !Directory.Exists(dest) && Directory.Exists(backup)) MoveDirectory(backup,dest);
            }
        }
        public static List<InstalledOutfit> ListInstalled(string costumeRoot)
        {
            string root=ValidateRoot(costumeRoot); var list=new List<InstalledOutfit>();
            if (Directory.Exists(root))
            {
                foreach (string ch in Directory.EnumerateDirectories(root))
                {
                    if (Path.GetFileName(ch).StartsWith(".") || (File.GetAttributes(ch)&FileAttributes.Hidden)!=0) continue;
                    Safety.NoReparse(ch);
                    foreach (string folder in Directory.EnumerateDirectories(ch))
                    {
                        if (Path.GetFileName(folder).StartsWith(".") || (File.GetAttributes(folder)&FileAttributes.Hidden)!=0) continue;
                        Safety.NoReparse(folder); string character=Path.GetFileName(ch); InstallManifest m=ManagedManifest(folder,character);
                        // Ignore empty placeholder dirs but retain imported/manual variants for browsing.
                        if (m==null && !Directory.EnumerateFileSystemEntries(folder).Any()) continue;
                        list.Add(ToOutfit(folder,character,true,m));
                    }
                    foreach (string file in Directory.EnumerateFiles(ch))
                    {
                        if ((File.GetAttributes(file)&(FileAttributes.Hidden|FileAttributes.ReparsePoint))!=0) continue;
                        string extension=Path.GetExtension(file).ToLowerInvariant();
                        if (extension==".zip" || extension==".rar" || extension==".7z" || extension==".pak") list.Add(ToOutfit(file,Path.GetFileName(ch),true,null));
                    }
                }
            }
            string disabled=Path.Combine(Path.GetDirectoryName(root),".slotsloader-installer-disabled"); Safety.NoReparseChain(disabled);
            if (Directory.Exists(disabled)) foreach (string folder in Directory.EnumerateDirectories(disabled))
            {
                Safety.NoReparse(folder); InstallManifest m=ManagedManifest(folder,null); if (m!=null) list.Add(ToOutfit(folder,m.Character,false,m));
            }
            ArchiveInstalls.Apply(list);   // 2.5.4: archives copied by Install all variants keep their library source
            LoaderResults.Apply(list,root);
            return list.OrderBy(x=>x.Character).ThenBy(x=>x.Name).ToList();
        }
        internal sealed class ManagedManifestSnapshot
        {
            internal string File;
            internal byte[] Before;
            internal InstallManifest Manifest;
        }
        // Rename needs only managed manifests, including disabled outfits. Match ListInstalled's visibility and
        // link rules without reading manual archives, loader reports, variant choices or UI snapshot stamps.
        internal static IEnumerable<ManagedManifestSnapshot> ManagedManifests(string costumeRoot)
        {
            string root = ValidateRoot(costumeRoot);
            if (Directory.Exists(root))
                foreach (string character in Directory.EnumerateDirectories(root))
                {
                    if (Path.GetFileName(character).StartsWith(".") || (File.GetAttributes(character) & FileAttributes.Hidden) != 0) continue;
                    Safety.NoReparse(character);
                    foreach (string folder in Directory.EnumerateDirectories(character))
                    {
                        if (Path.GetFileName(folder).StartsWith(".") || (File.GetAttributes(folder) & FileAttributes.Hidden) != 0) continue;
                        Safety.NoReparse(folder);
                        ManagedManifestSnapshot snapshot = ReadManagedSnapshot(folder, Path.GetFileName(character));
                        if (snapshot != null) yield return snapshot;
                    }
                }
            string disabled = Path.Combine(Path.GetDirectoryName(root), ".slotsloader-installer-disabled"); Safety.NoReparseChain(disabled);
            if (Directory.Exists(disabled))
                foreach (string folder in Directory.EnumerateDirectories(disabled))
                {
                    Safety.NoReparse(folder);
                    ManagedManifestSnapshot snapshot = ReadManagedSnapshot(folder, null);
                    if (snapshot != null) yield return snapshot;
                }
        }
        private static ManagedManifestSnapshot ReadManagedSnapshot(string folder, string character)
        {
            string file = Path.Combine(folder, ManifestName); if (!File.Exists(file)) return null;
            Safety.NoReparseChain(file);
            try
            {
                byte[] before = File.ReadAllBytes(file); InstallManifest manifest;
                using (var reader = new StreamReader(new MemoryStream(before), Encoding.UTF8, true))
                    manifest = Json.Serializer().Deserialize<InstallManifest>(reader.ReadToEnd());
                return ValidManagedManifest(manifest, character) ? new ManagedManifestSnapshot { File = file, Before = before, Manifest = manifest } : null;
            }
            catch { return null; }
        }
        public static string SetEnabled(InstalledOutfit outfit,string costumeRoot,bool enabled)
        {
            using (AcquireMutation(costumeRoot)) return SetEnabledLocked(outfit,costumeRoot,enabled);
        }
        private static string SetEnabledLocked(InstalledOutfit outfit,string costumeRoot,bool enabled)
        {
            string root=ValidateRoot(costumeRoot); InstallManifest m=ValidateOutfit(outfit,root);
            if (outfit.Enabled==enabled) return outfit.Folder;
            string dst;
            if (enabled) { string ch=Safety.Under(root,m.Character); Directory.CreateDirectory(ch); dst=Safety.Under(ch,m.Name); }
            else { string disabled=Path.Combine(Path.GetDirectoryName(root),".slotsloader-installer-disabled"); Safety.NoReparseChain(disabled); Directory.CreateDirectory(disabled); dst=Safety.Under(disabled,m.Id); }
            Safety.NoReparseChain(dst);
            if (Directory.Exists(dst) || File.Exists(dst)) throw new IOException("Destination already exists; no outfit was overwritten.");
            MoveDirectory(outfit.Folder,dst);
            OutfitHistory.Changed(enabled ? HistoryKinds.Enable : HistoryKinds.Disable,outfit,null);   // 2.4.1 history
            return dst;
        }
        public static string Uninstall(InstalledOutfit outfit,string costumeRoot)
        {
            using (AcquireMutation(costumeRoot)) return UninstallLocked(outfit,costumeRoot);
        }
        private static string UninstallLocked(InstalledOutfit outfit,string costumeRoot)
        {
            string root=ValidateRoot(costumeRoot); InstallManifest m=ValidateOutfit(outfit,root);
            string trash=Path.Combine(Path.GetDirectoryName(root),".slotsloader-installer-trash"); Safety.NoReparseChain(trash); Directory.CreateDirectory(trash);
            string dst=Safety.Under(trash,m.Id+"-"+DateTime.UtcNow.ToString("yyyyMMddHHmmss")+"-"+Guid.NewGuid().ToString("N").Substring(0,6));
            MoveDirectory(outfit.Folder,dst);
            OutfitHistory.Changed(HistoryKinds.Uninstall,outfit,null);   // 2.4.1 history
            return dst;
        }
        private static InstallManifest ValidateOutfit(InstalledOutfit o,string root)
        {
            if (!o.Managed) throw new IOException("This outfit was installed manually, so the Manager can't change it.");
            InstallManifest m=ManagedManifest(o.Folder,o.Character);
            if (m==null || m.Id!=o.Id) throw new IOException("This outfit's record changed. Refresh the Installed list.");
            string expected=o.Enabled ? Safety.Under(Safety.Under(root,m.Character),m.Name) : Safety.Under(Path.Combine(Path.GetDirectoryName(root),".slotsloader-installer-disabled"),m.Id);
            if (!Path.GetFullPath(o.Folder).Equals(expected,StringComparison.OrdinalIgnoreCase)) throw new IOException("Outfit path is outside the managed location.");
            Safety.NoReparseChain(o.Folder); return m;
        }
        private static InstalledOutfit ToOutfit(string folder,string character,bool enabled,InstallManifest m)
        {
            DateTime created;
            if (m==null || !DateTime.TryParse(m.CreatedUtc,null,System.Globalization.DateTimeStyles.RoundtripKind,out created)) created=DateTime.MinValue;
            return new InstalledOutfit {Folder=folder,Name=m==null ? Path.GetFileName(folder) : m.Name,Character=character,Enabled=enabled,Managed=m!=null,Id=m==null ? "" : m.Id,SourcePath=m==null ? "" : m.SourcePath,SelectedIds=m==null ? new List<string>() : m.SelectedIds,InstalledUtc=created==DateTime.MinValue ? created : created.ToUniversalTime(),SnapshotStamp=OutfitSnapshotStamp(folder)};
        }
        internal static string OutfitSnapshotStamp(string path)
        {
            Safety.NoReparseChain(path);
            if (Directory.Exists(path)) { var item=new DirectoryInfo(path); return "D:"+item.CreationTimeUtc.Ticks+":"+item.LastWriteTimeUtc.Ticks; }
            var file=new FileInfo(path); if (!file.Exists) throw new IOException("The outfit disappeared. Refresh the Installed list.");
            return "F:"+file.CreationTimeUtc.Ticks+":"+file.LastWriteTimeUtc.Ticks+":"+file.Length;
        }
        private static InstallManifest ManagedManifest(string folder,string character)
        {
            string p=Path.Combine(folder,ManifestName); if (!File.Exists(p)) return null;
            Safety.NoReparseChain(p);
            try
            {
                InstallManifest m=Json.Read<InstallManifest>(p);
                return ValidManagedManifest(m,character) ? m : null;
            }
            catch { return null; }
        }
        private static bool ValidManagedManifest(InstallManifest m, string character)
        {
            if (m == null || m.Format != "sf6-slotsloader-installer-v1" || !Regex.IsMatch(m.Id ?? "", @"^[0-9a-f]{32}$") || m.SelectedIds == null || m.Sha256 == null || m.Sha256.Count == 0) return false;
            Safety.Segment(m.Name); Safety.Segment(m.Character);
            return character == null || m.Character.Equals(character, StringComparison.OrdinalIgnoreCase);
        }
        internal static IDisposable AcquireMutation(string costumeRoot)
        {
            LinuxPlatform.RequireClosed("change extra outfits");
            if (LinuxPlatform.IsLinux)
            {
                string parent = Path.GetDirectoryName(Path.GetFullPath(costumeRoot).TrimEnd(Path.DirectorySeparatorChar));
                if (parent != null && Path.GetFileName(parent).Equals("reframework", StringComparison.OrdinalIgnoreCase)) LinuxPlatform.CanonicalRoot(Path.GetDirectoryName(parent), "game");
            }
            string root=ValidateRoot(costumeRoot).ToUpperInvariant();
            string key;
            using (var sha=FastHash.Sha256()) key=BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(root))).Replace("-","");
            return new MutationLock("Local\\SF6SlotsInstaller-"+key);
        }
        private sealed class MutationLock : IDisposable
        {
            private Mutex mutex;
            internal MutationLock(string name)
            {
                mutex=new Mutex(false,name);
                bool acquired=false;
                try { try { acquired=mutex.WaitOne(0); } catch (AbandonedMutexException) { acquired=true; } }
                catch { mutex.Dispose(); throw; }
                if (!acquired) { mutex.Dispose(); throw new IOException("Another Manager task is changing this costume folder. Refresh and try again when it finishes."); }
            }
            public void Dispose() { mutex.ReleaseMutex(); mutex.Dispose(); }
        }
        internal static string CostumeRoot(string costumeRoot) { return ValidateRoot(costumeRoot); }
        // 2.5.5: an extra outfit's copied swatch files in the older layout are rebuilt from the game's current file
        // (<game>\reframework\costume_mods: the game is two folders up). The recorded hashes follow; the rest is untouched.
        internal static Func<string,byte[]> SwatchGameFile;   // tests: the game's current swatch files
        internal static int FixSwatches(string stage,Dictionary<string,string> hashes,string costumeRoot)
        {
            var swatches=hashes.Keys.Where(k=>ModFixes.SwatchPath.IsMatch(ModFixes.NormalizeNativePath(k))).ToList();
            if (swatches.Count==0) return 0;
            GameReference reference=new GameReference { GameFile=SwatchGameFile };
            if (reference.GameFile==null)
                try { GamePaks paks=GamePaks.Load(Path.GetDirectoryName(Path.GetDirectoryName(costumeRoot))); reference.GameFile=GameReference.PakReader(paks.Base,paks.Dlc); }
                catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); return 0; }
            int fixedFiles=0;
            foreach (string key in swatches)
            {
                string file=Safety.Under(stage,key); byte[] data=File.ReadAllBytes(file);
                FixResult fix=ModFixes.UpgradeSwatches(ref data,reference.ReadGameFile(ModFixes.NormalizeNativePath(key)));
                if (fix==null || !fix.Changed) { if (fix!=null) Program.LogLine(key+": "+fix.Message); continue; }
                File.WriteAllBytes(file,data); hashes[key]=ReplaceEngine.Sha(data); fixedFiles++;
            }
            return fixedFiles;
        }
        private static string ValidateRoot(string costumeRoot)
        {
            string root=Path.GetFullPath(costumeRoot).TrimEnd(Path.DirectorySeparatorChar);
            if (!Path.GetFileName(root).Equals("costume_mods",StringComparison.OrdinalIgnoreCase)) throw new IOException("The destination must be the Slotsloader costume_mods folder.");
            Safety.NoReparseChain(root); return root;
        }
        private static string CleanMetadata(string s) { return (s ?? "").Replace("\r"," ").Replace("\n"," "); }
        internal static void MoveDirectory(string source,string destination)
        {
            for (int attempt=0;;attempt++)
            {
                try { Directory.Move(source,destination); return; }
                catch (IOException e)
                {
                    int code=e.HResult & 0xFFFF;
                    if (attempt>=4 || (code!=5 && code!=32 && code!=33)) throw;
                }
                catch (UnauthorizedAccessException) { if (attempt>=4) throw; }
                // File scanners can briefly retain an extracted directory handle.
                System.Threading.Thread.Sleep(100*(1<<attempt));
            }
        }
        public static void OpenFolder(string path)
        {
            if (LinuxPlatform.IsLinux) { LinuxPlatform.OpenPath(path, File.Exists(path)); return; }
            if (File.Exists(path)) { Process.Start(new ProcessStartInfo("explorer.exe","/select,"+ProcessUtil.Quote(Path.GetFullPath(path))) {UseShellExecute=true}); return; }
            if (!Directory.Exists(path)) throw new IOException("Folder does not exist yet: "+path);
            Process.Start(new ProcessStartInfo("explorer.exe",ProcessUtil.Quote(Path.GetFullPath(path))) {UseShellExecute=true});
        }
    }
    internal static class Safety
    {
        internal static void Segment(string s)
        {
            if (String.IsNullOrWhiteSpace(s) || s=="." || s==".." || s.EndsWith(".") || s.EndsWith(" ") || s.IndexOfAny(Path.GetInvalidFileNameChars())>=0 || s.Contains("/") || s.Contains("\\") || s.Any(c=>c<32) || Regex.IsMatch(s,@"^(con|prn|aux|nul|com[0-9]|lpt[0-9])(?:\.|$)",RegexOptions.IgnoreCase)) throw new IOException("Invalid Windows folder or filename: "+s);
        }
        internal static string ArchivePath(string path)
        {
            string n=path.Replace('\\','/').TrimEnd('/');
            if (n.StartsWith("/") || n.Length==0 || n.Contains(":")) throw new IOException("Unsafe archive path: "+path);
            foreach (string s in n.Split('/')) Segment(s);
            return n;
        }
        internal static string Under(string root,string relative)
        {
            string r=Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
            string p=Path.GetFullPath(Path.Combine(r,relative.Replace('/',Path.DirectorySeparatorChar)));
            if (!p.StartsWith(r,StringComparison.OrdinalIgnoreCase)) throw new IOException("Path escapes its destination: "+relative);
            return p;
        }
        internal static void NoReparse(string path)
        {
            // Wine marks some real mount points as reparse points. Native lstat distinguishes
            // those from links, and still rejects any symlink in the managed path.
            if (LinuxPlatform.IsLinux) { string full = Path.GetFullPath(path); LinuxPlatform.ValidateContainedPath(full, Path.GetPathRoot(full)); return; }
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint)!=0) throw new IOException("Links/junctions are unsupported for managed files: "+path);
        }
        internal static void NoReparseChain(string path)
        {
            if (LinuxPlatform.IsLinux) { string full = Path.GetFullPath(path); LinuxPlatform.ValidateContainedPath(full, Path.GetPathRoot(full)); return; }
            string current=Path.GetFullPath(path);
            while (!String.IsNullOrEmpty(current))
            {
                if (Directory.Exists(current) || File.Exists(current)) NoReparse(current);
                current=Path.GetDirectoryName(current);
            }
        }
        internal static void DeleteGenerated(string path,string root)
        {
            string expected=Under(root,Path.GetFileName(path));
            if (!Path.GetFullPath(path).Equals(expected,StringComparison.OrdinalIgnoreCase)) throw new IOException("Cleanup path failed validation.");
            NoReparseChain(path);
            if (Directory.Exists(path))
            {
                foreach (string child in Directory.EnumerateFileSystemEntries(path)) { NoReparse(child); if (Directory.Exists(child)) DeleteGenerated(child,path); else File.Delete(child); }
                Directory.Delete(path);
            }
        }
    }
    public static class Utilities
    {
        public static string FriendlyBytes(long bytes) { if (bytes>=1024L*1024*1024) return (bytes/(1024.0*1024*1024)).ToString("0.0")+" GB"; if (bytes>=1024*1024) return (bytes/(1024.0*1024)).ToString("0.0")+" MB"; if (bytes>=1024) return (bytes/1024.0).ToString("0.0")+" KB"; return bytes+" B"; }
    }
}
