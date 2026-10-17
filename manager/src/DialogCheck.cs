using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace SlotsInstaller
{
    // 2.2.0: --dialog-check <dir>. The full app starts exactly like a user launch (Program/MainForm init, not
    // headless; Data is the folder beside this exe, a scratch copy in test runs), then opens themed dialogs
    // owned by the main window. In each dialog's first paint the font is recorded (Data\log.txt), and the text
    // really drawn on screen is measured: the first line's ink height must be within 10 % of the same words in
    // a main-window label painted with Palette.Normal. The medium text must not scroll, the buttons must be
    // fully visible and the box must stay a normal width. Every dialog closes itself; nothing waits for input.
    internal static class DialogCheck
    {
        internal static int ExitCode = 1;
        private static readonly List<string> report = new List<string>();
        private static string folder;
        private static int failures, inspected, referenceInk, referenceButtonInk;
        private static DialogPaintMetrics reference;

        internal const string FirstLine = "Uninstall 'Cammy Colors'?";
        internal const string MediumText = FirstLine + "\n\nIts files are removed from the game folder; files it replaced are restored exactly (the next replacement in the list takes over shared files). The source mod stays in your library, so you can reinstall it.";

        internal static void Attach(MainForm main, string output)
        {
            folder = Path.GetFullPath(String.IsNullOrEmpty(output) ? Path.Combine(AppConfig.DataDir, "dialog-check") : output);
            Directory.CreateDirectory(folder);
            // Never wait for input: the whole check ends within 90 s whatever happens.
            var failsafe = new System.Threading.Timer(delegate { try { Write("FAIL timeout: the check did not finish within 90 s"); } catch { } Environment.Exit(3); }, null, 90000, System.Threading.Timeout.Infinite);
            main.FormClosed += delegate { failsafe.Dispose(); };
            main.Shown += delegate
            {
                var start = new Timer { Interval = 1500 };
                start.Tick += delegate { start.Stop(); start.Dispose(); Run(main); };
                start.Start();
            };
        }

        private static void Run(MainForm main)
        {
            Label probe = null;
            try
            {
                Line("Main window: DeviceDpi " + main.DeviceDpi + ", AutoScaleMode " + main.AutoScaleMode + ", font " + main.Font.SizeInPoints + " pt, screen DC DPI " + ScreenDcDpi() + ", " + Screen.AllScreens.Length + " screen(s), primary " + Screen.PrimaryScreen.Bounds.Size);
                // A main-window label painted with Palette.Normal, showing the dialog's first line.
                probe = new Label { Name = "dialogCheckProbe", Text = FirstLine, Font = Palette.Normal, AutoSize = true, UseMnemonic = false, ForeColor = Palette.Text, BackColor = Palette.Background, Location = new Point(DpiMetrics.Px(main, 24), DpiMetrics.Px(main, 80)) };
                main.Controls.Add(probe); probe.BringToFront();
                // Screen copies need the windows on top (the desktop may have other windows over them).
                main.TopMost = true; main.Activate(); Application.DoEvents(); System.Threading.Thread.Sleep(300); Application.DoEvents();
                PaintEventHandler paint = delegate(object sender, PaintEventArgs e) { if (reference == null) reference = DialogPaintMetrics.Capture(probe, e.Graphics); };
                probe.Paint += paint; probe.Invalidate(); probe.Update(); Application.DoEvents(); System.Threading.Thread.Sleep(150); Application.DoEvents();
                probe.Paint -= paint;
                if (reference == null) throw new InvalidOperationException("the main-window probe label did not paint");
                referenceInk = InkHeight(probe, Path.Combine(folder, "main-label.png"));
                Line("Main-window label (Palette.Normal): font " + reference.FontSize + " " + reference.Unit + ", Height " + reference.FontHeight + " px, line " + reference.MeasuredLineHeight + " px, Graphics DPI " + reference.GraphicsDpi + ", label DeviceDpi " + reference.LabelDeviceDpi + ", ink height of '" + FirstLine + "' " + referenceInk + " px");
                if (referenceInk <= 0) throw new InvalidOperationException("no text found on screen in the main-window label");
                probe.Text = "Yes"; probe.Invalidate(); probe.Update(); Application.DoEvents();
                referenceButtonInk = InkHeight(probe, null);
                Line("Main-window label 'Yes' ink height " + referenceButtonInk + " px (reference for the dialog buttons)");
                // Diagnostics: how TextRenderer measures in this process (screen DC vs the window's DC).
                using (Font pixel = new Font(Palette.Normal.FontFamily, Palette.Normal.SizeInPoints * main.DeviceDpi / 72F, GraphicsUnit.Pixel))
                using (Graphics screen = Graphics.FromHwnd(IntPtr.Zero))
                using (Graphics window = main.CreateGraphics())
                {
                    Func<IDeviceContext, Font, int> h = delegate(IDeviceContext dc, Font f) { return dc == null ? TextRenderer.MeasureText("Ag", f).Height : TextRenderer.MeasureText(dc, "Ag", f).Height; };
                    Line("GDI DPI " + ThemedMessageForm.GdiDpi() + "; measuring 'Ag' with Palette.Normal: no DC " + h(null, Palette.Normal) + " px, screen DC (" + screen.DpiY + " DPI) " + h(screen, Palette.Normal) + " px, window DC (" + window.DpiY + " DPI) " + h(window, Palette.Normal) + " px; with a " + pixel.Size + " px pixel font: no DC " + h(null, pixel) + " px, screen DC " + h(screen, pixel) + " px, window DC " + h(window, pixel) + " px");
                }

                ThemedMessageBox.ShownHook = Inspect;
                ThemedMessageBox.Show(main, MediumText, "Uninstall replacement", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                bool dontAsk;
                ThemedMessageBox.Ask(main, FirstLine + "\n\n" + MainForm.LooseLoaderPromptText, MainForm.LooseLoaderCaption, MessageBoxIcon.Question, MainForm.LooseLoaderButtons, 0, MainForm.LooseLoaderDontAsk, out dontAsk);
                ThemedMessageBox.Show(main, FirstLine, "Short message", MessageBoxButtons.OK, MessageBoxIcon.Information);
                bool? theme = ThemedMessageBox.ForcedDark;
                try
                {
                    ThemedMessageBox.ForcedDark = false; ThemedMessageBox.Show(main, MediumText, "Uninstall replacement (light)", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                    ThemedMessageBox.ForcedDark = true; ThemedMessageBox.Show(main, MediumText, "Uninstall replacement (dark)", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                }
                finally { ThemedMessageBox.ForcedDark = theme; }
                if (inspected < 5) Fail("only " + inspected + " of 5 dialogs were inspected");
            }
            catch (Exception ex) { Fail("setup: " + ex); }
            finally
            {
                ThemedMessageBox.ShownHook = null;
                if (probe != null) { main.Controls.Remove(probe); probe.Dispose(); }
                main.TopMost = false;
                ExitCode = failures == 0 ? 0 : 1;
                Line(failures == 0 ? "ALL PASS" : failures + " FAILED");
                Write(null);
                main.Close();
            }
        }

        private static void Inspect(ThemedMessageForm form)
        {
            var wait = new Timer { Interval = 700 };
            wait.Tick += delegate
            {
                wait.Stop(); wait.Dispose();
                try
                {
                    form.TopMost = true; form.Activate(); form.Refresh(); Application.DoEvents(); System.Threading.Thread.Sleep(250); Application.DoEvents();
                    DialogPaintMetrics m = form.PaintMetrics;
                    inspected++;
                    if (m == null) { Fail("'" + form.Caption + "': the message never painted"); return; }
                    Line(m.Line);
                    try
                    {
                        using (var bitmap = new Bitmap(form.Width, form.Height))
                        {
                            using (Graphics g = Graphics.FromImage(bitmap)) g.CopyFromScreen(form.Location, Point.Empty, form.Size);
                            bitmap.Save(Path.Combine(folder, "dialog-" + inspected + ".png"), ImageFormat.Png);
                        }
                    }
                    catch (Exception ex) { Line("(screen copy failed: " + ex.Message + ")"); }
                    int ink = InkHeight(form.MessageLabel, Path.Combine(folder, "dialog-" + inspected + "-message.png"));
                    double ratio = referenceInk > 0 && ink > 0 ? ink / (double)referenceInk : 0;
                    string text = "'" + form.Caption + "': ink height of the first line " + ink + " px vs main window " + referenceInk + " px (x" + ratio.ToString("0.00") + ")";
                    if (Math.Abs(ratio - 1) > 0.10) Fail(text); else Line("ok " + text);
                    if (m.FontDisposed) Fail("'" + form.Caption + "': GDI+ could not read the font the message painted with"); if (m.Scrolls) Fail("'" + form.Caption + "': the message scrolls (" + m.LabelHeight + " px text in a " + m.PanelHeight + " px panel)");
                    if (!m.ButtonsVisible) Fail("'" + form.Caption + "': buttons are clipped");
                    Button yes = form.DialogButtons.FirstOrDefault(b => b.Text == "&Yes");
                    if (yes != null)
                    {
                        Rectangle r = yes.RectangleToScreen(yes.ClientRectangle); int inset = Math.Max(2, r.Height / 6);
                        r.Inflate(-inset, -inset);
                        int buttonInk = InkHeight(r, Path.Combine(folder, "dialog-" + inspected + "-yes.png"));
                        double br = referenceButtonInk > 0 && buttonInk > 0 ? buttonInk / (double)referenceButtonInk : 0;
                        string bt = "'" + form.Caption + "': button 'Yes' ink height " + buttonInk + " px vs main window " + referenceButtonInk + " px (x" + br.ToString("0.00") + ")";
                        if (Math.Abs(br - 1) > 0.15) Fail(bt); else Line("ok " + bt);
                    }
                    int logicalWidth = (int)Math.Round(m.ClientWidth * 96.0 / Math.Max(96, form.DeviceDpi));
                    if (logicalWidth > 760) Fail("'" + form.Caption + "': " + logicalWidth + " logical px wide (expected at most 760)"); else Line("ok '" + form.Caption + "': " + logicalWidth + " logical px wide");
                }
                catch (Exception ex) { Fail("inspect: " + ex); }
                finally { form.DialogResult = form.DialogButtons[form.DialogButtons.Count - 1].DialogResult; form.Close(); }
            };
            wait.Start();
        }

        // Height in screen pixels of the first text line drawn in `control` (rows that hold ink, from the first
        // one to the first empty row after it), measured on a screen copy.
        internal static int InkHeight(Control control, string save)
        {
            Rectangle area = control.RectangleToScreen(control.ClientRectangle);
            Control panel = control.Parent;
            if (panel != null) area.Intersect(panel.RectangleToScreen(panel.ClientRectangle));
            return InkHeight(area, save);
        }
        internal static int InkHeight(Rectangle area, string save)
        {
            if (area.Width <= 0 || area.Height <= 0) return 0;
            using (var bitmap = new Bitmap(area.Width, area.Height))
            {
                using (Graphics g = Graphics.FromImage(bitmap)) g.CopyFromScreen(area.Location, Point.Empty, area.Size);
                try { if (save != null) bitmap.Save(save, ImageFormat.Png); } catch { }
                Color bg = bitmap.GetPixel(bitmap.Width - 1, bitmap.Height - 1);
                Func<int, bool> ink = delegate(int y)
                {
                    for (int x = 0; x < bitmap.Width; x++)
                    {
                        Color c = bitmap.GetPixel(x, y);
                        if (Math.Abs(c.R - bg.R) + Math.Abs(c.G - bg.G) + Math.Abs(c.B - bg.B) > 150) return true;
                    }
                    return false;
                };
                int top = -1;
                for (int y = 0; y < bitmap.Height; y++)
                {
                    bool has = ink(y);
                    if (top < 0) { if (has) top = y; }
                    else if (!has) return y - top;
                }
                return top < 0 ? 0 : bitmap.Height - top;
            }
        }

        private static int ScreenDcDpi() { using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) return (int)Math.Round(g.DpiX); }
        private static void Fail(string text) { failures++; Line("FAIL " + text); }
        private static void Line(string text) { report.Add(text); Console.WriteLine(text); }
        private static void Write(string extra)
        {
            if (extra != null) report.Add(extra);
            try { File.WriteAllLines(Path.Combine(folder ?? AppConfig.DataDir, "dialog-check.txt"), report); } catch { }
        }
    }
}
