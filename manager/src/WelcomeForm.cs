using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace SlotsInstaller
{
    // 1.0.11 (the user): only what a new user must know, as three topics: using the Manager beside an external mod
    // manager, on its own, and what to do when something breaks. Shown once at the first start (ViewPreferences
    // .WelcomeSeen, so also once after updating from 1.0.10) and from gear → Welcome. Esc, ✕ or Done close it.
    // A borderless card over the dimmed main window (the user: no title bar, no window chrome).
    internal sealed class WelcomeForm : Form
    {
        internal sealed class Topic
        {
            internal string Tab, Title, Lead, Badge = "";
            internal string[] Points;
            internal int Highlight = -1;   // this point is a card with Badge above its text
        }

        internal static readonly Topic[] Topics =
        {
            new Topic { Tab = "As companion", Title = "Use it with your mod manager", Lead = "Keep your external mod manager for replacements. Use this Manager for extra outfits.", Points = new[] {
                "Extra outfit adds a mod as a new costume slot. The original outfit stays in the game.",
                "Extra outfits need REFramework and Slotsloader. If one is missing, click the line under INSTALL AS to get it.",
                "While your mod manager has mods installed, Replace original is off here. Its mods stay its own, so manage them there." } },
            new Topic { Tab = "As standalone", Title = "Use it on its own", Lead = "Let this Manager handle every mod, replacements included.", Highlight = 2, Badge = "Automatic fixes", Points = new[] {
                "Uninstall everything in your external mod manager first. Replace original stays off until it's empty.",
                "Replace original overwrites the game's files. When two mods change the same outfit, the one higher on the Installed page wins.",
                "Every replacement gets a health check, and mods made for an older SF6 are fixed as they install: colour files, texture tables and old file names. Only mods installed with SLM get these fixes." } },
            new Topic { Tab = "Repair", Title = "When something breaks", Lead = "Start with the smallest fix.", Points = new[] {
                "After an SF6 update, a \"Game files changed\" banner appears. Click Reapply to put your replacements back.",
                "Game won't start? Gear → Restore → Remove this Manager's mods. You can undo it.",
                "Still broken? Gear → Restore → Restore game to vanilla removes your mods and your mod manager's. REFramework and Slotsloader installed with SLM stay." } },
        };

        private readonly WelcomePage page;
        private readonly FlatButton next;
        internal int Index { get { return page.Index; } }

        internal static void ShowWelcome(Form owner)
        {
            if (Program.Headless) return;
            // The main window dims behind the card: a black layer at half opacity over its client area, owning the card.
            using (var dim = new Form { FormBorderStyle = FormBorderStyle.None, BackColor = Color.Black, Opacity = 0.5, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Bounds = owner.RectangleToScreen(owner.ClientRectangle) })
            using (var form = new WelcomeForm())
            {
                dim.Show(owner);
                form.ShowDialog(dim);
            }
        }

        internal WelcomeForm()
        {
            SuspendLayout();
            AutoScaleDimensions = new SizeF(96F, 96F); AutoScaleMode = AutoScaleMode.Dpi;
            Text = "Welcome to Slotsloader Manager"; StartPosition = FormStartPosition.CenterParent;
            BackColor = Palette.Surface; ForeColor = Palette.Text; ShowInTaskbar = false; Padding = new Padding(31, 16, 31, 21); FormBorderStyle = FormBorderStyle.None;
            AppBranding.Apply(this); WindowChrome.Round(this);
            // 1.0.11 (the user): ✕ is the only close and the tabs move back, so the footer has just Next / Done; both buttons compact.
            var header = new Panel { Dock = DockStyle.Top, Height = 30, Padding = new Padding(0, 3, 0, 3) };
            var caption = new Label { UseMnemonic = false, Dock = DockStyle.Fill, Font = new Font("Segoe UI", 8F, FontStyle.Bold), ForeColor = Palette.Muted, TextAlign = ContentAlignment.MiddleLeft, Text = "WELCOME TO SLOTSLOADER MANAGER" };
            var dismiss = new FlatButton("✕", false) { Width = 26, Padding = Padding.Empty, Dock = DockStyle.Right, Font = Palette.Tiny, Name = "welcomeDismiss", AccessibleName = "Close the welcome" }; dismiss.Click += delegate { Close(); };
            header.Controls.Add(caption); header.Controls.Add(dismiss);
            page = new WelcomePage { Dock = DockStyle.Fill, Name = "welcomePage" };
            page.IndexChanged += delegate { UpdateButtons(); };
            var footer = new Panel { Dock = DockStyle.Bottom, Height = 46, Padding = new Padding(0, 16, 0, 0) };
            var hint = new Label { UseMnemonic = false, Dock = DockStyle.Fill, ForeColor = Palette.Muted, Font = Palette.Small, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, Text = "Open this again from gear → Welcome" };
            next = new FlatButton("Next", true) { Width = 88, Dock = DockStyle.Right, Font = Palette.Small, Name = "welcomeNext" }; next.Click += delegate { if (page.Index == Topics.Length - 1) Close(); else page.Index++; };
            footer.Controls.Add(hint); footer.Controls.Add(next);
            Controls.Add(page); Controls.Add(header); Controls.Add(footer);
            ClientSize = new Size(722, 520);   // 1.0.11 (the user): a bigger card; the height follows the tallest topic (OnLoad)
            AcceptButton = next;
            KeyPreview = true;
            KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape) { e.Handled = true; Close(); }
                else if (e.KeyCode == Keys.Left || e.KeyCode == Keys.Right) { e.Handled = true; page.Index += e.KeyCode == Keys.Right ? 1 : -1; }
            };
            DarkTheme.Apply(this); ResumeLayout(true); PerformAutoScale();
            UpdateButtons();
            ActiveControl = next;
        }

        // The page takes the height of its tallest topic, so the window never changes size between topics.
        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            int extra = page.TallestHeight() - page.Height;
            if (extra != 0) { ClientSize = new Size(ClientSize.Width, ClientSize.Height + extra); if (Owner != null) CenterToParent(); }
        }

        // A 1 px frame in the border colour (the window has no chrome of its own).
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var pen = new Pen(Palette.Border, Math.Max(1, DpiMetrics.Px(this, 1)))) e.Graphics.DrawRectangle(pen, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
        }

        private void UpdateButtons()
        {
            next.Text = page.Index == Topics.Length - 1 ? "Done" : "Next";
        }
    }

    // One topic: tabs naming all three (click to switch), the title, a short lead and the points. The highlighted point
    // is a raised card with an accent outline and its badge above the text.
    internal sealed class WelcomePage : Control
    {
        private const TextFormatFlags Wrap = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl;
        private const TextFormatFlags Line = TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter;
        private static readonly Font TitleFont = new Font("Segoe UI", 15F, FontStyle.Bold), BadgeFont = new Font("Segoe UI", 8.5F, FontStyle.Bold), Body = new Font("Segoe UI", 10.5F);
        private int index, hover = -1;
        internal event EventHandler IndexChanged;

        internal WelcomePage()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Palette.Surface; AccessibleRole = AccessibleRole.Grouping;
            Describe();
        }

        internal int Index
        {
            get { return index; }
            set
            {
                int next = Math.Max(0, Math.Min(WelcomeForm.Topics.Length - 1, value));
                if (next == index) return;
                index = next; Describe(); Invalidate();
                var handler = IndexChanged; if (handler != null) handler(this, EventArgs.Empty);
            }
        }

        private void Describe()
        {
            WelcomeForm.Topic t = WelcomeForm.Topics[index];
            AccessibleName = t.Tab + ": " + t.Title;
            AccessibleDescription = t.Lead + " " + String.Join(" ", t.Points);
        }

        private int Px(int logical) { return DpiMetrics.Px(this, logical); }
        private int TabsHeight { get { return Px(38); } }
        private Rectangle TabBounds(int i) { int w = ClientSize.Width / WelcomeForm.Topics.Length; return new Rectangle(i * w, 0, i == WelcomeForm.Topics.Length - 1 ? ClientSize.Width - i * w : w, TabsHeight); }
        private int TabAt(Point p) { for (int i = 0; i < WelcomeForm.Topics.Length; i++) if (TabBounds(i).Contains(p)) return i; return -1; }

        internal int TallestHeight()
        {
            using (Graphics g = CreateGraphics())
                return Enumerable.Range(0, WelcomeForm.Topics.Length).Max(i => Lay(g, WelcomeForm.Topics[i], false));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var b = new SolidBrush(BackColor)) g.FillRectangle(b, ClientRectangle);
            Lay(g, WelcomeForm.Topics[index], true);
        }

        // Walks one topic top to bottom at the control's width: draws it when asked, and returns the height it needs.
        private int Lay(Graphics g, WelcomeForm.Topic t, bool draw)
        {
            int width = ClientSize.Width, y = 0;
            if (draw)
            {
                using (var line = new SolidBrush(Palette.Border)) g.FillRectangle(line, 0, TabsHeight - Math.Max(1, Px(1)), width, Math.Max(1, Px(1)));
                for (int i = 0; i < WelcomeForm.Topics.Length; i++)
                {
                    Rectangle r = TabBounds(i);
                    TextRenderer.DrawText(g, WelcomeForm.Topics[i].Tab, Body, new Rectangle(r.X, r.Y, r.Width, r.Height - Px(3)), i == index ? Palette.Accent : i == hover ? Palette.Text : Palette.Muted, Line);
                    if (i == index) using (var accent = new SolidBrush(Palette.Accent)) g.FillRectangle(accent, r.X + Px(24), r.Bottom - Px(2), r.Width - Px(48), Px(2));
                }
            }
            y += TabsHeight + Px(20);
            int h = TextRenderer.MeasureText(g, t.Title, TitleFont, new Size(width, Int32.MaxValue), Wrap).Height;
            if (draw) TextRenderer.DrawText(g, t.Title, TitleFont, new Rectangle(0, y, width, h), Palette.Text, Wrap);
            y += h + Px(4);
            h = TextRenderer.MeasureText(g, t.Lead, Body, new Size(width, Int32.MaxValue), Wrap).Height;
            if (draw) TextRenderer.DrawText(g, t.Lead, Body, new Rectangle(0, y, width, h), Palette.Muted, Wrap);
            y += h + Px(14);
            for (int k = 0; k < t.Points.Length; k++)
            {
                bool card = k == t.Highlight;
                int inset = card ? Px(16) : 0, bullet = Px(22), textLeft = inset + bullet, textWidth = width - textLeft - inset;
                int top = y + (card ? Px(8) : 0), textTop = top + Px(card ? 14 : 12), badge = card ? Px(20) : 0;
                int textHeight = TextRenderer.MeasureText(g, t.Points[k], Body, new Size(textWidth, Int32.MaxValue), Wrap).Height;
                int bottom = textTop + badge + textHeight + Px(card ? 14 : 12);
                if (draw)
                {
                    if (card)
                    {
                        var rect = new RectangleF(Px(1), top, width - Px(2), bottom - top);
                        Glyphs.FillRounded(g, Palette.Raised, rect, Px(8));
                        Glyphs.DrawRounded(g, Palette.Accent, Math.Max(1F, Px(1)), rect, Px(8));
                        TextRenderer.DrawText(g, t.Badge.ToUpperInvariant(), BadgeFont, new Point(textLeft, textTop), Palette.Accent, TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
                    }
                    else if (k > 0 && k - 1 != t.Highlight)
                        using (var line = new SolidBrush(Palette.Border)) g.FillRectangle(line, 0, y, width, Math.Max(1, Px(1)));
                    int dot = Px(6);
                    using (var accent = new SolidBrush(Palette.Accent)) g.FillEllipse(accent, inset + Px(3), textTop + badge + Px(9), dot, dot);
                    TextRenderer.DrawText(g, t.Points[k], Body, new Rectangle(textLeft, textTop + badge, textWidth, textHeight), Palette.Text, Wrap);
                }
                y = bottom + (card ? Px(8) : 0);
            }
            return y;
        }

        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); int h = TabAt(e.Location); if (h != hover) { hover = h; Cursor = h >= 0 && h != index ? Cursors.Hand : Cursors.Default; Invalidate(); } }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = -1; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); int i = TabAt(e.Location); if (e.Button == MouseButtons.Left && i >= 0) Index = i; }
    }
}
