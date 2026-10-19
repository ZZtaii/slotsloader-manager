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
    // 2.5.0 (--slots-check, test.ps1 suite "slots"): several variants of one mod as extra slots, in a hidden window on a
    // generated game folder: the suggested outfit name, the hint, live ticks kept across installs, Reset options, and the
    // Installed page's group row (uninstall all / one, history, Select all).
    internal static class SlotChecks
    {
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        private const string Mod = "Buff Leon as Luke C1", Main = "01 Main files", Hair = "02 Leon Hair", Jacket = "03 Red Jacket";

        internal static int Run(string output)
        {
            string root = Path.Combine(Path.GetFullPath(output), "slots-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Safety.NoReparseChain(Path.GetFullPath(output)); Directory.CreateDirectory(root);
            var report = new List<string>(); int failures = 0;
            bool previousHeadless = Program.Headless; Program.Headless = true;
            SynchronizationContext previous = SynchronizationContext.Current; bool previousAuto = WindowsFormsSynchronizationContext.AutoInstall;
            WindowsFormsSynchronizationContext.AutoInstall = false;
            IProcessProbe previousProbe = GameProcesses.Probe; GameProcesses.Probe = new FakeProcessProbe();
            ReplaceSettings savedSettings = ReplaceSettings.Load();
            try
            {
                using (var ui = new WindowsFormsSynchronizationContext())
                {
                    SynchronizationContext.SetSynchronizationContext(ui);
                    string game = Path.Combine(root, "game"), mods = Path.Combine(root, "mods"), mod = Path.Combine(mods, Mod);
                    Directory.CreateDirectory(Path.Combine(game, "reframework", "costume_mods"));
                    Put(mod, Main + "/modinfo.ini", "name=Main files\r\nnameasbundle=" + Mod + "\r\n");
                    Put(mod, Main + "/natives/stm/product/model/esf/esf002/001/01/esf002_001_01.mesh.230110883", "leon body");
                    Put(mod, Main + "/natives/stm/product/model/esf/esf002/001/01/esf002_001_01.mdf2.40", "leon material");
                    Put(mod, Hair + "/modinfo.ini", "name=Leon Hair\r\nnameasbundle=" + Mod + "\r\n");
                    Put(mod, Hair + "/natives/stm/product/model/esf/esf002/001/01/esf002_001_01_hair_albd.tex.241101895", "hair");
                    Put(mod, Jacket + "/modinfo.ini", "name=Red Jacket\r\nnameasbundle=" + Mod + "\r\n");
                    Put(mod, Jacket + "/natives/stm/product/model/esf/esf002/001/01/esf002_001_01_jacket_albd.tex.241101895", "jacket");
                    var config = new AppConfig { GameRoot = game, Libraries = new List<string>(), SevenZipPath = "" };
                    using (var form = new MainForm(config))
                    {
                        form.PortraitCacheDirectory = Path.Combine(root, "portraits");
                        Set(form, "firstLoad", false); form.ShowInTaskbar = false; form.Opacity = 0; form.MinimumSize = Size.Empty; form.Show(); Application.DoEvents();
                        form.ClientSize = new Size(DpiMetrics.Px(form, 1232), DpiMetrics.Px(form, 640)); Application.DoEvents();
                        Call(form, "SetInstallMode", InstallModes.Slot, true);
                        Action<string, Action> check = delegate(string name, Action action)
                        {
                            try { action(); report.Add("PASS " + name); }
                            catch (Exception ex) { failures++; report.Add("FAIL " + name + ": " + ex); }
                        };
                        check("suggested name and hint (2.5.0): first install keeps the plain mod name and no hint; ticks stay after each install and the name then matches the slot (\"Updates this slot.\"); a different selection suggests \"<mod> · <options joined with ' + '>\" with \"Adds a new slot. Keeps: …\" (two names, then +N more); the same selection gives back the slot's name; a taken name gets \" 2\"; a typed name is kept until cleared; Replace mode leaves the name alone; the hint is the tooltip of the outfit name and Install", delegate { Naming(form, mod, game, root); });
                        check("Reset options (2.5.0): shown only when the ticks differ from the mod's defaults (both modes), puts the defaults back without a dialog, and the name and hint follow", delegate { Reset(form); });
                        check("Installed grouping (2.5.0): the slots of one source are one row \"<mod> · N slots\" (a source with one slot and manual entries stay plain rows); the chevron/keys expand it to its slots with their options; Uninstall on a slot removes just it, on the group all slots in one click, no dialog, deleted for good with no Undo (1.0.1), a history event per slot; Select all and Clear still work; renders", delegate { Grouping(form, game, root); });
                        check("Installed sort (2.5.5; 1.0.1: only the Installed chip): Installed ↓ (newest first) by default, a second press flips it; dated rows in order and undated ones last; saved in the view; an older view or a saved Character/Played sort gives Installed ↓", delegate { InstalledSort(form); });
                        check("Install all variants (2.5.4): an archive with several options is copied unchanged into costume_mods\\<character>\\ like a manual drop, linked to its library mod (✓ Full, Reinstall, footer Uninstall for good with no Undo, 1.0.1); folders and Replace original get no button; a manual file of the same name is never overwritten", delegate { AllVariants(form, mod, game, root); });
                        check("Renamed archive reinstall (1.0.5): keeps the installed filename, record and variant choices; outside-root records are ignored; changed, wrong-character and ambiguous copies are refused", delegate { RenamedArchiveReinstall(root); });
                        check("Archive records from another game (1.0.5): existing and absent old copies do not block a new root; their files and records stay untouched", delegate { ArchiveOtherGameRoots(root); });
                        form.Close();
                    }
                }
            }
            catch (Exception ex) { failures++; report.Add("FAIL setup: " + ex); }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous); WindowsFormsSynchronizationContext.AutoInstall = previousAuto;
                Program.Headless = previousHeadless; GameProcesses.Probe = previousProbe; try { savedSettings.Save(); } catch { }
            }
            report.Add(failures == 0 ? "ALL PASS" : failures + " FAILED");
            File.WriteAllLines(Path.Combine(root, "slots-check.txt"), report);
            Console.WriteLine(String.Join(Environment.NewLine, report));
            Console.WriteLine("Fixtures and renders: " + root);
            return failures == 0 ? 0 : 1;
        }

        private static void CopyTree(string from, string to) { Directory.CreateDirectory(to); foreach (string f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f))); foreach (string d in Directory.GetDirectories(from)) CopyTree(d, Path.Combine(to, Path.GetFileName(d))); }
        private static void Put(string root, string relative, string text) { string f = Path.Combine(root, relative.Replace('/', '\\')); Directory.CreateDirectory(Path.GetDirectoryName(f)); File.WriteAllText(f, text, Encoding.UTF8); }
        private static string Name(MainForm form) { return Get<PaddedTextField>(form, "outfitName").Text; }
        private static List<string> Ticks(MainForm form) { return Get<List<string>>(form, "selectedOrder").ToList(); }
        private static void Tick(MainForm form, string id, bool on) { Get<Dictionary<string, OptionNode>>(form, "componentNodes")[id].Checked = on; Application.DoEvents(); WaitIdle(form); }
        private static bool Same(IEnumerable<string> a, params string[] b) { return new HashSet<string>(a, StringComparer.OrdinalIgnoreCase).SetEquals(b); }
        private static void Install(MainForm form)
        {
            WaitIdle(form);
            Require(Get<FlatButton>(form, "installButton").Enabled, "Install is not enabled: " + Get<Label>(form, "planSummary").Text + " / " + Get<Label>(form, "planDetails").Text);
            Call(form, "InstallCurrent"); WaitIdle(form);
        }
        // Through reflection: the no-tooltip audit scans every method body, checks included.
        private static string Tip(MainForm form, Control c) { object tip = Get<object>(form, "slotHintTip"); return tip == null ? "" : (string)tip.GetType().GetMethod("GetToolTip").Invoke(tip, new object[] { c }) ?? ""; }

        private static void Naming(MainForm form, string mod, string game, string root)
        {
            Call(form, "ShowPage", Get<Panel>(form, "libraryPage"));
            Call(form, "LoadPackage", new LibraryEntry { SourcePath = mod, Name = Mod }); WaitIdle(form);
            string costume = Path.Combine(game, "reframework", "costume_mods", "Luke");
            Require(Convert.ToString(Get<ComboBox>(form, "targetCharacter").SelectedItem) == "Luke", "character");
            Require(Name(form) == Mod && form.SlotHintText == "" && Same(Ticks(form), Main), "first open: " + Name(form) + " / " + form.SlotHintText);
            Install(form);
            Require(Directory.Exists(Path.Combine(costume, Mod)), "first slot not installed");
            Require(Same(Ticks(form), Main) && Name(form) == Mod && form.SlotHintText == "Updates this slot.", "after the first install: " + Name(form) + " / " + form.SlotHintText);
            // Another variant from the live ticks.
            Tick(form, Hair, true);
            string hair = Mod + " · Leon Hair";
            Require(Name(form) == hair && form.SlotHintText == "Adds a new slot. Keeps: " + Mod + ".", "hair: " + Name(form) + " / " + form.SlotHintText);
            Require(Tip(form, Get<PaddedTextField>(form, "outfitName")) == form.SlotHintText && Tip(form, Get<FlatButton>(form, "installButton")) == form.SlotHintText, "tooltip text");
            Render(form, Path.Combine(root, "library-next-slot-" + form.DeviceDpi + ".png"));
            Install(form);
            Require(Directory.Exists(Path.Combine(costume, hair)) && Same(Ticks(form), Main, Hair), "ticks reset after the install: " + String.Join(",", Ticks(form)));
            Require(Name(form) == hair && form.SlotHintText == "Updates this slot.", "after the second install: " + Name(form) + " / " + form.SlotHintText);
            // A manual entry already uses the next name: " 2".
            Directory.CreateDirectory(Path.Combine(costume, Mod + " · Red Jacket", "natives")); File.WriteAllText(Path.Combine(costume, Mod + " · Red Jacket", "natives", "manual.bin"), "manual");
            Call(form, "RefreshInstalled", (string)null); WaitIdle(form);
            Tick(form, Hair, false); Tick(form, Jacket, true);
            string jacket = Mod + " · Red Jacket 2";
            Require(Name(form) == jacket && form.SlotHintText == "Adds a new slot. Keeps: " + Mod + ", " + hair + ".", "collision: " + Name(form) + " / " + form.SlotHintText);
            Install(form);
            Require(Directory.Exists(Path.Combine(costume, jacket)) && Same(Ticks(form), Main, Jacket), "jacket slot");
            // 1.0.3: opening the mod again shows the options of its newest install (not the defaults).
            Call(form, "LoadPackage", new LibraryEntry { SourcePath = mod, Name = Mod }); WaitIdle(form);
            Require(Same(Ticks(form), Main, Jacket), "reopened with the installed options: " + String.Join(",", Ticks(form)));
            // Two options: joined with " + "; three slots kept: two names, then +1 more.
            Tick(form, Hair, true);
            string both = Mod + " · Leon Hair + Red Jacket";
            Require(Name(form) == both && form.SlotHintText == "Adds a new slot. Keeps: " + Mod + ", " + hair + " +1 more.", "two options: " + Name(form) + " / " + form.SlotHintText);
            // The same selection as an installed slot gives back its name.
            Tick(form, Jacket, false);
            Require(Name(form) == hair && form.SlotHintText == "Updates this slot.", "same selection: " + Name(form));
            // A typed name is kept through option changes; clearing it brings the suggestion back.
            Get<PaddedTextField>(form, "outfitName").Text = "My Leon"; Application.DoEvents();
            Tick(form, Jacket, true);
            Require(Name(form) == "My Leon" && form.SlotHintText.StartsWith("Adds a new slot. Keeps: "), "typed name replaced: " + Name(form));
            Get<PaddedTextField>(form, "outfitName").Text = ""; Application.DoEvents();
            Tick(form, Jacket, false); Tick(form, Jacket, true);
            Require(Name(form) == both, "suggestion not back after clearing: " + Name(form));
            // Replace mode: the name is not suggested; back in Extra outfit it is.
            Call(form, "SetInstallMode", InstallModes.Replace, true); WaitIdle(form);
            Require(form.SlotHintText == "", "hint in Replace mode");
            Call(form, "SetInstallMode", InstallModes.Slot, true); WaitIdle(form);
            Require(Name(form) == both, "slot mode name: " + Name(form));
        }

        private static void Reset(MainForm form)
        {
            FlatButton reset = Get<FlatButton>(form, "resetOptionsButton");
            Require(!Same(Ticks(form), Main) && WantVisible(reset), "Reset options hidden while the ticks differ");
            int dialogs = 0; ThemedMessageBox.TestResponder = delegate { dialogs++; return DialogResult.Cancel; };
            try { reset.PerformClick(); Application.DoEvents(); WaitIdle(form); }
            finally { ThemedMessageBox.TestResponder = null; }
            Require(dialogs == 0 && Same(Ticks(form), Main) && !WantVisible(reset), "reset: " + String.Join(",", Ticks(form)));
            Require(Name(form) == Mod && form.SlotHintText == "Updates this slot.", "name after reset: " + Name(form) + " / " + form.SlotHintText);
            Call(form, "SetInstallMode", InstallModes.Replace, true); WaitIdle(form);
            Tick(form, Hair, true);
            Require(WantVisible(reset), "Reset options not shown in Replace mode");
            reset.PerformClick(); Application.DoEvents(); WaitIdle(form);
            Require(Same(Ticks(form), Main) && !WantVisible(reset), "Replace-mode reset");
            Call(form, "SetInstallMode", InstallModes.Slot, true); WaitIdle(form);
            Require(UiAudit.ToolTipFindings(form).Count == 0, "tooltip outside the allow-list: " + String.Join("; ", UiAudit.ToolTipFindings(form)));
            int scanned; List<string> code = UiAudit.ToolTipCode(out scanned); Require(code.Count == 0, "tooltip code outside the allow-list: " + String.Join("; ", code));
        }
        // Control.Visible reads false while a parent is hidden; the requested state is what the code set.
        private static bool WantVisible(Control c) { return (bool)typeof(Control).GetMethod("GetState", PrivateInstance).Invoke(c, new object[] { 2 }); }

        private static void Grouping(MainForm form, string game, string root)
        {
            Call(form, "ShowPage", Get<Panel>(form, "installedPage")); Call(form, "RefreshInstalled", (string)null); WaitIdle(form);
            var list = Get<ListView>(form, "installedList");
            Func<List<ListViewItem>> rows = delegate { return list.Items.Cast<ListViewItem>().ToList(); };
            Action<ListViewItem> select = delegate(ListViewItem item) { foreach (ListViewItem i in list.Items) i.Selected = false; item.Selected = true; Application.DoEvents(); Call(form, "ShowInstalledSelection"); };
            Func<OutfitGroup> group = delegate { return rows().Select(i => i.Tag as OutfitGroup).FirstOrDefault(g => g != null); };
            Require(group() != null && group().Label == Mod + " · 3 slots" && rows().Count == 2, "group row: " + String.Join(" | ", rows().Select(i => i.Text)));
            Require(rows().Any(i => i.Tag is InstalledOutfit && !((InstalledOutfit)i.Tag).Managed), "the manual entry should stay a plain row");
            select(rows().First(i => i.Tag is OutfitGroup));
            Require(Get<Label>(form, "installedTitle").Text == Mod + " · 3 slots" && Get<FlatButton>(form, "removeInstalledButton").Text == "Uninstall 3 slots", "group inspector: " + Get<FlatButton>(form, "removeInstalledButton").Text);
            Call(form, "ToggleGroup", group(), (bool?)true); Application.DoEvents();
            Require(rows().Count == 5 && rows().Count(i => i.Tag is InstalledOutfit && ((InstalledOutfit)i.Tag).Managed) == 3, "expanded: " + rows().Count);
            Require(MainForm.SlotOptions(rows().Select(i => i.Tag as InstalledOutfit).First(o => o != null && o.Name == Mod + " · Leon Hair")) == "Leon Hair", "slot options");
            Render(form, Path.Combine(root, "installed-grouped-" + form.DeviceDpi + ".png"));
            // Select all / Clear.
            Call(form, "SelectInstalledRows", true);
            Require(Get<ListView>(form, "installedList").SelectedItems.Count == 5 && ((List<InstalledOutfit>)Call(form, "GetInstalledSelections")).Count == 4, "select all");
            Call(form, "SelectInstalledRows", false);
            Require(list.SelectedItems.Count == 0, "clear");
            string costume = Path.Combine(game, "reframework", "costume_mods", "Luke");
            int dialogs = 0; ThemedMessageBox.TestResponder = delegate { dialogs++; return DialogResult.Cancel; };
            try
            {
                // 1.0.1: uninstall means uninstall: the slots are deleted (their source is in the library), no Undo.
                // Between the steps the fixture puts the deleted slot folders back (a reinstall would rename them).
                string saved = Path.Combine(root, "luke-slots-" + Guid.NewGuid().ToString("N").Substring(0, 6));
                foreach (string d in Directory.GetDirectories(costume)) CopyTree(d, Path.Combine(saved, Path.GetFileName(d)));
                Action putBack = delegate
                {
                    foreach (string d in Directory.GetDirectories(saved)) { string to = Path.Combine(costume, Path.GetFileName(d)); if (!Directory.Exists(to)) CopyTree(d, to); }
                    Call(form, "RefreshInstalled", (string)null); WaitIdle(form);
                };
                Func<string> status = delegate { return Get<Label>(form, "statusText").Text; };
                // One slot.
                DateTime before = DateTime.UtcNow.AddSeconds(-1);
                select(rows().First(i => i.Tag is InstalledOutfit && ((InstalledOutfit)i.Tag).Name == Mod + " · Leon Hair"));
                Require(Get<FlatButton>(form, "removeInstalledButton").Text == "Uninstall outfit", "slot button");
                Get<FlatButton>(form, "removeInstalledButton").PerformClick(); WaitIdle(form);
                Require(!Directory.Exists(Path.Combine(costume, Mod + " · Leon Hair")) && Directory.Exists(Path.Combine(costume, Mod)) && !form.UndoAvailable && status().Contains("Uninstalled"), "one slot uninstall: undo '" + form.UndoLabel + "', status " + status());
                Require(group() != null && group().Slots.Count == 2, "group after one uninstall");
                putBack();
                Require(Directory.Exists(Path.Combine(costume, Mod + " · Leon Hair")) && group().Slots.Count == 3, "fixture: the slot is not back");
                // The whole group.
                select(rows().First(i => i.Tag is OutfitGroup));
                Get<FlatButton>(form, "removeInstalledButton").PerformClick(); WaitIdle(form);
                Require(!Directory.Exists(Path.Combine(costume, Mod)) && !Directory.Exists(Path.Combine(costume, Mod + " · Leon Hair")) && !Directory.Exists(Path.Combine(costume, Mod + " · Red Jacket 2")) && Directory.Exists(Path.Combine(costume, Mod + " · Red Jacket")), "group uninstall");
                Require(!form.UndoAvailable && status().Contains("Uninstalled " + Mod + " (3 slots)"), "group status: " + status() + ", undo '" + form.UndoLabel + "'");
                List<HistoryEvent> events = HistoryStore.For(AppConfig.DataDir).Events().Where(e => e.Kind == HistoryKinds.Uninstall && e.Utc >= before && (e.Name ?? "").StartsWith(Mod)).ToList();
                Require(events.Select(e => e.Name).Distinct().Count() == 3, "history per slot: " + events.Count);
                putBack();
                Require(group() != null && group().Slots.Count == 3, "fixture: the group is not back");
                // Two of the three slot rows (Ctrl-selected): exactly those.
                Call(form, "ToggleGroup", group(), (bool?)true); Application.DoEvents();
                foreach (ListViewItem i in list.Items) i.Selected = false;
                foreach (ListViewItem i in rows().Where(i => i.Tag is InstalledOutfit && (((InstalledOutfit)i.Tag).Name == Mod + " · Leon Hair" || ((InstalledOutfit)i.Tag).Name == Mod + " · Red Jacket 2"))) i.Selected = true;
                Application.DoEvents(); Call(form, "ShowInstalledSelection");
                Require(list.SelectedItems.Count == 2 && Get<FlatButton>(form, "removeInstalledButton").Text == "Uninstall 2 slots", "two slots button: " + Get<FlatButton>(form, "removeInstalledButton").Text);
                Get<FlatButton>(form, "removeInstalledButton").PerformClick(); WaitIdle(form);
                Require(!Directory.Exists(Path.Combine(costume, Mod + " · Leon Hair")) && !Directory.Exists(Path.Combine(costume, Mod + " · Red Jacket 2")) && Directory.Exists(Path.Combine(costume, Mod)), "two of three");
                Require(!form.UndoAvailable && status().Contains("Uninstalled 2 slots of " + Mod), "two slots status: " + status());
                // One slot left: a plain row again (a source with one slot is not grouped).
                Require(group() == null && rows().Any(i => i.Tag is InstalledOutfit && ((InstalledOutfit)i.Tag).Name == Mod), "the remaining slot");
                putBack();
                Require(Directory.Exists(Path.Combine(costume, Mod + " · Leon Hair")) && Directory.Exists(Path.Combine(costume, Mod + " · Red Jacket 2")) && group() != null && group().Slots.Count == 3, "fixture: the two slots are not back");
            }
            finally { ThemedMessageBox.TestResponder = null; }
            Require(dialogs == 0, "a dialog opened");
            // A source with one slot is a plain row.
            Call(form, "SelectInstalledRows", true); var keep = ((List<InstalledOutfit>)Call(form, "GetInstalledSelections")).Where(o => o.Managed && o.Name != Mod).ToList();
            Call(form, "SelectInstalledRows", false);
            OutfitSetStore.RemoveOutfits(Path.Combine(game, "reframework", "costume_mods"), keep, "fixture");
            Call(form, "RefreshInstalled", (string)null); WaitIdle(form);
            Require(group() == null && rows().Any(i => i.Tag is InstalledOutfit && ((InstalledOutfit)i.Tag).Name == Mod), "single slot grouped");
        }

        // 2.5.5: the Installed sort chips. 1.0.1 (the user): only the Installed chip is left (Character and Played are
        // gone): Installed ↓ (newest first) by default, a press flips it; dated rows are in order with undated ones
        // last; the choice is saved in the view; an older view, or a saved Character/Played sort, gives Installed ↓.
        private static void InstalledSort(MainForm form)
        {
            Call(form, "ShowPage", Get<Panel>(form, "installedPage")); Application.DoEvents();
            Call(form, "RefreshInstalled", (string)null); WaitIdle(form);
            var chips = Get<Dictionary<InstalledSortKey, FlatButton>>(form, "installedSortChips"); ListView list = Get<ListView>(form, "installedList");
            Func<List<object>> top = delegate { return list.Items.Cast<ListViewItem>().Select(i => i.Tag).Where(t => !(t is InstalledOutfit) || !Get<Dictionary<InstalledOutfit, OutfitGroup>>(form, "groupedSlots").ContainsKey((InstalledOutfit)t)).ToList(); };
            Func<object, long> ticks = delegate(object row) { return (long)Call(form, "InstalledSortTicks", row); };
            Require(chips.Count == 1 && chips.ContainsKey(InstalledSortKey.Installed), "chips: " + String.Join(", ", chips.Keys));
            FlatButton chip = chips[InstalledSortKey.Installed];
            Require(form.InstalledSort == InstalledSortKey.Installed && !form.InstalledSortAscending && chip.Text == "Installed ↓" && chip.Active, "default: " + chip.Text);
            List<object> first = top();
            Require(first.Count >= 2, "rows: " + first.Count);
            Action<bool> ordered = delegate(bool asc)
            {
                List<long> keys = top().Select(ticks).ToList();
                int firstUndated = keys.FindIndex(k => k <= 0); if (firstUndated < 0) firstUndated = keys.Count;
                Require(keys.Skip(firstUndated).All(k => k <= 0), "an undated row before a dated one");
                for (int i = 1; i < firstUndated; i++) Require(asc ? keys[i - 1] <= keys[i] : keys[i - 1] >= keys[i], "Installed " + (asc ? "↑" : "↓") + " out of order at " + i);
                Require(top().Count == first.Count, "rows lost");
            };
            ordered(false);
            chip.PerformClick(); Application.DoEvents();
            Require(form.InstalledSortAscending && chip.Text == "Installed ↑" && chip.Active, "a second press did not flip: " + chip.Text);
            ordered(true);
            var view = (ViewPreferences)Call(form, "CaptureView");
            Require(view.InstalledSort == "installed" && view.InstalledSortOrder == "asc", "saved: " + view.InstalledSort + " " + view.InstalledSortOrder);
            Call(form, "RestoreInstalledSort", view); Call(form, "FillInstalled");
            Require(form.InstalledSort == InstalledSortKey.Installed && form.InstalledSortAscending, "the saved Installed ↑ did not come back");
            foreach (ViewPreferences old in new[] { new ViewPreferences(), new ViewPreferences { InstalledSort = "character", InstalledSortOrder = "asc" }, new ViewPreferences { InstalledSort = "played", InstalledSortOrder = "asc" } })
            {
                Call(form, "RestoreInstalledSort", old); Call(form, "FillInstalled");
                Require(form.InstalledSort == InstalledSortKey.Installed && !form.InstalledSortAscending && chip.Text == "Installed ↓" && top().SequenceEqual(first), "an older view (" + (old.InstalledSort ?? "none") + ") does not give Installed ↓");
            }
            Call(form, "ShowPage", Get<Panel>(form, "libraryPage")); Application.DoEvents();
        }        private static string InstalledSortLabelOf(InstalledSortKey key) { return MainForm.InstalledSortLabel(key); }

        // 2.5.4: Install all variants copies the archive unchanged into costume_mods\<character>\ (a manual drop), linked
        // to its library mod: ✓ Full, Reinstall, the footer's Uninstall (1.0.1: for good, no Undo); folders, single-option mods and Replace
        // original get no button; a manual file of the same name is never overwritten.
        private static void AllVariants(MainForm form, string mod, string game, string root)
        {
            string zip = Path.Combine(root, "zips", Mod + ".zip"); Directory.CreateDirectory(Path.GetDirectoryName(zip));
            System.IO.Compression.ZipFile.CreateFromDirectory(mod, zip);
            string dest = Path.Combine(game, "reframework", "costume_mods", "Luke", Mod + ".zip");
            Panel row = Get<Panel>(form, "allVariantsRow"); FlatButton all = Get<FlatButton>(form, "allVariantsButton"), uninstall = Get<FlatButton>(form, "libraryUninstallButton");
            Func<InstalledOutfit> copy = delegate { return Get<List<InstalledOutfit>>(form, "installed").FirstOrDefault(o => String.Equals(o.Folder, dest, StringComparison.OrdinalIgnoreCase)); };
            int dialogs = 0; ThemedMessageBox.TestResponder = delegate { dialogs++; return DialogResult.OK; };
            try
            {
                Call(form, "ShowPage", Get<Panel>(form, "libraryPage")); Call(form, "SetInstallMode", InstallModes.Slot, false); Application.DoEvents();
                Call(form, "LoadPackage", new LibraryEntry { SourcePath = mod, Name = Mod }); WaitIdle(form);
                Require(!row.Visible || !all.Visible, "a folder mod offers Install all variants");   // 1.0.6: the row may still show Add to outfit…
                Call(form, "LoadPackage", new LibraryEntry { SourcePath = zip, Name = Mod }); WaitIdle(form);
                Require(row.Visible && all.Visible && all.Enabled && all.Text == "Install all variants" && Convert.ToString(Get<ComboBox>(form, "targetCharacter").SelectedItem) == "Luke", "button: " + row.Visible + "/" + all.Enabled + "/" + all.Text);
                Call(form, "SetInstallMode", InstallModes.Replace, false); Application.DoEvents();
                Require(!row.Visible, "shown in Replace original");
                Call(form, "SetInstallMode", InstallModes.Slot, false); Application.DoEvents();
                // A manual file of the same name stays untouched.
                File.WriteAllText(dest, "manual"); int before = form.AllVariantsInstalls;
                all.PerformClick(); WaitIdle(form);
                Require(File.ReadAllText(dest) == "manual" && form.AllVariantsInstalls == before, "a manual archive was overwritten");
                File.Delete(dest); Call(form, "RefreshInstalled", (string)null); WaitIdle(form);
                all.PerformClick(); WaitIdle(form);
                Require(form.AllVariantsInstalls == before + 1 && File.Exists(dest) && File.ReadAllBytes(dest).SequenceEqual(File.ReadAllBytes(zip)), "the archive was not copied unchanged");
                Require(!Directory.EnumerateFiles(Path.GetDirectoryName(dest), ".*").Any(), "a partial copy was left");
                InstalledOutfit o = copy();
                Require(o != null && o.WholeArchive && !o.Managed && o.Enabled && String.Equals(o.SourcePath, zip, StringComparison.OrdinalIgnoreCase), "the copy is not linked to its source");
                Require(Library.InstallState(new LibraryEntry { SourcePath = zip, OptionIds = new List<string> { "x" } }, Get<List<InstalledOutfit>>(form, "installed")) == LibraryInstallState.Full, "library state is not Full");
                Require(all.Text == "Reinstall all variants" && uninstall.Visible && uninstall.Enabled && uninstall.Text == "Uninstall outfit", "after install: " + all.Text + " / " + uninstall.Text + " " + uninstall.Enabled);
                // Reinstall: in place, still one linked copy.
                all.PerformClick(); WaitIdle(form);
                Require(form.AllVariantsInstalls == before + 2 && copy() != null && copy().WholeArchive && Get<List<InstalledOutfit>>(form, "installed").Count(x => x.WholeArchive) == 1, "reinstall");
                // The footer's Uninstall (Ctrl) removes it for good (1.0.1: its source is in the library, so no Undo);
                // Install all variants brings it back, still linked.
                uninstall.PerformClick(); WaitIdle(form);
                Require(!File.Exists(dest) && copy() == null && !form.UndoAvailable && all.Text == "Install all variants", "uninstall: " + File.Exists(dest) + " / " + all.Text + " / undo '" + form.UndoLabel + "'");
                all.PerformClick(); WaitIdle(form);
                Require(File.Exists(dest) && copy() != null && copy().WholeArchive, "the reinstall did not give a linked copy");
                uninstall.PerformClick(); WaitIdle(form);
                Require(!File.Exists(dest), "cleanup uninstall");
            }
            finally { ThemedMessageBox.TestResponder = null; try { if (File.Exists(dest)) File.Delete(dest); } catch { } }
            Require(dialogs <= 1, "dialogs: " + dialogs);   // at most the refusal of the occupied name
        }

        private static void RenamedArchiveReinstall(string root)
        {
            string fixture = Path.Combine(root, "renamed-archive"), library = Path.Combine(fixture, "library"), data = Path.Combine(fixture, "Data");
            string costume = Path.Combine(fixture, "game", "reframework", "costume_mods"), source = Path.Combine(library, "Original.zip"), renamed = Path.Combine(library, "Renamed.zip");
            Directory.CreateDirectory(library); Directory.CreateDirectory(data); Directory.CreateDirectory(costume);
            File.WriteAllText(source, "archive version 1");
            string oldData = ArchiveInstalls.DataDirOverride, oldHistory = HistoryStore.DefaultDir, oldCache = Library.IdentityCacheDirectory;
            ArchiveInstalls.DataDirOverride = data; HistoryStore.DefaultDir = Path.Combine(fixture, "history"); Library.IdentityCacheDirectory = data;
            try
            {
                string recordsFile = Path.Combine(data, ArchiveInstalls.FileName);
                InstallResult first = ArchiveInstalls.Install(source, costume, "Luke", "Fixture");
                ArchiveInstallRecord firstRecord = Json.Read<ArchiveInstallFile>(recordsFile).Records.Single();
                string choiceKey = VariantChoices.SourceOf(new InstalledOutfit { Folder = first.Folder }, costume);
                VariantChoices.Restore(costume, choiceKey, new Dictionary<string, bool> { { "Blue", false } });
                byte[] choices = File.ReadAllBytes(VariantChoices.ChoicesFile(costume));
                SourceRename.Outcome rename = SourceRename.Run(source, "Renamed", new[] { library }, new KeyValuePair<string, string>[0], costume, null);
                Require(rename.Done && File.Exists(renamed), "rename: " + rename.Error);
                File.WriteAllText(renamed, "archive version 2");
                InstallResult again = ArchiveInstalls.Install(renamed, costume, "Luke", "Fixture");
                ArchiveInstallRecord updated = Json.Read<ArchiveInstallFile>(recordsFile).Records.Single();
                Require(again.Folder == first.Folder && File.ReadAllBytes(again.Folder).SequenceEqual(File.ReadAllBytes(renamed)) && !File.Exists(ArchiveInstalls.Destination(costume, "Luke", renamed)), "reinstall created a second filename or did not update the old copy");
                Require(updated.Id == firstRecord.Id && updated.Path == first.Folder && updated.SourcePath == renamed, "reinstall lost the record identity or source link");
                Require(File.ReadAllBytes(VariantChoices.ChoicesFile(costume)).SequenceEqual(choices) && !VariantChoices.ReadChoices(costume).IsOn(choiceKey, "Blue"), "variant choices changed or lost their installed filename");

                // Choosing another character makes its own copy, never reuses the first character's destination.
                InstallResult juri = ArchiveInstalls.Install(renamed, costume, "Juri", "Fixture");
                Require(juri.Folder == ArchiveInstalls.Destination(costume, "Juri", renamed) && File.Exists(first.Folder), "another character reused Luke's installed copy");
                byte[] records = File.ReadAllBytes(recordsFile), good = File.ReadAllBytes(first.Folder);
                long ticks = File.GetLastWriteTimeUtc(first.Folder).Ticks;
                byte[] changed = (byte[])good.Clone(); changed[0] ^= 1;
                File.WriteAllBytes(first.Folder, changed); File.SetLastWriteTimeUtc(first.Folder, new DateTime(ticks, DateTimeKind.Utc));
                RefuseArchive(delegate { ArchiveInstalls.Install(renamed, costume, "Luke", "Fixture"); }, "changed");
                Require(File.ReadAllBytes(first.Folder).SequenceEqual(changed) && File.ReadAllBytes(recordsFile).SequenceEqual(records), "changed archive or its record was overwritten");
                File.WriteAllBytes(first.Folder, good); File.SetLastWriteTimeUtc(first.Folder, new DateTime(ticks, DateTimeKind.Utc));

                // A record outside this root is ignored without opening its file. A record inside this root that
                // claims Luke but points into Juri remains a refusal, not an alternate destination.
                string outside = Path.Combine(fixture, "outside.zip"); File.WriteAllBytes(outside, good);
                ArchiveInstallFile external = Json.Read<ArchiveInstallFile>(recordsFile);
                external.Records.Add(new ArchiveInstallRecord { Id = "outside", Path = outside, SourcePath = renamed, Character = "Luke", Sha256 = updated.Sha256, Size = good.Length });
                Json.Write(recordsFile, external);
                using (var locked = new FileStream(outside, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    Require(ArchiveInstalls.Install(renamed, costume, "Luke", "Fixture").Folder == first.Folder, "an outside record redirected or blocked the current reinstall");
                }
                Require(File.ReadAllBytes(outside).SequenceEqual(good), "an outside archive was changed");
                File.WriteAllBytes(recordsFile, records);
                ArchiveInstallFile bad = Json.Read<ArchiveInstallFile>(recordsFile);
                bad.Records.Single(r => r.Character == "Luke").Path = juri.Folder;
                Json.Write(recordsFile, bad);
                byte[] juriBefore = File.ReadAllBytes(juri.Folder);
                RefuseArchive(delegate { ArchiveInstalls.Install(renamed, costume, "Luke", "Fixture"); }, "outside");
                Require(File.ReadAllBytes(juri.Folder).SequenceEqual(juriBefore) && File.ReadAllBytes(first.Folder).SequenceEqual(good), "a different character's destination was changed");
                File.WriteAllBytes(recordsFile, records);

                string extra = Path.Combine(Path.GetDirectoryName(first.Folder), "Extra.zip"); File.Copy(first.Folder, extra);
                ArchiveInstallFile ambiguous = Json.Read<ArchiveInstallFile>(recordsFile);
                ambiguous.Records.Add(new ArchiveInstallRecord { Id = "extra", Path = extra, SourcePath = renamed, Character = "Luke", Sha256 = updated.Sha256, Size = good.Length, WriteTicks = File.GetLastWriteTimeUtc(extra).Ticks });
                Json.Write(recordsFile, ambiguous);
                RefuseArchive(delegate { ArchiveInstalls.Install(renamed, costume, "Luke", "Fixture"); }, "More than one");
                Require(File.ReadAllBytes(first.Folder).SequenceEqual(good) && File.ReadAllBytes(extra).SequenceEqual(good) && !File.Exists(ArchiveInstalls.Destination(costume, "Luke", renamed)), "ambiguous reinstall changed an archive or added another");
            }
            finally { ArchiveInstalls.DataDirOverride = oldData; HistoryStore.DefaultDir = oldHistory; Library.IdentityCacheDirectory = oldCache; }
        }

        private static void ArchiveOtherGameRoots(string root)
        {
            string oldData = ArchiveInstalls.DataDirOverride, oldHistory = HistoryStore.DefaultDir;
            try
            {
                foreach (bool keepOldCopy in new[] { true, false })
                {
                    string fixture = Path.Combine(root, keepOldCopy ? "archive-old-game-present" : "archive-old-game-absent");
                    string source = Path.Combine(fixture, "library", "Shared.zip"), oldRoot = Path.Combine(fixture, "old-game", "reframework", "costume_mods"), newRoot = Path.Combine(fixture, "new-game", "reframework", "costume_mods");
                    Directory.CreateDirectory(Path.GetDirectoryName(source)); File.WriteAllText(source, "old archive contents");
                    ArchiveInstalls.DataDirOverride = Path.Combine(fixture, "Data"); HistoryStore.DefaultDir = Path.Combine(fixture, "history");
                    InstallResult oldCopy = ArchiveInstalls.Install(source, oldRoot, "Luke", "Shared");
                    string recordsFile = Path.Combine(ArchiveInstalls.DataDirOverride, ArchiveInstalls.FileName);
                    ArchiveInstallRecord oldRecord = Json.Read<ArchiveInstallFile>(recordsFile).Records.Single();
                    string oldJson = Json.Serializer().Serialize(oldRecord); byte[] oldBytes = File.ReadAllBytes(oldCopy.Folder);
                    if (!keepOldCopy) File.Delete(oldCopy.Folder);
                    File.WriteAllText(source, "updated archive for the new game");
                    InstallResult newCopy = ArchiveInstalls.Install(source, newRoot, "Luke", "Shared");
                    Require(newCopy.Folder == ArchiveInstalls.Destination(newRoot, "Luke", source) && File.ReadAllBytes(newCopy.Folder).SequenceEqual(File.ReadAllBytes(source)), "an old game's record blocked or redirected the new install");
                    ArchiveInstallFile both = Json.Read<ArchiveInstallFile>(recordsFile);
                    Require(both.Records.Count == 2 && Json.Serializer().Serialize(both.Records.Single(r => r.Id == oldRecord.Id)) == oldJson, "install changed the old game's record");
                    ArchiveInstallRecord newRecord = both.Records.Single(r => r.Id != oldRecord.Id);
                    Require(ArchiveInstalls.Install(source, newRoot, "Luke", "Shared").Folder == newCopy.Folder
                        && Json.Read<ArchiveInstallFile>(recordsFile).Records.Any(r => r.Id == newRecord.Id && r.Path == newCopy.Folder), "the old record interfered with the new game's reinstall");
                    Require(keepOldCopy ? File.ReadAllBytes(oldCopy.Folder).SequenceEqual(oldBytes) : !File.Exists(oldCopy.Folder), "the old game's present or absent copy was changed");
                }
            }
            finally { ArchiveInstalls.DataDirOverride = oldData; HistoryStore.DefaultDir = oldHistory; }
        }

        private static void RefuseArchive(Action action, string message)
        {
            try { action(); }
            catch (IOException ex) { Require(ex.Message.IndexOf(message, StringComparison.OrdinalIgnoreCase) >= 0, "unexpected refusal: " + ex.Message); return; }
            throw new InvalidOperationException("Archive install was not refused: " + message);
        }

        private static T Get<T>(object o, string name) { FieldInfo field = o.GetType().GetField(name, PrivateInstance); if (field == null) throw new MissingFieldException(name); return (T)field.GetValue(o); }
        private static void Set(object o, string name, object value) { o.GetType().GetField(name, PrivateInstance).SetValue(o, value); }
        private static object Call(object o, string name, params object[] args)
        {
            MethodInfo m = o.GetType().GetMethods(PrivateInstance | BindingFlags.Public).First(x => x.Name == name && x.GetParameters().Length == args.Length);
            return m.Invoke(o, args);
        }
        private static void WaitIdle(MainForm form)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(60);
            do { Application.DoEvents(); Thread.Sleep(10); if (!form.Working) { Application.DoEvents(); if (!form.Working) return; } } while (DateTime.UtcNow < deadline);
            throw new InvalidOperationException("Background work did not finish.");
        }
        private static void Render(MainForm form, string file)
        {
            form.PerformLayout(); Application.DoEvents();
            using (var bitmap = new Bitmap(form.ClientSize.Width, form.ClientSize.Height)) { form.DrawToBitmap(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height)); bitmap.Save(file, ImageFormat.Png); }
        }
        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    }
}
