using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace SlotsInstaller
{
    // 1.0.24 (the user: "drop/reorder random outfits via a pre-decided logic that user themselves select. and how many
    // repetitions, color etc. we could even show a little preview for colors"; "if the order isn't present, then well it
    // can be random"). A fighter's own list for its Random outfit, read by the Slots Loader's plugin at each draw
    // (plugin random_logic.hpp, rnd::parse_lists): "order" plays the list from the top, each outfit its repeats in a row;
    // "shuffle" makes a deck of it, an outfit its repeats cards; each outfit's ticked colours (none: any). No list for a
    // fighter: Random draws from every outfit as before. An extra outfit is named by its mod (registry.json mod_id and the
    // outfit it was made from), because the loader can number it again; a game outfit by its number (0 = Outfit 1).
    // The plugin reads the file again when it changes, so SF6 may run. Every save keeps a copy for Undo.
    internal sealed class RandomOutfit
    {
        internal bool Game;                   // one of the game's outfits (no mod)
        internal int CostumeNo;               // the number now (0 = Outfit 1)
        internal string ModId = "";           // registry.json mod_id of an extra outfit
        internal int Base = -1;               // the outfit it was made from (registry original_costume_no)
        internal string Name = "", Label = "";   // "Outfit V", the loader's label of the mod
        internal string Source = "";              // outfits.json "source": the mod's folder or archive (variants share it)
        internal bool Nsfw;
        internal uint[][] Swatches;           // per colour its two squares (R in the low byte, as via.Color); null: none known
        internal int ColourCount = 10;        // an extra outfit has 10; a game outfit as many as its colour files (game_swatches.json)
        internal string Key { get { return Game ? "game:" + CostumeNo.ToString(CultureInfo.InvariantCulture) : "mod:" + ModId + ":" + Base.ToString(CultureInfo.InvariantCulture); } }
    }

    internal sealed class RandomListItem
    {
        internal RandomOutfit Outfit;
        internal int Repeat = 1;
        internal List<int> Colours = new List<int>();   // 0-9; empty: any
    }

    internal sealed class RandomPlan
    {
        internal bool Shuffle;
        internal List<RandomListItem> Items = new List<RandomListItem>();
        // What the file would hold for it: two plans are the same when this is.
        internal string Signature
        {
            get
            {
                return (Shuffle ? "shuffle" : "order") + "|" + String.Join(";", Items.Select(i => i.Outfit.Key + "x" + i.Repeat + "c" + String.Join(",", i.Colours.OrderBy(c => c))));
            }
        }
    }

    internal static class RandomList
    {
        internal const string Relative = @"reframework\data\SF6_CostumeSlots_data\random_list.json";
        internal const string Format = "sf6-random-list-1";
        internal const int MaxRepeat = 20;   // as the plugin's rnd::kMaxRepeat
        internal const int MaxColours = 64;  // colour numbers 0 to 63, as the plugin's rnd::kMaxColours (EX colours past 9)
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

        internal static string PathOf(string game) { return Path.Combine(game ?? "", Relative); }
        private static string DataPath(string game, string file) { return Path.Combine(game ?? "", OutfitOrder.DataRelative, file); }
        private static JavaScriptSerializer Serializer() { return new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue, RecursionLimit = 64 }; }

        private static Dictionary<string, object> ReadObject(string path)
        {
            try
            {
                if (!File.Exists(path) || new FileInfo(path).Length > 32L * 1024 * 1024) return null;
                return Serializer().DeserializeObject(File.ReadAllText(path, Encoding.UTF8)) as Dictionary<string, object>;
            }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); return null; }
        }
        private static IEnumerable<Dictionary<string, object>> Array(Dictionary<string, object> o, string key)
        {
            object v; if (o == null || !o.TryGetValue(key, out v)) yield break;
            var list = v as object[]; if (list == null) { var al = v as System.Collections.ArrayList; if (al != null) list = al.ToArray(); }
            if (list == null) yield break;
            foreach (object e in list) { var d = e as Dictionary<string, object>; if (d != null) yield return d; }
        }
        private static int Int(Dictionary<string, object> o, string key, int fallback)
        {
            object v; if (o == null || !o.TryGetValue(key, out v) || v == null || v is string || v is bool) return fallback;
            try { return Convert.ToInt32(v, CultureInfo.InvariantCulture); } catch (Exception) { return fallback; }
        }
        private static string Str(Dictionary<string, object> o, string key) { object v; return o != null && o.TryGetValue(key, out v) ? v as string ?? "" : ""; }
        private static bool Bool(Dictionary<string, object> o, string key) { object v; return o != null && o.TryGetValue(key, out v) && v is bool && (bool)v; }

        internal static int FighterId(string fighterDir)
        {
            int id; return fighterDir != null && fighterDir.Length > 3 && Int32.TryParse(fighterDir.Substring(3), NumberStyles.Integer, CultureInfo.InvariantCulture, out id) ? id : -1;
        }

        // The outfits a fighter's list can hold: the game's (its outfit tables), then the loader's extra outfits as the last
        // build numbered them (outfits.json, with their mod and swatches from registry.json). The Random outfit is not one.
        // replacedBy: the game outfits a Replace original mod replaces now (costume number -> the mod's name): such a row is
        // "Outfit 1 (the mod)", its colours as the loader read them with the mod in place (game_swatches.json).
        internal static List<RandomOutfit> Outfits(string game, string fighterDir, Dictionary<int, string> replacedBy = null)
        {
            var list = new List<RandomOutfit>();
            int fid = FighterId(fighterDir); if (fid < 0) return list;
            try
            {
                GameReference tables = GameReference.Tables(game);
                Dictionary<int, uint[][]> gameSwatches = GameSwatches(game, fighterDir);
                foreach (int no in tables.CostumeByFolder.Where(kv => kv.Key.StartsWith(fighterDir + "/", StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Value).Distinct().OrderBy(n => n))
                {
                    string by = null; bool swapped = replacedBy != null && replacedBy.TryGetValue(no, out by) && !String.IsNullOrEmpty(by);
                    var o = new RandomOutfit { Game = true, CostumeNo = no, Name = "Outfit " + (no + 1).ToString(CultureInfo.InvariantCulture) + (swapped ? " (" + by + ")" : ""),
                        Label = swapped ? "Game outfit, replaced by " + by : "Game outfit" };
                    uint[][] sw; if (gameSwatches.TryGetValue(no, out sw) && sw.Length > 0) { o.Swatches = sw; o.ColourCount = sw.Length; }
                    list.Add(o);
                }
                if (list.Count == 0) list.Add(new RandomOutfit { Game = true, CostumeNo = 0, Name = "Outfit 1", Label = "Game outfit" });

                Dictionary<string, object> registry = ReadObject(DataPath(game, "registry.json")), report = ReadObject(DataPath(game, "outfits.json"));
                var slots = Array(registry, "slots").Where(s => Int(s, "fighter", -1) == fid).ToList();
                var possession = Array(registry, "possession").Where(p => Int(p, "fighter", -1) == fid).ToList();
                var randoms = new HashSet<int>(possession.Where(p => Bool(p, "random")).Select(p => Int(p, "costume_no", -1)));
                var rows = Array(report, "outfits").Where(o => String.Equals(Str(o, "fighter_dir"), fighterDir, StringComparison.OrdinalIgnoreCase)).ToList();
                foreach (Dictionary<string, object> s in slots.OrderBy(s => Int(s, "new_costume_no", 0)))
                {
                    int no = Int(s, "new_costume_no", -1); string mod = Str(s, "mod_id");
                    if (no < 0 || mod.Length == 0 || randoms.Contains(no) || list.Any(o => !o.Game && o.CostumeNo == no)) continue;
                    Dictionary<string, object> row = rows.FirstOrDefault(r => Int(r, "costume_no", -1) == no);
                    Dictionary<string, object> pos = possession.FirstOrDefault(p => Int(p, "costume_no", -1) == no);
                    string name = Str(row, "name"); if (name.Length == 0) name = Str(pos, "name"); if (name.Length == 0) name = "Slot " + no.ToString(CultureInfo.InvariantCulture);
                    // The loader's label names the variant (a bundle's options share one source, its ZIP)
                    string label = Str(row, "label"); if (label.Length == 0) label = Str(row, "source");
                    list.Add(new RandomOutfit { CostumeNo = no, ModId = mod, Base = Int(s, "original_costume_no", -1), Name = name, Label = label.Replace('\\', '/'), Source = Str(row, "source").Replace('\\', '/'),
                        Nsfw = Bool(s, "nsfw") || Bool(pos, "nsfw"), Swatches = SwatchesOf(pos) });
                }
            }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); }
            return list;
        }
        // The game's own outfits' swatches the loader keeps (SF6_Costumes_Data\game_swatches.json, made again when the game's
        // paks change: a replaced outfit's are the mod's); costume number -> per colour its squares (null where none)
        internal static Dictionary<int, uint[][]> GameSwatches(string game, string fighterDir)
        {
            var map = new Dictionary<int, uint[][]>();
            Dictionary<string, object> file = ReadObject(DataPath(game, "game_swatches.json"));
            if (file == null || Str(file, "format") != "sf6-game-swatches-1") return map;
            foreach (Dictionary<string, object> o in Array(file, "outfits"))
            {
                if (!String.Equals(Str(o, "fighter_dir"), fighterDir, StringComparison.OrdinalIgnoreCase)) continue;
                int no = Int(o, "costume_no", -1); uint[][] sw = SwatchesOf(o);
                if (no >= 0 && sw != null) map[no] = sw;
            }
            return map;
        }

        private static uint[][] SwatchesOf(Dictionary<string, object> pos)
        {
            object v; if (pos == null || !pos.TryGetValue("swatches", out v)) return null;
            var outer = v as object[]; if (outer == null) { var al = v as System.Collections.ArrayList; if (al != null) outer = al.ToArray(); }
            if (outer == null || outer.Length == 0) return null;
            var result = new uint[outer.Length][];
            for (int i = 0; i < outer.Length; i++)
            {
                var pair = outer[i] as object[]; if (pair == null) { var al = outer[i] as System.Collections.ArrayList; if (al != null) pair = al.ToArray(); }
                result[i] = pair == null ? new uint[0] : pair.Select(x => { try { return Convert.ToUInt32(x, CultureInfo.InvariantCulture); } catch (Exception) { return 0u; } }).ToArray();
            }
            return result;
        }

        // The fighter's saved list, matched against its outfits now (an outfit no longer there is left out); null: none.
        internal static RandomPlan Load(string game, string fighterDir, List<RandomOutfit> outfits)
        {
            int fid = FighterId(fighterDir);
            Dictionary<string, object> file = ReadObject(PathOf(game)), fighters, entry;
            if (fid < 0 || file == null || Str(file, "format") != Format) return null;
            object v;
            if (!file.TryGetValue("fighters", out v) || (fighters = v as Dictionary<string, object>) == null) return null;
            if (!fighters.TryGetValue(fid.ToString(CultureInfo.InvariantCulture), out v) || (entry = v as Dictionary<string, object>) == null) return null;
            var plan = new RandomPlan { Shuffle = Str(entry, "mode") == "shuffle" };
            foreach (Dictionary<string, object> e in Array(entry, "outfits"))
            {
                RandomOutfit outfit;
                if (e.ContainsKey("game")) { int no = Int(e, "game", -1); outfit = outfits.FirstOrDefault(o => o.Game && o.CostumeNo == no); }
                else
                {
                    string mod = Str(e, "mod_id"); int b = Int(e, "base", -1), no = Int(e, "costume", -1);
                    var same = outfits.Where(o => !o.Game && o.ModId == mod).ToList();   // as the plugin's replay::resolve_mod
                    outfit = same.FirstOrDefault(o => b >= 0 && o.Base == b) ?? same.FirstOrDefault(o => o.CostumeNo == no) ?? same.FirstOrDefault();
                }
                if (outfit == null) continue;
                var item = new RandomListItem { Outfit = outfit, Repeat = Math.Max(1, Math.Min(MaxRepeat, Int(e, "repeat", 1))) };
                object cs; var colours = e.TryGetValue("colours", out cs) ? (cs as object[] ?? (cs is System.Collections.ArrayList ? ((System.Collections.ArrayList)cs).ToArray() : null)) : null;
                if (colours != null) foreach (object c in colours) { int n; try { n = Convert.ToInt32(c, CultureInfo.InvariantCulture); } catch (Exception) { continue; } if (n >= 0 && n < MaxColours && !item.Colours.Contains(n)) item.Colours.Add(n); }
                plan.Items.Add(item);
            }
            return plan.Items.Count == 0 ? null : plan;
        }

        // The file with this fighter's list (null or empty: none, Random as before); the other fighters' lists stay as they
        // are. Returns the folder that holds the copy for Undo ("" when there was no file), or null when nothing changed.
        internal static string Save(string game, string fighterDir, RandomPlan plan, string backupRoot)
        {
            return Save(game, new Dictionary<string, RandomPlan> { { fighterDir ?? "", plan } }, backupRoot);
        }
        // 1.0.24 (PR #52 review): every fighter the window changed, in one write and one Undo copy.
        internal static string Save(string game, IDictionary<string, RandomPlan> plans, string backupRoot)
        {
            if (plans == null || plans.Count == 0) return null;
            if (plans.Keys.Any(d => FighterId(d) < 0)) throw new IOException("Choose a fighter.");
            string path = PathOf(game);
            Dictionary<string, object> file = ReadObject(path);
            // A file this Manager can't read as a Random list (damaged, or a newer format) holds lists it would lose: refused
            if (File.Exists(path) && (file == null || Str(file, "format") != Format))
                throw new IOException("random_list.json in the game's SF6_CostumeSlots_data folder can't be read as a Random list (damaged, or from a newer Slotsloader Manager). Move it away or delete it, then save again.");
            if (file == null) file = new Dictionary<string, object>();
            object v; var fighters = file.TryGetValue("fighters", out v) ? v as Dictionary<string, object> : null;
            fighters = fighters == null ? new Dictionary<string, object>() : new Dictionary<string, object>(fighters);
            foreach (var pair in plans)
            {
                string key = FighterId(pair.Key).ToString(CultureInfo.InvariantCulture); RandomPlan plan = pair.Value;
                if (plan == null || plan.Items.Count == 0) { fighters.Remove(key); continue; }
                var items = new List<object>();
                foreach (RandomListItem i in plan.Items)
                {
                    var e = new Dictionary<string, object>();
                    if (i.Outfit.Game) e["game"] = i.Outfit.CostumeNo;
                    else { e["mod_id"] = i.Outfit.ModId; e["base"] = i.Outfit.Base; e["costume"] = i.Outfit.CostumeNo; e["name"] = i.Outfit.Label; }
                    e["repeat"] = Math.Max(1, Math.Min(MaxRepeat, i.Repeat));
                    // colour numbers 0 to 63, as the plugin takes them (10 and up: a game outfit's EX colours)
                    if (i.Colours.Count > 0) e["colours"] = i.Colours.Where(c => c >= 0 && c < MaxColours).Distinct().OrderBy(c => c).ToArray();
                    items.Add(e);
                }
                fighters[key] = new Dictionary<string, object> { { "mode", plan.Shuffle ? "shuffle" : "order" }, { "outfits", items } };
            }
            var next = new Dictionary<string, object> { { "format", Format }, { "written_by", "Slotsloader Manager" }, { "fighters", fighters.OrderBy(kv => { int n; return Int32.TryParse(kv.Key, out n) ? n : Int32.MaxValue; }).ToDictionary(kv => kv.Key, kv => kv.Value) } };
            string text = Serializer().Serialize(next) + "\n";
            string before = File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : null;
            if (before == text || (before == null && fighters.Count == 0)) return null;
            string backup = "";
            if (before != null)
            {
                backup = Path.Combine(backupRoot, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N").Substring(0, 6));
                Directory.CreateDirectory(backup);
                File.Copy(path, Path.Combine(backup, "random_list.json"));
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            if (fighters.Count == 0) File.Delete(path); else Publish(path, text);
            Program.LogLine("Random list saved: " + String.Join("; ", plans.Select(p => p.Key + " " + (p.Value == null || p.Value.Items.Count == 0 ? "removed" : p.Value.Signature))) + ".");
            return backup;
        }

        // Undo: the file as it was ("": there was none).
        internal static void Restore(string game, string backup)
        {
            string path = PathOf(game);
            if (backup == "") { if (File.Exists(path)) File.Delete(path); return; }
            string copy = Path.Combine(backup ?? "", "random_list.json");
            if (!File.Exists(copy)) throw new IOException("The copy of the earlier list is gone.");
            Publish(path, File.ReadAllText(copy, Encoding.UTF8));
        }

        // Through DurableFile.Write: a replace that removed the target before failing keeps the new text (PR #52 review)
        private static void Publish(string path, string text) { DurableFile.Write(path, Utf8.GetBytes(text)); }
    }
}
