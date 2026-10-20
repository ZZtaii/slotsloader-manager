using System;
using System.Drawing;
using System.Windows.Forms;

namespace SlotsInstaller
{
    // 1.0.8: the inline editor of a MY FILTERS filter, under the chips in the Filters panel (no dialog): name, keywords
    // (commas or new lines between them; the box grows with them), a hint, the live count and Delete · Cancel · Save.
    // MainFormCustomFilters.cs fills it, previews the typed keywords in the list and saves.
    internal sealed class CustomFilterEditor : Panel
    {
        internal const int MinKeywordLines = 3, MaxKeywordLines = 8;
        internal readonly Label NameCaption, KeywordsCaption, Hint, Status;
        internal readonly PaddedTextField NameField;
        internal readonly TextBox KeywordsBox;
        internal readonly FlatButton SaveButton, CancelButton, DeleteButton;
        private readonly Panel keywordsFrame;
        internal event EventHandler DraftChanged, SaveRequested, CancelRequested, DeleteRequested, HeightChanged;
        private int lastHeight = -1;

        internal CustomFilterEditor()
        {
            BackColor = Palette.Surface; ForeColor = Palette.Text; Margin = Padding.Empty; Padding = Padding.Empty;
            NameCaption = Caption("NAME");
            KeywordsCaption = Caption("KEYWORDS");
            NameField = new PaddedTextField { AccessibleName = "Filter name", PlaceholderText = "NSFW" };
            NameField.Editor.MaxLength = LibraryFilters.MaxNameLength;
            NameField.TextChanged += delegate { Raise(DraftChanged); };
            keywordsFrame = new KeywordFrame { BackColor = Palette.Raised };
            KeywordsBox = new TextBox { Multiline = true, AcceptsReturn = true, WordWrap = true, ScrollBars = ScrollBars.None, BorderStyle = BorderStyle.None, BackColor = Palette.Raised, ForeColor = Palette.Text, Font = Palette.Normal, AccessibleName = "Filter keywords" };
            NativeDpiFont.Attach(KeywordsBox);
            KeywordsBox.TextChanged += delegate { Raise(DraftChanged); FitKeywords(); };
            KeywordsBox.Enter += delegate { keywordsFrame.Invalidate(); }; KeywordsBox.Leave += delegate { keywordsFrame.Invalidate(); };
            // A multi-line TextBox has no Ctrl+A of its own.
            KeywordsBox.KeyDown += delegate(object sender, KeyEventArgs e) { if (e.KeyCode == Keys.A && e.Modifiers == Keys.Control) { KeywordsBox.SelectAll(); e.Handled = e.SuppressKeyPress = true; } };
            keywordsFrame.Controls.Add(KeywordsBox);
            keywordsFrame.MouseDown += delegate { KeywordsBox.Focus(); };
            Hint = new Label { UseMnemonic = false, AutoSize = false, Font = Palette.Small, ForeColor = Palette.Muted, Text = "Commas or new lines between keywords. A keyword matches mod names and option names." };
            Status = new Label { UseMnemonic = false, AutoSize = false, Font = Palette.Small, ForeColor = Palette.Muted, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
            SaveButton = MakeButton("Save", true, delegate { Raise(SaveRequested); });
            CancelButton = MakeButton("Cancel", false, delegate { Raise(CancelRequested); });
            DeleteButton = MakeButton("Delete", false, delegate { Raise(DeleteRequested); });
            DeleteButton.Danger = true;
            SaveButton.AccessibleName = "Save filter"; CancelButton.AccessibleName = "Cancel editing"; DeleteButton.AccessibleName = "Delete filter";
            // Tab order: name, keywords, Save, Cancel, Delete.
            NameField.TabIndex = 0; keywordsFrame.TabIndex = 1; SaveButton.TabIndex = 2; CancelButton.TabIndex = 3; DeleteButton.TabIndex = 4;
            Controls.AddRange(new Control[] { NameCaption, NameField, KeywordsCaption, keywordsFrame, Hint, Status, SaveButton, CancelButton, DeleteButton });
        }

        private static Label Caption(string text)
        {
            return new Label { UseMnemonic = false, AutoSize = false, Text = text, Font = new Font("Segoe UI", 8F, FontStyle.Bold), ForeColor = Palette.Muted, TextAlign = ContentAlignment.BottomLeft };
        }
        private static FlatButton MakeButton(string text, bool primary, Action click)
        {
            var b = new FlatButton(text, primary) { Font = Palette.Small, Margin = Padding.Empty, Padding = new Padding(10, 0, 10, 0) };
            b.Click += delegate { click(); };
            return b;
        }
        private void Raise(EventHandler handler) { if (handler != null) handler(this, EventArgs.Empty); }

        // Editing an existing filter shows Delete; a new one doesn't.
        internal void Begin(string name, string keywords, bool existing)
        {
            NameField.Text = name ?? ""; KeywordsBox.Text = keywords ?? "";
            DeleteButton.Visible = existing;
            SetStatus("", false);
            FitKeywords(); PerformLayout();
        }
        internal void SetStatus(string text, bool error)
        {
            Status.Text = text ?? ""; Status.ForeColor = error ? Palette.Red : Palette.Muted;
        }

        private int Px(int logical) { return DpiMetrics.Px(this, logical); }
        private int LineHeight { get { return (int)Math.Ceiling(KeywordsBox.Font.GetHeight(DeviceDpi)); } }
        // Lines the keywords take at the box's width, between MinKeywordLines and MaxKeywordLines (the box scrolls past that).
        private int KeywordLines(int width)
        {
            int inner = Math.Max(1, width - 2 * Px(8));
            Size text = TextRenderer.MeasureText(KeywordsBox.Text.Length == 0 ? " " : KeywordsBox.Text + (KeywordsBox.Text.EndsWith("\n") ? " " : ""), KeywordsBox.Font, new Size(inner, Int32.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix);
            int lines = (int)Math.Ceiling(text.Height / (double)Math.Max(1, LineHeight));
            return Math.Max(MinKeywordLines, Math.Min(MaxKeywordLines, lines));
        }
        private int FrameHeight(int width) { return KeywordLines(width) * LineHeight + 2 * Px(6); }
        private int HintHeight(int width)
        {
            return TextRenderer.MeasureText(Hint.Text, Hint.Font, new Size(Math.Max(1, width), Int32.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height;
        }
        private int ContentWidth(int width) { return Math.Max(Px(120), width - Px(10) - Px(6)); }
        // The editor's height for a panel width (FilterPanel.Arrange sizes it with this).
        internal int HeightFor(int width)
        {
            int w = ContentWidth(width);
            return Px(4) + Px(18) + Px(4) + Px(32) + Px(12) + Px(18) + Px(4) + FrameHeight(w) + Px(Rhythm.Related) + HintHeight(w) + Px(12) + Px(30) + Px(Rhythm.Group);
        }
        private void FitKeywords()
        {
            int h = HeightFor(Width);
            if (h != lastHeight) { lastHeight = h; Raise(HeightChanged); }
            PerformLayout();
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int x = Px(10), w = ContentWidth(Width), y = Px(4);
            NameCaption.Bounds = new Rectangle(x, y, w, Px(18)); y += Px(18) + Px(4);
            NameField.Bounds = new Rectangle(x, y, Math.Min(w, Px(320)), Px(32)); y += Px(32) + Px(12);
            KeywordsCaption.Bounds = new Rectangle(x, y, w, Px(18)); y += Px(18) + Px(4);
            int frame = FrameHeight(w);
            keywordsFrame.Bounds = new Rectangle(x, y, w, frame);
            KeywordsBox.Bounds = new Rectangle(Px(8), Px(6), Math.Max(1, w - 2 * Px(8)), Math.Max(1, frame - 2 * Px(6)));
            y += frame + Px(Rhythm.Related);
            int hint = HintHeight(w); Hint.Bounds = new Rectangle(x, y, w, hint); y += hint + Px(12);
            int buttonHeight = Px(30), gap = Px(6), right = x + w;
            foreach (FlatButton b in new[] { SaveButton, CancelButton, DeleteButton })
            {
                if (!b.Visible && b == DeleteButton) continue;
                int bw = TextRenderer.MeasureText(b.Text, b.Font).Width + Px(28);
                b.Bounds = new Rectangle(right - bw, y, bw, buttonHeight); right -= bw + gap;
            }
            Status.Bounds = new Rectangle(x, y, Math.Max(1, right - x - gap), buttonHeight);
        }
        protected override void OnResize(EventArgs e) { base.OnResize(e); FitKeywords(); }
        protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); NameField.LayoutEditor(); FitKeywords(); }

        // The keywords box's rounded frame, drawn like PaddedTextField's (accent while it has the focus).
        private sealed class KeywordFrame : Panel
        {
            internal KeywordFrame() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); TabStop = false; }
            protected override void OnPaintBackground(PaintEventArgs e) { using (var outside = new SolidBrush(Parent == null ? Palette.Surface : Parent.BackColor)) e.Graphics.FillRectangle(outside, ClientRectangle); }
            protected override void OnPaint(PaintEventArgs e)
            {
                var face = new RectangleF(0.5F, 0.5F, Math.Max(1, Width - 1F), Math.Max(1, Height - 1F)); float radius = DpiMetrics.Px(this, 4);
                Glyphs.FillRounded(e.Graphics, BackColor, face, radius);
                Glyphs.DrawRounded(e.Graphics, ContainsFocus ? Palette.Accent : Palette.Border, ContainsFocus ? Math.Max(1F, DeviceDpi / 96F) : 1F, face, radius);
            }
        }
    }
}
