using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace SlotsInstaller
{
    // This is deliberately a managed control, not an owner-drawn native
    // TreeView. The entire row is composed in one buffer: native hot tracking
    // can no longer erase an indicator between custom-draw notifications.
    internal sealed class OptionTree : ScrollableControl
    {
        internal readonly OptionNodeCollection Nodes;
        internal event EventHandler<OptionNodeEventArgs> AfterCheck, AfterSelect;
        internal event EventHandler<OptionDrawEventArgs> DrawNode;
        private readonly List<OptionNode> rows = new List<OptionNode>();
        private OptionNode selected;
        private int updates, itemHeight=32, indent=18, wheelRemainder;
        internal int ItemHeight { get {return itemHeight;} set {itemHeight=Math.Max(1,value); Rebuild();} }
        internal int Indent { get {return indent;} set {indent=Math.Max(1,value); Invalidate();} }
        // Kept explicit for regression checks: there are no native state images
        // or native tooltip windows in this control.
        internal bool CheckBoxes {get {return false;} set {}}
        internal bool ShowNodeToolTips {get {return false;} set {}}
        internal IList<OptionNode> VisibleNodes {get {return rows.AsReadOnly();}}
        internal OptionNode SelectedNode
        {
            get {return selected;}
            set
            {
                if (selected==value) return;
                if (value!=null && value.TreeView!=this) throw new ArgumentException("Detached option.");
                selected=value; Invalidate();
                if(value!=null && AfterSelect!=null) AfterSelect(this,new OptionNodeEventArgs(value));
                AccessibilityNotifyClients(AccessibleEvents.Selection,-1);
            }
        }
        internal OptionNode TopNode
        {
            get {return rows.Count==0 ? null : rows[Math.Min(rows.Count-1,Math.Max(0,-AutoScrollPosition.Y/itemHeight))];}
            set {int index=rows.IndexOf(value); if(index>=0) AutoScrollPosition=new Point(0,index*itemHeight);}
        }
        internal OptionTree()
        {
            Nodes=new OptionNodeCollection(this,null);
            AutoScroll=true; TabStop=true;
            SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw|ControlStyles.Selectable,true);
            AccessibleName="Outfit options"; AccessibleRole=AccessibleRole.Outline;
        }
        internal void BeginUpdate() {updates++;}
        internal void EndUpdate() {if(updates>0) updates--; if(updates==0) Rebuild();}
        internal void ExpandAll() {BeginUpdate(); foreach(var node in AllNodes(Nodes)) node.Expand(); EndUpdate();}
        internal void CollapseAll() {BeginUpdate(); foreach(var node in AllNodes(Nodes)) node.Collapse(); EndUpdate();}
        private static IEnumerable<OptionNode> AllNodes(OptionNodeCollection nodes)
        {
            foreach(var node in nodes) {yield return node; foreach(var child in AllNodes(node.Nodes)) yield return child;}
        }
        // Shown when there are no options, e.g. before a mod is opened.
        private string emptyText="";
        internal string EmptyText {get {return emptyText;} set {emptyText=value ?? ""; if(rows.Count==0) Invalidate();}}
        private readonly Dictionary<OptionNode,int> rowIndex=new Dictionary<OptionNode,int>();
        internal int RowIndex(OptionNode node) {int index; return node!=null && rowIndex.TryGetValue(node,out index) ? index : -1;}
        // Repaint one row only, e.g. when the hovered option changes.
        internal void InvalidateNode(OptionNode node)
        {
            int index=RowIndex(node); if(index<0) return;
            Invalidate(new Rectangle(0,index*itemHeight+AutoScrollPosition.Y,ClientSize.Width,itemHeight));
        }
        internal void Rebuild()
        {
            if(updates>0) return;
            rows.Clear(); AddRows(Nodes);
            rowIndex.Clear(); for(int i=0;i<rows.Count;i++) rowIndex[rows[i]]=i;
            AutoScrollMinSize=new Size(0,rows.Count*itemHeight);
            VerticalScroll.SmallChange=itemHeight; VerticalScroll.LargeChange=Math.Max(itemHeight,ClientSize.Height);
            if(selected!=null && selected.TreeView!=this) selected=null;
            if(selected!=null && !rows.Contains(selected)) {var ancestor=selected.Parent;while(ancestor!=null && !rows.Contains(ancestor))ancestor=ancestor.Parent;SelectedNode=ancestor;}
            Invalidate();
            if(IsHandleCreated) AccessibilityNotifyClients(AccessibleEvents.Reorder,-1);
        }
        private void AddRows(OptionNodeCollection nodes)
        {
            foreach(var node in nodes) {rows.Add(node); if(node.IsExpanded) AddRows(node.Nodes);}
        }
        internal Rectangle NodeBounds(OptionNode node)
        {
            int index=RowIndex(node); if(index<0) return Rectangle.Empty;
            int depth=0; for(var parent=node.Parent;parent!=null;parent=parent.Parent) depth++;
            int x=DpiMetrics.Px(this,20)+depth*indent;
            return new Rectangle(x,index*itemHeight+AutoScrollPosition.Y,Math.Max(0,ClientSize.Width-x),itemHeight);
        }
        internal OptionNode GetNodeAt(Point point)
        {
            if(!ClientRectangle.Contains(point)) return null;
            int row=(point.Y-AutoScrollPosition.Y)/itemHeight;
            return row>=0 && row<rows.Count ? rows[row] : null;
        }
        internal void EnsureVisible(OptionNode node)
        {
            BeginUpdate(); for(var p=node.Parent;p!=null;p=p.Parent) p.Expand(); EndUpdate();
            int index=rows.IndexOf(node); if(index<0) return;
            int y=index*itemHeight, top=-AutoScrollPosition.Y;
            if(y<top) AutoScrollPosition=new Point(0,y);
            else if(y+itemHeight>top+ClientSize.Height) AutoScrollPosition=new Point(0,y+itemHeight-ClientSize.Height);
        }
        internal void CheckedChanged(OptionNode node)
        {
            Invalidate(); if(AfterCheck!=null) AfterCheck(this,new OptionNodeEventArgs(node));
            if(IsHandleCreated) AccessibilityNotifyClients(AccessibleEvents.StateChange,-1);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            if(rows.Count==0 && emptyText.Length>0)
            {
                int inset=DpiMetrics.Px(this,24);
                TextRenderer.DrawText(e.Graphics,emptyText,Font,new Rectangle(inset,inset,Math.Max(1,ClientSize.Width-inset*2),Math.Max(1,ClientSize.Height-inset*2)),Palette.Muted,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|TextFormatFlags.WordBreak|TextFormatFlags.NoPrefix);
            }
            int first=Math.Max(0,-AutoScrollPosition.Y/itemHeight);
            int last=Math.Min(rows.Count,first+ClientSize.Height/itemHeight+2);
            for(int i=first;i<last;i++)
            {
                var node=rows[i]; Rectangle bounds=NodeBounds(node);
                TreeNodeStates state=node==selected ? TreeNodeStates.Selected : TreeNodeStates.Default;
                if(Focused && node==selected) state|=TreeNodeStates.Focused;
                if(DrawNode!=null) DrawNode(this,new OptionDrawEventArgs(e.Graphics,node,bounds,state));
            }
            base.OnPaint(e);
        }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            if(Enabled && e.Button==MouseButtons.Left && e.Clicks==1)
            {
                Focus(); var node=GetNodeAt(e.Location);
                if(node!=null)
                {
                    Rectangle bounds=node.Bounds;
                    if(node.Nodes.Count>0 && e.X>=bounds.X-DpiMetrics.Px(this,20) && e.X<bounds.X)
                    {if(node.IsExpanded) node.Collapse(); else node.Expand();}
                    else
                    {
                        SelectedNode=node;
                        // Every point in an option row activates the same
                        // choice; the separate disclosure arrow only expands.
                        var component=node.Tag as ModComponent;
                        if(component!=null && !component.IsDummy) node.Checked=!node.Checked;
                    }
                }
            }
            base.OnMouseDown(e);
        }
        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            var node=GetNodeAt(e.Location);
            var component=node==null ? null : node.Tag as ModComponent;
            if(Enabled && e.Button==MouseButtons.Left && node!=null && node.Nodes.Count>0 && (component==null || component.IsDummy) && e.X>=node.Bounds.X+DpiMetrics.Px(this,18))
            {if(node.IsExpanded) node.Collapse(); else node.Expand();}
            base.OnMouseDoubleClick(e);
        }
        protected override bool IsInputKey(Keys keyData)
        {
            Keys key=keyData & Keys.KeyCode;
            // 2.4.1: Enter toggles the focused option (the form's Space means Install).
            return key==Keys.Up || key==Keys.Down || key==Keys.Left || key==Keys.Right || key==Keys.Home || key==Keys.End || key==Keys.PageUp || key==Keys.PageDown || key==Keys.Enter || base.IsInputKey(keyData);
        }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            int index=rows.IndexOf(selected), next=index;
            if(e.KeyCode==Keys.Up) next=Math.Max(0,index-1);
            else if(e.KeyCode==Keys.Down) next=Math.Min(rows.Count-1,index+1);
            else if(e.KeyCode==Keys.Home) next=0;
            else if(e.KeyCode==Keys.End) next=rows.Count-1;
            else if(e.KeyCode==Keys.PageUp) next=Math.Max(0,index-Math.Max(1,ClientSize.Height/itemHeight));
            else if(e.KeyCode==Keys.PageDown) next=Math.Min(rows.Count-1,index+Math.Max(1,ClientSize.Height/itemHeight));
            else if(e.KeyCode==Keys.Right && selected!=null && selected.Nodes.Count>0)
            {if(!selected.IsExpanded) selected.Expand(); else next=rows.IndexOf(selected.Nodes[0]); e.Handled=true;}
            else if(e.KeyCode==Keys.Left && selected!=null)
            {if(selected.IsExpanded && selected.Nodes.Count>0) selected.Collapse(); else if(selected.Parent!=null) next=rows.IndexOf(selected.Parent); e.Handled=true;}
            if(next>=0 && next<rows.Count && next!=index) {SelectedNode=rows[next]; SelectedNode.EnsureVisible(); e.Handled=true;}
            base.OnKeyDown(e);
        }
        protected override void OnScroll(ScrollEventArgs se) {base.OnScroll(se); Invalidate();}
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            wheelRemainder+=e.Delta;int steps=wheelRemainder/120;wheelRemainder%=120;
            int lines=SystemInformation.MouseWheelScrollLines;
            int distance=lines<0 ? ClientSize.Height : lines*itemHeight;
            if(steps!=0 && distance>0) AutoScrollPosition=new Point(0,Math.Max(0,Math.Min(Math.Max(0,rows.Count*itemHeight-ClientSize.Height),-AutoScrollPosition.Y-steps*distance)));
            var handled=e as HandledMouseEventArgs;if(handled!=null)handled.Handled=true;
            Invalidate();
        }
        protected override void OnGotFocus(EventArgs e) {base.OnGotFocus(e); Invalidate();}
        protected override void OnLostFocus(EventArgs e) {base.OnLostFocus(e); Invalidate();}
        protected override AccessibleObject CreateAccessibilityInstance() {return new TreeAccessibility(this);}
        private sealed class TreeAccessibility : ControlAccessibleObject
        {
            private readonly OptionTree tree;
            internal TreeAccessibility(OptionTree tree):base(tree) {this.tree=tree;}
            public override int GetChildCount() {return tree.Nodes.Count;}
            public override AccessibleObject GetChild(int index) {return index>=0 && index<tree.Nodes.Count ? new NodeAccessibility(tree.Nodes[index]) : null;}
            public override AccessibleObject GetFocused() {return tree.Focused && tree.SelectedNode!=null ? new NodeAccessibility(tree.SelectedNode) : null;}
            public override AccessibleObject GetSelected() {return tree.SelectedNode==null ? null : new NodeAccessibility(tree.SelectedNode);}
            public override AccessibleObject HitTest(int x,int y) {var node=tree.GetNodeAt(tree.PointToClient(new Point(x,y))); return node==null ? base.HitTest(x,y) : new NodeAccessibility(node);}
        }
        private sealed class NodeAccessibility : AccessibleObject
        {
            private readonly OptionNode node;
            internal NodeAccessibility(OptionNode node) {this.node=node;}
            public override string Name {get {return node.Text;} set {node.Text=value;}}
            public override AccessibleRole Role {get {return AccessibleRole.OutlineItem;}}
            public override Rectangle Bounds {get {return node.TreeView==null ? Rectangle.Empty : node.TreeView.RectangleToScreen(node.Bounds);}}
            public override AccessibleObject Parent {get {return node.Parent==null ? node.TreeView.AccessibilityObject : new NodeAccessibility(node.Parent);}}
            public override AccessibleStates State
            {
                get
                {
                    AccessibleStates state=AccessibleStates.Selectable|AccessibleStates.Focusable;
                    if(node.Checked) state|=AccessibleStates.Checked;
                    if(node.Nodes.Count>0) state|=node.IsExpanded ? AccessibleStates.Expanded : AccessibleStates.Collapsed;
                    if(node.TreeView.SelectedNode==node) {state|=AccessibleStates.Selected; if(node.TreeView.Focused) state|=AccessibleStates.Focused;}
                    if(!node.TreeView.Enabled) state|=AccessibleStates.Unavailable;
                    if(!node.TreeView.ClientRectangle.IntersectsWith(node.Bounds)) state|=AccessibleStates.Offscreen;
                    return state;
                }
            }
            public override int GetChildCount() {return node.Nodes.Count;}
            public override AccessibleObject GetChild(int index) {return index>=0 && index<node.Nodes.Count ? new NodeAccessibility(node.Nodes[index]) : null;}
            public override string DefaultAction {get {var c=node.Tag as ModComponent; return c!=null && !c.IsDummy ? "Toggle option" : node.IsExpanded ? "Collapse" : "Expand";}}
            public override void DoDefaultAction()
            {
                if(node.TreeView==null || !node.TreeView.Enabled) return;
                node.EnsureVisible(); node.TreeView.SelectedNode=node;
                var component=node.Tag as ModComponent;
                if(component!=null && !component.IsDummy) node.Checked=!node.Checked; else if(node.IsExpanded) node.Collapse(); else node.Expand();
            }
            public override void Select(AccessibleSelection flags)
            {
                if(node.TreeView==null) return;
                if((flags & AccessibleSelection.TakeFocus)!=0) node.TreeView.Focus();
                if((flags & (AccessibleSelection.TakeSelection|AccessibleSelection.AddSelection))!=0) {node.EnsureVisible();node.TreeView.SelectedNode=node;}
            }
        }
    }
    internal sealed class OptionNode
    {
        internal string Text;
        internal object Tag;
        internal OptionNode Parent;
        internal OptionTree TreeView;
        internal readonly OptionNodeCollection Nodes;
        private bool selected;
        internal bool IsExpanded {get;private set;}
        internal bool Checked {get {return selected;} set {if(selected==value)return; selected=value; if(TreeView!=null) TreeView.CheckedChanged(this);}}
        internal Rectangle Bounds {get {return TreeView==null ? Rectangle.Empty : TreeView.NodeBounds(this);}}
        internal OptionNode(string text) {Text=text; Nodes=new OptionNodeCollection(null,this);}
        internal void Attach(OptionTree tree,OptionNode parent) {TreeView=tree;Parent=parent;foreach(var child in Nodes) child.Attach(tree,this);}
        internal void Expand() {if(IsExpanded)return; IsExpanded=true; if(TreeView!=null)TreeView.Rebuild();}
        internal void Collapse() {if(!IsExpanded)return; IsExpanded=false; if(TreeView!=null)TreeView.Rebuild();}
        internal void EnsureVisible() {if(TreeView!=null)TreeView.EnsureVisible(this);}
    }
    internal sealed class OptionNodeCollection : Collection<OptionNode>
    {
        private readonly OptionTree tree;
        private readonly OptionNode parent;
        internal OptionNodeCollection(OptionTree tree,OptionNode parent) {this.tree=tree;this.parent=parent;}
        private OptionTree Owner {get {return parent==null ? tree : parent.TreeView;}}
        protected override void InsertItem(int index,OptionNode item) {base.InsertItem(index,item);item.Attach(Owner,parent);if(Owner!=null)Owner.Rebuild();}
        protected override void RemoveItem(int index) {this[index].Attach(null,null);base.RemoveItem(index);if(Owner!=null)Owner.Rebuild();}
        protected override void ClearItems() {foreach(var node in this)node.Attach(null,null);base.ClearItems();if(Owner!=null)Owner.Rebuild();}
    }
    internal sealed class OptionNodeEventArgs : EventArgs
    {
        internal readonly OptionNode Node;
        internal OptionNodeEventArgs(OptionNode node) {Node=node;}
    }
    internal sealed class OptionDrawEventArgs : EventArgs
    {
        internal readonly Graphics Graphics;
        internal readonly OptionNode Node;
        internal readonly Rectangle Bounds;
        internal readonly TreeNodeStates State;
        internal bool DrawDefault;
        internal OptionDrawEventArgs(Graphics graphics,OptionNode node,Rectangle bounds,TreeNodeStates state) {Graphics=graphics;Node=node;Bounds=bounds;State=state;}
    }
}
