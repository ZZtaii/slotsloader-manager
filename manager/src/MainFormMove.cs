using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows.Forms;

namespace SlotsInstaller
{
    // 1.0.18 (the user: "move up and down needs to be more smooth", and the order looked wrong afterwards). Move up/down
    // reorders the Installed list at once: the row moves (the list shows replacements in priority order,
    // MainFormInstalledSort.cs), stays selected and in view, and its "#n" and who-wins details follow. The new order is
    // saved MoveSaveDelay after the last click, as one change, without the busy look (no progress bar, the list and the
    // Move buttons keep working; other actions wait, as during any change). Clicks during the save are saved next. Undo
    // puts back the order from before the first click.
    internal sealed partial class MainForm
    {
        internal static int MoveSaveDelay = 350;   // ms; checks may shorten it
        private Timer moveTimer;
        private List<string> moveBaseOrder;        // the priority order before the first unsaved click (Undo)
        private readonly List<string> movedIds = new List<string>();
        private bool moveDirty, moveSaving, moveClosingHooked;
        internal bool MovePending { get { return moveDirty || moveSaving; } }

        private List<string> LocalOrder() { return gameRows.Where(r => r.Mod != null).Select(r => r.Mod.Id).ToList(); }

        // The rows Move up/down acts on: the selected replacements and tool mods, when nothing else is selected.
        private List<GameModRow> MovableSelection()
        {
            if (installedList == null) return new List<GameModRow>();
            List<GameModRow> picked = SelectedGameRows();
            return picked.Count > 0 && picked.Count == installedList.SelectedItems.Count && picked.All(r => r.Mod != null) ? picked : new List<GameModRow>();
        }
        // 1.0.18 (the user: multi-select had no Move up/down): whether a selected row has an unselected one above (-1) or below (+1).
        internal bool CanMoveSelection(int delta)
        {
            var set = new HashSet<GameModRow>(MovableSelection());
            if (set.Count == 0) return false;
            List<GameModRow> own = gameRows.Where(r => r.Mod != null).ToList();
            for (int i = 0; i < own.Count; i++)
            {
                int j = i + (delta < 0 ? -1 : 1);
                if (set.Contains(own[i]) && j >= 0 && j < own.Count && !set.Contains(own[j])) return true;
            }
            return false;
        }

        // 1.0.18 (the user: multi-select had no Disable): Enable selection / Disable selection for several selected
        // replacements and tool mods, one change; Undo puts each one's state back. False when one row (or none) is selected.
        private bool SetSelectionEnabled(bool enabled)
        {
            List<GameModRow> picked = MovableSelection();
            if (picked.Count < 2) return false;
            if (!RowChangesAllowed) return true;
            List<string> ids = picked.Where(r => r.Mod.Enabled != enabled).Select(r => r.Mod.Id).ToList();
            if (ids.Count == 0) return true;
            string done = (enabled ? "Enabled " : "Disabled ") + ReplacePlanner.N(ids.Count, "mod") + ".";
            ChangeGameRow(enabled ? "Enabling replacements" : "Disabling replacements", delegate(ReplaceEngine e) { return e.SetEnabled(ids, enabled); }, done, delegate(ReplaceEngine e) { return e.SetEnabled(ids, !enabled); });
            return true;
        }

        private void MoveGameRow(int delta)
        {
            List<GameModRow> picked = MovableSelection();
            if (picked.Count == 0 || delta == 0) return;
            if (busy ? !MovePending : !RowChangesAllowed) return;
            if (!GameIdle("changing priority")) return;
            List<GameModRow> own = gameRows.Where(r => r.Mod != null).ToList();
            // The selected rows move one place together; a row already against the edge (or another selected row) waits.
            var set = new HashSet<GameModRow>(picked); bool moved = false;
            if (delta < 0) { for (int i = 1; i < own.Count; i++) if (set.Contains(own[i]) && !set.Contains(own[i - 1])) { GameModRow t = own[i - 1]; own[i - 1] = own[i]; own[i] = t; moved = true; } }
            else { for (int i = own.Count - 2; i >= 0; i--) if (set.Contains(own[i]) && !set.Contains(own[i + 1])) { GameModRow t = own[i + 1]; own[i + 1] = own[i]; own[i] = t; moved = true; } }
            if (!moved) return;
            if (!MovePending)
            {
                UndoOnWorkStart();   // as every change: the previous Undo ends
                moveBaseOrder = LocalOrder(); movedIds.Clear();
                busy = true;         // quiet: nothing else starts until the order is saved
                if (hoverTimer != null) { hoverTimer.Stop(); hoverNode = null; }
            }
            // The replacement and tool rows come first in gameRows, in priority order.
            for (int i = 0; i < own.Count; i++) own[i].Priority = i + 1;
            gameRows = own.Concat(gameRows.Where(r => r.Mod == null)).ToList();
            UpdateShareWinners();
            foreach (GameModRow p in picked) if (!movedIds.Contains(p.Mod.Id, StringComparer.OrdinalIgnoreCase)) movedIds.Add(p.Mod.Id);
            moveDirty = true;
            FillInstalled();
            GameModRow row = picked.OrderBy(p => p.Priority).First();
            ListViewItem item = installedList.Items.Cast<ListViewItem>().FirstOrDefault(i => Object.ReferenceEquals(i.Tag, delta < 0 ? row : picked.OrderBy(p => p.Priority).Last()));
            if (item != null) { item.Focused = true; item.EnsureVisible(); }
            ShowInstalledSelection();
            string where = picked.Count > 1 ? ReplacePlanner.N(picked.Count, "mod") + " · #" + String.Join(", #", picked.Select(p => p.Priority).OrderBy(p => p)) + " of " + own.Count
                : row.Name + " · #" + row.Priority + " of " + own.Count + (row.Priority == 1 ? " · wins every shared file" : "");
            SetPageStatus(installedPage, where, Palette.Muted);
            if (currentPage == installedPage) { statusText.Text = where; statusText.ForeColor = Palette.Muted; }   // busy: the page status isn't shown by itself
            if (moveTimer == null) { moveTimer = new Timer(); moveTimer.Tick += delegate { SaveMoves(); }; }
            if (!moveClosingHooked) { moveClosingHooked = true; FormClosing += delegate { SaveMovesNow(); }; }
            moveTimer.Stop(); moveTimer.Interval = Math.Max(1, MoveSaveDelay); moveTimer.Start();
        }

