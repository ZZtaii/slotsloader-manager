using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace SlotsInstaller
{
    // 1.0.24 (the user: "lets also allow mods in installed tab to be draggable too for load order"): the replacements and
    // tool mods of the Installed list are dragged into their priority order, as Move up / Move down move them (both stay).
    // A line shows where the dragged rows go; near the list's top or bottom edge it scrolls. MoveGameRowsTo does the move.
    internal sealed partial class MainForm
    {
        private int installedDropBefore = -1;   // the priority place the dragged rows go before (0-based); -1: none
        private bool installedDragging;
        private const string InstalledDragData = "slotsloader-priority";

        private void WireInstalledDrag()
        {
            installedList.AllowDrop = true;
            installedList.ItemDrag += delegate
            {
                if (MovableSelection().Count == 0 || (busy ? !MovePending : !RowChangesAllowed)) return;
                installedDragging = true;
                try { installedList.DoDragDrop(InstalledDragData, DragDropEffects.Move); }
                finally { installedDragging = false; installedDropBefore = -1; installedList.Invalidate(); }
            };
            installedList.DragOver += delegate(object s, DragEventArgs e)
            {
                if (!installedDragging || e.Data == null || !e.Data.GetDataPresent(typeof(string))) { e.Effect = DragDropEffects.None; return; }
                Point p = installedList.PointToClient(new Point(e.X, e.Y));
                ScrollInstalledNear(p.Y);
                int before = InstalledDropPlace(p.Y);
                e.Effect = before >= 0 ? DragDropEffects.Move : DragDropEffects.None;
                if (before != installedDropBefore) { installedDropBefore = before; installedList.Invalidate(); }
            };
            installedList.DragLeave += delegate { if (installedDropBefore >= 0) { installedDropBefore = -1; installedList.Invalidate(); } };
            installedList.DragDrop += delegate
            {
                int before = installedDropBefore; installedDropBefore = -1; installedList.Invalidate();
                if (before >= 0) MoveGameRowsTo(before);
            };
        }

        // The priority place a drop at y means: before the replacement row under it (after it, on its lower half); after
        // the last one below them all; -1 over anything else (an extra outfit)
        internal int InstalledDropPlace(int y)
        {
            List<GameModRow> own = gameRows.Where(r => r.Mod != null).ToList();
            if (own.Count == 0) return -1;
            ListViewItem item = installedList.GetItemAt(Px(24), y);
            GameModRow row = item == null ? null : item.Tag as GameModRow;
            if (row != null && row.Mod != null)
            {
                int i = own.IndexOf(row); if (i < 0) return -1;
                Rectangle b = item.Bounds;
                return y > b.Top + b.Height / 2 ? i + 1 : i;
            }
            // below the last replacement row shown: after it
            ListViewItem last = installedList.Items.Cast<ListViewItem>().LastOrDefault(it => { var r = it.Tag as GameModRow; return r != null && r.Mod != null; });
            if (last != null && item == null && y > last.Bounds.Bottom) return own.Count;
            return -1;
        }

        // Near the top or bottom edge the list scrolls one row
        private void ScrollInstalledNear(int y)
        {
            int edge = Px(26);
            ListViewItem top = installedList.TopItem; if (top == null) return;
            if (y < edge && top.Index > 0) installedList.Items[top.Index - 1].EnsureVisible();
            else if (y > installedList.ClientSize.Height - edge)
            {
                ListViewItem low = installedList.GetItemAt(Px(24), installedList.ClientSize.Height - 2);
                if (low != null && low.Index + 1 < installedList.Items.Count) installedList.Items[low.Index + 1].EnsureVisible();
            }
        }

        // The drop line, over a replacement row's top (or the last one's bottom)
        private void DrawInstalledDropLine(DrawListViewSubItemEventArgs e, GameModRow row)
        {
            if (installedDropBefore < 0 || row == null || row.Mod == null) return;
            List<GameModRow> own = gameRows.Where(r => r.Mod != null).ToList();
            int i = own.IndexOf(row); if (i < 0) return;
            int y = i == installedDropBefore ? e.Bounds.Top : i == own.Count - 1 && installedDropBefore == own.Count ? e.Bounds.Bottom - Px(3) : -1;
            if (y < 0) return;
            using (Brush b = new SolidBrush(Palette.Accent)) e.Graphics.FillRectangle(b, e.Bounds.Left + Px(6), y, e.Bounds.Width - Px(12), Px(3));
        }
    }
}
