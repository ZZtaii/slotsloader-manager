using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Windows.Forms;

namespace SlotsInstaller
{
    internal static class LayoutChecks
    {
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr handle, int index);
        [DllImport("gdi32.dll",EntryPoint="GetObjectW")] private static extern int GetFontObject(IntPtr font,int count,[Out] byte[] buffer);
        private static readonly BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;

        internal static int Run(string source)
        {
            AppConfig settings = AppConfig.Load();
            ModPackage package = String.IsNullOrWhiteSpace(source) ? null : PackageReader.Load(source, settings.SevenZipPath);
            List<string> report = new List<string>();
            SynchronizationContext previous = SynchronizationContext.Current;
            bool previousAutoInstall = WindowsFormsSynchronizationContext.AutoInstall;
            WindowsFormsSynchronizationContext.AutoInstall = false;
            // This entry point intentionally runs without Application.Run.
            // Queue background completions onto the UI thread as the live app does.
            using (WindowsFormsSynchronizationContext uiContext = new WindowsFormsSynchronizationContext())
            {
                SynchronizationContext.SetSynchronizationContext(uiContext);
                try
                {
                    CheckSelectionRepairs(report,uiContext);
                    CheckCancellableWork(report,uiContext);
                    SynchronizationContext.SetSynchronizationContext(uiContext);
                    CheckAtSize(settings, package, new Size(1232, 640), 300, 430, report);
                    CheckAtSize(settings, package, new Size(1460, 920), 500, 550, report);
                    CheckAtSize(settings, package, new Size(1110, 610), 270, 370, report);
                }
                finally { SynchronizationContext.SetSynchronizationContext(previous); WindowsFormsSynchronizationContext.AutoInstall = previousAutoInstall; }
            }
            Directory.CreateDirectory(AppConfig.DataDir);
            File.WriteAllText(Path.Combine(AppConfig.DataDir, "layout-checks.txt"), String.Join(Environment.NewLine, report) + Environment.NewLine);
            return 0;
        }

        private static void CheckSelectionRepairs(List<string> report,SynchronizationContext uiContext)
        {
            using(var form=new MainForm(new AppConfig {Libraries=new List<string>()}))
            {
                Field("firstLoad").SetValue(form,false); form.ShowInTaskbar=false; form.Opacity=0; form.Show();
                var package=IntegrationTests.ChoiceFixture(); Field("package").SetValue(form,package);
                var fill=typeof(MainForm).GetMethod("FillComponents",PrivateInstance);
                fill.Invoke(form,new object[]{new List<string>{"A","A/Child"}});
                var nodes=(Dictionary<string,OptionNode>)Field("componentNodes").GetValue(form);
                nodes["B"].Checked=true;
                Require(!nodes["A"].Checked && !nodes["A/Child"].Checked && nodes["B"].Checked,"Switching alternatives left old descendants selected.");
                nodes["A/Child"].Checked=true;
                var order=(List<string>)Field("selectedOrder").GetValue(form);
                Require(nodes["A"].Checked && nodes["A/Child"].Checked && !nodes["B"].Checked && !order.Contains("B"),"Selecting a child did not enforce ancestor exclusivity.");
                Require(Composer.Plan(package,order).CanInstall,"Repaired nested selection is not installable.");
                fill.Invoke(form,new object[]{new List<string>{"A","Removed option"}});
                SynchronizationContext.SetSynchronizationContext(uiContext);
                typeof(MainForm).GetMethod("BuildPlan",PrivateInstance).Invoke(form,new object[]{false});
                WaitForIdle(form,"missing saved option");
                var plan=(CompositionPlan)Field("plan").GetValue(form);
                Require(!plan.CanInstall && plan.Errors.Any(e=>e.Contains("Removed option")),"Missing restored option was silently discarded.");
                Require(!ControlField<Button>(form,"installButton").Enabled,"Missing restored option left install enabled.");
                nodes["A/Child"].Checked=true;
                Application.DoEvents();
                SynchronizationContext.SetSynchronizationContext(uiContext);
                typeof(MainForm).GetMethod("BuildPlan",PrivateInstance).Invoke(form,new object[]{false}); WaitForIdle(form,"edited missing selection");
                var editedPlan=(CompositionPlan)Field("plan").GetValue(form);
                Require(editedPlan!=null,"Edited plan missing; status="+ControlField<Label>(form,"statusText").Text+"; disposed="+form.IsDisposed+"; package="+(Field("package").GetValue(form)!=null));
                Require(!editedPlan.CanInstall,"Changing another option bypassed missing-option review.");
                fill.Invoke(form,new object[]{new List<string>{"A","A/Child"}});
                Require(order.SequenceEqual(new[]{"A","A/Child"}),"Valid restore changed selection precedence.");
                fill.Invoke(form,new object[]{new List<string>{"a","a/child"}});
                Require(order.SequenceEqual(new[]{"A","A/Child"}),"Saved IDs were not canonicalized after a source casing change.");
                nodes["A/Child"].Checked=false;
                Require(order.SequenceEqual(new[]{"A"}) && !Composer.Plan(package,order).Layers.Any(c=>c.Id=="A/Child"),"An unchecked restored option remains in the composition.");
                report.Add("PASS: case-insensitive restore retains canonical component IDs; unchecking removes the option from the actual composition.");
                report.Add("PASS: nested alternatives clear descendants and enforce ancestor exclusivity; missing saved options block installation until reviewed; valid restores preserve precedence.");
                var group=new ModComponent {Id="group",Name="Options",IsDummy=true};
                Func<string,string,ModComponent> choice=delegate(string id,string name) {return new ModComponent {Id=id,Name=name,ParentId="group",Assets=new List<NativeAsset>{new NativeAsset {DestinationPath="natives/stm/product/model/esf/esf032/001/00/esf032_001_00.mesh.230110883",EntryPath=id+"/mesh",Size=1}}};};
                var inferred=new ModPackage {Components=new List<ModComponent>{group,choice("main","Main files"),choice("red","Red"),choice("blue","Blue")}};
                Field("package").SetValue(form,inferred);fill.Invoke(form,new object[]{new List<string>{"main","red"}});
                nodes["blue"].Checked=true;
                Require(nodes["main"].Checked && nodes["blue"].Checked && !nodes["red"].Checked,"Inferred alternative switching removed Main files or retained the old alternative.");
                Require(Composer.Plan(inferred,order).CanInstall,"UI choice switching produced an invalid main+variant selection.");
                report.Add("PASS: an inferred choice keeps shared Main files checked while switching overlapping alternatives.");
                form.Close();
            }
        }

        private static void CheckCancellableWork(List<string> report,SynchronizationContext uiContext)
        {
            using(var form=new MainForm(new AppConfig {Libraries=new List<string>()}))
            {
                Field("firstLoad").SetValue(form,false);form.ShowInTaskbar=false;form.Opacity=0;form.Show();
                MethodInfo work=typeof(MainForm).GetMethod("Work",PrivateInstance).MakeGenericMethod(typeof(int));
                bool completed=false;
                Func<int> action=delegate {for(int i=0;i<200;i++) {OperationContext.ThrowIfCancellationRequested();OperationContext.Report("Fixture progress",i,200);Thread.Sleep(5);}return 1;};
                SynchronizationContext.SetSynchronizationContext(uiContext);
                work.Invoke(form,new object[]{"Fixture",action,new Action<int>(delegate {completed=true;})});
                var cancel=ControlField<Button>(form,"cancelWork");var progress=ControlField<ProgressBar>(form,"statusProgress");
                DateTime deadline=DateTime.UtcNow.AddSeconds(5);
                while(progress.Style!=ProgressBarStyle.Continuous) {Require(DateTime.UtcNow<deadline,"Progress did not reach the UI.");Application.DoEvents();Thread.Sleep(5);}
                Require(cancel.Visible && cancel.Enabled,"Cooperative operation has no Cancel control.");cancel.PerformClick();
                WaitForIdle(form,"cancellation fixture");
                Require(!completed && ControlField<Label>(form,"statusText").Text.StartsWith("Cancelled"),"Cancellation invoked success or did not return the UI to idle.");
                bool commitStarted=false;
                Func<int> commit=delegate {using(OperationContext.NonCancellable()) {commitStarted=true;Thread.Sleep(250);}return 2;};
                SynchronizationContext.SetSynchronizationContext(uiContext);
                work.Invoke(form,new object[]{"Commit fixture",commit,new Action<int>(delegate {completed=true;})});
                deadline=DateTime.UtcNow.AddSeconds(5);
                while(!commitStarted || cancel.Enabled) {Require(DateTime.UtcNow<deadline,"Commit cancellation was not disabled.");Application.DoEvents();Thread.Sleep(5);}
                cancel.PerformClick();WaitForIdle(form,"commit fixture");Require(completed,"Non-cancellable finish did not complete.");
                report.Add("PASS: operation progress reaches status UI; Cancel stops cooperative work without success callback; commit progress disables Cancel until completion.");
                form.Close();
            }
        }

