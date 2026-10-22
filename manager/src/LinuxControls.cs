using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SlotsInstaller
{
    // Wine does not implement the Windows DarkMode_Explorer theme. Repaint the
    // native chrome; its scroll range, hit testing, keyboard and accessibility stay native.
    internal static class LinuxControls
    {
        private static readonly ConditionalWeakTable<Control, NativeSkin> controls = new ConditionalWeakTable<Control, NativeSkin>();
        private static readonly Dictionary<IntPtr, NativeSkin> popups = new Dictionary<IntPtr, NativeSkin>();

        internal static void Apply(Control control)
        {
            if (!LinuxPlatform.IsLinux || control == null || control.IsDisposed) return;
            controls.GetValue(control, delegate(Control item) { return new NativeSkin(item); });
            foreach (Control child in control.Controls) Apply(child);
        }

        internal static void ApplyWindow(IntPtr window)
        {
            if (!LinuxPlatform.IsLinux || window == IntPtr.Zero || popups.ContainsKey(window)) return;
            popups[window] = new NativeSkin(window);
        }

        [StructLayout(LayoutKind.Sequential)] internal struct Rect
        {
            internal int Left, Top, Right, Bottom;
            internal Rectangle Rectangle { get { return Rectangle.FromLTRB(Left, Top, Right, Bottom); } }
        }
        [StructLayout(LayoutKind.Sequential)] internal struct ScrollInfo
        {
            internal int Size;
            internal Rect Bounds;
            internal int ThumbSize, ThumbStart, ThumbEnd, Reserved;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)] internal int[] State;
        }
        [DllImport("user32.dll")] private static extern bool GetScrollBarInfo(IntPtr window, int objectId, ref ScrollInfo info);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
        [DllImport("user32.dll")] private static extern IntPtr GetWindowDC(IntPtr window);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
        [StructLayout(LayoutKind.Sequential)] private struct MouseTracking
        { internal int Size, Flags; internal IntPtr Window; internal int HoverTime; }
        [DllImport("user32.dll")] private static extern bool TrackMouseEvent(ref MouseTracking tracking);

        internal static bool Scrollbar(IntPtr window, bool vertical, out ScrollInfo info)
        {
            info = new ScrollInfo { Size = Marshal.SizeOf(typeof(ScrollInfo)), State = new int[6] };
            return GetScrollBarInfo(window, vertical ? -5 : -6, ref info) && (info.State[0] & 0x18000) == 0 && info.Bounds.Right > info.Bounds.Left && info.Bounds.Bottom > info.Bounds.Top;
        }

        internal static int ArrowExtent(ScrollInfo info, bool vertical)
        {
            int length = vertical ? info.Bounds.Bottom - info.Bounds.Top : info.Bounds.Right - info.Bounds.Left;
            // SCROLLBARINFO.dxyLineButton is the thumb length. Wine's native arrows
            // use SM_CXVSCROLL in both directions, preserving four pixels in tiny bars.
            return Math.Min(GetSystemMetrics(2), Math.Max(0, (length - 4) / 2));
        }

        internal static void CheckBoxLayout(CheckBox box, out Rectangle mark, out Rectangle text)
        {
            Rectangle area = box.ClientRectangle;
            area = Rectangle.FromLTRB(area.Left + box.Padding.Left, area.Top + box.Padding.Top,
                Math.Max(area.Left + box.Padding.Left, area.Right - box.Padding.Right), Math.Max(area.Top + box.Padding.Top, area.Bottom - box.Padding.Bottom));
            int glyph;
            using (Graphics graphics = box.CreateGraphics()) glyph = CheckBoxRenderer.GetGlyphSize(graphics, System.Windows.Forms.VisualStyles.CheckBoxState.UncheckedNormal).Width;
            int size = Math.Min(glyph, Math.Max(0, area.Height - 2)), gap = 3;
            bool right = box.CheckAlign == ContentAlignment.TopRight || box.CheckAlign == ContentAlignment.MiddleRight || box.CheckAlign == ContentAlignment.BottomRight;
            if (box.RightToLeft == RightToLeft.Yes) right = !right;
            int y = box.CheckAlign == ContentAlignment.TopLeft || box.CheckAlign == ContentAlignment.TopRight ? area.Top + 1 : box.CheckAlign == ContentAlignment.BottomLeft || box.CheckAlign == ContentAlignment.BottomRight ? area.Bottom - size - 1 : area.Top + (area.Height - size) / 2;
            mark = new Rectangle(right ? area.Right - size - 1 : area.Left + 1, y, size, size);
            text = new Rectangle(right ? area.Left : mark.Right + gap, area.Top, Math.Max(0, area.Width - size - gap - 2), area.Height);
        }

        private sealed class NativeSkin : NativeWindow
        {
            private readonly Control control;
            private readonly Timer tracking = new Timer { Interval = 30 };
            private bool painting, disposed;
            internal NativeSkin(Control owner)
            {
                control = owner;
                tracking.Tick += delegate { PaintChrome(IntPtr.Zero); };
                owner.HandleCreated += HandleCreated;
                owner.HandleDestroyed += delegate { if (Handle != IntPtr.Zero) ReleaseHandle(); tracking.Stop(); };
                owner.ControlAdded += delegate(object sender, ControlEventArgs e) { Apply(e.Control); };
                owner.Disposed += delegate { Dispose(); };
                if (owner.IsHandleCreated) HandleCreated(owner, EventArgs.Empty);
            }
            internal NativeSkin(IntPtr window)
            {
                tracking.Tick += delegate { PaintChrome(IntPtr.Zero); };
                AssignHandle(window);
            }
            private void HandleCreated(object sender, EventArgs e)
            {
                if (!disposed && Handle == IntPtr.Zero) AssignHandle(control.Handle);
            }
            private void Dispose()
            {
                if (disposed) return;
                disposed = true; tracking.Stop(); tracking.Dispose();
                if (Handle != IntPtr.Zero) { popups.Remove(Handle); ReleaseHandle(); }
            }
            protected override void WndProc(ref Message message)
            {
                int kind = message.Msg;
                IntPtr window = Handle;
                // Native scrollbar tracking runs a nested message loop. Repaint during it,
                // including held arrows and thumb dragging, without handling its input.
                bool track = kind == 0xA1 && (message.WParam.ToInt32() == 6 || message.WParam.ToInt32() == 7);
                if (track) tracking.Start();
                try { base.WndProc(ref message); }
                finally { if (track && !disposed) tracking.Stop(); }
                if (kind == 0x82)
                {
                    tracking.Stop();
                    if (control == null) { popups.Remove(window); Dispose(); }
                    return;
                }
                if (disposed || Handle == IntPtr.Zero) return;
                if (kind == 0xA0)
                { var leave = new MouseTracking { Size = Marshal.SizeOf(typeof(MouseTracking)), Flags = 0x12, Window = Handle }; TrackMouseEvent(ref leave); }
                if (kind == 0xF || kind == 0x85 || kind == 0x86 || kind == 0x5 || kind == 0x47 || kind == 0x7D ||
                    kind == 0xA0 || kind == 0xA1 || kind == 0xA2 || kind == 0x2A2 || kind == 0x115 || kind == 0x114 ||
                    kind == 0x20A || kind == 0x20E || kind == 0x100 || kind == 0x101 || kind == 0x1013 || kind == 0x1014 || kind == 7 || kind == 8 || kind == 0xA)
                {
                    PaintChrome(IntPtr.Zero);
                }
                else if (kind == 0x317) { PaintChrome(message.WParam); PaintCheckBox(message.WParam); }
                else if (kind == 0x318) PaintCheckBox(message.WParam);
                if (kind == 0xF || kind == 7 || kind == 8 || kind == 0xA || kind == 0xF1 || kind == 0x128 || kind == 0x200 || kind == 0x201 || kind == 0x202 || kind == 0x2A3)
                    PaintCheckBox(IntPtr.Zero);
            }

            private void PaintChrome(IntPtr suppliedDc)
            {
                if (painting || disposed || Handle == IntPtr.Zero || (control != null && !control.Visible)) return;
                Rect window;
                if (!GetWindowRect(Handle, out window)) return;
                ScrollInfo vertical, horizontal;
                bool v = Scrollbar(Handle, true, out vertical), h = Scrollbar(Handle, false, out horizontal);
                int border = BorderWidth(window);
                if (!v && !h && border == 0) return;
                IntPtr dc = suppliedDc == IntPtr.Zero ? GetWindowDC(Handle) : suppliedDc;
                if (dc == IntPtr.Zero) return;
                painting = true;
                try
                {
                    using (Graphics g = Graphics.FromHdc(dc))
                    {
                        g.SetClip(new Rectangle(0, 0, window.Right - window.Left, window.Bottom - window.Top), CombineMode.Intersect);
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        Point cursor; GetCursorPos(out cursor);
                        if (v) DrawBar(g, vertical, true, window, cursor);
                        if (h) DrawBar(g, horizontal, false, window, cursor);
                        if (v && h)
                            FillSolid(g, Palette.Surface, new Rectangle(vertical.Bounds.Left - window.Left, horizontal.Bounds.Top - window.Top, vertical.Bounds.Right - vertical.Bounds.Left, horizontal.Bounds.Bottom - horizontal.Bounds.Top));
                        if (border > 0)
                        {
                            g.SmoothingMode = SmoothingMode.None;
                            g.PixelOffsetMode = PixelOffsetMode.None;
                            using (Pen pen = new Pen(control != null && control.Focused ? Palette.Accent : Palette.Border))
                                for (int i = 0; i < border; i++) g.DrawRectangle(pen, i, i, window.Right - window.Left - 1 - i * 2, window.Bottom - window.Top - 1 - i * 2);
                        }
                    }
                }
                finally { painting = false; if (suppliedDc == IntPtr.Zero) ReleaseDC(Handle, dc); }
            }
            private int BorderWidth(Rect window)
            {
                if (control == null) return 1;
                var text = control as TextBoxBase; var list = control as ListView; var panel = control as Panel;
                BorderStyle style = text != null ? text.BorderStyle : list != null ? list.BorderStyle : panel != null ? panel.BorderStyle : BorderStyle.None;
                if (style == BorderStyle.None) return 0;
                return Math.Max(1, Math.Min(DpiMetrics.Px(control, 3), control.PointToScreen(Point.Empty).X - window.Left));
            }
            private void DrawBar(Graphics g, ScrollInfo info, bool vertical, Rect window, Point cursor)
            {
                Rectangle bar = info.Bounds.Rectangle; bar.Offset(-window.Left, -window.Top);
                int length = vertical ? bar.Height : bar.Width, breadth = vertical ? bar.Width : bar.Height;
                int arrow = ArrowExtent(info, vertical);
                FillSolid(g, Palette.Surface, bar);
                var first = vertical ? new Rectangle(bar.X, bar.Y, bar.Width, arrow) : new Rectangle(bar.X, bar.Y, arrow, bar.Height);
                var last = vertical ? new Rectangle(bar.X, bar.Bottom - arrow, bar.Width, arrow) : new Rectangle(bar.Right - arrow, bar.Y, arrow, bar.Height);
                Point local = new Point(cursor.X - window.Left, cursor.Y - window.Top);
                DrawArrow(g, first, vertical, false, (info.State[1] & 1) != 0, first.Contains(local));
                DrawArrow(g, last, vertical, true, (info.State[5] & 1) != 0, last.Contains(local));
                int start = Math.Max(arrow, Math.Min(length - arrow, info.ThumbStart));
                int end = Math.Max(start, Math.Min(length - arrow, info.ThumbEnd));
                if (end <= start || (info.State[3] & 0x18001) != 0) return;
                int inset = Math.Max(2, breadth / 4);
                Rectangle thumb = vertical ? new Rectangle(bar.Left + inset, bar.Top + start, Math.Max(2, bar.Width - inset * 2), end - start) : new Rectangle(bar.Left + start, bar.Top + inset, end - start, Math.Max(2, bar.Height - inset * 2));
                Color color = (info.State[3] & 8) != 0 ? Palette.Accent : thumb.Contains(local) ? Palette.Muted : Color.FromArgb(87, 93, 106);
                Glyphs.FillRounded(g, color, thumb, Math.Min(4, Math.Min(thumb.Width, thumb.Height) / 2F));
            }
            private static void DrawArrow(Graphics g, Rectangle bounds, bool vertical, bool forward, bool disabled, bool hover)
            {
                if (bounds.Width < 4 || bounds.Height < 4) return;
                if (hover && !disabled) FillSolid(g, Palette.Raised, bounds);
                float x = bounds.Left + bounds.Width / 2F, y = bounds.Top + bounds.Height / 2F;
                float half = Math.Max(2F, Math.Min(bounds.Width, bounds.Height) / 5F), sign = forward ? 1 : -1;
                PointF[] points = vertical ? new[] { new PointF(x - half, y - sign * half / 2), new PointF(x, y + sign * half / 2), new PointF(x + half, y - sign * half / 2) } : new[] { new PointF(x - sign * half / 2, y - half), new PointF(x + sign * half / 2, y), new PointF(x - sign * half / 2, y + half) };
                using (Pen pen = new Pen(disabled ? Palette.Border : Palette.Muted, 1.5F)) g.DrawLines(pen, points);
            }

            private static void FillSolid(Graphics graphics, Color color, Rectangle bounds)
            {
                SmoothingMode smoothing = graphics.SmoothingMode; PixelOffsetMode offset = graphics.PixelOffsetMode;
                try
                {
                    graphics.SmoothingMode = SmoothingMode.None; graphics.PixelOffsetMode = PixelOffsetMode.None;
                    using (Brush fill = new SolidBrush(color)) graphics.FillRectangle(fill, bounds);
                }
                finally { graphics.SmoothingMode = smoothing; graphics.PixelOffsetMode = offset; }
            }

            private void PaintCheckBox(IntPtr suppliedDc)
            {
                var box = control as CheckBox;
                if (painting || box == null || box.Appearance != Appearance.Normal || box.IsDisposed || !box.Visible) return;
                painting = true;
                try
                {
                    using (Graphics g = suppliedDc == IntPtr.Zero ? box.CreateGraphics() : Graphics.FromHdc(suppliedDc))
                    {
                        g.SetClip(box.ClientRectangle, CombineMode.Intersect);
                        FillSolid(g, box.BackColor, box.ClientRectangle);
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        Rectangle mark, text; CheckBoxLayout(box, out mark, out text);
                        int size = mark.Width;
                        if (size < 2) return;
                        Glyphs.FillRounded(g, box.Checked ? Palette.Accent : Palette.Raised, mark, DpiMetrics.Px(box, 2));
                        Glyphs.DrawRounded(g, box.Enabled ? Palette.Muted : Palette.Border, 1F, mark, DpiMetrics.Px(box, 2));
                        if (box.CheckState == CheckState.Checked)
                            using (Pen pen = new Pen(Palette.Background, Math.Max(1.5F, DpiMetrics.Px(box, 2)))) g.DrawLines(pen, new[] { new Point(mark.Left + size / 4, mark.Top + size / 2), new Point(mark.Left + size * 2 / 5, mark.Top + size * 3 / 4), new Point(mark.Left + size * 4 / 5, mark.Top + size / 4) });
                        else if (box.CheckState == CheckState.Indeterminate)
                            using (Pen pen = new Pen(Palette.Text, Math.Max(1.5F, DpiMetrics.Px(box, 2)))) g.DrawLine(pen, mark.Left + size / 4, mark.Top + size / 2, mark.Right - size / 4, mark.Top + size / 2);
                        // Native CheckBox.AutoSize already reserves its text margin.
                        TextFormatFlags flags = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
                        if (!box.UseMnemonic) flags |= TextFormatFlags.NoPrefix;
                        if (box.RightToLeft == RightToLeft.Yes) flags |= TextFormatFlags.RightToLeft | TextFormatFlags.Right;
                        TextRenderer.DrawText(g, box.Text, box.Font, text, box.Enabled ? box.ForeColor : Palette.Muted, flags);
                        if (box.Focused && text.Width > 2 && text.Height > 4) using (Pen pen = new Pen(Palette.Muted) { DashStyle = DashStyle.Dot }) g.DrawRectangle(pen, Rectangle.Inflate(text, -1, -2));
                    }
                }
                finally { painting = false; }
            }
        }
    }
}
