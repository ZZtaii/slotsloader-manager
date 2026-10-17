using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SlotsInstaller
{
    // The outer field owns spacing and its frame; the inner, single-line EDIT
    // retains Windows caret, selection, clipboard, keyboard and IME behavior.
    internal sealed class PaddedTextField : UserControl
    {
        [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern IntPtr SendMessage(IntPtr window,int message,IntPtr wParam,string text);
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window,int message,IntPtr wParam,IntPtr value);
        internal readonly TextBox Editor;
        private int trailingSpace;
        private string placeholderText = "";
        internal PaddedTextField()
        {
            AutoScaleMode=AutoScaleMode.None;
            SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer,true);
            TabStop=false; Size=new Size(150,30); Margin=Padding.Empty;
            BackColor=Palette.Raised; ForeColor=Palette.Text; Font=Palette.Normal;
            Editor=new TextBox {BorderStyle=BorderStyle.None,AutoSize=false,Multiline=false,WordWrap=false,BackColor=BackColor,ForeColor=ForeColor,Font=Font,Margin=Padding.Empty,TabIndex=0};
            Controls.Add(Editor); NativeDpiFont.Attach(Editor);
            Editor.TextChanged+=delegate {base.OnTextChanged(EventArgs.Empty);};
            Editor.Enter+=delegate {Invalidate();}; Editor.Leave+=delegate {Invalidate();};
            Editor.MouseEnter+=delegate {SetHover(true);}; Editor.MouseLeave+=delegate {SetHover(ClientRectangle.Contains(PointToClient(Control.MousePosition)));};
            Editor.HandleCreated+=delegate {SendMessage(Editor.Handle,0xD3,new IntPtr(3),IntPtr.Zero);ApplyPlaceholder();LayoutEditor();};
            Editor.DpiChangedAfterParent+=delegate {LayoutEditor();};
            MouseDown+=delegate {Editor.Focus();};
        }
        public override string Text {get {return Editor==null ? base.Text : Editor.Text;} set {if(Editor==null)base.Text=value;else Editor.Text=value ?? "";}}
        public override bool Focused {get {return Editor!=null && Editor.Focused;}}
        public new bool Focus() {return Editor.Focus();}
        internal int TextLength {get {return Editor.TextLength;}}
        internal void Clear() {Editor.Clear();}
        internal int TrailingSpace {get {return trailingSpace;} set {trailingSpace=Math.Max(0,value);LayoutEditor();}}
        internal string PlaceholderText {get {return placeholderText;} set {placeholderText=value ?? "";ApplyPlaceholder();}}
        private void ApplyPlaceholder() {if(Editor!=null && Editor.IsHandleCreated)SendMessage(Editor.Handle,0x1501,IntPtr.Zero,placeholderText);}
        internal void LayoutEditor()
        {
            if(Editor==null)return;
            int inset=DpiMetrics.Px(this,7), lineHeight=(int)Math.Ceiling(Font.GetHeight(DeviceDpi));
            int height=Math.Min(Math.Max(1,ClientSize.Height-DpiMetrics.Px(this,4)),lineHeight);
            Editor.Bounds=new Rectangle(inset,Math.Max(0,(ClientSize.Height-height)/2),Math.Max(1,ClientSize.Width-inset*2-DpiMetrics.Px(this,trailingSpace)),height);
            Editor.AccessibleName=AccessibleName;
        }
        protected override void OnResize(EventArgs e) {base.OnResize(e);LayoutEditor();}
        protected override void OnDpiChangedAfterParent(EventArgs e) {base.OnDpiChangedAfterParent(e);LayoutEditor();}
        protected override void OnFontChanged(EventArgs e) {base.OnFontChanged(e);if(Editor!=null)Editor.Font=Font;LayoutEditor();}
        protected override void OnBackColorChanged(EventArgs e) {base.OnBackColorChanged(e);if(Editor!=null)Editor.BackColor=BackColor;}
        protected override void OnForeColorChanged(EventArgs e) {base.OnForeColorChanged(e);if(Editor!=null)Editor.ForeColor=ForeColor;}
        private bool hovering;
        private void SetHover(bool value) {if(hovering==value)return;hovering=value;Invalidate();}
        protected override void OnMouseEnter(EventArgs e) {base.OnMouseEnter(e);SetHover(true);}
        protected override void OnMouseLeave(EventArgs e) {base.OnMouseLeave(e);SetHover(ClientRectangle.Contains(PointToClient(Control.MousePosition)));}
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // Rounded frame: the corners show the parent surface, the native
            // EDIT stays inset on the field colour.
            using(var outside=new SolidBrush(Parent==null ? Palette.Surface : Parent.BackColor))e.Graphics.FillRectangle(outside,ClientRectangle);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var face=new RectangleF(0.5F,0.5F,Math.Max(1,Width-1F),Math.Max(1,Height-1F));float radius=DpiMetrics.Px(this,4);
            Glyphs.FillRounded(e.Graphics,BackColor,face,radius);
            Glyphs.DrawRounded(e.Graphics,ContainsFocus ? Palette.Accent : hovering ? Color.FromArgb(72,77,87) : Palette.Border,ContainsFocus ? Math.Max(1F,DeviceDpi/96F) : 1F,face,radius);
        }
    }
}