        private static void CheckAtSize(AppConfig settings, ModPackage package, Size size, int minimumTreeHeight, int minimumTreeWidth, List<string> report)
        {
            using (MainForm form = new MainForm(settings))
            {
                // Shown normally scans the libraries. This check exercises layout and
                // source-archive selection without scanning or writing game folders.
                Field("firstLoad").SetValue(form, false);
                form.ShowInTaskbar = false;
                form.Opacity = 0;
                form.MinimumSize = Size.Empty;
                form.Show();
                Size logicalSize=size;
                size=new Size(DpiMetrics.Px(form,size.Width),DpiMetrics.Px(form,size.Height));
                minimumTreeHeight=DpiMetrics.Px(form,minimumTreeHeight); minimumTreeWidth=DpiMetrics.Px(form,minimumTreeWidth);
                // The native window cannot be larger than the available desktop.
                // Keep desktop-sized checks native; use the same fixed-size panel
                // host as screenshot capture for the larger design geometry.
                Control layoutHost = form;
                bool canvasGeometry = size.Width > Screen.PrimaryScreen.WorkingArea.Width || size.Height > Screen.PrimaryScreen.WorkingArea.Height;
                if (canvasGeometry)
                {
                    Panel canvas = new Panel { Size = size, BackColor = Palette.Background, ForeColor = Palette.Text };
                    Control[] children = form.Controls.Cast<Control>().ToArray();
                    foreach (Control child in children) form.Controls.Remove(child);
                    canvas.Controls.AddRange(children);
                    form.Controls.Add(canvas);
                    canvas.CreateControl();
                    layoutHost = canvas;
                }
                else form.ClientSize = size;
                if (package != null)
                {
                    Field("package").SetValue(form, package);
                    MethodInfo fill = typeof(MainForm).GetMethod("FillComponents", PrivateInstance);
                    if (fill == null) throw new InvalidOperationException("Layout check could not locate FillComponents.");
                    fill.Invoke(form, new object[] { Composer.DefaultSelection(package) });
                }
                OptionTree tree = ControlField<OptionTree>(form, "componentTree");
                tree.ExpandAll();
                LayoutChildren(layoutHost);
                Application.DoEvents();
                LayoutChildren(layoutHost);
                string context = logicalSize.Width + "x" + logicalSize.Height + " logical / " + size.Width + "x" + size.Height + " physical @ " + form.DeviceDpi + " DPI";
                Require(layoutHost.ClientSize == size, context + ": requested client size was not applied.");
                Require(tree.ClientSize.Height >= minimumTreeHeight, context + ": options height " + tree.ClientSize.Height + " is below " + minimumTreeHeight + ".");
                Require(tree.ClientSize.Width >= minimumTreeWidth, context + ": options width " + tree.ClientSize.Width + " is below " + minimumTreeWidth + ".");
                Require(tree.Font.SizeInPoints >= 10F, context + ": options font is below 10 points.");
                Require(tree.ItemHeight == DpiMetrics.Px(tree,32), context + ": options rows do not track their device DPI.");
                Require(tree.Visible, context + ": options tree is hidden.");
                foreach(var field in new[]{ControlField<PaddedTextField>(form,"search"),ControlField<PaddedTextField>(form,"outfitName")})
                {
                    TextBox native=field.Editor;
                    byte[] font=new byte[92];IntPtr handle=SendMessage(native.Handle,0x31,IntPtr.Zero,IntPtr.Zero);
                    Require(GetFontObject(handle,font.Length,font)>0 && Math.Abs(BitConverter.ToInt32(font,0))>=DpiMetrics.Px(native,12),context+": native input font retained a 96-DPI height.");
                    Require(field.Height>=DpiMetrics.Px(field,22),context+": native input field height is clipped.");
                }
                // 1.5 moved Open archive/Open folder from a library-page row into the header.
                Button[] sourceActions=AllControls(layoutHost).OfType<Button>().Where(b=>b.Text=="Open archive…" || b.Text=="Open folder…").ToArray();
                Require(sourceActions.Length==2,context+": Open archive/Open folder actions are missing.");
                foreach(Button action in sourceActions)
                {
                    Require(action.Visible && action.Parent.ClientRectangle.Contains(action.Bounds),context+": source action is clipped by its header area.");
                    RequireInside(action,layoutHost,context);
                    Require(!BoundsInHost(action,layoutHost).IntersectsWith(BoundsInHost(ControlField<Control>(form,"navSettings"),layoutHost)),context+": source action overlaps the navigation.");
                }
                Rectangle treeBounds = BoundsInHost(tree, layoutHost);
                RequireInside(tree, layoutHost, context);
                string[] inspectorFields = { "preview", "packageTitle", "packageMeta", "packageDescription", "targetCharacter", "outfitName" };   // 1.0.6: no destination line
                foreach (string name in inspectorFields)
                {
                    Control control = ControlField<Control>(form, name);
                    RequireInside(control, layoutHost, context);
                    Require(!treeBounds.IntersectsWith(BoundsInHost(control, layoutHost)), context + ": options overlap " + name + ".");
                }
                foreach (string name in new[] { "previewButton", "installButton", "libraryUninstallButton", "componentTab", "filesTab" })
                {
                    Control control = ControlField<Control>(form, name);
                    RequireInside(control, layoutHost, context);
                    Require(control.Visible, context + ": " + name + " is hidden.");
                }
                foreach (string name in new[] { "navLibrary", "navInstalled" })
                {
                    Control control = ControlField<Control>(form, name);
                    RequireInside(control, layoutHost, context);
                    Require(control.Visible && control.Enabled, context + ": " + name + " is unavailable.");
                }
                Control tools = ControlField<Control>(form, "navSettings");
                Require(tools != null && tools.Visible && tools.Enabled, context + ": settings/tools entry is unavailable.");
                RequireInside(tools, layoutHost, context);
                if (package != null)
                {
                    Require(tree.Nodes.Count > 0, context + ": archive options were not loaded.");
                    Require(AllNodes(tree.Nodes).Any(n => n.Checked), context + ": default main selection was not retained.");
                    Require(AllNodes(tree.Nodes).Where(n => n.Nodes.Count > 0).All(n => n.IsExpanded), context + ": an option group did not expand.");
                }
                CheckNavigation(form, layoutHost, settings, context);
                CheckLibraryUninstall(form, layoutHost, context, report);
                if (logicalSize.Width == 1232) CheckLibraryContextMenu(form, context, report);
                if (logicalSize.Width == 1232) CheckLibraryRailScroll(form, context, report);
                if (package != null) CheckOptionPainting(form, context, report);
                bool previewChecked = package != null && CheckHoverPreview(form, package, settings, context);
                report.Add("PASS: " + context + (canvasGeometry ? " canvas" : " native client") + " options " + tree.ClientSize.Width + "x" + tree.ClientSize.Height + "; " + tree.ItemHeight + "px rows; " + tree.Font.SizeInPoints.ToString("0.#") + "pt font; inspector and actions fit; Installed/Library and gear/Settings navigation works" + (previewChecked ? "; hover images/latest request preserve selections without busy state." : "."));
                form.Close();
            }
        }

