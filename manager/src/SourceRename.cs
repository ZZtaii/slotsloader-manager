using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace SlotsInstaller
{
    // 1.0.5 Rename (the user; Mod library right-click or F2): renames a mod's archive or folder in its library. The same
    // mods as Delete may be renamed (LibraryDelete.ScopeError), never one an external mod manager installed. Installed
    // copies keep pointing at it: the extra outfits' manifests, Install all variants records and Replace-original
    // records take the new path, and the history moves with it (HistoryKinds.Rename). If any of that fails, every step
    // is put back, the file name included. The Assistant has no rename tool.
    internal static class SourceRename
    {
        internal sealed class Outcome
        {
            internal string From, To, Error;
            internal bool Refused, Done;
            internal int Outfits, ArchiveCopies, Replacements;
        }

        private static readonly Regex Reserved = new Regex(@"^(con|prn|aux|nul|com[1-9]|lpt[1-9])(\..*)?$", RegexOptions.IgnoreCase);

        // The new full path for name (an archive keeps its extension; typing it again is fine), or why not.
        internal static string Target(string full, bool folder, string name, out string target)
        {
            target = null;
            string n = (name ?? "").Trim();
            string extension = folder ? "" : Path.GetExtension(full);
            if (!folder && extension.Length > 0 && n.Length > extension.Length && n.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) n = n.Substring(0, n.Length - extension.Length).TrimEnd();
            if (n.Length == 0) return "the name is empty.";
            if (n.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return "a name can't contain \\ / : * ? \" < > |.";
            if (n.EndsWith(".")) return "a name can't end with a dot.";
            if (Reserved.IsMatch(n)) return "Windows reserves the name " + n + ".";
            string parent = Path.GetDirectoryName(full);
            if (parent == null) return "its folder is not valid.";
            target = Path.Combine(parent, n + extension);
            if (target.Length >= 260) { target = null; return "the new path is too long for Windows."; }
            bool caseOnly = String.Equals(target, full, StringComparison.OrdinalIgnoreCase);
            if (!caseOnly && (File.Exists(target) || Directory.Exists(target))) { string taken = Path.GetFileName(target); target = null; return taken + " already exists in that folder."; }
            return null;
        }

        // Worker thread: the checks again (the disk may have changed since the click), the rename, then the records.
        internal static Outcome Run(string source, string name, IEnumerable<string> libraries, IEnumerable<KeyValuePair<string, string>> protectedPaths, string costumeRoot, Func<ReplaceEngine> engineFactory, string sevenZipPath = "")
        {
            var o = new Outcome();
            try
            {
                string full; bool folder;
                string refusal = LibraryDelete.ScopeError(source, libraries, protectedPaths, out full, out folder);
                if (refusal != null) { o.Error = RenameWording(refusal); o.Refused = true; return o; }
                string target;
                refusal = Target(full, folder, name, out target);
                if (refusal != null) { o.Error = refusal; o.Refused = true; return o; }
                o.From = full; o.To = target;
                if (String.Equals(target, full, StringComparison.Ordinal)) { o.Done = true; return o; }   // same name: nothing to do
                string busy = null;
                for (int attempt = 1; ; attempt++)
                {
                    long bytes; bool inUse; busy = LibraryDelete.Inspect(full, folder, out bytes, out inUse);
                    if (busy == null || !inUse || attempt >= LibraryDelete.InUseAttempts) break;
                    Thread.Sleep(LibraryDelete.InUseDelay);
                }
                if (busy != null) { o.Error = busy.Replace("Delete never follows links", "Rename never follows links"); return o; }

                bool hasCostumes = !String.IsNullOrWhiteSpace(costumeRoot) && Directory.Exists(Path.GetDirectoryName(Path.GetFullPath(costumeRoot)) ?? "");
                ReplaceEngine engine = engineFactory == null ? null : engineFactory();
                bool hasState = engine != null && File.Exists(engine.StatePath);
                LibraryIdentityRecord cachedIdentity = null;
                using (hasCostumes ? Installer.AcquireMutation(costumeRoot) : null)
                using (hasState ? engine.Lock() : null)
                {
                    if (hasState && engine.JournalPending) { o.Error = "a change to the game folder didn't finish. Restart the Manager to complete it first."; return o; }
                    var undo = new Stack<Action>();
                    try
                    {
                        if (!folder) cachedIdentity = Library.CaptureRenameIdentity(full, sevenZipPath);
                        Move(full, target, folder);
                        undo.Push(delegate { Move(target, full, folder); });
                        if (hasCostumes) o.Outfits = RenameInManifests(costumeRoot, full, target, undo);
                        o.ArchiveCopies = ArchiveInstalls.RenameSource(full, target);
                        if (o.ArchiveCopies > 0) undo.Push(delegate { ArchiveInstalls.RenameSource(target, full); });
                        if (hasState) o.Replacements = RenameInState(engine, full, target, undo);
                    }
                    catch (Exception ex)
                    {
                        if (ex is OutOfMemoryException) throw;
                        string rollback = "";
                        while (undo.Count > 0)
                        {
                            try { undo.Pop()(); }
                            catch (Exception again) { if (again is OutOfMemoryException) throw; Program.Log(again); rollback = " Some records could not be put back; see the log."; }
                        }
                        o.Error = ex.Message + rollback; o.From = full; o.To = null;
                        return o;
                    }
                }
                Library.CompleteSourceRename(full, target, cachedIdentity, sevenZipPath);
                if (hasCostumes) OutfitSetStore.RenameSource(costumeRoot, full, target);   // 1.0.8: list presets follow the mod (logs, never throws)
                History.Record(null, new HistoryEvent { Kind = HistoryKinds.Rename, Source = target, PrevId = History.Normalize(full), Name = folder ? Path.GetFileName(target) : Path.GetFileNameWithoutExtension(target), Detail = "from " + Path.GetFileName(full) });   // never throws
                o.Done = true;
            }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; o.Error = ex.Message; }
            return o;
        }

        // Delete's scope refusals, in Rename's words.
        internal static string RenameWording(string refusal)
        {
            return (refusal ?? "").Replace("Delete never removes a library itself.", "A library itself is renamed in Settings.")
                .Replace("Only a library's own mods can be deleted here.", "Only a library's own mods can be renamed here.")
                .Replace("Delete never follows links.", "Rename never follows links.");
        }

        // A case-only change (mod.zip -> Mod.zip) goes through a temporary name, which Windows needs.
        private static void Move(string from, string to, bool folder)
        {
            if (String.Equals(from, to, StringComparison.OrdinalIgnoreCase))
            {
                string temp = Path.Combine(Path.GetDirectoryName(from), "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".rename");
                Move(from, temp, folder);
                try { Move(temp, to, folder); } catch { Move(temp, from, folder); throw; }
                return;
            }
            if (folder) Directory.Move(from, to); else File.Move(from, to);
        }

        // Extra outfits (enabled and disabled) whose manifest names the old source. Returns how many changed.
        private static int RenameInManifests(string costumeRoot, string from, string to, Stack<Action> undo)
        {
            int changed = 0;
            foreach (Installer.ManagedManifestSnapshot snapshot in Installer.ManagedManifests(costumeRoot))
            {
                InstallManifest m = snapshot.Manifest;
                if (!Same(m.SourcePath, from)) continue;
                m.SourcePath = to;
                DurableFile.WriteJson(snapshot.File, m);
                string file = snapshot.File; byte[] before = snapshot.Before;
                undo.Push(delegate { DurableFile.Write(file, before); });
                changed++;
            }
            return changed;
        }

        // Replace-original and tool records of the old source. Saved under the game lock the caller holds.
        private static int RenameInState(ReplaceEngine engine, string from, string to, Stack<Action> undo)
        {
            ReplaceState state = engine.LoadState();
            int changed = 0;
            foreach (ReplaceMod mod in state.Mods) if (Same(mod.SourcePath, from)) { mod.SourcePath = to; changed++; }
            if (changed == 0) return 0;
            string file = engine.StatePath;
            byte[] before = File.ReadAllBytes(file);
            DurableFile.WriteJson(file, state);
            undo.Push(delegate { DurableFile.Write(file, before); });
            return changed;
        }

        private static bool Same(string a, string b)
        {
            if (String.IsNullOrWhiteSpace(a) || String.IsNullOrWhiteSpace(b)) return false;
            try { return String.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase); }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; return false; }
        }
    }
}
