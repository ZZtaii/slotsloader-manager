using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using System.Windows.Forms;

namespace SlotsInstaller
{
    // 2.3.0 library Filters panel: shown to the right of the library list, in place of the options column
    // (the inspector and footer stay). One screen, top to bottom: the "FIGHTERS" line (summary, Clear), the
    // fighter grid, and three tiles in the same style: Code tools and Misc (kinds; Misc shows its kind chips when
    // selected) and Installed (a narrowing toggle). The panel scrolls only when the window is too short for it.
    internal sealed class FilterPanel : Panel
    {
        internal const string ToolsKind = "tools", MiscKind = "misc", InstalledKind = "installed", LibraryPrefix = "lib:";
        // Hover text of the tiles (library tiles: the full path).
        internal event EventHandler FiltersChanged;
        internal readonly FighterGrid Grid = new FighterGrid();
        internal readonly FighterGrid Kinds = new FighterGrid { KeepOrder = true };
        internal readonly FlowLayoutPanel MiscChips;
        internal readonly Label Title, KindsTitle;
        // 1.0.8: MY FILTERS, the user's keyword filters (LibraryFilters.cs): one chip each, "+ New filter", and the
        // inline editor below them (MainFormCustomFilters.cs fills and drives them).
        internal readonly FlowLayoutPanel CustomChips;
        internal readonly Label CustomTitle;
        internal readonly CustomFilterEditor Editor;
        private readonly Panel body, header, kindsHeader;
        private bool arranging;

        internal FilterPanel()
        {
            BackColor = Palette.Surface; ForeColor = Palette.Text; AutoScroll = true;
            body = new Panel { Location = Point.Empty, BackColor = Palette.Surface, Margin = Padding.Empty, Padding = Padding.Empty };
            header = new Panel { Dock = DockStyle.Top, Height = 22, BackColor = Palette.Surface };
            // 2.4.1: the FIGHTERS row is just the heading; Clear is next to Filters above the mod list.
            Title = new Label { UseMnemonic = false, Text = "FIGHTERS", Dock = DockStyle.Fill, Font = new Font("Segoe UI", 8F, FontStyle.Bold), ForeColor = Palette.Muted, TextAlign = ContentAlignment.MiddleLeft };
            header.Controls.Add(Title);
            Grid.Dock = DockStyle.Top; Grid.AutoScroll = false; Grid.AccessibleName = "Fighters";
            Grid.SelectionChanged += delegate { Raise(); };
            kindsHeader = new Panel { Dock = DockStyle.Top, Height = 22, BackColor = Palette.Surface };
            KindsTitle = new Label { UseMnemonic = false, Text = "TOOL MODS, MISC, INSTALLED", Dock = DockStyle.Fill, Font = new Font("Segoe UI", 8F, FontStyle.Bold), ForeColor = Palette.Muted, TextAlign = ContentAlignment.BottomLeft };
            kindsHeader.Controls.Add(KindsTitle);
            Kinds.Dock = DockStyle.Top; Kinds.AutoScroll = false; Kinds.AccessibleName = "Tool mods, misc and installed mods";
            Kinds.SetFighters(new[] { new KeyValuePair<string, string>(ToolsKind, "Tool mods"), new KeyValuePair<string, string>(MiscKind, "Misc"), new KeyValuePair<string, string>(InstalledKind, "Installed") });
            Kinds.AccessibleNames[InstalledKind] = "Installed mods only"; Kinds.GroupBreak = 3;
            Kinds.SetPortrait(ToolsKind, KindArt.Placeholder(ToolsKind), "placeholder");
            Kinds.SetPortrait(MiscKind, KindArt.Placeholder(MiscKind), "placeholder");
            Kinds.SetPortrait(InstalledKind, KindArt.Placeholder(InstalledKind), "placeholder");
            Kinds.SelectionChanged += delegate { MiscChips.Visible = Kinds.Selected.Contains(MiscKind); Arrange(); Raise(); };
            MiscChips = new FlowLayoutPanel { Dock = DockStyle.Top, WrapContents = true, Visible = false, BackColor = Palette.Surface, Padding = new Padding(4, 2, 0, 4), Margin = Padding.Empty, AccessibleName = "Misc kinds" };
            // The MY FILTERS caption starts the chip row (a line of its own made the panel scroll at 1460x920 with the
            // Misc chips showing); MainFormCustomFilters.FillCustomChips keeps it first and sizes it like a chip.
            CustomTitle = new Label { UseMnemonic = false, Text = "MY FILTERS", AutoSize = false, Font = new Font("Segoe UI", 8F, FontStyle.Bold), ForeColor = Palette.Muted, TextAlign = ContentAlignment.MiddleLeft, Margin = Padding.Empty };
            CustomChips = new FlowLayoutPanel { Dock = DockStyle.Top, WrapContents = true, BackColor = Palette.Surface, Padding = new Padding(4, 6, 0, 0), Margin = Padding.Empty, AccessibleName = "My filters" };
            CustomChips.Controls.Add(CustomTitle);
            Editor = new CustomFilterEditor { Dock = DockStyle.Top, Visible = false };
            Editor.HeightChanged += delegate { Arrange(); };
            // Dock order: the last control added is docked first (top).
            body.Controls.Add(Editor); body.Controls.Add(CustomChips);
            body.Controls.Add(MiscChips); body.Controls.Add(Kinds); body.Controls.Add(kindsHeader); body.Controls.Add(Grid); body.Controls.Add(header);
            Controls.Add(body);
        }

