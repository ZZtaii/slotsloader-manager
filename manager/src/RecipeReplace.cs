using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SlotsInstaller
{
    // 1.0.18 (the user: recipes "should also be able to be installed as replacement"): a recipe in Replace original
    // mode is its mods installed as replacements, the base first and each part after it, so the last part ends on top
    // of the load order and wins the files it shares, as in the recipe. It is one record per mod, as for every
    // replacement: each can be switched, moved or uninstalled on its own afterwards. A mod the recipe uses more than
    // once (the base and a part of it, or two parts) is one record with those options together; when the mod's own
    // rules don't allow them together the recipe says so and installs nothing. Every mod is read, staged and checked
    // before the first record is written.
    internal sealed class RecipeReplaceResult
    {
        internal List<ReplaceMod> Installed = new List<ReplaceMod>();
        internal List<string> Failed = new List<string>();
    }

    internal static class RecipeReplace
    {
        internal sealed class Step
        {
            internal string Source = "", Mod = "";
            internal List<string> Ids = new List<string>();
            internal bool IsBase, Twice;
            internal CompositionPlan Plan;
            // 1.0.18: only these parts of the mod (OutfitParts); null once a layer of the mod takes all of it.
            internal HashSet<string> OnlyParts;
        }

        private static string NameOf(RecipeLayer layer) { return String.IsNullOrEmpty(layer.Mod) ? Path.GetFileNameWithoutExtension(layer.SourcePath ?? "") : layer.Mod; }

        // The recipe as one step per mod, in the order to install (the last one wins).
        internal static List<Step> Steps(OutfitRecipe recipe, string sevenZip)
        {
            // 1.0.18: what is turned off in the recipe is not installed.
            string recipeName = recipe.Name;
            recipe = recipe.Effective();
            if (recipe == null) throw new IOException("Every layer of " + recipeName + " is turned off. Turn one on first.");
            List<string> missing = OutfitRecipes.Missing(recipe);
            if (missing.Count > 0) throw new IOException("This recipe needs " + (missing.Count == 1 ? "a mod that is" : "mods that are") + " no longer in the library: " + String.Join(", ", missing) + ".");
            var steps = new List<Step>();
            var layers = new[] { recipe.Base }.Concat(recipe.Parts).ToList();
            for (int i = 0; i < layers.Count; i++)
            {
                RecipeLayer layer = layers[i];
                bool all = layer.Parts == null || layer.Parts.Count == 0;
                Step step = steps.FirstOrDefault(s => ReplacePlanner.SameSource(s.Source, layer.SourcePath));
                if (step == null) steps.Add(step = new Step { Source = layer.SourcePath, Mod = NameOf(layer), IsBase = i == 0, OnlyParts = all ? null : new HashSet<string>(layer.Parts, StringComparer.OrdinalIgnoreCase) });
                else
                {
                    step.Twice = true;
                    // One record per mod, so two layers of it are one install of their options together. That is only
                    // the recipe when neither is cut to parts, or both to the same ones: otherwise each layer's options
                    // would bring the other's parts too.
                    bool sameParts = all ? step.OnlyParts == null : step.OnlyParts != null && step.OnlyParts.SetEquals(layer.Parts);
                    if (!sameParts) throw new IOException("This recipe uses " + step.Mod + " more than once, taking different parts of it each time. As replacements a mod is one install, so that can't be kept apart. Install the recipe as an extra outfit instead.");
                    if (!step.IsBase) { steps.Remove(step); steps.Add(step); }   // a part used again counts where it is used last; the base stays underneath
                }
                foreach (string id in layer.SelectedIds) if (!step.Ids.Contains(id, StringComparer.OrdinalIgnoreCase)) step.Ids.Add(id);
            }
            foreach (Step step in steps)
            {
                ModPackage package = PackageReader.Load(step.Source, sevenZip, false);
                List<string> ids = step.Ids.Count > 0 ? step.Ids : Composer.DefaultSelection(package, true);
                if (ids.Any(id => package.Find(id) == null)) throw new IOException(step.Mod + " changed: an option of this recipe is no longer in it.");
                CompositionPlan plan = Composer.Plan(package, ids, true);
                if (step.OnlyParts != null)
                {
                    // Some parts only: the files of exactly these options, then the parts kept (no main-file rule applies).
                    plan = Installer.SelectionFiles(package, ids, step.OnlyParts.ToList(), sevenZip); plan.ReplaceMode = true;
                    if (plan.Errors.Count > 0 || plan.Files.Count == 0) throw new IOException(step.Mod + ": " + String.Join(" ", plan.Errors.DefaultIfEmpty("These options have no files of those parts.")));
                }
                else if (!plan.CanInstall)
                {
                    string why = String.Join(" ", plan.Errors.DefaultIfEmpty("This selection has no files."));
                    if (step.Twice) throw new IOException("This recipe uses " + step.Mod + " more than once, with options that can't be installed together as a replacement (" + why.TrimEnd('.') + "). Install it as an extra outfit instead.");
                    if (step.IsBase) throw new IOException(step.Mod + ": " + why);
                    // A part is a few options of a mod without what the mod itself asks for: its files alone, as in an extra outfit.
                    plan = Installer.SelectionFiles(package, ids); plan.ReplaceMode = true;
                    if (plan.Errors.Count > 0 || plan.Files.Count == 0) throw new IOException(step.Mod + ": " + String.Join(" ", plan.Errors.DefaultIfEmpty("These options have no files.")));
                }
                step.Plan = plan;
            }
            return steps;
        }

        internal static RecipeReplaceResult Install(OutfitRecipe recipe, ReplaceEngine engine, string sevenZip) { return Install(recipe, engine, sevenZip, null); }
        // alsoRemove (1.0.18, Update of an installed recipe): records of mods the recipe no longer installs (a layer
        // that was turned off), uninstalled once the recipe's own records are in.
        internal static RecipeReplaceResult Install(OutfitRecipe recipe, ReplaceEngine engine, string sevenZip, IEnumerable<string> alsoRemove)
        {
            var result = new RecipeReplaceResult();
            List<Step> steps = Steps(recipe, sevenZip);
            var prepared = new List<ReplacePlanner.PreparedInstall>();
            try
            {
                foreach (Step step in steps)
                {
                    ReplacePlanner.PreparedInstall one;
                    try { one = ReplacePlanner.PrepareInstall(step.Plan, sevenZip, engine, false, null); }
                    catch (SourceChangedException) { throw; }
                    catch (IOException ex) { throw new IOException(step.Mod + ": " + ex.Message, ex); }
                    prepared.Add(one);
                    if (one.Plan == null || !one.Plan.CanInstall) throw new IOException(step.Mod + ": " + (one.Plan == null ? "nothing to install." : String.Join(" ", one.Plan.Errors)));
                }
                string below = null;
                for (int i = 0; i < steps.Count; i++)
                {
                    try
                    {
                        ReplaceMod mod = ReplacePlanner.CommitInstall(prepared[i], engine, steps[i].Mod, false, below);
                        result.Installed.Add(mod); below = mod.Id;
                        foreach (ReplaceMod extra in prepared[i].Others)
                        {
                            try { engine.UninstallForGood(extra.Id, DriftChoice.Ask, null); }
                            catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); result.Failed.Add("removing the earlier " + extra.Name + " failed: " + ex.Message.TrimEnd('.')); }
                        }
                    }
                    catch (Exception ex)
                    {
                        if (ex is OutOfMemoryException || i == 0) throw;   // nothing is installed yet: the error is the whole answer
                        Program.Log(ex); result.Failed.Add(steps[i].Mod + " was not installed: " + ex.Message.TrimEnd('.'));
                    }
                }
                var made = new HashSet<string>(result.Installed.Select(m => m.Id), StringComparer.OrdinalIgnoreCase);
                foreach (string id in (alsoRemove ?? new string[0]).Where(x => !made.Contains(x)).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    ReplaceMod gone = engine.LoadState().Mods.FirstOrDefault(m => String.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));
                    if (gone == null) continue;
                    try { engine.UninstallForGood(id, DriftChoice.Ask, null); }
                    catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); result.Failed.Add("removing " + gone.Name + " (turned off in the recipe) failed: " + ex.Message.TrimEnd('.')); }
                }
                return result;
            }
            finally { foreach (ReplacePlanner.PreparedInstall one in prepared) ReplacePlanner.Discard(one); }
        }
    }
}
