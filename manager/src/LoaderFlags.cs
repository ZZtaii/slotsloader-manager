using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace SlotsInstaller
{
    // 1.0.19: what the user wants of a Slots Loader of the SF6 Mod Suite line (Data\loader-flags.json). Off unless the
    // user turns it on: the game's menus stay the game's. The update check inside the game is not a choice (the user,
    // 12 Nov 2026: "I don't even want it as option"): the Manager always keeps it off, its users update in the Manager.
    public sealed class LoaderFlagChoices
    {
        public string Format = "sf6-loader-flags-v1";
        // One switch per thing the loader adds to the game's menus (LoaderMenu.All; the user, 12 Nov 2026: "split in
        // different options so i can pick and choose"). False: left out of the game.
        // (13 Nov 2026: no switch for the Display NSFW Outfits row any more. In Manager mode the loader marks no outfit
        // NSFW and leaves the row out, so there is nothing for it to show. A choices file that still has "NsfwRow" reads fine.)
        public bool OutfitNames, FighterSettingsName, OnlineOutfitRow, TitleScreen, InterfacePacks, ReplaySettings;
        // 13 Nov 2026 (the user's game already had "menu_outfit_options": true written by hand before this switch was
        // there, and it must not be switched off behind their back): the in-game costume options. Null: never chosen in
        // the Manager (a choices file of an earlier build has no such field). The switch then is what the game folder's
        // settings.json holds, and the Manager leaves that key alone (LoaderFlags.IsOn, MenuKeys). True or false: chosen
        // here (the box or the command line), and written like the others.
        public bool? OutfitOptions;
        // The single switch of the first trial build: read once (LoaderFlags.Load turns every switch of that build on
        // for it), never written true again.
        public bool MenuAdditions;
        // 13 Nov 2026 (the user: set the loader's own game settings from the Manager, so their rows need not be added to
        // the game's menus): the game outfit other players see in place of an extra outfit ("Outfit 1" or "DriveTech"),
        // as chosen in the Manager and not written to the game folder yet; null once written. Not a standing choice:
        // the game's own "Outfit Others See" row, when shown, writes the same key, and the last change wins.
        public string OthersSeePending;
    }

    // One thing a suite-line Slots Loader adds to the game's menus: its name on the command line, its key in the
    // plugin's settings.json, its words in Settings, and its switch in LoaderFlagChoices.
    internal sealed class LoaderMenu
    {
        internal readonly string Id, Key, Label;
        // 13 Nov 2026 (the user: "help users learn what is what"): what the addition is, shown beside its picture on
        // Settings > Slotsloader (MainFormLoaderTab.cs).
        internal readonly string About;
        internal readonly Func<LoaderFlagChoices, bool> Get;
        internal readonly Action<LoaderFlagChoices, bool> Set;
        // Null for a switch that is always a choice (off until ticked). Else: true while it was never chosen in the
        // Manager, and it then is what the game folder holds (LoaderFlags.IsOn; only "options").
        internal readonly Func<LoaderFlagChoices, bool> Unchosen;
        private LoaderMenu(string id, string key, string label, string about, Func<LoaderFlagChoices, bool> get, Action<LoaderFlagChoices, bool> set, Func<LoaderFlagChoices, bool> unchosen = null) { Id = id; Key = key; Label = label; About = about; Get = get; Set = set; Unchosen = unchosen; }

        // Options rows and sections carry the names the game shows them under.
        internal static readonly LoaderMenu[] All =
        {
            new LoaderMenu("names", "menu_outfit_names", "Mod names in the costume lists", "The game's costume lists show each extra outfit under its mod's name. Long names scroll. Unticked, extra outfits keep the game's numbered names. A change rebuilds the outfits once at the next start.", c => c.OutfitNames, (c, v) => c.OutfitNames = v),
            // 13 Nov 2026: the seventh switch, after "names" because both change the outfits themselves (the loader's
            // "menu_outfit_options": only true turns it on).
            new LoaderMenu("options", "menu_outfit_options", "Costume options in the game", "Outfits installed with all their variants, and outfits with add-ons, keep their options. On the Fighter Settings screen, with the cursor on the Costume row, the guide bar at the bottom shows \"Costume options\" (F or a click), where they are switched. A change rebuilds the outfits once at the next start.", c => c.OutfitOptions == true, (c, v) => c.OutfitOptions = v, c => c.OutfitOptions == null),
            new LoaderMenu("fighter-settings", "menu_fighter_settings_name", "Mod name on Fighter Settings", "The Fighter Settings screen shows the mod's name for the extra outfit you picked.", c => c.FighterSettingsName, (c, v) => c.FighterSettingsName = v),
            new LoaderMenu("online-outfit", "menu_online_outfit_row", "Outfit Others See", "Adds the row \"Outfit Others See\" to the game's Options, under Interface, Other Settings, so it can be changed inside the game. The choice below sets the same thing from here.", c => c.OnlineOutfitRow, (c, v) => c.OnlineOutfitRow = v),
            new LoaderMenu("title-screen", "menu_title_screen", "Title Screen Selection", "Adds the \"Title Screen Selection\" section and the \"Skip intro screens\" row to the game's Options. Both need a title pack in the game's title_mods folder.", c => c.TitleScreen, (c, v) => c.TitleScreen = v),
            new LoaderMenu("interface-packs", "menu_interface_packs", "Interface Packs", "Adds the \"Interface Packs\" section and its editor to the game's Options, for packs in the game's ui_mods folder.", c => c.InterfacePacks, (c, v) => c.InterfacePacks = v),
            new LoaderMenu("replays", "menu_replay_settings", "Settings tab in the Replays menu", "Adds a settings tab to the game's Replays menu.", c => c.ReplaySettings, (c, v) => c.ReplaySettings = v),
        };
        // The outfit other players see (LoaderFlags.OthersSee): not a menu addition, but shown the same way.
        internal const string OthersSeeId = "others-see", OthersSeeLabel = "Outfit other players see";
        internal const string OthersSeeAbout = "While you wear an extra outfit, other players see one of the game's own outfits in its place: DriveTech (Outfit 1 for a fighter without it), or Outfit 1 for everyone. The Manager sets DriveTech until you choose.";
        internal static LoaderMenu ById(string id) { return All.FirstOrDefault(m => String.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase)); }
    }

    // 1.0.19 (the user: no updater and no added menus in the game unless asked for). The Slots Loader of the SF6 Mod
    // Suite line (contract 2, 1.18.0 and later) and its Native Core read two settings from the game folder:
    //   reframework\data\SF6_Update_data\state.json           "check_for_updates", "check_in_manager_mode"
    //   reframework\data\SF6_CostumeSlots_data\settings.json  one "menu_..." key per addition (LoaderMenu.All), and
    //                                                         "menu_additions": false for any this Manager has no
    //                                                         switch for yet (a newer loader's)
    // The Manager keeps them as the user chose, the way it keeps swap_repairs.txt and manager_installs.json: after
    // every committed change (ReplaceEngine.Execute), at start-up and at every refresh (MainForm), and when a choice
    // changes. Only those keys are set: every other key and the file's layout stay as they are. A file that is not
    // a JSON object is left alone. Nothing is written while SF6 runs (the plugin writes settings.json itself), nor
    // for a loader the Manager did not install. Once neither that loader nor the Native Core is installed, a file
    // that holds nothing but the Manager's keys is removed again, so an uninstall leaves the game folder as it was.
    // Never fails a change: a problem is logged.
    internal static class LoaderFlags
    {
        internal const string FileName = "loader-flags.json";
        internal const string CorePlugin = "reframework/plugins/sf6core_native.dll";
        internal const string StageData = "reframework/data/SF6_StageSlots_Data/loader/stage_paths.txt";
        internal const string UpdateStateRelative = @"reframework\data\SF6_Update_data\state.json";
        internal const string PluginSettingsRelative = @"reframework\data\SF6_CostumeSlots_data\settings.json";
        internal const string UpdateKey = "check_for_updates", UpdateManagedKey = "check_in_manager_mode", MenuKey = "menu_additions";
        // Keys an earlier build of this Manager wrote and no loader reads any more: taken out of the plugin's settings.json
        // again ("menu_nsfw_row": the switch of the Display NSFW Outfits row, trial builds of 12 Nov 2026 only).
        internal static readonly string[] RetiredMenuKeys = { "menu_nsfw_row" };
        // "Outfit Others See" (the plugin's settings.json, read at the game's start): "DriveTech" in any case is DriveTech,
        // every other text and a missing key are Outfit 1, as the plugin reads it.
        internal const string OthersSeeKey = "online_outfit", OthersSeeOutfit1 = "Outfit 1", OthersSeeDriveTech = "DriveTech";
        internal static string OthersSeeValue(string text) { return String.Equals((text ?? "").Trim(), OthersSeeDriveTech, StringComparison.OrdinalIgnoreCase) ? OthersSeeDriveTech : OthersSeeOutfit1; }
        // The user, 13 Nov 2026: "by default, let the outfits others see as drive tech". The loader's own default is
        // Outfit 1 (a file without the key), so for a loader it installed the Manager writes DriveTech into a settings.json
        // that has no "online_outfit" key at all (Apply). A key that is there, whoever wrote it, is never replaced by this.
        internal const string OthersSeeDefault = OthersSeeDriveTech;
        // What other players see: the Manager's change that waits to be written, else what the game folder holds, else
        // (no key there yet) the Manager's default, which the next Apply writes.
        internal static string OthersSee(string gameRoot, string dataDir)
        {
            string pending = Load(dataDir).OthersSeePending;
            if (pending != null) return OthersSeeValue(pending);
            string path = String.IsNullOrEmpty(gameRoot) ? null : Path.Combine(gameRoot, PluginSettingsRelative);
            return path == null || !HasKey(path, OthersSeeKey) ? OthersSeeDefault : OthersSeeValue(ReadText(path, OthersSeeKey));
        }
        internal static readonly Version SuiteFrom = new Version(1, 18, 0);
        // The loader the last Apply saw is of the suite line: "Load order" stays off on it (its registry and plugin
        // state have another shape than the fork's, which OutfitOrder rewrites).
        internal static volatile bool SuiteLoaderInstalled;

        internal static LoaderFlagChoices Load(string dataDir)
        {
            try
            {
                string path = Path.Combine(dataDir ?? "", FileName);
                LoaderFlagChoices c = File.Exists(path) ? Json.Read<LoaderFlagChoices>(path) : null;
                if (c == null) return new LoaderFlagChoices();
                // the first trial build's single switch: the additions of that build (the costume options came later and stay unchosen)
                if (c.MenuAdditions) { foreach (LoaderMenu m in LoaderMenu.All) if (m.Unchosen == null) m.Set(c, true); c.MenuAdditions = false; }
                return c;
            }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); return new LoaderFlagChoices(); }
        }
        internal static void Save(string dataDir, LoaderFlagChoices choices) { Directory.CreateDirectory(dataDir); Json.Write(Path.Combine(dataDir, FileName), choices); }
        // A switch as it stands: the choice made in the Manager. One never chosen here (LoaderMenu.Unchosen) is what the
        // plugin's settings.json in the game folder holds: true there is on, false or no key is off.
        internal static bool IsOn(LoaderMenu menu, LoaderFlagChoices choices, string gameRoot)
        {
            if (menu.Unchosen == null || !menu.Unchosen(choices)) return menu.Get(choices);
            return !String.IsNullOrEmpty(gameRoot) && Read(Path.Combine(gameRoot, PluginSettingsRelative), menu.Key) == true;
        }

        private static bool Has(ReplaceMod m, string path) { return m != null && m.Files != null && m.Files.Any(f => f != null && String.Equals((f.Path ?? "").Replace('\\', '/').TrimStart('/'), path, StringComparison.OrdinalIgnoreCase)); }
        // An enabled Slots Loader of the suite line: by its version when the Manager knows it, else by the stage data
        // only that line ships.
        internal static bool IsSuiteLoader(ReplaceMod mod)
        {
            if (mod == null || !mod.Enabled || !LoaderCostumePak.IsLoaderMod(mod)) return false;
            string source;
            Version v = UpdateCheck.ParseVersion(InstalledLoader.Version(mod, out source));
            return v != null ? v >= SuiteFrom : Has(mod, StageData);
        }
        internal static bool HasNativeCore(ReplaceState state) { return state != null && state.Mods.Any(m => m != null && m.Enabled && Has(m, CorePlugin)); }

        // True when a file in the game folder changed. `partsLater` (a box ticked in Settings, on the window's thread): the
        // added-parts list, which may read mods and copy files, is made in the background (ManagerParts.ApplyLater), and
        // the result does not count it.
        internal static bool Apply(string gameRoot, string dataDir, ReplaceState state, bool partsLater = false)
        {
            try
            {
                if (String.IsNullOrEmpty(gameRoot) || state == null) return false;
                bool loader = state.Mods.Any(IsSuiteLoader), core = HasNativeCore(state);
                SuiteLoaderInstalled = loader;
                if (!Directory.Exists(Path.Combine(gameRoot, "reframework"))) return false;
                LoaderFlagChoices want = Load(dataDir);
                // 13 Nov 2026: the parts added to outfits, for the loader to offer them in the game (ManagerParts.cs): listed
                // while this loader is installed and "Costume options in the game" is on, else the list is removed. Also
                // while SF6 runs: the loader reads it at its next start only, and the outfits may have changed meanwhile.
                bool partsWanted = loader && IsOn(LoaderMenu.ById("options"), want, gameRoot), changed = false;
                if (partsLater) ManagerParts.ApplyLater(gameRoot, partsWanted);
                else changed = ManagerParts.Apply(gameRoot, partsWanted);
                if (GameProcesses.Probe.IsGameRunning()) return changed;   // the settings: applied at the next refresh
                // The update check: always off, never a choice.
                changed |= Sync(Path.Combine(gameRoot, UpdateStateRelative), gameRoot, loader || core,
                    new[] { new KeyValuePair<string, bool>(UpdateKey, false), new KeyValuePair<string, bool>(UpdateManagedKey, false) });
                changed |= Sync(Path.Combine(gameRoot, PluginSettingsRelative), gameRoot, loader, MenuKeys(want), RetiredMenuKeys, new[] { OthersSeeKey });
                string pluginSettings = Path.Combine(gameRoot, PluginSettingsRelative);
                if (loader && want.OthersSeePending != null) changed |= WriteOthersSee(pluginSettings, dataDir, want);
                else if (loader && !HasKey(pluginSettings, OthersSeeKey)) changed |= WriteOthersSee(pluginSettings, OthersSeeDefault);   // the Manager's default, once: the key is there from then on
                return changed;
            }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.LogLine("Loader settings not written (" + ex.GetType().Name + ": " + ex.Message + ")."); return false; }
        }
        // The plugin's settings.json keys for these choices: each addition's own key, and "menu_additions": false, which
        // the loader takes for an addition whose key is not in the file (one a newer loader brings: off until this
        // Manager has a switch for it). Listed last to first: WithKeys puts a new key first, so a new file reads in
        // the table's order. A switch never chosen in the Manager (LoaderMenu.Unchosen) has no key here: what the game
        // folder holds for it stays as it is (true written by hand stays true, no key stays no key, which the loader
        // reads as off), and it is not one of the Manager's keys at an uninstall.
        internal static KeyValuePair<string, bool>[] MenuKeys(LoaderFlagChoices want)
        {
            var keys = new List<KeyValuePair<string, bool>> { new KeyValuePair<string, bool>(MenuKey, false) };
            keys.AddRange(LoaderMenu.All.Where(m => m.Unchosen == null || !m.Unchosen(want)).Select(m => new KeyValuePair<string, bool>(m.Key, m.Get(want))));
            keys.Reverse();
            return keys.ToArray();
        }
        internal static bool Sync(ReplaceEngine engine, bool partsLater = false)
        {
            try { return engine != null && Apply(engine.GameRoot, engine.DataDir, engine.LoadState(), partsLater); }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.LogLine("Loader settings not synced (" + ex.GetType().Name + ")."); return false; }
        }

        // The change made in the Manager goes into the plugin's settings.json once; the file is the truth from then on.
        // A file that is not a settings object keeps the change waiting (Sync has logged it).
        private static bool WriteOthersSee(string path, string dataDir, LoaderFlagChoices want)
        {
            bool? wrote = WriteOthersSeeText(path, OthersSeeValue(want.OthersSeePending));
            if (wrote == null) return false;
            want.OthersSeePending = null;
            Save(dataDir, want);
            return wrote.Value;
        }
        private static bool WriteOthersSee(string path, string value) { return WriteOthersSeeText(path, value) == true; }
        // True: the file was written; false: it already said so; null: it is not a settings object (left alone).
        private static bool? WriteOthersSeeText(string path, string value)
        {
            string text = File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : null;
            string next = WithText(text, OthersSeeKey, value);
            if (next == null) return null;
            if (next == text) return false;
            Safety.NoReparseChain(path);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            DurableFile.Write(path, new UTF8Encoding(false).GetBytes(next));
            return true;
        }
        // A top-level key of that name is in the file, whatever its value (false: no file, or not a settings object).
        internal static bool HasKey(string path, string key)
        {
            try
            {
                if (!File.Exists(path)) return false;
                Dictionary<string, object> o = Parse(File.ReadAllText(path, Encoding.UTF8).TrimStart('﻿'));
                return o != null && o.ContainsKey(key);
            }
            catch (Exception ex) { if (ex is IOException || ex is UnauthorizedAccessException) return false; throw; }
        }

        // `own`: other keys the Manager may have written into this file (not booleans), which do not keep it at an uninstall.
        private static bool Sync(string path, string gameRoot, bool wanted, KeyValuePair<string, bool>[] keys, string[] retired = null, string[] own = null)
        {
            string text = File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : null;
            retired = retired ?? new string[0];
            if (!wanted)
            {
                if (text == null || !OnlyKeys(text, keys.Select(k => k.Key).Concat(retired).Concat(own ?? new string[0]))) return false;
                Safety.NoReparseChain(path);
                File.Delete(path);
                for (string dir = Path.GetDirectoryName(path); dir != null && dir.Length > gameRoot.Length && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any(); dir = Path.GetDirectoryName(dir))
                    Directory.Delete(dir);
                return true;
            }
            string next = WithKeys(WithoutKeys(text, retired), keys);
            if (next == null) { Program.LogLine(Path.GetFileName(path) + " in " + Path.GetFileName(Path.GetDirectoryName(path)) + " is not a settings object: left as it is."); return false; }
            if (next == text) return false;
            Safety.NoReparseChain(path);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            DurableFile.Write(path, new UTF8Encoding(false).GetBytes(next));
            return true;
        }

        private static Dictionary<string, object> Parse(string text)
        {
            try { return Json.Serializer().DeserializeObject(text) as Dictionary<string, object>; }
            catch (Exception ex) { if (ex is ArgumentException || ex is InvalidOperationException) return null; throw; }
        }
        // The value of a top-level boolean key, or null (no file, not an object, no such key, not a boolean).
        internal static bool? Read(string path, string key)
        {
            try
            {
                if (!File.Exists(path)) return null;
                Dictionary<string, object> o = Parse(File.ReadAllText(path, Encoding.UTF8).TrimStart('﻿'));
                object v;
                return o != null && o.TryGetValue(key, out v) && v is bool ? (bool?)(bool)v : null;
            }
            catch (Exception ex) { if (ex is IOException || ex is UnauthorizedAccessException) return null; throw; }
        }
        // The value of a top-level text key, or null (no file, not an object, no such key, not a text).
        internal static string ReadText(string path, string key)
        {
            try
            {
                if (!File.Exists(path)) return null;
                Dictionary<string, object> o = Parse(File.ReadAllText(path, Encoding.UTF8).TrimStart('\uFEFF'));
                object v;
                return o != null && o.TryGetValue(key, out v) ? v as string : null;
            }
            catch (Exception ex) { if (ex is IOException || ex is UnauthorizedAccessException) return null; throw; }
        }
        // `text` (a JSON object, or null/blank for a new file) with this key set to this text (letters, digits and spaces
        // only: nothing to escape). As WithKeys: a value already there is replaced in place, a new key goes first in the
        // file's own indentation, and `text` itself comes back when the key already has the value. Null: `text` is not a
        // JSON object.
        internal static string WithText(string text, string key, string value)
        {
            if (value == null || !Regex.IsMatch(value, "^[A-Za-z0-9 ]*$")) throw new ArgumentException("A plain text is needed.", "value");
            return WithValues(text, new[] { new KeyValuePair<string, object>(key, value) });
        }
        // What WithKeys and WithText share. A value is a bool or a plain text (nothing to escape). A key that already has
        // a value of the same kind has it replaced in place; a new key goes first, in the file's own indentation. The
        // edit is checked by reading it back: when it does not give the wanted object, the whole object is written again
        // (layout lost, keys kept). Null: `text` is not a JSON object.
        private static string WithValues(string text, IList<KeyValuePair<string, object>> values)
        {
            string t = text ?? "";
            if (t.Length > 0 && t[0] == '\uFEFF') t = t.Substring(1);
            Dictionary<string, object> before = t.Trim().Length == 0 ? new Dictionary<string, object>() : Parse(t);
            if (before == null) return null;
            var expected = new HashSet<string>(before.Keys);
            if (before.Count == 0) t = "{\n}\n";
            Func<object, string> json = v => v is bool ? ((bool)v ? "true" : "false") : "\"" + (string)v + "\"";
            if (values.All(kv => { object v; return before.TryGetValue(kv.Key, out v) && Object.Equals(v, kv.Value); })) return text;   // already so: the file as it is
            foreach (KeyValuePair<string, object> kv in values)
            {
                object old;
                if (before.TryGetValue(kv.Key, out old))
                {
                    if (Object.Equals(old, kv.Value)) continue;
                    string key = "\"" + Regex.Escape(kv.Key) + "\"\\s*:\\s*";
                    Match m = old is bool && kv.Value is bool ? Regex.Match(t, "(" + key + ")(true|false)\\b")
                        : old is string && kv.Value is string ? Regex.Match(t, "(" + key + "\")((?:[^\"\\\\]|\\\\.)*)\"") : Match.Empty;
                    if (!m.Success) { t = null; break; }   // another type, or not where the pattern looks: rebuilt below
                    string replaced = kv.Value is bool ? json(kv.Value) : (string)kv.Value;
                    t = t.Substring(0, m.Groups[2].Index) + replaced + t.Substring(m.Groups[2].Index + m.Groups[2].Length);
                    continue;
                }
                int brace = t.IndexOf('{');
                Match indent = Regex.Match(t, "\\n([ \\t]+)\"");
                bool empty = Regex.IsMatch(t.Substring(brace + 1), "^\\s*\\}");
                t = t.Substring(0, brace + 1) + "\n" + (indent.Success ? indent.Groups[1].Value : "  ") + "\"" + kv.Key + "\": " + json(kv.Value) + (empty ? "\n}" + t.Substring(t.IndexOf('}', brace) + 1) : "," + t.Substring(brace + 1));
                before[kv.Key] = kv.Value; expected.Add(kv.Key);
            }
            Dictionary<string, object> after = t == null ? null : Parse(t);
            if (after != null && after.Count == expected.Count && expected.All(after.ContainsKey) && values.All(kv => { object v; return after.TryGetValue(kv.Key, out v) && Object.Equals(v, kv.Value); })) return t;
            Dictionary<string, object> whole = (text ?? "").Trim().Length == 0 ? new Dictionary<string, object>() : Parse(text.TrimStart('\uFEFF'));
            if (whole == null) return null;
            foreach (KeyValuePair<string, object> kv in values) whole[kv.Key] = kv.Value;
            return Json.Serializer().Serialize(whole) + "\n";
        }
        private static bool OnlyKeys(string text, IEnumerable<string> keys)
        {
            Dictionary<string, object> o = Parse(text);
            return o != null && o.Keys.All(k => keys.Contains(k));
        }

        // `text` without these top-level boolean keys, the rest of the file as it is (a key's line goes with it). `text`
        // itself when it has none of them or is not a JSON object (WithKeys refuses that one).
        internal static string WithoutKeys(string text, IEnumerable<string> keys)
        {
            if (String.IsNullOrWhiteSpace(text)) return text;
            Dictionary<string, object> before = Parse(text.TrimStart('\uFEFF'));
            if (before == null) return text;
            List<string> gone = (keys ?? new string[0]).Where(k => before.ContainsKey(k) && before[k] is bool).ToList();
            if (gone.Count == 0) return text;
            string t = text;
            foreach (string key in gone)
            {
                string pair = "\"" + Regex.Escape(key) + "\"\\s*:\\s*(?:true|false)";
                // its own line with the comma after it; else the comma before it (the last key); else the pair alone (the only key)
                Match m = Regex.Match(t, "(?<=\\n)[ \\t]*" + pair + "[ \\t]*,[ \\t]*\\r?\\n");
                if (!m.Success) m = Regex.Match(t, pair + "\\s*,\\s*");
                if (!m.Success) m = Regex.Match(t, ",\\s*" + pair);
                if (!m.Success) m = Regex.Match(t, pair);
                if (m.Success) t = t.Remove(m.Index, m.Length);
            }
            Dictionary<string, object> after = Parse(t.TrimStart('\uFEFF'));
            if (after != null && after.Count == before.Count - gone.Count && !gone.Any(after.ContainsKey) && after.Keys.All(before.ContainsKey)) return t;
            foreach (string key in gone) before.Remove(key);   // the in-place edit did not give the wanted object: written again whole
            return Json.Serializer().Serialize(before) + "\n";
        }

        // `text` (a JSON object, or null/blank for a new file) with each key set to its value. A key already there
        // has its value replaced in place; a new one goes first, in the file's own indentation. Null: `text` is not
        // a JSON object.
        internal static string WithKeys(string text, IEnumerable<KeyValuePair<string, bool>> keys)
        {
            return WithValues(text, keys.Select(k => new KeyValuePair<string, object>(k.Key, k.Value)).ToList());
        }
    }

    // Slotsloader CLI.exe loader-flags: shows the settings, or changes what the loader adds to the game's menus (1.0.19).
    internal static class LoaderFlagsCommandLine
    {
        internal static int Run(string[] args)
        {
            string game = null;
            var changes = new List<KeyValuePair<LoaderMenu, bool>>();   // in the order given: a later one wins
            string othersSee = null;
            try
            {
                string names = String.Join(", ", LoaderMenu.All.Select(m => m.Id));
                for (int i = 1; i < args.Length; i++)
                {
                    if (args[i] == "--game" && i + 1 < args.Length) game = Path.GetFullPath(args[++i]);
                    else if (args[i] == "--menus" && i + 1 < args.Length)
                    {
                        string all = args[++i].ToLowerInvariant();
                        if (all != "on" && all != "off") throw new ArgumentException("--menus takes on or off.");
                        changes.AddRange(LoaderMenu.All.Select(m => new KeyValuePair<LoaderMenu, bool>(m, all == "on")));
                    }
                    else if ((args[i] == "--show" || args[i] == "--hide") && i + 1 < args.Length)
                    {
                        bool show = args[i] == "--show";
                        foreach (string id in args[++i].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()))
                        {
                            LoaderMenu m = LoaderMenu.ById(id);
                            if (m == null) throw new ArgumentException("Unknown menu: " + id + ". Use " + names + ".");
                            changes.Add(new KeyValuePair<LoaderMenu, bool>(m, show));
                        }
                    }
                    else if (args[i] == "--others-see" && i + 1 < args.Length)
                    {
                        string seen = args[++i].Replace(" ", "").Replace("-", "").ToLowerInvariant();
                        if (seen != "outfit1" && seen != "drivetech") throw new ArgumentException("--others-see takes outfit1 or drivetech.");
                        othersSee = seen == "drivetech" ? LoaderFlags.OthersSeeDriveTech : LoaderFlags.OthersSeeOutfit1;
                    }
                    else throw new ArgumentException("Unknown option: " + args[i] + ". Use --game <folder>, --menus on|off, --show <names>, --hide <names> (" + names + "), --others-see outfit1|drivetech.");
                }
            }
            catch (ArgumentException ex) { Console.Error.WriteLine(ex.Message); return 2; }
            try
            {
                AppConfig config = File.Exists(Path.Combine(AppConfig.DataDir, "settings.json")) ? AppConfig.Load() : AppConfig.Detected();
                if (game != null) config.GameRoot = game;
                if (!Directory.Exists(config.GameRoot)) throw new IOException("Game folder not found: " + config.GameRoot);
                ReplaceEngine engine = ReplaceEngine.ForConfig(config);
                LoaderFlagChoices choices = LoaderFlags.Load(engine.DataDir);
                ReplaceState state = engine.LoadState();
                bool loader = state.Mods.Any(LoaderFlags.IsSuiteLoader), core = LoaderFlags.HasNativeCore(state);
                if (othersSee != null && !loader) othersSee = null;   // no loader reads it: nothing to keep waiting (the last line says so)
                if (changes.Count > 0 || othersSee != null)
                {
                    foreach (KeyValuePair<LoaderMenu, bool> c in changes) c.Key.Set(choices, c.Value);
                    if (othersSee != null) choices.OthersSeePending = othersSee;
                    LoaderFlags.Save(engine.DataDir, choices);
                }
                bool running = GameProcesses.Probe.IsGameRunning();
                bool changed = LoaderFlags.Apply(engine.GameRoot, engine.DataDir, state);
                Console.WriteLine("Added to the game's menus by the loader:");
                string settings = Path.Combine(engine.GameRoot, LoaderFlags.PluginSettingsRelative);
                foreach (LoaderMenu m in LoaderMenu.All)
                    Console.WriteLine("  " + m.Id.PadRight(17) + (LoaderFlags.IsOn(m, choices, engine.GameRoot) ? "on " : "off") + " · " + InGame(LoaderFlags.Read(settings, m.Key)) + (m.Unchosen != null && m.Unchosen(choices) ? ", not chosen here" : "") + " · " + m.Label);
                if (loader) Console.WriteLine("Other players see your extra outfits as: " + LoaderFlags.OthersSee(engine.GameRoot, engine.DataDir) + (LoaderFlags.Load(engine.DataDir).OthersSeePending != null ? " · not written yet" : ""));
                Console.WriteLine("Update check inside the game: always off · " + InGame(LoaderFlags.Read(Path.Combine(engine.GameRoot, LoaderFlags.UpdateStateRelative), LoaderFlags.UpdateKey)));
                Console.WriteLine(loader ? "Slots Loader of the SF6 Mod Suite line installed by this Manager" + (core ? ", with the Native Core." : ".")
                    : core ? "Native Core installed by this Manager; the installed Slotsloader adds no menus."
                    : "No loader that reads these settings is installed by this Manager: nothing is written to the game folder.");
                if (running) Console.WriteLine("SF6 is running: the game folder is updated at the next refresh after it closes.");
                else if (changed) Console.WriteLine("Game folder updated · " + VariantsCommandLine.AppliesText(false) + ".");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine("ERROR: " + ex.Message); Program.Log(ex); return 1; }
        }
        private static string InGame(bool? value) { return value == null ? "not set in the game folder" : "in the game folder: " + (value.Value ? "on" : "off"); }
    }
}
