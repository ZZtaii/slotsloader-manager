using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace SlotsInstaller
{
    // 1.0.15 (the user): short tours that point at the real controls, one at a time: the Mod library (after the
    // welcome), the Installed page and Settings (the first time each is opened), and again from gear → Take the tour.
    // Each step dims the window except the control it explains (outlined in the accent colour) and shows a card with
    // the step count, a title, a few lines and Skip tour / Back / Next. The card sits where it hides the least: over
    // the preview picture, the empty part of a list, or a section that isn't part of this step (MainFormTours.cs).
    internal sealed class TourStep
    {
        internal string Title;
        internal string[] Body;                         // "- " starts a bullet; other lines are paragraphs
        internal Action Prepare;                        // switches the page, tab or selection this step needs
        internal Func<Rectangle> Target;                // the control, in the main window's client coordinates; Empty skips the step
        internal Func<int> Width;                       // the card's width in pixels
        internal Func<Rectangle, Size, Point> Place;    // (the outlined area, the card's size) → the card's top-left
    }

    internal sealed class Tour
    {
        internal string Id, Name;
        internal List<TourStep> Steps = new List<TourStep>();
    }

    internal enum TourPointer { None, Left, Right, Top, Bottom }

    // The card. Run() shows it with a black layer (WelcomeForm.DimOpacity) over the main window, a hole where the
    // control is, and an accent ring around the hole; the checks drive ShowStep directly and render it themselves.
    internal sealed class TourForm : Form
    {
        private static readonly Font StepFont = new Font("Segoe UI", 9.5F, FontStyle.Bold), TitleFont = new Font("Segoe UI Semibold", 16F), BodyFont = new Font("Segoe UI", 12F), ButtonFont = new Font("Segoe UI Semibold", 11F);
        private const TextFormatFlags Wrap = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl;
        private readonly MainForm host;
        private readonly List<TourStep> steps;
        private readonly Form dim, ring;
        private readonly FlatButton back, next;
        private readonly Label skip;
        private int index = -1;
        private Rectangle card;   // the card's body inside this form (the pointer is outside it)

        internal int Index { get { return index; } }
        internal int Count { get { return steps.Count; } }
        internal TourStep Current { get { return index >= 0 ? steps[index] : null; } }
        internal Rectangle Hole { get; private set; }          // main window client coordinates
        internal Rectangle CardBounds { get; private set; }    // the card's body, main window client coordinates
        internal TourPointer Pointer { get; private set; }
        internal Point PointerTip { get; private set; }        // main window client coordinates
        internal GraphicsPath Shape { get; private set; }      // this form's outline (card and pointer), form coordinates

        // The steps that apply now (each one prepared in turn: a step whose control isn't there is left out).
        internal static List<TourStep> Applicable(MainForm host, Tour tour)
        {
            var list = new List<TourStep>();
            foreach (TourStep step in tour.Steps)
            {
                if (step.Prepare != null) step.Prepare();
                Settle(host);
                if (!step.Target().IsEmpty) list.Add(step);
            }
            return list;
        }

        internal static void Run(MainForm host, Tour tour)
        {
            if (Program.Headless || tour == null) return;
            List<TourStep> steps = Applicable(host, tour);
            if (steps.Count == 0) return;
            Rectangle client = host.RectangleToScreen(host.ClientRectangle);
            using (var dim = new Form { FormBorderStyle = FormBorderStyle.None, BackColor = Color.Black, Opacity = WelcomeForm.DimOpacity, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Bounds = client })
            using (var ring = new Form { FormBorderStyle = FormBorderStyle.None, BackColor = Palette.Accent, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Bounds = new Rectangle(client.Location, new Size(1, 1)) })
            using (var form = new TourForm(host, steps, dim, ring))
            {
                form.ShowStep(0);
                dim.Show(host); ring.Show(dim);
                form.ShowDialog(ring);
            }
        }

        internal TourForm(MainForm host, List<TourStep> steps) : this(host, steps, null, null) { }

        private TourForm(MainForm host, List<TourStep> steps, Form dim, Form ring)
        {
            this.host = host; this.steps = steps; this.dim = dim; this.ring = ring;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            FormBorderStyle = FormBorderStyle.None; StartPosition = FormStartPosition.Manual; ShowInTaskbar = false;
            BackColor = Palette.Surface; ForeColor = Palette.Text; Text = "Tour";
            AppBranding.Apply(this);
            skip = new Label { UseMnemonic = false, AutoSize = false, Text = "Skip tour", Font = ButtonFont, ForeColor = Palette.Muted, BackColor = Palette.Surface, TextAlign = ContentAlignment.MiddleLeft, Cursor = Cursors.Hand, Name = "tourSkip", AccessibleName = "Skip tour", AccessibleRole = AccessibleRole.PushButton };
            skip.MouseEnter += delegate { skip.ForeColor = Palette.Text; }; skip.MouseLeave += delegate { skip.ForeColor = Palette.Muted; };
            skip.Click += delegate { Close(); };
            back = new FlatButton("Back", false) { Font = ButtonFont, Name = "tourBack", Padding = Padding.Empty };
            back.Click += delegate { Go(-1); };
            next = new FlatButton("Next", true) { Font = ButtonFont, Name = "tourNext", Padding = Padding.Empty };
            next.Click += delegate { if (index == steps.Count - 1) Close(); else Go(1); };
            Controls.Add(skip); Controls.Add(back); Controls.Add(next);
            AcceptButton = next;
            KeyPreview = true;
            KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape) { e.Handled = true; Close(); }
                else if (e.KeyCode == Keys.Right) { e.Handled = true; if (index < steps.Count - 1) Go(1); }
                else if (e.KeyCode == Keys.Left) { e.Handled = true; if (index > 0) Go(-1); }
            };
            DarkTheme.Apply(this);
        }

        private int Px(int logical) { return DpiMetrics.Px(host, logical); }

        private static void Settle(Control root)
        {
            root.PerformLayout();
            foreach (Control c in root.Controls) Settle(c);
        }

        private void Go(int delta) { ShowStep(Math.Max(0, Math.Min(steps.Count - 1, index + delta))); }

        // Prepares the step, cuts the hole, places the card and the pointer, and moves the layers.
        internal void ShowStep(int i)
        {
            index = i;
            TourStep step = steps[i];
            if (step.Prepare != null) step.Prepare();
            Settle(host); host.Update();
            Rectangle client = host.ClientRectangle;
            Rectangle hole = step.Target(); hole.Inflate(Px(6), Px(6)); hole.Intersect(client);
            Hole = hole;
            int width = Math.Max(Px(300), Math.Min(step.Width(), client.Width - Px(16)));
            Size size = new Size(width, MeasureHeight(step, width));
            Rectangle body = Clamp(new Rectangle(step.Place(hole, size), size), client);
            if (body.IntersectsWith(hole))
            {
                // The preferred spot is too small here (a small window): beside the hole, wherever it fits.
                int gap = Px(16);
                Point[] tries = { new Point(hole.Right + gap, hole.Top), new Point(hole.Left - gap - size.Width, hole.Top), new Point(hole.Left, hole.Bottom + gap), new Point(hole.Left, hole.Top - gap - size.Height) };
                foreach (Point p in tries) { Rectangle r = Clamp(new Rectangle(p, size), client); if (!r.IntersectsWith(hole)) { body = r; break; } }
            }
            CardBounds = body;
            PlacePointer(body, hole);
            int tip = Px(10);
            Rectangle bounds = body;
            if (Pointer == TourPointer.Left) { bounds.X -= tip; bounds.Width += tip; }
            else if (Pointer == TourPointer.Right) bounds.Width += tip;
            else if (Pointer == TourPointer.Top) { bounds.Y -= tip; bounds.Height += tip; }
            else if (Pointer == TourPointer.Bottom) bounds.Height += tip;
            card = new Rectangle(body.X - bounds.X, body.Y - bounds.Y, body.Width, body.Height);
            Bounds = host.RectangleToScreen(bounds);
            SetShape(bounds);
            LayoutButtons(step);
            AccessibleName = "Tour step " + (i + 1) + " of " + steps.Count + ": " + step.Title;
            AccessibleDescription = String.Join(" ", step.Body.Select(b => b.StartsWith("- ") ? b.Substring(2) : b));
            if (dim != null) MoveLayers(hole);
            Invalidate();
        }

        private Rectangle Clamp(Rectangle r, Rectangle client)
        {
            int m = Px(8);
            r.X = Math.Max(client.Left + m, Math.Min(r.X, client.Right - m - r.Width));
            r.Y = Math.Max(client.Top + m, Math.Min(r.Y, client.Bottom - m - r.Height));
            return r;
        }

        // A pointer only when the card sits right next to the control; farther away the outline is enough.
        private void PlacePointer(Rectangle body, Rectangle hole)
        {
            int reach = Px(60), edge = Px(26);
            Pointer = TourPointer.None; PointerTip = Point.Empty;
            int cy = Math.Max(body.Top + edge, Math.Min(body.Bottom - edge, hole.Top + hole.Height / 2));
            int cx = Math.Max(body.Left + edge, Math.Min(body.Right - edge, hole.Left + hole.Width / 2));
            bool rows = hole.Bottom > body.Top + edge && hole.Top < body.Bottom - edge, columns = hole.Right > body.Left + edge && hole.Left < body.Right - edge;
            if (rows && hole.Right <= body.Left && body.Left - hole.Right <= reach) { Pointer = TourPointer.Left; PointerTip = new Point(body.Left - Px(10), cy); }
            else if (rows && hole.Left >= body.Right && hole.Left - body.Right <= reach) { Pointer = TourPointer.Right; PointerTip = new Point(body.Right + Px(10), cy); }
            else if (columns && hole.Bottom <= body.Top && body.Top - hole.Bottom <= reach) { Pointer = TourPointer.Top; PointerTip = new Point(cx, body.Top - Px(10)); }
            else if (columns && hole.Top >= body.Bottom && hole.Top - body.Bottom <= reach) { Pointer = TourPointer.Bottom; PointerTip = new Point(cx, body.Bottom + Px(10)); }
        }

        private void SetShape(Rectangle bounds)
        {
            var path = Glyphs.Rounded(new RectangleF(card.X, card.Y, card.Width, card.Height), Px(12));
            if (Pointer != TourPointer.None)
            {
                Point tip = new Point(PointerTip.X - bounds.X, PointerTip.Y - bounds.Y); int h = Px(10);
                Point[] tri = Pointer == TourPointer.Left ? new[] { new Point(card.Left + 1, tip.Y - h), tip, new Point(card.Left + 1, tip.Y + h) }
                    : Pointer == TourPointer.Right ? new[] { new Point(card.Right - 1, tip.Y - h), tip, new Point(card.Right - 1, tip.Y + h) }
                    : Pointer == TourPointer.Top ? new[] { new Point(tip.X - h, card.Top + 1), tip, new Point(tip.X + h, card.Top + 1) }
                    : new[] { new Point(tip.X - h, card.Bottom - 1), tip, new Point(tip.X + h, card.Bottom - 1) };
                path.AddPolygon(tri);
            }
            path.FillMode = FillMode.Winding;
            if (Shape != null) Shape.Dispose();
            Shape = path;
            Region old = Region; Region = new Region(path); if (old != null) old.Dispose();
        }

        private void LayoutButtons(TourStep step)
        {
            int pad = Px(24), h = Px(38), bottom = card.Bottom - pad - h;
            next.Text = index == steps.Count - 1 ? "Done" : "Next";
            next.Bounds = new Rectangle(card.Right - pad - Px(92), bottom, Px(92), h);
            back.Visible = index > 0;
            back.Bounds = new Rectangle(next.Left - Px(10) - Px(84), bottom, Px(84), h);
            using (Graphics g = CreateGraphics()) skip.Bounds = new Rectangle(card.Left + pad, bottom, TextRenderer.MeasureText(g, skip.Text, ButtonFont).Width + Px(8), h);
            ActiveControl = next;
        }

        // The dim layer keeps a hole over the control; the ring traces it in the accent colour.
        private void MoveLayers(Rectangle hole)
        {
            Rectangle client = host.ClientRectangle;
            dim.Bounds = host.RectangleToScreen(client);
            using (var cut = new GraphicsPath { FillMode = FillMode.Alternate })
            {
                cut.AddRectangle(client);
                using (GraphicsPath h = Glyphs.Rounded(hole, Px(8))) cut.AddPath(h, false);
                Region old = dim.Region; dim.Region = new Region(cut); if (old != null) old.Dispose();
            }
            int w = Math.Max(2, Px(3));
            Rectangle outer = Rectangle.Inflate(hole, w, w);
            ring.Bounds = host.RectangleToScreen(outer);
            using (var band = new GraphicsPath { FillMode = FillMode.Alternate })
            {
                using (GraphicsPath o = Glyphs.Rounded(new RectangleF(0, 0, outer.Width, outer.Height), Px(8) + w)) band.AddPath(o, false);
                using (GraphicsPath i = Glyphs.Rounded(new RectangleF(w, w, hole.Width, hole.Height), Px(8))) band.AddPath(i, false);
                Region old = ring.Region; ring.Region = new Region(band); if (old != null) old.Dispose();
            }
        }

        // Card layout, top to bottom: STEP n OF m and the dots, the title, the lines, then the buttons' row.
        internal int MeasureHeight(TourStep step, int width) { using (Graphics g = CreateGraphics()) return Lay(g, step, new Rectangle(0, 0, width, 0), false); }

        private int Lay(Graphics g, TourStep step, Rectangle body, bool draw)
        {
            int pad = Px(24), x = body.Left + pad, textWidth = body.Width - 2 * pad, y = body.Top + pad;
            if (draw)
            {
                TextRenderer.DrawText(g, "STEP " + (index + 1) + " OF " + steps.Count, StepFont, new Point(x, y), Palette.Muted, TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
                int dx = body.Right - pad;
                for (int d = steps.Count - 1; d >= 0; d--)
                {
                    int w = d == index ? Px(20) : Px(8); dx -= w;
                    Color c = d == index ? Palette.Accent : d < index ? Color.FromArgb(140, Palette.Accent) : Palette.Border;
                    Glyphs.FillRounded(g, c, new RectangleF(dx, y + Px(5), w, Px(8)), Px(4));
                    dx -= Px(6);
                }
            }
            y += Px(28);
            int h = TextRenderer.MeasureText(g, step.Title, TitleFont, new Size(textWidth, Int32.MaxValue), Wrap).Height;
            if (draw) TextRenderer.DrawText(g, step.Title, TitleFont, new Rectangle(x, y, textWidth, h), Palette.Text, Wrap);
            y += h + Px(8);
            foreach (string line in step.Body)
            {
                bool bullet = line.StartsWith("- ");
                string text = bullet ? line.Substring(2) : line;
                int indent = bullet ? Px(18) : 0;
                h = TextRenderer.MeasureText(g, text, BodyFont, new Size(textWidth - indent, Int32.MaxValue), Wrap).Height;
                if (draw)
                {
                    if (bullet) using (var dot = new SolidBrush(Palette.Accent)) { g.SmoothingMode = SmoothingMode.AntiAlias; g.FillEllipse(dot, x + Px(2), y + Px(9), Px(6), Px(6)); }
                    TextRenderer.DrawText(g, text, BodyFont, new Rectangle(x + indent, y, textWidth - indent, h), Color.FromArgb(214, 217, 223), Wrap);
                }
                y += h + Px(bullet ? 6 : 8);
            }
            y += Px(14) + Px(38) + pad;
            return y - body.Top;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            using (var b = new SolidBrush(Palette.Surface)) g.FillRectangle(b, ClientRectangle);
            Glyphs.DrawRounded(g, Palette.Border, Math.Max(1, Px(1)), new RectangleF(card.X + 0.5F, card.Y + 0.5F, card.Width - 1, card.Height - 1), Px(12));
            if (index >= 0) Lay(g, steps[index], card, true);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && Shape != null) { Shape.Dispose(); Shape = null; }
            base.Dispose(disposing);
        }
    }
}
