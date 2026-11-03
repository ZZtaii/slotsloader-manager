using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace SlotsInstaller
{
    // 1.0.16 (the user): the Filters tile "REFramework & code" replaced "Tool mods", which listed every DLL in the
    // libraries (Downloads folders: Skyrim SKSE plugins, ENB, Engine Fixes, 7-Zip patches). It lists SF6 code only, by
    // kind (its chips), decided from the paths of the archive or folder (Classify, pure) in the listing pass and kept in
    // the index (LibraryEntry/LibraryIdentityRecord.CodeKinds):
    //   script      - reframework/autorun/**/*.lua
    //   plugin      - reframework/plugins/**/*.dll
    //   reframework - REFramework itself (dinput8.dll at the game-root level with reframework_revision.txt, a
    //                 reframework/ folder or "REFramework" in the name), and other reframework/ files (data, fonts,
    //                 images) of a mod without natives files (costume mods carrying REFramework data stay costume
    //                 mods, as for IsToolMod)
    //   injector    - DLLs SF6 loads from its folder (dinput8 when not REFramework, dxgi, d3d12, version, winmm,
    //                 amd_ags_x64 = Slotsloader's proxy) and .asi files at the game-root level (the archive root or one
    //                 wrapper folder; .asi also in a scripts/ or plugins/ folder there), ReShade (ReShade64.dll,
    //                 ReShade.ini, reshade-shaders/)
    // Another game's mod (skse/f4se/sfse/obse/nvse folders, ENB, .esp .esm .esl .bsa .ba2 .pex files, Mod Engine,
    // regulation.bin anywhere) has none, unless it also has reframework/ files. Colour data keyed to a fighter is that
    // fighter's (ModCategories.ColourDataFighter), as in the listing pass. Other DLLs are no SF6 code; they still install
    // as tool mods (LibrarySearch.IsToolMod is unchanged).
    internal static class LibraryCode
    {
        internal const string Script = "script", Plugin = "plugin", Reframework = "reframework", Injector = "injector";
        internal static readonly string[] Order = { Script, Plugin, Reframework, Injector };
        internal static readonly string[] Labels = { "Scripts", "Plugins", "REFramework", "Code injectors" };   // the chips, in Order
        internal static string Label(string kind) { int i = Array.IndexOf(Order, kind); return i < 0 ? kind : Labels[i]; }
        private static readonly HashSet<string> Proxies = new HashSet<string>(new[] { "dinput8.dll", "dxgi.dll", "d3d12.dll", "version.dll", "winmm.dll", "amd_ags_x64.dll", "reshade64.dll", "reshade.ini" }, StringComparer.OrdinalIgnoreCase);
        private static readonly Regex OtherGameFolder = new Regex(@"^(skse|skse64|f4se|sfse|obse|obse64|nvse|xnvse|enbseries|modengine\d*)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex OtherGameFile = new Regex(@"\.(esp|esm|esl|bsa|ba2|pex)$|^(skse64|skse|f4se|sfse|obse64|obse|nvse)_|^(enbseries|enblocal)\.ini$|^regulation\.bin$|^modengine", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // The code kinds of one archive/folder (Order, no duplicates) from its entry paths ("/" or "\") and its name.
        internal static List<string> Classify(IEnumerable<string> paths, string name)
        {
            bool natives = false, otherGame = false, rfFolder = false, rfOther = false, revision = false, dinput8 = false, injector = false;
            var found = new HashSet<string>(StringComparer.Ordinal);
            foreach (string raw in paths ?? new string[0])
            {
                string path = (raw ?? "").Replace('\\', '/').Trim('/');
                if (path.Length == 0) continue;
                string[] parts = path.Split('/');
                if (parts.Any(p => p.Equals("__MACOSX", StringComparison.OrdinalIgnoreCase))) continue;
                string leaf = parts[parts.Length - 1];
                for (int i = 0; i < parts.Length - 1 && !otherGame; i++) if (OtherGameFolder.IsMatch(parts[i])) otherGame = true;
                if (OtherGameFile.IsMatch(leaf)) otherGame = true;
                if (parts.Any(p => p.Equals("natives", StringComparison.OrdinalIgnoreCase))) { natives = true; continue; }
                int rf = Array.FindIndex(parts, p => p.Equals("reframework", StringComparison.OrdinalIgnoreCase));
                if (rf >= 0 && rf < parts.Length - 1)
                {
                    rfFolder = true;
                    if (ModCategories.ColourDataFighter(path) != null) continue;
                    string tool = Library.ToolKind(parts);
                    if (tool == Library.ToolScript) found.Add(Script);
                    else if (tool == Library.ToolPlugin) found.Add(Plugin);
                    else if (tool != null) rfOther = true;
                    continue;
                }
                string lower = leaf.ToLowerInvariant();
                bool top = parts.Length <= 2, asi = lower.EndsWith(".asi", StringComparison.Ordinal);
                if (top && lower == "reframework_revision.txt") revision = true;
                if (top && lower == "dinput8.dll") dinput8 = true;
                else if (top && (Proxies.Contains(lower) || asi)) injector = true;
                else if (asi && parts.Length <= 3 && Regex.IsMatch(parts[parts.Length - 2], "^(scripts|plugins)$", RegexOptions.IgnoreCase)) injector = true;
                for (int i = 0; i < Math.Min(2, parts.Length - 1); i++) if (parts[i].Equals("reshade-shaders", StringComparison.OrdinalIgnoreCase)) injector = true;
            }
            if (otherGame && !rfFolder) return new List<string>();
            bool framework = dinput8 && (revision || rfFolder || (name ?? "").IndexOf("reframework", StringComparison.OrdinalIgnoreCase) >= 0);
            if (framework || (rfOther && !natives)) found.Add(Reframework);
            if (injector || (dinput8 && !framework)) found.Add(Injector);
            return Order.Where(found.Contains).ToList();
        }

        // A kind of the mod: its CodeKinds, or until they are known (an index record from before 1.0.16, read again in
        // the background) a conservative guess from ToolKinds: scripts, plugins and REFramework files without natives;
        // a plain DLL is no guess (most were other games' mods).
        internal static bool Has(LibraryEntry e, string kind)
        {
            if (e == null) return false;
            if (e.CodeKinds != null) return e.CodeKinds.Contains(kind);
            List<string> tools = e.ToolKinds;
            if (tools == null || tools.Count == 0) return false;
            if (kind == Script) return tools.Contains(Library.ToolScript);
            if (kind == Plugin) return tools.Contains(Library.ToolPlugin);
            return kind == Reframework && tools.Contains(Library.ToolReframework) && e.OptionCount == 0;
        }
        internal static bool Any(LibraryEntry e)
        {
            if (e == null) return false;
            if (e.CodeKinds != null) return e.CodeKinds.Count > 0;
            return Has(e, Script) || Has(e, Plugin) || Has(e, Reframework);
        }
        internal static bool AnyOf(LibraryEntry e, ICollection<string> kinds) { foreach (string k in kinds) if (Has(e, k)) return true; return false; }
    }
}
