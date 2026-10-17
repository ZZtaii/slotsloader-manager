using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace SlotsInstaller
{
    // Lines of text with optional inline action links ("Uninstall 'X'", "Undo"), painted in one
    // buffered control so long names wrap and links stay small. Used by the Replace-mode inspector.
    internal sealed class ConflictView : Control
    {
        internal sealed class Entry
        {
            internal string Text = "", Link = "", LinkTip = "";
            internal Color Color = Palette.Muted;
            internal Action OnLink;
            internal bool LinkEnabled = true;
            internal int MaxLines = 3;
        }
        private readonly List<Entry> entries = new List<Entry>();
        private readonly List<KeyValuePair<Rectangle, Entry>> links = new List<KeyValuePair<Rectangle, Entry>>();
        internal Action<string> ShowReason;   // a disabled link's reason, shown in the status strip when clicked
        private Entry hovered;
        internal IList<Entry> Entries { get { return entries.AsReadOnly(); } }

        internal ConflictView()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Palette.Surface; Font = Palette.Small; TabStop = false;
            AccessibleRole = AccessibleRole.Grouping; AccessibleName = "Conflicts with installed mods";
        }

        internal void LimitLines(int lines) { foreach (Entry e in entries) e.MaxLines = Math.Min(e.MaxLines, lines); Invalidate(); }

        internal void SetEntries(IEnumerable<Entry> values)
        {
            List<Entry> snapshot = values == null ? new List<Entry>() : values.ToList();   // 2.4.2: a copy, never a live view of entries
            entries.Clear(); entries.AddRange(snapshot);
            hovered = null; Cursor = Cursors.Default;
            AccessibleDescription = String.Join(" ", snapshot.Select(e => e.Text + (e.Link.Length > 0 ? " [" + e.Link + "]" : "")));
            Invalidate();
        }

        private int Px(int logical) { return DpiMetrics.Px(this, logical); }
        private const TextFormatFlags Wrap = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.TextBoxControl;

        private int LineHeight(Graphics g) { return TextRenderer.MeasureText(g, "Ag", Font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPrefix).Height; }
        private int TextHeight(Graphics g, Entry e, int width)
        {
            int line = LineHeight(g);
            int h = TextRenderer.MeasureText(g, e.Text, Font, new Size(Math.Max(1, width), int.MaxValue), Wrap).Height;
            return Math.Min(h, line * e.MaxLines);
        }

        // Height needed to show every entry at `width`.
        internal int PreferredHeightFor(int width)
        {
            if (entries.Count == 0) return 0;
            using (Graphics g = CreateGraphics())
            {
                int line = LineHeight(g), total = 0;
                foreach (Entry e in entries) total += TextHeight(g, e, width) + (e.Link.Length > 0 ? line : 0) + Px(6);
                return total;
            }
        }

        protected override void OnPaint(PaintEventArgs pe)
        {
            pe.Graphics.Clear(BackColor);
            links.Clear();
            int y = 0, line = LineHeight(pe.Graphics), width = ClientSize.Width;
            foreach (Entry e in entries)
            {
                int h = TextHeight(pe.Graphics, e, width);
                int reserve = e.Link.Length > 0 ? line : 0;   // the link keeps its line even when the text is cut
                if (y + Math.Min(h, line) + reserve > ClientSize.Height) break;
                h = Math.Min(h, Math.Max(line, (ClientSize.Height - y - reserve) / line * line));
                TextRenderer.DrawText(pe.Graphics, e.Text, Font, new Rectangle(0, y, width, h), e.Color, Wrap);
                y += h;
                if (e.Link.Length > 0 && y + line <= ClientSize.Height)
                {
                    Size size = TextRenderer.MeasureText(pe.Graphics, e.Link, Font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
                    var rect = new Rectangle(0, y, Math.Min(width, size.Width), line);
                    Color color = !e.LinkEnabled ? Palette.Muted : e == hovered ? Palette.Text : Palette.Accent;
                    using (Font underline = new Font(Font, FontStyle.Underline))
                        TextRenderer.DrawText(pe.Graphics, e.Link, e.LinkEnabled ? underline : Font, rect, color, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
                    links.Add(new KeyValuePair<Rectangle, Entry>(rect, e));
                    y += line;
                }
                y += Px(6);
            }
        }

        private Entry LinkAt(Point p) { foreach (var kv in links) if (kv.Key.Contains(p)) return kv.Value; return null; }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Entry at = LinkAt(e.Location);
            if (at == hovered) return;
            hovered = at; Cursor = at != null && at.LinkEnabled ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (hovered != null) { hovered = null; Invalidate(); } }
        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            Entry at = LinkAt(e.Location);
            if (at != null && at.LinkEnabled && at.OnLink != null) at.OnLink();
            else if (at != null && !at.LinkEnabled && ShowReason != null && !String.IsNullOrEmpty(at.LinkTip)) ShowReason(at.LinkTip);   // 2.4.0: no tooltip
        }
        // Tests and accessibility: activate the link of entry `index`.
        internal bool Activate(int index)
        {
            if (index < 0 || index >= entries.Count) return false;
            Entry e = entries[index];
            if (!e.LinkEnabled || e.OnLink == null) return false;
            e.OnLink(); return true;
        }
    }

    internal sealed partial class MainForm
    {
        // ---- Automatic review (2.0.1)
        // Any change that can alter the result (package, selection, mode, method, installed set) bumps
        // reviewGeneration and schedules a background run after a short pause. One run at a time; a
        // finished run applies only when its generation is still current, otherwise the latest request
        // starts. The app-wide busy state is never set, so the window stays usable.
        internal const int ReviewDelay = 300;
        internal static int ReviewTestDelay;
        private System.Windows.Forms.Timer reviewTimer;
        private int reviewGeneration, appliedGeneration = -1;
        private bool reviewRunning, openReportWhenReady, showFilesWhenReady, reviewScreenshotTaken;
        private CancellationTokenSource reviewCancel;
        private PrepareCache checkCache;
        private ModPackage checkCachePackage;
        private string reviewStage = "";
        internal int ReviewRuns, ReviewApplied, ReviewDiscarded;
        internal long LastReviewMilliseconds;
        internal string LastReviewSelection = "";
        private ConflictView conflictView;
        private RemovedReplacement lastRemoved;
        private string lastRemovedSource;

        // Library costume search (2.0.1): rank of each listed entry for the current costume query.
        private readonly Dictionary<LibraryEntry, int> libraryRanks = new Dictionary<LibraryEntry, int>();

        internal bool ReviewPending { get { return package != null && appliedGeneration != reviewGeneration; } }
        internal bool ReviewRunning { get { return reviewRunning; } }
        internal bool Working { get { return busy || ReviewPending; } }

        private sealed class ReviewResult
        {
            internal int Generation; internal ModPackage Package; internal string Mode; internal string[] Selection;
            internal CompositionPlan Composition; internal ReplacePlan Health; internal long Milliseconds;
        }

        private void EnsureReviewTimer()
        {
            if (reviewTimer != null) return;
            reviewTimer = new System.Windows.Forms.Timer { Interval = ReviewDelay };
            reviewTimer.Tick += delegate { reviewTimer.Stop(); ReviewTick(); };
            Disposed += delegate
            {
                reviewTimer.Dispose();
                if (reviewCancel != null) try { reviewCancel.Cancel(); } catch (ObjectDisposedException) { }
                if (checkCache != null) checkCache.Dispose();
            };
        }

        internal void RequestReview(int delay)
        {
            reviewGeneration++;
            if (package == null) { appliedGeneration = reviewGeneration; return; }
            if (reviewCancel != null) try { reviewCancel.Cancel(); } catch (ObjectDisposedException) { }
            EnsureReviewTimer();
            reviewTimer.Stop(); reviewTimer.Interval = Math.Max(1, delay); reviewTimer.Start();
        }

        private void ReviewTick()
        {
            if (IsDisposed || package == null || !ReviewPending || reviewRunning) return;
            if (busy) { reviewTimer.Interval = 150; reviewTimer.Start(); return; }
            StartReview();
        }

        private void StartReview()
        {
            if (CheckOpenPackage()) return;   // 2.4.1: the source changed on disk: reload (the reload reviews again)
            int generation = reviewGeneration; ModPackage current = package; string[] selection = selectedOrder.ToArray(); string mode = installMode; bool replace = IsReplaceMode;
            string[] missing = missingRestoredOptions.ToArray(); string sevenZip = config.SevenZipPath;
            bool choicesOpen = unresolvedChoices.Count > 0;
            ReplaceEngine engine = replace ? Engine() : null;
            string updateId = editingReplacement == null ? PendingLoaderReplaceId() : editingReplacement.Id;   // 2.4.0: a SlotsLoader update replaces the recorded one
            if (replace && (checkCache == null || !Object.ReferenceEquals(checkCachePackage, current)))
            {
                if (checkCache != null) checkCache.Dispose();
                checkCache = new PrepareCache(); checkCachePackage = current;
            }
            PrepareCache cache = replace ? checkCache : null;
            var cancel = new CancellationTokenSource(); reviewCancel = cancel; reviewRunning = true; ReviewRuns++; reviewStage = "";
            UpdateFooter();
            if (screenshotFile != null && screenshotPage == "checking" && !reviewScreenshotTaken) { reviewScreenshotTaken = true; CaptureScreenshot(); }
            var worker = new BackgroundWorker { WorkerReportsProgress = true };
            worker.DoWork += delegate(object s, DoWorkEventArgs e)
            {
                var clock = Stopwatch.StartNew();
                using (OperationContext.Begin(cancel.Token, delegate(OperationProgress p) { worker.ReportProgress(0, p); }))
                {
                    if (ReviewTestDelay > 0) Thread.Sleep(ReviewTestDelay);   // checks: keep a run in flight
                    OperationContext.ThrowIfCancellationRequested();
                    CompositionPlan composition = Composer.Plan(current, selection, replace);
                    if (missing.Length > 0) composition.Errors.Add("Saved options are missing: " + String.Join(", ", missing) + ". Click " + (replace ? "Health" : "Review files") + " to review the changed selection.");
                    ReplacePlan health = null;
                    if (replace && composition.CanInstall && !choicesOpen)
                    {
                        try { health = ReplacePlanner.Check(composition, sevenZip, engine, cache, updateId); }
                        catch (OperationCanceledException) { throw; }
                        catch (SourceChangedException) { throw; }   // 2.4.1: reloaded, never shown as a problem
                        catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); health = ReplacePlan.Failed(current.Name, current.SourcePath, ex.Message); }
                    }
                    e.Result = new ReviewResult { Generation = generation, Package = current, Mode = mode, Selection = selection, Composition = composition, Health = health, Milliseconds = clock.ElapsedMilliseconds };
                }
            };
            ProgressChangedEventHandler progressed = null;
            progressed = delegate(object s, ProgressChangedEventArgs e)
            {
                if (OffUiThread(delegate { progressed(s, e); })) return;
                if (IsDisposed || generation != reviewGeneration) return;
                var p = (OperationProgress)e.UserState;
                reviewStage = p.Stage + (p.Total > 0 ? " · " + (int)Math.Max(0, Math.Min(100, p.Completed * 100.0 / p.Total)) + "%" : "…");
                if (planDetails != null && ReviewPending) planDetails.Text = reviewStage;
            };
            worker.ProgressChanged += progressed;
            RunWorkerCompletedEventHandler finished = null;
            finished = delegate(object s, RunWorkerCompletedEventArgs e)
            {
                if (OffUiThread(delegate { finished(s, e); })) return;
                reviewRunning = false;
                if (reviewCancel == cancel) reviewCancel = null;
                cancel.Dispose();
                worker.Dispose();
                if (IsDisposed) return;
                ReviewResult result = e.Error == null && !e.Cancelled ? (ReviewResult)e.Result : null;
                bool current2 = generation == reviewGeneration && Object.ReferenceEquals(current, package) && mode == installMode;
                if (e.Error is SourceChangedException && Object.ReferenceEquals(current, package)) { ReloadOpenPackage(true); return; }
                if (result != null && current2) ApplyReview(result);
                else if (result == null && current2 && e.Error != null && !(e.Error is OperationCanceledException))
                {
                    Program.Log(e.Error);
                    var failed = new CompositionPlan { Package = current }; failed.Errors.Add("Health check failed: " + e.Error.Message);
                    ApplyReview(new ReviewResult { Generation = generation, Package = current, Mode = mode, Selection = selection, Composition = failed });
                }
                else ReviewDiscarded++;
                if (ReviewPending && reviewTimer != null && !reviewTimer.Enabled) { if (busy) { reviewTimer.Interval = 150; reviewTimer.Start(); } else StartReview(); }
            };
            worker.RunWorkerCompleted += finished;
            worker.RunWorkerAsync();
        }

        private void ApplyReview(ReviewResult r)
        {
            appliedGeneration = r.Generation; ReviewApplied++; LastReviewMilliseconds = r.Milliseconds;
            LastReviewSelection = String.Join("|", r.Selection);
            plan = r.Composition; replacePlan = r.Health; FillFiles(); UpdateFooter();
            // Keep the result of an action the user just took (install, uninstall, Undo) in the status strip.
            KeyValuePair<string, Color> shown;
            bool keepStatus = pageStatus.TryGetValue(libraryPage, out shown) && shown.Key.Length > 0 && !(shown.Key.StartsWith("Health check") || shown.Key.StartsWith("Checking") || shown.Key.StartsWith("Ready to compose") || shown.Key.StartsWith("Merged outfit preview") || shown.Key.EndsWith(LibraryFoundText(-1, config.Libraries.Count)) || shown.Key.Contains(" libraries · drop an archive"));
            if (keepStatus) { }
            else if (IsReplaceMode)
            {
                if (replacePlan != null) SetPageStatus(libraryPage, replacePlan.CanInstall ? "Health check done · " + replacePlan.AffectsText + " · " + Seconds(r.Milliseconds) : "Health check found problems · " + replacePlan.Errors.First(), replacePlan.CanInstall ? Palette.Green : Palette.Red);
            }
            else if (plan.CanInstall) SetPageStatus(libraryPage, "Merged outfit preview ready · " + ReplacePlanner.N(plan.Files.Count, "file"));
            if (screenshotFile != null && !reviewScreenshotTaken)
            {
                reviewScreenshotTaken = true;
                if (screenshotPage == "health") { CaptureHealthReport(); return; }
                if (screenshotPage == "checking") return;
                CaptureScreenshot(); return;
            }
            if (openReportWhenReady) { openReportWhenReady = false; ShowCurrentReport(); }
            if (showFilesWhenReady) { showFilesWhenReady = false; ShowDetailTab(true); }
            if (pendingLoaderUpdate != null) BeginInvoke((Action)ContinueLoaderUpdate);   // 2.4.0
        }

        private static string Seconds(long ms) { return ms < 1000 ? "checked in " + ms + " ms" : "checked in " + (ms / 1000.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " s"; }

        // Report / Review files: a view of the computed result. While a check runs it opens when ready.
        private void ShowReviewOrWait(bool open)
        {
            if (package == null || busy) return;
            if (!open) { RequestReview(1); return; }   // explicit re-check
            if (!ReviewPending)
            {
                if (IsReplaceMode) ShowCurrentReport(); else ShowDetailTab(true);
                return;
            }
            if (open) { if (IsReplaceMode) openReportWhenReady = true; else showFilesWhenReady = true; }
            if (!reviewRunning) { EnsureReviewTimer(); reviewTimer.Stop(); ReviewTick(); }
            if (open) SetPageStatus(libraryPage, "Checking " + package.Name + "… " + (IsReplaceMode ? "the report opens" : "the merged files show") + " when done.");
        }

        private void ShowCurrentReport()
        {
            if (Program.Headless) return;
            if (IsReplaceMode && replacePlan != null) { using (Form report = CreateHealthReport(replacePlan)) report.ShowDialog(this); }
            else if (plan != null) ShowCompositionReport();
        }

        // ---- Conflicts in the inspector and footer
        private string ConflictLinkBlocker()
        {
            if (busy) return "Wait for the current task to finish.";
            if (ReviewPending) return "Wait for the health check to finish.";
            if (FluffyBlocksPak) return FluffyPakReason;
            if (gameRunning && gameMethod != ReplaceMethods.Loose) return "Close SF6 first: game files can't change while it runs.";
            return null;
        }

        internal void UpdateConflictView()
        {
            if (conflictView == null) return;
            var entries = new List<ConflictView.Entry>();
            string blocker = ConflictLinkBlocker();
            // 1.0.1: no "Uninstalled … Undo" line: uninstall keeps no copy (lastRemoved is never set).
            if (IsReplaceMode && replacePlan != null && !ReviewPending)
            {
                var slots = replacePlan.ConflictList.Where(c => c.Kind == "slot").ToList();
                foreach (ReplaceConflict c in slots.Take(2))
                {
                    ReplaceConflict target = c;
                    entries.Add(new ConflictView.Entry { Text = c.Text, Color = Palette.Accent, Link = "Uninstall '" + c.ModName + "'", LinkEnabled = blocker == null, LinkTip = blocker ?? "Uninstall '" + c.ModName + "'", OnLink = delegate { UninstallConflict(target); } });
                }
                if (slots.Count > 2)
                    entries.Add(new ConflictView.Entry { Text = (slots.Count - 2) + " more mods change the same outfit.", Color = Palette.Accent, Link = "View report", OnLink = delegate { ShowReviewOrWait(true); }, MaxLines = 1 });
                ReplaceConflict files = replacePlan.ConflictList.FirstOrDefault(c => c.Kind == "files");
                if (files != null && slots.Count == 0) entries.Add(new ConflictView.Entry { Text = files.Text, Color = Palette.Accent, MaxLines = 2 });
                foreach (ReplaceConflict c in replacePlan.ConflictList.Where(c => c.Kind == "stack").Take(2)) entries.Add(new ConflictView.Entry { Text = c.Text, Color = Palette.Muted, MaxLines = 2 });
                if (replacePlan.UnknownLoose.Count > 0) entries.Add(new ConflictView.Entry { Text = ReplacePlanner.N(replacePlan.UnknownLoose.Count, "file") + " already in the game folder, not installed by the Manager (backed up, put back on uninstall).", Color = Palette.Muted, MaxLines = 2 });
            }
            conflictView.SetEntries(entries);
            SizeReplacesPanel();
        }

        // The REPLACES block grows with its conflict lines, keeping at least the preview minimum above it.
        private void SizeReplacesPanel()
        {
            if (replacesPanel == null || conflictView == null) return;
            int baseHeight = Px(70), width = Math.Max(Px(100), replacesPanel.ClientSize.Width);
            Control inspector = replacesPanel.Parent;
            int room = inspector == null ? Int32.MaxValue : inspector.ClientSize.Height - inspector.Padding.Vertical - (modePanel == null ? 0 : modePanel.Height) - Px(154) - baseHeight;
            // Short inspectors (1110x610): fewer text lines first, so the inline links stay visible.
            int want = conflictView.Entries.Count == 0 ? 0 : conflictView.PreferredHeightFor(width);
            for (int lines = 2; lines >= 1 && want > room; lines--) { conflictView.LimitLines(lines); want = conflictView.PreferredHeightFor(width); }
            int height = baseHeight + Math.Max(0, Math.Min(want, room));
            if (replacesPanel.Height != height) replacesPanel.Height = height;
        }

        private string FooterConflictText(ReplacePlan health)
        {
            ReplaceConflict c = health.Headline;
            if (c == null) return null;
            int more = health.ConflictList.Count(x => x.Kind != "stack") - 1;
            string area = c.Areas.Count == 0 ? "" : ReplacePlanner.ShortArea(c.Areas[0]);
            int rest = c.TheirFiles - c.Shared;
            string text;
            if (c.Kind == "files") text = c.Enabled ? (c.NewWins ? "Overrides " + ReplacePlanner.N(c.Shared, "file") + " from '" + c.ModName + "' (this install wins)" : "'" + c.ModName + "' keeps " + ReplacePlanner.N(c.Shared, "shared file") + " (higher in the list)") : "Shares " + ReplacePlanner.N(c.Shared, "file") + " with '" + c.ModName + "' (disabled)";
            else if (!c.Enabled) text = area + " also has '" + c.ModName + "' (disabled) · " + ReplacePlanner.N(c.Shared, "shared file");
            else if (!c.NewWins) text = area + " already has '" + c.ModName + "' · it keeps " + ReplacePlanner.N(c.Shared, "shared file") + " (higher in the list)";
            else text = area + " already has '" + c.ModName + "' · overrides " + (rest == 0 ? ReplacePlanner.AllOfIts(c.TheirFiles) : c.Shared + " of its " + c.TheirFiles + " files; " + rest + (rest == 1 ? " stays" : " stay") + " active");
            return text + (more > 0 ? " · +" + more + " more" : "");
        }

        private void UninstallConflict(ReplaceConflict c)
        {
            string blocker = ConflictLinkBlocker();
            if (blocker != null) { SetPageStatus(libraryPage, blocker, Palette.Red); return; }
            ReplaceEngine engine = Engine(); string id = c.ModId, name = c.ModName; string source = package == null ? null : package.SourcePath;
            IRecycleBin bin = RecycleBin.Current;
            // 1.0.1: uninstall means uninstall (ReplaceEngine.UninstallForGood): no kept copy and no Undo line.
            Work("Uninstalling '" + name + "'…", delegate { return engine.UninstallForGood(id, DriftChoice.Ask, bin) ?? ""; }, delegate(string message)
            {
                lastRemoved = null; lastRemovedSource = source;
                RefreshInstalled("✓ Uninstalled '" + name + "'." + (message.Contains("source is gone") ? " " + message.Substring(message.IndexOf("Its source is gone", StringComparison.Ordinal)) : ""));
            });
        }

        private void UndoRemoval()
        {
            RemovedReplacement removed = lastRemoved;
            if (removed == null || busy) return;
            if (FluffyBlocksPak || (gameRunning && gameMethod != ReplaceMethods.Loose)) { SetPageStatus(libraryPage, ConflictLinkBlocker() ?? "Close SF6 first.", Palette.Red); return; }
            ReplaceEngine engine = Engine(); string folder = removed.Folder; string name = removed.Name;
            RecordUndo("Uninstalled '" + name + "'");   // 2.4.1 history (MainFormHistory.cs)
            Work("Restoring '" + name + "'…", delegate { ReplaceMod mod; engine.RestoreRemoved(folder, out mod); return mod.Name; }, delegate(string restored)
            {
                lastRemoved = null;
                RefreshInstalled("✓ Restored '" + restored + "' (files, enabled state and priority)");
            });
        }

        // Extra outfit mode: the same source and options already installed as an extra outfit.
        internal InstalledOutfit DuplicateExtraOutfit()
        {
            if (package == null || IsReplaceMode || installed == null) return null;
            var selection = new HashSet<string>(selectedOrder, StringComparer.OrdinalIgnoreCase);
            return installed.FirstOrDefault(o => o.Managed && String.Equals(o.SourcePath, package.SourcePath, StringComparison.OrdinalIgnoreCase) && o.SelectedIds != null && selection.SetEquals(o.SelectedIds));
        }
        internal static string DuplicateText(InstalledOutfit o)
        {
            return "Already installed as '" + o.Name + "' for " + o.Character + (String.IsNullOrEmpty(o.LoaderOutfitName) ? "" : " (" + o.LoaderOutfitName + ")") + (o.Enabled ? "" : ", disabled");
        }

        // ---- Health report dialog: sized to its content (scrolls when long), dark title bar, Re-check.
        private Form CreateHealthReport(ReplacePlan health)
        {
            Form report = new Form();
            report.SuspendLayout();
            report.AutoScaleDimensions = new SizeF(96F, 96F); report.AutoScaleMode = AutoScaleMode.Dpi;
            report.Text = "Health check · " + health.ModName; report.ClientSize = new Size(820, 640); report.MinimumSize = new Size(600, 300); report.StartPosition = FormStartPosition.CenterParent;
            report.BackColor = Palette.Surface; report.ForeColor = Palette.Text; report.ShowInTaskbar = false; report.Padding = new Padding(20, 14, 20, 14);
            report.MinimizeBox = false; report.KeyPreview = true;
            report.KeyDown += delegate(object s, KeyEventArgs e) { if (e.KeyCode == Keys.Escape) report.Close(); };
            AppBranding.Apply(report);
            var body = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
            Action<string, Color, bool> line = delegate(string text, Color color, bool heading)
            {
                body.Controls.Add(new Label { UseMnemonic = false, Text = text, ForeColor = color, Font = heading ? Palette.Strong : Palette.Normal, AutoSize = true, MaximumSize = new Size(740, 0), Margin = new Padding(0, heading ? 12 : 2, 0, 4) });
            };
            line(health.CanInstall ? "Ready to install" : "Cannot install yet", health.CanInstall ? Palette.Green : Palette.Red, true);
            line("Replaces: " + (health.Affects.Count == 0 ? health.AffectsText : String.Join(", ", health.Affects)), Palette.Text, false);
            line(ReplacePlanner.N(health.Files.Count, "file") + " · " + Utilities.FriendlyBytes(health.Bytes) + " · " + (health.VanillaReplaced == 1 ? "1 replaces a game file · " : health.VanillaReplaced + " replace game files · ") + health.NewFiles + " new · " + health.RootFiles + " in the game folder · " + health.Fixed + " auto-fixed", Palette.Muted, false);
            var conflictTexts = new HashSet<string>(health.ConflictList.Select(c => c.Text));
            foreach (var group in new[] { new { L = "error", T = "Problems", C = Palette.Red } })
            {
                var items = health.Items.Where(i => i.Level == group.L).ToList();
                if (items.Count == 0) continue;
                line(group.T, group.C, true);
                foreach (HealthItem i in items) line(i.Text, group.C, false);
            }
            if (health.ConflictList.Count > 0 || health.UnknownLoose.Count > 0)
            {
                line("Other mods for the same outfit or files", Palette.Accent, true);
                foreach (ReplaceConflict c in health.ConflictList)
                {
                    line(c.Text, c.Kind == "stack" ? Palette.Muted : Palette.Accent, false);
                    foreach (string path in c.SharedPaths.Take(20)) line("   " + path + "  →  " + (!c.Enabled ? "'" + c.ModName + "' is disabled" : c.NewWins ? "this install wins" : "'" + c.ModName + "' wins"), Palette.Muted, false);
                    if (c.SharedPaths.Count > 20) line("   … " + (c.SharedPaths.Count - 20) + " more shared files", Palette.Muted, false);
                }
                if (health.ConflictList.Any(c => c.Shared > 0)) line("A new install goes to the top of the list and wins shared files. Reorder with Move up/down on the Installed page, or uninstall the other mod with the inspector link.", Palette.Muted, false);
                if (health.UnknownLoose.Count > 0)
                {
                    line(ReplacePlanner.N(health.UnknownLoose.Count, "file") + " already in the game folder, not installed by the Manager (backed up now, put back on uninstall):", Palette.Text, false);
                    foreach (string path in health.UnknownLoose.Take(20)) line("   " + path, Palette.Muted, false);
                }
            }
            foreach (var group in new[] { new { L = "warn", T = "Warnings", C = Palette.Accent }, new { L = "fix", T = "Fixed automatically", C = Palette.Green }, new { L = "info", T = "Notes", C = Palette.Muted } })
            {
                var items = health.Items.Where(i => i.Level == group.L && !conflictTexts.Contains(i.Text) && !(group.L == "info" && health.UnknownLoose.Count > 0 && i.Text.Contains("not installed by the Manager"))).ToList();
                if (items.Count == 0) continue;
                line(group.T, group.L == "info" ? Palette.Text : group.C, true);
                foreach (HealthItem i in items) line(i.Text, group.L == "info" ? Palette.Muted : group.C, false);
            }
            var fixedFiles = health.Files.Where(f => f.Fixes.Count > 0).Take(40).ToList();
            if (fixedFiles.Count > 0)
            {
                // Files with the same fix share one line: the fix once, then the files by folder.
                line("Per-file fixes", Palette.Text, true);
                foreach (var same in fixedFiles.GroupBy(f => String.Join("; ", f.Fixes.Select(x => x.Message))))
                {
                    var folders = same.GroupBy(f => f.Path.Substring(0, f.Path.LastIndexOf('/') + 1))
                        .Select(dir => "   " + dir.Key + String.Join(", ", dir.Select(f => f.Path.Substring(dir.Key.Length))));
                    line(same.Key + Environment.NewLine + String.Join(Environment.NewLine, folders), Palette.Muted, false);
                }
            }
            if (health.Method == ReplaceMethods.Pak && health.InvalidationPreview.Count > 0) { line("PAK entries to invalidate", Palette.Text, true); foreach (string row in health.InvalidationPreview.Take(30)) line(row, Palette.Muted, false); if (health.InvalidationPreview.Count > 30) line("… " + (health.InvalidationPreview.Count - 30) + " more", Palette.Muted, false); }
            if (health.Milliseconds > 0) line("Checked automatically · " + Seconds(health.Milliseconds).Substring("checked in ".Length), Palette.Muted, false);
            Action fit = delegate { int w = Math.Max(DpiMetrics.Px(report, 100), body.ClientSize.Width - DpiMetrics.Px(report, 28)); foreach (Control c in body.Controls) c.MaximumSize = new Size(w, 0); };
            body.Resize += delegate { fit(); };
            var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 46, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Padding = new Padding(0, 8, 0, 0) };
            FlatButton close = Button("Close", false, 110, delegate { report.Close(); });
            FlatButton recheck = Button("Re-check", false, 110, delegate { report.Close(); openReportWhenReady = true; RequestReview(1); });
            foreach (FlatButton b in new[] { close, recheck }) { b.Height = 36; b.Margin = new Padding(8, 0, 0, 0); actions.Controls.Add(b); }
            report.CancelButton = close;
            report.Controls.Add(body); report.Controls.Add(actions); DarkTheme.Apply(report); report.ResumeLayout(true); report.PerformAutoScale();
            // Size to the content: no large empty area for short reports; long ones scroll.
            report.PerformLayout(); fit(); body.PerformLayout();
            int content = body.Controls.Cast<Control>().Select(c => c.Bottom + c.Margin.Bottom).DefaultIfEmpty(0).Max() - body.AutoScrollPosition.Y;
            Rectangle area = Screen.FromControl(this).WorkingArea;
            int chrome = report.Height - report.ClientSize.Height;
            int wanted = report.Padding.Vertical + content + actions.Height + DpiMetrics.Px(report, 8);
            int height = Math.Max(DpiMetrics.Px(report, 260), Math.Min(wanted, area.Height * 85 / 100 - chrome));
            report.ClientSize = new Size(report.ClientSize.Width, height);
            report.Tag = wanted <= height ? "fits" : "scrolls";
            return report;
        }
    }
}
