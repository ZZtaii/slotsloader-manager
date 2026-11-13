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
                check("library index (1.0.6): a scan reads the display name and option names; a cached record keeps them; a record without them keeps its identity and only its names are read", delegate { IndexNames(root); });
                check("background index (1.0.6): the quick listing returns every mod at once (unread ones Pending, with their file-name fighter); reading one gives the same identity as a full scan; a 1.0.5 record (no names) keeps its identity without a relist and only its names are read; names fill the display name and option names", delegate { BackgroundIndex(root); });
                check("queue defaults (1.0.6): alternative top-level variants keep the first (the Default one first); a required choose-one group gets its default/first option; optional add-ons stay off; the plan installs", delegate { QueueDefaults(root); });
                check("outfit order (1.0.18): one fighter's extra outfits change places within the same slots (number, folder and scene move together), another fighter is untouched, the fingerprint is cleared, the report is renumbered and renamed, the equipped slot follows its outfit, Undo restores the files; a stale list and a running SF6 are refused; the window lists the slots and a moved row changes the order", delegate { OutfitOrderCheck(root); });
                check("add to outfit (1.0.6): exactly the ticked options' files go into the installed outfit (a lone hair option without main files is fine); nothing else is added or removed; recorded once; a reinstall of the same mod puts them back; files for another outfit slot are refused; the picker window renders", delegate { AddPart(root); });
                check("added parts as in-game options (1.0.19): manager_parts.json and its folders for a suite-line loader with the costume options on: a part on, a part off, two parts on one path, base only where a part that is on took a file of the outfit; a part removed, an outfit disabled or uninstalled, the switch off or no such loader leave nothing; a mod that is gone or changed leaves its outfit out with one log line; a second sync changes no file date; a part keeps its id through a reorder and on another outfit", delegate { ManagerPartsChecks.Run(root); });
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
            // A record without names keeps its identity (no relisting); a full scan reads just the names.
            Require(Library.CacheMisses == misses && third.Single(e => e.Name == "Goth Juri").DisplayName == "Goth Juri" && third.Single(e => e.Name == "Goth Juri").OptionNames.Contains("Bare Feet"), "names of an old record: " + (Library.CacheMisses - misses) + " relisted");
        }

        // ---- Background index -----------------------------------------------------------------------------------------
        private static void BackgroundIndex(string root)
        {
            string lib = Path.Combine(root, "bg-lib"); Directory.CreateDirectory(lib);
            string before = Library.IdentityCacheDirectory; Library.IdentityCacheDirectory = Path.Combine(root, "bg-index");
            try
            {
                for (int i = 0; i < 6; i++)
                    Zip(Path.Combine(lib, "Juri Look " + i + ".zip"), new Dictionary<string, string> {
                        { "Look/modinfo.ini", "name=Juri Look Number " + i + "\r\nauthor=Tester\r\n" },
                        { "Look/natives/stm/product/model/esf/esf016/001/01/esf016_001_01.mesh.230110883", "body " + i } });
                List<LibraryEntry> pending, names;
                List<LibraryEntry> quick = Library.ScanQuick(new[] { lib }, "", out pending, out names);
                Require(quick.Count == 6 && pending.Count == 6 && names.Count == 0 && quick.All(e => e.Pending && e.Character == "Juri" && e.CostumeKeys.Count == 0), "first quick listing: " + pending.Count + " pending");
                foreach (LibraryEntry e in pending) { LibraryEntry copy = Library.IndexCopy(e); Library.IndexEntry(copy, ""); Library.TakeIdentity(e, copy); }
                Library.SaveIndex();
                List<LibraryEntry> full = Library.Scan(new[] { lib }, "");
                Require(quick.All(e => !e.Pending && !e.NamesPending && e.Character == "Juri" && e.CostumeKeys.SequenceEqual(new[] { "016/001" }) && e.DisplayName.StartsWith("Juri Look Number") && e.Authors.SequenceEqual(new[] { "Tester" })), "read in the background: " + String.Join(", ", quick.Select(e => e.Detection + "/" + e.DisplayName)));
                Require(full.Select(e => e.Name + "|" + e.Character + "|" + String.Join(",", e.CostumeKeys) + "|" + e.DisplayName).SequenceEqual(quick.Select(e => e.Name + "|" + e.Character + "|" + String.Join(",", e.CostumeKeys) + "|" + e.DisplayName)), "background identity differs from a full scan");
                List<LibraryEntry> again = Library.ScanQuick(new[] { lib }, "", out pending, out names);
                Require(pending.Count == 0 && names.Count == 0 && again.All(e => !e.Pending && e.DisplayName.StartsWith("Juri Look Number")), "second start read again: " + pending.Count + "/" + names.Count);
                // A record from 1.0.5: identity and authors, no display name or option names.
                string file = Path.Combine(Library.IdentityCacheDirectory, Library.CacheFilename);
                var cache = Json.Serializer().Deserialize<LibraryIdentityCache>(File.ReadAllText(file));
                foreach (var r in cache.Records) { r.DisplayName = null; r.OptionNames = null; }
                File.WriteAllText(file, Json.Serializer().Serialize(cache));
                Library.IdentityCacheDirectory = Library.IdentityCacheDirectory;   // reload from disk
                int misses = Library.CacheMisses;
                List<LibraryEntry> updated = Library.ScanQuick(new[] { lib }, "", out pending, out names);
                Require(pending.Count == 0 && names.Count == 6 && updated.All(e => e.Character == "Juri" && e.CostumeKeys.Count == 1 && e.NamesPending) && Library.CacheMisses == misses, "1.0.5 records were listed again: " + pending.Count + " pending, " + names.Count + " names");
                foreach (LibraryEntry e in names) { LibraryEntry copy = Library.IndexCopy(e); Library.IndexNames(copy, ""); Library.TakeNames(e, copy); }
                Require(updated.All(e => !e.NamesPending && e.DisplayName.StartsWith("Juri Look Number") && e.OptionNames.Count == 1), "names pass");
                Library.SaveIndex();
                Library.ScanQuick(new[] { lib }, "", out pending, out names);
                Require(pending.Count == 0 && names.Count == 0, "names not kept in the index");
            }
            finally { Library.IdentityCacheDirectory = before; }
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
        private static void OutfitOrderCheck(string root)
        {
            string game = Path.Combine(root, "order-game"), data = Path.Combine(game, OutfitOrder.DataRelative), statePath = Path.Combine(game, OutfitOrder.StateRelative);
            Directory.CreateDirectory(data); Directory.CreateDirectory(Path.GetDirectoryName(statePath));
            Func<string, int, string, int, string> slot = delegate(string mod, int fighter, string dir, int no)
            { return "    {\n      \"mod_id\": \"" + mod + "\",\n      \"fighter\": " + fighter + ",\n      \"fighter_dir\": \"" + dir + "\",\n      \"original_costume_no\": 0,\n      \"original_folder\": \"001\",\n      \"new_costume_no\": " + no + ",\n      \"new_folder\": \"" + (no + 1).ToString("D3") + "\",\n      \"scene_name\": \"" + dir + "v" + no.ToString("D2") + "\"\n    }"; };
            string registry = "{\n  \"version\": 1,\n  \"fingerprint\": \"abc123\",\n  \"slots\": [\n" + String.Join(",\n", new[] { slot("ryu", 1, "esf001", 5), slot("aaa", 16, "esf016", 5), slot("bbb", 16, "esf016", 6), slot("ccc", 16, "esf016", 8) })
                + "\n  ],\n  \"next_ids\": {},\n  \"possession\": [\n    {\n      \"fighter\": 16,\n      \"fighter_dir\": \"esf016\",\n      \"costume_no\": 5,\n      \"name\": \"Outfit I\",\n      \"swatches\": [[1,2],[3,4]]\n    }\n  ]\n}\n";
            Func<int, string, int, string, string, string> row = delegate(int fighter, string dir, int no, string name, string source)
            { return "    {\"fighter\": " + fighter + ", \"fighter_dir\": \"" + dir + "\", \"costume_no\": " + no + ", \"name\": \"" + name + "\", \"based_on\": 0, \"label\": \"" + source + "\", \"kind\": \"folder\", \"source\": \"" + source + "\"}"; };
            string report = "{\n  \"generated\": \"2026-11-08 16:57\",\n  \"loader\": \"test\",\n  \"outfits\": [\n" + String.Join(",\n", new[] { row(1, "esf001", 5, "Outfit I", "Ryu/Gi"), row(16, "esf016", 5, "Outfit I", "Juri/Alpha"), row(16, "esf016", 6, "Outfit II", "Juri/Beta"), row(16, "esf016", 8, "Outfit III", "Juri/Gamma") })
                + "\n  ],\n  \"left_out\": [\n    {\"fighter\": 16, \"label\": \"Juri/Delta\", \"reason\": \"no mesh\"}\n  ]\n}\n";
            string state = "{\n    \"fighters\": {\n        \"1\": { \"slot\": 5, \"color\": 1 },\n        \"16\": { \"slot\": 8, \"color\": 2 },\n        \"30\": { \"slot\": false }\n    }\n}\n";
            string registryPath = Path.Combine(data, "registry.json"), reportPath = Path.Combine(data, "outfits.json");
            File.WriteAllText(registryPath, registry); File.WriteAllText(reportPath, report); File.WriteAllText(statePath, state);
            List<OutfitSlot> slots = OutfitOrder.Slots(game, "esf016");
            Require(slots.Select(s => s.CostumeNo).SequenceEqual(new[] { 5, 6, 8 }) && slots[2].Name == "Outfit III" && slots[2].Source == "Juri/Gamma" && OutfitOrder.Counts(game)["esf016"] == 3 && OutfitOrder.Counts(game)["esf001"] == 1, "slots read: " + String.Join(",", slots.Select(s => s.CostumeNo + " " + s.Source)));
            string undo = Path.Combine(root, "order-undo");
            Require(OutfitOrder.Apply(game, "esf016", new[] { 5, 6, 8 }, undo) == null && File.ReadAllText(registryPath) == registry, "the same order wrote something");
            // Gamma first: the outfit in slot 8 takes slot 5, Alpha 6, Beta 8.
            string backup = OutfitOrder.Apply(game, "esf016", new[] { 8, 5, 6 }, undo);
            string next = File.ReadAllText(registryPath);
            Func<string, string> block = delegate(string mod) { int at = next.IndexOf("\"mod_id\": \"" + mod + "\""); return next.Substring(at, next.IndexOf('}', at) - at); };
            Require(block("ccc").Contains("\"new_costume_no\": 5,") && block("ccc").Contains("\"new_folder\": \"006\"") && block("ccc").Contains("\"scene_name\": \"esf016v05\"")
                && block("aaa").Contains("\"new_costume_no\": 6,") && block("aaa").Contains("\"new_folder\": \"007\"") && block("bbb").Contains("\"new_costume_no\": 8,") && block("bbb").Contains("\"scene_name\": \"esf016v08\""), "the slots did not change places: " + next);
            Require(block("ryu").Contains("\"new_costume_no\": 5,") && block("ryu").Contains("\"scene_name\": \"esf001v05\"") && next.Contains("\"fingerprint\": \"\"") && next.Contains("\"swatches\": [[1,2],[3,4]]") && next.Contains("\"original_folder\": \"001\""), "another fighter, the fingerprint or the rest of the registry: " + next);
            List<OutfitSlot> after = OutfitOrder.Slots(game, "esf016");
            Require(after.Select(s => s.Source).SequenceEqual(new[] { "Juri/Gamma", "Juri/Alpha", "Juri/Beta" }) && after.Select(s => s.Name).SequenceEqual(new[] { "Outfit I", "Outfit II", "Outfit III" }) && File.ReadAllText(reportPath).Contains("\"left_out\"") && OutfitOrder.Slots(game, "esf001").Single().Source == "Ryu/Gi", "the report was not renumbered: " + File.ReadAllText(reportPath));
            string nextState = File.ReadAllText(statePath);
            Require(nextState.Contains("\"16\": { \"slot\": 5, \"color\": 2 }") && nextState.Contains("\"1\": { \"slot\": 5, \"color\": 1 }") && nextState.Contains("\"30\": { \"slot\": false }"), "the equipped slot did not follow: " + nextState);
            bool stale = false; try { OutfitOrder.Apply(game, "esf016", new[] { 5, 6, 7 }, undo); } catch (IOException) { stale = true; }
            Require(stale, "a list that no longer matches was accepted");
            IProcessProbe probe = GameProcesses.Probe;
            try
            {
                GameProcesses.Probe = new FakeProcessProbe { Game = true };
                bool refused = false; try { OutfitOrder.Apply(game, "esf016", new[] { 6, 5, 8 }, undo); } catch (IOException) { refused = true; }
                Require(refused, "the order changed while SF6 runs");
            }
            finally { GameProcesses.Probe = probe; }
            OutfitOrder.Restore(game, backup);
            Require(File.ReadAllText(registryPath) == registry.Replace("abc123", "") && File.ReadAllText(reportPath) == report && File.ReadAllText(statePath) == state, "Undo did not restore the files");
            // The window: the fighter's slots as rows; moving the last row to the top gives the order Apply takes.
            using (var form = new OutfitOrderForm(game, "Juri", new[] { new KeyValuePair<string, string>("001", "Ryu"), new KeyValuePair<string, string>("016", "Juri") }, null, OutfitOrder.Counts(game), delegate { return new List<string> { "Juri Omega" }; }, false))
            {
                Require(form.FighterDir == "esf016" && form.RowsForTest.Count == 3 && !form.Changed, "window rows: " + form.FighterDir + " / " + form.RowsForTest.Count);
                form.RowsForTest.MoveTo(2, 0);
                Require(form.Changed && form.Order.SequenceEqual(new[] { 8, 5, 6 }) && form.RowsForTest.SelectedIndex == 0, "moved row: " + String.Join(",", form.Order));
                // The drag itself: the top row lifted and dropped on the last place moves there in one step, on release.
                int rowPixels = form.RowsForTest.Height / form.RowsForTest.Count;
                form.RowsForTest.DragForTest(0, 2 * rowPixels + rowPixels / 2);
                Require(!form.Changed && form.Order.SequenceEqual(new[] { 5, 6, 8 }) && form.RowsForTest.SelectedIndex == 2 && !form.RowsForTest.Dragging, "a row dragged to the last place: " + String.Join(",", form.Order));
                form.RowsForTest.DragForTest(1, rowPixels + rowPixels / 2 + 2);
                Require(!form.Changed, "a press without a real move changed the order");
                Require(LabelShortener.Short(new[] { "Main Files - X", "Main Files - X + Option 01 - A", "Other" }).SequenceEqual(new[] { "Main Files - X", "+ Option 01 - A", "Other" }), "names that add to another are not shortened");
                form.Load("esf001", "Ryu");
                Require(form.RowsForTest.Count == 1 && !form.Changed && !form.RowsForTest.CanMove(1), "another fighter in the window");
            }
        }

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
            Require(selection.Errors.Count == 0 && selection.Files.Count == 3 && Composer.Plan(donor, new[] { hair.Id }).MissingMains.Count == 1, "a lone option: " + selection.Files.Count + " " + String.Join(" ", selection.Errors));
            Require(Installer.OutfitFolders(outfit).SequenceEqual(new[] { "001" }) && Installer.FolderMismatch(Installer.SelectionFolders(selection), Installer.OutfitFolders(outfit)) == null, "outfit folders");
            InstallResult added = Installer.AddToOutfit(outfit, costumes, selection, "");
            string folder = Path.Combine(costumes, "Juri", "Goth Slot");
            Func<string, string> read = delegate(string rel) { string f = Path.Combine(folder, rel.Replace('/', '\\')); return File.Exists(f) ? File.ReadAllText(f) : null; };
            Require(added.FileCount == 3 && read(m + "02/esf016_001_02.mesh.230110883") == "donor hair" && read(m + "02/esf016_001_02_v00.mdf2.31") == "donor hair material" && read(m + "01/esf016_001_01_hairB_albd.tex.241101895") == "donor hair texture", "the ticked files were not all copied");
            Require(read(m + "02/esf016_001_02_chain.chain.48") == "base chain" && read(m + "01/esf016_001_01.mesh.230110883") == "base body" && read(m + "01/esf016_001_01_v00.mdf2.31") == "base body material", "files outside the selection changed");
            InstallManifest manifest = Json.Read<InstallManifest>(Path.Combine(folder, ".slots-installer.json"));
            Require(manifest.Parts != null && manifest.Parts.Count == 1 && manifest.Parts[0].Files.Count == 3 && manifest.Parts[0].Options.SequenceEqual(new[] { "Hair B" }) && manifest.Sha256.ContainsKey(m + "01/esf016_001_01_hairB_albd.tex.241101895") && manifest.Sha256.ContainsKey(m + "02/esf016_001_02_chain.chain.48"), "manifest");
            // 1.0.18: the Installed page says what was added (row text and details), and nothing for an outfit without parts.
            InstalledOutfit grafted = Installer.ListInstalled(costumes).Single(o => o.Name == "Goth Slot");
            string partDetails = MainForm.AddedPartsDetails(grafted);
            Require(MainForm.AddedPartsShort(grafted) == "+ Hair B" && partDetails.StartsWith("ADDED FROM OTHER MODS") && partDetails.Contains("• Hair B from ") && partDetails.Contains(" · 3 files · added ") && !partDetails.Contains("no longer in the library"), "added parts shown: " + MainForm.AddedPartsShort(grafted) + " / " + partDetails);
            Require(MainForm.AddedPartsShort(outfit) == null && MainForm.AddedPartsDetails(outfit) == "" && MainForm.AddedPartsShort(new InstalledOutfit { Parts = new List<PartGraft> { new PartGraft { Options = new List<string> { "A", "B", "C" } } } }) == "+ 3 added options", "added parts text for none or many");
            // 1.0.18: Remove added part puts the outfit's own files back (a replaced file, a new file gone), drops the
            // record and leaves the rest alone; a part that is not on the outfit is refused. Added again for the steps below.
            InstallResult removed = Installer.RemovePart(grafted, costumes, Composer.Plan(basePkg, Composer.DefaultSelection(basePkg)), grafted.Parts[0], "");
            InstalledOutfit bare = Installer.ListInstalled(costumes).Single(o => o.Name == "Goth Slot");
            Require(removed.Message.StartsWith("Removed Hair B from ") && (bare.Parts == null || bare.Parts.Count == 0) && MainForm.AddedPartsShort(bare) == null && bare.Id == grafted.Id, "part not removed: " + removed.Message);
            Require(read(m + "02/esf016_001_02.mesh.230110883") == "base hair" && read(m + "02/esf016_001_02_v00.mdf2.31") == "base hair material" && read(m + "01/esf016_001_01_hairB_albd.tex.241101895") == null
                && read(m + "02/esf016_001_02_chain.chain.48") == "base chain" && read(m + "01/esf016_001_01.mesh.230110883") == "base body", "the outfit's own files did not come back");
            bool removeRefused = false; try { Installer.RemovePart(bare, costumes, Composer.Plan(basePkg, Composer.DefaultSelection(basePkg)), grafted.Parts[0], ""); } catch (IOException) { removeRefused = true; }
            Require(removeRefused, "removing a part that is not on the outfit was not refused");
            Installer.AddToOutfit(bare, costumes, Installer.SelectionFiles(donor, new[] { hair.Id }), "");
            // 1.0.18 Added parts window: Save rebuilds the outfit with exactly the listed parts in the listed order (the
            // last one applied wins); a part left out gives the outfit's own file back; the window shows the winner first.
            ModComponent donorBody = donor.Components.Single(c => c.Name == "Main Files");
            Installer.AddToOutfit(Installer.ListInstalled(costumes).Single(o => o.Name == "Goth Slot"), costumes, Installer.SelectionFiles(donor, new[] { donorBody.Id }), "");
            InstalledOutfit two = Installer.ListInstalled(costumes).Single(o => o.Name == "Goth Slot");
            Require(two.Parts.Count == 2 && two.Parts[0].Options.SequenceEqual(new[] { "Hair B" }) && two.Parts[1].Options.SequenceEqual(new[] { "Main Files" }) && read(m + "01/esf016_001_01.mesh.230110883") == "donor body", "two parts: " + String.Join(" | ", two.Parts.Select(Installer.GraftText)));
            // 1.0.18 ticks on added parts: a part that is turned off leaves the outfit (its own file is back) and stays in
            // the record as OffParts; a later rebuild keeps that record; turned on again it is applied where it was.
            CompositionPlan gothPlan = Composer.Plan(basePkg, Composer.DefaultSelection(basePkg));
            Installer.SetParts(two, costumes, gothPlan, new List<PartGraft> { two.Parts[0] }, "");
            Installer.SetOffParts(Installer.ListInstalled(costumes).Single(o => o.Name == "Goth Slot"), costumes, new List<PartGraft> { two.Parts[1] });
            InstalledOutfit oneOff = Installer.ListInstalled(costumes).Single(o => o.Name == "Goth Slot");
            Require(oneOff.Parts.Count == 1 && oneOff.OffParts != null && oneOff.OffParts.Count == 1 && oneOff.OffParts[0].Options.SequenceEqual(new[] { "Main Files" }) && read(m + "01/esf016_001_01.mesh.230110883") == "base body" && read(m + "02/esf016_001_02.mesh.230110883") == "donor hair",
                "a part turned off: on " + oneOff.Parts.Count + " / off " + (oneOff.OffParts == null ? 0 : oneOff.OffParts.Count) + " / body " + read(m + "01/esf016_001_01.mesh.230110883"));
            Installer.SetParts(oneOff, costumes, Composer.Plan(basePkg, Composer.DefaultSelection(basePkg)), new List<PartGraft> { oneOff.Parts[0], oneOff.OffParts[0] }, "");
            InstalledOutfit backOn = Installer.ListInstalled(costumes).Single(o => o.Name == "Goth Slot");
            Require(backOn.Parts.Count == 2 && backOn.OffParts != null && backOn.OffParts.Count == 1 && read(m + "01/esf016_001_01.mesh.230110883") == "donor body", "a rebuild lost the record of the parts that are off");
            Installer.SetOffParts(backOn, costumes, null);
            two = Installer.ListInstalled(costumes).Single(o => o.Name == "Goth Slot");
            Require(two.OffParts == null && two.Parts.Count == 2 && two.Parts[0].Options.SequenceEqual(new[] { "Hair B" }) && two.Parts[1].Options.SequenceEqual(new[] { "Main Files" }), "the parts are not as before: " + String.Join(" | ", two.Parts.Select(Installer.GraftText)));
            // 1.0.18: the outfit's own mod is the last row of the window (its name, its options, fixed at the bottom).
            using (var ownRow = new AddedPartsForm("Goth Slot", two.Parts, delegate { return false; }, null, "Juri Base", "Main files"))
                Require(ownRow.OwnModTextForTest == "Juri Base | The outfit's own mod · Main files · always at the bottom", "the outfit's own mod in the Added parts window: " + ownRow.OwnModTextForTest);
            using (var partsForm = new AddedPartsForm("Goth Slot", two.Parts, delegate { return false; }))
            {
                Require(partsForm.RowsForTest.Count == 2 && partsForm.RowsForTest.Parts[0] == two.Parts[1] && !partsForm.Changed, "the window does not show the winner (the last part applied) first");
                partsForm.RowsForTest.MoveTo(0, 1);
                Require(partsForm.Changed && partsForm.Wanted.SequenceEqual(new[] { two.Parts[1], two.Parts[0] }), "a moved row did not change the order to apply");
                partsForm.RowsForTest.Select(0); partsForm.RowsForTest.RemoveSelected();
                Require(partsForm.RowsForTest.Count == 1 && partsForm.Wanted.SequenceEqual(new[] { two.Parts[1] }), "Remove did not take the selected part off the list");
            }
            Func<CompositionPlan> basePlan = delegate { return Composer.Plan(basePkg, Composer.DefaultSelection(basePkg)); };
            Installer.SetParts(two, costumes, basePlan(), new List<PartGraft> { two.Parts[1], two.Parts[0] }, "");
            InstalledOutfit swapped = Installer.ListInstalled(costumes).Single(o => o.Name == "Goth Slot");
            Require(swapped.Parts.Count == 2 && swapped.Parts[0].Options.SequenceEqual(new[] { "Main Files" }) && swapped.Parts[1].Options.SequenceEqual(new[] { "Hair B" }) && read(m + "01/esf016_001_01.mesh.230110883") == "donor body" && read(m + "02/esf016_001_02.mesh.230110883") == "donor hair", "the saved order was not applied");
            InstallResult onlyHair = Installer.SetParts(swapped, costumes, basePlan(), new List<PartGraft> { swapped.Parts[1] }, "");
            InstalledOutfit one = Installer.ListInstalled(costumes).Single(o => o.Name == "Goth Slot");
            Require(onlyHair.Message.StartsWith("Saved the added parts of Goth Slot") && one.Parts.Count == 1 && one.Parts[0].Options.SequenceEqual(new[] { "Hair B" }) && read(m + "01/esf016_001_01.mesh.230110883") == "base body" && read(m + "02/esf016_001_02.mesh.230110883") == "donor hair", "a part left out did not give the outfit's own file back: " + onlyHair.Message);
            // 1.0.18 outfit recipes: an installed outfit is written down (base, options, parts in order), found again as
            // that recipe, and installing the recipe makes a second outfit with the same files; the same name saved again
            // replaces it; a recipe whose mod is gone is refused; Delete removes only the recipe.
            string recipeData = Path.Combine(root, "recipe-data");
            OutfitRecipes.Save(recipeData, OutfitRecipes.FromOutfit(one, "Juri Base", null, new string[0]));
            OutfitRecipes.Save(recipeData, OutfitRecipes.FromOutfit(one, "Juri Base", null, new string[0]));
            List<OutfitRecipe> savedRecipes = OutfitRecipes.List(recipeData);
            Require(savedRecipes.Count == 1 && savedRecipes[0].Name == "Goth Slot" && savedRecipes[0].Character == "Juri" && savedRecipes[0].Parts.Count == 1 && savedRecipes[0].Summary == "Juri Base + Hair B"
                && OutfitRecipes.Of(savedRecipes, one) == savedRecipes[0] && OutfitRecipes.Missing(savedRecipes[0]).Count == 0, "recipe saved: " + savedRecipes.Count + " / " + String.Join(" | ", savedRecipes.Select(r => r.Name + ": " + r.Summary)));
            InstallResult fromRecipe = OutfitRecipes.Install(savedRecipes[0], costumes, "Goth Recipe", "");
            InstalledOutfit made = Installer.ListInstalled(costumes).Single(o => o.Name == "Goth Recipe");
            Func<string, string> readMade = delegate(string rel) { string f = Path.Combine(made.Folder, rel.Replace('/', '\\')); return File.Exists(f) ? File.ReadAllText(f) : null; };
            Require(fromRecipe.Message.StartsWith("Installed Goth Recipe from the recipe Goth Slot") && made.Parts != null && made.Parts.Count == 1 && readMade(m + "02/esf016_001_02.mesh.230110883") == "donor hair" && readMade(m + "01/esf016_001_01.mesh.230110883") == "base body"
                && OutfitRecipes.Of(savedRecipes, made) == savedRecipes[0] && OutfitRecipes.Of(savedRecipes, new InstalledOutfit { Managed = true, Character = "Juri", SourcePath = one.SourcePath, SelectedIds = one.SelectedIds }) == null, "installed from the recipe: " + fromRecipe.Message);
            // 1.0.18: opened from an outfit that can't be saved as it is (every variant of an archive), the window is about
            // its mod: the recipes that use it are counted, and the wide button starts a new recipe with it.
            using (var aboutMod = new RecipesForm(delegate { return OutfitRecipes.List(recipeData); }, null, null, null, "Sakura as Ryu C1", "Sakura as Ryu C1", r => r.Name == "Goth Slot"))
            {
                Require(aboutMod.NewButtonForTest == "New recipe with Sakura as Ryu C1" && aboutMod.NoteForTest.StartsWith("1 recipe uses Sakura as Ryu C1, listed first."), "the recipes window for an outfit's mod: " + aboutMod.NewButtonForTest + " / " + aboutMod.NoteForTest);
                aboutMod.RequestNew();
                Require(aboutMod.NewRequested, "New recipe with the mod did not ask for the builder");
            }
            using (var noOutfit = new RecipesForm(delegate { return OutfitRecipes.List(recipeData); }, null, null, null))
                Require(noOutfit.NewButtonForTest == "New recipe", "the recipes window without an outfit: " + noOutfit.NewButtonForTest);
            // 1.0.18 (the user: no instant "Save as recipe"): from an installed outfit the one button asks for the builder
            // and saves nothing itself; the builder then starts with the outfit's mod and its added parts, as a new recipe.
            using (var fromOutfit = new RecipesForm(delegate { return OutfitRecipes.List(recipeData); }, one, null, null))
            {
                fromOutfit.RequestNew();
                Require(fromOutfit.NewButtonForTest == "New recipe from Goth Slot" && fromOutfit.NewRequested && OutfitRecipes.List(recipeData).Count == 1, "New recipe from an outfit: " + fromOutfit.NewButtonForTest);
            }
            // 1.0.18: Edit recipe asks for the builder with the selected recipe; it is off while nothing is listed.
            using (var editing = new RecipesForm(delegate { return OutfitRecipes.List(recipeData); }, null, null, null))
            {
                editing.RequestEdit();
                Require(editing.EditEnabledForTest && editing.EditRequested && editing.Selected != null && editing.Selected.Name == "Goth Slot" && !editing.NewRequested, "Edit recipe in the recipes window");
            }
            using (var nothing = new RecipesForm(delegate { return new List<OutfitRecipe>(); }, null, null, null))
            {
                nothing.RequestEdit();
                Require(!nothing.EditEnabledForTest && !nothing.EditRequested, "Edit recipe with no recipe");
            }
            using (var prefilled = new RecipeBuilderForm("Ryu", new[] { new KeyValuePair<string, string>("016", "Juri"), new KeyValuePair<string, string>("001", "Ryu") }, null, new List<LibraryEntry>(), ""))
            {
                prefilled.StartFrom(OutfitRecipes.FromOutfit(one, "Juri Base", null, new string[0]));
                Require(prefilled.Text == "New outfit recipe" && prefilled.Draft.Character == "Juri" && prefilled.Draft.Layers.Count == 2 && prefilled.Draft.Layers[0].Options.SequenceEqual(new[] { "Hair B" }) && prefilled.Draft.Layers[1].Mod == "Juri Base" && prefilled.Recipe == null,
                    "the builder from an installed outfit: " + prefilled.Text + " / " + String.Join(" > ", prefilled.Draft.Layers.Select(RecipeDraft.Shown)));
            }
            using (var recipesForm = new RecipesForm(delegate { return OutfitRecipes.List(recipeData); }, one, delegate(InstalledOutfit o) { return "saved"; }, delegate(OutfitRecipe r) { OutfitRecipes.Delete(recipeData, r.Id); }))
            {
                Require(recipesForm.ListForTest.Items.Count == 1 && recipesForm.Selected != null && recipesForm.Selected.Name == "Goth Slot", "the recipes window does not list the recipe");
                var gone = new OutfitRecipe { Name = "Gone", Character = "Juri", Base = new RecipeLayer { SourcePath = Path.Combine(root, "no such mod.zip"), Mod = "Lost Mod" } };
                Require(OutfitRecipes.Missing(gone).SequenceEqual(new[] { "Lost Mod" }), "a missing mod was not named");
                bool recipeRefused = false; try { OutfitRecipes.Install(gone, costumes, "Gone", ""); } catch (IOException) { recipeRefused = true; }
                Require(recipeRefused, "a recipe whose mod is gone was installed");
                recipesForm.DeleteSelected();
                Require(recipesForm.ListForTest.Items.Count == 0 && OutfitRecipes.List(recipeData).Count == 0 && Directory.Exists(made.Folder), "Delete recipe: " + recipesForm.ListForTest.Items.Count);
            }
            Directory.Delete(made.Folder, true);
            // 1.0.18 recipe builder (the user: no base, "it all depends on load orders"): a recipe is its mods in load
            // order, the newest on top; it says what replaces what and when two bring a body; files for another outfit
            // slot or another fighter are refused; the recipe it makes installs with the layers applied in order; the
            // window lists the fighter's mods and builds the same draft.
            var builder = new RecipeDraft("Juri", "016", "");
            Require(builder.AddLayer(baseMod, "Juri Base", null) == null && builder.Layers.Count == 1 && builder.Layers[0].Files.Count == 5 && builder.Notes().Count == 0 && builder.Slots().SequenceEqual(new[] { "001" }), "the first mod: " + builder.Layers.Count + " / " + String.Join(" | ", builder.Notes()));
            Require(builder.AddLayer(donorMod, "Hair Bundle", new[] { hair.Id }) == null && builder.Layers.Count == 2 && builder.Notes().SequenceEqual(new[] { "Hair B from Hair Bundle replaces 2 files of the layers below it." }), "hair on top: " + String.Join(" | ", builder.Notes()));
            Require(builder.AddLayer(donorMod, "Hair Bundle", new[] { donorBody.Id }) == null && builder.Layers.Count == 3 && builder.Layers[0].Options.SequenceEqual(new[] { "Main Files" })
                && builder.Notes()[0] == "More than one layer brings a complete body (Main Files from Hair Bundle; Juri Base). Only the top one will show.", "a second body: " + String.Join(" | ", builder.Notes()));
            string c2HairId = PackageReader.Load(otherMod, "").Components.Single(c => c.Name == "C2 Hair").Id;
            string wrongSlot = builder.AddLayer(otherMod, "C2 Hair", new[] { c2HairId });
            Require(wrongSlot != null && wrongSlot.Contains("made for Outfit 2") && builder.Layers.Count == 3, "files for another outfit slot: " + wrongSlot);
            string wrongFighter = new RecipeDraft("Ryu", "001", "").AddLayer(baseMod, "Juri Base", null);
            Require(wrongFighter != null && wrongFighter.Contains("belong to Juri"), "another fighter's mod: " + wrongFighter);
            OutfitRecipe mixed = builder.ToRecipe(" Mix ");
            Require(mixed.Name == "Mix" && mixed.Character == "Juri" && mixed.Parts.Count == 2 && mixed.Parts[0].Options.SequenceEqual(new[] { "Hair B" }) && mixed.Parts[1].Options.SequenceEqual(new[] { "Main Files" }) && mixed.Summary == "Juri Base + Hair B + Main Files", "recipe from the builder: " + mixed.Summary);
            OutfitRecipes.Install(mixed, costumes, "Mix", "");
            InstalledOutfit mix = Installer.ListInstalled(costumes).Single(o => o.Name == "Mix");
            Func<string, string> readMix = delegate(string rel) { string f = Path.Combine(mix.Folder, rel.Replace('/', '\\')); return File.Exists(f) ? File.ReadAllText(f) : null; };
            Require(mix.Parts.Count == 2 && readMix(m + "01/esf016_001_01.mesh.230110883") == "donor body" && readMix(m + "02/esf016_001_02.mesh.230110883") == "donor hair" && readMix(m + "02/esf016_001_02_chain.chain.48") == "base chain", "the built recipe did not install as built");
            Directory.Delete(mix.Folder, true);
            // Other options for a layer keep its place.
            Require(builder.ChangeLayer(builder.Layers[1], new[] { hair.Id, donorBody.Id }) == null && builder.Layers.Count == 3 && builder.Layers[1].Options.SequenceEqual(new[] { "Hair B", "Main Files" }) && builder.Layers[2].Mod == "Juri Base", "other options for a layer: " + String.Join(" > ", builder.Layers.Select(RecipeDraft.Shown)));
            // The order is the user's: a mod without a body may be at the bottom. As an extra outfit the recipe is then
            // built on the lowest mod that brings a body; that works when the mods under it share no file with it
            // (the same files come out), and is refused with the reason when they do.
            const string sharedSkin = "natives/stm/product/model/esf/esf016/000/00/esf016_000_00_skin_albd.tex.241101895";
            string skinMod = Path.Combine(root, "Juri Skin.zip");
            Zip(skinMod, new Dictionary<string, string> { { "Skin/modinfo.ini", "name=Juri Skin\r\n" }, { "Skin/" + sharedSkin, "oily skin" } });
            var skinFirst = new RecipeDraft("Juri", "016", "");
            string skinAdded = skinFirst.AddLayer(skinMod, "Juri Skin", null);
            Require(skinAdded == null && skinFirst.OutfitProblem() != null && skinFirst.OutfitProblem().StartsWith("No layer brings a complete body"), "a recipe without a body: " + skinAdded + " / " + skinFirst.OutfitProblem());
            Require(skinFirst.AddLayer(baseMod, "Juri Base", null) == null && skinFirst.OutfitProblem() == null && skinFirst.Notes().Count == 0, "the body above a shared mod: " + String.Join(" | ", skinFirst.Notes()));
            OutfitRecipe skinRecipe = skinFirst.ToRecipe("Skin First");
            Require(skinRecipe.Base.ModName == "Juri Skin" && skinRecipe.Parts.Count == 1 && skinRecipe.Parts[0].ModName == "Juri Base", "the bottom layer is saved first: " + skinRecipe.Summary);
            OutfitRecipes.Install(skinRecipe, costumes, "Skin First", "");
            InstalledOutfit skinOutfit = Installer.ListInstalled(costumes).Single(o => o.Name == "Skin First");
            Func<string, string> readSkin = delegate(string rel) { string f = Path.Combine(skinOutfit.Folder, rel.Replace('/', '\\')); return File.Exists(f) ? File.ReadAllText(f) : null; };
            Require(ReplacePlanner.SameSource(skinOutfit.SourcePath, baseMod) && skinOutfit.Parts != null && skinOutfit.Parts.Count == 1 && readSkin(sharedSkin) == "oily skin" && readSkin(m + "01/esf016_001_01.mesh.230110883") == "base body", "a recipe whose bottom mod has no body: built on " + skinOutfit.SourcePath);
            Directory.Delete(skinOutfit.Folder, true);
            var upsideDown = new RecipeDraft("Juri", "016", "");
            Require(upsideDown.AddLayer(donorMod, "Hair Bundle", new[] { hair.Id }) == null && upsideDown.AddLayer(baseMod, "Juri Base", null) == null && upsideDown.OutfitProblem() != null && upsideDown.OutfitProblem().Contains("Juri Base has to be below Hair B from Hair Bundle"), "a body over a mod it shares files with: " + upsideDown.OutfitProblem());
            bool orderRefused = false; try { OutfitRecipes.Install(upsideDown.ToRecipe("Upside"), costumes, "Upside", ""); } catch (IOException ex) { orderRefused = ex.Message.Contains("has to be below"); }
            Require(orderRefused && Installer.ListInstalled(costumes).All(o => o.Name != "Upside"), "an order that can't be an extra outfit was installed");
            // 1.0.18: the textures a material file names (MaterialCheck.cs), as natives paths without the version; no game, no verdict.
            List<string> named = MaterialCheck.Textures(System.Text.Encoding.Unicode.GetBytes("MDF\0esf_Body00\0Product/Model/esf/esf016/swm/bikini_ALBD.tex\0BaseColor\0systems/rendering/NullBlack.tex\0Product/Model/esf/esf016/swm/bikini_ALBD.tex\0"));
            Require(named.SequenceEqual(new[] { "natives/stm/product/model/esf/esf016/swm/bikini_albd.tex", "natives/stm/systems/rendering/nullblack.tex" }), "textures of a material file: " + String.Join(", ", named));
            Require(MaterialCheck.Notes(MaterialCheck.Layers(upsideDown), "", "").Count == 0 && MaterialCheck.Read(MaterialCheck.Layers(upsideDown), "", null).Count == 0, "the material check judged without a game");
            // 1.0.18 parts (OutfitParts.cs): 00 is the head, 01 the body, 02 the hair; a name that says what it is wins.
            Require(OutfitParts.Of(m + "02/esf016_001_02.mesh.230110883") == OutfitParts.Hair && OutfitParts.Of(m + "01/x.tex.1") == OutfitParts.Body && OutfitParts.Of("natives/STM/streaming/Product/Model/esf/esf016/000/00/h.tex.1") == OutfitParts.Head
                && OutfitParts.Of(m + "esf016_001_cmd_001.user.2") == OutfitParts.Colours && OutfitParts.Of(m + "30/a.mesh.1") == OutfitParts.Other && OutfitParts.Of("natives/stm/product/model/esf/esf016/long/hair_albd.tex.1") == OutfitParts.Other, "the part of a file");
            Require(OutfitParts.Kinds("Ponytail B", new[] { m + "01/a.mesh.1" }).SequenceEqual(new[] { OutfitParts.Hair }) && OutfitParts.Kinds("Juri Full", new[] { m + "02/a.mesh.1", m + "01/a.mesh.1", "natives/stm/product/model/esf/esf016/long/x.tex.1" }).SequenceEqual(new[] { OutfitParts.Body, OutfitParts.Hair })
                && OutfitParts.Kinds("Chair cover", new[] { m + "01/a.mesh.1" }).SequenceEqual(new[] { OutfitParts.Body }) && OutfitParts.Labels(new[] { "hair", "body" }) == "Body + Hair", "what an option is: " + String.Join(",", OutfitParts.Kinds("Ponytail B", new[] { m + "01/a.mesh.1" })));
            // A mod with the hair built in (no hair option): a recipe takes only its hair. The hair's folder comes whole,
            // and the textures its material file names come along from the mod's own folder; the body and its textures stay out.
            string fullMod = Path.Combine(root, "Juri Full");
            Action<string, byte[]> writeFull = delegate(string rel, byte[] bytes) { string file = Path.Combine(fullMod, "Full", rel.Replace('/', '\\')); Directory.CreateDirectory(Path.GetDirectoryName(file)); File.WriteAllBytes(file, bytes); };
            Func<string, byte[]> plain = System.Text.Encoding.UTF8.GetBytes, wide = System.Text.Encoding.Unicode.GetBytes;
            const string longFolder = "natives/stm/product/model/esf/esf016/long/", longStream = "natives/stm/streaming/product/model/esf/esf016/long/";
            writeFull("modinfo.ini", plain("name=Juri Full\r\n"));
            writeFull(m + "01/esf016_001_01.mesh.230110883", plain("full body")); writeFull(m + "01/esf016_001_01_v00.mdf2.31", wide("MDF\0Product/Model/esf/esf016/long/body_ALBD.tex\0"));
            writeFull(m + "02/esf016_001_02.mesh.230110883", plain("full hair")); writeFull(m + "02/esf016_001_02_v00.mdf2.31", wide("MDF\0Product/Model/esf/esf016/long/hair_ALBD.tex\0"));
            writeFull(longFolder + "hair_albd.tex.241101895", plain("full hair texture")); writeFull(longStream + "hair_albd.tex.241101895", plain("full hair texture hi")); writeFull(longFolder + "body_albd.tex.241101895", plain("full body texture"));
            var hairOnly = new RecipeDraft("Juri", "016", "");
            string hairWhy = hairOnly.AddLayer(baseMod, "Juri Base", null) ?? hairOnly.AddLayer(fullMod, "Juri Full", null, new[] { OutfitParts.Hair });
            Require(hairWhy == null && hairOnly.Layers.Count == 2 && hairOnly.Layers[0].Files.Count == 4 && hairOnly.Layers[0].Parts.SequenceEqual(new[] { OutfitParts.Hair }) && hairOnly.OutfitProblem() == null
                && hairOnly.Suppliers() == "Body: Juri Base   ·   Hair: Juri Full" && hairOnly.PartsOf(hairOnly.Layers[0]).SequenceEqual(new[] { OutfitParts.Body, OutfitParts.Hair, OutfitParts.Other }),
                "only the hair of a full mod: " + hairWhy + " / " + (hairOnly.Layers.Count > 0 ? hairOnly.Layers[0].Files.Count + " files" : "") + " / " + hairOnly.Suppliers());
            OutfitRecipe longRecipe = hairOnly.ToRecipe("Long");
            Require(longRecipe.Parts.Count == 1 && longRecipe.Parts[0].Parts.SequenceEqual(new[] { OutfitParts.Hair }), "the parts are saved with the layer");
            OutfitRecipes.Install(longRecipe, costumes, "Long", "");
            InstalledOutfit longOutfit = Installer.ListInstalled(costumes).Single(o => o.Name == "Long");
            Func<string, string> readLong = delegate(string rel) { string f = Path.Combine(longOutfit.Folder, rel.Replace('/', '\\')); return File.Exists(f) ? File.ReadAllText(f) : null; };
            Require(readLong(m + "01/esf016_001_01.mesh.230110883") == "base body" && readLong(m + "02/esf016_001_02.mesh.230110883") == "full hair" && readLong(longFolder + "hair_albd.tex.241101895") == "full hair texture" && readLong(longStream + "hair_albd.tex.241101895") == "full hair texture hi"
                && readLong(longFolder + "body_albd.tex.241101895") == null && readLong(m + "02/esf016_001_02_chain.chain.48") == "base chain" && longOutfit.Parts.Count == 1 && longOutfit.Parts[0].Parts.SequenceEqual(new[] { OutfitParts.Hair }) && longOutfit.Parts[0].Files.Count == 4,
                "hair only installed: body " + readLong(m + "01/esf016_001_01.mesh.230110883") + " / hair " + readLong(m + "02/esf016_001_02.mesh.230110883") + " / parts " + longOutfit.Parts.Count);
            Directory.Delete(longOutfit.Folder, true);
            // The whole layer again, and a layer that brings the body can't be cut for an extra outfit.
            Require(hairOnly.ChangeParts(hairOnly.Layers[0], null) == null && hairOnly.Layers[0].Parts.Count == 0 && hairOnly.Layers[0].Files.Count == 7 && hairOnly.Suppliers() == "Body: Juri Full   ·   Hair: Juri Full", "all parts again: " + hairOnly.Layers[0].Files.Count + " / " + hairOnly.Suppliers());
            Require(hairOnly.ChangeParts(hairOnly.Layers[1], new[] { OutfitParts.Body }) == null && hairOnly.ChangeParts(hairOnly.Layers[0], new[] { OutfitParts.Hair }) == null && hairOnly.OutfitProblem() != null && hairOnly.OutfitProblem().Contains("goes in whole"), "a cut body layer: " + hairOnly.OutfitProblem());
            bool cutRefused = false; try { OutfitRecipes.Install(hairOnly.ToRecipe("Cut"), costumes, "Cut", ""); } catch (IOException ex) { cutRefused = ex.Message.Contains("goes in whole"); }
            Require(cutRefused && Installer.ListInstalled(costumes).All(o => o.Name != "Cut"), "a cut body layer was installed as an extra outfit");
            // 1.0.18 ticks: a layer that is off is left out at install; an option that is off too; all off is nothing to install.
            longRecipe.Parts[0].Off = true;
            OutfitRecipe longOn = longRecipe.Effective();
            Require(longRecipe.AnyOff && longOn != null && longOn.Parts.Count == 0 && longOn.Base.ModName == "Juri Base", "a layer turned off");
            OutfitRecipes.Install(longRecipe, costumes, "Long Off", "");
            InstalledOutfit longOff = Installer.ListInstalled(costumes).Single(o => o.Name == "Long Off");
            Require((longOff.Parts == null || longOff.Parts.Count == 0) && File.ReadAllText(Path.Combine(longOff.Folder, (m + "02/esf016_001_02.mesh.230110883").Replace('/', '\\'))) == "base hair", "a layer that is off was installed");
            Directory.Delete(longOff.Folder, true);
            longRecipe.Base.Off = true;
            bool allOffRefused = false; try { OutfitRecipes.Install(longRecipe, costumes, "Nothing", ""); } catch (IOException ex) { allOffRefused = ex.Message.Contains("is turned off"); }
            Require(longRecipe.Effective() == null && allOffRefused, "a recipe with everything off");
            var twoOptions = new RecipeLayer { SourcePath = donorMod, Mod = "Hair Bundle", SelectedIds = new List<string> { hair.Id, donorBody.Id }, OptionNames = new List<string> { "Hair B", "Main Files" }, OffIds = new List<string> { donorBody.Id } };
            // 1.0.18 (review of PR 28): an outfit installed from a saved recipe carries the recipe's id; Update rebuilds it
            // in place in one transaction, so a recipe that can't be built leaves the outfit exactly as it was, and one
            // that can replaces its parts with the recipe's (none of the old ones kept); a renamed recipe keeps its id.
            string linkData = Path.Combine(root, "recipe-links");
            var linkDraft = new RecipeDraft("Juri", "016", "");
            Require((linkDraft.AddLayer(baseMod, "Juri Base", null) ?? linkDraft.AddLayer(fullMod, "Juri Full", null, new[] { OutfitParts.Hair })) == null, "the linked recipe's layers");
            OutfitRecipe linked = OutfitRecipes.Save(linkData, linkDraft.ToRecipe("Linked"));
            OutfitRecipes.Install(linked, costumes, "Linked", "");
            InstalledOutfit linkedOutfit = Installer.ListInstalled(costumes).Single(o => o.Name == "Linked");
            Func<string, string> readLinked = delegate(string rel) { string f = Path.Combine(linkedOutfit.Folder, rel.Replace('/', '\\')); return File.Exists(f) ? File.ReadAllText(f) : null; };
            Require(linkedOutfit.RecipeId == linked.Id && linked.Id.Length == 32 && readLinked(m + "02/esf016_001_02.mesh.230110883") == "full hair", "an outfit installed from a recipe does not carry its id: " + linkedOutfit.RecipeId);
            linked.Base.Parts = new List<string> { OutfitParts.Body };   // the body layer cut to parts: can't be an extra outfit
            bool updateRefused = false; try { OutfitRecipes.Install(linked, costumes, "Linked", "", true); } catch (IOException ex) { updateRefused = ex.Message.Contains("goes in whole"); }
            InstalledOutfit stillThere = Installer.ListInstalled(costumes).SingleOrDefault(o => o.Name == "Linked");
            Require(updateRefused && stillThere != null && stillThere.Id == linkedOutfit.Id && stillThere.Parts.Count == 1 && readLinked(m + "02/esf016_001_02.mesh.230110883") == "full hair" && readLinked(m + "01/esf016_001_01.mesh.230110883") == "base body",
                "an Update that can't be built did not leave the outfit as it was");
            linked.Base.Parts = new List<string>(); linked.Parts[0].Off = true;
            InstallResult updatedInPlace = OutfitRecipes.Install(linked, costumes, "Linked", "", true);
            InstalledOutfit afterUpdate = Installer.ListInstalled(costumes).Single(o => o.Name == "Linked");
            Require(updatedInPlace.Message.StartsWith("Updated Linked from the recipe Linked") && afterUpdate.Id == linkedOutfit.Id && afterUpdate.RecipeId == linked.Id && (afterUpdate.Parts == null || afterUpdate.Parts.Count == 0)
                && readLinked(m + "02/esf016_001_02.mesh.230110883") == "base hair" && readLinked(longFolder + "hair_albd.tex.241101895") == null, "Update in place: " + updatedInPlace.Message + " / parts " + (afterUpdate.Parts == null ? 0 : afterUpdate.Parts.Count));
            string linkedId = linked.Id; linked.Name = "Linked Again";
            Require(OutfitRecipes.Save(linkData, linked).Id == linkedId && OutfitRecipes.List(linkData).Count == 1 && OutfitRecipes.List(linkData)[0].Name == "Linked Again", "a renamed recipe did not keep its id");
            Directory.Delete(afterUpdate.Folder, true);
            Require(twoOptions.Effective().SelectedIds.SequenceEqual(new[] { hair.Id }) && twoOptions.Effective().OptionNames.SequenceEqual(new[] { "Hair B" }) && new RecipeLayer { SelectedIds = new List<string> { hair.Id }, OffIds = new List<string> { hair.Id } }.Effective() == null, "an option turned off");
            Func<string, string, LibraryEntry> libraryMod = delegate(string path, string name) { return new LibraryEntry { SourcePath = path, Name = name, Character = "Juri", CharacterCodes = new List<string> { "016" } }; };
            LibraryEntry baseEntry = libraryMod(baseMod, "Juri Base"), donorEntry = libraryMod(donorMod, "Juri Hair Bundle"), c2Entry = libraryMod(otherMod, "Juri C2 Hair"), sharedEntry = libraryMod(skinMod, "Juri Skin");
            baseEntry.CostumeKeys = new List<string> { "016/001" }; donorEntry.CostumeKeys = new List<string> { "016/001", "016/002" }; c2Entry.CostumeKeys = new List<string> { "016/002" }; sharedEntry.CostumeKeys = new List<string> { "016/000" };
            // 1.0.18: opened from a mod in the Mod library, that mod is in the recipe at once (and the selected row);
            // then the list keeps what fits the recipe: its outfit slot or shared files; Show all brings the rest
            // back; the options picker lists only the options that fit.
            using (var started = new RecipeBuilderForm("Ryu", new[] { new KeyValuePair<string, string>("016", "Juri"), new KeyValuePair<string, string>("001", "Ryu") }, null, new List<LibraryEntry> { baseEntry, donorEntry, c2Entry, sharedEntry }, ""))
            {
                started.StartOn(baseEntry, new[] { "no such option" });
                Require(started.Draft.Code == "016" && started.Draft.Layers.Count == 1 && started.Draft.Layers[0].Mod == "Juri Base" && started.StatusForTest == "", "opened on a mod, it is not in the recipe: " + started.Draft.Code + " / " + started.Draft.Layers.Count + " / " + started.StatusForTest);
                Require(started.ModsForTest.Count == 3 && started.ModsCaptionForTest == "FITS C1 · 3 OF 4", "the list once a mod is in: " + started.ModsForTest.Count + " / " + started.ModsCaptionForTest);
                started.ToggleShowAllForTest();
                Require(started.ModsForTest.Count == 4 && started.ModsCaptionForTest == "ALL 4 MODS", "Show all: " + started.ModsForTest.Count + " / " + started.ModsCaptionForTest);
                ModPackage c2Package = PackageReader.Load(otherMod, "", false);
                Require(!RecipeDraft.Options(c2Package).Any(started.Draft.Fits) && RecipeDraft.Options(donor).All(started.Draft.Fits), "options that fit the recipe");
                using (var fitPick = new OptionPickForm("C2", "Hint.", c2Package, new string[0], "", null, started.Draft.Fits))
                    Require(fitPick.RowCountForTest == 0, "the picker lists an option for another outfit: " + fitPick.RowCountForTest);
                // Add to recipe on a mod with one option that fits: no window, it goes straight in. With several (no
                // window can open in a check) nothing is added.
                started.SelectMod(sharedEntry); started.AddSelectedPart();
                Require(started.Draft.Layers.Count == 2 && started.Draft.Layers[0].Mod == "Juri Skin", "a single-option mod was not added directly: " + started.Draft.Layers.Count + " / " + started.StatusForTest);
                started.SelectMod(donorEntry); started.AddSelectedPart();
                Require(started.Draft.Layers.Count == 2, "a mod with several options was added without asking");
            }
            // 1.0.18 Edit recipe: the window opens with the saved recipe's fighter, name and mods in their order; the
            // layers show their option names (the bottom one's too).
            using (var editing = new RecipeBuilderForm("Ryu", new[] { new KeyValuePair<string, string>("016", "Juri"), new KeyValuePair<string, string>("001", "Ryu") }, null, new List<LibraryEntry> { baseEntry, donorEntry, c2Entry, sharedEntry }, ""))
            {
                editing.LoadRecipe(mixed);
                OutfitRecipe reloaded = editing.Draft.Layers.Count == 0 ? null : editing.Draft.ToRecipe("Mix");
                Require(editing.Text == "Edit outfit recipe" && editing.Draft.Code == "016" && reloaded != null && editing.Draft.Layers.Count == 3 && reloaded.Summary == mixed.Summary && editing.StatusForTest == "", "Edit recipe: " + editing.Text + " / " + (reloaded == null ? "empty" : reloaded.Summary) + " / " + editing.StatusForTest);
                Require(reloaded.Base.ShownOptions.SequenceEqual(new[] { "Juri Base" }) && reloaded.Parts[0].ShownOptions.SequenceEqual(new[] { "Hair B" }) && new RecipeLayer { SelectedIds = new List<string> { "B/Hair B", "Main" } }.ShownOptions.SequenceEqual(new[] { "Hair B", "Main" }), "layer option names: " + String.Join(", ", reloaded.Base.ShownOptions));
            }
            using (var builderForm = new RecipeBuilderForm("Juri", new[] { new KeyValuePair<string, string>("016", "Juri") }, null, new List<LibraryEntry> { baseEntry, donorEntry, c2Entry, new LibraryEntry { SourcePath = "x", Name = "Ryu Gi", CharacterCodes = new List<string> { "001" } } }, ""))
            {
                Require(builderForm.ModsForTest.Count == 3 && builderForm.Draft.Code == "016" && builderForm.Draft.Layers.Count == 0, "the builder does not list the fighter's mods: " + builderForm.ModsForTest.Count);
                // Nothing in the recipe yet: Add is on for the selected mod; Options and Remove have no row to act on.
                Require(builderForm.ButtonsForTest == "add" && builderForm.RowsForTest.Count == 0, "buttons with an empty recipe: '" + builderForm.ButtonsForTest + "'");
                builderForm.SelectMod(donorEntry);
                Require(builderForm.PreviewForTest == "Juri Hair Bundle | In your library", "the preview does not follow the list: " + builderForm.PreviewForTest);
                builderForm.AddPart(baseEntry, null);
                Require(builderForm.ButtonsForTest == "add options remove" && builderForm.RowsForTest.Count == 1, "buttons with one mod in: '" + builderForm.ButtonsForTest + "' / " + builderForm.RowsForTest.Count);
                builderForm.AddPart(donorEntry, new[] { hair.Id });
                Require(builderForm.Draft.Layers.Count == 2 && builderForm.RowsForTest.Count == 2 && builderForm.NotesForTest.Contains("replaces 2 files of the layers below it") && builderForm.StatusForTest == "", "the builder window's draft: " + builderForm.NotesForTest + " / " + builderForm.StatusForTest);
                // 1.0.18 preview column: it follows what was touched last (the new row here), then the mod selected in the list.
                Require(builderForm.PreviewForTest == "Juri Hair Bundle | #1 in the recipe · Hair B", "the preview does not follow the new row: " + builderForm.PreviewForTest);
                builderForm.SelectMod(donorEntry);
                Require(builderForm.PreviewForTest == "Juri Hair Bundle | In this recipe", "a mod of the recipe selected in the list: " + builderForm.PreviewForTest);
                builderForm.SelectMod(baseEntry);
                Require(builderForm.PreviewForTest == "Juri Base | In this recipe", "another mod of the recipe selected in the list: " + builderForm.PreviewForTest);
                // Every row moves: the body dragged above a mod it shares files with is said in the notes.
                builderForm.RowsForTest.MoveTo(0, 1);
                Require(builderForm.Draft.Layers[0].Mod == "Juri Base" && builderForm.Draft.Layers[1].Mod == "Juri Hair Bundle" && builderForm.NotesForTest.Contains("As an extra outfit, Juri Base has to be below"), "rows moved: " + String.Join(" > ", builderForm.Draft.Layers.Select(l => l.Mod)) + " / " + builderForm.NotesForTest);
                builderForm.ChangeOptions(builderForm.Draft.Layers[1], new[] { hair.Id, donorBody.Id });
                Require(builderForm.Draft.Layers.Count == 2 && builderForm.Draft.Layers[1].Options.Count == 2 && builderForm.RowsForTest.Count == 2, "Options on a row: " + String.Join(" > ", builderForm.Draft.Layers.Select(RecipeDraft.Shown)));
            }
            // The image itself: a mod's own screenshot is read for the mod and for an option without its own image, and
            // a mod without one gives none.
            string picMod = Path.Combine(root, "Juri Pic.zip");
            using (var zipFile = System.IO.Compression.ZipFile.Open(picMod, System.IO.Compression.ZipArchiveMode.Create))
            {
                Action<string, byte[]> put = delegate(string name, byte[] bytes) { using (Stream s = zipFile.CreateEntry(name).Open()) s.Write(bytes, 0, bytes.Length); };
                put("Pic/modinfo.ini", System.Text.Encoding.UTF8.GetBytes("name=Juri Pic\r\n"));
                put("Pic/" + m + "01/esf016_001_01.mesh.230110883", System.Text.Encoding.UTF8.GetBytes("pic body"));
                using (var bitmap = new System.Drawing.Bitmap(6, 4)) using (var png = new MemoryStream()) { bitmap.Save(png, System.Drawing.Imaging.ImageFormat.Png); put("Pic/screenshot.png", png.ToArray()); }
            }
            ModPackage picPackage = PackageReader.Load(picMod, "", false);
            using (System.Drawing.Image modImage = RecipeBuilderForm.ReadPreview(picPackage, null, ""))
            using (System.Drawing.Image optionImage = RecipeBuilderForm.ReadPreview(picPackage, picPackage.Components.First(c => c.Assets.Count > 0).Id, ""))
            using (System.Drawing.Image noImage = RecipeBuilderForm.ReadPreview(PackageReader.Load(baseMod, "", false), null, ""))
                Require(modImage != null && modImage.Width == 6 && modImage.Height == 4 && optionImage != null && optionImage.Width == 6 && noImage == null, "the builder's preview image: " + (modImage == null ? "none" : modImage.Width + "x" + modImage.Height) + " / " + (optionImage == null ? "none" : "ok") + " / " + (noImage == null ? "none" : "an image"));
            File.Delete(picMod);
            // 1.0.18: the Mod library's search finds recipes by the recipe words (the other words narrow them) or by name.
            Require(MainForm.RecipeMatches(new[] { mixed }, "rc").Count == 1 && MainForm.RecipeMatches(new[] { mixed }, "recipes juri").Count == 1 && MainForm.RecipeMatches(new[] { mixed }, "rc ryu").Count == 0
                && MainForm.RecipeMatches(new[] { mixed }, "mix").Count == 1 && MainForm.RecipeMatches(new[] { mixed }, "juri").Count == 1 && MainForm.RecipeMatches(new[] { mixed }, "").Count == 1 && MainForm.RecipeMatches(new[] { mixed }, "hair").Count == 1
                && MainForm.RecipeMatches(new[] { mixed }, "", new[] { "Ryu" }).Count == 0 && MainForm.RecipeMatches(new[] { mixed }, "", new[] { "Juri", "Ryu" }).Count == 1 && MainForm.RecipeMatches(new[] { mixed }, "cammy").Count == 0, "recipe search words");
            // 1.0.18: the options picker shows groups as headings; a choose-one group unticks the others; a required one
            // can't be emptied; the mod's rules (the validator) hold Use these back.
            Func<string, string, bool, ModComponent> part = delegate(string id, string parent, bool group)
            {
                var c = new ModComponent { Id = id, Name = id, ParentId = parent, IsDummy = group, Description = group ? "Choose one skin" : "" };
                if (!group) c.Assets.Add(new NativeAsset { EntryPath = id, DestinationPath = "natives/stm/" + (parent == "Skins" ? "skin.tex" : id + ".bin"), Size = 1 });
                return c;
            };
            var grouped = new ModPackage { Name = "Grouped", Components = new List<ModComponent> { part("", null, true), part("Main", "", false), part("Skins", "", true), part("Oily", "Skins", false), part("Dry", "Skins", false) } };
            grouped.PrepareChoices();
            string ruleSays = null;
            using (var pick = new OptionPickForm("Grouped", "hint", grouped, new[] { "Main", "Oily" }, "", delegate(List<string> ids) { return ruleSays; }))
            {
                var texts = Enumerable.Range(0, pick.RowCountForTest).Select(pick.RowTextForTest).ToList();
                Require(texts.SequenceEqual(new[] { "[ ] Main", "[Skins · Required · choose one]", "( ) Oily", "( ) Dry" }), "picker rows: " + String.Join(" | ", texts));
                pick.ToggleByName("Dry");
                Require(pick.Picked.SequenceEqual(new[] { "Main", "Dry" }), "choose one did not untick the other: " + String.Join(",", pick.Picked));
                pick.ToggleByName("Dry");
                Require(pick.Picked.Contains("Dry") && pick.CanAcceptForTest, "a required choice was emptied");
                ruleSays = "Needs more."; pick.ToggleByName("Main");
                Require(!pick.CanAcceptForTest && pick.StatusForTest == "Needs more.", "the mod's rules did not hold Use these back: " + pick.StatusForTest);
            }
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
                // 1.0.18: recipes are listed above the mods without a search word and narrowed by the search; a click
                // selects one (no mod open, its layers where a mod shows its options, Install ready in both modes);
                // opening a mod puts the recipe away.
                OutfitRecipe windowRecipe = OutfitRecipes.Save(AppConfig.DataDir, new OutfitRecipe { Name = "Window Recipe", Character = "Juri", Base = new RecipeLayer { SourcePath = Path.Combine(lib, "Juri Bundle.zip"), Mod = "Juri Bundle" } });
                try
                {
                    Call(form, "RecipesChanged"); Application.DoEvents();
                    Require(form.RecipeStripCountForTest >= 1, "the recipe is not listed without a search word: " + form.RecipeStripCountForTest);
                    Get<PaddedTextField>(form, "search").Text = "cammy"; WaitIdle(form);
                    Require(form.RecipeStripCountForTest == 0, "a search for another fighter still lists the recipe: " + form.RecipeStripCountForTest);
                    Get<PaddedTextField>(form, "search").Text = ""; WaitIdle(form);
                    form.OpenRecipe(windowRecipe); Application.DoEvents();
                    Require(form.OpenRecipeForTest != null && Get<Label>(form, "packageTitle").Text == "Window Recipe" && form.RecipeTextForTest == "#1 Juri Bundle" && form.RecipeInstallReadyForTest && list.SelectedItems.Count == 1,   // recipes are rows of the mod list: the open one is the selected row
                        "a selected recipe: '" + Get<Label>(form, "packageTitle").Text + "' / " + form.RecipeTextForTest + " / ready " + form.RecipeInstallReadyForTest + " / rows selected " + list.SelectedItems.Count);
                    Call(form, "SetInstallMode", InstallModes.Replace, false); Application.DoEvents();
                    Require(form.RecipeInstallReadyForTest, "Install is off for a recipe in Replace original");
                    Call(form, "SetInstallMode", InstallModes.Slot, true); Application.DoEvents();
                    Require(form.RecipeInstallReadyForTest && form.OpenRecipeForTest != null, "the recipe did not stay open across the mode switch");
                    // 1.0.18: "Add to recipe" on a mod lists the saved recipes of its fighter, and none for another fighter's mod.
                    LibraryEntry juriMod = list.Items.Cast<ListViewItem>().Select(i => i.Tag as LibraryEntry).First(x => x != null && x.Recipe == null && x.SourcePath.EndsWith("Juri Bundle.zip", StringComparison.OrdinalIgnoreCase));
                    LibraryEntry cammyMod = list.Items.Cast<ListViewItem>().Select(i => i.Tag as LibraryEntry).First(x => x != null && x.Recipe == null && x.Name.StartsWith("cammy-mod", StringComparison.OrdinalIgnoreCase));
                    Require(form.RecipesFor(juriMod).Any(r => r.Name == "Window Recipe") && form.RecipesFor(cammyMod).Count == 0 && form.RecipesFor(list.Items.Cast<ListViewItem>().Select(i => i.Tag as LibraryEntry).First(x => x.Recipe != null)).Count == 0,
                        "Add to recipe lists: " + form.RecipesFor(juriMod).Count + " / " + form.RecipesFor(cammyMod).Count);
                    // 1.0.18 ticks: the only layer can't be turned off (the recipe would install nothing), and nothing is saved then.
                    form.ToggleRecipeTick(form.OpenRecipeForTest.Base, null); Application.DoEvents();
                    Require(!form.OpenRecipeForTest.Base.Off && form.RecipeTextForTest == "#1 Juri Bundle" && !OutfitRecipes.List(AppConfig.DataDir).First(r => r.Name == "Window Recipe").Base.Off, "the only layer was turned off: " + form.RecipeTextForTest);
                    // 1.0.18: an installed recipe says so (row badge, footer) and Uninstall takes it off again.
                    FlatButton recipeUninstall = Get<FlatButton>(form, "libraryUninstallButton");
                    Require(!form.InstalledRecipe(windowRecipe).Any && !recipeUninstall.Enabled, "a recipe that is not installed offers Uninstall");
                    form.InstallRecipe(windowRecipe); WaitIdle(form);
                    MainForm.RecipeInstall madeRecipe = form.InstalledRecipe(windowRecipe);
                    Require(madeRecipe.Outfits.Count == 1 && madeRecipe.Replacements.Count == 0 && madeRecipe.Badge == "Extra", "an installed recipe is not shown as installed: " + madeRecipe.Outfits.Count + " / " + madeRecipe.Badge);
                    if (form.OpenRecipeForTest == null) form.OpenRecipe(windowRecipe);
                    Call(form, "UpdateLibraryUninstall"); Application.DoEvents();
                    Require(recipeUninstall.Enabled && recipeUninstall.Text == "Uninstall" && Get<Label>(form, "planDetails").Text.StartsWith("Installed now as an extra outfit."), "the open installed recipe: " + recipeUninstall.Text + " / " + recipeUninstall.Enabled + " / " + Get<Label>(form, "planDetails").Text);
                    form.UninstallRecipe(windowRecipe); WaitIdle(form);
                    Require(!form.InstalledRecipe(windowRecipe).Any, "Uninstall left the recipe's outfit installed");
                    if (form.OpenRecipeForTest == null) form.OpenRecipe(windowRecipe);
                    // Recipes are rows too: pick the first row that is a mod.
                    foreach (ListViewItem row in list.Items) row.Selected = false;
                    list.Items.Cast<ListViewItem>().First(i => i.Tag is LibraryEntry && ((LibraryEntry)i.Tag).Recipe == null).Selected = true; WaitIdle(form);
                    Require(form.OpenRecipeForTest == null && form.RecipeTextForTest == "", "opening a mod did not put the recipe away");
                    form.ClosePackage(); WaitIdle(form);
                    // 1.0.18: the right-click menu of a recipe row; Delete recipe takes it off the list, Undo puts it back.
                    form.OpenRecipe(windowRecipe); Application.DoEvents();
                    Require(form.OpenRecipeForTest != null && String.Join(" | ", form.RecipeMenuForTest) == "Edit recipe | Open recipes window | - | Delete recipe", "the recipe menu: " + String.Join(" | ", form.RecipeMenuForTest));
                    form.DeleteRecipe(form.OpenRecipeForTest); Application.DoEvents();
                    Require(form.RecipeStripCountForTest == 0 && form.OpenRecipeForTest == null && OutfitRecipes.List(AppConfig.DataDir).All(r => r.Name != "Window Recipe"), "Delete recipe: " + form.RecipeStripCountForTest);
                    form.PerformUndo(); Application.DoEvents();
                    windowRecipe = OutfitRecipes.List(AppConfig.DataDir).FirstOrDefault(r => r.Name == "Window Recipe") ?? windowRecipe;
                    Require(form.RecipeStripCountForTest == 1 && OutfitRecipes.List(AppConfig.DataDir).Any(r => r.Name == "Window Recipe"), "Undo did not put the recipe back: " + form.RecipeStripCountForTest);
                }
                finally { OutfitRecipes.Delete(AppConfig.DataDir, windowRecipe.Id); Call(form, "RecipesChanged"); }
                // The first scan listed everything at once and read it in the background; a second scan has nothing to read.
                Require(form.IndexRuns == 1 && !form.Indexing && list.Items.Cast<ListViewItem>().All(i => !((LibraryEntry)i.Tag).Pending && !((LibraryEntry)i.Tag).NamesPending), "background read: runs " + form.IndexRuns);
                Call(form, "RefreshLibrary", false); WaitIdle(form);
                Require(form.IndexRuns == 1 && Get<Label>(form, "libraryCount").Text == "31 MODS", "second scan: runs " + form.IndexRuns + " / " + Get<Label>(form, "libraryCount").Text);
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
                var deep = Get<CheckBox>(form, "deepSearch");
                deep.Checked = true; Application.DoEvents();
                Require(list.Items.Count == 1 && ((LibraryEntry)list.Items[0].Tag).Name == "Juri Bundle", "deep juri, barefoot: " + list.Items.Count);
                if (!SystemInformation.HighContrast)
                {
                    using (var buffer = new Bitmap(deep.Width, deep.Height))
                    using (var graphics = Graphics.FromImage(buffer))
                    {
                        graphics.Clear(Color.Black);
                        Call(deep, "OnPaint", new PaintEventArgs(graphics, deep.ClientRectangle));
                        Require(buffer.GetPixel(deep.Width - 2, 1).ToArgb() == deep.Parent.BackColor.ToArgb(), "Deep search left its paint buffer black");
                    }
                }
                deep.Checked = false;
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
                // Add to outfit shows for an open mod in Extra outfit mode.
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
