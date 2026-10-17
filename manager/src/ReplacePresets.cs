using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SlotsInstaller
{
    // 2.4.1: what restoring a preset's replacements would do (or did), by index into the saved list.
    internal sealed class PresetReplacementPlan
    {
        internal readonly List<int> Missing = new List<int>(), Identical = new List<int>(), Conflicts = new List<int>();
        internal readonly Dictionary<int, string> Matched = new Dictionary<int, string>();   // identical: saved index -> installed id
        internal bool InstallsNatives;
    }

    // 2.4.1: presets can hold replacements. Saved from the store (the composed files the engine keeps), so a preset
    // never depends on library paths, and restored through Change like every other install: SF6 closed, the Fluffy
    // PAK-method rule, journal and rollback. SlotsLoader itself is never saved (the pin/update system owns it).
    internal sealed partial class ReplaceEngine
    {
        // Enabled replacements and tool mods in priority order (0 = highest), minus SlotsLoader.
        internal static bool PresetCandidate(ReplaceMod m) { return m != null && m.Enabled && m.Files != null && m.Files.Count > 0 && !LoaderCostumePak.IsLoaderMod(m); }

        // Runs `body` under the game lock with the current state and the mods a preset saves (copy their store files
        // with StoreFile(state, mod, file) inside it). Refused while an interrupted change waits for its rollback.
        internal T WithPresetSnapshot<T>(Func<ReplaceState, List<ReplaceMod>, string, T> body)
        {
            using (Lock())
            {
                if (JournalPending) throw new IOException("An interrupted game-file change is pending. Refresh the Installed page, then try again.");
                ReplaceState state = LoadState();
                List<ReplaceMod> mods = state.Mods.Where(PresetCandidate).ToList();
                foreach (ReplaceMod m in mods)
                    foreach (ModFile f in m.Files)
                    {
                        string stored = StoreFile(state, m, f);
                        Safety.NoReparseChain(stored);
                        if (!File.Exists(stored)) throw new IOException("The stored copy of '" + m.Name + "' is missing (" + f.Path + "). Reinstall it, then save.");
                    }
                return body(state, mods, CurrentMethod(state));
            }
        }

        // Same files (paths and hashes) = the same replacement, whatever its name.
        internal static bool SameFiles(ReplaceMod a, ReplaceMod b)
        {
            if (a == null || b == null || a.Files == null || b.Files == null || a.Files.Count != b.Files.Count) return false;
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (ModFile f in a.Files) { if (map.ContainsKey(f.Path)) return false; map[f.Path] = f.Sha256 ?? ""; }
            foreach (ModFile f in b.Files) { string sha; if (!map.TryGetValue(f.Path, out sha) || !String.Equals(sha, f.Sha256 ?? "", StringComparison.OrdinalIgnoreCase)) return false; }
            return true;
        }

        private static PresetReplacementPlan ClassifyPreset(ReplaceState state, IList<ReplaceMod> saved)
        {
            var plan = new PresetReplacementPlan();
            for (int i = 0; i < saved.Count; i++)
            {
                ReplaceMod same = state.Mods.FirstOrDefault(m => SameFiles(m, saved[i]));
                if (same != null) { plan.Identical.Add(i); plan.Matched[i] = same.Id; }
                else if (state.Mods.Any(m => String.Equals((m.Name ?? "").Trim(), (saved[i].Name ?? "").Trim(), StringComparison.OrdinalIgnoreCase))) plan.Conflicts.Add(i);
                else plan.Missing.Add(i);
            }
            plan.InstallsNatives = plan.Missing.Any(i => saved[i].Files.Any(f => f.Path.StartsWith("natives/", StringComparison.OrdinalIgnoreCase)));
            return plan;
        }

        // Read-only preview for the confirmation.
        internal PresetReplacementPlan PlanPresetReplacements(IList<ReplaceMod> saved) { return ClassifyPreset(LoadState(), saved); }

        // True when restoring would put natives files in place with the loose method while the loader is off: the
        // window then asks like any install (MainForm.EnsureLooseLoader); the Assistant refuses.
        internal bool LooseLoaderOffFor(bool installsNatives)
        {
            if (!installsNatives || CurrentMethod(LoadState()) != ReplaceMethods.Loose) return false;
            string detail; return LooseLoader.State(GameRoot, out detail) != LooseLoaderState.Enabled;
        }

        // Installs the missing saved replacements in their saved order (each next to its saved neighbours that are
        // already installed; on top when none are), skips identical ones and same-named ones with other contents.
        // `stage(index, storeDir)` copies saved index's files into its new store folder (verified by the caller).
        internal string RestorePresetReplacements(IList<ReplaceMod> saved, Action<int, string> stage, out PresetReplacementPlan done)
        {
            PresetReplacementPlan preview = PlanPresetReplacements(saved);
            done = preview;
            if (preview.Missing.Count == 0) return "";
            GameProcesses.RequireGameClosed("restore replacements");
            var created = new List<string>(); PresetReplacementPlan plan = null; bool committed = false;
            try
            {
                string result = Change("Restore replacements", true, delegate(ReplaceState s)
                {
                    plan = ClassifyPreset(s, saved);
                    string root = StoreRoot(s);
                    var placed = new Dictionary<int, string>(plan.Matched);
                    for (int n = 0; n < plan.Missing.Count; n++)
                    {
                        int i = plan.Missing[n];
                        OperationContext.Report("Restoring replacement " + (n + 1) + " of " + plan.Missing.Count, n, plan.Missing.Count);
                        ReplaceMod source = saved[i];
                        var mod = new ReplaceMod
                        {
                            Id = Guid.NewGuid().ToString("N"), Name = source.Name, SourcePath = source.SourcePath ?? "", Kind = source.Kind ?? "", InstalledUtc = DateTime.UtcNow.ToString("o"),
                            Description = source.Description ?? "", SelectedIds = (source.SelectedIds ?? new List<string>()).ToList(), Enabled = source.Enabled, Test = false,
                            Files = source.Files.Select(f => new ModFile { Path = f.Path, Sha256 = f.Sha256, Size = f.Size, Fixes = f.Fixes ?? "" }).ToList(),
                            Notes = (source.Notes ?? new List<string>()).ToList(), Affects = source.Affects ?? ""
                        };
                        foreach (ModFile f in mod.Files) ValidateDestination(f.Path);
                        string storeDir = Safety.Under(root, mod.Id);
                        if (Directory.Exists(storeDir) || File.Exists(storeDir)) throw new IOException("The replacement store already holds " + storeDir + ".");
                        created.Add(storeDir);
                        stage(i, storeDir);
                        foreach (ModFile f in mod.Files)
                        {
                            string stored = Safety.Under(storeDir, f.Path);
                            if (!File.Exists(stored) || new FileInfo(stored).Length != f.Size) throw new IOException("The restored copy of " + f.Path + " is missing or changed.");
                        }
                        s.Mods.Insert(Position(s, saved.Count, placed, i), mod); placed[i] = mod.Id;
                    }
                    if (String.IsNullOrEmpty(s.StoreRoot)) s.StoreRoot = root;
                    return s;
                }, false, null);
                committed = true; done = plan;
                return result;
            }
            finally
            {
                if (!committed && created.Count > 0)
                {
                    HashSet<string> kept;
                    try { kept = new HashSet<string>(LoadState().Mods.Select(m => m.Id), StringComparer.OrdinalIgnoreCase); }
                    catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); kept = null; }
                    if (kept != null)
                        foreach (string dir in created)
                            if (!kept.Contains(Path.GetFileName(dir)) && Directory.Exists(dir)) { try { Directory.Delete(dir, true); } catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); } }
                }
            }
        }

        // After the nearest earlier saved replacement already in the list, else before the nearest later one, else on top.
        private static int Position(ReplaceState s, int count, Dictionary<int, string> placed, int index)
        {
            string id;
            for (int j = index - 1; j >= 0; j--)
                if (placed.TryGetValue(j, out id)) { int at = s.Mods.FindIndex(m => m.Id == id); if (at >= 0) return at + 1; }
            for (int j = index + 1; j < count; j++)
                if (placed.TryGetValue(j, out id)) { int at = s.Mods.FindIndex(m => m.Id == id); if (at >= 0) return at; }
            return 0;
        }
    }
}
