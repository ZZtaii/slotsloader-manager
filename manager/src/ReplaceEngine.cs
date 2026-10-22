using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace SlotsInstaller
{
    // The game's PAKs: base, DLC (both may be invalidated) and patch PAKs (never modified).
    internal sealed class GamePaks
    {
        internal PakIndex Base;
        internal List<PakIndex> Dlc = new List<PakIndex>();
        internal List<PakIndex> Patches = new List<PakIndex>();
        internal IEnumerable<PakIndex> Writable { get { if (Base != null) yield return Base; foreach (var p in Dlc) yield return p; } }

        internal static GamePaks Load(string gameRoot)
        {
            var paks = new GamePaks();
            string basePath = Path.Combine(gameRoot, "re_chunk_000.pak");
            if (File.Exists(basePath)) paks.Base = Cached(basePath);
            string dlc = Path.Combine(gameRoot, "dlc");
            if (Directory.Exists(dlc)) foreach (string f in Directory.GetFiles(dlc, "*.pak").OrderBy(x => x, StringComparer.OrdinalIgnoreCase)) paks.Dlc.Add(Cached(f));
            // 2.5.6: a patch PAK that can't be read (a PAK mod's, say) is skipped, never a reason every change fails.
            if (Directory.Exists(gameRoot))
                foreach (string f in Directory.GetFiles(gameRoot, "re_chunk_000.pak.patch_*.pak").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                    try { paks.Patches.Add(Cached(f)); }
                    catch (Exception ex) { if (!(ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException)) throw; Program.LogLine("Unreadable patch PAK skipped: " + Path.GetFileName(f) + " (" + ex.Message + ")"); }
            return paks;
        }

        private static readonly object cacheLock = new object();
        private static readonly Dictionary<string, PakIndex> cache = new Dictionary<string, PakIndex>(StringComparer.OrdinalIgnoreCase);
        internal static PakIndex Cached(string path)
        {
            var info = new FileInfo(path);
            string key = Path.GetFullPath(path);
            lock (cacheLock)
            {
                PakIndex found;
                if (cache.TryGetValue(key, out found) && found.Length == info.Length && found.LastWriteUtc == info.LastWriteTimeUtc) return found;
            }
            PakIndex index = PakIndex.Open(path);
            lock (cacheLock) { if (cache.Count > 16) cache.Clear(); cache[key] = index; }
            return index;
        }
        internal static void Forget(string path) { lock (cacheLock) cache.Remove(Path.GetFullPath(path)); }

        // Rows in base/DLC PAKs whose (non-zero) hash is this path's hash.
        internal List<KeyValuePair<PakIndex, int>> Rows(string relativePath)
        {
            ulong h = PakHash.PathHash(relativePath);
            var rows = new List<KeyValuePair<PakIndex, int>>();
            foreach (PakIndex p in Writable) foreach (int i in p.Find(h)) rows.Add(new KeyValuePair<PakIndex, int>(p, i));
            return rows;
        }
        // 2.5.7: the Manager's own PAK mods (patch PAKs it installed) are not the game's patch PAKs: a replacement of a file
        // one of them also holds is an overlap between two mods (the PAK mod wins in game), never a reason to refuse.
        internal readonly HashSet<string> ModPaks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        internal void SetModPaks(IEnumerable<ReplaceMod> mods) { foreach (ReplaceMod m in mods ?? new ReplaceMod[0]) foreach (ModFile f in m.Files) if (ReplaceEngine.IsPatchPakPath(f.Path)) ModPaks.Add(Path.GetFileName(f.Path)); }
        internal PakIndex ModPakContaining(string relativePath)
        {
            ulong h = PakHash.PathHash(relativePath);
            return Patches.FirstOrDefault(p => ModPaks.Contains(Path.GetFileName(p.Path)) && p.Contains(h));
        }
        internal PakIndex PatchContaining(string relativePath)
        {
            ulong h = PakHash.PathHash(relativePath);
            return Patches.FirstOrDefault(p => !ModPaks.Contains(Path.GetFileName(p.Path)) && p.Contains(h));
        }
        internal bool Vanilla(string relativePath) { ulong h = PakHash.PathHash(relativePath); return Writable.Any(p => p.Contains(h)); }
    }

    internal sealed partial class ReplaceEngine
    {
        internal readonly string GameRoot, DataDir;
        internal readonly ReplaceSettings Settings;
        internal Func<FluffyInstalls> FluffyProvider;
        // Test hooks: simulate a crash after N completed steps (no in-process rollback).
        internal static int CrashAfterStep = -1;
        internal sealed class SimulatedCrashException : Exception { internal SimulatedCrashException() : base("Simulated crash for a recovery test.") { } }

        internal ReplaceEngine(string gameRoot, string dataDir, ReplaceSettings settings)
        {
            GameRoot = Path.GetFullPath(gameRoot).TrimEnd(Path.DirectorySeparatorChar);
            DataDir = Path.GetFullPath(dataDir);
            Settings = settings ?? new ReplaceSettings();
        }

        internal static ReplaceEngine ForConfig(AppConfig config)
        {
            var settings = ReplaceSettings.Load();
            var engine = new ReplaceEngine(config.GameRoot, AppConfig.DataDir, settings);
            engine.FluffyProvider = delegate { return FluffyInstalls.Load(FluffyInstalls.DetectRoots(config.Libraries, settings.FluffyRoots)); };
            return engine;
        }

        internal string StatePath { get { return Path.Combine(DataDir, "replace-state.json"); } }
        internal string JournalPath { get { return Path.Combine(DataDir, "replace-journal.json"); } }
        internal string BackupDir { get { return Path.Combine(DataDir, "replace-backups"); } }
        internal string PakBackupDir { get { return Path.Combine(DataDir, "pak-backups"); } }
        internal string ConfigBackupDir { get { return Path.Combine(DataDir, "refw-config-backups"); } }
        internal string StoreRoot(ReplaceState state) { return !String.IsNullOrEmpty(state.StoreRoot) && state.Mods.Count > 0 ? state.StoreRoot : Settings.StoreRoot(DataDir); }
        internal string GamePath(string relative) { return Safety.Under(GameRoot, relative); }

        internal FluffyInstalls Fluffy() { return FluffyProvider == null ? new FluffyInstalls() : FluffyProvider(); }

        // ---- State
        internal ReplaceState LoadState() { RelocationReport report; return LoadState(out report); }
        internal ReplaceState LoadState(out RelocationReport relocation)
        {
            relocation = new RelocationReport(DataDir);
            if (!StateFileExists()) return new ReplaceState { GameRoot = GameRoot };
            ReplaceState s = ReadJsonShared<ReplaceState>(StatePath);   // 2.4.2: shared with a concurrent commit (ReplaceEngineRecovery.cs)
            if (s == null || s.Format != "sf6-replace-state-v1") throw new IOException("replace-state.json is unreadable. Move it aside only if you know the game files are clean.");
            if (s.Mods == null) s.Mods = new List<ReplaceMod>(); if (s.Deployed == null) s.Deployed = new List<DeployedFile>();
            if (s.Originals == null) s.Originals = new List<OriginalBackup>(); if (s.Paks == null) s.Paks = new List<PakStamp>(); if (s.CreatedDirs == null) s.CreatedDirs = new List<string>();
            if (s.Released == null) s.Released = new List<ReleasedFile>(); if (s.ForeignCopies == null) s.ForeignCopies = new List<ForeignCopy>();
            if (s.PreviousStoreRoots == null) s.PreviousStoreRoots = new List<string>();
            if ((s.Mods.Count > 0 || s.Deployed.Count > 0) && !String.Equals(Path.GetFullPath(s.GameRoot ?? "").TrimEnd(Path.DirectorySeparatorChar), GameRoot, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Replacements are recorded for another game folder (" + s.GameRoot + "). Switch Settings back to that folder and restore it first.");
            s.GameRoot = GameRoot;
            relocation = RelocateState(s);
            return s;
        }

        // ---- Relocation (2.1.0): the app folder may move with its Data (see DataRelocation).
        // Recorded backups and the default store are read from the current Data folder when their
        // recorded location is gone; the corrected paths are saved by the next state write.
        private RelocationReport RelocateState(ReplaceState s)
        {
            var report = new RelocationReport(DataDir);
            // An empty record keeps its store path although no store folder may exist: not "missing".
            var quiet = new RelocationReport(DataDir);
            s.StoreRoot = DataRelocation.Rebase(s.StoreRoot, DataDir, s.Mods.Count > 0 ? report : quiet, GameRoot);
            report.AddRebased(quiet);
            s.PreviousStoreRoots = s.PreviousStoreRoots.Select(r => DataRelocation.Rebase(r, DataDir, null, GameRoot)).ToList();   // 2.4.2, informational
            foreach (OriginalBackup o in s.Originals) o.Backup = DataRelocation.Rebase(o.Backup, DataDir, report, GameRoot);
            foreach (ReleasedFile r in s.Released) if (r.Original != null) r.Original.Backup = DataRelocation.Rebase(r.Original.Backup, DataDir, report, GameRoot);
            foreach (ForeignCopy c in s.ForeignCopies) c.Copy = DataRelocation.Rebase(c.Copy, DataDir, report, GameRoot);
            foreach (PakStamp p in s.Paks) p.Backup = DataRelocation.Rebase(p.Backup, DataDir, report, GameRoot);
            if (s.BaseAtInstall != null) s.BaseAtInstall.Backup = DataRelocation.Rebase(s.BaseAtInstall.Backup, DataDir, report, GameRoot);
            DataRelocation.Log(report, StatePath);
            return report;
        }

        // Journal sources (stored copies, original backups) and the paths it creates or cleans up.
        // Targets that do not exist yet (a removed copy's .removed folder) follow their parent folder.
        private void RelocateJournal(ReplaceJournal j)
        {
            var report = new RelocationReport(DataDir);
            var quiet = new RelocationReport(DataDir);
            foreach (JournalOp op in j.Ops ?? new List<JournalOp>())
            {
                op.BeforeSource = DataRelocation.Rebase(op.BeforeSource, DataDir, report, GameRoot);
                op.AfterSource = DataRelocation.Rebase(op.AfterSource, DataDir, report, GameRoot);
            }
            j.CreatedPaths = (j.CreatedPaths ?? new List<string>()).Select(p => DataRelocation.RebaseTarget(p, DataDir, quiet, GameRoot)).ToList();
            j.CleanupAfterCommit = (j.CleanupAfterCommit ?? new List<string>()).Select(p => DataRelocation.RebaseTarget(p, DataDir, quiet, GameRoot)).ToList();
            foreach (TrashMove move in j.TrashAfterCommit ?? new List<TrashMove>())
            {
                move.From = DataRelocation.RebaseTarget(move.From, DataDir, quiet, GameRoot);
                move.To = DataRelocation.RebaseTarget(move.To, DataDir, quiet, GameRoot);
            }
            report.AddRebased(quiet);
            DataRelocation.Log(report, JournalPath);
        }

        // Saves rebased paths right away: replace-state.json, pak-backups\*.json and a custom store
        // location that was inside the moved Data folder. Only Data records are written, under the
        // game lock; a pending journal is left to Recover(). Returns a message, or null when nothing moved.
        internal string PersistRelocation()
        {
            if (!RelocationPending()) return null;
            using (Lock())
            {
                if (JournalPending) return null;
                int saved = 0; var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (File.Exists(StatePath))
                {
                    RelocationReport report; ReplaceState s = LoadState(out report);
                    if (report.Rebased > 0) { DurableFile.WriteJson(StatePath, s); saved += report.Rebased; roots.UnionWith(report.OldRoots); }
                }
                if (Directory.Exists(PakBackupDir))
                    foreach (string meta in Directory.GetFiles(PakBackupDir, "*.json"))
                    {
                        try
                        {
                            PakStamp stamp = Json.Read<PakStamp>(meta);
                            if (stamp == null) continue;
                            var report = new RelocationReport(DataDir);
                            string backup = DataRelocation.Rebase(stamp.Backup, DataDir, report, GameRoot);
                            if (report.Rebased == 0) continue;
                            stamp.Backup = backup; DurableFile.WriteJson(meta, stamp); saved++; roots.UnionWith(report.OldRoots);
                        }
                        catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); }
                    }
                if (Settings.Relocated && String.Equals(DataRelocation.Normalize(DataDir), DataRelocation.Normalize(AppConfig.DataDir), StringComparison.OrdinalIgnoreCase))
                {
                    // Saved from a fresh read: in-memory overrides (the CLI's --method) are not persisted.
                    ReplaceSettings stored = ReplaceSettings.Load();
                    if (stored.Relocated) { stored.Save(); saved++; }
                    Settings.Relocated = false;
                }
                if (saved == 0) return null;
                string message = "Manager folder moved: " + ReplacePlanner.N(saved, "recorded path") + " updated to " + DataDir + (roots.Count > 0 ? " (from " + String.Join(", ", roots) + ")" : "") + ".";
                DataRelocation.WriteLog(DataDir, "Relocation saved: " + message);
                return message;
            }
        }

        // Lock-free pre-check: does any record still name a moved Data folder?
        private bool RelocationPending()
        {
            if (JournalPending) return false;
            if (Settings.Relocated) return true;
            try
            {
                if (File.Exists(StatePath)) { RelocationReport report; LoadState(out report); if (report.Rebased > 0) return true; }
                if (Directory.Exists(PakBackupDir))
                    foreach (string meta in Directory.GetFiles(PakBackupDir, "*.json"))
                    {
                        PakStamp stamp = Json.Read<PakStamp>(meta);
                        var report = new RelocationReport(DataDir);
                        if (stamp != null) DataRelocation.Rebase(stamp.Backup, DataDir, report, GameRoot);
                        if (report.Rebased > 0) return true;
                    }
            }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; }   // unreadable records are reported by their own readers
            return false;
        }

        private ReplaceState Clone(ReplaceState s) { return Json.Serializer().Deserialize<ReplaceState>(Json.Serializer().Serialize(s)); }

        internal string CurrentMethod(ReplaceState state)
        {
            if (state.Mods.Count > 0 || state.Deployed.Count > 0) return String.IsNullOrEmpty(state.Method) ? ReplaceMethods.Pak : state.Method;
            return DesiredMethod();
        }
        internal string DesiredMethod()
        {
            if (Settings.Method == ReplaceMethods.Loose || Settings.Method == ReplaceMethods.Pak) return Settings.Method;
            // 2.4.1: Automatic picks the method that works now: the loose file loader only when it is on; otherwise the
            // Fluffy PAK method, which invalidates the PAK entries so SF6 reads the loose files without the loader.
            // (2.4.0 picked loose whenever REFramework was installed, so with the loader off the game ignored them.)
            string detail;
            return ToolStatus.Detect(GameRoot).ReFramework && LooseLoader.State(GameRoot, out detail) == LooseLoaderState.Enabled ? ReplaceMethods.Loose : ReplaceMethods.Pak;
        }

        // ---- Locking
        internal IDisposable Lock()
        {
            string key;
            using (var sha = FastHash.Sha256()) key = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(GameRoot.ToUpperInvariant()))).Replace("-", "");
            var mutex = new Mutex(false, "Local\\SF6SlotsInstaller-game-" + key);
            bool acquired;
            try { acquired = mutex.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) { mutex.Dispose(); throw new IOException("Another Manager task is changing the game folder. Try again when it finishes."); }
            Interlocked.Increment(ref activeLocks);
            return new Releaser(mutex);
        }
        // 2.3.0: game locks held in this process (the portrait reader waits while a change runs).
        private static int activeLocks;
        internal static int ActiveLocks { get { return Thread.VolatileRead(ref activeLocks); } }
        private sealed class Releaser : IDisposable
        {
            private Mutex mutex; internal Releaser(Mutex m) { mutex = m; }
            public void Dispose() { if (mutex != null) { mutex.ReleaseMutex(); mutex.Dispose(); mutex = null; Interlocked.Decrement(ref activeLocks); } }
        }

        // ---- Hash helpers
        internal static string Sha(string file)
        {
            using (var sha = FastHash.Sha256())
            using (var s = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1 << 16))
                return BitConverter.ToString(sha.ComputeHash(s)).Replace("-", "").ToLowerInvariant();
        }
        internal static string Sha(byte[] data) { using (var sha = FastHash.Sha256()) return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "").ToLowerInvariant(); }
        private static string ShaOrNull(string file) { return File.Exists(file) ? Sha(file) : null; }
        private static string Hex(byte[] b) { return BitConverter.ToString(b).Replace("-", ""); }
        private static byte[] Unhex(string s) { var b = new byte[s.Length / 2]; for (int i = 0; i < b.Length; i++) b[i] = Convert.ToByte(s.Substring(2 * i, 2), 16); return b; }
        private static readonly byte[] Zero8 = new byte[8];
        private HashSet<string> removableDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private bool deferRenames;

        // ---- Recovery
        // Completes or rolls back an interrupted operation. Returns a message, or null when none was pending.
        internal string Recover()
        {
            if (!File.Exists(JournalPath)) return null;
            ReplaceJournal journal = ReadJsonShared<ReplaceJournal>(JournalPath);
            if (journal == null || journal.Format != "sf6-replace-journal-v1") throw new IOException("The replacement journal is unreadable: " + JournalPath + ". Nothing was changed; inspect it before continuing.");
            RelocateJournal(journal);
            ReplaceState state = LoadState();
            if (state.Generation >= journal.NewGeneration)
            {
                Cleanup(journal.CleanupAfterCommit);
                string trash = FinishTrash(journal.TrashAfterCommit);
                File.Delete(JournalPath);
                RecordRecovery(journal, true);
                return "Interrupted change '" + journal.Description + "' had finished; leftovers cleaned up." + (trash == null ? "" : " " + trash);
            }
            removableDirs = new HashSet<string>(state.CreatedDirs, StringComparer.OrdinalIgnoreCase);
            Rollback(journal);
            File.Delete(JournalPath);
            RecordRecovery(journal, false);
            return "Interrupted change '" + journal.Description + "' rolled back; game files match the last saved state.";
        }

        internal bool JournalPending { get { return File.Exists(JournalPath); } }
        private bool PendingJournalTouchesPak()
        {
            if (!JournalPending) return false;
            try { ReplaceJournal j = ReadJsonShared<ReplaceJournal>(JournalPath); return j == null || j.Ops == null || j.Ops.Any(o => o.Kind == "pak"); }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; return true; }
        }

        private void Rollback(ReplaceJournal journal)
        {
            var errors = new List<string>();
            foreach (string d in journal.CreatedDirs ?? new List<string>()) removableDirs.Add(d);
            for (int i = journal.Ops.Count - 1; i >= 0; i--)
            {
                try { Undo(journal.Ops[i]); }
                catch (Exception ex) { if (ex is OutOfMemoryException) throw; errors.Add(ex.Message); }
            }
            if (errors.Count > 0) throw new IOException("Rolling back '" + journal.Description + "' could not restore everything: " + String.Join(" ", errors) + " The journal is kept in " + JournalPath + "; the Manager retries at next start.");
            foreach (string created in journal.CreatedPaths.AsEnumerable().Reverse()) DeleteCreated(created);
        }

        private void DeleteCreated(string path)
        {
            try
            {
                if (!IsUnder(path, DataDir) && !IsUnder(path, Settings.StoreRoot(DataDir)) && !IsStorePath(path)) return;
                if (File.Exists(path)) File.Delete(path);
                else if (Directory.Exists(path)) Directory.Delete(path, true);
            }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); }
        }
        private bool IsStorePath(string path)
        {
            try { ReplaceState s = File.Exists(StatePath) ? LoadState() : null; return s != null && !String.IsNullOrEmpty(s.StoreRoot) && IsUnder(path, s.StoreRoot); } catch { return false; }
        }
        private static bool IsUnder(string path, string root)
        {
            if (String.IsNullOrEmpty(path) || String.IsNullOrEmpty(root)) return false;
            string r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return Path.GetFullPath(path).StartsWith(r, StringComparison.OrdinalIgnoreCase);
        }
        private void Cleanup(IEnumerable<string> paths) { foreach (string p in paths ?? new string[0]) DeleteCreated(p); }

        // ---- Operations (forward / undo; both idempotent)
        private void Do(JournalOp op)
        {
            if (op.Kind == "file") SetFile(op.Path, op.BeforeSha, op.AfterSha, op.AfterSource);
            else if (op.Kind == "move") Move(op.Path, op.Target);
            else if (op.Kind == "pak") WriteRows(op, false);
            else throw new IOException("Unknown journal operation " + op.Kind);
        }
        private void Undo(JournalOp op)
        {
            if (op.Kind == "file") SetFile(op.Path, op.AfterSha, op.BeforeSha, op.BeforeSource);
            else if (op.Kind == "move") Move(op.Target, op.Path);
            else if (op.Kind == "pak") WriteRows(op, true);
        }

        private void SetFile(string relative, string fromSha, string toSha, string toSource)
        {
            string full = GamePath(relative);
            Safety.NoReparseChain(full);
            string now = CurrentSha(relative, full);
            if (now == toSha) return;
            if (now != fromSha) throw new IOException(relative + " changed outside this operation; left as it is.");
            if (toSha == null)
            {
                // 2.5.6: a PAK mod's patch PAK below another one becomes an empty placeholder: the game reads patch PAKs
                // as an unbroken row from patch_001, so a gap would hide every PAK after it.
                if (IsPatchPakPath(relative) && HigherPatchPakExists(relative))
                {
                    if (!IsFluffyDummy(full)) { string dummy = full + ".slotsinstaller-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp"; File.WriteAllBytes(dummy, FluffyDummyPak); try { DurableFile.Publish(dummy, full); } finally { DurableFile.Discard(dummy, full, true); } }
                    return;
                }
                File.Delete(full);
                RemoveEmptyParents(full, relative.StartsWith("natives/", StringComparison.OrdinalIgnoreCase));
                return;
            }
            if (String.IsNullOrEmpty(toSource) || !File.Exists(toSource)) throw new IOException("The copy needed for " + relative + " is missing: " + toSource);
            Directory.CreateDirectory(Path.GetDirectoryName(full));
            string tmp = full + ".slotsinstaller-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
            bool complete = false;
            try
            {
                using (var input = new FileStream(toSource, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var output = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
                { input.CopyTo(output, 1 << 16); output.Flush(true); }
                if (Sha(tmp) != toSha) throw new IOException("The stored copy of " + relative + " does not match its recorded hash.");
                complete = true;
                // 1.0.11: a scanner holding the game file for a moment failed Enable/Disable ("Unable to remove the file to
                // be replaced"); it is retried (DurableFile.Publish). A replace that already removed the target leaves
                // the new copy in its place, which the journal's rollback recognises.
                DurableFile.Publish(tmp, full);
            }
            finally { DurableFile.Discard(tmp, full, complete); }
        }

        // natives folders are always cleaned up when empty; other folders only when this app created them.
        private void RemoveEmptyParents(string file, bool natives)
        {
            string stop = GameRoot.TrimEnd(Path.DirectorySeparatorChar);
            string dir = Path.GetDirectoryName(file);
            while (!String.IsNullOrEmpty(dir) && dir.Length > stop.Length && IsUnder(dir, stop))
            {
                if (Directory.EnumerateFileSystemEntries(dir).Any()) break;
                if (!natives && !removableDirs.Contains(RelativeToGame(dir))) break;
                Directory.Delete(dir);
                dir = Path.GetDirectoryName(dir);
            }
        }

        private void Move(string from, string to)
        {
            string a = GamePath(from), b = GamePath(to);
            if (!File.Exists(a) && File.Exists(b)) return;
            if (File.Exists(a) && !File.Exists(b)) { File.Move(a, b); return; }
            throw new IOException("Cannot rename " + from + " to " + to + ": unexpected files present.");
        }

        private void WriteRows(JournalOp op, bool undo)
        {
            string pak = GamePath(op.Pak);
            var writes = op.Rows.Select(r => new PakHashWrite { Index = r.Index, Expected = Unhex(undo ? r.After : r.Before), Value = Unhex(undo ? r.Before : r.After) }).ToList();
            PakWriter.WriteHashes(pak, op.PakLength, Unhex(op.PakHeader), writes);
            GamePaks.Forget(pak);
        }

        // ---- Transactions
        private void Execute(ReplaceState current, ReplaceState next, ReplaceJournal journal, GamePaks paks)
        {
            journal.NewGeneration = current.Generation + 1;
            next.Generation = journal.NewGeneration;
            next.UpdatedUtc = DateTime.UtcNow.ToString("o");
            Directory.CreateDirectory(DataDir);
            removableDirs = new HashSet<string>(current.CreatedDirs.Concat(journal.CreatedDirs), StringComparer.OrdinalIgnoreCase);
            DurableFile.WriteJson(JournalPath, journal);
            int step = 0;
            using (OperationContext.NonCancellable())
            {
                try
                {
                    foreach (JournalOp op in journal.Ops)
                    {
                        if (CrashAfterStep == step) throw new SimulatedCrashException();
                        OperationContext.Report("Applying game file changes", step, journal.Ops.Count);
                        Do(op); step++;
                    }
                    if (CrashAfterStep == step) throw new SimulatedCrashException();
                    StampPaks(next);
                    next.CreatedDirs = next.CreatedDirs.Concat(journal.CreatedDirs).Distinct(StringComparer.OrdinalIgnoreCase).Where(d => Directory.Exists(GamePath(d))).ToList();
                    DurableFile.WriteJson(StatePath, next);
                    if (CrashAfterStep == step + 1) throw new SimulatedCrashException();
                }
                catch (SimulatedCrashException) { throw; }
                catch (Exception ex)
                {
                    if (ex is OutOfMemoryException) throw;
                    try { Rollback(journal); File.Delete(JournalPath); }
                    catch (Exception rollback) { throw new IOException(ex.Message + " " + rollback.Message, ex); }
                    throw;
                }
                Cleanup(journal.CleanupAfterCommit);
                trashWarning = FinishTrash(journal.TrashAfterCommit) ?? trashWarning;
                File.Delete(JournalPath);
            }
            ManagerInstalls.Write(GameRoot, next);   // 1.0.9: the list other costume loaders read (never throws)
        }

        // Recoverable uninstall: the removed mod's stored files and its record move to <store>\.removed.
        // 2.4.2: a move that fails is remembered and retried (RetryTrash) and the returned warning reaches the
        // result message; before, it was only logged and the UI said the copy was kept.
        private string FinishTrash(IEnumerable<TrashMove> moves)
        {
            string warning = null;
            foreach (TrashMove move in moves ?? new TrashMove[0])
            {
                try { MoveToRemoved(move); }
                catch (Exception ex)
                {
                    if (ex is OutOfMemoryException) throw;
                    Program.Log(ex); RememberTrash(move);
                    if (warning == null) warning = TrashFailedText(TrashReason(ex));
                }
            }
            return warning;
        }

        // Record each PAK we hold rows in: identity ignoring hash fields, and its full table hash now.
        private void StampPaks(ReplaceState next)
        {
            var names = next.Deployed.SelectMany(d => d.Zeroed).Select(z => z.Pak).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var stamps = new List<PakStamp>();
            foreach (string name in names)
            {
                PakIndex p = PakIndex.Open(GamePath(name), false);
                PakStamp old = next.Paks.FirstOrDefault(s => String.Equals(s.Pak, name, StringComparison.OrdinalIgnoreCase));
                stamps.Add(new PakStamp { Pak = name, Length = p.Length, Fingerprint = p.Fingerprint, LayoutSha = p.LayoutSha256(), TocSha = p.TocSha256(), Backup = old == null ? "" : old.Backup });
            }
            next.Paks = stamps;
            string basePath = Path.Combine(GameRoot, "re_chunk_000.pak");
            if (File.Exists(basePath))
            {
                PakIndex b = GamePaks.Cached(basePath);
                next.BaseAtInstall = new PakStamp { Pak = "re_chunk_000.pak", Length = b.Length, Fingerprint = b.Fingerprint, LayoutSha = b.LayoutSha256(), TocSha = b.TocSha256() };
            }
        }

        // Full TOC copy (header + entry table) before the first change of each PAK identity.
        internal string EnsureTocBackup(PakIndex pak)
        {
            Directory.CreateDirectory(PakBackupDir);
            string layout = pak.LayoutSha256();
            string stem = Path.GetFileName(pak.Path) + "-" + pak.Length + "-" + pak.Fingerprint.ToString("X8") + "-" + layout.Substring(0, 12);
            string toc = Path.Combine(PakBackupDir, stem + ".toc"), meta = Path.Combine(PakBackupDir, stem + ".json");
            if (File.Exists(toc) && File.Exists(meta)) return toc;
            byte[] bytes = pak.Header.Concat(pak.Table).ToArray();
            DurableFile.Write(toc, bytes);
            DurableFile.WriteJson(meta, new PakStamp { Pak = Path.GetFileName(pak.Path), Length = pak.Length, Fingerprint = pak.Fingerprint, LayoutSha = layout, TocSha = pak.TocSha256(), Backup = toc });
            return toc;
        }

        private string RelativeToGame(string full)
        {
            string root = GameRoot + Path.DirectorySeparatorChar;
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new IOException("Path outside the game folder: " + full);
            return full.Substring(root.Length).Replace('\\', '/');
        }

        internal string StoreFile(ReplaceState state, ReplaceMod mod, ModFile file) { return Safety.Under(Safety.Under(StoreRoot(state), mod.Id), file.Path); }

        // Builds the journal that turns the game files of `current` into those of `next`.
        // Pre-existing files we overwrite are backed up now (and recorded in next.Originals).
        // 2.2.0: files another tool changed after this app installed them follow options.Choice (see DriftChoice);
        // the changer (a Fluffy mod, a known SlotsLoader/AGS build or "another tool") is named in every message.
        private ReplaceJournal Reconcile(ReplaceState current, ReplaceState next, string method, GamePaks paks, FluffyInstalls fluffy, ChangeOptions options, string description, List<string> messages)
        {
            var journal = new ReplaceJournal { OperationId = Guid.NewGuid().ToString("N"), Description = description, StartedUtc = DateTime.UtcNow.ToString("o") };
            paks.SetModPaks(current.Mods); paks.SetModPaks(next.Mods);   // 2.5.7
            next.Method = method;
            next.StoreRoot = StoreRoot(current);
            RememberStoreRoot(current, next);   // 2.4.2: the store moved in Settings: its .removed copies stay listed
            var desired = new Dictionary<string, KeyValuePair<ReplaceMod, ModFile>>(StringComparer.OrdinalIgnoreCase);
            foreach (ReplaceMod mod in next.Mods.Where(m => m.Enabled))
                foreach (ModFile f in mod.Files)
                    if (!desired.ContainsKey(f.Path)) desired[f.Path] = new KeyValuePair<ReplaceMod, ModFile>(mod, f);
            var paths = new HashSet<string>(desired.Keys, StringComparer.OrdinalIgnoreCase);
            foreach (DeployedFile d in current.Deployed) paths.Add(d.Path);
            foreach (ReleasedFile r in current.Released) paths.Add(r.Path);
            var restoreRows = new Dictionary<PakIndex, List<JournalRow>>();
            var zeroRows = new Dictionary<PakIndex, List<JournalRow>>();
            var fileOps = new List<JournalOp>();
            var deployed = new List<DeployedFile>();
            var originals = current.Originals.Select(o => o).ToList();
            var keptReleased = new List<ReleasedFile>();
            var foreignCopies = current.ForeignCopies.Select(c => c).ToList();
            var drift = new List<DriftItem>();
            int staleDropped = 0;
            Func<string, PakIndex> pakByName = delegate(string name) { string full = GamePath(name); return paks.Writable.FirstOrDefault(p => String.Equals(p.Path, full, StringComparison.OrdinalIgnoreCase)); };
            Func<ZeroedRow, bool> rowValid = delegate(ZeroedRow z)
            {
                PakIndex p = pakByName(z.Pak);
                PakStamp stamp = current.Paks.FirstOrDefault(s => String.Equals(s.Pak, z.Pak, StringComparison.OrdinalIgnoreCase));
                return p != null && stamp != null && p.Length == stamp.Length && p.Fingerprint == stamp.Fingerprint && p.LayoutSha256() == stamp.LayoutSha && z.Index < p.Count;
            };
            Action<Dictionary<PakIndex, List<JournalRow>>, PakIndex, JournalRow> addRow = delegate(Dictionary<PakIndex, List<JournalRow>> map, PakIndex p, JournalRow r)
            {
                List<JournalRow> list; if (!map.TryGetValue(p, out list)) map[p] = list = new List<JournalRow>();
                if (!list.Any(x => x.Index == r.Index)) list.Add(r);
            };
            Func<PakIndex, int, string> rowBytes = delegate(PakIndex p, int i) { var e = p.Entry(i); return Hex(PakWriter.HashBytes(e.HashLower, e.HashUpper)); };

            foreach (string path in paths.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                OperationContext.ThrowIfCancellationRequested();
                DeployedFile was = current.Deployment(path);
                KeyValuePair<ReplaceMod, ModFile> want; bool wanted = desired.TryGetValue(path, out want);
                string full = GamePath(path);
                Safety.NoReparseChain(full);
                string actual = CurrentSha(path, full);   // 2.5.6: an empty placeholder patch PAK counts as no file
                OriginalBackup original = originals.FirstOrDefault(o => String.Equals(o.Path, path, StringComparison.OrdinalIgnoreCase));
                DriftChoice choice = options.Choice;
                string beforeSource = null;

                // A file kept earlier with "Keep current file": left alone unless Reapply takes it back, or its
                // genuine original is restored (Restore original game file / restore-all).
                ReleasedFile rel = current.Release(path);
                if (rel != null)
                {
                    bool retake = wanted && options.Choice == DriftChoice.Repair && fluffy.Owner(path) == null && !IsPatchPakPath(path);   // 1.0.1: never over another tool's PAK
                    bool askedOriginal = options.RestoreReleased != null && options.RestoreReleased.Contains(path);
                    bool restore = !wanted && rel.Original != null && (askedOriginal || (options.IncludeReleased && options.Choice == DriftChoice.RestoreOriginal));
                    if (!wanted && rel.Original != null && options.IncludeReleased && options.Choice == DriftChoice.Ask && actual != rel.Original.Sha256 && !askedOriginal)
                    { drift.Add(new DriftItem { Path = path, Changer = rel.Changer, Missing = actual == null, HasOriginal = true, Kept = true }); keptReleased.Add(rel); continue; }
                    if (!retake && !restore) { if (wanted || rel.Original != null) keptReleased.Add(rel); continue; }
                    if (rel.Original != null && original == null) { original = rel.Original; originals.Add(original); }
                    choice = DriftChoice.RestoreOriginal;   // the other tool's file is kept in Data
                }

                if (wanted && was == null && rel == null)
                {
                    FluffyMod fluffyOwner = fluffy.Owner(path);
                    if (fluffyOwner != null) throw new IOException(path + " belongs to the mod '" + fluffyOwner.Name + "', installed by an external mod manager. Disable it there, then install again.");
                }
                ReleasedFile release = null;   // "Keep current file" decided now
                if (actual != null)
                {
                    ReplaceMod owner = was == null ? null : current.Find(was.Owner);
                    ModFile ownerFile = owner == null ? null : owner.Files.FirstOrDefault(f => String.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase));
                    if (was != null && actual == was.Sha256 && ownerFile != null && ownerFile.Sha256 == actual) beforeSource = StoreFile(current, owner, ownerFile);
                    else if (original != null && actual == original.Sha256) beforeSource = original.Backup;
                    else if (was != null && actual == was.Sha256)
                    {
                        // Our own deployment whose stored copy was refreshed since: a temporary copy recreates it on rollback.
                        string own = Path.Combine(BackupDir, "deployed-" + Guid.NewGuid().ToString("N") + ".bin");
                        Directory.CreateDirectory(BackupDir); File.Copy(full, own);
                        journal.CreatedPaths.Add(own); journal.CleanupAfterCommit.Add(own); beforeSource = own;
                    }
                    else if (was == null && original == null && rel == null)
                    {
                        // 2.5.6: a PAK mod never takes over a patch number another PAK uses (it would hide that PAK).
                        if (wanted && IsPatchPakPath(path)) throw new IOException(path + " is already used by another PAK now. Uninstall this PAK mod and install it again: it gets the next free number.");
                        if (wanted)
                        {
                            string backup = Path.Combine(BackupDir, Guid.NewGuid().ToString("N") + ".bin");
                            Directory.CreateDirectory(BackupDir); File.Copy(full, backup);
                            if (Sha(backup) != actual) throw new IOException("Could not back up " + path + " exactly.");
                            journal.CreatedPaths.Add(backup);
                            original = new OriginalBackup { Path = path, Backup = backup, Sha256 = actual, Size = new FileInfo(full).Length };
                            originals.Add(original); beforeSource = backup;
                        }
                    }
                    else
                    {
                        // Changed by another tool after this app installed it.
                        string changer = rel != null && !String.IsNullOrEmpty(rel.Changer) ? rel.Changer : KnownBuilds.Changer(path, actual, fluffy);
                        // An uninstall or restore only decides about the files it changes; another mod's changed file
                        // that keeps its owner is left as it is (the banner still reports it).
                        bool affected = rel != null || was == null || !wanted || !String.Equals(was.Owner, want.Key.Id, StringComparison.OrdinalIgnoreCase) || want.Value.Sha256 != was.Sha256;
                        if (!affected && (choice == DriftChoice.Ask || choice == DriftChoice.RestoreOriginal || choice == DriftChoice.KeepCurrent)) { deployed.Add(was); continue; }
                        // 1.0.1: another PAK at this patch number belongs to another tool: Reapply never overwrites it and
                        // Restore original never deletes it (either would break that tool's mods); the Manager lets go.
                        if (IsPatchPakPath(path) && (choice == DriftChoice.Repair || choice == DriftChoice.RestoreOriginal)) choice = DriftChoice.KeepCurrent;
                        if (choice == DriftChoice.Repair || choice == DriftChoice.RestoreOriginal)
                        {
                            string keep = Path.Combine(BackupDir, "changed-" + Guid.NewGuid().ToString("N") + ".bin");
                            Directory.CreateDirectory(BackupDir); File.Copy(full, keep);
                            if (Sha(keep) != actual) throw new IOException("Could not keep an exact copy of " + path + ".");
                            journal.CreatedPaths.Add(keep);
                            foreignCopies.Add(new ForeignCopy { Path = path, Copy = keep, Sha256 = actual, Size = new FileInfo(keep).Length, Changer = changer, KeptUtc = DateTime.UtcNow.ToString("o") });
                            beforeSource = keep; messages.Add(path + " had been replaced by " + changer + "; the original is back and that tool's version was removed.");
                        }
                        else if (choice == DriftChoice.KeepCurrent)
                            release = new ReleasedFile { Path = path, ForeignSha = actual, ForeignSize = new FileInfo(full).Length, Changer = changer, ReleasedUtc = DateTime.UtcNow.ToString("o"), Original = original };
                        else { drift.Add(new DriftItem { Path = path, Changer = changer, HasOriginal = original != null }); continue; }
                    }
                }
                else if (was != null && !wanted && original != null)
                {
                    // Our file is gone (deleted by another tool or by hand). Restore original brings the genuine file back.
                    string changer = KnownBuilds.Changer(path, null, fluffy);
                    if (choice == DriftChoice.Ask) { drift.Add(new DriftItem { Path = path, Changer = changer, Missing = true, HasOriginal = true }); continue; }
                    if (choice == DriftChoice.KeepCurrent) release = new ReleasedFile { Path = path, ForeignSha = "", ForeignSize = 0, Changer = changer, ReleasedUtc = DateTime.UtcNow.ToString("o"), Original = original };
                }
                if (release == null && was != null && actual != null && actual == was.Sha256 && fluffy.Owner(path) != null && (!wanted || !String.Equals(was.Owner, want.Key.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    // A Fluffy mod lists this file too (it installed the same copy over this app's): removing it would
                    // break that mod, so it stays for Fluffy; the genuine original stays restorable.
                    release = new ReleasedFile { Path = path, ForeignSha = actual, ForeignSize = new FileInfo(full).Length, Changer = KnownBuilds.Changer(path, actual, fluffy), ReleasedUtc = DateTime.UtcNow.ToString("o"), Original = original };
                }
                if (release != null)
                {
                    // Keep current file: this app stops managing the path; its PAK entries are restored unless a
                    // Fluffy mod now lists the file (Fluffy holds them for its own copy).
                    if (original != null) originals.Remove(original);
                    if (wanted || release.Original != null) keptReleased.Add(release);
                    bool fluffyHolds = fluffy.Owner(path) != null;
                    if (was != null)
                        foreach (ZeroedRow z in was.Zeroed)
                        {
                            if (!rowValid(z)) { staleDropped++; continue; }
                            PakIndex p = pakByName(z.Pak);
                            if (!fluffyHolds && p.Entry(z.Index).Zeroed) addRow(restoreRows, p, new JournalRow { Index = z.Index, Before = Hex(Zero8), After = Hex(PakWriter.HashBytes(z.Lower, z.Upper)) });
                        }
                    messages.Add(path + " stays as " + release.Changer + " left it; the Manager stops tracking it" + (release.Original != null ? " (the game's original is kept: Restore original game files puts it back)." : IsPatchPakPath(path) && wanted ? ". Reinstall the PAK mod to give it the next free number." : "."));
                    continue;
                }
                var zeroedNow = new List<ZeroedRow>();
                if (was != null)
                    foreach (ZeroedRow z in was.Zeroed)
                    {
                        if (!rowValid(z)) { staleDropped++; continue; }
                        PakIndex p = pakByName(z.Pak); var e = p.Entry(z.Index);
                        bool keepZero = wanted && method == ReplaceMethods.Pak && path.StartsWith("natives/", StringComparison.OrdinalIgnoreCase);
                        if (keepZero)
                        {
                            if (!e.Zeroed) addRow(zeroRows, p, new JournalRow { Index = z.Index, Before = rowBytes(p, z.Index), After = Hex(Zero8) });
                            zeroedNow.Add(z);
                        }
                        else if (e.Zeroed) addRow(restoreRows, p, new JournalRow { Index = z.Index, Before = Hex(Zero8), After = Hex(PakWriter.HashBytes(z.Lower, z.Upper)) });
                    }
                if (wanted)
                {
                    string target = StoreFile(next, want.Key, want.Value);
                    if (actual == null)
                        for (string dir = Path.GetDirectoryName(full); !String.IsNullOrEmpty(dir) && dir.Length > GameRoot.Length && !Directory.Exists(dir); dir = Path.GetDirectoryName(dir))
                        { string relDir = RelativeToGame(dir); if (!journal.CreatedDirs.Contains(relDir, StringComparer.OrdinalIgnoreCase)) journal.CreatedDirs.Add(relDir); }
                    if (actual != want.Value.Sha256) fileOps.Add(new JournalOp { Kind = "file", Path = path, BeforeSha = actual, BeforeSource = beforeSource, AfterSha = want.Value.Sha256, AfterSource = target });
                    if (method == ReplaceMethods.Pak && path.StartsWith("natives/", StringComparison.OrdinalIgnoreCase))
                    {
                        PakIndex patch = paks.PatchContaining(path);
                        if (patch != null) throw new IOException(path + " is in the patch PAK " + Path.GetFileName(patch.Path) + ", which is never modified: the PAK method can't replace it.");
                        foreach (var row in paks.Rows(path))
                        {
                            if (zeroedNow.Any(z => String.Equals(z.Pak, RelativeToGame(row.Key.Path), StringComparison.OrdinalIgnoreCase) && z.Index == row.Value)) continue;
                            var e = row.Key.Entry(row.Value);
                            addRow(zeroRows, row.Key, new JournalRow { Index = row.Value, Before = Hex(PakWriter.HashBytes(e.HashLower, e.HashUpper)), After = Hex(Zero8) });
                            zeroedNow.Add(new ZeroedRow { Pak = RelativeToGame(row.Key.Path), Index = row.Value, Lower = e.HashLower, Upper = e.HashUpper });
                        }
                    }
                    deployed.Add(new DeployedFile { Path = path, Owner = want.Key.Id, Sha256 = want.Value.Sha256, Size = want.Value.Size, Zeroed = zeroedNow });
                }
                else
                {
                    string targetSha = original == null ? null : original.Sha256;
                    if (actual != targetSha) fileOps.Add(new JournalOp { Kind = "file", Path = path, BeforeSha = actual, BeforeSource = beforeSource, AfterSha = targetSha, AfterSource = original == null ? null : original.Backup });
                    if (original != null) { originals.Remove(original); journal.CleanupAfterCommit.Add(original.Backup); }
                }
            }
            if (drift.Count > 0)
            {
                foreach (string created in journal.CreatedPaths) DeleteCreated(created);
                throw new DriftException(DriftMessage(drift, options.Choice), drift);
            }
            if (staleDropped > 0) messages.Add("Dropped " + ReplacePlanner.N(staleDropped, "PAK invalidation") + " from before a game update (the update restored " + (staleDropped == 1 ? "that entry)." : "those entries)."));
            next.Deployed = deployed;
            next.Originals = originals;
            next.Released = keptReleased;
            next.ForeignCopies = foreignCopies;

            // Crash reporter rename while replacements are installed (Fluffy does the same).
            bool anyEnabled = next.Mods.Any(m => m.Enabled);
            if (deferRenames) anyEnabled = current.CrashReporterRenamed;   // decided again on the next change with the game closed
            string crash = GamePath(CrashReporter.Name), renamed = GamePath(CrashReporter.RenamedName);
            var moves = new List<JournalOp>();
            next.CrashReporterRenamed = current.CrashReporterRenamed;
            if (anyEnabled && Settings.RenameCrashReporter && !current.CrashReporterRenamed && File.Exists(crash) && !File.Exists(renamed))
            { moves.Add(new JournalOp { Kind = "move", Path = CrashReporter.Name, Target = CrashReporter.RenamedName }); next.CrashReporterRenamed = true; }
            else if (!anyEnabled && current.CrashReporterRenamed)
            {
                if (File.Exists(renamed) && !File.Exists(crash)) moves.Add(new JournalOp { Kind = "move", Path = CrashReporter.RenamedName, Target = CrashReporter.Name });
                next.CrashReporterRenamed = false;
            }

            // PAK changes need a TOC backup and the header/length they were planned against.
            foreach (PakIndex p in zeroRows.Keys) { string b = EnsureTocBackup(p); var stamp = next.Paks.FirstOrDefault(s => String.Equals(s.Pak, RelativeToGame(p.Path), StringComparison.OrdinalIgnoreCase)); if (stamp == null) next.Paks.Add(new PakStamp { Pak = RelativeToGame(p.Path), Backup = b }); else stamp.Backup = b; }
            Func<PakIndex, List<JournalRow>, JournalOp> pakOp = delegate(PakIndex p, List<JournalRow> rows)
            { return new JournalOp { Kind = "pak", Pak = RelativeToGame(p.Path), PakLength = p.Length, PakHeader = Hex(p.Header), Rows = rows.OrderBy(r => r.Index).ToList() }; };
            journal.Ops.AddRange(restoreRows.Select(kv => pakOp(kv.Key, kv.Value)));
            journal.Ops.AddRange(fileOps);
            journal.Ops.AddRange(moves);
            journal.Ops.AddRange(zeroRows.Select(kv => pakOp(kv.Key, kv.Value)));
            return journal;
        }

        // How one change treats files other tools changed (2.2.0) and which kept files it restores.
        private sealed class ChangeOptions
        {
            internal DriftChoice Choice = DriftChoice.Refuse;
            internal HashSet<string> RestoreReleased;   // "Restore original game file" for these kept paths
            internal bool IncludeReleased;              // restore-all: kept files are part of the question
        }

        private static string DriftMessage(List<DriftItem> items, DriftChoice choice)
        {
            DriftItem first = items[0];
            string more = items.Count > 1 ? " (" + (items.Count - 1) + " more: " + String.Join(", ", items.Skip(1).Take(4).Select(i => i.Path)) + (items.Count > 5 ? ", …" : "") + ")" : "";
            string what = first.Missing ? first.Path + " is missing: removed after the Manager installed it (" + first.Changer + ")"
                : first.Path + " was replaced by " + first.Changer + " after the Manager installed it";
            if (choice == DriftChoice.Ask)
                return what + more + ". Choose Restore original (the game file comes back; the other tool's file is removed) or Keep current file (it stays; the Manager stops tracking it, and Restore original game files can still put the original back).";
            return what + more + ". Reapply puts the installed version back (the changed file is replaced), or uninstall and choose Restore original or Keep current file.";
        }

        // "Install Cammy Alt" -> "install Cammy Alt" for "Close SF6 to …" (mod names keep their case).
        private static string ActionText(string description) { return String.IsNullOrEmpty(description) ? "" : Char.ToLowerInvariant(description[0]) + description.Substring(1); }

        // Shared frame: lock, idle checks, recovery, plan, execute.
        private string Change(string description, bool needsIdle, Func<ReplaceState, ReplaceState> mutate, bool repairDrift, string methodOverride)
        {
            return Change(description, needsIdle, mutate, new ChangeOptions { Choice = repairDrift ? DriftChoice.Repair : DriftChoice.Refuse }, methodOverride, null);
        }
        private string Change(string description, bool needsIdle, Func<ReplaceState, ReplaceState> mutate, ChangeOptions options, string methodOverride, Dictionary<string, string> trashFolders)
        {
            using (Lock())
            {
                bool gameRunning = false, fluffyRunning = false;
                if (needsIdle)
                {
                    // 2.1.0: an open Fluffy Mod Manager blocks only PAK edits (below); loose and tool-mod files go ahead.
                    // Fluffy's installed.ini is read again by Reconcile (Fluffy()), so its ownership is current.
                    fluffyRunning = GameProcesses.Probe.IsFluffyRunning();
                    gameRunning = GameProcesses.Probe.IsGameRunning();
                }
                if (fluffyRunning && PendingJournalTouchesPak()) throw new IOException(GameProcesses.FluffyPakMessage("finish the interrupted change"));
                if (!gameRunning) Recover();
                trashWarning = null;
                if (TrashRetryPending) { try { RetryTrashLocked(); } catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); } }   // 2.4.2
                ReplaceState current = LoadState();
                ReplaceState next = mutate(Clone(current));
                string method = methodOverride ?? CurrentMethod(current);
                if (current.Mods.Count == 0 && current.Deployed.Count == 0 && methodOverride == null) method = DesiredMethod();
                FluffyInstalls external = Fluffy();
                string locked = ExternalLockReason(external);   // 1.0.1: nothing the mutation staged is kept (each caller cleans up on a throw)
                if (locked != null && AddsGameFiles(current, next, options, methodOverride, CurrentMethod(current), external)) throw new IOException(locked);
                GamePaks paks = GamePaks.Load(GameRoot);
                if (method == ReplaceMethods.Pak && paks.Base == null && next.Mods.Any(m => m.Enabled && m.Files.Any(f => f.Path.StartsWith("natives/", StringComparison.OrdinalIgnoreCase))))
                    throw new IOException("re_chunk_000.pak is missing from the game folder, so the PAK method can't be used.");
                var messages = new List<string>();
                deferRenames = gameRunning || fluffyRunning;   // the crash reporter is Fluffy's too: renamed at the next change with both closed
                ReplaceJournal journal;
                try { journal = Reconcile(current, next, method, paks, Fluffy(), options, description, messages); }
                finally { deferRenames = false; }
                deferRenames = false;
                if (fluffyRunning && journal.Ops.Any(o => o.Kind == "pak"))
                {
                    foreach (string created in journal.CreatedPaths) DeleteCreated(created);
                    throw new IOException(GameProcesses.FluffyPakMessage(ActionText(description)));
                }
                if (gameRunning)
                {
                    // Like Fluffy with REFramework's loose file loader: natives files may be re-copied while
                    // SF6 runs (quick testing). PAK edits, game-folder tools and renames still need the game closed.
                    bool looseOnly = method == ReplaceMethods.Loose && journal.Ops.All(o => o.Kind == "file" && o.Path.StartsWith("natives/", StringComparison.OrdinalIgnoreCase));
                    if (!looseOnly || JournalPending)
                    {
                        foreach (string created in journal.CreatedPaths) DeleteCreated(created);
                        GameProcesses.RequireIdle(ActionText(description));
                    }
                    messages.Add("Changed while SF6 runs: switch outfits or reload the match to load the new files.");
                }
                foreach (ReplaceMod gone in current.Mods.Where(m => next.Find(m.Id) == null))
                {
                    string storeDir = Safety.Under(StoreRoot(current), gone.Id), trash;
                    if (trashFolders != null && trashFolders.TryGetValue(gone.Id, out trash))
                    {
                        var record = new ReplaceRemovedRecord { GameRoot = GameRoot, RemovedUtc = DateTime.UtcNow.ToString("o"), Index = current.Mods.IndexOf(gone), Mod = gone };
                        string folder = Path.Combine(StoreRoot(current), RemovedFolderName, trash);
                        trashFolders[gone.Id] = folder;
                        journal.TrashAfterCommit.Add(new TrashMove { From = storeDir, To = folder, RecordJson = Json.Serializer().Serialize(record) });
                    }
                    else journal.CleanupAfterCommit.Add(storeDir);
                }
                foreach (ReplaceMod added in next.Mods.Where(m => current.Find(m.Id) == null)) journal.CreatedPaths.Insert(0, Safety.Under(StoreRoot(next), added.Id));
                Execute(current, next, journal, paks);
                PurgeForeignCopies();   // 1.0.1: another tool's file is kept only as this change's rollback (ReplaceEngineUninstall.cs)
                // 1.0.1: the patch row stays unbroken whatever left a gap (this change or another tool).
                if (!gameRunning && !fluffyRunning)
                    try { string gaps = GapText(FillPatchGaps()); if (gaps.Length > 0) messages.Add(gaps); }
                    catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); messages.Add("A patch PAK is missing and couldn't be filled (" + ex.Message.TrimEnd('.') + "); the PAKs after it may not load."); }
                RecordStateChange(description, current, next);   // 2.4.1 history (HistoryHooks.cs; never throws)
                if (trashWarning != null) messages.Add(trashWarning);   // 2.4.2: never "kept" when the copy wasn't saved
                return messages.Count == 0 ? "" : String.Join(" ", messages);
            }
        }

        // ---- Public API

        // Publishes a prepared plan (files staged in plan.StagingFolder under the store root).
        internal ReplaceMod Install(ReplacePlan plan, string name, bool test, string replaceId)
        {
            if (plan == null || !plan.CanInstall) throw new IOException(plan == null ? "Nothing to install." : String.Join(" ", plan.Errors));
            ReplaceMod mod = null; string storeDir = null;
            try
            {
                Change("Install " + name, true, delegate(ReplaceState state)
                {
                    ReplaceMod existing = String.IsNullOrEmpty(replaceId) ? null : state.Find(replaceId);
                    if (!String.IsNullOrEmpty(replaceId) && existing == null) throw new IOException("The replacement to update no longer exists. Refresh and try again.");
                    mod = new ReplaceMod { Id = Guid.NewGuid().ToString("N"), Name = name, SourcePath = plan.SourcePath, Kind = plan.Kind, InstalledUtc = DateTime.UtcNow.ToString("o"), SelectedIds = plan.SelectedIds.ToList(), Enabled = true, Test = test, Affects = plan.AffectsText };
                    mod.Files = plan.Files.Select(f => new ModFile { Path = f.Path, Sha256 = f.Sha256, Size = f.Size, Fixes = String.Join("; ", f.Fixes.Where(x => x.Changed).Select(x => x.Message)) }).ToList();
                    mod.Notes = plan.Items.Where(i => i.Level == "fix" || i.Level == "warn").Select(i => i.Text).ToList();
                    foreach (ModFile f in mod.Files) ValidateDestination(f.Path);
                    string root = StoreRoot(state);
                    storeDir = Safety.Under(root, mod.Id);
                    if (!IsUnder(plan.StagingFolder, root)) throw new IOException("Prepared files must be staged inside the replacement store.");
                    Installer.MoveDirectory(plan.StagingFolder, storeDir);
                    foreach (ModFile f in mod.Files)
                    {
                        string stored = Safety.Under(storeDir, f.Path);
                        if (!File.Exists(stored) || new FileInfo(stored).Length != f.Size) throw new IOException("The stored copy of " + f.Path + " is missing or changed.");
                    }
                    if (existing != null) { int at = state.Mods.IndexOf(existing); mod.Enabled = existing.Enabled; state.Mods[at] = mod; }
                    else state.Mods.Insert(0, mod);
                    if (String.IsNullOrEmpty(state.StoreRoot)) state.StoreRoot = root;
                    return state;
                }, false, null);
            }
            catch
            {
                if (storeDir != null && Directory.Exists(storeDir) && !LoadState().Mods.Any(m => mod != null && m.Id == mod.Id)) { try { Installer.MoveDirectory(storeDir, plan.StagingFolder); } catch { } }
                throw;
            }
            return mod;
        }

        internal string SetEnabled(string id, bool enabled)
        {
            return Change((enabled ? "Enable" : "Disable") + " replacement", true, delegate(ReplaceState s) { ReplaceMod m = Need(s, id); m.Enabled = enabled; return s; }, false, null);
        }
        internal string Move(string id, int delta)
        {
            int from = -1, target = -1;
            string result = Change("Change replacement priority", true, delegate(ReplaceState s)
            {
                ReplaceMod m = Need(s, id); int at = s.Mods.IndexOf(m), to = Math.Max(0, Math.Min(s.Mods.Count - 1, at + delta));
                from = at; target = to;
                s.Mods.RemoveAt(at); s.Mods.Insert(to, m); return s;
            }, false, null);
            RecordMove(id, from, target);   // 2.4.1 history
            return result;
        }
        // Uninstall keeps a recoverable copy (see RestoreRemoved); a reverted test install does not.
        // 2.2.0: files another tool changed never block an uninstall: Ask throws a DriftException naming them
        // (the window and the CLI ask, then call again with RestoreOriginal or KeepCurrent).
        internal string Uninstall(string id) { return Uninstall(id, DriftChoice.Ask); }
        internal string Uninstall(string id, DriftChoice choice) { string folder; return Uninstall(id, true, out folder, choice); }
        internal string Uninstall(string id, bool recoverable, out string removedFolder) { return Uninstall(id, recoverable, out removedFolder, recoverable ? DriftChoice.Ask : DriftChoice.Refuse); }
        internal string Uninstall(string id, bool recoverable, out string removedFolder, DriftChoice choice)
        {
            var trash = recoverable ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { { id, id + "-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6) } } : null;
            bool loader = LoaderCostumePak.IsLoaderMod(LoadState().Mods.FirstOrDefault(m => m.Id == id));
            string result = Change("Uninstall replacement", true, delegate(ReplaceState s) { s.Mods.Remove(Need(s, id)); return s; }, new ChangeOptions { Choice = choice }, null, trash);
            removedFolder = trash == null ? null : trash[id];
            return loader ? (result + " " + AfterLoaderRemoved()).Trim() : result;
        }

        internal const string RemovedFolderName = ".removed";

        // Removed replacements kept by Uninstall, newest first. Unreadable folders are skipped.
        internal List<RemovedReplacement> ListRemoved()
        {
            var result = new List<RemovedReplacement>();
            foreach (string root in RemovedRoots())   // 2.4.2: earlier store roots too (ReplaceEngineRecovery.cs)
            {
                string container = Path.Combine(root, RemovedFolderName);
                if (!Directory.Exists(container)) continue;
                foreach (string folder in Directory.GetDirectories(container))
                {
                    try { RemovedReplacement r = ReadRemoved(folder); if (r != null) result.Add(r); }
                    catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); }
                }
            }
            return result.OrderByDescending(r => r.Record.RemovedUtc, StringComparer.Ordinal).ToList();
        }

        internal RemovedReplacement ReadRemoved(string folder)
        {
            string full = Path.GetFullPath(folder);
            if (!String.Equals(Path.GetFileName(Path.GetDirectoryName(full)), RemovedFolderName, StringComparison.OrdinalIgnoreCase)) throw new IOException("Not a removed-replacement folder: " + folder);
            Safety.NoReparseChain(full);
            string recordFile = Path.Combine(full, "record.json"), files = Path.Combine(full, "files");
            if (!File.Exists(recordFile) || !Directory.Exists(files)) return null;
            ReplaceRemovedRecord record = Json.Read<ReplaceRemovedRecord>(recordFile);
            if (record == null || record.Format != "sf6-replace-removed-v1" || record.Mod == null || String.IsNullOrEmpty(record.Mod.Id) || record.Mod.Files == null) throw new IOException("Unrecognized removed-replacement record: " + recordFile);
            if (record.Mod.SelectedIds == null) record.Mod.SelectedIds = new List<string>();
            if (record.Mod.Notes == null) record.Mod.Notes = new List<string>();
            long bytes = 0; foreach (ModFile f in record.Mod.Files) { string p = Safety.Under(files, f.Path); if (File.Exists(p)) bytes += new FileInfo(p).Length; }
            return new RemovedReplacement { Folder = full, Record = record, Bytes = bytes };
        }

        // Puts a removed replacement back exactly: its files, enabled state and priority.
        internal string RestoreRemoved(string folder, out ReplaceMod restored)
        {
            RemovedReplacement removed = ReadRemoved(folder);
            if (removed == null) throw new IOException("That removed replacement is incomplete or was already restored.");
            if (!String.Equals(Path.GetFullPath(removed.Record.GameRoot ?? "").TrimEnd(Path.DirectorySeparatorChar), GameRoot, StringComparison.OrdinalIgnoreCase)) throw new IOException("'" + removed.Name + "' was removed from another game folder (" + removed.Record.GameRoot + ").");
            ReplaceMod mod = removed.Record.Mod;
            string files = Path.Combine(removed.Folder, "files");
            foreach (ModFile f in mod.Files)
            {
                OperationContext.ThrowIfCancellationRequested();
                string p = Safety.Under(files, f.Path);
                if (!File.Exists(p) || Sha(p) != f.Sha256) throw new IOException("The removed copy of '" + mod.Name + "' is incomplete: " + f.Path + " is missing or changed.");
            }
            string storeDir = null; bool done = false;
            try
            {
                string result = Change("Restore " + mod.Name, true, delegate(ReplaceState s)
                {
                    if (s.Find(mod.Id) != null) throw new IOException("'" + mod.Name + "' is already installed.");
                    string root = StoreRoot(s);
                    storeDir = Safety.Under(root, mod.Id);
                    if (Directory.Exists(storeDir)) throw new IOException("The replacement store already holds " + storeDir + ".");
                    foreach (ModFile f in mod.Files)
                    {
                        string target = Safety.Under(storeDir, f.Path);
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        File.Copy(Safety.Under(files, f.Path), target);
                    }
                    s.Mods.Insert(RestoreIndex(s.Mods, removed.Record), mod);   // 2.4.2: below replacements installed since
                    if (String.IsNullOrEmpty(s.StoreRoot)) s.StoreRoot = root;
                    return s;
                }, false, null);
                done = true;
                try { Directory.Delete(removed.Folder, true); } catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); }
                restored = mod;
                return result;
            }
            finally
            {
                if (!done && storeDir != null && Directory.Exists(storeDir)) { try { if (LoadState().Find(mod.Id) == null) Directory.Delete(storeDir, true); } catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); } }
            }
        }

        // Permanently deletes a removed replacement's retained copy.
        internal void DeleteRemoved(string folder)
        {
            if (IsLeftoverCopy(folder)) { DiscardLeftoverCopy(folder, null); return; }   // 1.0.11: a leftover PAK's Undo copy goes to the Recycle Bin
            RemovedReplacement removed = ReadRemoved(folder);
            if (removed == null && !Directory.Exists(folder)) return;
            Directory.Delete(Path.GetFullPath(folder), true);
        }
        internal string KeepTest(string id)
        {
            using (Lock())
            {
                ReplaceState s = LoadState(); ReplaceMod m = Need(s, id); if (!m.Test) return "";
                m.Test = false; s.Generation++; DurableFile.WriteJson(StatePath, s); return "";
            }
        }
        // Puts every installed replacement back (re-copies files, re-invalidates entries) after
        // a game update, a Steam file check or changes made by other tools. Re-runs the fixes
        // on stored files with the current game's type signatures first.
        // 2.4.0: SlotsLoader was uninstalled by this app. Its generated costume pak would keep loading the old
        // extra outfits with no loader to rebuild it: it is taken out of the row (LoaderCostumePak.Remove) unless a
        // SlotsLoader is still in the game folder (another copy, e.g. installed with Fluffy).
        internal string AfterLoaderRemoved()
        {
            try { return LoaderCostumePak.LoaderPresent(GameRoot) ? "" : LoaderCostumePak.Remove(GameRoot); }
            catch (Exception ex) { Program.Log(ex); return "The costume pak Slotsloader generated could not be checked (" + ex.Message.TrimEnd('.') + ")."; }
        }
        // The Installed page's "Remove it" (and CLI loader-pak-remove): refused while SF6 runs or a loader is installed.
        internal string RemoveOrphanedCostumePak()
        {
            if (GameProcesses.Probe.IsGameRunning()) throw new IOException("Close SF6 first: it keeps its paks open.");
            if (LoaderCostumePak.LoaderPresent(GameRoot)) throw new IOException("Slotsloader is installed; its costume pak is in use. Uninstall Slotsloader to remove it.");
            string result = LoaderCostumePak.Remove(GameRoot);
            return result.Length == 0 ? "No costume pak generated by Slotsloader was found." : result;
        }

        internal string Reapply()
        {
            string refixed = RefixStore();
            string result = Change("Reapply replacements", true, delegate(ReplaceState s) { return s; }, true, null);
            return (refixed + " " + result).Trim();
        }
        internal string RestoreAll() { return RestoreAll(DriftChoice.Ask); }
        internal string RestoreAll(DriftChoice choice) { List<string> kept; return RestoreAll(choice, out kept, false); }
        // 2.4.1: the window keeps a recoverable copy of every record (RestoreRemoved puts each back at its index).
        internal string RestoreAll(DriftChoice choice, out List<string> removedFolders) { return RestoreAll(choice, out removedFolders, true); }
        private string RestoreAll(DriftChoice choice, out List<string> removedFolders, bool recoverable)
        {
            ReplaceState before = LoadState();
            bool loader = before.Mods.Any(LoaderCostumePak.IsLoaderMod);
            string stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6);
            var trash = recoverable ? before.Mods.ToDictionary(m => m.Id, m => m.Id + "-" + stamp, StringComparer.OrdinalIgnoreCase) : null;
            string result = Change("Restore original game files", true, delegate(ReplaceState s) { s.Mods.Clear(); return s; }, new ChangeOptions { Choice = choice, IncludeReleased = true }, null, trash);
            removedFolders = trash == null ? new List<string>() : trash.Values.ToList();
            if (loader) result = (result + " " + AfterLoaderRemoved()).Trim();
            string natives = Path.Combine(GameRoot, "natives");
            if (Directory.Exists(natives))
            {
                int left = Directory.GetFiles(natives, "*", SearchOption.AllDirectories).Length;
                result = (result + " The natives folder still has " + ReplacePlanner.N(left, "file") + " the Manager didn't install (another mod manager's or manual mods).").Trim();
            }
            return result;
        }
        internal string SwitchMethod(string method)
        {
            if (method != ReplaceMethods.Loose && method != ReplaceMethods.Pak) throw new ArgumentException("Unknown method " + method);
            return Change("Switch the replacement method", true, delegate(ReplaceState s) { return s; }, false, method);
        }

        private static ReplaceMod Need(ReplaceState s, string id)
        {
            ReplaceMod m = s.Find(id); if (m == null) throw new IOException("That replacement no longer exists. Refresh the list."); return m;
        }

        private string RefixStore()
        {
            using (Lock())
            {
                ReplaceState state = LoadState();
                if (state.Mods.Count == 0) return "";
                GamePaks paks = GamePaks.Load(GameRoot);
                GameReference reference = GameReference.Load(GameRoot, paks.Base, paks.Dlc);
                reference.SwapRepairs = Settings.SwapRepairs;   // 1.0.5: Settings, off by default
                int changed = 0;
                foreach (ReplaceMod mod in state.Mods)
                    foreach (ModFile f in mod.Files)
                    {
                        if (!f.Path.StartsWith("natives/", StringComparison.OrdinalIgnoreCase)) continue;
                        string low = f.Path.ToLowerInvariant();
                        if (!low.Contains(".user.") && !low.Contains(".tex.") && !(reference.SwapRepairs && low.EndsWith(".scn.20"))) continue;
                        string stored = StoreFile(state, mod, f);
                        if (!File.Exists(stored) || new FileInfo(stored).Length > 256L * 1024 * 1024) continue;
                        List<FixResult> quick;
                        if (f.Path.IndexOf(".tex.", StringComparison.OrdinalIgnoreCase) >= 0 && ModFixes.TextureFileOk(stored, out quick)) continue;   // 1.0.1: nothing to repair (header only)
                        byte[] data = File.ReadAllBytes(stored);
                        List<FixResult> fixes = ModFixes.UpgradeModFile(ref data, reference, f.Path);
                        if (!fixes.Any(x => x.Changed)) continue;
                        string fresh = stored + ".refix-" + Guid.NewGuid().ToString("N").Substring(0, 6);
                        DurableFile.Write(fresh, data);
                        DurableFile.Publish(fresh, stored);
                        f.Sha256 = Sha(data); f.Size = data.Length; changed++;
                    }
                if (changed == 0) return "";
                state.Generation++; DurableFile.WriteJson(StatePath, state);
                return ReplacePlanner.N(changed, "stored file") + " updated for the current game version.";
            }
        }

        // 1.0.1: a program at the top of a mod (e.g. Slotsloader's SF6_SlotsClean.exe) that the game folder doesn't
        // have is a helper to run by hand: the planner skips it with a note. One the game has (its own exe) still blocks.
        internal static bool SkippedProgram(string path, string gameRoot)
        {
            string p = PakHash.Normalize(path).TrimStart('/');
            if (p.IndexOf('/') >= 0 || !p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return false;
            try { return !File.Exists(Path.Combine(gameRoot, p)); }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; return false; }
        }

        // 1.0.1 (user decision): while an external mod manager has any mod installed, the Manager doesn't change game
        // files beside it: only Extra outfits (costume_mods) and the Slotsloader Fork install. Replacements, PAK mods
        // and other tool mods wait until that manager is empty; uninstalling the Manager's own mods still works.
        internal static string ExternalLockReason(FluffyInstalls fluffy)
        {
            int n = fluffy == null ? 0 : fluffy.Mods.Count;
            return n == 0 ? null : "The external mod manager has " + ReplacePlanner.N(n, "mod") + ExternalLockTail;
        }
        private const string ExternalLockTail = " installed, so only Extra outfit, Slotsloader and REFramework install here. Uninstall them there to use Replace original.";
        // 1.0.11: the lockout among a health check's errors, and its short form ("The external mod manager has N mods").
        internal static bool IsExternalLock(string reason) { return reason != null && reason.EndsWith(ExternalLockTail, StringComparison.Ordinal); }
        internal static string ExternalLockShort(string reason) { return IsExternalLock(reason) ? reason.Substring(0, reason.Length - ExternalLockTail.Length) : reason; }
        internal string ExternalLockReason() { return ExternalLockReason(Fluffy()); }
        // 1.0.10 (the user): plain REFramework installs and updates past the lockout like Slotsloader (one dinput8.dll in
        // the game folder, no PAK or natives edits), unless the external manager installed dinput8.dll: then it is updated there.
        internal const string ReFrameworkFluffyReason = "The external mod manager installed REFramework (dinput8.dll), so update it there.";
        internal static bool ReFrameworkByFluffy(FluffyInstalls fluffy) { return fluffy != null && fluffy.OwnersOf("dinput8.dll").Count > 0; }
        // The lockout for one package's or record's files: null for Slotsloader and for plain REFramework the external manager doesn't own.
        internal static string ExternalLockReason(FluffyInstalls fluffy, IEnumerable<string> files)
        {
            List<string> list = (files ?? new string[0]).ToList();
            if (list.Any(p => String.Equals(PakHash.Normalize(p ?? "").TrimStart('/'), LoaderCostumePak.PluginPath, StringComparison.OrdinalIgnoreCase))) return null;
            string locked = ExternalLockReason(fluffy);
            if (locked == null || !ReplacePlanner.IsPlainReFramework(list)) return locked;
            return ReFrameworkByFluffy(fluffy) ? ReFrameworkFluffyReason : null;
        }

        // A change that puts game files in place: a mod added, enabled, updated or moved, Reapply, or a method switch.
        // Removing or disabling never does (the files under it are the game's or the Manager's own lower copies).
        private static bool AddsGameFiles(ReplaceState current, ReplaceState next, ChangeOptions options, string methodOverride, string currentMethod, FluffyInstalls fluffy)
        {
            if (options.Choice == DriftChoice.Repair) return true;
            if (methodOverride != null && !String.Equals(methodOverride, currentMethod, StringComparison.OrdinalIgnoreCase)) return true;
            foreach (ReplaceMod m in next.Mods.Where(x => x.Enabled && ExternalLockReason(fluffy, x.Files.Select(f => f.Path)) != null))   // Slotsloader, plain REFramework (1.0.10)
            {
                ReplaceMod was = current.Find(m.Id);
                if (was == null || !was.Enabled || !was.Files.Select(f => f.Path + "|" + f.Sha256).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).SequenceEqual(m.Files.Select(f => f.Path + "|" + f.Sha256).OrderBy(x => x, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase)) return true;
            }
            var before = current.Mods.Where(m => next.Find(m.Id) != null).Select(m => m.Id);
            var after = next.Mods.Where(m => current.Find(m.Id) != null).Select(m => m.Id);
            return !before.SequenceEqual(after, StringComparer.OrdinalIgnoreCase);
        }

        // Paths a mod may never write.
        internal static void ValidateDestination(string path)
        {
            string p = PakHash.Normalize(path).TrimStart('/');
            Safety.ArchivePath(p);
            string low = p.ToLowerInvariant(), leaf = low.Split('/').Last();
            bool root = low.IndexOf('/') < 0;
            // 2.5.6: a PAK mod's .pak arrives here already named as the next patch PAK (ReplacePlanner); nothing else may write one.
            if (!(root && IsPatchPakPath(p)) && (System.Text.RegularExpressions.Regex.IsMatch(leaf, @"^re_chunk_\d+\.pak") || (low.EndsWith(".pak") && !low.StartsWith("natives/"))))
                throw new IOException(p + ": only a PAK mod's .pak can be added, as the next patch PAK; the game's own PAKs are never written.");
            if (root && leaf.EndsWith(".exe")) throw new IOException(p + ": programs (.exe) are never installed into the game folder.");
            if (root && leaf.StartsWith("steam_api")) throw new IOException(p + ": Steam files are never replaced.");
            if (low.StartsWith("dlc/")) throw new IOException(p + ": DLC files are never overwritten.");
            if (leaf == LooseLoader.ConfigName.ToLowerInvariant() && root) throw new IOException(p + ": mods can't replace REFramework's config.");
            if (low.StartsWith("reframework/costume_mods/") && low.Substring("reframework/costume_mods/".Length).Contains("/")) throw new IOException(p + ": use Extra outfit mode for extra outfit folders.");
            if (low.Contains("/.slotsloader-installer") || low.StartsWith(".slotsloader-installer")) throw new IOException(p + ": Manager storage is not a game file.");
            if (leaf == CrashReporter.RenamedName.ToLowerInvariant()) throw new IOException(p + ": the crash reporter backup is not replaced.");
        }

        // ---- Status
        internal GameFilesStatus Status()
        {
            GameFilesStatus status = GameStatus();
            // 1.0.11: leftover patch PAKs (ReplaceLeftoverPaks.cs), last among the details (the summary quotes the first).
            try { status.LeftoverPaks = LeftoverPaks().Select(p => p.Name).ToList(); }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); }
            if (status.LeftoverPaks.Count > 0) status.Details.Add(LeftoverStatusText(status.LeftoverPaks));
            return status;
        }
        private GameFilesStatus GameStatus()
        {
            var status = new GameFilesStatus { JournalPending = JournalPending };
            try { status.OrphanCostumePaks = LoaderCostumePak.Orphaned(GameRoot).Select(p => p.Name).ToList(); }
            catch (Exception ex) { Program.Log(ex); }
            try { status.OrphanedRows = OrphanedRows(out status.OrphanedRowsWhere); }   // 2.4.2 (PakRepair.cs)
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); }
            ReplaceState state;
            try { RelocationReport relocation; state = LoadState(out relocation); status.Relocation = relocation; } catch (Exception ex) { status.Details.Add(ex.Message); return status; }
            if (state.Mods.Count == 0 && state.Deployed.Count == 0 && state.Released.Count == 0) return status;
            try
            {
                foreach (PakStamp stamp in state.Paks)
                {
                    string full = GamePath(stamp.Pak);
                    if (!File.Exists(full)) { status.PakChanged = true; status.Details.Add(stamp.Pak + " is missing"); continue; }
                    PakIndex p = GamePaks.Cached(full);
                    if (p.Length != stamp.Length || p.Fingerprint != stamp.Fingerprint || p.LayoutSha256() != stamp.LayoutSha) { status.PakChanged = true; status.Details.Add(stamp.Pak + " was updated"); continue; }
                    foreach (ZeroedRow z in state.Deployed.SelectMany(d => d.Zeroed).Where(z => String.Equals(z.Pak, stamp.Pak, StringComparison.OrdinalIgnoreCase)))
                        if (z.Index >= p.Count || !p.Entry(z.Index).Zeroed) status.RowsNotZero++;
                }
                if (status.RowsNotZero > 0) status.Details.Add(status.RowsNotZero + " invalidated PAK entries restored by another tool or a Steam file check");
                FluffyInstalls fluffy = null;
                foreach (DeployedFile d in state.Deployed)
                {
                    string full = GamePath(d.Path);
                    if (!File.Exists(full)) { status.FilesMissing++; continue; }
                    long length = new FileInfo(full).Length;
                    // Game-folder tool files (not natives) are small: compared by content, so a same-size build counts too.
                    bool changed = length != d.Size || (!d.Path.StartsWith("natives/", StringComparison.OrdinalIgnoreCase) && length < 64L * 1024 * 1024 && Sha(full) != d.Sha256);
                    if (!changed) continue;
                    status.FilesChanged++;
                    if (status.ChangedBy.Count < 6)
                    {
                        if (fluffy == null) fluffy = Fluffy();
                        string sha = length < 64L * 1024 * 1024 ? Sha(full) : null;
                        status.ChangedBy.Add(d.Path + " (by " + KnownBuilds.Changer(d.Path, sha, fluffy) + ")");
                    }
                }
                if (status.FilesMissing > 0) status.Details.Add(status.FilesMissing + " installed files are missing");
                CountBackups(state, status);
                if (status.FilesChanged > 0) status.Details.Add(status.FilesChanged + " installed files changed" + (status.ChangedBy.Count > 0 ? ": " + String.Join(", ", status.ChangedBy) : ""));
                status.Released = state.Released.Count; status.ForeignCopies = state.ForeignCopies.Count;
                if (status.Released > 0) status.Details.Add(status.Released + " game file" + (status.Released == 1 ? " is" : "s are") + " kept from another tool (" + String.Join(", ", state.Released.Take(3).Select(r => r.Path + " by " + r.Changer)) + "); original" + (status.Released == 1 ? "" : "s") + " kept in the Manager's Data folder");
                string basePath = Path.Combine(GameRoot, "re_chunk_000.pak");
                if (state.BaseAtInstall != null && File.Exists(basePath))
                {
                    PakIndex b = GamePaks.Cached(basePath);
                    status.BaseChanged = b.Length != state.BaseAtInstall.Length || b.Fingerprint != state.BaseAtInstall.Fingerprint || b.LayoutSha256() != state.BaseAtInstall.LayoutSha;
                }
                // 2.4.1: warn only when an installed, enabled mod actually depends on the loose file loader.
                List<ReplaceMod> needLoader = NeedLooseLoader(state, CurrentMethod(state));
                if (needLoader.Count > 0)
                {
                    string detail; status.LooseLoaderOff = LooseLoader.State(GameRoot, out detail) != LooseLoaderState.Enabled;
                    if (status.LooseLoaderOff) status.Details.Add("Needs the loose file loader: " + String.Join(", ", needLoader.Take(3).Select(m => "'" + m.Name + "'")) + (needLoader.Count > 3 ? " and " + (needLoader.Count - 3) + " more" : ""));
                }
            }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; status.Details.Add("Status check failed: " + ex.Message); }
            return status;
        }

        // 2.4.1: the installed, enabled mods whose files only reach the game through REFramework's loose file loader:
        // files under natives/ installed with the loose method (the PAK method invalidates the PAK entries instead).
        // SlotsLoader itself (amd_ags proxy, reframework plugin and data) never needs it, and a disabled mod is not
        // in the game folder, so neither triggers the warning on its own.
        internal static List<ReplaceMod> NeedLooseLoader(ReplaceState state, string method)
        {
            if (state == null || method != ReplaceMethods.Loose) return new List<ReplaceMod>();
            return state.Mods.Where(m => m.Enabled && !LoaderCostumePak.IsLoaderMod(m) && m.Files.Any(f => (f.Path ?? "").Replace('\\', '/').StartsWith("natives/", StringComparison.OrdinalIgnoreCase))).ToList();
        }

        // Backups this app keeps in Data (originals it overwrote, PAK TOC copies) and the stored
        // replacement copies, checked on disk. A missing original backup means that original cannot
        // be put back by an uninstall; a missing stored copy cannot be reapplied.
        internal static void CountBackups(ReplaceState state, GameFilesStatus status, Func<ReplaceMod, ModFile, string> storeFile)
        {
            status.BackupsRecorded = 0; status.BackupsMissing = 0; status.StoredMissing = 0; status.FirstMissingBackup = null; status.FirstMissingStored = null;
            Action<string> backup = delegate(string path)
            {
                status.BackupsRecorded++;
                bool present; try { present = !String.IsNullOrEmpty(path) && File.Exists(path); } catch { present = false; }
                if (!present) { status.BackupsMissing++; if (status.FirstMissingBackup == null) status.FirstMissingBackup = String.IsNullOrEmpty(path) ? "(no path recorded)" : path; }
            };
            foreach (OriginalBackup o in state.Originals) backup(o.Backup);
            foreach (ReleasedFile r in state.Released) if (r.Original != null) backup(r.Original.Backup);
            foreach (PakStamp p in state.Paks.Where(p => !String.IsNullOrEmpty(p.Backup))) backup(p.Backup);
            foreach (ReplaceMod m in state.Mods)
                foreach (ModFile f in m.Files)
                {
                    string stored = null; bool present;
                    try { stored = storeFile(m, f); present = File.Exists(stored); } catch { present = false; }
                    if (!present) { status.StoredMissing++; if (status.FirstMissingStored == null) status.FirstMissingStored = stored ?? (m.Id + "/" + f.Path); }
                }
            if (status.BackupsMissing > 0) status.Details.Add(status.BackupsMissing + " of " + ReplacePlanner.N(status.BackupsRecorded, "backup") + " missing on disk (first: " + status.FirstMissingBackup + ")");
            if (status.StoredMissing > 0) status.Details.Add(status.StoredMissing + " stored replacement copies missing (first: " + status.FirstMissingStored + ")");
        }
        private void CountBackups(ReplaceState state, GameFilesStatus status) { CountBackups(state, status, delegate(ReplaceMod m, ModFile f) { return StoreFile(state, m, f); }); }

        // Last resort when the record is lost: rows that are zero now but hold a hash in the
        // TOC backup are restored, only when the PAK still has the backup's identity, every
        // other field matches, and the path is not one Fluffy says it installed.
        internal int RestoreFromTocBackup(string pakRelative)
        {
            using (Lock())
            {
                GameProcesses.RequireIdle("restore PAK entries");
                string full = GamePath(pakRelative);
                PakIndex p = PakIndex.Open(full);
                string layout = p.LayoutSha256();
                string meta = Directory.Exists(PakBackupDir) ? Directory.GetFiles(PakBackupDir, Path.GetFileName(full) + "-" + p.Length + "-" + p.Fingerprint.ToString("X8") + "-" + layout.Substring(0, 12) + ".json").FirstOrDefault() : null;
                if (meta == null) throw new IOException("No TOC backup matches the current " + pakRelative + " (size, fingerprint and layout).");
                PakStamp stamp = Json.Read<PakStamp>(meta);
                byte[] toc = File.ReadAllBytes(Path.ChangeExtension(meta, ".toc"));
                if (stamp.LayoutSha != layout || toc.Length != PakIndex.HeaderSize + p.Table.Length) throw new IOException("The TOC backup does not match the PAK layout.");
                var fluffyHashes = new HashSet<ulong>(Fluffy().OwnedPaths.Where(x => x.StartsWith("natives/", StringComparison.OrdinalIgnoreCase)).Select(PakHash.PathHash));
                var writes = new List<PakHashWrite>();
                for (int i = 0; i < p.Count; i++)
                {
                    int o = PakIndex.HeaderSize + i * PakIndex.EntrySize;
                    ulong backupHash = BitConverter.ToUInt64(toc, o);
                    if (backupHash == 0 || !p.Entry(i).Zeroed || fluffyHashes.Contains(backupHash)) continue;
                    writes.Add(new PakHashWrite { Index = i, Expected = new byte[8], Value = toc.Skip(o).Take(8).ToArray() });
                }
                PakWriter.WriteHashes(full, p.Length, p.Header, writes);
                GamePaks.Forget(full);
                return writes.Count;
            }
        }
    }
}
