using System;
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
            ReplaceChecks.Refused(delegate { preview.SaveHandoff(f.Engine,null); },"differs");
            put(f.Game,file,"final");
            string copy = Path.Combine(root,"other-copy");
            put(copy,"modinfo.ini","name=Alternative");
            put(copy,file,"different download");
            ModMigration.Match(preview.Rows[0],PackageReader.Load(copy,null),null);
            ReplaceChecks.Refused(delegate { preview.SaveHandoff(f.Engine,null); },"covered file");
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
            ReplaceChecks.Refused(delegate { plan.Restore(f.Engine,null); },"Close the other mod manager"); probe.Fluffy = false;
            ReplaceChecks.Refused(delegate { plan.Restore(f.Engine,null); },"original game file");
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
    }
}
