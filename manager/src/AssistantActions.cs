using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

namespace SlotsInstaller
{
    internal enum AssistantActionKind { Install, SetEnabled, Uninstall, SavePreset, RestorePreset, ReplaceInstall, GameModChange, PresetImport }
    internal enum AssistantActionState { Pending, Running, Succeeded, Failed, NotRun, Discarded }

    internal sealed class AssistantAction
    {
        internal int Number;
        internal AssistantActionKind Kind;
        internal AssistantActionState State = AssistantActionState.Pending;
        internal string Title, Result = "";
        // Install
        internal string ModId, ModName, SourcePath, Character, OutfitName, ReplaceInstalledId;
        internal List<string> OptionIds = new List<string>();
        internal List<string> OptionNames = new List<string>();
        internal int FileCount; internal long Bytes;
        // Enable / uninstall
        internal string InstalledId; internal bool Enabled;
        internal List<string> InstalledIds = new List<string>();
        internal List<string> InstalledNames = new List<string>();
        // Presets
        internal string PresetName, PresetId;
        internal string PresetScopeCode = "";   // 2.4.1: "" | both | extra_outfits | replacements
        internal bool AutoApplied;
        // Replace original (Fluffy-style): installs, changes and Fluffy preset imports
        internal string ReplacementId = "", GameAction = "", Affects = "", PresetPath = "", PresetMode = "";
        // Other installed replacements for the same outfit or files (2.0.1). Information only: an install never removes them.
        internal List<ReplaceConflict> Conflicts = new List<ReplaceConflict>();
        // 2.4.1: installed records of the same source (another variant or option set): the install switches them in place.
        internal List<string> Replaces = new List<string>();
        internal string ModeLabel { get { return Kind == AssistantActionKind.Install ? "Extra outfit" : Kind == AssistantActionKind.ReplaceInstall || Kind == AssistantActionKind.GameModChange ? "Replace original" : Kind == AssistantActionKind.PresetImport ? "External preset" : "Outfits"; } }

        internal string StateText
        {
            get
            {
                switch (State)
                {
                    case AssistantActionState.Pending: return "awaiting approval";
                    case AssistantActionState.Running: return "running";
                    case AssistantActionState.Succeeded: return "done";
                    case AssistantActionState.Failed: return "failed";
                    case AssistantActionState.Discarded: return "discarded";
                    default: return "not run";
                }
            }
        }
    }

    internal sealed class AssistantBatchResult
    {
        internal int Requested, Succeeded;
        internal AssistantAction FailedAction;
        internal List<AssistantAction> Actions = new List<AssistantAction>();
        internal bool Cancelled;
    }

    // Validation and execution of mutating requests. The same checks run when an
    // action is queued and again immediately before it runs.
    internal sealed class AssistantActionRunner
    {
        private readonly AssistantWorkspace workspace;
        internal AssistantActionRunner(AssistantWorkspace workspace) { this.workspace = workspace; }

        internal static string Describe(AssistantAction a)
        {
            switch (a.Kind)
            {
                case AssistantActionKind.ReplaceInstall:
                    return "Replace original · " + (String.IsNullOrEmpty(a.ReplacementId) ? "Install" : "Update") + " '" + a.ModName + "'" + (a.OptionNames.Count > 0 ? " (" + String.Join(" + ", a.OptionNames) + ")" : "") + (a.Affects.Length > 0 ? " · replaces " + a.Affects : "")
                        + (a.Conflicts.Count > 0 ? " · " + String.Join("; ", a.Conflicts.Take(2).Select(ReplacePlanner.ConflictBrief)) + (a.Conflicts.Count > 2 ? " +" + (a.Conflicts.Count - 2) : "") : "");
                case AssistantActionKind.GameModChange:
                    return "Replace original · " + GameActionLabel(a.GameAction) + (a.InstalledNames.Count > 0 ? " '" + String.Join("", a.InstalledNames) + "'" : "") + (a.GameAction == "uninstall" ? " (deleted; reinstall from the library)" : "");
                case AssistantActionKind.PresetImport:
                    return "Import external preset '" + a.PresetName + "' · costume mods as " + (a.PresetMode == "extra_outfit" ? "extra outfits" : "replacements") + ", tool mods into the game folder";
                case AssistantActionKind.Install:
                    return "Extra outfit · " + (String.IsNullOrEmpty(a.ReplaceInstalledId) ? "Install" : "Replace") + " '" + a.OutfitName + "' for " + a.Character + " from '" + a.ModName + "'" + (a.OptionNames.Count > 0 ? " (" + String.Join(" + ", a.OptionNames) + ")" : "");
                case AssistantActionKind.SetEnabled: return (a.Enabled ? "Enable" : "Disable") + " '" + String.Join("", a.InstalledNames) + "'";
                case AssistantActionKind.Uninstall: return "Uninstall " + a.InstalledNames.Count + " outfit" + (a.InstalledNames.Count == 1 ? "" : "s") + ": " + String.Join(", ", a.InstalledNames.Select(n => "'" + n + "'")) + " (deleted; manual outfits go to the Recycle Bin)";
                case AssistantActionKind.SavePreset: return "Save preset '" + a.PresetName + "' of " + ScopeWords(PresetScopes.Parse(a.PresetScopeCode) ?? PresetScope.Both, "enabled ");
                default: return "Restore preset '" + a.PresetName + "' (adds missing " + ScopeWords(PresetScopes.Parse(a.PresetScopeCode) ?? PresetScope.Both, "") + ")";
            }
        }