        private static void CheckLibraryUninstall(MainForm form, Control layoutHost, string context, List<string> report)
        {
            FlatButton button = ControlField<FlatButton>(form, "libraryUninstallButton");
            Require(button.Visible && button.Text == "Uninstall outfit", context + ": main library uninstall button is missing.");
            foreach (string name in new[] { "libraryUninstallButton", "previewButton", "installButton" })
            {
                Control action = ControlField<Control>(form, name);
                RequireInside(action, layoutHost, context);
                Require(action.Parent.ClientRectangle.Contains(action.Bounds), context + ": footer action is clipped: " + action.Text + ".");
                foreach (string otherName in new[] { "libraryUninstallButton", "previewButton", "installButton" })
                    if (name != otherName) Require(!BoundsInHost(action, layoutHost).IntersectsWith(BoundsInHost(ControlField<Control>(form, otherName), layoutHost)), context + ": footer actions overlap.");
            }

            ListView list = ControlField<ListView>(form, "modList");
            ContextMenuStrip contextMenu = list.ContextMenuStrip;
            // 2.4.1: a separator and Delete (to the Recycle Bin) follow, last.
            Require(contextMenu != null && contextMenu.Items.Count == 9 && contextMenu.Items[2].Text == "Rename" && contextMenu.Items[3] is ToolStripSeparator && contextMenu.Items[5].Text == "Uninstall replacement" && contextMenu.Items[6].Text == "Uninstall mod (external manager)" && contextMenu.Items[7] is ToolStripSeparator && contextMenu.Items[8].Text.StartsWith("Delete", StringComparison.Ordinal), context + ": uninstall context action is missing.");
            ToolStripMenuItem contextAction = contextMenu.Items[4] as ToolStripMenuItem;
            Require(contextAction != null && contextAction.Text == "Uninstall outfit", context + ": uninstall context action has changed.");
            ModPackage oldPackage = (ModPackage)Field("package").GetValue(form);
            List<InstalledOutfit> oldInstalled = (List<InstalledOutfit>)Field("installed").GetValue(form);
            bool oldBusy = (bool)Field("busy").GetValue(form);
            bool oldSelecting = (bool)Field("selectingMod").GetValue(form);
            bool oldButtonEnabled = button.Enabled;
            object oldMenuTag = contextMenu.Tag, oldActionTag = contextAction.Tag;
            bool oldActionEnabled = contextAction.Enabled;
            ListViewItem[] oldRows = list.Items.Cast<ListViewItem>().ToArray();
            ListViewItem[] oldSelected = list.SelectedItems.Cast<ListViewItem>().ToArray();
            ListViewItem oldFocused = list.FocusedItem;
            ListViewItem oldTop = list.Items.Count == 0 ? null : list.TopItem;
            ToolStripItem[] oldSubmenu = contextAction.DropDownItems.Cast<ToolStripItem>().ToArray();
            contextAction.DropDownItems.Clear();
            MethodInfo update = typeof(MainForm).GetMethod("UpdateLibraryUninstall", PrivateInstance);
            MethodInfo populate = typeof(MainForm).GetMethod("PopulateLibraryUninstall", PrivateInstance);
            MethodInfo openChooser = typeof(MainForm).GetMethod("UninstallLibraryOutfit", PrivateInstance);
            string loadedSource = @"C:\fixture\loaded.zip", rowSource = @"C:\fixture\clicked.rar";
            InstalledOutfit active = new InstalledOutfit { Managed = true, Enabled = true, Character = "A.K.I", Name = "First outfit", SourcePath = loadedSource.ToUpperInvariant() };
            InstalledOutfit disabled = new InstalledOutfit { Managed = true, Enabled = false, Character = "Cammy", Name = "Second outfit", SourcePath = loadedSource };
            InstalledOutfit manual = new InstalledOutfit { Managed = false, Enabled = true, Character = "A.K.I", Name = "Manual outfit", SourcePath = loadedSource };
            InstalledOutfit rowOutfit = new InstalledOutfit { Managed = true, Enabled = true, Character = "Juri", Name = "Clicked row outfit", SourcePath = rowSource };
            try
            {
                Field("busy").SetValue(form, false); Field("selectingMod").SetValue(form, true);
                Field("installed").SetValue(form, new List<InstalledOutfit> { active, disabled, manual, rowOutfit });
                Field("package").SetValue(form, null); update.Invoke(form, null);
                Require(button.Visible && !button.Enabled, context + ": uninstall is enabled without a loaded package.");
                openChooser.Invoke(form, null);
                Field("package").SetValue(form, new ModPackage { SourcePath = " " }); update.Invoke(form, null);
                populate.Invoke(form, new object[] { contextAction, " " });
                Require(!button.Enabled && !contextAction.Enabled && contextAction.Tag == null && contextAction.DropDownItems.Count == 0, context + ": a missing source has an uninstall target.");
                Field("package").SetValue(form, new ModPackage { SourcePath = @"C:\fixture\uninstalled.zip" }); update.Invoke(form, null);
                populate.Invoke(form, new object[] { contextAction, @"C:\fixture\uninstalled.zip" });
                Require(!button.Enabled && !contextAction.Enabled && contextAction.Tag == null, context + ": an uninstalled source has an uninstall target.");
                Field("package").SetValue(form, new ModPackage { SourcePath = loadedSource });
                Field("installed").SetValue(form, new List<InstalledOutfit> { manual }); update.Invoke(form, null);
                populate.Invoke(form, new object[] { contextAction, loadedSource });
                Require(!button.Enabled && !contextAction.Enabled && contextAction.Tag == null && contextAction.DropDownItems.Count == 0, context + ": manual outfits are offered for uninstall.");
                Field("installed").SetValue(form, new List<InstalledOutfit> { active, manual }); update.Invoke(form, null);
                populate.Invoke(form, new object[] { contextAction, loadedSource });
                Require(button.Enabled && contextAction.Enabled && Object.ReferenceEquals(contextAction.Tag, active) && contextAction.DropDownItems.Count == 0, context + ": a sole managed outfit did not link case-insensitively.");
                Field("installed").SetValue(form, new List<InstalledOutfit> { disabled, manual }); update.Invoke(form, null);
                populate.Invoke(form, new object[] { contextAction, loadedSource });
                Require(button.Enabled && contextAction.Enabled && Object.ReferenceEquals(contextAction.Tag, disabled) && contextAction.DropDownItems.Count == 0, context + ": a disabled managed outfit cannot be uninstalled.");
                Field("installed").SetValue(form, new List<InstalledOutfit> { disabled, manual, rowOutfit, active }); update.Invoke(form, null);
                populate.Invoke(form, new object[] { contextAction, loadedSource });
                // 2.4.1: several outfits of one source are one action (no chooser): it uninstalls them all, with Undo.
                Require(button.Enabled && contextAction.Enabled && contextAction.DropDownItems.Count == 0 && contextAction.Text == "Uninstall outfits (2)", context + ": several outfits of one source are not one action: " + contextAction.Text + ", " + contextAction.DropDownItems.Count + " sub-items.");

                list.Items.Clear();
                LibraryEntry loadedEntry = new LibraryEntry { Name = "Loaded source", Kind = "ZIP", SourcePath = loadedSource };
                LibraryEntry clickedEntry = new LibraryEntry { Name = "Clicked source", Kind = "RAR", SourcePath = rowSource };
                ListViewItem loadedRow = new ListViewItem(loadedEntry.Name) { Tag = loadedEntry };
                ListViewItem clickedRow = new ListViewItem(clickedEntry.Name) { Tag = clickedEntry };
                list.Items.Add(loadedRow); list.Items.Add(clickedRow); loadedRow.Selected = true;
                Application.DoEvents();
                ShowLibraryRowMenu(list, clickedRow, context);
                Require(contextAction.Enabled && Object.ReferenceEquals(contextAction.Tag, rowOutfit) && contextAction.DropDownItems.Count == 0, context + ": right-click uninstall targeted the loaded source instead of the clicked row.");
                Require(loadedRow.Selected && !clickedRow.Selected && button.Enabled, context + ": right-clicking another source changed the loaded selection or footer action.");
                contextMenu.Close();
                ShowLibraryRowMenu(list, loadedRow, context);
                Require(contextAction.Enabled && contextAction.DropDownItems.Count == 0 && contextAction.Text == "Uninstall outfits (2)", context + ": the loaded row's context action is not one action for both outfits: " + contextAction.Text);
                contextMenu.Close();
                clickedEntry.SourcePath = @"C:\fixture\uninstalled.zip";
                ShowLibraryRowMenu(list, clickedRow, context);
                Require(!contextAction.Enabled && contextAction.Tag == null && contextAction.DropDownItems.Count == 0 && button.Enabled, context + ": uninstalled row borrowed the loaded source's uninstall target.");
                contextMenu.Close();
                clickedEntry.SourcePath = rowSource;
                Field("busy").SetValue(form, true); update.Invoke(form, null);
                Require(!button.Enabled, context + ": footer uninstall remains enabled while busy.");
                ShowLibraryRowMenu(list, clickedRow, context);
                Require(!contextAction.Enabled, context + ": context uninstall remains enabled while busy.");
                contextMenu.Close(); openChooser.Invoke(form, null);
                Require(((List<InstalledOutfit>)Field("installed").GetValue(form)).Count == 4, context + ": the footer uninstall ran while busy.");
                Field("busy").SetValue(form, false); update.Invoke(form, null);
                Require(button.Enabled, context + ": footer uninstall stayed disabled after work finished.");
                report.Add("PASS: " + context + " library uninstall footer fits; loaded package and right-click row target their own managed outfits; active, disabled, case-insensitive single outfits and several outfits as one action, missing/manual/uninstalled sources and busy gating verified without removing files.");
            }
            finally
            {
                contextMenu.Close();
                foreach (ToolStripItem item in contextAction.DropDownItems.Cast<ToolStripItem>().ToArray()) item.Dispose();
                contextAction.DropDownItems.Clear(); contextAction.DropDownItems.AddRange(oldSubmenu);
                contextMenu.Tag = oldMenuTag; contextAction.Tag = oldActionTag; contextAction.Enabled = oldActionEnabled;
                list.Items.Clear(); list.Items.AddRange(oldRows);
                foreach (ListViewItem row in oldSelected) row.Selected = true;
                if (oldFocused != null) oldFocused.Focused = true;
                if (oldTop != null) list.TopItem = oldTop;
                Field("package").SetValue(form, oldPackage); Field("installed").SetValue(form, oldInstalled);
                Field("busy").SetValue(form, oldBusy); Field("selectingMod").SetValue(form, oldSelecting);
                update.Invoke(form, null); button.Enabled = oldButtonEnabled;
            }
        }

        private static void ShowLibraryRowMenu(ListView list, ListViewItem row, string context)
        {
            Rectangle bounds = row.Bounds;
            Require(bounds.Height > 0 && list.ClientRectangle.Contains(bounds.Location), context + ": uninstall context test row is not visible.");
            Point location = new Point(bounds.Left + 8, bounds.Top + bounds.Height / 2);
            typeof(Control).GetMethod("OnMouseDown", PrivateInstance).Invoke(list, new object[] { new MouseEventArgs(MouseButtons.Right, 1, location.X, location.Y, 0) });
            list.ContextMenuStrip.Show(list, location);
            Require(list.ContextMenuStrip.Visible && Object.ReferenceEquals(list.ContextMenuStrip.Tag, row.Tag), context + ": uninstall menu did not target the right-click row.");
        }

