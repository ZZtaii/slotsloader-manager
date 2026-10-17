using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

namespace SlotsInstaller
{
    // 2.4.2: window checks for the 2.0.1 code-review follow-ups (run by --replace-ui-check).
    internal static partial class ReplaceUiChecks
    {
        private static void ClickLabel(Label label) { typeof(Control).GetMethod("OnClick", PrivateInstance).Invoke(label, new object[] { EventArgs.Empty }); Application.DoEvents(); }
        private static void RefreshInstalledPage(MainForm form) { Call(form, "ShowPage", Get<Panel>(form, "installedPage")); Call(form, "RefreshInstalled", (string)null); WaitIdle(form); }
        private static ReplaceMod InstallInWindow(MainForm form, ReplaceChecks.Fixture f, ReplaceEngine engine, string mod)
        {
            Call(form, "SetInstallMode", InstallModes.Replace, true);
            Load(form, f, mod); WaitReview(form);
            Call(form, "InstallCurrent"); WaitIdle(form);
            return engine.LoadState().Mods.FirstOrDefault(m => m.SourcePath == f.Mod(mod));
        }

        // 5. The footer's "Saved options are missing" clears with a click on the footer, as with Health / Review files.
        private static void FooterMissingOptions(MainForm form, ReplaceChecks.Fixture f)
        {
            foreach (bool replace in new[] { true, false })
            {
                string mode = replace ? "Replace original" : "Extra outfit";
                Call(form, "SetInstallMode", replace ? InstallModes.Replace : InstallModes.Slot, true);
                Load(form, f, replace ? "modA" : "modB"); WaitReview(form);
                var missing = Get<List<string>>(form, "missingRestoredOptions");
                missing.Add("Old option"); form.RequestReview(1); WaitReview(form);
                string before = Summary(form) + " / " + Details(form);
                Require(before.Contains("Saved options are missing: Old option") && before.Contains(replace ? "Click Health" : "Click Review files") && !Get<FlatButton>(form, "installButton").Enabled, mode + ": flag not shown: " + before);
                ClickLabel(Get<Label>(form, replace ? "planDetails" : "planSummary"));
                WaitReview(form);
                string after = Summary(form) + " / " + Details(form);
                // (The inline "Reviewing without saved options…" line is replaced by "Checking…" and then the result, as
                // with the Health / Review files button since 2.4.1.)
                Require(missing.Count == 0 && !after.Contains("Saved options") && Summary(form).StartsWith("Ready to install") && (!replace || Get<FlatButton>(form, "installButton").Enabled), mode + ": the footer click did not clear the flag: " + after);
            }
            Call(form, "SetInstallMode", InstallModes.Replace, true);
        }

        // 3. Settings line: removed copies and the keep rule under the replacement store.
        private static void SettingsStoreLine(MainForm form, ReplaceChecks.Fixture f, string root)
        {
            ReplaceEngine engine = FormEngine(f);
            ReplaceMod c = InstallInWindow(form, f, engine, "modC");
            Require(c != null, "fixture install");
            // 1.0.1: only Restore original game files keeps .removed copies, and only while its Undo is available.
            RefreshInstalledPage(form);
            Call(form, "RestoreOriginalGameFiles"); WaitIdle(form);
            List<RemovedReplacement> removed = engine.ListRemoved();
            Require(removed.Count == 1 && removed[0].Record.Mod.Id == c.Id && form.UndoAvailable, "Restore original game files kept " + removed.Count + " copies, Undo " + form.UndoAvailable);
            Panel page = Get<Panel>(form, "settingsPage");
            try
            {
                foreach (Size logical in new[] { new Size(1232, 640), new Size(1110, 610) })
                {
                    form.ClientSize = new Size(DpiMetrics.Px(form, logical.Width), DpiMetrics.Px(form, logical.Height)); Application.DoEvents();
                    Call(form, "ShowPage", page); Call(form, "LoadSettingsFields"); Call(form, "ShowSettingsTab", true); form.PerformLayout(); Application.DoEvents();
                    var label = Get<Label>(form, "settingsStoreStatus");
                    string[] lines = label.Text.Replace("\r\n", "\n").Split('\n');
                    ReplaceState state = engine.LoadState();
                    string expected = MainForm.RemovedCopiesLine(removed.Count, removed.Sum(r => r.Bytes));
                    Require(lines.Length == 3 && lines[0].EndsWith(" · " + ReplacePlanner.N(state.Mods.Count, "installed replacement")) && lines[1] == expected && lines[2] == engine.StoreRoot(state), "store note: " + label.Text);
                    Require(expected == "Removed copies: " + removed.Count + " · " + Utilities.FriendlyBytes(removed.Sum(r => r.Bytes)) + " · kept only while their Undo is available" && !label.Text.Contains("freed on uninstall"), "line: " + expected);
                    Panel panel = Get<Panel>(form, "settingsReplacePanel");
                    Label fluffy = AllControls(panel).OfType<Label>().First(l => l.Text == "External mod manager");
                    Label caption = AllControls(panel).OfType<Label>().First(l => l.Text == "Replacement store");
                    Rectangle note = BoundsIn(label, page), next = BoundsIn(fluffy, page);
                    Require(page.ClientRectangle.Contains(note) && note.Bottom <= next.Top && ((NoteLabel)label).LineCount == 3, "store note clipped or overlapping at " + logical + ": " + note + " vs Fluffy " + next + ", lines " + ((NoteLabel)label).LineCount);
                    Render(form, Path.Combine(root, "settings-store-" + logical.Width + "x" + logical.Height + "-" + form.DeviceDpi + ".png"));
                    // The store block alone (caption to note), for the copy review.
                    Rectangle top = BoundsIn(caption, form), bottom = BoundsIn(label, form);
                    // DrawToBitmap of a form includes its frame: client coordinates move by the frame offset.
                    Point client = form.PointToScreen(Point.Empty); Size frame = new Size(client.X - form.Left, client.Y - form.Top);
                    top.Offset(frame.Width, frame.Height); bottom.Offset(frame.Width, frame.Height);
                    Rectangle crop = Rectangle.FromLTRB(Math.Max(0, Math.Min(top.Left, bottom.Left) - DpiMetrics.Px(form, 8)), Math.Max(0, top.Top - DpiMetrics.Px(form, 8)), Math.Min(form.ClientSize.Width, Math.Max(top.Right, bottom.Right) + DpiMetrics.Px(form, 8)), Math.Min(form.ClientSize.Height, bottom.Bottom + DpiMetrics.Px(form, 8)));
                    using (var full = new Bitmap(form.ClientSize.Width, form.ClientSize.Height))
                    {
                        form.DrawToBitmap(full, new Rectangle(0, 0, full.Width, full.Height));
                        using (Bitmap part = full.Clone(crop, full.PixelFormat)) part.Save(Path.Combine(root, "settings-store-line-" + logical.Width + "x" + logical.Height + "-" + form.DeviceDpi + ".png"), ImageFormat.Png);
                    }
                }
            }
            finally
            {
                form.ClientSize = new Size(DpiMetrics.Px(form, 1232), DpiMetrics.Px(form, 640)); Call(form, "ShowSettingsTab", false); ShowLibrary(form);
            }
            // The copy goes as soon as its Undo ends (here: a new change), and the refresh keeps nothing else.
            ReplaceMod again = InstallInWindow(form, f, engine, "modC");
            RefreshInstalledPage(form);
            Require(again != null && !form.UndoAvailable && engine.ListRemoved().Count == 0, "a removed copy outlived its Undo: " + engine.ListRemoved().Count);
            engine.RestoreAll(); RefreshInstalledPage(form); ShowLibrary(form);
        }

