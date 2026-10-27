using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

namespace SlotsInstaller
{
    // 1.0.8 MY FILTERS (LibraryFilters.cs, MainFormCustomFilters.cs, CustomFilterEditor.cs), part of --filter-check.
    internal static partial class FilterChecks
    {
        private static List<LibraryEntry> CustomFixture()
        {
            List<LibraryEntry> lib = SearchFixture();
            Func<string, string, LibraryEntry> e = delegate(string name, string character)
            {
                var x = new LibraryEntry { Name = name, Kind = "ZIP", OptionCount = 1, SourcePath = @"C:\fixture\filters\" + name + ".zip", Character = character };
                string code = Characters.ByCode.First(kv => kv.Value == character).Key;
                x.CharacterCodes = new List<string> { code }; x.CostumeKeys = new List<string> { code + "/001" };
                return x;
            };
            LibraryEntry nude = e("Cammy Nude C1", "Cammy");                                     // name
            LibraryEntry bundle = e("Juri Swim Bundle", "Juri"); bundle.OptionCount = 2;             // option name
            bundle.OptionNames = new List<string> { "Bikini", "Topless" }; bundle.OptionIds = new List<string> { "Juri Swim Bundle/01 Bikini", "Juri Swim Bundle/02 Top" };
            LibraryEntry folder = e("C1 Mai Pack", "Mai"); folder.OptionCount = 2;                   // option folder, spaced keyword
            folder.OptionIds = new List<string> { "C1 Mai Pack/Mai See-Through Dress", "C1 Mai Pack/Mai Dress" };
            LibraryEntry typed = e("Manon Edit", "Manon"); typed.CustomName = "Manon NSFW edit";      // typed name (Name view)
            LibraryEntry hat = e("Hatless Cammy", "Cammy");                                          // no keyword ("tless" isn't one)
            LibraryEntry panty = e("Mai Panty Pack", "Mai"); panty.OptionCount = 1; panty.OptionNames = new List<string> { "Normal panties" };   // not "no panties"
            lib.AddRange(new[] { nude, bundle, folder, typed, hat, panty });
            return lib;
        }

        private static void CheckCustomFilters(string root, Fixture game, List<string> report)
        {
            // ---- Store and matching ----
            if (File.Exists(LibraryFilters.FilePath)) File.Delete(LibraryFilters.FilePath);
            LibraryFilters.Reload();
            List<CustomFilter> defaults = LibraryFilters.All();
            Require(defaults.Count == 1 && defaults[0].Id == LibraryFilters.NsfwId && defaults[0].Name == "NSFW" && defaults[0].Keywords.SequenceEqual(LibraryFilters.NsfwKeywords) && !File.Exists(LibraryFilters.FilePath), "default NSFW filter without a file");
            List<string> parsed = LibraryFilters.ParseKeywords("nude, Topless\nsee  through;;NUDE, , -\r\n\"x\" ，see-through");
            Require(parsed.SequenceEqual(new[] { "nude", "Topless", "see through", "x" }), "keywords: " + String.Join(" | ", parsed));
            List<LibraryEntry> lib = CustomFixture();
            CustomFilter nsfw = defaults[0];
            Func<string, bool> hit = delegate(string name) { return LibraryFilters.Matches(lib.Single(x => x.Name == name), nsfw); };
            Require(hit("Cammy Nude C1") && hit("Juri Swim Bundle") && hit("C1 Mai Pack") && hit("Manon Edit") && !hit("Hatless Cammy") && !hit("Mai Panty Pack") && !hit("Cammy Barefoot"), "NSFW matches: " + String.Join(", ", lib.Where(x => LibraryFilters.Matches(x, nsfw)).Select(x => x.Name)));
            // Short keywords start a word; the cache follows name changes.
            Require(LibraryFilters.Matches(lib.Single(x => x.Name == "Juri Jacket"), new List<string> { "ja" }) && !LibraryFilters.Matches(lib.Single(x => x.Name == "Juri Jacket"), new List<string> { "ck" }), "two-letter keywords start a word");
            LibraryEntry renamed = lib.Single(x => x.Name == "Cammy Boots");
            Require(!LibraryFilters.Matches(renamed, nsfw), "Cammy Boots matched NSFW");
            renamed.CustomName = "Cammy Boots Topless";
            Require(LibraryFilters.Matches(renamed, nsfw), "a typed name did not refresh the cached match");
            renamed.CustomName = null;

            // ---- Window: chips, states, counts ----
            MethodInfo capture = typeof(MainForm).GetMethod("CaptureView", PrivateInstance), restore = typeof(MainForm).GetMethod("RestoreView", PrivateInstance);
            Func<MainForm, string, object[], object> call = delegate(MainForm f, string name, object[] args) { return typeof(MainForm).GetMethod(name, PrivateInstance).Invoke(f, args); };
            using (Window w = Open(root, null, new Size(1460, 920), lib))
            {
                MainForm form = w.Form; FilterPanel panel = Get<FilterPanel>(form, "filterPanel");
                // 1.0.15: the library starts at 330, where the grid has 7 fighters per row and MY FILTERS scrolls into
                // view (accepted by the user); the chip place is checked with the 250 library, 8 per row as before.
                SplitContainer librarySplit = Get<SplitContainer>(form, "librarySplit"); librarySplit.SplitterDistance = DpiMetrics.Px(form, MainForm.OldDefaultLibraryWidth);
                form.ShowFilters(true, false); Settle(w.Host);
                FlatButton button = Get<FlatButton>(form, "filterButton");
                Func<string, FlatButton> chip = delegate(string id) { return panel.CustomChips.Controls.OfType<FlatButton>().Single(b => (string)b.Tag == id); };
                List<FlatButton> chips = panel.CustomChips.Controls.OfType<FlatButton>().ToList();
                Require(chips.Count == 2 && chips[0].Text == "NSFW 4" && chips[1].Text == "+ New filter" && panel.CustomTitle.Text == "MY FILTERS", "chips: " + String.Join(", ", chips.Select(c => c.Text)));
                Require(chips.All(c => c.Visible && c.Height == DpiMetrics.Px(panel, 26)) && In(chips[1], w.Host).Bottom <= In(panel, w.Host).Bottom, "chip size or place: " + String.Join(", ", chips.Select(c => c.Size.ToString())));
                int all = Listed(form).Count;
                chip("nsfw").PerformClick();
                Require(Names(form) == "C1 Mai Pack, Cammy Nude C1, Juri Swim Bundle, Manon Edit" && chip("nsfw").Active && button.Text == "Filters 1" && form.CustomState("nsfw") == CustomFilterState.Only, "Only: " + Names(form));
                chip("nsfw").PerformClick();
                Require(Listed(form).Count == all - 4 && !Listed(form).Contains("Cammy Nude C1") && chip("nsfw").Text == "Hide NSFW 4" && chip("nsfw").Danger && !chip("nsfw").Active && button.Text == "Filters 1", "Hide: " + Names(form) + " / " + chip("nsfw").Text);
                // AND-ed with fighters; the chip counts within them; the fighter cards count without the hidden mods.
                panel.Grid.SetSelected(new[] { "009" }, true);
                Require(Names(form) == "Cammy Barefoot, Cammy Boots, Cammy Scripted, Hatless Cammy" && chip("nsfw").Text == "Hide NSFW 1" && panel.Grid.Cards.Single(c => c.Code == "016").Count == 1 && button.Text == "Filters 2", "Hide AND Cammy: " + Names(form) + " / " + chip("nsfw").Text);
                ViewPreferences saved = (ViewPreferences)capture.Invoke(form, null);
                Require(saved.FiltersHidden.SequenceEqual(new[] { "nsfw" }) && saved.FiltersOnly.Count == 0, "saved states: hidden " + String.Join(",", saved.FiltersHidden));
                Get<FlatButton>(form, "filterClearButton").PerformClick();
                Require(form.CustomState("nsfw") == CustomFilterState.Off && Listed(form).Count == all && chip("nsfw").Text == "NSFW 4" && button.Text == "Filters", "Clear kept a MY FILTERS chip on");
                restore.Invoke(form, new object[] { new ViewPreferences { FiltersOnly = new List<string> { "nsfw", "gone" } } }); Apply(form);
                Require(form.CustomState("nsfw") == CustomFilterState.Only && Listed(form).Count == 4, "restored Only state");
                // The chip menu: the other states, then Edit.
                ContextMenuStrip menu = Get<ContextMenuStrip>(form, "customMenu");
                Require((bool)call(form, "FillCustomMenu", new object[] { chip("nsfw") }) && String.Join("|", menu.Items.Cast<ToolStripItem>().Select(i => i is ToolStripSeparator ? "-" : i.Text)) == "Hide these|Turn off|-|Edit filter", "menu: " + String.Join("|", menu.Items.Cast<ToolStripItem>().Select(i => i.Text)));
                menu.Items[1].PerformClick();
                Require(form.CustomState("nsfw") == CustomFilterState.Off && Listed(form).Count == all, "menu Turn off");

                // ---- Editor: new filter, live preview, save ----
                chips[1].PerformClick(); Settle(w.Host);
                CustomFilterEditor editor = panel.Editor;
                Require(form.CustomEditorOpen && editor.Visible && !editor.DeleteButton.Visible && editor.Height > DpiMetrics.Px(panel, 150) && In(editor, w.Host).Width > DpiMetrics.Px(panel, 300), "editor not shown: " + editor.Size);
                Require(In(editor.SaveButton, w.Host).Right <= In(panel, w.Host).Right && editor.Controls.Cast<Control>().Where(c => c.Visible).All(c => c.Right <= editor.Width && c.Bottom <= editor.Height), "editor contents outside the editor");
                editor.NameField.Text = "Barefoot"; editor.KeywordsBox.Text = "barefoot\r\nhatless";
                call(form, "ApplyCustomDraft", null);
                Require(Names(form) == "Cammy Barefoot, Hatless Cammy" && editor.Status.Text == "2 mods listed", "preview: " + Names(form) + " / " + editor.Status.Text);
                ClosePanelCheck(form, panel);
                using (var bmp = new Bitmap(panel.Width, panel.Height)) { panel.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height)); bmp.Save(Path.Combine(root, "custom-filter-editor.png"), ImageFormat.Png); }
                // Keys in the editor: Tab moves between its fields (INSTALL AS stays), Esc cancels without closing the panel.
                form.FocusOverride = editor.NameField.Editor;
                bool replaceBefore = form.IsReplaceMode;
                Require((bool)call(form, "CustomFilterKey", new object[] { Keys.Tab }) && form.IsReplaceMode == replaceBefore, "Tab in the editor");
                editor.SaveButton.PerformClick(); Settle(w.Host);
                form.FocusOverride = null;
                LibraryFilters.Reload();
                List<CustomFilter> stored = LibraryFilters.All();
                Require(stored.Count == 2 && stored[1].Name == "Barefoot" && stored[1].Keywords.SequenceEqual(new[] { "barefoot", "hatless" }) && File.Exists(LibraryFilters.FilePath), "saved filters: " + String.Join(", ", stored.Select(f => f.Name)));
                string barefoot = stored[1].Id;
                Require(!form.CustomEditorOpen && !editor.Visible && Listed(form).Count == all && chip(barefoot).Text == "Barefoot 2" && form.CustomState(barefoot) == CustomFilterState.Off, "after save: " + Listed(form).Count + " / " + chip(barefoot).Text);
                // Both Only chips are OR-ed.
                chip(barefoot).PerformClick(); chip("nsfw").PerformClick();
                Require(Listed(form).Count == 6 && button.Text == "Filters 2", "two Only chips: " + Names(form));
                Get<FlatButton>(form, "filterClearButton").PerformClick();

                // ---- Editor: rename refused on a clash, Esc cancels, edit keeps state, delete ----
                form.BeginCustomFilterEdit(barefoot); Settle(w.Host);
                Require(editor.DeleteButton.Visible && editor.NameField.Text == "Barefoot" && editor.KeywordsBox.Text == "barefoot, hatless", "edit fields: " + editor.KeywordsBox.Text);
                editor.NameField.Text = "nsfw"; editor.SaveButton.PerformClick();
                Require(form.CustomEditorOpen && editor.Status.Text == "A filter named nsfw exists." && editor.Status.ForeColor == Palette.Red, "name clash: " + editor.Status.Text);
                form.FocusOverride = editor.KeywordsBox;
                Require((bool)call(form, "CustomFilterKey", new object[] { Keys.Escape }) && !form.CustomEditorOpen && form.FiltersOpen, "Esc in the editor");
                form.FocusOverride = null;
                LibraryFilters.Reload();
                Require(LibraryFilters.All()[1].Name == "Barefoot", "a cancelled edit was saved");
                form.SetCustomState(barefoot, CustomFilterState.Hide);
                form.BeginCustomFilterEdit(barefoot); editor.KeywordsBox.Text = "barefoot"; editor.SaveButton.PerformClick();
                Require(form.CustomState(barefoot) == CustomFilterState.Hide && chip(barefoot).Text == "Hide Barefoot 1", "edit kept the state: " + chip(barefoot).Text);
                form.BeginCustomFilterEdit(barefoot); editor.DeleteButton.PerformClick(); Settle(w.Host);
                LibraryFilters.Reload();
                Require(LibraryFilters.All().Count == 1 && panel.CustomChips.Controls.OfType<FlatButton>().Count() == 2 && form.CustomState(barefoot) == CustomFilterState.Off && Listed(form).Count == all, "delete");
                // Deleting every filter is remembered (the default does not come back).
                form.BeginCustomFilterEdit("nsfw"); editor.DeleteButton.PerformClick();
                LibraryFilters.Reload();
                Require(LibraryFilters.All().Count == 0 && panel.CustomChips.Controls.OfType<FlatButton>().Count() == 1, "deleting NSFW: " + LibraryFilters.All().Count);
                // Closing the panel cancels an edit and its preview.
                form.BeginCustomFilterEdit(""); editor.KeywordsBox.Text = "jacket"; call(form, "ApplyCustomDraft", null);
                Require(Names(form) == "Juri Jacket", "preview before closing");
                form.ShowFilters(false, false);
                Require(!form.CustomEditorOpen && Listed(form).Count == all, "closing the panel kept the preview");
            }
            File.Delete(LibraryFilters.FilePath); LibraryFilters.Reload();
            report.Add("  NSFW keywords: " + LibraryFilters.KeywordText(LibraryFilters.NsfwKeywords));
        }
        // A mod picked in the list keeps the panel (and the editor) open while a filter is edited.
        private static void ClosePanelCheck(MainForm form, FilterPanel panel)
        {
            typeof(MainForm).GetMethod("ClosePanelForPickedMod", PrivateInstance).Invoke(form, null);
            Require(form.FiltersOpen && form.CustomEditorOpen && panel.Editor.Visible, "picking a mod closed the editor");
        }
    }
}
