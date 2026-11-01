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
            var preview = ModMigration.Scan(f.Engine,entries.Where(e => !e.SourcePath.EndsWith("Beta")).ToList(),null);
            ReplaceChecks.Require(preview.Rows[1].Problem != null,"missing archive reported ready");
            ReplaceChecks.Refused(delegate { preview.SaveHandoff(f.Engine,null); },"Resolve every mod");
            ModMigration.Match(preview.Rows[1],PackageReader.Load(Path.Combine(lib,"Beta"),null),null);
            ReplaceChecks.Require(preview.SharedDetails(preview.Rows[0]).Contains("Beta"),"lost conflict winner");
            put(f.Game,file,"drift");
            ReplaceChecks.Refused(delegate { preview.SaveHandoff(f.Engine,null); },"differs");
            put(f.Game,file,"final");
            preview.SaveHandoff(f.Engine,null);
            string pending = ModMigration.Pending(f.Engine);
            var plan = ReplaceEngine.ReadJsonShared<ModMigration>(pending);
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
    }
}
