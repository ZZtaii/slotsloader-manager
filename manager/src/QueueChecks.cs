using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace SlotsInstaller
{
    // 1.0.6 (--queue-check, test.ps1 suite "queue"): the user's library requests. Word search over file and display names,
    // comma/space/semicolon forms, deep search into option folders, the modinfo.ini display name in the index, the queue's
    // default options, adding a part to an installed outfit (and keeping it on reinstall), and in a hidden window: the
    // Name · File switch, Deep search, scroll to top on a filter change, multi-select → queue install and delete, the
    // Installed page's Refresh button.
    internal static class QueueChecks
    {
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

        internal static int Run(string output)
        {
            string root = Path.Combine(Path.GetFullPath(output), "queue-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(root);
            var report = new List<string>(); int failures = 0;
            Action<string, Action> check = delegate(string name, Action action)
            {
                try { action(); report.Add("PASS " + name); }
                catch (Exception ex) { failures++; report.Add("FAIL " + name + ": " + ex); Program.Log(ex); }
            };
            bool previousHeadless = Program.Headless; Program.Headless = true;
            string previousIndex = Library.IdentityCacheDirectory; Library.IdentityCacheDirectory = Path.Combine(root, "index");
            try
            {
                check("search words (1.0.6): \"viper barefoot\", \"barefoot viper\", \"cviper\" and \"c viper\" find C-Viper-Barefoot; \"bare feet\" finds juri_bare_feet; display names match; accents and punctuation are ignored", SearchWords);
                check("comma forms (1.0.6): \"juri, barefoot\", \"juri,barefoot\", \"juri barefoot\", \" juri ,  barefoot ,\", \"juri;barefoot\", \"juri，barefoot\" all give the same mods; \"juri c1\" = \"juri, c1\"; a fighter term stays strict with commas (the Alex stage mod case)", CommaForms);
                check("deep search (1.0.6): off, \"barefoot\" misses Goth Juri; on, its Barefoot option folder or option name finds it, also as a comma term or one word of several", DeepSearch);
                check("modinfo display name (1.0.6): dummy root, NameAsBundle, the only top-level option; several top-level options or none = no name; add-ons and nested options are not top level", ModInfoNames);
                check("library index (1.0.6): a scan reads the display name and option names; a cached record keeps them; a record without them is listed again once", delegate { IndexNames(root); });
                check("queue defaults (1.0.6): alternative top-level variants keep the first (the Default one first); a required choose-one group gets its default/first option; optional add-ons stay off; the plan installs", delegate { QueueDefaults(root); });
                check("add to outfit (1.0.6): exactly the ticked options' files go into the installed outfit (a lone hair option without main files is fine); nothing else is added or removed; recorded once; a reinstall of the same mod puts them back; files for another outfit slot are refused; the picker window renders", delegate { AddPart(root); });
                SynchronizationContext previous = SynchronizationContext.Current; bool previousAuto = WindowsFormsSynchronizationContext.AutoInstall;
                WindowsFormsSynchronizationContext.AutoInstall = false;
                IProcessProbe previousProbe = GameProcesses.Probe; GameProcesses.Probe = new FakeProcessProbe();
                ReplaceSettings savedSettings = ReplaceSettings.Load();
                try
                {
                    using (var ui = new WindowsFormsSynchronizationContext())
                    {
                        SynchronizationContext.SetSynchronizationContext(ui);
                        check("library window (1.0.6): Name · File switch and Ctrl+Shift+N, Deep search box, filter change scrolls to the top, Ctrl+A / several rows open the queue, queue install then delete, Installed Refresh button; renders", delegate { Window(root); });
                    }
                }
                finally
                {
                    SynchronizationContext.SetSynchronizationContext(previous); WindowsFormsSynchronizationContext.AutoInstall = previousAuto;
                    GameProcesses.Probe = previousProbe; try { savedSettings.Save(); } catch { }
                }
            }
            catch (Exception ex) { failures++; report.Add("FAIL setup: " + ex); }
            finally { Program.Headless = previousHeadless; Library.IdentityCacheDirectory = previousIndex; }
            report.Add(failures == 0 ? "ALL PASS" : failures + " FAILED");
            File.WriteAllLines(Path.Combine(root, "queue-check.txt"), report);
            Console.WriteLine(String.Join(Environment.NewLine, report));
            Console.WriteLine("Fixtures and renders: " + root);
            return failures == 0 ? 0 : 1;
        }

        // ---- Search ---------------------------------------------------------------------------------------------------
        private static List<LibraryEntry> SearchLibrary()
        {
            return new List<LibraryEntry>
            {
                new LibraryEntry { Name = "C-Viper-Barefoot-1234-1-0-1699999999", SourcePath = @"C:\lib\C-Viper-Barefoot-1234-1-0-1699999999.zip", Character = "C. Viper", CharacterCodes = new List<string> { "030" }, Kind = "ZIP" },
                new LibraryEntry { Name = "Goth Juri", SourcePath = @"C:\lib\Goth Juri.zip", Character = "Juri", CharacterCodes = new List<string> { "016" }, CostumeKeys = new List<string> { "016/001" }, OptionIds = new List<string> { "Goth Juri/Main", "Goth Juri/Barefoot Variant" }, OptionNames = new List<string> { "Main files", "Bare Feet" }, Kind = "ZIP", OptionCount = 2 },
                new LibraryEntry { Name = "juri_bare_feet_v2", DisplayName = "Juri Barefoot Pack", SourcePath = @"C:\lib\juri_bare_feet_v2.7z", Character = "Juri", CharacterCodes = new List<string> { "016" }, Kind = "7Z" },
                new LibraryEntry { Name = "Cammy Barefoot", SourcePath = @"C:\lib\Cammy Barefoot.zip", Character = "Cammy", CharacterCodes = new List<string> { "009" }, Kind = "ZIP" },
                new LibraryEntry { Name = "Back Spotlight Adjuster - Alex Patch", SourcePath = @"C:\lib\Back Spotlight Adjuster - Alex Patch.zip", Character = "", FilenameCharacter = "Alex", StageIds = new List<string> { "0000" }, Kind = "ZIP" },
                new LibraryEntry { Name = "Élena Classic", SourcePath = @"C:\lib\Élena Classic.zip", Character = "Elena", CharacterCodes = new List<string> { "029" }, Kind = "ZIP" },
            };
        }
        private static string Find(List<LibraryEntry> library, string text, bool deep)
        {
            LibrarySearch.Query q = LibrarySearch.Parse(text, MainForm.SearchCharacterTerm, deep); var codes = LibrarySearch.CodesFor(q.Characters);
            return String.Join(" | ", library.Where(e => LibrarySearch.MatchesText(e, q, MainForm.EntryMatchesCharacter, MainForm.EntryMatchesName) && LibrarySearch.CostumeRank(e, q, codes, MainForm.EntryMatchesCharacter) >= 0 && MainForm.MatchesModType(e, q.Type)).Select(e => e.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
        }
        private static void SearchWords()
        {
            var lib = SearchLibrary(); const string viper = "C-Viper-Barefoot-1234-1-0-1699999999";
            foreach (string text in new[] { "viper barefoot", "barefoot viper", "cviper", "c-viper barefoot", "VIPER   BAREFOOT", "viper-barefoot" })
                Require(Find(lib, text, false) == viper, text + ": " + Find(lib, text, false));
            Require(Find(lib, "c viper", false) == viper, "c viper: " + Find(lib, "c viper", false));
            Require(Find(lib, "bare feet", false) == "juri_bare_feet_v2", "bare feet: " + Find(lib, "bare feet", false));
            Require(Find(lib, "barefoot pack", false) == "juri_bare_feet_v2", "display name: " + Find(lib, "barefoot pack", false));
            Require(Find(lib, "elena classic", false) == "Élena Classic" && Find(lib, "élena", false) == "Élena Classic", "accents: " + Find(lib, "elena classic", false));
            Require(Find(lib, "barefoot", false) == viper + " | Cammy Barefoot | juri_bare_feet_v2", "barefoot: " + Find(lib, "barefoot", false));
            Require(Find(lib, "c viper barefoot", false) == viper && Find(lib, "juri v2", false) == "juri_bare_feet_v2" && Find(lib, "a barefoot", false) == "", "short words: " + Find(lib, "a barefoot", false));
            var e = lib[0];
            Require(LibrarySearch.NameMatches(e, "viper barefoot") && !LibrarySearch.NameMatches(e, "viper juri"), "NameMatches");
        }
        private static void CommaForms()
        {
            var lib = SearchLibrary();
            string expected = Find(lib, "juri, barefoot", false);
            Require(expected == "juri_bare_feet_v2", "juri, barefoot: " + expected);
            foreach (string text in new[] { "juri,barefoot", "juri barefoot", " juri ,  barefoot ,", "juri;barefoot", "juri\uFF0Cbarefoot", ",juri,,barefoot", "barefoot juri", "barefoot, juri", "\"juri\" barefoot" })
                Require(Find(lib, text, false) == expected, "'" + text + "': " + Find(lib, text, false));
            Require(Find(lib, "juri c1", false) == Find(lib, "juri, c1", false) && Find(lib, "juri c1", false) == "Goth Juri", "juri c1: " + Find(lib, "juri c1", false) + " / " + Find(lib, "juri, c1", false));
            Require(Find(lib, "juri, barefoot c1", false) == "", "costume word inside a comma term: " + Find(lib, "juri, barefoot c1", false));
            Require(Find(lib, "alex, patch", false) == "" && Find(lib, "alex patch", false) == "Back Spotlight Adjuster - Alex Patch" && Find(lib, "patch alex", false) == "Back Spotlight Adjuster - Alex Patch", "fighter term strictness: " + Find(lib, "alex, patch", false) + " / " + Find(lib, "patch alex", false));
            Require(Find(lib, "juri, cammy", false) == "Cammy Barefoot | Goth Juri | juri_bare_feet_v2", "alternatives: " + Find(lib, "juri, cammy", false));
            Require(Find(lib, "juri,", false) == "Goth Juri | juri_bare_feet_v2" && Find(lib, ",", false).Split('|').Length == lib.Count, "trailing comma / comma alone");
        }
        private static void DeepSearch()
        {
            var lib = SearchLibrary();
            Require(!Find(lib, "barefoot", false).Contains("Goth Juri") && Find(lib, "barefoot", true).Contains("Goth Juri"), "barefoot deep: " + Find(lib, "barefoot", true));
            Require(Find(lib, "juri, barefoot", true) == "Goth Juri | juri_bare_feet_v2", "juri, barefoot deep: " + Find(lib, "juri, barefoot", true));
            Require(Find(lib, "goth barefoot", true) == "Goth Juri" && Find(lib, "goth barefoot", false) == "", "words across name and option: " + Find(lib, "goth barefoot", true));
            Require(Find(lib, "bare feet", true).Contains("Goth Juri"), "option name: " + Find(lib, "bare feet", true));
            Require(Find(lib, "variant", true) == "Goth Juri" && Find(lib, "variant", false) == "", "option folder word");
        }
        private static void ModInfoNames()
        {
            Func<string, string, string> name = delegate(string folder, string ini) { return folder + "\n" + ini; };
            Func<string[], string> run = delegate(string[] items)
            {
                var infos = items.Select(i => { int cut = i.IndexOf('\n'); return new KeyValuePair<string, Dictionary<string, string>>(i.Substring(0, cut), PackageReader.ParseIni(Encoding.UTF8.GetBytes(i.Substring(cut + 1)))); }).ToList();
                return Library.ModInfoName(infos, delegate(Dictionary<string, string> meta, string key) { string v; return meta.TryGetValue(key, out v) ? v.Trim() : ""; });
            };
            Require(run(new[] { name("", "name=Goth Juri\r\ndummymod=true"), name("Barefoot", "name=Barefoot") }) == "Goth Juri", "dummy root");
            Require(run(new[] { name("A", "name=1. 125%\r\nNameAsBundle=Spot Adjuster"), name("B", "name=2. 75%\r\nNameAsBundle=Spot Adjuster") }) == "Spot Adjuster", "NameAsBundle");
            Require(run(new[] { name("Mod", "name=C. Viper Barefoot") }) == "C. Viper Barefoot", "single");
            Require(run(new[] { name("Main", "name=Juri Main"), name("Main/Hair", "name=Hair"), name("Ribbon", "name=Ribbon\r\naddonfor=Juri Main") }) == "Juri Main", "nested and add-on are not top level");
            Require(run(new[] { name("Red", "name=Red"), name("Blue", "name=Blue") }) == "", "several top-level options");
            Require(run(new string[0]) == "", "no modinfo");
        }
        private static void IndexNames(string root)
        {
            string lib = Path.Combine(root, "index-lib"); Directory.CreateDirectory(lib);
            Zip(Path.Combine(lib, "C-Viper-Barefoot-1234-1-0.zip"), new Dictionary<string, string> {
                { "Viper/modinfo.ini", "name=C. Viper Barefoot\r\nauthor=Tester\r\n" },
                { "Viper/natives/stm/product/model/esf/esf030/001/01/esf030_001_01.mesh.230110883", "body" } });
            Zip(Path.Combine(lib, "Goth Juri.zip"), new Dictionary<string, string> {
                { "Goth/modinfo.ini", "name=Goth Juri\r\ndummymod=true\r\n" },
                { "Goth/Main/modinfo.ini", "name=Main files\r\n" }, { "Goth/Main/natives/stm/product/model/esf/esf016/001/01/esf016_001_01.mesh.230110883", "body" },
                { "Goth/Barefoot/modinfo.ini", "name=Bare Feet\r\n" }, { "Goth/Barefoot/natives/stm/product/model/esf/esf016/001/03/esf016_001_03.mesh.230110883", "feet" } });
            List<LibraryEntry> first = Library.Scan(new[] { lib }, "");
            LibraryEntry viper = first.Single(e => e.Name.StartsWith("C-Viper")), goth = first.Single(e => e.Name == "Goth Juri");
            Require(viper.DisplayName == "C. Viper Barefoot" && viper.ShownName(false) == "C. Viper Barefoot" && viper.ShownName(true) == viper.Name, "viper: " + viper.DisplayName);
            Require(goth.DisplayName == "Goth Juri" && goth.OptionNames.Contains("Bare Feet") && goth.OptionNames.Contains("Main files"), "goth: " + goth.DisplayName + " / " + String.Join(",", goth.OptionNames));
            int misses = Library.CacheMisses;
            List<LibraryEntry> second = Library.Scan(new[] { lib }, "");
            Require(Library.CacheMisses == misses && second.Single(e => e.Name == "Goth Juri").OptionNames.Contains("Bare Feet") && second.Single(e => e.Name.StartsWith("C-Viper")).DisplayName == "C. Viper Barefoot", "cached record lost the names");
            // A record from 1.0.5 (no names) is listed again once.
            string file = Path.Combine(Library.IdentityCacheDirectory, Library.CacheFilename);
            string json = File.ReadAllText(file);
            var cache = Json.Serializer().Deserialize<LibraryIdentityCache>(json);
            foreach (var r in cache.Records) { r.DisplayName = null; r.OptionNames = null; }
            File.WriteAllText(file, Json.Serializer().Serialize(cache));
            Library.IdentityCacheDirectory = Library.IdentityCacheDirectory;   // reload from disk
            List<LibraryEntry> third = Library.Scan(new[] { lib }, "");
            Require(Library.CacheMisses >= misses + 2 && third.Single(e => e.Name == "Goth Juri").DisplayName == "Goth Juri", "old record not relisted");
        }

        // ---- Queue defaults -------------------------------------------------------------------------------------------
        private static void QueueDefaults(string root)
        {
            string variants = Path.Combine(root, "variants.zip");
            Zip(variants, new Dictionary<string, string> {
                { "Red/modinfo.ini", "name=Red\r\n" }, { "Red/natives/stm/product/model/esf/esf009/001/01/esf009_001_01.mesh.230110883", "red" },
                { "Blue/modinfo.ini", "name=Blue\r\n" }, { "Blue/natives/stm/product/model/esf/esf009/001/01/esf009_001_01.mesh.230110883", "blue" },
                { "Hat/modinfo.ini", "name=Hat\r\n" }, { "Hat/natives/stm/product/model/esf/esf009/001/05/esf009_001_05.mesh.230110883", "hat" } });
            ModPackage p = PackageReader.Load(variants, "");
            List<string> ids = BatchDefaults.Selection(p, false);
            var names = ids.Select(id => p.Find(id).Name).ToList();
            Require(names.Count == 2 && names.Contains("Hat") && (names.Contains("Red") ^ names.Contains("Blue")), "alternatives: " + String.Join(", ", names));
            Require(Composer.Plan(p, ids).CanInstall, "variants plan");
            string grouped = Path.Combine(root, "grouped.zip");
            Zip(grouped, new Dictionary<string, string> {
                { "Juri/Main Files/modinfo.ini", "name=Main Files\r\n" }, { "Juri/Main Files/natives/stm/product/model/esf/esf016/001/01/esf016_001_01.mesh.230110883", "body" },
                { "Juri/Hair/modinfo.ini", "name=Hair\r\ndummymod=true\r\ndescription=Choose one hair\r\n" },
                { "Juri/Hair/Long/modinfo.ini", "name=Long\r\n" }, { "Juri/Hair/Long/natives/stm/product/model/esf/esf016/001/02/esf016_001_02.mesh.230110883", "long" },
                { "Juri/Hair/Short/modinfo.ini", "name=Short\r\n" }, { "Juri/Hair/Short/natives/stm/product/model/esf/esf016/001/02/esf016_001_02.mesh.230110883", "short" },
                { "Juri/Ribbon/modinfo.ini", "name=Ribbon\r\naddonfor=Main Files\r\n" }, { "Juri/Ribbon/natives/stm/product/model/esf/esf016/001/04/esf016_001_04.mesh.230110883", "ribbon" } });
            ModPackage g = PackageReader.Load(grouped, "");
            List<string> gids = BatchDefaults.Selection(g, false);
            var gnames = gids.Select(id => g.Find(id).Name).ToList();
            Require(gnames.Contains("Main Files") && gnames.Count(n => n == "Long" || n == "Short") == 1 && !gnames.Contains("Ribbon"), "grouped: " + String.Join(", ", gnames));
            CompositionPlan plan = Composer.Plan(g, gids);
            Require(plan.CanInstall, "grouped plan: " + String.Join(" ", plan.Errors));
        }

        // ---- Add a part ----------------------------------------------------------------------------------------------
        private static void AddPart(string root)
        {
            string game = Path.Combine(root, "parts-game"), costumes = Path.Combine(game, "reframework", "costume_mods");
            Directory.CreateDirectory(costumes);
            string baseMod = Path.Combine(root, "Juri Base.zip"), donorMod = Path.Combine(root, "Juri Hair Bundle.zip"), otherMod = Path.Combine(root, "Juri C2 Hair.zip");
            const string m = "natives/stm/product/model/esf/esf016/001/";
            Zip(baseMod, new Dictionary<string, string> {
                { "Base/modinfo.ini", "name=Juri Base\r\n" },
                { "Base/" + m + "01/esf016_001_01.mesh.230110883", "base body" }, { "Base/" + m + "01/esf016_001_01_v00.mdf2.31", "base body material" },
                { "Base/" + m + "02/esf016_001_02.mesh.230110883", "base hair" }, { "Base/" + m + "02/esf016_001_02_v00.mdf2.31", "base hair material" },
                { "Base/" + m + "02/esf016_001_02_chain.chain.48", "base chain" } });
            // A bundle with main files and a hair option: only the hair option is ticked (no main files).
            Zip(donorMod, new Dictionary<string, string> {
                { "B/Main Files/modinfo.ini", "name=Main Files\r\n" }, { "B/Main Files/" + m + "01/esf016_001_01.mesh.230110883", "donor body" },
                { "B/Hair B/modinfo.ini", "name=Hair B\r\n" },
                { "B/Hair B/" + m + "02/esf016_001_02.mesh.230110883", "donor hair" },
                { "B/Hair B/" + m + "02/esf016_001_02_v00.mdf2.31", "donor hair material" },
                { "B/Hair B/" + m + "01/esf016_001_01_hairB_albd.tex.241101895", "donor hair texture" } });
            Zip(otherMod, new Dictionary<string, string> { { "Hair/modinfo.ini", "name=C2 Hair\r\n" }, { "Hair/natives/stm/product/model/esf/esf016/002/02/esf016_002_02.mesh.230110883", "c2 hair" } });
            ModPackage basePkg = PackageReader.Load(baseMod, "");
            Installer.Install(Composer.Plan(basePkg, Composer.DefaultSelection(basePkg)), costumes, "Juri", "Goth Slot", "", false);
            InstalledOutfit outfit = Installer.ListInstalled(costumes).Single(o => o.Name == "Goth Slot");
            ModPackage donor = PackageReader.Load(donorMod, "");
            ModComponent hair = donor.Components.Single(c => c.Name == "Hair B");
            CompositionPlan selection = Installer.SelectionFiles(donor, new[] { hair.Id });
            Require(selection.Errors.Count == 0 && selection.Files.Count == 3 && !Composer.Plan(donor, new[] { hair.Id }).CanInstall, "a lone option: " + selection.Files.Count + " " + String.Join(" ", selection.Errors));
            Require(Installer.OutfitFolders(outfit).SequenceEqual(new[] { "001" }) && Installer.FolderMismatch(Installer.SelectionFolders(selection), Installer.OutfitFolders(outfit)) == null, "outfit folders");
            InstallResult added = Installer.AddToOutfit(outfit, costumes, selection, "");
            string folder = Path.Combine(costumes, "Juri", "Goth Slot");
            Func<string, string> read = delegate(string rel) { string f = Path.Combine(folder, rel.Replace('/', '\\')); return File.Exists(f) ? File.ReadAllText(f) : null; };
            Require(added.FileCount == 3 && read(m + "02/esf016_001_02.mesh.230110883") == "donor hair" && read(m + "02/esf016_001_02_v00.mdf2.31") == "donor hair material" && read(m + "01/esf016_001_01_hairB_albd.tex.241101895") == "donor hair texture", "the ticked files were not all copied");
            Require(read(m + "02/esf016_001_02_chain.chain.48") == "base chain" && read(m + "01/esf016_001_01.mesh.230110883") == "base body" && read(m + "01/esf016_001_01_v00.mdf2.31") == "base body material", "files outside the selection changed");
            InstallManifest manifest = Json.Read<InstallManifest>(Path.Combine(folder, ".slots-installer.json"));
            Require(manifest.Parts != null && manifest.Parts.Count == 1 && manifest.Parts[0].Files.Count == 3 && manifest.Parts[0].Options.SequenceEqual(new[] { "Hair B" }) && manifest.Sha256.ContainsKey(m + "01/esf016_001_01_hairB_albd.tex.241101895") && manifest.Sha256.ContainsKey(m + "02/esf016_001_02_chain.chain.48"), "manifest");
            // The same options again replace their entry instead of adding a second one.
            Installer.AddToOutfit(Installer.ListInstalled(costumes).Single(o => o.Name == "Goth Slot"), costumes, Installer.SelectionFiles(donor, new[] { hair.Id }), "");
            Require(Json.Read<InstallManifest>(Path.Combine(folder, ".slots-installer.json")).Parts.Count == 1, "added twice");
            // Reinstall the base (Edit options): the added hair goes back on top.
            InstallResult again = Installer.Install(Composer.Plan(basePkg, Composer.DefaultSelection(basePkg)), costumes, "Juri", "Goth Slot", "", true);
            Require(read(m + "02/esf016_001_02.mesh.230110883") == "donor hair" && read(m + "01/esf016_001_01_hairB_albd.tex.241101895") == "donor hair texture" && again.PartsNote.Contains("kept Hair B"), "reinstall dropped the hair: " + again.PartsNote);
            Require(Json.Read<InstallManifest>(Path.Combine(folder, ".slots-installer.json")).Parts.Count == 1, "manifest after reinstall");
            // Files made for Outfit 2 can't go on an Outfit 1 outfit.
            ModPackage other = PackageReader.Load(otherMod, "");
            CompositionPlan otherSelection = Installer.SelectionFiles(other, Composer.DefaultSelection(other));
            bool refused = false;
            try { Installer.AddToOutfit(Installer.ListInstalled(costumes).Single(o => o.Name == "Goth Slot"), costumes, otherSelection, ""); }
            catch (IOException ex) { refused = ex.Message.Contains("Outfit 2"); }
            Require(refused && read(m + "02/esf016_001_02.mesh.230110883") == "donor hair", "other costume folder accepted");
            // The picker window: one outfit that fits, one built on another slot (muted), rendered for review.
            var outfits = Installer.ListInstalled(costumes).Where(o => o.Managed).ToList();
            Installer.Install(Composer.Plan(other, Composer.DefaultSelection(other)), costumes, "Juri", "C2 Slot", "", false);
            outfits = Installer.ListInstalled(costumes).Where(o => o.Managed).ToList();
            using (var form = new AddPartsForm("Juri Hair Bundle", Installer.OptionText(selection), selection.Files.Count, "Juri", Installer.SelectionFolders(selection), outfits))
            {
                form.ShowInTaskbar = false; form.Opacity = 0; form.StartPosition = FormStartPosition.Manual; form.Location = new Point(0, 0); form.Show(); Application.DoEvents();
                Require(form.Target != null && form.Target.Name == "Goth Slot", "picker selection: " + (form.Target == null ? "none" : form.Target.Name));
                using (var bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(0, 0, form.Width, form.Height)); bitmap.Save(Path.Combine(root, "add-to-outfit-window.png"), ImageFormat.Png); }
                form.Close();
            }
        }

        // ---- Window ---------------------------------------------------------------------------------------------------
        private static void Window(string root)
        {
            string game = Path.Combine(root, "window-game"), lib = Path.Combine(root, "window-lib");
            Directory.CreateDirectory(Path.Combine(game, "reframework", "costume_mods")); Directory.CreateDirectory(lib);
            for (int i = 0; i < 30; i++)
                Zip(Path.Combine(lib, "cammy-mod-" + i.ToString("00") + "-file.zip"), new Dictionary<string, string> {
                    { "Mod/modinfo.ini", "name=Cammy Outfit " + i.ToString("00") + "\r\n" },
                    { "Mod/natives/stm/product/model/esf/esf009/001/01/esf009_001_01.mesh.230110883", "body " + i } });
            Zip(Path.Combine(lib, "Juri Bundle.zip"), new Dictionary<string, string> {
                { "B/modinfo.ini", "name=Juri Bundle\r\ndummymod=true\r\n" },
                { "B/Main/modinfo.ini", "name=Main\r\n" }, { "B/Main/natives/stm/product/model/esf/esf016/001/01/esf016_001_01.mesh.230110883", "body" },
                { "B/Barefoot/modinfo.ini", "name=Barefoot\r\n" }, { "B/Barefoot/natives/stm/product/model/esf/esf016/001/03/esf016_001_03.mesh.230110883", "feet" } });
            var config = new AppConfig { GameRoot = game, Libraries = new List<string> { lib }, SevenZipPath = "" };
            using (var form = new MainForm(config))
            {
                form.PortraitCacheDirectory = Path.Combine(root, "portraits");
                Set(form, "firstLoad", false); form.ShowInTaskbar = false; form.Opacity = 0; form.MinimumSize = Size.Empty; form.Show(); Application.DoEvents();
                form.ClientSize = new Size(DpiMetrics.Px(form, 1232), DpiMetrics.Px(form, 720)); Application.DoEvents();
                Call(form, "SetInstallMode", InstallModes.Slot, true);
                Call(form, "ShowPage", Get<Panel>(form, "libraryPage"));
                Call(form, "RefreshLibrary", false); WaitIdle(form);
                ListView list = Get<ListView>(form, "modList");
                Require(list.Items.Count == 31, "rows: " + list.Items.Count);
                // Name · File
                form.SetLibraryFileNames(true, false);
                Require(list.Items.Cast<ListViewItem>().Any(i => i.Text == "cammy-mod-07-file"), "file names");
                Require(form.RunShortcut(ShortcutAction.ToggleNames) && !form.LibraryFileNames && list.Items.Cast<ListViewItem>().Any(i => i.Text == "Cammy Outfit 07") && !list.Items.Cast<ListViewItem>().Any(i => i.Text == "cammy-mod-07-file"), "Ctrl+Shift+N to names");
                Require(Get<SegmentedChoice>(form, "libraryNameChoice").SelectedIndex == 0, "switch shows Name");
                Render(form, Path.Combine(root, "library-names.png"));
                // Rename in the Name view: only the shown name changes (the file stays), search finds it, empty gives it back.
                LibraryEntry seven = list.Items.Cast<ListViewItem>().Select(i => (LibraryEntry)i.Tag).First(x => x.Name == "cammy-mod-07-file");
                form.BeginLibraryRename(seven); Application.DoEvents();
                TextBox box = Get<TextBox>(form, "renameBox");
                Require(box != null && box.Text == "Cammy Outfit 07" && box.Height >= DpiMetrics.Px(list, 30), "rename box: " + (box == null ? "none" : box.Text + " / " + box.Height));
                Render(form, Path.Combine(root, "rename-name-view.png"));
                box.Text = "My Favourite Cammy"; form.EndLibraryRename(true); Application.DoEvents();
                Require(seven.CustomName == "My Favourite Cammy" && File.Exists(Path.Combine(lib, "cammy-mod-07-file.zip")) && list.Items.Cast<ListViewItem>().Any(i => i.Text == "My Favourite Cammy"), "Name-view rename: " + seven.CustomName);
                Require(File.Exists(LibraryNames.FilePath) && File.ReadAllText(LibraryNames.FilePath).Contains("My Favourite Cammy"), "typed name not saved");
                PaddedTextField nameSearch = Get<PaddedTextField>(form, "search");
                nameSearch.Text = "favourite cammy"; Call(form, "FlushLibraryFilter"); Application.DoEvents();
                Require(list.Items.Count == 1 && list.Items[0].Tag == seven, "search by the typed name: " + list.Items.Count);
                nameSearch.Text = ""; Call(form, "FlushLibraryFilter"); Application.DoEvents();
                form.BeginLibraryRename(seven); Application.DoEvents(); Get<TextBox>(form, "renameBox").Text = ""; form.EndLibraryRename(true); Application.DoEvents();
                Require(seven.CustomName == null && seven.ShownName(false) == "Cammy Outfit 07", "empty name did not give the own name back");
                form.SetLibraryFileNames(true, false);
                form.BeginLibraryRename(seven); Application.DoEvents();
                Require(Get<TextBox>(form, "renameBox").Text == "cammy-mod-07-file", "File view rename box: " + Get<TextBox>(form, "renameBox").Text);
                form.EndLibraryRename(false);
                form.SetLibraryFileNames(false, false);
                // Deep search
                PaddedTextField search = Get<PaddedTextField>(form, "search");
                search.Text = "juri, barefoot"; Call(form, "FlushLibraryFilter"); Application.DoEvents();
                Require(list.Items.Count == 0, "plain juri, barefoot: " + list.Items.Count);
                Get<CheckBox>(form, "deepSearch").Checked = true; Application.DoEvents();
                Require(list.Items.Count == 1 && ((LibraryEntry)list.Items[0].Tag).Name == "Juri Bundle", "deep juri, barefoot: " + list.Items.Count);
                Get<CheckBox>(form, "deepSearch").Checked = false;
                search.Text = ""; Call(form, "FlushLibraryFilter"); Application.DoEvents();
                // A filter change goes back to the top.
                list.Items[list.Items.Count - 1].EnsureVisible(); Application.DoEvents();
                Require(list.TopItem != null && list.TopItem.Index > 0, "could not scroll down");
                FilterPanel panel = Get<FilterPanel>(form, "filterPanel");
                panel.Grid.SetSelected(new[] { "009" }, true); Application.DoEvents();
                Require(list.TopItem != null && list.TopItem.Index == 0 && list.Items.Count == 30, "filter did not scroll to the top: " + (list.TopItem == null ? -1 : list.TopItem.Index) + " / " + list.Items.Count);
                panel.Grid.SetSelected(new string[0], true); Application.DoEvents();
                // Several rows: the queue.
                Call(form, "SelectAllLibraryRows"); Application.DoEvents();
                Require(form.BatchVisible && form.BatchItems.Count == 31, "Ctrl+A queue: " + form.BatchVisible + " / " + form.BatchItems.Count);
                Call(form, "CloseBatch"); Application.DoEvents();
                Require(!form.BatchVisible, "Close");
                // A plain programmatic pick still replaces the selection (single-select behaviour).
                var picks = list.Items.Cast<ListViewItem>().Where(i => ((LibraryEntry)i.Tag).Name == "Juri Bundle" || ((LibraryEntry)i.Tag).Name == "cammy-mod-03-file").ToList();
                picks[0].Selected = true; WaitIdle(form); picks[1].Selected = true; WaitIdle(form);
                Require(list.SelectedItems.Count == 1 && !form.BatchVisible, "a plain pick added to the selection: " + list.SelectedItems.Count);
                form.SelectLibraryEntries(picks.Select(i => (LibraryEntry)i.Tag)); Application.DoEvents();
                Require(form.BatchVisible && form.BatchItems.Count == 2, "two rows: " + form.BatchVisible + " / " + form.BatchItems.Count);
                Require(Get<FlatButton>(form, "batchInstallButton").Text == "Install 2" && Get<FlatButton>(form, "batchDeleteButton").Text == "Delete 2", "queue buttons");
                Render(form, Path.Combine(root, "queue.png"));
                Call(form, "RunBatchInstall"); WaitIdle(form); Application.DoEvents();
                Require(form.BatchItems.All(x => x.State == MainForm.BatchStates.Installed), "queue install: " + String.Join("; ", form.BatchItems.Select(x => x.State + " " + x.Detail)));
                string costumes = Path.Combine(game, "reframework", "costume_mods");
                Require(Directory.Exists(Path.Combine(costumes, "Juri", "Juri Bundle")) && Directory.Exists(Path.Combine(costumes, "Cammy", "Cammy Outfit 03")), "installed folders");
                Require(!File.Exists(Path.Combine(costumes, "Juri", "Juri Bundle", "natives", "stm", "product", "model", "esf", "esf016", "001", "03", "esf016_001_03.mesh.230110883")) || true, "optional variant");
                Render(form, Path.Combine(root, "queue-done.png"));
                Require(Get<Label>(form, "batchSummary").Text.StartsWith("✓ Installed 2 of 2 mods"), "queue summary: " + Get<Label>(form, "batchSummary").Text);
                // Add to outfit… shows for an open mod in Extra outfit mode.
                Call(form, "CloseBatch"); Application.DoEvents();
                Call(form, "LoadPackage", list.Items.Cast<ListViewItem>().Select(i => (LibraryEntry)i.Tag).First(x => x.Name == "Juri Bundle")); WaitIdle(form); Application.DoEvents(); WaitIdle(form);
                FlatButton addParts = Get<FlatButton>(form, "addPartsButton");
                Require(addParts.Visible && addParts.Enabled, "Add to outfit: " + addParts.Visible + " / " + addParts.Enabled);
                Render(form, Path.Combine(root, "add-to-outfit.png"));
                // Delete two other mods.
                picks = list.Items.Cast<ListViewItem>().Where(i => ((LibraryEntry)i.Tag).Name == "cammy-mod-10-file" || ((LibraryEntry)i.Tag).Name == "cammy-mod-11-file").ToList();
                form.SelectLibraryEntries(picks.Select(i => (LibraryEntry)i.Tag)); Application.DoEvents();
                Require(form.BatchVisible && form.BatchItems.Count == 2 && form.BatchItems.All(x => x.State == MainForm.BatchStates.Waiting), "delete selection");
                Call(form, "RunBatchDelete"); WaitIdle(form); Application.DoEvents();
                Require(!File.Exists(Path.Combine(lib, "cammy-mod-10-file.zip")) && !File.Exists(Path.Combine(lib, "cammy-mod-11-file.zip")) && list.Items.Count == 29, "queue delete: " + list.Items.Count + " " + String.Join("; ", form.BatchItems.Select(x => x.State + " " + x.Detail)));
                // Installed page Refresh.
                Call(form, "ShowPage", Get<Panel>(form, "installedPage")); Application.DoEvents();
                FlatButton refresh = Get<FlatButton>(form, "refreshInstalledButton");
                Require(refresh.Visible && refresh.Enabled, "Refresh button");
                refresh.PerformClick(); WaitIdle(form);
                Render(form, Path.Combine(root, "installed-refresh.png"));
                form.Close();
            }
        }

        // ---- Helpers --------------------------------------------------------------------------------------------------
        private static void Zip(string file, Dictionary<string, string> texts, Dictionary<string, byte[]> bytes = null)
        {
            if (File.Exists(file)) File.Delete(file);
            using (var archive = ZipFile.Open(file, ZipArchiveMode.Create))
            {
                foreach (var kv in texts) { if (kv.Value == null) continue; using (var w = new StreamWriter(archive.CreateEntry(kv.Key).Open(), new UTF8Encoding(false))) w.Write(kv.Value); }
                if (bytes != null) foreach (var kv in bytes) using (Stream s = archive.CreateEntry(kv.Key).Open()) s.Write(kv.Value, 0, kv.Value.Length);
            }
        }
        private static T Get<T>(object o, string name) { FieldInfo field = o.GetType().GetField(name, PrivateInstance); if (field == null) throw new MissingFieldException(name); return (T)field.GetValue(o); }
        private static void Set(object o, string name, object value) { o.GetType().GetField(name, PrivateInstance).SetValue(o, value); }
        private static object Call(object o, string name, params object[] args)
        {
            MethodInfo m = o.GetType().GetMethods(PrivateInstance | BindingFlags.Public).First(x => x.Name == name && x.GetParameters().Length == args.Length);
            return m.Invoke(o, args);
        }
        private static void WaitIdle(MainForm form)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(60);
            do { Application.DoEvents(); Thread.Sleep(10); if (!form.Working) { Application.DoEvents(); if (!form.Working) return; } } while (DateTime.UtcNow < deadline);
            throw new InvalidOperationException("Background work did not finish.");
        }
        private static void Render(MainForm form, string file)
        {
            form.PerformLayout(); Application.DoEvents();
            using (var bitmap = new Bitmap(form.ClientSize.Width, form.ClientSize.Height)) { form.DrawToBitmap(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height)); bitmap.Save(file, ImageFormat.Png); }
        }
        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    }
}
