using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace SlotsInstaller
{
    // 1.0.19 (--replace-check): the suite loader's updater and menu settings (LoaderFlags.cs).
    internal static class LoaderFlagChecks
    {
        private static KeyValuePair<string, bool>[] K(params object[] pairs)
        {
            var list = new List<KeyValuePair<string, bool>>();
            for (int i = 0; i + 1 < pairs.Length; i += 2) list.Add(new KeyValuePair<string, bool>((string)pairs[i], (bool)pairs[i + 1]));
            return list.ToArray();
        }
        private static Dictionary<string, object> Obj(string text) { return Json.Serializer().DeserializeObject(text) as Dictionary<string, object>; }
        private static void Require(bool condition, string message) { ReplaceChecks.Require(condition, message); }

        internal static void Run(string root)
        {
            // ---- the key writer: only the key changes, the layout stays
            const string plugin = "{\n    \"display_nsfw\": false,\n    \"online_outfit\": \"Outfit 1\",\n    \"select_options_button\": \"R1\",\n    \"select_options_key\": \"C\"\n}\n";
            Require(LoaderFlags.WithKeys(null, K("menu_additions", false)) == "{\n  \"menu_additions\": false\n}\n", "a new file: " + LoaderFlags.WithKeys(null, K("menu_additions", false)));
            Require(LoaderFlags.WithKeys("  \r\n", K("menu_additions", true)) == "{\n  \"menu_additions\": true\n}\n", "a blank file is a new one");
            string added = LoaderFlags.WithKeys(plugin, K("menu_additions", false));
            Require(added == "{\n    \"menu_additions\": false," + plugin.Substring(1), "a key added first, in the file's indentation, the rest untouched: " + added);
            Require(LoaderFlags.WithKeys(added, K("menu_additions", false)) == added, "the same value writes nothing new");
            Require(LoaderFlags.WithKeys(added, K("menu_additions", true)) == added.Replace("\"menu_additions\": false", "\"menu_additions\": true"), "a value replaced in place");
            Require(LoaderFlags.WithKeys("[1]", K("x", true)) == null && LoaderFlags.WithKeys("{ broken", K("x", true)) == null && LoaderFlags.WithKeys("garbage", K("x", true)) == null, "not a JSON object: refused");
            Dictionary<string, object> rebuilt = Obj(LoaderFlags.WithKeys("{\"menu_additions\": \"no\", \"keep\": 7}", K("menu_additions", false)));
            Require(rebuilt != null && rebuilt["menu_additions"] is bool && !(bool)rebuilt["menu_additions"] && Convert.ToInt32(rebuilt["keep"]) == 7, "a key of another type is set, the others kept");
            const string updater = "{\n  \"check_for_updates\": true,\n  \"ignored_suite_version\": \"1.35.0\",\n  \"pending_cleanup\": [\n    \"a.dll.old-1\"\n  ]\n}\n";
            string off = LoaderFlags.WithKeys(updater, K("check_for_updates", false, "check_in_manager_mode", false));
            Dictionary<string, object> offObj = Obj(off);
            Require(offObj != null && !(bool)offObj["check_for_updates"] && !(bool)offObj["check_in_manager_mode"] && (string)offObj["ignored_suite_version"] == "1.35.0"
                && off.Contains("\"pending_cleanup\": [\n    \"a.dll.old-1\"\n  ]") && off.StartsWith("{\n  \"check_in_manager_mode\": false,\n  \"check_for_updates\": false,"), "the updater's own keys and layout kept: " + off);
            Require(Obj(LoaderFlags.WithKeys("﻿{\"a\":1}", K("menu_additions", false))).Count == 2 && Obj(LoaderFlags.WithKeys("{}", K("menu_additions", false))).Count == 1, "a BOM, a compact object, an empty object");

            // ---- which loader: by version (a release zip), else by the stage data only the suite line ships
            string zips = Path.Combine(root, "zips"); Directory.CreateDirectory(zips);
            Func<string, string, ReplaceMod> zipped = delegate(string name, string version)
            {
                string folder = Path.Combine(zips, name); Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, "modinfo.ini"), "name=" + name + "\r\nversion=" + version + "\r\n");
                string zip = Path.Combine(zips, name + ".zip"); ZipFile.CreateFromDirectory(folder, zip);
                var mod = new ReplaceMod { Id = name, Name = name, SourcePath = zip, Enabled = true };
                mod.Files.Add(new ModFile { Path = LoaderCostumePak.PluginPath });
                return mod;
            };
            ReplaceMod byVersion = zipped("suite", "1.27.3"), forkByVersion = zipped("fork", "1.10.9"), first = zipped("first-contract-2", "1.18.0"), before = zipped("before", "1.17.0");
            Require(LoaderFlags.IsSuiteLoader(byVersion) && LoaderFlags.IsSuiteLoader(first) && !LoaderFlags.IsSuiteLoader(forkByVersion) && !LoaderFlags.IsSuiteLoader(before), "the suite line starts at 1.18.0");
            byVersion.Enabled = false;
            Require(!LoaderFlags.IsSuiteLoader(byVersion) && !LoaderFlags.IsSuiteLoader(null), "a loader turned off sets nothing");
            var unknown = new ReplaceMod { Id = "u", Name = "u", SourcePath = Path.Combine(zips, "gone"), Enabled = true };
            unknown.Files.Add(new ModFile { Path = LoaderCostumePak.PluginPath });
            Require(!LoaderFlags.IsSuiteLoader(unknown), "no version and no stage data: not the suite line");
            unknown.Files.Add(new ModFile { Path = LoaderFlags.StageData.Replace('/', '\\') });
            Require(LoaderFlags.IsSuiteLoader(unknown), "no version, but the suite line's stage data");

            // ---- in a game folder
            var f = ReplaceChecks.Create(Path.Combine(root, "fixture"), false, ReplaceMethods.Loose);
            var original = ReplaceChecks.Snapshot(f.Game);
            string metadata = Path.Combine(f.Mod("tool"), "Tool Pack", "modinfo.ini");
            string state = Path.Combine(f.Game, LoaderFlags.UpdateStateRelative), settings = Path.Combine(f.Game, LoaderFlags.PluginSettingsRelative);

            File.WriteAllText(metadata, "name=Slotsloader Fork\r\nauthor=ZZtaii, Wael3rd\r\nversion=1.10.9\r\n");
            ReplaceMod fork = ReplaceChecks.Install(f, "tool", "Fork loader");
            Require(!File.Exists(state) && !File.Exists(settings) && !LoaderFlags.SuiteLoaderInstalled, "the Manager's own Slotsloader: nothing written");
            f.Engine.Uninstall(fork.Id);
            ReplaceChecks.SameSnapshot(original, ReplaceChecks.Snapshot(f.Game), "fork loader uninstalled");

            File.WriteAllText(metadata, "name=SF6 Slots Loader\r\nauthor=Wael3rd\r\nversion=1.27.3\r\nSlotsloaderContract=2\r\n");
            ReplaceChecks.Put(f.Mods, "tool/Tool Pack/" + LoaderFlags.StageData, Encoding.UTF8.GetBytes("stage paths"));
            ReplaceMod suite = ReplaceChecks.Install(f, "tool", "Suite loader");
            // the six switches that are always a choice; the seventh (the costume options, second in the table) is written
            // only once it was chosen in the Manager (CostumeOptions below), so its key is "not set" in most lines here
            LoaderMenu[] six = LoaderMenu.All.Where(m => m.Unchosen == null).ToArray();
            // the game folder's menu keys, one character each in LoaderMenu.All's order: 1 on, 0 off, - not set
            Func<string> menus = delegate { return String.Concat(LoaderMenu.All.Select(m => { bool? v = LoaderFlags.Read(settings, m.Key); return v == null ? "-" : v.Value ? "1" : "0"; })); };
            Require(LoaderMenu.All.Length == 7 && LoaderMenu.All.Select(m => m.Key).Distinct().Count() == 7 && LoaderMenu.All.Select(m => m.Id).Distinct().Count() == 7
                && LoaderMenu.All.Select(m => m.Label).Distinct().Count() == 7 && LoaderMenu.All.All(m => m.Key.StartsWith("menu_") && m.Key != LoaderFlags.MenuKey), "seven additions, each with its own name, key and words");
            // 13 Nov 2026: the Display NSFW Outfits row has no switch (nothing is NSFW in Manager mode, the loader leaves the row out)
            Require(LoaderMenu.ById("nsfw") == null && !LoaderMenu.All.Any(m => m.Key == "menu_nsfw_row" || m.Label.IndexOf("NSFW", StringComparison.OrdinalIgnoreCase) >= 0) && LoaderFlags.RetiredMenuKeys.Contains("menu_nsfw_row"), "no switch for the NSFW row");
            Require(LoaderFlags.SuiteLoaderInstalled && LoaderFlags.Read(state, LoaderFlags.UpdateKey) == false && LoaderFlags.Read(state, LoaderFlags.UpdateManagedKey) == false
                && LoaderFlags.Read(settings, LoaderFlags.MenuKey) == false && menus() == "0-00000", "suite loader installed: no update check, nothing added to the menus, the costume options key not written: " + menus());
            // 13 Nov 2026 (the user: "by default, let the outfits others see as drive tech"): a settings.json without the
            // "online_outfit" key gets DriveTech from the Manager, first in the file
            Require(File.ReadAllText(settings) == "{\n  \"online_outfit\": \"DriveTech\",\n  \"menu_additions\": false,\n" + String.Join(",\n", six.Select(m => "  \"" + m.Key + "\": false")) + "\n}\n", "a new settings.json: the outfit others see, then the table's order: " + File.ReadAllText(settings));
            Require(LoaderFlags.OthersSeeDefault == "DriveTech" && LoaderFlags.OthersSee(f.Game, f.Data) == "DriveTech" && LoaderFlags.HasKey(settings, "online_outfit") && !LoaderFlags.HasKey(settings, "display_nsfw"), "DriveTech is the Manager's default");
            DateTime written = File.GetLastWriteTimeUtc(settings);
            Require(!LoaderFlags.Sync(f.Engine) && File.GetLastWriteTimeUtc(settings) == written, "a second sync writes nothing");

            // the plugin wrote its own settings since: its keys stay, ours come back
            File.WriteAllText(settings, plugin);
            Require(LoaderFlags.Sync(f.Engine) && File.ReadAllText(settings) == "{\n    \"menu_additions\": false," + String.Concat(six.Select(m => "\n    \"" + m.Key + "\": false,")) + plugin.Substring(1),
                "the plugin's own file gets the keys and keeps the rest: " + File.ReadAllText(settings));
            // the user picks two of them: only those reach the game, the update check inside the game stays off (not a choice)
            LoaderFlags.Save(f.Data, new LoaderFlagChoices { OnlineOutfitRow = true, ReplaySettings = true });
            Require(LoaderFlags.Sync(f.Engine) && menus() == "0-01001" && LoaderFlags.Read(settings, LoaderFlags.MenuKey) == false && LoaderFlags.Read(state, LoaderFlags.UpdateKey) == false
                && LoaderFlags.Read(state, LoaderFlags.UpdateManagedKey) == false && File.ReadAllText(settings).EndsWith(plugin.Substring(1)), "two additions chosen: " + menus());
            LoaderFlags.Save(f.Data, new LoaderFlagChoices { OutfitNames = true, FighterSettingsName = true, TitleScreen = true, InterfacePacks = true });
            Require(LoaderFlags.Sync(f.Engine) && menus() == "1-10110" && LoaderFlags.Read(settings, LoaderFlags.MenuKey) == false, "the other four: " + menus());
            // the game folder still holds only the first trial build's single key: the six are added beside it
            File.WriteAllText(settings, "{ \"display_nsfw\": false, \"menu_additions\": false }");
            Require(LoaderFlags.Sync(f.Engine) && menus() == "1-10110" && LoaderFlags.Read(settings, LoaderFlags.MenuKey) == false && LoaderFlags.Read(settings, "display_nsfw") == false,
                "a settings.json of the first trial build gets the six keys: " + File.ReadAllText(settings));
            // the game folder holds the seven keys of the trial build of 12 Nov 2026: the NSFW row's key is taken out again, on
            // its own line, as the last key and in a compact file; the plugin's own keys and the layout stay
            string sixLines = String.Concat(six.Select(m => "    \"" + m.Key + "\": " + (m.Get(LoaderFlags.Load(f.Data)) ? "true" : "false") + ",\n"));
            File.WriteAllText(settings, "{\n    \"menu_additions\": false,\n    \"menu_nsfw_row\": true,\n" + sixLines + plugin.Substring(2));
            Require(LoaderFlags.Sync(f.Engine) && File.ReadAllText(settings) == "{\n    \"menu_additions\": false,\n" + sixLines + plugin.Substring(2) && LoaderFlags.Read(settings, "menu_nsfw_row") == null,
                "the retired NSFW key goes, its line with it: " + File.ReadAllText(settings));
            Require(LoaderFlags.WithoutKeys("{\n  \"a\": 1,\n  \"menu_nsfw_row\": false\n}\n", LoaderFlags.RetiredMenuKeys) == "{\n  \"a\": 1\n}\n"
                && LoaderFlags.WithoutKeys("{ \"menu_nsfw_row\": false, \"a\": 1 }", LoaderFlags.RetiredMenuKeys) == "{ \"a\": 1 }" && Obj(LoaderFlags.WithoutKeys("{\"menu_nsfw_row\":true}", LoaderFlags.RetiredMenuKeys)).Count == 0
                && LoaderFlags.WithoutKeys(plugin, LoaderFlags.RetiredMenuKeys) == plugin && LoaderFlags.WithoutKeys("garbage", LoaderFlags.RetiredMenuKeys) == "garbage" && LoaderFlags.WithoutKeys(null, LoaderFlags.RetiredMenuKeys) == null
                && LoaderFlags.WithoutKeys("{ \"menu_nsfw_row\": \"text\" }", LoaderFlags.RetiredMenuKeys) == "{ \"menu_nsfw_row\": \"text\" }", "taking a key out: last key, compact, only key, none, not an object, not a boolean");
            // a choices file of that build (it has NsfwRow) reads as the other six
            File.WriteAllText(Path.Combine(f.Data, LoaderFlags.FileName), "{\"Format\":\"sf6-loader-flags-v1\",\"OutfitNames\":true,\"NsfwRow\":true,\"ReplaySettings\":true}");
            Require(LoaderFlags.Sync(f.Engine) && menus() == "1-00001", "a choices file with NsfwRow: " + menus());
            // a choices file of the first trial build (one switch for everything, and the update check as a choice): every
            // addition on, the update check off whatever it says
            File.WriteAllText(Path.Combine(f.Data, LoaderFlags.FileName), "{\"Format\":\"sf6-loader-flags-v1\",\"UpdateCheckInGame\":true,\"MenuAdditions\":true}");
            File.WriteAllText(state, "{\n  \"check_for_updates\": true\n}\n");
            LoaderFlagChoices old = LoaderFlags.Load(f.Data);
            Require(six.All(m => m.Get(old)) && old.OutfitOptions == null && !old.MenuAdditions && LoaderFlags.Sync(f.Engine) && menus() == "1-11111" && LoaderFlags.Read(settings, LoaderFlags.MenuKey) == false
                && LoaderFlags.Read(state, LoaderFlags.UpdateKey) == false && LoaderFlags.Read(state, LoaderFlags.UpdateManagedKey) == false,
                "the first trial build's choices: every addition of that build on (the costume options, which came later, not chosen), its update check ignored: " + menus());
            LoaderFlags.Save(f.Data, new LoaderFlagChoices());
            Require(LoaderFlags.Sync(f.Engine) && menus() == "0-00000" && LoaderFlags.Read(state, LoaderFlags.UpdateKey) == false, "and all off again");
            Require(LoaderMenu.ById("Online-Outfit") == LoaderMenu.All[3] && LoaderMenu.ById("replays") == LoaderMenu.All[6] && LoaderMenu.ById("options") == LoaderMenu.All[1] && LoaderMenu.All[1].Key == "menu_outfit_options" && LoaderMenu.ById("stage-select") == null, "a menu by its name on the command line");
            CostumeOptions(f, settings, plugin, menus);
            // 13 Nov 2026: "Outfit Others See" set from the Manager (the plugin's "online_outfit", a text)
            Require(LoaderFlags.WithText(null, "online_outfit", "DriveTech") == "{\n  \"online_outfit\": \"DriveTech\"\n}\n", "a new file: " + LoaderFlags.WithText(null, "online_outfit", "DriveTech"));
            Require(LoaderFlags.WithText(plugin, "online_outfit", "DriveTech") == plugin.Replace("\"Outfit 1\"", "\"DriveTech\"") && LoaderFlags.WithText(plugin, "online_outfit", "Outfit 1") == plugin,
                "a text replaced in place, the same text writes nothing new: " + LoaderFlags.WithText(plugin, "online_outfit", "DriveTech"));
            const string noKey = "{\n    \"display_nsfw\": false\n}\n";
            Require(LoaderFlags.WithText(noKey, "online_outfit", "DriveTech") == "{\n    \"online_outfit\": \"DriveTech\"," + noKey.Substring(1), "a text key added first, in the file's indentation: " + LoaderFlags.WithText(noKey, "online_outfit", "DriveTech"));
            Dictionary<string, object> retyped = Obj(LoaderFlags.WithText("{\"online_outfit\": 4, \"keep\": 7}", "online_outfit", "DriveTech"));
            Require(retyped != null && (string)retyped["online_outfit"] == "DriveTech" && Convert.ToInt32(retyped["keep"]) == 7 && LoaderFlags.WithText("garbage", "online_outfit", "DriveTech") == null,
                "a key of another type is set and the others kept; not a JSON object: refused");
            Require(LoaderFlags.OthersSeeValue("drivetech") == "DriveTech" && LoaderFlags.OthersSeeValue("Outfit 3") == "Outfit 1" && LoaderFlags.OthersSeeValue(null) == "Outfit 1", "DriveTech in any case, else Outfit 1, as the plugin reads it");
            // in the game folder: written once, then the file is the truth (the game's own row may change it later)
            File.WriteAllText(settings, plugin);
            LoaderFlags.Sync(f.Engine);
            Require(LoaderFlags.OthersSee(f.Game, f.Data) == "Outfit 1", "what the game folder holds is what the Manager shows");
            LoaderFlagChoices seen = LoaderFlags.Load(f.Data); seen.OthersSeePending = "DriveTech"; LoaderFlags.Save(f.Data, seen);
            Require(LoaderFlags.OthersSee(f.Game, f.Data) == "DriveTech" && LoaderFlags.ReadText(settings, "online_outfit") == "Outfit 1", "a change that waits is what the Manager shows");
            Require(LoaderFlags.Sync(f.Engine) && LoaderFlags.ReadText(settings, "online_outfit") == "DriveTech" && LoaderFlags.Load(f.Data).OthersSeePending == null && menus() == "0-00000"
                && File.ReadAllText(settings).Contains("\"select_options_key\": \"C\""), "the change is written once, the other keys kept: " + File.ReadAllText(settings));
            File.WriteAllText(settings, File.ReadAllText(settings).Replace("\"DriveTech\"", "\"Outfit 1\""));   // the game's own row changed it since
            Require(!LoaderFlags.Sync(f.Engine) && LoaderFlags.OthersSee(f.Game, f.Data) == "Outfit 1", "a later change made in the game is kept");
            // the default is for a file without the key only: Outfit 1 chosen (by the user here, or in the game) stays, and a key
            // that went missing gets the default again
            File.WriteAllText(settings, "{\n    \"display_nsfw\": false\n}\n");
            Require(LoaderFlags.OthersSee(f.Game, f.Data) == "DriveTech" && LoaderFlags.Sync(f.Engine) && LoaderFlags.ReadText(settings, "online_outfit") == "DriveTech" && LoaderFlags.Read(settings, "display_nsfw") == false,
                "no key: DriveTech is written beside the plugin's keys: " + File.ReadAllText(settings));
            seen = LoaderFlags.Load(f.Data); seen.OthersSeePending = "Outfit 1"; LoaderFlags.Save(f.Data, seen);
            Require(LoaderFlags.Sync(f.Engine) && LoaderFlags.ReadText(settings, "online_outfit") == "Outfit 1" && !LoaderFlags.Sync(f.Engine) && LoaderFlags.OthersSee(f.Game, f.Data) == "Outfit 1", "Outfit 1 chosen in the Manager stays over the default");
            File.WriteAllText(settings, File.ReadAllText(settings).Replace("\"Outfit 1\"", "7"));
            Require(!LoaderFlags.Sync(f.Engine) && LoaderFlags.OthersSee(f.Game, f.Data) == "Outfit 1", "a key of another type is the loader's Outfit 1 and is left alone");
            File.WriteAllText(settings, plugin); LoaderFlags.Sync(f.Engine);
            // a file that is not a settings object is left alone
            File.WriteAllText(state, "garbage");
            Require(!LoaderFlags.Sync(f.Engine) && File.ReadAllText(state) == "garbage", "an unreadable state.json is not replaced");
            File.Delete(state);
            Require(LoaderFlags.Sync(f.Engine) && LoaderFlags.Read(state, LoaderFlags.UpdateKey) == false, "a deleted state.json comes back at the next sync");

            // uninstalled: a file with other keys stays, one that holds only the Manager's keys goes
            f.Engine.Uninstall(suite.Id);
            Require(!LoaderFlags.SuiteLoaderInstalled && !File.Exists(state) && !Directory.Exists(Path.GetDirectoryName(state)) && File.Exists(settings)
                && File.ReadAllText(settings).Contains("\"display_nsfw\""), "uninstall: state.json (only the Manager's keys) removed, the plugin's settings kept");
            File.Delete(settings); Directory.Delete(Path.GetDirectoryName(settings)); Directory.Delete(Path.Combine(f.Game, "reframework", "data")); Directory.Delete(Path.Combine(f.Game, "reframework"));
            ReplaceChecks.SameSnapshot(original, ReplaceChecks.Snapshot(f.Game), "nothing else left behind");
            suite = ReplaceChecks.Install(f, "tool", "Suite loader");
            Require(File.Exists(state) && File.Exists(settings), "installed again");
            // a file that holds only the Manager's keys (the outfit others see among them) and a retired one goes at the uninstall too
            Require(LoaderFlags.ReadText(settings, "online_outfit") == "DriveTech", "installed again: the default is written again");
            File.WriteAllText(settings, File.ReadAllText(settings).Replace("{\n", "{\n  \"menu_nsfw_row\": false,\n"));
            f.Engine.Uninstall(suite.Id);
            ReplaceChecks.SameSnapshot(original, ReplaceChecks.Snapshot(f.Game), "uninstall leaves the game folder as it was");
            // the costume options chosen in the Manager: its key is one of the Manager's and goes with the file
            LoaderFlags.Save(f.Data, new LoaderFlagChoices { OutfitOptions = true });
            suite = ReplaceChecks.Install(f, "tool", "Suite loader");
            Require(LoaderFlags.Read(settings, "menu_outfit_options") == true, "installed again with the costume options chosen: " + File.ReadAllText(settings));
            f.Engine.Uninstall(suite.Id);
            ReplaceChecks.SameSnapshot(original, ReplaceChecks.Snapshot(f.Game), "uninstall with the costume options chosen leaves the game folder as it was");
            // set by hand and never chosen here, the key is not the Manager's: the file stays, the key as it was
            LoaderFlags.Save(f.Data, new LoaderFlagChoices());
            suite = ReplaceChecks.Install(f, "tool", "Suite loader");
            File.WriteAllText(settings, File.ReadAllText(settings).Replace("{\n", "{\n  \"menu_outfit_options\": true,\n"));
            f.Engine.Uninstall(suite.Id);
            Require(File.Exists(settings) && LoaderFlags.Read(settings, "menu_outfit_options") == true, "costume options set by hand: the settings file is not the Manager's alone and stays at the uninstall");
            File.Delete(settings); Directory.Delete(Path.GetDirectoryName(settings)); Directory.Delete(Path.Combine(f.Game, "reframework", "data")); Directory.Delete(Path.Combine(f.Game, "reframework"));
            ReplaceChecks.SameSnapshot(original, ReplaceChecks.Snapshot(f.Game), "nothing else left behind after the hand-set file");

            // the Native Core alone (beside the Manager's own Slotsloader): its updater is told, the plugin's settings are not touched
            ReplaceChecks.Put(f.Mods, "core/Native Core/modinfo.ini", Encoding.UTF8.GetBytes("name=SF6 Native Core\r\nauthor=Wael3rd\r\nversion=0.9.13\r\n"));
            ReplaceChecks.Put(f.Mods, "core/Native Core/" + LoaderFlags.CorePlugin, Encoding.UTF8.GetBytes("core"));
            ReplaceMod core = ReplaceChecks.Install(f, "core", "Native Core");
            Require(LoaderFlags.Read(state, LoaderFlags.UpdateKey) == false && !File.Exists(settings) && !LoaderFlags.SuiteLoaderInstalled, "Native Core alone: update check off, no menu setting");
            f.Engine.Uninstall(core.Id);
            ReplaceChecks.SameSnapshot(original, ReplaceChecks.Snapshot(f.Game), "Native Core uninstalled");
        }

        // 13 Nov 2026: the seventh switch, the in-game costume options ("menu_outfit_options"; only true turns it on).
        // The user's game had it set to true by hand before the Manager knew the key, and their choices file has no value
        // for it: a choice never made in the Manager is what the game folder holds and the Manager leaves the key alone;
        // only a choice made here (the box, the command line) writes it. The suite loader is installed; ends as it began
        // (nothing chosen, the plugin's own file synced).
        private static void CostumeOptions(ReplaceChecks.Fixture f, string settings, string plugin, Func<string> menus)
        {
            LoaderMenu options = LoaderMenu.ById("options");
            string choicesFile = Path.Combine(f.Data, LoaderFlags.FileName);
            Func<bool> on = delegate { return LoaderFlags.IsOn(options, LoaderFlags.Load(f.Data), f.Game); };
            // a choices file of an earlier build (no such field) and a game file with true written by hand
            File.WriteAllText(choicesFile, "{\"Format\":\"sf6-loader-flags-v1\",\"OutfitNames\":true,\"ReplaySettings\":true}");
            string byHand = plugin.Replace("{\n", "{\n    \"menu_outfit_options\": true,\n");
            File.WriteAllText(settings, byHand);
            LoaderFlagChoices old = LoaderFlags.Load(f.Data);
            Require(old.OutfitOptions == null && options.Unchosen(old) && !options.Get(old) && on(), "a choices file without the field: not chosen, and on because the game folder says so");
            Require(LoaderFlags.Sync(f.Engine) && LoaderFlags.Read(settings, options.Key) == true && menus() == "1100001" && File.ReadAllText(settings).EndsWith(byHand.Substring(1)) && on() && LoaderFlags.Load(f.Data).OutfitOptions == null,
                "true written by hand stays true after a sync, its line untouched: " + File.ReadAllText(settings));
            Require(!LoaderFlags.Sync(f.Engine) && LoaderFlags.Read(settings, options.Key) == true, "a second sync writes nothing");
            // another switch changes: saving the choices keeps this one not chosen, and the game's true stays
            LoaderFlagChoices again = LoaderFlags.Load(f.Data); again.TitleScreen = true; LoaderFlags.Save(f.Data, again);
            Require(LoaderFlags.Load(f.Data).OutfitOptions == null && LoaderFlags.Sync(f.Engine) && menus() == "1100101" && on(), "another switch saved: still not chosen, still on: " + menus());
            // no key anywhere: off, and the Manager does not write the key
            File.WriteAllText(settings, plugin);
            Require(LoaderFlags.Sync(f.Engine) && !LoaderFlags.HasKey(settings, options.Key) && !on() && menus() == "1-00101", "no key anywhere: off, the key left out: " + File.ReadAllText(settings));
            // false in the game folder and no choice: off, left as it is
            File.WriteAllText(settings, byHand.Replace("\"menu_outfit_options\": true", "\"menu_outfit_options\": false"));
            Require(LoaderFlags.Sync(f.Engine) && LoaderFlags.Read(settings, options.Key) == false && !on() && menus() == "1000101", "false in the game folder: off: " + menus());
            // a choice made in the Manager is written like the others: off over the game's true
            File.WriteAllText(settings, byHand); LoaderFlags.Sync(f.Engine);
            LoaderFlagChoices chosen = LoaderFlags.Load(f.Data); options.Set(chosen, false); LoaderFlags.Save(f.Data, chosen);
            Require(LoaderFlags.Load(f.Data).OutfitOptions == false && !on() && LoaderFlags.Sync(f.Engine) && LoaderFlags.Read(settings, options.Key) == false && File.ReadAllText(settings).Contains("\n    \"menu_outfit_options\": false,\n"),
                "switched off in the Manager: written false in place: " + File.ReadAllText(settings));
            // and on, into a file without the key and into a new file (in the table's order, after the mod names)
            options.Set(chosen, true); LoaderFlags.Save(f.Data, chosen);
            File.WriteAllText(settings, plugin);
            Require(LoaderFlags.Load(f.Data).OutfitOptions == true && on() && LoaderFlags.Sync(f.Engine) && LoaderFlags.Read(settings, options.Key) == true && menus() == "1100101" && !LoaderFlags.Sync(f.Engine), "switched on in the Manager: written true: " + menus());
            File.Delete(settings);
            Require(LoaderFlags.Sync(f.Engine) && File.ReadAllText(settings) == "{\n  \"online_outfit\": \"DriveTech\",\n  \"menu_additions\": false,\n" + String.Join(",\n", LoaderMenu.All.Select(m => "  \"" + m.Key + "\": " + (m.Get(chosen) ? "true" : "false"))) + "\n}\n"
                && File.ReadAllText(settings).Contains("\"menu_outfit_names\": true,\n  \"menu_outfit_options\": true,\n"), "a new settings.json with the costume options chosen: seven keys in the table's order: " + File.ReadAllText(settings));
            LoaderFlags.Save(f.Data, new LoaderFlagChoices());
            Require(LoaderFlags.Load(f.Data).OutfitOptions == null, "a saved choices file reads its unchosen switch as unchosen again");
            File.WriteAllText(settings, plugin); LoaderFlags.Sync(f.Engine);
        }
    }
}
