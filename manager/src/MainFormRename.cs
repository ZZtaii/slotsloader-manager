using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace SlotsInstaller
{
    // 1.0.5 Rename (the user): Mod library right-click → Rename, or F2. The name is edited in place on the row, like
    // Explorer: Enter or clicking away renames, Esc cancels. No popup (the click is the consent, the old name is one
    // more rename away). The work is SourceRename.Run; installed copies keep pointing at the mod.
    internal sealed partial class MainForm
    {
        internal const string RenameFluffyRefusal = "Installed by an external mod manager: uninstall it there before renaming.";
        private ToolStripMenuItem libraryRenameItem;
        private TextBox renameBox;
        private LibraryEntry renaming;
        private LibraryEntry afterRenameSelection;
        internal int RenameRuns;   // checks: how many renames reached the worker

        // BuildLibrary: after Open in Explorer.
        private void AddLibraryRename(ContextMenuStrip menu)
        {
            libraryRenameItem = new ToolStripMenuItem("Rename") { ShortcutKeyDisplayString = "F2" };
            libraryRenameItem.Click += delegate { LibraryEntry entry = menu.Tag as LibraryEntry; if (entry != null) BeginInvoke((Action)delegate { BeginLibraryRename(entry); }); };
            menu.Items.Add(libraryRenameItem);
        }
        private void PopulateLibraryRename(LibraryEntry entry)
        {
            if (libraryRenameItem == null) return;
            bool show = entry != null && !String.IsNullOrEmpty(entry.SourcePath) && entry.Kind != "Library";
            libraryRenameItem.Visible = show; libraryRenameItem.Enabled = show && !busy;
            libraryRenameItem.AccessibleDescription = show && LinkedFluffy(entry.SourcePath).Count > 0 ? RenameFluffyRefusal : "Renames its archive or folder. Installed copies keep working.";
        }

        internal static string RenameText(LibraryEntry entry)
        {
            if (entry == null || String.IsNullOrEmpty(entry.SourcePath)) return "";
            return entry.Kind == "Folder" ? Path.GetFileName(entry.SourcePath.TrimEnd('\\')) : Path.GetFileNameWithoutExtension(entry.SourcePath);
        }

        internal void BeginLibraryRename(LibraryEntry entry)
        {
            if (entry == null || busy || String.IsNullOrEmpty(entry.SourcePath) || entry.Kind == "Library") return;
            if (LinkedFluffy(entry.SourcePath).Count > 0) { SetPageStatus(libraryPage, RenameFluffyRefusal, Palette.Accent); return; }
            ListViewItem row = modList.Items.Cast<ListViewItem>().FirstOrDefault(i => Object.ReferenceEquals(i.Tag, entry));
            if (row == null) return;
            CloseRenameBox();
            row.EnsureVisible();
            renaming = entry;
            Rectangle r = row.Bounds;
            renameBox = new TextBox { Font = Palette.Strong, BackColor = Palette.Raised, ForeColor = Palette.Text, BorderStyle = BorderStyle.FixedSingle, Text = RenameText(entry), AccessibleName = "New name" };
            renameBox.Bounds = new Rectangle(r.Left + Px(8), r.Top + Px(5), Math.Max(Px(80), r.Width - Px(18)), renameBox.PreferredHeight);
            // Commit during focus transfer, before another control can start background work.
            renameBox.LostFocus += delegate { EndLibraryRename(true); };
            modList.Controls.Add(renameBox);
            renameBox.BringToFront(); renameBox.Focus(); renameBox.SelectAll();
        }

        // ProcessCmdKey: the box takes Enter, Esc and Tab before the window's shortcuts (Esc clears the search, Tab
        // switches INSTALL AS).
        private bool RenameKey(Keys keyData)
        {
            if (renameBox == null || !renameBox.Focused) return false;
            if (keyData == Keys.Escape) { EndLibraryRename(false); return true; }
            if (keyData == Keys.Enter || keyData == Keys.Tab) { EndLibraryRename(true); return true; }
            return false;
        }

        private void CloseRenameBox()
        {
            TextBox box = renameBox; renameBox = null; renaming = null;
            if (box == null) return;
            bool hadFocus = box.Focused;
            modList.Controls.Remove(box); box.Dispose();
            if (hadFocus) modList.Focus();
        }

        private bool CommitRenameBeforeSelection(LibraryEntry next)
        {
            if (renameBox == null) return false;
            afterRenameSelection = next != null && !Object.ReferenceEquals(next, renaming) ? next : null;
            EndLibraryRename(true);
            if (busy) return true;   // completion opens the clicked row after saving the name
            afterRenameSelection = null;
            return false;
        }

        // The list scrolled or resized under the box, or the user is done: rename when the text changed.
        internal void EndLibraryRename(bool commit)
        {
            if (renameBox == null) return;
            LibraryEntry entry = renaming; string name = renameBox.Text;
            if (commit && busy) return;   // keep the edit available rather than silently discarding it
            CloseRenameBox();
            if (!commit || entry == null || String.Equals(name.Trim(), RenameText(entry), StringComparison.Ordinal)) return;
            RenameLibrarySource(entry, name);
        }

        internal void RenameLibrarySource(LibraryEntry entry, string name)
        {
            if (entry == null || busy || String.IsNullOrEmpty(entry.SourcePath) || entry.Kind == "Library") return;
            string label = SourceLabel(entry.SourcePath), source = entry.SourcePath;
            if (LinkedFluffy(source).Count > 0) { SetPageStatus(libraryPage, RenameFluffyRefusal, Palette.Accent); return; }
            string[] libraries = config.Libraries.ToArray(); List<KeyValuePair<string, string>> kept = DeleteProtectedPaths();
            string full, target; bool folder;
            string refusal = LibraryDelete.ScopeError(source, libraries, kept, out full, out folder);
            if (refusal == null) refusal = SourceRename.Target(full, folder, name, out target);
            if (refusal != null) { SetPageStatus(libraryPage, "Can't rename " + label + ": " + SourceRename.RenameWording(refusal), Palette.Accent); return; }
            // The open mod's check may be reading the archive: stop it so its handle doesn't count as "in use".
            if (package != null && SamePath(package.SourcePath, full)) RequestReview(1);
            string root = config.CostumeRoot, sevenZip = config.SevenZipPath;
            ReplaceEngine engine = null;
            if (!String.IsNullOrWhiteSpace(config.GameRoot)) try { engine = Engine(); } catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); }
            RenameRuns++;
            // The installed lists are read again in the same task, so the rows show the new path at once.
            Work("Renaming " + label + "…", delegate
            {
                SourceRename.Outcome outcome = SourceRename.Run(source, name, libraries, kept, root, engine == null ? null : (Func<ReplaceEngine>)delegate { return engine; }, sevenZip);
                ScanResult scan = outcome.Done && !String.Equals(outcome.From, outcome.To, StringComparison.Ordinal) ? WithChangedSources(new ScanResult { Installed = Installer.ListInstalled(root), Game = LoadGameRows(true), Removed = ReadRemovedUsage(root) }) : null;
                return new KeyValuePair<SourceRename.Outcome, ScanResult>(outcome, scan);
            }, delegate(KeyValuePair<SourceRename.Outcome, ScanResult> done) { LibraryRenameDone(entry, label, done.Key, done.Value); });
        }

        private void LibraryRenameDone(LibraryEntry entry, string label, SourceRename.Outcome outcome, ScanResult scan)
        {
            LibraryEntry next = afterRenameSelection; afterRenameSelection = null;
            if (!outcome.Done)
            {
                SetPageStatus(libraryPage, (outcome.Refused ? "Can't rename " : "Couldn't rename ") + label + ": " + outcome.Error, outcome.Refused ? Palette.Accent : Palette.Red);
                return;
            }
            if (String.Equals(outcome.From, outcome.To, StringComparison.Ordinal)) return;
            bool wasOpen = package != null && SamePath(package.SourcePath, outcome.From);
            PackageReload reload = wasOpen ? CapturePackageReload(false) : null;
            ReplaceMod editing = wasOpen ? editingReplacement : null;
            // Every row of this source takes the new path and name; the rest of the list is untouched (no rescan).
            foreach (LibraryEntry e in SameSourceEntries(entry))
            {
                string oldName = RenameText(e);
                e.SourcePath = outcome.To;
                if (String.Equals(e.Name, oldName, StringComparison.Ordinal)) e.Name = RenameText(e);
                Library.RefreshFilenameIdentity(e);
                libraryRows.Remove(e); installStates.Remove(e); libraryRanks.Remove(e);
            }
            if (changedSources.Remove(outcome.From)) changedSources.Add(outcome.To);
            if (wasOpen) ClosePackage();
            string copies = outcome.Outfits + outcome.ArchiveCopies + outcome.Replacements == 0 ? "" : " Installed copies now point to it.";
            string message = "Renamed " + label + " to " + Path.GetFileName(outcome.To) + "." + copies;
            if (scan != null) RefreshInstalledDone(message)(scan);
            else { ApplyFilters(); SetSharedStatus(message, Palette.Green); }
            if (assistantPage != null) assistantPage.LibraryChanged();
            if (next != null && library.Contains(next)) LoadPackage(next);
            else if (wasOpen)
            {
                reload.Renamed = true; reload.CompletionStatus = message;
                if (editing != null) editing.SourcePath = outcome.To;
                pendingReload = reload;
                LoadPackage(entry, null, editing);
            }
            else
            {
                ListViewItem row = modList.Items.Cast<ListViewItem>().FirstOrDefault(i => Object.ReferenceEquals(i.Tag, entry));
                if (row != null) { bool previous = selectingMod; selectingMod = true; try { row.Selected = true; row.Focused = true; row.EnsureVisible(); } finally { selectingMod = previous; } }
            }
        }
    }
}
