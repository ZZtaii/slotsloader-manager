using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace SlotsInstaller
{
    // Library search shared by the Mod library and the Assistant (2.0.1 adds costume terms).
    //   "aki, cammy"      character alternatives;  "aki, barefoot"  character + name words
    //   "cammy, c1"       mods whose native files change Cammy's costume folder 001 (also "c01",
    //                     "costume 1", "outfit 1", "o1", "costume:1"); several characters = any of them;
    //                     no character = any character's costume 1.
    // Costume matches rank first, then mods that only change the character's shared parts (000),
    // then mods whose title says "C1" (unreadable/unknown sources can only match by title).
    // 2.0.3 tool terms: "reframework", "code", "tool(s)", "tool mod(s)", "code mod(s)", "ref" list tool mods
    // (2.0.4: also any start of reframework/plugins/scripts/tools from three letters, e.g. "refra"),
    // "plugin(s)", "script(s)"/"lua" and "dll(s)" one kind, OR-ed with the usual name/path match; is:tool / type:tool
    // (Filters "Tool mod") keeps only tool mods.
    // 2.3.0 category terms: "stage(s)" and "misc" list that category (OR-ed with the name match, like the tool
    // words); is:stage / is:misc / is:character keep only that category (ModCategories).
    internal static class LibrarySearch
    {
        internal const int Direct = 0, SharedParts = 1, TitleOnly = 2;
        internal const string AnyTool = "tool";
        // 2.5.9: by:name / by:"two words" keeps mods whose modinfo.ini author contains the name.
        private static readonly Regex Tokens = new Regex(@"(^|[\s,])(?:(?:is|type):(bundle|normal|standalone|unreadable|tool|stage|misc|character)|format:(zip|rar|7z|folder)|costume:0*(\d{1,2})|by:(?:""([^""]+)""|([^\s,""]+)))(?=\s|,|$)", RegexOptions.IgnoreCase);
        private static readonly Regex CostumeWord = new Regex(@"^(?:c|o|costume|outfit)\s*0*(\d{1,2})$", RegexOptions.IgnoreCase);

        internal sealed class Query
        {
            internal string Plain = "", Type, Format, Author;
            internal bool Comma;
            internal HashSet<string> Characters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            internal List<string> Names = new List<string>();
            internal List<int> Costumes = new List<int>();
            // Tool keyword of the plain query, and of each comma name term that is one (term -> keyword).
            internal string PlainTool;
            internal Dictionary<string, string> NameTools = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            // 2.3.0: category word of the plain query and of comma name terms ("stage", "misc").
            internal string PlainCategory;
            // 2.4.1: the plain query is a character name, alias or unique start of one ("aki" = A.K.I, "cam" = Cammy):
            // that character's mods match too, besides the name/path substring.
            internal string PlainCharacter;
            internal Dictionary<string, string> NameCategories = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            internal bool HasCostume { get { return Costumes.Count > 0; } }
        }

        // "reframework"/"code"/"tool mods"/"ref" -> "tool"; "plugins" -> "plugin"; "lua"/"scripts" -> "script";
        // "dlls" -> "dll"; null for any other term. Case-insensitive; the long words also match from their
        // first three letters while typing ("ref", "refra", "plu", "scri", "too", "cod").
        internal static string ToolKeyword(string term)
        {
            string t = Regex.Replace((term ?? "").Trim().ToLowerInvariant(), @"\s+", " ");
            switch (t)
            {
                case "reframework": case "ref": case "refw": case "code": case "tool": case "tools":
                case "tool mod": case "tool mods": case "code mod": case "code mods": return AnyTool;
                case "plugin": case "plugins": return Library.ToolPlugin;
                case "script": case "scripts": case "lua": return Library.ToolScript;
                case "dll": case "dlls": return Library.ToolDll;
            }
            if (t.Length < 3) return null;
            if ("reframework".StartsWith(t, StringComparison.Ordinal) || "tools".StartsWith(t, StringComparison.Ordinal) || "code".StartsWith(t, StringComparison.Ordinal)) return AnyTool;
            if ("plugins".StartsWith(t, StringComparison.Ordinal)) return Library.ToolPlugin;
            if ("scripts".StartsWith(t, StringComparison.Ordinal)) return Library.ToolScript;
            return null;
        }

        private static bool HasTool(LibraryEntry e, string kind) { return e.ToolKinds != null && e.ToolKinds.Contains(kind); }

        // Code/tool mod: REFramework scripts or plugins, or other DLLs. REFramework data alone counts only
        // when the mod has no natives options, so costume mods carrying colour/data JSON stay costume mods.
        internal static bool IsToolMod(LibraryEntry e)
        {
            if (e == null || e.ToolKinds == null || e.ToolKinds.Count == 0) return false;
            return HasTool(e, Library.ToolScript) || HasTool(e, Library.ToolPlugin) || HasTool(e, Library.ToolDll) || (HasTool(e, Library.ToolReframework) && e.OptionCount == 0);
        }

        // Does `e` match a ToolKeyword result? "dll" also matches REFramework plugins (they are DLLs).
        internal static bool MatchesTool(LibraryEntry e, string keyword)
        {
            if (keyword == AnyTool) return IsToolMod(e);
            if (keyword == Library.ToolDll) return HasTool(e, Library.ToolDll) || HasTool(e, Library.ToolPlugin);
            return keyword != null && HasTool(e, keyword);
        }

        // "REFramework plugin" / "REFramework script" / "DLL" / "Script + plugin" / "REFramework files", or "".
        internal static string ToolLabel(LibraryEntry e)
        {
            if (!IsToolMod(e)) return "";
            var parts = new List<string>();
            if (HasTool(e, Library.ToolScript)) parts.Add("script");
            if (HasTool(e, Library.ToolPlugin)) parts.Add("plugin");
            if (HasTool(e, Library.ToolDll)) parts.Add("DLL");
            if (parts.Count == 0) return "REFramework files";
            if (parts.Count == 1) return parts[0] == "DLL" ? "DLL" : "REFramework " + parts[0];
            string text = String.Join(" + ", parts);
            return Char.ToUpperInvariant(text[0]) + text.Substring(1);
        }

        // "c1" -> 1, "costume 12" -> 12; null when the term is not a costume term.
        internal static int? CostumeNumber(string term)
        {
            Match m = CostumeWord.Match((term ?? "").Trim());
            int n;
            return m.Success && Int32.TryParse(m.Groups[1].Value, out n) && n > 0 ? n : (int?)null;
        }
        internal static string Folder(int costume) { return costume.ToString("000", CultureInfo.InvariantCulture); }
        internal static string Label(int costume) { return "C" + costume.ToString(CultureInfo.InvariantCulture); }

        internal static Query Parse(string text, Func<string, string> characterTerm)
        {
            var q = new Query();
            string query = Tokens.Replace((text ?? "").Trim(), delegate(Match match)
            {
                if (match.Groups[2].Success) q.Type = match.Groups[2].Value.ToLowerInvariant();
                if (match.Groups[3].Success) q.Format = match.Groups[3].Value;
                if (match.Groups[4].Success) { int n = Int32.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture); if (n > 0 && !q.Costumes.Contains(n)) q.Costumes.Add(n); }
                if (match.Groups[5].Success || match.Groups[6].Success) q.Author = (match.Groups[5].Success ? match.Groups[5].Value : match.Groups[6].Value).Trim();
                return match.Groups[1].Value;
            }).Trim().Trim(',').Trim();
            q.Comma = query.IndexOf(',') >= 0;
            if (q.Comma)
            {
                foreach (string term in query.Split(',').Select(part => part.Trim()).Where(part => part.Length > 0))
                {
                    string found = characterTerm(term); int? costume = CostumeNumber(term);
                    if (found != null) q.Characters.Add(found);
                    else if (costume.HasValue) { if (!q.Costumes.Contains(costume.Value)) q.Costumes.Add(costume.Value); }
                    else
                    {
                        q.Names.Add(term);
                        string tool = ToolKeyword(term);
                        if (tool != null) q.NameTools[term] = tool;
                        string category = ModCategories.Keyword(term);
                        if (category != null) q.NameCategories[term] = category;
                    }
                }
            }
            else
            {
                int? costume = CostumeNumber(query);
                string character = query.Length > 0 ? characterTerm(query) : null;
                if (costume.HasValue) { if (!q.Costumes.Contains(costume.Value)) q.Costumes.Add(costume.Value); }
                else if (character != null && q.HasCostume) q.Characters.Add(character);   // "cammy costume:1"
                else { q.Plain = query; q.PlainTool = ToolKeyword(query); q.PlainCategory = ModCategories.Keyword(query); q.PlainCharacter = character; }
            }
            return q;
        }

        internal static HashSet<string> CodesFor(IEnumerable<string> characters)
        {
            var names = new HashSet<string>(characters, StringComparer.OrdinalIgnoreCase);
            return new HashSet<string>(SlotsInstaller.Characters.ByCode.Where(kv => names.Contains(kv.Value)).Select(kv => kv.Key), StringComparer.OrdinalIgnoreCase);
        }

        private static bool TitleSaysCostume(LibraryEntry e, int costume)
        {
            var pattern = new Regex(@"(?<![A-Za-z0-9])(?:c|o|costume|outfit|cos)[\s_\-]*0*" + costume.ToString(CultureInfo.InvariantCulture) + @"(?![0-9])", RegexOptions.IgnoreCase);
            return pattern.IsMatch(e.Name ?? "") || pattern.IsMatch(Path.GetFileName(e.SourcePath ?? ""));
        }

        // Rank of `e` for the costume part of the query (Direct, SharedParts, TitleOnly), or -1.
        internal static int CostumeRank(LibraryEntry e, Query q, HashSet<string> codes, Func<LibraryEntry, string, bool> matchesCharacter)
        {
            if (!q.HasCostume) return Direct;
            var folders = new HashSet<string>(q.Costumes.Select(Folder));
            bool anyCharacter = q.Characters.Count == 0;
            var keys = e.CostumeKeys ?? new List<string>();
            if (keys.Any(k => folders.Contains(k.Substring(4)) && (anyCharacter || codes.Contains(k.Substring(0, 3))))) return Direct;
            // Only the shared parts (000) of the character: used by every outfit, so a secondary result.
            // Without a character term ("c1" alone) shared parts are not specific enough to list.
            var mine = keys.Where(k => codes.Contains(k.Substring(0, 3))).ToList();
            if (!anyCharacter && mine.Count > 0 && mine.All(k => k.EndsWith("/000"))) return SharedParts;
            // Unreadable or unknown sources have no keys: their title (and the character named in it) still matches.
            if (q.Costumes.Any(c => TitleSaysCostume(e, c)) && (anyCharacter || q.Characters.Any(name => matchesCharacter(e, name) || (e.Name ?? "").IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0))) return TitleOnly;
            return -1;
        }

        // 2.5.9: the author (modinfo.ini author=) contains `term`.
        internal static bool MatchesAuthor(LibraryEntry e, string term)
        {
            return !String.IsNullOrEmpty(term) && e.Authors != null && e.Authors.Any(a => a.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        // Text part of the query (characters, name words, plain substring). Costume terms are ranked separately.
        // 2.5.9: a plain word or comma name term also matches the author; by:name keeps only that author's mods.
        internal static bool MatchesText(LibraryEntry e, Query q, Func<LibraryEntry, string, bool> matchesCharacter, Func<LibraryEntry, string, bool> matchesName)
        {
            if (q.Author != null && !MatchesAuthor(e, q.Author)) return false;
            if (q.Comma)
            {
                // With a costume term the character is already applied by the costume rank (native key or title).
                bool characterOk = q.Characters.Count == 0 || q.HasCostume || q.Characters.Any(name => matchesCharacter(e, name));
                string tool, category;
                return characterOk && q.Names.All(term => matchesName(e, term) || MatchesAuthor(e, term) || (q.NameTools.TryGetValue(term, out tool) && MatchesTool(e, tool)) || (q.NameCategories.TryGetValue(term, out category) && ModCategories.InCategory(e, category)));
            }
            string query = q.Plain;
            return query.Length == 0 || (q.PlainCharacter != null && matchesCharacter(e, q.PlainCharacter)) || (q.PlainTool != null && MatchesTool(e, q.PlainTool)) || (q.PlainCategory != null && ModCategories.InCategory(e, q.PlainCategory)) || MatchesAuthor(e, query) || (e.Name ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 || (e.SourcePath ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 || (e.Character ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 || e.CharacterCodes.Any(code => ("esf" + code).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        // "C1, C2" / "Shared parts" / "" for the library row and inspector.
        internal static string CostumeLabel(LibraryEntry e)
        {
            var keys = e.CostumeKeys ?? new List<string>();
            if (keys.Count == 0) return "";
            var numbers = keys.Where(k => !k.EndsWith("/000")).Select(k => Int32.Parse(k.Substring(4), CultureInfo.InvariantCulture)).Distinct().OrderBy(n => n).ToList();
            if (numbers.Count == 0) return "Shared parts";
            return String.Join(", ", numbers.Take(4).Select(Label)) + (numbers.Count > 4 ? " +" + (numbers.Count - 4) : "") + (keys.Any(k => k.EndsWith("/000")) ? " + shared" : "");
        }

        // Costume choices present in the library, for the Filters overlay: 1, 2, 3 …
        internal static List<int> CostumesIn(IEnumerable<LibraryEntry> library)
        {
            return library.SelectMany(e => e.CostumeKeys ?? new List<string>()).Where(k => !k.EndsWith("/000")).Select(k => Int32.Parse(k.Substring(4), CultureInfo.InvariantCulture)).Distinct().OrderBy(n => n).ToList();
        }
    }
}
