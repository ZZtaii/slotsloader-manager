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
    internal sealed class MigrationForm : Form
    {
        private readonly ReplaceEngine engine;
        private readonly List<LibraryEntry> library;
        private readonly string sevenZip;
        private readonly Label title, status;
        private readonly ListBox rows;
        private readonly TextBox details;
        private readonly FlatButton action, locate, folder, close;
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 1800 };
        private ModMigration plan;
        private bool confirmingForget;
        private bool working, waiting, completed, polling, pollFailed;
        internal bool Completed { get { return completed; } }
        internal string LibraryFolder { get { return plan == null ? null : Path.Combine(plan.Root, "Mods"); } }

        internal MigrationForm(ReplaceEngine engine, List<LibraryEntry> library, string sevenZip)
        {
            this.engine = engine; this.library = library; this.sevenZip = sevenZip;
            SuspendLayout();
            AutoScaleDimensions = new SizeF(96,96); AutoScaleMode = AutoScaleMode.Dpi;
            Text = "Migrate from another mod manager"; StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(820,545); MinimumSize = new Size(740,450); Padding = new Padding(18);
            BackColor = Palette.Surface; ForeColor = Palette.Text; Font = Palette.Normal; ShowInTaskbar = false;
            AppBranding.Apply(this);
            title = new Label { Dock = DockStyle.Top, Height = 42, Font = Palette.Strong, Text = "Scanning your SF6 mod setup…" };
            status = new Label { Dock = DockStyle.Top, Height = 65, ForeColor = Palette.Muted, UseMnemonic = false, AutoEllipsis = true };
            rows = new ListBox { Dock = DockStyle.Fill, BackColor = Palette.Background, ForeColor = Palette.Text,
                Font = Palette.Normal, BorderStyle = BorderStyle.None, DrawMode = DrawMode.OwnerDrawFixed, IntegralHeight = false, AccessibleName = "Installed mods in install order" };
            details = new TextBox { Dock = DockStyle.Bottom, Height = 100, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
                BorderStyle = BorderStyle.None, BackColor = Palette.Surface, ForeColor = Palette.Muted, Font = Palette.Small, AccessibleName = "Selected mod details" };
            NativeDpiFont.Attach(rows); NativeDpiFont.Attach(details);
            rows.HandleCreated += delegate { rows.ItemHeight = DpiMetrics.Px(rows,32); };
            rows.DpiChangedAfterParent += delegate { rows.ItemHeight = DpiMetrics.Px(rows,32); };
            rows.DrawItem += delegate(object sender, DrawItemEventArgs e) {
                bool selected = (e.State & DrawItemState.Selected) != 0;
                using (var brush = new SolidBrush(selected ? Palette.Selected : Palette.Background)) e.Graphics.FillRectangle(brush,e.Bounds);
                if (e.Index < 0 || e.Index >= rows.Items.Count) return;
                var bounds = Rectangle.Inflate(e.Bounds,-DpiMetrics.Px(rows,10),0);
                TextRenderer.DrawText(e.Graphics,rows.Items[e.Index].ToString(),rows.Font,bounds,selected ? Palette.Accent : Palette.Text,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            };
            rows.SelectedIndexChanged += delegate { ShowDetails(); };
            var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 52, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
            action = AddButton(bar,"Start migration",true,172,Advance);
            close = AddButton(bar,"Close",false,78,Close);
            locate = AddButton(bar,"Locate archive…",false,132,delegate { Locate(false); });
            folder = AddButton(bar,"Locate folder…",false,126,delegate { if (waiting) ToggleForget(); else Locate(true); });
            Controls.Add(rows); Controls.Add(details); Controls.Add(bar); Controls.Add(status); Controls.Add(title);
            DarkTheme.Apply(this); ResumeLayout(true); PerformAutoScale();
            Shown += delegate { Scan(); };
            FormClosing += delegate(object sender, FormClosingEventArgs e) { if (working) e.Cancel = true; };
            FormClosed += delegate { timer.Dispose(); };
            timer.Tick += delegate { Poll(); };
        }

        private static FlatButton AddButton(FlowLayoutPanel bar, string text, bool primary, int width, Action click)
        {
            var b = new FlatButton(text, primary) { Width = width, Height = 36, Font = Palette.Small, Margin = new Padding(6,8,0,0) };
            b.Click += delegate { click(); }; bar.Controls.Add(b); return b;
        }

        private async void Run(Action job, Action success)
        {
            if (working) return;
            working = true; Buttons();
            var progress = new Progress<OperationProgress>(p => { if (!IsDisposed && working) status.Text = p.Stage +
                (p.Total > 0 ? " · " + Math.Min(p.Completed + 1,p.Total) + " of " + p.Total : ""); });
            try
            {
                await Task.Run(delegate { using (OperationContext.Begin(CancellationToken.None, ((IProgress<OperationProgress>)progress).Report)) job(); });
                success();
            }
            catch (Exception ex)
            {
                timer.Stop(); pollFailed = true;
                status.Text = ex.GetBaseException().Message; details.Text = status.Text;
            }
            finally { working = false; Buttons(); }
        }

        private void Scan()
        {
            Run(delegate {
                if (File.Exists(ModMigration.Pending(engine)))
                {
                    var saved = ReplaceEngine.ReadJsonShared<ModMigration>(ModMigration.Pending(engine));
                    if (saved == null || String.IsNullOrEmpty(saved.Id) || saved.Rows == null || saved.Rows.Count == 0)
                        throw new IOException("The saved migration plan could not be read.");
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

        private void RenderRows()
        {
            int selected = rows.SelectedIndex;
            rows.BeginUpdate(); rows.Items.Clear();
            for (int i = 0; i < plan.Rows.Count; i++)
            {
                var r = plan.Rows[i]; rows.Items.Add((i + 1) + ".  " + r.Name + (r.Problem == null ? "" : "  ·  Needs attention"));
            }
            rows.EndUpdate();
            int unresolved = plan.Rows.Count(r => r.Problem != null);
            rows.SelectedIndex = unresolved > 0 ? plan.Rows.FindIndex(r => r.Problem != null) : Math.Max(0,selected);
            title.Text = "We found your SF6 mod setup";
            status.Text = ReplacePlanner.N(plan.Rows.Count, "active mod") + " in install order" +
                (unresolved == 0 ? " · Sources located. Start migration to verify them." : " · " + unresolved + (unresolved == 1 ? " needs attention" : " need attention")) +
                Environment.NewLine + "Select a mod to review its source and shared files.";
            ShowDetails(); Buttons();
        }

        private void ShowDetails()
        {
            if (plan == null || rows.SelectedIndex < 0) { details.Text = ""; return; }
            var r = plan.Rows[rows.SelectedIndex];
            string shared = plan.SharedDetails(r);
            details.Text = (r.Problem ?? (r.Source + (String.IsNullOrEmpty(r.Component) ? "" : Environment.NewLine + "Option · " + r.Component))) +
                (shared.Length == 0 ? "" : Environment.NewLine + "Shared files and their winners" + Environment.NewLine + shared);
            Buttons();
        }

        private void Buttons()
        {
            if (action == null) return;
            var row = plan == null || rows.SelectedIndex < 0 ? null : plan.Rows[rows.SelectedIndex];
            bool supported = row != null && ModMigration.Supports(row.Type) && !row.Files.Any(ReplaceEngine.IsModPak);
            locate.Visible = !waiting && !completed && supported;
            folder.Visible = locate.Visible || waiting;
            folder.Text = waiting ? (confirmingForget ? "Keep plan" : "Forget plan…") : "Locate folder…";
            locate.Enabled = folder.Enabled = !working;
            close.Enabled = !working;
            action.Text = completed ? "View installed mods" : waiting ? (pollFailed && !confirmingForget ? "Retry migration" : (confirmingForget ? "Forget saved plan" : "Check now")) : plan == null ? "Scan again" : "Start migration";
            action.Enabled = !working && (completed || waiting || plan == null || plan.Rows.All(r => r.Problem == null));
        }

        private void Locate(bool directory)
        {
            if (working || rows.SelectedIndex < 0) return;
            string path;
            if (directory)
            {
                using (var d = new FolderBrowserDialog { Description = "Choose the source mod folder" })
                { if (d.ShowDialog(this) != DialogResult.OK) return; path = d.SelectedPath; }
            }
            else using (var d = new OpenFileDialog { Title = "Locate source archive", Filter = "Mod archives|*.zip;*.7z;*.rar" })
            { if (d.ShowDialog(this) != DialogResult.OK) return; path = d.FileName; }
            var row = plan.Rows[rows.SelectedIndex];
            Run(delegate { ModMigration.Match(row,PackageReader.Load(path,sevenZip),null); }, RenderRows);
        }

        private void Advance()
        {
            if (completed) { DialogResult = DialogResult.OK; return; }
            if (plan == null) { Scan(); return; }
            if (!waiting)
            {
                status.Text = "Verifying sources and saving your migration plan…";
                Run(delegate { plan.SaveHandoff(engine,sevenZip); }, WaitForHandoff); return;
            }
            if (confirmingForget)
            {
                Run(delegate { plan.Forget(engine); }, delegate { waiting = false; DialogResult = DialogResult.Cancel; });
                return;
            }
            WaitForHandoff(); Poll();
        }

        private void ToggleForget()
        {
            confirmingForget = !confirmingForget;
            if (confirmingForget)
            {
                timer.Stop(); title.Text = "Forget this migration plan?";
                status.Text = "This removes the saved checklist. If you already uninstalled mods, you will need to install them yourself.";
                Buttons();
            }
            else WaitForHandoff();
        }

        private void WaitForHandoff()
        {
            waiting = true; pollFailed = false;
            title.Text = plan.CompatibilityChecked ? "Your setup is saved" : "Checking the earlier migration plan";
            status.Text = plan.CompatibilityChecked ? "In the other mod manager, choose Uninstall all mods for Street Fighter 6, then close it. Migration will continue automatically." :
                "Keep any active mods installed while the Manager checks this plan.";
            timer.Start(); Buttons();
        }

        private async void Poll()
        {
            if (working || polling || !waiting || pollFailed || confirmingForget) return;
            polling = true;
            try
            {
                string wait = await Task.Run(delegate { return engine.LoadState().LastMigration == plan.Id ? null : plan.Waiting(engine); });
                if (IsDisposed || Disposing || confirmingForget) return;
                if (wait != null) { status.Text = wait; return; }
                timer.Stop(); title.Text = "Restoring your mods"; status.Text = "Verifying the handoff…";
                Run(delegate { plan.Restore(engine,sevenZip); }, delegate {
                    completed = true; waiting = false; title.Text = "Migration complete";
                    status.Text = ReplacePlanner.N(plan.Rows.Count,"mod") + " migrated in the original install order.";
                });
            }
            catch (Exception ex) { if (!IsDisposed) { timer.Stop(); pollFailed = true; status.Text = ex.Message; Buttons(); } }
            finally { polling = false; }
        }
    }
}