        private static void CheckLibraryContextMenu(MainForm form, string context, List<string> report)
        {
            ListView list = ControlField<ListView>(form, "modList");
            ContextMenuStrip menu = list.ContextMenuStrip;
            Require(menu != null, context + ": library context menu is missing.");
            Require(menu.Items.Count == 9 && menu.Items[6].Text == "Uninstall mod (external manager)" && menu.Items[0].Text == "Open" && menu.Items[1].Text == "Open in Explorer" && menu.Items[2].Text == "Rename" && menu.Items[3] is ToolStripSeparator && menu.Items[4].Text == "Uninstall outfit" && menu.Items[5].Text == "Uninstall replacement"
                && menu.Items[7] is ToolStripSeparator && menu.Items[8].Text.StartsWith("Delete", StringComparison.Ordinal) && menu.Items[8].ForeColor == Palette.Red, context + ": library context actions are missing.");   // 2.4.1: Delete last, danger colour; 1.0.5: Rename after Open in Explorer
            LibraryEntry first = new LibraryEntry { Name = "First", Kind = "ZIP", SourcePath = @"C:\fixture\first.zip" };
            LibraryEntry second = new LibraryEntry { Name = "Second", Kind = "RAR", SourcePath = @"C:\fixture\second.rar" };
            list.Items.Add(new ListViewItem(first.Name) { Tag = first });
            list.Items.Add(new ListViewItem(second.Name) { Tag = second });
            Application.DoEvents();
            Rectangle row = list.Items[1].Bounds;
            Require(row.Height > 0 && list.ClientRectangle.Contains(row.Location), context + ": library context test row is not visible.");
            MethodInfo mouseDown = typeof(Control).GetMethod("OnMouseDown", PrivateInstance);
            mouseDown.Invoke(list, new object[] { new MouseEventArgs(MouseButtons.Right, 1, row.Left + 8, row.Top + row.Height / 2, 0) });
            Require(OpenedFrom(menu, delegate { menu.Show(list, new Point(row.Left + 8, row.Top + row.Height / 2)); }) == list && Object.ReferenceEquals(menu.Tag, second), context + ": right-click menu did not target the row under the pointer.");
            menu.Close();
            int emptyY = row.Bottom + 8;
            Require(emptyY < list.ClientSize.Height, context + ": library context test has no empty list area.");
            mouseDown.Invoke(list, new object[] { new MouseEventArgs(MouseButtons.Right, 1, row.Left + 8, emptyY, 0) });
            menu.Show(list, new Point(row.Left + 8, emptyY));
            Require(!menu.Visible, context + ": right-clicking empty library space opened a mod menu.");
            Require(list.SelectedItems.Count == 0, context + ": opening the library menu selected a different mod.");
            Field("library").SetValue(form, new List<LibraryEntry> { first, second });
            Field("selectingMod").SetValue(form, true);
            list.Items[1].Selected = true;
            ListViewItem retained = list.Items[1];
            typeof(MainForm).GetMethod("ApplyFilters", PrivateInstance).Invoke(form, null);
            Require(Object.ReferenceEquals(list.Items[1], retained) && retained.Selected, context + ": an unchanged filter rebuilt or deselected the library rows.");
            retained.Selected = false;
            Field("selectingMod").SetValue(form, false);
            PaddedTextField search = ControlField<PaddedTextField>(form, "search");
            Label clear = ControlField<Label>(form, "searchClear");
            search.Text = "Second";
            Require(clear.Visible && list.Items.Count == 1 && Object.ReferenceEquals(list.Items[0].Tag, second), context + ": search did not show its clear button and filter the list.");
            typeof(Control).GetMethod("OnClick", PrivateInstance).Invoke(clear, new object[] { EventArgs.Empty });
            Require(search.TextLength == 0 && !clear.Visible && list.Items.Count == 2, context + ": clear search did not restore the library list.");
            FlatButton filterButton = ControlField<FlatButton>(form, "filterButton");
            // 2.3.0: Filters opens a panel to the right of the list (the 2.2.0 popup over the list is gone).
            FilterPanel filterPanel = ControlField<FilterPanel>(form, "filterPanel");
            // 2.4.1: the header is the count row (26), the search (37) and the sort row (26: 20 px chips + Rhythm.Related),
            // so the list starts 89 px below the browser padding: 99 px, bound 106 (was 80 before the sort row).
            // 1.0.6: + the Deep search / Name · File row (MainForm.SearchOptionsHeight, 28): 127 px, bound 134.
            Require(!filterPanel.Visible && list.Top <= DpiMetrics.Px(list,106 + MainForm.SearchOptionsHeight), context + ": collapsed filters did not free library list space (list top " + list.Top + ").");
            typeof(Control).GetMethod("OnClick", PrivateInstance).Invoke(filterButton, new object[] { EventArgs.Empty });
            Application.DoEvents();
            Rectangle listScreen = list.RectangleToScreen(list.ClientRectangle), panelScreen = filterPanel.RectangleToScreen(filterPanel.ClientRectangle);
            Require(filterPanel.Visible && filterPanel.Grid.Visible && filterPanel.Kinds.Visible && list.Visible && panelScreen.Left >= listScreen.Right, context + ": Filters button did not show the panel beside the list: panel " + panelScreen + " list " + listScreen);
            typeof(Control).GetMethod("OnClick", PrivateInstance).Invoke(filterButton, new object[] { EventArgs.Empty });
            Require(!filterPanel.Visible, context + ": Filters button did not close the panel.");
            first.Character = "A.K.I"; first.OptionCount = 4; first.Name = "First Barefoot";
            second.Character = "Cammy"; second.OptionCount = 5;
            LibraryEntry third = new LibraryEntry { Name = "Third", Character = "A.K.I", Kind = "ZIP", OptionCount = 5, SourcePath = @"C:\fixture\third-barefoot.zip" };
            LibraryEntry fourth = new LibraryEntry { Name = "Fourth Barefoot", Character = "Juri", Kind = "Folder", OptionCount = 1, SourcePath = @"C:\fixture\fourth" };
            Field("library").SetValue(form, new List<LibraryEntry> { first, second, third, fourth });
            typeof(MainForm).GetMethod("ApplyFilters", PrivateInstance).Invoke(form, null);
            search.Text = "aki, cammy";
            Require(list.Items.Count == 3 && list.Items.Cast<ListViewItem>().All(item => !Object.ReferenceEquals(item.Tag, fourth)), context + ": comma-separated character names were not ORed.");
            search.Text = "aki, barefoot";
            Require(list.Items.Count == 2 && list.Items.Cast<ListViewItem>().All(item => Object.ReferenceEquals(item.Tag, first) || Object.ReferenceEquals(item.Tag, third)), context + ": character and display/filename terms were not combined.");
            search.Text = "is:bundle";
            Require(list.Items.Count == 2 && list.Items.Cast<ListViewItem>().All(item => ((LibraryEntry)item.Tag).OptionCount == 5), context + ": is:bundle did not restrict search results.");
            search.Clear();
            // 2.3.0: the mod type and format dropdowns are gone; their search tokens remain.
            search.Text = "is:normal";
            Require(list.Items.Count == 2 && list.Items.Cast<ListViewItem>().All(item => ((LibraryEntry)item.Tag).OptionCount <= 4), context + ": is:normal missed the four-option boundary.");
            search.Text = "format:rar";
            Require(list.Items.Count == 1 && Object.ReferenceEquals(list.Items[0].Tag, second), context + ": format:rar missed the archive.");
            search.Clear();
            search.Text = "format:zip";
            Require(list.Items.Count == 2 && list.Items.Cast<ListViewItem>().All(item => ((LibraryEntry)item.Tag).Kind == "ZIP"), context + ": format:zip search token was not applied.");
            search.Clear();
            Field("installed").SetValue(form, new List<InstalledOutfit> {
                new InstalledOutfit { Managed = true, Enabled = true, SourcePath = first.SourcePath, SelectedIds = new List<string> { "Main" } },
                new InstalledOutfit { Managed = true, Enabled = false, SourcePath = second.SourcePath, SelectedIds = new List<string> { "Main" } }
            });
            filterPanel.SetInstalled(true);
            Require(list.Items.Count == 2 && list.Items.Cast<ListViewItem>().All(item => Object.ReferenceEquals(item.Tag, first) || Object.ReferenceEquals(item.Tag, second)), context + ": Installed tile missed active or disabled linked sources.");
            Require(filterButton.Text.Contains("1"), context + ": Installed tile was not counted as an active filter.");
            search.Text = "aki, barefoot";
            Require(list.Items.Count == 1 && Object.ReferenceEquals(list.Items[0].Tag, first), context + ": Installed tile did not combine with comma search.");
            search.Clear(); filterPanel.SetInstalled(false);
            Field("installed").SetValue(form, new List<InstalledOutfit>());
            // 2.0.3: tool-mod terms, is:tool and the Filters "Tool mod" type; a costume mod with REFramework data is not a tool mod.
            LibraryEntry plugin = new LibraryEntry { Name = "Frame Meter", Kind = "ZIP", OptionCount = 0, SourcePath = @"C:\fixture\frame-meter.zip", ToolKinds = new List<string> { "plugin" } };
            LibraryEntry colors = new LibraryEntry { Name = "Cammy Colors", Character = "Cammy", Kind = "ZIP", OptionCount = 1, SourcePath = @"C:\fixture\cammy-colors.zip", ToolKinds = new List<string> { "reframework" }, CostumeKeys = new List<string> { "009/001" } };
            Field("library").SetValue(form, new List<LibraryEntry> { first, second, third, fourth, plugin, colors });
            typeof(MainForm).GetMethod("ApplyFilters", PrivateInstance).Invoke(form, null);
            foreach (string text in new[] { "reframework", "code", "Tool mods", "plugin", "dll", "is:tool", "is:tool, frame" })
            {
                search.Text = text;
                Require(list.Items.Count == 1 && Object.ReferenceEquals(list.Items[0].Tag, plugin), context + ": '" + text + "' did not list only the tool mod (" + list.Items.Count + ").");
            }
            search.Text = "lua";
            Require(list.Items.Count == 0, context + ": 'lua' listed a plugin-only mod.");
            search.Clear();
            filterPanel.Kinds.Activate(0, false);
            Require(list.Items.Count == 1 && Object.ReferenceEquals(list.Items[0].Tag, plugin) && filterButton.Text.Contains("1"), context + ": the Code tools tile did not restrict the library.");
            filterPanel.Kinds.Activate(0, false);
            Require(MainForm.LibraryRowLabel(plugin, null) == "REFramework plugin" && MainForm.LibraryRowLabel(colors, null) == "C1", context + ": library row labels for tool/costume mods are wrong.");
            Require(UiAudit.ToolTipFindings(form).Count == 0 && search.PlaceholderText == "Search or is:bundle", context + ": a tooltip is set (2.4.0: none) or the search placeholder changed: " + String.Join("; ", UiAudit.ToolTipFindings(form)));
            // 2.5.9: Discord, bug, coffee at the right end of the status bar; each has its hover; Discord and coffee open their page, the bug is hover only.
            // The hover text is also each icon's accessible name (set together in AddSupportIcon); no tooltip call here (ui-polish scans for them).
            var icons = (List<IconLink>)Field("supportIcons").GetValue(form);
            Panel statusBar = (Panel)Field("statusPanel").GetValue(form);
            Require(icons.Count == 3 && icons.Select(i => i.Name).SequenceEqual(new[] { "discordLink", "bugHint", "coffeeLink" }) && icons[0].Left < icons[1].Left && icons[1].Left < icons[2].Left, context + ": support icons missing or out of order.");
            Require(Field("supportTip").GetValue(form) != null && icons[0].AccessibleName == MainForm.DiscordHint && icons[1].AccessibleName == "Found a bug? Report it on Discord or Nexus" && icons[2].AccessibleName == "Love it? Support me", context + ": support icon hovers wrong.");
            Rectangle hostBounds = icons[2].Parent.Bounds;
            Require(icons.All(i => i.Visible && i.Width > 0 && i.Parent.ClientRectangle.Contains(i.Bounds)) && hostBounds.Right == statusBar.ClientSize.Width - statusBar.Padding.Right, context + ": support icons not at the status bar's right end: " + hostBounds + " in " + statusBar.ClientSize);
            var opened = new List<string>(); Func<string, bool> previousHook = MainForm.OpenUrlHook; MainForm.OpenUrlHook = delegate(string url) { opened.Add(url); return true; };
            try { foreach (IconLink icon in icons) typeof(Control).GetMethod("OnClick", PrivateInstance).Invoke(icon, new object[] { EventArgs.Empty }); }
            finally { MainForm.OpenUrlHook = previousHook; }
            Require(opened.SequenceEqual(new[] { "https://discord.gg/9Vr2SJ3", "https://www.patreon.com/ZZtai/posts/buy-me-coffee-167011360" }) && icons[1].Cursor == Cursors.Default && icons[2].Cursor == Cursors.Hand, context + ": support icon clicks: " + String.Join(", ", opened));
            Field("library").SetValue(form, new List<LibraryEntry> { first, second, third, fourth });
            typeof(MainForm).GetMethod("ApplyFilters", PrivateInstance).Invoke(form, null);
            MethodInfo setBusy = typeof(MainForm).GetMethod("SetBusy", PrivateInstance);
            setBusy.Invoke(form, new object[] { true, "Checking library painting" });
            Require(list.Enabled && ((BufferedModList)list).InputLocked, context + ": package loading disabled and repainted the library list.");
            setBusy.Invoke(form, new object[] { false, "Ready" });
            Require(list.Enabled && !((BufferedModList)list).InputLocked, context + ": library input stayed locked after loading.");
            report.Add("PASS: library menu, stable rows, search clear, Filters panel beside the list, Installed tile, comma character/name search, is:normal and format: tokens, tool-mod search terms and the Code tools tile, and busy painting.");
        }

