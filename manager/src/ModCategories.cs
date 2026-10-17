using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace SlotsInstaller
{
    // 2.3.0 library categories, decided in the library listing pass (index v4). A mod can be in Characters and
    // Code tools; Misc only holds mods in neither (stage mods are the Misc kind "Stage"):
    //   Characters - fighter files: any /esf/esfNNN/ or /esfNNN_ native path (model incl. colour _cmd_ files,
    //                charparam, vfx/character, weather/wind, animation ...), the fighter's own art
    //                (gui/data/character_image/NNN/), and REFramework colour data keyed to a fighter
    //                (a colour folder plus an esfNNN or fighter-name segment, e.g. reframework/data/SF6ColorSync/Juri/);
    //                i.e. LibraryEntry.CharacterCodes;
    //   Stage      - (Misc kind) native files holding a stage code essNNNN (environment, level, camera, light, vfx, sound,
    //                weather, gui area images ...), plus stage folders without a code (product/environment/,
    //                esmcontents/stage, a stage-name message);
    //   Code tools - the 2.0.3 tool kinds (LibrarySearch.IsToolMod);
    //   Misc       - a readable mod in none of these, labelled by what it changes (MiscLabel: UI, Sound,
    //                World Tour avatar ...; "PAK only" / "Other").
    internal static class ModCategories
    {
        internal const string Characters = "characters", Stages = "stages", Tools = "tools", Misc = "misc";
        // Stages is the Misc kind "Stage" (search: stage, is:stage), not a category of its own.
        internal const string OtherStage = "other";
        internal const string LabelPak = "PAK only", LabelOther = "Other", LabelStage = "Stage";
        // Misc labels in display order; raw folder names never reach the UI (rare ones are "Other").
        internal static readonly string[] LabelOrder = { LabelStage, "UI", "Text", "Sound", "VFX", "Lighting", "Camera", "Animation", "World Tour avatar", "NPCs", "Shared character files", "Materials", "Weather", LabelPak, LabelOther };

        private static readonly Regex EssCode = new Regex(@"(?<![a-z0-9])ess(\d{4})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex StageFolder = new Regex(@"^product/(?:environment/|esmcontents/stage/|level/scene/ess|camera/scene/ess/|weather/wind/ess/|light/[^/]+/ess/|vfx/environment/|sound/sound_scene/ess/|message/[^/]+/(?:[^/]+/)*stagename)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex FighterArt = new Regex(@"^product/gui/data/character_image/(\d{3})/", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex EsfSegment = new Regex(@"(?<![a-z0-9])esf(\d{3})(?![0-9])", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex ColourWord = new Regex(@"colou?r", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Stages are keyed by their four-digit number ("0410" = ess0410_00, Pao Pao Cafe 6). A file's "_NN" after the
        // code is usually a mesh or texture index, not a stage variant, so it is ignored; message entries of
        // another variant keep it ("0101_01").
        internal static string StageKey(int stage, int variant) { return stage.ToString("0000", CultureInfo.InvariantCulture) + (variant == 0 ? "" : "_" + variant.ToString("00", CultureInfo.InvariantCulture)); }

        // The part of a native path after natives/<platform>/ (and streaming/), lower case:
        // "natives/stm/streaming/product/model/x" -> "product/model/x".
        internal static string Relative(string nativeAfterNatives)
        {
            string p = (nativeAfterNatives ?? "").Replace('\\', '/').TrimStart('/').ToLowerInvariant();
            int slash = p.IndexOf('/');
            if (slash > 0 && (p.StartsWith("stm/") || p.StartsWith("x64/") || p.StartsWith("msg/"))) p = p.Substring(slash + 1);
            if (p.StartsWith("streaming/")) p = p.Substring(10);
            return p;
        }

        // A fighter's own art outside model/esf (gui/data/character_image/NNN/...): its code, else null.
        internal static string FighterArtCode(string nativeAfterNatives)
        {
            Match m = FighterArt.Match(Relative(nativeAfterNatives));
            return m.Success ? m.Groups[1].Value : null;
        }

        // REFramework colour data keyed to a fighter (outside natives): "reframework/data/SF6ColorSync/Juri/x.json",
        // ".../colors/esf016_c1.json", ".../colours/cammy.json" -> the fighter code, else null.
        internal static string ColourDataFighter(string path)
        {
            string p = (path ?? "").Replace('\\', '/');
            int rf = p.IndexOf("reframework/", StringComparison.OrdinalIgnoreCase);
            if (rf < 0) return null;
            string inside = p.Substring(rf + 12);
            if (!ColourWord.IsMatch(inside)) return null;
            Match esf = EsfSegment.Match(inside);
            if (esf.Success) return esf.Groups[1].Value;
            foreach (string segment in inside.Split('/'))
            {
                string stem = segment.Contains('.') ? segment.Substring(0, segment.IndexOf('.')) : segment;
                string code = FighterCodeOfName(stem);
                if (code != null) return code;
            }
            return null;
        }
        // "Juri", "chun-li", "MBison", "aki" -> code; null for anything else (whole segment only).
        internal static string FighterCodeOfName(string text)
        {
            string compact = new string((text ?? "").ToLowerInvariant().Where(Char.IsLetterOrDigit).ToArray());
            if (compact.Length < 2) return null;
            foreach (var kv in SlotsInstaller.Characters.ByCode)
                if (compact == new string(kv.Value.ToLowerInvariant().Where(Char.IsLetterOrDigit).ToArray())) return kv.Key;
            switch (compact) { case "viper": return "030"; case "honda": return "020"; case "bison": return "026"; case "chunli": return "004"; }
            return null;
        }

        // One native file (the path after the natives/ folder): its stage key (or "other") when it is stage
        // content, else null. Fighter files are never stage content.
        internal static string StageOf(string nativeAfterNatives, bool fighterFile)
        {
            if (fighterFile) return null;
            string p = Relative(nativeAfterNatives);
            Match m = EssCode.Match(p);
            if (m.Success) return StageKey(Int32.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), 0);
            return StageFolder.IsMatch(p) ? OtherStage : null;
        }

        // Friendly Misc label of a native file that is neither fighter nor stage content.
        internal static string MiscLabel(string nativeAfterNatives)
        {
            string p = Relative(nativeAfterNatives);
            string[] parts = p.Split('/');
            int at = parts[0] == "product" ? 1 : 0;
            string top = at < parts.Length - 1 ? parts[at] : "";
            string next = at + 1 < parts.Length - 1 ? parts[at + 1] : "";
            switch (top)
            {
                case "gui": case "ui": return "UI";
                case "message": return "Text";
                case "sound": return "Sound";
                case "vfx": return "VFX";
                case "light": return "Lighting";
                case "camera": return "Camera";
                case "animation": case "sectionroot": return "Animation";
                case "model":
                    if (next == "wcs" || next == "wpl" || next == "wcf" || next == "wcm") return "World Tour avatar";
                    if (next == "mob" || next == "npc") return "NPCs";
                    if (next == "cmn" || next == "esf") return "Shared character files";
                    return LabelOther;
                case "mastermaterial": case "systems": return "Materials";
                case "weather": return "Weather";
            }
            return LabelOther;   // charparam/character/escape without a fighter code, scenes, loose files ...
        }

        // Raw top folder -> label, for the documentation and the report (MiscLabel applies it).
        internal static readonly string[][] LabelMap = {
            new[] { "gui/, ui/", "UI" }, new[] { "message/", "Text" }, new[] { "sound/", "Sound" }, new[] { "vfx/ (no fighter or stage code)", "VFX" },
            new[] { "light/ (no stage code)", "Lighting" }, new[] { "camera/", "Camera" }, new[] { "animation/, sectionroot/animation", "Animation" },
            new[] { "model/wcs, wpl, wcf, wcm", "World Tour avatar" }, new[] { "model/mob, npc", "NPCs" }, new[] { "model/cmn, loose model/*.tex", "Shared character files" },
            new[] { "mastermaterial/, systems/rendering", "Materials" }, new[] { "weather/ (no fighter or stage code)", "Weather" },
            new[] { "only .pak files", LabelPak }, new[] { "character/, escape/, charparam/ without esfNNN, scene/, objectroot/, anything else, no natives", LabelOther } };

        // Accumulates one archive's files during the listing pass.
        internal sealed class Collector
        {
            internal readonly HashSet<string> Stages = new HashSet<string>(StringComparer.Ordinal);
            internal readonly HashSet<string> Misc = new HashSet<string>(StringComparer.Ordinal);
            internal bool AnyNative, AnyFighter, AnyPak, AnyFile;
            internal void AddNative(string nativeAfterNatives, bool fighterFile)
            {
                AnyNative = true; AnyFile = true;
                if (fighterFile) { AnyFighter = true; return; }
                string stage = StageOf(nativeAfterNatives, false);
                if (stage != null) { Stages.Add(stage); return; }
                Misc.Add(MiscLabel(nativeAfterNatives));
            }
            internal void AddFighterData() { AnyFile = true; AnyFighter = true; }
            internal void AddOther(string path)
            {
                AnyFile = true;
                if (path != null && path.EndsWith(".pak", StringComparison.OrdinalIgnoreCase)) AnyPak = true;
            }
            internal List<string> StageList() { return Stages.OrderBy(s => s == OtherStage ? "~" : s, StringComparer.Ordinal).ToList(); }
            // Misc only for a mod in no other category ("costume mods stay out of Misc"); stage content is the
            // Misc kind "Stage" (stage packs bring their stages' NPCs, which are not listed separately).
            internal List<string> MiscList(bool toolMod)
            {
                if (AnyFighter || toolMod) return new List<string>();
                var labels = new HashSet<string>(Misc, StringComparer.Ordinal);
                if (Stages.Count > 0) { labels.Add(LabelStage); labels.Remove("NPCs"); }
                if (labels.Count == 0) labels.Add(AnyPak && !AnyNative ? LabelPak : LabelOther);
                return SortLabels(labels);
            }
        }

        internal static List<string> SortLabels(IEnumerable<string> labels)
        {
            return labels.Distinct().OrderBy(l => { int i = Array.IndexOf(LabelOrder, l); return i < 0 ? LabelOrder.Length : i; }).ThenBy(l => l, StringComparer.OrdinalIgnoreCase).ToList();
        }

        internal static bool IsCharacter(LibraryEntry e) { return e != null && ((e.CharacterCodes != null && e.CharacterCodes.Count > 0) || (e.CostumeKeys != null && e.CostumeKeys.Count > 0)); }
        internal static bool IsTool(LibraryEntry e) { return LibrarySearch.IsToolMod(e); }
        internal static bool IsMisc(LibraryEntry e) { return e != null && e.MiscAreas != null && e.MiscAreas.Count > 0; }
        // 2.3.1: a Misc (any kind) or Code tools mod without fighter files gets no fighter from its file or folder
        // name ("Back Spotlight Adjuster - Alex Patch" is not an Alex mod). Mods with fighter files never use the guess.
        internal static bool NameGuessAllowed(LibraryEntry e) { return e != null && ((e.CharacterCodes != null && e.CharacterCodes.Count > 0) || !(IsMisc(e) || IsTool(e))); }
        // A stage mod: Misc of kind "Stage" (a mod with fighter files and stage files is a Characters mod).
        internal static bool IsStage(LibraryEntry e) { return IsMisc(e) && e.MiscAreas.Contains(LabelStage); }
        internal static bool HasMiscKind(LibraryEntry e, string kind) { return IsMisc(e) && (String.IsNullOrEmpty(kind) || e.MiscAreas.Contains(kind)); }

        internal static bool InCategory(LibraryEntry e, string category)
        {
            switch (category)
            {
                case Characters: return IsCharacter(e);
                case Stages: return IsStage(e);
                case Tools: return IsTool(e);
                case Misc: return IsMisc(e);
            }
            return true;
        }

        // "Characters, Code tools" / "Misc (Stage, Text)" for diagnostics and reports.
        internal static string Describe(LibraryEntry e)
        {
            var parts = new List<string>();
            if (IsCharacter(e)) parts.Add("Characters");
            if (IsTool(e)) parts.Add("Code tools");
            if (IsMisc(e)) parts.Add("Misc (" + String.Join(", ", e.MiscAreas) + ")");
            return parts.Count == 0 ? (e != null && e.OptionCount < 0 ? "Unreadable" : "none") : String.Join(", ", parts);
        }

        // Stage display name: the game's name when known ("Training Room"), else the code ("Stage ess0000_00").
        internal static string StageName(string key, IDictionary<string, string> names)
        {
            if (key == OtherStage) return "Other stage files";
            string name;
            if (names != null && names.TryGetValue(key, out name) && !String.IsNullOrWhiteSpace(name)) return name;
            return "Stage ess" + key + (key.Length == 4 ? "_00" : "");
        }

        // Short row label of a Misc mod (costume and tool labels come first): "Stage · Training Room +19" / "UI · Sound".
        internal static string RowLabel(LibraryEntry e, IDictionary<string, string> stageNames)
        {
            if (!IsMisc(e)) return "";
            if (IsStage(e))
            {
                string unused; var named = (e.StageIds ?? new List<string>()).Where(s => s != OtherStage).Select(s => stageNames != null && stageNames.TryGetValue(s, out unused) ? StageName(s, stageNames) : "ess" + s + (s.Length == 4 ? "_00" : "")).Distinct().ToList();
                if (named.Count == 0) return "Stage files";
                return "Stage · " + named[0] + (named.Count > 1 ? " +" + (named.Count - 1) : "");
            }
            return String.Join(" · ", e.MiscAreas.Take(3)) + (e.MiscAreas.Count > 3 ? " +" + (e.MiscAreas.Count - 3) : "");
        }

        // Plain search words for categories (OR-ed with the name match, like the tool words).
        internal static string Keyword(string term)
        {
            string t = Regex.Replace((term ?? "").Trim().ToLowerInvariant(), @"\s+", " ");
            switch (t)
            {
                case "stage": case "stages": case "stage mod": case "stage mods": return Stages;
                case "misc": case "misc mods": case "other mods": return Misc;
            }
            return null;
        }
    }
}