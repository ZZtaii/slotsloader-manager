using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

namespace SlotsInstaller
{
    // 2.3.0 --filter-real-check <output> <game root> (test.ps1 -RealArchives, suite filter-real). Read-only for the
    // game and both Fluffy libraries: portraits are extracted into this run's Data\portraits\v1, the library
    // index goes to this run's Data, and the screenshots into <output>.
    internal static class FilterRealChecks
    {
        internal static string[] RealLibraries { get { return LocalTestConfig.Current.Libraries.ToArray(); } }   // 2.4.3: from tests.local.json
        private static readonly BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;

        internal static int Run(string output, string gameRoot)
        {
            output = Path.GetFullPath(output); Directory.CreateDirectory(output);
            var report = new List<string>(); int failures = 0;
            Action<string, Action> check = delegate(string name, Action body)
            {
                var clock = Stopwatch.StartNew();
                try { body(); report.Add("PASS " + name + " (" + clock.ElapsedMilliseconds + " ms)"); }
                catch (Exception ex) { failures++; report.Add("FAIL " + name + ": " + ex.Message); Program.Log(ex); }
            };
            string cacheDir = FighterPortraits.DefaultCacheDirectory;
            PortraitReport cold = null;
            var known = Characters.ByCode.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
            string pakBefore = PakIdentity(gameRoot);
            check("portraits from the real game (cold extraction into this run's Data)", delegate
            {
                Require(Directory.Exists(gameRoot), "game folder not found: " + gameRoot);
                Require(!File.Exists(Path.Combine(cacheDir, "portraits.tsv")), "this run's Data already has a portrait cache; use a new output folder");
                int tiles = 0;
                cold = FighterPortraits.Load(gameRoot, known, cacheDir, CancellationToken.None, delegate(PortraitInfo info, Bitmap b) { tiles++; if (b != null) b.Dispose(); }, null);
                var lines = new List<string> { "cache " + cold.Cache + "; " + cold.PakCount + " PAKs; open " + cold.OpenMs + " ms, resolve " + cold.ResolveMs + " ms, total " + cold.TotalMs + " ms" + (cold.Discarded ? " (DISCARDED: the game changed while reading)" : ""), "identity " + cold.Identity, "#id\tname\tstatus\tcandidate\tpak\tsize\tformat\tms\tpath\tnote" };
                foreach (PortraitInfo f in cold.Fighters) lines.Add(String.Join("\t", f.Id, f.Name, f.Status, f.Candidate, f.Pak, f.Size, f.Format, f.Milliseconds.ToString(), f.Path, f.Note));
                lines.Add("stage names (" + cold.StageSource + "):");
                foreach (var kv in cold.StageNames.OrderBy(k => k.Key, StringComparer.Ordinal)) lines.Add("  ess" + kv.Key + "\t" + kv.Value);
                File.WriteAllLines(Path.Combine(output, "portraits-real.txt"), lines);
                Require(cold.Cache == "miss" && !cold.Discarded, "expected a cold extraction, got " + cold.Cache + (cold.Discarded ? " (discarded)" : ""));
                var missing = known.Where(id => !cold.Fighters.Any(f => f.Id == id && f.Ok)).ToList();
                Require(missing.Count == 0, "fighters without a portrait: " + String.Join(", ", missing.Select(id => id + " " + FighterPortraits.DisplayName(id) + " (" + String.Join("; ", cold.Fighters.Where(f => f.Id == id).Select(f => f.Status + " " + f.Note)) + ")")));
                Require(tiles == cold.Fighters.Count && known.All(id => File.Exists(Path.Combine(cacheDir, id + ".png"))), "tiles/PNGs missing");
                using (Bitmap sample = FighterPortraits.LoadPng(Path.Combine(cacheDir, "009.png")))
                    Require(sample != null && sample.Width == FighterPortraits.TileWidth && sample.Height == FighterPortraits.TileHeight && Opaque(sample) > 0.25, "Cammy's tile is not a 300x420 portrait with an opaque figure");
                Require(cold.StageNames.Count >= 10 && cold.StageNames.ContainsKey("0000"), "stage names were not read from the game: " + cold.StageNames.Count + " (" + cold.StageSource + ")");
                report.Add("  " + cold.Fighters.Count(f => f.Ok) + "/" + known.Count + " known fighters; candidates: " + String.Join(", ", cold.Fighters.GroupBy(f => f.Candidate).Select(g => g.Key + " " + g.Count())) + "; " + cold.Fighters.Count(f => !known.Contains(f.Id)) + " fighters newer than this build; " + cold.TotalMs + " ms cold; " + cold.StageNames.Count + " stage names");
            });
            check("portraits cached (second load reads only the PNGs)", delegate
            {
                PortraitReport warm = FighterPortraits.Load(gameRoot, known, cacheDir, CancellationToken.None, delegate(PortraitInfo info, Bitmap b) { if (b != null) b.Dispose(); }, null);
                File.AppendAllText(Path.Combine(output, "portraits-real.txt"), "second load: cache " + warm.Cache + ", " + warm.TotalMs + " ms, resolve " + warm.ResolveMs + " ms" + Environment.NewLine);
                Require(warm.Cache == "hit" && warm.ResolveMs == 0 && warm.Fighters.Count(f => f.Ok) >= known.Count && warm.StageNames.Count == cold.StageNames.Count, "second load was " + warm.Cache);
                report.Add("  cached load " + warm.TotalMs + " ms");
            });
            check("game PAKs unchanged by the portrait reads", delegate
            {
                string after = PakIdentity(gameRoot);
                // The user may install with Fluffy at any time; only a change during our reads would matter, and those are read-only.
                if (after != pakBefore) report.Add("  NOTE PAK identity changed during the run (another tool wrote the game; our reads are FileAccess.Read only): " + pakBefore + " -> " + after);
            });

            List<LibraryEntry> library = null;
            check("classify the real libraries (read-only listing into this run's Data)", delegate
            {
                string previous = Library.IdentityCacheDirectory; Library.IdentityCacheDirectory = AppConfig.DataDir;
                var clock = Stopwatch.StartNew();
                try { library = Library.Scan(RealLibraries.Where(Directory.Exists), @"C:\Program Files\7-Zip\7z.exe"); }
                finally { Library.IdentityCacheDirectory = previous; }
                long ms = clock.ElapsedMilliseconds;
                var lines = new List<string>();
                foreach (string lib in RealLibraries)
                {
                    var mods = library.Where(e => String.Equals(Path.GetDirectoryName(e.SourcePath), lib, StringComparison.OrdinalIgnoreCase)).ToList();
                    lines.Add(lib + ": " + mods.Count + " mods; Characters " + mods.Count(ModCategories.IsCharacter) + ", Stages " + mods.Count(ModCategories.IsStage) + ", Code tools " + mods.Count(ModCategories.IsTool) + ", Misc " + mods.Count(ModCategories.IsMisc) + " (Stage " + mods.Count(ModCategories.IsStage) + "), unreadable " + mods.Count(e => e.OptionCount < 0) + ", in several categories " + mods.Count(e => Categories(e) > 1));
                }
                lines.Add("all: " + library.Count + " mods; Characters " + library.Count(ModCategories.IsCharacter) + ", Stages " + library.Count(ModCategories.IsStage) + ", Code tools " + library.Count(ModCategories.IsTool) + ", Misc " + library.Count(ModCategories.IsMisc) + " (Stage " + library.Count(ModCategories.IsStage) + "), unreadable " + library.Count(e => e.OptionCount < 0) + ", several " + library.Count(e => Categories(e) > 1) + ", none " + library.Count(e => Categories(e) == 0 && e.OptionCount >= 0) + "; listing " + ms + " ms");
                lines.Add("label map: " + String.Join("; ", ModCategories.LabelMap.Select(m => m[0] + " -> " + m[1])));
                lines.Add("misc kinds: " + String.Join(", ", library.SelectMany(e => e.MiscAreas).GroupBy(l => l).OrderByDescending(g => g.Count()).Select(g => g.Key + " " + g.Count())));
                lines.Add("stage codes in Misc stage mods: " + String.Join(", ", library.Where(ModCategories.IsStage).SelectMany(e => e.StageIds).GroupBy(s => s).OrderBy(g => g.Key).Select(g => ModCategories.StageName(g.Key, cold != null ? cold.StageNames : null) + " (" + g.Key + ") " + g.Count())));
                lines.Add("tool kinds: " + String.Join(", ", Library.ToolKindOrder.Select(k => k + " " + library.Count(e => ModCategories.IsTool(e) && e.ToolKinds.Contains(k)))));
                lines.Add("fighter codes from art/colour data: " + String.Join(", ", library.Where(e => e.CharacterCodes.Count > 0 && e.CostumeKeys.Count == 0).Select(e => e.Name + " {" + String.Join(",", e.CharacterCodes) + "}").Take(40)));
                lines.Add("");
                lines.Add("stage mods (Misc, kind Stage):"); foreach (LibraryEntry e in library.Where(ModCategories.IsStage).OrderBy(e => e.Name)) lines.Add("  " + e.Name + " :: " + String.Join(", ", e.StageIds.Select(s => ModCategories.StageName(s, cold != null ? cold.StageNames : null))) + " :: " + ModCategories.Describe(e));
                lines.Add("misc mods:"); foreach (LibraryEntry e in library.Where(ModCategories.IsMisc).OrderBy(e => e.Name)) lines.Add("  " + e.Name + " :: " + String.Join(", ", e.MiscAreas) + " :: " + ModCategories.Describe(e));
                lines.Add("code tools:"); foreach (LibraryEntry e in library.Where(ModCategories.IsTool).OrderBy(e => e.Name)) lines.Add("  " + e.Name + " :: " + String.Join("+", e.ToolKinds) + " :: " + ModCategories.Describe(e));
                lines.Add("in several categories:"); foreach (LibraryEntry e in library.Where(e => Categories(e) > 1).OrderBy(e => e.Name)) lines.Add("  " + e.Name + " :: " + ModCategories.Describe(e));
                lines.Add("readable, no category:"); foreach (LibraryEntry e in library.Where(e => Categories(e) == 0 && e.OptionCount >= 0).OrderBy(e => e.Name)) lines.Add("  " + e.Name);
                File.WriteAllLines(Path.Combine(output, "categories-real.txt"), lines);
                foreach (string line in lines.Take(RealLibraries.Length + 4)) report.Add("  " + line);
                Require(library.Count > 100 && library.Count(ModCategories.IsCharacter) > library.Count / 2, "unexpectedly few character mods");
                Require(library.Count(ModCategories.IsStage) >= 1 && library.Count(ModCategories.IsTool) >= 1 && library.Count(ModCategories.IsMisc) >= 1, "a category is empty");
                Require(library.All(e => !(ModCategories.IsMisc(e) && (ModCategories.IsCharacter(e) || ModCategories.IsTool(e)))), "a Characters or Code tools mod is also Misc");
                Require(library.SelectMany(e => e.MiscAreas).All(l => ModCategories.LabelOrder.Contains(l)), "a raw folder name reached the Misc kinds");
                Require(library.Count(e => Categories(e) == 0 && e.OptionCount >= 0) == 0, "readable mods without any category");
                var examples = library.Where(ModCategories.IsStage).Take(3).Select(e => e.Name + " (" + String.Join(", ", e.StageIds.Select(s => ModCategories.StageName(s, cold != null ? cold.StageNames : null))) + ")")
                    .Concat(library.Where(e => ModCategories.IsMisc(e) && !ModCategories.IsCharacter(e)).Take(4).Select(e => e.Name + " (" + String.Join(", ", e.MiscAreas) + ")"));
                report.Add("  examples: " + String.Join("; ", examples));
            });
            check("no file-name fighter for Misc / Code tools mods (2.3.1): every changed row before -> after and the change in each fighter card count", delegate
            {
                Require(library != null, "no library");
                // 2.3.0 rule: without fighter files the row took the file-name guess, and FighterCodes counted it.
                Func<LibraryEntry, string> oldCharacter = delegate(LibraryEntry e) { return e.Detection == "Error" || e.Detection == "Ambiguous" || e.Detection == "UnknownNative" || e.CharacterCodes.Count > 0 ? e.Character : e.FilenameCharacter ?? ""; };
                Func<LibraryEntry, HashSet<string>> oldCodes = delegate(LibraryEntry e) { var codes = new HashSet<string>(e.CharacterCodes, StringComparer.Ordinal); string c = oldCharacter(e); if (!String.IsNullOrEmpty(c)) foreach (var kv in Characters.ByCode) if (String.Equals(kv.Value, c, StringComparison.OrdinalIgnoreCase)) codes.Add(kv.Key); return codes; };
                var changed = library.Where(e => !String.Equals(oldCharacter(e), e.Character ?? "", StringComparison.Ordinal)).OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
                var lines = new List<string> { changed.Count + " library rows change:" };
                foreach (LibraryEntry e in changed) lines.Add("  " + e.Name + " :: " + (ModCategories.IsTool(e) ? "Code tools" : "Misc " + String.Join("+", e.MiscAreas)) + " :: \"" + oldCharacter(e) + "\" -> \"" + MainForm.LibraryCharacterLabel(e) + "\"");
                var deltas = Characters.ByCode.Keys.Select(code => new { code, before = library.Count(e => oldCodes(e).Contains(code)), after = library.Count(e => MainForm.FighterCodes(e).Contains(code)) }).Where(d => d.before != d.after).ToList();
                lines.Add("fighter card counts that change (all mods, no filter): " + (deltas.Count == 0 ? "none" : String.Join(", ", deltas.Select(d => Characters.ByCode[d.code] + " " + d.before + " -> " + d.after))));
                File.WriteAllLines(Path.Combine(output, "name-guess-real.txt"), lines);
                foreach (string line in lines) report.Add("  " + line);
                Require(changed.All(e => e.CharacterCodes.Count == 0 && (ModCategories.IsMisc(e) || ModCategories.IsTool(e)) && e.Character == ""), "a mod with fighter files or outside Misc/Code tools changed");
                Require(library.Where(e => e.CharacterCodes.Count == 0 && !ModCategories.IsMisc(e) && !ModCategories.IsTool(e) && e.Detection != "Error").All(e => e.Character == (e.FilenameCharacter ?? "")), "the name guess changed for a mod outside Misc/Code tools");
            });
            check("category search terms on the real libraries", delegate
            {
                Require(library != null, "no library");
                Func<string, int> find = delegate(string text)
                {
                    LibrarySearch.Query q = LibrarySearch.Parse(text, MainForm.SearchCharacterTerm); var codes = LibrarySearch.CodesFor(q.Characters);
                    return library.Count(e => LibrarySearch.MatchesText(e, q, MainForm.EntryMatchesCharacter, MainForm.EntryMatchesName) && LibrarySearch.CostumeRank(e, q, codes, MainForm.EntryMatchesCharacter) >= 0 && MainForm.MatchesModType(e, q.Type));
                };
                int stage = find("stage"), isStage = find("is:stage"), misc = find("misc"), isMisc = find("is:misc"), isCharacter = find("is:character");
                report.Add("  'stage' " + stage + ", is:stage " + isStage + ", 'misc' " + misc + ", is:misc " + isMisc + ", is:character " + isCharacter + ", 'cam, c1' " + find("cam, c1"));
                Require(isStage == library.Count(ModCategories.IsStage) && stage >= isStage && isMisc == library.Count(ModCategories.IsMisc) && misc >= isMisc && isCharacter == library.Count(ModCategories.IsCharacter), "category terms disagree with the categories");
            });
            check("sort chips on the real libraries (2.4.1, read-only): top 3 for Added ↓ and Last used ↓ (installed outfits from the game's costume_mods, removed outfits and removed sets beside it, replacement records from this run's Data), sort timing in a hidden window", delegate
            {
                Require(library != null, "no library");
                var lines = SortReal(gameRoot, library);
                File.WriteAllLines(Path.Combine(output, "sort-real.txt"), lines);
                foreach (string line in lines) report.Add("  " + line);
            });
            check("the window's own library load (fresh and cached index) shows the real mod count in the status line; screenshots of the whole panel with the real libraries and portraits (fighters, Misc UI, Code tools, Installed, a library tile; no scrolling with Misc chips)", delegate
            {
                Require(library != null, "no library");
                var lines = new List<string>();
                var shots = Screenshots(output, gameRoot, library, cacheDir, lines);
                File.WriteAllLines(Path.Combine(output, "screenshots-real.txt"), lines);
                foreach (string line in lines) report.Add("  " + line);
                report.Add("  " + String.Join(", ", shots.Select(Path.GetFileName)));
            });
            report.Add(failures == 0 ? "ALL PASS" : failures + " FAILED");
            File.WriteAllLines(Path.Combine(output, "filter-real-check.txt"), report);
            File.WriteAllLines(Path.Combine(AppConfig.DataDir, "filter-real-check.txt"), report);
            return failures == 0 ? 0 : 1;
        }

