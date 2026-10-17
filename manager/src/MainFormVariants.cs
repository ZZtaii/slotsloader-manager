using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace SlotsInstaller
{
    // 2.5.0: the variants of a manual archive/folder as one buffered check list (the option list's marks).
    // A click or Space/Enter on a row raises Toggled; the form writes variant_choices.json and reports inline.
    internal sealed class VariantList : ScrollableControl
    {
        internal sealed class Row { internal string Label = "", Note = ""; internal bool On, NoteAccent; }
        private List<Row> rows = new List<Row>();
        private int focus = -1, hover = -1;
        internal event Action<int> Toggled;
        internal IList<Row> Rows { get { return rows.AsReadOnly(); } }
        internal int RowHeight { get { return DpiMetrics.Px(this, 30); } }

        internal VariantList()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            BackColor = Palette.Surface; ForeColor = Palette.Text; Font = Palette.Small; TabStop = true; AutoScroll = true;
            AccessibleRole = AccessibleRole.List; AccessibleName = "Variants";
        }
        internal void SetRows(IEnumerable<Row> values)
        {
            rows = values.ToList();
            if (focus >= rows.Count) focus = rows.Count - 1;
            hover = -1; AutoScrollMinSize = new Size(0, rows.Count * RowHeight); Invalidate();
            if (IsHandleCreated) AccessibilityNotifyClients(AccessibleEvents.Reorder, -1);
        }
        internal Rectangle RowBounds(int index) { return new Rectangle(0, index * RowHeight + AutoScrollPosition.Y, ClientSize.Width, RowHeight); }
        private int RowAt(Point p) { int i = (p.Y - AutoScrollPosition.Y) / Math.Max(1, RowHeight); return p.Y >= 0 && i >= 0 && i < rows.Count ? i : -1; }
        // Tests and accessibility: the same path as a click.
        internal void Toggle(int index) { if (index < 0 || index >= rows.Count || !Enabled) return; focus = index; Invalidate(); if (Toggled != null) Toggled(index); }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            int h = RowHeight, px = DpiMetrics.Px(this, 1);
            for (int i = 0; i < rows.Count; i++)
            {
                Rectangle row = RowBounds(i); if (row.Bottom < 0 || row.Top > ClientSize.Height) continue;
                Row r = rows[i];
                if (i == hover) using (Brush bg = new SolidBrush(Palette.Raised)) e.Graphics.FillRectangle(bg, row);
                Rectangle mark = new Rectangle(row.X + 4 * px, row.Y + (h - 13 * px) / 2, 13 * px, 13 * px);
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (Pen pen = new Pen(r.On ? Palette.Accent : i == hover ? Palette.Text : Palette.Muted, 1.4F * DeviceDpi / 96F))
                using (GraphicsPath box = Glyphs.Rounded(mark, 3 * px)) e.Graphics.DrawPath(pen, box);
                if (r.On) Glyphs.FillRounded(e.Graphics, Palette.Accent, Rectangle.Inflate(mark, -3 * px, -3 * px), px);
                int noteWidth = r.Note.Length == 0 ? 0 : Math.Min(row.Width / 2, TextRenderer.MeasureText(e.Graphics, r.Note, Font, new Size(int.MaxValue, h), TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine).Width + 4 * px);
                int x = mark.Right + 10 * px;
                TextRenderer.DrawText(e.Graphics, r.Label, Font, new Rectangle(x, row.Y, Math.Max(1, row.Right - x - noteWidth - 8 * px), h), r.On ? Palette.Text : Palette.Muted, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);
                if (noteWidth > 0) TextRenderer.DrawText(e.Graphics, r.Note, Font, new Rectangle(row.Right - noteWidth - 4 * px, row.Y, noteWidth, h), r.NoteAccent ? Palette.Accent : Palette.Muted, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
                if (i == focus && Focused) Glyphs.DrawRounded(e.Graphics, Color.FromArgb(170, Palette.Accent), Math.Max(1F, DeviceDpi / 96F), new RectangleF(row.X + px, row.Y + px, Math.Max(1, row.Width - 2 * px), h - 2 * px), 4 * px);
            }
        }
        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); int at = RowAt(e.Location); if (at != hover) { hover = at; Cursor = at >= 0 ? Cursors.Hand : Cursors.Default; Invalidate(); } }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (hover >= 0) { hover = -1; Invalidate(); } }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); Focus(); if (e.Button == MouseButtons.Left && e.Clicks == 1) Toggle(RowAt(e.Location)); }
        protected override bool IsInputKey(Keys keyData) { Keys k = keyData & Keys.KeyCode; return k == Keys.Up || k == Keys.Down || k == Keys.Space || k == Keys.Enter || base.IsInputKey(keyData); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (rows.Count == 0) return;
            if (e.KeyCode == Keys.Up || e.KeyCode == Keys.Down)
            {
                focus = Math.Max(0, Math.Min(rows.Count - 1, focus + (e.KeyCode == Keys.Up ? -1 : 1)));
                Rectangle r = RowBounds(focus);
                if (r.Top < 0) AutoScrollPosition = new Point(0, focus * RowHeight); else if (r.Bottom > ClientSize.Height) AutoScrollPosition = new Point(0, (focus + 1) * RowHeight - ClientSize.Height);
                Invalidate(); e.Handled = true;
            }
            else if ((e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) && e.Modifiers == Keys.None) { Toggle(Math.Max(0, focus)); e.Handled = true; e.SuppressKeyPress = true; }
        }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); if (focus < 0 && rows.Count > 0) focus = 0; Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
        protected override void OnScroll(ScrollEventArgs se) { base.OnScroll(se); Invalidate(); }
        protected override void OnMouseWheel(MouseEventArgs e) { base.OnMouseWheel(e); Invalidate(); }
        protected override AccessibleObject CreateAccessibilityInstance() { return new ListAccessibility(this); }
        private sealed class ListAccessibility : ControlAccessibleObject
        {
            private readonly VariantList list;
            internal ListAccessibility(VariantList list) : base(list) { this.list = list; }
            public override int GetChildCount() { return list.rows.Count; }
            public override AccessibleObject GetChild(int index) { return index >= 0 && index < list.rows.Count ? new RowAccessibility(list, index) : null; }
        }
        private sealed class RowAccessibility : AccessibleObject
        {
            private readonly VariantList list; private readonly int index;
            internal RowAccessibility(VariantList list, int index) { this.list = list; this.index = index; }
            public override string Name { get { Row r = list.rows[index]; return r.Label + (r.Note.Length > 0 ? ", " + r.Note : ""); } }
            public override AccessibleRole Role { get { return AccessibleRole.CheckButton; } }
            public override AccessibleStates State { get { return AccessibleStates.Focusable | (list.rows[index].On ? AccessibleStates.Checked : AccessibleStates.None); } }
            public override Rectangle Bounds { get { return list.RectangleToScreen(list.RowBounds(index)); } }
            public override AccessibleObject Parent { get { return list.AccessibilityObject; } }
            public override string DefaultAction { get { return list.rows[index].On ? "Turn off" : "Turn on"; } }
            public override void DoDefaultAction() { list.Toggle(index); }
        }
    }

    internal sealed partial class MainForm
    {
        private Panel variantsPanel;
        private Label variantsCaption, variantsNote;
        private VariantList variantList;
        private InstalledOutfit variantsOutfit;
        private List<VariantState> variantStates = new List<VariantState>();
        internal const int VariantRowsShown = 6;   // more rows scroll inside the list
        internal const string VariantsTooOld = "Variant on/off needs a newer Slotsloader.";

        // Installed page inspector, between the actions and the ABOUT card.
        private Panel BuildVariantsPanel()
        {
            variantsPanel = new Panel { Dock = DockStyle.Top, Height = 34 + 30, Visible = false, Padding = Padding.Empty };
            variantsCaption = Caption("VARIANTS"); variantsCaption.Dock = DockStyle.Top; variantsCaption.Height = 34; variantsCaption.Padding = new Padding(0, 10, 0, 0); variantsCaption.AutoEllipsis = true;
            variantList = new VariantList { Dock = DockStyle.Fill };
            variantList.Toggled += ToggleVariant;
            variantsNote = new Label { UseMnemonic = false, Dock = DockStyle.Fill, ForeColor = Palette.Muted, Font = Palette.Small, AutoEllipsis = true, Text = VariantsTooOld, Visible = false };
            variantsPanel.Controls.Add(variantList); variantsPanel.Controls.Add(variantsNote); variantsPanel.Controls.Add(variantsCaption);
            return variantsPanel;
        }

        // A manual archive/folder SlotsLoader split into several variants: one row each (on/off, the slot a built one got).
        private void ShowVariants(InstalledOutfit outfit)
        {
            if (variantsPanel == null) return;
            variantsOutfit = outfit != null && (outfit.Variants != null || outfit.VariantsUnsupported) ? outfit : null;
            variantsPanel.Visible = variantsOutfit != null;
            if (variantsOutfit == null) { variantStates = new List<VariantState>(); variantList.SetRows(new VariantList.Row[0]); return; }
            if (outfit.Variants == null)
            {
                variantsCaption.Text = "VARIANTS"; variantList.Visible = false; variantsNote.Visible = true;
                variantsPanel.Height = Px(34) + Px(24);
                return;
            }
            variantStates = VariantChoices.States(outfit.Variants, VariantChoices.ReadChoices(config.CostumeRoot));
            variantsCaption.Text = "VARIANTS · " + variantStates.Count(v => v.On) + " OF " + variantStates.Count + " ON";
            variantList.SetRows(variantStates.Select(v => new VariantList.Row { Label = v.Label, On = v.On, Note = VariantChoices.Note(v), NoteAccent = v.Pending }));
            variantList.Visible = true; variantsNote.Visible = false; variantList.Enabled = !busy;
            variantsPanel.Height = Px(34) + Math.Min(variantStates.Count, VariantRowsShown) * variantList.RowHeight;
        }

        // On click: written at once (also while SF6 runs; SlotsLoader reads it at the next launch), reported inline with Undo.
        private void ToggleVariant(int index)
        {
            InstalledOutfit outfit = variantsOutfit;
            if (busy || outfit == null || outfit.Variants == null || index < 0 || index >= variantStates.Count) return;
            string root = config.CostumeRoot, source = outfit.Variants.Source, label = variantStates[index].Label;
            bool on = !variantStates[index].On;
            Dictionary<string, bool> before;
            try
            {
                before = VariantChoices.Entry(root, source);
                VariantChoices.Set(root, outfit.Variants, label, on);
            }
            catch (Exception ex) { Program.Log(ex); SetPageStatus(installedPage, "Couldn't change " + label + ": " + ex.Message, Palette.Red); ShowVariants(outfit); return; }
            bool running = false;
            try { running = GameProcesses.Probe.IsGameRunning(); } catch (Exception ex) { Program.Log(ex); }
            string done = label + (on ? " on" : " off");
            ShowVariants(outfit);
            SetPageStatus(installedPage, done + " · " + VariantsCommandLine.AppliesText(running), Palette.Green);
            OfferUndo(done, delegate
            {
                try { VariantChoices.Restore(root, source, before); }
                catch (Exception ex) { Program.Log(ex); SetPageStatus(installedPage, "Couldn't undo: " + ex.Message, Palette.Red); return; }
                if (variantsOutfit == outfit) ShowVariants(outfit);
                SetPageStatus(installedPage, "✓ Undone: " + done + ".", Palette.Green);
            });
        }
        internal VariantList VariantListForTest { get { return variantList; } }
    }
}
