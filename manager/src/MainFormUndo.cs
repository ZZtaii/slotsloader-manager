using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace SlotsInstaller
{
    // 2.4.1: no confirmation popups for recoverable actions. The click is the consent; the status strip reports the
    // result with an Undo that restores exactly what the action changed (records, order, enabled state, files) through
    // the existing recovery paths. An Undo lasts until the next change or until the app closes.
    internal sealed partial class MainForm
    {
        private FlatButton undoButton;
        private Action pendingUndo;
        private Func<string> pendingUndoBlocker;     // 1.0.1: why the Undo can't run right now (null = it can)
        private bool keepUndo;                       // the installed-list refresh after an action is not a new change
        internal string UndoLabel = "";              // tests: what Undo would undo
        internal bool UndoAvailable { get { return pendingUndo != null; } }

        private void BuildUndo(Panel status)
        {
            undoButton = Button("Undo", false, 64, delegate { PerformUndo(); });
            undoButton.Dock = DockStyle.Right; undoButton.Font = Palette.Small; undoButton.Visible = false; undoButton.Margin = Padding.Empty;
            status.Controls.Add(undoButton);
            // 1.0.1: closing the app ends the Undo, so its kept copies go too (start-up deletes any a crash left).
            FormClosed += delegate { DeleteRemovedCopies(undoFolders, false); };
        }
        // Shows "message" with Undo until the next change.
        private void OfferUndo(string message, Action undo, Func<string> blocker = null)
        {
            pendingUndo = undo; pendingUndoBlocker = blocker; UndoLabel = message ?? "";
            if (undoButton != null) { undoButton.Visible = true; undoButton.AccessibleName = "Undo: " + UndoLabel; }
        }
        // discardCopies: the Undo ends unused, so the .removed copies only it could put back are deleted (1.0.1, the
        // user: kept copies go as soon as their Undo ends). PerformUndo passes false: its copies are about to be used.
        private void ClearUndo(bool discardCopies = true)
        {
            string[] copies = undoFolders;
            pendingUndo = null; pendingUndoBlocker = null; UndoLabel = ""; undoFolders = new string[0];
            if (undoButton != null) undoButton.Visible = false;
            if (discardCopies && copies.Length > 0) DeleteRemovedCopies(copies, true);
        }
        private void DeleteRemovedCopies(string[] folders, bool background)
        {
            if (folders == null || folders.Length == 0 || String.IsNullOrWhiteSpace(config.GameRoot)) return;
            ReplaceEngine engine;
            try { engine = Engine(); } catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); return; }
            System.Threading.WaitCallback delete = delegate { foreach (string f in folders) { try { engine.DeleteRemoved(f); } catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); } } };
            if (background) System.Threading.ThreadPool.QueueUserWorkItem(delete); else delete(null);
        }
        // The copies an Undo that is running right now restores (protected until it finishes; ProtectedRemovedFolders).
        private volatile string[] undoInFlight = new string[0];
        // 2.4.2: the .removed copies an Undo of this window still needs (never pruned; see ReplaceEngine.PruneRemoved).
        private volatile string[] undoFolders = new string[0];
        internal IEnumerable<string> ProtectedRemovedFolders()
        {
            RemovedReplacement inline = lastRemoved;
            return undoFolders.Concat(undoInFlight).Concat(inline == null ? new string[0] : new[] { inline.Folder }).ToList();
        }
        internal void PerformUndo()
        {
            Action undo = pendingUndo;
            if (undo == null || busy) return;
            // 1.0.1: an Undo that can't run yet says why and stays (it used to fail and vanish, its copies unprotected).
            string why = null;
            try { if (pendingUndoBlocker != null) why = pendingUndoBlocker(); } catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); }
            if (why != null) { SetPageStatus(currentPage, why, Palette.Accent); return; }
            string label = UndoLabel;
            undoInFlight = undoFolders;
            ClearUndo(false);
            RecordUndo(label);   // 2.4.1 history: its own event (MainFormHistory.cs); the restore it runs records its own
            undo();
        }
        // Called when any Work starts: a new change ends the previous Undo (the refresh that reports a result does not).
        private void UndoOnWorkStart() { if (!keepUndo) ClearUndo(); }

        // ---- Replace original: every record of this mod's source, one click (Mod library footer and right-click).
        internal void UninstallAllReplacements(string sourcePath)
        {
            if (busy || String.IsNullOrEmpty(sourcePath)) return;
            List<GameModRow> rows = LinkedReplacements(sourcePath);
            if (rows.Count == 0) return;
            string blocker = ReplacementUninstallBlocker(rows);
            if (blocker != null) { SetPageStatus(currentPage, blocker, Palette.Accent); return; }
            List<string> ids = rows.Select(r => r.Mod.Id).ToList();
            string name = package != null && ReplacePlanner.SameSource(package.SourcePath, sourcePath) ? package.Name : rows[0].Name;
            UninstallReplacementIds(ids, "Uninstalled " + name + (ids.Count > 1 ? " (" + ids.Count + ")" : ""), "Uninstalling " + name);
        }
        // 1.0.1: uninstall means uninstall (ReplaceEngine.UninstallForGood): no copy kept, so no Undo; the library's
        // source reinstalls it. A mod whose source is gone keeps its files in the Recycle Bin (said inline).
        private void UninstallReplacementIds(List<string> ids, string done, string label)
        {
            IRecycleBin bin = RecycleBin.Current;
            ChangeWithDriftChoice(label, delegate(ReplaceEngine e, DriftChoice choice)
            {
                var messages = new List<string>();
                foreach (string id in ids)
                {
                    if (e.LoadState().Find(id) == null) continue;   // already removed by an earlier attempt (drift retry)
                    string message = e.UninstallForGood(id, choice, bin);
                    if (!String.IsNullOrEmpty(message) && !messages.Contains(message)) messages.Add(message);
                }
                return String.Join(" ", messages.Where(m => m.Contains("costume pak") || m.Contains("source is gone")));
            }, done + ".");
        }
        internal static string UndoMissingText(int missing) { return "Not restored: " + ReplacePlanner.N(missing, "mod") + " (copy not saved). The Manager tries again at the next start."; }
        private static string RestoreFolders(ReplaceEngine engine, List<string> folders)
        {
            // 2.4.2: a copy whose move into .removed failed is tried again first, and one still missing is reported.
            try { engine.RetryTrash(); } catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); }
            List<RemovedReplacement> listed = engine.ListRemoved();
            // Lowest original index first, so every record lands back at its own position.
            var removed = folders.Select(f => new { Folder = f, Record = listed.FirstOrDefault(r => String.Equals(r.Folder, f, StringComparison.OrdinalIgnoreCase)) })
                .Where(x => x.Record != null).OrderBy(x => x.Record.Record.Index).ToList();
            foreach (var x in removed) { ReplaceMod restored; engine.RestoreRemoved(x.Folder, out restored); }
            int missing = folders.Count - removed.Count;
            return missing == 0 ? "" : UndoMissingText(missing);
        }

        // ---- Extra outfits: every installed copy from this mod's source.
        internal void UninstallAllOutfits(string sourcePath)
        {
            if (busy) return;
            List<InstalledOutfit> matches = LinkedOutfits(sourcePath);
            if (matches.Count == 0) return;
            string name = package != null && String.Equals(package.SourcePath, sourcePath, StringComparison.OrdinalIgnoreCase) ? package.Name : matches[0].Name;
            RemoveOutfitsWithUndo(matches, "Uninstalled " + name + (matches.Count > 1 ? " (" + matches.Count + ")" : ""));
        }
        // 1.0.1: uninstall means uninstall (OutfitSetStore.UninstallOutfits): deleted, or in the Recycle Bin when there
        // is no source to reinstall from. No copy is kept, so there is no Undo.
        private void RemoveOutfitsWithUndo(List<InstalledOutfit> outfits, string done)
        {
            string root = config.CostumeRoot; IRecycleBin bin = RecycleBin.Current;
            Work("Uninstalling…", delegate { return OutfitSetStore.UninstallOutfits(root, outfits, bin); }, delegate(OutfitSetStore.UninstallResult r)
            {
                string note = (r.Recycled > 0 ? " " + ReplacePlanner.N(r.Recycled, "outfit") + " without a source went to the Recycle Bin." : "")
                    + (r.Kept.Count > 0 ? " Still installed: " + String.Join("; ", r.Kept) + "." : "");
                if (r.Deleted + r.Recycled == 0) { RefreshInstalled(); SetPageStatus(currentPage, "Nothing uninstalled." + note, Palette.Accent); return; }
                RefreshInstalled("✓ " + done + "." + note);
            });
        }

        // ---- 1.0.1: Restore game to vanilla (gear → Restore). A reset with no Undo, so the one question first.
        private void RestoreGameToVanilla()
        {
            if (busy || String.IsNullOrWhiteSpace(config.GameRoot)) return;
            if (!ConfirmVanilla()) return;
            ReplaceEngine engine = Engine();
            Work("Restoring the game to vanilla…", delegate { return engine.RestoreVanilla(); }, delegate(string message) { RefreshInstalled("✓ " + message); });
        }
        internal const string VanillaQuestion = "Restore SF6 to vanilla?\n\nThis removes everything this Manager installed and every mod the external mod manager installed (its list is cleared), deletes all patch PAKs and the natives folder, and puts back the game entries mods invalidated. REFramework and your extra outfits stay.\n\nThere is no Undo. Your mods stay in your libraries to reinstall.";
        private bool ConfirmVanilla()
        {
            if (Program.Headless && ThemedMessageBox.TestResponder == null) return false;
            bool ignored;
            return ThemedMessageBox.Ask(this, VanillaQuestion, "Restore game to vanilla", MessageBoxIcon.Warning, new[] { "Restore to vanilla", "Cancel" }, 1, null, out ignored) == 0;
        }

        // ---- Restore original game files: recoverable since 2.4.1 (every record is kept in .removed), so no popup.
        private void RestoreAllWithUndo()
        {
            var folders = new List<string>();
            ChangeWithDriftChoice("Restoring original game files", delegate(ReplaceEngine e, DriftChoice c)
            {
                List<string> kept; string result = e.RestoreAll(c, out kept); folders.Clear(); folders.AddRange(kept); undoFolders = folders.ToArray(); return result;
            }, "Original game files restored.", delegate(ReplaceEngine e) { try { return RestoreFolders(e, folders); } finally { undoInFlight = new string[0]; } }, RestoreAllUndoBlocker);
        }
        // Putting the replacements back is an install, which the external-manager lockout refuses while that manager
        // has mods (found in the user's live test, 2026-10-17); a running manager blocks game-file changes too.
        private string RestoreAllUndoBlocker()
        {
            if (FluffyBlocksPak) return "Can't undo yet: " + FluffyPakReason;
            int mods = Engine().Fluffy().Mods.Count;
            return mods == 0 ? null : "Can't undo yet: the external mod manager has " + ReplacePlanner.N(mods, "mod") + " installed. Uninstall " + (mods == 1 ? "it" : "them") + " there, close it, then press Undo again.";
        }
    }
}
