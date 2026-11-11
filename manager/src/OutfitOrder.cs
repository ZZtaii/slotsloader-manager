using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace SlotsInstaller
{
    // 1.0.18 (user request): the order of a fighter's extra outfits. Slotsloader gives every extra outfit a slot
    // number once and keeps it (registry.json "slots": mod, fighter, new_costume_no, new_folder, scene_name), and it
    // takes no order from outside. So the Manager exchanges the slots themselves: the outfits of one fighter keep the
    // same set of slot numbers, handed out in the order the user chose. Each slot row gets another row's
    // (new_costume_no, new_folder, scene_name), the fingerprint is cleared so the loader builds its pak again at the
    // next SF6 start, the last build's report (outfits.json) is renumbered so the Manager shows the new order at once,
    // and the plugin's equipped slot (state.json) follows its outfit. The three files are copied first: Undo puts them
    // back. SF6 must be closed (it reads the registry at start and rewrites state.json while it runs).
    // What cannot follow: the game's own save and old replays name slots by number.
    internal sealed class OutfitSlot
    {
        internal int CostumeNo;
        internal string Name = "", Label = "", Source = "";   // "Outfit II", the loader's label, the path under costume_mods
    }

    internal static class OutfitOrder
    {
        internal const string DataRelative = @"reframework\data\SF6_Costumes_Data", StateRelative = @"reframework\data\SF6_CostumeSlots_data\state.json";
        private static readonly Regex Block = new Regex(@"\{[^{}]*\}", RegexOptions.Singleline);
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

        private static string RegistryPath(string game) { return Path.Combine(game ?? "", DataRelative, "registry.json"); }
        private static string ReportPath(string game) { return Path.Combine(game ?? "", DataRelative, "outfits.json"); }
        private static string StatePath(string game) { return Path.Combine(game ?? "", StateRelative); }

        private static string Text(string block, string key) { Match m = Regex.Match(block, "\"" + key + "\"\\s*:\\s*\"([^\"]*)\""); return m.Success ? m.Groups[1].Value : null; }
        private static int Number(string block, string key) { Match m = Regex.Match(block, "\"" + key + "\"\\s*:\\s*(-?\\d+)"); int n; return m.Success && Int32.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) ? n : Int32.MinValue; }
        private static string SetText(string block, string key, string value) { return Regex.Replace(block, "(\"" + key + "\"\\s*:\\s*\")[^\"]*(\")", m => m.Groups[1].Value + value + m.Groups[2].Value); }
        private static string SetNumber(string block, string key, int value) { return Regex.Replace(block, "(\"" + key + "\"\\s*:\\s*)-?\\d+", m => m.Groups[1].Value + value.ToString(CultureInfo.InvariantCulture)); }
        // The text of one top-level array ("slots", "outfits"): from its '[' to the matching ']'.
        private static bool ArraySpan(string json, string key, out int start, out int end)
        {
            start = end = -1;
            Match m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*\\[");
            if (!m.Success) return false;
            start = m.Index + m.Length; int depth = 1; bool quoted = false;
            for (int i = start; i < json.Length; i++)
            {
                char c = json[i];
                if (quoted) { if (c == '\\') i++; else if (c == '"') quoted = false; continue; }
                if (c == '"') quoted = true; else if (c == '[') depth++; else if (c == ']' && --depth == 0) { end = i; return true; }
            }
            return false;
        }
        private static string Read(string path) { return File.Exists(path) && new FileInfo(path).Length < 32L * 1024 * 1024 ? File.ReadAllText(path) : null; }

        // The slot numbers Slotsloader holds for this fighter ("esf016"), ascending.
        internal static List<int> RegistrySlots(string gameRoot, string fighterDir)
        {
            var numbers = new List<int>();
            string json = Read(RegistryPath(gameRoot)); int a, b;
            if (json == null || !ArraySpan(json, "slots", out a, out b)) return numbers;
            foreach (Match m in Block.Matches(json.Substring(a, b - a)))
                if (String.Equals(Text(m.Value, "fighter_dir"), fighterDir, StringComparison.OrdinalIgnoreCase)) { int n = Number(m.Value, "new_costume_no"); if (n != Int32.MinValue && !numbers.Contains(n)) numbers.Add(n); }
            numbers.Sort();
            return numbers;
        }

        // This fighter's extra outfits as the last build numbered them, in slot order. Only outfits that hold a slot in
        // the registry are listed: one the game has not loaded yet has no slot to exchange.
        internal static List<OutfitSlot> Slots(string gameRoot, string fighterDir)
        {
            var rows = new List<OutfitSlot>();
            try
            {
                string json = Read(ReportPath(gameRoot)); int a, b;
                if (json == null || !ArraySpan(json, "outfits", out a, out b)) return rows;
                var held = new HashSet<int>(RegistrySlots(gameRoot, fighterDir));
                foreach (Match m in Block.Matches(json.Substring(a, b - a)))
                {
                    if (!String.Equals(Text(m.Value, "fighter_dir"), fighterDir, StringComparison.OrdinalIgnoreCase)) continue;
                    int n = Number(m.Value, "costume_no");
                    if (!held.Contains(n) || rows.Any(r => r.CostumeNo == n)) continue;
                    rows.Add(new OutfitSlot { CostumeNo = n, Name = Unescape(Text(m.Value, "name")), Label = Unescape(Text(m.Value, "label")), Source = Unescape(Text(m.Value, "source")) });
                }
            }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); }
            return rows.OrderBy(r => r.CostumeNo).ToList();
        }
        private static string Unescape(string s) { return (s ?? "").Replace("\\/", "/").Replace("\\\\", "\\").Replace("\\\"", "\""); }

        // Fighters ("esf016") with at least two extra outfits holding a slot, and how many.
        internal static Dictionary<string, int> Counts(string gameRoot)
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string json = Read(ReportPath(gameRoot)); int a, b;
                if (json == null || !ArraySpan(json, "outfits", out a, out b)) return counts;
                foreach (string dir in Block.Matches(json.Substring(a, b - a)).Cast<Match>().Select(m => Text(m.Value, "fighter_dir")).Where(d => !String.IsNullOrEmpty(d)).Distinct(StringComparer.OrdinalIgnoreCase))
                    counts[dir] = Slots(gameRoot, dir).Count;
            }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); }
            return counts;
        }

        // `order`: the fighter's current slot numbers in the order wanted (first = the lowest slot). Every number must
        // be one Slots() lists, each once. Returns the folder holding the copies for Undo (null when nothing changed).
        internal static string Apply(string gameRoot, string fighterDir, IList<int> order, string backupRoot)
        {
            if (GameProcesses.Probe.IsGameRunning()) throw new IOException("Close SF6 to change the load order: it reads the slots when it starts.");
            List<OutfitSlot> now = Slots(gameRoot, fighterDir);
            var current = now.Select(s => s.CostumeNo).ToList();
            if (order == null || order.Count != current.Count || order.Distinct().Count() != order.Count || order.Any(n => !current.Contains(n)))
                throw new IOException("The outfits changed since the list was opened. Open it again.");
            var target = new Dictionary<int, int>();   // the outfit now in slot X goes to slot target[X]
            for (int i = 0; i < order.Count; i++) target[order[i]] = current[i];
            if (target.All(kv => kv.Key == kv.Value)) return null;

            string registryPath = RegistryPath(gameRoot), reportPath = ReportPath(gameRoot), statePath = StatePath(gameRoot);
            string registry = File.ReadAllText(registryPath), report = File.ReadAllText(reportPath), state = File.Exists(statePath) ? File.ReadAllText(statePath) : null;

            // registry.json: each of this fighter's rows takes the slot (number, folder, scene) of its target.
            int a, b;
            if (!ArraySpan(registry, "slots", out a, out b)) throw new IOException("Slotsloader's slot list could not be read.");
            string slots = registry.Substring(a, b - a);
            var triple = new Dictionary<int, KeyValuePair<string, string>>();
            foreach (Match m in Block.Matches(slots))
                if (String.Equals(Text(m.Value, "fighter_dir"), fighterDir, StringComparison.OrdinalIgnoreCase))
                    triple[Number(m.Value, "new_costume_no")] = new KeyValuePair<string, string>(Text(m.Value, "new_folder"), Text(m.Value, "scene_name"));
            if (target.Keys.Any(n => !triple.ContainsKey(n) || triple[n].Key == null || triple[n].Value == null)) throw new IOException("Slotsloader's slot list does not hold every outfit in this list. Start SF6 once, then try again.");
            string nextSlots = Block.Replace(slots, delegate(Match m)
            {
                int to;
                if (!String.Equals(Text(m.Value, "fighter_dir"), fighterDir, StringComparison.OrdinalIgnoreCase) || !target.TryGetValue(Number(m.Value, "new_costume_no"), out to)) return m.Value;
                return SetText(SetText(SetNumber(m.Value, "new_costume_no", to), "new_folder", triple[to].Key), "scene_name", triple[to].Value);
            });
            string nextRegistry = registry.Substring(0, a) + nextSlots + registry.Substring(b);
            nextRegistry = Regex.Replace(nextRegistry, "(\"fingerprint\"\\s*:\\s*\")[^\"]*(\")", "$1$2");   // the loader builds its pak again

            // outfits.json: the same exchange for the number and the name the game shows ("Outfit II" belongs to the slot).
            string nextReport = report;
            if (ArraySpan(report, "outfits", out a, out b))
            {
                string outfits = report.Substring(a, b - a);
                var names = new Dictionary<int, string>();
                foreach (Match m in Block.Matches(outfits))
                    if (String.Equals(Text(m.Value, "fighter_dir"), fighterDir, StringComparison.OrdinalIgnoreCase)) names[Number(m.Value, "costume_no")] = Text(m.Value, "name");
                string nextOutfits = Block.Replace(outfits, delegate(Match m)
                {
                    int to;
                    if (!String.Equals(Text(m.Value, "fighter_dir"), fighterDir, StringComparison.OrdinalIgnoreCase) || !target.TryGetValue(Number(m.Value, "costume_no"), out to)) return m.Value;
                    string block = SetNumber(m.Value, "costume_no", to); string name;
                    return names.TryGetValue(to, out name) && name != null ? SetText(block, "name", name) : block;
                });
                nextReport = report.Substring(0, a) + nextOutfits + report.Substring(b);
            }

            // state.json: the slot this fighter has equipped follows its outfit ("16": { "slot": 7, ... }).
            string nextState = state; int fighter;
            if (state != null && Int32.TryParse(Regex.Replace(fighterDir, @"\D", ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out fighter))
                nextState = Regex.Replace(state, "(\"" + fighter.ToString(CultureInfo.InvariantCulture) + "\"\\s*:\\s*\\{\\s*\"slot\"\\s*:\\s*)(\\d+)", delegate(Match m)
                {
                    int equipped, to;
                    return Int32.TryParse(m.Groups[2].Value, out equipped) && target.TryGetValue(equipped, out to) ? m.Groups[1].Value + to.ToString(CultureInfo.InvariantCulture) : m.Value;
                });

            string backup = Path.Combine(backupRoot, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(backup);
            File.Copy(registryPath, Path.Combine(backup, "registry.json")); File.Copy(reportPath, Path.Combine(backup, "outfits.json"));
            if (state != null) File.Copy(statePath, Path.Combine(backup, "state.json"));
            Publish(registryPath, nextRegistry); Publish(reportPath, nextReport);
            if (state != null && nextState != state) Publish(statePath, nextState);
            Program.LogLine("Outfit order changed for " + fighterDir + ": " + String.Join(", ", target.Where(kv => kv.Key != kv.Value).Select(kv => kv.Key + " to " + kv.Value)) + ".");
            return backup;
        }

        // Undo: the three files as they were.
        internal static void Restore(string gameRoot, string backup)
        {
            if (GameProcesses.Probe.IsGameRunning()) throw new IOException("Close SF6 to put the load order back.");
            if (String.IsNullOrEmpty(backup) || !Directory.Exists(backup)) throw new IOException("The copy of the earlier order is gone.");
            foreach (var pair in new[] { new KeyValuePair<string, string>("registry.json", RegistryPath(gameRoot)), new KeyValuePair<string, string>("outfits.json", ReportPath(gameRoot)), new KeyValuePair<string, string>("state.json", StatePath(gameRoot)) })
            {
                string copy = Path.Combine(backup, pair.Key);
                if (File.Exists(copy)) Publish(pair.Value, File.ReadAllText(copy));
            }
            // The registry copy holds the fingerprint of the build that is no longer in the pak.
            LoaderCostumePak.ClearFingerprint(gameRoot);
        }

        private static void Publish(string path, string text)
        {
            string tmp = path + ".slotsinstaller-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
            File.WriteAllText(tmp, text, Utf8);
            try { DurableFile.Publish(tmp, path); } finally { if (File.Exists(tmp)) File.Delete(tmp); }
        }
    }
}
