using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace SlotsInstaller
{
    internal static class InstallImprovementChecks
    {
        private static void Require(bool ok, string why) { if (!ok) throw new InvalidOperationException(why); }
        internal static void Run(string root)
        {
            Directory.CreateDirectory(root);
            Options(Path.Combine(root, "options"));
            BundleEditing(root);
            StoredBundleEditing(root);
            StoredRealArchive(Path.Combine(root, "stored-real"));
            Search(Path.Combine(root, "search"));
            Ranking();
            Console.WriteLine("PASS install improvements: root choices, saved winners, ZIP and 7Z bundle edits, PAK search/index and portrait ordering");
        }

        // 1.0.16: a RAR/7Z layout lives in the Data folder (BundleStore), matched by content; the archive is never written.
        private static void StoredBundleEditing(string root)
        {
            string sevenZip = SevenZipTool.Resolve("");
            if (String.IsNullOrEmpty(sevenZip)) { Console.WriteLine("SKIP 7Z bundle edits: no 7-Zip on this machine"); return; }
            string dir = Path.Combine(root, "stored"), other = Path.Combine(root, "stored-copies"), path = Path.Combine(dir, "Stored edit.7z");
            Directory.CreateDirectory(dir); Directory.CreateDirectory(other);
            ProcessUtil.RunText(sevenZip, "a -t7z -y " + ProcessUtil.Quote(path) + " " + ProcessUtil.Quote(Path.Combine(root, "options", "*")));
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-1));
            string previous = BundleStore.DataDirOverride; BundleStore.DataDirOverride = Path.Combine(root, "stored-data");
            try
            {
                var original = PackageReader.Load(path, sevenZip);
                Require(!original.HasBundleLayout && original.LayoutError == null && !Directory.Exists(BundleStore.Folder), "a 7Z without a saved layout");
                var rows = BundleLayout.Options(original);
                var group = rows.Single(r => original.Find(r.Id).InferredChoice); group.Rule = "Choose up to one";
                var newGroup = new BundleOption { Id = BundleLayout.GroupPrefix + Guid.NewGuid().ToString("N"), Name = "Levels", Rule = "Choose any" };
                rows.Add(newGroup); group.Parent = newGroup.Id;
                var renamed = rows.First(r => !original.Find(r.Id).IsDummy); renamed.Name = "Brightest";
                string digest = ReplaceEngine.Sha(path), stamp = SourceReader.Stamp(path);
                DateTime written = File.GetLastWriteTimeUtc(path), installed = DateTime.UtcNow.AddMinutes(-30);
                BundleLayout.Save(path, stamp, rows);
                Require(ReplaceEngine.Sha(path) == digest && SourceReader.Stamp(path) == stamp && File.GetLastWriteTimeUtc(path) == written && Directory.GetFiles(dir).Length == 1, "a 7Z layout save wrote the archive or beside it");
                Require(!MainForm.SourceNewerThan(path, installed) && !BundleEdits.LayoutOnlySince(path, DateTime.UtcNow.AddMinutes(1)), "a 7Z layout save looks like a source change");
                string stored = Path.Combine(BundleStore.Folder, digest + ".json");
                Require(File.Exists(stored) && File.ReadAllText(stored).Contains("Stored edit.7z") && File.Exists(Path.Combine(BundleStore.Folder, "index.json")), "stored layout file or index missing");
                int hashes = BundleStore.Hashes;
                var edited = PackageReader.Load(path, sevenZip);
                Require(edited.HasBundleLayout && edited.LayoutError == null && edited.Find(group.Id).ParentId == newGroup.Id && edited.Find(newGroup.Id) != null && edited.Find(newGroup.Id).Name == "Levels"
                    && !edited.Find(group.Id).RequiresChoice && edited.Find(renamed.Id).Name == "Brightest", "reopened 7Z: groups, rules or names");
                Require(BundleStore.Hashes == hashes, "an unchanged 7Z was hashed again on opening");
                var members = edited.Components.Where(c => c.ParentId == group.Id).Select(c => c.Id).ToList();
                Require(Composer.Plan(edited, members.Take(1), true).CanInstall && !Composer.Plan(edited, members.Take(2), true).CanInstall, "stored optional choose-one group");
                // An install whose selection names the layout's group restores it; without the layout it needs a review.
                var saved = new List<string> { newGroup.Id, members[1] }; string note;
                Require(Composer.RestoreSelection(edited, saved, out note).SequenceEqual(saved) && Composer.Plan(edited, saved, true).CanInstall, "an install with a layout group did not restore");
                ReplaceChecks.Refused(() => Composer.RestoreSelection(original, saved, out note), "Review");
                // By content: a copy under another name in another folder, then the original moved.
                string copy = Path.Combine(other, "Renamed copy.7z"); File.Copy(path, copy);
                hashes = BundleStore.Hashes;
                Require(PackageReader.Load(copy, sevenZip).Find(renamed.Id).Name == "Brightest" && BundleStore.Hashes == hashes + 1, "a copied 7Z lost its layout");
                Require(PackageReader.Load(copy, sevenZip).HasBundleLayout && BundleStore.Hashes == hashes + 1, "a copied 7Z was hashed on every open");
                string moved = Path.Combine(other, "Moved.7z"); File.Move(path, moved);
                Require(PackageReader.Load(moved, sevenZip).HasBundleLayout, "a moved 7Z lost its layout");
                // Another archive of the same size is hashed and not matched; one of another size is not even hashed.
                byte[] bytes = File.ReadAllBytes(moved); bytes[bytes.Length / 2] ^= 0xFF;
                string lookalike = Path.Combine(other, "Same size.7z"); File.WriteAllBytes(lookalike, bytes);
                bool damaged; hashes = BundleStore.Hashes;
                Require(BundleStore.Find(lookalike, out damaged) == null && !damaged && BundleStore.Hashes == hashes + 1, "a different 7Z of the same size matched");
                string small = Path.Combine(other, "Other.7z");
                ProcessUtil.RunText(sevenZip, "a -t7z -y " + ProcessUtil.Quote(small) + " " + ProcessUtil.Quote(Path.Combine(root, "other-options", "*")));
                hashes = BundleStore.Hashes;
                var plain = PackageReader.Load(small, sevenZip);
                Require(!plain.HasBundleLayout && plain.LayoutError == null && BundleStore.Hashes == hashes, "an archive of another size was hashed or given a layout");
                // A stored layout that doesn't fit (another mod's) and a damaged one: the author's layout and the usual note.
                string smallSha = ReplaceEngine.Sha(small);
                DurableFile.WriteJson(Path.Combine(BundleStore.Folder, smallSha + ".json"), new BundleStore.Layout { Sha256 = smallSha, Size = new FileInfo(small).Length, File = "Other.7z", Options = rows });
                var stale = PackageReader.Load(small, sevenZip);
                Require(stale.LayoutError != null && stale.LayoutError.Contains("no longer matches") && !stale.HasBundleLayout && stale.Warnings[0] == stale.LayoutError && stale.Components.Any(c => c.Id == "A"), "a stored layout that doesn't fit: " + stale.LayoutError);
                BundleLayout.Save(small, SourceReader.Stamp(small), BundleLayout.Options(stale));
                var repaired = PackageReader.Load(small, sevenZip);
                Require(repaired.LayoutError == null && repaired.HasBundleLayout, "saving the author layout did not replace a stale stored one");
                File.WriteAllText(stored, "{ not json");
                var broken = PackageReader.Load(moved, sevenZip);
                Require(!broken.HasBundleLayout && broken.LayoutError != null && broken.LayoutError.Contains("damaged"), "a damaged stored layout: " + broken.LayoutError);
                BundleLayout.Save(moved, SourceReader.Stamp(moved), rows);
                Require(PackageReader.Load(moved, sevenZip).HasBundleLayout, "saving over a damaged stored layout");
                // Restore author layout: the stored layout goes, for every copy; the archive stays the same.
                BundleLayout.Save(moved, SourceReader.Stamp(moved), null);
                Require(!File.Exists(stored) && !PackageReader.Load(moved, sevenZip).HasBundleLayout && !PackageReader.Load(copy, sevenZip).HasBundleLayout && ReplaceEngine.Sha(moved) == digest, "reset left the stored layout or changed the 7Z");
            }
            finally { BundleStore.DataDirOverride = previous; }
        }

        // 1.0.16: a real RAR with several options (tests.local.json Archives.bundleRar) opens with a layout from a scratch Data folder. Read
        // only: skipped without the local config; the archive's bytes and write time are compared after.
        private static void StoredRealArchive(string root)
        {
            string rar = null;
            if (LocalTestConfig.Available) try { rar = LocalTestConfig.Current.Archive("bundleRar"); } catch (InvalidOperationException) { }
            if (rar == null || !File.Exists(rar)) { Console.WriteLine("SKIP real RAR bundle edit: no Archives.bundleRar in the local test config"); return; }
            string sevenZip = SevenZipTool.Resolve(LocalTestConfig.Current.SevenZip);
            string previous = BundleStore.DataDirOverride; BundleStore.DataDirOverride = Path.Combine(root, "data");
            try
            {
                string digest = ReplaceEngine.Sha(rar), stamp = SourceReader.Stamp(rar);
                var original = PackageReader.Load(rar, sevenZip);
                Require(!original.HasBundleLayout && original.Components.Count(c => !c.IsDummy) >= 2, "real RAR: needs two options and no saved layout");
                var rows = BundleLayout.Options(original);
                var option = rows.First(r => !original.Find(r.Id).IsDummy); option.Name = "Edited " + option.Name;
                var group = new BundleOption { Id = BundleLayout.GroupPrefix + Guid.NewGuid().ToString("N"), Name = "Real group", Rule = "Choose any" };
                rows.Add(group); option.Parent = group.Id;
                BundleLayout.Save(rar, stamp, rows);
                var edited = PackageReader.Load(rar, sevenZip);
                Require(edited.HasBundleLayout && edited.Find(option.Id).Name == option.Name && edited.Find(option.Id).ParentId == group.Id, "real RAR: stored layout not applied");
                Require(SourceReader.Stamp(rar) == stamp && ReplaceEngine.Sha(rar) == digest, "real RAR: the archive changed");
                Console.WriteLine("PASS real RAR bundle edit: " + Path.GetFileName(rar));
            }
            finally { BundleStore.DataDirOverride = previous; }
        }
        private static void BundleEditing(string root)
        {
            string path = Path.Combine(root,"bundle-edit.zip");
            ZipFile.CreateFromDirectory(Path.Combine(root,"options"), path);
            var original = PackageReader.Load(path,"");
            var rows = BundleLayout.Options(original);
            Require(!BundleSuggestions.Local(original,rows).Changed,"local suggestion subdivided an existing exclusive group");
            var neutral=new ModPackage();
            foreach(var label in new[]{"Red palette","Blue palette","Cape"})
                neutral.Components.Add(new ModComponent {Id=label,Name=label});
            var plain=BundleLayout.Options(neutral);
            var proposed=BundleSuggestions.Local(neutral,plain);
            Require(proposed.Changed && proposed.Items.Single(r=>r.Name=="Colors").Rule=="Choose any" &&
                proposed.Items.Single(r=>r.Name=="Cape").Parent==null && plain.All(r=>r.Parent==null) &&
                !BundleSuggestions.Local(neutral,proposed.Items).Changed,"uncertain options, draft isolation or repeated suggestion");
            var group = rows.Single(r => original.Find(r.Id).InferredChoice);
            group.Rule = "Choose up to one";
            var newGroup = new BundleOption { Id=BundleLayout.GroupPrefix+Guid.NewGuid().ToString("N"), Name="Options", Rule="Choose any" };
            rows.Add(newGroup); group.Parent=newGroup.Id;
            string digest = ReplaceEngine.Sha(path), stamp = SourceReader.Stamp(path);
            long length = new FileInfo(path).Length;
            BundleLayout.Save(path,stamp,rows);
            var edited = PackageReader.Load(path,"");
            Require(edited.Find(group.Id).ParentId==newGroup.Id && !edited.Find(group.Id).RequiresChoice,"edited parent and rule");
            long size;
            Require(BundleLayout.ArchiveHash(path,out size)==digest && size==length,"original payload identity after editing");
            var members = edited.Components.Where(c=>c.ParentId==group.Id).Select(c=>c.Id).ToList();
            Require(Composer.Plan(edited,members.Take(1),true).CanInstall && !Composer.Plan(edited,members.Take(2),true).CanInstall,"edited optional choose-one group");
            newGroup.Parent=group.Id; string now=SourceReader.Stamp(path);
            try { BundleLayout.Save(path,now,rows); throw new Exception("Circular bundle saved"); } catch(InvalidDataException) { }
            Require(now==SourceReader.Stamp(path),"rejected edit modified the ZIP");
            BundleLayout.Save(path,now,null);
            Require(ReplaceEngine.Sha(path)==digest,"author layout restoration changed the original archive");
            // 1.0.15: a readable trailer; a save is recorded as layout-only ("Source changed" ignores it).
            var fresh = BundleLayout.Options(PackageReader.Load(path,"")); fresh[0].Name += " edited";
            BundleLayout.Save(path,SourceReader.Stamp(path),fresh);
            byte[] layoutComment; using (var s = File.OpenRead(path)) layoutComment = ZipIntegrity.EndComment(s);
            Require(Encoding.UTF8.GetString(layoutComment).Contains("Slotsloader Manager option layout") && PackageReader.Load(path,"").HasBundleLayout,"readable layout trailer");
            Require(BundleEdits.LayoutOnlySince(path,DateTime.UtcNow.AddMinutes(1)) && !BundleEdits.LayoutOnlySince(path,new DateTime(2001,1,2,0,0,0,DateTimeKind.Utc)),"layout-only save not recorded");
            // A layout that no longer fits its ZIP (here another ZIP's, as when files are added in another program)
            // opens with the author layout and a note, and Edit bundle can save over it.
            foreach (string option in new[] { "A", "B" })
            {
                Directory.CreateDirectory(Path.Combine(root, "other-options", option, "natives", "stm"));
                File.WriteAllText(Path.Combine(root, "other-options", option, "modinfo.ini"), "name=" + option);
                File.WriteAllBytes(Path.Combine(root, "other-options", option, "natives", "stm", option + ".bin"), new byte[] { 1 });
            }
            string other = Path.Combine(root,"bundle-other.zip");
            ZipFile.CreateFromDirectory(Path.Combine(root,"other-options"), other);
            byte[] bytes = File.ReadAllBytes(other);   // no comment yet: its last two bytes are the comment length 0
            File.WriteAllBytes(other, bytes.Take(bytes.Length-2).Concat(new[] { (byte)layoutComment.Length, (byte)(layoutComment.Length>>8) }).Concat(layoutComment).ToArray());
            var stale = PackageReader.Load(other,"");
            Require(stale.LayoutError!=null && !stale.HasBundleLayout && stale.Warnings[0]==stale.LayoutError && stale.Components.Any(c=>c.Id=="A"),"a layout that no longer fits its ZIP: "+stale.LayoutError);
            BundleLayout.Save(other,SourceReader.Stamp(other),BundleLayout.Options(stale));
            var repaired = PackageReader.Load(other,"");
            Require(repaired.LayoutError==null && repaired.HasBundleLayout,"saving the author layout did not replace a stale one");
        }
        internal static string OptionsFixture(string root)
        {
            string[] labels = { "1. 125%", "2. 75%", "3. 50%", "4. 25%", "5. Off" };
            for (int i = 0; i < labels.Length; i++)
            {
                ReplaceChecks.Put(root, "level" + i + "/modinfo.ini", Encoding.UTF8.GetBytes("Name=" + labels[i] + "\nNameAsBundle=Lighting levels\n"));
                for (int f = 0; f < 29; f++) ReplaceChecks.Put(root, "level" + i + "/natives/stm/lighting/file" + f + ".bin", new[] { (byte)i, (byte)f });
            }
            return root;
        }
        private static ModComponent Peer(string id, string bundle, string path, bool rootFile)
        {
            var c = new ModComponent { Id = id, Name = id };
            c.Metadata["NameAsBundle"] = bundle;
            (rootFile ? c.RootAssets : c.Assets).Add(new NativeAsset { EntryPath = id + "/" + path, DestinationPath = path, Size = 1 });
            return c;
        }
        private static void Options(string root)
        {
            var package = PackageReader.Load(OptionsFixture(root), "");
            var ids = package.Components.Where(c => !c.IsDummy).Select(c => c.Id).ToList();
            var group = package.Components.Single(c => c.InferredChoice);
            Require(package.Name == "Lighting levels" && MainForm.ShortGroupName(group, package) == "Lighting levels", "synthetic group renamed the mod/footer");
            Require(!group.IsChoiceGroup && package.IsChoice(group), "inferred choice acquired required prose semantics");
            Require(Composer.DefaultSelection(package, true).Count == 0 && BatchDefaults.Selection(package, true).Count == 0, "GUI/queue guessed a level");
            Require(Composer.UnresolvedChoiceGroups(package, new string[0], true).Single() == group && Composer.Plan(package, new string[0], true).Errors.Any(e => e.Contains("Choose an option")), "empty package needs an actionable choice");
            Require(!Composer.Plan(package, ids, true).CanInstall, "fresh all-five selection accepted");
            string cliReport = Path.Combine(Path.GetDirectoryName(root), "root-options.json");
            Require(CommandLine.Run(new[] { "list-options", root, "--json", cliReport }) == 0, "CLI list-options failed");
            var listed = Json.Serializer().Deserialize<ModCheckReport>(File.ReadAllText(cliReport));
            Require(listed.Options.Count(o => o.Category) == 1 && listed.Options.Where(o => !o.Category).All(o => o.ParentId == group.Id && !o.DefaultSelected), "CLI group/parent/default output");
            var workspace = new AssistantWorkspace(new AppConfig { GameRoot = Path.Combine(Path.GetDirectoryName(root), "assistant-game"), Libraries = new List<string>(), SevenZipPath = "" }, new AssistantSettings());
            workspace.SetLibrary(new List<LibraryEntry> { new LibraryEntry { SourcePath = root, Name = package.Name } });
            var toolbox = new AssistantToolbox(workspace);
            string modId = AssistantWorkspace.ModId(root);
            var output = toolbox.Execute("get_mod_options", new Dictionary<string, object> { { "mod_id", modId } });
            string optionsJson = AssistantJson.Write(output.Content);
            Require(!output.IsError && optionsJson.Contains("choose_one_group") && optionsJson.Contains("\"required\":false") && optionsJson.Contains("at most one"), "Assistant optional-group contract");
            var rejected = toolbox.Execute("install_replacement", new Dictionary<string, object> { { "mod_id", modId }, { "option_ids", ids.Cast<object>().ToList() }, { "update_replacement_id", "" } });
            Require(rejected.IsError && toolbox.Actions.Count == 0, "Assistant queued a fresh multi-pick");
            foreach (string id in ids)
            {
                var plan = Composer.Plan(package, new[] { id }, true);
                Require(plan.CanInstall && plan.Files.Count == 29 && plan.Files.Values.All(f => f.Component.Id == id), "single level's files changed");
            }
            string note;
            var restored = Composer.RestoreSelection(package, ids, out note);
            Require(restored.SequenceEqual(new[] { ids.Last() }) && note.Contains("5. Off"), "legacy all-five winner changed");
            Require(Composer.RestoreSelection(package, ids.AsEnumerable().Reverse(), out note).Single() == ids[0], "stored order was replaced by menu order");
            ReplaceChecks.Refused(() => Composer.RestoreSelection(package, ids.Concat(new[] { "missing" }), out note), "Review");
            package.Find(ids[0]).Assets.Add(new NativeAsset { EntryPath = "changed", DestinationPath = "stm/extra.bin" });
            ReplaceChecks.Refused(() => Composer.RestoreSelection(package, ids, out note), "Review");
            package.Find(ids[0]).Assets.RemoveAt(package.Find(ids[0]).Assets.Count - 1);
            var extra = Peer("unrelated", "", "natives/stm/unrelated.bin", false); package.Components.Add(extra); package.RebuildIndexes();
            var interleaved = new[] { ids[0], extra.Id, ids[4] };
            Require(Composer.RestoreSelection(package, interleaved, out note).SequenceEqual(new[] { extra.Id, ids[4] }), "unrelated order changed");
            Require(Composer.Plan(package, new[] { extra.Id }, true).CanInstall && Composer.UnresolvedChoiceGroups(package, new[] { extra.Id }, true).Count == 0, "optional cohort forced a pick");
            var oldGroupId = group.Id; RootOptionChoices.Add(package); package.PrepareChoices();
            Require(package.Components.Count(c => c.InferredChoice) == 1 && group.Id == oldGroupId, "inference is not idempotent");
            var roots = new ModPackage { Components = new List<ModComponent> { Peer("a", "Pack", "addon.pak", true), Peer("b", "pack", "ADDON.PAK", true) } };
            RootOptionChoices.Add(roots); roots.PrepareChoices();
            Require(!Composer.Plan(roots, new[] { "a", "b" }, true).CanInstall && Composer.Plan(roots, new[] { "a" }, true).CanInstall, "root-only/case-insensitive alternatives");
            roots.Components.Add(Peer("x", "Second", "second.pak", true)); roots.Components.Add(Peer("y", "Second", "second.pak", true));
            RootOptionChoices.Add(roots); roots.PrepareChoices();
            Require(roots.Components.Count(c => c.InferredChoice) == 2 && Composer.Plan(roots, new[] { "a" }, true).CanInstall && Composer.UnresolvedChoiceGroups(roots, new[] { "a" }, true).Count == 0, "one optional cohort forced the other");
            foreach (int negative in Enumerable.Range(0, 5))
            {
                var a = Peer("a", "Pack", "natives/stm/a.bin", false); var b = Peer("b", "Pack", "natives/stm/a.bin", false);
                var p = new ModPackage { Components = new List<ModComponent> { a, b } };
                if (negative == 0) b.Assets.Add(new NativeAsset { DestinationPath = "natives/stm/b.bin" });
                if (negative == 1) b.Name = "Main files";
                if (negative == 2) b.AddonFor = "unresolved dependency";
                if (negative == 3) p.Components.Add(new ModComponent { Id = "child", ParentId = "b" });
                if (negative == 4) b.Metadata["NameAsBundle"] = "Another pack";
                Require(RootOptionChoices.Find(p).Count == 0, "overbroad root inference " + negative);
            }
        }
        private static bool Matches(LibraryEntry e, string text)
        {
            var q = LibrarySearch.Parse(text, MainForm.SearchCharacterTerm);
            return LibrarySearch.MatchesText(e, q, MainForm.EntryMatchesCharacter, MainForm.EntryMatchesName)
                && LibrarySearch.CostumeRank(e, q, LibrarySearch.CodesFor(q.Characters), MainForm.EntryMatchesCharacter) >= 0;
        }
        private static void Search(string root)
        {
            var record = MainForm.InstalledRowEntry("NULSPACE", "", "gone.zip", new[] { "RE_CHUNK_000.PAK.PATCH_002.PAK" }, false);
            foreach (string query in new[] { "pak", "PAK", "pak nulspace", "pak, nulspace", "pak mods nulspace", "pak mod" }) Require(Matches(record, query), "PAK query failed: " + query);
            Require(!Matches(record, "pak, unrelated"), "PAK bypassed remaining AND terms");
            var source = new LibraryEntry { Name = "Library title", HasPakPayload = false, Authors = new List<string> { "SKX" } };
            var merged = MainForm.InstalledSearchEntry(record, source);
            Require(Matches(merged, "pak by:SKX") && Matches(merged, "pak skx"), "record payload and library author failed to compose");
            Require(source.HasPakPayload == false && source.Name == "Library title", "installed matching mutated the library");
            source.HasPakPayload = true; source.MiscAreas.Add(ModCategories.LabelPak);
            var loose = MainForm.InstalledSearchEntry(MainForm.InstalledRowEntry("Loose", "", "gone.zip", new[] { "natives/stm/a.bin" }, false), source);
            Require(!Matches(loose, "pak"), "changed source asserted an uninstalled PAK payload");
            Require(Matches(MainForm.InstalledRowEntry("Tool", "", "", new[] { "addon.pak", "reframework/autorun/helper.lua" }, true), "pak"), "recorded tool PAK missing");
            Require(!Matches(MainForm.InstalledRowEntry("Tool", "", "", new[] { "reframework/plugins/loader.dll" }, true), "pak"), "unrecorded generated PAK was inferred");
            Require(Matches(new LibraryEntry { Name = "Pak example", HasPakPayload = false }, "pak"), "ordinary name match removed");
            Directory.CreateDirectory(root);
            string zip = Path.Combine(root, "Mixed.zip");
            using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
                foreach (string entry in new[] { "Native/modinfo.ini", "Native/natives/stm/product/model/esf/esf009/002/a.bin", "Packed/modinfo.ini", "Packed/addon.pak" })
                    using (var writer = new StreamWriter(z.CreateEntry(entry).Open())) writer.Write(entry.EndsWith("modinfo.ini") ? "name=Fixture" : "fixture");
            string oldCache = Library.IdentityCacheDirectory;
            try
            {
                Library.IdentityCacheDirectory = Path.Combine(root, "cache");
                var entries = Library.Scan(new[] { root }, "");
                var mixed = entries.Single(e => e.SourcePath == zip);
                Require(mixed.HasPakPayload == true && mixed.CharacterCodes.Contains("009") && Matches(mixed, "pak, cammy"), "mixed package payload fact missing");
                var captured = Library.CaptureRenameIdentity(zip, ""); Require(captured != null && captured.HasPakPayload == true, "PAK fact lost by index clone");
                var indexFile = Directory.GetFiles(Library.IdentityCacheDirectory, "*.json").Single();
                string json = File.ReadAllText(indexFile);
                File.WriteAllText(indexFile, json.Replace("\"HasPakPayload\":true", "\"HasPakPayload\":null"));
                // Changing cache roots invalidates the in-process index as on a restart.
                Library.IdentityCacheDirectory = Path.Combine(root, "other-cache");
                Library.IdentityCacheDirectory = Path.Combine(root, "cache");
                List<LibraryEntry> pending, names;
                var quick = Library.ScanQuick(new[] { root }, "", out pending, out names).Single(e => e.SourcePath == zip);
                Require(!quick.Pending && quick.CharacterCodes.Contains("009") && !quick.HasPakPayload.HasValue && pending.Contains(quick), "old cache discarded identity or failed to backfill");
                var copy = Library.IndexCopy(quick);
                using (var locked = new FileStream(quick.SourcePath, FileMode.Open, FileAccess.Read, FileShare.None)) Library.IndexEntry(copy, "");
                Require(!copy.HasPakPayload.HasValue && copy.CharacterCodes.Contains("009") && copy.Detection != "Error", "failed optional backfill erased known identity");
                Library.IndexEntry(copy, ""); Library.TakeIdentity(quick, copy); Library.SaveIndex();
                Require(quick.HasPakPayload == true && Matches(quick, "pak"), "background payload backfill failed");
            }
            finally { Library.IdentityCacheDirectory = oldCache; }
        }
        private static void Ranking()
        {
            var single = new LibraryEntry { Name = "Z Single", SourcePath = "s", Detection = "Native", CharacterCodes = new List<string> { "008" }, NamesPending = true };
            var multi = new LibraryEntry { Name = "A Multi", SourcePath = "m", Detection = "Ambiguous", CharacterCodes = new List<string> { "008", "001" }, AddedUtc = DateTime.UtcNow };
            var guessed = new LibraryEntry { Name = "B Guess", SourcePath = "g", Detection = "Filename", CharacterCodes = new List<string> { "008" } };
            var pending = new LibraryEntry { Name = "C Pending", SourcePath = "p", Detection = "Native", CharacterCodes = new List<string> { "008" }, Pending = true };
            var ranked = new[] { multi, guessed, pending, single }.Select((e, i) => new KeyValuePair<LibraryEntry, int>(e, i)).ToList();
            foreach (LibrarySortKey key in Enum.GetValues(typeof(LibrarySortKey))) foreach (bool asc in new[] { false, true })
            {
                var ordered = LibrarySorting.Order(ranked, key, asc, e => e == multi, e => e == multi ? 100 : 0);
                Require(Object.ReferenceEquals(ordered, LibrarySorting.SpecificFirst(ordered, new string[0])), "no-portrait ordering changed");
                var specific = LibrarySorting.SpecificFirst(ordered, new[] { "008", "001" });
                Require(specific.First() == single && specific.Skip(1).SequenceEqual(ordered.Where(e => e != single)), "specificity or existing within-group order changed");
            }
        }
    }
}
