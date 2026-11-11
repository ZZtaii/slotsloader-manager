using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SlotsInstaller
{
    // 1.0.18 (user request: "mini-presets for outfits ... set load priority for each mod in it"): an outfit recipe is
    // one outfit written down: the base mod with its options, and the parts other mods add on top, in the order they
    // are applied (the last one wins a shared file). It holds names and option ids only, never mod files, so it is a
    // few hundred bytes and installing it needs the mods in the library. This is the shape an installed extra outfit
    // already has in its install record (source, options, Parts), so any outfit the Manager installed can be saved as
    // one, and installing one is an ordinary install with its parts added in the same transaction.
    // Kept in Data\outfit-recipes\<id>.json.
    public sealed class RecipeLayer
    {
        public string SourcePath = "", Mod = "";
        public List<string> SelectedIds = new List<string>();
        public List<string> Options = new List<string>();
        // 1.0.18: the option names for showing the layer (the base keeps Options empty so the summary names it by its
        // mod). A recipe saved before this has none: the names then come from Options, else from the option folders.
        public List<string> OptionNames = new List<string>();
        // 1.0.18 (the user: "enable disable parts of outfit recipe"): a layer that is turned off stays in the recipe
        // and is skipped at install; OffIds are the options of it that are turned off. Parts: only these parts of the
        // mod are taken (OutfitParts: "hair", "body" ...), none named = all of them.
        public bool Off;
        public List<string> OffIds = new List<string>();
        public List<string> Parts = new List<string>();

        internal bool IsOff(string id) { return OffIds != null && OffIds.Contains(id ?? "", StringComparer.OrdinalIgnoreCase); }
        // The layer as it installs: without the options turned off. Null when the layer or all its options are off.
        internal RecipeLayer Effective()
        {
            if (Off) return null;
            var on = new RecipeLayer { SourcePath = SourcePath, Mod = Mod, Parts = (Parts ?? new List<string>()).ToList() };
            List<string> ids = SelectedIds ?? new List<string>();
            bool names = OptionNames != null && OptionNames.Count == ids.Count, options = Options != null && Options.Count == ids.Count;
            for (int i = 0; i < ids.Count; i++)
            {
                if (IsOff(ids[i])) continue;
                on.SelectedIds.Add(ids[i]);
                if (names) on.OptionNames.Add(OptionNames[i]);
                if (options) on.Options.Add(Options[i]);
            }
            if (!names && OptionNames != null && on.SelectedIds.Count == ids.Count) on.OptionNames = OptionNames.ToList();
            if (!options && Options != null && on.SelectedIds.Count == ids.Count) on.Options = Options.ToList();
            return ids.Count > 0 && on.SelectedIds.Count == 0 ? null : on;
        }

        internal string ModName { get { return String.IsNullOrEmpty(Mod) ? Path.GetFileNameWithoutExtension(SourcePath ?? "") : Mod; } }
        internal List<string> ShownOptions
        {
            get
            {
                if (OptionNames != null && OptionNames.Count > 0) return OptionNames;
                if (Options != null && Options.Count > 0) return Options;
                return (SelectedIds ?? new List<string>()).Select(id => (id ?? "").Replace('\\', '/').TrimEnd('/')).Select(id => id.Substring(id.LastIndexOf('/') + 1)).Where(n => n.Length > 0).ToList();
            }
        }
    }
    public sealed class OutfitRecipe
    {
        public string Format = "sf6-outfit-recipe-v1", Id = "", Name = "", Character = "", CreatedUtc = "";
        public RecipeLayer Base = new RecipeLayer();
        public List<RecipeLayer> Parts = new List<RecipeLayer>();   // in the order applied: the last one wins
        // 1.0.18: the replacement records the last Replace original install of this recipe made (ReplaceMod.Id).
        // Uninstall and Update act on these, never on a record that only has the same mod. Ids that are gone are ignored.
        public List<string> InstalledRecords = new List<string>();

        internal List<RecipeLayer> AllLayers { get { return new[] { Base }.Concat(Parts ?? new List<RecipeLayer>()).Where(l => l != null).ToList(); } }
        // 1.0.18: the recipe as it installs: the layers that are on, with the options that are on (the bottom one of
        // them is its Base). Null when everything is turned off.
        internal OutfitRecipe Effective()
        {
            List<RecipeLayer> on = AllLayers.Select(l => l.Effective()).Where(l => l != null).ToList();
            if (on.Count == 0) return null;
            return new OutfitRecipe { Format = Format, Id = Id, Name = Name, Character = Character, CreatedUtc = CreatedUtc, Base = on[0], Parts = on.Skip(1).ToList(), InstalledRecords = (InstalledRecords ?? new List<string>()).ToList() };
        }
        internal bool AnyOff { get { return AllLayers.Any(l => l.Off || (l.OffIds != null && l.OffIds.Count > 0)); } }

        // "Goth Juri C1 + Hair B + Oily skin"
        internal string Summary
        {
            get
            {
                Func<RecipeLayer, string> text = l => l.Options != null && l.Options.Count > 0 ? String.Join(" + ", l.Options) : (String.IsNullOrEmpty(l.Mod) ? Path.GetFileName(l.SourcePath ?? "") : l.Mod);
                string own = String.IsNullOrEmpty(Base.Mod) ? Path.GetFileName(Base.SourcePath ?? "") : Base.Mod;
                return own + String.Concat(Parts.Select(p => " + " + text(p)));
            }
        }
    }

    internal static class OutfitRecipes
    {
        internal const string FolderName = "outfit-recipes", FormatName = "sf6-outfit-recipe-v1";
        internal static string Folder(string dataDir) { return Path.Combine(dataDir, FolderName); }

        internal static List<OutfitRecipe> List(string dataDir)
        {
            var list = new List<OutfitRecipe>();
            string folder = Folder(dataDir);
            if (!Directory.Exists(folder)) return list;
            foreach (string file in Directory.GetFiles(folder, "*.json"))
            {
                try
                {
                    if (new FileInfo(file).Length > 1024 * 1024) continue;
                    OutfitRecipe r = Json.Read<OutfitRecipe>(file);
                    if (r == null || r.Format != FormatName || String.IsNullOrWhiteSpace(r.Name) || r.Base == null || String.IsNullOrWhiteSpace(r.Base.SourcePath)) continue;
                    if (r.Parts == null) r.Parts = new List<RecipeLayer>();
                    if (r.InstalledRecords == null) r.InstalledRecords = new List<string>();
                    r.Parts.RemoveAll(p => p == null || String.IsNullOrWhiteSpace(p.SourcePath));
                    foreach (RecipeLayer l in new[] { r.Base }.Concat(r.Parts)) { if (l.SelectedIds == null) l.SelectedIds = new List<string>(); if (l.Options == null) l.Options = new List<string>(); if (l.OptionNames == null) l.OptionNames = new List<string>(); if (l.OffIds == null) l.OffIds = new List<string>(); if (l.Parts == null) l.Parts = new List<string>(); }
                    r.Id = Path.GetFileNameWithoutExtension(file);
                    list.Add(r);
                }
                catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); }
            }
            return list.OrderBy(r => r.Character, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        // The recipe of an outfit the Manager installed (its install record), with `parts` when the window's list
        // differs from what is installed. `name` is made unique among the saved recipes of the fighter.
        internal static OutfitRecipe FromOutfit(InstalledOutfit outfit, string baseMod, IEnumerable<PartGraft> parts, IEnumerable<string> baseOptions)
        {
            if (outfit == null || !outfit.Managed || String.IsNullOrWhiteSpace(outfit.SourcePath)) throw new IOException("Only an outfit the Manager installed from a mod can be saved as a recipe.");
            var recipe = new OutfitRecipe { Name = outfit.Name, Character = outfit.Character ?? "", CreatedUtc = DateTime.UtcNow.ToString("o") };
            recipe.Base = new RecipeLayer { SourcePath = outfit.SourcePath, Mod = baseMod ?? "", SelectedIds = (outfit.SelectedIds ?? new List<string>()).ToList(), Options = (baseOptions ?? new string[0]).ToList() };
            foreach (PartGraft g in (parts ?? outfit.Parts ?? new List<PartGraft>()).Where(g => g != null))
                recipe.Parts.Add(new RecipeLayer { SourcePath = g.SourcePath ?? "", Mod = g.Mod ?? "", SelectedIds = (g.SelectedIds ?? new List<string>()).ToList(), Options = (g.Options ?? new List<string>()).ToList(), Parts = (g.Parts ?? new List<string>()).ToList() });
            return recipe;
        }

        internal static OutfitRecipe Save(string dataDir, OutfitRecipe recipe)
        {
            if (recipe == null || String.IsNullOrWhiteSpace(recipe.Name)) throw new IOException("Name the recipe.");
            Safety.Segment(recipe.Name.Trim());
            string folder = Folder(dataDir); Directory.CreateDirectory(folder);
            List<OutfitRecipe> existing = List(dataDir);
            // The same name for the same fighter is the same recipe: saved again, it is replaced.
            OutfitRecipe same = existing.FirstOrDefault(r => String.Equals(r.Name, recipe.Name.Trim(), StringComparison.OrdinalIgnoreCase) && String.Equals(r.Character, recipe.Character, StringComparison.OrdinalIgnoreCase));
            recipe.Name = recipe.Name.Trim(); recipe.Format = FormatName;
            // A recipe saved under a new name keeps its id (it is the same recipe, and its installs name it by id).
            Guid kept;
            recipe.Id = same != null ? same.Id : Guid.TryParseExact(recipe.Id ?? "", "N", out kept) ? recipe.Id : Guid.NewGuid().ToString("N");
            if (String.IsNullOrEmpty(recipe.CreatedUtc)) recipe.CreatedUtc = DateTime.UtcNow.ToString("o");
            Json.Write(Path.Combine(folder, recipe.Id + ".json"), recipe);
            return recipe;
        }

        internal static void Delete(string dataDir, string id)
        {
            Guid parsed;
            if (!Guid.TryParseExact(id ?? "", "N", out parsed)) throw new IOException("That recipe no longer exists.");
            string file = Path.Combine(Folder(dataDir), id + ".json");
            if (File.Exists(file)) File.Delete(file);
        }

        private static bool SameLayer(RecipeLayer l, string source, IEnumerable<string> ids)
        {
            return ReplacePlanner.SameSource(l.SourcePath, source) && new HashSet<string>(l.SelectedIds ?? new List<string>(), StringComparer.OrdinalIgnoreCase).SetEquals(ids ?? new string[0]);
        }
        // The saved recipe this installed outfit is (same fighter, base, options and parts in the same order), or null.
        internal static OutfitRecipe Of(IEnumerable<OutfitRecipe> recipes, InstalledOutfit outfit)
        {
            if (outfit == null || !outfit.Managed) return null;
            var parts = (outfit.Parts ?? new List<PartGraft>()).Where(g => g != null).ToList();
            foreach (OutfitRecipe r in recipes ?? new OutfitRecipe[0])
            {
                if (!String.Equals(r.Character, outfit.Character, StringComparison.OrdinalIgnoreCase) || !SameLayer(r.Base, outfit.SourcePath, outfit.SelectedIds) || r.Parts.Count != parts.Count) continue;
                bool same = true;
                for (int i = 0; i < parts.Count && same; i++) same = SameLayer(r.Parts[i], parts[i].SourcePath, parts[i].SelectedIds);
                if (same) return r;
            }
            return null;
        }

        // Mods the recipe names that are not where it says (deleted, moved): it can't be installed until they are back.
        internal static List<string> Missing(OutfitRecipe recipe)
        {
            return new[] { recipe.Base }.Concat(recipe.Parts).Where(l => !File.Exists(l.SourcePath) && !Directory.Exists(l.SourcePath))
                .Select(l => String.IsNullOrEmpty(l.Mod) ? Path.GetFileName(l.SourcePath) : l.Mod).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        // Installs the recipe as a new extra outfit named `outfitName`: the base with its options, then every part in
        // order, in one transaction (a part that can't be added fails the install before anything is written).
        internal static InstallResult Install(OutfitRecipe recipe, string costumeRoot, string outfitName, string sevenZipPath) { return Install(recipe, costumeRoot, outfitName, sevenZipPath, false); }
        // replace (1.0.18, Update of an installed recipe): the outfit of that name is rebuilt in place, in one
        // transaction, with exactly the recipe's layers; when the recipe can't be built the outfit stays as it is.
        internal static InstallResult Install(OutfitRecipe recipe, string costumeRoot, string outfitName, string sevenZipPath, bool replace)
        {
            // 1.0.18: what is turned off in the recipe is not installed.
            string recipeName = recipe.Name, recipeId = recipe.Id;
            recipe = recipe.Effective();
            if (recipe == null) throw new IOException("Every layer of " + recipeName + " is turned off. Turn one on first.");
            List<string> missing = Missing(recipe);
            if (missing.Count > 0) throw new IOException("This recipe needs " + (missing.Count == 1 ? "a mod that is" : "mods that are") + " no longer in the library: " + String.Join(", ", missing) + ".");
            // 1.0.18 (the user: no base, "it all depends on load orders"): the layers bottom-up, each a mod with its
            // options. An extra outfit is built on a mod with a body, so the outfit itself is the lowest layer that
            // brings one, and the others go on it in their order. A layer under it lands on top of it instead, which
            // gives the same files as long as the two share none; when they do, the order can't be built and says so.
            List<RecipeLayer> layers = new[] { recipe.Base }.Concat(recipe.Parts).ToList();
            var packages = new List<ModPackage>(); var selections = new List<CompositionPlan>(); var chosen = new List<List<string>>();
            foreach (RecipeLayer layer in layers)
            {
                ModPackage package = PackageReader.Load(layer.SourcePath, sevenZipPath, false);
                List<string> ids = layer.SelectedIds.Count > 0 ? layer.SelectedIds.ToList() : Composer.DefaultSelection(package, false);
                if (ids.Any(id => package.Find(id) == null)) throw new IOException(layer.ModName + " changed: an option of this recipe is no longer in it.");
                CompositionPlan selection = Installer.SelectionFiles(package, ids, layer.Parts, sevenZipPath);
                if (selection.Errors.Count > 0) throw new IOException(layer.ModName + ": " + String.Join(" ", selection.Errors));
                packages.Add(package); chosen.Add(ids); selections.Add(selection);
            }
            int body = selections.FindIndex(s => RecipeDraft.BringsBody(s.Files.Keys));
            if (body > 0)
            {
                var bodyFiles = new HashSet<string>(selections[body].Files.Keys, StringComparer.OrdinalIgnoreCase);
                int clash = selections.Take(body).ToList().FindIndex(s => s.Files.Keys.Any(bodyFiles.Contains));
                if (clash >= 0) throw new IOException("As an extra outfit, " + layers[body].ModName + " has to be below " + layers[clash].ModName + " in this recipe: it brings the body, and they share files. Move it down with Edit recipe, or install the recipe as Replace original.");
            }
            if (body < 0) body = 0;
            // The mod the outfit is built on goes in whole: an extra outfit is a mod's own install with the others on it.
            if (layers[body].Parts != null && layers[body].Parts.Count > 0)
                throw new IOException("As an extra outfit, " + layers[body].ModName + " goes in whole: the outfit is built on it. Take all its parts (a mod above it still wins the files they share), or install the recipe as Replace original.");
            CompositionPlan plan = Composer.SavedPlan(packages[body], chosen[body], false);
            if (!plan.CanInstall) throw new IOException("Couldn't install " + recipe.Name + ": " + layers[body].ModName + ": " + String.Join(" ", plan.Errors));
            var parts = new List<CompositionPlan>();
            for (int i = 0; i < layers.Count; i++) if (i != body) parts.Add(selections[i]);
            // In place: none of the outfit's earlier parts are kept (an empty part order), only the recipe's are added.
            InstallResult result = replace
                ? Installer.Install(plan, costumeRoot, recipe.Character, outfitName, sevenZipPath, true, null, parts, null, new List<PartGraft>())
                : Installer.Install(plan, costumeRoot, recipe.Character, outfitName, sevenZipPath, false, null, parts);
            // The outfit is this recipe's: Uninstall and Update find it by that, not by what it looks like.
            if (!String.IsNullOrEmpty(recipeId))
                try
                {
                    InstalledOutfit made = Installer.ListInstalled(costumeRoot).FirstOrDefault(o => o.Managed && String.Equals(Path.GetFullPath(o.Folder), Path.GetFullPath(result.Folder), StringComparison.OrdinalIgnoreCase));
                    if (made != null) Installer.SetRecipeId(made, costumeRoot, recipeId);
                }
                catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); }
            result.Message = (replace ? "Updated " : "Installed ") + outfitName + " from the recipe " + recipe.Name + " for " + recipe.Character + ". Restart SF6 to load it.";
            return result;
        }
    }
}
