using System;
using System.Collections.Generic;
using System.Linq;

namespace SlotsInstaller
{
    // Shared path-only fighter identity for library scans and opened packages. Component ownership,
    // category labels, metadata and previews stay with their callers. Art needs the complete source:
    // one fighter's art contributes its code; a portrait set for several fighters does not.
    internal sealed class CharacterIdentity
    {
        internal struct NativeFile
        {
            internal bool Fighter;
            internal string ArtCode;
        }

        private readonly HashSet<string> codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> artCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        internal bool AddOutside(string path)
        {
            string colour = ModCategories.ColourDataFighter(path);
            if (colour == null) return false;
            codes.Add(colour);
            return true;
        }

        // The path after natives/, including the platform folder (e.g. /stm/product/...).
        internal NativeFile AddNative(string native)
        {
            var result = new NativeFile();
            foreach (string code in Library.FighterCodes(native)) { codes.Add(code); result.Fighter = true; }
            result.ArtCode = result.Fighter ? null : ModCategories.FighterArtCode(native);
            if (result.ArtCode != null) artCodes.Add(result.ArtCode);
            return result;
        }

        internal bool SingleFighterArt { get { return artCodes.Count == 1; } }

        internal List<string> Finish()
        {
            var result = new HashSet<string>(codes, StringComparer.OrdinalIgnoreCase);
            if (SingleFighterArt) result.Add(artCodes.First());
            return result.OrderBy(code => code, StringComparer.Ordinal).ToList();
        }
    }
}
