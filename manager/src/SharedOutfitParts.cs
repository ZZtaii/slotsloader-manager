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
            string[] list;
            if (folder == "000" || folder == outfitFolder || !Borrowed.TryGetValue(code + "/" + outfitFolder, out list)) return false;
            if (part.Length == 0)
                return list.Any(e => !e.StartsWith("tex:", StringComparison.Ordinal) && e.StartsWith(folder + "/", StringComparison.Ordinal)
                    && name.StartsWith("esf" + code + "_" + folder + "_" + e.Substring(4, 2) + "_", StringComparison.Ordinal));
            string key = folder + "/" + part;
            return list.Contains(key) || (name.Contains(".tex.") && list.Contains("tex:" + key));
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
