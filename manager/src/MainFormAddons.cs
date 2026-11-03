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

        // 1.0.16 (PR #22 review, F1): the addon records whose ticks this open of the mod showed (restored when it opened,
        // or installed since). Only these are uninstalled when their addon ends up with nothing ticked: a record never
        // shown ticked (another copy, one the window didn't restore) is left alone. Cleared when another mod opens.
        private readonly HashSet<string> shownAddonRecords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Reopening: each addon's ids from the newest record of its source, enabled first, a disabled one too (Replace
        // original; the record is remembered in `shown`), or from the added part of the outfit the main ids came from
        // (Extra outfit).
        private List<KeyValuePair<AddonPart, List<string>>> InstalledAddonIds(ModPackage merged, bool fromReplace, InstalledOutfit outfit, ICollection<string> shown)
        {
            var result = new List<KeyValuePair<AddonPart, List<string>>>();
            if (merged == null || merged.AddonParts == null) return result;
            foreach (AddonPart part in merged.AddonParts)
            {
                List<string> ids = null;
                if (fromReplace)
                {
                    ReplaceMod record = gameRows.Where(r => r.Mod != null && ReplacePlanner.SameSource(r.Mod.SourcePath, part.SourcePath) && r.Mod.SelectedIds != null && r.Mod.SelectedIds.Count > 0)
                        .Select(r => r.Mod).OrderByDescending(m => m.Enabled).ThenByDescending(m => m.InstalledUtc ?? "", StringComparer.Ordinal).FirstOrDefault();
                    if (record != null) { ids = record.SelectedIds; if (shown != null) shown.Add(record.Id); }
                }
                else if (outfit != null && outfit.Parts != null)
                    ids = outfit.Parts.Where(g => ReplacePlanner.SameSource(g.SourcePath, part.SourcePath) && g.SelectedIds != null).Select(g => g.SelectedIds).LastOrDefault();
                if (ids != null && ids.Count > 0) result.Add(new KeyValuePair<AddonPart, List<string>>(part, ids.ToList()));
            }
            return result;
        }

        // The record an addon's install switches in place: the one shown, else the highest-priority one of its source.
        private static ReplaceMod AddonTarget(IEnumerable<ReplaceMod> mods, AddonPart part, ICollection<string> shown)
        {
            List<ReplaceMod> same = mods.Where(m => m != null && ReplacePlanner.SameSource(m.SourcePath, part.SourcePath)).ToList();
            return same.FirstOrDefault(m => shown.Contains(m.Id)) ?? same.FirstOrDefault();
        }

        // 1.0.16 (PR #22 review, F3): "the same install" counts the addon ticks too (main + Barefoot and main + Full have
        // the same main ids because of the fill-in). Replace original: each ticked addon's record (AddonTarget) has
        // those ids; an unticked addon has no shown record (one never shown stays as it is, so it doesn't count).
        private bool AddonRecordsMatch(AddonSplit split)
        {
            if (split == null || !split.Merged) return true;
            List<ReplaceMod> mods = gameRows.Where(r => r.Mod != null).Select(r => r.Mod).ToList();
            foreach (var part in split.Parts)
            {
                if (part.Value.Count == 0) { if (mods.Any(m => shownAddonRecords.Contains(m.Id) && ReplacePlanner.SameSource(m.SourcePath, part.Key.SourcePath))) return false; continue; }
                ReplaceMod target = AddonTarget(mods, part.Key, shownAddonRecords);
                if (target == null || target.SelectedIds == null || !Composer.MatchesSavedSelection(part.Key.Package, part.Value, target.SelectedIds)) return false;
            }
            return true;
        }
        // Extra outfit: the outfit's added parts from each addon have the ticked options (none when nothing is ticked).
        internal static bool AddonPartsMatch(AddonSplit split, InstalledOutfit outfit)
        {
            if (split == null || !split.Merged) return true;
            foreach (var part in split.Parts)
            {
                List<string> saved = (outfit.Parts ?? new List<PartGraft>()).Where(g => ReplacePlanner.SameSource(g.SourcePath, part.Key.SourcePath) && g.SelectedIds != null).SelectMany(g => g.SelectedIds).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                List<string> ticked = part.Value.Count == 0 ? new List<string>() : Installer.SelectionFiles(part.Key.Package, part.Value).Layers.Select(c => c.Id).ToList();
                if (ticked.Count == 0 && saved.Count == 0) continue;
                if (ticked.Count == 0 || saved.Count == 0 || !Composer.MatchesSavedSelection(part.Key.Package, ticked, saved)) return false;
            }
            return true;
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
        // 1.0.16 (PR #22 review, F1): the lines say what Install does: only a shown record is uninstalled, a disabled
        // one stays disabled, other records of an addon's source go (the 2.4.1 switch keeps one).
        private static void AdjustAddonHealth(ReplacePlan health, ModPackage merged, AddonSplit split, ReplaceEngine engine, ICollection<string> shown)
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
            foreach (var part in split.Parts)
            {
                if (part.Value.Count == 0)
                {
                    List<string> names = state.Mods.Where(m => shown.Contains(m.Id) && ReplacePlanner.SameSource(m.SourcePath, part.Key.SourcePath)).Select(m => m.Name).ToList();
                    if (names.Count > 0) health.Items.Add(new HealthItem("info", "Uninstalls " + String.Join(", ", names) + ": none of its options is ticked."));
                    continue;
                }
                ReplaceMod target = AddonTarget(state.Mods, part.Key, shown);
                if (target == null) continue;
                if (!target.Enabled) health.Items.Add(new HealthItem("info", target.Name + " stays disabled, as on the Installed page: enable it there to see " + LibraryAddons.OptionNames(part.Key.Package, part.Value) + " in game."));
                List<string> others = state.Mods.Where(m => m.Id != target.Id && ReplacePlanner.SameSource(m.SourcePath, part.Key.SourcePath)).Select(m => m.Name).ToList();
                if (others.Count > 0) health.Items.Add(new HealthItem("info", "Uninstalls " + String.Join(", ", others) + ": another record of " + part.Key.FileName + " (one stays)."));
            }
        }

        // What an install with addons did (Replace original). Leftover: addon records still to uninstall (after a drift
        // question); Failed: the steps after the main commit that didn't go through, for the status line.
        private sealed class AddonInstall
        {
            internal ReplaceMod Main; internal DriftException Drift;
            internal List<string> Said = new List<string>(), Failed = new List<string>(), Installed = new List<string>();
            internal List<ReplaceMod> Leftover = new List<ReplaceMod>(), Removed = new List<ReplaceMod>();
        }
        internal static Action<AddonPart> BeforeAddonCommit;   // tests: a step after the main commit that fails

        // Worker (Replace original). 1.0.16 (PR #22 review, F2): each ticked addon is planned, staged and checked first
        // (a changed archive, a plan error, a refused health check stop here with nothing written); then the main record;
        // then each addon's record (an earlier one switched in place) directly above the main record in the same change
        // (no Move); then the records to uninstall: other records of a ticked addon's source and, for an addon with
        // nothing ticked, its shown record (F1). A drift question there goes back to the window (ChangeWithDriftChoice's
        // question); a step that fails after the main commit is named in the status, not thrown.
        private static AddonInstall InstallWithAddons(ReplaceEngine engine, CompositionPlan composition, string sevenZip, string name, string replaceId, AddonSplit split, ICollection<string> shown)
        {
            var result = new AddonInstall();
            var prepared = new List<KeyValuePair<AddonPart, ReplacePlanner.PreparedInstall>>();
            var options = new Dictionary<AddonPart, string>();
            try
            {
                foreach (var part in split == null ? new KeyValuePair<AddonPart, List<string>>[0] : split.Ticked.ToArray())
                {
                    CompositionPlan own = Composer.Plan(part.Key.Package, part.Value, true);
                    if (!own.CanInstall) throw new IOException(part.Key.FileName + ": " + String.Join(" ", own.Errors));
                    options[part.Key] = Installer.OptionText(own);
                    ReplaceMod target = AddonTarget(engine.LoadState().Mods, part.Key, shown);
                    try { prepared.Add(new KeyValuePair<AddonPart, ReplacePlanner.PreparedInstall>(part.Key, ReplacePlanner.PrepareInstall(own, sevenZip, engine, false, target == null ? null : target.Id))); }
                    catch (SourceChangedException) { throw; }   // reloaded (MainForm.Error), nothing installed
                    catch (IOException ex) { throw new IOException(part.Key.FileName + ": " + ex.Message, ex); }
                }
                ReplacePlan mainPlan;
                result.Main = ReplacePlanner.PrepareAndInstall(composition, sevenZip, engine, name, false, replaceId, out mainPlan);
                if (split == null) return result;
                foreach (var p in prepared)
                {
                    try
                    {
                        if (BeforeAddonCommit != null) BeforeAddonCommit(p.Key);
                        ReplaceMod addon = ReplacePlanner.CommitInstall(p.Value, engine, p.Key.RecordName, false, result.Main.Id);
                        result.Installed.Add(addon.Id); result.Leftover.AddRange(p.Value.Others);
                        result.Said.Add(addon.Name + " (" + options[p.Key] + (addon.Enabled ? "" : ", kept disabled") + ") · " + ReplacePlanner.N(addon.Files.Count, "file"));
                    }
                    catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); result.Failed.Add("adding " + options[p.Key] + " from " + p.Key.FileName + " failed: " + ex.Message.TrimEnd('.')); }
                }
                ReplaceState state = engine.LoadState();
                foreach (var part in split.Parts.Where(x => x.Value.Count == 0))
                    result.Leftover.AddRange(state.Mods.Where(m => shown.Contains(m.Id) && ReplacePlanner.SameSource(m.SourcePath, part.Key.SourcePath)));
                RemoveAddonRecords(engine, result, DriftChoice.Ask);
                return result;
            }
            finally { foreach (var p in prepared) ReplacePlanner.Discard(p.Value); }
        }
        // Uninstalls the leftover records for good (no kept copy, as the 2.4.1 switch); stops at a drift question.
        private static void RemoveAddonRecords(ReplaceEngine engine, AddonInstall result, DriftChoice choice)
        {
            result.Drift = null;
            foreach (ReplaceMod old in result.Leftover.ToList())
            {
                if (engine.LoadState().Find(old.Id) == null) { result.Leftover.Remove(old); continue; }
                try { engine.UninstallForGood(old.Id, choice, null); result.Removed.Add(old); }
                catch (DriftException ex) { if (choice != DriftChoice.Ask) throw; result.Drift = ex; return; }
                catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); result.Failed.Add("removing " + old.Name + " failed: " + ex.Message.TrimEnd('.')); }
                result.Leftover.Remove(old);
            }
        }
        // " + X Addon (Barefoot) · 1 file + removed Y", or the failures: "; adding Barefoot from Y.zip failed: …".
        private static string AddonInstallText(AddonInstall r, bool failures)
        {
            if (failures) return r.Failed.Count == 0 ? "" : "; " + String.Join("; ", r.Failed);
            var parts = new List<string>(r.Said);
            if (r.Removed.Count > 0) parts.Add("removed " + String.Join(", ", r.Removed.Select(m => m.Name)));
            return parts.Count == 0 ? "" : " + " + String.Join(" + ", parts);
        }

        // Window side of InstallWithAddons: the shown records follow the install, a drift question is asked as for any
        // uninstall (Cancel keeps the records and says so), then the status line.
        private void FinishAddonInstall(AddonInstall r, Action<AddonInstall> done)
        {
            foreach (string id in r.Installed) shownAddonRecords.Add(id);
            foreach (ReplaceMod m in r.Removed) shownAddonRecords.Remove(m.Id);
            if (r.Drift == null) { done(r); return; }
            DriftChoice choice = AskDrift(r.Drift);
            LastDriftChoice = choice;
            if (choice == DriftChoice.Ask)
            {
                r.Failed.Add("kept " + String.Join(", ", r.Leftover.Select(m => m.Name)) + ": " + r.Drift.Items[0].Text);
                r.Drift = null; r.Leftover.Clear(); done(r); return;
            }
            ReplaceEngine again = Engine();
            Work("Installing replacement…", delegate { RemoveAddonRecords(again, r, choice); return r; }, delegate(AddonInstall after) { FinishAddonInstall(after, done); });
        }
    }
}
