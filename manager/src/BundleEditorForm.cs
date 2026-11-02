using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SlotsInstaller
{
    internal sealed class BundleEditorForm : Form
    {
        private readonly ModPackage package;
        private readonly string stamp;
        private readonly bool zip;      // 1.0.16: a RAR/7Z layout is saved in the Data folder, not in the archive
        private readonly string kind;   // "ZIP", "RAR" or "7Z"
        private readonly TreeView tree = new TreeView();
        private readonly PaddedTextField name = new PaddedTextField();
        private readonly ComboBox parent = MainForm.Combo(), rule = MainForm.Combo();
        private readonly Label hint = new EditorLabel(), status = new EditorLabel();
        private readonly FlatButton save = new FlatButton("Save ZIP", true) {Font=Palette.Small};
        private readonly FlatButton remove, moveUp, moveDown;
        private readonly Label suggestionNote=Caption("");
        private readonly Label noteHeading=Caption("LAYOUT GUIDE");
        private readonly FlatButton details;
        private string assistantNote;
        private readonly FlatButton undo, refine;
        private List<BundleOption> beforeSuggestion;
        private bool wasRestore;
        private CancellationTokenSource refining;
        private readonly List<Control> editing = new List<Control>();   // disabled while the Assistant refines (Close and Cancel stay)
        private string savedLayout;   // the saved layout, to tell a real change from an edit that was undone
        private Panel confirmPanel;   // "Discard unsaved changes?" in the footer
        private TreeNode dropTarget;
        private int dropPlace;        // where a drag lands on dropTarget: -1 above it, 0 inside it, 1 below it
        internal Func<string,ClaudeClient> ClientFactory = key=>new ClaudeClient(key);
        private bool filling, dirty, restore, saving;
        private readonly List<BundleOption> author;
        private readonly HashSet<string> groupIds;
        private readonly List<Action> dpiLayout=new List<Action>();

        internal BundleEditorForm(ModPackage package, string stamp, bool assistantShown, AssistantKeyStore keys, AssistantSettings settings)
        {
            this.package=package; this.stamp=stamp;
            zip=BundleLayout.IsZip(package.SourcePath); kind=Path.GetExtension(package.SourcePath).TrimStart('.').ToUpperInvariant();
            if(!zip) save.Text="Save layout";
            author = package.AuthorOptions ?? BundleLayout.Options(package);
            groupIds = new HashSet<string>(package.Components.Where(c=>c.IsDummy).Select(c=>c.Id), StringComparer.OrdinalIgnoreCase);
            // Scale the complete control tree together, including the fixed toolbar and field rows.
            SuspendLayout();
            AutoScaleDimensions=new SizeF(96,96); AutoScaleMode=AutoScaleMode.Dpi;
            Text="Edit bundle · " + package.Name; Font=Palette.Normal; BackColor=Palette.Surface; ForeColor=Palette.Text;
            ClientSize=new Size(1040,720); Padding=new Padding(16);
            StartPosition=FormStartPosition.CenterParent; ShowInTaskbar=false; AppBranding.Apply(this);
            var grid=new TableLayoutPanel {Dock=DockStyle.Fill, ColumnCount=2, RowCount=4};
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,56)); grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,44));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute,44)); grid.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute,42)); grid.RowStyles.Add(new RowStyle(SizeType.Absolute,48));
            var suggestions=new FlowLayoutPanel {Dock=DockStyle.Fill,WrapContents=false};
            var suggest=Action("Suggest layout",140,delegate {
                TrySuggestLocal();
            });
            suggestions.Controls.Add(suggest); editing.Add(suggest);
            bool hasKey=keys.HasConfiguredKey;
            if(assistantShown || hasKey)
            {
                // While a refinement runs this button cancels it; the rest of the editor waits.
                refine=Action("Refine with Assistant",180,delegate {
                    if(refining!=null) {refining.Cancel(); return;}
                    if(!hasKey) {SetNote("ASSISTANT","Add your Claude API key in Settings to use refinement."); return;}
                    Refine(keys,settings);
                });
                suggestions.Controls.Add(refine);
            }
            undo=Action("Undo suggestion",150,UndoSuggestion); undo.Visible=false;
            suggestions.Controls.Add(undo); editing.Add(undo);
            var toolbar=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=2,RowCount=1,Margin=Padding.Empty};
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            toolbar.Controls.Add(suggestions,0,0);
            grid.Controls.Add(toolbar,0,0); grid.SetColumnSpan(toolbar,2);
            tree.Dock=DockStyle.Fill; tree.BackColor=Palette.Background; tree.ForeColor=Palette.Text; tree.HideSelection=false;
            tree.BorderStyle=BorderStyle.None; tree.ShowLines=false; tree.AllowDrop=true; tree.AccessibleName="Bundle options";
            tree.DrawMode=TreeViewDrawMode.OwnerDrawAll; tree.FullRowSelect=true;
            tree.SizeChanged+=delegate {tree.Invalidate();};
            name.SizeChanged+=delegate {name.Invalidate(true);};
            tree.DrawNode+=DrawOption;
            tree.EnabledChanged+=delegate {tree.Invalidate();};
            tree.MouseDown+=delegate(object sender,MouseEventArgs e) {
                if(e.Button!=MouseButtons.Left || e.Clicks!=1) return;
                var hit=tree.HitTest(e.Location);
                var node=hit.Node;
                if(node!=null && node.Nodes.Count>0 && hit.Location!=TreeViewHitTestLocations.PlusMinus &&
                    e.X>=node.Bounds.Left-tree.Indent && e.X<node.Bounds.Left) node.Toggle();
            };
            tree.HandleCreated+=delegate {tree.ItemHeight=DpiMetrics.Px(tree,28);};
            tree.DpiChangedAfterParent+=delegate {tree.ItemHeight=DpiMetrics.Px(tree,28);};
            tree.AfterSelect+=delegate { tree.Invalidate(); ShowSelected(); };
            tree.ItemDrag+=delegate(object sender,ItemDragEventArgs e) { tree.DoDragDrop(e.Item,DragDropEffects.Move); };
            // The top or bottom quarter of a row places the option above or below it; the middle moves it inside.
            // Below the last row: the end of the top level. The target row shows where it lands.
            tree.DragOver+=delegate(object sender,DragEventArgs e) {
                var source=e.Data.GetData(typeof(TreeNode)) as TreeNode;
                TreeNode target=null; int place=0;
                bool ok=source!=null && source.TreeView==tree && DropAt(source,tree.PointToClient(new Point(e.X,e.Y)),out target,out place);
                e.Effect=ok ? DragDropEffects.Move : DragDropEffects.None;
                ShowDrop(ok ? target : null,place);
            };
            tree.DragLeave+=delegate { ShowDrop(null,0); };
            tree.DragDrop+=delegate(object sender,DragEventArgs e) {
                var source=e.Data.GetData(typeof(TreeNode)) as TreeNode;
                TreeNode target=null; int place=0;
                bool ok=source!=null && source.TreeView==tree && DropAt(source,tree.PointToClient(new Point(e.X,e.Y)),out target,out place);
                ShowDrop(null,0);
                if(!ok) return;
                if(place==0) MoveNode(source,target);
                else PlaceBeside(source,target,place);
            };
            grid.Controls.Add(tree,0,1); editing.Add(tree);
            var fields=new TableLayoutPanel { Dock=DockStyle.Fill, Padding=new Padding(10,0,0,0), ColumnCount=1, RowCount=8 };
            foreach(int h in new[]{26,38,26,38,26,38}) fields.RowStyles.Add(new RowStyle(SizeType.Absolute,h));
            fields.RowStyles.Add(new RowStyle(SizeType.Absolute,62)); fields.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            AddField(fields,"Option or group name",name,0); AddField(fields,"Move under",parent,2); AddField(fields,"Selection rule",rule,4);
            parent.DropDownStyle=rule.DropDownStyle=ComboBoxStyle.DropDownList;
            name.Editor.MaxLength=200; name.TextChanged+=delegate {
                if(filling || tree.SelectedNode==null) return;
                Item(tree.SelectedNode).Name=name.Text; LabelNode(tree.SelectedNode); Changed();
            };
            parent.SelectionChangeCommitted+=delegate { var target=parent.SelectedItem as ParentItem; if(target!=null) MoveNode(tree.SelectedNode,target.Node); };
            rule.SelectionChangeCommitted+=delegate {
                Item(tree.SelectedNode).Rule=(string)rule.SelectedItem; LabelNode(tree.SelectedNode); Explain(); Changed();
            };
            hint.Dock=DockStyle.Fill; hint.ForeColor=Palette.Muted; hint.Padding=new Padding(0,8,0,0);
            fields.Controls.Add(hint,0,6); grid.Controls.Add(fields,1,1);
            foreach(Control field in new Control[]{name,parent,rule}) editing.Add(field);
            suggestionNote.AutoSize=true; suggestionNote.Dock=DockStyle.Top; suggestionNote.TextAlign=ContentAlignment.TopLeft;
            suggestionNote.Text="Drag an option onto a group to move it inside, or near a row's top or bottom edge to place it above or below. Suggest layout can get you started.";
            suggestionNote.Font=Palette.Small;
            var notes=new Panel {Dock=DockStyle.Fill,AutoScroll=true};
            var noteBody=new TableLayoutPanel {Dock=DockStyle.Top,AutoSize=true,ColumnCount=1,RowCount=3,Margin=Padding.Empty};
            noteBody.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            noteHeading.AutoSize=true; noteHeading.Font=Palette.Tiny; noteHeading.ForeColor=Palette.Muted;
            noteBody.Controls.Add(noteHeading,0,0); noteBody.Controls.Add(suggestionNote,0,1);
            details=Action("Show details",112,delegate {
                bool show=details.Text=="Show details";
                details.Text=show ? "Hide details" : "Show details";
                suggestionNote.Text=show ? assistantNote : assistantOverview;
            }); details.Visible=false; noteBody.Controls.Add(details,0,2); notes.Controls.Add(noteBody);
            notes.SizeChanged+=delegate {suggestionNote.MaximumSize=new Size(Math.Max(1,notes.ClientSize.Width-DpiMetrics.Px(this,18)),0);};
            fields.Controls.Add(notes,0,7);
            var actions=new FlowLayoutPanel { Dock=DockStyle.Fill, WrapContents=false };
            actions.Controls.Add(Action("New group",100,NewGroup));
            remove=Action("Ungroup",90,Ungroup); actions.Controls.Add(remove);
            moveUp=Action("Up",54,delegate { Reorder(-1); });
            moveDown=Action("Down",66,delegate { Reorder(1); });
            actions.Controls.Add(moveUp); actions.Controls.Add(moveDown);
            grid.Controls.Add(actions,0,2); editing.Add(actions);
            var reset=Action("Restore author layout",180,delegate {
                LoadTree(author); Changed(true);
                SetNote("AUTHOR LAYOUT","The original groups and selection rules are ready to review.");
            });
            reset.Anchor=AnchorStyles.Top|AnchorStyles.Right;
            reset.Margin=new Padding(6,6,3,0);
            toolbar.Controls.Add(reset,1,0); editing.Add(reset);
            var footer=new Panel { Dock=DockStyle.Fill };
            status.Dock=DockStyle.Fill; status.TextAlign=ContentAlignment.MiddleLeft; status.ForeColor=Palette.Muted;
            status.Text=IdleText();
            var cancel=Action("Close",95,Close); cancel.Dock=DockStyle.Right;
            save.Width=125; save.Dock=DockStyle.Right; save.Click+=delegate { SaveLayout(); }; save.Enabled=false;
            footer.Controls.Add(status); footer.Controls.Add(cancel);
            var gap=new Panel {Dock=DockStyle.Right,Width=8};
            footer.Controls.Add(gap); footer.Controls.Add(save);
            grid.Controls.Add(footer,0,3); grid.SetColumnSpan(footer,2); Controls.Add(grid);
            CancelButton=cancel;
            var confirm=confirmPanel=new Panel {Dock=DockStyle.Fill,BackColor=Palette.Sidebar,Visible=false};
            var question=Caption("Discard unsaved changes?"); question.Font=Palette.Strong;
            var discard=Action("Discard changes",150,delegate {dirty=false; DialogResult=DialogResult.Cancel; Close();});
            discard.Dock=DockStyle.Right; discard.Danger=true;
            var keep=Action("Keep editing",130,delegate {
                confirm.Visible=false; foreach(Control c in footer.Controls) if(c!=confirm) c.Visible=true;
                foreach(Control section in grid.Controls) section.Enabled=true;
                CancelButton=cancel; AcceptButton=null; tree.Focus();
            });
            keep.Dock=DockStyle.Right;
            confirm.Controls.Add(question); confirm.Controls.Add(discard);
            confirm.Controls.Add(new Panel {Dock=DockStyle.Right,Width=DpiMetrics.Px(this,8)}); confirm.Controls.Add(keep);
            footer.Controls.Add(confirm); confirm.BringToFront();
            FormClosing+=delegate(object sender, FormClosingEventArgs e) {
                if(refining!=null) refining.Cancel();
                if(saving) { e.Cancel=true; return; }
                if(dirty && DialogResult!=DialogResult.OK) {
                    e.Cancel=true; DialogResult=DialogResult.None;
                    foreach(Control section in grid.Controls) if(section!=footer) section.Enabled=false;
                    foreach(Control c in footer.Controls) if(c!=confirm) c.Visible=false;
                    confirm.Visible=true; CancelButton=keep; keep.DialogResult=DialogResult.None; AcceptButton=keep; keep.Focus();
                }
            };
            dpiLayout.Add(delegate {
                grid.RowStyles[0].Height=DpiMetrics.Px(this,44);
                grid.RowStyles[2].Height=DpiMetrics.Px(this,42);
                grid.RowStyles[3].Height=DpiMetrics.Px(this,48);
                for(int i=0;i<6;i++) fields.RowStyles[i].Height=DpiMetrics.Px(this,i%2==0 ? 26 : 38);
                fields.RowStyles[6].Height=DpiMetrics.Px(this,62);
                Padding=new Padding(DpiMetrics.Px(this,16));
                gap.Width=DpiMetrics.Px(this,8); save.Width=DpiMetrics.Px(this,125);
                tree.Indent=DpiMetrics.Px(this,22); tree.ItemHeight=DpiMetrics.Px(this,28); name.LayoutEditor();
            });
            DpiChanged+=delegate {BeginInvoke((MethodInvoker)ApplyEditorMetrics);};
            DarkTheme.Apply(this); LoadTree(BundleLayout.Options(package));
            savedLayout=Signature(Draft());
            if(package.LayoutError!=null)
            {
                // The ZIP's saved layout was ignored on reading: saving this draft replaces it.
                SetNote("SAVED LAYOUT IGNORED",package.LayoutError);
                UpdateDirty();
            }
            ResumeLayout(true); PerformAutoScale();
        }

        protected override void OnLoad(EventArgs e)
        {
            ApplyEditorMetrics();
            var area=Screen.FromControl(Owner ?? this).WorkingArea;
            Size desired=SizeFromClientSize(new Size(DpiMetrics.Px(this,1040),DpiMetrics.Px(this,720)));
            Size=new Size(Math.Min(desired.Width,area.Width*95/100),Math.Min(desired.Height,area.Height*95/100));
            var anchor=Owner==null ? area : Owner.Bounds;
            Location=new Point(Math.Max(area.Left,Math.Min(area.Right-Width,anchor.Left+(anchor.Width-Width)/2)),
                Math.Max(area.Top,Math.Min(area.Bottom-Height,anchor.Top+(anchor.Height-Height)/2)));
            base.OnLoad(e);
        }

        private void ApplyEditorMetrics()
        {
            var area=Screen.FromControl(this).WorkingArea;
            MinimumSize=new Size(Math.Min(area.Width,DpiMetrics.Px(this,740)),Math.Min(area.Height,DpiMetrics.Px(this,505)));
            foreach(var apply in dpiLayout) apply();
        }

        private FlatButton Action(string text,int width,Action click)
        {
            var button=new FlatButton(text,false) {Width=width,Height=32,Font=Palette.Small};
            dpiLayout.Add(delegate {button.Width=DpiMetrics.Px(this,width); if(button.Dock==DockStyle.None) button.Height=DpiMetrics.Px(this,32);});
            button.Click+=delegate {click();}; return button;
        }
        // Windows draws a disabled Label using system colors, even on the dark editor surface.
        private sealed class EditorLabel : Label
        {
            protected override void OnPaint(PaintEventArgs e)
            {
                if(Enabled) {base.OnPaint(e); return;}
                var area=Rectangle.FromLTRB(Padding.Left,Padding.Top,Width-Padding.Right,Height-Padding.Bottom);
                var flags=TextFormatFlags.WordBreak|TextFormatFlags.NoPrefix;
                if(TextAlign==ContentAlignment.MiddleLeft) flags|=TextFormatFlags.VerticalCenter;
                TextRenderer.DrawText(e.Graphics,Text,Font,area,Palette.Muted,flags);
            }
        }
        private static Label Caption(string text) {return new EditorLabel {Text=text,Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft,ForeColor=Palette.Text};}
        private static void AddField(TableLayoutPanel panel,string title,Control field,int row)
        {
            var label=Caption(title); panel.Controls.Add(label,0,row); field.Dock=DockStyle.Fill; field.AccessibleName=title; panel.Controls.Add(field,0,row+1);
        }
        private static BundleOption Item(TreeNode node) {return (BundleOption)node.Tag;}
        private void DrawOption(object sender,DrawTreeNodeEventArgs e)
        {
            var item=Item(e.Node);
            if(item==null || e.Bounds.Height<=0) return;
            var row=new Rectangle(0,e.Bounds.Top,tree.ClientSize.Width,e.Bounds.Height);
            bool selected=e.Node==tree.SelectedNode;
            using(var brush=new SolidBrush(selected ? Palette.Selected : tree.BackColor)) e.Graphics.FillRectangle(brush,row);
            int x=e.Node.Bounds.Left;
            if(e.Node.Nodes.Count>0)
                Glyphs.Chevron(e.Graphics,new PointF(x-tree.Indent/2f,row.Top+row.Height/2f),DpiMetrics.Px(tree,4),
                    e.Node.IsExpanded ? ChevronDirection.Down : ChevronDirection.Right,Palette.Muted,1.6f*tree.DeviceDpi/96f);
            var flags=TextFormatFlags.NoPrefix|TextFormatFlags.VerticalCenter|TextFormatFlags.SingleLine|TextFormatFlags.EndEllipsis;
            int right=row.Right-DpiMetrics.Px(tree,8);
            if(item.Rule!="Original")
            {
                int width=TextRenderer.MeasureText(e.Graphics,item.Rule,Palette.Small).Width+DpiMetrics.Px(tree,12);
                var tag=new Rectangle(right-width,row.Top,width,row.Height);
                TextRenderer.DrawText(e.Graphics,item.Rule,Palette.Small,tag,Palette.Muted,flags|TextFormatFlags.Right);
                right=tag.Left;
            }
            TextRenderer.DrawText(e.Graphics,String.IsNullOrWhiteSpace(item.Name) ? "(unnamed)" : item.Name,
                e.Node.Nodes.Count>0 ? Palette.Strong : tree.Font,new Rectangle(x,row.Top,Math.Max(1,right-x),row.Height),
                !tree.Enabled ? Palette.Muted : selected ? Palette.Accent : Palette.Text,flags);
            if(e.Node!=dropTarget) return;
            // Where a dragged option lands: a frame around the row for inside it, a line for above or below it.
            int inset=DpiMetrics.Px(tree,4);
            using(var pen=new Pen(Palette.Accent,Math.Max(2,DpiMetrics.Px(tree,2))))
            {
                if(dropPlace==0) e.Graphics.DrawRectangle(pen,new Rectangle(x-inset,row.Top+1,Math.Max(1,row.Right-inset-(x-inset)),row.Height-3));
                else { int y=dropPlace<0 ? row.Top+1 : row.Bottom-2; e.Graphics.DrawLine(pen,x-inset,y,row.Right-inset,y); }
            }
        }
        private bool DropAt(TreeNode source,Point point,out TreeNode target,out int place)
        {
            target=tree.GetNodeAt(point); place=0;
            if(target==null)
            {
                if(tree.Nodes.Count==0) return false;
                target=tree.Nodes[tree.Nodes.Count-1]; place=1;   // below the last row: the end of the top level
                return target!=source;
            }
            int band=Math.Max(1,target.Bounds.Height/4);
            place=point.Y<target.Bounds.Top+band ? -1 : point.Y>=target.Bounds.Bottom-band ? 1 : 0;
            if(place==0) return CanMove(source,target);
            for(var n=target;n!=null;n=n.Parent) if(n==source) return false;   // never beside its own child
            return true;
        }
        private void ShowDrop(TreeNode target,int place)
        {
            if(target==dropTarget && place==dropPlace) return;
            dropTarget=target; dropPlace=place; tree.Invalidate();
        }
        private void PlaceBeside(TreeNode node,TreeNode target,int place)
        {
            if(node==null || target==null || node==target) return;
            node.Remove();
            var siblings=target.Parent==null ? tree.Nodes : target.Parent.Nodes;
            siblings.Insert(target.Index+(place>0 ? 1 : 0),node);
            tree.SelectedNode=node; ShowSelected(); Changed();
        }
        private bool Group(BundleOption item) {return groupIds.Contains(item.Id) || item.Id.StartsWith(BundleLayout.GroupPrefix,StringComparison.Ordinal);}
        private static IEnumerable<TreeNode> Walk(TreeNodeCollection nodes)
        {
            foreach(TreeNode node in nodes) { yield return node; foreach(var child in Walk(node.Nodes)) yield return child; }
        }
        private static bool CanMove(TreeNode source,TreeNode target)
        {
            if(source==null || source.Parent==target) return false;
            for(var n=target;n!=null;n=n.Parent) if(n==source) return false;
            return true;
        }
        private sealed class ParentItem
        {
            internal TreeNode Node;
            internal bool Ambiguous;
            public override string ToString() {
                if(Node==null) return "Top level";
                if(!Ambiguous) return Item(Node).Name;
                var path=new Stack<string>();
                for(var n=Node;n!=null;n=n.Parent) path.Push(Item(n).Name);
                return String.Join(" › ",path);
            }
        }
        private void LoadTree(List<BundleOption> items)
        {
            tree.BeginUpdate(); tree.Nodes.Clear();
            var nodes=new Dictionary<string,TreeNode>(StringComparer.OrdinalIgnoreCase);
            foreach(var item in items)
            {
                var node=new TreeNode { Tag=new BundleOption {Id=item.Id,Name=item.Name,Parent=item.Parent,Rule=item.Rule} };
                LabelNode(node); nodes.Add(item.Id,node);
            }
            foreach(var item in items) (item.Parent==null ? tree.Nodes : nodes[item.Parent].Nodes).Add(nodes[item.Id]);
            tree.ExpandAll(); tree.EndUpdate(); tree.SelectedNode=tree.Nodes.Count==0 ? null : tree.Nodes[0]; ShowSelected();
        }
        private void LabelNode(TreeNode node)
        {
            var item=Item(node); node.Text=(String.IsNullOrWhiteSpace(item.Name) ? "(unnamed)" : item.Name) +
                (item.Rule=="Original" ? "" : "  ·  "+item.Rule);
        }
        private void Explain()
        {
            switch((string)rule.SelectedItem)
            {
                case "Required": hint.Text="Must be included when its parent is active. At the top level, this applies to the whole bundle."; break;
                case "Optional": hint.Text="Can be left unchecked. Selecting a nested option also includes its parent files."; break;
                case "Choose one": hint.Text="One branch must be selected when the parent is active."; break;
                case "Choose up to one": hint.Text="Pick one alternative, or leave the group unchecked."; break;
                case "Choose any": hint.Text="Options in this group can be combined."; break;
                default: hint.Text="Uses the Manager’s usual rules for this option."; break;
            }
        }
        private void ShowSelected()
        {
            filling=true;
            try
            {
                var node=tree.SelectedNode;
                moveUp.Enabled=node!=null && node.PrevNode!=null;
                moveDown.Enabled=node!=null && node.NextNode!=null;
                if(node==null) {remove.Enabled=false; return;}
                var item=Item(node); name.Text=item.Name;
                parent.Items.Clear(); parent.Items.Add(new ParentItem()); parent.SelectedIndex=0;
                var targets=Walk(tree.Nodes).Where(n=>n==node.Parent || CanMove(node,n)).ToList();
                var duplicates=new HashSet<string>(targets.GroupBy(n=>Item(n).Name,StringComparer.OrdinalIgnoreCase)
                    .Where(g=>g.Count()>1).Select(g=>g.Key),StringComparer.OrdinalIgnoreCase);
                foreach(var other in targets)
                {
                    int i=parent.Items.Add(new ParentItem {Node=other,Ambiguous=duplicates.Contains(Item(other).Name)}); if(other==node.Parent) parent.SelectedIndex=i;
                }
                rule.Items.Clear(); rule.Items.AddRange(Group(item) ? BundleLayout.GroupRules : BundleLayout.FileRules);
                rule.SelectedItem=item.Rule; Explain();
                remove.Enabled=item.Id.StartsWith(BundleLayout.GroupPrefix,StringComparison.Ordinal);
            } finally { filling=false; }
        }
        // restoring: Restore author layout, which saves no layout at all.
        private void Changed(bool restoring=false)
        {
            beforeSuggestion=null; undo.Visible=false;
            restore=restoring; UpdateDirty();
        }
        // Save is offered only for a real change: an edit that was undone, or the author layout on a ZIP without a
        // saved one, rewrites nothing. A ZIP whose saved layout was ignored always needs a save.
        private void UpdateDirty()
        {
            dirty=package.LayoutError!=null || (restore ? package.HasBundleLayout : Signature(Draft())!=savedLayout);
            save.Enabled=dirty && !saving && refining==null;
            string problem=dirty ? DraftProblem() : null;
            status.ForeColor=problem!=null ? Palette.Accent : Palette.Muted;
            status.Text=problem ?? (!dirty ? (restore ? "This "+kind+" already uses the author layout." : IdleText())
                : restore ? "Author layout preview. Choose "+save.Text+" to apply." : "Unsaved changes");
        }
        private string IdleText()
        {
            return zip ? "Changes apply to this ZIP when you save." : "Saved in the Data folder. The "+kind+" itself is not changed.";
        }
        private static string Signature(List<BundleOption> items)
        {
            return String.Join("\n",items.Select(o=>o.Id+"\t"+(o.Parent ?? "")+"\t"+o.Name+"\t"+o.Rule));
        }
        // The rules Save enforces (BundleLayout.Materialize), shown while editing instead of only at Save.
        private string DraftProblem()
        {
            if(restore) return null;
            foreach(TreeNode node in Walk(tree.Nodes))
            {
                var item=Item(node);
                if(String.IsNullOrWhiteSpace(item.Name)) return "Name every option and group before saving.";
                if(!Group(item) || (item.Rule!="Choose one" && item.Rule!="Choose up to one")) continue;
                if(item.Rule=="Choose one" && node.Nodes.Count==0) return "Move at least one option into '"+item.Name.Trim()+"' or change its rule to Choose any.";
                if(node.Nodes.Count>1 && node.Nodes.Cast<TreeNode>().Any(child=>Required(Item(child))))
                    return "A required option in '"+item.Name.Trim()+"' would block its alternatives. Move it outside the group or mark it Optional.";
            }
            return null;
        }
        private bool Required(BundleOption item)
        {
            if(Group(item) || item.Rule=="Optional") return false;
            if(item.Rule=="Required") return true;
            ModComponent c=package.Find(item.Id);
            return c!=null && Composer.IsBase(new ModComponent {Name=c.Name,AuthorBase=c.AuthorBase});
        }
        // Selects the row a save problem names ('Name' in quotes), or the first unnamed row.
        private void SelectProblem(string message)
        {
            int a=message.IndexOf('\''), b=a<0 ? -1 : message.IndexOf('\'',a+1);
            TreeNode node=b>a+1 ? Walk(tree.Nodes).FirstOrDefault(n=>String.Equals(Item(n).Name.Trim(),message.Substring(a+1,b-a-1),StringComparison.OrdinalIgnoreCase))
                : Walk(tree.Nodes).FirstOrDefault(n=>String.IsNullOrWhiteSpace(Item(n).Name));
            if(node!=null) {node.EnsureVisible(); tree.SelectedNode=node;}
        }
        private List<BundleOption> Draft()
        {
            return Walk(tree.Nodes).Select(n=>new BundleOption {Id=Item(n).Id, Name=Item(n).Name.Trim(), Rule=Item(n).Rule, Parent=n.Parent==null ? null : Item(n.Parent).Id}).ToList();
        }
        private string assistantOverview;
        private void SetNote(string heading,string text)
        {
            noteHeading.Text=heading; suggestionNote.Text=text; details.Visible=false; assistantNote=null;
        }
        private void Preview(List<BundleOption> previous,BundleSuggestion result,bool assistant=false)
        {
            if(result.Changed)
            {
                wasRestore=restore;
                LoadTree(result.Items); Changed(); beforeSuggestion=previous; undo.Visible=true;
            }
            SetNote(assistant ? "ASSISTANT REVIEW" : "LOCAL SUGGESTION",result.Note);
            if(assistant && result.Note.Length>180)
            {
                assistantNote=result.Note;
                assistantOverview=result.Changed ? "The suggested changes are in your draft." : "No layout changes suggested.";
                suggestionNote.Text=assistantOverview; details.Text="Show details"; details.Visible=true;
            }
        }
        private void TrySuggestLocal()
        {
            var draft=Draft();
            Preview(draft,BundleSuggestions.Local(package,draft));
        }
        private void UndoSuggestion()
        {
            LoadTree(beforeSuggestion); restore=wasRestore; undo.Visible=false; beforeSuggestion=null; UpdateDirty();
            SetNote("LAYOUT GUIDE","Previous draft restored.");
        }
        private void Refine(AssistantKeyStore keys,AssistantSettings settings)
        {
            if(refining!=null) return;
            var draft=Draft();
            var cancel=new CancellationTokenSource(); refining=cancel;
            SetEditing(false); SetNote("ASSISTANT REVIEW","Reviewing the current layout… Cancel refinement stops it and keeps your draft.");
            Task.Run(delegate {
                BundleSuggestion result=null;
                string error=null;
                try {
                    string source;
                    using(var client=ClientFactory(keys.Load(out source)))
                        result=BundleSuggestions.Refine(package,draft,settings,client,cancel.Token);
                }
                catch(OperationCanceledException) {error="Refinement cancelled. Your draft is intact.";}
                catch(Exception ex) {error=ex.Message;}
                try {
                    BeginInvoke((Action)delegate {
                        refining=null;
                        bool cancelled=cancel.IsCancellationRequested; cancel.Dispose();
                        if(!confirmPanel.Visible) SetEditing(true);   // closing with unsaved changes: the question stays in charge
                        if(cancelled) suggestionNote.Text="Refinement cancelled. Your draft is intact.";
                        else if(error!=null) suggestionNote.Text=error;
                        else Preview(draft,result,true);
                    });
                }
                catch(InvalidOperationException) {cancel.Dispose();} // The editor closed during the request.
            });
        }
        // While the Assistant refines, only Close and Cancel refinement work.
        private void SetEditing(bool enabled)
        {
            foreach(Control c in editing) c.Enabled=enabled;
            if(refine!=null) refine.Text=enabled ? "Refine with Assistant" : "Cancel refinement";
            save.Enabled=enabled && dirty;
        }
        private void MoveNode(TreeNode node,TreeNode target)
        {
            if(!CanMove(node,target)) return;
            node.Remove(); (target==null ? tree.Nodes : target.Nodes).Add(node);
            if(target!=null) target.Expand(); tree.SelectedNode=node; ShowSelected(); Changed();
        }
        private void Reorder(int delta)
        {
            var node=tree.SelectedNode; if(node==null) return;
            var siblings=node.Parent==null ? tree.Nodes : node.Parent.Nodes; int index=node.Index+delta;
            if(index<0 || index>=siblings.Count) return;
            node.Remove(); siblings.Insert(index,node); tree.SelectedNode=node; Changed();
        }
        private void NewGroup()
        {
            var item=new BundleOption {Id=BundleLayout.GroupPrefix+Guid.NewGuid().ToString("N"),Name="New group",Rule="Choose any"};
            var node=new TreeNode {Tag=item}; LabelNode(node);
            var target=tree.SelectedNode; (target==null ? tree.Nodes : target.Nodes).Add(node);
            if(target!=null) target.Expand(); tree.SelectedNode=node; Changed(); name.Focus(); name.Editor.SelectAll();
        }
        private void Ungroup()
        {
            var node=tree.SelectedNode; if(node==null || !Item(node).Id.StartsWith(BundleLayout.GroupPrefix,StringComparison.Ordinal)) return;
            var siblings=node.Parent==null ? tree.Nodes : node.Parent.Nodes; int index=node.Index;
            while(node.Nodes.Count>0) {var child=node.Nodes[0]; child.Remove(); siblings.Insert(index++,child);}
            node.Remove(); ShowSelected(); Changed();
        }
        private async void SaveLayout()
        {
            if(saving || !dirty) return;
            var items=Draft();
            string problem=DraftProblem();
            if(problem!=null) {SelectProblem(problem); status.ForeColor=Palette.Red; status.Text=problem; return;}
            saving=true; Controls[0].Enabled=false; UseWaitCursor=true; status.ForeColor=Palette.Muted; status.Text=zip ? "Saving ZIP…" : "Saving layout…";
            try
            {
                var layout=restore ? null : items;
                await Task.Run(delegate {BundleLayout.Save(package.SourcePath,stamp,layout);});
                saving=false; dirty=false; DialogResult=DialogResult.OK; Close();
            }
            catch(InvalidDataException ex)
            {
                // A layout rule: shown on the row it concerns, not in a dialog.
                saving=false; Controls[0].Enabled=true; UseWaitCursor=false;
                SelectProblem(ex.Message); status.ForeColor=Palette.Red; status.Text=ex.Message;
            }
            catch(Exception ex)
            {
                // No popup (PolishChecks): the reason stays in the status line, and the draft stays in the editor.
                if(ex is OutOfMemoryException) throw;
                saving=false; Controls[0].Enabled=true; UseWaitCursor=false;
                status.ForeColor=Palette.Red; status.Text="Could not save the "+(zip ? "ZIP" : "layout")+": "+ex.Message.TrimEnd('.')+". Your changes are still here.";
            }
        }
    }
}
