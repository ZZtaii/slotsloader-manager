using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
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
        internal static bool PakKeyword(string term) { return Regex.IsMatch((term ?? "").Trim(), @"^pak(?:\s+mods?)?$", RegexOptions.IgnoreCase); }
        internal static bool MatchesPak(LibraryEntry e, string term)
        {
            return PakKeyword(term) && (e.HasPakPayload ?? (e.MiscAreas != null && e.MiscAreas.Contains(ModCategories.LabelPak)));
        }
        // 2.5.9: by:name / by:"two words" keeps mods whose modinfo.ini author contains the name.
        private static readonly Regex Tokens = new Regex(@"(^|[\s,])(?:(?:is|type):(bundle|normal|standalone|unreadable|tool|stage|misc|character)|format:(zip|rar|7z|folder)|costume:0*(\d{1,2})|by:(?:""([^""]+)""|([^\s,""]+)))(?=\s|,|$)", RegexOptions.IgnoreCase);
        private static readonly Regex CostumeWord = new Regex(@"^(?:c|o|costume|outfit)\s*0*(\d{1,2})$", RegexOptions.IgnoreCase);

        // 1.0.6: one word of a plain query ("juri barefoot" = juri AND barefoot); a fighter spelled in several words
        // ("chun li", "dee jay") stays one word.
        internal sealed class Word { internal string Text, Character, Tool, Category; }

        internal sealed class Query
        {
            internal string Plain = "", Type, Format, Author;
            internal bool Comma;
            // 1.0.6 deep search: name words also match the mod's option folders and option names (a bundle's
            // "Barefoot" variant), not only its own names.
            internal bool Deep;
            internal List<Word> PlainWords = new List<Word>();
            internal Dictionary<string, List<Word>> NameWords = new Dictionary<string, List<Word>>(StringComparer.OrdinalIgnoreCase);
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

        internal static Query Parse(string text, Func<string, string> characterTerm) { return Parse(text, characterTerm, false); }
        internal static Query Parse(string text, Func<string, string> characterTerm, bool deep)
        {
            var q = new Query { Deep = deep };
            string query = Tokens.Replace((text ?? "").Trim(), delegate(Match match)
            {
                if (match.Groups[2].Success) q.Type = match.Groups[2].Value.ToLowerInvariant();
                if (match.Groups[3].Success) q.Format = match.Groups[3].Value;
                if (match.Groups[4].Success) { int n = Int32.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture); if (n > 0 && !q.Costumes.Contains(n)) q.Costumes.Add(n); }
                if (match.Groups[5].Success || match.Groups[6].Success) q.Author = (match.Groups[5].Success ? match.Groups[5].Value : match.Groups[6].Value).Trim();
                return match.Groups[1].Value;
            });
            // 1.0.6: ";" and full-width commas separate terms like ","; quotes and runs of spaces don't matter.
            query = Regex.Replace(query.Replace(';', ',').Replace('，', ',').Replace('،', ',').Replace("\"", " "), @"\s+", " ");
            query = Regex.Replace(query, @"\s*,[\s,]*", ",").Trim().Trim(',').Trim();
            q.Comma = query.IndexOf(',') >= 0;
            if (q.Comma)
            {
                foreach (string part in query.Split(',').Select(part => part.Trim()).Where(part => part.Length > 0))
                {
                    string term = part;
                    string found = characterTerm(term); int? costume = CostumeNumber(term);
                    if (found != null) q.Characters.Add(found);
                    else if (costume.HasValue) { if (!q.Costumes.Contains(costume.Value)) q.Costumes.Add(costume.Value); }
                    else
                    {
                        // 1.0.6: a costume word inside a term ("juri c1", "barefoot costume 2") is that costume; what's
                        // left is the fighter when it names one, else the name words.
                        string rest = ExtractCostumes(term, q.Costumes);
                        if (rest.Length == 0) continue;
                        if (rest != term)
                        {
                            List<Word> split = Words(rest, characterTerm);
                            foreach (Word w in split.Where(w => w.Character != null)) q.Characters.Add(w.Character);
                            rest = String.Join(" ", split.Where(w => w.Character == null).Select(w => w.Text));
                            if (rest.Length == 0) continue;
                        }
                        term = rest;
                        if (q.Names.Contains(term, StringComparer.OrdinalIgnoreCase)) continue;
                        q.Names.Add(term);
                        string tool = ToolKeyword(term);
                        if (tool != null) q.NameTools[term] = tool;
                        string category = ModCategories.Keyword(term);
                        if (category != null) q.NameCategories[term] = category;
                        List<Word> words = Words(term, characterTerm);
                        if (words.Count > 1) q.NameWords[term] = words;
                    }
                }
            }
            else
            {
                int? costume = CostumeNumber(query);
                string character = query.Length > 0 ? characterTerm(query) : null;
                if (costume.HasValue) { if (!q.Costumes.Contains(costume.Value)) q.Costumes.Add(costume.Value); }
                else if (character != null && q.HasCostume) q.Characters.Add(character);   // "cammy costume:1"
                else
                {
                    // 1.0.6: "juri c1" / "juri barefoot costume 2" without a comma: the costume word is the costume and a
                    // fighter word its fighter, like "juri, c1".
                    string rest = ExtractCostumes(query, q.Costumes);
                    if (rest != query)
                    {
                        List<Word> split = Words(rest, characterTerm);
                        foreach (Word w in split.Where(w => w.Character != null)) q.Characters.Add(w.Character);
                        query = String.Join(" ", split.Where(w => w.Character == null).Select(w => w.Text));
                        character = query.Length > 0 ? characterTerm(query) : null;
                    }
                    q.Plain = query; q.PlainTool = ToolKeyword(query); q.PlainCategory = ModCategories.Keyword(query); q.PlainCharacter = character;
                    List<Word> words = Words(query, characterTerm);
                    if (words.Count > 1) q.PlainWords = words;
                }
            }
            return q;
        }

        // Costume words of a term ("c1", "o2", "costume 3", "outfit 4") go to `costumes`; returns the rest of the term.
        private static readonly Regex CostumeInText = new Regex(@"(?<![\p{L}\p{N}])(?:c|o|costume|outfit)\s*0*(\d{1,2})(?![\p{L}\p{N}])", RegexOptions.IgnoreCase);
        private static string ExtractCostumes(string term, List<int> costumes)
        {
            // Only when the term has other words too: "c1" alone is handled by CostumeNumber.
            if (term.IndexOf(' ') < 0) return term;
            var found = new List<int>();
            string rest = CostumeInText.Replace(term, delegate(Match m) { int n = Int32.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture); if (n > 0) found.Add(n); return " "; });
            if (found.Count == 0) return term;
            foreach (int n in found) if (!costumes.Contains(n)) costumes.Add(n);
            return Regex.Replace(rest, @"\s+", " ").Trim();
        }

        // Words of a plain query. Runs of up to three words that name a fighter ("chun li", "dee jay") are one word;
        // words without a letter or digit ("-", "+", "&") are dropped.
        internal static List<Word> Words(string text, Func<string, string> characterTerm)
        {
            var raw = (text ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Where(w => w.Any(Char.IsLetterOrDigit)).ToList();
            var words = new List<Word>();
            for (int i = 0; i < raw.Count; )
            {
                int taken = 1; string character = null;
                for (int span = Math.Min(3, raw.Count - i); span >= 2 && character == null; span--)
                {
                    string joined = String.Join(" ", raw.Skip(i).Take(span));
                    // Only a full name joins words ("chun li"); a start of one ("dee") is left to the single word.
                    string who = characterTerm(joined);
                    if (who != null && Compact(joined).Length >= Compact(who).Length) { character = who; taken = span; }
                }
                string word = String.Join(" ", raw.Skip(i).Take(taken));
                if (taken == 1 && PakKeyword(word) && i + 1 < raw.Count && Regex.IsMatch(raw[i + 1], @"^mods?$", RegexOptions.IgnoreCase))
                { taken = 2; word += " " + raw[i + 1]; }
                if (character == null) character = characterTerm(word);
                words.Add(new Word { Text = word, Character = character, Tool = ToolKeyword(word), Category = ModCategories.Keyword(word) });
                i += taken;
            }
            return words;
        }

        // ---- 1.0.6 name matching --------------------------------------------------------------------------------
        // Lower case letters and digits only, accents dropped: "C-Viper-Barefoot" -> "cviperbarefoot", "Élena" -> "elena".
        internal static string Compact(string text)
        {
            if (String.IsNullOrEmpty(text)) return "";
            string decomposed = text.Normalize(NormalizationForm.FormD);
            var b = new StringBuilder(decomposed.Length);
            foreach (char c in decomposed)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
                if (Char.IsLetterOrDigit(c)) b.Append(Char.ToLowerInvariant(c));
            }
            return b.ToString();
        }
        // The compact words of a term: "viper barefoot" -> viper, barefoot; "C-Viper" -> c, viper.
        internal static List<string> CompactWords(string term)
        {
            return Regex.Split(term ?? "", @"[^\p{L}\p{N}\p{Mn}]+").Select(Compact).Where(w => w.Length > 0).Distinct().ToList();
        }
        private static bool Has(string text, string term) { return !String.IsNullOrEmpty(text) && text.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0; }

        // Compact texts of an entry's names (file name, display name), cached on the entry until they change.
        private static string[] NameKeys(LibraryEntry e)
        {
            string file = FileName(e.SourcePath);
            LibraryEntry.SearchCache c = e.Search;
            if (c == null || !ReferenceEquals(c.Name, e.Name) || !ReferenceEquals(c.DisplayName, e.DisplayName) || !ReferenceEquals(c.CustomName, e.CustomName) || !ReferenceEquals(c.Source, e.SourcePath) || !ReferenceEquals(c.Character, e.Character))
            {
                c = new LibraryEntry.SearchCache { Name = e.Name, DisplayName = e.DisplayName, CustomName = e.CustomName, Source = e.SourcePath, Character = e.Character, Options = c == null ? null : c.Options, OptionTokens = c == null ? null : c.OptionTokens, OptionIds = c == null ? null : c.OptionIds, OptionNames = c == null ? null : c.OptionNames };
                c.Names = new[] { Compact(e.Name), Compact(e.DisplayName), Compact(e.CustomName), Compact(file) }.Where(s => s.Length > 0).Distinct().ToArray();
                c.NameTokens = new[] { e.Name, e.DisplayName, e.CustomName, file, e.Character }.SelectMany(TokensAndPairs).Distinct().ToArray();
                e.Search = c;
            }
            return c.Names;
        }
        // Words of a name, split at spaces, punctuation, lower→upper case and letter↔digit changes: "CViper-Barefoot2" ->
        // c, viper, barefoot, 2. For one- and two-letter words of a several-word search ("a manual" must not match every "a").
        private static readonly Regex TokenBreak = new Regex(@"[^\p{L}\p{N}\p{Mn}]+|(?<=\p{Ll})(?=\p{Lu})|(?<=\p{Lu})(?=\p{Lu}\p{Ll})|(?<=\p{L})(?=\p{N})|(?<=\p{N})(?=\p{L})");
        internal static IEnumerable<string> WordTokens(string text)
        {
            if (String.IsNullOrEmpty(text)) return new string[0];
            return TokenBreak.Split(text).Select(Compact).Where(t => t.Length > 0);
        }
        // The words, and each word joined to the next ("v", "2" -> also "v2"), so a two-character word can span a split.
        private static IEnumerable<string> TokensAndPairs(string text)
        {
            var words = WordTokens(text).ToList();
            for (int i = 0; i < words.Count; i++) { yield return words[i]; if (i + 1 < words.Count) yield return words[i] + words[i + 1]; }
        }
        // A one-letter word must be a whole word ("a manual" = A Manual archive, not D Manual archive); two characters
        // start a word ("v2", "ex").
        private static bool ShortWordIn(string[] tokens, string compact)
        {
            return compact.Length == 1 ? tokens.Any(t => t == compact) : tokens.Any(t => t.StartsWith(compact, StringComparison.Ordinal));
        }
        private static string[] NameTokens(LibraryEntry e) { NameKeys(e); return e.Search.NameTokens; }
        private static string[] OptionTokens(LibraryEntry e) { OptionKeys(e); return e.Search.OptionTokens; }
        // Compact texts of the option folders (every folder level: "Goth Juri/Barefoot" -> "gothjuri", "barefoot",
        // "gothjuribarefoot") and option names of a bundle, for deep search.
        private static string[] OptionKeys(LibraryEntry e)
        {
            NameKeys(e);
            LibraryEntry.SearchCache c = e.Search;
            if (c.Options == null || !ReferenceEquals(c.OptionIds, e.OptionIds) || !ReferenceEquals(c.OptionNames, e.OptionNames))
            {
                var keys = new List<string>();
                foreach (string id in e.OptionIds ?? new List<string>())
                {
                    foreach (string segment in id.Split('/')) keys.Add(Compact(segment));
                    keys.Add(Compact(id));
                }
                foreach (string name in e.OptionNames ?? new List<string>()) keys.Add(Compact(name));
                c.Options = keys.Where(k => k.Length > 0).Distinct().ToArray(); c.OptionIds = e.OptionIds; c.OptionNames = e.OptionNames;
                c.OptionTokens = (e.OptionIds ?? new List<string>()).SelectMany(id => id.Split('/')).Concat(e.OptionNames ?? new List<string>()).SelectMany(TokensAndPairs).Distinct().ToArray();
            }
            return c.Options;
        }
        private static string FileName(string path)
        {
            try { return String.IsNullOrEmpty(path) ? "" : Path.GetFileName(path.TrimEnd('\\', '/')); }
            catch (ArgumentException) { return ""; }
        }

        // The mod's file name or display name holds `term`: as typed, or word by word ignoring spaces, dashes and
        // underscores ("viper barefoot", "barefoot viper" and "cviper" all find C-Viper-Barefoot.zip).
        internal static bool NameMatches(LibraryEntry e, string term)
        {
            if (e == null || String.IsNullOrEmpty(term)) return e != null;
            if (Has(e.Name, term) || Has(e.DisplayName, term) || Has(e.CustomName, term) || Has(FileName(e.SourcePath), term)) return true;
            return WordsIn(NameKeys(e), NameTokens(e), term, false);
        }
        // Deep search: an option folder or option name holds `term` (all its words in the same option).
        internal static bool OptionMatches(LibraryEntry e, string term)
        {
            if (e == null || String.IsNullOrEmpty(term)) return false;
            return WordsIn(OptionKeys(e), OptionTokens(e), term, true);
        }
        // 1.0.8 MY FILTERS keyword (LibraryFilters): `compact` (Compact of the keyword) inside a name or an option folder/name,
        // as one run ("see through" = "See-Through" = "seethrough", never "see … through" or "Normal panties" for "no panties");
        // one or two characters must start a word, as in search.
        internal static bool HasCompact(LibraryEntry e, string compact)
        {
            if (e == null || String.IsNullOrEmpty(compact)) return false;
            if (compact.Length <= 2) return ShortWordIn(NameTokens(e), compact) || ShortWordIn(OptionTokens(e), compact);
            foreach (string k in NameKeys(e)) if (k.IndexOf(compact, StringComparison.Ordinal) >= 0) return true;
            foreach (string k in OptionKeys(e)) if (k.IndexOf(compact, StringComparison.Ordinal) >= 0) return true;
            return false;
        }
        // Every word of `term` is in one of `keys` (sameKey: all in the same one); the whole term without spaces also counts.
        // One- and two-character words go by `tokens` (ShortWordIn): inside a longer word they match almost anything.
        private static bool WordsIn(string[] keys, string[] tokens, string term, bool sameKey)
        {
            if (keys.Length == 0) return false;
            string whole = Compact(term);
            if (whole.Length == 0) return false;
            List<string> words = CompactWords(term);
            if (words.Count < 2) return whole.Length <= 2 ? ShortWordIn(tokens, whole) : keys.Any(k => k.IndexOf(whole, StringComparison.Ordinal) >= 0);
            if (whole.Length > 2 && keys.Any(k => k.IndexOf(whole, StringComparison.Ordinal) >= 0)) return true;
            List<string> shortWords = words.Where(w => w.Length <= 2).ToList(), longWords = words.Where(w => w.Length > 2).ToList();
            if (!shortWords.All(w => ShortWordIn(tokens, w))) return false;
            return sameKey ? longWords.Count == 0 || keys.Any(k => longWords.All(w => k.IndexOf(w, StringComparison.Ordinal) >= 0))
                           : longWords.All(w => keys.Any(k => k.IndexOf(w, StringComparison.Ordinal) >= 0));
        }

        // One word of a plain query: what a one-word search would match (fighter, tool or category word, author,
        // names, the detected fighter, esf code), plus option names with deep search.
        private static bool WordMatches(LibraryEntry e, Word w, bool deep, Func<LibraryEntry, string, bool> matchesCharacter, Func<LibraryEntry, string, bool> matchesName)
        {
            string compact = Compact(w.Text);
            // One or two letters/digits: the start of a word only ("a manual" = A Manual, not Barefoot Manual; "v2").
            if (compact.Length <= 2)
                return (w.Character != null && matchesCharacter(e, w.Character)) || ShortWordIn(NameTokens(e), compact) || (deep && ShortWordIn(OptionTokens(e), compact));
            return MatchesPak(e, w.Text) || (w.Character != null && matchesCharacter(e, w.Character)) || (w.Tool != null && MatchesTool(e, w.Tool)) || (w.Category != null && ModCategories.InCategory(e, w.Category))
                || MatchesAuthor(e, w.Text) || matchesName(e, w.Text) || Has(e.Character, w.Text) || (e.CharacterCodes ?? new List<string>()).Any(code => ("esf" + code).IndexOf(w.Text, StringComparison.OrdinalIgnoreCase) >= 0)
                || (deep && OptionMatches(e, w.Text));
        }

        internal static HashSet<string> CodesFor(IEnumerable<string> characters)
        {
            var names = new HashSet<string>(characters, StringComparer.OrdinalIgnoreCase);
            return new HashSet<string>(SlotsInstaller.Characters.ByCode.Where(kv => names.Contains(kv.Value)).Select(kv => kv.Key), StringComparer.OrdinalIgnoreCase);
        }

        private static bool TitleSaysCostume(LibraryEntry e, int costume)
        {
            var pattern = new Regex(@"(?<![A-Za-z0-9])(?:c|o|costume|outfit|cos)[\s_\-]*0*" + costume.ToString(CultureInfo.InvariantCulture) + @"(?![0-9])", RegexOptions.IgnoreCase);
            return pattern.IsMatch(e.Name ?? "") || pattern.IsMatch(e.DisplayName ?? "") || pattern.IsMatch(FileName(e.SourcePath));
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
            if (q.Costumes.Any(c => TitleSaysCostume(e, c)) && (anyCharacter || q.Characters.Any(name => matchesCharacter(e, name) || NameMatches(e, name)))) return TitleOnly;
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
                string tool, category; List<Word> words;
                // 1.0.6: a term of several words also matches word by word ("goth juri, barefoot"), and deep search
                // looks into the option folders and names.
                return characterOk && q.Names.All(term => MatchesPak(e, term) || matchesName(e, term) || MatchesAuthor(e, term) || (q.NameTools.TryGetValue(term, out tool) && MatchesTool(e, tool)) || (q.NameCategories.TryGetValue(term, out category) && ModCategories.InCategory(e, category))
                    || (q.Deep && OptionMatches(e, term)) || (q.NameWords.TryGetValue(term, out words) && words.All(w => WordMatches(e, w, q.Deep, matchesCharacter, matchesName))));
            }
            string query = q.Plain;
            if (MatchesPak(e, query)) return true;
            if (query.Length == 0 || (q.PlainCharacter != null && matchesCharacter(e, q.PlainCharacter)) || (q.PlainTool != null && MatchesTool(e, q.PlainTool)) || (q.PlainCategory != null && ModCategories.InCategory(e, q.PlainCategory)) || MatchesAuthor(e, query) || matchesName(e, query) || Has(e.SourcePath, query) || Has(e.Character, query) || (e.CharacterCodes ?? new List<string>()).Any(code => ("esf" + code).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)) return true;
            // 1.0.6: several words match one by one, in any order ("juri barefoot" = a Juri mod named …Barefoot…).
            if (q.Deep && OptionMatches(e, query)) return true;
            return q.PlainWords.Count > 1 && q.PlainWords.All(w => WordMatches(e, w, q.Deep, matchesCharacter, matchesName));
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