        private static void CheckLibraryRailScroll(MainForm form, string context, List<string> report)
        {
            ListView list = ControlField<ListView>(form, "modList");
            List<LibraryEntry> oldLibrary = (List<LibraryEntry>)Field("library").GetValue(form);
            List<InstalledOutfit> oldInstalled = (List<InstalledOutfit>)Field("installed").GetValue(form);
            List<LibraryEntry> entries = Enumerable.Range(0, 20).Select(i => new LibraryEntry { Name = "Rail row " + i.ToString("00"), Kind = "ZIP", Character = "A.K.I", SourcePath = @"C:\fixture\rail-" + i.ToString("00") + ".zip", OptionCount = 1, OptionIds = new List<string> { "Main" } }).ToList();
            Field("library").SetValue(form, entries);
            Field("installed").SetValue(form, new List<InstalledOutfit> { new InstalledOutfit { Managed = true, Enabled = true, SourcePath = entries[3].SourcePath, SelectedIds = new List<string> { "Main" } } });
            Field("selectingMod").SetValue(form, true);
            try
            {
                typeof(MainForm).GetMethod("ApplyFilters", PrivateInstance).Invoke(form, null);
                Application.DoEvents();
                Require(list.Items.Count == entries.Count, context + ": library rail fixture rows were filtered unexpectedly.");
                Require((GetWindowLong(list.Handle,-16) & 0x00100000)==0,context+": populated library has an unnecessary horizontal scrollbar; column="+list.Columns[0].Width+" client="+list.ClientSize+" control="+list.Size+".");
                ListViewItem firstVisible = list.TopItem;
                bool fullInvalidation = false;
                InvalidateEventHandler onInvalidated = delegate(object sender, InvalidateEventArgs e) { if (e.InvalidRect.Width >= list.ClientSize.Width - 1 && e.InvalidRect.Height >= list.ClientSize.Height - 1) fullInvalidation = true; };
                list.Invalidated += onInvalidated;
                try { SendMessage(list.Handle, 0x115, new IntPtr(1), IntPtr.Zero); Application.DoEvents(); }
                finally { list.Invalidated -= onInvalidated; }
                Require(list.TopItem != firstVisible && fullInvalidation, context + ": library scroll did not redraw the whole owner-drawn row surface.");
                // 2.4.1: the installed row leads the list; scroll back to the top to see it beside a plain row.
                Require(Object.ReferenceEquals(list.Items[0].Tag, entries[3]), context + ": the installed row is not first.");
                SendMessage(list.Handle, 0x115, new IntPtr(6), IntPtr.Zero); Application.DoEvents();   // SB_TOP
                ListViewItem gray = list.Items.Cast<ListViewItem>().Single(item => Object.ReferenceEquals(item.Tag, entries[2]));
                ListViewItem green = list.Items.Cast<ListViewItem>().Single(item => Object.ReferenceEquals(item.Tag, entries[3]));
                Require(list.ClientRectangle.IntersectsWith(gray.Bounds) && list.ClientRectangle.IntersectsWith(green.Bounds), context + ": rail colors are not in the scroll viewport.");
                gray.Selected = true;
                Require(((BufferedModList)list).SuppressIdleHover && !list.HotTracking && !list.HoverSelection && !list.ShowItemToolTips, context + ": library hover is not fully disabled.");
                Require(!((BufferedModList)ControlField<ListView>(form,"installedList")).SuppressIdleHover && !((BufferedModList)ControlField<ListView>(form,"fileList")).SuppressIdleHover, context + ": library hover policy leaked into other lists.");
                int moves=0,hovers=0;
                MouseEventHandler onMove=delegate {moves++;}; EventHandler onHover=delegate {hovers++;};
                list.MouseMove+=onMove; list.MouseHover+=onHover;
                try
                {
                    foreach(var row in new[]{gray,green,gray,green})
                    {
                        IntPtr point=new IntPtr(((row.Bounds.Top+row.Bounds.Height/2)<<16)|24);
                        SendMessage(list.Handle,0x200,IntPtr.Zero,point);
                        SendMessage(list.Handle,0x2A1,IntPtr.Zero,point);
                        SendMessage(list.Handle,0x2A3,IntPtr.Zero,IntPtr.Zero);
                        list.Update();
                    }
                    Require(moves==0 && hovers==0 && gray.Selected && !green.Selected,context+": idle hover reached the list or changed selection.");
                    SendMessage(list.Handle,0x200,new IntPtr(1),new IntPtr(((gray.Bounds.Top+20)<<16)|24));
                    Require(moves>0,context+": button-held mouse movement was blocked.");
                }
                finally {list.MouseMove-=onMove; list.MouseHover-=onHover;}
                using (Bitmap bitmap = new Bitmap(list.Width, list.Height))
                {
                    list.DrawToBitmap(bitmap, list.ClientRectangle);
                    Require(bitmap.GetPixel(gray.Bounds.Left + DpiMetrics.Px(list,2), gray.Bounds.Top + gray.Bounds.Height / 2).ToArgb() == Palette.Border.ToArgb(), context + ": uninstalled row lost its baked-in gray rail after scrolling.");
                    Require(bitmap.GetPixel(green.Bounds.Left + DpiMetrics.Px(list,2), green.Bounds.Top + green.Bounds.Height / 2).ToArgb() == Palette.Green.ToArgb(), context + ": installed row lost its green rail after scrolling.");
                }
                // Exercise actual button/key messages while preventing fixture
                // selection from trying to open a nonexistent source archive.
                Field("busy").SetValue(form,true);
                CancelEventHandler suppressPopup=delegate(object sender,CancelEventArgs e) {e.Cancel=true;};
                list.ContextMenuStrip.Opening+=suppressPopup;
                try
                {
                    IntPtr point=new IntPtr(((green.Bounds.Top+20)<<16)|24);
                    PostMessage(list.Handle,0x201,new IntPtr(1),point); PostMessage(list.Handle,0x202,IntPtr.Zero,point); Application.DoEvents();
                    Require(green.Selected,context+": native left click stopped selecting rows after hover suppression.");
                    PostMessage(list.Handle,0x204,new IntPtr(2),point); PostMessage(list.Handle,0x205,IntPtr.Zero,point); Application.DoEvents();
                    Require(Object.ReferenceEquals(list.ContextMenuStrip.Tag,green.Tag),context+": native right click lost the source-menu target.");
                    list.Focus(); int selectedIndex=green.Index;
                    SendMessage(list.Handle,0x100,new IntPtr((int)Keys.Down),IntPtr.Zero); SendMessage(list.Handle,0x101,new IntPtr((int)Keys.Down),IntPtr.Zero);
                    Require(list.SelectedIndices.Count==1 && list.SelectedIndices[0]==selectedIndex+1,context+": keyboard navigation stopped working after hover suppression.");
                }
                finally {list.ContextMenuStrip.Opening-=suppressPopup; Field("busy").SetValue(form,false);}
                gray.Selected = false;
                fullInvalidation = false;
                list.Invalidated += onInvalidated;
                try { SendMessage(list.Handle, 0x20A, new IntPtr(unchecked(-120 << 16)), IntPtr.Zero); Application.DoEvents(); }
                finally { list.Invalidated -= onInvalidated; }
                Require(fullInvalidation, context + ": library mouse-wheel scroll did not redraw the rails.");
                report.Add("PASS: " + context + " library scrollbar and wheel redraw retain gray default and green installed row rails.");
                report.Add("PASS: " + context + " native idle move/hover/leave suppressed only in library; selected gray and installed green rails render; button movement, native left/right clicks and keyboard navigation preserved.");
            }
            finally
            {
                Field("library").SetValue(form, oldLibrary); Field("installed").SetValue(form, oldInstalled);
                typeof(MainForm).GetMethod("ApplyFilters", PrivateInstance).Invoke(form, null);
                Field("selectingMod").SetValue(form, false);
            }
        }

