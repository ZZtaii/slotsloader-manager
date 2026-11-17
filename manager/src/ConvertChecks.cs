using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace SlotsInstaller
{
    // 1.0.20 Convert RAR and 7Z to ZIP (ArchiveConvert, ConvertRecords, ConvertForm) on generated 7Z fixtures made with
    // the installed 7-Zip (7-Zip can't write RAR; RAR and 7Z go through the same 7-Zip path). --convert-real-check
    // converts a copy of a real RAR from tests.local.json. Nothing outside the run folder is written: the Recycle Bin is
    // a stub, and the records live in a fixture game folder and Data folder.
    internal static class ConvertChecks
    {
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        private const string Mesh = "natives/stm/product/model/esf/esf032/001/01/esf032_001_01.mesh.230110883";
        private const string Tex = "natives/stm/product/model/esf/esf032/001/01/esf032_001_01_alba.tex.241106027";

        private sealed class StubBin : IRecycleBin
        {
            internal string Folder; internal readonly List<string> Recycled = new List<string>();
            public string Unavailable(string path, bool folder, long bytes) { return null; }
            public void Recycle(string path, bool folder) { Recycled.Add(path); File.Move(path, Path.Combine(Folder, Recycled.Count + " " + Path.GetFileName(path))); }
            public void DeletePermanently(string path, bool folder) { throw new InvalidOperationException("The converter never deletes for good."); }
        }

        internal static int Run(string outputDirectory)
        {
            string root = Path.Combine(Path.GetFullPath(outputDirectory), "convert-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Safety.NoReparseChain(root); Directory.CreateDirectory(root);
            var report = new List<string>(); int failures = 0;
            Action<string, Action> check = delegate(string name, Action action)
            {
                try { action(); report.Add("PASS " + name); }
                catch (Exception ex) { failures++; report.Add("FAIL " + name + ": " + ex); }
            };
            string previousBundle = BundleStore.DataDirOverride, previousInstalls = ArchiveInstalls.DataDirOverride, previousIndex = Library.IdentityCacheDirectory;
            bool? previousLong = LongPaths.EnabledOverride; int? previousLimit = LongPaths.FileLimitOverride;
            try
            {
                string tool = SevenZipTool.Resolve("");
                Require(File.Exists(tool), "no 7-Zip to make the 7Z fixtures with");
                string data = Path.Combine(root, "Data"); Directory.CreateDirectory(data);
                BundleStore.DataDirOverride = data; ArchiveInstalls.DataDirOverride = data; Library.IdentityCacheDirectory = Path.Combine(root, "index");
                report.Add("Fixtures under " + root + "; 7-Zip " + tool);
                check("convert: a solid 7Z with several file types, an empty file, an empty folder, a non-ASCII name and a 3 MB file becomes a ZIP of the same name with the same entry paths, sizes and CRCs; dates of the archive kept; comment empty; no temporary file left; the original untouched", delegate { Basic(root, tool); });
                check("left out: a ZIP of the same name is never replaced (Survey and Convert); a path too long for Windows is left out", delegate { LeftOut(root, tool); });
                check("failures: a password-protected 7Z (names visible, and names encrypted), a truncated one and one with a damaged file fail with a short reason; no ZIP and no temporary file stay", delegate { Failures(root, tool); });
                check("disk space: too little free space fails before writing", delegate { Space(root, tool); });
                check("records: extra outfit (source, added part, part turned off), Install all variants, replacement read back from the archive (path, archive hash, size, stamp), recipe, preset (pointers, add-ons, replacement identity), typed name and history follow the ZIP", delegate { Records(root, tool, data); });
                check("records rollback: a record that can't be written puts every record back and removes the ZIP", delegate { Rollback(root, tool, data); });
                check("option layout: a 7Z's saved layout (Data\\bundle-layouts) moves into the ZIP's comment and the ZIP opens with it", delegate { Layout(root, tool); });
                check("window: the question counts the mods and the space and names what is left out (installed in the external mod manager); the window converts, Cancel stops after the current mod; the second question moves only the converted originals to the (stub) Recycle Bin", delegate { Window(root, tool, data); });
            }
            catch (Exception ex) { failures++; report.Add("FAIL setup: " + ex); }
            finally
            {
                BundleStore.DataDirOverride = previousBundle; ArchiveInstalls.DataDirOverride = previousInstalls; Library.IdentityCacheDirectory = previousIndex;
                LongPaths.EnabledOverride = previousLong; LongPaths.FileLimitOverride = previousLimit; ArchiveConvert.FreeSpaceOverride = null;
                ThemedMessageBox.TestResponder = null; ConvertForm.ShownForChecks = null; ConvertForm.BeforeItemForChecks = null;
            }
            report.Add(failures == 0 ? "ALL PASS" : failures + " FAILED");
            File.WriteAllLines(Path.Combine(root, "convert-checks.txt"), report);
            Console.WriteLine(String.Join(Environment.NewLine, report));
            return failures == 0 ? 0 : 1;
        }

        // Read-only for the real archive: a copy is converted inside the run folder.
        internal static int RunReal(string outputDirectory, string archive)
        {
            string root = Path.Combine(Path.GetFullPath(outputDirectory), "convert-real-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(root);
            var report = new List<string>(); int failures = 0;
            string previousBundle = BundleStore.DataDirOverride; BundleStore.DataDirOverride = Path.Combine(root, "Data");
            try
            {
                Require(File.Exists(archive), "set Archives.convertRar (a real RAR) in tests.local.json: " + archive);
                string tool = SevenZipTool.ResolveOrFetch("");
                string lib = Path.Combine(root, "library"); Directory.CreateDirectory(lib);
                string copy = Path.Combine(lib, Path.GetFileName(archive));
                File.Copy(archive, copy);
                File.SetCreationTimeUtc(copy, File.GetCreationTimeUtc(archive)); File.SetLastWriteTimeUtc(copy, File.GetLastWriteTimeUtc(archive));
                var clock = System.Diagnostics.Stopwatch.StartNew();
                ArchiveConvert.Result r = ArchiveConvert.Convert(copy, "", null, null);
                Require(r.Done, "not converted: " + r.Error);
                Same(tool, copy, r.Target);
                ModPackage before = PackageReader.Load(copy, ""), after = PackageReader.Load(r.Target, "");
                Require(before.Components.Select(c => c.Id).SequenceEqual(after.Components.Select(c => c.Id), StringComparer.Ordinal), "the ZIP has other options");
                Require(File.GetLastWriteTimeUtc(r.Target) == File.GetLastWriteTimeUtc(copy), "write time not kept");
                report.Add("PASS real RAR " + Path.GetFileName(archive) + " (" + r.SourceBytes + " bytes) -> ZIP " + r.ZipBytes + " bytes, " + r.Files + " files, " + before.Components.Count + " options, " + clock.ElapsedMilliseconds + " ms");
            }
            catch (Exception ex) { failures++; report.Add("FAIL real: " + ex); }
            finally { BundleStore.DataDirOverride = previousBundle; }
            report.Add(failures == 0 ? "ALL PASS" : failures + " FAILED");
            File.WriteAllLines(Path.Combine(root, "convert-real-checks.txt"), report);
            Console.WriteLine(String.Join(Environment.NewLine, report));
            return failures == 0 ? 0 : 1;
        }

        // ---- Fixtures ---------------------------------------------------------------------------------------------
        private static string Folder(string root, string name, bool big)
        {
            string src = Path.Combine(root, "sources", name + "-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Put(src, "Main/modinfo.ini", "name=" + name + " main\nscreenshot=screenshot.jpg\n");
            Put(src, "Main/" + Mesh, name + " mesh " + new string('m', 5000));
            Put(src, "Main/" + Tex, name + " texture " + new string('t', 20000));
            Put(src, "Main/screenshot.jpg", "not really a jpeg");
            Put(src, "Hair B/modinfo.ini", "name=Hair B\n");
            Put(src, "Hair B/" + Mesh, name + " hair");
            Put(src, "Extras/Café notes.txt", "naïve");
            Put(src, "Extras/empty.txt", "");
            Directory.CreateDirectory(Path.Combine(src, "Extras", "Empty folder"));
            if (big) { var bytes = new byte[3 * 1024 * 1024]; new Random(7).NextBytes(bytes); File.WriteAllBytes(Path.Combine(src, "Main", "big.bin"), bytes); }
            return src;
        }
        private static string SevenZ(string tool, string src, string archive, string extra = "")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(archive));
            ProcessUtil.RunText(tool, "a -t7z -mx=1 -bso0 -bsp0 -sccUTF-8 " + extra + " -- " + ProcessUtil.Quote(archive) + " " + ProcessUtil.Quote(Path.Combine(src, "*")));
            return archive;
        }
        private static string Make(string root, string tool, string lib, string name, bool big = false, string extra = "")
        {
            string archive = SevenZ(tool, Folder(root, name, big), Path.Combine(lib, name + ".7z"), extra);
            File.SetCreationTimeUtc(archive, new DateTime(2024, 3, 4, 5, 6, 7, DateTimeKind.Utc));
            File.SetLastWriteTimeUtc(archive, new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc));
            return archive;
        }

        // The ZIP holds exactly the archive's files (path, size, CRC from 7-Zip's listing; bytes read back).
        private static void Same(string tool, string source, string zip)
        {
            List<ArchiveConvert.Item> items = ArchiveConvert.List(tool, source).Where(i => !i.Folder).ToList();
            using (ZipArchive z = ZipFile.OpenRead(zip))
            {
                byte[] comment; uint[] crcs = ZipIntegrity.ReadChecksums(zip, z, out comment);
                var files = new Dictionary<string, KeyValuePair<long, uint>>(StringComparer.Ordinal);
                for (int i = 0; i < z.Entries.Count; i++) if (!z.Entries[i].FullName.EndsWith("/")) files[z.Entries[i].FullName] = new KeyValuePair<long, uint>(z.Entries[i].Length, crcs[i]);
                Require(files.Count == items.Count, "ZIP files " + files.Count + ", archive files " + items.Count);
                foreach (ArchiveConvert.Item i in items)
                {
                    KeyValuePair<long, uint> f;
                    Require(files.TryGetValue(i.Path, out f), "ZIP lacks " + i.Path + " (exact case)");
                    Require(f.Key == i.Size, i.Path + " size " + f.Key + " vs " + i.Size);
                    Require(!i.Crc.HasValue || f.Value == i.Crc.Value, i.Path + " CRC differs");
                }
            }
        }

        private static void Basic(string root, string tool)
        {
            string lib = Path.Combine(root, "basic");
            string archive = Make(root, tool, lib, "Solid Mod", true);
            byte[] original = File.ReadAllBytes(archive);
            long last = -1, total = -1;
            ArchiveConvert.Result r = ArchiveConvert.Convert(archive, "", null, delegate(long d, long t) { last = d; total = t; });
            Require(r.Done, "not converted: " + r.Error);
            Require(r.Target == Path.Combine(lib, "Solid Mod.zip") && File.Exists(r.Target), "ZIP not beside it: " + r.Target);
            Require(last == total && total > 3 * 1024 * 1024, "progress ended at " + last + " of " + total);
            Same(tool, archive, r.Target);
            using (ZipArchive z = ZipFile.OpenRead(r.Target))
            {
                Require(z.Entries.Any(e => e.FullName == "Extras/Empty folder/"), "the empty folder was dropped");
                Require(z.Entries.Any(e => e.FullName == "Extras/Café notes.txt"), "the non-ASCII name changed");
                Require(z.Entries.Single(e => e.FullName == "Extras/empty.txt").Length == 0, "the empty file");
            }
            using (var s = File.OpenRead(r.Target)) Require(ZipIntegrity.EndComment(s).Length == 0, "the ZIP has a comment");
            Require(File.GetCreationTimeUtc(r.Target) == new DateTime(2024, 3, 4, 5, 6, 7, DateTimeKind.Utc) && File.GetLastWriteTimeUtc(r.Target) == new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc), "dates not kept");
            Require(Library.AddedTime(File.GetCreationTimeUtc(r.Target), File.GetLastWriteTimeUtc(r.Target)) == Library.AddedTime(File.GetCreationTimeUtc(archive), File.GetLastWriteTimeUtc(archive)), "Added time differs");
            Require((File.GetAttributes(r.Target) & FileAttributes.Hidden) == 0, "the ZIP is hidden");
            Require(original.SequenceEqual(File.ReadAllBytes(archive)), "the original changed");
            Require(!Directory.GetFiles(lib, ArchiveConvert.TempPrefix + "*").Any(), "a temporary file stayed");
            Require(r.ZipBytes == new FileInfo(r.Target).Length && r.SourceBytes == original.Length && r.Files == 9, "result numbers: " + r.Files + " files");
            ModPackage p = PackageReader.Load(r.Target, "");
            Require(p.Components.Any(c => c.Name == "Solid Mod main") && p.Components.Any(c => c.Name == "Hair B"), "the ZIP's options");
        }

        private static void LeftOut(string root, string tool)
        {
            string lib = Path.Combine(root, "leftout");
            string archive = Make(root, tool, lib, "Taken");
            string zip = Path.Combine(lib, "Taken.zip"); File.WriteAllText(zip, "someone else's zip");
            List<ArchiveConvert.Candidate> survey = ArchiveConvert.Survey(new[] { archive, archive.ToUpperInvariant(), Path.Combine(lib, "other.zip") }, null);
            Require(survey.Count == 1 && survey[0].Skip == "a ZIP of the same name is already there", "survey: " + String.Join("; ", survey.Select(c => c.Name + "=" + c.Skip)));
            ArchiveConvert.Result r = ArchiveConvert.Convert(archive, "", null, null);
            Require(!r.Done && r.Error.Contains("already there") && File.ReadAllText(zip) == "someone else's zip", "the existing ZIP: " + r.Error);
            string deep = Make(root, tool, Path.Combine(lib, "deep"), "Long name mod");
            LongPaths.EnabledOverride = false; LongPaths.FileLimitOverride = deep.Length;   // the .7z fits, the .zip is one longer
            try
            {
                survey = ArchiveConvert.Survey(new[] { deep }, null);
                Require(survey[0].Skip != null && survey[0].Skip.Contains("too long"), "long path survey: " + survey[0].Skip);
                r = ArchiveConvert.Convert(deep, "", null, null);
                Require(!r.Done && r.Error.Contains("too long") && !File.Exists(Path.ChangeExtension(deep, ".zip")), "long path: " + r.Error);
            }
            finally { LongPaths.EnabledOverride = null; LongPaths.FileLimitOverride = null; }
            survey = ArchiveConvert.Survey(new[] { archive }, delegate(string s) { return "kept by the caller"; });
            Require(survey[0].Skip == "kept by the caller", "caller's reason");
        }

        private static void Failures(string root, string tool)
        {
            string lib = Path.Combine(root, "failures");
            Action<string, string, string> fails = delegate(string archive, string word, string what)
            {
                ArchiveConvert.Result r = ArchiveConvert.Convert(archive, "", null, null);
                Require(!r.Done && r.Error != null && r.Error.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0, what + ": " + r.Error);
                Require(r.Error.Length < 400, what + ": reason too long: " + r.Error);
                Require(!File.Exists(Path.ChangeExtension(archive, ".zip")), what + ": a ZIP stayed");
                Require(!Directory.GetFiles(Path.GetDirectoryName(archive), ArchiveConvert.TempPrefix + "*").Any(), what + ": a temporary file stayed");
            };
            fails(Make(root, tool, lib, "Locked", false, "-pSecret"), "password", "password, names visible");
            fails(Make(root, tool, lib, "Locked names", false, "-pSecret -mhe=on"), "password", "password, names encrypted");
            string cut = Make(root, tool, lib, "Cut", true);
            byte[] bytes = File.ReadAllBytes(cut); File.WriteAllBytes(cut, bytes.Take(bytes.Length / 2).ToArray());
            fails(cut, "damaged", "truncated");
            string flipped = SevenZ(tool, Folder(root, "Flipped", true), Path.Combine(lib, "Flipped.7z"), "-mx=0 -ms=off");
            bytes = File.ReadAllBytes(flipped); bytes[bytes.Length / 2] ^= 0x5A; File.WriteAllBytes(flipped, bytes);
            fails(flipped, "damaged", "damaged file data");
        }

        private static void Space(string root, string tool)
        {
            string archive = Make(root, tool, Path.Combine(root, "space"), "Roomy");
            ArchiveConvert.FreeSpaceOverride = delegate(string folder) { return 1024; };
            try
            {
                ArchiveConvert.Result r = ArchiveConvert.Convert(archive, "", null, null);
                Require(!r.Done && r.Error.Contains("free space") && !File.Exists(Path.ChangeExtension(archive, ".zip")), "space: " + r.Error);
            }
            finally { ArchiveConvert.FreeSpaceOverride = null; }
        }

        // A game folder with every kind of record that names the archive.
        private sealed class Fixture
        {
            internal string Lib, Game, Archive, Other, Manifest, Recipe;
            internal AppConfig Config;
            internal ReplaceEngine Engine;
            internal ConvertRecords.Context Context;
        }
        private static Fixture Build(string root, string tool, string data, string name)
        {
            var f = new Fixture { Lib = Path.Combine(root, name, "library"), Game = Path.Combine(root, name, "game") };
            f.Archive = Make(root, tool, f.Lib, name + " Mod");
            f.Other = Make(root, tool, f.Lib, name + " Other");
            f.Config = new AppConfig { GameRoot = f.Game, Libraries = new List<string> { f.Lib }, SevenZipPath = "" };
            Directory.CreateDirectory(f.Config.CostumeRoot);
            string outfit = Path.Combine(f.Config.CostumeRoot, "Ingrid", name + " outfit");
            Directory.CreateDirectory(Path.Combine(outfit, "natives"));
            File.WriteAllText(Path.Combine(outfit, "natives", "fixture.bin"), "managed bytes");
            f.Manifest = Path.Combine(outfit, ".slots-installer.json");
            Json.Write(f.Manifest, new InstallManifest { Id = Guid.NewGuid().ToString("N"), Character = "Ingrid", Name = name + " outfit", SourcePath = f.Archive, CreatedUtc = DateTime.UtcNow.ToString("o"), SelectedIds = new List<string> { "Main" }, Sha256 = new Dictionary<string, string> { { "natives/fixture.bin", new string('a', 64) } },
                Parts = new List<PartGraft> { new PartGraft { SourcePath = f.Other, Mod = "Other" }, new PartGraft { SourcePath = f.Archive, Mod = "Self hair", SelectedIds = new List<string> { "Hair B" } } },
                OffParts = new List<PartGraft> { new PartGraft { SourcePath = f.Archive, Mod = "Self off" } } });
            Json.Write(Path.Combine(data, ArchiveInstalls.FileName), new ArchiveInstallFile { Records = new List<ArchiveInstallRecord> {
                new ArchiveInstallRecord { Id = "a1", Path = Path.Combine(f.Config.CostumeRoot, "Ingrid", name + " Mod.7z"), SourcePath = f.Archive, Character = "Ingrid", Size = 1, WriteTicks = 1 },
                new ArchiveInstallRecord { Id = "a2", Path = Path.Combine(f.Config.CostumeRoot, "Ingrid", name + " Other.7z"), SourcePath = f.Other, Character = "Ingrid", Size = 1, WriteTicks = 1, AddonSources = new List<string> { f.Archive } } } });
            f.Engine = new ReplaceEngine(f.Game, data, new ReplaceSettings());
            long size; string sha = BundleLayout.ArchiveHash(f.Archive, out size);
            DurableFile.WriteJson(f.Engine.StatePath, new ReplaceState { Generation = 4, GameRoot = f.Engine.GameRoot, StoreRoot = Path.Combine(data, "replace-store"), Mods = new List<ReplaceMod> {
                new ReplaceMod { Id = "r1", Name = name + " Mod", SourcePath = f.Archive, Kind = "Outfit", Enabled = true, SelectedIds = new List<string> { "Main" }, ArchiveSha = sha, ArchiveSize = size, ArchiveStamp = SourceReader.Stamp(f.Archive),
                    Files = new List<ModFile> { new ModFile { Path = Mesh, Size = MeshSize(tool, f.Archive), ArchiveEntry = "Main/" + Mesh, Sha256 = new string('b', 64) } } },
                new ReplaceMod { Id = "r2", Name = "Unrelated", SourcePath = f.Other, Kind = "Outfit", Enabled = true } } });
            OutfitRecipe recipe = OutfitRecipes.Save(data, new OutfitRecipe { Name = name + " recipe", Character = "Ingrid", Base = new RecipeLayer { SourcePath = f.Archive, Mod = "Base", SelectedIds = new List<string> { "Main" } }, Parts = new List<RecipeLayer> { new RecipeLayer { SourcePath = f.Other, Mod = "Other" }, new RecipeLayer { SourcePath = f.Archive, Mod = "Self" } } });
            f.Recipe = Path.Combine(OutfitRecipes.Folder(data), recipe.Id + ".json");
            f.Context = new ConvertRecords.Context { CostumeRoot = f.Config.CostumeRoot, DataDir = data, Engine = f.Engine };
            return f;
        }
        private static long MeshSize(string tool, string archive) { return ArchiveConvert.List(tool, archive).Single(i => i.Path == "Main/" + Mesh).Size; }

        private static void Records(string root, string tool, string data)
        {
            Fixture f = Build(root, tool, data, "Rec");
            OutfitSetInfo preset = OutfitSetStore.CreatePreset(f.Config.CostumeRoot, "Rec preset", PresetScope.Both, f.Engine, null);
            string set = Directory.GetFiles(Path.Combine(f.Game, "reframework", ".slotsloader-installer-presets"), "set.json", SearchOption.AllDirectories).Single();
            Require(File.ReadAllText(set).Contains(Path.GetFileName(f.Archive)), "the fixture preset doesn't list the archive");
            LibraryNames.Set(new LibraryEntry { SourcePath = f.Archive, Name = "Rec Mod" }, "My typed name");
            string zip = Path.ChangeExtension(f.Archive, ".zip");
            ArchiveConvert.Result r = ArchiveConvert.Convert(f.Archive, "", f.Context, null);
            Require(r.Done, "not converted: " + r.Error);
            InstallManifest m = Json.Read<InstallManifest>(f.Manifest);
            Require(m.SourcePath == zip && m.Parts[0].SourcePath == f.Other && m.Parts[1].SourcePath == zip && m.OffParts[0].SourcePath == zip, "manifest: " + m.SourcePath + " / " + m.Parts[1].SourcePath + " / " + m.OffParts[0].SourcePath);
            ArchiveInstallFile installs = Json.Read<ArchiveInstallFile>(Path.Combine(data, ArchiveInstalls.FileName));
            Require(installs.Records[0].SourcePath == zip && installs.Records[1].SourcePath == f.Other && installs.Records[1].AddonSources[0] == zip, "archive-installs");
            ReplaceState state = f.Engine.LoadState();
            ReplaceMod rm = state.Mods.Single(x => x.Id == "r1"); long size; string sha = BundleLayout.ArchiveHash(zip, out size);
            Require(rm.SourcePath == zip && rm.ArchiveSha == sha && rm.ArchiveSize == size && rm.ArchiveStamp == SourceReader.Stamp(zip) && state.Generation == 5, "replacement identity: " + rm.SourcePath + " gen " + state.Generation);
            Require(state.Mods.Single(x => x.Id == "r2").SourcePath == f.Other, "an unrelated replacement moved");
            Require(ReplaceEngine.MovedArchive(rm, new[] { new LibraryEntry { SourcePath = zip, Size = new FileInfo(zip).Length } }) == zip, "the replacement doesn't recognise its ZIP");
            OutfitRecipe recipe = Json.Read<OutfitRecipe>(f.Recipe);
            Require(recipe.Base.SourcePath == zip && recipe.Parts[0].SourcePath == f.Other && recipe.Parts[1].SourcePath == zip, "recipe");
            string text = File.ReadAllText(set);
            Require(!text.Contains(Path.GetFileName(f.Archive)) && text.Contains(Path.GetFileName(zip)), "preset still names the archive");
            OutfitSetRecord record = Json.Read<OutfitSetRecord>(set);
            PresetReplacementPointer pointer = record.Pointers.Replacements.FirstOrDefault(p => p.SourcePath == zip);
            Require(pointer != null && pointer.ArchiveSha == sha && pointer.ArchiveSize == size && pointer.SourceStamp == MainForm.SourceStamp(zip), "preset replacement pointer identity");
            var entry = new LibraryEntry { SourcePath = zip, Name = "Rec Mod" }; LibraryNames.Apply(new[] { entry });
            Require(entry.CustomName == "My typed name", "the typed name stayed with the archive");
            Require(r.Records == 5, "records changed: " + r.Records);   // manifest, two archive-installs fields, replacement, recipe
            string history = HistoryStore.Default.FilePath;
            Require(File.Exists(history) && File.ReadAllText(history).Contains("converted from Rec Mod.7z"), "no Rename history event in " + history);
            Require(File.Exists(f.Archive), "the original was removed");
        }

        private static void Rollback(string root, string tool, string data)
        {
            Fixture f = Build(root, tool, data, "Back");
            byte[] manifest = File.ReadAllBytes(f.Manifest), state = File.ReadAllBytes(f.Engine.StatePath), installs = File.ReadAllBytes(Path.Combine(data, ArchiveInstalls.FileName));
            ArchiveConvert.Result r;
            using (new FileStream(f.Recipe, FileMode.Open, FileAccess.Read, FileShare.None)) r = ArchiveConvert.Convert(f.Archive, "", f.Context, null);   // recipes are written last
            Require(!r.Done && r.Error.Contains("records"), "converted anyway: " + r.Error);
            Require(!File.Exists(Path.ChangeExtension(f.Archive, ".zip")), "the ZIP stayed");
            Require(manifest.SequenceEqual(File.ReadAllBytes(f.Manifest)) && state.SequenceEqual(File.ReadAllBytes(f.Engine.StatePath)) && installs.SequenceEqual(File.ReadAllBytes(Path.Combine(data, ArchiveInstalls.FileName))), "a record was not put back");
            using (f.Engine.Lock()) { }   // the locks were released
            File.WriteAllText(f.Engine.JournalPath, "{}");
            try { Require(ConvertRecords.Blocker(f.Context) != null, "a pending game change doesn't block"); }
            finally { File.Delete(f.Engine.JournalPath); }
            Require(ConvertRecords.Blocker(f.Context) == null, "blocked without a reason");
        }

        private static void Layout(string root, string tool)
        {
            string archive = Make(root, tool, Path.Combine(root, "layout"), "Layout Mod");
            ModPackage author = PackageReader.Load(archive, "");
            List<BundleOption> options = BundleLayout.Options(author);
            BundleOption hair = options.Single(o => o.Name == "Hair B"); hair.Name = "Hair, renamed in the Manager";
            BundleLayout.Save(archive, SourceReader.Stamp(archive), options);
            Require(PackageReader.Load(archive, "").HasBundleLayout, "fixture: the 7Z has no stored layout");
            ArchiveConvert.Result r = ArchiveConvert.Convert(archive, "", null, null);
            Require(r.Done && r.LayoutMoved, "layout not moved: " + r.Error);
            ModPackage zip = PackageReader.Load(r.Target, "");
            Require(zip.HasBundleLayout && zip.Components.Any(c => c.Name == "Hair, renamed in the Manager") && zip.LayoutError == null, "the ZIP doesn't open with the layout");
            Same(tool, archive, r.Target);
            long layered; string a = BundleLayout.ArchiveHash(r.Target, out layered);
            Require(a != null && layered < new FileInfo(r.Target).Length, "ArchiveHash should leave the layout out");
        }

        private static void Window(string root, string tool, string data)
        {
            Fixture f = Build(root, tool, data, "Win");
            string third = Make(root, tool, f.Lib, "Win Third");
            string fluffyRoot = Path.Combine(root, "Win", "Fluffy", "Games", "SF6"), fluffyMods = Path.Combine(fluffyRoot, "Mods");
            string theirs = Make(root, tool, fluffyMods, "Fluffy Mod");
            File.WriteAllText(Path.Combine(fluffyRoot, "installed.ini"), "[Fluffy Mod]\r\nModName=Fluffy Mod\r\ninstalltype=normal\r\nfile=natives/stm/fluffy.tex\r\n");
            f.Config.Libraries.Add(fluffyMods);
            bool previousHeadless = Program.Headless; Program.Headless = true;
            SynchronizationContext previous = SynchronizationContext.Current;
            bool previousAutoInstall = WindowsFormsSynchronizationContext.AutoInstall; WindowsFormsSynchronizationContext.AutoInstall = false;
            bool previousAutoRefresh = MainForm.FluffyAutoRefresh; MainForm.FluffyAutoRefresh = false;
            IRecycleBin previousBin = RecycleBin.Current;
            var bin = new StubBin { Folder = Path.Combine(root, "Win", "stub-recycle-bin") }; Directory.CreateDirectory(bin.Folder);
            RecycleBin.Current = bin;
            var asked = new List<string>(); int recycleAnswer = 0;
            ThemedMessageBox.TestResponder = delegate(ThemedMessageForm box)
            {
                asked.Add(box.Caption + "|" + box.MessageText);
                if (box.Caption == "Convert RAR and 7Z to ZIP") return box.DialogButtons[0].DialogResult;
                if (box.Caption == "Move originals to Recycle Bin") { recycleAnswer++; return DialogResult.Yes; }
                return DialogResult.Cancel;
            };
            string state = null;
            try
            {
                using (var ui = new WindowsFormsSynchronizationContext())
                {
                    SynchronizationContext.SetSynchronizationContext(ui);
                    using (var form = new MainForm(f.Config))
                    {
                        Set(form, "firstLoad", false); form.ShowInTaskbar = false; form.Opacity = 0; form.MinimumSize = Size.Empty; form.Show(); Application.DoEvents();
                        form.ClientSize = new Size(DpiMetrics.Px(form, 1232), DpiMetrics.Px(form, 640)); Application.DoEvents();
                        form.GetType().GetMethod("ShowPage", PrivateInstance).Invoke(form, new object[] { form.GetType().GetField("settingsPage", PrivateInstance).GetValue(form) }); Application.DoEvents();
                        Render(form, Path.Combine(root, "Win", "settings-general.png"));
                        Set(form, "library", Library.Scan(f.Config.Libraries, ""));
                        Set(form, "libraryScanned", true);
                        FluffyInstalls fluffy = FluffyInstalls.Load(new[] { fluffyRoot });
                        Set(form, "gameRows", new List<GameModRow> { new GameModRow { Kind = "Fluffy", Fluffy = fluffy.Mods[0] } });
                        List<ArchiveConvert.Candidate> candidates = form.ConvertCandidates();
                        Require(candidates.Count == 4 && candidates.Single(c => c.Source == theirs).Skip == MainForm.ConvertFluffySkip && candidates.Count(c => c.Skip == null) == 3, "candidates: " + String.Join("; ", candidates.Select(c => c.Name + "=" + c.Skip)));
                        string none, question = MainForm.ConvertQuestion(candidates, delegate(string s) { return 1L << 40; }, out none);
                        Require(question.StartsWith("Convert 3 7Z mods to ZIP?") && question.Contains("1 left out. 1: installed in the external mod manager.") && question.Contains("The originals take"), "question: " + question);
                        Require(!question.Contains("—") && !question.Contains("Fluffy"), "question wording");
                        question = MainForm.ConvertQuestion(candidates, delegate(string s) { return 10; }, out none);
                        Require(question.Contains("may not be enough"), "low space warning");
                        Require(MainForm.ConvertQuestion(new List<ArchiveConvert.Candidate>(), null, out none) == null && none == "There are no RAR or 7Z mods in your libraries.", "nothing to convert");
                        // Cancel before the third mod: two convert, one is not started; recycle the two.
                        ConvertForm.BeforeItemForChecks = delegate(ConvertForm w, int i) { if (i == 2) w.RequestStop(); };
                        ConvertForm.ShownForChecks = delegate(ConvertForm w)
                        {
                            var timer = new System.Windows.Forms.Timer { Interval = 30 };
                            bool recycled = false;
                            timer.Tick += delegate
                            {
                                if (w.Busy) return;
                                if (!recycled && w.CanRecycle) { recycled = true; state = w.Progress; w.ClickRecycle(); return; }
                                if (w.Busy) return;
                                timer.Stop(); timer.Dispose(); state = state + " || " + w.Progress + " || " + w.LogText;
                                Render(w, Path.Combine(root, "Win", "convert-window.png"));
                                w.Close();
                            };
                            timer.Start();
                        };
                        form.ConvertArchives();
                        Require(form.ConvertRuns == 1 && form.LastConvert != null, "the window didn't run");
                        List<ArchiveConvert.Result> results = form.LastConvert.Results;
                        Require(results.Count == 2 && results.All(x => x.Done), "results: " + String.Join("; ", results.Select(x => Path.GetFileName(x.Source) + "=" + (x.Done ? "ok" : x.Error))));
                        Require(state != null && state.Contains("2 converted") && state.Contains("1 not started (cancelled)") && state.Contains("1 left out") && state.Contains("2 originals moved to the Recycle Bin"), "window text: " + state);
                        Require(recycleAnswer == 1 && bin.Recycled.Count == 2 && bin.Recycled.All(p => results.Any(x => x.Source == p)), "recycled: " + String.Join(", ", bin.Recycled));
                        Require(File.Exists(theirs) && !File.Exists(Path.ChangeExtension(theirs, ".zip")), "the external manager's installed mod was converted");
                        Require(results.All(x => File.Exists(x.Target) && !File.Exists(x.Source)), "ZIPs or originals");
                        Require(asked.Count(a => a.StartsWith("Convert RAR and 7Z to ZIP|")) == 1, "the first question: " + asked.Count);
                        form.Close();
                    }
                }
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous); WindowsFormsSynchronizationContext.AutoInstall = previousAutoInstall;
                Program.Headless = previousHeadless; RecycleBin.Current = previousBin; MainForm.FluffyAutoRefresh = previousAutoRefresh;
            }
        }

        private static void Render(Control c, string file)
        {
            using (var bitmap = new Bitmap(Math.Max(1, c.Width), Math.Max(1, c.Height))) { c.DrawToBitmap(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height)); bitmap.Save(file, System.Drawing.Imaging.ImageFormat.Png); }
        }
        private static void Put(string root, string path, string contents) { string file = Safety.Under(root, path); Directory.CreateDirectory(Path.GetDirectoryName(file)); File.WriteAllText(file, contents, new UTF8Encoding(false)); }
        private static void Set(object o, string name, object value) { FieldInfo field = o.GetType().GetField(name, PrivateInstance); if (field == null) throw new MissingFieldException(name); field.SetValue(o, value); }
        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    }
}
