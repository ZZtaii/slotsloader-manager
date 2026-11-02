using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SlotsInstaller
{
    // 1.0.16: addon archives shown inside their main mod (LibraryAddons.cs). The open package may be merged; whatever
    // reads files or plans an install uses SourcePackage and the split ids. Plain Install (both modes) installs the main
    // mod as before, then each addon with its own ticks on top: in Replace original its own record directly above the
    // main record, in Extra outfit an added part of the same outfit. An addon with nothing ticked is taken out again.
    internal sealed partial class MainForm
    {
        // The package that owns the open mod's files: the main mod itself when addons are shown inside it.
        private ModPackage SourcePackage { get { return package == null ? null : package.MergedFrom ?? package; } }
        private AddonSplit CurrentSplit() { return LibraryAddons.Split(package, selectedOrder, IsReplaceMode); }
        // The main mod's ids of the current ticks (fill-ins included): what its record is installed with.
        private List<string> MainSelection() { return CurrentSplit().MainIds; }

        // A library row as the worker sees it (copied on the UI thread).
        private sealed class AddonCandidate { internal string SourcePath, Name; internal List<string> OptionNames; }

        // UI thread: the library's other archives with a known mod name and option names (the worker reads those named
        // like the open mod).
        private List<AddonCandidate> AddonCandidates(LibraryEntry opened, out string openedName)
        {
            openedName = "";
            var list = new List<AddonCandidate>();
            if (opened == null || String.IsNullOrEmpty(opened.SourcePath)) return list;
            LibraryEntry listed = library.FirstOrDefault(e => e != null && String.Equals(e.SourcePath, opened.SourcePath, StringComparison.OrdinalIgnoreCase));
            string known = listed != null && !String.IsNullOrWhiteSpace(listed.DisplayName) ? listed.DisplayName : opened.DisplayName;
            openedName = (known ?? "").Trim();
            foreach (LibraryEntry e in library)
            {
                if (e == null || String.IsNullOrEmpty(e.SourcePath) || e.NamesPending || String.IsNullOrWhiteSpace(e.DisplayName) || e.OptionNames == null || e.OptionNames.Count == 0) continue;
                if (String.Equals(e.SourcePath, opened.SourcePath, StringComparison.OrdinalIgnoreCase)) continue;
                // A known name: only rows named the same (the package name is checked again once it is read).
                if (openedName.Length > 0 && !String.Equals(e.DisplayName.Trim(), openedName, StringComparison.OrdinalIgnoreCase)) continue;
                list.Add(new AddonCandidate { SourcePath = e.SourcePath, Name = e.DisplayName.Trim(), OptionNames = e.OptionNames.ToList() });
            }
            return list;
        }

        // Worker thread: the opened package with the addons found among the candidates (read through the open-package
        // cache), or the package itself when there are none. A candidate is read only when it is named like the open mod
        // and has an option name the open mod hasn't (another copy or version of the same mod is never read for this).
        private ModPackage WithAddons(ModPackage loaded, string openedName, List<AddonCandidate> candidates, string sevenZip)
        {
            if (loaded == null || loaded.HasBundleLayout || candidates == null || candidates.Count == 0) return loaded;
            Func<string, bool> named = name => String.Equals(name, (loaded.Name ?? "").Trim(), StringComparison.OrdinalIgnoreCase) || (openedName.Length > 0 && String.Equals(name, openedName, StringComparison.OrdinalIgnoreCase));
            var own = new HashSet<string>(loaded.Components.Select(c => (c.Name ?? "").Trim()), StringComparer.OrdinalIgnoreCase);
            var addons = new List<ModPackage>();
            foreach (AddonCandidate candidate in candidates.Where(c => named(c.Name) && c.OptionNames.Any(n => !own.Contains(n.Trim()))).OrderBy(c => c.SourcePath, StringComparer.OrdinalIgnoreCase))
            {
                OperationContext.ThrowIfCancellationRequested();
                try
                {
                    string stamp = SourceStamp(candidate.SourcePath);
                    if (stamp == null) continue;
                    ModPackage other = ReadOpenPackage(candidate.SourcePath, stamp, sevenZip);
                    // Only the main mod takes its addons in (an addon opened alone is listed as before).
                    if (LibraryAddons.IsAddonOf(loaded, other) && !LibraryAddons.IsAddonOf(other, loaded)) addons.Add(other);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); }   // an unreadable addon: the main mod alone
            }
            return addons.Count == 0 ? loaded : LibraryAddons.Merge(loaded, addons);
        }

        // Reopening: each addon's ids from the newest enabled record of its source (Replace original) or from the added
        // part of the outfit the main ids came from (Extra outfit).
        private List<KeyValuePair<AddonPart, List<string>>> InstalledAddonIds(ModPackage merged, bool fromReplace, InstalledOutfit outfit)
        {
            var result = new List<KeyValuePair<AddonPart, List<string>>>();
            if (merged == null || merged.AddonParts == null) return result;
            foreach (AddonPart part in merged.AddonParts)
            {
                List<string> ids = null;
                if (fromReplace)
                    ids = gameRows.Where(r => r.Mod != null && r.Mod.Enabled && ReplacePlanner.SameSource(r.Mod.SourcePath, part.SourcePath) && r.Mod.SelectedIds != null && r.Mod.SelectedIds.Count > 0)
                        .Select(r => r.Mod).OrderByDescending(m => m.InstalledUtc ?? "", StringComparer.Ordinal).Select(m => m.SelectedIds).FirstOrDefault();
                else if (outfit != null && outfit.Parts != null)
                    ids = outfit.Parts.Where(g => ReplacePlanner.SameSource(g.SourcePath, part.SourcePath) && g.SelectedIds != null).Select(g => g.SelectedIds).LastOrDefault();
                if (ids != null && ids.Count > 0) result.Add(new KeyValuePair<AddonPart, List<string>>(part, ids.ToList()));
            }
            return result;
        }

        // Review: the merged selection's own errors (two picks in one group) and each addon's, the notes and fill-in warnings.
        private static void AddAddonReview(CompositionPlan composition, ModPackage merged, string[] selection, AddonSplit split, bool replace)
        {
            foreach (string error in Composer.Plan(merged, selection, replace).Errors) if (!composition.Errors.Contains(error)) composition.Errors.Add(error);
            foreach (var part in split.Ticked)
            {
                CompositionPlan own = replace ? Composer.Plan(part.Key.Package, part.Value, true) : Installer.SelectionFiles(part.Key.Package, part.Value);
                foreach (string error in own.Errors) composition.Errors.Add(part.Key.FileName + ": " + error);
            }
            if (!replace) composition.Warnings.AddRange(split.Warnings);
            composition.Warnings.AddRange(split.Notes);
        }

        // Health: the main mod's own addon records are not conflicts (the addon is meant to win the files they share).
        private static void AdjustAddonHealth(ReplacePlan health, ModPackage merged, AddonSplit split, ReplaceEngine engine)
        {
            if (health == null || merged.AddonParts == null) return;
            ReplaceState state = engine.LoadState();
            var mine = new HashSet<string>(state.Mods.Where(m => merged.AddonParts.Any(p => ReplacePlanner.SameSource(m.SourcePath, p.SourcePath))).Select(m => m.Id), StringComparer.OrdinalIgnoreCase);
            foreach (ReplaceConflict c in health.ConflictList.Where(c => mine.Contains(c.ModId)).ToList())
            {
                health.ConflictList.Remove(c);
                int at = health.Items.FindIndex(i => i.Level == c.Level && i.Text == c.Text);
                if (at >= 0) health.Items.RemoveAt(at);
            }
            health.Conflicts = health.ConflictList.Where(x => x.Enabled).Sum(x => x.Shared);
            foreach (string warning in split.Warnings) health.Items.Add(new HealthItem("warn", warning));
            foreach (var part in split.Parts.Where(p => p.Value.Count == 0))
            {
                List<string> names = state.Mods.Where(m => ReplacePlanner.SameSource(m.SourcePath, part.Key.SourcePath)).Select(m => m.Name).ToList();
                if (names.Count > 0) health.Items.Add(new HealthItem("info", "Uninstalls " + String.Join(", ", names) + ": none of its options is ticked."));
            }
        }

        // Worker (Replace original): each addon after the main record. Its own record (an earlier one of the same source is
        // switched in place by PrepareAndInstall), moved directly above the main record; nothing ticked uninstalls it.
        private static string InstallAddonRecords(ReplaceEngine engine, ReplaceMod main, AddonSplit split, string sevenZip)
        {
            var said = new List<string>();
            foreach (var part in split.Parts)
            {
                List<ReplaceMod> existing = engine.LoadState().Mods.Where(m => ReplacePlanner.SameSource(m.SourcePath, part.Key.SourcePath)).ToList();
                if (part.Value.Count == 0)
                {
                    foreach (ReplaceMod old in existing) engine.UninstallForGood(old.Id, DriftChoice.Ask, null);
                    if (existing.Count > 0) said.Add("removed " + String.Join(", ", existing.Select(m => m.Name)));
                    continue;
                }
                CompositionPlan own = Composer.Plan(part.Key.Package, part.Value, true);
                if (!own.CanInstall) throw new IOException(part.Key.FileName + ": " + String.Join(" ", own.Errors));
                ReplacePlan prepared;
                ReplaceMod addon = ReplacePlanner.PrepareAndInstall(own, sevenZip, engine, part.Key.RecordName, false, null, out prepared);
                ReplaceState state = engine.LoadState();
                int below = state.Mods.FindIndex(m => m.Id == main.Id), at = state.Mods.FindIndex(m => m.Id == addon.Id);
                if (below >= 0 && at >= 0 && at != below - 1) engine.Move(addon.Id, (at > below ? below : below - 1) - at);
                said.Add(addon.Name + " (" + Installer.OptionText(own) + ") · " + ReplacePlanner.N(addon.Files.Count, "file"));
            }
            return said.Count == 0 ? "" : " + " + String.Join(" + ", said);
        }

        // Worker (Extra outfit): each addon's ticks added to the outfit the main mod was just installed as.
        private static string AddAddonParts(string folder, string costumeRoot, AddonSplit split, string sevenZip)
        {
            string note = "";
            foreach (var part in split.Ticked)
            {
                CompositionPlan selection = Installer.SelectionFiles(part.Key.Package, part.Value);
                InstalledOutfit target = Installer.ListInstalled(costumeRoot).FirstOrDefault(o => String.Equals(Path.GetFullPath(o.Folder).TrimEnd('\\'), Path.GetFullPath(folder).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
                if (target == null) throw new IOException("Installed, but the outfit wasn't found to add " + Installer.OptionText(selection) + " from " + part.Key.FileName + ". Refresh and install again.");
                Installer.AddToOutfit(target, costumeRoot, selection, sevenZip);
                note += " · added " + Installer.OptionText(selection) + " from " + part.Key.RecordName;
            }
            return note;
        }
    }
}
