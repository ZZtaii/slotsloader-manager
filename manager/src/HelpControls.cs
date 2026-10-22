using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace SlotsInstaller
{
    // 1.0.11: the Help page's topic list: group captions and one row per topic, drawn like the app's other lists
    // (the chosen row in the selection colour with an accent rail). Click, or Up/Down with the focus here, chooses.
    internal sealed class HelpTopicList : Control
    {
        private static readonly Font GroupFont = new Font("Segoe UI", 8F, FontStyle.Bold), RowFont = new Font("Segoe UI", 10F);
        private const TextFormatFlags Line = TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter;
        private readonly List<object> rows = new List<object>();   // a group name (string) or a HelpTopic
        private HelpTopic selected;
        private int hover = -1;
        internal Action<HelpTopic> Chosen;
        internal string Empty = "No topic matches.";

        internal HelpTopicList()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            TabStop = true; BackColor = Palette.Surface; AccessibleRole = AccessibleRole.List; AccessibleName = "Help topics";
        }

        internal IEnumerable<HelpTopic> Topics { get { return rows.OfType<HelpTopic>(); } }
        internal HelpTopic Selected { get { return selected; } set { selected = value; Invalidate(); } }

        internal void SetTopics(IEnumerable<HelpTopic> topics)
        {
            rows.Clear(); string group = null;
            foreach (HelpTopic t in topics ?? new HelpTopic[0])
            {
                if (t.Group != group) { group = t.Group; rows.Add(group); }
                rows.Add(t);
            }
            hover = -1; Invalidate();
        }

        private int Px(int logical) { return DpiMetrics.Px(this, logical); }
        private int RowHeight(object row) { return row is string ? Px(rows.IndexOf(row) == 0 ? 22 : 34) : Px(32); }
        internal int NaturalHeight(int width) { return rows.Count == 0 ? Px(40) : rows.Sum(r => RowHeight(r)) + Px(6); }
        private int RowAt(int y) { int top = 0; for (int i = 0; i < rows.Count; i++) { int h = RowHeight(rows[i]); if (y >= top && y < top + h) return i; top += h; } return -1; }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            using (var b = new SolidBrush(BackColor)) g.FillRectangle(b, ClientRectangle);
            if (rows.Count == 0) { TextRenderer.DrawText(g, Empty, RowFont, new Rectangle(Px(10), 0, Width - Px(10), Px(32)), Palette.Muted, Line); return; }
            int top = 0;
            for (int i = 0; i < rows.Count; i++)
            {
                int h = RowHeight(rows[i]);
                string group = rows[i] as string;
                if (group != null) TextRenderer.DrawText(g, group.ToUpperInvariant(), GroupFont, new Rectangle(Px(10), top, Width - Px(10), h - Px(4)), Palette.Muted, (Line & ~TextFormatFlags.VerticalCenter) | TextFormatFlags.Bottom);
                else
                {
                    var t = (HelpTopic)rows[i];
                    var r = new Rectangle(0, top, Width, h);
                    bool on = t == selected;
                    if (on || i == hover) using (var b = new SolidBrush(on ? Palette.Selected : Palette.Raised)) g.FillRectangle(b, r);
                    if (on) using (var b = new SolidBrush(Palette.Accent)) g.FillRectangle(b, 0, top, Math.Max(2, Px(3)), h);
                    TextRenderer.DrawText(g, t.Title, RowFont, new Rectangle(Px(12), top, Width - Px(16), h), on ? Palette.Text : Color.FromArgb(205, 208, 215), Line);
                    if (on && Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(r, -1, -1));
                }
                top += h;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); int i = RowAt(e.Y); if (i >= 0 && !(rows[i] is HelpTopic)) i = -1; if (i != hover) { hover = i; Cursor = i >= 0 ? Cursors.Hand : Cursors.Default; Invalidate(); } }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = -1; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e); Focus();
            int i = RowAt(e.Y);
            if (e.Button == MouseButtons.Left && i >= 0 && rows[i] is HelpTopic) Choose((HelpTopic)rows[i]);
        }
        protected override bool IsInputKey(Keys keyData) { Keys k = keyData & Keys.KeyCode; return k == Keys.Up || k == Keys.Down || base.IsInputKey(keyData); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode != Keys.Up && e.KeyCode != Keys.Down) return;
            List<HelpTopic> list = Topics.ToList();
            if (list.Count == 0) return;
            int i = list.IndexOf(selected) + (e.KeyCode == Keys.Down ? 1 : -1);
            Choose(list[Math.Max(0, Math.Min(list.Count - 1, i))]); e.Handled = true;
        }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
        private void Choose(HelpTopic t) { if (t == null) return; Selected = t; var handler = Chosen; if (handler != null) handler(t); }
    }

    // 1.0.11: one Help article: the group, title and lead, then headings, paragraphs, list items and note cards, with
    // inline `code` (a raised chip in a monospace font), **bold** and [[links]] to other topics (accent; a click opens the
    // topic, or scrolls to its heading), wrapped word by word. Previous / Next at the end; "← Back to …" at the top after a link.
    internal sealed class HelpArticleView : Control
    {
        private static readonly Font Body = new Font("Segoe UI", 10.5F), BodyBold = new Font("Segoe UI", 10.5F, FontStyle.Bold), CodeFont = new Font("Consolas", 10F),
            TitleFont = new Font("Segoe UI", 17F, FontStyle.Bold), HeadingFont = new Font("Segoe UI", 12F, FontStyle.Bold), CaptionFont = new Font("Segoe UI", 8F, FontStyle.Bold);
        // No padding anywhere, so titles and headings line up with the paragraphs and the group caption.
        private const TextFormatFlags Wrap = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPadding;
        private const TextFormatFlags Bare = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
        private sealed class LinkSpot { internal Rectangle Bounds; internal int Seq; internal string Target; }
        private HelpTopic topic, previous, next, backTo;
        private Rectangle previousLink, nextLink, backLink;
        private int hoverLink, hoverSeq = -1, linkSeq;   // hoverLink: -1 previous, 1 next, 2 back
        private readonly List<LinkSpot> links = new List<LinkSpot>();
        private readonly Dictionary<string, int> headingTops = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        internal Action<HelpTopic, string> Navigate;   // a [[link]]: the topic and its heading (or null)
        internal Action<HelpTopic> Page;               // Previous / Next
        internal Action Back;                          // "← Back to …" or the mouse's back button

        internal HelpArticleView()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Palette.Surface; AccessibleRole = AccessibleRole.Document;
        }

        internal HelpTopic Topic { get { return topic; } }
        internal HelpTopic BackTo { get { return backTo; } }
        internal IEnumerable<string> LinkTargets { get { return links.Select(l => l.Target).Distinct(); } }   // as last drawn
        internal void Show(HelpTopic t, HelpTopic before, HelpTopic after, HelpTopic back)
        {
            topic = t; previous = before; next = after; backTo = back; hoverLink = 0; hoverSeq = -1;
            AccessibleName = t == null ? "" : t.Title;
            AccessibleDescription = t == null ? "" : HelpDocument.Plain(t.Lead + " " + String.Join(" ", t.Blocks.Select(b => (b.Title.Length > 0 ? b.Title + ": " : "") + b.Text)));
            Invalidate();
        }

        private int Px(int logical) { return DpiMetrics.Px(this, logical); }
        internal int NaturalHeight(int width) { using (Graphics g = CreateGraphics()) return Lay(g, width, false); }
        // Where a heading of the shown topic starts (null when it has none of that name); valid after a layout at the current width.
        internal int? HeadingTop(string heading) { int y; return heading != null && headingTops.TryGetValue(heading.Trim(), out y) ? y : (int?)null; }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var b = new SolidBrush(BackColor)) g.FillRectangle(b, ClientRectangle);
            Lay(g, ClientSize.Width, true);
        }

        // Walks the article top to bottom at this width: draws it when asked and returns the height it needs.
        private int Lay(Graphics g, int width, bool draw)
        {
            if (topic == null) return Px(40);
            if (draw) links.Clear();
            headingTops.Clear(); linkSeq = 0;
            int pad = Px(28), x = pad, w = Math.Max(Px(120), width - 2 * pad), y = Px(22);
            if (draw) TextRenderer.DrawText(g, topic.Group.ToUpperInvariant(), CaptionFont, new Point(x, y), Palette.Muted, Bare);
            backLink = Rectangle.Empty;
            if (backTo != null)
            {
                string text = "← Back to " + backTo.Title; Size s = TextRenderer.MeasureText(g, text, Palette.Small, Size.Empty, Bare);
                int bw = Math.Min(s.Width, w / 2);
                backLink = new Rectangle(x + w - bw, y - Px(3), bw, s.Height + Px(6));
                if (draw) TextRenderer.DrawText(g, text, Palette.Small, backLink, hoverLink == 2 ? Palette.Text : Palette.Accent, Bare | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
            }
            y += Px(22);
            int h = TextRenderer.MeasureText(g, topic.Title, TitleFont, new Size(w, Int32.MaxValue), Wrap).Height;
            if (draw) TextRenderer.DrawText(g, topic.Title, TitleFont, new Rectangle(x, y, w, h), Palette.Text, Wrap);
            y += h + Px(2);
            if (topic.Lead.Length > 0) y += Inline(g, topic.Lead, Palette.Muted, x, y, w, draw) + Px(10);
            for (int i = 0; i < topic.Blocks.Count; i++)
            {
                HelpBlock b = topic.Blocks[i];
                switch (b.Kind)
                {
                    case HelpBlockKind.Heading:
                        y += Px(12);
                        if (!headingTops.ContainsKey(b.Text)) headingTops[b.Text] = y;
                        h = TextRenderer.MeasureText(g, b.Text, HeadingFont, new Size(w, Int32.MaxValue), Wrap).Height;
                        if (draw) TextRenderer.DrawText(g, b.Text, HeadingFont, new Rectangle(x, y, w, h), Palette.Text, Wrap);
                        y += h + Px(6);
                        break;
                    case HelpBlockKind.Item:
                        int indent = Px(20), dot = Px(6);
                        if (draw) using (var accent = new SolidBrush(Palette.Accent)) g.FillEllipse(accent, x + Px(4), y + LineHeight(g) / 2 - dot / 2 - Px(1), dot, dot);
                        y += Inline(g, b.Text, Palette.Text, x + indent, y, w - indent, draw) + Px(4);
                        if (i + 1 >= topic.Blocks.Count || topic.Blocks[i + 1].Kind != HelpBlockKind.Item) y += Px(8);
                        break;
                    case HelpBlockKind.Note:
                        int inner = Px(14), badge = b.Title.Length > 0 ? Px(20) : 0, seq = linkSeq;
                        int textHeight = Inline(g, b.Text, Palette.Text, x + inner, 0, w - 2 * inner, false);
                        linkSeq = seq;   // the measuring pass numbered this card's links; the drawing pass numbers them again
                        var card = new Rectangle(x, y + Px(4), w, inner * 2 + badge + textHeight);
                        if (draw)
                        {
                            Glyphs.FillRounded(g, Palette.Raised, card, Px(8));
                            Glyphs.DrawRounded(g, Palette.Accent, Math.Max(1F, Px(1)), card, Px(8));
                            if (badge > 0) TextRenderer.DrawText(g, b.Title.ToUpperInvariant(), CaptionFont, new Point(card.X + inner, card.Y + inner), Palette.Accent, Bare);
                        }
                        Inline(g, b.Text, Palette.Text, card.X + inner, card.Y + inner + badge, w - 2 * inner, draw);
                        y = card.Bottom + Px(14);
                        break;
                    default:
                        y += Inline(g, b.Text, Palette.Text, x, y, w, draw) + Px(10);
                        break;
                }
            }
            // Previous / Next: a line, then the two links.
            y += Px(10);
            if (draw) using (var line = new SolidBrush(Palette.Border)) g.FillRectangle(line, x, y, w, Math.Max(1, Px(1)));
            y += Px(14);
            int linkHeight = Px(26);
            previousLink = nextLink = Rectangle.Empty;
            if (previous != null)
            {
                string text = "← " + previous.Title; Size s = TextRenderer.MeasureText(g, text, Body, Size.Empty, Bare);
                previousLink = new Rectangle(x, y, Math.Min(s.Width, w / 2 - Px(8)), linkHeight);
                if (draw) TextRenderer.DrawText(g, text, Body, previousLink, hoverLink == -1 ? Palette.Text : Palette.Accent, Bare | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);
            }
            if (next != null)
            {
                string text = next.Title + " →"; Size s = TextRenderer.MeasureText(g, text, Body, Size.Empty, Bare);
                int lw = Math.Min(s.Width, w / 2 - Px(8));
                nextLink = new Rectangle(x + w - lw, y, lw, linkHeight);
                if (draw) TextRenderer.DrawText(g, text, Body, nextLink, hoverLink == 1 ? Palette.Text : Palette.Accent, Bare | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
            }
            return y + linkHeight + Px(24);
        }

        private int LineHeight(Graphics g) { return TextRenderer.MeasureText(g, "Ag", Body, Size.Empty, Bare).Height + Px(7); }

        // Inline text wrapped word by word in [x, x + width): plain words in `color`, **bold** and `code` in the text colour,
        // links in the accent (the text colour under the pointer); a code span is a raised chip (one chip per span and line).
        // Returns the height used. Drawing records each link word's bounds for the pointer.
        private int Inline(Graphics g, string text, Color color, int x, int y, int width, bool draw)
        {
            int lineHeight = LineHeight(g), baseHeight = TextRenderer.MeasureText(g, "Ag", Body, Size.Empty, Bare).Height;
            int space = TextRenderer.MeasureText(g, "a a", Body, Size.Empty, Bare).Width - TextRenderer.MeasureText(g, "aa", Body, Size.Empty, Bare).Width;
            int chipPad = Px(4), cx = x, cy = y;
            bool pendingSpace = false;
            foreach (HelpRun run in HelpDocument.Runs(text))
            {
                int seq = run.Link != null ? linkSeq++ : -1;
                Font f = run.Code ? CodeFont : run.Bold ? BodyBold : Body;
                Color c = run.Link != null ? (seq == hoverSeq ? Palette.Text : Palette.Accent) : run.Code || run.Bold ? Palette.Text : color;
                int fontHeight = TextRenderer.MeasureText(g, "Ag", f, Size.Empty, Bare).Height;
                int chipRight = -1;   // the right edge of this code span's chip on the current line
                string[] words = run.Text.Split(' ');
                for (int k = 0; k < words.Length; k++)
                {
                    if (k > 0) pendingSpace = true;
                    string word = words[k];
                    if (word.Length == 0) { if (k == 0 && run.Text.Length > 0) pendingSpace = true; continue; }
                    int ww = TextRenderer.MeasureText(g, word, f, Size.Empty, Bare).Width + (run.Code ? 2 * chipPad : 0);
                    int gap = pendingSpace && cx > x ? space : 0;
                    if (cx > x && cx + gap + ww > x + width) { cx = x; cy += lineHeight; gap = 0; chipRight = -1; }
                    int left = cx + gap;
                    if (draw)
                    {
                        if (run.Code)
                        {
                            var chip = new Rectangle(left, cy + (baseHeight - fontHeight) / 2 - Px(2), ww, fontHeight + Px(4));
                            Glyphs.FillRounded(g, Palette.Raised, chip, Px(4));
                            if (chipRight >= 0) using (var b = new SolidBrush(Palette.Raised)) g.FillRectangle(b, chipRight - Px(4), chip.Y, left - chipRight + Px(8), chip.Height);   // one chip across the span
                            chipRight = chip.Right;
                        }
                        TextRenderer.DrawText(g, word, f, new Point(left + (run.Code ? chipPad : 0), cy + (baseHeight - fontHeight) / 2), c, Bare);
                        if (run.Link != null) links.Add(new LinkSpot { Bounds = new Rectangle(left - (gap > 0 ? gap : 0), cy, ww + (gap > 0 ? gap : 0), lineHeight - Px(4)), Seq = seq, Target = run.Link });
                    }
                    else if (run.Code) chipRight = left + ww;
                    cx = left + ww; pendingSpace = false;
                }
                if (run.Text.EndsWith(" ")) pendingSpace = true;
            }
            return cy - y + lineHeight;
        }

        private LinkSpot LinkAt(Point p) { return links.FirstOrDefault(l => l.Bounds.Contains(p)); }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            LinkSpot spot = LinkAt(e.Location);
            int h = backLink.Contains(e.Location) ? 2 : previousLink.Contains(e.Location) ? -1 : nextLink.Contains(e.Location) ? 1 : 0, seq = spot == null ? -1 : spot.Seq;
            if (h != hoverLink || seq != hoverSeq) { hoverLink = h; hoverSeq = seq; Cursor = h != 0 || seq >= 0 ? Cursors.Hand : Cursors.Default; Invalidate(); }
        }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (hoverLink != 0 || hoverSeq >= 0) { hoverLink = 0; hoverSeq = -1; Invalidate(); } }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.XButton1) { if (backTo != null && Back != null) Back(); return; }
            if (e.Button != MouseButtons.Left) return;
            if (backLink.Contains(e.Location)) { if (Back != null) Back(); return; }
            LinkSpot spot = LinkAt(e.Location);
            if (spot != null) { string heading; HelpTopic target = HelpDocument.Find(spot.Target, out heading); if (target != null && Navigate != null) Navigate(target, heading); return; }
            HelpTopic page = previousLink.Contains(e.Location) ? previous : nextLink.Contains(e.Location) ? next : null;
            if (page != null && Page != null) Page(page);
        }
    }
}
