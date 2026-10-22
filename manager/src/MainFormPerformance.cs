using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace SlotsInstaller
{
    internal sealed partial class MainForm
    {
        private KeyValuePair<string, PlannedFile>[] fileRows = new KeyValuePair<string, PlannedFile>[0];
        private CompositionPlan fileRowsPlan;
        private bool fileRowsOverrides;

        private void ClearFileRows()
        {
            fileList.VirtualListSize = 0;
            fileRows = new KeyValuePair<string, PlannedFile>[0];
            fileRowsPlan = null;
        }

        private void RetrieveFileRow(object sender, RetrieveVirtualItemEventArgs e)
        {
            if (e.ItemIndex < 0 || e.ItemIndex >= fileRows.Length) { e.Item = new ListViewItem(""); return; }
            var pair = fileRows[e.ItemIndex];
            var row = new ListViewItem(System.IO.Path.GetFileName(pair.Key.Replace('/', System.IO.Path.DirectorySeparatorChar)));
            row.SubItems.Add(pair.Value.Component.Name);
            row.SubItems.Add(Utilities.FriendlyBytes(pair.Value.Asset.Size));
            e.Item = row;
        }

        // Only the worker touches snapshot.Search and Hits while a job is running.
        // Live entries (and their search caches) stay exclusively on the UI thread.
        private sealed class FilterSnapshot
        {
            internal LibraryEntry Original, Copy;
            internal List<string> Ids, Names;
            internal readonly Dictionary<string, bool> Hits = new Dictionary<string, bool>(StringComparer.Ordinal);
            internal FilterSnapshot(LibraryEntry e)
            {
                Original = e; Ids = e.OptionIds; Names = e.OptionNames;
                Copy = new LibraryEntry { Name = e.Name, DisplayName = e.DisplayName, CustomName = e.CustomName,
                    SourcePath = e.SourcePath, Character = e.Character,
                    OptionIds = Ids == null ? null : new List<string>(Ids),
                    OptionNames = Names == null ? null : new List<string>(Names) };
            }
            internal bool Current()
            {
                return Copy.Name == Original.Name && Copy.DisplayName == Original.DisplayName && Copy.CustomName == Original.CustomName
                    && Copy.SourcePath == Original.SourcePath && Copy.Character == Original.Character
                    && ReferenceEquals(Ids, Original.OptionIds) && ReferenceEquals(Names, Original.OptionNames);
            }
        }
        private readonly Dictionary<LibraryEntry, FilterSnapshot> filterSnapshots = new Dictionary<LibraryEntry, FilterSnapshot>();
        private object filterSnapshotSource;
        private BackgroundWorker filterMatchWorker;
        private CancellationTokenSource filterMatchCancel;
        private string filterMatchRequest;
        private bool customMatchesPending;
        private bool listAwaitsMatches;   // 1.0.11: the pending matches decide the rows (a chip on Only/Hide, a draft)

        private bool EnsureCustomMatches(List<CustomFilter> required)
        {
            var definitions = required.GroupBy(LibraryFilters.MatchKey).ToDictionary(g => g.Key,
                g => g.First().Keywords.Select(LibrarySearch.Compact).Where(k => k.Length > 0).Distinct().ToArray(), StringComparer.Ordinal);
            string request = String.Join("\0", definitions.Keys.OrderBy(k => k, StringComparer.Ordinal));
            if (filterMatchWorker != null)
            {
                if (request != filterMatchRequest || !ReferenceEquals(filterSnapshotSource, library)) filterMatchCancel.Cancel();
                customMatchesPending = definitions.Count != 0;
                return !customMatchesPending;
            }
            if (!ReferenceEquals(filterSnapshotSource, library))
            {
                filterSnapshots.Clear(); filterSnapshotSource = library;
            }
            if (definitions.Count == 0) { customMatchesPending = false; return true; }
            var work = new List<FilterSnapshot>();
            foreach (LibraryEntry entry in library)
            {
                FilterSnapshot snapshot;
                if (!filterSnapshots.TryGetValue(entry, out snapshot) || !snapshot.Current())
                    filterSnapshots[entry] = snapshot = new FilterSnapshot(entry);
                if (definitions.Keys.Any(k => !snapshot.Hits.ContainsKey(k))) work.Add(snapshot);
            }
            // Deletes can mutate the list in place; release the removed entries too.
            if (filterSnapshots.Count > library.Count)
            {
                var live = new HashSet<LibraryEntry>(library);
                foreach (var gone in filterSnapshots.Keys.Where(e => !live.Contains(e)).ToList()) filterSnapshots.Remove(gone);
            }
            if (work.Count == 0) { customMatchesPending = false; return true; }
            Action<CancellationToken> match = delegate(CancellationToken token)
            {
                foreach (FilterSnapshot snapshot in work)
                {
                    if (snapshot.Hits.Count + definitions.Count > 64) snapshot.Hits.Clear();
                    foreach (var definition in definitions)
                    {
                        token.ThrowIfCancellationRequested();
                        if (!snapshot.Hits.ContainsKey(definition.Key))
                            snapshot.Hits[definition.Key] = definition.Value.Any(k => LibrarySearch.HasCompact(snapshot.Copy, k));
                    }
                }
            };
            if (library.Count <= 128)
            {
                match(CancellationToken.None); customMatchesPending = false; return true;
            }
            customMatchesPending = true;
            var cancel = new CancellationTokenSource();
            var worker = new BackgroundWorker();
            filterMatchCancel = cancel; filterMatchWorker = worker; filterMatchRequest = request;
            worker.DoWork += delegate { try { match(cancel.Token); } catch (OperationCanceledException) { } };
            worker.RunWorkerCompleted += delegate(object sender, RunWorkerCompletedEventArgs e)
            {
                filterMatchWorker = null; filterMatchCancel = null;
                cancel.Dispose(); worker.Dispose();
                if (IsDisposed || Disposing) return;
                if (e.Error != null) { Program.Log(e.Error); customMatchesPending = false; UpdateLibraryCaption(); UpdateCustomDraftStatus(); SetPageStatus(libraryPage, "Could not match filters. Try changing the filter.", Palette.Red); return; }
                ApplyFilters();
            };
            worker.RunWorkerAsync();
            return false;
        }

        private bool CustomMatch(LibraryEntry entry, string key)
        {
            return filterSnapshots[entry].Hits[key];
        }

        private void UpdateCustomDraftStatus()
        {
            if (customEditingId == null) return;
            filterPanel.Editor.SetStatus(customMatchesPending ? "Finding matching mods…" : customDraft == null
                ? "Type keywords to list their mods." : (captionShown == 1 ? "1 mod listed" : captionShown + " mods listed"), false);
        }
    }
}
