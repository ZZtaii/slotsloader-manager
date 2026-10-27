using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace SlotsInstaller
{
    internal sealed class ViewPreferences
    {
        public string Format = "sf6-installer-view-v2";
        public int Dpi = 96;
        public bool HasBounds, Maximized, InstalledOnly, OverridesOnly;
        public int X, Y, Width, Height, LibraryWidth, InstalledWidth;
        public string Page = "Library", Search = "", Character = "";
        // 2.2.0 library dropdown: read once into SelectedLibraries (when still configured), never written again.
        // The 2.2.0 ModType/ArchiveFormat and the 2.3.0 test-build Costume values are no longer read or written.
        [System.Web.Script.Serialization.ScriptIgnore] public string Source = "";
        public string InstalledSearch = "", InstalledCharacter = "", InstalledState = "";
        // 2.3.0 Filters panel: open/closed, selected fighters, kind tiles ("tools", "misc"), Misc kinds and the
        // costume choice. `Character` (one name, 2.2.0 and older) is read once into Fighters; `FilterTab` and
        // `Stages` (from 2.3.0 test builds with tabs) are only read: a "tools"/"misc"/"stages" tab becomes that kind.
        public bool FiltersOpen;
        public string FilterTab = "";
        public List<string> Fighters = new List<string>(), Kinds = new List<string>(), MiscAreas = new List<string>(), Stages = new List<string>(), SelectedLibraries = new List<string>();
        // 2.4.1 sort chips: "added" | "last_used" | "installed" and "desc" | "asc". Missing or unknown (older files) = Installed ↓.
        public string LibrarySort = "", LibrarySortOrder = "", InstalledSort = "", InstalledSortOrder = "";   // 2.5.5: Installed sort
        // 1.0.6: library rows show "name" (modinfo.ini) or "file" names (missing = file); Deep search on/off.
        public string LibraryNames = "";
        public bool DeepSearch;
        // 1.0.8: MY FILTERS chips showing only their matches / hiding them, by filter id (LibraryFilters.cs).
        public List<string> FiltersOnly = new List<string>(), FiltersHidden = new List<string>();
        // 1.0.11: the welcome (WelcomeForm) was shown; missing in older files, so updating users see it once too.
        public bool WelcomeSeen;
        // 1.0.15: the tours that ran ("library", "installed", "settings"; MainFormTours.cs); missing in older files.
        public List<string> ToursSeen = new List<string>();
        // 1.0.11: where Help resumes: the topic's title and the article's scroll offset (logical pixels).
        public string HelpTopic = "";
        public int HelpOffset;
        internal static ViewPreferences Load(string file)
        {
            try
            {
                if (!File.Exists(file)) return new ViewPreferences();
                var value=Json.Read<ViewPreferences>(file);
                return value!=null && (value.Format=="sf6-installer-view-v1" || value.Format=="sf6-installer-view-v2") ? value : new ViewPreferences();
            }
            catch (Exception ex) { Program.Log(ex); return new ViewPreferences(); }
        }
        internal void Save(string file) { Directory.CreateDirectory(Path.GetDirectoryName(file)); Json.Write(file,this); }
        internal Rectangle VisibleBounds(Rectangle area,Size minimum)
        {
            int width=Math.Max(Math.Min(minimum.Width,area.Width),Math.Min(Width,area.Width));
            int height=Math.Max(Math.Min(minimum.Height,area.Height),Math.Min(Height,area.Height));
            return new Rectangle(Math.Max(area.Left,Math.Min(X,area.Right-width)),Math.Max(area.Top,Math.Min(Y,area.Bottom-height)),width,height);
        }
    }

    internal sealed partial class MainForm : Form
    {
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
        private readonly AppConfig config;
        private List<LibraryEntry> library = new List<LibraryEntry>();
        private List<InstalledOutfit> installed = new List<InstalledOutfit>();
        private ModPackage package;
        private CompositionPlan plan;
        private readonly Dictionary<string, OptionNode> componentNodes = new Dictionary<string, OptionNode>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> selectedOrder = new List<string>();
        private readonly List<string> missingRestoredOptions = new List<string>();
        private readonly Dictionary<LibraryEntry,LibraryInstallState> installStates = new Dictionary<LibraryEntry,LibraryInstallState>();
        private List<InstalledOutfit> indexedInstalled;
        private Dictionary<string,List<InstalledOutfit>> installedBySource;
        private bool changingChecks;
        private bool busy;
        private bool firstLoad = true;
        private bool dpiLayoutReady;
        private bool applyingDpi;
        // 1.0.15: the library starts at 330 (was 250; the user picked it from renders): few users find the splitter,
        // and long mod names were cut. Until the splitter is dragged the width follows the window (DefaultLibraryFor),
        // so a small window keeps room for the options.
        internal const int DefaultLibraryWidth=330, OldDefaultLibraryWidth=250;
        private int libraryLogicalWidth=DefaultLibraryWidth, installedLogicalWidth=650;
        private bool libraryWidthChosen;
        private readonly List<Action> dpiLayouts = new List<Action>();
        private readonly List<Tuple<TableLayoutPanel,float[],float[]>> tableMetrics=new List<Tuple<TableLayoutPanel,float[],float[]>>();
        private SplitContainer librarySplit, installedSplit;
        private ViewPreferences viewPreferences;
        private bool rememberView, lastMaximized, viewSaveErrorLogged;
        private string lastMainPage = "Library";
        private readonly System.Windows.Forms.Timer viewSaveTimer = new System.Windows.Forms.Timer { Interval=600 };
        private PaddedTextField installedSearch;
        private ComboBox installedState;   // 1.0.1: the character filter is a portrait picker (MainFormCharacterPicker.cs)
        private CheckBox overridesOnly;
        private bool selectingMod;
        // 2.3.1: set while the user clicks or navigates the library list (mouse button or key down), so that a
        // selection made by the user closes the Filters panel and a programmatic one (repopulating after a filter,
        // count or search change) does not.
        private bool userPickingMod;
        private void ClosePanelForPickedMod() { if (FiltersOpen && !CustomEditorOpen) ShowFilters(false, false); }   // 1.0.8: not while a filter is edited
        // W/S in the library list: the next or previous row, like Down/Up (nothing past the ends).
        private void MoveLibrarySelection(int delta)
        {
            if (modList.Items.Count == 0) return;
            libraryDirection = delta < 0 ? -1 : 1;   // 1.0.12: the row read ahead (MainFormOpen.cs)
            int index = modList.SelectedIndices.Count > 0 ? modList.SelectedIndices[0] : modList.FocusedItem != null ? modList.FocusedItem.Index : -1;
            int next = index < 0 ? 0 : Math.Max(0, Math.Min(modList.Items.Count - 1, index + delta));
            if (next == index) return;
            ListViewItem target = modList.Items[next];
            // 1.0.6: the list allows several rows; W/S go back to one.
            bool previous = selectingMod; selectingMod = true;
            try { foreach (ListViewItem other in modList.SelectedItems.Cast<ListViewItem>().ToList()) if (other != target) other.Selected = false; }
            finally { selectingMod = previous; }
            userPickingMod = true;
            try { target.Selected = true; target.Focused = true; target.EnsureVisible(); }
            finally { userPickingMod = false; }
        }
        private string screenshotFile;
        private string screenshotSource;
        private string screenshotPage;
        private bool screenshotAvailableSize;
        private string openingSource;
        private Image previewImage;
        private string previewComponentId;
        private string inspectedComponentId;
        private OptionNode hoverNode;
        private System.Windows.Forms.Timer hoverTimer;
        private int previewGeneration;
        private bool previewLoading;
        private ModPackage pendingPreviewPackage;
        private string pendingPreviewId;
        private int pendingPreviewGeneration;
        private readonly PreviewImageCache previewCache = new PreviewImageCache(64L * 1024 * 1024);

        private Panel libraryPage, installedPage, settingsPage;
        private FlatButton navLibrary, navInstalled, navSettings;
        private ListView modList, installedList, fileList;
        private PaddedTextField search, outfitName, settingsGame, settingsSevenZip;
        private Action showSevenZipNote;   // 1.0.11
        private TextBox installedHelp;
        private Label installedSelectionCount;
        private FlatButton selectAllInstalledButton, clearInstalledSelectionButton, presetsButton;
        private bool fillingInstalled;
        private ComboBox targetCharacter;
        private ListBox settingsLibraries;
        private OptionTree componentTree;
        private PictureBox preview;
        private Label componentVersion;
        private Label packageTitle, packageMeta, packageDescription, packageHint, noPreview, libraryCount, selectedLabel, targetPath, planSummary, planDetails, searchClear;
        private Label installedTitle, installedDetail, statusText, componentTitle;
        private FlatButton previewButton, installButton, libraryUninstallButton, componentTab, filesTab, openInstalledButton, editInstalledButton, toggleInstalledButton, removeInstalledButton, filterButton;
        private Panel componentPanel, filesPanel;
        private ProgressBar statusProgress;
        private FlatButton cancelWork;
        private CancellationTokenSource activeOperation;
        private Label settingsStatus;
        // 1.5 polish: header tools, icon buttons, installed detail layout,
        // page-aware status, required-choice marking, drop target and search pacing.
        private FlatButton openArchiveButton, openFolderButton, expandAllButton, collapseAllButton, copyPathButton, removeLibraryButton;
        private FlowLayoutPanel headerTools;
        private ChipStrip installedChips;
        private Panel installedPathRow, installedActions;
        private Label installedSectionCaption, refreshHint;
        private DropOverlay dropOverlay;
        private Panel currentPage;
        private readonly Dictionary<Panel, KeyValuePair<string, Color>> pageStatus = new Dictionary<Panel, KeyValuePair<string, Color>>();
        private List<ModComponent> unresolvedChoices = new List<ModComponent>();
        private readonly HashSet<string> unresolvedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly System.Windows.Forms.Timer searchTimer = new System.Windows.Forms.Timer { Interval = 140 };
        internal const int DebouncedLibrarySize = 300;
        private readonly Dictionary<LibraryEntry, ListViewItem> libraryRows = new Dictionary<LibraryEntry, ListViewItem>();
        private List<LibraryEntry> libraryRowsSource;
        private readonly HashSet<Panel> pagesLaidOut = new HashSet<Panel>();
        private int pagesLaidOutDpi;
        private readonly Dictionary<string, int> measuredWidths = new Dictionary<string, int>();
        private int measuredDpi;
        private readonly Dictionary<string, string> libraryFolderStatus = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // 1.6 Assistant tab and its Settings section.
        private AssistantPage assistantPage;
        private FlatButton navAssistant;
        private PaddedTextField settingsApiKey;
        private Label settingsKeyStatus, settingsClaudeStatus;
        private FlatButton saveKeyButton, removeKeyButton, testKeyButton;
        private ComboBox settingsModel, settingsEffort;
        private CheckBox settingsAutoApply;
        private bool loadingClaudeSettings;
        // 2.4.1: Settings → General → "Show Assistant tab". The page is built the first time it is shown while the
        // tab is on; until then no conversation, tools or knowledge pack exist. The settings and key store are
        // the window's, so the Settings card works without the page.
        private AssistantSettings assistantSettings;
        private AssistantKeyStore assistantKeys;
        private bool assistantTabShown;
        private CheckBox settingsShowAssistant;
        private readonly List<Control> claudeDetails = new List<Control>(), claudeRows = new List<Control>();
        private ToolStripItem assistantShortcutItem;
        private Panel pagesPanel;

        internal MainForm(AppConfig config)
        {
            SuspendLayout();
            this.config = config;
            assistantSettings = AssistantSettings.Load(AppConfig.DataDir); assistantKeys = new AssistantKeyStore(AppConfig.DataDir);
            assistantTabShown = AssistantSettings.ResolveShowTab(AppConfig.DataDir, assistantSettings);
            Text = "Slotsloader Manager · " + GetType().Assembly.GetName().Version.ToString(3);
            AppBranding.Apply(this);
            Font = Palette.Normal;
            BackColor = Palette.Background;
            ForeColor = Palette.Text;
            Size = new Size(1478,958);
            MinimumSize = new Size(1110, 650);
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleDimensions = new SizeF(96F,96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            DoubleBuffered = true;
            BuildWindow();
            CaptureTableMetrics(this);
            ResumeLayout(false);
            PerformAutoScale();
            dpiLayoutReady=true;
            Rectangle workingArea = Screen.FromControl(this).WorkingArea;
            MinimumSize = new Size(Math.Min(Px(1110),workingArea.Width),Math.Min(Px(650),workingArea.Height));
            Size = new Size(Math.Min(Px(1478),Math.Max(MinimumSize.Width,workingArea.Width-Px(48))),Math.Min(Px(958),Math.Max(MinimumSize.Height,workingArea.Height-Px(48))));
            PerformLayout();
            ApplyDpiMetrics();
            DpiChanged += delegate { BeginInvoke((MethodInvoker)delegate { pagesLaidOut.Clear(); ApplyDpiMetrics(); }); };
            UpdateLocation();
            CloakUntilPainted();   // 1.0.11 (MainFormStartPaint.cs): its Shown hook must come before the welcome's
            WireViewPersistence();
            WireDropTarget();
            WireUninstallKeys();   // 2.5.2 (MainFormKeys.cs)
            searchTimer.Tick += delegate { searchTimer.Stop(); ApplyFilters(); };
            Load += delegate { if (!Program.Headless && screenshotFile==null) RestoreView(ViewPreferences.Load(ViewPreferencesPath)); };
            Shown += delegate
            {
                if (!Program.Headless && screenshotFile==null) { RestoreSplitters(); rememberView=true; }
                if (firstLoad) { firstLoad = false; if (screenshotFile != null && !screenshotAvailableSize) ClientSize = new Size(Px(1460), Px(920)); RefreshLibrary(); }
                // 1.0.11: the welcome once, over the opened window (marked seen first, so a crash doesn't bring it back).
                // 1.0.15: then the Mod library tour, unless the welcome's Help link was used (it runs at the next start).
                if (rememberView && viewPreferences != null && !viewPreferences.WelcomeSeen)
                    BeginInvoke((MethodInvoker)delegate { viewPreferences.WelcomeSeen = true; SaveView(); if (!WelcomeForm.ShowWelcome(this, ShowHelp)) StartTour(LibraryTourId, true); });
                else if (rememberView) BeginInvoke((MethodInvoker)delegate { StartTour(LibraryTourId, true); });
            };
            FormClosing += delegate { SaveView(); if (assistantPage != null) assistantPage.Shutdown(); };
            FormClosed += delegate { if (filterMatchCancel != null) filterMatchCancel.Cancel(); StopReadAhead(false); ClearPreviewCache(); if (previewImage != null) previewImage.Dispose(); if (hoverTimer != null) hoverTimer.Dispose(); if (portraitLoad != null) portraitLoad.Cancel(); };
            Disposed += delegate { if (hoverTimer != null) hoverTimer.Dispose(); viewSaveTimer.Dispose(); searchTimer.Dispose(); };
        }

        private int Px(int logical) {return dpiLayoutReady ? DpiMetrics.Px(this,logical) : logical;}
        // "12 mods found in 2 libraries" (1 mod / 1 library); mods < 0 gives the suffix the review compares against.
        internal static string LibraryFoundText(int mods, int libraries) { return (mods < 0 ? "" : ReplacePlanner.N(mods, "mod")) + " found in " + ReplacePlanner.N(libraries, "library"); }
        // 2.4.0 (no tooltips): the package's native character codes are part of the meta line.
        // 2.5.9 byline: every modinfo.ini author of the open mod ("by A, B +1"); no author, no line.
        private Action layoutInspectorInfo;
        private List<string> PackageAuthors() { return package == null ? new List<string>() : Library.CleanAuthors(package.Components.Select(c => c.Author)); }
        internal void SetByline(IList<string> authors)
        {
            string text = Library.AuthorText(authors);
            packageMeta.Text = text.Length == 0 ? "" : "by " + text;
            if (layoutInspectorInfo != null) layoutInspectorInfo();
        }
        // 1.0.5: the mod's version is its top option's version= (else the first one any option names).
        private string PackageVersion()
        {
            if (package == null) return "";
            ModComponent root = package.Components.FirstOrDefault(c => c.ParentId == null && !String.IsNullOrWhiteSpace(c.Version));
            return (root ?? package.Components.FirstOrDefault(c => !String.IsNullOrWhiteSpace(c.Version)) ?? new ModComponent()).Version ?? "";
        }
        internal static string VersionText(string version)
        {
            string v = (version ?? "").Trim();
            return v.Length == 0 ? "" : Char.IsDigit(v[0]) ? "v" + v : v;
        }
        internal void SetVersion(string version)
        {
            componentVersion.Text = VersionText(version);
            componentVersion.Visible = componentVersion.Text.Length > 0;
            PlaceVersion();
        }
        private int Ascent(Font font)
        {
            FontFamily family = font.FontFamily;
            return (int)Math.Round(font.SizeInPoints * DeviceDpi / 72F * family.GetCellAscent(font.Style) / family.GetEmHeight(font.Style));
        }
        private void PlaceVersion()
        {
            if (componentVersion == null) return;
            Padding pad = componentTitle.Padding;
            int width = !componentVersion.Visible ? 0 : Math.Min(TextRenderer.MeasureText(componentVersion.Text, componentVersion.Font).Width + Px(4), componentTitle.ClientSize.Width * 2 / 5);
            // On the title's baseline (each font's ascent), never below the row: the row clips anything lower.
            int top = pad.Top + Math.Max(0, Ascent(componentTitle.Font) - Ascent(componentVersion.Font));
            top = Math.Max(0, Math.Min(top, componentTitle.ClientSize.Height - componentVersion.Font.Height));
            componentVersion.Bounds = new Rectangle(componentTitle.ClientSize.Width - width, top, width, Math.Max(1, componentTitle.ClientSize.Height - top));
            componentTitle.Padding = new Padding(pad.Left, pad.Top, width == 0 ? 0 : width + Px(10), pad.Bottom);   // a long title ends in "…" before it
        }
        private void CaptureTableMetrics(Control root)
        {
            var table=root as TableLayoutPanel;
            if(table!=null) tableMetrics.Add(Tuple.Create(table,table.ColumnStyles.Cast<ColumnStyle>().Select(s=>s.Width).ToArray(),table.RowStyles.Cast<RowStyle>().Select(s=>s.Height).ToArray()));
            foreach(Control child in root.Controls) CaptureTableMetrics(child);
        }
        private void ApplyDpiMetrics()
        {
            applyingDpi=true;
            foreach(var metrics in tableMetrics)
            {
                for(int i=0;i<metrics.Item2.Length;i++) if(metrics.Item1.ColumnStyles[i].SizeType==SizeType.Absolute) metrics.Item1.ColumnStyles[i].Width=Px((int)metrics.Item2[i]);
                for(int i=0;i<metrics.Item3.Length;i++) if(metrics.Item1.RowStyles[i].SizeType==SizeType.Absolute) metrics.Item1.RowStyles[i].Height=Px((int)metrics.Item3[i]);
            }
            // Invisible pages are docked only when first shown. Apply their
            // splitter minimums after they have a real client extent.
            if(librarySplit.Width>=Px(952)) {librarySplit.SplitterWidth=Px(12);librarySplit.Panel1MinSize=Px(220);librarySplit.Panel2MinSize=Px(720);RestoreSplitter(librarySplit,Px(libraryWidthChosen ? libraryLogicalWidth : DefaultLibraryFor()));}
            if(installedSplit.Width>=Px(768)) {installedSplit.SplitterWidth=Px(18);installedSplit.Panel1MinSize=Px(460);installedSplit.Panel2MinSize=Px(290);RestoreSplitter(installedSplit,Px(installedLogicalWidth));}
            componentTree.ItemHeight=Px(32); componentTree.Indent=Px(18);
            foreach(var list in new[]{modList,installedList,fileList,batchList})   // 1.0.6: the queue list too
            {
                if(list==null) continue;
                var buffered=(BufferedModList)list;
                if(list.SmallImageList!=null) list.SmallImageList.ImageSize=new Size(Px(1),Px(buffered.LogicalRowHeight));
            }
            if(settingsLibraries.ItemHeight!=Px(50)) settingsLibraries.ItemHeight=Px(50);
            foreach(Action layout in dpiLayouts) layout();
            foreach(var field in new[]{search,installedSearch,outfitName,settingsGame,settingsSevenZip,settingsApiKey})field.LayoutEditor();
            if (assistantPage != null) assistantPage.ApplyDpi();
            componentTree.Invalidate(); modList.Invalidate(); installedList.Invalidate(); if(batchList!=null) batchList.Invalidate();
            applyingDpi=false;
        }

        private static string ViewPreferencesPath { get { return Path.Combine(AppConfig.DataDir,"view-settings.json"); } }
        private void WireViewPersistence()
        {
            viewSaveTimer.Tick+=delegate {SaveView();};
            LocationChanged+=delegate { QueueViewSave(); };
            SizeChanged+=delegate { if (WindowState!=FormWindowState.Minimized) lastMaximized=WindowState==FormWindowState.Maximized; QueueViewSave(); };
            librarySplit.SplitterMoved+=delegate {if(!applyingDpi) libraryLogicalWidth=(int)Math.Round(librarySplit.SplitterDistance*96.0/DeviceDpi);QueueViewSave();};
            // 1.0.15: SplitterMoving fires only for a drag (mouse or keyboard); until then the width follows the window.
            librarySplit.SplitterMoving+=delegate {libraryWidthChosen=true;};
            librarySplit.SizeChanged+=delegate {if(!libraryWidthChosen && dpiLayoutReady && !applyingDpi && librarySplit.Width>=Px(952)) {applyingDpi=true;try{RestoreSplitter(librarySplit,Px(DefaultLibraryFor()));}finally{applyingDpi=false;}}};
            installedSplit.SplitterMoved+=delegate {if(!applyingDpi) installedLogicalWidth=(int)Math.Round(installedSplit.SplitterDistance*96.0/DeviceDpi);QueueViewSave();};
            installedState.SelectedIndexChanged+=delegate {QueueViewSave();};
            filterPanel.FiltersChanged+=delegate {QueueViewSave();};
            search.TextChanged+=delegate {QueueViewSave();}; installedSearch.TextChanged+=delegate {QueueViewSave();};
            overridesOnly.CheckedChanged+=delegate {QueueViewSave();};
        }
        private void QueueViewSave()
        {
            if (!rememberView || Program.Headless || IsDisposed) return;
            viewSaveTimer.Stop(); viewSaveTimer.Start();
        }
        private void SaveView()
        {
            viewSaveTimer.Stop();
            if (!rememberView || Program.Headless || IsDisposed) return;
            try { CaptureView().Save(ViewPreferencesPath); viewSaveErrorLogged=false; }
            catch (Exception ex)
            {
                if (!viewSaveErrorLogged) Program.Log(ex); viewSaveErrorLogged=true;
                SetPageStatus(currentPage,"Could not save view settings: "+ex.Message,Palette.Red);
            }
        }
        private ViewPreferences CaptureView()
        {
            Rectangle bounds=WindowState==FormWindowState.Normal ? Bounds : RestoreBounds;
            return new ViewPreferences {
                Dpi=DeviceDpi, HasBounds=bounds.Width>0 && bounds.Height>0, X=bounds.X,Y=bounds.Y,Width=bounds.Width,Height=bounds.Height,
                Maximized=WindowState==FormWindowState.Maximized || (WindowState==FormWindowState.Minimized && lastMaximized),
                LibraryWidth=libraryWidthChosen ? (int)Math.Round(librarySplit.SplitterDistance*96.0/DeviceDpi) : 0,InstalledWidth=(int)Math.Round(installedSplit.SplitterDistance*96.0/DeviceDpi),Page=lastMainPage,
                Search=search.Text,Character=SingleFighterName(),
                InstalledOnly=filterPanel.InstalledOn,OverridesOnly=overridesOnly.Checked,
                InstalledSearch=installedSearch.Text,InstalledCharacter=SavedInstalledCharacters(),InstalledState=Convert.ToString(installedState.SelectedItem),
                FiltersOpen=filterPanel.Visible,
                Fighters=filterPanel.Grid.Selected.OrderBy(c=>c,StringComparer.Ordinal).ToList(),
                Kinds=new[]{FilterPanel.ToolsKind,FilterPanel.MiscKind}.Where(k=>filterPanel.Kinds.Selected.Contains(k)).ToList(),SelectedLibraries=filterPanel.LibrariesSelected.OrderBy(k=>k,StringComparer.OrdinalIgnoreCase).ToList(),MiscAreas=MiscKindsSelectedOrPending().ToList(),
                LibrarySort=LibrarySorting.Code(librarySort),LibrarySortOrder=librarySortAscending ? "asc" : "desc",InstalledSort=InstalledSortCode(installedSort),InstalledSortOrder=installedSortAscending ? "asc" : "desc",LibraryNames=libraryFileNames ? "file" : "name",DeepSearch=DeepSearchOn,
                FiltersOnly=CustomIds(CustomFilterState.Only),FiltersHidden=CustomIds(CustomFilterState.Hide),
                WelcomeSeen=viewPreferences!=null && viewPreferences.WelcomeSeen,
                ToursSeen=viewPreferences!=null && viewPreferences.ToursSeen!=null ? viewPreferences.ToursSeen : new List<string>(),
                HelpTopic=HelpResumeTopic ?? "",HelpOffset=HelpResumeOffset
            };
        }
        // Older builds read one character name; keep it meaningful when exactly one fighter is selected.
        private string SingleFighterName()
        {
            var selected=filterPanel.Grid.Selected;
            return selected.Count==1 ? FighterPortraits.DisplayName(selected.First()) : "";
        }
        private static void SelectPreference(ComboBox combo,string value)
        {
            int selected=-1;
            for(int i=0;i<combo.Items.Count;i++) if(String.Equals(Convert.ToString(combo.Items[i]),value,StringComparison.OrdinalIgnoreCase)) {selected=i;break;}
            combo.SelectedIndex=selected<0 ? 0 : selected;
        }
        private void RestoreView(ViewPreferences saved)
        {
            viewPreferences=saved;
            if(saved.HasBounds && saved.Width>0 && saved.Height>0)
            {
                // v1 bounds came from a DPI-virtualized window. v2 stores
                // physical bounds and their DPI; pane widths stay logical.
                double ratio=DeviceDpi/(double)Math.Max(96,saved.Dpi);
                if(saved.Format=="sf6-installer-view-v1") {saved.X=(int)Math.Round(saved.X*ratio);saved.Y=(int)Math.Round(saved.Y*ratio);}
                saved.Width=(int)Math.Round(saved.Width*ratio);saved.Height=(int)Math.Round(saved.Height*ratio);
                Rectangle area=Screen.FromPoint(new Point(saved.X,saved.Y)).WorkingArea;
                MinimumSize=new Size(Math.Min(MinimumSize.Width,area.Width),Math.Min(MinimumSize.Height,area.Height));
                StartPosition=FormStartPosition.Manual; Bounds=saved.VisibleBounds(area,MinimumSize);
            }
            search.Text=saved.Search ?? "";
            overridesOnly.Checked=saved.OverridesOnly;
            installedSearch.Text=saved.InstalledSearch ?? ""; RestoreInstalledCharacters(saved.InstalledCharacter); SelectPreference(installedState,saved.InstalledState);
            RestoreFilterPanel(saved);
            RestoreLibraryView(saved);   // 1.0.6: Name · File and Deep search
            RestoreLibrarySort(saved);
            RestoreInstalledSort(saved);   // 2.5.5 (FillInstalled runs with the next refresh)
            // 2.4.1: a saved Assistant view opens Mod library while the tab is hidden.
            ShowPage(saved.Page=="Installed" ? installedPage : saved.Page=="Assistant" && assistantTabShown ? (Panel)EnsureAssistantPage() : libraryPage);
            // 2.5.4: Filters are open at every start (the saved open/closed state is no longer used). On a saved Installed
            // or Assistant page the panel is open, without switching page, for when the library is shown.
            if(currentPage==libraryPage) ShowFilters(true,false);
            else {choicesHost.Visible=false;filterPanel.Visible=true;portraitsRequested=true;EnsurePortraits(false);UpdateFilterButton();}
            if(saved.Maximized) WindowState=FormWindowState.Maximized;
            lastMaximized=saved.Maximized;
        }
        private static void RestoreSplitter(SplitContainer split,int width)
        {
            int maximum=split.ClientSize.Width-split.SplitterWidth-split.Panel2MinSize;
            if(width>0 && maximum>=split.Panel1MinSize) split.SplitterDistance=Math.Max(split.Panel1MinSize,Math.Min(width,maximum));
        }
        // 330 on the default window (library split about 1430 wide), less on a smaller one, never under the old 250.
        private int DefaultLibraryFor()
        {
            double split=librarySplit.Width*96.0/DeviceDpi;
            return Math.Max(OldDefaultLibraryWidth,Math.Min(DefaultLibraryWidth,(int)Math.Round(split*DefaultLibraryWidth/1420.0)));
        }
        private void RestoreSplitters()
        {
            if(viewPreferences==null) return;
            // Until 1.0.15 every saved view had a width, dragged or not; one within DPI rounding of the old default was
            // never dragged. Since 1.0.15 an undragged library saves 0.
            if(viewPreferences.LibraryWidth>0 && Math.Abs(viewPreferences.LibraryWidth-OldDefaultLibraryWidth)>2) {libraryLogicalWidth=viewPreferences.LibraryWidth;libraryWidthChosen=true;}
            if(viewPreferences.InstalledWidth>0) installedLogicalWidth=viewPreferences.InstalledWidth;
            ApplyDpiMetrics();
        }

        internal void ConfigureOpeningSource(string source) { openingSource = source; }

        internal void ConfigureScreenshot(string outputFile, string source, string page, bool availableSize)
        {
            screenshotFile = Path.GetFullPath(outputFile);
            screenshotSource = source;
            screenshotPage = page;
            screenshotAvailableSize = availableSize;
            if (!availableSize) { FormBorderStyle = FormBorderStyle.None; MaximumSize = new Size(Px(1800), Px(1200)); }
            ShowInTaskbar = false;
            Opacity = 0;
        }

        internal static int ExerciseUI(string source)
        {
            AppConfig settings = AppConfig.Load();
            using (MainForm form = new MainForm(settings))
            {
                form.CreateControl(); form.componentTree.CreateControl();
                form.package = PackageReader.Load(source, settings.SevenZipPath);
                form.FillComponents(Composer.DefaultSelection(form.package));
                string[] names = { "1) Main files", "Default", "Regular full outfit barefoot", "hide shirt" };
                List<ModComponent> required = names.Select(name => form.package.Components.First(c => String.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))).ToList();
                foreach (ModComponent c in required) form.componentNodes[c.Id].Checked = true;
                if (required.Any(c => !form.componentNodes[c.Id].Checked)) throw new InvalidOperationException("UI regression: selecting an independent optional component deselected another required component.");
                if (required.Any(c => !form.selectedOrder.Contains(c.Id))) throw new InvalidOperationException("UI regression: checked components were missing from the composition order.");
                CompositionPlan combined = Composer.Plan(form.package, form.selectedOrder);
                if (!combined.CanInstall) throw new InvalidOperationException("UI regression: complete bundle failed: " + String.Join("; ", combined.Errors));
                ModComponent alternative = form.package.Components.First(c => String.Equals(c.Name, "oily", StringComparison.OrdinalIgnoreCase));
                form.componentNodes[alternative.Id].Checked = true;
                if (form.componentNodes[required[1].Id].Checked || !form.componentNodes[alternative.Id].Checked) throw new InvalidOperationException("UI regression: choosing another skin did not replace the previous skin choice.");
                if (!form.componentNodes[required[0].Id].Checked || !form.componentNodes[required[2].Id].Checked || !form.componentNodes[required[3].Id].Checked) throw new InvalidOperationException("UI regression: choose-one skin affected independent selections.");
                form.componentNodes[required[1].Id].Checked = true;
                ModComponent dummy = form.package.Components.First(c => c.IsDummy && c.ParentId == null);
                form.componentNodes[dummy.Id].Checked = true;
                if (form.componentNodes[dummy.Id].Checked) throw new InvalidOperationException("UI regression: a dummy group became installable.");
                if (required.Any(c => !form.componentNodes[c.Id].Checked)) throw new InvalidOperationException("UI regression: dummy group click modified selected files.");
                if (form.componentNodes[required[0].Id].Parent == null) throw new InvalidOperationException("UI regression: empty root component ID did not resolve as the main component's parent.");
                List<string> restored = new List<string> { required[0].Id, required[1].Id, required[3].Id, required[2].Id };
                form.FillComponents(restored);
                if (!form.selectedOrder.SequenceEqual(restored)) throw new InvalidOperationException("UI regression: restoring a saved outfit changed its component precedence.");
                Directory.CreateDirectory(AppConfig.DataDir);
                File.WriteAllText(Path.Combine(AppConfig.DataDir, "ui-exercise.txt"), "PASS: main + skin + outfit + hide-shirt remain selected\r\nPASS: choose-one skin replaces only its sibling\r\nPASS: dummy categories remain unchecked\r\nPASS: Addonfor hierarchy resolves\r\nPASS: saved option precedence restores unchanged\r\nPASS: complete selection plans " + combined.Files.Count + " merged files without game writes\r\n");
                return 0;
            }
        }

        private void BuildWindow()
        {
            Panel status = new Panel { Dock = DockStyle.Bottom, Height = 30, BackColor = Palette.Sidebar, Padding = new Padding(18, 0, 18, 0) };
            statusText = new Label { UseMnemonic=false, Dock = DockStyle.Fill, Text = "Ready", TextAlign = ContentAlignment.MiddleLeft, ForeColor = Palette.Muted, Font = Palette.Small };
            statusProgress = new ProgressBar { Dock = DockStyle.Right, Width = 150, Height = 6, Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 30, Visible = false };
            cancelWork=Button("Cancel",false,78,delegate {if(activeOperation!=null) {activeOperation.Cancel();cancelWork.Enabled=false;statusText.Text="Cancelling… finishing the current safe step.";}});
            cancelWork.Dock=DockStyle.Right;cancelWork.Font=Palette.Small;cancelWork.Visible=false;
            status.Controls.Add(statusText); status.Controls.Add(statusProgress);status.Controls.Add(cancelWork); BuildUndo(status); status.Controls.Add(BuildSupportLinks());   // 2.5.9: outermost right
            Controls.Add(status); statusPanel = status;

            Panel header = new Panel { Dock = DockStyle.Top, Height = 48, BackColor = Palette.Sidebar, Padding = new Padding(16, 6, 16, 6) };
            FlowLayoutPanel headerActions = new FlowLayoutPanel { Dock = DockStyle.Left, Width = 448, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Padding = Padding.Empty };
            navLibrary = Button("Mod library", false, 116, delegate { ShowPage(libraryPage); });
            navInstalled = Button("Installed", false, 104, delegate { ShowPage(installedPage); RefreshInstalled(); });
            navAssistant = Button("Assistant", false, 108, delegate { ShowAssistant(); });
            ContextMenuStrip toolsMenu = new ContextMenuStrip { BackColor = Palette.Surface, ForeColor = Palette.Text, ShowImageMargin = false, Font = Palette.Normal };
            toolsMenu.Renderer = new ToolStripProfessionalRenderer(new DarkMenuColors());
            ToolStripMenuItem settingsItem = new ToolStripMenuItem("Settings…", null, delegate { RunShortcut(ShortcutAction.SettingsPage); }) { ShortcutKeyDisplayString = Shortcuts.Display(ShortcutAction.SettingsPage) };
            toolsMenu.Items.Add(settingsItem);
            // 2.5.9: no Open costume folder, Refresh libraries (F5 / ↻ stay) or Import external preset (Settings → Game files) here.
            // 1.0.1: no Recover removed outfits (uninstall keeps no copy; the user asked to remove it).
            toolsMenu.Items.Add("History", null, delegate { if (!busy) ShowHistory(); });   // 2.4.1 (MainFormHistory.cs)
            // 1.0.1 (the user): Restore → this Manager's mods only (Undo), or the whole game back to vanilla (MainFormUndo.cs).
            ToolStripMenuItem restoreItem = new ToolStripMenuItem("Restore");
            var restoreMenu = (ToolStripDropDownMenu)restoreItem.DropDown;
            restoreMenu.ShowImageMargin = false; restoreMenu.BackColor = Palette.Surface; restoreMenu.ForeColor = Palette.Text;
            restoreMenu.Renderer = new ToolStripProfessionalRenderer(new DarkMenuColors());
            restoreItem.DropDownItems.Add("Remove this Manager's mods", null, delegate { RestoreOriginalGameFiles(); });
            restoreItem.DropDownItems.Add("Restore game to vanilla…", null, delegate { RestoreGameToVanilla(); });
            toolsMenu.Items.Add(restoreItem);
            toolsMenu.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem shortcutsItem = new ToolStripMenuItem("Keyboard shortcuts");
            var shortcutsMenu = (ToolStripDropDownMenu)shortcutsItem.DropDown;
            shortcutsMenu.ShowImageMargin = false; shortcutsMenu.BackColor = Palette.Surface; shortcutsMenu.ForeColor = Palette.Text; shortcutsMenu.Font = Palette.Normal;
            shortcutsMenu.Renderer = new ToolStripProfessionalRenderer(new DarkMenuColors());
            foreach (ShortcutInfo shortcut in Shortcuts.All)
            {
                ShortcutInfo item = shortcut;
                var entry = new ToolStripMenuItem(item.Description, null, delegate { if (item.Action != ShortcutAction.ClearSearch) RunShortcut(item.Action); }) { ShortcutKeyDisplayString = item.Display, Tag = item, Enabled = item.Action != ShortcutAction.ClearSearch };
                shortcutsItem.DropDownItems.Add(entry);
                if (item.Action == ShortcutAction.AssistantPage) assistantShortcutItem = entry;
            }
            toolsMenu.Items.Add(shortcutsItem);
            // 1.0.11 (the user): Help here (F1); no Welcome (Settings → General → Show welcome) and no About (Settings → About tab).
            toolsMenu.Items.Add(new ToolStripMenuItem("Help", null, delegate { RunShortcut(ShortcutAction.HelpPage); }) { ShortcutKeyDisplayString = Shortcuts.Display(ShortcutAction.HelpPage) });
            toolsMenu.Items.Add(BuildTourMenu());   // 1.0.15: the three tours again (MainFormTours.cs)
            RoomyMenu(toolsMenu); RoomyMenu(shortcutsMenu); RoomyMenu(restoreMenu);   // 1.0.1: larger text and taller rows (the user)
            toolsMenu.Opening+=delegate {if (assistantShortcutItem != null) assistantShortcutItem.Visible = assistantTabShown;};
            navSettings = Button("", false, 42, delegate { toolsMenu.Show(navSettings, new Point(0, navSettings.Height)); });
            navSettings.ContextMenuStrip = toolsMenu; navSettings.Glyph = ButtonGlyph.Gear;
            navSettings.Padding = new Padding(0);
            navSettings.AccessibleName = "Settings and tools";
            navSettings.Disposed += delegate { toolsMenu.Dispose(); };
            foreach (FlatButton button in new[] { navLibrary, navInstalled, navAssistant, navSettings }) { button.Height = 36; button.Margin = new Padding(0, 0, 6, 0); }
            headerActions.Controls.Add(navLibrary); headerActions.Controls.Add(navInstalled); headerActions.Controls.Add(navAssistant); headerActions.Controls.Add(navSettings);
            navLibrary.Navigation = true; navInstalled.Navigation = true; navAssistant.Navigation = true; navSettings.Navigation = true; header.Controls.Add(headerActions);
            // Library source actions live in the header, freeing a full row
            // above the panes for options. They show on the library page only.
            headerTools = new FlowLayoutPanel { Dock = DockStyle.Right, Width = 300, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Padding = new Padding(0, 2, 0, 0), Margin = Padding.Empty };
            openFolderButton = Button("Open folder…", false, 140, delegate { RunShortcut(ShortcutAction.OpenFolder); });
            openArchiveButton = Button("Open archive…", false, 140, delegate { RunShortcut(ShortcutAction.OpenArchive); });
            foreach (FlatButton button in new[] { openFolderButton, openArchiveButton }) { button.Height = 32; button.Margin = new Padding(8, 0, 0, 0); button.Font = Palette.Small; headerTools.Controls.Add(button); }
            header.Controls.Add(BuildGameTools());
            header.Controls.Add(headerTools);
            Controls.Add(header);

            Panel shell = new Panel { Dock = DockStyle.Fill };
            Panel pages = new Panel { Dock = DockStyle.Fill, Padding = new Padding(14, 10, 14, 10) };
            // 2.4.1: the Assistant page is added on first show (EnsureAssistantPage).
            libraryPage = BuildLibraryPage(); installedPage = BuildInstalledPage(); settingsPage = BuildSettingsPage(); pagesPanel = pages;
            pages.Controls.Add(libraryPage); pages.Controls.Add(installedPage); pages.Controls.Add(settingsPage);
            shell.Controls.Add(pages);
            dropOverlay = new DropOverlay { Dock = DockStyle.Fill };
            shell.Controls.Add(dropOverlay);
            pages.BringToFront();
            Controls.Add(shell);
            shell.BringToFront();
            ShowPage(libraryPage);
            ApplyAssistantTab();
            DarkTheme.Apply(this);
        }

        private Panel BuildLibraryPage()
        {
            Panel page = new Panel { Dock = DockStyle.Fill };

            SplitContainer split = new SplitContainer { Size = new Size(1180, 680), Dock = DockStyle.Fill, SplitterDistance = DefaultLibraryWidth, SplitterWidth = 12, BackColor = Palette.Background, Panel1MinSize = 220, Panel2MinSize = 720, FixedPanel = FixedPanel.Panel1 };
            librarySplit=split;
            Panel browser = new Panel { Dock = DockStyle.Fill, BackColor = Palette.Surface, Padding = new Padding(10) };
            // 2.4.1: row 2 is the sort strip (LibrarySort.cs): 20 px chips and the Rhythm.Related gap to the list.
            // 1.0.11 (the user): a Rhythm.Related gap under the count / Filters row, so Filters doesn't sit on the search box.
            TableLayoutPanel filters = new TableLayoutPanel { Dock = DockStyle.Top, Height = 63 + Rhythm.Related + SearchOptionsHeight + SortChipHeight + Rhythm.Related, ColumnCount = 2, RowCount = 4, Padding = new Padding(0), Margin = new Padding(0) };
            // 2.4.1: the right cell holds Filters and, while a filter is active, Clear. Its width is fixed, so neither
            // the Filters button nor the count moves when Clear appears.
            filters.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); filters.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 142));
            filters.RowStyles.Add(new RowStyle(SizeType.Absolute, 26 + Rhythm.Related)); filters.RowStyles.Add(new RowStyle(SizeType.Absolute, 37)); filters.RowStyles.Add(new RowStyle(SizeType.Absolute, SearchOptionsHeight)); filters.RowStyles.Add(new RowStyle(SizeType.Absolute, SortChipHeight + Rhythm.Related));
            // The caption carries the live count that previously used its own row.
            libraryCount = Caption("MOD LIBRARY"); libraryCount.Dock = DockStyle.Fill; libraryCount.AutoEllipsis = true; libraryCount.Margin = new Padding(libraryCount.Margin.Left, 0, libraryCount.Margin.Right, Rhythm.Related); filters.Controls.Add(libraryCount, 0, 0);
            filterButton = Button("Filters", false, 84, delegate { ShowFilters(!filterPanel.Visible, false); });
            filterButton.Dock = DockStyle.Right; filterButton.Height = 25; filterButton.Margin = new Padding(0, 0, 0, 1); filterButton.Font = Palette.Small; filterButton.AccessibleName = "Show library filters"; filterButton.TabIndex = 0;
            filterClearButton = Button("Clear", false, 54, delegate { bool hadFocus = filterClearButton.Focused; ClearLibraryFilters(); if (hadFocus && filterButton.CanFocus) filterButton.Focus(); });
            filterClearButton.Dock = DockStyle.Right; filterClearButton.Height = 25; filterClearButton.Font = Palette.Small; filterClearButton.AccessibleName = "Clear filters"; filterClearButton.Visible = false; filterClearButton.TabIndex = 1;
            var filterActions = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 1 + Rhythm.Related), Padding = Padding.Empty };
            filterActions.Controls.Add(filterClearButton); filterActions.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 4 }); filterActions.Controls.Add(filterButton);
            filterButton.SendToBack();   // docked first: the rightmost
            filters.Controls.Add(filterActions, 1, 0);
            search = TextField(); search.Dock = DockStyle.Fill; search.Margin = new Padding(0, 0, 0, 7); search.TrailingSpace=27; search.PlaceholderText="Search or is:bundle"; search.AccessibleName="Search mod library"; search.TextChanged += delegate { libraryToTop = true; QueueLibraryFilter(); };
            filters.Controls.Add(search, 0, 1); filters.SetColumnSpan(search, 2);
            Panel searchOptions = BuildSearchOptions(); filters.Controls.Add(searchOptions, 0, 2); filters.SetColumnSpan(searchOptions, 2);   // 1.0.6
            Panel sorts = BuildSortStrip(); filters.Controls.Add(sorts, 0, 3); filters.SetColumnSpan(sorts, 2);
            search.Editor.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter && e.Modifiers == Keys.None) { e.Handled = e.SuppressKeyPress = true; OpenTopLibraryMatch(); }
                else if (e.KeyCode == Keys.Down && e.Modifiers == Keys.None && modList.Items.Count > 0)
                {
                    e.Handled = e.SuppressKeyPress = true; FlushLibraryFilter();
                    if (modList.Items.Count > 0) { modList.Focus(); if (modList.FocusedItem == null) modList.Items[0].Focused = true; }
                }
            };
            searchClear = SearchClear.Attach(search, FlushLibraryFilter);   // 2.4.1: the shared × (also on Installed)
            // 2.3.0: the Filters panel (FilterPanel.cs) opens to the right of this list, in place of the options
            // column; the shared filters below its tabs replace the 2.2.0 popup.
            filterPanel = new FilterPanel { Dock = DockStyle.Fill, Visible = false };
            filterPanel.FiltersChanged += delegate { if (quietFilters == 0) libraryToTop = true; ApplyFilters(); };   // 1.0.6: a filter change shows the first match, like a search
            filterPanel.Grid.SetFighters(GridFighters());
            // 2.3.1: no hover tooltips anywhere in the panel (the help text is in UI-REFERENCE.md); accessible names stay.
            // 2.4.1: the panel has no Clear of its own; the list header's Clear (filterClearButton) is the only one.
            filterPanel.SetLibraries(config.Libraries);
            BuildCustomFilters();   // 1.0.8: MY FILTERS (MainFormCustomFilters.cs)
            // 2.3.0: the costume, format, library and mod-type dropdowns were removed; their search tokens remain
            // (c1 / costume:1, format:zip, is:bundle, is:normal, is:unreadable, is:tool). "Installed mods only" is the
            // Installed tile (FilterPanel.InstalledOn).
            modList = CreateList(58); ((BufferedModList)modList).RepaintAfterScroll = true; modList.ShowItemToolTips = false; modList.Columns.Add("Mod", 300); modList.DrawSubItem += DrawLibraryItem;
            modList.MultiSelect = true;   // 1.0.6: Ctrl/Shift+click or Ctrl+A queue several mods (MainFormBatch.cs)
            ((BufferedModList)modList).SuppressIdleHover = true;
            modList.HotTracking = false; modList.HoverSelection = false;
            ((BufferedModList)modList).BeforePointerSelection = delegate(Point point)
            {
                ListViewItem row = modList.GetItemAt(point.X, point.Y);
                return CommitRenameBeforeSelection(row == null ? null : row.Tag as LibraryEntry);
            };
            // 1.0.6: read once the selection settles: one row opens the mod, several open the queue (MainFormBatch.cs).
            modList.ItemSelectionChanged += delegate(object sender, ListViewItemSelectionChangedEventArgs e) { LibraryItemSelected(e); };
            modList.SelectedIndexChanged += delegate { LibrarySelectionEvent(); };
            modList.MouseClick += delegate(object sender, MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Left || (busy && !packageLoading)) return;   // 1.0.12: a pick during a mod's read waits as the next
                if ((Control.ModifierKeys & (Keys.Control | Keys.Shift)) != 0 || modList.SelectedItems.Count > 1) return;   // 1.0.6: building a selection
                ListViewItem row = modList.GetItemAt(e.X, e.Y);
                LibraryEntry entry = row == null ? null : row.Tag as LibraryEntry;
                if (entry != null) ClosePanelForPickedMod();
                if (entry != null && (package == null || !String.Equals(package.SourcePath, entry.SourcePath, StringComparison.OrdinalIgnoreCase))) LoadPackage(entry);
            };
            modList.MouseDown += delegate(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left && modList.GetItemAt(e.X, e.Y) != null) userPickingMod = true; };
            modList.MouseUp += delegate { userPickingMod = false; };
            modList.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                Keys k = e.KeyCode;
                // 2.3.1: W/S move like Up/Down (and close the Filters panel like them); A/D do what Left/Right do in
                // this one-column list: nothing. None of the four starts the list's type-ahead jump any more.
                if (e.Modifiers == Keys.None && (k == Keys.W || k == Keys.A || k == Keys.S || k == Keys.D))
                {
                    e.Handled = e.SuppressKeyPress = true;
                    if (k == Keys.W || k == Keys.S) MoveLibrarySelection(k == Keys.W ? -1 : 1);
                    return;
                }
                if (k == Keys.Up || k == Keys.PageUp || k == Keys.Home) libraryDirection = -1;   // 1.0.12: the row read ahead (MainFormOpen.cs)
                else if (k == Keys.Down || k == Keys.PageDown || k == Keys.End) libraryDirection = 1;
                if (k == Keys.Up || k == Keys.Down || k == Keys.PageUp || k == Keys.PageDown || k == Keys.Home || k == Keys.End || k == Keys.Left || k == Keys.Right) { userPickingMod = true; BeginInvoke((Action)delegate { userPickingMod = false; }); }
                else if (k == Keys.A && e.Modifiers == Keys.Control) { e.Handled = e.SuppressKeyPress = true; SelectAllLibraryRows(); }   // 1.0.6
                else if (k == Keys.Enter && e.Modifiers == Keys.None && modList.SelectedItems.Count == 1)
                {
                    e.Handled = e.SuppressKeyPress = true;
                    ClosePanelForPickedMod();
                    LibraryEntry entry = (LibraryEntry)modList.SelectedItems[0].Tag;
                    if ((!busy || packageLoading) && (package == null || !String.Equals(package.SourcePath, entry.SourcePath, StringComparison.OrdinalIgnoreCase))) LoadPackage(entry);
                }
            };
            modList.KeyUp += delegate { userPickingMod = false; };
            Action sizeLibrary=delegate { ((BufferedModList)modList).FitSingleColumn(); };
            modList.Resize += delegate { sizeLibrary(); }; dpiLayouts.Add(sizeLibrary);
            ContextMenuStrip modMenu = new ContextMenuStrip { BackColor = Palette.Surface, ForeColor = Palette.Text, ShowImageMargin = false, Font = Palette.Normal };
            modMenu.Renderer = new ToolStripProfessionalRenderer(new DarkMenuColors());
            modMenu.Items.Add("Open", null, delegate { OpenLibrarySource(modMenu.Tag as LibraryEntry, false); });
            modMenu.Items.Add("Open in Explorer", null, delegate { OpenLibrarySource(modMenu.Tag as LibraryEntry, true); });
            AddLibraryRename(modMenu);   // 1.0.5 (MainFormRename.cs)
            modMenu.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem uninstallItem = new ToolStripMenuItem("Uninstall outfit");
            uninstallItem.Click += delegate { LibraryEntry entry = modMenu.Tag as LibraryEntry; if (entry != null) UninstallAllOutfits(entry.SourcePath); };   // 2.4.1: all of its extra outfits, Undo
            modMenu.Items.Add(uninstallItem);
            // 2.0.3: sources installed as Replace-original replacements or tool mods.
            ToolStripMenuItem uninstallReplacementItem = new ToolStripMenuItem("Uninstall replacement") { Visible = false };
            uninstallReplacementItem.Click += delegate { LibraryEntry entry = modMenu.Tag as LibraryEntry; if (entry != null) UninstallAllReplacements(entry.SourcePath); };   // 2.4.1: all of its records, Undo
            modMenu.Items.Add(uninstallReplacementItem);
            // 2.2.0: sources Fluffy Mod Manager installed (its Mods folder entry matches an installed.ini section).
            ToolStripMenuItem uninstallFluffyItem = new ToolStripMenuItem("Uninstall mod (external manager)") { Visible = false };
            uninstallFluffyItem.Click += delegate { GameModRow row = uninstallFluffyItem.Tag as GameModRow; if (row != null) UninstallFluffyRow(row); };
            modMenu.Items.Add(uninstallFluffyItem);
            AddLibraryDelete(modMenu);   // 2.4.1: separator + Delete (to the Recycle Bin), last, danger colour (MainFormDelete.cs)
            modList.ContextMenuStrip = modMenu;
            modList.MouseDown += delegate(object sender, MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Right) return;
                ListViewItem row = modList.GetItemAt(e.X, e.Y);
                modMenu.Tag = row == null ? null : row.Tag;
            };
            modList.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Apps || (e.KeyCode == Keys.F10 && e.Shift))
                    modMenu.Tag = modList.SelectedItems.Count == 0 ? null : modList.SelectedItems[0].Tag;
                if (e.KeyCode == Keys.F2 && e.Modifiers == Keys.None && modList.SelectedItems.Count > 0) { e.Handled = true; BeginLibraryRename(modList.SelectedItems[0].Tag as LibraryEntry); }   // 1.0.5
            };
            // 1.0.5: the rename box sits on its row; scrolling or resizing the list ends the edit (Enter's rules).
            modList.MouseWheel += delegate { EndLibraryRename(true); };
            modList.Resize += delegate { EndLibraryRename(true); };
            modMenu.Opening += delegate(object sender, CancelEventArgs e)
            {
                LibraryEntry entry = modMenu.Tag as LibraryEntry;
                e.Cancel = entry == null;
                string source = entry == null ? null : entry.SourcePath;
                PopulateLibraryUninstall(uninstallItem, source);
                PopulateReplacementUninstall(uninstallReplacementItem, source);
                PopulateFluffyUninstall(uninstallFluffyItem, source);
                PopulateLibraryDelete(entry);   // 2.4.1
                PopulateLibraryRename(entry);   // 1.0.5
                // A source installed only as replacements (or by Fluffy) shows just that action; with neither, the disabled outfit action stays.
                uninstallItem.Visible = LinkedOutfits(source).Count > 0 || (LinkedReplacements(source).Count == 0 && LinkedFluffy(source).Count == 0);
            };
            modList.Disposed += delegate { modMenu.Dispose(); };
            browser.Controls.Add(modList); browser.Controls.Add(BuildLibraryEmpty()); browser.Controls.Add(filters);   // 1.0.12 (23 Oct): in place of an empty list (MainFormLibraryEmpty.cs)
            split.Panel1.Controls.Add(browser);
            split.Panel2.Controls.Add(detailPanel = BuildDetail());
            split.Panel2.Controls.Add(BuildBatchPanel()); batchPanel.BringToFront();   // 1.0.6: hidden until several mods are selected
            page.Controls.Add(split); split.BringToFront();
            return page;
        }

        private Panel BuildDetail()
        {
            Panel detail = new Panel { Dock = DockStyle.Fill, BackColor = Palette.Surface, Padding = new Padding(12) };
            packageTitle = new Label { UseMnemonic=false, Text = "Choose a mod", Dock = DockStyle.Top, Height = 34, Font = new Font("Segoe UI", 15F, FontStyle.Bold), AutoEllipsis = true };
            detail.Controls.Add(packageTitle);

            Panel footer = new Panel { Dock = DockStyle.Bottom, Height = 64, BackColor = Palette.Background, Padding = new Padding(10, 6, 8, 5) };
            FlowLayoutPanel actions = new FlowLayoutPanel { Dock = DockStyle.Right, Width = 444, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Padding = new Padding(0, 7, 0, 0) };
            installButton = Button("Install", true, 136, delegate { InstallCurrent(); }); installButton.Enabled = false;
            installButton.MouseEnter += delegate { StartReviewNow(); };   // 1.0.12: heading for Install, the check need not wait
            installButton.KeyHint = "Space"; installButton.AccessibleDescription = "Shortcut: Space";   // 2.4.1
            previewButton = Button("Review files", false, 116, delegate { BuildPlan(true); }); previewButton.Enabled = false;
            libraryUninstallButton = Button("Uninstall outfit", false, 172, delegate { UninstallLibraryOutfit(); }); libraryUninstallButton.Enabled = false; libraryUninstallButton.Danger = true;
            libraryUninstallButton.KeyHint = "Ctrl"; libraryUninstallButton.AccessibleDescription = "Shortcut: tap Ctrl";   // 2.5.2 (MainFormKeys.cs)
            foreach (FlatButton button in new[] { installButton, previewButton, libraryUninstallButton }) { button.Height = 38; button.Margin = new Padding(5, 0, 0, 0); button.Font = Palette.Small; }
            actions.Controls.Add(installButton); actions.Controls.Add(previewButton); actions.Controls.Add(libraryUninstallButton);
            actions.MouseMove += delegate(object sender, MouseEventArgs e) { if (installButton.Bounds.Contains(e.Location)) StartReviewNow(); };   // a disabled Install gets no mouse events; its container does
            Panel summary = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 1, 8, 0) };
            planSummary = new Label { UseMnemonic=false, Text = "Review your selection", Dock = DockStyle.Top, Height = 23, Font = Palette.Strong, ForeColor = Palette.Text, AutoEllipsis = true };
            planDetails = new Label { UseMnemonic=false, Text = "Select your main files and options.", Dock = DockStyle.Fill, ForeColor = Palette.Muted, Font = Palette.Small, AutoEllipsis = true };
            summary.Controls.Add(planDetails); summary.Controls.Add(planSummary);
            planSummary.Cursor = Cursors.Hand; planDetails.Cursor = Cursors.Hand;
            // Unresolved required choices: the summary jumps to the first one.
            // Otherwise both lines open the full composition report.
            planSummary.Click += delegate { if (FooterClearsMissingOptions()) return; if (unresolvedChoices.Count > 0) JumpToFirstRequiredChoice(); else if (IsReplaceMode) ShowReviewOrWait(true); else ShowCompositionReport(); };   // 2.4.2: missing options clear like Health (MainFormRecovery.cs)
            planDetails.Click += delegate { if (FooterClearsMissingOptions()) return; if (unresolvedChoices.Count > 0 && (plan == null || IsReplaceMode)) JumpToFirstRequiredChoice(); else if (IsReplaceMode) ShowReviewOrWait(true); else if (plan != null) ShowCompositionReport(); };
            planSummary.AccessibleRole = AccessibleRole.Link; planDetails.AccessibleRole = AccessibleRole.Link;
            footer.Controls.Add(summary); footer.Controls.Add(actions);
            detail.Controls.Add(footer);

            TableLayoutPanel body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = new Padding(0), Padding = new Padding(0) };
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 14));
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 250));
            body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            Panel choices = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0), Padding = new Padding(0, 0, 0, 9) };
            Panel choiceTabs = new Panel { Dock = DockStyle.Top, Height = 36 };
            componentTab = Button("Options", false, 102, delegate { ShowDetailTab(false); }); componentTab.Dock = DockStyle.Left;
            filesTab = Button("Merged files", false, 116, delegate { ShowDetailTab(true); if (plan == null && !ReviewPending && package != null) RequestReview(1); }); filesTab.Dock = DockStyle.Left;
            FlatButton expand = Button("", false, 38, delegate { componentTree.ExpandAll(); if (componentTree.Nodes.Count > 0) componentTree.TopNode = componentTree.Nodes[0]; }); expand.Dock = DockStyle.Right;
            FlatButton collapse = Button("", false, 38, delegate { componentTree.CollapseAll(); foreach (OptionNode root in componentTree.Nodes) root.Expand(); }); collapse.Dock = DockStyle.Right;
            expand.Glyph = ButtonGlyph.ExpandAll; collapse.Glyph = ButtonGlyph.CollapseAll;
            expand.AccessibleName = "Expand all option groups"; collapse.AccessibleName = "Collapse option groups";
            expand.Padding = new Padding(0); collapse.Padding = new Padding(0);
            
            expandAllButton = expand; collapseAllButton = collapse;
            // A small gap between the two icon buttons.
            Panel iconGap = new Panel { Dock = DockStyle.Right, Width = 4 };
            selectedLabel = new Label { UseMnemonic=false, Dock = DockStyle.Fill, Text = "0 selected", TextAlign = ContentAlignment.MiddleRight, ForeColor = Palette.Muted, Font = Palette.Small, Padding = new Padding(0, 0, 9, 0) };
            choiceTabs.Controls.Add(selectedLabel); choiceTabs.Controls.Add(BuildResetOptions()); choiceTabs.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 4 }); choiceTabs.Controls.Add(collapse); choiceTabs.Controls.Add(iconGap); choiceTabs.Controls.Add(expand); choiceTabs.Controls.Add(filesTab); choiceTabs.Controls.Add(new Panel { Dock = DockStyle.Left, Width = Rhythm.Related }); choiceTabs.Controls.Add(componentTab);   // 1.0.11: a gap between the two tabs (the user)
            componentPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 6, 0, 0) };
            componentTree = new OptionTree { Dock = DockStyle.Fill, Indent = 18, ItemHeight = 32, BackColor = Palette.Surface, ForeColor = Palette.Text, Font = new Font("Segoe UI", 10.5F) };
            componentTree.EmptyText = "Choose a mod from the library, or drop an archive (ZIP, RAR, 7Z) or mod folder anywhere in this window.\n\nCtrl+O opens an archive · Ctrl+F searches the library";
            componentTree.DrawNode += DrawComponentNode;
            // OptionTree paints the background, indicator and text together;
            // no native TreeView hot tracking/state images can overpaint them.
            // 2.4.1: Enter toggles the focused option (Space installs, see ProcessCmdKey).
            componentTree.AccessibleDescription = "Click an option or press Enter to include it. Space installs the selection.";
            componentTree.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode != Keys.Enter || e.Modifiers != Keys.None) return;
                e.Handled = true; e.SuppressKeyPress = true;
                OptionNode node = componentTree.SelectedNode;
                ModComponent option = node == null ? null : node.Tag as ModComponent;
                if (option != null && !option.IsDummy) node.Checked = !node.Checked;
            };
            componentTree.AfterCheck += ComponentChecked;
            componentTree.AfterSelect += delegate(object s, OptionNodeEventArgs e) { ModComponent c = e.Node.Tag as ModComponent; if (c != null) ShowComponent(c); };
            hoverTimer = new System.Windows.Forms.Timer { Interval = 220 };
            hoverTimer.Tick += delegate { hoverTimer.Stop(); PreviewOption(hoverNode); };
            componentTree.MouseMove += delegate(object s, MouseEventArgs e)
            {
                OptionNode node = componentTree.GetNodeAt(e.Location);
                if (node == hoverNode) return;
                hoverTimer.Stop(); SetHoveredOption(node);
                if (node != null && !busy) hoverTimer.Start();
            };
            componentTree.MouseLeave += delegate { hoverTimer.Stop(); SetHoveredOption(null); };
            componentPanel.Controls.Add(componentTree);
            filesPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 6, 0, 0), Visible = false };
            fileList = CreateList(28); fileList.OwnerDraw = true; fileList.DrawSubItem += DrawFileItem; fileList.View = View.Details; fileList.HeaderStyle = ColumnHeaderStyle.Nonclickable; fileList.Font = Palette.Small;
            fileList.Columns.Add("File", 400); fileList.Columns.Add("From option", 145); fileList.Columns.Add("Size", 66);
            fileList.VirtualMode = true; fileList.RetrieveVirtualItem += RetrieveFileRow;
            // The native header sizes itself from the list's HFONT; give it the
            // device-DPI font so the column captions are not clipped at 300%.
            NativeDpiFont.Attach(fileList);
            Action sizeFiles=delegate { fileList.Columns[1].Width=Px(145);fileList.Columns[2].Width=Px(66);fileList.Columns[0].Width = Math.Max(Px(150), fileList.ClientSize.Width - Px(211)); };
            fileList.Resize += delegate { sizeFiles(); }; dpiLayouts.Add(sizeFiles);
            filesPanel.Controls.Add(fileList);
            overridesOnly = new CheckBox { Text = "Overrides only", Dock = DockStyle.Top, Height = 28, ForeColor = Palette.Muted, Font = Palette.Small };
            overridesOnly.CheckedChanged += delegate { FillFiles(); };
            filesPanel.Controls.Add(overridesOnly);
            filesPanel.VisibleChanged += delegate { if (filesPanel.Visible) FillFiles(); };
            choices.Controls.Add(componentPanel); choices.Controls.Add(filesPanel); choices.Controls.Add(choiceTabs);
            // 2.3.0: the Filters panel takes the options column while open (between the list and the inspector).
            Panel centre = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0), Padding = new Padding(0) };
            filterPanel.Padding = new Padding(0, 0, 0, 9);
            centre.Controls.Add(choices); centre.Controls.Add(filterPanel); filterPanel.BringToFront();
            choicesHost = choices;
            body.Controls.Add(centre, 0, 0);

            Panel inspector = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0), BackColor = Palette.Surface, Padding = new Padding(8, 0, 0, 9) };
            // 1.0.6 (the user): no destination line under the fields; Add to outfit… shares the Install all variants row.
            TableLayoutPanel target = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 56, ColumnCount = 2, RowCount = 2, Padding = new Padding(0, 6, 0, 0) };
            target.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 36)); target.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 64));
            target.RowStyles.Add(new RowStyle(SizeType.Absolute, 20)); target.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            Label characterCaption = Caption("CHARACTER"); characterCaption.Dock = DockStyle.Fill; characterCaption.Margin=Padding.Empty;
            targetCharacter = Combo(); targetCharacter.Dock = DockStyle.Fill; targetCharacter.Margin = new Padding(0, 0, 8, 0); targetCharacter.Items.AddRange(Characters.Names.Cast<object>().ToArray()); targetCharacter.SelectedIndexChanged += delegate { UpdateDestination(); ApplySuggestedName(); };
            Label outfitCaption = Caption("OUTFIT NAME"); outfitCaption.Dock = DockStyle.Fill; outfitCaption.Margin=Padding.Empty;
            outfitName = TextField(); outfitName.Dock = DockStyle.Fill; outfitName.Margin = new Padding(0); outfitName.AccessibleName="Outfit name"; outfitName.TextChanged += delegate { OutfitNameEdited(); UpdateDestination(); UpdateInstallButton(); };   // 2.5.0: a typed name is kept (MainFormSlots.cs)
            targetPath = new Label { UseMnemonic=false, Dock = DockStyle.Fill, ForeColor = Palette.Muted, Font = Palette.Tiny, AutoEllipsis = true, Padding = new Padding(0, 5, 0, 0) };
            target.Controls.Add(characterCaption, 0, 0); target.Controls.Add(outfitCaption, 1, 0); target.Controls.Add(targetCharacter, 0, 1); target.Controls.Add(outfitName, 1, 1);   // targetPath keeps its text but is not shown (1.0.6)
            Panel info = new Panel { Dock = DockStyle.Fill, AutoScroll = false, Padding = new Padding(0) };
            Panel visual = new Panel { Dock = DockStyle.Top, Height = 154, BackColor = Palette.Background };
            preview = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Palette.Background };
            noPreview = new Label { UseMnemonic=false, Text = "No preview", TextAlign = ContentAlignment.MiddleCenter, Dock = DockStyle.Fill, ForeColor = Palette.Border, Font = new Font("Segoe UI", 16F, FontStyle.Bold) };
            preview.Cursor = Cursors.Hand; preview.Click += delegate { EnlargePreview(); };  visual.Controls.Add(preview); visual.Controls.Add(noPreview);
            componentTitle = new Label { UseMnemonic=false, Text = "OPTION PREVIEW", Dock = DockStyle.Top, Height = 30, Font = Palette.Strong, AutoEllipsis = true, Padding = new Padding(0, 7, 0, 0) };
            // 1.0.5 (the user): the modinfo.ini version, right-aligned on the title row (SetVersion).
            componentVersion = new Label { UseMnemonic=false, Text = "", ForeColor = Palette.Muted, Font = Palette.Small, AutoEllipsis = true, TextAlign = ContentAlignment.TopRight, Anchor = AnchorStyles.Top | AnchorStyles.Right, Visible = false, AccessibleName = "Mod version" };
            componentTitle.Controls.Add(componentVersion);
            componentTitle.Resize += delegate { PlaceVersion(); };
            // 2.5.9: the byline ("by Author") under the option name; hidden when the mod names no author.
            packageMeta = new Label { UseMnemonic=false, Text = "", Dock = DockStyle.Top, Height = 24, ForeColor = Palette.Muted, Font = Palette.Small, AutoEllipsis = true, Visible = false, AccessibleName = "Mod author" };
            packageDescription = new Label { UseMnemonic=false, Text = "Hover an option to preview it.", Dock = DockStyle.Top, Height = 24, ForeColor = Palette.Text, Font = Palette.Small, AutoEllipsis = true };
            packageHint = new Label { UseMnemonic=false, Text = "Main files first. Later selected options take precedence.", Dock = DockStyle.Top, Height = 50, ForeColor = Palette.Muted, Font = Palette.Small, AutoEllipsis = true, Padding = new Padding(0, 6, 0, 0) };
            info.Controls.Add(packageHint); info.Controls.Add(packageDescription); info.Controls.Add(packageMeta); info.Controls.Add(componentTitle); info.Controls.Add(visual);
            inspector.Controls.Add(info); inspector.Controls.Add(target); inspector.Controls.Add(BuildAllVariantsRow()); inspector.Controls.Add(BuildReplacesPanel()); inspector.Controls.Add(BuildModePanel()); info.BringToFront();   // 2.5.4: Install all variants between the name and INSTALL AS
            targetPanel = target;
            // 1.0.8 (the user): the inspector scrolls when its details don't fit (OverlayScroll.cs): the option description
            // and the REPLACES notes are shown in full, the preview keeps its minimum, and a thin thumb shows while scrolling.
            inspectorScroller = new OverlayScroller { Dock = DockStyle.Fill, Margin = new Padding(0), AccessibleName = "Mod details" };
            inspectorScroller.SetContent(inspector, InspectorNaturalHeight);
            inspectorScroller.Expand = ExpandInspectorDetails;   // compact until the user scrolls down over it
            inspectorScroller.Resize += delegate { if (!inspectorDetailsOpen) SizeReplacesPanel(); };   // compact notes fit the column's new height
            body.Controls.Add(inspectorScroller, 2, 0);
            detail.Controls.Add(body); body.BringToFront();
            Action sizeInspector = delegate
            {
                // The preview takes what the text leaves, down to its minimum; below that the column scrolls.
                packageHint.Visible = false; packageMeta.Visible = packageMeta.Text.Length > 0;
                FitDescription();
                visual.Height = Math.Max(Px(InspectorPreviewMin), info.ClientSize.Height - InspectorTextHeight());
            };
            layoutInspectorInfo = sizeInspector;
            Action sizeBody=delegate { body.ColumnStyles[2].Width = detail.Width >= Px(1050) ? Px(460) : detail.Width >= Px(900) ? Px(390) : Px(340); sizeInspector(); };
            body.Resize += delegate { sizeBody(); }; dpiLayouts.Add(sizeBody);
            info.Resize += delegate { sizeInspector(); };
            packageDescription.TextChanged += delegate { sizeInspector(); };
            foreach (Control part in new Control[] { modePanel, replacesPanel, allVariantsRow, target, componentTitle, packageMeta, packageDescription })
            { part.SizeChanged += delegate { inspectorScroller.Relayout(); }; part.VisibleChanged += delegate { inspectorScroller.Relayout(); }; }
            replacesPanel.SizeChanged += delegate { if (replacesPanel.Width != replacesWidth) { replacesWidth = replacesPanel.Width; SizeReplacesPanel(); } };   // notes re-wrap at a new width
            ShowDetailTab(false);
            UpdateFooter();
            return detail;
        }

        private Panel BuildInstalledPage()
        {
            Panel page = new Panel { Dock = DockStyle.Fill };
            // One compact toolbar replaces the former page title, subtitle and
            // separate filter row; the top tab already names the page.
            Panel heading = new Panel { Dock = DockStyle.Top, Height = 40 };
            Panel presetAction=new Panel {Dock=DockStyle.Right,Width=42,Padding=new Padding(0,0,0,10)};
            presetsButton=Button("",false,42,delegate {if(!busy)ShowPresetsWindow();});presetsButton.Dock=DockStyle.Fill;presetsButton.Margin=Padding.Empty;   // 1.0.1: icon, hover "Presets"
            presetsButton.Glyph=ButtonGlyph.Presets;presetsButton.AccessibleName="Presets";IconHint(presetsButton,"Presets");
            presetAction.Controls.Add(presetsButton);
            FlowLayoutPanel filters = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = Padding.Empty, Margin = Padding.Empty };
            installedSearch = TextField(); installedSearch.Width = 260; installedSearch.AccessibleName="Search installed"; installedSearch.PlaceholderText="Search installed"; installedSearch.Margin=new Padding(0,0,8,0); installedSearchClear = SearchClear.Attach(installedSearch, FillInstalled);
            installedState = Combo(); installedState.Width = 150; installedState.Margin=Padding.Empty; installedState.Items.AddRange(new object[] { "All outfits", "Enabled", "Disabled", "Managed", "Manual" }); installedState.SelectedIndex = 0;
            installedKind = new SegmentedChoice(InstalledKinds) { Width = 240, Height = 30, Margin = new Padding(8, 0, 0, 0), Name = "installedKind", AccessibleName = "Installed type filter" };   // 1.0.1: was a type dropdown
            filters.Controls.Add(installedSearch); filters.Controls.Add(BuildInstalledCharacterPicker()); dpiLayouts.Add(delegate { installedCharacterButton.Height = installedState.Height; }); filters.Controls.Add(installedState); filters.Controls.Add(installedKind);
            heading.Controls.Add(filters); heading.Controls.Add(presetAction); filters.BringToFront(); AttachHistoryButton(heading); page.Controls.Add(heading); page.Controls.Add(BuildGameBanner());   // 2.4.1: History (MainFormHistory.cs)
            installedSearch.TextChanged += delegate { FillInstalled(); }; installedState.SelectedIndexChanged += delegate { if (!fillingInstalledFilters) FillInstalled(); }; installedKind.SelectionChanged += delegate { if (!fillingInstalledFilters) FillInstalled(); };
            SplitContainer split = new SplitContainer { Size = new Size(1180, 680), Dock = DockStyle.Fill, SplitterDistance = 650, SplitterWidth = 18, Panel1MinSize = 460, Panel2MinSize = 290 };
            installedSplit=split;
            installedList = CreateList(62); installedList.MultiSelect=true; installedList.Columns.Add("Outfit", 620); installedList.DrawSubItem += DrawInstalledItem;
            Action sizeInstalled=delegate { ((BufferedModList)installedList).FitSingleColumn(true); };
            installedList.Resize += delegate { sizeInstalled(); }; dpiLayouts.Add(sizeInstalled);
            installedList.SelectedIndexChanged += delegate {if(!fillingInstalled)ShowInstalledSelection();};
            installedList.KeyDown+=delegate(object sender,KeyEventArgs e) {if(e.Control && e.KeyCode==Keys.A && !busy) {SelectInstalledRows(true);e.Handled=true;e.SuppressKeyPress=true;}};
            split.Panel1.Controls.Add(installedList); WireGroupRows();   // 2.5.0 (MainFormSlots.cs)
            Panel selectionRow=new Panel {Dock=DockStyle.Top,Height=34};
            installedSelectionCount=new Label {UseMnemonic=false,Dock=DockStyle.Fill,Text="0 selected",TextAlign=ContentAlignment.MiddleLeft,Font=Palette.Small,ForeColor=Palette.Muted,AutoEllipsis=true};
            FlowLayoutPanel selectionActions=new FlowLayoutPanel {Dock=DockStyle.Right,Width=172,WrapContents=false,Padding=Padding.Empty,Margin=Padding.Empty};
            selectAllInstalledButton=Button("Select all",false,100,delegate {SelectInstalledRows(true);});
            clearInstalledSelectionButton=Button("Clear",false,62,delegate {ClearInstalledFilters();SelectInstalledRows(false);});   // 2.5.4: also the three filters
            foreach(var action in new[]{selectAllInstalledButton,clearInstalledSelectionButton}) {action.Height=28;action.Font=Palette.Small;action.Margin=new Padding(5,0,0,0);selectionActions.Controls.Add(action);}
            selectionRow.Controls.Add(installedSelectionCount);selectionRow.Controls.Add(BuildInstalledSortStrip());selectionRow.Controls.Add(new Panel {Dock=DockStyle.Right,Width=10});selectionRow.Controls.Add(selectionActions);split.Panel1.Controls.Add(selectionRow);
            // Detail hierarchy: wrapped title, chips, compact copyable path,
            // aligned actions, then the loader result/help card.
            Panel info = new Panel { Dock = DockStyle.Fill, BackColor = Palette.Surface, Padding = new Padding(20, 18, 20, 12) };
            installedTitle = new ClampedLabel { MaxLines = 2, Text = "Choose outfits", Font = new Font("Segoe UI", 14F, FontStyle.Bold), Dock = DockStyle.Top, Height = 34 };
            installedChips = new ChipStrip { Dock = DockStyle.Top, Height = 32, Padding = new Padding(0, 6, 0, 0) };
            // The "N shared files" chip opens the shared-files details in the card below.
            installedChips.Click += delegate { ShowSharedFilesDetails(); };
            installedPathRow = new Panel { Dock = DockStyle.Top, Height = 38, Padding = new Padding(0, 8, 0, 0) };
            installedDetail = new ClampedLabel { MaxLines = 1, UsePathEllipsis = true, Dock = DockStyle.Fill, ForeColor = Palette.Muted, Font = Palette.Small };
            copyPathButton = Button("Copy path", false, 92, delegate { CopyInstalledPath(); }); copyPathButton.Dock = DockStyle.Right; copyPathButton.Font = Palette.Small; copyPathButton.Margin = Padding.Empty;
            installedPathRow.Controls.Add(installedDetail); installedPathRow.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 8 }); installedPathRow.Controls.Add(copyPathButton);
            installedActions = new Panel { Dock = DockStyle.Top, Height = 92, Padding = new Padding(0, 12, 0, 0) };
            openInstalledButton = Button("Open folder", false, 200, delegate { GameModRow row = GetGameRowSelection(); if (row != null) { OpenPath(row.Fluffy != null ? row.Fluffy.Root : config.GameRoot); return; } InstalledOutfit outfit = GetInstalledSelection(); OutfitGroup group = GetGroupSelection(); if (group != null) outfit = group.Slots[0]; if (outfit != null) OpenPath(File.Exists(outfit.Folder) ? Path.GetDirectoryName(outfit.Folder) : outfit.Folder); });
            editInstalledButton = Button("Edit options", false, 200, delegate { EditOutfit(); });
            toggleInstalledButton = Button("Disable outfit", false, 200, delegate { ToggleOutfit(); });
            removeInstalledButton = Button("Uninstall outfit", false, 200, delegate { RemoveOutfit(); }); removeInstalledButton.Danger = true;
            removeInstalledButton.KeyHint = "Ctrl";   // 2.5.3: a lone Ctrl tap, as in the Mod library (MainFormKeys.cs)
            moveUpButton = Button("Move up", false, 200, delegate { MoveGameRow(-1); }); moveDownButton = Button("Move down", false, 200, delegate { MoveGameRow(1); });
            reapplyButton = Button("Reapply all", false, 200, delegate { ReapplyAll(); });
            reinstallButton = Button("Reinstall", false, 200, delegate { ReinstallSelected(); }); reinstallButton.AccessibleName = "Reinstall from the changed source"; reinstallButton.Visible = false;   // 2.4.1
            
            openInstalledButton.Enabled = false; editInstalledButton.Enabled = false; toggleInstalledButton.Enabled = false; removeInstalledButton.Enabled = false;
            foreach (FlatButton button in new[] { reinstallButton, openInstalledButton, editInstalledButton, toggleInstalledButton, removeInstalledButton, moveUpButton, moveDownButton, reapplyButton }) { button.Margin = Padding.Empty; installedActions.Controls.Add(button); }
            installedActions.Resize += delegate { LayoutInstalledActions(); }; dpiLayouts.Add(LayoutInstalledActions);
            installedSectionCaption = Caption("ABOUT"); installedSectionCaption.Dock = DockStyle.Top; installedSectionCaption.Height = 34; installedSectionCaption.Padding = new Padding(0, 10, 0, 0); installedSectionCaption.AutoEllipsis = true;
            Panel helpCard = new Panel { Dock = DockStyle.Fill, BackColor = Palette.Background, Padding = new Padding(12, 10, 6, 10) };
            installedHelp = new TextBox { Dock = DockStyle.Fill, AutoSize = false, Multiline = true, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = Palette.Background, ForeColor = Palette.Muted, ScrollBars = ScrollBars.Vertical, Font = Palette.Small, Text = "Outfits the Manager installed can be disabled or uninstalled. Others are listed for reference." };
            NativeDpiFont.Attach(installedHelp); WireHelpSelection();
            helpCard.Controls.Add(installedHelp);
            refreshHint = new Label { UseMnemonic=false, Text = "Restart SF6 to see changes.", ForeColor = Palette.Accent, Dock = DockStyle.Bottom, Height = 30, Font = Palette.Small, TextAlign = ContentAlignment.BottomLeft };
            // Docking runs from the highest index: the title is added last of the top group.
            info.Controls.Add(helpCard); info.Controls.Add(installedSectionCaption); info.Controls.Add(BuildVariantsPanel()); info.Controls.Add(installedActions); info.Controls.Add(installedPathRow); info.Controls.Add(installedChips); info.Controls.Add(installedTitle); info.Controls.Add(refreshHint);
            helpCard.BringToFront();
            split.Panel2.Controls.Add(info); page.Controls.Add(split); split.BringToFront();
            return page;
        }

        private Panel BuildSettingsPage()
        {
            Panel page = new Panel { Dock = DockStyle.Fill };
            Panel content = new Panel { Dock = DockStyle.Fill, BackColor = Palette.Surface, Padding = new Padding(20, 14, 20, 16) };
            // The gear tab already names the page; one short muted line on the General tab only.
            Label subtitle = new Label { UseMnemonic=false, Text = "Uses your mod libraries; settings and records stay separate.", Dock = DockStyle.Top, Height = 26, ForeColor = Palette.Muted, Font = Palette.Small, AutoEllipsis = true };
            Panel fields = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 8, 0, 0) };
            TableLayoutPanel layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 13, Margin = new Padding(0) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 118));
            // Rhythm: caption, field, its note (Related gap above the text), then a group gap before the next caption.
            // Mod libraries explain themselves under the caption instead (1.0.11; -1 = that line's row, sized by it).
            int[] heights = { 24, 32, 20 + Rhythm.Related + 4, Rhythm.Group - 6, 24, 32, 20 + Rhythm.Related + 4, Rhythm.Group - 6, 24, -1, 0, Rhythm.AfterAction, 26 };
            foreach (int h in heights) layout.RowStyles.Add(h < 0 ? new RowStyle(SizeType.AutoSize) : h == 0 ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.Absolute, h));
            Label gameCaption = Caption("Game folder"); gameCaption.Dock = DockStyle.Fill; layout.Controls.Add(gameCaption, 0, 0); layout.SetColumnSpan(gameCaption, 2);
            settingsGame = TextField(); settingsGame.AccessibleName="Game folder"; settingsGame.Dock = DockStyle.Fill; settingsGame.Margin = new Padding(0, 0, 10, 0); layout.Controls.Add(settingsGame, 0, 1);
            FlatButton browseGame=Button("Browse…", false, 108, delegate { ChooseSettingFolder(settingsGame); }); browseGame.Dock=DockStyle.Fill; browseGame.Margin=Padding.Empty; layout.Controls.Add(browseGame, 1, 1);
            Label dest = new Label { UseMnemonic=false, Name = "settingsGameNote", Text = "", ForeColor = Palette.Muted, Font = Palette.Small, Dock = DockStyle.Fill, Padding = new Padding(0, Rhythm.Related, 0, 0), Margin = Padding.Empty, AutoEllipsis = true }; layout.Controls.Add(dest, 0, 2); layout.SetColumnSpan(dest, 2);
            Label sevenCaption = Caption("7-Zip (RAR and 7Z)"); sevenCaption.Dock = DockStyle.Fill; layout.Controls.Add(sevenCaption, 0, 4); layout.SetColumnSpan(sevenCaption, 2);
            settingsSevenZip = TextField(); settingsSevenZip.AccessibleName="7-Zip executable"; settingsSevenZip.PlaceholderText = "Automatic"; settingsSevenZip.Dock = DockStyle.Fill; settingsSevenZip.Margin = new Padding(0, 0, 10, 0); layout.Controls.Add(settingsSevenZip, 0, 5);
            FlatButton browseSevenZip=Button("Browse…", false, 108, delegate { using (OpenFileDialog d = new OpenFileDialog { Filter = "7-Zip executable|7z.exe|Executable|*.exe", FileName = settingsSevenZip.Text }) { if (d.ShowDialog(this) == DialogResult.OK) settingsSevenZip.Text = d.FileName; } }); browseSevenZip.Dock=DockStyle.Fill; browseSevenZip.Margin=Padding.Empty; layout.Controls.Add(browseSevenZip, 1, 5);
            Label sevenHint = new Label { UseMnemonic=false, Name = "settingsSevenZipNote", Text = "ZIP and folders work without 7-Zip.", ForeColor = Palette.Muted, Font = Palette.Small, Dock = DockStyle.Fill, Padding = new Padding(0, Rhythm.Related, 0, 0), Margin = Padding.Empty, AutoEllipsis = true }; layout.Controls.Add(sevenHint, 0, 6); layout.SetColumnSpan(sevenHint, 2);
            settingsGame.TextChanged += delegate { dest.Text = Directory.Exists(settingsGame.Text) ? "Found · extra outfits install to reframework\\costume_mods" : "Folder not found"; dest.ForeColor = Directory.Exists(settingsGame.Text) ? Palette.Green : Palette.Red; };
            // 1.0.11: empty = automatic (installed 7-Zip, else the copy beside the Manager); the note says which one runs.
            showSevenZipNote = delegate { string typed = settingsSevenZip.Text.Trim(); sevenHint.Text = typed.Length > 0 && !File.Exists(typed) ? "Not found at that path · clear the field to find 7-Zip automatically" : SevenZipTool.Describe(typed); };
            settingsSevenZip.TextChanged += delegate { showSevenZipNote(); };
            Label libCaption = Caption("Mod libraries"); libCaption.Dock = DockStyle.Fill; layout.Controls.Add(libCaption, 0, 8); layout.SetColumnSpan(libCaption, 2);
            // The line gives way on short windows, so the list keeps at least two rows (100 px).
            int hintHeight = 20 + Rhythm.Related, roomy = heights.Where(h => h > 0).Sum() + hintHeight + 104;
            Label libHint = new Label { UseMnemonic=false, Name = "settingsLibrariesNote", Text = "Folders where you keep downloaded mods. Everything in them shows in the Mod library tab.", ForeColor = Palette.Muted, Font = Palette.Small, AutoSize = false, Height = hintHeight, Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top, Margin = Padding.Empty, AutoEllipsis = true }; layout.Controls.Add(libHint, 0, 9); layout.SetColumnSpan(libHint, 2);
            layout.Resize += delegate { libHint.Visible = layout.Height >= Px(roomy); };
            // Padded, owner-drawn library rows with a found/missing state and
            // the mod count from the last scan, framed like the other fields.
            Panel librariesFrame = new Panel { Dock = DockStyle.Fill, BackColor = Palette.Border, Padding = new Padding(1), Margin = new Padding(0, 0, 10, 0) };
            settingsLibraries = new ListBox { Dock = DockStyle.Fill, BackColor = Palette.Background, ForeColor = Palette.Text, BorderStyle = BorderStyle.None, Font = Palette.Normal, IntegralHeight = false, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 50, AccessibleName = "Mod libraries" };
            NativeDpiFont.Attach(settingsLibraries);
            settingsLibraries.DrawItem += DrawLibraryFolder;
            settingsLibraries.HandleCreated += delegate { if (settingsLibraries.ItemHeight != Px(50)) settingsLibraries.ItemHeight = Px(50); };
            settingsLibraries.SelectedIndexChanged += delegate { if (removeLibraryButton != null) removeLibraryButton.Enabled = settingsLibraries.SelectedIndex >= 0; };
            librariesFrame.Controls.Add(settingsLibraries);
            layout.Controls.Add(librariesFrame, 0, 10);
            Panel libActions = new Panel { Dock = DockStyle.Fill, Margin=Padding.Empty };
            FlatButton addLibrary = Button("Add…", false, 118, delegate { using (FolderBrowserDialog d = new FolderBrowserDialog { Description = "Choose a library folder, such as your mod manager's Mods folder" }) { if (d.ShowDialog(this) == DialogResult.OK && !settingsLibraries.Items.Cast<string>().Contains(d.SelectedPath, StringComparer.OrdinalIgnoreCase)) { libraryFolderStatus.Remove(d.SelectedPath); settingsLibraries.Items.Add(d.SelectedPath); settingsLibraries.SelectedIndex = settingsLibraries.Items.Count - 1; } } });
            removeLibraryButton = Button("Remove", false, 118, delegate { if (settingsLibraries.SelectedIndex >= 0) { int index = settingsLibraries.SelectedIndex; settingsLibraries.Items.RemoveAt(index); if (settingsLibraries.Items.Count > 0) settingsLibraries.SelectedIndex = Math.Min(index, settingsLibraries.Items.Count - 1); } removeLibraryButton.Enabled = settingsLibraries.SelectedIndex >= 0; });
            removeLibraryButton.Enabled = false; addLibraryButton = addLibrary;

            addLibrary.Dock = DockStyle.Top; removeLibraryButton.Dock = DockStyle.Top;
            addLibrary.Height = 32; removeLibraryButton.Height = 32;
            libActions.Controls.Add(removeLibraryButton); libActions.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 8 }); libActions.Controls.Add(addLibrary);
            layout.Controls.Add(libActions, 1, 10);
            settingsStatus = new Label { UseMnemonic=false, ForeColor = Palette.Muted, Font = Palette.Small, Dock = DockStyle.Fill, AutoEllipsis = true }; layout.Controls.Add(settingsStatus, 0, 12); layout.SetColumnSpan(settingsStatus, 2);
            FlowLayoutPanel actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 56, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(0, 8, 0, 0) };
            actions.Controls.Add(Button("Save settings", true, 154, delegate { SaveSettings(); }));
            actions.Controls.Add(Button("Open Data folder", false, 168, delegate { OpenPath(AppConfig.DataDir); }));
            actions.Controls.Add(Button("Show welcome", false, 150, delegate { WelcomeForm.ShowWelcome(this, ShowHelp); }));   // 1.0.11 (the user): here, not in the gear menu
            fields.Controls.Add(actions); fields.Controls.Add(layout);
            layout.BringToFront();
            Panel settingsGap = new Panel { Dock = DockStyle.Right, Width = 24 }; Panel claudeCard = BuildClaudeSettings();
            content.Controls.Add(fields); content.Controls.Add(settingsGap); content.Controls.Add(claudeCard); content.Controls.Add(BuildSettingsTabs(subtitle, new Control[] { fields, settingsGap, claudeCard }, content)); page.Controls.Add(content);
            return page;
        }

        // ---- Assistant -------------------------------------------------------------
        private AssistantPage BuildAssistantPage()
        {
            var host = new AssistantPageHost
            {
                Config = config,
                Library = delegate { return library.Count > 0 ? library : null; },
                OutfitsChanged = delegate { if (!busy && !IsDisposed) RefreshInstalled(); },
                Status = delegate(string text, Color color) { SetPageStatus(assistantPage, text, color); },
                HostBusy = delegate { return busy; },
                BeginWork = BeginExternalWork,
                ReportProgress = ReportExternalProgress,
                EndWork = EndExternalWork,
                OpenSettings = delegate { RunShortcut(ShortcutAction.SettingsPage); },
                Saved = assistantSettings,
                KeyStore = assistantKeys
            };
            return new AssistantPage(host);
        }

        // 2.4.1: the page is built on its first show. It was not there when the form scaled its pages in the
        // constructor, so it gets the window's DPI scale and the dark theme here.
        private AssistantPage EnsureAssistantPage()
        {
            if (assistantPage != null) return assistantPage;
            AssistantPage page = BuildAssistantPage();
            page.Visible = false;
            if (dpiLayoutReady && DeviceDpi != 96) page.Scale(new SizeF(DeviceDpi / 96F, DeviceDpi / 96F));
            DarkTheme.Apply(page);
            pagesPanel.Controls.Add(page);
            assistantPage = page;
            return page;
        }

        private void ShowAssistant()
        {
            if (!assistantTabShown) return;   // 2.4.1: the tab is off in Settings: no page, no requests
            AssistantPage page = EnsureAssistantPage();
            ShowPage(page);
            page.UpdateEmptyState();
            if (!Program.Headless && !page.Running) page.Prompt.Editor.Focus();
        }

        // 2.4.1: Settings → General → "Show Assistant tab" takes effect at once. While it is off the saved key and
        // the Assistant's settings stay stored; turning it on brings the tab and the whole card back.
        private void SetAssistantTabShown(bool show, bool save)
        {
            if (show == assistantTabShown) return;
            assistantTabShown = show; assistantSettings.ShowTab = show;
            if (save)
            {
                try { AssistantSettings.SaveShowTab(AppConfig.DataDir, show); }
                catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); SetPageStatus(settingsPage, "Couldn't save the Assistant tab option: " + ex.Message, Palette.Red); }
            }
            ApplyAssistantTab();
            if (show) ShowKeyStatus();
        }

        // The header tab, the Settings card rows below the option, and leaving the Assistant when it is turned off.
        private void ApplyAssistantTab()
        {
            navAssistant.Visible = assistantTabShown;
            foreach (Control row in claudeDetails) row.Visible = assistantTabShown;
            // A row whose window is created only now is re-indexed to its native z-order, which reverses the
            // docking: put the rows back in their order (each added row goes to the front, as when built).
            if (claudeRows.Count > 0)
            {
                Control card = claudeRows[0].Parent; card.SuspendLayout();
                try { foreach (Control row in claudeRows) row.BringToFront(); } finally { card.ResumeLayout(true); }
            }
            if (settingsShowAssistant != null && settingsShowAssistant.Checked != assistantTabShown)
            {
                bool loading = loadingClaudeSettings; loadingClaudeSettings = true;
                try { settingsShowAssistant.Checked = assistantTabShown; } finally { loadingClaudeSettings = loading; }
            }
            if (assistantTabShown) return;
            if (assistantPage != null && assistantPage.Running) assistantPage.Stop();
            if (assistantPage != null && currentPage == assistantPage) ShowPage(libraryPage);
            if (lastMainPage == "Assistant") { lastMainPage = "Library"; QueueViewSave(); }
        }

        // Screenshots of the Assistant and of its key show the tab for that run only (nothing is saved).
        private void ShowAssistantTabForScreenshot() { if (!assistantTabShown) { assistantTabShown = true; ApplyAssistantTab(); } }

        // Work started outside Work<T> (approved Assistant actions) still locks the
        // window and uses the status strip's progress and Cancel.
        internal bool BeginExternalWork(string message, CancellationTokenSource cancellation)
        {
            if (busy) return false;
            activeOperation = cancellation; SetBusy(true, message);
            return true;
        }
        internal void ReportExternalProgress(OperationProgress progress)
        {
            if (IsDisposed || activeOperation == null) return;
            cancelWork.Enabled = progress.CanCancel && !activeOperation.IsCancellationRequested;
            statusProgress.Style = progress.Total > 0 ? ProgressBarStyle.Continuous : ProgressBarStyle.Marquee;
            int percent = progress.Total > 0 ? (int)Math.Max(0, Math.Min(100, progress.Completed * 100.0 / progress.Total)) : 0;
            if (progress.Total > 0) statusProgress.Value = percent;
            statusText.Text = activeOperation.IsCancellationRequested && progress.CanCancel ? "Cancelling… finishing the current safe step." : progress.Stage + (progress.Total > 0 ? " · " + percent + "%" : "…");
        }
        internal void EndExternalWork(CancellationTokenSource cancellation)
        {
            if (IsDisposed) return;
            if (activeOperation == cancellation) activeOperation = null;
            if (busy) SetBusy(false, "Ready");
        }

        private Panel BuildClaudeSettings()
        {
            var card = new Panel { Dock = DockStyle.Right, Width = 430, Padding = new Padding(0) };
            // 2.4.1: "Show Assistant tab" and its note head the card; every row after them is a detail that
            // collapses (takes no space) while the tab is off.
            bool detail = false;
            Action<Control> add = delegate(Control row) { row.Dock = DockStyle.Top; card.Controls.Add(row); row.BringToFront(); claudeRows.Add(row); if (detail) claudeDetails.Add(row); };
            add(Caption("Assistant"));
            settingsShowAssistant = new CheckBox { Text = "Show Assistant tab", Height = 28, ForeColor = Palette.Text, Font = Palette.Small, Padding = new Padding(2, 0, 0, 0), Checked = assistantTabShown, AccessibleName = "Show the Assistant tab in the header" };
            settingsShowAssistant.CheckedChanged += delegate { if (!loadingClaudeSettings) SetAssistantTabShown(settingsShowAssistant.Checked, true); };
            add(settingsShowAssistant);
            add(new NoteLabel { Text = "The Assistant finds, checks and installs mods from your libraries. It runs on Claude by Anthropic with your own API key (api.anthropic.com).", ForeColor = Palette.Muted, Font = Palette.Small, Height = 40, Padding = new Padding(0, Rhythm.Related, 0, 0) });
            detail = true;
            add(Rhythm.Gap(Rhythm.Group - 8));
            Label keyCaption = Caption("API key"); add(keyCaption);
            settingsApiKey = TextField(); settingsApiKey.AccessibleName = "Anthropic API key"; settingsApiKey.Height = 32; settingsApiKey.Editor.UseSystemPasswordChar = true; settingsApiKey.PlaceholderText = "Paste a key";
            settingsApiKey.Editor.ShortcutsEnabled = true;
            add(settingsApiKey);
            var keyActions = new FlowLayoutPanel { Height = 32 + 8, WrapContents = false, Padding = new Padding(0, 8, 0, 0) };
            saveKeyButton = Button("Save key", false, 110, delegate { SaveApiKey(); });
            removeKeyButton = Button("Remove", false, 96, delegate { RemoveApiKey(); });
            testKeyButton = Button("Test key", false, 110, delegate { TestApiKey(); });
            removeKeyButton.AccessibleName = "Remove the saved key"; testKeyButton.AccessibleName = "Test the key (one small request)";
            foreach (FlatButton b in new[] { saveKeyButton, removeKeyButton, testKeyButton }) { b.Height = 32; b.Font = Palette.Small; b.Margin = new Padding(0, 0, 8, 0); keyActions.Controls.Add(b); }
            add(keyActions);
            add(Rhythm.Gap(Rhythm.AfterAction - 4));
            settingsKeyStatus = new Label { UseMnemonic = false, Height = 22, Font = Palette.Small, ForeColor = Palette.Muted, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
            add(settingsKeyStatus);
            add(Rhythm.Gap(Rhythm.Group - 8));
            var choices = new TableLayoutPanel { Height = 56, ColumnCount = 2, RowCount = 2, Margin = Padding.Empty, Padding = Padding.Empty };
            choices.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60)); choices.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
            choices.RowStyles.Add(new RowStyle(SizeType.Absolute, 24)); choices.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            Label modelCaption = Caption("Default model"); modelCaption.Dock = DockStyle.Fill; choices.Controls.Add(modelCaption, 0, 0);
            Label effortCaption = Caption("Default effort"); effortCaption.Dock = DockStyle.Fill; choices.Controls.Add(effortCaption, 1, 0);
            settingsModel = Combo(); settingsModel.Dock = DockStyle.Fill; settingsModel.Margin = new Padding(0, 0, 10, 0); settingsModel.AccessibleName = "Default Assistant model";
            foreach (AssistantModel m in AssistantModels.All) settingsModel.Items.Add(m.DisplayName + (m.Id == AssistantModels.DefaultModel ? " (default)" : ""));
            settingsEffort = Combo(); settingsEffort.Dock = DockStyle.Fill; settingsEffort.Margin = Padding.Empty; settingsEffort.AccessibleName = "Default effort";
            foreach (string label in AssistantModels.EffortLabels) settingsEffort.Items.Add(label);
            choices.Controls.Add(settingsModel, 0, 1); choices.Controls.Add(settingsEffort, 1, 1);
            add(choices);
            add(Rhythm.Gap(Rhythm.Related));
            settingsAutoApply = new CheckBox { Text = "Apply changes without asking (not recommended)", Height = 28, ForeColor = Palette.Text, Font = Palette.Small, Padding = new Padding(2, 0, 0, 0), AccessibleName = "Apply the Assistant's changes without asking (not recommended)" };
            add(settingsAutoApply);
            add(Rhythm.Gap(Rhythm.Group - 8));
            settingsClaudeStatus = new NoteLabel { Height = 60, ForeColor = Palette.Muted, Font = Palette.Tiny, Text = "Sent to Anthropic: your messages, mod and option names and descriptions, installed outfit names. Not sent: images, folder paths. Model and effort save as you change them." };
            add(settingsClaudeStatus);
            settingsModel.SelectedIndexChanged += delegate { SaveClaudePreferences(); };
            settingsEffort.SelectedIndexChanged += delegate { SaveClaudePreferences(); };
            settingsAutoApply.CheckedChanged += delegate { SaveClaudePreferences(); };
            return card;
        }

        private void LoadClaudeSettings()
        {
            loadingClaudeSettings = true;
            try
            {
                if (assistantPage != null) assistantPage.ReloadSavedSettings();
                else { AssistantSettings fresh = AssistantSettings.Load(AppConfig.DataDir); assistantSettings.Model = fresh.Model; assistantSettings.Effort = fresh.Effort; assistantSettings.AutoApply = fresh.AutoApply; }
                AssistantSettings saved = assistantSettings;
                settingsShowAssistant.Checked = assistantTabShown;
                settingsModel.SelectedIndex = Math.Max(0, AssistantModels.All.FindIndex(m => m.Id == saved.Model));
                settingsEffort.SelectedIndex = Math.Max(0, Array.IndexOf(AssistantModels.Efforts, saved.Effort));
                settingsAutoApply.Checked = saved.AutoApply;
                settingsApiKey.Clear();
                if (assistantTabShown) ShowKeyStatus();   // 2.4.1: the key is not read while the card is collapsed
            }
            finally { loadingClaudeSettings = false; }
        }

        private void ShowKeyStatus()
        {
            string status; try { status = assistantKeys.Status(); } catch (Exception ex) { status = "Key status unavailable: " + ex.Message; }
            settingsKeyStatus.Text = status;
            settingsKeyStatus.ForeColor = status.StartsWith("Saved") || status.StartsWith("Using") ? Palette.Green : status == "No key" ? Palette.Muted : Palette.Red;
            removeKeyButton.Enabled = assistantKeys.HasSavedKey;
        }

        private void SaveClaudePreferences()
        {
            if (loadingClaudeSettings || settingsModel.SelectedIndex < 0 || settingsEffort.SelectedIndex < 0) return;
            AssistantSettings saved = assistantSettings;
            saved.Model = AssistantModels.All[settingsModel.SelectedIndex].Id; saved.Effort = AssistantModels.Efforts[settingsEffort.SelectedIndex];
            saved.AutoApply = settingsAutoApply.Checked;
            try
            {
                saved.Save(AppConfig.DataDir);
                if (assistantPage != null) assistantPage.ReloadSavedSettings();
                settingsKeyStatus.Text = "Assistant settings saved."; settingsKeyStatus.ForeColor = Palette.Green;
            }
            catch (Exception ex) { Error(ex); }
        }

        private void SaveApiKey()
        {
            try
            {
                string key = settingsApiKey.Text.Trim();
                if (key.Length == 0) { settingsKeyStatus.Text = "Paste a key first."; settingsKeyStatus.ForeColor = Palette.Accent; return; }
                assistantKeys.Save(key);
                settingsApiKey.Clear(); key = null;
                ShowKeyStatus(); if (assistantPage != null) assistantPage.UpdateEmptyState();
            }
            catch (ArgumentException ex) { settingsKeyStatus.Text = ex.Message; settingsKeyStatus.ForeColor = Palette.Red; }
            catch (Exception ex) { Error(new IOException("Couldn't save the key: " + ex.Message)); }
        }

        private void RemoveApiKey()
        {
            if (!Program.Headless && ThemedMessageBox.Show(this, "Remove the saved API key from this PC?", "Remove API key", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            try { assistantKeys.Remove(); ShowKeyStatus(); if (assistantPage != null) assistantPage.UpdateEmptyState(); }
            catch (Exception ex) { Error(ex); }
        }

        private void TestApiKey()
        {
            if (!assistantTabShown) return;   // 2.4.1: no request while the Assistant is off
            string typed = settingsApiKey.Text.Trim(), source;
            string key = typed.Length > 0 ? typed : assistantKeys.Load(out source);
            if (String.IsNullOrEmpty(key)) { settingsKeyStatus.Text = "Paste or save a key first."; settingsKeyStatus.ForeColor = Palette.Accent; return; }
            string model = settingsModel.SelectedIndex >= 0 ? AssistantModels.All[settingsModel.SelectedIndex].Id : AssistantModels.DefaultModel;
            testKeyButton.Enabled = false; settingsKeyStatus.Text = "Testing " + AssistantModels.Find(model).DisplayName + "…"; settingsKeyStatus.ForeColor = Palette.Muted;
            Func<string, ClaudeClient> factory = EnsureAssistantPage().ClientFactory;   // the page's factory (diagnostics; tests point it at a mock)
            var worker = new BackgroundWorker();
            worker.DoWork += delegate(object s, DoWorkEventArgs e) { e.Result = AssistantConnection.Test(factory(key), model); };
            worker.RunWorkerCompleted += delegate(object s, RunWorkerCompletedEventArgs e)
            {
                key = null; worker.Dispose();
                if (IsDisposed) return;
                testKeyButton.Enabled = true;
                if (e.Error != null) { settingsKeyStatus.Text = e.Error.Message; settingsKeyStatus.ForeColor = Palette.Red; SetPageStatus(currentPage, e.Error.Message, Palette.Red); }
                else { settingsKeyStatus.Text = (string)e.Result; settingsKeyStatus.ForeColor = Palette.Green;  }
            };
            worker.RunWorkerAsync();
        }

        // 1.0.1: the gear menu and its Keyboard shortcuts submenu: 11 pt text, more room around each item.
        internal static readonly Font MenuFont = new Font("Segoe UI", 11F);
        private static void RoomyMenu(ToolStripDropDownMenu menu)
        {
            menu.Font = MenuFont; menu.Padding = new Padding(2, 6, 2, 6);
            foreach (ToolStripItem item in menu.Items)
            {
                item.Font = MenuFont;
                if (item is ToolStripSeparator) { item.Margin = new Padding(0, 3, 0, 3); continue; }
                item.Padding = new Padding(8, 7, 12, 7);
            }
        }

        private static FlatButton Button(string text, bool primary, int width, Action click)
        {
            FlatButton button = new FlatButton(text, primary) { Width = width, Margin = new Padding(0, 0, 7, 8) };
            button.Click += delegate { click(); };
            return button;
        }

        private FlatButton NavButton(string text, Action click)
        {
            FlatButton button = Button(text, false, 181, click); button.Height = 46; button.TextAlign = ContentAlignment.MiddleLeft; button.Padding = new Padding(14, 0, 0, 0);
            return button;
        }

        private static Label Caption(string text) { return new Label { UseMnemonic=false, Text = text, Font = new Font("Segoe UI", 8F, FontStyle.Bold), ForeColor = Palette.Muted, AutoSize = false, Height = 24, TextAlign = ContentAlignment.MiddleLeft }; }
        private static Label Heading(string text) { return new Label { UseMnemonic=false, Text = text, Font = new Font("Segoe UI", 22F, FontStyle.Bold), Height = 45, AutoSize = false }; }
        private static PaddedTextField TextField() { return new PaddedTextField(); }
        private static ComboBox Combo() { return new DarkCombo { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, BackColor = Palette.Raised, ForeColor = Palette.Text, Font = Palette.Small, IntegralHeight = false, DropDownHeight = 350 }; }

        private static ListView CreateList(int rowHeight)
        {
            ImageList images = new ImageList { ImageSize = new Size(1, rowHeight), ColorDepth = ColorDepth.Depth32Bit };
            images.Images.Add(new Bitmap(1, rowHeight));
            ListView list = new BufferedModList { LogicalRowHeight=rowHeight, Dock = DockStyle.Fill, BackColor = Palette.Surface, ForeColor = Palette.Text, BorderStyle = BorderStyle.None, View = View.Details, HeaderStyle = ColumnHeaderStyle.None, FullRowSelect = true, HideSelection = false, MultiSelect = false, OwnerDraw = true, ShowItemToolTips = false, SmallImageList = images };
            list.DrawColumnHeader += delegate(object s, DrawListViewColumnHeaderEventArgs e) { using (Brush bg = new SolidBrush(Palette.Raised)) e.Graphics.FillRectangle(bg, e.Bounds); TextRenderer.DrawText(e.Graphics, e.Header.Text, Palette.Small, e.Bounds, Palette.Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter); };
            list.DrawItem += delegate(object s, DrawListViewItemEventArgs e) { if (list.View != View.Details) e.DrawDefault = true; };
            list.Disposed += delegate { images.Dispose(); };
            return list;
        }

        private void DrawLibraryItem(object sender, DrawListViewSubItemEventArgs e)
        {
            LibraryEntry entry = e.Item.Tag as LibraryEntry; if (entry == null) return;
            bool selected = e.Item.Selected;
            LibraryInstallState installState = CachedInstallState(entry);
            using (Brush bg = new SolidBrush(selected ? Palette.Selected : Palette.Surface)) e.Graphics.FillRectangle(bg, e.Bounds);
            Rectangle r = e.Bounds;
            // Every row has a neutral rail. Only active app-managed installs
            // turn it green; selection is already visible in the row background.
            using (Brush chip = new SolidBrush(installState == LibraryInstallState.Full || installState == LibraryInstallState.Partial ? Palette.Green : Palette.Border)) e.Graphics.FillRectangle(chip, r.Left + Px(1), r.Top + Px(12), Px(3), r.Height - Px(24));
            TextRenderer.DrawText(e.Graphics, LibraryRowName(entry), Palette.Strong, new Rectangle(r.Left + Px(13), r.Top + Px(8), r.Width - Px(24), Px(24)), Palette.Text, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            int searchRank; string costume = LibraryRowLabel(entry, libraryRanks.TryGetValue(entry, out searchRank) ? searchRank : (int?)null);
            string subtitle = entry.Pending   // 1.0.6: not read yet (background indexing)
                ? (String.IsNullOrEmpty(entry.Character) ? "" : entry.Character + "   ·   ") + "Reading…   ·   " + entry.Kind.ToUpperInvariant() + (entry.Size > 0 ? "   ·   " + Utilities.FriendlyBytes(entry.Size) : "")
                : LibraryCharacterLabel(entry) + (costume.Length > 0 ? "   ·   " + costume : "") + "   ·   " + entry.Kind.ToUpperInvariant() + (entry.Size > 0 ? "   ·   " + Utilities.FriendlyBytes(entry.Size) : "");
            // 1.0.1: "Extra · " before the state when the mod is installed as an extra outfit (the user).
            bool extra = installState != LibraryInstallState.None && InstalledAsExtra(entry);   // 1.0.1: "Extra" alone when fully installed (the user: no "Full" next to it)
            string badge = installState == LibraryInstallState.None ? "" : extra && installState == LibraryInstallState.Full ? "Extra" : (extra ? "Extra · " : "") + (installState == LibraryInstallState.Full ? "✓ Full" : installState == LibraryInstallState.Partial ? "◐ Partial" : "Disabled");
            int badgeWidth = badge.Length == 0 ? 0 : Math.Max(Px(63), TextRenderer.MeasureText(e.Graphics, badge, Palette.Tiny, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix).Width + Px(8));
            subtitle = WithUsage(e.Graphics, subtitle, UsageMeta(entry.SourcePath, installState != LibraryInstallState.None), Palette.Tiny, r.Width - Px(24) - badgeWidth);   // 2.4.1 history, only when it fits
            TextRenderer.DrawText(e.Graphics, subtitle, Palette.Tiny, new Rectangle(r.Left + Px(13), r.Top + Px(33), r.Width - Px(24) - badgeWidth, Px(20)), Palette.Muted, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            if (installState != LibraryInstallState.None)
            {
                TextRenderer.DrawText(e.Graphics, badge, Palette.Tiny, new Rectangle(r.Right - Px(5) - badgeWidth, r.Top + Px(33), badgeWidth, Px(20)), installState == LibraryInstallState.Disabled ? Palette.Muted : Palette.Green, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            }
        }

        private void DrawInstalledItem(object sender, DrawListViewSubItemEventArgs e)
        {
            GameModRow gameRow = e.Item.Tag as GameModRow; if (gameRow != null) { DrawGameRow(e, gameRow); return; }
            OutfitGroup group = e.Item.Tag as OutfitGroup; if (group != null) { DrawGroupRow(e, group); return; }   // 2.5.0 (MainFormSlots.cs)
            InstalledOutfit outfit = e.Item.Tag as InstalledOutfit; if (outfit == null) return;
            bool selected = e.Item.Selected; Rectangle r = e.Bounds;
            if (groupedSlots.ContainsKey(outfit)) { using (Brush bg = new SolidBrush(selected ? Palette.Selected : Palette.Surface)) e.Graphics.FillRectangle(bg, r); r = new Rectangle(r.Left + Px(20), r.Top, r.Width - Px(20), r.Height); }   // a slot of an expanded group
            using (Brush bg = new SolidBrush(selected ? Palette.Selected : Palette.Surface)) e.Graphics.FillRectangle(bg, r);
            TextRenderer.DrawText(e.Graphics, outfit.Name, Palette.Strong, new Rectangle(r.Left + Px(14), r.Top + Px(10), r.Width - Px(25), Px(24)), Palette.Text, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            string sub = outfit.Character + "   ·   " + (outfit.Enabled ? "Enabled" : "Disabled") + "   ·   " + (outfit.Managed ? "Managed" : outfit.WholeArchive ? "All variants" : "Manual");
            if(!String.IsNullOrEmpty(outfit.LoaderOutfitName)) sub=outfit.Character+"   ·   "+(outfit.LoaderStatus=="Loaded" ? outfit.LoaderOutfitName : "Last: "+outfit.LoaderOutfitName)+"   ·   "+outfit.LoaderStatus;
            if (groupedSlots.ContainsKey(outfit)) sub = SlotOptions(outfit) + (String.IsNullOrEmpty(outfit.LoaderOutfitName) ? "" : "   ·   " + (outfit.LoaderStatus == "Loaded" ? outfit.LoaderOutfitName : "Last: " + outfit.LoaderOutfitName)) + "   ·   " + (outfit.Enabled ? (String.IsNullOrEmpty(outfit.LoaderStatus) ? "Enabled" : outfit.LoaderStatus) : "Disabled");   // 2.5.0: a slot shows its options
            if (outfit.Managed && SourceChanged(outfit.SourcePath)) sub = "Source changed   ·   " + sub;   // 2.4.1: Reinstall applies the edit
            Rectangle meta = DrawSourceGoneNote(e.Graphics, outfit.Managed || outfit.WholeArchive ? outfit.SourcePath : null, new Rectangle(r.Left + Px(14), r.Top + Px(36), r.Width - Px(25), Px(20)));   // 2.4.1 Delete: muted "Source deleted"
            if (!groupedSlots.ContainsKey(outfit)) sub = "Extra   ·   " + sub;   // 1.0.1: the install type, like "Replacement" on game rows; no "played …" (the user)
            TextRenderer.DrawText(e.Graphics, sub, Palette.Small, meta, outfit.Enabled ? Palette.Green : Palette.Muted, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            DrawUsageAfter(e.Graphics, sub, InstalledAgo(outfit), Palette.Small, meta);   // 1.0.1: when it was installed, when it fits
        }

        private void DrawComponentNode(object sender, OptionDrawEventArgs e)
        {
            ModComponent component = e.Node.Tag as ModComponent; if (component == null) { e.DrawDefault = true; return; }
            bool selected = (e.State & TreeNodeStates.Selected) != 0;
            System.Drawing.Drawing2D.GraphicsState saved = e.Graphics.Save();
            try
            {
            Rectangle row = new Rectangle(0, e.Bounds.Y, componentTree.ClientSize.Width, componentTree.ItemHeight);
            e.Graphics.SetClip(row, CombineMode.Intersect);
            bool hovered = e.Node == hoverNode;
            using (Brush bg = new SolidBrush(selected || hovered ? Palette.Raised : Palette.Surface)) e.Graphics.FillRectangle(bg, row);
            int x = e.Bounds.X + Px(18);
            bool required = component.IsDummy && unresolvedIds.Contains(component.Id);
            // Unresolved choose-one groups carry an accent edge as well as a label.
            if (required) using (Brush edge = new SolidBrush(Palette.Accent)) e.Graphics.FillRectangle(edge, 0, row.Y + Px(6), Px(3), row.Height - Px(12));
            if (e.Node.Nodes.Count > 0)
                Glyphs.Chevron(e.Graphics, new PointF(e.Bounds.X - Px(11), row.Y + row.Height / 2F), Px(4), e.Node.IsExpanded ? ChevronDirection.Down : ChevronDirection.Right, hovered ? Palette.Text : Palette.Muted, 1.6F * DeviceDpi / 96F);
            if (!component.IsDummy)
            {
                ModComponent parent = e.Node.Parent == null ? null : e.Node.Parent.Tag as ModComponent;
                ModPackage current = package;   // 2.4.2: null while the tree still shows the previous mod (was a NullReferenceException in paint)
                bool radio = parent != null && current != null && current.IsChoiceBranch(parent,component);
                Rectangle mark = new Rectangle(x - Px(17), row.Y + Px(9), Px(13), Px(13));
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                Color markColor = e.Node.Checked ? Palette.Accent : hovered ? Palette.Text : Palette.Muted;
                using (Pen pen = new Pen(markColor, 1.4F*DeviceDpi/96F))
                { if (radio) e.Graphics.DrawEllipse(pen, mark); else using (GraphicsPath box = Glyphs.Rounded(mark, Px(3))) e.Graphics.DrawPath(pen, box); }
                if (e.Node.Checked)
                { Rectangle inner = Rectangle.Inflate(mark, -Px(3), -Px(3)); if (radio) using (Brush fill = new SolidBrush(Palette.Accent)) e.Graphics.FillEllipse(fill, inner); else Glyphs.FillRounded(e.Graphics, Palette.Accent, inner, Px(1)); }
            }
            bool choice = component.IsDummy && IsChoiceGroup(component);
            string name = component.Name + (choice && !required && !component.InferredChoice ? "  ·  Choose one" : "");
            Font nameFont = component.IsDummy ? Palette.Strong : componentTree.Font;
            int available = Math.Max(1, row.Width - x - Px(8));
            int pillWidth = 0; string pill = component.InferredChoice ? "Choose an option" : "Required · choose one";
            if (required) pillWidth = MeasuredWidth(pill, Palette.Tiny) + Px(20);
            int nameWidth = required ? Math.Min(MeasuredWidth(name, nameFont), Math.Max(Px(40), available - pillWidth - Px(10))) : available;
            const string defaultTag = "  ·  Default";   // 1.0.4: what the mod installs when nothing in the group is picked (OptionOrder)
            if (component.IsDefaultChoice) nameWidth = Math.Min(MeasuredWidth(name, nameFont), Math.Max(Px(40), available - MeasuredWidth(defaultTag, nameFont)));
            TextRenderer.DrawText(e.Graphics, name, nameFont, new Rectangle(x + Px(3), row.Y, nameWidth, row.Height), component.IsDummy ? (required ? Palette.Text : Palette.Muted) : Palette.Text, TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter);
            if (component.IsDefaultChoice)
                TextRenderer.DrawText(e.Graphics, defaultTag, nameFont, new Rectangle(x + Px(3) + nameWidth, row.Y, Math.Max(1, available - nameWidth), row.Height), Palette.Muted, TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter);
            if (required)
            {
                var badge = new Rectangle(x + Px(3) + nameWidth + Px(10), row.Y + (row.Height - Px(22)) / 2, pillWidth, Px(22));
                Glyphs.FillRounded(e.Graphics, Color.FromArgb(64, 44, 27), badge, badge.Height / 2F);
                TextRenderer.DrawText(e.Graphics, pill, Palette.Tiny, badge, Palette.Accent, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
            if (selected && componentTree.Focused) Glyphs.DrawRounded(e.Graphics, Color.FromArgb(170, Palette.Accent), Math.Max(1F, DeviceDpi / 96F), new RectangleF(Px(4), row.Y + Px(2), Math.Max(1, row.Width - Px(8)), row.Height - Px(4)), Px(4));
            }
            finally { e.Graphics.Restore(saved); }
        }

        private void DrawFileItem(object sender, DrawListViewSubItemEventArgs e)
        {
            using (Brush bg = new SolidBrush(e.Item.Selected ? Palette.Selected : Palette.Surface)) e.Graphics.FillRectangle(bg, e.Bounds);
            Rectangle text = Rectangle.Inflate(e.Bounds, -Px(5), 0);
            TextRenderer.DrawText(e.Graphics, e.SubItem.Text, Palette.Small, text, e.ColumnIndex == 0 ? Palette.Text : Palette.Muted, TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }

        private void ShowPage(Panel page)
        {
            bool changed = currentPage != page;
            Control pages = page.Parent;
            Action swap = delegate
            {
                if (changed && pages != null) pages.SuspendLayout();
                try
                {
                    // 1.0.1: the new page is shown on top first, then the others are hidden: no empty frame in between.
                    page.Visible = true; page.BringToFront();
                    libraryPage.Visible = page == libraryPage; installedPage.Visible = page == installedPage; settingsPage.Visible = page == settingsPage; if (assistantPage != null) assistantPage.Visible = page == assistantPage; if (historyPage != null) historyPage.Visible = page == historyPage; if (helpPage != null) helpPage.Visible = page == helpPage; navLibrary.SetActive(page == libraryPage); navInstalled.SetActive(page == installedPage || (historyPage != null && page == historyPage)); navAssistant.SetActive(assistantPage != null && page == assistantPage); navSettings.SetActive(page == settingsPage);
                    if (headerTools != null) headerTools.Visible = page == libraryPage;
                    currentPage = page;
                }
                finally { if (changed && pages != null) pages.ResumeLayout(true); }
            };
            swap();
            if (changed) UpdateGameFilesToast();   // 1.0.10: hidden on Installed, where the banner says it
            if(page==libraryPage || page==installedPage || page==assistantPage) {lastMainPage=page==installedPage ? "Installed" : page==assistantPage ? "Assistant" : "Library"; QueueViewSave();}
            // A page needs DPI metrics once per DPI after it first has a real
            // size; repeating the full pass on every tab switch only re-laid
            // out unchanged controls.
            if(dpiLayoutReady && (pagesLaidOutDpi != DeviceDpi || !pagesLaidOut.Contains(page)))
            {
                if (pagesLaidOutDpi != DeviceDpi) { pagesLaidOut.Clear(); pagesLaidOutDpi = DeviceDpi; }
                page.PerformLayout(); ApplyDpiMetrics();
                // Splitter minimums need a real extent; retry until the page has one.
                bool complete = page == libraryPage ? librarySplit.Width >= Px(952) : page == installedPage ? installedSplit.Width >= Px(768) : page == assistantPage ? assistantPage.Split.Width >= Px(732) : true;
                if (complete) pagesLaidOut.Add(page);
            }
            ShowPageStatus();
            // 1.0.15: the first visit of Installed or Settings runs its tour (MainFormTours.cs; not while restoring the view).
            if (changed && AutoToursAllowed && (page == installedPage || page == settingsPage))
            {
                string tour = page == installedPage ? InstalledTourId : SettingsTourId;
                if (!TourSeen(tour)) BeginInvoke((MethodInvoker)delegate { StartTour(tour, true); });
            }
        }

        // ---- Page-aware status -------------------------------------------------
        // Each page keeps its own last message. Switching pages shows that page's
        // message (or a contextual default) instead of whatever was last set.
        private void SetPageStatus(Panel page, string text, Color color)
        {
            if (page == null) return;
            pageStatus[page] = new KeyValuePair<string, Color>(text ?? "", color);
            if (page == currentPage && !busy) ShowPageStatus();
        }
        private void SetPageStatus(Panel page, string text) { SetPageStatus(page, text, Palette.Muted); }
        private void ClearPageStatus(Panel page) { if (page != null && pageStatus.Remove(page) && page == currentPage && !busy) ShowPageStatus(); }
        // Messages about shared state (installs, uninstalls, scans) apply to both main pages.
        private void SetSharedStatus(string text, Color color) { SetPageStatus(libraryPage, text, color); SetPageStatus(installedPage, text, color); }
        internal string DefaultStatus(Panel page)
        {
            if (page == installedPage) return ReplacePlanner.N(installed.Count, "extra outfit") + (gameRows.Count(r => r.Mod != null) > 0 ? " · " + ReplacePlanner.N(gameRows.Count(r => r.Mod != null), "replacement or tool mod").Replace("replacement or tool mods", "replacements and tool mods") : "");
            if (page == settingsPage) return "Change paths or libraries, then Save settings.";
            if (assistantPage != null && page == assistantPage) return assistantPage.Running ? "The Assistant is working…" : "Describe what you want · the Assistant's changes wait in the Plan for approval";
            if (package != null) return "Ready to compose " + package.Name;
            return ReplacePlanner.N(library.Count, "mod") + " in " + ReplacePlanner.N(config.Libraries.Count, "library") + " · drop an archive or folder to open it";
        }
        private void ShowPageStatus()
        {
            if (statusText == null || busy || currentPage == null) return;
            KeyValuePair<string, Color> status;
            if (pageStatus.TryGetValue(currentPage, out status) && status.Key.Length > 0) { statusText.Text = status.Key; statusText.ForeColor = status.Value; }
            else { statusText.Text = DefaultStatus(currentPage); statusText.ForeColor = Palette.Muted; }
        }

        private void ShowDetailTab(bool files)
        {
            componentPanel.Visible = !files; filesPanel.Visible = files;
            if (files) { filesPanel.BringToFront(); FillFiles(); } else componentPanel.BringToFront();
            componentTab.SetActive(!files); filesTab.SetActive(files);
        }

        private void SetBusy(bool value, string message)
        {
            if (value && hoverTimer != null) { hoverTimer.Stop(); hoverNode = null; }
            if (value) StopReadAhead(true);   // 1.0.12: the archive read ahead may be the one this work renames, deletes or installs
            busy = value; statusProgress.Visible = value;
            // While busy the strip shows the operation; afterwards it returns to the page's own status.
            if (value) { statusText.Text = message; statusText.ForeColor = Palette.Muted; } else ShowPageStatus();
            cancelWork.Visible=value;cancelWork.Enabled=value && activeOperation!=null;
            if(value) statusProgress.Style=ProgressBarStyle.Marquee;
            Cursor = value ? Cursors.AppStarting : Cursors.Default;
            ((BufferedModList)modList).InputLocked = value; componentTree.Enabled = !value;
            ((BufferedModList)installedList).InputLocked=value; UpdateResetOptions();
            targetCharacter.Enabled = !value; outfitName.Enabled = !value;
            previewButton.Enabled = !value && package != null;
            UpdateInstallButton(); UpdateLibraryUninstall(); ShowInstalledSelection(); UpdateRefreshButton(); if (BatchVisible) UpdateBatchUi();
            if (assistantPage != null) assistantPage.UpdateButtons();
            if (!value && portraitRetry && portraitsRequested) EnsurePortraits(false);
        }

        private void Work<T>(string message, Func<T> action, Action<T> completed)
        {
            if (busy) return;
            UndoOnWorkStart();   // 2.4.1: a new change ends the previous Undo
            CancellationTokenSource cancellation=new CancellationTokenSource();activeOperation=cancellation;
            SetBusy(true, message);
            BackgroundWorker worker = new BackgroundWorker {WorkerReportsProgress=true};
            worker.DoWork += delegate(object s, DoWorkEventArgs e)
            {
                try {using(OperationContext.Begin(cancellation.Token,delegate(OperationProgress progress) {worker.ReportProgress(0,progress);})) e.Result=action();}
                catch(OperationCanceledException) {e.Cancel=true;}
            };
            ProgressChangedEventHandler progressed = null;
            progressed = delegate(object s,ProgressChangedEventArgs e)
            {
                if (OffUiThread(delegate { progressed(s, e); })) return;
                if(IsDisposed || activeOperation!=cancellation) return;
                var progress=(OperationProgress)e.UserState;
                cancelWork.Enabled=progress.CanCancel && !cancellation.IsCancellationRequested;
                statusProgress.Style=progress.Total>0 ? ProgressBarStyle.Continuous : ProgressBarStyle.Marquee;
                int percent=progress.Total>0 ? (int)Math.Max(0,Math.Min(100,progress.Completed*100.0/progress.Total)) : 0;
                if(progress.Total>0) statusProgress.Value=percent;
                statusText.Text=cancellation.IsCancellationRequested && progress.CanCancel ? "Cancelling… finishing the current safe step." : progress.Stage+(progress.Total>0 ? " · "+percent+"%" : "…");
            };
            worker.ProgressChanged += progressed;
            RunWorkerCompletedEventHandler finished = null;
            finished = delegate(object s, RunWorkerCompletedEventArgs e)
            {
                if (OffUiThread(delegate { finished(s, e); })) return;   // 2.4.2: never touch the UI from a pool thread
                if(activeOperation==cancellation) activeOperation=null;
                cancellation.Dispose();
                if (IsDisposed) {if(!e.Cancelled && e.Error==null) {var abandoned=e.Result as IDisposable;if(abandoned!=null)abandoned.Dispose();} worker.Dispose(); return; }
                SetBusy(false, "Ready");
                if(e.Cancelled) SetPageStatus(currentPage,"Cancelled. No pending changes were committed.");
                else if (e.Error != null) Error(e.Error);
                else
                {
                    try { completed((T)e.Result); }
                    catch (Exception ex) { Error(ex); }
                }
                worker.Dispose();
            };
            worker.RunWorkerCompleted += finished;
            worker.RunWorkerAsync();
        }

        // 2.4.2: a BackgroundWorker started without the WinForms synchronization context (before the window's first
        // control, or in a check without one) raises its events on a pool thread. Running the completion there raced
        // the UI thread ("Collection was modified" in ConflictView.SetEntries, 2.3.0 log). Returns true when the call
        // was moved to the UI thread.
        internal int OffUiCalls;
        private bool OffUiThread(Action again)
        {
            if (IsDisposed || !IsHandleCreated || !InvokeRequired) return false;
            OffUiCalls++;
            try { BeginInvoke(again); } catch (InvalidOperationException) { return false; }
            return true;
        }

        private sealed class ScanResult { internal List<LibraryEntry> Entries, Pending, NamesPending; internal List<InstalledOutfit> Installed; internal GameRowsResult Game; internal List<UsageRecord> Removed; internal HashSet<string> Changed; }
        private static ScanResult WithChangedSources(ScanResult s) { s.Changed = ChangedSources(s.Installed, s.Game == null ? null : s.Game.Rows); return s; }   // 2.4.1

        // 2.5.2: toTop (F5, the ↻ button, gear → Refresh libraries) scrolls the list back to the top when it is sorted by Added ↓, so the newest mods show.
        private void RefreshLibrary(bool toTop = false)
        {
            string[] libraries = config.Libraries.ToArray(); string root = config.CostumeRoot; string sevenZip = config.SevenZipPath;
            // 1.0.6: the quick listing (what the index knows); the rest is read in the background (MainFormIndexing.cs).
            StopIndexing();
            Work("Scanning libraries and installed outfits…", delegate { List<LibraryEntry> pending, names; List<LibraryEntry> entries = Library.ScanQuick(libraries, sevenZip, out pending, out names); return WithChangedSources(new ScanResult { Entries = entries, Pending = pending, NamesPending = names, Installed = Installer.ListInstalled(root), Game = LoadGameRows(true), Removed = ReadRemovedUsage(root) }); }, delegate(ScanResult result)
            {
                // 1.0.6: LibraryNames.Apply gives the entries the names typed in the Name view.
                library = result.Entries; LibraryNames.Apply(library); installed = result.Installed; UseRemovedUsage(result.Removed); changedSources = result.Changed ?? changedSources; ApplyGameRows(result.Game); UpdateModeUi();
                quietFilters++; try { filterPanel.SetLibraries(libraries); } finally { quietFilters--; }   // a library removed in Settings loses its tile and selection
                ApplyFilters(); FillInstalled(); UpdateLibraryUninstall();
                libraryScanned = true; UpdateLibraryEmpty();   // 1.0.12 (23 Oct)
                if (toTop && librarySort == LibrarySortKey.Added && !librarySortAscending && modList.Items.Count > 0) modList.EnsureVisible(0);
                StartIndexing(result.Pending, result.NamesPending);   // 1.0.6: two background passes (MainFormIndexing.cs)
                // A game update or PAK-method install since the last read changes the PAK identity: re-check.
                if (portraitsRequested) EnsurePortraits(true);
                SetPageStatus(libraryPage, libraries.Length == 0 ? "No mod libraries yet · add one in gear → Settings" : LibraryFoundText(library.Count, libraries.Length) + (package == null ? " · drop an archive or folder to open it" : ""));
                // 2.4.3: nothing is compiled in; when Steam didn't reveal SF6 on the first start, say where to set it.
                if (String.IsNullOrWhiteSpace(config.GameRoot)) SetPageStatus(libraryPage, "SF6 game folder not found. Choose it in gear → Settings → Game folder.", Palette.Accent);
                ClearPageStatus(installedPage);
                RefreshLibraryFolderStatus();
                if (assistantPage != null) assistantPage.LibraryChanged();
                if (package != null && screenshotFile == null) BeginInvoke((Action)delegate { CheckOpenPackage(); });   // 2.4.1: F5 / Refresh also reload a changed open mod
                if (screenshotFile != null)
                {
                    if (screenshotPage != null && screenshotPage.IndexOf("assistant", StringComparison.OrdinalIgnoreCase) >= 0) CaptureScreenshot();
                    else if (!String.IsNullOrWhiteSpace(screenshotSource)) LoadPackage(new LibraryEntry { SourcePath = screenshotSource, Name = Path.GetFileNameWithoutExtension(screenshotSource) });
                    else if (library.Count > 0) LoadPackage(library[0]);
                    else CaptureScreenshot();
                }
                else if (!String.IsNullOrWhiteSpace(openingSource))
                {
                    string source = openingSource; openingSource = null;
                    LoadPackage(new LibraryEntry { SourcePath = source, Name = Path.GetFileNameWithoutExtension(source) });
                }
            });
        }

        private void ApplyFilters()
        {
            if (modList == null || filterPanel == null) return;
            if (!Object.ReferenceEquals(facetRowsSource, library)) RefreshFacetRows();
            // Shared with the Assistant: characters, name words, is:/format:/costume: tokens and costume terms (c1, costume 1).
            LibrarySearch.Query query = LibraryQuery();   // 1.0.6: with Deep search
            HashSet<string> searchedCodes = LibrarySearch.CodesFor(query.Characters);
            // 2.3.0: fighters are OR-ed, the kind tiles (Code tools, Misc with its kinds) OR-ed, the library tiles OR-ed;
            // fighters, kinds, Installed and libraries are AND-ed with each other and with search. Each card or tile
            // counts what selecting it would add to the rest of the selection.
            HashSet<string> fighters = filterPanel.Grid.Selected, miscKinds = filterPanel.MiscSelected, libraries = filterPanel.LibrariesSelected;
            bool toolsOn = filterPanel.ToolsOn, miscOn = filterPanel.MiscOn, kindsOn = toolsOn || miscOn, installedOn = filterPanel.InstalledOn;
            var fighterCounts = new Dictionary<string, int>(StringComparer.Ordinal); var miscCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var libraryCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int toolCount = 0, miscCount = 0, installedCount = 0;
            Action<Dictionary<string, int>, string> count = delegate(Dictionary<string, int> map, string key) { int n; map.TryGetValue(key, out n); map[key] = n + 1; };
            // 1.0.8 MY FILTERS (MainFormCustomFilters.cs): Only chips OR-ed, each Hide chip leaves its matches out, both
            // AND-ed with the rest. While a filter is edited its own state is ignored and the typed keywords (customDraft)
            // narrow the list instead. A chip counts its matches among the mods the other filters and Hide chips leave.
            List<CustomFilter> custom = customFilters; int customCount = custom.Count;
            string[] customKeys = custom.Select(LibraryFilters.MatchKey).ToArray();
            CustomFilterState[] customState = custom.Select(f => f.Id == customEditingId ? CustomFilterState.Off : CustomState(f.Id)).ToArray();
            bool anyOnly = customState.Contains(CustomFilterState.Only);
            CustomFilter draft = customDraft; string draftKey = draft == null ? null : LibraryFilters.MatchKey(draft);
            bool countCustom = FiltersOpen;
            var required = custom.Where((f, i) => countCustom || customState[i] != CustomFilterState.Off).ToList();
            if (draft != null) required.Add(draft);
            listAwaitsMatches = false;
            if (!EnsureCustomMatches(required))
            {
                // 1.0.11: with every chip Off and no draft the matches only count the chips. List the mods now; the
                // chip counts follow when matching ends (it runs ApplyFilters again, and the same rows stay put).
                listAwaitsMatches = draft != null || customState.Any(s => s != CustomFilterState.Off);
                if (listAwaitsMatches) { UpdateCustomDraftStatus(); UpdateLibraryCaption(); return; }
                countCustom = false;
            }
            var customHit = new bool[customCount]; var customTotals = new int[customCount];
            libraryRanks.Clear();
            var ranked = new List<KeyValuePair<LibraryEntry, int>>();
            foreach (LibraryEntry e in library)
            {
                if (!LibrarySearch.MatchesText(e, query, EntryMatchesCharacter, EntryMatchesName)) continue;
                int rank = LibrarySearch.CostumeRank(e, query, searchedCodes, EntryMatchesCharacter);
                if (rank < 0) continue;
                if (!MatchesModType(e, query.Type) || !MatchesFormat(e, query.Format)) continue;
                HashSet<string> codes = FighterCodes(e);
                bool isTool = ModCategories.IsTool(e), isMisc = ModCategories.IsMisc(e), isInstalled = CachedInstallState(e) != LibraryInstallState.None;
                string from = String.IsNullOrEmpty(e.SourcePath) ? "" : FilterPanel.LibraryKey(e.LibraryFolder ?? "");
                bool fighterOk = fighters.Count == 0 || codes.Overlaps(fighters);
                bool kindOk = !kindsOn || (toolsOn && isTool) || (miscOn && isMisc && (miscKinds.Count == 0 || e.MiscAreas.Any(miscKinds.Contains)));
                bool installedOk = !installedOn || isInstalled, libraryOk = libraries.Count == 0 || libraries.Contains(from);
                bool onlyOk = !anyOnly, draftOk = draft == null || CustomMatch(e, draftKey); int hides = 0;
                for (int i = 0; i < customCount; i++)
                {
                    bool hit = customHit[i] = (countCustom || customState[i] != CustomFilterState.Off) && CustomMatch(e, customKeys[i]);
                    if (hit && customState[i] == CustomFilterState.Only) onlyOk = true;
                    if (hit && customState[i] == CustomFilterState.Hide) hides++;
                }
                bool customOk = onlyOk && hides == 0 && draftOk;
                if (fighterOk && kindOk && installedOk && libraryOk && draftOk)
                    for (int i = 0; i < customCount; i++) if (customHit[i] && hides - (customState[i] == CustomFilterState.Hide ? 1 : 0) == 0) customTotals[i]++;
                if (kindOk && installedOk && libraryOk && customOk) foreach (string code in codes) count(fighterCounts, code);
                if (fighterOk && installedOk && libraryOk && customOk) { if (isTool) toolCount++; if (isMisc) { miscCount++; foreach (string label in e.MiscAreas) count(miscCounts, label); } }
                if (fighterOk && kindOk && libraryOk && customOk && isInstalled) installedCount++;
                if (fighterOk && kindOk && installedOk && customOk) count(libraryCounts, from);
                if (!fighterOk || !kindOk || !installedOk || !libraryOk || !customOk) continue;
                ranked.Add(new KeyValuePair<LibraryEntry, int>(e, rank));
                if (query.HasCostume) libraryRanks[e] = rank;
            }
            UpdateFacetCounts(fighterCounts, miscCounts, toolCount, miscCount, installedCount, libraryCounts);
            if (countCustom) { customCounts.Clear(); for (int i = 0; i < customCount; i++) customCounts[custom[i].Id] = customTotals[i]; }
            UpdateCustomChipLooks();
            UpdateFilterButton();
            // Costume searches list mods that change that costume first, then shared parts, then title-only matches.
            // 2.4.1: installed mods (the Installed tile's definition, from the cached state) come first within the same
            // rank, so they lead with no filter, with any filter and among equally relevant search results.
            // 2.4.1 sort chips (LibrarySort.cs): Installed (default ↓) keeps that relevance first; Added and Last used
            // replace it. Ties fall back to the name.
            List<LibraryEntry> rows = SortLibraryRows(ranked);
            bool sameRows = modList.Items.Count == rows.Count;
            for (int i = 0; sameRows && i < rows.Count; i++) sameRows = Object.ReferenceEquals(modList.Items[i].Tag, rows[i]);
            if (!sameRows) UpdateLibraryRows(rows);
            ScrollLibraryToTopIfAsked();
            captionShown = rows.Count; UpdateLibraryCaption();   // 1.0.6: "· READING n%" while mods are read in the background
            UpdateCustomDraftStatus();
        }

        // Each native row insert costs ~0.07 ms, so rebuilding 800+ rows per
        // keystroke was the main search cost. When the visible rows keep their
        // order and only a few leave (widening a search, small refinements),
        // just those rows are removed/inserted; otherwise one cached range is
        // inserted after a single clear.
        private void UpdateLibraryRows(List<LibraryEntry> rows)
        {
            if (!Object.ReferenceEquals(libraryRowsSource, library)) { libraryRows.Clear(); libraryRowsSource = library; }
            Func<LibraryEntry, ListViewItem> rowFor = delegate(LibraryEntry entry)
            {
                ListViewItem item;
                if (!libraryRows.TryGetValue(entry, out item) || item.ListView != null && item.ListView != modList) libraryRows[entry] = item = new ListViewItem(LibraryRowName(entry)) { Tag = entry };
                // Detached rows keep their old flags; a stale selected flag would
                // otherwise reselect (and reload) a mod when it is inserted.
                if (item.ListView == null) { if (item.Selected) item.Selected = false; if (item.Focused) item.Focused = false; string shown = LibraryRowName(entry); if (item.Text != shown) item.Text = shown; }
                return item;
            };
            var target = new Dictionary<LibraryEntry, int>();
            for (int i = 0; i < rows.Count; i++) target[rows[i]] = i;
            int kept = 0, last = -1; bool ordered = true;
            foreach (ListViewItem item in modList.Items)
            {
                var entry = item.Tag as LibraryEntry; int index;
                if (entry == null) { ordered = false; break; }
                if (target.TryGetValue(entry, out index)) { if (index <= last) { ordered = false; break; } last = index; kept++; }
            }
            // 2.4.1 sort chips: the same rows in another order are reordered natively (LVM_SORTITEMS, ~10x cheaper
            // than clearing and inserting 2,667 rows); the selection stays on its row.
            bool sameSet = !ordered && modList.Items.Count == rows.Count && rows.Count > 1;
            if (sameSet) foreach (ListViewItem item in modList.Items) { var entry = item.Tag as LibraryEntry; if (entry == null || !target.ContainsKey(entry)) { sameSet = false; break; } }
            bool previousSelecting = selectingMod; selectingMod = true; ListViewItem keepVisible = null;
            // 1.0.6: a multi-selection (the queue) survives a rebuild for the rows still listed.
            HashSet<LibraryEntry> keepSelected = modList.SelectedItems.Count > 1 ? new HashSet<LibraryEntry>(modList.SelectedItems.Cast<ListViewItem>().Select(i => i.Tag as LibraryEntry).Where(x => x != null)) : null;
            modList.BeginUpdate();
            try
            {
                if (sameSet) { modList.ListViewItemSorter = new LibraryRowOrder(target); modList.ListViewItemSorter = null; if (modList.SelectedItems.Count > 0) keepVisible = modList.SelectedItems[0]; }
                // Measured at 288 DPI: a native row removal costs about four
                // inserts, a full clear about 140. Pick the cheaper path.
                int removed = modList.Items.Count - kept, inserted = rows.Count - kept;
                if (ordered && removed * 4 + inserted * 2 < 140 + rows.Count)
                {
                    for (int i = modList.Items.Count - 1; i >= 0; i--) if (!target.ContainsKey((LibraryEntry)modList.Items[i].Tag)) modList.Items.RemoveAt(i);
                    for (int i = 0; i < rows.Count; i++) if (i >= modList.Items.Count || !Object.ReferenceEquals(modList.Items[i].Tag, rows[i])) modList.Items.Insert(i, rowFor(rows[i]));
                }
                if (modList.Items.Count != rows.Count || Enumerable.Range(0, rows.Count).Any(i => !Object.ReferenceEquals(modList.Items[i].Tag, rows[i])))
                {
                    modList.Items.Clear();
                    modList.Items.AddRange(rows.Select(rowFor).ToArray());
                    // Keep the loaded mod highlighted when it is still listed.
                    if (package != null) foreach (ListViewItem item in modList.Items) if (String.Equals(((LibraryEntry)item.Tag).SourcePath, package.SourcePath, StringComparison.OrdinalIgnoreCase)) { item.Selected = true; item.Focused = true; keepVisible = item; break; }
                }
                // 2.4.1: also after the incremental path (a refresh replaces every entry): the loaded mod's new row is selected.
                if (package != null && modList.SelectedItems.Count == 0) foreach (ListViewItem item in modList.Items) if (String.Equals(((LibraryEntry)item.Tag).SourcePath, package.SourcePath, StringComparison.OrdinalIgnoreCase)) { item.Selected = true; item.Focused = true; keepVisible = item; break; }
                if (keepSelected != null) foreach (ListViewItem item in modList.Items) if (!item.Selected && keepSelected.Contains(item.Tag as LibraryEntry)) item.Selected = true;
            }
            finally { modList.EndUpdate(); selectingMod = previousSelecting; }
            // 2.4.1: a row that moved (installed first) stays selected and in view; the inspector is not reloaded.
            if (keepVisible != null) keepVisible.EnsureVisible();
            ((BufferedModList)modList).FitSingleColumn(true);
        }

        // Large libraries apply search after a short pause in typing; small
        // ones (and every explicit action) filter immediately.
        private void QueueLibraryFilter()
        {
            if (library.Count <= DebouncedLibrarySize || !IsHandleCreated) { searchTimer.Stop(); ApplyFilters(); return; }
            UpdateFilterButton();
            searchTimer.Stop(); searchTimer.Start();
        }
        private void FlushLibraryFilter() { if (searchTimer.Enabled) { searchTimer.Stop(); ApplyFilters(); } }
        internal void OpenTopLibraryMatch()
        {
            FlushLibraryFilter();
            if ((busy && !packageLoading) || modList.Items.Count == 0) return;
            ListViewItem first = modList.Items[0];
            LibraryEntry entry = first.Tag as LibraryEntry;
            if (entry == null) return;
            selectingMod = true;
            try { modList.SelectedItems.Cast<ListViewItem>().ToList().ForEach(row => row.Selected = false); first.Selected = true; first.Focused = true; first.EnsureVisible(); }
            finally { selectingMod = false; }
            ClosePanelForPickedMod();   // 2.3.1: Enter picks a mod
            if (package == null || !String.Equals(package.SourcePath, entry.SourcePath, StringComparison.OrdinalIgnoreCase)) LoadPackage(entry);
        }

        private void UpdateFilterButton()
        {
            if (filterButton == null || filterPanel == null) return;
            int active = ActiveFilterCount();
            filterButton.Text = active == 0 ? "Filters" : "Filters " + active;
            filterButton.SetActive(filterPanel.Visible || active > 0);
            filterButton.AccessibleName = (filterPanel.Visible ? "Hide" : "Show") + " library filters" + (active > 0 ? ", " + active + " active" : "");
            if (filterClearButton != null) filterClearButton.Visible = active > 0;
        }
        // Selected fighters, the kind tiles, and each shared filter count once.
        internal int ActiveFilterCount()
        {
            return (filterPanel.Grid.Selected.Count > 0 ? 1 : 0) + (filterPanel.ToolsOn || filterPanel.MiscOn ? 1 : 0)
                + (filterPanel.InstalledOn ? 1 : 0) + (filterPanel.LibrariesSelected.Count > 0 ? 1 : 0)
                + customFilters.Count(f => CustomState(f.Id) != CustomFilterState.Off);   // 1.0.8: each MY FILTERS chip that is on
        }

        // ---- 2.3.0 Filters panel ----------------------------------------------------------------------------
        private FilterPanel filterPanel;
        private Panel choicesHost;
        private FlatButton filterClearButton;
        private object facetRowsSource;

        // Stage names read from the game's stage-name message (empty without a game folder), for rows and labels.
        internal static Dictionary<string, string> StageNames = new Dictionary<string, string>(StringComparer.Ordinal);
        private CancellationTokenSource portraitLoad;
        private string portraitGame, portraitCacheDirectory;
        private bool portraitsLoaded, portraitsRequested, portraitRetry;
        internal PortraitReport LastPortraitReport;
        private readonly Dictionary<string, string> discoveredFighters = new Dictionary<string, string>(StringComparer.Ordinal);
        internal string PortraitCacheDirectory { get { return portraitCacheDirectory ?? FighterPortraits.DefaultCacheDirectory; } set { portraitCacheDirectory = value; } }
        internal bool PortraitsLoading { get { return portraitLoad != null; } }

        internal bool FiltersOpen { get { return filterPanel != null && filterPanel.Visible; } }
        internal void ShowFilters(bool open, bool focusPanel)
        {
            if (filterPanel == null) return;
            if (open && currentPage != libraryPage) ShowPage(libraryPage);
            if (open && BatchVisible) HideBatch();   // 1.0.6: the Filters panel shows in the inspector's place, which the queue covers
            if (!open) EndCustomFilterEdit();   // 1.0.8: closing the panel cancels a filter edit (and its preview)
            if (filterPanel.Visible != open)
            {
                bool hadFocus = filterPanel.ContainsFocus;
                choicesHost.Visible = !open; filterPanel.Visible = open;
                if (!open && hadFocus && modList.CanFocus) modList.Focus();
            }
            if (open) { portraitsRequested = true; EnsurePortraits(false); if (focusPanel && filterPanel.Grid.CanFocus) filterPanel.Grid.Focus(); ApplyFilters(); }
            UpdateFilterButton(); QueueViewSave();
        }

        private void ClearLibraryFilters()
        {
            // Fighters, Tool mods/Misc, the Misc chips, Installed and libraries; the search text stays (it is not a filter).
            filterPanel.ClearSelections(); pendingMiscKinds.Clear();
            customStates.Clear(); UpdateCustomChipLooks();   // 1.0.8: MY FILTERS chips too
            libraryToTop = true; ApplyFilters(); QueueViewSave();
        }

        private void RestoreFilterPanel(ViewPreferences saved)
        {
            quietFilters++; try { RestoreFilterPanelQuiet(saved); } finally { quietFilters--; }
        }
        private void RestoreFilterPanelQuiet(ViewPreferences saved)
        {
            var fighters = (saved.Fighters ?? new List<string>()).Where(c => c != null && c.Length == 3).ToList();
            if (fighters.Count == 0 && !String.IsNullOrWhiteSpace(saved.Character))
                fighters.AddRange(Characters.ByCode.Where(kv => String.Equals(kv.Value, saved.Character, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key));
            foreach (string code in fighters) if (!filterPanel.Grid.Cards.Any(c => c.Code == code)) discoveredFighters[code] = FighterPortraits.DisplayName(code);
            filterPanel.Grid.SetFighters(GridFighters());
            filterPanel.Grid.SetSelected(fighters, false);
            var kinds = new List<string>(saved.Kinds ?? new List<string>());
            var miscKinds = new List<string>(saved.MiscAreas ?? new List<string>());
            // 2.3.0 test builds had tabs: their tab becomes the kind tile (stages = Misc kind "Stage").
            if (kinds.Count == 0 && saved.FilterTab == ModCategories.Tools) kinds.Add(FilterPanel.ToolsKind);
            if (kinds.Count == 0 && saved.FilterTab == ModCategories.Misc) kinds.Add(FilterPanel.MiscKind);
            if (kinds.Count == 0 && saved.FilterTab == ModCategories.Stages) { kinds.Add(FilterPanel.MiscKind); miscKinds = new List<string> { ModCategories.LabelStage }; }
            if (saved.FilterTab == ModCategories.Tools) miscKinds.Clear();
            if (saved.InstalledOnly) kinds.Add(FilterPanel.InstalledKind);
            // Libraries: the saved tiles, or the 2.2.0 dropdown's single library; only paths still configured (and
            // only with two or more libraries, when the tiles exist).
            var libraries = new List<string>(saved.SelectedLibraries ?? new List<string>());
            if (libraries.Count == 0 && !String.IsNullOrWhiteSpace(saved.Source)) libraries.Add(saved.Source);
            foreach (string library in libraries) { string code = FilterPanel.LibraryPrefix + FilterPanel.LibraryKey(library); if (filterPanel.Kinds.Cards.Any(c => c.Code == code)) kinds.Add(code); }
            filterPanel.SetKinds(kinds.Where(k => k == FilterPanel.ToolsKind || k == FilterPanel.MiscKind || k == FilterPanel.InstalledKind || k.StartsWith(FilterPanel.LibraryPrefix, StringComparison.Ordinal)));
            pendingMiscKinds.Clear(); pendingMiscKinds.UnionWith(miscKinds);
            foreach (FlatButton chip in filterPanel.MiscChips.Controls.OfType<FlatButton>()) chip.SetActive(pendingMiscKinds.Contains((string)chip.Tag));
            pendingMiscKinds.RemoveWhere(k => filterPanel.MiscChips.Controls.OfType<FlatButton>().Any(b => (string)b.Tag == k));
            RestoreCustomStates(saved);   // 1.0.8
        }

        // Fighters of the grid: the built-in roster, fighters found in the game and fighter codes of library mods.
        private List<KeyValuePair<string, string>> GridFighters()
        {
            var all = new Dictionary<string, string>(Characters.ByCode, StringComparer.Ordinal);
            // Unknown esf codes in library paths are not fighters by themselves (mods reference esf040 etc.).
            foreach (var kv in discoveredFighters) if (!all.ContainsKey(kv.Key)) all[kv.Key] = kv.Value;
            return all.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList();
        }

        // Fighter codes a mod belongs to: its native codes, or the fighter its name/detection gives.
        internal static HashSet<string> FighterCodes(LibraryEntry e)
        {
            var codes = new HashSet<string>(e.CharacterCodes ?? new List<string>(), StringComparer.Ordinal);
            if (!String.IsNullOrEmpty(e.Character)) foreach (var kv in Characters.ByCode) if (String.Equals(kv.Value, e.Character, StringComparison.OrdinalIgnoreCase)) codes.Add(kv.Key);
            return codes;
        }

        // Misc kind chips from the whole library (counts come from ApplyFilters); pending kinds from view settings apply once listed.
        private readonly HashSet<string> pendingMiscKinds = new HashSet<string>(StringComparer.Ordinal);
        private IEnumerable<string> MiscKindsSelectedOrPending() { return filterPanel.MiscSelected.Concat(pendingMiscKinds).Distinct(); }
        private void RefreshFacetRows()
        {
            quietFilters++; try { RefreshFacetRowsQuiet(); } finally { quietFilters--; }
        }
        private void RefreshFacetRowsQuiet()
        {
            facetRowsSource = library;
            filterPanel.Grid.SetFighters(GridFighters());
            var selected = new HashSet<string>(MiscKindsSelectedOrPending(), StringComparer.Ordinal);
            List<string> kinds = ModCategories.SortLabels(library.SelectMany(e => e.MiscAreas));
            FlowLayoutPanel chips = filterPanel.MiscChips;
            chips.SuspendLayout();
            try
            {
                foreach (Control old in chips.Controls.Cast<Control>().ToList()) { chips.Controls.Remove(old); old.Dispose(); }
                foreach (string kind in kinds)
                {
                    string key = kind;
                    FlatButton chip = new FlatButton(kind, false) { Tag = key, Height = 26, AutoSize = false, Font = Palette.Small, Margin = new Padding(0, 0, 6, 6), Padding = new Padding(6, 0, 6, 0), AccessibleName = "Misc kind " + kind };
                    chip.Width = TextRenderer.MeasureText(kind + " 999", chip.Font).Width + DpiMetrics.Px(filterPanel, 18);
                    chip.Height = DpiMetrics.Px(filterPanel, 26);
                    chip.SetActive(selected.Contains(key));
                    chip.Click += delegate { chip.SetActive(!chip.Active); pendingMiscKinds.Clear(); filterPanel.RaiseChanged(); };
                    // Arrow keys move between the chips (WinForms); W/A/S/D do the same (2.3.1).
                    chip.KeyDown += delegate(object sender, KeyEventArgs e)
                    {
                        Keys arrow = FighterGrid.WasdArrow(e);
                        if (arrow == e.KeyCode) return;
                        chips.SelectNextControl(chip, arrow == Keys.Right || arrow == Keys.Down, true, false, true);
                        e.Handled = e.SuppressKeyPress = true;
                    };
                    chips.Controls.Add(chip);
                }
            }
            finally { chips.ResumeLayout(); }
            pendingMiscKinds.RemoveWhere(k => kinds.Contains(k));
            FillCustomChips();   // 1.0.8: MY FILTERS, at the same time and size rules as the Misc chips
            filterPanel.Arrange();
        }

        private void UpdateFacetCounts(Dictionary<string, int> fighters, Dictionary<string, int> misc, int tools, int miscTotal, int installedTotal, Dictionary<string, int> libraries)
        {
            filterPanel.Grid.SetCounts(fighters);
            var tiles = new Dictionary<string, int> { { FilterPanel.ToolsKind, tools }, { FilterPanel.MiscKind, miscTotal }, { FilterPanel.InstalledKind, installedTotal } };
            foreach (var kv in libraries) tiles[FilterPanel.LibraryPrefix + kv.Key] = kv.Value;
            // Library codes keep the configured spelling of the path; match them case-insensitively.
            foreach (FighterCard card in filterPanel.Kinds.Cards.Where(c => c.Code.StartsWith(FilterPanel.LibraryPrefix, StringComparison.Ordinal)))
            { int n; tiles[card.Code] = libraries.TryGetValue(card.Code.Substring(FilterPanel.LibraryPrefix.Length), out n) ? n : 0; }
            filterPanel.Kinds.SetCounts(tiles);
            foreach (FlatButton chip in filterPanel.MiscChips.Controls.OfType<FlatButton>())
            {
                int n; misc.TryGetValue((string)chip.Tag, out n);
                string text = (string)chip.Tag + " " + n;
                if (chip.Text != text) chip.Text = text;
            }
            int selected = filterPanel.Grid.Selected.Count;
            string kinds = String.Join(" + ", new[] { filterPanel.ToolsOn ? "tool mods" : null, filterPanel.MiscOn ? (filterPanel.MiscSelected.Count > 0 ? String.Join(", ", filterPanel.MiscSelected) : "misc") : null }.Where(s => s != null));
            // 2.4.1: no summary line in the FIGHTERS row; screen readers get it as the grid's description.
            filterPanel.Grid.AccessibleDescription = (selected == 0 ? "All fighters" : ReplacePlanner.N(selected, "fighter") + " selected") + (kinds.Length > 0 ? " · only " + kinds : "");
        }

        // ---- Portraits (background) ----
        // Reads the cached portraits, or extracts them from the game PAKs, on a pool thread; each arrives with
        // BeginInvoke. Name tiles show until then (and without a game folder). Deferred while a change of
        // this app holds the game lock; re-run when the game folder changes or a read overlapped a change.
        internal void EnsurePortraits(bool recheck)
        {
            if (filterPanel == null || IsDisposed) return;
            string game = config.GameRoot ?? "";
            if (!recheck && portraitsLoaded && String.Equals(portraitGame, game, StringComparison.OrdinalIgnoreCase)) return;
            if (portraitLoad != null)
            {
                if (String.Equals(portraitGame, game, StringComparison.OrdinalIgnoreCase)) return;
                portraitLoad.Cancel(); portraitLoad = null;
            }
            if (!IsHandleCreated) { portraitRetry = true; return; }
            var cancellation = new CancellationTokenSource(); portraitLoad = cancellation; portraitGame = game; portraitRetry = false;
            List<string> ids = GridFighters().Select(kv => kv.Key).ToList(); string cacheDir = PortraitCacheDirectory;
            Func<bool> changing = delegate { return ReplaceEngine.ActiveLocks > 0; };
            ThreadPool.QueueUserWorkItem(delegate
            {
                PortraitReport report = null; Exception failure = null;
                try
                {
                    report = FighterPortraits.Load(game, ids, cacheDir, cancellation.Token, delegate(PortraitInfo info, Bitmap bitmap)
                    {
                        if (!PostToUi(delegate
                        {
                            if (cancellation.IsCancellationRequested || IsDisposed) { if (bitmap != null) bitmap.Dispose(); return; }
                            if (!filterPanel.Grid.Cards.Any(c => c.Code == info.Id)) { discoveredFighters[info.Id] = info.Name; quietFilters++; try { filterPanel.Grid.SetFighters(GridFighters()); } finally { quietFilters--; } }
                            if (installedFighters != null && bitmap != null) { if (!installedFighters.Cards.Any(c => c.Code == info.Id)) installedFighters.SetFighters(GridFighters()); installedFighters.SetPortrait(info.Id, (Bitmap)bitmap.Clone(), info.Ok ? info.Candidate : info.Note); QueueCharacterRenders(); }   // 1.0.1: the Installed picker's copy
                            filterPanel.Grid.SetPortrait(info.Id, bitmap, info.Ok ? info.Candidate : info.Note);
                        }) && bitmap != null) bitmap.Dispose();
                    }, changing);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { failure = ex; }
                PostToUi(delegate
                {
                    if (portraitLoad == cancellation) portraitLoad = null;
                    if (!cancellation.IsCancellationRequested && !IsDisposed) FinishPortraits(report, failure);
                    cancellation.Dispose();
                });
            });
        }
        private bool PostToUi(MethodInvoker action)
        {
            try { if (IsDisposed || !IsHandleCreated) return false; BeginInvoke(action); return true; }
            catch (InvalidOperationException) { return false; }
        }
        private void FinishPortraits(PortraitReport report, Exception failure)
        {
            if (failure != null) { Program.Log(failure); portraitsLoaded = true; return; }
            if (report == null) return;
            LastPortraitReport = report;
            portraitsLoaded = !report.Discarded && report.Cache != "deferred";
            if (!portraitsLoaded) portraitRetry = true;
            if (report.StageNames.Count > 0 || StageNames.Count > 0) { StageNames = new Dictionary<string, string>(report.StageNames, StringComparer.Ordinal); RefreshFacetRows(); ApplyFilters(); modList.Invalidate(); }
            Program.LogLine("Portraits: cache " + report.Cache + ", " + report.Fighters.Count(f => f.Ok) + "/" + report.Fighters.Count + " fighters, " + report.StageNames.Count + " stage names, " + report.TotalMs + " ms" + (report.Discarded ? " (the game changed while reading; not cached)" : ""));
        }

        private bool InstalledAsExtra(LibraryEntry entry) { CachedInstallState(entry); return installedBySource.ContainsKey(entry.SourcePath); }
        private LibraryInstallState CachedInstallState(LibraryEntry entry)
        {
            if (!Object.ReferenceEquals(indexedInstalled,installed))
            {
                indexedInstalled = installed; installStates.Clear();
                installedBySource = installed.Where(o=>(o.Managed || o.WholeArchive) && !String.IsNullOrWhiteSpace(o.SourcePath)).GroupBy(o=>o.SourcePath,StringComparer.OrdinalIgnoreCase).ToDictionary(g=>g.Key,g=>g.ToList(),StringComparer.OrdinalIgnoreCase);
            }
            LibraryInstallState state;
            if (!installStates.TryGetValue(entry,out state))
            {
                List<InstalledOutfit> matches;
                state = installedBySource.TryGetValue(entry.SourcePath,out matches) ? Library.InstallState(entry,matches) : LibraryInstallState.None;
                if (state == LibraryInstallState.None && replacedSources.Contains(entry.SourcePath)) state = LibraryInstallState.Full;
                installStates[entry] = state;
            }
            return state;
        }

        internal static string SearchCharacterTerm(string term)
        {
            string compact = new string(term.ToLowerInvariant().Where(Char.IsLetterOrDigit).ToArray());
            foreach (string name in Characters.Names)
                if (compact == new string(name.ToLowerInvariant().Where(Char.IsLetterOrDigit).ToArray())) return name;
            if (compact == "viper") return "C. Viper";
            if (compact == "honda") return "E. Honda";
            if (compact == "bison") return "M. Bison";
            // 2.0.4: the start of a name, three letters or more, when only one character begins with it
            // ("cam, c1"); an ambiguous start stays a name word.
            if (compact.Length < 3) return null;
            var starts = Characters.Names.Where(name => new string(name.ToLowerInvariant().Where(Char.IsLetterOrDigit).ToArray()).StartsWith(compact, StringComparison.Ordinal)).ToList();
            foreach (var alias in new[] { new[] { "viper", "C. Viper" }, new[] { "honda", "E. Honda" }, new[] { "bison", "M. Bison" } })
                if (alias[0].StartsWith(compact, StringComparison.Ordinal) && !starts.Contains(alias[1])) starts.Add(alias[1]);
            return starts.Count == 1 ? starts[0] : null;
        }

        internal static bool EntryMatchesCharacter(LibraryEntry entry, string character)
        {
            return String.Equals(entry.Character, character, StringComparison.OrdinalIgnoreCase) || entry.CharacterCodes.Any(code => Characters.ByCode.ContainsKey(code) && String.Equals(Characters.ByCode[code], character, StringComparison.OrdinalIgnoreCase));
        }

        internal static bool EntryMatchesName(LibraryEntry entry, string term)
        {
            // 1.0.6: the display name too, and word by word ("viper barefoot" finds C-Viper-Barefoot).
            return LibrarySearch.NameMatches(entry, term);
        }

        // Costume label of a library row ("C1, C3 + shared", "Shared parts (all outfits)", "… · title match"),
        // or for a tool mod without costume files its tool label ("REFramework plugin", "Script + plugin", "DLL").
        internal static string LibraryRowLabel(LibraryEntry entry, int? searchRank)
        {
            string costume = LibrarySearch.CostumeLabel(entry);
            if (searchRank == LibrarySearch.SharedParts) costume = "Shared parts (all outfits)";
            else if (searchRank == LibrarySearch.TitleOnly) costume = (costume.Length > 0 ? costume + " · " : "") + "title match";
            if (costume.Length > 0) return costume;
            string tool = LibrarySearch.ToolLabel(entry);
            // 2.3.0: stage and Misc mods name what they touch ("Stage · Training Room", "UI · Sound").
            return tool.Length > 0 ? tool : ModCategories.RowLabel(entry, StageNames);
        }

        internal static bool MatchesModType(LibraryEntry entry, string type)
        {
            if (String.IsNullOrEmpty(type) || type == "All mod types") return true;
            if (type.Equals("Tool mod", StringComparison.OrdinalIgnoreCase) || type.Equals(LibrarySearch.AnyTool, StringComparison.OrdinalIgnoreCase)) return LibrarySearch.IsToolMod(entry);
            if (type.Equals("Bundle", StringComparison.OrdinalIgnoreCase)) return entry.OptionCount > Library.BundleThreshold;
            if (type.Equals("Normal", StringComparison.OrdinalIgnoreCase) || type.Equals("Standalone", StringComparison.OrdinalIgnoreCase)) return entry.OptionCount >= 0 && entry.OptionCount <= Library.BundleThreshold;
            if (type.Equals("Unreadable", StringComparison.OrdinalIgnoreCase)) return entry.OptionCount < 0;
            // 2.3.0: is:stage, is:misc, is:character.
            if (type.Equals("stage", StringComparison.OrdinalIgnoreCase)) return ModCategories.IsStage(entry);
            if (type.Equals("misc", StringComparison.OrdinalIgnoreCase)) return ModCategories.IsMisc(entry);
            if (type.Equals("character", StringComparison.OrdinalIgnoreCase)) return ModCategories.IsCharacter(entry);
            return false;
        }

        private static bool MatchesFormat(LibraryEntry entry, string format)
        {
            return String.IsNullOrEmpty(format) || format == "All formats" || String.Equals(entry.Kind, format, StringComparison.OrdinalIgnoreCase);
        }

        internal static string LibraryCharacterLabel(LibraryEntry entry)
        {
            if (entry.Detection == "Ambiguous") return "Multiple characters";
            if (entry.Detection == "UnknownNative") return "Unmapped ESF code";
            if (entry.Detection == "Error") return "Source unreadable";
            return String.IsNullOrEmpty(entry.Character) ? "No character detected" : entry.Character;
        }

        private sealed class PackageResult : IDisposable { internal ModPackage Package; internal PreviewImageResult Preview; internal List<string> Defaults; internal string Stamp; public void Dispose() {if(Preview!=null)Preview.Dispose();} }

        // 1.0.3 (the user): a mod opened from the library shows the options it is installed with. The newest record from
        // this source (enabled first) in the shown mode is used (a replacement in Replace original, a Manager extra outfit
        // in Extra outfit), else one of the other mode. IDs the mod no longer has are kept for the review gate
        // (missingRestoredOptions); options without files in the shown mode (a replacement's root-only option opened
        // in Extra outfit) are dropped. No saved selection (or Install all variants) = null, the defaults.
        private List<string> InstalledSelection(ModPackage pkg)
        {
            if (pkg == null || String.IsNullOrEmpty(pkg.SourcePath)) return null;
            Func<string, bool> same = source => String.Equals(source, pkg.SourcePath, StringComparison.OrdinalIgnoreCase);
            List<string> replaced = gameRows.Where(r => r.Mod != null && same(r.Mod.SourcePath) && r.Mod.SelectedIds != null && r.Mod.SelectedIds.Count > 0)
                .Select(r => r.Mod).OrderByDescending(m => m.Enabled).ThenByDescending(m => m.InstalledUtc ?? "", StringComparer.Ordinal).Select(m => m.SelectedIds).FirstOrDefault();
            List<string> extra = installed.Where(o => o.Managed && same(o.SourcePath) && o.SelectedIds != null && o.SelectedIds.Count > 0)
                .OrderByDescending(o => o.Enabled).ThenByDescending(o => o.InstalledUtc).Select(o => o.SelectedIds).FirstOrDefault();
            bool replaceMode = IsReplaceMode;
            List<string> ids = (replaceMode ? replaced ?? extra : extra ?? replaced) ?? new List<string>();
            ids = ids.Where(id => { ModComponent c = pkg.Find(id); return c == null || (!c.IsDummy && c.HasFiles(replaceMode)); }).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return ids.Count > 0 ? ids : null;
        }

        private void LoadPackage(LibraryEntry entry) { LoadPackage(entry, null); }

        private void LoadPackage(LibraryEntry entry, InstalledOutfit restore) { LoadPackage(entry, restore, null); }
        private void LoadPackage(LibraryEntry entry, InstalledOutfit restore, ReplaceMod restoreReplace)
        {
            if (busy)
            {
                // 1.0.12: a library pick while a mod is read waits as the next one (MainFormOpen.cs).
                if (packageLoading && restore == null && restoreReplace == null && pendingReload == null) QueueOpen(entry);
                return;
            }
            StopReadAhead(false, entry.SourcePath);   // 7-Zip for this mod, unless it is the one being read ahead
            // A review of the old package must become obsolete as soon as another load starts, even while
            // that load is still reading and the package field has not been replaced yet.
            if (package != null) RequestReview(1);
            string sevenZip = config.SevenZipPath;
            // 2.3.0: a mod that does not force Replace opens in the remembered mode (an earlier tool package or edited
            // replacement may have forced Replace without changing what is remembered).
            string remembered = InstallModes.Slot;
            try { remembered = ReplaceSettings.Load().StartMode(); } catch (Exception ex) { Program.Log(ex); }
            bool replaceLoad = restoreReplace != null || remembered == InstallModes.Replace;
            PackageReload reload = pendingReload; pendingReload = null;   // 2.4.1: a reload after the source changed on disk
            if (reload != null) replaceLoad = reload.Mode == InstallModes.Replace || restoreReplace != null;
            OpenWork(entry.SourcePath, delegate
            {
                string stamp = SourceStamp(entry.SourcePath);   // before reading: a change while reading is seen next time
                ModPackage loaded = ReadOpenPackage(entry.SourcePath, stamp, sevenZip);
                List<string> defaults = Composer.DefaultSelection(loaded, replaceLoad || IsToolPackage(loaded));
                byte[] image = null;
                try { image = loaded.PreviewBytes(defaults.FirstOrDefault(), sevenZip); } catch { }
                return new PackageResult { Package = loaded, Preview = PreviewImageResult.Decode(image), Defaults = defaults, Stamp = stamp };
            }, delegate(PackageResult result)
            {
                if (reload == null) CollapseInspectorDetails();   // 1.0.8: another mod opens with compact details (1.0.12: when it shows)
                package = result.Package; plan = null; replacePlan = null; packageStamp = result.Stamp;
                // 1.0.1 (the user): INSTALL AS is what you last clicked. A PAK-only or tool mod shows Replace original with
                // Extra outfit faded, only while it is open (never remembered); Edit options opens in the edited outfit's
                // own mode for that edit only.
                forcedReplaceReason = IsPakPackage(package) ? PakModReason : IsToolPackage(package) ? "Tool mods install into the game folder" : restoreReplace != null ? "Editing an installed replacement" : null;
                string wantedMode = forcedReplaceReason != null ? InstallModes.Replace : restore != null ? InstallModes.Slot : reload != null ? reload.Mode : remembered;
                if (installMode != wantedMode) SetInstallMode(wantedMode, false);
                editingReplacement = restoreReplace;
                hoverTimer.Stop(); hoverNode = null; previewGeneration++;
                pendingPreviewPackage = null; pendingPreviewId = null;
                ClearPreviewCache();
                selectingMod = true;
                bool relist = false;
                foreach (LibraryEntry listed in library.Where(e => String.Equals(e.SourcePath, package.SourcePath, StringComparison.OrdinalIgnoreCase)))
                {
                    string known = ListedIdentity(listed);
                    listed.CharacterCodes = package.CharacterCodes.ToList(); listed.DetectionError = null;
                    listed.Character = ModCategories.NameGuessAllowed(listed) ? package.Character : "";   // 2.3.1: no name guess for Misc/Code tools
                    listed.OptionCount = package.Components.Count(c => c.Assets.Count > 0);
                    listed.OptionIds = package.Components.Where(c => c.Assets.Count > 0).Select(c => c.Id).ToList();
                    installStates.Remove(listed);
                    listed.Detection = listed.CharacterCodes.Count > 1 ? "Ambiguous" : listed.CharacterCodes.Count == 1 ? (Characters.ByCode.ContainsKey(listed.CharacterCodes[0]) ? "Native" : "UnknownNative") : String.IsNullOrEmpty(listed.Character) ? "Unknown" : "Filename";
                    if (ListedIdentity(listed) != known) relist = true;
                }
                if (relist) ApplyFilters();   // 1.0.12: only when the read changed what the list filters and sorts on
                foreach (ListViewItem row in modList.Items)
                {
                    LibraryEntry listed = row.Tag as LibraryEntry;
                    if (listed != null && String.Equals(listed.SourcePath, package.SourcePath, StringComparison.OrdinalIgnoreCase))
                    {
                        row.Selected = true;
                        if (!modList.ClientRectangle.IntersectsWith(row.Bounds)) row.EnsureVisible();
                        modList.Invalidate(row.Bounds);
                        break;
                    }
                }
                selectingMod = false;
                packageTitle.Text = package.Name;
                ModComponent primary = package.Components.FirstOrDefault(c => !c.IsDummy && c.Assets.Count > 0) ?? package.Components.FirstOrDefault();
                componentTitle.Text = primary == null ? "OPTION PREVIEW" : primary.Name;
                SetByline(PackageAuthors()); SetVersion(primary == null || String.IsNullOrWhiteSpace(primary.Version) ? PackageVersion() : primary.Version);
                packageDescription.Text = primary == null ? "No options found in this mod." : primary.Description;
                if (String.IsNullOrWhiteSpace(packageDescription.Text)) packageDescription.Text = "Hover an option to preview it. Check its box to include it.";
                packageHint.Text = package.Warnings.Count > 0 ? String.Join("\n", package.Warnings.Take(2)) : "Main files first. Options selected later take precedence when they replace the same file.";
                SetPreview(result.Preview.TakeImage()); previewComponentId = result.Defaults.FirstOrDefault(); inspectedComponentId = previewComponentId;
                if (previewComponentId != null) previewCache.Add(previewComponentId, previewImage);
                outfitNameTyped = false; outfitNameLocked = restore != null;   // 2.5.0: Edit options keeps the outfit's name; otherwise the name is suggested (ApplySuggestedName)
                SetOutfitName(restore == null ? CleanName(package.Name) : restore.Name);
                targetCharacter.SelectedIndex = targetCharacter.Items.IndexOf(restore == null ? package.Character : restore.Character);
                var savedSelection = restoreReplace != null ? restoreReplace.SelectedIds : restore != null ? restore.SelectedIds : reload == null ? InstalledSelection(package) : null;
                string savedNote = "";
                if (savedSelection != null)
                {
                    try { savedSelection = Composer.RestoreSelection(package, savedSelection, out savedNote); }
                    catch (IOException ex) { savedNote = ex.Message; } // retain original IDs: missing/multi-pick validation blocks installation
                }
                FillComponents(savedSelection ?? result.Defaults); ResetPlan(); ShowDetailTab(false); UpdateDestination(); UpdateModeUi();
                // 1.0.12: the health check (extraction, fixes) waits until browsing settles on this mod; the merged-files
                // review of Extra outfit is only a composition and keeps the short delay.
                if (IsReplaceMode) RequestReview(BrowseReviewDelay);
                previewButton.Enabled = true; UpdateLibraryUninstall(); SetPageStatus(libraryPage, savedNote.Length > 0 ? savedNote : "Ready to compose " + package.Name);
                if (reload == null) ShowPage(libraryPage);   // 2.4.1: a reload in the background keeps the page you are on
                if (reload != null) ApplyReload(reload);
                componentTree.BeginInvoke((MethodInvoker)delegate { if (package.Components.Count <= 40) componentTree.ExpandAll(); else { foreach (OptionNode root in componentTree.Nodes) root.Expand(); foreach (string id in selectedOrder) foreach (OptionNode ancestor in Ancestors(componentNodes[id])) ancestor.Expand(); } if (componentTree.Nodes.Count > 0) componentTree.TopNode = componentTree.Nodes[0]; });
            });
        }

        private static string CleanName(string name)
        {
            string cleaned = (name ?? "").Trim();
            foreach (char invalid in Path.GetInvalidFileNameChars()) cleaned = cleaned.Replace(invalid.ToString(), " ");
            return cleaned.Trim().TrimEnd('.');
        }

        private void FillComponents(List<string> defaults)
        {
            package.PrepareChoices();
            missingRestoredOptions.Clear();
            missingRestoredOptions.AddRange(defaults.Where(id => { var c=package.Find(id); return c==null || c.IsDummy || !c.HasFiles(IsReplaceMode); }).Distinct(StringComparer.OrdinalIgnoreCase));
            changingChecks = true; componentTree.BeginUpdate(); componentTree.Nodes.Clear(); componentNodes.Clear(); selectedOrder.Clear();
            foreach (ModComponent c in package.Components)
            {
                OptionNode node = new OptionNode(c.Name) { Tag = c, Checked = !c.IsDummy && defaults.Contains(c.Id, StringComparer.OrdinalIgnoreCase) };
                componentNodes[c.Id] = node;
                if (node.Checked) selectedOrder.Add(c.Id);
            }
            foreach (ModComponent c in package.Components)
            {
                OptionNode node = componentNodes[c.Id]; OptionNode parent;
                if (c.ParentId != null && componentNodes.TryGetValue(c.ParentId, out parent) && !Object.ReferenceEquals(node, parent) && !WouldCycle(node, parent)) parent.Nodes.Add(node);
                else componentTree.Nodes.Add(node);
            }
            selectedOrder.Clear(); selectedOrder.AddRange(defaults.Where(id => componentNodes.ContainsKey(id) && componentNodes[id].Checked).Select(id => ((ModComponent)componentNodes[id].Tag).Id).Distinct(StringComparer.OrdinalIgnoreCase));
            componentTree.EndUpdate();
            foreach (OptionNode root in componentTree.Nodes) root.Expand();
            foreach (string id in selectedOrder) foreach (OptionNode ancestor in Ancestors(componentNodes[id])) ancestor.Expand();
            changingChecks = false; UpdateSelectedCount(); UpdateRequiredChoices();
            componentTree.ShowNodeToolTips = false;
            // A new selection invalidates the last automatic review.
            plan = null; replacePlan = null; RequestReview(ReviewDelay);
        }

        private static bool WouldCycle(OptionNode node, OptionNode parent)
        {
            OptionNode p = parent; while (p != null) { if (p == node) return true; p = p.Parent; } return false;
        }

        private bool IsChoiceGroup(ModComponent component)
        {
            return package != null && package.IsChoice(component);
        }

        private void UncheckBranch(OptionNode node)
        {
            foreach (OptionNode child in new[] {node}.Concat(Descendants(node)))
            {
                child.Checked=false;
                var component=child.Tag as ModComponent;
                if (component!=null) selectedOrder.Remove(component.Id);
            }
        }

        private void EnforceBranchChoice(OptionNode node)
        {
            if (node.Parent==null) return;
            var parent=node.Parent.Tag as ModComponent;
            if (parent==null || !package.IsChoiceBranch(parent,node.Tag as ModComponent)) return;
            foreach (OptionNode sibling in node.Parent.Nodes) if (sibling!=node && package.IsChoiceBranch(parent,sibling.Tag as ModComponent)) UncheckBranch(sibling);
        }

        private void ComponentChecked(object sender, OptionNodeEventArgs e)
        {
            if (changingChecks || package == null) return;
            ModComponent component = e.Node.Tag as ModComponent; if (component == null) return;
            changingChecks = true;
            string groupHint = null;
            if (component.IsDummy)
            {
                e.Node.Checked = false;
                groupHint = IsChoiceGroup(component) ? "Choose one option in this group." : "Select options inside this group.";
            }
            else if (e.Node.Checked)
            {
                EnforceBranchChoice(e.Node);
                foreach (OptionNode ancestor in Ancestors(e.Node))
                {
                    EnforceBranchChoice(ancestor);
                    ModComponent c = ancestor.Tag as ModComponent;
                    if (c != null && !c.IsDummy && c.HasFiles(IsReplaceMode) && !ancestor.Checked) { ancestor.Checked = true; if (!selectedOrder.Contains(c.Id)) selectedOrder.Insert(0, c.Id); }
                }
                selectedOrder.Remove(component.Id); selectedOrder.Add(component.Id);
            }
            else
            {
                selectedOrder.Remove(component.Id);
                foreach (OptionNode child in Descendants(e.Node))
                {
                    ModComponent c = child.Tag as ModComponent;
                    if (c != null && !c.IsDummy) { child.Checked = false; selectedOrder.Remove(c.Id); }
                }
            }
            changingChecks = false; UpdateRequiredChoices(); ResetPlan(); UpdateSelectedCount(); componentTree.Invalidate();
            if (groupHint != null) SetPageStatus(libraryPage, groupHint);
        }

        private static IEnumerable<OptionNode> Ancestors(OptionNode node)
        {
            OptionNode current = node.Parent; while (current != null) { yield return current; current = current.Parent; }
        }

        private static IEnumerable<OptionNode> Descendants(OptionNode node)
        {
            foreach (OptionNode child in node.Nodes) { yield return child; foreach (OptionNode descendant in Descendants(child)) yield return descendant; }
        }

        private void ShowComponent(ModComponent component)
        {
            if (package == null || busy) return;
            int generation = ++previewGeneration;
            inspectedComponentId = component.Id;
            componentTitle.Text = component.Name;
            packageDescription.Text = String.IsNullOrWhiteSpace(component.Description) ? component.Name : component.Description;
            List<string> own = Library.CleanAuthors(new[] { component.Author });
            SetByline(own.Count > 0 ? own : PackageAuthors());   // an option by someone else names them; otherwise the mod's author
            SetVersion(String.IsNullOrWhiteSpace(component.Version) ? PackageVersion() : component.Version);
            if (previewComponentId == component.Id && previewImage != null) return;
            Image cachedImage;
            if (previewCache.TryCopy(component.Id, out cachedImage))
            {
                pendingPreviewPackage = null; pendingPreviewId = null;
                SetPreview(cachedImage); previewComponentId = component.Id; return;
            }
            SetPreview(null); noPreview.Text = "Loading preview…";
            PostponeReview(BrowseReviewDelay);   // 1.0.12: the preview first; the health check waits for browsing to settle
            if (previewLoading) { pendingPreviewPackage = package; pendingPreviewId = component.Id; pendingPreviewGeneration = generation; }
            else ReadComponentPreview(package, component.Id, generation);
        }

        internal void PreviewOption(OptionNode node)
        {
            if (node == null || node.TreeView != componentTree || busy) return;
            SetHoveredOption(node);
            ModComponent component = node.Tag as ModComponent;
            if (component != null) ShowComponent(component);
        }

        private void SetHoveredOption(OptionNode node)
        {
            if (hoverNode == node) return;
            OptionNode previous = hoverNode;
            hoverNode = node;
            // One managed buffer owns the complete row, including its marker.
            // A hover transition never paints a separate native state image;
            // only the two affected rows are repainted.
            if (previous != null && previous.TreeView == componentTree) componentTree.InvalidateNode(previous);
            if (node != null && node.TreeView == componentTree) componentTree.InvalidateNode(node);
        }

        private void ClearPreviewCache() { previewCache.Dispose(); }

        private void ReadComponentPreview(ModPackage current, string id, int generation)
        {
            previewLoading = true; string sevenZip = config.SevenZipPath;
            BackgroundWorker worker = new BackgroundWorker();
            worker.DoWork += delegate(object s, DoWorkEventArgs e) { e.Result = PreviewImageResult.Decode(current.PreviewBytes(id, sevenZip)); };
            worker.RunWorkerCompleted += delegate(object s, RunWorkerCompletedEventArgs e)
            {
                using(var result=e.Error==null ? (PreviewImageResult)e.Result : new PreviewImageResult())
                {
                    if (IsDisposed) { previewLoading = false; worker.Dispose(); return; }
                    if (current == package)
                    {
                        if (e.Error == null) previewCache.Add(id, result.Image);
                        if (generation == previewGeneration && inspectedComponentId == id) { SetPreview(result.TakeImage()); previewComponentId = id; }
                    }
                }
                worker.Dispose();
                previewLoading = false;
                ModPackage next = pendingPreviewPackage; string nextId = pendingPreviewId; int nextGeneration = pendingPreviewGeneration;
                pendingPreviewPackage = null; pendingPreviewId = null;
                if (next == package && next != null && nextGeneration == previewGeneration && inspectedComponentId == nextId)
                {
                    ReadComponentPreview(next, nextId, nextGeneration);
                }
            };
            worker.RunWorkerAsync();
        }

        private void SetPreview(Image next)
        {
            preview.Image = next; Image old = previewImage; previewImage = next; if (old != null) old.Dispose();
            noPreview.Visible = next == null;
            noPreview.Text = "No preview";
        }

        private void EnlargePreview()
        {
            if (previewImage == null) return;
            using (Image copy = new Bitmap(previewImage))
            using (Form viewer = CreatePreviewForm(copy)) viewer.ShowDialog(this);
        }
        private Form CreatePreviewForm(Image previewCopy)
        {
            Form viewer=new Form();viewer.SuspendLayout();
            viewer.AutoScaleDimensions=new SizeF(96F,96F);viewer.AutoScaleMode=AutoScaleMode.Dpi;
            viewer.Text=componentTitle.Text+" · Preview";viewer.ClientSize=new Size(1000,780);viewer.StartPosition=FormStartPosition.CenterParent;
            viewer.BackColor=Palette.Background;viewer.KeyPreview=true;viewer.ShowInTaskbar=false;
            AppBranding.Apply(viewer);
            viewer.Controls.Add(new PictureBox {Dock=DockStyle.Fill,Image=previewCopy,SizeMode=PictureBoxSizeMode.Zoom});
            viewer.KeyDown+=delegate(object sender,KeyEventArgs e) {if(e.KeyCode==Keys.Escape)viewer.Close();};
            viewer.ResumeLayout(true);viewer.PerformAutoScale();
            Rectangle available=Screen.FromControl(this).WorkingArea;
            viewer.Size=new Size(Math.Min(viewer.Width,available.Width-Px(48)),Math.Min(viewer.Height,available.Height-Px(48)));
            return viewer;
        }

        private void UpdateSelectedCount() { selectedLabel.Text = selectedOrder.Count + " selected"; UpdateResetOptions(); }

        private void ShowCompositionReport()
        {
            if (plan == null) return;
            using (Form report = CreateCompositionReport()) report.ShowDialog(this);
        }

        // Used by PolishChecks to verify dialog chrome without a real plan.
        internal Form CreateCompositionReportForTest()
        {
            CompositionPlan saved = plan;
            try { if (plan == null) plan = new CompositionPlan(); return CreateCompositionReport(); }
            finally { plan = saved; }
        }

        private Form CreateCompositionReport()
        {
            Form report = new Form();
            report.SuspendLayout();
            report.AutoScaleDimensions=new SizeF(96F,96F);report.AutoScaleMode=AutoScaleMode.Dpi;
            report.Text="Composition review";report.ClientSize=new Size(780,620);report.MinimumSize=new Size(600,400);report.StartPosition=FormStartPosition.CenterParent;
            report.BackColor=Palette.Surface;report.ForeColor=Palette.Text;report.ShowInTaskbar=false;report.Padding=new Padding(20);
            AppBranding.Apply(report);
            FlowLayoutPanel body = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
            Action<string, Color, bool> line = delegate(string text, Color color, bool heading)
            {
                Label label = new Label { UseMnemonic=false, Text = text, ForeColor = color, Font = heading ? Palette.Strong : Palette.Normal, AutoSize = true, MaximumSize = new Size(690, 0), Margin = new Padding(0, heading ? 16 : 4, 0, 7) };
                body.Controls.Add(label);
            };
            line(plan.CanInstall ? "Ready to install" : "Complete your selection", plan.CanInstall ? Palette.Green : Palette.Text, true);
            line(ReplacePlanner.N(plan.Files.Count, "file") + " · " + Utilities.FriendlyBytes(plan.TotalBytes) + " · " + ReplacePlanner.N(plan.Overrides.Count, "override"), Palette.Muted, false);
            if (plan.Errors.Count > 0)
            {
                line("Required changes", Palette.Red, true);
                foreach (string error in plan.Errors)
                {
                    string issue = error;
                    FlatButton jump = Button(issue + "  →", false, 680, delegate
                    {
                        OptionNode node = componentNodes.Values.FirstOrDefault(n => issue.IndexOf(((ModComponent)n.Tag).Name, StringComparison.OrdinalIgnoreCase) >= 0);
                        ShowDetailTab(false); if (node != null) { node.Expand(); node.EnsureVisible(); componentTree.SelectedNode = node; componentTree.Focus(); } report.Close();
                    });
                    jump.TextAlign = ContentAlignment.MiddleLeft; body.Controls.Add(jump);
                }
            }
            if (plan.Warnings.Count > 0) { line("Notes", Palette.Text, true); foreach (string warning in plan.Warnings) line(warning, Palette.Muted, false); }
            line("Option order", Palette.Text, true);
            for (int i = 0; i < plan.Layers.Count; i++) line((i + 1) + ".  " + plan.Layers[i].Name, Palette.Text, false);
            line("Overrides · the later option wins", Palette.Text, true);
            if (plan.Overrides.Count == 0) line("No selected options overwrite each other.", Palette.Muted, false);
            foreach (var item in plan.Overrides) line(item.Path + "\n" + item.Previous + " → " + item.Winner, Palette.Muted, false);
            body.Resize += delegate { foreach (Control c in body.Controls) { c.MaximumSize = new Size(Math.Max(DpiMetrics.Px(report,100), body.ClientSize.Width-DpiMetrics.Px(report,28)), c is FlatButton ? DpiMetrics.Px(report,38) : 0); if (c is FlatButton) { c.Width = Math.Max(DpiMetrics.Px(report,100), body.ClientSize.Width-DpiMetrics.Px(report,28)); c.Height = DpiMetrics.Px(report,38); } } };
            FlatButton close = Button("Close", false, 110, delegate { report.Close(); }); close.Dock = DockStyle.Bottom;
            report.Controls.Add(body); report.Controls.Add(close); DarkTheme.Apply(report); report.ResumeLayout(true);report.PerformAutoScale();return report;
        }
        private void ResetPlan()
        {
            plan = null; replacePlan = null; ClearFileRows();
            // Selection changed: the previous review result no longer applies.
            KeyValuePair<string, Color> stale;
            if (pageStatus.TryGetValue(libraryPage, out stale) && (stale.Key.StartsWith("Merged outfit preview ready") || stale.Key.StartsWith("Review the composition") || stale.Key.StartsWith("Make the required choices") || stale.Key.StartsWith("Choose one option in") || stale.Key.StartsWith("Health check done") || stale.Key.StartsWith("Health check found"))) ClearPageStatus(libraryPage);
            RequestReview(ReviewDelay);
            ApplySuggestedName();   // 2.5.0: the next slot's name follows the ticks (MainFormSlots.cs)
            UpdateFooter();
        }

        // ---- Required choices and the footer -----------------------------------
        private void UpdateRequiredChoices()
        {
            unresolvedChoices = package == null ? new List<ModComponent>() : Composer.UnresolvedChoiceGroups(package, selectedOrder, IsReplaceMode);
            unresolvedIds.Clear(); foreach (ModComponent group in unresolvedChoices) unresolvedIds.Add(group.Id);
            componentTree.Invalidate();
            UpdateFooter();
        }

        // Short label for a group in the footer, e.g. "Goth Juri skin options" -> "skin".
        internal static string ShortGroupName(ModComponent group, ModPackage owner)
        {
            if (group.InferredChoice) return group.Name.StartsWith("Choose one: ", StringComparison.Ordinal) ? group.Name.Substring(12) : group.Name;
            string name = Regex.Replace(group.Name ?? "", @"^\s*\d+\s*[\).:\-]\s*", "").Trim();
            var generic = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "options", "option", "choices", "choice", "variants", "variant", "select", "choose", "one", "the", "a", "-", "·" };
            var noise = new HashSet<string>(generic, StringComparer.OrdinalIgnoreCase);
            if (owner != null) foreach (string word in Regex.Split((owner.Name ?? "") + " " + (owner.Character ?? ""), @"[^\p{L}\p{N}]+")) if (word.Length > 1) noise.Add(word);
            Func<HashSet<string>, string> strip = delegate(HashSet<string> words) { return String.Join(" ", Regex.Split(name, @"\s+").Where(w => w.Length > 0 && !words.Contains(w.Trim('(', ')', '[', ']', ',', '.')))).Trim(); };
            // Drop the mod's own name words first ("Goth Juri skin options" -> "skin");
            // if nothing is left, drop only the generic words ("Outfit choices" -> "Outfit").
            string result = strip(noise);
            if (result.Length == 0) result = strip(generic);
            return result.Length == 0 ? name : result;
        }

        internal string RequiredChoiceSummary()
        {
            if (unresolvedChoices.Count == 0) return "";
            var names = unresolvedChoices.Select(g => ShortGroupName(g, package)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            string shown = String.Join(", ", names.Take(3));
            return "Choose: " + shown + (names.Count > 3 ? " +" + (names.Count - 3) : "");
        }

        private void UpdateFooter()
        {
            if (planSummary == null) return;
            if (IsReplaceMode && package != null) { UpdateReplaceFooter(); return; }
            string tip = ""; Color detailColor = Palette.Muted;
            if (conflictView != null) UpdateConflictView();
            if (package == null)
            {
                planSummary.Text = "Choose a mod to begin"; planSummary.ForeColor = Palette.Text;
                planDetails.Text = "Select a library mod, use Open archive, or drop a file or folder here.";
            }
            else if (missingRestoredOptions.Count > 0 && plan == null)
            {
                planSummary.Text = "Saved options are missing"; planSummary.ForeColor = Palette.Red;
                planDetails.Text = "Click Review files to check the remaining selection.";
                tip = "Missing saved options: " + String.Join(", ", missingRestoredOptions);
            }
            else if (unresolvedChoices.Count > 0)
            {
                planSummary.Text = RequiredChoiceSummary(); planSummary.ForeColor = Palette.Accent;
                planDetails.Text = unresolvedChoices.All(g => g.InferredChoice) ? "Select an option to install · click to choose" : "Required before Install · click to jump to the first choice" + (plan != null && plan.Errors.Count > 0 ? " · " + plan.Errors.Count + " required changes" : "");
                tip = "Unresolved choices:" + Environment.NewLine + String.Join(Environment.NewLine, unresolvedChoices.Select(g => g.Name));
            }
            else if (ReviewPending)
            {
                // 1.0.12 (the user): only the result is shown; a check still running after ReviewSlowHint says "Checking…".
                planSummary.Text = reviewSlow ? "Checking…" : ""; planSummary.ForeColor = Palette.Muted;
                planDetails.Text = "";
            }
            else if (plan == null)
            {
                planSummary.Text = "Select options"; planSummary.ForeColor = Palette.Text;
                planDetails.Text = "Select main files and any options to compose an outfit.";
            }
            else if (plan.Errors.Count > 0)
            {
                planSummary.Text = plan.Errors.Count == 1 ? "1 required change" : plan.Errors.Count + " required changes"; planSummary.ForeColor = Palette.Red;
                planDetails.Text = plan.Errors[0] + " · View details";
                tip = String.Join(Environment.NewLine, plan.Errors.Concat(plan.Warnings));
            }
            else
            {
                planSummary.Text = "Ready to install · " + ReplacePlanner.N(plan.Files.Count, "file"); planSummary.ForeColor = Palette.Green;
                bool named = targetCharacter.SelectedIndex >= 0 && !String.IsNullOrWhiteSpace(outfitName.Text);
                InstalledOutfit duplicate = DuplicateExtraOutfit();
                planDetails.Text = !named ? "Choose a character and outfit name to install." : duplicate != null ? DuplicateText(duplicate) + (String.Equals(duplicate.Name, outfitName.Text.Trim(), StringComparison.OrdinalIgnoreCase) ? " · Install updates it" : " · installing again adds another extra outfit") : Utilities.FriendlyBytes(plan.TotalBytes) + " · " + plan.Overrides.Count + " overrides · " + (plan.Warnings.Count > 0 ? "View notes" : "Selection reviewed");
                if (named && duplicate != null) detailColor = Palette.Accent;
                tip = String.Join(Environment.NewLine, (duplicate == null ? new string[0] : new[] { DuplicateText(duplicate) + "." }).Concat(plan.Warnings).Concat(plan.Overrides.Select(o => o.Path + ": " + o.Previous + " → " + o.Winner)));
            }
            planDetails.ForeColor = detailColor;
            
            UpdateInstallButton();
        }

        internal void JumpToFirstRequiredChoice()
        {
            if (unresolvedChoices.Count == 0 || package == null) return;
            ShowPage(libraryPage); ShowDetailTab(false);
            OptionNode node;
            if (!componentNodes.TryGetValue(unresolvedChoices[0].Id, out node)) return;
            node.Expand(); node.EnsureVisible();
            componentTree.TopNode = node;
            componentTree.SelectedNode = node; componentTree.Focus();
            SetPageStatus(libraryPage, "Choose one option in “" + node.Text + "”" + (unresolvedChoices.Count > 1 ? " · " + (unresolvedChoices.Count - 1) + " more required" : ""), Palette.Accent);
        }

        private int MeasuredWidth(string text, Font font)
        {
            if (measuredDpi != DeviceDpi) { measuredWidths.Clear(); measuredDpi = DeviceDpi; }
            string key = font.Name + "|" + font.SizeInPoints + "|" + (int)font.Style + "|" + text;
            int width;
            if (!measuredWidths.TryGetValue(key, out width))
            {
                using (Graphics graphics = componentTree.CreateGraphics()) width = TextRenderer.MeasureText(graphics, text, font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine).Width;
                if (measuredWidths.Count > 2000) measuredWidths.Clear();
                measuredWidths[key] = width;
            }
            return width;
        }

        private void FillFiles()
        {
            if (!filesPanel.Visible) { ClearFileRows(); return; }
            if (ReferenceEquals(fileRowsPlan, plan) && fileRowsOverrides == overridesOnly.Checked) return;
            HashSet<string> overridden = plan == null || !overridesOnly.Checked ? null : new HashSet<string>(plan.Overrides.Select(o => o.Path), StringComparer.OrdinalIgnoreCase);
            fileList.VirtualListSize = 0;
            fileRows = plan == null ? new KeyValuePair<string, PlannedFile>[0]
                : plan.Files.Where(p => overridden == null || overridden.Contains(p.Key)).OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).ToArray();
            fileRowsPlan = plan; fileRowsOverrides = overridesOnly.Checked;
            fileList.VirtualListSize = fileRows.Length;
            fileList.Invalidate();
        }

        // Review files / Report and explicit re-checks. The review itself runs automatically
        // (MainFormReview.cs); this shows the current result or waits for it.
        private void BuildPlan(bool switchTab)
        {
            if (package == null || busy) return;
            if (missingRestoredOptions.Count>0 && switchTab)
            {
                // 2.4.1: the click is the consent: review without them and say so inline (the installed outfit stays until Install).
                SetPageStatus(libraryPage, "Reviewing without saved options no longer in the mod: " + String.Join(", ", missingRestoredOptions) + ".", Palette.Accent);
                missingRestoredOptions.Clear(); plan = null; replacePlan = null; RequestReview(1);
            }
            ShowReviewOrWait(switchTab);
        }
        private void UpdateDestination()
        {
            string character = Convert.ToString(targetCharacter.SelectedItem); string name = outfitName.Text.Trim();
            targetPath.Text = String.IsNullOrEmpty(character) || String.IsNullOrEmpty(name) ? "Choose a character and outfit name." : @"costume_mods\" + character + "\\" + name + @"\natives";
            if (plan != null) UpdateFooter(); else UpdateInstallButton();
            UpdateAllVariants();
        }

        private void UpdateInstallButton()
        {
            UpdateAddPartsButton();   // 1.0.6
            if (IsReplaceMode) { UpdateReplaceButtons(); UpdateSlotHint(); return; }
            UpdateSlotHint();
            if (installButton != null) installButton.Enabled = !busy && !ReviewPending && missingRestoredOptions.Count==0 && plan != null && plan.CanInstall && targetCharacter.SelectedIndex >= 0 && !String.IsNullOrWhiteSpace(outfitName.Text);
            // Since 2.0.1 the review runs automatically: Review files is a view, never a required step.
            if (previewButton != null) previewButton.Emphasis = false;
        }

        private void InstallCurrent()
        {
            if (busy || package == null || ReviewPending) return;
            WithFreshPackage(delegate
            {
                if (IsReplaceMode) InstallReplacement(); else InstallSlot();
            });
        }

        private void InstallSlot()
        {
            if (busy || ReviewPending || missingRestoredOptions.Count>0 || plan == null || !plan.CanInstall) return;
            string character = Convert.ToString(targetCharacter.SelectedItem); string name = outfitName.Text.Trim();
            InstalledOutfit existing = installed.FirstOrDefault(o => String.Equals(o.Character, character, StringComparison.OrdinalIgnoreCase) && String.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase));
            bool replace = false;
            if (existing != null)
            {
                if (!existing.Managed) { Error(new InvalidOperationException("An outfit not installed by Manager already uses that name. Choose another name.")); return; }
                if (!existing.Enabled) { Error(new InvalidOperationException("A disabled outfit already uses that name. Enable or uninstall it, or choose another name.")); return; }
                // 2.4.1: the same name is a switch (Install was the consent). 1.0.1: the earlier copy is not kept.
                replace = true;
            }
            CompositionPlan current = plan; string root = config.CostumeRoot; string sevenZip = config.SevenZipPath;
            Work("Installing extra outfit…", delegate { return Installer.Install(current, root, character, name, sevenZip, replace); }, delegate(InstallResult result)
            {
                RefreshInstalled("✓ Installed " + name + " · " + ReplacePlanner.N(result.FileCount, "file") + result.PartsNote + ". Restart SF6 to see changes.");   // 1.0.6: added parts kept/removed
            });
        }

        private void RefreshInstalled(string completionStatus = null)
        {
            string root = config.CostumeRoot;
            keepUndo = true;   // the refresh reports a result; it is not a new change
            try { Work("Reading installed outfits…", delegate { using (InstallMetrics.Measure("installed refresh")) return WithChangedSources(new ScanResult { Installed = Installer.ListInstalled(root), Game = LoadGameRows(true), Removed = ReadRemovedUsage(root) }); }, RefreshInstalledDone(completionStatus)); }
            finally { keepUndo = false; }
        }
        private Action<ScanResult> RefreshInstalledDone(string completionStatus)
        {
            return delegate(ScanResult scan)
            {
                installed = scan.Installed; ApplySuggestedName(); UseRemovedUsage(scan.Removed); changedSources = scan.Changed ?? changedSources; ApplyGameRows(scan.Game); FillInstalled(); ApplyFilters(); UpdateLibraryUninstall(); modList.Invalidate();
                if (completionStatus != null) SetSharedStatus(completionStatus, completionStatus.Contains("wasn't saved") || completionStatus.Contains("Not restored") ? Palette.Accent : Palette.Green);   // 2.4.2: a warning is not a green success
                else SetPageStatus(installedPage, ReplacePlanner.N(installed.Count, "extra outfit"));
                ShowInstalledSelection();
            };
        }

        private void FillInstalled()
        {
            var selectedPaths=new HashSet<string>(installedList.SelectedItems.Cast<ListViewItem>().Select(i=>i.Tag as InstalledOutfit).Where(o=>o!=null).Select(o=>o.Folder),StringComparer.OrdinalIgnoreCase);
            foreach (ListViewItem item in installedList.SelectedItems) { var grouped = item.Tag as OutfitGroup; if (grouped != null) selectedPaths.Add("group:" + grouped.Key); }   // 2.5.0
            foreach (ListViewItem item in installedList.SelectedItems) { var g = item.Tag as GameModRow; if (g != null) selectedPaths.Add("game:" + g.Key); }
            IEnumerable<InstalledOutfit> matches = ShowOutfitRows ? installed : new List<InstalledOutfit>();
            InstalledQuery searchQuery = ParseInstalledSearch();   // 2.4.1: the library's matching (aliases, prefixes, terms)
            matches = matches.Where(o => OutfitMatchesSearch(searchQuery, o)).ToList();
            if (installedCharacters.Count > 0) matches = matches.Where(o => installedCharacters.Contains(o.Character ?? ""));   // 1.0.1: the portrait picker (MainFormCharacterPicker.cs)
            string state = installedState == null ? "" : Convert.ToString(installedState.SelectedItem);
            matches = matches.Where(o => state == "Enabled" ? o.Enabled : state == "Disabled" ? !o.Enabled : state == "Managed" ? o.Managed || o.WholeArchive : state == "Manual" ? !o.Managed && !o.WholeArchive : true);
            List<InstalledOutfit> ordered = matches.OrderBy(o => o.Character, StringComparer.OrdinalIgnoreCase).ThenBy(o => o.Name, StringComparer.OrdinalIgnoreCase).ToList();
            Dictionary<string, OutfitGroup> groups = BuildGroups(ordered); var emitted = new HashSet<string>(); groupedSlots.Clear();   // 2.5.0: slots of one source under one row
            var entries = new List<object>();
            foreach (InstalledOutfit outfit in ordered)
            {
                OutfitGroup group;
                if (outfit.Managed && !String.IsNullOrEmpty(outfit.SourcePath) && groups.TryGetValue(GroupKey(outfit), out group)) { if (emitted.Add(group.Key)) entries.Add(group); }
                else entries.Add(outfit);
            }
            // The rows to show, in order (a group's slots follow it when expanded).
            var wanted = new List<object>();
            foreach (object entry in SortInstalledRows(entries, FilterGameRows().ToList()))   // 2.5.5: the sort chips (MainFormInstalledSort.cs)
            {
                wanted.Add(entry);
                OutfitGroup group = entry as OutfitGroup;
                if (group != null && expandedGroups.Contains(group.Key)) foreach (InstalledOutfit slot in group.Slots) { groupedSlots[slot] = group; wanted.Add(slot); }
            }
            // 1.0.1 (the user: rows flashed on every letter typed): like the library, rows that stay are reused; only
            // rows that leave are removed and new ones inserted, so typing never clears and repaints the whole list.
            var wantedKeys = wanted.Select(InstalledRowKey).ToList();
            var keep = new HashSet<string>(wantedKeys, StringComparer.OrdinalIgnoreCase);
            fillingInstalled = true; bool changed = false;
            try
            {
                var currentKeys = installedList.Items.Cast<ListViewItem>().Select(i => InstalledRowKey(i.Tag)).ToList();
                if (!currentKeys.SequenceEqual(wantedKeys, StringComparer.OrdinalIgnoreCase))
                {
                    changed = true; installedList.BeginUpdate();
                    try
                    {
                        for (int i = installedList.Items.Count - 1; i >= 0; i--) if (!keep.Contains(InstalledRowKey(installedList.Items[i].Tag))) installedList.Items.RemoveAt(i);
                        bool inOrder = true;
                        for (int i = 0; i < wanted.Count && inOrder; i++)
                        {
                            if (i < installedList.Items.Count && String.Equals(InstalledRowKey(installedList.Items[i].Tag), wantedKeys[i], StringComparison.OrdinalIgnoreCase)) continue;
                            if (installedList.Items.Cast<ListViewItem>().Skip(i).Any(x => String.Equals(InstalledRowKey(x.Tag), wantedKeys[i], StringComparison.OrdinalIgnoreCase))) { inOrder = false; break; }   // reordered: rebuild
                            installedList.Items.Insert(i, NewInstalledRow(wanted[i], selectedPaths));
                        }
                        if (!inOrder || installedList.Items.Count != wanted.Count)
                        {
                            installedList.Items.Clear();
                            installedList.Items.AddRange(wanted.Select(w => NewInstalledRow(w, selectedPaths)).ToArray());
                        }
                    }
                    finally { installedList.EndUpdate(); }
                }
                // Kept rows get this fill's objects (groups and outfits are rebuilt by every refresh); painting reads Tag.
                for (int i = 0; i < wanted.Count && i < installedList.Items.Count; i++)
                {
                    ListViewItem item = installedList.Items[i];
                    if (!Object.ReferenceEquals(item.Tag, wanted[i])) { item.Tag = wanted[i]; installedList.RedrawItems(i, i, true); }
                    string text = InstalledRowText(wanted[i]); if (item.Text != text) item.Text = text;
                }
            }
            finally { fillingInstalled = false; }
            if (changed) ((BufferedModList)installedList).FitSingleColumn(true);
            ShowInstalledSelection();
        }
        private static string InstalledRowKey(object tag)
        {
            OutfitGroup group = tag as OutfitGroup; if (group != null) return "group:" + group.Key;
            GameModRow game = tag as GameModRow; if (game != null) return "game:" + game.Key;
            InstalledOutfit outfit = tag as InstalledOutfit; return outfit == null ? "" : outfit.Folder;
        }
        private static string InstalledRowText(object tag)
        {
            OutfitGroup group = tag as OutfitGroup; if (group != null) return group.Label;
            GameModRow game = tag as GameModRow; if (game != null) return game.Name;
            InstalledOutfit outfit = tag as InstalledOutfit; return outfit == null ? "" : outfit.Name;
        }
        private static ListViewItem NewInstalledRow(object tag, HashSet<string> selectedPaths)
        {
            return new ListViewItem(InstalledRowText(tag)) { Tag = tag, Selected = selectedPaths.Contains(InstalledRowKey(tag)) };
        }

        private List<InstalledOutfit> GetInstalledSelections() {return installedList==null ? new List<InstalledOutfit>() : installedList.SelectedItems.Cast<ListViewItem>().SelectMany(row=>row.Tag is OutfitGroup ? ((OutfitGroup)row.Tag).Slots : new List<InstalledOutfit>{row.Tag as InstalledOutfit}).Where(outfit=>outfit!=null).Distinct().ToList();}   // 2.5.0: a group row stands for all its slots
        private InstalledOutfit GetInstalledSelection() {return installedList.SelectedItems.Count==1 ? installedList.SelectedItems[0].Tag as InstalledOutfit : null;}
        private void SelectInstalledRows(bool selected)
        {
            if(busy)return;fillingInstalled=true;installedList.BeginUpdate();
            try {foreach(ListViewItem row in installedList.Items)row.Selected=selected;}
            finally {installedList.EndUpdate();fillingInstalled=false;}
            ShowInstalledSelection();
        }

        // 2.5.4: Clear puts the character, outfit and type filters back to All (the search stays, as in the Mod library).
        private bool InstalledFiltersSet { get { return installedState != null && (installedCharacters.Count > 0 || installedState.SelectedIndex > 0 || installedKind.SelectedIndex > 0); } }
        private void ClearInstalledFilters()
        {
            if (busy || !InstalledFiltersSet) return;
            fillingInstalledFilters = true;
            try { SetInstalledCharacters(new string[0]); installedState.SelectedIndex = 0; installedKind.SelectedIndex = 0; }
            finally { fillingInstalledFilters = false; }
            FillInstalled();
        }
        private bool fillingInstalledFilters;

        private void ShowInstalledSelection()
        {
            if (installedList == null) return;
            var selected=GetInstalledSelections();
            InstalledOutfit outfit = GetInstalledSelection();
            GameModRow gameRow = GetGameRowSelection();
            ShowVariants(outfit);   // 2.5.0: a manual archive/folder with several variants (MainFormVariants.cs)
            moveUpButton.Visible = moveDownButton.Visible = reapplyButton.Visible = gameRow != null && gameRow.Mod != null;
            if (gameRow != null)
            {
                bool idle = RowChangesAllowed;
                installedSelectionCount.Text = "1 selected · " + installedList.Items.Count + " visible";
                selectAllInstalledButton.Enabled = !busy && installedList.Items.Count > 0; clearInstalledSelectionButton.Enabled = !busy; presetsButton.Enabled = !busy;
                openInstalledButton.Enabled = !busy;
                bool own = gameRow.Mod != null;
                editInstalledButton.Visible = toggleInstalledButton.Visible = own;
                // 2.2.0: Fluffy's mods can be uninstalled here too (normal installs, with Fluffy closed).
                // 1.0.11: a leftover patch PAK has Remove PAK (refused inline while SF6 or the external manager runs).
                bool leftover = gameRow.Leftover != null;
                removeInstalledButton.Visible = own || gameRow.Fluffy != null || leftover;
                string fluffyBlocker = gameRow.Fluffy != null ? FluffyUninstallBlocker(gameRow.Fluffy) : leftover ? LeftoverBlocker : null;
                editInstalledButton.Enabled = idle && own; toggleInstalledButton.Enabled = idle && own; removeInstalledButton.Enabled = own ? idle : leftover ? !busy : gameRow.Fluffy != null && fluffyBlocker == null; removeInstalledButton.AccessibleDescription = fluffyBlocker ?? "";
                moveUpButton.Enabled = idle && own && gameRow.Priority > 1; moveDownButton.Enabled = idle && own && gameRow.Priority < gameRows.Count(r => r.Mod != null); reapplyButton.Enabled = idle && own;
                editInstalledButton.Text = "Edit options"; toggleInstalledButton.Text = own && gameRow.Mod.Enabled ? "Disable" : "Enable"; removeInstalledButton.Text = leftover ? LeftoverRemoveText : "Uninstall";
                reinstallButton.Visible = own && SourceChanged(gameRow.Mod.SourcePath); reinstallButton.Enabled = idle && own;
                LayoutInstalledActions(); ShowGameRow(gameRow);
                return;
            }
            installedSelectionCount.Text=selected.Count+" selected · "+installedList.Items.Count+" visible";   // 2.4.1: the multi-select hint is in the detail line
            selectAllInstalledButton.Enabled=!busy && installedList.Items.Count>0;clearInstalledSelectionButton.Enabled=!busy && (selected.Count>0 || InstalledFiltersSet);presetsButton.Enabled=!busy;
            openInstalledButton.Enabled = !busy && outfit != null; editInstalledButton.Enabled = !busy && outfit != null && outfit.Managed && outfit.Enabled && !String.IsNullOrEmpty(outfit.SourcePath); toggleInstalledButton.Enabled = !busy && outfit != null && outfit.Managed; removeInstalledButton.Enabled = !busy && selected.Count>0;
            editInstalledButton.Visible = toggleInstalledButton.Visible = outfit != null && outfit.Managed;removeInstalledButton.Visible=selected.Count>0;
            reinstallButton.Visible = outfit != null && outfit.Managed && SourceChanged(outfit.SourcePath); reinstallButton.Enabled = !busy;
            removeInstalledButton.Text=selected.Count>1 ? "Uninstall selection" : "Uninstall outfit";
            if (outfit != null) toggleInstalledButton.Text = outfit.Enabled ? "Disable outfit" : "Enable outfit";
            OutfitGroup group = GetGroupSelection();   // 2.5.0: one group row = all its slots
            if (group != null) { removeInstalledButton.Text = "Uninstall " + ReplacePlanner.N(group.Slots.Count, "slot"); openInstalledButton.Enabled = !busy; }
            else if (SlotsOfOneGroup() != null) removeInstalledButton.Text = "Uninstall " + ReplacePlanner.N(selected.Count, "slot");   // 2.5.0: some slots of one group
            LayoutInstalledActions();
            if (group != null) { ShowGroupDetails(group); return; }
            if(selected.Count>1)
            {
                installedTitle.Text=selected.Count+" outfits selected";
                installedChips.SetChips(new[] {
                    ChipStrip.Neutral(selected.Count(o=>o.Enabled)+" enabled"), ChipStrip.Quiet(selected.Count(o=>!o.Enabled)+" disabled"),
                    ChipStrip.Neutral(selected.Count(o=>o.Managed)+" managed"), ChipStrip.Quiet(selected.Count(o=>!o.Managed)+" manual") });
                installedDetail.Text=selected.Count(o=>o.Enabled)+" enabled · "+selected.Count(o=>!o.Enabled)+" disabled · "+selected.Count(o=>o.Managed)+" managed · "+selected.Count(o=>!o.Managed)+" manual";
                installedPathRow.Visible=false;
                installedSectionCaption.Text="SELECTED OUTFITS";
                installedHelp.Text=RemovedCopyHelp(selected)+" Libraries are not changed.\r\n\r\n"+String.Join("\r\n",selected.Select(o=>o.Character+" · "+o.Name+(o.Enabled ? "" : " (disabled)")));
                installedHelp.Select(0,0);return;
            }
            if (outfit == null)
            {
                installedTitle.Text = "Choose outfits"; installedChips.SetChips(new ChipStrip.Chip[0]);
                installedDetail.Text = "Ctrl-click or Shift-click to select several."; installedPathRow.Visible = false;
                installedSectionCaption.Text = "TIPS";
                installedHelp.Text = "Uninstall deletes outfits; reinstall them from the Mod library. Manual outfits, which have no source there, go to the Recycle Bin. Save a preset to restore your enabled outfits later.";
                return;
            }
            installedTitle.Text = outfit.Name;
            var chips = new List<ChipStrip.Chip> { ChipStrip.Neutral(outfit.Character) };
            AddAuthorChip(chips, outfit.SourcePath);   // 2.5.9
            if (!String.IsNullOrEmpty(outfit.LoaderOutfitName)) chips.Add(outfit.LoaderStatus == "Loaded" ? ChipStrip.Neutral(outfit.LoaderOutfitName) : ChipStrip.Quiet("Last: " + outfit.LoaderOutfitName));
            string state = String.IsNullOrEmpty(outfit.LoaderStatus) ? (outfit.Enabled ? "Enabled" : "Disabled") : outfit.LoaderStatus;
            chips.Add(state == "Loaded" || state == "Enabled" ? ChipStrip.Good(state) : state == "Pending restart" ? ChipStrip.Warn(state) : ChipStrip.Quiet(state));
            if (!outfit.Enabled && state != "Disabled") chips.Add(ChipStrip.Quiet("Disabled"));
            chips.Add(outfit.Managed ? ChipStrip.Neutral("Managed") : outfit.WholeArchive ? ChipStrip.Neutral("All variants") : ChipStrip.Quiet("Manual"));   // 2.5.4
            if (outfit.Managed && SourceChanged(outfit.SourcePath)) chips.Add(ChipStrip.Warn("Source changed"));   // 2.4.1
            string sourceGone = outfit.Managed || outfit.WholeArchive ? SourceGoneNote(outfit.SourcePath) : null;
            if (sourceGone != null) chips.Add(ChipStrip.Quiet(sourceGone));   // 2.4.1 Delete: still installed, its source is gone
            installedChips.SetChips(chips);
            installedDetail.Text = outfit.Folder; installedPathRow.Visible = true;
            string help = (outfit.Managed ? "Installed by Manager. Disable moves it out of costume_mods. " : outfit.WholeArchive ? "Every variant, copied by the Manager from " + Path.GetFileName(outfit.SourcePath) + ". " : "Not installed by Manager. ") + RemovedCopyHelp(new List<InstalledOutfit> { outfit });
            if(!String.IsNullOrEmpty(outfit.LoaderStatus))
            {
                installedSectionCaption.Text = "LAST SLOTSLOADER RUN · " + (String.IsNullOrEmpty(outfit.LoaderGenerated) ? "NO RESULT" : outfit.LoaderGenerated.ToUpperInvariant());
                installedHelp.Text = FormatLoaderDetails(outfit.LoaderOutfitDetails) + help;
            }
            else { installedSectionCaption.Text = "ABOUT"; installedHelp.Text = help; }
            installedHelp.Text=installedHelp.Text.Replace("\r\n","\n").Replace("\n",Environment.NewLine);
            installedHelp.Select(0,0);
        }

        // "esf013 · Outfit I · A.K.I/Archive.zip/01 Main files" becomes a short
        // heading line and an indented source line, one block per mapping.
        internal static string FormatLoaderDetails(string details)
        {
            if (String.IsNullOrWhiteSpace(details)) return "No mapping in the last Slotsloader run.\r\n\r\n";
            var blocks = new List<string>();
            foreach (string line in details.Replace("\r\n", "\n").Split('\n').Where(l => l.Trim().Length > 0))
            {
                string[] parts = line.Split(new[] { " · " }, 3, StringSplitOptions.None);
                blocks.Add(parts.Length == 3 ? parts[1].Trim() + "  ·  " + parts[0].Trim() + "\r\n" + parts[2].Trim() : line.Trim());
            }
            return String.Join("\r\n\r\n", blocks) + "\r\n\r\n";
        }

        // Visible actions pack into two equal columns, so hidden actions never
        // leave a staggered gap.
        private void LayoutInstalledActions()
        {
            if (installedActions == null) return;
            var buttons = new[] { reinstallButton, openInstalledButton, editInstalledButton, toggleInstalledButton, removeInstalledButton, moveUpButton, moveDownButton, reapplyButton };
            // Control.Visible reads false while the page is hidden; use the requested state.
            var shown = buttons.Where(b => InstalledActionShown(b)).ToList();
            int gap = Px(8), height = Px(38), top = installedActions.Padding.Top;
            int width = Math.Max(Px(60), (installedActions.ClientSize.Width - gap) / 2);
            for (int i = 0; i < shown.Count; i++) shown[i].Bounds = new Rectangle((i % 2) * (width + gap), top + (i / 2) * (height + gap), width, height);
            int rows = Math.Max(1, (shown.Count + 1) / 2);
            int wanted = top + rows * height + (rows - 1) * gap;
            if (installedActions.Height != wanted) installedActions.Height = wanted;
        }
        private bool InstalledActionShown(FlatButton button)
        {
            var outfit = GetInstalledSelection(); int count = installedList == null ? 0 : installedList.SelectedItems.Count;
            if (button == openInstalledButton) return true;
            GameModRow gameRow = installedList == null ? null : GetGameRowSelection();
            if (button == reinstallButton) return count == 1 && (gameRow != null ? gameRow.Mod != null && SourceChanged(gameRow.Mod.SourcePath) : outfit != null && outfit.Managed && SourceChanged(outfit.SourcePath));
            if (gameRow != null) return gameRow.Mod != null || (button == removeInstalledButton && (gameRow.Fluffy != null || gameRow.Leftover != null));
            if (button == moveUpButton || button == moveDownButton || button == reapplyButton) return false;
            if (button == removeInstalledButton) return count > 0;
            return outfit != null && outfit.Managed;
        }
        private void CopyInstalledPath()
        {
            InstalledOutfit outfit = GetInstalledSelection(); OutfitGroup group = GetGroupSelection();
            if (group != null) outfit = new InstalledOutfit { Folder = group.Source };   // 2.5.0: a group row copies its source
            if (outfit == null) return;
            try { Clipboard.SetText(outfit.Folder); SetPageStatus(installedPage, "Copied path: " + outfit.Folder); }
            catch (Exception error) { SetPageStatus(installedPage, "Could not copy the path: " + error.Message, Palette.Red); }
        }

        private void ToggleOutfit()
        {
            GameModRow gameRow = GetGameRowSelection(); if (gameRow != null) { if (!busy) ToggleGameRow(gameRow); return; }
            InstalledOutfit outfit = GetInstalledSelection(); if (outfit == null || !outfit.Managed || busy) return;
            string root = config.CostumeRoot; bool enabled = !outfit.Enabled;
            Work(enabled ? "Enabling outfit…" : "Disabling outfit…", delegate { return Installer.SetEnabled(outfit, root, enabled); }, delegate(string path) { RefreshInstalled(); });
        }

        private void EditOutfit()
        {
            GameModRow gameRow = GetGameRowSelection(); if (gameRow != null) { if (!busy) EditGameRow(gameRow); return; }
            InstalledOutfit outfit = GetInstalledSelection(); if (outfit == null || !outfit.Managed || busy) return;
            if (!File.Exists(outfit.SourcePath) && !Directory.Exists(outfit.SourcePath)) { Error(new FileNotFoundException("Mod not found: " + outfit.SourcePath + ". Restore it there to edit this outfit.")); return; }
            ShowPage(libraryPage);
            LoadPackage(new LibraryEntry { SourcePath = outfit.SourcePath, Name = outfit.Name }, outfit);
        }

        private void RemoveOutfit()
        {
            GameModRow gameRow = GetGameRowSelection(); if (gameRow != null) { if (!busy) UninstallGameRow(gameRow); return; }
            var selected=GetInstalledSelections();if(busy || selected.Count==0)return;
            // 2.4.1: no confirmation. 1.0.1: the selection is deleted (no source: Recycle Bin); no Undo.
            OutfitGroup group = GetGroupSelection();   // 2.5.0: a group row uninstalls all its slots
            OutfitGroup some = group == null ? SlotsOfOneGroup() : null;
            RemoveOutfitsWithUndo(selected, "Uninstalled " + (some != null ? ReplacePlanner.N(selected.Count, "slot") + " of " + some.Name : group != null ? group.Name + " (" + ReplacePlanner.N(group.Slots.Count, "slot") + ")" : selected.Count == 1 ? selected[0].Name : ReplacePlanner.N(selected.Count, "outfit")));
        }

        private List<InstalledOutfit> LinkedOutfits(string sourcePath)
        {
            if (String.IsNullOrWhiteSpace(sourcePath)) return new List<InstalledOutfit>();
            return installed.Where(o => (o.Managed || o.WholeArchive) && String.Equals(o.SourcePath, sourcePath, StringComparison.OrdinalIgnoreCase))   // 2.5.4: also Install all variants
                .OrderBy(o => o.Character, StringComparer.OrdinalIgnoreCase).ThenBy(o => o.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        // Replacements and tool mods installed from this source (Replace-original mode), highest priority first.
        private List<GameModRow> LinkedReplacements(string sourcePath)
        {
            if (String.IsNullOrWhiteSpace(sourcePath)) return new List<GameModRow>();
            return gameRows.Where(r => r.Mod != null && String.Equals(r.Mod.SourcePath, sourcePath, StringComparison.OrdinalIgnoreCase)).OrderBy(r => r.Priority).ToList();
        }

        // Why a replacement can't be uninstalled right now (same wording as the inspector's Uninstall link), or null.
        private string ReplacementUninstallBlocker(IEnumerable<GameModRow> rows = null)
        {
            if (busy) return "Wait for the current task to finish.";
            if (rows == null ? FluffyBlocksPak : FluffyBlocks(rows.Select(r => r.Mod))) return FluffyPakReason;
            if (gameRunning && gameMethod != ReplaceMethods.Loose) return "Close SF6 first: game files can't change while it runs.";
            return null;
        }
        private string ReplacementBlockerSuffix(IEnumerable<GameModRow> rows = null)
        {
            if (busy) return "";
            if (rows == null ? FluffyBlocksPak : FluffyBlocks(rows.Select(r => r.Mod))) return " (close the external mod manager first: same PAK)";
            if (gameRunning && gameMethod != ReplaceMethods.Loose) return " (close SF6 first)";
            return "";
        }
        private static string ReplacementAction(List<GameModRow> rows) { return rows.Count > 0 && rows.All(r => r.Kind == "Tool") ? "Uninstall tool mod" : "Uninstall replacement"; }
        private static string ReplacementChoice(GameModRow row) { return ((row.Kind == "Tool" ? "Tool mod" : "Replacement") + " #" + row.Priority + " · " + row.Name + (row.Mod.Enabled ? "" : " (disabled)")).Replace("&", "&&"); }

        private void UpdateLibraryUninstall()
        {
            if (libraryUninstallButton == null) return;
            string source = package == null ? null : package.SourcePath;
            int outfits = LinkedOutfits(source).Count; List<GameModRow> replacements = LinkedReplacements(source);
            string blocker = replacements.Count > 0 ? ReplacementUninstallBlocker(replacements) : null;
            // Extra outfit mode always shows the button; Replace mode only when this source is installed (either way).
            // 2.5.3: it removes what is installed, whatever INSTALL AS shows (LibraryUninstallsReplacements).
            libraryUninstallButton.Visible = !IsReplaceMode || replacements.Count > 0 || outfits > 0;
            libraryUninstallButton.Text = LibraryUninstallsReplacements(outfits, replacements.Count) ? "Uninstall" : "Uninstall outfit";
            libraryUninstallButton.Enabled = !busy && package != null && (outfits > 0 || (replacements.Count > 0 && blocker == null));
            libraryUninstallButton.AccessibleDescription = blocker ?? "Shortcut: tap Ctrl";   // 2.4.0: no tooltip; the header shows "● Mod manager running"
            UpdateAllVariants();   // 2.5.4 (MainFormAllVariants.cs)
        }

        private void PopulateReplacementUninstall(ToolStripMenuItem action, string sourcePath)
        {
            foreach (ToolStripItem item in action.DropDownItems.Cast<ToolStripItem>().ToArray()) item.Dispose();
            action.DropDownItems.Clear(); action.Tag = null;
            List<GameModRow> rows = LinkedReplacements(sourcePath);
            string blocker = ReplacementUninstallBlocker(rows);
            action.Visible = rows.Count > 0;
            action.Enabled = rows.Count > 0 && blocker == null; action.AccessibleDescription = blocker ?? "";
            action.Text = ReplacementAction(rows) + (rows.Count > 0 ? ReplacementBlockerSuffix(rows) : "");
            if (rows.Count > 0) action.Tag = rows[0];   // 2.4.1: one item uninstalls every record of the source
        }

        // The Installed page's recoverable flow: confirmation, then the engine uninstall that keeps a copy.
        private void UninstallLibraryReplacement(GameModRow row)
        {
            if (row == null || row.Mod == null || busy) return;
            string blocker = ReplacementUninstallBlocker(new[] { row });
            if (blocker != null) { SetPageStatus(currentPage, blocker, Palette.Accent); return; }
            UninstallGameRow(row);
        }

        private void PopulateLibraryUninstall(ToolStripMenuItem action, string sourcePath)
        {
            foreach (ToolStripItem item in action.DropDownItems.Cast<ToolStripItem>().ToArray()) item.Dispose();
            action.DropDownItems.Clear(); action.Tag = null;
            List<InstalledOutfit> matches = LinkedOutfits(sourcePath);
            action.Enabled = !busy && matches.Count > 0;
            if (matches.Count > 0) action.Tag = matches[0];   // 2.4.1: one item uninstalls every extra outfit of the source
            action.Text = matches.Count > 1 ? "Uninstall outfits (" + matches.Count + ")" : "Uninstall outfit";
        }

        // 2.4.1: one click, no menu, no confirmation: every install of this mod in the current mode (all its
        // replacements in Replace original, all its extra outfits in Extra outfit), recoverably, with Undo.
        // 2.5.3: when the mode's kind has nothing installed, the other kind is removed (before, a replacement-only
        // mod showed an enabled "Uninstall outfit" in Extra outfit mode that did nothing).
        private void UninstallLibraryOutfit()
        {
            if (busy || package == null) return;
            if (LibraryUninstallsReplacements(LinkedOutfits(package.SourcePath).Count, LinkedReplacements(package.SourcePath).Count)) UninstallAllReplacements(package.SourcePath);
            else UninstallAllOutfits(package.SourcePath);
        }
        private bool LibraryUninstallsReplacements(int outfits, int replacements) { return IsReplaceMode ? replacements > 0 || outfits == 0 : outfits == 0 && replacements > 0; }

        private void RemoveOutfit(InstalledOutfit outfit)
        {
            if (outfit == null || !outfit.Managed || busy) return;
            RemoveOutfitsWithUndo(new List<InstalledOutfit> { outfit }, "Uninstalled " + outfit.Name);
        }

        private void LoadSettingsFields()
        {
            RefreshLibraryFolderStatus();
            settingsGame.Text = config.GameRoot; settingsSevenZip.Text = File.Exists(config.SevenZipPath ?? "") ? config.SevenZipPath : ""; showSevenZipNote(); settingsLibraries.Items.Clear(); settingsLibraries.Items.AddRange(config.Libraries.Cast<object>().ToArray()); settingsStatus.Text = "Settings folder: " + AppConfig.DataDir;
            settingsStatus.ForeColor = Palette.Muted;
            if (removeLibraryButton != null) removeLibraryButton.Enabled = settingsLibraries.SelectedIndex >= 0;
            LoadClaudeSettings();
        }

        // Found/missing and the mod count from the last scan, cached so that
        // painting never touches the disk.
        private void RefreshLibraryFolderStatus()
        {
            libraryFolderStatus.Clear();
            if (settingsLibraries != null) settingsLibraries.Invalidate();
        }
        private string LibraryFolderStatus(string folder, out Color color)
        {
            string status;
            if (!libraryFolderStatus.TryGetValue(folder, out status))
            {
                bool exists = false; try { exists = Directory.Exists(folder); } catch { }
                string trimmed = folder.TrimEnd(Path.DirectorySeparatorChar);
                bool configured = config.Libraries.Any(l => String.Equals(l.TrimEnd(Path.DirectorySeparatorChar), trimmed, StringComparison.OrdinalIgnoreCase));
                int count = library.Count(e => String.Equals(e.LibraryFolder, trimmed, StringComparison.OrdinalIgnoreCase));
                status = !exists ? "missing|Folder not found" : configured && library.Count > 0 ? "ok|Found · " + count + (count == 1 ? " mod" : " mods") : "ok|Found · scanned after Save settings";
                libraryFolderStatus[folder] = status;
            }
            color = status.StartsWith("missing|") ? Palette.Red : Palette.Green;
            return status.Substring(status.IndexOf('|') + 1);
        }
        private void DrawLibraryFolder(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= settingsLibraries.Items.Count) { using (Brush empty = new SolidBrush(Palette.Background)) e.Graphics.FillRectangle(empty, e.Bounds); return; }
            bool selected = (e.State & DrawItemState.Selected) != 0;
            using (Brush bg = new SolidBrush(Palette.Background)) e.Graphics.FillRectangle(bg, e.Bounds);
            Rectangle card = new Rectangle(e.Bounds.Left + Px(4), e.Bounds.Top + Px(3), e.Bounds.Width - Px(8), e.Bounds.Height - Px(6));
            if (selected) Glyphs.FillRounded(e.Graphics, Palette.Selected, card, Px(4));
            if (selected) using (Brush edge = new SolidBrush(Palette.Accent)) e.Graphics.FillRectangle(edge, card.Left, card.Top + Px(6), Px(3), card.Height - Px(12));
            string folder = Convert.ToString(settingsLibraries.Items[e.Index]);
            Color statusColor; string status = LibraryFolderStatus(folder, out statusColor);
            int left = card.Left + Px(14), width = Math.Max(1, card.Width - Px(24));
            TextRenderer.DrawText(e.Graphics, folder, Palette.Normal, new Rectangle(left, card.Top + Px(3), width, Px(22)), Palette.Text, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.PathEllipsis | TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(e.Graphics, status, Palette.Small, new Rectangle(left, card.Top + Px(23), width, Px(18)), statusColor, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);
            if ((e.State & DrawItemState.Focus) != 0 && (e.State & DrawItemState.NoFocusRect) == 0) Glyphs.DrawRounded(e.Graphics, Color.FromArgb(170, Palette.Accent), Math.Max(1F, DeviceDpi / 96F), RectangleF.Inflate(card, -0.5F, -0.5F), Px(4));
        }

        private void SaveSettings()
        {
            if (busy) return;
            try
            {
                string game = settingsGame.Text.Trim();
                if (String.IsNullOrWhiteSpace(game) || !Directory.Exists(game)) throw new InvalidOperationException("Game folder not found. Choose your SF6 folder.");
                string seven = settingsSevenZip.Text.Trim();
                if (seven.Length > 0 && !File.Exists(seven)) throw new InvalidOperationException("7-Zip not found at that path. Choose 7z.exe again or clear the field.");
                List<string> paths = settingsLibraries.Items.Cast<string>().Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                string missing = paths.FirstOrDefault(p => !Directory.Exists(p));
                if (missing != null) throw new InvalidOperationException("Library folder not found: " + missing);
                config.GameRoot = Path.GetFullPath(game); config.SevenZipPath = seven; config.Libraries = paths; config.Save(); SevenZipTool.ClearCache();
                UpdateLocation(); UpdateDestination(); settingsStatus.Text = "Saved · " + paths.Count + (paths.Count == 1 ? " library" : " libraries"); settingsStatus.ForeColor = Palette.Green;
                RefreshLibrary();
            }
            catch (Exception e) { Error(e); }
        }

        private void UpdateLocation() {  }

        private void ChooseSettingFolder(PaddedTextField field)
        {
            using (FolderBrowserDialog dialog = new FolderBrowserDialog { Description = "Choose the SF6 game folder", SelectedPath = Directory.Exists(field.Text) ? field.Text : "" }) if (dialog.ShowDialog(this) == DialogResult.OK) field.Text = dialog.SelectedPath;
        }

        private void ChoosePackageFile()
        {
            if (busy) return;
            using (OpenFileDialog dialog = new OpenFileDialog { Title = "Open a mod archive", Filter = "Mod archives|*.zip;*.rar;*.7z|All files|*.*", InitialDirectory = config.Libraries.FirstOrDefault(Directory.Exists) ?? "" }) if (dialog.ShowDialog(this) == DialogResult.OK) OpenSource(dialog.FileName);
        }

        private void ChoosePackageFolder()
        {
            if (busy) return;
            using (FolderBrowserDialog dialog = new FolderBrowserDialog { Description = "Choose an extracted mod folder", SelectedPath = config.Libraries.FirstOrDefault(Directory.Exists) ?? "" }) if (dialog.ShowDialog(this) == DialogResult.OK) OpenSource(dialog.SelectedPath);
        }

        // The single path used by Open archive, Open folder and drag and drop.
        internal void OpenSource(string path)
        {
            if (busy || String.IsNullOrWhiteSpace(path)) return;
            bool folder = Directory.Exists(path);
            string trimmed = folder ? path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : path;
            LoadPackage(new LibraryEntry { SourcePath = trimmed, Name = folder ? Path.GetFileName(trimmed) : Path.GetFileNameWithoutExtension(trimmed) });
        }

        // ---- Drag and drop -------------------------------------------------------
        private void WireDropTarget()
        {
            AllowDrop = true;
            DragEnter += delegate(object sender, DragEventArgs e) { e.Effect = PresentDrop(e.Data); };
            DragOver += delegate(object sender, DragEventArgs e) { e.Effect = PresentDrop(e.Data); };
            DragLeave += delegate { HideDropOverlay(); };
            DragDrop += delegate(object sender, DragEventArgs e)
            {
                HideDropOverlay();
                string reason; string path = DropPaths.Accept(DropPaths.FromData(e.Data), out reason);
                if (path == null) { SetPageStatus(currentPage, reason, Palette.Red); return; }
                if (busy) { SetPageStatus(currentPage, "Wait for the current task to finish, then drop the mod again.", Palette.Red); return; }
                OpenSource(path);
            };
        }
        private string lastDropKey;
        private DragDropEffects PresentDrop(IDataObject data)
        {
            string[] paths = DropPaths.FromData(data);
            string reason; string path = DropPaths.Accept(paths, out reason);
            bool accepting = path != null && !busy;
            string key = (path ?? "") + "|" + reason + "|" + accepting;
            if (key != lastDropKey || !dropOverlay.Visible)
            {
                lastDropKey = key;
                if (accepting) dropOverlay.Present(true, "Drop to open " + (Directory.Exists(path) ? Path.GetFileName(path) : Path.GetFileName(path)), Directory.Exists(path) ? "Extracted mod folder · opens in Mod library" : Path.GetExtension(path).TrimStart('.').ToUpperInvariant() + " archive · opens in Mod library");
                else dropOverlay.Present(false, path != null ? "Busy right now" : "Can't open this", path != null ? "Wait for the current task to finish, then drop it again." : reason);
                if (!dropOverlay.Visible) { dropOverlay.Visible = true; dropOverlay.BringToFront(); }
                dropOverlay.Update();
            }
            return accepting ? DragDropEffects.Copy : DragDropEffects.None;
        }
        private void HideDropOverlay() { lastDropKey = null; if (dropOverlay != null && dropOverlay.Visible) dropOverlay.Visible = false; }

        // ---- Keyboard shortcuts ----------------------------------------------------
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (RenameKey(keyData)) return true;   // 1.0.5: the rename box's Enter / Esc / Tab first (MainFormRename.cs)
            if (CustomFilterKey(keyData)) return true;   // 1.0.8: the filter editor's Esc / Enter / Tab (MainFormCustomFilters.cs)
            // 2.4.1: Space installs the selected options, exactly like a click on the footer's Install.
            if (keyData == Keys.Space && SpaceInstallsHere())
            {
                const int WM_KEYDOWN = 0x0100;
                bool repeat = ((long)msg.LParam & 0x40000000L) != 0;   // previous key state: auto-repeat
                if (msg.Msg == WM_KEYDOWN && !repeat) PressInstall();
                return true;
            }
            ShortcutInfo shortcut = Shortcuts.Find(keyData);
            if (shortcut != null && RunShortcut(shortcut.Action)) return true;
            return base.ProcessCmdKey(ref msg, keyData);
        }
        // Where Space means Install: the Mod library page, focus in the mod list, the options tree, the merged
        // files or any other non-input part of the page. Not in text fields, combo boxes, buttons and check boxes
        // (Space presses or toggles them), the Filters panel (Space selects there), the INSTALL AS switch, toasts,
        // or any other page. Dialogs are their own windows and never reach this.
        internal bool SpaceInstallsHere()
        {
            if (currentPage != libraryPage || !libraryPage.Visible) return false;
            Control focused = FocusedControl();
            if (focused == null) return true;
            if (filterPanel != null && filterPanel.Visible && filterPanel.Contains(focused)) return false;
            if (toastHost != null && toastHost.Contains(focused)) return false;
            if (!libraryPage.Contains(focused)) return false;
            return !(focused is TextBoxBase || focused is ComboBox || focused is ButtonBase || focused is SegmentedToggle || focused is ListControl || focused is UpDownBase || focused is TrackBar);
        }
        internal Control FocusOverride;   // tests: hidden windows cannot hold keyboard focus
        private Control FocusedControl()
        {
            if (FocusOverride != null) return FocusOverride;
            Control c = ActiveControl;
            while (c is ContainerControl && ((ContainerControl)c).ActiveControl != null) c = ((ContainerControl)c).ActiveControl;
            return c;
        }
        internal int SpaceInstalls;   // tests: presses that reached the Install button
        // A disabled or hidden Install does nothing, like a click on it; never while a task runs.
        private void PressInstall()
        {
            if (installButton != null && !installButton.Enabled && !BatchVisible) StartReviewNow();   // 1.0.12: Space before the check ran starts it
            if (busy || installButton == null || !installButton.Enabled || !installButton.Visible || BatchVisible) return;   // 1.0.6: never under the queue
            SpaceInstalls++;
            installButton.PerformClick();
        }
        private PaddedTextField PageSearch() { return currentPage == libraryPage ? search : currentPage == installedPage ? installedSearch : null; }
        // Returns true when the key was consumed.
        internal bool RunShortcut(ShortcutAction action)
        {
            switch (action)
            {
                case ShortcutAction.SwitchMode:
                    // 1.0.1 (the user): Tab flips INSTALL AS in the Mod library (remembered like a click). Elsewhere, or
                    // while a PAK/tool mod locks it, Tab keeps moving the focus.
                    if (currentPage == libraryPage && BatchVisible) { if (busy || batchMode.Locked) return false; batchMode.Choose(1 - batchMode.SelectedIndex); return true; }   // 1.0.6: the queue's mode
                    if (currentPage != libraryPage || busy || modeToggle == null || modeToggle.Locked) return false;
                    SetInstallMode(IsReplaceMode ? InstallModes.Slot : InstallModes.Replace, true);
                    return true;
                case ShortcutAction.FocusSearch:
                {
                    PaddedTextField field = PageSearch(); if (field == null) return false;
                    field.Focus(); field.Editor.SelectAll(); return true;
                }
                case ShortcutAction.ToggleFilters:
                    if (currentPage != libraryPage) { ShowFilters(true, true); return true; }
                    ShowFilters(!filterPanel.Visible, true); return true;
                case ShortcutAction.ClearSearch:
                {
                    // Esc inside the open Filters panel closes it; elsewhere it clears the focused search.
                    if (filterPanel != null && filterPanel.Visible && filterPanel.ContainsFocus) { ShowFilters(false, false); return true; }
                    PaddedTextField field = search.ContainsFocus ? search : installedSearch.ContainsFocus ? installedSearch : historySearch != null && historySearch.ContainsFocus ? historySearch : null;
                    if (field == null || field.TextLength == 0) return false;
                    field.Clear(); if (field == search) FlushLibraryFilter(); else if (field == historySearch) FillHistory(); else FillInstalled(); return true;
                }
                case ShortcutAction.LibraryPage: ShowPage(libraryPage); return true;
                case ShortcutAction.InstalledPage: if (currentPage != installedPage) { ShowPage(installedPage); RefreshInstalled(); } return true;
                case ShortcutAction.AssistantPage: if (!assistantTabShown) return false; ShowAssistant(); return true;   // 2.4.1: inert while the tab is off
                case ShortcutAction.SettingsPage: ShowPage(settingsPage); LoadSettingsFields(); if (settingsAboutPanel != null && settingsAboutPanel.Visible) ShowSettingsTab(false); return true;   // 2.5.9: Settings… after About = General
                case ShortcutAction.HelpPage: ShowHelp(); return true;   // 1.0.11
                case ShortcutAction.RefreshLibraries: if (!busy) RefreshLibrary(true); return true;
                case ShortcutAction.OpenArchive: if (!busy) ChoosePackageFile(); return true;
                case ShortcutAction.OpenFolder: if (!busy) ChoosePackageFolder(); return true;
                case ShortcutAction.ToggleNames: if (currentPage != libraryPage) return false; SetLibraryFileNames(!libraryFileNames, true); return true;   // 1.0.6
            }
            return false;
        }

        private void OpenPath(string path)
        {
            try { Installer.OpenFolder(path); } catch (Exception e) { Error(e); }
        }

        private void OpenLibrarySource(LibraryEntry entry, bool revealInExplorer)
        {
            if (entry == null) return;
            try
            {
                string path = Path.GetFullPath(entry.SourcePath);
                if (!File.Exists(path) && !Directory.Exists(path)) throw new IOException("Mod not found: " + path);
                if (revealInExplorer)
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "/select," + ProcessUtil.Quote(path)) { UseShellExecute = true });
                else
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception e) { Error(e); }
        }

        private void Error(Exception exception)
        {
            Program.Log(exception);
            // 2.4.1: the source changed while installing (a race the stamp checks did not catch): nothing was installed;
            // reload it at once and say so, instead of asking for a Reload the user can't find.
            if (exception is SourceChangedException && package != null) { BeginInvoke((Action)delegate { ReloadOpenPackage(true); }); return; }
            SetPageStatus(currentPage, exception.Message, Palette.Red);
            if (screenshotFile != null) { Environment.ExitCode = 1; Close(); return; }
            // 2.4.1: errors are inline (the status strip, red): the cause and the next step, no popup.
        }

        [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
        // DrawToBitmap paints a classic, light frame regardless of the DWM title
        // bar. When Windows can render the composed window (PW_RENDERFULLCONTENT)
        // and its client pixels match the control rendering, use that image so
        // the saved capture shows the real dark caption.
        private Bitmap CaptureWindowImage() { return CaptureWindowImage(this); }
        private Bitmap CaptureWindowImage(Form form)
        {
            int Width = form.Width, Height = form.Height; Size ClientSize = form.ClientSize; IntPtr Handle = form.Handle; int Left = form.Left, Top = form.Top;
            Func<Point, Point> PointToScreen = form.PointToScreen;
            Bitmap reference = new Bitmap(Width, Height);
            form.DrawToBitmap(reference, new Rectangle(0, 0, Width, Height));
            string method = "DrawToBitmap";
            try
            {
                Bitmap composed = new Bitmap(Width, Height);
                bool printed;
                using (Graphics graphics = Graphics.FromImage(composed)) { IntPtr dc = graphics.GetHdc(); try { printed = PrintWindow(Handle, dc, 2); } finally { graphics.ReleaseHdc(dc); } }
                Point client = PointToScreen(Point.Empty); int dx = client.X - Left, dy = client.Y - Top;
                int matches = 0, samples = 0;
                if (printed)
                    for (int i = 1; i < 10; i++)
                        for (int j = 1; j < 10; j++)
                        {
                            int x = dx + ClientSize.Width * i / 10, y = dy + ClientSize.Height * j / 10;
                            if (x >= Width || y >= Height) continue;
                            samples++; Color a = composed.GetPixel(x, y), b = reference.GetPixel(x, y);
                            if (Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B) <= 12) matches++;
                        }
                // DrawToBitmap paints overlapping siblings (such as the drop overlay)
                // in reverse z-order, so a mismatch there is not proof of failure:
                // also accept a composed image that is clearly not blank.
                var distinct = new HashSet<int>();
                if (printed) for (int i = 0; i < 20; i++) for (int j = 0; j < 20; j++) distinct.Add(composed.GetPixel(Math.Min(Width - 1, dx + ClientSize.Width * i / 20), Math.Min(Height - 1, dy + ClientSize.Height * j / 20)).ToArgb());
                bool usable = printed && samples > 0 && (matches >= samples * 9 / 10 || (screenshotPage == "drop" && distinct.Count >= 3 && !distinct.All(c => (c & 0xFFFFFF) == 0)));
                if (usable) { reference.Dispose(); reference = composed; method = "PrintWindow(PW_RENDERFULLCONTENT)"; }
                else composed.Dispose();
                method += " · " + matches + "/" + samples + " client samples matched; " + distinct.Count + " distinct";
            }
            catch (Exception error) { Program.Log(error); }
            try { File.AppendAllText(Path.Combine(AppConfig.DataDir, "screenshot-capture.txt"), DateTime.Now.ToString("s") + " " + Path.GetFileName(screenshotFile) + ": " + method + Environment.NewLine); } catch { }
            return reference;
        }

        // Screenshot mode: a scripted conversation against a local mock and generated fixtures.
        private void RunAssistantDemo()
        {
            string root = Path.Combine(Path.GetDirectoryName(screenshotFile), "assistant-demo-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            ShowAssistantTabForScreenshot();
            using (AssistantDemo demo = AssistantDemo.Attach(EnsureAssistantPage(), root))
            {
                ShowAssistant();
                assistantPage.Send(AssistantDemo.Prompt);
                var clock = System.Diagnostics.Stopwatch.StartNew();
                while (assistantPage.Running && clock.ElapsedMilliseconds < 30000) { Application.DoEvents(); Thread.Sleep(15); }
                Application.DoEvents();
                if (assistantPage.ActionList.Items.Count > 0) assistantPage.ActionList.SelectedIndex = 0;
            }
        }

        private void CaptureScreenshot()
        {
            if (String.IsNullOrEmpty(screenshotFile)) return;
            BeginInvoke((MethodInvoker)delegate
            {
                try
                {
                    // "min-<page>" renders the smallest supported client (1110x610 logical).
                    if (screenshotPage != null && screenshotPage.StartsWith("min-", StringComparison.OrdinalIgnoreCase))
                    {
                        screenshotPage = screenshotPage.Substring(4); MinimumSize = Size.Empty; WindowState = FormWindowState.Normal;
                        ClientSize = new Size(Px(1110), Px(610)); PerformLayout(); Application.DoEvents();
                    }
                    if (String.Equals(screenshotPage, "installed", StringComparison.OrdinalIgnoreCase))
                    {
                        ShowPage(installedPage);
                        if (installedList.Items.Count > 0) { installedList.Items[0].Selected = true; ShowInstalledSelection(); }
                    }
                    else if (String.Equals(screenshotPage, "installed-characters", StringComparison.OrdinalIgnoreCase))
                    {
                        // 1.0.1: the character picker's popup, saved beside the shot as <file>-characters.png (it is its own window).
                        ShowPage(installedPage); ToggleCharacterPopup(); Application.DoEvents();
                        Control popupPanel = ((ToolStripControlHost)characterPopup.Items[0]).Control;
                        using (var popupShot = new Bitmap(popupPanel.Width, popupPanel.Height)) { popupPanel.DrawToBitmap(popupShot, new Rectangle(Point.Empty, popupPanel.Size)); popupShot.Save(Path.ChangeExtension(screenshotFile, null) + "-characters.png", System.Drawing.Imaging.ImageFormat.Png); }
                        characterPopup.Close();
                    }
                    else if (String.Equals(screenshotPage, "merged", StringComparison.OrdinalIgnoreCase)) { ShowDetailTab(true); }
                    else if (screenshotPage != null && screenshotPage.StartsWith("search=", StringComparison.OrdinalIgnoreCase)) { search.Text = screenshotPage.Substring(7); FlushLibraryFilter(); ApplyFilters(); if (modList.Items.Count > 0) modList.EnsureVisible(0); }
                    else if (String.Equals(screenshotPage, "settings", StringComparison.OrdinalIgnoreCase)) { ShowPage(settingsPage); LoadSettingsFields(); if (settingsLibraries.Items.Count > 0) settingsLibraries.SelectedIndex = 0; }
                    else if (String.Equals(screenshotPage, "about", StringComparison.OrdinalIgnoreCase)) { ShowPage(settingsPage); LoadSettingsFields(); ShowAboutTab(); }   // 1.0.11
                    else if (String.Equals(screenshotPage, "settings-key", StringComparison.OrdinalIgnoreCase))
                    {
                        // Renders the Claude section with a fake saved key: only its last four characters appear.
                        ShowAssistantTabForScreenshot(); assistantKeys.Save(AssistantDemo.DemoKey);
                        ShowPage(settingsPage); LoadSettingsFields(); if (settingsLibraries.Items.Count > 0) settingsLibraries.SelectedIndex = 0;
                    }
                    else if (String.Equals(screenshotPage, "installed-replace", StringComparison.OrdinalIgnoreCase))
                    {
                        ShowPage(installedPage);
                        foreach (ListViewItem row in installedList.Items) if (row.Tag is GameModRow) { row.Selected = true; break; }
                        ShowInstalledSelection();
                    }
                    else if (String.Equals(screenshotPage, "settings-replace", StringComparison.OrdinalIgnoreCase)) { ShowPage(settingsPage); LoadSettingsFields(); ShowSettingsTab(true); }
                    else if (String.Equals(screenshotPage, "settings-about", StringComparison.OrdinalIgnoreCase)) { ShowPage(settingsPage); LoadSettingsFields(); ShowAboutTab(); }
                    else if (String.Equals(screenshotPage, "assistant", StringComparison.OrdinalIgnoreCase)) ShowPage(EnsureAssistantPage());
                    else if (String.Equals(screenshotPage, "installed-conflicts", StringComparison.OrdinalIgnoreCase))
                    {
                        ShowPage(installedPage);
                        foreach (ListViewItem row in installedList.Items) { var g = row.Tag as GameModRow; if (g != null && g.HasConflicts) { row.Selected = true; break; } }
                        ShowInstalledSelection(); ShowSharedFilesDetails();
                    }
                    else if (String.Equals(screenshotPage, "assistant", StringComparison.OrdinalIgnoreCase)) { ShowAssistantTabForScreenshot(); ShowAssistant(); }
                    else if (String.Equals(screenshotPage, "assistant-demo", StringComparison.OrdinalIgnoreCase)) RunAssistantDemo();
                    componentTree.ExpandAll();
                    if (componentTree.Nodes.Count > 0) componentTree.TopNode = componentTree.Nodes[0];
                    if (String.Equals(screenshotPage, "drop", StringComparison.OrdinalIgnoreCase))
                    {
                        var data = new DataObject(DataFormats.FileDrop, new[] { String.IsNullOrWhiteSpace(screenshotSource) ? AppConfig.DataDir : screenshotSource });
                        PresentDrop(data);
                    }
                    // 2.3.0: "filters" opens the Filters panel.
                    if (String.Equals(screenshotPage, "filters", StringComparison.OrdinalIgnoreCase)) ShowFilters(true, false);
                    PerformLayout(); Update();
                    string folder = Path.GetDirectoryName(screenshotFile); if (!String.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
                    if (String.Equals(screenshotPage, "dropdown", StringComparison.OrdinalIgnoreCase))
                    {
                        ((DarkCombo)targetCharacter).CaptureDropdown(screenshotFile); Close(); return;
                    }
                    if (String.Equals(screenshotPage, "report", StringComparison.OrdinalIgnoreCase))
                    {
                        using (Form report = CreateCompositionReport())
                        {
                            report.Opacity = 0; report.Show(); report.PerformLayout(); Application.DoEvents();
                            using (Bitmap bitmap = new Bitmap(report.Width, report.Height)) { report.DrawToBitmap(bitmap, new Rectangle(0, 0, report.Width, report.Height)); bitmap.Save(screenshotFile, ImageFormat.Png); }
                            report.Close();
                        }
                        Close(); return;
                    }
                    if (screenshotAvailableSize)
                    {
                        using (Bitmap bitmap = CaptureWindowImage()) bitmap.Save(screenshotFile, ImageFormat.Png);
                    }
                    else
                    {
                        using (Panel canvas = new Panel { Size = new Size(Px(1460), Px(920)), BackColor = Palette.Background, ForeColor = Palette.Text })
                        {
                            Control[] children = Controls.Cast<Control>().ToArray();
                            foreach (Control child in children) Controls.Remove(child);
                            canvas.Controls.AddRange(children); Controls.Add(canvas); canvas.CreateControl(); canvas.PerformLayout(); canvas.Update();
                            componentTree.ExpandAll();
                            if (componentTree.Nodes.Count > 0) componentTree.TopNode = componentTree.Nodes[0];
                            using (Bitmap bitmap = new Bitmap(canvas.Width, canvas.Height)) { canvas.DrawToBitmap(bitmap, new Rectangle(0, 0, canvas.Width, canvas.Height)); bitmap.Save(screenshotFile, ImageFormat.Png); }
                        }
                    }
                    Close();
                }
                catch (Exception e) { Program.Log(e); Environment.ExitCode = 1; Close(); }
            });
        }
    }
}















