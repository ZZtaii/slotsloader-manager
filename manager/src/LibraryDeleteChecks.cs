using System;
using System.Collections.Generic;
using System.ComponentModel;
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
    // 2.4.1 library Delete (MainFormDelete.cs) in a hidden window on generated fixtures: a stub Recycle Bin (the real
    // one is never called to recycle or delete), a fixture Fluffy installed.ini, managed extra outfits.
    internal static class LibraryDeleteChecks
    {
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        private const string Mesh = "natives/stm/product/model/esf/esf032/001/01/esf032_001_01.mesh.230110883";

        // Moves what it "recycles" into its own folder, so the fixture shows where it went.
        private sealed class StubBin : IRecycleBin
        {
            internal string Folder; internal string UnavailableReason; internal Exception ThrowNext;
            internal readonly List<string> Recycled = new List<string>(), Permanent = new List<string>();
            internal int Queries;
            public string Unavailable(string path, bool folder, long bytes) { Queries++; return UnavailableReason; }
            public void Recycle(string path, bool folder)
            {
                if (ThrowNext != null) { Exception e = ThrowNext; ThrowNext = null; throw e; }
                Recycled.Add(path);
                string target = Path.Combine(Folder, Recycled.Count + " " + Path.GetFileName(path));
                if (folder) Directory.Move(path, target); else File.Move(path, target);
            }
            public void DeletePermanently(string path, bool folder) { Permanent.Add(path); if (folder) Directory.Delete(path, true); else File.Delete(path); }
        }

        internal static int Run(string outputDirectory)
        {
            string root = Path.Combine(Path.GetFullPath(outputDirectory), "delete-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Safety.NoReparseChain(root); Directory.CreateDirectory(root);
            var report = new List<string>(); int failures = 0;
            bool previousHeadless = Program.Headless; Program.Headless = true;
            SynchronizationContext previous = SynchronizationContext.Current;
            bool previousAutoInstall = WindowsFormsSynchronizationContext.AutoInstall;
            WindowsFormsSynchronizationContext.AutoInstall = false;
            IRecycleBin previousBin = RecycleBin.Current;
            var bin = new StubBin { Folder = Path.Combine(root, "stub-recycle-bin") }; Directory.CreateDirectory(bin.Folder);
            RecycleBin.Current = bin;
            string previousIndex = Library.IdentityCacheDirectory; Library.IdentityCacheDirectory = Path.Combine(root, "index");
            bool previousAutoRefresh = MainForm.FluffyAutoRefresh; MainForm.FluffyAutoRefresh = false;
            int popups = 0; DialogResult answer = DialogResult.Cancel; var asked = new List<string>();
            ThemedMessageBox.TestResponder = delegate(ThemedMessageForm box) { popups++; asked.Add(box.Caption + "|" + box.MessageText); return answer; };
            try
            {
                string lib = Path.Combine(root, "library"), fluffyRoot = Path.Combine(root, "Fluffy", "Games", "SF6"), fluffyMods = Path.Combine(fluffyRoot, "Mods");
                string outside = Path.Combine(root, "outside"), game = Path.Combine(root, "game");
                foreach (string name in new[] { "Alpha", "Bravo", "Charlie", "Echo", "India", "Kilo", "Lima" }) Zip(Path.Combine(lib, name + ".zip"), name, root);
                Put(Path.Combine(lib, "Delta"), "Main/modinfo.ini", "name=Delta main\n"); Put(Path.Combine(lib, "Delta"), "Main/" + Mesh, "delta mesh");
                Put(Path.Combine(lib, "Delta"), "readme.txt", "read-only file"); File.SetAttributes(Path.Combine(lib, "Delta", "readme.txt"), FileAttributes.ReadOnly);
                Put(Path.Combine(lib, "Hotel"), "Main/modinfo.ini", "name=Hotel main\n"); Put(Path.Combine(lib, "Hotel"), "Main/" + Mesh, "hotel mesh");
                Zip(Path.Combine(fluffyMods, "Fluffy Mod.zip"), "Fluffy Mod", root);
                File.WriteAllText(Path.Combine(fluffyRoot, "installed.ini"), "[Fluffy Mod]\r\nModName=Fluffy Mod\r\ninstalltype=invalidate\r\nfile=natives/stm/fluffy.tex\r\n");
                Zip(Path.Combine(outside, "Outside.zip"), "Outside", root);
                var config = new AppConfig { GameRoot = game, Libraries = new List<string> { lib, fluffyMods }, SevenZipPath = "" };
                Managed(Path.Combine(config.CostumeRoot, "Juri", "Echo outfit"), "Echo outfit", Path.Combine(lib, "Echo.zip"));
                Managed(Path.Combine(config.CostumeRoot, "Juri", "Foxtrot outfit"), "Foxtrot outfit", Path.Combine(lib, "Foxtrot.zip"));   // its source was never in the library
                Managed(Path.Combine(config.CostumeRoot, "Juri", "Kilo outfit"), "Kilo outfit", Path.Combine(lib, "Kilo.zip"));   // 1.0.5 Rename
                // A junction as a mod and a folder mod with a junction inside, both pointing outside the library.
                Put(Path.Combine(outside, "target"), "keep.txt", "outside the library");
                Put(Path.Combine(outside, "target2"), "keep.txt", "outside the library");
                Put(Path.Combine(lib, "Golf"), "Main/modinfo.ini", "name=Golf main\n");
                bool junctions = Junction(Path.Combine(lib, "Link"), Path.Combine(outside, "target")) && Junction(Path.Combine(lib, "Golf", "inner"), Path.Combine(outside, "target2"));

                using (var ui = new WindowsFormsSynchronizationContext())
                {
                    SynchronizationContext.SetSynchronizationContext(ui);
                    using (var form = new MainForm(config))
                    {
                        Set(form, "firstLoad", false); form.ShowInTaskbar = false; form.Opacity = 0; form.MinimumSize = Size.Empty; form.Show(); Application.DoEvents();
                        form.ClientSize = new Size(DpiMetrics.Px(form, 1232), DpiMetrics.Px(form, 640)); Application.DoEvents();
                        Set(form, "library", Library.Scan(config.Libraries, ""));
                        Set(form, "installed", Installer.ListInstalled(config.CostumeRoot));
                        FluffyInstalls fluffy = FluffyInstalls.Load(new[] { fluffyRoot });
                        Require(fluffy.Mods.Count == 1, "fixture installed.ini: " + fluffy.Mods.Count + " mods");
                        Set(form, "gameRows", new List<GameModRow> { new GameModRow { Kind = "Fluffy", Fluffy = fluffy.Mods[0] } });
                        Call(form, "ShowPage", Get<Panel>(form, "libraryPage")); Call(form, "ApplyFilters"); Call(form, "FillInstalled"); Application.DoEvents();
                        report.Add("Hidden window at " + form.DeviceDpi + " DPI; generated fixtures under " + root + "; stub Recycle Bin " + bin.Folder + (junctions ? "" : "; junctions unavailable (mklink /J failed), the link cases use the static check only"));
                        Action<string, Action> check = delegate(string name, Action action)
                        {
                            try { action(); report.Add("PASS " + name); }
                            catch (Exception ex) { failures++; report.Add("FAIL " + name + ": " + ex); }
                        };
                        check("menu: Open, Open in Explorer, separator, the uninstall items, separator, Delete last in the danger colour; Delete archive / Delete folder; disabled while busy; not offered for an unreadable library row", delegate { MenuShape(form); });
                        check("rename (1.0.5): Rename after Open in Explorer (F2); edited on the row; Enter renames the archive and the installed outfit's manifest, entry and history follow; Esc cancels; a taken or invalid name and a Fluffy-installed mod are refused inline; a failed record write puts everything back; a case-only rename works", delegate { Rename(form, lib, config, root); });
                        check("rename records (1.0.5): Replace-original records and Install all variants records take the new path under the game lock", delegate { RenameRecords(lib, config, root); });
                        check("rename metadata: valid archive cache survives, stale identities are rejected, filename guesses refresh, managed manifest discovery preserves scope", delegate { RenameMetadataChecks.Run(root); });
                        check("rename UI: native click-away saves before opening the clicked row; options, typed name, target and replacement edit survive a rename", delegate { ManagerImprovementChecks.RenameUi(form, lib, config, root); });
                        check("folder freshness: scans run off the UI thread, superseded checks cancel, switching mods ignores old results, Install revalidates; activation leaves an unchanged archive alone and reloads a changed one",delegate { ManagerImprovementChecks.FolderReview(form, lib, config, root); });
                        check("Fluffy: a mod Fluffy's installed.ini lists is refused inline (" + LibraryDelete.FluffyRefusal + "); nothing deleted, row kept", delegate { FluffyRefused(form, bin, Path.Combine(fluffyMods, "Fluffy Mod.zip")); });
                        check("scope: a source outside the libraries and a library root are refused inline; .. escapes, case and trailing separators resolve; a folder holding a library or the game folder is refused", delegate { Scope(form, bin, lib, outside, game); });
                        if (junctions) check("links: a junction mod and a folder mod with a junction inside are refused; the junction targets are untouched", delegate { Links(form, bin, lib, outside); });
                        check("in use: an archive held open (FileShare.None) and a file inside a folder mod give a red inline error with the reason; a shell IOException (stub) too; nothing removed from the list or the disk", delegate { Locked(form, bin, lib); });
                        check("zip: Delete archive sends it to the (stub) Recycle Bin with no popup; the row goes at once (no rescan), counts follow, selection moves to the next row, the open mod's inspector clears, the library index forgets it", delegate { DeleteZip(form, bin, lib, root, ref popups); });
                        check("folder: Delete folder (with a read-only file) goes to the Recycle Bin and its row goes", delegate { DeleteFolder(form, bin, lib); });
                        check("multi-mod archive: three library entries from one archive read Delete archive (3 mods); one delete removes all three rows", delegate { DeleteMulti(form, bin, lib); });
                        check("installed: deleting the source of an installed outfit leaves it installed; its Installed row and details say Source deleted; a source missing from its library says Source missing", delegate { Installed(form, bin, lib, config, root); });
                        check("no popup in the normal flow (ThemedMessageBox.TestResponder never called)", delegate { Require(popups == 0, "popups: " + String.Join(" / ", asked)); });
                        check("Recycle Bin unavailable: the one question (Delete permanently?); Cancel keeps it; Delete permanently deletes it and its row", delegate { Permanent(form, bin, lib, ref answer, ref popups, asked); });
                        form.Close();
                    }
                }
                check2(report, ref failures, "Recycle Bin detection (read-only: SHQueryRecycleBin, drive type, BitBucket settings; nothing is recycled)", delegate { Detection(root, report); });
            }
            catch (Exception ex) { failures++; report.Add("FAIL setup: " + ex); }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous); WindowsFormsSynchronizationContext.AutoInstall = previousAutoInstall;
                Program.Headless = previousHeadless; RecycleBin.Current = previousBin; ThemedMessageBox.TestResponder = null;
                Library.IdentityCacheDirectory = previousIndex; MainForm.FluffyAutoRefresh = previousAutoRefresh;
            }
            report.Add(failures == 0 ? "ALL PASS" : failures + " FAILED");
            File.WriteAllLines(Path.Combine(root, "library-delete-checks.txt"), report);
            Console.WriteLine(String.Join(Environment.NewLine, report));
            return failures == 0 ? 0 : 1;
        }
        private static void check2(List<string> report, ref int failures, string name, Action action)
        {
            try { action(); report.Add("PASS " + name); }
            catch (Exception ex) { failures++; report.Add("FAIL " + name + ": " + ex); }
        }

        // ---- checks -------------------------------------------------------------------------------------------------
        private static void MenuShape(MainForm form)
        {
            ContextMenuStrip menu = Get<ListView>(form, "modList").ContextMenuStrip;
            ToolStripMenuItem delete = OpenMenu(form, Entry(form, "Alpha"));
            string[] texts = menu.Items.Cast<ToolStripItem>().Select(i => i is ToolStripSeparator ? "|" : i.Text).ToArray();
            Require(menu.Items.Count == 9 && texts[0] == "Open" && texts[1] == "Open in Explorer" && texts[2] == "Rename" && texts[3] == "|" && texts[7] == "|" && Object.ReferenceEquals(delete, menu.Items[8]), "menu items: " + String.Join(", ", texts));
            Require(delete.Text == "Delete archive" && delete.Available && delete.Enabled && delete.ForeColor == Palette.Red && menu.Items[7].Available, "zip item: " + delete.Text + " available " + delete.Available + " enabled " + delete.Enabled + " colour " + delete.ForeColor);
            Require(OpenMenu(form, Entry(form, "Delta")).Text == "Delete folder", "folder item: " + delete.Text);
            Call(form, "SetBusy", true, "Fixture busy");
            try { Require(!OpenMenu(form, Entry(form, "Alpha")).Enabled, "Delete is enabled while busy"); }
            finally { Call(form, "SetBusy", false, "Ready"); }
            var unreadable = new LibraryEntry { Name = "Missing library", Kind = "Library", SourcePath = @"C:\fixture\missing-library", Detection = "Error" };
            OpenMenu(form, unreadable);
            Require(!delete.Available && !menu.Items[7].Available && !menu.Items[2].Available, "Delete or Rename offered for an unreadable library row");
        }

        // ---- 1.0.5 Rename
        private static void Rename(MainForm form, string lib, AppConfig config, string root)
        {
            string previousHistory = HistoryStore.DefaultDir; HistoryStore.DefaultDir = Path.Combine(root, "rename-history");
            try
            {
                string kilo = Path.Combine(lib, "Kilo.zip"), renamed = Path.Combine(lib, "Kilo Renamed.zip");
                string manifest = Path.Combine(config.CostumeRoot, "Juri", "Kilo outfit", ".slots-installer.json");
                History.Record(null, new HistoryEvent { Kind = HistoryKinds.Install, Mode = HistoryModes.Extra, Source = kilo, Name = "Kilo outfit", Slot = "Juri/Kilo outfit" });
                LibraryEntry entry = Entry(form, "Kilo");
                ContextMenuStrip menu = Get<ListView>(form, "modList").ContextMenuStrip;
                OpenMenu(form, entry);
                ToolStripItem item = menu.Items[2];
                Require(item.Text == "Rename" && item.Available && item.Enabled && ((ToolStripMenuItem)item).ShortcutKeyDisplayString == "F2", "Rename item: " + item.Text + " " + item.Available + " " + item.Enabled);
                // Esc: nothing changes.
                form.BeginLibraryRename(entry);
                TextBox box = Get<TextBox>(form, "renameBox");
                Require(box != null && box.Text == "Kilo" && box.Parent == Get<ListView>(form, "modList"), "rename box: " + (box == null ? "none" : box.Text));
                box.Text = "Not this"; form.EndLibraryRename(false); WaitIdle(form);
                Require(File.Exists(kilo) && Get<TextBox>(form, "renameBox") == null && entry.Name == "Kilo" && form.RenameRuns == 0, "Esc renamed or kept the box");
                // Refusals, inline: a taken name, an invalid one, a mod Fluffy installed.
                form.RenameLibrarySource(entry, "Alpha"); WaitIdle(form);
                Require(Status(form) == "Can't rename Kilo.zip: Alpha.zip already exists in that folder." && File.Exists(kilo), "taken: " + Status(form));
                form.RenameLibrarySource(entry, "a:b"); WaitIdle(form);
                Require(Status(form).StartsWith("Can't rename Kilo.zip: a name can't contain", StringComparison.Ordinal) && File.Exists(kilo), "invalid: " + Status(form));
                form.BeginLibraryRename(Entry(form, "Fluffy Mod"));
                Require(Status(form) == MainForm.RenameFluffyRefusal && Get<TextBox>(form, "renameBox") == null, "Fluffy: " + Status(form));
                Require(form.RenameRuns == 0, "a refusal reached the worker");
                // A record that can't be written: the file name and every record are put back.
                string before = File.ReadAllText(manifest);
                File.SetAttributes(manifest, FileAttributes.ReadOnly);
                try { form.RenameLibrarySource(entry, "Kilo Renamed"); WaitIdle(form); }
                finally { File.SetAttributes(manifest, FileAttributes.Normal); }
                Require(Status(form).StartsWith("Couldn't rename Kilo.zip: ", StringComparison.Ordinal) && StatusColor(form) == Palette.Red && File.Exists(kilo) && !File.Exists(renamed) && File.ReadAllText(manifest) == before && entry.SourcePath == kilo, "rollback: " + Status(form));
                // Enter on the row: renamed, with the installed outfit, the entry and the history.
                form.BeginLibraryRename(entry);
                Get<TextBox>(form, "renameBox").Text = "Kilo Renamed.zip";   // typing the extension again is fine
                form.EndLibraryRename(true); WaitIdle(form);
                Require(Status(form) == "Renamed Kilo.zip to Kilo Renamed.zip. Installed copies now point to it." && StatusColor(form) == Palette.Green, "status: " + Status(form));
                Require(!File.Exists(kilo) && File.Exists(renamed) && entry.SourcePath == renamed && entry.Name == "Kilo Renamed" && Listed(form).Contains("Kilo Renamed") && !Listed(form).Contains("Kilo"), "entry: " + entry.SourcePath + " / " + entry.Name);
                Require(LibraryDelete.Same(Json.Read<InstallManifest>(manifest).SourcePath, renamed), "manifest: " + Json.Read<InstallManifest>(manifest).SourcePath);
                Require(((List<InstalledOutfit>)Get<object>(form, "installed")).Any(o => o.Name == "Kilo outfit" && LibraryDelete.Same(o.SourcePath, renamed)), "the Installed list kept the old path");
                Require(form.SourceGoneNote(renamed) == null, "Installed says " + form.SourceGoneNote(renamed));
                HistoryIndex index = HistoryStore.Default.Index();
                Require(index.Find(History.Normalize(renamed)) != null && index.Find(History.Normalize(renamed)).LastInstalled.HasValue && index.Find(History.Normalize(kilo)) == null, "history did not follow the file");
                Require((string)typeof(MainForm).GetMethod("Verb", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { new HistoryEvent { Kind = HistoryKinds.Rename } }) == "Renamed", "History page verb");
                // Case only.
                form.RenameLibrarySource(entry, "kilo renamed"); WaitIdle(form);
                Require(Directory.GetFiles(lib, "kilo renamed.zip").Select(Path.GetFileName).SequenceEqual(new[] { "kilo renamed.zip" }) && entry.Name == "kilo renamed", "case-only: " + String.Join(", ", Directory.GetFiles(lib, "kilo*").Select(Path.GetFileName)) + " / " + Status(form));
            }
            finally { HistoryStore.DefaultDir = previousHistory; }
        }

        private static void RenameRecords(string lib, AppConfig config, string root)
        {
            string data = Path.Combine(root, "rename-data"); Directory.CreateDirectory(data);
            string lima = Path.Combine(lib, "Lima.zip"), next = Path.Combine(lib, "Lima 2.zip");
            string game = config.GameRoot; Directory.CreateDirectory(game);
            var engine = new ReplaceEngine(game, data, new ReplaceSettings());
            var state = new ReplaceState { GameRoot = engine.GameRoot, Mods = new List<ReplaceMod> { new ReplaceMod { Id = "lima1", Name = "Lima", SourcePath = lima }, new ReplaceMod { Id = "other", Name = "Other", SourcePath = Path.Combine(lib, "Alpha.zip") } } };
            DurableFile.WriteJson(engine.StatePath, state);
            string previous = ArchiveInstalls.DataDirOverride; ArchiveInstalls.DataDirOverride = data;
            string previousHistory = HistoryStore.DefaultDir; HistoryStore.DefaultDir = Path.Combine(root, "rename-history-2");
            try
            {
                Json.Write(Path.Combine(data, ArchiveInstalls.FileName), new ArchiveInstallFile { Records = new List<ArchiveInstallRecord> { new ArchiveInstallRecord { Id = "a1", Path = Path.Combine(config.CostumeRoot, "Juri", "Lima.zip"), SourcePath = lima, Character = "Juri" } } });
                SourceRename.Outcome o = SourceRename.Run(lima, "Lima 2", config.Libraries, new KeyValuePair<string, string>[0], config.CostumeRoot, delegate { return engine; });
                Require(o.Done && o.Replacements == 1 && o.ArchiveCopies == 1 && File.Exists(next) && !File.Exists(lima), "outcome: " + o.Error + " replacements " + o.Replacements + " copies " + o.ArchiveCopies);
                ReplaceState after = engine.LoadState();
                Require(LibraryDelete.Same(after.Find("lima1").SourcePath, next) && LibraryDelete.Same(after.Find("other").SourcePath, Path.Combine(lib, "Alpha.zip")), "state: " + after.Find("lima1").SourcePath);
                ArchiveInstallFile records = Json.Read<ArchiveInstallFile>(Path.Combine(data, ArchiveInstalls.FileName));
                Require(LibraryDelete.Same(records.Records.Single().SourcePath, next), "archive record: " + records.Records.Single().SourcePath);
                // A change to the game folder that didn't finish (a journal) blocks it; nothing moves.
                File.WriteAllText(engine.JournalPath, "{}");
                try
                {
                    SourceRename.Outcome blocked = SourceRename.Run(next, "Lima 3", config.Libraries, new KeyValuePair<string, string>[0], config.CostumeRoot, delegate { return engine; });
                    Require(!blocked.Done && File.Exists(next) && blocked.Error.Contains("didn't finish"), "journal: " + blocked.Error);
                }
                finally { File.Delete(engine.JournalPath); }
            }
            finally { ArchiveInstalls.DataDirOverride = previous; HistoryStore.DefaultDir = previousHistory; }
        }

        private static void FluffyRefused(MainForm form, StubBin bin, string zip)
        {
            int runs = form.DeleteRuns, recycled = bin.Recycled.Count;
            LibraryEntry entry = Entry(form, "Fluffy Mod");
            Click(form, entry);
            Require(Status(form) == LibraryDelete.FluffyRefusal && File.Exists(zip) && Listed(form).Contains("Fluffy Mod") && form.DeleteRuns == runs && bin.Recycled.Count == recycled, "Fluffy mod: '" + Status(form) + "', exists " + File.Exists(zip));
        }

        private static void Scope(MainForm form, StubBin bin, string lib, string outside, string game)
        {
            int recycled = bin.Recycled.Count;
            var stray = new LibraryEntry { Name = "Outside", Kind = "ZIP", SourcePath = Path.Combine(outside, "Outside.zip") };
            var libraryItself = new LibraryEntry { Name = "library", Kind = "Folder", SourcePath = lib };
            AddEntries(form, stray, libraryItself);
            Click(form, stray);
            Require(Status(form).StartsWith("Can't delete Outside.zip: it is not in one of your libraries", StringComparison.Ordinal) && File.Exists(stray.SourcePath) && Listed(form).Contains("Outside"), "outside: " + Status(form));
            Click(form, libraryItself);
            Require(Status(form).StartsWith("Can't delete library: it is a library folder", StringComparison.Ordinal) && Directory.Exists(lib), "library root: " + Status(form));
            Require(bin.Recycled.Count == recycled && bin.Queries == 0, "the stub was reached for a refused path");
            RemoveEntries(form, stray, libraryItself);
            // The path rules on their own.
            var none = new KeyValuePair<string, string>[0]; string full; bool folder;
            Require(LibraryDelete.ScopeError(Path.Combine(lib, @"..\outside\Outside.zip"), new[] { lib }, none, out full, out folder) != null, ".. escaped the library");
            Require(LibraryDelete.ScopeError(lib.ToUpperInvariant() + @"\ALPHA.ZIP", new[] { lib.ToLowerInvariant() + @"\" }, none, out full, out folder) == null && !folder, "case or a trailing separator refused a library mod");
            Require(LibraryDelete.ScopeError(Path.Combine(lib, "Delta"), new[] { lib }, none, out full, out folder) == null && folder, "a folder mod was refused");
            Require(LibraryDelete.ScopeError(lib, new[] { Path.GetDirectoryName(lib), lib }, none, out full, out folder) != null, "a library inside another library was deletable");
            Require(LibraryDelete.ScopeError(game, new[] { Path.GetDirectoryName(game) }, new[] { new KeyValuePair<string, string>("the game folder", game) }, out full, out folder) != null, "the game folder was deletable from a library above it");
            Require(LibraryDelete.ScopeError(Path.Combine(lib, "Gone.zip"), new[] { lib }, none, out full, out folder) != null, "a missing source was accepted");
            Require(LibraryDelete.ScopeError(Path.GetPathRoot(lib), new[] { Path.GetPathRoot(lib) }, none, out full, out folder) != null, "a drive root was deletable");
        }

        private static void Links(MainForm form, StubBin bin, string lib, string outside)
        {
            int recycled = bin.Recycled.Count;
            var link = new LibraryEntry { Name = "Link", Kind = "Folder", SourcePath = Path.Combine(lib, "Link") };   // the scan skips links; a stale row could still name one
            AddEntries(form, link);
            Click(form, link);
            Require(Status(form).StartsWith("Can't delete Link: it is a link", StringComparison.Ordinal), "junction mod: " + Status(form));
            RemoveEntries(form, link);
            Click(form, Entry(form, "Golf"));
            Require(Status(form).StartsWith("Can't delete Golf: it contains a link (inner)", StringComparison.Ordinal) || Status(form).StartsWith("Couldn't delete Golf: it contains a link (inner)", StringComparison.Ordinal), "folder with a junction: " + Status(form));
            Require(Directory.Exists(Path.Combine(lib, "Golf")) && File.Exists(Path.Combine(outside, "target", "keep.txt")) && File.Exists(Path.Combine(outside, "target2", "keep.txt")) && bin.Recycled.Count == recycled && Listed(form).Contains("Golf"), "a link case changed something");
        }

        private static void Locked(MainForm form, StubBin bin, string lib)
        {
            int recycled = bin.Recycled.Count; int before = Listed(form).Count;
            string alpha = Path.Combine(lib, "Alpha.zip");
            using (new FileStream(alpha, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Click(form, Entry(form, "Alpha"));
            }
            Require(Status(form) == "Couldn't delete Alpha.zip: it is in use by another program. Close it and try again." && StatusColor(form) == Palette.Red, "locked archive: " + Status(form) + " / " + StatusColor(form));
            Require(File.Exists(alpha) && Listed(form).Contains("Alpha") && Listed(form).Count == before && bin.Recycled.Count == recycled, "a locked archive changed the list or the disk");
            string inner = Path.Combine(lib, "Hotel", "Main", Mesh.Replace('/', '\\'));
            using (new FileStream(inner, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Click(form, Entry(form, "Hotel"));
            }
            Require(Status(form).StartsWith("Couldn't delete Hotel: Main\\natives", StringComparison.Ordinal) && Status(form).EndsWith("is in use by another program. Close it and try again.", StringComparison.Ordinal) && StatusColor(form) == Palette.Red, "locked file in a folder: " + Status(form));
            Require(Directory.Exists(Path.Combine(lib, "Hotel")) && Listed(form).Contains("Hotel") && bin.Recycled.Count == recycled, "a locked folder changed the list or the disk");
            // The shell's own failure (the real call throws IOException when the file is locked after the check).
            bin.ThrowNext = new IOException("The process cannot access the file because it is being used by another process.");
            Click(form, Entry(form, "Alpha"));
            Require(Status(form) == "Couldn't delete Alpha.zip: The process cannot access the file because it is being used by another process." && StatusColor(form) == Palette.Red && File.Exists(alpha) && Listed(form).Contains("Alpha"), "shell IOException: " + Status(form));
        }

        private static void DeleteZip(MainForm form, StubBin bin, string lib, string root, ref int popups)
        {
            string bravo = Path.Combine(lib, "Bravo.zip"), index = Path.Combine(root, "index", Library.CacheFilename);
            Require(File.Exists(index) && File.ReadAllText(index).Contains("Bravo.zip"), "the library index has no Bravo record to forget");
            LibraryEntry entry = Entry(form, "Bravo");
            Call(form, "LoadPackage", entry); WaitIdle(form);
            Require(Get<object>(form, "package") != null && Get<Label>(form, "packageTitle").Text != "Choose a mod", "Bravo did not open");
            List<string> rows = Listed(form); int at = rows.IndexOf("Bravo"); string after = rows[at + 1];
            int total = ((List<LibraryEntry>)Get<object>(form, "library")).Count;
            ListView list = Get<ListView>(form, "modList");
            Require(list.SelectedItems.Count == 1 && ((LibraryEntry)list.SelectedItems[0].Tag).Name == "Bravo", "Bravo is not the selected row");
            int scans = Library.CacheMisses;
            Click(form, entry);
            Require(Status(form) == "Moved Bravo.zip to the Recycle Bin." && StatusColor(form) == Palette.Green, "status: " + Status(form));
            Require(bin.Recycled.Count > 0 && LibraryDelete.Same(bin.Recycled.Last(), bravo) && !File.Exists(bravo) && File.Exists(Path.Combine(bin.Folder, bin.Recycled.Count + " Bravo.zip")), "Bravo is not in the stub Recycle Bin");
            Require(!Listed(form).Contains("Bravo") && Listed(form).Count == rows.Count - 1 && ((List<LibraryEntry>)Get<object>(form, "library")).Count == total - 1, "the row or the entry stayed");
            Require(Get<Label>(form, "libraryCount").Text == (total - 1) + " MODS", "count: " + Get<Label>(form, "libraryCount").Text);
            Require(list.SelectedItems.Count == 1 && ((LibraryEntry)list.SelectedItems[0].Tag).Name == after, "selection did not move to the next row (" + after + ")");
            Require(Get<object>(form, "package") == null && Get<Label>(form, "packageTitle").Text == "Choose a mod", "the inspector still shows Bravo");
            Require(!File.ReadAllText(index).Contains("Bravo.zip"), "the library index still has Bravo");
            Require(Library.CacheMisses == scans, "the delete rescanned the library");
            Call(form, "ApplyFilters");
            Require(!Listed(form).Contains("Bravo"), "Bravo came back on the next filter pass");
            Require(!Library.Scan(new[] { lib }, "").Any(e => e.Name == "Bravo"), "Bravo is still on disk for a listing");
            Require(popups == 0, "a popup appeared");
        }

        private static void DeleteFolder(MainForm form, StubBin bin, string lib)
        {
            Click(form, Entry(form, "Delta"));
            Require(Status(form) == "Moved Delta to the Recycle Bin." && LibraryDelete.Same(bin.Recycled.Last(), Path.Combine(lib, "Delta")) && !Directory.Exists(Path.Combine(lib, "Delta")) && Directory.Exists(Path.Combine(bin.Folder, bin.Recycled.Count + " Delta")), "folder: " + Status(form));
            Require(!Listed(form).Contains("Delta"), "the folder row stayed");
        }

        private static void DeleteMulti(MainForm form, StubBin bin, string lib)
        {
            LibraryEntry charlie = Entry(form, "Charlie");
            var second = new LibraryEntry { Name = "Charlie (part 2)", Kind = "ZIP", SourcePath = charlie.SourcePath };
            var third = new LibraryEntry { Name = "Charlie (part 3)", Kind = "ZIP", SourcePath = charlie.SourcePath.ToUpperInvariant() };
            AddEntries(form, second, third);
            Require(Listed(form).Count(n => n.StartsWith("Charlie", StringComparison.Ordinal)) == 3, "fixture rows");
            Require(OpenMenu(form, second).Text == "Delete archive (3 mods)", "multi text: " + OpenMenu(form, second).Text);
            int recycled = bin.Recycled.Count;
            Click(form, second);
            Require(Status(form) == "Moved Charlie.zip to the Recycle Bin." && bin.Recycled.Count == recycled + 1 && !File.Exists(charlie.SourcePath), "multi: " + Status(form));
            Require(!Listed(form).Any(n => n.StartsWith("Charlie", StringComparison.Ordinal)) && !((List<LibraryEntry>)Get<object>(form, "library")).Any(e => e.Name.StartsWith("Charlie", StringComparison.Ordinal)), "a Charlie row stayed: " + String.Join(", ", Listed(form)));
        }

        private static void Installed(MainForm form, StubBin bin, string lib, AppConfig config, string root)
        {
            string echo = Path.Combine(lib, "Echo.zip");
            var installed = (List<InstalledOutfit>)Get<object>(form, "installed");
            InstalledOutfit outfit = installed.Single(o => o.Name == "Echo outfit");
            Require(form.SourceGoneNote(echo) == null && form.SourceGoneNote(Path.Combine(lib, "Alpha.zip")) == null, "a listed source has a note");
            Require(form.SourceGoneNote(Path.Combine(lib, "Foxtrot.zip")) == "Source missing", "Foxtrot: " + form.SourceGoneNote(Path.Combine(lib, "Foxtrot.zip")));
            Click(form, Entry(form, "Echo"));
            Require(Status(form) == "Moved Echo.zip to the Recycle Bin." && !File.Exists(echo) && !Listed(form).Contains("Echo"), "Echo: " + Status(form));
            Require(Directory.Exists(outfit.Folder) && File.Exists(Path.Combine(outfit.Folder, "natives", "fixture.bin")) && Installer.ListInstalled(config.CostumeRoot).Any(o => o.Name == "Echo outfit" && o.Managed), "the installed outfit went with its source");
            Require(form.SourceGoneNote(echo) == "Source deleted" && form.SourceGoneNote(echo.ToUpperInvariant()) == "Source deleted", "Echo note: " + form.SourceGoneNote(echo));
            Call(form, "ShowPage", Get<Panel>(form, "installedPage")); Application.DoEvents();
            ListView list = Get<ListView>(form, "installedList");
            ListViewItem row = list.Items.Cast<ListViewItem>().First(i => i.Tag is InstalledOutfit && ((InstalledOutfit)i.Tag).Name == "Echo outfit");
            foreach (ListViewItem other in list.SelectedItems.Cast<ListViewItem>().ToList()) other.Selected = false;
            row.Selected = true; Call(form, "ShowInstalledSelection"); Application.DoEvents();
            ChipStrip chips = Get<ChipStrip>(form, "installedChips");
            Require(chips.Chips.Any(c => c.Text == "Source deleted"), "Echo details: " + String.Join(", ", chips.Chips.Select(c => c.Text)));
            Require(list.Items.Cast<ListViewItem>().Select(i => i.Bounds.Height).Distinct().Count() == 1, "rows of different heights");
            Render(form, Path.Combine(root, "installed-source-deleted.png"));
            Call(form, "ShowPage", Get<Panel>(form, "libraryPage")); Application.DoEvents();
        }

        private static void Permanent(MainForm form, StubBin bin, string lib, ref DialogResult answer, ref int popups, List<string> asked)
        {
            string india = Path.Combine(lib, "India.zip");
            bin.UnavailableReason = "it is on a network drive";
            try
            {
                answer = DialogResult.No;   // the "Cancel" button
                Click(form, Entry(form, "India"));
                Require(popups == 1 && asked.Last().StartsWith("Delete permanently?|The Recycle Bin can't take India.zip: it is on a network drive.", StringComparison.Ordinal), "question: " + String.Join(" / ", asked));
                Require(File.Exists(india) && Listed(form).Contains("India") && bin.Permanent.Count == 0 && Status(form) == "Kept India.zip: the Recycle Bin can't take it (it is on a network drive).", "Cancel: " + Status(form));
                answer = DialogResult.Yes;   // "Delete permanently"
                Click(form, Entry(form, "India"));
                Require(popups == 2 && bin.Permanent.Count == 1 && !File.Exists(india) && !Listed(form).Contains("India") && Status(form) == "Deleted India.zip permanently.", "permanent: " + Status(form));
            }
            finally { bin.UnavailableReason = null; }
        }

        private static void Detection(string root, List<string> report)
        {
            var shell = new ShellRecycleBin();
            Require(shell.Unavailable(@"\\fixture-server\share\Mod.zip", false, 1) == "it is on a network location", "UNC path");
            string local = shell.Unavailable(root, true, 1);
            report.Add("  real detection for the fixture folder (" + Path.GetPathRoot(root) + "): " + (local ?? "Recycle Bin available"));
        }

        // ---- helpers ------------------------------------------------------------------------------------------------
        private static ToolStripMenuItem OpenMenu(MainForm form, LibraryEntry entry)
        {
            ContextMenuStrip menu = Get<ListView>(form, "modList").ContextMenuStrip;
            menu.Tag = entry;
            var args = new CancelEventArgs();
            typeof(ContextMenuStrip).GetMethod("OnOpening", PrivateInstance).Invoke(menu, new object[] { args });   // what the right-click runs, without showing the menu
            Require(!args.Cancel, "the menu did not open for " + entry.Name);
            return (ToolStripMenuItem)menu.Items[menu.Items.Count - 1];
        }
        private static void Click(MainForm form, LibraryEntry entry)
        {
            ToolStripMenuItem delete = OpenMenu(form, entry);
            Require(delete.Available && delete.Enabled, "Delete is not offered for " + entry.Name);
            delete.PerformClick(); WaitIdle(form);
        }
        private static LibraryEntry Entry(MainForm form, string name) { return ((List<LibraryEntry>)Get<object>(form, "library")).Single(e => e.Name == name); }
        private static List<string> Listed(MainForm form) { return Get<ListView>(form, "modList").Items.Cast<ListViewItem>().Select(i => ((LibraryEntry)i.Tag).Name).ToList(); }
        private static void AddEntries(MainForm form, params LibraryEntry[] entries) { Set(form, "library", new List<LibraryEntry>((List<LibraryEntry>)Get<object>(form, "library")).Concat(entries).ToList()); Call(form, "ApplyFilters"); }
        private static void RemoveEntries(MainForm form, params LibraryEntry[] entries) { Set(form, "library", ((List<LibraryEntry>)Get<object>(form, "library")).Where(e => !entries.Contains(e)).ToList()); Call(form, "ApplyFilters"); }
        private static string Status(MainForm form) { return Get<Label>(form, "statusText").Text; }
        private static Color StatusColor(MainForm form) { return Get<Label>(form, "statusText").ForeColor; }
        private static void Zip(string archive, string name, string root)
        {
            string source = Path.Combine(root, "zip-sources", name);
            Put(source, "Main/modinfo.ini", "name=" + name + " main\n"); Put(source, "Main/" + Mesh, name + " mesh");
            Directory.CreateDirectory(Path.GetDirectoryName(archive));
            ZipFile.CreateFromDirectory(source, archive);
        }
        private static void Managed(string folder, string name, string source)
        {
            Directory.CreateDirectory(Path.Combine(folder, "natives")); File.WriteAllText(Path.Combine(folder, "natives", "fixture.bin"), "managed bytes");
            Json.Write(Path.Combine(folder, ".slots-installer.json"), new InstallManifest { Id = Guid.NewGuid().ToString("N"), Character = "Juri", Name = name, SourcePath = source, CreatedUtc = DateTime.UtcNow.ToString("o"), SelectedIds = new List<string> { "Main" }, Sha256 = new Dictionary<string, string> { { "natives/fixture.bin", new string('a', 64) } } });
        }
        private static bool Junction(string link, string target)
        {
            try
            {
                var start = new ProcessStartInfo("cmd.exe", "/c mklink /J \"" + link + "\" \"" + target + "\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                using (Process p = Process.Start(start)) { p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd(); p.WaitForExit(10000); }
                return Directory.Exists(link) && (File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0;
            }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; return false; }
        }
        private static void Put(string root, string path, string contents) { string file = Safety.Under(root, path); Directory.CreateDirectory(Path.GetDirectoryName(file)); File.WriteAllText(file, contents, new UTF8Encoding(false)); }
        private static void Render(MainForm form, string file)
        {
            using (var bitmap = new Bitmap(form.ClientSize.Width, form.ClientSize.Height)) { form.DrawToBitmap(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height)); bitmap.Save(file, ImageFormat.Png); }
        }
        private static T Get<T>(object o, string name) { FieldInfo field = o.GetType().GetField(name, PrivateInstance); if (field == null) throw new MissingFieldException(name); return (T)field.GetValue(o); }
        private static void Set(object o, string name, object value) { o.GetType().GetField(name, PrivateInstance).SetValue(o, value); }
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
    }
}