        internal Panel Body { get { return body; } }
        protected override void OnLayout(LayoutEventArgs e) { Arrange(); base.OnLayout(e); }
        protected override void OnResize(EventArgs e) { base.OnResize(e); Arrange(); }

        // The grids are as tall as their cards, the chips as their rows; the panel scrolls the rest.
        internal void Arrange()
        {
            if (arranging || body == null) return;
            arranging = true;
            try
            {
                int width = Math.Max(1, ClientSize.Width - Padding.Horizontal);
                int gridHeight = Grid.HeightFor(width), kindsHeight = Kinds.HeightFor(width);
                if (Grid.Height != gridHeight) Grid.Height = gridHeight;
                if (Kinds.Height != kindsHeight) Kinds.Height = kindsHeight;
                int chips = 0;
                if (MiscChips.Visible) { chips = MiscChips.GetPreferredSize(new Size(width, 0)).Height; if (MiscChips.Height != chips) MiscChips.Height = chips; }
                int custom = CustomChips.GetPreferredSize(new Size(width, 0)).Height; if (CustomChips.Height != custom) CustomChips.Height = custom;
                int editor = 0;
                if (Editor.Visible) { editor = Editor.HeightFor(width); if (Editor.Height != editor) Editor.Height = editor; }
                var size = new Size(width, header.Height + gridHeight + kindsHeader.Height + kindsHeight + chips + custom + editor);
                if (body.Size != size) body.Size = size;
                if (body.Left != Padding.Left + AutoScrollPosition.X) body.Left = Padding.Left + AutoScrollPosition.X;
            }
            finally { arranging = false; }
        }

        internal bool ToolsOn { get { return Kinds.Selected.Contains(ToolsKind); } }
        internal bool MiscOn { get { return Kinds.Selected.Contains(MiscKind); } }
        // Installed is a narrowing toggle (AND), not a kind.
        internal bool InstalledOn { get { return Kinds.Selected.Contains(InstalledKind); } }
        // Selected library tiles (full paths); none with a single library, whose tiles are hidden.
        internal HashSet<string> LibrariesSelected { get { return new HashSet<string>(Kinds.Selected.Where(c => c.StartsWith(LibraryPrefix, StringComparison.Ordinal)).Select(c => c.Substring(LibraryPrefix.Length)), StringComparer.OrdinalIgnoreCase); } }
        internal static string LibraryKey(string path)
        {
            string full = path ?? "";
            try { full = System.IO.Path.GetFullPath(full); } catch (Exception ex) { if (!(ex is ArgumentException || ex is NotSupportedException || ex is System.IO.PathTooLongException || ex is System.Security.SecurityException)) throw; }
            return full.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
        }
        // One tile per configured library after Installed (hidden with a single library). The label is the
        // folder name or, when two libraries share it, the nearest folder above that tells them apart; selections
        // of removed libraries go.
        internal void SetLibraries(IList<string> paths)
        {
            var keys = (paths ?? new List<string>()).Where(p => !String.IsNullOrWhiteSpace(p)).Select(LibraryKey).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var tiles = new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>(ToolsKind, "Tool mods"), new KeyValuePair<string, string>(MiscKind, "Misc"), new KeyValuePair<string, string>(InstalledKind, "Installed") };
            foreach (string code in Kinds.AccessibleNames.Keys.Where(k => k.StartsWith(LibraryPrefix, StringComparison.Ordinal)).ToList()) Kinds.AccessibleNames.Remove(code);
            if (keys.Count > 1)
            {
                Func<string, string> leaf = delegate(string p) { string n = System.IO.Path.GetFileName(p); return String.IsNullOrEmpty(n) ? p : n; };
                foreach (string key in keys)
                {
                    string label = leaf(key);
                    var same = keys.Where(k => String.Equals(leaf(k), label, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (same.Count > 1)
                    {
                        // The nearest folder above that tells them apart: <drive>\Fluffy\Games\SF6\Mods and
                        // <drive>\Games\Fluffy - Copy\Games\SF6\Mods -> "Fluffy" and "Fluffy - Copy" (the full path is in the tooltip).
                        Func<string, int, string> up = delegate(string p, int levels) { string d = p; for (int i = 0; i < levels && !String.IsNullOrEmpty(d); i++) d = System.IO.Path.GetDirectoryName(d); return String.IsNullOrEmpty(d) ? "" : leaf(d); };
                        for (int levels = 1; levels < 16; levels++)
                        {
                            var names = same.Select(k => up(k, levels)).ToList();
                            if (names.Distinct(StringComparer.OrdinalIgnoreCase).Count() == names.Count) { string mine = up(key, levels); if (mine.Length > 0) label = mine; break; }
                        }
                    }
                    string code = LibraryPrefix + key;
                    tiles.Add(new KeyValuePair<string, string>(code, label));
                    Kinds.AccessibleNames[code] = "Library " + key;
                }
            }
            bool selectionLost = Kinds.Selected.Any(c => c.StartsWith(LibraryPrefix, StringComparison.Ordinal) && !tiles.Any(t => t.Key == c));
            Kinds.SetFighters(tiles);
            foreach (FighterCard card in Kinds.Cards.Where(c => c.Portrait == null && c.Code.StartsWith(LibraryPrefix, StringComparison.Ordinal)).ToList()) Kinds.SetPortrait(card.Code, KindArt.Placeholder("library"), "placeholder");
            KindsTitle.Text = keys.Count > 1 ? "TOOL MODS, MISC, INSTALLED, LIBRARIES" : "TOOL MODS, MISC, INSTALLED";
            Kinds.AccessibleName = keys.Count > 1 ? "Tool mods, misc, installed mods and libraries" : "Tool mods, misc and installed mods";
            Arrange();
            if (selectionLost) Raise();
        }
        internal void SetInstalled(bool on)
        {
            var selected = Kinds.Selected;
            if (on) selected.Add(InstalledKind); else selected.Remove(InstalledKind);
            Kinds.SetSelected(selected, true);
        }
        // Selected Misc chips (their Tag is the kind label); empty = every Misc kind.
        internal HashSet<string> MiscSelected { get { return new HashSet<string>(MiscChips.Controls.OfType<FlatButton>().Where(b => b.Active).Select(b => (string)b.Tag), StringComparer.Ordinal); } }

        internal void ClearSelections()
        {
            Grid.SetSelected(new string[0], false); Kinds.SetSelected(new string[0], false);
            foreach (FlatButton chip in MiscChips.Controls.OfType<FlatButton>()) chip.SetActive(false);
            MiscChips.Visible = false; Arrange();
        }
        internal void SetKinds(IEnumerable<string> kinds)
        {
            Kinds.SetSelected(kinds, false); MiscChips.Visible = MiscOn; Arrange();
        }
        // 1.0.8: scrolls the panel so the open filter editor is in view (its bottom first, then its top).
        internal void ShowEditor()
        {
            if (!Editor.Visible) return;
            // Content coordinates: the client position plus the scrolled distance.
            int top = -AutoScrollPosition.Y, view = ClientSize.Height;
            int sectionTop = body.Top + CustomChips.Top + top, editorBottom = body.Top + Editor.Bottom + top;
            int y = top;
            if (editorBottom > top + view) y = editorBottom - view;
            if (sectionTop < y) y = sectionTop;
            if (y != top) AutoScrollPosition = new Point(0, Math.Max(0, y));
        }
        internal void RaiseChanged() { Raise(); }
        private void Raise() { var handler = FiltersChanged; if (handler != null) handler(this, EventArgs.Empty); }
    }