        // 2.4.1: "enabled extra outfits and replacements" / "extra outfits" / "replacements".
        private static string ScopeWords(PresetScope scope, string adjective)
        {
            return adjective + (scope == PresetScope.ExtraOutfits ? "extra outfits" : scope == PresetScope.Replacements ? "replacements" : "extra outfits and replacements");
        }

        internal static string GameActionLabel(string action)
        {
            switch (action)
            {
                case "enable": return "Enable"; case "disable": return "Disable"; case "move_up": return "Move up"; case "move_down": return "Move down";
                case "uninstall": return "Uninstall"; case "reapply_all": return "Reapply all replacements"; default: return "Restore original game files (removes every replacement and tool mod)";
            }
        }

        internal static ReplaceMod FindReplacement(ReplaceState state, string id) { return state.Mods.FirstOrDefault(m => AssistantWorkspace.ReplacementId(m) == id); }

        // Replace-original install: same composition rules plus protected paths and Fluffy ownership.
        internal CompositionPlan ValidateReplaceInstall(AssistantAction a, IEnumerable<AssistantAction> others, bool fresh, out List<string> errors)
        {
            errors = new List<string>();
            if (fresh) { if (!File.Exists(a.SourcePath) && !Directory.Exists(a.SourcePath)) { errors.Add("The mod is no longer in the library."); return null; } }
            else
            {
                LibraryEntry entry = workspace.FindMod(a.ModId);
                if (entry == null) { errors.Add("Unknown mod_id '" + a.ModId + "'. Use an ID returned by search_mods."); return null; }
                if (entry.Detection == "Error") { errors.Add("This mod could not be read: " + workspace.Scrub(entry.DetectionError)); return null; }
                a.SourcePath = entry.SourcePath;
            }
            ModPackage package = fresh ? PackageReader.Load(a.SourcePath, workspace.SevenZip) : workspace.Package(workspace.FindMod(a.ModId));
            a.ModName = package.Name;
            var unknown = a.OptionIds.Where(id => package.Find(id) == null).ToList();
            if (unknown.Count > 0) { errors.Add("Unknown option_id(s): " + String.Join(", ", unknown.Select(id => "'" + id + "'")) + ". Use IDs from get_mod_options."); return null; }
            if (a.OptionIds.Count == 0) a.OptionIds = Composer.DefaultSelection(package, true);
            a.OptionNames = a.OptionIds.Select(id => package.Find(id)).Where(c => c != null && !c.IsDummy).Select(c => c.Name).ToList();
            CompositionPlan plan = Composer.Plan(package, a.OptionIds, true);
            errors.AddRange(plan.Errors);
            ReplaceEngine engine = workspace.Engine();
            FluffyInstalls fluffy = engine.Fluffy();
            ReplaceState state = engine.LoadState();
            var reference = GameReference.FromCmd(null);
            var affects = new List<string>();
            foreach (string path in plan.Files.Keys)
            {
                string dest = path.StartsWith("natives/", StringComparison.OrdinalIgnoreCase) ? ModFixes.NormalizeNativePath(path) : PakHash.Normalize(path).TrimStart('/');
                if (ReplaceEngine.SkippedProgram(dest, engine.GameRoot)) continue;
                try { ReplaceEngine.ValidateDestination(dest); } catch (IOException ex) { errors.Add(ex.Message); }
                FluffyMod owner = fluffy.Owner(dest);
                if (owner != null && state.Deployment(dest) == null) errors.Add(dest + " is installed by an external mod manager (" + owner.Name + "). Disable that mod there first.");
                var m = System.Text.RegularExpressions.Regex.Match(dest, @"^natives/stm/(?:streaming/)?product/model/esf/esf(\d{3})/(\d{3})/");
                if (m.Success) { string label = reference.DescribeFolder(m.Groups[1].Value, m.Groups[2].Value); if (!affects.Contains(label)) affects.Add(label); }
            }
            a.Affects = String.Join(", ", affects.Take(3));
            if (!String.IsNullOrEmpty(a.ReplacementId) && FindReplacement(state, a.ReplacementId) == null) errors.Add("Unknown update_replacement_id '" + a.ReplacementId + "'. Use an ID from list_game_mods.");
            // Same outfit / same files as installed replacements, exactly as the app's health check reports them.
            ReplaceMod updating = String.IsNullOrEmpty(a.ReplacementId) ? null : FindReplacement(state, a.ReplacementId);
            var dests = plan.Files.Keys.Select(path => { string d = path.StartsWith("natives/", StringComparison.OrdinalIgnoreCase) ? ModFixes.NormalizeNativePath(path) : PakHash.Normalize(path).TrimStart('/'); return d.StartsWith("natives/", StringComparison.OrdinalIgnoreCase) ? ModFixes.UpgradeTexSuffix(d, reference.TexVersion) ?? d : d; }).ToList();
            a.Replaces = state.Mods.Where(m => !m.Test && ReplacePlanner.SameSource(m.SourcePath, a.SourcePath)).Select(m => m.Name).ToList();
            a.Conflicts = ReplacePlanner.AnalyzeConflicts(dests, state, updating == null ? null : updating.Id, GameReference.Tables(workspace.Config.GameRoot), a.SourcePath, plan.Layers.Select(c => c.Id));
            foreach (AssistantAction other in others ?? new AssistantAction[0])
            {
                if (other == a || other.State != AssistantActionState.Pending) continue;
                if (other.Kind == AssistantActionKind.ReplaceInstall && String.Equals(other.ModId, a.ModId, StringComparison.OrdinalIgnoreCase) && other.OptionIds.SequenceEqual(a.OptionIds, StringComparer.OrdinalIgnoreCase)) errors.Add("Queued action #" + other.Number + " already installs this selection.");
                if (!String.IsNullOrEmpty(a.ReplacementId) && other.ReplacementId == a.ReplacementId) errors.Add("Queued action #" + other.Number + " already changes that replacement.");
            }
            a.FileCount = plan.Files.Count; a.Bytes = plan.TotalBytes;
            return plan;
        }