        private static void CheckOptionPainting(MainForm form, string context, List<string> report)
        {
            OptionTree tree = ControlField<OptionTree>(form, "componentTree");
            Require(!tree.CheckBoxes, context + ": native checkboxes must not overlay custom indicators.");
            CheckFullRowActivation(form,context,report);
            List<string> order = new List<string>((List<string>)Field("selectedOrder").GetValue(form));
            OptionNode option = AllNodes(tree.Nodes).First(n => !((ModComponent)n.Tag).IsDummy);
            option.EnsureVisible();
            bool before = option.Checked;
            typeof(Control).GetMethod("OnMouseDown", PrivateInstance).Invoke(tree, new object[] { new MouseEventArgs(MouseButtons.Left, 1, option.Bounds.X + 7, option.Bounds.Y + 16, 0) });
            Require(option.Checked != before, context + ": clicking custom indicator did not toggle exactly once.");
            tree.SelectedNode = option;
            // 2.4.1: Enter toggles the focused option (Space installs the selection, handled by the window).
            typeof(Control).GetMethod("OnKeyDown", PrivateInstance).Invoke(tree, new object[] { new KeyEventArgs(Keys.Enter) });
            Require(option.Checked == before, context + ": Enter did not toggle custom indicator.");
            typeof(MainForm).GetMethod("FillComponents", PrivateInstance).Invoke(form, new object[] { order });
            tree.ExpandAll(); tree.TopNode = tree.Nodes[0]; tree.SelectedNode = null;
            OptionNode checkedOption=AllNodes(tree.Nodes).First(n=>n.Checked);
            checkedOption.EnsureVisible();
            string[] checkedBefore=AllNodes(tree.Nodes).Where(n=>n.Checked).Select(n=>((ModComponent)n.Tag).Id).ToArray();
            // Each native input message is followed immediately by a captured
            // managed frame, rather than only testing the eventual end state.
            // OptionTree has one buffered paint path and no SysTreeView32 HWND.
            for(int frame=0;frame<24;frame++)
            {
                Rectangle row=checkedOption.Bounds;
                int x=row.X+DpiMetrics.Px(tree,frame%2==0 ? 8 : 45), y=row.Y+row.Height/2;
                SendMessage(tree.Handle,0x200,IntPtr.Zero,new IntPtr((y<<16)|(x & 0xffff)));
                if(frame%3==0) SendMessage(tree.Handle,0x2A3,IntPtr.Zero,IntPtr.Zero);
                tree.Update();
                using(var frameImage=new Bitmap(tree.Width,tree.Height))
                {
                    tree.DrawToBitmap(frameImage,tree.ClientRectangle);
                    Require(frameImage.GetPixel(row.X+DpiMetrics.Px(tree,7),row.Y+DpiMetrics.Px(tree,15)).ToArgb()==Palette.Accent.ToArgb(),context+": orange indicator vanished in native hover frame "+frame);
                    if(frame==23) {Directory.CreateDirectory(AppConfig.DataDir);frameImage.Save(Path.Combine(AppConfig.DataDir,"option-frame-"+tree.DeviceDpi+"-"+tree.Width+"x"+tree.Height+".png"),ImageFormat.Png);}
                }
            }
            Require(AllNodes(tree.Nodes).Where(n=>n.Checked).Select(n=>((ModComponent)n.Tag).Id).SequenceEqual(checkedBefore),context+": native hover changed checked state.");
            tree.SelectedNode=checkedOption;tree.Focus();
            AccessibleObject accessible=tree.AccessibilityObject.GetSelected();
            Require(accessible!=null && accessible.Name==checkedOption.Text && (accessible.State & AccessibleStates.Checked)!=0,context+": selected checked option is not exposed to accessibility.");
            SendMessage(tree.Handle,0x100,new IntPtr((int)Keys.Home),IntPtr.Zero);
            Require(tree.SelectedNode==tree.VisibleNodes[0],context+": Home did not navigate the managed option tree.");
            SendMessage(tree.Handle,0x100,new IntPtr((int)Keys.Down),IntPtr.Zero);
            Require(tree.VisibleNodes.Count<2 || tree.SelectedNode==tree.VisibleNodes[1],context+": Down did not navigate the managed option tree.");
            SendMessage(tree.Handle,0x100,new IntPtr((int)Keys.End),IntPtr.Zero);
            Require(tree.SelectedNode==tree.VisibleNodes[tree.VisibleNodes.Count-1] && tree.ClientRectangle.IntersectsWith(tree.SelectedNode.Bounds),context+": End did not scroll the final option into view.");
            tree.SelectedNode=null;tree.TopNode=tree.Nodes[0];
            if(tree.VisibleNodes.Count*tree.ItemHeight>tree.ClientSize.Height)
            {
                int scrollBefore=tree.AutoScrollPosition.Y;
                SendMessage(tree.Handle,0x20A,new IntPtr(unchecked(-120<<16)),IntPtr.Zero);
                Require(tree.AutoScrollPosition.Y<scrollBefore,context+": native wheel input did not scroll option rows.");
                tree.TopNode=tree.Nodes[0];
            }
            MethodInfo move = typeof(Control).GetMethod("OnMouseMove", PrivateInstance);
            MethodInfo leave = typeof(Control).GetMethod("OnMouseLeave", PrivateInstance);
            leave.Invoke(tree, new object[] { EventArgs.Empty });
            using (Bitmap baseline = new Bitmap(tree.Width, tree.Height))
            using (Bitmap after = new Bitmap(tree.Width, tree.Height))
            {
                tree.DrawToBitmap(baseline, tree.ClientRectangle);
                foreach (OptionNode node in AllNodes(tree.Nodes).ToArray())
                {
                    node.EnsureVisible();
                    move.Invoke(tree, new object[] { new MouseEventArgs(MouseButtons.None, 0, node.Bounds.X + 25, node.Bounds.Y + 16, 0) });
                    tree.Update();
                }
                leave.Invoke(tree, new object[] { EventArgs.Empty });
                tree.TopNode = tree.Nodes[0]; tree.Update();
                tree.DrawToBitmap(after, tree.ClientRectangle);
                Require(ImageFingerprint(baseline) == ImageFingerprint(after), context + ": rendered rows changed after hover/scroll roundtrip.");
            }
            Require(((List<string>)Field("selectedOrder").GetValue(form)).SequenceEqual(order), context + ": hover/scroll changed composition order.");
            // Mouse/keyboard selection can load previews. Finish those requests
            // before the following test deliberately exercises uncached reads.
            DateTime deadline = DateTime.UtcNow.AddSeconds(10);
            while ((bool)Field("previewLoading").GetValue(form) || Field("pendingPreviewId").GetValue(form) != null)
            {
                Require(DateTime.UtcNow < deadline, context + ": indicator preview did not settle.");
                Application.DoEvents(); Thread.Sleep(10);
            }
            ((PreviewImageCache)Field("previewCache").GetValue(form)).Dispose();
            report.Add("PASS: " + context + " managed full-row buffer; 24 immediate native hover/leave frames retain orange indicator; mouse/Space toggles, Home/Down/End navigation and checked accessibility state; hover/scroll roundtrip bitmap stable. Saved images are hidden-control renderings, not desktop captures.");
        }

        private static void ClickOption(OptionTree tree, Point point, int downMessage)
        {
            IntPtr location=new IntPtr((point.Y<<16)|(point.X & 0xffff));
            SendMessage(tree.Handle,downMessage,new IntPtr(1),location);
            SendMessage(tree.Handle,0x202,IntPtr.Zero,location);
        }