    // Placeholder art of the kind tiles, drawn at run time in the app's colours (no asset files). Kept in one
    // place so real images can replace it later.
    internal static class KindArt
    {
        internal static Bitmap Placeholder(string kind)
        {
            int w = FighterPortraits.TileWidth, h = FighterPortraits.TileHeight;
            var b = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                using (Brush back = FighterGrid.VerticalGradient(new Rectangle(0, 0, w, h), Color.FromArgb(70, 74, 86), Color.FromArgb(30, 32, 39))) g.FillRectangle(back, 0, 0, w, h);
                var square = new RectangleF(w * 0.18F, h * 0.16F, w * 0.64F, w * 0.64F);
                Glyphs.FillRounded(g, Color.FromArgb(235, Palette.Accent), square, w * 0.12F);
                Glyphs.DrawRounded(g, Color.FromArgb(120, Palette.Text), w * 0.012F, square, w * 0.12F);
                Color ink = Palette.Background;
                if (kind == FilterPanel.ToolsKind)
                {
                    using (var font = new Font("Consolas", w * 0.16F, FontStyle.Bold, GraphicsUnit.Pixel))
                    using (var brush = new SolidBrush(ink))
                    using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                        g.DrawString("</>", font, brush, square, format);
                }
                else if (kind == "library")
                {
                    // A folder: tab and body.
                    float fx = square.X + square.Width * 0.2F, fy = square.Y + square.Height * 0.3F, fw = square.Width * 0.6F, fh = square.Height * 0.44F;
                    Glyphs.FillRounded(g, ink, new RectangleF(fx, fy - fh * 0.14F, fw * 0.42F, fh * 0.3F), fh * 0.08F);
                    Glyphs.FillRounded(g, ink, new RectangleF(fx, fy, fw, fh), fh * 0.1F);
                }
                else if (kind == FilterPanel.InstalledKind)
                {
                    using (var pen = new Pen(ink, w * 0.07F) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                        g.DrawLines(pen, new[] { new PointF(square.X + square.Width * 0.24F, square.Y + square.Height * 0.52F), new PointF(square.X + square.Width * 0.43F, square.Y + square.Height * 0.71F), new PointF(square.X + square.Width * 0.77F, square.Y + square.Height * 0.31F) });
                }
                else
                {
                    // A 2x2 grid of boxes: "everything else".
                    float cell = square.Width * 0.24F, gap = square.Width * 0.08F, x0 = square.X + (square.Width - 2 * cell - gap) / 2, y0 = square.Y + (square.Height - 2 * cell - gap) / 2;
                    for (int i = 0; i < 4; i++) Glyphs.FillRounded(g, ink, new RectangleF(x0 + (i % 2) * (cell + gap), y0 + (i / 2) * (cell + gap), cell, cell), cell * 0.18F);
                }
            }
            return b;
        }
    }
    // Shared base of the fighter grid and the facet lists: owner-drawn items, vertical auto-scroll, a focus
    // item moved by the keyboard, click/Space toggling and one accessible child per item.
    internal abstract class FacetSurface : Panel
    {
        internal event EventHandler SelectionChanged, HoverChanged;
        protected int focusIndex, hoverIndex = -1, anchorIndex = -1;
        protected FacetSurface()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            DoubleBuffered = true; AutoScroll = true; TabStop = true; BackColor = Palette.Surface;
        }
        internal abstract int ItemCount { get; }
        internal abstract Rectangle ItemBounds(int index);           // content coordinates (unscrolled)
        internal abstract string ItemAccessibleName(int index);
        internal abstract bool IsItemSelected(int index);
        protected abstract void ToggleItem(int index);
        protected abstract void SelectRange(int from, int to);
        protected abstract Size ContentSize();
        protected abstract void PaintItems(Graphics g, Rectangle visible);
        protected virtual bool HitItem(int index, Point content) { return ItemBounds(index).Contains(content); }
        internal int FocusIndex { get { return focusIndex; } }
        internal int HoverIndex { get { return hoverIndex; } }
        protected int Px(int logical) { return DpiMetrics.Px(this, logical); }

