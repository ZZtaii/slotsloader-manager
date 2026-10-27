using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace SlotsInstaller
{
    // Real archives are read only. Every write uses a fresh test directory in this workspace.
    internal static class IntegrationTests
    {
        private static int passed;
        private static int failed;
        private static string root;
        private static string library;
        private static string costumeRoot;
        private static string simpleSource;
        private static string sevenZip;
        private static string previousIdentityCacheDirectory;
        private static List<LibraryEntry> realLibraryEntries;
        private static ModPackage simplePackage;
        private static CompositionPlan simplePlan;

        public static int Run(string testRoot)
        {
            passed = 0;
            failed = 0;
            realLibraryEntries = null;
            previousIdentityCacheDirectory = Library.IdentityCacheDirectory;
            try
            {
                string workspace = Environment.GetEnvironmentVariable("SLOTSLOADER_WORKSPACE");   // 2.4.3: set by test.ps1, never compiled in
                if (String.IsNullOrWhiteSpace(workspace)) workspace = LocalTestConfig.Current.Workspace;
                string requested = Path.GetFullPath(testRoot);
                Require(!String.IsNullOrWhiteSpace(workspace) && IsWithin(requested, workspace), "Self-test root must be inside the test workspace (SLOTSLOADER_WORKSPACE).");
                root = Path.Combine(requested, "run-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                Directory.CreateDirectory(root);
                library = Path.Combine(root, "library");
                Directory.CreateDirectory(library);
                Library.IdentityCacheDirectory = Path.Combine(root, "identity-cache");
                Library.ClearIdentityCache();
                costumeRoot = Path.Combine(root, "sandbox-game", "reframework", "costume_mods");
                Directory.CreateDirectory(costumeRoot);
                File.WriteAllText(Path.Combine(root, "sandbox-game", "StreetFighter6.exe"), "synthetic fixture executable");
                sevenZip = @"C:\Program Files\7-Zip\7z.exe";
                simpleSource = MakeSimpleDirectory(Path.Combine(library, "Simple Ingrid"));
                simplePackage = PackageReader.Load(simpleSource, sevenZip);
                simplePlan = Composer.Plan(simplePackage, simplePackage.Components.Where(c => !c.IsDummy && c.Assets.Count > 0).Select(c => c.Id));

                Console.WriteLine("Self-test output: " + root);
                Test("single directory metadata, payload and character", TestDirectory);
                Test("ZIP wrapper and metadata preview", TestZip);
                Test("7Z archive and source provenance", TestSevenZip);
                Test("solid 7Z batches metadata and preview reads", TestSevenZipBatch);
                Test("long paths: short staging names, too long stops before 7-Zip (1.0.15)", TestLongPaths);
                Test("existing RAR reader", TestRar);
                Test("library deduplication and archive kinds", TestLibrary);
                Test("four-option normal and five-option bundle indexing survives cache reload", TestLibraryOptionCount);
                Test("installed source coverage distinguishes partial, full, disabled and manual outfits", TestLibraryInstallState);
                Test("Goth Juri bundle composition, overrides and native merge", TestJuri);
                Test("Ingrid standalone archive and 29-file merge", TestIngrid);
                Test("managed enable, disable and recoverable uninstall", TestLifecycle);
                Test("managed replacement and rollback on missing source asset", TestReplacementRollback);
                Test("unmanaged outfit collision preserves existing files", TestUnmanagedCollision);
                Test("incompatible character target is refused", TestTargetIdentity);
                Test("invalid Windows outfit names are refused", TestInvalidNames);
                Test("malicious ZIP parent traversal is refused", TestUnsafeZip);
                Test("non-native package files are excluded", TestNonNativeFiles);
                Test("vanilla MDF fallback remains installable with warning", TestMissingMaterial);
                Test("nested backup native root remains an unselected component", TestNestedBackup);
                Test("independent addon bundle branches have scoped requirements", TestIndependentBranches);
                Test("numeric ZIP and donor-named RAR use native character identity", TestRealLibraryCharacters);
                Test("all 32 character IDs retain exact canonical roster mapping", TestRosterLibraryCharacters);
                Test("folder character identity refreshes when native paths change", TestFolderCharacterRefresh);
                Test("ZIP identity cache invalidates changed archives and explicit refresh", TestZipCharacterRefresh);
                Test("multiple and unmapped native IDs never use a false filename identity", TestAmbiguousLibraryCharacters);
                Test("filename fallback stays separate from authoritative native identity", TestLibraryFilenameFallback);
                Test("large preview sets retain a bounded cache and inherited previews share bytes", TestBoundedPreviews);
                Test("nested choice branches are validated without rejecting their children", TestChoiceBranches);
                Test("concurrent lifecycle operations are refused for the same root", TestMutationLock);
                Test("destinations created or changed during staging are preserved", TestCommitRace);
                Test("manifest hashes match all installed fixture payloads", TestManifestHashes);
            }
            catch (Exception ex)
            {
                failed++;
                Console.WriteLine("FAIL setup: " + ex.Message);
            }
            finally { Library.IdentityCacheDirectory = previousIdentityCacheDirectory; }
            Console.WriteLine("RESULT: " + passed + " passed, " + failed + " failed");
            Console.WriteLine("Generated fixtures retained for inspection: " + root);
            return failed == 0 ? 0 : 1;
        }

        private static void Test(string name, Action action)
        {
            try
            {
                action();
                passed++;
                Console.WriteLine("PASS " + name);
            }
            catch (Exception ex)
            {
                failed++;
                Console.WriteLine("FAIL " + name + ": " + ex.ToString());
            }
        }

        private static void TestDirectory()
        {
            Require(simplePackage.Components.Count == 1, "Expected one directory component.");
            Require(simplePackage.CharacterCodes.Contains("032"), "Expected Ingrid native character code 032.");
            Require(simplePackage.Character == "Ingrid", "Expected Ingrid identity.");
            Require(simplePackage.Components[0].Name == "Simple Ingrid", "Colon metadata key should parse.");
            Require(simplePackage.Components[0].Author == "Fixture", "Whitespace/case metadata should parse.");
            Require(simplePlan.CanInstall, "Minimal body mesh/MDF directory should compose: " + string.Join("; ", simplePlan.Errors));
            Require(simplePlan.Files.Count == 3, "Expected three native fixture files.");
            Require(simplePlan.Files.Keys.All(k => k.StartsWith("natives/", StringComparison.OrdinalIgnoreCase)), "Native paths must retain the native root.");
        }

        private static void TestZip()
        {
            string zipPath = Path.Combine(library, "Simple Ingrid.zip");
            string wrapped = Path.Combine(root, "zip-wrapper");
            string packageDir = MakeSimpleDirectory(Path.Combine(wrapped, "Arbitrary wrapper", "Simple Ingrid"));
            ZipFile.CreateFromDirectory(wrapped, zipPath);
            ModPackage package = PackageReader.Load(zipPath, sevenZip);
            Require(package.Components.Count == 1, "ZIP wrapper must not create extra components.");
            Require(package.Components[0].Assets.Count == 3, "ZIP native assets missing.");
            Require(package.Components[0].Metadata.ContainsKey("nameasbundle"), "Unknown/extra metadata should survive.");
            byte[] preview = package.PreviewBytes(package.Components[0].Id, sevenZip);
            Require(preview != null && preview.SequenceEqual(File.ReadAllBytes(Path.Combine(packageDir, "screenshot.png"))), "Screenshot filename fallback should read correct bytes.");
            CompositionPlan plan = Composer.Plan(package, new[] { package.Components[0].Id });
            InstallResult result = Installer.Install(plan, costumeRoot, "Ingrid", "ZIP fixture", sevenZip, false);
            Require(result.FileCount == 3 && CountNatives(result.Folder) == 3, "ZIP extraction count mismatch.");
            AssertFileEqual(Path.Combine(packageDir, MeshRelative()), Path.Combine(result.Folder, MeshRelative()));
        }

        private static void TestSevenZip()
        {
            Require(File.Exists(sevenZip), "7-Zip tool is unavailable.");
            string path = Path.Combine(library, "Simple Ingrid.7z");
            RunTool(sevenZip, "a -t7z -y " + Quote(path) + " " + Quote(simpleSource), root);
            ModPackage package = PackageReader.Load(path, sevenZip);
            Require(package.Components.Count == 1 && package.Components[0].Assets.Count == 3, "7Z native asset metadata mismatch.");
            CompositionPlan plan = Composer.Plan(package, package.Components.Where(c => c.Assets.Count > 0).Select(c => c.Id));
            Require(plan.CanInstall, "7Z plan should install.");
            InstallResult result = Installer.Install(plan, costumeRoot, "Ingrid", "7Z fixture", sevenZip, false);
            AssertFileEqual(Path.Combine(simpleSource, MeshRelative()), Path.Combine(result.Folder, MeshRelative()));
            Require(File.Exists(Path.Combine(result.Folder, ".slots-installer.json")), "Managed provenance missing.");
            Require(result.FileCount == 3 && CountNatives(result.Folder) == 3, "7Z extraction count mismatch.");
        }

        private static void TestSevenZipBatch()
        {
            Require(File.Exists(sevenZip), "7-Zip tool is unavailable.");
            string source = MakeSimpleDirectory(Path.Combine(root,"batch-7z-source","Base"));
            WriteFixture(source,"Alternative/modinfo.ini",Encoding.UTF8.GetBytes("name=Alternative\n"));
            byte[] image = File.ReadAllBytes(Path.Combine(source,"screenshot.png"));
            WriteFixture(source,"Alternative/screenshot.png",image);
            string path = Path.Combine(library,"Batch previews.7z");
            RunTool(sevenZip,"a -t7z -y " + Quote(path) + " " + Quote(source),root);
            ModPackage package = PackageReader.Load(path,sevenZip,true);
            Require(package.Components.Count == 2 && package.PreloadedPreviews.Count == 2,"7Z metadata and preview files were not batched.");
            ModComponent main = package.Components.First(c => c.Name == "Simple Ingrid");
            ModComponent alternative = package.Components.First(c => c.Name == "Alternative");
            Require(package.PreviewBytes(main.Id,sevenZip).SequenceEqual(image),"Base preview differs after batch extraction.");
            Require(package.PreviewBytes(alternative.Id,sevenZip).SequenceEqual(image),"Alternative preview differs after batch extraction.");
            ModPackage uncached = PackageReader.Load(path,sevenZip);
            Require(uncached.PreloadedPreviews.Count == 0 && uncached.Components.Count == package.Components.Count,"Non-UI package load changed with preview preloading.");
        }

        // 1.0.15 (LongPaths.cs): a user's 7-Zip scratch path was 265 characters. The fixture root is deeper than any real
        // game folder, so the limit is moved to sit exactly at this fixture's longest scratch path.
        private static void TestLongPaths()
        {
            Require(File.Exists(sevenZip), "7-Zip tool is unavailable.");
            string path = Path.Combine(library, "Long paths.7z");
            RunTool(sevenZip, "a -t7z -y " + Quote(path) + " " + Quote(simpleSource), root);
            ModPackage package = PackageReader.Load(path, sevenZip);
            CompositionPlan plan = Composer.Plan(package, package.Components.Where(c => c.Assets.Count > 0).Select(c => c.Id));
            string staging = Path.Combine(Path.GetDirectoryName(costumeRoot), ".slotsloader-installer-staging");
            string longest = plan.Files.Values.Select(f => f.Asset.EntryPath).OrderByDescending(p => p.Length).First();
            var outputs = new List<string>(); bool ran = false;
            Func<string,string,string> previous = SourceReader.SevenZipRun;
            try
            {
                SourceReader.SevenZipRun = delegate(string exe, string args)
                {
                    ran = true;
                    int at = args.IndexOf("\"-o", StringComparison.Ordinal); outputs.Add(args.Substring(at + 3, args.IndexOf('"', at + 3) - at - 3));
                    return ProcessUtil.RunText(exe, args);
                };
                LongPaths.EnabledOverride = false; LongPaths.FileLimitOverride = 32000;
                Installer.Install(plan, costumeRoot, "Ingrid", "Long A", sevenZip, false);
                Require(outputs.Count == 1, "7-Zip should run once.");
                string raw = outputs[0];
                Require(Path.GetFileName(raw) == "r" && System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(Path.GetDirectoryName(raw)), "^[0-9a-f]{8}$") && String.Equals(Path.GetDirectoryName(Path.GetDirectoryName(raw)), staging, StringComparison.OrdinalIgnoreCase), "Scratch should be <staging>\\<8 hex>\\r: " + raw);
                int need = Safety.Under(raw, longest).Length;

                LongPaths.FileLimitOverride = need; ran = false;
                Installer.Install(plan, costumeRoot, "Ingrid", "Long B", sevenZip, false);
                Require(ran && Directory.Exists(Path.Combine(costumeRoot, "Ingrid", "Long B")), "A scratch path exactly at the limit should install.");

                LongPaths.FileLimitOverride = need - 1; ran = false;
                int before = Directory.GetDirectories(staging).Length;
                try { Installer.Install(plan, costumeRoot, "Ingrid", "Long C", sevenZip, false); throw new InvalidOperationException("A scratch path over the limit installed."); }
                catch (IOException ex) { Require(ex.Message.StartsWith("This path is too long for Windows (" + need + " characters; the limit is " + (need - 1)), "Unexpected message: " + ex.Message); }
                Require(!ran, "7-Zip ran although a path was too long.");
                Require(!Directory.Exists(Path.Combine(costumeRoot, "Ingrid", "Long C")) && Directory.GetDirectories(staging).Length == before, "A refused install left files behind.");

                // Where the files land counts too (ZIP: no 7-Zip scratch).
                ModPackage zip = PackageReader.Load(Path.Combine(library, "Simple Ingrid.zip"), sevenZip);
                CompositionPlan zipPlan = Composer.Plan(zip, new[] { zip.Components[0].Id });
                LongPaths.FileLimitOverride = Safety.Under(Path.Combine(costumeRoot, "Ingrid", "Long D"), zipPlan.Files.Keys.OrderByDescending(p => p.Length).First()).Length - 1;
                try { Installer.Install(zipPlan, costumeRoot, "Ingrid", "Long D", sevenZip, false); throw new InvalidOperationException("An installed path over the limit installed."); }
                catch (IOException ex) { Require(ex.Message.StartsWith("This path is too long for Windows"), "Unexpected message: " + ex.Message); }
                Require(!Directory.Exists(Path.Combine(costumeRoot, "Ingrid", "Long D")) && Directory.GetDirectories(staging).Length == before, "A refused ZIP install left files behind.");
            }
            finally { SourceReader.SevenZipRun = previous; LongPaths.EnabledOverride = null; LongPaths.FileLimitOverride = null; }
        }

        private static void TestBoundedPreviews()
        {
            string source=MakeSimpleDirectory(Path.Combine(root,"many-previews"));
            byte[] image=File.ReadAllBytes(Path.Combine(source,"screenshot.png"));
            for (int i=0;i<17;i++) WriteFixture(source,"Extra"+i+"/screenshot.png",image);
            WriteFixture(source,"Child/modinfo.ini",Encoding.UTF8.GetBytes("name=Child\n"));
            string archive=Path.Combine(root,"many-previews.7z");
            RunTool(sevenZip,"a -t7z -y "+Quote(archive)+" "+Quote(source),root);
            var p=PackageReader.Load(archive,sevenZip,true);
            Require(p.PreloadedPreviews.Count==16,"Large image set disabled preloading or exceeded the count limit.");
            Require(p.PreloadedPreviews.Values.Sum(b=>(long)b.Length)<=16L*1024*1024,"Preload byte budget exceeded.");
            var parent=p.Components.Single(c=>c.Name=="Simple Ingrid");
            var child=p.Components.Single(c=>c.Name=="Child");
            p.PreloadedPreviews.Clear();
            byte[] first=p.PreviewBytes(parent.Id,sevenZip), inherited=p.PreviewBytes(child.Id,sevenZip);
            Require(Object.ReferenceEquals(first,inherited) && first.SequenceEqual(image),"Inherited preview was not cached by its resolved entry.");
            Require(p.PreloadedPreviews.Count==1,"Inherited preview cache duplicated an image.");
            // A changed archive must not reuse its old listing for an uncached read.
            p.PreloadedPreviews.Clear();
            File.SetLastWriteTimeUtc(archive,File.GetLastWriteTimeUtc(archive).AddSeconds(5));
            Require(p.PreviewBytes(parent.Id,sevenZip).SequenceEqual(image),"Changed archive listing could not refresh.");
        }

        internal static ModPackage ChoiceFixture()
        {
            var p=new ModPackage();
            p.Components.Add(new ModComponent {Id="Group",Name="Group",IsDummy=true,Description=""});
            foreach (string id in new[]{"A","A/Child","B"})
            {
                var c=new ModComponent {Id=id,Name=id,ParentId=id=="A/Child" ? "A" : "Group",Description=""};
                c.Assets.Add(new NativeAsset {EntryPath=id+"/"+MeshRelative(),DestinationPath=MeshRelative().Replace('\\','/'),Size=1}); p.Components.Add(c);
            }
            p.PrepareChoices(); return p;
        }
        private static void TestChoiceBranches()
        {
            var p=ChoiceFixture();
            Require(Composer.Plan(p,new[]{"A","A/Child"}).CanInstall,"Parent and its child incorrectly counted as two alternatives.");
            Require(!Composer.Plan(p,new[]{"A","B","A/Child"}).CanInstall,"Inferred exclusive branches were allowed together.");
            p.Components[0].Description="Choose one";
            Require(Composer.Plan(p,new[]{"A","A/Child"}).CanInstall,"Explicit choice rejected a selected branch's child.");
            Require(!Composer.Plan(p,new[]{"A","B"}).CanInstall,"Root explicit group skipped exclusivity validation.");
        }
        private static void TestMutationLock()
        {
            var installed=Installer.Install(simplePlan,costumeRoot,"Ingrid","Lock fixture",sevenZip,false);
            var outfit=Installer.ListInstalled(costumeRoot).Single(o=>o.Folder==installed.Folder);
            using (Installer.AcquireMutation(costumeRoot))
            {
                var task=System.Threading.Tasks.Task.Run(delegate {
                    Action[] attempts={delegate {Installer.Install(simplePlan,costumeRoot,"Ingrid","Lock blocked",sevenZip,false);},delegate {Installer.SetEnabled(outfit,costumeRoot,false);},delegate {Installer.Uninstall(outfit,costumeRoot);}};
                    foreach (var attempt in attempts)
                    {
                        bool refused=false;
                        try {attempt();} catch(IOException ex) {refused=ex.Message.Contains("Another Manager task");}
                        Require(refused,"Concurrent mutation did not honor the root lock.");
                    }
                });
                Require(task.Wait(5000),"Concurrent mutation waited instead of returning a useful error.");
            }
            Require(Directory.Exists(outfit.Folder),"Lock test moved the outfit.");
        }
        private static void TestCommitRace()
        {
            string source=MakeSimpleDirectory(Path.Combine(root,"race-source"));
            using(var stream=new FileStream(Path.Combine(source,MeshRelative()),FileMode.Open,FileAccess.Write)) stream.SetLength(64L*1024*1024);
            var p=PackageReader.Load(source,sevenZip); var plan=Composer.Plan(p,Composer.DefaultSelection(p));
            string staging=Path.Combine(Path.GetDirectoryName(costumeRoot),".slotsloader-installer-staging");
            foreach (bool existing in new[]{false,true})
            {
                string name=existing ? "Race changed" : "Race created", dest=Path.Combine(costumeRoot,"Ingrid",name);
                var original=Installer.Install(simplePlan,costumeRoot,"Ingrid",existing ? name : "Race template",sevenZip,false);
                string manifest=File.ReadAllText(Path.Combine(original.Folder,".slots-installer.json"));
                manifest=manifest.Replace("Race template",name);
                int started=0; Exception eventError=null;
                using(var done=new System.Threading.ManualResetEvent(false))
                using(var watcher=new FileSystemWatcher(staging))
                {
                    watcher.IncludeSubdirectories=true; watcher.NotifyFilter=NotifyFilters.DirectoryName;
                    watcher.Created+=delegate {
                        if(System.Threading.Interlocked.Exchange(ref started,1)!=0) return;
                        try {Directory.CreateDirectory(dest); File.WriteAllText(Path.Combine(dest,".slots-installer.json"),manifest+" "); File.WriteAllText(Path.Combine(dest,"competitor.txt"),"preserve");}
                        catch(Exception ex) {eventError=ex;} finally {done.Set();}
                    };
                    watcher.EnableRaisingEvents=true;
                    bool refused=false;
                    try {Installer.Install(plan,costumeRoot,"Ingrid",name,sevenZip,existing);} catch(IOException ex) {refused=ex.Message.Contains("destination changed");}
                    Require(done.WaitOne(5000) && eventError==null,"Race fixture did not create competing destination.");
                    Require(refused && File.Exists(Path.Combine(dest,"competitor.txt")),"Install replaced an unexpected destination.");
                }
            }
        }
        private static void TestManifestHashes()
        {
            foreach(var outfit in Installer.ListInstalled(costumeRoot).Where(o=>o.Managed))
            {
                var manifest=Json.Read<InstallManifest>(Path.Combine(outfit.Folder,".slots-installer.json"));
                // The race-created marker fixture deliberately contains only an ownership record.
                if(outfit.Name=="Race created") continue;
                foreach(var pair in manifest.Sha256)
                    using(var sha=SHA256.Create()) using(var input=File.OpenRead(Path.Combine(outfit.Folder,pair.Key.Replace('/',Path.DirectorySeparatorChar))))
                        Require(pair.Value==BitConverter.ToString(sha.ComputeHash(input)).Replace("-","").ToLowerInvariant(),"Installed manifest hash mismatch: "+outfit.Name+"/"+pair.Key);
            }
        }

        private static void TestRar()
        {
            string path = LocalTestConfig.Current.Archive("viperHair");
            Require(File.Exists(path), "Provided existing RAR library file unavailable.");
            ModPackage package = PackageReader.Load(path, sevenZip);
            Require(package.Components.Count > 0 && package.Components.Any(c => c.Assets.Count > 0), "RAR reader did not discover native assets.");
            Require(package.CharacterCodes.Contains("030"), "Viper source native code should be found (filename mentions donor Manon hair).");
            ModComponent component = package.Components.First(c => c.Assets.Count > 0);
            package.PreviewBytes(component.Id, sevenZip); // A package without an image is valid.
        }

        private static void TestLibrary()
        {
            List<LibraryEntry> once = Library.Scan(new[] { library });
            List<LibraryEntry> twice = Library.Scan(new[] { library, library.ToUpperInvariant() });
            Require(once.Count == twice.Count, "Repeated library paths should be deduplicated.");
            Require(once.Any(e => e.SourcePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)), "ZIP library entry missing.");
            Require(once.Any(e => e.SourcePath.EndsWith(".7z", StringComparison.OrdinalIgnoreCase)), "7Z library entry missing.");
            Require(once.Any(e => Directory.Exists(e.SourcePath)), "Directory library entry missing.");
        }

        private static void TestLibraryOptionCount()
        {
            string normal = Path.Combine(library, "Four options");
            string bundleSource = Path.Combine(root, "five-option-source");
            for (int i = 1; i <= 5; i++)
            {
                string nativeFile = "Option " + i + "/natives/stm/product/model/esf/esf013/001/01/option" + i + ".mesh.230110883";
                if (i <= 4) WriteFixture(normal, nativeFile, new byte[] { (byte)i });
                WriteFixture(bundleSource, nativeFile, new byte[] { (byte)i });
            }
            string bundle = Path.Combine(library, "Five options.zip");
            ZipFile.CreateFromDirectory(bundleSource, bundle);
            List<LibraryEntry> entries = Library.Scan(new[] { library }, sevenZip);
            LibraryEntry normalEntry = entries.Single(e => e.SourcePath == normal);
            LibraryEntry bundleEntry = entries.Single(e => e.SourcePath == bundle);
            Require(normalEntry.OptionCount == 4 && bundleEntry.OptionCount == 5, "The four/five option boundary was indexed incorrectly.");
            Require(bundleEntry.OptionIds.OrderBy(id => id).SequenceEqual(PackageReader.Load(bundle, sevenZip).Components.Where(c => c.Assets.Count > 0).Select(c => c.Id).OrderBy(id => id)), "Indexed option IDs disagree with selectable components.");
            Require(normalEntry.OptionCount == PackageReader.Load(normal, sevenZip).Components.Count(c => c.Assets.Count > 0), "Folder index disagrees with selectable options.");
            Require(bundleEntry.OptionCount == PackageReader.Load(bundle, sevenZip).Components.Count(c => c.Assets.Count > 0), "ZIP index disagrees with selectable options.");
            string cache = Library.IdentityCacheDirectory;
            Library.IdentityCacheDirectory = cache;
            entries = Library.Scan(new[] { library }, sevenZip);
            Require(entries.Single(e => e.SourcePath == bundle).OptionIds.Count == 5, "Bundle option IDs were lost on disk-cache reload.");

            string cacheFile = Path.Combine(cache, Library.CacheFilename);
            LibraryIdentityCache old = Json.Read<LibraryIdentityCache>(cacheFile);
            old.Records.Single(r => r.SourcePath == bundle).OptionCount = null;
            old.Records.Single(r => r.SourcePath == bundle).OptionIds = null;
            Json.Write(cacheFile, old);
            Library.IdentityCacheDirectory = cache;
            entries = Library.Scan(new[] { library }, sevenZip);
            Require(entries.Single(e => e.SourcePath == bundle).OptionCount == 5, "An older identity-cache entry was not reindexed.");
        }

        private static void TestLibraryInstallState()
        {
            LibraryEntry entry = new LibraryEntry { SourcePath = @"C:\fixture\bundle.zip", OptionCount = 3, OptionIds = new List<string> { "Main", "Skin", "Hair" } };
            InstalledOutfit first = new InstalledOutfit { Managed = true, Enabled = true, SourcePath = entry.SourcePath, SelectedIds = new List<string> { "Main", "Skin" } };
            InstalledOutfit second = new InstalledOutfit { Managed = true, Enabled = true, SourcePath = entry.SourcePath.ToUpperInvariant(), SelectedIds = new List<string> { "Hair" } };
            Require(Library.InstallState(entry, new InstalledOutfit[0]) == LibraryInstallState.None, "Uninstalled source was marked as installed.");
            Require(Library.InstallState(entry, new[] { first }) == LibraryInstallState.Partial, "Incomplete selected options were not partial.");
            Require(Library.InstallState(entry, new[] { first, second }) == LibraryInstallState.Full, "Options across active outfits did not mark full coverage.");
            first.Enabled = false; second.Enabled = false;
            Require(Library.InstallState(entry, new[] { first, second }) == LibraryInstallState.Disabled, "Disabled outfits were shown as active installs.");
            first.Managed = false;
            Require(Library.InstallState(entry, new[] { first }) == LibraryInstallState.None, "Manual outfits were falsely linked to a library source.");
        }

        private static void TestJuri()
        {
            string path = LocalTestConfig.Current.Archive("juri");
            ModPackage package = PackageReader.Load(path, sevenZip);
            Require(package.Components.Count == 19, "Goth Juri expected 19 components.");
            Require(package.Components.Count(c => c.IsDummy) == 4, "Goth Juri expected four dummy groups.");
            Require(package.Character == "Juri" && package.CharacterCodes.Contains("016"), "Goth Juri character mismatch.");
            ModComponent main = Named(package, "1) Main files");
            ModComponent skin = Named(package, "Default");
            ModComponent outfit = Named(package, "Regular full outfit barefoot");
            ModComponent hideShirt = Named(package, "hide shirt");
            Require(main.Assets.Count == 47, "Goth Juri main expected 47 native assets.");
            Require(skin.ParentId == Named(package, "2) Goth Juri skin options").Id, "Trimmed Addonfor parent resolution failed.");
            Require(outfit.ParentId == Named(package, "3) Goth Juri outfit options").Id, "Outfit parent resolution failed.");
            CompositionPlan incomplete = Composer.Plan(package, new[] { main.Id });
            Require(!incomplete.CanInstall && incomplete.Errors.Count > 0, "Main-only Juri package must fail missing mesh/MDF validation.");
            CompositionPlan plan = Composer.Plan(package, new[] { main.Id, skin.Id, outfit.Id });
            Require(plan.CanInstall && plan.Files.Count == 49, "Complete Goth Juri expected 49 assets: " + string.Join("; ", plan.Errors));
            CompositionPlan conflicting = Composer.Plan(package, new[] { main.Id, skin.Id, Named(package, "oily").Id, outfit.Id });
            Require(!conflicting.CanInstall && conflicting.Errors.Any(e => e.IndexOf("one", StringComparison.OrdinalIgnoreCase) >= 0), "Conflicting skin alternatives must require one selected choice.");
            CompositionPlan overlays = Composer.Plan(package, new[] { hideShirt.Id, outfit.Id, skin.Id, main.Id });
            Require(overlays.CanInstall && overlays.Files.Count == 49, "Main should apply before selected optional overlays.");
            Require(overlays.Overrides.Any(o => o.Winner == hideShirt.Name), "Hide-shirt override winner not recorded.");
            NativeAsset overlayAsset = hideShirt.Assets.Single();
            Require(overlays.Files[overlayAsset.DestinationPath].Component.Id == hideShirt.Id, "Selected hide-shirt must replace main shirt file.");
            InstallResult result = Installer.Install(overlays, costumeRoot, "Juri", "Goth Juri self-test", sevenZip, false);
            Require(result.FileCount == 49 && CountNatives(result.Folder) == 49, "Merged Goth Juri native file count mismatch.");
            AssertArchiveFileEqual(path, overlayAsset.EntryPath, Path.Combine(result.Folder, overlayAsset.DestinationPath.Replace('/', Path.DirectorySeparatorChar)));
            NativeAsset body = outfit.Assets.Single();
            AssertArchiveFileEqual(path, body.EntryPath, Path.Combine(result.Folder, body.DestinationPath.Replace('/', Path.DirectorySeparatorChar)));
            Require(!Directory.Exists(Path.Combine(result.Folder, "natives", "natives")), "Native root was accidentally duplicated.");
            Require(File.Exists(Path.Combine(result.Folder, "natives", "stm", "product", "weather", "wind", "mode", "016", "esf016_001_01_Dev_AC_Pose_01_Loop_body.chain.52")), "Shared wind chain path was omitted.");
        }

        private static void TestIngrid()
        {
            string path = LocalTestConfig.Current.Archive("ingrid");
            ModPackage package = PackageReader.Load(path, sevenZip);
            Require(package.Components.Count == 1 && package.Components[0].Assets.Count == 29, "Ingrid expected one 29-file component.");
            Require(package.Character == "Ingrid", "Ingrid native code identity detection failed.");
            byte[] preview = package.PreviewBytes(package.Components[0].Id, sevenZip);
            Require(preview != null && preview.Length == 772670, "Ingrid screenshot.jpg fallback missing.");
            CompositionPlan plan = Composer.Plan(package, Composer.DefaultSelection(package));
            Require(plan.CanInstall && plan.Files.Count == 29, "Ingrid default single-mod selection should be complete.");
            InstallResult result = Installer.Install(plan, costumeRoot, "Ingrid", "Ingrid C.C. Barefoot self-test", sevenZip, false);
            Require(result.FileCount == 29 && CountNatives(result.Folder) == 29, "Ingrid merged native count mismatch.");
            NativeAsset mesh = package.Components[0].Assets.First(a => a.DestinationPath.IndexOf(".mesh.", StringComparison.OrdinalIgnoreCase) >= 0);
            AssertArchiveFileEqual(path, mesh.EntryPath, Path.Combine(result.Folder, mesh.DestinationPath.Replace('/', Path.DirectorySeparatorChar)));
            Require(Directory.Exists(Path.Combine(result.Folder, "natives", "stm", "product", "model", "esf", "esf032", "CCTex")), "Custom texture folder missing.");
        }

        private static void TestLifecycle()
        {
            InstallResult result = Installer.Install(simplePlan, costumeRoot, "Ingrid", "Lifecycle fixture", sevenZip, false);
            InstalledOutfit outfit = InstalledByFolder(result.Folder);
            Require(outfit.Managed && outfit.Enabled, "Fresh outfit must be recognized as enabled managed install.");
            string disabledPath = Installer.SetEnabled(outfit, costumeRoot, false);
            Require(!Directory.Exists(result.Folder) && Directory.Exists(disabledPath), "Disable must move outfit out of Slotsloader's scanned tree.");
            Require(!IsWithin(disabledPath, costumeRoot), "Disabled outfit remains inside costume_mods.");
            InstalledOutfit disabled = InstalledByFolder(disabledPath);
            Require(disabled.Managed && !disabled.Enabled, "Disabled install must still be discoverable.");
            string enabledPath = Installer.SetEnabled(disabled, costumeRoot, true);
            Require(Directory.Exists(enabledPath) && IsWithin(enabledPath, costumeRoot), "Enable must restore outfit to scanned tree.");
            InstalledOutfit enabled = InstalledByFolder(enabledPath);
            string trash = Installer.Uninstall(enabled, costumeRoot);
            Require(!Directory.Exists(enabledPath) && Directory.Exists(trash), "Uninstall must retain a recoverable copy.");
            Require(!IsWithin(trash, costumeRoot), "Removed outfit remains inside scanned costume_mods tree.");
            Require(CountNatives(trash) == 3, "Recoverable uninstall should retain every native file.");
        }

        private static void TestReplacementRollback()
        {
            const string name = "Rollback fixture";
            InstallResult result = Installer.Install(simplePlan, costumeRoot, "Ingrid", name, sevenZip, false);
            byte[] previous = File.ReadAllBytes(Path.Combine(result.Folder, MeshRelative()));
            MustThrow(delegate { Installer.Install(simplePlan, costumeRoot, "Ingrid", name, sevenZip, false); }, "Existing managed outfit requires explicit replace selection.");
            string mesh = Path.Combine(simpleSource, MeshRelative());
            string hidden = mesh + ".temporarily-missing";
            File.Move(mesh, hidden);
            try
            {
                MustThrow(delegate { Installer.Install(simplePlan, costumeRoot, "Ingrid", name, sevenZip, true); }, "Missing source asset must fail staged replacement.");
                Require(Directory.Exists(result.Folder), "Failed replacement destroyed previous install.");
                Require(File.ReadAllBytes(Path.Combine(result.Folder, MeshRelative())).SequenceEqual(previous), "Failed replacement changed previous mesh.");
            }
            finally { File.Move(hidden, mesh); }
            InstallResult replaced = Installer.Install(simplePlan, costumeRoot, "Ingrid", name, sevenZip, true);
            Require(replaced.FileCount == 3 && CountNatives(replaced.Folder) == 3, "Explicit managed replacement failed.");
        }

        private static void TestUnmanagedCollision()
        {
            string destination = Path.Combine(costumeRoot, "Ingrid", "Existing manual outfit");
            Directory.CreateDirectory(destination);
            string sentinel = Path.Combine(destination, "my-existing-file.txt");
            File.WriteAllText(sentinel, "Preserve this exact existing content.");
            MustThrow(delegate { Installer.Install(simplePlan, costumeRoot, "Ingrid", "Existing manual outfit", sevenZip, true); }, "Unmanaged destination must never be replaced.");
            Require(File.ReadAllText(sentinel) == "Preserve this exact existing content.", "Unmanaged outfit was altered.");
            InstalledOutfit existing = InstalledByFolder(destination);
            Require(!existing.Managed, "Manual folder incorrectly recognized as managed.");
            MustThrow(delegate { Installer.Uninstall(existing, costumeRoot); }, "Unmanaged uninstall must be refused.");
            MustThrow(delegate { Installer.SetEnabled(existing, costumeRoot, false); }, "Unmanaged disable must be refused.");
            Require(File.Exists(sentinel), "Unmanaged lifecycle action changed existing folder.");
        }

        private static void TestTargetIdentity()
        {
            MustThrow(delegate { Installer.Install(simplePlan, costumeRoot, "Juri", "Wrong-character fixture", sevenZip, false); }, "Ingrid native assets must not be installed as a Juri costume.");
            Require(!Directory.Exists(Path.Combine(costumeRoot, "Juri", "Wrong-character fixture")), "Incompatible character destination was created.");
            MustThrow(delegate { Installer.Install(simplePlan, costumeRoot, "..", "Target traversal fixture", sevenZip, false); }, "Character path traversal must be refused.");
        }

        private static void TestInvalidNames()
        {
            foreach (string invalid in new[] { "../Escape", "..", "CON", "Bad:Name", "bad/name", "bad\\name", ".", ".hidden", "natives", "trailing.", "trailing " })
            {
                string testName = invalid;
                MustThrow(delegate { Installer.Install(simplePlan, costumeRoot, "Ingrid", testName, sevenZip, false); }, "Invalid outfit name accepted: " + invalid);
            }
            Require(!Directory.Exists(Path.Combine(root, "Escape")), "Name traversal escaped target.");
        }

        private static void TestUnsafeZip()
        {
            string path = Path.Combine(root, "unsafe-traversal.zip");
            using (ZipArchive zip = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                AddEntry(zip, "Bad/modinfo.ini", Encoding.UTF8.GetBytes("name=Unsafe Ingrid"));
                AddEntry(zip, "Bad/" + MeshRelative().Replace('\\', '/'), new byte[] { 1, 2, 3 });
                AddEntry(zip, "Bad/" + MaterialRelative().Replace('\\', '/'), new byte[] { 4, 5, 6 });
                AddEntry(zip, "Bad/natives/stm/../../../../escaped-by-zip.txt", new byte[] { 7, 8, 9 });
            }
            MustThrow(delegate
            {
                ModPackage package = PackageReader.Load(path, sevenZip);
                CompositionPlan plan = Composer.Plan(package, package.Components.Select(c => c.Id));
                Installer.Install(plan, costumeRoot, "Ingrid", "Unsafe ZIP fixture", sevenZip, false);
            }, "Parent traversal archive must be refused before writes.");
            Require(!File.Exists(Path.Combine(root, "escaped-by-zip.txt")), "Malicious ZIP escaped staging root.");
            Require(!Directory.Exists(Path.Combine(costumeRoot, "Ingrid", "Unsafe ZIP fixture")), "Malicious archive produced an installed outfit.");
        }

        private static void TestNonNativeFiles()
        {
            InstallResult result = Installer.Install(simplePlan, costumeRoot, "Ingrid", "Payload filter fixture", sevenZip, false);
            Require(!File.Exists(Path.Combine(result.Folder, "arbitrary-script.lua")), "Source script was copied into outfit.");
            Require(!File.Exists(Path.Combine(result.Folder, "screenshot.png")), "Source image was copied into outfit.");
            Require(CountNatives(result.Folder) == 3, "Unexpected files appeared in merged native payload.");
        }

        private static void TestMissingMaterial()
        {
            string directory = MakeSimpleDirectory(Path.Combine(root, "vanilla-material-fixture"));
            string material = Path.Combine(directory, MaterialRelative());
            File.Move(material, material + ".excluded");
            // The renamed file is still below natives; remove it from the planned fixture
            // by moving it to the package's metadata area before re-reading.
            File.Move(material + ".excluded", Path.Combine(directory, "material-fixture.backup"));
            ModPackage package = PackageReader.Load(directory, sevenZip);
            CompositionPlan plan = Composer.Plan(package, Composer.DefaultSelection(package));
            Require(plan.CanInstall, "Loader's vanilla material fallback should allow a body mesh without local MDF.");
            Require(plan.Warnings.Any(w => w.IndexOf("material", StringComparison.OrdinalIgnoreCase) >= 0 || w.IndexOf("MDF", StringComparison.OrdinalIgnoreCase) >= 0), "Missing local material should produce an informative warning.");
        }

        private static void TestNestedBackup()
        {
            string directory = MakeSimpleDirectory(Path.Combine(root, "nested-backup-fixture"));
            string backup = Path.Combine(directory, "old-backup");
            WriteFixture(backup, MeshRelative(), Encoding.UTF8.GetBytes("obsolete backup mesh must never override current root"));
            WriteFixture(backup, MaterialRelative(), Encoding.UTF8.GetBytes("obsolete backup material"));
            ModPackage package = PackageReader.Load(directory, sevenZip);
            Require(package.Components.Count == 2, "Unannotated nested natives root must remain its own component.");
            ModComponent current = Named(package, "Simple Ingrid");
            ModComponent old = Named(package, "old-backup");
            Require(current.Assets.Count == 3 && old.Assets.Count == 2, "Nested backup was swallowed into parent component.");
            List<string> selected = Composer.DefaultSelection(package);
            Require(selected.Contains(current.Id) && !selected.Contains(old.Id), "Nested backup was selected automatically.");
            CompositionPlan plan = Composer.Plan(package, selected);
            Require(plan.CanInstall && plan.Files.Count == 3 && plan.Overrides.Count == 0, "Root-only selection should ignore nested old assets.");
            Require(plan.Files[MeshRelative().Replace('\\', '/')].Component.Id == current.Id, "Backup mesh won the root selection.");
        }

        private static void TestIndependentBranches()
        {
            string directory = Path.Combine(root, "independent-branches-fixture");
            foreach (string branch in new[] { "A", "B" })
            {
                WriteFixture(directory, branch + "-header/modinfo.ini", Encoding.UTF8.GetBytes("name=Bundle " + branch + "\nDummymod=True\n"));
                WriteFixture(directory, branch + "-main/modinfo.ini", Encoding.UTF8.GetBytes("name=" + branch + " Main files\nAddonfor=Bundle " + branch + "\n"));
                WriteFixture(Path.Combine(directory, branch + "-main"), MeshRelative(), Encoding.UTF8.GetBytes("branch " + branch + " mesh"));
                WriteFixture(directory, branch + "-skin-header/modinfo.ini", Encoding.UTF8.GetBytes("name=" + branch + " skin options\nAddonfor=Bundle " + branch + "\nDummymod=True\ndescription=Select one skin\n"));
                WriteFixture(directory, branch + "-skin/modinfo.ini", Encoding.UTF8.GetBytes("name=" + branch + " skin\nAddonfor=" + branch + " skin options\n"));
                WriteFixture(Path.Combine(directory, branch + "-skin"), MaterialRelative(), Encoding.UTF8.GetBytes("branch " + branch + " material"));
            }
            ModPackage package = PackageReader.Load(directory, sevenZip);
            ModComponent mainA = Named(package, "A Main files");
            ModComponent mainB = Named(package, "B Main files");
            List<string> defaults = Composer.DefaultSelection(package);
            Require(defaults.Contains(mainA.Id) && !defaults.Contains(mainB.Id), "Default selection must choose one main bundle branch.");
            CompositionPlan plan = Composer.Plan(package, new[] { mainA.Id, Named(package, "A skin").Id });
            Require(plan.CanInstall && plan.Files.Count == 2, "Unselected Bundle B requirements blocked complete Bundle A: " + string.Join("; ", plan.Errors));
            CompositionPlan incomplete = Composer.Plan(package, new[] { mainA.Id });
            Require(!incomplete.CanInstall, "Active Bundle A must still enforce its required skin choice.");
            Require(incomplete.Errors.All(e => e.IndexOf("B skin", StringComparison.OrdinalIgnoreCase) < 0), "Inactive Bundle B should not produce choice errors.");
        }

        private static void TestRealLibraryCharacters()
        {
            string numeric = LocalTestConfig.Current.Archive("numericOutfit");
            string donor = LocalTestConfig.Current.Archive("viperHair");
            Require(File.Exists(numeric) && File.Exists(donor), "Real character-detection regression archives are unavailable.");
            // Read the user's existing library only; do not extract into it or change its archives.
            realLibraryEntries = Library.Scan(new[] { Path.GetDirectoryName(numeric) }, sevenZip);
            LibraryEntry entry = LibraryByPath(realLibraryEntries, numeric);
            Require(entry.Character == "Chun-Li" && entry.Detection == "Native", "Numeric download must be classified by esf004, not left under an unknown filename.");
            Require(entry.CharacterCodes.SequenceEqual(new[] { "004" }), "Numeric download should expose its exact single native ID 004.");
            Require(string.IsNullOrEmpty(entry.FilenameCharacter), "Numeric regression filename should not supply a guessed character.");
            entry = LibraryByPath(realLibraryEntries, donor);
            Require(entry.FilenameCharacter == "Manon", "Donor-named RAR must retain its misleading filename guess for this regression.");
            Require(entry.Character == "C. Viper" && entry.Detection == "Native", "Viper assets must override the filename's donor Manon identity.");
            Require(entry.CharacterCodes.SequenceEqual(new[] { "030" }), "Donor hair archive should expose native ID 030 only.");
        }

        private static void TestRosterLibraryCharacters()
        {
            // Independent primary-source roster expectation. IDs 023/024 are intentionally absent.
            string[] expected = {
                "001|Ryu", "002|Luke", "003|Kimberly", "004|Chun-Li", "005|Manon", "006|Zangief",
                "007|JP", "008|Dhalsim", "009|Cammy", "010|Ken", "011|Dee Jay", "012|Lily",
                "013|A.K.I", "014|Rashid", "015|Blanka", "016|Juri", "017|Marisa", "018|Guile",
                "019|Ed", "020|E. Honda", "021|Jamie", "022|Akuma", "025|Sagat", "026|M. Bison",
                "027|Terry", "028|Mai", "029|Elena", "030|C. Viper", "031|Alex", "032|Ingrid", "033|Yasmine", "034|Arjun"
            };
            Require(Characters.ByCode.Count == expected.Length, "Roster map must contain exactly the 32 supported playable fighter IDs.");
            Require(!Characters.ByCode.ContainsKey("023") && !Characters.ByCode.ContainsKey("024"), "Sparse IDs 023/024 must not shift later fighters.");
            string fixtureLibrary = Path.Combine(root, "character-roster-library");
            Directory.CreateDirectory(fixtureLibrary);
            foreach (string row in expected)
            {
                string[] pair = row.Split('|');
                Require(Characters.ByCode.ContainsKey(pair[0]) && Characters.ByCode[pair[0]] == pair[1], "Canonical mapping differs for esf" + pair[0] + ": " + pair[1]);
                // Uppercase and wrapper directories must still identify the native engine route.
                CreateCharacterZip(Path.Combine(fixtureLibrary, "numeric-" + pair[0] + ".zip"), new[] { pair[0] }, true);
            }
            List<LibraryEntry> entries = Library.Scan(new[] { fixtureLibrary }, sevenZip);
            Require(entries.Count == expected.Length, "Every roster fixture should be present in the library.");
            foreach (string row in expected)
            {
                string[] pair = row.Split('|');
                LibraryEntry entry = LibraryByPath(entries, Path.Combine(fixtureLibrary, "numeric-" + pair[0] + ".zip"));
                Require(entry.Character == pair[1] && entry.Detection == "Native", "Native roster detection differs for esf" + pair[0] + ".");
                Require(entry.CharacterCodes.SequenceEqual(new[] { pair[0] }), "Roster fixture acquired an unrelated character ID.");
            }
        }

        private static void TestFolderCharacterRefresh()
        {
            string fixtureLibrary = Path.Combine(root, "folder-identity-library");
            string source = Path.Combine(fixtureLibrary, "Juri misleading folder name");
            string first = CharacterMeshRelative("001");
            string next = CharacterMeshRelative("004");
            WriteFixture(source, first, Encoding.UTF8.GetBytes("same bytes while source character route changes"));
            DateTime folderStamp = Directory.GetLastWriteTimeUtc(source);
            LibraryEntry before = LibraryByPath(Library.Scan(new[] { fixtureLibrary }, sevenZip), source);
            Require(before.Character == "Ryu" && before.Detection == "Native", "Folder scan must use payload over Juri filename.");
            string previousPath = Path.Combine(source, first.Replace('/', Path.DirectorySeparatorChar));
            string nextPath = Path.Combine(source, next.Replace('/', Path.DirectorySeparatorChar));
            Require(IsWithin(previousPath, root) && IsWithin(nextPath, root), "Folder refresh mutation escaped test fixtures.");
            Directory.CreateDirectory(Path.GetDirectoryName(nextPath));
            File.Move(previousPath, nextPath);
            // A nested payload edit need not change the source root directory's timestamp.
            Directory.SetLastWriteTimeUtc(source, folderStamp);
            LibraryEntry after = LibraryByPath(Library.Scan(new[] { fixtureLibrary }, sevenZip), source);
            Require(after.Character == "Chun-Li" && after.CharacterCodes.SequenceEqual(new[] { "004" }), "Rescanning a changed folder must discard the old Ryu identity.");
        }

        private static void TestZipCharacterRefresh()
        {
            string fixtureLibrary = Path.Combine(root, "zip-identity-library");
            Directory.CreateDirectory(fixtureLibrary);
            string source = Path.Combine(fixtureLibrary, "Cammy misleading numeric-identity.zip");
            CreateCharacterZip(source, new[] { "001" }, false);
            LibraryEntry first = LibraryByPath(Library.Scan(new[] { fixtureLibrary }, sevenZip), source);
            Require(first.Character == "Ryu" && first.Detection == "Native", "Initial ZIP scan should find Ryu.");
            LibraryEntry cached = LibraryByPath(Library.Scan(new[] { fixtureLibrary }, sevenZip), source);
            Require(cached.Character == "Ryu" && cached.CharacterCodes.SequenceEqual(new[] { "001" }), "Unchanged cached ZIP identity changed.");
            DateTime previousStamp = File.GetLastWriteTimeUtc(source);
            long previousSize = new FileInfo(source).Length;
            string replacement = Path.Combine(fixtureLibrary, "replacement.fixture");
            CreateCharacterZip(replacement, new[] { "016" }, false);
            Require(new FileInfo(replacement).Length == previousSize, "Cache regression fixture should change identity without changing archive length.");
            Require(IsWithin(source, root) && IsWithin(replacement, root), "ZIP refresh mutation escaped test fixtures.");
            File.Replace(replacement, source, null);
            File.SetLastWriteTimeUtc(source, previousStamp.AddSeconds(10));
            LibraryEntry updated = LibraryByPath(Library.Scan(new[] { fixtureLibrary }, sevenZip), source);
            Require(updated.Character == "Juri" && updated.CharacterCodes.SequenceEqual(new[] { "016" }), "Changed ZIP at the same source path reused stale Ryu cache identity.");
            Library.ClearIdentityCache();
            LibraryEntry refreshed = LibraryByPath(Library.Scan(new[] { fixtureLibrary }, sevenZip), source);
            Require(refreshed.Character == "Juri" && refreshed.Detection == "Native", "Explicit cache refresh should preserve the archive's current authoritative identity.");
        }

        private static void TestAmbiguousLibraryCharacters()
        {
            string fixtureLibrary = Path.Combine(root, "ambiguous-identity-library");
            Directory.CreateDirectory(fixtureLibrary);
            string ambiguous = Path.Combine(fixtureLibrary, "Juri and Ryu misleading bundle.zip");
            CreateCharacterZip(ambiguous, new[] { "001", "016" }, false);
            foreach (string code in new[] { "023", "024" }) CreateCharacterZip(Path.Combine(fixtureLibrary, "Ryu sparse-" + code + ".zip"), new[] { code }, false);
            List<LibraryEntry> entries = Library.Scan(new[] { fixtureLibrary }, sevenZip);
            LibraryEntry multi = LibraryByPath(entries, ambiguous);
            Require(string.IsNullOrEmpty(multi.Character) && multi.Detection == "Ambiguous", "A two-character native bundle must not be filed under one filename guess.");
            Require(new HashSet<string>(multi.CharacterCodes).SetEquals(new[] { "001", "016" }), "Ambiguous bundle must retain both actual native IDs.");
            foreach (string code in new[] { "023", "024" })
            {
                LibraryEntry unknown = LibraryByPath(entries, Path.Combine(fixtureLibrary, "Ryu sparse-" + code + ".zip"));
                Require(string.IsNullOrEmpty(unknown.Character) && unknown.Detection == "UnknownNative", "Unmapped native ID " + code + " must not fall back to Ryu filename.");
                Require(unknown.CharacterCodes.SequenceEqual(new[] { code }), "Unmapped ID should remain visible for review.");
            }
        }

        private static void TestLibraryFilenameFallback()
        {
            string fixtureLibrary = Path.Combine(root, "filename-fallback-library");
            Directory.CreateDirectory(fixtureLibrary);
            string source = Path.Combine(fixtureLibrary, "Cammy metadata-only.zip");
            using (ZipArchive archive = ZipFile.Open(source, ZipArchiveMode.Create))
            {
                AddEntry(archive, "modinfo.ini", Encoding.UTF8.GetBytes("name=Cammy metadata-only\nauthor=Fixture\n"));
                AddEntry(archive, "documentation/esf030/example.mesh.230110883", Encoding.UTF8.GetBytes("Non-native path mentioning Viper is documentation, not payload."));
            }
            LibraryEntry entry = LibraryByPath(Library.Scan(new[] { fixtureLibrary }, sevenZip), source);
            // 2.3.1: a readable mod without fighter files is Misc (here "Other") or Code tools and gets no fighter from
            // its name; the guess stays separate in FilenameCharacter and never becomes the row's fighter.
            Require(entry.FilenameCharacter == "Cammy" && entry.Character == "" && entry.Detection == "Unknown" && ModCategories.IsMisc(entry), "A Misc mod without native identity must not take the filename fighter (" + entry.Character + " / " + entry.Detection + ").");
            Require(entry.CharacterCodes.Count == 0, "Incidental esf text outside natives must not produce an authoritative native ID.");
        }

        private static LibraryEntry LibraryByPath(IEnumerable<LibraryEntry> entries, string path)
        {
            string full = Path.GetFullPath(path);
            LibraryEntry entry = entries.FirstOrDefault(e => string.Equals(Path.GetFullPath(e.SourcePath), full, StringComparison.OrdinalIgnoreCase));
            Require(entry != null, "Library entry missing: " + path);
            return entry;
        }

        private static string CharacterMeshRelative(string code)
        {
            return "natives/stm/product/model/esf/esf" + code + "/001/01/esf" + code + "_001_01.mesh.230110883";
        }

        private static void CreateCharacterZip(string path, IEnumerable<string> codes, bool uppercaseWrapped)
        {
            Require(IsWithin(path, root), "Character ZIP fixture escaped test root.");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (ZipArchive archive = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                foreach (string code in codes)
                {
                    string entry = CharacterMeshRelative(code);
                    if (uppercaseWrapped) entry = "Arbitrary wrapper/" + entry.ToUpperInvariant();
                    // No compression ensures same-width character IDs produce equal-size archives.
                    ZipArchiveEntry asset = archive.CreateEntry(entry, CompressionLevel.NoCompression);
                    byte[] payload = Encoding.UTF8.GetBytes("Character detection checks native paths without decoding binary assets.");
                    using (Stream stream = asset.Open()) stream.Write(payload, 0, payload.Length);
                }
                AddEntry(archive, "docs/donor_esf032.txt", Encoding.UTF8.GetBytes("Unrelated donor name must not affect classification."));
            }
        }

        private static ModComponent Named(ModPackage package, string name)
        {
            ModComponent component = package.Components.FirstOrDefault(c => string.Equals(c.Name.Trim(), name, StringComparison.OrdinalIgnoreCase));
            Require(component != null, "Component missing: " + name);
            return component;
        }

        private static InstalledOutfit InstalledByFolder(string path)
        {
            InstalledOutfit outfit = Installer.ListInstalled(costumeRoot).FirstOrDefault(o => string.Equals(Path.GetFullPath(o.Folder), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));
            Require(outfit != null, "Installed outfit enumeration missing " + path);
            return outfit;
        }

        private static string MakeSimpleDirectory(string path)
        {
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "modinfo.ini"), "NAME: Simple Ingrid\n AUTHOR = Fixture\n version=1.0\nnameasbundle=Simple bundle\n", Encoding.UTF8);
            WriteFixture(path, MeshRelative(), Encoding.UTF8.GetBytes("test mesh fixture version one"));
            WriteFixture(path, MaterialRelative(), Encoding.UTF8.GetBytes("test material fixture version one"));
            WriteFixture(path, "natives/stm/product/model/esf/esf032/FixtureTex/test_ALBD.tex.241101895", Encoding.UTF8.GetBytes("test texture fixture"));
            // An actual valid 1x1 PNG; image decoding and fallback can be tested safely.
            File.WriteAllBytes(Path.Combine(path, "screenshot.png"), Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="));
            File.WriteAllText(Path.Combine(path, "arbitrary-script.lua"), "error('this is not a native costume payload')");
            return path;
        }

        private static string MeshRelative() { return "natives/stm/product/model/esf/esf032/001/01/esf032_001_01.mesh.230110883".Replace('/', Path.DirectorySeparatorChar); }
        private static string MaterialRelative() { return "natives/stm/product/model/esf/esf032/001/01/esf032_001_01_v00.mdf2.31".Replace('/', Path.DirectorySeparatorChar); }

        private static void WriteFixture(string basePath, string relative, byte[] bytes)
        {
            string path = Path.Combine(basePath, relative.Replace('/', Path.DirectorySeparatorChar));
            Require(IsWithin(path, root), "Fixture write path escaped the generated test root.");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, bytes);
        }

        private static void AddEntry(ZipArchive archive, string name, byte[] bytes)
        {
            ZipArchiveEntry entry = archive.CreateEntry(name);
            using (Stream stream = entry.Open()) { stream.Write(bytes, 0, bytes.Length); }
        }

        private static int CountNatives(string outfit)
        {
            string native = Path.Combine(outfit, "natives");
            return Directory.Exists(native) ? Directory.GetFiles(native, "*", SearchOption.AllDirectories).Length : 0;
        }

        private static void AssertFileEqual(string source, string output)
        {
            Require(File.Exists(output), "Expected output file missing: " + output);
            using (Stream a = File.OpenRead(source))
            using (Stream b = File.OpenRead(output))
            using (SHA256 hash = SHA256.Create())
            {
                byte[] first = hash.ComputeHash(a);
                byte[] second = hash.ComputeHash(b);
                Require(first.SequenceEqual(second), "Output bytes differ from selected source asset.");
            }
        }

        private static void AssertArchiveFileEqual(string archive, string entryName, string output)
        {
            Require(File.Exists(output), "Expected output file missing: " + output);
            using (ZipArchive zip = ZipFile.OpenRead(archive))
            {
                ZipArchiveEntry entry = zip.GetEntry(entryName);
                Require(entry != null, "Expected source ZIP entry missing: " + entryName);
                using (Stream a = entry.Open())
                using (Stream b = File.OpenRead(output))
                using (SHA256 hash = SHA256.Create())
                {
                    byte[] first = hash.ComputeHash(a);
                    byte[] second = hash.ComputeHash(b);
                    Require(first.SequenceEqual(second), "Installed asset differs from selected ZIP component.");
                }
            }
        }

        private static void MustThrow(Action action, string failure)
        {
            bool threw = false;
            try { action(); }
            catch (Exception) { threw = true; }
            Require(threw, failure);
        }

        private static bool IsWithin(string path, string parent)
        {
            string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string basePath = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(full, basePath, StringComparison.OrdinalIgnoreCase) || full.StartsWith(basePath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        private static string Quote(string value) { return "\"" + value.Replace("\"", "\\\"") + "\""; }

        private static void RunTool(string executable, string arguments, string directory)
        {
            ProcessStartInfo info = new ProcessStartInfo(executable, arguments);
            info.WorkingDirectory = directory;
            info.CreateNoWindow = true;
            info.UseShellExecute = false;
            info.RedirectStandardOutput = true;
            info.RedirectStandardError = true;
            using (Process process = Process.Start(info))
            {
                string stdout = process.StandardOutput.ReadToEnd();
                string stderr = process.StandardError.ReadToEnd();
                process.WaitForExit();
                Require(process.ExitCode == 0, "7-Zip fixture creation failed: " + stdout + stderr);
            }
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
