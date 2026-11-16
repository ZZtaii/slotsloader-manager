using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

namespace SlotsInstaller
{
    // 1.0.19 --filter-check: colour packs (a mod without a model, at least half of whose outfit files are colour files) found from
    // the library index, the colour search words, and recipes sorted with the mods instead of in front of them.
    internal static partial class FilterChecks
    {
        private static string Cmd(string fighter, string outfit, int n) { return "natives/stm/product/model/esf/esf" + fighter + "/" + outfit + "/esf" + fighter + "_" + outfit + "_cmd_" + n.ToString("000") + ".user.2"; }
        private static LibraryEntry ColourEntry(string name, string character, string code, string outfit, int files, int colours, int models)
        {
            return new LibraryEntry { Name = name, Kind = "ZIP", OptionCount = 1, SourcePath = @"C:\fixture\filters\" + name + ".zip", Character = character, CharacterCodes = code == null ? new List<string>() : new List<string> { code }, CostumeKeys = code == null ? new List<string>() : new List<string> { code + "/" + outfit }, FileMix = files < 0 ? null : new List<int> { files, colours, models } };
        }
        // SortFixture with two colour packs (neither says "color" or "cmd"), an outfit with colour files (Juri Jacket), a
        // mod named Colors without any, and one named Cmd whose files are not known yet.
        private static List<LibraryEntry> ColourFixture()
        {
            var lib = SortFixture();
            lib.Single(e => e.Name == "Juri Jacket").FileMix = new List<int> { 30, 10, 1 };
            lib.Single(e => e.Name == "Cammy Barefoot").FileMix = new List<int> { 5, 0, 1 };
            LibraryEntry palette = ColourEntry("Juri Palette Set", "Juri", "016", "001", 10, 10, 0), tints = ColourEntry("Cammy Tints", "Cammy", "009", "002", 13, 10, 0);
            LibraryEntry nails = ColourEntry("Exclusive Colors and Nails", "Juri", "016", "001", 20, 0, 0), notes = ColourEntry("Cmd Tool Notes", "", null, null, -1, 0, 0);
            palette.AddedUtc = SortBase.AddDays(11); tints.AddedUtc = SortBase.AddDays(12); nails.AddedUtc = SortBase.AddDays(13); notes.AddedUtc = SortBase.AddDays(14);
            lib.AddRange(new[] { palette, tints, nails, notes });
            return lib;
        }

        private static void CheckColourPacks(string root)
        {
            // What is counted: files in fighter outfit folders, the colour files and the models among them.
            const string body = "natives/stm/product/model/esf/esf016/001/01/";
            List<int> pack = Library.CountFileMix(Enumerable.Range(1, 10).Select(i => "Juri Colours/" + Cmd("016", "001", i)).Concat(new[] { "Juri Colours/modinfo.ini", "screenshot.png" }));
            List<int> outfit = Library.CountFileMix(new[] { body + "esf016_001_01.mesh.230110883", body + "esf016_001_01_v00.mdf2.31", body + "body_albd.tex.241101895", "natives/stm/streaming/product/model/esf/esf016/001/01/body_albd.tex.241101895",
                Cmd("016", "001", 1), Cmd("016", "001", 2), "natives/stm/product/model/esf/esf016/001/esf016_001_cmd_ex_001.user.2", "natives/stm/product/gui/data/x.tex.241101895", "reframework/data/colours/esf016_c1.json" });
            List<int> back = Library.CountFileMix(new[] { @"Mod\natives\stm\product\model\esf\esf016\001\esf016_001_cmd_003.user.2" });
            Require(String.Join(",", pack) == "10,10,0" && String.Join(",", outfit) == "7,3,1" && String.Join(",", back) == "1,1,0" && String.Join(",", Library.CountFileMix(new[] { "reframework/autorun/x.lua" })) == "0,0,0",
                "file mix: " + String.Join(",", pack) + " / " + String.Join(",", outfit) + " / " + String.Join(",", back));
            // A colour pack: colour files, no model, and the colour files are at least half of its outfit files.
            Func<int, int, int, LibraryEntry> mix = delegate(int files, int colours, int models) { return ColourEntry("x", "Juri", "016", "001", files, colours, models); };
            Require(LibrarySearch.IsColourPack(mix(10, 10, 0)) && LibrarySearch.IsColourPack(mix(13, 10, 0)) && !LibrarySearch.IsColourPack(mix(30, 10, 0)) && !LibrarySearch.IsColourPack(mix(11, 10, 1)) && LibrarySearch.IsColourPack(mix(20, 10, 0)) && !LibrarySearch.IsColourPack(mix(21, 10, 0))
                && !LibrarySearch.IsColourPack(mix(0, 0, 0)) && !LibrarySearch.IsColourPack(mix(-1, 0, 0)) && !LibrarySearch.IsColourPack(null), "colour pack rule");
            Require(LibrarySearch.ColourRank(mix(10, 10, 0)) == 0 && LibrarySearch.ColourRank(mix(30, 10, 1)) == 1 && LibrarySearch.ColourRank(mix(5, 0, 1)) == 2 && LibrarySearch.ColourRank(mix(-1, 0, 0)) == 2, "colour rank");
            // The words.
            foreach (string word in new[] { "color pack", "Colour  Packs", "colors", "colour", "color bundle", "colours bundles" }) Require(LibrarySearch.ColourKeyword(word) == LibrarySearch.ColourPack, "not a colour pack word: " + word);
            foreach (string word in new[] { "cmd", "CMDs" }) Require(LibrarySearch.ColourKeyword(word) == LibrarySearch.ColourCmd, "not a cmd word: " + word);
            foreach (string word in new[] { "colo", "pack", "color packing", "cmdx", "bundle", "" }) Require(LibrarySearch.ColourKeyword(word) == null, "a colour word: " + word);
            // Search: the colour words find the mods by what is in them, OR-ed with the name; fighters and commas combine.
            var lib = ColourFixture();
            Require(Find(lib, "color pack") == "Cammy Tints, Juri Palette Set" && Find(lib, "colour bundles") == Find(lib, "color pack"), "color pack: " + Find(lib, "color pack"));
            Require(Find(lib, "colors") == "Cammy Tints, Exclusive Colors and Nails, Juri Palette Set", "colors: " + Find(lib, "colors"));
            Require(Find(lib, "cmd") == "Cammy Tints, Cmd Tool Notes, Juri Jacket, Juri Palette Set", "cmd: " + Find(lib, "cmd"));
            Require(Find(lib, "juri color pack") == "Juri Palette Set" && Find(lib, "juri, cmd") == "Juri Jacket, Juri Palette Set" && Find(lib, "cammy cmds") == "Cammy Tints" && Find(lib, "cammy, colour pack") == "Cammy Tints",
                "combined: " + Find(lib, "juri color pack") + " / " + Find(lib, "juri, cmd") + " / " + Find(lib, "cammy cmds") + " / " + Find(lib, "cammy, colour pack"));
            Require(Find(lib, "jacket") == "Juri Jacket" && Find(lib, "cammy") == "Cammy Barefoot, Cammy Boots, Cammy Scripted, Cammy Tints", "other searches changed: " + Find(lib, "jacket") + " / " + Find(lib, "cammy"));
            Func<string, string> colour = delegate(string text) { return LibrarySearch.Parse(text, MainForm.SearchCharacterTerm).Colour ?? "none"; };
            Require(colour("juri color pack") == "pack" && colour("juri, cmd") == "cmd" && colour("cmds") == "cmd" && colour("juri jacket") == "none" && colour("") == "none", "the query's colour word: " + colour("juri color pack") + " / " + colour("juri, cmd") + " / " + colour("juri jacket"));
            Require(MainForm.LibraryRowLabel(lib.Single(e => e.Name == "Juri Palette Set"), null) == "C1 \u00b7 Colour pack" && MainForm.LibraryRowLabel(lib.Single(e => e.Name == "Juri Jacket"), null) == "C1", "row label: " + MainForm.LibraryRowLabel(lib.Single(e => e.Name == "Juri Palette Set"), null));
        }

        // The index: a scan stores the file mix; an index from before it keeps every identity, gives a record without
        // outfit files its zeros without a read, queues the others for the background read, and the next start queues none.
        private static void CheckColourIndex(string root)
        {
            string lib = Path.Combine(root, "colour-index"); Directory.CreateDirectory(lib);
            string pack = Path.Combine(lib, "Juri Palette Set.zip"), outfit = Path.Combine(lib, "Outfit.zip"), script = Path.Combine(lib, "Hitbox Viewer.zip");
            Zip(pack, Enumerable.Range(1, 10).Select(i => "Set A/" + Cmd("016", "001", i)).Concat(new[] { "Set A/modinfo.ini" }).ToArray());
            Zip(outfit, Mesh, Cmd("009", "001", 1), Cmd("009", "001", 2));
            Zip(script, "reframework/autorun/hitbox.lua");
            string previous = Library.IdentityCacheDirectory, cache = Path.Combine(root, "colour-index-cache");
            try
            {
                Library.IdentityCacheDirectory = cache;
                List<LibraryEntry> scanned = Library.Scan(new[] { lib }, "");
                Func<List<LibraryEntry>, string, LibraryEntry> at = delegate(List<LibraryEntry> list, string path) { return list.Single(e => String.Equals(e.SourcePath, path, StringComparison.OrdinalIgnoreCase)); };
                Func<LibraryEntry, string> text = delegate(LibraryEntry e) { return e.FileMix == null ? "null" : String.Join(",", e.FileMix); };
                Require(text(at(scanned, pack)) == "10,10,0" && text(at(scanned, outfit)) == "3,2,1" && text(at(scanned, script)) == "0,0,0" && LibrarySearch.IsColourPack(at(scanned, pack)) && !LibrarySearch.IsColourPack(at(scanned, outfit)) && LibrarySearch.HasColourFiles(at(scanned, outfit)),
                    "fresh scan: " + String.Join("; ", scanned.Select(e => e.Name + "=" + text(e))));
                string file = Path.Combine(cache, Library.CacheFilename);
                LibraryIdentityCache records = Json.Read<LibraryIdentityCache>(file);
                Require(records.Records.All(r => r.FileMix != null && r.FileMix.Count == 3), "the file mix is not stored in the index");
                // The records as an older Manager wrote them, read as on a restart; the archives are locked.
                foreach (LibraryIdentityRecord r in records.Records) r.FileMix = null;
                Json.Write(file, records);
                Library.IdentityCacheDirectory = Path.Combine(root, "colour-index-other"); Library.IdentityCacheDirectory = cache;
                List<LibraryEntry> pending, names, quick; int misses = Library.CacheMisses;
                var locks = new[] { pack, outfit, script }.Select(p => new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.None)).ToList();
                try { quick = Library.ScanQuick(new[] { lib }, "", out pending, out names); }
                finally { foreach (FileStream s in locks) s.Dispose(); }
                LibraryEntry qPack = at(quick, pack), qOutfit = at(quick, outfit), qScript = at(quick, script);
                Require(Library.CacheMisses == misses && quick.All(e => !e.Pending && e.Detection != "Error" && e.OptionCount >= 0), "the quick listing lost identity: " + String.Join("; ", quick.Select(e => e.Name + " " + e.Detection)));
                Require(qPack.FileMix == null && qOutfit.FileMix == null && pending.Contains(qPack) && pending.Contains(qOutfit) && text(qScript) == "0,0,0" && !pending.Contains(qScript), "old records: " + text(qPack) + " / " + text(qOutfit) + " / " + text(qScript) + ", " + pending.Count + " queued");
                Require(!LibrarySearch.IsColourPack(qPack) && !LibrarySearch.HasColourFiles(qOutfit), "a mod whose files are not known yet was called a colour pack");
                foreach (LibraryEntry e in new[] { qPack, qOutfit }) { LibraryEntry c = Library.IndexCopy(e); Library.IndexEntry(c, ""); Library.TakeIdentity(e, c); }
                Library.SaveIndex();
                Require(text(qPack) == "10,10,0" && text(qOutfit) == "3,2,1" && Library.CacheMisses == misses, "background read: " + text(qPack) + " / " + text(qOutfit) + ", misses " + (Library.CacheMisses - misses));
                Library.IdentityCacheDirectory = Path.Combine(root, "colour-index-other"); Library.IdentityCacheDirectory = cache;
                quick = Library.ScanQuick(new[] { lib }, "", out pending, out names);
                Require(pending.Count == 0 && text(at(quick, pack)) == "10,10,0" && text(at(quick, outfit)) == "3,2,1", "after the background read: " + pending.Count + " queued");
            }
            finally { Library.IdentityCacheDirectory = previous; }
        }

