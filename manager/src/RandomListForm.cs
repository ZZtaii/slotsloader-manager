using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace SlotsInstaller
{
    // 1.0.24: "Random list" (the Installed page's toolbar, beside the other page tools; RandomList.cs). One fighter at a
    // time: Random as before, or the fighter's own list In order or Shuffled. A row per outfit, dragged into the list's
    // order by its grip: tick it into the list, how many times it comes up, and the colours it may wear, painted from the
    // loader's swatches (none ticked: any colour). A game outfit another mod replaces carries that mod's name. Save writes
    // random_list.json at once (the plugin reads it at the next draw) and offers Undo.
    internal sealed partial class MainForm
    {
        private FlatButton randomListButton;   // the Installed page's toolbar (MainFormHistory.cs AttachHistoryButton)
        private static readonly Regex ReplacedModelFolder = new Regex(@"^natives/stm/(?:streaming/)?product/model/esf/(esf\d{3})/(\d{3})/", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        // The game outfits of a fighter that a Replace original mod replaces now: costume number -> the mod's name (the
        // enabled one highest in priority when several do, as it wins)
        private Dictionary<int, string> ReplacedOutfits(string fighterDir)
        {
            var map = new Dictionary<int, string>();
            try
            {
                GameReference tables = GameReference.Tables(config.GameRoot);
                foreach (ReplaceMod mod in ReplaceEngine.ForConfig(config).LoadState().Mods.Where(m => m.Enabled))
                    foreach (ModFile file in mod.Files ?? new List<ModFile>())
                    {
                        Match m = ReplacedModelFolder.Match((file.Path ?? "").Replace('\\', '/').TrimStart('/'));
                        if (!m.Success || m.Groups[2].Value == "000" || !String.Equals(m.Groups[1].Value, fighterDir, StringComparison.OrdinalIgnoreCase)) continue;
                        // state.Mods is in priority order, #1 first: the first mod found for an outfit is the one that wins
                        int costume; if (tables.CostumeByFolder.TryGetValue(m.Groups[1].Value.ToLowerInvariant() + "/" + m.Groups[2].Value, out costume) && !map.ContainsKey(costume)) map[costume] = mod.Name;
                    }
            }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); }
            return map;
        }

        // 1.0.24 (the user: "by default just open last opened fighter"): the fighter the window showed when it was last closed,
        // saved or not, kept in view-settings.json; never opened yet: the window opens its fighter picker
        private string randomListFighter;
        internal string LastRandomListFighter
        {
            get { return !String.IsNullOrEmpty(randomListFighter) ? randomListFighter : viewPreferences != null && viewPreferences.RandomListFighter != null ? viewPreferences.RandomListFighter : ""; }
        }

        internal void ShowRandomList()
        {
            if (busy || String.IsNullOrWhiteSpace(config.GameRoot)) return;
            string start = LastRandomListFighter;   // none yet: the window opens its fighter picker
            string game = config.GameRoot;
            Dictionary<string, RandomPlan> plans; List<string> names;
            bool chosen = RandomListForm.Choose(this, game, start, GridFighters(), filterPanel.Grid.Cards, OutfitOrder.Counts(game), ReplacedOutfits, out plans, out names,
                delegate(string shown) { if (!String.IsNullOrEmpty(shown) && shown != randomListFighter) { randomListFighter = shown; QueueViewSave(); } });
            if (!chosen) return;
            string who = names.Count == 1 ? names[0] + "'s" : String.Join(", ", names.Take(names.Count - 1)) + " and " + names[names.Count - 1] + "'s";
            string backups = System.IO.Path.Combine(AppConfig.DataDir, "random-list-undo");
            Work("Saving " + who + " Random list" + (names.Count == 1 ? "" : "s") + "…", delegate { return RandomList.Save(game, plans, backups); }, delegate(string backup)
            {
                if (backup == null) { SetPageStatus(installedPage, "The Random list is unchanged.", Palette.Muted); return; }
                string done = names.Count == 1 && plans.Values.First() == null ? names[0] + "'s Random draws from every outfit again"
                    : who + " Random list" + (names.Count == 1 ? "" : "s") + " saved";
                OfferUndo(done, delegate
                {
                    Work("Putting " + who + " Random list" + (names.Count == 1 ? "" : "s") + " back…", delegate { RandomList.Restore(game, backup); return true; },
                        delegate(bool ok) { RefreshInstalled("✓ Undone: " + done + "."); });
                });
                RefreshInstalled("✓ " + done + ". SF6 uses it at the next Random pick, also while it runs.");
            });
        }
    }

    // One outfit of the window: the outfit and what the list would hold for it
    internal sealed class RandomEntry
    {
        internal RandomOutfit Outfit;
        internal string Shown = "";   // the second line (RandomListForm.DisplayLabels)
        internal bool InList;
        internal int Repeat = 1;
        internal HashSet<int> Colours = new HashSet<int>();
        internal int ColourCount { get { return Math.Max(1, Outfit == null ? 10 : Outfit.ColourCount); } }
    }

    // The rows: grip, tick, names, "− ×2 +", one chip per colour. As wide as the chips need: the host scrolls sideways.
    internal sealed class RandomRows : DragList<RandomEntry>
    {
        internal const int RowPixels = 58;
        internal bool Dimmed;   // Random as before: the list is not used
        internal event EventHandler EntryChanged;
        internal RandomRows() : base(RowPixels) { }
        internal List<RandomEntry> Entries { get { return items.ToList(); } }
        internal RandomEntry this[int i] { get { return items[i]; } }
        internal void SetEntries(List<RandomEntry> list) { SetItems(list); FitWidth(); }

        private int Chips { get { return items.Count == 0 ? 10 : items.Max(e => e.ColourCount); } }
        private const int Chip = 22, ChipGap = 7, NameMin = 220;   // the gap leaves room for "EX10" under a box
        // The content's columns, from the content's left
        private int TickLeft { get { return Px(6); } }
        private int NamesLeft { get { return TickLeft + Px(18) + Px(12); } }
        private int RightPart { get { return Px(10) + PillWidth + Px(20) + Chips * (Px(Chip) + Px(ChipGap)) - Px(ChipGap) + Px(12); } }
        // 1.0.24 (the user: "there's probably a better way to design this"): the repeats are one pill, − | 2× | +, as tall as a
        // colour box, with what the number means under it, as the colours have their labels
        private const int PillEnd = 24, PillMiddle = 34;
        private int PillWidth { get { return 2 * Px(PillEnd) + Px(PillMiddle); } }
        internal int Mode;   // 0 Random as before, 1 In order, 2 Shuffled: the pill's caption
        // What the number means, under the pill (the user: "is it like probability or the number of time it's gonna appear?"):
        // a count. In order: that many times in a row; Shuffled: that many of the round's picks ("2 of 6 picks").
        internal static string RepeatCaption(int mode, RandomEntry e, int total)
        {
            if (mode == 1) return "in a row";
            if (mode == 2) return e != null && e.InList && total > 0 ? e.Repeat.ToString(CultureInfo.InvariantCulture) + " of " + total.ToString(CultureInfo.InvariantCulture) + " picks" : "per round";
            return "times";
        }
        private int TotalPicks { get { return items.Where(x => x.InList).Sum(x => x.Repeat); } }
        private void DrawRepeat(Graphics g, Rectangle c, RandomEntry e, bool off)
        {
            Rectangle m = Minus(c), n = CountBox(c), p = Plus(c), pill = new Rectangle(m.Left, m.Top, p.Right - m.Left, m.Height);
            Color line = off ? Palette.Border : Color.FromArgb(72, 77, 88);
            // the pill: rounded, on whole pixels (the edge anti-aliased, the inside crisp)
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath path = RoundedPath(new RectangleF(pill.Left + 0.5F, pill.Top + 0.5F, pill.Width - 1, pill.Height - 1), Px(6)))
            {
                using (var fill = new SolidBrush(Palette.Surface)) g.FillPath(fill, path);
                using (var pen = new Pen(line)) g.DrawPath(pen, path);
            }
            g.SmoothingMode = SmoothingMode.None;
            using (var sep = new SolidBrush(line)) { g.FillRectangle(sep, n.Left, n.Top + Px(5), 1, n.Height - Px(10)); g.FillRectangle(sep, n.Right - 1, n.Top + Px(5), 1, n.Height - Px(10)); }
            // − and + as lines (a font's minus sits off centre); the end that can't go further is dimmed
            int arm = Px(4), bar = Math.Max(2, Px(2)) / 2 * 2;   // even, so it centres
            Color live = off ? Palette.Muted : Palette.Text, dead = Palette.Border;
            using (var b = new SolidBrush(e.Repeat > 1 ? live : dead))
                g.FillRectangle(b, m.Left + m.Width / 2 - arm, m.Top + m.Height / 2 - bar / 2, 2 * arm, bar);
            using (var b = new SolidBrush(e.Repeat < RandomList.MaxRepeat ? live : dead))
            {
                g.FillRectangle(b, p.Left + p.Width / 2 - arm, p.Top + p.Height / 2 - bar / 2, 2 * arm, bar);
                g.FillRectangle(b, p.Left + p.Width / 2 - bar / 2, p.Top + p.Height / 2 - arm, bar, 2 * arm);
            }
            TextRenderer.DrawText(g, e.Repeat.ToString(CultureInfo.InvariantCulture) + "×", off ? Palette.Small : Palette.Strong, n, off ? Palette.Muted : Palette.Text, Centred);
            TextRenderer.DrawText(g, RepeatCaption(Mode, e, TotalPicks), LabelFont, new Rectangle(pill.Left - Px(10), pill.Bottom + Px(1), pill.Width + Px(20), Px(13)), off ? Palette.Muted : Palette.Accent, Centred);
        }
        private static GraphicsPath RoundedPath(RectangleF r, float radius)
        {
            float d = radius * 2; var path = new GraphicsPath();
            path.AddArc(r.Left, r.Top, d, d, 180, 90); path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure(); return path;
        }
        internal int NeededWidth { get { return GripWidth + NamesLeft + Px(NameMin) + RightPart; } }
        // As wide as the host, or as the chips need (the host scrolls sideways)
        internal void FitWidth()
        {
            var host = Parent; int view = host == null ? Width : host.ClientSize.Width;
            Width = Math.Max(view, NeededWidth); Invalidate();
        }
        private Rectangle TickBox(Rectangle c) { int s = Px(18); return new Rectangle(c.Left + TickLeft, c.Top + (c.Height - s) / 2, s, s); }
        private int RepeatLeft(Rectangle c) { return c.Right - RightPart + Px(10); }
        // the pill's three parts, at the colour boxes' height (ChipAt)
        private Rectangle Minus(Rectangle c) { return new Rectangle(RepeatLeft(c), c.Top + Px(9), Px(PillEnd), Px(Chip)); }
        private Rectangle CountBox(Rectangle c) { Rectangle m = Minus(c); return new Rectangle(m.Right, m.Top, Px(PillMiddle), m.Height); }
        private Rectangle Plus(Rectangle c) { Rectangle n = CountBox(c); return new Rectangle(n.Right, n.Top, Px(PillEnd), n.Height); }
        private Rectangle ChipAt(Rectangle c, int i) { int s = Px(Chip), step = s + Px(ChipGap); int left = Plus(c).Right + Px(20); return new Rectangle(left + i * step, c.Top + Px(9), s, s); }   // its label under it (ChipLabel)   // whole pixels: every chip the same size and spacing

        // Points inside row i's content, for tests
        internal Point TickPointForTest(int i) { Rectangle c = ContentBounds(i), r = TickBox(c); return new Point(r.Left + 2 - c.Left, r.Top + 2 - c.Top); }
        internal Point PlusPointForTest(int i) { Rectangle c = ContentBounds(i), r = Plus(c); return new Point(r.Left + 2 - c.Left, r.Top + 2 - c.Top); }
        internal Point ChipPointForTest(int i, int chip) { Rectangle c = ContentBounds(i), r = ChipAt(c, chip); return new Point(r.Left + 2 - c.Left, r.Top + 2 - c.Top); }

        protected override bool PressAt(RandomEntry e, Rectangle c, Point p)
        {
            bool changed = false;
            if (TickBox(c).Contains(p) || (p.X >= c.Left && p.X <= TickBox(c).Right + Px(6))) { e.InList = !e.InList; changed = true; }
            else if (Minus(c).Contains(p)) { if (e.Repeat > 1) e.Repeat--; e.InList = true; changed = true; }
            else if (Plus(c).Contains(p)) { if (e.Repeat < RandomList.MaxRepeat) e.Repeat++; e.InList = true; changed = true; }
            else for (int i = 0; i < e.ColourCount; i++)
                if (ChipAt(c, i).Contains(p)) { if (!e.Colours.Remove(i)) e.Colours.Add(i); e.InList = true; changed = true; break; }
            if (!changed) return false;
            Redraw(); if (EntryChanged != null) EntryChanged(this, EventArgs.Empty);
            return true;
        }
        protected override bool ActiveAt(RandomEntry e, Rectangle c, Point p)
        {
            if (p.X <= TickBox(c).Right + Px(6) || Minus(c).Contains(p) || Plus(c).Contains(p)) return true;
            for (int i = 0; i < e.ColourCount; i++) if (ChipAt(c, i).Contains(p)) return true;
            return false;
        }

        // 1.0.24 (the user: "have a little number for the color too. And for the additional colors, you could start it like EX1, 2, 3"):
        // the label under a colour box: Color 1-10 by number, the colours past them (a game outfit's Ex / Dx files) EX1, EX2...
        internal static string ChipLabel(int i) { return i < 10 ? (i + 1).ToString(CultureInfo.InvariantCulture) : "EX" + (i - 9).ToString(CultureInfo.InvariantCulture); }
        private static readonly Font LabelFont = new Font("Segoe UI", 7F);

        // Text centred in a box without Windows' extra padding (it shifted the numbers unevenly)
        private const TextFormatFlags Centred = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
        // An outline inside the box, "width" pixels thick, on whole pixels
        private static void Outline(Graphics g, Rectangle r, Color color, int width)
        {
            using (var b = new SolidBrush(color))
            {
                g.FillRectangle(b, r.Left, r.Top, r.Width, width); g.FillRectangle(b, r.Left, r.Bottom - width, r.Width, width);
                g.FillRectangle(b, r.Left, r.Top, width, r.Height); g.FillRectangle(b, r.Right - width, r.Top, width, r.Height);
            }
        }

        // The loader writes a swatch as via.Color stores it: R in the low byte, then G, B
        private static Color Rgb(uint v) { return Color.FromArgb(255, (int)(v & 0xFF), (int)((v >> 8) & 0xFF), (int)((v >> 16) & 0xFF)); }

        protected override void DrawRow(Graphics g, Rectangle c, RandomEntry e, int place, bool selectedRow, bool lifted)
        {
            var old = g.SmoothingMode; g.SmoothingMode = SmoothingMode.None;   // boxes on whole pixels (anti-aliased edges made them look uneven)
            bool off = !e.InList || Dimmed;
            Rectangle t = TickBox(c);
            Outline(g, t, e.InList ? Palette.Accent : Palette.Muted, Math.Max(1, Px(1)));
            if (e.InList) using (var fill = new SolidBrush(Palette.Accent)) g.FillRectangle(fill, Rectangle.Inflate(t, -Px(3), -Px(3)));
            Color text = off ? Palette.Muted : Palette.Text;
            int left = c.Left + NamesLeft, right = RepeatLeft(c) - Px(10);
            const TextFormatFlags one = TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
            TextRenderer.DrawText(g, e.Outfit.Name + (e.Outfit.Nsfw ? "  · NSFW" : ""), Palette.Strong, new Rectangle(left, c.Top + Px(8), right - left, Px(22)), text, one);
            TextRenderer.DrawText(g, e.Shown, Palette.Small, new Rectangle(left, c.Top + Px(30), right - left, Px(20)), Palette.Muted, one);
            DrawRepeat(g, c, e, off);
            bool any = e.Colours.Count == 0;
            uint[][] sw = e.Outfit.Swatches;
            for (int i = 0; i < e.ColourCount; i++)
            {
                Rectangle r = ChipAt(c, i);
                if (sw != null && i < sw.Length && sw[i] != null && sw[i].Length > 0)
                {
                    using (var a = new SolidBrush(Rgb(sw[i][0]))) g.FillRectangle(a, r.Left, r.Top, r.Width / 2, r.Height);
                    using (var b = new SolidBrush(Rgb(sw[i].Length > 1 ? sw[i][1] : sw[i][0]))) g.FillRectangle(b, r.Left + r.Width / 2, r.Top, r.Width - r.Width / 2, r.Height);
                }
                else
                {
                    using (var b = new SolidBrush(Palette.Surface)) g.FillRectangle(b, r);   // no swatch known: its label says which
                }
                bool on = e.Colours.Contains(i);
                if (!any && !on) using (var veil = new SolidBrush(Color.FromArgb(170, Palette.Raised))) g.FillRectangle(veil, r);
                if (off) using (var veil = new SolidBrush(Color.FromArgb(110, Palette.Raised))) g.FillRectangle(veil, r);
                Outline(g, r, on ? Palette.Accent : Palette.Border, on ? Math.Max(2, Px(2)) : 1);
                TextRenderer.DrawText(g, ChipLabel(i), LabelFont, new Rectangle(r.Left - Px(6), r.Bottom + Px(1), r.Width + Px(12), Px(13)), on ? Palette.Accent : Palette.Muted, Centred);
            }
            g.SmoothingMode = old;
        }
    }

    internal sealed class RandomListForm : Form
    {
        private readonly string game;
        private readonly PickerButton fighterButton;
        private readonly FlatButton modeNone, modeOrder, modeShuffle, save;
        private readonly Panel listHost;
        private readonly RandomRows rows;
        private readonly Label note;
        private readonly ToolStripDropDown popup;
        private readonly FighterGrid grid;
        private readonly Func<string, Dictionary<int, string>> replaced;
        private string fighterDir = "", fighterName = "", original = "none";
        private int mode;   // 0 Random as before, 1 In order, 2 Shuffled
        internal string FighterDir { get { return fighterDir; } }
        internal string FighterName { get { return fighterName; } }
        internal RandomRows RowsForTest { get { return rows; } }
        internal int Mode { get { return mode; } set { SetMode(value); } }

        // The list as the rows hold it (null: Random as before)
        internal RandomPlan Plan { get { return PlanOf(rows.Entries, mode); } }
        private static RandomPlan PlanOf(IEnumerable<RandomEntry> entries, int mode)
        {
            if (mode == 0) return null;
            var plan = new RandomPlan { Shuffle = mode == 2 };
            foreach (RandomEntry e in entries.Where(e => e.InList)) plan.Items.Add(new RandomListItem { Outfit = e.Outfit, Repeat = e.Repeat, Colours = e.Colours.OrderBy(c => c).ToList() });
            return plan;
        }
        private static string SignatureOf(RandomPlan p) { return p == null ? "none" : p.Signature; }

        // 1.0.24 (PR #52 review: switching fighter threw edits away): each fighter shown keeps its rows, mode and the list it
        // was opened with while the window is open; Save writes every fighter whose list changed, in one go.
        private sealed class FighterEdits { internal string Name; internal List<RandomEntry> Entries; internal int Mode; internal string Original; }
        private readonly Dictionary<string, FighterEdits> shownFighters = new Dictionary<string, FighterEdits>(StringComparer.OrdinalIgnoreCase);
        private void KeepShown()
        {
            if (fighterDir.Length == 0) return;
            shownFighters[fighterDir] = new FighterEdits { Name = fighterName, Entries = rows.Entries, Mode = mode, Original = original };
        }
        // Every fighter whose list differs from the one it was opened with: fighter folder -> its list (null: Random as before)
        internal Dictionary<string, RandomPlan> Plans
        {
            get
            {
                KeepShown();
                var plans = new Dictionary<string, RandomPlan>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in shownFighters)
                {
                    RandomPlan p = PlanOf(kv.Value.Entries, kv.Value.Mode);
                    // In order or Shuffled with nothing ticked can't be saved: left out (re-review: it greyed out Save for all)
                    if (p != null && p.Items.Count == 0) continue;
                    if (SignatureOf(p) != kv.Value.Original) plans[kv.Key] = p;
                }
                return plans;
            }
        }
        // The fighters (names) set to In order or Shuffled with nothing ticked: not saved, the note says so
        internal List<string> EmptyNames
        {
            get { KeepShown(); return shownFighters.Where(kv => kv.Value.Mode != 0 && !kv.Value.Entries.Any(e => e.InList)).Select(kv => kv.Value.Name).ToList(); }
        }
        // The fighters (names) with changes, the one shown first
        internal List<string> ChangedNames { get { var plans = Plans; return shownFighters.Where(kv => plans.ContainsKey(kv.Key)).OrderBy(kv => kv.Key == fighterDir ? 0 : 1).Select(kv => kv.Value.Name).ToList(); } }
        internal bool Changed { get { return Plans.Count > 0; } }
        // Something to save (a list In order or Shuffled with nothing ticked is left out of Plans)
        internal bool CanSave { get { return Plans.Count > 0; } }

        internal static bool Choose(IWin32Window owner, string gameRoot, string fighter, IEnumerable<KeyValuePair<string, string>> fighters, IEnumerable<FighterCard> portraits,
            Dictionary<string, int> counts, Func<string, Dictionary<int, string>> replaced, out Dictionary<string, RandomPlan> plans, out List<string> names, Action<string> shownLast = null)
        {
            plans = null; names = null;
            if (Program.Headless) return false;
            using (var form = new RandomListForm(gameRoot, fighter, fighters, portraits, counts, replaced))
            {
                DialogResult result = form.ShowDialog(owner);
                if (shownLast != null) shownLast(form.FighterDir);   // the fighter it showed last, saved or not
                if (result != DialogResult.OK || !form.CanSave) return false;
                plans = form.Plans; names = form.ChangedNames;
                return true;
            }
        }

        internal RandomListForm(string gameRoot, string fighter, IEnumerable<KeyValuePair<string, string>> fighters, IEnumerable<FighterCard> portraits, Dictionary<string, int> counts,
            Func<string, Dictionary<int, string>> replaced = null)
        {
            game = gameRoot; this.replaced = replaced;
            SuspendLayout();
            AutoScaleDimensions = new SizeF(96F, 96F); AutoScaleMode = AutoScaleMode.Dpi;
            Text = "Random list"; StartPosition = FormStartPosition.CenterParent; Name = "randomListForm";
            BackColor = Palette.Surface; ForeColor = Palette.Text; ShowInTaskbar = false; Padding = new Padding(18, 14, 18, 14); MaximizeBox = false; MinimizeBox = false; FormBorderStyle = FormBorderStyle.FixedDialog;
            AppBranding.Apply(this);
            var title = new Label { UseMnemonic = false, Dock = DockStyle.Top, Height = 30, Font = new Font("Segoe UI", 12F, FontStyle.Bold), AutoEllipsis = true, Text = "Random outfit list" };
            var pickRow = new Panel { Dock = DockStyle.Top, Height = 44, Padding = new Padding(0, 4, 0, 6) };
            fighterButton = new PickerButton { Dock = DockStyle.Left, Width = 220, Name = "randomListFighter", AccessibleName = "Fighter" };
            modeShuffle = new FlatButton("Shuffled", false) { Dock = DockStyle.Right, Width = 104, Font = Palette.Small, Name = "randomListShuffle" };
            modeOrder = new FlatButton("In order", false) { Dock = DockStyle.Right, Width = 104, Font = Palette.Small, Name = "randomListOrder" };
            modeNone = new FlatButton("Random", false) { Dock = DockStyle.Right, Width = 104, Font = Palette.Small, Name = "randomListNone" };
            modeNone.Click += delegate { SetMode(0); }; modeOrder.Click += delegate { SetMode(1); }; modeShuffle.Click += delegate { SetMode(2); };
            // Docked last-added first: the filler goes in first so it only takes what is left
            pickRow.Controls.Add(new Panel { Dock = DockStyle.Fill }); pickRow.Controls.Add(fighterButton);
            pickRow.Controls.Add(modeNone); pickRow.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 6 }); pickRow.Controls.Add(modeOrder); pickRow.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 6 }); pickRow.Controls.Add(modeShuffle);
            note = new Label { UseMnemonic = false, Dock = DockStyle.Top, Height = 58, ForeColor = Palette.Muted, Font = Palette.Small, Padding = new Padding(0, 4, 0, 0) };
            rows = new RandomRows { Location = Point.Empty, Name = "randomListRows", AccessibleName = "Outfits Random may pick, in the list's order" };
            listHost = new Panel { Dock = DockStyle.Top, Height = RandomRows.RowPixels * 7 + 2, AutoScroll = true, BackColor = Palette.Background, Name = "randomListHost" };
            listHost.Controls.Add(rows);
            listHost.Resize += delegate { rows.FitWidth(); };
            rows.EntryChanged += delegate { UpdateState(); };
            rows.Changed += delegate { UpdateState(); };
            var footer = new Panel { Dock = DockStyle.Bottom, Height = 48, Padding = new Padding(0, 14, 0, 0) };
            save = new FlatButton("Save list", true) { Width = 130, Dock = DockStyle.Right, Font = Palette.Small, Name = "randomListSave" }; save.Click += delegate { Accept(); };
            var cancel = new FlatButton("Cancel", false) { Width = 100, Dock = DockStyle.Right, Font = Palette.Small }; cancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            var hint = new Label { UseMnemonic = false, Dock = DockStyle.Fill, ForeColor = Palette.Muted, Font = Palette.Small, TextAlign = ContentAlignment.MiddleLeft, Text = "Drag a row by its grip to set the order." };
            footer.Controls.Add(hint); footer.Controls.Add(cancel); footer.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 8 }); footer.Controls.Add(save);
            Controls.Add(footer); Controls.Add(listHost); Controls.Add(note); Controls.Add(pickRow); Controls.Add(title);
            ClientSize = new Size(820, Padding.Vertical + title.Height + pickRow.Height + note.Height + listHost.Height + footer.Height);

            var panel = new Panel { BackColor = Palette.Surface, Padding = new Padding(8), Name = "randomListFighters" };
            grid = new FighterGrid { Dock = DockStyle.Fill, Name = "randomListGrid", AccessibleName = "Fighters" };
            grid.SetFighters(fighters);
            foreach (FighterCard card in portraits ?? new FighterCard[0]) if (card.Portrait != null) grid.SetPortrait(card.Code, (Bitmap)card.Portrait.Clone(), card.PortraitNote);
            grid.SetCounts((counts ?? new Dictionary<string, int>()).Where(kv => kv.Key.Length > 3).GroupBy(kv => kv.Key.Substring(3)).ToDictionary(g => g.Key, g => g.First().Value, StringComparer.Ordinal));
            panel.Controls.Add(grid);
            var host = new ToolStripControlHost(panel) { AutoSize = false, Margin = Padding.Empty, Padding = Padding.Empty };
            popup = new ToolStripDropDown { Padding = Padding.Empty, AutoClose = true, DropShadowEnabled = true, BackColor = Palette.Border };
            popup.Items.Add(host);
            grid.SelectionChanged += delegate
            {
                string code = grid.Selected.FirstOrDefault(c => "esf" + c != fighterDir) ?? grid.Selected.FirstOrDefault();
                if (code == null) { grid.SetSelected(new[] { fighterDir.Length > 3 ? fighterDir.Substring(3) : "" }, false); return; }
                string name; if (!Characters.ByCode.TryGetValue(code, out name)) name = FighterPortraits.DisplayName(code);
                popup.Close(); Load("esf" + code, name);
            };
            Action togglePicker = delegate
            {
                if (popup.Visible) { popup.Close(); return; }
                int width = DpiMetrics.Px(this, 720), height = Math.Min(DpiMetrics.Px(this, 520), grid.HeightFor(width - DpiMetrics.Px(this, 16)));
                panel.Size = new Size(width, height + DpiMetrics.Px(this, 16)); host.Size = panel.Size;
                popup.Show(fighterButton, new Point(0, fighterButton.Height + DpiMetrics.Px(this, 4)));
                grid.Focus();
            };
            fighterButton.Click += delegate { togglePicker(); };
            openPicker = delegate { if (!popup.Visible) togglePicker(); };
            Disposed += delegate { popup.Dispose(); };
            KeyPreview = true;
            KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape && !popup.Visible && !rows.Dragging) { DialogResult = DialogResult.Cancel; Close(); }   // during a drag Escape cancels the drag
                else if (e.KeyCode == Keys.Enter && save.Enabled && !popup.Visible) { e.Handled = true; Accept(); }
            };
            DarkTheme.Apply(this); ResumeLayout(true); PerformAutoScale();
            string dir = null, shown = fighter ?? "";
            foreach (var kv in Characters.ByCode) if (String.Equals(kv.Value, fighter, StringComparison.OrdinalIgnoreCase)) { dir = "esf" + kv.Key; shown = kv.Value; }
            // the fighter by its folder ("esf016", what the Manager keeps since the PR #52 review), its name from the game tables
            if (dir == null && fighter != null && fighter.Length == 6 && fighter.StartsWith("esf", StringComparison.OrdinalIgnoreCase) && fighter.Substring(3).All(Char.IsDigit))
            {
                string code = fighter.Substring(3), known;
                dir = "esf" + code; shown = Characters.ByCode.TryGetValue(code, out known) ? known : FighterPortraits.DisplayName(code);
            }
            if (dir == null) shown = "";   // unknown: the picker opens
            Load(dir ?? "", shown);
            ActiveControl = rows;
            // 1.0.24 (the user: "if no last fighter then i guess open fighter select"): no fighter to start on: the picker opens
            if (dir == null) Shown += delegate { if (fighterDir.Length == 0) openPicker(); };
        }
        private Action openPicker;
        internal bool PickerOpenForTest { get { return popup.Visible; } }

        internal void Load(string dir, string name)
        {
            KeepShown();   // the fighter shown so far keeps its edits
            fighterDir = dir ?? ""; fighterName = name ?? "";
            fighterButton.Text = fighterName.Length == 0 ? "Choose a fighter" : fighterName;
            if (fighterDir.Length > 3) grid.SetSelected(new[] { fighterDir.Substring(3) }, false);
            FighterEdits kept;
            if (fighterDir.Length > 0 && shownFighters.TryGetValue(fighterDir, out kept))
            {
                // shown before in this window: as it was left
                rows.SetEntries(kept.Entries);
                rows.Height = DpiMetrics.Px(this, RandomRows.RowPixels) * Math.Max(1, kept.Entries.Count);
                rows.FitWidth();
                mode = kept.Mode; original = kept.Original;
                UpdateState();
                return;
            }
            Dictionary<int, string> replacedBy = null;
            if (fighterDir.Length > 0 && replaced != null) { try { replacedBy = replaced(fighterDir); } catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); } }
            List<RandomOutfit> outfits = fighterDir.Length == 0 ? new List<RandomOutfit>() : RandomList.Outfits(game, fighterDir, replacedBy);
            RandomPlan plan = fighterDir.Length == 0 ? null : RandomList.Load(game, fighterDir, outfits);
            Dictionary<RandomOutfit, string> labels = DisplayLabels(outfits, fighterName);
            Func<RandomOutfit, string> label = o => { string l; return labels.TryGetValue(o, out l) ? l : o.Label; };
            // The list first, in its order, then the outfits not in it
            var list = new List<RandomEntry>();
            if (plan != null) foreach (RandomListItem item in plan.Items) list.Add(new RandomEntry { Outfit = item.Outfit, Shown = label(item.Outfit), InList = true, Repeat = item.Repeat, Colours = new HashSet<int>(item.Colours) });
            foreach (RandomOutfit o in outfits) if (plan == null || !plan.Items.Any(i => i.Outfit == o)) list.Add(new RandomEntry { Outfit = o, Shown = label(o) });
            rows.Empty = fighterDir.Length == 0 ? "Choose a fighter." : fighterName + " has no outfit the Slots Loader knows yet. Start SF6 once.";
            rows.SetEntries(list);
            rows.Height = DpiMetrics.Px(this, RandomRows.RowPixels) * Math.Max(1, list.Count);
            rows.FitWidth();
            mode = plan == null ? 0 : plan.Shuffle ? 2 : 1;
            original = plan == null ? "none" : plan.Signature;
            UpdateState();
        }

        // The second line of a row: the mod, and for variants of one mod what tells them apart (as Load order's rows: the
        // shared folder or archive dropped, LabelShortener; then the " - " pieces every variant shares at both ends dropped).
        internal static Dictionary<RandomOutfit, string> DisplayLabels(List<RandomOutfit> outfits, string fighter)
        {
            var result = new Dictionary<RandomOutfit, string>();
            string prefix = (fighter ?? "") + "/";
            Func<string, string> strip = s => s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? s.Substring(prefix.Length) : s;
            foreach (RandomOutfit o in outfits.Where(o => o.Game)) result[o] = o.Label;
            foreach (var group in outfits.Where(o => !o.Game).GroupBy(o => o.Source ?? "", StringComparer.OrdinalIgnoreCase))
            {
                var members = group.ToList();
                if (members.Count == 1 || group.Key.Length == 0) { foreach (RandomOutfit o in members) result[o] = strip(o.Label.Length > 0 ? o.Label : o.Source); continue; }
                var names = members.Select(o => { string l = o.Label ?? ""; return l.StartsWith(group.Key + "/", StringComparison.OrdinalIgnoreCase) ? l.Substring(group.Key.Length + 1) : strip(l); }).ToList();
                names = LabelShortener.Short(names);
                var parts = names.Select(n => n.Split(new[] { " - " }, StringSplitOptions.None)).ToList();
                int head = 0, tail = 0, shortest = parts.Min(p => p.Length);
                while (head < shortest - 1 && parts.All(p => String.Equals(p[head], parts[0][head], StringComparison.OrdinalIgnoreCase))) head++;
                while (tail < shortest - 1 - head && parts.All(p => String.Equals(p[p.Length - 1 - tail], parts[0][parts[0].Length - 1 - tail], StringComparison.OrdinalIgnoreCase))) tail++;
                string mod = strip(group.Key);
                for (int i = 0; i < members.Count; i++)
                {
                    string part = String.Join(" - ", parts[i].Skip(head).Take(parts[i].Length - head - tail));
                    result[members[i]] = mod + (part.Length > 0 ? " · " + part : "");
                }
            }
            return result;
        }

        internal void SetMode(int value) { mode = Math.Max(0, Math.Min(2, value)); UpdateState(); }

        private void UpdateState()
        {
            modeNone.Active = mode == 0; modeOrder.Active = mode == 1; modeShuffle.Active = mode == 2;
            modeNone.Invalidate(); modeOrder.Invalidate(); modeShuffle.Invalidate();
            rows.Dimmed = mode == 0; rows.Mode = mode; rows.Invalidate();
            save.Enabled = CanSave;
            var entries = rows.Entries;
            int ticked = entries.Count(e => e.InList), places = entries.Where(e => e.InList).Sum(e => e.Repeat);
            string colours = " Colours: tick the ones an outfit may wear; none ticked means any.";
            note.Text = fighterDir.Length == 0 ? "Choose a fighter."
                : entries.Count == 0 ? fighterName + " has no outfit the Slots Loader knows yet. Start SF6 once."
                : mode == 0 ? "Random draws from every outfit " + fighterName + " has, each once before any comes up again. Choose In order or Shuffled to make your own list."
                : ticked == 0 ? "Tick the outfits Random may pick." + colours
                : (mode == 1 ? "Random plays the ticked outfits from the top. The number is a count, not a chance: at 2× an outfit comes up twice in a row, then the next one."
                             : "Random deals the ticked outfits like cards. The number is a count, not a chance: at 2× an outfit has two cards, so it comes up twice in every round, in a shuffled order.")
                  + " A round is " + ReplacePlanner.N(places, "pick") + "." + colours + " An outfit you don't own or can't see now (NSFW hidden) is skipped.";
            List<string> others = ChangedNames.Where(n => !String.Equals(n, fighterName, StringComparison.OrdinalIgnoreCase)).ToList();
            if (others.Count > 0) note.Text += " Also changed, saved together: " + String.Join(", ", others) + ".";
            List<string> empty = EmptyNames.Where(n => !String.Equals(n, fighterName, StringComparison.OrdinalIgnoreCase)).ToList();
            if (empty.Count > 0) note.Text += " Nothing ticked, not saved: " + String.Join(", ", empty) + ".";
        }
        private void Accept() { if (!save.Enabled) return; DialogResult = DialogResult.OK; Close(); }
    }
}
