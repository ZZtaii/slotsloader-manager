using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SlotsInstaller
{
    // 1.0.8 (the user): a column that scrolls when its content is taller than the window gives it (the Mod library
    // inspector: preview, option details, REPLACES notes, INSTALL AS). No native scroll bar and no reserved gutter: a
    // thin rounded thumb in the app's colours appears over the right edge only while the user scrolls (wheel, dragging
    // the thumb, or Tab moving the focus out of view) and hides again shortly after. It widens under the pointer and
    // can be dragged. The wheel scrolls the column wherever the pointer is over it, before its children (a closed
    // dropdown no longer changes its value), but only when there is something to scroll.
    internal sealed class OverlayScroller : Panel, IMessageFilter
    {
        internal const int ThumbWidth = 4, ThumbHoverWidth = 8, ThumbInset = 3, ThumbMinLength = 32, HideDelayMs = 900, WheelStep = 60;
        private Control content;
        private Func<int, int> naturalHeight;   // content width -> the height it needs
        private int offset, contentHeight;
        private bool laying, again, filtering;
        // A control's own Visible flag: Control.Visible is false for every child while a parent is hidden (another page).
        private static readonly System.Reflection.MethodInfo GetState = typeof(Control).GetMethod("GetState", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance, null, new[] { typeof(int) }, null);
        internal static bool OwnVisible(Control c) { return GetState == null ? c.Visible : (bool)GetState.Invoke(c, new object[] { 2 }); }
        private readonly ScrollThumb thumb;
        private readonly Timer hideTimer;

        internal OverlayScroller()
        {
            BackColor = Palette.Surface; Margin = Padding.Empty; Padding = Padding.Empty;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
            thumb = new ScrollThumb(this) { Visible = false };
            Controls.Add(thumb);
            hideTimer = new Timer { Interval = HideDelayMs };
            hideTimer.Tick += delegate { if (thumb.Dragging || thumb.Hovering) return; hideTimer.Stop(); thumb.Visible = false; };
        }

        internal Control Content { get { return content; } }
        internal int Offset { get { return offset; } }
        internal int MaxOffset { get { return Math.Max(0, contentHeight - ClientSize.Height); } }
        internal bool ThumbShown { get { return thumb.Visible; } }
        internal Rectangle ThumbBounds { get { return thumb.Bounds; } }
        internal bool CanScroll { get { return MaxOffset > 0; } }
        // 1.0.8: called on a wheel turn down while there is nothing (more) to scroll; true when the content grew (the
        // inspector's compact details opened), and the wheel then scrolls it.
        internal Func<bool> Expand;

        // `natural`: the height the content needs at a width; the content is at least as tall as the column.
        internal void SetContent(Control c, Func<int, int> natural)
        {
            content = c; naturalHeight = natural;
            c.Dock = DockStyle.None; c.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            Controls.Add(c); thumb.BringToFront();
            WatchFocus(c);
            Relayout();
        }
        // Tab into a control out of view scrolls it into view.
        private void WatchFocus(Control c)
        {
            c.Enter += delegate(object sender, EventArgs e) { Control focused = sender as Control; if (focused != null && focused != content && !focused.HasChildren) ScrollIntoView(focused); };
            c.ControlAdded += delegate(object sender, ControlEventArgs e) { WatchFocus(e.Control); };
            foreach (Control child in c.Controls) WatchFocus(child);
        }

        // Sizes the content for the column: its width, and the taller of the column and what it needs; keeps the offset in range.
        // A change of the content while it is being sized (text re-wrapping at the new width) runs one more pass.
        internal void Relayout()
        {
            if (content == null) return;
            if (laying) { again = true; return; }
            laying = true;
            try
            {
                for (int pass = 0; pass < 4; pass++)
                {
                    again = false;
                    int width = Math.Max(1, ClientSize.Width), view = Math.Max(1, ClientSize.Height);
                    contentHeight = Math.Max(view, naturalHeight == null ? view : naturalHeight(width));
                    offset = Math.Max(0, Math.Min(offset, MaxOffset));
                    var bounds = new Rectangle(0, -offset, width, contentHeight);
                    if (content.Bounds != bounds) content.Bounds = bounds;
                    if (!again) break;
                }
                if (!CanScroll) { thumb.Visible = false; hideTimer.Stop(); }
                PlaceThumb();
            }
            finally { laying = false; }
        }
        protected override void OnLayout(LayoutEventArgs e) { base.OnLayout(e); Relayout(); }
        protected override void OnResize(EventArgs e) { base.OnResize(e); Relayout(); }

        internal bool ScrollTo(int value, bool show)
        {
            int next = Math.Max(0, Math.Min(value, MaxOffset));
            if (show && CanScroll) Flash();
            if (next == offset || content == null) return false;
            offset = next;
            content.Top = -offset;
            PlaceThumb();
            return true;
        }
        internal bool ScrollBy(int delta) { return ScrollTo(offset + delta, true); }
        internal void ScrollIntoView(Control c)
        {
            if (content == null || !CanScroll || c == null || !content.Contains(c)) return;
            Point at = content.PointToClient(c.PointToScreen(Point.Empty));   // content coordinates
            int pad = DpiMetrics.Px(this, 8), view = ClientSize.Height;
            if (at.Y - pad < offset) ScrollTo(at.Y - pad, true);
            else if (at.Y + c.Height + pad > offset + view) ScrollTo(Math.Min(at.Y - pad, at.Y + c.Height + pad - view), true);
        }

        // The thumb shows for a moment after each scroll.
        private void Flash()
        {
            PlaceThumb();
            if (!thumb.Visible) { thumb.Visible = true; thumb.BringToFront(); }
            hideTimer.Stop(); hideTimer.Start();
        }
        internal void PlaceThumb()
        {
            if (content == null) return;
            int view = Math.Max(1, ClientSize.Height), inset = DpiMetrics.Px(this, ThumbInset);
            int track = Math.Max(1, view - 2 * inset);
            int length = Math.Max(DpiMetrics.Px(this, ThumbMinLength), (int)((long)track * view / Math.Max(1, contentHeight)));
            length = Math.Min(track, length);
            int y = inset + (MaxOffset == 0 ? 0 : (int)((long)(track - length) * offset / MaxOffset));
            int width = DpiMetrics.Px(this, thumb.Hovering || thumb.Dragging ? ThumbHoverWidth : ThumbWidth);
            var bounds = new Rectangle(ClientSize.Width - width - DpiMetrics.Px(this, 2), y, width, length);
            if (thumb.Bounds != bounds) thumb.Bounds = bounds;
        }
        // Thumb drag: pixels of thumb travel to content offset.
        internal int OffsetForThumbTravel(int startOffset, int travel)
        {
            int inset = DpiMetrics.Px(this, ThumbInset), track = Math.Max(1, ClientSize.Height - 2 * inset), room = Math.Max(1, track - thumb.Height);
            return startOffset + (int)((long)travel * MaxOffset / room);
        }

        // ---- Wheel ---------------------------------------------------------------------------------------------
        private const int WM_MOUSEWHEEL = 0x020A;
        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); if (!filtering) { Application.AddMessageFilter(this); filtering = true; } }
        protected override void OnHandleDestroyed(EventArgs e) { if (filtering) { Application.RemoveMessageFilter(this); filtering = false; } base.OnHandleDestroyed(e); }
        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg != WM_MOUSEWHEEL || (!CanScroll && Expand == null) || !Visible || IsDisposed) return false;
            Control under = Control.FromChildHandle(WindowFromPoint(Cursor.Position));
            if (under == null || (under != this && !Contains(under))) return false;
            return Wheel((short)(((long)m.WParam >> 16) & 0xFFFF));
        }
        // One notch (120) scrolls WheelStep logical pixels; touchpads send smaller steps.
        internal bool Wheel(int delta)
        {
            if (delta < 0 && offset >= MaxOffset && Expand != null && Expand()) { Relayout(); if (!CanScroll) return true; }
            if (!CanScroll) return false;
            ScrollBy(-(int)Math.Round(delta / 120.0 * DpiMetrics.Px(this, WheelStep)));
            return true;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { if (filtering) { Application.RemoveMessageFilter(this); filtering = false; } hideTimer.Dispose(); }
            base.Dispose(disposing);
        }

        // The thumb: a rounded bar clipped to its shape (no strip around it covers the content), muted, lighter while
        // hovered or dragged.
        private sealed class ScrollThumb : Control
        {
            private readonly OverlayScroller owner;
            private bool hovering, dragging; private int dragY, dragOffset;
            internal bool Hovering { get { return hovering; } }
            internal bool Dragging { get { return dragging; } }
            internal ScrollThumb(OverlayScroller owner)
            {
                this.owner = owner;
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
                SetStyle(ControlStyles.Selectable, false);
                TabStop = false; AccessibleRole = AccessibleRole.ScrollBar; AccessibleName = "Scroll the details";
            }
            protected override void OnResize(EventArgs e)
            {
                base.OnResize(e);
                if (Width <= 0 || Height <= 0) return;
                using (GraphicsPath path = Rounded(new Rectangle(0, 0, Width, Height), Width / 2f)) Region = new Region(path);
            }
            private static GraphicsPath Rounded(Rectangle r, float radius)
            {
                var path = new GraphicsPath(); float d = Math.Max(1f, radius * 2);
                path.AddArc(r.X, r.Y, d, d, 180, 90); path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
                path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
                path.CloseFigure();
                return path;
            }
            protected override void OnPaint(PaintEventArgs e)
            {
                Color colour = dragging ? Palette.Text : hovering ? Color.FromArgb(196, 201, 211) : Palette.Muted;
                using (var brush = new SolidBrush(colour)) e.Graphics.FillRectangle(brush, ClientRectangle);
            }
            protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hovering = true; owner.PlaceThumb(); Invalidate(); }
            protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hovering = false; if (!dragging) owner.PlaceThumb(); Invalidate(); owner.hideTimer.Stop(); owner.hideTimer.Start(); }
            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                if (e.Button != MouseButtons.Left) return;
                dragging = true; dragY = Cursor.Position.Y; dragOffset = owner.Offset; Capture = true; Invalidate();
            }
            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                if (dragging) owner.ScrollTo(owner.OffsetForThumbTravel(dragOffset, Cursor.Position.Y - dragY), true);
            }
            protected override void OnMouseUp(MouseEventArgs e)
            {
                base.OnMouseUp(e);
                if (!dragging) return;
                dragging = false; Capture = false; owner.PlaceThumb(); Invalidate(); owner.hideTimer.Stop(); owner.hideTimer.Start();
            }
            protected override void OnMouseCaptureChanged(EventArgs e) { base.OnMouseCaptureChanged(e); if (dragging && !Capture) { dragging = false; Invalidate(); } }
        }
    }
}
