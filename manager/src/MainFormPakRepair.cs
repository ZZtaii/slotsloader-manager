using System;
using System.Windows.Forms;

namespace SlotsInstaller
{
    // 2.4.2: the Installed banner's Repair for game entries another tool left invalidated (zeroed PAK rows that no
    // installed mod owns; ReplaceEngine.RepairOrphanedRows). One click, the result inline, no popup; refused inline while
    // SF6 or Fluffy runs (both read or write the same PAK entries).
    internal sealed partial class MainForm
    {
        private FlatButton bannerRepair;
        internal const string RepairGameRunning = "Close SF6 to repair game entries.", RepairFluffyRunning = "Close the external mod manager to repair game entries: it edits the same PAK entries.";
        internal string RepairBlocker { get { return gameRunning ? RepairGameRunning : fluffyRunning ? RepairFluffyRunning : null; } }

        private void UpdateRepairButton()
        {
            if (bannerRepair == null) return;
            bannerRepair.Visible = gameStatus != null && gameStatus.OrphanedRows > 0;
            bannerRepair.Enabled = !busy;
            bannerRepair.AccessibleDescription = RepairBlocker ?? "";
        }

        private void RepairGameEntries()
        {
            if (busy) return;
            string blocker = RepairBlocker;
            if (blocker != null) { SetPageStatus(currentPage, blocker, Palette.Accent); return; }
            ReplaceEngine engine = Engine();
            Work("Repairing game entries…", delegate { return engine.RepairOrphanedRows(true).Message; }, delegate(string message) { RefreshInstalled("✓ " + message); });
        }
    }
}
