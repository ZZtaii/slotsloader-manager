using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace SlotsInstaller
{
    // 1.5 UI checks with generated fixtures only: page-aware status, required
    // choice marking and footer jump, drop acceptance through the shared open
    // path, keyboard shortcuts, dark title bar attributes, installed-detail
    // layout, settings library rows, search pacing and a synthetic timing pass.
    internal static class PolishChecks
    {
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        private const string Mesh = "natives/stm/product/model/esf/esf032/001/01/esf032_001_01.mesh.230110883";
        private const string Material = "natives/stm/product/model/esf/esf032/001/01/esf032_001_01.mdf2.40";
        private const string Texture = "natives/stm/product/model/esf/esf032/001/01/skin.tex.28";

        internal static int Run(string fixtureRoot, string realArchive)
        {
            string root = Path.Combine(Path.GetFullPath(fixtureRoot), "polish-" + Guid.NewGuid().ToString("N").Substring(0, 10));
            Safety.NoReparseChain(root); Directory.CreateDirectory(root);
            var report = new List<string>();
            bool previousHeadless = Program.Headless; Program.Headless = true;
            SynchronizationContext previous = SynchronizationContext.Current;
            bool previousAutoInstall = WindowsFormsSynchronizationContext.AutoInstall;
            WindowsFormsSynchronizationContext.AutoInstall = false;
            try
            {
                using (var ui = new WindowsFormsSynchronizationContext())
                {
                    SynchronizationContext.SetSynchronizationContext(ui);
                    string source = CreateRequiredFixture(Path.Combine(root, "Fixture required bundle"));
                    string archive = Path.Combine(root, "Fixture required bundle.zip");
                    ZipFile.CreateFromDirectory(source, archive);
                    string game = Path.Combine(root, "game");
                    string costumes = Path.Combine(game, "reframework", "costume_mods");
                    CreateInstalledFixtures(costumes);
                    var config = new AppConfig { GameRoot = game, Libraries = new List<string>(), SevenZipPath = "" };
                    using (var form = new MainForm(config))
                    {
                        Set(form, "firstLoad", false); form.ShowInTaskbar = false; form.Opacity = 0; form.MinimumSize = Size.Empty; form.Show(); Application.DoEvents();
                        form.ClientSize = new Size(DpiMetrics.Px(form, 1232), DpiMetrics.Px(form, 640)); Application.DoEvents();
                        report.Add("Hidden native window at " + form.DeviceDpi + " DPI, generated fixtures under " + root + ". No real outfits or libraries were written.");
                        CheckTitleBar(form, report);
                        CheckStatus(form, report);
                        CheckRequiredChoices(form, source, report, root);
                        CheckDrop(form, archive, source, root, report);
                        CheckShortcuts(form, archive, report);
                        CheckInstalledDetails(form, report, root);
                        CheckSettingsLibraries(form, root, report);
                        CheckTiming(form, report);
                        CheckNoTooltips(form, report);
                        CheckPopupAllowList(report);
                        CheckOldErrors(form, report);
                        CheckWelcome(form, root, report);
                        CheckHelp(form, root, report);
                        CheckTours(form, root, report);
                        form.Close();
                    }
                    if (!String.IsNullOrWhiteSpace(realArchive) && File.Exists(realArchive)) CheckRealRequired(realArchive, report);
                }
                report.Add("ALL PASS");
                File.WriteAllLines(Path.Combine(root, "polish-checks.txt"), report);
                Console.WriteLine(String.Join(Environment.NewLine, report));
                return 0;
            }
            catch (Exception error)
            {
                report.Add("FAIL: " + error);
                File.WriteAllLines(Path.Combine(root, "polish-checks.txt"), report);
                Console.WriteLine(String.Join(Environment.NewLine, report));
                return 1;
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous); WindowsFormsSynchronizationContext.AutoInstall = previousAutoInstall;
                Program.Headless = previousHeadless;
            }
        }

        private static string CreateRequiredFixture(string source)
        {
            Put(source, "main/modinfo.ini", "name=Main files\n"); Put(source, "main/" + Material, "required shared material");
            Put(source, "outfits/modinfo.ini", "name=Outfit choices\ndummymod=true\ndescription=Choose one outfit\n");
            Put(source, "outfits/dress/modinfo.ini", "name=Dress\n"); Put(source, "outfits/dress/" + Mesh, "dress mesh");
            Put(source, "outfits/suit/modinfo.ini", "name=Suit\n"); Put(source, "outfits/suit/" + Mesh, "suit mesh");
            Put(source, "skins/modinfo.ini", "name=Skin choices\ndummymod=true\ndescription=Choose one skin\n");
            Put(source, "skins/default/modinfo.ini", "name=Default skin\n"); Put(source, "skins/default/" + Texture, "skin texture");
            Put(source, "skins/tan/modinfo.ini", "name=Tan skin\n"); Put(source, "skins/tan/" + Texture, "tan texture");
            return source;
        }

        private static void CreateInstalledFixtures(string costumes)
        {
            string juri = Path.Combine(costumes, "Juri"); Directory.CreateDirectory(juri);
            File.WriteAllText(Path.Combine(juri, "A Manual archive with a deliberately long name for the two line title check.zip"), "opaque archive fixture");
            string managed = Path.Combine(juri, "B Managed outfit");
            Directory.CreateDirectory(Path.Combine(managed, "natives")); File.WriteAllText(Path.Combine(managed, "natives", "fixture.bin"), "managed bytes");
            Json.Write(Path.Combine(managed, ".slots-installer.json"), new InstallManifest { Id = Guid.NewGuid().ToString("N"), Character = "Juri", Name = "B Managed outfit", SourcePath = "fixture-source.zip", CreatedUtc = DateTime.UtcNow.ToString("o"), SelectedIds = new List<string> { "main" }, Sha256 = new Dictionary<string, string> { { "natives/fixture.bin", new string('a', 64) } } });
        }

        private static void CheckTitleBar(MainForm form, List<string> report)
        {
            int build = Environment.OSVersion.Version.Build;
            WindowChrome.LastDarkModeResult = -1; WindowChrome.ApplyTo(form.Handle);
            if (build >= 17763) Require(WindowChrome.LastDarkModeResult == 0, "DWM dark title bar attribute was rejected: 0x" + WindowChrome.LastDarkModeResult.ToString("X"));
            if (build >= 22000) Require(WindowChrome.LastCaptionResult == 0, "DWM caption colour attribute was rejected: 0x" + WindowChrome.LastCaptionResult.ToString("X"));
            WindowChrome.LastDarkModeResult = -1;
            using (var reportForm = (Form)Call(form, "CreateCompositionReportForTest"))
            {
                reportForm.Opacity = 0; reportForm.ShowInTaskbar = false; reportForm.Show(); Application.DoEvents();
                if (build >= 17763) Require(WindowChrome.LastDarkModeResult == 0, "Composition report did not receive the dark title bar.");
                reportForm.Close();
            }
            report.Add("PASS: dark title bar: DWMWA_USE_IMMERSIVE_DARK_MODE=0x" + WindowChrome.LastDarkModeResult.ToString("X") + ", caption colour=0x" + WindowChrome.LastCaptionResult.ToString("X") + " on Windows build " + build + "; applied to the main window and dialogs through AppBranding.");
        }

        private static void CheckStatus(MainForm form, List<string> report)
        {
            Panel library = Get<Panel>(form, "libraryPage"), installed = Get<Panel>(form, "installedPage"), settings = Get<Panel>(form, "settingsPage");
            Label status = Get<Label>(form, "statusText");
            Call(form, "ShowPage", library);
            Call(form, "SetPageStatus", library, "Review the composition issues before installing", Palette.Red);
            Require(status.Text.StartsWith("Review the composition"), "Library status was not shown on the library page.");
            Call(form, "ShowPage", installed); WaitIdle(form);
            Require(!status.Text.Contains("composition"), "Installed outfits shows a stale library status: " + status.Text);
            Call(form, "ShowPage", settings);
            Require(!status.Text.Contains("composition") && status.Text == form.DefaultStatus(settings), "Settings shows a stale library status: " + status.Text);
            Call(form, "ShowPage", library);
            Require(status.Text.StartsWith("Review the composition"), "Returning to the library lost its own status.");
            Call(form, "SetBusy", true, "Fixture work");
            Require(status.Text == "Fixture work", "Busy work is not shown in the status strip.");
            Call(form, "SetBusy", false, "Ready");
            Require(status.Text.StartsWith("Review the composition"), "Finishing work replaced the page status with a generic message: " + status.Text);
            Call(form, "ClearPageStatus", library);
            Require(status.Text == form.DefaultStatus(library), "Clearing the library status did not fall back to its contextual default.");
            report.Add("PASS: status strip is page-aware: library message hidden on Installed/Settings, restored on return, busy text returns to the page status, contextual defaults per page.");
        }

        private static void CheckRequiredChoices(MainForm form, string source, List<string> report, string root)
        {
            ModPackage package = PackageReader.Load(source, "");
            Set(form, "package", package);
            // 1.0.18: the defaults now tick a choice in each required group; this check starts from the main files alone.
            var defaults = Composer.DefaultSelection(package);
            Require(Composer.UnresolvedChoiceGroups(package, defaults).Count == 0, "The default selection left a required group open.");
            Call(form, "FillComponents", defaults.Where(id => Composer.IsBase(package.Find(id))).ToList());
            var tree = Get<OptionTree>(form, "componentTree"); tree.ExpandAll();
            var unresolved = Get<List<ModComponent>>(form, "unresolvedChoices");
            var order = Get<List<string>>(form, "selectedOrder");
            // 1.0.19 (the user: "relax requirements ... show a popup (less annoying though) around the image that it may
            // not work without x mod and highlight the required mod temporarily but install the mod anyways"): the two
            // empty required groups are notes in the plan and one line under the preview picture, not a stop.
            CompositionPlan mainsOnly = Composer.Plan(package, order);
            var planNames = mainsOnly.Needs.Where(n => n.Needed.IsDummy).Select(n => n.Needed.Name).OrderBy(n => n).ToArray();
            Require(unresolved.Select(g => g.Name).OrderBy(n => n).SequenceEqual(planNames) && planNames.Length == 2 && mainsOnly.Errors.All(e => !e.StartsWith("Choose an option")), "Live required groups differ from the composition plan: [" + String.Join(", ", unresolved.Select(g => g.Name)) + "] vs [" + String.Join(", ", planNames) + "]");
            Label summary = Get<Label>(form, "planSummary"); Label notice = form.NeedNotice;
            Require(!form.ChoicesBlock && summary.Text != "Choose: Outfit, Skin", "An empty required group still held the footer with main files ticked: " + summary.Text);
            Require(notice.Text == "May not work without an option from “Outfit choices” and an option from “Skin choices”" && OverlayScroller.OwnVisible(notice), "The line under the preview picture does not name the empty required groups: " + notice.Text);
            Require(notice.Parent.BackColor == Palette.Background && notice.BackColor == Palette.Raised && notice.Bottom == notice.Parent.ClientSize.Height && notice.Width == notice.Parent.ClientSize.Width, "The line is not the strip under the picture.");
            var nodes = Get<Dictionary<string, OptionNode>>(form, "componentNodes");
            // 13 Nov 2026 (the user: "highlights should never happen on mod hovers, only on mod installs"): opening a mod
            // lights nothing up, whatever its ticks leave out. The line above and the marker below are all there is.
            Require(!unresolved.Any(g => form.IsFlashing(g.Id)) && form.CurrentNeeds.Count == 2, "Opening a mod lit rows up.");
            OptionNode outfitGroup = nodes[unresolved[0].Id];
            // The marker is drawn into the row: an accent edge at the left of the group row.
            outfitGroup.EnsureVisible(); tree.Update();
            using (var image = new Bitmap(tree.Width, tree.Height))
            {
                tree.DrawToBitmap(image, tree.ClientRectangle);
                Rectangle row = outfitGroup.Bounds;
                Require(image.GetPixel(DpiMetrics.Px(tree, 1), row.Y + row.Height / 2).ToArgb() == Palette.Accent.ToArgb(), "Unresolved required group is not marked in the option list.");
                image.Save(Path.Combine(root, "polish-required-" + tree.DeviceDpi + ".png"), ImageFormat.Png);
            }
            // The main files alone make no outfit (no model): that still keeps Install off, the empty groups do not.
            WaitIdle(form);
            Require(summary.Text == "1 required change" && Get<Label>(form, "planDetails").Text.StartsWith("The selection has no outfit mesh") && !Get<Button>(form, "installButton").Enabled, "A selection with no model was not held back: " + summary.Text);
            // Scroll away, then click the line under the picture: it shows the first group left empty and lights it again.
            outfitGroup = nodes[unresolved[0].Id];   // the review may have reloaded the fixture: the rows are new objects
            tree.CollapseAll(); tree.TopNode = tree.VisibleNodes[tree.VisibleNodes.Count - 1]; tree.SelectedNode = null; Call(form, "Flash", new List<string>());
            typeof(Control).GetMethod("OnClick", PrivateInstance).Invoke(notice, new object[] { EventArgs.Empty });
            Require(tree.ClientRectangle.IntersectsWith(outfitGroup.Bounds) && outfitGroup.IsExpanded && form.IsFlashing(unresolved[0].Id), "A click on the line did not show and light the first group left empty: bounds " + outfitGroup.Bounds + " in " + tree.ClientRectangle + ", expanded " + outfitGroup.IsExpanded + ", lit " + form.IsFlashing(unresolved[0].Id) + ", " + form.CurrentNeeds.Count + " left out.");
            Get<ComboBox>(form, "targetCharacter").SelectedItem = "Ingrid"; Get<PaddedTextField>(form, "outfitName").Text = "Polish fixture";
            nodes[package.Components.First(c => c.Name == "Dress").Id].Checked = true;
            Require(unresolved != Get<List<ModComponent>>(form, "unresolvedChoices") && Get<List<ModComponent>>(form, "unresolvedChoices").Count == 1 && notice.Text == "May not work without an option from “Skin choices”", "Resolving one group did not update the marker list: " + notice.Text);
            // Install is on with the skin group still empty; the footer repeats the note in the accent colour.
            WaitIdle(form);
            Require(summary.Text.StartsWith("Ready to install") && Get<Button>(form, "installButton").Enabled && Get<Label>(form, "planDetails").Text == "May not work without an option from “Skin choices” · View notes" && Get<Label>(form, "planDetails").ForeColor == Palette.Accent,
                "An empty required group kept Install off, or the footer does not say what is left out: " + summary.Text + " / " + Get<Label>(form, "planDetails").Text);
            nodes[package.Components.First(c => c.Name == "Tan skin").Id].Checked = true;
            Require(Get<List<ModComponent>>(form, "unresolvedChoices").Count == 0 && form.CurrentNeeds.Count == 0 && notice.Text == "" && !OverlayScroller.OwnVisible(notice), "The line stayed after every group was answered: " + notice.Text);
            Require(Get<List<ModComponent>>(form, "unresolvedChoices").Count == 0 && form.ReviewPending && summary.Text == "", "Resolving every group did not start the automatic review (1.0.12: the footer waits for the result): " + summary.Text);
            Require(!Get<FlatButton>(form, "previewButton").Emphasis, "Review files is emphasised although the review runs automatically.");
            WaitIdle(form);
            Require(summary.Text.StartsWith("Ready to install"), "The automatic review did not finish with Ready to install: " + summary.Text);
            Get<ComboBox>(form, "targetCharacter").SelectedIndex = -1; Get<PaddedTextField>(form, "outfitName").Text = "";
            Call(form, "BuildPlan", false); WaitIdle(form);
            Require(!Get<Button>(form, "installButton").Enabled && summary.Text.StartsWith("Ready to install") && Get<Label>(form, "planDetails").Text.Contains("character and outfit name"), "Missing destination was not explained inline: " + Get<Label>(form, "planDetails").Text);
            Get<ComboBox>(form, "targetCharacter").SelectedItem = "Ingrid"; Get<PaddedTextField>(form, "outfitName").Text = "Polish fixture";
            Require(Get<Button>(form, "installButton").Enabled && summary.Text.StartsWith("Ready to install"), "A complete, reviewed fixture selection did not enable Install: " + summary.Text);
            Require(!Get<FlatButton>(form, "previewButton").Emphasis, "Review files stayed emphasised after review.");
            report.Add("PASS: required choose-one groups are computed exactly as the composition plan notes them, marked in the list (accent edge + label) and named in one line under the preview picture; a click on the line shows the first one; Install is on with a group left empty and the footer repeats the note; answering them clears the line (1.0.19).");
            CheckLeftOut(form, Path.Combine(root, "Fixture left out"), report);
        }

        // 1.0.19: an option the user unticks by hand stays out when the option that needs it is ticked again; the line
        // under the picture names it, its row lights up, and Install stays on.
        private static void CheckLeftOut(MainForm form, string source, List<string> report)
        {
            Put(source, "body/modinfo.ini", "name=Body\n"); Put(source, "body/" + Mesh, "body mesh"); Put(source, "body/" + Material, "body material");
            Put(source, "body/hair/modinfo.ini", "name=Long hair\n"); Put(source, "body/hair/" + Mesh.Replace("/01/", "/02/").Replace("_01.", "_02."), "hair mesh");
            ModPackage package = PackageReader.Load(source, "");
            ModComponent body = package.Components.Single(c => c.Name == "Body"), hair = package.Components.Single(c => c.Name == "Long hair");
            Require(String.Equals(hair.ParentId, body.Id, StringComparison.OrdinalIgnoreCase), "Fixture: the hair option is not under the body option.");
            Set(form, "package", package);
            Call(form, "FillComponents", new List<string>());
            var nodes = Get<Dictionary<string, OptionNode>>(form, "componentNodes"); var order = Get<List<string>>(form, "selectedOrder"); Label notice = form.NeedNotice;
            Require(form.CurrentNeeds.Count == 0 && notice.Text == "", "An empty selection showed the line: " + notice.Text);
            nodes[hair.Id].Checked = true;
            Require(nodes[body.Id].Checked && form.CurrentNeeds.Count == 0 && notice.Text == "", "Ticking an option no longer ticks what it needs.");
            nodes[body.Id].Checked = false;
            Require(!nodes[hair.Id].Checked && order.Count == 0 && notice.Text == "", "Unticking an option left the options under it ticked.");
            // The option an option sits under in the list comes back with it, even after a hand untick (review of PR 29:
            // a child alone installs without its body). Only Requirement= and Required options stay out once unticked.
            nodes[hair.Id].Checked = true;
            Require(nodes[body.Id].Checked && order.Contains(body.Id) && order.Contains(hair.Id) && form.CurrentNeeds.Count == 0 && notice.Text == "", "Ticking an option did not bring back the option it sits under.");
            // A saved selection that leaves the body out (an earlier build allowed it) is opened as it was
            Call(form, "FillComponents", new List<string> { hair.Id });
            Require(!nodes[body.Id].Checked && order.SequenceEqual(new[] { hair.Id }), "A saved selection's left-out option was ticked when the mod was opened.");
            Require(form.CurrentNeeds.Single().Needed == body && form.CurrentNeeds.Single().By == hair && notice.Text == "May not work without “Body”", "The line does not name the option left out: " + notice.Text);
            // 13 Nov 2026: a changed tick lights no row up (the user: only at Install). The row is drawn as any other.
            Require(!form.IsFlashing(body.Id) && !form.IsFlashing(hair.Id), "A changed tick lit a row up.");
            var tree = Get<OptionTree>(form, "componentTree"); tree.SelectedNode = null; tree.Update();
            using (var image = new Bitmap(tree.Width, tree.Height))
            {
                tree.DrawToBitmap(image, tree.ClientRectangle);
                Rectangle row = nodes[body.Id].Bounds;
                Require(image.GetPixel(DpiMetrics.Px(tree, 1), row.Y + row.Height / 2).ToArgb() == Palette.Surface.ToArgb() && image.GetPixel(row.Right - DpiMetrics.Px(tree, 6), row.Y + row.Height / 2).ToArgb() == Palette.Surface.ToArgb(), "The row of the option left out is drawn lit although nothing was installed or clicked.");
                // No indent (the user: "yes no indent"): the option under Body starts at the same column as Body.
                Require(nodes[hair.Id].Bounds.X == nodes[body.Id].Bounds.X, "The options list indents a level.");
            }
            // A click on the line lights it: a light grey that lifts the row a little, with a grey edge at the left (the
            // user, 13 Nov 2026: "change the highlight from orang'ish to more lightish grey that kind of blends in").
            typeof(Control).GetMethod("OnClick", PrivateInstance).Invoke(notice, new object[] { EventArgs.Empty });
            Require(form.IsFlashing(body.Id) && !form.IsFlashing(hair.Id), "A click on the line did not light the option left out.");
            tree.SelectedNode = null; tree.Update();
            using (var image = new Bitmap(tree.Width, tree.Height))
            {
                tree.DrawToBitmap(image, tree.ClientRectangle);
                Rectangle row = nodes[body.Id].Bounds;
                Color edge = image.GetPixel(DpiMetrics.Px(tree, 1), row.Y + row.Height / 2), lit = image.GetPixel(row.Right - DpiMetrics.Px(tree, 6), row.Y + row.Height / 2);
                Color wanted = MainForm.Blend(Palette.Surface, MainForm.NeedTint, MainForm.NeedFlashStrength);
                Func<Color, bool> grey = c => Math.Max(c.R, Math.Max(c.G, c.B)) - Math.Min(c.R, Math.Min(c.G, c.B)) <= 20;
                Require(grey(MainForm.NeedTint) && grey(MainForm.NeedEdge) && edge.ToArgb() != Palette.Accent.ToArgb() && grey(edge) && edge.R > lit.R, "The edge of a lit row is not a grey: " + edge);
                Require(lit.ToArgb() != Palette.Surface.ToArgb() && grey(lit) && lit.R >= Palette.Surface.R && lit.R <= wanted.R && lit.R - Palette.Surface.R <= 24 && wanted.R - Palette.Surface.R >= 12,
                    "The lit row is not a light grey a little above the row's own colour: " + lit + " for " + wanted);
                image.Save(Path.Combine(Path.GetDirectoryName(source), "polish-left-out-lit-" + tree.DeviceDpi + ".png"), ImageFormat.Png);
            }
            Call(form, "Flash", new List<string>());
            Get<ComboBox>(form, "targetCharacter").SelectedItem = "Ingrid"; Get<PaddedTextField>(form, "outfitName").Text = "Left out fixture";
            WaitIdle(form);
            CompositionPlan plan = Get<CompositionPlan>(form, "plan");
            Require(plan != null && plan.CanInstall && plan.Warnings.Contains("'Long hair' may not work without 'Body'.") && plan.StrictErrors().Contains("'Long hair' needs its parent 'Body'."), "The plan does not carry the note: " + (plan == null ? "no plan" : String.Join(" / ", plan.Errors.Concat(plan.Warnings))));
            Require(Get<Label>(form, "planSummary").Text.StartsWith("Ready to install") && Get<Button>(form, "installButton").Enabled && Get<Label>(form, "planDetails").Text.StartsWith("May not work without “Body”"), "Install is off, or the footer is silent, with an option left out: " + Get<Label>(form, "planSummary").Text + " / " + Get<Label>(form, "planDetails").Text);
            // Ticking it again takes the line away, and from then on it is ticked with the option that needs it.
            nodes[body.Id].Checked = true;
            Require(form.CurrentNeeds.Count == 0 && notice.Text == "" && !form.IsFlashing(body.Id), "The line stayed after the option was ticked again.");
            nodes[hair.Id].Checked = false; nodes[body.Id].Checked = false; nodes[body.Id].Checked = true; nodes[body.Id].Checked = false; nodes[hair.Id].Checked = true;
            Require(nodes[body.Id].Checked, "After hand unticks, ticking the option under it did not bring it back.");
            Call(form, "FillComponents", new List<string> { hair.Id });
            // Opening a selection that leaves an option out says so in the line and lights nothing up (13 Nov 2026).
            Require(notice.Text == "May not work without “Body”" && form.CurrentNeeds.Count == 1, "Opening a selection that leaves an option out did not say so.");
            Require(!form.IsFlashing(body.Id) && !form.IsFlashing(hair.Id), "Opening a mod with an option left out lit its row up.");
            nodes[hair.Id].Checked = false; nodes[hair.Id].Checked = true;
            Require(nodes[body.Id].Checked && notice.Text == "", "Ticking an option of a saved selection did not bring back the option it sits under.");
            Call(form, "ClosePackage");
            Require(notice.Text == "" && form.CurrentNeeds.Count == 0, "The line stayed after the mod was closed.");
            report.Add("PASS: what the ticks leave out (1.0.19): ticking an option brings back the option it sits under; a saved selection that leaves it out opens as it was, the line under the preview picture names it (\"May not work without “Body”\"), no row lights up for a tick or for an opened mod, a click on the line lights its row in a light grey with a grey edge (13 Nov 2026), Install stays on and the footer repeats the note; the Assistant's strict list still holds the old error; ticking it again or closing the mod clears the line; the options list has no indent.");
        }

        private static void CheckDrop(MainForm form, string archive, string folder, string root, List<string> report)
        {
            string reason;
            Require(DropPaths.Accept(new[] { archive }, out reason) == archive, "A ZIP archive was not accepted: " + reason);
            Require(DropPaths.Accept(new[] { folder + Path.DirectorySeparatorChar }, out reason) == folder, "A mod folder was not accepted: " + reason);
            string text = Path.Combine(root, "notes.txt"); File.WriteAllText(text, "not a mod");
            Require(DropPaths.Accept(new[] { text }, out reason) == null && reason.Contains("TXT"), "A text file was accepted.");
            Require(DropPaths.Accept(new[] { archive, folder }, out reason) == null, "Several dropped items were accepted.");
            Require(DropPaths.Accept(new[] { Path.Combine(root, "missing.zip") }, out reason) == null, "A missing file was accepted.");
            var overlay = Get<DropOverlay>(form, "dropOverlay");
            MethodInfo enter = typeof(Control).GetMethod("OnDragEnter", PrivateInstance), drop = typeof(Control).GetMethod("OnDragDrop", PrivateInstance), leave = typeof(Control).GetMethod("OnDragLeave", PrivateInstance);
            // Unsupported item: highlighted as refused, nothing opens.
            var refused = new DragEventArgs(new DataObject(DataFormats.FileDrop, new[] { text }), 0, 10, 10, DragDropEffects.Copy | DragDropEffects.Link, DragDropEffects.None);
            enter.Invoke(form, new object[] { refused });
            Require(refused.Effect == DragDropEffects.None && overlay.Visible && !overlay.Accepting, "Unsupported drop was not refused visibly.");
            leave.Invoke(form, new object[] { EventArgs.Empty });
            Require(!overlay.Visible, "Drop highlight stayed visible after the drag left.");
            ModPackage before = Get<ModPackage>(form, "package");
            var accepted = new DragEventArgs(new DataObject(DataFormats.FileDrop, new[] { archive }), 0, 10, 10, DragDropEffects.Copy | DragDropEffects.Link, DragDropEffects.None);
            enter.Invoke(form, new object[] { accepted });
            Require(accepted.Effect == DragDropEffects.Copy && overlay.Visible && overlay.Accepting && overlay.Title.Contains(Path.GetFileName(archive)), "Supported archive was not highlighted for dropping.");
            using (var image = new Bitmap(overlay.Width, overlay.Height)) { overlay.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size)); image.Save(Path.Combine(root, "polish-drop-" + overlay.DeviceDpi + ".png"), ImageFormat.Png); }
            drop.Invoke(form, new object[] { accepted });
            Require(!overlay.Visible, "Drop highlight stayed visible after dropping.");
            WaitIdle(form);
            ModPackage loaded = Get<ModPackage>(form, "package");
            Require(loaded != before && String.Equals(loaded.SourcePath, archive, StringComparison.OrdinalIgnoreCase), "Dropped archive did not open through the shared open path.");
            Require(Get<Panel>(form, "libraryPage").Visible && Get<Label>(form, "packageTitle").Text == loaded.Name, "Dropped archive did not show in Mod library.");
            // The same OpenSource path handles a folder, as Open folder does.
            form.OpenSource(folder); WaitIdle(form);
            Require(String.Equals(Get<ModPackage>(form, "package").SourcePath, folder, StringComparison.OrdinalIgnoreCase), "OpenSource did not open the extracted folder.");
            report.Add("PASS: drag and drop accepts one ZIP/RAR/7Z archive or folder, refuses text files, several items and missing paths with a visible red highlight, shows an accent drop highlight, and opens the dropped fixture archive through OpenSource, the same path Open archive and Open folder use.");
        }

        private static void CheckShortcuts(MainForm form, string archive, List<string> report)
        {
            var expected = new Dictionary<Keys, ShortcutAction> {
                { Keys.Control | Keys.F, ShortcutAction.FocusSearch }, { Keys.Escape, ShortcutAction.ClearSearch },
                { Keys.Control | Keys.D1, ShortcutAction.LibraryPage }, { Keys.Control | Keys.D2, ShortcutAction.InstalledPage }, { Keys.Control | Keys.D3, ShortcutAction.AssistantPage },
                { Keys.Control | Keys.Oemcomma, ShortcutAction.SettingsPage }, { Keys.F5, ShortcutAction.RefreshLibraries },
                { Keys.Control | Keys.O, ShortcutAction.OpenArchive }, { Keys.Control | Keys.Shift | Keys.O, ShortcutAction.OpenFolder },
                { Keys.Control | Keys.Shift | Keys.F, ShortcutAction.ToggleFilters }, { Keys.Tab, ShortcutAction.SwitchMode },   // 1.0.1: Tab flips INSTALL AS in the Mod library
                { Keys.Control | Keys.Shift | Keys.N, ShortcutAction.ToggleNames },   // 1.0.6: mod names / file names in the Mod library
                { Keys.F1, ShortcutAction.HelpPage } };   // 1.0.11: Help
            Require(Shortcuts.All.Length == expected.Count && Shortcuts.All.All(s => expected.ContainsKey(s.Keys) && expected[s.Keys] == s.Action), "Shortcut table differs from the documented mapping.");
            Require(Enum.GetNames(typeof(ShortcutAction)).All(n => n.Replace("InstalledPage", "").IndexOf("Install", StringComparison.OrdinalIgnoreCase) < 0 &&n.IndexOf("Remove", StringComparison.OrdinalIgnoreCase) < 0 && n.IndexOf("Delete", StringComparison.OrdinalIgnoreCase) < 0), "A shortcut can install, uninstall or remove.");
            // 1.0.1: Tab flips INSTALL AS in the Mod library (unless a PAK/tool mod locks it) and flips it back.
            {
                var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                typeof(MainForm).GetMethod("ShowPage", flags).Invoke(form, new object[] { typeof(MainForm).GetField("libraryPage", flags).GetValue(form) }); Application.DoEvents();
                var toggle = (SegmentedToggle)typeof(MainForm).GetField("modeToggle", flags).GetValue(form);
                if (!toggle.Locked)
                {
                    bool before = form.IsReplaceMode;
                    Require(form.RunShortcut(ShortcutAction.SwitchMode) && form.IsReplaceMode != before && toggle.SelectedIndex == (form.IsReplaceMode ? 1 : 0), "Tab did not switch INSTALL AS in the Mod library.");
                    Require(form.RunShortcut(ShortcutAction.SwitchMode) && form.IsReplaceMode == before, "A second Tab did not switch INSTALL AS back.");
                    report.Add("PASS: Tab switches INSTALL AS in the Mod library and back.");
                }
            }
            Panel library = Get<Panel>(form, "libraryPage"), installed = Get<Panel>(form, "installedPage"), settings = Get<Panel>(form, "settingsPage");
            MethodInfo process = typeof(MainForm).GetMethod("ProcessCmdKey", PrivateInstance);
            Func<Keys, bool> press = delegate(Keys keys) { var message = new Message(); return (bool)process.Invoke(form, new object[] { message, keys }); };
            Require(press(Keys.Control | Keys.D2) && installed.Visible, "Ctrl+2 did not open Installed outfits."); WaitIdle(form);
            Require(press(Keys.Control | Keys.D3) && Get<Panel>(form, "assistantPage").Visible && !installed.Visible, "Ctrl+3 did not open the Assistant.");
            Require(press(Keys.Control | Keys.Oemcomma) && settings.Visible, "Ctrl+, did not open Settings.");
            Require(press(Keys.Control | Keys.D1) && library.Visible, "Ctrl+1 did not open Mod library.");
            form.Activate(); Application.DoEvents();
            PaddedTextField search = Get<PaddedTextField>(form, "search");
            Require(press(Keys.Control | Keys.F) && (search.ContainsFocus || form.ActiveControl == search || form.ActiveControl == search.Editor), "Ctrl+F did not focus the library search.");
            search.Text = "zzz"; Application.DoEvents();
            if (!search.ContainsFocus) search.Editor.Focus();
            // A hidden test window may not own keyboard focus; then Esc is
            // exercised through the same handler with the focus check reported.
            bool escFocused = search.ContainsFocus;
            if (escFocused) Require(press(Keys.Escape) && search.TextLength == 0, "Esc did not clear the focused search.");
            else { search.Clear(); report.Add("NOTE: hidden window had no keyboard focus; Esc-clears-search was not exercised natively in this run."); }
            search.Clear();
            Require(!press(Keys.Escape), "Esc was consumed with nothing to clear.");
            Require(press(Keys.F5), "F5 was not handled."); WaitIdle(form);
            // 2.3.0: Ctrl+Shift+F shows and hides the Filters panel (from another page it opens the library).
            FilterPanel filters = Get<FilterPanel>(form, "filterPanel");
            Require(press(Keys.Control | Keys.Shift | Keys.F) && filters.Visible && library.Visible, "Ctrl+Shift+F did not open the Filters panel.");
            Require(press(Keys.Control | Keys.Shift | Keys.F) && !filters.Visible, "Ctrl+Shift+F did not close the Filters panel.");
            Require(press(Keys.Control | Keys.D2) && installed.Visible, "Ctrl+2 did not open Installed outfits."); WaitIdle(form);
            Require(press(Keys.Control | Keys.Shift | Keys.F) && filters.Visible && library.Visible, "Ctrl+Shift+F on another page did not open the library with Filters.");
            Require(press(Keys.Control | Keys.Shift | Keys.F) && !filters.Visible, "Ctrl+Shift+F did not close the Filters panel again.");
            // Enter in the library search opens the top match.
            var entry = new LibraryEntry { Name = "Fixture required bundle", SourcePath = archive, Kind = "ZIP", Character = "Ingrid", OptionCount = 3 };
            Set(form, "library", new List<LibraryEntry> { entry }); Call(form, "ApplyFilters");
            Set(form, "package", null);
            search.Text = "fixture"; form.OpenTopLibraryMatch(); WaitIdle(form);
            Require(Get<ModPackage>(form, "package") != null && String.Equals(Get<ModPackage>(form, "package").SourcePath, archive, StringComparison.OrdinalIgnoreCase), "Enter/top match did not open the first library result.");
            search.Clear();
            var gear = Get<FlatButton>(form, "navSettings");
            var menu = gear.ContextMenuStrip.Items.OfType<ToolStripMenuItem>().FirstOrDefault(i => i.Text == "Keyboard shortcuts");
            Require(menu != null && menu.DropDownItems.Count == Shortcuts.All.Length && menu.DropDownItems.OfType<ToolStripMenuItem>().All(i => !String.IsNullOrEmpty(i.ShortcutKeyDisplayString)), "Gear menu does not list the keyboard shortcuts.");
            Require(gear.ContextMenuStrip.Items.OfType<ToolStripMenuItem>().Any(i => i.Text.StartsWith("Settings") && i.ShortcutKeyDisplayString == "Ctrl+,"), "Settings menu item does not show its shortcut.");
            report.Add("PASS: shortcuts Ctrl+F/Esc/Ctrl+1/Ctrl+2/Ctrl+3/Ctrl+,/F5/Ctrl+O/Ctrl+Shift+O/Ctrl+Shift+F map as documented (none installs, uninstalls or removes); page switching, search focus/clear and refresh work through ProcessCmdKey; Enter in search opens the top match; the gear lists every shortcut.");
        }

        private static void CheckInstalledDetails(MainForm form, List<string> report, string root)
        {
            Panel installed = Get<Panel>(form, "installedPage");
            Call(form, "ShowPage", installed); Call(form, "RefreshInstalled", new object[] { null }); WaitIdle(form);
            var list = Get<ListView>(form, "installedList");
            Require(list.Items.Count == 2, "Installed fixtures were not listed: " + list.Items.Count);
            var buttons = new[] { "openInstalledButton", "editInstalledButton", "toggleInstalledButton", "disableInstalledButton", "removeInstalledButton" }.Select(n => Get<FlatButton>(form, n)).ToArray();
            foreach (ListViewItem row in list.Items) row.Selected = false;
            // 1.0.1: the default sort is Installed (newest first), so the rows are found by kind, not position.
            ListViewItem manualItem = list.Items.Cast<ListViewItem>().FirstOrDefault(i => i.Tag is InstalledOutfit && !((InstalledOutfit)i.Tag).Managed), managedItem = list.Items.Cast<ListViewItem>().FirstOrDefault(i => i.Tag is InstalledOutfit && ((InstalledOutfit)i.Tag).Managed);
            Require(manualItem != null && managedItem != null, "Fixture lacks a manual and a managed outfit.");
            manualItem.Selected = true; Application.DoEvents();
            var manual = (InstalledOutfit)manualItem.Tag;
            var visible = buttons.Where(b => b.Visible).ToArray();
            // 1.0.18 (the user): an even number of actions pairs up, Uninstall included. Open folder · Load order, then
            // Recipes · Uninstall: Uninstall is a normal button at the end of the second row.
            Require(visible.Length == 2 && visible[0] == buttons[0] && visible[1] == buttons[4] && visible[1].Top > visible[0].Bottom && visible[0].Width == visible[1].Width && visible[1].Left > visible[0].Right
                && Get<FlatButton>(form, "recipesButton").Top == visible[1].Top && Get<FlatButton>(form, "loadOrderButton").Top == visible[0].Top, "Manual outfit actions: Open folder and Load order, then Recipes and Uninstall.");
            var title = Get<ClampedLabel>(form, "installedTitle");
            int line; using (Graphics g = title.CreateGraphics()) line = TextRenderer.MeasureText(g, "Ag", title.Font).Height;
            Require(title.Height <= line * 2 + title.Padding.Vertical + 2 && title.Height >= line, "Installed title is not limited to two lines: " + title.Height + " vs line " + line);
            Require(Get<ChipStrip>(form, "installedChips").Chips.Any(c => c.Text == "Juri") && Get<ChipStrip>(form, "installedChips").Chips.Any(c => c.Text == "Manual"), "Installed chips omit character or managed state.");
            Require(Get<Panel>(form, "installedPathRow").Visible && Get<Label>(form, "installedDetail").Text == manual.Folder, "Installed path row is missing.");
            manualItem.Selected = false; managedItem.Selected = true; Application.DoEvents();
            visible = buttons.Where(b => b.Visible).ToArray();
            // 1.0.15: Open folder, Edit options / Health, Disable outfit / Uninstall alone on the full width (no gap).
            Require(visible.Length == 5 && visible[0].Top == visible[1].Top && visible[2].Top == visible[3].Top && visible[0].Left == visible[2].Left && visible[1].Left == visible[3].Left && visible[0].Width == visible[3].Width
                && visible[4].Top > visible[3].Top && visible[4].Left == visible[0].Left && visible[4].Right == visible[1].Right, "Managed outfit actions are not an aligned grid.");
            foreach (var button in visible) Require(button.Parent.ClientRectangle.Contains(button.Bounds), "Installed action is clipped: " + button.Text);
            using (var image = new Bitmap(installed.Width, installed.Height)) { installed.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size)); image.Save(Path.Combine(root, "polish-installed-" + installed.DeviceDpi + ".png"), ImageFormat.Png); }
            report.Add("PASS: installed details: two-line title, character/state chips, path row with copy action, actions in an aligned two-column grid with Uninstall last (manual: an even count, so Uninstall pairs up; managed: an odd count, so Uninstall takes the last row) without clipping.");
        }

        private static void CheckSettingsLibraries(MainForm form, string root, List<string> report)
        {
            Panel settings = Get<Panel>(form, "settingsPage");
            Call(form, "ShowPage", settings); Call(form, "LoadSettingsFields");
            var libraries = Get<ListBox>(form, "settingsLibraries");
            Require(libraries.DrawMode == DrawMode.OwnerDrawFixed && libraries.ItemHeight == DpiMetrics.Px(libraries, 50), "Library rows are not padded owner-drawn rows.");
            libraries.Items.Add(root); libraries.Items.Add(Path.Combine(root, "no such library"));
            MethodInfo status = typeof(MainForm).GetMethod("LibraryFolderStatus", PrivateInstance);
            object[] found = { root, null }, missing = { Path.Combine(root, "no such library"), null };
            string a = (string)status.Invoke(form, found), b = (string)status.Invoke(form, missing);
            Require(a.StartsWith("Found") && (Color)found[1] == Palette.Green && b == "Folder not found" && (Color)missing[1] == Palette.Red, "Library folder status is wrong: " + a + " / " + b);
            var remove = Get<FlatButton>(form, "removeLibraryButton");
            libraries.SelectedIndex = -1; Application.DoEvents();
            Require(!remove.Enabled, "Remove is enabled without a selected library.");
            libraries.SelectedIndex = 1; Application.DoEvents();
            Require(remove.Enabled, "Remove is disabled with a selected library.");
            var layout = (TableLayoutPanel)Get<PaddedTextField>(form, "settingsGame").Parent;
            Control browse = layout.GetControlFromPosition(1, 1), add = remove.Parent.Controls.OfType<FlatButton>().First(c => c.Text == "Add");
            Require(Math.Abs(BoundsIn(add, layout).Left - browse.Left) <= 1 && Math.Abs(BoundsIn(add, layout).Width - browse.Width) <= 1, "Add is not aligned with Browse.");
            Require(AllControls(settings).OfType<Button>().Any(x => x.Text == "Save settings" && x.Visible), "Save settings is missing.");
            libraries.Items.Clear();
            report.Add("PASS: settings libraries use padded owner-drawn rows with found/missing state and scan counts; Remove follows the selection; Add/Remove align with Browse.");
        }

        private static void CheckTiming(MainForm form, List<string> report)
        {
            var entries = Enumerable.Range(0, 900).Select(i => new LibraryEntry { Name = (i % 9 == 0 ? "Juri " : i % 5 == 0 ? "Cammy " : "Outfit ") + i.ToString("000"), Kind = i % 3 == 0 ? "RAR" : "ZIP", Character = i % 9 == 0 ? "Juri" : "Cammy", SourcePath = @"C:\fixture\timing\" + i.ToString("000") + ".zip", OptionCount = i % 7 }).ToList();
            Panel library = Get<Panel>(form, "libraryPage"), installed = Get<Panel>(form, "installedPage"), settings = Get<Panel>(form, "settingsPage");
            Call(form, "ShowPage", library);
            Set(form, "library", entries); Set(form, "selectingMod", true);
            var list = Get<ListView>(form, "modList"); var search = Get<PaddedTextField>(form, "search");
            try
            {
                Stopwatch clock = Stopwatch.StartNew(); Call(form, "ApplyFilters"); list.Update(); double all = clock.Elapsed.TotalMilliseconds;
                // Large libraries pace typing: text changes queue one filter pass.
                search.Text = "juri";
                var timer = Get<System.Windows.Forms.Timer>(form, "searchTimer");
                Require(timer.Enabled && list.Items.Count == 900, "Typing in a large library did not defer filtering.");
                clock.Restart(); Call(form, "FlushLibraryFilter"); list.Update(); double juri = clock.Elapsed.TotalMilliseconds;
                Require(!timer.Enabled && list.Items.Count == 100, "Deferred search did not apply: " + list.Items.Count);
                search.Text = ""; clock.Restart(); Call(form, "FlushLibraryFilter"); list.Update(); double clear = clock.Elapsed.TotalMilliseconds;
                Require(list.Items.Count == 900, "Clearing the search did not restore all rows.");
                var pages = new List<double>();
                foreach (Panel page in new[] { installed, settings, library, installed, settings, library }) { clock.Restart(); Call(form, "ShowPage", page); form.Update(); pages.Add(clock.Elapsed.TotalMilliseconds); }
                WaitIdle(form);
                Require(all < 2000 && juri < 2000 && clear < 2000 && pages.Max() < 2000, "A UI step exceeded two seconds.");
                report.Add(String.Format("PASS: timing (synthetic 900-entry library, informational): all rows {0:0.0} ms; 'juri' {1:0.0} ms; clear {2:0.0} ms; page switches max {3:0.0} ms, median {4:0.0} ms. Search is deferred while typing above {5} entries.", all, juri, clear, pages.Max(), pages.OrderBy(p => p).ElementAt(pages.Count / 2), MainForm.DebouncedLibrarySize));
            }
            finally { Set(form, "selectingMod", false); Set(form, "library", new List<LibraryEntry>()); Call(form, "ApplyFilters"); }
        }

        private static void CheckRealRequired(string archive, List<string> report)
        {
            ModPackage package = PackageReader.Load(archive, @"C:\Program Files\7-Zip\7z.exe");
            package.PrepareChoices();
            var defaults = Composer.DefaultSelection(package);
            var live = Composer.UnresolvedChoiceGroups(package, defaults).Select(g => g.Name).OrderBy(n => n).ToArray();
            // 1.0.19: with something ticked, an empty required group is a note in the plan (Needs), not an error.
            var plan = defaults.Count == 0 ? live : Composer.Plan(package, defaults).Needs.Where(n => n.Needed.IsDummy).Select(n => n.Needed.Name).OrderBy(n => n).ToArray();
            Require(live.SequenceEqual(plan), "Real archive: live required groups differ from the plan.");
            report.Add("PASS: real archive (read only) " + Path.GetFileName(archive) + ": live required groups match the plan: " + String.Join(", ", live) + "; footer shows \"" + "Choose: " + String.Join(", ", Composer.UnresolvedChoiceGroups(package, defaults).Select(g => MainForm.ShortGroupName(g, package))) + "\".");
        }

        private static void WaitIdle(MainForm form)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(30);
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
        private static void Put(string root, string path, string contents) { string file = Safety.Under(root, path); Directory.CreateDirectory(Path.GetDirectoryName(file)); File.WriteAllText(file, contents, new UTF8Encoding(false)); }
        // 2.4.0: no hover text anywhere: no code creates a ToolTip or sets a tooltip text (every method body of the
        // app), and no control of the window has one on any page or Settings tab.
        // 2.4.1: popups only where they are absolutely necessary. A new
        // ThemedMessageBox/MessageBox call site fails here until it is added deliberately with a reason.
        internal static readonly string[] PopupAllowList =
        {
            "AssistantPage.Build",          // the usage meter's per-model breakdown: a viewer the user opens by clicking
            "MainForm.AskDrift",            // another tool changed our files: Restore original / Keep current (only the user can decide)
            "MainForm.ArchiveDeleteChoice",   // deleting the original archive can remove a replacement repair source
            "MainForm.ConfirmReclaimSpace", // Reclaim space deletes stored copies: repairs then need the mod archives (no Undo)
            "MainForm.AskPermanentDelete",  // library Delete where the Recycle Bin can't take it: deleted for good (no Undo); never asked when it can
            "MainForm.BuildGameBanner",     // the banner's details: a viewer the user opens by clicking the banner text
            "MainForm.InspectOrToggleInstalled", // Health on an installed extra outfit opens its file and loader report
            "MainForm.RemoveApiKey",        // the saved API key is deleted for good (no Undo)
            "Program.ShowError",            // an error the app cannot continue after (start-up, unhandled)
            "RecoveryForm.DeleteSelected",  // a recoverable copy is deleted for good (no Undo)
            "MainForm.ConfirmVanilla",      // 1.0.1: Restore game to vanilla removes every mod for good (no Undo)
            "MainForm.ConfirmLostParts",    // 1.0.18 Added parts Save: asked only when added parts are lost for good (their mods are gone); otherwise no question, Undo puts the list back
        };
        // 2.4.1: modal windows (Form.ShowDialog). Each is opened by the user's own click to view or edit something,
        // except OutfitSetsForm.Confirm (a saved copy deleted for good, no Undo). Only the welcome opens on its own, once.
        internal static readonly string[] WindowAllowList =
        {
            "WelcomeForm.ShowWelcome",      // 1.0.11 the welcome: once at the first start, then gear → Welcome (Esc / Close)
            "TourForm.Run",                 // 1.0.15 the tours: once per page at first use, then gear → Take the tour (Esc / Skip tour)
            "AddPartsForm.Choose",          // 1.0.6 Add to outfit: pick the installed outfit and the parts (Cancel changes nothing)
            "AddedPartsForm.Choose",        // 1.0.18 Added parts: reorder or remove what other mods added to one outfit (Cancel changes nothing; Save has Undo)
            "RecipeBuilderForm.Choose",     // 1.0.18 New recipe: fighter, base, parts on top, what overrides what (Cancel saves nothing)
            "RecipeBuilderForm.EnlargePreview", // 1.0.18 the builder's preview image, enlarged (click the preview; Esc closes)
            "OptionPickForm.Choose",        // 1.0.18 the builder's Options: tick a mod's options (Cancel keeps the earlier ticks)
            "RecipesForm.Choose",           // 1.0.18 Outfit recipes: the saved recipes; Install, Delete recipe, save the selected outfit (Close changes nothing)
            "OutfitOrderForm.Choose",       // 1.0.18 Outfit order: drag one fighter's extra outfits into place (Cancel changes nothing; Save has Undo)
            "FluffyPresetForm.Review",      // Import Fluffy preset: map each entry before anything installs (Cancel installs nothing)
            "MainForm.ShowMigration",       // Migrate installed mods: review, then wait while the user uninstalls there (Close keeps the saved plan)
            "MainForm.EnlargePreview",      // the preview image, enlarged (click the preview)
            "MainForm.ShowCompositionReport", // Health (Extra outfit mode): the compatibility report
            "MainForm.BuildLibraryPage",    // 1.0.15 right-click → Reorder bundle: the editor (Close / Discard changes)
            "MainForm.InspectOrToggleInstalled", // 1.0.15 Installed → Health: the read-only file report (Close)
            "MainForm.ShowCurrentReport",   // Health (Replace original): the full report; the inspector shows the summary inline
            "MainForm.ShowPresetsWindow",   // Presets…
            "OutfitSetsForm.Confirm",       // Delete saved copy: permanent (no Undo)
            "OutfitSetsForm.ShowPresets",   // Presets… (older entry point kept for callers without the replace engine)
            "RecoveryForm.ShowRecovery",    // Recover…
        };
        private static void CheckPopupAllowList(List<string> report)
        {
            List<string> windows = UiAudit.DialogCalls();
            var unexpectedWindows = windows.Where(s => !WindowAllowList.Contains(s)).ToList();
            Require(unexpectedWindows.Count == 0, "modal windows outside the allow-list: " + String.Join(", ", unexpectedWindows));
            report.Add("PASS: modal windows only on the allow-list (" + windows.Count + ": " + String.Join(", ", windows) + ")" + (WindowAllowList.Any(s => !windows.Contains(s)) ? "; unused: " + String.Join(", ", WindowAllowList.Where(s => !windows.Contains(s))) : ""));
            List<string> sites = UiAudit.PopupCalls();
            var unexpected = sites.Where(s => !PopupAllowList.Contains(s)).ToList();
            var gone = PopupAllowList.Where(s => !sites.Contains(s)).ToList();
            Require(unexpected.Count == 0, "popup call sites outside the allow-list: " + String.Join(", ", unexpected));
            report.Add("PASS: popups only on the allow-list (" + sites.Count + " call sites: " + String.Join(", ", sites) + ")" + (gone.Count > 0 ? "; allow-list entries no longer used: " + String.Join(", ", gone) : ""));
        }

        // 1.0.11 welcome: three topics (tabs, arrows, Back / Next / Done), sized for the tallest topic so nothing is cut;
        // never opens by itself in a headless window. Renders each topic for review.
        private static void CheckWelcome(MainForm form, string root, List<string> report)
        {
            Require(Application.OpenForms.OfType<WelcomeForm>().Count() == 0, "the welcome opened by itself in a check window");
            using (var welcome = new WelcomeForm())
            {
                welcome.ShowInTaskbar = false; welcome.Opacity = 0; welcome.Show(form); Application.DoEvents();
                var page = (WelcomePage)welcome.Controls.Find("welcomePage", true).Single();
                Func<string, FlatButton> button = delegate(string name) { return (FlatButton)welcome.Controls.Find(name, true).Single(); };
                Require(page.Height >= page.TallestHeight(), "welcome cut short: " + page.Height + " < " + page.TallestHeight());
                Require(welcome.FormBorderStyle == FormBorderStyle.None && welcome.Controls.Find("welcomeDismiss", true).Length == 1, "the welcome has window chrome or no ✕");
                // The user's header: the app icon and a plain title with "(SLM)", not cut off.
                Label title = (Label)welcome.Controls.Find("welcomeTitle", true).Single(); PictureBox icon = (PictureBox)welcome.Controls.Find("welcomeIcon", true).Single();
                Require(title.Text == "Welcome to Slotsloader Manager (SLM)" && title.PreferredWidth <= title.Width && icon.Image != null && icon.Image.Width == DpiMetrics.Px(welcome, 20), "welcome header: '" + title.Text + "' " + title.PreferredWidth + "/" + title.Width + ", icon " + (icon.Image == null ? "none" : icon.Image.Width.ToString()));
                // How it looks over the app: the main window under ShowWelcome's black layer (DimOpacity), the card centred.
                using (var shot = new Bitmap(form.ClientSize.Width, form.ClientSize.Height))
                using (var card = new Bitmap(welcome.Width, welcome.Height))
                {
                    form.DrawToBitmap(shot, new Rectangle(Point.Empty, shot.Size)); welcome.DrawToBitmap(card, new Rectangle(Point.Empty, card.Size));
                    using (Graphics g = Graphics.FromImage(shot))
                    {
                        using (var dim = new SolidBrush(Color.FromArgb((int)Math.Round(255 * WelcomeForm.DimOpacity), Color.Black))) g.FillRectangle(dim, 0, 0, shot.Width, shot.Height);
                        g.DrawImage(card, (shot.Width - card.Width) / 2, (shot.Height - card.Height) / 2);
                    }
                    shot.Save(Path.Combine(root, "welcome-overlay-" + welcome.DeviceDpi + ".png"), ImageFormat.Png);
                }
                for (int i = 0; i < WelcomeForm.Topics.Length; i++)
                {
                    bool last = i == WelcomeForm.Topics.Length - 1;
                    Require(welcome.Index == i && button("welcomeNext").Text == (last ? "Done" : "Next"), "welcome topic " + i + " button");
                    Require(page.AccessibleName == WelcomeForm.Topics[i].Tab + ": " + WelcomeForm.Topics[i].Title, "welcome accessible name: " + page.AccessibleName);
                    using (var b = new Bitmap(welcome.Width, welcome.Height)) { welcome.DrawToBitmap(b, new Rectangle(Point.Empty, b.Size)); b.Save(Path.Combine(root, "welcome-" + (i + 1) + "-" + welcome.DeviceDpi + ".png"), ImageFormat.Png); }
                    if (!last) button("welcomeNext").PerformClick(); Application.DoEvents();
                }
                page.Index = 0; Application.DoEvents();
                Require(welcome.Index == 0, "welcome back to the first topic");
                // Only ✕ and Next / Done (1.0.11, the user), both compact.
                Require(welcome.Controls.Find("welcomeBack", true).Length == 0 && welcome.Controls.Find("welcomeClose", true).Length == 0, "the welcome still has Back or Close");
                Require(button("welcomeNext").Height <= DpiMetrics.Px(welcome, 32) && button("welcomeDismiss").Height <= DpiMetrics.Px(welcome, 26), "welcome buttons too tall: " + button("welcomeNext").Height + " / " + button("welcomeDismiss").Height);
                button("welcomeNext").PerformClick(); button("welcomeNext").PerformClick(); button("welcomeNext").PerformClick(); Application.DoEvents();
                Require(welcome.IsDisposed || !welcome.Visible, "Done did not close the welcome");
            }
            report.Add("PASS: welcome (1.0.11): a borderless card with ✕, three topics with tabs, Next / Done (no Back or Close); sized to the tallest topic; never opens by itself in a check window; renders welcome-1..3.png and welcome-overlay.png.");
        }

        // 1.0.15 tours (Tours.cs, MainFormTours.cs): every step outlines a visible control inside the window, its card fits
        // the window without covering that control, Back from step 2 and Done on the last; never opens by itself in a
        // check window. Renders each step over the window (tour-<id>-<n>-<dpi>.png) for review.
        private static void CheckTours(MainForm form, string root, List<string> report)
        {
            Require(Application.OpenForms.OfType<TourForm>().Count() == 0, "a tour opened by itself in a check window");
            var counts = new List<string>();
            foreach (Tour tour in new[] { form.LibraryTour(), form.InstalledTour(), form.SettingsTour() })
            {
                List<TourStep> steps = TourForm.Applicable(form, tour);
                int expected = tour.Id == MainForm.InstalledTourId ? 3 : tour.Steps.Count;   // Installed: rows from elsewhere and Reapply only when there are such rows
                Require(steps.Count >= expected && steps.Count <= tour.Steps.Count, "tour " + tour.Id + ": " + steps.Count + " of " + tour.Steps.Count + " steps apply");
                using (var card = new TourForm(form, steps))
                {
                    card.ShowInTaskbar = false; card.Opacity = 0; card.ShowStep(0); card.Show(form); Application.DoEvents();
                    Func<string, Control> part = delegate(string name) { return card.Controls.Find(name, true).Single(); };
                    for (int i = 0; i < steps.Count; i++)
                    {
                        card.ShowStep(i); Application.DoEvents();
                        string context = "tour " + tour.Id + " step " + (i + 1) + " '" + steps[i].Title + "'";
                        Rectangle client = form.ClientRectangle;
                        Require(!card.Hole.IsEmpty && client.Contains(card.Hole), context + ": outline " + card.Hole + " outside the window " + client);
                        Require(client.Contains(card.CardBounds), context + ": card " + card.CardBounds + " outside the window " + client);
                        Require(!card.CardBounds.IntersectsWith(card.Hole), context + ": card " + card.CardBounds + " covers the outlined control " + card.Hole);
                        Require(part("tourNext").Text == (i == steps.Count - 1 ? "Done" : "Next") && part("tourBack").Visible == (i > 0) && part("tourSkip").Visible, context + ": buttons");
                        Require(steps[i].Body.All(line => !line.Contains("—")), context + ": an em-dash in the copy");
                        RenderTour(form, card, Path.Combine(root, "tour-" + tour.Id + "-" + (i + 1) + "-" + form.DeviceDpi + ".png"));
                    }
                    ((FlatButton)part("tourNext")).PerformClick(); Application.DoEvents();
                    Require(card.IsDisposed || !card.Visible, "tour " + tour.Id + ": Done did not close it");
                }
                counts.Add(tour.Id + " " + steps.Count);
            }
            report.Add("PASS: tours (1.0.15): every step outlines a control inside the window and its card fits without covering it; Back / Next / Done; never opens by itself (" + String.Join(", ", counts) + " steps); renders tour-<id>-<n>.png.");
        }

        // The window as the tour shows it: dimmed except the outlined control, the accent ring, the card (and pointer).
        private static void RenderTour(MainForm form, TourForm card, string file)
        {
            using (var window = new Bitmap(form.Width, form.Height))
            using (var shot = new Bitmap(form.ClientSize.Width, form.ClientSize.Height))
            using (var cardImage = new Bitmap(card.Width, card.Height))
            {
                form.DrawToBitmap(window, new Rectangle(Point.Empty, window.Size));
                Point origin = form.PointToScreen(Point.Empty); origin.Offset(-form.Left, -form.Top);
                card.DrawToBitmap(cardImage, new Rectangle(Point.Empty, cardImage.Size));
                using (Graphics g = Graphics.FromImage(shot))
                {
                    g.DrawImage(window, -origin.X, -origin.Y);
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    using (var cut = new System.Drawing.Drawing2D.GraphicsPath { FillMode = System.Drawing.Drawing2D.FillMode.Alternate })
                    using (var hole = Glyphs.Rounded(card.Hole, DpiMetrics.Px(form, 8)))
                    using (var dim = new SolidBrush(Color.FromArgb((int)Math.Round(255 * WelcomeForm.DimOpacity), Color.Black)))
                    using (var ring = new Pen(Palette.Accent, Math.Max(2, DpiMetrics.Px(form, 3))))
                    {
                        cut.AddRectangle(new Rectangle(Point.Empty, shot.Size)); cut.AddPath(hole, false);
                        g.FillPath(dim, cut); g.DrawPath(ring, hole);
                    }
                    Rectangle at = form.RectangleToClient(card.Bounds);
                    using (var shape = (System.Drawing.Drawing2D.GraphicsPath)card.Shape.Clone())
                    using (var move = new System.Drawing.Drawing2D.Matrix())
                    {
                        move.Translate(at.X, at.Y); shape.Transform(move);
                        g.SetClip(shape); g.DrawImage(cardImage, at.X, at.Y); g.ResetClip();
                    }
                }
                shot.Save(file, ImageFormat.Png);
            }
        }

        // 1.0.11 Help (gear → Help, F1): every topic of docs/HELP.md in the list and readable in the article (rendered for
        // review), Previous / Next in order, search narrows the list, the welcome's Help link, Show welcome in Settings.
        private static void CheckHelp(MainForm form, string root, List<string> report)
        {
            List<HelpTopic> topics = HelpDocument.Topics;
            Require(topics.Count > 0 && topics.All(t => t.Group.Length > 0 && t.Title.Length > 0 && t.Lead.Length > 0 && t.Blocks.Count > 0), "help topics: " + topics.Count + " (each needs a group, title, lead and text)");
            Require(Shortcuts.All.Any(s => s.Keys == Keys.F1 && s.Action == ShortcutAction.HelpPage), "F1 is not the Help shortcut");
            Call(form, "RunShortcut", ShortcutAction.HelpPage); Application.DoEvents();
            Panel page = Get<Panel>(form, "helpPage"); var list = Get<HelpTopicList>(form, "helpTopics"); var article = Get<HelpArticleView>(form, "helpArticle"); var search = Get<PaddedTextField>(form, "helpSearch");
            Require(page.Visible && list.Topics.SequenceEqual(topics) && article.Topic == topics[0] && list.Selected == topics[0], "F1 did not open Help on the first topic");
            string folder = Path.Combine(root, "help"); Directory.CreateDirectory(folder);
            for (int i = 0; i < topics.Count; i++)
            {
                Call(form, "ShowHelpTopic", topics[i]); Application.DoEvents();
                Require(article.Topic == topics[i] && list.Selected == topics[i] && article.Height >= article.NaturalHeight(article.Width), "help topic " + topics[i].Title + " is cut short");
                using (var b = new Bitmap(article.Width, Math.Min(article.Height, DpiMetrics.Px(form, 2400)))) { article.DrawToBitmap(b, new Rectangle(0, 0, b.Width, b.Height)); b.Save(Path.Combine(folder, (i + 1).ToString("00") + ".png"), ImageFormat.Png); }
            }
            using (var b = new Bitmap(form.ClientSize.Width, form.ClientSize.Height)) { Call(form, "ShowHelpTopic", topics[0]); Application.DoEvents(); form.DrawToBitmap(b, new Rectangle(Point.Empty, b.Size)); b.Save(Path.Combine(folder, "page.png"), ImageFormat.Png); }
            // Search: every word must appear; no match empties the list and leaves the article alone.
            string word = topics.Last().Title.Split(' ').OrderByDescending(w => w.Length).First();
            search.Text = word; Application.DoEvents();
            Require(list.Topics.Any() && list.Topics.All(t => t.Matches(word)) && list.Topics.Contains(article.Topic), "help search for '" + word + "': " + String.Join(", ", list.Topics.Select(t => t.Title)));
            HelpTopic shown = article.Topic;
            search.Text = "zzqx nothing matches"; Application.DoEvents();
            Require(!list.Topics.Any() && article.Topic == shown, "a search with no match");
            search.Text = ""; Application.DoEvents();
            Require(list.Topics.Count() == topics.Count, "clearing the help search");
            // [[links]]: every one names a topic (and a heading of it when it says #Heading).
            var broken = new List<string>();
            foreach (HelpTopic t in topics)
                foreach (string text in new[] { t.Lead }.Concat(t.Blocks.Select(b => b.Text)))
                    foreach (HelpRun run in HelpDocument.Runs(text).Where(r => r.Link != null))
                    {
                        string heading; HelpTopic target = HelpDocument.Find(run.Link, out heading);
                        if (target == null || (heading != null && !target.Blocks.Any(b => b.Kind == HelpBlockKind.Heading && String.Equals(b.Text, heading, StringComparison.OrdinalIgnoreCase)))) broken.Add(t.Title + " → " + run.Link);
                    }
            Require(broken.Count == 0, "help links to no topic or heading: " + String.Join("; ", broken));
            // Following a link opens its topic (at its heading) with "← Back to …"; Back returns to the same place.
            var scroll = Get<OverlayScroller>(form, "helpArticleScroll");
            HelpTopic from = topics.FirstOrDefault(t => t.Blocks.Any(b => HelpDocument.Runs(b.Text).Any(r => r.Link != null)));
            if (from != null)
            {
                HelpRun link = from.Blocks.SelectMany(b => HelpDocument.Runs(b.Text)).First(r => r.Link != null);
                string heading; HelpTopic target = HelpDocument.Find(link.Link, out heading);
                Call(form, "ShowHelpTopic", from); Application.DoEvents();
                scroll.ScrollTo(scroll.MaxOffset / 2, false); int fromOffset = scroll.Offset;
                Call(form, "FollowHelpLink", target, heading); Application.DoEvents();
                Require(article.Topic == target && article.BackTo == from && list.Selected == target && (heading == null || scroll.Offset > 0 || scroll.MaxOffset == 0), "following " + link.Link + " from " + from.Title + ": " + article.Topic.Title + ", back to " + (article.BackTo == null ? "none" : article.BackTo.Title) + ", offset " + scroll.Offset);
                Call(form, "HelpBack"); Application.DoEvents();
                Require(article.Topic == from && article.BackTo == null && scroll.Offset == fromOffset, "Back after a link: " + article.Topic.Title + " at " + scroll.Offset + " (was " + fromOffset + ")");
                report.Add("PASS: Help links: " + topics.Sum(t => t.Blocks.Sum(b => HelpDocument.Runs(b.Text).Count(r => r.Link != null))) + " links all resolve; following " + from.Title + " → " + link.Link + " and Back.");
            }
            // Leaving Help and opening it again resumes the topic and scroll position.
            Call(form, "ShowHelpTopic", topics[topics.Count / 2]); Application.DoEvents();
            scroll.ScrollTo(scroll.MaxOffset, false); int resumeOffset = scroll.Offset;
            Call(form, "ShowPage", Get<Panel>(form, "libraryPage")); Application.DoEvents();
            Call(form, "RunShortcut", ShortcutAction.HelpPage); Application.DoEvents();
            Require(article.Topic == topics[topics.Count / 2] && scroll.Offset == resumeOffset, "reopening Help: " + article.Topic.Title + " at " + scroll.Offset + " (was " + resumeOffset + ")");
            Require((string)typeof(MainForm).GetProperty("HelpResumeTopic", PrivateInstance).GetValue(form, null) == article.Topic.Title, "the view settings would not resume this topic");
            // The welcome's footer link closes it and asks for Help; Settings → General has Show welcome.
            using (var welcome = new WelcomeForm())
            {
                welcome.ShowInTaskbar = false; welcome.Opacity = 0; welcome.Show(form); Application.DoEvents();
                Control link = welcome.Controls.Find("welcomeHelp", true).Single();
                Require(link.Text == "Help and troubleshooting (F1)" && link.Visible, "the welcome's Help link: '" + link.Text + "'");
                Control nextButton = welcome.Controls.Find("welcomeNext", true).Single();
                int linkMiddle = link.Top + link.Height / 2, nextMiddle = nextButton.Top + nextButton.Height / 2;
                Require(Math.Abs(linkMiddle - nextMiddle) <= DpiMetrics.Px(welcome, 2) && ((Label)link).PreferredWidth <= link.Width, "the Help link is not on Next's line or is cut: middles " + linkMiddle + " / " + nextMiddle);
                typeof(Control).GetMethod("OnClick", PrivateInstance).Invoke(link, new object[] { EventArgs.Empty }); Application.DoEvents();
                Require(welcome.HelpRequested && (welcome.IsDisposed || !welcome.Visible), "the welcome's Help link did not close it and ask for Help: requested " + welcome.HelpRequested + ", visible " + (!welcome.IsDisposed && welcome.Visible));
            }
            Require(AllControls(Get<Panel>(form, "settingsPage")).OfType<FlatButton>().Any(b => b.Text == "Show welcome"), "Settings → General has no Show welcome");
            Call(form, "ShowPage", Get<Panel>(form, "libraryPage")); Application.DoEvents();
            report.Add("PASS: Help (1.0.11): F1 / gear → Help opens " + topics.Count + " topics in " + topics.Select(t => t.Group).Distinct().Count() + " groups (rendered in help\\), each article whole, search narrows the list, the welcome's Help link, Settings → General → Show welcome.");
        }

        private static void CheckNoTooltips(MainForm form, List<string> report)
        {
            int scanned; List<string> code = UiAudit.ToolTipCode(out scanned);
            Require(scanned > 500 && code.Count == 0, "tooltip code remains: " + String.Join("; ", code));
            var found = new List<string>();
            foreach (string page in new[] { "libraryPage", "installedPage", "settingsPage", "assistantPagePanel" })
            {
                FieldInfo field = typeof(MainForm).GetField(page, PrivateInstance);
                if (field == null) continue;
                Call(form, "ShowPage", field.GetValue(form)); Application.DoEvents();
                found.AddRange(UiAudit.ToolTipFindings(form));
            }
            Call(form, "ShowSettingsTab", true); Application.DoEvents(); found.AddRange(UiAudit.ToolTipFindings(form));
            Call(form, "ShowLoaderTab"); Application.DoEvents(); found.AddRange(UiAudit.ToolTipFindings(form));
            Call(form, "ShowSettingsTab", false); Call(form, "ShowPage", typeof(MainForm).GetField("libraryPage", PrivateInstance).GetValue(form)); Application.DoEvents();
            Require(found.Count == 0, "tooltips in the window: " + String.Join("; ", found.Distinct()));
            report.Add("PASS: no tooltips (2.4.0): " + scanned + " method bodies scanned for ToolTip / ToolTipText; every page and Settings tab of the window has no ToolTip, list, tree or menu tooltip.");
        }

        // 2.4.2 regressions for two errors seen in earlier logs:
        // - DrawComponentNode threw a NullReferenceException when the tree still showed a mod while `package` was null.
        // - "Collection was modified" in ConflictView.SetEntries: a Work completion ran on a pool thread when the worker
        //   started without the WinForms synchronization context, racing the UI thread. Completions now run on the UI
        //   thread, and SetEntries copies its input first.
        private static void CheckOldErrors(MainForm form, List<string> report)
        {
            var tree = Get<OptionTree>(form, "componentTree");
            ModPackage saved = Get<ModPackage>(form, "package");
            var package = new ModPackage { SourcePath = @"C:\fixture\paint.zip", Name = "Paint" };
            var group = new ModComponent { Id = "Colors", Name = "Colors", IsDummy = true };
            var red = new ModComponent { Id = "Colors/Red", Name = "Red", ParentId = "Colors" };
            red.Assets.Add(new NativeAsset { EntryPath = "Colors/Red/natives/x.tex", DestinationPath = "natives/x.tex", Size = 1 });
            package.Components.Add(group); package.Components.Add(red);
            Set(form, "package", package); Call(form, "FillComponents", new List<string> { "Colors/Red" }); tree.ExpandAll(); Application.DoEvents();
            Set(form, "package", null);
            try
            {
                using (var bitmap = new Bitmap(Math.Max(1, tree.Width), Math.Max(1, tree.Height))) tree.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
            }
            catch (NullReferenceException ex) { throw new InvalidOperationException("the options tree threw while package was null: " + ex.Message); }
            finally { Set(form, "package", saved); }
            // ConflictView: a live view of its own entries keeps them (copied before clearing).
            var view = new ConflictView();
            view.SetEntries(new[] { new ConflictView.Entry { Text = "one" }, new ConflictView.Entry { Text = "two", Link = "Undo" } });
            view.SetEntries(view.Entries.Where(e => true));
            Require(view.Entries.Count == 2 && view.AccessibleDescription == "one two [Undo]", "SetEntries with a view of its own entries: " + view.Entries.Count);
            // A worker started without a synchronization context completes on the UI thread.
            int ui = Thread.CurrentThread.ManagedThreadId, ranOn = -1, calls = form.OffUiCalls;
            SynchronizationContext context = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
            try
            {
                MethodInfo work = typeof(MainForm).GetMethod("Work", PrivateInstance).MakeGenericMethod(typeof(int));
                work.Invoke(form, new object[] { "Checking threads…", (Func<int>)delegate { return 1; }, (Action<int>)delegate(int x) { ranOn = Thread.CurrentThread.ManagedThreadId; } });
            }
            finally { SynchronizationContext.SetSynchronizationContext(context); }
            DateTime deadline = DateTime.UtcNow.AddSeconds(20);
            while (ranOn < 0 && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(10); }
            Require(ranOn == ui && form.OffUiCalls > calls, "a Work completion without a synchronization context ran on thread " + ranOn + " (UI " + ui + ")");
            report.Add("PASS: 2.4.2 regressions: the options tree paints with no package; ConflictView.SetEntries copies its input; a Work completion started without a synchronization context runs on the UI thread");
        }
        private static T Get<T>(object o, string name) { FieldInfo field = o.GetType().GetField(name, PrivateInstance); if (field == null) throw new MissingFieldException(name); return (T)field.GetValue(o); }
        private static void Set(object o, string name, object value) { o.GetType().GetField(name, PrivateInstance).SetValue(o, value); }
        private static object Call(object o, string name, params object[] args)
        {
            var methods = o.GetType().GetMethods(PrivateInstance | BindingFlags.Public).Where(m => m.Name == name && m.GetParameters().Length == args.Length).ToArray();
            if (methods.Length == 0) throw new MissingMethodException(name);
            return methods[0].Invoke(o, args);
        }
        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException("UI polish regression: " + message); }
    }
}
