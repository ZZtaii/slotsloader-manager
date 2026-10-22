using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace SlotsInstaller
{
    // Replace-original UI in a hidden native window, against a generated game folder
    // with synthetic PAKs, a fixture Fluffy installed.ini and a mock launcher/probe.
    internal static partial class ReplaceUiChecks
    {
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

        private sealed class RecordingLauncher : IGameLauncher { internal int Count; public void Launch(string gameRoot) { Count++; } }

        internal static int Run(string fixtureRoot)
        {
            string root = Path.Combine(Path.GetFullPath(fixtureRoot), "replace-ui-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(root);
            var report = new List<string>();
            bool previousHeadless = Program.Headless; Program.Headless = true;
            IProcessProbe previousProbe = GameProcesses.Probe; IGameLauncher previousLauncher = GameProcesses.Launcher;
            var probe = new FakeProcessProbe(); var launcher = new RecordingLauncher();
            GameProcesses.Probe = probe; GameProcesses.Launcher = launcher;
            SynchronizationContext previous = SynchronizationContext.Current;
            bool previousAutoInstall = WindowsFormsSynchronizationContext.AutoInstall;
            WindowsFormsSynchronizationContext.AutoInstall = false;
            ReplaceSettings savedSettings = ReplaceSettings.Load();
            int failures = 0;
            bool previousAutoRefresh = MainForm.FluffyAutoRefresh; MainForm.FluffyAutoRefresh = false;   // checks edit installed.ini themselves; FluffyLink turns it on
            try
            {
                var settings = new ReplaceSettings { Method = ReplaceMethods.Pak }; settings.Save();
                using (var ui = new WindowsFormsSynchronizationContext())
                {
                    SynchronizationContext.SetSynchronizationContext(ui);
                    ReplaceChecks.Fixture f = ReplaceChecks.Create(Path.Combine(root, "fixture"), false, ReplaceMethods.Pak);
                    string fluffy = Path.Combine(root, "Fluffy", "Games", "SF6");
                    Directory.CreateDirectory(Path.Combine(fluffy, "Mods"));
                    // 1.0.1: empty (with a mod in it only Extra outfit, Slotsloader and REFramework install: the lockout); the checks
                    // about the external manager add their mods and put this back.
                    File.WriteAllText(Path.Combine(fluffy, "installed.ini"), "");
                    var config = new AppConfig { GameRoot = f.Game, Libraries = new List<string> { f.Mods, Path.Combine(fluffy, "Mods") }, SevenZipPath = "" };
                    var clean = ReplaceChecks.Snapshot(f.Game);
                    using (var form = new MainForm(config))
                    {
                        // 2.5.5: before this window's ShowInTaskbar/Opacity changes recreate its handle (which hid the 2.5.2 bug).
                        bool ctrlTapAtStart = form.CtrlTapRegistered && form.IsHandleCreated;
                        form.PortraitCacheDirectory = Path.Combine(root, "portraits-" + Guid.NewGuid().ToString("N").Substring(0, 6));   // the fixture game's portraits stay out of the shared Data
                        Set(form, "firstLoad", false); form.ShowInTaskbar = false; form.Opacity = 0; form.MinimumSize = Size.Empty; form.Show(); Application.DoEvents();
                        form.ClientSize = new Size(DpiMetrics.Px(form, 1232), DpiMetrics.Px(form, 640)); Application.DoEvents();
                        Action<string, Action> check = delegate(string name, Action action)
                        {
                            try { action(); report.Add("PASS " + name); }
                            catch (Exception ex) { failures++; report.Add("FAIL " + name + ": " + ex); }
                        };
                        check("Ctrl tap listener (2.5.5): registered as soon as the window is built, not on a later handle creation that the real app never has", delegate { Require(ctrlTapAtStart, "the Ctrl tap filter was not registered when the constructor returned"); });
                        check("install-mode toggle: Extra outfit by default, switches the inspector and footer, remembers the last mode", delegate { Toggle(form); });
                        check("INSTALL AS (2.3.0): one joined toggle, the selected half accent-filled with a check, radio-group accessibility, Left/Right keys, the choice remembered across a restart (no '· remembered' hint since 2.3.1), a tool mod forces Replace ('· locked for this mod', reason shown, remembered choice kept) and the next costume returns to it; no Default install mode in Settings", delegate { ModeToggle(form, f, root); });
                check("health check on a fixture mod: replaced outfit, fixes and Ready to install", delegate { Health(form, f); });
                        check("no popups for recoverable actions (2.4.1): Disable, Move and Restore original game files run without a dialog; Undo restores records, order and enabled state; SF6 running refuses inline; the footer's one-click Uninstall of every record of a mod runs without a dialog and keeps no copy, so no Undo (1.0.1); a new change ends the previous Undo", delegate { UndoFlows(form, f, probe); });
                        check("Space installs (2.4.1): from the mod list and the options tree like a click on Install; Enter toggles an option; Space types in the search and outfit name, selects in Filters, presses a focused button, does nothing while Install is disabled; auto-repeat installs once; the footer reads Install [Space] / Health; 2.5.2/2.5.3: a lone Ctrl tap uninstalls in the library and on Installed (not Ctrl+key/click or in the searches), also a replacement in Extra outfit mode; W/S move on Installed", delegate { SpaceInstall(form, f); });
                        check("footer and inspector fit without overlap in both modes at 1232x640 and 1110x610", delegate { Layout(form, root); });
                        check("install a replacement; Installed page lists it with priority and a read-only external manager row; the Extra / Installed type filter (1.0.1)", delegate { InstallAndList(form, f, fluffy); });
                        check("game-file change banner appears and Reapply clears it", delegate { Banner(form, f); });
                        check("Reapply notice (1.0.10): the game-file change is a toast on the other pages (not on Installed, where the banner is) with Reapply, Show details and ✕ (back when the notice changes or at Launch); Launch SF6 says so first and starts on a second click; Reapply from the toast clears it and Launch starts at once", delegate { ReapplyNotice(form, f, launcher); });
                        check("Launch SF6 uses the launcher; the running indicator disables replacement changes", delegate { Launch(form, probe, launcher); });
                        check("no Test in game in the window (removed in 2.3.0): three footer actions; a leftover test record is kept as a normal install at start-up and deleted", delegate { LeftoverTest(form, f); });
                        check("Uninstall from the Installed page restores the clean game folder", delegate { Uninstall(form, f, clean); });
                        check("leftover PAK row (1.0.11): the fixture's unlisted patch PAK is a 'Leftover PAK' row ('not listed by any mod manager · size · name') with Remove PAK; SF6 running refuses inline; Remove PAK takes it out with no popup and offers Undo; Undo puts it back byte for byte; an Undo that ends unused sends its copy to the Recycle Bin", delegate { LeftoverPakRow(form, f, probe, clean); });
                        check("library uninstall (2.0.3): right-click and footer offer Uninstall replacement for a source installed as a replacement, disabled with the reason while Fluffy runs, uninstall for good (no copy kept, 1.0.1); extra-outfit entries unchanged", delegate { LibraryReplacementUninstall(form, f, probe, clean); });
                        check("Settings: Game files & replacements tab shows method, store, Fluffy and tool status without hiding General", delegate { SettingsTab(form, root); });
                        check("Extra outfit mode warns when Slotsloader is missing (1.0.8: after REFramework, which comes first)", delegate { SlotsLoaderNotice(form, f); });
                        check("REFramework (1.0.8): build date, commit and upscaler mark read from dinput8.dll; Extra outfit notice for missing, not started and out-of-date builds (an upscaler build is never old; an unknown date is fine); one click downloads the pinned nightly, verified, opened in Replace original; Install puts dinput8.dll in the game, a newer pin updates that record in place, uninstall puts the earlier dinput8.dll back", delegate { ReFrameworkFlow(form, f); });
                        check("Slotsloader offer (2.3.1, no questions in 2.4.1): only the pinned build: a library copy found by SHA-256 (a renamed SF6_SlotsLoader_v9.9.zip beside it is ignored), else a download (the click is the consent, no dialog) verified into Data\\downloads and opened in Replace original, then the cached copy; a 404 shows the reason and the release page", delegate { LoaderOffer(form, f); });
                        check("automatic health check (2.0.1): Checking… then Ready without clicking; Install enables automatically; the button reads Report", delegate { AutoCheck(form, f, root); });
                        check("debounce and supersede (2.0.1): rapid changes start one run; a stale run is discarded and the last selection's result applies", delegate { Supersede(form, f); });
                        check("Install waits for a fresh result after a change; Report opens the current result (waits while checking)", delegate { InstallWaits(form, f); });
                        check("report dialog (2.0.1): dark title bar attributes, sized to its content, Re-check, conflicts section", delegate { ReportDialog(form, root); });
                        check("conflicts (2.0.1): footer and inspector name the installed mod with counts; inline Uninstall link (disabled while Fluffy runs) uninstalls without a dialog, for good (no copy, no Undo line; 1.0.1); reinstalling from the library restores byte for byte", delegate { ConflictLink(form, f, probe, root); });
                        check("installs never remove other mods; opening another mod changes nothing; no dialog opens", delegate { NoAutomaticRemoval(form, f, probe, launcher); });
                        check("Installed page (2.0.1): shared-files chip opens the details naming who wins; same-outfit stacking on rows (the Has conflicts filter was removed in 1.0.1)", delegate { InstalledConflicts(form, root); });
                        check("external mods (1.0.1 lockout): the footer gives the reason (1.0.11: 'Replace original is off' and a short line; Extra outfit mode names it above the toggle)", delegate { FluffyFooter(form, f, fluffy); });
                        check("Fluffy link (2.2.0): a Fluffy install shows on the Installed page by itself within 2 s (watcher); its Uninstall (and the library right-click item for its Mods entry) is disabled with the reason while Fluffy runs, enabled when closed, and removes its file and installed.ini entry; a PAK install says to use Fluffy", delegate { FluffyLink(form, f, fluffy, probe); });
                        check("other tools' files (2.2.0): the banner names the Fluffy mod that replaced a tool-mod file; Uninstall asks Restore original / Keep current file / Cancel; Cancel changes nothing; Keep current file uninstalls and keeps the file with the original restorable", delegate { DriftDialog(form, f, fluffy, probe); });
                        check("Extra outfit mode reviews automatically and notes a duplicate install (same source and options)", delegate { DuplicateExtra(form, f); });
                        check("Settings has no automatic-uninstall option", delegate { NoAutoUninstallSetting(form); });
                        check("root alternatives: empty initial choice, radio switching, Reset and saved all-five winner without a record rewrite; renders", delegate { RootChoiceUi(form, f, root); });
                        check("changed on disk (2.4.1): editing the open zip reloads it on F5, on window activation and before Install (selection kept by option id, else by name; mode and Install kept; status says so); a deleted source clears the inspector; the library index re-reads the edited archive; an installed copy shows Source changed and Reinstall applies the edit in place; no \"changed on disk\" problem reaches the user", delegate { SourceReload(form, f); });
                        check("saved options missing (2.4.2): a click on the footer reviews without them and clears the flag in Replace original and Extra outfit, like Health / Review files", delegate { FooterMissingOptions(form, f); });
                        check("removed copies (2.4.2): Settings → Game files & replacements says how many (the copies Restore original game files keeps for its Undo), their size and the keep rule (1.0.1: only while their Undo is available) under the store (no '(freed on uninstall)'), fits at 1232x640 and 1110x610; render at the device DPI; a new change ends the Undo and the copy is deleted", delegate { SettingsStoreLine(form, f, root); });
                        check("copy not saved (2.4.2): Restore original game files (1.0.1: uninstall keeps no copy) with a store file open in another program says the copy wasn't saved (never 'kept'); the refresh retries and keeps it; Undo puts it back", delegate { TrashWarningInline(form, f); });
                        check("Undo position (2.4.2): after another program installs a mod over the same files, the window's Undo of Restore original game files puts the removed mods back below it", delegate { UndoBelowNewer(form, f); });
                        check("cleanup: Restore original game files leaves no replacement installed (the next suites share Data)", delegate { var e = new ReplaceEngine(f.Game, AppConfig.DataDir, ReplaceSettings.Load()); e.RestoreAll(); Require(e.LoadState().Mods.Count == 0 && !Directory.Exists(Path.Combine(f.Game, "natives")), "cleanup"); });
                        check("game entries left invalidated and Fluffy PAK installs (2.4.2): the banner says \"2 game entries left invalidated by another tool.\" with Repair (compact row, rendered at the device DPI); SF6 or Fluffy running refuses inline; Repair restores both (the Fluffy-owned entry stays) with the result inline and no popup; both Fluffy instances are watched, also with an unreadable Manager record; an invalidate install uninstalls from the Installed page (entry restored) and Undo zeroes it again", delegate { FluffyPakUiChecks.Run(root, probe); });
                        check("replacement method (2.4.1): Automatic installs with the Fluffy PAK method while the loose file loader is off (entries zeroed) and loose when it is on, with no question; an explicit loose setting is respected and the banner converts it (Use PAK method; refused while SF6 runs); another variant of an installed mod is a switch (one record, no warning, priority kept), duplicates collapse with no copy kept (1.0.1), a different mod still warns; Fluffy open does not block loose or tool installs", delegate { AutomaticMethod(root, probe); });
                        form.Close();
                    }
                }
            }
            catch (Exception ex) { failures++; report.Add("FAIL setup: " + ex); }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous); WindowsFormsSynchronizationContext.AutoInstall = previousAutoInstall;
                Program.Headless = previousHeadless; GameProcesses.Probe = previousProbe; GameProcesses.Launcher = previousLauncher; MainForm.FluffyAutoRefresh = previousAutoRefresh;
                try { savedSettings.Save(); } catch { }
            }
            report.Add(failures == 0 ? "ALL PASS" : failures + " FAILED");
            File.WriteAllLines(Path.Combine(root, "replace-ui-checks.txt"), report);
            try { File.WriteAllLines(Path.Combine(AppConfig.DataDir, "replace-ui-checks.txt"), report); } catch { }
            Console.WriteLine(String.Join(Environment.NewLine, report));
            return failures == 0 ? 0 : 1;
        }

        private static T Get<T>(object o, string name) { FieldInfo field = o.GetType().GetField(name, PrivateInstance); if (field == null) throw new MissingFieldException(name); return (T)field.GetValue(o); }
        private static void Set(object o, string name, object value) { o.GetType().GetField(name, PrivateInstance).SetValue(o, value); }
        // 1.0.1: the Installed page's type filter is a segmented All / Extra / Installed toggle (was a dropdown).
        internal static void Kind(MainForm form, string option)
        {
            SegmentedChoice kind = Get<SegmentedChoice>(form, "installedKind");
            int index = Array.IndexOf(kind.Options, option);
            if (index < 0) throw new InvalidOperationException("no '" + option + "' type filter: " + String.Join(", ", kind.Options));
            kind.Choose(index); Application.DoEvents();
            if (kind.SelectedIndex != index) throw new InvalidOperationException("the type filter did not switch to " + option);
        }
        private static object Call(object o, string name, params object[] args)
        {
            MethodInfo m = o.GetType().GetMethods(PrivateInstance | BindingFlags.Public).First(x => x.Name == name && x.GetParameters().Length == args.Length);
            return m.Invoke(o, args);
        }
        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        private static void WaitIdle(MainForm form)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(60);
            do { Application.DoEvents(); Thread.Sleep(10); if (!form.Working) { Application.DoEvents(); if (!form.Working) return; } } while (DateTime.UtcNow < deadline);
            throw new InvalidOperationException("Background work did not finish.");
        }
        private static Rectangle BoundsIn(Control control, Control ancestor)
        {
            Point location = Point.Empty; Control current = control;
            while (current != null && current != ancestor) { location.Offset(current.Left, current.Top); current = current.Parent; }
            return new Rectangle(location, control.Size);
        }
        private static IEnumerable<Control> AllControls(Control root) { foreach (Control child in root.Controls) { yield return child; foreach (Control d in AllControls(child)) yield return d; } }
        private static void Render(MainForm form, string file)
        {
            using (var bitmap = new Bitmap(form.ClientSize.Width, form.ClientSize.Height)) { form.DrawToBitmap(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height)); bitmap.Save(file, ImageFormat.Png); }
        }

        private static void Toggle(MainForm form)
        {
            Require(!form.IsReplaceMode, "Extra outfit must be the default mode.");
            var toggle = Get<SegmentedToggle>(form, "modeToggle");
            Require(toggle.SelectedIndex == 0 && !toggle.Locked && Get<Panel>(form, "targetPanel").Visible && !Get<Panel>(form, "replacesPanel").Visible, "slot-mode inspector state");
            typeof(Control).GetMethod("OnMouseDown", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(toggle, new object[] { new MouseEventArgs(MouseButtons.Left, 1, toggle.Width * 3 / 4, toggle.Height / 2, 0) }); Application.DoEvents();
            Require(form.IsReplaceMode && toggle.SelectedIndex == 1 && !Get<Panel>(form, "targetPanel").Visible && Get<Panel>(form, "replacesPanel").Visible, "replace-mode inspector state");
            Require(Get<FlatButton>(form, "installButton").Text == "Install" && Get<FlatButton>(form, "installButton").KeyHint == "Space" && Get<FlatButton>(form, "previewButton").Text == "Health" && !Get<FlatButton>(form, "libraryUninstallButton").Visible, "replace-mode footer labels");
            Require(ReplaceSettings.Load().LastMode == InstallModes.Replace, "last mode not remembered");
        }

        private static void ModeToggle(MainForm form, ReplaceChecks.Fixture f, string root)
        {
            var toggle = Get<SegmentedToggle>(form, "modeToggle");
            Func<int, AccessibleObject> option = delegate(int i) { return toggle.AccessibilityObject.GetChild(i); };
            Action<string> shot = delegate(string name)
            {
                Control panel = Get<Panel>(form, "modePanel"); form.PerformLayout(); Application.DoEvents();
                using (var b = new Bitmap(panel.Width, panel.Height)) { panel.DrawToBitmap(b, new Rectangle(0, 0, panel.Width, panel.Height)); b.Save(Path.Combine(root, "install-as-" + name + ".png"), ImageFormat.Png); }
            };
            Require(toggle.AccessibleName == "Install as" && toggle.AccessibilityObject.Role == AccessibleRole.Grouping && toggle.AccessibilityObject.GetChildCount() == 2 && option(0).Role == AccessibleRole.RadioButton, "toggle accessibility");
            // Pixels: the selected half is the accent colour, the other is not.
            Func<int, Color> half = delegate(int i) { using (var b = new Bitmap(toggle.Width, toggle.Height)) { toggle.DrawToBitmap(b, new Rectangle(0, 0, b.Width, b.Height)); Rectangle r = toggle.SegmentBounds(i); return b.GetPixel(r.Left + r.Width / 8, r.Top + r.Height / 2); } };
            Require(form.IsReplaceMode && toggle.SelectedIndex == 1 && (option(1).State & AccessibleStates.Checked) != 0 && (option(0).State & AccessibleStates.Checked) == 0 && half(1).ToArgb() == Palette.Accent.ToArgb() && half(0).ToArgb() != Palette.Accent.ToArgb(), "Replace state: " + half(0) + " / " + half(1));
            shot("replace");
            typeof(Control).GetMethod("OnKeyDown", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(toggle, new object[] { new KeyEventArgs(Keys.Left) }); Application.DoEvents();
            Require(!form.IsReplaceMode && toggle.SelectedIndex == 0 && (option(0).State & AccessibleStates.Checked) != 0 && half(0).ToArgb() == Palette.Accent.ToArgb() && ReplaceSettings.Load().LastMode == InstallModes.Slot, "Left did not choose Extra outfit");
            shot("extra-outfit");
            typeof(Control).GetMethod("OnKeyDown", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(toggle, new object[] { new KeyEventArgs(Keys.Right) }); Application.DoEvents();
            Require(form.IsReplaceMode && ReplaceSettings.Load().LastMode == InstallModes.Replace, "Right did not choose Replace original");
            // Remembered across a restart: a new window starts in the last choice.
            using (MainForm second = new MainForm(new AppConfig { GameRoot = f.Game, Libraries = new List<string>(), SevenZipPath = "" }))
            {
                second.PortraitCacheDirectory = Path.Combine(f.Root, "portraits-" + Guid.NewGuid().ToString("N").Substring(0, 6));   // the fixture game's portraits stay out of the shared Data
                typeof(MainForm).GetField("firstLoad", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(second, false);
                second.ShowInTaskbar = false; second.Opacity = 0; second.Show(); Application.DoEvents();
                Require(second.IsReplaceMode && Get<SegmentedToggle>(second, "modeToggle").SelectedIndex == 1, "Replace not remembered across a restart");
                second.Close();
            }
            Call(form, "SetInstallMode", InstallModes.Slot, true); Application.DoEvents();
            using (MainForm second = new MainForm(new AppConfig { GameRoot = f.Game, Libraries = new List<string>(), SevenZipPath = "" }))
            {
                second.PortraitCacheDirectory = Path.Combine(f.Root, "portraits-" + Guid.NewGuid().ToString("N").Substring(0, 6));   // the fixture game's portraits stay out of the shared Data
                typeof(MainForm).GetField("firstLoad", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(second, false);
                second.ShowInTaskbar = false; second.Opacity = 0; second.Show(); Application.DoEvents();
                Require(!second.IsReplaceMode, "Extra outfit not remembered across a restart");
                second.Close();
            }
            // A tool mod forces Replace (locked, reason in the notice) and keeps the remembered choice.
            Call(form, "LoadPackage", new LibraryEntry { SourcePath = f.Mod("tool"), Name = "tool" }); WaitIdle(form);
            Require(form.IsReplaceMode && toggle.Locked && toggle.SelectedIndex == 1 && Get<Label>(form, "modeNotice").Text.StartsWith("Tool mods install into the game folder") && Get<Label>(form, "modeLockHint").Text == "· locked for this mod" && ReplaceSettings.Load().LastMode == InstallModes.Slot, "forced Replace: " + Get<Label>(form, "modeNotice").Text);
            { Control r = Get<Label>(form, "modeLockHint"); form.PerformLayout(); Application.DoEvents(); Require(r.Visible && r.Width >= TextRenderer.MeasureText(r.Text, r.Font).Width && r.Parent.ClientRectangle.Contains(r.Bounds), "lock hint clipped"); }
            Require((option(0).State & AccessibleStates.Unavailable) != 0, "locked half not reported unavailable");
            shot("locked");
            typeof(Control).GetMethod("OnKeyDown", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(toggle, new object[] { new KeyEventArgs(Keys.Left) }); Application.DoEvents();
            Require(form.IsReplaceMode && ReplaceSettings.Load().LastMode == InstallModes.Slot, "a locked toggle changed the mode");
            // The next costume returns to the remembered Extra outfit.
            Call(form, "LoadPackage", new LibraryEntry { SourcePath = f.Mod("modB"), Name = "modB" }); WaitIdle(form);
            Require(!form.IsReplaceMode && !toggle.Locked && toggle.SelectedIndex == 0 && Get<Label>(form, "modeLockHint").Text == "", "the remembered mode did not come back after the tool mod (or a hint is still shown)");
            Require(typeof(MainForm).GetField("settingsDefaultMode", BindingFlags.NonPublic | BindingFlags.Instance) == null, "Settings still has Default install mode");
            // Leave Replace selected for the following checks (as the earlier toggle check did).
            Call(form, "SetInstallMode", InstallModes.Replace, true); Application.DoEvents();
        }

        private static void Health(MainForm form, ReplaceChecks.Fixture f)
        {
            Call(form, "LoadPackage", new LibraryEntry { SourcePath = f.Mod("modA"), Name = "modA" }); WaitIdle(form);
            Require(Get<ModPackage>(form, "package") != null, "package not loaded");
            Call(form, "BuildPlan", false); WaitIdle(form);
            ReplacePlan plan = Get<ReplacePlan>(form, "replacePlan");
            Require(plan != null && plan.CanInstall, "health check result: " + (plan == null ? "none" : ReplacePlanner.Report(plan)));
            Require(Get<Label>(form, "planSummary").Text.StartsWith("Ready to install") && Get<Label>(form, "replacesLabel").Text.Contains("Cammy Outfit 2"), "footer/inspector: " + Get<Label>(form, "planSummary").Text + " / " + Get<Label>(form, "replacesLabel").Text);
            Require(Get<Label>(form, "planDetails").Text.Contains("auto-fixed"), "fix count missing in the footer");
            Require(Get<FlatButton>(form, "installButton").Enabled, "install not enabled");
            using (Form report = (Form)Call(form, "CreateHealthReport", plan))
            {
                report.Opacity = 0; report.Show(); Application.DoEvents();
                var texts = AllControls(report).OfType<Label>().Select(l => l.Text).ToList();
                Require(texts.Any(t => t == "Fixed automatically") && texts.Any(t => t.Contains("outdated type signature")) && texts.Any(t => t.Contains("streaming copy")), "health report contents");
                report.Close();
            }
        }

        // 2.4.1: no popups for recoverable actions; the status strip offers Undo, which restores records, order and enabled state.
        private static void UndoFlows(MainForm form, ReplaceChecks.Fixture f, FakeProcessProbe probe)
        {
            ReplaceEngine engine = FormEngine(f);
            int asked = 0;
            ThemedMessageBox.TestResponder = delegate(ThemedMessageForm box) { asked++; return DialogResult.Cancel; };
            try
            {
                Func<string, ReplaceMod> install = delegate(string mod)
                {
                    Load(form, f, mod); WaitReview(form);
                    Call(form, "InstallCurrent"); WaitIdle(form);
                    return engine.LoadState().Mods.FirstOrDefault(m => m.SourcePath == f.Mod(mod));
                };
                Func<string> order = delegate { return String.Join(",", engine.LoadState().Mods.Select(m => m.SourcePath + (m.Enabled ? "+" : "-"))); };
                Func<string, GameModRow> row = delegate(string mod) { return Get<List<GameModRow>>(form, "gameRows").First(r => r.Mod != null && r.Mod.SourcePath == f.Mod(mod)); };
                Action refresh = delegate { Call(form, "ShowPage", Get<Panel>(form, "installedPage")); Call(form, "RefreshInstalled", (string)null); WaitIdle(form); };
                Action undo = delegate { Require(form.UndoAvailable, "no Undo offered"); form.PerformUndo(); WaitIdle(form); refresh(); };
                install("tool"); install("modA");
                Require(engine.LoadState().Mods.Count == 2, "fixture installs: " + order());
                refresh(); string start = order();
                // Disable, then Undo.
                Call(form, "ToggleGameRow", row("modA")); WaitIdle(form); refresh();
                Require(!engine.LoadState().Mods.First(m => m.SourcePath == f.Mod("modA")).Enabled && form.UndoAvailable && form.UndoLabel.StartsWith("Disabled"), "disable: " + order());
                undo(); Require(order() == start, "Undo of Disable: " + order() + " (was " + start + ")");
                // Move down, then Undo.
                ListView list = Get<ListView>(form, "installedList");
                foreach (ListViewItem item in list.Items) item.Selected = item.Tag is GameModRow && ((GameModRow)item.Tag).Mod != null && ((GameModRow)item.Tag).Mod.SourcePath == f.Mod("modA");
                Application.DoEvents(); Call(form, "MoveGameRow", 1); WaitIdle(form); refresh();
                Require(order() != start && form.UndoLabel.Contains("moved"), "move: " + order());
                undo(); Require(order() == start, "Undo of Move: " + order() + " (was " + start + ")");
                // A second record of modA (2.4.0 could leave duplicates), the newest one disabled.
                Load(form, f, "modA"); WaitReview(form);
                CompositionPlan comp = Get<CompositionPlan>(form, "plan");
                ReplacePlan dup = ReplacePlanner.Prepare(comp, "", engine, Path.Combine(engine.StoreRoot(engine.LoadState()), ".staging-undo-" + Guid.NewGuid().ToString("N")), null, null);
                engine.Install(dup, "modA second", false, null); engine.SetEnabled(engine.LoadState().Mods[0].Id, false);
                refresh(); string withDuplicate = order();
                // Restore original game files: no popup, Undo brings every record back in order with its enabled state.
                Call(form, "RestoreOriginalGameFiles"); WaitIdle(form); refresh();
                Require(engine.LoadState().Mods.Count == 0 && asked == 0 && engine.ListRemoved().Count == 3, "restore all: " + order() + ", kept copies " + engine.ListRemoved().Count);
                undo(); Require(order() == withDuplicate, "Undo of Restore original game files: " + order() + " (was " + withDuplicate + ")");
                // SF6 running: refused inline, nothing changes, no dialog.
                probe.Game = true; Call(form, "PollGame");
                Call(form, "UninstallAllReplacements", f.Mod("modA")); WaitIdle(form);
                Require(order() == withDuplicate && asked == 0, "uninstall ran while SF6 was running");
                probe.Game = false; Call(form, "PollGame");
                // The Mod library footer: one click, every record of this source, no menu, no dialog. 1.0.1: uninstall
                // means uninstall: no copy is kept (the library's source reinstalls it), so no Undo is offered.
                Call(form, "ShowPage", Get<Panel>(form, "libraryPage")); Load(form, f, "modA"); WaitReview(form);
                Call(form, "UninstallLibraryOutfit"); WaitIdle(form);
                string uninstallStatus = Get<Label>(form, "statusText").Text; refresh();
                Require(engine.LoadState().Mods.All(m => m.SourcePath != f.Mod("modA")) && engine.LoadState().Mods.Count == 1 && asked == 0 && uninstallStatus.Contains("Uninstalled") && uninstallStatus.Contains("(2)") && !form.UndoAvailable && engine.ListRemoved().Count == 0,
                    "footer uninstall: " + order() + ", asked " + asked + ", status " + uninstallStatus + ", undo '" + form.UndoLabel + "', kept copies " + engine.ListRemoved().Count);
                // A new change ends the previous Undo.
                Call(form, "ToggleGameRow", row("tool")); WaitIdle(form); refresh();
                Require(form.UndoAvailable, "no Undo after Disable");
                Call(form, "ToggleGameRow", row("tool")); WaitIdle(form); refresh();
                Require(form.UndoLabel.StartsWith("Enabled"), "the Undo did not follow the latest change: " + form.UndoLabel);
            }
            finally { ThemedMessageBox.TestResponder = null; probe.Game = false; Call(form, "PollGame"); engine.RestoreAll(); Call(form, "RefreshInstalled", (string)null); WaitIdle(form); Call(form, "ShowPage", Get<Panel>(form, "libraryPage")); Application.DoEvents(); }
        }

        // Space as the window receives it: WM_KEYDOWN through ProcessCmdKey; bit 30 of lParam marks an auto-repeat.
        private static bool PressSpace(MainForm form, Control focused, bool repeat)
        {
            form.FocusOverride = focused;
            try
            {
                Message msg = Message.Create(form.Handle, 0x0100, (IntPtr)(int)Keys.Space, (IntPtr)(repeat ? 0x40390001 : 0x00390001));
                object[] args = { msg, Keys.Space };
                return (bool)typeof(MainForm).GetMethod("ProcessCmdKey", PrivateInstance).Invoke(form, args);
            }
            finally { form.FocusOverride = null; }
        }
        private static void SpaceInstall(MainForm form, ReplaceChecks.Fixture f)
        {
            var engine = new ReplaceEngine(f.Game, AppConfig.DataDir, ReplaceSettings.Load());
            FlatButton install = Get<FlatButton>(form, "installButton"), health = Get<FlatButton>(form, "previewButton");
            Require(install.Text == "Install" && install.KeyHint == "Space" && install.AccessibleDescription == "Shortcut: Space" && health.Text == "Health", "footer labels: " + install.Text + " / " + health.Text);
            Control modList = Get<Control>(form, "modList"), tree = Get<Control>(form, "componentTree");
            Control searchEditor = Get<PaddedTextField>(form, "search").Editor, nameEditor = Get<PaddedTextField>(form, "outfitName").Editor;
            Action ready = delegate
            {
                Call(form, "LoadPackage", new LibraryEntry { SourcePath = f.Mod("modA"), Name = "modA" }); WaitIdle(form);
                Call(form, "BuildPlan", false); WaitIdle(form);
                Require(install.Enabled, "Install not ready: " + Summary(form));
            };
            Action undo = delegate { foreach (ReplaceMod m in engine.LoadState().Mods.ToList()) engine.Uninstall(m.Id); Call(form, "RefreshInstalled", (string)null); WaitIdle(form); };
            ready();
            int before = form.SpaceInstalls;
            // Where Space is not Install: the key goes on to the control (types a space, selects, presses).
            Require(!PressSpace(form, searchEditor, false) && !PressSpace(form, nameEditor, false) && !PressSpace(form, health, false) && form.SpaceInstalls == before, "Space was taken in a text field or on a button");
            form.ShowFilters(true, false); Application.DoEvents();
            Require(!PressSpace(form, Get<FilterPanel>(form, "filterPanel").Grid, false) && form.SpaceInstalls == before, "Space must select in the Filters panel");
            form.ShowFilters(false, false); Application.DoEvents();
            // Disabled Install: nothing happens (a click on a disabled button does nothing either); the key is still taken.
            install.Enabled = false;
            Require(PressSpace(form, modList, false) && form.SpaceInstalls == before && engine.LoadState().Mods.Count == 0, "Space with Install disabled");
            Call(form, "UpdateFooter"); Application.DoEvents(); Require(install.Enabled, "Install not re-enabled");
            // Auto-repeat alone installs nothing; the first key-down installs once.
            Require(PressSpace(form, modList, true) && form.SpaceInstalls == before, "an auto-repeat installed");
            Require(PressSpace(form, modList, false), "Space in the mod list was not taken"); WaitIdle(form);
            Require(PressSpace(form, modList, true), "repeat not taken"); WaitIdle(form);
            Require(form.SpaceInstalls == before + 1 && engine.LoadState().Mods.Count == 1, "Space from the mod list: " + (form.SpaceInstalls - before) + " presses, " + engine.LoadState().Mods.Count + " installed");
            undo(); ready();
            // From the options tree (the user's flow: click options, then Space).
            Require(PressSpace(form, tree, false), "Space in the options tree was not taken"); WaitIdle(form);
            Require(form.SpaceInstalls == before + 2 && engine.LoadState().Mods.Count == 1, "Space from the options tree installed " + engine.LoadState().Mods.Count);
            undo(); ready();
            // Enter toggles the focused option in the tree.
            OptionTree options = (OptionTree)tree;
            OptionNode node = AllNodes(options.Nodes).First(n => n.Tag is ModComponent && !((ModComponent)n.Tag).IsDummy);
            options.SelectedNode = node; bool was = node.Checked;
            typeof(Control).GetMethod("OnKeyDown", PrivateInstance).Invoke(options, new object[] { new KeyEventArgs(Keys.Enter) }); Application.DoEvents();
            Require(node.Checked == !was, "Enter did not toggle the option");
            typeof(Control).GetMethod("OnKeyDown", PrivateInstance).Invoke(options, new object[] { new KeyEventArgs(Keys.Enter) }); Application.DoEvents();
            Require(node.Checked == was && options.AccessibleDescription.Contains("Enter") && options.AccessibleDescription.Contains("Space installs"), "Enter did not toggle back / accessible description: " + options.AccessibleDescription);
            WaitIdle(form);
            // Installed: Ctrl uninstalls there; Space is not taken (below).
            Require(engine.LoadState().Mods.Count == 0, "Space test left an install");
            ready();
            UninstallKeys(form, engine, modList, ready);
        }
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
        // A plain tap is posted as real WM_KEYDOWN / auto-repeat / WM_KEYUP messages, so it goes through the message loop
        // and the registered filter (2.5.5: 2.5.2–2.5.4 passed a direct call while the filter was never registered).
        // A key or click in between is fed to the handler directly (posting them would type or click in the control).
        private static void TapCtrl(MainForm form, Control focused, params int[] between)
        {
            form.FocusOverride = focused;
            try
            {
                if (between.Length == 0 && focused != null)
                {
                    PostMessage(focused.Handle, 0x0100, (IntPtr)0x11, (IntPtr)0x001D0001);
                    PostMessage(focused.Handle, 0x0100, (IntPtr)0x11, (IntPtr)0x401D0001);
                    PostMessage(focused.Handle, 0x0101, (IntPtr)0x11, unchecked((IntPtr)(int)0xC01D0001));
                    for (int i = 0; i < 5; i++) Application.DoEvents();
                    return;
                }
                form.CtrlTapMessage(0x0100, 0x11, false); form.CtrlTapMessage(0x0100, 0x11, true);
                foreach (int m in between) form.CtrlTapMessage(m, m == 0x0100 ? (int)Keys.C : 0, false);
                form.CtrlTapMessage(0x0101, 0x11, false);
            }
            finally { form.FocusOverride = null; }
        }
        // Ctrl tapped alone uninstalls in the Mod library (2.5.2) and on the Installed page (2.5.3; Space is not taken
        // there); W/S move in the Installed list. 2.5.3: the library's Uninstall removes a replacement also while
        // INSTALL AS shows Extra outfit (before, the button was enabled and did nothing).
        private static void UninstallKeys(MainForm form, ReplaceEngine engine, Control modList, Action ready)
        {
            FlatButton uninstall = Get<FlatButton>(form, "libraryUninstallButton");
            Control searchEditor = Get<PaddedTextField>(form, "search").Editor;
            Require(PressSpace(form, modList, false), "Space did not install"); WaitIdle(form);
            Require(engine.LoadState().Mods.Count == 1 && uninstall.Visible && uninstall.Enabled && uninstall.KeyHint == "Ctrl", "library Uninstall not ready: " + uninstall.Visible + "/" + uninstall.Enabled + "/" + uninstall.KeyHint);
            int ctrl = form.CtrlUninstalls;
            TapCtrl(form, modList, 0x0100); TapCtrl(form, modList, 0x0201); TapCtrl(form, searchEditor);
            Require(form.CtrlUninstalls == ctrl && engine.LoadState().Mods.Count == 1, "Ctrl+key, Ctrl+click or Ctrl in the search uninstalled");
            TapCtrl(form, modList); WaitIdle(form);
            Require(form.CtrlUninstalls == ctrl + 1 && engine.LoadState().Mods.Count == 0 && !form.UndoAvailable, "Ctrl tap did not uninstall (1.0.1: for good, no Undo): " + engine.LoadState().Mods.Count + ", undo '" + form.UndoLabel + "'");
            // The user's case: installed as a replacement, then INSTALL AS switched to Extra outfit.
            ready();
            Require(PressSpace(form, modList, false), "Space did not install"); WaitIdle(form);
            Call(form, "SetInstallMode", InstallModes.Slot, false); WaitIdle(form); Call(form, "UpdateLibraryUninstall");
            Require(uninstall.Visible && uninstall.Enabled && uninstall.Text == "Uninstall", "Extra outfit mode, replacement installed: " + uninstall.Visible + "/" + uninstall.Enabled + "/" + uninstall.Text);
            TapCtrl(form, modList); WaitIdle(form);
            Require(form.CtrlUninstalls == ctrl + 2 && engine.LoadState().Mods.Count == 0, "Uninstall in Extra outfit mode left the replacement: " + engine.LoadState().Mods.Count);
            Call(form, "SetInstallMode", InstallModes.Replace, false); WaitIdle(form);
            ready();
            Require(PressSpace(form, modList, false), "Space did not install again"); WaitIdle(form);
            Call(form, "ShowPage", Get<Panel>(form, "installedPage")); Application.DoEvents();
            Kind(form, "All"); Get<PaddedTextField>(form, "installedSearch").Text = ""; Application.DoEvents();
            ListView list = Get<ListView>(form, "installedList"); FlatButton remove = Get<FlatButton>(form, "removeInstalledButton");
            Control installedSearch = Get<PaddedTextField>(form, "installedSearch").Editor;
            foreach (ListViewItem item in list.SelectedItems.Cast<ListViewItem>().ToList()) item.Selected = false;
            Application.DoEvents();
            Require(list.Items.Count >= 1 && remove.KeyHint == "Ctrl", "installed rows " + list.Items.Count + " / hint " + remove.KeyHint);
            // W/S: next/previous row, one selected, nothing past the ends.
            Action<Keys> key = delegate(Keys k) { typeof(Control).GetMethod("OnKeyDown", PrivateInstance).Invoke(list, new object[] { new KeyEventArgs(k) }); Application.DoEvents(); };
            list.Items[0].Selected = true; list.Items[0].Focused = true; Application.DoEvents();
            if (list.Items.Count >= 2)
            {
                list.Items[1].Selected = true; Application.DoEvents();
                key(Keys.S); Require(list.SelectedIndices.Count == 1 && list.SelectedIndices[0] == 1, "S did not move to row 2");
                key(Keys.W);
            }
            key(Keys.W); Require(list.SelectedIndices.Count == 1 && list.SelectedIndices[0] == 0, "W did not move to row 1");
            key(Keys.S | Keys.Control); Require(list.SelectedIndices[0] == 0, "Ctrl+S moved");
            int row = list.Items.Cast<ListViewItem>().ToList().FindIndex(i => i.Tag is GameModRow && ((GameModRow)i.Tag).Mod != null);
            Require(row >= 0, "no replacement row");
            foreach (ListViewItem item in list.SelectedItems.Cast<ListViewItem>().ToList()) item.Selected = false;
            list.Items[row].Selected = true; list.Items[row].Focused = true; Application.DoEvents(); Call(form, "ShowInstalledSelection");
            Require(remove.Visible && remove.Enabled, "Installed Uninstall not ready");
            int taps = form.InstalledCtrlUninstalls;
            Require(!PressSpace(form, list, false) && engine.LoadState().Mods.Count == 1, "Space was taken on the Installed page");
            TapCtrl(form, installedSearch); TapCtrl(form, list, 0x0100);
            Require(form.InstalledCtrlUninstalls == taps && form.CtrlUninstalls == ctrl + 2 && engine.LoadState().Mods.Count == 1, "Ctrl in the Installed search or Ctrl+key uninstalled");
            TapCtrl(form, list); WaitIdle(form);
            Require(form.InstalledCtrlUninstalls == taps + 1 && form.CtrlUninstalls == ctrl + 2 && engine.LoadState().Mods.Count == 0 && !form.UndoAvailable, "Ctrl tap on Installed did not uninstall (1.0.1: for good, no Undo): " + engine.LoadState().Mods.Count + ", undo '" + form.UndoLabel + "'");
            Call(form, "ShowPage", Get<Panel>(form, "libraryPage")); Application.DoEvents();
            ready();
        }
        private static IEnumerable<OptionNode> AllNodes(System.Collections.IEnumerable nodes) { foreach (OptionNode n in nodes) { yield return n; foreach (OptionNode c in AllNodes(n.Nodes)) yield return c; } }

        private static void Layout(MainForm form, string root)
        {
            foreach (Size logical in new[] { new Size(1232, 640), new Size(1110, 610) })
            {
                form.ClientSize = new Size(DpiMetrics.Px(form, logical.Width), DpiMetrics.Px(form, logical.Height)); form.PerformLayout(); Application.DoEvents();
                foreach (string mode in new[] { InstallModes.Slot, InstallModes.Replace })
                {
                    Call(form, "SetInstallMode", mode, false); form.PerformLayout(); Application.DoEvents();
                    var buttons = new[] { "installButton", "previewButton", "libraryUninstallButton" }.Select(n => Get<FlatButton>(form, n)).Where(b => b.Visible).ToList();
                    foreach (FlatButton b in buttons)
                    {
                        Require(b.Parent.ClientRectangle.Contains(b.Bounds), logical + " " + mode + ": footer action clipped: " + b.Text);
                        foreach (FlatButton o in buttons) if (o != b) Require(!BoundsIn(b, form).IntersectsWith(BoundsIn(o, form)), "footer actions overlap");
                    }
                    Control modePanel = Get<Panel>(form, "modePanel"), tree = Get<OptionTree>(form, "componentTree");
                    Require(form.ClientRectangle.Contains(BoundsIn(modePanel, form)) && !BoundsIn(modePanel, form).IntersectsWith(BoundsIn(tree, form)), "mode panel placement");
                    { var c = Get<SegmentedToggle>(form, "modeToggle"); Require(c.Width >= DpiMetrics.Px(form, 200) && c.Height >= DpiMetrics.Px(form, 28) && c.Parent.ClientRectangle.Contains(c.Bounds) && c.SegmentBounds(0).Right == c.SegmentBounds(1).Left && c.SegmentBounds(1).Right == c.Width, "INSTALL AS toggle clipped or split: " + c.Bounds); }
                    Require(Get<Label>(form, "modeLockHint").Text == "" && !AllControls(form).Any(x => (x.Text ?? "").Contains("remembered")), "a \"· remembered\" hint is shown (removed in 2.3.1)");
                    Require(tree.ClientSize.Height >= DpiMetrics.Px(form, logical.Width == 1110 ? 270 : 300), logical + ": options height " + tree.ClientSize.Height);
                    Render(form, Path.Combine(root, "library-" + mode + "-" + logical.Width + "x" + logical.Height + "-" + form.DeviceDpi + ".png"));
                }
            }
            form.ClientSize = new Size(DpiMetrics.Px(form, 1232), DpiMetrics.Px(form, 640)); Application.DoEvents();
        }

        private static void InstallAndList(MainForm form, ReplaceChecks.Fixture f, string fluffy)
        {
            Call(form, "SetInstallMode", InstallModes.Replace, true);
            Call(form, "BuildPlan", false); WaitIdle(form);
            Call(form, "InstallCurrent"); WaitIdle(form);
            ReplaceState state = new ReplaceEngine(f.Game, AppConfig.DataDir, ReplaceSettings.Load()).LoadState();
            Require(state.Mods.Count == 1 && File.Exists(Path.Combine(f.Game, ReplaceChecks.V1.Replace('/', '\\'))), "replacement not installed");
            string ini = Path.Combine(fluffy, "installed.ini"), saved = File.ReadAllText(ini);
            File.WriteAllText(ini, saved + "[Fluffy Tool]\r\nModName=Fluffy Tool\r\ninstalltype=pak_mods\r\nfile=reframework/autorun/fluffy_tool.lua\r\n");
            try
            {
            Call(form, "RefreshInstalled", (string)null); WaitIdle(form);
            Call(form, "ShowPage", Get<Panel>(form, "installedPage")); Application.DoEvents();
            var list = Get<ListView>(form, "installedList");
            var rows = list.Items.Cast<ListViewItem>().Select(i => i.Tag).OfType<GameModRow>().ToList();
            Require(rows.Any(r => r.Mod != null && r.Priority == 1) && rows.Any(r => r.Fluffy != null && r.Name == "Fluffy Tool"), "installed rows: " + String.Join(", ", rows.Select(r => r.Name)));
            // 1.0.1: the type filter is All / Extra / Installed; Installed is every game-folder row (replacements, tool
            // mods, PAK mods and the external manager's rows), Extra none of them.
            Kind(form, "Extra");
            Require(!list.Items.Cast<ListViewItem>().Any(i => i.Tag is GameModRow), "Extra filter lists game-folder rows");
            Kind(form, "Installed");
            var shown = list.Items.Cast<ListViewItem>().ToList();
            // 1.0.11: the fixture's unlisted patch PAK is a leftover row too.
            Require(shown.Count == 3 && shown.All(i => i.Tag is GameModRow) && shown.Any(i => ((GameModRow)i.Tag).Fluffy != null) && shown.Any(i => ((GameModRow)i.Tag).Mod != null) && shown.Any(i => ((GameModRow)i.Tag).Leftover != null), "Installed filter: " + String.Join(", ", shown.Select(i => i.Text)));
            Func<Func<GameModRow, bool>, ListViewItem> pick = delegate(Func<GameModRow, bool> match)
            {
                ListViewItem item = list.Items.Cast<ListViewItem>().First(i => i.Tag is GameModRow && match((GameModRow)i.Tag));
                foreach (ListViewItem i in list.Items) i.Selected = i == item;
                Application.DoEvents(); Call(form, "ShowInstalledSelection"); return item;
            };
            pick(r => r.Fluffy != null);
            // 1.0.1: external rows show Uninstall disabled with the reason (that manager uninstalls its own mods).
            FlatButton remove = Get<FlatButton>(form, "removeInstalledButton");
            Require(remove.Visible && !remove.Enabled && remove.AccessibleDescription == "Uninstall it in the external mod manager." && !Get<FlatButton>(form, "toggleInstalledButton").Visible, "Fluffy PAK row: Uninstall must be shown disabled with the reason");
            pick(r => r.Mod != null);
            Require(Get<FlatButton>(form, "toggleInstalledButton").Text == "Disable" && Get<FlatButton>(form, "removeInstalledButton").Visible && Get<FlatButton>(form, "moveUpButton").Visible && !Get<FlatButton>(form, "moveUpButton").Enabled, "replacement actions");
            Require(Get<ChipStrip>(form, "installedChips").Chips.Any(c => c.Text == "#1") && Get<ChipStrip>(form, "installedChips").Chips.Any(c => c.Text == "Enabled"), "replacement chips");
            }
            finally { File.WriteAllText(ini, saved); Kind(form, "All"); Call(form, "RefreshInstalled", (string)null); WaitIdle(form); }
        }

        private static void Banner(MainForm form, ReplaceChecks.Fixture f)
        {
            Call(form, "ShowPage", Get<Panel>(form, "installedPage")); Application.DoEvents();
            File.Delete(Path.Combine(f.Game, ReplaceChecks.V2.Replace('/', '\\')));
            Call(form, "RefreshInstalled", (string)null); WaitIdle(form);
            Panel banner = Get<Panel>(form, "gameBanner");
            Require(banner.Visible && Get<Label>(form, "gameBannerText").Text.StartsWith("Game files changed") && Get<FlatButton>(form, "bannerReapply").Visible, "banner not shown");
            Get<FlatButton>(form, "bannerReapply").PerformClick(); WaitIdle(form);
            Require(!banner.Visible && File.Exists(Path.Combine(f.Game, ReplaceChecks.V2.Replace('/', '\\'))), "Reapply did not repair and clear the banner");
        }

        private static void ReapplyNotice(MainForm form, ReplaceChecks.Fixture f, RecordingLauncher launcher)
        {
            string v2 = Path.Combine(f.Game, ReplaceChecks.V2.Replace('/', '\\'));
            Panel library = Get<Panel>(form, "libraryPage"), installed = Get<Panel>(form, "installedPage");
            Func<List<FlatButton>> buttons = delegate { return form.UpdateToast(MainForm.GameFilesToast).Controls.OfType<FlowLayoutPanel>().Single().Controls.OfType<FlatButton>().ToList(); };
            try
            {
                Call(form, "ShowPage", library); Application.DoEvents();
                Require(form.UpdateToast(MainForm.GameFilesToast) == null, "a notice while the game files match");
                File.Delete(v2);
                Call(form, "RefreshInstalled", (string)null); WaitIdle(form);
                Panel toast = form.UpdateToast(MainForm.GameFilesToast);
                Require(toast != null && OverlayScroller.OwnVisible(toast.Parent) && toast.AccessibleName.StartsWith("Game files changed") && toast.AccessibleRole == AccessibleRole.Alert, "no notice on the Mod library: " + (toast == null ? "none" : toast.AccessibleName));
                Require(buttons().Select(b => b.Text).SequenceEqual(new[] { "Reapply", "Show details", "✕" }) && buttons().All(b => b.TabStop && !String.IsNullOrEmpty(b.AccessibleName)) && buttons()[0].Enabled, "notice buttons: " + String.Join(", ", buttons().Select(b => b.Text + (b.Enabled ? "" : " (off)"))));
                // Installed has the banner instead; back on the Mod library the toast returns.
                Call(form, "ShowPage", installed); Application.DoEvents();
                Require(form.UpdateToast(MainForm.GameFilesToast) == null && Get<Panel>(form, "gameBanner").Visible, "the notice must give way to the Installed banner");
                Call(form, "ShowPage", library); Application.DoEvents();
                Require(form.UpdateToast(MainForm.GameFilesToast) != null, "the notice did not come back on the Mod library");
                // ✕ hides it for the session, also across refreshes; Launch brings it back with the warning.
                buttons()[2].PerformClick(); Application.DoEvents();
                Call(form, "RefreshInstalled", (string)null); WaitIdle(form);
                Require(form.UpdateToast(MainForm.GameFilesToast) == null, "a dismissed notice came back without a change");
                int before = launcher.Count;
                Get<FlatButton>(form, "launchButton").PerformClick(); Application.DoEvents();
                string status = Get<Label>(form, "statusText").Text;
                Require(launcher.Count == before && status.Contains("click Launch SF6 again") && form.UpdateToast(MainForm.GameFilesToast) != null, "first Launch with a Reapply pending: " + launcher.Count + " / " + status);
                Get<FlatButton>(form, "launchButton").PerformClick(); Application.DoEvents();
                Require(launcher.Count == before + 1, "the second Launch did not start the game");
                // Reapply from the toast repairs, clears it, and Launch starts at once.
                buttons()[0].PerformClick(); WaitIdle(form);
                Require(File.Exists(v2) && form.UpdateToast(MainForm.GameFilesToast) == null, "Reapply from the notice did not repair and clear it");
                Get<FlatButton>(form, "launchButton").PerformClick(); Application.DoEvents();
                Require(launcher.Count == before + 2, "Launch still warned after the Reapply");
            }
            finally
            {
                if (!File.Exists(v2)) { Call(form, "ShowPage", installed); Call(form, "RefreshInstalled", (string)null); WaitIdle(form); Get<FlatButton>(form, "bannerReapply").PerformClick(); WaitIdle(form); }
                Call(form, "ShowPage", library); Application.DoEvents();
            }
        }

        private static void Launch(MainForm form, FakeProcessProbe probe, RecordingLauncher launcher)
        {
            int before = launcher.Count;
            Get<FlatButton>(form, "launchButton").PerformClick();
            Require(launcher.Count == before + 1, "launcher not called");
            probe.Game = true; Call(form, "PollGame");
            Require(Get<Label>(form, "gameStateLabel").Text.Contains("Game running") && !Get<FlatButton>(form, "launchButton").Enabled && !Get<FlatButton>(form, "installButton").Enabled, "running state");
            probe.Game = false; probe.Fluffy = true; Call(form, "PollGame");
            Require(Get<Label>(form, "gameStateLabel").Text.Contains("Mod manager running") && !Get<FlatButton>(form, "installButton").Enabled, "Fluffy running state");
            probe.Fluffy = false; Call(form, "PollGame");
            Require(Get<Label>(form, "gameStateLabel").Text == "Game closed", "closed state");
        }

        private static void LeftoverTest(MainForm form, ReplaceChecks.Fixture f)
        {
            Require(typeof(MainForm).GetField("testButton", BindingFlags.NonPublic | BindingFlags.Instance) == null && typeof(MainForm).GetMethod("TestInGame", BindingFlags.NonPublic | BindingFlags.Instance) == null, "Test in game is still in the window");
            ShowLibrary(form);
            var actions = Get<FlatButton>(form, "installButton").Parent;
            Require(actions.Controls.OfType<FlatButton>().Select(b => b.Text).All(t => !t.Contains("Test")) && actions.Controls.Count == 3, "footer actions: " + String.Join(", ", actions.Controls.OfType<FlatButton>().Select(b => b.Text)));
            var engine = new ReplaceEngine(f.Game, AppConfig.DataDir, ReplaceSettings.Load());
            ReplaceMod target = engine.LoadState().Mods.First();
            TestSession.Save(AppConfig.DataDir, new TestSessionRecord { Mode = InstallModes.Replace, ReplaceModId = target.Id, Name = target.Name, Phase = "ended" });
            var before = ReplaceChecks.Snapshot(f.Game);
            int forms = Application.OpenForms.Count;
            Call(form, "FinishLeftoverTest"); WaitIdle(form);
            Require(TestSession.Load(AppConfig.DataDir) == null && engine.LoadState().Mods.Any(m => m.Id == target.Id && !m.Test && m.Enabled) && Application.OpenForms.Count == forms, "leftover test not kept quietly");
            ReplaceChecks.SameSnapshot(before, ReplaceChecks.Snapshot(f.Game), "after keeping a leftover test");
            Require(File.ReadAllText(Path.Combine(AppConfig.DataDir, "log.txt")).Contains("Test in game (removed in 2.3.0): Kept '" + target.Name + "'"), "leftover not logged");
        }
        private static void ShowLibrary(MainForm form) { Call(form, "ShowPage", Get<Panel>(form, "libraryPage")); Application.DoEvents(); }

        private static void Uninstall(MainForm form, ReplaceChecks.Fixture f, Dictionary<string, string> clean)
        {
            Call(form, "ShowPage", Get<Panel>(form, "installedPage")); Call(form, "RefreshInstalled", (string)null); WaitIdle(form);
            var list = Get<ListView>(form, "installedList");
            ListViewItem row = list.Items.Cast<ListViewItem>().First(i => i.Tag is GameModRow && ((GameModRow)i.Tag).Mod != null);
            foreach (ListViewItem i in list.Items) i.Selected = false;
            row.Selected = true; Application.DoEvents(); Call(form, "ShowInstalledSelection");
            Get<FlatButton>(form, "removeInstalledButton").PerformClick(); WaitIdle(form);
            ReplaceChecks.SameSnapshot(clean, ReplaceChecks.Snapshot(f.Game), "after uninstalling from the Installed page");
            Require(!list.Items.Cast<ListViewItem>().Any(i => i.Tag is GameModRow && ((GameModRow)i.Tag).Mod != null), "row still listed");
        }

        private static void LibraryReplacementUninstall(MainForm form, ReplaceChecks.Fixture f, FakeProcessProbe probe, Dictionary<string, string> clean)
        {
            ReplaceEngine engine = FormEngine(f);
            Call(form, "SetInstallMode", InstallModes.Replace, true);
            Load(form, f, "modA"); WaitReview(form);
            Call(form, "InstallCurrent"); WaitReview(form);
            ReplaceMod mod = engine.LoadState().Mods.Single();
            ListView list = Get<ListView>(form, "modList"); ContextMenuStrip menu = list.ContextMenuStrip;
            var outfitItem = (ToolStripMenuItem)menu.Items[4]; var replacementItem = (ToolStripMenuItem)menu.Items[5];   // 1.0.5: Rename is item 2
            FlatButton button = Get<FlatButton>(form, "libraryUninstallButton");


            LibraryEntry entry = new LibraryEntry { SourcePath = f.Mod("modA"), Name = "modA", Kind = "Folder" };
            Action open = delegate { menu.Tag = entry; menu.Show(list, new Point(4, 4)); Application.DoEvents(); };
            List<InstalledOutfit> oldInstalled = Get<List<InstalledOutfit>>(form, "installed");
            try
            {
                open();
                Require(menu.Visible && replacementItem.Visible && replacementItem.Enabled && replacementItem.Text == "Uninstall replacement" && !outfitItem.Visible
                    && replacementItem.Tag is GameModRow && ((GameModRow)replacementItem.Tag).Mod.Id == mod.Id && replacementItem.AccessibleDescription == "", "right-click on a replacement source: " + replacementItem.Text + " visible=" + replacementItem.Visible + " enabled=" + replacementItem.Enabled + " menu=" + menu.Visible + " outfit=" + outfitItem.Visible + " tag=" + replacementItem.Tag + " reason=" + replacementItem.AccessibleDescription + " name=" + mod.Name);
                menu.Close();
                Require(button.Visible && button.Enabled && button.Text == "Uninstall", "Replace-mode footer Uninstall: visible=" + button.Visible + " enabled=" + button.Enabled + " " + button.Text);
                var buttons = new[] { "installButton", "previewButton", "libraryUninstallButton" }.Select(n => Get<FlatButton>(form, n)).Where(b => b.Visible).ToList();
                foreach (FlatButton b in buttons)
                {
                    Require(b.Parent.ClientRectangle.Contains(b.Bounds), "footer action clipped with Uninstall shown: " + b.Text);
                    foreach (FlatButton o in buttons) if (o != b) Require(!BoundsIn(b, form).IntersectsWith(BoundsIn(o, form)), "footer actions overlap with Uninstall shown");
                }
                // Fluffy running: disabled with the app's reason; a direct call changes nothing.
                probe.Fluffy = true; Call(form, "PollGame");
                open();
                Require(replacementItem.Visible && !replacementItem.Enabled && replacementItem.Text == "Uninstall replacement (close the external mod manager first: same PAK)" && replacementItem.AccessibleDescription.Contains("Close the external mod manager first"), "Fluffy running: " + replacementItem.Text + " / " + replacementItem.AccessibleDescription);
                menu.Close();
                Require(!button.Enabled && button.AccessibleDescription.Contains("Close the external mod manager first"), "footer Uninstall not disabled with the reason while Fluffy runs");
                Call(form, "UninstallLibraryReplacement", Get<List<GameModRow>>(form, "gameRows").First(r => r.Mod != null)); WaitReview(form);
                Require(engine.LoadState().Find(mod.Id) != null, "uninstalled while Fluffy runs");
                probe.Fluffy = false; Call(form, "PollGame");
                // Extra outfit mode: the footer covers the replacement (2.5.3: labelled Uninstall, and a click removes it).
                Call(form, "SetInstallMode", InstallModes.Slot, true);
                Require(button.Visible && button.Text == "Uninstall" && button.Enabled, "Extra outfit footer for a replacement-only source: " + button.Text);
                // An extra-outfit entry is unchanged: Uninstall outfit, no replacement action.
                string other = f.Mod("modC");
                Set(form, "installed", new List<InstalledOutfit> { new InstalledOutfit { Managed = true, Enabled = true, Character = "Cammy", Name = "Extra C", SourcePath = other } });
                menu.Tag = new LibraryEntry { SourcePath = other, Name = "modC", Kind = "Folder" }; menu.Show(list, new Point(4, 4)); Application.DoEvents();
                Require(outfitItem.Visible && outfitItem.Enabled && outfitItem.Text == "Uninstall outfit" && outfitItem.Tag is InstalledOutfit && !replacementItem.Visible, "extra-outfit entry menu changed");
                menu.Close();
                menu.Tag = new LibraryEntry { SourcePath = f.Mod("modB"), Name = "modB", Kind = "Folder" }; menu.Show(list, new Point(4, 4)); Application.DoEvents();
                Require(outfitItem.Visible && !outfitItem.Enabled && !replacementItem.Visible, "uninstalled entry menu changed");
                menu.Close();
                Set(form, "installed", oldInstalled);
                Call(form, "SetInstallMode", InstallModes.Replace, true);
                // Uninstall from the right-click menu: the Installed page's flow (1.0.1: for good; the source reinstalls it).
                open();
                replacementItem.PerformClick(); WaitReview(form);
                Require(engine.LoadState().Find(mod.Id) == null && !engine.ListRemoved().Any(r => r.Record.Mod.Id == mod.Id) && !form.UndoAvailable, "not uninstalled for good from the library menu (a copy or an Undo was kept)");
                ReplaceChecks.SameSnapshot(clean, ReplaceChecks.Snapshot(f.Game), "after uninstalling from the library menu");
                Require(!button.Visible, "Replace-mode Uninstall still shown without a replacement");
            }
            finally
            {
                menu.Close(); probe.Fluffy = false; Call(form, "PollGame"); Set(form, "installed", oldInstalled);
                // Leave the next checks a clean game folder even if this one failed.
                if (engine.LoadState().Find(mod.Id) != null) { engine.Uninstall(mod.Id); Call(form, "RefreshInstalled", (string)null); WaitReview(form); }
            }
        }

        private static void SettingsTab(MainForm form, string root)
        {
            foreach (Size logical in new[] { new Size(1232, 640), new Size(1110, 610) })
            {
                form.ClientSize = new Size(DpiMetrics.Px(form, logical.Width), DpiMetrics.Px(form, logical.Height)); Application.DoEvents();
                Panel page = Get<Panel>(form, "settingsPage");
                Call(form, "ShowPage", page); Call(form, "LoadSettingsFields"); Call(form, "ShowSettingsTab", true); form.PerformLayout(); Application.DoEvents();
                Panel panel = Get<Panel>(form, "settingsReplacePanel");
                Require(panel.Visible && !Get<ListBox>(form, "settingsLibraries").Visible, "tab switch");
                foreach (string caption in new[] { "Replacement method", "REFramework and Slotsloader", "Game files", "Replacement store", "External mod manager" })
                {
                    Label label = AllControls(panel).OfType<Label>().FirstOrDefault(l => l.Text == caption);
                    Require(label != null && page.ClientRectangle.Contains(BoundsIn(label, page)), "caption missing or clipped: " + caption + " at " + logical);
                }
                Require(!AllControls(panel).OfType<Label>().Any(l => l.Text == "Default install mode") && !AllControls(panel).OfType<ComboBox>().Any(c => c.Items.Cast<object>().Any(i => Convert.ToString(i).Contains("Extra outfit"))), "Settings still shows Default install mode at " + logical);
                foreach (Button b in AllControls(panel).OfType<Button>()) Require(b.Parent.ClientRectangle.Contains(b.Bounds) && page.ClientRectangle.Contains(BoundsIn(b, page)), "button clipped: " + b.Text + " at " + logical);
                Require(Get<Label>(form, "settingsFluffyStatus").Text.Contains("0 mods installed by an external mod manager"), "Fluffy summary (the fixture's external manager is empty since 1.0.1): " + Get<Label>(form, "settingsFluffyStatus").Text);
                Require(Get<Label>(form, "settingsMethodStatus").Text.Contains("PAK method"), "method status: " + Get<Label>(form, "settingsMethodStatus").Text);
                Render(form, Path.Combine(root, "settings-replace-" + logical.Width + "x" + logical.Height + "-" + form.DeviceDpi + ".png"));
                Call(form, "ShowSettingsTab", false); Application.DoEvents();
                Require(!panel.Visible && Get<ListBox>(form, "settingsLibraries").Visible && Get<ListBox>(form, "settingsLibraries").Height >= DpiMetrics.Px(form, 100), "General tab after switching back");
            }
            form.ClientSize = new Size(DpiMetrics.Px(form, 1232), DpiMetrics.Px(form, 640)); Application.DoEvents();
        }

        // ---- 2.0.1 checks
        private static void WaitReview(MainForm form)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(60);
            do { Application.DoEvents(); Thread.Sleep(10); if (!form.Working) { Application.DoEvents(); if (!form.Working) return; } } while (DateTime.UtcNow < deadline);
            throw new InvalidOperationException("The automatic review did not finish.");
        }
        private static void WaitBusyOnly(MainForm form)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(60);
            do { Application.DoEvents(); Thread.Sleep(5); } while (Get<bool>(form, "busy") && DateTime.UtcNow < deadline);
        }
        private static ReplaceEngine FormEngine(ReplaceChecks.Fixture f) { return new ReplaceEngine(f.Game, AppConfig.DataDir, ReplaceSettings.Load()); }
        private static string Summary(MainForm form) { return Get<Label>(form, "planSummary").Text; }
        private static string Details(MainForm form) { return Get<Label>(form, "planDetails").Text; }
        private static void Load(MainForm form, ReplaceChecks.Fixture f, string mod) { ShowLibrary(form); Call(form, "LoadPackage", new LibraryEntry { SourcePath = f.Mod(mod), Name = mod }); WaitBusyOnly(form); }

        private static void AutoCheck(MainForm form, ReplaceChecks.Fixture f, string root)
        {
            Call(form, "SetInstallMode", InstallModes.Replace, true);
            MainForm.ReviewTestDelay = 900;
            try
            {
                Load(form, f, "modA");
                Require(form.ReviewPending && Summary(form) == "Checking Cammy Colors…" && !Get<FlatButton>(form, "installButton").Enabled, "checking state: " + Summary(form));
                Require(!Get<bool>(form, "busy") && Get<OptionTree>(form, "componentTree").Enabled, "the check set the app-wide busy state");
                DateTime until = DateTime.UtcNow.AddSeconds(5); while (!form.ReviewRunning && DateTime.UtcNow < until) { Application.DoEvents(); Thread.Sleep(5); }
                Render(form, Path.Combine(root, "library-replace-checking-" + form.DeviceDpi + ".png"));
            }
            finally { MainForm.ReviewTestDelay = 0; }
            WaitReview(form);
            Require(Summary(form).StartsWith("Ready to install") && Get<FlatButton>(form, "installButton").Enabled, "not ready automatically: " + Summary(form) + " / " + Details(form));
            Require(Get<FlatButton>(form, "previewButton").Text == "Health" && !Get<FlatButton>(form, "previewButton").Emphasis, "Health button");
            Require(Get<ReplacePlan>(form, "replacePlan") != null && Get<Label>(form, "replacesLabel").Text.Contains("Cammy Outfit 2"), "inspector after the automatic check");
            Render(form, Path.Combine(root, "library-replace-ready-" + form.DeviceDpi + ".png"));
        }

        // 2.4.1: the user edits the zip that is open in the inspector (their own mod, while iterating on it).
        private static void WriteEditZip(string zip, string main, string extraFolder, string extraName, string extra)
        {
            string staging = zip + ".staging"; if (Directory.Exists(staging)) Directory.Delete(staging, true);
            ReplaceChecks.Put(staging, "modinfo.ini", System.Text.Encoding.UTF8.GetBytes("name=Edit Me\r\ndummymod=true\r\n"));
            ReplaceChecks.Put(staging, "Main Files/modinfo.ini", System.Text.Encoding.UTF8.GetBytes("name=Main Files\r\n"));
            ReplaceChecks.Put(staging, "Main Files/" + ReplaceChecks.V5, System.Text.Encoding.UTF8.GetBytes(main));
            ReplaceChecks.Put(staging, extraFolder + "/modinfo.ini", System.Text.Encoding.UTF8.GetBytes("name=" + extraName + "\r\n"));
            ReplaceChecks.Put(staging, extraFolder + "/natives/stm/product/model/esf/esf009/002/01/edit_extra.tex.241101895", System.Text.Encoding.UTF8.GetBytes(extra));
            if (File.Exists(zip)) File.Delete(zip);
            System.IO.Compression.ZipFile.CreateFromDirectory(staging, zip);
            Directory.Delete(staging, true);
        }
        private static void SourceReload(MainForm form, ReplaceChecks.Fixture f)
        {
            string zip = f.Mod("Edit Me.zip");
            ReplaceEngine engine = FormEngine(f);
            int asked = 0; ThemedMessageBox.TestResponder = delegate(ThemedMessageForm box) { asked++; return DialogResult.Cancel; };
            Func<string> problems = delegate { return Summary(form) + " / " + Details(form) + " / " + Get<Label>(form, "statusText").Text; };
            Action noStale = delegate { Require(problems().IndexOf("changed on disk", StringComparison.OrdinalIgnoreCase) < 0, "a changed-on-disk problem reached the user: " + problems()); };
            Func<List<string>> picked = delegate { return Get<List<string>>(form, "selectedOrder").ToList(); };
            Action<int> settle = delegate(int reloadsBefore)
            {
                DateTime deadline = DateTime.UtcNow.AddSeconds(30);
                while (form.SourceReloads == reloadsBefore && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(10); }
                WaitIdle(form); WaitReview(form);
            };
            try
            {
                WriteEditZip(zip, "main v1", "Extra", "Extra", "extra v1");
                Call(form, "SetInstallMode", InstallModes.Replace, true);
                Load(form, f, "Edit Me.zip"); WaitReview(form);
                ExtraNode(form).Checked = true; WaitReview(form);
                string extraId = picked().First(id => id.IndexOf("Extra", StringComparison.OrdinalIgnoreCase) >= 0);
                Require(Get<FlatButton>(form, "installButton").Enabled, "fixture: Install not enabled: " + problems());
                // 1. F5: the list is rescanned and the open mod reloads with its selection.
                WriteEditZip(zip, "main v2, longer content", "Extra", "Extra", "extra v2"); File.SetLastWriteTimeUtc(zip, DateTime.UtcNow.AddMinutes(1));
                int before = form.SourceReloads;
                // The rescan also re-checks portraits once Filters was opened; this suite's fixture game must not leave a
                // portrait cache in the shared Data (filter-real requires none).
                bool portraits = Get<bool>(form, "portraitsRequested"); Set(form, "portraitsRequested", false);
                try { Call(form, "RefreshLibrary", false); settle(before); } finally { Set(form, "portraitsRequested", portraits); }
                Require(form.SourceReloads == before + 1 && picked().Contains(extraId) && form.IsReplaceMode && Get<FlatButton>(form, "installButton").Enabled && Get<Label>(form, "statusText").Text.StartsWith("Reloaded Edit Me: the file changed."), "F5 did not reload: reloads " + (form.SourceReloads - before) + ", selection " + String.Join(",", picked()) + ", " + problems());
                noStale();
                // 2. Window activation.
                WriteEditZip(zip, "main v3", "Extra", "Extra", "extra v3 · activation"); File.SetLastWriteTimeUtc(zip, DateTime.UtcNow.AddMinutes(2));
                before = form.SourceReloads;
                Call(form, "CheckOpenPackageOnActivate"); settle(before);
                Require(form.SourceReloads == before + 1 && picked().Contains(extraId) && Get<FlatButton>(form, "installButton").Enabled, "activation did not reload: " + problems());
                noStale();
                // 3. The option ids changed in the edit (folder renamed): the selection follows the option name. Install
                //    on a stale package reloads instead of installing.
                WriteEditZip(zip, "main v4", "Extra v2", "Extra", "extra v4"); File.SetLastWriteTimeUtc(zip, DateTime.UtcNow.AddMinutes(3));
                before = form.SourceReloads; int records = engine.LoadState().Mods.Count;
                Call(form, "InstallCurrent"); settle(before);
                string renamed = picked().FirstOrDefault(id => id.IndexOf("Extra v2", StringComparison.OrdinalIgnoreCase) >= 0);
                Require(form.SourceReloads == before + 1 && renamed != null && engine.LoadState().Mods.Count == records && String.IsNullOrEmpty(form.LastReloadDropped), "renamed option: selection " + String.Join(",", picked()) + ", dropped '" + form.LastReloadDropped + "', records " + engine.LoadState().Mods.Count);
                noStale();
                // 4. The library index re-reads the edited archive (stamp = size + write time): option ids follow.
                LibraryEntry indexed = Library.Scan(new[] { f.Mods }, "").First(e => String.Equals(e.SourcePath, zip, StringComparison.OrdinalIgnoreCase));
                Require(indexed.OptionIds.Any(id => id.IndexOf("Extra v2", StringComparison.OrdinalIgnoreCase) >= 0) && !indexed.OptionIds.Any(id => id.EndsWith("Extra", StringComparison.OrdinalIgnoreCase)), "the index served the old listing: " + String.Join(",", indexed.OptionIds));
                // 5. An installed copy: the edit marks it; Reinstall applies it in place (same record, no warning).
                Call(form, "InstallCurrent"); WaitIdle(form);
                ReplaceMod mod = engine.LoadState().Mods.Single(m => String.Equals(m.SourcePath, zip, StringComparison.OrdinalIgnoreCase));
                DateTime installedAt = LibraryUsage.ParseUtc(mod.InstalledUtc), edited = installedAt.AddSeconds(5);
                WriteEditZip(zip, "main v5, edited after install", "Extra v2", "Extra", "extra v5"); File.SetLastWriteTimeUtc(zip, edited);
                while (DateTime.UtcNow < edited.AddSeconds(1)) { Application.DoEvents(); Thread.Sleep(50); }
                Call(form, "ShowPage", Get<Panel>(form, "installedPage")); Call(form, "RefreshInstalled", (string)null); WaitIdle(form);
                Require(form.SourceChanged(zip), "the installed copy is not marked Source changed");
                ListView list = Get<ListView>(form, "installedList");
                foreach (ListViewItem item in list.Items) item.Selected = item.Tag is GameModRow && ((GameModRow)item.Tag).Mod != null && ((GameModRow)item.Tag).Mod.Id == mod.Id;
                Application.DoEvents(); Call(form, "ShowInstalledSelection");
                FlatButton reinstall = Get<FlatButton>(form, "reinstallButton");
                bool offered = (bool)Call(form, "InstalledActionShown", reinstall);   // Control.Visible reads false while hidden
                Require(offered && reinstall.Enabled && reinstall.Text == "Reinstall", "Reinstall not offered: shown " + offered + ", enabled " + reinstall.Enabled);
                Render(form, Path.Combine(Path.GetDirectoryName(f.Mods), "installed-source-changed-" + form.DeviceDpi + ".png"));
                string oldHash = mod.Files.First(x => x.Path.EndsWith(".mesh.230110883", StringComparison.OrdinalIgnoreCase) || x.Path.Contains(Path.GetFileName(ReplaceChecks.V5))).Sha256;
                int position = engine.LoadState().Mods.FindIndex(m => m.Id == mod.Id);
                Call(form, "ReinstallSelected"); WaitIdle(form);
                var after = engine.LoadState().Mods.Where(m => String.Equals(m.SourcePath, zip, StringComparison.OrdinalIgnoreCase)).ToList();
                Require(after.Count == 1 && engine.LoadState().Mods.FindIndex(m => m.Id == after[0].Id) == position && after[0].Files.Any(x => x.Sha256 != oldHash && x.Path.Contains(Path.GetFileName(ReplaceChecks.V5))) && !form.SourceChanged(zip) && asked == 0 && Get<Label>(form, "statusText").Text.Contains("Reinstalled"), "Reinstall: records " + after.Count + ", records " + after.Count + ", still marked " + form.SourceChanged(zip) + ", asked " + asked + ", status " + Get<Label>(form, "statusText").Text);
                // 6. The source is deleted: the inspector clears and says so; the installed copy stays.
                Call(form, "ShowPage", Get<Panel>(form, "libraryPage")); Load(form, f, "Edit Me.zip"); WaitReview(form);
                File.Delete(zip);
                Call(form, "CheckOpenPackage"); WaitReview(form);
                Require(Get<ModPackage>(form, "package") == null && Get<Label>(form, "statusText").Text.Contains("is no longer on disk") && Get<Label>(form, "packageTitle").Text == "Choose a mod" && !Get<FlatButton>(form, "installButton").Enabled, "deleted source: " + problems());
                Require(engine.LoadState().Mods.Any(m => String.Equals(m.SourcePath, zip, StringComparison.OrdinalIgnoreCase)) && asked == 0, "the installed copy went with its source, or a dialog opened");
                // The selection mapping itself: an id that changed falls back to the one option with that name.
                List<string> dropped;
                var mapped = MainForm.MapSelection(new ModPackage { Components = { new ModComponent { Id = "Main Files", Name = "Main Files" }, new ModComponent { Id = "Color/Red v2", Name = "Red" } } }, new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>("Main Files", "Main Files"), new KeyValuePair<string, string>("Color/Red", "Red"), new KeyValuePair<string, string>("Color/Blue", "Blue") }, out dropped);
                Require(String.Join("|", mapped) == "Main Files|Color/Red v2" && String.Join("|", dropped) == "Blue", "MapSelection: " + String.Join("|", mapped) + " / dropped " + String.Join("|", dropped));
            }
            finally
            {
                ThemedMessageBox.TestResponder = null;
                foreach (ReplaceMod m in engine.LoadState().Mods.Where(m => String.Equals(m.SourcePath, zip, StringComparison.OrdinalIgnoreCase)).ToList()) engine.Uninstall(m.Id);
                if (File.Exists(zip)) File.Delete(zip);
                Call(form, "ShowPage", Get<Panel>(form, "libraryPage")); Call(form, "RefreshInstalled", (string)null); WaitIdle(form);
            }
        }
        private static void WriteMulti(ReplaceChecks.Fixture f)
        {
            string dir = f.Mod("modMulti");
            if (Directory.Exists(dir)) return;
            ReplaceChecks.Put(dir, "modinfo.ini", System.Text.Encoding.UTF8.GetBytes("name=Multi Pack\r\ndummymod=true\r\n"));
            ReplaceChecks.Put(dir, "Main Files/modinfo.ini", System.Text.Encoding.UTF8.GetBytes("name=Main Files\r\n"));
            ReplaceChecks.Put(dir, "Main Files/" + ReplaceChecks.V5, System.Text.Encoding.UTF8.GetBytes("multi mesh"));
            ReplaceChecks.Put(dir, "Extra/modinfo.ini", System.Text.Encoding.UTF8.GetBytes("name=Extra\r\n"));
            ReplaceChecks.Put(dir, "Extra/natives/stm/product/model/esf/esf009/002/01/extra_a.tex.241101895", ReplaceChecks.Tex(128));
        }
        private static OptionNode ExtraNode(MainForm form)
        {
            var nodes = Get<Dictionary<string, OptionNode>>(form, "componentNodes");
            return nodes.Values.First(n => ((ModComponent)n.Tag).Name == "Extra");
        }
        private static string Selection(MainForm form) { return String.Join("|", Get<List<string>>(form, "selectedOrder")); }

        private static void Supersede(MainForm form, ReplaceChecks.Fixture f)
        {
            WriteMulti(f);
            Load(form, f, "modMulti"); WaitReview(form);
            OptionNode extra = ExtraNode(form);
            // Rapid changes inside the debounce window: one run, for the last selection.
            int runs = form.ReviewRuns, applied = form.ReviewApplied;
            for (int i = 0; i < 5; i++) extra.Checked = !extra.Checked;
            Require(form.ReviewPending && !Get<FlatButton>(form, "installButton").Enabled, "a change did not invalidate the result");
            WaitReview(form);
            Require(form.ReviewRuns == runs + 1 && form.ReviewApplied == applied + 1 && form.LastReviewSelection == Selection(form), "debounce: runs " + (form.ReviewRuns - runs) + ", applied " + (form.ReviewApplied - applied) + ", last " + form.LastReviewSelection + " vs " + Selection(form));
            // A change while a run is in flight: that run is discarded, the newest selection's result applies.
            MainForm.ReviewTestDelay = 600;
            try
            {
                int discarded = form.ReviewDiscarded; applied = form.ReviewApplied;
                extra.Checked = !extra.Checked;
                DateTime until = DateTime.UtcNow.AddSeconds(5); while (!form.ReviewRunning && DateTime.UtcNow < until) { Application.DoEvents(); Thread.Sleep(5); }
                Require(form.ReviewRunning, "the first run did not start");
                string stale = Selection(form);
                extra.Checked = !extra.Checked; extra.Checked = !extra.Checked; extra.Checked = !extra.Checked;
                string final = Selection(form);
                Require(final != stale, "fixture selections should differ");
                WaitReview(form);
                Require(form.ReviewDiscarded > discarded && form.ReviewApplied == applied + 1 && form.LastReviewSelection == final && Get<CompositionPlan>(form, "plan").Layers.Select(c => c.Id).OrderBy(x => x).SequenceEqual(Get<List<string>>(form, "selectedOrder").OrderBy(x => x)), "supersede: discarded " + (form.ReviewDiscarded - discarded) + ", applied " + (form.ReviewApplied - applied) + ", last " + form.LastReviewSelection + " vs " + final);
            }
            finally { MainForm.ReviewTestDelay = 0; }
        }

        // 2.4.1: Automatic picks the method that works now (loose only with the loader on, else PAK); Not now installs
        // with the PAK method; an explicit loose setting is respected; the banner converts loose installs to PAK;
        // a second install of the same source asks, and says clearly when No adds a second replacement.
        private static void AutomaticMethod(string root, FakeProcessProbe probe)
        {
            ReplaceChecks.Fixture f = ReplaceChecks.Create(Path.Combine(root, "auto-method"), true, ReplaceMethods.Loose);
            ReplaceSettings saved = ReplaceSettings.Load();
            new ReplaceSettings { Method = "" }.Save();
            string configPath = Path.Combine(f.Game, LooseLoader.ConfigName), original = File.ReadAllText(configPath);
            Require(LooseLoader.Read(f.Game, LooseLoader.EnabledKey) != "true", "fixture loader must start off");
            var config = new AppConfig { GameRoot = f.Game, Libraries = new List<string> { f.Mods }, SevenZipPath = "" };
            DialogResult answer = DialogResult.No;
            ThemedMessageBox.TestResponder = delegate(ThemedMessageForm box) { return answer; };
            try
            {
                using (var form = new MainForm(config))
                {
                    form.PortraitCacheDirectory = Path.Combine(root, "portraits-" + Guid.NewGuid().ToString("N").Substring(0, 6));   // the fixture game's portraits stay out of the shared Data
                    Set(form, "firstLoad", false); form.ShowInTaskbar = false; form.Opacity = 0; form.MinimumSize = Size.Empty; form.Show(); Application.DoEvents();
                    form.ClientSize = new Size(DpiMetrics.Px(form, 1232), DpiMetrics.Px(form, 640)); Application.DoEvents();
                    Call(form, "SetInstallMode", InstallModes.Replace, true);
                    ReplaceEngine engine = FormEngine(f);
                    Func<ReplaceState, int> zeroed = delegate(ReplaceState s) { return s.Deployed.Sum(d => d.Zeroed.Count); };
                    Func<string, ReplaceMod> install = delegate(string mod)
                    {
                        Load(form, f, mod); WaitReview(form);
                        Require(Get<ReplacePlan>(form, "replacePlan") != null && Get<ReplacePlan>(form, "replacePlan").CanInstall, mod + ": not ready: " + Summary(form) + " / " + Details(form));
                        Call(form, "InstallCurrent"); WaitIdle(form);
                        return engine.LoadState().Mods.LastOrDefault(m => m.SourcePath == f.Mod(mod));
                    };
                    Action clear = delegate { engine.RestoreAll(); Require(engine.LoadState().Mods.Count == 0, "cleanup"); File.WriteAllText(configPath, original); };
                    // Automatic with the loader off: the question; Not now installs with the PAK method (entries zeroed).
                    Require(new ReplaceEngine(f.Game, AppConfig.DataDir, ReplaceSettings.Load()).DesiredMethod() == ReplaceMethods.Pak, "Automatic with the loader off must pick the PAK method");
                    answer = DialogResult.No;
                    ReplaceMod pak = install("modA");
                    ReplaceState st = engine.LoadState();
                    Require(pak != null && form.LastLooseLoaderChoice == 1 && st.Method == ReplaceMethods.Pak && zeroed(st) > 0 && File.ReadAllText(configPath) == original, "Not now under Automatic: method " + st.Method + ", zeroed " + zeroed(st) + ", choice " + form.LastLooseLoaderChoice);
                    Call(form, "RefreshInstalled", (string)null); WaitIdle(form);
                    Require(!engine.Status().LooseLoaderOff && Get<Label>(form, "statusText").Text.Contains("PAK method") || !engine.Status().LooseLoaderOff, "PAK installs must not show the loader-off banner");
                    // The same source again is a switch: one record, no warning, no question, the priority kept.
                    install("tool");   // a second record so the priority position can be seen (the tool goes on top)
                    int at = engine.LoadState().Mods.FindIndex(m => m.SourcePath == f.Mod("modA"));
                    install("modA");
                    Require(engine.LoadState().Mods.Count(m => m.SourcePath == f.Mod("modA")) == 1 && engine.LoadState().Mods.FindIndex(m => m.SourcePath == f.Mod("modA")) == at, "same options: not one record at its place");
                    // Another option set of an installed mod (a multi-option pack) is a switch: no warning about itself,
                    // one muted "Replaces installed" line, one record kept at its place.
                    WriteMulti(f);
                    ReplaceMod multi = install("modMulti");
                    int multiAt = engine.LoadState().Mods.FindIndex(m => m.SourcePath == f.Mod("modMulti"));
                    Load(form, f, "modMulti"); WaitReview(form);
                    OptionNode extra = ExtraNode(form); extra.Checked = !extra.Checked; WaitReview(form);
                    ReplacePlan variant = Get<ReplacePlan>(form, "replacePlan");
                    Require(!variant.Items.Any(i => i.Level == "warn" && i.Text.Contains(multi.Name)) && variant.Items.Any(i => i.Level == "info" && i.Text.StartsWith("Replaces installed: ")) && variant.ConflictList.All(c => !String.Equals(c.ModName, multi.Name, StringComparison.OrdinalIgnoreCase)), "a variant of the installed mod warned: " + String.Join(" | ", variant.Items.Select(i => i.Level + " " + i.Text)));
                    Call(form, "InstallCurrent"); WaitIdle(form);
                    Require(engine.LoadState().Mods.Count(m => m.SourcePath == f.Mod("modMulti")) == 1 && engine.LoadState().Mods.FindIndex(m => m.SourcePath == f.Mod("modMulti")) == multiAt, "variant switch: " + engine.LoadState().Mods.Count(m => m.SourcePath == f.Mod("modMulti")) + " records");
                    // Two existing duplicates (2.4.0 could leave them) collapse to one on the next install; 1.0.1: the other leaves no copy (its source is in the library).
                    {
                        Load(form, f, "modA"); WaitReview(form);
                        CompositionPlan comp = Get<CompositionPlan>(form, "plan");
                        string staging = Path.Combine(engine.StoreRoot(engine.LoadState()), ".staging-test-" + Guid.NewGuid().ToString("N"));
                        ReplacePlan second = ReplacePlanner.Prepare(comp, "", engine, staging, null, null);
                        engine.Install(second, "modA duplicate", false, null);
                        Require(engine.LoadState().Mods.Count(m => m.SourcePath == f.Mod("modA")) == 2, "fixture duplicate not created");
                        int removedBefore = engine.ListRemoved().Count;
                        Call(form, "InstallCurrent"); WaitIdle(form);
                        Require(engine.LoadState().Mods.Count(m => m.SourcePath == f.Mod("modA")) == 1 && engine.ListRemoved().Count == removedBefore, "duplicates did not collapse (1.0.1: the other goes with no copy kept): " + engine.LoadState().Mods.Count(m => m.SourcePath == f.Mod("modA")) + " records, " + (engine.ListRemoved().Count - removedBefore) + " new copies");
                    }
                    // A different mod for the same outfit still warns.
                    Load(form, f, "modB"); WaitReview(form);
                    Require(Get<ReplacePlan>(form, "replacePlan").Items.Any(i => i.Level == "warn" && i.Text.Contains("already has")), "a different mod for the same outfit must still warn");
                    clear();
                    // Automatic with the loader on: loose.
                    LooseLoader.Write(f.Game, LooseLoader.EnabledKey, "true", engine.ConfigBackupDir);
                    Require(new ReplaceEngine(f.Game, AppConfig.DataDir, ReplaceSettings.Load()).DesiredMethod() == ReplaceMethods.Loose, "Automatic with the loader on must pick loose");
                    ReplaceMod loose = install("modA");
                    Require(loose != null && engine.LoadState().Method == ReplaceMethods.Loose && zeroed(engine.LoadState()) == 0, "Automatic with the loader on: " + engine.LoadState().Method);
                    clear();
                    // 2.4.4: an explicit loose setting never installs loose files while the loader is off: the PAK method,
                    // said in the status line, and the setting is kept.
                    new ReplaceSettings { Method = ReplaceMethods.Loose, LastMode = InstallModes.Replace }.Save();
                    answer = DialogResult.No;
                    ReplaceMod explicitPak = install("modA");
                    Require(explicitPak != null && engine.LoadState().Method == ReplaceMethods.Pak && zeroed(engine.LoadState()) > 0 && Get<Label>(form, "statusText").Text.Contains("PAK method used") && ReplaceSettings.Load().Method == ReplaceMethods.Loose && File.ReadAllText(configPath) == original,
                        "explicit loose with the loader off: method " + engine.LoadState().Method + ", zeroed " + zeroed(engine.LoadState()) + ", status '" + Get<Label>(form, "statusText").Text + "', setting '" + ReplaceSettings.Load().Method + "'");
                    clear();
                    // A loose install whose loader was turned off afterwards: the banner converts it to PAK.
                    new ReplaceSettings { Method = ReplaceMethods.Loose, LastMode = InstallModes.Replace }.Save();
                    LooseLoader.Write(f.Game, LooseLoader.EnabledKey, "true", engine.ConfigBackupDir);
                    ReplaceMod explicitLoose = install("modA");
                    LooseLoader.Write(f.Game, LooseLoader.EnabledKey, "false", engine.ConfigBackupDir);
                    Require(explicitLoose != null && engine.LoadState().Method == ReplaceMethods.Loose, "explicit loose not respected with the loader on: " + engine.LoadState().Method);
                    Call(form, "ShowPage", Get<Panel>(form, "installedPage")); Application.DoEvents();
                    Call(form, "RefreshInstalled", (string)null); WaitIdle(form);
                    Require(Get<Panel>(form, "gameBanner").Visible && Get<FlatButton>(form, "bannerLoader").Visible && Get<FlatButton>(form, "bannerPak").Visible && Get<FlatButton>(form, "bannerPak").Text == "Use PAK method", "banner actions for loose installs with the loader off");
                    // 2.4.1: the compact banner: one slim row, regular-weight text, compact secondary buttons.
                    {
                        NoticeBanner banner = Get<Panel>(form, "gameBanner") as NoticeBanner;
                        Require(banner != null && banner.Height == DpiMetrics.Px(form, NoticeBanner.RowHeight) && !banner.Message.Font.Bold && banner.Message.Text.StartsWith("Loose file loader off: SF6 ignores loose replaced files."), "banner: " + (banner == null ? "not a NoticeBanner" : banner.Height + " px, bold " + banner.Message.Font.Bold + ", '" + banner.Message.Text + "'"));
                        foreach (FlatButton b in banner.Actions.Controls.OfType<FlatButton>().Where(b => b.Visible))
                            Require(!b.Primary && b.Height == DpiMetrics.Px(form, NoticeBanner.ButtonHeight) && b.Bottom <= banner.Actions.Height, "banner button '" + b.Text + "': primary " + b.Primary + ", " + b.Height + " px");
                        Require(TextRenderer.MeasureText(banner.Message.Text, banner.Message.Font).Width <= banner.Message.Width, "banner text is cut at " + form.ClientSize);
                        Render(form, Path.Combine(root, "installed-banner-loose-" + form.DeviceDpi + ".png"));
                    }
                    Call(form, "UsePakMethod"); WaitIdle(form);
                    Render(form, Path.Combine(root, "installed-banner-none-" + form.DeviceDpi + ".png"));
                    st = engine.LoadState();
                    Require(st.Method == ReplaceMethods.Pak && zeroed(st) > 0 && ReplaceSettings.Load().Method == "" && !Get<Panel>(form, "gameBanner").Visible, "Use PAK method: " + st.Method + ", zeroed " + zeroed(st) + ", setting '" + ReplaceSettings.Load().Method + "', banner " + Get<Panel>(form, "gameBanner").Visible);
                    // SF6 running: the conversion is refused and nothing changes.
                    clear(); new ReplaceSettings { Method = ReplaceMethods.Loose, LastMode = InstallModes.Replace }.Save();
                    LooseLoader.Write(f.Game, LooseLoader.EnabledKey, "true", engine.ConfigBackupDir); install("modA"); LooseLoader.Write(f.Game, LooseLoader.EnabledKey, "false", engine.ConfigBackupDir);
                    probe.Game = true; Call(form, "PollGame");
                    Call(form, "UsePakMethod"); WaitIdle(form);
                    Require(engine.LoadState().Method == ReplaceMethods.Loose, "Use PAK method ran while SF6 was running");
                    probe.Game = false; Call(form, "PollGame");
                    clear();
                    // 2.1.0: Fluffy open does not block loose or tool-mod changes (only PAK edits).
                    probe.Fluffy = true; Call(form, "PollGame");
                    LooseLoader.Write(f.Game, LooseLoader.EnabledKey, "true", engine.ConfigBackupDir);   // 2.4.4: loose installs need the loader on
                    Require(install("tool") != null, "tool mod not installed while Fluffy runs");
                    { ReplaceMod fluffyLoose = install("modA"); Require(fluffyLoose != null && engine.LoadState().Method == ReplaceMethods.Loose, "loose replacement not installed while Fluffy runs: " + (fluffyLoose == null ? "not installed" : "installed") + ", method " + engine.LoadState().Method + ", setting '" + ReplaceSettings.Load().Method + "', status " + Get<Label>(form, "statusText").Text + " / " + Summary(form)); }
                    clear();
                    probe.Fluffy = false; Call(form, "PollGame");
                    form.Close();
                }
            }
            finally
            {
                ThemedMessageBox.TestResponder = null; probe.Game = false; probe.Fluffy = false;
                // Leave the shared Data with no replacement recorded for this fixture game even if a step failed
                // (the later suites refuse a state recorded for another game folder).
                try { var e = new ReplaceEngine(f.Game, AppConfig.DataDir, ReplaceSettings.Load()); if (e.LoadState().Mods.Count > 0) e.RestoreAll(); } catch (Exception ex) { Console.WriteLine("cleanup: " + ex.Message); }
                saved.Save();
            }
        }

        private static void InstallWaits(MainForm form, ReplaceChecks.Fixture f)
        {
            ReplaceEngine engine = FormEngine(f);
            int before = engine.LoadState().Mods.Count;
            OptionNode extra = ExtraNode(form);
            MainForm.ReviewTestDelay = 400;
            try
            {
                extra.Checked = !extra.Checked;
                Require(!Get<FlatButton>(form, "installButton").Enabled, "Install stayed enabled after a change");
                Call(form, "InstallCurrent"); Application.DoEvents();
                Require(!Get<bool>(form, "busy") && engine.LoadState().Mods.Count == before, "Install used a stale result");
                Get<FlatButton>(form, "previewButton").PerformClick();
                Require(Get<bool>(form, "openReportWhenReady") && Get<Label>(form, "statusText").Text.Contains("the report opens when done"), "Report did not wait for the running check");
            }
            finally { MainForm.ReviewTestDelay = 0; }
            WaitReview(form);
            Require(!Get<bool>(form, "openReportWhenReady") && Get<FlatButton>(form, "installButton").Enabled && form.LastReviewSelection == Selection(form), "the fresh result did not arrive or the report request was not consumed");
        }

        private static void ReportDialog(MainForm form, string root)
        {
            ReplacePlan plan = Get<ReplacePlan>(form, "replacePlan");
            using (Form report = (Form)Call(form, "CreateHealthReport", plan))
            {
                report.Opacity = 0; WindowChrome.LastDarkModeResult = -1; WindowChrome.LastCaptionResult = -1;
                report.Show(); Application.DoEvents();
                Require(WindowChrome.LastDarkModeResult == 0 && WindowChrome.LastCaptionResult == 0, "dark title bar attributes were not applied: " + WindowChrome.LastDarkModeResult + "/" + WindowChrome.LastCaptionResult);
                Require((string)report.Tag == "fits" && report.ClientSize.Height < DpiMetrics.Px(report, 640) && report.ClientSize.Height >= DpiMetrics.Px(report, 260), "report not sized to its content: " + report.ClientSize + " " + report.Tag);
                var texts = AllControls(report).OfType<Control>().Select(c => c.Text).ToList();
                Require(texts.Contains("Re-check") && texts.Contains("Close"), "Re-check/Close missing");
                using (Bitmap image = (Bitmap)Call(form, "CaptureWindowImage", report)) image.Save(Path.Combine(root, "report-dialog-" + report.DeviceDpi + ".png"), ImageFormat.Png);
                report.Close();
            }
        }

        private static void ConflictLink(MainForm form, ReplaceChecks.Fixture f, FakeProcessProbe probe, string root)
        {
            ReplaceEngine engine = FormEngine(f);
            Load(form, f, "modB"); WaitReview(form);
            Call(form, "InstallCurrent"); WaitReview(form);
            ReplaceMod alt = engine.LoadState().Mods.Single(m => m.Name.StartsWith("Cammy Alt"));
            Load(form, f, "modA"); WaitReview(form);
            Require(Details(form).StartsWith("Cammy Outfit 2 already has '" + alt.Name + "' · overrides 1 of its 2 files; 1 stays active") && Get<Label>(form, "planDetails").ForeColor == Palette.Accent && Summary(form).StartsWith("Ready to install"), "footer conflict line: " + Details(form));
            var view = Get<ConflictView>(form, "conflictView");
            Require(view.Entries.Count >= 1 && view.Entries[0].Link == "Uninstall '" + alt.Name + "'" && view.Entries[0].LinkEnabled && view.Entries[0].Text.EndsWith("already has '" + alt.Name + "'.") && view.Entries[0].Detail.StartsWith("Enabled · installed ") && view.Entries[0].Detail.Contains("This install overrides 1 of them; the other one stays active"), "inspector conflict entry (1.0.8: headline + details): " + view.Entries[0].Text + " / " + view.Entries[0].Detail);
            Require(view.Height > 0 && Get<Panel>(form, "replacesPanel").ClientRectangle.Contains(BoundsIn(view, Get<Panel>(form, "replacesPanel"))), "conflict lines clipped");
            Render(form, Path.Combine(root, "library-conflict-" + form.DeviceDpi + ".png"));
            probe.Fluffy = true; Call(form, "PollGame");
            Require(!view.Entries[0].LinkEnabled && view.Entries[0].LinkTip.Contains("Close the external mod manager"), "link not disabled while Fluffy runs");
            probe.Fluffy = false; Call(form, "PollGame");
            Require(view.Entries[0].LinkEnabled, "link not enabled again");
            var before = ReplaceChecks.Snapshot(f.Game); int forms = Application.OpenForms.Count;
            Require(view.Activate(0), "link did not run");
            WaitReview(form);
            Require(Application.OpenForms.Count == forms, "a dialog opened");
            // 1.0.1: uninstall means uninstall: no copy kept, no Undo line in the inspector or the status strip.
            Require(engine.LoadState().Find(alt.Id) == null && !engine.ListRemoved().Any(r => r.Record.Mod.Id == alt.Id) && !form.UndoAvailable, "not uninstalled for good (a copy or an Undo was kept)");
            Require(!Details(form).Contains("already has") && view.Entries.All(e => e.Link != "Undo" && !e.Text.Contains("already has")), "warning not cleared / an Undo line is shown: " + Details(form) + " / " + String.Join(" | ", view.Entries.Select(e => e.Text + " [" + e.Link + "]")));
            Require(Get<Label>(form, "statusText").Text.Contains("Uninstalled '" + alt.Name + "'"), "status line");
            // Reinstalling it from the library (the way back) gives the same game folder byte for byte.
            Load(form, f, "modB"); WaitReview(form);
            Call(form, "InstallCurrent"); WaitReview(form);
            Require(Application.OpenForms.Count == forms && engine.LoadState().Mods.Count == 1 && engine.LoadState().Mods[0].Name == alt.Name && engine.LoadState().Mods[0].Enabled, "the reinstall from the library: " + String.Join(", ", engine.LoadState().Mods.Select(m => m.Name)));
            ReplaceChecks.SameSnapshot(before, ReplaceChecks.Snapshot(f.Game), "after reinstalling from the library");
            Load(form, f, "modA"); WaitReview(form);
            Require(Details(form).Contains("already has '" + alt.Name + "'"), "warning did not come back after the reinstall");
            form.ClientSize = new Size(DpiMetrics.Px(form, 1110), DpiMetrics.Px(form, 610)); form.PerformLayout(); Application.DoEvents(); Call(form, "UpdateFooter");
            Require(view.Height >= DpiMetrics.Px(form, 14) && Get<OptionTree>(form, "componentTree").ClientSize.Height >= DpiMetrics.Px(form, 270), "1110x610: conflict lines or options squeezed: " + view.Height);
            Require(view.PreferredHeightFor(view.Width) <= view.Height && view.Entries[0].Link.Length > 0, "1110x610: the conflict text and its Uninstall link do not fit: " + view.PreferredHeightFor(view.Width) + " > " + view.Height);
            Render(form, Path.Combine(root, "library-conflict-1110x610-" + form.DeviceDpi + ".png"));
            form.ClientSize = new Size(DpiMetrics.Px(form, 1232), DpiMetrics.Px(form, 640)); Application.DoEvents();
        }

        private static void NoAutomaticRemoval(MainForm form, ReplaceChecks.Fixture f, FakeProcessProbe probe, RecordingLauncher launcher)
        {
            ReplaceEngine engine = FormEngine(f);
            int forms = Application.OpenForms.Count;
            Call(form, "InstallCurrent"); WaitReview(form);   // modA on top of Cammy Alt
            ReplaceState s = engine.LoadState();
            Require(s.Mods.Count == 2 && s.Mods.All(m => m.Enabled) && s.Mods[0].Name.StartsWith("Cammy Colors"), "install removed or disabled another mod");
            var before = ReplaceChecks.Snapshot(f.Game); var order = s.Mods.Select(m => m.Id).ToList();
            Load(form, f, "modC"); WaitReview(form);
            Require(engine.LoadState().Mods.Select(m => m.Id).SequenceEqual(order) && Application.OpenForms.Count == forms, "opening another mod changed the installed mods or opened a dialog");
            ReplaceChecks.SameSnapshot(before, ReplaceChecks.Snapshot(f.Game), "after opening another mod with other mods installed");
        }

        private static void InstalledConflicts(MainForm form, string root)
        {
            Call(form, "ShowPage", Get<Panel>(form, "installedPage")); Call(form, "RefreshInstalled", (string)null); WaitReview(form);
            var list = Get<ListView>(form, "installedList");
            // 1.0.1: the Has conflicts filter is gone; the game-folder rows (Installed) carry the conflict data.
            Kind(form, "Installed");
            var rows = list.Items.Cast<ListViewItem>().Select(i => i.Tag).OfType<GameModRow>().Where(r => r.Mod != null).ToList();
            Require(rows.Count == 2 && rows.All(r => r.HasConflicts), "conflicting replacement rows: " + rows.Count + " (" + String.Join(", ", rows.Select(r => r.Name + (r.HasConflicts ? " +" : " -"))) + ")");
            GameModRow alt = rows.Single(r => r.Name.StartsWith("Cammy Alt"));
            Require(alt.Stacks.Any(x => x == "Cammy Outfit 2 · 2 mods · 'Cammy Colors' wins") && alt.Conflicts == 1 && alt.Shares.Single().Other.StartsWith("Cammy Colors") && !alt.Shares.Single().ThisWins, "row conflict data: " + String.Join("; ", alt.Stacks));
            foreach (ListViewItem i in list.Items) i.Selected = ((GameModRow)i.Tag) == alt;
            Application.DoEvents(); Call(form, "ShowInstalledSelection");
            Require(Get<ChipStrip>(form, "installedChips").Chips.Any(c => c.Text == "1 shared file"), "shared-file chip");
            TextBox help = Get<TextBox>(form, "installedHelp");
            Require(help.SelectionLength == 0 && help.HideSelection, "picking a row selected text in Files and fixes: " + help.SelectionLength);
            typeof(Control).GetMethod("OnClick", PrivateInstance).Invoke(Get<ChipStrip>(form, "installedChips"), new object[] { EventArgs.Empty }); Application.DoEvents();
            // 2.4.0: the chip scrolls to the section heading and selects nothing (no blue block in the card).
            int heading = help.GetLineFromCharIndex(help.Text.IndexOf("SHARED FILES", StringComparison.Ordinal));
            Require(help.SelectionLength == 0 && form.HelpFirstVisibleLine() == heading && help.Text.Contains("• 'Cammy Colors") && help.Text.Contains("(#1, enabled) · 1 shared file · 'Cammy Colors") && help.Text.Contains(ReplaceChecks.V1), "shared-files details: selection " + help.SelectionLength + ", first line " + form.HelpFirstVisibleLine() + " (heading " + heading + ")");
            // Keyboard focus (the edit control selects everything when tabbed into) leaves nothing selected; a selection made with the mouse stays.
            Func<MouseButtons> realButtons = MainForm.PressedMouseButtons; MainForm.PressedMouseButtons = delegate { return MouseButtons.None; };   // the real desktop's mouse must not matter
            try { help.SelectAll(); typeof(Control).GetMethod("OnEnter", PrivateInstance).Invoke(help, new object[] { EventArgs.Empty }); Application.DoEvents(); } finally { MainForm.PressedMouseButtons = realButtons; }
            Require(help.SelectionLength == 0, "keyboard focus left the whole Files and fixes text selected");
            help.Select(0, 12); Require(help.SelectedText.Length == 12, "a user selection must still work for copying");
            help.Select(0, 0);
            Render(form, Path.Combine(root, "installed-conflicts-" + form.DeviceDpi + ".png"));
            Kind(form, "All");
        }

        private static void FluffyFooter(MainForm form, ReplaceChecks.Fixture f, string fluffy)
        {
            string ini = Path.Combine(fluffy, "installed.ini"); string saved = File.ReadAllText(ini);
            try
            {
                File.WriteAllText(ini, saved + "[Fluffy Cammy]\r\nModName=Fluffy Cammy\r\ninstalltype=invalidate\r\nfile=natives/stm/product/model/esf/esf009/002/esf009_002_cmd_001.user.2\r\n");
                Load(form, f, "modC"); WaitReview(form);
                // 1.0.11 (the user): its own heading and a short line; Extra outfit mode says it above the toggle before switching.
                Label lockHint = Get<Label>(form, "modeLockHint");
                Require(Summary(form) == "Replace original is off" && Details(form) == "The external mod manager has 1 mod installed · uninstall them there or use Extra outfit · View report" && !Get<FlatButton>(form, "installButton").Enabled, "Fluffy footer: " + Summary(form) + " / " + Details(form));
                Require(Get<Label>(form, "modeNotice").Text == "Off while the external mod manager has mods installed · use Extra outfit" && lockHint.Text == "", "Replace original strip: " + Get<Label>(form, "modeNotice").Text + " / hint " + lockHint.Text);
                Render(form, Path.Combine(Path.GetDirectoryName(f.Mods), "library-external-lock-replace-" + form.DeviceDpi + ".png"));
                Call(form, "SetInstallMode", InstallModes.Slot, true); WaitReview(form);
                Require(lockHint.Text == "· Replace off: the external mod manager has 1 mod" && lockHint.ForeColor == Palette.Accent && lockHint.PreferredWidth <= lockHint.Width, "Extra outfit hint: '" + lockHint.Text + "' " + lockHint.PreferredWidth + "/" + lockHint.Width);
                Render(form, Path.Combine(Path.GetDirectoryName(f.Mods), "library-external-lock-extra-" + form.DeviceDpi + ".png"));
                Call(form, "SetInstallMode", InstallModes.Replace, true); WaitReview(form);
                // 1.0.10: REFramework the external manager installed is updated there, so the notice offers no click.
                string dll = Path.Combine(f.Game, "dinput8.dll"), dir = Path.Combine(f.Game, "reframework"); bool hadDir = Directory.Exists(dir);
                byte[] oldDll = File.Exists(dll) ? File.ReadAllBytes(dll) : null;
                try
                {
                    File.WriteAllBytes(dll, Encoding.ASCII.GetBytes("REFramework 01.01.2025")); Directory.CreateDirectory(dir);
                    Call(form, "SetInstallMode", InstallModes.Slot, true);
                    Label notice = Get<Label>(form, "modeNotice");
                    Require(notice.Text == "REFramework from 1 Jan 2025 is out of date · click to update it" && notice.Cursor == Cursors.Hand, "notice before the external manager lists dinput8.dll: " + notice.Text);
                    // The external manager starts listing the same dinput8.dll (the file doesn't change): the next poll follows (PR #9 review).
                    File.WriteAllText(ini, saved + "[Fluffy REFramework]\r\nModName=Fluffy REFramework\r\ninstalltype=normal\r\nfile=dinput8.dll\r\n");
                    Call(form, "CheckToolStamp");
                    Require(notice.Text == "REFramework from 1 Jan 2025 is out of date · update it in the external mod manager" && notice.Cursor == Cursors.Default, "notice for the external manager's REFramework: " + notice.Text);
                    File.WriteAllText(ini, saved); Call(form, "CheckToolStamp");
                    Require(notice.Text == "REFramework from 1 Jan 2025 is out of date · click to update it", "notice after the external manager stopped listing dinput8.dll: " + notice.Text);
                }
                finally
                {
                    if (oldDll != null) File.WriteAllBytes(dll, oldDll); else if (File.Exists(dll)) File.Delete(dll);
                    if (!hadDir && Directory.Exists(dir)) Directory.Delete(dir, true);
                }
            }
            finally { File.WriteAllText(ini, saved); }
        }

        private static GameModRow SelectGameRow(MainForm form, Func<GameModRow, bool> match)
        {
            var list = Get<ListView>(form, "installedList");
            Kind(form, "All");
            ListViewItem item = list.Items.Cast<ListViewItem>().FirstOrDefault(i => i.Tag is GameModRow && match((GameModRow)i.Tag));
            foreach (ListViewItem i in list.Items) i.Selected = i == item;
            Application.DoEvents(); Call(form, "ShowInstalledSelection");
            return item == null ? null : (GameModRow)item.Tag;
        }

        private static void FluffyLink(MainForm form, ReplaceChecks.Fixture f, string fluffy, FakeProcessProbe probe)
        {
            string ini = Path.Combine(fluffy, "installed.ini"); string saved = File.ReadAllText(ini);
            string lua = Path.Combine(f.Game, "reframework", "autorun", "ui_tool.lua");
            MainForm.FluffyAutoRefresh = true;
            try
            {
                Call(form, "ShowPage", Get<Panel>(form, "installedPage")); Call(form, "RefreshInstalled", (string)null); WaitIdle(form);
                int refreshes = form.FluffyRefreshCount;
                // "Fluffy" installs a tool mod: its file, its Mods folder and its installed.ini section.
                ReplaceChecks.Put(Path.Combine(fluffy, "Mods", "UI Tool"), "reframework/autorun/ui_tool.lua", System.Text.Encoding.UTF8.GetBytes("-- ui tool"));
                Directory.CreateDirectory(Path.GetDirectoryName(lua)); File.WriteAllText(lua, "-- ui tool");
                DateTime start = DateTime.UtcNow;
                File.WriteAllText(ini, saved + "[UI Tool]\r\nModID_v1=77\r\nModName=UI Tool\r\nShortModID_v1=7\r\ninstalltype=normal\r\nfile=reframework\\autorun\\ui_tool.lua\r\n");
                GameModRow row = null;
                while (DateTime.UtcNow < start.AddSeconds(2.5) && row == null)
                {
                    Application.DoEvents(); Thread.Sleep(20);
                    if (!form.Working) row = Get<List<GameModRow>>(form, "gameRows").FirstOrDefault(r => r.Fluffy != null && r.Name == "UI Tool");
                }
                double ms = (DateTime.UtcNow - start).TotalMilliseconds;
                Require(row != null && form.FluffyRefreshCount > refreshes, "the Fluffy install did not appear by itself within 2.5 s");
                Require(ms <= 2000, "the Fluffy install appeared after " + (int)ms + " ms (expected within 2 s)");
                WaitIdle(form);
                row = SelectGameRow(form, r => r.Fluffy != null && r.Name == "UI Tool");
                // 1.0.1: the external manager's mods are uninstalled there: Uninstall is off with the reason, the library item too.
                FlatButton remove = Get<FlatButton>(form, "removeInstalledButton");
                Require(row != null && remove.Visible && !remove.Enabled && remove.AccessibleDescription == "Uninstall it in the external mod manager.", "Fluffy row Uninstall: visible " + remove.Visible + ", enabled " + remove.Enabled + ", reason " + remove.AccessibleDescription);
                var item = new ToolStripMenuItem(); Call(form, "PopulateFluffyUninstall", item, Path.Combine(fluffy, "Mods", "UI Tool"));
                Require(item.Available && !item.Enabled && item.Text == "Uninstall mod (external manager) (uninstall it there)", "library item for the Fluffy source: " + item.Text + " enabled " + item.Enabled);
                var other = new ToolStripMenuItem(); Call(form, "PopulateFluffyUninstall", other, f.Mod("modA"));
                Require(!other.Available, "a non-Fluffy source offers the Fluffy uninstall");
                Call(form, "UninstallFluffyRow", row); WaitIdle(form);
                Require(File.Exists(lua) && File.ReadAllText(ini).Contains("[UI Tool]") && Get<Label>(form, "statusText").Text == "Uninstall it in the external mod manager.", "the refused external uninstall: " + Get<Label>(form, "statusText").Text);
                Console.WriteLine("NOTE Fluffy watcher (window): the install showed after " + (int)ms + " ms");
            }
            finally { MainForm.FluffyAutoRefresh = false; probe.Fluffy = false; File.WriteAllText(ini, saved); Call(form, "RefreshInstalled", (string)null); WaitIdle(form); }
        }

        private static void DriftDialog(MainForm form, ReplaceChecks.Fixture f, string fluffy, FakeProcessProbe probe)
        {
            string ini = Path.Combine(fluffy, "installed.ini"); string saved = File.ReadAllText(ini);
            ReplaceEngine engine = FormEngine(f);
            string seen = ""; DialogResult answer = DialogResult.Cancel; int asked = 0;
            ThemedMessageBox.TestResponder = delegate(ThemedMessageForm box) { asked++; seen = box.Caption + "|" + String.Join("|", box.DialogButtons.Select(b => b.Text)) + "|" + box.MessageText; return answer; };
            try
            {
                Call(form, "SetInstallMode", InstallModes.Replace, true);
                Load(form, f, "tool"); WaitReview(form);
                Call(form, "InstallCurrent"); WaitReview(form);
                ReplaceMod tool = engine.LoadState().Mods.Single(m => m.SourcePath == f.Mod("tool"));
                File.WriteAllText(Path.Combine(f.Game, "amd_ags_x64.dll"), FluffyLinkChecks.Proxy196);
                File.WriteAllText(ini, saved + "[Suite]\r\nModName=SF6 Mod Suite\r\ninstalltype=normal\r\nfile=amd_ags_x64.dll\r\n");
                Call(form, "ShowPage", Get<Panel>(form, "installedPage")); Call(form, "RefreshInstalled", (string)null); WaitIdle(form);
                Require(Get<Label>(form, "gameBannerText").Text.StartsWith("Game files changed") && Get<Label>(form, "gameBannerText").AccessibleDescription.Contains("SF6 Mod Suite"), "the banner must name the Fluffy mod: " + Get<Label>(form, "gameBannerText").Text);
                GameModRow row = SelectGameRow(form, r => r.Mod != null && r.Mod.Id == tool.Id);
                var before = ReplaceChecks.Snapshot(f.Game);
                answer = DialogResult.Cancel;
                Get<FlatButton>(form, "removeInstalledButton").PerformClick(); WaitIdle(form);
                Require(asked == 1 && seen.StartsWith(MainForm.DriftCaption + "|Restore &original|&Keep current file|Cancel|") && seen.Contains("amd_ags_x64.dll was replaced by the external mod manager's mod 'SF6 Mod Suite'") && form.LastDriftChoice == DriftChoice.Ask, "the question: " + seen);
                Require(engine.LoadState().Find(tool.Id) != null, "Cancel uninstalled the tool mod");
                ReplaceChecks.SameSnapshot(before, ReplaceChecks.Snapshot(f.Game), "after Cancel");
                answer = DialogResult.No;   // Keep current file
                row = SelectGameRow(form, r => r.Mod != null && r.Mod.Id == tool.Id);
                Get<FlatButton>(form, "removeInstalledButton").PerformClick(); WaitIdle(form);
                ReplaceState s = engine.LoadState();
                Require(asked == 2 && form.LastDriftChoice == DriftChoice.KeepCurrent && s.Find(tool.Id) == null && s.Released.Any(r => r.Path == "amd_ags_x64.dll" && r.Original != null) && File.ReadAllText(Path.Combine(f.Game, "amd_ags_x64.dll")) == FluffyLinkChecks.Proxy196, "Keep current file from the window");
                Require(Get<Label>(form, "statusText").Text.Contains("Uninstalled"), "status: " + Get<Label>(form, "statusText").Text);
            }
            finally
            {
                ThemedMessageBox.TestResponder = null;
                File.WriteAllText(ini, saved);
                try { engine.RestoreAll(DriftChoice.RestoreOriginal); } catch (Exception ex) { Console.WriteLine("cleanup: " + ex.Message); }
                Call(form, "RefreshInstalled", (string)null); WaitIdle(form);
            }
            Require(File.ReadAllText(Path.Combine(f.Game, "amd_ags_x64.dll")) == "genuine AMD AGS library" && engine.LoadState().Released.Count == 0, "cleanup did not restore the genuine file");
        }

        private static void DuplicateExtra(MainForm form, ReplaceChecks.Fixture f)
        {
            Call(form, "SetInstallMode", InstallModes.Slot, true);
            Load(form, f, "modB");
            Require(form.ReviewPending && Summary(form).StartsWith("Checking"), "slot mode did not review automatically: " + Summary(form));
            WaitReview(form);
            Require(Get<CompositionPlan>(form, "plan") != null && Summary(form).StartsWith("Ready to install"), "slot review: " + Summary(form));
            Get<ComboBox>(form, "targetCharacter").SelectedItem = "Cammy"; Get<PaddedTextField>(form, "outfitName").Text = "Alt Extra";
            Require(Get<FlatButton>(form, "installButton").Enabled, "Install not enabled after the automatic review");
            Call(form, "InstallCurrent"); WaitReview(form);
            Load(form, f, "modB"); WaitReview(form);
            Require(Details(form).StartsWith("Already installed as 'Alt Extra' for Cammy") && Get<Label>(form, "planDetails").ForeColor == Palette.Accent, "duplicate note: " + Details(form));
        }

        private static void NoAutoUninstallSetting(MainForm form)
        {
            Panel panel = Get<Panel>(form, "settingsReplacePanel");
            Require(!AllControls(panel).Any(c => (c.Text ?? "").IndexOf("uninstall", StringComparison.OrdinalIgnoreCase) >= 0 && c is CheckBox), "an automatic-uninstall option exists");
        }

        private static void LoaderOffer(MainForm form, ReplaceChecks.Fixture f)
        {
            var seen = new List<string>(); DialogResult answer = DialogResult.Yes;
            string decoy = Path.Combine(f.Mods, "SF6_SlotsLoader_v9.9.zip"), tool = Path.GetFullPath(Path.Combine(f.Mods, "loader under another name.zip"));
            ThemedMessageBox.TestResponder = delegate(ThemedMessageForm box) { seen.Add(box.Caption + "|" + box.MessageText); return answer; };
            using (var server = new LoaderChecks.LoopbackServer())
            {
                string cached = null;
                try
                {
                    // 1. The pinned bytes are in a library under another name: found by hash, nothing downloaded.
                    File.WriteAllBytes(decoy, LoaderChecks.FixtureZip("decoy 9.9"));
                    File.WriteAllBytes(tool, LoaderChecks.FixtureZip("ui library copy"));
                    LoaderRelease.Pin = LoaderChecks.Pin(File.ReadAllBytes(tool), "https://127.0.0.1:1/never-requested.zip");
                    ShowLibrary(form); Call(form, "SetInstallMode", InstallModes.Slot, true); form.LastLoaderFile = null;
                    Call(form, "OfferSlotsLoaderInstall"); WaitIdle(form);
                    LoaderFile hit = form.LastLoaderFile;
                    Require(hit != null && hit.Source == LoaderSource.Library && String.Equals(hit.Path, tool, StringComparison.OrdinalIgnoreCase), "library copy: " + (hit == null ? "none" : hit.Source + " " + hit.Path));
                    Require(seen.Count == 0, "the library copy asked: " + String.Join(" / ", seen));
                    Require(form.IsReplaceMode && String.Equals(Get<ModPackage>(form, "package").SourcePath, tool, StringComparison.OrdinalIgnoreCase), "the library copy did not open in Replace original");
                    // 2. Not in the libraries: downloaded at once (the click is the consent), verified, opened in Replace original.
                    byte[] bytes = LoaderChecks.FixtureZip("ui download");
                    server.Routes["/loader.zip"] = new LoaderChecks.LoopbackServer.Route { Body = bytes };
                    LoaderPin pin = LoaderChecks.Pin(bytes, server.Base + "/loader.zip");
                    LoaderRelease.Pin = pin; LoaderRelease.AllowLoopbackHttp = true;
                    cached = Path.Combine(LoaderRelease.DownloadDir, pin.Asset);
                    seen.Clear(); form.LastLoaderFile = null;
                    Call(form, "OfferSlotsLoaderInstall"); WaitIdle(form);
                    LoaderFile got = form.LastLoaderFile;
                    Require(seen.Count == 0, "the download asked: " + String.Join(" / ", seen));
                    Require(got != null && got.Source == LoaderSource.Download && got.Path == Path.GetFullPath(cached) && File.ReadAllBytes(cached).SequenceEqual(bytes) && !File.Exists(cached + ".part") && server.Requests == 1, "download: " + (got == null ? "none" : got.Source + " " + got.Path) + ", requests " + server.Requests);
                    Require(form.IsReplaceMode && String.Equals(Get<ModPackage>(form, "package").SourcePath, got.Path, StringComparison.OrdinalIgnoreCase), "the download did not open in Replace original");
                    WaitReview(form);
                    ReplacePlan health = Get<ReplacePlan>(form, "replacePlan");
                    Require(health != null && health.CanInstall && health.RootFiles >= 2, "the health check of the downloaded loader: " + (health == null ? "none" : health.CanInstall + ", " + health.RootFiles + " game-root files"));
                    // 3. Again: the verified copy in Data\downloads, no request, no question.
                    seen.Clear(); Call(form, "OfferSlotsLoaderInstall"); WaitIdle(form);
                    Require(form.LastLoaderFile.Source == LoaderSource.Cache && server.Requests == 1 && seen.Count == 0, "cache: " + form.LastLoaderFile.Source + " / " + String.Join(" / ", seen));
                    // 4. A failed download: one message with the reason and the release page; nothing kept.
                    File.Delete(cached); LoaderRelease.Pin = LoaderChecks.With(pin, server.Base + "/gone.zip"); form.LastLoaderFile = null;
                    Call(form, "OfferSlotsLoaderInstall"); WaitIdle(form);
                    string status = Get<Label>(form, "statusText").Text;
                    Require(form.LastLoaderFile == null && status.StartsWith("Couldn't download Slotsloader " + pin.Version + ": HTTP 404 Not Found from 127.0.0.1") && status.Contains(pin.ReleasePage + " and drop the zip on the window") && !File.Exists(cached) && !File.Exists(cached + ".part"), "failure: " + status);
                }
                finally
                {
                    ThemedMessageBox.TestResponder = null; LoaderRelease.Pin = LoaderPin.Release; LoaderRelease.AllowLoopbackHttp = false;
                    if (File.Exists(decoy)) File.Delete(decoy);
                    if (File.Exists(tool)) File.Delete(tool);
                    if (cached != null && File.Exists(cached)) File.Delete(cached);
                }
            }
        }

        private static void SlotsLoaderNotice(MainForm form, ReplaceChecks.Fixture f)
        {
            ShowLibrary(form);
            Call(form, "SetInstallMode", InstallModes.Slot, true);
            Require(Get<Label>(form, "modeNotice").Text.StartsWith("REFramework isn't installed, so extra outfits won't load"), "notice without REFramework: " + Get<Label>(form, "modeNotice").Text);
            string dll = Path.Combine(f.Game, "dinput8.dll"), dir = Path.Combine(f.Game, "reframework"); bool hadDir = Directory.Exists(dir);
            try
            {
                File.WriteAllBytes(dll, Encoding.ASCII.GetBytes("placeholder REFramework, no build date")); Directory.CreateDirectory(dir);
                Call(form, "SetInstallMode", InstallModes.Slot, true);
                Require(Get<Label>(form, "modeNotice").Text.Contains("Slotsloader isn't installed"), "notice: " + Get<Label>(form, "modeNotice").Text);
            }
            finally { File.Delete(dll); if (!hadDir && Directory.Exists(dir)) Directory.Delete(dir, true); }
        }

        private static string Sha(byte[] b) { using (var sha = System.Security.Cryptography.SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(b)).Replace("-", ""); }
        private static byte[] ReFrameworkZip(string dll)
        {
            using (var ms = new MemoryStream())
            {
                using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, true))
                    foreach (var e in new[] { new KeyValuePair<string, string>("dinput8.dll", dll), new KeyValuePair<string, string>("reframework_revision.txt", "fixture revision") })
                        using (var w = new StreamWriter(zip.CreateEntry(e.Key).Open())) w.Write(e.Value);
                return ms.ToArray();
            }
        }
        private static void ReFrameworkFlow(MainForm form, ReplaceChecks.Fixture f)
        {
            // Reading builds.
            ReFrameworkBuild up = ReFrameworkBuild.Parse(Encoding.ASCII.GetBytes("x 961c151d2e87f2686a955a9be24d316f1362bf21 date 31.08.2026 a2cd2f0a734e68fc9bc2127a7f4a385487afb484 Upscaler v1.31.08.20261 x"));
            ReFrameworkBuild nightly = ReFrameworkBuild.Parse(Encoding.ASCII.GetBytes("d052e8fd3fd41d0a1a33394fcf6e6c19be39b1ee 11.10.2026")), old = ReFrameworkBuild.Parse(Encoding.ASCII.GetBytes("built 01.01.2025")), none = ReFrameworkBuild.Parse(Encoding.ASCII.GetBytes("no date here"));
            ReFrameworkPin supported = ReFrameworkRelease.Supported;
            var engine = new ReplaceEngine(f.Game, AppConfig.DataDir, ReplaceSettings.Load());   // the window's Data (the fixture engine keeps its own)
            Require(up.BuildDate == new DateTime(2026, 8, 31) && up.Commit == "a2cd2f0a734e68fc9bc2127a7f4a385487afb484" && up.Upscaler && !up.OlderThan(supported), "upscaler build: " + up.BuildDate + " " + up.Commit);
            Require(nightly.BuildDate == new DateTime(2026, 10, 11) && !nightly.Upscaler && !nightly.OlderThan(supported) && old.OlderThan(supported) && none.BuildDate == null && !none.OlderThan(supported), "nightly/old/unknown builds");
            Require(supported.Size == 13272178 && supported.Sha256 == "27376E1DA177DDCDE9C46B3541BDC6FA1AD54A11D0CAB9F7782EFF0481455E94" && supported.Url == "https://github.com/praydog/REFramework-nightly/releases/download/" + supported.Tag + "/REFramework.zip", "supported pin");
            // Only a plain REFramework record is updated in place; a mod bundling dinput8.dll with other files keeps its record.
            Func<string, string[], ReplaceMod> record = delegate(string id, string[] paths) { return new ReplaceMod { Id = id, Files = paths.Select(p => new ModFile { Path = p }).ToList() }; };
            ReplaceMod plainRecord = record("plain", new[] { "dinput8.dll" }), bundleRecord = record("bundle", new[] { "dinput8.dll", "reframework/autorun/helper.lua" }), other = record("other", new[] { "natives/stm/x.tex" });
            string[] plainZip = { "dinput8.dll", "reframework_revision.txt" };
            Require(ReplacePlanner.ReFrameworkUpdateTarget(new[] { other, plainRecord }, plainZip) == "plain", "a plain REFramework record is not updated in place");
            Require(ReplacePlanner.ReFrameworkUpdateTarget(new[] { bundleRecord, plainRecord }, plainZip) == null && ReplacePlanner.ReFrameworkUpdateTarget(new[] { bundleRecord }, plainZip) == null, "a bundle holding dinput8.dll would be replaced");
            Require(ReplacePlanner.ReFrameworkUpdateTarget(new[] { plainRecord }, new[] { "dinput8.dll", "reframework/plugins/x.dll" }) == null, "a bundle would replace the plain REFramework record");
            string dll = Path.Combine(f.Game, "dinput8.dll"), dir = Path.Combine(f.Game, "reframework"); bool hadDir = Directory.Exists(dir);
            Func<string> notice = delegate { Call(form, "SetInstallMode", InstallModes.Slot, true); return Get<Label>(form, "modeNotice").Text; };
            Action<string> putDll = delegate(string text) { File.WriteAllBytes(dll, Encoding.ASCII.GetBytes(text)); File.SetLastWriteTimeUtc(dll, DateTime.UtcNow.AddSeconds(-new Random().Next(10, 100000))); };
            using (var server = new LoaderChecks.LoopbackServer())
            {
                try
                {
                    ShowLibrary(form);
                    Require(notice().StartsWith("REFramework isn't installed") && Get<Label>(form, "modeNotice").Cursor == Cursors.Hand, "missing: " + notice());
                    putDll("REFramework 11.10.2026");
                    Require(notice() == "Start SF6 once so REFramework can finish setting up; extra outfits load after that" && Get<Label>(form, "modeNotice").Cursor == Cursors.Default, "not started: " + notice());
                    Directory.CreateDirectory(dir);
                    Require(notice().StartsWith("Slotsloader isn't installed"), "current build: " + notice());
                    putDll("REFramework 01.01.2025");
                    Require(notice() == "REFramework from 1 Jan 2025 is out of date · click to update it", "old build: " + notice());
                    // 1.0.10 (tester report): a dinput8.dll replaced outside the Manager updates the notice at the next poll.
                    putDll("REFramework 13.10.2026"); Call(form, "CheckToolStamp");
                    Require(Get<Label>(form, "modeNotice").Text.StartsWith("Slotsloader isn't installed"), "a REFramework updated outside the Manager kept the old notice: " + Get<Label>(form, "modeNotice").Text);
                    putDll("REFramework 01.01.2025"); Call(form, "CheckToolStamp");
                    Require(Get<Label>(form, "modeNotice").Text.StartsWith("REFramework from 1 Jan 2025 is out of date"), "poll after an older build: " + Get<Label>(form, "modeNotice").Text);
                    putDll("REFramework Upscaler 01.01.2025");
                    Require(notice().StartsWith("Slotsloader isn't installed"), "an upscaler build was called old: " + notice());
                    // One click: the pinned nightly downloaded, verified and opened in Replace original; Install puts it in.
                    putDll("REFramework 01.01.2025"); byte[] before = File.ReadAllBytes(dll);
                    byte[] first = ReFrameworkZip("REFramework fixture build one 12.10.2026");
                    server.Routes["/refw-one.zip"] = new LoaderChecks.LoopbackServer.Route { Body = first };
                    ReFrameworkPin pin = ReFrameworkRelease.Make("nightly-09998-fixture", "09998", first.Length, Sha(first), "fixture", new DateTime(2026, 10, 12));
                    pin.Url = server.Base + "/refw-one.zip"; ReFrameworkRelease.Pin = pin; LoaderRelease.AllowLoopbackHttp = true;
                    form.LastReFrameworkFile = null; Call(form, "OfferReFrameworkInstall"); WaitIdle(form);
                    LoaderFile got = form.LastReFrameworkFile;
                    Require(got != null && got.Source == LoaderSource.Download && File.ReadAllBytes(got.Path).SequenceEqual(first) && server.Requests == 1, "download: " + (got == null ? Get<Label>(form, "statusText").Text : got.Source + " " + got.Path));
                    Require(form.IsReplaceMode && String.Equals(Get<ModPackage>(form, "package").SourcePath, got.Path, StringComparison.OrdinalIgnoreCase), "not opened in Replace original: " + Get<Label>(form, "statusText").Text);
                    WaitReview(form);
                    Call(form, "InstallCurrent"); WaitReview(form);
                    List<ReplaceMod> refw = engine.LoadState().Mods.Where(m => m.Files.Any(x => x.Path == "dinput8.dll")).ToList();
                    Require(refw.Count == 1 && Encoding.ASCII.GetString(File.ReadAllBytes(dll)).Contains("build one"), "install: " + refw.Count + " records");
                    // A newer pin updates that record in place (one REFramework record).
                    byte[] second = ReFrameworkZip("REFramework fixture build two 13.10.2026");
                    server.Routes["/refw-two.zip"] = new LoaderChecks.LoopbackServer.Route { Body = second };
                    ReFrameworkPin pin2 = ReFrameworkRelease.Make("nightly-09999-fixture", "09999", second.Length, Sha(second), "fixture", new DateTime(2026, 10, 13));
                    pin2.Url = server.Base + "/refw-two.zip"; ReFrameworkRelease.Pin = pin2;
                    Call(form, "OfferReFrameworkInstall"); WaitIdle(form); WaitReview(form);
                    Call(form, "InstallCurrent"); WaitReview(form);
                    refw = engine.LoadState().Mods.Where(m => m.Files.Any(x => x.Path == "dinput8.dll")).ToList();
                    Require(refw.Count == 1 && Encoding.ASCII.GetString(File.ReadAllBytes(dll)).Contains("build two"), "update in place: " + refw.Count + " records");
                    // Uninstall puts the earlier dinput8.dll back.
                    string removed; engine.Uninstall(refw[0].Id, false, out removed);
                    Require(File.ReadAllBytes(dll).SequenceEqual(before), "uninstall did not restore the earlier dinput8.dll");
                }
                finally
                {
                    ReFrameworkRelease.Pin = ReFrameworkRelease.Supported; LoaderRelease.AllowLoopbackHttp = false;
                    foreach (ReplaceMod m in engine.LoadState().Mods.Where(m => m.Files.Any(x => x.Path == "dinput8.dll")).ToList()) { string removed; try { engine.Uninstall(m.Id, false, out removed); } catch (IOException) { } }
                    if (File.Exists(dll)) File.Delete(dll);
                    if (!hadDir && Directory.Exists(dir)) Directory.Delete(dir, true);
                }
            }
        }
    }
}
