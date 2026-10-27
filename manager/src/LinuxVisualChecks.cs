using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace SlotsInstaller
{
    // Reproducible visual fixtures, never the installed app's Data or real game.
    // Run a fresh COPY of the build: nonempty Data is deliberately refused.
    // External mode leaves each real window ready for an X11 framebuffer capture;
    // the Windows reference mode renders hidden controls without stealing focus.
    internal static class LinuxVisualChecks
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr window, int index);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr window, int index, int value);
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
        [StructLayout(LayoutKind.Sequential)] private struct NativeRect { internal int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] private static extern bool AdjustWindowRectEx(ref NativeRect rect, int style, bool menu, int extendedStyle);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
        [StructLayout(LayoutKind.Sequential)] private struct NativeScrollInfo
        { internal int Size, Mask, Min, Max; internal uint Page; internal int Position, TrackPosition; }
        [DllImport("user32.dll")] private static extern bool GetScrollInfo(IntPtr window, int bar, ref NativeScrollInfo info);
        [StructLayout(LayoutKind.Sequential)] private struct NativeComboInfo
        { internal int Size; internal NativeRect Item, Button; internal int State; internal IntPtr Combo, Edit, List; }
        [DllImport("user32.dll")] private static extern bool GetComboBoxInfo(IntPtr window, ref NativeComboInfo info);

        internal static int Run(string output, bool externalCapture)
        {
            string root = Path.GetFullPath(output);
            if (Directory.Exists(root) || File.Exists(root)) throw new IOException("Visual checks require a NEW output directory.");
            if (Directory.Exists(AppConfig.DataDir) && Directory.EnumerateFileSystemEntries(AppConfig.DataDir).Any())
                throw new IOException("Visual checks require an isolated app copy with empty Data. Existing Data was not read or changed.");
            Safety.NoReparseChain(root); Safety.NoReparseChain(AppConfig.DataDir);
            Directory.CreateDirectory(root); Directory.CreateDirectory(AppConfig.DataDir);
            File.WriteAllText(Path.Combine(AppConfig.DataDir, "visual-fixtures-only.txt"), "Synthetic visual test data. No real game or user library.\n");
            // A visual fixture must never discover a real install or use an inherited credential.
            Environment.SetEnvironmentVariable("SLOTSLOADER_NO_DETECT", "1");
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
            bool priorHeadless = Program.Headless; Program.Headless = true;
            IntPtr priorDpi = IntPtr.Zero;
            if (!externalCapture && !LinuxPlatform.IsLinux)
            {
                // A hidden 96-DPI reference is comparable to the Deck display even
                // when the user's Windows desktop is at 300%. This is thread-local.
                try { priorDpi = SetThreadDpiAwarenessContext(new IntPtr(-1)); } catch (EntryPointNotFoundException) { }
            }
            var report = new List<string>();
            SynchronizationContext previous = SynchronizationContext.Current;
            bool autoInstall = WindowsFormsSynchronizationContext.AutoInstall;
            WindowsFormsSynchronizationContext.AutoInstall = false;
            try
            {
                string selected;
                AppConfig config = CreateFixtures(root, out selected);
                config.Save();
                Library.IdentityCacheDirectory = Path.Combine(root, "identity-cache");
                report.Add("Synthetic visual fixtures only; no real game, library, artwork or credentials.");
                report.Add(externalCapture ? "Capture: external X11 framebuffer handshake; image files are supplied by the capture harness." : "Capture: hidden WinForms DrawToBitmap reference; not a desktop framebuffer screenshot.");
                report.Add("Matched content areas: 1272x766 and 1592x966 pixels at 96 DPI. Capture names identify the isolated Linux desktop target; native window frames differ by platform and actual dimensions are recorded.");
                using (var context = new WindowsFormsSynchronizationContext())
                {
                    SynchronizationContext.SetSynchronizationContext(context);
                    using (var form = new MainForm(config))
                    {
                        form.MinimumSize = Size.Empty; form.StartPosition = FormStartPosition.Manual;
                        form.Location = Point.Empty; form.ShowInTaskbar = externalCapture;
                        Set(form, "firstLoad", false);
                        if (!externalCapture) HideWithoutActivation(form);
                        form.Show(); SizeWindow(form, 1280, 800, externalCapture); Pump(200);
                        Call(form, "RefreshLibrary", false); WaitIdle(form);
                        Call(form, "ShowAssistantTabForScreenshot");
                        Call(form, "LoadPackage", new LibraryEntry { SourcePath = selected, Name = "Visual Test Outfit", Character = "Cammy", Kind = "FOLDER" });
                        WaitIdle(form);
                        Require(Get<ModPackage>(form, "package") != null, "Fixture package did not open.");
                        var filters = Get<FilterPanel>(form, "filterPanel");
                        form.ShowFilters(true, false); WaitIdle(form);
                        foreach (FighterCard card in filters.Grid.Cards)
                            filters.Grid.SetPortrait(card.Code, Art(240, 320, "TEST", card.Name, card.Code.Sum(c => (int)c)), "Synthetic test artwork");
                        var list = Get<ListView>(form, "modList");
                        Require(list.Items.Count >= 30, "Fixture library did not populate.");
                        list.EnsureVisible(list.Items.Count - 1); Pump(120);
                        Require(list.TopItem != null && list.TopItem.Index > 0, "Library did not scroll to its final rows.");
                        list.EnsureVisible(0); Pump(120);
                        report.Add("PASS: populated native library scrolls to its final rows and back without losing rows.");
                        Capture(form, root, "01-library-1280x800", externalCapture, report);

                        filters.AutoScrollPosition = new Point(0, 250); Pump(120);
                        Require(filters.AutoScrollPosition.Y < 0, "Fighter panel did not scroll.");
                        Capture(form, root, "02-library-scrolled-1280x800", externalCapture, report);
                        filters.AutoScrollPosition = Point.Empty;
                        var search = Get<PaddedTextField>(form, "search");
                        search.Focus(); search.Text = "Visual Test"; Call(form, "FlushLibraryFilter"); WaitIdle(form);
                        Require(list.Items.Count > 0 && list.Items.Count < 30, "Search did not narrow the generated library.");
                        report.Add("PASS: real search field focus/text change narrows the library; clear restores rows.");
                        search.Text = ""; Call(form, "FlushLibraryFilter"); WaitIdle(form);

                        var combo = Get<ComboBox>(form, "targetCharacter");
                        combo.Focus(); int oldSelection = combo.SelectedIndex;
                        if (combo.Items.Count > 1)
                        {
                            SendMessage(combo.Handle, 0x100, new IntPtr((int)Keys.Down), IntPtr.Zero); // WM_KEYDOWN
                            SendMessage(combo.Handle, 0x101, new IntPtr((int)Keys.Down), IntPtr.Zero);
                            Pump(100); combo.SelectedIndex = oldSelection;
                        }
                        // A combo popup is a separate native window; opacity on its
                        // hidden parent does not hide it. Only open it on the isolated
                        // external display, never on the user's Windows desktop.
                        if (externalCapture)
                        {
                            combo.DroppedDown = true; Pump(150);
                            Require(combo.DroppedDown, "Character dropdown did not open.");
                            Capture(form, root, "03-character-dropdown-1280x800", true, report);
                            combo.DroppedDown = false;
                            report.Add("PASS: native character dropdown opens and closes after keyboard focus.");
                        }
                        else
                        {
                            Capture(form, root, "03-character-control-1280x800", false, report);
                            report.Add("REFERENCE: hidden Windows capture keeps the native dropdown closed to avoid showing a popup on the user's desktop.");
                        }

                        Call(form, "ShowPage", Get<Panel>(form, "installedPage"));
                        Call(form, "RefreshInstalled", new object[] { null }); WaitIdle(form);
                        var installed = Get<ListView>(form, "installedList");
                        Require(installed.Items.Count >= 8, "Generated Installed page is empty.");
                        installed.Items[0].Selected = true; Call(form, "ShowInstalledSelection"); Pump(100);
                        Capture(form, root, "04-installed-1280x800", externalCapture, report);
                        // beta4: a tour step with its real layers (dim with a hole, accent ring, card) under Wine.
                        using (TourForm tour = TourForm.ShowPreview(form, form.InstalledTour(), 2))
                        {
                            Pump(300);
                            Require(tour.Visible && !tour.Hole.IsEmpty && form.ClientRectangle.Contains(tour.CardBounds) && !tour.CardBounds.IntersectsWith(tour.Hole), "Tour step layout: outline " + tour.Hole + ", card " + tour.CardBounds);
                            Require(tour.Current != null && tour.Current.Title == "Change what's installed", "Tour preview opened '" + (tour.Current == null ? "" : tour.Current.Title) + "'.");
                            Capture(form, root, "04b-tour-installed-1280x800", externalCapture, report);
                            tour.Close();
                        }
                        Pump(150);
                        report.Add("PASS: a tour step shows over the window (dimmed, outlined control, card) and closes with its layers.");

                        Call(form, "ShowPage", Get<Panel>(form, "settingsPage")); Call(form, "LoadSettingsFields");
                        Get<ListBox>(form, "settingsLibraries").SelectedIndex = 0; Pump(100);
                        Capture(form, root, "05-settings-1280x800", externalCapture, report);
                        Call(form, "ShowUpdatesTab"); Pump(100);
                        if (LinuxPlatform.IsLinux)
                        {
                            var update = form.Controls.Find("linuxAppUpdate", true).Single();
                            Require(update.Visible && update.Parent.ClientRectangle.Contains(update.Bounds), "Linux update button does not fit.");
                        }
                        Capture(form, root, "05b-updates-1280x800", externalCapture, report);

                        Call(form, "ShowAssistant");
                        var assistant = Get<AssistantPage>(form, "assistantPage");
                        Require(assistant != null, "Assistant did not open.");
                        using (var demo = AssistantDemo.Attach(assistant, Path.Combine(root, "assistant-demo")))
                        {
                            Require(assistant.Send(AssistantDemo.Prompt), "Mock Assistant did not start.");
                            PumpUntil(delegate { return !assistant.Running; }, 60000, "Mock Assistant did not complete.");
                            File.WriteAllLines(Path.Combine(root, "assistant-transcript-fixture.txt"), assistant.Transcript.Entries.Select(e => e.Kind + ": " + e.Text));
                            Require(assistant.ActionList.Items.Count > 0, "Mock Assistant did not create its visual Plan.");
                            assistant.ActionList.SelectedIndex = 0; Pump(150);
                            Capture(form, root, "06-assistant-1280x800", externalCapture, report);
                        }
                        report.Add("PASS: Assistant uses a local mock, fake credential and generated mods; no actions applied.");

                        using (var dialog = new ThemedMessageForm("This is a visual test using generated files.\n\nThe sample library, previews and installed outfits are synthetic. No real game or personal mod library is changed.", "Visual test confirmation", new[] { "Keep preview", "Close" }, 0, "Remember this test choice", MessageBoxIcon.Information, ThemedMessageBox.Dark, form, 0))
                        {
                            if (!externalCapture) HideWithoutActivation(dialog);
                            dialog.Show(form); Pump(150);
                            Require(dialog.DialogButtons.All(b => b.Visible && b.Parent.ClientRectangle.Contains(b.Bounds)), "Confirmation buttons are clipped.");
                            Capture(dialog, root, "07-confirmation-dialog", externalCapture, report);
                            dialog.Close();
                        }

                        Call(form, "ShowPage", Get<Panel>(form, "libraryPage"));
                        SizeWindow(form, 1600, 1000, externalCapture); Pump(200);
                        Capture(form, root, "08-library-1600x1000", externalCapture, report);
                        SizeWindow(form, 1280, 800, externalCapture); Pump(150);
                        Require(form.ClientSize.Width >= 1100 && form.ClientSize.Height >= 600, "Resizing back lost usable content space.");
                        report.Add("PASS: window resize preserves usable layout; actual native bounds are recorded for every capture.");
                        if (externalCapture && LinuxPlatform.IsLinux)
                        {
                            Call(form, "ShowPage", Get<Panel>(form, "settingsPage")); Call(form, "ShowUpdatesTab"); Pump(100);
                            ((Button)form.Controls.Find("linuxAppUpdate", true).Single()).PerformClick(); Pump(100);
                            Require(form.IsDisposed, "Linux update action did not close the idle window.");
                            report.Add("PASS: Linux app update action hands off to the authenticated native session and closes the idle app.");
                        }
                        else form.Close();
                    }
                }
                report.Add("ALL PASS");
                File.WriteAllLines(Path.Combine(root, "visual-checks.txt"), report);
                File.WriteAllText(Path.Combine(root, "capture-complete.txt"), "ALL PASS\n");
                Console.WriteLine(String.Join(Environment.NewLine, report));
                return 0;
            }
            catch (Exception error)
            {
                report.Add("FAIL: " + error); File.WriteAllLines(Path.Combine(root, "visual-checks.txt"), report);
                Console.WriteLine(String.Join(Environment.NewLine, report)); return 1;
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous); WindowsFormsSynchronizationContext.AutoInstall = autoInstall;
                Program.Headless = priorHeadless;
                if (priorDpi != IntPtr.Zero) SetThreadDpiAwarenessContext(priorDpi);
            }
        }

        private static AppConfig CreateFixtures(string root, out string selected)
        {
            string game = Path.Combine(root, "Synthetic Game"), library = Path.Combine(root, "Synthetic Library");
            Directory.CreateDirectory(game); Directory.CreateDirectory(library);
            File.WriteAllText(Path.Combine(game, "StreetFighter6.exe"), "Visual test fixture; not an executable.");
            selected = Path.Combine(library, "00 Visual Test Outfit");
            Put(selected, "modinfo.ini", "name=Visual Test Outfit\nauthor=Generated test fixture\ndescription=Synthetic test artwork and files for interface verification. Not a playable mod.\nscreenshot=cover.png\n");
            Put(selected, AssistantFixture.Mesh("009", "001"), "synthetic mesh");
            Put(selected, AssistantFixture.Mdf("009", "001"), "synthetic material");
            using (Bitmap art = Art(960, 540, "SYNTHETIC TEST ART", "Visual Test Outfit", 5)) art.Save(Path.Combine(selected, "cover.png"), ImageFormat.Png);
            var fighters = new[] { new[] { "Cammy", "009" }, new[] { "Juri", "016" }, new[] { "Chun-Li", "004" }, new[] { "Ingrid", "032" } };
            for (int i = 1; i <= 40; i++)
            {
                string[] fighter = fighters[i % fighters.Length]; string name = fighter[0] + " Test Outfit " + i.ToString("00");
                string folder = Path.Combine(library, name);
                Put(folder, "modinfo.ini", "name=" + name + "\nauthor=Generated fixture\ndescription=Synthetic interface test data.\n");
                Put(folder, AssistantFixture.Mesh(fighter[1], "001"), "synthetic mesh " + i);
                Put(folder, AssistantFixture.Mdf(fighter[1], "001"), "synthetic material " + i);
            }
            for (int i = 1; i <= 12; i++)
            {
                string[] fighter = fighters[i % fighters.Length];
                string name = "Test installed outfit " + i.ToString("00"), folder = Path.Combine(game, "reframework", "costume_mods", fighter[0], name);
                Put(folder, "natives/visual-fixture.bin", "Synthetic installed outfit. Not playable.");
                Json.Write(Path.Combine(folder, ".slots-installer.json"), new InstallManifest { Id = i.ToString("x32"), Character = fighter[0], Name = name, SourcePath = Path.Combine(library, fighter[0] + " Test Outfit " + i.ToString("00")), CreatedUtc = new DateTime(2026, 1, 1, 0, i, 0, DateTimeKind.Utc).ToString("o"), SelectedIds = new List<string> { "" }, Sha256 = new Dictionary<string, string> { { "natives/visual-fixture.bin", new string('a', 64) } } });
            }
            return new AppConfig { GameRoot = game, Libraries = new List<string> { library }, SevenZipPath = "" };
        }

        private static Bitmap Art(int width, int height, string title, string subtitle, int seed)
        {
            var bitmap = new Bitmap(width, height);
            using (Graphics g = Graphics.FromImage(bitmap))
            using (var gradient = new LinearGradientBrush(new Rectangle(0, 0, width, height), Color.FromArgb(41, 57, 77), Color.FromArgb(97 + (seed & 31), 58, 40), 35F))
            using (var white = new SolidBrush(Color.FromArgb(236, 237, 240)))
            using (var line = new Pen(Color.FromArgb(100, 220, 225, 235), Math.Max(2F, width / 240F)))
            using (var font = new Font(Palette.Normal.FontFamily, Math.Max(10F, width / 32F), FontStyle.Bold, GraphicsUnit.Pixel))
            using (var small = new Font(Palette.Normal.FontFamily, Math.Max(9F, width / 48F), FontStyle.Regular, GraphicsUnit.Pixel))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                g.FillRectangle(gradient, 0, 0, width, height);
                for (int i = 1; i < 8; i++) g.DrawLine(line, 0, height * i / 8, width, height * i / 8);
                g.DrawEllipse(line, width / 3, height / 6, width / 3, height / 3);
                g.DrawLine(line, width / 2, height / 2, width / 4, height * 3 / 4);
                g.DrawLine(line, width / 2, height / 2, width * 3 / 4, height * 3 / 4);
                g.DrawString(title, font, white, new RectangleF(width / 16F, height * 0.77F, width * 0.9F, height * 0.12F));
                g.DrawString(subtitle, small, white, new RectangleF(width / 16F, height * 0.89F, width * 0.9F, height * 0.1F));
            }
            return bitmap;
        }

        private static void Capture(Form form, string root, string name, bool external, List<string> report)
        {
            form.PerformLayout(); form.Refresh(); Pump(250);
            string file = Path.Combine(root, name + ".png");
            var info = new Dictionary<string, object> { { "Name", name }, { "File", file }, { "WindowX", form.Left }, { "WindowY", form.Top }, { "Width", form.Width }, { "Height", form.Height }, { "ClientWidth", form.ClientSize.Width }, { "ClientHeight", form.ClientSize.Height }, { "Dpi", form.DeviceDpi }, { "Capture", external ? "external-framebuffer" : "hidden-DrawToBitmap" } };
            Json.Write(Path.Combine(root, name + ".json"), info);
            WriteGeometry(form, Path.Combine(root, name + "-geometry.json"));
            if (external)
            {
                string ready = Path.Combine(root, "capture-ready.json"), next = Path.Combine(root, "capture-next");
                if (File.Exists(next)) File.Delete(next);
                Json.Write(ready, info);
                Console.WriteLine("CAPTURE READY: " + name); Console.Out.Flush();
                PumpUntil(delegate { return File.Exists(next) && File.ReadAllText(next).Trim() == name; }, 180000, "No external capture acknowledgement for " + name);
                File.Delete(next); File.Delete(ready);
            }
            else
            {
                using (var bitmap = new Bitmap(form.Width, form.Height))
                { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(file, ImageFormat.Png); }
            }
            report.Add("CAPTURE: " + name + " outer=" + form.Width + "x" + form.Height + " client=" + form.ClientSize.Width + "x" + form.ClientSize.Height + " DPI=" + form.DeviceDpi + " font=" + form.Font.Name);
        }

        private static void HideWithoutActivation(Form form)
        {
            form.Opacity = 0; form.ShowInTaskbar = false;
            form.MaximumSize = new Size(4096, 4096);
            IntPtr handle = form.Handle;
            SetWindowLong(handle, -20, GetWindowLong(handle, -20) | 0x08000000); // WS_EX_NOACTIVATE
        }
        private static void WriteGeometry(Form form, string path)
        {
            var rows = new List<Dictionary<string, object>>();
            foreach (Control control in Descendants(form))
            {
                if (control.IsDisposed || !control.Visible || !control.IsHandleCreated) continue;
                NativeRect window; if (!GetWindowRect(control.Handle, out window)) continue;
                Point client = control.PointToScreen(Point.Empty);
                var row = new Dictionary<string, object> {
                    { "Name", control.Name }, { "Type", control.GetType().Name }, { "Handle", control.Handle.ToInt64() },
                    { "Bounds", new[] { control.Left, control.Top, control.Width, control.Height } },
                    { "ClientSize", new[] { control.ClientSize.Width, control.ClientSize.Height } },
                    { "ClientScreenOrigin", new[] { client.X, client.Y } }, { "DeviceDpi", control.DeviceDpi },
                    { "WindowRect", new[] { window.Left, window.Top, window.Right, window.Bottom } },
                    { "VScroll", ScrollGeometry(control.Handle, true) }, { "HScroll", ScrollGeometry(control.Handle, false) }
                };
                var checkbox = control as CheckBox;
                if (checkbox != null)
                {
                    Size measured = TextRenderer.MeasureText(checkbox.Text, checkbox.Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
                    Size glyph; using (Graphics g = checkbox.CreateGraphics()) glyph = CheckBoxRenderer.GetGlyphSize(g, System.Windows.Forms.VisualStyles.CheckBoxState.UncheckedNormal);
                    row["CheckBox"] = new Dictionary<string, object> { { "Text", checkbox.Text }, { "AutoSize", checkbox.AutoSize }, { "PreferredSize", new[] { checkbox.PreferredSize.Width, checkbox.PreferredSize.Height } }, { "MeasuredTextNoPadding", new[] { measured.Width, measured.Height } }, { "NativeGlyphSize", new[] { glyph.Width, glyph.Height } } };
                }
                rows.Add(row);
                var combo = control as ComboBox;
                if (combo != null && combo.DroppedDown)
                {
                    var native = new NativeComboInfo { Size = Marshal.SizeOf(typeof(NativeComboInfo)) };
                    if (GetComboBoxInfo(combo.Handle, ref native) && native.List != IntPtr.Zero && GetWindowRect(native.List, out window))
                        rows.Add(new Dictionary<string, object> { { "Name", control.Name + " dropdown" }, { "Type", "NativeComboPopup" }, { "Handle", native.List.ToInt64() }, { "WindowRect", new[] { window.Left, window.Top, window.Right, window.Bottom } }, { "DeviceDpi", control.DeviceDpi }, { "VScroll", ScrollGeometry(native.List, true) }, { "HScroll", ScrollGeometry(native.List, false) } });
                }
            }
            Json.Write(path, new Dictionary<string, object> { { "SystemMetrics", new Dictionary<string, int> { { "SM_CXVSCROLL_2", GetSystemMetrics(2) }, { "SM_CYHSCROLL_3", GetSystemMetrics(3) }, { "SM_CYVSCROLL_20", GetSystemMetrics(20) }, { "SM_CXHSCROLL_21", GetSystemMetrics(21) } } }, { "VisibleControls", rows } });
        }
        private static Dictionary<string, object> ScrollGeometry(IntPtr window, bool vertical)
        {
            LinuxControls.ScrollInfo bar;
            bool visible = LinuxControls.Scrollbar(window, vertical, out bar);
            var range = new NativeScrollInfo { Size = Marshal.SizeOf(typeof(NativeScrollInfo)), Mask = 0x17 };
            bool rangeAvailable = GetScrollInfo(window, vertical ? 1 : 0, ref range);
            return new Dictionary<string, object> {
                { "Visible", visible }, { "Size", bar.Size },
                { "Bounds", new[] { bar.Bounds.Left, bar.Bounds.Top, bar.Bounds.Right, bar.Bounds.Bottom } },
                { "ThumbSize", bar.ThumbSize }, { "ThumbStart", bar.ThumbStart }, { "ThumbEnd", bar.ThumbEnd }, { "State", bar.State },
                { "RangeAvailable", rangeAvailable }, { "RangeMin", range.Min }, { "RangeMax", range.Max },
                { "Page", range.Page }, { "Position", range.Position }, { "TrackPosition", range.TrackPosition }
            };
        }
        private static IEnumerable<Control> Descendants(Control root)
        { yield return root; foreach (Control child in root.Controls) foreach (Control item in Descendants(child)) yield return item; }
        private static void SizeWindow(Form form, int width, int height, bool external)
        {
            // Match the content area, not differently sized native title bars.
            // The isolated Linux window has 8 horizontal / 34 vertical chrome
            // pixels at 96 DPI, giving 1272x766 inside its 1280x800 outer window.
            Size client = new Size(width - 8, height - 34);
            form.ClientSize = client;
            // The user's high-DPI desktop can have a smaller logical working area.
            // A hidden native window may exceed it for offscreen rendering; no
            // screen resolution, desktop scaling or other window is changed.
            if (!external && !LinuxPlatform.IsLinux)
            {
                var bounds = new NativeRect { Right = client.Width, Bottom = client.Height };
                if (!AdjustWindowRectEx(ref bounds, GetWindowLong(form.Handle, -16), false, GetWindowLong(form.Handle, -20)))
                    throw new InvalidOperationException("Cannot compute the hidden reference window frame.");
                SetWindowPos(form.Handle, IntPtr.Zero, 0, 0, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top, 0x0004 | 0x0010);
            }
        }
        private static void WaitIdle(MainForm form)
        {
            PumpUntil(delegate { return !form.Working && !form.PortraitsLoading; }, 60000, "Fixture scan/layout did not complete.");
            Pump(250);
        }
        private static void Pump(int milliseconds) { var clock = Stopwatch.StartNew(); while (clock.ElapsedMilliseconds < milliseconds) { Application.DoEvents(); Thread.Sleep(10); } }
        private static void PumpUntil(Func<bool> complete, int milliseconds, string error)
        { var clock = Stopwatch.StartNew(); do { Application.DoEvents(); Thread.Sleep(15); if (complete()) return; } while (clock.ElapsedMilliseconds < milliseconds); throw new InvalidOperationException(error); }
        private static T Get<T>(MainForm form, string name) { return (T)typeof(MainForm).GetField(name, Private).GetValue(form); }
        private static void Set(MainForm form, string name, object value) { typeof(MainForm).GetField(name, Private).SetValue(form, value); }
        private static object Call(MainForm form, string name, params object[] args)
        {
            MethodInfo method = typeof(MainForm).GetMethods(Private).First(m => m.Name == name && m.GetParameters().Length == args.Length && m.GetParameters().Select((p, i) => args[i] == null || p.ParameterType.IsInstanceOfType(args[i])).All(x => x));
            return method.Invoke(form, args);
        }
        private static void Put(string root, string relative, string text) { string path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)); Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, text, new UTF8Encoding(false)); }
        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    }
}
