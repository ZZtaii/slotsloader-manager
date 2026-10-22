using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace SlotsInstaller
{
    internal static partial class ReplaceUiChecks
    {
        // 1.0.12 (the user: instant options while browsing). Opening a mod is quiet (no progress bar or status text, the list
        // and options stay usable), the newest pick wins and stops the read in flight, the shown mod picked again keeps its
        // ticks, the mod being read picked again after another pick is still opened, a Ctrl/Shift selection changed during a
        // read decides what opens, archives read this session are kept until they change, the health check waits for
        // browsing to settle (StartReviewNow starts it at once) and the next RAR/7Z row is read ahead.
        private static void Browse(MainForm form, ReplaceChecks.Fixture f, string root)
        {
            Call(form, "SetInstallMode", InstallModes.Replace, true); ShowLibrary(form);
            string one = f.Mod("zz-browse-1.zip"), two = f.Mod("zz-browse-2.zip"), three = f.Mod("zz-browse-3.zip"), ahead = f.Mod("zz-browse-4.7z");
            foreach (string zip in new[] { one, two, three }) { if (File.Exists(zip)) File.Delete(zip); ZipFile.CreateFromDirectory(f.Mod("modA"), zip); }
            string sevenZip = SevenZipTool.Resolve("");   // the read-ahead part needs a 7Z, so 7-Zip
            if (File.Exists(ahead)) File.Delete(ahead);
            if (sevenZip != null) ProcessUtil.RunText(sevenZip, "a -t7z -y " + ProcessUtil.Quote(ahead) + " " + ProcessUtil.Quote(Path.Combine(f.Mod("modA"), "*")));
            Func<string, LibraryEntry> entry = delegate(string path) { return new LibraryEntry { SourcePath = path, Name = Path.GetFileNameWithoutExtension(path) }; };
            Func<string> shown = delegate { ModPackage p = Get<ModPackage>(form, "package"); return p == null ? null : p.SourcePath; };
            Action<string> waitShown = delegate(string path)
            {
                DateTime until = DateTime.UtcNow.AddSeconds(30);
                while ((Get<bool>(form, "busy") || !String.Equals(shown(), path, StringComparison.OrdinalIgnoreCase)) && DateTime.UtcNow < until) { Application.DoEvents(); Thread.Sleep(5); }
                Require(String.Equals(shown(), path, StringComparison.OrdinalIgnoreCase) && !Get<bool>(form, "busy"), "not shown: " + Path.GetFileName(path) + " (shown " + Path.GetFileName(shown() ?? "") + ")");
            };
            var gate = new ManualResetEventSlim(true);
            var search = Get<PaddedTextField>(form, "search"); string text = search.Text;
            ListView list = Get<ListView>(form, "modList");
            try
            {
                // The fixture rows alone in the library list (the selection part needs real rows).
                Call(form, "RefreshLibrary", false); WaitIdle(form);
                search.Text = "zz-browse"; Call(form, "ApplyFilters"); Application.DoEvents();
                List<LibraryEntry> rows = list.Items.Cast<ListViewItem>().Select(i => (LibraryEntry)i.Tag).ToList();
                Func<string, LibraryEntry> row = delegate(string path) { return rows.Single(r => String.Equals(r.SourcePath, path, StringComparison.OrdinalIgnoreCase)); };
                Require(rows.Count == (sevenZip == null ? 3 : 4), "fixture rows: " + String.Join(", ", rows.Select(r => Path.GetFileName(r.SourcePath))));
                MainForm.BeforeSourceStamp = delegate(string path) { if (String.Equals(path, one, StringComparison.OrdinalIgnoreCase)) gate.Wait(15000); };

                Call(form, "LoadPackage", entry(three)); waitShown(three); WaitReview(form);
                string status = Get<Label>(form, "statusText").Text;
                // Quiet: busy for other work, but no busy look and the list, options and status keep what they had.
                gate.Reset(); Call(form, "LoadPackage", entry(one)); Application.DoEvents();
                Require(Get<bool>(form, "busy") && form.PackageLoading, "the read did not start");
                Require(!Get<ProgressBar>(form, "statusProgress").Visible && Get<Label>(form, "statusText").Text == status && !((BufferedModList)list).InputLocked && Get<OptionTree>(form, "componentTree").Enabled,
                    "opening a mod showed the busy look: progress " + Get<ProgressBar>(form, "statusProgress").Visible + ", status '" + Get<Label>(form, "statusText").Text + "'");
                // The newest pick wins; the read in flight stops before it reads anything (three is the shown mod: picked
                // and left again, it changes nothing).
                int reads = form.PackageReads;
                Call(form, "LoadPackage", entry(two)); Call(form, "LoadPackage", entry(three)); Call(form, "LoadPackage", entry(two));
                gate.Set(); waitShown(two);
                Require(form.PackageReads == reads + 1, "not only the newest pick was read: reads +" + (form.PackageReads - reads));
                // The shown mod picked again during a read: the read stops and nothing reopens (its ticks stay).
                List<string> ticks = Get<List<string>>(form, "selectedOrder").ToList(); ModPackage before = Get<ModPackage>(form, "package");
                gate.Reset(); Call(form, "LoadPackage", entry(one)); Application.DoEvents();
                Call(form, "LoadPackage", entry(two)); gate.Set();
                DateTime settle = DateTime.UtcNow.AddSeconds(10); while (form.PackageLoading && DateTime.UtcNow < settle) { Application.DoEvents(); Thread.Sleep(5); }
                Require(Object.ReferenceEquals(Get<ModPackage>(form, "package"), before) && Get<List<string>>(form, "selectedOrder").SequenceEqual(ticks), "the shown mod was reopened");
                // PR #16 review: the mod being read, picked again after another pick stopped its read, still opens (the shown
                // mod used to stay active, with Install enabled for it).
                gate.Reset(); Call(form, "LoadPackage", entry(one)); Application.DoEvents();
                Call(form, "LoadPackage", entry(three)); Call(form, "LoadPackage", entry(one));
                gate.Set(); waitShown(one); WaitReview(form);
                Require(String.Equals(shown(), one, StringComparison.OrdinalIgnoreCase), "back on the stopped read: " + Path.GetFileName(shown() ?? ""));
                // PR #16 review: Ctrl/Shift changes during a read decide what opens. Two rows then one: that one opens, the read
                // mod is not selected again and the queue stays closed.
                Call(form, "LoadPackage", entry(three)); waitShown(three); WaitReview(form);
                gate.Reset(); Call(form, "LoadPackage", entry(one)); Application.DoEvents();
                form.SelectLibraryEntries(new[] { row(one), row(two) }); form.SelectLibraryEntries(new[] { row(two) });
                gate.Set(); waitShown(two);
                List<string> selected = list.SelectedItems.Cast<ListViewItem>().Select(i => ((LibraryEntry)i.Tag).SourcePath).ToList();
                Require(!form.BatchVisible && selected.Count == 1 && String.Equals(selected[0], two, StringComparison.OrdinalIgnoreCase), "after a selection change during a read: queue " + form.BatchVisible + ", selected " + String.Join(", ", selected.Select(Path.GetFileName)));
                // Two rows left selected: the queue opens with them.
                gate.Reset(); Call(form, "LoadPackage", entry(one)); Application.DoEvents();
                form.SelectLibraryEntries(new[] { row(two), row(three) });
                gate.Set();
                settle = DateTime.UtcNow.AddSeconds(10); while ((form.PackageLoading || !form.BatchVisible) && DateTime.UtcNow < settle) { Application.DoEvents(); Thread.Sleep(5); }
                Require(form.BatchVisible && list.SelectedItems.Count == 2 && !String.Equals(shown(), one, StringComparison.OrdinalIgnoreCase), "two rows picked during a read: queue " + form.BatchVisible + ", shown " + Path.GetFileName(shown() ?? ""));
                form.SelectLibraryEntries(new[] { row(two) }); waitShown(two);
                MainForm.BeforeSourceStamp = null;
                WaitReview(form);

                // Kept while unchanged; an edited archive is read again.
                reads = form.PackageReads;
                Call(form, "LoadPackage", entry(one)); waitShown(one);
                Call(form, "LoadPackage", entry(two)); waitShown(two);
                Require(form.PackageReads == reads, "a kept archive was read again: reads +" + (form.PackageReads - reads));
                File.SetLastWriteTimeUtc(one, DateTime.UtcNow.AddMinutes(1));
                Call(form, "LoadPackage", entry(one)); waitShown(one);
                Require(form.PackageReads == reads + 1, "an edited archive came from the kept copy");
                // Footer: only the result. The check waits for browsing to settle; pointing at Install starts it at once.
                Require(form.ReviewPending && !form.ReviewRunning && Summary(form) == "" && Details(form) == "" && !Get<FlatButton>(form, "installButton").Enabled, "after opening: " + Summary(form) + " / " + Details(form));
                DateTime quiet = DateTime.UtcNow.AddMilliseconds(MainForm.BrowseReviewDelay / 2); while (DateTime.UtcNow < quiet) { Application.DoEvents(); Thread.Sleep(5); }
                Require(!form.ReviewRunning && form.ReviewPending, "the health check did not wait for browsing to settle");
                form.StartReviewNow();
                Require(form.ReviewRunning || !form.ReviewPending, "StartReviewNow did not start the check");
                WaitReview(form);
                Require(Summary(form).StartsWith("Ready to install"), "result: " + Summary(form));

                // Read ahead: the next RAR/7Z row in the direction of travel is read while the current mod shows.
                if (sevenZip == null) return;   // no 7-Zip on this machine
                MainForm.PrefetchInChecks = true;
                List<string> paths = list.Items.Cast<ListViewItem>().Select(i => ((LibraryEntry)i.Tag).SourcePath).ToList();
                int to = paths.FindIndex(p => String.Equals(p, ahead, StringComparison.OrdinalIgnoreCase));
                // Stand on a zip next to the 7Z (not the shown one), moving towards it.
                int start = to > 0 ? to - 1 : to + 1;
                if (String.Equals(paths[start], shown(), StringComparison.OrdinalIgnoreCase)) { string other = paths.First(p => p != paths[start] && p != ahead); Call(form, "LoadPackage", entry(other)); waitShown(other); }
                int prefetched = form.PrefetchReads;
                Set(form, "libraryDirection", to > start ? 1 : -1);
                foreach (ListViewItem item in list.SelectedItems.Cast<ListViewItem>().ToList()) item.Selected = false;
                list.Items[start].Selected = true; list.Items[start].Focused = true;
                waitShown(paths[start]);
                DateTime until = DateTime.UtcNow.AddSeconds(15); while (form.PrefetchReads == prefetched && DateTime.UtcNow < until) { Application.DoEvents(); Thread.Sleep(10); }
                Require(form.PrefetchReads == prefetched + 1, "the next 7Z row was not read ahead");
                int hits = form.PackageCacheHits; reads = form.PackageReads;
                Call(form, "MoveLibrarySelection", to > start ? 1 : -1); waitShown(ahead);
                Require(form.PackageCacheHits == hits + 1 && form.PackageReads == reads, "the 7Z read ahead was read again on opening");
                WaitReview(form);
            }
            finally
            {
                MainForm.BeforeSourceStamp = null; MainForm.PrefetchInChecks = false; gate.Set();
                WaitReview(form);
                if (form.BatchVisible) Call(form, "HideBatch");
                search.Text = text; Call(form, "ApplyFilters");
                foreach (string file in new[] { one, two, three, ahead }) try { if (File.Exists(file)) File.Delete(file); } catch (IOException) { }
                Call(form, "RefreshLibrary", false); WaitIdle(form);
            }
        }
    }
}
