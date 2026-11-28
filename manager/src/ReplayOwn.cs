using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace SlotsInstaller
{
    // 28 Nov 2026 (the user: "ticking it would turn it to "own" costume for all characters", "user can then configure it
    // selectively if they want, ingame"). Ticking "Settings tab in the Replays menu" sets every character of the tab's
    // Replay Outfit Settings to My Own: a replay of one of the player's online matches shows the outfit and colour the
    // player really wore there (the loader's journal), the opponent as the replay recorded it. Since loader 1.28.0 a
    // character left alone is Off (what the replay recorded, Outfit 1 / DriveTech for the player's own fighter).
    // The loader's own file, the one the tab writes (plugin replay_logic.hpp, from_json / to_json):
    //   reframework\data\SF6_CostumeSlots_data\replay_outfits.json
    //   {"version":1,"fighters":{"<fighter id>":{"mode":"own"}, ...}, "stage":{...}, "bgm":{...}}
    // Written once per tick, when the loader is installed and SF6 is closed (the tab would write over it): a character set
    // to an outfit in the game ("mode":"forced") keeps it, one set Off (no entry, or "mode":"off" with its costume options)
    // becomes My Own with its options kept, the stage and the music stay. From then on the file is the game's: the choices
    // made in the tab stay until the box is unticked and ticked again. Uninstalled, the file goes when it is still what the
    // tick left and holds nothing but My Own entries, so an uninstall leaves the game folder as it was; the next install with
    // the box ticked writes it again. A file this Manager can't read (damaged, a newer format) is left as it is.
    // Data\replay-own.json remembers the tick and what was written. Never fails a change: a problem is logged.
    internal sealed class ReplayOwnState
    {
        public bool Ticked;        // the tick was applied (unticked: false again)
        public string Written;     // SHA-256 of replay_outfits.json as the tick left it; null: nothing written
    }

    internal static class ReplayOwn
    {
        internal const string Relative = @"reframework\data\SF6_CostumeSlots_data\replay_outfits.json";
        internal const string StateFile = "replay-own.json";
        internal const string Own = "own";
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

        internal static string PathOf(string game) { return Path.Combine(game ?? "", Relative); }

        internal static ReplayOwnState LoadState(string dataDir)
        {
            try
            {
                string path = Path.Combine(dataDir ?? "", StateFile);
                return (File.Exists(path) ? Json.Read<ReplayOwnState>(path) : null) ?? new ReplayOwnState();
            }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); return new ReplayOwnState(); }
        }
        private static void SaveState(string dataDir, ReplayOwnState s) { Directory.CreateDirectory(dataDir); Json.Write(Path.Combine(dataDir, StateFile), s); }

        // True when replay_outfits.json changed. `loader`: a suite-line Slots Loader this Manager installed is enabled;
        // `ticked`: "Settings tab in the Replays menu". Called by LoaderFlags.Apply while SF6 is closed.
        internal static bool Apply(string game, string dataDir, bool loader, bool ticked)
        {
            ReplayOwnState s = LoadState(dataDir);
            string path = PathOf(game);
            if (!loader)
            {
                // no file (removed by hand, or never written): the next install with the box ticked writes it. A file the
                // player changed in the game stays, and so does the tick: a loader turned off and on again keeps their choices.
                if (!File.Exists(path)) { if (s.Ticked || s.Written != null) { s.Ticked = false; s.Written = null; SaveState(dataDir, s); } return false; }
                if (s.Written == null || Hash(path) != s.Written || !OnlyOwn(path)) return false;
                Safety.NoReparseChain(path);
                File.Delete(path);
                for (string dir = Path.GetDirectoryName(path); dir != null && dir.Length > game.Length && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any(); dir = Path.GetDirectoryName(dir))
                    Directory.Delete(dir);
                s.Ticked = false; s.Written = null; SaveState(dataDir, s);
                Program.LogLine("Replays: replay_outfits.json removed (it held only the My Own the tick wrote).");
                return true;
            }
            if (!ticked)
            {
                if (s.Ticked) { s.Ticked = false; SaveState(dataDir, s); }
                return false;
            }
            if (s.Ticked) return false;
            int set;
            bool? wrote = Write(game, out set);
            if (wrote == null) Program.LogLine("Replays: replay_outfits.json can't be read as Replay Outfit Settings (damaged, or a newer loader's): left as it is, no character set to My Own.");
            else if (wrote == true) Program.LogLine("Replays: " + set.ToString(CultureInfo.InvariantCulture) + " character" + (set == 1 ? "" : "s") + " set to My Own in replay_outfits.json.");
            s.Ticked = true; s.Written = wrote == null || !File.Exists(path) ? null : Hash(path);
            SaveState(dataDir, s);
            return wrote == true;
        }

        // Every character: this Manager's list and any other fighter the loader's registry names (one newer than this Manager).
        internal static List<int> Fighters(string game)
        {
            var ids = new SortedSet<int>();
            foreach (string code in Characters.ByCode.Keys) { int n; if (Int32.TryParse(code, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) && n > 0) ids.Add(n); }
            try
            {
                string registry = Path.Combine(game ?? "", OutfitOrder.DataRelative, "registry.json");
                Dictionary<string, object> r = File.Exists(registry) && new FileInfo(registry).Length <= 32L * 1024 * 1024 ? Parse(File.ReadAllText(registry, Encoding.UTF8)) : null;
                object v;
                if (r != null && r.TryGetValue("possession", out v))
                {
                    IEnumerable<object> list = v as object[] ?? (v is System.Collections.ArrayList ? ((System.Collections.ArrayList)v).Cast<object>() : Enumerable.Empty<object>());
                    foreach (object e in list)
                    {
                        var d = e as Dictionary<string, object>; object f; int n;
                        if (d != null && d.TryGetValue("fighter", out f) && Number(f, out n) && n > 0) ids.Add(n);
                    }
                }
            }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); }
            return ids.ToList();
        }

        // Every character on My Own unless the game has it on an outfit. True: written; false: nothing to change; null: not a
        // file of this format (left alone). `set`: the characters changed.
        internal static bool? Write(string game, out int set)
        {
            set = 0;
            string path = PathOf(game);
            string before = File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : null;
            Dictionary<string, object> file;
            if (before == null || before.Trim().TrimStart('﻿').Trim().Length == 0) file = new Dictionary<string, object> { { "version", 1 } };
            else if ((file = Parse(before)) == null || !VersionOne(file)) return null;
            object v;
            Dictionary<string, object> fighters;
            if (!file.TryGetValue("fighters", out v)) fighters = new Dictionary<string, object>();
            else if ((fighters = v as Dictionary<string, object>) == null) return null;
            fighters = new Dictionary<string, object>(fighters);
            foreach (int fid in Fighters(game))
            {
                string key = fid.ToString(CultureInfo.InvariantCulture);
                object e; var entry = fighters.TryGetValue(key, out e) ? e as Dictionary<string, object> : null;
                if (entry == null) { fighters[key] = new Dictionary<string, object> { { "mode", Own } }; ++set; continue; }
                object m; string mode = entry.TryGetValue("mode", out m) ? m as string : null;
                if (mode == "forced" || mode == Own) continue;
                entry = new Dictionary<string, object>(entry); entry["mode"] = Own;   // Off: its costume options stay
                fighters[key] = entry; ++set;
            }
            if (set == 0) return false;
            string text = Text(file, fighters);
            if (text == before) return false;
            Safety.NoReparseChain(path);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            DurableFile.Write(path, Utf8.GetBytes(text));
            return true;
        }

        // Laid out as the tab writes it: one line per character, by fighter id; every other key after them, as it was.
        private static string Text(Dictionary<string, object> file, Dictionary<string, object> fighters)
        {
            var ser = Json.Serializer();
            var sb = new StringBuilder("{\n    \"version\": 1,\n    \"fighters\": {");
            bool first = true;
            foreach (var kv in fighters.OrderBy(kv => { int n; return Int32.TryParse(kv.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) ? n : Int32.MaxValue; }).ThenBy(kv => kv.Key, StringComparer.Ordinal))
            {
                sb.Append(first ? "\n" : ",\n").Append("        ").Append(ser.Serialize(kv.Key)).Append(": ").Append(ser.Serialize(kv.Value));
                first = false;
            }
            sb.Append(first ? "}" : "\n    }");
            foreach (var kv in file)
                if (kv.Key != "version" && kv.Key != "fighters") sb.Append(",\n    ").Append(ser.Serialize(kv.Key)).Append(": ").Append(ser.Serialize(kv.Value));
            return sb.Append("\n}\n").ToString();
        }

        // Nothing but the version and My Own entries (no outfit, no options, no stage, no music)
        internal static bool OnlyOwn(string path)
        {
            Dictionary<string, object> file = Parse(File.ReadAllText(path, Encoding.UTF8));
            if (file == null || !VersionOne(file) || file.Keys.Any(k => k != "version" && k != "fighters")) return false;
            object v; var fighters = file.TryGetValue("fighters", out v) ? v as Dictionary<string, object> : null;
            return fighters != null && fighters.Values.All(e => { var d = e as Dictionary<string, object>; object m; return d != null && d.Count == 1 && d.TryGetValue("mode", out m) && Own.Equals(m); });
        }

        private static bool VersionOne(Dictionary<string, object> file) { object v; int n; return file.TryGetValue("version", out v) && Number(v, out n) && n == 1; }
        private static bool Number(object v, out int n)
        {
            n = 0;
            if (v == null || v is string || v is bool) return false;
            try { n = Convert.ToInt32(v, CultureInfo.InvariantCulture); return true; } catch (Exception ex) { if (ex is FormatException || ex is InvalidCastException || ex is OverflowException) return false; throw; }
        }
        private static Dictionary<string, object> Parse(string text)
        {
            try { return Json.Serializer().DeserializeObject((text ?? "").TrimStart('﻿')) as Dictionary<string, object>; }
            catch (Exception ex) { if (ex is ArgumentException || ex is InvalidOperationException) return null; throw; }
        }
        private static string Hash(string path)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path))).Replace("-", "");
        }
    }
}
