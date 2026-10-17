using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

namespace SlotsInstaller
{
    // 2.0.3: the themed message box (part of --polish-check). Both themes are forced through
    // ThemedMessageBox.ForcedDark; the registry is only read, never written. Nothing is copied
    // to the clipboard (the Ctrl+C text is checked directly).
    internal static class ThemedDialogChecks
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException("Themed dialog: " + message); }

        private sealed class Case
        {
            internal MessageBoxButtons Buttons; internal MessageBoxIcon Icon; internal MessageBoxDefaultButton Default;
            internal string[] Texts; internal DialogResult Accept, Escape;
        }

        internal static void Run(MainForm owner, string root, List<string> report)
        {
            bool? previous = ThemedMessageBox.ForcedDark;
            try
            {
                bool system = ThemedMessageBox.SystemUsesDarkApps();
                ThemedMessageBox.ForcedDark = null;
                Require(ThemedMessageBox.Dark == system, "the unforced theme does not follow AppsUseLightTheme");
                var cases = new[]
                {
                    new Case { Buttons = MessageBoxButtons.YesNoCancel, Icon = MessageBoxIcon.Question, Default = MessageBoxDefaultButton.Button1, Texts = new[] { "&Yes", "&No", "Cancel" }, Accept = DialogResult.Yes, Escape = DialogResult.Cancel },
                    new Case { Buttons = MessageBoxButtons.YesNo, Icon = MessageBoxIcon.Warning, Default = MessageBoxDefaultButton.Button2, Texts = new[] { "&Yes", "&No" }, Accept = DialogResult.No, Escape = DialogResult.None },
                    new Case { Buttons = MessageBoxButtons.OK, Icon = MessageBoxIcon.Error, Default = MessageBoxDefaultButton.Button1, Texts = new[] { "OK" }, Accept = DialogResult.OK, Escape = DialogResult.OK },
                    new Case { Buttons = MessageBoxButtons.OKCancel, Icon = MessageBoxIcon.Information, Default = MessageBoxDefaultButton.Button2, Texts = new[] { "OK", "Cancel" }, Accept = DialogResult.Cancel, Escape = DialogResult.Cancel },
                    new Case { Buttons = MessageBoxButtons.AbortRetryIgnore, Icon = MessageBoxIcon.None, Default = MessageBoxDefaultButton.Button3, Texts = new[] { "&Abort", "&Retry", "&Ignore" }, Accept = DialogResult.Ignore, Escape = DialogResult.None },
                    new Case { Buttons = MessageBoxButtons.RetryCancel, Icon = MessageBoxIcon.Warning, Default = MessageBoxDefaultButton.Button1, Texts = new[] { "&Retry", "Cancel" }, Accept = DialogResult.Retry, Escape = DialogResult.Cancel },
                };
                const string caption = "Update replacement";
                const string text = "'Cammy Colors' is already installed. Update it in place (keeps its priority)?\n\nNo installs a second copy above it.";
                int build = Environment.OSVersion.Version.Build;
                foreach (bool dark in new[] { true, false })
                {
                    ThemedMessageBox.ForcedDark = dark;
                    Require(ThemedMessageBox.Dark == dark, "forced theme ignored");
                    foreach (Case c in cases)
                    {
                        WindowChrome.LastDarkModeResult = -1;
                        using (var box = new ThemedMessageForm(text, caption, c.Buttons, c.Icon, c.Default, ThemedMessageBox.Dark, owner, 0))
                        {
                            string name = (dark ? "dark " : "light ") + c.Buttons;
                            Require(box.Text == caption && box.MessageLabel.Text == text.Replace("\n", Environment.NewLine) && box.AccessibleName == caption && box.MessageLabel.AccessibleName == box.MessageLabel.Text, name + ": caption/text");
                            Require(box.DialogButtons.Select(b => b.Text).SequenceEqual(c.Texts) && box.DialogButtons.All(b => b.UseMnemonic && b.AccessibleName == b.Text.Replace("&", "")), name + ": buttons " + String.Join("|", box.DialogButtons.Select(b => b.Text)));
                            Require(((Button)box.AcceptButton).DialogResult == c.Accept && box.ActiveControl == box.AcceptButton, name + ": default button");
                            Require(c.Escape == DialogResult.None ? box.CancelButton == null && !box.ControlBox : ((Button)box.CancelButton).DialogResult == c.Escape && box.ControlBox, name + ": Escape/close box");
                            Require(c.Icon == MessageBoxIcon.None ? box.IconBox.Image == null && box.IconBox.Width == 0 : box.IconBox.Image != null && box.IconBox.Image.Width == box.IconBox.Width && box.IconBox.AccessibleName == (c.Icon == MessageBoxIcon.Error ? "Error" : c.Icon == MessageBoxIcon.Warning ? "Warning" : c.Icon == MessageBoxIcon.Question ? "Question" : "Information"), name + ": icon");
                            Require(dark ? box.BackColor == Palette.Surface && box.ButtonStrip.BackColor == Palette.Background && box.MessageLabel.ForeColor == Palette.Text && box.DialogButtons.All(b => b.FlatStyle == FlatStyle.Flat && b.BackColor == Palette.Raised)
                                         : box.BackColor == SystemColors.Window && box.ButtonStrip.BackColor == SystemColors.Control && box.DialogButtons.All(b => b.FlatStyle == FlatStyle.System), name + ": colours");
                            Require(box.CopyText == "---------------------------\r\n" + caption + "\r\n---------------------------\r\n" + text.Replace("\n", "\r\n") + "\r\n---------------------------\r\n" + String.Join("", c.Texts.Select(t => t.Replace("&", "") + "   ")) + "\r\n---------------------------\r\n", name + ": Ctrl+C text");
                            // Shown (hidden): keys behave like the Windows message box.
                            box.Opacity = 0; box.Show(owner); Application.DoEvents();
                            if (dark && build >= 17763) Require(WindowChrome.LastDarkModeResult == 0, name + ": dark title bar not applied");
                            if (!dark) Require(WindowChrome.LastDarkModeResult == -1, name + ": light dialog got the dark title bar");
                            Rectangle work = Screen.FromRectangle(owner.Bounds).WorkingArea;
                            Require(work.Contains(box.Bounds), name + ": off screen " + box.Bounds + " in " + work);
                            Point centre = new Point(owner.Left + (owner.Width - box.Width) / 2, owner.Top + (owner.Height - box.Height) / 2);
                            centre.X = Math.Max(work.Left, Math.Min(centre.X, work.Right - box.Width)); centre.Y = Math.Max(work.Top, Math.Min(centre.Y, work.Bottom - box.Height));
                            Require(Math.Abs(box.Left - centre.X) <= 1 && Math.Abs(box.Top - centre.Y) <= 1, name + ": not centred on the owner: " + box.Location + " vs " + centre);
                            Require(box.IconBox.Visible == (c.Icon != MessageBoxIcon.None), name + ": icon visibility");
                            box.DialogResult = DialogResult.None;
                            Invoke(box, "ProcessDialogKey", Keys.Escape);
                            Require(box.DialogResult == (c.Escape == DialogResult.None ? DialogResult.None : c.Escape), name + ": Escape gave " + box.DialogResult);
                            box.DialogResult = DialogResult.None;
                            Invoke(box, "ProcessDialogKey", Keys.Return);
                            Require(box.DialogResult == c.Accept, name + ": Enter gave " + box.DialogResult);
                            Button mnemonicButton = box.DialogButtons.FirstOrDefault(b => b.Text.StartsWith("&"));
                            if (mnemonicButton != null)
                            {
                                box.DialogResult = DialogResult.None;
                                bool handled = (bool)typeof(Control).GetMethod("ProcessMnemonic", Private).Invoke(mnemonicButton, new object[] { Char.ToLowerInvariant(mnemonicButton.Text[1]) });
                                Require(handled && box.DialogResult == mnemonicButton.DialogResult, name + ": mnemonic " + mnemonicButton.Text);
                            }
                            if (c.Buttons == MessageBoxButtons.YesNoCancel) Render(box, Path.Combine(root, "message-box-" + (dark ? "dark" : "light") + "-" + box.DeviceDpi + ".png"));
                            box.DialogResult = DialogResult.None; box.Hide();
                        }
                    }
                }
                report.Add("PASS: themed message box: dark (Palette, dark title bar) and light (system colours) when forced; unforced follows AppsUseLightTheme (now " + (system ? "dark" : "light") + "); captions, text, button order/default/Enter/Escape/close box/mnemonics for all six button sets, icons, accessible names, Ctrl+C text, centred on the owner and on screen.");

                // Sizing at 100/150/200/300 % without clipping; long text scrolls inside the working area.
                string longText = String.Join("\n", Enumerable.Range(1, 160).Select(i => "Saved option " + i + " is no longer available in this archive and was removed from the selection."));
                string wide = String.Join(" ", Enumerable.Repeat("These saved options are no longer available and the remaining selection will be reviewed.", 6));
                var lines = new List<string>();
                foreach (bool dark in new[] { true, false })
                {
                    var widths = new Dictionary<int, int>();
                    foreach (int dpi in new[] { 96, 144, 192, 288 })
                    {
                        foreach (string message in new[] { text, wide, longText, "OK" })
                            using (var box = new ThemedMessageForm(message, "Saved options changed", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button1, dark, owner, dpi))
                            {
                                string name = (dark ? "dark " : "light ") + dpi + " DPI '" + message.Substring(0, Math.Min(12, message.Length)) + "'";
                                int s26 = dpi * 26 / 96;
                                Rectangle client = new Rectangle(Point.Empty, box.ClientSize);
                                Require(box.LayoutDpi == dpi && box.DialogButtons.All(b => b.Height >= s26 && b.Width >= dpi * 88 / 96 && box.ButtonStrip.ClientRectangle.Contains(b.Bounds)), name + ": buttons " + String.Join(",", box.DialogButtons.Select(b => b.Bounds.ToString())));
                                foreach (Button b in box.DialogButtons) foreach (Button o in box.DialogButtons) if (b != o) Require(!b.Bounds.IntersectsWith(o.Bounds), name + ": buttons overlap");
                                foreach (Button b in box.DialogButtons) Require(box.Measure(b.Text.Replace("&", ""), Int32.MaxValue, TextFormatFlags.Default).Width <= b.Width - dpi * 8 / 96 && b.Font.Equals(Palette.Normal), name + ": button text clipped or not the app font " + b.Text);
                                Require(client.Contains(box.MessagePanel.Bounds) && client.Contains(box.IconBox.Bounds) && client.Contains(box.ButtonStrip.Bounds), name + ": parts outside the client " + box.ClientSize);
                                Require(!box.MessagePanel.Bounds.IntersectsWith(box.ButtonStrip.Bounds) && !box.MessagePanel.Bounds.IntersectsWith(box.IconBox.Bounds), name + ": parts overlap");
                                Size needed = box.Measure(message, box.MessageLabel.Width, TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl);
                                Require(box.MessageLabel.Height >= needed.Height && needed.Width <= box.MessageLabel.Width, name + ": text clipped " + needed + " in " + box.MessageLabel.Size);
                                bool scrolls = box.MessageLabel.Height > box.MessagePanel.Height;
                                Require(scrolls == (message == longText), name + ": scrolling " + scrolls);
                                Rectangle work = Screen.FromRectangle(owner.Bounds).WorkingArea;
                                if (dpi <= owner.DeviceDpi) Require(box.Height <= work.Height && box.Width <= work.Width, name + ": larger than the working area " + box.Size);
                                Require(box.MessageLabel.Width <= dpi * 520 / 96 + 1, name + ": text wider than the wrap width");
                                if (message == wide) widths[dpi] = box.ClientSize.Width;
                                if (message == text && dpi == 96) lines.Add((dark ? "dark" : "light") + " 96 DPI " + box.ClientSize.Width + "x" + box.ClientSize.Height);
                                if (message == text && dpi == 288) lines.Add((dark ? "dark" : "light") + " 288 DPI " + box.ClientSize.Width + "x" + box.ClientSize.Height);
                            }
                    }
                    foreach (int dpi in new[] { 144, 192, 288 })
                    {
                        double ratio = widths[dpi] / (double)widths[96], expected = dpi / 96.0;
                        Require(Math.Abs(ratio - expected) / expected < 0.15, (dark ? "dark " : "light ") + dpi + " DPI width does not scale: " + widths[dpi] + " vs " + widths[96]);
                    }
                }
                report.Add("PASS: themed message box sizing at 96/144/192/288 DPI in both themes: buttons keep the minimum size without clipping or overlap, wrapped text fits its label, long text scrolls inside the working area, widths scale with DPI (" + String.Join("; ", lines) + ").");

                foreach (bool dark in new[] { true, false }) { ThemedMessageBox.ForcedDark = dark; RealModal(owner, root, report, dark, false); }
                RealModal(owner, root, report, ThemedMessageBox.Dark, true);

                // No direct MessageBox.Show call is left anywhere in the application.
                MethodInfo[] boxShow = typeof(MessageBox).GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => m.Name == "Show").ToArray();
                var offenders = new List<string>(); int scanned = 0;
                Assembly app = typeof(ThemedMessageBox).Assembly;
                foreach (Type type in app.GetTypes())
                    foreach (MethodBase method in type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly).Cast<MethodBase>().Concat(type.GetConstructors(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)))
                    {
                        MethodBody body; try { body = method.GetMethodBody(); } catch (Exception) { continue; }
                        if (body == null) continue;
                        byte[] il = body.GetILAsByteArray(); scanned++;
                        for (int i = 0; i + 4 < il.Length; i++)
                        {
                            if (il[i] != 0x28 && il[i] != 0x6F) continue;   // call / callvirt
                            int token = BitConverter.ToInt32(il, i + 1);
                            if ((token >> 24) != 0x0A && (token >> 24) != 0x06) continue;   // MemberRef / MethodDef
                            MethodBase target; try { target = method.Module.ResolveMethod(token, type.IsGenericType ? type.GetGenericArguments() : null, method.IsGenericMethod ? method.GetGenericArguments() : null); } catch (Exception) { continue; }
                            if (target != null && target.DeclaringType == typeof(MessageBox) && target.Name == "Show") offenders.Add(type.FullName + "." + method.Name);
                        }
                    }
                Require(boxShow.Length > 0 && scanned > 500 && offenders.Count == 0, "MessageBox.Show is still called by: " + String.Join(", ", offenders.Distinct()) + " (" + scanned + " methods scanned)");
                report.Add("PASS: no MessageBox.Show call remains in the application (" + scanned + " method bodies scanned); every confirmation uses ThemedMessageBox.");
            }
            finally { ThemedMessageBox.ForcedDark = previous; }
        }

        // 2.1.0: the dialog SHOWN for real (visible, modal) on this machine's monitor, like the user sees it.
        // A timer reads the metrics while ShowDialog runs, then answers with the default button.
        private static int TextHeight(Font font) { return TextRenderer.MeasureText("Ag", font).Height; }
        // simulateRescale: the fonts are scaled x3 while the handle is created, as Windows Forms did on the
        // user's 288-DPI monitor (2.0.4); the dialog must still end up at the app's text size.
        internal static void RealModal(MainForm owner, string root, List<string> report, bool dark, bool simulateRescale)
        {
            string metrics = null; Exception failure = null; int ticks = 0; ThemedMessageForm seen = null;
            using (var timer = new Timer { Interval = 100 })
            {
                timer.Tick += delegate
                {
                    ticks++;
                    ThemedMessageForm box = Application.OpenForms.OfType<ThemedMessageForm>().FirstOrDefault(f => f.Visible && f.IsHandleCreated);
                    if (box == null) { if (ticks > 100) { timer.Stop(); failure = new InvalidOperationException("Themed dialog: the modal dialog never appeared"); } return; }
                    if (ticks < 4) return;   // let it paint at its final size
                    timer.Stop(); seen = box;
                    try
                    {
                        Application.DoEvents();
                        Rectangle work = Screen.FromHandle(box.Handle).WorkingArea;
                        int message = TextHeight(box.MessageLabel.Font), main = TextHeight(owner.Font);
                        using (Form reference = ReferenceDialog(owner))
                        {
                            int dialogFont = TextHeight(reference.Controls[0].Font);
                            Rectangle client = box.ClientRectangle;
                            var buttons = box.DialogButtons.Select(b => box.RectangleToClient(b.RectangleToScreen(b.ClientRectangle))).ToList();
                            bool vscroll = box.MessagePanel.VerticalScroll.Visible || box.MessageLabel.Height > box.MessagePanel.ClientSize.Height;
                            metrics = (simulateRescale ? "with a simulated x3 creation-time rescale, " : "") + (dark ? "dark" : "light") + " shown at " + box.DeviceDpi + " DPI (owner " + owner.DeviceDpi + "): message text " + message + " px, main window text " + main + " px, report dialog text " + dialogFont + " px; client " + client.Width + "x" + client.Height + " in a " + work.Width + "x" + work.Height + " working area; scroll bar " + vscroll + "; buttons " + String.Join(" ", buttons.Select(b => b.ToString()));
                            try { using (var bitmap = new Bitmap(box.Width, box.Height)) { box.DrawToBitmap(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height)); bitmap.Save(Path.Combine(root, "message-box-shown-" + (dark ? "dark" : "light") + "-" + box.DeviceDpi + ".png"), ImageFormat.Png); } } catch (Exception) { }
                            Require(box.DeviceDpi == owner.DeviceDpi, "shown on another DPI than its owner: " + metrics);
                            Require(Math.Abs(message - main) <= main * 0.15, "message text is not the main window's text size: " + metrics);
                            Require(Math.Abs(message - dialogFont) <= dialogFont * 0.15, "message text is not the report dialog's text size: " + metrics);
                            Require(client.Width <= work.Width * 0.6, "dialog wider than 60% of the working area: " + metrics);
                            Require(!vscroll, "the loose-loader text scrolls: " + metrics);
                            Require(box.OptionBox != null && client.Contains(box.RectangleToClient(box.OptionBox.RectangleToScreen(box.OptionBox.ClientRectangle))) && TextHeight(box.OptionBox.Font) == message, "the Don't ask again check box is clipped or sized differently: " + metrics);
                            Require(box.DialogButtons.All(b => TextRenderer.MeasureText(b.Text.Replace("&", ""), b.Font).Width <= b.Width), "button text clipped: " + metrics);
                            Require(buttons.All(b => client.Contains(b)), "a button is outside the client area: " + metrics);
                            Require(work.Contains(box.Bounds), "dialog outside the working area: " + metrics);
                        }
                    }
                    catch (Exception ex) { failure = ex; }
                    finally { ((Button)box.AcceptButton).PerformClick(); }
                };
                timer.Start();
                int answer;
                if (!simulateRescale) { bool ignored; answer = ThemedMessageBox.Ask(owner, MainForm.LooseLoaderPromptText, MainForm.LooseLoaderCaption, MessageBoxIcon.Question, MainForm.LooseLoaderButtons, 0, MainForm.LooseLoaderDontAsk, out ignored); }
                else
                    using (var box = new ThemedMessageForm(MainForm.LooseLoaderPromptText, MainForm.LooseLoaderCaption, MainForm.LooseLoaderButtons, 0, MainForm.LooseLoaderDontAsk, MessageBoxIcon.Question, dark, owner, 0))
                    {
                        box.HandleCreated += delegate
                        {
                            Func<Font, Font> x3 = delegate(Font f) { return new Font(f.FontFamily, f.Size * 3, f.Style, f.Unit); };
                            box.Font = x3(box.Font); box.MessageLabel.Font = x3(box.MessageLabel.Font); foreach (Button b in box.DialogButtons) b.Font = x3(b.Font); box.OptionBox.Font = x3(box.OptionBox.Font);
                        };
                        answer = box.ShowDialog(owner) == DialogResult.Yes ? 0 : -1;
                    }
                Require(failure != null || answer == 0, "the default button is not Turn on: " + answer);
            }
            if (failure != null) throw failure;
            Require(seen != null && metrics != null, "no metrics were read");
            report.Add("PASS: themed message box shown for real (visible, modal): " + metrics + ".");
        }
        // The established dialog pattern (report/viewer dialogs): 96-DPI logical build, Palette fonts, AutoScaleMode.Dpi.
        private static Form ReferenceDialog(Form owner)
        {
            var form = new Form { ShowInTaskbar = false, Opacity = 0, StartPosition = FormStartPosition.Manual, Location = owner.Location };
            form.SuspendLayout(); form.AutoScaleDimensions = new SizeF(96F, 96F); form.AutoScaleMode = AutoScaleMode.Dpi; form.ClientSize = new Size(400, 200);
            form.Controls.Add(new Label { Text = "Reference", Font = Palette.Normal, AutoSize = true });
            form.ResumeLayout(true); form.PerformAutoScale(); form.Show(owner); Application.DoEvents();
            return form;
        }

        private static void Invoke(Form form, string name, Keys key) { typeof(Form).GetMethod(name, Private).Invoke(form, new object[] { key }); }
        private static void Render(Form form, string file)
        {
            using (var bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height)); bitmap.Save(file, ImageFormat.Png); }
        }
    }
}
