using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SlotsInstaller
{
    // Data\leftover-paks\<name>-<time>-<rand>\record.json + the PAK itself: a leftover patch PAK removed on the user's click.
    public sealed class LeftoverPakRecord
    {
        public string Format = "sf6-leftover-pak-v1";
        public string GameRoot = "", Name = "", RemovedUtc = "";
        public int Number;
        public long Length;
        public bool Placeholder;   // an empty placeholder took its number (a higher patch PAK exists)
    }

    // 1.0.11 (the user: an external mod manager with nothing installed left its patch PAKs, so the "uninstalled" mod still
    // showed in game): a leftover is a patch PAK nothing claims. Not Slotsloader's (costume or stage pak), readable, not the
    // empty 16-byte placeholder, no file of a record in this Manager's state (enabled or not) and listed by no installed.ini
    // of the external mod manager. A PAK dropped in by hand looks the same, so nothing is automatic: the Installed page
    // lists each one and removes it only on a click. Removal keeps the PAK in Data\leftover-paks for Undo (it goes to the
    // Recycle Bin when the Undo ends) and leaves the empty placeholder where a higher patch PAK exists (the game stops at
    // the first missing number); other PAKs are never renumbered.
    internal sealed partial class ReplaceEngine
    {
        internal const string LeftoverRemoveAction = "remove a leftover PAK", LeftoverRestoreAction = "put the leftover PAK back";
        internal const string LeftoverTitle = "Leftover PAK", LeftoverReason = "not listed by any mod manager";
        internal string LeftoverCopyDir { get { return Path.Combine(DataDir, "leftover-paks"); } }

        internal List<PatchPak> LeftoverPaks()
        {
            if (JournalPending || !Directory.Exists(GameRoot)) return new List<PatchPak>();   // a half-done change may own a PAK
            return LeftoverPaks(LoaderCostumePak.Scan(GameRoot), LoadState(), Fluffy());
        }
        internal static List<PatchPak> LeftoverPaks(IEnumerable<PatchPak> paks, ReplaceState state, FluffyInstalls fluffy)
        {
            // An unreadable installed.ini, or no external mod manager found (it is found through its Mods folder in the mod
            // libraries; PR #12 review): who owns what is unknown, so nothing counts as leftover.
            if (fluffy == null || fluffy.Roots.Count == 0 || fluffy.Warnings.Count > 0) return new List<PatchPak>();
            var owned = new HashSet<string>((state == null ? new List<ReplaceMod>() : state.Mods).SelectMany(m => m.Files).Select(f => FluffyInstalls.Key(f.Path)), StringComparer.OrdinalIgnoreCase);
            return (paks ?? new PatchPak[0]).Where(p => p.Kind == PatchPakKind.Mod && !owned.Contains(p.Name) && fluffy.OwnersOf(p.Name).Count == 0 && !IsFluffyDummy(p.Path)).ToList();
        }
        internal static string LeftoverDetail(PatchPak p) { return LeftoverReason + " · " + Utilities.FriendlyBytes(p.Length); }
        internal static string LeftoverStatusText(List<string> names) { return ReplacePlanner.N(names.Count, "leftover PAK") + " " + LeftoverReason + " (SF6 still loads " + (names.Count == 1 ? "it" : "them") + "): " + String.Join(", ", names.Take(6)) + (names.Count > 6 ? ", …" : ""); }

        // Moves the PAK into Data\leftover-paks (`copyFolder`, for Undo). Refused while SF6 or the external mod manager runs.
        internal string RemoveLeftoverPak(string name, out string copyFolder)
        {
            copyFolder = null;
            GameProcesses.RequireIdle(LeftoverRemoveAction);
            using (Lock())
            {
                PatchPak pak = LeftoverPaks().FirstOrDefault(p => String.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
                if (pak == null) throw new IOException((name ?? "That PAK") + " is no longer a leftover PAK (a mod manager lists it now, or it is gone). Nothing was changed.");
                Safety.NoReparseChain(pak.Path);
                bool placeholder = HigherPatchPakExists(pak.Name);
                string folder = Path.Combine(LeftoverCopyDir, pak.Name + "-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6));
                Directory.CreateDirectory(folder);
                DurableFile.WriteJson(Path.Combine(folder, "record.json"), new LeftoverPakRecord { GameRoot = GameRoot, Name = pak.Name, Number = pak.Number, Length = pak.Length, Placeholder = placeholder, RemovedUtc = DateTime.UtcNow.ToString("o") });
                try { File.Move(pak.Path, Path.Combine(folder, pak.Name)); }
                catch { try { Directory.Delete(folder, true); } catch { } throw; }
                // A crash before the placeholder leaves a gap that FillPatchGaps fills at the next start.
                if (placeholder) { string tmp = pak.Path + ".slotsinstaller-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp"; File.WriteAllBytes(tmp, FluffyDummyPak); File.Move(tmp, pak.Path); }
                copyFolder = folder;
                Program.LogLine("Leftover PAK removed: " + pak.Name + " (" + Utilities.FriendlyBytes(pak.Length) + ")" + (placeholder ? ", empty placeholder left in its place" : "") + "; copy kept for Undo in " + folder);
                return placeholder ? "An empty placeholder keeps its number, so the PAKs after it still load." : "";
            }
        }

        // Undo: the PAK goes back only where its number is still the empty placeholder or free.
        internal string RestoreLeftoverPak(string folder)
        {
            GameProcesses.RequireIdle(LeftoverRestoreAction);
            using (Lock())
            {
                string full = Path.GetFullPath(folder ?? "");
                LeftoverPakRecord r = ReadLeftoverCopy(full);
                string copy = r == null ? null : Path.Combine(full, r.Name);
                if (r == null || !File.Exists(copy) || new FileInfo(copy).Length != r.Length) throw new IOException("The removed copy of the leftover PAK is missing or incomplete. Nothing was changed.");
                if (!String.Equals(Path.GetFullPath(r.GameRoot ?? "").TrimEnd(Path.DirectorySeparatorChar), GameRoot, StringComparison.OrdinalIgnoreCase)) throw new IOException(r.Name + " was removed from another game folder (" + r.GameRoot + "). Nothing was changed.");
                string target = GamePath(r.Name);
                Safety.NoReparseChain(target);
                if (File.Exists(target) && !IsFluffyDummy(target)) throw new IOException(r.Name + " is now another PAK, so the leftover PAK can't go back in its place. Nothing was changed; the removed copy goes to the Recycle Bin.");
                string tmp = target + ".slotsinstaller-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
                File.Move(copy, tmp);
                try { if (File.Exists(target)) File.Replace(tmp, target, null); else File.Move(tmp, target); }
                catch { try { if (File.Exists(tmp) && !File.Exists(copy)) File.Move(tmp, copy); } catch { } throw; }
                List<int> gaps = FillPatchGaps();   // a lower PAK another tool removed meanwhile
                try { Directory.Delete(full, true); } catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); }
                Program.LogLine("Leftover PAK put back: " + r.Name);
                return (r.Name + " is back in the game folder. " + GapText(gaps)).Trim();
            }
        }

        private LeftoverPakRecord ReadLeftoverCopy(string folder)
        {
            if (!IsUnder(folder, LeftoverCopyDir)) throw new IOException("Not a removed leftover PAK: " + folder);
            Safety.NoReparseChain(folder);
            string file = Path.Combine(folder, "record.json");
            LeftoverPakRecord r = File.Exists(file) ? Json.Read<LeftoverPakRecord>(file) : null;
            return r != null && r.Format == "sf6-leftover-pak-v1" && IsPatchPakPath(r.Name) && Path.GetFileName(r.Name) == r.Name ? r : null;
        }
        internal bool IsLeftoverCopy(string folder) { try { return IsUnder(Path.GetFullPath(folder), LeftoverCopyDir); } catch (Exception ex) { if (ex is OutOfMemoryException) throw; return false; } }

        // The Undo ended unused (or a crash left the copy): the PAK goes to the Recycle Bin, deleted when it can't take it.
        internal void DiscardLeftoverCopy(string folder, IRecycleBin bin)
        {
            string full = Path.GetFullPath(folder);
            if (!IsUnder(full, LeftoverCopyDir) || !Directory.Exists(full)) return;
            Safety.NoReparseChain(full);
            bin = bin ?? RecycleBin.Current;
            foreach (string pak in Directory.GetFiles(full, "*.pak"))
            {
                string why = bin.Unavailable(pak, false, new FileInfo(pak).Length);
                if (why == null) bin.Recycle(pak, false);
                if (File.Exists(pak)) { File.Delete(pak); Program.LogLine("Removed leftover PAK " + Path.GetFileName(pak) + " deleted: the Recycle Bin can't take it (" + (why ?? "Windows left it in place") + ")."); }
                else Program.LogLine("Removed leftover PAK " + Path.GetFileName(pak) + " moved to the Recycle Bin (its Undo ended).");
            }
            Directory.Delete(full, true);
        }
        // Start-up and every refresh: every copy except the live Undo's. Skipped while a change holds the game lock.
        internal int DiscardLeftoverCopiesExcept(IEnumerable<string> keep, IRecycleBin bin)
        {
            if (!Directory.Exists(LeftoverCopyDir)) return 0;
            var kept = new HashSet<string>((keep ?? new string[0]).Select(k => Path.GetFullPath(k).TrimEnd('\\')), StringComparer.OrdinalIgnoreCase);
            IDisposable held;
            try { held = Lock(); } catch (IOException) { return 0; }
            int n = 0;
            using (held)
                foreach (string folder in Directory.GetDirectories(LeftoverCopyDir))
                {
                    if (kept.Contains(Path.GetFullPath(folder).TrimEnd('\\'))) continue;
                    try { DiscardLeftoverCopy(folder, bin); n++; } catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); }
                }
            return n;
        }
    }
}
