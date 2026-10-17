using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SlotsInstaller
{
    // 2.4.1: the INSTALL AS segmented style with any number of equal segments (the presets window's
    // Extra outfits · Replacements · Both). SegmentedToggle stays the two-way control it is.
    internal sealed class SegmentedChoice : Control
    {
        internal event EventHandler SelectionChanged;   // raised for user changes only
        private readonly string[] options;
        private int selected, hover = -1;
        private bool locked;
        internal readonly string[] Descriptions;
        internal SegmentedChoice(params string[] options)
        {
            if (options == null || options.Length < 2) throw new ArgumentException("A segmented choice needs at least two options.");
            this.options = (string[])options.Clone(); Descriptions = new string[options.Length];
            for (int i = 0; i < Descriptions.Length; i++) Descriptions[i] = "";
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            TabStop = true; Font = Palette.Small; Cursor = Cursors.Hand; Height = 32;
        }
        internal string[] Options { get { return (string[])options.Clone(); } }
        internal int Count { get { return options.Length; } }
        internal int SelectedIndex
        {
            get { return selected; }
            set { int v = Math.Max(0, Math.Min(options.Length - 1, value)); if (v == selected) return; selected = v; Invalidate(); for (int i = 0; i < options.Length; i++) AccessibilityNotifyClients(AccessibleEvents.StateChange, i); }
        }
        internal bool Locked { get { return locked; } set { if (locked == value) return; locked = value; Cursor = value ? Cursors.Default : Cursors.Hand; Invalidate(); } }
        internal Rectangle SegmentBounds(int index)
        {
            int left = Width * index / options.Length, right = Width * (index + 1) / options.Length;
            return new Rectangle(left, 0, right - left, Height);
        }
        private int IndexAt(int x) { return Math.Max(0, Math.Min(options.Length - 1, Width <= 0 ? 0 : x * options.Length / Width)); }
        internal void Choose(int index)
        {
            if (locked || index == selected || index < 0 || index >= options.Length) return;
            SelectedIndex = index;
            var handler = SelectionChanged; if (handler != null) handler(this, EventArgs.Empty);
        }
        private int Px(int logical) { return DpiMetrics.Px(this, logical); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (CanFocus) Focus(); if (e.Button == MouseButtons.Left) Choose(IndexAt(e.X)); }
        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); int h = IndexAt(e.X); if (h != hover) { hover = h; Invalidate(); } }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = -1; Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
        protected override bool IsInputKey(Keys keyData) { Keys k = keyData & Keys.KeyCode; return k == Keys.Left || k == Keys.Right || k == Keys.Up || k == Keys.Down || base.IsInputKey(keyData); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Left || e.KeyCode == Keys.Up) { Choose(selected - 1); e.Handled = true; }
            else if (e.KeyCode == Keys.Right || e.KeyCode == Keys.Down) { Choose(selected + 1); e.Handled = true; }
            else if (e.KeyCode == Keys.Home) { Choose(0); e.Handled = true; }
            else if (e.KeyCode == Keys.End) { Choose(options.Length - 1); e.Handled = true; }
            else if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) { Choose((selected + 1) % options.Length); e.Handled = e.SuppressKeyPress = true; }
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            bool inactive = locked || !Enabled;
            using (Brush outside = new SolidBrush(Parent == null ? Palette.Surface : Parent.BackColor)) g.FillRectangle(outside, ClientRectangle);
            float radius = Px(5), line = Math.Max(1F, Px(1));
            var face = new RectangleF(line / 2, line / 2, Width - line, Height - line);
            Glyphs.FillRounded(g, Palette.Raised, face, radius);
            using (GraphicsPath outline = Glyphs.Rounded(face, radius))
            {
                GraphicsState state = g.Save();
                g.SetClip(outline, CombineMode.Intersect);
                if (hover >= 0 && hover != selected && !inactive) using (Brush b = new SolidBrush(Palette.Border)) g.FillRectangle(b, SegmentBounds(hover));
                using (Brush b = new SolidBrush(inactive && !Enabled ? FlatButton.DisabledBack : Palette.Accent)) g.FillRectangle(b, SegmentBounds(selected));
                using (var pen = new Pen(Palette.Border, line)) for (int i = 1; i < options.Length; i++) if (i != selected && i - 1 != selected) { int x = SegmentBounds(i).Left; g.DrawLine(pen, x, Px(6), x, Height - Px(6)); }
                g.Restore(state);
            }
            Glyphs.DrawRounded(g, Focused && ShowFocusCues ? Palette.Text : Palette.Border, Focused && ShowFocusCues ? Math.Max(1.5F, Px(2)) : line, face, radius);
            for (int i = 0; i < options.Length; i++)
            {
                Rectangle r = SegmentBounds(i);
                bool isOn = i == selected;
                Color fore = isOn ? (Enabled ? Palette.Background : FlatButton.DisabledText) : inactive ? FlatButton.DisabledText : Palette.Text;
                Size text = TextRenderer.MeasureText(g, options[i], Font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                int mark = isOn ? Px(14) : 0;
                int width = Math.Min(r.Width - Px(8), text.Width + mark);
                int x = r.Left + (r.Width - width) / 2;
                if (isOn)
                {
                    int cy = r.Top + r.Height / 2, s = Px(4);
                    using (var pen = new Pen(fore, Math.Max(1.5F, Px(2))) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                        g.DrawLines(pen, new[] { new Point(x, cy), new Point(x + s, cy + s), new Point(x + Px(10), cy - s) });
                }
                TextRenderer.DrawText(g, options[i], Font, new Rectangle(x + mark, r.Top, Math.Max(1, r.Right - x - mark - Px(2)), r.Height), fore, TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }
        }
        // Tests: the widest segment label fits its segment (with the check mark).
        internal bool LabelsFit()
        {
            using (Graphics g = CreateGraphics())
                for (int i = 0; i < options.Length; i++)
                    if (TextRenderer.MeasureText(g, options[i], Font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width + Px(14) > SegmentBounds(i).Width - Px(8)) return false;
            return true;
        }
        protected override AccessibleObject CreateAccessibilityInstance() { return new GroupAccessible(this); }
        private sealed class GroupAccessible : ControlAccessibleObject
        {
            private readonly SegmentedChoice owner;
            internal GroupAccessible(SegmentedChoice owner) : base(owner) { this.owner = owner; }
            public override AccessibleRole Role { get { return AccessibleRole.Grouping; } }
            public override int GetChildCount() { return owner.options.Length; }
            public override AccessibleObject GetChild(int index) { return index >= 0 && index < owner.options.Length ? new OptionAccessible(owner, index, this) : null; }
            public override AccessibleObject GetFocused() { return owner.Focused ? GetChild(owner.selected) : base.GetFocused(); }
        }
        internal sealed class OptionAccessible : AccessibleObject
        {
            private readonly SegmentedChoice owner; private readonly int index; private readonly AccessibleObject parent;
            internal OptionAccessible(SegmentedChoice owner, int index, AccessibleObject parent) { this.owner = owner; this.index = index; this.parent = parent; }
            public override string Name { get { return owner.options[index]; } }
            public override string Description { get { return owner.Descriptions[index]; } }
            public override AccessibleRole Role { get { return AccessibleRole.RadioButton; } }
            public override AccessibleObject Parent { get { return parent; } }
            public override string DefaultAction { get { return "Select"; } }
            public override void DoDefaultAction() { owner.Choose(index); }
            public override AccessibleStates State
            {
                get
                {
                    AccessibleStates s = AccessibleStates.Focusable;
                    if (owner.selected == index) s |= AccessibleStates.Checked;
                    if (owner.Focused && owner.selected == index) s |= AccessibleStates.Focused;
                    if ((owner.locked || !owner.Enabled) && owner.selected != index) s |= AccessibleStates.Unavailable;
                    return s;
                }
            }
            public override Rectangle Bounds { get { return owner.RectangleToScreen(owner.SegmentBounds(index)); } }
        }
    }
}
