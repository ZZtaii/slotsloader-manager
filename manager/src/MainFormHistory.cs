using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace SlotsInstaller
{
    // 2.4.1: install/usage history in the window. The History view is a page reached from the Installed page's
    // History button and gear → History (no popup, no tooltip). Library and Installed rows add a short muted
    // "played 3d ago" / "installed 12 Oct" to their meta line when it fits. Game sessions are recorded from the
    // existing SF6 probe (PollGame), and SlotsLoader's log is read once at start-up and after each session, in the
    // background.
    internal sealed partial class MainForm
    {
        internal const int HistoryRowHeight = 46, HistoryDateWidth = 96, HistoryMaxRows = 2000;
        internal static readonly string[] HistoryKindFilters = { "All events", "Played", "Installs", "Switches", "Uninstalls", "Enabled or disabled", "Priority moves", "Undo and restores", "External mod manager" };
        private Panel historyPage;
        private ListView historyList;
        private PaddedTextField historySearch;
        private Label historySearchClear, historyEmpty;
        private ComboBox historyCharacter, historyKind;
        private FlatButton historyButton, historyBackButton;
        private HistorySessions historySessions;
        private int historyVersionSeen = -1;
        private int historyBackfillRunning;
        internal static bool HistoryAutoBackfill = true;   // tests run the backfill themselves
        internal string LastHistoryBackfill = "";          // tests: the last backfill's summary

        private HistoryStore HistoryData { get { return HistoryStore.Default; } }

        // ---- Last used and the meta line (no file access: the in-memory index)
        // 2.4.1 Last used: the last time the mod was played, else its last install or switch (null = never).
        internal DateTime? HistoryLastUsed(string sourcePath)
        {
            if (String.IsNullOrEmpty(sourcePath)) return null;
            return HistoryData.Peek().LastUsed(sourcePath);
        }
        internal string UsageMeta(string sourcePath, bool installedNow)
        {
            if (String.IsNullOrEmpty(sourcePath)) return null;
            return History.Meta(HistoryData.Peek().Find(sourcePath), installedNow, History.Now);
        }
        // The text with "   ·   meta" appended only when the whole line still fits `width`; otherwise unchanged.
        private static string WithUsage(Graphics g, string text, string meta, Font font, int width)
        {
            if (String.IsNullOrEmpty(meta)) return text;
            string joined = text + "   ·   " + meta;
            const TextFormatFlags flags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
            return TextRenderer.MeasureText(g, joined, font, new Size(Int32.MaxValue, Int32.MaxValue), flags).Width <= width ? joined : text;
        }
        // For lines drawn in another colour (Installed rows): the meta in Muted right after the text, when it fits.
        private void DrawUsageAfter(Graphics g, string text, string meta, Font font, Rectangle line)
        {
            if (String.IsNullOrEmpty(meta)) return;
            const TextFormatFlags flags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
            // The line was drawn with GDI's default left padding: continue right after its last glyph.
            int padded = TextRenderer.MeasureText(g, text, font, new Size(Int32.MaxValue, Int32.MaxValue), flags).Width;
            int bare = TextRenderer.MeasureText(g, text, font, new Size(Int32.MaxValue, Int32.MaxValue), flags | TextFormatFlags.NoPadding).Width;
            int used = bare + (padded - bare) / 2;
            string part = "   ·   " + meta;
            int need = TextRenderer.MeasureText(g, part, font, new Size(Int32.MaxValue, Int32.MaxValue), flags | TextFormatFlags.NoPadding).Width;
            if (used + need > line.Width) return;
            TextRenderer.DrawText(g, part, font, new Rectangle(line.Left + used, line.Top, need + Px(2), line.Height), Palette.Muted, flags | TextFormatFlags.NoPadding);
        }
        private string OutfitUsageMeta(InstalledOutfit o) { return o == null || !o.Managed ? null : UsageMeta(o.SourcePath, true); }
        private string GameRowUsageMeta(GameModRow r) { return r == null || r.Mod == null ? null : UsageMeta(r.Mod.SourcePath, true); }

        // ---- Sessions (called from PollGame every 1.5 s) and the background log backfill
        private void HistoryPoll(bool game)
        {
            try
            {
                if (historySessions == null) historySessions = new HistorySessions(null);
                bool stopped = historySessions.InSession && !game;
                historySessions.Poll(game, ActiveHistoryMods, HistorySessions.GameProcessStart, History.Now);
                if (stopped && HistoryAutoBackfill) StartHistoryBackfill();
                HistoryData.Peek();   // a background check of the file every 2 s (the CLI or the Assistant may have written)
                int version = HistoryData.Version;
                if (version != historyVersionSeen) { historyVersionSeen = version; HistoryChanged(); }
            }
            catch (Exception ex) { Program.Log(ex); }
        }
        // The active installed set: enabled extra outfits, enabled replacements and tool mods.
        internal List<HistoryMod> ActiveHistoryMods()
        {
            var mods = new List<HistoryMod>();
            foreach (InstalledOutfit o in installed.Where(x => x.Enabled))
            {
                string folder = Path.GetFileName(o.Folder ?? "");
                mods.Add(new HistoryMod { Source = o.Managed ? History.Normalize(o.SourcePath) : "", Name = o.Managed ? o.Name : Path.GetFileNameWithoutExtension(folder), Mode = HistoryModes.Extra, Character = o.Character ?? "", Slot = (o.Character ?? "") + "/" + (o.Managed ? o.Name : folder) });
            }
            foreach (GameModRow r in gameRows.Where(x => x.Mod != null && x.Mod.Enabled))
            {
                HistoryEvent shape = History.ForReplacement(HistoryKinds.Install, r.Mod);
                mods.Add(new HistoryMod { Source = History.Normalize(r.Mod.SourcePath), Name = shape.Name, Mode = shape.Mode, Character = shape.Character, Slot = shape.Slot });
            }
            return mods;
        }
        private void HistoryChanged()
        {
            if (IsDisposed) return;
            modList.Invalidate(); installedList.Invalidate();
            if (librarySort == LibrarySortKey.LastUsed) ApplyFilters();
            if (historyPage != null && historyPage.Visible) FillHistory();
        }
        private void StartHistory()
        {
            HistoryData.Peek();   // loads the history in the background
            if (HistoryAutoBackfill) StartHistoryBackfill();
        }
        // Reads SlotsLoader's log (read-only, from the last offset) off the UI thread.
        private void StartHistoryBackfill()
        {
            if (Interlocked.CompareExchange(ref historyBackfillRunning, 1, 0) != 0) return;
            string game = config.GameRoot, root = config.CostumeRoot, data = AppConfig.DataDir;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    List<InstalledOutfit> outfits = Directory.Exists(root) ? Installer.ListInstalled(root) : new List<InstalledOutfit>();
                    List<ReplaceMod> mods = new List<ReplaceMod>();
                    try { if (Directory.Exists(game)) mods = new ReplaceEngine(game, data, ReplaceSettings.Load()).LoadState().Mods; } catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); }
                    LoaderLogResult result = LoaderLogBackfill.Run(game, HistoryStore.DefaultDir ?? data, outfits, mods, DateTime.UtcNow);
                    LastHistoryBackfill = result.Summary;
                    if (result.Recorded > 0) Program.LogLine("History: Slotsloader log " + result.Summary + ".");
                }
                catch (Exception ex) { Program.Log(ex); }
                finally { Interlocked.Exchange(ref historyBackfillRunning, 0); }
            });
        }

        internal void RecordUndo(string label)
        {
            History.Record(null, new HistoryEvent { Kind = HistoryKinds.Undo, Name = label ?? "", Detail = "Undo: " + (label ?? "") });
        }

        // ---- Entry points: the Installed page's History button and gear → History
        // Called while the Installed page is built: History sits left of Presets….
        private void AttachHistoryButton(Panel heading)
        {
            var holder = new Panel { Dock = DockStyle.Right, Width = 50, Padding = new Padding(0, 0, 8, 10) };
            historyButton = Button("", false, 42, delegate { if (!busy) ShowHistory(); });   // 1.0.1: icon, hover "History"
            historyButton.Dock = DockStyle.Fill; historyButton.Margin = Padding.Empty; historyButton.Name = "historyButton"; historyButton.AccessibleName = "History";
            historyButton.Glyph = ButtonGlyph.History; IconHint(historyButton, "History");
            holder.Controls.Add(historyButton);
            heading.Controls.Add(holder);
            heading.Controls.SetChildIndex(holder, 1);   // docks after Presets…, so it sits to its left
            // 1.0.6 (the user): Refresh re-reads what is installed (extra outfits, game-folder mods), left of History.
            var refreshHolder = new Panel { Dock = DockStyle.Right, Width = 50, Padding = new Padding(0, 0, 8, 10) };
            refreshInstalledButton = Button("", false, 42, delegate { if (!busy) RefreshInstalled(); });   // the page status shows the fresh count
            refreshInstalledButton.Dock = DockStyle.Fill; refreshInstalledButton.Margin = Padding.Empty; refreshInstalledButton.Name = "refreshInstalledButton"; refreshInstalledButton.AccessibleName = "Refresh installed";
            refreshInstalledButton.Glyph = ButtonGlyph.Refresh; IconHint(refreshInstalledButton, "Refresh");
            refreshHolder.Controls.Add(refreshInstalledButton);
            heading.Controls.Add(refreshHolder);
            heading.Controls.SetChildIndex(refreshHolder, 1);   // docks after History, so it sits to its left
            Shown += delegate { if (!Program.Headless) StartHistory(); };
            FormClosing += delegate { if (historySessions != null) historySessions.Close(History.Now); };
        }

        internal void ShowHistory()
        {
            Panel page = EnsureHistoryPage();
            ShowPage(page);
            navInstalled.SetActive(true);
            historyVersionSeen = HistoryData.Version;
            FillHistory();
        }

        private Panel EnsureHistoryPage()
        {
            if (historyPage != null) return historyPage;
            Panel page = BuildHistoryPage();
            page.Visible = false;
            if (dpiLayoutReady && DeviceDpi != 96) page.Scale(new SizeF(DeviceDpi / 96F, DeviceDpi / 96F));
            DarkTheme.Apply(page);
            pagesPanel.Controls.Add(page);
            historyPage = page;
            LayoutHistory();
            return page;
        }

        private Panel BuildHistoryPage()
        {
            var page = new Panel { Dock = DockStyle.Fill, Name = "historyPage" };
            var heading = new Panel { Dock = DockStyle.Top, Height = 40 };
            var filters = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = Padding.Empty, Margin = Padding.Empty };
            historySearch = TextField(); historySearch.Width = 260; historySearch.AccessibleName = "Filter history by mod or character"; historySearch.PlaceholderText = "Filter by mod or character"; historySearch.Margin = new Padding(0, 0, 8, 0); historySearch.Name = "historySearch";
            historyCharacter = Combo(); historyCharacter.Width = 170; historyCharacter.Margin = new Padding(0, 0, 8, 0); historyCharacter.Items.Add("All characters"); historyCharacter.Items.AddRange(Characters.Names.Cast<object>().ToArray()); historyCharacter.SelectedIndex = 0; historyCharacter.AccessibleName = "History character filter"; historyCharacter.Name = "historyCharacter";
            historyKind = Combo(); historyKind.Width = 190; historyKind.Margin = Padding.Empty; historyKind.Items.AddRange(HistoryKindFilters.Cast<object>().ToArray()); historyKind.SelectedIndex = 0; historyKind.AccessibleName = "History event filter"; historyKind.Name = "historyKind";
            filters.Controls.Add(historySearch); filters.Controls.Add(historyCharacter); filters.Controls.Add(historyKind);
            var back = new Panel { Dock = DockStyle.Right, Width = 150, Padding = new Padding(0, 0, 0, 10) };
            historyBackButton = Button("Back to Installed", false, 150, delegate { ShowPage(installedPage); RefreshInstalled(); });
            historyBackButton.Dock = DockStyle.Fill; historyBackButton.Margin = Padding.Empty; historyBackButton.Name = "historyBackButton";
            back.Controls.Add(historyBackButton);
            heading.Controls.Add(filters); heading.Controls.Add(back); filters.BringToFront();
            historySearchClear = SearchClear.Attach(historySearch, delegate { FillHistory(); });
            historySearch.TextChanged += delegate { FillHistory(); }; historyCharacter.SelectedIndexChanged += delegate { FillHistory(); }; historyKind.SelectedIndexChanged += delegate { FillHistory(); };

            var body = new Panel { Dock = DockStyle.Fill, BackColor = Palette.Surface, Padding = new Padding(0, 4, 0, 0) };
            historyList = CreateList(HistoryRowHeight); historyList.Columns.Add("Event", 900); historyList.Name = "historyList"; historyList.AccessibleName = "History";
            ((BufferedModList)historyList).RepaintAfterScroll = true;
            historyList.DrawSubItem += DrawHistoryItem;
            historyList.Resize += delegate { ((BufferedModList)historyList).FitSingleColumn(); };
            historyEmpty = new Label { UseMnemonic = false, Dock = DockStyle.Top, Height = 40, ForeColor = Palette.Muted, Font = Palette.Small, Padding = new Padding(14, 12, 14, 0), Text = "No history yet. Installs, changes and game launches appear here.", Visible = false, Name = "historyEmpty" };
            body.Controls.Add(historyList); body.Controls.Add(historyEmpty);
            page.Controls.Add(body); page.Controls.Add(heading); body.BringToFront();
            dpiLayouts.Add(LayoutHistory);
            return page;
        }
        private void LayoutHistory()
        {
            if (historyList == null) return;
            if (historyList.SmallImageList != null) historyList.SmallImageList.ImageSize = new Size(Px(1), Px(HistoryRowHeight));
            historySearch.LayoutEditor();
            ((BufferedModList)historyList).FitSingleColumn(true);
        }

        // ---- Rows
        internal sealed class HistoryRow
        {
            internal DateTime Utc;
            internal string Kind = "", Verb = "", Title = "", Detail = "";
            internal List<string> Keys = new List<string>();
            internal List<string> Characters = new List<string>();
            internal List<HistoryMod> Mods = new List<HistoryMod>();
            internal string Mode = "";
        }
        internal static string ModeLabel(string mode)
        {
            return mode == HistoryModes.Extra ? "Extra outfit" : mode == HistoryModes.Tool ? "Tool mod" : mode == HistoryModes.Fluffy ? "External manager mod" : mode == HistoryModes.Replace ? "Replacement" : "";
        }
        private static string Verb(HistoryEvent e)
        {
            switch (e.Kind)
            {
                case HistoryKinds.Install: return "Installed";
                case HistoryKinds.Update: return "Switched";
                case HistoryKinds.Uninstall: return "Uninstalled";
                case HistoryKinds.Enable: return "Enabled";
                case HistoryKinds.Disable: return "Disabled";
                case HistoryKinds.Reorder: return "Moved";
                case HistoryKinds.Recover: return e.Via == "journal" ? "Recovered" : "Restored";
                case HistoryKinds.Undo: return "Undo";
                case HistoryKinds.FluffyUninstall: return "Uninstalled (external manager)";
                case HistoryKinds.Rename: return "Renamed";
            }
            return e.Kind;
        }
        private static string KindFilter(string kind)
        {
            switch (kind)
            {
                case HistoryKinds.Install: return "Installs";
                case HistoryKinds.Update: return "Switches";
                case HistoryKinds.Uninstall: return "Uninstalls";
                case HistoryKinds.Enable: case HistoryKinds.Disable: return "Enabled or disabled";
                case HistoryKinds.Reorder: return "Priority moves";
                case HistoryKinds.Recover: case HistoryKinds.Undo: return "Undo and restores";
                case HistoryKinds.FluffyUninstall: return "External mod manager";
                case HistoryKinds.Rename: return "Renames";
                case "play": return "Played";
            }
            return "";
        }

        // Newest first. Probe sessions and SlotsLoader runs of one launch are one "Played" row.
        internal static List<HistoryRow> HistoryRows(List<HistoryEvent> events, HistoryIndex index, DateTime nowUtc)
        {
            var rows = new List<HistoryRow>();
            foreach (HistoryEvent e in events)
            {
                if (HistoryKinds.IsPlay(e.Kind) || e.Kind == HistoryKinds.SessionStop || e.Kind == HistoryKinds.Summary) continue;
                var row = new HistoryRow { Utc = e.Utc, Kind = e.Kind, Verb = Verb(e), Mode = e.Mode };
                string name = String.IsNullOrEmpty(e.Name) ? e.Slot : e.Name;
                row.Title = e.Kind == HistoryKinds.Undo ? (e.Name ?? "") : name;
                var parts = new List<string>();
                if (e.Kind == HistoryKinds.Reorder && !String.IsNullOrEmpty(e.Detail)) { row.Verb = "Moved " + e.Detail; }
                string mode = ModeLabel(e.Mode); if (mode.Length > 0) parts.Add(mode);
                if (!String.IsNullOrEmpty(e.Character)) parts.Add(e.Character);
                if (e.Mode == HistoryModes.Replace && !String.IsNullOrEmpty(e.Slot) && e.Slot != name) parts.Add(e.Slot);
                if (!String.IsNullOrEmpty(e.Mod) && e.Mod != name) parts.Add("from " + e.Mod);
                if (e.Options != null && e.Kind != HistoryKinds.Uninstall) parts.Add(ReplacePlanner.N(e.Options.Count, "option"));
                if (e.Kind == HistoryKinds.Update && e.PrevOptions != null && e.Options != null && !e.PrevOptions.SequenceEqual(e.Options, StringComparer.OrdinalIgnoreCase)) parts.Add("options changed");
                if (e.Kind != HistoryKinds.Reorder && e.Kind != HistoryKinds.Undo && !String.IsNullOrEmpty(e.Detail)) parts.Add(e.Detail);
                row.Detail = String.Join(" · ", parts);
                string key = HistoryIndex.Key(e); if (key.Length > 0) row.Keys.Add(key);
                if (!String.IsNullOrEmpty(e.Character)) row.Characters.AddRange(e.Character.Split(new[] { ", " }, StringSplitOptions.RemoveEmptyEntries));
                if (key.Length > 0) row.Mods.Add(new HistoryMod { Source = e.Source, Name = name, Mode = e.Mode, Character = e.Character, Slot = e.Slot });
                rows.Add(row);
            }
            var stops = events.Where(e => e.Kind == HistoryKinds.SessionStop && e.Start.HasValue).ToList();
            foreach (HistoryPlay play in index.Plays)
            {
                var row = new HistoryRow { Utc = play.Utc, Kind = "play", Verb = "Played SF6" };
                var mods = play.Events.Where(e => e.Mods != null).SelectMany(e => e.Mods).GroupBy(m => HistoryIndex.Key(m), StringComparer.OrdinalIgnoreCase).Where(g => g.Key.Length > 0).Select(g => g.First()).ToList();
                row.Mods = mods; row.Keys = mods.Select(HistoryIndex.Key).ToList();
                row.Characters = mods.Where(m => !String.IsNullOrEmpty(m.Character)).SelectMany(m => m.Character.Split(new[] { ", " }, StringSplitOptions.RemoveEmptyEntries)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                int outfits = mods.Count(m => m.Mode == HistoryModes.Extra), others = mods.Count - outfits;
                var counts = new List<string>();
                if (outfits > 0) counts.Add(ReplacePlanner.N(outfits, "extra outfit"));
                if (others > 0) counts.Add(ReplacePlanner.N(others, "replacement or tool mod").Replace("replacement or tool mods", "replacements and tool mods"));
                row.Title = counts.Count == 0 ? "no mods active" : String.Join(", ", counts);
                var parts = new List<string>();
                HistoryEvent stop = play.Probe ? stops.Where(s => Math.Abs((s.Start.Value - play.Utc).TotalMinutes) <= 3).OrderBy(s => s.Utc).FirstOrDefault() : null;
                if (stop != null && !String.IsNullOrEmpty(stop.Detail) && stop.Via == "probe") parts.Add(stop.Detail);
                if (play.Slots > 0) parts.Add(ReplacePlanner.N(play.Slots, "slot") + " built by Slotsloader");
                string names = String.Join(", ", mods.Select(m => m.Name).Where(n => !String.IsNullOrEmpty(n)).Take(6));
                if (names.Length > 0) parts.Add(names + (mods.Count > 6 ? ", …" : ""));
                row.Detail = String.Join(" · ", parts);
                rows.Add(row);
            }
            return rows.OrderByDescending(r => r.Utc).ToList();
        }

        private static bool CharacterMatches(HistoryRow row, string who) { return row.Characters.Any(c => String.Equals(c, who, StringComparison.OrdinalIgnoreCase)); }

        // The filtered timeline (also used by tests).
        internal List<HistoryRow> FilteredHistory()
        {
            HistoryIndex index = HistoryData.Index();
            List<HistoryRow> rows = HistoryRows(HistoryData.Events(), index, History.Now);
            string kind = historyKind == null ? "" : Convert.ToString(historyKind.SelectedItem);
            if (kind.Length > 0 && kind != HistoryKindFilters[0]) rows = rows.Where(r => KindFilter(r.Kind) == kind).ToList();
            if (historyCharacter != null && historyCharacter.SelectedIndex > 0) { string who = Convert.ToString(historyCharacter.SelectedItem); rows = rows.Where(r => CharacterMatches(r, who)).ToList(); }
            string text = historySearch == null ? "" : historySearch.Text.Trim();
            if (text.Length > 0)
            {
                // The Installed page's matching (LibrarySearch: "aki" = A.K.I, "cam", "cammy, c1", is:tool), per mod.
                var q = new InstalledQuery { Empty = false, Query = LibrarySearch.Parse(text, SearchCharacterTerm) };
                q.Codes = LibrarySearch.CodesFor(q.Query.Characters);
                q.Library = new Dictionary<string, LibraryEntry>(StringComparer.OrdinalIgnoreCase);
                foreach (LibraryEntry e in library) if (!String.IsNullOrEmpty(e.SourcePath) && !q.Library.ContainsKey(e.SourcePath)) q.Library[e.SourcePath] = e;
                var cache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                Func<HistoryMod, bool> matches = delegate(HistoryMod m)
                {
                    string key = HistoryIndex.Key(m) + "|" + m.Name; bool hit;
                    if (!cache.TryGetValue(key, out hit)) { hit = InstalledRowMatches(q, m.Name, m.Character, m.Source, null, m.Mode == HistoryModes.Tool) || (m.Name ?? "").IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0; cache[key] = hit; }
                    return hit;
                };
                rows = rows.Where(r => r.Mods.Any(matches) || (r.Kind == HistoryKinds.Undo && r.Title.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
            }
            return rows;
        }

        internal void FillHistory()
        {
            if (historyList == null) return;
            List<HistoryRow> rows = FilteredHistory();
            historyList.BeginUpdate();
            try
            {
                historyList.Items.Clear();
                foreach (HistoryRow row in rows.Take(HistoryMaxRows)) historyList.Items.Add(new ListViewItem(row.Verb + " " + row.Title) { Tag = row });
            }
            finally { historyList.EndUpdate(); }
            ((BufferedModList)historyList).FitSingleColumn(true);
            bool filtered = historySearch.TextLength > 0 || historyCharacter.SelectedIndex > 0 || historyKind.SelectedIndex > 0;
            historyEmpty.Text = filtered ? "Nothing matches these filters." : "No history yet. Installs, changes and game launches appear here.";
            historyEmpty.Visible = rows.Count == 0;
            HistoryIndex index = HistoryData.Index();
            string since = index.Since.HasValue ? " · since " + History.Date(index.Since.Value, History.Now) : "";
            string shown = rows.Count > HistoryMaxRows ? "newest " + HistoryMaxRows + " of " + ReplacePlanner.N(rows.Count, "event") : ReplacePlanner.N(rows.Count, "event");
            SetPageStatus(historyPage, shown + " · " + ReplacePlanner.N(index.Launches, "launch").Replace("launchs", "launches") + " recorded" + since);
        }

        private void DrawHistoryItem(object sender, DrawListViewSubItemEventArgs e)
        {
            HistoryRow row = e.Item.Tag as HistoryRow; if (row == null) return;
            Rectangle r = e.Bounds;
            using (Brush bg = new SolidBrush(e.Item.Selected ? Palette.Selected : Palette.Surface)) e.Graphics.FillRectangle(bg, r);
            DateTime local = row.Utc.ToLocalTime(), today = History.Now.ToLocalTime().Date;
            string day = local.Date == today ? "Today" : local.Date == today.AddDays(-1) ? "Yesterday" : History.Date(row.Utc, History.Now);
            const TextFormatFlags one = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;
            TextRenderer.DrawText(e.Graphics, day, Palette.Small, new Rectangle(r.Left + Px(14), r.Top + Px(5), Px(HistoryDateWidth - 18), Px(20)), Palette.Muted, one);
            TextRenderer.DrawText(e.Graphics, local.ToString("HH:mm", CultureInfo.InvariantCulture), Palette.Tiny, new Rectangle(r.Left + Px(14), r.Top + Px(24), Px(HistoryDateWidth - 18), Px(18)), Palette.Muted, one);
            int x = r.Left + Px(HistoryDateWidth), width = Math.Max(1, r.Right - x - Px(12));
            Color verbColor = row.Kind == "play" ? Palette.Green : row.Kind == HistoryKinds.Uninstall || row.Kind == HistoryKinds.FluffyUninstall ? Palette.Muted : Palette.Accent;
            int verbWidth = TextRenderer.MeasureText(e.Graphics, row.Verb, Palette.Strong, new Size(Int32.MaxValue, Int32.MaxValue), TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine).Width;
            verbWidth = Math.Min(verbWidth, width / 2);
            TextRenderer.DrawText(e.Graphics, row.Verb, Palette.Strong, new Rectangle(x, r.Top + Px(4), verbWidth, Px(22)), verbColor, one);
            TextRenderer.DrawText(e.Graphics, row.Title, Palette.Normal, new Rectangle(x + verbWidth + Px(4), r.Top + Px(4), Math.Max(1, width - verbWidth - Px(4)), Px(22)), Palette.Text, one);
            TextRenderer.DrawText(e.Graphics, row.Detail, Palette.Tiny, new Rectangle(x, r.Top + Px(25), width, Px(18)), Palette.Muted, one);
            using (Pen line = new Pen(Palette.Background, Math.Max(1F, DeviceDpi / 96F))) e.Graphics.DrawLine(line, r.Left + Px(12), r.Bottom - 1, r.Right - Px(12), r.Bottom - 1);
        }
    }
}
