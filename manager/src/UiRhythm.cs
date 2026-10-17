using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace SlotsInstaller
{
    // 2.4.0: one vertical rhythm for stacked settings and notes (96-DPI logical pixels; the window scales them).
    // A control and its own note are close, a button and the result it produces get a clear gap, and groups are
    // further apart than lines.
    internal static class Rhythm
    {
        internal const int Related = 6;       // a field or check box and its own note
        internal const int AfterAction = 14;  // a button and the result it produces
        internal const int Group = 20;        // before the next caption
        internal const float LineSpacing = 1.35F;
        internal static Panel Gap(int height) { return new Panel { Height = height, Margin = Padding.Empty, Padding = Padding.Empty, TabStop = false }; }
    }

    // 2.4.1: the one compact notice banner (Installed page: loose file loader, leftover costume pak, game files that
    // changed, missing backups). One slim row: a muted tint with an accent stripe on the left, regular-weight text
    // at the normal UI size, and compact secondary buttons as tall as the page's other small buttons.
    internal sealed class NoticeBanner : Panel
    {
        internal const int RowHeight = 36, ButtonHeight = 28;
        internal static readonly Color Tint = Color.FromArgb(44, 39, 35);
        internal readonly Label Message;
        internal readonly FlowLayoutPanel Actions;
        internal NoticeBanner()
        {
            Dock = DockStyle.Top; Height = RowHeight; BackColor = Tint; Padding = new Padding(14, 4, 6, 4);
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
            Actions = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Padding = Padding.Empty, Margin = Padding.Empty, BackColor = Tint };
            Message = new Label { UseMnemonic = false, Dock = DockStyle.Fill, ForeColor = Palette.Text, Font = Palette.Normal, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, BackColor = Tint };
            Controls.Add(Message); Controls.Add(Actions);
        }
        // Buttons keep their own width; the style is the compact secondary one (Danger stays for destructive actions).
        internal FlatButton Add(FlatButton button)
        {
            button.Primary = false; button.Height = ButtonHeight; button.Font = Palette.Small; button.Margin = new Padding(6, 0, 0, 0); button.BackColor = Palette.Raised; button.ForeColor = Palette.Text;
            Actions.Controls.Add(button); return button;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (Brush stripe = new SolidBrush(Palette.Accent)) e.Graphics.FillRectangle(stripe, 0, 0, Math.Max(2, DpiMetrics.Px(this, 3)), Height);
        }
    }

    // A muted note that wraps at word boundaries and draws every line at the same line height, growing to fit
    // when docked to the top. A line holding a folder path is not wrapped; its middle is shortened with an ellipsis.
    internal sealed class NoteLabel : Label
    {
        private bool fitting;
        internal int MinLines;
        internal NoteLabel()
        {
            UseMnemonic = false; AutoSize = false;
            SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
        }
        // Measured with the control's own device context, as painting does (Font.Height reports the system DPI).
        private int LineHeight(IDeviceContext dc) { return (int)Math.Ceiling(TextRenderer.MeasureText(dc, "Ag", Font, new Size(Int32.MaxValue, Int32.MaxValue), TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine).Height * Rhythm.LineSpacing); }

        private List<string> Lines(IDeviceContext dc, int width)
        {
            var lines = new List<string>();
            foreach (string paragraph in (Text ?? "").Replace("\r\n", "\n").Split('\n'))
            {
                if (paragraph.Length == 0) { lines.Add(""); continue; }
                if (IsPath(paragraph)) { lines.Add(paragraph); continue; }   // a folder path stays on one line (middle ellipsis)
                string current = "";
                foreach (string word in paragraph.Split(' '))
                {
                    string candidate = current.Length == 0 ? word : current + " " + word;
                    if (current.Length > 0 && Measure(dc, candidate) > width) { lines.Add(current); current = word; }
                    else current = candidate;
                }
                lines.Add(current);
            }
            if ((Text ?? "").Length == 0) lines.Clear();
            return lines;
        }
        private static bool IsPath(string text) { return text.Contains(":\\") || text.StartsWith("\\\\"); }
        private int Measure(IDeviceContext dc, string text) { return TextRenderer.MeasureText(dc, text, Font, new Size(Int32.MaxValue, Int32.MaxValue), TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width; }
        // Tests: the wrapped lines at the current width.
        internal int LineCount { get { if (!IsHandleCreated) return 0; using (Graphics g = CreateGraphics()) return Lines(g, Math.Max(1, Width - Padding.Horizontal)).Count; } }

        private void Fit()
        {
            if (fitting || !IsHandleCreated || Dock != DockStyle.Top) return;
            int width = Width - Padding.Horizontal;
            if (width <= 0) return;
            int height;
            using (Graphics g = CreateGraphics()) height = Math.Max(MinLines, Lines(g, width).Count) * LineHeight(g) + Padding.Vertical;
            if (Height == height) return;
            fitting = true;
            try { Height = height; } finally { fitting = false; }
        }
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Fit(); }
        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Fit(); Invalidate(); }
        protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); Fit(); Invalidate(); }
        protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); Fit(); }
        protected override void OnPaddingChanged(EventArgs e) { base.OnPaddingChanged(e); Fit(); Invalidate(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            int width = Width - Padding.Horizontal, line = LineHeight(e.Graphics), y = Padding.Top;
            if (width <= 0) return;
            foreach (string text in Lines(e.Graphics, width))
            {
                if (y >= Height) break;
                TextFormatFlags flags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | (IsPath(text) ? TextFormatFlags.PathEllipsis : TextFormatFlags.EndEllipsis);
                TextRenderer.DrawText(e.Graphics, text, Font, new Rectangle(Padding.Left, y, width, line), Enabled ? ForeColor : SystemColors.GrayText, flags);
                y += line;
            }
        }
    }
}
