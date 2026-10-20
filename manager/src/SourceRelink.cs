using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace SlotsInstaller
{
    // 1.0.10 (the user: a replacement whose zip was renamed outside the Manager was copied into a list preset): a record
    // whose recorded library source is gone (a replacement or tool mod, or a managed extra outfit) is pointed at the one
    // listed library mod that holds the files it installed. It runs when a list preset is saved, before each item is
    // listed or copied (OutfitSetStore.CreatePreset), on the library as already listed (no scan).
    //   Candidates: a compact name (letters and digits) equal to the record's name or its old file name, or a modinfo
    //   name equal to one of them; a version after the name counts as the same ("X - v1.0" = "X"). Each is read like an install: its listing, the record's options composed (paths as
    //   ReplacePlanner.Prepare names them, an old version suffix it renamed included), and every file of the record
    //   must be there with its size, then its SHA-256. A file the Manager changed (ModFile.Fixes) only has to be
    //   there; at least one unchanged file must be compared. A patch PAK matches one of the mod's PAKs by its bytes.
    //   Exactly one match: SourcePath is updated (the replace state or the outfit's manifest, as a library rename
    //   writes it; no game file changes) with a Rename history event. None or several: left as is (the preset copies it).
    internal static class SourceRelink
    {
        private sealed class Wanted
        {
            internal string Name = "", OldSource = "";
            internal List<string> SelectedIds = new List<string>();
            internal bool Replace;
            internal readonly List<WantedFile> Files = new List<WantedFile>();
        }
        private sealed class WantedFile { internal string Path = "", Sha256 = ""; internal long Size; internal bool Fixed, Pak; }

        // Replacements and tool mods (not Slotsloader, not test installs) whose source is gone. Returns how many were relinked.
        internal static int Replacements(ReplaceEngine engine, List<LibraryMod> library, string sevenZip)
        {
            if (engine == null || library == null || library.Count == 0 || !File.Exists(engine.StatePath)) return 0;
            var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // record id -> new source
            foreach (ReplaceMod m in engine.LoadState().Mods)
            {
                if (m == null || m.Test || LoaderCostumePak.IsLoaderMod(m) || String.IsNullOrWhiteSpace(m.SourcePath) || Exists(m.SourcePath) || m.Files == null || m.Files.Count == 0) continue;
                var want = new Wanted { Name = m.Name ?? "", OldSource = m.SourcePath, SelectedIds = (m.SelectedIds ?? new List<string>()).ToList(), Replace = true };
                foreach (ModFile f in m.Files) want.Files.Add(new WantedFile { Path = Key(f.Path, true), Sha256 = f.Sha256 ?? "", Size = f.Size, Fixed = !String.IsNullOrEmpty(f.Fixes), Pak = ReplaceEngine.IsPatchPakPath(f.Path) });
                string to = Find(want, library, sevenZip);
                if (to != null) found[m.Id] = to;
            }
            if (found.Count == 0) return 0;
            var events = new List<HistoryEvent>();
            using (engine.Lock())
            {
                if (engine.JournalPending) return 0;   // an unfinished change goes first (next start)
                ReplaceState state = engine.LoadState();
                foreach (ReplaceMod m in state.Mods)
                {
                    string to;
                    if (m == null || !found.TryGetValue(m.Id, out to) || Exists(m.SourcePath)) continue;
                    events.Add(Renamed(m.SourcePath, to));
                    m.SourcePath = to;
                }
                if (events.Count > 0) DurableFile.WriteJson(engine.StatePath, state);
            }
            if (events.Count > 0) History.Record(null, events);   // never throws
            return events.Count;
        }

        // Managed extra outfits (enabled or disabled) whose source is gone. One with parts from other mods is left out (its
        // manifest hashes mix the mods), and one whose colour swatches the Manager rebuilt never matches (the manifest
        // keeps no fix marks). The caller holds the costume root's mutation lock. Returns how many were relinked.
        internal static int Outfits(string costumeRoot, List<LibraryMod> library, string sevenZip)
        {
            if (library == null || library.Count == 0) return 0;
            var events = new List<HistoryEvent>();
            foreach (Installer.ManagedManifestSnapshot s in Installer.ManagedManifests(costumeRoot).ToList())
            {
                InstallManifest m = s.Manifest;
                if (m == null || String.IsNullOrWhiteSpace(m.SourcePath) || Exists(m.SourcePath) || (m.Parts != null && m.Parts.Count > 0) || m.Sha256 == null || m.Sha256.Count == 0) continue;
                var want = new Wanted { Name = m.Name ?? "", OldSource = m.SourcePath, SelectedIds = (m.SelectedIds ?? new List<string>()).ToList() };   // outfits are named after their mod by default
                string folder = Path.GetDirectoryName(s.File);
                foreach (var pair in m.Sha256)
                {
                    long size = -1;
                    try { var info = new FileInfo(Safety.Under(folder, pair.Key)); if (info.Exists) size = info.Length; }
                    catch (Exception ex) { if (ex is OutOfMemoryException) throw; }
                    if (size < 0) { want = null; break; }   // a file left the outfit: its record no longer describes it
                    want.Files.Add(new WantedFile { Path = Key(pair.Key, false), Sha256 = pair.Value ?? "", Size = size });
                }
                string to = want == null ? null : Find(want, library, sevenZip);
                if (to == null) continue;
                events.Add(Renamed(m.SourcePath, to));
                m.SourcePath = to;
                DurableFile.WriteJson(s.File, m);
            }
            if (events.Count > 0) History.Record(null, events);
            return events.Count;
        }

        private static HistoryEvent Renamed(string from, string to)
        {
            string name = Directory.Exists(to) ? Path.GetFileName(to.TrimEnd('\\')) : Path.GetFileNameWithoutExtension(to);
            return new HistoryEvent { Kind = HistoryKinds.Rename, Source = History.Normalize(to), PrevId = History.Normalize(from), Name = name, Detail = "from " + Path.GetFileName((from ?? "").TrimEnd('\\')) + ", found by its files" };
        }

        // The one listed mod that holds the record's files; null when none or several do.
        private static string Find(Wanted want, List<LibraryMod> library, string sevenZip)
        {
            string match = null;
            foreach (LibraryMod c in Candidates(want, library))
            {
                OperationContext.Report("Looking for " + Path.GetFileName(want.OldSource.TrimEnd('\\')) + " in your libraries", 0, 0);
                bool holds;
                try { holds = Holds(c.Path, want, sevenZip); }
                catch (Exception ex) { if (Fatal(ex)) throw; Program.Log(ex); holds = false; }
                if (!holds) continue;
                if (match != null) return null;   // several: no guess
                match = Path.GetFullPath(c.Path);
            }
            return match;
        }

        // Listed mods named like the record (compact names, no disk access but an existence check).
        private static List<LibraryMod> Candidates(Wanted want, List<LibraryMod> library)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            Action<string> add = delegate(string n) { string c = Compact(n); if (c.Length >= 3) names.Add(c); };
            add(want.Name);
            int options = want.Name.IndexOf(" · ", StringComparison.Ordinal); if (options > 0) add(want.Name.Substring(0, options));   // "Mod · Option + Option" (Assistant installs)
            add(Stem(want.OldSource));
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<LibraryMod>();
            foreach (LibraryMod c in library)
            {
                if (c == null || String.IsNullOrWhiteSpace(c.Path)) continue;
                string stem = Compact(Stem(c.Path)), mod = Compact(c.Name);
                if (!names.Any(n => Close(n, stem) || Close(n, mod))) continue;
                string full;
                try { full = Path.GetFullPath(c.Path).TrimEnd('\\'); } catch (Exception ex) { if (ex is OutOfMemoryException) throw; continue; }
                if (seen.Add(full) && Exists(full)) result.Add(c);
            }
            return result;
        }

        // The candidate composes, with the record's options, every file of the record (see the class note).
        private static bool Holds(string source, Wanted want, string sevenZip)
        {
            using (var reader = new SourceReader(source, sevenZip))
            {
                ModPackage package = PackageReader.Load(source, reader, false);
                List<string> ids;
                if (want.SelectedIds.Count == 0) ids = Composer.DefaultSelection(package, want.Replace);
                else
                {
                    List<string> dropped;
                    ids = MainForm.MapSelection(package, want.SelectedIds.Select(id => new KeyValuePair<string, string>(id, "")).ToList(), out dropped);
                    if (dropped.Count > 0) return false;
                }
                CompositionPlan plan = Composer.Plan(package, ids, want.Replace);
                if (!plan.CanInstall) return false;
                var byPath = new Dictionary<string, PlannedFile>(StringComparer.OrdinalIgnoreCase);
                var byStem = new Dictionary<string, List<PlannedFile>>(StringComparer.OrdinalIgnoreCase);
                var paks = new List<PlannedFile>();
                foreach (PlannedFile f in plan.Files.Values)
                {
                    if (want.Replace && ReplaceEngine.IsModPak(f.Asset.DestinationPath)) { paks.Add(f); continue; }
                    string key = Key(f.Asset.DestinationPath, want.Replace), stem = VersionStem(key);
                    byPath[key] = f;
                    if (stem == null) continue;
                    List<PlannedFile> same; if (!byStem.TryGetValue(stem, out same)) byStem[stem] = same = new List<PlannedFile>();
                    same.Add(f);
                }
                var compare = new List<KeyValuePair<WantedFile, PlannedFile>>(); var wantedPaks = new List<WantedFile>();
                foreach (WantedFile w in want.Files)
                {
                    if (w.Pak) { wantedPaks.Add(w); continue; }
                    PlannedFile f;
                    if (!byPath.TryGetValue(w.Path, out f))
                    {
                        // Renamed at install from an older version suffix (.tex.143230113 -> the current one): the one file of that stem.
                        List<PlannedFile> same; string stem = VersionStem(w.Path);
                        if (stem == null || !byStem.TryGetValue(stem, out same) || same.Count != 1) return false;
                        f = same[0];
                    }
                    if (w.Fixed) continue;   // changed by the Manager: present is enough
                    if (f.Asset.Size != w.Size) return false;
                    compare.Add(new KeyValuePair<WantedFile, PlannedFile>(w, f));
                }
                if (compare.Count + wantedPaks.Count == 0) return false;   // nothing unchanged to compare
                List<PlannedFile> sizedPaks = paks.Where(p => wantedPaks.Any(w => w.Size == p.Asset.Size)).ToList();
                if (sizedPaks.Count < wantedPaks.Count) return false;
                Dictionary<string, string> hashes = reader.VerifyFiles(compare.Select(p => p.Value).Concat(sizedPaks).Distinct().ToList());
                foreach (var pair in compare) if (!SameHash(hashes, pair.Value, pair.Key.Sha256)) return false;
                foreach (WantedFile w in wantedPaks)
                {
                    PlannedFile hit = sizedPaks.FirstOrDefault(p => p.Asset.Size == w.Size && SameHash(hashes, p, w.Sha256));
                    if (hit == null) return false;
                    sizedPaks.Remove(hit);
                }
                return true;
            }
        }

        // A record path as ReplacePlanner.Prepare writes it (natives lower case), or a manifest key as is.
        private static string Key(string path, bool replace)
        {
            string p = PakHash.Normalize(path).TrimStart('/');
            return replace && p.StartsWith("natives/", StringComparison.OrdinalIgnoreCase) ? ModFixes.NormalizeNativePath(p) : p;
        }
        // "…/x.tex.241101895" -> "…/x.tex": a path without its numeric version suffix; null when it has none.
        private static string VersionStem(string path)
        {
            Match m = Regex.Match(path ?? "", @"^(.+\.[a-z0-9_]+)\.\d+$", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value : null;
        }
        private static bool SameHash(Dictionary<string, string> hashes, PlannedFile f, string sha)
        {
            string h;
            return hashes.TryGetValue(f.Asset.DestinationPath, out h) && !String.IsNullOrEmpty(sha) && String.Equals(h, sha, StringComparison.OrdinalIgnoreCase);
        }
        // The same compact name, or one with a version after it ("X - v1.0" and "X"; the shorter has 6 letters or more).
        private static bool Close(string a, string b)
        {
            if (String.IsNullOrEmpty(a) || String.IsNullOrEmpty(b)) return false;
            if (a == b) return true;
            string shorter = a.Length < b.Length ? a : b, longer = a.Length < b.Length ? b : a;
            return shorter.Length >= 6 && longer.StartsWith(shorter, StringComparison.Ordinal) && Regex.IsMatch(longer.Substring(shorter.Length), @"^v?\d");
        }
        // Letters and digits, lower case: "Sakura Voice over Ryu - English" = "sakura_voice_over_ryu_english".
        private static string Compact(string name)
        {
            var sb = new StringBuilder();
            foreach (char ch in name ?? "") if (Char.IsLetterOrDigit(ch)) sb.Append(Char.ToLowerInvariant(ch));
            return sb.ToString();
        }
        // A mod's file name without the archive extension, or its folder name.
        private static string Stem(string path)
        {
            try
            {
                string name = Path.GetFileName((path ?? "").TrimEnd('\\', '/'));
                string extension = Path.GetExtension(name).ToLowerInvariant();
                return extension == ".zip" || extension == ".rar" || extension == ".7z" ? Path.GetFileNameWithoutExtension(name) : name;
            }
            catch (ArgumentException) { return ""; }
        }
        private static bool Exists(string path)
        {
            try { return !String.IsNullOrWhiteSpace(path) && Path.IsPathRooted(path) && (File.Exists(path) || Directory.Exists(path)); }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; return false; }
        }
        private static bool Fatal(Exception ex) { return ex is OutOfMemoryException || ex is OperationCanceledException || ex is System.Threading.ThreadAbortException; }
    }
}
