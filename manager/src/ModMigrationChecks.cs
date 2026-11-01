using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace SlotsInstaller
{
    internal static class ModMigrationChecks
    {
        internal static void Run(string root, FakeProcessProbe probe)
        {
            var f = ReplaceChecks.Create(root,false,ReplaceMethods.Pak);
            string external = Path.Combine(root,"Games","SF6"), lib = Path.Combine(external,"Mods");
            string file = "reframework/autorun/shared.lua", ini = Path.Combine(external,"installed.ini");
            Action<string,string,string> put = (dir,path,text) => ReplaceChecks.Put(dir,path,Encoding.UTF8.GetBytes(text));
            put(lib,"Alpha/modinfo.ini","name=Alpha");
            put(lib,"Beta/modinfo.ini","name=Beta");
            put(lib,"Alpha/"+file,"first"); put(lib,"Beta/"+file,"final");
            put(external,"Backup/"+file,"stock"); put(f.Game,file,"final");
            string manifest = "[Alpha]\ninstalltype=normal\nfile="+file+"\n[Beta]\ninstalltype=invalidate\nfile="+file+"\n";
            File.WriteAllText(ini,manifest);
            f.Engine.FluffyProvider = delegate { return FluffyInstalls.Load(new[] {external}); };
            var entries = Library.Scan(new[] {lib},null);
            File.WriteAllText(ini,manifest.Replace("[Alpha]","[Alpha"));
            ReplaceChecks.Refused(delegate { ModMigration.Scan(f.Engine,entries,null); },"unreadable");
            File.WriteAllText(ini,manifest.Replace("[Beta]","[Beta"));
            ReplaceChecks.Refused(delegate { ModMigration.Scan(f.Engine,entries,null); },"unreadable");
            File.WriteAllText(ini,"file=unassigned.lua\n"+manifest);
            ReplaceChecks.Refused(delegate { ModMigration.Scan(f.Engine,entries,null); },"unreadable");
            File.WriteAllText(ini,manifest);
            var preview = ModMigration.Scan(f.Engine,entries.Where(e => !e.SourcePath.EndsWith("Beta")).ToList(),null);
            ReplaceChecks.Require(preview.Rows[1].Problem != null,"missing archive reported ready");
            ReplaceChecks.Refused(delegate { preview.SaveHandoff(f.Engine,null); },"Resolve every mod");
            ModMigration.Match(preview.Rows[1],PackageReader.Load(Path.Combine(lib,"Beta"),null),null);
            ReplaceChecks.Require(preview.SharedDetails(preview.Rows[0]).Contains("Beta"),"lost conflict winner");
            put(f.Game,file,"drift");
            ReplaceChecks.Refused(delegate { preview.SaveHandoff(f.Engine,null); },"doesn't match the file in the game");
            put(f.Game,file,"final");
            string copy = Path.Combine(root,"other-copy");
            put(copy,"modinfo.ini","name=Alternative");
            put(copy,file,"different download");
            ModMigration.Match(preview.Rows[0],PackageReader.Load(copy,null),null);
            ReplaceChecks.Refused(delegate { preview.SaveHandoff(f.Engine,null); },"Mods folder");
            ReplaceChecks.Require(!File.Exists(ModMigration.Pending(f.Engine)),"saved a mismatched covered layer");
            put(copy,file,"first");
            ModMigration.Match(preview.Rows[0],PackageReader.Load(copy,null),null);
            preview.SaveHandoff(f.Engine,null);
            preview.Forget(f.Engine);
            ModMigration.Match(preview.Rows[0],PackageReader.Load(Path.Combine(lib,"Alpha"),null),null);
            preview.SaveHandoff(f.Engine,null);
            string pending = ModMigration.Pending(f.Engine);
            var plan = ReplaceEngine.ReadJsonShared<ModMigration>(pending);
            plan.CompatibilityChecked = false;
            ReplaceChecks.Refused(delegate { plan.Waiting(f.Engine); },"Keep the remaining mods installed");
            plan.CompatibilityChecked = true;
            ReplaceChecks.Refused(delegate { plan.Restore(f.Engine,null); },"uninstall all");
            File.WriteAllText(ini,"garbled");
            ReplaceChecks.Refused(delegate { plan.Restore(f.Engine,null); },"unreadable");
            File.WriteAllText(ini,"");
            probe.Fluffy = true;
            ReplaceChecks.Refused(delegate { plan.Restore(f.Engine,null); },"Close your other mod manager"); probe.Fluffy = false;
            ReplaceChecks.Refused(delegate { plan.Restore(f.Engine,null); },"back to its original");
            put(f.Game,file,"stock");
            string first = Path.Combine(lib,"Alpha",file);
            var stamp = File.GetLastWriteTimeUtc(first);
            put(lib,"Alpha/"+file,"wrong"); File.SetLastWriteTimeUtc(first,stamp);
            ReplaceChecks.Refused(delegate { plan.Restore(f.Engine,null); },"changed");
            ReplaceChecks.Require(File.Exists(pending) && !f.Engine.JournalPending && File.ReadAllText(f.Engine.GamePath(file)) == "stock","failed import altered baseline");
            put(lib,"Alpha/"+file,"first"); File.SetLastWriteTimeUtc(first,stamp);
            try
            {
                MainForm.BeforeSourceStamp = delegate { MainForm.BeforeSourceStamp = null; put(f.Game,file,"changed during preparation"); };
                ReplaceChecks.Refused(delegate { plan.Restore(f.Engine,null); },"original game file changed");
            }
            finally { MainForm.BeforeSourceStamp = null; }
            ReplaceChecks.Require(File.Exists(pending) && !f.Engine.JournalPending && f.Engine.LoadState().Mods.Count == 0 &&
                File.ReadAllText(f.Engine.GamePath(file)) == "changed during preparation","migration overwrote or adopted the changed original");
            put(f.Game,file,"stock");
            ReplaceEngine.CrashAfterStep = 1;
            try { ReplaceChecks.Refused(delegate { plan.Restore(f.Engine,null); },"crash"); } finally { ReplaceEngine.CrashAfterStep = -1; }
            ReplaceChecks.Require(f.Engine.JournalPending && File.Exists(pending),"interrupted migration lost its recovery information");
            plan.Restore(f.Engine,null);
            var state = f.Engine.LoadState();
            ReplaceChecks.Require(state.Mods.Count == 2 && state.Mods[0].Name == "Beta" && File.ReadAllText(f.Engine.GamePath(file)) == "final","wrong migration winner");
            DurableFile.WriteJson(pending,plan); // Simulate shutdown after committing ownership, before retiring the plan.
            plan.Restore(f.Engine,null);
            ReplaceChecks.Require(!File.Exists(pending) && f.Engine.LoadState().Generation == state.Generation,"replayed a committed migration");
            f.Engine.UninstallForGood(state.Mods[0].Id,DriftChoice.Refuse,null);
            ReplaceChecks.Require(File.ReadAllText(f.Engine.GamePath(file)) == "first","lost overwritten layer");
            f.Engine.UninstallForGood(state.Mods[1].Id,DriftChoice.Refuse,null);
            ReplaceChecks.Require(File.ReadAllText(f.Engine.GamePath(file)) == "stock","lost original game file");
        }

        internal static void Compatibility(string root)
        {
            var f = ReplaceChecks.Create(Path.Combine(root,"blocked"),false,ReplaceMethods.Pak);
            string external = Path.Combine(f.Root,"External"), lib = Path.Combine(external,"Mods");
            string ini = Path.Combine(external,"installed.ini");
            f.Engine.FluffyProvider = delegate { return FluffyInstalls.Load(new[] {external}); };
            Action<string,string,string,string> blocked = delegate(string name,string file,string metadata,string error)
            {
                ReplaceChecks.Put(lib,name+"/modinfo.ini",Encoding.UTF8.GetBytes(metadata));
                ReplaceChecks.Put(lib,name+"/"+file,Encoding.UTF8.GetBytes("active"));
                ReplaceChecks.Put(f.Game,file,Encoding.UTF8.GetBytes("active"));
                string manifest = "["+name+"]\nfile="+file;
                File.WriteAllText(ini,manifest);
                var preview = ModMigration.Scan(f.Engine,Library.Scan(new[] {lib},null),null);
                ReplaceChecks.Refused(delegate { preview.SaveHandoff(f.Engine,null); },error);
                ReplaceChecks.Require(!File.Exists(ModMigration.Pending(f.Engine)) && File.ReadAllText(ini) == manifest &&
                    File.ReadAllText(f.Engine.GamePath(file)) == "active" && f.Engine.LoadState().Mods.Count == 0,"failed preview changed the active setup");
            };
            blocked("Patch",ReplaceChecks.P1,"name=Patch","patch PAK");
            blocked("Upstream",LoaderCostumePak.PluginPath,"name=Costume Slots Loader\nauthor=Wael\nversion=1.11.0","upstream");
            foreach (string method in new[] {ReplaceMethods.Loose,""})
            {
                var fw = ReplaceChecks.Create(Path.Combine(root,method == "" ? "automatic" : "loose"),true,method);
                string setup = Path.Combine(fw.Root,"External"), mods = Path.Combine(setup,"Mods");
                byte[] dll = Encoding.UTF8.GetBytes("external framework"), mesh = Encoding.UTF8.GetBytes("outfit mesh");
                ReplaceChecks.Put(mods,"Framework/modinfo.ini",Encoding.UTF8.GetBytes("name=Framework"));
                ReplaceChecks.Put(mods,"Framework/dinput8.dll",dll); ReplaceChecks.Put(fw.Game,"dinput8.dll",dll);
                ReplaceChecks.Put(mods,"Outfit/modinfo.ini",Encoding.UTF8.GetBytes("name=Outfit"));
                ReplaceChecks.Put(mods,"Outfit/"+ReplaceChecks.V5,mesh); ReplaceChecks.Put(fw.Game,ReplaceChecks.V5,mesh);
                File.WriteAllText(Path.Combine(setup,"installed.ini"),"[Outfit]\nfile="+ReplaceChecks.V5+"\n[Framework]\nfile=dinput8.dll");
                fw.Engine.FluffyProvider = delegate { return FluffyInstalls.Load(new[] {setup}); };
                var preview = ModMigration.Scan(fw.Engine,Library.Scan(new[] {mods},null),null);
                if (method == ReplaceMethods.Loose) ReplaceChecks.Refused(delegate { preview.SaveHandoff(fw.Engine,null); },"Enable the loose");
                File.WriteAllText(LooseLoader.ConfigPath(fw.Game),LooseLoader.EnabledKey+"=true");
                preview.SaveHandoff(fw.Engine,null);
                File.Delete(Path.Combine(setup,"installed.ini")); File.Delete(fw.Engine.GamePath("dinput8.dll")); File.Delete(fw.Engine.GamePath(ReplaceChecks.V5));
                var plan = ReplaceEngine.ReadJsonShared<ModMigration>(ModMigration.Pending(fw.Engine));
                plan.Restore(fw.Engine,null);
                var state = fw.Engine.LoadState();
                ReplaceChecks.Require(state.Method == ReplaceMethods.Loose && state.Deployed.All(x => x.Zeroed.Count == 0) &&
                    state.Mods.Count == 2 && File.ReadAllBytes(fw.Engine.GamePath("dinput8.dll")).SequenceEqual(dll) &&
                    File.ReadAllBytes(fw.Engine.GamePath(ReplaceChecks.V5)).SequenceEqual(mesh),"failed to restore the framework and outfit together with the loose method");
            }
        }

        // 1.0.15 review: the other manager lists old suffixes under their current names and may have repaired a
        // file the way this Manager does; a path this Manager had let go of ("Keep current file") is taken back.
        internal static void Recorded(string root)
        {
            var reference = ReplaceChecks.Create(Path.Combine(root,"reference"),false,ReplaceMethods.Pak);
            var repaired = ReplaceChecks.Install(reference,"modA","Reference");
            var f = ReplaceChecks.Create(Path.Combine(root,"renamed"),false,ReplaceMethods.Pak);
            string setup = Path.Combine(f.Root,"External"), lib = Path.Combine(setup,"Mods"), ini = Path.Combine(setup,"installed.ini");
            f.Engine.FluffyProvider = delegate { return FluffyInstalls.Load(new[] {setup}); };
            string source = f.Mod("modA");
            var listed = new List<string>();
            foreach (string file in Directory.GetFiles(source,"*",SearchOption.AllDirectories))
            {
                string rel = file.Substring(source.Length + 1).Replace('\\','/');
                ReplaceChecks.Put(lib,"Colors/" + rel,File.ReadAllBytes(file));
                if (rel.StartsWith("natives/",StringComparison.OrdinalIgnoreCase)) listed.Add(rel.Replace(".tex.143230113",".tex.241101895"));
            }
            bool changed = false;
            foreach (string rel in listed)
            {
                ModFile ours = repaired.Files.Single(x => String.Equals(x.Path,rel,StringComparison.OrdinalIgnoreCase));
                byte[] bytes = File.ReadAllBytes(reference.Engine.GamePath(ours.Path));
                ReplaceChecks.Put(f.Game,rel,bytes);
                string raw = Path.Combine(source,rel.Replace(".tex.241101895",".tex.143230113").Replace('/','\\'));
                if (!File.Exists(raw)) raw = Path.Combine(source,rel.Replace('/','\\'));
                changed |= !File.ReadAllBytes(raw).SequenceEqual(bytes);
            }
            ReplaceChecks.Require(changed && listed.Any(p => p.EndsWith(".tex.241101895") && p.Contains("_albd")),"fixture: no renamed or repaired file to compare");
            File.WriteAllText(ini,"[Colors]\r\ninstalltype=normal\r\n" + String.Concat(listed.Select(p => "file=" + p.Replace('/','\\') + "\r\n")));
            var preview = ModMigration.Scan(f.Engine,Library.Scan(new[] {lib},null),null);
            ReplaceChecks.Require(preview.Rows.Count == 1 && preview.Rows[0].Problem == null,"renamed suffixes did not match their download: " + preview.Rows[0].Problem);
            preview.SaveHandoff(f.Engine,null);
            File.Delete(ini); foreach (string rel in listed) File.Delete(f.Engine.GamePath(rel));
            ReplaceEngine.ReadJsonShared<ModMigration>(ModMigration.Pending(f.Engine)).Restore(f.Engine,null);
            ReplaceChecks.Require(f.Engine.LoadState().Mods.Count == 1 && listed.All(rel => File.Exists(f.Engine.GamePath(rel))),"repaired download not migrated");

            // A kept path: the Manager's Upper over Lower, then the other manager's Outside over both, then Upper uninstalled
            // with "Keep current file". Outside wins the path in the other manager, so it must win it after migration too.
            var k = ReplaceChecks.Create(Path.Combine(root,"kept"),false,ReplaceMethods.Pak);
            string kept = "reframework/autorun/kept.lua", keptSetup = Path.Combine(k.Root,"External"), keptLib = Path.Combine(keptSetup,"Mods");
            k.Engine.FluffyProvider = delegate { return FluffyInstalls.Load(new[] {keptSetup}); };
            ReplaceChecks.Put(k.Mods,"Lower/"+kept,Encoding.UTF8.GetBytes("lower")); ReplaceChecks.Put(k.Mods,"Upper/"+kept,Encoding.UTF8.GetBytes("upper"));
            ReplaceChecks.Install(k,"Lower","Lower"); var upper = ReplaceChecks.Install(k,"Upper","Upper");
            ReplaceChecks.Put(keptSetup,"Backup/"+kept,Encoding.UTF8.GetBytes("upper"));
            ReplaceChecks.Put(keptLib,"Outside/modinfo.ini",Encoding.UTF8.GetBytes("name=Outside")); ReplaceChecks.Put(keptLib,"Outside/"+kept,Encoding.UTF8.GetBytes("outside"));
            ReplaceChecks.Put(k.Game,kept,Encoding.UTF8.GetBytes("outside"));
            File.WriteAllText(Path.Combine(keptSetup,"installed.ini"),"[Outside]\nfile="+kept);
            k.Engine.Uninstall(upper.Id,DriftChoice.KeepCurrent);
            ReplaceChecks.Require(k.Engine.LoadState().Released.Any(r => r.Path == kept),"fixture: the path was not kept");
            ModMigration.Scan(k.Engine,Library.Scan(new[] {keptLib},null),null).SaveHandoff(k.Engine,null);
            File.Delete(Path.Combine(keptSetup,"installed.ini")); ReplaceChecks.Put(k.Game,kept,Encoding.UTF8.GetBytes("upper"));   // its uninstall puts back its backup
            ReplaceEngine.ReadJsonShared<ModMigration>(ModMigration.Pending(k.Engine)).Restore(k.Engine,null);
            var state = k.Engine.LoadState();
            ReplaceChecks.Require(File.ReadAllText(k.Engine.GamePath(kept)) == "outside" && state.Mods[0].Name == "Outside" && !state.Released.Any(r => r.Path == kept),
                "the other manager's winner stayed hidden under a kept file: " + File.ReadAllText(k.Engine.GamePath(kept)));
            k.Engine.UninstallForGood(state.Mods[0].Id,DriftChoice.Refuse,null);
            ReplaceChecks.Require(File.ReadAllText(k.Engine.GamePath(kept)) == "lower","the Manager's own layer lost the path after the migrated one left");
        }
    }
}
