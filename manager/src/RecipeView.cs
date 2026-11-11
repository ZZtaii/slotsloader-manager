using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace SlotsInstaller
{
    // 1.0.18 (the user, about the first version, one centred sentence per layer: "kinda hard to make sense of"): what
    // a recipe is made of, where a mod shows its options. One card per layer, the winner first.
    // 1.0.18 (the user: "it could be cool if user could enable disable parts of outfit recipe", then about the first
    // ticks, small boxes in a grey block: "I think you can do the UI better"): a card has a header row (the layer's
    // tick, the mod's name, what is taken from it as a chip, its place in the load order) and one row per option with
    // its own tick. The rows light up under the pointer and the whole row is the click. A layer that is on has an
    // accent rail; one that is off folds to its header, dimmed, with an "Off" chip. Unticked, a layer or an option
    // stays in the recipe and is left out at install; a click asks the owner to change the recipe (Toggle).
    // Taller than the space, it scrolls with its host.
    internal sealed class RecipeLayersView : Control
    {
        private sealed class Line { internal string Text = "", Id; internal bool Off; }
        private sealed class Block { internal string Tag = "", Title = "", Parts = ""; internal List<Line> Lines = new List<Line>(); internal bool Base, Off, Wins; internal RecipeLayer Layer; }
        private sealed class Hit { internal Rectangle Area; internal RecipeLayer Layer; internal string Id; }
        private readonly List<Block> blocks = new List<Block>();
        private readonly List<Hit> hits = new List<Hit>();
        private string note = "";
        private Point pointer = new Point(-1, -1);
        private static readonly Color Dim = Color.FromArgb(104, 110, 122);   // what is turned off: dimmer than the muted option text
        private static readonly Color Hover = Color.FromArgb(46, 50, 58);    // a row under the pointer, a shade over Palette.Raised
        private const int HeaderHeight = 44, LineHeight = 30, CardBottom = 8, CardGap = 8, CaptionHeight = 30, NoteHeight = 52, Box = 16, Inset = 16;
        // (layer, option id or null for the layer itself): the tick was clicked.
        internal event Action<RecipeLayer, string> Toggle;

        internal RecipeLayersView()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Palette.Surface;
        }
        private int Px(int logical) { return DpiMetrics.Px(this, logical); }

        internal void SetRecipe(OutfitRecipe recipe)
        {
            blocks.Clear(); hits.Clear(); note = "";
            if (recipe != null)
            {
                // Load order, the winner first: the user asked for no "base", every mod is a numbered layer.
                List<RecipeLayer> topDown = Enumerable.Reverse(new[] { recipe.Base }.Concat(recipe.Parts)).ToList();
                for (int i = 0; i < topDown.Count; i++)
                {
                    RecipeLayer layer = topDown[i];
                    List<string> own = layer.ShownOptions.ToList(), ids = layer.SelectedIds ?? new List<string>();
                    bool whole = own.Count == 0 || (own.Count == 1 && String.Equals(own[0], layer.ModName, StringComparison.OrdinalIgnoreCase));
                    var block = new Block { Tag = "#" + (i + 1), Base = whole, Title = layer.ModName, Layer = layer, Off = layer.Off, Parts = layer.Parts != null && layer.Parts.Count > 0 ? OutfitParts.Labels(layer.Parts) + " only" : "" };
                    if (whole) block.Lines.Add(new Line { Text = "The whole mod, as it installs by default" });
                    // An option has a tick of its own when the names and the options line up (a recipe saved by the builder).
                    else for (int k = 0; k < own.Count; k++) block.Lines.Add(new Line { Text = own[k], Id = own.Count == ids.Count ? ids[k] : null, Off = own.Count == ids.Count && layer.IsOff(ids[k]) });
                    blocks.Add(block);
                }
                // The top layer that is on wins what it shares with the others; said on it when there is more than one.
                List<Block> on = blocks.Where(b => !b.Off).ToList();
                if (on.Count > 1) on[0].Wins = true;
                note = (recipe.AnyOff ? "Unticked layers and options stay in the recipe and are left out when it installs. " : "Untick a layer or an option to leave it out when the recipe installs. ")
                    + (topDown.Count == 1 ? "" : "A layer higher in the list wins the files it shares with the ones below it.");
            }
            warnings.Clear();
            Height = ContentHeight; Invalidate();
        }
        // What will not work in game (MaterialCheck.cs), in the accent colour above the layers.
        private readonly List<string> warnings = new List<string>();
        internal string WarningsForTest { get { return String.Join(" | ", warnings); } }
        internal void SetWarnings(IEnumerable<string> list)
        {
            warnings.Clear(); if (list != null) warnings.AddRange(list);
            Height = ContentHeight; Invalidate();
        }
        private const TextFormatFlags Wrapped = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix;
        private int WarningWidth { get { return Math.Max(Px(120), Width - Px(12) - Px(28)); } }
        private int WarningHeight(string text) { return TextRenderer.MeasureText(text, Palette.Small, new Size(WarningWidth, Int32.MaxValue), Wrapped).Height + Px(20); }
        private int WarningsHeight { get { return warnings.Sum(w => WarningHeight(w) + Px(6)); } }
        protected override void OnResize(EventArgs e) { base.OnResize(e); if (warnings.Count > 0 && Height != ContentHeight) Height = ContentHeight; }
        // A layer that is off shows its header only.
        private int CardHeight(Block b) { return Px(HeaderHeight) + (b.Off ? 0 : b.Lines.Count * Px(LineHeight) + Px(CardBottom)); }
        internal int ContentHeight { get { return Px(CaptionHeight) + WarningsHeight + blocks.Sum(b => CardHeight(b) + Px(CardGap)) + Px(NoteHeight); } }
        // "#1 Hair Mod [Hair B; Clips] / #2 Juri Base"; a layer or an option that is off is marked "(off)".
        internal string TextForTest { get { return String.Join(" / ", blocks.Select(b => b.Tag + " " + b.Title + (b.Off ? " (off)" : "") + (b.Base ? "" : " [" + String.Join("; ", (b.Parts.Length > 0 ? new[] { b.Parts } : new string[0]).Concat(b.Lines.Select(l => l.Text + (l.Off ? " (off)" : "")))) + "]"))); } }
        // Clicks the tick of layer `index` (the winner is 0), or of its option `id`.
        internal void ClickTickForTest(int index, string id) { if (index >= 0 && index < blocks.Count && Toggle != null) Toggle(blocks[index].Layer, id); }

        private static GraphicsPath Rounded(Rectangle r, int radius)
        {
            var path = new GraphicsPath(); int d = Math.Max(2, radius * 2);
            path.AddArc(r.Left, r.Top, d, d, 180, 90); path.AddArc(r.Right - d - 1, r.Top, d, d, 270, 90);
            path.AddArc(r.Right - d - 1, r.Bottom - d - 1, d, d, 0, 90); path.AddArc(r.Left, r.Bottom - d - 1, d, d, 90, 90);
            path.CloseFigure(); return path;
        }
        private void Tick(Graphics g, Rectangle box, bool on, bool dim)
        {
            using (GraphicsPath shape = Rounded(box, Px(3)))
            {
                using (Brush fill = new SolidBrush(on ? (dim ? Palette.Border : Palette.Accent) : Palette.Surface)) g.FillPath(fill, shape);
                using (Pen pen = new Pen(on ? (dim ? Palette.Border : Palette.Accent) : dim ? Palette.Border : Palette.Muted)) g.DrawPath(pen, shape);
            }
            if (!on) return;
            using (Pen mark = new Pen(dim ? Palette.Muted : Palette.Background, Math.Max(1.5f, Px(2))) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                g.DrawLines(mark, new[] { new PointF(box.Left + box.Width * 0.24f, box.Top + box.Height * 0.52f), new PointF(box.Left + box.Width * 0.43f, box.Top + box.Height * 0.70f), new PointF(box.Left + box.Width * 0.77f, box.Top + box.Height * 0.30f) });
        }
        // A small rounded label, right-aligned at `right`; returns its left edge.
        private int Chip(Graphics g, string text, int right, int top, int rowHeight, Color fore, Color back)
        {
            const TextFormatFlags flat = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter;
            int width = TextRenderer.MeasureText(g, text, Palette.Tiny, Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine).Width + Px(14), height = Px(22);
            var area = new Rectangle(right - width, top + (rowHeight - height) / 2, width, height);
            using (GraphicsPath shape = Rounded(area, Px(4))) using (Brush fill = new SolidBrush(back)) g.FillPath(fill, shape);
            TextRenderer.DrawText(g, text, Palette.Tiny, area, fore, flat);
            return area.Left - Px(6);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            hits.Clear();
            using (Brush bg = new SolidBrush(Palette.Surface)) g.FillRectangle(bg, ClientRectangle);
            const TextFormatFlags one = TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter;
            int left = Px(4), width = Math.Max(1, Width - Px(12)), y = 0, box = Px(Box), inset = Px(Inset);
            TextRenderer.DrawText(g, "LOAD ORDER · THE TOP ONE WINS", Palette.Tiny, new Rectangle(left, y, width, Px(CaptionHeight)), Palette.Muted, one);
            y += Px(CaptionHeight);
            foreach (string warning in warnings)
            {
                int warningHeight = WarningHeight(warning);
                var area = new Rectangle(left, y, width, warningHeight);
                using (Brush fill = new SolidBrush(NoticeBanner.Tint)) g.FillRectangle(fill, area);
                using (Brush rail = new SolidBrush(Palette.Accent)) g.FillRectangle(rail, area.Left, area.Top, Px(3), area.Height);
                TextRenderer.DrawText(g, warning, Palette.Small, new Rectangle(area.Left + Px(14), area.Top + Px(10), WarningWidth, warningHeight - Px(20)), Palette.Accent, Wrapped);
                y += warningHeight + Px(6);
            }
            foreach (Block b in blocks)
            {
                int height = CardHeight(b);
                var card = new Rectangle(left, y, width, height);
                using (Brush fill = new SolidBrush(b.Off ? Palette.Surface : Palette.Raised)) g.FillRectangle(fill, card);
                if (b.Off) using (Pen edge = new Pen(Palette.Border)) g.DrawRectangle(edge, card.Left, card.Top, card.Width - 1, card.Height - 1);
                // The header: the whole row is the layer's tick.
                var header = new Rectangle(card.Left, card.Top, card.Width, Px(HeaderHeight));
                if (header.Contains(pointer)) using (Brush lit = new SolidBrush(b.Off ? Palette.Raised : Hover)) g.FillRectangle(lit, header);
                using (Brush rail = new SolidBrush(b.Off ? Palette.Border : Palette.Accent)) g.FillRectangle(rail, card.Left, card.Top, Px(3), card.Height);
                hits.Add(new Hit { Area = header, Layer = b.Layer, Id = null });
                int x = card.Left + inset;
                Tick(g, new Rectangle(x, header.Top + (header.Height - box) / 2, box, box), !b.Off, false);
                // Right to left: the place in the load order, then what the layer is.
                int right = card.Right - Px(12);
                var rank = new Rectangle(right - Px(34), header.Top, Px(34), header.Height);
                TextRenderer.DrawText(g, b.Tag, Palette.Small, rank, b.Off ? Dim : Palette.Muted, one | TextFormatFlags.Right);
                right = rank.Left - Px(8);
                if (b.Off) right = Chip(g, "Off", right, header.Top, header.Height, Palette.Muted, Palette.Raised);
                else if (b.Wins) right = Chip(g, "Wins shared files", right, header.Top, header.Height, Palette.Muted, Palette.Surface);
                if (b.Parts.Length > 0) right = Chip(g, b.Parts, right, header.Top, header.Height, b.Off ? Dim : Palette.Accent, b.Off ? Palette.Raised : NoticeBanner.Tint);
                int titleLeft = x + box + Px(12);
                TextRenderer.DrawText(g, b.Title, Palette.Strong, new Rectangle(titleLeft, header.Top, Math.Max(1, right - titleLeft), header.Height), b.Off ? Dim : Palette.Text, one);
                if (!b.Off)
                {
                    int top = header.Bottom;
                    foreach (Line line in b.Lines)
                    {
                        var row = new Rectangle(card.Left + Px(3), top, card.Width - Px(3), Px(LineHeight));
                        int textLeft = titleLeft;
                        if (line.Id != null)
                        {
                            if (row.Contains(pointer)) using (Brush lit = new SolidBrush(Hover)) g.FillRectangle(lit, row);
                            Tick(g, new Rectangle(titleLeft, top + (row.Height - box) / 2, box, box), !line.Off, false);
                            hits.Add(new Hit { Area = row, Layer = b.Layer, Id = line.Id });
                            textLeft += box + Px(10);
                        }
                        TextRenderer.DrawText(g, line.Text, Palette.Small, new Rectangle(textLeft, top, Math.Max(1, card.Right - Px(12) - textLeft), row.Height), line.Off ? Dim : line.Id != null ? Palette.Text : Palette.Muted, one);
                        top += Px(LineHeight);
                    }
                }
                y += height + Px(CardGap);
            }
            TextRenderer.DrawText(g, note, Palette.Small, new Rectangle(left, y + Px(2), width, Px(NoteHeight) - Px(2)), Palette.Muted, TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
        }

        private Hit HitAt(Point p) { return hits.LastOrDefault(h => h.Area.Contains(p)); }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Hit before = HitAt(pointer), now = HitAt(e.Location);
            pointer = e.Location;
            Cursor = now != null ? Cursors.Hand : Cursors.Default;
            if (!ReferenceEquals(before, now)) Invalidate();
        }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); pointer = new Point(-1, -1); Cursor = Cursors.Default; Invalidate(); }
        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button != MouseButtons.Left) return;
            Hit hit = HitAt(e.Location);
            if (hit != null && Toggle != null) Toggle(hit.Layer, hit.Id);
        }
    }
}
