using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;

namespace SlotsInstaller
{
    // 1.0.22: an extra outfit is made from a game outfit (outfits.json "based_on") and reads that outfit's own files
    // wherever it doesn't bring its own, so a Replace original install over those files changes it too. Seen 26 Nov 2026:
    // "Ingrid with a Hat" over Ingrid Outfit 1 put its hat, chain and colours on the Bare Legs bundle's options that
    // carry no head mesh. Never a refusal: one warning per game outfit, naming the extra outfits it reaches.
    internal static class SharedOutfitFiles
    {
        private static readonly Regex ModelFolder = new Regex(@"^natives/stm/(?:streaming/)?product/model/esf/esf(\d{3})/(\d{3})/", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private const int NamesShown = 4;

        internal static List<string> Warnings(string gameRoot, IEnumerable<string> paths, GameReference reference)
        {
            var lines = new List<string>();
            try
            {
                if (String.IsNullOrEmpty(gameRoot) || reference == null) return lines;
                // Replaced files by game outfit ("esf032/001" -> paths); a fighter's shared parts (000) are every outfit's.
                var byOutfit = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (string raw in paths ?? Enumerable.Empty<string>())
                {
                    string p = (raw ?? "").Replace('\\', '/').TrimStart('/');
                    Match m = ModelFolder.Match(p);
                    if (!m.Success || m.Groups[2].Value == "000") continue;
                    string key = "esf" + m.Groups[1].Value + "/" + m.Groups[2].Value;
                    if (!reference.CostumeByFolder.ContainsKey(key)) continue;
                    List<string> list; if (!byOutfit.TryGetValue(key, out list)) byOutfit[key] = list = new List<string>();
                    list.Add(p.ToLowerInvariant());
                }
                if (byOutfit.Count == 0) return lines;
                List<Dictionary<string, object>> rows = Rows(gameRoot);
                if (rows.Count == 0) return lines;
                var shipped = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var outfit in byOutfit)
                {
                    string fighterDir = outfit.Key.Substring(0, 6), folder = outfit.Key.Substring(7);
                    int costume = reference.CostumeByFolder[outfit.Key];
                    var reached = new List<string>(); int outfits = 0;
                    foreach (var source in rows.Where(r => String.Equals(NativeModCheck.Value(r, "fighter_dir"), fighterDir, StringComparison.OrdinalIgnoreCase) && NativeModCheck.Value(r, "based_on") == costume.ToString(System.Globalization.CultureInfo.InvariantCulture))
                        .GroupBy(r => NativeModCheck.Value(r, "source").Replace('\\', '/').Trim('/'), StringComparer.OrdinalIgnoreCase))
                    {
                        string disk = OnDisk(gameRoot, source.Key);
                        HashSet<string> files;
                        if (!shipped.TryGetValue(source.Key, out files)) shipped[source.Key] = files = disk == null ? null : FilesOf(disk);
                        // A source that ships every one of these files itself is left out. A file counts as shipped when any of
                        // its options has it (an archive of several options is listed whole), so a bundle whose options differ
                        // can be left out although one option falls back: the warning may miss it, it never names a wrong one.
                        if (files != null && outfit.Value.All(files.Contains)) continue;
                        string name = disk != null ? Path.GetFileName(disk.TrimEnd(Path.DirectorySeparatorChar)) : source.Key.Substring(source.Key.LastIndexOf('/') + 1);
                        if (disk != null && File.Exists(disk)) name = Path.GetFileNameWithoutExtension(name);
                        int count = source.Count(); outfits += count;
                        reached.Add(name + (count > 1 ? " (" + count + " outfits)" : ""));
                    }
                    if (reached.Count == 0) continue;
                    string names = String.Join(", ", reached.Take(NamesShown)) + (reached.Count > NamesShown ? " and " + (reached.Count - NamesShown) + " more" : "");
                    lines.Add("Extra outfits made from " + reference.DescribeFolder(fighterDir.Substring(3), folder) + " show this mod's files wherever they don't bring their own: " + names
                        + ". To leave " + (outfits == 1 ? "it as it is" : "them as they are") + ", install this mod as an Extra outfit instead.");
                }
            }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); lines.Clear(); }
            return lines;
        }

        // The last build's outfits.json (none, unreadable or too large: nothing to warn about).
        private static List<Dictionary<string, object>> Rows(string gameRoot)
        {
            string file = Path.Combine(gameRoot, "reframework", "data", "SF6_Costumes_Data", "outfits.json");
            if (!File.Exists(file)) return new List<Dictionary<string, object>>();
            Safety.NoReparseChain(file);
            if (new FileInfo(file).Length > 16L * 1024 * 1024) return new List<Dictionary<string, object>>();
            return NativeModCheck.Rows(Json.Read<Dictionary<string, object>>(file), "outfits");
        }

        // A reported source ("Ingrid/Ingrid C1 Bare Legs Bundle.zip") in costume_mods or costume_mods_nsfw, read either way
        // the loader may have written it (LoaderResults.AsLoaderWrote); null when it isn't there.
        private static string OnDisk(string gameRoot, string source)
        {
            int slash = source.IndexOf('/');
            if (slash <= 0) return null;
            string character = source.Substring(0, slash), rest = source.Substring(slash + 1);
            foreach (string rootName in new[] { "costume_mods", "costume_mods_nsfw" })
            {
                string dir = Path.Combine(gameRoot, "reframework", rootName, character);
                if (!Directory.Exists(dir)) continue;
                foreach (string entry in Directory.EnumerateFileSystemEntries(dir))
                {
                    string relative = character + "/" + Path.GetFileName(entry);
                    if (String.Equals(relative, source, StringComparison.OrdinalIgnoreCase) || String.Equals(LoaderResults.AsLoaderWrote(relative), source, StringComparison.OrdinalIgnoreCase)) return entry;
                }
            }
            return null;
        }

        // Game paths a folder or ZIP ships (lowercase, from "natives/" on); null when it can't be listed (RAR, 7Z).
        private static HashSet<string> FilesOf(string path)
        {
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Action<string> add = delegate(string name)
            {
                string n = name.Replace('\\', '/');
                int at = n.IndexOf("natives/", StringComparison.OrdinalIgnoreCase);
                if (at >= 0 && (at == 0 || n[at - 1] == '/')) files.Add(n.Substring(at).ToLowerInvariant());
            };
            try
            {
                if (Directory.Exists(path)) { foreach (string f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)) add(f.Substring(path.Length)); return files; }
                if (!path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return null;
                using (ZipArchive zip = ZipFile.OpenRead(path)) foreach (ZipArchiveEntry e in zip.Entries) if (e.Length > 0) add(e.FullName);
                return files;
            }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); return null; }
        }
    }
}
