using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace SlotsInstaller
{
    // An Installed-page row that is not an extra outfit: a replacement/tool mod
    // installed by this app, or a mod Fluffy Mod Manager installed (2.2.0: uninstallable here, never written by this app).
    internal sealed class GameModRow
    {
        internal string Kind = "";          // Replacement, Tool, Mixed, Fluffy
        internal ReplaceMod Mod;
        internal FluffyMod Fluffy;
        internal int Priority, Conflicts, Owned;
        internal List<RowShare> Shares = new List<RowShare>();      // other mods with the same files
        internal List<string> Stacks = new List<string>();          // "Cammy Outfit 2 · 2 mods · 'A' wins"
        internal bool HasConflicts { get { return Shares.Count > 0 || Stacks.Count > 0; } }
        internal string Name { get { return Mod != null ? Mod.Name : Fluffy != null ? Fluffy.Name : ""; } }
        internal string KindLabel { get { return Kind == "Fluffy" ? "External mod manager" : Kind == "Pak" ? "PAK mod" : Kind == "Tool" ? "Tool mod" : Kind == "Mixed" ? "Tool mod + replacement" : "Replacement"; } }
    }

    internal sealed class RowShare
    {
        internal string Other = "";
        internal int OtherPriority, Count;
        internal bool OtherEnabled, ThisWins;
        internal List<string> Paths = new List<string>();
    }

    internal sealed partial class MainForm
    {
        private string installMode = InstallModes.Slot;
        private ReplaceSettings replaceSettings = ReplaceSettings.Load();
        private ReplacePlan replacePlan;
        private ReplaceMod editingReplacement;
        private FlatButton launchButton;
        // 2.3.0: INSTALL AS is one joined toggle; a tool package or an edited replacement forces Replace without
        // changing the remembered choice (forcedReplaceReason says why).
        private SegmentedToggle modeToggle;
        private Label modeLockHint;   // "· locked for this mod" while a mod forces Replace (2.3.1: no "· remembered")
        private string forcedReplaceReason;
        private Label gameStateLabel, replacesLabel, replacesDetail, modeNotice;
        private Panel modePanel, replacesPanel, targetPanel;
        private System.Windows.Forms.Timer gameTimer;
        private bool gameRunning, fluffyRunning;
        private bool leftoverTestPending;
        private ToolStatus toolStatus = new ToolStatus();
        private SegmentedChoice installedKind;   // 1.0.1: All · Extra · Installed (extra outfits / everything in the game folder)
        internal static readonly string[] InstalledKinds = { "All", "Extra", "Installed" };
        private string InstalledKindFilter { get { return installedKind == null ? "All" : InstalledKinds[installedKind.SelectedIndex]; } }
        private Panel gameBanner;
        private FlatButton bannerRemovePak;   // 2.4.0
        private Label gameBannerText;
        private FlatButton bannerReapply, bannerRestore, bannerLoader, bannerPak, moveUpButton, moveDownButton, reapplyButton;
        private List<GameModRow> gameRows = new List<GameModRow>();
        private GameFilesStatus gameStatus = new GameFilesStatus();
        private readonly HashSet<string> replacedSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private FlatButton settingsGeneralTab, settingsReplaceTab;
        private Panel settingsReplacePanel;
        private Control[] settingsGeneralParts = new Control[0];
        private Label settingsSubtitle;   // 2.4.0: shown on the General tab only
        private ComboBox settingsMethod;
        private CheckBox settingsCrashReporter, settingsSwapRepairs;
        private Label settingsMethodStatus, settingsToolStatus, settingsGameFiles, settingsStoreStatus, settingsFluffyStatus;
        private PaddedTextField settingsStore;
        private FlatButton loaderOnButton, loaderOffButton, installSlotsLoaderButton, installReFrameworkButton;
        private bool loadingReplaceSettings;
        private string gameMethod = "";
        // Loose-method replacements of natives files may change while SF6 runs (the engine re-checks).
        private bool RowChangesAllowed { get { return !busy && !FluffyBlocksPak && (!gameRunning || gameMethod == ReplaceMethods.Loose); } }
        // 2.1.0: an open Fluffy Mod Manager only blocks PAK edits (it edits the same PAK). Loose-method and
        // tool-mod changes and REFramework config edits go ahead; Fluffy's installed.ini is read again at each change.
        internal const string FluffyPakReason = "Close the external mod manager first: with the PAK method, both apps edit the same PAK.";
        private bool FluffyBlocksPak { get { return fluffyRunning && gameMethod == ReplaceMethods.Pak; } }
        private bool FluffyBlocks(IEnumerable<ReplaceMod> mods) { return FluffyBlocksPak && mods.Any(m => m != null && m.Files.Any(f => f.Path.StartsWith("natives/", StringComparison.OrdinalIgnoreCase))); }
        private bool FluffyBlocksPlan { get { return fluffyRunning && replacePlan != null && replacePlan.Method == ReplaceMethods.Pak && replacePlan.Files.Any(f => f.Native); } }

        internal bool IsReplaceMode { get { return installMode == InstallModes.Replace; } }
        private ReplaceEngine Engine() { replaceSettings = ReplaceSettings.Load(); var engine = new ReplaceEngine(config.GameRoot, AppConfig.DataDir, replaceSettings); AppConfig cfg = config; ReplaceSettings rs = replaceSettings; engine.FluffyProvider = delegate { return FluffyInstalls.Load(FluffyInstalls.DetectRoots(cfg.Libraries, rs.FluffyRoots)); }; return engine; }

        // ---- Header: Launch SF6 with a running indicator
        private Control BuildGameTools()
        {
            var tools = new FlowLayoutPanel { Dock = DockStyle.Right, Width = 270, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Padding = new Padding(0, 2, 0, 0), Margin = Padding.Empty };
            launchButton = Button("Launch SF6", false, 118, delegate { LaunchGame(); });
            launchButton.Height = 32; launchButton.Font = Palette.Small; launchButton.Margin = new Padding(8, 0, 0, 0);
            gameStateLabel = new Label { UseMnemonic = false, Width = 140, Height = 32, TextAlign = ContentAlignment.MiddleRight, Font = Palette.Small, ForeColor = Palette.Muted, Text = "Game closed", Margin = Padding.Empty };
            tools.Controls.Add(launchButton); tools.Controls.Add(gameStateLabel);
            gameTimer = new System.Windows.Forms.Timer { Interval = 1500 };
            gameTimer.Tick += delegate { PollGame(); };
            Shown += delegate { if (!Program.Headless) { PollGame(); gameTimer.Start(); FinishLeftoverTest(); if (screenshotFile == null) StartUpdates(); } string shot = (screenshotPage ?? "").Replace("min-", ""); SetInstallMode(shot == "replace" || shot == "health" || shot == "checking" ? InstallModes.Replace : replaceSettings.StartMode(), false); };
            FormClosing += delegate { gameTimer.Stop(); };
            Disposed += delegate { gameTimer.Dispose(); if (fluffyWatcher != null) fluffyWatcher.Dispose(); };
            Activated += delegate { CheckFluffyStamp(); CheckOpenPackageOnActivate(); };
            return tools;
        }

        private void PollGame()
        {
            if (IsDisposed) return;
            bool game = false, fluffy = false;
            try { game = GameProcesses.Probe.IsGameRunning(); fluffy = GameProcesses.Probe.IsFluffyRunning(); } catch (Exception ex) { Program.Log(ex); }
            bool changed = game != gameRunning || fluffy != fluffyRunning;
            gameRunning = game; fluffyRunning = fluffy;
            HistoryPoll(game);   // 2.4.1: game sessions and history repaints (MainFormHistory.cs; never throws)
            gameStateLabel.Text = game ? "● Game running" : fluffy ? "● Mod manager running" : "Game closed";
            gameStateLabel.ForeColor = game ? Palette.Green : fluffy ? Palette.Accent : Palette.Muted;
            launchButton.Enabled = !game && !busy;
            if (changed) { UpdateFooter(); ShowInstalledSelection(); UpdateBanner(); UpdateLibraryUninstall(); }
            if (leftoverTestPending && !gameRunning && !busy) FinishLeftoverTest();
            CheckFluffyStamp();
        }

        private void LaunchGame()
        {
            try { GameProcesses.Launcher.Launch(config.GameRoot); SetPageStatus(currentPage, "Starting SF6 through Steam…"); }
            catch (Exception ex) { Error(ex); }
        }

        // ---- Library: install mode toggle and the replacement target
        private Panel BuildModePanel()
        {
            modePanel = new Panel { Dock = DockStyle.Bottom, Height = 96, Padding = new Padding(0, 6, 0, 0) };
            Panel captionRow = new Panel { Dock = DockStyle.Top, Height = 22 };
            Label caption = Caption("INSTALL AS"); caption.Dock = DockStyle.Left; caption.Width = 78; caption.Height = 22;
            modeLockHint = new Label { UseMnemonic = false, Text = "", Dock = DockStyle.Fill, Font = Palette.Tiny, ForeColor = Palette.Muted, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
            captionRow.Controls.Add(modeLockHint); captionRow.Controls.Add(caption);
            var toggle = new Panel { Dock = DockStyle.Top, Height = 34, Padding = new Padding(0, 0, 0, 2) };
            modeToggle = new SegmentedToggle("Extra outfit", "Replace original") { Dock = DockStyle.Fill, AccessibleName = "Install as" };
            modeToggle.Descriptions[0] = "Extra outfit (Slotsloader): adds the outfit as a new slot; the original stays.";
            modeToggle.Descriptions[1] = "Replace original: overwrites the game's files. Also installs tool mods (REFramework scripts, plugins, Slotsloader).";
            modeToggle.SelectionChanged += delegate { SetInstallMode(modeToggle.SelectedIndex == 1 ? InstallModes.Replace : InstallModes.Slot, true); };
            toggle.Controls.Add(modeToggle);
            modeNotice = new Label { UseMnemonic = false, Dock = DockStyle.Fill, Font = Palette.Tiny, ForeColor = Palette.Muted, AutoEllipsis = true, Padding = new Padding(0, 4, 0, 0) };
            modeNotice.Click += delegate { if (IsReplaceMode || toolStatus == null) return; if (toolStatus.ReFrameworkOffer) OfferReFrameworkInstall(); else if (toolStatus.ReFramework && !toolStatus.SlotsLoader) OfferSlotsLoaderInstall(); };   // 1.0.8: REFramework first
            modePanel.Controls.Add(modeNotice); modePanel.Controls.Add(toggle); modePanel.Controls.Add(captionRow);
            return modePanel;
        }

        private Panel BuildReplacesPanel()
        {
            replacesPanel = new Panel { Dock = DockStyle.Bottom, Height = 70, Padding = new Padding(0, 6, 0, 0), Visible = false };
            Label caption = Caption("REPLACES"); caption.Dock = DockStyle.Fill;
            // 1.0.8 (the user): "Scroll to expand" at the right of the heading while notes are cut short; a click expands too.
            expandHint = new Label { UseMnemonic = false, Text = "Scroll to expand", Dock = DockStyle.Right, AutoSize = false, Font = Palette.Tiny, ForeColor = Palette.Muted, TextAlign = ContentAlignment.MiddleRight, Cursor = Cursors.Hand, Visible = false, AccessibleName = "Show all details", AccessibleRole = AccessibleRole.PushButton };
            expandHint.Click += delegate { ExpandInspectorDetails(); };
            expandHint.MouseEnter += delegate { expandHint.ForeColor = Palette.Text; }; expandHint.MouseLeave += delegate { expandHint.ForeColor = Palette.Muted; };
            var captionRow = new Panel { Dock = DockStyle.Top, Height = 20 };
            captionRow.Controls.Add(caption); captionRow.Controls.Add(expandHint);
            replacesLabel = new Label { UseMnemonic = false, Dock = DockStyle.Top, Height = 24, Font = Palette.Strong, ForeColor = Palette.Text, AutoEllipsis = true, Text = "Checking…" };
            replacesDetail = new Label { UseMnemonic = false, Dock = DockStyle.Top, Height = 20, Font = Palette.Tiny, ForeColor = Palette.Muted, AutoEllipsis = true };
            conflictView = new ConflictView { Dock = DockStyle.Fill }; conflictView.ShowReason = delegate(string why) { SetPageStatus(libraryPage, why, Palette.Accent); };
            replacesPanel.Controls.Add(conflictView); replacesPanel.Controls.Add(replacesDetail); replacesPanel.Controls.Add(replacesLabel); replacesPanel.Controls.Add(captionRow);
            replacesPanel.Resize += delegate { if (conflictView.Entries.Count > 0) conflictView.Invalidate(); };
            return replacesPanel;
        }

        internal void SetInstallMode(string mode, bool remember)
        {
            string next = mode == InstallModes.Replace ? InstallModes.Replace : InstallModes.Slot;
            bool changed = next != installMode;
            installMode = next;
            if (remember)
            {
                try { replaceSettings = ReplaceSettings.Load(); replaceSettings.LastMode = next; replaceSettings.Save(); } catch (Exception ex) { Program.Log(ex); }
            }
            if (changed && package != null)
            {
                var keep = selectedOrder.Where(id => { var c = package.Find(id); return c != null && c.HasFiles(IsReplaceMode); }).ToList();
                FillComponents(keep.Count > 0 ? keep : Composer.DefaultSelection(package, IsReplaceMode));
                if (!IsReplaceMode) editingReplacement = null;
            }
            ResetPlan();
            UpdateModeUi();
        }

        internal void UpdateModeUi()
        {
            if (modePanel == null) return;
            modeToggle.SelectedIndex = IsReplaceMode ? 1 : 0;
            bool forced = IsReplaceMode && forcedReplaceReason != null;
            modeToggle.Locked = forced;
            modeLockHint.Text = forced ? "· locked for this mod" : "";
            if (targetPanel != null) targetPanel.Visible = !IsReplaceMode;
            replacesPanel.Visible = IsReplaceMode;
            if (IsReplaceMode)
            {
                ReplaceEngine engine = null; string method = ReplaceMethods.Pak, detail = "";
                try { engine = Engine(); method = engine.CurrentMethod(engine.LoadState()); } catch (Exception ex) { detail = ex.Message; }
                LooseLoaderState loader = LooseLoader.State(config.GameRoot, out detail);
                // The health check and the banner say how to turn it on; the strip only gives the state.
                modeNotice.Text = (forced ? forcedReplaceReason + " · " : "Overwrites game files · ") + ReplaceMethods.Label(method) + (method == ReplaceMethods.Loose ? (loader == LooseLoaderState.Enabled ? " (on)" : " (off)") : "");
                if (forcedReplaceReason == PakModReason) modeNotice.Text = PakModReason;   // 2.5.6: the replace method plays no part
                modeNotice.ForeColor = Palette.Muted; modeNotice.Cursor = Cursors.Default;
                // 1.0.1: the external-manager lockout (ReplaceEngine.ExternalLockReason); the health check gives the full reason.
                string locked = null;
                try { if (engine != null && !IsLoaderPackage(package)) locked = engine.ExternalLockReason(); } catch (Exception ex) { Program.Log(ex); }
                if (locked != null) { modeNotice.Text = "Off while the external mod manager has mods installed · use Extra outfit"; modeNotice.ForeColor = Palette.Accent; }
                // 2.4.1: INSTALL AS already names the mode; "Health" opens the health-check report.
                installButton.Text = editingReplacement != null ? "Update" : "Install";
                previewButton.Text = "Health"; previewButton.AccessibleName = "Health check report";
                // The footer Uninstall shows when this source is installed as a replacement/tool mod (UpdateLibraryUninstall).
            }
            else
            {
                toolStatus = ToolStatus.Detect(config.GameRoot);
                // 1.0.8 (the user): REFramework comes first; Slotsloader is its plugin.
                string notice = ReFrameworkNotice(toolStatus); bool clickable = toolStatus.ReFrameworkOffer;
                if (notice == null) { notice = toolStatus.SlotsLoader ? "Adds the outfit as a new slot · Slotsloader is installed" : "Slotsloader isn't installed, so extra outfits won't load · click to install it"; clickable = !toolStatus.SlotsLoader; }
                bool fine = notice.StartsWith("Adds the outfit", StringComparison.Ordinal);
                modeNotice.Text = notice;
                modeNotice.ForeColor = fine ? Palette.Muted : Palette.Accent;
                modeNotice.Cursor = clickable ? Cursors.Hand : Cursors.Default;
                installButton.Text = "Install"; previewButton.Text = "Review files"; previewButton.AccessibleName = "Review files";
                // Extra outfit mode always shows Uninstall outfit (UpdateLibraryUninstall).
            }
            UpdateLibraryUninstall();
            UpdateFooter();
            UpdateDestination();
        }

        // 1.0.8: the Extra outfit notice for REFramework, or null when it is fine (then Slotsloader's notice shows).
        internal static string ReFrameworkNotice(ToolStatus tools)
        {
            if (!tools.GameFound) return null;
            if (!tools.ReFrameworkFile) return "REFramework isn't installed, so extra outfits won't load · click to install it";
            if (!tools.ReFramework) return "Start SF6 once so REFramework can finish setting up; extra outfits load after that";
            if (tools.ReFrameworkOutdated) return "REFramework from " + tools.ReFrameworkBuild.DateText + " is out of date · click to update it";
            return null;
        }

        private static bool IsLoaderPackage(ModPackage p) { return p != null && p.Components.SelectMany(c => c.RootAssets).Any(a => String.Equals(PakHash.Normalize(a.DestinationPath).TrimStart('/'), LoaderCostumePak.PluginPath, StringComparison.OrdinalIgnoreCase)); }
        private static bool IsToolPackage(ModPackage p) { return p != null && p.Components.All(c => c.Assets.Count == 0) && p.Components.Any(c => c.RootAssets.Count > 0) && !IsPakPackage(p); }
        // 2.5.6: only .pak files outside natives (a stage, UI or other PAK mod): added as the next patch PAK (ReplacePakMods.cs).
        // Extra outfit mode copies natives files only, so it can't install one; the notice says what Replace does, nothing more.
        internal const string PakModReason = "Adds the mod's .pak as the next patch PAK";
        private static bool IsPakPackage(ModPackage p) { return p != null && p.Components.All(c => c.Assets.Count == 0) && p.Components.Any(c => c.RootAssets.Count > 0) && p.Components.SelectMany(c => c.RootAssets).All(a => ReplaceEngine.IsModPak(a.DestinationPath)); }

        private void UpdateReplaceFooter()
        {
            string tip = ""; Color detailColor = Palette.Muted;
            if (missingRestoredOptions.Count > 0 && replacePlan == null && (plan == null || plan.Errors.Count == 0))
            {
                planSummary.Text = "Saved options are missing"; planSummary.ForeColor = Palette.Red;
                planDetails.Text = "Click Health to review the changed selection.";
            }
            else if (unresolvedChoices.Count > 0)
            {
                planSummary.Text = RequiredChoiceSummary(); planSummary.ForeColor = Palette.Accent;
                planDetails.Text = "Required before Install · click to jump to the first choice";
            }
            else if (ReviewPending)
            {
                planSummary.Text = "Checking " + package.Name + "…"; planSummary.ForeColor = Palette.Text;
                planDetails.Text = reviewStage.Length > 0 ? reviewStage : "Running the health check · Install enables when it's done";
            }
            else if (plan != null && plan.Errors.Count > 0)
            {
                planSummary.Text = plan.Errors.Count == 1 ? "1 required change" : plan.Errors.Count + " required changes"; planSummary.ForeColor = Palette.Red;
                planDetails.Text = plan.Errors[0] + " · View details"; tip = String.Join(Environment.NewLine, plan.Errors);
            }
            else if (replacePlan == null)
            {
                planSummary.Text = "Nothing to install yet"; planSummary.ForeColor = Palette.Text;
                planDetails.Text = "Select the main files and any options.";
            }
            else if (!replacePlan.CanInstall)
            {
                planSummary.Text = "Cannot install"; planSummary.ForeColor = Palette.Red;
                planDetails.Text = replacePlan.Errors.First() + " · View report"; tip = String.Join(Environment.NewLine, replacePlan.Errors);
            }
            else
            {
                int warnings = replacePlan.Items.Count(i => i.Level == "warn");
                planSummary.Text = "Ready to install · " + ReplacePlanner.N(replacePlan.Files.Count, "file"); planSummary.ForeColor = Palette.Green;
                string blocked = gameRunning && !(replacePlan.Method == ReplaceMethods.Loose && replacePlan.RootFiles == 0) ? "Close SF6 to install · " : FluffyBlocksPlan ? "Close the external mod manager to install (it edits the same PAK) · " : "";
                string conflict = FooterConflictText(replacePlan);
                ReplaceMod same = SameInstalledReplacement();
                if (conflict != null) { planDetails.Text = blocked + conflict + " · View report"; detailColor = Palette.Accent; }
                else if (same != null) { planDetails.Text = blocked + "Already installed as '" + same.Name + "' · Install updates it · View report"; detailColor = Palette.Accent; }
                else planDetails.Text = blocked + (replacePlan.VanillaReplaced == 1 ? "1 replaces a game file · " : replacePlan.VanillaReplaced + " replace game files · ") + replacePlan.Fixed + " auto-fixed" + (warnings > 0 ? " · " + ReplacePlanner.N(warnings, "warning") : "") + " · View report";
                tip = ReplacePlanner.Report(replacePlan);
            }
            planDetails.ForeColor = detailColor;
            if (replacePlan != null && !ReviewPending)
            {
                replacesLabel.Text = replacePlan.AffectsText;
                replacesDetail.Text = replacePlan.Kind == "Pak" ? String.Join(", ", replacePlan.Files.Select(x => x.Path)) + " (after the patch PAKs already there)" : replacePlan.Kind == "Tool" ? (replacePlan.RootFiles == 1 ? "1 file goes" : replacePlan.RootFiles + " files go") + " to the game folder" : (replacePlan.Method == ReplaceMethods.Pak ? (replacePlan.RowsToInvalidate == 1 ? "1 PAK entry" : replacePlan.RowsToInvalidate + " PAK entries") + " invalidated" : "No PAK change (loose file loader)") + (replacePlan.Conflicts > 0 ? " · " + replacePlan.Conflicts + " shared with other replacements" : "");
            }
            else { replacesLabel.Text = ReviewPending ? "Checking…" : "—"; replacesDetail.Text = editingReplacement != null ? "Updating '" + editingReplacement.Name + "' in place" : ""; }
            
            UpdateConflictView();
            UpdateReplaceButtons();
        }

        // An installed replacement from the same source with the same options (Install offers to update it).
        private ReplaceMod SameInstalledReplacement()
        {
            if (package == null || editingReplacement != null) return null;
            var selection = new HashSet<string>(selectedOrder, StringComparer.OrdinalIgnoreCase);
            return gameRows.Where(r => r.Mod != null).Select(r => r.Mod).FirstOrDefault(m => String.Equals(m.SourcePath, package.SourcePath, StringComparison.OrdinalIgnoreCase) && selection.SetEquals(m.SelectedIds ?? new List<string>()));
        }

        private void UpdateReplaceButtons()
        {
            bool ready = !busy && !ReviewPending && replacePlan != null && replacePlan.CanInstall && missingRestoredOptions.Count == 0 && unresolvedChoices.Count == 0;
            bool whileRunning = replacePlan != null && replacePlan.Method == ReplaceMethods.Loose && replacePlan.RootFiles == 0;
            installButton.Enabled = ready && (!gameRunning || whileRunning) && !FluffyBlocksPlan;
            previewButton.Emphasis = false;
        }
        private void CaptureHealthReport()
        {
            if (replacePlan == null) { Close(); return; }
            using (Form report = CreateHealthReport(replacePlan))
            {
                // PrintWindow keeps the dark DWM title bar in the capture (DrawToBitmap paints a light classic frame).
                report.Opacity = 0; report.StartPosition = FormStartPosition.CenterScreen; report.Show(); report.PerformLayout(); Application.DoEvents();
                string folder = Path.GetDirectoryName(screenshotFile); if (!String.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
                using (Bitmap bitmap = CaptureWindowImage(report)) bitmap.Save(screenshotFile, System.Drawing.Imaging.ImageFormat.Png);
                report.Close();
            }
            Close();
        }

        private string ReplacementName()
        {
            if (editingReplacement != null) return editingReplacement.Name;
            var layers = plan == null ? new List<ModComponent>() : plan.Layers.Where(c => !Composer.IsBase(c) && !String.Equals(c.Name, package.Name, StringComparison.OrdinalIgnoreCase)).ToList();
            return package.Name + (layers.Count > 0 && layers.Count <= 3 ? " · " + String.Join(" + ", layers.Select(c => c.Name)) : "");
        }

        // 2.1.0: the loose file loader is optional. Asked only when an install puts natives files in place with
        // the loose method while the loader is off: Turn on (recommended, default), Not now (installs with the
        // loader off; the banner and health check keep saying so) and "Don't ask again" (for Not now; undone in
        // Settings). Escape or the close box cancels the install. Tool-only installs never ask.
        internal const string LooseLoaderCaption = "Loose file loader";
        // 2.4.1: with the Automatic method, Not now installs with the Fluffy PAK method (it needs no loader).
        internal const string LooseLoaderPromptText = "Turn on REFramework's loose file loader?\n\nTurn on installs loose files (only LooseFileLoader_Enabled changes; the config is backed up first). Not now uses the PAK method instead, which needs no loader.";
        // With the method set to the loose file loader in Settings, Not now installs loose anyway; the banner says SF6 ignores them.
        internal const string LooseLoaderExplicitText = "Turn on REFramework's loose file loader?\n\nYour Settings use the loose file loader, so SF6 ignores replaced files while it's off. Only LooseFileLoader_Enabled changes (the config is backed up first). Not now installs anyway.";
        internal static readonly string[] LooseLoaderButtons = { "&Turn on (recommended)", "&Not now" };
        internal const string LooseLoaderDontAsk = "&Don't ask again";

        // 2.4.1: the Presets window restores replacements too (scope Extra outfits · Replacements · Both). It reports
        // inline; afterwards the Installed page (outfits and replacements) is refreshed with what was restored.
        private void ShowPresetsWindow()
        {
            bool outfits, replacements;
            using (var form = new OutfitSetsForm(config.CostumeRoot, Engine, EnsureLooseLoader))
            {
                form.ShowDialog(this);
                outfits = form.Changed && !form.ReplacementsChanged || form.Changed && form.OutfitsChanged;
                replacements = form.ReplacementsChanged;
            }
            if (!outfits && !replacements) return;
            RefreshInstalled(replacements && !outfits ? "Restored replacements. Restart SF6 to load them." : replacements ? "Restored outfits and replacements. Restart SF6 to load them." : "Restored outfits. Restart SF6 to load them.");
        }

        // Returns false when the install must not go ahead (cancelled, or the loader could not be turned on).
        // usePak: install with the Fluffy PAK method (Automatic method, loader left off), converting older loose installs.
        private bool EnsureLooseLoader(ReplaceEngine engine, bool installsNatives) { bool usePak; return EnsureLooseLoader(engine, installsNatives, out usePak); }
        private bool EnsureLooseLoader(ReplaceEngine engine, bool installsNatives, out bool usePak)
        {
            usePak = false;
            if (!installsNatives) return true;
            ReplaceState current = engine.LoadState();
            string detail; LooseLoaderState state = LooseLoader.State(config.GameRoot, out detail);
            if (state == LooseLoaderState.Enabled) return true;
            // Without REFramework there is nothing to ask (PAK); existing PAK installs stay PAK.
            if (state == LooseLoaderState.ReFrameworkMissing) { usePak = engine.CurrentMethod(current) == ReplaceMethods.Loose; return true; }
            if (current.Mods.Count > 0 && engine.CurrentMethod(current) != ReplaceMethods.Loose) return true;
            // 2.4.1: no question any more. 2.4.4: never install loose files while the loader is off, even with an explicit
            // loose setting (SF6 ignored a whole Sakura install that way): the install uses the Fluffy PAK method, which
            // works without the loader, and the status line says so.
            usePak = true;
            LastLooseLoaderChoice = 1;
            return true;
        }
        internal int LastLooseLoaderChoice = -2;   // tests: -2 not asked, -1 cancelled, 0 Turn on, 1 Not now
        private void InstallReplacement()
        {
            if (busy || ReviewPending || replacePlan == null || !replacePlan.CanInstall || plan == null) return;
            ReplaceEngine engine = Engine();
            LastLooseLoaderChoice = -2;
            bool usePak;
            if (!EnsureLooseLoader(engine, replacePlan.Files.Any(f => f.Native), out usePak)) return;
            // Older loose installs move to the PAK method with this one (the engine keeps one method for all).
            bool convert = usePak && engine.LoadState().Mods.Count > 0 && engine.CurrentMethod(engine.LoadState()) == ReplaceMethods.Loose;
            // 2.4.4: an explicit loose setting would still pick loose for a first install; this install uses PAK (the saved
            // setting stays as chosen).
            bool overridden = usePak && engine.Settings.Method == ReplaceMethods.Loose;
            if (overridden)
            {
                ReplaceSettings pakSettings = ReplaceSettings.Load(); pakSettings.Method = ReplaceMethods.Pak;
                engine = new ReplaceEngine(config.GameRoot, AppConfig.DataDir, pakSettings) { FluffyProvider = engine.FluffyProvider };
            }
            string replaceId = editingReplacement == null ? null : editingReplacement.Id;
            if (replaceId == null) replaceId = PendingLoaderReplaceId();   // 2.4.0: a SlotsLoader update replaces the recorded one in place
            // 2.4.1: another variant of an installed mod (same source) is a switch: PrepareAndInstall replaces it in place
            // and folds any duplicates into one record, without asking (the user ended up with two "Aki Bare Legs Bundle"
            // records of 34 and 32 files: 2.4.0 matched only identical options and otherwise added a second one silently).
            // A SlotsLoader zip with another file name still updates the installed SlotsLoader in place.
            if (replaceId == null && IsSlotsLoaderPackage(package) && !engine.LoadState().Mods.Any(m => ReplacePlanner.SameSource(m.SourcePath, package.SourcePath)))
            {
                ReplaceMod loader = engine.LoadState().Mods.FirstOrDefault(m => LoaderCostumePak.IsLoaderMod(m) || System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(m.SourcePath ?? ""), @"slots.?loader", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
                if (loader != null) replaceId = loader.Id;
            }
            CompositionPlan composition = plan; string sevenZip = config.SevenZipPath; string name = ReplacementName(); string game = config.GameRoot;
            Work("Installing replacement…", delegate
            {
                if (convert) engine.SwitchMethod(ReplaceMethods.Pak);
                ReplacePlan prepared; ReplaceMod mod = ReplacePlanner.PrepareAndInstall(composition, sevenZip, engine, name, false, replaceId, out prepared); if (LoaderCostumePak.IsLoaderMod(mod)) UnownedSlotsFlag.Ensure(game); return mod;
            }, delegate(ReplaceMod mod)
            {
                editingReplacement = null; UpdateModeUi(); LoaderUpdateInstalled();
                string method = usePak && mod.Files.Any(f => f.Path.StartsWith("natives/", StringComparison.OrdinalIgnoreCase)) ? (overridden ? " · PAK method used: loose file loader off" : " · PAK method (loose file loader off)") : "";
                RefreshInstalled("✓ Installed " + mod.Name + " · " + ReplacePlanner.N(mod.Files.Count, "file") + method + ". " + (gameRunning ? "Restart SF6 to see it." : "Start SF6 to see it."));
            });
        }

        private static bool IsSlotsLoaderPackage(ModPackage p) { return p != null && p.Components.Any(c => c.RootAssets.Any(a => a.DestinationPath.EndsWith("SF6_CostumeSlotsNative.dll", StringComparison.OrdinalIgnoreCase) || a.DestinationPath.Equals("amd_ags_x64.dll", StringComparison.OrdinalIgnoreCase))); }

        // ---- SlotsLoader offer (Extra outfit mode needs the loader installed)
        // 2.3.1: always the pinned build (LoaderRelease.Pin): a library file with its SHA-256, else the verified copy in
        // Data\downloads, else (after asking) a verified download from the GitHub release. Nothing is installed here:
        // the package opens in Replace original mode and the user installs it after the health check.
        internal const string LoaderToolText = "It adds amd_ags_x64.dll (a proxy), amd_ags_x64_real.dll and the Slotsloader plugin and data to the game folder; the game's own amd_ags_x64.dll is backed up and restored on uninstall. It opens in Replace original mode: review the health check, then click Install.";
        internal LoaderFile LastLoaderFile;   // tests
        private void OfferSlotsLoaderInstall()
        {
            // 1.0.1: a click while another task runs used to do nothing at all; it says why now.
            if (busy) { SetPageStatus(currentPage, "Another task is running. Click Install Slotsloader again when it finishes.", Palette.Accent); return; }
            LoaderPin pin = CurrentInstallPin();
            List<string> libraries = config.Libraries.ToList(); string downloads = LoaderRelease.DownloadDir;
            string shown = UpdateCheck.ShortVersion(pin.Version);
            Work("Looking for Slotsloader " + shown + " in your libraries…", delegate { return LoaderRelease.FindLocal(pin, libraries, downloads); }, delegate(LoaderFile local)
            {
                // 2.4.1: the click is the consent: no questions. The verified zip opens in Replace original; the health
                // check and Install follow as for any tool mod.
                if (local != null) { OpenLoader(local); return; }
                Work("Downloading Slotsloader " + shown + "…", delegate { return LoaderRelease.Download(pin, downloads); }, OpenLoader);
            });
        }
        // ---- 1.0.8 REFramework offer: the pinned nightly (ReFramework.cs) from Data\downloads or a verified download, opened
        // in Replace original like Slotsloader; Install puts dinput8.dll in the game folder (an existing one is backed up and
        // put back on uninstall) and replaces the REFramework this app installed before.
        internal LoaderFile LastReFrameworkFile;   // tests
        private void OfferReFrameworkInstall()
        {
            if (busy) { SetPageStatus(currentPage, "Another task is running. Click again when it finishes.", Palette.Accent); return; }
            ReFrameworkPin pin = ReFrameworkRelease.Pin; string downloads = LoaderRelease.DownloadDir;
            Work("Getting " + pin.Label + "…", delegate { return ReFrameworkRelease.Fetch(pin, downloads); }, delegate(LoaderFile file)
            {
                LastReFrameworkFile = file;
                Program.LogLine(pin.Label + " from the " + file.SourceLabel + ": " + file.Path + " · SHA-256 " + file.Sha256);
                SetInstallMode(InstallModes.Replace, false);
                ShowPage(libraryPage);
                LoadPackage(new LibraryEntry { SourcePath = file.Path, Name = pin.Label });
                SetPageStatus(libraryPage, pin.Label + " is open below: click Install to add it to the game.", Palette.Accent);
            });
        }

        private void OpenLoader(LoaderFile file)
        {
            LastLoaderFile = file;
            Program.LogLine("Slotsloader from the " + file.SourceLabel + ": " + file.Path + " · SHA-256 " + file.Sha256);
            LibraryEntry entry = library.FirstOrDefault(e => String.Equals(e.SourcePath, file.Path, StringComparison.OrdinalIgnoreCase)) ?? new LibraryEntry { SourcePath = file.Path, Name = Path.GetFileNameWithoutExtension(file.Path) };
            SetInstallMode(InstallModes.Replace, false);
            ShowPage(libraryPage);
            LoadPackage(entry);
            // 1.0.1: no popup (popup rule), so the next step is said where the user looks.
            SetPageStatus(libraryPage, "Slotsloader " + UpdateCheck.ShortVersion(CurrentInstallPin().Version) + " is open below: click Install to add it to the game.", Palette.Accent);
        }

        // ---- Leftover "Test in game" (the feature was removed in 2.3.0)
        // A test install left by an older version (Data\test-session.json) is kept as a normal install, without a
        // prompt: the replacement loses its test mark, REFramework's logging keys go back, the record is deleted and
        // the outcome logged. Waits while SF6 runs (REFramework rewrites its config on exit) or work is going on.
        private void FinishLeftoverTest()
        {
            TestSessionRecord record = TestSession.Load(AppConfig.DataDir);
            if (record == null) { leftoverTestPending = false; return; }
            if (busy || gameRunning || !Directory.Exists(config.GameRoot)) { leftoverTestPending = true; return; }
            leftoverTestPending = false;
            ReplaceEngine engine = Engine();
            Work("Keeping a test install from an earlier version…", delegate { return TestSession.FinishLeftover(engine, record); }, delegate(string message)
            {
                Program.LogLine("Test in game (removed in 2.3.0): " + message);
                RefreshInstalled("✓ " + message);
            });
        }
        // ---- Installed page: replacements, tool mods and Fluffy rows
        private sealed class GameRowsResult { internal List<GameModRow> Rows = new List<GameModRow>(); internal GameFilesStatus Status = new GameFilesStatus(); internal string Recovery, Method = "", FluffyStamp = ""; internal HashSet<string> Sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase); internal List<string> FluffyRoots = new List<string>(); }

        private GameRowsResult LoadGameRows(bool allowRecovery)
        {
            var result = new GameRowsResult();
            if (!Directory.Exists(config.GameRoot)) return result;
            ReplaceEngine engine = Engine();
            try
            {
                if (allowRecovery && engine.JournalPending && !GameProcesses.Probe.IsGameRunning() && !GameProcesses.Probe.IsFluffyRunning()) result.Recovery = engine.Recover();
            }
            catch (Exception ex) { Program.Log(ex); result.Recovery = ex.Message; }
            // 2.1.0: after the app folder was moved, the corrected Data paths are saved once (Data records only).
            try
            {
                string moved = allowRecovery ? engine.PersistRelocation() : null;
                if (moved != null && String.IsNullOrEmpty(result.Recovery)) result.Recovery = moved;
            }
            catch (Exception ex) { Program.Log(ex); }
            // 2.4.2: a recoverable copy whose move into .removed failed is tried again (start-up and every refresh),
            // and .removed copies past the keep rules are deleted (never one an Undo here still needs).
            // 1.0.1: the keep rule is "only what a live Undo needs": nothing else can use a copy any more.
            if (allowRecovery)
            {
                try { string trash = engine.RetryTrash(); if (trash != null && String.IsNullOrEmpty(result.Recovery)) result.Recovery = trash; }
                catch (Exception ex) { Program.Log(ex); }
                try { engine.DeleteRemovedExcept(ProtectedRemovedFolders()); }
                catch (Exception ex) { Program.Log(ex); }
                SwapRepairFlag.Sync(engine.GameRoot, engine.Settings.SwapRepairs);   // 1.0.5: Slotsloader follows the setting
                // 1.0.1: a gap in the patch PAK row (any tool's) is filled at start-up and every refresh, with SF6 and
                // the external mod manager closed.
                try
                {
                    if (!GameProcesses.Probe.IsGameRunning() && !GameProcesses.Probe.IsFluffyRunning())
                        using (engine.Lock()) { string gaps = ReplaceEngine.GapText(engine.FillPatchGaps()); if (gaps.Length > 0) result.Recovery = String.IsNullOrEmpty(result.Recovery) ? gaps : result.Recovery + " " + gaps; }
                }
                catch (Exception ex) { Program.Log(ex); }
            }
            try
            {
                ReplaceState state = engine.LoadState();
                result.Method = engine.CurrentMethod(state);
                var owners = state.Deployed.GroupBy(d => d.Owner).ToDictionary(g => g.Key, g => g.Count());
                GameReference reference = GameReference.Tables(config.GameRoot);
                var pathSets = state.Mods.ToDictionary(m => m.Id, m => new HashSet<string>(m.Files.Select(f => f.Path), StringComparer.OrdinalIgnoreCase));
                var slotSets = state.Mods.ToDictionary(m => m.Id, m => ReplacePlanner.SlotAreas(m.Files.Select(f => f.Path)));
                for (int i = 0; i < state.Mods.Count; i++)
                {
                    ReplaceMod m = state.Mods[i];
                    int owned; owners.TryGetValue(m.Id, out owned);
                    var row = new GameModRow { Kind = String.IsNullOrEmpty(m.Kind) ? "Replacement" : m.Kind, Mod = m, Priority = i + 1, Owned = owned };
                    for (int j = 0; j < state.Mods.Count; j++)
                    {
                        if (j == i) continue;
                        ReplaceMod o = state.Mods[j];
                        var shared = o.Files.Select(f => f.Path).Where(pathSets[m.Id].Contains).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
                        if (shared.Count > 0) row.Shares.Add(new RowShare { Other = o.Name, OtherPriority = j + 1, OtherEnabled = o.Enabled, Count = shared.Count, ThisWins = i < j, Paths = shared });
                    }
                    row.Conflicts = row.Shares.Where(s => s.OtherEnabled).Sum(s => s.Count);
                    // Outfits (and other areas) changed by several enabled mods: the stacking is visible on the row.
                    if (m.Enabled)
                        foreach (string area in slotSets[m.Id].OrderBy(a => a, StringComparer.OrdinalIgnoreCase))
                        {
                            var mods = state.Mods.Where(x => x.Enabled && slotSets[x.Id].Contains(area)).ToList();
                            if (mods.Count < 2) continue;
                            bool overlap = mods.Any(a => mods.Any(b => a != b && b.Files.Any(f => pathSets[a.Id].Contains(f.Path))));
                            row.Stacks.Add(ReplacePlanner.ShortArea(ReplacePlanner.AreaLabel(area, reference)) + " · " + mods.Count + " mods · " + (overlap ? "'" + mods[0].Name + "' wins" : "no shared files"));
                        }
                    result.Rows.Add(row);
                    if (m.Enabled && !String.IsNullOrEmpty(m.SourcePath)) result.Sources.Add(m.SourcePath);
                }
                FluffyInstalls fluffyInstalls = engine.Fluffy();
                foreach (FluffyMod f in fluffyInstalls.Mods) result.Rows.Add(new GameModRow { Kind = "Fluffy", Fluffy = f });
                result.FluffyRoots = fluffyInstalls.Roots.ToList(); result.FluffyStamp = fluffyInstalls.Stamp ?? FluffyInstalls.StampOf(fluffyInstalls.Roots);
                result.Status = engine.Status();
            }
            catch (Exception ex) { Program.Log(ex); result.Status.Details.Add(ex.Message); }
            // 2.4.2: every Fluffy root is listed and watched even when the Manager's own record can't be read.
            if (result.FluffyRoots.Count == 0)
            {
                try
                {
                    FluffyInstalls fi = engine.Fluffy();
                    result.FluffyRoots = fi.Roots.ToList(); result.FluffyStamp = fi.Stamp ?? FluffyInstalls.StampOf(fi.Roots);
                    if (!result.Rows.Any(r => r.Fluffy != null)) foreach (FluffyMod fm in fi.Mods) result.Rows.Add(new GameModRow { Kind = "Fluffy", Fluffy = fm });
                }
                catch (Exception ex) { Program.Log(ex); }
            }
            return result;
        }

        private void ApplyGameRows(GameRowsResult result)
        {
            gameRows = result.Rows; gameStatus = result.Status; gameMethod = result.Method;
            WatchFluffy(result.FluffyRoots, result.FluffyStamp);
            replacedSources.Clear(); foreach (string s in result.Sources) replacedSources.Add(s);
            installStates.Clear();
            UpdateBanner();
            // The installed set changed: the automatic check and duplicate notes are re-evaluated.
            if (package != null && IsReplaceMode) RequestReview(ReviewDelay);
            UpdateFooter();
            if (!String.IsNullOrEmpty(result.Recovery)) SetSharedStatus(result.Recovery, Palette.Accent);
        }

        private Panel BuildGameBanner()
        {
            // 2.4.1: the compact NoticeBanner (one slim row, accent stripe, regular text, compact buttons).
            var banner = new NoticeBanner { Visible = false };
            gameBanner = banner;
            bannerRestore = banner.Add(Button("Restore original game files", false, 196, delegate { RestoreOriginalGameFiles(); })); bannerRestore.Danger = true;
            bannerReapply = banner.Add(Button("Reapply", false, 84, delegate { ReapplyAll(); }));
            bannerPak = banner.Add(Button("Use PAK method", false, 130, delegate { UsePakMethod(); })); bannerPak.AccessibleName = "Use the PAK method for the installed replacements";
            bannerLoader = banner.Add(Button("Turn loose file loader on", false, 190, delegate { SetLooseLoader(true); }));
            bannerRemovePak = banner.Add(Button("Remove it", false, 90, delegate { RemoveOrphanedCostumePak(); })); bannerRemovePak.AccessibleName = "Remove the leftover Slotsloader costume pak";
            bannerRepair = banner.Add(Button("Repair", false, 76, delegate { RepairGameEntries(); })); bannerRepair.AccessibleName = "Repair the game entries another tool left invalidated";   // 2.4.2 (MainFormPakRepair.cs)
            gameBannerText = banner.Message;
            // 2.4.0 (no tooltips): clicking the banner text lists every detail.
            gameBannerText.Cursor = Cursors.Hand;
            gameBannerText.Click += delegate { if (gameStatus != null && gameStatus.Details.Count > 0) ThemedMessageBox.Show(this, gameStatus.Summary + "\n\n" + String.Join("\n", gameStatus.Details.Select(d => "• " + d)), "Game files", MessageBoxButtons.OK, MessageBoxIcon.Information); };
            return gameBanner;
        }

        private void UpdateBanner()
        {
            if (gameBanner == null) return;
            string text = gameStatus == null ? "" : gameStatus.Summary;
            gameBanner.Visible = text.Length > 0;
            gameBannerText.Text = text; gameBannerText.AccessibleDescription = gameStatus == null ? "" : String.Join("; ", gameStatus.Details);
            bool idle = !busy && !gameRunning;
            bannerReapply.Visible = gameStatus != null && (gameStatus.NeedsReapply || gameStatus.BaseChanged);
            bannerLoader.Visible = gameStatus != null && gameStatus.LooseLoaderOff;
            bannerPak.Visible = bannerLoader.Visible; bannerPak.Enabled = idle && !fluffyRunning;   // 2.4.1: or convert the loose installs
            bannerRemovePak.Visible = gameStatus != null && gameStatus.OrphanCostumePaks.Count > 0;
            bannerRemovePak.Enabled = idle;
            UpdateRepairButton();   // 2.4.2
            // 2.4.1: Restore original game files only where it fits: game files changed or SF6 was updated, with
            // replacements or tool mods to restore. It is not the answer to the loose file loader or a leftover pak
            // notice, and it stays available in Settings → Game files & replacements.
            bannerRestore.Visible = gameStatus != null && (gameStatus.NeedsReapply || gameStatus.BaseChanged) && (gameRows ?? new List<GameModRow>()).Any(r => r.Mod != null);
            bannerLoader.Enabled = idle;
            bannerReapply.Enabled = bannerRestore.Enabled = idle && !FluffyBlocksPak;
        }

        private GameModRow GetGameRowSelection() { return installedList.SelectedItems.Count == 1 ? installedList.SelectedItems[0].Tag as GameModRow : null; }

        private IEnumerable<GameModRow> FilterGameRows()
        {
            if (InstalledKindFilter == "Extra") return new GameModRow[0];
            IEnumerable<GameModRow> rows = gameRows;
            if (installedSearch != null && installedSearch.Text.Trim().Length > 0) { InstalledQuery searchQuery = ParseInstalledSearch(); rows = rows.Where(r => GameRowMatchesSearch(searchQuery, r)).ToList(); }
            if (installedCharacters.Count > 0) rows = rows.Where(r => r.Mod != null && installedCharacters.Any(who => (r.Mod.Affects ?? "").IndexOf(who, StringComparison.OrdinalIgnoreCase) >= 0));
            string state = installedState == null ? "" : Convert.ToString(installedState.SelectedItem);
            rows = rows.Where(r => state == "Enabled" ? (r.Mod == null || r.Mod.Enabled) : state == "Disabled" ? (r.Mod != null && !r.Mod.Enabled) : state == "Managed" ? r.Mod != null : state == "Manual" ? r.Fluffy != null : true);
            return rows;
        }
        private bool ShowOutfitRows { get { return InstalledKindFilter != "Installed"; } }

        private void DrawGameRow(DrawListViewSubItemEventArgs e, GameModRow row)
        {
            bool selected = e.Item.Selected; Rectangle r = e.Bounds;
            using (Brush bg = new SolidBrush(selected ? Palette.Selected : Palette.Surface)) e.Graphics.FillRectangle(bg, r);
            Color rail = row.Fluffy != null ? Palette.Border : row.Mod.Enabled ? Palette.Accent : Palette.Border;
            using (Brush b = new SolidBrush(rail)) e.Graphics.FillRectangle(b, r.Left + Px(1), r.Top + Px(12), Px(3), r.Height - Px(24));
            TextRenderer.DrawText(e.Graphics, row.Name, Palette.Strong, new Rectangle(r.Left + Px(14), r.Top + Px(10), r.Width - Px(25), Px(24)), Palette.Text, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            string sub = row.Fluffy != null ? "External mod manager   ·   " + ReplacePlanner.N(row.Fluffy.Files.Count, "file") + (row.Fluffy.CopiedFiles ? "" : "   ·   PAK install") + "   ·   uninstall it there"
                : (SourceChanged(row.Mod.SourcePath) && !LoaderCostumePak.IsLoaderMod(row.Mod) ? "Source changed   ·   " : "") + row.KindLabel + "   ·   #" + row.Priority + "   ·   " + (row.Mod.Enabled ? "Enabled" : "Disabled") + (row.Conflicts > 0 ? "   ·   " + ReplacePlanner.N(row.Conflicts, "shared file") : "") + (row.Mod.Test ? "   ·   Test" : "") + (row.Stacks.Count > 0 ? "   ·   " + row.Stacks[0] : String.IsNullOrEmpty(row.Mod.Affects) || row.Kind == "Tool" ? "" : "   ·   " + row.Mod.Affects);
            Rectangle meta = DrawSourceGoneNote(e.Graphics, row.Mod != null && !LoaderCostumePak.IsLoaderMod(row.Mod) ? row.Mod.SourcePath : null, new Rectangle(r.Left + Px(14), r.Top + Px(36), r.Width - Px(25), Px(20)));   // 2.4.1 Delete: muted "Source deleted"
            TextRenderer.DrawText(e.Graphics, sub, Palette.Small, meta, row.Fluffy != null ? Palette.Muted : row.Mod.Enabled ? Palette.Accent : Palette.Muted, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);   // 1.0.1: no "played …" (the user)
            DrawUsageAfter(e.Graphics, sub, InstalledAgo(row), Palette.Small, meta);   // 1.0.1: when it was installed, when it fits
        }

        private void ShowGameRow(GameModRow row)
        {
            installedTitle.Text = row.Name; 
            var chips = new List<ChipStrip.Chip> { row.Fluffy != null ? ChipStrip.Quiet("External mod manager") : ChipStrip.Neutral(row.KindLabel) };
            if (row.Mod != null && !LoaderCostumePak.IsLoaderMod(row.Mod)) AddAuthorChip(chips, row.Mod.SourcePath);   // 2.5.9
            if (row.Mod != null && SourceChanged(row.Mod.SourcePath)) chips.Add(ChipStrip.Warn("Source changed"));   // 2.4.1: Reinstall applies the edit
            string sourceGone = row.Mod != null && !LoaderCostumePak.IsLoaderMod(row.Mod) ? SourceGoneNote(row.Mod.SourcePath) : null;
            if (sourceGone != null) chips.Add(ChipStrip.Quiet(sourceGone));   // 2.4.1 Delete: still installed, its source is gone
            if (row.Mod != null)
            {
                chips.Add(ChipStrip.Neutral("#" + row.Priority));
                chips.Add(row.Mod.Enabled ? ChipStrip.Good("Enabled") : ChipStrip.Quiet("Disabled"));
                if (row.Conflicts > 0) chips.Add(ChipStrip.Warn(ReplacePlanner.N(row.Conflicts, "shared file")));
                else if (row.Stacks.Count > 0) chips.Add(ChipStrip.Quiet("Same outfit as another mod"));
                if (row.Mod.Test) chips.Add(ChipStrip.Warn("Test"));
            }
            installedChips.SetChips(chips);
            installedChips.Cursor = row.HasConflicts ? Cursors.Hand : Cursors.Default;
            installedDetail.Text = row.Mod != null ? row.Mod.SourcePath : row.Fluffy.Root; installedPathRow.Visible = true;
            installedSectionCaption.Text = row.Fluffy != null ? "INSTALLED BY AN EXTERNAL MOD MANAGER" : "FILES AND FIXES";
            var lines = new List<string>();
            if (row.Fluffy != null)
            {
                lines.Add("Installed by an external mod manager (" + row.Fluffy.IniPath + "). The Manager never overwrites its files, and while that manager has mods installed only Extra outfit and Slotsloader install here.");   // 1.0.1
                string blocker = row.Fluffy.UninstallBlocker;
                // 2.4.2: PAK installs too (FluffyPakUninstall.cs).
                if (blocker == null && row.Fluffy.PakInstall) lines.Add("PAK install (" + row.Fluffy.InstallType + "): Uninstall also restores the game entries it invalidated (only ones the Manager can prove; otherwise nothing changes) and removes its patch PAK, leaving an empty placeholder where another PAK sits above.");
                lines.Add(blocker ?? "Uninstall works like the manager's own: it deletes the mod's files (except those another of its mods lists), puts back its backups and _FMMBACKUP renames, and removes the empty folders it created and its installed.ini entry (installed.ini is backed up first). Close the external mod manager first. A copy is kept in gear → Recover removed outfits.");
                lines.Add(""); lines.AddRange(row.Fluffy.Files.Take(60));
                if (row.Fluffy.Files.Count > 60) lines.Add("… " + (row.Fluffy.Files.Count - 60) + " more");
            }
            else
            {
                ReplaceMod m = row.Mod;
                if (row.HasConflicts)
                {
                    lines.Add("SHARED FILES AND SAME OUTFIT · higher in the list wins; Move up/down changes it");
                    foreach (RowShare s in row.Shares)
                    {
                        lines.Add("• '" + s.Other + "' (#" + s.OtherPriority + ", " + (s.OtherEnabled ? "enabled" : "disabled") + ") · " + ReplacePlanner.N(s.Count, "shared file") + " · " + (!s.OtherEnabled ? "it is disabled, so this mod's copy is used" : !m.Enabled ? "this mod is disabled, so '" + s.Other + "' is used" : s.ThisWins ? "this mod wins" : "'" + s.Other + "' wins"));
                        foreach (string p in s.Paths.Take(12)) lines.Add("     " + p);
                        if (s.Paths.Count > 12) lines.Add("     … " + (s.Paths.Count - 12) + " more");
                    }
                    foreach (string stack in row.Stacks) lines.Add("• " + stack);
                    lines.Add("");
                }
                lines.Add((m.Enabled ? "Installed in the game folder" : "Disabled: stored by the Manager, not in the game folder") + " · " + ReplacePlanner.N(m.Files.Count, "file") + " · " + row.Owned + " deployed" + (row.HasConflicts ? "" : " (higher in the list wins shared files)") + ".");
                if (!String.IsNullOrEmpty(m.Affects) && row.Kind != "Tool") lines.Add("Replaces: " + m.Affects);
                foreach (string note in m.Notes.Take(12)) lines.Add("• " + note);
                lines.Add("");
                foreach (ModFile f in m.Files.Take(80)) lines.Add(f.Path + (String.IsNullOrEmpty(f.Fixes) ? "" : "  — " + f.Fixes));
                if (m.Files.Count > 80) lines.Add("… " + (m.Files.Count - 80) + " more");
            }
            installedHelp.Text = String.Join(Environment.NewLine, lines); installedHelp.Select(0, 0);
        }

        // 2.4.0: the details card never shows a selection on its own: no select-all on keyboard focus (the edit
        // control does that when tabbed into), nothing selected when a row is picked or the chip is clicked.
        // Text the user selects with the mouse stays selected and can be copied.
        internal int HelpFirstVisibleLine() { return installedHelp.IsHandleCreated ? (int)SendMessage(installedHelp.Handle, 0x00CE, IntPtr.Zero, IntPtr.Zero) : 0; }   // EM_GETFIRSTVISIBLELINE
        private void ScrollHelpToLine(int line)
        {
            if (!installedHelp.IsHandleCreated) return;
            SendMessage(installedHelp.Handle, 0x00B6, IntPtr.Zero, (IntPtr)(line - HelpFirstVisibleLine()));   // EM_LINESCROLL
        }
        // The physical mouse state; checks replace it so a button held on the real desktop can't change a hidden test.
        internal static Func<MouseButtons> PressedMouseButtons = delegate { return Control.MouseButtons; };
        private void WireHelpSelection()
        {
            installedHelp.HideSelection = true;
            // TextBox.OnGotFocus selects everything on keyboard focus; undo that right after (a click or drag sets its own selection).
            EventHandler keyboardFocus = delegate
            {
                if (PressedMouseButtons() != MouseButtons.None) return;
                installedHelp.Select(0, 0);
                if (IsHandleCreated) BeginInvoke((MethodInvoker)delegate { if (!installedHelp.IsDisposed && PressedMouseButtons() == MouseButtons.None) installedHelp.Select(0, 0); });
            };
            installedHelp.Enter += keyboardFocus; installedHelp.GotFocus += keyboardFocus;
        }

        // The "N shared files" chip: scroll the details card to the shared-files section.
        internal void ShowSharedFilesDetails()
        {
            GameModRow row = GetGameRowSelection();
            if (row == null || !row.HasConflicts) return;
            int at = installedHelp.Text.IndexOf("SHARED FILES", StringComparison.Ordinal);
            if (at < 0) return;
            // 2.4.0: scroll the section heading to the top without selecting anything (no blue block, no focus).
            installedHelp.Select(0, 0);
            ScrollHelpToLine(installedHelp.GetLineFromCharIndex(at));
            SetPageStatus(installedPage, "Shared files and same-outfit mods for '" + row.Name + "' are listed below · the higher mod wins · Move up/down changes it", Palette.Accent);
        }

        private bool GameIdle(string action, bool pakEdit = true)
        {
            if (FluffyBlocksPak && pakEdit) { Error(new IOException(FluffyPakReason)); return false; }
            return true;
        }

        // 2.4.1: undo (optional) runs as its own change and is offered in the status strip until the next change.
        private void ChangeGameRow(string label, Func<ReplaceEngine, string> action, string done) { ChangeGameRow(label, action, done, null); }
        private void ChangeGameRow(string label, Func<ReplaceEngine, string> action, string done, Func<ReplaceEngine, string> undo)
        {
            if (busy || !GameIdle(label.ToLowerInvariant())) return;
            ReplaceEngine engine = Engine();
            Work(label + "…", delegate { return action(engine); }, delegate(string message) { Finished(done, message, undo); });
        }
        private void Finished(string done, string message, Func<ReplaceEngine, string> undo, Func<string> undoBlocker = null)
        {
            if (undo != null) OfferUndo(done.TrimEnd('.'), delegate { ChangeGameRow("Undoing", undo, "Undone: " + done.TrimEnd('.') + "."); }, undoBlocker);
            RefreshInstalled("✓ " + done + (String.IsNullOrEmpty(message) ? "" : " " + message));
        }

        private void ToggleGameRow(GameModRow row) { if (row.Mod == null) return; string id = row.Mod.Id; bool enable = !row.Mod.Enabled; ChangeGameRow(enable ? "Enabling replacement" : "Disabling replacement", delegate(ReplaceEngine e) { return e.SetEnabled(id, enable); }, (enable ? "Enabled " : "Disabled ") + row.Name + ".", delegate(ReplaceEngine e) { return e.SetEnabled(id, !enable); }); }
        private void MoveGameRow(int delta) { GameModRow row = GetGameRowSelection(); if (row == null || row.Mod == null) return; string id = row.Mod.Id; ChangeGameRow("Changing priority", delegate(ReplaceEngine e) { return e.Move(id, delta); }, row.Name + " moved " + (delta < 0 ? "up." : "down."), delegate(ReplaceEngine e) { return e.Move(id, -delta); }); }
        // 2.4.1: no confirmation. 1.0.1: no copy kept and no Undo (uninstall means uninstall; the source reinstalls it).
        private void UninstallGameRow(GameModRow row)
        {
            if (row.Fluffy != null) { UninstallFluffyRow(row); return; }
            if (row.Mod == null) return;
            UninstallReplacementIds(new List<string> { row.Mod.Id }, "Uninstalled " + row.Name, "Uninstalling replacement");
        }
        internal static string UninstallQuestion(ReplaceMod mod, string name)
        {
            return "Uninstall '" + name + "'?\n\nIts files leave the game folder and the files it replaced come back (the next mod in the list takes over shared files). The source mod stays in your library."
                + (LoaderCostumePak.IsLoaderMod(mod) ? " Slotsloader's costume pak is removed too, so its extra outfits stop loading." : "");
        }
        private void ReapplyAll() { string game = config.GameRoot; ChangeGameRow("Reapplying replacements", delegate(ReplaceEngine e) { string r = e.Reapply(); UnownedSlotsFlag.Ensure(game); return r; }, "Reapplied all replacements."); }
        // 2.4.0: SlotsLoader is gone but its costume pak is not (Installed page banner, "Remove it").
        private void RemoveOrphanedCostumePak()
        {
            // 2.4.1: no popup (the click is the consent). The pak is generated: a reinstalled SlotsLoader rebuilds it.
            if (busy) return;
            ChangeGameRow("Removing Slotsloader's costume pak",delegate(ReplaceEngine e) { return e.RemoveOrphanedCostumePak(); }, "Done.");
        }
        // 2.4.1: recoverable (every record is kept), so no popup; Undo puts them all back in order.
        private void RestoreOriginalGameFiles() { RestoreAllWithUndo(); }

        // ---- 2.2.0: files another tool changed never block an uninstall: ask, then run again with the answer.
        private sealed class DriftAttempt { internal string Message; internal DriftException Drift; }
        internal const string DriftCaption = "Game files changed by another tool";
        internal static readonly string[] DriftButtons = { "Restore &original", "&Keep current file", "Cancel" };
        internal DriftChoice LastDriftChoice = DriftChoice.Refuse;   // tests: Refuse = not asked, Ask = cancelled

        internal static string DriftQuestion(DriftException drift)
        {
            var lines = drift.Items.Take(8).Select(i => "• " + i.Text + (i.Kept ? " (kept earlier)" : "")).ToList();
            if (drift.Items.Count > 8) lines.Add("• … " + (drift.Items.Count - 8) + " more");
            return "Another tool changed files the Manager installed:\n\n" + String.Join("\n", lines) +
                "\n\nRestore original: the game's file (or the next mod's copy) comes back; the other tool's file is removed." +
                "\nKeep current file: the other tool's file stays and the Manager stops managing it. The game's original is kept, so Restore original game files can put it back.";
        }
        private DriftChoice AskDrift(DriftException drift)
        {
            if (Program.Headless && ThemedMessageBox.TestResponder == null) return DriftChoice.Ask;
            bool ignored;
            int answer = ThemedMessageBox.Ask(this, DriftQuestion(drift), DriftCaption, MessageBoxIcon.Warning, DriftButtons, 0, null, out ignored);
            return answer == 0 ? DriftChoice.RestoreOriginal : answer == 1 ? DriftChoice.KeepCurrent : DriftChoice.Ask;
        }
        private void ChangeWithDriftChoice(string label, Func<ReplaceEngine, DriftChoice, string> action, string done) { ChangeWithDriftChoice(label, action, done, null); }
        private void ChangeWithDriftChoice(string label, Func<ReplaceEngine, DriftChoice, string> action, string done, Func<ReplaceEngine, string> undo, Func<string> undoBlocker = null)
        {
            if (busy || !GameIdle(label.ToLowerInvariant())) return;
            ReplaceEngine engine = Engine();
            LastDriftChoice = DriftChoice.Refuse;
            Work(label + "…", delegate
            {
                try { return new DriftAttempt { Message = action(engine, DriftChoice.Ask) }; }
                catch (DriftException ex) { return new DriftAttempt { Drift = ex }; }
            }, delegate(DriftAttempt attempt)
            {
                if (attempt.Drift == null) { Finished(done, attempt.Message, undo, undoBlocker); return; }
                DriftChoice choice = AskDrift(attempt.Drift);
                LastDriftChoice = choice;
                if (choice == DriftChoice.Ask) { SetPageStatus(currentPage, "Cancelled: nothing changed. " + attempt.Drift.Items[0].Text + ".", Palette.Accent); return; }
                ReplaceEngine again = Engine();
                Work(label + "…", delegate { return action(again, choice); }, delegate(string message) { Finished(done, message, undo, undoBlocker); });
            });
        }

        // ---- 2.2.0: Fluffy's installs, uninstalled from here (one-way: this app never writes its own installs into Fluffy).
        internal string FluffyUninstallBlocker(FluffyMod mod)
        {
            if (mod == null) return "Not installed by an external mod manager.";
            if (busy) return "Wait for the current task to finish.";
            if (mod.UninstallBlocker != null) return mod.UninstallBlocker;
            if (fluffyRunning) return ReplaceEngine.FluffyRunningReason;
            if (gameRunning) return "Close SF6 first: game files can't change while it runs.";
            return null;
        }
        private void UninstallFluffyRow(GameModRow row)
        {
            if (row == null || row.Fluffy == null || busy) return;
            string blocker = FluffyUninstallBlocker(row.Fluffy);
            // 2.4.1: the reason inline only; no confirmation (a copy is kept and Undo restores it).
            if (blocker != null) { SetPageStatus(currentPage, blocker, Palette.Accent); return; }
            string root = row.Fluffy.Root, section = row.Fluffy.Section, name = row.Name;
            ReplaceEngine engine = Engine(); string kept = null;
            Work("Uninstalling " + name + "…", delegate { string folder; string m = engine.UninstallFluffy(root, section, out folder); kept = folder; return m; }, delegate(string message)
            {
                if (kept != null) OfferUndo("Uninstalled " + name, delegate { ReplaceEngine again = Engine(); Work("Restoring " + name + "…", delegate { return again.RestoreFluffyRemoved(kept); }, delegate(string m) { RefreshInstalled("✓ Undone: " + name + " is back in the external mod manager."); }); });
                RefreshInstalled("✓ " + message);
            });
        }

        // Library entries that are the source of a Fluffy install: <Fluffy>\Games\SF6\Mods\<section or name>[.zip/.7z/.rar].
        internal static bool FluffySourceMatches(FluffyMod mod, string sourcePath)
        {
            if (mod == null || String.IsNullOrWhiteSpace(sourcePath)) return false;
            try
            {
                string full = Path.GetFullPath(sourcePath).TrimEnd('\\');
                if (!String.Equals(Path.GetDirectoryName(full), Path.Combine(Path.GetFullPath(mod.Root).TrimEnd('\\'), "Mods"), StringComparison.OrdinalIgnoreCase)) return false;
                string name = Path.GetFileName(full), ext = Path.GetExtension(full).ToLowerInvariant();
                if (ext == ".zip" || ext == ".7z" || ext == ".rar") name = Path.GetFileNameWithoutExtension(full);
                return String.Equals(name, mod.Section, StringComparison.OrdinalIgnoreCase) || String.Equals(name, mod.Name, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; return false; }
        }
        private List<GameModRow> LinkedFluffy(string sourcePath) { return gameRows.Where(r => r.Fluffy != null && FluffySourceMatches(r.Fluffy, sourcePath)).ToList(); }
        private void PopulateFluffyUninstall(ToolStripMenuItem action, string sourcePath)
        {
            List<GameModRow> rows = LinkedFluffy(sourcePath);
            action.Visible = rows.Count > 0; action.Tag = rows.Count > 0 ? rows[0] : null;
            string blocker = rows.Count > 0 ? FluffyUninstallBlocker(rows[0].Fluffy) : null;
            action.Enabled = rows.Count > 0 && blocker == null; action.AccessibleDescription = blocker ?? "";
            action.Text = "Uninstall mod (external manager)" + (blocker == null || rows.Count == 0 ? "" : rows[0].Fluffy.UninstallBlocker != null ? " (uninstall it there)" : fluffyRunning ? " (close the external mod manager first)" : gameRunning ? " (close SF6 first)" : "");
        }

        // installed.ini is watched (FileSystemWatcher, debounced) and its stamp re-checked on activation and every
        // poll, so Fluffy's installs and uninstalls show up on the Installed page right away.
        private FluffyWatcher fluffyWatcher;
        private List<string> fluffyRoots = new List<string>();
        private string fluffyStamp = "";
        private bool fluffyChangePending;
        internal int FluffyRefreshCount;   // tests
        internal static bool FluffyAutoRefresh = true;   // UI checks that edit installed.ini themselves turn the automatic refresh off
        private void WatchFluffy(List<string> roots, string stamp)
        {
            fluffyRoots = roots ?? new List<string>(); fluffyStamp = stamp ?? "";
            string key = FluffyWatcher.Key(fluffyRoots);
            if (fluffyWatcher != null && fluffyWatcher.RootsKey == key && !fluffyWatcher.NeedsRestart) return;   // 2.4.2: a root that appeared or a failed watcher starts over
            if (fluffyWatcher != null) fluffyWatcher.Dispose();
            fluffyWatcher = null;
            if (fluffyRoots.Count == 0) return;
            fluffyWatcher = new FluffyWatcher(fluffyRoots, 250);
            fluffyWatcher.Changed += delegate { try { if (!IsDisposed && IsHandleCreated) BeginInvoke((MethodInvoker)FluffyChanged); } catch (InvalidOperationException) { } };
        }
        private void CheckFluffyStamp()
        {
            if (!FluffyAutoRefresh) return;
            if (fluffyChangePending && !busy) { FluffyChanged(); return; }
            if (fluffyWatcher != null && fluffyWatcher.NeedsRestart) WatchFluffy(fluffyRoots, fluffyStamp);   // 2.4.2
            if (fluffyRoots.Count == 0) return;
            string now; try { now = FluffyInstalls.StampOf(fluffyRoots); } catch (Exception ex) { Program.Log(ex); return; }
            if (now != fluffyStamp) FluffyChanged();
        }
        private void FluffyChanged()
        {
            if (IsDisposed || !FluffyAutoRefresh) return;
            if (busy) { fluffyChangePending = true; return; }
            fluffyChangePending = false;
            try { fluffyStamp = FluffyInstalls.StampOf(fluffyRoots); } catch (Exception ex) { Program.Log(ex); }
            FluffyRefreshCount++;
            RefreshInstalled("External mod manager's mods changed · list updated");
        }
        private void EditGameRow(GameModRow row)
        {
            if (row.Mod == null || (!File.Exists(row.Mod.SourcePath) && !Directory.Exists(row.Mod.SourcePath))) { Error(new FileNotFoundException("The mod's source file or folder is gone: " +(row.Mod == null ? "" : row.Mod.SourcePath))); return; }
            ShowPage(libraryPage);
            LoadPackage(new LibraryEntry { SourcePath = row.Mod.SourcePath, Name = row.Name }, null, row.Mod);
        }

        // 2.4.1: the banner's other answer to loose installs with the loader off: convert them to the Fluffy PAK method
        // (the Settings conversion: SF6 closed, Fluffy closed; no popup, Undo switches back). An explicit loose setting
        // returns to Automatic.
        private void UsePakMethod()
        {
            if (busy || !GameIdle("switching to the PAK method")) return;
            string previous;
            try { previous = ReplaceSettings.Load().Method ?? ""; }
            catch (Exception ex) { Error(ex); return; }
            // The explicit loose setting goes back to Automatic only once the conversion has worked.
            ChangeGameRow("Switching to the PAK method", delegate(ReplaceEngine e)
            {
                string message = e.SwitchMethod(ReplaceMethods.Pak);
                if (previous == ReplaceMethods.Loose) { ReplaceSettings s = ReplaceSettings.Load(); s.Method = ""; s.Save(); }
                return message;
            }, "Replacements now use the PAK method.",
                delegate(ReplaceEngine e) { ReplaceSettings s = ReplaceSettings.Load(); s.Method = previous; s.Save(); return e.SwitchMethod(ReplaceMethods.Loose); });
        }

        // 2.4.1: no popup (the config is backed up first); Undo sets it back.
        private void SetLooseLoader(bool enabled)
        {
            if (busy || !GameIdle("change REFramework's config", false)) return;
            try
            {
                ReplaceEngine engine = Engine();
                LooseLoader.Write(config.GameRoot, LooseLoader.EnabledKey, enabled ? "true" : "false", engine.ConfigBackupDir);
                if (enabled) { replaceSettings = ReplaceSettings.Load(); replaceSettings.LooseLoaderConsent = true; replaceSettings.Save(); }
                LoadReplaceSettingsFields();
                OfferUndo("Loose file loader " + (enabled ? "on" : "off"), delegate { SetLooseLoader(!enabled); });
                RefreshInstalled("✓ Loose file loader " + (enabled ? "on" : "off"));
            }
            catch (Exception ex) { Error(ex); }
        }

        // ---- Settings: "Game files & replacements" tab
        private Control BuildSettingsTabs(Label subtitle, Control[] generalParts, Panel content)
        {
            settingsGeneralParts = generalParts;
            var row = new Panel { Dock = DockStyle.Top, Height = 34, Padding = new Padding(0, 0, 0, 4) };
            settingsGeneralTab = Button("General", false, 96, delegate { ShowSettingsTab(false); });
            settingsReplaceTab = Button("Game files & replacements", false, 210, delegate { ShowSettingsTab(true); });
            settingsUpdatesTab = Button("Updates", false, 96, delegate { ShowUpdatesTab(); });
            settingsAboutTab = Button("About", false, 84, delegate { ShowAboutTab(); });   // 2.5.9 (MainFormSupport.cs)
            foreach (FlatButton b in new[] { settingsAboutTab, settingsUpdatesTab, settingsReplaceTab, settingsGeneralTab }) { b.Dock = DockStyle.Left; b.Height = 30; b.Font = Palette.Small; b.Navigation = true; row.Controls.Add(b); }
            row.Controls.Add(new Panel { Dock = DockStyle.Left, Width = 10 });
            subtitle.Dock = DockStyle.Fill; subtitle.Padding = new Padding(12, 6, 0, 0);
            row.Controls.Add(subtitle); subtitle.BringToFront(); settingsSubtitle = subtitle;
            settingsReplacePanel = BuildReplaceSettings();
            content.Controls.Add(settingsReplacePanel);
            settingsUpdatesPanel = BuildUpdatesSettings();
            content.Controls.Add(settingsUpdatesPanel);
            settingsAboutPanel = BuildAboutSettings();
            content.Controls.Add(settingsAboutPanel);
            settingsGeneralTab.SetActive(true);
            return row;
        }

        private void ShowSettingsTab(bool replace)
        {
            foreach (Control c in settingsGeneralParts) c.Visible = !replace;
            if (settingsSubtitle != null) settingsSubtitle.Visible = !replace;
            settingsReplacePanel.Visible = replace;
            if (settingsUpdatesPanel != null) { settingsUpdatesPanel.Visible = false; settingsUpdatesTab.SetActive(false); }
            HideAboutTab();   // 2.5.9
            if (replace) { settingsReplacePanel.BringToFront(); LoadReplaceSettingsFields(); installSlotsLoaderButton.Text = "Install Slotsloader " + CurrentInstallPin().Version + "…"; }
            settingsGeneralTab.SetActive(!replace); settingsReplaceTab.SetActive(replace);
        }

        private Panel BuildReplaceSettings()
        {
            var panel = new Panel { Dock = DockStyle.Fill, Visible = false, Padding = new Padding(0, 4, 0, 0) };
            var columns = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty, Padding = Padding.Empty };
            columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 52)); columns.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 24)); columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 48));
            var left = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty }; var right = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
            Action<Panel, Control> add = delegate(Panel host, Control c) { c.Dock = DockStyle.Top; host.Controls.Add(c); c.BringToFront(); };
            // 2.4.0 rhythm: caption, control, its note (Related), buttons, then a Group gap before the next caption.
            Func<string, int, Label> note = delegate(string text, int height) { return new NoteLabel { Text = text, Height = height, ForeColor = Palette.Muted, Font = Palette.Small, Padding = new Padding(0, Rhythm.Related, 0, 0) }; };
            Func<int, FlowLayoutPanel> buttonRow = delegate(int top) { return new FlowLayoutPanel { Height = 32 + top, WrapContents = false, Padding = new Padding(0, top, 0, 0), Margin = Padding.Empty }; };
            add(left, Caption("Replacement method"));
            settingsMethod = Combo(); settingsMethod.Height = 30; settingsMethod.AccessibleName = "Replacement method";
            settingsMethod.Items.AddRange(new object[] { "Automatic (loose file loader if it is on, else PAK method)", "REFramework loose file loader (no PAK edits)", "PAK method (works without REFramework)" });
            add(left, settingsMethod);
            settingsMethodStatus = note("", 52); add(left, settingsMethodStatus);
            var loaderRow = buttonRow(8);
            loaderOnButton = Button("Turn loose file loader on", false, 200, delegate { SetLooseLoader(true); });
            loaderOffButton = Button("Turn it off", false, 110, delegate { SetLooseLoader(false); });
            loaderOffButton.AccessibleName = "Turn the loose file loader off";
            foreach (FlatButton b in new[] { loaderOnButton, loaderOffButton }) { b.Height = 32; b.Font = Palette.Small; b.Margin = new Padding(0, 0, 8, 0); loaderRow.Controls.Add(b); }
            add(left, loaderRow);
            add(left, Rhythm.Gap(Rhythm.AfterAction - 4));
            // 2.4.1: no "Ask before installing while the loose file loader is off": the question is gone (Automatic uses the PAK method then).
            // 2.3.0: no "Default install mode" here any more: the library's INSTALL AS remembers the last choice
            // (ReplaceSettings.DefaultMode stays in the file and is used only before a first choice).
            settingsCrashReporter = new CheckBox { Text = "Rename the crash reporter while replacements are installed", Height = 28, ForeColor = Palette.Text, Font = Palette.Small, Padding = new Padding(2, 0, 0, 0) };
            add(left, settingsCrashReporter);
            // 1.0.5: off by default, so ordinary mods never get swap warnings; Replace original and Slotsloader both follow it.
            settingsSwapRepairs = new CheckBox { Text = "Try to fix character-swap mods broken by game updates", Height = 28, ForeColor = Palette.Text, Font = Palette.Small, Padding = new Padding(2, 0, 0, 0) };
            add(left, settingsSwapRepairs);
            add(left, Rhythm.Gap(Rhythm.Group));
            add(left, Caption("REFramework and Slotsloader"));
            settingsToolStatus = note("", 52); add(left, settingsToolStatus);
            installSlotsLoaderButton = Button("Install Slotsloader " + LoaderRelease.Pin.Version + "…", false, 230, delegate { OfferSlotsLoaderInstall(); }); installSlotsLoaderButton.Height = 32; installSlotsLoaderButton.Font = Palette.Small; installSlotsLoaderButton.Margin = Padding.Empty;
            // 1.0.8: REFramework first (Slotsloader is its plugin); shown while it is missing or out of date.
            installReFrameworkButton = Button("Install " + ReFrameworkRelease.Pin.Label + "…", false, 260, delegate { OfferReFrameworkInstall(); }); installReFrameworkButton.Height = 32; installReFrameworkButton.Font = Palette.Small; installReFrameworkButton.Margin = new Padding(0, 0, 8, 0);
            var toolRow = buttonRow(8); toolRow.Controls.Add(installReFrameworkButton); toolRow.Controls.Add(installSlotsLoaderButton); add(left, toolRow);

            add(right, Caption("Game files"));
            settingsGameFiles = note("", 76); add(right, settingsGameFiles);
            var gameRow = buttonRow(8);
            FlatButton reapply = Button("Reapply all", false, 120, delegate { ReapplyAll(); });
            FlatButton restore = Button("Restore original game files", false, 220, delegate { RestoreOriginalGameFiles(); }); restore.Danger = true;
            foreach (FlatButton b in new[] { reapply, restore }) { b.Height = 32; b.Font = Palette.Small; b.Margin = new Padding(0, 0, 8, 0); gameRow.Controls.Add(b); }
            add(right, gameRow);
            add(right, Rhythm.Gap(Rhythm.Group));
            add(right, Caption("Replacement store"));
            var storeRow = new TableLayoutPanel { Height = 32, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty };
            storeRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); storeRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 108)); storeRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92)); storeRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            settingsStore = TextField(); settingsStore.Dock = DockStyle.Fill; settingsStore.Margin = new Padding(0, 0, 8, 0); settingsStore.AccessibleName = "Replacement store folder"; settingsStore.PlaceholderText = "Default: Data\\replace-store";
            FlatButton browse = Button("Browse…", false, 100, delegate { using (var d = new FolderBrowserDialog { Description = "Choose a folder for the replacement store" }) if (d.ShowDialog(this) == DialogResult.OK) settingsStore.Text = d.SelectedPath; });
            browse.Dock = DockStyle.Fill; browse.Margin = new Padding(0, 0, 8, 0); browse.Font = Palette.Small;
            FlatButton save = Button("Save", true, 84, delegate { SaveReplacePreferences(); }); save.Dock = DockStyle.Fill; save.Margin = Padding.Empty; save.Font = Palette.Small;
            save.AccessibleName = "Save the replacement store folder";
            storeRow.Controls.Add(settingsStore, 0, 0); storeRow.Controls.Add(browse, 1, 0); storeRow.Controls.Add(save, 2, 0);
            add(right, storeRow);
            settingsStoreStatus = note("", 52); add(right, settingsStoreStatus);
            add(right, Rhythm.Gap(Rhythm.Group));
            add(right, Caption("External mod manager"));
            settingsFluffyStatus = note("", 52); add(right, settingsFluffyStatus);
            var fluffyRow = buttonRow(8);
            FlatButton import = Button("Import external preset (.prt)…", false, 190, delegate { ImportFluffyPreset(); }); import.Height = 32; import.Font = Palette.Small; import.Margin = Padding.Empty;
            fluffyRow.Controls.Add(import);
            add(right, fluffyRow);
            columns.Controls.Add(left, 0, 0); columns.Controls.Add(right, 2, 0);
            panel.Controls.Add(columns);
            settingsMethod.SelectedIndexChanged += delegate { if (!loadingReplaceSettings) SaveReplacePreferences(); };
            settingsCrashReporter.CheckedChanged += delegate { if (!loadingReplaceSettings) SaveReplacePreferences(); };
            settingsSwapRepairs.CheckedChanged += delegate { if (!loadingReplaceSettings) SaveReplacePreferences(); };
            return panel;
        }

        private void LoadReplaceSettingsFields()
        {
            if (settingsMethod == null) return;
            loadingReplaceSettings = true;
            try
            {
                replaceSettings = ReplaceSettings.Load();
                settingsMethod.SelectedIndex = replaceSettings.Method == ReplaceMethods.Loose ? 1 : replaceSettings.Method == ReplaceMethods.Pak ? 2 : 0;
                settingsCrashReporter.Checked = replaceSettings.RenameCrashReporter;
                settingsSwapRepairs.Checked = replaceSettings.SwapRepairs;
                settingsStore.Text = replaceSettings.StoreLocation ?? "";
                ReplaceEngine engine = Engine(); ReplaceState state = null;
                try { state = engine.LoadState(); } catch (Exception ex) { settingsGameFiles.Text = ex.Message; }
                string detail; LooseLoaderState loader = LooseLoader.State(config.GameRoot, out detail);
                string current = state == null ? "" : ReplaceMethods.Label(engine.CurrentMethod(state));
                settingsMethodStatus.Text = "In use: " + current + (state != null && state.Mods.Count > 0 && engine.DesiredMethod() != engine.CurrentMethod(state) ? " · changing it converts installed replacements" : "") + Environment.NewLine + detail;
                settingsMethodStatus.ForeColor = loader == LooseLoaderState.Unreadable ? Palette.Red : Palette.Muted;
                loaderOnButton.Enabled = loader == LooseLoaderState.Disabled; loaderOffButton.Enabled = loader == LooseLoaderState.Enabled;
                ToolStatus tools = ToolStatus.Detect(config.GameRoot);
                settingsToolStatus.Text = tools.ReFrameworkDetail + Environment.NewLine + tools.SlotsLoaderDetail;
                settingsToolStatus.ForeColor = tools.SlotsLoader && !tools.ReFrameworkOffer && tools.ReFramework ? Palette.Muted : Palette.Accent;
                installSlotsLoaderButton.Enabled = !tools.SlotsLoader;
                installReFrameworkButton.Visible = tools.ReFrameworkOffer;
                installReFrameworkButton.Text = (tools.ReFrameworkFile ? "Update to " : "Install ") + ReFrameworkRelease.Pin.Label + "…";
                if (state != null)
                {
                    string pak = "Base PAK: not found";
                    string basePath = Path.Combine(config.GameRoot, "re_chunk_000.pak");
                    if (File.Exists(basePath)) { PakIndex p = GamePaks.Cached(basePath); pak = "Base PAK: " + ReplacePlanner.N(p.Count, "entry") + ", " + p.CountZeroed() + " invalidated"; }
                    int backups = Directory.Exists(engine.PakBackupDir) ? Directory.GetFiles(engine.PakBackupDir, "*.toc").Length : 0;
                    settingsGameFiles.Text = ReplacePlanner.N(state.Mods.Count, "replacement or tool mod").Replace("replacement or tool mods", "replacements and tool mods") + " (" + state.Mods.Count(m => m.Enabled) + " enabled) · " + ReplacePlanner.N(state.Deployed.Count, "file") + " deployed · " + ReplacePlanner.N(state.Deployed.Sum(d => d.Zeroed.Count), "PAK entry") + " held" + Environment.NewLine + pak + " · " + ReplacePlanner.N(backups, "TOC backup") + " kept" + Environment.NewLine + tools.CrashReporterState;
                    // 2.4.2: installed copies and the removed copies (Undo, Recovery) with their keep rule; "(freed on
                    // uninstall)" was wrong since 2.0.1 (an uninstall keeps a copy in .removed).
                    string store = engine.StoreRoot(state);
                    settingsStoreStatus.Text = StoreStatusText(InstalledStoreBytes(store), state.Mods.Count, engine.ListRemoved(), store);
                }
                FluffyInstalls fluffy = engine.Fluffy();
                settingsFluffyStatus.Text = ReplacePlanner.N(fluffy.Mods.Count, "mod") + " installed by an external mod manager (protected) · " + (fluffyRunning ? "it is running" : "it is closed") + " · " + ReplacePlanner.N(FluffyPresets.PresetFiles(fluffy.Roots).Count, "preset") + (fluffy.Roots.Count == 0 ? Environment.NewLine + "No external mod manager found next to your libraries." : String.Concat(fluffy.Roots.Select(r => Environment.NewLine + r)));
            }
            catch (Exception ex) { Program.Log(ex); settingsGameFiles.Text = ex.Message; }
            finally { loadingReplaceSettings = false; }
        }

        private void SaveReplacePreferences()
        {
            try
            {
                // 2.1.0 fix: Engine() reloads replaceSettings, so it runs before the edits (it used to run after
                // them and the reloaded, unchanged copy was saved: method, default mode and crash-reporter
                // changes made in Settings were lost).
                ReplaceEngine engine = Engine();
                ReplaceState state = engine.LoadState();
                replaceSettings = ReplaceSettings.Load();
                string method = settingsMethod.SelectedIndex == 1 ? ReplaceMethods.Loose : settingsMethod.SelectedIndex == 2 ? ReplaceMethods.Pak : "";
                bool methodChanged = method != replaceSettings.Method;
                string previousMethod = replaceSettings.Method ?? "";
                replaceSettings.Method = method;
                replaceSettings.RenameCrashReporter = settingsCrashReporter.Checked;
                replaceSettings.SwapRepairs = settingsSwapRepairs.Checked;
                string store = settingsStore.Text.Trim();
                if (!String.Equals(store, replaceSettings.StoreLocation ?? "", StringComparison.OrdinalIgnoreCase))
                {
                    if (state.Mods.Count > 0) throw new IOException("Uninstall the installed replacements before moving the replacement store.");
                    if (store.Length > 0 && !Directory.Exists(store)) throw new IOException("Replacement store folder not found: " + store);
                    replaceSettings.StoreLocation = store;
                }
                replaceSettings.Save();
                SwapRepairFlag.Sync(config.GameRoot, replaceSettings.SwapRepairs);
                if (methodChanged && (state.Mods.Count > 0 || state.Deployed.Count > 0))
                {
                    engine = Engine(); string target = engine.DesiredMethod();
                    // 2.4.1: the choice in Settings is the consent: convert at once; Undo converts back.
                    string from = engine.CurrentMethod(state), previousSetting = previousMethod;
                    if (target != from)
                    {
                        if (target == ReplaceMethods.Loose && !EnsureLooseLoader(new ReplaceEngine(config.GameRoot, AppConfig.DataDir, new ReplaceSettings { Method = ReplaceMethods.Loose }), state.Mods.Any(m => m.Enabled && m.Files.Any(f => f.Path.StartsWith("natives/", StringComparison.OrdinalIgnoreCase))))) { LoadReplaceSettingsFields(); return; }
                        ChangeGameRow("Switching the replacement method", delegate(ReplaceEngine e) { return e.SwitchMethod(target); }, "Replacements now use the " + ReplaceMethods.Label(target) + ".",
                            delegate(ReplaceEngine e) { ReplaceSettings s = ReplaceSettings.Load(); s.Method = previousSetting; s.Save(); return e.SwitchMethod(from); });
                    }
                }
                LoadReplaceSettingsFields(); UpdateModeUi();
                if (IsReplaceMode && package != null) RequestReview(ReviewDelay);
            }
            catch (Exception ex) { Error(ex); LoadReplaceSettingsFields(); }
        }

        private void ImportFluffyPreset()
        {
            if (busy) return;
            ReplaceEngine engine = Engine();
            string file;
            using (var d = new OpenFileDialog { Title = "Import external preset (.prt)", Filter = "Presets (.prt)|*.prt|All files|*.*" })
            {
                string dir = FluffyPresets.PresetFiles(engine.Fluffy().Roots).Select(Path.GetDirectoryName).FirstOrDefault();
                if (dir != null) d.InitialDirectory = dir;
                if (d.ShowDialog(this) != DialogResult.OK) return;
                file = d.FileName;
            }
            List<LibraryEntry> entries = library; string sevenZip = config.SevenZipPath;
            Work("Matching the preset to your libraries…", delegate { return FluffyPresets.Group(FluffyPresets.Map(FluffyPresets.Parse(File.ReadAllText(file)), entries, sevenZip)); }, delegate(List<PresetMapping> mappings)
            {
                if (FluffyPresetForm.Review(this, Path.GetFileNameWithoutExtension(file), mappings))
                {
                    AppConfig cfg = config;
                    if (mappings.Any(m => m.Include && m.Entry != null && m.Mode == InstallModes.Replace && (m.Kind != "Tool")) && !EnsureLooseLoader(engine, true)) return;
                    Work("Installing the preset…", delegate { return PresetInstaller.Install(cfg, engine, mappings.Where(m => m.Include && m.Entry != null).ToList()); }, delegate(string summary) { RefreshInstalled(summary); });
                }
            });
        }
    }
}
