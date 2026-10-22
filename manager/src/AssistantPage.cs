using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace SlotsInstaller
{
    // What the Assistant tab needs from the main window.
    internal sealed class AssistantPageHost
    {
        internal AppConfig Config;
        internal Func<List<LibraryEntry>> Library;          // the scanned library, or null before the first scan
        internal Action OutfitsChanged;                      // refresh Installed/Library state after changes
        internal Action<string, Color> Status;               // page status line
        internal Func<bool> HostBusy;
        internal Func<string, CancellationTokenSource, bool> BeginWork;   // lock the window and show progress/cancel
        internal Action<OperationProgress> ReportProgress;
        internal Action<CancellationTokenSource> EndWork;
        internal Action OpenSettings;
        // 2.4.1: the window's own settings and key store (the Settings card uses them while the page does not exist).
        internal AssistantSettings Saved;
        internal AssistantKeyStore KeyStore;
    }

    // Settings → Test connection: one tiny request at low effort.
    internal static class AssistantConnection
    {
        internal static string Test(ClaudeClient client, string model)
        {
            using (client)
            {
                AssistantModel m = AssistantModels.Find(model) ?? AssistantModels.Find(AssistantModels.DefaultModel);
                var body = new JsonObj().Add("model", m.Id).Add("max_tokens", 1024)
                    .Add("thinking", new JsonObj().Add("type", "adaptive").Add("display", "summarized"))
                    .Add("output_config", new JsonObj().Add("effort", "low"));
                if (m.ServerFallback) body.Add("fallbacks", "default");
                body.Add("messages", new[] { new JsonObj().Add("role", "user").Add("content", "Reply with the single word: ready") });
                var clock = System.Diagnostics.Stopwatch.StartNew();
                ClaudeResponse r = client.Send(AssistantJson.Write(body), m.ServerFallback, CancellationToken.None);
                var usage = AssistantJson.Obj(r.Data, "usage");
                decimal cost = AssistantUsageMeter.Estimate(m.Id, AssistantJson.Long(usage, "input_tokens"), AssistantJson.Long(usage, "output_tokens"), AssistantJson.Long(usage, "cache_read_input_tokens"), AssistantJson.Long(usage, "cache_creation_input_tokens"));
                return "Connected · " + m.DisplayName + " replied in " + (clock.ElapsedMilliseconds / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " s · ≈ $" + cost.ToString("0.0000", CultureInfo.InvariantCulture);
            }
        }
    }

    internal enum TranscriptKind { User, Claude, Thinking, Tool, ToolError, Notice, Error, App, Hint }

    internal sealed class TranscriptEntry
    {
        internal TranscriptKind Kind;
        internal string Text = "";
        internal Image Thumb;
        internal bool Expanded;
        internal int Top, Height;
    }

    // Owner-drawn conversation view: messages, muted tool activity, collapsible
    // thinking summaries and preview thumbnails. Paints only visible entries.
    internal sealed class TranscriptView : ScrollableControl
    {
        private readonly List<TranscriptEntry> entries = new List<TranscriptEntry>();
        private int laidOutWidth = -1, laidOutDpi = -1;
        internal string EmptyTitle = "", EmptyBody = "", EmptyStatus = "";
        internal Color EmptyStatusColor = Palette.Muted;
        private readonly ContextMenuStrip menu;
        private TranscriptEntry menuEntry;

        internal TranscriptView()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            AutoScroll = true; BackColor = Palette.Surface; ForeColor = Palette.Text; TabStop = true;
            AccessibleName = "Assistant conversation"; AccessibleRole = AccessibleRole.List;
            menu = new ContextMenuStrip { BackColor = Palette.Surface, ForeColor = Palette.Text, ShowImageMargin = false, Font = Palette.Normal, Renderer = new ToolStripProfessionalRenderer(new DarkMenuColors()) };
            var copyOne = new ToolStripMenuItem("Copy message", null, delegate { if (menuEntry != null) CopyText(menuEntry.Text); });
            menu.Items.Add(copyOne);
            menu.Items.Add("Copy conversation", null, delegate { CopyText(ConversationText()); });
            menu.Opening += delegate { copyOne.Enabled = menuEntry != null; };
            ContextMenuStrip = menu;
        }

        internal IList<TranscriptEntry> Entries { get { return entries.AsReadOnly(); } }

        internal TranscriptEntry Add(TranscriptKind kind, string text, Image thumb)
        {
            bool atBottom = -AutoScrollPosition.Y + ClientSize.Height >= AutoScrollMinSize.Height - DpiMetrics.Px(this, 24);
            var entry = new TranscriptEntry { Kind = kind, Text = (text ?? "").Trim(), Thumb = thumb };
            entries.Add(entry);
            laidOutWidth = -1; Relayout();
            if (atBottom) ScrollToBottom();
            Invalidate();
            return entry;
        }

        internal void Clear()
        {
            foreach (var e in entries) if (e.Thumb != null) e.Thumb.Dispose();
            entries.Clear(); laidOutWidth = -1; Relayout(); AutoScrollPosition = Point.Empty; Invalidate();
        }

        internal void ScrollToBottom() { if (AutoScrollMinSize.Height > ClientSize.Height) AutoScrollPosition = new Point(0, AutoScrollMinSize.Height); }

        internal string ConversationText()
        {
            var sb = new StringBuilder();
            foreach (var e in entries)
            {
                string label = e.Kind == TranscriptKind.User ? "You" : e.Kind == TranscriptKind.Claude ? "Assistant" : e.Kind == TranscriptKind.Thinking ? "Thinking" : e.Kind == TranscriptKind.App ? "App" : "·";
                sb.Append(label).Append(": ").AppendLine(e.Text).AppendLine();
            }
            return sb.ToString();
        }

        private static void CopyText(string text) { try { if (!String.IsNullOrEmpty(text)) Clipboard.SetText(text); } catch (System.Runtime.InteropServices.ExternalException) { } }

        private int Px(int logical) { return DpiMetrics.Px(this, logical); }
        private int Pad { get { return Px(18); } }
        private Font FontFor(TranscriptKind kind)
        {
            switch (kind) { case TranscriptKind.User: case TranscriptKind.Claude: return Palette.Normal; default: return Palette.Small; }
        }
        private const TextFormatFlags Wrap = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl;

        private Size ThumbSize(Image thumb)
        {
            if (thumb == null) return Size.Empty;
            double scale = Math.Min(Px(200) / (double)thumb.Width, Px(130) / (double)thumb.Height);
            return new Size(Math.Max(1, (int)(thumb.Width * scale)), Math.Max(1, (int)(thumb.Height * scale)));
        }

        // Text area for an entry relative to its top-left.
        private Rectangle TextArea(TranscriptEntry e, int width)
        {
            int indent = e.Kind == TranscriptKind.User ? Px(14) : e.Kind == TranscriptKind.Claude ? 0 : Px(18);
            int top = e.Kind == TranscriptKind.User ? Px(28) : e.Kind == TranscriptKind.Claude ? Px(20) : 0;
            int right = e.Kind == TranscriptKind.User ? Px(14) : 0;
            return new Rectangle(indent, top, Math.Max(Px(40), width - indent - right), 0);
        }

        internal void Relayout()
        {
            int width = Math.Max(Px(120), ClientSize.Width - Pad * 2);
            if (width == laidOutWidth && DeviceDpi == laidOutDpi) return;
            laidOutWidth = width; laidOutDpi = DeviceDpi;
            int y = Pad;
            using (Graphics g = CreateGraphics())
            {
                TranscriptKind? previous = null;
                foreach (var e in entries)
                {
                    bool block = e.Kind == TranscriptKind.User || e.Kind == TranscriptKind.Claude;
                    if (previous.HasValue) y += block || previous == TranscriptKind.User || previous == TranscriptKind.Claude ? Px(14) : Px(6);
                    Rectangle area = TextArea(e, width);
                    string text = e.Kind == TranscriptKind.Thinking && !e.Expanded ? FirstLine(e.Text) : e.Text;
                    TextFormatFlags flags = e.Kind == TranscriptKind.Thinking && !e.Expanded ? TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis : Wrap;
                    int textHeight = TextRenderer.MeasureText(g, text.Length == 0 ? " " : text, FontFor(e.Kind), new Size(area.Width, 0), flags).Height;
                    int h = area.Top + textHeight;
                    if (e.Kind == TranscriptKind.User) h += Px(12);
                    if (e.Thumb != null) h += Px(8) + ThumbSize(e.Thumb).Height;
                    e.Top = y; e.Height = h; y += h;
                    previous = e.Kind;
                }
            }
            AutoScrollMinSize = new Size(0, entries.Count == 0 ? 0 : y + Pad);
        }

        private static string FirstLine(string text)
        {
            string line = (text ?? "").Replace("\r", "").Split('\n').FirstOrDefault(l => l.Trim().Length > 0) ?? "";
            return "Thinking · " + line.Trim();
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); Relayout(); Invalidate(); }
        protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); laidOutWidth = -1; Relayout(); Invalidate(); }
        protected override void OnScroll(ScrollEventArgs se) { base.OnScroll(se); Invalidate(); }
        protected override void OnMouseWheel(MouseEventArgs e) { base.OnMouseWheel(e); Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e); Focus();
            menuEntry = HitTest(e.Location);
            if (e.Button == MouseButtons.Left && menuEntry != null && menuEntry.Kind == TranscriptKind.Thinking)
            {
                menuEntry.Expanded = !menuEntry.Expanded; laidOutWidth = -1; Relayout(); Invalidate();
            }
        }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

        private TranscriptEntry HitTest(Point p)
        {
            int y = p.Y - AutoScrollPosition.Y;
            return entries.FirstOrDefault(e => y >= e.Top && y < e.Top + e.Height);
        }

        protected override void OnPaint(PaintEventArgs pe)
        {
            Graphics g = pe.Graphics;
            g.Clear(BackColor);
            int width = Math.Max(Px(120), ClientSize.Width - Pad * 2);
            if (entries.Count == 0) { PaintEmpty(g); return; }
            int offset = AutoScrollPosition.Y;
            foreach (var e in entries)
            {
                int top = e.Top + offset;
                if (top > ClientSize.Height || top + e.Height < 0) continue;
                var origin = new Point(Pad, top);
                Rectangle area = TextArea(e, width); area.Offset(origin);
                string text = e.Kind == TranscriptKind.Thinking && !e.Expanded ? FirstLine(e.Text) : e.Text;
                Font font = FontFor(e.Kind);
                switch (e.Kind)
                {
                    case TranscriptKind.User:
                    {
                        var card = new RectangleF(origin.X, top, width, e.Height);
                        Glyphs.FillRounded(g, Palette.Raised, card, Px(8));
                        TextRenderer.DrawText(g, "YOU", Palette.Tiny, new Rectangle(origin.X + Px(14), top + Px(8), width, Px(18)), Palette.Muted, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
                        break;
                    }
                    case TranscriptKind.Claude:
                        TextRenderer.DrawText(g, "ASSISTANT", Palette.Tiny, new Rectangle(origin.X, top, width, Px(18)), Palette.Accent, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
                        break;
                    default:
                        DrawMarker(g, e.Kind, new Point(origin.X + Px(6), top + Px(9)));
                        break;
                }
                Color color = e.Kind == TranscriptKind.User || e.Kind == TranscriptKind.Claude ? Palette.Text
                    : e.Kind == TranscriptKind.Error || e.Kind == TranscriptKind.ToolError ? Palette.Red
                    : e.Kind == TranscriptKind.App ? Palette.Green
                    : e.Kind == TranscriptKind.Hint ? Palette.Accent : Palette.Muted;
                TextFormatFlags flags = e.Kind == TranscriptKind.Thinking && !e.Expanded ? TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis : Wrap;
                Font drawFont = e.Kind == TranscriptKind.Thinking ? ItalicSmall : font;
                TextRenderer.DrawText(g, text, drawFont, new Rectangle(area.X, area.Y, area.Width, e.Height), color, flags);
                if (e.Thumb != null)
                {
                    Size size = ThumbSize(e.Thumb);
                    var r = new Rectangle(area.X, top + e.Height - size.Height, size.Width, size.Height);
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(e.Thumb, r);
                    Glyphs.DrawRounded(g, Palette.Border, 1F, new RectangleF(r.X - 0.5F, r.Y - 0.5F, r.Width + 1F, r.Height + 1F), Px(3));
                }
            }
            if (Focused && ShowFocusCues) Glyphs.DrawRounded(g, Color.FromArgb(120, Palette.Accent), 1F, new RectangleF(0.5F, 0.5F, ClientSize.Width - 1F, ClientSize.Height - 1F), Px(4));
        }

        private static Font italicSmall;
        private static Font ItalicSmall { get { return italicSmall ?? (italicSmall = new Font(UiFonts.Family, 9F, FontStyle.Italic)); } }

        private void DrawMarker(Graphics g, TranscriptKind kind, Point center)
        {
            Color c = kind == TranscriptKind.Error || kind == TranscriptKind.ToolError ? Palette.Red : kind == TranscriptKind.App ? Palette.Green : kind == TranscriptKind.Hint ? Palette.Accent : Palette.Border;
            SmoothingMode previous = g.SmoothingMode; g.SmoothingMode = SmoothingMode.AntiAlias;
            if (kind == TranscriptKind.Thinking)
                Glyphs.Chevron(g, new PointF(center.X, center.Y), Px(3), ChevronDirection.Right, Palette.Muted, 1.4F * DeviceDpi / 96F);
            else using (Brush b = new SolidBrush(c)) g.FillEllipse(b, center.X - Px(3), center.Y - Px(3), Px(6), Px(6));
            g.SmoothingMode = previous;
        }

        private void PaintEmpty(Graphics g)
        {
            int width = Math.Min(Px(620), Math.Max(Px(200), ClientSize.Width - Pad * 4));
            int x = (ClientSize.Width - width) / 2, y = Math.Max(Pad, ClientSize.Height / 2 - Px(130));
            using (var title = new Font(UiFonts.Family, 15F, FontStyle.Bold))
            {
                int h = TextRenderer.MeasureText(g, EmptyTitle, title, new Size(width, 0), Wrap).Height;
                TextRenderer.DrawText(g, EmptyTitle, title, new Rectangle(x, y, width, h), Palette.Text, Wrap | TextFormatFlags.HorizontalCenter);
                y += h + Px(10);
            }
            int body = TextRenderer.MeasureText(g, EmptyBody, Palette.Normal, new Size(width, 0), Wrap).Height;
            TextRenderer.DrawText(g, EmptyBody, Palette.Normal, new Rectangle(x, y, width, body), Palette.Muted, Wrap | TextFormatFlags.HorizontalCenter);
            y += body + Px(14);
            if (EmptyStatus.Length > 0)
            {
                int h = TextRenderer.MeasureText(g, EmptyStatus, Palette.Small, new Size(width, 0), Wrap).Height;
                TextRenderer.DrawText(g, EmptyStatus, Palette.Small, new Rectangle(x, y, width, h), EmptyStatusColor, Wrap | TextFormatFlags.HorizontalCenter);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { foreach (var e in entries) if (e.Thumb != null) e.Thumb.Dispose(); menu.Dispose(); }
            base.Dispose(disposing);
        }
    }

    // Multi-line prompt in the same rounded frame as the single-line fields.
    internal sealed class PromptBox : Panel
    {
        internal readonly TextBox Editor;
        internal event EventHandler Submit;
        internal PromptBox()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Palette.Raised;
            Editor = new TextBox { Multiline = true, AcceptsReturn = true, WordWrap = true, ScrollBars = ScrollBars.None, BorderStyle = BorderStyle.None, BackColor = Palette.Raised, ForeColor = Palette.Text, Font = Palette.Normal, AccessibleName = "Message to the Assistant" };
            NativeDpiFont.Attach(Editor);
            Controls.Add(Editor);
            Editor.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter && !e.Shift && !e.Control && !e.Alt) { e.Handled = true; e.SuppressKeyPress = true; if (Submit != null) Submit(this, EventArgs.Empty); }
                else if (e.KeyCode == Keys.A && e.Control) { Editor.SelectAll(); e.Handled = true; e.SuppressKeyPress = true; }
            };
            Editor.Enter += delegate { Invalidate(); }; Editor.Leave += delegate { Invalidate(); };
            MouseDown += delegate { Editor.Focus(); };
        }
        internal void LayoutEditor()
        {
            int inset = DpiMetrics.Px(this, 10), top = DpiMetrics.Px(this, 8);
            Editor.Bounds = new Rectangle(inset, top, Math.Max(1, ClientSize.Width - inset - DpiMetrics.Px(this, 4)), Math.Max(1, ClientSize.Height - top * 2));
        }
        protected override void OnResize(EventArgs e) { base.OnResize(e); LayoutEditor(); }
        protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); LayoutEditor(); }
        protected override void OnPaintBackground(PaintEventArgs e) { using (var outside = new SolidBrush(Parent == null ? Palette.Surface : Parent.BackColor)) e.Graphics.FillRectangle(outside, ClientRectangle); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var face = new RectangleF(0.5F, 0.5F, Math.Max(1, Width - 1F), Math.Max(1, Height - 1F)); float radius = DpiMetrics.Px(this, 6);
            Glyphs.FillRounded(e.Graphics, BackColor, face, radius);
            Glyphs.DrawRounded(e.Graphics, ContainsFocus ? Palette.Accent : Palette.Border, ContainsFocus ? Math.Max(1F, DeviceDpi / 96F) : 1F, face, radius);
        }
    }

    // Queued actions with check boxes, state and a two-line title.
    internal sealed class ActionListBox : ListBox
    {
        internal readonly HashSet<int> Checked = new HashSet<int>();
        internal event EventHandler CheckedChanged;
        internal ActionListBox()
        {
            DrawMode = DrawMode.OwnerDrawFixed; IntegralHeight = false; BorderStyle = BorderStyle.None;
            BackColor = Palette.Background; ForeColor = Palette.Text; Font = Palette.Small; ItemHeight = 64; AccessibleName = "Planned actions";
            NativeDpiFont.Attach(this);
            HandleCreated += delegate { ItemHeight = DpiMetrics.Px(this, 64); };
        }
        protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); ItemHeight = DpiMetrics.Px(this, 64); }
        internal AssistantAction ActionAt(int index) { return index >= 0 && index < Items.Count ? Items[index] as AssistantAction : null; }
        private Rectangle Box(Rectangle bounds) { int s = DpiMetrics.Px(this, 15); return new Rectangle(bounds.Left + DpiMetrics.Px(this, 12), bounds.Top + DpiMetrics.Px(this, 12), s, s); }
        internal void Toggle(int index)
        {
            AssistantAction a = ActionAt(index);
            if (a == null || a.State != AssistantActionState.Pending) return;
            if (!Checked.Remove(a.Number)) Checked.Add(a.Number);
            Invalidate(GetItemRectangle(index));
            if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty);
        }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            int index = IndexFromPoint(e.Location);
            if (index >= 0 && e.Button == MouseButtons.Left && e.X < DpiMetrics.Px(this, 40)) Toggle(index);
        }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Space && SelectedIndex >= 0) { Toggle(SelectedIndex); e.Handled = true; }
        }
        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            using (Brush bg = new SolidBrush(Palette.Background)) e.Graphics.FillRectangle(bg, e.Bounds);
            AssistantAction a = ActionAt(e.Index); if (a == null) return;
            bool selected = (e.State & DrawItemState.Selected) != 0;
            var card = new Rectangle(e.Bounds.Left + DpiMetrics.Px(this, 2), e.Bounds.Top + DpiMetrics.Px(this, 3), e.Bounds.Width - DpiMetrics.Px(this, 4), e.Bounds.Height - DpiMetrics.Px(this, 6));
            Glyphs.FillRounded(e.Graphics, selected ? Palette.Selected : Palette.Surface, card, DpiMetrics.Px(this, 5));
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle box = Box(e.Bounds);
            bool pending = a.State == AssistantActionState.Pending;
            if (pending)
            {
                bool on = Checked.Contains(a.Number);
                using (var pen = new Pen(on ? Palette.Accent : Palette.Muted, 1.4F * DeviceDpi / 96F)) using (var path = Glyphs.Rounded(box, DpiMetrics.Px(this, 3))) e.Graphics.DrawPath(pen, path);
                if (on) Glyphs.FillRounded(e.Graphics, Palette.Accent, Rectangle.Inflate(box, -DpiMetrics.Px(this, 3), -DpiMetrics.Px(this, 3)), DpiMetrics.Px(this, 1));
            }
            else
            {
                Color c = a.State == AssistantActionState.Succeeded ? Palette.Green : a.State == AssistantActionState.Failed ? Palette.Red : Palette.Muted;
                using (Brush b = new SolidBrush(c)) e.Graphics.FillEllipse(b, box.Left + box.Width / 4, box.Top + box.Height / 4, box.Width / 2, box.Height / 2);
            }
            int left = box.Right + DpiMetrics.Px(this, 10), right = card.Right - DpiMetrics.Px(this, 8);
            string state = a.AutoApplied && a.State == AssistantActionState.Succeeded ? "Auto-applied" : a.State == AssistantActionState.Succeeded ? "Applied" : Char.ToUpperInvariant(a.StateText[0]) + a.StateText.Substring(1);
            Color stateColor = a.State == AssistantActionState.Succeeded ? Palette.Green : a.State == AssistantActionState.Failed ? Palette.Red : a.State == AssistantActionState.Pending ? Palette.Accent : Palette.Muted;
            TextRenderer.DrawText(e.Graphics, "#" + a.Number + " · " + state, Palette.Tiny, new Rectangle(left, card.Top + DpiMetrics.Px(this, 5), right - left, DpiMetrics.Px(this, 16)), stateColor, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(e.Graphics, a.Title, Font, new Rectangle(left, card.Top + DpiMetrics.Px(this, 21), right - left, card.Bottom - card.Top - DpiMetrics.Px(this, 23)), Palette.Text, TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.TextBoxControl);
            if ((e.State & DrawItemState.Focus) != 0 && Focused) Glyphs.DrawRounded(e.Graphics, Color.FromArgb(170, Palette.Accent), Math.Max(1F, DeviceDpi / 96F), RectangleF.Inflate(card, -0.5F, -0.5F), DpiMetrics.Px(this, 5));
        }
    }

    // The Assistant tab: conversation on the left, the Plan on the right.
    internal sealed class AssistantPage : Panel
    {
        private readonly AssistantPageHost host;
        internal readonly AssistantSettings Saved;          // Data\assistant-settings.json
        internal readonly AssistantSettings Session;        // what the tools read; auto-apply is per session
        internal readonly AssistantKeyStore KeyStore;
        private AssistantWorkspace workspace;
        internal Func<string> KeyOverride;                  // demo/tests: a fake key that is never saved
        private AssistantConversation conversation;
        private decimal finishedCost;
        private CancellationTokenSource runCancel, applyCancel;
        private bool running, applying, syncingCombos;
        private string lastApplyText; private Color lastApplyColor = Palette.Muted;
        internal Func<string, ClaudeClient> ClientFactory;   // key -> client; tests and demos point it at a mock

        internal DarkCombo ModelCombo, EffortCombo;
        internal Label UsageLabel, PlanCaption, ApplyStatus, AutoApplyWarning, PromptHint;
        internal FlatButton NewChatButton, SendButton, StopButton, ApplyButton, DiscardButton, CancelApplyButton;
        internal TranscriptView Transcript;
        internal PromptBox Prompt;
        internal ActionListBox ActionList;
        internal ClampedLabel ActionDetail;
        internal CheckBox AutoApply;
        internal SplitContainer Split;
        private Panel planPanel, applyRow;
        private string usageBreakdown = "";

        internal AssistantConversation Conversation { get { return conversation; } }
        internal bool Running { get { return running; } }
        internal bool Applying { get { return applying; } }
        private string DataDir { get { return AppConfig.DataDir; } }

        internal AssistantPage(AssistantPageHost host)
        {
            this.host = host;
            Dock = DockStyle.Fill;
            Saved = host.Saved ?? AssistantSettings.Load(DataDir);
            Session = new AssistantSettings { Model = Saved.Model, Effort = Saved.Effort, AutoApply = Saved.AutoApply };
            KeyStore = host.KeyStore ?? new AssistantKeyStore(DataDir);
            workspace = new AssistantWorkspace(host.Config, Session) { LibraryProvider = host.Library };
            ClientFactory = delegate(string key)
            {
                var client = new ClaudeClient(key);
                client.Diagnostics = AppendDiagnostics;
                return client;
            };
            Build();
            UpdateEmptyState(); UpdateButtons(); UpdateUsage();
        }

        private int Px(int logical) { return DpiMetrics.Px(this, logical); }

        private static FlatButton MakeButton(string text, bool primary, int width, Action click)
        {
            var b = new FlatButton(text, primary) { Width = width, Margin = new Padding(0, 0, 7, 0) };
            b.Click += delegate { click(); };
            return b;
        }

        private void Build()
        {
            // Header: model and effort switchers, the session cost meter and New chat.
            var header = new Panel { Dock = DockStyle.Top, Height = 40, Padding = new Padding(0, 0, 0, 8) };
            var switches = new FlowLayoutPanel { Dock = DockStyle.Left, Width = 560, WrapContents = false, Padding = Padding.Empty, Margin = Padding.Empty };
            ModelCombo = new DarkCombo { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, BackColor = Palette.Raised, ForeColor = Palette.Text, Font = Palette.Small, IntegralHeight = false, Width = 230, Margin = new Padding(0, 0, 8, 0), AccessibleName = "Model" };
            foreach (AssistantModel m in AssistantModels.All) ModelCombo.Items.Add(m.DisplayName);
            EffortCombo = new DarkCombo { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, BackColor = Palette.Raised, ForeColor = Palette.Text, Font = Palette.Small, IntegralHeight = false, Width = 150, Margin = new Padding(0, 0, 8, 0), AccessibleName = "Effort" };
            foreach (string label in AssistantModels.EffortLabels) EffortCombo.Items.Add(label + " effort");
            switches.Controls.Add(ModelCombo); switches.Controls.Add(EffortCombo);
            NewChatButton = MakeButton("New chat", false, 110, NewChat); NewChatButton.Dock = DockStyle.Right; NewChatButton.Height = 32; NewChatButton.Font = Palette.Small; NewChatButton.Margin = Padding.Empty;
            UsageLabel = new Label { UseMnemonic = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight, ForeColor = Palette.Muted, Font = Palette.Small, AutoEllipsis = true, Padding = new Padding(0, 0, 10, 0) };
            // 2.4.0 (no tooltips): clicking the usage meter shows the per-model breakdown.
            UsageLabel.Cursor = Cursors.Hand; UsageLabel.AccessibleRole = AccessibleRole.PushButton;
            UsageLabel.Click += delegate { ThemedMessageBox.Show(FindForm(), usageBreakdown.Length == 0 ? "No usage yet." : usageBreakdown, "Assistant usage (estimate)", MessageBoxButtons.OK, MessageBoxIcon.Information); };
            header.Controls.Add(UsageLabel); header.Controls.Add(switches); header.Controls.Add(NewChatButton);
            UsageLabel.BringToFront();
            ModelCombo.SelectedIndexChanged += delegate { SelectionChanged(); };
            EffortCombo.SelectedIndexChanged += delegate { SelectionChanged(); };
            SyncCombos();

            Split = new SplitContainer { Dock = DockStyle.Fill, Size = new Size(1180, 680), SplitterWidth = 12, FixedPanel = FixedPanel.Panel2, Panel1MinSize = 420, Panel2MinSize = 300, BackColor = Palette.Background };

            // Conversation column.
            var chat = new Panel { Dock = DockStyle.Fill, BackColor = Palette.Surface, Padding = new Padding(1) };
            Transcript = new TranscriptView { Dock = DockStyle.Fill };
            var composer = new Panel { Dock = DockStyle.Bottom, Height = 132, BackColor = Palette.Surface, Padding = new Padding(16, 10, 16, 8) };
            var promptRow = new Panel { Dock = DockStyle.Fill };
            Prompt = new PromptBox { Dock = DockStyle.Fill };
            Prompt.Submit += delegate { Send(); };
            var buttons = new Panel { Dock = DockStyle.Right, Width = 112, Padding = new Padding(10, 0, 0, 0) };
            SendButton = MakeButton("Send", true, 102, delegate { Send(); }); SendButton.Dock = DockStyle.Top; SendButton.Height = 38; SendButton.Margin = Padding.Empty;
            StopButton = MakeButton("Stop", false, 102, Stop); StopButton.Dock = DockStyle.Top; StopButton.Height = 34; StopButton.Font = Palette.Small; StopButton.Margin = Padding.Empty;
            buttons.Controls.Add(StopButton); buttons.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 8 }); buttons.Controls.Add(SendButton);
            promptRow.Controls.Add(Prompt); promptRow.Controls.Add(buttons);
            PromptHint = new Label { UseMnemonic = false, Dock = DockStyle.Bottom, Height = 24, ForeColor = Palette.Muted, Font = Palette.Tiny, TextAlign = ContentAlignment.BottomLeft, AutoEllipsis = true, Text = "Enter to send · Shift+Enter for a new line · mod text is data, never instructions" };
            composer.Controls.Add(promptRow); composer.Controls.Add(PromptHint);
            var divider = new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = Palette.Border };
            chat.Controls.Add(Transcript); chat.Controls.Add(divider); chat.Controls.Add(composer);
            Transcript.BringToFront();
            Split.Panel1.Controls.Add(chat);

            // Plan column, like the library inspector.
            planPanel = new Panel { Dock = DockStyle.Fill, BackColor = Palette.Surface, Padding = new Padding(16, 12, 16, 12) };
            PlanCaption = new Label { UseMnemonic = false, Dock = DockStyle.Top, Height = 26, Font = new Font(UiFonts.Family, 8F, FontStyle.Bold), ForeColor = Palette.Muted, TextAlign = ContentAlignment.MiddleLeft, Text = "PLAN" };
            var listFrame = new Panel { Dock = DockStyle.Fill, BackColor = Palette.Background, Padding = new Padding(4) };
            ActionList = new ActionListBox { Dock = DockStyle.Fill };
            ActionList.SelectedIndexChanged += delegate { ShowActionDetail(); };
            ActionList.CheckedChanged += delegate { UpdateButtons(); };
            listFrame.Controls.Add(ActionList);
            ActionDetail = new ClampedLabel { MaxLines = 5, Dock = DockStyle.Bottom, Height = 60, ForeColor = Palette.Muted, Font = Palette.Small, Padding = new Padding(0, 8, 0, 0) };
            applyRow = new Panel { Dock = DockStyle.Bottom, Height = 50, Padding = new Padding(0, 10, 0, 0) };
            ApplyButton = MakeButton("Apply selected", true, 150, ApplySelected); ApplyButton.Dock = DockStyle.Left; ApplyButton.Margin = Padding.Empty;
            DiscardButton = MakeButton("Discard", false, 96, DiscardSelected); DiscardButton.Dock = DockStyle.Right; DiscardButton.Margin = Padding.Empty;
            CancelApplyButton = MakeButton("Cancel", false, 96, delegate { if (applyCancel != null) { applyCancel.Cancel(); CancelApplyButton.Enabled = false; ApplyStatus.Text = "Cancelling at the next safe step…"; } });
            CancelApplyButton.Dock = DockStyle.Right; CancelApplyButton.Margin = Padding.Empty; CancelApplyButton.Visible = false;
            applyRow.Controls.Add(ApplyButton); applyRow.Controls.Add(DiscardButton); applyRow.Controls.Add(CancelApplyButton);
            ApplyStatus = new Label { UseMnemonic = false, Dock = DockStyle.Bottom, Height = 26, ForeColor = Palette.Muted, Font = Palette.Small, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
            var autoRow = new Panel { Dock = DockStyle.Bottom, Height = 66, Padding = new Padding(0, 10, 0, 0) };
            AutoApply = new CheckBox { Text = "Auto-apply the Assistant's changes", Dock = DockStyle.Top, Height = 26, ForeColor = Palette.Text, Font = Palette.Small, Checked = Session.AutoApply };
            AutoApplyWarning = new Label { UseMnemonic = false, Dock = DockStyle.Fill, ForeColor = Palette.Muted, Font = Palette.Tiny, AutoEllipsis = true };
            autoRow.Controls.Add(AutoApplyWarning); autoRow.Controls.Add(AutoApply); AutoApplyWarning.BringToFront();
            AutoApply.CheckedChanged += delegate { AutoApplyChanged(); };
            var autoDivider = new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = Palette.Border };
            planPanel.Controls.Add(listFrame); planPanel.Controls.Add(PlanCaption); planPanel.Controls.Add(ActionDetail); planPanel.Controls.Add(ApplyStatus); planPanel.Controls.Add(applyRow); planPanel.Controls.Add(autoDivider); planPanel.Controls.Add(autoRow);
            listFrame.BringToFront();
            Split.Panel2.Controls.Add(planPanel);

            Controls.Add(Split); Controls.Add(header); Split.BringToFront();
            UpdateAutoApplyWarning();
        }

        // ---- DPI ------------------------------------------------------------
        internal void ApplyDpi()
        {
            if (Split.Width >= Px(732)) { Split.SplitterWidth = Px(12); Split.Panel1MinSize = Px(420); Split.Panel2MinSize = Px(300); int want = Split.Width - Split.SplitterWidth - Px(360); Split.SplitterDistance = Math.Max(Split.Panel1MinSize, Math.Min(want, Split.Width - Split.SplitterWidth - Split.Panel2MinSize)); }
            ActionList.ItemHeight = Px(64); Prompt.LayoutEditor(); Transcript.Relayout(); Transcript.Invalidate();
        }

        // ---- settings and selection ------------------------------------------
        internal void ReloadSavedSettings()
        {
            AssistantSettings fresh = AssistantSettings.Load(DataDir);
            Saved.Model = fresh.Model; Saved.Effort = fresh.Effort; Saved.AutoApply = fresh.AutoApply;
            if (conversation == null) { Session.Model = Saved.Model; Session.Effort = Saved.Effort; SyncCombos(); }
            UpdateEmptyState();
        }

        private void SyncCombos()
        {
            syncingCombos = true;
            try
            {
                ModelCombo.SelectedIndex = Math.Max(0, AssistantModels.All.FindIndex(m => m.Id == Session.Model));
                EffortCombo.SelectedIndex = Math.Max(0, Array.IndexOf(AssistantModels.Efforts, Session.Effort));
            }
            finally { syncingCombos = false; }
        }

        private void SelectionChanged()
        {
            if (syncingCombos || ModelCombo.SelectedIndex < 0 || EffortCombo.SelectedIndex < 0) return;
            if (running) { SyncCombos(); return; }
            string model = AssistantModels.All[ModelCombo.SelectedIndex].Id, effort = AssistantModels.Efforts[EffortCombo.SelectedIndex];
            if (model == Session.Model && effort == Session.Effort) return;
            Session.Model = model; Session.Effort = effort;
            if (conversation != null)
            {
                string hint = conversation.SetSelection(model, effort);
                if (hint != null) Transcript.Add(TranscriptKind.Hint, hint, null);
            }
            // The last-used selection becomes the default for the next session.
            Saved.Model = model; Saved.Effort = effort;
            try { Saved.Save(DataDir); } catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); }
            UpdateEmptyState();
        }

        private void AutoApplyChanged()
        {
            if (AutoApply.Checked == Session.AutoApply) return;
            // 2.4.1: no popup; the check box is the consent and the red line under it says changes now run without approval.
            Session.AutoApply = AutoApply.Checked;
            UpdateAutoApplyWarning(); UpdateEmptyState();
        }

        private void UpdateAutoApplyWarning()
        {
            AutoApplyWarning.Text = Session.AutoApply ? "On: changes run without your approval." : "Off: changes wait here until you apply them.";
            AutoApplyWarning.ForeColor = Session.AutoApply ? Palette.Red : Palette.Muted;
        }

        internal void UpdateEmptyState()
        {
            AssistantModel m = AssistantModels.Find(Session.Model);
            Transcript.EmptyTitle = "Ask the Assistant to find, install or explain mods";
            Transcript.EmptyBody = "For example:\n“Give me 5 options for Chun-Li C1.”\n“What's installed for Ryu, and which of those mods haven't I played in a while?”\n\nThe Assistant searches your library, checks what's installed and plans the changes. Nothing is installed until you apply it in the Plan.";
            string key = null;
            try { key = LoadKey(); } catch (Exception ex) { if (ex is OutOfMemoryException) throw; }
            if (key == null) { Transcript.EmptyStatus = "No API key · add one in Settings → Assistant"; Transcript.EmptyStatusColor = Palette.Accent; }
            else { Transcript.EmptyStatus = (m == null ? Session.Model : m.DisplayName) + " · " + AssistantModels.EffortLabel(Session.Effort) + " effort" + (Session.AutoApply ? " · auto-apply ON" : ""); Transcript.EmptyStatusColor = Session.AutoApply ? Palette.Red : Palette.Muted; }
            key = null;
            Transcript.Invalidate();
        }

        // ---- conversation ----------------------------------------------------
        internal void LibraryChanged() { workspace.InvalidateLibrary(); }

        // Points the Assistant at another configuration (generated fixtures for
        // checks and the screenshot demo). Starts a new chat.
        internal void Rebind(AppConfig config, Func<List<LibraryEntry>> library)
        {
            if (running || applying) throw new InvalidOperationException("Wait for the Assistant to finish.");
            workspace = new AssistantWorkspace(config, Session) { LibraryProvider = library };
            conversation = null; ActionList.Checked.Clear(); Transcript.Clear(); RefreshPlan(); UpdateUsage();
        }

        private string LoadKey()
        {
            if (KeyOverride != null) return KeyOverride();
            string source; return KeyStore.Load(out source);
        }

        private void AppendDiagnostics(string line)
        {
            try
            {
                Directory.CreateDirectory(DataDir);
                string file = Path.Combine(DataDir, "assistant-log.txt");
                if (File.Exists(file) && new FileInfo(file).Length > 512 * 1024) File.Delete(file);
                File.AppendAllText(file, line + Environment.NewLine);
            }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; }
        }

        internal bool Send() { return Send(Prompt.Editor.Text); }

        internal bool Send(string text)
        {
            text = (text ?? "").Trim();
            if (running || applying || text.Length == 0) return false;
            string key = LoadKey();
            if (key == null)
            {
                Transcript.Add(TranscriptKind.Error, "No API key. Add your Anthropic API key in Settings → Assistant, or set ANTHROPIC_API_KEY.", null);
                if (host.Status != null) host.Status("Add an API key in Settings to use the Assistant.", Palette.Accent);
                return false;
            }
            if (conversation == null)
            {
                Func<string, ClaudeClient> factory = ClientFactory; string captured = key;
                conversation = new AssistantConversation(workspace, delegate { return factory(captured); }, Session.Model, Session.Effort);
                conversation.Toolbox.ActionChanged = delegate(AssistantAction a) { Post(delegate { RefreshPlan(); }); };
                conversation.Events = delegate(AssistantEvent e) { Post(delegate { OnEvent(e); }); };
            }
            Prompt.Editor.Clear(); lastApplyText = null;
            var cancel = new CancellationTokenSource(); runCancel = cancel;
            SetRunning(true);
            AssistantConversation current = conversation;
            var worker = new Thread(delegate()
            {
                AssistantTurnResult result = null; Exception failure = null;
                try { result = current.Send(text, cancel.Token); }
                catch (Exception ex) { failure = ex; }
                Post(delegate { TurnFinished(current, cancel, result, failure); });
            }) { IsBackground = true, Name = "assistant-turn" };
            worker.SetApartmentState(ApartmentState.STA);
            worker.Start();
            return true;
        }

        private void Post(Action action)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke((MethodInvoker)delegate { if (!IsDisposed) action(); }); }
            catch (InvalidOperationException) { }
        }

        private void OnEvent(AssistantEvent e)
        {
            switch (e.Kind)
            {
                case AssistantEventKind.User: Transcript.Add(TranscriptKind.User, e.Text, null); break;
                case AssistantEventKind.Text: Transcript.Add(TranscriptKind.Claude, e.Text, null); break;
                case AssistantEventKind.Thinking: Transcript.Add(TranscriptKind.Thinking, e.Text, null); break;
                case AssistantEventKind.Tool: Transcript.Add(TranscriptKind.Tool, e.Text, Thumbnail(e.Image)); break;
                case AssistantEventKind.ToolError: Transcript.Add(TranscriptKind.ToolError, e.Text, null); break;
                case AssistantEventKind.Notice: Transcript.Add(TranscriptKind.Notice, e.Text, null); break;
                case AssistantEventKind.Error: Transcript.Add(TranscriptKind.Error, e.Text, null); break;
                case AssistantEventKind.AppNote: Transcript.Add(TranscriptKind.App, e.Text, null); break;
                case AssistantEventKind.Action:
                    if (e.Action != null && e.Action.State == AssistantActionState.Pending && !Session.AutoApply) ActionList.Checked.Add(e.Action.Number);
                    RefreshPlan(); break;
                case AssistantEventKind.Usage: UpdateUsage(); break;
            }
        }

        private static Image Thumbnail(byte[] jpeg)
        {
            if (jpeg == null) return null;
            try { using (var ms = new MemoryStream(jpeg)) using (var img = Image.FromStream(ms)) return new Bitmap(img); }
            catch (ArgumentException) { return null; }
        }

        private void TurnFinished(AssistantConversation current, CancellationTokenSource cancel, AssistantTurnResult result, Exception failure)
        {
            if (runCancel == cancel) runCancel = null;
            cancel.Dispose();
            SetRunning(false);
            if (failure != null) { Program.Log(failure); Transcript.Add(TranscriptKind.Error, failure.Message, null); }
            UpdateUsage(); RefreshPlan();
            if (current.Actions.Any(a => a.AutoApplied) && host.OutfitsChanged != null) host.OutfitsChanged();
            if (host.Status != null)
            {
                if (result == null) host.Status("The Assistant stopped with an error.", Palette.Red);
                else if (result.Status == AssistantTurnStatus.Completed) host.Status(PendingCount() > 0 ? PendingCount() + " change" + (PendingCount() == 1 ? "" : "s") + " waiting for approval in the Plan" : "The Assistant replied · " + result.ModelCalls + " request" + (result.ModelCalls == 1 ? "" : "s"), PendingCount() > 0 ? Palette.Accent : Palette.Muted);
                else host.Status(result.Message, result.Status == AssistantTurnStatus.Cancelled ? Palette.Muted : Palette.Red);
            }
            if (!Program.Headless) Prompt.Editor.Focus();
        }

        internal void Stop()
        {
            if (runCancel != null) { runCancel.Cancel(); StopButton.Enabled = false; }
        }

        internal void Shutdown()
        {
            if (runCancel != null) runCancel.Cancel();
            if (applyCancel != null) applyCancel.Cancel();
        }

        private void SetRunning(bool value)
        {
            running = value;
            UpdateButtons();
            if (value && host.Status != null) host.Status("The Assistant is working…", Palette.Muted);
        }

        internal void UpdateButtons()
        {
            bool hostBusy = host.HostBusy != null && host.HostBusy();
            SendButton.Enabled = !running && !applying;
            StopButton.Enabled = running && runCancel != null && !runCancel.IsCancellationRequested;
            ModelCombo.Enabled = !running && !applying; EffortCombo.Enabled = !running && !applying;
            NewChatButton.Enabled = !running && !applying;
            int chosen = CheckedPending().Count;
            ApplyButton.Enabled = !running && !applying && !hostBusy && chosen > 0;
            ApplyButton.Text = chosen > 1 ? "Apply " + chosen + " selected" : "Apply selected";
            DiscardButton.Enabled = !running && !applying && chosen > 0;
            DiscardButton.Visible = !applying; CancelApplyButton.Visible = applying;
            AutoApply.Enabled = !running && !applying;
        }

        private int PendingCount() { return conversation == null ? 0 : conversation.Toolbox.PendingActions().Count; }
        private List<AssistantAction> CheckedPending()
        {
            if (conversation == null) return new List<AssistantAction>();
            return conversation.Toolbox.PendingActions().Where(a => ActionList.Checked.Contains(a.Number)).ToList();
        }

        internal void RefreshPlan()
        {
            List<AssistantAction> actions = conversation == null ? new List<AssistantAction>() : conversation.Actions.ToList();
            int selected = ActionList.SelectedIndex;
            AssistantAction selectedAction = ActionList.ActionAt(selected);
            ActionList.BeginUpdate();
            try
            {
                ActionList.Items.Clear();
                foreach (var a in actions) ActionList.Items.Add(a);
                if (selectedAction != null && actions.Contains(selectedAction)) ActionList.SelectedItem = selectedAction;
                else if (actions.Count > 0) ActionList.SelectedIndex = actions.Count - 1;
            }
            finally { ActionList.EndUpdate(); }
            ActionList.Checked.RemoveWhere(n => !actions.Any(a => a.Number == n && a.State == AssistantActionState.Pending));
            int pending = actions.Count(a => a.State == AssistantActionState.Pending);
            PlanCaption.Text = pending > 0 ? "PLAN · " + pending + " TO APPROVE" : actions.Count > 0 ? "PLAN · NOTHING TO APPROVE" : "PLAN";
            PlanCaption.ForeColor = pending > 0 ? Palette.Accent : Palette.Muted;
            if (!applying)
            {
                if (lastApplyText != null) { ApplyStatus.Text = lastApplyText; ApplyStatus.ForeColor = lastApplyColor; }
                else { ApplyStatus.Text = actions.Count == 0 ? "Changes the Assistant proposes appear here." : pending > 0 ? "Check actions to run, then Apply selected." : ""; ApplyStatus.ForeColor = Palette.Muted; }
            }
            ShowActionDetail(); UpdateButtons();
        }

        private void ShowActionDetail()
        {
            AssistantAction a = ActionList.ActionAt(ActionList.SelectedIndex);
            if (a == null) { ActionDetail.Text = ""; return; }
            string detail = a.Kind == AssistantActionKind.Install ? "Extra outfit · " + a.Character + " · " + ReplacePlanner.N(a.FileCount, "file") + " · " + Utilities.FriendlyBytes(a.Bytes) + (String.IsNullOrEmpty(a.ReplaceInstalledId) ? " · new outfit" : " · replaces existing outfit")
                : a.Kind == AssistantActionKind.ReplaceInstall ? "Replace original · " + ReplacePlanner.N(a.FileCount, "file") + " · " + Utilities.FriendlyBytes(a.Bytes) + (a.Affects.Length > 0 ? " · " + a.Affects : "")
                : a.Kind == AssistantActionKind.GameModChange || a.Kind == AssistantActionKind.PresetImport ? a.ModeLabel : "";
            ActionDetail.Text = (detail.Length > 0 ? detail + Environment.NewLine : "") + (a.Result.Length > 0 ? a.Result : a.State == AssistantActionState.Pending ? "Not run yet. Restart SF6 after applying to see changes." : "");
            ActionDetail.ForeColor = a.State == AssistantActionState.Failed ? Palette.Red : Palette.Muted;
        }

        internal void ApplySelected()
        {
            List<AssistantAction> chosen = CheckedPending();
            if (running || applying || chosen.Count == 0 || conversation == null) return;
            var cancel = new CancellationTokenSource();
            string message = "Applying " + chosen.Count + " change" + (chosen.Count == 1 ? "" : "s") + "…";
            if (host.BeginWork != null && !host.BeginWork(message, cancel)) { cancel.Dispose(); ApplyStatus.Text = "Wait for the current task to finish, then apply again."; return; }
            applyCancel = cancel; applying = true;
            ApplyStatus.Text = message; ApplyStatus.ForeColor = Palette.Muted; CancelApplyButton.Enabled = true;
            UpdateButtons();
            AssistantConversation current = conversation; int[] numbers = chosen.Select(a => a.Number).ToArray();
            var worker = new BackgroundWorker { WorkerReportsProgress = true };
            worker.DoWork += delegate(object s, DoWorkEventArgs e)
            {
                e.Result = current.Apply(numbers, cancel.Token, delegate(OperationProgress p) { worker.ReportProgress(0, p); });
            };
            worker.ProgressChanged += delegate(object s, ProgressChangedEventArgs e)
            {
                var p = (OperationProgress)e.UserState;
                if (host.ReportProgress != null) host.ReportProgress(p);
                if (!cancel.IsCancellationRequested) ApplyStatus.Text = p.Stage + (p.Total > 0 ? " · " + (int)(p.Completed * 100.0 / p.Total) + "%" : "…");
                CancelApplyButton.Enabled = p.CanCancel && !cancel.IsCancellationRequested;
            };
            worker.RunWorkerCompleted += delegate(object s, RunWorkerCompletedEventArgs e)
            {
                applying = false; applyCancel = null;
                if (host.EndWork != null) host.EndWork(cancel);
                cancel.Dispose(); worker.Dispose();
                if (IsDisposed) return;
                if (e.Error != null) { Program.Log(e.Error); ApplyStatus.Text = e.Error.Message; ApplyStatus.ForeColor = Palette.Red; }
                else
                {
                    var batch = (AssistantBatchResult)e.Result;
                    ApplyStatus.Text = batch.FailedAction != null ? "Stopped at #" + batch.FailedAction.Number + " · " + batch.Succeeded + " of " + batch.Requested + " applied" : batch.Cancelled ? "Cancelled · " + batch.Succeeded + " of " + batch.Requested + " applied" : "✓ Applied " + batch.Succeeded + " of " + batch.Requested + " · restart SF6 to see changes";
                    ApplyStatus.ForeColor = batch.FailedAction != null ? Palette.Red : batch.Succeeded > 0 ? Palette.Green : Palette.Muted;
                    if (host.Status != null) host.Status(ApplyStatus.Text, ApplyStatus.ForeColor);
                    if (batch.Succeeded > 0 && host.OutfitsChanged != null) host.OutfitsChanged();
                }
                lastApplyText = ApplyStatus.Text; lastApplyColor = ApplyStatus.ForeColor;
                RefreshPlan();
            };
            worker.RunWorkerAsync();
        }

        internal void DiscardSelected()
        {
            List<AssistantAction> chosen = CheckedPending();
            if (running || applying || chosen.Count == 0 || conversation == null) return;
            conversation.Discard(chosen.Select(a => a.Number));
            RefreshPlan();
            lastApplyText = "Discarded " + chosen.Count + " action" + (chosen.Count == 1 ? "" : "s") + ". The Assistant is told on your next message."; lastApplyColor = Palette.Muted;
            ApplyStatus.Text = lastApplyText; ApplyStatus.ForeColor = lastApplyColor;
        }

        internal void NewChat()
        {
            if (running || applying) return;
            int pending = PendingCount();
            // 2.4.1: no popup: New chat is the consent; the status says what was dropped (nothing had been applied).
            if (conversation != null) finishedCost += conversation.Usage.TotalCost;
            conversation = null; ActionList.Checked.Clear(); lastApplyText = null;
            Transcript.Clear(); RefreshPlan(); UpdateUsage(); UpdateEmptyState();
            if (host.Status != null) host.Status(pending > 0 ? "New chat · " + ReplacePlanner.N(pending, "unapplied change") + " dropped." : "New chat · describe what you want.", Palette.Muted);
        }

        private void UpdateUsage()
        {
            decimal chat = conversation == null ? 0m : conversation.Usage.TotalCost;
            string chatText = conversation == null ? "No usage yet" : conversation.Usage.Summary().Replace(" (estimate)", "");
            UsageLabel.Text = chatText + (finishedCost > 0 ? " · session ≈ $" + (finishedCost + chat).ToString("0.0000", CultureInfo.InvariantCulture) : "") + "  (estimate)";
            var lines = new List<string> { "Estimated from published prices; your Anthropic bill is final." };
            if (conversation != null) foreach (var u in conversation.Usage.Snapshot())
            {
                AssistantModel m = AssistantModels.Find(u.Model);
                lines.Add((m == null ? u.Model : m.DisplayName) + ": " + u.Requests + (u.Requests == 1 ? " request · " : " requests · ") + u.InputTokens + " in · " + u.CacheReadTokens + " cache read · " + u.CacheWriteTokens + " cache write · " + u.OutputTokens + " out · ≈ $" + u.Cost.ToString("0.0000", CultureInfo.InvariantCulture));
            }
            usageBreakdown = String.Join("\n", lines);
            UsageLabel.AccessibleDescription = usageBreakdown;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { Shutdown(); }
            base.Dispose(disposing);
        }
    }
}
