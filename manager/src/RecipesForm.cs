using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace SlotsInstaller
{
    // 1.0.18 (user request): outfit recipes on the Installed page (OutfitRecipes.cs). "Outfit recipes" lists the
    // saved recipes; Install makes a new extra outfit from the selected one, Delete removes the recipe (never an
    // installed outfit), and "Save <outfit> as recipe" writes down the selected installed outfit. The Added parts
    // window saves its list the same way. An outfit that matches a saved recipe is tagged on its row and found by the
    // search words "recipe", "recipes", "rc", "outfits" and "ou".
    internal sealed partial class MainForm
    {
        private FlatButton recipesButton, recipesIconButton;
        private List<OutfitRecipe> recipeCache;
        internal const string RecipeSearchWords = " recipe recipes rc outfits";
        private List<OutfitRecipe> Recipes()
        {
            if (recipeCache == null) { try { recipeCache = OutfitRecipes.List(AppConfig.DataDir); } catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); recipeCache = new List<OutfitRecipe>(); } }
            return recipeCache;
        }
        private void RecipesChanged()
        {
            recipeCache = null; recipeEntries.Clear(); InvalidateInstalledSearch(); if (installedList != null) { FillInstalled(); installedList.Invalidate(); }
            if (openRecipe != null && package == null && !Recipes().Any(r => r.Id == openRecipe.Id)) ClosePackage();   // the open recipe was deleted
            if (modList != null && search != null) ApplyFilters();   // their rows are part of the mod list
        }

        // 1.0.18 (the user: "if i search using recipe keywords then the recipes should take priority over other
        // searches"): the words rc, recipe(s), ou and outfit(s) mean "recipes" and are not searched for themselves.
        private List<OutfitRecipe> recipeHitList = new List<OutfitRecipe>();
        internal static readonly string[] RecipeWords = { "rc", "recipe", "recipes", "ou", "outfit", "outfits" };
        // 1.0.18 (the user, later: recipes "should also appear in library normally without typing rc"): every recipe is
        // listed, narrowed by the search words (name, fighter and contents) and by the fighters ticked in the filters.
        internal static List<OutfitRecipe> RecipeMatches(IEnumerable<OutfitRecipe> recipes, string text, ICollection<string> fighters = null)
        {
            var words = (text ?? "").Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(w => w.Trim()).Where(w => w.Length > 0).ToList();
            var rest = words.Where(w => !RecipeWords.Contains(w, StringComparer.OrdinalIgnoreCase)).ToList();
            return (recipes ?? new OutfitRecipe[0]).Where(r => (fighters == null || fighters.Count == 0 || fighters.Contains(r.Character ?? "", StringComparer.OrdinalIgnoreCase))
                && rest.All(w => (r.Name + " " + r.Character + " " + r.Summary).IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
        }
        // 1.0.18 (the user, about recipes in their own strip above the list: "the recipes are stuck and pinned, and
        // they don't move as you scroll between entries"): recipes are rows of the mod list itself, first in it. They
        // scroll with it and are selected like a mod. A row is a LibraryEntry with its Recipe set and no source file,
        // so what works on mod files passes it by (the install queue, rename, delete, read-ahead); opening one goes
        // to OpenRecipe (LoadPackage), its right-click menu is the recipe's own, and it is drawn by DrawLibraryItem.
        // fighterCodes: the fighters ticked in the filters; off: a filter no recipe can pass is on (tool mods,
        // installed only).
        private readonly Dictionary<string, LibraryEntry> recipeEntries = new Dictionary<string, LibraryEntry>(StringComparer.Ordinal);
        private List<LibraryEntry> recipeRowsShown = new List<LibraryEntry>();
        private List<LibraryEntry> RecipeRows(IEnumerable<string> fighterCodes, bool off)
        {
            var fighters = new HashSet<string>((fighterCodes ?? new string[0]).Select(c => Characters.ByCode.ContainsKey(c) ? Characters.ByCode[c] : c), StringComparer.OrdinalIgnoreCase);
            List<OutfitRecipe> hits = off || search == null ? new List<OutfitRecipe>() : RecipeMatches(Recipes(), search.Text, fighters);
            var rows = new List<LibraryEntry>();
            foreach (OutfitRecipe r in hits)
            {
                LibraryEntry entry;
                if (!recipeEntries.TryGetValue(r.Id, out entry) || !ReferenceEquals(entry.Recipe, r))
                    recipeEntries[r.Id] = entry = new LibraryEntry { SourcePath = "", Name = r.Name, DisplayName = r.Name, Character = r.Character, Kind = "Recipe", Recipe = r };
                rows.Add(entry);
            }
            recipeHitList = hits; recipeRowsShown = rows;
            return rows;
        }
        internal int RecipeStripCountForTest { get { return recipeRowsShown.Count; } }
        // What a recipe row says under its name: the mods it is made of (their options are in the view on the right).
        internal static string RecipeRowText(OutfitRecipe r)
        {
            return "Recipe   ·   " + r.Character + "   ·   " + String.Join(" + ", new[] { r.Base.ModName }.Concat(r.Parts.Select(p => p.ModName)).Distinct(StringComparer.OrdinalIgnoreCase));
        }
        // The place the strip had in the layout stays (an empty panel); the recipe menu is made here.
        private Control BuildRecipeStrip()
        {
            recipeMenu = BuildRecipeMenu(); Disposed += delegate { recipeMenu.Dispose(); };
            return new Panel { Dock = DockStyle.Top, Height = 0, Visible = false, Name = "recipeStrip" };
        }
        private ContextMenuStrip recipeMenu;
        internal List<string> RecipeMenuForTest { get { return recipeMenu.Items.Cast<ToolStripItem>().Where(i => i.Text != "Uninstall" || InstalledRecipe(recipeMenu.Tag as OutfitRecipe).Any).Select(i => i is ToolStripSeparator ? "-" : i.Text + (i.Enabled ? "" : " (off)")).ToList(); } }
        // The user: "theres no context options on recipes in library, add appropriate ones on right click", and then
        // "The first two install options are not needed since you can install them from the main tab anyway."
        private ContextMenuStrip BuildRecipeMenu()
        {
            var menu = new ContextMenuStrip { BackColor = Palette.Surface, ForeColor = Palette.Text, ShowImageMargin = false, Font = Palette.Normal, Renderer = new ToolStripProfessionalRenderer(new DarkMenuColors()) };
            ToolStripItem edit = menu.Items.Add("Edit recipe", null, delegate { var r = menu.Tag as OutfitRecipe; if (r != null && !busy) EditRecipe(r); });
            menu.Items.Add("Open recipes window", null, delegate { var r = menu.Tag as OutfitRecipe; if (r != null && !busy) ShowRecipes(r.Name); });
            menu.Items.Add(new ToolStripSeparator());
            // Only while the recipe is installed (InstalledRecipe).
            ToolStripItem uninstall = menu.Items.Add("Uninstall", null, delegate { var r = menu.Tag as OutfitRecipe; if (r != null && !busy) UninstallRecipe(r); });
            uninstall.ForeColor = Palette.Red; uninstall.Visible = false;
            ToolStripItem delete = menu.Items.Add("Delete recipe", null, delegate { var r = menu.Tag as OutfitRecipe; if (r != null && !busy) DeleteRecipe(r); });
            delete.ForeColor = Palette.Red;
            menu.Opening += delegate(object sender, System.ComponentModel.CancelEventArgs e)
            {
                var r = menu.Tag as OutfitRecipe;
                if (r == null) { e.Cancel = true; return; }
                edit.Enabled = !busy; delete.Enabled = !busy;
                RecipeInstall now = InstalledRecipe(r);
                string why = now.Replacements.Count > 0 ? ReplacementUninstallBlocker(now.Replacements) : busy ? "busy" : null;
                uninstall.Visible = now.Any; uninstall.Enabled = why == null;
                uninstall.Text = "Uninstall" + (now.Replacements.Count > 0 ? ReplacementBlockerSuffix(now.Replacements) : "");
            };
            return menu;
        }
        // Right-click (or the menu key) on a recipe row of the mod list.
        internal void ShowRecipeMenuFor(OutfitRecipe recipe, Point screen)
        {
            if (recipe == null) return;
            recipeMenu.Tag = recipe;
            if (!Program.Headless) recipeMenu.Show(screen);
        }
        private ListViewItem RecipeRow(OutfitRecipe recipe)
        {
            if (recipe == null || modList == null) return null;
            foreach (ListViewItem item in modList.Items) { var e = item.Tag as LibraryEntry; if (e != null && e.Recipe != null && e.Recipe.Id == recipe.Id) return item; }
            return null;
        }
        // The open recipe's row is the list's selection (ClosePackage takes every selection off first).
        private void SelectRecipeRow(OutfitRecipe recipe)
        {
            ListViewItem row = RecipeRow(recipe); if (row == null) return;
            bool previous = selectingMod; selectingMod = true;
            try
            {
                foreach (ListViewItem other in modList.SelectedItems.Cast<ListViewItem>().ToList()) if (other != row) other.Selected = false;
                if (!row.Selected) row.Selected = true;
                row.Focused = true;
            }
            finally { selectingMod = previous; }
            lastSelectionCount = modList.SelectedItems.Count;
        }
        // 1.0.18 (the user: "installed recipes should show they are installed and similarly allow uninstallation too"):
        // what is installed from a recipe right now. As an extra outfit: the outfits whose install record is the
        // recipe (OutfitRecipes.Of). As replacements: one record per mod of the recipe, with that mod's options;
        // all of them, or it does not count. Nothing is stored for this: it is read from what is installed, so a
        // recipe installed before this existed shows too.
        internal sealed class RecipeInstall
        {
            internal List<InstalledOutfit> Outfits = new List<InstalledOutfit>();
            internal List<GameModRow> Replacements = new List<GameModRow>();
            internal bool Any { get { return Outfits.Count > 0 || Replacements.Count > 0; } }
            internal bool Enabled { get { return Outfits.Any(o => o.Enabled) || (Replacements.Count > 0 && Replacements.All(r => r.Mod.Enabled)); } }
            // 1.0.18: installed, but not as the recipe is ticked now (a layer or an option was turned on or off since):
            // Install reads "Update" and puts it in again as ticked. Stale: records of mods the recipe no longer installs.
            internal bool Changed;
            internal List<GameModRow> Stale = new List<GameModRow>();
            internal string Badge { get { return !Any ? "" : Changed ? "◐ Changed" : !Enabled ? "Disabled" : Outfits.Any(o => o.Enabled) ? "Extra" : "✓ Installed"; } }
        }
        internal RecipeInstall InstalledRecipe(OutfitRecipe recipe)
        {
            if (recipe == null || recipe.Base == null) return new RecipeInstall();
            OutfitRecipe now = recipe.Effective();
            RecipeInstall found = now == null ? new RecipeInstall() : InstalledExactly(now);
            List<string> all = recipe.AllLayers.Select(l => l.SourcePath ?? "").ToList();
            Func<string, IEnumerable<string>, bool> among = (source, list) => list.Any(s => ReplacePlanner.SameSource(s, source));
            List<string> on = now == null ? new List<string>() : now.AllLayers.Select(l => l.SourcePath ?? "").ToList();
            List<GameModRow> rows = (gameRows ?? new List<GameModRow>()).Where(r => r.Mod != null).ToList();
            if (found.Replacements.Count > 0)
            {
                // A mod that was turned off in the recipe and is still installed: not as ticked.
                found.Stale = rows.Where(r => among(r.Mod.SourcePath, all) && !among(r.Mod.SourcePath, on)).ToList();
                if (found.Stale.Count > 0) { found.Changed = true; found.Replacements.AddRange(found.Stale.Where(s => !found.Replacements.Contains(s))); }
            }
            if (found.Any) return found;
            // Not installed as ticked. Installed another way: an outfit of the recipe's name built from its mods, or a
            // record of every mod that is on (whatever its options).
            foreach (InstalledOutfit o in installed ?? new List<InstalledOutfit>())
                if (o.Managed && String.Equals(o.Character, recipe.Character, StringComparison.OrdinalIgnoreCase) && String.Equals(o.Name, recipe.Name, StringComparison.OrdinalIgnoreCase)
                    && among(o.SourcePath, all) && (o.Parts ?? new List<PartGraft>()).Where(g => g != null).All(g => among(g.SourcePath, all))) found.Outfits.Add(o);
            if (on.Count > 0 && on.All(s => rows.Any(r => ReplacePlanner.SameSource(r.Mod.SourcePath, s))))
            {
                found.Replacements = rows.Where(r => among(r.Mod.SourcePath, all)).OrderBy(r => r.Priority).ToList();
                found.Stale = found.Replacements.Where(r => !among(r.Mod.SourcePath, on)).ToList();
            }
            found.Changed = found.Any;
            return found;
        }
        // Installed exactly as this (already effective) recipe says.
        private RecipeInstall InstalledExactly(OutfitRecipe recipe)
        {
            var found = new RecipeInstall();
            // A layer saved without options is the mod as it installs by default: any options of that mod match it.
            Func<RecipeLayer, string, List<string>, bool> same = (layer, source, ids) => ReplacePlanner.SameSource(layer.SourcePath, source)
                && (layer.SelectedIds == null || layer.SelectedIds.Count == 0 || new HashSet<string>(layer.SelectedIds, StringComparer.OrdinalIgnoreCase).SetEquals(ids ?? new List<string>()));
            List<RecipeLayer> recipeParts = recipe.Parts ?? new List<RecipeLayer>();
            foreach (InstalledOutfit o in installed ?? new List<InstalledOutfit>())
            {
                if (!o.Managed || !String.Equals(o.Character, recipe.Character, StringComparison.OrdinalIgnoreCase)) continue;
                List<PartGraft> parts = (o.Parts ?? new List<PartGraft>()).Where(g => g != null).ToList();
                // The outfit is built on the lowest layer that brings a body, so its record may start at another layer
                // than the recipe's bottom one: the same mods with the same options, whatever the order.
                var layers = new[] { recipe.Base }.Concat(recipeParts).ToList();
                if (layers.Count != parts.Count + 1) continue;
                var have = new List<KeyValuePair<string, List<string>>> { new KeyValuePair<string, List<string>>(o.SourcePath, o.SelectedIds) };
                have.AddRange(parts.Select(g => new KeyValuePair<string, List<string>>(g.SourcePath, g.SelectedIds)));
                bool all = true;
                foreach (RecipeLayer layer in layers)
                {
                    int at = have.FindIndex(h => same(layer, h.Key, h.Value));
                    if (at < 0) { all = false; break; }
                    have.RemoveAt(at);
                }
                if (all) found.Outfits.Add(o);
            }
            var mods = new List<KeyValuePair<string, HashSet<string>>>();
            foreach (RecipeLayer layer in new[] { recipe.Base }.Concat(recipe.Parts ?? new List<RecipeLayer>()))
            {
                var mod = mods.FirstOrDefault(m => ReplacePlanner.SameSource(m.Key, layer.SourcePath));
                if (mod.Key == null) mods.Add(mod = new KeyValuePair<string, HashSet<string>>(layer.SourcePath ?? "", new HashSet<string>(StringComparer.OrdinalIgnoreCase)));
                mod.Value.UnionWith(layer.SelectedIds ?? new List<string>());
            }
            foreach (var mod in mods)
            {
                GameModRow row = (gameRows ?? new List<GameModRow>()).Where(r => r.Mod != null && ReplacePlanner.SameSource(r.Mod.SourcePath, mod.Key)
                    && mod.Value.All(id => (r.Mod.SelectedIds ?? new List<string>()).Contains(id, StringComparer.OrdinalIgnoreCase))).OrderBy(r => r.Priority).FirstOrDefault();
                if (row == null) { found.Replacements.Clear(); break; }
                found.Replacements.Add(row);
            }
            return found;
        }
        // Uninstall from the library: what the recipe installed, as extra outfits and as replacements.
        internal void UninstallRecipe(OutfitRecipe recipe)
        {
            if (busy || recipe == null) return;
            RecipeInstall now = InstalledRecipe(recipe);
            if (!now.Any) { SetPageStatus(currentPage, recipe.Name + " is not installed.", Palette.Accent); return; }
            UninstallSelection(now.Outfits, now.Replacements);
        }

        // Delete from the library: no question, Undo puts the recipe back (installed outfits are never touched).
        internal void DeleteRecipe(OutfitRecipe recipe)
        {
            if (busy || recipe == null) return;
            try { OutfitRecipes.Delete(AppConfig.DataDir, recipe.Id); }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); SetPageStatus(currentPage, "Couldn't delete the recipe: " + ex.Message, Palette.Red); return; }
            RecipesChanged();
            SetPageStatus(currentPage, "✓ Deleted the recipe " + recipe.Name + ". Installed outfits are not touched.", Palette.Green);
            OfferUndo("Delete the recipe " + recipe.Name, delegate
            {
                recipe.Id = "";
                OutfitRecipes.Save(AppConfig.DataDir, recipe); RecipesChanged();
                SetPageStatus(currentPage, "✓ The recipe " + recipe.Name + " is back.", Palette.Green);
            });
        }
        // Edit: the builder opens with the recipe's layers; saving replaces the recipe (also under a new name).
        internal void EditRecipe(OutfitRecipe recipe) { EditRecipe(recipe, null); }
        // 1.0.18 (the user: "add an option to add to existing recipe too"): the Mod library's right-click on a mod
        // lists the saved recipes of its fighter under "Add to recipe"; the builder opens with that recipe and the
        // mod on top of it, and saving replaces the recipe as Edit recipe does.
        internal List<OutfitRecipe> RecipesFor(LibraryEntry entry)
        {
            if (entry == null || entry.Recipe != null || String.IsNullOrEmpty(entry.SourcePath)) return new List<OutfitRecipe>();
            var names = new HashSet<string>((entry.CharacterCodes ?? new List<string>()).Where(c => Characters.ByCode.ContainsKey(c)).Select(c => Characters.ByCode[c]), StringComparer.OrdinalIgnoreCase);
            if (names.Count == 0 && !String.IsNullOrEmpty(entry.Character)) names.Add(entry.Character);
            return Recipes().Where(r => names.Count == 0 || names.Contains(r.Character ?? "")).ToList();
        }
        private void PopulateAddToRecipe(ToolStripMenuItem action, LibraryEntry entry)
        {
            foreach (ToolStripItem item in action.DropDownItems.Cast<ToolStripItem>().ToArray()) item.Dispose();
            action.DropDownItems.Clear();
            List<OutfitRecipe> fits = RecipesFor(entry);
            action.Visible = fits.Count > 0; action.Enabled = !busy;
            foreach (OutfitRecipe fit in fits)
            {
                OutfitRecipe target = fit;
                action.DropDownItems.Add(target.Name.Replace("&", "&&"), null, delegate { if (!busy) EditRecipe(target, entry); });
            }
            if (fits.Count > 0)
            {
                action.DropDown.BackColor = Palette.Surface; action.DropDown.ForeColor = Palette.Text; action.DropDown.Font = Palette.Normal;
                action.DropDown.Renderer = new ToolStripProfessionalRenderer(new DarkMenuColors());
                var dropDown = action.DropDown as ToolStripDropDownMenu; if (dropDown != null) dropDown.ShowImageMargin = false;
            }
        }
        internal void EditRecipe(OutfitRecipe recipe, LibraryEntry addOnTop)
        {
            if (busy || recipe == null) return;
            OutfitRecipe edited; bool installNow;
            if (!RecipeBuilderForm.Choose(this, recipe.Character, GridFighters(), filterPanel.Grid.Cards, library == null ? new List<LibraryEntry>() : library.ToList(), config.SevenZipPath, null, null, out edited, out installNow, recipe, config.GameRoot, addOnTop)) return;
            try
            {
                edited.CreatedUtc = recipe.CreatedUtc;
                // The ticks of the Mod library's view stay with the layers that were not changed.
                foreach (RecipeLayer fresh in edited.AllLayers)
                {
                    RecipeLayer old = recipe.AllLayers.FirstOrDefault(l => ReplacePlanner.SameSource(l.SourcePath, fresh.SourcePath) && new HashSet<string>(l.SelectedIds ?? new List<string>(), StringComparer.OrdinalIgnoreCase).SetEquals(fresh.SelectedIds ?? new List<string>()));
                    if (old != null) { fresh.Off = old.Off; fresh.OffIds = (old.OffIds ?? new List<string>()).ToList(); }
                }
                OutfitRecipe saved = OutfitRecipes.Save(AppConfig.DataDir, edited);
                if (saved.Id != recipe.Id) OutfitRecipes.Delete(AppConfig.DataDir, recipe.Id);   // renamed or moved to another fighter: still one recipe
            }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); SetPageStatus(currentPage, "Couldn't save the recipe: " + ex.Message, Palette.Red); return; }
            bool wasOpen = openRecipe != null && package == null && openRecipe.Id == recipe.Id;
            if (wasOpen) ClosePackage();
            RecipesChanged();
            OutfitRecipe now = Recipes().FirstOrDefault(r => String.Equals(r.Name, edited.Name, StringComparison.OrdinalIgnoreCase) && String.Equals(r.Character, edited.Character, StringComparison.OrdinalIgnoreCase));
            if (wasOpen && now != null && currentPage == libraryPage) OpenRecipe(now);
            if (installNow && now != null) InstallRecipe(now);
            else SetPageStatus(currentPage, "✓ Saved the recipe " + edited.Name + ".", Palette.Green);
        }

        // ---- A recipe open in the Mod library's inspector (no mod is open then) ----
        private OutfitRecipe openRecipe;
        private List<string> openRecipeMissing = new List<string>();
        private int recipePreviewTicket; private bool recipePreviewLoading;
        internal OutfitRecipe OpenRecipeForTest { get { return openRecipe; } }
        internal bool RecipeInstallReadyForTest { get { return openRecipe != null && installButton.Enabled; } }
        internal const string NoModText = "Choose a mod from the library, or drop an archive (ZIP, RAR, 7Z) or mod folder anywhere in this window.\n\nCtrl+O opens an archive · Ctrl+F searches the library";
        // What the recipe is made of, where a mod shows its options (RecipeView.cs): one block per layer.
        private Panel recipeViewHost; private RecipeLayersView recipeView;
        internal string RecipeTextForTest { get { return recipeView == null || !recipeViewShown ? "" : recipeView.TextForTest; } }
        private bool recipeViewShown;
        private void ShowRecipeView(OutfitRecipe recipe)
        {
            if (recipeViewHost == null)
            {
                recipeViewHost = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Palette.Surface, Visible = false, Name = "recipeView" };
                recipeView = new RecipeLayersView { Dock = DockStyle.Top, AccessibleName = "The layers of the recipe, the winner first" };
                recipeView.Toggle += ToggleRecipeTick;
                recipeViewHost.Controls.Add(recipeView);
                recipeViewHost.Resize += delegate { recipeView.Invalidate(); };
                componentPanel.Controls.Add(recipeViewHost);
            }
            recipeView.SetRecipe(recipe);
            // The option tree is hidden meanwhile: it is empty, and its "Choose a mod" text must not show through.
            recipeViewShown = recipe != null; recipeViewHost.Visible = recipeViewShown; componentTree.Visible = !recipeViewShown;
            if (recipeViewShown) { recipeViewHost.BringToFront(); recipeViewHost.AutoScrollPosition = Point.Empty; }
        }
        // 1.0.18 (the user: "enable disable parts of outfit recipe"): a tick of the open recipe was clicked: a layer
        // (id null) or one of its options. The recipe is saved with it at once; nothing in the game changes until
        // Install (which reads Update while the recipe is installed with other ticks).
        internal void ToggleRecipeTick(RecipeLayer layer, string id)
        {
            if (busy || openRecipe == null || package != null || layer == null || !openRecipe.AllLayers.Contains(layer)) return;
            bool wasOff = layer.Off; List<string> wasIds = (layer.OffIds ?? new List<string>()).ToList();
            if (layer.OffIds == null) layer.OffIds = new List<string>();
            if (id == null) layer.Off = !layer.Off;
            else if (layer.Off) { layer.Off = false; layer.OffIds.RemoveAll(x => String.Equals(x, id, StringComparison.OrdinalIgnoreCase)); }   // an option of a layer that is off: the layer is on again, with it
            else if (layer.IsOff(id)) layer.OffIds.RemoveAll(x => String.Equals(x, id, StringComparison.OrdinalIgnoreCase));
            else
            {
                layer.OffIds.Add(id);
                // The last option off is the layer off; turned on again it has all its options.
                if ((layer.SelectedIds ?? new List<string>()).All(layer.IsOff)) { layer.Off = true; layer.OffIds.Clear(); }
            }
            string why = openRecipe.Effective() == null ? "A recipe needs one layer that is on." : null;
            if (why == null)
                try { OutfitRecipes.Save(AppConfig.DataDir, openRecipe); }
                catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); why = "Couldn't save the recipe: " + ex.Message; }
            if (why != null) { layer.Off = wasOff; layer.OffIds = wasIds; SetPageStatus(currentPage, why, Palette.Accent); return; }
            Point scroll = recipeViewHost.AutoScrollPosition;
            recipeView.SetRecipe(openRecipe);
            recipeViewHost.AutoScrollPosition = new Point(-scroll.X, -scroll.Y);
            selectedLabel.Text = RecipeLayerCount(openRecipe);
            modList.Invalidate();
            UpdateFooter(); UpdateInstallButton();
            CheckRecipeMaterials(openRecipe);
            SetPageStatus(currentPage, "", Palette.Muted);
        }
        // "2 layers", or "1 of 2 layers on"
        private static string RecipeLayerCount(OutfitRecipe recipe)
        {
            int all = recipe.AllLayers.Count, on = recipe.AllLayers.Count(l => !l.Off);
            return on == all ? ReplacePlanner.N(all, "layer") : on + " of " + ReplacePlanner.N(all, "layer") + " on";
        }
        private void CloseRecipe()
        {
            if (openRecipe != null)
            {
                selectedLabel.Text = ""; if (recipeViewHost != null) ShowRecipeView(null);
                if (installButton != null && editingReplacement == null) installButton.Text = "Install";   // a recipe's "Update" does not stay for a mod
            }
            openRecipe = null; recipePreviewTicket++; recipePreviewLoading = false;
        }
        internal void OpenRecipe(OutfitRecipe recipe)
        {
            if (recipe == null || busy || (openRecipe != null && package == null && openRecipe.Id == recipe.Id)) return;
            ClosePackage();   // also takes the selection off the mod list
            openRecipe = recipe;
            try { openRecipeMissing = OutfitRecipes.Missing(recipe); } catch (Exception ex) { if (ex is OutOfMemoryException) throw; openRecipeMissing = new List<string>(); }
            SelectRecipeRow(recipe);
            packageTitle.Text = recipe.Name; componentTitle.Text = "OUTFIT RECIPE";
            packageDescription.Text = "For " + recipe.Character + "  ·  " + ReplacePlanner.N(1 + recipe.Parts.Count, "mod") + (recipe.Parts.Count == 0 ? "" : " in load order");
            ShowDetailTab(false); ShowRecipeView(recipe);
            selectedLabel.Text = RecipeLayerCount(recipe); UpdateResetOptions();
            // The name the new extra outfit gets (the user can change it), and the recipe's fighter.
            if (targetCharacter.Items.Contains(recipe.Character)) targetCharacter.SelectedItem = recipe.Character;
            outfitName.Text = recipe.Name;
            UpdateFooter(); UpdateInstallButton();
            LoadRecipePreview(recipe);
            CheckRecipeMaterials(recipe);
        }
        // Material files that point at textures nobody brings (MaterialCheck.cs): said above the layers and after an
        // install. Run again when a tick changes; an older answer is dropped (the ticket).
        private int recipeCheckTicket;
        private void CheckRecipeMaterials(OutfitRecipe recipe)
        {
            int ticket = ++recipeCheckTicket;
            openRecipeWarnings = new List<string>();
            if (openRecipeMissing.Count > 0) return;
            string id = recipe.Id;
            MaterialCheck.Begin(this, MaterialCheck.Layers(recipe), config.SevenZipPath, config.GameRoot, delegate(List<string> found)
            {
                if (ticket != recipeCheckTicket || openRecipe == null || openRecipe.Id != id || package != null || found.Count == 0) return;
                openRecipeWarnings = found; if (recipeView != null) recipeView.SetWarnings(found);
            });
        }
        private List<string> openRecipeWarnings = new List<string>();
        internal string RecipeWarningsForTest { get { return String.Join(" | ", openRecipeWarnings); } }
        // After an install of the open recipe: what will not show in game, in one sentence.
        private string RecipeHeadsUp(OutfitRecipe recipe)
        {
            return openRecipe != null && recipe != null && openRecipe.Id == recipe.Id && openRecipeWarnings.Count > 0 ? " Heads up: " + openRecipeWarnings[0] : "";
        }
        private void UpdateRecipeFooter()
        {
            bool missing = openRecipeMissing.Count > 0;
            planSummary.ForeColor = missing ? Palette.Red : Palette.Text; planDetails.ForeColor = Palette.Muted;
            planSummary.Text = missing ? "Can't install this recipe" : IsReplaceMode ? "Installs as replacements" : "Installs as a new extra outfit";
            RecipeInstall now = InstalledRecipe(openRecipe);
            if (!missing && now.Changed) planSummary.Text = "Update installs it as ticked now";
            string installedAs = !now.Any ? "" : now.Changed ? "Installed with other ticks" + (now.Outfits.Count > 0 ? " as an extra outfit" : " as replacements") + ". " : "Installed now" + (now.Outfits.Count > 0 && now.Replacements.Count > 0 ? " as an extra outfit and as replacements" : now.Outfits.Count > 0 ? " as an extra outfit" : " as " + ReplacePlanner.N(now.Replacements.Count, "replacement")) + (now.Enabled ? "" : " (disabled)") + ". ";
            planDetails.Text = missing ? "No longer in the library: " + String.Join(", ", openRecipeMissing)
                : installedAs + (IsReplaceMode ? (gameRunning ? "Close SF6 first. " : "") + "Its mods in load order, the bottom one first. One replacement per mod."
                : "Its mods in load order, as one new outfit.");
        }
        private void UpdateRecipeButtons()
        {
            if (installButton == null) return;
            installButton.Enabled = !busy && openRecipeMissing.Count == 0 && (!IsReplaceMode || !gameRunning);
            previewButton.Enabled = false; previewButton.Emphasis = false;
            // 1.0.18: installed with other ticks: the button puts it in again as ticked (the kind that is installed).
            RecipeInstall made = openRecipe == null ? new RecipeInstall() : InstalledRecipe(openRecipe);
            installButton.Text = made.Changed ? "Update" : "Install";
            if (made.Changed) installButton.Enabled = !busy && openRecipeMissing.Count == 0 && (made.Replacements.Count == 0 || ReplacementUninstallBlocker(made.Replacements) == null);
            UpdateLibraryUninstall();
        }
        private void InstallOpenRecipe()
        {
            if (busy || openRecipe == null || openRecipeMissing.Count > 0) return;
            RecipeInstall made = InstalledRecipe(openRecipe);
            if (made.Changed) { UpdateInstalledRecipe(openRecipe, made); return; }
            if (IsReplaceMode) InstallRecipeAsReplacement(openRecipe); else InstallRecipe(openRecipe, outfitName.Text);
        }
        // The recipe again as it is ticked now, in place of what is installed from it: the outfit under its name, the
        // replacements with the records of mods that were turned off taken out.
        internal void UpdateInstalledRecipe(OutfitRecipe recipe, RecipeInstall made)
        {
            if (busy || recipe == null || made == null || !made.Changed) return;
            if (made.Replacements.Count > 0)
            {
                string blocker = ReplacementUninstallBlocker(made.Replacements);
                if (blocker != null) { SetPageStatus(currentPage, blocker, Palette.Accent); return; }
                InstallRecipeAsReplacement(recipe, made.Stale.Select(r => r.Mod.Id).ToList());
            }
            if (made.Outfits.Count == 0) return;
            if (made.Replacements.Count > 0) return;   // one change at a time: the outfit keeps its ticks until the next Update
            List<InstalledOutfit> old = made.Outfits.ToList();
            string root = config.CostumeRoot, sevenZip = config.SevenZipPath, name = old[0].Name; IRecycleBin bin = RecycleBin.Current;
            Work("Updating " + name + " from its recipe…", delegate
            {
                // Checked before the old outfit goes: a recipe that can't install leaves it in place.
                if (recipe.Effective() == null) throw new System.IO.IOException("Every layer of " + recipe.Name + " is turned off. Turn one on first.");
                OutfitSetStore.UninstallOutfits(root, old, bin);
                return OutfitRecipes.Install(recipe, root, name, sevenZip);
            }, delegate(InstallResult result) { RefreshInstalled("✓ Updated " + name + " from the recipe " + recipe.Name + ". Restart SF6 to load it." + RecipeHeadsUp(recipe)); });
        }
        // The base mod's own image, read off the UI thread; a later selection makes an earlier answer stale.
        private void LoadRecipePreview(OutfitRecipe recipe)
        {
            int ticket = ++recipePreviewTicket; string source = recipe.Base.SourcePath, sevenZip = config.SevenZipPath, id = recipe.Id;
            if (openRecipeMissing.Count > 0 || !IsHandleCreated) return;
            recipePreviewLoading = true;
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                Image image = null;
                try { image = RecipeBuilderForm.ReadPreview(PackageReader.Load(source, sevenZip, false), null, sevenZip); }
                catch (Exception ex) { Program.Log(ex); }
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        if (ticket != recipePreviewTicket || openRecipe == null || openRecipe.Id != id || package != null) { if (image != null) image.Dispose(); return; }
                        recipePreviewLoading = false; SetPreview(image);
                    });
                }
                catch (InvalidOperationException) { if (image != null) image.Dispose(); }
            });
        }
        internal void WaitRecipePreviewForTest(int milliseconds)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (recipePreviewLoading && clock.ElapsedMilliseconds < milliseconds) { Application.DoEvents(); System.Threading.Thread.Sleep(10); }
        }

        // Replace original: the recipe's mods as replacements, the base first (RecipeReplace.cs).
        internal void InstallRecipeAsReplacement(OutfitRecipe recipe) { InstallRecipeAsReplacement(recipe, null); }
        internal void InstallRecipeAsReplacement(OutfitRecipe recipe, List<string> alsoRemove)
        {
            if (busy || recipe == null) return;
            ReplaceEngine engine = Engine();
            bool usePak;
            if (!EnsureLooseLoader(engine, true, out usePak)) return;
            bool convert = usePak && engine.LoadState().Mods.Count > 0 && engine.CurrentMethod(engine.LoadState()) == ReplaceMethods.Loose;
            if (usePak && engine.Settings.Method == ReplaceMethods.Loose)
            {
                ReplaceSettings pakSettings = ReplaceSettings.Load(); pakSettings.Method = ReplaceMethods.Pak;
                engine = new ReplaceEngine(config.GameRoot, AppConfig.DataDir, pakSettings) { FluffyProvider = engine.FluffyProvider, SourceConfig = config };
            }
            string sevenZip = config.SevenZipPath;
            Work("Installing " + recipe.Name + " as replacements…", delegate
            {
                if (convert) engine.SwitchMethod(ReplaceMethods.Pak);
                return RecipeReplace.Install(recipe, engine, sevenZip, alsoRemove);
            }, delegate(RecipeReplaceResult r)
            {
                RefreshInstalled((r.Failed.Count == 0 ? "✓ " : "") + "Installed the recipe " + recipe.Name + " as " + ReplacePlanner.N(r.Installed.Count, "replacement") + ": " + String.Join(", then ", r.Installed.Select(m => m.Name))
                    + (r.Failed.Count == 0 ? "" : "; " + String.Join("; ", r.Failed)) + ". " + (gameRunning ? "Restart SF6 to see it." : "Start SF6 to see it.") + RecipeHeadsUp(recipe));
            });
        }
        internal OutfitRecipe RecipeOf(InstalledOutfit outfit) { return OutfitRecipes.Of(Recipes(), outfit); }

        // The library's name for a mod file ("Goth Juri C1"), else its file name.
        private string ModNameOf(string source)
        {
            LibraryEntry entry = library == null ? null : library.FirstOrDefault(e => ReplacePlanner.SameSource(e.SourcePath, source));
            return entry != null && !String.IsNullOrWhiteSpace(entry.DisplayName) ? entry.DisplayName : System.IO.Path.GetFileNameWithoutExtension(source ?? "");
        }
        // Saves the outfit (with `parts` when given: the Added parts window's list) as a recipe; returns what to say.
        internal string SaveOutfitAsRecipe(InstalledOutfit outfit, IEnumerable<PartGraft> parts)
        {
            OutfitRecipe recipe = OutfitRecipes.FromOutfit(outfit, ModNameOf(outfit.SourcePath), parts, SlotOptions(outfit) == "Main files" ? new string[0] : new[] { SlotOptions(outfit) });
            bool replaces = Recipes().Any(r => String.Equals(r.Name, recipe.Name, StringComparison.OrdinalIgnoreCase) && String.Equals(r.Character, recipe.Character, StringComparison.OrdinalIgnoreCase));
            OutfitRecipes.Save(AppConfig.DataDir, recipe);
            RecipesChanged();
            return (replaces ? "Updated the recipe " : "Saved the recipe ") + recipe.Name + " (" + recipe.Summary + ").";
        }

        internal void ShowRecipes() { ShowRecipes(null); }
        // select: the recipe to have selected when the window opens (a search result that was clicked), or null.
        internal void ShowRecipes(string select)
        {
            if (busy) return;
            // 1.0.18 (the user: the Recipes button on an outfit "doesn't account for the mod from which I click"): the
            // window is about the selected outfit's mod. Its recipes come first with the first one selected, New recipe
            // starts with that mod, and an outfit that can't be saved as it is (every variant of an archive, a group of
            // slots) offers a new recipe with its mod instead of a greyed-out Save.
            InstalledOutfit picked = currentPage == installedPage ? GetInstalledSelection() : null;
            if (picked == null && currentPage == installedPage) { OutfitGroup group = GetGroupSelection(); if (group != null && group.Slots.Count > 0) picked = group.Slots[0]; }
            string pickedSource = picked == null || String.IsNullOrWhiteSpace(picked.SourcePath) ? null : picked.SourcePath;
            LibraryEntry pickedMod = pickedSource == null || library == null ? null : library.FirstOrDefault(e => ReplacePlanner.SameSource(e.SourcePath, pickedSource));
            InstalledOutfit current = picked != null && picked.Managed && pickedSource != null ? picked : null;
            Func<OutfitRecipe, bool> uses = r => pickedSource != null && r.AllLayers.Any(l => ReplacePlanner.SameSource(l.SourcePath, pickedSource));
            if (select == null && pickedSource != null) select = Recipes().Where(uses).Select(r => r.Name).FirstOrDefault();
            string newWith = current == null && pickedMod != null ? ModNameOf(pickedSource) : null;
            OutfitRecipe chosen; bool build, edit;
            bool install = RecipesForm.Choose(this, delegate { return Recipes().OrderByDescending(uses).ToList(); }, current,
                delegate(InstalledOutfit o) { return SaveOutfitAsRecipe(o, null); },
                delegate(OutfitRecipe r) { OutfitRecipes.Delete(AppConfig.DataDir, r.Id); RecipesChanged(); }, out chosen, out build, out edit, select, newWith,
                pickedSource == null ? null : ModNameOf(pickedSource), uses);
            if (edit && chosen != null) { EditRecipe(chosen); return; }
            if (build)
            {
                // 1.0.18 (the user: no instant "Save as recipe"; New recipe should open the builder with the outfit's
                // mod and every added part already in it, "because oftentimes you want to rename it"): nothing is saved
                // until the builder's Save.
                OutfitRecipe from = null;
                if (current != null)
                    try { from = OutfitRecipes.FromOutfit(current, ModNameOf(current.SourcePath), null, SlotOptions(current) == "Main files" ? new string[0] : new[] { SlotOptions(current) }); }
                    catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); }
                ShowRecipeBuilder(pickedMod, current == null ? null : current.SelectedIds, from);
                return;
            }
            if (install) InstallRecipe(chosen);
        }
        // The builder (RecipeBuilder.cs): from the recipes window's New recipe, or the library's right-click on a mod
        // (that mod is then the base).
        internal void ShowRecipeBuilder(LibraryEntry startBase) { ShowRecipeBuilder(startBase, null); }
        // ids: the options to start with (an installed outfit's own), else the mod's ticked or default ones.
        internal void ShowRecipeBuilder(LibraryEntry startBase, IList<string> ids) { ShowRecipeBuilder(startBase, ids, null); }
        // from: an installed outfit as a recipe (its mod and its added parts, in order): the builder opens with all of
        // it as a new recipe to name and save.
        internal void ShowRecipeBuilder(LibraryEntry startBase, IList<string> ids, OutfitRecipe from)
        {
            if (busy) return;
            InstalledOutfit picked = installedList == null ? null : GetInstalledSelection();
            // The user: a mod that is clicked in the Mod library "should be selected as base already": the mod open
            // there starts the recipe when no other mod was right-clicked, with the options ticked for it.
            LibraryEntry open = package == null || library == null || currentPage != libraryPage ? null : library.FirstOrDefault(e => ReplacePlanner.SameSource(e.SourcePath, package.MergedFrom != null ? package.MergedFrom.SourcePath : package.SourcePath));
            if (startBase == null) startBase = open;
            List<string> startIds = ids != null && ids.Count > 0 && startBase != null ? ids.ToList() : startBase != null && ReferenceEquals(startBase, open) ? LibraryAddons.MainOnly(package, selectedOrder) : null;
            List<string> known = startBase == null || startBase.CharacterCodes == null ? new List<string>() : startBase.CharacterCodes.Where(c => Characters.ByCode.ContainsKey(c)).ToList();
            string fighter = known.Count > 0 ? Characters.ByCode[known[0]] : picked != null ? picked.Character : installedCharacters.FirstOrDefault();
            OutfitRecipe recipe; bool installNow;
            if (!RecipeBuilderForm.Choose(this, fighter, GridFighters(), filterPanel.Grid.Cards, library == null ? new List<LibraryEntry>() : library.ToList(), config.SevenZipPath, startBase, startIds, out recipe, out installNow, null, config.GameRoot, null, from)) return;
            try { OutfitRecipes.Save(AppConfig.DataDir, recipe); }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); SetPageStatus(currentPage, "Couldn't save the recipe: " + ex.Message, Palette.Red); return; }
            RecipesChanged();
            if (installNow) InstallRecipe(recipe);
            else SetPageStatus(currentPage, "✓ Saved the recipe " + recipe.Name + " (" + recipe.Summary + "). Install it from Installed → Recipes", Palette.Green);
        }
        internal void InstallRecipe(OutfitRecipe recipe) { InstallRecipe(recipe, null); }
        // wanted: the outfit name typed in the Mod library (a recipe open there), else the recipe's own name.
        internal void InstallRecipe(OutfitRecipe recipe, string wanted)
        {
            if (busy || recipe == null) return;
            string first = String.IsNullOrWhiteSpace(wanted) ? recipe.Name : wanted.Trim();
            try { Safety.Segment(first); } catch (Exception ex) { if (ex is OutOfMemoryException) throw; SetPageStatus(currentPage, ex.Message, Palette.Red); return; }
            string root = config.CostumeRoot, sevenZip = config.SevenZipPath, name = first;
            var taken = new HashSet<string>((installed ?? new List<InstalledOutfit>()).Where(o => String.Equals(o.Character, recipe.Character, StringComparison.OrdinalIgnoreCase)).Select(o => o.Name), StringComparer.OrdinalIgnoreCase);
            for (int n = 2; taken.Contains(name); n++) name = first + " " + n;
            Work("Installing " + name + " from its recipe…", delegate { return OutfitRecipes.Install(recipe, root, name, sevenZip); }, delegate(InstallResult result) { RefreshInstalled("✓ " + result.Message + RecipeHeadsUp(recipe)); });
        }
    }

    internal sealed class RecipesForm : Form
    {
        private readonly ListBox list;
        private readonly Label note;
        private readonly FlatButton install, delete, newRecipe, editRecipe;
        private readonly Func<List<OutfitRecipe>> load;
        private readonly Func<InstalledOutfit, string> save;
        private readonly Action<OutfitRecipe> remove;
        private readonly InstalledOutfit current;
        private List<OutfitRecipe> recipes = new List<OutfitRecipe>();
        internal OutfitRecipe Selected { get { return list.SelectedIndex >= 0 && list.SelectedIndex < recipes.Count ? recipes[list.SelectedIndex] : null; } }
        internal ListBox ListForTest { get { return list; } }
        internal string NoteForTest { get { return note.Text; } }

        internal bool NewRequested { get; private set; }   // New recipe was clicked: the builder opens next
        internal static bool Choose(IWin32Window owner, Func<List<OutfitRecipe>> load, InstalledOutfit current, Func<InstalledOutfit, string> save, Action<OutfitRecipe> remove, out OutfitRecipe chosen, out bool newRequested, out bool editRequested, string select = null, string newWith = null, string about = null, Func<OutfitRecipe, bool> uses = null)
        {
            chosen = null; newRequested = false; editRequested = false;
            if (Program.Headless) return false;
            using (var form = new RecipesForm(load, current, save, remove, newWith, about, uses))
            {
                if (select != null) form.Reload(select);
                DialogResult result = form.ShowDialog(owner);
                newRequested = form.NewRequested;
                if (form.EditRequested && form.Selected != null) { editRequested = true; chosen = form.Selected; return false; }
                if (result != DialogResult.OK) return false;
                chosen = form.Selected;
                return chosen != null;
            }
        }

        // 1.0.18: opened from an installed outfit, the window is about that outfit's mod. `about`: the mod's name;
        // `uses`: a recipe has it (those are listed first by the caller and marked); `newWith`: the outfit can't be
        // saved as it is, so the wide button starts a new recipe with its mod instead.
        private readonly string newWith, about;
        private readonly Func<OutfitRecipe, bool> uses;
        internal string NewButtonForTest { get { return newRecipe.Text; } }
        internal RecipesForm(Func<List<OutfitRecipe>> load, InstalledOutfit current, Func<InstalledOutfit, string> save, Action<OutfitRecipe> remove) : this(load, current, save, remove, null, null, null) { }
        internal RecipesForm(Func<List<OutfitRecipe>> load, InstalledOutfit current, Func<InstalledOutfit, string> save, Action<OutfitRecipe> remove, string newWith, string about, Func<OutfitRecipe, bool> uses)
        {
            this.load = load; this.save = save; this.remove = remove; this.current = current; this.newWith = newWith; this.about = about; this.uses = uses;
            SuspendLayout();
            AutoScaleDimensions = new SizeF(96F, 96F); AutoScaleMode = AutoScaleMode.Dpi;
            Text = "Outfit recipes"; StartPosition = FormStartPosition.CenterParent; Name = "recipesForm";
            BackColor = Palette.Surface; ForeColor = Palette.Text; ShowInTaskbar = false; Padding = new Padding(18, 14, 18, 14); MaximizeBox = false; MinimizeBox = false; FormBorderStyle = FormBorderStyle.FixedDialog;
            AppBranding.Apply(this);
            var title = new Label { UseMnemonic = false, Dock = DockStyle.Top, Height = 30, Font = new Font("Segoe UI", 12F, FontStyle.Bold), AutoEllipsis = true, Text = "Outfit recipes" };
            note = new Label { UseMnemonic = false, Dock = DockStyle.Top, Height = 44, ForeColor = Palette.Muted, Font = Palette.Small, Padding = new Padding(0, 2, 0, 0) };
            list = new ListBox { Dock = DockStyle.Top, Height = 52 * 6 + 4, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 52, BorderStyle = BorderStyle.None, BackColor = Palette.Background, ForeColor = Palette.Text, IntegralHeight = false, Name = "recipesList", AccessibleName = "Saved outfit recipes" };
            list.DrawItem += DrawRecipe;
            var saveRow = new Panel { Dock = DockStyle.Top, Height = 44, Padding = new Padding(0, 10, 0, 0) };
            // 1.0.18 (the user: "Remove the instant save as recipe button", "oftentimes you want to rename it"): one
            // button. From an installed outfit it opens the builder with that outfit's mod and added parts already in
            // it; nothing is saved until the builder's Save.
            newRecipe = new FlatButton(current != null ? "New recipe from " + current.Name : newWith != null ? "New recipe with " + newWith : "New recipe", false) { Dock = DockStyle.Fill, Font = Palette.Small, Name = "recipesNew" };
            newRecipe.Click += delegate { RequestNew(); };
            // 1.0.18 (the user: "we should have a edit recipe option besides new recipe too"): the selected recipe in the builder.
            editRecipe = new FlatButton("Edit recipe", false) { Dock = DockStyle.Left, Width = 130, Font = Palette.Small, Name = "recipesEdit" };
            editRecipe.Click += delegate { RequestEdit(); };
            saveRow.Controls.Add(newRecipe); saveRow.Controls.Add(new Panel { Dock = DockStyle.Left, Width = 8 }); saveRow.Controls.Add(editRecipe);
            var footer = new Panel { Dock = DockStyle.Bottom, Height = 48, Padding = new Padding(0, 14, 0, 0) };
            install = new FlatButton("Install as extra outfit", true) { Width = 190, Dock = DockStyle.Right, Font = Palette.Small, Name = "recipesInstall" }; install.Click += delegate { Accept(); };
            var close = new FlatButton("Close", false) { Width = 90, Dock = DockStyle.Right, Font = Palette.Small }; close.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            delete = new FlatButton("Delete recipe", false) { Width = 130, Dock = DockStyle.Left, Font = Palette.Small, Name = "recipesDelete", Danger = true }; delete.Click += delegate { DeleteSelected(); };
            footer.Controls.Add(new Panel { Dock = DockStyle.Fill }); footer.Controls.Add(delete); footer.Controls.Add(close); footer.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 8 }); footer.Controls.Add(install);
            Controls.Add(footer); Controls.Add(saveRow); Controls.Add(list); Controls.Add(note); Controls.Add(title);
            ClientSize = new Size(600, Padding.Vertical + title.Height + note.Height + list.Height + saveRow.Height + footer.Height);
            list.SelectedIndexChanged += delegate { UpdateState(null); };
            list.DoubleClick += delegate { Accept(); };
            KeyPreview = true;
            KeyDown += delegate(object s, KeyEventArgs e) { if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); } else if (e.KeyCode == Keys.Enter && install.Enabled) { e.Handled = true; Accept(); } };
            DarkTheme.Apply(this); ResumeLayout(true); PerformAutoScale();
            list.BackColor = Palette.Background;
            list.ItemHeight = Math.Min(255, DpiMetrics.Px(this, 52));   // a ListBox's item height is in pixels: it does not scale with the form
            Reload(null);
            ActiveControl = list;
        }

        internal void Reload(string selectName)
        {
            recipes = load == null ? new List<OutfitRecipe>() : (load() ?? new List<OutfitRecipe>());
            list.BeginUpdate(); list.Items.Clear(); foreach (OutfitRecipe r in recipes) list.Items.Add(r.Name); list.EndUpdate();
            int at = selectName == null ? -1 : recipes.FindIndex(r => String.Equals(r.Name, selectName, StringComparison.OrdinalIgnoreCase));
            if (recipes.Count > 0) list.SelectedIndex = at >= 0 ? at : 0;
            UpdateState(null);
        }
        internal void RequestNew() { NewRequested = true; DialogResult = DialogResult.Cancel; Close(); }
        internal bool EditRequested { get; private set; }   // Edit recipe was clicked: the builder opens next with the selected recipe
        internal void RequestEdit() { if (Selected == null) return; EditRequested = true; DialogResult = DialogResult.Cancel; Close(); }
        internal bool EditEnabledForTest { get { return editRecipe.Enabled; } }
        internal void DeleteSelected()
        {
            OutfitRecipe r = Selected; if (r == null || remove == null) return;
            try { remove(r); } catch (Exception ex) { if (ex is OutOfMemoryException) throw; UpdateState(ex.Message); return; }
            Reload(null); UpdateState("Deleted the recipe " + r.Name + ". Installed outfits are not touched.");
        }
        private void UpdateState(string said)
        {
            OutfitRecipe r = Selected;
            List<string> missing = r == null ? new List<string>() : OutfitRecipes.Missing(r);
            install.Enabled = r != null && missing.Count == 0; delete.Enabled = r != null; editRecipe.Enabled = r != null;
            note.Text = said ?? (recipes.Count == 0 ? "No recipes yet. A recipe writes down one outfit: its mod, its options and the parts added on top, in order. New recipe starts one."
                : missing.Count > 0 ? "Can't install this one: " + String.Join(", ", missing) + (missing.Count == 1 ? " is" : " are") + " no longer in the library."
                : about != null && uses != null ? (recipes.Any(uses) ? ReplacePlanner.N(recipes.Count(uses), "recipe") + (recipes.Count(uses) == 1 ? " uses " : " use ") + about + ", listed first. " : "No recipe uses " + about + " yet. New recipe starts one with it. ") + "Installing a recipe makes a new extra outfit."
                : "A recipe writes down one outfit: its mod, its options and the parts added on top, in order. Installing it makes a new extra outfit.");
            note.ForeColor = said == null && missing.Count > 0 ? Palette.Accent : Palette.Muted;
        }
        private void Accept() { if (!install.Enabled) return; DialogResult = DialogResult.OK; Close(); }

        private void DrawRecipe(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= recipes.Count) return;
            OutfitRecipe r = recipes[e.Index]; bool on = (e.State & DrawItemState.Selected) != 0; Rectangle b = e.Bounds;
            using (Brush fill = new SolidBrush(on ? Palette.Selected : Palette.Background)) e.Graphics.FillRectangle(fill, b);
            using (Brush rail = new SolidBrush(on ? Palette.Accent : Palette.Border)) e.Graphics.FillRectangle(rail, b.Left + DpiMetrics.Px(this, 2), b.Top + DpiMetrics.Px(this, 10), DpiMetrics.Px(this, 3), b.Height - DpiMetrics.Px(this, 20));
            const TextFormatFlags one = TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
            int x = b.Left + DpiMetrics.Px(this, 14), w = b.Width - DpiMetrics.Px(this, 24);
            // A recipe that has the mod the window was opened on says so on the right.
            if (uses != null && uses(r))
            {
                int tag = DpiMetrics.Px(this, 110);
                TextRenderer.DrawText(e.Graphics, "Uses this mod", Palette.Tiny, new Rectangle(b.Right - tag - DpiMetrics.Px(this, 10), b.Top + DpiMetrics.Px(this, 6), tag, DpiMetrics.Px(this, 22)), Palette.Green, one | TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
                w -= tag;
            }
            TextRenderer.DrawText(e.Graphics, r.Name, Palette.Strong, new Rectangle(x, b.Top + DpiMetrics.Px(this, 6), w, DpiMetrics.Px(this, 22)), Palette.Text, one);
            TextRenderer.DrawText(e.Graphics, r.Character + " · " + r.Summary, Palette.Tiny, new Rectangle(x, b.Top + DpiMetrics.Px(this, 28), w, DpiMetrics.Px(this, 18)), Palette.Muted, one);
        }
    }
}