        private static void CheckFullRowActivation(MainForm form,string context,List<string> report)
        {
            var tree=ControlField<OptionTree>(form,"componentTree");
            var saved=new List<string>((List<string>)Field("selectedOrder").GetValue(form));
            var package=(ModPackage)Field("package").GetValue(form);
            try
            {
                OptionNode option=AllNodes(tree.Nodes).First(n=>!((ModComponent)n.Tag).IsDummy);
                option.EnsureVisible();
                foreach(int x in new[]{DpiMetrics.Px(tree,2),option.Bounds.X+DpiMetrics.Px(tree,7),option.Bounds.X+DpiMetrics.Px(tree,44),tree.ClientSize.Width-DpiMetrics.Px(tree,3)})
                {
                    bool before=option.Checked;
                    Point point=new Point(x,option.Bounds.Y+tree.ItemHeight/2);
                    ClickOption(tree,point,0x201);
                    Require(option.Checked!=before && tree.SelectedNode==option,context+": full-row click did not toggle exactly once at x="+x);
                    ClickOption(tree,point,0x203);
                    Require(option.Checked!=before,context+": double-click second press toggled the option back.");
                    ClickOption(tree,point,0x201);
                    Require(option.Checked==before,context+": repeated full-row click did not restore the checkbox.");
                }
                var group=AllNodes(tree.Nodes).FirstOrDefault(n=>n.Nodes.Count>=2 && n.Nodes.Count(c=>package.IsChoiceBranch((ModComponent)n.Tag,(ModComponent)c.Tag))>=2);
                if(group!=null)
                {
                    var choices=group.Nodes.Where(n=>package.IsChoiceBranch((ModComponent)group.Tag,(ModComponent)n.Tag)).Take(2).ToArray();
                    foreach(var choice in choices)
                    {
                        choice.EnsureVisible();
                        if(choice.Checked) choice.Checked=false;
                        ClickOption(tree,new Point(tree.ClientSize.Width-DpiMetrics.Px(tree,3),choice.Bounds.Y+tree.ItemHeight/2),0x201);
                    }
                    Require(!choices[0].Checked && choices[1].Checked,context+": full-row radio choice bypassed sibling exclusivity.");
                    string[] before=AllNodes(tree.Nodes).Where(n=>n.Checked).Select(n=>((ModComponent)n.Tag).Id).ToArray();
                    group.EnsureVisible(); bool expanded=group.IsExpanded;
                    ClickOption(tree,new Point(group.Bounds.X-DpiMetrics.Px(tree,10),group.Bounds.Y+tree.ItemHeight/2),0x201);
                    Require(group.IsExpanded!=expanded && !group.Checked,context+": disclosure arrow did not remain an expansion-only action.");
                    Require(AllNodes(tree.Nodes).Where(n=>n.Checked).Select(n=>((ModComponent)n.Tag).Id).SequenceEqual(before),context+": expanding a category changed checked options.");
                    group.EnsureVisible();
                    ClickOption(tree,new Point(tree.ClientSize.Width-DpiMetrics.Px(tree,3),group.Bounds.Y+tree.ItemHeight/2),0x201);
                    Require(!group.Checked,context+": category text became a checked option.");
                }
                report.Add("PASS: "+context+" native full-row clicks (padding, marker, text and far edge) toggle once; double-click does not undo the choice; radio exclusivity and separate disclosure arrows remain intact.");
            }
            finally
            {
                typeof(MainForm).GetMethod("FillComponents",PrivateInstance).Invoke(form,new object[]{saved});
                tree.ExpandAll(); tree.TopNode=tree.Nodes[0]; tree.SelectedNode=null;
            }
        }

        private static bool CheckHoverPreview(MainForm form, ModPackage package, AppConfig settings, string context)
        {
            List<ModComponent> pictured = package.Components.Where(c => !c.IsDummy && !String.IsNullOrEmpty(c.PreviewEntry)).Take(4).ToList();
            if (pictured.Count < 4) return false;
            OptionTree tree = ControlField<OptionTree>(form, "componentTree");
            PictureBox preview = ControlField<PictureBox>(form, "preview");
            Label title = ControlField<Label>(form, "componentTitle");
            string[] checkedIds = AllNodes(tree.Nodes).Where(n => n.Checked).Select(n => ((ModComponent)n.Tag).Id).ToArray();
            string[] order = ((List<string>)Field("selectedOrder").GetValue(form)).ToArray();
            OptionNode selected = tree.SelectedNode;
            Dictionary<string, OptionNode> nodes = AllNodes(tree.Nodes).ToDictionary(n => ((ModComponent)n.Tag).Id, StringComparer.OrdinalIgnoreCase);

            // Raise the native control's mouse event on a visible row so the
            // hit-test, debounce timer and shared hover path are all exercised.
            MethodInfo leave = typeof(Control).GetMethod("OnMouseLeave", PrivateInstance);
            MethodInfo move = typeof(Control).GetMethod("OnMouseMove", PrivateInstance);
            leave.Invoke(tree, new object[] { EventArgs.Empty });
            OptionNode hovered = nodes[pictured[0].Id];
            hovered.EnsureVisible();
            Application.DoEvents();
            Rectangle row = hovered.Bounds;
            Require(row.Height > 0 && tree.ClientRectangle.IntersectsWith(row), context + ": hover test row is not visible.");
            move.Invoke(tree, new object[] { new MouseEventArgs(MouseButtons.None, 0, row.Left + 4, row.Top + row.Height / 2, 0) });
            System.Windows.Forms.Timer hoverTimer = (System.Windows.Forms.Timer)Field("hoverTimer").GetValue(form);
            Require(hoverTimer.Enabled && hoverTimer.Interval >= 150, context + ": mouse movement did not start the hover debounce timer.");
            RequirePreviewInteractive(form, context);
            WaitForPreview(form, pictured[0].Id, context);
            VerifyPreview(form, pictured[0], package, settings, preview, title, context);
            string firstImage = ImageFingerprint(preview.Image);

            // A second request is in flight when the third is queued. The final
            // picture must belong to the newest hover, regardless of completion.
            form.PreviewOption(nodes[pictured[1].Id]);
            form.PreviewOption(nodes[pictured[2].Id]);
            // Fast cached reads can finish between these requests. In that case
            // the newest option is already displayed instead of pending.
            Require((string)Field("inspectedComponentId").GetValue(form) == pictured[2].Id, context + ": rapid hover did not retain the newest inspected option.");
            RequirePreviewInteractive(form, context);
            WaitForPreview(form, pictured[2].Id, context);
            VerifyPreview(form, pictured[2], package, settings, preview, title, context);
            Require(firstImage != ImageFingerprint(preview.Image), context + ": hovering a different pictured option did not change the image.");

            // Returning to a cached option while another image is still loading
            // must also supersede that load, rather than letting it overwrite it.
            form.PreviewOption(nodes[pictured[3].Id]);
            Require((bool)Field("previewLoading").GetValue(form), context + ": uncached hover did not start a read.");
            form.PreviewOption(nodes[pictured[0].Id]);
            Require((string)Field("previewComponentId").GetValue(form) == pictured[0].Id && preview.Image != null && Field("pendingPreviewId").GetValue(form) == null, context + ": decoded cache hit waited for the old preview worker.");
            RequirePreviewInteractive(form, context);
            WaitForPreview(form, pictured[0].Id, context);
            VerifyPreview(form, pictured[0], package, settings, preview, title, context);
            Require(AllNodes(tree.Nodes).Where(n => n.Checked).Select(n => ((ModComponent)n.Tag).Id).SequenceEqual(checkedIds), context + ": hovering changed checked components.");
            Require(((List<string>)Field("selectedOrder").GetValue(form)).SequenceEqual(order), context + ": hovering changed component precedence.");
            Require(Object.ReferenceEquals(tree.SelectedNode, selected), context + ": hovering changed the selected tree row.");
            return true;
        }

        private static void RequirePreviewInteractive(MainForm form, string context)
        {
            Require(!(bool)Field("busy").GetValue(form), context + ": hover preview put the application into its busy state.");
            foreach (string name in new[] { "componentTree", "modList", "targetCharacter", "outfitName" })
                Require(ControlField<Control>(form, name).Enabled, context + ": hover preview disabled " + name + ".");
        }