        protected void RaiseSelectionChanged()
        {
            Invalidate(); var handler = SelectionChanged; if (handler != null) handler(this, EventArgs.Empty);
            if (IsHandleCreated && focusIndex >= 0 && focusIndex < ItemCount) AccessibilityNotifyClients(AccessibleEvents.StateChange, focusIndex);
        }

        protected void UpdateScrollExtent()
        {
            if (!AutoScroll) return;
            Size size = ContentSize();
            Size wanted = new Size(0, size.Height > ClientSize.Height ? size.Height : 0);
            if (AutoScrollMinSize != wanted) AutoScrollMinSize = wanted;
        }

        internal Point ToContent(Point client) { return new Point(client.X - AutoScrollPosition.X, client.Y - AutoScrollPosition.Y); }
        internal int HitTest(Point client)
        {
            Point p = ToContent(client);
            for (int i = 0; i < ItemCount; i++) if (HitItem(i, p)) return i;
            return -1;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            using (Brush back = new SolidBrush(BackColor)) e.Graphics.FillRectangle(back, ClientRectangle);
            GraphicsState state = e.Graphics.Save();
            e.Graphics.TranslateTransform(AutoScrollPosition.X, AutoScrollPosition.Y);
            Rectangle visible = new Rectangle(-AutoScrollPosition.X, -AutoScrollPosition.Y, ClientSize.Width, ClientSize.Height);
            PaintItems(e.Graphics, visible);
            e.Graphics.Restore(state);
        }

