using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SlotsInstaller
{
    // 1.0.12 (the user: "instant snappy options" while browsing mods). Opening a mod is quiet: the read runs in the
    // background with busy set (nothing else starts on a half-read mod), but without the busy look. No progress bar or
    // "Reading…" text, the library list keeps taking keys and clicks, the option list stays enabled. A pick made while
    // one is read waits as the next mod (only the newest) and stops the read in flight. Archives read this session are
    // kept while their stamp is the same (packageCache), and the next RAR/7Z row in the direction of travel is read
    // ahead, so moving through the list shows the options and the preview at once.
    internal sealed partial class MainForm
    {
        private bool packageLoading;
        private string loadingPath;
        private CancellationTokenSource openCancel;
        private LibraryEntry nextOpen;
        private bool selectionAfterOpen;
        private int libraryDirection = 1;   // the last keyboard move in the list: +1 down, -1 up (what is read ahead)
        internal int PackageReads, PackageCacheHits, PrefetchReads;   // checks
        internal static bool PrefetchInChecks;   // read-ahead is off in checks unless one turns it on

        internal bool PackageLoading { get { return packageLoading; } }

        // What the library list filters, sorts and labels a row by: opening a mod relists only when this changed.
        private static string ListedIdentity(LibraryEntry e)
        {
            return e.Character + "|" + e.Detection + "|" + e.DetectionError + "|" + e.OptionCount + "|" + String.Join(",", e.CharacterCodes) + "|" + String.Join(",", e.OptionIds);
        }

        // A pick while a mod is read: the newest waits, the read in flight stops (its 7-Zip is killed). Back on the mod
        // still shown: the read stops and nothing reopens, so its ticked options stay.
        private void QueueOpen(LibraryEntry entry)
        {
            if (entry == null) return;
            if (String.Equals(entry.SourcePath, loadingPath, StringComparison.OrdinalIgnoreCase)) { nextOpen = null; keepShown = false; return; }
            bool shown = package != null && String.Equals(entry.SourcePath, package.SourcePath, StringComparison.OrdinalIgnoreCase);
            nextOpen = shown ? null : entry; keepShown = shown;
            if (openCancel != null) try { openCancel.Cancel(); } catch (ObjectDisposedException) { }
        }
        private bool keepShown;

        private void OpenWork(string path, Func<PackageResult> read, Action<PackageResult> show)
        {
            UndoOnWorkStart();   // as every Work: a new action ends the previous Undo
            var cancel = new CancellationTokenSource(); openCancel = cancel; loadingPath = path; packageLoading = true;
            busy = true;
            if (hoverTimer != null) { hoverTimer.Stop(); hoverNode = null; }
            UpdateInstallButton();
            var worker = new BackgroundWorker();
            worker.DoWork += delegate(object s, DoWorkEventArgs e)
            {
                try { using (InstallMetrics.Measure("open: read")) using (OperationContext.Begin(cancel.Token, null)) e.Result = read(); }
                catch (OperationCanceledException) { e.Cancel = true; }
            };
            RunWorkerCompletedEventHandler finished = null;
            finished = delegate(object s, RunWorkerCompletedEventArgs e)
            {
                if (OffUiThread(delegate { finished(s, e); })) return;
                PackageResult result = e.Error == null && !e.Cancelled ? (PackageResult)e.Result : null;
                if (openCancel == cancel) openCancel = null;
                cancel.Dispose(); worker.Dispose();
                packageLoading = false; loadingPath = null;
                if (IsDisposed) { if (result != null) result.Dispose(); return; }
                SetBusy(false, "Ready");
                LibraryEntry next = nextOpen; nextOpen = null; bool back = keepShown; keepShown = false;
                if (next != null) { if (result != null) result.Dispose(); LoadPackage(next); return; }   // passed over: never shown
                if (back) { if (result != null) result.Dispose(); if (package != null) RequestReview(BrowseReviewDelay); }   // the shown mod again
                else if (e.Error != null) Error(e.Error);
                else if (result != null)
                {
                    try { using (InstallMetrics.Measure("open: show")) show(result); } catch (Exception ex) { Error(ex); }
                    ReadAhead();
                }
                if (selectionAfterOpen) { selectionAfterOpen = false; BeginInvoke((Action)LibrarySelectionSettled); }
            };
            worker.RunWorkerCompleted += finished;
            worker.RunWorkerAsync();
        }

        // ---- Archives read this session
        private sealed class CachedPackage { internal string Stamp; internal ModPackage Package; internal long Bytes, Used; }
        private const int PackageCacheCount = 12;
        private const long PackageCacheBytes = 96L * 1024 * 1024;   // preloaded previews of the kept packages
        private readonly Dictionary<string, CachedPackage> packageCache = new Dictionary<string, CachedPackage>(StringComparer.OrdinalIgnoreCase);
        private readonly object packageCacheGate = new object();
        private long packageCacheClock;

        // Worker thread: the kept package, the one being read ahead (waited for), else a new read.
        private ModPackage ReadOpenPackage(string path, string stamp, string sevenZip)
        {
            Task ahead = null;
            lock (packageCacheGate)
            {
                ModPackage kept = KeptPackage(path, stamp);
                if (kept != null) { PackageCacheHits++; return kept; }
                if (readAhead != null && String.Equals(readAhead.Path, path, StringComparison.OrdinalIgnoreCase)) ahead = readAhead.Task;
            }
            if (ahead != null)
            {
                try { ahead.Wait(OperationContext.Token); } catch (AggregateException) { }
                lock (packageCacheGate) { ModPackage kept = KeptPackage(path, stamp); if (kept != null) { PackageCacheHits++; return kept; } }
            }
            Interlocked.Increment(ref PackageReads);
            ModPackage loaded = PackageReader.Load(path, sevenZip, true);
            KeepPackage(path, stamp, loaded);
            return loaded;
        }
        private ModPackage KeptPackage(string path, string stamp)
        {
            CachedPackage kept;
            if (stamp == null || !packageCache.TryGetValue(path, out kept)) return null;
            if (kept.Stamp != stamp) { packageCache.Remove(path); return null; }
            kept.Used = ++packageCacheClock;
            return kept.Package;
        }
        // Archives only: a folder mod's stamp walks every file and its read is as quick as the walk.
        private void KeepPackage(string path, string stamp, ModPackage package)
        {
            if (stamp == null || !stamp.StartsWith("file:", StringComparison.Ordinal) || package == null) return;
            lock (packageCacheGate)
            {
                packageCache[path] = new CachedPackage { Stamp = stamp, Package = package, Bytes = package.PreloadedPreviews.Values.Sum(b => (long)b.Length), Used = ++packageCacheClock };
                while (packageCache.Count > PackageCacheCount || (packageCache.Count > 1 && packageCache.Values.Sum(c => c.Bytes) > PackageCacheBytes))
                    packageCache.Remove(packageCache.OrderBy(kv => kv.Value.Used).First().Key);
            }
        }

        // ---- Reading the next RAR/7Z row ahead (7-Zip takes 0.1-2 s; a ZIP or folder reads in milliseconds anyway)
        private sealed class ReadAheadJob { internal string Path; internal Task Task; internal CancellationTokenSource Cancel; }
        private ReadAheadJob readAhead;

        private void ReadAhead()
        {
            if ((Program.Headless && !PrefetchInChecks) || package == null || busy || modList.SelectedIndices.Count != 1) return;
            int index = modList.SelectedIndices[0] + libraryDirection;
            if (index < 0 || index >= modList.Items.Count) return;
            LibraryEntry next = modList.Items[index].Tag as LibraryEntry;
            if (next == null || String.IsNullOrEmpty(next.SourcePath) || !ReadsWithSevenZip(next.SourcePath)) return;
            string path = next.SourcePath, sevenZip = config.SevenZipPath;
            lock (packageCacheGate) if (readAhead != null && String.Equals(readAhead.Path, path, StringComparison.OrdinalIgnoreCase)) return;
            StopReadAhead(false);
            var job = new ReadAheadJob { Path = path, Cancel = new CancellationTokenSource() };
            job.Task = new Task(delegate
            {
                try
                {
                    using (OperationContext.Begin(job.Cancel.Token, null))
                    {
                        string stamp = SourceStamp(path);
                        lock (packageCacheGate) if (stamp == null || KeptPackage(path, stamp) != null) return;
                        ModPackage read = PackageReader.Load(path, sevenZip, true);
                        Interlocked.Increment(ref PrefetchReads);
                        KeepPackage(path, stamp, read);
                    }
                }
                catch (Exception ex) { if (ex is OutOfMemoryException) throw; }   // only a head start: opening it reads again and reports
                finally { lock (packageCacheGate) if (readAhead == job) readAhead = null; }
            });
            lock (packageCacheGate) readAhead = job;
            job.Task.Start();
        }
        private static bool ReadsWithSevenZip(string path)
        {
            string ext = Path.GetExtension(path);
            return String.Equals(ext, ".rar", StringComparison.OrdinalIgnoreCase) || String.Equals(ext, ".7z", StringComparison.OrdinalIgnoreCase);
        }
        // wait: Work (rename, delete, install, refresh) needs the archive free, so it waits for 7-Zip to stop.
        private void StopReadAhead(bool wait) { StopReadAhead(wait, null); }
        private void StopReadAhead(bool wait, string keep)
        {
            ReadAheadJob job;
            lock (packageCacheGate)
            {
                job = readAhead;
                if (job == null || (keep != null && String.Equals(job.Path, keep, StringComparison.OrdinalIgnoreCase))) return;
                readAhead = null;
            }
            try { job.Cancel.Cancel(); } catch (ObjectDisposedException) { }
            if (wait) try { job.Task.Wait(5000); } catch (AggregateException) { }
        }
    }
}
