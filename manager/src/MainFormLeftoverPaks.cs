using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace SlotsInstaller
{
    // 1.0.11: a patch PAK no mod manager lists (ReplaceLeftoverPaks.cs) is a row on the Installed page. Remove PAK takes it
    // out on the click (no popup, the click is the consent), refused inline while SF6 or the external mod manager runs;
    // Undo puts it back while its number is still the empty placeholder or free. Its copy goes to the Recycle Bin when
    // the Undo ends. A PAK added by hand looks the same, so nothing happens without the click.
    internal sealed partial class MainForm
    {
        internal const string LeftoverRemoveText = "Remove PAK";
        private string LeftoverBlocker { get { return gameRunning ? "Close SF6 to " + ReplaceEngine.LeftoverRemoveAction + "." : fluffyRunning ? GameProcesses.FluffyPakMessage(ReplaceEngine.LeftoverRemoveAction) : null; } }
        private static string LeftoverRowText(GameModRow row) { return ReplaceEngine.LeftoverReason + "   ·   " + Utilities.FriendlyBytes(row.Leftover.Length) + "   ·   " + row.Leftover.Name; }

        private void ShowLeftoverRow(GameModRow row)
        {
            PatchPak pak = row.Leftover;
            installedTitle.Text = row.Name;
            installedChips.SetChips(new List<ChipStrip.Chip> { ChipStrip.Quiet(ReplaceEngine.LeftoverTitle), ChipStrip.Warn("No mod manager lists it"), ChipStrip.Neutral(Utilities.FriendlyBytes(pak.Length)) });
            installedChips.Cursor = Cursors.Default;
            installedDetail.Text = pak.Path; installedPathRow.Visible = true;
            installedSectionCaption.Text = "LEFTOVER PATCH PAK";
            var lines = new List<string>
            {
                pak.Name + " is in the game folder, but neither this Manager nor the external mod manager lists it, so SF6 still loads it: a mod you uninstalled there can still show in game.",
                "",
                LeftoverRemoveText + " takes it out of the game folder; Undo puts it back. " + (row.LeftoverBelowAnother ? "A patch PAK after it stays, so an empty placeholder keeps its number and the PAKs after it still load." : "It is the last patch PAK, so nothing takes its place."),
                "A PAK you added by hand looks the same: remove it only if you don't want it. Close SF6 and the external mod manager first.",
            };
            installedHelp.Text = String.Join(Environment.NewLine, lines); installedHelp.Select(0, 0);
        }

        private void RemoveLeftoverRow(GameModRow row)
        {
            if (row == null || row.Leftover == null || busy) return;
            string blocker = LeftoverBlocker;
            if (blocker != null) { SetPageStatus(currentPage, blocker, Palette.Accent); return; }
            string name = row.Leftover.Name, done = "Removed leftover PAK " + name + ".";
            ReplaceEngine engine = Engine(); string kept = null;
            Work("Removing " + name + "…", delegate { string folder; string m = engine.RemoveLeftoverPak(name, out folder); kept = folder; undoFolders = new[] { folder }; return m; }, delegate(string message)
            {
                string folder = kept;
                Finished(done, message, delegate(ReplaceEngine e) { try { return e.RestoreLeftoverPak(folder); } finally { undoInFlight = new string[0]; } }, LeftoverUndoBlocker);
            });
        }
        private string LeftoverUndoBlocker()
        {
            if (gameRunning) return "Can't undo yet: close SF6 to " + ReplaceEngine.LeftoverRestoreAction + ".";
            if (fluffyRunning) return "Can't undo yet: " + GameProcesses.FluffyPakMessage(ReplaceEngine.LeftoverRestoreAction);
            return null;
        }
    }
}
