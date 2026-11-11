using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace SlotsInstaller
{
    // 1.0.18 (the user, after trying the first version: "shouldn't it be like drag the window and then drag to
    // whatever order"): a list whose rows are dragged into place. A pressed row lifts off the list and follows the
    // pointer; the other rows close up and open a gap where it would land, and it drops there on release. Nothing
    // changes until the release (the first version swapped rows under the pointer as it moved). Alt+Up / Alt+Down move
    // the selected row from the keyboard. Rows are drawn by the subclass, which is told the place a row is shown at.
    internal abstract class DragList<T> : Control where T : class
    {
        protected List<T> items = new List<T>();
        private readonly int logicalRow;
        private int selected = -1, hover = -1;
        private int dragFrom = -1, dragTarget = -1, dragY, grabOffset, pressY;
        private bool lifted;
        internal string Empty = "";
        internal event EventHandler Changed, SelectionChanged;

        protected DragList(int rowHeight)
        {
            logicalRow = rowHeight;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            TabStop = true; BackColor = Palette.Background;
        }
        internal int Count { get { return items.Count; } }
        internal int SelectedIndex { get { return selected; } }
        internal bool Dragging { get { return lifted; } }
        protected int RowHeight { get { return DpiMetrics.Px(this, logicalRow); } }
        protected int Px(int logical) { return DpiMetrics.Px(this, logical); }
        protected void SetItems(IEnumerable<T> list) { items = list.ToList(); selected = items.Count > 0 ? 0 : -1; hover = -1; EndDrag(); Invalidate(); }
        private int IndexAt(int y) { int i = y / Math.Max(1, RowHeight); return y >= 0 && i >= 0 && i < items.Count ? i : -1; }

        internal bool CanMove(int delta) { int to = selected + delta; return selected >= 0 && to >= 0 && to < items.Count; }
        internal void MoveSelected(int delta) { if (CanMove(delta)) MoveTo(selected, selected + delta); }
        internal void Select(int index)
        {
            if (index < 0 || index >= items.Count || index == selected) return;
            selected = index; Invalidate(); KeepInView(index); Raise(SelectionChanged);
        }
        internal void MoveTo(int from, int to)
        {
            if (from < 0 || from >= items.Count || to < 0 || to >= items.Count || from == to) return;
            T item = items[from]; items.RemoveAt(from); items.Insert(to, item);
            selected = to; Invalidate(); KeepInView(to); Raise(Changed);
        }
        internal void RemoveSelected()
        {
            if (selected < 0 || selected >= items.Count) return;
            items.RemoveAt(selected); selected = Math.Min(selected, items.Count - 1); EndDrag();
            Invalidate(); Raise(Changed); Raise(SelectionChanged);
        }
        private void Raise(EventHandler handler) { if (handler != null) handler(this, EventArgs.Empty); }
        private void KeepInView(int index)
        {
            var host = Parent as ScrollableControl;
            if (host == null || !host.AutoScroll) return;
            int top = index * RowHeight, view = -host.AutoScrollPosition.Y;
            if (top < view) host.AutoScrollPosition = new Point(0, top);
            else if (top + RowHeight > view + host.ClientSize.Height) host.AutoScrollPosition = new Point(0, top + RowHeight - host.ClientSize.Height);
        }
        private void EndDrag() { dragFrom = dragTarget = -1; lifted = false; if (Capture) Capture = false; Cursor = Cursors.Default; }

        // The drag as the mouse drives it, for tests: press on a row, move to a y inside the list, release.
        internal void DragForTest(int from, int toY) { BeginPress(from, from * RowHeight + RowHeight / 2); MoveDrag(toY); Release(); }
        // The same without the release: the row stays lifted (screenshots of the drag).
        internal void LiftForTest(int from, int toY) { BeginPress(from, from * RowHeight + RowHeight / 2); MoveDrag(toY); }
        private void BeginPress(int index, int y) { Select(index); dragFrom = index; dragTarget = index; grabOffset = y - index * RowHeight; pressY = dragY = y; lifted = false; }
        private void MoveDrag(int y)
        {
            if (dragFrom < 0) return;
            if (!lifted && Math.Abs(y - pressY) < Px(4)) return;   // a click, not a drag
            lifted = true; dragY = y;
            int top = Math.Max(0, Math.Min((items.Count - 1) * RowHeight, y - grabOffset));
            dragTarget = Math.Max(0, Math.Min(items.Count - 1, (top + RowHeight / 2) / Math.Max(1, RowHeight)));
            // Near the edge of a scrolling host the list scrolls with the drag.
            var host = Parent as ScrollableControl;
            if (host != null && host.AutoScroll)
            {
                int view = -host.AutoScrollPosition.Y, edge = RowHeight / 2;
                if (y - view < edge) host.AutoScrollPosition = new Point(0, Math.Max(0, view - edge));
                else if (y - view > host.ClientSize.Height - edge) host.AutoScrollPosition = new Point(0, view + edge);
            }
            Invalidate();
        }
        private void Release()
        {
            int from = dragFrom, to = dragTarget; bool moved = lifted;
            EndDrag(); Invalidate();
            if (moved && from >= 0 && to >= 0 && from != to) MoveTo(from, to);
        }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e); Focus();
            if (e.Button != MouseButtons.Left) return;
            int i = IndexAt(e.Y); if (i < 0) return;
            BeginPress(i, e.Y); Capture = true;
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragFrom >= 0 && (e.Button & MouseButtons.Left) != 0) { MoveDrag(e.Y); if (lifted) Cursor = Cursors.SizeNS; return; }
            int h = IndexAt(e.Y); if (h != hover) { hover = h; Invalidate(); }
        }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); if (dragFrom >= 0) Release(); }
        protected override void OnMouseCaptureChanged(EventArgs e) { base.OnMouseCaptureChanged(e); if (!Capture && dragFrom >= 0) { EndDrag(); Invalidate(); } }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = -1; Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
        protected override bool IsInputKey(Keys keyData) { Keys k = keyData & Keys.KeyCode; return k == Keys.Up || k == Keys.Down || base.IsInputKey(keyData); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape && dragFrom >= 0) { EndDrag(); Invalidate(); e.Handled = true; return; }
            int step = e.KeyCode == Keys.Up ? -1 : e.KeyCode == Keys.Down ? 1 : 0;
            if (step == 0) return;
            if (e.Alt || e.Control) MoveSelected(step); else Select(selected + step);
            e.Handled = true;
        }

        // place: the position the row is shown at (while another row is lifted, the place it would have after the drop).
        protected abstract void DrawRow(Graphics g, Rectangle bounds, T item, int place, bool selectedRow, bool lifted);
        protected virtual string EmptyText { get { return Empty; } }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics; int h = RowHeight;
            using (Brush bg = new SolidBrush(Palette.Background)) g.FillRectangle(bg, ClientRectangle);
            if (items.Count == 0) { TextRenderer.DrawText(g, EmptyText, Palette.Small, new Rectangle(Px(12), 0, Width - Px(24), Height), Palette.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix); return; }
            for (int i = 0; i < items.Count; i++)
            {
                if (lifted && i == dragFrom) continue;
                int place = !lifted ? i : (i < dragFrom ? i : i - 1); if (lifted && place >= dragTarget) place++;
                var r = new Rectangle(0, place * h, Width, h);
                bool on = !lifted && i == selected;
                using (Brush b = new SolidBrush(on ? Palette.Selected : !lifted && i == hover ? Palette.Raised : Palette.Background)) g.FillRectangle(b, r);
                using (Brush rail = new SolidBrush(on ? Palette.Accent : Palette.Border)) g.FillRectangle(rail, r.Left + Px(2), r.Top + Px(10), Px(3), h - Px(20));
                DrawRow(g, r, items[i], place, on, false);
                using (var pen = new Pen(Palette.Border)) g.DrawLine(pen, r.Left + Px(12), r.Bottom - 1, r.Right - Px(12), r.Bottom - 1);
            }
            if (lifted)
            {
                // Where it lands: an outlined gap. Then the lifted row itself, over everything.
                var gap = new Rectangle(Px(6), dragTarget * h + Px(4), Width - Px(12), h - Px(8));
                using (var pen = new Pen(Palette.Accent, Math.Max(1F, DeviceDpi / 96F)) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash }) g.DrawRectangle(pen, gap);
                int top = Math.Max(0, Math.Min((items.Count - 1) * h, dragY - grabOffset));
                var f = new Rectangle(Px(4), top, Width - Px(8), h);
                using (Brush shade = new SolidBrush(Color.FromArgb(90, 0, 0, 0))) g.FillRectangle(shade, f.Left + Px(3), f.Top + Px(4), f.Width, f.Height);
                using (Brush b = new SolidBrush(Palette.Selected)) g.FillRectangle(b, f);
                using (var pen = new Pen(Palette.Accent, Math.Max(1F, DeviceDpi / 96F * 1.5F))) g.DrawRectangle(pen, f.Left, f.Top, f.Width - 1, f.Height - 1);
                DrawRow(g, f, items[dragFrom], dragTarget, true, true);
            }
            else if (Focused && ShowFocusCues && selected >= 0) using (var pen = new Pen(Palette.Text)) g.DrawRectangle(pen, 0, selected * h, Width - 1, h - 1);
        }
    }

    // Names that share a long start are hard to tell apart in a row ("Main Files - X", "Main Files - X + Option 01 - A",
    // "Main Files - X + Option 02 - B"): when one name is the start of others, those show only what they add ("+ Option
    // 01 - A"). The full name stays the accessible name.
    internal static class LabelShortener
    {
        internal static List<string> Short(IList<string> labels)
        {
            var result = labels.Select(l => l ?? "").ToList();
            foreach (string stem in result.Where(l => l.Length >= 8).Distinct().OrderByDescending(l => l.Length).ToList())
                for (int i = 0; i < result.Count; i++)
                    if (labels[i] != null && labels[i].Length > stem.Length + 3 && labels[i].StartsWith(stem + " + ", StringComparison.Ordinal) && result[i] == labels[i])
                        result[i] = "+ " + labels[i].Substring(stem.Length + 3);
            return result;
        }
    }
}
