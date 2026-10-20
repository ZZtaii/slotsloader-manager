using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SlotsInstaller
{
    // The assistant's view of the user's library and costume folder. Exposes
    // stable short IDs instead of local paths; all reads go through the same
    // backend calls the GUI uses.
    internal sealed class AssistantWorkspace
    {
        internal readonly AppConfig Config;
        internal readonly AssistantSettings Settings;
        // The GUI supplies its already-scanned library; otherwise the first search scans it.
        internal Func<List<LibraryEntry>> LibraryProvider;
        private List<LibraryEntry> library;
        private readonly object sync = new object();
        private sealed class CachedPackage { internal string Stamp; internal ModPackage Package; }
        private readonly Dictionary<string,CachedPackage> packages = new Dictionary<string,CachedPackage>(StringComparer.Ordinal);

        internal AssistantWorkspace(AppConfig config, AssistantSettings settings)
        {
            Config = config; Settings = settings ?? new AssistantSettings();
        }

        internal string CostumeRoot { get { return Config.CostumeRoot; } }
        internal string SevenZip { get { return Config.SevenZipPath ?? ""; } }

        internal static string ShortHash(string text)
        {
            using (var sha = FastHash.Sha256())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
                return BitConverter.ToString(hash, 0, 5).Replace("-", "").ToLowerInvariant();
            }
        }
        internal static string NormalizePath(string path)
        {
            try { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant(); }
            catch (Exception ex) { if (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException) return (path ?? "").ToUpperInvariant(); throw; }
        }
        internal static string ModId(string sourcePath) { return "mod-" + ShortHash("source:" + NormalizePath(sourcePath)); }
        // Managed outfits keep their ID across enable/disable moves; manual entries use their location.
        internal static string InstalledId(InstalledOutfit outfit)
        {
            return "out-" + ShortHash(outfit.Managed && !String.IsNullOrEmpty(outfit.Id) ? "managed:" + outfit.Id.ToLowerInvariant() : "path:" + NormalizePath(outfit.Folder));
        }
        internal static string PresetId(OutfitSetInfo set) { return "set-" + ShortHash("set:" + (set.Id ?? "").ToLowerInvariant()); }
        internal static string ReplacementId(ReplaceMod mod) { return "rep-" + ShortHash("replacement:" + (mod.Id ?? "").ToLowerInvariant()); }
        internal static string FluffyPresetId(string path) { return "prt-" + ShortHash("fluffy-preset:" + NormalizePath(path)); }
        // Replace-original engine for this workspace (the app's Data folder and the configured game).
        internal ReplaceEngine Engine() { return ReplaceEngine.ForConfig(Config); }
        internal List<string> FluffyPresetFiles() { return FluffyPresets.PresetFiles(FluffyInstalls.DetectRoots(Config.Libraries, ReplaceSettings.Load().FluffyRoots)); }

        internal void SetLibrary(List<LibraryEntry> entries) { lock (sync) library = entries; }
        internal void InvalidateLibrary() { lock (sync) library = null; }

        internal List<LibraryEntry> Library()
        {
            lock (sync)
            {
                if (library != null) return library;
            }
            List<LibraryEntry> loaded = LibraryProvider != null ? LibraryProvider() : null;
            if (loaded == null) loaded = SlotsInstaller.Library.Scan(Config.Libraries ?? new List<string>(), SevenZip);
            loaded = loaded.Where(e => e != null && e.Kind != "Library" && !String.IsNullOrEmpty(e.SourcePath)).ToList();
            lock (sync) library = loaded;
            return loaded;
        }

        // 1.0.10: the library already listed (this workspace's or the app's), never a new scan; null when there is none.
        internal List<LibraryEntry> ListedLibrary()
        {
            lock (sync)
            {
                if (library != null) return library;
            }
            return LibraryProvider != null ? LibraryProvider() : null;
        }

        internal LibraryEntry FindMod(string modId)
        {
            return Library().FirstOrDefault(e => ModId(e.SourcePath) == modId);
        }

        internal List<InstalledOutfit> Installed()
        {
            if (!Directory.Exists(CostumeRoot)) return new List<InstalledOutfit>();
            return Installer.ListInstalled(CostumeRoot);
        }

        internal ModPackage Package(LibraryEntry entry)
        {
            string id = ModId(entry.SourcePath);
            string stamp = null;
            if (File.Exists(entry.SourcePath)) { var info = new FileInfo(entry.SourcePath); stamp = info.Length + ":" + info.LastWriteTimeUtc.Ticks; }
            lock (sync)
            {
                CachedPackage cached;
                if (stamp != null && packages.TryGetValue(id, out cached) && cached.Stamp == stamp) return cached.Package;
            }
            ModPackage package = PackageReader.Load(entry.SourcePath, SevenZip);
            if (stamp != null) lock (sync)
            {
                if (packages.Count >= 24) packages.Remove(packages.Keys.First());
                packages[id] = new CachedPackage { Stamp = stamp, Package = package };
            }
            return package;
        }

        internal string LoaderResultFile { get { return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(CostumeRoot)), "data", "SF6_Costumes_Data", "outfits.json"); } }

        // Removes local paths from text that is sent to the model.
        internal string Scrub(string text)
        {
            if (String.IsNullOrEmpty(text)) return text ?? "";
            var roots = new List<KeyValuePair<string,string>>();
            Action<string,string> add = delegate(string path, string label)
            {
                if (String.IsNullOrWhiteSpace(path)) return;
                try { roots.Add(new KeyValuePair<string,string>(Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar), label)); } catch (Exception ex) { if (!(ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)) throw; }
            };
            add(CostumeRoot, "<costume_mods>"); add(Config.GameRoot, "<game>");
            if (Config.Libraries != null) foreach (string lib in Config.Libraries) add(lib, "<library>");
            add(AppConfig.DataDir, "<app data>");
            foreach (var root in roots.OrderByDescending(r => r.Key.Length))
                text = Regex.Replace(text, Regex.Escape(root.Key), root.Value.Replace("$", "$$"), RegexOptions.IgnoreCase);
            return Regex.Replace(text, @"(?<![<\w])(?:[A-Za-z]:\\|\\\\)[^\s""'<>|]*", "<path>");
        }

        internal static string Character(string requested, out string error)
        {
            error = null;
            if (String.IsNullOrWhiteSpace(requested)) return "";
            string compact = new string(requested.ToLowerInvariant().Where(Char.IsLetterOrDigit).ToArray());
            foreach (string name in Characters.Names)
                if (compact == new string(name.ToLowerInvariant().Where(Char.IsLetterOrDigit).ToArray())) return name;
            if (compact == "viper") return "C. Viper";
            if (compact == "honda") return "E. Honda";
            if (compact == "bison") return "M. Bison";
            if (compact == "chunli") return "Chun-Li";
            error = "Unknown character '" + requested + "'. Use one of: " + String.Join(", ", Characters.Names) + ".";
            return null;
        }

        internal static bool EntryMatchesCharacter(LibraryEntry entry, string character)
        {
            return String.Equals(entry.Character, character, StringComparison.OrdinalIgnoreCase) || entry.CharacterCodes.Any(code => Characters.ByCode.ContainsKey(code) && String.Equals(Characters.ByCode[code], character, StringComparison.OrdinalIgnoreCase));
        }

        internal static string CharacterOfFiles(CompositionPlan plan, string character)
        {
            var codes = plan.Files.Keys.SelectMany(p => Regex.Matches(p, @"/esf/esf(\d{3})/", RegexOptions.IgnoreCase).Cast<Match>().Select(m => m.Groups[1].Value)).Distinct().ToList();
            string expected = Characters.ByCode.Where(k => k.Value == character).Select(k => k.Key).FirstOrDefault();
            if (expected == null || codes.All(c => c == expected)) return null;
            return "These files are for " + String.Join(", ", codes.Select(c => Characters.ByCode.ContainsKey(c) ? Characters.ByCode[c] : "esf" + c)) + ", not " + character + ". The Manager cannot move mods to another character.";
        }
    }
}