        protected override void OnLayout(LayoutEventArgs e) { base.OnLayout(e); UpdateScrollExtent(); }
        protected override void OnResize(EventArgs e) { base.OnResize(e); UpdateScrollExtent(); Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); if (focusIndex >= 0 && focusIndex < ItemCount) AccessibilityNotifyClients(AccessibleEvents.Focus, focusIndex); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int hit = HitTest(e.Location);
            if (hit != hoverIndex) { hoverIndex = hit; Invalidate(); var hover = HoverChanged; if (hover != null) hover(this, EventArgs.Empty); }
            Cursor = hit >= 0 ? Cursors.Hand : Cursors.Default;
        }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (hoverIndex != -1) { hoverIndex = -1; Invalidate(); } }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (CanFocus) Focus();
            if (e.Button != MouseButtons.Left) return;
            int hit = HitTest(e.Location);
            if (hit < 0) return;
            Activate(hit, (ModifierKeys & Keys.Shift) != 0);
        }
        // Click toggles; Shift+click selects the range from the last clicked item.
        internal void Activate(int index, bool range)
        {
            if (index < 0 || index >= ItemCount) return;
            focusIndex = index;
            if (range && anchorIndex >= 0 && anchorIndex < ItemCount) SelectRange(anchorIndex, index);
            else { ToggleItem(index); anchorIndex = index; }
            RaiseSelectionChanged();
        }
        protected override bool IsInputKey(Keys keyData)
        {
            Keys key = keyData & Keys.KeyCode;
            if (key == Keys.Up || key == Keys.Down || key == Keys.Left || key == Keys.Right || key == Keys.Home || key == Keys.End || key == Keys.PageUp || key == Keys.PageDown || key == Keys.Space || key == Keys.Enter) return true;
            return base.IsInputKey(keyData);
        }
        protected virtual int Columns { get { return 1; } }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            int count = ItemCount; if (count == 0) return;
            int columns = Math.Max(1, Columns), next = Math.Max(0, Math.Min(count - 1, focusIndex));
            int page = Math.Max(1, ClientSize.Height / Math.Max(1, ItemBounds(0).Height)) * columns;
            Keys key = WasdArrow(e);
            if (key != e.KeyCode) e.SuppressKeyPress = true;
            switch (key)
            {
                case Keys.Left: next = Math.Max(0, next - 1); break;
                case Keys.Right: next = Math.Min(count - 1, next + 1); break;
                case Keys.Up: next = next - columns >= 0 ? next - columns : next; break;
                case Keys.Down: next = next + columns < count ? next + columns : next; break;
                case Keys.Home: next = 0; break;
                case Keys.End: next = count - 1; break;
                case Keys.PageUp: next = Math.Max(0, next - page); break;
                case Keys.PageDown: next = Math.Min(count - 1, next + page); break;
                case Keys.Space: case Keys.Enter: Activate(next, e.Shift); e.Handled = e.SuppressKeyPress = true; return;
                default: return;
            }
            e.Handled = true;
            if (next != focusIndex) { focusIndex = next; EnsureVisible(next); Invalidate(); if (Focused) AccessibilityNotifyClients(AccessibleEvents.Focus, next); }
        }
        // 2.3.1: W/A/S/D move like Up/Left/Down/Right (no modifier; only while a grid, tile or chip has the focus,
        // never in the search box or any text field).
        internal static Keys WasdArrow(KeyEventArgs e)
        {
            if (e.Modifiers != Keys.None) return e.KeyCode;
            switch (e.KeyCode) { case Keys.W: return Keys.Up; case Keys.A: return Keys.Left; case Keys.S: return Keys.Down; case Keys.D: return Keys.Right; default: return e.KeyCode; }
        }
        internal void EnsureVisible(int index)
        {
            if (index < 0 || index >= ItemCount) return;
            Rectangle r = ItemBounds(index); int top = -AutoScrollPosition.Y, pad = Px(4);
            if (!AutoScroll)
            {
                ScrollableControl scroller = Parent as ScrollableControl; while (scroller != null && !scroller.AutoScroll) scroller = scroller.Parent as ScrollableControl;
                if (scroller == null) return;
                Point at = scroller.PointToClient(PointToScreen(r.Location));
                int y = -scroller.AutoScrollPosition.Y;
                if (at.Y - pad < 0) scroller.AutoScrollPosition = new Point(0, Math.Max(0, y + at.Y - pad));
                else if (at.Y + r.Height + pad > scroller.ClientSize.Height) scroller.AutoScrollPosition = new Point(0, y + at.Y + r.Height + pad - scroller.ClientSize.Height);
                return;
            }
            if (r.Top - pad < top) AutoScrollPosition = new Point(0, Math.Max(0, r.Top - pad));
            else if (r.Bottom + pad > top + ClientSize.Height) AutoScrollPosition = new Point(0, r.Bottom + pad - ClientSize.Height);
        }
        protected void DrawFocusRing(Graphics g, GraphicsPath path)
        {
            using (var pen = new Pen(Palette.Text, Math.Max(1.5F, Px(2))) { DashStyle = DashStyle.Dot }) g.DrawPath(pen, path);
        }
        protected bool ShowItemFocus(int index) { return Focused && index == focusIndex; }

        protected override AccessibleObject CreateAccessibilityInstance() { return new SurfaceAccessible(this); }
        private sealed class SurfaceAccessible : ControlAccessibleObject
        {
            private readonly FacetSurface owner;
            internal SurfaceAccessible(FacetSurface owner) : base(owner) { this.owner = owner; }
            public override AccessibleRole Role { get { return AccessibleRole.List; } }
            public override int GetChildCount() { return owner.ItemCount; }
            public override AccessibleObject GetChild(int index) { return index >= 0 && index < owner.ItemCount ? new ItemAccessible(owner, index, this) : null; }
            public override AccessibleObject GetFocused() { return owner.Focused && owner.focusIndex >= 0 && owner.focusIndex < owner.ItemCount ? GetChild(owner.focusIndex) : base.GetFocused(); }
            public override AccessibleObject HitTest(int x, int y) { int hit = owner.HitTest(owner.PointToClient(new Point(x, y))); return hit >= 0 ? GetChild(hit) : base.HitTest(x, y); }
        }
        internal sealed class ItemAccessible : AccessibleObject
        {
            private readonly FacetSurface owner; private readonly int index; private readonly AccessibleObject parent;
            internal ItemAccessible(FacetSurface owner, int index, AccessibleObject parent) { this.owner = owner; this.index = index; this.parent = parent; }
            public override string Name { get { return owner.ItemAccessibleName(index); } }
            public override AccessibleRole Role { get { return AccessibleRole.CheckButton; } }
            public override AccessibleObject Parent { get { return parent; } }
            public override string DefaultAction { get { return owner.IsItemSelected(index) ? "Clear" : "Select"; } }
            public override void DoDefaultAction() { owner.Activate(index, false); }
            public override AccessibleStates State
            {
                get
                {
                    AccessibleStates s = AccessibleStates.Focusable | AccessibleStates.Selectable;
                    if (owner.IsItemSelected(index)) s |= AccessibleStates.Checked | AccessibleStates.Selected;
                    if (owner.Focused && owner.focusIndex == index) s |= AccessibleStates.Focused;
                    Rectangle r = owner.ItemBounds(index); r.Offset(owner.AutoScrollPosition);
                    if (!owner.ClientRectangle.IntersectsWith(r)) s |= AccessibleStates.Offscreen;
                    return s;
                }
            }
            public override Rectangle Bounds { get { Rectangle r = owner.ItemBounds(index); r.Offset(owner.AutoScrollPosition); return owner.RectangleToScreen(r); } }
            public override void Select(AccessibleSelection flags) { if ((flags & AccessibleSelection.TakeFocus) != 0) { owner.Focus(); owner.focusIndex = index; owner.Invalidate(); } }
        }
    }

    internal sealed class FighterCard
    {
        internal string Code, Name;
        internal int Count;
        internal bool Selected;
        internal Bitmap Portrait;   // owned by the grid
        internal string PortraitNote = "";
    }

    // The fighter roster: slanted 5:7 cards in code order (the game's release order), grayscale portraits
    // turning to colour on hover and when selected, name and mod count on each card, dimmed without mods.
    internal sealed class FighterGrid : FacetSurface
    {
        internal const int MinCardWidth = 70, MaxCardWidth = 100, Gap = 6, Margin0 = 6;
        internal const double Slant = 0.12;          // horizontal lean per unit of card height
        private readonly List<FighterCard> cards = new List<FighterCard>();
        internal bool KeepOrder;   // kind tiles keep their given order
        private readonly Dictionary<string, Bitmap> colour = new Dictionary<string, Bitmap>(StringComparer.Ordinal), gray = new Dictionary<string, Bitmap>(StringComparer.Ordinal);
        private Size renderedSize;
        private int columns = 1, cardWidth, cardHeight, slant, originX;

        internal IList<FighterCard> Cards { get { return cards; } }
        internal int CardWidth { get { Measure(); return cardWidth; } }
        internal int CardHeight { get { Measure(); return cardHeight; } }
        protected override int Columns { get { Measure(); return columns; } }
        internal int ColumnCount { get { return Columns; } }
        internal override int ItemCount { get { return cards.Count; } }

        internal HashSet<string> Selected { get { return new HashSet<string>(cards.Where(c => c.Selected).Select(c => c.Code), StringComparer.Ordinal); } }

        // Fighters in code order; keeps selection, counts and portraits of fighters already shown.
        internal void SetFighters(IEnumerable<KeyValuePair<string, string>> fighters)
        {
            var old = cards.ToDictionary(c => c.Code, StringComparer.Ordinal);
            var next = new List<FighterCard>();
            var unique = fighters.GroupBy(f => f.Key).Select(g => g.First());
            foreach (var f in KeepOrder ? unique : unique.OrderBy(f => f.Key, StringComparer.Ordinal))
            {
                FighterCard card;
                if (!old.TryGetValue(f.Key, out card)) card = new FighterCard { Code = f.Key };
                card.Name = f.Value; next.Add(card); old.Remove(f.Key);
            }
            foreach (FighterCard gone in old.Values) { if (gone.Portrait != null) gone.Portrait.Dispose(); ForgetRender(gone.Code); }
            cards.Clear(); cards.AddRange(next);
            focusIndex = Math.Max(0, Math.Min(focusIndex, cards.Count - 1));
            UpdateScrollExtent(); Invalidate();
        }

        internal void SetCounts(IDictionary<string, int> counts)
        {
            bool changed = false;
            foreach (FighterCard c in cards) { int n; n = counts != null && counts.TryGetValue(c.Code, out n) ? n : 0; if (c.Count != n) { c.Count = n; changed = true; } }
            if (changed) Invalidate();
        }

        internal void SetSelected(IEnumerable<string> codes, bool raise)
        {
            var set = new HashSet<string>(codes ?? new string[0], StringComparer.Ordinal); bool changed = false;
            foreach (FighterCard c in cards) { bool s = set.Contains(c.Code); if (c.Selected != s) { c.Selected = s; changed = true; } }
            if (changed) { if (raise) RaiseSelectionChanged(); else Invalidate(); }
        }

        // Takes ownership of `portrait` (null = name tile).
        internal bool SetPortrait(string code, Bitmap portrait, string note)
        {
            FighterCard card = cards.FirstOrDefault(c => c.Code == code);
            if (card == null) { if (portrait != null) portrait.Dispose(); return false; }
            if (card.Portrait != null && !Object.ReferenceEquals(card.Portrait, portrait)) card.Portrait.Dispose();
            card.Portrait = portrait; card.PortraitNote = note ?? ""; ForgetRender(code);
            int i = cards.IndexOf(card);
            if (IsHandleCreated) { Rectangle r = ItemBounds(i); r.Offset(AutoScrollPosition); r.Inflate(Px(2), Px(2)); Invalidate(r); }
            return true;
        }
        internal int PortraitCount { get { return cards.Count(c => c.Portrait != null); } }

        protected override void ToggleItem(int index) { cards[index].Selected = !cards[index].Selected; }
        protected override void SelectRange(int from, int to)
        {
            int a = Math.Min(from, to), b = Math.Max(from, to);
            for (int i = a; i <= b; i++) cards[i].Selected = true;
        }
        internal override bool IsItemSelected(int index) { return cards[index].Selected; }
        internal readonly Dictionary<string, string> AccessibleNames = new Dictionary<string, string>(StringComparer.Ordinal);
        internal override string ItemAccessibleName(int index)
        {
            FighterCard c = cards[index]; string name;
            return (AccessibleNames.TryGetValue(c.Code, out name) ? name : c.Name) + ", " + (c.Count == 1 ? "1 mod" : c.Count + " mods") + (c.Selected ? ", selected" : "");
        }

        private void Measure() { Measure(ClientSize.Width); }
        // Height of all cards for a control width (the panel sizes the grid with it).
        internal int HeightFor(int width)
        {
            Measure(width);
            int rows = RowCount();
            return 2 * Px(Margin0) + rows * cardHeight + Math.Max(0, rows - 1) * Px(Gap);
        }
        // Cards from GroupBreak on (the library tiles) start after a small gap and wrap with the row.
        internal int GroupBreak = -1;
        internal const int GroupGap = 14;
        private readonly List<Point> slots = new List<Point>();   // x offset from originX, row
        private int RowCount() { return GroupBreak < 0 ? (cards.Count + columns - 1) / Math.Max(1, columns) : (slots.Count == 0 ? 0 : slots.Max(s => s.Y) + 1); }
        private void Measure(int width)
        {
            int avail = Math.Max(1, width - 2 * Px(Margin0));
            int gap = Px(Gap), min = Px(MinCardWidth), max = Px(MaxCardWidth);
            int cols = 1;
            for (int c = 1; c <= 16; c++)
            {
                double each = (avail - (c - 1) * gap) / (c + Slant * 1.4);
                if (each >= min) cols = c; else break;
            }
            double w = Math.Min(max, (avail - (cols - 1) * gap) / (cols + Slant * 1.4));
            columns = cols; cardWidth = Math.Max(1, (int)w); cardHeight = (int)Math.Round(cardWidth * 7.0 / 5.0); slant = (int)Math.Round(cardHeight * Slant);
            int total = cols * cardWidth + (cols - 1) * gap + slant;
            originX = Px(Margin0) + Math.Max(0, (avail - total) / 2);
            slots.Clear();
            if (GroupBreak >= 0)
            {
                int x = 0, row = 0, limit = Math.Max(total, cardWidth + slant);
                for (int i = 0; i < cards.Count; i++)
                {
                    if (i == GroupBreak && i > 0) x += Px(GroupGap);
                    if (i > 0 && x + cardWidth + slant > limit) { row++; x = 0; }
                    slots.Add(new Point(x, row)); x += cardWidth + gap;
                }
            }
            Size size = new Size(cardWidth + slant, cardHeight);
            if (size != renderedSize) { ClearRenders(); renderedSize = size; }
        }

        internal override Rectangle ItemBounds(int index)
        {
            Measure();
            int gap = Px(Gap);
            if (GroupBreak >= 0 && index < slots.Count) return new Rectangle(originX + slots[index].X, Px(Margin0) + slots[index].Y * (cardHeight + gap), cardWidth + slant, cardHeight);
            int row = index / columns, col = index % columns;
            return new Rectangle(originX + col * (cardWidth + gap), Px(Margin0) + row * (cardHeight + gap), cardWidth + slant, cardHeight);
        }
        // The name band's width, and its font: the normal small font, or the tiny one when the name would be cut
        // ("Fluffy - Copy"); longer names still end in an ellipsis.
        internal int NameWidth() { Measure(); return cardWidth - Px(4); }
        internal Font NameFont(string name)
        {
            const TextFormatFlags flags = TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
            return TextRenderer.MeasureText(name, Palette.Small, Size.Empty, flags).Width <= NameWidth() ? Palette.Small : Palette.Tiny;
        }
        internal bool NameFits(string name) { return TextRenderer.MeasureText(name, NameFont(name), Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix).Width <= NameWidth(); }
        // The slanted card: top edge shifted right by the slant.
        internal GraphicsPath CardPath(Rectangle box)
        {
            var path = new GraphicsPath();
            path.AddPolygon(new[] { new Point(box.Left + slant, box.Top), new Point(box.Right, box.Top), new Point(box.Right - slant, box.Bottom), new Point(box.Left, box.Bottom) });
            return path;
        }
        protected override bool HitItem(int index, Point content)
        {
            Rectangle box = ItemBounds(index);
            if (!box.Contains(content)) return false;
            using (GraphicsPath path = CardPath(box)) return path.IsVisible(content);
        }
        protected override Size ContentSize()
        {
            Measure();
            int rows = RowCount();
            return new Size(ClientSize.Width, 2 * Px(Margin0) + rows * cardHeight + Math.Max(0, rows - 1) * Px(Gap));
        }

        private void ForgetRender(string code)
        {
            Bitmap b;
            if (colour.TryGetValue(code, out b)) { b.Dispose(); colour.Remove(code); }
            if (gray.TryGetValue(code, out b)) { b.Dispose(); gray.Remove(code); }
        }
        private void ClearRenders()
        {
            foreach (Bitmap b in colour.Values.Concat(gray.Values)) b.Dispose();
            colour.Clear(); gray.Clear();
        }
        internal bool IsRendered(FighterCard card, bool inColour) { Measure(); return (inColour ? colour : gray).ContainsKey(card.Code); }   // 1.0.1: warm-up (MainFormCharacterPicker.cs)
        // The portrait scaled to the card box (cover, top-aligned), in colour or grayscale (ColorMatrix).
        internal Bitmap Rendered(FighterCard card, bool inColour)
        {
            if (card.Portrait == null) return null;
            Measure();
            var cache = inColour ? colour : gray; Bitmap b;
            if (cache.TryGetValue(card.Code, out b)) return b;
            int w = Math.Max(1, renderedSize.Width), h = Math.Max(1, renderedSize.Height);
            b = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(b))
            using (var attributes = new ImageAttributes())
            {
                g.Clear(Color.Transparent);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.PixelOffsetMode = PixelOffsetMode.HighQuality; g.CompositingQuality = CompositingQuality.HighQuality;
                if (!inColour)
                    attributes.SetColorMatrix(new ColorMatrix(new[] {
                        new[] { 0.299F, 0.299F, 0.299F, 0, 0 }, new[] { 0.587F, 0.587F, 0.587F, 0, 0 }, new[] { 0.114F, 0.114F, 0.114F, 0, 0 },
                        new[] { 0F, 0, 0, 1, 0 }, new[] { 0F, 0, 0, 0, 1 } }));
                attributes.SetWrapMode(WrapMode.TileFlipXY);
                int drawHeight = (int)Math.Round(w * (double)card.Portrait.Height / card.Portrait.Width);
                Rectangle target = drawHeight >= h ? new Rectangle(0, 0, w, drawHeight) : new Rectangle((w - (int)Math.Round(h * (double)card.Portrait.Width / card.Portrait.Height)) / 2, 0, (int)Math.Round(h * (double)card.Portrait.Width / card.Portrait.Height), h);
                g.DrawImage(card.Portrait, target, 0, 0, card.Portrait.Width, card.Portrait.Height, GraphicsUnit.Pixel, attributes);
            }
            cache[card.Code] = b;
            return b;
        }

        protected override void PaintItems(Graphics g, Rectangle visible)
        {
            Measure();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            for (int i = 0; i < cards.Count; i++)
            {
                Rectangle box = ItemBounds(i);
                if (!box.IntersectsWith(Rectangle.Inflate(visible, Px(4), Px(4)))) continue;
                PaintCard(g, cards[i], box, i == hoverIndex, ShowItemFocus(i));
            }
        }

        // 2.3.1: a LinearGradientBrush over exactly the filled rectangle wraps at its first row and paints it in the end
        // colour: the name band drew a thin dark line across every portrait at its top edge (about 72 % down the card).
        // The brush now spans one pixel more on each side and flips instead of wrapping.
        internal static LinearGradientBrush VerticalGradient(Rectangle fill, Color top, Color bottom)
        {
            var span = new Rectangle(fill.X, fill.Y - 1, Math.Max(1, fill.Width), fill.Height + 2);
            return new LinearGradientBrush(span, top, bottom, 90F) { WrapMode = WrapMode.TileFlipXY };
        }

        private void PaintCard(Graphics g, FighterCard card, Rectangle box, bool hover, bool focus)
        {
            bool dim = card.Count == 0;
            using (GraphicsPath path = CardPath(box))
            {
                GraphicsState state = g.Save();
                g.SetClip(path, CombineMode.Intersect);
                using (Brush back = VerticalGradient(box, Color.FromArgb(58, 61, 72), Color.FromArgb(26, 28, 34))) g.FillRectangle(back, box);
                Bitmap image = Rendered(card, card.Selected || hover);
                if (image != null) g.DrawImage(image, box.Location);
                else
                {
                    // Name tile: until (or without) a portrait from the game.
                    using (var font = new Font("Segoe UI", 11F, FontStyle.Bold))
                        TextRenderer.DrawText(g, card.Name, font, new Rectangle(box.Left + slant / 2 + Px(2), box.Top + Px(18), box.Width - slant - Px(4), box.Height - Px(22)), dim ? Palette.Muted : card.Selected ? Palette.Accent : Palette.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
                }
                if (dim) using (Brush veil = new SolidBrush(Color.FromArgb(150, Palette.Background))) g.FillRectangle(veil, box);
                // Name band at the bottom.
                int band = Px(22);
                Rectangle bandRect = new Rectangle(box.Left, box.Bottom - band - Px(6), box.Width, band + Px(6));
                using (Brush shade = VerticalGradient(bandRect, Color.FromArgb(0, 0, 0, 0), Color.FromArgb(215, 8, 9, 12))) g.FillRectangle(shade, bandRect);
                Color nameColor = dim ? Palette.Muted : card.Selected ? Palette.Accent : Palette.Text;
                if (image != null) TextRenderer.DrawText(g, card.Name, NameFont(card.Name), new Rectangle(box.Left + Px(3), box.Bottom - band, NameWidth(), band), nameColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                // Mod count, top right.
                string count = card.Count.ToString();
                Size textSize = TextRenderer.MeasureText(g, count, Palette.Tiny, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
                Rectangle pill = new Rectangle(box.Right - textSize.Width - Px(12), box.Top + Px(4), textSize.Width + Px(8), Px(16));
                Glyphs.FillRounded(g, dim ? Color.FromArgb(170, Palette.Raised) : card.Selected ? Palette.Accent : Color.FromArgb(215, Palette.Background), pill, Px(7));
                TextRenderer.DrawText(g, count, Palette.Tiny, pill, dim ? Palette.Muted : card.Selected ? Palette.Background : Palette.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
                g.Restore(state);
                if (card.Selected) using (var pen = new Pen(Palette.Accent, Math.Max(2F, Px(2)))) g.DrawPath(pen, path);
                else if (hover) using (var pen = new Pen(Color.FromArgb(160, Palette.Text), Math.Max(1F, Px(1)))) g.DrawPath(pen, path);
                else using (var pen = new Pen(Palette.Border, 1F)) g.DrawPath(pen, path);
                if (focus && ShowFocusCues) using (GraphicsPath ring = CardPath(Rectangle.Inflate(box, -Px(3), -Px(3)))) DrawFocusRing(g, ring);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { ClearRenders(); foreach (FighterCard c in cards) if (c.Portrait != null) c.Portrait.Dispose(); cards.Clear(); }
            base.Dispose(disposing);
        }
    }
}
