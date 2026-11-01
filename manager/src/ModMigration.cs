using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SlotsInstaller
{
    public sealed class MigrationRow
    {
        public string Name, Section, Type, Source, Component, Stamp, Problem;
        public List<string> Files = new List<string>();
        public Dictionary<string,string> PayloadHashes = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);

        // Keep hashes under the original installed names, before suffix upgrades or byte repairs.
        internal void CheckSourceBytes(CompositionPlan payload, Dictionary<string,PlannedFile> chosen, Dictionary<string,string> staged,
            SourceReader reader, string staging, bool preview)
        {
            var byEntry = chosen.ToDictionary(p => p.Value.Asset.EntryPath,p => staged[p.Key],StringComparer.OrdinalIgnoreCase);
            var hashes = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            var skipped = new List<PlannedFile>();
            foreach (var file in payload.Files.Values)
            {
                string hash;
                if (byEntry.TryGetValue(file.Asset.EntryPath,out hash)) hashes.Add(FluffyInstalls.Key(file.Asset.DestinationPath),hash);
                else skipped.Add(file);
            }
            // Both an obsolete and a current file may be installed externally, but only the current one is staged.
            if (skipped.Count > 0)
            {
                string scratch = Safety.NewFolder(staging,".migration-");
                try
                {
                    foreach (var file in reader.CopyFiles(skipped,Path.Combine(scratch,"files"),scratch))
                        hashes.Add(FluffyInstalls.Key(file.Key),file.Value);
                }
                finally { Directory.Delete(scratch,true); }
            }
            if (preview) { PayloadHashes = hashes; return; }
            string expected;
            if (PayloadHashes == null || hashes.Count != PayloadHashes.Count ||
                hashes.Any(f => !PayloadHashes.TryGetValue(f.Key,out expected) || f.Value != expected))
                throw new IOException("The reviewed source contents changed: " + Source);
        }
    }

    public sealed class ModMigration
    {
        public string Id, Root, Game, ManifestHash;
        public bool CompatibilityChecked;
        public List<MigrationRow> Rows = new List<MigrationRow>();
        public Dictionary<string,string> Originals = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        private ILookup<string,MigrationRow> owners;
        private ILookup<string,MigrationRow> Owners
        {
            get
            {
                return owners ?? (owners = Rows.SelectMany(r => r.Files.Select(f => new { File=f, Row=r }))
                    .ToLookup(p => p.File,p => p.Row,StringComparer.OrdinalIgnoreCase));
            }
        }

        internal static string Pending(ReplaceEngine e) { return Path.Combine(e.DataDir,"pending-migration.json"); }

        internal static bool Supports(string type)
        {
            var m = new FluffyMod { InstallType = type };
            return m.CopiedFiles || String.Equals(type,"invalidate",StringComparison.OrdinalIgnoreCase);
        }

        private static List<FluffyMod> ReadActive(ReplaceEngine e, string savedRoot, out Dictionary<string,string> manifests)
        {
            var external = e.Fluffy();
            if (external.Warnings.Count > 0) throw new IOException(String.Join(" ",external.Warnings));
            manifests = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            var mods = new List<FluffyMod>();
            foreach (string root in external.Roots.Concat(new[] {savedRoot}).Where(r => r != null).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                byte[] bytes = FluffyInstalls.ReadShared(Path.Combine(root,"installed.ini"),true);
                if (bytes == null) continue;
                var parsed = FluffyInstalls.Parse(FluffyInstalls.DecodeIni(bytes),root,true);
                if (parsed.Count == 0) continue;
                manifests.Add(root,ReplaceEngine.Sha(bytes));
                mods.AddRange(parsed);
            }
            return mods;
        }

        internal static ModMigration Scan(ReplaceEngine e, List<LibraryEntry> library, string sevenZip)
        {
            Dictionary<string,string> manifests;
            var mods = ReadActive(e,null,out manifests);
            if (manifests.Count != 1) throw new IOException(manifests.Count == 0 ? "No active SF6 mods were found. Add the other manager’s SF6 Mods folder in Settings." :
                "More than one setup lists active mods. Their combined install order cannot be verified.");
            string root = mods[0].Root;
            var entries = mods.Select(m => new FluffyPresetEntry { Section=m.Section, ModName=m.Name, ModId=m.ModId, Order=m.Index }).ToList();
            var matches = FluffyPresets.Map(entries,library,sevenZip);
            var plan = new ModMigration { Id=Guid.NewGuid().ToString("N"), Root=root, Game=e.GameRoot, ManifestHash=manifests[root] };
            for (int i=0; i<mods.Count; i++)
            {
                var m = mods[i];
                var row = new MigrationRow { Name=m.Name, Section=m.Section, Type=m.InstallType,
                    Files=m.Files.Select(FluffyInstalls.Key).Distinct(StringComparer.OrdinalIgnoreCase).ToList() };
                plan.Rows.Add(row);
                if (String.Equals(row.Type,"pakcopy_reengine_invalidate",StringComparison.OrdinalIgnoreCase) || row.Files.Any(ReplaceEngine.IsModPak))
                    row.Problem = "This mod uses a numbered patch PAK. Its install order cannot be verified for migration.";
                else if (!Supports(row.Type)) row.Problem = "Unsupported install method: " + row.Type;
                else if (matches[i].Package == null) row.Problem = matches[i].Ambiguous ? "Multiple sources match. Locate the archive or folder used by the other manager." : matches[i].Message;
                else Match(row,matches[i].Package,entries[i].ExplicitIds);
            }
            return plan;
        }

        internal static void Match(MigrationRow row, ModPackage package, List<string> suggested)
        {
            if (!Supports(row.Type) || row.Files.Any(ReplaceEngine.IsModPak)) return;
            var recorded = new HashSet<string>(row.Files,StringComparer.OrdinalIgnoreCase);
            var choices = package.Components.Where(c => !c.IsDummy && c.HasFiles(true) &&
                recorded.SetEquals(c.Assets.Concat(c.RootAssets).Select(a => FluffyInstalls.Key(a.DestinationPath)))).ToList();
            if (choices.Count > 1)
                choices = choices.Where(c => suggested != null ? suggested.Contains(c.Id,StringComparer.OrdinalIgnoreCase) :
                    String.Equals(c.Name,row.Name,StringComparison.OrdinalIgnoreCase) ||
                    String.Equals(c.Id.Split('/').Last(),row.Section,StringComparison.OrdinalIgnoreCase)).ToList();
            row.Source = null;
            row.Problem = choices.Count == 1 ? null : "Could not verify a unique option with the recorded installed files.";
            if (choices.Count != 1) return;
            row.Source = package.SourcePath; row.Component = choices[0].Id; row.Stamp = MainForm.SourceStamp(row.Source);
        }

        internal string SharedDetails(MigrationRow row)
        {
            var lines = row.Files.Where(p => Owners[p].Count() > 1).Select(p => p + "  →  " + Owners[p].Last().Name);
            string details = String.Join(Environment.NewLine,lines);
            if (row.Files.Any(p => Owners[p].Last() != row))
                details += Environment.NewLine + "Covered files cannot be read from the game. Their bytes must match the recorded option in the other manager’s Mods folder.";
            return details;
        }

        // A winning loose file proves nothing about the version of a layer underneath it.
        // Resolve that layer independently in the external library, even after a manual Locate.
        private void VerifyCoveredSources(string sevenZip)
        {
            var covered = Rows.Where(r => r.Files.Any(f => Owners[f].Last() != r)).ToList();
            if (covered.Count == 0) return;
            var entries = covered.Select(r => new FluffyPresetEntry { Section=r.Section, ModName=r.Name }).ToList();
            var library = Library.Scan(new[] {Path.Combine(Root,"Mods")},sevenZip);
            var matches = FluffyPresets.Map(entries,library,sevenZip);
            for (int i=0; i<covered.Count; i++)
            {
                var row = covered[i]; var match = matches[i];
                var reference = new MigrationRow { Section=row.Section, Name=row.Name, Type=row.Type, Files=row.Files };
                if (match.Package != null) Match(reference,match.Package,entries[i].ExplicitIds);
                string section = row.Section.Replace('\\','/');
                if (reference.Source == null || !(String.Equals(Path.GetFileName(reference.Source),section,StringComparison.OrdinalIgnoreCase) ||
                    (File.Exists(reference.Source) && String.Equals(Path.GetFileNameWithoutExtension(reference.Source),section,StringComparison.OrdinalIgnoreCase)) ||
                    String.Equals(reference.Component,section,StringComparison.OrdinalIgnoreCase) ||
                    String.Equals(reference.Component.Split('/').Last(),section,StringComparison.OrdinalIgnoreCase)))
                    throw new IOException("Cannot verify the covered files for " + row.Name + ". Restore its recorded archive or option in the other manager’s Mods folder.");
                if (ReplacePlanner.SameSource(row.Source,reference.Source) && row.Component == reference.Component) continue;
                var original = new PresetLayer { Package=match.Package, Id=reference.Component, Name=row.Name };
                var files = original.Payload().Files.Values.Where(f => Owners[FluffyInstalls.Key(f.Asset.DestinationPath)].Last() != row);
                var selectedHashes = new Dictionary<string,string>(row.PayloadHashes,StringComparer.OrdinalIgnoreCase);
                string scratch = Safety.NewFolder(Path.GetTempPath(),"slm-");
                try
                {
                    using (var reader = new SourceReader(reference.Source,sevenZip))
                        foreach (var file in reader.CopyFiles(files,Path.Combine(scratch,"files"),scratch))
                        {
                            string expected;
                            if (!selectedHashes.TryGetValue(FluffyInstalls.Key(file.Key),out expected) || expected != file.Value)
                                throw new IOException(row.Name + " differs from its recorded source for the covered file " + file.Key + ". Locate " + reference.Source + ".");
                        }
                }
                finally { Directory.Delete(scratch,true); }
            }
        }

        private void RequireSnapshot(ReplaceEngine e)
        {
            Dictionary<string,string> manifests;
            ReadActive(e,Root,out manifests);
            string hash;
            if (manifests.Count != 1 || !manifests.TryGetValue(Root,out hash) || hash != ManifestHash)
                throw new IOException("The active setup changed. Close the preview and scan again.");
        }

        private static bool Within(string path,string root)
        {
            string prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return (Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar).StartsWith(prefix,StringComparison.OrdinalIgnoreCase);
        }

        internal string InstallMethod(ReplaceEngine e, ReplaceState state)
        {
            string method = e.CurrentMethod(state);
            if (!Rows.Any(r => r.Files.Any(f => f.StartsWith("natives/",StringComparison.OrdinalIgnoreCase)))) return method;
            bool automatic = String.IsNullOrEmpty(e.Settings.Method) && state.Mods.Count == 0 && state.Deployed.Count == 0;
            // External uninstall can temporarily remove the DLL while its enabled config remains.
            if ((automatic || method == ReplaceMethods.Loose) && Rows.Any(r => r.Files.Contains("dinput8.dll",StringComparer.OrdinalIgnoreCase)) &&
                String.Equals(LooseLoader.Read(Game,LooseLoader.EnabledKey),"true",StringComparison.OrdinalIgnoreCase))
                return ReplaceMethods.Loose;
            if (e.LooseLoaderOffFor(true))
                throw new IOException("Enable the loose file loader or choose the PAK method in Game files & replacements before migrating.");
            return method;
        }

        private List<PresetLayer> LoadLayers(ReplaceEngine e,string sevenZip)
        {
            if (!String.Equals(Game,e.GameRoot,StringComparison.OrdinalIgnoreCase)) throw new IOException("Select the SF6 folder used for this migration: " + Game);
            var state = e.LoadState();
            var layers = new List<PresetLayer>();
            var packages = new Dictionary<string,ModPackage>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in Rows)
            {
                if (Within(r.Source,Game) || Within(Game,r.Source) || Within(r.Source,e.DataDir) || Within(r.Source,Path.Combine(Root,"Backup")) || Within(r.Source,e.StoreRoot(state)))
                    throw new IOException("Choose a library source outside the game, backups and Manager data: " + r.Source);
                ModPackage p;
                if (!packages.TryGetValue(r.Source,out p))
                {
                    if (String.IsNullOrEmpty(r.Stamp) || r.Stamp.StartsWith("unreadable:") || MainForm.SourceStamp(r.Source) != r.Stamp)
                        throw new IOException("The reviewed source is missing or changed: " + r.Source);
                    packages[r.Source] = p = PackageReader.Load(r.Source,sevenZip);
                }
                var layer = new PresetLayer { Package=p, Name=r.Name, Id=r.Component, Order=layers.Count, Stamp=r.Stamp };
                var payload = layer.Payload();
                if (!new HashSet<string>(r.Files,StringComparer.OrdinalIgnoreCase).SetEquals(payload.Files.Keys.Select(FluffyInstalls.Key)))
                    throw new IOException("Source files differ for " + r.Name);
                if (layers.Any(l => l.Package == p && l.Id == r.Component) || state.Mods.Any(m => ReplacePlanner.SameSource(m.SourcePath,r.Source)) ||
                    (ReplacePlanner.IsLoaderComposition(payload) && (state.Mods.Any(LoaderCostumePak.IsLoaderMod) || layers.Any(l => ReplacePlanner.IsLoaderComposition(l.Payload())))) ||
                    (ReplacePlanner.IsReFrameworkComposition(payload) && (ReplacePlanner.ReFrameworkUpdateTarget(state.Mods,payload.Files.Keys) != null || layers.Any(l => ReplacePlanner.IsReFrameworkComposition(l.Payload())))))
                    throw new IOException("This entry overlaps another managed mod or tool: " + r.Name);
                layers.Add(layer);
            }
            return layers;
        }

        internal void SaveHandoff(ReplaceEngine e,string sevenZip)
        {
            if (Rows.Count == 0 || Rows.Any(r => r.Problem != null || r.Source == null)) throw new IOException("Resolve every mod before starting migration.");
            RequireSnapshot(e);
            var layers = LoadLayers(e,sevenZip);
            var state = e.LoadState();
            string method = InstallMethod(e,state);
            Originals.Clear();
            for (int i=0; i<layers.Count; i++)
            {
                var layer = layers[i];
                var scratch = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(),"slm-" + Guid.NewGuid().ToString("N"))).FullName;
                try
                {
                    foreach (string key in Rows[i].Files)
                    {
                        if (ReplaceEngine.SkippedProgram(key,e.GameRoot) || ReplaceEngine.IsModPak(key))
                            throw new IOException("This migration cannot verify the installed program or numbered PAK: " + key);
                        ReplaceEngine.ValidateDestination(key);
                    }
                    var check = ReplacePlanner.PrepareCore(layer.Payload(),sevenZip,e,scratch,null,null,state,null,Rows[i],method,true);
                    if (!check.CanInstall) throw new IOException(layer.Name + ": " + String.Join(" ",check.Errors));
                    foreach (var deployed in check.Files.Where(f => !Owners.Contains(f.Path) && !Originals.ContainsKey(f.Path)))
                    {
                        string path = e.GamePath(deployed.Path);
                        Safety.NoReparseChain(path);
                        Originals.Add(deployed.Path,File.Exists(path) ? ReplaceEngine.Sha(path) : null);
                    }
                    foreach (var file in Rows[i].PayloadHashes)
                    {
                        string key = file.Key;
                        if (Owners[key].Last() != Rows[i]) continue;
                        string live = e.GamePath(key);
                        Safety.NoReparseChain(live);
                        if (!File.Exists(live) || ReplaceEngine.Sha(live) != file.Value)
                            throw new IOException(layer.Name + " differs from the active game file: " + key + ". Locate the matching source.");
                        string backup = Safety.Under(Path.Combine(Root,"Backup"),key);
                        string sibling = Path.Combine(Path.GetDirectoryName(live),Path.GetFileNameWithoutExtension(live) + "_FMMBACKUP" + Path.GetExtension(live));
                        Originals[key] = File.Exists(backup) ? ReplaceEngine.Sha(backup) : File.Exists(sibling) ? ReplaceEngine.Sha(sibling) : null;
                    }
                }
                finally { Directory.Delete(scratch,true); }
            }
            VerifyCoveredSources(sevenZip);
            using (e.Lock())
            {
                if (File.Exists(Pending(e))) throw new IOException("A saved migration already exists. Reopen migration to resume it.");
                RequireSnapshot(e);
                CompatibilityChecked = true;
                Directory.CreateDirectory(e.DataDir); DurableFile.WriteJson(Pending(e),this);
            }
        }

        internal string Waiting(ReplaceEngine e)
        {
            if (!String.Equals(Game,e.GameRoot,StringComparison.OrdinalIgnoreCase)) return "Select the original SF6 game folder to resume migration.";
            if (GameProcesses.Probe.IsGameRunning()) return "Close Street Fighter 6 to continue.";
            if (!Directory.Exists(Root)) return "Reconnect the mod manager setup: " + Root;
            if (File.Exists(Path.Combine(Root,"installed.ini.new"))) return "Waiting for the other manager to finish saving.";
            Dictionary<string,string> manifests;
            if (ReadActive(e,Root,out manifests).Count > 0)
            {
                if (!CompatibilityChecked) throw new IOException("This plan predates the compatibility checks. Keep the remaining mods installed. Forget this plan and review the full setup again before uninstalling.");
                return "In the other mod manager, uninstall all Street Fighter 6 mods, then close it.";
            }
            if (GameProcesses.Probe.IsFluffyRunning()) return "Close the other mod manager. Migration will continue automatically.";
            return null;
        }

        private void RequireSavedPlan(ReplaceEngine e)
        {
            if (!File.Exists(Pending(e)) || ReplaceEngine.ReadJsonShared<ModMigration>(Pending(e)).Id != Id)
                throw new IOException("The saved migration has changed. Reopen migration.");
        }

        internal void Forget(ReplaceEngine e)
        {
            using (e.Lock()) { RequireSavedPlan(e); File.Delete(Pending(e)); }
        }

        internal void Restore(ReplaceEngine e,string sevenZip)
        {
            using (e.Lock())
            {
                if (!String.Equals(Game,e.GameRoot,StringComparison.OrdinalIgnoreCase)) throw new IOException("Select the original game folder to resume migration.");
                if (!File.Exists(Pending(e)) && e.LoadState().LastMigration == Id) return;
                RequireSavedPlan(e);
                if (e.JournalPending) { GameProcesses.RequireIdle("resume migration"); e.Recover(); }
                if (e.LoadState().LastMigration != Id)
                {
                    string wait = Waiting(e);
                    if (wait != null) throw new IOException(wait);
                    foreach (var f in Originals)
                    {
                        string path = e.GamePath(f.Key); Safety.NoReparseChain(path);
                        if ((File.Exists(path) ? ReplaceEngine.Sha(path) : null) != f.Value)
                            throw new IOException("The original game file has not been restored: " + f.Key);
                    }
                    List<string> where;
                    if (e.OrphanedRows(out where) > 0) throw new IOException("Game archive entries still need repair. Review them on Installed, then resume migration.");
                    GameProcesses.RequireIdle("migrate mods");
                    var layers = LoadLayers(e,sevenZip);
                    VerifyCoveredSources(sevenZip);
                    e.ImportLayers(layers,sevenZip,this);
                }
                File.Delete(Pending(e));
            }
        }
    }
}
