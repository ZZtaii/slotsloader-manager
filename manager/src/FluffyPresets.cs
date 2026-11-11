using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace SlotsInstaller
{
    // Fluffy Mod Manager presets (Games\SF6\Presets\*.prt): "v2", then one section per
    // installed mod or option folder ([folder or archive name], ModID_v1, ShortModID_v1, ModName).
    internal sealed class FluffyPresetEntry
    {
        internal string Section = "", ModName = "", ModId = "";
        internal int Order;
        internal List<string> ExplicitIds = new List<string>();
        internal List<string> RequiredIds = new List<string>();
    }

    internal sealed class PresetMapping
    {
        internal List<FluffyPresetEntry> Sections = new List<FluffyPresetEntry>();
        internal LibraryEntry Entry;
        internal ModPackage Package;
        internal List<string> OptionIds = new List<string>();
        internal string Kind = "";        // Costume, Tool, Mixed
        internal string Mode = InstallModes.Replace;
        internal string Character = "";
        internal string Message = "", Stamp;
        internal bool Ambiguous;
        internal List<PresetMapping> Choices;   // Ambiguous: the matching archives (Entry, Package, OptionIds), one to pick
        internal bool Include = true;
        internal string DisplayName { get { return Sections.Count == 0 ? "" : Sections[0].ModName.Length > 0 ? Sections[0].ModName : Sections[0].Section; } }
    }

    internal static class FluffyPresets
    {
        internal static List<FluffyPresetEntry> Parse(string text)
        {
            var list = new List<FluffyPresetEntry>(); FluffyPresetEntry current = null; int order = 0;
            foreach (string raw in (text ?? "").Replace("\r", "").Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.Equals("v2", StringComparison.OrdinalIgnoreCase) || line.StartsWith(";")) continue;
                if (line.StartsWith("[") && line.EndsWith("]")) { current = new FluffyPresetEntry { Section = line.Substring(1, line.Length - 2), Order = order++ }; list.Add(current); continue; }
                if (current == null) continue;
                int eq = line.IndexOf('='); if (eq < 1) continue;
                string key = line.Substring(0, eq).Trim(), value = line.Substring(eq + 1).Trim();
                if (key.Equals("ModName", StringComparison.OrdinalIgnoreCase)) current.ModName = value;
                else if (key.Equals("ModID_v1", StringComparison.OrdinalIgnoreCase)) current.ModId = value;
            }
            return list;
        }

        internal static List<string> PresetFiles(IEnumerable<string> fluffyRoots)
        {
            var files = new List<string>();
            foreach (string root in fluffyRoots ?? new string[0])
            {
                string dir = Path.Combine(root, "Presets");
                try { if (Directory.Exists(dir)) files.AddRange(Directory.GetFiles(dir, "*.prt").OrderBy(f => f, StringComparer.OrdinalIgnoreCase)); } catch (Exception ex) { if (ex is OutOfMemoryException) throw; }
            }
            return files;
        }

        private static string Key(string s) { return new string((s ?? "").ToLowerInvariant().Where(Char.IsLetterOrDigit).ToArray()); }
        private static string LastSegment(string id) { int i = (id ?? "").LastIndexOf('/'); return i < 0 ? id ?? "" : id.Substring(i + 1); }

        // One mapping per preset section, in preset order. Package loads are cached.
        internal static List<PresetMapping> Map(List<FluffyPresetEntry> entries, IList<LibraryEntry> library, string sevenZip)
        {
            var stamps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var packages = new Dictionary<string, ModPackage>(StringComparer.OrdinalIgnoreCase);
            Func<LibraryEntry, ModPackage> load = delegate(LibraryEntry e)
            {
                ModPackage p;
                if (!packages.TryGetValue(e.SourcePath, out p)) { try { p = PackageReader.Load(e.SourcePath, sevenZip); } catch (Exception ex) { if (ex is OutOfMemoryException || ex is OperationCanceledException) throw; p = null; } packages[e.SourcePath] = p; }
                return p;
            };
            var usable = library.Where(e => e != null && e.Kind != "Library" && e.Detection != "Error" && !String.IsNullOrEmpty(e.SourcePath)).ToList();
            var result = new List<PresetMapping>();
            foreach (FluffyPresetEntry entry in entries)
            {
                OperationContext.ThrowIfCancellationRequested();
                var mapping = new PresetMapping(); mapping.Sections.Add(entry);
                string sectionKey = Key(entry.Section), nameKey = Key(entry.ModName);
                var candidates = new List<LibraryEntry>();
                Func<LibraryEntry, bool> archiveMatch = e => Key(e.Name) == sectionKey || Key(Path.GetFileName(e.SourcePath)) == sectionKey;
                candidates.AddRange(usable.Where(archiveMatch));
                candidates.AddRange(usable.Where(e => e.OptionIds.Any(id => Key(LastSegment(id)) == sectionKey)));
                candidates.AddRange(usable.Where(e => nameKey.Length > 0 && (Key(e.DisplayName) == nameKey || (e.OptionNames != null && e.OptionNames.Any(n => Key(n) == nameKey)))));
                // Option folders without native files (tool mods) are not indexed: the best-named others are read too,
                // at most 16 per entry, so a large library is never read whole.
                var words = Regex.Split(entry.Section + " " + entry.ModName, @"[^A-Za-z0-9]+").Where(w => w.Length >= 4).Select(Key).Where(w => !StopWords.Contains(w)).Distinct().ToList();
                var fallback = usable.Select(e => new { E = e, K = Key(e.Name) })
                    .Select(x => new { x.E, Score = words.Count(w => x.K.Contains(w)) + (sectionKey.Length >= 4 && x.K.Contains(sectionKey) ? 2 : 0) })
                    .Where(x => x.Score > 0).OrderByDescending(x => x.Score).Take(16).Select(x => x.E);
                int best = 0;
                var matches = new List<PresetMapping>();
                var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (LibraryEntry candidate in candidates.Concat(fallback))
                {
                    if (!visited.Add(candidate.SourcePath)) continue;
                    OperationContext.ThrowIfCancellationRequested();
                    ModPackage package = load(candidate);
                    if (package == null) continue;
                    bool archive = archiveMatch(candidate);
                    foreach (ModComponent c in package.Components.Where(c => !c.IsDummy && c.HasFiles(true)).Concat(new ModComponent[] { null }))
                    {
                        bool folder = c != null && c.Id.Length > 0 && Key(LastSegment(c.Id)) == sectionKey;
                        bool named = c != null && nameKey.Length > 0 && Key(c.Name) == nameKey;
                        // The section names the archive or folder. Display names refine that match.
                        int score = c == null && archive ? 4 : 0;
                        if (named || folder) score = (archive || folder ? 4 : 0) + (named ? 2 : 0) + (folder && archive ? 1 : 0);
                        if (score == 0 || score < best) continue;
                        if (score > best) { matches.Clear(); best = score; }
                        matches.Add(new PresetMapping { Entry = candidate, Package = package,
                            OptionIds = c == null ? Composer.DefaultSelection(package, true) : new List<string> { c.Id } });
                    }
                }
                if (matches.Count > 1) matches = DistinctCopies(matches);
                if (matches.Count == 1) Resolve(mapping, matches[0], stamps);
                else if (matches.Count > 1)
                {
                    // The review picks one (FluffyPresetForm); until then the entry is skipped.
                    mapping.Ambiguous = true; mapping.Choices = matches; mapping.Include = false;
                    // The paths are in the review's chooser (FluffyPresetForm.ChooseMatch); the column stays short.
                    mapping.Message = "'" + entry.Section + "' matches " + matches.Count + " archives. Double-click this row to choose one.";
                }
                else { mapping.Message = "'" + entry.Section + "' (" + entry.ModName + ") not found in your libraries."; mapping.Include = false; }
                result.Add(mapping);
            }
            return result;
        }
        private static readonly HashSet<string> StopWords = new HashSet<string>(new[] { "barefoot", "with", "mods", "main" });

        // A match the review chose, or the only one: its options become the entry's layer in preset order.
        internal static void Resolve(PresetMapping mapping, PresetMapping match, Dictionary<string, string> stamps = null)
        {
            FluffyPresetEntry entry = mapping.Sections[0];
            mapping.Entry = match.Entry; mapping.Package = match.Package;
            entry.ExplicitIds = match.OptionIds.ToList();
            entry.RequiredIds = Complete(mapping.Package, entry.ExplicitIds);
            mapping.OptionIds = entry.RequiredIds.ToList();
            if (stamps == null || !stamps.TryGetValue(mapping.Entry.SourcePath, out mapping.Stamp))
            {
                mapping.Stamp = MainForm.SourceStamp(mapping.Entry.SourcePath);
                if (stamps != null) stamps[mapping.Entry.SourcePath] = mapping.Stamp;
            }
            mapping.Ambiguous = false; mapping.Choices = null; mapping.Include = true;
            Describe(mapping);
        }
        internal static string ChoiceText(PresetMapping match)
        {
            var names = match.OptionIds.Select(id => match.Package.Find(id)).Where(c => c != null).Select(c => c.Name).ToList();
            return match.Entry.SourcePath + (names.Count == 0 ? "" : " (" + String.Join(" + ", names) + ")");
        }
        // The same download in two libraries is one match: the same option of byte-identical archives.
        private static List<PresetMapping> DistinctCopies(List<PresetMapping> matches)
        {
            var kept = new List<PresetMapping>();
            foreach (PresetMapping m in matches) if (!kept.Any(k => SameCopy(k, m))) kept.Add(m);
            return kept;
        }
        private static bool SameCopy(PresetMapping a, PresetMapping b)
        {
            if (!a.OptionIds.SequenceEqual(b.OptionIds, StringComparer.OrdinalIgnoreCase)) return false;
            try
            {
                var x = new FileInfo(a.Entry.SourcePath); var y = new FileInfo(b.Entry.SourcePath);
                if (!x.Exists || !y.Exists || x.Length != y.Length) return false;   // folders are never collapsed
                return ReplaceEngine.Sha(x.FullName) == ReplaceEngine.Sha(y.FullName);
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        // Adds the real ancestors and in-scope main files an option needs.
        internal static List<string> Complete(ModPackage package, List<string> ids)
        {
            var result = new List<string>();
            foreach (string id in ids)
            {
                var chain = new List<string>(); ModComponent c = package.Find(id); var seen = new HashSet<string>();
                while (c != null && seen.Add(c.Id)) { if (!c.IsDummy && c.HasFiles(true)) chain.Insert(0, c.Id); c = c.ParentId == null ? null : package.Find(c.ParentId); }
                foreach (string x in chain) if (!result.Contains(x, StringComparer.OrdinalIgnoreCase)) result.Add(x);
            }
            CompositionPlan plan = Composer.Plan(package, result, true);
            foreach (ModComponent main in plan.MissingMains)
                if (!result.Contains(main.Id, StringComparer.OrdinalIgnoreCase)) result.Insert(0, main.Id);
            return result;
        }

        private static void Describe(PresetMapping mapping)
        {
            ModPackage p = mapping.Package;
            var layers = mapping.OptionIds.Select(id => p.Find(id)).Where(c => c != null).ToList();
            bool natives = layers.Any(c => c.Assets.Count > 0), root = layers.Any(c => c.RootAssets.Count > 0);
            mapping.Kind = natives && root ? "Mixed" : root ? "Tool" : "Costume";
            mapping.Character = p.Character ?? "";
            mapping.Mode = InstallModes.Replace;
            CompositionPlan plan = Composer.Plan(p, mapping.OptionIds, true);
            mapping.Message = (plan.CanInstall ? "" : String.Join(" ", plan.Errors) + " ") + Path.GetFileName(mapping.Entry.SourcePath) + " · " + String.Join(" + ", layers.Select(c => c.Name));
        }

        // Group archive rows for review and extra outfits. Sections retain the replacement execution order.
        internal static List<PresetMapping> Group(List<PresetMapping> mappings)
        {
            var grouped = new List<PresetMapping>();
            foreach (PresetMapping m in mappings)
            {
                PresetMapping same = m.Entry == null ? null : grouped.FirstOrDefault(g => g.Entry != null && String.Equals(g.Entry.SourcePath, m.Entry.SourcePath, StringComparison.OrdinalIgnoreCase));
                if (same == null) { grouped.Add(m); continue; }
                same.Sections.AddRange(m.Sections);
                foreach (string id in m.OptionIds) if (!same.OptionIds.Contains(id, StringComparer.OrdinalIgnoreCase)) same.OptionIds.Add(id);
                Describe(same);
            }
            return grouped;
        }
    }
}
