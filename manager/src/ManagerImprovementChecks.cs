using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace SlotsInstaller
{
    internal static class ManagerImprovementChecks
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        private const string Mesh = "natives/stm/product/model/esf/esf032/001/01/esf032_001_01.mesh.230110883";
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr h, int m, IntPtr w, IntPtr l);

        internal static void RenameUi(MainForm form, string lib, AppConfig config, string root)
        {
            string source = Path.Combine(lib, "Rename edits.zip"), other = Path.Combine(lib, "Rename next.zip");
            MakeZip(source, Path.Combine(root, "rename-edit-source"));
            MakeZip(other, Path.Combine(root, "rename-next-source"));
            Refresh(form, config);
            LibraryEntry entry = Entry(form, source);
            Call(form, "LoadPackage", entry); Idle(form);
            Call(form, "SetInstallMode", InstallModes.Slot, false);
            Call(form, "FillComponents", new List<string> { "Extra" });
            Call(form, "SetOutfitName", "My unfinished outfit");
            Set(form, "outfitNameTyped", true); Set(form, "outfitNameLocked", true);
            Get<ComboBox>(form, "targetCharacter").SelectedItem = "Ryu";
            Idle(form);
            Rename(form, entry, "Rename edited");
            Require(Get<List<string>>(form, "selectedOrder").SequenceEqual(new[] { "Extra" }), "rename reset the chosen options");
            Require(Get<PaddedTextField>(form, "outfitName").Text == "My unfinished outfit" && Get<bool>(form, "outfitNameTyped") && Get<bool>(form, "outfitNameLocked"), "rename reset the typed outfit name");
            Require(Convert.ToString(Get<ComboBox>(form, "targetCharacter").SelectedItem) == "Ryu" && !form.IsReplaceMode, "rename reset the target/mode");
            Call(form, "FillComponents", new List<string>()); Idle(form);
            Rename(form, entry, "Rename empty");
            Require(Get<List<string>>(form, "selectedOrder").Count == 0, "rename replaced an intentionally empty selection with defaults");

            var edit = new ReplaceMod { Id = "rename-edit-context", Name = "Edited replacement", SourcePath = entry.SourcePath, SelectedIds = new List<string> { "Main" } };
            Call(form, "LoadPackage", entry, null, edit); Idle(form);
            Call(form, "FillComponents", new List<string> { "Extra" }); Idle(form);
            Rename(form, entry, "Rename replacement");
            Require(form.IsReplaceMode && Get<ReplaceMod>(form, "editingReplacement").Id == edit.Id && Get<List<string>>(form, "selectedOrder").SequenceEqual(new[] { "Extra" }), "rename lost replacement editing context");

            ListView list = Get<ListView>(form, "modList");
            LibraryEntry next = Entry(form, other);
            ListViewItem row = list.Items.Cast<ListViewItem>().Single(x => Object.ReferenceEquals(x.Tag, next));
            row.EnsureVisible();
            form.BeginLibraryRename(entry);
            Get<TextBox>(form, "renameBox").Text = "Rename clicked";
            int runs = form.RenameRuns;
            Rectangle bounds = row.Bounds;
            int clickX = bounds.Left + 20, clickY = bounds.Top + bounds.Height / 2;
            IntPtr position = new IntPtr((clickY << 16) | (clickX & 0xffff));
            // Queue both messages: native list drag detection must be able to consume the mouse-up.
            PostMessage(list.Handle, 0x201, new IntPtr(1), position);
            PostMessage(list.Handle, 0x202, IntPtr.Zero, position);
            PumpUntil(delegate { return form.RenameRuns > runs; }, "native click did not commit the inline edit");
            Idle(form);
            Require(File.Exists(Path.Combine(lib, "Rename clicked.zip")) && !File.Exists(Path.Combine(lib, "Rename replacement.zip")), "click-away dropped the rename");
            Require(Get<ModPackage>(form, "package").SourcePath == other, "click-away did not open the requested row after saving");
            Call(form, "ClosePackage");
        }

        internal static void FolderReview(MainForm form, string lib, AppConfig config, string root)
        {
            string folder = Path.Combine(lib, "Fresh folder"), other = Path.Combine(lib, "Fresh other.zip");
            MakeFolder(folder); MakeZip(other, Path.Combine(root, "fresh-other-source")); Refresh(form, config);
            Call(form, "LoadPackage", Entry(form, folder)); Idle(form);
            int uiThread = Thread.CurrentThread.ManagedThreadId, scanThread = 0, calls = 0;
            using (var entered = new ManualResetEvent(false))
            using (var release = new ManualResetEvent(false))
            using (var nextEntered = new ManualResetEvent(false))
            using (var releaseNext = new ManualResetEvent(false))
            {
                try
                {
                    MainForm.BeforeSourceStamp = delegate(string path)
                    {
                        if (path != folder) return;
                        scanThread = Thread.CurrentThread.ManagedThreadId;
                        if (Interlocked.Increment(ref calls) == 1) { entered.Set(); if (!release.WaitOne(10000)) throw new IOException("Freshness test gate timed out."); }
                    };
                    int discarded = form.ReviewDiscarded, reloads = form.SourceReloads;
                    form.RequestReview(1);
                    PumpUntil(delegate { return entered.WaitOne(0); }, "folder check never started");
                    Require(scanThread != uiThread, "folder scan blocked the UI thread");
                    bool responded = false; form.BeginInvoke((Action)delegate { responded = true; });
                    PumpUntil(delegate { return responded; }, "UI did not respond while folder scan was blocked");
                    File.WriteAllText(Path.Combine(folder, "Extra", Mesh), "updated extra file");
                    Call(form, "FillComponents", new List<string> { "Extra" });
                    release.Set(); Idle(form);
                    Require(form.ReviewDiscarded > discarded && form.SourceReloads == reloads + 1 && Get<List<string>>(form, "selectedOrder").SequenceEqual(new[] { "Extra" }), "superseded scan was applied or latest selection lost");

                    // A stale Install reloads without writing an outfit; its preflight is a worker too.
                    reloads = form.SourceReloads; int installed = Installer.ListInstalled(config.CostumeRoot).Count;
                    File.WriteAllText(Path.Combine(folder, "Extra", Mesh), "another changed extra file before install");
                    Call(form, "InstallCurrent"); Idle(form);
                    Require(scanThread != uiThread && form.SourceReloads == reloads + 1 && Installer.ListInstalled(config.CostumeRoot).Count == installed, "Install did not revalidate asynchronously before writes");

                    entered.Reset(); release.Reset(); calls = 0;
                    reloads = form.SourceReloads;
                    form.RequestReview(1);
                    PumpUntil(delegate { return entered.WaitOne(0); }, "second folder check never started");
                    File.WriteAllText(Path.Combine(folder, "Extra", Mesh), "changed while the next package loads");
                    // Finish the old freshness check while the next package is still reading. Its completion
                    // must not enqueue a reload that later targets the newly opened package.
                    MainForm.BeforeSourceStamp = delegate(string path)
                    {
                        if (path != other) return;
                        nextEntered.Set(); if (!releaseNext.WaitOne(10000)) throw new IOException("Next package test gate timed out.");
                    };
                    Call(form, "LoadPackage", Entry(form, other));
                    PumpUntil(delegate { return nextEntered.WaitOne(0); }, "next package did not start loading");
                    release.Set();
                    PumpUntil(delegate { return !Get<bool>(form, "reviewRunning"); }, "old review did not finish while the next package was loading");
                    releaseNext.Set(); Idle(form);
                    Require(Get<ModPackage>(form, "package").SourcePath == other && form.SourceReloads == reloads, "obsolete scan reloaded the newly opened package");

                    // An unchanged archive: window activation / F5 starts no new check (Install stays enabled); a changed one reloads at once.
                    int generation = Get<int>(form, "reviewGeneration");
                    form.CheckOpenPackage();
                    Require(Get<int>(form, "reviewGeneration") == generation && !form.ReviewPending, "activation checked an unchanged archive again");
                    File.SetLastWriteTimeUtc(other, DateTime.UtcNow.AddMinutes(1));
                    form.CheckOpenPackage(); Idle(form);
                    Require(form.SourceReloads == reloads + 1 && Get<ModPackage>(form, "package").SourcePath == other, "activation missed a changed archive");
                }
                finally { release.Set(); releaseNext.Set(); MainForm.BeforeSourceStamp = null; }
            }
            Call(form, "ClosePackage");
        }

        private static void Rename(MainForm form, LibraryEntry entry, string name)
        {
            form.BeginLibraryRename(entry); Get<TextBox>(form, "renameBox").Text = name; form.EndLibraryRename(true); Idle(form);
            Require(Path.GetFileNameWithoutExtension(entry.SourcePath) == name, "rename failed: " + Get<Label>(form, "statusText").Text);
        }
        private static void MakeFolder(string folder)
        {
            foreach (string part in new[] { "Main", "Extra" })
            {
                string file = Path.Combine(folder, part, Mesh); Directory.CreateDirectory(Path.GetDirectoryName(file));
                File.WriteAllText(file, part + " fixture"); File.WriteAllText(Path.Combine(folder, part, "modinfo.ini"), "name=" + part + "\n");
            }
        }
        private static void MakeZip(string file, string folder) { MakeFolder(folder); ZipFile.CreateFromDirectory(folder, file); }
        private static void Refresh(MainForm form, AppConfig config) { Set(form, "library", Library.Scan(config.Libraries, config.SevenZipPath)); Call(form, "ApplyFilters"); }
        private static LibraryEntry Entry(MainForm form, string path) { return Get<List<LibraryEntry>>(form, "library").Single(e => e.SourcePath == path); }
        private static T Get<T>(object o, string name) { return (T)o.GetType().GetField(name, Flags).GetValue(o); }
        private static void Set(object o, string name, object value) { o.GetType().GetField(name, Flags).SetValue(o, value); }
        private static object Call(object o, string name, params object[] args) { return o.GetType().GetMethods(Flags).First(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(o, args); }
        private static void Require(bool yes, string reason) { if (!yes) throw new InvalidOperationException(reason); }
        private static void Idle(MainForm form) { PumpUntil(delegate { return !form.Working; }, "background work did not finish"); Application.DoEvents(); }
        private static void PumpUntil(Func<bool> done, string reason)
        {
            var timer = Stopwatch.StartNew();
            do { Application.DoEvents(); if (done()) return; Thread.Sleep(10); } while (timer.ElapsedMilliseconds < 15000);
            throw new InvalidOperationException(reason);
        }
    }
}