        internal List<string> ValidateGameModChange(AssistantAction a, IEnumerable<AssistantAction> others)
        {
            var errors = new List<string>();
            ReplaceState state = workspace.Engine().LoadState();
            bool global = a.GameAction == "reapply_all" || a.GameAction == "restore_original_game_files";
            if (global) { if (state.Mods.Count == 0) errors.Add("The Manager has no replacements or tool mods installed."); a.InstalledNames = new List<string>(); }
            else
            {
                ReplaceMod mod = FindReplacement(state, a.ReplacementId);
                if (mod == null) { errors.Add("Unknown replacement_id '" + a.ReplacementId + "'. Use an ID from list_game_mods."); return errors; }
                a.InstalledNames = new List<string> { mod.Name };
                int index = state.Mods.IndexOf(mod);
                if (a.GameAction == "enable" && mod.Enabled) errors.Add("'" + mod.Name + "' is already enabled.");
                if (a.GameAction == "disable" && !mod.Enabled) errors.Add("'" + mod.Name + "' is already disabled.");
                if (a.GameAction == "move_up" && index == 0) errors.Add("'" + mod.Name + "' is already at the top.");
                if (a.GameAction == "move_down" && index == state.Mods.Count - 1) errors.Add("'" + mod.Name + "' is already at the bottom.");
            }
            foreach (AssistantAction o in (others ?? new AssistantAction[0]).Where(o => o != a && o.State == AssistantActionState.Pending && o.Kind == AssistantActionKind.GameModChange))
                if (o.ReplacementId == a.ReplacementId && !global) errors.Add("Queued action #" + o.Number + " already changes that replacement.");
            return errors;
        }

