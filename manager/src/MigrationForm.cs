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
    // Settings → Migrate installed mods: review the other manager's active mods (step 1), wait while the user
    // uninstalls them there (step 2), then install the reviewed layers here in the same order (step 3).
    internal sealed class MigrationForm : Form
    {
        private readonly ReplaceEngine engine;
        private readonly List<LibraryEntry> library;
        private readonly string sevenZip;
        private readonly Label title, status;
        private readonly MigrationSteps steps;
        private readonly ListBox rows;
        private readonly TextBox details;
        private readonly FlatButton action, locate, folder, forget, close;
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 1800 };
        private ModMigration plan;
        private bool confirmingForget;
        private bool working, waiting, completed, polling, pollFailed, unreadablePlan;
        internal bool Completed { get { return completed; } }
        internal string LibraryFolder { get { return plan == null ? null : Path.Combine(plan.Root, "Mods"); } }

        internal MigrationForm(ReplaceEngine engine, List<LibraryEntry> library, string sevenZip)
        {
            this.engine = engine; this.library = library; this.sevenZip = sevenZip;
            SuspendLayout();
            AutoScaleDimensions = new SizeF(96,96); AutoScaleMode = AutoScaleMode.Dpi;
            Text = "Migrate from another mod manager"; StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(840,580); MinimumSize = new Size(760,480); Padding = new Padding(18,14,18,12);
            BackColor = Palette.Surface; ForeColor = Palette.Text; Font = Palette.Normal; ShowInTaskbar = false;
            AppBranding.Apply(this);
            steps = new MigrationSteps { Dock = DockStyle.Top, Height = 40, AccessibleName = "Migration steps" };
            title = new Label { Dock = DockStyle.Top, Height = 30, Font = Palette.Strong, UseMnemonic = false, TextAlign = ContentAlignment.MiddleLeft, Text = "Reading your other mod manager's setup…" };
            status = new Label { Dock = DockStyle.Top, Height = 48, ForeColor = Palette.Muted, Font = Palette.Small, UseMnemonic = false, AutoEllipsis = true };
            rows = new ListBox { Dock = DockStyle.Fill, BackColor = Palette.Background, ForeColor = Palette.Text,
                Font = Palette.Normal, BorderStyle = BorderStyle.None, DrawMode = DrawMode.OwnerDrawFixed, IntegralHeight = false, AccessibleName = "Active mods in install order" };
            details = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.None, WordWrap = true,
                BorderStyle = BorderStyle.None, BackColor = Palette.Surface, ForeColor = Palette.Muted, Font = Palette.Small, AccessibleName = "Selected mod details", TabStop = false };
            // The details sit under a thin rule, inset like the rows above them.
            var detailsHost = new Panel { Dock = DockStyle.Bottom, Height = 112, Padding = new Padding(10,10,0,0) };
            detailsHost.Paint += delegate(object sender, PaintEventArgs e) { using (var pen = new Pen(Palette.Border)) e.Graphics.DrawLine(pen,0,0,detailsHost.Width,0); };
            detailsHost.Controls.Add(details);
            details.ClientSizeChanged += delegate { FitDetailsScroll(); };
            NativeDpiFont.Attach(rows); NativeDpiFont.Attach(details);
            rows.HandleCreated += delegate { rows.ItemHeight = DpiMetrics.Px(rows,34); };
            rows.DpiChangedAfterParent += delegate { rows.ItemHeight = DpiMetrics.Px(rows,34); };
            rows.DrawItem += DrawRow;
            rows.SelectedIndexChanged += delegate { ShowDetails(); };
            var bar = new Panel { Dock = DockStyle.Bottom, Height = 50, Padding = new Padding(0,12,0,0) };
            var left = new FlowLayoutPanel { Dock = DockStyle.Left, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
            var right = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
            action = AddButton(right,"Start migration",true,160,Advance,new Padding(8,0,0,0));
            close = AddButton(right,"Close",false,96,delegate { if (confirmingForget) ToggleForget(); else Close(); },new Padding(8,0,0,0));
            locate = AddButton(left,"Locate archive…",false,132,delegate { Locate(false); },new Padding(0,0,8,0));
            folder = AddButton(left,"Locate folder…",false,124,delegate { Locate(true); },new Padding(0,0,8,0));
            forget = AddButton(left,"Forget plan…",false,118,ToggleForget,new Padding(0,0,8,0));
            bar.Controls.Add(left); bar.Controls.Add(right);
            Controls.Add(rows); Controls.Add(detailsHost); Controls.Add(bar); Controls.Add(status); Controls.Add(title); Controls.Add(steps);
            CancelButton = close;
            DarkTheme.Apply(this); ResumeLayout(true); PerformAutoScale();
            Buttons();
            Shown += delegate { rows.Focus(); Scan(); };
            FormClosing += delegate(object sender, FormClosingEventArgs e) { if (working) e.Cancel = true; };
            FormClosed += delegate { timer.Dispose(); };
            timer.Tick += delegate { Poll(); };
        }

        private static FlatButton AddButton(FlowLayoutPanel bar, string text, bool primary, int width, Action click, Padding margin)
        {
            var b = new FlatButton(text, primary) { Width = width, Height = 36, Font = Palette.Small, Margin = margin };
            b.Click += delegate { click(); }; bar.Controls.Add(b); return b;
        }

        private async void Run(Action job, Action success)
        {
            if (working) return;
            working = true; Buttons();
            var progress = new Progress<OperationProgress>(p => { if (!IsDisposed && working) SetStatus(p.Stage +
                (p.Total > 0 ? " · " + Math.Min(p.Completed + 1,p.Total) + " of " + p.Total : ""), false); });
            try
            {
                await Task.Run(delegate { using (OperationContext.Begin(CancellationToken.None, ((IProgress<OperationProgress>)progress).Report)) job(); });
                working = false;
                success();
            }
            catch (Exception ex)
            {
                working = false;
                if (!IsDisposed) ShowError(ex.GetBaseException().Message);
            }
            finally { working = false; if (!IsDisposed) { Buttons(); rows.Invalidate(); } }
        }

        // An error keeps the current step: the scan can run again, the preview stays editable, and a saved
        // plan waits for Retry migration.
        private void ShowError(string message)
        {
            timer.Stop();
            if (unreadablePlan) title.Text = "Can't read the saved migration plan";
            else if (plan == null) title.Text = "Can't read your other mod manager's setup";
            else if (waiting) { pollFailed = true; title.Text = "Migration paused"; steps.Step = 1; }   // Retry waits for the other manager again
            else title.Text = "Not ready to migrate yet";
            steps.Visible = !unreadablePlan;
            SetStatus(message, true);
            // The whole message, selectable, when the status line cuts it short.
            Size need = TextRenderer.MeasureText(message, status.Font, new Size(Math.Max(1, status.Width), Int32.MaxValue), TextFormatFlags.WordBreak);
            if (need.Height > status.Height) { details.Text = message; FitDetailsScroll(); }
            Buttons();
        }

        private void SetStatus(string text, bool error)
        {
            status.Text = text; status.ForeColor = error ? Palette.Red : Palette.Muted;
        }

        private void Scan()
        {
            Run(delegate {
                unreadablePlan = false;
                if (File.Exists(ModMigration.Pending(engine)))
                {
                    ModMigration saved = null;
                    try { saved = ReplaceEngine.ReadJsonShared<ModMigration>(ModMigration.Pending(engine)); }
                    catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); }
                    unreadablePlan = saved == null || String.IsNullOrEmpty(saved.Id) || saved.Rows == null || saved.Rows.Count == 0;
                    if (unreadablePlan) throw new IOException("The file is damaged or from another version. Forget it to start over. Mods you already uninstalled in your other mod manager need installing again.");
                    plan = saved; waiting = true;
                }
                else
                {
                    var roots = engine.Fluffy().Roots.Select(r => Path.Combine(r, "Mods")).Where(Directory.Exists)
                        .Where(r => !library.Any(l => String.Equals(l.LibraryRoot,r,StringComparison.OrdinalIgnoreCase))).ToList();
                    var entries = roots.Count == 0 ? library : library.Concat(Library.Scan(roots, sevenZip)).ToList();
                    plan = ModMigration.Scan(engine, entries, sevenZip);
                }
            }, delegate {
                RenderRows();
                if (waiting) WaitForHandoff();
            });
        }

        private bool Fixable(MigrationRow r) { return ModMigration.Supports(r.Type) && !r.Files.Any(ReplaceEngine.IsModPak); }

        // The state shown at the right of a row, and its colour.
        private string RowState(MigrationRow r, out Color color)
        {
            if (completed) { color = Palette.Green; return "✓ Moved"; }
            if (r.Problem != null) { color = Fixable(r) ? Palette.Accent : Palette.Red; return Fixable(r) ? "Needs attention" : "Can't migrate"; }
            if (waiting) { color = Palette.Muted; return "Saved"; }
            color = Palette.Green; return "Ready";
        }

        private void DrawRow(object sender, DrawItemEventArgs e)
        {
            bool selected = (e.State & DrawItemState.Selected) != 0;
            using (var brush = new SolidBrush(selected ? Palette.Selected : Palette.Background)) e.Graphics.FillRectangle(brush,e.Bounds);
            if (plan == null || e.Index < 0 || e.Index >= plan.Rows.Count) return;
            var r = plan.Rows[e.Index];
            Color stateColor; string state = RowState(r,out stateColor);
            const TextFormatFlags flags = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
            var bounds = Rectangle.Inflate(e.Bounds,-DpiMetrics.Px(rows,12),0);
            int stateWidth = TextRenderer.MeasureText(e.Graphics,state,Palette.Small,new Size(Int32.MaxValue,bounds.Height),flags).Width;
            TextRenderer.DrawText(e.Graphics,state,Palette.Small,new Rectangle(bounds.Right - stateWidth,bounds.Top,stateWidth,bounds.Height),stateColor,flags);
            string number = (e.Index + 1) + ".";
            int numberWidth = DpiMetrics.Px(rows,30);
            TextRenderer.DrawText(e.Graphics,number,rows.Font,new Rectangle(bounds.Left,bounds.Top,numberWidth,bounds.Height),Palette.Muted,flags);
            var name = new Rectangle(bounds.Left + numberWidth,bounds.Top,Math.Max(0,bounds.Width - numberWidth - stateWidth - DpiMetrics.Px(rows,16)),bounds.Height);
            TextRenderer.DrawText(e.Graphics,r.Name,rows.Font,name,selected ? Palette.Accent : Palette.Text,flags | TextFormatFlags.EndEllipsis);
        }

        private void RenderRows()
        {
            int selected = rows.SelectedIndex;
            rows.BeginUpdate(); rows.Items.Clear();
            for (int i = 0; i < plan.Rows.Count; i++)
            {
                Color unused; var r = plan.Rows[i];
                rows.Items.Add((i + 1) + ". " + r.Name + ", " + RowState(r,out unused));   // the accessible text; DrawRow paints the row
            }
            rows.EndUpdate();
            int unresolved = plan.Rows.Count(r => r.Problem != null);
            rows.SelectedIndex = plan.Rows.Count == 0 ? -1 : unresolved > 0 && !waiting ? plan.Rows.FindIndex(r => r.Problem != null) : Math.Max(0,Math.Min(selected,plan.Rows.Count - 1));
            if (!waiting && !completed)
            {
                title.Text = "Review the mods to move here";
                SetStatus(ReplacePlanner.N(plan.Rows.Count, "active mod") + " in install order, so later ones win shared files." +
                    (unresolved == 0 ? " Everything is located: Start migration checks each one against the game." :
                    " " + unresolved + (unresolved == 1 ? " needs attention: select it to see why." : " need attention: select one to see why.")), false);
            }
            steps.Step = completed ? 3 : waiting ? 1 : 0; steps.Visible = true;
            ShowDetails(); Buttons();
        }

        private void ShowDetails()
        {
            if (plan == null || rows.SelectedIndex < 0 || rows.SelectedIndex >= plan.Rows.Count) { details.Text = ""; FitDetailsScroll(); return; }
            var r = plan.Rows[rows.SelectedIndex];
            var lines = new List<string>();
            if (completed) lines.Add("Installed here as a replacement. It is on the Installed page.");
            if (r.Problem != null)
            {
                lines.Add(r.Problem);
                if (Fixable(r) && !waiting) lines.Add("Use Locate archive or Locate folder to pick the download it was installed from.");
            }
            else
            {
                lines.Add("Source: " + r.Source);
                if (!String.IsNullOrEmpty(r.Component)) lines.Add("Option: " + r.Component);
            }
            string shared = plan.SharedDetails(r, !completed);
            if (shared.Length > 0) { lines.Add(""); lines.Add("Shared files and the mod that wins each:"); lines.Add(shared); }
            details.Text = String.Join(Environment.NewLine, lines);
            FitDetailsScroll();
            Buttons();
        }

        // A scroll bar only when the text overflows.
        private void FitDetailsScroll()
        {
            if (!details.IsHandleCreated) return;
            Size need = TextRenderer.MeasureText(details.Text + " ", details.Font, new Size(Math.Max(1,details.ClientSize.Width - DpiMetrics.Px(details,20)), Int32.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);
            ScrollBars wanted = need.Height > details.ClientSize.Height ? ScrollBars.Vertical : ScrollBars.None;
            if (details.ScrollBars != wanted) details.ScrollBars = wanted;
        }

        private void Buttons()
        {
            if (action == null) return;
            var row = plan == null || rows.SelectedIndex < 0 || rows.SelectedIndex >= plan.Rows.Count ? null : plan.Rows[rows.SelectedIndex];
            bool preview = plan != null && !waiting && !completed;
            locate.Visible = folder.Visible = preview && row != null && Fixable(row);
            forget.Visible = (waiting || unreadablePlan) && !completed && !confirmingForget;
            locate.Enabled = folder.Enabled = forget.Enabled = !working;
            close.Text = confirmingForget ? "Keep plan" : "Close";
            close.Enabled = !working;
            action.Primary = !confirmingForget; action.Danger = confirmingForget;
            action.Text = completed ? "View installed mods" : confirmingForget ? "Forget plan" : waiting ? (pollFailed ? "Retry migration" : "Check now") : plan == null ? (unreadablePlan ? "Try again" : "Scan again") : "Start migration";
            action.Enabled = !working && (completed || waiting || plan == null || plan.Rows.All(r => r.Problem == null));
            action.Invalidate();
        }

        private void Locate(bool directory)
        {
            if (working || rows.SelectedIndex < 0) return;
            string path;
            if (directory)
            {
                using (var d = new FolderBrowserDialog { Description = "Choose the mod folder it was installed from" })
                { if (d.ShowDialog(this) != DialogResult.OK) return; path = d.SelectedPath; }
            }
            else using (var d = new OpenFileDialog { Title = "Locate the archive it was installed from", Filter = "Mod archives|*.zip;*.7z;*.rar" })
            { if (d.ShowDialog(this) != DialogResult.OK) return; path = d.FileName; }
            var row = plan.Rows[rows.SelectedIndex];
            SetStatus("Reading " + Path.GetFileName(path) + "…", false);
            Run(delegate { ModMigration.Match(row,PackageReader.Load(path,sevenZip),null); }, RenderRows);
        }

        private void Advance()
        {
            if (completed) { DialogResult = DialogResult.OK; return; }
            if (confirmingForget)
            {
                ModMigration saved = plan;   // null: a damaged plan file, deleted unread
                Run(delegate { if (saved == null) ModMigration.Discard(engine); else saved.Forget(engine); }, delegate { waiting = false; DialogResult = DialogResult.Cancel; });
                return;
            }
            if (plan == null) { Scan(); return; }
            if (!waiting)
            {
                title.Text = "Checking your mods";
                SetStatus("Comparing each source with the game files and saving your migration plan…", false);
                Run(delegate { plan.SaveHandoff(engine,sevenZip); }, delegate { waiting = true; RenderRows(); WaitForHandoff(); }); return;
            }
            WaitForHandoff(); Poll();
        }

        private void ToggleForget()
        {
            if (working) return;
            confirmingForget = !confirmingForget;
            if (confirmingForget)
            {
                timer.Stop(); title.Text = "Forget this migration plan?";
                SetStatus("The saved list of mods is deleted. If you already uninstalled them in your other mod manager, install them again yourself.", false);
                Buttons();
            }
            else if (plan == null) { Buttons(); Scan(); }
            else WaitForHandoff();
        }

        private void WaitForHandoff()
        {
            waiting = true; pollFailed = false;
            steps.Step = 1;
            title.Text = plan.CompatibilityChecked ? "Your setup is saved" : "Checking the earlier migration plan";
            SetStatus(plan.CompatibilityChecked ? ModMigration.UninstallThere : "Keep any active mods installed while the Manager checks this plan.", false);
            timer.Start(); Buttons(); rows.Invalidate();
        }

        private async void Poll()
        {
            if (working || polling || !waiting || pollFailed || confirmingForget) return;
            polling = true;
            try
            {
                string wait = await Task.Run(delegate { return engine.LoadState().LastMigration == plan.Id ? null : plan.Waiting(engine); });
                if (IsDisposed || Disposing || confirmingForget || pollFailed) return;
                if (wait != null) { SetStatus(wait, false); return; }
                timer.Stop(); title.Text = "Installing your mods here"; steps.Step = 2;
                SetStatus("Checking the original game files…", false);
                Run(delegate { plan.Restore(engine,sevenZip); }, delegate {
                    completed = true; waiting = false; steps.Step = 3; title.Text = "Migration complete";
                    SetStatus(ReplacePlanner.N(plan.Rows.Count,"mod") + " moved here as replacements, in the same order. The last one in this list is on top of the Installed page.", false);
                    RenderRows();
                });
            }
            catch (Exception ex) { if (!IsDisposed) ShowError(ex.GetBaseException().Message); }
            finally { polling = false; }
        }
    }

    // The three steps of a migration: done ones get a check, the current one the accent.
    internal sealed class MigrationSteps : Control
    {
        private static readonly string[] Names = { "Review", "Uninstall in your other manager", "Install here" };
        private int step;
        internal int Step { get { return step; } set { if (step == value) return; step = value; AccessibleDescription = value >= Names.Length ? "Done" : "Step " + (value + 1) + " of 3: " + Names[value]; Invalidate(); } }
        internal MigrationSteps()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Font = Palette.Small; AccessibleRole = AccessibleRole.StaticText; AccessibleDescription = "Step 1 of 3: Review";
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            using (var back = new SolidBrush(Parent == null ? Palette.Surface : Parent.BackColor)) e.Graphics.FillRectangle(back, ClientRectangle);
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            const TextFormatFlags flags = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
            int x = 0, dot = DpiMetrics.Px(this, 20), gap = DpiMetrics.Px(this, 8), line = DpiMetrics.Px(this, 28), mid = Height / 2 - DpiMetrics.Px(this, 2);
            for (int i = 0; i < Names.Length; i++)
            {
                bool done = i < step, current = i == step;
                Color fill = done ? Palette.Green : current ? Palette.Accent : Palette.Raised;
                var circle = new Rectangle(x, mid - dot / 2, dot, dot);
                using (var brush = new SolidBrush(fill)) e.Graphics.FillEllipse(brush, circle);
                TextRenderer.DrawText(e.Graphics, done ? "✓" : (i + 1).ToString(), Palette.Tiny, circle, done || current ? Palette.Background : Palette.Muted,
                    flags | TextFormatFlags.HorizontalCenter);
                x += dot + gap;
                Size text = TextRenderer.MeasureText(e.Graphics, Names[i], Font, new Size(Int32.MaxValue, Height), flags);
                TextRenderer.DrawText(e.Graphics, Names[i], Font, new Rectangle(x, mid - Height / 2, text.Width + 1, Height), current ? Palette.Text : done ? Palette.Green : Palette.Muted, flags);
                x += text.Width + gap;
                if (i == Names.Length - 1) break;
                using (var pen = new Pen(done ? Palette.Green : Palette.Border, Math.Max(1F, DpiMetrics.Px(this, 1)))) e.Graphics.DrawLine(pen, x, mid, x + line, mid);
                x += line + gap;
            }
        }
    }
}
