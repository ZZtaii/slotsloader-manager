using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SlotsInstaller
{
    internal static class RecoveryRegressionChecks
    {
        private sealed class FixtureUnavailableException : Exception { internal FixtureUnavailableException(string message) : base(message) { } }
        private const string Mesh="natives/stm/product/model/esf/esf032/001/01/esf032_001_01.mesh.230110883";
        private const string Material="natives/stm/product/model/esf/esf032/001/01/esf032_001_01.mdf2.40";
        private sealed class Fixture
        {
            internal string Root, Source, CostumeRoot, Installed;
            internal CompositionPlan Plan;
        }
        internal static int Run(string fixtureRoot)
        {
            string full=Path.GetFullPath(fixtureRoot);
            Safety.NoReparseChain(full);
            // Keep junction fixture paths within Windows PowerShell's legacy path
            // limit; the app itself still uses its long-path manifest/config.
            string run;
            do { run=Path.Combine(full,"r-"+Guid.NewGuid().ToString("N").Substring(0,12)); } while (Directory.Exists(run));
            Directory.CreateDirectory(run);
            Console.WriteLine("Recovery fixture root: "+run);
            int failures=0, skipped=0;
            // 1.0.1: a reinstall (variant switch) keeps no backup copy; the old retained-backup checks are gone with it.
            Check("a reinstall over a managed outfit keeps no backup copy and leaves the source intact",delegate { SwitchKeepsNothing(Path.Combine(run,"switch")); },ref failures,ref skipped);
            Check("unrecognized and manual recovery folders remain protected",delegate { UnknownFolders(Path.Combine(run,"unknown")); },ref failures,ref skipped);
            Check("escaped paths and changed recovery snapshots are refused",delegate { InvalidSnapshots(Path.Combine(run,"snapshots")); },ref failures,ref skipped);
            Check("junction-containing copies are refused before any deletion",delegate { Junctions(Path.Combine(run,"junctions")); },ref failures,ref skipped);
            Check("recovery actions share the installer mutation lock",delegate { MutationLock(Path.Combine(run,"lock")); },ref failures,ref skipped);
            Console.WriteLine("Recovery regression checks: "+(5-failures-skipped)+" passed, "+failures+" failed, "+skipped+" skipped.");
            return failures;
        }
        private static void Check(string name,Action test,ref int failures,ref int skipped)
        {
            try { test(); Console.WriteLine("PASS "+name); }
            catch (FixtureUnavailableException ex) { skipped++; Console.WriteLine("SKIP "+name+": "+ex.Message); }
            catch (Exception ex) { failures++; Console.WriteLine("FAIL "+name+": "+ex); }
        }
        private static void Require(bool condition,string message) { if (!condition) throw new InvalidOperationException(message); }
        private static void Refused(Action action)
        {
            bool refused=false; try { action(); } catch (IOException) { refused=true; }
            Require(refused,"Unsafe or colliding recovery action was not refused.");
        }
        private static void Put(string root,string path,string value)
        {
            string file=Safety.Under(root,path); Directory.CreateDirectory(Path.GetDirectoryName(file)); File.WriteAllText(file,value,new UTF8Encoding(false));
        }
        private static Fixture Create(string root)
        {
            var fixture=new Fixture {Root=root,Source=Path.Combine(root,"source"),CostumeRoot=Path.Combine(root,"game","reframework","costume_mods")};
            Put(fixture.Source,"modinfo.ini","name=Recovery fixture\n"); Put(fixture.Source,Mesh,"source mesh marker"); Put(fixture.Source,Material,"source material marker");
            ModPackage package=PackageReader.Load(fixture.Source,""); fixture.Plan=Composer.Plan(package,Composer.DefaultSelection(package));
            fixture.Installed=Installer.Install(fixture.Plan,fixture.CostumeRoot,"Ingrid","Recovery fixture","",false).Folder;
            return fixture;
        }
        private static RecoveryEntry Trash(Fixture fixture)
        {
            Installer.Uninstall(Installer.ListInstalled(fixture.CostumeRoot).Single(o=>o.Folder==fixture.Installed),fixture.CostumeRoot);
            return RecoveryStore.List(fixture.CostumeRoot).Entries.Single(e=>e.Kind==RecoveryKind.Trash);
        }
        private static void SourceIntact(Fixture fixture)
        {
            Require(File.ReadAllText(Safety.Under(fixture.Source,Mesh))=="source mesh marker" && File.ReadAllText(Safety.Under(fixture.Source,Material))=="source material marker","Recovery changed original source files.");
        }
        private static void SwitchKeepsNothing(string root)
        {
            Fixture fixture=Create(root);
            Installer.Install(fixture.Plan,fixture.CostumeRoot,"Ingrid","Recovery fixture","",true);
            Require(RecoveryStore.List(fixture.CostumeRoot).Entries.Count==0,"A reinstall left a backup copy.");
            Require(File.ReadAllText(Safety.Under(fixture.Installed,Mesh))=="source mesh marker" && Installer.ListInstalled(fixture.CostumeRoot).Single().Managed,"The reinstalled outfit is not the managed copy.");
            SourceIntact(fixture);
        }        private static void UnknownFolders(string root)
        {
            Fixture fixture=Create(root); RecoveryEntry trash=Trash(fixture);
            string backups=Path.Combine(Path.GetDirectoryName(fixture.CostumeRoot),".slotsloader-installer-backups");
            string manual=Path.Combine(backups,"manual-copy"), unowned=Path.Combine(backups,Guid.NewGuid().ToString("N"));
            Put(manual,"keep.txt","manual"); Put(unowned,"keep.txt","unowned"); Put(unowned,".slots-installer.json","{broken json");
            RecoveryListing listing=RecoveryStore.List(fixture.CostumeRoot);
            Require(listing.Entries.Count==1 && listing.Warnings.Count==2,"Unknown folders were treated as manageable recovery copies.");
            Refused(delegate { RecoveryStore.Delete(fixture.CostumeRoot,new RecoveryEntry {Folder=manual,Kind=RecoveryKind.Backup}); });
            Refused(delegate { RecoveryStore.Delete(fixture.CostumeRoot,new RecoveryEntry {Folder=unowned,Kind=RecoveryKind.Backup}); });
            Require(File.Exists(Path.Combine(manual,"keep.txt")) && File.Exists(Path.Combine(unowned,"keep.txt")) && Directory.Exists(trash.Folder),"Unrecognized folders were altered."); SourceIntact(fixture);
        }
        private static void InvalidSnapshots(string root)
        {
            Fixture fixture=Create(root); RecoveryEntry entry=Trash(fixture);
            var outside=new RecoveryEntry {Folder=fixture.Source,Kind=entry.Kind,Id=entry.Id,ManifestText=entry.ManifestText,Bytes=entry.Bytes,FileCount=entry.FileCount};
            Refused(delegate { RecoveryStore.Delete(fixture.CostumeRoot,outside); }); Refused(delegate { RecoveryStore.Restore(fixture.CostumeRoot,outside); });
            string manifest=Path.Combine(entry.Folder,".slots-installer.json"); File.AppendAllText(manifest," ");
            Refused(delegate { RecoveryStore.Restore(fixture.CostumeRoot,entry); }); Refused(delegate { RecoveryStore.Delete(fixture.CostumeRoot,entry); });
            RecoveryEntry fresh=RecoveryStore.List(fixture.CostumeRoot).Entries.Single(); Put(fresh.Folder,"new-file.txt","new content");
            Refused(delegate { RecoveryStore.Delete(fixture.CostumeRoot,fresh); });
            Require(Directory.Exists(entry.Folder) && File.Exists(Path.Combine(entry.Folder,"new-file.txt")),"Changed snapshot was deleted."); SourceIntact(fixture);
        }
        private static void Junctions(string root)
        {
            Fixture fixture=Create(root); RecoveryEntry entry=Trash(fixture);
            string protectedRoot=Path.Combine(root,"protected"); Put(protectedRoot,"keep.txt","protected sentinel");
            string link=Path.Combine(entry.Folder,"j");
            string script="$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path '"+link.Replace("'","''")+"' -Target '"+protectedRoot.Replace("'","''")+"' | Out-Null";
            using (var process=new Process())
            {
                process.StartInfo=new ProcessStartInfo("powershell.exe","-NoProfile -NonInteractive -EncodedCommand "+Convert.ToBase64String(Encoding.Unicode.GetBytes(script))) {UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true,RedirectStandardOutput=true};
                process.Start(); string errors=process.StandardError.ReadToEnd();
                Require(process.WaitForExit(15000),"Junction fixture process did not finish.");
                if (process.ExitCode!=0 && errors.IndexOf("Access is denied",StringComparison.OrdinalIgnoreCase)>=0)
                    throw new FixtureUnavailableException("This execution token cannot create junctions. No reparse-point safety result is claimed for this run.");
                Require(process.ExitCode==0,"Could not create fixture junction: "+errors);
            }
            Require((File.GetAttributes(link)&FileAttributes.ReparsePoint)!=0,"Junction fixture was not a reparse point.");
            Refused(delegate { RecoveryStore.Restore(fixture.CostumeRoot,entry); }); Refused(delegate { RecoveryStore.Delete(fixture.CostumeRoot,entry); });
            Require(RecoveryStore.List(fixture.CostumeRoot).Entries.Count==0,"Junction-containing copy was offered as safe recovery content.");
            Require(File.Exists(Safety.Under(entry.Folder,Mesh)) && File.ReadAllText(Path.Combine(protectedRoot,"keep.txt"))=="protected sentinel","Reparse refusal was not completed before mutation."); SourceIntact(fixture);
        }
        private static void MutationLock(string root)
        {
            Fixture fixture=Create(root); RecoveryEntry entry=Trash(fixture);
            using (Installer.AcquireMutation(fixture.CostumeRoot))
            {
                Task test=Task.Run(delegate { Refused(delegate { RecoveryStore.Restore(fixture.CostumeRoot,entry); }); Refused(delegate { RecoveryStore.Delete(fixture.CostumeRoot,entry); }); });
                Require(test.Wait(5000),"Recovery action did not promptly respect the mutation lock.");
            }
            Require(Directory.Exists(entry.Folder),"Locked action mutated the retained copy."); SourceIntact(fixture);
        }
    }
}
