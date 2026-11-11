using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace SlotsInstaller
{
    // The outfits of one fighter, one row per slot. The left column is the slot ("Outfit II": it stays) and whether the
    // outfit shown there moved; the rest is the outfit (it moves), on up to two lines.
    internal sealed class OrderRows : DragList<OutfitSlot>
    {
        private List<OutfitSlot> slots = new List<OutfitSlot>();   // the slots, ascending (fixed)
        private readonly Dictionary<OutfitSlot, string> shown = new Dictionary<OutfitSlot, string>();
        internal event EventHandler OrderChanged { add { Changed += value; } remove { Changed -= value; } }
        internal OrderRows(int rowHeight) : base(rowHeight) { }
        // The current slot numbers in the order wanted (OutfitOrder.Apply's input).
        internal List<int> Order { get { return items.Select(s => s.CostumeNo).ToList(); } }
        internal string ShownForTest(int index) { return shown[items[index]]; }
        internal void SetSlots(List<OutfitSlot> list, string fighter)
        {
            slots = list.ToList(); shown.Clear();
            string prefix = (fighter ?? "") + "/";
            // Variants of one mod are told apart by what they add; the mod's own folder or file name is dropped.
            foreach (var group in list.GroupBy(s => s.Source ?? "", StringComparer.OrdinalIgnoreCase))
            {
                var members = group.ToList();
                var names = members.Select(s =>
                {
                    string l = s.Label ?? "";
                    if (group.Key.Length > 0 && l.StartsWith(group.Key + "/", StringComparison.OrdinalIgnoreCase)) l = l.Substring(group.Key.Length + 1);
                    else if (l.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) l = l.Substring(prefix.Length);
                    return l;
                }).ToList();
                List<string> shortNames = LabelShortener.Short(names);
                for (int i = 0; i < members.Count; i++) shown[members[i]] = shortNames[i];
            }
            SetItems(list);
        }
        protected override void DrawRow(Graphics g, Rectangle r, OutfitSlot item, int place, bool selectedRow, bool lifted)
        {
            OutfitSlot slot = slots[Math.Max(0, Math.Min(slots.Count - 1, place))]; bool moved = item.CostumeNo != slot.CostumeNo;
            int slotWidth = Px(110), x = r.Left + Px(14);
            const TextFormatFlags one = TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
            TextRenderer.DrawText(g, slot.Name, Palette.Small, new Rectangle(x, r.Top + Px(8), slotWidth, Px(22)), Palette.Text, one);
            TextRenderer.DrawText(g, moved ? "was " + item.Name : "in place", Palette.Tiny, new Rectangle(x, r.Top + Px(32), slotWidth, Px(18)), moved ? Palette.Accent : Palette.Muted, one);
            var text = new Rectangle(x + slotWidth, r.Top + Px(6), r.Width - slotWidth - Px(28), r.Height - Px(12));
            string label; if (!shown.TryGetValue(item, out label)) label = item.Label;
            TextRenderer.DrawText(g, label, Palette.Small, text, Palette.Text, TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter);
        }
    }

    // The added parts of one outfit as rows, the winner first.
    internal sealed class PartRows : DragList<PartGraft>
    {
        private readonly Func<PartGraft, bool> sourceGone;
        private readonly Dictionary<PartGraft, bool> gone = new Dictionary<PartGraft, bool>();
        internal PartRows(int rowHeight, Func<PartGraft, bool> sourceGone) : base(rowHeight) { this.sourceGone = sourceGone; }
        internal List<PartGraft> Parts { get { return items.ToList(); } }
        protected override string EmptyText { get { return String.IsNullOrEmpty(Empty) ? "No added parts." : Empty; } }
        // The recipe builder's rows: the mod's name first and what is ticked from it under it (every row is a mod in
        // load order there); the Added parts window keeps the option first.
        internal bool ModFirst;
        internal void SetParts(List<PartGraft> list)
        {
            gone.Clear();
            foreach (PartGraft part in list) { bool missing = false; try { missing = sourceGone != null && sourceGone(part); } catch (Exception ex) { if (ex is OutOfMemoryException) throw; } gone[part] = missing; }
            SetItems(list);
        }
        protected override void DrawRow(Graphics g, Rectangle r, PartGraft part, int place, bool selectedRow, bool lifted)
        {
            bool missing = gone.ContainsKey(part) && gone[part];
            int rankWidth = Px(44), x = r.Left + Px(14), w = r.Width - Px(28) - rankWidth;
            const TextFormatFlags one = TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
            TextRenderer.DrawText(g, "#" + (place + 1), Palette.Small, new Rectangle(x, r.Top, rankWidth, r.Height), Palette.Muted, one | TextFormatFlags.VerticalCenter);
            string options = part.Options != null && part.Options.Count > 0 ? String.Join(" + ", part.Options) : "Options";
            string mod = String.IsNullOrEmpty(part.Mod) ? System.IO.Path.GetFileName(part.SourcePath ?? "") : part.Mod;
            if (ModFirst)
            {
                bool whole = part.Options == null || part.Options.Count == 0 || (part.Options.Count == 1 && String.Equals(part.Options[0], mod, StringComparison.OrdinalIgnoreCase));
                // 1.0.18: what the layer is (OutfitParts: "Hair", "Body + Head"), or "Hair only" when only some parts are taken.
                bool only = part.Parts != null && part.Parts.Count > 0;
                string kinds = only ? OutfitParts.Labels(part.Parts) + " only" : OutfitParts.Labels(OutfitParts.Kinds(whole ? mod : String.Join(" ", part.Options), part.Files));
                string under = (whole ? "The whole mod" : String.Join(" + ", part.Options)) + (kinds.Length > 0 ? " · " + kinds : "") + " · " + ReplacePlanner.N((part.Files ?? new List<string>()).Count, "file");
                TextRenderer.DrawText(g, mod, Palette.Strong, new Rectangle(x + rankWidth, r.Top + Px(6), w, Px(22)), Palette.Text, one);
                TextRenderer.DrawText(g, under, Palette.Tiny, new Rectangle(x + rankWidth, r.Top + Px(28), w, Px(18)), Palette.Muted, one);
                return;
            }
            TextRenderer.DrawText(g, options, Palette.Strong, new Rectangle(x + rankWidth, r.Top + Px(6), w, Px(22)), Palette.Text, one);
            string detail = "from " + mod + " · " + ReplacePlanner.N((part.Files ?? new List<string>()).Count, "file") + (place == 0 && Count > 1 ? " · wins shared files" : "") + (missing ? " · its mod is gone: a save can't put it back" : "");
            TextRenderer.DrawText(g, detail, Palette.Tiny, new Rectangle(x + rankWidth, r.Top + Px(28), w, Px(18)), missing ? Palette.Accent : Palette.Muted, one);
        }
    }

    // 1.0.18 (the user: "this scroll is not smooth"): a plain list of names that scrolls by pixels, not by rows. The
    // wheel sets where to go and a short timer glides there; a thin thumb on the right shows the place and can be
    // dragged. Up, Down, Page Up, Page Down, Home and End move the selection.
    internal sealed class SmoothList : Control
    {
        private List<string> rows = new List<string>();
        private readonly int logicalRow;
        private int selected = -1, hover = -1;
        private float offset, target;
        private readonly Timer glide = new Timer { Interval = 12 };
        private bool thumbDrag; private int thumbGrab;
        internal event EventHandler SelectedIndexChanged, Activated;
        internal SmoothList(int rowHeight)
        {
            logicalRow = rowHeight;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            TabStop = true; BackColor = Palette.Background;
            glide.Tick += delegate
            {
                float step = (target - offset) * 0.28F;
                if (Math.Abs(target - offset) < 0.75F) { offset = target; glide.Stop(); } else offset += step;
                Invalidate();
            };
            Disposed += delegate { glide.Dispose(); };
        }
        internal int Count { get { return rows.Count; } }
        internal int SelectedIndex { get { return selected; } set { Select(value, true); } }
        internal int ScrollOffsetForTest { get { return (int)Math.Round(target); } }
        private int RowHeight { get { return DpiMetrics.Px(this, logicalRow); } }
        private int Max { get { return Math.Max(0, rows.Count * RowHeight - ClientSize.Height); } }
        internal void SetRows(IEnumerable<string> names) { details = null; rows = names.ToList(); selected = rows.Count > 0 ? 0 : -1; offset = target = 0; hover = -1; Invalidate(); Raise(SelectedIndexChanged); }
        // Two-line rows (a name and a muted line under it), with nothing selected until a row is clicked: the Mod
        // library's recipe rows, where the selection belongs to either this list or the mod list.
        private List<string> details;
        internal void SetRows(IEnumerable<string> names, IEnumerable<string> under) { rows = names.ToList(); details = under.ToList(); selected = -1; offset = target = 0; hover = -1; Invalidate(); }
        internal void ClearSelection() { if (selected < 0) return; selected = -1; Invalidate(); }
        internal int IndexAtY(int y) { return IndexAt(y); }
        // The colour behind the rows (the Mod library's recipe rows sit on the list's own surface, not on a darker box).
        internal Color Fill = Palette.Background;
        internal void SelectQuietly(int index) { selected = index >= 0 && index < rows.Count ? index : -1; Invalidate(); }
        private void Raise(EventHandler handler) { if (handler != null) handler(this, EventArgs.Empty); }
        private void ScrollTo(float value, bool animate) { target = Math.Max(0, Math.Min(Max, value)); if (animate && IsHandleCreated && Visible) glide.Start(); else { offset = target; Invalidate(); } }
        private void Select(int index, bool keepInView)
        {
            if (index < 0 || index >= rows.Count) return;
            bool changed = index != selected; selected = index;
            if (keepInView)
            {
                int top = index * RowHeight;
                if (top < target) ScrollTo(top, true); else if (top + RowHeight > target + ClientSize.Height) ScrollTo(top + RowHeight - ClientSize.Height, true);
            }
            Invalidate(); if (changed) Raise(SelectedIndexChanged);
        }
        private int IndexAt(int y) { int i = (int)((y + offset) / Math.Max(1, RowHeight)); return y >= 0 && i >= 0 && i < rows.Count ? i : -1; }
        private Rectangle Thumb()
        {
            if (Max == 0) return Rectangle.Empty;
            int track = ClientSize.Height, w = DpiMetrics.Px(this, 5), length = Math.Max(DpiMetrics.Px(this, 28), (int)((long)track * track / Math.Max(1, rows.Count * RowHeight)));
            return new Rectangle(ClientSize.Width - w - DpiMetrics.Px(this, 3), (int)((track - length) * (offset / Max)), w, length);
        }
        protected override void OnMouseWheel(MouseEventArgs e) { base.OnMouseWheel(e); ScrollTo(target - e.Delta / 120F * RowHeight * 3, true); }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e); Focus();
            if (e.Button != MouseButtons.Left) return;
            Rectangle thumb = Thumb();
            if (!thumb.IsEmpty && e.X >= thumb.Left - DpiMetrics.Px(this, 6)) { thumbDrag = true; thumbGrab = thumb.Contains(e.Location) ? e.Y - thumb.Top : thumb.Height / 2; Capture = true; DragThumb(e.Y); return; }
            Select(IndexAt(e.Y), true);
        }
        private void DragThumb(int y) { Rectangle thumb = Thumb(); int free = ClientSize.Height - thumb.Height; if (free > 0) ScrollTo((float)(y - thumbGrab) / free * Max, false); }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (thumbDrag) { DragThumb(e.Y); return; }
            int h = IndexAt(e.Y); if (h != hover) { hover = h; Invalidate(); }
        }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); thumbDrag = false; Capture = false; }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = -1; Invalidate(); }
        protected override void OnMouseDoubleClick(MouseEventArgs e) { base.OnMouseDoubleClick(e); if (IndexAt(e.Y) == selected && selected >= 0) Raise(Activated); }
        protected override void OnResize(EventArgs e) { base.OnResize(e); ScrollTo(target, false); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
        protected override bool IsInputKey(Keys keyData) { Keys k = keyData & Keys.KeyCode; return k == Keys.Up || k == Keys.Down || k == Keys.PageUp || k == Keys.PageDown || k == Keys.Home || k == Keys.End || base.IsInputKey(keyData); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            int page = Math.Max(1, ClientSize.Height / Math.Max(1, RowHeight) - 1), to = selected;
            if (e.KeyCode == Keys.Up) to--; else if (e.KeyCode == Keys.Down) to++; else if (e.KeyCode == Keys.PageUp) to -= page; else if (e.KeyCode == Keys.PageDown) to += page;
            else if (e.KeyCode == Keys.Home) to = 0; else if (e.KeyCode == Keys.End) to = rows.Count - 1;
            else if (e.KeyCode == Keys.Enter && selected >= 0) { Raise(Activated); e.Handled = true; return; } else return;
            if (rows.Count > 0) Select(Math.Max(0, Math.Min(rows.Count - 1, to)), true);
            e.Handled = true;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics; int h = RowHeight;
            using (Brush bg = new SolidBrush(Fill)) g.FillRectangle(bg, ClientRectangle);
            int first = Math.Max(0, (int)(offset / Math.Max(1, h)));
            for (int i = first; i < rows.Count; i++)
            {
                var r = new Rectangle(0, (int)Math.Round(i * h - offset), Width, h);
                if (r.Top > ClientSize.Height) break;
                if (i == selected || i == hover) using (Brush b = new SolidBrush(i == selected ? Palette.Selected : Palette.Raised)) g.FillRectangle(b, r);
                if (details != null && i < details.Count)
                {
                    const TextFormatFlags one = TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
                    int x = r.Left + DpiMetrics.Px(this, 14), w = r.Width - DpiMetrics.Px(this, 32);
                    using (Brush rail = new SolidBrush(i == selected ? Palette.Accent : Palette.Border)) g.FillRectangle(rail, r.Left + DpiMetrics.Px(this, 2), r.Top + DpiMetrics.Px(this, 10), DpiMetrics.Px(this, 3), h - DpiMetrics.Px(this, 20));
                    TextRenderer.DrawText(g, rows[i], Palette.Strong, new Rectangle(x, r.Top + DpiMetrics.Px(this, 6), w, DpiMetrics.Px(this, 22)), Palette.Text, one);
                    TextRenderer.DrawText(g, details[i], Palette.Tiny, new Rectangle(x, r.Top + DpiMetrics.Px(this, 28), w, DpiMetrics.Px(this, 18)), Palette.Muted, one);
                    continue;
                }
                TextRenderer.DrawText(g, rows[i], Palette.Small, new Rectangle(r.Left + DpiMetrics.Px(this, 12), r.Top, r.Width - DpiMetrics.Px(this, 30), h), Palette.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            }
            Rectangle thumb = Thumb();
            if (!thumb.IsEmpty) Glyphs.FillRounded(g, thumbDrag ? Palette.Text : Palette.Muted, thumb, thumb.Width / 2F);
        }
    }
}
