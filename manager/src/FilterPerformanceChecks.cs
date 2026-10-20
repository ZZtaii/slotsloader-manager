using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace SlotsInstaller
{
    internal static partial class FilterChecks
    {
        private static void PerformanceCall(MainForm form, string method, params object[] args)
        {
            typeof(MainForm).GetMethod(method, PrivateInstance).Invoke(form, args);
        }
        private static void WaitMatches(MainForm form)
        {
            Pump(() => F("filterMatchWorker").GetValue(form) == null && !(bool)F("customMatchesPending").GetValue(form), 30, "background filter matching");
        }
        private static void CheckResponsiveness(string root, List<string> report)
        {
            LibraryFilters.Save(new[] { LibraryFilters.DefaultNsfw() });
            var library = Enumerable.Range(0, 2700).Select(i => new LibraryEntry { Name = "Fixture " + i,
                SourcePath = System.IO.Path.Combine(root, "fixture-" + i + ".zip"), Kind = "ZIP",
                OptionIds = new List<string> { "Main/Normal", "Main/Other" },
                OptionNames = new List<string> { i % 3 == 0 ? "Topless" : "Jacket", "Boots" } }).ToList();
            using (Window w = Open(root, null, new Size(1232, 640), null))
            {
                MainForm form = w.Form;
                form.ShowFilters(false, false);
                F("library").SetValue(form, library); Apply(form);
                Require(F("filterMatchWorker").GetValue(form) == null, "closed panel matched inactive filters");
                Require(Listed(form).Count == 2700, "unfiltered library count");
                var clock = Stopwatch.StartNew();
                form.SetCustomState("nsfw", CustomFilterState.Only);
                long dispatched = clock.ElapsedMilliseconds;
                Require(F("filterMatchWorker").GetValue(form) != null, "cold large-library matching did not leave the UI thread");
                // Change the active state and indexed names before the first completion.
                form.SetCustomState("nsfw", CustomFilterState.Hide);
                library[0].OptionNames = new List<string> { "Jacket" };
                Apply(form); WaitMatches(form);
                Require(Listed(form).Count == 1801 && Listed(form).Contains("Fixture 0"), "stale name/state won background match");
                clock.Restart(); Apply(form); long warm = clock.ElapsedMilliseconds;
                form.ShowFilters(true, false); WaitMatches(form);
                Require(Get<Dictionary<string, int>>(form, "customCounts")["nsfw"] == 899, "open-panel counts incorrect");
                // Rapid drafts must publish only the newest definition.
                form.BeginCustomFilterEdit("nsfw");
                var editor = Get<FilterPanel>(form, "filterPanel").Editor;
                editor.KeywordsBox.Text = "boots"; PerformanceCall(form, "ApplyCustomDraft");
                editor.KeywordsBox.Text = "jacket"; PerformanceCall(form, "ApplyCustomDraft");
                WaitMatches(form);
                Require(Listed(form).Count == 1801, "older draft replaced latest matching rows");
                form.ShowFilters(false, false); WaitMatches(form);
                // Replace the library while a cold match is pending; no old rows may return.
                var replacement = new List<LibraryEntry> { new LibraryEntry { Name = "New library jacket", Kind = "ZIP", SourcePath = System.IO.Path.Combine(root, "new-library.zip") } };
                library[1].CustomName = "New nude name"; Apply(form);
                F("library").SetValue(form, replacement); Apply(form); WaitMatches(form);
                Require(Names(form) == "New library jacket", "previous library rows returned");
                report.Add("  2,700 entries: cold match dispatch " + dispatched + " ms; warm full ApplyFilters " + warm + " ms; stale names, state, drafts and library replacement passed.");

                form.SetCustomState("nsfw", CustomFilterState.Off);
                var plan = new CompositionPlan();
                var component = new ModComponent { Id = "main", Name = "Main files" };
                for (int i = 4999; i >= 0; i--)
                {
                    string path = "natives/file-" + i.ToString("D4") + ".mesh";
                    plan.Files[path] = new PlannedFile { Component = component, Asset = new NativeAsset { Size = i + 1 } };
                }
                plan.Overrides.Add(new FileOverride { Path = "NATIVES/FILE-0020.MESH" });
                PerformanceCall(form, "ShowDetailTab", false);
                F("plan").SetValue(form, plan);
                clock.Restart(); PerformanceCall(form, "FillFiles"); long hidden = clock.ElapsedMilliseconds;
                var files = Get<ListView>(form, "fileList");
                Require(files.VirtualMode && files.VirtualListSize == 0 && Get<Array>(form, "fileRows").Length == 0, "hidden review materialized file rows");
                clock.Restart(); PerformanceCall(form, "ShowDetailTab", true); long opened = clock.ElapsedMilliseconds;
                Require(files.VirtualListSize == 5000 && files.Items[0].Text == "file-0000.mesh" && files.Items[4999].Text == "file-4999.mesh", "virtual files count/order");
                files.EnsureVisible(4999); files.Update();
                Get<CheckBox>(form, "overridesOnly").Checked = true;
                Require(files.VirtualListSize == 1 && files.Items[0].Text == "file-0020.mesh" && files.Items[0].SubItems[1].Text == "Main files", "override filter or columns");
                PerformanceCall(form, "ShowDetailTab", false); F("plan").SetValue(form, new CompositionPlan()); PerformanceCall(form, "FillFiles");
                PerformanceCall(form, "ShowDetailTab", true);
                Require(files.VirtualListSize == 0, "old rows survived changed hidden review");
                report.Add("  5,000 files: hidden update " + hidden + " ms; virtual list opening " + opened + " ms; scrolling, sorting, overrides and plan replacement passed.");
            }
            CheckDecodedPreviewCache();
            CheckIndexBurst(root);
            LibraryFilters.Reload();
        }

        private static void CheckIndexBurst(string root)
        {
            var library = Enumerable.Range(0, 300).Select(i => new LibraryEntry { Name = "Unread " + i,
                Kind = "ZIP", SourcePath = System.IO.Path.Combine(root, "index-burst-" + i + ".zip") }).ToList();
            using (Window w = Open(root, null, new Size(1232, 640), null))
            using (var timer = new System.Windows.Forms.Timer())
            {
                MainForm form = w.Form; form.ShowFilters(false, false);
                F("library").SetValue(form, library); Apply(form);
                Type resultType = typeof(MainForm).GetNestedType("IndexResult", System.Reflection.BindingFlags.NonPublic);
                object queue = F("indexQueue").GetValue(form);
                var enqueue = queue.GetType().GetMethod("Enqueue");
                foreach (LibraryEntry entry in library)
                {
                    object result = Activator.CreateInstance(resultType, true);
                    resultType.GetField("Entry", PrivateInstance).SetValue(result, entry);
                    var found = Library.IndexCopy(entry); found.DisplayName = "Indexed " + entry.Name;
                    resultType.GetField("Found", PrivateInstance).SetValue(result, found);
                    enqueue.Invoke(queue, new[] { result });
                }
                F("indexTimer").SetValue(form, timer);
                F("indexCancel").SetValue(form, new System.Threading.CancellationTokenSource());
                F("identityTotal").SetValue(form, library.Count); F("indexWorkerDone").SetValue(form, true);
                PerformanceCall(form, "ApplyIndexResults");
                int first = (int)F("identityApplied").GetValue(form);
                Require(first > 0 && first <= 128 && form.Indexing, "index burst was not split into bounded slices");
                int slices = 1;
                while (form.Indexing && slices++ < 400) PerformanceCall(form, "ApplyIndexResults");
                Require(!form.Indexing && (int)F("identityApplied").GetValue(form) == 300, "index burst did not finish");
                Require(library.All(e => e.DisplayName == "Indexed " + e.Name) && !(bool)F("indexUiDirty").GetValue(form), "index results or final UI flush lost");
            }
        }

        private static void CheckDecodedPreviewCache()
        {
            using (var red = new Bitmap(16, 16))
            using (var blue = new Bitmap(16, 16))
            using (var large = new Bitmap(64, 64))
            using (var cache = new PreviewImageCache(2048))
            {
                using (Graphics g = Graphics.FromImage(red)) g.Clear(Color.Red);
                using (Graphics g = Graphics.FromImage(blue)) g.Clear(Color.Blue);
                cache.Add("red", red); cache.Add("blue", blue);
                Image shown; Require(cache.TryCopy("RED", out shown), "preview case-insensitive cache miss");
                using (shown)
                {
                    Require(shown.Size == red.Size && ((Bitmap)shown).GetPixel(0, 0).ToArgb() == Color.Red.ToArgb(), "decoded preview changed pixels/size");
                    cache.Add("third", red);
                    Image missing; Require(!cache.TryCopy("blue", out missing), "preview did not evict least recently used");
                    cache.Add("large", large); Require(!cache.TryCopy("large", out missing) && cache.Bytes <= 2048, "decoded budget exceeded");
                    cache.Dispose();
                    Require(((Bitmap)shown).GetPixel(0, 0).ToArgb() == Color.Red.ToArgb(), "eviction disposed displayed image");
                }
                for (int i = 0; i < 300; i++) cache.Add("empty-" + i, null);
                Require(cache.Count == 128 && cache.Bytes == 0, "missing previews grew without a bound");
                Image empty; Require(cache.TryCopy("empty-299", out empty) && empty == null, "missing preview not cached");
            }
        }
    }
}
