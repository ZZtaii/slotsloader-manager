using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SlotsInstaller
{
    internal static class InstallModes
    {
        internal const string Slot = "slot", Replace = "replace";
        internal static string Label(string mode) { return mode == Replace ? "Replace original" : "Extra outfit (Slotsloader)"; }
    }

    internal static class ReplaceMethods
    {
        internal const string Loose = "loose", Pak = "pak";
        internal static string Label(string method) { return method == Pak ? "PAK method" : "REFramework loose file loader"; }
    }

    // Data\replace-settings.json. Existing settings.json is unchanged.
    public sealed class ReplaceSettings
    {
        public string Format = "sf6-replace-settings-v1";
        public string DefaultMode = "slot";
        public string LastMode = "";
        public string StoreLocation = "";
        public string Method = "";               // "" = automatic (2.4.1): loose while the loose file loader is on, else pak
        public bool LooseLoaderConsent;           // asked once before turning the loader on
        public bool SkipLooseLoaderPrompt;        // 2.1.0: "Don't ask again" for installing with the loader off (older files: false)
        public bool RenameCrashReporter = true;
        public bool SwapRepairs;                  // 1.0.5: off by default; known fixes for character-swap mods (SwapRepairs.cs, SwapRepairFlag)
        public List<string> FluffyRoots = new List<string>();
        internal bool Relocated;                  // StoreLocation was read from a moved Data folder (not serialized)

        internal static string FilePath { get { return Path.Combine(AppConfig.DataDir, "replace-settings.json"); } }
        internal static ReplaceSettings Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return new ReplaceSettings();
                var s = Json.Read<ReplaceSettings>(FilePath);
                if (s == null || s.Format != "sf6-replace-settings-v1") return new ReplaceSettings();
                if (s.FluffyRoots == null) s.FluffyRoots = new List<string>();
                // A custom store inside the app's own Data folder moves with it (2.1.0); saved with the next Save().
                var relocation = new RelocationReport(AppConfig.DataDir);
                string store = DataRelocation.Rebase(s.StoreLocation, AppConfig.DataDir, relocation);
                if (relocation.Rebased > 0) { s.StoreLocation = store; s.Relocated = true; }
                DataRelocation.Log(relocation, FilePath);
                return s;
            }
            catch (Exception ex) { Program.Log(ex); return new ReplaceSettings(); }
        }
        internal void Save() { Directory.CreateDirectory(AppConfig.DataDir); Json.Write(FilePath, this); }
        internal string StoreRoot(string dataDir) { return String.IsNullOrWhiteSpace(StoreLocation) ? Path.Combine(dataDir, "replace-store") : Path.GetFullPath(StoreLocation); }
        internal string StartMode() { string m = String.IsNullOrEmpty(LastMode) ? DefaultMode : LastMode; return m == InstallModes.Replace ? m : InstallModes.Slot; }
    }

    // Data\replace-state.json: everything this app installed into the game folder.
    public sealed class ReplaceState
    {
        public string Format = "sf6-replace-state-v1";
        public int Generation;
        public string GameRoot = "";
        public string Method = "";
        public string StoreRoot = "";
        public List<ReplaceMod> Mods = new List<ReplaceMod>();     // index 0 = highest priority
        public List<DeployedFile> Deployed = new List<DeployedFile>();
        public List<OriginalBackup> Originals = new List<OriginalBackup>();
        public List<PakStamp> Paks = new List<PakStamp>();
        public PakStamp BaseAtInstall;                              // base PAK identity when mods were last applied
        public bool CrashReporterRenamed;
        public List<string> CreatedDirs = new List<string>();      // game folders this app created (removed when empty)
        public List<ReleasedFile> Released = new List<ReleasedFile>();     // 2.2.0: game files left to another tool ("Keep current file")
        public List<ForeignCopy> ForeignCopies = new List<ForeignCopy>();  // 2.2.0: other tools' files replaced by "Restore original", kept in Data
        public string UpdatedUtc = "";
        public List<string> PreviousStoreRoots = new List<string>();     // 2.4.2: earlier store roots (their .removed copies stay listed)

        internal ReplaceMod Find(string id) { return Mods.FirstOrDefault(m => String.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase)); }
        internal DeployedFile Deployment(string path) { return Deployed.FirstOrDefault(d => String.Equals(d.Path, path, StringComparison.OrdinalIgnoreCase)); }
        internal OriginalBackup Original(string path) { return Originals.FirstOrDefault(o => String.Equals(o.Path, path, StringComparison.OrdinalIgnoreCase)); }
        internal ReleasedFile Release(string path) { return Released.FirstOrDefault(r => String.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase)); }
    }

    // 2.2.0: a game file another tool overwrote after this app installed it, kept by "Keep current file". This
    // app no longer installs or restores it (Reapply takes it back); the genuine file's backup stays restorable.
    public sealed class ReleasedFile
    {
        public string Path = "", ForeignSha = "", Changer = "", ReleasedUtc = "";
        public long ForeignSize;
        public OriginalBackup Original;           // null when the file did not exist before this app
    }

    // 2.2.0: a file another tool had put in place, replaced by "Restore original" (or Reapply); kept in Data.
    public sealed class ForeignCopy
    {
        public string Path = "", Copy = "", Sha256 = "", Changer = "", KeptUtc = "";
        public long Size;
    }

    // How an uninstall/restore treats game files another tool changed after this app installed them.
    internal enum DriftChoice
    {
        Refuse,           // other changes (enable/disable/priority/install): refused with the changer named
        Ask,              // uninstall / restore-all: DriftException lists the files, the caller asks and retries
        RestoreOriginal,  // genuine file back (or the next replacement's copy); the other tool's file is kept in Data
        KeepCurrent,      // the other tool's file stays and this app stops managing it; the genuine backup stays restorable
        Repair            // Reapply: put the installed version back (the other tool's file is kept in Data)
    }

    internal sealed class DriftItem
    {
        internal string Path = "", Changer = "";
        internal bool Missing, HasOriginal, Kept;   // Kept: a file kept earlier with "Keep current file" (restore-all)
        internal string Text { get { return Missing ? Path + " is missing (removed by " + Changer + ")" : Path + " was replaced by " + Changer; } }
    }

    internal sealed class DriftException : IOException
    {
        internal readonly List<DriftItem> Items;
        internal DriftException(string message, List<DriftItem> items) : base(message) { Items = items; }
    }

    public sealed class ReplaceMod
    {
        public string Id = "", Name = "", SourcePath = "", Kind = "", InstalledUtc = "", Description = "";
        public List<string> SelectedIds = new List<string>();
        public bool Enabled = true, Test;
        public List<ModFile> Files = new List<ModFile>();
        public List<string> Notes = new List<string>();
        public string Affects = "";
    }

    public sealed class ModFile
    {
        public string Path = "", Sha256 = "", Fixes = "";
        public long Size;
    }

    public sealed class DeployedFile
    {
        public string Path = "", Owner = "", Sha256 = "";
        public long Size;
        public List<ZeroedRow> Zeroed = new List<ZeroedRow>();
    }

    public sealed class ZeroedRow
    {
        public string Pak = "";          // relative to the game root
        public int Index;
        public uint Lower, Upper;
    }

    public sealed class OriginalBackup
    {
        public string Path = "", Backup = "", Sha256 = "";
        public long Size;
    }

    public sealed class PakStamp
    {
        public string Pak = "", LayoutSha = "", TocSha = "", Backup = "";
        public long Length;
        public uint Fingerprint;
    }

    // Data\replace-journal.json: intent written before any game write.
    public sealed class ReplaceJournal
    {
        public string Format = "sf6-replace-journal-v1";
        public string OperationId = "", Description = "", StartedUtc = "";
        public int NewGeneration;
        public List<JournalOp> Ops = new List<JournalOp>();
        public List<string> CreatedPaths = new List<string>();    // deleted when rolled back
        public List<string> CleanupAfterCommit = new List<string>();
        public List<string> CreatedDirs = new List<string>();     // game-relative folders created by this operation
        public List<TrashMove> TrashAfterCommit = new List<TrashMove>();   // recoverable uninstalls (2.0.1)
    }

    public sealed class JournalOp
    {
        public string Kind = "";     // file, pak, move
        // file: make Path go from Before (sha or null = absent) to After; sources recreate either side.
        public string Path = "", BeforeSha, BeforeSource, AfterSha, AfterSource;
        // move: rename Path -> Target.
        public string Target;
        // pak: rows of one PAK.
        public string Pak; public long PakLength; public string PakHeader;
        public List<JournalRow> Rows = new List<JournalRow>();
    }

    public sealed class JournalRow
    {
        public int Index;
        public string Before = "", After = "";   // 16 hex chars (8 bytes)
    }

    // A file ready to deploy: prepared in the store (or a scratch folder for checks).
    internal sealed class PreparedFile
    {
        internal string Path, Source, Sha256, EntryPath, Component;
        internal long Size;
        internal List<FixResult> Fixes = new List<FixResult>();
        internal bool Native { get { return Path.StartsWith("natives/", StringComparison.OrdinalIgnoreCase); } }
    }

    internal sealed class HealthItem
    {
        internal string Level;   // info, fix, warn, error
        internal string Text;
        internal HealthItem(string level, string text) { Level = level; Text = text; }
    }

    // Another installed replacement that touches the same files or the same outfit/area.
    //   slot  - same outfit (or same non-costume area) and shared files: warning, with an Uninstall link
    //   files - shared files in another area (e.g. the character's shared parts): warning, no link
    //   stack - same outfit, no shared files: both stay installed (information)
    internal sealed class ReplaceConflict
    {
        internal string Kind = "", ModId = "", ModName = "", InstalledUtc = "", Text = "", Level = "warn";
        internal bool Enabled, NewWins = true;
        internal int Priority, TheirFiles, Shared;
        internal List<string> Areas = new List<string>();
        internal List<string> SharedPaths = new List<string>();
        internal bool CanUninstall { get { return Kind == "slot" && !String.IsNullOrEmpty(ModId); } }
    }

    // The replace-mode equivalent of a composition review: what will change and what may go wrong.
    internal sealed class ReplacePlan
    {
        internal string ModName = "", SourcePath = "", Kind = "", Method = "";
        internal List<string> SelectedIds = new List<string>();
        internal List<PreparedFile> Files = new List<PreparedFile>();
        internal List<string> Affects = new List<string>();
        internal List<HealthItem> Items = new List<HealthItem>();
        internal int VanillaReplaced, NewFiles, RootFiles, RowsToInvalidate, Fixed, Conflicts;
        internal long Bytes;
        internal List<string> InvalidationPreview = new List<string>();
        internal List<ReplaceConflict> ConflictList = new List<ReplaceConflict>();
        internal List<string> UnknownLoose = new List<string>();     // natives files already in the game folder, not installed by this app
        internal string StagingFolder;
        internal long Milliseconds;
        internal bool CanInstall { get { return Files.Count > 0 && !Items.Any(i => i.Level == "error"); } }
        internal IEnumerable<string> Errors { get { return Items.Where(i => i.Level == "error").Select(i => i.Text); } }
        internal string AffectsText { get { return Affects.Count == 0 ? (Kind == "Pak" ? "A new patch PAK" : RootFiles > 0 ? "Game folder (tool mod)" : "Other game files") : String.Join(", ", Affects.Take(3)) + (Affects.Count > 3 ? " +" + (Affects.Count - 3) : ""); } }
        // The first conflict worth a footer line: same outfit with shared files, then other shared files.
        internal ReplaceConflict Headline { get { return ConflictList.FirstOrDefault(c => c.Kind == "slot") ?? ConflictList.FirstOrDefault(c => c.Kind == "files"); } }
        internal static ReplacePlan Failed(string modName, string sourcePath, string message)
        {
            var plan = new ReplacePlan { ModName = modName ?? "", SourcePath = sourcePath ?? "" };
            plan.Items.Add(new HealthItem("error", "Health check failed: " + message));
            return plan;
        }
    }

    // <store>\.removed\<id>-<time>-<rand>\record.json + files\: a replacement removed with Uninstall,
    // restorable exactly (files, enabled state and priority).
    public sealed class ReplaceRemovedRecord
    {
        public string Format = "sf6-replace-removed-v1";
        public string GameRoot = "", RemovedUtc = "";
        public int Index;
        public ReplaceMod Mod = new ReplaceMod();
    }

    internal sealed class RemovedReplacement
    {
        internal string Folder;
        internal ReplaceRemovedRecord Record;
        internal long Bytes;
        internal string Name { get { return Record.Mod.Name; } }
    }

    // A removed mod's store folder moves to the recoverable area once the change is committed.
    public sealed class TrashMove
    {
        public string From = "", To = "";
        public string RecordJson = "";
    }

    internal sealed class GameFilesStatus
    {
        internal bool PakChanged, BaseChanged, LooseLoaderOff, JournalPending;
        internal int RowsNotZero, FilesMissing, FilesChanged;
        internal int BackupsRecorded, BackupsMissing, StoredMissing;     // 2.1.0: backups in Data checked on disk
        internal string FirstMissingBackup, FirstMissingStored;
        internal RelocationReport Relocation;                            // paths read from a moved Data folder
        internal List<string> ChangedBy = new List<string>();            // 2.2.0: "amd_ags_x64.dll (by the external mod manager's mod 'X')"
        internal int Released, ForeignCopies;                            // 2.2.0: kept files / other tools' copies kept in Data
        internal List<string> Details = new List<string>();
        internal List<string> OrphanCostumePaks = new List<string>();   // 2.4.0: loader costume paks with no SlotsLoader installed
        internal int OrphanedRows;                                       // 2.4.2: zeroed PAK rows no installed mod owns (PakRepair.cs)
        internal List<string> OrphanedRowsWhere = new List<string>();    // "re_chunk_000.pak #11886"
        internal List<string> LeftoverPaks = new List<string>();         // 1.0.11: patch PAKs no mod manager lists (ReplaceLeftoverPaks.cs)
        internal bool NeedsReapply { get { return PakChanged || RowsNotZero > 0 || FilesMissing > 0 || FilesChanged > 0; } }
        internal string Summary
        {
            get
            {
                if (JournalPending) return "An interrupted change will be rolled back.";
                if (NeedsReapply) return "Game files changed. Reapply your replacements" + (Details.Count > 0 ? " (" + Details[0] + ")" : "");
                if (BackupsMissing > 0) return BackupsMissing + (BackupsMissing == 1 ? " backup is" : " backups are") + " missing, so uninstalling can't restore the original" + (BackupsMissing == 1 ? "" : "s") + " (" + FirstMissingBackup + ")";
                if (StoredMissing > 0) return StoredMissing + " stored replacement cop" + (StoredMissing == 1 ? "y is" : "ies are") + " missing. Reinstall " + (StoredMissing == 1 ? "that mod" : "those mods") + " (" + FirstMissingStored + ")";
                if (BaseChanged) return "SF6 was updated after your replacements were applied. Run a health check on them.";
                if (OrphanedRows > 0) return PakRepair.OrphanText(OrphanedRows) + ".";   // 2.4.2: the banner's Repair
                if (OrphanCostumePaks.Count > 0) return LoaderCostumePak.OrphanText + ".";
                if (LooseLoaderOff) return "Loose file loader off: SF6 ignores loose replaced files.";   // 2.4.1: the button says what to do
                return "";
            }
        }
    }
}