        private static void WaitForPreview(MainForm form, string id, string context)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(10);
            while ((bool)Field("previewLoading").GetValue(form) || Field("pendingPreviewId").GetValue(form) != null || (string)Field("previewComponentId").GetValue(form) != id || ControlField<PictureBox>(form, "preview").Image == null)
            {
                Require(DateTime.UtcNow < deadline, context + ": hover preview did not finish.");
                RequirePreviewInteractive(form, context);
                Application.DoEvents();
                Thread.Sleep(10);
            }
        }

        private static void VerifyPreview(MainForm form, ModComponent component, ModPackage package, AppConfig settings, PictureBox preview, Label title, string context)
        {
            Require(title.Text == component.Name, context + ": hover title did not identify the requested option.");
            Require((string)Field("previewComponentId").GetValue(form) == component.Id, context + ": an older hover won the image request (wanted " + component.Name + " [" + component.Id + "], got [" + Field("previewComponentId").GetValue(form) + "], inspected [" + Field("inspectedComponentId").GetValue(form) + "]).");
            Require(preview.Image != null, context + ": pictured option produced no preview.");
            byte[] bytes = package.PreviewBytes(component.Id, settings.SevenZipPath);
            using (MemoryStream stream = new MemoryStream(bytes))
            using (Image expected = Image.FromStream(stream))
                Require(ImageFingerprint(preview.Image) == ImageFingerprint(expected), context + ": preview image does not match the requested archive option.");
            RequirePreviewInteractive(form, context);
        }

        private static string ImageFingerprint(Image image)
        {
            if (image == null) return null;
            using (Bitmap pixels = new Bitmap(image))
            using (MemoryStream stream = new MemoryStream())
            using (SHA256 hash = SHA256.Create())
            {
                pixels.Save(stream, ImageFormat.Png);
                return Convert.ToBase64String(hash.ComputeHash(stream.ToArray()));
            }
        }

        // A menu that opened (its Opened event) and the control it opened from. A hidden test window is not the
        // foreground app, so Windows may close the menu right after it opened; Visible alone is not reliable then.
        private static Control OpenedFrom(ContextMenuStrip menu, Action open)
        {
            Control source = null; bool opened = false;
            EventHandler handler = delegate { opened = true; source = menu.SourceControl; };
            menu.Opened += handler;
            try { open(); DateTime end = DateTime.UtcNow.AddSeconds(2); while (!opened && DateTime.UtcNow < end) { Application.DoEvents(); System.Threading.Thread.Sleep(20); } }
            finally { menu.Opened -= handler; }
            return opened ? source : null;
        }

        private static void CheckNavigation(MainForm form, Control layoutHost, AppConfig settings, string context)
        {
            FlatButton library = ControlField<FlatButton>(form, "navLibrary");
            FlatButton installed = ControlField<FlatButton>(form, "navInstalled");
            FlatButton gear = ControlField<FlatButton>(form, "navSettings");
            Panel libraryPage = ControlField<Panel>(form, "libraryPage");
            Panel installedPage = ControlField<Panel>(form, "installedPage");
            Panel settingsPage = ControlField<Panel>(form, "settingsPage");
            OptionTree tree = ControlField<OptionTree>(form, "componentTree");
            string[] checkedNames = AllNodes(tree.Nodes).Where(n => n.Checked).Select(n => n.Text).ToArray();

            installed.PerformClick();
            Application.DoEvents();
            Require(installedPage.Visible && !libraryPage.Visible && !settingsPage.Visible && installed.Active && !library.Active, context + ": Installed outfits tab did not open its page.");
            WaitForIdle(form, context);
            TextBox details=ControlField<TextBox>(form,"installedHelp");
            string originalDetails=details.Text;
            try
            {
                Require(details.ReadOnly && details.Multiline && details.WordWrap && details.ScrollBars==ScrollBars.Vertical,context+": installed mappings are not readable in the scrollable detail pane.");
                details.Text=String.Join(Environment.NewLine,Enumerable.Range(1,50).Select(i=>"Fixture mapping "+i+": Outfit III · Costume 3"));
                details.Select(details.TextLength,0);details.ScrollToCaret();Application.DoEvents();
                Require(SendMessage(details.Handle,0xCE,IntPtr.Zero,IntPtr.Zero).ToInt64()>0,context+": long installed mappings cannot scroll to their final lines.");
            }
            finally {details.Text=originalDetails;details.Select(0,0);}
            library.PerformClick();
            Application.DoEvents();
            Require(libraryPage.Visible && !installedPage.Visible && !settingsPage.Visible && library.Active && !installed.Active, context + ": Mod library tab did not return to its page.");

            ContextMenuStrip menu = gear.ContextMenuStrip;
            Require(menu != null, context + ": gear has no settings/tools menu.");
            Require(OpenedFrom(menu, delegate { gear.PerformClick(); }) == gear, context + ": gear did not open its menu.");
            ToolStripMenuItem settingsItem = menu.Items.OfType<ToolStripMenuItem>().FirstOrDefault(i => i.Text.StartsWith("Settings", StringComparison.OrdinalIgnoreCase));
            Require(settingsItem != null && settingsItem.Enabled, context + ": Settings menu item is unavailable.");
            // 2.5.9: gear menu without Open costume folder, Refresh libraries and Import external preset.
            // 1.0.11 (the user): Help (F1) at the end; no Welcome (Settings → General) and no About (the Settings → About tab).
            string[] gearItems = menu.Items.OfType<ToolStripMenuItem>().Select(i => i.Text).ToArray();
            Require(gearItems.SequenceEqual(new[] { "Settings…", "History", "Restore", "Keyboard shortcuts", "Help" }), context + ": gear menu items: " + String.Join(" | ", gearItems));
            ToolStripMenuItem helpItem = menu.Items.OfType<ToolStripMenuItem>().Last();
            helpItem.PerformClick(); menu.Close(); Application.DoEvents(); LayoutChildren(layoutHost);
            Panel helpPage = ControlField<Panel>(form, "helpPage");
            Require(helpItem.ShortcutKeyDisplayString == "F1" && helpPage != null && helpPage.Visible && !settingsPage.Visible && gear.Active, context + ": gear → Help did not open the Help page.");
            settingsItem.PerformClick(); ControlField<FlatButton>(form, "settingsAboutTab").PerformClick(); Application.DoEvents(); LayoutChildren(layoutHost);
            Panel aboutPanel = ControlField<Panel>(form, "settingsAboutPanel");
            AboutCard[] aboutCards = AllControls(aboutPanel).OfType<AboutCard>().OrderBy(c => c.Parent.Top).ThenBy(c => c.Left).ToArray();   // reading order
            Require(settingsPage.Visible && aboutPanel.Visible && ControlField<FlatButton>(form, "settingsAboutTab").Active && !ControlField<Panel>(form, "settingsUpdatesPanel").Visible, context + ": the About tab did not open Settings → About.");
            Require(aboutCards.Select(c => c.Url).SequenceEqual(new[] { MainForm.DiscordUrl, MainForm.PatreonUrl, MainForm.RepoUrl, MainForm.AuthorUrl, MainForm.ColorSyncUrl, MainForm.NexusProfileUrl, MainForm.AuthorGitHubUrl, MainForm.Wael3rdUrl, MainForm.BrewedVfxUrl }), context + ": About cards: " + String.Join(", ", aboutCards.Select(c => c.Title)));
            // Cards keep their size and stay side by side inside the panel's width (it scrolls vertically when short, never sideways).
            Require(aboutCards.All(c => c.Visible && c.Width > 0 && c.Height > 0 && c.Right <= c.Parent.Width) && !aboutPanel.HorizontalScroll.Visible && aboutCards.First().Parent.Right <= aboutPanel.ClientSize.Width, context + ": About cards are clipped or scroll sideways.");
            Require(OpenedFrom(menu, delegate { gear.PerformClick(); }) == gear, context + ": gear did not open its menu again.");
            settingsItem.PerformClick();
            menu.Close();
            Application.DoEvents();
            LayoutChildren(layoutHost);
            Require(settingsPage.Visible && !libraryPage.Visible && !installedPage.Visible && gear.Active, context + ": Settings menu item did not open its page.");
            Require(ControlField<PaddedTextField>(form, "settingsGame").Text == settings.GameRoot, context + ": Settings page did not load the configured game folder.");
            Require(ControlField<ListBox>(form, "settingsLibraries").Items.Count == settings.Libraries.Count, context + ": Settings page did not load the configured libraries.");
            Button save = AllControls(settingsPage).OfType<Button>().FirstOrDefault(b => b.Text == "Save settings");
            Require(save != null && save.Visible && save.Enabled, context + ": Save settings button is unavailable.");
            RequireInside(save, layoutHost, context);

            library.PerformClick();
            Application.DoEvents();
            LayoutChildren(layoutHost);
            Require(libraryPage.Visible && !installedPage.Visible && !settingsPage.Visible && library.Active && !gear.Active && tree.Visible, context + ": Mod library did not return after Settings.");
            Require(AllNodes(tree.Nodes).Where(n => n.Checked).Select(n => n.Text).SequenceEqual(checkedNames), context + ": page navigation changed the selected options.");
        }

        private static void WaitForIdle(MainForm form, string context)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(10);
            while (form.Working)
            {
                Require(DateTime.UtcNow < deadline, context + ": installed-outfit read did not finish.");
                Application.DoEvents();
                Thread.Sleep(10);
            }
        }

        private static FieldInfo Field(string name)
        {
            FieldInfo field = typeof(MainForm).GetField(name, PrivateInstance);
            if (field == null) throw new InvalidOperationException("Layout check could not locate field " + name + ".");
            return field;
        }

        private static T ControlField<T>(MainForm form, string name) where T : Control
        {
            T control = Field(name).GetValue(form) as T;
            if (control == null) throw new InvalidOperationException("Layout check found no control for " + name + ".");
            return control;
        }

        private static Rectangle BoundsInHost(Control control, Control host)
        {
            Point location = new Point(0, 0);
            Control current = control;
            while (current != null && current != host)
            {
                location.Offset(current.Left, current.Top);
                current = current.Parent;
            }
            if (current != host) throw new InvalidOperationException("Layout check found a detached control.");
            return new Rectangle(location, control.Size);
        }

        private static void RequireInside(Control control, Control host, string context)
        {
            Rectangle bounds = BoundsInHost(control, host);
            Require(bounds.Width > 0 && bounds.Height > 0 && host.ClientRectangle.Contains(bounds), context + ": control " + control.GetType().Name + " (" + control.Text + ") is outside the layout host at " + bounds + ".");
        }

        private static void LayoutChildren(Control root)
        {
            root.PerformLayout();
            foreach (Control child in root.Controls) LayoutChildren(child);
        }

        private static IEnumerable<Control> AllControls(Control root)
        {
            foreach (Control child in root.Controls)
            {
                yield return child;
                foreach (Control descendant in AllControls(child)) yield return descendant;
            }
        }

        private static IEnumerable<OptionNode> AllNodes(OptionNodeCollection nodes)
        {
            foreach (OptionNode node in nodes)
            {
                yield return node;
                foreach (OptionNode descendant in AllNodes(node.Nodes)) yield return descendant;
            }
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("UI layout regression: " + message);
        }
    }
}

