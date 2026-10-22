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
            Search(Path.Combine(root, "search"));
            Ranking();
            Console.WriteLine("PASS install improvements: root choices, saved winners, PAK search/index and portrait ordering");
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
