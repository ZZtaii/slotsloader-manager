using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SlotsInstaller
{
    // 1.0.20 Settings → General → Convert RAR to ZIP: the progress window. It converts the mods one by one
    // (ArchiveConvert.Convert), shows the mod, its number, a bar and the time left; Cancel stops after the mod being
    // converted (each mod is converted whole or not at all). At the end it lists what was left out or failed, and offers
    // to move the originals of the converted mods to the Recycle Bin, after a second question. Nothing is deleted
    // otherwise.
    internal sealed class ConvertForm : Form
    {
        internal sealed class Host
        {
            internal string SevenZip;
            internal ConvertRecords.Context Records;
            internal string[] Libraries;
            internal List<KeyValuePair<string, string>> Protected;   // LibraryDelete's protected paths
            internal IRecycleBin Bin;
        }

        private readonly Host host;
        private readonly List<ArchiveConvert.Candidate> work;
        private readonly int leftOut;
        private readonly Label title, current, numbers;
        private readonly ConvertBar bar;
        private readonly TextBox log;
        private readonly FlatButton cancel, recycle, close;
        private bool running, stopRequested, recycling, recycled;
        internal readonly List<ArchiveConvert.Result> Results = new List<ArchiveConvert.Result>();
        internal readonly List<string> Recycled = new List<string>();
        internal bool Changed { get { return Results.Any(r => r.Done) || Recycled.Count > 0; } }
        internal static Action<ConvertForm> ShownForChecks;   // checks: drive the window without a person
        internal static Action<ConvertForm, int> BeforeItemForChecks;   // checks: before mod number i (0-based) starts
        internal bool Busy { get { return running || recycling; } }
        internal bool CanRecycle { get { return recycle.Visible && recycle.Enabled; } }
        internal string Progress { get { return title.Text + " | " + current.Text + " | " + numbers.Text; } }
        internal string LogText { get { return log.Text; } }
        internal void ClickRecycle() { AskRecycle(); }

        internal ConvertForm(Host host, List<ArchiveConvert.Candidate> candidates)
        {
            this.host = host;
            work = candidates.Where(c => c.Skip == null).ToList();
            leftOut = candidates.Count - work.Count;
            SuspendLayout();
            AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
            Text = "Convert RAR and 7Z to ZIP"; StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(640, 420); MinimumSize = new Size(520, 360); Padding = new Padding(18, 14, 18, 12);
            BackColor = Palette.Surface; ForeColor = Palette.Text; Font = Palette.Normal; ShowInTaskbar = false; MaximizeBox = false;
            AppBranding.Apply(this);
            title = new Label { Dock = DockStyle.Top, Height = 30, Font = Palette.Strong, UseMnemonic = false, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
            current = new Label { Dock = DockStyle.Top, Height = 26, UseMnemonic = false, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, AccessibleName = "Mod being converted" };
            bar = new ConvertBar { Dock = DockStyle.Top, Height = 8, AccessibleName = "Progress" };
            numbers = new Label { Dock = DockStyle.Top, Height = 30, ForeColor = Palette.Muted, Font = Palette.Small, UseMnemonic = false, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
            var gap = new Panel { Dock = DockStyle.Top, Height = 8 };
            log = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = true, BorderStyle = BorderStyle.None,
                BackColor = Palette.Raised, ForeColor = Palette.Muted, Font = Palette.Small, AccessibleName = "Mods left out or not converted" };
            NativeDpiFont.Attach(log);
            var logFrame = new Panel { Dock = DockStyle.Fill, BackColor = Palette.Raised, Padding = new Padding(10, 8, 4, 8) };
            logFrame.Controls.Add(log);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 50, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Padding = new Padding(0, 12, 0, 0) };
            close = AddButton(buttons, "Close", false, 96, delegate { Close(); });
            cancel = AddButton(buttons, "Cancel", false, 110, RequestStop);
            recycle = AddButton(buttons, "Move originals to " + BinName, true, 236, AskRecycle);
            Controls.Add(logFrame); Controls.Add(gap); Controls.Add(numbers); Controls.Add(bar); Controls.Add(current); Controls.Add(title); Controls.Add(buttons);
            CancelButton = close;
            DarkTheme.Apply(this); ResumeLayout(true); PerformAutoScale();
            if (leftOut > 0) Note(leftOut + " left out:" + Environment.NewLine + String.Join(Environment.NewLine, candidates.Where(c => c.Skip != null).Select(c => "  " + c.Name + ": " + c.Skip.TrimEnd('.'))));
            Buttons();
            Shown += delegate { if (ShownForChecks != null) ShownForChecks(this); Start(); };
            // Closing while it works means "stop after this mod".
            FormClosing += delegate(object sender, FormClosingEventArgs e) { if (running || recycling) { e.Cancel = true; if (running) RequestStop(); } };
        }

        private static FlatButton AddButton(FlowLayoutPanel panel, string text, bool primary, int width, Action click)
        {
            var b = new FlatButton(text, primary) { Width = width, Height = 36, Font = Palette.Small, Margin = new Padding(8, 0, 0, 0) };
            b.Click += delegate { click(); }; panel.Controls.Add(b); return b;
        }

        private void Buttons()
        {
            cancel.Visible = running; cancel.Enabled = running && !stopRequested;
            close.Visible = !running; close.Enabled = !recycling;
            int converted = Results.Count(r => r.Done);
            recycle.Visible = !running && !recycled && converted > 0;
            recycle.Enabled = !recycling;
        }

        private void Note(string text)
        {
            log.AppendText((log.TextLength > 0 ? Environment.NewLine + Environment.NewLine : "") + text);
        }

        internal void RequestStop()
        {
            if (!running || stopRequested) return;
            stopRequested = true;
            numbers.Text = "Stopping after this mod…";
            Buttons();
        }

        private async void Start()
        {
            if (running || work.Count == 0) { Finish(); return; }
            running = true; Buttons();
            long totalBytes = Math.Max(1, work.Sum(c => c.Size)), doneBytes = 0;
            var clock = Stopwatch.StartNew();
            for (int i = 0; i < work.Count; i++)
            {
                if (BeforeItemForChecks != null) BeforeItemForChecks(this, i);
                if (stopRequested || IsDisposed) break;
                ArchiveConvert.Candidate c = work[i];
                title.Text = "Converting " + (i + 1) + " of " + work.Count;
                current.Text = c.Name;
                long before = doneBytes; int index = i;
                Action<long, long> progress = delegate(long done, long total)
                {
                    double part = total <= 0 ? 0 : Math.Min(1.0, (double)done / total);
                    long now = before + (long)(c.Size * part);
                    try { BeginInvoke((Action)delegate { Show(now, totalBytes, clock.Elapsed); }); } catch (InvalidOperationException) { }
                };
                Show(before, totalBytes, clock.Elapsed);
                ArchiveConvert.Result r = await Task.Run(delegate { return ArchiveConvert.Convert(c.Source, host.SevenZip, host.Records, progress); });
                Results.Add(r);
                doneBytes += c.Size;
                if (!r.Done) Note("Not converted: " + c.Name + ": " + r.Error);
            }
            running = false;
            Finish();
        }

        private void Show(long done, long total, TimeSpan elapsed)
        {
            if (IsDisposed || !running) return;
            bar.Value = (double)done / total;
            string left = "";
            if (elapsed.TotalSeconds >= 5 && done > 0)
            {
                double seconds = elapsed.TotalSeconds * (total - done) / done;
                left = " · " + Remaining(TimeSpan.FromSeconds(seconds));
            }
            if (!stopRequested) numbers.Text = SizeText(done) + " of " + SizeText(total) + left;
        }

        internal static string Remaining(TimeSpan t)
        {
            if (t.TotalMinutes < 1) return "less than a minute left";
            if (t.TotalMinutes < 60) return "about " + Math.Ceiling(t.TotalMinutes) + " min left";
            return "about " + (int)t.TotalHours + " h " + t.Minutes + " min left";
        }

        internal static string SizeText(long bytes)
        {
            if (bytes >= 1L << 30) return (bytes / (double)(1L << 30)).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " GB";
            if (bytes >= 1L << 20) return (bytes / (double)(1L << 20)).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " MB";
            return Math.Max(1, (bytes + 1023) / 1024) + " KB";
        }

        // The summary: converted, not converted, left out, and the space the ZIPs take.
        internal string Summary()
        {
            List<ArchiveConvert.Result> done = Results.Where(r => r.Done).ToList();
            int failed = Results.Count - done.Count, notStarted = work.Count - Results.Count;
            var parts = new List<string> { done.Count + " converted" };
            if (failed > 0) parts.Add(failed + " not converted");
            if (leftOut > 0) parts.Add(leftOut + " left out");
            if (notStarted > 0) parts.Add(notStarted + " not started (cancelled)");
            string text = String.Join(" · ", parts) + ".";
            if (done.Count > 0)
            {
                long rar = done.Sum(r => r.SourceBytes), zip = done.Sum(r => r.ZipBytes);
                text += " The ZIPs take " + SizeText(zip) + "; the originals take " + SizeText(rar) + ".";
            }
            return text;
        }

        private void Finish()
        {
            int converted = Results.Count(r => r.Done);
            title.Text = stopRequested ? "Stopped" : "Done";
            current.Text = Summary();
            bar.Value = stopRequested ? bar.Value : 1;
            numbers.Text = converted == 0 ? "Nothing was changed." : "Installed copies, recipes and presets use the ZIPs now. The originals are still there, so each mod shows twice in the Mod library until they go.";
            if (converted > 0 && Results.Any(r => r.LayoutMoved)) Note(Results.Count(r => r.LayoutMoved) + " option layouts moved into their ZIPs.");
            Buttons();
        }

        // The second question; then each original goes to the Recycle Bin (never deleted for good).
        // Linux beta: the originals go to the desktop's Trash (LinuxRecycleBin), so the words say so.
        private static string BinName { get { return LinuxPlatform.IsLinux ? "Trash" : "Recycle Bin"; } }
        private void AskRecycle()
        {
            List<ArchiveConvert.Result> done = Results.Where(r => r.Done && File.Exists(r.Source) && File.Exists(r.Target)).ToList();
            if (done.Count == 0 || recycling) return;
            string question = "Move the " + done.Count + " original RAR and 7Z files to the " + BinName + "?\n\nThey take " + SizeText(done.Sum(r => r.SourceBytes)) + ". " + (LinuxPlatform.IsLinux ? "The space is freed when you empty the Trash" : "Windows frees the space when you empty the Recycle Bin") + ", and you can restore them from it until then. Their ZIPs stay.";
            if (ThemedMessageBox.Show(this, question, "Move originals to " + BinName, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            Recycle(done);
        }

        private async void Recycle(List<ArchiveConvert.Result> done)
        {
            recycling = true; Buttons();
            var kept = new List<string>();
            for (int i = 0; i < done.Count; i++)
            {
                ArchiveConvert.Result r = done[i];
                title.Text = "Moving " + (i + 1) + " of " + done.Count + " to the " + BinName;
                current.Text = Path.GetFileName(r.Source);
                bar.Value = (double)i / done.Count;
                LibraryDelete.Outcome o = await Task.Run(delegate { return LibraryDelete.Run(r.Source, host.Libraries, host.Protected, host.Bin, false); });
                if (o.Done) Recycled.Add(r.Source);
                else kept.Add(Path.GetFileName(r.Source) + ": " + (o.Unavailable != null ? "the " + BinName + " can't take it (" + o.Unavailable + ")" : (o.Error ?? "").TrimEnd('.')));
            }
            recycling = false; recycled = true;
            title.Text = "Done";
            bar.Value = 1;
            current.Text = Summary();
            numbers.Text = Recycled.Count + " originals moved to the " + BinName + (kept.Count > 0 ? "; " + kept.Count + " kept." : ".");
            if (kept.Count > 0) Note("Kept (not moved to the " + BinName + "):" + Environment.NewLine + String.Join(Environment.NewLine, kept.Select(k => "  " + k)));
            Buttons();
        }
    }

    // A thin bar: the accent part is done.
    internal sealed class ConvertBar : Control
    {
        private double value;
        internal double Value { get { return value; } set { double v = Math.Max(0, Math.Min(1, value)); if (v == this.value) return; this.value = v; Invalidate(); } }
        internal ConvertBar() { SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true); BackColor = Palette.Raised; }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Palette.Raised);
            int width = (int)Math.Round(ClientSize.Width * value);
            if (width > 0) using (var brush = new SolidBrush(Palette.Accent)) e.Graphics.FillRectangle(brush, 0, 0, width, ClientSize.Height);
        }
    }
}
