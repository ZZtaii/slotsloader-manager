using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace SlotsInstaller
{
    // 1.0.25 (the user: "these shared materials aren't able to be added to outfit in recipe or as extra mod ... we
    // want to relax it but be more accurate to realities like shared materials"): the game builds many outfits from
    // other outfits' parts. Ingrid Outfit 5 wears the head and hair of Outfit 2, most Outfit 5s the hair of Outfit 1,
    // Zangief and Guile Outfit 1 the hair of Outfit 2, and many material files name another outfit's body or hair
    // textures. A mod for that part ships it in the other outfit's folder, and it fits the outfit that wears it:
    // as Replace original the game loads it there, and the loader takes such files into an extra outfit's slot.
    // The table (SharedOutfitParts.Data.cs) comes from the game's costume scenes and material files. An outfit whose
    // mod ships its own costume scene wears what that scene names instead (A.K.I. WT Swimwear: Outfit 2's body with
    // Outfit 4's head and hair, so a hair for the shared 000/02 doesn't show on it).
    internal static partial class SharedOutfitParts
    {
        private static readonly Regex ModelPath = new Regex(@"^natives/stm/(?:streaming/)?product/model/esf/esf(\d{3})/(\d{3})/(?:(\d{2})/)?([^/]+)$", RegexOptions.IgnoreCase);
        private static readonly Regex ScenePath = new Regex(@"^natives/stm/product/charparam/esf/esf(\d{3})/esf\1v\d{2}\.scn\.\d+$", RegexOptions.IgnoreCase);
        private static readonly Regex SceneMesh = new Regex(@"^@?\s*product/model/esf/esf(\d{3})/(\d{3})/(\d{2})/[^/]+\.mesh$", RegexOptions.IgnoreCase);

        // What an outfit wears: per part ("02"), the folders its meshes come from. From its own costume scene when its
        // mod ships one (Scene), else the game's outfit in that folder (Worn).
        internal sealed class Wear
        {
            internal readonly string Code, Folder;
            internal readonly Dictionary<string, List<string>> Scene;
            internal Wear(string code, string folder, Dictionary<string, List<string>> scene) { Code = code; Folder = folder; Scene = scene; }
            internal List<string> Of(string part)
            {
                List<string> own;
                if (Scene != null && Scene.TryGetValue(part, out own)) return own;
                return GameWorn(Code, Folder, part);
            }
        }

        internal static bool IsScene(string path) { return ScenePath.IsMatch((path ?? "").Replace('\\', '/')); }

        // The parts a costume scene's meshes come from ("02" -> "004"), with the folder of its body (part 01, not 000).
        internal static Dictionary<string, List<string>> ReadScene(byte[] data, out string code, out string body)
        {
            code = null; body = null;
            var parts = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            if (data == null) return parts;
            for (int i = 0; i + 1 < data.Length; i += 2)
            {
                int j = i; var s = new StringBuilder();
                while (j + 1 < data.Length && data[j + 1] == 0 && data[j] >= 0x20 && data[j] < 0x7f) { s.Append((char)data[j]); j += 2; }
                if (s.Length > 8)
                {
                    Match m = SceneMesh.Match(s.ToString());
                    if (m.Success && (code == null || code == m.Groups[1].Value))
                    {
                        code = m.Groups[1].Value;
                        string folder = m.Groups[2].Value, part = m.Groups[3].Value;
                        if (part == "01" && folder != "000" && body == null) body = folder;
                        List<string> list;
                        if (!parts.TryGetValue(part, out list)) parts[part] = list = new List<string>();
                        if (!list.Contains(folder)) list.Add(folder);
                    }
                }
                i = j;
            }
            return parts;
        }
        // The costume scene among these files whose body is in `outfitFolder`, read as above; null when there is none.
        internal static Dictionary<string, List<string>> OwnScene(IEnumerable<string> paths, Func<string, byte[]> read, string outfitFolder)
        {
            foreach (string path in (paths ?? new string[0]).Where(IsScene))
            {
                byte[] data;
                try { data = read(path); } catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); continue; }
                string code, body;
                Dictionary<string, List<string>> parts = ReadScene(data, out code, out body);
                if (body == outfitFolder && parts.Count > 0) return parts;
            }
            return null;
        }
        // The same for an installed outfit (or a staged one): its natives files on disk under `root`.
        internal static Dictionary<string, List<string>> OwnScene(string root, string outfitFolder)
        {
            string esf = Path.Combine(root ?? "", "natives", "stm", "product", "charparam", "esf");
            if (!Directory.Exists(esf)) return null;
            var paths = Directory.EnumerateFiles(esf, "*.scn.*", SearchOption.AllDirectories).Select(f => "natives/stm/product/charparam/esf/" + f.Substring(esf.Length).TrimStart('\\').Replace('\\', '/'));
            return OwnScene(paths, p => File.ReadAllBytes(Path.Combine(root, p.Replace('/', '\\'))), outfitFolder);
        }

        // Whether an outfit that wears `wear` loads this file of a folder other than its own: a part it takes from that
        // folder (any of its files), a texture of another part its materials name, a physics file of such a part or of
        // the same part another outfit wears from the same place, or a shared (000) file: textures and the fighter's
        // own 000 files always, a part's mesh and material only when the outfit wears that part from 000.
        internal static bool Takes(Wear wear, string path)
        {
            Match m = ModelPath.Match((path ?? "").Replace('\\', '/'));
            if (wear == null || !m.Success || m.Groups[1].Value != wear.Code) return false;
            string code = wear.Code, folder = m.Groups[2].Value, part = m.Groups[3].Value, name = m.Groups[4].Value.ToLowerInvariant();
            bool texture = name.Contains(".tex.");
            if (folder == wear.Folder) return true;
            if (folder == "000")
            {
                if (part.Length == 0 || texture) return true;
                List<string> worn = wear.Of(part);
                return worn.Count == 0 || worn.Contains("000");
            }
            string[] list;
            if (!Borrowed.TryGetValue(code + "/" + wear.Folder, out list)) list = new string[0];
            bool sceneHas = wear.Scene != null && part.Length > 0 && wear.Scene.ContainsKey(part);
            if (part.Length == 0)
            {
                // A file of that folder's own part NN ("002/esf013_002_02_chain.chain.52": Outfit 2's chain for its hair)
                string prefix = "esf" + code + "_" + folder + "_";
                if (!name.StartsWith(prefix, StringComparison.Ordinal) || name.Length < prefix.Length + 3 || name[prefix.Length + 2] != '_') return false;
                string nn = name.Substring(prefix.Length, 2);
                List<string> mine = wear.Of(nn);
                if (mine.Contains(folder)) return true;
                if ((wear.Scene == null || !wear.Scene.ContainsKey(nn)) && list.Contains(folder + "/" + nn)) return true;
                // A.K.I Emily Hair "C1, C2": the hair in 000/02 and a chain for each outfit that wears it (001, 002, 005):
                // the other outfit wears the same part from the same place, so it is that part's physics for the other
                // outfit; this outfit loads its own and leaves it be.
                return mine.Intersect(GameWorn(code, folder, nn)).Any();
            }
            if (wear.Of(part).Contains(folder)) return true;
            if (!sceneHas && list.Contains(folder + "/" + part)) return true;
            return texture && list.Contains("tex:" + folder + "/" + part);
        }

        // The folders the game outfit in `outfitFolder` takes part `part` from ("000" for a shared one); empty when not known.
        internal static List<string> GameWorn(string code, string outfitFolder, string part)
        {
            string[] list;
            if (!Worn.TryGetValue(code + "/" + outfitFolder, out list)) return new List<string>();
            return list.Where(e => e.StartsWith(part + ":", StringComparison.Ordinal)).Select(e => e.Substring(part.Length + 1)).ToList();
        }
        internal static string WornFolder(string code, string outfitFolder, string part) { return GameWorn(code, outfitFolder, part).FirstOrDefault(); }

        internal static string PartName(string part)
        {
            switch (part) { case "00": return "head"; case "01": return "body"; case "02": return "hair"; case "10": return "face"; default: return "part " + part; }
        }
        // "the shared hair (this outfit wears Outfit 4's)": why shared files of these parts don't fit `wear`.
        internal static string SharedText(IEnumerable<string> parts, Wear wear)
        {
            var list = parts.Distinct().OrderBy(p => p, StringComparer.Ordinal).ToList();
            var from = list.SelectMany(p => wear == null ? new List<string>() : wear.Of(p)).Where(f => f != "000").Distinct().OrderBy(f => f, StringComparer.Ordinal).ToList();
            string whose = from.Count == 0 ? "" : " (" + (wear != null && wear.Scene != null ? "this outfit's own scene uses " : "this outfit wears ")
                + String.Join(" and ", from.Select(f => f == wear.Folder ? "its own" : "Outfit " + Int32.Parse(f) + "'s")) + ")";
            return "the shared " + String.Join(" and ", list.Select(PartName)) + whose;
        }

        // Whether that outfit takes anything from `folder` (a library mod's costume folder, before it is opened).
        internal static bool TakesFrom(string code, string outfitFolder, string folder)
        {
            string[] list;
            return Borrowed.TryGetValue(code + "/" + outfitFolder, out list)
                && list.Any(e => (e.StartsWith("tex:", StringComparison.Ordinal) ? e.Substring(4) : e).StartsWith(folder + "/", StringComparison.Ordinal));
        }
    }
}