        // 2. A store file open in another program: Restore original game files (1.0.1: the one change that still keeps
        // .removed copies; uninstall keeps none) says the copy wasn't saved.
        private static void TrashWarningInline(MainForm form, ReplaceChecks.Fixture f)
        {
            ReplaceEngine engine = FormEngine(f);
            try
            {
                ReplaceMod b = InstallInWindow(form, f, engine, "modB");
                Require(b != null && engine.LoadState().Mods.Count == 1, "fixture install");
                RefreshInstalledPage(form);
                string storeDir = Path.Combine(engine.StoreRoot(engine.LoadState()), b.Id), status;
                using (new FileStream(Directory.GetFiles(storeDir, "*", SearchOption.AllDirectories).First(), FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    Call(form, "RestoreOriginalGameFiles"); WaitIdle(form);
                    status = Get<Label>(form, "statusText").Text;
                }
                Require(status.Contains("restored") && status.Contains(ReplaceEngine.TrashFailedStart) && status.Contains("next start") && !status.Contains("kept in"), "status: " + status);
                Require(engine.LoadState().Find(b.Id) == null && File.Exists(engine.TrashPendingPath) && !engine.ListRemoved().Any(r => r.Record.Mod.Id == b.Id) && form.UndoAvailable, "after the failed move");
                RefreshInstalledPage(form);   // LoadGameRows retries the move
                Require(!File.Exists(engine.TrashPendingPath) && engine.ListRemoved().Any(r => r.Record.Mod.Id == b.Id) && form.UndoAvailable, "the refresh did not keep the copy");
                form.PerformUndo(); WaitIdle(form); RefreshInstalledPage(form);
                Require(engine.LoadState().Find(b.Id) != null, "Undo did not put it back");
            }
            finally { engine.RestoreAll(); RefreshInstalledPage(form); ShowLibrary(form); }
        }

        // 6. Another program installs a mod over the same files; the window's Undo puts the removed mod below it.
        private static void UndoBelowNewer(MainForm form, ReplaceChecks.Fixture f)
        {
            ReplaceEngine engine = FormEngine(f);
            try
            {
                engine.RestoreAll(); RefreshInstalledPage(form);
                ReplaceMod a = InstallInWindow(form, f, engine, "modA");
                ReplaceMod b = InstallInWindow(form, f, engine, "modB");   // [B, A]
                RefreshInstalledPage(form);
                // 1.0.1: the window's Undo of a removal is the one after Restore original game files (uninstall has none).
                Call(form, "RestoreOriginalGameFiles"); WaitIdle(form);
                Require(form.UndoAvailable && engine.LoadState().Mods.Count == 0, "restore all: Undo " + form.UndoAvailable + ", " + engine.LoadState().Mods.Count + " installed");
                Thread.Sleep(30);
                ModPackage package = PackageReader.Load(f.Mod("modWeird"), ""); ReplacePlan plan;
                ReplaceMod w = ReplacePlanner.PrepareAndInstall(Composer.Plan(package, Composer.DefaultSelection(package, true), true), "", engine, "Weird folder", false, null, out plan);
                Require(form.UndoAvailable, "the other program's install ended the window's Undo");
                form.PerformUndo(); WaitIdle(form); RefreshInstalledPage(form);
                ReplaceState s = engine.LoadState();
                Require(s.Mods.Select(m => m.Id).SequenceEqual(new[] { w.Id, b.Id, a.Id }), "order after Undo: " + String.Join(", ", s.Mods.Select(m => m.Name)));
                Require(s.Deployment(ReplaceChecks.V5).Owner == w.Id, "the newer install lost the shared mesh");
            }
            finally { engine.RestoreAll(); RefreshInstalledPage(form); ShowLibrary(form); }
        }
    }
}
