using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace SlotsInstaller
{
    // 2.4.1: the Mod library's sort chips (Added · Last used · Installed). Installed ↓ is the default.
    internal enum LibrarySortKey { Installed, Added, LastUsed }

    // One install, switch or uninstall the Manager recorded for a library source.
    internal sealed class UsageRecord
    {
        internal string SourcePath;
        internal DateTime Utc;
    }

    // "Last used" until 2.5.0 records play times: the latest time the Manager installed, switched or uninstalled a
    // mod. The removed records (replacements removed with Uninstall, uninstalled extra outfits, removed sets) are
    // read once per folder and cached: their folders never change after they are written, so a refresh only lists
    // the containers and reads the folders it has not seen.
    internal static class LibraryUsage
    {
        private static readonly object Sync = new object();
        private static readonly Dictionary<string, List<UsageRecord>> Folders = new Dictionary<string, List<UsageRecord>>(StringComparer.OrdinalIgnoreCase);
        private static readonly Regex TimedFolder = new Regex(@"^[0-9a-f]{32}-([0-9]{14})-[0-9a-f]{6}$", RegexOptions.IgnoreCase);
        private static readonly Regex GuidFolder = new Regex(@"^[0-9a-f]{32}$", RegexOptions.IgnoreCase);
        internal static int FolderReads;   // tests: record folders read (not served from the cache)

        internal static void ClearCache() { lock (Sync) Folders.Clear(); }

        // Read-only. `storeRoots` are the replacement stores (each has its own .removed folder); `costumeRoot` is
        // SlotsLoader's costume_mods folder (its siblings hold the removed outfits and removed sets).
        internal static List<UsageRecord> ReadRemoved(string costumeRoot, IEnumerable<string> storeRoots)
        {
            var result = new List<UsageRecord>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string store in storeRoots ?? new string[0])
            {
                string root = Full(store);
                if (root == null || !seen.Add(root)) continue;
                foreach (string folder in ListFolders(Path.Combine(root, ReplaceEngine.RemovedFolderName))) result.AddRange(Cached(folder, ReadRemovedReplacement));
            }
            string costume = Full(costumeRoot), parent = costume == null ? null : Path.GetDirectoryName(costume);
            if (!String.IsNullOrEmpty(parent))
            {
                foreach (string folder in ListFolders(Path.Combine(parent, ".slotsloader-installer-trash"))) result.AddRange(Cached(folder, ReadRemovedOutfit));
                foreach (string folder in ListFolders(Path.Combine(parent, ".slotsloader-installer-removed-sets"))) result.AddRange(Cached(folder, ReadRemovedSet));
            }
            return result;
        }

        private static string Full(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) return null;
            try { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar); }
            catch (Exception ex) { if (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException || ex is System.Security.SecurityException) return null; throw; }
        }
        private static List<string> ListFolders(string container)
        {
            try { return Directory.Exists(container) ? Directory.GetDirectories(container).ToList() : new List<string>(); }
            catch (Exception ex) { if (ex is IOException || ex is UnauthorizedAccessException) return new List<string>(); throw; }
        }
        private static List<UsageRecord> Cached(string folder, Func<string, List<UsageRecord>> read)
        {
            List<UsageRecord> records;
            lock (Sync) if (Folders.TryGetValue(folder, out records)) return records;
            try { records = read(folder) ?? new List<UsageRecord>(); }
            catch (Exception ex)
            {
                if (ex is OutOfMemoryException || ex is OperationCanceledException) throw;
                return new List<UsageRecord>();   // unreadable now: not cached, tried again on the next refresh
            }
            Interlocked.Increment(ref FolderReads);
            lock (Sync) Folders[folder] = records;
            return records;
        }
        private static DateTime NameTime(string folder)
        {
            Match m = TimedFolder.Match(Path.GetFileName(folder));
            DateTime t;
            return m.Success && DateTime.TryParseExact(m.Groups[1].Value, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out t) ? t : DateTime.MinValue;
        }
        internal static DateTime ParseUtc(string text)
        {
            DateTime t;
            return !String.IsNullOrWhiteSpace(text) && DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out t) ? t.ToUniversalTime() : DateTime.MinValue;
        }
        private static T ReadJson<T>(string file, long limit) where T : class
        {
            var info = new FileInfo(file);
            if (!info.Exists || info.Length > limit) return null;
            return Json.Serializer().Deserialize<T>(File.ReadAllText(file));
        }
        private static List<UsageRecord> One(string source, DateTime utc)
        {
            var list = new List<UsageRecord>();
            if (!String.IsNullOrWhiteSpace(source) && utc > DateTime.MinValue) list.Add(new UsageRecord { SourcePath = source, Utc = utc });
            return list;
        }
        // <store>\.removed\<id>-<yyyyMMddHHmmss>-<rand>\record.json (ReplaceEngine.Uninstall).
        private static List<UsageRecord> ReadRemovedReplacement(string folder)
        {
            if (!TimedFolder.IsMatch(Path.GetFileName(folder))) return null;
            var record = ReadJson<ReplaceRemovedRecord>(Path.Combine(folder, "record.json"), 64L * 1024 * 1024);
            if (record == null || record.Mod == null) return null;
            DateTime when = ParseUtc(record.RemovedUtc); if (when == DateTime.MinValue) when = NameTime(folder);
            return One(record.Mod.SourcePath, when);
        }
        // <costume_mods parent>\.slotsloader-installer-trash\<id>-<yyyyMMddHHmmss>-<rand>\.slots-installer.json (Installer.Uninstall).
        private static List<UsageRecord> ReadRemovedOutfit(string folder)
        {
            DateTime when = NameTime(folder);
            if (when == DateTime.MinValue) return null;
            var manifest = ReadJson<InstallManifest>(Path.Combine(folder, ".slots-installer.json"), 1024 * 1024);
            return manifest == null ? null : One(manifest.SourcePath, when);
        }
        // <costume_mods parent>\.slotsloader-installer-removed-sets\<id>\set.json + payload\<storage>\.slots-installer.json.
        private static List<UsageRecord> ReadRemovedSet(string folder)
        {
            if (!GuidFolder.IsMatch(Path.GetFileName(folder))) return null;
            var record = ReadJson<OutfitSetRecord>(Path.Combine(folder, "set.json"), 64L * 1024 * 1024);
            if (record == null || record.Entries == null || record.Kind != OutfitSetKind.Removed.ToString()) return null;
            DateTime when = ParseUtc(record.CreatedUtc);
            var list = new List<UsageRecord>();
            if (when == DateTime.MinValue) return list;
            foreach (OutfitSetItem item in record.Entries)
            {
                if (item == null || !item.Managed || !item.Directory || !Regex.IsMatch(item.Storage ?? "", @"^[0-9]{6}$")) continue;
                var manifest = ReadJson<InstallManifest>(Path.Combine(folder, "payload", item.Storage, ".slots-installer.json"), 1024 * 1024);
                if (manifest != null) list.AddRange(One(manifest.SourcePath, when));
            }
            return list;
        }
    }

    // Latest recorded use per library source (ticks, UTC). Built from data already loaded at refresh time.
    internal sealed class LibraryUsageIndex
    {
        private readonly Dictionary<string, long> latest = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        internal int Count { get { return latest.Count; } }
        internal void Add(string source, DateTime utc)
        {
            if (String.IsNullOrWhiteSpace(source) || utc <= DateTime.MinValue) return;
            long t = (utc.Kind == DateTimeKind.Local ? utc.ToUniversalTime() : utc).Ticks, old;
            if (!latest.TryGetValue(source, out old) || t > old) latest[source] = t;
        }
        internal long Ticks(string source) { long t; return source != null && latest.TryGetValue(source, out t) ? t : 0; }
        internal static LibraryUsageIndex Build(IEnumerable<InstalledOutfit> outfits, IEnumerable<ReplaceMod> replacements, IEnumerable<UsageRecord> removed)
        {
            var index = new LibraryUsageIndex();
            if (outfits != null) foreach (InstalledOutfit o in outfits) if (o != null && o.Managed) index.Add(o.SourcePath, o.InstalledUtc);
            if (replacements != null) foreach (ReplaceMod m in replacements) if (m != null) index.Add(m.SourcePath, LibraryUsage.ParseUtc(m.InstalledUtc));
            if (removed != null) foreach (UsageRecord r in removed) if (r != null) index.Add(r.SourcePath, r.Utc);
            return index;
        }
    }

    internal static class LibrarySorting
    {
        internal const long Now = Int64.MaxValue;   // a mod installed now: the most recent use

        internal static string Code(LibrarySortKey key) { return key == LibrarySortKey.Added ? "added" : key == LibrarySortKey.LastUsed ? "last_used" : "installed"; }
        internal static bool TryParse(string code, out LibrarySortKey key)
        {
            switch ((code ?? "").Trim().ToLowerInvariant())
            {
                case "added": key = LibrarySortKey.Added; return true;
                case "last_used": case "last-used": case "lastused": key = LibrarySortKey.LastUsed; return true;
                case "installed": key = LibrarySortKey.Installed; return true;
            }
            key = LibrarySortKey.Installed; return false;
        }
        internal static string Label(LibrarySortKey key) { return key == LibrarySortKey.Added ? "Added" : key == LibrarySortKey.LastUsed ? "Last used" : "Installed"; }
        internal static string Direction(LibrarySortKey key, bool ascending)
        {
            if (key == LibrarySortKey.Added) return ascending ? "oldest first" : "newest first";
            if (key == LibrarySortKey.LastUsed) return ascending ? "least recent first" : "most recent first";
            return ascending ? "not installed first" : "installed first";
        }

        // The list order. Installed keeps the search relevance (rank) first in both directions; Added and Last used
        // replace it. Every tie falls back to the name (A–Z), then the path. `lastUsed` is only called for Last used
        // (0 = never used: last in ↓, first in ↑; LibrarySorting.Now = installed now).
        internal static List<LibraryEntry> Order(IList<KeyValuePair<LibraryEntry, int>> ranked, LibrarySortKey key, bool ascending, Func<LibraryEntry, bool> installed, Func<LibraryEntry, long> lastUsed)
        {
            int n = ranked.Count;
            var rows = new Row[n];
            for (int i = 0; i < n; i++)
            {
                LibraryEntry e = ranked[i].Key;
                long primary;
                if (key == LibrarySortKey.Added) primary = e.AddedUtc.Ticks;
                else if (key == LibrarySortKey.LastUsed) primary = lastUsed(e);
                else primary = installed(e) ? 1 : 0;
                rows[i] = new Row { Entry = e, Rank = key == LibrarySortKey.Installed ? ranked[i].Value : 0, Key = ascending ? primary : -primary - 1, Name = e.Name ?? "", Path = e.SourcePath ?? "" };
            }
            Array.Sort(rows, Compare);
            var result = new List<LibraryEntry>(n);
            foreach (Row r in rows) result.Add(r.Entry);
            return result;
        }
        private struct Row { internal LibraryEntry Entry; internal int Rank; internal long Key; internal string Name, Path; }
        private static int Compare(Row a, Row b)
        {
            int c = a.Rank.CompareTo(b.Rank); if (c != 0) return c;
            c = a.Key.CompareTo(b.Key); if (c != 0) return c;
            c = StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name); if (c != 0) return c;
            return StringComparer.OrdinalIgnoreCase.Compare(a.Path, b.Path);
        }
    }

    internal sealed partial class MainForm
    {
        internal const int SortChipHeight = 20;     // with Rhythm.Related below it: the 26 px strip
        private LibrarySortKey librarySort = LibrarySortKey.Installed;
        private bool librarySortAscending;
        private Panel sortStrip;
        private readonly Dictionary<LibrarySortKey, FlatButton> sortChips = new Dictionary<LibrarySortKey, FlatButton>();
        private static readonly LibrarySortKey[] SortChipOrder = { LibrarySortKey.Added, LibrarySortKey.LastUsed, LibrarySortKey.Installed };
        // Removed records read with the last refresh (background), and the Last used index built from them, the
        // installed outfits and the replacement records (rebuilt only when one of the three changes).
        private List<UsageRecord> removedUsage = new List<UsageRecord>();
        private LibraryUsageIndex usageIndex;
        private object usageOutfits, usageRows, usageRemoved;

        // Native reorder of the list's rows into the target order (UpdateLibraryRows).
        private sealed class LibraryRowOrder : System.Collections.IComparer
        {
            private readonly Dictionary<LibraryEntry, int> target;
            internal LibraryRowOrder(Dictionary<LibraryEntry, int> target) { this.target = target; }
            public int Compare(object x, object y)
            {
                int a, b;
                target.TryGetValue((LibraryEntry)((ListViewItem)x).Tag, out a); target.TryGetValue((LibraryEntry)((ListViewItem)y).Tag, out b);
                return a.CompareTo(b);
            }
        }

        internal LibrarySortKey LibrarySort { get { return librarySort; } }
        internal bool LibrarySortAscending { get { return librarySortAscending; } }

        private Panel BuildSortStrip()
        {
            var strip = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = Padding.Empty, Name = "sortStrip", AccessibleName = "Sort the mod library", AccessibleRole = AccessibleRole.ToolBar };
            foreach (LibrarySortKey key in SortChipOrder)
            {
                LibrarySortKey k = key;
                var chip = new FlatButton(LibrarySorting.Label(k), false) { Tag = k, AutoSize = false, Font = Palette.Small, Margin = Padding.Empty, Padding = new Padding(6, 0, 6, 0), Height = SortChipHeight, Name = "sort" + k };
                chip.Click += delegate { SortChipClicked(k); };
                // Arrow keys move between the chips (WinForms); W/A/S/D do the same, as on the Misc chips.
                chip.KeyDown += delegate(object sender, KeyEventArgs e)
                {
                    Keys arrow = FighterGrid.WasdArrow(e);
                    if (arrow == e.KeyCode) return;
                    strip.SelectNextControl(chip, arrow == Keys.Right || arrow == Keys.Down, true, false, true);
                    e.Handled = e.SuppressKeyPress = true;
                };
                sortChips[k] = chip; strip.Controls.Add(chip);
            }
            // 2.4.1: Refresh libraries without the keyboard, at the right end of the row: the same path as F5
            // (RunShortcut), disabled while any Work task (a scan included) runs.
            refreshLibraryButton = new FlatButton(RefreshGlyph, false) { AutoSize = false, Font = RefreshFont, Margin = Padding.Empty, Padding = Padding.Empty, Height = SortChipHeight, Name = "refreshLibraryButton", AccessibleName = "Refresh libraries", AccessibleDescription = "Shortcut: " + Shortcuts.Display(ShortcutAction.RefreshLibraries) };
            refreshLibraryButton.Click += delegate { RunShortcut(ShortcutAction.RefreshLibraries); };
            strip.Controls.Add(refreshLibraryButton);
            sortStrip = strip;
            Action layout = LayoutSortStrip;
            strip.Resize += delegate { layout(); }; dpiLayouts.Add(layout);
            UpdateSortChips();
            return strip;
        }
        internal const string RefreshGlyph = "↻";
        private static readonly Font RefreshFont = new Font("Segoe UI Symbol", 10F);
        private FlatButton refreshLibraryButton;
        private void UpdateRefreshButton() { if (refreshLibraryButton != null && refreshLibraryButton.Enabled == busy) refreshLibraryButton.Enabled = !busy; }

        // Fixed widths (the label with either arrow), so the chips never move when the arrow appears or flips. In a
        // narrow list the chips' side room and the gaps shrink before anything would overlap Refresh.
        private void LayoutSortStrip()
        {
            if (sortStrip == null) return;
            int height = Px(SortChipHeight), refreshWidth = Px(SortChipHeight + 4), available = sortStrip.ClientSize.Width - refreshWidth - Px(4);
            const TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
            // Tiers, roomiest first: [side room, gap, 1 = room for either arrow on every chip]. The first that fits is used.
            // [side room, gap, 1 = room for either arrow on every chip, 1 = the smaller font]
            int[][] tiers = { new[] { 16, Rhythm.Related, 1, 0 }, new[] { 10, 4, 1, 0 }, new[] { 10, 4, 0, 0 }, new[] { 6, 3, 0, 0 }, new[] { 6, 3, 0, 1 }, new[] { 4, 2, 0, 1 } };
            var widths = new Dictionary<LibrarySortKey, int>(); int room = 0, gap = 0;
            foreach (int[] tier in tiers)
            {
                room = Px(tier[0]); gap = Px(tier[1]); int total = 0;
                foreach (LibrarySortKey key in SortChipOrder)
                {
                    FlatButton chip = sortChips[key]; string label = LibrarySorting.Label(key);
                    Font font = tier[3] == 1 ? Palette.Tiny : Palette.Small; if (chip.Font != font) chip.Font = font;
                    int text = tier[2] == 1 ? Math.Max(TextRenderer.MeasureText(label + " ↓", chip.Font, Size.Empty, flags).Width, TextRenderer.MeasureText(label + " ↑", chip.Font, Size.Empty, flags).Width) : TextRenderer.MeasureText(chip.Text, chip.Font, Size.Empty, flags).Width;
                    widths[key] = text + room; total += text + room + gap;
                }
                if (total - gap <= available) break;
            }
            int x = 0;
            foreach (LibrarySortKey key in SortChipOrder)
            {
                FlatButton chip = sortChips[key];
                chip.Padding = Padding.Empty;   // the label is centred in the chip; the side room is in its width
                chip.SetBounds(x, 0, widths[key], height);
                x += widths[key] + gap;
            }
            if (refreshLibraryButton != null) refreshLibraryButton.SetBounds(Math.Max(x, sortStrip.ClientSize.Width - refreshWidth), 0, refreshWidth, height);
        }

        private void SortChipClicked(LibrarySortKey key)
        {
            if (key == librarySort) librarySortAscending = !librarySortAscending;
            else { librarySort = key; librarySortAscending = false; }   // a new sort starts newest / most recent / installed first
            UpdateSortChips();
            searchTimer.Stop(); ApplyFilters(); QueueViewSave();
        }

        internal void SetLibrarySort(LibrarySortKey key, bool ascending)
        {
            librarySort = key; librarySortAscending = ascending;
            UpdateSortChips(); ApplyFilters();
        }

        private void UpdateSortChips()
        {
            foreach (var kv in sortChips)
            {
                bool active = kv.Key == librarySort;
                string label = LibrarySorting.Label(kv.Key);
                string text = active ? label + (librarySortAscending ? " ↑" : " ↓") : label;
                if (kv.Value.Text != text) kv.Value.Text = text;
                if (kv.Value.Active != active) kv.Value.SetActive(active);
                // The name says what the list shows (active) or what a press shows (inactive: its first direction).
                kv.Value.AccessibleName = "Sort by " + label + ", " + LibrarySorting.Direction(kv.Key, active && librarySortAscending);
                kv.Value.AccessibleDescription = active ? "Selected. Press again for " + LibrarySorting.Direction(kv.Key, !librarySortAscending) + "." : "";
            }
            LayoutSortStrip();   // a narrow column sizes the chips by their current text
        }

        // Older view files have no sort: Installed ↓.
        private void RestoreLibrarySort(ViewPreferences saved)
        {
            LibrarySortKey key;
            bool known = LibrarySorting.TryParse(saved == null ? null : saved.LibrarySort, out key);
            librarySort = known ? key : LibrarySortKey.Installed;
            librarySortAscending = known && String.Equals(saved.LibrarySortOrder, "asc", StringComparison.OrdinalIgnoreCase);
            UpdateSortChips(); ApplyFilters();
        }

        // ApplyFilters' order for the filtered rows (no disk access: the Added time is on the entry, Last used comes
        // from the cached index).
        private List<LibraryEntry> SortLibraryRows(List<KeyValuePair<LibraryEntry, int>> ranked)
        {
            Func<LibraryEntry, bool> isInstalled = delegate(LibraryEntry e) { return CachedInstallState(e) != LibraryInstallState.None; };
            Func<LibraryEntry, long> lastUsed = null;
            if (librarySort == LibrarySortKey.LastUsed)
                lastUsed = delegate(LibraryEntry e) { DateTime? t = LibraryLastUsed(e); return t.HasValue ? t.Value.Ticks : 0; };
            return LibrarySorting.Order(ranked, librarySort, librarySortAscending, isInstalled, lastUsed);
        }

        private LibraryUsageIndex UsageIndex()
        {
            if (usageIndex == null || !Object.ReferenceEquals(usageOutfits, installed) || !Object.ReferenceEquals(usageRows, gameRows) || !Object.ReferenceEquals(usageRemoved, removedUsage))
            {
                usageOutfits = installed; usageRows = gameRows; usageRemoved = removedUsage;
                usageIndex = LibraryUsageIndex.Build(installed, gameRows.Where(r => r.Mod != null).Select(r => r.Mod), removedUsage);
            }
            return usageIndex;
        }
        // THE Last used key (UTC; null = never used). In memory only: the history index, then the install/uninstall
        // records index, rebuilt only after a refresh changed its inputs.
        internal DateTime? LibraryLastUsed(LibraryEntry entry)
        {
            if (entry == null) return null;
            // 2.4.1 history (MainFormHistory.cs): the last time it was played, else its last install or switch; in
            // memory only. Mods the history does not know yet use the install/uninstall records below.
            DateTime? used = HistoryLastUsed(entry.SourcePath);
            if (used.HasValue) return used;
            long ticks = UsageIndex().Ticks(entry.SourcePath);
            return ticks > 0 ? new DateTime(ticks, DateTimeKind.Utc) : (DateTime?)null;
        }

        // Background part of a refresh: the removed records (cached per folder, so only new ones are read).
        private List<UsageRecord> ReadRemovedUsage(string costumeRoot)
        {
            var stores = new List<string>();
            try
            {
                // 2.4.2: shared read of replace-state.json (a commit may replace it now) and the earlier store roots too.
                stores.AddRange(ReplaceEngine.KnownStoreRoots(AppConfig.DataDir, ReplaceSettings.Load()));
            }
            catch (Exception ex) { if (ex is OutOfMemoryException || ex is OperationCanceledException) throw; Program.Log(ex); }
            try { return LibraryUsage.ReadRemoved(costumeRoot, stores); }
            catch (Exception ex) { if (ex is OutOfMemoryException || ex is OperationCanceledException) throw; Program.Log(ex); return new List<UsageRecord>(); }
        }
        private void UseRemovedUsage(List<UsageRecord> records) { if (records != null) removedUsage = records; }
    }
}