        internal static InstalledOutfit FindInstalled(List<InstalledOutfit> rows, string installedId)
        {
            return rows.FirstOrDefault(o => AssistantWorkspace.InstalledId(o) == installedId);
        }

        private static string Label(InstalledOutfit o) { return o.Character + "/" + o.Name; }

        // Validates an install against current state and returns its plan. `others`
        // are queued actions it must not collide with (empty when executing).
        internal CompositionPlan ValidateInstall(AssistantAction a, IEnumerable<AssistantAction> others, bool fresh, out List<string> errors)
        {
            errors = new List<string>();
            LibraryEntry entry = null;
            if (fresh)
            {
                if (!File.Exists(a.SourcePath) && !Directory.Exists(a.SourcePath)) { errors.Add("The mod is no longer in the library."); return null; }
            }
            else
            {
                entry = workspace.FindMod(a.ModId);
                if (entry == null) { errors.Add("Unknown mod_id '" + a.ModId + "'. Use an ID returned by search_mods."); return null; }
                if (entry.Detection == "Error") { errors.Add("This mod could not be read: " + workspace.Scrub(entry.DetectionError)); return null; }
                a.SourcePath = entry.SourcePath;
            }
            ModPackage package = fresh ? PackageReader.Load(a.SourcePath, workspace.SevenZip) : workspace.Package(entry);
            a.ModName = package.Name;
            var unknown = a.OptionIds.Where(id => package.Find(id) == null).ToList();
            if (unknown.Count > 0) { errors.Add("Unknown option_id(s): " + String.Join(", ", unknown.Select(id => "'" + id + "'")) + ". Use IDs from get_mod_options."); return null; }
            if (a.OptionIds.Count == 0) { errors.Add("Select at least one option (include the main files)."); return null; }
            a.OptionNames = a.OptionIds.Select(id => package.Find(id)).Where(c => !c.IsDummy).Select(c => c.Name).ToList();
            CompositionPlan plan = Composer.Plan(package, a.OptionIds);
            errors.AddRange(plan.Errors);
            string wrong = AssistantWorkspace.CharacterOfFiles(plan, a.Character);
            if (wrong != null) errors.Add(wrong);
            string name = a.OutfitName ?? "";
            try { Safety.Segment(name); }
            catch (IOException) { errors.Add("The outfit name '" + name + "' is not a valid Windows folder name. Avoid \\ / : * ? \" < > | and trailing dots or spaces."); }
            if (name.StartsWith(".") || name.Equals("natives", StringComparison.OrdinalIgnoreCase)) errors.Add("Outfit names must not begin with a dot or be 'natives'.");
            List<InstalledOutfit> installed = workspace.Installed();
            InstalledOutfit sameName = installed.FirstOrDefault(o => String.Equals(o.Character, a.Character, StringComparison.OrdinalIgnoreCase) && String.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase));
            if (String.IsNullOrEmpty(a.ReplaceInstalledId))
            {
                if (sameName != null) errors.Add("Name collision: " + a.Character + " already has an outfit named '" + sameName.Name + "' (" + AssistantWorkspace.InstalledId(sameName) + (sameName.Managed ? (sameName.Enabled ? ", managed" : ", managed, disabled") : ", installed manually") + "). Choose a different outfit_name, or set replace_installed_id to replace a managed outfit.");
            }
            else
            {
                InstalledOutfit target = FindInstalled(installed, a.ReplaceInstalledId);
                if (target == null) errors.Add("Unknown replace_installed_id '" + a.ReplaceInstalledId + "'. Use an ID from list_installed.");
                else if (!target.Managed) errors.Add("'" + Label(target) + "' was installed manually; only outfits the Manager installed can be replaced. Install under a new name instead.");
                else if (!target.Enabled) errors.Add("'" + Label(target) + "' is disabled. Enable it before replacing it.");
                else if (!String.Equals(target.Character, a.Character, StringComparison.OrdinalIgnoreCase)) errors.Add("'" + Label(target) + "' belongs to " + target.Character + ", not " + a.Character + ".");
                else if (!String.Equals(target.Name, name, StringComparison.OrdinalIgnoreCase)) errors.Add("Replacing keeps the outfit's name. Use outfit_name '" + target.Name + "' (or an empty string) with this replace_installed_id.");
            }
            foreach (AssistantAction other in others ?? new AssistantAction[0])
            {
                if (other == a || other.State != AssistantActionState.Pending) continue;
                if (other.Kind == AssistantActionKind.Install && String.Equals(other.Character, a.Character, StringComparison.OrdinalIgnoreCase) && String.Equals(other.OutfitName, name, StringComparison.OrdinalIgnoreCase))
                    errors.Add("Queued action #" + other.Number + " already installs '" + other.OutfitName + "' for " + other.Character + ". Choose a different outfit_name.");
                if (!String.IsNullOrEmpty(a.ReplaceInstalledId) && (other.InstalledId == a.ReplaceInstalledId || other.InstalledIds.Contains(a.ReplaceInstalledId)))
                    errors.Add("Queued action #" + other.Number + " already changes that outfit.");
            }
            a.FileCount = plan.Files.Count; a.Bytes = plan.TotalBytes;
            return plan;
        }

