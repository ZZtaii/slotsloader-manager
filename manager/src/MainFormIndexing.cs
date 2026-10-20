using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SlotsInstaller
{
    // 1.0.6 (the user): a first start (or the first start after an update) no longer waits for every archive. Library.ScanQuick
    // lists the library with what the index knows; the rest is read here on pool threads (four at a time, rows on screen
    // first) while the window stays usable, in two passes:
    //   1. identity: the archive listing (fighter, outfits, option folders, kinds), what filters and costume search need;
    //   2. names: the modinfo.ini files (display name, option names, authors), for the Name view and deep search. ZIPs and
    //      folders read them in pass 1 (cheap); RAR/7Z archives, where they cost a second 7-Zip run, wait for pass 2.
    // Finished mods are applied on the UI thread every 0.4 s and the filters and counts follow. The list caption shows
    // "· READING n%" then "· NAMES n%". A refresh, a new scan or closing the window stops the run; what was read is in the
    // index already.
    internal sealed partial class MainForm
    {
        private enum IndexPass { Identity, Names }
        private sealed class IndexResult { internal LibraryEntry Entry, Found; internal IndexPass Pass; }
        private CancellationTokenSource indexCancel;
        private ConcurrentQueue<IndexResult> indexQueue = new ConcurrentQueue<IndexResult>();
        private System.Windows.Forms.Timer indexTimer;
        private int identityTotal, identityApplied, namesTotal, namesApplied;
        private bool indexWorkerDone;
        private bool indexUiDirty;
        private DateTime indexLastUiUpdate;
        internal bool Indexing { get { return indexCancel != null; } }
        internal int IndexRuns;   // checks: background runs started

        private void StartIndexing(List<LibraryEntry> pending, List<LibraryEntry> namesPending)
        {
            StopIndexing();
            pending = pending ?? new List<LibraryEntry>(); namesPending = namesPending ?? new List<LibraryEntry>();
            if (pending.Count + namesPending.Count == 0 || IsDisposed) return;
            // The rows the user sees first, then the rest in list order.
            var order = new Dictionary<LibraryEntry, int>();
            for (int i = 0; i < modList.Items.Count; i++) { var e = modList.Items[i].Tag as LibraryEntry; if (e != null && !order.ContainsKey(e)) order[e] = i; }
            int top = modList.TopItem == null ? 0 : modList.TopItem.Index;
            Func<LibraryEntry, int> rank = delegate(LibraryEntry e) { int i; return order.TryGetValue(e, out i) ? (i >= top ? i - top : Int32.MaxValue / 2 + i) : Int32.MaxValue; };
            List<LibraryEntry> identityWork = pending.OrderBy(rank).ToList();
            var namesLater = new ConcurrentBag<LibraryEntry>(namesPending);
            var cancel = new CancellationTokenSource(); var queue = new ConcurrentQueue<IndexResult>();
            indexCancel = cancel; indexQueue = queue; indexWorkerDone = false; IndexRuns++;
            identityTotal = identityWork.Count; identityApplied = 0; namesTotal = namesPending.Count; namesApplied = 0;
            indexUiDirty = false; indexLastUiUpdate = DateTime.MinValue;
            if (indexTimer == null)
            {
                indexTimer = new System.Windows.Forms.Timer { Interval = 400 }; indexTimer.Tick += delegate { ApplyIndexResults(); };
                FormClosing += delegate { StopIndexing(); try { Library.SaveIndex(); } catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); } };
                Disposed += delegate { indexTimer.Dispose(); };
            }
            indexTimer.Interval = 400; indexTimer.Start();
            UpdateLibraryCaption();
            string sevenZip = config.SevenZipPath;
            var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Math.Min(4, Environment.ProcessorCount)), CancellationToken = cancel.Token };
            ThreadPool.QueueUserWorkItem(delegate
            {
                int read = 0;
                Action saveNow = delegate { if (Interlocked.Increment(ref read) % 200 == 0) { try { Library.SaveIndex(); } catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); } } };
                try
                {
                    Parallel.ForEach(identityWork, options, delegate(LibraryEntry entry)
                    {
                        LibraryEntry copy = Library.IndexCopy(entry);
                        using (OperationContext.Begin(cancel.Token, null)) Library.IndexEntry(copy, sevenZip);
                        if (copy.NamesPending) { namesLater.Add(entry); Interlocked.Increment(ref namesTotal); }
                        queue.Enqueue(new IndexResult { Entry = entry, Found = copy, Pass = IndexPass.Identity });
                        saveNow();
                    });
                    List<LibraryEntry> namesWork = namesLater.Distinct().OrderBy(rank).ToList();
                    Parallel.ForEach(namesWork, options, delegate(LibraryEntry entry)
                    {
                        LibraryEntry copy = Library.IndexCopy(entry);
                        using (OperationContext.Begin(cancel.Token, null)) Library.IndexNames(copy, sevenZip);
                        queue.Enqueue(new IndexResult { Entry = entry, Found = copy, Pass = IndexPass.Names });
                        saveNow();
                    });
                }
                catch (OperationCanceledException) { }
                catch (AggregateException ex) { if (!ex.InnerExceptions.All(x => x is OperationCanceledException)) Program.Log(ex); }
                catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); }
                try { Library.SaveIndex(); } catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); }
                PostToUi(delegate { if (indexCancel == cancel) { indexWorkerDone = true; ApplyIndexResults(); } });
            });
        }

        // Stops a run (refresh, a new scan, closing). Mods already read stay in the index.
        private void StopIndexing()
        {
            CancellationTokenSource cancel = indexCancel; indexCancel = null;
            if (indexTimer != null) indexTimer.Stop();
            indexQueue = new ConcurrentQueue<IndexResult>();
            if (cancel != null) { try { cancel.Cancel(); } catch (ObjectDisposedException) { } }
            UpdateLibraryCaption();
        }

        // UI thread: the mods read since the last tick take what was found; the list, filters and counts follow.
        private void ApplyIndexResults()
        {
            if (indexCancel == null || IsDisposed) return;
            // A refresh stops the run before it replaces the list, so every result belongs to the listed mods. A delete only
            // drops rows (the result then changes nothing on screen); a rename keeps the file's content, so its result holds.
            bool sameList = true;
            int applied = 0; IndexResult r;
            int previousIdentity = identityApplied;
            var slice = System.Diagnostics.Stopwatch.StartNew();
            while (applied < 128 && slice.ElapsedMilliseconds < 8 && indexQueue.TryDequeue(out r))
            {
                if (r.Pass == IndexPass.Identity) identityApplied++; else namesApplied++;
                if (r.Pass == IndexPass.Identity) { Library.TakeIdentity(r.Entry, r.Found); installStates.Remove(r.Entry); libraryRanks.Remove(r.Entry); }
                else Library.TakeNames(r.Entry, r.Found);
                ListViewItem row;
                if (libraryRows.TryGetValue(r.Entry, out row)) { string shown = LibraryRowName(r.Entry); if (row.Text != shown) row.Text = shown; }
                applied++;
            }
            bool finished = indexWorkerDone && indexQueue.IsEmpty;
            if (applied > 0 && sameList)
            {
                indexUiDirty = true;
                if (finished || previousIdentity < identityTotal && identityApplied >= identityTotal) facetRowsSource = null;
            }
            // Drain bursts promptly in small slices; refresh the full list at most
            // every 400 ms (the old timer's pace), plus the final flush.
            indexTimer.Interval = indexQueue.IsEmpty ? 400 : 16;
            if (indexUiDirty && (finished || (DateTime.UtcNow - indexLastUiUpdate).TotalMilliseconds >= 400))
            {
                indexUiDirty = false; indexLastUiUpdate = DateTime.UtcNow;
                ApplyFilters(); modList.Invalidate();
            }
            if (finished)
            {
                CancellationTokenSource done = indexCancel; indexCancel = null; indexTimer.Stop();
                try { done.Dispose(); } catch (ObjectDisposedException) { }
                if (sameList && assistantPage != null) assistantPage.LibraryChanged();
                Program.LogLine("Library index: read " + identityApplied + " mods and the names of " + namesApplied + " in the background.");
            }
            UpdateLibraryCaption();
        }

        // "2,554 MODS · READING 37%" (pass 1), then "· NAMES 60%" (pass 2).
        private int captionShown = -1;
        private void UpdateLibraryCaption()
        {
            if (libraryCount == null) return;
            int shown = captionShown < 0 ? library.Count : captionShown;
            string text = library.Count == 0 ? "MOD LIBRARY" : shown == library.Count ? library.Count + " MODS" : shown + " OF " + library.Count + " MODS";
            if (customMatchesPending) text += " · FILTERING…";
            if (Indexing)
            {
                if (identityApplied < identityTotal) text += " · READING " + Math.Min(99, identityApplied * 100 / Math.Max(1, identityTotal)) + "%";
                else if (namesTotal > 0) text += " · NAMES " + Math.Min(99, namesApplied * 100 / Math.Max(1, namesTotal)) + "%";
            }
            if (libraryCount.Text != text) libraryCount.Text = text;
            libraryCount.AccessibleDescription = !Indexing ? "" : identityApplied < identityTotal ? "Reading " + (identityTotal - identityApplied) + " more mods in the background" : "Reading the names of " + Math.Max(0, namesTotal - namesApplied) + " more mods in the background";
        }
    }
}
