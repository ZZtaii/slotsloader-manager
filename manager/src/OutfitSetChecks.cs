using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SlotsInstaller
{
    // Generated fixtures only. Archives are deliberately opaque bytes: a saved
    // installed set must not depend on opening them or finding its source mods.
    internal static class OutfitSetChecks
    {
        private const string Mesh="natives/stm/product/model/esf/esf032/001/01/esf032_001_01.mesh.230110883";
        private sealed class Fixture
        {
            internal string Root,Source,CostumeRoot;
            internal List<InstalledOutfit> Rows { get { return Installer.ListInstalled(CostumeRoot); } }
        }
        private sealed class FixtureUnavailableException : Exception { internal FixtureUnavailableException(string message):base(message) {} }

        internal static int Run(string fixtureRoot)
        {
            string root=Path.GetFullPath(fixtureRoot); Safety.NoReparseChain(root);
            string run=Safety.Under(root,"os-"+Guid.NewGuid().ToString("N").Substring(0,10));
            Directory.CreateDirectory(run); Console.WriteLine("Outfit set fixture root: "+run);
            int failures=0,skips=0,count=0;
            Action<string,Action> check=delegate(string name,Action action)
            {
                count++;
                try { action(); Console.WriteLine("PASS "+name); }
                catch(FixtureUnavailableException ex) { skips++; Console.WriteLine("SKIP "+name+": "+ex.Message); }
                catch(Exception ex) { failures++; Console.WriteLine("FAIL "+name+": "+ex); }
            };
            check("opaque installed bytes survive save/remove/restore without source mods",delegate { RoundTrip(Path.Combine(run,"roundtrip")); });
            check("selected subset and disabled managed outfit restore to exact locations",delegate { Subset(Path.Combine(run,"subset")); });
            check("duplicate, stale and escaped removal inputs fail before changes",delegate { InvalidInputs(Path.Combine(run,"inputs")); });
            check("identical destinations skip, conflicting destination blocks the entire restore",delegate { Collisions(Path.Combine(run,"collisions")); });
            check("damaged manifests and payloads refuse restore without live changes",delegate { CorruptSnapshots(Path.Combine(run,"corruption")); });
            check("snapshot paths cannot escape their payload or game root",delegate { Traversal(Path.Combine(run,"traversal")); });
            check("managed ownership matches the payload after a saved-set refresh",delegate { ManagedIdentity(Path.Combine(run,"identity")); });
            check("cancellation leaves live outfits intact before commit",delegate { Cancellation(Path.Combine(run,"cancel")); });
            check("failed remove and restore commits roll back their earlier moves",delegate { Rollback(Path.Combine(run,"rollback")); });
            check("rollback collisions preserve original payload and the replacement entry",delegate { FailedRollback(Path.Combine(run,"rollback-conflict")); });
            check("set operations respect the shared installer mutation lock",delegate { MutationLock(Path.Combine(run,"lock")); });
            check("deleting one saved set preserves active outfits and other sets",delegate { DeleteSet(Path.Combine(run,"delete")); });
            check("reparse-point sources and snapshots are rejected without following them",delegate { Reparse(Path.Combine(run,"links")); });
            IProcessProbe previousProbe=GameProcesses.Probe; var probe=new FakeProcessProbe(); GameProcesses.Probe=probe;
            try
            {
                check("preset scope (2.4.1): Extra outfits / Replacements / Both save what they say (tool mods in, Slotsloader and disabled ones out, priority order, options, method); v1 stays v1, replacements make v2; list labels",delegate { ScopedSave(Path.Combine(run,"scope-save")); });
                check("preset scope (2.4.1): presets without a scope (older Managers) read as Extra outfits and restore as before; a damaged saved replacement refuses restore without changes",delegate { OlderPresets(Path.Combine(run,"scope-old")); });
                check("preset scope (2.4.1): restore of each part without the library: missing replacements installed in saved order (also between ones still installed), identical skipped, a same-named different replacement skipped and reported, extra outfits only touch costume_mods, an extra-outfit conflict stops Both before any replacement changes",delegate { probe.Game=false;probe.Fluffy=false;ScopedRestore(Path.Combine(run,"scope-restore")); });
                check("preset scope (2.4.1): SF6 running refuses a replacement restore (nothing changes, no store leftovers); Fluffy open refuses PAK-method rows; all-identical restores still pass",delegate { RunningRefused(Path.Combine(run,"scope-running"),probe); });
                check("list presets (1.0.8): v3 saves library pointers (outfit source, options, place; replacements in priority order, no Slotsloader) and copies only what has no source; info, labels, old-format mark; a damaged pointer is refused",delegate { probe.Game=false;probe.Fluffy=false;PointerSave(Path.Combine(run,"list-save")); });
                check("list presets (1.0.8): restore reinstalls from the library (saved order, same game files, same options), skips what is installed, notes a missing mod, a taken place and a changed source while the rest goes on; a library rename moves the pointers",delegate { probe.Game=false;probe.Fluffy=false;PointerRestore(Path.Combine(run,"list-restore")); });
                check("list presets (1.0.8): copied and listed replacements restore in one saved priority order; 1.0.10: a copied replacement says its mod was not found at the recorded path",delegate { probe.Game=false;probe.Fluffy=false;PointerMixedOrder(Path.Combine(run,"list-mixed")); });
                check("list presets (1.0.10): at save, a replacement and an extra outfit whose library mod was renamed outside the Manager point at the one library mod holding their files (natives paths as installed, a file the Manager fixed by path only, a renamed old texture suffix, a version in the new name), no game file changes, a Rename history event; they are listed, not copied, and restore from it; a same-named mod with other bytes is not it, two identical ones are left as is and copied with the reason",delegate { probe.Game=false;probe.Fluffy=false;PointerRelink(Path.Combine(run,"list-relink")); });
                check("list presets (1.0.10): a manual drop identical to a listed library archive is saved as a pointer to it (a same-size one with other bytes is not), a different drop is still copied as a manual drop; the copy list, its size and reasons show in the details and the save summary; restore skips the drop while it is installed, brings it back from the library under its own name, and leaves a changed one alone",delegate { probe.Game=false;probe.Fluffy=false;PointerTwins(Path.Combine(run,"list-twins")); });
            }
            finally { GameProcesses.Probe=previousProbe; }
            Console.WriteLine("Outfit set checks: "+(count-failures-skips)+" passed, "+failures+" failed, "+skips+" skipped.");
            return failures==0 ? 0 : 1;
        }
        private static void Require(bool condition,string message) { if(!condition) throw new InvalidOperationException(message); }
        private static void Refused(Action action)
        {
            try { action(); } catch(IOException) { return; }
            throw new InvalidOperationException("The unsafe or changed operation was accepted.");
        }
        private static void Cancelled(Action action)
        {
            try { action(); } catch(OperationCanceledException) { return; }
            throw new InvalidOperationException("Cancellation did not stop the operation.");
        }
        private static void Put(string root,string path,string value)
        {
            string target=Safety.Under(root,path); Directory.CreateDirectory(Path.GetDirectoryName(target)); File.WriteAllText(target,value,new UTF8Encoding(false));
        }
        private static Fixture Create(string root)
        {
            var f=new Fixture {Root=root,Source=Path.Combine(root,"sources"),CostumeRoot=Path.Combine(root,"game","reframework","costume_mods")};
            Put(f.Source,"modinfo.ini","name=Outfit set fixture\n"); Put(f.Source,Mesh,"native fixture bytes\0\u00e9");
            ModPackage package=PackageReader.Load(f.Source,""); var plan=Composer.Plan(package,Composer.DefaultSelection(package));
            Installer.Install(plan,f.CostumeRoot,"Ingrid","Managed A","",false);
            Installer.Install(plan,f.CostumeRoot,"Ingrid","Managed B","",false);
            Installer.Install(plan,f.CostumeRoot,"Ingrid","Disabled C","",false);
            Installer.SetEnabled(f.Rows.Single(o=>o.Name=="Disabled C"),f.CostumeRoot,false);
            Put(f.CostumeRoot,"Juri/Manual directory/readme.txt","manual folder sentinel");
            Put(f.CostumeRoot,"Juri/Manual directory/nested/mesh.dat","manual opaque mesh");
            Directory.CreateDirectory(Safety.Under(f.CostumeRoot,"Juri/Manual directory/empty/subdir"));
            foreach(string extension in new[] {"zip","rar","7z","pak"})
            {
                string file=Safety.Under(f.CostumeRoot,"Juri/Manual "+extension+"."+extension);
                File.WriteAllBytes(file,new byte[] {0,255,13,10,(byte)extension[0],17,0,88,9,220});
            }
            Put(root,"protected/keep.txt","outside outfit sentinel");
            Require(f.Rows.Count==8 && f.Rows.Count(o=>o.Enabled)==7,"Fixture enumeration is incomplete.");
            return f;
        }
        private static string Hash(string file)
        {
            using(var input=File.OpenRead(file)) using(var sha=SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(input)).Replace("-","");
        }
        private static Dictionary<string,string> Inventory(string path)
        {
            var result=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            if(File.Exists(path)) { Safety.NoReparseChain(path);result[""]=Hash(path);return result; }
            if(!Directory.Exists(path)) return result;
            var pending=new Stack<string>();pending.Push(path);
            while(pending.Count>0)
            {
                string dir=pending.Pop();Safety.NoReparseChain(dir);
                string relative=dir==path ? "" : dir.Substring(path.Length+1).Replace('\\','/');result[relative+"/"]="directory";
                foreach(string child in Directory.EnumerateFileSystemEntries(dir))
                {
                    Safety.NoReparse(child);
                    if(Directory.Exists(child)) pending.Push(child); else result[child.Substring(path.Length+1).Replace('\\','/')]=Hash(child);
                }
            }
            return result;
        }
        private static string Fingerprint(string path)
        {
            return String.Join("\n",Inventory(path).OrderBy(p=>p.Key,StringComparer.OrdinalIgnoreCase).Select(p=>p.Key+"="+p.Value));
        }
        private static Dictionary<string,string> Live(Fixture f)
        {
            return f.Rows.ToDictionary(o=>o.Folder,o=>(o.Enabled ? "enabled:" : "disabled:")+(o.Managed ? "managed:" : "manual:")+Fingerprint(o.Folder),StringComparer.OrdinalIgnoreCase);
        }
        private static void Equal(Dictionary<string,string> expected,Dictionary<string,string> actual,string message)
        {
            Require(expected.Count==actual.Count && expected.All(p=>actual.ContainsKey(p.Key) && actual[p.Key]==p.Value),message);
        }
        private static void Protected(Fixture f) { Require(File.ReadAllText(Path.Combine(f.Root,"protected","keep.txt"))=="outside outfit sentinel","Protected fixture was changed."); }
        private static void RoundTrip(string root)
        {
            Fixture f=Create(root); var before=Live(f);
            OutfitSetInfo preset=OutfitSetStore.CreatePreset(f.CostumeRoot,"All enabled outfit bytes");
            Require(preset.Kind==OutfitSetKind.Preset && preset.OutfitCount==7 && preset.Bytes>0,"Preset metadata or enabled-only membership is wrong.");
            Equal(before,Live(f),"Saving a preset changed live outfits.");
            OutfitSetStore.RemoveOutfits(f.CostumeRoot,f.Rows.Where(o=>o.Enabled).ToList(),"Removed for roundtrip");
            Require(f.Rows.Count==1 && !f.Rows.Single().Enabled,"Batch removal changed the disabled outfit or left an enabled entry.");
            Safety.DeleteGenerated(f.Source,f.Root);
            OutfitSetRestoreResult restored=OutfitSetStore.Restore(f.CostumeRoot,preset);
            Require(restored.Restored==7 && restored.Skipped==0,"Restore count is wrong.");
            Equal(before,Live(f),"Restored files, empty directories, identities or enable states differ from their originals.");
            restored=OutfitSetStore.Restore(f.CostumeRoot,preset);
            Require(restored.Restored==0 && restored.Skipped==7,"Repeated restore duplicated or rewrote existing identical outfits.");
            Equal(before,Live(f),"Repeated restore changed installed bytes.");Protected(f);
        }
        private static void Subset(string root)
        {
            Fixture f=Create(root); var before=Live(f);
            var selected=f.Rows.Where(o=>o.Name=="Managed A" || o.Name=="Manual zip.zip" || !o.Enabled).ToList();
            OutfitSetInfo removed=OutfitSetStore.RemoveOutfits(f.CostumeRoot,selected,"Three selected only");
            Require(removed.Kind==OutfitSetKind.Removed && removed.OutfitCount==3,"Removed set membership is wrong.");
            var remaining=Live(f);var expected=before.Where(p=>!selected.Any(o=>String.Equals(o.Folder,p.Key,StringComparison.OrdinalIgnoreCase))).ToDictionary(p=>p.Key,p=>p.Value,StringComparer.OrdinalIgnoreCase);
            Equal(expected,remaining,"Unselected outfits changed during batch removal.");
            Safety.DeleteGenerated(f.Source,f.Root);
            var restored=OutfitSetStore.Restore(f.CostumeRoot,removed);
            Require(restored.Restored==3 && restored.Skipped==0,"Selected set restore count is wrong.");
            Equal(before,Live(f),"Disabled or selected outfit did not restore to its original location/state.");Protected(f);
        }
        private static void InvalidInputs(string root)
        {
            Fixture f=Create(root);var before=Live(f);var row=f.Rows.First(o=>o.Enabled);
            Refused(delegate { OutfitSetStore.RemoveOutfits(f.CostumeRoot,new[] {row,row},"Duplicate"); });
            Refused(delegate { OutfitSetStore.RemoveOutfits(f.CostumeRoot,new InstalledOutfit[0],"Empty"); });
            Refused(delegate { OutfitSetStore.RemoveOutfits(f.CostumeRoot,new[] {(InstalledOutfit)null},"Null"); });
            OutfitSetStore.CreatePreset(f.CostumeRoot,"Unique preset name");
            Refused(delegate { OutfitSetStore.CreatePreset(f.CostumeRoot,"UNIQUE PRESET NAME"); });
            Refused(delegate { OutfitSetStore.CreatePreset(f.CostumeRoot,"../escaped name"); });
            Equal(before,Live(f),"Invalid selection changed active outfits.");
            var stale=f.Rows.Single(o=>o.Name=="Manual zip.zip");File.AppendAllText(stale.Folder," changed");before=Live(f);
            Refused(delegate { OutfitSetStore.RemoveOutfits(f.CostumeRoot,new[] {stale},"Stale"); });
            Equal(before,Live(f),"A stale row was removed.");
            string manual=Path.Combine(f.Root,"protected");
            var outside=new InstalledOutfit {Folder=manual,Character="Juri",Name="Protected",Managed=false,Enabled=true};
            Refused(delegate { OutfitSetStore.RemoveOutfits(f.CostumeRoot,new[] {outside},"Outside"); });
            var moved=f.Rows.Single(o=>o.Name=="Managed A");Installer.SetEnabled(moved,f.CostumeRoot,false);before=Live(f);
            Refused(delegate { OutfitSetStore.RemoveOutfits(f.CostumeRoot,new[] {moved},"Moved"); });
            Equal(before,Live(f),"Stale moved row altered its new location.");Protected(f);
        }
        private static void Collisions(string root)
        {
            Fixture f=Create(root);var original=Live(f);var preset=OutfitSetStore.CreatePreset(f.CostumeRoot,"Collision set");
            var skipped=OutfitSetStore.Restore(f.CostumeRoot,preset);Require(skipped.Restored==0 && skipped.Skipped==7,"Identical live outfits were not skipped.");
            Equal(original,Live(f),"Skip changed live files.");
            OutfitSetStore.RemoveOutfits(f.CostumeRoot,f.Rows.Where(o=>o.Enabled).ToList(),"Remove before conflict");
            Put(f.CostumeRoot,"Juri/Manual zip.zip","user replacement must survive");
            var before=Live(f);string saved=Fingerprint(preset.Folder);
            Refused(delegate { OutfitSetStore.Restore(f.CostumeRoot,preset); });
            Equal(before,Live(f),"Restore published earlier entries before detecting a later conflict.");
            Require(Fingerprint(preset.Folder)==saved,"Refused restore changed its saved snapshot.");Protected(f);
        }
        private static Dictionary<string,object> Manifest(OutfitSetInfo set)
        {
            return Json.Serializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(Path.Combine(set.Folder,"set.json")));
        }
        private static void WriteManifest(OutfitSetInfo set,Dictionary<string,object> data) { File.WriteAllText(Path.Combine(set.Folder,"set.json"),Json.Serializer().Serialize(data),new UTF8Encoding(false)); }
        private static Dictionary<string,object> FirstEntry(Dictionary<string,object> data) { return ((System.Collections.IEnumerable)data["Entries"]).Cast<Dictionary<string,object>>().First(); }
        private static void CorruptSnapshots(string root)
        {
            Fixture f=Create(root);var preset=OutfitSetStore.CreatePreset(f.CostumeRoot,"Damaged payload");
            OutfitSetStore.RemoveOutfits(f.CostumeRoot,f.Rows.Where(o=>o.Enabled).ToList(),"Remove before damaged restore");
            var before=Live(f);string payload=Directory.EnumerateFiles(Path.Combine(preset.Folder,"payload"),"*",SearchOption.AllDirectories).First();
            byte[] bytes=File.ReadAllBytes(payload);Require(bytes.Length>0,"No payload bytes in fixture.");bytes[bytes.Length-1]^=0x40;File.WriteAllBytes(payload,bytes);
            Refused(delegate { OutfitSetStore.Restore(f.CostumeRoot,preset); });Equal(before,Live(f),"Corrupt payload restore changed live outfits.");
            var clean=OutfitSetStore.List(f.CostumeRoot).Entries.First(s=>s.Kind==OutfitSetKind.Removed);
            File.WriteAllText(Path.Combine(clean.Folder,"set.json"),"{broken json");
            Refused(delegate { OutfitSetStore.Restore(f.CostumeRoot,clean); });Equal(before,Live(f),"Invalid JSON restore changed live outfits.");
            Require(OutfitSetStore.List(f.CostumeRoot).Warnings.Count>0,"Broken set manifest has no visible diagnosis.");Protected(f);
        }
        private static void Traversal(string root)
        {
            Fixture f=Create(root);var before=Live(f);
            foreach(string field in new[] {"RelativePath","Storage"})
            {
                var set=OutfitSetStore.CreatePreset(f.CostumeRoot,"Escaped "+field);var data=Manifest(set);
                FirstEntry(data)[field]="../../protected";WriteManifest(set,data);
                Refused(delegate { OutfitSetStore.Restore(f.CostumeRoot,set); });
                // A fresh listing must not make an invalid destination trusted.
                var fresh=OutfitSetStore.List(f.CostumeRoot);Require(!fresh.Entries.Any(s=>s.Id==set.Id),"Traversal manifest remained actionable after refresh.");
                Equal(before,Live(f),"Traversal restore changed live outfits.");
            }
            Protected(f);
        }
        private static void Cancellation(string root)
        {
            Fixture f=Create(root);var before=Live(f);var preset=OutfitSetStore.CreatePreset(f.CostumeRoot,"Cancellation baseline");var selected=f.Rows.Where(o=>o.Enabled).ToList();
            using(var cts=new CancellationTokenSource()) using(OperationContext.Begin(cts.Token,null))
            {
                cts.Cancel();
                Cancelled(delegate { OutfitSetStore.CreatePreset(f.CostumeRoot,"Cancelled create"); });
                Cancelled(delegate { OutfitSetStore.RemoveOutfits(f.CostumeRoot,selected,"Cancelled removal"); });
                Cancelled(delegate { OutfitSetStore.Restore(f.CostumeRoot,preset); });
            }
            Equal(before,Live(f),"Pre-cancelled operation changed live outfits.");
            CancelDuringProgress(delegate { OutfitSetStore.CreatePreset(f.CostumeRoot,"Cancelled while copying"); },"Copying outfit files");
            Equal(before,Live(f),"Cancelled save changed live outfits.");
            CancelDuringProgress(delegate { OutfitSetStore.RemoveOutfits(f.CostumeRoot,selected,"Cancelled during removal preflight"); });
            Equal(before,Live(f),"Cancelled removal changed live outfits.");
            bool ignored=false;
            using(var cts=new CancellationTokenSource())
            using(OperationContext.Begin(cts.Token,delegate(OperationProgress p)
            {
                if(!p.CanCancel && p.Stage.StartsWith("Removing outfit ",StringComparison.Ordinal) && p.Completed==1) {ignored=true;cts.Cancel();}
            })) OutfitSetStore.RemoveOutfits(f.CostumeRoot,selected,"Commit cancellation is deferred");
            Require(ignored && f.Rows.Count==1,"Cancellation interrupted the noncancellable removal commit.");
            var absent=Live(f);string saved=Fingerprint(preset.Folder);
            CancelDuringProgress(delegate { OutfitSetStore.Restore(f.CostumeRoot,preset); },"Copying outfit files");
            Equal(absent,Live(f),"Cancelled restore published live outfits.");Require(Fingerprint(preset.Folder)==saved,"Cancelled restore changed the saved set.");
            Require(OutfitSetStore.Restore(f.CostumeRoot,preset).Restored==7,"Retry after cancellation failed.");
            Equal(before,Live(f),"Post-cancellation retry differed from original bytes.");Protected(f);
        }
        private static void ManagedIdentity(string root)
        {
            Fixture f=Create(root);
            var removed=OutfitSetStore.RemoveOutfits(f.CostumeRoot,f.Rows.Where(o=>!o.Enabled).ToList(),"Disabled identity");
            var before=Live(f);var data=Manifest(removed);FirstEntry(data)["ManagedId"]=Guid.NewGuid().ToString("N");WriteManifest(removed,data);
            var listed=OutfitSetStore.List(f.CostumeRoot);
            Require(!listed.Entries.Any(s=>s.Id==removed.Id),"A changed ManagedId remained actionable; disabled restore would use a folder inconsistent with its ownership record.");
            Refused(delegate { OutfitSetStore.Restore(f.CostumeRoot,removed); });Equal(before,Live(f),"Changed managed identity altered live outfits.");
            var preset=OutfitSetStore.CreatePreset(f.CostumeRoot,"Changed destination");data=Manifest(preset);
            var managed=((System.Collections.IEnumerable)data["Entries"]).Cast<Dictionary<string,object>>().First(e=>(bool)e["Managed"]);
            managed["RelativePath"]="Ingrid/Changed name";WriteManifest(preset,data);
            Require(!OutfitSetStore.List(f.CostumeRoot).Entries.Any(s=>s.Id==preset.Id),"A destination differing from its ownership record remained actionable after refresh.");
            Refused(delegate { OutfitSetStore.Restore(f.CostumeRoot,preset); });Equal(before,Live(f),"Changed managed destination altered live outfits.");Protected(f);
        }
        private static void CancelDuringProgress(Action action,string stage=null)
        {
            bool observed=false;
            using(var cts=new CancellationTokenSource())
            using(OperationContext.Begin(cts.Token,delegate(OperationProgress p) { if(p.CanCancel && p.Completed>0 && (stage==null || p.Stage==stage)) {observed=true;cts.Cancel();} })) Cancelled(action);
            Require(observed,"Mid-operation cancellation fixture never reached progress.");
        }
        private static void Rollback(string root)
        {
            Fixture f=Create(root);var before=Live(f);var selected=f.Rows.Where(o=>o.Enabled).ToList();bool injected=false;
            using(OperationContext.Begin(CancellationToken.None,delegate(OperationProgress p)
            {
                if(!p.CanCancel && p.Stage.StartsWith("Removing outfit ",StringComparison.Ordinal) && p.Completed==1) {injected=true;throw new IOException("Injected first removal commit failure.");}
            })) Refused(delegate { OutfitSetStore.RemoveOutfits(f.CostumeRoot,selected,"Injected remove"); });
            Require(injected,"Removal rollback fixture did not reach its first committed move.");Equal(before,Live(f),"Failed removal left a partial live set.");
            var removed=OutfitSetStore.RemoveOutfits(f.CostumeRoot,f.Rows.Where(o=>o.Enabled).ToList(),"Restore rollback");var absent=Live(f);injected=false;
            using(OperationContext.Begin(CancellationToken.None,delegate(OperationProgress p)
            {
                if(!p.CanCancel && p.Stage.StartsWith("Restoring outfit ",StringComparison.Ordinal) && p.Completed==1) {injected=true;throw new IOException("Injected first restore commit failure.");}
            })) Refused(delegate { OutfitSetStore.Restore(f.CostumeRoot,removed); });
            Require(injected,"Restore rollback fixture did not reach its first committed move.");Equal(absent,Live(f),"Failed restore left a partially restored live set.");
            Require(OutfitSetStore.Restore(f.CostumeRoot,removed).Restored==7,"Restore failed after rollback cleanup.");Equal(before,Live(f),"Post-rollback retry differed from original bytes.");Protected(f);
        }
        private static void MutationLock(string root)
        {
            Fixture f=Create(root);var before=Live(f);var set=OutfitSetStore.CreatePreset(f.CostumeRoot,"Lock baseline");var rows=f.Rows;
            using(Installer.AcquireMutation(f.CostumeRoot))
            {
                Task task=Task.Run(delegate
                {
                    Refused(delegate { OutfitSetStore.CreatePreset(f.CostumeRoot,"Locked create"); });
                    Refused(delegate { OutfitSetStore.RemoveOutfits(f.CostumeRoot,rows,"Locked remove"); });
                    Refused(delegate { OutfitSetStore.Restore(f.CostumeRoot,set); });
                    Refused(delegate { OutfitSetStore.Delete(f.CostumeRoot,set); });
                });
                Require(task.Wait(10000),"Locked set actions did not return promptly.");
            }
            Equal(before,Live(f),"Locked operation changed live outfits.");Require(Directory.Exists(set.Folder),"Locked delete removed its snapshot.");Protected(f);
        }
        private static void FailedRollback(string root)
        {
            Fixture f=Create(root);var first=f.Rows.Single(o=>o.Name=="Managed A");var second=f.Rows.Single(o=>o.Name=="Manual directory");
            string firstBytes=Fingerprint(first.Folder),secondBytes=Fingerprint(second.Folder);bool injected=false;string failure="";
            using(OperationContext.Begin(CancellationToken.None,delegate(OperationProgress p)
            {
                if(!p.CanCancel && p.Stage.StartsWith("Removing outfit ",StringComparison.Ordinal) && p.Completed==1)
                {
                    injected=true;Put(first.Folder,"replacement.txt","new external replacement");throw new IOException("Injected rollback collision.");
                }
            }))
            {
                try { OutfitSetStore.RemoveOutfits(f.CostumeRoot,new[] {first,second},"Rollback preservation"); }
                catch(IOException ex) {failure=ex.Message;}
            }
            Require(injected && failure.Length>0,"Rollback collision was not exercised.");
            string staging=Path.Combine(Path.GetDirectoryName(f.CostumeRoot),".slotsloader-installer-set-staging");
            string retained=Directory.EnumerateDirectories(staging).Single();
            Require(failure.IndexOf(retained,StringComparison.OrdinalIgnoreCase)>=0,"Rollback failure did not identify the retained data location.");
            Require(Fingerprint(Path.Combine(retained,"payload","000000"))==firstBytes,"Original payload was deleted or changed after rollback failed.");
            Require(File.ReadAllText(Path.Combine(first.Folder,"replacement.txt"))=="new external replacement","Rollback overwrote the replacement entry.");
            Require(Fingerprint(second.Folder)==secondBytes,"Rollback failure changed an outfit not yet moved.");Protected(f);
        }
        private static void DeleteSet(string root)
        {
            Fixture f=Create(root);var before=Live(f);var first=OutfitSetStore.CreatePreset(f.CostumeRoot,"Delete selected");var second=OutfitSetStore.CreatePreset(f.CostumeRoot,"Keep sibling");
            string sibling=Fingerprint(second.Folder);OutfitSetStore.Delete(f.CostumeRoot,first);
            Require(!Directory.Exists(first.Folder) && Fingerprint(second.Folder)==sibling,"Set deletion changed an unselected snapshot.");Equal(before,Live(f),"Deleting saved set changed active outfits.");Protected(f);
        }
        private static void MakeJunction(string link,string target)
        {
            string script="$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path '"+link.Replace("'","''")+"' -Target '"+target.Replace("'","''")+"' | Out-Null";
            using(var process=new Process())
            {
                process.StartInfo=new ProcessStartInfo("powershell.exe","-NoProfile -NonInteractive -EncodedCommand "+Convert.ToBase64String(Encoding.Unicode.GetBytes(script))) {UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true,RedirectStandardOutput=true};
                process.Start();string error=process.StandardError.ReadToEnd();
                if(!process.WaitForExit(15000)) {try {process.Kill();} catch {} throw new IOException("Junction fixture timed out.");}
                if(process.ExitCode!=0 && error.IndexOf("Access is denied",StringComparison.OrdinalIgnoreCase)>=0) throw new FixtureUnavailableException("This token cannot create junctions; no reparse-point result is claimed.");
                Require(process.ExitCode==0,"Could not create junction fixture: "+error);
            }
        }
        private static void Reparse(string root)
        {
            Fixture f=Create(root);var preset=OutfitSetStore.CreatePreset(f.CostumeRoot,"Reparse snapshot");
            string manual=Safety.Under(f.CostumeRoot,"Juri/Manual directory"),link=Path.Combine(manual,"linked");
            MakeJunction(link,Path.Combine(f.Root,"protected"));
            Require((File.GetAttributes(link)&FileAttributes.ReparsePoint)!=0,"Fixture link is not a reparse point.");
            Refused(delegate { OutfitSetStore.CreatePreset(f.CostumeRoot,"Unsafe preset"); });
            Refused(delegate { OutfitSetStore.RemoveOutfits(f.CostumeRoot,f.Rows.Where(o=>o.Folder==manual).ToList(),"Unsafe remove"); });
            Require(File.Exists(Path.Combine(manual,"readme.txt")),"Reparse rejection removed live outfit.");
            string payload=Directory.EnumerateDirectories(Path.Combine(preset.Folder,"payload")).First();MakeJunction(Path.Combine(payload,"linked"),Path.Combine(f.Root,"protected"));
            Refused(delegate { OutfitSetStore.Restore(f.CostumeRoot,preset); });
            Refused(delegate { OutfitSetStore.Delete(f.CostumeRoot,preset); });Protected(f);
        }

        // ---- 2.4.1 preset scope: a generated game (ReplaceChecks fixture: synthetic PAKs, PAK method) with extra
        // outfits in costume_mods and replacements, a tool mod and SlotsLoader installed by the engine.
        private sealed class ScopeFixture
        {
            internal ReplaceChecks.Fixture R; internal string CostumeRoot;
            internal ReplaceEngine Engine { get { return R.Engine; } }
            internal ReplaceMod A, B, Script, Loader;
            internal List<string> Order() { return Engine.LoadState().Mods.Select(m=>m.Name).ToList(); }
        }
        private static ScopeFixture CreateScope(string root)
        {
            var s=new ScopeFixture {R=ReplaceChecks.Create(root,false,ReplaceMethods.Pak)};
            s.CostumeRoot=Path.Combine(s.R.Game,"reframework","costume_mods");
            string source=Path.Combine(root,"outfit-source");
            Put(source,"modinfo.ini","name=Outfit set fixture\n"); Put(source,Mesh,"native fixture bytes");
            ModPackage package=PackageReader.Load(source,""); var plan=Composer.Plan(package,Composer.DefaultSelection(package));
            Installer.Install(plan,s.CostumeRoot,"Ingrid","Managed A","",false);
            Directory.CreateDirectory(Safety.Under(s.CostumeRoot,"Juri"));
            File.WriteAllBytes(Safety.Under(s.CostumeRoot,"Juri/Manual zip.zip"),new byte[] {80,75,3,4,1,2,3});
            ReplaceChecks.Put(s.R.Mods,"tool/Script Pack/modinfo.ini",Encoding.UTF8.GetBytes("name=Script Pack\r\n"));
            ReplaceChecks.Put(s.R.Mods,"tool/Script Pack/reframework/autorun/preset_script.lua",Encoding.UTF8.GetBytes("-- preset fixture script"));
            s.A=ReplaceChecks.Install(s.R,"modA","Cammy Colors");
            s.B=ReplaceChecks.Install(s.R,"modB","Cammy Alt");
            s.Script=ReplaceChecks.Install(s.R,"tool/Script Pack","Script Pack");
            s.Loader=ReplaceChecks.Install(s.R,"tool/Tool Pack","Slotsloader");
            Require(LoaderCostumePak.IsLoaderMod(s.Loader) && !LoaderCostumePak.IsLoaderMod(s.Script),"Fixture loader identification is wrong.");
            Require(String.Join("|",s.Order())=="Slotsloader|Script Pack|Cammy Alt|Cammy Colors","Fixture priority order: "+String.Join("|",s.Order()));
            return s;
        }
        // Game files the engine manages (natives, root and REFramework files), without costume_mods and saved sets.
        private static Dictionary<string,string> GameFiles(ScopeFixture s)
        {
            return ReplaceChecks.Snapshot(s.R.Game).Where(p=>!p.Key.StartsWith("reframework\\costume_mods",StringComparison.OrdinalIgnoreCase) && !p.Key.StartsWith("reframework\\.slotsloader-installer",StringComparison.OrdinalIgnoreCase)).ToDictionary(p=>p.Key,p=>p.Value,StringComparer.OrdinalIgnoreCase);
        }
        private static void SameGame(Dictionary<string,string> a,Dictionary<string,string> b,string context)
        {
            var diff=a.Keys.Union(b.Keys,StringComparer.OrdinalIgnoreCase).Where(k=>{string x,y;return !a.TryGetValue(k,out x) || !b.TryGetValue(k,out y) || x!=y;}).ToList();
            Require(diff.Count==0,context+": game files differ: "+String.Join(", ",diff.Take(8)));
        }
        private static Dictionary<string,string> Outfits(ScopeFixture s)
        {
            return Installer.ListInstalled(s.CostumeRoot).ToDictionary(o=>o.Folder,o=>(o.Enabled ? "enabled:" : "disabled:")+Fingerprint(o.Folder),StringComparer.OrdinalIgnoreCase);
        }
        private static OutfitSetInfo Fresh(ScopeFixture s,OutfitSetInfo set) { return OutfitSetStore.List(s.CostumeRoot).Entries.Single(e=>e.Id==set.Id); }
        private static void UninstallAll(ScopeFixture s,params string[] names)
        {
            foreach(string name in names) { string removed; s.Engine.Uninstall(s.Engine.LoadState().Mods.Single(m=>m.Name==name).Id,false,out removed); }
        }

        private static void ScopedSave(string root)
        {
            ScopeFixture s=CreateScope(root);
            s.Engine.SetEnabled(s.B.Id,false);   // disabled: not saved
            var before=GameFiles(s);
            OutfitSetInfo outfits=OutfitSetStore.CreateCopyPreset(s.CostumeRoot,"Outfits",PresetScope.ExtraOutfits,s.Engine);
            OutfitSetInfo mods=OutfitSetStore.CreateCopyPreset(s.CostumeRoot,"Mods",PresetScope.Replacements,s.Engine);
            OutfitSetInfo both=OutfitSetStore.CreateCopyPreset(s.CostumeRoot,"Both",PresetScope.Both,s.Engine);
            SameGame(before,GameFiles(s),"Saving presets");
            Require(outfits.Scope==PresetScope.ExtraOutfits && outfits.OutfitCount==2 && outfits.ReplacementCount==0,"Extra outfits preset holds the wrong things.");
            Require(mods.Scope==PresetScope.Replacements && mods.OutfitCount==0 && String.Join("|",mods.Replacements)=="Script Pack|Cammy Colors" && mods.ReplacementMethod==ReplaceMethods.Pak,"Replacements preset: "+String.Join("|",mods.Replacements)+" method "+mods.ReplacementMethod);
            Require(both.Scope==PresetScope.Both && both.OutfitCount==2 && both.ReplacementCount==2,"Both preset holds the wrong things.");
            Require(mods.Bytes==s.Engine.LoadState().Mods.Where(m=>m.Name=="Script Pack" || m.Name=="Cammy Colors").Sum(m=>m.Files.Sum(f=>f.Size)),"Replacement bytes are not counted.");
            var record=Manifest(mods);
            Require((string)record["Format"]=="sf6-slotsloader-outfit-set-v2" && (string)record["Scope"]=="replacements" && Directory.Exists(Path.Combine(mods.Folder,"replacements","000000")) && Directory.Exists(Path.Combine(mods.Folder,"replacements","000001")),"Replacements preset record/payload layout is wrong.");
            var first=(Dictionary<string,object>)((System.Collections.IEnumerable)((Dictionary<string,object>)record["Replacements"])["Mods"]).Cast<Dictionary<string,object>>().First()["Mod"];
            Require((string)first["Name"]=="Script Pack" && first.ContainsKey("SelectedIds") && first.ContainsKey("Files"),"Saved replacement lacks its options or files.");
            var plain=Manifest(outfits);
            Require((string)plain["Format"]=="sf6-slotsloader-outfit-set-v1" && (string)plain["Scope"]=="extra_outfits" && (!plain.ContainsKey("Replacements") || plain["Replacements"]==null) && !Directory.Exists(Path.Combine(outfits.Folder,"replacements")),"An extra-outfit preset is no longer the v1 format older Managers read.");
            Require(!File.ReadAllText(Path.Combine(mods.Folder,"set.json")).Contains("SF6_CostumeSlotsNative"),"Slotsloader was saved in a preset.");
            Refused(delegate { OutfitSetStore.CreateCopyPreset(s.CostumeRoot,"No engine",PresetScope.Both,null); });
            // Labels.
            Require(OutfitSetsForm.Counts(outfits)=="2 extra outfits" && OutfitSetsForm.Counts(mods)=="2 replacements" && OutfitSetsForm.Counts(both)=="2 extra outfits · 2 replacements","Counts: "+OutfitSetsForm.Counts(both));
            Require(OutfitSetsForm.Subtitle(both).StartsWith("2 extra outfits · 2 replacements · full copy · "+Utilities.FriendlyBytes(both.Bytes)+" · "),"Subtitle: "+OutfitSetsForm.Subtitle(both));   // 1.0.8: an old-format preset is marked
            var single=new OutfitSetInfo {Name="One",OutfitCount=1,ReplacementCount=1,Bytes=10,CreatedUtc=DateTime.UtcNow};
            Require(OutfitSetsForm.Counts(single)=="1 extra outfit · 1 replacement","Singular counts: "+OutfitSetsForm.Counts(single));
            Require(OutfitSetsForm.Contents(both).Contains("Replacements · saved with the PAK method") && OutfitSetsForm.Contents(both).Contains("1. Script Pack"),"Details: "+OutfitSetsForm.Contents(both));
            // Nothing of a kind enabled.
            UninstallAll(s,"Script Pack","Cammy Colors");
            Refused(delegate { OutfitSetStore.CreateCopyPreset(s.CostumeRoot,"Empty mods",PresetScope.Replacements,s.Engine); });
            Require(OutfitSetStore.CreateCopyPreset(s.CostumeRoot,"Both, outfits only",PresetScope.Both,s.Engine).Scope==PresetScope.ExtraOutfits,"Both with no enabled replacements must save the extra outfits.");
            Require(PresetScopes.Parse("both")==PresetScope.Both && PresetScopes.Parse("")==null && PresetScopes.Limit(PresetScope.Both,PresetScope.Replacements)==PresetScope.Replacements,"Scope codes.");
        }
        private static void OlderPresets(string root)
        {
            ScopeFixture s=CreateScope(root);
            OutfitSetInfo old=OutfitSetStore.CreatePreset(s.CostumeRoot,"From 2.4.0");
            var data=Manifest(old); data.Remove("Scope"); data.Remove("Replacements"); WriteManifest(old,data);
            OutfitSetInfo listed=Fresh(s,old);
            Require(listed.Scope==PresetScope.ExtraOutfits && listed.ReplacementCount==0 && listed.OutfitCount==2 && OutfitSetsForm.Counts(listed)=="2 extra outfits","An older preset does not read as Extra outfits.");
            var before=Outfits(s);
            OutfitSetStore.RemoveOutfits(s.CostumeRoot,Installer.ListInstalled(s.CostumeRoot),"Removed for old preset");
            var restored=OutfitSetStore.Restore(s.CostumeRoot,listed,PresetScope.Both,s.Engine);
            Require(restored.Restored==2 && restored.ReplacementsRestored==0,"An older preset did not restore as before.");
            Equal(before,Outfits(s),"Older preset restore differs.");
            // A v1 record must not carry replacements, and a damaged saved replacement refuses restore.
            data=Manifest(listed); data["Replacements"]=new Dictionary<string,object> {{"Method","pak"},{"Mods",new object[0]}}; WriteManifest(listed,data);
            Require(!OutfitSetStore.List(s.CostumeRoot).Entries.Any(e=>e.Id==listed.Id),"A v1 record with replacements stayed actionable.");
            OutfitSetInfo mods=OutfitSetStore.CreateCopyPreset(s.CostumeRoot,"Mods",PresetScope.Replacements,s.Engine);
            UninstallAll(s,"Cammy Colors");
            var game=GameFiles(s); string state=File.ReadAllText(s.Engine.StatePath);
            string file=Directory.EnumerateFiles(Path.Combine(mods.Folder,"replacements"),"*",SearchOption.AllDirectories).First(p=>p.EndsWith(".user.2",StringComparison.OrdinalIgnoreCase));
            byte[] bytes=File.ReadAllBytes(file); bytes[bytes.Length-1]^=0x20; File.WriteAllBytes(file,bytes);
            Refused(delegate { OutfitSetStore.Restore(s.CostumeRoot,mods,PresetScope.Replacements,s.Engine); });
            SameGame(game,GameFiles(s),"Damaged replacement restore"); Require(File.ReadAllText(s.Engine.StatePath)==state,"Damaged replacement restore changed the state.");
            data=Manifest(mods); data["Scope"]="extra_outfits"; WriteManifest(mods,data);
            Require(!OutfitSetStore.List(s.CostumeRoot).Entries.Any(e=>e.Id==mods.Id),"A scope that disagrees with the contents stayed actionable.");
        }
        private static void ScopedRestore(string root)
        {
            ScopeFixture s=CreateScope(root);
            OutfitSetInfo both=OutfitSetStore.CreateCopyPreset(s.CostumeRoot,"Everything",PresetScope.Both,s.Engine);
            var fullGame=GameFiles(s); var fullOutfits=Outfits(s);
            Directory.Delete(s.R.Mods,true);   // self-contained: no library needed
            // Replacements part only: every replacement missing.
            UninstallAll(s,"Script Pack","Cammy Alt","Cammy Colors");
            OutfitSetStore.RemoveOutfits(s.CostumeRoot,Installer.ListInstalled(s.CostumeRoot).Where(o=>o.Name=="Managed A").ToList(),"Removed one");
            var outfitsWithout=Outfits(s);
            OutfitSetPreview preview=OutfitSetStore.Preview(s.CostumeRoot,both,PresetScope.Both,s.Engine);
            Require(preview.AddReplacements.Count==3 && preview.AddOutfits.Count==1 && preview.ExistingOutfits.Count==1 && preview.InstallsNatives,"Preview: "+preview.AddReplacements.Count+"/"+preview.AddOutfits.Count);
            Require(OutfitSetsForm.RestoreChoices(PresetScope.Both).Length==3 && OutfitSetsForm.RestoreChoices(PresetScope.Replacements).SequenceEqual(new[]{PresetScope.Replacements}),"Restore choices are not limited to the preset's contents.");
            var r=OutfitSetStore.Restore(s.CostumeRoot,both,PresetScope.Replacements,s.Engine);
            Require(r.ReplacementsRestored==3 && r.ReplacementsSkipped==0 && r.Restored==0 && r.Skipped==0,"Replacements restore counts.");
            Require(String.Join("|",s.Order())=="Script Pack|Cammy Alt|Cammy Colors|Slotsloader","Saved order not kept: "+String.Join("|",s.Order()));
            SameGame(fullGame,GameFiles(s),"Replacements restore");
            Equal(outfitsWithout,Outfits(s),"A replacements-only restore changed costume_mods.");
            ReplaceMod back=s.Engine.LoadState().Mods.Single(m=>m.Name=="Cammy Colors");
            Require(ReplaceEngine.SameFiles(back,s.A) && back.SelectedIds.SequenceEqual(s.A.SelectedIds) && back.Enabled && back.Id!=s.A.Id,"Restored replacement lost its files, options or enabled state.");
            Require(OutfitSetsForm.RestoreSummary(r,PresetScope.Replacements)=="Restored 3 replacements. Restart SF6.","Summary: "+OutfitSetsForm.RestoreSummary(r,PresetScope.Replacements));
            // Again: identical skipped.
            r=OutfitSetStore.Restore(s.CostumeRoot,both,PresetScope.Replacements,s.Engine);
            Require(r.ReplacementsRestored==0 && r.ReplacementsSkipped==3,"Identical replacements were not skipped.");
            // One missing between installed ones goes back to its saved place.
            UninstallAll(s,"Cammy Alt");
            r=OutfitSetStore.Restore(s.CostumeRoot,both,PresetScope.Replacements,s.Engine);
            Require(r.ReplacementsRestored==1 && r.ReplacementsSkipped==2 && String.Join("|",s.Order())=="Script Pack|Cammy Alt|Cammy Colors|Slotsloader","Order after a partial restore: "+String.Join("|",s.Order()));
            // Same name, other contents: skipped and reported, never overwritten.
            UninstallAll(s,"Cammy Colors");
            ReplaceChecks.Put(s.R.Mods,"other/modinfo.ini",Encoding.UTF8.GetBytes("name=Other\r\n"));
            ReplaceChecks.Put(s.R.Mods,"other/natives/stm/product/ui/title.user.2",Encoding.UTF8.GetBytes("someone else's title"));
            ReplaceMod impostor=ReplaceChecks.Install(s.R,"other","Cammy Colors");
            var gameBefore=GameFiles(s);
            r=OutfitSetStore.Restore(s.CostumeRoot,both,PresetScope.Replacements,s.Engine);
            Require(r.ReplacementsRestored==0 && r.ReplacementsSkipped==2 && r.ReplacementConflicts.SequenceEqual(new[]{"Cammy Colors"}),"A same-named replacement was not skipped and reported.");
            Require(s.Engine.LoadState().Mods.Count(m=>m.Name=="Cammy Colors")==1 && ReplaceEngine.SameFiles(s.Engine.LoadState().Mods.Single(m=>m.Name=="Cammy Colors"),impostor),"A same-named replacement was overwritten.");
            SameGame(gameBefore,GameFiles(s),"Conflict restore");
            Require(OutfitSetsForm.RestoreSummary(r,PresetScope.Replacements).Contains("skipped “Cammy Colors” (a different version is installed)"),"Conflict summary: "+OutfitSetsForm.RestoreSummary(r,PresetScope.Replacements));
            UninstallAll(s,"Cammy Colors");
            // Extra outfits part only: replacements untouched.
            string state=File.ReadAllText(s.Engine.StatePath);
            r=OutfitSetStore.Restore(s.CostumeRoot,both,PresetScope.ExtraOutfits,s.Engine);
            Require(r.Restored==1 && r.Skipped==1 && r.ReplacementsRestored==0 && File.ReadAllText(s.Engine.StatePath)==state,"Extra-outfits restore touched replacements.");
            Equal(fullOutfits,Outfits(s),"Extra outfits restore differs.");
            // Both: an extra-outfit conflict stops everything before any replacement changes.
            OutfitSetStore.RemoveOutfits(s.CostumeRoot,Installer.ListInstalled(s.CostumeRoot).Where(o=>o.Name=="Manual zip.zip").ToList(),"Removed zip");
            File.WriteAllBytes(Safety.Under(s.CostumeRoot,"Juri/Manual zip.zip"),new byte[] {9,9,9});
            Refused(delegate { OutfitSetStore.Restore(s.CostumeRoot,both,PresetScope.Both,s.Engine); });
            Require(File.ReadAllText(s.Engine.StatePath)==state,"A refused Both restore changed replacements.");
            File.Delete(Safety.Under(s.CostumeRoot,"Juri/Manual zip.zip"));
            r=OutfitSetStore.Restore(s.CostumeRoot,both,PresetScope.Both,s.Engine);
            Require(r.Restored==1 && r.Skipped==1 && r.ReplacementsRestored==1 && r.ReplacementsSkipped==2,"Both restore counts.");
            SameGame(fullGame,GameFiles(s),"Both restore"); Equal(fullOutfits,Outfits(s),"Both restore outfits.");
            Require(OutfitSetsForm.RestoreSummary(r,PresetScope.Both)=="Restored 1 extra outfit · 1 replacement · 3 identical skipped. Restart SF6.","Both summary: "+OutfitSetsForm.RestoreSummary(r,PresetScope.Both));
        }
        // ---- 1.0.8 list presets (v3, OutfitSetPointers.cs) ----
        private static Dictionary<string,object> PointerRecord(OutfitSetInfo set) { return (Dictionary<string,object>)Manifest(set)["Pointers"]; }
        private static void PointerSave(string root)
        {
            ScopeFixture s=CreateScope(root);
            var before=GameFiles(s);
            OutfitSetInfo both=OutfitSetStore.CreatePreset(s.CostumeRoot,"List",PresetScope.Both,s.Engine);
            SameGame(before,GameFiles(s),"Saving a list preset");
            var record=Manifest(both); var pointers=PointerRecord(both);
            var outfits=((System.Collections.IEnumerable)pointers["Outfits"]).Cast<Dictionary<string,object>>().ToList();
            var mods=((System.Collections.IEnumerable)pointers["Replacements"]).Cast<Dictionary<string,object>>().ToList();
            Require((string)record["Format"]=="sf6-slotsloader-outfit-set-v3" && (string)record["Scope"]=="both","List preset format/scope: "+record["Format"]);
            Require(outfits.Count==1 && (string)outfits[0]["Character"]=="Ingrid" && (string)outfits[0]["Folder"]=="Managed A" && (string)outfits[0]["Mode"]=="composed" && String.Equals((string)outfits[0]["SourcePath"],Path.Combine(root,"outfit-source"),StringComparison.OrdinalIgnoreCase) && ((System.Collections.IList)outfits[0]["SelectedIds"]).Count>0,"Outfit pointer: "+Json.Serializer().Serialize(outfits));
            Require(String.Join("|",mods.Select(m=>(string)m["Name"]))=="Script Pack|Cammy Alt|Cammy Colors" && mods.All(m=>Directory.Exists((string)m["SourcePath"])),"Replacement pointers (priority order, no Slotsloader): "+String.Join("|",mods.Select(m=>(string)m["Name"])));
            Require(String.Join("|",mods.Select(m=>Convert.ToString(m["Order"])))=="0|1|2","Replacement pointer places: "+String.Join("|",mods.Select(m=>Convert.ToString(m["Order"]))));
            // Only the manual zip (no library source) is copied; no replacement store copies.
            var entries=((System.Collections.IEnumerable)record["Entries"]).Cast<Dictionary<string,object>>().ToList();
            Require(entries.Count==1 && (string)entries[0]["RelativePath"]=="Juri/Manual zip.zip" && !Directory.Exists(Path.Combine(both.Folder,"replacements")) && Directory.GetFileSystemEntries(Path.Combine(both.Folder,"payload")).Length==1,"Copied part of a list preset: "+entries.Count);
            long copy=new FileInfo(Safety.Under(s.CostumeRoot,"Juri/Manual zip.zip")).Length;
            Require(both.Pointers && both.Copied==1 && both.OutfitCount==2 && both.ReplacementCount==3 && both.Bytes>=copy && both.Bytes<copy+64*1024,"List preset info: copied "+both.Copied+", bytes "+both.Bytes);
            Require(both.Outfits.SequenceEqual(new[]{"Ingrid/Managed A","Juri/Manual zip.zip (copy)"}) && both.Replacements.SequenceEqual(new[]{"Script Pack","Cammy Alt","Cammy Colors"}),"Listed contents: "+String.Join(", ",both.Outfits));
            Require(OutfitSetsForm.Subtitle(both).StartsWith("2 extra outfits · 3 replacements · 1 copied · "+Utilities.FriendlyBytes(both.Bytes)+" · ") && OutfitSetsForm.NoteText.StartsWith("Saves a list"),"Subtitle: "+OutfitSetsForm.Subtitle(both));
            OutfitSetInfo old=OutfitSetStore.CreateCopyPreset(s.CostumeRoot,"Copies",PresetScope.Both,s.Engine);
            Require(!old.Pointers && OutfitSetsForm.Subtitle(old).Contains("full copy · "),"An old-format preset is not marked: "+OutfitSetsForm.Subtitle(old));
            // A damaged pointer makes the preset unreadable (left untouched, not listed).
            var bad=Manifest(both); ((Dictionary<string,object>)((System.Collections.IList)((Dictionary<string,object>)bad["Pointers"])["Outfits"])[0])["SourcePath"]="relative\\path"; WriteManifest(both,bad);
            Require(!OutfitSetStore.List(s.CostumeRoot).Entries.Any(e=>e.Id==both.Id),"A list preset with a relative source stayed actionable.");
            // Nothing with a source and nothing enabled: refused.
            UninstallAll(s,"Script Pack","Cammy Alt","Cammy Colors");
            Refused(delegate { OutfitSetStore.CreatePreset(s.CostumeRoot,"Empty list",PresetScope.Replacements,s.Engine); });
        }
        private static void PointerRestore(string root)
        {
            ScopeFixture s=CreateScope(root);
            OutfitSetInfo both=OutfitSetStore.CreatePreset(s.CostumeRoot,"Everything",PresetScope.Both,s.Engine);
            var fullGame=GameFiles(s);
            InstalledOutfit managed=Installer.ListInstalled(s.CostumeRoot).Single(o=>o.Name=="Managed A");
            List<string> ids=managed.SelectedIds.ToList();
            // Everything gone: reinstalled from the library (replacements in their saved order), the manual zip from its copy.
            UninstallAll(s,"Script Pack","Cammy Alt","Cammy Colors");
            OutfitSetStore.UninstallOutfits(s.CostumeRoot,Installer.ListInstalled(s.CostumeRoot),new CheckRecycleBin());
            Require(Installer.ListInstalled(s.CostumeRoot).Count==0,"Fixture: outfits left after uninstall.");
            OutfitSetPreview preview=OutfitSetStore.Preview(s.CostumeRoot,both,PresetScope.Both,s.Engine);
            Require(preview.AddReplacements.Count==3 && preview.AddOutfits.Count==2 && preview.InstallsNatives,"List preview: "+preview.AddReplacements.Count+"/"+preview.AddOutfits.Count);
            var r=OutfitSetStore.Restore(s.CostumeRoot,both,PresetScope.Both,s.Engine);
            Require(r.ReplacementsRestored==3 && r.Restored==2 && r.Notes.Count==0,"List restore counts: "+r.ReplacementsRestored+"/"+r.Restored+" notes "+String.Join(" | ",r.Notes));
            Require(String.Join("|",s.Order())=="Script Pack|Cammy Alt|Cammy Colors|Slotsloader","Saved order not kept: "+String.Join("|",s.Order()));
            SameGame(fullGame,GameFiles(s),"List restore");
            InstalledOutfit back=Installer.ListInstalled(s.CostumeRoot).Single(o=>o.Name=="Managed A");
            Require(back.Managed && back.Enabled && back.Character=="Ingrid" && ReplacePlanner.SameSource(back.SourcePath,managed.SourcePath) && back.SelectedIds.SequenceEqual(ids) && File.Exists(Safety.Under(s.CostumeRoot,"Juri/Manual zip.zip")),"Restored outfits differ.");
            Require(OutfitSetsForm.RestoreSummary(r,PresetScope.Both)=="Restored 2 extra outfits · 3 replacements. Restart SF6.","Summary: "+OutfitSetsForm.RestoreSummary(r,PresetScope.Both));
            // Again: everything already there is skipped.
            r=OutfitSetStore.Restore(s.CostumeRoot,both,PresetScope.Both,s.Engine);
            Require(r.Restored==0 && r.Skipped==2 && r.ReplacementsRestored==0 && r.ReplacementsSkipped==3 && r.Notes.Count==0,"Second restore: "+r.Restored+"/"+r.Skipped+"/"+r.ReplacementsSkipped+" "+String.Join(" | ",r.Notes));
            // Only a lower-priority one missing: it goes back below the ones still installed, not on top.
            UninstallAll(s,"Cammy Colors");
            r=OutfitSetStore.Restore(s.CostumeRoot,both,PresetScope.Replacements,s.Engine);
            Require(r.ReplacementsRestored==1 && r.Notes.Count==0 && String.Join("|",s.Order())=="Script Pack|Cammy Alt|Cammy Colors|Slotsloader","Partial restore order: "+String.Join("|",s.Order())+" "+String.Join(" | ",r.Notes));
            SameGame(fullGame,GameFiles(s),"Partial list restore");
            // Another mod under a saved replacement's name: skipped and reported, never stacked over it.
            UninstallAll(s,"Cammy Colors");
            ReplaceChecks.Put(s.R.Mods,"other/modinfo.ini",Encoding.UTF8.GetBytes("name=Other\r\n"));
            ReplaceChecks.Put(s.R.Mods,"other/natives/stm/product/ui/title.user.2",Encoding.UTF8.GetBytes("someone else's title"));
            ReplaceMod impostor=ReplaceChecks.Install(s.R,"other","Cammy Colors");
            Require(OutfitSetStore.Preview(s.CostumeRoot,both,PresetScope.Replacements,s.Engine).ConflictReplacements.SequenceEqual(new[]{"Cammy Colors"}),"A same-named replacement is not previewed as a conflict.");
            r=OutfitSetStore.Restore(s.CostumeRoot,both,PresetScope.Replacements,s.Engine);
            Require(r.ReplacementsRestored==0 && r.ReplacementsSkipped==2 && r.ReplacementConflicts.SequenceEqual(new[]{"Cammy Colors"}) && s.Engine.LoadState().Mods.Count(m=>m.Name=="Cammy Colors")==1 && ReplaceEngine.SameFiles(s.Engine.LoadState().Mods.Single(m=>m.Name=="Cammy Colors"),impostor),"A same-named replacement was not skipped: "+String.Join("|",s.Order()));
            UninstallAll(s,"Cammy Colors");
            r=OutfitSetStore.Restore(s.CostumeRoot,both,PresetScope.Replacements,s.Engine);
            Require(r.ReplacementsRestored==1 && String.Join("|",s.Order())=="Script Pack|Cammy Alt|Cammy Colors|Slotsloader","Restore after the conflict: "+String.Join("|",s.Order()));
            // Saved with an added part, installed without it: noted, not counted as identical.
            string setFile=Path.Combine(both.Folder,"set.json"); byte[] original=File.ReadAllBytes(setFile);
            var withPart=Manifest(both); var savedOutfit=(Dictionary<string,object>)((System.Collections.IList)((Dictionary<string,object>)withPart["Pointers"])["Outfits"])[0];
            savedOutfit["Parts"]=new List<object>{ new Dictionary<string,object>{{"SourcePath",Path.Combine(root,"outfit-source")},{"SourceStamp",""},{"Mod","Part fixture"},{"SelectedIds",ids.Cast<object>().ToList()}} };
            WriteManifest(both,withPart);
            r=OutfitSetStore.Restore(s.CostumeRoot,Fresh(s,both),PresetScope.ExtraOutfits,s.Engine);
            Require(r.Restored==0 && r.Skipped==1 && r.Notes.SequenceEqual(new[]{"Ingrid/Managed A: installed, but its added parts differ from the preset; left as is."}),"Missing part: "+r.Restored+"/"+r.Skipped+" "+String.Join(" | ",r.Notes));
            File.WriteAllBytes(setFile,original);
            // A mod no longer in the library, a different outfit in the place and a changed source: noted, the rest goes on.
            UninstallAll(s,"Cammy Alt","Cammy Colors");
            Directory.Move(Path.Combine(s.R.Mods,"modB"),Path.Combine(s.R.Mods,"modB gone"));
            OutfitSetStore.UninstallOutfits(s.CostumeRoot,Installer.ListInstalled(s.CostumeRoot).Where(o=>o.Name=="Managed A").ToList(),new CheckRecycleBin());
            Put(s.CostumeRoot,"Ingrid/Managed A/readme.txt","someone else's outfit");
            Put(Path.Combine(root,"outfit-source"),"extra.txt","changed after saving");
            r=OutfitSetStore.Restore(s.CostumeRoot,both,PresetScope.Both,s.Engine);
            Require(r.ReplacementsRestored==1 && r.ReplacementsSkipped==1 && r.Restored==0 && r.Skipped==1,"Partial restore counts: "+r.ReplacementsRestored+"/"+r.ReplacementsSkipped+"/"+r.Restored+"/"+r.Skipped);
            Require(r.Notes.Count==2 && r.Notes.Any(n=>n.StartsWith("Cammy Alt: not in your libraries any more")) && r.Notes.Any(n=>n.StartsWith("Ingrid/Managed A: a different outfit is installed there")),"Notes: "+String.Join(" | ",r.Notes));
            Require(File.ReadAllText(Safety.Under(s.CostumeRoot,"Ingrid/Managed A/readme.txt"))=="someone else's outfit","A different outfit in the place was changed.");
            Require(OutfitSetsForm.RestoreSummary(r,PresetScope.Both).Contains(" · 2 notes below."),"Summary with notes: "+OutfitSetsForm.RestoreSummary(r,PresetScope.Both));
            Directory.Delete(Safety.Under(s.CostumeRoot,"Ingrid/Managed A"),true);
            r=OutfitSetStore.Restore(s.CostumeRoot,both,PresetScope.ExtraOutfits,s.Engine);
            Require(r.Restored==1 && r.Notes.Count==1 && r.Notes[0].StartsWith("Ingrid/Managed A: the mod changed since the preset was saved"),"Changed source note: "+String.Join(" | ",r.Notes));
            // A library rename moves the preset's pointers with the mod.
            string renamed=Path.Combine(s.R.Mods,"modA renamed"); Directory.Move(Path.Combine(s.R.Mods,"modA"),renamed);
            OutfitSetStore.RenameSource(s.CostumeRoot,Path.Combine(s.R.Mods,"modA"),renamed);
            Require(Json.Serializer().Serialize(PointerRecord(Fresh(s,both))).Contains(Json.Serializer().Serialize(renamed).Trim('"')),"Rename did not move the pointer.");
        }

        // A replacement whose mod left the library is copied; restored with the listed ones, all keep one saved order.
        private static void PointerMixedOrder(string root)
        {
            ScopeFixture s=CreateScope(root);
            var fullGame=GameFiles(s);
            Directory.Move(Path.Combine(s.R.Mods,"modB"),Path.Combine(s.R.Mods,"modB gone"));
            OutfitSetInfo mods=OutfitSetStore.CreatePreset(s.CostumeRoot,"Mixed",PresetScope.Replacements,s.Engine);
            Require(mods.Pointers && mods.Replacements.SequenceEqual(new[]{"Script Pack","Cammy Alt (copy)","Cammy Colors"}),"Mixed preset contents: "+String.Join(", ",mods.Replacements));
            // 1.0.10: the recorded source is gone (moved outside the Manager): copied, not guessed, and the details say why.
            string gone="not found at "+s.B.SourcePath, size=Utilities.FriendlyBytes(s.B.Files.Sum(f=>f.Size));
            Require(mods.Copies.Count==1 && mods.Copies[0].Name=="Cammy Alt" && mods.Copies[0].Reason==gone && OutfitSetsForm.Contents(mods).Contains("\r\n1 item has no library mod and is copied ("+size+"):\r\nCammy Alt · "+size+" · "+gone+"\r\n"),"Copied replacement reason: "+OutfitSetsForm.Contents(mods));
            var older=Manifest(mods); ((Dictionary<string,object>)older["Pointers"]).Remove("CopiedReplacementReasons"); WriteManifest(mods,older); mods=Fresh(s,mods);
            Require(mods.Copies.Single().Reason==gone,"A preset saved before 1.0.10 lost the reason: "+mods.Copies.Single().Reason);
            UninstallAll(s,"Script Pack","Cammy Alt","Cammy Colors");
            var r=OutfitSetStore.Restore(s.CostumeRoot,mods,PresetScope.Replacements,s.Engine);
            Require(r.ReplacementsRestored==3 && r.Notes.Count==0 && String.Join("|",s.Order())=="Script Pack|Cammy Alt|Cammy Colors|Slotsloader","Mixed restore order: "+String.Join("|",s.Order())+" "+String.Join(" | ",r.Notes));
            SameGame(fullGame,GameFiles(s),"Mixed restore");
            // Only the copied one missing: back between the listed ones.
            UninstallAll(s,"Cammy Alt");
            r=OutfitSetStore.Restore(s.CostumeRoot,mods,PresetScope.Replacements,s.Engine);
            Require(r.ReplacementsRestored==1 && String.Join("|",s.Order())=="Script Pack|Cammy Alt|Cammy Colors|Slotsloader","Copied one restored out of order: "+String.Join("|",s.Order()));
        }

        // 1.0.10: library mods renamed outside the Manager (SourceRelink).
        private static void CopyTree(string from,string to)
        {
            foreach(string file in Directory.GetFiles(from,"*",SearchOption.AllDirectories)) { string target=Path.Combine(to,file.Substring(from.Length+1)); Directory.CreateDirectory(Path.GetDirectoryName(target)); File.Copy(file,target); }
        }
        private static LibraryEntry Listed(string path,string modName)
        {
            bool folder=Directory.Exists(path);
            return new LibraryEntry {SourcePath=path,Name=folder ? Path.GetFileName(path) : Path.GetFileNameWithoutExtension(path),Kind=folder ? "Folder" : Path.GetExtension(path).TrimStart('.').ToUpperInvariant(),Size=folder ? 0 : new FileInfo(path).Length,DisplayName=modName ?? ""};
        }
        private static void PointerRelink(string root)
        {
            string previousHistory=HistoryStore.DefaultDir; HistoryStore.DefaultDir=Path.Combine(root,"history");
            try
            {
                ScopeFixture s=CreateScope(Path.Combine(root,"one"));
                string mods=s.R.Mods, oldA=Path.Combine(mods,"modA"), oldB=Path.Combine(mods,"modB"), oldOutfit=Path.Combine(root,"one","outfit-source");
                ReplaceMod a=s.Engine.LoadState().Mods.Single(m=>m.Name=="Cammy Colors");
                Require(a.Files.Any(f=>!String.IsNullOrEmpty(f.Fixes)) && a.Files.Any(f=>String.IsNullOrEmpty(f.Fixes)) && a.Files.Any(f=>f.Path.Contains("_albd.tex.") && !f.Path.EndsWith(".tex.143230113")),"Fixture: Cammy Colors needs a fixed file, an unchanged one and its texture renamed from .tex.143230113: "+String.Join(", ",a.Files.Select(f=>f.Path+" ["+f.Fixes+"]")));
                var fullGame=GameFiles(s);
                Func<string> outfitFiles=delegate { InstalledOutfit o=Installer.ListInstalled(s.CostumeRoot).Single(x=>x.Name=="Managed A"); return String.Join("\n",Fingerprint(o.Folder).Split('\n').Where(l=>!l.StartsWith(".slots-installer.json="))); };
                string outfitBefore=outfitFiles();
                // Renamed outside the Manager: Cammy Colors is now a ZIP named after the mod (beside a same-named ZIP with one
                // file changed), Cammy Alt a folder with a version in its name, the extra outfit's source too.
                string zipA=Path.Combine(mods,"Cammy Colors.zip"), otherA=Path.Combine(mods,"Cammy_Colors.zip"), newB=Path.Combine(mods,"Cammy Alt v1.1"), newOutfit=Path.Combine(root,"one","outfit-source v2");
                string changed=Path.Combine(root,"one","changed-a"); CopyTree(oldA,changed);
                File.WriteAllText(Path.Combine(changed,@"natives\stm\product\model\esf\esf009\002\01\custom_a.tex.241101895"),"custom texture X");   // same size, other bytes
                ZipFile.CreateFromDirectory(oldA,zipA); ZipFile.CreateFromDirectory(changed,otherA); Directory.Delete(oldA,true);
                Directory.Move(oldB,newB); Directory.Move(oldOutfit,newOutfit);
                List<LibraryMod> library=OutfitSetStore.LibraryMods(new[]{Listed(otherA,"Cammy Colors"),Listed(zipA,"Cammy Colors"),Listed(newB,"Cammy Alt"),Listed(Path.Combine(mods,"tool","Script Pack"),"Script Pack"),Listed(newOutfit,"Outfit set fixture"),Listed(Path.Combine(mods,"modC"),"Costume with helper")});
                OutfitSetInfo set=OutfitSetStore.CreatePreset(s.CostumeRoot,"Renamed",PresetScope.Both,s.Engine,library);
                SameGame(fullGame,GameFiles(s),"Relinking at save"); Require(outfitFiles()==outfitBefore,"Relinking changed the outfit's files.");
                List<ReplaceMod> state=s.Engine.LoadState().Mods;
                Require(String.Equals(state.Single(m=>m.Name=="Cammy Colors").SourcePath,zipA,StringComparison.OrdinalIgnoreCase) && String.Equals(state.Single(m=>m.Name=="Cammy Alt").SourcePath,newB,StringComparison.OrdinalIgnoreCase),"Relinked sources: "+String.Join(", ",state.Select(m=>m.Name+" = "+m.SourcePath)));
                InstalledOutfit managed=Installer.ListInstalled(s.CostumeRoot).Single(o=>o.Name=="Managed A");
                Require(String.Equals(managed.SourcePath,newOutfit,StringComparison.OrdinalIgnoreCase),"The extra outfit's source was not relinked: "+managed.SourcePath);
                Require(set.Copies.Count==1 && set.Copies[0].Name=="Juri/Manual zip.zip" && set.Replacements.SequenceEqual(new[]{"Script Pack","Cammy Alt","Cammy Colors"}) && set.Outfits.Contains("Ingrid/Managed A"),"Relinked preset: copies "+String.Join(", ",set.Copies.Select(c=>c.Name))+"; "+String.Join(", ",set.Replacements));
                var events=File.ReadAllLines(Path.Combine(HistoryStore.DefaultDir,HistoryStore.FileName)).Select(l=>HistoryJson.Read(l)).Where(e=>e!=null && e.Kind==HistoryKinds.Rename).ToList();
                Require(events.Count==3 && events.All(e=>e.Detail.EndsWith(", found by its files")) && events.Any(e=>String.Equals(e.Source,zipA,StringComparison.OrdinalIgnoreCase) && String.Equals(e.PrevId,oldA,StringComparison.OrdinalIgnoreCase)),"Rename events: "+String.Join(" | ",events.Select(e=>e.PrevId+" -> "+e.Source+" ("+e.Detail+")")));
                // Restored from the new sources: the same game files and outfits.
                UninstallAll(s,"Script Pack","Cammy Alt","Cammy Colors");
                OutfitSetStore.UninstallOutfits(s.CostumeRoot,Installer.ListInstalled(s.CostumeRoot),new CheckRecycleBin());
                var r=OutfitSetStore.Restore(s.CostumeRoot,set,PresetScope.Both,s.Engine);
                Require(r.ReplacementsRestored==3 && r.Restored==2 && r.Notes.Count==0,"Restore from the renamed mods: "+r.ReplacementsRestored+"/"+r.Restored+" "+String.Join(" | ",r.Notes));
                SameGame(fullGame,GameFiles(s),"Restore from the renamed mods");
                Require(String.Join("|",s.Order())=="Script Pack|Cammy Alt|Cammy Colors|Slotsloader","Restored order: "+String.Join("|",s.Order()));

                // Two identical candidates, or only a same-named one with other bytes: left as is, copied with the reason.
                ScopeFixture t=CreateScope(Path.Combine(root,"two"));
                string twoA=Path.Combine(t.R.Mods,"modA"), twoB=Path.Combine(t.R.Mods,"modB");
                string sameB1=Path.Combine(t.R.Mods,"Cammy Alt"), sameB2=Path.Combine(t.R.Mods,"Cammy Alt.zip"), onlyA=Path.Combine(t.R.Mods,"Cammy Colors.zip");
                string changedTwo=Path.Combine(root,"two","changed-a"); CopyTree(twoA,changedTwo);
                File.WriteAllText(Path.Combine(changedTwo,@"natives\stm\product\model\esf\esf009\002\01\custom_a.tex.241101895"),"custom texture X");
                ZipFile.CreateFromDirectory(changedTwo,onlyA); Directory.Delete(twoA,true);
                ZipFile.CreateFromDirectory(twoB,sameB2); Directory.Move(twoB,sameB1);
                string stateBefore=File.ReadAllText(t.Engine.StatePath);
                OutfitSetInfo kept=OutfitSetStore.CreatePreset(t.CostumeRoot,"Unsure",PresetScope.Replacements,t.Engine,OutfitSetStore.LibraryMods(new[]{Listed(sameB1,"Cammy Alt"),Listed(sameB2,"Cammy Alt"),Listed(onlyA,"Cammy Colors")}));
                Require(File.ReadAllText(t.Engine.StatePath)==stateBefore,"An unsure match changed the replace state.");
                Require(kept.Copies.Count==2 && kept.Copies.Any(c=>c.Name=="Cammy Alt" && c.Reason=="not found at "+twoB) && kept.Copies.Any(c=>c.Name=="Cammy Colors" && c.Reason=="not found at "+twoA),"Unsure copies: "+String.Join(" | ",kept.Copies.Select(c=>c.Name+": "+c.Reason)));
            }
            finally { HistoryStore.DefaultDir=previousHistory; }
        }

        // 1.0.10: a manual drop whose bytes equal a listed library archive is listed as a pointer to it, not copied.
        private static void PointerTwins(string root)
        {
            ScopeFixture s=CreateScope(root);
            string previousData=ArchiveInstalls.DataDirOverride; ArchiveInstalls.DataDirOverride=Path.Combine(root,"manager-data");
            try
            {
                string drop=Safety.Under(s.CostumeRoot,"Juri/Manual zip.zip"), other=Safety.Under(s.CostumeRoot,"Juri/Other drop.zip");
                byte[] bytes=File.ReadAllBytes(drop), otherBytes=new byte[]{1,2,3};
                File.WriteAllBytes(other,otherBytes);
                string library=Path.Combine(root,"library"); Directory.CreateDirectory(library);
                string decoy=Path.Combine(library,"Decoy.zip"), twin=Path.Combine(library,"Juri twin.zip"), near=Path.Combine(library,"Near miss.zip");
                File.WriteAllBytes(decoy,bytes.Take(bytes.Length-1).Concat(new byte[]{(byte)(bytes[bytes.Length-1]^1)}).ToArray());   // same size, other bytes, listed first
                File.WriteAllBytes(twin,bytes);
                File.WriteAllBytes(near,new byte[]{1,2,4});
                var listed=new List<LibraryEntry> { new LibraryEntry {SourcePath=decoy,Size=bytes.Length,Kind="ZIP"}, new LibraryEntry {SourcePath=twin,Size=bytes.Length,Kind="ZIP"},
                    new LibraryEntry {SourcePath=near,Size=3,Kind="ZIP"}, new LibraryEntry {SourcePath=Path.Combine(root,"outfit-source"),Size=0,Kind="Folder"} };
                List<LibraryMod> archives=OutfitSetStore.LibraryMods(listed);
                Require(archives.Count==4 && archives.Count(a=>a.Size>0)==3,"Listed library mods: "+archives.Count);
                OutfitSetInfo set=OutfitSetStore.CreatePreset(s.CostumeRoot,"Twins",PresetScope.Both,s.Engine,archives);
                var outfits=((System.Collections.IEnumerable)PointerRecord(set)["Outfits"]).Cast<Dictionary<string,object>>().ToList();
                var pointer=outfits.SingleOrDefault(o=>(string)o["Folder"]=="Manual zip.zip");
                Require(pointer!=null && (string)pointer["Character"]=="Juri" && (string)pointer["Mode"]=="all_variants" && String.Equals((string)pointer["SourcePath"],twin,StringComparison.OrdinalIgnoreCase),"The manual drop is not a pointer to its twin: "+Json.Serializer().Serialize(outfits));
                var entries=((System.Collections.IEnumerable)Manifest(set)["Entries"]).Cast<Dictionary<string,object>>().ToList();
                Require(entries.Count==1 && (string)entries[0]["RelativePath"]=="Juri/Other drop.zip","Copied entries: "+String.Join(", ",entries.Select(e=>(string)e["RelativePath"])));
                Require(set.Copied==1 && set.Copies.Count==1 && set.Copies[0].Name=="Juri/Other drop.zip" && set.Copies[0].Bytes==3 && set.Copies[0].Reason==OutfitSetStore.ManualDrop,"Copies: "+String.Join(", ",set.Copies.Select(c=>c.Name+" "+c.Bytes+" "+c.Reason)));
                Require(set.Outfits.Contains("Juri/Manual zip.zip") && set.Outfits.Contains("Juri/Other drop.zip (copy)"),"Listed contents: "+String.Join(", ",set.Outfits));
                // The window: what is copied, its size and why; the save summary; the subtitle as before.
                string size=Utilities.FriendlyBytes(3), details=OutfitSetsForm.Contents(set);
                Require(details.Contains("\r\n1 item has no library mod and is copied ("+size+"):\r\nJuri/Other drop.zip · "+size+" · manual drop\r\n"),"Details: "+details);
                Require(OutfitSetsForm.SaveSummary(set)=="Saved “Twins” · 3 extra outfits · 3 replacements · 1 copied ("+size+"), listed below.","Save summary: "+OutfitSetsForm.SaveSummary(set));
                Require(OutfitSetsForm.Subtitle(set).StartsWith("3 extra outfits · 3 replacements · 1 copied · "+Utilities.FriendlyBytes(set.Bytes)+" · "),"Subtitle: "+OutfitSetsForm.Subtitle(set));
                var two=new OutfitSetInfo {Pointers=true,Copies=new List<OutfitSetCopy>{new OutfitSetCopy{Name="A",Bytes=1024},new OutfitSetCopy{Name="B",Bytes=1024,Reason="manual drop"}}};
                Require(OutfitSetsForm.CopiedLines(two).SequenceEqual(new[]{"2 items have no library mod and are copied ("+Utilities.FriendlyBytes(2048)+"):","A · "+Utilities.FriendlyBytes(1024),"B · "+Utilities.FriendlyBytes(1024)+" · manual drop"}),"Plural lines: "+String.Join(" | ",OutfitSetsForm.CopiedLines(two)));
                // Without a listed library both drops are copied, as in 1.0.8.
                OutfitSetInfo plain=OutfitSetStore.CreatePreset(s.CostumeRoot,"No library",PresetScope.ExtraOutfits,null);
                Require(plain.Copies.Count==2 && plain.Copies.All(c=>c.Reason==OutfitSetStore.ManualDrop),"Without the library: "+plain.Copies.Count);
                // Still installed: the identical drop counts as the saved outfit.
                var r=OutfitSetStore.Restore(s.CostumeRoot,set,PresetScope.ExtraOutfits,s.Engine);
                Require(r.Restored==0 && r.Skipped==3 && r.Notes.Count==0,"Restore while installed: "+r.Restored+"/"+r.Skipped+" "+String.Join(" | ",r.Notes));
                // Gone: the drop comes back from its twin under its own name, linked to the twin; the other from its copy.
                OutfitSetStore.UninstallOutfits(s.CostumeRoot,Installer.ListInstalled(s.CostumeRoot),new CheckRecycleBin());
                Require(Installer.ListInstalled(s.CostumeRoot).Count==0,"Fixture: outfits left after uninstall.");
                r=OutfitSetStore.Restore(s.CostumeRoot,set,PresetScope.ExtraOutfits,s.Engine);
                Require(r.Restored==3 && r.Notes.Count==0,"Restore: "+r.Restored+" "+String.Join(" | ",r.Notes));
                Require(File.ReadAllBytes(drop).SequenceEqual(bytes) && !File.Exists(Safety.Under(s.CostumeRoot,"Juri/Juri twin.zip")) && File.ReadAllBytes(other).SequenceEqual(otherBytes),"Restored drops differ.");
                InstalledOutfit back=Installer.ListInstalled(s.CostumeRoot).Single(o=>String.Equals(o.Folder,drop,StringComparison.OrdinalIgnoreCase));
                Require(back.WholeArchive && ReplacePlanner.SameSource(back.SourcePath,twin),"The restored drop is not linked to its library twin.");
                r=OutfitSetStore.Restore(s.CostumeRoot,set,PresetScope.ExtraOutfits,s.Engine);
                Require(r.Restored==0 && r.Skipped==3 && r.Notes.Count==0,"Second restore: "+r.Restored+"/"+r.Skipped+" "+String.Join(" | ",r.Notes));
                // Same size, other bytes in the place: a different outfit, left as is.
                File.WriteAllBytes(drop,File.ReadAllBytes(decoy));
                r=OutfitSetStore.Restore(s.CostumeRoot,set,PresetScope.ExtraOutfits,s.Engine);
                Require(r.Restored==0 && r.Skipped==2 && r.Notes.SequenceEqual(new[]{"Juri/Manual zip.zip: a different outfit is installed there; left as is."}) && File.ReadAllBytes(drop).SequenceEqual(File.ReadAllBytes(decoy)),"A changed drop: "+String.Join(" | ",r.Notes));
                // A preset saved before 1.0.10 (no reasons kept): a manual entry still says manual drop.
                var older=Manifest(set); ((Dictionary<string,object>)older["Pointers"]).Remove("CopiedOutfitReasons"); WriteManifest(set,older);
                Require(Fresh(s,set).Copies.Single().Reason==OutfitSetStore.ManualDrop,"An older preset's copy reason: "+Fresh(s,set).Copies.Single().Reason);
            }
            finally { ArchiveInstalls.DataDirOverride=previousData; }
        }

        private static void RunningRefused(string root,FakeProcessProbe probe)
        {
            ScopeFixture s=CreateScope(root);
            OutfitSetInfo mods=OutfitSetStore.CreateCopyPreset(s.CostumeRoot,"Mods",PresetScope.Replacements,s.Engine);
            probe.Game=true;
            var same=OutfitSetStore.Restore(s.CostumeRoot,mods,PresetScope.Replacements,s.Engine);
            Require(same.ReplacementsSkipped==3 && same.ReplacementsRestored==0,"An all-identical restore should pass while SF6 runs (nothing changes).");
            probe.Game=false; UninstallAll(s,"Cammy Alt");
            var game=GameFiles(s); string state=File.ReadAllText(s.Engine.StatePath);
            string store=s.R.Engine.Settings.StoreRoot(s.R.Data); var stored=Directory.GetDirectories(store).Select(Path.GetFileName).OrderBy(n=>n).ToList();
            probe.Game=true;
            try
            {
                string message=null; try { OutfitSetStore.Restore(s.CostumeRoot,mods,PresetScope.Replacements,s.Engine); } catch(IOException ex) { message=ex.Message; }
                Require(message!=null && message.StartsWith("Close SF6 to restore replacements"),"SF6 running was not refused: "+message);
            }
            finally { probe.Game=false; }
            probe.Fluffy=true;
            try
            {
                string message=null; try { OutfitSetStore.Restore(s.CostumeRoot,mods,PresetScope.Replacements,s.Engine); } catch(IOException ex) { message=ex.Message; }
                Require(message!=null && message.StartsWith("Close the external mod manager to restore replacements"),"Fluffy open was not refused for PAK rows: "+message);
            }
            finally { probe.Fluffy=false; }
            SameGame(game,GameFiles(s),"Refused restores"); Require(File.ReadAllText(s.Engine.StatePath)==state,"A refused restore changed the state.");
            Require(Directory.GetDirectories(store).Select(Path.GetFileName).OrderBy(n=>n).SequenceEqual(stored),"A refused restore left store folders behind.");
            var r=OutfitSetStore.Restore(s.CostumeRoot,mods,PresetScope.Replacements,s.Engine);
            Require(r.ReplacementsRestored==1 && String.Join("|",s.Order())=="Slotsloader|Script Pack|Cammy Alt|Cammy Colors","Retry after the refusals: "+String.Join("|",s.Order()));
        }
    }
}