        // Each row's shared-file lines name the winner from the priorities shown (the save reloads them anyway).
        private void UpdateShareWinners()
        {
            var priority = gameRows.Where(r => r.Mod != null).ToDictionary(r => r.Mod.Id, r => r.Priority, StringComparer.OrdinalIgnoreCase);
            foreach (GameModRow r in gameRows.Where(x => x.Mod != null))
                foreach (RowShare s in r.Shares)
                {
                    int other;
                    if (String.IsNullOrEmpty(s.OtherId) || !priority.TryGetValue(s.OtherId, out other)) continue;
                    s.OtherPriority = other; s.ThisWins = r.Priority < other;
                }
        }

        private sealed class MoveSave { internal string Message; internal GameRowsResult Game; }

        private void SaveMoves()
        {
            if (moveTimer != null) moveTimer.Stop();
            if (!moveDirty || moveSaving || IsDisposed) return;
            List<string> order = LocalOrder(), moved = movedIds.ToList(), before = moveBaseOrder;
            moveDirty = false; moveSaving = true;
            ReplaceEngine engine = Engine();
            var worker = new BackgroundWorker();
            worker.DoWork += delegate(object s, DoWorkEventArgs e)
            {
                using (InstallMetrics.Measure("priority save"))
                    e.Result = new MoveSave { Message = engine.SetOrder(order, moved), Game = LoadGameRows(false) };
            };
            RunWorkerCompletedEventHandler finished = null;
            finished = delegate(object s, RunWorkerCompletedEventArgs e)
            {
                if (OffUiThread(delegate { finished(s, e); })) return;
                worker.Dispose(); moveSaving = false;
                if (IsDisposed) return;
                if (e.Error != null)
                {
                    moveDirty = false; movedIds.Clear(); moveBaseOrder = null;
                    SetBusy(false, "Ready"); Error(e.Error); RefreshInstalled();   // the saved order comes back
                    return;
                }
                if (moveDirty) { moveTimer.Stop(); moveTimer.Start(); return; }   // more clicks: saved next, the rows shown stay
                var result = (MoveSave)e.Result;
                movedIds.Clear(); moveBaseOrder = null;
                SetBusy(false, "Ready");
                ApplyGameRows(result.Game); FillInstalled();
                GameModRow row = GetGameRowSelection();
                string done = moved.Count == 1 && row != null && row.Mod != null && String.Equals(row.Mod.Id, moved[0], StringComparison.OrdinalIgnoreCase)
                    ? row.Name + " moved to #" + row.Priority : ReplacePlanner.N(moved.Count, "mod") + " moved";
                if (before != null && !before.SequenceEqual(order, StringComparer.OrdinalIgnoreCase))
                    OfferUndo(done, delegate { ChangeGameRow("Undoing", delegate(ReplaceEngine en) { return en.SetOrder(before, moved); }, "Undone: " + done + "."); });
                SetSharedStatus("✓ " + done + "." + (String.IsNullOrEmpty(result.Message) ? "" : " " + result.Message), Palette.Green);
                ShowInstalledSelection();
            };
            worker.RunWorkerCompleted += finished;
            worker.RunWorkerAsync();
        }

        // Closing the window with an unsaved order: saved before it goes.
        private void SaveMovesNow()
        {
            if (!moveDirty) return;
            if (moveTimer != null) moveTimer.Stop();
            moveDirty = false;
            try { Engine().SetOrder(LocalOrder(), movedIds.ToList()); }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); }
        }
    }
}
