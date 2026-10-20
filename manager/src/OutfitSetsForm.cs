using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace SlotsInstaller
{
    internal sealed class OutfitSetsForm : Form
    {
        private readonly string costumeRoot;
        private readonly Func<ReplaceEngine> engineFactory;
        private readonly Func<ReplaceEngine,bool,bool> ensureLooseLoader;
        private readonly Func<IEnumerable<LibraryEntry>> libraryEntries;   // 1.0.10: the Mod library's list (read on the UI thread at Save)
        private readonly BufferedModList sets;
        private readonly PaddedTextField nameInput;
        private readonly SegmentedChoice scope,restoreScope;   // what to save; what to restore (a preset holding both kinds)
        private readonly FlatButton save,restore,delete,refresh,close,cancel;
        private readonly NoteLabel note;
        private readonly Label status;
        private readonly TextBox details;
        private readonly ProgressBar progress;
        private readonly ImageList rows;
        private bool busy,closeRequested,fittingList;
        private List<string> restoreNotes=new List<string>(); private string restoreNotesId;   // 1.0.8: the last restore's notes
        private CancellationTokenSource cancellation;
        internal bool Changed {get;private set;}
        // 2.4.1: a restore installed replacements (the main window refreshes its replacement list).
        internal bool ReplacementsChanged {get;private set;}
        internal bool OutfitsChanged {get;private set;}

        internal const string NoteText="Saves a list of what's enabled. Restore reinstalls it from your libraries; anything without a library source is copied.";   // 1.0.8 (the user): a list, not copies
        internal const string HelpText="Restore adds what's missing and skips what's already installed. Restart SF6 after.";

        internal static bool ShowPresets(IWin32Window owner,string costumeRoot) {return ShowPresets(owner,costumeRoot,null,null);}
        // 2.4.1: engine = the main window's replace engine (Fluffy roots included); ensureLooseLoader = its loose file
        // loader question (MainForm.EnsureLooseLoader). Without them the window builds an engine from the settings
        // and refuses natives replacements while the loader is off unless "Don't ask again" was chosen.
        internal static bool ShowPresets(IWin32Window owner,string costumeRoot,Func<ReplaceEngine> engine,Func<ReplaceEngine,bool,bool> ensureLooseLoader)
        {
            using(var form=new OutfitSetsForm(costumeRoot,engine,ensureLooseLoader)) {form.ShowDialog(owner);return form.Changed;}
        }
        internal OutfitSetsForm(string costumeRoot):this(costumeRoot,null,null) {}
        internal OutfitSetsForm(string costumeRoot,Func<ReplaceEngine> engineFactory,Func<ReplaceEngine,bool,bool> ensureLooseLoader):this(costumeRoot,engineFactory,ensureLooseLoader,null) {}
        // 1.0.10: library = the Mod library's listed mods, so a manual drop identical to a library archive is listed, not copied.
        internal OutfitSetsForm(string costumeRoot,Func<ReplaceEngine> engineFactory,Func<ReplaceEngine,bool,bool> ensureLooseLoader,Func<IEnumerable<LibraryEntry>> library)
        {
            this.costumeRoot=costumeRoot;this.engineFactory=engineFactory;this.ensureLooseLoader=ensureLooseLoader;libraryEntries=library;SuspendLayout();
            AutoScaleDimensions=new SizeF(96F,96F);AutoScaleMode=AutoScaleMode.Dpi;
            Text="Outfit presets";ClientSize=new Size(840,600);MinimumSize=new Size(720,500);StartPosition=FormStartPosition.CenterParent;
            Font=Palette.Normal;BackColor=Palette.Background;ForeColor=Palette.Text;ShowInTaskbar=false;MinimizeBox=false;AppBranding.Apply(this);
            var layout=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=1,RowCount=6,Padding=new Padding(18),Margin=Padding.Empty};
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            // note · name row (+ gap to its result, the list) · list · details · status · buttons
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute,32+Rhythm.AfterAction));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute,96+Rhythm.Related));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute,28));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute,32+Rhythm.Related));
            note=new NoteLabel {Dock=DockStyle.Top,Text=NoteText,ForeColor=Palette.Muted,Font=Palette.Small,Margin=new Padding(0,0,0,Rhythm.Related),MinLines=1};
            layout.Controls.Add(note,0,0);
            var naming=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=3,RowCount=1,Margin=new Padding(0,0,0,Rhythm.AfterAction)};
            naming.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));naming.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,330));naming.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,80));
            naming.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            nameInput=new PaddedTextField {Dock=DockStyle.Fill,Margin=new Padding(0,0,8,0),PlaceholderText="Preset name",AccessibleName="Preset name"};
            scope=new SegmentedChoice(PresetScopes.Labels) {Dock=DockStyle.Fill,Margin=new Padding(0,0,8,0),AccessibleName="Include"};
            scope.Descriptions[0]="Enabled extra outfits in costume_mods.";
            scope.Descriptions[1]="Enabled replacements and tool mods, with their order and options. Slotsloader is left out (updates manage it).";
            scope.Descriptions[2]="Extra outfits and replacements.";
            scope.SelectedIndex=(int)PresetScopes.LastSaveScope();
            scope.SelectionChanged+=delegate {PresetScopes.RememberSaveScope((PresetScope)scope.SelectedIndex);};
            save=ActionButton("Save",80,SavePreset);save.Dock=DockStyle.Fill;save.Margin=Padding.Empty;
            naming.Controls.Add(nameInput,0,0);naming.Controls.Add(scope,1,0);naming.Controls.Add(save,2,0);layout.Controls.Add(naming,0,1);
            rows=new ImageList {ImageSize=new Size(1,54),ColorDepth=ColorDepth.Depth32Bit};rows.Images.Add(new Bitmap(1,54));
            sets=new BufferedModList {Dock=DockStyle.Fill,LogicalRowHeight=54,RepaintAfterScroll=true,BackColor=Palette.Surface,ForeColor=Palette.Text,BorderStyle=BorderStyle.None,View=View.Details,HeaderStyle=ColumnHeaderStyle.None,FullRowSelect=true,HideSelection=false,MultiSelect=false,OwnerDraw=true,SmallImageList=rows,Margin=Padding.Empty,AccessibleName="Presets"};
            sets.Columns.Add("Presets",700);sets.DrawItem+=delegate(object sender,DrawListViewItemEventArgs e){if(sets.View!=View.Details)e.DrawDefault=true;};
            sets.DrawSubItem+=DrawSet;sets.SelectedIndexChanged+=delegate {UpdateSelection();};sets.Resize+=delegate {FitList();};layout.Controls.Add(sets,0,2);
            details=new TextBox {Dock=DockStyle.Fill,AutoSize=false,Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical,BorderStyle=BorderStyle.None,BackColor=Palette.Background,ForeColor=Palette.Muted,Font=Palette.Small,Margin=new Padding(0,Rhythm.Related,0,0),AccessibleName="Preset contents"};
            NativeDpiFont.Attach(details);layout.Controls.Add(details,0,3);
            var statusRow=new Panel {Dock=DockStyle.Fill,Margin=Padding.Empty};
            status=new Label {UseMnemonic=false,Dock=DockStyle.Fill,ForeColor=Palette.Muted,AutoEllipsis=true,TextAlign=ContentAlignment.MiddleLeft,Font=Palette.Small};
            progress=new ProgressBar {Dock=DockStyle.Right,Width=125,Visible=false};
            cancel=ActionButton("Cancel",78,delegate {if(cancellation!=null){cancellation.Cancel();cancel.Enabled=false;status.Text="Cancelling after the current step…";}});cancel.Dock=DockStyle.Right;cancel.Visible=false;
            statusRow.Controls.Add(status);statusRow.Controls.Add(progress);statusRow.Controls.Add(cancel);layout.Controls.Add(statusRow,0,4);
            var actions=new FlowLayoutPanel {Dock=DockStyle.Fill,FlowDirection=FlowDirection.RightToLeft,WrapContents=false,Margin=Padding.Empty,Padding=new Padding(0,Rhythm.Related,0,0)};
            // Widths fit the row at the 720 minimum with the restore choice shown (72+72+80+330+80 + 5×8 ≤ 684).
            close=ActionButton("Close",72,Close);delete=ActionButton("Delete",72,DeleteSet);restore=ActionButton("Restore",80,RestoreSet);refresh=ActionButton("Refresh",80,delegate {RefreshSets(null);});
            // Shown left of Restore only for a preset holding both kinds (Both by default for each selection).
            restoreScope=new SegmentedChoice(PresetScopes.Labels) {Width=330,Height=32,Margin=new Padding(8,0,0,0),AccessibleName="Restore",Visible=false,SelectedIndex=(int)PresetScope.Both};
            restoreScope.Descriptions[0]="Restore the extra outfits only.";restoreScope.Descriptions[1]="Restore the replacements only.";restoreScope.Descriptions[2]="Restore everything the preset holds.";
            foreach(Control action in new Control[]{close,delete,restore,restoreScope,refresh})actions.Controls.Add(action);
            sets.ItemSelectionChanged+=delegate {restoreScope.SelectedIndex=(int)PresetScope.Both;};
            layout.Controls.Add(actions,0,5);Controls.Add(layout);CancelButton=close;
            nameInput.TextChanged+=delegate {UpdateSelection();};
            Shown+=delegate {FitList();RefreshSets(null);};
            DpiChanged+=delegate {BeginInvoke((MethodInvoker)delegate {ApplyDpiMetrics();});};
            FormClosing+=delegate(object sender,FormClosingEventArgs e) {if(busy){e.Cancel=true;closeRequested=true;if(cancellation!=null)cancellation.Cancel();status.Text="Finishing the current step, then closing…";}};
            Disposed+=delegate {rows.Dispose();};DarkTheme.Apply(this);ResumeLayout(true);PerformAutoScale();ApplyDpiMetrics();UpdateSelection();
        }
        // Compact buttons (height 32, small text), as in Settings.
        private static FlatButton ActionButton(string text,int width,Action action)
        {
            var button=new FlatButton(text,false) {Width=width,Height=32,Font=Palette.Small,Margin=new Padding(8,0,0,0),AccessibleName=text};button.Click+=delegate {action();};return button;
        }
        private void FitList()
        {
            if(fittingList)return;fittingList=true;
            try {Size rowSize=new Size(DpiMetrics.Px(this,1),DpiMetrics.Px(this,54));if(rows.ImageSize!=rowSize)rows.ImageSize=rowSize;sets.FitSingleColumn(true);}
            finally {fittingList=false;}
        }
        private void ApplyDpiMetrics()
        {
            Rectangle available=Screen.FromControl(this).WorkingArea;
            MinimumSize=new Size(Math.Min(DpiMetrics.Px(this,720),available.Width),Math.Min(DpiMetrics.Px(this,500),available.Height));
            FitList();nameInput.LayoutEditor();
        }

        // "12 extra outfits · 3 replacements" (an empty kind is left out; an empty preset says 0 extra outfits).
        internal static string Counts(OutfitSetInfo e)
        {
            var parts=new List<string>();
            if(e.OutfitCount>0 || e.ReplacementCount==0)parts.Add(ReplacePlanner.N(e.OutfitCount,"extra outfit"));
            if(e.ReplacementCount>0)parts.Add(ReplacePlanner.N(e.ReplacementCount,"replacement"));
            return String.Join(" · ",parts);
        }
        // The list's second line: "12 extra outfits · 3 replacements · 2026-10-15 14:02"; 1.0.8: a list preset says how
        // many entries it copied ("2 copied · 140 MB"), an older one "full copy · 4.3 GB".
        internal static string Subtitle(OutfitSetInfo e)
        {
            return (e.Kind==OutfitSetKind.Removed ? "Removed selection · " : "")+Counts(e)+" · "+SizeText(e)+e.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        }
        internal static string SizeText(OutfitSetInfo e)
        {
            if(e.Pointers) return e.Copied>0 ? e.Copied+" copied · "+Utilities.FriendlyBytes(e.Bytes)+" · " : "";
            return (e.Kind==OutfitSetKind.Preset ? "full copy · " : "")+Utilities.FriendlyBytes(e.Bytes)+" · ";
        }
        private void DrawSet(object sender,DrawListViewSubItemEventArgs e)
        {
            var entry=e.Item.Tag as OutfitSetInfo;if(entry==null)return;
            using(var fill=new SolidBrush(e.Item.Selected ? Palette.Selected : Palette.Surface))e.Graphics.FillRectangle(fill,e.Bounds);
            int inset=DpiMetrics.Px(this,10);var title=new Rectangle(e.Bounds.Left+inset,e.Bounds.Top+DpiMetrics.Px(this,5),Math.Max(1,e.Bounds.Width-inset*2),DpiMetrics.Px(this,23));
            TextRenderer.DrawText(e.Graphics,entry.Name,Palette.Strong,title,Palette.Text,TextFormatFlags.SingleLine|TextFormatFlags.EndEllipsis|TextFormatFlags.NoPrefix);
            title.Y=e.Bounds.Top+DpiMetrics.Px(this,29);title.Height=DpiMetrics.Px(this,20);
            TextRenderer.DrawText(e.Graphics,Subtitle(entry),Palette.Small,title,Palette.Muted,TextFormatFlags.SingleLine|TextFormatFlags.EndEllipsis|TextFormatFlags.NoPrefix);
        }
        internal static string Contents(OutfitSetInfo e)
        {
            var lines=new List<string> {e.Name+" · "+Counts(e)+(e.Pointers && e.Copied==0 ? "" : " · "+Utilities.FriendlyBytes(e.Bytes))};
            if(e.Pointers && e.Copies.Count>0)lines.AddRange(CopiedLines(e));
            bool both=e.OutfitCount>0 && e.ReplacementCount>0;
            if(both)lines.Add("Extra outfits");
            lines.AddRange(e.Outfits ?? new List<string>());
            if(e.ReplacementCount>0)
            {
                lines.Add("Replacements"+(String.IsNullOrEmpty(e.ReplacementMethod) ? "" : " · saved with the "+ReplaceMethods.Label(e.ReplacementMethod)));
                for(int i=0;i<e.Replacements.Count;i++)lines.Add((i+1)+". "+e.Replacements[i]);
            }
            return String.Join("\r\n",lines);
        }
        // 1.0.10 (the user: say what a list preset copies): "2 items have no library mod and are copied (51 MB):", then
        // "Juri/Summer.zip · 40 MB · manual drop" or "Cammy Alt · 11 MB · not found at <path>" for each.
        internal static List<string> CopiedLines(OutfitSetInfo e)
        {
            int n=e.Copies.Count;
            var lines=new List<string> {(n==1 ? "1 item has no library mod and is copied (" : n+" items have no library mod and are copied (")+Utilities.FriendlyBytes(e.Copies.Sum(c=>c.Bytes))+"):"};
            foreach(OutfitSetCopy c in e.Copies)lines.Add(c.Name+" · "+Utilities.FriendlyBytes(c.Bytes)+(String.IsNullOrEmpty(c.Reason) ? "" : " · "+c.Reason));
            return lines;
        }
        // The status after Save: "Saved “Weekend” · 12 extra outfits · 3 replacements · 3 copied (51 MB), listed below."
        internal static string SaveSummary(OutfitSetInfo saved)
        {
            string text="Saved “"+saved.Name+"” · "+Counts(saved);
            if(saved.Pointers && saved.Copies.Count>0)text+=" · "+saved.Copies.Count+" copied ("+Utilities.FriendlyBytes(saved.Copies.Sum(c=>c.Bytes))+"), listed below";
            return text+".";
        }
        private OutfitSetInfo Selected() {return sets.SelectedItems.Count==1 ? sets.SelectedItems[0].Tag as OutfitSetInfo : null;}
        private void UpdateSelection()
        {
            var selected=Selected();save.Enabled=!busy && !String.IsNullOrWhiteSpace(nameInput.Text);restore.Enabled=delete.Enabled=!busy && selected!=null;refresh.Enabled=!busy;nameInput.Enabled=!busy;scope.Locked=busy;
            restoreScope.Visible=selected!=null && Holds(selected)==PresetScope.Both;restoreScope.Locked=busy;
            sets.InputLocked=busy;
            details.Text=selected==null ? HelpText : (restoreNotesId==selected.Id && restoreNotes.Count>0 ? "Last restore\r\n"+String.Join("\r\n",restoreNotes)+"\r\n\r\n" : "")+Contents(selected);
            details.Select(0,0);
        }
        private void RefreshSets(string completion) {RefreshSets(completion,null);}
        // `selectId`: the preset to select afterwards (1.0.10: the one just saved, so its details show what was copied).
        private void RefreshSets(string completion,string selectId)
        {
            if(closeRequested)return;
            Run("Reading presets…",delegate {return OutfitSetStore.List(costumeRoot);},delegate(OutfitSetListing listing)
            {
                string selectedId=selectId ?? (Selected()==null ? null : Selected().Id);
                sets.BeginUpdate();try {sets.Items.Clear();foreach(var entry in listing.Entries.OrderByDescending(entry=>entry.CreatedUtc)) {var row=new ListViewItem(entry.Name) {Tag=entry};sets.Items.Add(row);row.Selected=entry.Id==selectedId;}}
                finally {sets.EndUpdate();FitList();}
                status.Text=completion ?? (listing.Entries.Count==1 ? "1 preset" : listing.Entries.Count+" presets")+" · "+Utilities.FriendlyBytes(listing.Entries.Sum(entry=>entry.Bytes));
                if(listing.Warnings.Count>0)status.Text+=" · "+listing.Warnings.Count+" unreadable copies left untouched";
                UpdateSelection();
            });
        }
        // The main window's engine, or one built from the settings for this game folder.
        private ReplaceEngine Engine()
        {
            if(engineFactory!=null)return engineFactory();
            string game=Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(costumeRoot).TrimEnd(Path.DirectorySeparatorChar)));
            AppConfig config=null;
            try {config=AppConfig.Load();} catch(Exception ex) {if(ex is OutOfMemoryException)throw;Program.Log(ex);}
            if(config==null || String.IsNullOrWhiteSpace(config.GameRoot) || !String.Equals(Path.GetFullPath(config.GameRoot).TrimEnd(Path.DirectorySeparatorChar),game,StringComparison.OrdinalIgnoreCase))
                config=new AppConfig {GameRoot=game,Libraries=new List<string>()};
            if(config.Libraries==null)config.Libraries=new List<string>();
            return ReplaceEngine.ForConfig(config);
        }
        internal PresetScope SaveScope {get {return (PresetScope)scope.SelectedIndex;}}
        private void SavePreset()
        {
            if(busy || String.IsNullOrWhiteSpace(nameInput.Text))return;string name=nameInput.Text.Trim();PresetScope chosen=SaveScope;
            ReplaceEngine engine=null;
            if(PresetScopes.Replacements(chosen)) {try {engine=Engine();} catch(Exception ex) {if(ex is OutOfMemoryException)throw;ShowError(ex.Message);return;}}
            // 1.0.10: the library's archives as listed now (no disk scan), for manual drops identical to one of them.
            List<LibraryArchive> library=null;
            if(libraryEntries!=null) {try {library=OutfitSetStore.LibraryArchives(libraryEntries());} catch(Exception ex) {if(ex is OutOfMemoryException)throw;Program.Log(ex);}}
            Run("Saving…",delegate {return OutfitSetStore.CreatePreset(costumeRoot,name,chosen,engine,library);},delegate(OutfitSetInfo saved) {nameInput.Clear();RefreshSets(SaveSummary(saved),saved.Id);});
        }

        // The part of a preset to restore: the inline choice when it holds both kinds, else what it holds.
        internal static PresetScope[] RestoreChoices(PresetScope holds)
        {
            return holds==PresetScope.Both ? new[]{PresetScope.ExtraOutfits,PresetScope.Replacements,PresetScope.Both} : new[]{holds};
        }
        private static PresetScope Holds(OutfitSetInfo e) {return e.Kind==OutfitSetKind.Removed ? PresetScope.ExtraOutfits : e.Scope;}
        internal PresetScope RestoreScope
        {
            get {var selected=Selected();if(selected==null)return PresetScope.ExtraOutfits;PresetScope holds=Holds(selected);return holds==PresetScope.Both ? (PresetScope)restoreScope.SelectedIndex : holds;}
        }
        // 2.4.1: no confirmation (the click is the consent): restore acts and reports inline. The only question left
        // is MainForm's loose file loader one, when natives replacements would be ignored without it.
        private void RestoreSet()
        {
            var selected=Selected();if(busy || selected==null)return;
            PresetScope chosen=RestoreScope;
            ReplaceEngine engine=null;
            if(PresetScopes.Replacements(chosen)) {try {engine=Engine();} catch(Exception ex) {if(ex is OutOfMemoryException)throw;ShowError(ex.Message);return;}}
            Run("Restoring…",delegate {return PresetScopes.Replacements(chosen) ? OutfitSetStore.Preview(costumeRoot,selected,chosen,engine) : null;},delegate(OutfitSetPreview preview)
            {
                if(preview!=null && preview.AddReplacements.Count>0 && preview.InstallsNatives && !LooseLoaderReady(engine)) {if(status.ForeColor!=Palette.Red)status.Text="Not restored. Nothing changed.";return;}
                Run("Restoring…",delegate {return OutfitSetStore.Restore(costumeRoot,selected,chosen,engine);},delegate(OutfitSetRestoreResult result)
                {
                    Changed=true;if(result.ReplacementsRestored>0)ReplacementsChanged=true;if(result.Restored>0)OutfitsChanged=true;
                    restoreNotes=result.Notes;restoreNotesId=selected.Id;   // 1.0.8: shown above the preset's contents
                    RefreshSets(RestoreSummary(result,chosen));
                });
            });
        }
        // MainForm's question (Turn on / Not now / Don't ask again); without it, natives replacements wait for the loader.
        private bool LooseLoaderReady(ReplaceEngine engine)
        {
            if(ensureLooseLoader!=null)return ensureLooseLoader(engine,true);
            if(!engine.LooseLoaderOffFor(true) || engine.Settings.SkipLooseLoaderPrompt)return true;
            ShowError("REFramework's loose file loader is off, so SF6 would ignore these replacements. Turn it on in Settings, or choose the PAK method there.");
            return false;
        }
        internal static string RestoreSummary(OutfitSetRestoreResult r,PresetScope chosen)
        {
            var parts=new List<string>();
            if(PresetScopes.Outfits(chosen))parts.Add(ReplacePlanner.N(r.Restored,"extra outfit"));
            if(PresetScopes.Replacements(chosen))parts.Add(ReplacePlanner.N(r.ReplacementsRestored,"replacement"));
            string text="Restored "+String.Join(" · ",parts);
            int skipped=r.Skipped+r.ReplacementsSkipped;
            if(skipped>0)text+=" · "+skipped+" identical skipped";
            if(r.ReplacementConflicts.Count>0)text+=" · skipped "+String.Join(", ",r.ReplacementConflicts.Select(n=>"“"+n+"”"))+" (a different version is installed)";
            if(r.Notes.Count>0)text+=" · "+ReplacePlanner.N(r.Notes.Count,"note")+" below";
            text+=". Restart SF6.";
            if(!String.IsNullOrEmpty(r.ReplaceMessage))text+=" "+r.ReplaceMessage;
            return text;
        }
        private void DeleteSet()
        {
            var selected=Selected();if(busy || selected==null)return;
            var names=(selected.Outfits ?? new List<string>()).Concat(selected.Replacements ?? new List<string>());
            if(!Confirm(this,"Delete saved copy","Permanently delete “"+selected.Name+"” ("+Utilities.FriendlyBytes(selected.Bytes)+")? This can't be undone. What's installed stays unchanged.",names,"Delete",true))return;
            Run("Deleting…",delegate {OutfitSetStore.Delete(costumeRoot,selected);return true;},delegate(bool done) {RefreshSets("Deleted “"+selected.Name+"”.");});
        }
        // 2.4.1: errors are reported in the status line (no message box); the window can always continue.
        private void ShowError(string message) {status.Text=message;status.ForeColor=Palette.Red;}
        private void Run<T>(string message,Func<T> action,Action<T> completed)
        {
            if(busy)return;busy=true;status.Text=message;status.ForeColor=Palette.Muted;progress.Visible=cancel.Visible=true;progress.Style=ProgressBarStyle.Marquee;cancel.Enabled=true;UpdateSelection();
            var token=new CancellationTokenSource();cancellation=token;var worker=new BackgroundWorker {WorkerReportsProgress=true};
            worker.DoWork+=delegate(object sender,DoWorkEventArgs e) {try {using(OperationContext.Begin(token.Token,value=>worker.ReportProgress(0,value)))e.Result=action();}catch(OperationCanceledException){e.Cancel=true;}};
            worker.ProgressChanged+=delegate(object sender,ProgressChangedEventArgs e)
            {
                if(IsDisposed)return;var value=(OperationProgress)e.UserState;cancel.Enabled=value.CanCancel && !token.IsCancellationRequested;
                progress.Style=value.Total>0 ? ProgressBarStyle.Continuous : ProgressBarStyle.Marquee;
                if(value.Total>0)progress.Value=(int)Math.Max(0,Math.Min(100,value.Completed*100.0/value.Total));
                status.Text=token.IsCancellationRequested && value.CanCancel ? "Cancelling after the current step…" : value.Stage+(value.Total>0 ? " · "+progress.Value+"%" : "…");
            };
            worker.RunWorkerCompleted+=delegate(object sender,RunWorkerCompletedEventArgs e)
            {
                busy=false;cancellation=null;token.Dispose();worker.Dispose();if(IsDisposed)return;
                progress.Visible=cancel.Visible=false;UpdateSelection();
                if(e.Error!=null)ShowError(e.Error.Message);
                else if(e.Cancelled)status.Text="Cancelled. Nothing changed.";
                else completed((T)e.Result);
                if(closeRequested)Close();
            };
            worker.RunWorkerAsync();
        }
        private static bool Confirm(IWin32Window owner,string title,string explanation,IEnumerable<string> names,string action,bool permanent)
        {
            using(var dialog=CreateConfirmation(title,explanation,names,action,permanent))return dialog.ShowDialog(owner)==DialogResult.OK;
        }
        internal static Form CreateConfirmation(string title,string explanation,IEnumerable<string> names,string action,bool permanent)
        {
            var dialog=new Form();
            dialog.SuspendLayout();dialog.AutoScaleDimensions=new SizeF(96F,96F);dialog.AutoScaleMode=AutoScaleMode.Dpi;dialog.Text=title;dialog.ClientSize=new Size(600,420);dialog.MinimumSize=new Size(500,330);dialog.StartPosition=FormStartPosition.CenterParent;dialog.ShowInTaskbar=false;dialog.BackColor=Palette.Surface;dialog.ForeColor=Palette.Text;dialog.Font=Palette.Normal;dialog.Padding=new Padding(18);AppBranding.Apply(dialog);
            var summary=new TextBox {Name="confirmationSummary",AccessibleName="Action details",Dock=DockStyle.Top,Height=108,AutoSize=false,Multiline=true,ReadOnly=true,BorderStyle=BorderStyle.None,BackColor=Palette.Surface,Text=explanation,ForeColor=permanent ? Palette.Red : Palette.Text,ScrollBars=ScrollBars.Vertical,Font=Palette.Normal};NativeDpiFont.Attach(summary);
            var list=new TextBox {Name="confirmationOutfits",AccessibleName="Selected outfits",Dock=DockStyle.Fill,AutoSize=false,Multiline=true,ReadOnly=true,BorderStyle=BorderStyle.None,BackColor=Palette.Surface,ForeColor=Palette.Muted,ScrollBars=ScrollBars.Vertical,Font=Palette.Normal,Text=String.Join(Environment.NewLine,names ?? Enumerable.Empty<string>())};NativeDpiFont.Attach(list);
            var actions=new FlowLayoutPanel {Dock=DockStyle.Bottom,Height=46,FlowDirection=FlowDirection.RightToLeft,WrapContents=false,Padding=new Padding(0,10,0,0)};
            var cancelButton=ActionButton("Cancel",100,delegate {dialog.DialogResult=DialogResult.Cancel;});var apply=ActionButton(action,140,delegate {dialog.DialogResult=DialogResult.OK;});
            actions.Controls.Add(cancelButton);actions.Controls.Add(apply);dialog.Controls.Add(list);dialog.Controls.Add(summary);dialog.Controls.Add(actions);dialog.CancelButton=cancelButton;
            Action sizeSummary=delegate
            {
                int availableWidth=Math.Max(1,dialog.ClientSize.Width-dialog.Padding.Horizontal-SystemInformation.VerticalScrollBarWidth);
                int measured;using(Graphics graphics=dialog.CreateGraphics())measured=TextRenderer.MeasureText(graphics,summary.Text,summary.Font,new Size(availableWidth,0),TextFormatFlags.WordBreak|TextFormatFlags.NoPrefix|TextFormatFlags.TextBoxControl).Height+DpiMetrics.Px(dialog,8);
                summary.Height=Math.Min(Math.Max(DpiMetrics.Px(dialog,68),measured),Math.Max(DpiMetrics.Px(dialog,68),(dialog.ClientSize.Height-dialog.Padding.Vertical)/3));
            };
            DarkTheme.Apply(dialog);dialog.ResumeLayout(true);dialog.PerformAutoScale();dialog.MinimumSize=new Size(DpiMetrics.Px(dialog,500),DpiMetrics.Px(dialog,330));
            dialog.Resize+=delegate {sizeSummary();};dialog.Shown+=delegate {sizeSummary();summary.Select(0,0);list.Select(0,0);dialog.ActiveControl=cancelButton;cancelButton.Focus();};dialog.DpiChanged+=delegate {dialog.MinimumSize=new Size(DpiMetrics.Px(dialog,500),DpiMetrics.Px(dialog,330));sizeSummary();};sizeSummary();
            return dialog;
        }
    }
}