        // In the window: a colour word lists the colour packs first, then the other mods with colour files, then the
        // name matches, in every sort; and a recipe is sorted with the mods (a recipe word still lists recipes first).
        private static void CheckColourAndRecipeOrder(string root)
        {
            MethodInfo recipesChanged = typeof(MainForm).GetMethod("RecipesChanged", PrivateInstance);
            using (Window w = Open(root, null, new Size(1460, 920), ColourFixture()))
            {
                MainForm form = w.Form; PaddedTextField search = Get<PaddedTextField>(form, "search");
                var chips = SortChips(form);
                Func<string> shown = delegate { return String.Join(", ", Shown(form)); };
                search.Text = "cmd"; Apply(form);
                Require(shown() == "Cammy Tints, Juri Palette Set, Juri Jacket, Cmd Tool Notes", "cmd, Installed \u2193: " + shown());
                chips[LibrarySortKey.Added].PerformClick(); Application.DoEvents();
                Require(shown() == "Cammy Tints, Juri Palette Set, Juri Jacket, Cmd Tool Notes", "cmd, Added \u2193: " + shown());
                chips[LibrarySortKey.Added].PerformClick(); Application.DoEvents();
                Require(shown() == "Juri Palette Set, Cammy Tints, Juri Jacket, Cmd Tool Notes", "cmd, Added \u2191: " + shown());
                chips[LibrarySortKey.Installed].PerformClick(); Application.DoEvents();
                search.Text = "colors"; Apply(form);
                Require(shown() == "Cammy Tints, Juri Palette Set, Exclusive Colors and Nails", "colors: " + shown());
                search.Text = "juri color pack"; Apply(form);
                Require(shown() == "Juri Palette Set", "juri color pack: " + shown());
                search.Text = ""; Apply(form);
            }
            var lib = SortFixture();
            var named = new LibraryEntry { Name = "Juri Outfits Pack", Kind = "ZIP", OptionCount = 1, SourcePath = @"C:\fixture\filters\Juri Outfits Pack.zip", Character = "Juri", CharacterCodes = new List<string> { "016" }, CostumeKeys = new List<string> { "016/002" }, AddedUtc = SortBase.AddDays(-1) };
            lib.Add(named);
            named.Detection = "Native"; lib.Single(e => e.Name == "Juri Jacket").Detection = "Native";   // as a scan marks a mod with one fighter's files
            OutfitRecipe recipe = OutfitRecipes.Save(AppConfig.DataDir, new OutfitRecipe { Name = "Zeta Recipe", Character = "Juri", CreatedUtc = SortBase.AddDays(4).AddHours(12).ToString("o"), Base = new RecipeLayer { SourcePath = lib.Single(e => e.Name == "Juri Jacket").SourcePath, Mod = "Juri Jacket" } });
            try
            {
                using (Window w = Open(root, null, new Size(1460, 920), lib))
                {
                    MainForm form = w.Form; PaddedTextField search = Get<PaddedTextField>(form, "search"); FilterPanel panel = Get<FilterPanel>(form, "filterPanel");
                    var chips = SortChips(form);
                    Func<string> shown = delegate { return String.Join(", ", Shown(form)); };
                    SortUsage(form, lib); recipesChanged.Invoke(form, null); Apply(form);
                    // Installed first, then names A to Z: the recipe (not installed) is among the names, not on top.
                    Require(shown() == "Cammy Boots, Select Icons, Cammy Barefoot, Cammy Scripted, Frame Meter, Hitbox Viewer, Juri Jacket, Juri Outfits Pack, Lighting Overhaul, Louder Impact, Ranger Hut Night, Zeta Recipe", "Installed \u2193 with a recipe: " + shown());
                    // Added: by when the recipe was made, between the mods that arrived before and after it.
                    chips[LibrarySortKey.Added].PerformClick(); Application.DoEvents();
                    Require(shown() == "Cammy Scripted, Ranger Hut Night, Cammy Boots, Louder Impact, Select Icons, Zeta Recipe, Hitbox Viewer, Juri Jacket, Frame Meter, Cammy Barefoot, Lighting Overhaul, Juri Outfits Pack", "Added \u2193 with a recipe: " + shown());
                    chips[LibrarySortKey.Added].PerformClick(); Application.DoEvents();
                    Require(shown() == "Juri Outfits Pack, Lighting Overhaul, Cammy Barefoot, Frame Meter, Hitbox Viewer, Juri Jacket, Zeta Recipe, Select Icons, Louder Impact, Cammy Boots, Ranger Hut Night, Cammy Scripted", "Added \u2191 with a recipe: " + shown());
                    // Last used: a recipe that is not installed was never used (last).
                    chips[LibrarySortKey.LastUsed].PerformClick(); Application.DoEvents();
                    Require(Shown(form).Take(4).SequenceEqual(new[] { "Juri Jacket", "Louder Impact", "Frame Meter", "Cammy Boots" }) && Shown(form).IndexOf("Zeta Recipe") > 4, "Last used \u2193 with a recipe: " + shown());
                    chips[LibrarySortKey.Installed].PerformClick(); Application.DoEvents();
                    // A fighter picked in the filters: the recipe stays with that fighter's own mods, in name order.
                    panel.Grid.SetSelected(new[] { "016" }, true);
                    Require(shown() == "Juri Jacket, Juri Outfits Pack, Zeta Recipe", "Juri picked: " + shown());
                    panel.Grid.SetSelected(new string[0], true);
                    // A recipe word lists the recipes before the mods it also finds by name.
                    search.Text = "outfits"; Apply(form);
                    Require(shown() == "Zeta Recipe, Juri Outfits Pack", "a recipe word: " + shown());
                    search.Text = ""; Apply(form);
                    Require(Get<Label>(form, "libraryCount").Text == "11 MODS", "the caption counts the recipe: " + Get<Label>(form, "libraryCount").Text);
                }
            }
            finally { OutfitRecipes.Delete(AppConfig.DataDir, recipe.Id); }
        }
    }
}
