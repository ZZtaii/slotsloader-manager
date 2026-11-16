using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace SlotsInstaller
{
    // 1.0.19 (--queue-check, beside "add to outfit"): the list of added parts the loader reads to offer them in the
    // game (ManagerParts.cs; the loader's docs/MANAGER_CONTRACT.md, "added parts as in-game options"). Generated mods in
    // a fixture game folder; every state is also checked the way the loader checks it (Loader below).
    internal static class ManagerPartsChecks
    {
        private const string M = "natives/stm/product/model/esf/esf016/001/";
        private const string HairMesh = M + "02/esf016_001_02.mesh.230110883", HairMaterial = M + "02/esf016_001_02_v00.mdf2.31", Chain = M + "02/esf016_001_02_chain.chain.48";
        private const string HairTexture = M + "01/esf016_001_01_hairB_albd.tex.241101895", ExtraTexture = M + "01/esf016_001_01_extra_albd.tex.241101895", TwoTexture = M + "02/esf016_001_02_two.tex.241101895";
        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        private static void Zip(string file, Dictionary<string, string> texts)
        {
            using (ZipArchive zip = ZipFile.Open(file, ZipArchiveMode.Create))
                foreach (var kv in texts) using (var w = new StreamWriter(zip.CreateEntry(kv.Key).Open(), new UTF8Encoding(false))) w.Write(kv.Value);
        }
        private static void Put(string file, string text) { Directory.CreateDirectory(Path.GetDirectoryName(file)); File.WriteAllText(file, text, new UTF8Encoding(false)); }

        private sealed class Part { internal string Id, Name, Dir; internal bool On; internal Dictionary<string, long> Files; }
        private sealed class Entry { internal string Source, Base; internal List<Part> Parts = new List<Part>(); internal Dictionary<string, long> BaseFiles = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase); }
        private static Dictionary<string, long> FilesOf(string folder)
        {
            var files = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            if (Directory.Exists(folder)) foreach (string f in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)) files[f.Substring(folder.Length + 1).Replace('\\', '/')] = new FileInfo(f).Length;
            return files;
        }
        // The list as the loader reads it, with the loader's own checks: the format; every folder below manager_parts
        // and with files; ids of letters, digits, - and _ (8 hex here), one per part of an outfit; "on" a JSON bool;
        // each file of a part that is on, unless a later part that is on has the same path, in the outfit's folder at
        // the same size; base files only at paths a part that is on took, and "base": null when there is none.
        private static List<Entry> Loader(string game)
        {
            string data = ManagerInstalls.Folder(game), file = ManagerParts.FilePath(game), costumes = Path.Combine(game, "reframework", "costume_mods");
            var entries = new List<Entry>();
            if (!File.Exists(file)) { Require(!Directory.Exists(ManagerParts.FolderPath(game)), "no list, but the manager_parts folder is there"); return entries; }
            string text = File.ReadAllText(file, Encoding.UTF8);
            Require(File.ReadAllBytes(file)[0] == (byte)'{' && !text.Contains("\r") && !Directory.EnumerateFiles(data, "*.tmp").Any(), "the list has a BOM or CR, or a temporary file is left beside it");
            var doc = (Dictionary<string, object>)Json.Serializer().DeserializeObject(text);
            Require((string)doc["format"] == "sf6-manager-parts-1", "format: " + doc["format"]);
            foreach (Dictionary<string, object> o in (object[])doc["outfits"])
            {
                var e = new Entry { Source = (string)o["source"], Base = (string)o["base"] };
                string outfit = Path.Combine(costumes, e.Source.Replace('/', '\\'));
                Require(!e.Source.Contains("\\") && e.Source.Split('/').Length == 2 && Directory.Exists(Path.Combine(outfit, "natives")) && !entries.Any(x => x.Source == e.Source), "source is not one folder of costume_mods, listed once: " + e.Source);
                var owner = new Dictionary<string, Part>(StringComparer.OrdinalIgnoreCase);
                foreach (Dictionary<string, object> p in (object[])o["parts"])
                {
                    Require(p["on"] is bool && p["dir"] is string, "\"on\" is no bool or \"dir\" is missing in " + e.Source);
                    var part = new Part { Id = (string)p["id"], Name = (string)p["name"], On = (bool)p["on"], Dir = (string)p["dir"] };
                    Require(Regex.IsMatch(part.Id, "^[0-9a-f]{8}$") && !e.Parts.Any(x => x.Id == part.Id) && part.Name.Length > 0, "id or name of a part of " + e.Source + ": " + part.Id + " / " + part.Name);
                    Require(part.Dir.StartsWith("manager_parts/") && !part.Dir.Contains("\\") && !part.Dir.Contains("..") && part.Dir.EndsWith("/" + part.Id), "dir: " + part.Dir);
                    part.Files = FilesOf(Path.Combine(data, part.Dir.Replace('/', '\\')));
                    Require(part.Files.Count > 0 && part.Files.Keys.All(k => k.StartsWith("natives/")), "the folder of part " + part.Name + " has no files, or files outside natives");
                    if (part.On) foreach (string path in part.Files.Keys) owner[path] = part;
                    e.Parts.Add(part);
                }
                Require(e.Parts.Count > 0, "an outfit without parts is listed: " + e.Source);
                foreach (var kv in owner)
                {
                    var inOutfit = new FileInfo(Path.Combine(outfit, kv.Key.Replace('/', '\\')));
                    Require(inOutfit.Exists && inOutfit.Length == kv.Value.Files[kv.Key], e.Source + ": " + kv.Key + " of the part " + kv.Value.Name + ", which is on, is not in the outfit's folder at its size");
                }
                if (e.Base != null)
                {
                    Require(e.Base.StartsWith("manager_parts/") && e.Base.EndsWith("/base"), "base: " + e.Base);
                    e.BaseFiles = FilesOf(Path.Combine(data, e.Base.Replace('/', '\\')));
                    Require(e.BaseFiles.Count > 0 && e.BaseFiles.Keys.All(owner.ContainsKey), e.Source + ": the base folder is empty or holds a file at no path of a part that is on: " + String.Join(", ", e.BaseFiles.Keys.Where(k => !owner.ContainsKey(k))));
                }
                entries.Add(e);
            }
            // Nothing else under manager_parts than the listed folders and each outfit's built.json
            var listedDirs = new HashSet<string>(entries.SelectMany(e => e.Parts.Select(p => p.Dir).Concat(e.Base == null ? new string[0] : new[] { e.Base })), StringComparer.OrdinalIgnoreCase);
            foreach (string outfitDir in Directory.EnumerateDirectories(ManagerParts.FolderPath(game)))
            {
                Require(Directory.EnumerateFiles(outfitDir).Select(Path.GetFileName).SequenceEqual(new[] { ManagerParts.BuiltName }), "files beside the folders of " + Path.GetFileName(outfitDir));
                foreach (string dir in Directory.EnumerateDirectories(outfitDir))
                    Require(listedDirs.Contains("manager_parts/" + Path.GetFileName(outfitDir) + "/" + Path.GetFileName(dir)), "a folder that is not listed: " + dir);
            }
            Require(!Directory.EnumerateFiles(ManagerParts.FolderPath(game)).Any(), "files in manager_parts itself");
            return entries;
        }
        private static string Dates(string game)
        {
            string data = ManagerInstalls.Folder(game);
            return String.Join("\n", Directory.EnumerateFiles(data, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).Select(f => f.Substring(data.Length) + "|" + new FileInfo(f).Length + "|" + File.GetLastWriteTimeUtc(f).Ticks));
        }
        private static string Read(string game, string dir, string path)
        {
            string f = Path.Combine(ManagerInstalls.Folder(game), dir.Replace('/', '\\'), path.Replace('/', '\\'));
            return File.Exists(f) ? File.ReadAllText(f) : null;
        }

        internal static void Run(string root)
        {
            string game = Path.Combine(root, "manager-parts-game"), costumes = Path.Combine(game, "reframework", "costume_mods"), data = ManagerInstalls.Folder(game), mods = Path.Combine(root, "manager-parts-mods");
            Directory.CreateDirectory(costumes); Directory.CreateDirectory(mods);
            string baseMod = Path.Combine(mods, "MP Base.zip"), bundle = Path.Combine(mods, "MP Hair Bundle.zip"), extras = Path.Combine(mods, "MP Extras.zip"), two = Path.Combine(mods, "MP Hair Two");
            Zip(baseMod, new Dictionary<string, string> {
                { "Base/modinfo.ini", "name=MP Base\r\n" }, { "Base/" + M + "01/esf016_001_01.mesh.230110883", "base body" }, { "Base/" + M + "01/esf016_001_01_v00.mdf2.31", "base body material" },
                { "Base/" + HairMesh, "base hair" }, { "Base/" + HairMaterial, "base hair material" }, { "Base/" + Chain, "base chain" } });
            Zip(bundle, new Dictionary<string, string> {
                { "B/Main Files/modinfo.ini", "name=Main Files\r\n" }, { "B/Main Files/" + M + "01/esf016_001_01.mesh.230110883", "donor body" },
                { "B/Hair B/modinfo.ini", "name=Hair B\r\n" }, { "B/Hair B/" + HairMesh, "donor hair" }, { "B/Hair B/" + HairMaterial, "donor hair material" }, { "B/Hair B/" + HairTexture, "donor hair texture" } });
            Zip(extras, new Dictionary<string, string> { { "Extras/modinfo.ini", "name=MP Extras\r\n" }, { "Extras/" + ExtraTexture, "extra texture" } });
            Put(Path.Combine(two, "modinfo.ini"), "name=Hair Two\r\n"); Put(Path.Combine(two, HairMesh.Replace('/', '\\')), "second hair mesh"); Put(Path.Combine(two, TwoTexture.Replace('/', '\\')), "second hair texture");

            Func<InstalledOutfit> goth = delegate { return Installer.ListInstalled(costumes).Single(o => o.Name == "Goth Slot"); };
            Func<string, string[], CompositionPlan> pick = delegate(string mod, string[] names)
            {
                ModPackage p = PackageReader.Load(mod, "");
                return Installer.SelectionFiles(p, names.Select(n => p.Components.Single(c => c.Name == n).Id).ToArray());
            };
            Func<CompositionPlan> basePlan = delegate { ModPackage p = PackageReader.Load(baseMod, ""); return Composer.Plan(p, Composer.DefaultSelection(p)); };
            string folder = Path.Combine(costumes, "Juri", "Goth Slot");
            Func<string, string> inOutfit = delegate(string path) { string f = Path.Combine(folder, path.Replace('/', '\\')); return File.Exists(f) ? File.ReadAllText(f) : null; };

            Installer.Install(basePlan(), costumes, "Juri", "Goth Slot", "", false);
            Installer.AddToOutfit(goth(), costumes, pick(extras, new[] { "MP Extras" }), "");
            // ---- the loader keeps no data in this game folder: nothing is written
            Require(!ManagerParts.Apply(game, true) && !Directory.Exists(data), "written into a game folder without SF6_Costumes_Data");
            Directory.CreateDirectory(data);
            Require(!ManagerParts.Apply(game, false) && !Directory.EnumerateFileSystemEntries(data).Any(), "not wanted: something was written");

            // ---- one part that is on and replaces nothing: listed, its files in its folder, no base
            int builds = ManagerParts.Builds;
            Require(ManagerParts.Apply(game, true), "the first sync wrote nothing");
            List<Entry> list = Loader(game);
            Entry e = list.Single();
            Require(e.Source == "Juri/Goth Slot" && e.Base == null && e.Parts.Count == 1 && e.Parts[0].On && e.Parts[0].Name == "MP Extras" && e.Parts[0].Files.Keys.SequenceEqual(new[] { ExtraTexture })
                && Read(game, e.Parts[0].Dir, ExtraTexture) == "extra texture" && ManagerParts.Builds == builds + 1, "one part on: " + File.ReadAllText(ManagerParts.FilePath(game)));
            string extrasId = e.Parts[0].Id, key = e.Parts[0].Dir.Split('/')[1];
            Require(key == goth().Id.Substring(0, 12) && extrasId == ManagerParts.Id(goth().Parts[0], false), "folder names: " + e.Parts[0].Dir);
            // the file as written: keys in this order, base null, on a bool
            Require(File.ReadAllText(ManagerParts.FilePath(game)) == "{\n  \"format\": \"sf6-manager-parts-1\",\n  \"outfits\": [\n    {\n      \"source\": \"Juri/Goth Slot\",\n      \"base\": null,\n      \"parts\": [\n"
                + "        { \"id\": \"" + extrasId + "\", \"name\": \"MP Extras\", \"on\": true, \"dir\": \"manager_parts/" + key + "/" + extrasId + "\" }\n      ]\n    }\n  ]\n}\n", "the list's text: " + File.ReadAllText(ManagerParts.FilePath(game)));
            // ---- a second sync changes no file and no date, and reads no mod
            string dates = Dates(game); builds = ManagerParts.Builds; int copies = ManagerParts.Copies;
            System.Threading.Thread.Sleep(30);
            Require(!ManagerParts.Apply(game, true) && Dates(game) == dates && ManagerParts.Builds == builds && ManagerParts.Copies == copies, "a second sync changed a file or a date, or read the mods again");
            // the mod of a part that is on and whole in the folder is not needed: gone, the list is made all the same
            string away = extras + ".away";
            File.Move(extras, away); Directory.Delete(Path.Combine(ManagerParts.FolderPath(game), key), true);
            Require(ManagerParts.Apply(game, true) && Loader(game).Single().Parts[0].Id == extrasId && Read(game, "manager_parts/" + key + "/" + extrasId, ExtraTexture) == "extra texture", "a part whose files are all in the outfit's folder needed its mod");
            File.Move(away, extras);

            // ---- a part that replaces two files of the outfit and brings one new: base holds the outfit's own two
            Installer.AddToOutfit(goth(), costumes, pick(bundle, new[] { "Hair B" }), "");
            dates = Dates(game);
            Require(ManagerParts.Apply(game, true), "a part added: nothing written");
            e = Loader(game).Single();
            Require(e.Parts.Count == 2 && e.Parts[0].Name == "MP Extras" && e.Parts[1].Name.StartsWith("Hair B"), "two parts: " + String.Join(" | ", e.Parts.Select(p => p.Name)));
            Part hairB = e.Parts[1];
            Require(e.Parts[0].Id == extrasId && hairB.On && hairB.Files.Count == 3 && Read(game, hairB.Dir, HairMesh) == "donor hair" && Read(game, hairB.Dir, HairMaterial) == "donor hair material" && Read(game, hairB.Dir, HairTexture) == "donor hair texture",
                "the part's folder: " + String.Join(", ", hairB.Files.Keys));
            Require(e.Base == "manager_parts/" + key + "/base" && e.BaseFiles.Keys.OrderBy(k => k).SequenceEqual(new[] { HairMesh, HairMaterial }.OrderBy(k => k)) && Read(game, e.Base, HairMesh) == "base hair" && Read(game, e.Base, HairMaterial) == "base hair material",
                "base is not the outfit's own two files under the part: " + String.Join(", ", e.BaseFiles.Keys));
            // the first part's file was not written again (its date is the one of before)
            string extraLine = dates.Split('\n').Single(l => l.Contains(extrasId) && l.Contains("extra_albd"));
            Require(Dates(game).Split('\n').Contains(extraLine), "a file that was already right was written again");
            string hairId = hairB.Id;

            // ---- two parts on the same path: the later one's file is in the folder, the earlier part's own file is read
            //      from its mod; base still holds the outfit's own file of every path a part took
            Installer.AddToOutfit(goth(), costumes, pick(two, new[] { "Hair Two" }), "");
            Require(inOutfit(HairMesh) == "second hair mesh" && ManagerParts.Apply(game, true), "a third part: nothing written");
            e = Loader(game).Single();
            Require(e.Parts.Select(p => p.Id).Take(2).SequenceEqual(new[] { extrasId, hairId }) && e.Parts.Count == 3 && e.Parts.All(p => p.On) && e.Parts[2].Name == "Hair Two", "three parts in the order applied: " + String.Join(" | ", e.Parts.Select(p => p.Name)));
            string twoId = e.Parts[2].Id;
            Require(Read(game, e.Parts[1].Dir, HairMesh) == "donor hair" && Read(game, e.Parts[1].Dir, HairMaterial) == "donor hair material" && Read(game, e.Parts[2].Dir, HairMesh) == "second hair mesh" && Read(game, e.Parts[2].Dir, TwoTexture) == "second hair texture"
                && e.Parts[2].Files.Count == 2 && Read(game, e.Base, HairMesh) == "base hair" && Read(game, e.Base, HairMaterial) == "base hair material" && e.BaseFiles.Count == 2, "two parts on one path: each folder holds its own whole file, base the outfit's");

            // ---- reordered (the Added parts window's Save): the same ids in the new order, the folder follows
            InstalledOutfit now = goth();
            Installer.SetParts(now, costumes, basePlan(), new List<PartGraft> { now.Parts[2], now.Parts[0], now.Parts[1] }, "");
            Require(inOutfit(HairMesh) == "donor hair" && ManagerParts.Apply(game, true), "reordered: nothing written");
            e = Loader(game).Single();
            Require(e.Parts.Select(p => p.Id).SequenceEqual(new[] { twoId, extrasId, hairId }) && Read(game, e.Parts[0].Dir, HairMesh) == "second hair mesh" && Read(game, e.Parts[2].Dir, HairMesh) == "donor hair" && e.BaseFiles.Count == 2,
                "a part's id changed with its place, or the order is not the one applied: " + String.Join(",", e.Parts.Select(p => p.Id)));

            // ---- a part turned off (as the Installed page does it): listed with "on": false where it was, its files whole
            //      in its folder (from its mod: the outfit's folder has none of them), base only under the parts that are on
            now = goth();
            PartGraft offPart = now.Parts[2]; offPart.Place = 2;
            Installer.SetParts(now, costumes, basePlan(), new List<PartGraft> { now.Parts[0], now.Parts[1] }, "");
            Installer.SetOffParts(goth(), costumes, new List<PartGraft> { offPart });
            Require(inOutfit(HairMesh) == "second hair mesh" && inOutfit(HairMaterial) == "base hair material" && inOutfit(HairTexture) == null && ManagerParts.Apply(game, true), "a part off: nothing written");
            e = Loader(game).Single();
            Require(e.Parts.Select(p => p.Id + (p.On ? "+" : "-")).SequenceEqual(new[] { twoId + "+", extrasId + "+", hairId + "-" }), "a part off: " + String.Join(",", e.Parts.Select(p => p.Id + (p.On ? "+" : "-"))));
            Require(e.Parts[2].Files.Count == 3 && Read(game, e.Parts[2].Dir, HairMesh) == "donor hair" && Read(game, e.Parts[2].Dir, HairTexture) == "donor hair texture" && e.BaseFiles.Keys.SequenceEqual(new[] { HairMesh }) && Read(game, e.Base, HairMesh) == "base hair",
                "a part that is off keeps its files; base is only under the parts that are on: " + String.Join(", ", e.BaseFiles.Keys));
            Require(File.ReadAllText(ManagerParts.FilePath(game)).Contains("\"on\": false, \"dir\": \"manager_parts/" + key + "/" + hairId + "\""), "\"on\": false is not written as a bool");
            dates = Dates(game);
            System.Threading.Thread.Sleep(30);
            Require(!ManagerParts.Apply(game, true) && Dates(game) == dates, "a second sync with a part off changed something");

            // ---- a mod that is needed and gone: the outfit is left out, said once in the log; back, it is listed again
            string log = Path.Combine(AppConfig.DataDir, "log.txt");
            Func<int> said = delegate { return File.Exists(log) ? Regex.Matches(File.ReadAllText(log), "Added parts of Juri/Goth Slot are not offered in the game").Count : 0; };
            int lines = said();
            away = bundle + ".away";
            File.Move(bundle, away); Directory.Delete(Path.Combine(ManagerParts.FolderPath(game), key), true);
            Require(ManagerParts.Apply(game, true) && Loader(game).Count == 0 && !File.Exists(ManagerParts.FilePath(game)) && said() == lines + 1, "the mod of a part that is off is gone: the outfit is still listed, or the log is silent");
            Require(!ManagerParts.Apply(game, true) && said() == lines + 1, "said again at the next sync");
            File.Move(away, bundle);
            Require(ManagerParts.Apply(game, true) && Loader(game).Single().Parts.Count == 3, "the mod is back: not listed again");
            // the outfit's own mod changed since the install (another file list): left out, not guessed
            now = goth(); offPart = now.OffParts[0];
            Installer.SetParts(now, costumes, basePlan(), new List<PartGraft> { now.Parts[0], now.Parts[1], offPart }, ""); Installer.SetOffParts(goth(), costumes, null);
            Require(ManagerParts.Apply(game, true) && Loader(game).Single().Parts.All(p => p.On), "all three on again");
            byte[] baseBytes = File.ReadAllBytes(baseMod);
            File.Delete(baseMod);
            Zip(baseMod, new Dictionary<string, string> { { "Base/modinfo.ini", "name=MP Base\r\n" }, { "Base/" + M + "01/esf016_001_01.mesh.230110883", "base body" }, { "Base/" + HairMesh, "base hair" } });
            Directory.Delete(Path.Combine(ManagerParts.FolderPath(game), key), true);
            lines = said();
            Require(ManagerParts.Apply(game, true) && Loader(game).Count == 0 && said() == lines + 1 && File.ReadAllText(log).Contains("the outfit's own mod no longer gives the files of the install record"), "the outfit's own mod changed: its base was guessed");
            File.WriteAllBytes(baseMod, baseBytes);
            Require(ManagerParts.Apply(game, true) && Loader(game).Single().BaseFiles.Count == 2, "the outfit's own mod is as installed again: not listed");

            // ---- ids: the same part on another outfit and under another outfit name has the same id
            Installer.Install(basePlan(), costumes, "Juri", "Another Name", "", false);
            Installer.AddToOutfit(Installer.ListInstalled(costumes).Single(o => o.Name == "Another Name"), costumes, pick(bundle, new[] { "Hair B" }), "");
            Require(ManagerParts.Apply(game, true), "a second outfit: nothing written");
            list = Loader(game);
            Require(list.Select(x => x.Source).SequenceEqual(new[] { "Juri/Another Name", "Juri/Goth Slot" }) && list[0].Parts.Single().Id == hairId && list[0].BaseFiles.Count == 2 && list[0].Parts[0].Dir != list[1].Parts.Single(p => p.Id == hairId).Dir,
                "two outfits, the same part: " + String.Join(" | ", list.Select(x => x.Source + ":" + String.Join(",", x.Parts.Select(p => p.Id)))));
            PartGraft sample = goth().Parts.Single(g => g.Options.Contains("Hair B"));
            Require(ManagerParts.Id(sample, false) == hairId && ManagerParts.Id(new PartGraft { SourcePath = Path.Combine(root, "elsewhere", Path.GetFileName(bundle)), SelectedIds = sample.SelectedIds.ToList(), Mod = "Renamed", Options = new List<string> { "x" }, Place = 5 }, false) == hairId
                && ManagerParts.Id(new PartGraft { SourcePath = sample.SourcePath, SelectedIds = sample.SelectedIds.ToList(), Parts = new List<string> { "hair" } }, false) != hairId
                && ManagerParts.Ids(new List<PartGraft> { sample, new PartGraft { SourcePath = Path.Combine(root, "elsewhere", Path.GetFileName(bundle)), SelectedIds = sample.SelectedIds.ToList() } }).Distinct().Count() == 2,
                "a part's id is not made from its mod's file name, its options and the parts taken alone, or two parts share one");

            // ---- a part removed: its entry and folder go; the outfit disabled: not listed, enabled again: listed
            now = goth();
            Installer.RemovePart(now, costumes, basePlan(), now.Parts.Single(g => g.Options.Contains("Hair Two")), "");
            Require(ManagerParts.Apply(game, true), "a part removed: nothing written");
            e = Loader(game).Single(x => x.Source == "Juri/Goth Slot");
            Require(e.Parts.Select(p => p.Id).SequenceEqual(new[] { extrasId, hairId }) && !Directory.Exists(Path.Combine(ManagerParts.FolderPath(game), key, twoId)), "the removed part is still there");
            Installer.SetEnabled(goth(), costumes, false);
            Require(ManagerParts.Apply(game, true) && Loader(game).Select(x => x.Source).SequenceEqual(new[] { "Juri/Another Name" }) && !Directory.Exists(Path.Combine(ManagerParts.FolderPath(game), key)), "a disabled outfit is listed or its folder stays");
            Installer.SetEnabled(Installer.ListInstalled(costumes).Single(o => o.Name == "Goth Slot"), costumes, true);
            Require(ManagerParts.Apply(game, true) && Loader(game).Count == 2, "enabled again: not listed");

            // ---- through the loader's settings (LoaderFlags.Apply): written only for a suite-line loader of this Manager
            //      with "Costume options in the game" on (chosen here, or never chosen and true in the game folder)
            string flags = Path.Combine(root, "manager-parts-data"); Directory.CreateDirectory(flags);
            var loader = new ReplaceMod { Id = "loader", Name = "SF6 Slots Loader", SourcePath = Path.Combine(root, "gone"), Enabled = true };
            loader.Files.Add(new ModFile { Path = LoaderCostumePak.PluginPath }); loader.Files.Add(new ModFile { Path = LoaderFlags.StageData });
            var with = new ReplaceState(); with.Mods.Add(loader);
            var without = new ReplaceState();
            string settings = Path.Combine(game, LoaderFlags.PluginSettingsRelative);
            Func<bool> there = delegate { return File.Exists(ManagerParts.FilePath(game)) && Directory.Exists(ManagerParts.FolderPath(game)); };
            Func<bool> gone = delegate { return !File.Exists(ManagerParts.FilePath(game)) && !Directory.Exists(ManagerParts.FolderPath(game)); };
            LoaderFlags.Save(flags, new LoaderFlagChoices { OutfitOptions = true });
            LoaderFlags.Apply(game, flags, without);
            Require(gone(), "no such loader installed: the list is there");
            File.WriteAllText(Path.Combine(data, "outfits.json"), "{}");   // the loader's own file beside it: never touched
            LoaderFlags.Apply(game, flags, with);
            Require(there() && Loader(game).Count == 2, "the loader installed and the switch on: no list");
            LoaderFlags.Save(flags, new LoaderFlagChoices { OutfitOptions = false });
            LoaderFlags.Apply(game, flags, with);
            Require(gone() && File.ReadAllText(Path.Combine(data, "outfits.json")) == "{}", "the switch off: the list or its folder stays, or something else went");
            LoaderFlags.Save(flags, new LoaderFlagChoices());
            LoaderFlags.Apply(game, flags, with);
            Require(gone(), "never chosen and no key in the game folder: off");
            Put(settings, "{\n  \"menu_outfit_options\": true,\n  \"display_nsfw\": false\n}\n");
            LoaderFlags.Apply(game, flags, with);
            Require(there() && Loader(game).Count == 2, "never chosen and true in the game folder: no list");
            LoaderFlags.Apply(game, flags, without);
            Require(gone() && File.Exists(Path.Combine(data, "outfits.json")), "the loader uninstalled: the list stays");
            LoaderFlags.SuiteLoaderInstalled = false;

            // ---- the outfits uninstalled: the entries go one by one, then the list and the folder
            Require(ManagerParts.Apply(game, true) && Loader(game).Count == 2, "listed again");
            Installer.Uninstall(goth(), costumes);
            Require(ManagerParts.Apply(game, true) && Loader(game).Single().Source == "Juri/Another Name" && !Directory.Exists(Path.Combine(ManagerParts.FolderPath(game), key)), "an uninstalled outfit is still listed");
            Installer.Uninstall(Installer.ListInstalled(costumes).Single(o => o.Name == "Another Name"), costumes);
            Require(ManagerParts.Apply(game, true) && gone() && File.Exists(Path.Combine(data, "outfits.json")), "no outfit with parts left: the list or its folder stays");
            Require(!ManagerParts.Apply(game, true), "nothing to do, but something changed");
        }
    }
}
