using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

namespace SlotsInstaller
{
    // UI half of --filter-check: the Filters panel in hidden native windows (Opacity 0, not in the taskbar);
    // sizes larger than the desktop use a canvas host as LayoutChecks does.
    internal static partial class FilterChecks
    {
        private sealed class Window : IDisposable
        {
            internal MainForm Form; internal Control Host; internal string Context;
            public void Dispose() { if (Form != null) { Form.Close(); Form.Dispose(); } }
        }

        private static Window Open(string root, string gameRoot, Size logical, List<LibraryEntry> library) { return Open(root, gameRoot, logical, library, null); }
        private static Window Open(string root, string gameRoot, Size logical, List<LibraryEntry> library, IList<string> libraries)
        {
            var config = new AppConfig { Libraries = libraries == null ? new List<string>() : libraries.ToList(), GameRoot = gameRoot ?? Path.Combine(root, "no-game"), SevenZipPath = "" };
            var w = new Window { Form = new MainForm(config) };
            MainForm form = w.Form;
            F("firstLoad").SetValue(form, false);
            form.ShowInTaskbar = false; form.Opacity = 0; form.MinimumSize = Size.Empty; form.Show();
            form.PortraitCacheDirectory = Path.Combine(root, "ui-cache-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Size size = new Size(DpiMetrics.Px(form, logical.Width), DpiMetrics.Px(form, logical.Height));
            w.Host = form;
            if (size.Width > Screen.PrimaryScreen.WorkingArea.Width || size.Height > Screen.PrimaryScreen.WorkingArea.Height)
            {
                Panel canvas = new Panel { Size = size, BackColor = Palette.Background, ForeColor = Palette.Text };
                Control[] children = form.Controls.Cast<Control>().ToArray();
                foreach (Control child in children) form.Controls.Remove(child);
                canvas.Controls.AddRange(children); form.Controls.Add(canvas); canvas.CreateControl();
                w.Host = canvas;
            }
            else form.ClientSize = size;
            w.Context = logical.Width + "x" + logical.Height + " logical @ " + form.DeviceDpi + " DPI" + (w.Host == form ? "" : " (canvas)");
            if (library != null)
            {
                F("library").SetValue(form, library);
                Apply(form);
            }
            Settle(w.Host);
            return w;
        }

        private static FieldInfo F(string name)
        {
            FieldInfo field = typeof(MainForm).GetField(name, PrivateInstance | BindingFlags.Static | BindingFlags.NonPublic);
            if (field == null) throw new InvalidOperationException("no field " + name);
            return field;
        }
        private static T Get<T>(MainForm form, string name) where T : class { T value = F(name).GetValue(form) as T; if (value == null) throw new InvalidOperationException("field " + name + " is empty"); return value; }
        private static void Apply(MainForm form) { typeof(MainForm).GetMethod("ApplyFilters", PrivateInstance).Invoke(form, null); }
        private static void Settle(Control host) { Layout(host); Application.DoEvents(); Layout(host); }
        private static void Layout(Control root) { root.PerformLayout(); foreach (Control c in root.Controls) Layout(c); }
        private static Rectangle In(Control c, Control host)
        {
            Point at = Point.Empty; Control cur = c;
            while (cur != null && cur != host) { at.Offset(cur.Left, cur.Top); cur = cur.Parent; }
            if (cur != host) throw new InvalidOperationException("detached control " + c.Name);
            return new Rectangle(at, c.Size);
        }
        private static List<string> Listed(MainForm form) { return Get<ListView>(form, "modList").Items.Cast<ListViewItem>().Select(i => ((LibraryEntry)i.Tag).Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList(); }
        private static string Names(MainForm form) { return String.Join(", ", Listed(form)); }
        private static void Pump(Func<bool> done, int seconds, string what)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(seconds);
            while (!done()) { Require(DateTime.UtcNow < deadline, what + " did not finish"); Application.DoEvents(); Thread.Sleep(10); }
            Application.DoEvents();
        }
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
        private static IntPtr Point32(Point p) { return (IntPtr)((p.Y << 16) | (p.X & 0xFFFF)); }

        // 2.3.1: a mod the user picks in the list (mouse, arrow keys, Enter) closes the Filters panel and shows its
        // options; programmatic selections (filter, search, refresh) leave it open; reopening keeps the selections.
        private static void CheckPickClosesPanel(string root, Fixture game)
        {
            using (Window w = Open(root, null, new Size(1460, 920), SearchFixture()))
            {
                MainForm form = w.Form; FilterPanel panel = Get<FilterPanel>(form, "filterPanel");
                ListView list = Get<ListView>(form, "modList");
                Action idle = delegate { Pump(() => !form.Working, 30, "loading"); Settle(w.Host); };
                // Posted input is handled asynchronously (and the fixture mods fail to open, which is fine here):
                // wait for the expected state, or a short settle time for "nothing happens".
                Func<Func<bool>, bool> until = delegate(Func<bool> done)
                {
                    DateTime deadline = DateTime.UtcNow.AddSeconds(5);
                    while (!done() && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(10); }
                    idle(); return done();
                };
                Action settleInput = delegate { DateTime end = DateTime.UtcNow.AddMilliseconds(300); while (DateTime.UtcNow < end) { Application.DoEvents(); Thread.Sleep(10); } idle(); };
                form.ShowFilters(true, false); Settle(w.Host);
                // Programmatic: a filter change, a search change and a selection made in code keep the panel open.
                Toggle(panel.Grid, "009"); Settle(w.Host);
                Require(form.FiltersOpen && Names(form) == "Cammy Barefoot, Cammy Boots, Cammy Scripted", "a fighter click closed the panel or did not filter");
                var searchBox = Get<PaddedTextField>(form, "search"); searchBox.Text = "boots";
                typeof(MainForm).GetMethod("FlushLibraryFilter", PrivateInstance).Invoke(form, null); Settle(w.Host);
                Require(form.FiltersOpen && Names(form) == "Cammy Boots", "a search change closed the panel: " + Names(form));
                searchBox.Text = ""; typeof(MainForm).GetMethod("FlushLibraryFilter", PrivateInstance).Invoke(form, null); Settle(w.Host);
                list.Items[1].Selected = true; idle();
                Require(form.FiltersOpen, "a programmatic selection closed the panel");
                foreach (ListViewItem item in list.SelectedItems.Cast<ListViewItem>().ToList()) item.Selected = false; idle();
                // Mouse click on a row (real Windows messages to the list).
                Rectangle row = list.GetItemRect(0);
                Point at = new Point(row.Left + row.Width / 3, row.Top + row.Height / 2);
                PostMessage(list.Handle, 0x0201, (IntPtr)1, Point32(at)); PostMessage(list.Handle, 0x0202, IntPtr.Zero, Point32(at));
                Require(until(() => !form.FiltersOpen && Get<OptionTree>(form, "componentTree").Visible && list.Items[0].Selected), "a click on a mod did not close the panel (open " + form.FiltersOpen + ", row 0 selected " + list.Items[0].Selected + ")");
                Require(panel.Grid.Selected.SetEquals(new[] { "009" }), "closing lost the fighter selection");
                // Reopening keeps the selections; arrow-key navigation in the list closes it again.
                form.ShowFilters(true, false); Settle(w.Host);
                Require(form.FiltersOpen && panel.Grid.Selected.SetEquals(new[] { "009" }) && Names(form) == "Cammy Barefoot, Cammy Boots, Cammy Scripted", "reopening lost the selections");
                list.Focus(); Application.DoEvents();
                int before = list.SelectedIndices[0];
                PostMessage(list.Handle, 0x0100, (IntPtr)0x28, IntPtr.Zero); PostMessage(list.Handle, 0x0101, (IntPtr)0x28, IntPtr.Zero);   // Down
                Require(until(() => !form.FiltersOpen && list.SelectedIndices.Count > 0 && list.SelectedIndices[0] == before + 1), "Down in the list did not move or close the panel");
                // 2.3.1: W/S in the list move like Up/Down and close the panel too; A/D (like Left/Right) do nothing.
                Func<int> current = delegate { return list.SelectedIndices.Count == 0 ? -1 : list.SelectedIndices[0]; };
                Action<char> press = delegate(char ch)
                {
                    list.Focus(); Application.DoEvents();
                    PostMessage(list.Handle, 0x0100, (IntPtr)Char.ToUpperInvariant(ch), IntPtr.Zero); PostMessage(list.Handle, 0x0101, (IntPtr)Char.ToUpperInvariant(ch), IntPtr.Zero);
                };
                form.ShowFilters(true, false); Settle(w.Host);
                press('w'); Require(until(() => current() == before && !form.FiltersOpen), "W did not move up / close the panel: " + current() + " open " + form.FiltersOpen);
                form.ShowFilters(true, false); Settle(w.Host);
                press('s'); Require(until(() => current() == before + 1 && !form.FiltersOpen), "S did not move down / close the panel: " + current() + " open " + form.FiltersOpen);
                form.ShowFilters(true, false); Settle(w.Host);
                press('a'); settleInput(); press('d'); settleInput();
                Require(current() == before + 1 && form.FiltersOpen, "A/D changed the selection or closed the panel: " + current() + " open " + form.FiltersOpen);
                press('s'); Require(until(() => current() == before + 2), "S to the last row: " + current());
                press('s'); settleInput(); Require(current() == before + 2, "S past the last row: " + current());
                // Other letters keep the list's own type-ahead (jump to the first mod starting with them).
                press('c'); settleInput(); int jumped = current();
                Require(jumped >= 0 && ((LibraryEntry)list.Items[jumped].Tag).Name.StartsWith("C", StringComparison.OrdinalIgnoreCase), "type-ahead: " + jumped);
                // Enter on the selected mod closes it too.
                form.ShowFilters(true, false); Settle(w.Host); list.Focus(); Application.DoEvents();
                PostMessage(list.Handle, 0x0100, (IntPtr)0x0D, IntPtr.Zero); PostMessage(list.Handle, 0x0101, (IntPtr)0x0D, IntPtr.Zero);
                Require(until(() => !form.FiltersOpen), "Enter in the list did not close the panel");
                // Enter in the search box opens the top match and closes the panel.
                form.ShowFilters(true, false); Settle(w.Host);
                form.OpenTopLibraryMatch(); idle();
                Require(!form.FiltersOpen, "Enter in the search (top match) did not close the panel");
                // A filter applied from the reopened panel still leaves it open.
                form.ShowFilters(true, false); Settle(w.Host);
                Toggle(panel.Grid, "009"); Settle(w.Host);
                Require(form.FiltersOpen && Listed(form).Count == 10, "clearing the fighter closed the panel");
            }
        }
        private static void Key(Control c, Keys keys) { typeof(Control).GetMethod("OnKeyDown", PrivateInstance).Invoke(c, new object[] { new KeyEventArgs(keys) }); }

        private static IEnumerable<Control> AllIn(Control root) { foreach (Control c in root.Controls) { yield return c; foreach (Control d in AllIn(c)) yield return d; } }
        private static void Toggle(FighterGrid grid, string code) { grid.Activate(grid.Cards.ToList().FindIndex(c => c.Code == code), false); }
        private static FlatButton Chip(FilterPanel panel, string kind) { return panel.MiscChips.Controls.OfType<FlatButton>().Single(b => (string)b.Tag == kind); }

        private static string CheckLayout(string root, Fixture game, Size logical)
        {
            using (Window w = Open(root, null, logical, TwoLibraries(), TwoLibraryPaths))
            {
                MainForm form = w.Form; Control host = w.Host; string ctx = w.Context;
                FilterPanel panel = Get<FilterPanel>(form, "filterPanel");
                ListView list = Get<ListView>(form, "modList");
                OptionTree tree = Get<OptionTree>(form, "componentTree");
                Require(!panel.Visible && tree.Visible, ctx + ": the panel must start closed");
                form.ShowFilters(true, false); Settle(host);
                Rectangle p = In(panel, host), l = In(list, host);
                Require(panel.Visible && list.Visible && !tree.Visible && host.ClientRectangle.Contains(p) && p.Left >= l.Right && l.Width >= DpiMetrics.Px(form, 180), ctx + ": panel " + p + " list " + l);
                foreach (string name in new[] { "preview", "packageTitle", "targetCharacter", "outfitName", "installButton", "previewButton", "libraryUninstallButton", "filterButton", "search" })
                {
                    Control c = Get<Control>(form, name); Rectangle r = In(c, host);
                    Require(c.Visible && host.ClientRectangle.Contains(r) && !r.IntersectsWith(p), ctx + ": " + name + " " + r + " overlaps the panel " + p + " or is hidden/outside");
                }
                Require(!panel.Controls.Cast<Control>().Concat(panel.Body.Controls.Cast<Control>()).Any(c => c is ComboBox || c is CheckBox || c is TableLayoutPanel), ctx + ": a dropdown, checkbox or filter table is still in the panel");
                FighterGrid grid = panel.Grid, kinds = panel.Kinds;
                int cw = grid.CardWidth, ch = grid.CardHeight, cols = grid.ColumnCount;
                Require(cw >= DpiMetrics.Px(form, FighterGrid.MinCardWidth) - 1 && cw <= DpiMetrics.Px(form, FighterGrid.MaxCardWidth) + 1 && Math.Abs(ch - cw * 7.0 / 5.0) <= 1.5 && cols >= 4, ctx + ": cards " + cw + "x" + ch + " in " + cols + " columns");
                Require(grid.Height >= grid.ItemBounds(grid.ItemCount - 1).Bottom && grid.ItemBounds(cols - 1).Right <= grid.ClientSize.Width, ctx + ": the grid must show every card (the panel scrolls, not the grid)");
                Require(kinds.ItemCount == 5 && kinds.Cards[0].Name == "Tool mods" && kinds.Cards[1].Name == "Misc" && kinds.Cards[2].Name == "Installed" && kinds.Cards[3].Name == "filters" && kinds.Cards[4].Name == "other"
                    && kinds.CardWidth == cw && kinds.CardHeight == ch && kinds.Cards.All(c => c.Portrait != null), ctx + ": tiles are not face-sized tiles with placeholder art (" + String.Join(",", kinds.Cards.Select(c => c.Name)) + ")");
                // The library tiles start after a small gap; all tiles stay inside the row (wrapping when needed).
                Rectangle installedTile = kinds.ItemBounds(2), firstLibrary = kinds.ItemBounds(3);
                int step = kinds.ItemBounds(1).Left - kinds.ItemBounds(0).Left;
                Require(firstLibrary.Top > installedTile.Top || firstLibrary.Left - installedTile.Left == step + DpiMetrics.Px(form, FighterGrid.GroupGap), ctx + ": no gap before the library tiles");
                for (int i = 0; i < kinds.ItemCount; i++) Require(kinds.ItemBounds(i).Right <= kinds.ClientSize.Width && kinds.ItemBounds(i).Bottom <= kinds.Height, ctx + ": tile " + i + " outside the row");
                Rectangle g = In(grid, host), k = In(kinds, host), title = In(panel.Title, host);
                Require(g.Bottom <= k.Top && title.Bottom <= g.Top, ctx + ": parts out of order: grid " + g + " tiles " + k + " heading " + title);
                // 2.4.1: the FIGHTERS row is only the heading (no summary text, no Clear); the summary is the grid's description.
                Control fightersRow = panel.Title.Parent;
                Require(fightersRow.Controls.Count == 1 && panel.Title.Text == "FIGHTERS" && !AllIn(panel).OfType<FlatButton>().Any(b => b.Text == "Clear") && !String.IsNullOrEmpty(grid.AccessibleDescription), ctx + ": FIGHTERS row: " + String.Join(", ", fightersRow.Controls.Cast<Control>().Select(c => c.GetType().Name + " " + c.Text)) + " / description " + grid.AccessibleDescription);
                // The only Clear sits left of Filters above the mod list, appears with the first active filter, and moves neither Filters nor the count.
                FlatButton filtersButton = Get<FlatButton>(form, "filterButton"), headerClear = Get<FlatButton>(form, "filterClearButton");
                Label countLabel = Get<Label>(form, "libraryCount");
                Rectangle filtersBefore = In(filtersButton, host), countBefore = In(countLabel, host);
                Require(!headerClear.Visible, ctx + ": Clear shown with no filter");
                Toggle(grid, "009"); Settle(host);
                Rectangle clearRect = In(headerClear, host);
                Require(headerClear.Visible && headerClear.AccessibleName == "Clear filters" && clearRect.Right <= In(filtersButton, host).Left && In(filtersButton, host) == filtersBefore && In(countLabel, host) == countBefore && clearRect.Width >= TextRenderer.MeasureText("Clear", headerClear.Font).Width && clearRect.Left >= countBefore.Right, ctx + ": header Clear " + clearRect + " Filters " + In(filtersButton, host) + " (was " + filtersBefore + ") count " + In(countLabel, host) + " (was " + countBefore + ")");
                if (logical.Width == 1460)
                    using (var bmp = new Bitmap(host.Width, host.Height)) { host.DrawToBitmap(bmp, new Rectangle(0, 0, host.Width, host.Height)); bmp.Save(Path.Combine(root, "list-header-clear-" + logical.Width + "x" + logical.Height + ".png"), ImageFormat.Png); }
                Toggle(grid, "009"); Settle(host);
                Require(!headerClear.Visible, ctx + ": Clear still shown after the last filter was removed");
                // Misc chips appear under the tiles when Misc is selected, and wrap inside the panel.
                Require(!panel.MiscChips.Visible, ctx + ": chips shown without Misc");
                Toggle(kinds, FilterPanel.MiscKind); Settle(host);
                Rectangle chips = In(panel.MiscChips, host);
                Require(panel.MiscChips.Visible && panel.MiscChips.Controls.Count >= 3 && chips.Top >= In(kinds, host).Bottom && chips.Bottom <= p.Bottom + (panel.VerticalScroll.Visible ? panel.Body.Height : 0), ctx + ": chips " + chips);
                foreach (Control chip in panel.MiscChips.Controls) { Rectangle r = In(chip, host); Require(r.Left >= chips.Left && r.Right <= chips.Right && r.Bottom <= chips.Bottom && r.Width >= TextRenderer.MeasureText(chip.Text, chip.Font).Width, ctx + ": chip " + chip.Text + " clipped " + r); }
                // 2.3.1/2.4.0: no hover tooltip anywhere (ui-polish audits the whole app); accessible names stay.
                Require(UiAudit.ToolTipFindings(form).Count == 0, ctx + ": tooltips: " + String.Join("; ", UiAudit.ToolTipFindings(form)));
                Require(kinds.AccessibilityObject.GetChild(2).Name.StartsWith("Installed mods only") && kinds.AccessibilityObject.GetChild(4).Name.StartsWith(@"Library ") && panel.MiscChips.Controls.Cast<Control>().All(c => c.AccessibleName.StartsWith("Misc kind ")), ctx + ": accessible names");
                int content = panel.Body.Height + panel.Padding.Vertical;
                bool scrolls = panel.VerticalScroll.Visible;
                Require(content <= panel.ClientSize.Height || scrolls, ctx + ": content " + content + " taller than the panel " + panel.ClientSize.Height + " without scrolling");
                // 1.0.15: the library starts at 330, where the grid has 7 per row and the panel may scroll (accepted by the
                // user); with the library at the old 250 it still fits on one screen.
                if (logical.Width == 1460 && scrolls)
                {
                    SplitContainer split = Get<SplitContainer>(form, "librarySplit"); int was = split.SplitterDistance;
                    split.SplitterDistance = DpiMetrics.Px(form, MainForm.OldDefaultLibraryWidth); Settle(host);
                    int narrow = panel.Body.Height + panel.Padding.Vertical; bool still = panel.VerticalScroll.Visible;
                    split.SplitterDistance = was; Settle(host);
                    Require(!still && narrow <= panel.ClientSize.Height, ctx + ": the panel scrolls at 1460x920 with the Misc chips showing and a 250 library (content " + narrow + ", panel " + panel.ClientSize.Height + ")");
                }
                if (scrolls)
                {
                    panel.ScrollControlIntoView(panel.MiscChips); Settle(host);
                    Rectangle last = In(panel.MiscChips, host);
                    Require(last.Bottom <= p.Bottom && last.Top >= p.Top, ctx + ": the Misc chips cannot be scrolled into view " + last);
                }
                panel.AutoScrollPosition = Point.Empty; Toggle(kinds, FilterPanel.MiscKind); Settle(host);
                string summary = ctx + ": panel " + p.Width + "x" + p.Height + ", list " + l.Width + " px, " + cols + " columns of " + cw + "x" + ch + " px cards, content with Misc chips " + content + " px" + (scrolls ? " (panel scrolls)" : " (fits, no scrolling)");
                if (logical.Width == 1460)
                {
                    using (var bmp = new Bitmap(host.Width, host.Height)) { host.DrawToBitmap(bmp, new Rectangle(0, 0, host.Width, host.Height)); bmp.Save(Path.Combine(root, "panel-" + logical.Width + "x" + logical.Height + ".png"), ImageFormat.Png); }
                }
                FlatButton button = Get<FlatButton>(form, "filterButton");
                button.PerformClick(); Settle(host);
                Require(!panel.Visible && tree.Visible && list.Visible && !button.Active, ctx + ": Filters did not close back to the options");
                button.PerformClick(); Settle(host);
                Require(panel.Visible && button.Active, ctx + ": Filters did not reopen");
                return summary;
            }
        }

        private static void CheckCombinations(string root, Fixture game)
        {
            using (Window w = Open(root, null, new Size(1232, 640), SearchFixture()))
            {
                MainForm form = w.Form; FilterPanel panel = Get<FilterPanel>(form, "filterPanel");
                form.ShowFilters(true, false);
                PaddedTextField search = Get<PaddedTextField>(form, "search");
                FlatButton button = Get<FlatButton>(form, "filterButton");
                Func<string, int> count = delegate(string code) { return panel.Grid.Cards.Single(c => c.Code == code).Count; };
                Func<string, int> kindCount = delegate(string code) { return panel.Kinds.Cards.Single(c => c.Code == code).Count; };
                Require(Listed(form).Count == 10 && form.ActiveFilterCount() == 0 && button.Text == "Filters" && !Get<FlatButton>(form, "filterClearButton").Visible, "nothing selected lists every mod");
                Require(panel.Kinds.ItemCount == 3, "library tiles shown with no library configured");
                Require(count("009") == 3 && count("016") == 1 && count("001") == 0 && panel.Grid.Cards.Count == Characters.ByCode.Count && kindCount("tools") == 3 && kindCount("misc") == 4 && kindCount("installed") == 0, "counts 009=" + count("009") + " tools=" + kindCount("tools") + " misc=" + kindCount("misc"));
                panel.Grid.SetSelected(new[] { "009" }, true);
                Require(Names(form) == "Cammy Barefoot, Cammy Boots, Cammy Scripted" && button.Text == "Filters 1" && kindCount("tools") == 1 && kindCount("misc") == 0, "Cammy: " + Names(form));
                panel.Grid.SetSelected(new[] { "009", "016" }, true);
                Require(Names(form) == "Cammy Barefoot, Cammy Boots, Cammy Scripted, Juri Jacket", "Cammy + Juri: " + Names(form));
                search.Text = "c1"; Apply(form);
                Require(Names(form) == "Cammy Barefoot, Cammy Scripted, Juri Jacket", "c1 (search) with Cammy + Juri: " + Names(form));
                search.Clear(); Apply(form);
                // Kind tiles narrow the fighters.
                Toggle(panel.Kinds, "tools");
                Require(Names(form) == "Cammy Scripted" && count("009") == 1 && count("016") == 0 && button.Text == "Filters 2", "Cammy/Juri + Code tools: " + Names(form));
                panel.Grid.SetSelected(new string[0], true);
                Require(Names(form) == "Cammy Scripted, Frame Meter, Hitbox Viewer" && button.Text == "Filters 1", "Code tools: " + Names(form));
                Toggle(panel.Kinds, "tools"); Toggle(panel.Kinds, "misc");
                Require(Names(form) == "Lighting Overhaul, Louder Impact, Ranger Hut Night, Select Icons" && panel.MiscChips.Visible && Chip(panel, "Stage").Text == "Stage 2" && Chip(panel, "UI").Text == "UI 1", "Misc: " + Names(form));
                Chip(panel, "Stage").PerformClick();
                Require(Names(form) == "Lighting Overhaul, Ranger Hut Night", "Misc Stage: " + Names(form));
                Chip(panel, "UI").PerformClick();
                Require(Names(form) == "Lighting Overhaul, Ranger Hut Night, Select Icons", "Misc Stage + UI: " + Names(form));
                Toggle(panel.Kinds, "tools");
                Require(Names(form) == "Cammy Scripted, Frame Meter, Hitbox Viewer, Lighting Overhaul, Ranger Hut Night, Select Icons", "Code tools + Misc kinds: " + Names(form));
                search.Text = "is:unreadable"; Apply(form);
                Require(Listed(form).Count == 0 && button.Text == "Filters 1", "tiles + is:unreadable must intersect");
                search.Clear(); Apply(form);
                Get<FlatButton>(form, "filterClearButton").PerformClick();
                Require(Listed(form).Count == 10 && form.ActiveFilterCount() == 0 && panel.Kinds.Selected.Count == 0 && panel.MiscSelected.Count == 0 && !panel.MiscChips.Visible, "Clear did not reset every filter");
                // 2.4.1: the header Clear keeps the search text (not a filter); the list and the count follow.
                FlatButton headerClear = Get<FlatButton>(form, "filterClearButton");
                search.Text = "cammy"; Apply(form); int searched = Listed(form).Count;
                Toggle(panel.Kinds, FilterPanel.ToolsKind); Apply(form);
                Require(headerClear.Visible && Listed(form).Count <= searched, "Clear hidden with an active filter");
                headerClear.PerformClick(); Application.DoEvents();
                string expectedCount = searched == 10 ? "10 MODS" : searched + " OF 10 MODS";
                Require(search.Text == "cammy" && Listed(form).Count == searched && Get<Label>(form, "libraryCount").Text == expectedCount && !headerClear.Visible && form.ActiveFilterCount() == 0, "Clear with a search: search '" + search.Text + "', " + Listed(form).Count + " listed (expected " + searched + "), count '" + Get<Label>(form, "libraryCount").Text + "'");
                search.Clear(); Apply(form);
                search.Text = "stage"; Apply(form);
                Require(Names(form) == "Lighting Overhaul, Ranger Hut Night", "'stage' search: " + Names(form));
                search.Text = "is:misc"; Apply(form);
                Require(Names(form) == "Lighting Overhaul, Louder Impact, Ranger Hut Night, Select Icons" && count("009") == 0, "is:misc with counts: " + Names(form));
                search.Text = "is:bundle"; Apply(form);
                Require(Listed(form).Count == 0, "is:bundle (no fixture has five options)");
                search.Text = "is:normal, format:zip"; Apply(form);
                Require(Listed(form).Count == 10, "is:normal, format:zip: " + Names(form));
                search.Clear(); Apply(form);
                // Installed: a narrowing toggle AND-ed with everything else; its count follows the selection.
                F("installed").SetValue(form, new List<InstalledOutfit> { new InstalledOutfit { Managed = true, Enabled = true, SourcePath = SearchFixture()[1].SourcePath, SelectedIds = new List<string> { "Main" } } });
                Apply(form);
                Require(kindCount("installed") == 1, "installed count " + kindCount("installed"));
                InstalledFirst(form, panel, search);
                Toggle(panel.Kinds, "installed");
                Require(Names(form) == "Cammy Boots" && panel.InstalledOn && count("009") == 1 && count("016") == 0 && button.Text == "Filters 1" && panel.Kinds.AccessibilityObject.GetChild(2).Name == "Installed mods only, 1 mod, selected", "Installed: " + Names(form) + " / " + panel.Kinds.AccessibilityObject.GetChild(2).Name);
                panel.Grid.SetSelected(new[] { "016" }, true);
                Require(Listed(form).Count == 0 && kindCount("installed") == 0 && panel.InstalledOn && panel.Grid.Selected.Contains("016"), "Installed + Juri must intersect and keep both selections");
                Toggle(panel.Kinds, "tools");
                Require(panel.InstalledOn && panel.ToolsOn && panel.Grid.Selected.Count == 1, "selecting a kind must not deselect Installed");
                Get<FlatButton>(form, "filterClearButton").PerformClick();
                Require(!panel.InstalledOn && Listed(form).Count == 10, "Clear did not reset Installed");
            }
        }

        // 2.4.1: installed mods lead the list with no filter, with any filter and among equally relevant search results;
        // their rows carry the green rail. "Cammy Boots" is installed (set by the caller); "Select Icons" is made installed here.
        private static void InstalledFirst(MainForm form, FilterPanel panel, PaddedTextField search)
        {
            // 2.4.1: this is the default sort chip, Installed ↓ (installed first, then the name order A–Z).
            Require(form.LibrarySort == LibrarySortKey.Installed && !form.LibrarySortAscending && SortChips(form)[LibrarySortKey.Installed].Active && SortChips(form)[LibrarySortKey.Installed].Text == "Installed ↓", "the default sort is not Installed ↓");
            var installed = (List<InstalledOutfit>)F("installed").GetValue(form);
            LibraryEntry icons = SearchFixture().First(e => e.Name == "Select Icons");
            F("installed").SetValue(form, new List<InstalledOutfit>(installed) { new InstalledOutfit { Managed = true, Enabled = true, SourcePath = icons.SourcePath, SelectedIds = new List<string> { "Main" } } });
            Apply(form);
            Func<List<string>> names = delegate { return Get<ListView>(form, "modList").Items.Cast<ListViewItem>().Select(i => ((LibraryEntry)i.Tag).Name).ToList(); };   // display order
            List<string> all = names();
            Require(all.Count == 10 && all.Take(2).OrderBy(n => n).SequenceEqual(new[] { "Cammy Boots", "Select Icons" }) && all.Take(2).SequenceEqual(all.Take(2).OrderBy(n => n, StringComparer.OrdinalIgnoreCase)) && all.Skip(2).SequenceEqual(all.Skip(2).OrderBy(n => n, StringComparer.OrdinalIgnoreCase)), "installed first with no filter: " + String.Join(", ", all));
            panel.Grid.SetSelected(new[] { "009" }, true); Apply(form);
            Require(names().First() == "Cammy Boots", "installed first with Cammy selected: " + String.Join(", ", names()));
            panel.Grid.SetSelected(new string[0], true); Toggle(panel.Kinds, FilterPanel.MiscKind); Apply(form);
            Require(names().First() == "Select Icons" && names().Count == 4, "installed first with Misc: " + String.Join(", ", names()));
            Toggle(panel.Kinds, FilterPanel.MiscKind); Apply(form);
            // A plain text search has no ranking: installed first there too.
            search.Text = "i"; Apply(form);
            List<string> searched = names(); int firstPlain = searched.FindIndex(n => n != "Cammy Boots" && n != "Select Icons");
            Require(searched.Take(firstPlain).All(n => n == "Cammy Boots" || n == "Select Icons") && searched.Skip(firstPlain).All(n => n != "Cammy Boots" && n != "Select Icons"), "installed first in a text search: " + String.Join(", ", searched));
            // A costume search keeps its relevance order; installed-first only breaks ties.
            search.Text = "cammy, c1"; Apply(form);
            var ranks = (Dictionary<LibraryEntry, int>)F("libraryRanks").GetValue(form);
            MethodInfo state = typeof(MainForm).GetMethod("CachedInstallState", BindingFlags.Instance | BindingFlags.NonPublic);
            var rows = Get<ListView>(form, "modList").Items.Cast<ListViewItem>().Select(i => (LibraryEntry)i.Tag).ToList();
            for (int i = 1; i < rows.Count; i++)
            {
                int a = ranks[rows[i - 1]], b = ranks[rows[i]];
                bool ia = Convert.ToInt32(state.Invoke(form, new object[] { rows[i - 1] })) != 0, ib = Convert.ToInt32(state.Invoke(form, new object[] { rows[i] })) != 0;
                Require(a < b || (a == b && (ia || !ib)), "costume search order broken at " + rows[i - 1].Name + " (rank " + a + ", installed " + ia + ") / " + rows[i].Name + " (rank " + b + ", installed " + ib + ")");
            }
            search.Clear(); Apply(form);
            // The marker: installed rows have the green rail (and the ✓ Full badge).
            var list = (ListView)F("modList").GetValue(form);
            ListViewItem boots = list.Items.Cast<ListViewItem>().First(i => ((LibraryEntry)i.Tag).Name == "Cammy Boots");
            Rectangle rowRect = boots.Bounds;
            using (var bmp = new Bitmap(list.Width, list.Height))
            {
                list.DrawToBitmap(bmp, new Rectangle(0, 0, list.Width, list.Height));
                Color rail = bmp.GetPixel(rowRect.Left + DpiMetrics.Px(list, 2), rowRect.Top + rowRect.Height / 2);
                Require(Math.Abs(rail.G - Palette.Green.G) < 40 && rail.G > rail.R, "installed row rail colour " + rail + " (expected green)");
            }
            F("installed").SetValue(form, installed); Apply(form);
        }

        // Two configured libraries for the library-tile checks: the fixture mods under C:\fixture\filters and three
        // of them under C:\fixture\other.
        private static readonly string[] TwoLibraryPaths = { @"C:\fixture\filters", @"C:\fixture\other" };
        private static List<LibraryEntry> TwoLibraries()
        {
            var lib = SearchFixture();
            foreach (LibraryEntry e in lib.Where(x => x.Name == "Frame Meter" || x.Name == "Select Icons" || x.Name == "Juri Jacket")) e.SourcePath = @"C:\fixture\other\" + e.Name + ".zip";
            return lib;
        }

        private static void CheckLibraries(string root, Fixture game)
        {
            MethodInfo capture = typeof(MainForm).GetMethod("CaptureView", PrivateInstance), restore = typeof(MainForm).GetMethod("RestoreView", PrivateInstance);
            // One library: no tiles, and a saved library selection is ignored.
            using (Window w = Open(root, null, new Size(1232, 640), SearchFixture(), new[] { @"C:\fixture\filters" }))
            {
                MainForm form = w.Form; FilterPanel panel = Get<FilterPanel>(form, "filterPanel");
                restore.Invoke(form, new object[] { new ViewPreferences { SelectedLibraries = new List<string> { @"C:\fixture\filters" } } }); Apply(form);
                Require(panel.Kinds.ItemCount == 3 && panel.LibrariesSelected.Count == 0 && Listed(form).Count == 10 && form.ActiveFilterCount() == 0, "one library: tiles or selection shown (" + panel.Kinds.ItemCount + ")");
            }
            using (Window w = Open(root, null, new Size(1232, 640), TwoLibraries(), TwoLibraryPaths))
            {
                MainForm form = w.Form; FilterPanel panel = Get<FilterPanel>(form, "filterPanel");
                form.ShowFilters(true, false);
                FlatButton button = Get<FlatButton>(form, "filterButton");
                Func<string, int> tile = delegate(string code) { return panel.Kinds.Cards.Single(c => c.Code == code).Count; };
                string filters = FilterPanel.LibraryPrefix + @"C:\fixture\filters", other = FilterPanel.LibraryPrefix + @"C:\fixture\other";
                Require(panel.Kinds.ItemCount == 5 && tile(filters) == 7 && tile(other) == 3 && panel.Kinds.AccessibilityObject.GetChild(4).Name == @"Library C:\fixture\other, 3 mods", "library tiles: " + String.Join(", ", panel.Kinds.Cards.Select(c => c.Name + " " + c.Count)));
                Toggle(panel.Kinds, other);
                Require(Names(form) == "Frame Meter, Juri Jacket, Select Icons" && button.Text == "Filters 1" && panel.LibrariesSelected.SetEquals(new[] { @"C:\fixture\other" }), "other library: " + Names(form));
                Toggle(panel.Kinds, filters);
                Require(Listed(form).Count == 10 && button.Text == "Filters 1", "libraries must be OR-ed: " + Listed(form).Count);
                Toggle(panel.Kinds, filters);
                panel.Grid.SetSelected(new[] { "016" }, true);
                Require(Names(form) == "Juri Jacket" && tile(filters) == 0 && tile(other) == 1 && panel.Grid.Cards.Single(c => c.Code == "009").Count == 0 && button.Text == "Filters 2", "library AND fighter: " + Names(form));
                Toggle(panel.Kinds, "tools");
                Require(Listed(form).Count == 0 && button.Text == "Filters 3", "library AND fighter AND Code tools");
                panel.Grid.SetSelected(new string[0], true);
                Require(Names(form) == "Frame Meter", "library AND Code tools: " + Names(form));
                ViewPreferences saved = (ViewPreferences)capture.Invoke(form, null);
                string json = Json.Serializer().Serialize(saved);
                Require(saved.SelectedLibraries.SequenceEqual(new[] { @"C:\fixture\other" }) && !json.Contains("\"Source\"") && !json.Contains("\"ModType\"") && !json.Contains("\"ArchiveFormat\"") && !json.Contains("\"Costume\""), "saved libraries / removed values written: " + json);
                Get<FlatButton>(form, "filterClearButton").PerformClick();
                Require(panel.LibrariesSelected.Count == 0 && Listed(form).Count == 10, "Clear did not reset the libraries");
                // The 2.2.0 dropdown's library migrates only while it is configured.
                restore.Invoke(form, new object[] { new ViewPreferences { Source = @"C:\fixture\other\" } }); Apply(form);
                Require(panel.LibrariesSelected.SetEquals(new[] { @"C:\fixture\other" }) && Listed(form).Count == 3, "2.2.0 library not migrated");
                restore.Invoke(form, new object[] { new ViewPreferences { Source = @"C:\nowhere\Mods" } }); Apply(form);
                Require(panel.LibrariesSelected.Count == 0 && Listed(form).Count == 10, "an unconfigured 2.2.0 library was kept");
                // Removing a library in Settings drops its tile and selection (RefreshLibrary calls SetLibraries).
                Toggle(panel.Kinds, other);
                panel.SetLibraries(new[] { @"C:\fixture\filters" }); Apply(form);
                Require(panel.Kinds.ItemCount == 3 && panel.LibrariesSelected.Count == 0 && Listed(form).Count == 10, "a removed library kept its selection");
                // Libraries with the same folder name are told apart by their parent.
                panel.SetLibraries(new[] { @"C:\fixture\a\Mods", @"C:\fixture\b\Mods" });
                Require(panel.Kinds.Cards.Skip(3).Select(c => c.Name).SequenceEqual(new[] { "a", "b" }), "duplicate names: " + String.Join(", ", panel.Kinds.Cards.Skip(3).Select(c => c.Name)));
                panel.SetLibraries(new[] { @"E:\Fluffy\Games\SF6\Mods", @"E:\Games\Fluffy - Copy\Games\SF6\Mods" });
                Require(panel.Kinds.Cards.Skip(3).Select(c => c.Name).SequenceEqual(new[] { "Fluffy", "Fluffy - Copy" }), "the user's two libraries: " + String.Join(", ", panel.Kinds.Cards.Skip(3).Select(c => c.Name)));
                // Inter's metrics can ellipsize a library name that Segoe fits. The
                // portrait band intentionally uses EndEllipsis; retain the full accessible name.
                Require(panel.Kinds.Cards.Select((c, i) => panel.Kinds.NameFits(c.Name) ||
                    (LinuxPlatform.IsLinux && i >= panel.Kinds.GroupBreak && panel.Kinds.GroupBreak >= 0 &&
                     panel.Kinds.ItemAccessibleName(i).StartsWith("Library ", StringComparison.Ordinal) &&
                     panel.Kinds.ItemAccessibleName(i).Contains(c.Name))).All(ok => ok),
                    "a tile name is cut without its full accessible name: " + String.Join(", ", panel.Kinds.Cards.Where(c => !panel.Kinds.NameFits(c.Name)).Select(c => c.Name)));
            }
        }

        private static double Brightness(Bitmap b, Rectangle r)
        {
            double sum = 0; int n = 0;
            for (int y = r.Top + r.Height / 4; y < r.Bottom - r.Height / 4; y += 3) for (int x = r.Left + r.Width / 4; x < r.Right - r.Width / 4; x += 3) { Color c = b.GetPixel(x, y); sum += c.R + c.G + c.B; n++; }
            return n == 0 ? 0 : sum / n;
        }

        private static void CheckGrid(string root, Fixture game)
        {
            using (Window w = Open(root, null, new Size(1460, 920), SearchFixture()))
            {
                MainForm form = w.Form; FilterPanel panel = Get<FilterPanel>(form, "filterPanel");
                form.ShowFilters(true, true); Settle(w.Host);
                FighterGrid grid = panel.Grid; int cols = grid.ColumnCount;
                Key(grid, Keys.Right); Require(grid.FocusIndex == 1, "Right");
                Key(grid, Keys.Down); Require(grid.FocusIndex == 1 + cols, "Down " + grid.FocusIndex);
                Key(grid, Keys.End); Require(grid.FocusIndex == grid.ItemCount - 1, "End");
                Key(grid, Keys.Home); Require(grid.FocusIndex == 0, "Home");
                Key(grid, Keys.Up); Require(grid.FocusIndex == 0, "Up at the top must stay");
                // 2.3.1: W/A/S/D move like the arrow keys; with a modifier they do nothing here.
                Key(grid, Keys.D); Require(grid.FocusIndex == 1, "D");
                Key(grid, Keys.S); Require(grid.FocusIndex == 1 + cols, "S " + grid.FocusIndex);
                Key(grid, Keys.A); Require(grid.FocusIndex == cols, "A " + grid.FocusIndex);
                Key(grid, Keys.W); Require(grid.FocusIndex == 0, "W " + grid.FocusIndex);
                Key(grid, Keys.S | Keys.Control); Key(grid, Keys.D | Keys.Shift); Require(grid.FocusIndex == 0, "Ctrl+S / Shift+D moved the focus");
                // Typed into the search box, W/A/S/D are text and the grid does not move.
                PaddedTextField search = Get<PaddedTextField>(form, "search");
                search.Editor.Focus(); Application.DoEvents();
                foreach (char ch in "wasd") { PostMessage(search.Editor.Handle, 0x0100, (IntPtr)Char.ToUpperInvariant(ch), IntPtr.Zero); PostMessage(search.Editor.Handle, 0x0101, (IntPtr)Char.ToUpperInvariant(ch), IntPtr.Zero); }   // the message loop translates WM_KEYDOWN into WM_CHAR
                Application.DoEvents(); Application.DoEvents();
                // (posted key messages can arrive as two characters in this hidden window; what matters is that they are text)
                // (the translation follows the live keyboard: with Caps Lock on they arrive as capitals)
                Require(search.Text.Length >= 4 && new string(search.Text.ToLowerInvariant().Distinct().ToArray()) == "wasd" && grid.FocusIndex == 0, "search box: \"" + search.Text + "\", grid focus " + grid.FocusIndex);
                search.Text = ""; typeof(MainForm).GetMethod("FlushLibraryFilter", PrivateInstance).Invoke(form, null); Application.DoEvents();
                Key(grid, Keys.Space);
                Require(grid.Cards[0].Selected && Listed(form).Count == 0 && grid.Cards[0].Code == "001", "Space did not select Ryu (no Ryu mods)");
                Key(grid, Keys.Space); Require(!grid.Cards[0].Selected && Listed(form).Count == 10, "Space did not clear");
                int cammy = grid.Cards.ToList().FindIndex(c => c.Code == "009");
                Rectangle box = grid.ItemBounds(cammy);
                typeof(Control).GetMethod("OnMouseDown", PrivateInstance).Invoke(grid, new object[] { new MouseEventArgs(MouseButtons.Left, 1, box.Left + box.Width / 2, box.Top + box.Height / 2, 0) });
                Require(grid.Cards[cammy].Selected && Names(form) == "Cammy Barefoot, Cammy Boots, Cammy Scripted", "click did not select Cammy");
                Require(grid.HitTest(new Point(box.Left + 1, box.Top + 1)) != cammy, "the slanted corner must not hit the card");
                // A click picks one fighter (browsing), Ctrl+click adds one.
                int juri = grid.Cards.ToList().FindIndex(c => c.Code == "016");
                grid.Activate(juri, false); Require(grid.Selected.SetEquals(new[] { "016" }) && Names(form) == "Juri Jacket", "a click on Juri must replace Cammy: " + Names(form));
                grid.Activate(cammy, false, true); Require(grid.Selected.SetEquals(new[] { "009", "016" }), "Ctrl+click must add Cammy");
                grid.Activate(cammy, false); Require(grid.Selected.SetEquals(new[] { "009" }), "a click on one of several selected fighters must keep only it");
                grid.Activate(cammy + 2, true);
                Require(grid.Selected.SetEquals(grid.Cards.Skip(cammy).Take(3).Select(c => c.Code)), "Shift+click range");
                AccessibleObject acc = grid.AccessibilityObject;
                Require(acc.GetChildCount() == grid.Cards.Count && acc.Role == AccessibleRole.List, "accessible children");
                AccessibleObject child = acc.GetChild(cammy);
                Require(child.Name == "Cammy, 3 mods, selected" && (child.State & AccessibleStates.Checked) != 0 && child.Role == AccessibleRole.CheckButton && acc.GetChild(0).Name == "Ryu, 0 mods", "accessible name/state: " + child.Name + " / " + acc.GetChild(0).Name);
                child.DoDefaultAction();
                Require(!grid.Cards[cammy].Selected, "accessible default action did not toggle");
                grid.SetSelected(new string[0], true);
                // The kind tiles behave like the fighter cards (keyboard, accessibility).
                AccessibleObject kinds = panel.Kinds.AccessibilityObject;
                Require(kinds.GetChildCount() == 3 && kinds.GetChild(0).Name == "Tool mods, 3 mods" && kinds.GetChild(1).Name == "Misc, 4 mods" && kinds.GetChild(2).Name == "Installed mods only, 0 mods", "kind accessibility: " + kinds.GetChild(0).Name + " / " + kinds.GetChild(1).Name);
                Key(panel.Kinds, Keys.D); Key(panel.Kinds, Keys.D); Key(panel.Kinds, Keys.A); Require(panel.Kinds.FocusIndex == 1, "tiles: D D A");
                Key(panel.Kinds, Keys.A); Key(panel.Kinds, Keys.Right); Key(panel.Kinds, Keys.Space);
                Require(panel.MiscOn && panel.MiscChips.Visible && Listed(form).Count == 4 && kinds.GetChild(1).Name == "Misc, 4 mods, selected", "Misc tile keyboard");
                // Chips: W/A/S/D move between them like the arrow keys (A/W back, D/S forward).
                Settle(w.Host);
                var chipList = panel.MiscChips.Controls.OfType<FlatButton>().ToList();
                Require(chipList.Count >= 2, "chips for the WASD check");
                chipList[0].Focus(); Application.DoEvents();
                Key(chipList[0], Keys.D); Application.DoEvents();
                Require(chipList[1].Focused, "D on a chip did not move to the next chip");
                Key(chipList[1], Keys.A); Application.DoEvents();
                Require(chipList[0].Focused, "A on a chip did not move back");
                panel.Kinds.Focus();
                Key(panel.Kinds, Keys.Space);
                Require(!panel.MiscOn && !panel.MiscChips.Visible && Listed(form).Count == 10, "Misc tile off");
                Settle(w.Host);
                // Dimmed: a fighter without mods is darker than one with mods (both name tiles).
                using (var bmp = new Bitmap(grid.Width, grid.Height))
                {
                    grid.DrawToBitmap(bmp, new Rectangle(0, 0, grid.Width, grid.Height));
                    double without = Brightness(bmp, grid.ItemBounds(0)), with = Brightness(bmp, grid.ItemBounds(cammy));
                    Require(with > without * 1.25, "dimmed card " + without.ToString("0") + " vs " + with.ToString("0"));
                    bmp.Save(Path.Combine(root, "grid-name-tiles.png"), ImageFormat.Png);
                }
                // Placeholder art: gray until selected, accent colour when selected.
                Toggle(panel.Kinds, "misc"); Settle(w.Host);
                using (var bmp = new Bitmap(panel.Kinds.Width, panel.Kinds.Height))
                {
                    panel.Kinds.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
                    Func<Bitmap, double> tint = delegate(Bitmap b) { double s = 0; int n = 0; for (int y = 0; y < b.Height; y += 5) for (int x = 0; x < b.Width; x += 5) { Color c = b.GetPixel(x, y); s += Math.Abs(c.R - c.B); n++; } return s / n; };
                    double gray = tint(panel.Kinds.Rendered(panel.Kinds.Cards[0], false)), colour = tint(panel.Kinds.Rendered(panel.Kinds.Cards[1], true));
                    Require(gray < 2 && colour > 15 && panel.Kinds.Cards[1].Selected, "kind tile states: gray " + gray.ToString("0.0") + " colour " + colour.ToString("0.0"));                    bmp.Save(Path.Combine(root, "kind-tiles.png"), ImageFormat.Png);
                }
                using (Bitmap art = KindArt.Placeholder(FilterPanel.ToolsKind))
                    Require(art.Width == FighterPortraits.TileWidth && art.Height == FighterPortraits.TileHeight && FilterRealChecks.Opaque(art) > 0.9, "placeholder art");
            }
        }

        private static void CheckAsync(string root, Fixture game)
        {
            Require(game != null, "no fixture game");
            var savedNames = MainForm.StageNames;
            try
            {
                // Synthetic cached PNGs for every fighter, keyed by the fixture game's identity.
                string cache = Path.Combine(root, "synthetic-cache"); Directory.CreateDirectory(cache);
                var ids = Characters.ByCode.Keys.OrderBy(k => k).ToList(); var rows = new List<PortraitInfo>();
                for (int i = 0; i < ids.Count; i++)
                {
                    using (var b = new Bitmap(FighterPortraits.TileWidth, FighterPortraits.TileHeight))
                    using (Graphics g = Graphics.FromImage(b)) { g.Clear(Color.FromArgb(255, 40 + i * 6, 200 - i * 4, 90)); b.Save(Path.Combine(cache, ids[i] + ".png"), ImageFormat.Png); }
                    rows.Add(new PortraitInfo { Id = ids[i], Status = "ok", Candidate = "face", Path = "synthetic", Pak = "re_chunk_000.pak" });
                }
                FighterPortraits.WriteManifest(cache, GamePakReader.Open(game.Game).Identity, rows, new Dictionary<string, string> { { "0800", "Fixture Hut" } }, "synthetic");
                using (Window w = Open(root, game.Game, new Size(1232, 640), SearchFixture()))
                {
                    MainForm form = w.Form; FilterPanel panel = Get<FilterPanel>(form, "filterPanel");
                    form.PortraitCacheDirectory = cache;
                    form.ShowFilters(true, false);
                    Require(form.PortraitsLoading && panel.Grid.PortraitCount == 0, "name tiles must show until the portraits arrive (" + panel.Grid.PortraitCount + ")");
                    Pump(delegate { return !form.PortraitsLoading; }, 20, "cached portrait load");
                    Require(panel.Grid.PortraitCount == ids.Count && form.LastPortraitReport.Cache == "hit", "cached portraits: " + panel.Grid.PortraitCount + " (" + form.LastPortraitReport.Cache + ")");
                    FighterCard ken = panel.Grid.Cards.Single(c => c.Code == "010"); int k = ids.IndexOf("010");
                    Color expected = Color.FromArgb(255, 40 + k * 6, 200 - k * 4, 90), got = ken.Portrait.GetPixel(10, 10);
                    Require(Math.Abs(got.R - expected.R) + Math.Abs(got.G - expected.G) + Math.Abs(got.B - expected.B) < 6, "wrong PNG on Ken's card");
                    Require(MainForm.StageNames.ContainsKey("0800") && MainForm.LibraryRowLabel(SearchFixture()[3], null) == "Stage · Fixture Hut", "stage names from the cache were not applied to the row labels");
                    using (var bmp = new Bitmap(panel.Grid.Width, panel.Grid.Height))
                    {
                        panel.Grid.SetSelected(new[] { "010" }, true); panel.Grid.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
                        Rectangle sel = panel.Grid.ItemBounds(panel.Grid.Cards.IndexOf(ken)), other = panel.Grid.ItemBounds(panel.Grid.Cards.ToList().FindIndex(c => c.Code == "011"));
                        Color selected = bmp.GetPixel(sel.Left + sel.Width / 2, sel.Top + sel.Height / 3), gray = bmp.GetPixel(other.Left + other.Width / 2, other.Top + other.Height / 3);
                        Require(Math.Abs(gray.R - gray.G) <= 3 && Math.Abs(gray.G - gray.B) <= 3 && Math.Abs(selected.G - selected.R) > 20, "grayscale/colour cards: selected " + selected + " other " + gray);
                        bmp.Save(Path.Combine(root, "grid-synthetic-portraits.png"), ImageFormat.Png);
                    }
                }
                // Extraction from the generated PAKs: tiles arrive one by one; the new fighter 035 gets a card.
                using (Window w = Open(root, game.Game, new Size(1232, 640), SearchFixture()))
                {
                    MainForm form = w.Form; FilterPanel panel = Get<FilterPanel>(form, "filterPanel");
                    form.ShowFilters(true, false);
                    Require(panel.Grid.PortraitCount == 0, "portraits before the pump");
                    Pump(delegate { return !form.PortraitsLoading; }, 30, "portrait extraction");
                    Require(form.LastPortraitReport.Cache == "miss" && panel.Grid.Cards.Any(c => c.Code == "035" && c.Name == "esf035" && c.Portrait != null), "new fighter card missing");
                    Require(panel.Grid.PortraitCount == 9 && File.Exists(Path.Combine(form.PortraitCacheDirectory, "portraits.tsv")), "extracted portraits " + panel.Grid.PortraitCount);
                }
            }
            finally { MainForm.StageNames = savedNames; }
        }

        // ---- 2.4.1 sort chips (Added · Last used · Installed) ----------------------------------------------------
        private static readonly DateTime SortBase = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        // SearchFixture with Added times (Hitbox Viewer and Juri Jacket share one: the name breaks the tie).
        private static List<LibraryEntry> SortFixture()
        {
            var lib = SearchFixture();
            string[] arrival = { "Lighting Overhaul", "Cammy Barefoot", "Frame Meter", "Juri Jacket", "Hitbox Viewer", "Select Icons", "Louder Impact", "Cammy Boots", "Ranger Hut Night", "Cammy Scripted" };
            for (int i = 0; i < arrival.Length; i++) { string name = arrival[i]; lib.Single(e => e.Name == name).AddedUtc = SortBase.AddDays(i); }
            lib.Single(e => e.Name == "Hitbox Viewer").AddedUtc = SortBase.AddDays(3);
            return lib;
        }
        // Installed: Cammy Boots (day 2) and Select Icons (day 1). Use records: Juri Jacket removed (day 20), Louder Impact a
        // disabled replacement (day 15), Frame Meter removed (day 10), Cammy Boots removed before (day 5: the latest wins).
        private static void SortUsage(MainForm form, List<LibraryEntry> lib)
        {
            Func<string, string> src = delegate(string name) { return lib.Single(e => e.Name == name).SourcePath; };
            F("installed").SetValue(form, new List<InstalledOutfit> {
                new InstalledOutfit { Managed = true, Enabled = true, SourcePath = src("Cammy Boots"), SelectedIds = new List<string> { "Main" }, InstalledUtc = SortBase.AddDays(2) },
                new InstalledOutfit { Managed = true, Enabled = true, SourcePath = src("Select Icons"), SelectedIds = new List<string> { "Main" }, InstalledUtc = SortBase.AddDays(1) } });
            F("gameRows").SetValue(form, new List<GameModRow> { new GameModRow { Kind = "Replacement", Mod = new ReplaceMod { Name = "Louder", SourcePath = src("Louder Impact"), Enabled = false, InstalledUtc = SortBase.AddDays(15).ToString("o") } } });
            F("removedUsage").SetValue(form, new List<UsageRecord> { new UsageRecord { SourcePath = src("Juri Jacket"), Utc = SortBase.AddDays(20) }, new UsageRecord { SourcePath = src("Frame Meter"), Utc = SortBase.AddDays(10) }, new UsageRecord { SourcePath = src("Cammy Boots"), Utc = SortBase.AddDays(5) } });
        }
        private static List<string> Shown(MainForm form) { return Get<ListView>(form, "modList").Items.Cast<ListViewItem>().Select(i => ((LibraryEntry)i.Tag).Name).ToList(); }
        private static Dictionary<LibrarySortKey, FlatButton> SortChips(MainForm form) { return (Dictionary<LibrarySortKey, FlatButton>)F("sortChips").GetValue(form); }

        private static void CheckSorts(string root, Fixture game, List<string> report)
        {
            List<LibraryEntry> lib = SortFixture();
            string file = Path.Combine(root, "view-settings-sort.json");
            MethodInfo capture = typeof(MainForm).GetMethod("CaptureView", PrivateInstance), restore = typeof(MainForm).GetMethod("RestoreView", PrivateInstance);
            using (Window w = Open(root, null, new Size(1460, 920), lib))
            {
                MainForm form = w.Form; FilterPanel panel = Get<FilterPanel>(form, "filterPanel"); PaddedTextField search = Get<PaddedTextField>(form, "search");
                ListView list = Get<ListView>(form, "modList");
                SortUsage(form, lib); Apply(form);
                var chips = SortChips(form);
                FlatButton added = chips[LibrarySortKey.Added], used = chips[LibrarySortKey.LastUsed], inst = chips[LibrarySortKey.Installed];
                Func<string> state = delegate { return form.LibrarySort + (form.LibrarySortAscending ? " asc" : " desc") + " [" + String.Join(" | ", chips.Values.Select(c => c.Text + (c.Active ? "*" : "") + " = " + c.AccessibleName)) + "]"; };
                // The default: Installed ↓ (installed first, then names A–Z).
                const string installedDesc = "Cammy Boots, Select Icons, Cammy Barefoot, Cammy Scripted, Frame Meter, Hitbox Viewer, Juri Jacket, Lighting Overhaul, Louder Impact, Ranger Hut Night";
                Require(form.LibrarySort == LibrarySortKey.Installed && !form.LibrarySortAscending && inst.Active && !added.Active && !used.Active && inst.Text == "Installed \u2193" && added.Text == "Added" && used.Text == "Last used", "default: " + state());
                Require(inst.AccessibleName == "Sort by Installed, installed first" && added.AccessibleName == "Sort by Added, newest first" && used.AccessibleName == "Sort by Last used, most recent first" && inst.AccessibleDescription.StartsWith("Selected"), "accessible names: " + state());
                Require(String.Join(", ", Shown(form)) == installedDesc, "Installed \u2193: " + String.Join(", ", Shown(form)));
                // Each sort, both directions; a new sort starts descending, a second click flips it.
                var expected = new[] {
                    new object[] { added, false, "Cammy Scripted, Ranger Hut Night, Cammy Boots, Louder Impact, Select Icons, Hitbox Viewer, Juri Jacket, Frame Meter, Cammy Barefoot, Lighting Overhaul", "Added \u2193", "Sort by Added, newest first" },
                    new object[] { added, true, "Lighting Overhaul, Cammy Barefoot, Frame Meter, Hitbox Viewer, Juri Jacket, Select Icons, Louder Impact, Cammy Boots, Ranger Hut Night, Cammy Scripted", "Added \u2191", "Sort by Added, oldest first" },
                    new object[] { used, false, "Juri Jacket, Louder Impact, Frame Meter, Cammy Boots, Select Icons, Cammy Barefoot, Cammy Scripted, Hitbox Viewer, Lighting Overhaul, Ranger Hut Night", "Last used \u2193", "Sort by Last used, most recent first" },
                    new object[] { used, true, "Cammy Barefoot, Cammy Scripted, Hitbox Viewer, Lighting Overhaul, Ranger Hut Night, Select Icons, Cammy Boots, Frame Meter, Louder Impact, Juri Jacket", "Last used \u2191", "Sort by Last used, least recent first" },
                    new object[] { inst, false, installedDesc, "Installed \u2193", "Sort by Installed, installed first" },
                    new object[] { inst, true, "Cammy Barefoot, Cammy Scripted, Frame Meter, Hitbox Viewer, Juri Jacket, Lighting Overhaul, Louder Impact, Ranger Hut Night, Cammy Boots, Select Icons", "Installed \u2191", "Sort by Installed, not installed first" } };
                foreach (object[] step in expected)
                {
                    FlatButton chip = (FlatButton)step[0];
                    chip.PerformClick(); Application.DoEvents();
                    Require(form.LibrarySortAscending == (bool)step[1] && chip.Active && chip.Text == (string)step[3] && chip.AccessibleName == (string)step[4] && chips.Values.Count(c => c.Active) == 1, "chip state after " + step[3] + ": " + state());
                    Require(String.Join(", ", Shown(form)) == (string)step[2], step[3] + ": " + String.Join(", ", Shown(form)) + " (expected " + step[2] + ")");
                }
                report.Add("  each sort both ways; ties by name (Hitbox Viewer / Juri Jacket share an Added time); never used last in \u2193, first in \u2191; Last used = the latest install/uninstall record (no history in the fixture), not \"installed now\"");
                // Last used: the index is built once from the loaded data and reused (no rebuild per sort, no disk).
                int reads = LibraryUsage.FolderReads;
                used.PerformClick(); object index = F("usageIndex").GetValue(form); used.PerformClick(); used.PerformClick();
                Require(index != null && Object.ReferenceEquals(index, F("usageIndex").GetValue(form)) && LibraryUsage.FolderReads == reads, "the Last used index was rebuilt or records were read during a sort");
                DateTime? jacket = form.LibraryLastUsed(lib.Single(e => e.Name == "Juri Jacket")), boots = form.LibraryLastUsed(lib.Single(e => e.Name == "Cammy Boots")), hut = form.LibraryLastUsed(lib.Single(e => e.Name == "Ranger Hut Night"));
                Require(jacket == SortBase.AddDays(20) && boots == SortBase.AddDays(5) && hut == null, "LibraryLastUsed: " + jacket + " / " + boots + " / " + hut);
                // Search: Installed keeps relevance first (rank, then installed, then name); Added/Last used lead instead.
                inst.PerformClick(); if (form.LibrarySortAscending) inst.PerformClick();
                // A Cammy shared-parts mod (rank 1, the newest arrival) joins for this part.
                var shared = new LibraryEntry { Name = "Cammy Shared Skin", Kind = "ZIP", OptionCount = 1, SourcePath = @"C:\fixture\filters\Cammy Shared Skin.zip", Character = "Cammy", CharacterCodes = new List<string> { "009" }, CostumeKeys = new List<string> { "009/000" }, AddedUtc = SortBase.AddDays(30) };
                F("library").SetValue(form, new List<LibraryEntry>(lib) { shared });
                search.Text = "cammy, c1"; Apply(form);
                var ranks = (Dictionary<LibraryEntry, int>)F("libraryRanks").GetValue(form);
                var rows = list.Items.Cast<ListViewItem>().Select(i => (LibraryEntry)i.Tag).ToList();
                Require(rows.Count >= 2 && ranks.Values.Distinct().Count() >= 2, "the costume search fixture needs two ranks: " + String.Join(", ", rows.Select(r => r.Name + " " + ranks[r])));
                for (int i = 1; i < rows.Count; i++) Require(ranks[rows[i - 1]] <= ranks[rows[i]], "Installed with a costume search: relevance is not first at " + rows[i].Name);
                List<string> relevance = Shown(form);
                added.PerformClick();
                rows = list.Items.Cast<ListViewItem>().Select(i => (LibraryEntry)i.Tag).ToList();
                for (int i = 1; i < rows.Count; i++) Require(rows[i - 1].AddedUtc >= rows[i].AddedUtc, "Added with a search: not by Added at " + rows[i].Name);
                Require(new HashSet<string>(Shown(form)).SetEquals(relevance) && !Shown(form).SequenceEqual(relevance), "Added with a search changed the results or kept the relevance order: " + String.Join(", ", Shown(form)));
                // Enter opens the top row of the current order.
                form.OpenTopLibraryMatch(); Pump(() => !form.Working, 30, "opening the top match");
                Require(list.Items.Count > 0 && list.Items[0].Selected && ((LibraryEntry)list.Items[0].Tag).Name == "Cammy Shared Skin", "Enter did not pick the top row of the Added order");
                search.Clear(); F("library").SetValue(form, lib); F("package").SetValue(form, null); Apply(form);
                // Filters, Clear and the selection survive a sort change.
                LibraryEntry picked = lib.Single(e => e.Name == "Juri Jacket");
                F("package").SetValue(form, new ModPackage { SourcePath = picked.SourcePath, Name = picked.Name });
                panel.Grid.SetSelected(new[] { "009", "016" }, true);
                List<string> filtered = Listed(form);
                used.PerformClick(); Application.DoEvents();
                Require(Listed(form).SequenceEqual(filtered) && form.ActiveFilterCount() == 1 && list.SelectedItems.Count == 1 && Object.ReferenceEquals(list.SelectedItems[0].Tag, picked), "a sort change lost the filter or the selection: " + Names(form) + " / selected " + (list.SelectedItems.Count == 0 ? "none" : list.SelectedItems[0].Text));
                Get<FlatButton>(form, "filterClearButton").PerformClick();
                Require(form.LibrarySort == LibrarySortKey.LastUsed && Listed(form).Count == 10 && String.Join(", ", Shown(form)) == (string)expected[2][2], "Clear changed the sort: " + state() + ": " + String.Join(", ", Shown(form)));
                F("package").SetValue(form, null);
                // Keyboard: the chips are tab stops after the search; W/A/S/D move between them like the arrows.
                Control strip = added.Parent;
                Require(chips.Values.All(c => c.TabStop && c.CanSelect) && strip.Parent == search.Parent && strip.TabIndex > search.TabIndex, "the chips are not keyboard reachable after the search");
                var press = new KeyEventArgs(Keys.D); typeof(Control).GetMethod("OnKeyDown", PrivateInstance).Invoke(added, new object[] { press });
                Require(press.Handled, "D on a chip was not handled");
                Require(UiAudit.ToolTipFindings(form).Count == 0, "tooltips: " + String.Join("; ", UiAudit.ToolTipFindings(form)));
                // One slim row between the search and the list (26 px with the gap), Refresh at its right end; also at
                // the narrowest library column.
                FlatButton refresh = Get<FlatButton>(form, "refreshLibraryButton");
                foreach (int width in new[] { 0, 220 })
                {
                    if (width > 0) { SplitContainer split = Get<SplitContainer>(form, "librarySplit"); split.SplitterDistance = split.Panel1MinSize; Settle(w.Host); }
                    Rectangle s = In(strip, w.Host), sr = In(search, w.Host), lr = In(list, w.Host), rr = In(refresh, w.Host);
                    Require(s.Height == DpiMetrics.Px(form, 26) && s.Top >= sr.Bottom && lr.Top >= s.Bottom && rr.Right <= s.Right && rr.Height == DpiMetrics.Px(form, 20), w.Context + ": strip " + s + " search " + sr + " list " + lr + " refresh " + rr);
                    Rectangle previous = Rectangle.Empty;
                    foreach (FlatButton c in new[] { added, used, inst })
                    {
                        Rectangle r = In(c, w.Host);
                        Require(r.Height == DpiMetrics.Px(form, 20) && r.Top == s.Top && r.Right <= rr.Left && r.Left >= previous.Right && r.Width >= TextRenderer.MeasureText(c.Text, c.Font, Size.Empty, TextFormatFlags.NoPadding).Width + DpiMetrics.Px(form, 4), w.Context + " (library " + (width > 0 ? width + " px" : "default") + "): chip " + c.Text + " " + r + " refresh " + rr);
                        previous = r;
                    }
                    report.Add("  strip " + s.Width + "x" + s.Height + " px at " + form.DeviceDpi + " DPI (" + (width > 0 ? "narrowest library column" : "default column") + "): chips " + String.Join(", ", new[] { added, used, inst }.Select(c => c.Width.ToString())) + " px, Refresh " + rr.Width + " px");
                }
                // The saved choice: written with the view settings and restored; old files (no field) or an unknown value = Installed ↓.
                added.PerformClick(); added.PerformClick();
                ((ViewPreferences)capture.Invoke(form, null)).Save(file);
            }
            ViewPreferences saved = ViewPreferences.Load(file);
            Require(saved.LibrarySort == "added" && saved.LibrarySortOrder == "asc", "saved sort: " + saved.LibrarySort + " " + saved.LibrarySortOrder);
            Action<ViewPreferences, LibrarySortKey, bool, string> restored = delegate(ViewPreferences prefs, LibrarySortKey key, bool asc, string what)
            {
                using (Window w = Open(root, null, new Size(1232, 640), null))
                {
                    MainForm form = w.Form;
                    restore.Invoke(form, new object[] { prefs });
                    F("library").SetValue(form, SortFixture()); Apply(form);
                    FlatButton chip = SortChips(form)[key];
                    Require(form.LibrarySort == key && form.LibrarySortAscending == asc && chip.Active && chip.Text.EndsWith(asc ? "\u2191" : "\u2193"), what + ": " + form.LibrarySort + " " + form.LibrarySortAscending);
                    if (key == LibrarySortKey.Added) Require(Shown(form).First() == "Lighting Overhaul", what + ": list not in Added \u2191 order");
                }
            };
            restored(saved, LibrarySortKey.Added, true, "restored Added \u2191");
            File.WriteAllText(file, "{\"Format\":\"sf6-installer-view-v2\",\"Search\":\"\",\"FiltersOpen\":false}");
            restored(ViewPreferences.Load(file), LibrarySortKey.Installed, false, "a 2.4.0 file without a sort");
            File.WriteAllText(file, "{\"Format\":\"sf6-installer-view-v2\",\"LibrarySort\":\"size\",\"LibrarySortOrder\":\"asc\"}");
            restored(ViewPreferences.Load(file), LibrarySortKey.Installed, false, "an unknown saved sort");
            File.WriteAllText(file, "{\"Format\":\"sf6-installer-view-v2\",\"LibrarySort\":\"last_used\"}");
            restored(ViewPreferences.Load(file), LibrarySortKey.LastUsed, false, "a saved sort without an order");
        }

        // Added = creation time, or the write time when the creation time is missing or later; the index keeps it and
        // an index record without it is filled in by the next listing without relisting the archive.
        private static void CheckAddedTime(string root)
        {
            string lib = Path.Combine(root, "added-lib"); Directory.CreateDirectory(lib);
            string later = Path.Combine(lib, "Copied Later.zip"), normal = Path.Combine(lib, "Normal.zip");
            Zip(later, "natives/stm/product/model/esf/esf009/001/01/esf009_001_01.mesh.230110883");
            Zip(normal, "natives/stm/product/model/esf/esf016/001/01/esf016_001_01.mesh.230110883");
            DateTime written = new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc), created = new DateTime(2026, 2, 1, 8, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(later, written); File.SetCreationTimeUtc(later, created);   // creation later than the write time
            File.SetCreationTimeUtc(normal, written); File.SetLastWriteTimeUtc(normal, created);
            // Linux filesystems need not support setting birth time through Wine. Verify the
            // fallback with explicit values, then use the actual persisted times for disk/cache checks.
            Require(Library.AddedTime(created, written) == written && Library.AddedTime(written, created) == written, "creation/write fallback");
            if (!LinuxPlatform.IsLinux) Require(new FileInfo(later).CreationTimeUtc > new FileInfo(later).LastWriteTimeUtc, "the temp file did not keep a creation time later than its write time");
            DateTime laterExpected = Library.AddedTime(new FileInfo(later).CreationTimeUtc, new FileInfo(later).LastWriteTimeUtc);
            DateTime normalExpected = Library.AddedTime(new FileInfo(normal).CreationTimeUtc, new FileInfo(normal).LastWriteTimeUtc);
            LibraryEntry e1 = Library.DescribeEntry(later), e2 = Library.DescribeEntry(normal);
            Require(e1.AddedUtc == laterExpected && e2.AddedUtc == normalExpected, "Added times differ from the persisted filesystem timestamps");
            Require(Library.AddedTime(DateTime.FromFileTimeUtc(0), written) == written && Library.AddedTime(created, DateTime.FromFileTimeUtc(0)) == created && Library.AddedTime(DateTime.MinValue, DateTime.MinValue) == DateTime.MinValue, "missing creation/write times");
            string folder = Path.Combine(lib, "Folder Mod"); Directory.CreateDirectory(folder); Directory.SetCreationTimeUtc(folder, created); Directory.SetLastWriteTimeUtc(folder, created.AddHours(1));
            Require(Library.DescribeEntry(folder).AddedUtc == Library.AddedTime(new DirectoryInfo(folder).CreationTimeUtc, new DirectoryInfo(folder).LastWriteTimeUtc), "folder Added: " + Library.DescribeEntry(folder).AddedUtc.ToString("o"));
            string cache = Path.Combine(root, "added-index"), previous = Library.IdentityCacheDirectory;
            try
            {
                Library.IdentityCacheDirectory = cache;
                int misses = Library.CacheMisses;
                List<LibraryEntry> listed = Library.Scan(new[] { lib }, "");
                string index = Path.Combine(cache, Library.CacheFilename);
                LibraryIdentityCache records = Json.Read<LibraryIdentityCache>(index);
                Require(Library.CacheMisses - misses == 3 && records.Format == "sf6-native-library-index-v4" && records.Records.Count == 2 && records.Records.All(r => r.AddedUtc == Library.AddedStamp(Path.GetFileName(r.SourcePath) == "Copied Later.zip" ? laterExpected : normalExpected)) && listed.Single(e => e.Name == "Copied Later").AddedUtc == laterExpected, "first listing: index " + String.Join(", ", records.Records.Select(r => Path.GetFileName(r.SourcePath) + " " + r.AddedUtc)));
                // A record from before 2.4.1 (no AddedUtc): loaded, not relisted, filled in and saved.
                foreach (LibraryIdentityRecord r in records.Records) r.AddedUtc = null;
                Json.Write(index, records);
                Require(Json.Read<LibraryIdentityCache>(index).Records.All(r => r.AddedUtc == null), "the old-style index still has Added times");
                misses = Library.CacheMisses; Library.IdentityCacheDirectory = cache;
                listed = Library.Scan(new[] { lib }, "");
                records = Json.Read<LibraryIdentityCache>(index);
                Require(Library.CacheMisses - misses == 1 && records.Records.All(r => r.AddedUtc == Library.AddedStamp(Path.GetFileName(r.SourcePath) == "Copied Later.zip" ? laterExpected : normalExpected)) && listed.Single(e => e.Name == "Normal").AddedUtc == normalExpected, "upgrade relisted " + (Library.CacheMisses - misses) + " sources (only the folder is listed again) / records " + String.Join(", ", records.Records.Select(r => r.AddedUtc)));
            }
            finally { Library.IdentityCacheDirectory = previous; }
        }

        // 2,667 synthetic mods (the user's library size): sort keys and a full chip click (sort + list update), in ms.
        private static string CheckSortTiming(string root)
        {
            var random = new Random(2667);
            var lib = new List<LibraryEntry>();
            for (int i = 0; i < 2667; i++)
            {
                string name = "Mod " + random.Next(100000).ToString("D5") + " " + i;
                var e = new LibraryEntry { Name = name, Kind = "ZIP", OptionCount = 1, SourcePath = @"C:\fixture\timing\" + name + ".zip", AddedUtc = SortBase.AddMinutes(random.Next(0, 500000)) };
                if (i % 3 == 0) { e.Character = "Cammy"; e.CharacterCodes = new List<string> { "009" }; e.CostumeKeys = new List<string> { "009/00" + (1 + i % 3) }; }
                lib.Add(e);
            }
            var outfits = lib.Where((e, i) => i % 40 == 0).Select(e => new InstalledOutfit { Managed = true, Enabled = true, SourcePath = e.SourcePath, SelectedIds = new List<string> { "Main" }, InstalledUtc = SortBase }).ToList();
            var removed = lib.Where((e, i) => i % 9 == 1).Select(e => new UsageRecord { SourcePath = e.SourcePath, Utc = SortBase.AddMinutes(random.Next(0, 500000)) }).ToList();
            var lines = new List<string>();
            using (Window w = Open(root, null, new Size(1460, 920), null))
            {
                MainForm form = w.Form;
                F("installed").SetValue(form, outfits); F("removedUsage").SetValue(form, removed);
                F("library").SetValue(form, lib); Apply(form); Settle(w.Host);
                ListView list = Get<ListView>(form, "modList");
                Require(list.Items.Count == 2667, "listed " + list.Items.Count);
                // Sort keys alone (what a sort costs before the list redraws), median of 5.
                MethodInfo sortRows = typeof(MainForm).GetMethod("SortLibraryRows", PrivateInstance);
                var ranked = lib.Select(e => new KeyValuePair<LibraryEntry, int>(e, 0)).ToList();
                var keys = new List<string>();
                foreach (LibrarySortKey key in new[] { LibrarySortKey.Added, LibrarySortKey.LastUsed, LibrarySortKey.Installed })
                    foreach (bool asc in new[] { false, true })
                    {
                        F("librarySort").SetValue(form, key); F("librarySortAscending").SetValue(form, asc);
                        var times = new List<double>();
                        for (int n = 0; n < 5; n++) { var clock = System.Diagnostics.Stopwatch.StartNew(); sortRows.Invoke(form, new object[] { ranked }); times.Add(clock.Elapsed.TotalMilliseconds); }
                        times.Sort(); keys.Add(LibrarySorting.Label(key) + (asc ? " \u2191 " : " \u2193 ") + times[2].ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
                    }
                form.SetLibrarySort(LibrarySortKey.Installed, false); Settle(w.Host);
                var chips = SortChips(form); var clicks = new List<string>(); double worst = 0;
                int reads = LibraryUsage.FolderReads;
                foreach (var step in new[] { LibrarySortKey.Added, LibrarySortKey.Added, LibrarySortKey.LastUsed, LibrarySortKey.LastUsed, LibrarySortKey.Installed, LibrarySortKey.Installed })
                {
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    chips[step].PerformClick(); list.Refresh();
                    double ms = clock.Elapsed.TotalMilliseconds; worst = Math.Max(worst, ms);
                    clicks.Add(chips[step].Text + " " + ms.ToString("0", System.Globalization.CultureInfo.InvariantCulture));
                    Require(list.Items.Count == 2667, "rows lost after " + chips[step].Text);
                }
                Require(LibraryUsage.FolderReads == reads, "records were read during a sort");
                lines.Add("2,667 synthetic mods at " + form.DeviceDpi + " DPI: sort keys only (median of 5, ms): " + String.Join(", ", keys));
                lines.Add("chip click incl. list update and repaint (ms): " + String.Join(", ", clicks));
                Require(worst < 1000, "a sort click took " + worst.ToString("0") + " ms");
            }
            return String.Join(Environment.NewLine + "  ", lines);
        }

        // The Refresh button at the end of the sort row runs F5's refresh (RunShortcut): same scan and status text,
        // disabled while it runs, filters / sort / selection kept.
        private static void CheckSortRefresh(string root)
        {
            string lib = Path.Combine(root, "refresh-lib"); Directory.CreateDirectory(lib);
            Zip(Path.Combine(lib, "Cammy Refresh.zip"), "natives/stm/product/model/esf/esf009/001/01/esf009_001_01.mesh.230110883");
            Zip(Path.Combine(lib, "Juri Refresh.zip"), "natives/stm/product/model/esf/esf016/001/01/esf016_001_01.mesh.230110883");
            Zip(Path.Combine(lib, "Ryu Refresh.zip"), "natives/stm/product/model/esf/esf001/001/01/esf001_001_01.mesh.230110883");
            using (Window w = Open(root, null, new Size(1460, 920), null, new[] { lib }))
            {
                MainForm form = w.Form; FilterPanel panel = Get<FilterPanel>(form, "filterPanel"); ListView list = Get<ListView>(form, "modList");
                FlatButton refresh = Get<FlatButton>(form, "refreshLibraryButton"); Label status = Get<Label>(form, "statusText");
                Require(refresh.AccessibleName == "Refresh libraries" && refresh.Enabled && refresh.Visible, "Refresh button: " + refresh.AccessibleName);
                form.RunShortcut(ShortcutAction.RefreshLibraries);
                string f5 = status.Text;
                Require(form.Working && !refresh.Enabled, "F5 refresh: busy " + form.Working + ", button enabled " + refresh.Enabled);
                Pump(() => !form.Working, 60, "the F5 refresh");
                Require(Listed(form).Count == 3 && refresh.Enabled, "after F5: " + Names(form));
                LibraryEntry cammy = ((List<LibraryEntry>)F("library").GetValue(form)).Single(e => e.Name == "Cammy Refresh");
                typeof(MainForm).GetMethod("LoadPackage", PrivateInstance, null, new[] { typeof(LibraryEntry) }, null).Invoke(form, new object[] { cammy });
                Pump(() => !form.Working, 60, "opening Cammy Refresh");
                panel.Grid.SetSelected(new[] { "009", "016" }, true);
                SortChips(form)[LibrarySortKey.Added].PerformClick(); SortChips(form)[LibrarySortKey.Added].PerformClick();
                List<string> before = Shown(form);
                refresh.PerformClick();
                Require(form.Working && !refresh.Enabled && status.Text == f5, "the button did not start F5's refresh: busy " + form.Working + ", status '" + status.Text + "' (F5: '" + f5 + "')");
                refresh.PerformClick(); form.RunShortcut(ShortcutAction.RefreshLibraries);   // ignored while it runs
                Pump(() => !form.Working, 60, "the button refresh");
                Require(refresh.Enabled && Shown(form).SequenceEqual(before) && form.ActiveFilterCount() == 1 && panel.Grid.Selected.SetEquals(new[] { "009", "016" }) && form.LibrarySort == LibrarySortKey.Added && form.LibrarySortAscending, "after the button refresh: " + String.Join(", ", Shown(form)) + " (was " + String.Join(", ", before) + ") sort " + form.LibrarySort);
                Require(list.SelectedItems.Count == 1 && String.Equals(((LibraryEntry)list.SelectedItems[0].Tag).SourcePath, cammy.SourcePath, StringComparison.OrdinalIgnoreCase), "the selection was lost by the refresh");
            }
            CheckRefreshToTop(root);
        }
        // 2.5.2: with Added ↓ a manual refresh scrolls the list back to the top (the newest mods); other sorts keep the scroll.
        private static void CheckRefreshToTop(string root)
        {
            string lib = Path.Combine(root, "refresh-top-lib"); Directory.CreateDirectory(lib);
            for (int i = 0; i < 40; i++) Zip(Path.Combine(lib, "Ryu Scroll " + i.ToString("00") + ".zip"), "natives/stm/product/model/esf/esf001/001/01/esf001_001_01.mesh.230110883");
            using (Window w = Open(root, null, new Size(1460, 920), null, new[] { lib }))
            {
                MainForm form = w.Form; ListView list = Get<ListView>(form, "modList");
                form.RunShortcut(ShortcutAction.RefreshLibraries); Pump(() => !form.Working, 60, "the first scan");
                Func<bool, int> refreshFromBottom = delegate(bool ascending)
                {
                    form.SetLibrarySort(LibrarySortKey.Added, ascending); Application.DoEvents();
                    ListViewItem last = list.Items[list.Items.Count - 1]; last.Selected = true; last.Focused = true; Pump(() => !form.Working, 60, "opening the last row");
                    list.EnsureVisible(list.Items.Count - 1); Application.DoEvents();
                    Require(list.TopItem != null && list.TopItem.Index > 0, "the list did not scroll: " + list.Items.Count + " rows");
                    form.RunShortcut(ShortcutAction.RefreshLibraries); Pump(() => !form.Working, 60, "the refresh"); Application.DoEvents();
                    return list.TopItem.Index;
                };
                Require(list.Items.Count == 40, "rows " + list.Items.Count);
                Require(refreshFromBottom(false) == 0, "Added ↓ refresh did not go to the top");
                Require(refreshFromBottom(true) > 0, "Added ↑ refresh went to the top");
            }
        }

        // 288-DPI renders of the list header with each sort active (sort-added.png, sort-last-used.png, sort-installed.png).
        private static string CheckSortScreens(string root)
        {
            var files = new List<string>();
            List<LibraryEntry> lib = SortFixture();
            using (Window w = Open(root, null, new Size(1460, 920), lib))
            {
                MainForm form = w.Form; SortUsage(form, lib); Apply(form);
                ListView list = Get<ListView>(form, "modList");
                Control browser = list.Parent;
                foreach (var shot in new[] { new { Key = LibrarySortKey.Added, File = "sort-added.png" }, new { Key = LibrarySortKey.LastUsed, File = "sort-last-used.png" }, new { Key = LibrarySortKey.Installed, File = "sort-installed.png" } })
                {
                    form.SetLibrarySort(shot.Key, false); Settle(w.Host); w.Host.Refresh();
                    Rectangle b = In(browser, w.Host), l = In(list, w.Host);
                    int rows = list.Items.Count > 0 ? list.GetItemRect(0).Height * 3 : 0;
                    var crop = new Rectangle(b.Left, b.Top, b.Width, Math.Min(b.Height, l.Top - b.Top + rows));
                    using (var full = new Bitmap(w.Host.Width, w.Host.Height))
                    {
                        w.Host.DrawToBitmap(full, new Rectangle(0, 0, full.Width, full.Height));
                        using (Bitmap part = full.Clone(crop, full.PixelFormat)) part.Save(Path.Combine(root, shot.File), ImageFormat.Png);
                    }
                    files.Add(shot.File);
                }
                return "screenshots at " + form.DeviceDpi + " DPI: " + String.Join(", ", files);
            }
        }

        private static void CheckViewSettings(string root, Fixture game)
        {
            string file = Path.Combine(root, "view-settings.json");
            MethodInfo capture = typeof(MainForm).GetMethod("CaptureView", PrivateInstance), restore = typeof(MainForm).GetMethod("RestoreView", PrivateInstance);
            using (Window w = Open(root, null, new Size(1232, 640), SearchFixture()))
            {
                MainForm form = w.Form; FilterPanel panel = Get<FilterPanel>(form, "filterPanel");
                form.ShowFilters(true, false);
                Toggle(panel.Kinds, "misc"); Chip(panel, "Stage").PerformClick(); Toggle(panel.Kinds, "installed");
                ((ViewPreferences)capture.Invoke(form, null)).Save(file);
            }
            ViewPreferences saved = ViewPreferences.Load(file);
            Require(saved.FiltersOpen && saved.Kinds.SequenceEqual(new[] { "misc" }) && saved.MiscAreas.SequenceEqual(new[] { "Stage" }) && saved.InstalledOnly && saved.Fighters.Count == 0 && saved.Character == "", "saved: open " + saved.FiltersOpen + " kinds " + String.Join(",", saved.Kinds));
            string text = File.ReadAllText(file);
            Require(!text.Contains("\"Source\"") && !text.Contains("\"ModType\"") && !text.Contains("\"ArchiveFormat\"") && !text.Contains("\"Costume\""), "removed dropdown values are still written");
            using (Window w = Open(root, null, new Size(1232, 640), null))
            {
                MainForm form = w.Form; FilterPanel panel = Get<FilterPanel>(form, "filterPanel");
                restore.Invoke(form, new object[] { saved });
                // The library arrives after the view is restored (as at start-up).
                F("library").SetValue(form, SearchFixture()); Apply(form);
                Require(panel.Visible && panel.MiscOn && !panel.ToolsOn && panel.InstalledOn && panel.MiscSelected.SetEquals(new[] { "Stage" }), "restored state differs");
                Require(Listed(form).Count == 0, "Installed AND Misc Stage: " + Names(form));
                Toggle(panel.Kinds, "installed");
                Require(Names(form) == "Lighting Overhaul, Ranger Hut Night", "restored Misc Stage: " + Names(form));
            }
            // Values of the removed dropdowns (2.2.0 / test builds) are ignored: nothing filters, nothing counts.
            File.WriteAllText(file, "{\"Format\":\"sf6-installer-view-v2\",\"ModType\":\"Tool mod\",\"ArchiveFormat\":\"RAR\",\"Costume\":\"C1\",\"Source\":\"C:\\\\nowhere\",\"Character\":\"\"}");
            using (Window w = Open(root, null, new Size(1232, 640), SearchFixture()))
            {
                MainForm form = w.Form;
                restore.Invoke(form, new object[] { ViewPreferences.Load(file) }); Apply(form);
                Require(Listed(form).Count == 10 && form.ActiveFilterCount() == 0 && Get<FlatButton>(form, "filterButton").Text == "Filters", "a removed dropdown's saved value still filters");
            }
            using (Window w = Open(root, null, new Size(1232, 640), SearchFixture()))
            {
                MainForm form = w.Form; FilterPanel panel = Get<FilterPanel>(form, "filterPanel");
                restore.Invoke(form, new object[] { new ViewPreferences { Character = "Cammy" } });
                Apply(form);
                Require(panel.Visible && panel.Grid.Selected.SetEquals(new[] { "009" }) && panel.Kinds.Selected.Count == 0 && Names(form) == "Cammy Barefoot, Cammy Boots, Cammy Scripted", "2.2.0 Character not migrated (2.5.4: Filters open at every start): " + panel.Visible + " / " + Names(form));
            }
            using (Window w = Open(root, null, new Size(1232, 640), SearchFixture()))
            {
                MainForm form = w.Form; FilterPanel panel = Get<FilterPanel>(form, "filterPanel");
                restore.Invoke(form, new object[] { new ViewPreferences { FilterTab = "stages", Stages = new List<string> { "0800" } } });
                Apply(form);
                Require(panel.MiscOn && panel.MiscSelected.SetEquals(new[] { "Stage" }) && Names(form) == "Lighting Overhaul, Ranger Hut Night", "a tab setting of the 2.3.0 test builds did not migrate: " + Names(form));
                restore.Invoke(form, new object[] { new ViewPreferences { FilterTab = "tools", MiscAreas = new List<string> { "UI" } } });
                Apply(form);
                Require(panel.ToolsOn && !panel.MiscOn && panel.MiscSelected.Count == 0 && Names(form) == "Cammy Scripted, Frame Meter, Hitbox Viewer", "tools tab migration: " + Names(form));
            }
        }    }
}
