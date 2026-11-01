using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SlotsInstaller
{
    internal sealed class PresetLayer
    {
        internal ModPackage Package;
        internal string Id, Name, Stamp;
        internal int Order;

        internal CompositionPlan Payload()
        {
            ModComponent component = Package.Find(Id);
            if (component == null || component.IsDummy || !component.HasFiles(true))
                throw new IOException("The preset option '" + Name + "' is missing. Review the preset again.");
            var plan = new CompositionPlan { Package = Package, ReplaceMode = true };
            plan.Layers.Add(component);
            foreach (NativeAsset asset in component.Assets.Concat(component.RootAssets))
                plan.Files[asset.DestinationPath] = new PlannedFile { Component = component, Asset = asset };
            return plan;
        }

        internal static List<PresetLayer> Ordered(List<PresetMapping> groups)
        {
            var units = new List<PresetLayer>();
            foreach (PresetMapping group in groups)
            {
                CompositionPlan validation = Composer.Plan(group.Package, group.OptionIds, true);
                if (!validation.CanInstall) throw new IOException(group.DisplayName + ": " + String.Join(" ", validation.Errors));
                var explicitIds = new HashSet<string>(group.Sections.SelectMany(s => s.ExplicitIds), StringComparer.OrdinalIgnoreCase);
                var placed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (FluffyPresetEntry section in group.Sections.OrderBy(s => s.Order))
                {
                    // A named parent keeps its own preset position. An omitted parent is supplied once, with its first child.
                    foreach (string id in section.RequiredIds.Where(id => !explicitIds.Contains(id)).Concat(section.ExplicitIds))
                    {
                        if (!placed.Add(id)) continue;
                        ModComponent c = group.Package.Find(id);
                        units.Add(new PresetLayer { Package = group.Package, Id = id, Order = section.Order, Stamp = group.Stamp,
                            Name = PresetInstaller.CleanName(id.Length == 0 && section.ModName.Length > 0 ? section.ModName : c.Name) });
                    }
                }
            }
            return units.OrderBy(u => u.Order).ToList();
        }
    }

    internal sealed partial class ReplaceEngine
    {
        // All replacement layers enter as one highest-priority block. The store GUID is physical;
        // source + component tracks a layer across reimports, including after a library relink.
        internal List<ReplaceMod> ImportLayers(List<PresetLayer> layers, string sevenZip, ModMigration migration = null)
        {
            var imported = new List<ReplaceMod>();
            if (layers.Count == 0) return imported;
            string preparationId = Guid.NewGuid().ToString("N");
            using (Lock())
            try
            {
                string method = migration == null ? null : migration.InstallMethod(this,LoadState());
                var options = new ChangeOptions();
                if (migration != null) options.MigrationOriginals = new Dictionary<string,string>(migration.Originals,StringComparer.OrdinalIgnoreCase);
                Change("Import ordered preset replacements", true, delegate(ReplaceState state)
                {
                    GameProcesses.RequireGameClosed("import preset replacements");
                    if (JournalPending) throw new IOException("Finish the interrupted change before importing a preset.");
                    var legacy = new List<ReplaceMod>();
                    foreach (var source in layers.GroupBy(u => u.Package.SourcePath, StringComparer.OrdinalIgnoreCase))
                    {
                        string stamp = MainForm.SourceStamp(source.Key);
                        if (stamp == null || stamp.StartsWith("unreadable:") || source.Any(u => u.Stamp != stamp))
                            throw new IOException("The source changed. Review the preset again: " + source.Key);
                        var existing = state.Mods.Where(m => !m.Test && !m.PresetLayer && ReplacePlanner.SameSource(m.SourcePath, source.Key)).ToList();
                        var selected = new HashSet<string>(source.Select(u => u.Id), StringComparer.OrdinalIgnoreCase);
                        if (existing.Count > 1 || existing.Any(m => !selected.SetEquals(m.SelectedIds)))
                            throw new IOException("Different options from '" + Path.GetFileName(source.Key) + "' are installed. Uninstall that replacement or skip the archive in this preset.");
                        legacy.AddRange(existing);
                    }
                    // SF6 reads mod PAKs by their numbered filenames. Reuse the selected records' numbers in
                    // ascending preset order and reserve new numbers above every existing patch.
                    var selectedRecords = state.Mods.Where(m => legacy.Contains(m) || (m.PresetLayer && layers.Any(u =>
                        ReplacePlanner.SameSource(m.SourcePath, u.Package.SourcePath) && m.SelectedIds.Contains(u.Id, StringComparer.OrdinalIgnoreCase)))).ToList();
                    var occupied = new HashSet<string>(state.Mods.Except(selectedRecords).SelectMany(m => m.Files).Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
                    var available = new HashSet<string>(selectedRecords.SelectMany(m => m.Files).Select(f => f.Path)
                        .Where(p => IsPatchPakPath(p) && !occupied.Contains(p)), StringComparer.OrdinalIgnoreCase);
                    int highestOutside = Directory.EnumerateFiles(GameRoot, "re_chunk_000.pak.patch_*.pak").Select(Path.GetFileName)
                        .Concat(occupied).Where(p => IsPatchPakPath(p) && !available.Contains(p)).Select(PatchNumber).DefaultIfEmpty(0).Max();
                    var patchSlots = new Queue<string>(available.Where(p => PatchNumber(p) > highestOutside).OrderBy(PatchNumber));
                    int nextPatch = NextPatchNumber(state, null);
                    Func<string> reservePatch = delegate { return patchSlots.Count > 0 ? patchSlots.Dequeue() : PatchPakName(nextPatch++); };
                    // Record all temporary locations before extraction. A process exit during preparation has
                    // no game operations to undo. Execute atomically replaces this with the deployment journal.
                    string root = StoreRoot(state);
                    string stage = Path.Combine(root, ".staging-" + preparationId);
                    var ids = layers.Select(u => Guid.NewGuid().ToString("N")).ToList();
                    var prep = new ReplaceJournal { OperationId = preparationId, Description = "Prepare preset replacements",
                        StartedUtc = DateTime.UtcNow.ToString("o"), NewGeneration = state.Generation + 1 };
                    prep.CreatedPaths.Add(stage);
                    prep.CreatedPaths.AddRange(ids.Select(id => Safety.Under(root, id)));
                    if (prep.CreatedPaths.Any(p => File.Exists(p) || Directory.Exists(p))) throw new IOException("The staging path already exists. Try again.");
                    Directory.CreateDirectory(DataDir);
                    DurableFile.WriteJson(JournalPath, prep);
                    for (int i = 0; i < layers.Count; i++)
                    {
                        OperationContext.ThrowIfCancellationRequested();
                        PresetLayer unit = layers[i];
                        OperationContext.Report("Preparing " + unit.Name, i, layers.Count);
                        string source = unit.Package.SourcePath;
                        var matches = state.Mods.Where(m => !m.Test && ReplacePlanner.SameSource(m.SourcePath, source) &&
                            (m.PresetLayer || legacy.Contains(m)) && m.SelectedIds.Count == 1 &&
                            String.Equals(m.SelectedIds[0], unit.Id, StringComparison.OrdinalIgnoreCase)).ToList();
                        if (matches.Count > 1) throw new IOException("Multiple installed records match '" + unit.Name + "'. Review them on the Installed page.");
                        ReplaceMod old = matches.SingleOrDefault();
                        CompositionPlan payload = unit.Payload();
                        if (old == null && ReplacePlanner.IsLoaderComposition(payload)) old = state.Mods.FirstOrDefault(LoaderCostumePak.IsLoaderMod);
                        if (old == null && ReplacePlanner.IsReFrameworkComposition(payload)) old = state.Find(ReplacePlanner.ReFrameworkUpdateTarget(state.Mods, payload.Files.Keys));
                        if (old != null && imported.Contains(old)) throw new IOException("This preset selects multiple builds of the same tool. Select one.");
                        string folder = null;
                        try
                        {
                            ReplacePlan plan = ReplacePlanner.PrepareCore(payload, sevenZip, this, stage, null, old == null ? null : old.Id, state, reservePatch, migration == null ? null : migration.Rows[i], method);
                            if (!plan.CanInstall) throw new IOException(unit.Name + ": " + String.Join(" ", plan.Errors));
                            ReplaceMod mod = PublishPrepared(plan, unit.Name, false, state, out folder, ids[i]);
                            // An unchanged reimport retains the installed store and its ID, including references held by the UI.
                            if (old != null && ReplacePlanner.SameSource(old.SourcePath, source) && SameFiles(old, mod) &&
                                old.Files.All(f => PayloadMatches(state, old, f)))
                            {
                                Directory.Delete(folder, true); folder = null; mod = old;
                            }
                            if (old != null) { state.Mods.Remove(old); legacy.Remove(old); }
                            // Loader and REFramework use their ordinary updater and singleton identity.
                            mod.PresetLayer = !ReplacePlanner.IsLoaderComposition(payload) && !ReplacePlanner.IsPlainReFramework(payload.Files.Keys);
                            mod.Enabled = true;
                            state.Mods.Insert(0, mod); imported.Add(mod);
                        }
                        finally
                        {
                            if (Directory.Exists(stage)) Directory.Delete(stage, true);
                        }
                    }
                    foreach (var source in layers.GroupBy(u => u.Package.SourcePath, StringComparer.OrdinalIgnoreCase))
                        if (MainForm.SourceStamp(source.Key) != source.First().Stamp) throw new IOException("The source changed during import: " + source.Key);
                    foreach (ReplaceMod old in legacy) state.Mods.Remove(old);
                    OperationContext.ThrowIfCancellationRequested();
                    if (migration != null)
                    {
                        string wait = migration.Waiting(this);
                        if (wait != null) throw new IOException(wait);
                        state.LastMigration = migration.Id;
                    }
                    return state;
                }, options, method, null);
            }
            catch
            {
                // Validation, cancellation or allocation failed before game publication. A deployment journal
                // from a simulated crash or failed rollback stays intact for normal recovery.
                if (JournalPending && ReadJsonShared<ReplaceJournal>(JournalPath).OperationId == preparationId) Recover();
                throw;
            }
            if (imported.Any(LoaderCostumePak.IsLoaderMod)) SwapRepairFlag.Ensure(GameRoot);
            return imported;
        }
    }
}
