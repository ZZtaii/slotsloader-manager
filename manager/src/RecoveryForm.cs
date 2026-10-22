using System;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace SlotsInstaller
{
    internal sealed class RecoveryForm : Form
    {
        private readonly string costumeRoot;
        private readonly ReplaceEngine engine;   // removed replacements (2.0.1); null lists extra outfits only
        private readonly ListView copies;
        private readonly Label status;
        private readonly Label selectedPath;
        private readonly TableLayoutPanel tableHeader;
        private readonly ImageList rowImages;
        private readonly FlatButton restore, delete, refresh;
        private bool busy, mutation, closeRequested, fittingColumns;
        private CancellationTokenSource cancellation;
        internal bool Changed { get; private set; }

        internal static bool ShowRecovery(IWin32Window owner,string costumeRoot) { return ShowRecovery(owner,costumeRoot,null); }
        internal static bool ShowRecovery(IWin32Window owner,string costumeRoot,ReplaceEngine engine)
        {
            using (var form=new RecoveryForm(costumeRoot,engine)) { form.ShowDialog(owner); return form.Changed; }
        }
        internal RecoveryForm(string costumeRoot) : this(costumeRoot,null) { }
        internal RecoveryForm(string costumeRoot,ReplaceEngine engine)
        {
            AppBranding.Apply(this);
            this.costumeRoot=costumeRoot; this.engine=engine;
            SuspendLayout();
            Text="Recover removed outfits";
            AutoScaleDimensions=new SizeF(96F,96F); AutoScaleMode=AutoScaleMode.Dpi;
            StartPosition=FormStartPosition.CenterParent; ClientSize=new Size(860,520); MinimumSize=new Size(730,430);
            BackColor=Palette.Background; ForeColor=Palette.Text; Font=Palette.Normal;
            MinimizeBox=false; ShowInTaskbar=false;
            var layout=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=1,RowCount=5,Padding=new Padding(18),BackColor=Palette.Background};
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute,60)); layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute,54)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute,30)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute,46));
            var explanation=new Label {Dock=DockStyle.Fill,Text="Restore an outfit or file to where it was; existing outfits are never overwritten. A replacement returns with its list position and enabled state, a mod an external mod manager installed with its installed.ini entry; an original game file replaces the other tool's file kept over it.",ForeColor=Palette.Muted,Margin=new Padding(0,0,0,10)};
            layout.Controls.Add(explanation,0,0);
            rowImages=new ImageList {ImageSize=new Size(1,32),ColorDepth=ColorDepth.Depth32Bit}; rowImages.Images.Add(new Bitmap(1,32));
            copies=new BufferedModList {LogicalRowHeight=32,RepaintAfterScroll=true,Dock=DockStyle.Fill,View=View.Details,HeaderStyle=ColumnHeaderStyle.None,OwnerDraw=true,SmallImageList=rowImages,FullRowSelect=true,HideSelection=false,MultiSelect=false,BackColor=Palette.Surface,ForeColor=Palette.Text,BorderStyle=BorderStyle.None,Font=Palette.Normal,Margin=new Padding(0)};
            copies.Columns.Add("Name",235); copies.Columns.Add("Character",120); copies.Columns.Add("Type",80); copies.Columns.Add("Date",160); copies.Columns.Add("Size",100);
            copies.DrawItem+=delegate(object sender,DrawListViewItemEventArgs e) { if (copies.View!=View.Details) e.DrawDefault=true; };
            copies.DrawSubItem+=delegate(object sender,DrawListViewSubItemEventArgs e)
            {
                using (var brush=new SolidBrush(e.Item.Selected ? Palette.Selected : Palette.Surface)) e.Graphics.FillRectangle(brush,e.Bounds);
                Rectangle bounds=e.Bounds; bounds.Inflate(-DpiMetrics.Px(this,5),0);
                TextRenderer.DrawText(e.Graphics,e.SubItem.Text,Palette.Normal,bounds,Palette.Text,TextFormatFlags.Left|TextFormatFlags.VerticalCenter|TextFormatFlags.SingleLine|TextFormatFlags.EndEllipsis|TextFormatFlags.NoPrefix);
            };
            copies.SelectedIndexChanged+=delegate { UpdateSelection(); };
            copies.Resize+=delegate { ResizeColumns(); };
            var table=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=1,RowCount=2,Margin=new Padding(0)};
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            table.RowStyles.Add(new RowStyle(SizeType.Absolute,32)); table.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            tableHeader=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=5,RowCount=1,BackColor=Palette.Raised,Margin=new Padding(0)};
            for (int i=0;i<5;i++)
            {
                tableHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,copies.Columns[i].Width));
                tableHeader.Controls.Add(new Label {Dock=DockStyle.Fill,Text=copies.Columns[i].Text,ForeColor=Palette.Muted,Font=Palette.Small,TextAlign=ContentAlignment.MiddleLeft,Margin=new Padding(5,0,0,0)},i,0);
            }
            table.Controls.Add(tableHeader,0,0); table.Controls.Add(copies,0,1); layout.Controls.Add(table,0,1);
            selectedPath=new Label {Dock=DockStyle.Fill,AutoEllipsis=true,BackColor=Palette.Background,ForeColor=Palette.Muted,Font=Palette.Small,Margin=new Padding(0,9,0,0),TabStop=false};
            layout.Controls.Add(selectedPath,0,2);
            status=new Label {Dock=DockStyle.Fill,ForeColor=Palette.Muted,AutoEllipsis=true,Margin=new Padding(0,4,0,0)}; layout.Controls.Add(status,0,3);
            var actions=new FlowLayoutPanel {Dock=DockStyle.Fill,FlowDirection=FlowDirection.RightToLeft,WrapContents=false,Margin=new Padding(0),Padding=new Padding(0,5,0,0)};
            var close=new FlatButton("Close",false) {Width=92,Height=36,Margin=new Padding(7,0,0,0)};
            close.Click+=delegate { Close(); }; CancelButton=close;
            delete=new FlatButton("Delete permanently",false) {Width=180,Height=36,Margin=new Padding(7,0,0,0)};
            delete.Click+=delegate { DeleteSelected(); };
            restore=new FlatButton("Restore",true) {Width=150,Height=36,Margin=new Padding(7,0,0,0)};
            restore.Click+=delegate { RestoreSelected(); };
            refresh=new FlatButton("Refresh",false) {Width=100,Height=36,Margin=new Padding(7,0,0,0)};
            refresh.Click+=delegate { RefreshCopies(); };
            actions.Controls.Add(close); actions.Controls.Add(delete); actions.Controls.Add(restore); actions.Controls.Add(refresh);
            layout.Controls.Add(actions,0,4); Controls.Add(layout);
            DarkTheme.Apply(this);
            Shown+=delegate { ResizeColumns(); RefreshCopies(); };
            DpiChanged+=delegate { BeginInvoke((MethodInvoker)delegate { ResizeColumns(); }); };
            Disposed+=delegate { rowImages.Dispose(); };
            ResumeLayout(true); PerformAutoScale(); UpdateSelection();
        }
        private void ResizeColumns()
        {
            if (copies.Columns.Count!=5 || fittingColumns) return;
            fittingColumns=true;
            try
            {
                // Reset the native horizontal extent before fitting a narrower
                // window. ListView can retain its old scroll range on resize.
                copies.Columns[0].Width=1;
                copies.Columns[1].Width=DpiMetrics.Px(this,112); copies.Columns[2].Width=DpiMetrics.Px(this,100);
                copies.Columns[3].Width=DpiMetrics.Px(this,150); copies.Columns[4].Width=DpiMetrics.Px(this,88);
                int others=copies.Columns.Cast<ColumnHeader>().Skip(1).Sum(c=>c.Width);
                copies.Columns[0].Width=Math.Max(DpiMetrics.Px(this,170),copies.ClientSize.Width-others-SystemInformation.VerticalScrollBarWidth-DpiMetrics.Px(this,4));
                if (tableHeader!=null) for (int i=0;i<5;i++) tableHeader.ColumnStyles[i].Width=copies.Columns[i].Width;
                rowImages.ImageSize=new Size(DpiMetrics.Px(this,1),DpiMetrics.Px(this,32));
            }
            finally { fittingColumns=false; }
        }
        private RecoveryEntry Selected()
        {
            return copies.SelectedItems.Count==1 ? copies.SelectedItems[0].Tag as RecoveryEntry : null;
        }
        private void UpdateSelection()
        {
            RecoveryEntry selected=Selected(); restore.Enabled=!busy && selected!=null; delete.Enabled=!busy && selected!=null; refresh.Enabled=!busy;
            selectedPath.Text=selected==null ? "Select an item to restore or delete it." : selected.Folder;
        }
        private void RefreshCopies()
        {
            if (busy) return;
            Run("Reading copies…",false,delegate { return ListAll(); },delegate(RecoveryListing listing)
            {
                copies.BeginUpdate();
                try
                {
                    copies.Items.Clear();
                    foreach (RecoveryEntry entry in listing.Entries)
                    {
                        var item=new ListViewItem(new[]{entry.Name,entry.Character,entry.TypeLabel,entry.InstalledUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),Utilities.FriendlyBytes(entry.Bytes)}) {Tag=entry};
                        copies.Items.Add(item);
                    }
                }
                finally { copies.EndUpdate(); ResizeColumns(); }
                long total=listing.Entries.Sum(e=>e.Bytes);
                status.ForeColor=Palette.Muted;
                status.Text=(listing.Entries.Count==1 ? "1 copy" : listing.Entries.Count+" copies")+" · "+Utilities.FriendlyBytes(total)+(listing.Warnings.Count==0 ? "" : " · "+listing.Warnings.Count+" unreadable folders left untouched");
                UpdateSelection();
            });
        }
        private void RestoreSelected()
        {
            RecoveryEntry entry=Selected(); if (busy || entry==null) return;
            Run("Restoring…",true,delegate { if (entry.Replacement!=null) { ReplaceMod mod; engine.RestoreRemoved(entry.Folder,out mod); return mod.Name; } if (entry.Fluffy!=null) return engine.RestoreFluffyRemoved(entry.Folder); if (entry.Released!=null) return engine.RestoreReleasedOriginal(entry.Released.Path); if (entry.Foreign!=null) return engine.RestoreForeignCopy(entry.Foreign.Copy); return RecoveryStore.Restore(costumeRoot,entry); },delegate(string destination) { Changed=true; if (!closeRequested) RefreshCopies(); });
        }
        private void DeleteSelected()
        {
            RecoveryEntry entry=Selected(); if (busy || entry==null) return;
            string question="Permanently delete this copy?\n\n"+(String.IsNullOrEmpty(entry.Character) ? "" : entry.Character+": ")+entry.Name+"\n"+Utilities.FriendlyBytes(entry.Bytes)+"\n\n"+entry.Folder+"\n\nThis can't be undone.";
            if (ThemedMessageBox.Show(this,question,"Delete copy",MessageBoxButtons.YesNo,MessageBoxIcon.Warning,MessageBoxDefaultButton.Button2)!=DialogResult.Yes) return;
            Run("Deleting…",true,delegate { if (entry.Replacement!=null) engine.DeleteRemoved(entry.Folder); else if (entry.Fluffy!=null) engine.DeleteFluffyRemoved(entry.Folder); else if (entry.Released!=null) engine.DropReleased(entry.Released.Path); else if (entry.Foreign!=null) engine.DeleteForeignCopy(entry.Foreign.Copy); else RecoveryStore.Delete(costumeRoot,entry); return true; },delegate(bool done) { Changed=true; if (!closeRequested) RefreshCopies(); });
        }
        private void Run<T>(string message,bool changing,Func<T> action,Action<T> done)
        {
            busy=true; mutation=changing; status.Text=message; ((BufferedModList)copies).InputLocked=true; UpdateSelection();
            cancellation=new CancellationTokenSource(); CancellationToken token=cancellation.Token;
            var worker=new BackgroundWorker();
            worker.DoWork+=delegate(object sender,DoWorkEventArgs e) { using (OperationContext.Begin(token,null)) e.Result=action(); };
            worker.RunWorkerCompleted+=delegate(object sender,RunWorkerCompletedEventArgs e)
            {
                busy=false; mutation=false; cancellation.Dispose(); cancellation=null; ((BufferedModList)copies).InputLocked=false; UpdateSelection();
                if (e.Error==null) done((T)e.Result);
                else if (!(e.Error is OperationCanceledException))
                {
                    status.Text=e.Error.Message; status.ForeColor=Palette.Red;   // 2.4.1: inline, no popup
                }
                worker.Dispose(); if (closeRequested) Close();
            };
            worker.RunWorkerAsync();
        }
        // Extra-outfit backups/trash plus replacements removed with Uninstall, newest first.
        private RecoveryListing ListAll()
        {
            RecoveryListing listing;
            try { listing=RecoveryStore.List(costumeRoot); }
            catch (System.IO.IOException ex) { if (engine==null) throw; listing=new RecoveryListing(); listing.Warnings.Add(ex.Message); }
            if (engine!=null)
                foreach (RemovedReplacement r in engine.ListRemoved())
                {
                    DateTime removed; DateTime.TryParse(r.Record.RemovedUtc,System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.RoundtripKind,out removed);
                    string area=(r.Record.Mod.Affects ?? "").Split(',')[0].Trim();
                    listing.Entries.Add(new RecoveryEntry {Folder=r.Folder,Name=r.Name,Character=ReplacePlanner.ShortArea(area.Length==0 ? "Game files" : area),Id=r.Record.Mod.Id,Kind=RecoveryKind.Trash,Replacement=r,InstalledUtc=removed.ToUniversalTime(),Bytes=r.Bytes,FileCount=r.Record.Mod.Files.Count,ManifestText=""});
                }
            if (engine!=null) AddGameFileEntries(engine,listing);
            listing.Entries.Sort((a,b)=>b.InstalledUtc.CompareTo(a.InstalledUtc));
            return listing;
        }
        // 2.2.0: Fluffy mods uninstalled here, originals behind files kept from other tools, other tools' kept files.
        internal static void AddGameFileEntries(ReplaceEngine engine,RecoveryListing listing)
        {
            Func<string,DateTime> when=delegate(string utc) { DateTime t; DateTime.TryParse(utc,System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.RoundtripKind,out t); return t.ToUniversalTime(); };
            try
            {
                foreach (FluffyRemoved f in engine.ListFluffyRemoved())
                    listing.Entries.Add(new RecoveryEntry {Folder=f.Folder,Name=f.Name,Character="External mod manager · "+ReplacePlanner.N(f.Record.Files.Count,"file"),Id=f.Record.Section,Kind=RecoveryKind.Trash,Fluffy=f,InstalledUtc=when(f.Record.RemovedUtc),Bytes=f.Bytes,FileCount=f.Record.Files.Count,ManifestText=""});
                ReplaceState state=engine.LoadState();
                foreach (ReleasedFile r in state.Released.Where(x=>x.Original!=null))
                    listing.Entries.Add(new RecoveryEntry {Folder=r.Original.Backup,Name="Original "+r.Path,Character="kept: "+r.Changer,Id=r.Path,Kind=RecoveryKind.Backup,Released=r,InstalledUtc=when(r.ReleasedUtc),Bytes=r.Original.Size,FileCount=1,ManifestText=""});
                foreach (ForeignCopy c in state.ForeignCopies)
                    listing.Entries.Add(new RecoveryEntry {Folder=c.Copy,Name=c.Path,Character=c.Changer,Id=c.Copy,Kind=RecoveryKind.Trash,Foreign=c,InstalledUtc=when(c.KeptUtc),Bytes=c.Size,FileCount=1,ManifestText=""});
            }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; listing.Warnings.Add(ex.Message); }
        }
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (busy)
            {
                e.Cancel=true; closeRequested=true;
                if (!mutation && cancellation!=null) cancellation.Cancel();
                status.Text=mutation ? "Finishing this action before closing…" : "Stopping the scan before closing…";
            }
            base.OnFormClosing(e);
        }
    }
}
