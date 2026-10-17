using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace SlotsInstaller
{
    // 2.4.2 follow-ups of the 2.0.1 code review: shared state reads, recoverable copies whose move into
    // <store>\.removed failed, pruning of .removed, earlier store roots and where Undo puts a mod back.
    internal sealed partial class ReplaceEngine
    {
        // ---- Shared reads (replace-state.json, the journal)
        // A commit replaces replace-state.json with File.Replace. A reader that holds the file without Delete sharing
        // (File.ReadAllText) makes that replace fail, so a background check reading at the same instant could roll a
        // user's change back. Reads share ReadWrite | Delete (the replace then succeeds and the reader sees the old,
        // complete file) and retry briefly when they meet another program's handle or the instant of the replace.
        internal const int SharedReadRetries = 8;
        internal static int SharedReadRetried;   // tests: retries taken (any reader)
        internal static string ReadShared(string path)
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan))
                    using (var reader = new StreamReader(stream, Encoding.UTF8, true)) return reader.ReadToEnd();
                }
                // File.Replace leaves the target missing for an instant after the temporary file is renamed (seen in the full
                // run: no .tmp left, no target yet), so a missing file is looked at a few times even without a .tmp beside it.
                catch (FileNotFoundException) { if (attempt >= SharedReadRetries || (attempt >= MissingRetries && !ReplacePending(path))) throw; }
                catch (DirectoryNotFoundException) { throw; }
                catch (IOException) { if (attempt >= SharedReadRetries) throw; }
                catch (UnauthorizedAccessException) { if (attempt >= SharedReadRetries) throw; }   // a file in the instant of deletion
                Interlocked.Increment(ref SharedReadRetried);
                Thread.Sleep(10 + attempt * 10);
            }
        }
        internal static T ReadJsonShared<T>(string path) { return Json.Serializer().Deserialize<T>(ReadShared(path)); }
        // A temporary file beside the target: a DurableFile write is about to replace it.
        private static bool ReplacePending(string path)
        {
            try { string dir = Path.GetDirectoryName(path); return Directory.Exists(dir) && Directory.GetFiles(dir, Path.GetFileName(path) + ".*.tmp").Length > 0; }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; return false; }
        }
        // replace-state.json may be missing for an instant while another process replaces it: look again before
        // reading "no state".
        // Quick second looks at a missing state file (about 30 ms in all when it really is missing).
        private const int MissingRetries = 3;
        private bool StateFileExists()
        {
            if (File.Exists(StatePath)) return true;
            for (int i = 0; i < SharedReadRetries && (i < MissingRetries || ReplacePending(StatePath)); i++) { Thread.Sleep(5 + i * 5); if (File.Exists(StatePath)) return true; }
            return false;
        }

        // ---- Recoverable copies whose move into .removed failed after the commit (2.4.2)
        // The change itself committed, so it is not rolled back. The move is remembered in
        // Data\replace-trash-pending.json and retried at the next start and before every change; the result message
        // says the copy wasn't saved, so the UI never claims it was kept.
        internal string TrashPendingPath { get { return Path.Combine(DataDir, "replace-trash-pending.json"); } }
        internal const string TrashFailedStart = "The copy for Undo and Recovery wasn't saved";
        internal static string TrashFailedText(string reason)
        {
            return TrashFailedStart + " (" + (reason ?? "").Trim().TrimEnd('.') + "). The Manager tries again at the next start.";
        }
        // A folder that can't be moved almost always has a file open in another program (Explorer, a scanner).
        internal static string TrashReason(Exception ex)
        {
            string m = ex.Message ?? "";
            if ((ex is IOException || ex is UnauthorizedAccessException) && (m.Contains("denied") || m.Contains("being used"))) return "a file in it is open in another program";
            return m;
        }
        private string trashWarning;

        private void MoveToRemoved(TrashMove move)
        {
            if (!Directory.Exists(move.From)) return;
            Directory.CreateDirectory(move.To);
            DurableFile.Write(Path.Combine(move.To, "record.json"), Encoding.UTF8.GetBytes(move.RecordJson));
            string files = Path.Combine(move.To, "files");
            if (!Directory.Exists(files)) Directory.Move(move.From, files);
        }

        private void RememberTrash(TrashMove move)
        {
            try
            {
                TrashPending pending = ReadTrashPending() ?? new TrashPending();
                if (!pending.Moves.Any(m => String.Equals(m.To, move.To, StringComparison.OrdinalIgnoreCase))) pending.Moves.Add(move);
                Directory.CreateDirectory(DataDir);
                DurableFile.WriteJson(TrashPendingPath, pending);
            }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); }
        }
        private TrashPending ReadTrashPending()
        {
            if (!File.Exists(TrashPendingPath)) return null;
            TrashPending p = ReadJsonShared<TrashPending>(TrashPendingPath);
            if (p == null || p.Format != "sf6-replace-trash-pending-v1") throw new IOException("Unrecognized " + TrashPendingPath);
            if (p.Moves == null) p.Moves = new List<TrashMove>();
            var quiet = new RelocationReport(DataDir);
            foreach (TrashMove m in p.Moves) { m.From = DataRelocation.RebaseTarget(m.From, DataDir, quiet, GameRoot); m.To = DataRelocation.RebaseTarget(m.To, DataDir, quiet, GameRoot); }
            return p;
        }
        internal bool TrashRetryPending { get { return File.Exists(TrashPendingPath); } }

        // Retries the remembered moves. Returns null when nothing was pending, else a message for the status line.
        // Another Manager task changing the game folder: tried again later (null).
        internal string RetryTrash()
        {
            if (!TrashRetryPending) return null;
            try { using (Lock()) return RetryTrashLocked(); }
            catch (IOException ex) { if (ex.Message.StartsWith("Another Manager task", StringComparison.Ordinal)) return null; throw; }
        }
        private string RetryTrashLocked()
        {
            TrashPending pending;
            try { pending = ReadTrashPending(); }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); return null; }
            if (pending == null) return null;
            ReplaceState state = LoadState();
            var left = new List<TrashMove>(); int moved = 0; string firstError = null;
            foreach (TrashMove move in pending.Moves)
            {
                ReplaceRemovedRecord record = null;
                try { record = Json.Serializer().Deserialize<ReplaceRemovedRecord>(move.RecordJson); } catch (Exception ex) { if (ex is OutOfMemoryException) throw; }
                // Nothing left to move, or the folder belongs to an installed replacement again: dropped.
                if (!Directory.Exists(move.From) || record == null || record.Mod == null || state.Find(record.Mod.Id) != null) continue;
                try { MoveToRemoved(move); moved++; }
                catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); left.Add(move); if (firstError == null) firstError = TrashReason(ex); }
            }
            if (left.Count == 0) File.Delete(TrashPendingPath);
            else DurableFile.WriteJson(TrashPendingPath, new TrashPending { Moves = left });
            if (moved > 0) DataRelocation.WriteLog(DataDir, "Recovery: " + ReplacePlanner.N(moved, "removed replacement") + " moved to " + RemovedFolderName + " on retry.");
            if (left.Count > 0) return TrashFailedText(firstError);
            return moved > 0 ? "Kept " + ReplacePlanner.N(moved, "removed replacement") + " for Recovery." : null;
        }

        // ---- Store roots: the current one, the recorded one and every earlier one (2.4.2)
        // Settings can move the store while nothing is installed; the next change records the old root in
        // ReplaceState.PreviousStoreRoots, so its .removed copies stay listed (Recovery, Undo, Last used).
        internal List<string> RemovedRoots()
        {
            var roots = new List<string> { Settings.StoreRoot(DataDir) };
            try
            {
                ReplaceState s = LoadState();
                if (!String.IsNullOrEmpty(s.StoreRoot)) roots.Add(s.StoreRoot);
                roots.AddRange(s.PreviousStoreRoots.Where(r => !String.IsNullOrWhiteSpace(r)));
            }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; }
            return DistinctRoots(roots);
        }
        // The same without a game folder check (the library's Last used sort reads only the removed records).
        internal static List<string> KnownStoreRoots(string dataDir, ReplaceSettings settings)
        {
            var roots = new List<string> { settings.StoreRoot(dataDir) };
            string state = Path.Combine(dataDir, "replace-state.json");
            try
            {
                ReplaceState s = File.Exists(state) ? ReadJsonShared<ReplaceState>(state) : null;
                if (s != null)
                {
                    if (!String.IsNullOrEmpty(s.StoreRoot)) roots.Add(DataRelocation.Rebase(s.StoreRoot, dataDir, null));
                    foreach (string r in s.PreviousStoreRoots ?? new List<string>()) if (!String.IsNullOrWhiteSpace(r)) roots.Add(DataRelocation.Rebase(r, dataDir, null));
                }
            }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); }
            return DistinctRoots(roots);
        }
        private static List<string> DistinctRoots(IEnumerable<string> roots)
        {
            var result = new List<string>(); var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string r in roots)
            {
                string full;
                try { full = Path.GetFullPath(r).TrimEnd(Path.DirectorySeparatorChar); } catch (Exception ex) { if (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException) continue; throw; }
                if (seen.Add(full)) result.Add(full);
            }
            return result;
        }
        // Reconcile: a store that changed since the last change is remembered.
        private static void RememberStoreRoot(ReplaceState current, ReplaceState next)
        {
            if (next.PreviousStoreRoots == null) next.PreviousStoreRoots = new List<string>();
            string old = current.StoreRoot;
            if (String.IsNullOrWhiteSpace(old) || String.Equals(DataRelocation.Normalize(old), DataRelocation.Normalize(next.StoreRoot ?? ""), StringComparison.OrdinalIgnoreCase)) return;
            if (!next.PreviousStoreRoots.Any(r => String.Equals(DataRelocation.Normalize(r), DataRelocation.Normalize(old), StringComparison.OrdinalIgnoreCase))) next.PreviousStoreRoots.Add(old);
        }

        // ---- Pruning .removed (2.4.2)
        // Copies older than RemovedKeepDays are deleted; then, while all copies together exceed RemovedMaxBytes, the
        // oldest. Never: the newest copy of each mod (same source, else same record), a copy younger than
        // RemovedMinAge (so another window's or the CLI's Undo of today is safe), or a copy the caller still offers
        // Undo for. The window prunes at start-up and after each change (LoadGameRows).
        internal const int RemovedKeepDays = 30;
        internal const long RemovedMaxBytes = 5L * 1024 * 1024 * 1024;
        internal static readonly TimeSpan RemovedMinAge = TimeSpan.FromDays(1);
        internal sealed class PruneResult { internal int Deleted; internal long Bytes; internal List<string> Folders = new List<string>(); }

        internal static DateTime RemovedTime(RemovedReplacement r)
        {
            DateTime t = LibraryUsage.ParseUtc(r.Record.RemovedUtc);
            if (t > DateTime.MinValue) return t;
            Match m = Regex.Match(Path.GetFileName(r.Folder.TrimEnd(Path.DirectorySeparatorChar)), @"-([0-9]{14})-[0-9a-f]{6}$", RegexOptions.IgnoreCase);
            if (m.Success && DateTime.TryParseExact(m.Groups[1].Value, "yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out t)) return t;
            try { return Directory.GetCreationTimeUtc(r.Folder); } catch (Exception ex) { if (ex is OutOfMemoryException) throw; return DateTime.UtcNow; }
        }
        private static string ModKey(RemovedReplacement r)
        {
            string source = r.Record.Mod.SourcePath;
            if (!String.IsNullOrWhiteSpace(source)) { try { return "source|" + Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant(); } catch (Exception ex) { if (ex is OutOfMemoryException) throw; } }
            return "id|" + (r.Record.Mod.Id ?? "").ToUpperInvariant();
        }
        private static string FullFolder(string folder) { try { return Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar); } catch (Exception ex) { if (ex is OutOfMemoryException) throw; return folder ?? ""; } }

        // Which copies the rules delete (pure: no disk access beyond the folder times already read).
        internal static List<RemovedReplacement> PruneCandidates(List<RemovedReplacement> copies, IEnumerable<string> keep, DateTime nowUtc)
        {
            var keepSet = new HashSet<string>((keep ?? new string[0]).Where(k => !String.IsNullOrEmpty(k)).Select(FullFolder), StringComparer.OrdinalIgnoreCase);
            var times = copies.ToDictionary(c => c, RemovedTime);
            var newest = new HashSet<RemovedReplacement>(copies.GroupBy(ModKey).Select(g => g.OrderByDescending(c => times[c]).ThenByDescending(c => c.Folder, StringComparer.OrdinalIgnoreCase).First()));
            var eligible = copies.Where(c => !newest.Contains(c) && !keepSet.Contains(FullFolder(c.Folder)) && nowUtc - times[c] >= RemovedMinAge).OrderBy(c => times[c]).ToList();
            var delete = eligible.Where(c => nowUtc - times[c] > TimeSpan.FromDays(RemovedKeepDays)).ToList();
            long total = copies.Sum(c => c.Bytes) - delete.Sum(c => c.Bytes);
            foreach (RemovedReplacement c in eligible)
            {
                if (total <= RemovedMaxBytes) break;
                if (delete.Contains(c)) continue;
                delete.Add(c); total -= c.Bytes;
            }
            return delete;
        }

        internal PruneResult PruneRemoved(IEnumerable<string> keep) { return PruneRemoved(keep, DateTime.UtcNow); }
        internal PruneResult PruneRemoved(IEnumerable<string> keep, DateTime nowUtc)
        {
            var result = new PruneResult();
            IDisposable held;
            try { held = Lock(); }
            catch (IOException) { return result; }   // a change is running: pruned next time
            using (held)
            {
                foreach (RemovedReplacement c in PruneCandidates(ListRemoved(), keep, nowUtc))
                {
                    try { DeleteRemoved(c.Folder); result.Deleted++; result.Bytes += c.Bytes; result.Folders.Add(c.Folder); }
                    catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); }
                }
            }
            if (result.Deleted > 0) DataRelocation.WriteLog(DataDir, "Recovery: deleted " + ReplacePlanner.N(result.Deleted, "removed replacement copy") + " (" + Utilities.FriendlyBytes(result.Bytes) + "): older than " + RemovedKeepDays + " days or above " + Utilities.FriendlyBytes(RemovedMaxBytes) + "; each mod's newest copy kept.");
            return result;
        }

        // ---- Where a removed replacement goes back (2.4.2)
        // Below every replacement installed or updated after it was removed (a newer install keeps the files it won),
        // otherwise among the mods it was removed from at its old place: after as many of them as were above it.
        internal static int RestoreIndex(IList<ReplaceMod> mods, ReplaceRemovedRecord record)
        {
            int wanted = Math.Max(0, record.Index);
            DateTime removed = LibraryUsage.ParseUtc(record.RemovedUtc);
            if (removed == DateTime.MinValue) return Math.Min(wanted, mods.Count);   // a record without a time: its old index
            Func<ReplaceMod, bool> newer = delegate(ReplaceMod m) { return LibraryUsage.ParseUtc(m.InstalledUtc) > removed; };
            int at = mods.Count, older = 0, lastNewer = -1;
            for (int i = 0; i < mods.Count; i++) if (newer(mods[i])) lastNewer = i;
            for (int i = 0; i <= mods.Count; i++)
            {
                if (older >= wanted) { at = i; break; }
                if (i < mods.Count && !newer(mods[i])) older++;
            }
            return Math.Max(at, lastNewer + 1);
        }
    }

    // Data\replace-trash-pending.json (2.4.2): recoverable copies whose move into .removed is retried.
    public sealed class TrashPending
    {
        public string Format = "sf6-replace-trash-pending-v1";
        public List<TrashMove> Moves = new List<TrashMove>();
    }
}
