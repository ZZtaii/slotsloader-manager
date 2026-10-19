using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace SlotsInstaller
{
    // 2.4.1 Delete (Mod library right-click, last item, danger colour): moves the mod's source archive or folder to
    // the Windows Recycle Bin. The click is the consent and the Recycle Bin is the undo, so there is no popup and no
    // Undo button. Only a mod the library lists (a direct child, or one inside a group folder) is deleted (never a library itself, never through a
    // link); a mod Fluffy lists as installed is refused; installed copies stay installed. When the Recycle Bin can't
    // take it (network or removable drive, Recycle Bin turned off, larger than its size) the one question is
    // "Delete permanently?". The Assistant has no delete tool: deleting the user's files stays a manual action.
    internal interface IRecycleBin
    {
        // null when Windows' Recycle Bin takes this file or folder (bytes: its size); otherwise why not, in a few words.
        string Unavailable(string path, bool folder, long bytes);
        void Recycle(string path, bool folder);
        void DeletePermanently(string path, bool folder);
    }

    internal static class RecycleBin
    {
        // The real one. Checks swap in a stub, so they never touch the user's Recycle Bin.
        internal static IRecycleBin Current = new ShellRecycleBin();
    }

    // Microsoft.VisualBasic.FileIO (part of .NET Framework 4.8): the shell's own delete, with the Recycle Bin and
    // only the shell's error dialogs (no confirmation; the click was the consent).
    internal sealed class ShellRecycleBin : IRecycleBin
    {
        [StructLayout(LayoutKind.Sequential)] private struct QueryInfo { public int Size; public long Bytes; public long Items; }
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHQueryRecycleBin(string root, ref QueryInfo info);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool GetVolumePathName(string path, StringBuilder volume, int length);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool GetVolumeNameForVolumeMountPoint(string mountPoint, StringBuilder name, int length);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern uint GetDriveType(string root);
        private const uint DriveRemovable = 2, DriveFixed = 3, DriveRemote = 4;

        // Windows recycles only on local fixed volumes (USB sticks and memory cards, network shares and mapped
        // drives delete for good), and not when the volume's Recycle Bin is set to "Don't move files to the Recycle
        // Bin" (NukeOnDelete), a policy turns it off (NoRecycleFiles), or the item is larger than its custom maximum
        // size (MaxCapacity, MB). In each of those cases the shell would delete permanently without asking.
        public string Unavailable(string path, bool folder, long bytes)
        {
            string full = Path.GetFullPath(path);
            if (full.StartsWith(@"\\?\", StringComparison.Ordinal) && full.Length > 6 && full[5] == ':') full = full.Substring(4);
            if (full.StartsWith(@"\\", StringComparison.Ordinal)) return "it is on a network location";
            string volume = VolumeOf(full);
            uint type = GetDriveType(volume);
            if (type == DriveRemote) return "it is on a network drive";
            if (type == DriveRemovable) return "it is on a removable drive";
            if (type != DriveFixed) return "this drive has no Recycle Bin";
            var info = new QueryInfo { Size = Marshal.SizeOf(typeof(QueryInfo)) };
            if (SHQueryRecycleBin(volume, ref info) != 0) return "this drive has no Recycle Bin";
            if (PolicyOff(Registry.CurrentUser) || PolicyOff(Registry.LocalMachine)) return "a policy turns the Recycle Bin off";
            string guid = VolumeGuid(volume);
            if (guid != null)
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\BitBucket\Volume\" + guid))
                {
                    if (key != null)
                    {
                        object nuke = key.GetValue("NukeOnDelete"), max = key.GetValue("MaxCapacity");
                        if (nuke is int && (int)nuke != 0) return "the Recycle Bin is turned off for this drive";
                        if (max is int && (int)max > 0 && bytes > (long)(int)max * 1024L * 1024L) return "it is larger than this drive's Recycle Bin (" + (int)max + " MB)";
                    }
                }
            return null;
        }
        public void Recycle(string path, bool folder)
        {
            OnShellThread(delegate
            {
                if (folder) Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                else Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            });
        }
        public void DeletePermanently(string path, bool folder)
        {
            OnShellThread(delegate
            {
                if (folder) Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.DeletePermanently);
                else Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.DeletePermanently);
            });
        }
        // The shell's file operation (and its error dialog) on an STA thread, not on the Work pool thread.
        private static void OnShellThread(ThreadStart action)
        {
            Exception error = null;
            var thread = new Thread(delegate() { try { action(); } catch (Exception ex) { error = ex; } }) { IsBackground = true, Name = "Recycle Bin" };
            thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
            if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
        }
        private static string VolumeOf(string full)
        {
            var volume = new StringBuilder(1024);
            string root = GetVolumePathName(full, volume, volume.Capacity) ? volume.ToString() : Path.GetPathRoot(full);
            return root.EndsWith("\\", StringComparison.Ordinal) ? root : root + "\\";
        }
        private static string VolumeGuid(string volume)
        {
            var name = new StringBuilder(64);
            if (!GetVolumeNameForVolumeMountPoint(volume, name, name.Capacity)) return null;
            string text = name.ToString(); int open = text.IndexOf('{'), close = text.IndexOf('}');
            return open >= 0 && close > open ? text.Substring(open, close - open + 1) : null;
        }
        private static bool PolicyOff(RegistryKey hive)
        {
            try
            {
                using (RegistryKey key = hive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer"))
                {
                    object value = key == null ? null : key.GetValue("NoRecycleFiles");
                    return value is int && (int)value != 0;
                }
            }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; return false; }
        }
    }

    // The checks, separate from the window so they are tested directly.
    internal static class LibraryDelete
    {
        internal const string FluffyRefusal = "Installed by an external mod manager: uninstall it first.";
        internal static int InUseAttempts = 3, InUseDelay = 200;   // a handle held for a moment (a scanner, a preview) is waited out

        internal sealed class Outcome
        {
            internal string Full, Error, Unavailable;
            internal bool Folder, Refused, Done, Permanent;
        }

        internal static string Normal(string path) { return Path.GetFullPath(path).TrimEnd('\\'); }
        internal static bool Same(string a, string b) { return String.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
        // path is container or inside it (both normalized).
        internal static bool Within(string path, string container) { return Same(path, container) || path.StartsWith(container + "\\", StringComparison.OrdinalIgnoreCase); }

        // null when source may be deleted: a direct child of a configured library (full paths, case-insensitive),
        // not a library itself, not holding a library or a protected folder, and not a link. Otherwise the refusal.
        internal static string ScopeError(string source, IEnumerable<string> libraries, IEnumerable<KeyValuePair<string, string>> protectedPaths, out string full, out bool folder)
        {
            full = null; folder = false;
            if (String.IsNullOrWhiteSpace(source)) return "this mod has no file or folder on disk.";
            try { full = Normal(source); }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; return "its path is not valid."; }
            var roots = new List<string>();
            foreach (string library in libraries ?? new string[0])
            {
                if (String.IsNullOrWhiteSpace(library)) continue;
                try { roots.Add(Normal(library)); } catch (Exception ex) { if (ex is OutOfMemoryException) throw; }
            }
            string target = full;
            if (roots.Any(r => Same(r, target))) return "it is a library folder. Delete never removes a library itself.";
            string parent = Path.GetDirectoryName(full);
            // 1.0.2: or a mod in a group folder (Mods\Cammy\mod.zip, two levels at most), as Library.Scan lists it.
            Func<string, bool> listedIn = dir => dir != null && roots.Any(r => Same(r, dir.TrimEnd('\\')));
            Func<string, bool> group = dir => dir != null && Library.IsGroupFolder(dir);
            string grand = parent == null ? null : Path.GetDirectoryName(parent);
            bool listed = listedIn(parent) || (group(parent) && (listedIn(grand) || (group(grand) && listedIn(grand == null ? null : Path.GetDirectoryName(grand)))));
            if (parent == null || !listed) return "it is not in one of your libraries. Only a library's own mods can be deleted here.";
            foreach (string root in roots) if (Within(root, target)) return "it contains the library " + root + ".";
            foreach (KeyValuePair<string, string> kept in protectedPaths ?? new KeyValuePair<string, string>[0])
            {
                if (String.IsNullOrWhiteSpace(kept.Value)) continue;
                string other; try { other = Normal(kept.Value); } catch (Exception ex) { if (ex is OutOfMemoryException) throw; continue; }
                if (Within(other, target)) return "it contains " + kept.Key + ".";
            }
            FileAttributes attributes;
            try { attributes = File.GetAttributes(full); }
            catch (FileNotFoundException) { return "it is no longer on disk. Press F5 to rescan."; }
            catch (DirectoryNotFoundException) { return "it is no longer on disk. Press F5 to rescan."; }
            if ((attributes & FileAttributes.ReparsePoint) != 0) return "it is a link (junction or symbolic link), not the mod itself. Delete never follows links.";
            folder = (attributes & FileAttributes.Directory) != 0;
            return null;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
        private const uint DeleteAccess = 0x00010000, ReadAttributes = 0x80, ShareAll = 7, OpenExisting = 3, BackupSemantics = 0x02000000, OpenReparsePoint = 0x00200000;

        // Every file and folder of the source can be deleted now: nothing holds it open without sharing delete (the
        // same open the shell needs to move it), no link inside. Returns why not, or null; bytes = its size.
        internal static string Inspect(string full, bool folder, out long bytes, out bool inUse)
        {
            bytes = 0; inUse = false;
            if (!folder) { bytes = new FileInfo(full).Length; return Openable(full, false, "it", out inUse); }
            string busy = Openable(full, true, "the folder", out inUse); if (busy != null) return busy;
            var pending = new Stack<DirectoryInfo>(); pending.Push(new DirectoryInfo(full));
            while (pending.Count > 0)
            {
                foreach (FileSystemInfo item in pending.Pop().EnumerateFileSystemInfos())
                {
                    string relative = item.FullName.Substring(full.Length).TrimStart('\\');
                    if ((item.Attributes & FileAttributes.ReparsePoint) != 0) return "it contains a link (" + relative + "). Delete never follows links; remove the link first.";
                    var directory = item as DirectoryInfo;
                    if (directory != null) pending.Push(directory); else bytes += ((FileInfo)item).Length;
                    busy = Openable(item.FullName, directory != null, relative, out inUse);
                    if (busy != null) return busy;
                }
            }
            return null;
        }
        private static string Openable(string path, bool directory, string what, out bool inUse)
        {
            inUse = false;
            string native = path.Length >= 248 && !path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\" + path : path;
            using (SafeFileHandle handle = CreateFile(native, DeleteAccess | ReadAttributes, ShareAll, IntPtr.Zero, OpenExisting, OpenReparsePoint | (directory ? BackupSemantics : 0), IntPtr.Zero))
            {
                if (!handle.IsInvalid) return null;
                int error = Marshal.GetLastWin32Error();
                if (error == 32 || error == 33) { inUse = true; return what + " is in use by another program. Close it and try again."; }
                if (error == 5) return "Windows denied access to " + what + ".";
                if (error == 2 || error == 3) return what + " changed while it was being checked. Try again.";
                return what + ": " + new Win32Exception(error).Message;
            }
        }

        // Worker thread: the checks again (the disk may have changed since the click), then the delete.
        internal static Outcome Run(string source, IEnumerable<string> libraries, IEnumerable<KeyValuePair<string, string>> protectedPaths, IRecycleBin bin, bool permanent)
        {
            var o = new Outcome { Permanent = permanent };
            try
            {
                string refusal = ScopeError(source, libraries, protectedPaths, out o.Full, out o.Folder);
                if (refusal != null) { o.Error = refusal; o.Refused = true; return o; }
                long bytes = 0; string busy = null;
                for (int attempt = 1; ; attempt++)
                {
                    bool inUse; busy = Inspect(o.Full, o.Folder, out bytes, out inUse);
                    if (busy == null || !inUse || attempt >= InUseAttempts) break;
                    Thread.Sleep(InUseDelay);
                }
                if (busy != null) { o.Error = busy; return o; }
                if (!permanent)
                {
                    o.Unavailable = bin.Unavailable(o.Full, o.Folder, bytes);
                    if (o.Unavailable != null) return o;
                    bin.Recycle(o.Full, o.Folder);
                }
                else bin.DeletePermanently(o.Full, o.Folder);
                if (File.Exists(o.Full) || Directory.Exists(o.Full)) { o.Error = "Windows left it in place."; return o; }
                Library.ForgetSource(o.Full);
                if (!Same(o.Full, source)) Library.ForgetSource(source);
                o.Done = true;
            }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; o.Error = ex.Message; }
            return o;
        }
    }

    internal sealed partial class MainForm
    {
        private ToolStripMenuItem libraryDeleteItem;
        private ToolStripSeparator libraryDeleteSeparator;
        // Sources deleted in this session (their installed copies say "Source deleted").
        private readonly HashSet<string> deletedSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private object listedSourcesFor;
        private readonly HashSet<string> listedSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase), listedRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        internal int DeleteRuns;   // checks: how many deletes reached the worker

        // BuildLibrary: a separator and Delete, last, in the danger colour (like Uninstall on the Installed page).
        private void AddLibraryDelete(ContextMenuStrip menu)
        {
            libraryDeleteSeparator = new ToolStripSeparator();
            libraryDeleteItem = new ToolStripMenuItem("Delete") { ForeColor = Palette.Red };
            libraryDeleteItem.Click += delegate { DeleteLibrarySource(libraryDeleteItem.Tag as LibraryEntry); };
            menu.Items.Add(libraryDeleteSeparator); menu.Items.Add(libraryDeleteItem);
        }
        // Opening: "Delete archive" / "Delete folder", "(3 mods)" when several library entries share the source.
        private void PopulateLibraryDelete(LibraryEntry entry)
        {
            if (libraryDeleteItem == null) return;
            bool show = entry != null && !String.IsNullOrEmpty(entry.SourcePath) && entry.Kind != "Library";
            libraryDeleteItem.Visible = libraryDeleteSeparator.Visible = show; libraryDeleteItem.Tag = show ? entry : null;
            if (!show) return;
            int count = SameSourceEntries(entry).Count;
            string kind = entry.Kind == "Folder" ? "Delete folder" : entry.Kind == "ZIP" || entry.Kind == "RAR" || entry.Kind == "7Z" ? "Delete archive" : "Delete";
            libraryDeleteItem.Text = kind + (count > 1 ? " (" + count + " mods)" : "");
            libraryDeleteItem.Enabled = !busy;
            libraryDeleteItem.AccessibleDescription = LinkedFluffy(entry.SourcePath).Count > 0 ? LibraryDelete.FluffyRefusal : "Moves it to the Recycle Bin. Installed copies stay installed.";
        }
        private List<LibraryEntry> SameSourceEntries(LibraryEntry entry)
        {
            var same = library.Where(e => e != null && String.Equals(e.SourcePath, entry.SourcePath, StringComparison.OrdinalIgnoreCase)).ToList();
            if (!same.Contains(entry)) same.Add(entry);
            return same;
        }
        private List<KeyValuePair<string, string>> DeleteProtectedPaths()
        {
            var kept = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("the game folder", config.GameRoot),
                new KeyValuePair<string, string>("this app's folder", AppDomain.CurrentDomain.BaseDirectory),
                new KeyValuePair<string, string>("this app's Data folder", AppConfig.DataDir)
            };
            foreach (string root in fluffyRoots) kept.Add(new KeyValuePair<string, string>("External mod manager's folder", root));
            return kept;
        }
        private static string SourceLabel(string source)
        {
            try { string name = Path.GetFileName(source.TrimEnd('\\')); return String.IsNullOrEmpty(name) ? source : name; }
            catch (ArgumentException) { return source; }
        }

        internal void DeleteLibrarySource(LibraryEntry entry)
        {
            if (entry == null || busy || String.IsNullOrEmpty(entry.SourcePath) || entry.Kind == "Library") return;
            List<LibraryEntry> group = SameSourceEntries(entry);
            string label = SourceLabel(entry.SourcePath), source = entry.SourcePath;
            if (LinkedFluffy(source).Count > 0) { SetPageStatus(libraryPage, LibraryDelete.FluffyRefusal, Palette.Accent); return; }
            string[] libraries = config.Libraries.ToArray(); List<KeyValuePair<string, string>> kept = DeleteProtectedPaths();
            string full; bool folder;
            string refusal = LibraryDelete.ScopeError(source, libraries, kept, out full, out folder);
            if (refusal != null) { SetPageStatus(libraryPage, "Can't delete " + label + ": " + refusal, Palette.Accent); return; }
            // The open mod's check may be reading the archive: stop it so its handle doesn't count as "in use".
            if (package != null && reviewCancel != null && SamePath(package.SourcePath, full)) try { reviewCancel.Cancel(); } catch (ObjectDisposedException) { }
            IRecycleBin bin = RecycleBin.Current;
            DeleteRuns++;
            Work("Moving " + label + " to the Recycle Bin\u2026", delegate { return LibraryDelete.Run(source, libraries, kept, bin, false); },
                delegate(LibraryDelete.Outcome outcome) { LibraryDeleteDone(group, label, source, libraries, kept, bin, outcome); });
        }
        private static bool SamePath(string a, string b)
        {
            try { return !String.IsNullOrEmpty(a) && !String.IsNullOrEmpty(b) && LibraryDelete.Same(LibraryDelete.Normal(a), LibraryDelete.Normal(b)); }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; return false; }
        }
        private void LibraryDeleteDone(List<LibraryEntry> group, string label, string source, string[] libraries, List<KeyValuePair<string, string>> kept, IRecycleBin bin, LibraryDelete.Outcome outcome)
        {
            if (outcome.Unavailable != null)
            {
                // Never a silent permanent delete: the one question of this feature (PolishChecks.PopupAllowList).
                if (!AskPermanentDelete(label, outcome.Unavailable)) { SetPageStatus(libraryPage, "Kept " + label + ": the Recycle Bin can't take it (" + outcome.Unavailable + ").", Palette.Muted); return; }
                Work("Deleting " + label + "\u2026", delegate { return LibraryDelete.Run(source, libraries, kept, bin, true); },
                    delegate(LibraryDelete.Outcome again) { LibraryDeleteDone(group, label, source, libraries, kept, bin, again); });
                return;
            }
            if (!outcome.Done)
            {
                SetPageStatus(libraryPage, (outcome.Refused ? "Can't delete " : "Couldn't delete ") + label + ": " + outcome.Error, outcome.Refused ? Palette.Accent : Palette.Red);
                return;
            }
            FinishLibraryDelete(group, outcome.Full, outcome.Permanent ? "Deleted " + label + " permanently." : "Moved " + label + " to the Recycle Bin.");
        }
        private bool AskPermanentDelete(string label, string reason)
        {
            if (Program.Headless && ThemedMessageBox.TestResponder == null) return false;
            bool ignored;
            int answer = ThemedMessageBox.Ask(this, "The Recycle Bin can't take " + label + ": " + reason + ".\n\nDelete it permanently? This can't be undone. Installed copies stay installed.",
                "Delete permanently?", MessageBoxIcon.Warning, new[] { "Delete permanently", "Cancel" }, 1, null, out ignored);
            return answer == 0;
        }

        // The rows go at once (no rescan); the index forgot the source in the worker. Selection moves to the next row;
        // the inspector clears when it showed this mod; counts, filters and sort follow from ApplyFilters.
        private void FinishLibraryDelete(List<LibraryEntry> group, string full, string message)
        {
            var gone = new HashSet<LibraryEntry>(group);
            LibraryNames.Forget(new[] { full }.Concat(group.Select(e => e.SourcePath)));   // 1.0.6
            deletedSources.Add(full); foreach (LibraryEntry e in group) deletedSources.Add(e.SourcePath);
            changedSources.Remove(full); foreach (LibraryEntry e in group) changedSources.Remove(e.SourcePath);
            int next = -1;
            foreach (ListViewItem item in modList.SelectedItems) if (gone.Contains(item.Tag as LibraryEntry)) { next = item.Index; break; }
            if (next < 0 && modList.FocusedItem != null && gone.Contains(modList.FocusedItem.Tag as LibraryEntry)) next = modList.FocusedItem.Index;
            if (package != null && SamePath(package.SourcePath, full)) ClosePackage();
            List<LibraryEntry> before = library;
            library = library.Where(e => !gone.Contains(e)).ToList();
            if (Object.ReferenceEquals(libraryRowsSource, before)) libraryRowsSource = library;   // keep the row cache of the other entries
            foreach (LibraryEntry e in gone) { libraryRows.Remove(e); installStates.Remove(e); libraryRanks.Remove(e); }
            ApplyFilters();
            if (next >= 0 && modList.Items.Count > 0)
            {
                bool previous = selectingMod; selectingMod = true;
                try
                {
                    foreach (ListViewItem item in modList.SelectedItems.Cast<ListViewItem>().ToList()) item.Selected = false;
                    ListViewItem row = modList.Items[Math.Min(next, modList.Items.Count - 1)];
                    row.Selected = true; row.Focused = true; row.EnsureVisible();
                }
                finally { selectingMod = previous; }
            }
            FillInstalled(); installedList.Invalidate(); UpdateLibraryUninstall();
            if (assistantPage != null) assistantPage.LibraryChanged();
            SetSharedStatus(message, Palette.Green);
        }

        // Installed rows and details: "Source deleted" (deleted here this session) or "Source missing" (in a library
        // folder but not in its last listing). Painting never touches the disk: both come from memory.
        internal string SourceGoneNote(string source)
        {
            if (String.IsNullOrEmpty(source)) return null;
            if (deletedSources.Contains(source)) return "Source deleted";
            if (!Object.ReferenceEquals(listedSourcesFor, library))
            {
                listedSourcesFor = library; listedSources.Clear(); listedRoots.Clear();
                if (library.Count > 0)
                {
                    foreach (string root in config.Libraries) if (!String.IsNullOrWhiteSpace(root)) listedRoots.Add(root.TrimEnd('\\'));
                    foreach (LibraryEntry e in library)
                    {
                        if (e == null || String.IsNullOrEmpty(e.SourcePath)) continue;
                        if (e.Kind == "Library") listedRoots.Remove(e.SourcePath.TrimEnd('\\'));   // unreadable library: no conclusion
                        else
                        {
                            listedSources.Add(e.SourcePath);
                            string folder = Path.GetDirectoryName(e.SourcePath);   // 1.0.2: a group folder counts as listed too
                            if (e.LibraryRoot != null && folder != null && !String.Equals(folder.TrimEnd('\\'), e.LibraryRoot.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) listedRoots.Add(folder.TrimEnd('\\'));
                        }
                    }
                }
            }
            if (listedRoots.Count == 0 || listedSources.Contains(source)) return null;
            try { string parent = Path.GetDirectoryName(source); return parent != null && listedRoots.Contains(parent.TrimEnd('\\')) ? "Source missing" : null; }
            catch (ArgumentException) { return null; }
        }
        // Draws the muted note at the start of an Installed row's meta line; returns the rest of the line.
        private Rectangle DrawSourceGoneNote(Graphics graphics, string source, Rectangle meta)
        {
            string note = SourceGoneNote(source);
            if (note == null) return meta;
            string text = note + "   \u00B7   ";
            int width = Math.Min(meta.Width, MeasuredWidth(text, Palette.Small));
            TextRenderer.DrawText(graphics, text, Palette.Small, new Rectangle(meta.Left, meta.Top, width, meta.Height), Palette.Muted, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            return new Rectangle(meta.Left + width, meta.Top, Math.Max(0, meta.Width - width), meta.Height);
        }
    }
}
