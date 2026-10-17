using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SlotsInstaller
{
    // 2.5.0: variant on/off for whole mod archives (and loose multi-option folders) dropped into costume_mods.
    // SlotsLoader writes variants.json (every variant it found, on and off, with the slot each built variant got);
    // the Manager writes variant_choices.json, which SlotsLoader reads at the next SF6 launch. Both live in
    // <game>\reframework\data\SF6_Costumes_Data. A variant is on unless the choices file says false.
    public sealed class ArchiveVariant
    {
        public string Label = "", Slot = "";   // Slot: "Outfit V" for a built variant, "" when it was not built
        public bool Enabled;                   // as SlotsLoader applied it at the last launch
    }
    public sealed class ArchiveVariants
    {
        public string Source = "", FighterDir = "";   // Source: relative to costume_mods, forward slashes ("Luke/X.zip")
        public List<ArchiveVariant> Variants = new List<ArchiveVariant>();
    }
    internal sealed class VariantReport
    {
        internal bool Present;                 // false: no variants.json (a SlotsLoader older than the contract, or never run)
        internal string Generated = "", Loader = "", Error;
        internal List<ArchiveVariants> Archives = new List<ArchiveVariants>();
        internal ArchiveVariants Find(string source) { string key = VariantChoices.Normalize(source); return Archives.FirstOrDefault(a => String.Equals(VariantChoices.Normalize(a.Source), key, StringComparison.OrdinalIgnoreCase)); }
    }
    // One row as the window, CLI and Assistant show it: On is what the next launch applies.
    internal sealed class VariantState
    {
        internal string Label, Slot; internal bool On, Built;
        internal bool Pending { get { return On != Built; } }
    }
    internal sealed class VariantChoiceFile
    {
        internal bool Malformed;               // unreadable JSON or an unknown version: read as all on
        internal int Version = 1;
        internal Dictionary<string, Dictionary<string, bool>> Archives = new Dictionary<string, Dictionary<string, bool>>(StringComparer.OrdinalIgnoreCase);
        internal Dictionary<string, bool> Find(string source)
        {
            string key = VariantChoices.Normalize(source);
            foreach (var pair in Archives) if (String.Equals(VariantChoices.Normalize(pair.Key), key, StringComparison.OrdinalIgnoreCase)) return pair.Value;
            return null;
        }
        internal bool IsOn(string source, string label)
        {
            Dictionary<string, bool> map = Find(source); bool on;
            return map == null || !map.TryGetValue(label ?? "", out on) || on;
        }
    }

    internal static class VariantChoices
    {
        internal const string ReportName = "variants.json", ChoicesName = "variant_choices.json";
        private static readonly object writeLock = new object();

        internal static string DataFolder(string costumeRoot) { return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(costumeRoot).TrimEnd(Path.DirectorySeparatorChar)), "data", "SF6_Costumes_Data"); }
        internal static string ReportFile(string costumeRoot) { return Path.Combine(DataFolder(costumeRoot), ReportName); }
        internal static string ChoicesFile(string costumeRoot) { return Path.Combine(DataFolder(costumeRoot), ChoicesName); }
        internal static string Normalize(string source) { return (source ?? "").Replace('\\', '/').Trim('/'); }
        // An installed entry's source key: its path below costume_mods with forward slashes.
        internal static string SourceOf(InstalledOutfit outfit, string costumeRoot)
        {
            if (outfit == null || !NativeModCheck.IsWithin(outfit.Folder, costumeRoot)) return null;
            return Normalize(Path.GetFullPath(outfit.Folder).Substring(Path.GetFullPath(costumeRoot).TrimEnd(Path.DirectorySeparatorChar).Length));
        }

        internal static VariantReport ReadReport(string costumeRoot)
        {
            var report = new VariantReport();
            string file = ReportFile(costumeRoot);
            try
            {
                if (!File.Exists(file)) return report;
                Safety.NoReparseChain(file);
                if (new FileInfo(file).Length > 16L * 1024 * 1024) throw new IOException("variants.json exceeds the supported size.");
                var data = Json.Serializer().Deserialize<Dictionary<string, object>>(ReplaceEngine.ReadShared(file) ?? "");
                report.Present = true;
                report.Generated = NativeModCheck.Value(data, "generated"); report.Loader = NativeModCheck.Value(data, "loader");
                foreach (var row in NativeModCheck.Rows(data, "archives"))
                {
                    var archive = new ArchiveVariants { Source = Normalize(NativeModCheck.Value(row, "source")), FighterDir = NativeModCheck.Value(row, "fighter_dir") };
                    if (archive.Source.Length == 0) continue;
                    foreach (var v in NativeModCheck.Rows(row, "variants"))
                    {
                        object enabled; v.TryGetValue("enabled", out enabled);
                        string label = NativeModCheck.Value(v, "label");
                        if (label.Length == 0 || archive.Variants.Any(x => x.Label == label)) continue;
                        archive.Variants.Add(new ArchiveVariant { Label = label, Enabled = enabled is bool ? (bool)enabled : !"false".Equals(Convert.ToString(enabled), StringComparison.OrdinalIgnoreCase), Slot = NativeModCheck.Value(v, "slot") });
                    }
                    if (report.Find(archive.Source) == null) report.Archives.Add(archive);
                }
            }
            catch (Exception ex) { Program.Log(ex); report.Present = false; report.Error = "variants.json is unreadable."; report.Archives.Clear(); }
            return report;
        }

        // Missing = all on. A malformed file is tolerated (read as all on, logged) and replaced by the next change.
        internal static VariantChoiceFile ReadChoices(string costumeRoot)
        {
            var result = new VariantChoiceFile();
            string file = ChoicesFile(costumeRoot);
            try
            {
                if (!File.Exists(file)) return result;
                Safety.NoReparseChain(file);
                if (new FileInfo(file).Length > 16L * 1024 * 1024) throw new IOException("variant_choices.json exceeds the supported size.");
                var data = Json.Serializer().Deserialize<Dictionary<string, object>>(ReplaceEngine.ReadShared(file) ?? "");
                object version, archives;
                if (data == null || !data.TryGetValue("version", out version) || !(version is int)) throw new InvalidDataException("variant_choices.json has no version.");
                result.Version = (int)version;
                if (result.Version != 1) { result.Malformed = true; Program.LogLine("variant_choices.json version " + result.Version + " is not supported; read as all on."); return result; }
                if (data.TryGetValue("archives", out archives) && archives is IDictionary<string, object>)
                    foreach (var pair in (IDictionary<string, object>)archives)
                    {
                        var labels = pair.Value as IDictionary<string, object>; if (labels == null) continue;
                        var map = new Dictionary<string, bool>(StringComparer.Ordinal);
                        foreach (var label in labels) if (label.Value is bool) map[label.Key] = (bool)label.Value;
                        result.Archives[pair.Key] = map;
                    }
            }
            catch (Exception ex) { Program.Log(ex); result = new VariantChoiceFile { Malformed = true }; }
            return result;
        }

        internal static List<VariantState> States(ArchiveVariants archive, VariantChoiceFile choices)
        {
            if (archive == null) return new List<VariantState>();
            return archive.Variants.Select(v => new VariantState { Label = v.Label, Slot = v.Slot ?? "", Built = v.Enabled, On = choices == null || choices.IsOn(archive.Source, v.Label) }).ToList();
        }

        // A copy of this archive's entry (null when it has none), for Undo.
        internal static Dictionary<string, bool> Entry(string costumeRoot, string source)
        {
            Dictionary<string, bool> map = ReadChoices(costumeRoot).Find(source);
            return map == null ? null : new Dictionary<string, bool>(map, StringComparer.Ordinal);
        }

        // Turns one variant on or off. Only false is stored; the archive's entry goes when all its variants are on.
        internal static void Set(string costumeRoot, ArchiveVariants archive, string label, bool on)
        {
            if (archive == null || !archive.Variants.Any(v => v.Label == label)) throw new ArgumentException("Slotsloader lists no variant '" + label + "' in " + (archive == null ? "this mod" : archive.Source) + ".");
            Update(costumeRoot, archive.Source, delegate(Dictionary<string, bool> map)
            {
                if (on) map.Remove(label); else map[label] = false;
                return archive.Variants.All(v => { bool value; return !map.TryGetValue(v.Label, out value) || value; }) ? null : map;
            });
        }
        // Puts an archive's entry back exactly (Undo); null removes it.
        internal static void Restore(string costumeRoot, string source, Dictionary<string, bool> entry)
        {
            Update(costumeRoot, source, delegate(Dictionary<string, bool> map) { return entry == null ? null : new Dictionary<string, bool>(entry, StringComparer.Ordinal); });
        }

        private static void Update(string costumeRoot, string source, Func<Dictionary<string, bool>, Dictionary<string, bool>> change)
        {
            lock (writeLock)
            {
                string file = ChoicesFile(costumeRoot);
                VariantChoiceFile current = ReadChoices(costumeRoot);
                if (current.Malformed && current.Version != 1) throw new IOException("variant_choices.json is from a newer version (" + current.Version + "). Not changed.");
                if (current.Malformed) Program.LogLine("variant_choices.json was unreadable; replaced (other entries could not be kept).");
                string key = current.Archives.Keys.FirstOrDefault(k => String.Equals(Normalize(k), Normalize(source), StringComparison.OrdinalIgnoreCase));
                var map = key == null ? new Dictionary<string, bool>(StringComparer.Ordinal) : new Dictionary<string, bool>(current.Archives[key], StringComparer.Ordinal);
                if (key != null) current.Archives.Remove(key);
                Dictionary<string, bool> next = change(map);
                if (next != null && next.Count > 0) current.Archives[Normalize(source)] = next;   // the loader's own spelling of the source
                var archives = new SortedDictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                foreach (var pair in current.Archives) archives[pair.Key] = new SortedDictionary<string, bool>(pair.Value, StringComparer.Ordinal);
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                if (File.Exists(file)) Safety.NoReparseChain(file);
                DurableFile.WriteJson(file, new Dictionary<string, object> { { "version", 1 }, { "archives", archives } });   // temp file + replace
            }
        }

        // "Off at next launch" / the slot a built variant got.
        internal static string Note(VariantState v)
        {
            if (v.Pending) return v.On ? "On at next launch" : "Off at next launch";
            return v.On && v.Slot.Length > 0 ? v.Slot : "";
        }
    }

    // SlotsLoader CLI.exe variants: list, or turn one variant on/off (2.5.0).
    internal static class VariantsCommandLine
    {
        internal static int Run(string[] args)
        {
            string game = null, source = null, label = null, value = null;
            try
            {
                for (int i = 1; i < args.Length; i++)
                {
                    if (args[i] == "--game" && i + 1 < args.Length) game = Path.GetFullPath(args[++i]);
                    else if (args[i] == "--set" && i + 3 < args.Length) { source = args[++i]; label = args[++i]; value = args[++i].ToLowerInvariant(); }
                    else throw new ArgumentException(args[i] == "--set" ? "--set needs \"<source>\" \"<label>\" on|off." : "Unknown option: " + args[i]);
                }
                if (value != null && value != "on" && value != "off") throw new ArgumentException("--set ends with on or off.");
            }
            catch (ArgumentException ex) { Console.Error.WriteLine(ex.Message); return 2; }
            try
            {
                AppConfig config = File.Exists(Path.Combine(AppConfig.DataDir, "settings.json")) ? AppConfig.Load() : AppConfig.Detected();
                if (game != null) config.GameRoot = game;
                if (!Directory.Exists(config.GameRoot)) throw new IOException("Game folder not found: " + config.GameRoot);
                string root = config.CostumeRoot;
                VariantReport report = VariantChoices.ReadReport(root);
                if (!report.Present)
                {
                    Console.WriteLine(report.Error ?? "No variants.json: the installed Slotsloader is too old for variant on/off, or SF6 hasn't started with it yet.");
                    return value == null ? 0 : 1;
                }
                if (value != null)
                {
                    ArchiveVariants archive = report.Find(source);
                    if (archive == null) throw new IOException("variants.json lists no archive '" + source + "'. Run variants to see the sources.");
                    ArchiveVariant variant = archive.Variants.FirstOrDefault(v => v.Label == label) ?? archive.Variants.FirstOrDefault(v => String.Equals(v.Label, label, StringComparison.OrdinalIgnoreCase));
                    if (variant == null) throw new IOException("'" + archive.Source + "' has no variant '" + label + "'.");
                    VariantChoices.Set(root, archive, variant.Label, value == "on");
                    Console.WriteLine("'" + variant.Label + "' " + value + " for " + archive.Source + " · " + AppliesText(GameProcesses.Probe.IsGameRunning()));
                    return 0;
                }
                VariantChoiceFile choices = VariantChoices.ReadChoices(root);
                Console.WriteLine("Slotsloader variants (" + (report.Generated.Length > 0 ? report.Generated : "no date") + (report.Loader.Length > 0 ? ", " + report.Loader : "") + ")" + (choices.Malformed ? " · variant_choices.json unreadable, read as all on" : ""));
                if (report.Archives.Count == 0) Console.WriteLine("  No archive with variants.");
                foreach (ArchiveVariants archive in report.Archives)
                {
                    Console.WriteLine("  " + archive.Source + (archive.FighterDir.Length > 0 ? "  " + archive.FighterDir : ""));
                    foreach (VariantState v in VariantChoices.States(archive, choices))
                    {
                        string note = VariantChoices.Note(v);
                        Console.WriteLine("    [" + (v.On ? "on " : "off") + "] " + v.Label + (note.Length > 0 ? "  · " + note : ""));
                    }
                }
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine("ERROR: " + ex.Message); Program.Log(ex); return 1; }
        }
        internal static string AppliesText(bool gameRunning) { return gameRunning ? "applies after SF6 restarts" : "applies at next launch"; }
    }
}
