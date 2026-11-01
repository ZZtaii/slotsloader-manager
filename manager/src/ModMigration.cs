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
            var byEntry = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in chosen) byEntry[p.Value.Asset.EntryPath] = staged[p.Key];
            var hashes = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            var skipped = new List<PlannedFile>();
            foreach (var file in payload.Files.Values)
            {
                string hash;
                if (byEntry.TryGetValue(file.Asset.EntryPath,out hash)) hashes[FluffyInstalls.Key(file.Asset.DestinationPath)] = hash;
                else skipped.Add(file);
            }
            // Both an obsolete and a current file may be installed externally, but only the current one is staged.
            if (skipped.Count > 0)
            {
                string scratch = Safety.NewFolder(staging,".migration-");
                try
                {
                    foreach (var file in reader.CopyFiles(skipped,Path.Combine(scratch,"files"),scratch))
                        hashes[FluffyInstalls.Key(file.Key)] = file.Value;
                }
                finally { Directory.Delete(scratch,true); }
            }
            if (preview) { PayloadHashes = hashes; return; }
            string expected;
            if (PayloadHashes == null || hashes.Count != PayloadHashes.Count ||
                hashes.Any(f => !PayloadHashes.TryGetValue(f.Key,out expected) || f.Value != expected))
                throw new IOException("A download changed since the review: " + Source);
        }
    }

    public sealed class ModMigration
    {
        public string Id, Root, Game, ManifestHash;
        public bool CompatibilityChecked;
        public List<MigrationRow> Rows = new List<MigrationRow>();
        public Dictionary<string,string> Originals = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        private ILookup<string,MigrationRow> owners;
        // Every row listing a file, in install order (the last one's copy is in the game), by RecordedName.
        private ILookup<string,MigrationRow> Owners
        {
            get
            {
                return owners ?? (owners = Rows.SelectMany(r => r.Files.Select(f => new { File=RecordedName(f), Row=r }))
                    .ToLookup(p => p.File,p => p.Row,StringComparer.OrdinalIgnoreCase));
            }
        }
        private MigrationRow Winner(string path) { return Owners[RecordedName(path)].LastOrDefault(); }

        internal const string UninstallThere = "Open your other mod manager, choose Uninstall all mods for Street Fighter 6, then close it. This window continues on its own.";

        internal static string Pending(ReplaceEngine e) { return Path.Combine(e.DataDir,"pending-migration.json"); }

        // The other manager installs these old suffixes under the current ones and lists the new names
        // (its install code renames them before writing installed.ini). Matching compares both sides by this name.
        private static readonly string[][] SuffixRenames = { new[] { ".mesh.220721329", ".mesh.230110883" }, new[] { ".tex.143230113", ".tex.241101895" }, new[] { ".tex.36", ".tex.241101895" } };
        internal static string RecordedName(string path)
        {
            string key = FluffyInstalls.Key(path);
            foreach (string[] rename in SuffixRenames)
                if (key.EndsWith(rename[0],StringComparison.OrdinalIgnoreCase)) return key.Substring(0,key.Length - rename[0].Length) + rename[1];
            return key;
        }
        private static HashSet<string> RecordedNames(IEnumerable<string> paths) { return new HashSet<string>(paths.Select(RecordedName),StringComparer.OrdinalIgnoreCase); }

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
            if (manifests.Count != 1) throw new IOException(manifests.Count == 0 ? "Your other mod manager lists no active SF6 mods. If it has some, add its Mods folder under Settings → General → Mod libraries." :
                "Two mod manager folders list active mods, so their combined install order is unknown. Uninstall the mods in one of them first.");
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
                    row.Problem = "Installed as a numbered patch PAK, which can't be moved safely. Uninstall it in your other mod manager first, then install it here yourself.";
                else if (!Supports(row.Type)) row.Problem = "Installed with a method this Manager doesn't use (" + row.Type + "). Uninstall it in your other mod manager first, then install it here yourself.";
                else if (matches[i].Package != null) Match(row,matches[i].Package,entries[i].ExplicitIds);
                else if (matches[i].Ambiguous) MatchOneOf(row,matches[i].Choices);
                else row.Problem = "Not found in your mod libraries.";
            }
            return plan;
        }

        // Several downloads matched by name: the one whose options install exactly the recorded files wins.
        private static void MatchOneOf(MigrationRow row, List<PresetMapping> choices)
        {
            var found = new List<MigrationRow>();
            foreach (PresetMapping choice in choices ?? new List<PresetMapping>())
            {
                if (choice.Package == null) continue;
                var candidate = new MigrationRow { Name=row.Name, Section=row.Section, Type=row.Type, Files=row.Files };
                Match(candidate,choice.Package,choice.OptionIds);
                if (candidate.Problem == null) found.Add(candidate);
            }
            if (found.Count == 1) { row.Source = found[0].Source; row.Component = found[0].Component; row.Stamp = found[0].Stamp; row.Problem = null; return; }
            row.Source = null;
            row.Problem = "Several downloads in your mod libraries match it.";
        }

        internal static void Match(MigrationRow row, ModPackage package, List<string> suggested)
        {
            if (!Supports(row.Type) || row.Files.Any(ReplaceEngine.IsModPak)) return;
            var recorded = RecordedNames(row.Files);
            var choices = package.Components.Where(c => !c.IsDummy && c.HasFiles(true) &&
                recorded.SetEquals(RecordedNames(c.Assets.Concat(c.RootAssets).Select(a => a.DestinationPath)))).ToList();
            if (choices.Count > 1)
                choices = choices.Where(c => suggested != null ? suggested.Contains(c.Id,StringComparer.OrdinalIgnoreCase) :
                    String.Equals(c.Name,row.Name,StringComparison.OrdinalIgnoreCase) ||
                    String.Equals(c.Id.Split('/').Last(),row.Section,StringComparison.OrdinalIgnoreCase)).ToList();
            row.Source = null;
            row.Problem = choices.Count == 1 ? null : "No option of this download installs exactly the files your other mod manager installed for it.";
            if (choices.Count != 1) return;
            row.Source = package.SourcePath; row.Component = choices[0].Id; row.Stamp = MainForm.SourceStamp(row.Source);
        }

        internal string SharedDetails(MigrationRow row, bool beforeMove = true)
        {
            var lines = row.Files.Where(p => Owners[RecordedName(p)].Count() > 1).Select(p => p + "  →  " + Winner(p).Name).ToList();
            if (beforeMove && row.Files.Any(p => Winner(p) != row))
                lines.Add("A later mod covers this one's copy, so the Manager checks it against your other mod manager's Mods folder instead of the game.");
            return String.Join(Environment.NewLine,lines);
        }

        // The section names the archive or folder in the other manager's Mods folder, maybe followed by the option's folder.
        private bool SectionNames(string section, string source, string component)
        {
            string mods = Path.GetFullPath(Path.Combine(Root,"Mods")).TrimEnd(Path.DirectorySeparatorChar);
            string full = Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar);
            var bases = new List<string> { Path.GetFileName(full) };
            if (Within(full,mods) && full.Length > mods.Length) bases.Add(full.Substring(mods.Length + 1));
            if (File.Exists(full)) bases.AddRange(bases.ToList().Select(b => b.Substring(0,b.Length - Path.GetExtension(b).Length)));
            var names = new List<string>(bases);
            if (!String.IsNullOrEmpty(component))
            {
                string last = component.Split('/').Last();
                names.Add(component); names.Add(last);
                foreach (string b in bases) { names.Add(b + "/" + component); names.Add(b + "/" + last); }
            }
            Func<string,string> norm = s => s.Replace('\\','/').Trim('/');
            return names.Any(n => String.Equals(norm(n),norm(section),StringComparison.OrdinalIgnoreCase));
        }

        // A winning loose file proves nothing about the version of a layer underneath it.
        // Resolve that layer independently in the external library, even after a manual Locate.
        private void VerifyCoveredSources(string sevenZip)
        {
            var covered = Rows.Where(r => r.Files.Any(f => Winner(f) != r)).ToList();
            if (covered.Count == 0) return;
            var entries = covered.Select(r => new FluffyPresetEntry { Section=r.Section, ModName=r.Name }).ToList();
            var library = Library.Scan(new[] {Path.Combine(Root,"Mods")},sevenZip);
            var matches = FluffyPresets.Map(entries,library,sevenZip);
            for (int i=0; i<covered.Count; i++)
            {
                var row = covered[i]; var match = matches[i];
                var reference = new MigrationRow { Section=row.Section, Name=row.Name, Type=row.Type, Files=row.Files };
                if (match.Package != null) Match(reference,match.Package,entries[i].ExplicitIds);
                if (reference.Source == null || !SectionNames(row.Section,reference.Source,reference.Component))
                    throw new IOException("Can't check " + row.Name + ": a later mod covers its files, and its download isn't in your other mod manager's Mods folder to compare with. Put it back there, then try again.");
                if (ReplacePlanner.SameSource(row.Source,reference.Source) && row.Component == reference.Component) continue;
                var original = new PresetLayer { Package=match.Package, Id=reference.Component, Name=row.Name };
                var files = original.Payload().Files.Values.Where(f => Winner(f.Asset.DestinationPath) != row);
                var selected = row.PayloadHashes.GroupBy(p => RecordedName(p.Key),StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key,g => new HashSet<string>(g.Select(p => p.Value)),StringComparer.OrdinalIgnoreCase);
                string scratch = Safety.NewFolder(Path.GetTempPath(),"slm-");
                try
                {
                    using (var reader = new SourceReader(reference.Source,sevenZip))
                        foreach (var file in reader.CopyFiles(files,Path.Combine(scratch,"files"),scratch))
                        {
                            HashSet<string> expected;
                            if (!selected.TryGetValue(RecordedName(file.Key),out expected) || !expected.Contains(file.Value))
                                throw new IOException(row.Name + ": the download you picked differs from the one in your other mod manager's Mods folder (" + file.Key + "). Locate that one instead: " + reference.Source);
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
                throw new IOException("Your other mod manager's list of mods changed. Close this window and open it again to review the new list.");
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
            if (!String.Equals(Game,e.GameRoot,StringComparison.OrdinalIgnoreCase)) throw new IOException("This migration is for the game in " + Game + ". Select that folder in Settings first.");
            var state = e.LoadState();
            var layers = new List<PresetLayer>();
            var packages = new Dictionary<string,ModPackage>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in Rows)
            {
                if (Within(r.Source,Game) || Within(Game,r.Source) || Within(r.Source,e.DataDir) || Within(r.Source,Path.Combine(Root,"Backup")) || Within(r.Source,e.StoreRoot(state)))
                    throw new IOException("Choose a download outside the game, backups and Manager data: " + r.Source);
                ModPackage p;
                if (!packages.TryGetValue(r.Source,out p))
                {
                    if (String.IsNullOrEmpty(r.Stamp) || r.Stamp.StartsWith("unreadable:") || MainForm.SourceStamp(r.Source) != r.Stamp)
                        throw new IOException("A download changed or went missing since the review: " + r.Source);
                    packages[r.Source] = p = PackageReader.Load(r.Source,sevenZip);
                }
                var layer = new PresetLayer { Package=p, Name=r.Name, Id=r.Component, Order=layers.Count, Stamp=r.Stamp };
                var payload = layer.Payload();
                if (!RecordedNames(r.Files).SetEquals(RecordedNames(payload.Files.Keys)))
                    throw new IOException(r.Name + ": the download no longer has the reviewed files.");
                if (layers.Any(l => l.Package == p && l.Id == r.Component) || state.Mods.Any(m => ReplacePlanner.SameSource(m.SourcePath,r.Source)) ||
                    (ReplacePlanner.IsLoaderComposition(payload) && (state.Mods.Any(LoaderCostumePak.IsLoaderMod) || layers.Any(l => ReplacePlanner.IsLoaderComposition(l.Payload())))) ||
                    (ReplacePlanner.IsReFrameworkComposition(payload) && (ReplacePlanner.ReFrameworkUpdateTarget(state.Mods,payload.Files.Keys) != null || layers.Any(l => ReplacePlanner.IsReFrameworkComposition(l.Payload())))))
                    throw new IOException(r.Name + " overlaps a mod or tool this Manager already has installed, or another mod in the list. Uninstall it here first.");
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
                OperationContext.Report("Checking " + layer.Name, i, layers.Count);
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
                    foreach (var deployed in check.Files.Where(f => !Owners.Contains(RecordedName(f.Path)) && !Originals.ContainsKey(f.Path)))
                    {
                        string path = e.GamePath(deployed.Path);
                        Safety.NoReparseChain(path);
                        Originals.Add(deployed.Path,File.Exists(path) ? ReplaceEngine.Sha(path) : null);
                    }
                    // The other manager may have installed the raw file or repaired it as this Manager does: either matches.
                    var repaired = check.Files.GroupBy(f => RecordedName(f.Path),StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key,g => g.First().Sha256,StringComparer.OrdinalIgnoreCase);
                    var installed = Rows[i].Files.GroupBy(RecordedName,StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key,g => g.First(),StringComparer.OrdinalIgnoreCase);
                    foreach (var group in Rows[i].PayloadHashes.GroupBy(p => RecordedName(p.Key),StringComparer.OrdinalIgnoreCase))
                    {
                        string key, fixedSha;
                        if (!installed.TryGetValue(group.Key,out key) || Winner(key) != Rows[i]) continue;
                        var accepted = new HashSet<string>(group.Select(p => p.Value));
                        if (repaired.TryGetValue(group.Key,out fixedSha)) accepted.Add(fixedSha);
                        string live = e.GamePath(key);
                        Safety.NoReparseChain(live);
                        if (!File.Exists(live) || !accepted.Contains(ReplaceEngine.Sha(live)))
                            throw new IOException(layer.Name + ": the download doesn't match the file in the game (" + key + "). Locate the one it was installed from.");
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
            if (!String.Equals(Game,e.GameRoot,StringComparison.OrdinalIgnoreCase)) return "This migration is for the game in " + Game + ". Select that folder in Settings to continue.";
            if (GameProcesses.Probe.IsGameRunning()) return "Close Street Fighter 6 to continue.";
            if (!Directory.Exists(Root)) return "Can't find your other mod manager's folder: " + Root + ". Reconnect its drive to continue.";
            if (File.Exists(Path.Combine(Root,"installed.ini.new"))) return "Waiting for your other mod manager to finish saving.";
            Dictionary<string,string> manifests;
            List<FluffyMod> active;
            // The other manager rewrites its list while it uninstalls: a read that fails now is retried at the next check.
            try { active = ReadActive(e,Root,out manifests); }
            catch (Exception ex) { if (!(ex is IOException || ex is UnauthorizedAccessException)) throw; return "Waiting to read your other mod manager's list of mods: " + ex.Message; }
            if (active.Count > 0)
            {
                if (!CompatibilityChecked) throw new IOException("This plan predates the compatibility checks. Keep the remaining mods installed. Forget this plan and review the full setup again before uninstalling.");
                return UninstallThere;
            }
            if (GameProcesses.Probe.IsFluffyRunning()) return "Close your other mod manager. This window continues on its own.";
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

        // A saved plan that can't be read (damaged, or from another build) is deleted without reading it.
        internal static void Discard(ReplaceEngine e)
        {
            using (e.Lock()) if (File.Exists(Pending(e))) File.Delete(Pending(e));
        }

        internal void Restore(ReplaceEngine e,string sevenZip)
        {
            using (e.Lock())
            {
                if (!String.Equals(Game,e.GameRoot,StringComparison.OrdinalIgnoreCase)) throw new IOException("This migration is for the game in " + Game + ". Select that folder in Settings to continue.");
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
                            throw new IOException("This game file isn't back to its original yet: " + f.Key + ". In your other mod manager, uninstall all mods again, then choose Retry migration.");
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
