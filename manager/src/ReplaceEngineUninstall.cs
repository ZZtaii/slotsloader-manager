using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SlotsInstaller
{
    // 1.0.1 (the user: "uninstall should mean uninstall"): a replacement or tool mod leaves the game folder and the
    // store with no copy kept, because its source in the library is the way back. Only a mod whose source is gone
    // keeps its files: its removed copy goes to the Recycle Bin, or is deleted when the Recycle Bin can't take it
    // (no copy outlives its Undo; the result says so).
    internal sealed partial class ReplaceEngine
    {
        // 1.0.1: every .removed copy except `keep` (the live Undo's) is deleted: since uninstall keeps nothing and
        // Recover removed outfits is gone, only Restore original game files' Undo can use one. Skipped while a change
        // holds the game lock (next refresh).
        internal int DeleteRemovedExcept(IEnumerable<string> keep)
        {
            var kept = new HashSet<string>((keep ?? new string[0]).Select(k => Path.GetFullPath(k).TrimEnd('\\')), StringComparer.OrdinalIgnoreCase);
            IDisposable held;
            try { held = Lock(); } catch (IOException) { return 0; }
            int deleted = 0; long bytes = 0;
            using (held)
                foreach (RemovedReplacement c in ListRemoved())
                {
                    if (kept.Contains(Path.GetFullPath(c.Folder).TrimEnd('\\'))) continue;
                    try { DeleteRemoved(c.Folder); deleted++; bytes += c.Bytes; } catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); }
                }
            if (deleted > 0) DataRelocation.WriteLog(DataDir, "Deleted " + ReplacePlanner.N(deleted, "removed replacement copy") + " (" + Utilities.FriendlyBytes(bytes) + "): no Undo needs them.");
            return deleted;
        }
        // 1.0.1 (the user: keep the game's own small originals, nothing else): another tool's file that "Restore
        // original" (or Reapply) replaced is copied only so the change can roll back; once it committed the copy goes.
        // The game originals behind "Keep current file" (Released) stay: Restore original game files puts them back.
        internal int PurgeForeignCopies()
        {
            ReplaceState s = LoadState();
            if (s.ForeignCopies == null || s.ForeignCopies.Count == 0) return 0;
            int n = 0;
            foreach (ForeignCopy c in s.ForeignCopies)
            {
                try
                {
                    string copy = Path.GetFullPath(c.Copy);
                    if (copy.StartsWith(Path.GetFullPath(BackupDir).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) && File.Exists(copy)) { File.Delete(copy); n++; }
                }
                catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); }
            }
            s.ForeignCopies.Clear(); s.Generation++; DurableFile.WriteJson(StatePath, s);
            return n;
        }
        internal static bool SourceExists(string source)
        {
            if (String.IsNullOrWhiteSpace(source)) return false;
            try { return File.Exists(source) || Directory.Exists(source); }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; return false; }
        }
        internal string UninstallForGood(string id, DriftChoice choice, IRecycleBin bin)
        {
            ReplaceMod mod = LoadState().Mods.FirstOrDefault(m => m.Id == id);
            bool keep = mod != null && !SourceExists(mod.SourcePath);
            string folder;
            string message = Uninstall(id, keep, out folder, choice) ?? "";
            if (folder == null || !Directory.Exists(folder)) return message;
            string name = mod.Name;
            try
            {
                long bytes = new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
                string why = (bin ?? RecycleBin.Current).Unavailable(folder, true, bytes);
                if (why == null) { (bin ?? RecycleBin.Current).Recycle(folder, true); if (!Directory.Exists(folder)) return (message + " Its source is gone, so its files went to the Recycle Bin.").Trim(); }
                return (message + " Its source is gone and the Recycle Bin can't take its files (" + (why ?? "Windows left them in place") + "), so they were deleted.").Trim();
            }
            catch (Exception ex)
            {
                if (ex is OutOfMemoryException) throw;
                Program.Log(ex);
                return (message + " Its source is gone and its files couldn't go to the Recycle Bin, so they were deleted.").Trim();
            }
        }
    }
}
