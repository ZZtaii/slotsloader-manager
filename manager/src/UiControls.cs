using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace SlotsInstaller
{
    internal static class DpiMetrics
    {
        internal static int Px(Control control, int logical) { return (int)Math.Round(logical * control.DeviceDpi / 96.0); }
        internal static Rectangle Rect(Control control, int x, int y, int width, int height) { return new Rectangle(Px(control,x),Px(control,y),Px(control,width),Px(control,height)); }
    }
    internal static class Palette
    {
        internal static readonly Color Background = Color.FromArgb(19, 21, 25);
        internal static readonly Color Sidebar = Color.FromArgb(25, 27, 32);
        internal static readonly Color Surface = Color.FromArgb(30, 33, 39);
        internal static readonly Color Raised = Color.FromArgb(39, 42, 49);
        internal static readonly Color Border = Color.FromArgb(53, 57, 65);
        internal static readonly Color Text = Color.FromArgb(236, 237, 240);
        internal static readonly Color Muted = Color.FromArgb(152, 158, 170);
        internal static readonly Color Accent = Color.FromArgb(255, 156, 70);
        internal static readonly Color Selected = Color.FromArgb(43, 47, 55);
        internal static readonly Color Green = Color.FromArgb(134, 207, 166);
        internal static readonly Color Red = Color.FromArgb(244, 132, 133);
        internal static readonly Font Normal = new Font("Segoe UI", 10F);
        internal static readonly Font Small = new Font("Segoe UI", 9F);
        internal static readonly Font Tiny = new Font("Segoe UI", 8F);
        internal static readonly Font Strong = new Font("Segoe UI", 10F, FontStyle.Bold);
    }

    internal sealed class FlatButton : Button
    {
        internal static readonly Color DisabledBack = Color.FromArgb(33, 36, 42), DisabledText = Color.FromArgb(128, 134, 146);
        internal bool Primary;
        internal bool Active;
        internal bool Navigation;
        internal bool Danger;
        private bool emphasis;
        private ButtonGlyph glyph;
        private bool hovering, pressed;
        internal FlatButton(string text, bool primary)
        {
            Text = text;
            Primary = primary;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            FlatAppearance.MouseOverBackColor = Palette.Raised;
            FlatAppearance.MouseDownBackColor = Palette.Selected;
            Font = Palette.Strong;
            Cursor = Cursors.Hand;
            Height = 38;
            Padding = new Padding(8, 0, 8, 0);
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
            UpdateColors();
        }
        // 2.4.1: a small key cap after the label ("Install [Space]"): the button's keyboard shortcut, shown without a
        // tooltip. Drawn in the label colour at reduced strength so it reads as secondary on any button style.
        private string keyHint;
        internal string KeyHint { get { return keyHint; } set { if (keyHint == value) return; keyHint = value; Invalidate(); } }
        internal Rectangle KeyHintBounds;   // tests: where the cap was last painted
        private void PaintWithKeyHint(Graphics g, Color foreground)
        {
            const TextFormatFlags flags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter;
            Font capFont = Palette.Tiny;
            Size text = TextRenderer.MeasureText(g, Text, Font, new Size(Int32.MaxValue, Int32.MaxValue), flags);
            Size cap = TextRenderer.MeasureText(g, keyHint, capFont, new Size(Int32.MaxValue, Int32.MaxValue), flags);
            int padX = DpiMetrics.Px(this, 5), gap = DpiMetrics.Px(this, 8), capH = Math.Min(Height - DpiMetrics.Px(this, 8), cap.Height + DpiMetrics.Px(this, 4));
            int capW = cap.Width + 2 * padX, total = text.Width + gap + capW;
            int x = Math.Max(Padding.Left, (Width - total) / 2), mid = Height / 2;
            TextRenderer.DrawText(g, Text, Font, new Rectangle(x, 0, text.Width + 1, Height), foreground, flags);
            var capRect = new Rectangle(x + text.Width + gap, mid - capH / 2, capW, capH);
            // GDI text ignores alpha: blend the label colour toward the face colour instead.
            Color face = CurrentBackground; float k = Enabled ? 0.68F : 0.55F;
            Color capColor = Color.FromArgb((int)(foreground.R * k + face.R * (1 - k)), (int)(foreground.G * k + face.G * (1 - k)), (int)(foreground.B * k + face.B * (1 - k)));
            Glyphs.DrawRounded(g, capColor, Math.Max(1F, DpiMetrics.Px(this, 1)), new RectangleF(capRect.X + 0.5F, capRect.Y + 0.5F, capRect.Width - 1, capRect.Height - 1), DpiMetrics.Px(this, 3));
            TextRenderer.DrawText(g, keyHint, capFont, capRect, capColor, flags | TextFormatFlags.HorizontalCenter);
            KeyHintBounds = capRect;
        }
        // Next-step hint: an accent outline on an enabled secondary action.
        internal bool Emphasis { get { return emphasis; } set { if (emphasis == value) return; emphasis = value; Invalidate(); } }
        internal ButtonGlyph Glyph { get { return glyph; } set { glyph = value; Invalidate(); } }
        internal bool Hovering { get { return hovering; } }
        internal void SetActive(bool active) { Active = active; UpdateColors(); Invalidate(); }
        private void UpdateColors()
        {
            BackColor = !Enabled ? DisabledBack : Primary ? Palette.Accent : Active ? Palette.Selected : Palette.Raised;
            ForeColor = !Enabled ? DisabledText : Primary ? Palette.Background : Active ? Palette.Accent : Palette.Text;
        }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); if (!Enabled) pressed = false; UpdateColors(); Invalidate(); }
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hovering = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hovering = false; pressed = false; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button == MouseButtons.Left) { pressed = true; Invalidate(); } }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); if (pressed) { pressed = false; Invalidate(); } }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
        internal Color CurrentBackground
        {
            get
            {
                if (!Enabled) return Navigation ? (Parent == null ? Palette.Sidebar : Parent.BackColor) : DisabledBack;
                if (Primary) return pressed ? Color.FromArgb(232, 138, 58) : hovering ? Color.FromArgb(255, 174, 106) : Palette.Accent;
                if (Navigation) return pressed ? Palette.Selected : hovering ? Palette.Raised : (Parent == null ? Palette.Sidebar : Parent.BackColor);
                if (Danger && (hovering || pressed)) return pressed ? Color.FromArgb(58, 34, 37) : Color.FromArgb(70, 40, 43);
                if (pressed) return Color.FromArgb(34, 37, 43);
                if (Active) return hovering ? Palette.Border : Palette.Selected;
                return hovering ? Palette.Border : Palette.Raised;
            }
        }
        internal Color CurrentForeground
        {
            get { return !Enabled ? DisabledText : Primary ? Palette.Background : Active ? Palette.Accent : Danger ? Palette.Red : Palette.Text; }
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            Color outside = Parent == null ? Palette.Background : Parent.BackColor;
            Color background = CurrentBackground;
            using (Brush brush = new SolidBrush(outside)) e.Graphics.FillRectangle(brush, ClientRectangle);
            float radius = DpiMetrics.Px(this, 5);
            var face = new RectangleF(0, 0, Width, Height);
            Glyphs.FillRounded(e.Graphics, background, face, radius);
            if (Emphasis && Enabled && !Primary) Glyphs.DrawRounded(e.Graphics, Color.FromArgb(150, Palette.Accent), Math.Max(1, DpiMetrics.Px(this, 1)), RectangleF.Inflate(face, -0.5F - DpiMetrics.Px(this, 1) / 2F, -0.5F - DpiMetrics.Px(this, 1) / 2F), radius);
            if (Navigation && Active) Glyphs.FillRounded(e.Graphics, Palette.Accent, new RectangleF(DpiMetrics.Px(this, 10), Height - DpiMetrics.Px(this, 3), Width - DpiMetrics.Px(this, 20), DpiMetrics.Px(this, 2)), DpiMetrics.Px(this, 1));
            Color foreground = CurrentForeground;
            if (Glyph != ButtonGlyph.None && String.IsNullOrEmpty(Text))
                Glyphs.Draw(e.Graphics, Glyph, ClientRectangle, Navigation && !Active && !hovering ? Palette.Text : foreground, DeviceDpi);
            else if (!String.IsNullOrEmpty(KeyHint)) PaintWithKeyHint(e.Graphics, foreground);
            else
            {
                TextFormatFlags alignment = TextAlign == ContentAlignment.MiddleLeft ? TextFormatFlags.Left : TextFormatFlags.HorizontalCenter;
                TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle(Padding.Left, DpiMetrics.Px(this, 2), Math.Max(0, Width - Padding.Horizontal), Height - DpiMetrics.Px(this, 4)), foreground, alignment | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }
            if (Focused && ShowFocusCues)
            {
                // Navigation tabs already show an accent underline when active;
                // their keyboard ring is a quieter neutral outline.
                float ring = Navigation ? Math.Max(1F, DpiMetrics.Px(this, 1)) : Math.Max(1.5F, DpiMetrics.Px(this, 2));
                Glyphs.DrawRounded(e.Graphics, Navigation ? Color.FromArgb(120, 126, 138) : Primary ? Palette.Text : Palette.Accent, ring, RectangleF.Inflate(face, -ring / 2 - 0.5F, -ring / 2 - 0.5F), radius);
            }
        }
    }

    internal static class DarkTheme
    {
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] private static extern int SetWindowTheme(IntPtr window, string app, string id);
        internal static void ApplyWindow(IntPtr window) { if (window != IntPtr.Zero) SetWindowTheme(window, "DarkMode_Explorer", null); }
        internal static void Apply(Control control)
        {
            control.HandleCreated += delegate { SetWindowTheme(control.Handle, "DarkMode_Explorer", null); };
            if (control.IsHandleCreated) SetWindowTheme(control.Handle, "DarkMode_Explorer", null);
            foreach (Control child in control.Controls) Apply(child);
        }
    }

    internal sealed class BufferedModList : ListView
    {
        internal int LogicalRowHeight;
        internal bool InputLocked;
        internal bool RepaintAfterScroll;
        internal bool SuppressIdleHover;
        internal Func<Point, bool> BeforePointerSelection;
        private bool fittingColumn;
        internal BufferedModList() { DoubleBuffered = true; }
        [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out NativeRect rect);
        internal void FitSingleColumn(bool rowsChanged = false)
        {
            if (Columns.Count != 1 || fittingColumn || !IsHandleCreated) return;
            // Adding rows can create a native vertical scrollbar without a
            // managed Resize event. Fit after EndUpdate as well as resizing,
            // using the actual native client width. Reset the prior column
            // extent after repopulation: the native list otherwise retains
            // its old scroll range when the requested width is unchanged.
            fittingColumn = true;
            try
            {
                if (rowsChanged) Columns[0].Width = 1;
                NativeRect rect;
                int width = GetClientRect(Handle, out rect) ? rect.Right - rect.Left : ClientSize.Width;
                Columns[0].Width = Math.Max(1, width - DpiMetrics.Px(this, 2));
            }
            finally { fittingColumn = false; }
        }
        protected override void WndProc(ref Message message)
        {
            // Give inline editors a chance to commit before the native list changes selection/focus.
            if (message.Msg == 0x201 && !InputLocked && BeforePointerSelection != null)
            {
                long pos = message.LParam.ToInt64();
                if (BeforePointerSelection(new Point((short)(pos & 0xffff), (short)((pos >> 16) & 0xffff)))) return;
            }
            // The library has no hover action. Keep the native ListView and
            // WinForms mouse-move path from partially repainting custom rows.
            // Button/capture movement still reaches the control for dragging.
            if (SuppressIdleHover && ((message.Msg == 0x200 &&
                (message.WParam.ToInt64() & 0x73) == 0 && !Capture) ||
                message.Msg == 0x2A1 || message.Msg == 0x2A3)) return;
            // Keep the rows painted while background work runs. Disabling the
            // native ListView erased and repainted the entire library on each click.
            if (InputLocked && (message.Msg == 0x201 || message.Msg == 0x203 ||
                message.Msg == 0x204 || message.Msg == 0x206 ||
                message.Msg == 0x100 || message.Msg == 0x102 || message.Msg == 0x104 || message.Msg == 0x7B)) return;
            int kind = message.Msg;
            bool scroll = RepaintAfterScroll && (kind == 0x115 || kind == 0x114 || kind == 0x20A || kind == 0x20E || kind == 0x1014);
            if (RepaintAfterScroll && kind == 0x100)
            {
                Keys key = (Keys)message.WParam.ToInt32();
                scroll = key == Keys.Up || key == Keys.Down || key == Keys.PageUp || key == Keys.PageDown || key == Keys.Home || key == Keys.End;
            }
            base.WndProc(ref message);
            // The owner-drawn library rail is part of every row. A native
            // scroll can move old pixels without calling DrawSubItem again.
            if (scroll && IsHandleCreated && !IsDisposed) Invalidate();
        }
    }

    internal sealed class DarkMenuColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground { get { return Palette.Surface; } }
        public override Color MenuItemSelected { get { return Palette.Raised; } }
        public override Color MenuItemBorder { get { return Palette.Border; } }
        public override Color MenuBorder { get { return Palette.Border; } }
        public override Color ImageMarginGradientBegin { get { return Palette.Surface; } }
        public override Color ImageMarginGradientMiddle { get { return Palette.Surface; } }
        public override Color ImageMarginGradientEnd { get { return Palette.Surface; } }
    }

    internal sealed class DarkCombo : ComboBox
    {
        private bool sizingFace;
        [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct ComboInfo { public int Size; public NativeRect Item, Button; public int State; public IntPtr Combo, Edit, List; }
        [DllImport("user32.dll")] private static extern bool GetComboBoxInfo(IntPtr window, ref ComboInfo info);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
        [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window,int message,IntPtr wParam,IntPtr lParam);
        internal void AlignFieldHeight()
        {
            if(!IsHandleCreated || sizingFace)return;
            sizingFace=true;
            try
            {
                int selectionHeight=SendMessage(Handle,0x154,new IntPtr(-1),IntPtr.Zero).ToInt32();
                if(selectionHeight>0)
                {
                    int chrome=Math.Max(0,Height-selectionHeight);
                    SendMessage(Handle,0x153,new IntPtr(-1),new IntPtr(Math.Max(DpiMetrics.Px(this,22),DpiMetrics.Px(this,30)-chrome)));
                }
            }
            finally {sizingFace=false;}
        }
        internal void CaptureDropdown(string file)
        {
            DroppedDown = true; Application.DoEvents();
            try
            {
                ComboInfo info = new ComboInfo { Size = Marshal.SizeOf(typeof(ComboInfo)) }; NativeRect rect;
                if (!GetComboBoxInfo(Handle, ref info) || !GetWindowRect(info.List, out rect)) throw new InvalidOperationException("Cannot inspect dropdown window.");
                using (Bitmap bitmap = new Bitmap(rect.Right - rect.Left, rect.Bottom - rect.Top))
                using (Graphics graphics = Graphics.FromImage(bitmap))
                {
                    IntPtr dc = graphics.GetHdc();
                    try { if (!PrintWindow(info.List, dc, 0)) throw new InvalidOperationException("Dropdown render failed."); }
                    finally { graphics.ReleaseHdc(dc); }
                    bitmap.Save(file, ImageFormat.Png);
                }
            }
            finally { DroppedDown = false; }
        }
        internal DarkCombo()
        {
            DrawMode = DrawMode.OwnerDrawFixed; ItemHeight = 22;
            DrawItem += delegate(object sender, DrawItemEventArgs e)
            {
                using (Brush fill = new SolidBrush((e.State & DrawItemState.Selected) != 0 ? Palette.Selected : Palette.Raised)) e.Graphics.FillRectangle(fill, e.Bounds);
                if (e.Index >= 0) TextRenderer.DrawText(e.Graphics, GetItemText(Items[e.Index]), Font, Rectangle.Inflate(e.Bounds, -DpiMetrics.Px(this,5), 0), Palette.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                if ((e.State & DrawItemState.Focus) != 0) e.DrawFocusRectangle();
            };
        }
        protected override void OnDropDown(EventArgs e)
        {
            ComboInfo info = new ComboInfo { Size = Marshal.SizeOf(typeof(ComboInfo)) };
            if (GetComboBoxInfo(Handle, ref info)) DarkTheme.ApplyWindow(info.List);
            base.OnDropDown(e);
        }
        private bool hovering;
        protected override void OnMouseEnter(EventArgs e) {base.OnMouseEnter(e);hovering=true;Invalidate();}
        protected override void OnMouseLeave(EventArgs e) {base.OnMouseLeave(e);hovering=false;Invalidate();}
        protected override void OnGotFocus(EventArgs e) {base.OnGotFocus(e);Invalidate();}
        protected override void OnLostFocus(EventArgs e) {base.OnLostFocus(e);Invalidate();}
        protected override void OnDpiChangedAfterParent(EventArgs e) {base.OnDpiChangedAfterParent(e);ItemHeight=DpiMetrics.Px(this,22);AlignFieldHeight();}
        protected override void OnHandleCreated(EventArgs e) {base.OnHandleCreated(e);ItemHeight=DpiMetrics.Px(this,22);AlignFieldHeight();}
        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if ((m.Msg == 15 || m.Msg == 0x318 || m.Msg == 0x317) && DropDownStyle == ComboBoxStyle.DropDownList)
            {
                using (Graphics g = m.Msg == 15 ? CreateGraphics() : Graphics.FromHdc(m.WParam))
                {
                    using (Brush outside = new SolidBrush(Parent == null ? Palette.Surface : Parent.BackColor)) g.FillRectangle(outside, ClientRectangle);
                    float radius = DpiMetrics.Px(this, 4);
                    var face = new RectangleF(0.5F, 0.5F, Width - 1F, Height - 1F);
                    Glyphs.FillRounded(g, Palette.Raised, face, radius);
                    Glyphs.DrawRounded(g, Focused ? Palette.Accent : hovering ? Color.FromArgb(72, 77, 87) : Palette.Border, 1F, face, radius);
                    TextRenderer.DrawText(g, Text, Font, new Rectangle(DpiMetrics.Px(this,7),0,Math.Max(1,Width-DpiMetrics.Px(this,29)),Height), Enabled ? Palette.Text : Palette.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                    Glyphs.Chevron(g, new PointF(Width - DpiMetrics.Px(this, 13), Height / 2F), DpiMetrics.Px(this, 4), ChevronDirection.Down, Enabled ? Palette.Muted : Palette.Border, 1.5F * DeviceDpi / 96F);
                }
            }
        }
    }

    // 2.3.0: a joined two-way toggle (INSTALL AS). One rounded outline around both halves; the selected half is filled
    // with the accent colour, dark text and a check mark; Left/Right (or Space) switch; radio-group accessibility.
    // While Locked (a forced choice) the other half is dimmed and clicks or keys do nothing.
    internal sealed class SegmentedToggle : Control
    {
        internal event EventHandler SelectionChanged;   // raised for user changes only
        private readonly string[] options;
        private int selected, hover = -1;
        private bool locked;
        internal SegmentedToggle(string first, string second)
        {
            options = new[] { first, second };
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            TabStop = true; Font = Palette.Small; Cursor = Cursors.Hand; Height = 32;
        }
        internal string[] Options { get { return (string[])options.Clone(); } }
        internal readonly string[] Descriptions = { "", "" };
        internal int SelectedIndex { get { return selected; } set { int v = Math.Max(0, Math.Min(1, value)); if (v == selected) return; selected = v; Invalidate(); AccessibilityNotifyClients(AccessibleEvents.StateChange, 0); AccessibilityNotifyClients(AccessibleEvents.StateChange, 1); } }
        internal bool Locked { get { return locked; } set { if (locked == value) return; locked = value; Cursor = value ? Cursors.Default : Cursors.Hand; Invalidate(); } }
        internal Rectangle SegmentBounds(int index) { int half = Width / 2; return index == 0 ? new Rectangle(0, 0, half, Height) : new Rectangle(half, 0, Width - half, Height); }
        internal void Choose(int index)
        {
            if (locked || index == selected || index < 0 || index > 1) return;
            SelectedIndex = index;
            var handler = SelectionChanged; if (handler != null) handler(this, EventArgs.Empty);
        }
        private int Px(int logical) { return DpiMetrics.Px(this, logical); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (CanFocus) Focus(); if (e.Button == MouseButtons.Left) Choose(e.X < Width / 2 ? 0 : 1); }
        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); int h = e.X < Width / 2 ? 0 : 1; if (h != hover) { hover = h; Invalidate(); } }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = -1; Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
        protected override bool IsInputKey(Keys keyData) { Keys k = keyData & Keys.KeyCode; return k == Keys.Left || k == Keys.Right || k == Keys.Up || k == Keys.Down || base.IsInputKey(keyData); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Left || e.KeyCode == Keys.Up) { Choose(0); e.Handled = true; }
            else if (e.KeyCode == Keys.Right || e.KeyCode == Keys.Down) { Choose(1); e.Handled = true; }
            else if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) { Choose(1 - selected); e.Handled = e.SuppressKeyPress = true; }
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            using (Brush outside = new SolidBrush(Parent == null ? Palette.Surface : Parent.BackColor)) g.FillRectangle(outside, ClientRectangle);
            float radius = Px(5), line = Math.Max(1F, Px(1));
            var face = new RectangleF(line / 2, line / 2, Width - line, Height - line);
            Glyphs.FillRounded(g, Palette.Raised, face, radius);
            Rectangle on = SegmentBounds(selected);
            using (GraphicsPath outline = Glyphs.Rounded(face, radius))
            {
                GraphicsState state = g.Save();
                g.SetClip(outline, CombineMode.Intersect);
                if (hover >= 0 && hover != selected && !locked) using (Brush b = new SolidBrush(Palette.Border)) g.FillRectangle(b, SegmentBounds(hover));
                using (Brush b = new SolidBrush(Palette.Accent)) g.FillRectangle(b, on);
                g.Restore(state);
            }
            Glyphs.DrawRounded(g, Focused && ShowFocusCues ? Palette.Text : Palette.Border, Focused && ShowFocusCues ? Math.Max(1.5F, Px(2)) : line, face, radius);
            for (int i = 0; i < 2; i++)
            {
                Rectangle r = SegmentBounds(i);
                bool isOn = i == selected;
                Color fore = isOn ? Palette.Background : locked ? FlatButton.DisabledText : Palette.Text;
                Size text = TextRenderer.MeasureText(g, options[i], Font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                int mark = isOn ? Px(14) : 0;
                int width = Math.Min(r.Width - Px(8), text.Width + mark);
                int x = r.Left + (r.Width - width) / 2;
                if (isOn)
                {
                    // Check mark on the selected side.
                    int cy = r.Top + r.Height / 2, s = Px(4);
                    using (var pen = new Pen(fore, Math.Max(1.5F, Px(2))) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                        g.DrawLines(pen, new[] { new Point(x, cy), new Point(x + s, cy + s), new Point(x + Px(10), cy - s) });
                }
                TextRenderer.DrawText(g, options[i], Font, new Rectangle(x + mark, r.Top, Math.Max(1, r.Right - x - mark - Px(2)), r.Height), fore, TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }
        }
        protected override AccessibleObject CreateAccessibilityInstance() { return new GroupAccessible(this); }
        private sealed class GroupAccessible : ControlAccessibleObject
        {
            private readonly SegmentedToggle owner;
            internal GroupAccessible(SegmentedToggle owner) : base(owner) { this.owner = owner; }
            public override AccessibleRole Role { get { return AccessibleRole.Grouping; } }
            public override int GetChildCount() { return 2; }
            public override AccessibleObject GetChild(int index) { return index == 0 || index == 1 ? new OptionAccessible(owner, index, this) : null; }
            public override AccessibleObject GetFocused() { return owner.Focused ? GetChild(owner.selected) : base.GetFocused(); }
        }
        internal sealed class OptionAccessible : AccessibleObject
        {
            private readonly SegmentedToggle owner; private readonly int index; private readonly AccessibleObject parent;
            internal OptionAccessible(SegmentedToggle owner, int index, AccessibleObject parent) { this.owner = owner; this.index = index; this.parent = parent; }
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
                    if (owner.locked && owner.selected != index) s |= AccessibleStates.Unavailable;
                    return s;
                }
            }
            public override Rectangle Bounds { get { return owner.RectangleToScreen(owner.SegmentBounds(index)); } }
        }
    }
}
