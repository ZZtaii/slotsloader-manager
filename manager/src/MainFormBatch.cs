using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace SlotsInstaller
{
    // 1.0.6: the options a queued mod installs with, chosen without asking.
    internal static class BatchDefaults
    {
        // The mod's default options (Composer.DefaultSelection), made installable: top-level options that share files are
        // alternatives, so only the first one in menu order stays (the "· Default" one first); each required choose-one
        // group gets its default choice, else its first option with files. Optional options and groups stay off; the
        // parents and main files a choice needs are added (FluffyPresets.Complete).
        internal static List<string> Selection(ModPackage p, bool replace)
        {
            p.PrepareChoices();
            var order = new Dictionary<ModComponent, int>();
            for (int i = 0; i < p.Components.Count; i++) if (!order.ContainsKey(p.Components[i])) order[p.Components[i]] = i;
            Func<ModComponent, int> rank = delegate(ModComponent c) { int i; return order.TryGetValue(c, out i) ? i : Int32.MaxValue; };
            Func<ModComponent, HashSet<string>> files = delegate(ModComponent c) { return new HashSet<string>((replace ? c.Assets.Concat(c.RootAssets) : c.Assets).Select(a => a.DestinationPath), StringComparer.OrdinalIgnoreCase); };
            List<ModComponent> chosen = Composer.DefaultSelection(p, replace).Select(p.Find).Where(c => c != null).OrderBy(rank).ToList();
            if (chosen.Count > 1 && !chosen.Any(Composer.IsBase))
            {
                var kept = new List<ModComponent>(); var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (ModComponent c in chosen.OrderByDescending(c => c.IsDefaultChoice).ThenBy(rank))
                {
                    HashSet<string> mine = files(c);
                    if (kept.Count > 0 && mine.Overlaps(taken)) continue;   // another variant of the same outfit
                    kept.Add(c); taken.UnionWith(mine);
                }
                chosen = kept.OrderBy(rank).ToList();
            }
            var ids = chosen.Select(c => c.Id).ToList();
            for (int round = 0; round < 10; round++)
            {
                List<ModComponent> open = Composer.UnresolvedChoiceGroups(p, ids, replace);
                if (open.Count == 0) break;
                bool added = false;
                foreach (ModComponent group in open)
                {
                    ModComponent pick = FirstReal(p, group, replace, rank);
                    if (pick != null && !ids.Contains(pick.Id, StringComparer.OrdinalIgnoreCase)) { ids.Add(pick.Id); added = true; }
                }
                if (!added) break;
            }
            return FluffyPresets.Complete(p, ids);
        }
        // The group's default choice, else its first option with files (looking into sub-groups), in menu order.
        private static ModComponent FirstReal(ModPackage p, ModComponent group, bool replace, Func<ModComponent, int> rank)
        {
            var children = p.Components.Where(c => String.Equals(c.ParentId, group.Id, StringComparison.OrdinalIgnoreCase)).OrderBy(rank).ToList();
            ModComponent best = children.FirstOrDefault(c => c.IsDefaultChoice && !c.IsDummy && c.HasFiles(replace)) ?? children.FirstOrDefault(c => !c.IsDummy && c.HasFiles(replace));
            if (best != null) return best;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { group.Id };
            foreach (ModComponent child in children.Where(c => c.IsDummy))
                if (seen.Add(child.Id)) { ModComponent deeper = FirstReal(p, child, replace, rank); if (deeper != null) return deeper; }
            return null;
        }
    }

    // 1.0.6 (the user): several mods selected in the Mod library (Ctrl/Shift+click, Ctrl+A) open a queue in place of the
    // inspector. Install runs them one after another with each mod's default options (BatchDefaults); Delete moves them to
    // the Recycle Bin. Each row shows its own result; a mod that can't go in is skipped with the reason and the rest
    // continue. Cancel (status bar) stops after the current mod.
    internal sealed partial class MainForm
    {
        internal sealed class BatchItem
        {
            internal LibraryEntry Entry;
            internal string State = BatchStates.Waiting, Detail = "";
        }
        internal static class BatchStates
        {
            internal const string Waiting = "Waiting", Working = "Working…", Installed = "✓ Installed", Deleted = "✓ Deleted", Skipped = "Skipped", Failed = "Failed", NotRun = "Not run";
        }

        private Panel batchPanel, detailPanel;
        private Label batchTitle, batchNote, batchSummary;
        private ListView batchList;
        private FlatButton batchInstallButton, batchDeleteButton, batchCloseButton;
        private SegmentedChoice batchMode;
        private readonly List<BatchItem> batchItems = new List<BatchItem>();
        private bool batchRunning, selectionQueued, selectionPicking;

        internal bool BatchVisible { get { return batchPanel != null && batchPanel.Visible; } }
        internal List<BatchItem> BatchItems { get { return batchItems.ToList(); } }   // tests

        private Panel BuildBatchPanel()
        {
            batchPanel = new Panel { Dock = DockStyle.Fill, BackColor = Palette.Surface, Padding = new Padding(12), Visible = false, Name = "batchPanel" };
            batchTitle = new Label { UseMnemonic = false, Text = "", Dock = DockStyle.Top, Height = 34, Font = new Font("Segoe UI", 15F, FontStyle.Bold), AutoEllipsis = true };
            batchNote = new Label { UseMnemonic = false, Text = "Each mod installs with its default options. Bundles get the main files and the first or default choice of each required group; optional variants stay off.", Dock = DockStyle.Top, Height = 42, ForeColor = Palette.Muted, Font = Palette.Small, AutoEllipsis = true };
            var modeRow = new Panel { Dock = DockStyle.Top, Height = 44, Padding = new Padding(0, 4, 0, 8) };
            Label modeCaption = Caption("INSTALL AS"); modeCaption.Dock = DockStyle.Left; modeCaption.Width = 92; modeCaption.Height = 32;
            batchMode = new SegmentedChoice("Extra outfit", "Replace original") { Dock = DockStyle.Left, Width = 300, Name = "batchMode", AccessibleName = "Install the queue as" };
            batchMode.Descriptions[0] = "Each mod becomes a new slot (Slotsloader). Code tools and PAK mods still replace.";
            batchMode.Descriptions[1] = "Each mod replaces the original game files.";
            batchMode.SelectionChanged += delegate { BatchModeChosen(batchMode.SelectedIndex == 1 ? InstallModes.Replace : InstallModes.Slot); };
            modeRow.Controls.Add(batchMode); modeRow.Controls.Add(modeCaption);

            batchList = CreateList(50); batchList.Columns.Add("Mod", 300); batchList.DrawSubItem += DrawBatchItem; batchList.ShowItemToolTips = false;
            ((BufferedModList)batchList).SuppressIdleHover = true; batchList.HotTracking = false; batchList.HoverSelection = false;
            batchList.Name = "batchList"; batchList.AccessibleName = "Queued mods";
            Action sizeBatch = delegate { ((BufferedModList)batchList).FitSingleColumn(); };
            batchList.Resize += delegate { sizeBatch(); }; dpiLayouts.Add(sizeBatch);

            Panel footer = new Panel { Dock = DockStyle.Bottom, Height = 64, BackColor = Palette.Background, Padding = new Padding(10, 6, 8, 5) };
            FlowLayoutPanel actions = new FlowLayoutPanel { Dock = DockStyle.Right, Width = 444, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Padding = new Padding(0, 7, 0, 0) };
            batchInstallButton = Button("Install", true, 150, delegate { RunBatchInstall(); });
            batchDeleteButton = Button("Delete", false, 140, delegate { RunBatchDelete(); }); batchDeleteButton.Danger = true;
            batchCloseButton = Button("Close", false, 90, delegate { CloseBatch(); });
            foreach (FlatButton button in new[] { batchInstallButton, batchDeleteButton, batchCloseButton }) { button.Height = 38; button.Margin = new Padding(5, 0, 0, 0); button.Font = Palette.Small; }
            batchInstallButton.Name = "batchInstallButton"; batchDeleteButton.Name = "batchDeleteButton"; batchCloseButton.Name = "batchCloseButton";
            batchDeleteButton.AccessibleDescription = "Moves the selected mods to the Recycle Bin. Installed copies stay installed.";
            actions.Controls.Add(batchInstallButton); actions.Controls.Add(batchDeleteButton); actions.Controls.Add(batchCloseButton);
            batchSummary = new Label { UseMnemonic = false, Text = "", Dock = DockStyle.Fill, ForeColor = Palette.Muted, Font = Palette.Small, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
            footer.Controls.Add(batchSummary); footer.Controls.Add(actions);

            var listHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 0, 0, 9) };
            listHost.Controls.Add(batchList);
            batchPanel.Controls.Add(listHost); batchPanel.Controls.Add(footer); batchPanel.Controls.Add(modeRow); batchPanel.Controls.Add(batchNote); batchPanel.Controls.Add(batchTitle);
            listHost.BringToFront();
            return batchPanel;
        }

        private void DrawBatchItem(object sender, DrawListViewSubItemEventArgs e)
        {
            BatchItem item = e.Item.Tag as BatchItem; if (item == null) return;
            Rectangle r = e.Bounds;
            using (Brush bg = new SolidBrush(Palette.Surface)) e.Graphics.FillRectangle(bg, r);
            Color stateColor = item.State == BatchStates.Installed || item.State == BatchStates.Deleted ? Palette.Green : item.State == BatchStates.Failed ? Palette.Red : item.State == BatchStates.Working ? Palette.Accent : Palette.Muted;
            using (Brush rail = new SolidBrush(item.State == BatchStates.Waiting || item.State == BatchStates.NotRun ? Palette.Border : stateColor)) e.Graphics.FillRectangle(rail, r.Left + Px(1), r.Top + Px(10), Px(3), r.Height - Px(20));
            const TextFormatFlags one = TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
            int stateWidth = Math.Min(r.Width / 3, TextRenderer.MeasureText(e.Graphics, item.State, Palette.Small, Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine).Width + Px(8));
            TextRenderer.DrawText(e.Graphics, LibraryRowName(item.Entry), Palette.Strong, new Rectangle(r.Left + Px(13), r.Top + Px(5), r.Width - Px(24) - stateWidth, Px(22)), Palette.Text, one);
            string detail = item.Detail.Length > 0 ? item.Detail : LibraryCharacterLabel(item.Entry) + "   ·   " + (item.Entry.Kind ?? "").ToUpperInvariant() + (item.Entry.Size > 0 ? "   ·   " + Utilities.FriendlyBytes(item.Entry.Size) : "");
            TextRenderer.DrawText(e.Graphics, detail, Palette.Tiny, new Rectangle(r.Left + Px(13), r.Top + Px(28), r.Width - Px(24), Px(18)), item.State == BatchStates.Failed ? Palette.Red : Palette.Muted, one);
            TextRenderer.DrawText(e.Graphics, item.State, Palette.Small, new Rectangle(r.Right - Px(8) - stateWidth, r.Top + Px(5), stateWidth, Px(22)), stateColor, one | TextFormatFlags.Right);
        }

        // ---- Selection ------------------------------------------------------------------------------------------
        // The list's selection settles before it is read: a click on one row in a multi-selection deselects the others
        // one by one, and a half-done state must not open (or queue) the wrong mod.
        // Several rows only with Ctrl or Shift (click, arrows) or when the app selects several (Ctrl+A, a rebuild keeping
        // the queue's rows): any other new selection replaces the old one, as in the single-select list before 1.0.6.
        private void LibraryItemSelected(ListViewItemSelectionChangedEventArgs e)
        {
            if (!e.IsSelected || selectingMod || (Control.ModifierKeys & (Keys.Control | Keys.Shift)) != 0 || modList.SelectedItems.Count < 2) return;
            bool previous = selectingMod; selectingMod = true;
            try { foreach (ListViewItem other in modList.SelectedItems.Cast<ListViewItem>().ToList()) if (other != e.Item) other.Selected = false; }
            finally { selectingMod = previous; }
        }
        // Selects these rows as one selection (tests, and a queue rebuilt by the app).
        internal void SelectLibraryEntries(IEnumerable<LibraryEntry> entries)
        {
            var wanted = new HashSet<LibraryEntry>(entries);
            bool previous = selectingMod; selectingMod = true;
            try { foreach (ListViewItem item in modList.Items) { bool on = wanted.Contains(item.Tag as LibraryEntry); if (item.Selected != on) item.Selected = on; } }
            finally { selectingMod = previous; }
            lastSelectionCount = modList.SelectedItems.Count;
            LibrarySelectionSettled();
        }
        private int lastSelectionCount;
        private void LibrarySelectionEvent()
        {
            int count = modList.SelectedItems.Count, before = lastSelectionCount; lastSelectionCount = count;
            if (count == 0 || busy || selectingMod || (Control.MouseButtons & MouseButtons.Right) != 0) return;
            // One row picked from one row (or none), no queue open: open it at once, as always.
            if (count == 1 && before <= 1 && !BatchVisible && !selectionQueued)
            {
                if (CommitRenameBeforeSelection(modList.SelectedItems[0].Tag as LibraryEntry)) return;
                if (userPickingMod) ClosePanelForPickedMod();
                LoadPackage((LibraryEntry)modList.SelectedItems[0].Tag);
                return;
            }
            if (userPickingMod) selectionPicking = true;
            if (selectionQueued) return;
            selectionQueued = true;
            BeginInvoke((Action)LibrarySelectionSettled);
        }
        private void LibrarySelectionSettled()
        {
            selectionQueued = false; bool picking = selectionPicking; selectionPicking = false;
            if (IsDisposed || busy || selectingMod) return;
            List<LibraryEntry> selected = modList.SelectedItems.Cast<ListViewItem>().Select(i => i.Tag as LibraryEntry).Where(x => x != null).ToList();
            if (selected.Count >= 2) { ShowBatch(selected); return; }
            if (selected.Count == 0) return;
            if (BatchVisible) HideBatch();
            if (CommitRenameBeforeSelection(selected[0])) return;
            if (picking) ClosePanelForPickedMod();
            LoadPackage(selected[0]);
        }
        internal void SelectAllLibraryRows()
        {
            if (busy || modList.Items.Count == 0) return;
            bool previous = selectingMod; selectingMod = true;
            modList.BeginUpdate();
            try { foreach (ListViewItem item in modList.Items) if (!item.Selected) item.Selected = true; }
            finally { modList.EndUpdate(); selectingMod = previous; }
            lastSelectionCount = modList.SelectedItems.Count;
            LibrarySelectionSettled();
        }

        private void ShowBatch(List<LibraryEntry> entries)
        {
            if (batchPanel == null || batchRunning) return;
            // One row per source: group folders can list a source once per entry.
            var unique = new List<LibraryEntry>(); var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (LibraryEntry entry in entries) if (!String.IsNullOrEmpty(entry.SourcePath) && entry.Kind != "Library" && seen.Add(entry.SourcePath)) unique.Add(entry);
            batchItems.Clear(); batchItems.AddRange(unique.Select(x => new BatchItem { Entry = x }));
            FillBatchList();
            batchMode.SelectedIndex = BatchStartMode() == InstallModes.Replace ? 1 : 0;
            if (FiltersOpen) ShowFilters(false, false);
            // The queue takes the inspector's place (hidden, like the options column under the Filters panel).
            if (!batchPanel.Visible) { batchPanel.Visible = true; batchPanel.BringToFront(); if (detailPanel != null) detailPanel.Visible = false; }
            UpdateBatchUi();
        }
        private void HideBatch()
        {
            if (batchPanel == null || batchRunning) return;
            if (detailPanel != null) detailPanel.Visible = true;
            batchPanel.Visible = false; batchItems.Clear(); batchList.Items.Clear();
        }
        // Close: back to the inspector, the selection reduced to the open mod's row (or none).
        private void CloseBatch()
        {
            if (busy) return;
            HideBatch();
            bool previous = selectingMod; selectingMod = true;
            try
            {
                foreach (ListViewItem item in modList.SelectedItems.Cast<ListViewItem>().ToList())
                    if (package == null || !String.Equals(((LibraryEntry)item.Tag).SourcePath, package.SourcePath, StringComparison.OrdinalIgnoreCase)) item.Selected = false;
            }
            finally { selectingMod = previous; }
            if (modList.CanFocus) modList.Focus();
        }
        private void FillBatchList()
        {
            batchList.BeginUpdate();
            try { batchList.Items.Clear(); batchList.Items.AddRange(batchItems.Select(x => new ListViewItem(LibraryRowName(x.Entry)) { Tag = x }).ToArray()); }
            finally { batchList.EndUpdate(); }
            ((BufferedModList)batchList).FitSingleColumn(true);
        }
        private void UpdateBatchUi()
        {
            if (batchPanel == null) return;
            int n = batchItems.Count;
            bool finished = batchItems.Any(x => x.State != BatchStates.Waiting);
            batchTitle.Text = ReplacePlanner.N(n, "mod") + " selected";
            batchInstallButton.Text = "Install " + n; batchDeleteButton.Text = "Delete " + n;
            batchInstallButton.Enabled = batchDeleteButton.Enabled = !busy && n > 0 && !finished;
            batchCloseButton.Enabled = !busy;
            batchMode.Locked = busy || finished;
            if (!batchRunning && !finished) batchSummary.Text = "Ctrl+click or Shift+click to change the selection";
            batchList.Invalidate();
        }
        private void SetBatchItem(BatchItem item, string state, string detail)
        {
            item.State = state; item.Detail = detail ?? "";
            int done = batchItems.Count(x => x.State != BatchStates.Waiting && x.State != BatchStates.Working);
            // Only while it runs: a row update posted just before the end must not replace the final summary.
            if (batchRunning) batchSummary.Text = done + " of " + batchItems.Count + " done" + (state == BatchStates.Working ? " · " + LibraryRowName(item.Entry) : "");
            foreach (ListViewItem row in batchList.Items) if (row.Tag == item) { batchList.Invalidate(row.Bounds); if (state == BatchStates.Working) row.EnsureVisible(); break; }
        }

        // The mode the queue starts in: the toggle's, or the remembered one while an open PAK/tool mod forces Replace.
        private string BatchStartMode()
        {
            if (forcedReplaceReason == null) return installMode;
            try { return ReplaceSettings.Load().StartMode(); } catch (Exception ex) { Program.Log(ex); return InstallModes.Slot; }
        }
        private void BatchModeChosen(string mode)
        {
            if (modeToggle != null && !modeToggle.Locked) { SetInstallMode(mode, true); return; }
            try { replaceSettings = ReplaceSettings.Load(); replaceSettings.LastMode = mode; replaceSettings.Save(); } catch (Exception ex) { Program.Log(ex); }
        }
        internal string BatchMode { get { return batchMode != null && batchMode.SelectedIndex == 1 ? InstallModes.Replace : InstallModes.Slot; } }

        // ---- Install queue ----------------------------------------------------------------------------------------
        private sealed class BatchInstallContext
        {
            internal bool Replace, UsePak, Convert, Overridden;
            internal string Root, SevenZip, Game;
            internal ReplaceEngine Engine;
            internal HashSet<string> ExtraSources, ReplacedSources;
            internal Dictionary<string, HashSet<string>> TakenNames;
        }

        internal void RunBatchInstall()
        {
            if (busy || batchRunning || batchItems.Count == 0 || batchItems.Any(x => x.State != BatchStates.Waiting)) return;
            var ctx = new BatchInstallContext { Replace = BatchMode == InstallModes.Replace, Root = config.CostumeRoot, SevenZip = config.SevenZipPath, Game = config.GameRoot };
            ctx.ExtraSources = new HashSet<string>(installed.Where(o => (o.Managed || o.WholeArchive) && !String.IsNullOrWhiteSpace(o.SourcePath)).Select(o => o.SourcePath), StringComparer.OrdinalIgnoreCase);
            ctx.ReplacedSources = new HashSet<string>(gameRows.Where(r => r.Mod != null && !String.IsNullOrWhiteSpace(r.Mod.SourcePath)).Select(r => r.Mod.SourcePath), StringComparer.OrdinalIgnoreCase);
            ctx.TakenNames = installed.GroupBy(o => o.Character ?? "", StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => new HashSet<string>(g.Select(o => o.Name ?? ""), StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
            if (!ctx.Replace && String.IsNullOrWhiteSpace(ctx.Root)) { batchSummary.Text = "Set the SF6 game folder first (gear → Settings)."; return; }
            // Replace installs (and code tools in either mode) share one engine; the loose-loader rule of a single install applies once.
            try
            {
                ReplaceEngine engine = Engine();
                bool usePak; EnsureLooseLoader(engine, true, out usePak);
                ctx.UsePak = usePak;
                ctx.Convert = usePak && engine.LoadState().Mods.Count > 0 && engine.CurrentMethod(engine.LoadState()) == ReplaceMethods.Loose;
                ctx.Overridden = usePak && engine.Settings.Method == ReplaceMethods.Loose;
                if (ctx.Overridden) { ReplaceSettings pakSettings = ReplaceSettings.Load(); pakSettings.Method = ReplaceMethods.Pak; engine = new ReplaceEngine(config.GameRoot, AppConfig.DataDir, pakSettings) { FluffyProvider = engine.FluffyProvider }; }
                ctx.Engine = engine;
            }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); ctx.Engine = null; }
            List<BatchItem> items = batchItems.ToList();
            batchRunning = true; UpdateBatchUi();
            Work("Installing " + ReplacePlanner.N(items.Count, "mod") + "…", delegate { return InstallQueue(items, ctx); }, delegate(string summary)
            {
                batchRunning = false; UpdateBatchUi(); batchSummary.Text = summary;
                RefreshInstalled(summary);
            });
            if (!busy) { batchRunning = false; UpdateBatchUi(); }
        }

        // Worker side: every UI change goes through PostToUi.
        private string InstallQueue(List<BatchItem> items, BatchInstallContext ctx)
        {
            int ok = 0, skipped = 0, failed = 0; bool cancelled = false, converted = false;
            CancellationToken token = OperationContext.Token;
            for (int i = 0; i < items.Count; i++)
            {
                BatchItem item = items[i];
                if (token.IsCancellationRequested) { cancelled = true; for (int j = i; j < items.Count; j++) { BatchItem rest = items[j]; PostToUi(delegate { SetBatchItem(rest, BatchStates.NotRun, "Cancelled before it started"); }); } break; }
                string label = item.Entry.ShownName(false);
                PostToUi(delegate { SetBatchItem(item, BatchStates.Working, "Reading the mod…"); });
                OperationContext.Report("Installing " + label + " (" + (i + 1) + "/" + items.Count + ")", i, items.Count);
                try
                {
                    string state, detail;
                    InstallOne(item.Entry, ctx, ref converted, out state, out detail);
                    if (state == BatchStates.Installed) ok++; else skipped++;
                    PostToUi(delegate { SetBatchItem(item, state, detail); });
                }
                catch (OperationCanceledException)
                {
                    cancelled = true; failed++;
                    PostToUi(delegate { SetBatchItem(item, BatchStates.Failed, "Cancelled; nothing of this mod was installed"); });
                    for (int j = i + 1; j < items.Count; j++) { BatchItem rest = items[j]; PostToUi(delegate { SetBatchItem(rest, BatchStates.NotRun, "Cancelled before it started"); }); }
                    break;
                }
                catch (Exception ex)
                {
                    if (ex is OutOfMemoryException) throw;
                    failed++; Program.Log(ex); string message = ex.Message;
                    PostToUi(delegate { SetBatchItem(item, BatchStates.Failed, message); });
                }
            }
            return "✓ Installed " + ok + " of " + ReplacePlanner.N(items.Count, "mod") + (skipped > 0 ? " · " + skipped + " skipped" : "") + (failed > 0 ? " · " + failed + " failed" : "") + (cancelled ? " · cancelled" : "") + (ok > 0 ? ". " + (gameRunning ? "Restart SF6 to see them." : "Start SF6 to see them.") : ".");
        }

        private void InstallOne(LibraryEntry entry, BatchInstallContext ctx, ref bool converted, out string state, out string detail)
        {
            ModPackage p = PackageReader.Load(entry.SourcePath, ctx.SevenZip);
            if (IsSlotsLoaderPackage(p)) { state = BatchStates.Skipped; detail = "Slotsloader installs on its own: open it and press Install"; return; }
            bool replace = ctx.Replace || IsToolPackage(p) || IsPakPackage(p);
            if (replace ? ctx.ReplacedSources.Contains(entry.SourcePath) : ctx.ExtraSources.Contains(entry.SourcePath)) { state = BatchStates.Skipped; detail = replace ? "Already installed (Replace original)" : "Already installed as an extra outfit"; return; }
            List<string> ids = BatchDefaults.Selection(p, replace);
            CompositionPlan plan = Composer.Plan(p, ids, replace);
            if (!plan.CanInstall) { state = BatchStates.Skipped; detail = plan.Errors.Count > 0 ? plan.Errors[0] : "Nothing to install"; return; }
            string options = OptionText(plan);
            if (replace)
            {
                if (ctx.Engine == null) throw new IOException("The game folder can't be read. Check gear → Settings → Game folder.");
                if (ctx.Convert && !converted) { ctx.Engine.SwitchMethod(ReplaceMethods.Pak); converted = true; }
                var layers = plan.Layers.Where(c => !Composer.IsBase(c) && !String.Equals(c.Name, p.Name, StringComparison.OrdinalIgnoreCase)).ToList();
                string name = p.Name + (layers.Count > 0 && layers.Count <= 3 ? " · " + String.Join(" + ", layers.Select(c => c.Name)) : "");
                ReplacePlan prepared;
                ReplaceMod mod = ReplacePlanner.PrepareAndInstall(plan, ctx.SevenZip, ctx.Engine, name, false, null, out prepared);
                ctx.ReplacedSources.Add(entry.SourcePath);
                state = BatchStates.Installed; detail = "Replace original · " + ReplacePlanner.N(mod.Files.Count, "file") + options;
                return;
            }
            string character = p.Character;
            if (String.IsNullOrEmpty(character)) { state = BatchStates.Skipped; detail = "No single character found: open it to choose one"; return; }
            HashSet<string> taken;
            if (!ctx.TakenNames.TryGetValue(character, out taken)) ctx.TakenNames[character] = taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string baseName = PresetInstaller.CleanName(p.Name), unique = baseName; int n = 2;
            while (taken.Contains(unique)) unique = baseName + " " + n++;
            InstallResult result = Installer.Install(plan, ctx.Root, character, unique, ctx.SevenZip, false);
            taken.Add(unique); ctx.ExtraSources.Add(entry.SourcePath);
            state = BatchStates.Installed; detail = character + " · " + unique + " · " + ReplacePlanner.N(result.FileCount, "file") + options;
        }
        // " · Main + Red" for a bundle (the options it got), "" for a one-option mod.
        private static string OptionText(CompositionPlan plan)
        {
            var names = plan.Layers.Select(c => c.Name).Where(x => !String.IsNullOrWhiteSpace(x)).ToList();
            if (plan.Package == null || plan.Package.Components.Count(c => !c.IsDummy && c.HasFiles(plan.ReplaceMode)) <= 1 || names.Count == 0) return "";
            return " · " + String.Join(" + ", names.Take(3)) + (names.Count > 3 ? " +" + (names.Count - 3) : "");
        }

        // ---- Delete queue -----------------------------------------------------------------------------------------
        internal void RunBatchDelete()
        {
            if (busy || batchRunning || batchItems.Count == 0 || batchItems.Any(x => x.State != BatchStates.Waiting)) return;
            string[] libraries = config.Libraries.ToArray(); List<KeyValuePair<string, string>> kept = DeleteProtectedPaths();
            var work = new List<BatchItem>();
            foreach (BatchItem item in batchItems)
            {
                string full; bool folder;
                if (LinkedFluffy(item.Entry.SourcePath).Count > 0) { SetBatchItem(item, BatchStates.Skipped, LibraryDelete.FluffyRefusal); continue; }
                string refusal = LibraryDelete.ScopeError(item.Entry.SourcePath, libraries, kept, out full, out folder);
                if (refusal != null) { SetBatchItem(item, BatchStates.Skipped, refusal); continue; }
                if (package != null && reviewCancel != null && SamePath(package.SourcePath, full)) try { reviewCancel.Cancel(); } catch (ObjectDisposedException) { }
                work.Add(item);
            }
            if (work.Count == 0) { UpdateBatchUi(); batchSummary.Text = "Nothing was deleted."; return; }
            IRecycleBin bin = RecycleBin.Current;
            DeleteRuns++;
            batchRunning = true; UpdateBatchUi();
            Work("Moving " + ReplacePlanner.N(work.Count, "mod") + " to the Recycle Bin…", delegate { return DeleteQueue(work, libraries, kept, bin, false); }, delegate(List<KeyValuePair<BatchItem, LibraryDelete.Outcome>> outcomes)
            {
                batchRunning = false;
                BatchDeleteDone(outcomes, libraries, kept, bin);
            });
            if (!busy) { batchRunning = false; UpdateBatchUi(); }
        }
        private List<KeyValuePair<BatchItem, LibraryDelete.Outcome>> DeleteQueue(List<BatchItem> items, string[] libraries, List<KeyValuePair<string, string>> kept, IRecycleBin bin, bool permanent)
        {
            var outcomes = new List<KeyValuePair<BatchItem, LibraryDelete.Outcome>>();
            CancellationToken token = OperationContext.Token;
            for (int i = 0; i < items.Count; i++)
            {
                BatchItem item = items[i];
                if (token.IsCancellationRequested) { for (int j = i; j < items.Count; j++) { BatchItem rest = items[j]; PostToUi(delegate { SetBatchItem(rest, BatchStates.NotRun, "Cancelled before it started"); }); } break; }
                PostToUi(delegate { SetBatchItem(item, BatchStates.Working, permanent ? "Deleting…" : "Moving to the Recycle Bin…"); });
                OperationContext.Report((permanent ? "Deleting " : "Recycling ") + item.Entry.ShownName(false) + " (" + (i + 1) + "/" + items.Count + ")", i, items.Count);
                LibraryDelete.Outcome outcome;
                try { outcome = LibraryDelete.Run(item.Entry.SourcePath, libraries, kept, bin, permanent); }
                catch (OperationCanceledException) { outcome = new LibraryDelete.Outcome { Error = "Cancelled" }; }
                catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); outcome = new LibraryDelete.Outcome { Error = ex.Message }; }
                outcomes.Add(new KeyValuePair<BatchItem, LibraryDelete.Outcome>(item, outcome));
                LibraryDelete.Outcome o = outcome;
                PostToUi(delegate
                {
                    if (o.Done) SetBatchItem(item, BatchStates.Deleted, o.Permanent ? "Deleted permanently" : "In the Recycle Bin");
                    else if (o.Unavailable != null) SetBatchItem(item, BatchStates.Waiting, "The Recycle Bin can't take it (" + o.Unavailable + ")");
                    else SetBatchItem(item, o.Refused ? BatchStates.Skipped : BatchStates.Failed, o.Error ?? "Not deleted");
                });
            }
            return outcomes;
        }
        private void BatchDeleteDone(List<KeyValuePair<BatchItem, LibraryDelete.Outcome>> outcomes, string[] libraries, List<KeyValuePair<string, string>> kept, IRecycleBin bin)
        {
            var done = outcomes.Where(o => o.Value.Done).ToList();
            var unavailable = outcomes.Where(o => o.Value.Unavailable != null).Select(o => o.Key).ToList();
            if (done.Count > 0) FinishBatchDelete(done);
            if (unavailable.Count > 0)
            {
                // Never a silent permanent delete: one question for all of them (PolishChecks.PopupAllowList).
                string label = unavailable.Count == 1 ? unavailable[0].Entry.ShownName(false) : ReplacePlanner.N(unavailable.Count, "mod");
                if (AskPermanentDelete(label, outcomes.First(o => o.Value.Unavailable != null).Value.Unavailable))
                {
                    batchRunning = true; UpdateBatchUi();
                    Work("Deleting " + label + "…", delegate { return DeleteQueue(unavailable, libraries, kept, bin, true); }, delegate(List<KeyValuePair<BatchItem, LibraryDelete.Outcome>> again)
                    {
                        batchRunning = false;
                        foreach (var o in again.Where(x => x.Value.Unavailable != null)) SetBatchItem(o.Key, BatchStates.Failed, o.Value.Unavailable);
                        BatchDeleteDone(again.Where(x => x.Value.Unavailable == null).ToList(), libraries, kept, bin);
                    });
                    if (!busy) batchRunning = false;
                    return;
                }
                foreach (BatchItem item in unavailable) SetBatchItem(item, BatchStates.Skipped, "Kept: the Recycle Bin can't take it");
            }
            UpdateBatchUi();
            int deleted = batchItems.Count(x => x.State == BatchStates.Deleted);
            string summary = "Deleted " + deleted + " of " + ReplacePlanner.N(batchItems.Count, "mod") + (batchItems.Any(x => x.State == BatchStates.Skipped || x.State == BatchStates.Failed) ? " · see the list for the rest" : "") + ".";
            batchSummary.Text = summary;
            SetSharedStatus(summary, deleted > 0 ? Palette.Green : Palette.Accent);
        }
        // The deleted rows leave the list at once (no rescan), like a single delete (FinishLibraryDelete); the queue stays
        // on screen with the results until another mod is picked.
        private void FinishBatchDelete(List<KeyValuePair<BatchItem, LibraryDelete.Outcome>> done)
        {
            var gone = new HashSet<LibraryEntry>();
            foreach (var d in done)
            {
                foreach (LibraryEntry e in SameSourceEntries(d.Key.Entry)) gone.Add(e);
                if (!String.IsNullOrEmpty(d.Value.Full)) { deletedSources.Add(d.Value.Full); changedSources.Remove(d.Value.Full); }
                if (package != null && SamePath(package.SourcePath, d.Value.Full ?? d.Key.Entry.SourcePath)) ClosePackage();
            }
            foreach (LibraryEntry e in gone) { deletedSources.Add(e.SourcePath); changedSources.Remove(e.SourcePath); }
            LibraryNames.Forget(gone.Select(e => e.SourcePath).Concat(done.Select(d => d.Value.Full)));
            List<LibraryEntry> before = library;
            library = library.Where(e => !gone.Contains(e)).ToList();
            if (Object.ReferenceEquals(libraryRowsSource, before)) libraryRowsSource = library;
            foreach (LibraryEntry e in gone) { libraryRows.Remove(e); installStates.Remove(e); libraryRanks.Remove(e); }
            bool previous = selectingMod; selectingMod = true;
            try { ApplyFilters(); foreach (ListViewItem item in modList.SelectedItems.Cast<ListViewItem>().ToList()) item.Selected = false; }
            finally { selectingMod = previous; }
            FillInstalled(); installedList.Invalidate(); UpdateLibraryUninstall();
            if (assistantPage != null) assistantPage.LibraryChanged();
        }
    }
}
