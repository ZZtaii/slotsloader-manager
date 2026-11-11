using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SlotsInstaller
{
    // 1.0.16 (the user: "Sakura as Ryu C1 Addon.zip" belongs inside "Sakura as Ryu C1"): Fluffy-style mods ship extra
    // options as a second archive whose options name a group of the main mod (AddonFor=Outfit) that the addon itself
    // doesn't have. Opened from the Mod library, the main mod shows them in that group (Barefoot next to Full); opened
    // alone, an addon is listed as before. The merged package is for the option list and the ticks only: every file read
    // and install plan uses the main package (MergedFrom) or the addon's own package with their own option ids
    // (Split). Each install stays a record that is valid alone: the main record with a fill-in branch of a required
    // choose-one group the addon answered, the addon's record (or outfit graft) on top of it.
    internal sealed class AddonPart
    {
        internal string Prefix, SourcePath;
        internal ModPackage Package;
        internal string FileName { get { return Path.GetFileName(SourcePath ?? ""); } }
        internal string RecordName { get { return Path.GetFileNameWithoutExtension(SourcePath ?? ""); } }
    }

    // A merged selection split into what each archive installs.
    internal sealed class AddonSplit
    {
        internal List<string> MainIds = new List<string>();
        internal List<KeyValuePair<AddonPart, List<string>>> Parts = new List<KeyValuePair<AddonPart, List<string>>>();
        internal List<string> FillIns = new List<string>();       // main branches added so the main record is valid alone
        internal List<string> Warnings = new List<string>();      // a fill-in's files the addon doesn't replace
        internal bool Merged;
        internal IEnumerable<KeyValuePair<AddonPart, List<string>>> Ticked { get { return Parts.Where(p => p.Value.Count > 0); } }
        // "Also installs Barefoot from Sakura as Ryu C1 Addon.zip on top."
        internal IEnumerable<string> Notes
        {
            get
            {
                foreach (var part in Ticked)
                    yield return "Also installs " + LibraryAddons.OptionNames(part.Key.Package, part.Value) + " from " + part.Key.FileName + " on top.";
            }
        }
    }

    internal static class LibraryAddons
    {
        internal const string PrefixHead = "addon:";

        private static string Clean(string name) { return (name ?? "").Trim(); }
        private static bool SameName(string a, string b) { return Clean(a).Length > 0 && String.Equals(Clean(a), Clean(b), StringComparison.OrdinalIgnoreCase); }

        // The one main component an addon's AddonFor names, or null (none or several).
        private static ModComponent Target(ModPackage main, string addonFor)
        {
            if (String.IsNullOrWhiteSpace(addonFor)) return null;
            List<ModComponent> named = main.Components.Where(c => SameName(c.Name, addonFor)).Take(2).ToList();
            return named.Count == 1 ? named[0] : null;
        }

        // An addon of `main`: same package name, another source, no saved bundle layout on either, and at least one option
        // whose AddonFor the addon itself can't resolve names exactly one option of the main mod.
        internal static bool IsAddonOf(ModPackage main, ModPackage candidate)
        {
            if (main == null || candidate == null || main.MergedFrom != null || candidate.MergedFrom != null) return false;
            if (main.HasBundleLayout || candidate.HasBundleLayout || !SameName(main.Name, candidate.Name) || ReplacePlanner.SameSource(main.SourcePath, candidate.SourcePath)) return false;
            return candidate.Components.Any(c => c.ParentId == null && !String.IsNullOrWhiteSpace(c.AddonFor) && Target(main, c.AddonFor) != null);
        }

        // A new package (cached packages are shared and never changed): the main's components as they are, then each
        // addon's as copies under "addon:<n>:" ids. An addon's dummy root named like the main mod is left out (its options
        // go under the main's root); an option with an unresolved AddonFor goes under the main option it names.
        internal static ModPackage Merge(ModPackage main, IList<ModPackage> addons)
        {
            if (main == null || addons == null || addons.Count == 0) return main;
            var merged = new ModPackage { SourcePath = main.SourcePath, Name = main.Name, Character = main.Character, CharacterCodes = main.CharacterCodes.ToList(), Warnings = main.Warnings.ToList(),
                RootFilesIgnored = main.RootFilesIgnored, PreviewEntries = main.PreviewEntries, PreviewSourceStamp = main.PreviewSourceStamp, MergedFrom = main, AddonParts = new List<AddonPart>() };
            merged.Components.AddRange(main.Components);
            List<ModComponent> mainRoots = main.Components.Where(c => c.IsDummy && c.ParentId == null && SameName(c.Name, main.Name)).ToList();
            string mainRoot = mainRoots.Count == 1 ? mainRoots[0].Id : null;
            for (int n = 0; n < addons.Count; n++)
            {
                ModPackage addon = addons[n];
                var part = new AddonPart { Prefix = PrefixHead + n + ":", SourcePath = addon.SourcePath, Package = addon };
                merged.AddonParts.Add(part);
                HashSet<string> dropped = DroppedRoots(main, addon);
                foreach (ModComponent c in addon.Components)
                {
                    if (dropped.Contains(c.Id)) continue;
                    string parent;
                    ModComponent target = c.ParentId == null ? Target(main, c.AddonFor) : null;
                    if (target != null) parent = target.Id;
                    else if (c.ParentId == null) parent = null;
                    else if (dropped.Contains(c.ParentId)) parent = mainRoot;
                    else parent = part.Prefix + c.ParentId;
                    merged.Components.Add(new ModComponent { Id = part.Prefix + c.Id, Name = c.Name, Description = c.Description, Author = c.Author, Version = c.Version, AddonFor = c.AddonFor, ParentId = parent,
                        PreviewEntry = c.PreviewEntry, IsDummy = c.IsDummy, InferredChoice = c.InferredChoice, BundleRule = c.BundleRule, AuthorBase = c.AuthorBase, AuthorChoiceRequired = c.AuthorChoiceRequired,
                        IsDefaultChoice = c.IsDefaultChoice, Metadata = c.Metadata, Assets = c.Assets, RootAssets = c.RootAssets });
                }
            }
            merged.PrepareChoices();
            return merged;
        }

        // An addon's dummy roots named like the main mod ("00 Menu"): its options go under the main's root instead.
        private static HashSet<string> DroppedRoots(ModPackage main, ModPackage addon)
        {
            return new HashSet<string>(addon.Components.Where(c => c.IsDummy && c.ParentId == null && SameName(c.Name, main.Name)).Select(c => c.Id), StringComparer.OrdinalIgnoreCase);
        }

        // 1.0.18 (Install all variants with addons, ArchiveCombine.cs): the addon's files that go into the combined copy of
        // the main archive, as (entry in the addon, path in the copy). Each file belongs to its deepest option or group
        // folder; files of a dropped root (Merge leaves it out too) and loose files outside every folder of the addon (a
        // README) are left out. Options at the addon's top level (no folder of their own) get a folder named after the
        // addon file, so they don't land on the main mod's top level.
        internal static List<KeyValuePair<string, string>> CombinedEntries(ModPackage main, ModPackage addon, IEnumerable<string> entries)
        {
            HashSet<string> dropped = DroppedRoots(main, addon);
            List<string> owners = addon.Components.Select(c => c.Id).Where(id => id != null && !id.StartsWith(":", StringComparison.Ordinal)).Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(id => id.Length).ToList();
            string wrap = Path.GetFileNameWithoutExtension(addon.SourcePath ?? "").Trim();
            var result = new List<KeyValuePair<string, string>>();
            foreach (string path in entries ?? new string[0])
            {
                string owner = owners.FirstOrDefault(id => id.Length == 0 || path.StartsWith(id + "/", StringComparison.OrdinalIgnoreCase));
                if (owner == null || dropped.Contains(owner)) continue;
                if (owner.Length == 0 && wrap.Length == 0) continue;
                result.Add(new KeyValuePair<string, string>(path, owner.Length == 0 ? wrap + "/" + path : path));
            }
            return result;
        }

        internal static AddonPart PartOf(ModPackage merged, string id)
        {
            if (merged == null || merged.AddonParts == null || id == null || !id.StartsWith(PrefixHead, StringComparison.Ordinal)) return null;
            return merged.AddonParts.FirstOrDefault(p => id.StartsWith(p.Prefix, StringComparison.Ordinal));
        }
        internal static bool IsAddonId(ModPackage merged, string id) { return PartOf(merged, id) != null; }
        // The ids the main package knows (the ticks of a feature that works on the main mod alone: Add to outfit).
        internal static List<string> MainOnly(ModPackage merged, IEnumerable<string> ids) { return (ids ?? new string[0]).Where(id => !IsAddonId(merged, id)).ToList(); }

        internal static string OptionNames(ModPackage package, IEnumerable<string> ids)
        {
            List<string> names = ids.Select(package.Find).Where(c => c != null && !c.IsDummy).Select(c => c.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return names.Count == 0 ? "options" : String.Join(" + ", names);
        }

        // The child of `groupId` on the way from `c` up to it, or null when `c` isn't under the group.
        private static ModComponent BranchUnder(ModPackage p, ModComponent c, string groupId)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (c != null && seen.Add(c.Id))
            {
                if (String.Equals(c.ParentId, groupId, StringComparison.OrdinalIgnoreCase)) return c;
                c = c.ParentId == null ? null : p.Find(c.ParentId);
            }
            return null;
        }
        private static IEnumerable<string> Destinations(ModComponent c, bool replace) { return (replace ? c.Assets.Concat(c.RootAssets) : c.Assets).Select(a => a.DestinationPath); }

        // The merged ticks as the main ids (fill-ins included, in the place of the addon branch they stand in for) and each
        // addon's own ids. Fill-in: a main choose-one group that requires a choice and is answered only by an addon branch
        // gets the main branch whose files the addon branch covers best (tie: the main's default, then the first), so the
        // main record installs and re-plans alone; the addon on top wins the shared files.
        internal static AddonSplit Split(ModPackage merged, IEnumerable<string> ids, bool replace)
        {
            var split = new AddonSplit();
            List<string> list = (ids ?? new string[0]).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (merged == null || merged.MergedFrom == null || merged.AddonParts == null) { split.MainIds = list; return split; }
            split.Merged = true;
            ModPackage main = merged.MergedFrom;
            foreach (AddonPart part in merged.AddonParts)
                split.Parts.Add(new KeyValuePair<AddonPart, List<string>>(part, list.Where(id => id.StartsWith(part.Prefix, StringComparison.Ordinal)).Select(id => id.Substring(part.Prefix.Length)).ToList()));
            List<string> mainIds = MainOnly(merged, list);
            merged.PrepareChoices(); main.PrepareChoices();
            var fillAt = new Dictionary<int, string>();
            List<string> defaults = Composer.DefaultSelection(main, replace);
            foreach (ModComponent group in Composer.UnresolvedChoiceGroups(main, mainIds, replace))
            {
                ModComponent shown = merged.Find(group.Id);
                if (shown == null || !merged.IsChoice(shown)) continue;
                // The addon branch the merged selection picked in this group, and every ticked addon option under it.
                int first = -1; ModComponent branch = null; var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < list.Count; i++)
                {
                    if (!IsAddonId(merged, list[i])) continue;
                    ModComponent c = merged.Find(list[i]);
                    if (c == null || c.IsDummy || !c.HasFiles(replace)) continue;
                    ModComponent b = BranchUnder(merged, c, group.Id);
                    if (b == null || !IsAddonId(merged, b.Id) || !merged.IsChoiceBranch(shown, b) || (branch != null && b != branch)) continue;
                    if (branch == null) { branch = b; first = i; }
                    foreach (string path in Destinations(c, replace)) covered.Add(path);
                }
                if (branch == null) continue;
                List<ModComponent> candidates = main.Components.Where(c => String.Equals(c.ParentId, group.Id, StringComparison.OrdinalIgnoreCase) && main.IsChoiceBranch(group, c) && !c.IsDummy && c.HasFiles(replace)).ToList();
                if (candidates.Count == 0) continue;   // nothing to stand in: the main record's own check names the group
                ModComponent pick = candidates.OrderByDescending(c => Destinations(c, replace).Count(covered.Contains)).ThenBy(c => Destinations(c, replace).Count(p => !covered.Contains(p)))
                    .ThenByDescending(c => defaults.Contains(c.Id, StringComparer.OrdinalIgnoreCase)).ThenByDescending(c => c.IsDefaultChoice).First();
                fillAt[first] = pick.Id; split.FillIns.Add(pick.Id);
                List<string> left = Destinations(pick, replace).Where(p => !covered.Contains(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (left.Count > 0)
                    split.Warnings.Add(pick.Name + " is installed with the main mod so it stays valid alone; " + branch.Name + " replaces its files except " + ReplacePlanner.N(left.Count, "file") + " (e.g. " + Path.GetFileName(left[0]) + "), which stay installed under it.");
            }
            for (int i = 0; i < list.Count; i++)
            {
                string fill;
                if (fillAt.TryGetValue(i, out fill) && !split.MainIds.Contains(fill, StringComparer.OrdinalIgnoreCase)) split.MainIds.Add(fill);
                if (!IsAddonId(merged, list[i]) && !split.MainIds.Contains(list[i], StringComparer.OrdinalIgnoreCase)) split.MainIds.Add(list[i]);
            }
            return split;
        }

        // Reopening: the main record's ids plus each addon's installed ids (namespaced). A main branch in a choose-one group
        // an addon option occupies was a fill-in, so it is shown unticked (Full off when Barefoot was chosen).
        internal static List<string> Restore(ModPackage merged, IEnumerable<string> mainIds, IEnumerable<KeyValuePair<AddonPart, List<string>>> addonIds)
        {
            List<string> result = (mainIds ?? new string[0]).ToList();
            if (merged == null || merged.MergedFrom == null) return result;
            var added = new List<string>();
            foreach (var part in addonIds ?? new KeyValuePair<AddonPart, List<string>>[0])
                foreach (string id in part.Value ?? new List<string>()) if (merged.Find(part.Key.Prefix + id) != null) added.Add(part.Key.Prefix + id);
            if (added.Count == 0) return result;
            merged.PrepareChoices();
            foreach (string id in added)
            {
                ModComponent c = merged.Find(id);
                for (ModComponent child = c, parent = c == null || c.ParentId == null ? null : merged.Find(c.ParentId); parent != null; child = parent, parent = parent.ParentId == null ? null : merged.Find(parent.ParentId))
                {
                    if (IsAddonId(merged, parent.Id) || !merged.IsChoice(parent) || !merged.IsChoiceBranch(parent, child)) continue;
                    ModComponent group = parent, branch = child;
                    result.RemoveAll(m => { ModComponent mc = merged.Find(m); ModComponent b = mc == null ? null : BranchUnder(merged, mc, group.Id); return b != null && b != branch && !IsAddonId(merged, b.Id) && merged.IsChoiceBranch(group, b); });
                }
            }
            foreach (string id in added) if (!result.Contains(id, StringComparer.OrdinalIgnoreCase)) result.Add(id);
            return result;
        }

        // Requirement=Main files of an addon option: the main options it names (honoured by ticking them).
        internal static List<ModComponent> Requirements(ModPackage merged, ModComponent addonOption)
        {
            var result = new List<ModComponent>();
            string wanted;
            if (merged == null || merged.MergedFrom == null || addonOption == null || addonOption.Metadata == null || !addonOption.Metadata.TryGetValue("requirement", out wanted)) return result;
            foreach (string name in (wanted ?? "").Split(',', ';').Select(Clean).Where(s => s.Length > 0))
            {
                ModComponent target = Target(merged.MergedFrom, name);
                if (target != null && !target.IsDummy && target.HasFiles(true)) result.Add(target);
            }
            return result;
        }
    }
}
