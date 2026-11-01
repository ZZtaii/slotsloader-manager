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
    }

    public sealed class ModMigration
    {
        public string Id, Root, Game, ManifestHash;
        public List<MigrationRow> Rows = new List<MigrationRow>();
        public Dictionary<string,string> Originals = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string,string> sharedWinners;

        internal static string Pending(ReplaceEngine e) { return Path.Combine(e.DataDir,"pending-migration.json"); }

        internal static bool Supports(string type)
        {
            var m = new FluffyMod { InstallType = type };
            return m.CopiedFiles || String.Equals(type,"invalidate",StringComparison.OrdinalIgnoreCase);
        }

        internal static ModMigration Scan(ReplaceEngine e, List<LibraryEntry> library, string sevenZip)
        {
            var external = e.Fluffy();
            if (external.Warnings.Count > 0) throw new IOException(String.Join(" ",external.Warnings));
            var active = external.Mods.Select(m => m.Root).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (active.Count != 1) throw new IOException(active.Count == 0 ? "No active SF6 mods were found. Add the other manager’s SF6 Mods folder in Settings." :
                "More than one setup lists active mods. Their combined install order cannot be verified.");
            string root = active[0];
            var ini = File.ReadAllBytes(Path.Combine(root,"installed.ini"));
            var mods = FluffyInstalls.Parse(FluffyInstalls.DecodeIni(ini),root);
            if (mods.Count == 0) throw new IOException("The active setup changed. Scan again.");
            var entries = mods.Select(m => new FluffyPresetEntry { Section=m.Section, ModName=m.Name, ModId=m.ModId, Order=m.Index }).ToList();
            var matches = FluffyPresets.Map(entries,library,sevenZip);
            var plan = new ModMigration { Id=Guid.NewGuid().ToString("N"), Root=root, Game=e.GameRoot, ManifestHash=ReplaceEngine.Sha(ini) };
            for (int i=0; i<mods.Count; i++)
            {
                var m = mods[i];
                var row = new MigrationRow { Name=m.Name, Section=m.Section, Type=m.InstallType,
                    Files=m.Files.Select(FluffyInstalls.Key).Distinct(StringComparer.OrdinalIgnoreCase).ToList() };
                plan.Rows.Add(row);
                if (String.Equals(row.Type,"pakcopy_reengine_invalidate",StringComparison.OrdinalIgnoreCase) || row.Files.Any(ReplaceEngine.IsModPak))
                    row.Problem = "This mod uses a numbered patch PAK. Its install order cannot be verified for migration.";
                else if (!Supports(row.Type)) row.Problem = "Unsupported install method: " + row.Type;
                else if (matches[i].Package == null) row.Problem = matches[i].Message;
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
            if (sharedWinners == null)
                sharedWinners = Rows.SelectMany(r => r.Files.Select(path => new { Path=path, Row=r }))
                    .GroupBy(f => f.Path,StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1)
                    .ToDictionary(g => g.Key,g => g.Last().Row.Name,StringComparer.OrdinalIgnoreCase);
            return String.Join(Environment.NewLine,row.Files.Where(sharedWinners.ContainsKey).Select(p => p + "  →  " + sharedWinners[p]));
        }

        private void RequireSnapshot(ReplaceEngine e)
        {
            var external = e.Fluffy();
            if (external.Warnings.Count > 0) throw new IOException(String.Join(" ",external.Warnings));
            if (ReplaceEngine.Sha(Path.Combine(Root,"installed.ini")) != ManifestHash ||
                external.Mods.Any(m => !String.Equals(m.Root,Root,StringComparison.OrdinalIgnoreCase)))
                throw new IOException("The active setup changed. Close the preview and scan again.");
        }

        private static bool Within(string path,string root)
        {
            string prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return (Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar).StartsWith(prefix,StringComparison.OrdinalIgnoreCase);
        }

        private List<PresetLayer> LoadLayers(ReplaceEngine e,string sevenZip)
        {
            if (!String.Equals(Game,e.GameRoot,StringComparison.OrdinalIgnoreCase)) throw new IOException("Select the SF6 folder used for this migration: " + Game);
            if (e.LooseLoaderOffFor(Rows.Any(r => r.Files.Any(f => f.StartsWith("natives/",StringComparison.OrdinalIgnoreCase)))))
                throw new IOException("Enable the loose file loader or choose the PAK method in Game files & replacements before migrating.");
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
            var winners = new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
            for (int i=0; i<Rows.Count; i++) foreach (var file in Rows[i].Files) winners[file] = i;
            Originals.Clear();
            for (int i=0; i<layers.Count; i++)
            {
                var layer = layers[i];
                var scratch = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(),"slm-" + Guid.NewGuid().ToString("N"))).FullName;
                try
                {
                    using (var reader = new SourceReader(layer.Package.SourcePath,sevenZip))
                    {
                        var hashes = reader.CopyFiles(layer.Payload().Files.Values,Path.Combine(scratch,"files"),scratch);
                        Rows[i].PayloadHashes = hashes.ToDictionary(f => FluffyInstalls.Key(f.Key),f => f.Value,StringComparer.OrdinalIgnoreCase);
                    }
                    foreach (var file in Rows[i].PayloadHashes)
                    {
                        string key = file.Key;
                        if (ReplaceEngine.SkippedProgram(key,e.GameRoot) || ReplaceEngine.IsModPak(key))
                            throw new IOException("This migration cannot verify the installed program or numbered PAK: " + key);
                        ReplaceEngine.ValidateDestination(key);
                        if (winners[key] != i) continue;
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
            using (e.Lock())
            {
                if (File.Exists(Pending(e))) throw new IOException("A saved migration already exists. Reopen migration to resume it.");
                RequireSnapshot(e);
                Directory.CreateDirectory(e.DataDir); DurableFile.WriteJson(Pending(e),this);
            }
        }

        internal string Waiting(ReplaceEngine e)
        {
            if (!String.Equals(Game,e.GameRoot,StringComparison.OrdinalIgnoreCase)) return "Select the original SF6 game folder to resume migration.";
            if (GameProcesses.Probe.IsGameRunning()) return "Close Street Fighter 6 to continue.";
            if (!Directory.Exists(Root)) return "Reconnect the mod manager setup: " + Root;
            if (File.Exists(Path.Combine(Root,"installed.ini.new"))) return "Waiting for the other manager to finish saving.";
            var external = e.Fluffy();
            if (external.Warnings.Count > 0) throw new IOException(String.Join(" ",external.Warnings));
            byte[] bytes = null;
            try { bytes = File.ReadAllBytes(Path.Combine(Root,"installed.ini")); } catch (FileNotFoundException) { }
            string ini = bytes == null ? "" : FluffyInstalls.DecodeIni(bytes);
            if (external.Mods.Count > 0 || FluffyInstalls.Parse(ini,Root).Count > 0) return "In the other mod manager, uninstall all Street Fighter 6 mods, then close it.";
            if (ini.Split('\n').Any(line => line.Trim().Length > 0 && !line.Trim().StartsWith(";"))) throw new IOException("The installed mod list is unreadable. Check it before continuing.");
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
                    e.ImportLayers(LoadLayers(e,sevenZip),sevenZip,this);
                }
                File.Delete(Pending(e));
            }
        }
    }
}
