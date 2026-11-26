using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace SlotsInstaller
{
    // 1.0.15 (the user, from mockups): the three tours (Tours.cs). The Mod library tour runs once after the welcome (or
    // at the next start for someone who saw the welcome before 1.0.15) and opens the top mod first, so its options are
    // there to point at; it waits while the library is empty. The Installed and Settings tours run the first time each
    // page opens. Gear → Take the tour runs any of them again. ViewPreferences.ToursSeen remembers which ran.
    internal sealed partial class MainForm
    {
        internal const string LibraryTourId = "library", InstalledTourId = "installed", SettingsTourId = "settings";
        private System.Windows.Forms.Timer tourWait;

        private bool TourSeen(string id) { return viewPreferences != null && viewPreferences.ToursSeen != null && viewPreferences.ToursSeen.Contains(id); }
        private bool AutoToursAllowed { get { return rememberView && viewPreferences != null && !Program.Headless && screenshotFile == null; } }

        // The control's bounds in this window's client coordinates; Empty while it (or a parent) is hidden.
        private Rectangle TourRect(Control c)
        {
            if (c == null || !c.Visible || c.Width <= 0 || c.Height <= 0) return Rectangle.Empty;
            return RectangleToClient(c.RectangleToScreen(c.ClientRectangle));
        }
        private Rectangle TourUnion(params Control[] controls)
        {
            Rectangle all = Rectangle.Empty;
            foreach (Control c in controls) { Rectangle r = TourRect(c); if (!r.IsEmpty) all = all.IsEmpty ? r : Rectangle.Union(all, r); }
            return all;
        }
        private static Control TourCaption(Control sibling, string text)
        {
            return sibling == null || sibling.Parent == null ? null : sibling.Parent.Controls.OfType<Label>().FirstOrDefault(l => String.Equals(l.Text, text, StringComparison.OrdinalIgnoreCase));
        }
        // The visible sibling right below `c` (a docked button row under its note).
        private static Control TourBelow(Control c)
        {
            return c == null || c.Parent == null ? null : c.Parent.Controls.Cast<Control>().Where(o => o != c && o.Visible && o.Top >= c.Bottom - 1).OrderBy(o => o.Top).FirstOrDefault();
        }

        // ---- Starting a tour

        internal void StartTour(string id, bool auto)
        {
            if (Program.Headless) return;
            if (auto && (!AutoToursAllowed || TourSeen(id))) return;
            if (id == LibraryTourId) { StartLibraryTour(auto); return; }
            if (id == InstalledTourId && currentPage != installedPage) { ShowPage(installedPage); RefreshInstalled(); }
            if (id == SettingsTourId && currentPage != settingsPage) RunShortcut(ShortcutAction.SettingsPage);
            WhenReady(delegate { return !busy && Form.ActiveForm == this; }, delegate { RunTour(id == InstalledTourId ? InstalledTour() : SettingsTour()); }, 20, null);
        }

        // The Mod library tour needs a mod open: the top row of the list is opened when none is.
        private void StartLibraryTour(bool auto)
        {
            if (currentPage != libraryPage) ShowPage(libraryPage);
            Action noMods = auto ? (Action)null : delegate { SetPageStatus(libraryPage, "Add a mod library first: the tour shows a mod's options. Gear → Settings → Mod libraries."); };
            WhenReady(delegate { return !busy && modList.Items.Count > 0 && currentPage == libraryPage && Form.ActiveForm == this; }, delegate
            {
                if (package == null && modList.Items.Count > 0)
                {
                    userPickingMod = true;
                    try { ListViewItem top = modList.Items[0]; top.Selected = true; top.Focused = true; top.EnsureVisible(); }
                    finally { userPickingMod = false; }
                }
                WhenReady(delegate { return package != null && !busy && componentTree.Nodes.Count > 0; }, delegate { RunTour(LibraryTour()); }, 20, noMods);
            }, auto ? 180 : 15, noMods);
        }

        // Polls `ready` (a library scan or a mod being read finishes first); gives up after `seconds`.
        private void WhenReady(Func<bool> ready, Action run, int seconds, Action gaveUp)
        {
            if (tourWait != null) { tourWait.Stop(); tourWait.Dispose(); tourWait = null; }
            if (ready()) { run(); return; }
            DateTime deadline = DateTime.UtcNow.AddSeconds(seconds);
            var timer = new System.Windows.Forms.Timer { Interval = 300 };
            tourWait = timer;
            timer.Tick += delegate
            {
                if (IsDisposed) { timer.Stop(); return; }
                bool done = ready();
                if (!done && DateTime.UtcNow < deadline) return;
                timer.Stop(); timer.Dispose(); if (tourWait == timer) tourWait = null;
                if (done) run(); else if (gaveUp != null) gaveUp();
            };
            timer.Start();
        }

        private void RunTour(Tour tour)
        {
            if (viewPreferences != null)
            {
                if (viewPreferences.ToursSeen == null) viewPreferences.ToursSeen = new List<string>();
                if (!viewPreferences.ToursSeen.Contains(tour.Id)) { viewPreferences.ToursSeen.Add(tour.Id); SaveView(); }   // marked first, like the welcome
            }
            TourForm.Run(this, tour);
        }

        // Gear → Take the tour.
        private ToolStripMenuItem BuildTourMenu()
        {
            var item = new ToolStripMenuItem("Take the tour");
            var menu = (ToolStripDropDownMenu)item.DropDown;
            menu.ShowImageMargin = false; menu.BackColor = Palette.Surface; menu.ForeColor = Palette.Text; menu.Font = Palette.Normal;
            menu.Renderer = new ToolStripProfessionalRenderer(new DarkMenuColors());
            item.DropDownItems.Add("Mod library", null, delegate { StartTour(LibraryTourId, false); });
            item.DropDownItems.Add("Installed page", null, delegate { StartTour(InstalledTourId, false); });
            item.DropDownItems.Add("Settings", null, delegate { StartTour(SettingsTourId, false); });
            RoomyMenu(menu);
            return item;
        }

        // ---- The tours. The copy is the user's approved mockup text.

        private void ShowLibraryForTour()
        {
            if (currentPage != libraryPage) ShowPage(libraryPage);
            if (FiltersOpen) ShowFilters(false, false);
            ShowDetailTab(false);
        }

        internal Tour LibraryTour()
        {
            // Every card sits on the preview picture, so the list, the options and the fields stay readable.
            Func<int> width = delegate { return Math.Min(Px(440), TourRect(preview.Parent).Width - Px(12)); };
            Func<Rectangle, Size, Point> overPreview = delegate(Rectangle hole, Size size) { Rectangle p = TourRect(preview.Parent); return new Point(p.Left + Px(6), p.Top + Px(14)); };
            Func<string, string[], Func<Rectangle>, TourStep> step = delegate(string title, string[] body, Func<Rectangle> target) { return new TourStep { Title = title, Body = body, Prepare = ShowLibraryForTour, Target = target, Width = width, Place = overPreview }; };
            var tour = new Tour { Id = LibraryTourId, Name = "Mod library" };
            // 26 Nov 2026 (the user: "update tour guides appropriately and shorten them"): five steps, the one-click setup,
            // and that INSTALL AS is picked per mod (some people thought it was a mode for the whole app).
            tour.Steps.Add(step("Your mod library", new[] { "Every mod in your mod folders. Click one to open it. Ctrl+F searches by name, fighter or costume." }, delegate { return TourRect(librarySplit.Panel1); }));
            tour.Steps.Add(step("Pick the options", new[] { "Tick what you want. A \"Choose one\" group takes one pick. Hover an option to preview it." }, delegate { return TourRect(choicesHost); }));
            tour.Steps.Add(step("Install as", new[] { "You choose this for each mod, not once for the app.", "- Extra outfit adds a new costume slot; the original stays.", "- Replace original swaps the game's own files; Uninstall puts them back." }, delegate { return TourRect(modePanel); }));
            tour.Steps.Add(step("One click to set up", new[] { "Extra outfits need REFramework and Slotsloader. If this line says one is missing, click it: everything missing installs at once." },
                delegate { return !IsReplaceMode && !String.IsNullOrEmpty(modeNotice.Text) ? TourRect(modeNotice) : TourRect(modePanel); }));
            tour.Steps.Add(step("Installed and Help", new[] { "Your installs are on the Installed page, with Undo.", "Help: the gear or F1. This tour is there too." }, delegate { return TourRect(navInstalled); }));
            return tour;
        }

        private static bool TourElsewhere(ListViewItem row) { var game = row.Tag as GameModRow; return game != null && (game.Kind == "Fluffy" || game.Kind == "Leftover"); }
        private Rectangle TourRows(IEnumerable<ListViewItem> rows)
        {
            Rectangle list = TourRect(installedList), all = Rectangle.Empty;
            foreach (ListViewItem row in rows)
            {
                Rectangle r = RectangleToClient(installedList.RectangleToScreen(installedList.GetItemRect(row.Index)));
                r.Intersect(list); if (r.IsEmpty) continue;
                all = all.IsEmpty ? r : Rectangle.Union(all, r);
            }
            return all;
        }
        private void SelectRowForTour()
        {
            if (installedList.SelectedItems.Count == 1 && !TourElsewhere(installedList.SelectedItems[0])) return;
            List<ListViewItem> rows = installedList.Items.Cast<ListViewItem>().Where(r => !TourElsewhere(r)).ToList();
            ListViewItem pick = rows.FirstOrDefault(r => r.Tag is GameModRow && ((GameModRow)r.Tag).Mod != null) ?? rows.FirstOrDefault();
            if (pick == null) return;
            foreach (ListViewItem other in installedList.SelectedItems.Cast<ListViewItem>().ToList()) if (other != pick) other.Selected = false;
            pick.Selected = true; pick.Focused = true; pick.EnsureVisible();
        }

        internal Tour InstalledTour()
        {
            // Cards sit in the list under its rows (the empty part of the list), so rows and details stay readable.
            Func<int> width = delegate { return Math.Min(Px(640), TourRect(installedList).Width - Px(24)); };
            Func<Rectangle, Size, Point> inList = delegate(Rectangle hole, Size size)
            {
                Rectangle list = TourRect(installedList);
                Rectangle rows = TourRows(installedList.Items.Cast<ListViewItem>());
                int top = rows.IsEmpty ? list.Top + Px(12) : rows.Bottom + Px(12);
                return new Point(list.Left + Px(12), Math.Min(top, list.Bottom - size.Height - Px(12)));
            };
            Action show = delegate { if (currentPage != installedPage) ShowPage(installedPage); };
            Func<string, string[], Action, Func<Rectangle>, TourStep> step = delegate(string title, string[] body, Action prepare, Func<Rectangle> target) { return new TourStep { Title = title, Body = body, Prepare = prepare, Target = target, Width = width, Place = inList }; };
            var tour = new Tour { Id = InstalledTourId, Name = "Installed page" };
            // 26 Nov 2026: shorter, five steps (After an SF6 update joined Refresh, History, Presets).
            tour.Steps.Add(step("Everything you installed", new[] { "Extra: your extra outfits. Installed: replacements and tool mods. All: both. Search or pick a character to narrow it." }, show,
                delegate { return TourUnion(installedSearch, installedKind); }));
            tour.Steps.Add(step("The order", new[] { "When mods change the same file, the higher one wins. #1 is on top; what you install last goes there." }, show,
                delegate { var rows = installedList.Items.Cast<ListViewItem>().Where(r => !TourElsewhere(r)).Take(3).ToList(); return rows.Count == 0 ? TourRect(installedList) : TourRows(rows); }));
            tour.Steps.Add(step("Rows from elsewhere", new[] { "- Leftover PAK: a patch PAK no mod manager lists. Remove PAK takes it out, with Undo.", "- External mod manager rows: uninstall those there." }, show,
                delegate { return TourRows(installedList.Items.Cast<ListViewItem>().Where(TourElsewhere).Take(3)); }));
            tour.Steps.Add(step("Change what's installed", new[] { "Health, Disable, Uninstall, and Move up / down to change who wins. Undo shows in the status bar." },
                delegate { show(); SelectRowForTour(); },
                delegate { return TourUnion(openInstalledButton, editInstalledButton, toggleInstalledButton, disableInstalledButton, removeInstalledButton, moveUpButton, moveDownButton); }));
            tour.Steps.Add(step("Updates, History, Presets", new[] { "- After an SF6 update, click Reapply in the banner to put replacements back.", "- History lists every change. Presets save your setup for later." }, show,
                delegate { return gameBanner.Visible ? TourUnion(gameBanner, refreshInstalledButton, historyButton, presetsButton) : TourUnion(refreshInstalledButton, historyButton, presetsButton); }));
            return tour;
        }

        internal Tour SettingsTour()
        {
            Action general = delegate { if (currentPage != settingsPage) RunShortcut(ShortcutAction.SettingsPage); ShowSettingsTab(false); };
            Action replace = delegate { if (currentPage != settingsPage) RunShortcut(ShortcutAction.SettingsPage); ShowSettingsTab(true); };
            var layout = (TableLayoutPanel)settingsGame.Parent;
            Func<int, int, Control> cell = delegate(int column, int row) { return layout.GetControlFromPosition(column, row); };
            // Beside a General section: over the Assistant column on its right.
            Func<Func<Rectangle>, Func<int>> besideWidth = delegate(Func<Rectangle> target) { return delegate { return Math.Min(Px(456), ClientSize.Width - target().Right - Px(36)); }; };
            Func<Rectangle, Size, Point> right = delegate(Rectangle hole, Size size) { return new Point(hole.Right + Px(16), hole.Top); };
            // Left-column sections on the Game files tab: over the Replacement store (no step of its own).
            Func<Control> store = delegate { return TourCaption(settingsStore.Parent, "Replacement store"); };
            Func<int> storeWidth = delegate { Rectangle s = TourRect(store()); return Math.Min(Px(566), ClientSize.Width - s.Left - Px(12)); };
            Func<Rectangle, Size, Point> overStore = delegate(Rectangle hole, Size size) { Rectangle s = TourRect(store()); return new Point(s.Left - Px(4), s.Top - Px(8)); };
            Func<Rectangle> gameFolder = delegate { return TourUnion(cell(0, 0), settingsGame, cell(1, 1), cell(0, 2)); };
            Func<Rectangle> libraries = delegate { return TourUnion(cell(0, 8), cell(0, 9), cell(0, 10), cell(1, 10)); };
            Func<Rectangle> gameFiles = delegate { return TourUnion(TourCaption(settingsGameFiles, "Game files"), settingsGameFiles, TourBelow(settingsGameFiles)); };
            Func<Rectangle> external = delegate { return TourUnion(TourCaption(settingsFluffyStatus, "External mod manager"), settingsFluffyStatus, TourBelow(settingsFluffyStatus)); };
            var tour = new Tour { Id = SettingsTourId, Name = "Settings" };
            // 26 Nov 2026: shorter, six steps (no Replacement method: Automatic suits nearly everyone); the tools step
            // names the one-click button.
            tour.Steps.Add(new TourStep { Title = "Game folder", Body = new[] { "Where SF6 is installed, found through Steam. Use Browse if yours is somewhere else." }, Prepare = general, Target = gameFolder, Width = besideWidth(gameFolder), Place = right });
            tour.Steps.Add(new TourStep { Title = "Mod libraries", Body = new[] { "The folders where you keep downloaded mods, your other mod manager's too. Click Save settings after a change." }, Prepare = general, Target = libraries, Width = besideWidth(libraries), Place = right });
            tour.Steps.Add(new TourStep { Title = "REFramework, Slotsloader, Native Core", Body = new[] { "Extra outfits need the first two; stage pictures need Native Core. One button installs whatever is missing." }, Prepare = replace,
                Target = delegate { return TourUnion(TourCaption(settingsToolStatus, "REFramework and Slotsloader"), settingsToolStatus, installReFrameworkButton.Parent); }, Width = storeWidth, Place = overStore });
            tour.Steps.Add(new TourStep { Title = "Game files", Body = new[] { "- Reapply all puts replacements back after an SF6 update.", "- Restore original game files takes this Manager's replacements out, with Undo." }, Prepare = replace,
                Target = gameFiles, Width = delegate { return Math.Min(Px(560), gameFiles().Left - Px(32)); }, Place = delegate(Rectangle hole, Size size) { return new Point(hole.Left - Px(16) - size.Width, hole.Top); } });
            tour.Steps.Add(new TourStep { Title = "External mod manager", Body = new[] { "While your other mod manager has mods installed, Replace original stays off here. Migrate installed mods moves them over." }, Prepare = replace,
                Target = external, Width = delegate { return external().Width + Px(12); }, Place = delegate(Rectangle hole, Size size) { return new Point(hole.Left, hole.Top - Px(16) - size.Height); } });
            tour.Steps.Add(new TourStep { Title = "Slotsloader and updates", Body = new[] { "Updates for the Manager and Slotsloader, and what Slotsloader adds to the game's menus." }, Prepare = replace,
                Target = delegate { return TourRect(settingsLoaderTab); }, Width = delegate { return Px(430); }, Place = delegate(Rectangle hole, Size size) { return new Point(hole.Left, hole.Bottom + Px(16)); } });
            return tour;
        }
    }
}
