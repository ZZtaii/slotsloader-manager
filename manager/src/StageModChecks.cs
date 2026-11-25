using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace SlotsInstaller
{
    // 25 Nov 2026: Extra stage (StageMods.cs) on a generated game folder. Nothing here touches the real game.
    internal static class StageModChecks
    {
        private static void Require(bool condition, string message) { ReplaceChecks.Require(condition, message); }
        internal const string LevelPath = "natives/stm/product/level/scene/ess/ess0000_00/ess0000_00_level.scn.20";
        internal const string BeachPath = "natives/stm/product/environment/ess0200/ess0200_sand.tex.143221013";

        private static void Write(string path, string text) { Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, text, new UTF8Encoding(false)); }
        private static void Write(string path, byte[] bytes) { Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllBytes(path, bytes); }
        private static byte[] Png() { return Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg=="); }

        // The suite loader's footprint: its proxy DLL (over 200 KB) and its stage path list.
        internal static string Game(string root, bool loader)
        {
            string game = Path.Combine(root, "game");
            Directory.CreateDirectory(game);
            if (loader)
            {
                Write(Path.Combine(game, StageMods.LoaderDll), new byte[300 * 1024]);
                Write(Path.Combine(game, StageMods.StagePathsRelative), LevelPath + "\n" + BeachPath + "\nnatives/stm/product/camera/scene/ess/ess0100_00_camera.scn.20\n");
            }
            return game;
        }

        internal static void Engine(string root)
        {
            string game = Game(root, true), mods = Path.Combine(root, "mods");
            Require(StageMods.LoaderReady(game) && !StageMods.LoaderReady(Game(Path.Combine(root, "plain"), false)), "loader detection");
            Require(StageMods.PakHashes(new byte[] { 1, 2, 3 }).Count == 0 && StageMods.PakHashes(Encoding.ASCII.GetBytes("KPKA\u0004\0\u0008\0\u0001\0\0\0\0\0\0\0")).Count == 0, "garbage or an encrypted table read as a PAK");

            // A pack of two options over one beach file (alternatives), one with a picture; a one-option lamp; a stage PAK; a
            // PAK of something else; a fighter mod.
            string pack = Path.Combine(mods, "Night Pack");
            Write(Path.Combine(pack, "Option A", "modinfo.ini"), "name=Dusk\r\nauthor=Tester\r\n");
            Write(Path.Combine(pack, "Option A", BeachPath.Replace('/', '\\')), "dusk");
            Write(Path.Combine(pack, "Option B", "modinfo.ini"), "name=Midnight\r\nscreenshot=shot.png\r\n");
            Write(Path.Combine(pack, "Option B", BeachPath.Replace('/', '\\')), "midnight");
            Write(Path.Combine(pack, "Option B", "shot.png"), Png());
            string lamp = Path.Combine(mods, "Lamp");
            Write(Path.Combine(lamp, "modinfo.ini"), "name=Lamp\r\n");
            Write(Path.Combine(lamp, "natives", "stm", "product", "light", "stage", "ess0000", "lamp.user.2"), "lamp");
            string arena = Path.Combine(mods, "Arena"); Directory.CreateDirectory(arena);
            PakWriter.WriteFixture(Path.Combine(arena, "Arena.pak"), new List<KeyValuePair<string, byte[]>> { new KeyValuePair<string, byte[]>(LevelPath, Encoding.UTF8.GetBytes("arena")), new KeyValuePair<string, byte[]>("natives/stm/arena/new_prop.mesh.2109108288", Encoding.UTF8.GetBytes("prop")) }, 0, 0);
            string other = Path.Combine(mods, "Other"); Directory.CreateDirectory(other);
            PakWriter.WriteFixture(Path.Combine(other, "Other.pak"), new List<KeyValuePair<string, byte[]>> { new KeyValuePair<string, byte[]>("natives/stm/product/gui/menu/other.tex.143221013", Encoding.UTF8.GetBytes("ui")) }, 0, 0);
            string fighter = Path.Combine(mods, "Fighter");
            Write(Path.Combine(fighter, "natives", "stm", "product", "model", "esf", "esf001", "001", "01", "esf001_001_01.mesh.230110883"), "ryu");
            Write(Path.Combine(fighter, "natives", "stm", "product", "environment", "ess0200", "x.tex.143221013"), "beach");

            ModPackage packP = PackageReader.Load(pack, ""), lampP = PackageReader.Load(lamp, ""), arenaP = PackageReader.Load(arena, ""), otherP = PackageReader.Load(other, ""), fighterP = PackageReader.Load(fighter, "");
            StageInfo s = StageMods.Detect(packP, null, game);
            Require(s != null && s.Keys.SequenceEqual(new[] { "0200" }) && !s.FromPak, "natives pack: " + (s == null ? "none" : String.Join(",", s.Keys)));
            Require(StageMods.Detect(lampP, null, game).Keys.SequenceEqual(new[] { "0000" }), "lamp");
            Require(StageMods.Detect(arenaP, null, game) == null, "a PAK mod without its PAK read must stay undecided");
            using (var reader = new SourceReader(arena, ""))
            {
                StageInfo a = StageMods.Detect(arenaP, reader, game);
                Require(a != null && a.FromPak && a.Keys.SequenceEqual(new[] { "0000" }), "stage PAK: " + (a == null ? "none" : String.Join(",", a.Keys)));
                Require(StageMods.Detect(arenaP, reader, Path.Combine(root, "plain", "game")) == null, "a stage PAK without the stage list must not be a stage");
            }
            using (var reader = new SourceReader(other, "")) Require(StageMods.Detect(otherP, reader, game) == null, "a PAK of interface files is no stage");
            Require(StageMods.Detect(fighterP, null, game) == null, "a mod with fighter files is no stage mod");

            // Default ticks: one option is ticked, several are left to the user.
            Require(MainForm.StageDefaults(packP).Count == 0 && MainForm.StageDefaults(lampP).Count == 1 && MainForm.StageDefaults(arenaP).Count == 1, "defaults: pack " + MainForm.StageDefaults(packP).Count + ", lamp " + MainForm.StageDefaults(lampP).Count + ", arena " + MainForm.StageDefaults(arenaP).Count);

            // Install both options: one folder, one sub-folder per option with its modinfo.ini (and picture), the record.
            string stageRoot = StageMods.Root(game);
            Directory.CreateDirectory(Path.Combine(stageRoot, "Manual"));
            Write(Path.Combine(stageRoot, "Manual", "modinfo.ini"), "name=Manual\r\n");
            var both = packP.Components.Where(c => !c.IsDummy && c.Assets.Count > 0).Select(c => c.Id).ToList();
            InstallResult r = StageMods.Install(packP, both, game, "", "Night Pack");
            string installedPack = Path.Combine(stageRoot, "Night Pack");
            Require(r.Folder == installedPack && File.ReadAllText(Path.Combine(installedPack, "Dusk", BeachPath.Replace('/', '\\'))) == "dusk" && File.ReadAllText(Path.Combine(installedPack, "Midnight", BeachPath.Replace('/', '\\'))) == "midnight", "both options copied: " + r.Folder);
            string ini = File.ReadAllText(Path.Combine(installedPack, "Midnight", "modinfo.ini"));
            Require(ini.Contains("name=Midnight") && ini.Contains("screenshot=screenshot.png") && ini.Contains("nameAsBundle=Night Pack") && File.Exists(Path.Combine(installedPack, "Midnight", "screenshot.png")), "option modinfo: " + ini);
            Require(File.ReadAllText(Path.Combine(installedPack, "Dusk", "modinfo.ini")).Contains("author=Tester"), "author kept");
            List<StageInstall> listed = StageMods.List(game);
            Require(listed.Count == 1 && listed[0].Name == "Night Pack" && listed[0].Record.OptionIds.Count == 2 && String.Equals(listed[0].Record.SourcePath, Path.GetFullPath(pack), StringComparison.OrdinalIgnoreCase), "listed: " + String.Join(", ", listed.Select(x => x.Name)) + " (the hand-made folder must not be listed)");

            // Install again with one option: the same folder, the other option gone, nothing left over.
            StageMods.Install(packP, new[] { both[0] }, game, "", "Night Pack");
            Require(Directory.GetDirectories(installedPack).Length == 1 && StageMods.List(game).Single().Record.OptionIds.Count == 1, "an update keeps one folder with the ticked option only");
            Require(Directory.GetFileSystemEntries(stageRoot).Select(Path.GetFileName).OrderBy(n => n).SequenceEqual(new[] { "Manual", "Night Pack" }), "left over: " + String.Join(", ", Directory.GetFileSystemEntries(stageRoot).Select(Path.GetFileName)));

            // One-option mods are named after the mod; a stage PAK goes in as it is.
            StageMods.Install(lampP, MainForm.StageDefaults(lampP), game, "", "Lamp");
            StageMods.Install(arenaP, MainForm.StageDefaults(arenaP), game, "", "Arena");
            Require(File.Exists(Path.Combine(stageRoot, "Lamp", "Lamp", "natives", "stm", "product", "light", "stage", "ess0000", "lamp.user.2")), "lamp folder");
            Require(File.ReadAllBytes(Path.Combine(stageRoot, "Arena", "Arena", "Arena.pak")).SequenceEqual(File.ReadAllBytes(Path.Combine(arena, "Arena.pak"))), "the PAK is copied byte for byte");
            Require(StageMods.List(game).Count == 3, "three installed");

            // Nothing ticked, or no loader: refused, nothing changes.
            Refused(delegate { StageMods.Install(packP, new string[0], game, "", "Night Pack"); }, "Tick at least one option");
            string plain = Game(Path.Combine(root, "plain"), false);
            Refused(delegate { StageMods.Install(lampP, MainForm.StageDefaults(lampP), plain, "", "Lamp"); }, "needs Wael's Slots Loader");
            Require(!Directory.Exists(StageMods.Root(plain)), "a refused install made stage_mods");

            // Uninstall: only the Manager's folders, only that one.
            Refused(delegate { StageMods.Uninstall(game, Path.Combine(stageRoot, "Manual")); }, "was not installed by the Manager");
            Refused(delegate { StageMods.Uninstall(game, Path.Combine(root, "mods", "Lamp")); }, "not in the game's stage_mods");
            StageMods.Uninstall(game, installedPack);
            Require(!Directory.Exists(installedPack) && Directory.Exists(Path.Combine(stageRoot, "Lamp")) && Directory.Exists(Path.Combine(stageRoot, "Arena")) && Directory.Exists(Path.Combine(stageRoot, "Manual")), "uninstall removed the wrong folders");
            Require(File.Exists(Path.Combine(arena, "Arena.pak")) && File.Exists(Path.Combine(pack, "Option A", "modinfo.ini")), "the sources must never change");

            // The library lists a stage PAK as a Stage mod once the stage list is there, "PAK only" without it.
            Func<string> saved = StageMods.LibraryGameRoot;
            try
            {
                string zip = Path.Combine(root, "Arena.zip");
                System.IO.Compression.ZipFile.CreateFromDirectory(arena, zip);
                StageMods.LibraryGameRoot = delegate { return plain; };
                LibraryEntry before = Library.DescribeEntry(zip); Library.ForgetSource(zip); Library.IndexEntry(before, "");
                Require(before.MiscAreas.Contains(ModCategories.LabelPak) && before.StageIds.Count == 0, "without the list: " + String.Join(",", before.MiscAreas));
                StageMods.LibraryGameRoot = delegate { return game; };
                Library.ForgetSource(zip);
                LibraryEntry after = Library.DescribeEntry(zip); Library.IndexEntry(after, "");
                Require(ModCategories.IsStage(after) && after.StageIds.SequenceEqual(new[] { "0000" }) && !after.MiscAreas.Contains(ModCategories.LabelPak), "with the list: " + String.Join(",", after.MiscAreas) + " / " + String.Join(",", after.StageIds));
                Require(LibrarySearch.MatchesText(after, LibrarySearch.Parse("stage", MainForm.SearchCharacterTerm), delegate { return false; }, delegate { return false; }), "the library search 'stage' must find a stage PAK");
                Require(!ModCategories.IsStage(before), "a PAK-only entry was a Stage mod without the stage list");
            }
            finally { StageMods.LibraryGameRoot = saved; }
        }

        internal static void Refused(Action action, string expected)
        {
            try { action(); }
            catch (IOException ex) { Require(ex.Message.Contains(expected), "refused with '" + ex.Message + "', expected '" + expected + "'"); return; }
            throw new InvalidOperationException("Not refused: " + expected);
        }

        // A two-option stage pack (the same beach file in both: alternatives) and a one-option lamp, in `dir`.
        internal static void Mods(string dir, out string pack, out string lamp)
        {
            pack = Path.Combine(dir, "Night Pack"); lamp = Path.Combine(dir, "Lamp");
            Write(Path.Combine(pack, "Option A", "modinfo.ini"), "name=Dusk\r\n");
            Write(Path.Combine(pack, "Option A", BeachPath.Replace('/', '\\')), "dusk");
            Write(Path.Combine(pack, "Option B", "modinfo.ini"), "name=Midnight\r\n");
            Write(Path.Combine(pack, "Option B", BeachPath.Replace('/', '\\')), "midnight");
            Write(Path.Combine(lamp, "modinfo.ini"), "name=Lamp\r\n");
            Write(Path.Combine(lamp, "natives", "stm", "product", "light", "stage", "ess0000", "lamp.user.2"), "lamp");
        }
    }

    // 25 Nov 2026: Extra stage in the window, on the replace-ui fixture's game folder (put back as it was afterwards).
    internal static partial class ReplaceUiChecks
    {
        internal static void ExtraStageUi(MainForm form, ReplaceChecks.Fixture f, string root)
        {
            string game = Get<AppConfig>(form, "config").GameRoot, dll = Path.Combine(game, StageMods.LoaderDll), list = Path.Combine(game, StageMods.StagePathsRelative);
            byte[] dllBefore = File.Exists(dll) ? File.ReadAllBytes(dll) : null;
            bool dataBefore = Directory.Exists(Path.Combine(game, "reframework", "data", "SF6_StageSlots_Data")), refBefore = Directory.Exists(Path.Combine(game, "reframework")), modsBefore = Directory.Exists(StageMods.Root(game));
            string modeBefore = Get<string>(form, "installMode");
            string pack, lamp; StageModChecks.Mods(Path.Combine(root, "extra-stage-mods"), out pack, out lamp);
            Func<ModPackage> shown = delegate { return Get<ModPackage>(form, "package"); };
            Action<string> open = delegate(string source)
            {
                Call(form, "LoadPackage", new LibraryEntry { SourcePath = source, Name = Path.GetFileName(source) });
                DateTime until = DateTime.UtcNow.AddSeconds(30);
                while ((Get<bool>(form, "busy") || shown() == null || !String.Equals(shown().SourcePath, source, StringComparison.OrdinalIgnoreCase)) && DateTime.UtcNow < until) { Application.DoEvents(); System.Threading.Thread.Sleep(5); }
                WaitIdle(form);
            };
            var toggle = Get<SegmentedToggle>(form, "modeToggle"); var install = Get<FlatButton>(form, "installButton");
            try
            {
                File.WriteAllBytes(dll, new byte[300 * 1024]);
                Directory.CreateDirectory(Path.GetDirectoryName(list));
                File.WriteAllText(list, StageModChecks.LevelPath + "\n" + StageModChecks.BeachPath + "\n");
                Call(form, "SetInstallMode", InstallModes.Slot, true); ShowLibrary(form);

                // Several options: Extra stage, nothing ticked, Install off; no character or outfit name; no Health.
                open(pack);
                Require(form.StageMode && toggle.Options[0] == MainForm.StageModeLabel && !Get<Panel>(form, "targetPanel").Visible && !Get<FlatButton>(form, "previewButton").Enabled, "Extra stage shown: mode " + form.StageMode + ", label " + toggle.Options[0] + ", target " + Get<Panel>(form, "targetPanel").Visible);
                Require(Get<List<string>>(form, "selectedOrder").Count == 0 && !install.Enabled && Get<Label>(form, "planSummary").Text == "Select options", "several options start unticked: " + String.Join(",", Get<List<string>>(form, "selectedOrder")) + " / " + Get<Label>(form, "planSummary").Text);
                Require(Get<Label>(form, "modeNotice").Text.StartsWith("Each ticked option becomes a variant", StringComparison.Ordinal), "notice: " + Get<Label>(form, "modeNotice").Text);
                // Both alternatives ticked (in Extra outfit they would untick each other): both install as variants.
                List<string> both = shown().Components.Where(c => !c.IsDummy && c.Assets.Count > 0).Select(c => c.Id).ToList();
                var nodes = Get<Dictionary<string, OptionNode>>(form, "componentNodes");
                foreach (string id in both) nodes[id].Checked = true;
                Application.DoEvents();
                Require(Get<List<string>>(form, "selectedOrder").Count == 2 && install.Enabled && Get<Label>(form, "planSummary").Text == "Ready to install · 2 variants", "ticking both alternatives: " + String.Join(",", Get<List<string>>(form, "selectedOrder")) + " / " + Get<Label>(form, "planSummary").Text);
                install.PerformClick(); WaitIdle(form);
                string folder = Path.Combine(StageMods.Root(game), "Night Pack");
                Require(Directory.Exists(Path.Combine(folder, "Dusk")) && Directory.Exists(Path.Combine(folder, "Midnight")) && Get<List<GameModRow>>(form, "gameRows").Count(r => r.Stage != null) == 1, "install: " + Get<Label>(form, "statusText").Text);
                FlatButton libraryUninstall = Get<FlatButton>(form, "libraryUninstallButton");
                Require(libraryUninstall.Text == "Uninstall" && libraryUninstall.Enabled && Get<Label>(form, "planDetails").Text.StartsWith("Installed with 2 variants", StringComparison.Ordinal), "installed state in the library: " + libraryUninstall.Text + " / " + Get<Label>(form, "planDetails").Text);

                // Opened again: the installed options are ticked. Replace original keeps the label and shows Health.
                open(lamp); open(pack);
                Require(Get<List<string>>(form, "selectedOrder").Count == 2, "reopened with its installed options: " + Get<List<string>>(form, "selectedOrder").Count);
                Call(form, "SetInstallMode", InstallModes.Replace, true);
                Require(!form.StageMode && toggle.Options[0] == MainForm.StageModeLabel && Get<FlatButton>(form, "previewButton").Enabled, "Replace original on a stage mod");
                Call(form, "SetInstallMode", InstallModes.Slot, true);

                // One option: ticked at once.
                open(lamp);
                Require(form.StageMode && Get<List<string>>(form, "selectedOrder").Count == 1 && install.Enabled, "a one-option stage mod starts ticked");

                // The Installed page: an Extra stage row with Uninstall; Uninstall deletes only its folder.
                Call(form, "ShowPage", Get<Panel>(form, "installedPage")); Call(form, "RefreshInstalled", (string)null); WaitIdle(form);
                GameModRow row = SelectGameRow(form, r => r.Stage != null);
                FlatButton remove = Get<FlatButton>(form, "removeInstalledButton");
                Require(row != null && row.Name == "Night Pack" && remove.Visible && remove.Enabled && remove.Text == "Uninstall" && Get<Label>(form, "installedTitle").Text == "Night Pack" && Get<TextBox>(form, "installedHelp").Text.Contains("• Midnight"), "stage row: " + (row == null ? "none" : row.Name) + " / " + remove.Text);
                Kind(form, "Installed"); Application.DoEvents();
                Require(!Get<ListView>(form, "installedList").Items.Cast<ListViewItem>().Any(i => i.Tag is GameModRow && ((GameModRow)i.Tag).Stage != null), "Installed (replacements) must not list an extra stage");
                row = SelectGameRow(form, r => r.Stage != null);
                remove.PerformClick(); WaitIdle(form);
                Require(!Directory.Exists(folder) && !Get<List<GameModRow>>(form, "gameRows").Any(r => r.Stage != null) && File.Exists(Path.Combine(pack, "Option A", "modinfo.ini")), "uninstall: " + Get<Label>(form, "statusText").Text);
            }
            finally
            {
                WaitIdle(form);
                Call(form, "ClosePackage");
                if (Directory.Exists(StageMods.Root(game)) && !modsBefore) Directory.Delete(StageMods.Root(game), true);
                if (!dataBefore && Directory.Exists(Path.Combine(game, "reframework", "data", "SF6_StageSlots_Data"))) Directory.Delete(Path.Combine(game, "reframework", "data", "SF6_StageSlots_Data"), true);
                if (!refBefore && Directory.Exists(Path.Combine(game, "reframework"))) Directory.Delete(Path.Combine(game, "reframework"), true);
                if (dllBefore != null) File.WriteAllBytes(dll, dllBefore); else if (File.Exists(dll)) File.Delete(dll);
                Call(form, "SetInstallMode", modeBefore, true);
                Call(form, "ShowPage", Get<Panel>(form, "installedPage")); Call(form, "RefreshInstalled", (string)null); WaitIdle(form);
            }
        }
    }
}