        internal List<string> ValidateOther(AssistantAction a, IEnumerable<AssistantAction> others)
        {
            var errors = new List<string>();
            var pending = (others ?? new AssistantAction[0]).Where(o => o != a && o.State == AssistantActionState.Pending).ToList();
            switch (a.Kind)
            {
                case AssistantActionKind.SetEnabled:
                {
                    var rows = workspace.Installed(); InstalledOutfit row = FindInstalled(rows, a.InstalledId);
                    if (row == null) { errors.Add("Unknown installed_id '" + a.InstalledId + "'. Use an ID from list_installed."); break; }
                    a.InstalledNames = new List<string> { Label(row) };
                    if (!row.Managed) errors.Add("'" + Label(row) + "' was installed manually; only outfits the Manager installed can be enabled or disabled.");
                    else if (row.Enabled == a.Enabled) errors.Add("'" + Label(row) + "' is already " + (a.Enabled ? "enabled" : "disabled") + ".");
                    else if (a.Enabled && rows.Any(o => o != row && o.Enabled && String.Equals(o.Character, row.Character, StringComparison.OrdinalIgnoreCase) && String.Equals(o.Name, row.Name, StringComparison.OrdinalIgnoreCase)))
                        errors.Add("An enabled outfit named '" + row.Name + "' already exists for " + row.Character + ".");
                    foreach (var o in pending) if (o.InstalledId == a.InstalledId || o.InstalledIds.Contains(a.InstalledId) || o.ReplaceInstalledId == a.InstalledId) errors.Add("Queued action #" + o.Number + " already changes that outfit.");
                    break;
                }
                case AssistantActionKind.Uninstall:
                {
                    if (a.InstalledIds.Count == 0) { errors.Add("installed_ids is empty."); break; }
                    if (a.InstalledIds.Distinct().Count() != a.InstalledIds.Count) { errors.Add("installed_ids contains duplicates."); break; }
                    var rows = workspace.Installed(); var names = new List<string>();
                    foreach (string id in a.InstalledIds)
                    {
                        InstalledOutfit row = FindInstalled(rows, id);
                        if (row == null) errors.Add("Unknown installed_id '" + id + "'. Use an ID from list_installed.");
                        else names.Add(Label(row));
                    }
                    a.InstalledNames = names;
                    foreach (var o in pending) foreach (string id in a.InstalledIds) if (o.InstalledId == id || o.InstalledIds.Contains(id) || o.ReplaceInstalledId == id) errors.Add("Queued action #" + o.Number + " already changes " + id + ".");
                    break;
                }
                case AssistantActionKind.SavePreset:
                {
                    string name = (a.PresetName ?? "").Trim(); a.PresetName = name;
                    if (name.Length == 0) { errors.Add("Give the preset a name."); break; }
                    if (Directory.Exists(workspace.CostumeRoot) && OutfitSetStore.List(workspace.CostumeRoot).Entries.Any(e => e.Kind == OutfitSetKind.Preset && String.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase))) errors.Add("A preset named '" + name + "' already exists. Choose another name.");
                    foreach (var o in pending) if (o.Kind == AssistantActionKind.SavePreset && String.Equals(o.PresetName, name, StringComparison.OrdinalIgnoreCase)) errors.Add("Queued action #" + o.Number + " already saves a preset with that name.");
                    PresetScope? saveScope = PresetScopes.Parse(a.PresetScopeCode);
                    if (saveScope == null && !String.IsNullOrEmpty(a.PresetScopeCode)) { errors.Add("Unknown scope '" + a.PresetScopeCode + "'. Use extra_outfits, replacements or both."); break; }
                    a.PresetScopeCode = PresetScopes.Code(saveScope ?? PresetScope.Both);
                    if (saveScope == PresetScope.Replacements)
                    {
                        try { if (!workspace.Engine().LoadState().Mods.Any(ReplaceEngine.PresetCandidate)) errors.Add("No enabled replacements or tool mods to save (Slotsloader itself is never saved)."); }
                        catch (IOException ex) { errors.Add("The installed replacements can't be read: " + workspace.Scrub(ex.Message)); }
                    }
                    break;
                }
                case AssistantActionKind.RestorePreset:
                {
                    OutfitSetInfo set = FindPreset(a.PresetId);
                    if (set == null) { errors.Add("Unknown preset_id '" + a.PresetId + "'. Use an ID from list_presets."); break; }
                    a.PresetName = set.Name;
                    PresetScope? wanted = PresetScopes.Parse(a.PresetScopeCode);
                    if (wanted == null && !String.IsNullOrEmpty(a.PresetScopeCode)) { errors.Add("Unknown scope '" + a.PresetScopeCode + "'. Use extra_outfits, replacements or both."); break; }
                    PresetScope holds = set.Kind == OutfitSetKind.Removed ? PresetScope.ExtraOutfits : set.Scope;
                    if (wanted == PresetScope.Replacements && !PresetScopes.Replacements(holds)) { errors.Add("'" + set.Name + "' holds no replacements."); break; }
                    if (wanted == PresetScope.ExtraOutfits && !PresetScopes.Outfits(holds)) { errors.Add("'" + set.Name + "' holds no extra outfits."); break; }
                    a.PresetScopeCode = PresetScopes.Code(PresetScopes.Limit(wanted ?? PresetScope.Both, holds));
                    break;
                }
            }
            return errors;
        }

        internal OutfitSetInfo FindPreset(string presetId)
        {
            if (!Directory.Exists(workspace.CostumeRoot)) return null;
            return OutfitSetStore.List(workspace.CostumeRoot).Entries.FirstOrDefault(e => AssistantWorkspace.PresetId(e) == presetId);
        }

        // Runs one action now. Throws with a user-readable message on failure.
        internal string Execute(AssistantAction a)
        {
            OperationContext.ThrowIfCancellationRequested();
            string root = workspace.CostumeRoot;
            switch (a.Kind)
            {
                case AssistantActionKind.Install:
                {
                    List<string> errors;
                    CompositionPlan plan = ValidateInstall(a, null, true, out errors);
                    if (errors.Count > 0 || plan == null) throw new InvalidOperationException(String.Join(" ", errors));
                    InstallResult result = Installer.Install(plan, root, a.Character, a.OutfitName, workspace.SevenZip, !String.IsNullOrEmpty(a.ReplaceInstalledId));
                    return (String.IsNullOrEmpty(a.ReplaceInstalledId) ? "Installed '" : "Replaced '") + a.OutfitName + "' for " + a.Character + " (" + ReplacePlanner.N(result.FileCount, "file") + ", " + Utilities.FriendlyBytes(result.Bytes) + ").";
                }
                case AssistantActionKind.SetEnabled:
                {
                    var errors = ValidateOther(a, null);
                    if (errors.Count > 0) throw new InvalidOperationException(String.Join(" ", errors));
                    InstalledOutfit row = FindInstalled(workspace.Installed(), a.InstalledId);
                    Installer.SetEnabled(row, root, a.Enabled);
                    return (a.Enabled ? "Enabled '" : "Disabled '") + Label(row) + "'.";
                }
                case AssistantActionKind.Uninstall:
                {
                    var errors = ValidateOther(a, null);
                    if (errors.Count > 0) throw new InvalidOperationException(String.Join(" ", errors));
                    var rows = workspace.Installed();
                    var selected = a.InstalledIds.Select(id => FindInstalled(rows, id)).ToList();
                    // 1.0.1: uninstall means uninstall (no kept copy); an outfit with no source goes to the Recycle Bin.
                    OutfitSetStore.UninstallResult r = OutfitSetStore.UninstallOutfits(root, selected, RecycleBin.Current);
                    return "Uninstalled " + ReplacePlanner.N(r.Deleted + r.Recycled, "outfit") + (r.Recycled > 0 ? " (" + r.Recycled + " without a source went to the Recycle Bin)" : "") + (r.Kept.Count > 0 ? ". Still installed: " + String.Join("; ", r.Kept) : "") + ".";
                }
                case AssistantActionKind.SavePreset:
                {
                    var errors = ValidateOther(a, null);
                    if (errors.Count > 0) throw new InvalidOperationException(String.Join(" ", errors));
                    PresetScope saveScope = PresetScopes.Parse(a.PresetScopeCode) ?? PresetScope.Both;
                    OutfitSetInfo set = OutfitSetStore.CreatePreset(root, a.PresetName, saveScope, PresetScopes.Replacements(saveScope) ? workspace.Engine() : null);
                    a.PresetId = AssistantWorkspace.PresetId(set);
                    return "Saved preset '" + set.Name + "' with " + ReplacePlanner.N(set.OutfitCount, "outfit") + (set.ReplacementCount > 0 ? " and " + ReplacePlanner.N(set.ReplacementCount, "replacement") : "") + " (" + a.PresetId + ").";
                }
                case AssistantActionKind.ReplaceInstall:
                {
                    List<string> errors;
                    CompositionPlan plan = ValidateReplaceInstall(a, null, true, out errors);
                    if (errors.Count > 0 || plan == null) throw new InvalidOperationException(String.Join(" ", errors));
                    ReplaceEngine engine = workspace.Engine();
                    string detail;
                    if (engine.CurrentMethod(engine.LoadState()) == ReplaceMethods.Loose && plan.Files.Keys.Any(k => k.StartsWith("natives/", StringComparison.OrdinalIgnoreCase)) && LooseLoader.State(engine.GameRoot, out detail) != LooseLoaderState.Enabled)
                        throw new InvalidOperationException("REFramework's loose file loader is off, so a replacement would be ignored. Turn it on in Settings → Game files & replacements (the Assistant cannot change settings), or choose the PAK method there.");
                    ReplacePlan prepared;
                    ReplaceMod mod = ReplacePlanner.PrepareAndInstall(plan, workspace.SevenZip, engine, a.ModName + (a.OptionNames.Count > 1 ? " · " + String.Join(" + ", a.OptionNames.Skip(1).Take(3)) : ""), false, String.IsNullOrEmpty(a.ReplacementId) ? null : FindReplacement(engine.LoadState(), a.ReplacementId).Id, out prepared);
                    a.ReplacementId = AssistantWorkspace.ReplacementId(mod);
                    return "Replace original: installed '" + mod.Name + "' (" + ReplacePlanner.N(mod.Files.Count, "file") + ", " + prepared.Fixed + " auto-fixed" + (prepared.Affects.Count > 0 ? ", replaces " + prepared.AffectsText : "") + ").";
                }
                case AssistantActionKind.GameModChange:
                {
                    var errors = ValidateGameModChange(a, null);
                    if (errors.Count > 0) throw new InvalidOperationException(String.Join(" ", errors));
                    ReplaceEngine engine = workspace.Engine();
                    if (a.GameAction == "reapply_all") return "Reapplied every replacement. " + engine.Reapply();
                    if (a.GameAction == "restore_original_game_files") return "Restored the original game files. " + engine.RestoreAll();
                    ReplaceState current = engine.LoadState();
                    ReplaceMod target = FindReplacement(current, a.ReplacementId); string id = target.Id;
                    if (a.GameAction == "enable" || a.GameAction == "disable") engine.SetEnabled(id, a.GameAction == "enable");
                    else if (a.GameAction == "move_up" || a.GameAction == "move_down") engine.Move(id, a.GameAction == "move_up" ? -1 : 1);
                    else
                    {
                        // 2.4.1: as the app's one-click Uninstall: every record of that mod's source goes (1.0.1: no kept copy).
                        var ids = current.Mods.Where(m => m.Id == id || !m.Test && ReplacePlanner.SameSource(m.SourcePath, target.SourcePath)).Select(m => m.Id).ToList();
                        foreach (string each in ids) engine.UninstallForGood(each, DriftChoice.Ask, null);
                        if (ids.Count > 1) return "Uninstall '" + String.Join("", a.InstalledNames) + "' (" + ids.Count + " installs of this mod): done.";
                    }
                    return GameActionLabel(a.GameAction) + " '" + String.Join("", a.InstalledNames) + "': done.";
                }
                case AssistantActionKind.PresetImport:
                {
                    if (!File.Exists(a.PresetPath)) throw new InvalidOperationException("The preset file is no longer available.");
                    var mappings = FluffyPresets.Group(FluffyPresets.Map(FluffyPresets.Parse(File.ReadAllText(a.PresetPath)), workspace.Library(), workspace.SevenZip));
                    foreach (PresetMapping m in mappings) if (a.PresetMode == "extra_outfit" && m.Kind != "Tool" && !String.IsNullOrEmpty(m.Character)) m.Mode = InstallModes.Slot;
                    var selected = mappings.Where(m => m.Entry != null).ToList();
                    string summary = PresetInstaller.Install(workspace.Config, workspace.Engine(), selected);
                    int missing = mappings.Count(m => m.Entry == null);
                    return summary.TrimStart('✓', ' ') + (missing > 0 ? " " + missing + (missing == 1 ? " mod from the preset is" : " mods from the preset are") + " not in your libraries." : "");
                }
                default:
                {
                    var errors = ValidateOther(a, null);
                    if (errors.Count > 0) throw new InvalidOperationException(String.Join(" ", errors));
                    OutfitSetInfo set = FindPreset(a.PresetId);
                    if (set == null) throw new InvalidOperationException("The preset no longer exists.");
                    PresetScope scope = PresetScopes.Parse(a.PresetScopeCode) ?? PresetScope.ExtraOutfits;
                    ReplaceEngine engine = null;
                    if (PresetScopes.Replacements(scope))
                    {
                        engine = workspace.Engine();
                        // Same rule as install_replacement: the Assistant can't turn the loose file loader on.
                        OutfitSetPreview preview = OutfitSetStore.Preview(root, set, scope, engine);
                        if (preview.AddReplacements.Count > 0 && engine.LooseLoaderOffFor(preview.InstallsNatives))
                            throw new InvalidOperationException("REFramework's loose file loader is off, so restored replacements would be ignored. Turn it on in Settings → Game files & replacements (the Assistant cannot change settings), or choose the PAK method there.");
                    }
                    OutfitSetRestoreResult result = OutfitSetStore.Restore(root, set, scope, engine);
                    string text = "Restored '" + set.Name + "'";
                    if (PresetScopes.Outfits(scope)) text += ": " + result.Restored + " added, " + result.Skipped + " already present";
                    if (PresetScopes.Replacements(scope))
                        text += (PresetScopes.Outfits(scope) ? "; replacements: " : ": replacements: ") + result.ReplacementsRestored + " installed, " + result.ReplacementsSkipped + " identical skipped"
                            + (result.ReplacementConflicts.Count > 0 ? ", skipped " + String.Join(", ", result.ReplacementConflicts.Select(n => "'" + n + "'")) + " (a different version is installed)" : "");
                    return text + "." + (String.IsNullOrEmpty(result.ReplaceMessage) ? "" : " " + result.ReplaceMessage);
                }
            }
        }

        // Approved actions run in order on the caller's worker thread; the batch
        // stops at the first failure.
        internal AssistantBatchResult RunBatch(IList<AssistantAction> actions, CancellationToken token, Action<OperationProgress> progress, Action<AssistantAction> changed)
        {
            var batch = new AssistantBatchResult { Requested = actions.Count };
            batch.Actions.AddRange(actions);
            bool stop = false;
            using (OperationContext.Begin(token, progress))
            {
                foreach (AssistantAction a in actions)
                {
                    if (stop || token.IsCancellationRequested)
                    {
                        if (token.IsCancellationRequested && !stop) batch.Cancelled = true;
                        a.State = AssistantActionState.NotRun; a.Result = token.IsCancellationRequested ? "Not run: cancelled." : "Not run: an earlier action failed.";
                        if (changed != null) changed(a); stop = true; continue;
                    }
                    a.State = AssistantActionState.Running; if (changed != null) changed(a);
                    try
                    {
                        a.Result = Execute(a); a.State = AssistantActionState.Succeeded; batch.Succeeded++;
                    }
                    catch (OperationCanceledException)
                    {
                        a.State = AssistantActionState.NotRun; a.Result = "Cancelled; nothing changed."; batch.Cancelled = true; stop = true;
                    }
                    catch (Exception ex)
                    {
                        if (ex is OutOfMemoryException || ex is StackOverflowException) throw;
                        a.State = AssistantActionState.Failed; a.Result = workspace.Scrub(ex.Message); batch.FailedAction = a; stop = true;
                    }
                    if (changed != null) changed(a);
                }
            }
            return batch;
        }
    }
}
