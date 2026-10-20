using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace SlotsInstaller
{
    // 1.0.8 (the user): filters made of keywords, shown as chips under MY FILTERS in the Filters panel. A filter
    // matches a mod whose name (file name, modinfo.ini name or typed name) or any option folder or option name holds
    // one of its keywords (LibrarySearch.HasCompact: case, spaces and dashes don't matter; one- and two-letter keywords
    // must start a word). Each chip is Off, Only (show only matches, chips OR-ed) or Hide (leave matches out). Kept in
    // Data\library-filters.json; without the file the default NSFW filter is listed, so a user who never edits it gets
    // later default keywords too.
    public sealed class CustomFilter
    {
        public string Id = "", Name = "";
        public List<string> Keywords = new List<string>();
        internal CustomFilter Copy() { return new CustomFilter { Id = Id, Name = Name, Keywords = new List<string>(Keywords ?? new List<string>()) }; }
    }

    internal enum CustomFilterState { Off, Only, Hide }

    internal static class LibraryFilters
    {
        internal const string Format = "slotsloader-library-filters-v1", NsfwId = "nsfw";
        internal const int MaxFilters = 24, MaxKeywords = 200, MaxKeywordLength = 60, MaxNameLength = 32;
        // Picked from the user's 2,697-mod library (2026-10-20) and trimmed by the user: words that name nudity in mod and
        // option names. Clothing words (bikini, lingerie, thong, skimpy) are left out; "sex" ("passexport"), "tless"
        // ("hatless"), "transparent" (glasses) and "r18" (version numbers) matched harmless mods there.
        internal static readonly string[] NsfwKeywords =
        {
            "nsfw", "nude", "naked", "topless", "bottomless", "pubic", "pubes", "nipple", "pasties", "lewd", "futa",
            "see through", "see thru", "no panties", "no underwear", "erotic"
        };
        internal static CustomFilter DefaultNsfw() { return new CustomFilter { Id = NsfwId, Name = "NSFW", Keywords = NsfwKeywords.ToList() }; }

        public sealed class Store { public string Format = LibraryFilters.Format; public List<CustomFilter> Filters = new List<CustomFilter>(); }
        private static readonly object Gate = new object();
        private static List<CustomFilter> filters;
        private static string loadedFrom;
        internal static string FilePath { get { return Path.Combine(AppConfig.DataDir, "library-filters.json"); } }

        // The filters in their saved order (copies: edit them through Save).
        internal static List<CustomFilter> All()
        {
            lock (Gate) return Loaded().Select(f => f.Copy()).ToList();
        }
        private static List<CustomFilter> Loaded()
        {
            string file = FilePath;
            if (filters != null && String.Equals(loadedFrom, file, StringComparison.OrdinalIgnoreCase)) return filters;
            loadedFrom = file;
            filters = null;
            try
            {
                if (File.Exists(file))
                {
                    Store store = Json.Read<Store>(file);
                    if (store != null && store.Format == Format && store.Filters != null) filters = Clean(store.Filters);
                }
            }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); }
            if (filters == null) filters = new List<CustomFilter> { DefaultNsfw() };
            return filters;
        }
        // Valid filters only: a name, at least one keyword, unique ids and names, within the limits.
        private static List<CustomFilter> Clean(IEnumerable<CustomFilter> list)
        {
            var result = new List<CustomFilter>();
            foreach (CustomFilter f in list)
            {
                if (f == null || result.Count >= MaxFilters) continue;
                string name = CleanName(f.Name);
                List<string> keywords = ParseKeywords(String.Join(",", f.Keywords ?? new List<string>()));
                if (name.Length == 0 || keywords.Count == 0) continue;
                if (result.Any(r => String.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase))) continue;
                string id = String.IsNullOrWhiteSpace(f.Id) || result.Any(r => r.Id == f.Id) ? NewId(result) : f.Id.Trim();
                result.Add(new CustomFilter { Id = id, Name = name, Keywords = keywords });
            }
            return result;
        }
        // Replaces every filter (the panel's create, edit and delete all go through here).
        internal static void Save(IList<CustomFilter> list)
        {
            lock (Gate)
            {
                List<CustomFilter> clean = Clean(list ?? new List<CustomFilter>());
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                Json.Write(FilePath, new Store { Filters = clean });
                filters = clean; loadedFrom = FilePath;
            }
        }
        // Reads the file again next time (tests; another copy of the app saved it).
        internal static void Reload() { lock (Gate) { filters = null; loadedFrom = null; } }
        internal static string NewId(IEnumerable<CustomFilter> existing)
        {
            var taken = new HashSet<string>((existing ?? new CustomFilter[0]).Select(f => f.Id), StringComparer.Ordinal);
            string id;
            do id = "f" + Guid.NewGuid().ToString("N").Substring(0, 8); while (taken.Contains(id));
            return id;
        }

        internal static string CleanName(string name)
        {
            string clean = Regex.Replace(name ?? "", @"\s+", " ").Trim();
            return clean.Length > MaxNameLength ? clean.Substring(0, MaxNameLength).Trim() : clean;
        }
        // Keywords from typed text: commas, semicolons or new lines between them; spaces inside one stay ("see through").
        // Words without a letter or digit are dropped, duplicates (ignoring case and spacing) kept once.
        internal static List<string> ParseKeywords(string text)
        {
            var result = new List<string>(); var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string part in Regex.Split(text ?? "", @"[,;\r\n，、]+"))
            {
                string keyword = Regex.Replace(part, @"\s+", " ").Trim().Trim('"', '\'').Trim();
                if (keyword.Length > MaxKeywordLength) keyword = keyword.Substring(0, MaxKeywordLength).Trim();
                string key = LibrarySearch.Compact(keyword);
                if (key.Length == 0 || !seen.Add(key)) continue;
                result.Add(keyword);
                if (result.Count >= MaxKeywords) break;
            }
            return result;
        }
        internal static string KeywordText(IEnumerable<string> keywords) { return String.Join(", ", keywords ?? new string[0]); }

        // ---- Matching ----------------------------------------------------------------------------------------------
        internal static bool Matches(LibraryEntry e, IList<string> keywords)
        {
            if (e == null || keywords == null) return false;
            foreach (string k in keywords) if (LibrarySearch.HasCompact(e, LibrarySearch.Compact(k))) return true;
            return false;
        }
        // Cached per entry until its names, option names or the filter's keywords change (the list re-filters on every
        // search keystroke; a cold pass over 2,700 mods with the NSFW keywords takes a few hundred ms).
        private sealed class Hits
        {
            internal string Name, DisplayName, CustomName, Source; internal List<string> OptionIds, OptionNames;
            internal readonly Dictionary<string, bool> ByFilter = new Dictionary<string, bool>(StringComparer.Ordinal);
        }
        private static readonly ConditionalWeakTable<LibraryEntry, Hits> Cache = new ConditionalWeakTable<LibraryEntry, Hits>();
        internal static string MatchKey(CustomFilter f) { return String.Join("\n", f.Keywords ?? new List<string>()); }
        internal static bool Matches(LibraryEntry e, CustomFilter f) { return Matches(e, f, MatchKey(f)); }
        internal static bool Matches(LibraryEntry e, CustomFilter f, string key)
        {
            if (e == null || f == null) return false;
            Hits h = Cache.GetValue(e, delegate { return new Hits(); });
            if (!ReferenceEquals(h.Name, e.Name) || !ReferenceEquals(h.DisplayName, e.DisplayName) || !ReferenceEquals(h.CustomName, e.CustomName) || !ReferenceEquals(h.Source, e.SourcePath) || !ReferenceEquals(h.OptionIds, e.OptionIds) || !ReferenceEquals(h.OptionNames, e.OptionNames))
            {
                h.ByFilter.Clear(); h.Name = e.Name; h.DisplayName = e.DisplayName; h.CustomName = e.CustomName; h.Source = e.SourcePath; h.OptionIds = e.OptionIds; h.OptionNames = e.OptionNames;
            }
            bool hit;
            if (!h.ByFilter.TryGetValue(key, out hit)) { hit = Matches(e, f.Keywords); if (h.ByFilter.Count > 32) h.ByFilter.Clear(); h.ByFilter[key] = hit; }
            return hit;
        }
    }
}
