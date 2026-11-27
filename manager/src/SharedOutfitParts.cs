using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace SlotsInstaller
{
    // 1.0.25 (the user: "these shared materials aren't able to be added to outfit in recipe or as extra mod ... we
    // want to relax it but be more accurate to realities like shared materials"): the game builds many outfits from
    // other outfits' parts. Ingrid Outfit 5 wears the head and hair of Outfit 2, most Outfit 5s the hair of Outfit 1,
    // Zangief and Guile Outfit 1 the hair of Outfit 2, and many material files name another outfit's body or hair
    // textures. A mod for that part ships it in the other outfit's folder, and it fits the outfit that wears it:
    // as Replace original the game loads it there, and the loader takes such files into an extra outfit's slot.
    // The table (SharedOutfitParts.Data.cs) comes from the game's costume scenes and material files.
    internal static partial class SharedOutfitParts
    {
        private static readonly Regex ModelPath = new Regex(@"^natives/stm/(?:streaming/)?product/model/esf/esf(\d{3})/(\d{3})/(?:(\d{2})/)?([^/]+)$", RegexOptions.IgnoreCase);

        // Whether the game's outfit in `outfitFolder` (its body's folder) loads this file of another outfit's folder:
        // any file of a part whose mesh or material it wears, a texture of another part its materials name, or a
        // physics file of a part it wears ("002/esf032_002_02_chain.chain.52").
        internal static bool Takes(string outfitFolder, string path)
        {
            Match m = ModelPath.Match((path ?? "").Replace('\\', '/'));
            if (!m.Success) return false;
            string code = m.Groups[1].Value, folder = m.Groups[2].Value, part = m.Groups[3].Value, name = m.Groups[4].Value.ToLowerInvariant();
            if (folder == "000" || folder == outfitFolder) return false;
            string[] list;
            if (!Borrowed.TryGetValue(code + "/" + outfitFolder, out list)) list = new string[0];
            if (part.Length == 0)
            {
                // A file of that folder's own part NN ("002/esf013_002_02_chain.chain.52": Outfit 2's chain for its hair)
                string prefix = "esf" + code + "_" + folder + "_";
                if (!name.StartsWith(prefix, StringComparison.Ordinal) || name.Length < prefix.Length + 3 || name[prefix.Length + 2] != '_') return false;
                string nn = name.Substring(prefix.Length, 2);
                if (list.Any(e => !e.StartsWith("tex:", StringComparison.Ordinal) && e == folder + "/" + nn)) return true;
                // 1.0.25 (A.K.I Emily Hair "C1, C2": the hair in 000/02 and a chain for each outfit that wears it, 001, 002,
                // 005): the other outfit wears the same part as this one, so it is that part's physics for the other
                // outfit; this outfit loads its own and leaves it be.
                string mine = WornFolder(code, outfitFolder, nn), theirs = WornFolder(code, folder, nn);
                return mine != null && mine == theirs;
            }
            string key = folder + "/" + part;
            return list.Contains(key) || (name.Contains(".tex.") && list.Contains("tex:" + key));
        }
        // The folder the game outfit in `outfitFolder` takes part `part` from ("000" for a shared one), or null.
        internal static string WornFolder(string code, string outfitFolder, string part)
        {
            string[] list;
            if (!Worn.TryGetValue(code + "/" + outfitFolder, out list)) return null;
            string hit = list.FirstOrDefault(e => e.StartsWith(part + ":", StringComparison.Ordinal));
            return hit == null ? null : hit.Substring(part.Length + 1);
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