        // Reads only: the library list already scanned, ListInstalled on the game's costume_mods, the removed-outfit
        // and removed-set records beside it and this run's Data. Nothing is written outside <output> and this run's Data.
        private static List<string> SortReal(string gameRoot, List<LibraryEntry> library)
        {
            var lines = new List<string>();
            string costumeRoot = new AppConfig { GameRoot = gameRoot }.CostumeRoot;
            List<InstalledOutfit> outfits = Directory.Exists(costumeRoot) ? Installer.ListInstalled(costumeRoot) : new List<InstalledOutfit>();
            var clock = Stopwatch.StartNew();
            List<UsageRecord> removed = LibraryUsage.ReadRemoved(costumeRoot, new[] { ReplaceSettings.Load().StoreRoot(AppConfig.DataDir) });
            long readMs = clock.ElapsedMilliseconds;
            clock.Restart();
            List<UsageRecord> again = LibraryUsage.ReadRemoved(costumeRoot, new[] { ReplaceSettings.Load().StoreRoot(AppConfig.DataDir) });
            long cachedMs = clock.ElapsedMilliseconds;
            lines.Add(library.Count + " mods; " + outfits.Count(o => o.Managed) + " managed extra outfits installed; " + removed.Count + " removal records (first read " + readMs + " ms, cached " + cachedMs + " ms, " + again.Count + " again); Added unknown for " + library.Count(e => e.AddedUtc == DateTime.MinValue));
            // No Work runs here, so no UI SynchronizationContext: disposing a second WindowsFormsSynchronizationContext
            // disposes the thread's shared marshaling control, and the later window scan (Screenshots) would never complete.
            bool previousAutoInstall = WindowsFormsSynchronizationContext.AutoInstall;
            WindowsFormsSynchronizationContext.AutoInstall = false;
            try
            {
                using (var form = new MainForm(new AppConfig { GameRoot = gameRoot, Libraries = RealLibraries.ToList(), SevenZipPath = @"C:\Program Files\7-Zip\7z.exe" }))
                {
                    Field("firstLoad").SetValue(form, false);
                    form.ShowInTaskbar = false; form.Opacity = 0; form.MinimumSize = Size.Empty; form.Show();
                    form.ClientSize = new Size(Math.Min(DpiMetrics.Px(form, 1460), Screen.PrimaryScreen.WorkingArea.Width), Math.Min(DpiMetrics.Px(form, 920), Screen.PrimaryScreen.WorkingArea.Height));
                    Field("installed").SetValue(form, outfits); Field("removedUsage").SetValue(form, removed);
                    Field("library").SetValue(form, library);
                    typeof(MainForm).GetMethod("ApplyFilters", PrivateInstance).Invoke(form, null); Application.DoEvents();
                    ListView list = (ListView)Field("modList").GetValue(form);
                    var chips = (Dictionary<LibrarySortKey, FlatButton>)Field("sortChips").GetValue(form);
                    Func<LibraryEntry, string> when = delegate(LibraryEntry e)
                    {
                        DateTime? used = form.LibraryLastUsed(e);
                        return !used.HasValue ? "never used" : "used " + used.Value.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture) + " UTC";
                    };
                    var timings = new List<string>();
                    foreach (LibrarySortKey key in new[] { LibrarySortKey.Added, LibrarySortKey.LastUsed, LibrarySortKey.Installed })
                    {
                        clock.Restart(); chips[key].PerformClick(); list.Refresh(); long ms = clock.ElapsedMilliseconds;
                        timings.Add(chips[key].Text + " " + ms + " ms");
                        Require(list.Items.Count == library.Count, "rows lost after " + chips[key].Text);
                        var top = list.Items.Cast<ListViewItem>().Take(3).Select(i => (LibraryEntry)i.Tag).ToList();
                        if (key == LibrarySortKey.Added) lines.Add("Added \u2193 top 3: " + String.Join("; ", top.Select(e => e.Name + " (" + (e.AddedUtc == DateTime.MinValue ? "unknown" : e.AddedUtc.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture) + " UTC") + ")")));
                        if (key == LibrarySortKey.LastUsed) lines.Add("Last used \u2193 top 3: " + String.Join("; ", top.Select(e => e.Name + " (" + when(e) + ")")) + "; " + library.Count(e => form.LibraryLastUsed(e).HasValue) + " with a use time, " + library.Count(e => !form.LibraryLastUsed(e).HasValue) + " never used");
                        if (key == LibrarySortKey.Added) for (int i = 1; i < list.Items.Count; i++) Require(((LibraryEntry)list.Items[i - 1].Tag).AddedUtc >= ((LibraryEntry)list.Items[i].Tag).AddedUtc, "Added \u2193 out of order at row " + i);
                    }
                    lines.Add("sort click incl. list update at " + form.DeviceDpi + " DPI: " + String.Join(", ", timings));
                    form.Close();
                }
            }
            finally { WindowsFormsSynchronizationContext.AutoInstall = previousAutoInstall; }
            return lines;
        }

        private static int Categories(LibraryEntry e) { return (ModCategories.IsCharacter(e) ? 1 : 0) + (ModCategories.IsTool(e) ? 1 : 0) + (ModCategories.IsMisc(e) ? 1 : 0); }

        private static string PakIdentity(string gameRoot) { try { return GamePakReader.Open(gameRoot).Identity; } catch (Exception ex) { return "unreadable: " + ex.Message; } }

        internal static double Opaque(Bitmap b)
        {
            int opaque = 0, total = 0;
            for (int y = 0; y < b.Height; y += 7) for (int x = 0; x < b.Width; x += 7) { total++; if (b.GetPixel(x, y).A > 200) opaque++; }
            return total == 0 ? 0 : opaque / (double)total;
        }

        // Renders the whole panel in a hidden window (Opacity 0, not in the taskbar) at 1460x920 logical, saved at
        // half the physical size. The library comes from the window's own scan (RefreshLibrary, as at start-up):
        // once with an empty index (a fresh listing) and once cached, and the status line is recorded each time.
        internal static List<string> Screenshots(string output, string gameRoot, List<LibraryEntry> library, string cacheDir, List<string> statusLines)
        {
            var files = new List<string>();
            var config = new AppConfig { GameRoot = gameRoot, Libraries = RealLibraries.ToList(), SevenZipPath = @"C:\Program Files\7-Zip\7z.exe" };
            string previousIndex = Library.IdentityCacheDirectory;
            // The library scan reports progress through a BackgroundWorker: it needs the UI context, as in the other window checks.
            SynchronizationContext previousContext = SynchronizationContext.Current;
            bool previousAutoInstall = WindowsFormsSynchronizationContext.AutoInstall;
            WindowsFormsSynchronizationContext.AutoInstall = false;
            using (var uiContext = new WindowsFormsSynchronizationContext())
            try
            {
            SynchronizationContext.SetSynchronizationContext(uiContext);
            using (var form = new MainForm(config))
            {
                Field("firstLoad").SetValue(form, false);
                form.ShowInTaskbar = false; form.Opacity = 0; form.MinimumSize = Size.Empty; form.Show();
                form.PortraitCacheDirectory = cacheDir;
                Size size = new Size(DpiMetrics.Px(form, 1460), DpiMetrics.Px(form, 920));
                Panel canvas = new Panel { Size = size, BackColor = Palette.Background, ForeColor = Palette.Text };
                Control[] children = form.Controls.Cast<Control>().ToArray();
                foreach (Control child in children) form.Controls.Remove(child);
                canvas.Controls.AddRange(children); form.Controls.Add(canvas); canvas.CreateControl();
                Label status = (Label)Field("statusText").GetValue(form);
                MethodInfo refresh = typeof(MainForm).GetMethod("RefreshLibrary", PrivateInstance);
                string fresh = Path.Combine(output, "fresh-index-" + Guid.NewGuid().ToString("N").Substring(0, 6));
                try
                {
                    foreach (string pass in new[] { "fresh", "cached" })
                    {
                        if (pass == "fresh") Library.IdentityCacheDirectory = fresh;
                        int misses = Library.CacheMisses; var clock = Stopwatch.StartNew();
                        refresh.Invoke(form, new object[] { false });
                        DateTime until = DateTime.UtcNow.AddMinutes(10);
                        while (form.Working && DateTime.UtcNow < until) { Application.DoEvents(); Thread.Sleep(20); }
                        Application.DoEvents();
                        var listed = (List<LibraryEntry>)Field("library").GetValue(form);
                        string expected = MainForm.LibraryFoundText(listed.Count, RealLibraries.Length);
                        statusLines.Add(pass + " load (" + (Library.CacheMisses - misses) + " sources read past the index (folders are always read), " + clock.ElapsedMilliseconds + " ms): status \"" + status.Text + "\"");
                        Require(!form.Working && listed.Count == library.Count && status.Text.StartsWith(expected), pass + " load status \"" + status.Text + "\" (expected \"" + expected + " …\")");
                    }
                }
                finally { Library.IdentityCacheDirectory = previousIndex; try { Directory.Delete(fresh, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
                form.ShowFilters(true, false);
                DateTime deadline = DateTime.UtcNow.AddSeconds(60);
                while (form.PortraitsLoading && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(10); }
                Application.DoEvents();
                FilterPanel panel = (FilterPanel)Field("filterPanel").GetValue(form);
                Require(panel.Grid.PortraitCount >= Characters.ByCode.Count, "portraits did not arrive in the grid: " + panel.Grid.PortraitCount);
                Require(panel.Kinds.ItemCount == 5, "library tiles missing: " + String.Join(", ", panel.Kinds.Cards.Select(c => c.Name)));
                statusLines.Add("tiles: " + String.Join(", ", panel.Kinds.Cards.Select(c => c.Name + " " + c.Count)));
                var shots = new[] { "filters-fighters.png", "filters-misc-ui.png", "filters-code-tools.png", "filters-installed.png", "filters-library.png" };
                MethodInfo clear = typeof(MainForm).GetMethod("ClearLibraryFilters", PrivateInstance);
                foreach (string shot in shots)
                {
                    clear.Invoke(form, null); panel.AutoScrollPosition = Point.Empty;
                    if (shot == "filters-fighters.png") panel.Grid.SetSelected(new[] { "009", "016" }, true);
                    if (shot == "filters-code-tools.png") panel.Kinds.Activate(0, false);
                    if (shot == "filters-installed.png") panel.Kinds.Activate(2, false);
                    if (shot == "filters-library.png") panel.Kinds.Activate(4, false);   // the second library (Fluffy - Copy)
                    if (shot == "filters-misc-ui.png")
                    {
                        panel.Kinds.Activate(1, false);
                        FlatButton ui = panel.MiscChips.Controls.OfType<FlatButton>().FirstOrDefault(b => (string)b.Tag == "UI");
                        Require(ui != null, "no UI chip"); ui.PerformClick();
                    }
                    // Highlight the first row without opening the mod (no package read in this check).
                    ListView list = (ListView)Field("modList").GetValue(form);
                    Field("selectingMod").SetValue(form, true);
                    try { foreach (ListViewItem item in list.SelectedItems.Cast<ListViewItem>().ToList()) item.Selected = false; if (list.Items.Count > 0) { list.Items[0].Selected = true; list.EnsureVisible(0); } }
                    finally { Field("selectingMod").SetValue(form, false); }
                    LayoutAll(canvas); Application.DoEvents(); LayoutAll(canvas); canvas.Refresh();
                    // 1.0.15: no "fits" requirement here any more: at the 330 library the panel may scroll (accepted by the
                    // user); the filters suite checks it fits with a 250 library. The status line still says which.
                    statusLines.Add(shot + ": " + list.Items.Count + " mods listed, status \"" + status.Text + "\", panel " + (panel.VerticalScroll.Visible ? "scrolls" : "fits"));
                    string file = Path.Combine(output, shot);
                    using (var full = new Bitmap(canvas.Width, canvas.Height))
                    {
                        canvas.DrawToBitmap(full, new Rectangle(0, 0, canvas.Width, canvas.Height));
                        using (var half = new Bitmap(full.Width / 2, full.Height / 2))
                        using (Graphics g = Graphics.FromImage(half)) { g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.DrawImage(full, new Rectangle(0, 0, half.Width, half.Height)); half.Save(file, ImageFormat.Png); }
                        if (shot == "filters-fighters.png")
                        {
                            // 2.3.1: full-resolution crop of the first three portrait cards around the name band, 2x.
                            Rectangle card = panel.Grid.ItemBounds(0), third = panel.Grid.ItemBounds(2);
                            Point origin = canvas.PointToClient(panel.Grid.PointToScreen(Point.Empty));
                            int bandTop = card.Bottom - DpiMetrics.Px(form, 28);
                            var crop = new Rectangle(origin.X + card.Left, origin.Y + bandTop - DpiMetrics.Px(form, 16), third.Right - card.Left, DpiMetrics.Px(form, 32));
                            crop.Intersect(new Rectangle(Point.Empty, full.Size));
                            string zoomFile = Path.Combine(output, "filters-portrait-band-2x.png");
                            using (var zoom = new Bitmap(crop.Width * 2, crop.Height * 2))
                            using (Graphics g = Graphics.FromImage(zoom)) { g.InterpolationMode = InterpolationMode.NearestNeighbor; g.PixelOffsetMode = PixelOffsetMode.Half; g.DrawImage(full, new Rectangle(0, 0, zoom.Width, zoom.Height), crop, GraphicsUnit.Pixel); zoom.Save(zoomFile, ImageFormat.Png); }
                            // The row at the band top must not be darker than both neighbours (the 2.3.0 seam).
                            Func<int, double> luminance = delegate(int y) { double s = 0; int n = 0; for (int x = origin.X + card.Left + card.Width / 4; x < origin.X + card.Right - card.Width / 4; x++) { Color c = full.GetPixel(x, origin.Y + y); s += (c.R + c.G + c.B) / 3.0; n++; } return s / n; };
                            double above = luminance(bandTop - 1), at = luminance(bandTop), below = luminance(bandTop + 1);
                            statusLines.Add("portrait band (Ryu, " + form.DeviceDpi + " DPI): rows above/at/below the band top " + above.ToString("0.0") + " / " + at.ToString("0.0") + " / " + below.ToString("0.0"));
                            Require(!(at + 8 < above && at + 8 < below), "dark seam at the band top: " + above.ToString("0.0") + " / " + at.ToString("0.0") + " / " + below.ToString("0.0"));
                            files.Add(zoomFile);
                        }
                    }
                    files.Add(file);
                }
                // 2.3.1: a click on a mod (real Windows messages to the list) closes the panel and shows its options.
                {
                    clear.Invoke(form, null); panel.Grid.SetSelected(new[] { "009", "016" }, true); LayoutAll(canvas); Application.DoEvents();
                    ListView list = (ListView)Field("modList").GetValue(form);
                    Require(form.FiltersOpen && list.Items.Count > 0, "the panel is not open before the click");
                    Rectangle row = list.GetItemRect(0); Point at = new Point(row.Left + row.Width / 3, row.Top + row.Height / 2);
                    IntPtr xy = (IntPtr)((at.Y << 16) | (at.X & 0xFFFF));
                    PostMessage(list.Handle, 0x0201, (IntPtr)1, xy); PostMessage(list.Handle, 0x0202, IntPtr.Zero, xy);
                    DateTime until = DateTime.UtcNow.AddSeconds(90);
                    do { Application.DoEvents(); Thread.Sleep(20); } while ((form.Working || (Field("package").GetValue(form) == null)) && DateTime.UtcNow < until);
                    LayoutAll(canvas); Application.DoEvents(); LayoutAll(canvas); canvas.Refresh();
                    var picked = (ModPackage)Field("package").GetValue(form);
                    Require(!form.FiltersOpen && picked != null && ((Control)Field("componentTree").GetValue(form)).Visible, "the click did not close the panel / open the mod (open " + form.FiltersOpen + ")");
                    statusLines.Add("filters-mod-picked.png: clicked \"" + picked.Name + "\" (Cammy/Juri selected): panel closed, " + picked.Components.Count + " options shown; reopening keeps " + String.Join("+", panel.Grid.Selected.OrderBy(s => s)));
                    string file = Path.Combine(output, "filters-mod-picked.png");
                    using (var full = new Bitmap(canvas.Width, canvas.Height))
                    {
                        canvas.DrawToBitmap(full, new Rectangle(0, 0, canvas.Width, canvas.Height));
                        using (var half = new Bitmap(full.Width / 2, full.Height / 2))
                        using (Graphics g = Graphics.FromImage(half)) { g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.DrawImage(full, new Rectangle(0, 0, half.Width, half.Height)); half.Save(file, ImageFormat.Png); }
                    }
                    files.Add(file);
                }
                form.Close();
            }
            }
            finally { SynchronizationContext.SetSynchronizationContext(previousContext); WindowsFormsSynchronizationContext.AutoInstall = previousAutoInstall; }
            return files;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
        private static void LayoutAll(Control root) { root.PerformLayout(); foreach (Control child in root.Controls) LayoutAll(child); }
        private static FieldInfo Field(string name)
        {
            FieldInfo field = typeof(MainForm).GetField(name, PrivateInstance);
            if (field == null) throw new InvalidOperationException("Filter check could not locate field " + name + ".");
            return field;
        }
        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    }
}