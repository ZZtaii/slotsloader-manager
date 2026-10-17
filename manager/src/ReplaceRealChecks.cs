using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace SlotsInstaller
{
    // Read-only checks against the user's real game, libraries and Fluffy presets.
    // Nothing is written outside the given output folder and the system temp folder.
    internal static class ReplaceRealChecks
    {
        // Outputs of SlotsLoader's C++ fixer (built from the loader source) for real outdated files.
        // reference: SHA-256 of the vanilla esf001_001_cmd_000.user.2 these outputs were made with.
        private const string ReferenceSha = "E943E7611A265A560F0FAABD528BF5D18851B9CA41841C952648E5DF834DBE58";
        // 2.4.3: the archives, the game and the libraries come from the local test config (tests.local.json), never
        // from the source: [kind, archive, entry, SHA-256 of the entry, SHA-256 after the fix].
        private static string[][] Golden { get { return LocalTestConfig.Current.ReplaceCases.Select(c => c.ToArray()).ToArray(); } }
        private static string RealGame { get { return LocalTestConfig.Current.GameRoot; } }
        private static string SmallReplacement { get { return LocalTestConfig.Current.Archive("smallReplacement"); } }
        private static string[] Libraries { get { return LocalTestConfig.Current.Libraries.ToArray(); } }
        private static string[] FluffyRoots { get { return LocalTestConfig.Current.FluffyRoots.ToArray(); } }

        internal static int Run(string output)
        {
            string root = Path.GetFullPath(output); Directory.CreateDirectory(root);
            var report = new List<string>(); int failures = 0, skips = 0;
            Action<string, Func<string>> check = delegate(string name, Func<string> action)
            {
                try { string detail = action(); report.Add("PASS " + name + (String.IsNullOrEmpty(detail) ? "" : ": " + detail)); }
                catch (SkipException ex) { skips++; report.Add("SKIP " + name + ": " + ex.Message); }
                catch (Exception ex) { failures++; report.Add("FAIL " + name + ": " + ex); }
                Console.WriteLine(report[report.Count - 1]);
            };
            string pakPath = Path.Combine(RealGame, "re_chunk_000.pak");
            string beforeToc = null;
            check("real base PAK parses read-only; known vanilla paths resolve; reference colour file decodes", delegate
            {
                if (!File.Exists(pakPath)) throw new SkipException("game not found");
                PakIndex p = PakIndex.Open(pakPath);
                beforeToc = p.TocSha256();
                var cmd = p.Find(PakHash.PathHash(ModFixes.ReferenceCmdPath));
                ReplaceChecks.Require(cmd.Count == 1, "reference colour file not found");
                byte[] data = p.Read(p.Entry(cmd[0]));
                ReplaceChecks.Require(data.Length > 1000 && data[0] == 'U', "reference did not decode");
                return "KPKA " + p.Major + "." + p.Minor + ", features " + p.Features + ", " + p.Count + " entries, fingerprint 0x" + p.Fingerprint.ToString("X8") + ", zeroed " + p.CountZeroed() + ", TOC SHA-256 " + beforeToc.Substring(0, 16) + ", table-only SHA-256 " + TableSha(p).Substring(0, 16) + ", reference entry #" + cmd[0] + " (" + data.Length + " bytes)";
            });
            byte[] referenceCmd = null;
            check("C# fixers match Slotsloader's C++ output byte for byte on real outdated files", delegate
            {
                if (!File.Exists(pakPath)) throw new SkipException("game not found");
                referenceCmd = PakIndex.Open(pakPath).ReadByPath(ModFixes.ReferenceCmdPath);
                bool sameReference = ReplaceEngine.Sha(referenceCmd).ToUpperInvariant() == ReferenceSha;
                GameReference reference = GameReference.FromCmd(referenceCmd);
                int compared = 0;
                foreach (string[] g in Golden)
                {
                    if (!File.Exists(g[1])) continue;
                    byte[] data;
                    using (var zip = ZipFile.OpenRead(g[1])) { var e = zip.GetEntry(g[2]); if (e == null) continue; using (var s = e.Open()) using (var m = new MemoryStream()) { s.CopyTo(m); data = m.ToArray(); } }
                    if (ReplaceEngine.Sha(data).ToUpperInvariant() != g[3]) continue;
                    var fixes = ModFixes.UpgradeModFile(ref data, reference);
                    string kind = fixes.Count == 0 ? "" : fixes[0].Kind;
                    ReplaceChecks.Require(kind == g[0], "fix kind for " + Path.GetFileName(g[2]) + ": " + kind);
                    if (sameReference) ReplaceChecks.Require(ReplaceEngine.Sha(data).ToUpperInvariant() == g[4], "output differs from C++ for " + Path.GetFileName(g[2]));
                    compared++;
                }
                if (compared == 0) throw new SkipException("sample archives not available");
                return compared + " samples (CRC x3, 2023 layout, mip x2, unrepairable mip)" + (sameReference ? ", byte-identical to C++" : ", kinds only (game reference changed since the C++ outputs were recorded)");
            });
            check("replace-plan dry run against the real game (read-only): rows to invalidate, fixes, no game write", delegate
            {
                if (!File.Exists(pakPath) || !File.Exists(SmallReplacement)) throw new SkipException("game or sample mod not found");
                bool nativesBefore = Directory.Exists(Path.Combine(RealGame, "natives"));
                string data = Path.Combine(root, "dry-run-data"); Directory.CreateDirectory(data);
                var engine = new ReplaceEngine(RealGame, data, new ReplaceSettings { Method = ReplaceMethods.Pak, StoreLocation = Path.Combine(data, "store") });
                engine.FluffyProvider = delegate { return FluffyInstalls.Load(FluffyRoots); };
                ModPackage package = PackageReader.Load(SmallReplacement, @"C:\Program Files\7-Zip\7z.exe");
                ReplacePlan plan = ReplacePlanner.Check(Composer.Plan(package, Composer.DefaultSelection(package, true), true), @"C:\Program Files\7-Zip\7z.exe", engine);
                File.WriteAllText(Path.Combine(root, "dry-run-replace-plan.txt"), ReplacePlanner.Report(plan) + Environment.NewLine + Environment.NewLine + String.Join(Environment.NewLine, plan.InvalidationPreview));
                ReplaceChecks.Require(PakIndex.Open(pakPath).TocSha256() == beforeToc, "real PAK table changed");
                ReplaceChecks.Require(Directory.Exists(Path.Combine(RealGame, "natives")) == nativesBefore, "natives folder changed");
                ReplaceChecks.Require(!File.Exists(engine.StatePath) && !File.Exists(engine.JournalPath), "dry run wrote engine state");
                return plan.ModName + ": " + plan.Files.Count + " files, " + plan.VanillaReplaced + " vanilla, " + plan.RowsToInvalidate + " rows, " + plan.Fixed + " auto-fixed; affects " + plan.AffectsText + "; can install: " + plan.CanInstall;
            });
            check("automatic health check timing (2.0.1, read-only): Juri bundle and the largest RAR, cold and re-check with the extraction cache; no game write", delegate
            {
                if (!File.Exists(pakPath)) throw new SkipException("game not found");
                string sevenZip = LocalTestConfig.Current.SevenZip;
                var sources = new List<string>();
                string juri = LocalTestConfig.Current.Archive("juri");
                if (File.Exists(juri)) sources.Add(juri);
                string rar = Libraries.Where(Directory.Exists).SelectMany(l => Directory.GetFiles(l, "*.rar")).OrderByDescending(f => new FileInfo(f).Length).FirstOrDefault();
                if (rar != null && File.Exists(sevenZip)) sources.Add(rar);
                if (sources.Count == 0) throw new SkipException("no sample archives");
                bool nativesBefore = Directory.Exists(Path.Combine(RealGame, "natives"));
                string data = Path.Combine(root, "timing-data"); Directory.CreateDirectory(data);
                var engine = new ReplaceEngine(RealGame, data, new ReplaceSettings { Method = ReplaceMethods.Pak, StoreLocation = Path.Combine(data, "store") });
                engine.FluffyProvider = delegate { return FluffyInstalls.Load(FluffyRoots); };
                var lines = new List<string>();
                foreach (string source in sources)
                {
                    var load = System.Diagnostics.Stopwatch.StartNew();
                    ModPackage package = PackageReader.Load(source, sevenZip, true);
                    load.Stop();
                    List<string> selection = Composer.DefaultSelection(package, true);
                    for (int round = 0; round < 10; round++)   // resolve choose-one groups with their first option, as a user would
                    {
                        var open = Composer.UnresolvedChoiceGroups(package, selection, true);
                        if (open.Count == 0) break;
                        foreach (ModComponent g in open) { ModComponent child = package.Components.FirstOrDefault(c => c.ParentId == g.Id && c.HasFiles(true)) ?? package.Components.FirstOrDefault(c => c.ParentId == g.Id); if (child != null && !selection.Contains(child.Id)) selection.Add(child.Id); }
                    }
                    CompositionPlan composition = Composer.Plan(package, selection, true);
                    if (!composition.CanInstall) { lines.Add(Path.GetFileName(source) + ": default selection not installable (" + String.Join("; ", composition.Errors.Take(2)) + ")"); continue; }
                    using (var cache = new PrepareCache())
                    {
                        var cold = System.Diagnostics.Stopwatch.StartNew(); ReplacePlan first = ReplacePlanner.Check(composition, sevenZip, engine, cache, null); cold.Stop();
                        var warm = System.Diagnostics.Stopwatch.StartNew(); ReplacePlan second = ReplacePlanner.Check(composition, sevenZip, engine, cache, null); warm.Stop();
                        ReplaceChecks.Require(cache.Extractions == 1 && first.Files.Count == second.Files.Count, "the re-check extracted again");
                        lines.Add(Path.GetFileName(source) + " (" + Utilities.FriendlyBytes(new FileInfo(source).Length) + ", " + composition.Files.Count + " selected files, " + Utilities.FriendlyBytes(composition.TotalBytes) + "): package read " + load.ElapsedMilliseconds + " ms; automatic check cold " + cold.ElapsedMilliseconds + " ms, re-check " + warm.ElapsedMilliseconds + " ms (extractions " + cache.Extractions + ", cache hits " + cache.Hits + "); affects " + first.AffectsText + "; conflicts " + first.ConflictList.Count + "; can install " + first.CanInstall);
                    }
                }
                ReplaceChecks.Require(PakIndex.Open(pakPath).TocSha256() == beforeToc, "real PAK table changed");
                ReplaceChecks.Require(Directory.Exists(Path.Combine(RealGame, "natives")) == nativesBefore, "natives folder changed");
                ReplaceChecks.Require(!File.Exists(engine.StatePath), "check wrote engine state");
                File.WriteAllLines(Path.Combine(root, "auto-check-timing.txt"), lines);
                return String.Join(" | ", lines);
            });
            check("library costume keys and 'cammy, c1' search on the real libraries (2.0.1, read-only; cold listing and cached rescan timed)", delegate
            {
                var dirs = Libraries.Where(Directory.Exists).ToArray();
                if (dirs.Length == 0) throw new SkipException("libraries not found");
                string previous = Library.IdentityCacheDirectory;
                try
                {
                    Library.IdentityCacheDirectory = Path.Combine(root, "costume-identity-cache");
                    var cold = System.Diagnostics.Stopwatch.StartNew(); var library = Library.Scan(dirs, @"C:\Program Files\7-Zip\7z.exe"); cold.Stop();
                    Library.IdentityCacheDirectory = Path.Combine(root, "costume-identity-cache");
                    var warm = System.Diagnostics.Stopwatch.StartNew(); library = Library.Scan(dirs, @"C:\Program Files\7-Zip\7z.exe"); warm.Stop();
                    int withKeys = library.Count(e => e.CostumeKeys.Count > 0), sharedOnly = library.Count(e => e.CostumeKeys.Count > 0 && e.CostumeKeys.All(k => k.EndsWith("/000")));
                    var lines = new List<string> { library.Count + " mods; " + withKeys + " with costume keys (" + sharedOnly + " shared parts only); cold listing " + cold.ElapsedMilliseconds + " ms, cached rescan " + warm.ElapsedMilliseconds + " ms" };
                    foreach (string text in new[] { "cammy, c1", "cammy, c2" })
                    {
                        LibrarySearch.Query q = LibrarySearch.Parse(text, MainForm.SearchCharacterTerm); var codes = LibrarySearch.CodesFor(q.Characters);
                        var ranked = library.Where(e => LibrarySearch.MatchesText(e, q, MainForm.EntryMatchesCharacter, MainForm.EntryMatchesName)).Select(e => new { E = e, R = LibrarySearch.CostumeRank(e, q, codes, MainForm.EntryMatchesCharacter) }).Where(x => x.R >= 0).OrderBy(x => x.R).ThenBy(x => x.E.Name, StringComparer.OrdinalIgnoreCase).ToList();
                        // 2.0.0 behaviour: comma search treated "c1" as a name word (title/file name) plus the character.
                        string word = text.Split(',')[1].Trim();
                        int titleOnly = library.Count(e => MainForm.EntryMatchesCharacter(e, "Cammy") && MainForm.EntryMatchesName(e, word));
                        lines.Add("'" + text + "': " + ranked.Count + " results (" + ranked.Count(x => x.R == LibrarySearch.Direct) + " change the costume files, " + ranked.Count(x => x.R == LibrarySearch.SharedParts) + " shared parts only, " + ranked.Count(x => x.R == LibrarySearch.TitleOnly) + " by title only); 2.0.0 title search: " + titleOnly);
                        foreach (var x in ranked) lines.Add("   " + (x.R == 0 ? "files " : x.R == 1 ? "shared" : "title ") + "  " + x.E.Name + "  [" + String.Join(",", x.E.CostumeKeys) + "]");
                    }
                    File.WriteAllLines(Path.Combine(root, "costume-search.txt"), lines);
                    return String.Join(" | ", lines.Where(l => !l.StartsWith("   ")));
                }
                finally { Library.IdentityCacheDirectory = previous; }
            });
            check("library tool kinds and 'reframework'/'code' search on the real libraries (2.0.3, read-only; reuses the listing above)", delegate
            {
                var dirs = Libraries.Where(Directory.Exists).ToArray();
                if (dirs.Length == 0) throw new SkipException("libraries not found");
                string previous = Library.IdentityCacheDirectory;
                try
                {
                    Library.IdentityCacheDirectory = Path.Combine(root, "costume-identity-cache");
                    var clock = System.Diagnostics.Stopwatch.StartNew(); var library = Library.Scan(dirs, @"C:\Program Files\7-Zip\7z.exe"); clock.Stop();
                    var lines = new List<string>();
                    Func<string, int> withKind = kind => library.Count(e => e.ToolKinds.Contains(kind));
                    int tools = library.Count(LibrarySearch.IsToolMod), refOnly = library.Count(e => e.ToolKinds.Count > 0 && !LibrarySearch.IsToolMod(e));
                    lines.Add(library.Count + " mods (" + library.Count(e => e.Detection == "Error") + " unreadable); tool kinds: script " + withKind(Library.ToolScript) + ", plugin " + withKind(Library.ToolPlugin) + ", dll " + withKind(Library.ToolDll) + ", reframework " + withKind(Library.ToolReframework) + "; tool mods " + tools + "; costume mods with REFramework files only (not tool mods) " + refOnly + "; rescan " + clock.ElapsedMilliseconds + " ms");
                    foreach (string text in new[] { "reframework", "code", "plugin", "lua", "dll", "is:tool" })
                    {
                        LibrarySearch.Query q = LibrarySearch.Parse(text, MainForm.SearchCharacterTerm); var codes = LibrarySearch.CodesFor(q.Characters);
                        var found = library.Where(e => LibrarySearch.MatchesText(e, q, MainForm.EntryMatchesCharacter, MainForm.EntryMatchesName) && LibrarySearch.CostumeRank(e, q, codes, MainForm.EntryMatchesCharacter) >= 0 && MainForm.MatchesModType(e, q.Type)).OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
                        int byKind = found.Count(e => q.PlainTool != null ? LibrarySearch.MatchesTool(e, q.PlainTool) : LibrarySearch.IsToolMod(e));
                        lines.Add("'" + text + "': " + found.Count + " results (" + byKind + " by tool kind, " + (found.Count - byKind) + " by name/path only)");
                        foreach (var e in found) lines.Add("   " + e.Name + "  [" + String.Join("+", e.ToolKinds) + "]" + (LibrarySearch.ToolLabel(e).Length > 0 ? "  " + LibrarySearch.ToolLabel(e) : "") + "  " + e.Kind + (e.OptionCount > 0 ? "  " + e.OptionCount + " natives options" : ""));
                    }
                    File.WriteAllLines(Path.Combine(root, "tool-search.txt"), lines);
                    return String.Join(" | ", lines.Where(l => !l.StartsWith("   ")));
                }
                finally { Library.IdentityCacheDirectory = previous; }
            });
            check("Fluffy presets map to the real libraries (read-only dry run)", delegate
            {
                var files = FluffyPresets.PresetFiles(FluffyRoots);
                if (files.Count == 0) throw new SkipException("no presets");
                var library = Library.Scan(Libraries.Where(Directory.Exists), @"C:\Program Files\7-Zip\7z.exe");
                var lines = new List<string>(); int total = 0, mapped = 0;
                foreach (string file in files)
                {
                    var entries = FluffyPresets.Parse(File.ReadAllText(file));
                    var map = FluffyPresets.Map(entries, library, @"C:\Program Files\7-Zip\7z.exe");
                    int ok = map.Count(m => m.Entry != null);
                    total += map.Count; mapped += ok;
                    lines.Add(file + ": " + ok + " of " + map.Count + " sections mapped; " + FluffyPresets.Group(map).Count(g => g.Entry != null) + " installs");
                    foreach (var m in map) lines.Add("  " + (m.Entry == null ? "UNMATCHED " : "ok  ") + m.Sections[0].Section + " -> " + m.Message + (m.Entry == null ? "" : " [" + m.Kind + "]"));
                }
                File.WriteAllLines(Path.Combine(root, "preset-dry-run.txt"), lines);
                return mapped + " of " + total + " sections mapped across " + files.Count + " presets (details in preset-dry-run.txt)";
            });
            check("real game tool status and REFramework loader state are detected (read-only)", delegate
            {
                if (!Directory.Exists(RealGame)) throw new SkipException("game not found");
                ToolStatus t = ToolStatus.Detect(RealGame); string detail; LooseLoaderState state = LooseLoader.State(RealGame, out detail);
                return t.ReFrameworkDetail + "; " + t.SlotsLoaderDetail + "; " + t.CrashReporterState + "; " + detail + " (" + state + ")";
            });
            File.WriteAllLines(Path.Combine(root, "replace-real-checks.txt"), report);
            Console.WriteLine("Replace real checks: " + failures + " failed, " + skips + " skipped.");
            return failures == 0 ? 0 : 1;
        }

        private static string TableSha(PakIndex p)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(p.Table)).Replace("-", "");
        }
        private sealed class SkipException : Exception { internal SkipException(string m) : base(m) { } }
    }
}
