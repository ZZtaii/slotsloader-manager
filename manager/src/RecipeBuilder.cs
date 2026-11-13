using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace SlotsInstaller
{
    // 1.0.18 (user request): a recipe built from nothing. Pick a fighter, a base mod with its options, then parts of
    // other mods on top; the list shows the winner first and says what each part overrides before anything is
    // installed. RecipeDraft is the logic (no window): it reads the mods, applies the same rules as Add to outfit
    // (the fighter's files only, and only files made for an outfit slot the base is built on) and explains overlaps.
    // A layer is a PartGraft (mod, option ids and names, the files it writes), as in an install record.
    internal sealed class RecipeDraft
    {
        private static readonly Regex BodyMesh = new Regex(@"^natives/stm/product/model/esf/esf(\d{3})/(\d{3})/01/[^/]+\.mesh\.\d+$", RegexOptions.IgnoreCase);
        internal readonly string Character, Code, SevenZip;
        // 1.0.18 (the user: "we should remove the concept of base. It just adds an unnecessary thing ... it all depends
        // on load orders, and you can already move it up and down"): a recipe is its layers in load order and nothing
        // else. The winner is first and the last one is simply at the bottom; every layer is a mod with options ticked.
        internal readonly List<PartGraft> Layers = new List<PartGraft>();
        internal RecipeDraft(string character, string code, string sevenZip) { Character = character ?? ""; Code = code ?? ""; SevenZip = sevenZip ?? ""; }

        internal static string NameOf(ModComponent c, ModPackage package)
        {
            ModComponent parent = c.ParentId == null ? null : package.Find(c.ParentId);
            return parent != null && parent.IsDummy && !String.IsNullOrWhiteSpace(parent.Name) && parent.ParentId != null ? parent.Name + " / " + c.Name : c.Name;
        }
        // The options of a mod that carry files (what the picker lists), in menu order.
        internal static List<ModComponent> Options(ModPackage package) { return package.Components.Where(c => !c.IsDummy && c.Assets.Count > 0).ToList(); }

        private string WrongFighter(IEnumerable<string> paths)
        {
            var codes = paths.SelectMany(p => Regex.Matches(p, @"/esf/esf(\d{3})/", RegexOptions.IgnoreCase).Cast<Match>().Select(m => m.Groups[1].Value)).Distinct().Where(c => c != Code).ToList();
            return codes.Count == 0 ? null : "These files belong to " + String.Join(", ", codes.Select(c => Characters.ByCode.ContainsKey(c) ? Characters.ByCode[c] : "esf" + c)) + ", not " + Character + ".";
        }
        private static List<string> BodyFolders(IEnumerable<string> paths)
        {
            return paths.Select(p => BodyMesh.Match(p)).Where(m => m.Success && m.Groups[2].Value != "000").Select(m => m.Groups[2].Value).Distinct().ToList();
        }
        // These files hold a complete body for an outfit slot (what an extra outfit is built on).
        internal static bool BringsBody(IEnumerable<string> paths) { return BodyFolders(paths ?? new string[0]).Count > 0; }
        private static IEnumerable<string> Folders(IEnumerable<string> paths)
        {
            return paths.Select(p => Regex.Match(p, @"^natives/stm/(?:streaming/)?product/model/esf/esf\d{3}/(\d{3})/", RegexOptions.IgnoreCase)).Where(m => m.Success).Select(m => m.Groups[1].Value).Distinct();
        }
        // A layer in a sentence: the mod when it is the whole mod, its options when they are few, else how many.
        internal static string Shown(PartGraft layer)
        {
            string mod = String.IsNullOrEmpty(layer.Mod) ? Path.GetFileNameWithoutExtension(layer.SourcePath ?? "") : layer.Mod;
            List<string> options = layer.Options ?? new List<string>();
            if (options.Count == 0 || (options.Count == 1 && String.Equals(options[0], mod, StringComparison.OrdinalIgnoreCase))) return mod;
            return options.Count <= 2 ? String.Join(" + ", options) + " from " + mod : mod;
        }

        // The outfit slots ("001") the recipe is made for: the ones its layers bring a body for, else the ones their
        // files are made for. A new layer's files may be made for one of them or be shared ("000"). Empty while
        // nothing decides it yet. (The user: with a C1 mod in it, "only show c1 outfit mods, or mods containing skin
        // texture mods or hair c1 mods or cmd colors for c1".)
        internal List<string> Slots() { return Slots(null); }
        private List<string> Slots(PartGraft except)
        {
            List<string> files = Layers.Where(l => l != except).SelectMany(l => l.Files).ToList();
            List<string> body = BodyFolders(files);
            return body.Count > 0 ? body : Folders(files).Where(f => f != "000").Distinct().ToList();
        }
        // A library mod has something for this recipe: files for one of its outfit slots, or shared files (from the
        // library index, no archive is opened). A mod whose costume folders are not known is kept.
        internal bool Fits(LibraryEntry entry)
        {
            List<string> slots = Slots();
            if (slots.Count == 0 || entry == null) return true;
            var keys = (entry.CostumeKeys ?? new List<string>()).Where(k => k != null && k.StartsWith(Code + "/", StringComparison.Ordinal)).Select(k => k.Substring(Code.Length + 1)).ToList();
            return keys.Count == 0 || keys.Any(k => k == "000" || slots.Contains(k));
        }
        // An option that could be added on its own.
        internal bool Fits(ModComponent option)
        {
            if (option == null) return true;
            List<string> paths = option.Assets.Select(a => a.DestinationPath).ToList();
            if (WrongFighter(paths) != null) return false;
            List<string> slots = Slots();
            return slots.Count == 0 || Installer.FolderMismatch(Folders(paths), slots) == null;
        }

        // One layer: the mod with these options, or as it installs by default when none are named (its own rules
        // pick them). `replacing`: the layer it takes the place of (its files don't count for the slot check).
        private string Build(string source, string mod, IList<string> ids, PartGraft replacing, out PartGraft layer) { return Build(source, mod, ids, null, replacing, out layer); }
        // parts (1.0.18): only these parts of the options (OutfitParts), or null/empty for all of them.
        private string Build(string source, string mod, IList<string> ids, IList<string> parts, PartGraft replacing, out PartGraft layer)
        {
            layer = null;
            ModPackage package = PackageReader.Load(source, SevenZip, false);
            List<string> wanted;
            if (ids == null || ids.Count == 0)
            {
                // The mod's own rules pick the options when they give an outfit; a mod that is not an outfit by itself
                // (shared textures, a hair) goes in with what it ticks by default, else with everything it has.
                List<string> defaults = Composer.DefaultSelection(package, false);
                CompositionPlan own = Composer.Plan(package, defaults);
                wanted = own.CanInstall ? own.Layers.Select(c => c.Id).ToList() : defaults.Where(id => { ModComponent c = package.Find(id); return c != null && !c.IsDummy && c.Assets.Count > 0; }).ToList();
                if (wanted.Count == 0) wanted = Options(package).Select(c => c.Id).ToList();
                if (wanted.Count == 0) return "This mod has no outfit files.";
            }
            else wanted = ids.ToList();
            CompositionPlan selection = Installer.SelectionFiles(package, wanted, parts, SevenZip);
            if (selection.Errors.Count > 0 || selection.Files.Count == 0) return String.Join(" ", selection.Errors.DefaultIfEmpty("These options have no outfit files."));
            string wrong = WrongFighter(selection.Files.Keys); if (wrong != null) return wrong;
            List<string> slots = Slots(replacing);
            string mismatch = slots.Count == 0 ? null : Installer.FolderMismatch(Folders(selection.Files.Keys), slots);
            if (mismatch != null) return "These files are made for " + mismatch + ", and this recipe is made for " + Installer.SlotText(slots) + ". Materials point at their own outfit slot's files.";
            layer = new PartGraft { SourcePath = source, Mod = mod ?? package.Name ?? "", SelectedIds = selection.Layers.Select(c => c.Id).ToList(), Options = selection.Layers.Select(c => NameOf(c, package)).ToList(), Files = selection.Files.Keys.ToList(), Parts = (selection.OnlyParts ?? new List<string>()).ToList() };
            return null;
        }
        // The parts the layer's options have in all (what "only these parts" can choose from), in OutfitParts.Order.
        internal List<string> PartsOf(PartGraft layer)
        {
            ModPackage package = PackageReader.Load(layer.SourcePath, SevenZip, false);
            return OutfitParts.In(Installer.SelectionFiles(package, layer.SelectedIds.Where(id => package.Find(id) != null)).Files.Keys);
        }
        // How many files each part of the layer's options has (for the Parts picker).
        internal Dictionary<string, int> PartCounts(PartGraft layer)
        {
            ModPackage package = PackageReader.Load(layer.SourcePath, SevenZip, false);
            return Installer.SelectionFiles(package, layer.SelectedIds.Where(id => package.Find(id) != null)).Files.Keys.GroupBy(OutfitParts.Of, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        }
        // Only these parts of a layer, in its place (none or all of them: the whole layer again). Returns why not, or null.
        internal string ChangeParts(PartGraft old, IList<string> parts)
        {
            int at = Layers.IndexOf(old); if (at < 0) return "That layer is no longer in the recipe.";
            PartGraft layer; string why = Build(old.SourcePath, old.Mod, old.SelectedIds, parts, old, out layer);
            if (why != null) return why;
            Layers[at] = layer;
            return null;
        }
        // Who supplies each part: the top layer that has files for it. "Body: Goth Juri · Hair: Long Hair"
        internal string Suppliers()
        {
            var lines = new List<string>();
            foreach (string part in new[] { OutfitParts.Body, OutfitParts.Hair, OutfitParts.Head })
            {
                PartGraft top = Layers.FirstOrDefault(l => (l.Files ?? new List<string>()).Any(f => OutfitParts.Of(f) == part));
                if (top != null) lines.Add(OutfitParts.Label(part) + ": " + (String.IsNullOrEmpty(top.Mod) ? Path.GetFileNameWithoutExtension(top.SourcePath ?? "") : top.Mod));
            }
            return lines.Count < 2 && Layers.Count < 2 ? "" : String.Join("   ·   ", lines);
        }
        // A layer on top of everything so far (ids null: the mod's default options). Returns why not, or null.
        internal string AddLayer(string source, string mod, IList<string> ids) { return AddLayer(source, mod, ids, null); }
        internal string AddLayer(string source, string mod, IList<string> ids, IList<string> parts)
        {
            PartGraft layer; string why = Build(source, mod, ids, parts, null, out layer);
            if (why != null) return why;
            Layers.RemoveAll(l => Installer.SamePart(l, layer));
            Layers.Insert(0, layer);
            return null;
        }
        // Other options for a layer, in its place. Layers that no longer fit the recipe's outfit slot are taken off
        // and named (LastDropped). Returns why not, or null.
        internal List<string> LastDropped = new List<string>();
        internal string ChangeLayer(PartGraft old, IList<string> ids)
        {
            LastDropped = new List<string>();
            int at = Layers.IndexOf(old); if (at < 0) return "That layer is no longer in the recipe.";
            if (ids == null || ids.Count == 0) return "Tick at least one option.";
            PartGraft layer; string why = Build(old.SourcePath, old.Mod, ids, old.Parts, old, out layer);
            if (why != null) return why;
            Layers[at] = layer;
            List<string> slots = Slots();
            var dropped = Layers.Where(l => l != layer && slots.Count > 0 && Installer.FolderMismatch(Folders(l.Files), slots) != null).ToList();
            foreach (PartGraft l in dropped) Layers.Remove(l);
            LastDropped = dropped.Select(Shown).ToList();
            return null;
        }

        // Why this order can't be built as an extra outfit, or null. An extra outfit is built on the lowest layer
        // that brings a body (OutfitRecipes.Install); a layer under it must not share files with it, or it would win
        // them. Replace original has no such rule.
        internal string OutfitProblem()
        {
            if (Layers.Count == 0) return null;
            List<PartGraft> bottomUp = Enumerable.Reverse(Layers).ToList();
            int body = bottomUp.FindIndex(l => BringsBody(l.Files));
            if (body < 0) return "No layer brings a complete body, so this recipe can't be an extra outfit. It installs as Replace original.";
            if (bottomUp[body].Parts != null && bottomUp[body].Parts.Count > 0) return "As an extra outfit, " + Shown(bottomUp[body]) + " goes in whole (the outfit is built on it), so with only some of its parts this recipe installs as Replace original.";
            if (body == 0) return null;
            var files = new HashSet<string>(bottomUp[body].Files, StringComparer.OrdinalIgnoreCase);
            PartGraft clash = bottomUp.Take(body).FirstOrDefault(l => l.Files.Any(files.Contains));
            return clash == null ? null : "As an extra outfit, " + Shown(bottomUp[body]) + " has to be below " + Shown(clash) + ": it brings the body, and they share files. Move it down, or install the recipe as Replace original.";
        }

        // What the user should know before installing: per layer what it replaces, a layer that ends up with nothing,
        // two complete bodies (the report people would otherwise file as "it doesn't work"), and an order that can't
        // be an extra outfit.
        internal List<string> Notes()
        {
            var notes = new List<string>();
            if (Layers.Count == 0) return notes;
            var above = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var bodies = new List<string>();
            for (int i = 0; i < Layers.Count; i++)
            {
                PartGraft layer = Layers[i];
                var below = new HashSet<string>(Layers.Skip(i + 1).SelectMany(l => l.Files), StringComparer.OrdinalIgnoreCase);
                int hidden = layer.Files.Count(above.Contains), wins = layer.Files.Count(f => below.Contains(f) && !above.Contains(f));
                string what = Shown(layer);
                // What a layer loses is said once, by the layer that takes it ("replaces N files of the layers below it").
                if (hidden == layer.Files.Count && layer.Files.Count > 0) notes.Add(what + " adds nothing: every file of it is overridden by a layer above it.");
                if (wins > 0) notes.Add(what + " replaces " + ReplacePlanner.N(wins, "file") + " of the layers below it.");
                if (BringsBody(layer.Files)) bodies.Add(what);
                above.UnionWith(layer.Files);
            }
            if (bodies.Count > 1) notes.Insert(0, "More than one layer brings a complete body (" + String.Join("; ", bodies) + "). Only the top one will show.");
            string outfit = OutfitProblem(); if (outfit != null) notes.Insert(0, outfit);
            return notes;
        }

        // The file format keeps its two fields: Base is the bottom layer and Parts the ones above it, bottom-up.
        internal OutfitRecipe ToRecipe(string name)
        {
            if (Layers.Count == 0) throw new IOException("Add a mod first.");
            Func<PartGraft, RecipeLayer> layer = g => new RecipeLayer { SourcePath = g.SourcePath, Mod = g.Mod, SelectedIds = g.SelectedIds.ToList(), Options = g.Options.ToList(), OptionNames = g.Options.ToList(), Parts = (g.Parts ?? new List<string>()).ToList() };
            List<PartGraft> bottomUp = Enumerable.Reverse(Layers).ToList();
            var recipe = new OutfitRecipe { Name = (name ?? "").Trim(), Character = Character, CreatedUtc = DateTime.UtcNow.ToString("o"), Base = layer(bottomUp[0]) };
            recipe.Base.Options = new List<string>();   // the summary names the bottom layer by its mod
            foreach (PartGraft g in bottomUp.Skip(1)) recipe.Parts.Add(layer(g));   // saved in the order applied: the winner last
            return recipe;
        }
    }

    // 1.0.18: the recipe builder's Parts picker, shown in a drop-down under the Parts button: which parts of the
    // selected mod the recipe takes. A caption with the mod's name, one row per part (the app's accent tick, the
    // part's name, its file count on the right; the row lights up under the pointer and is the click), and Apply.
    internal sealed class PartPicker : Control
    {
        internal sealed class Item { internal string Part = "", Label = "", Detail = ""; internal bool On; }
        private readonly List<Item> items;
        private readonly string mod;
        private readonly FlatButton apply;
        private int hover = -1;
        private static readonly Color Lit = Color.FromArgb(46, 50, 58);
        private const int Pad = 12, CaptionHeight = 44, RowHeight = 34, HintHeight = 44, ButtonHeight = 34, Box = 16, WidthLogical = 320;
        internal const string Hint = "The recipe takes only the ticked parts of this mod. What is unticked comes from the mods below it, or from the game.";
        internal event Action Applied;
        internal List<string> Ticked { get { return items.Where(i => i.On).Select(i => i.Part).ToList(); } }
        internal IList<Item> Items { get { return items.AsReadOnly(); } }

        internal PartPicker(string mod, List<Item> items)
        {
            this.mod = mod ?? ""; this.items = items ?? new List<Item>();
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            BackColor = Palette.Raised; ForeColor = Palette.Text; Name = "recipePartPicker"; AccessibleName = "Parts of the selected mod to take";
            apply = new FlatButton("Apply", true) { Font = Palette.Small, Name = "recipePartApply" };
            apply.Click += delegate { if (Applied != null && Ticked.Count > 0) Applied(); };
            Controls.Add(apply);
        }
        private int Px(int logical) { return DpiMetrics.Px(this, logical); }
        // The size for the window that shows it (the control has no handle yet, so that window's scale is used).
        internal Size PreferredFor(Control owner)
        {
            Func<int, int> px = v => DpiMetrics.Px(owner, v);
            return new Size(px(WidthLogical), px(CaptionHeight) + items.Count * px(RowHeight) + px(HintHeight) + px(ButtonHeight) + px(Pad));
        }
        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            apply.Bounds = new Rectangle(Px(Pad), Height - Px(Pad) - Px(ButtonHeight), Math.Max(1, Width - Px(Pad) * 2), Px(ButtonHeight));
        }
        private Rectangle RowBounds(int index) { return new Rectangle(0, Px(CaptionHeight) + index * Px(RowHeight), Width, Px(RowHeight)); }
        private int RowAt(Point p) { for (int i = 0; i < items.Count; i++) if (RowBounds(i).Contains(p)) return i; return -1; }
        internal void Toggle(int index)
        {
            if (index < 0 || index >= items.Count) return;
            items[index].On = !items[index].On;
            apply.Enabled = items.Any(i => i.On);   // nothing ticked is nothing to take
            Invalidate();
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics; g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (Brush bg = new SolidBrush(Palette.Raised)) g.FillRectangle(bg, ClientRectangle);
            using (Pen edge = new Pen(Palette.Border)) g.DrawRectangle(edge, 0, 0, Width - 1, Height - 1);
            const TextFormatFlags one = TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter;
            int pad = Px(Pad), box = Px(Box);
            TextRenderer.DrawText(g, "TAKE FROM THIS MOD", Palette.Tiny, new Rectangle(pad, Px(6), Width - pad * 2, Px(18)), Palette.Muted, one);
            TextRenderer.DrawText(g, mod, Palette.Small, new Rectangle(pad, Px(22), Width - pad * 2, Px(18)), Palette.Text, one);
            for (int i = 0; i < items.Count; i++)
            {
                Item item = items[i]; Rectangle row = RowBounds(i);
                if (i == hover) using (Brush lit = new SolidBrush(Lit)) g.FillRectangle(lit, new Rectangle(1, row.Top, row.Width - 2, row.Height));
                var mark = new Rectangle(pad, row.Top + (row.Height - box) / 2, box, box);
                using (System.Drawing.Drawing2D.GraphicsPath shape = Glyphs.Rounded(mark, Px(3)))
                {
                    using (Brush fill = new SolidBrush(item.On ? Palette.Accent : Palette.Surface)) g.FillPath(fill, shape);
                    using (Pen pen = new Pen(item.On ? Palette.Accent : Palette.Muted)) g.DrawPath(pen, shape);
                }
                if (item.On)
                    using (Pen tick = new Pen(Palette.Background, Math.Max(1.5f, Px(2))) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round, LineJoin = System.Drawing.Drawing2D.LineJoin.Round })
                        g.DrawLines(tick, new[] { new PointF(mark.Left + mark.Width * 0.24f, mark.Top + mark.Height * 0.52f), new PointF(mark.Left + mark.Width * 0.43f, mark.Top + mark.Height * 0.70f), new PointF(mark.Left + mark.Width * 0.77f, mark.Top + mark.Height * 0.30f) });
                int detailWidth = item.Detail.Length == 0 ? 0 : TextRenderer.MeasureText(g, item.Detail, Palette.Tiny, Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine).Width + Px(4);
                int textLeft = mark.Right + Px(10);
                TextRenderer.DrawText(g, item.Label, Palette.Small, new Rectangle(textLeft, row.Top, Math.Max(1, row.Right - pad - detailWidth - textLeft), row.Height), item.On ? Palette.Text : Palette.Muted, one);
                if (detailWidth > 0) TextRenderer.DrawText(g, item.Detail, Palette.Tiny, new Rectangle(row.Right - pad - detailWidth, row.Top, detailWidth, row.Height), Palette.Muted, one | TextFormatFlags.Right);
            }
            // What the ticks mean, in a sentence, between the rows and Apply.
            int hintTop = Px(CaptionHeight) + items.Count * Px(RowHeight);
            TextRenderer.DrawText(g, Hint, Palette.Tiny, new Rectangle(pad, hintTop + Px(4), Width - pad * 2, Px(HintHeight) - Px(8)), Palette.Muted, TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
        }
        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); int at = RowAt(e.Location); if (at != hover) { hover = at; Cursor = at >= 0 ? Cursors.Hand : Cursors.Default; Invalidate(); } }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (hover >= 0) { hover = -1; Invalidate(); } }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button == MouseButtons.Left) Toggle(RowAt(e.Location)); }
    }

    internal sealed class RecipeBuilderForm : Form
    {
        private readonly List<LibraryEntry> library;
        private readonly string sevenZip;
        private readonly PickerButton fighterButton;
        private readonly ToolStripDropDown popup;
        private readonly FighterGrid grid;
        private readonly PaddedTextField nameBox, searchBox;
        private readonly SmoothList mods;
        private readonly Label notes, status, modsCaption, titleLabel;
        private readonly FlatButton showAllButton;
        private bool showAll;   // every mod of the fighter, also the ones that don't fit the recipe
        internal string ModsCaptionForTest { get { return modsCaption.Text; } }
        internal void ToggleShowAllForTest() { ToggleShowAll(); }
        private readonly PartRows rows;
        internal PartRows RowsForTest { get { return rows; } }
        private readonly FlatButton addPart, layerOptions, layerParts, remove, save, saveInstall;
        // 1.0.18 (the user: "i would like to see image previews"): the middle column shows the image of what was
        // clicked last, a mod in the list, the base or a part. Images are read off the UI thread, one at a time, the
        // newest request last; a few are kept so going back to one is instant.
        private sealed class PreviewWant { internal string Source, OptionId, Title, Detail; internal string Key { get { return Source + "|" + (OptionId ?? "*"); } } }
        private const int PreviewKept = 6, PreviewLongSide = 2560;
        private readonly Panel previewColumn;
        private readonly PictureBox preview;
        private readonly Label previewNote, previewTitle, previewDetail;
        private readonly Timer previewDelay = new Timer { Interval = 140 };
        private readonly Dictionary<string, Image> previewImages = new Dictionary<string, Image>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> previewOrder = new List<string>();
        private readonly Dictionary<string, ModPackage> previewPackages = new Dictionary<string, ModPackage>(StringComparer.OrdinalIgnoreCase);   // the reading thread only
        private PreviewWant wanted; private bool previewBusy;
        internal string PreviewForTest { get { return wanted == null ? "" : wanted.Title + " | " + wanted.Detail; } }
        internal bool PreviewLoadingForTest { get { return wanted != null && !previewImages.ContainsKey(wanted.Key); } }
        internal bool PreviewShownForTest { get { return preview.Image != null; } }
        private List<LibraryEntry> shown = new List<LibraryEntry>();
        private RecipeDraft draft;
        // The game folder, for the material check; empty in tests (then the check is skipped).
        internal string GameRoot = "";
        private string materialKey = "", lastSaid; private List<string> materialNotes = new List<string>();        internal RecipeDraft Draft { get { return draft; } }
        internal OutfitRecipe Recipe { get; private set; }
        internal bool InstallAfter { get; private set; }
        internal SmoothList ModsForTest { get { return mods; } }
        internal string NotesForTest { get { return notes.Text; } }
        internal string StatusForTest { get { return status.Text; } }
        internal string ButtonsForTest { get { return ((addPart.Enabled ? "add " : "") + (layerOptions.Enabled ? "options " : "") + (remove.Enabled ? "remove" : "")).Trim(); } }

        // edit: a saved recipe to change (the window opens with its layers), else a new one.
        internal static bool Choose(IWin32Window owner, string fighter, IEnumerable<KeyValuePair<string, string>> fighters, IEnumerable<FighterCard> portraits, List<LibraryEntry> library, string sevenZip, LibraryEntry startBase, IList<string> startIds, out OutfitRecipe recipe, out bool install, OutfitRecipe edit = null, string gameRoot = null, LibraryEntry addOnTop = null, OutfitRecipe startFrom = null, Func<string, string, bool> nameTaken = null)
        {
            recipe = null; install = false;
            if (Program.Headless) return false;
            using (var form = new RecipeBuilderForm(fighter, fighters, portraits, library, sevenZip))
            {
                form.GameRoot = gameRoot ?? ""; form.NameTaken = nameTaken;
                if (edit != null) { form.LoadRecipe(edit); form.AddOnTop = addOnTop; }
                else if (startFrom != null) form.StartFrom(startFrom);
                else form.StartOn(startBase, startIds);
                if (form.ShowDialog(owner) != DialogResult.OK || form.Recipe == null) return false;
                recipe = form.Recipe; install = form.InstallAfter;
                return true;
            }
        }

        private static string Shown(LibraryEntry e) { return String.IsNullOrWhiteSpace(e.DisplayName) ? (e.Name ?? Path.GetFileNameWithoutExtension(e.SourcePath ?? "")) : e.DisplayName; }

        internal RecipeBuilderForm(string fighter, IEnumerable<KeyValuePair<string, string>> fighters, IEnumerable<FighterCard> portraits, List<LibraryEntry> library, string sevenZip)
        {
            this.library = library ?? new List<LibraryEntry>(); this.sevenZip = sevenZip ?? "";
            SuspendLayout();
            AutoScaleDimensions = new SizeF(96F, 96F); AutoScaleMode = AutoScaleMode.Dpi;
            Text = "New outfit recipe"; StartPosition = FormStartPosition.CenterParent; Name = "recipeBuilderForm";
            BackColor = Palette.Surface; ForeColor = Palette.Text; ShowInTaskbar = false; Padding = new Padding(18, 14, 18, 14); MaximizeBox = false; MinimizeBox = false; FormBorderStyle = FormBorderStyle.FixedDialog;
            AppBranding.Apply(this);
            ClientSize = new Size(1220, 640);
            var title = titleLabel = new Label { UseMnemonic = false, Dock = DockStyle.Top, Height = 30, Font = new Font("Segoe UI", 12F, FontStyle.Bold), Text = "New outfit recipe" };
            var top = new Panel { Dock = DockStyle.Top, Height = 42, Padding = new Padding(0, 4, 0, 8) };
            fighterButton = new PickerButton { Dock = DockStyle.Left, Width = 200, Name = "recipeFighter", AccessibleName = "Fighter" };
            nameBox = new PaddedTextField { Dock = DockStyle.Fill, Name = "recipeName", AccessibleName = "Recipe name", PlaceholderText = "Name of the outfit" };
            var nameCaption = new Label { UseMnemonic = false, Dock = DockStyle.Left, Width = 70, Text = "Name", ForeColor = Palette.Muted, Font = Palette.Small, TextAlign = ContentAlignment.MiddleRight, Padding = new Padding(0, 0, 8, 0) };
            top.Controls.Add(nameBox); top.Controls.Add(nameCaption); top.Controls.Add(fighterButton);

            // Left: the fighter's mods in the library.
            var left = new Panel { Dock = DockStyle.Left, Width = 330, Padding = new Padding(0, 0, 14, 0) };
            var leftCaption = new Panel { Dock = DockStyle.Top, Height = 24, Padding = new Padding(0, 0, 0, 7) };
            modsCaption = new Label { UseMnemonic = false, Dock = DockStyle.Fill, Font = new Font("Segoe UI", 8F, FontStyle.Bold), ForeColor = Palette.Muted, AutoEllipsis = true, Text = "MODS IN YOUR LIBRARY" };
            showAllButton = new FlatButton("Show all", false) { Dock = DockStyle.Right, Width = 92, Font = Palette.Tiny, Navigation = true, Visible = false, Name = "recipeShowAll", AccessibleName = "Show every mod of this fighter, or only the ones that fit the base" };
            leftCaption.Controls.Add(modsCaption); leftCaption.Controls.Add(showAllButton);
            searchBox = new PaddedTextField { Dock = DockStyle.Top, Height = 30, Name = "recipeSearch", AccessibleName = "Search this fighter's mods", PlaceholderText = "Search this fighter's mods" };
            mods = new SmoothList(34) { Dock = DockStyle.Fill, Name = "recipeMods", AccessibleName = "Mods for this fighter" };
            var leftButtons = new Panel { Dock = DockStyle.Bottom, Height = 44, Padding = new Padding(0, 10, 0, 0) };
            // One way in for every mod (the user: no base, "it all depends on load orders"): it goes on top of the list.
            addPart = new FlatButton("Add to recipe", false) { Dock = DockStyle.Left, Width = 170, Font = Palette.Small, Name = "recipeAddPart", AccessibleName = "Add the selected mod to the recipe, on top" };
            leftButtons.Controls.Add(addPart);
            left.Controls.Add(mods); left.Controls.Add(leftButtons); left.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 6 }); left.Controls.Add(searchBox); left.Controls.Add(leftCaption);

            // Middle: the image of what was clicked last (a mod in the list, the base or a part).
            previewColumn = new Panel { Dock = DockStyle.Left, Width = 380, Padding = new Padding(0, 0, 14, 0) };
            var previewCaption = new Label { UseMnemonic = false, Dock = DockStyle.Top, Height = 24, Font = new Font("Segoe UI", 8F, FontStyle.Bold), ForeColor = Palette.Muted, Text = "PREVIEW" };
            previewDetail = new Label { UseMnemonic = false, Dock = DockStyle.Bottom, Height = 22, ForeColor = Palette.Muted, Font = Palette.Tiny, AutoEllipsis = true, Name = "recipePreviewDetail" };
            previewTitle = new Label { UseMnemonic = false, Dock = DockStyle.Bottom, Height = 30, ForeColor = Palette.Text, Font = Palette.Strong, AutoEllipsis = true, TextAlign = ContentAlignment.BottomLeft, Name = "recipePreviewTitle" };
            var previewFrame = new Panel { Dock = DockStyle.Fill, BackColor = Palette.Background };
            preview = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Palette.Background, Cursor = Cursors.Hand, Visible = false, Name = "recipePreview", AccessibleName = "Preview image. Click to enlarge." };
            previewNote = new Label { UseMnemonic = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Palette.Muted, Font = Palette.Small, BackColor = Palette.Background, Padding = new Padding(16), Text = "Click a mod to see its image." };
            previewFrame.Controls.Add(preview); previewFrame.Controls.Add(previewNote);
            previewColumn.Controls.Add(previewFrame); previewColumn.Controls.Add(previewTitle); previewColumn.Controls.Add(previewDetail); previewColumn.Controls.Add(previewCaption);

            // Right: the recipe so far.
            var right = new Panel { Dock = DockStyle.Fill };
            // The list is the whole recipe, in load order: every mod in it is a row of the same kind, the winner first.
            // Rows are dragged or moved with the buttons; Options changes what is ticked for the selected one.
            var partsCaption = new Label { UseMnemonic = false, Dock = DockStyle.Top, Height = 24, Font = new Font("Segoe UI", 8F, FontStyle.Bold), ForeColor = Palette.Muted, Text = "THE RECIPE · THE TOP ONE WINS" };
            rows = new PartRows(52, null) { Dock = DockStyle.Top, Name = "recipeParts", ModFirst = true, Empty = "Nothing in the recipe yet. Select a mod on the left and click Add to recipe.", AccessibleName = "The mods in the recipe, in load order, the winner first" };
            var listHost = new Panel { Dock = DockStyle.Top, Height = 52 * 5 + 2, AutoScroll = true, BackColor = Palette.Background };
            listHost.Controls.Add(rows);
            var partButtons = new Panel { Dock = DockStyle.Top, Height = 42, Padding = new Padding(0, 8, 0, 0) };
            remove = new FlatButton("Remove", false) { Dock = DockStyle.Left, Width = 100, Font = Palette.Small, Name = "recipeRemove", Danger = true };
            remove.Width = 84;
            layerOptions = new FlatButton("Options", false) { Dock = DockStyle.Left, Width = 84, Font = Palette.Small, Name = "recipeLayerOptions", AccessibleName = "Change the options ticked for the selected mod" };
            // 1.0.18: take only some parts of the selected mod (its hair, say), OutfitParts.cs.
            layerParts = new FlatButton("Parts", false) { Dock = DockStyle.Left, Width = 72, Font = Palette.Small, Name = "recipeLayerParts", AccessibleName = "Take only some parts of the selected mod: body, hair, head" };
            layerParts.Click += delegate { PickLayerParts(); };
            var down = new FlatButton("Move down", false) { Dock = DockStyle.Left, Width = 96, Font = Palette.Small }; down.Click += delegate { rows.MoveSelected(1); };
            var up = new FlatButton("Move up", false) { Dock = DockStyle.Left, Width = 84, Font = Palette.Small }; up.Click += delegate { rows.MoveSelected(-1); };
            partButtons.Controls.Add(remove); partButtons.Controls.Add(new Panel { Dock = DockStyle.Left, Width = 8 }); partButtons.Controls.Add(layerParts); partButtons.Controls.Add(new Panel { Dock = DockStyle.Left, Width = 8 }); partButtons.Controls.Add(layerOptions); partButtons.Controls.Add(new Panel { Dock = DockStyle.Left, Width = 8 }); partButtons.Controls.Add(down); partButtons.Controls.Add(new Panel { Dock = DockStyle.Left, Width = 8 }); partButtons.Controls.Add(up);
            notes = new Label { UseMnemonic = false, Dock = DockStyle.Fill, ForeColor = Palette.Muted, Font = Palette.Small, Padding = new Padding(0, 10, 0, 0) };
            right.Controls.Add(notes); right.Controls.Add(partButtons); right.Controls.Add(listHost); right.Controls.Add(partsCaption);

            var footer = new Panel { Dock = DockStyle.Bottom, Height = 48, Padding = new Padding(0, 14, 0, 0) };
            saveInstall = new FlatButton("Save and install", true) { Width = 150, Dock = DockStyle.Right, Font = Palette.Small, Name = "recipeSaveInstall" };
            save = new FlatButton("Save recipe", false) { Width = 120, Dock = DockStyle.Right, Font = Palette.Small, Name = "recipeSave" };
            var cancel = new FlatButton("Cancel", false) { Width = 96, Dock = DockStyle.Right, Font = Palette.Small }; cancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            status = new Label { UseMnemonic = false, Dock = DockStyle.Fill, ForeColor = Palette.Accent, Font = Palette.Small, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
            footer.Controls.Add(status); footer.Controls.Add(cancel); footer.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 8 }); footer.Controls.Add(save); footer.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 8 }); footer.Controls.Add(saveInstall);
            Controls.Add(right); Controls.Add(previewColumn); Controls.Add(left); Controls.Add(footer); Controls.Add(top); Controls.Add(title);

            // The fighter picker (one fighter), as in the Outfit order window.
            var panel = new Panel { BackColor = Palette.Surface, Padding = new Padding(8), Name = "recipeFighters" };
            grid = new FighterGrid { Dock = DockStyle.Fill, Name = "recipeGrid", AccessibleName = "Fighters" };
            grid.SetFighters(fighters ?? new KeyValuePair<string, string>[0]);
            foreach (FighterCard card in portraits ?? new FighterCard[0]) if (card.Portrait != null) grid.SetPortrait(card.Code, (Bitmap)card.Portrait.Clone(), card.PortraitNote);
            grid.SetCounts(this.library.SelectMany(e => (e.CharacterCodes ?? new List<string>()).Distinct().Select(c => c)).GroupBy(c => c).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal));
            panel.Controls.Add(grid);
            var host = new ToolStripControlHost(panel) { AutoSize = false, Margin = Padding.Empty, Padding = Padding.Empty };
            popup = new ToolStripDropDown { Padding = Padding.Empty, AutoClose = true, DropShadowEnabled = true, BackColor = Palette.Border };
            popup.Items.Add(host);
            grid.SelectionChanged += delegate
            {
                string code = grid.Selected.FirstOrDefault(c => draft == null || c != draft.Code) ?? grid.Selected.FirstOrDefault();
                if (code == null) return;
                string name; if (!Characters.ByCode.TryGetValue(code, out name)) name = FighterPortraits.DisplayName(code);
                popup.Close(); SetFighter(name, code);
            };
            fighterButton.Click += delegate
            {
                if (popup.Visible) { popup.Close(); return; }
                int width = DpiMetrics.Px(this, 720), height = Math.Min(DpiMetrics.Px(this, 520), grid.HeightFor(width - DpiMetrics.Px(this, 16)));
                panel.Size = new Size(width, height + DpiMetrics.Px(this, 16)); host.Size = panel.Size;
                popup.Show(fighterButton, new Point(0, fighterButton.Height + DpiMetrics.Px(this, 4))); grid.Focus();
            };
            Disposed += delegate { popup.Dispose(); };

            searchBox.TextChanged += delegate { FillMods(); };
            showAllButton.Click += delegate { ToggleShowAll(); };
            mods.SelectedIndexChanged += delegate { if (draft != null) UpdateState(null); PreviewSelectedMod(); };
            mods.Click += delegate { PreviewSelectedMod(); };
            rows.Click += delegate { PreviewSelectedPart(); };
            preview.Click += delegate { EnlargePreview(); };
            previewDelay.Tick += delegate { previewDelay.Stop(); StartPreviewLoad(); };
            Disposed += delegate
            {
                previewDelay.Dispose(); preview.Image = null;
                foreach (Image image in previewImages.Values) if (image != null) image.Dispose();
                previewImages.Clear(); previewOrder.Clear();
            };
            mods.Activated += delegate { AddSelectedPart(); };
            addPart.Click += delegate { AddSelectedPart(); };
            layerOptions.Click += delegate { PickLayerOptions(); };
            remove.Click += delegate { rows.RemoveSelected(); };
            rows.Changed += delegate { SyncLayers(); };
            rows.SelectionChanged += delegate { UpdateState(null); PreviewSelectedPart(); };
            nameBox.TextChanged += delegate { UpdateState(null); };
            save.Click += delegate { Accept(false); };
            saveInstall.Click += delegate { Accept(true); };
            KeyPreview = true;
            KeyDown += delegate(object s, KeyEventArgs e) { if (e.KeyCode == Keys.Escape && !popup.Visible) { DialogResult = DialogResult.Cancel; Close(); } };
            DarkTheme.Apply(this); ResumeLayout(true); PerformAutoScale();
            previewFrame.BackColor = preview.BackColor = previewNote.BackColor = Palette.Background;

            string start = null, shownName = fighter ?? "";
            foreach (var kv in Characters.ByCode) if (String.Equals(kv.Value, fighter, StringComparison.OrdinalIgnoreCase)) { start = kv.Key; shownName = kv.Value; }
            SetFighter(start == null ? "" : shownName, start ?? "");
        }

        // The window never opens larger than the screen it is on: at 300% scaling a 4K screen has room for about
        // 1280 x 670, and the footer buttons were cut off. The preview column gives way first when it is narrow.
        protected override void OnLoad(EventArgs e)
        {
            Rectangle area = Screen.FromControl(Owner ?? (Control)this).WorkingArea;
            int wide = Math.Min(Width, area.Width), high = Math.Min(Height, area.Height), lost = Width - wide;
            if (lost > 0) previewColumn.Width = Math.Max(DpiMetrics.Px(this, 240), previewColumn.Width - lost);
            if (wide != Width || high != Height) Size = new Size(wide, high);
            base.OnLoad(e);
        }
        // 1.0.18 (the user: "add an option to add to existing recipe too"): opened from a mod's "Add to recipe", the
        // saved recipe is loaded and that mod goes on top once the window is up (its options are asked for then).
        internal LibraryEntry AddOnTop;
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e); StartPreviewLoad();
            if (draft != null && draft.Layers.Count > 0) UpdateState(lastSaid);
            if (AddOnTop != null) { LibraryEntry add = AddOnTop; AddOnTop = null; SelectMod(add); AddEntry(add); }
        }

        // ---- The preview column ----
        // The image of one mod (optionId null: the image the Mod library shows for it) or of one of its options.
        internal static Image ReadPreview(ModPackage package, string optionId, string sevenZip)
        {
            string id = optionId;
            if (id == null) { try { id = Composer.DefaultSelection(package, false).FirstOrDefault(); } catch (Exception ex) { if (ex is OutOfMemoryException) throw; } }
            using (PreviewImageResult decoded = PreviewImageResult.Decode(package.PreviewBytes(id, sevenZip)))
            {
                Image full = decoded.TakeImage();
                if (full == null || Math.Max(full.Width, full.Height) <= PreviewLongSide) return full;
                // A very large screenshot is kept smaller: several of them stay in memory while the window is open.
                using (full)
                {
                    double scale = (double)PreviewLongSide / Math.Max(full.Width, full.Height);
                    var small = new Bitmap(Math.Max(1, (int)(full.Width * scale)), Math.Max(1, (int)(full.Height * scale)));
                    using (Graphics g = Graphics.FromImage(small)) { g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic; g.DrawImage(full, 0, 0, small.Width, small.Height); }
                    return small;
                }
            }
        }
        private void WantPreview(string source, string optionId, string title, string detail)
        {
            if (String.IsNullOrEmpty(source)) { ClearPreview(); return; }
            wanted = new PreviewWant { Source = source, OptionId = optionId, Title = title ?? "", Detail = detail ?? "" };
            previewTitle.Text = wanted.Title; previewDetail.Text = wanted.Detail;
            if (previewImages.ContainsKey(wanted.Key)) { previewDelay.Stop(); ShowWanted(); return; }
            preview.Image = null; preview.Visible = false; previewNote.Text = "Loading the image…"; previewNote.Visible = true;
            previewDelay.Stop(); previewDelay.Start();
        }
        private void ClearPreview()
        {
            wanted = null; previewDelay.Stop(); preview.Image = null; preview.Visible = false;
            previewTitle.Text = previewDetail.Text = ""; previewNote.Text = "Click a mod to see its image."; previewNote.Visible = true;
        }
        private void ShowWanted()
        {
            Image image; previewImages.TryGetValue(wanted.Key, out image);
            previewOrder.Remove(wanted.Key); previewOrder.Add(wanted.Key);
            preview.Image = image; preview.Visible = image != null;
            previewNote.Text = image == null ? "This mod has no preview image." : ""; previewNote.Visible = image == null;
        }
        private void StartPreviewLoad()
        {
            if (previewBusy || wanted == null || !IsHandleCreated || IsDisposed || previewImages.ContainsKey(wanted.Key)) return;
            previewBusy = true;
            PreviewWant want = wanted; string zip = sevenZip;
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                Image image = null;
                try
                {
                    ModPackage package;
                    if (!previewPackages.TryGetValue(want.Source, out package))
                    {
                        if (previewPackages.Count >= 4) previewPackages.Clear();
                        previewPackages[want.Source] = package = PackageReader.Load(want.Source, zip, false);
                    }
                    image = ReadPreview(package, want.OptionId, zip);
                }
                catch (Exception ex) { try { Program.Log(ex); } catch { } }   // no image is the answer; nothing may escape a pool thread
                try { BeginInvoke((MethodInvoker)delegate { PreviewLoaded(want, image); }); }
                catch (InvalidOperationException) { if (image != null) image.Dispose(); }   // the window closed meanwhile
            });
        }
        private void PreviewLoaded(PreviewWant want, Image image)
        {
            previewBusy = false;
            if (IsDisposed || previewImages.ContainsKey(want.Key)) { if (image != null) image.Dispose(); return; }
            previewImages[want.Key] = image; previewOrder.Add(want.Key);
            while (previewOrder.Count > PreviewKept)
            {
                string oldest = previewOrder.FirstOrDefault(k => wanted == null || k != wanted.Key); if (oldest == null) break;
                Image old = previewImages[oldest]; previewImages.Remove(oldest); previewOrder.Remove(oldest);
                if (old != null && !ReferenceEquals(old, preview.Image)) old.Dispose();
            }
            if (wanted != null && wanted.Key == want.Key) ShowWanted(); else StartPreviewLoad();
        }
        // Until the image is there (screenshots and checks; the window must be shown).
        internal void WaitPreviewForTest(int milliseconds)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            if (previewDelay.Enabled) { previewDelay.Stop(); StartPreviewLoad(); }
            while (PreviewLoadingForTest && clock.ElapsedMilliseconds < milliseconds) { Application.DoEvents(); System.Threading.Thread.Sleep(10); }
        }
        private bool InRecipe(string source) { return draft != null && draft.Layers.Any(l => String.Equals(l.SourcePath, source, StringComparison.OrdinalIgnoreCase)); }
        private void PreviewSelectedMod()
        {
            LibraryEntry e = SelectedMod; if (e == null) { if (draft == null || draft.Layers.Count == 0) ClearPreview(); return; }
            WantPreview(e.SourcePath, null, Shown(e), InRecipe(e.SourcePath) ? "In this recipe" : "In your library");
        }
        // A row of the recipe: the mod's own image when it is the whole mod, else the image of its first option.
        private void PreviewSelectedPart()
        {
            List<PartGraft> parts = rows.Parts; int at = rows.SelectedIndex;
            if (at < 0 || at >= parts.Count) { PreviewSelectedMod(); return; }
            PartGraft part = parts[at];
            string mod = String.IsNullOrEmpty(part.Mod) ? Path.GetFileName(part.SourcePath ?? "") : part.Mod;
            List<string> options = part.Options ?? new List<string>();
            bool whole = options.Count == 0 || (options.Count == 1 && String.Equals(options[0], mod, StringComparison.OrdinalIgnoreCase));
            WantPreview(part.SourcePath, whole || part.SelectedIds == null ? null : part.SelectedIds.FirstOrDefault(), mod,
                "#" + (at + 1) + " in the recipe" + (whole ? "" : " · " + (options.Count <= 2 ? String.Join(" + ", options) : ReplacePlanner.N(options.Count, "option"))));
        }
        private void EnlargePreview()
        {
            if (preview.Image == null || Program.Headless) return;
            using (Image copy = new Bitmap(preview.Image))
            using (var viewer = new Form())
            {
                viewer.SuspendLayout();
                viewer.AutoScaleDimensions = new SizeF(96F, 96F); viewer.AutoScaleMode = AutoScaleMode.Dpi;
                viewer.Text = previewTitle.Text + " · Preview"; viewer.ClientSize = new Size(1000, 780); viewer.StartPosition = FormStartPosition.CenterParent;
                viewer.BackColor = Palette.Background; viewer.KeyPreview = true; viewer.ShowInTaskbar = false;
                AppBranding.Apply(viewer);
                viewer.Controls.Add(new PictureBox { Dock = DockStyle.Fill, Image = copy, SizeMode = PictureBoxSizeMode.Zoom });
                viewer.KeyDown += delegate(object s, KeyEventArgs e) { if (e.KeyCode == Keys.Escape) viewer.Close(); };
                viewer.ResumeLayout(true); viewer.PerformAutoScale();
                Rectangle available = Screen.FromControl(this).WorkingArea;
                viewer.Size = new Size(Math.Min(viewer.Width, available.Width - DpiMetrics.Px(this, 48)), Math.Min(viewer.Height, available.Height - DpiMetrics.Px(this, 48)));
                viewer.ShowDialog(this);
            }
        }

        internal void SetFighter(string name, string code)
        {
            ClearPreview();
            draft = new RecipeDraft(name, code, sevenZip); showAll = false;
            fighterButton.Text = String.IsNullOrEmpty(code) ? "Choose a fighter" : name;
            if (!String.IsNullOrEmpty(code)) grid.SetSelected(new[] { code }, false);
            rows.SetParts(new List<PartGraft>());
            FillMods(); UpdateState(null);
        }
        private void FillMods()
        {
            string text = (searchBox.Text ?? "").Trim();
            List<LibraryEntry> all = draft == null || draft.Code.Length == 0 ? new List<LibraryEntry>() : library.Where(e => e.CharacterCodes != null && e.CharacterCodes.Contains(draft.Code) && (e.ToolKinds == null || e.ToolKinds.Count == 0)).ToList();
            // Once the recipe has a mod, only the mods that have something for its outfit (that slot or shared files); Show all brings the rest back.
            List<string> slots = draft == null ? new List<string>() : draft.Slots();
            List<LibraryEntry> fitting = slots.Count == 0 ? all : all.Where(draft.Fits).ToList();
            bool narrowed = fitting.Count < all.Count;
            modsCaption.Text = !narrowed ? "MODS IN YOUR LIBRARY" : showAll ? "ALL " + all.Count + " MODS" : "FITS " + String.Join(", ", slots.Select(s => "C" + Int32.Parse(s))) + " · " + fitting.Count + " OF " + all.Count;
            showAllButton.Visible = narrowed; showAllButton.Text = showAll ? "Only fitting" : "Show all";
            // 1.0.19: a colour word ("color pack", "cmd") also finds the colour packs (and, for cmd, the mods with colour
            // files) whatever their name, the colour packs first (LibrarySearch.ColourKeyword).
            string colour = LibrarySearch.ColourKeyword(text);
            shown = (narrowed && !showAll ? fitting : all).Where(e => text.Length == 0 || Shown(e).IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0 || LibrarySearch.MatchesColour(e, colour))
                .OrderBy(e => colour == null ? 0 : LibrarySearch.ColourRank(e)).ThenBy(Shown, StringComparer.OrdinalIgnoreCase).ToList();
            mods.SetRows(shown.Select(Shown));
        }
        private void ToggleShowAll() { LibraryEntry kept = SelectedMod; showAll = !showAll; FillMods(); SelectMod(kept); }
        // 1.0.18 (Edit recipe in the Mod library's right-click menu): the window with a saved recipe's fighter, name and
        // layers in their order. A layer whose mod is gone or changed is left out and named in the status line.
        internal void LoadRecipe(OutfitRecipe recipe)
        {
            if (recipe == null) return;
            Text = titleLabel.Text = "Edit outfit recipe";
            string code = Characters.ByCode.Where(kv => String.Equals(kv.Value, recipe.Character, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key).FirstOrDefault();
            if (code != null && (draft == null || draft.Code != code)) SetFighter(Characters.ByCode[code], code);
            nameBox.Text = recipe.Name;
            Func<RecipeLayer, LibraryEntry> entryOf = l => library.FirstOrDefault(e => ReplacePlanner.SameSource(e.SourcePath, l.SourcePath))
                ?? new LibraryEntry { SourcePath = l.SourcePath, Name = l.ModName, DisplayName = l.ModName, CharacterCodes = new List<string> { draft.Code } };
            var problems = new List<string>();
            foreach (RecipeLayer layer in new[] { recipe.Base }.Concat(recipe.Parts))   // bottom-up: each goes on top of the ones before it
            {
                AddPart(entryOf(layer), layer.SelectedIds.Count > 0 ? layer.SelectedIds : null, layer.Parts);
                if (status.Text.Length > 0) problems.Add(layer.ModName + ": " + status.Text);
            }
            if (problems.Count > 0) UpdateState("Left out. " + String.Join(" ", problems));
        }
        // 1.0.18 (the user: New recipe on an installed outfit should open "with all the edit parts and the mods for
        // that extra mod ... automatically added", not save at once, "because oftentimes you want to rename it"): a new
        // recipe that starts with these layers and this name. It is a new recipe: the window says so, and saving
        // makes one (or replaces one of the same name, as always).
        internal void StartFrom(OutfitRecipe recipe)
        {
            if (recipe == null) return;
            LoadRecipe(recipe);
            Text = titleLabel.Text = "New outfit recipe";
        }
        // The user: "if i click a mod and select outfit recipe through it ... it should be selected ... already".
        // The mod the window was opened on is its first layer: with the options ticked in the Mod library when they
        // can be added, else as the mod installs by default; a mod for several fighters is tried for each of them.
        internal void StartOn(LibraryEntry start, IList<string> startIds)
        {
            if (start == null) return;
            var codes = new List<string> { draft == null ? "" : draft.Code };
            codes.AddRange((start.CharacterCodes ?? new List<string>()).Where(c => Characters.ByCode.ContainsKey(c)));
            foreach (string code in codes.Where(c => !String.IsNullOrEmpty(c)).Distinct())
            {
                if (draft == null || draft.Code != code) SetFighter(Characters.ByCode[code], code);
                SelectMod(start);
                if (startIds != null && startIds.Count > 0) AddPart(start, startIds);
                if (draft.Layers.Count == 0) AddPart(start, null);
                if (draft.Layers.Count > 0) return;
            }
        }
        // The mod the window was opened on is the selected row, so the two lists agree.
        internal void SelectMod(LibraryEntry entry) { int at = entry == null ? -1 : shown.IndexOf(entry); if (at >= 0) mods.SelectedIndex = at; }
        private LibraryEntry SelectedMod { get { return mods.SelectedIndex >= 0 && mods.SelectedIndex < shown.Count ? shown[mods.SelectedIndex] : null; } }

        private string Try(Func<string> action)
        {
            Cursor previous = Cursor; Cursor = Cursors.WaitCursor;
            try { return action(); }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); return ex.Message; }
            finally { Cursor = previous; }
        }
        private string FitKey() { return draft == null ? "" : String.Join(",", draft.Slots()); }
        // The mod list follows the recipe: when the outfit it is made for changes, the list narrows (or widens) again.
        private void RefreshFit(string before, LibraryEntry keep)
        {
            if (FitKey() == before) return;
            LibraryEntry kept = keep ?? SelectedMod; FillMods(); SelectMod(kept);
        }
        // Add to recipe: the selected mod goes on top. The first one goes in as it installs by default (Options
        // changes what is ticked). After that, a mod with one option that fits goes straight in (the user: with "a
        // single installable option and no other variants ... we don't need to open a window"); with several, the
        // window asks which.
        internal void AddSelectedPart()
        {
            AddEntry(SelectedMod);
        }
        internal void AddEntry(LibraryEntry e)
        {
            if (e == null || draft == null) return;
            ModPackage package = null;
            string why = Try(delegate { package = PackageReader.Load(e.SourcePath, sevenZip, false); return null; });
            if (package == null) { UpdateState(why); return; }
            if (draft.Layers.Count == 0) { AddPart(e, null); return; }
            List<ModComponent> fitting = RecipeDraft.Options(package).Where(draft.Fits).ToList();
            if (fitting.Count == 0) { UpdateState("Nothing in " + Shown(e) + " fits this recipe: its options are made for other outfits."); return; }
            if (fitting.Count == 1) { AddPart(e, new[] { fitting[0].Id }); return; }
            List<string> picked = OptionPickForm.Choose(this, Shown(e), "Tick the options to add. Only their files go into the recipe, on top of what is there.", package, new string[0], sevenZip, null, draft.Fits);
            if (picked == null) return;
            AddPart(e, picked);
        }
        // Parts: only some parts of the selected row's mod. A small menu of the parts its options have, ticked = taken.
        private void PickLayerParts()
        {
            List<PartGraft> parts = rows.Parts; int at = rows.SelectedIndex;
            if (draft == null || at < 0 || at >= parts.Count) return;
            PartGraft layer = parts[at];
            List<string> available = null;
            string why = Try(delegate { available = draft.PartsOf(layer); return null; });
            if (available == null) { UpdateState(why); return; }
            if (available.Count < 2) { UpdateState(layer.Mod + " has only " + (available.Count == 0 ? "other files" : OutfitParts.Label(available[0]).ToLowerInvariant()) + " in these options: there is nothing to leave out."); return; }
            // 1.0.18 (the user, about the first version, a plain menu with the system's light check boxes: "this has a
            // weird UI for the parts"): a small panel under the button in the app's own look. One row per part with the
            // recipe view's tick and how many files it is; the whole row is the click. Apply takes the ticked parts;
            // a click elsewhere closes it and changes nothing.
            Dictionary<string, int> counts = null;
            Try(delegate { counts = draft.PartCounts(layer); return null; });
            bool all = layer.Parts == null || layer.Parts.Count == 0;
            var picker = new PartPicker(layer.Mod, available.Select(p => new PartPicker.Item { Part = p, Label = OutfitParts.Label(p), Detail = counts != null && counts.ContainsKey(p) ? ReplacePlanner.N(counts[p], "file") : "", On = all || layer.Parts.Contains(p, StringComparer.OrdinalIgnoreCase) }).ToList());
            picker.Size = picker.PreferredFor(this);
            var drop = new ToolStripDropDown { Padding = Padding.Empty, Margin = Padding.Empty, BackColor = Palette.Raised, DropShadowEnabled = true, AutoClose = true };
            drop.Items.Add(new ToolStripControlHost(picker) { AutoSize = false, Margin = Padding.Empty, Padding = Padding.Empty, Size = picker.Size });
            bool apply = false;
            picker.Applied += delegate { apply = true; drop.Close(); };
            drop.Closed += delegate
            {
                List<string> ticked = picker.Ticked; bool go = apply;
                BeginInvoke((MethodInvoker)delegate { drop.Dispose(); if (go && ticked.Count > 0) ChangeLayerParts(layer, ticked.Count == available.Count ? null : ticked); });
            };
            drop.Show(layerParts, new Point(0, layerParts.Height + DpiMetrics.Px(this, 4)));
        }
        // parts null or empty: the whole layer again.
        internal void ChangeLayerParts(PartGraft layer, IList<string> parts)
        {
            if (draft == null || layer == null) return;
            string fit = FitKey();
            string why = Try(delegate { return draft.ChangeParts(layer, parts); });
            RefreshFit(fit, null);
            rows.SetParts(draft.Layers);
            UpdateState(why);
        }
        // ids null: the mod as it installs by default.
        internal void AddPart(LibraryEntry entry, IList<string> ids) { AddPart(entry, ids, null); }
        internal void AddPart(LibraryEntry entry, IList<string> ids, IList<string> onlyParts)
        {
            if (draft == null || entry == null) return;
            bool first = draft.Layers.Count == 0; string fit = FitKey();
            string why = Try(delegate { return draft.AddLayer(entry.SourcePath, Shown(entry), ids, onlyParts); });
            if (why == null && first && String.IsNullOrWhiteSpace(nameBox.Text)) nameBox.Text = Shown(entry);
            if (why == null) RefreshFit(fit, first ? entry : null);
            rows.SetParts(draft.Layers);
            UpdateState(why);
            if (why == null) PreviewSelectedPart();   // the new one is the top row, which SetParts selects
        }
        // Options: what is ticked for the selected row's mod, in its place in the list.
        private void PickLayerOptions()
        {
            List<PartGraft> parts = rows.Parts; int at = rows.SelectedIndex;
            if (draft == null || at < 0 || at >= parts.Count) return;
            PartGraft layer = parts[at];
            ModPackage package = null;
            string why = Try(delegate { package = PackageReader.Load(layer.SourcePath, sevenZip, false); return null; });
            if (package == null) { UpdateState(why); return; }
            List<string> picked = OptionPickForm.Choose(this, layer.Mod, "The options of this mod that go into the recipe.", package, layer.SelectedIds, sevenZip);
            if (picked == null) return;
            ChangeOptions(layer, picked);
        }
        internal void ChangeOptions(PartGraft layer, IList<string> ids)
        {
            int at = draft.Layers.IndexOf(layer); string fit = FitKey();
            string why = Try(delegate { return draft.ChangeLayer(layer, ids); });
            rows.SetParts(draft.Layers);
            if (at > 0 && at < rows.Count) rows.Select(at);
            if (why == null) RefreshFit(fit, null);
            UpdateState(why ?? (draft.LastDropped.Count > 0 ? "Taken off, no longer made for this recipe's outfit: " + String.Join("; ", draft.LastDropped) : null));
            PreviewSelectedPart();
        }
        // The rows were moved or one was removed.
        private void SyncLayers()
        {
            if (draft == null) return;
            string fit = FitKey();
            List<PartGraft> now = rows.Parts;
            draft.Layers.Clear(); draft.Layers.AddRange(now);
            RefreshFit(fit, null);
            UpdateState(null);
            if (now.Count > 0) PreviewSelectedPart(); else PreviewSelectedMod();
        }
        private void UpdateState(string said)
        {
            bool any = draft != null && draft.Layers.Count > 0;
            addPart.Enabled = SelectedMod != null;
            layerOptions.Enabled = layerParts.Enabled = remove.Enabled = any && rows.SelectedIndex >= 0;
            rows.Height = DpiMetrics.Px(this, 52) * Math.Max(1, rows.Count);
            List<string> list = draft == null ? new List<string>() : draft.Notes();
            // Material files that point at textures nobody brings (MaterialCheck.cs), read in the background.
            if (draft != null && !String.IsNullOrWhiteSpace(GameRoot))
            {
                List<MaterialCheck.Layer> layers = MaterialCheck.Layers(draft);
                string key = MaterialCheck.Key(layers, GameRoot);
                lastSaid = said;
                if (key != materialKey && IsHandleCreated)
                {
                    materialKey = key; materialNotes = new List<string>();
                    MaterialCheck.Begin(this, layers, sevenZip, GameRoot, delegate(List<string> found) { if (key == materialKey && found.Count > 0) { materialNotes = found; UpdateState(lastSaid); } });
                }
                list.InsertRange(0, materialNotes);
            }
            // 1.0.18: who supplies the body, the hair and the head (the top layer that has each); Parts and the order change it.
            string suppliers = draft == null ? "" : draft.Suppliers();
            notes.Text = (suppliers.Length > 0 ? suppliers + Environment.NewLine : "") + (list.Count > 0 ? String.Join(Environment.NewLine, list.Select(n => "• " + n))
                : !any ? "" : draft.Layers.Count == 1 ? "One mod so far. Add more: a mod higher in the list wins the files it shares with the ones below it."
                : "Nothing overlaps. A mod higher in the list wins the files it shares with the ones below it.");
            bool named = !String.IsNullOrWhiteSpace(nameBox.Text);
            save.Enabled = saveInstall.Enabled = any && named;
            status.Text = said ?? (any && !named ? "Name the recipe." : "");
        }
        private void Accept(bool install)
        {
            if (draft == null || draft.Layers.Count == 0) return;
            try { Recipe = draft.ToRecipe(nameBox.Text); Safety.Segment(Recipe.Name); }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; Recipe = null; UpdateState(ex.Message); return; }
            // 1.0.18 (review): saving under the name of another recipe of this fighter replaces that recipe. It is said
            // first; the same button again goes ahead.
            string key = Recipe.Name + "\n" + Recipe.Character;
            if (NameTaken != null && NameTaken(Recipe.Name, Recipe.Character) && !String.Equals(replaceConfirmed, key, StringComparison.OrdinalIgnoreCase))
            {
                replaceConfirmed = key; string taken = Recipe.Name; Recipe = null;
                UpdateState("There is already a recipe named " + taken + " for " + draft.Character + ". Save again to replace it, or change the name.");
                return;
            }
            InstallAfter = install; DialogResult = DialogResult.OK; Close();
        }
        // (name, fighter): another saved recipe has this name for this fighter (not the one being edited).
        internal Func<string, string, bool> NameTaken;
        private string replaceConfirmed;
    }
}
