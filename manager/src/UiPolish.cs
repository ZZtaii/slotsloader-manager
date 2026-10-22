using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SlotsInstaller
{
    // Vector glyphs drawn with anti-aliased GDI+ at the control's device DPI.
    // Font glyphs such as "⌄" fall back to a plain letter in Segoe UI.
    internal enum ButtonGlyph { None, ExpandAll, CollapseAll, Gear, Coffee, Bug, Chat, GitHub, Patreon, Nexus, ColorSync, History, Presets, Refresh }
    internal enum ChevronDirection { Right, Down, Up }

    internal static class Glyphs
    {
        internal static GraphicsPath Rounded(RectangleF bounds, float radius)
        {
            var path = new GraphicsPath();
            float d = Math.Max(0.1F, Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height)));
            if (radius <= 0.5F) { path.AddRectangle(bounds); return path; }
            path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
        internal static void FillRounded(Graphics graphics, Color color, RectangleF bounds, float radius)
        {
            SmoothingMode previous = graphics.SmoothingMode; graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Rounded(bounds, radius)) using (var brush = new SolidBrush(color)) graphics.FillPath(brush, path);
            graphics.SmoothingMode = previous;
        }
        internal static void DrawRounded(Graphics graphics, Color color, float width, RectangleF bounds, float radius)
        {
            SmoothingMode previous = graphics.SmoothingMode; graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Rounded(bounds, radius)) using (var pen = new Pen(color, width)) graphics.DrawPath(pen, path);
            graphics.SmoothingMode = previous;
        }
        internal static void Chevron(Graphics graphics, PointF center, float half, ChevronDirection direction, Color color, float width)
        {
            PointF[] points;
            if (direction == ChevronDirection.Right) points = new[] { new PointF(center.X - half / 2, center.Y - half), new PointF(center.X + half / 2, center.Y), new PointF(center.X - half / 2, center.Y + half) };
            else if (direction == ChevronDirection.Down) points = new[] { new PointF(center.X - half, center.Y - half / 2), new PointF(center.X, center.Y + half / 2), new PointF(center.X + half, center.Y - half / 2) };
            else points = new[] { new PointF(center.X - half, center.Y + half / 2), new PointF(center.X, center.Y - half / 2), new PointF(center.X + half, center.Y + half / 2) };
            SmoothingMode previous = graphics.SmoothingMode; graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var pen = new Pen(color, width) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round }) graphics.DrawLines(pen, points);
            graphics.SmoothingMode = previous;
        }
        internal static void Draw(Graphics graphics, ButtonGlyph glyph, Rectangle area, Color color, int dpi)
        {
            if (BrandGlyphs.Handles(glyph)) { BrandGlyphs.Draw(graphics, glyph, area, color, dpi); return; }   // 2.5.9 About-card icons
            float scale = dpi / 96F;
            var center = new PointF(area.Left + area.Width / 2F, area.Top + area.Height / 2F);
            float stroke = 1.6F * scale;
            if (glyph == ButtonGlyph.ExpandAll || glyph == ButtonGlyph.CollapseAll)
            {
                var direction = glyph == ButtonGlyph.ExpandAll ? ChevronDirection.Down : ChevronDirection.Up;
                float half = 4.5F * scale, offset = 2.8F * scale;
                Chevron(graphics, new PointF(center.X, center.Y - offset), half, direction, color, stroke);
                Chevron(graphics, new PointF(center.X, center.Y + offset), half, direction, color, stroke);
            }
            else if (glyph == ButtonGlyph.Gear)
            {
                float outer = 9F * scale, inner = outer * 0.72F, hole = outer * 0.34F;
                const int teeth = 8;
                var points = new List<PointF>();
                for (int t = 0; t < teeth; t++)
                {
                    double a = t * Math.PI * 2 / teeth, w = Math.PI / teeth;
                    foreach (var p in new[] { Tuple.Create(a - w * 0.62, inner), Tuple.Create(a - w * 0.36, outer), Tuple.Create(a + w * 0.36, outer), Tuple.Create(a + w * 0.62, inner) })
                        points.Add(new PointF(center.X + (float)(Math.Cos(p.Item1) * p.Item2), center.Y + (float)(Math.Sin(p.Item1) * p.Item2)));
                }
                SmoothingMode previous = graphics.SmoothingMode; graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (var path = new GraphicsPath(FillMode.Alternate))
                using (var brush = new SolidBrush(color))
                {
                    path.AddPolygon(points.ToArray());
                    path.AddEllipse(center.X - hole, center.Y - hole, hole * 2, hole * 2);
                    graphics.FillPath(brush, path);
                }
                graphics.SmoothingMode = previous;
            }
            else if (glyph == ButtonGlyph.Bug)
            {
                // 2.5.9 status-bar bug icon, outlined like the other glyphs (about 16 px at 100 %).
                SmoothingMode previous = graphics.SmoothingMode; graphics.SmoothingMode = SmoothingMode.AntiAlias;
                float s = scale, x = center.X, y = center.Y;
                using (var pen = new Pen(color, 1.4F * s) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                {
                    y += 0.75F * s;
                    graphics.DrawEllipse(pen, x - 4F * s, y - 3F * s, 8F * s, 10F * s);
                    graphics.DrawLine(pen, x, y - 1F * s, x, y + 6.5F * s);
                    graphics.DrawArc(pen, x - 2.5F * s, y - 6.5F * s, 5F * s, 5F * s, 180, 180);
                    foreach (int side in new[] { -1, 1 })
                    {
                        graphics.DrawLine(pen, x + side * 4F * s, y, x + side * 7F * s, y - 1.5F * s);
                        graphics.DrawLine(pen, x + side * 4F * s, y + 2.5F * s, x + side * 7.5F * s, y + 2.5F * s);
                        graphics.DrawLine(pen, x + side * 3.5F * s, y + 5F * s, x + side * 6.5F * s, y + 7F * s);
                        graphics.DrawLine(pen, x + side * 1.5F * s, y - 6F * s, x + side * 3F * s, y - 8.5F * s);
                    }
                }
                graphics.SmoothingMode = previous;
            }
            else if (glyph == ButtonGlyph.History)
            {
                // 1.0.1 Installed-page History icon: a clock whose rim turns back into an arrow (about 18 px at 100 %).
                SmoothingMode previous = graphics.SmoothingMode; graphics.SmoothingMode = SmoothingMode.AntiAlias;
                float s = scale, x = center.X, y = center.Y, r = 7.5F * s;
                using (var pen = new Pen(color, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                {
                    graphics.DrawArc(pen, x - r, y - r, r * 2, r * 2, 200, 300);   // open at the upper left
                    double end = 200 * Math.PI / 180;
                    var tip = new PointF(x + (float)(Math.Cos(end) * r), y + (float)(Math.Sin(end) * r));
                    graphics.DrawLines(pen, new[] { new PointF(tip.X - 3.2F * s, tip.Y - 0.6F * s), tip, new PointF(tip.X + 0.4F * s, tip.Y - 3.4F * s) });
                    graphics.DrawLines(pen, new[] { new PointF(x, y - 4F * s), new PointF(x, y), new PointF(x + 3F * s, y + 2F * s) });
                }
                graphics.SmoothingMode = previous;
            }
            else if (glyph == ButtonGlyph.Presets)
            {
                // 1.0.1 Installed-page Presets icon: three stacked layers (a saved set of outfits).
                SmoothingMode previous = graphics.SmoothingMode; graphics.SmoothingMode = SmoothingMode.AntiAlias;
                float s = scale, x = center.X, y = center.Y, w = 8F * s, h = 3.6F * s;
                using (var pen = new Pen(color, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                {
                    float top = y - 3.6F * s;
                    graphics.DrawPolygon(pen, new[] { new PointF(x, top - h), new PointF(x + w, top), new PointF(x, top + h), new PointF(x - w, top) });
                    foreach (float dy in new[] { 3.6F * s, 7.2F * s })
                        graphics.DrawLines(pen, new[] { new PointF(x - w, top + dy), new PointF(x, top + dy + h), new PointF(x + w, top + dy) });
                }
                graphics.SmoothingMode = previous;
            }
            else if (glyph == ButtonGlyph.Refresh)
            {
                // Installed-page Refresh icon (1.0.8 redraw: the 1.0.6 one, open at the top with a tiny tick, read as a power
                // symbol): a clockwise circle open at the right, its end an arrow head along the turn; History's mirror.
                SmoothingMode previous = graphics.SmoothingMode; graphics.SmoothingMode = SmoothingMode.AntiAlias;
                float s = scale, x = center.X, y = center.Y, r = 7.5F * s;   // History's size
                const float start = 25F, sweep = 285F;   // degrees, clockwise from 3 o'clock: the gap spans 310°..385° (the right side)
                using (var pen = new Pen(color, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                {
                    graphics.DrawArc(pen, x - r, y - r, r * 2, r * 2, start, sweep);
                    double end = (start + sweep) * Math.PI / 180;
                    // Direction of travel at the end (clockwise on screen) and the head: a tip a little past the arc end, two arms back.
                    double tx = -Math.Sin(end), ty = Math.Cos(end);
                    var tip = new PointF(x + (float)(Math.Cos(end) * r + tx * 1.2 * s), y + (float)(Math.Sin(end) * r + ty * 1.2 * s));
                    Func<double, PointF> arm = delegate(double turn)
                    {
                        double c = Math.Cos(turn), n = Math.Sin(turn), bx = -tx * c + ty * n, by = -tx * n - ty * c;   // back direction turned by `turn`
                        return new PointF(tip.X + (float)(bx * 4.2 * s), tip.Y + (float)(by * 4.2 * s));
                    };
                    graphics.DrawLines(pen, new[] { arm(Math.PI / 4.2), tip, arm(-Math.PI / 4.2) });
                }
                graphics.SmoothingMode = previous;
            }
        }
    }

    // 2.5.9: Settings → About card: a rounded button with a bold title, a ↗ and a one- or two-line description.
    // The whole card opens Url (click, Enter or Space); hover/focus draws the accent border and title.
    internal sealed class AboutCard : Control
    {
        internal static readonly Font TitleFont = new Font("Segoe UI", 11F, FontStyle.Bold);
        internal string Title = "", Body = "", Url;
        internal ButtonGlyph Icon;          // drawn 1.5x at the left, in IconColor (brand colours); None = text only
        internal Color IconColor = Palette.Text;
        internal Image Picture;             // 1.0.11: a logo drawn in the icon tile instead of Icon (BrewedVFX)
        private bool hot;
        internal AboutCard()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            Cursor = Cursors.Hand; TabStop = true; AccessibleRole = AccessibleRole.Link;
        }
        protected override void OnMouseEnter(EventArgs e) { hot = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hot = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override bool IsInputKey(Keys keyData) { return keyData == Keys.Enter || keyData == Keys.Space || base.IsInputKey(keyData); }
        protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) { e.Handled = true; OnClick(EventArgs.Empty); } base.OnKeyDown(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            float s = DeviceDpi / 96F;
            Color parent = Parent != null ? Parent.BackColor : Palette.Surface;
            using (var bg = new SolidBrush(parent)) e.Graphics.FillRectangle(bg, ClientRectangle);
            bool lit = hot || Focused;
            var card = new RectangleF(0.5F * s, 0.5F * s, Width - 1.5F * s, Height - 1.5F * s);
            Glyphs.FillRounded(e.Graphics, lit ? Palette.Selected : Palette.Background, card, 8 * s);
            Glyphs.DrawRounded(e.Graphics, lit ? Palette.Accent : Palette.Border, Math.Max(1F, s), card, 8 * s);
            int pad = (int)(16 * s), top = (int)(12 * s), titleHeight = (int)(26 * s);
            if (Icon != ButtonGlyph.None || Picture != null)
            {
                // A 40 px tile at the left, vertically centred; the text starts after it.
                int tile = (int)(40 * s);
                var tileRect = new RectangleF(pad, (Height - tile) / 2F, tile, tile);
                Glyphs.FillRounded(e.Graphics, Palette.Raised, tileRect, 8 * s);
                if (Picture != null)
                {
                    var previous = e.Graphics.InterpolationMode; e.Graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    // Fitted inside the tile with its own proportions (a wide logo is centred vertically).
                    float inset = 2 * s, box = tile - 2 * inset, fit = Math.Min(box / Picture.Width, box / Picture.Height);
                    float w = Picture.Width * fit, h = Picture.Height * fit;
                    e.Graphics.DrawImage(Picture, new RectangleF(tileRect.X + (tile - w) / 2F, tileRect.Y + (tile - h) / 2F, w, h));
                    e.Graphics.InterpolationMode = previous;
                }
                else Glyphs.Draw(e.Graphics, Icon, Rectangle.Round(tileRect), IconColor, (int)(DeviceDpi * 1.5F));
                pad += tile + (int)(14 * s);
            }
            int right = (int)(16 * s);
            TextRenderer.DrawText(e.Graphics, Title, TitleFont, new Rectangle(pad, top, Width - pad - right - (int)(20 * s), titleHeight), lit ? Palette.Accent : Palette.Text, TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(e.Graphics, "↗", Palette.Normal, new Rectangle(Width - right - (int)(16 * s), top, (int)(16 * s), titleHeight), lit ? Palette.Accent : Palette.Muted, TextFormatFlags.NoPrefix | TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(e.Graphics, Body, Palette.Normal, new Rectangle(pad, top + titleHeight + (int)(4 * s), Width - pad - right, Height - top - titleHeight - (int)(14 * s)), Palette.Muted, TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.TextBoxControl);
        }
    }

    // 2.5.9: a clickable glyph (status-bar support/bug icons): muted, the accent colour while hovered.
    internal sealed class IconLink : Control
    {
        internal ButtonGlyph Glyph;
        private bool hot;
        internal IconLink()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand; TabStop = false; AccessibleRole = AccessibleRole.Link;
        }
        protected override void OnMouseEnter(EventArgs e) { hot = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hot = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            using (var bg = new SolidBrush(BackColor)) e.Graphics.FillRectangle(bg, ClientRectangle);
            Glyphs.Draw(e.Graphics, Glyph, ClientRectangle, hot ? Palette.Accent : Palette.Muted, DeviceDpi);
        }
    }

    // A label whose text wraps to at most MaxLines and ends in an ellipsis,
    // or a single line with a path ellipsis. Its height follows its content.
    internal sealed class ClampedLabel : Label
    {
        internal int MaxLines = 2;
        internal bool UsePathEllipsis;
        private int fittedWidth = -1;
        private string fittedText;
        private string displayText = "";
        internal string DisplayText { get { return displayText; } }
        internal ClampedLabel()
        {
            UseMnemonic = false; AutoSize = false;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
        }
        private TextFormatFlags Flags
        {
            get
            {
                return TextFormatFlags.NoPrefix | (MaxLines > 1 ? TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl
                    : TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | (UsePathEllipsis ? TextFormatFlags.PathEllipsis : TextFormatFlags.EndEllipsis));
            }
        }
        internal void Fit()
        {
            int width = Math.Max(1, ClientSize.Width - Padding.Horizontal);
            if (width == fittedWidth && Text == fittedText) return;
            fittedWidth = width; fittedText = Text;
            if (!IsHandleCreated || MaxLines <= 1) { displayText = Text ?? ""; return; }
            using (Graphics graphics = CreateGraphics())
            {
                int line = TextRenderer.MeasureText(graphics, "Ag", Font, new Size(width, 0), Flags).Height;
                string text = Text ?? "";
                Func<string, int> height = delegate(string value) { return TextRenderer.MeasureText(graphics, value.Length == 0 ? " " : value, Font, new Size(width, 0), Flags).Height; };
                int limit = line * MaxLines;
                if (height(text) > limit)
                {
                    // Longest prefix that still fits with an ellipsis.
                    int low = 0, high = text.Length;
                    while (low < high) { int mid = (low + high + 1) / 2; if (height(text.Substring(0, mid).TrimEnd() + "…") <= limit) low = mid; else high = mid - 1; }
                    text = text.Substring(0, low).TrimEnd() + "…";
                }
                displayText = text;
                int lines = Math.Max(1, Math.Min(MaxLines, (int)Math.Ceiling(height(text) / (double)line)));
                int wanted = lines * line + Padding.Vertical;
                if (Height != wanted) Height = wanted;
            }
        }
        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Fit(); Invalidate(); }
        protected override void OnFontChanged(EventArgs e) { fittedWidth = -1; base.OnFontChanged(e); Fit(); }
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); fittedWidth = -1; Fit(); }
        protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); fittedWidth = -1; Fit(); }
        protected override void OnResize(EventArgs e) { base.OnResize(e); if (Math.Max(1, ClientSize.Width - Padding.Horizontal) != fittedWidth) Fit(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var area = new Rectangle(Padding.Left, Padding.Top, Math.Max(1, ClientSize.Width - Padding.Horizontal), Math.Max(1, ClientSize.Height - Padding.Vertical));
            string text = MaxLines > 1 ? displayText : Text ?? "";
            TextFormatFlags flags = Flags;
            if (MaxLines <= 1 && UsePathEllipsis && text.Length > 0)
            {
                // A path ellipsis keeps the file name whole; when even the name
                // is too wide, end it with an ellipsis instead of clipping it.
                string name = text.Substring(text.LastIndexOfAny(new[] { '\\', '/' }) + 1);
                if (TextRenderer.MeasureText(e.Graphics, "…\\" + name, Font, new Size(int.MaxValue, area.Height), TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine).Width > area.Width)
                { text = "…\\" + name; flags = (flags & ~TextFormatFlags.PathEllipsis) | TextFormatFlags.EndEllipsis; }
            }
            TextRenderer.DrawText(e.Graphics, text, Font, area, Enabled ? ForeColor : Palette.Muted, flags);
        }
    }

    // Compact rounded labels for character, mapped outfits and state.
    internal sealed class ChipStrip : Control
    {
        internal struct Chip { internal string Text; internal Color Fore, Back; internal Chip(string text, Color fore, Color back) { Text = text; Fore = fore; Back = back; } }
        private readonly List<Chip> chips = new List<Chip>();
        internal IList<Chip> Chips { get { return chips.AsReadOnly(); } }
        internal ChipStrip()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Font = Palette.Small; TabStop = false; AccessibleRole = AccessibleRole.StaticText;
        }
        internal static Chip Neutral(string text) { return new Chip(text, Palette.Text, Palette.Raised); }
        internal static Chip Good(string text) { return new Chip(text, Palette.Green, Color.FromArgb(30, 52, 43)); }
        internal static Chip Warn(string text) { return new Chip(text, Palette.Accent, Color.FromArgb(60, 43, 29)); }
        internal static Chip Quiet(string text) { return new Chip(text, Palette.Muted, Color.FromArgb(34, 37, 43)); }
        internal void SetChips(IEnumerable<Chip> values)
        {
            var next = values.Where(c => !String.IsNullOrEmpty(c.Text)).ToList();
            if (next.Count == chips.Count && next.Zip(chips, (a, b) => a.Text == b.Text && a.Fore == b.Fore && a.Back == b.Back).All(same => same)) return;   // 1.0.1: no repaint for the same chips
            chips.Clear(); chips.AddRange(next);
            AccessibleName = String.Join(", ", chips.Select(c => c.Text));
            Fit(); Invalidate();
        }
        private List<Rectangle> Arrange(Graphics graphics, int width)
        {
            var result = new List<Rectangle>();
            int x = 0, y = Padding.Top, h = DpiMetrics.Px(this, 26), gap = DpiMetrics.Px(this, 6), pad = DpiMetrics.Px(this, 10);
            foreach (var chip in chips)
            {
                int w = Math.Min(width, TextRenderer.MeasureText(graphics, chip.Text, Font, new Size(int.MaxValue, h), TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine).Width + pad * 2);
                if (x > 0 && x + w > width) { x = 0; y += h + gap; }
                result.Add(new Rectangle(x, y, w, h)); x += w + gap;
            }
            return result;
        }
        internal void Fit()
        {
            if (!IsHandleCreated) return;
            using (Graphics graphics = CreateGraphics())
            {
                var boxes = Arrange(graphics, Math.Max(1, ClientSize.Width));
                int wanted = boxes.Count == 0 ? 0 : boxes.Max(b => b.Bottom) + Padding.Bottom;
                if (Height != wanted) Height = wanted;
            }
        }
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Fit(); }
        protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); Fit(); }
        private int lastWidth = -1;
        protected override void OnResize(EventArgs e) { base.OnResize(e); if (Width != lastWidth) { lastWidth = Width; Fit(); } }
        protected override void OnPaint(PaintEventArgs e)
        {
            using (var back = new SolidBrush(Parent == null ? Palette.Surface : Parent.BackColor)) e.Graphics.FillRectangle(back, ClientRectangle);
            var boxes = Arrange(e.Graphics, Math.Max(1, ClientSize.Width));
            for (int i = 0; i < chips.Count; i++)
            {
                Glyphs.FillRounded(e.Graphics, chips[i].Back, boxes[i], boxes[i].Height / 2F);
                TextRenderer.DrawText(e.Graphics, chips[i].Text, Font, boxes[i], chips[i].Fore, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }
        }
    }

    // Shown over the pages while a file or folder is dragged onto the window.
    internal sealed class DropOverlay : Control
    {
        internal string Title = "", Detail = "";
        internal bool Accepting;
        internal DropOverlay()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Palette.Background; Visible = false; TabStop = false; AccessibleRole = AccessibleRole.Alert;
        }
        internal void Present(bool accepting, string title, string detail)
        {
            Accepting = accepting; Title = title ?? ""; Detail = detail ?? ""; AccessibleName = Title + ". " + Detail;
            Invalidate();
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Palette.Background);
            int inset = DpiMetrics.Px(this, 18);
            var frame = new RectangleF(inset, inset, Math.Max(1, Width - inset * 2), Math.Max(1, Height - inset * 2));
            Color accent = Accepting ? Palette.Accent : Palette.Red;
            Glyphs.FillRounded(e.Graphics, Color.FromArgb(Accepting ? 30 : 26, accent), frame, DpiMetrics.Px(this, 10));
            SmoothingMode previous = e.Graphics.SmoothingMode; e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Glyphs.Rounded(frame, DpiMetrics.Px(this, 10)))
            using (var pen = new Pen(accent, DpiMetrics.Px(this, 2)) { DashStyle = DashStyle.Dash })
                e.Graphics.DrawPath(pen, path);
            e.Graphics.SmoothingMode = previous;
            using (var title = new Font("Segoe UI", 18F, FontStyle.Bold))
            {
                int titleHeight = TextRenderer.MeasureText(e.Graphics, "Ag", title).Height;
                int top = Height / 2 - titleHeight;
                var area = new Rectangle(inset * 2, top, Math.Max(1, Width - inset * 4), titleHeight);
                TextRenderer.DrawText(e.Graphics, Title, title, area, Accepting ? Palette.Text : Palette.Red, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
                var detail = new Rectangle(inset * 2, top + titleHeight + DpiMetrics.Px(this, 6), Math.Max(1, Width - inset * 4), DpiMetrics.Px(this, 48));
                TextRenderer.DrawText(e.Graphics, Detail, Palette.Normal, detail, Palette.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
            }
        }
    }

    // Dark, header-matched Windows title bars. Every app form calls
    // AppBranding.Apply, which forwards here. Older systems ignore it.
    internal static class WindowChrome
    {
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
        internal const int DarkModeAttribute = 20, LegacyDarkModeAttribute = 19, BorderColorAttribute = 34, CaptionColorAttribute = 35, TextColorAttribute = 36;
        internal static int LastDarkModeResult = -1, LastCaptionResult = -1;
        internal static int ColorRef(Color color) { return color.R | (color.G << 8) | (color.B << 16); }
        internal static void Apply(Form form)
        {
            form.HandleCreated += delegate { ApplyTo(form.Handle); FirstEraseFill.Attach(form); };
            if (form.IsHandleCreated) { ApplyTo(form.Handle); FirstEraseFill.Attach(form); }
        }
        internal static void ApplyTo(IntPtr window)
        {
            if (window == IntPtr.Zero) return;
            try
            {
                int on = 1;
                int result = DwmSetWindowAttribute(window, DarkModeAttribute, ref on, 4);
                if (result != 0) result = DwmSetWindowAttribute(window, LegacyDarkModeAttribute, ref on, 4);
                LastDarkModeResult = result;
                int caption = ColorRef(Palette.Sidebar), border = ColorRef(Palette.Border), text = ColorRef(Palette.Text);
                LastCaptionResult = DwmSetWindowAttribute(window, CaptionColorAttribute, ref caption, 4);
                DwmSetWindowAttribute(window, BorderColorAttribute, ref border, 4);
                DwmSetWindowAttribute(window, TextColorAttribute, ref text, 4);
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
        }
        // 1.0.11: rounded corners for a borderless card window (Windows 11; older systems ignore it).
        internal const int CornerAttribute = 33, RoundCorners = 2;
        internal static void Round(Form form)
        {
            form.HandleCreated += delegate { try { int round = RoundCorners; DwmSetWindowAttribute(form.Handle, CornerAttribute, ref round, 4); } catch (DllNotFoundException) { } catch (EntryPointNotFoundException) { } };
        }
    }

    // 1.0.11: never a white frame. When a window is shown, Windows erases each control with its own colour, except the
    // custom-drawn ones (they skip the erase and wait for their first paint), whose area stayed the bare white surface
    // until then. The window's first visible erase now fills its whole client area, under its controls too, with its
    // background colour, so a control that has not painted yet shows the app's dark background instead.
    internal sealed class FirstEraseFill : NativeWindow
    {
        [DllImport("user32.dll")] private static extern IntPtr GetDCEx(IntPtr window, IntPtr clip, int flags);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
        private readonly Form form;
        private bool done;
        private FirstEraseFill(Form form) { this.form = form; AssignHandle(form.Handle); }
        internal static void Attach(Form form)
        {
            var fill = new FirstEraseFill(form);
            form.HandleDestroyed += delegate { GC.KeepAlive(fill); };   // the handle table holds subclasses weakly
        }
        protected override void WndProc(ref Message m)
        {
            const int WM_ERASEBKGND = 0x14, DCX_CACHE = 0x2, DCX_CLIPSIBLINGS = 0x10;
            if (m.Msg == WM_ERASEBKGND && !done && IsWindowVisible(Handle))
            {
                done = true;
                IntPtr dc = GetDCEx(Handle, IntPtr.Zero, DCX_CACHE | DCX_CLIPSIBLINGS);   // no DCX_CLIPCHILDREN: under the controls too
                if (dc != IntPtr.Zero)
                {
                    try { using (Graphics g = Graphics.FromHdc(dc)) using (var brush = new SolidBrush(form.BackColor)) g.FillRectangle(brush, form.ClientRectangle); }
                    finally { ReleaseDC(Handle, dc); }
                }
            }
            base.WndProc(ref m);
        }
    }

    // One table drives key handling, the gear menu and tooltips. None of
    // these shortcuts installs, uninstalls or removes anything.
    internal enum ShortcutAction { FocusSearch, ClearSearch, LibraryPage, InstalledPage, AssistantPage, SettingsPage, RefreshLibraries, OpenArchive, OpenFolder, ToggleFilters, SwitchMode, ToggleNames, HelpPage }
    internal sealed class ShortcutInfo
    {
        internal readonly Keys Keys; internal readonly ShortcutAction Action; internal readonly string Display, Description;
        internal ShortcutInfo(Keys keys, ShortcutAction action, string display, string description) { Keys = keys; Action = action; Display = display; Description = description; }
    }
    internal static class Shortcuts
    {
        internal static readonly ShortcutInfo[] All =
        {
            new ShortcutInfo(Keys.Control | Keys.F, ShortcutAction.FocusSearch, "Ctrl+F", "Search this page"),
            new ShortcutInfo(Keys.Escape, ShortcutAction.ClearSearch, "Esc", "Clear search"),
            new ShortcutInfo(Keys.Control | Keys.D1, ShortcutAction.LibraryPage, "Ctrl+1", "Mod library"),
            new ShortcutInfo(Keys.Control | Keys.D2, ShortcutAction.InstalledPage, "Ctrl+2", "Installed page"),
            new ShortcutInfo(Keys.Control | Keys.D3, ShortcutAction.AssistantPage, "Ctrl+3", "Assistant"),
            new ShortcutInfo(Keys.Control | Keys.Oemcomma, ShortcutAction.SettingsPage, "Ctrl+,", "Settings"),
            new ShortcutInfo(Keys.F1, ShortcutAction.HelpPage, "F1", "Help"),   // 1.0.11
            new ShortcutInfo(Keys.F5, ShortcutAction.RefreshLibraries, "F5", "Refresh libraries"),
            new ShortcutInfo(Keys.Control | Keys.O, ShortcutAction.OpenArchive, "Ctrl+O", "Open archive"),
            new ShortcutInfo(Keys.Control | Keys.Shift | Keys.O, ShortcutAction.OpenFolder, "Ctrl+Shift+O", "Open folder"),
            new ShortcutInfo(Keys.Control | Keys.Shift | Keys.F, ShortcutAction.ToggleFilters, "Ctrl+Shift+F", "Show or hide filters"),
            new ShortcutInfo(Keys.Tab, ShortcutAction.SwitchMode, "Tab", "Switch Extra outfit / Replace original"),   // 1.0.1: Mod library only
            new ShortcutInfo(Keys.Control | Keys.Shift | Keys.N, ShortcutAction.ToggleNames, "Ctrl+Shift+N", "Show mod names or file names"),   // 1.0.6: Mod library only
        };
        internal static ShortcutInfo Find(Keys keys) { return All.FirstOrDefault(s => s.Keys == keys); }
        internal static string Display(ShortcutAction action) { var item = All.FirstOrDefault(s => s.Action == action); return item == null ? "" : item.Display; }
    }

    internal static class DropPaths
    {
        internal static readonly string[] ArchiveExtensions = { ".zip", ".rar", ".7z" };
        // Returns the single source to open, or null with a reason.
        internal static string Accept(string[] paths, out string reason)
        {
            reason = null;
            if (paths == null || paths.Length == 0) { reason = "Drop a ZIP, RAR or 7Z archive, or a mod folder."; return null; }
            if (paths.Length > 1) { reason = "Drop one archive or folder at a time."; return null; }
            string path = paths[0];
            try
            {
                if (String.IsNullOrWhiteSpace(path)) { reason = "This item has no file path."; return null; }
                path = Path.GetFullPath(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (Directory.Exists(path)) return path;
                if (!File.Exists(path)) { reason = "This item no longer exists."; return null; }
                if (!ArchiveExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)) { reason = Path.GetExtension(path).ToUpperInvariant().TrimStart('.') + " files are not mod archives. Use ZIP, RAR, 7Z or a folder."; return null; }
                return path;
            }
            catch (Exception error) { reason = error.Message; return null; }
        }
        internal static string[] FromData(IDataObject data)
        {
            if (data == null || !data.GetDataPresent(DataFormats.FileDrop)) return null;
            return data.GetData(DataFormats.FileDrop) as string[];
        }
    }
}
