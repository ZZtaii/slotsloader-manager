using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace SlotsInstaller
{
    // 2.4.1: the source of the open mod changed on disk (the user edited their own zip or folder). Reading a stale
    // package would fail ("changed on disk"), so the open mod is reloaded in place instead: on F5 / Refresh, when the
    // window is activated, before every health check or review, and before Install. The selection (by option id,
    // else by option name), the INSTALL AS mode, character and outfit name are kept and the check runs again. A
    // source that is gone clears the inspector and says so. No popup.
    internal sealed class SourceChangedException : IOException
    {
        internal SourceChangedException() : base("The mod changed on disk while it was being read.") { }
    }

    internal sealed partial class MainForm
    {
        private sealed class PackageReload
        {
            internal string Mode, Character, OutfitName, Name, CompletionStatus; internal bool Raced, NameTyped, NameLocked, Renamed;
            internal List<KeyValuePair<string, string>> Selection = new List<KeyValuePair<string, string>>();   // option id -> option name
        }
        private string packageStamp;
        private PackageReload pendingReload;
        internal int SourceReloads { get; private set; }
        internal string LastReloadDropped { get; private set; }
        internal static Action<string> BeforeSourceStamp;   // deterministic worker/cancellation checks

        // Size and write time of an archive; for a folder mod also its file count, total size and newest write
        // time (an edit, an added or a removed file all change it). null when the source is gone.
        internal static string SourceStamp(string path)
        {
            try
            {
                OperationContext.ThrowIfCancellationRequested();
                if (BeforeSourceStamp != null) BeforeSourceStamp(path);
                OperationContext.ThrowIfCancellationRequested();
                if (String.IsNullOrEmpty(path)) return null;
                if (File.Exists(path)) return FileStamp(new FileInfo(path));
                if (!Directory.Exists(path)) return null;
                var dir = new DirectoryInfo(path);
                long count = 0, size = 0, latest = dir.LastWriteTimeUtc.Ticks;
                foreach (FileSystemInfo item in dir.EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
                {
                    OperationContext.ThrowIfCancellationRequested();
                    var file = item as FileInfo;
                    if (file != null) { count++; size += file.Length; }
                    latest = Math.Max(latest, item.LastWriteTimeUtc.Ticks);
                }
                return "folder:" + count + ":" + size + ":" + latest;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; return "unreadable:" + ex.GetType().Name; }
        }

        private static string FileStamp(FileInfo f) { return "file:" + f.Length + ":" + f.LastWriteTimeUtc.Ticks; }

        // An archive's stamp is one file stat, cheap enough for the UI thread: false only for a folder mod (its stamp
        // walks every file, so a worker compares it). stamp is null when the archive is gone.
        private static bool TryArchiveStamp(string path, out string stamp)
        {
            stamp = null;
            if (String.IsNullOrEmpty(path) || Directory.Exists(path)) return false;
            try { var f = new FileInfo(path); if (f.Exists) stamp = FileStamp(f); return true; }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; return false; }   // the worker's SourceStamp decides
        }

        // Window activation / F5: an unchanged archive needs no new check (Install stays enabled); a folder mod is
        // compared by the review worker, never walked on the UI thread.
        internal void CheckOpenPackage()
        {
            if (package == null || busy || packageStamp == null) return;
            string now;
            if (TryArchiveStamp(package.SourcePath, out now))
            {
                if (now == packageStamp) return;
                if (now == null) SourceGone(); else ReloadOpenPackage(false);
                return;
            }
            RequestReview(1);
            UpdateInstallButton();
        }

        // Install must also validate a fresh source: an archive at once, a folder mod in a worker.
        private void WithFreshPackage(Action unchanged)
        {
            if (package == null || busy) return;
            string stamp;
            if (TryArchiveStamp(package.SourcePath, out stamp))
            {
                if (stamp == null) SourceGone();
                else if (stamp != packageStamp) ReloadOpenPackage(false);
                else unchanged();
                return;
            }
            ModPackage current = package; string expected = packageStamp;
            Work("Checking mod source…", delegate { return SourceStamp(current.SourcePath); }, delegate(string now)
            {
                if (!Object.ReferenceEquals(current, package) || packageStamp != expected) return;
                if (now == null) SourceGone();
                else if (now != expected) ReloadOpenPackage(false);
                else unchanged();
            });
        }
        private void CheckOpenPackageOnActivate()
        {
            if (package == null || busy || IsDisposed) return;
            BeginInvoke((Action)delegate { if (!IsDisposed) CheckOpenPackage(); });
        }

        private void ReloadOpenPackage(bool raced)
        {
            if (package == null || IsDisposed) return;
            if (busy) { BeginInvoke((Action)delegate { ReloadOpenPackage(raced); }); return; }
            if (!File.Exists(package.SourcePath) && !Directory.Exists(package.SourcePath)) { SourceGone(); return; }
            PackageReload reload = CapturePackageReload(raced);
            LibraryEntry entry = library.FirstOrDefault(e => String.Equals(e.SourcePath, package.SourcePath, StringComparison.OrdinalIgnoreCase)) ?? new LibraryEntry { SourcePath = package.SourcePath, Name = package.Name };
            pendingReload = reload;
            LoadPackage(entry, null, editingReplacement);
            if (pendingReload == reload) pendingReload = null;   // LoadPackage refused (busy): nothing pending
        }

        private PackageReload CapturePackageReload(bool raced)
        {
            var reload = new PackageReload { Mode = installMode, Character = Convert.ToString(targetCharacter.SelectedItem), OutfitName = outfitName.Text, Name = package.Name, Raced = raced, NameTyped = outfitNameTyped, NameLocked = outfitNameLocked };
            foreach (string id in selectedOrder) { ModComponent c = package.Find(id); reload.Selection.Add(new KeyValuePair<string, string>(id, c == null ? "" : c.Name)); }
            return reload;
        }

        // The old selection on the reloaded package: the same option id, else the one option with the same name.
        internal static List<string> MapSelection(ModPackage reloaded, List<KeyValuePair<string, string>> old, out List<string> dropped)
        {
            var ids = new List<string>(); dropped = new List<string>();
            foreach (var pick in old)
            {
                ModComponent same = reloaded.Find(pick.Key);
                if (same == null && pick.Value.Length > 0)
                {
                    var named = reloaded.Components.Where(c => String.Equals(c.Name, pick.Value, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (named.Count == 1) same = named[0];
                }
                if (same != null) { if (!ids.Contains(same.Id, StringComparer.OrdinalIgnoreCase)) ids.Add(same.Id); }
                else dropped.Add(pick.Value.Length > 0 ? pick.Value : pick.Key);
            }
            return ids;
        }

        // Runs at the end of LoadPackage's completion when it was a reload.
        private void ApplyReload(PackageReload reload)
        {
            SourceReloads++;
            if (forcedReplaceReason == null && installMode != reload.Mode) SetInstallMode(reload.Mode, false);
            if (reload.Renamed || !String.IsNullOrEmpty(reload.OutfitName)) SetOutfitName(reload.OutfitName);
            outfitNameTyped = reload.NameTyped; outfitNameLocked = reload.NameLocked;   // 2.5.0: a typed name stays typed, a suggestion stays a suggestion
            if (reload.Renamed) targetCharacter.SelectedIndex = targetCharacter.Items.IndexOf(reload.Character);
            else if (!String.IsNullOrEmpty(reload.Character) && targetCharacter.Items.Contains(reload.Character)) targetCharacter.SelectedItem = reload.Character;
            List<string> dropped;
            List<string> ids = MapSelection(package, reload.Selection, out dropped);
            LastReloadDropped = String.Join(", ", dropped);
            if (ids.Count == 0 && !reload.Renamed) ids = Composer.DefaultSelection(package, IsReplaceMode);
            FillComponents(ids); ResetPlan(); UpdateDestination(); UpdateModeUi();
            string text = reload.CompletionStatus ?? ("Reloaded " + package.Name + ": the file changed" + (reload.Raced ? " while it was being installed; nothing was installed. Click Install again." : "."));
            if (dropped.Count > 0) text += " No longer in the mod: " + String.Join(", ", dropped) + ".";
            SetPageStatus(libraryPage, text, dropped.Count > 0 ? Palette.Accent : reload.Renamed ? Palette.Green : Palette.Muted);
            RequestReview(1);
        }

        // The open mod's source was deleted or moved: clear the inspector and say so inline.
        private void SourceGone()
        {
            string name = package == null ? "The mod" : package.Name;
            ClosePackage();
            SetPageStatus(libraryPage, name + " is no longer on disk (deleted or moved). Choose another mod, or press F5 to rescan.", Palette.Accent);
        }
        internal void ClosePackage()
        {
            if (reviewCancel != null) try { reviewCancel.Cancel(); } catch (ObjectDisposedException) { }
            package = null; plan = null; replacePlan = null; packageStamp = null; editingReplacement = null; forcedReplaceReason = null;
            changingChecks = true;
            try { componentTree.BeginUpdate(); componentTree.Nodes.Clear(); componentTree.EndUpdate(); }
            finally { changingChecks = false; }
            componentNodes.Clear(); selectedOrder.Clear(); missingRestoredOptions.Clear(); ClearFileRows();
            packageTitle.Text = "Choose a mod"; SetByline(null); SetVersion(null); packageDescription.Text = ""; packageHint.Text = ""; componentTitle.Text = "OPTION PREVIEW";
            SetPreview(null); ClearPreviewCache(); previewButton.Enabled = false;
            foreach (ListViewItem row in modList.SelectedItems.Cast<ListViewItem>().ToList()) row.Selected = false;
            RequestReview(1); UpdateFooter(); UpdateInstallButton(); UpdateLibraryUninstall(); UpdateModeUi();
        }

        // ---- Installed copies whose source changed after they were installed ----------------------------------
        // The source (archive, or any file in a folder mod) was written after the install: a compact "Source changed"
        // marker on the row, and Reinstall applies the edit in place (a switch: same record, no warnings).
        private FlatButton reinstallButton;
        private HashSet<string> changedSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        internal static bool SourceNewerThan(string path, DateTime installedUtc)
        {
            try
            {
                if (String.IsNullOrEmpty(path) || installedUtc.Year < 2001) return false;
                DateTime limit = installedUtc.ToUniversalTime().AddSeconds(2);
                if (File.Exists(path)) return File.GetLastWriteTimeUtc(path) > limit;
                if (!Directory.Exists(path)) return false;
                return new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories).Any(f => f.LastWriteTimeUtc > limit);
            }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; return false; }
        }
        // Worker thread: the installed sources (managed outfits, Manager replacements and tool mods) that changed.
        internal static HashSet<string> ChangedSources(IEnumerable<InstalledOutfit> outfits, IEnumerable<GameModRow> rows)
        {
            var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var newest = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            Action<string, DateTime> add = delegate(string source, DateTime when)
            {
                if (String.IsNullOrEmpty(source)) return;
                DateTime seen; if (!newest.TryGetValue(source, out seen) || when > seen) newest[source] = when;
            };
            foreach (InstalledOutfit o in outfits ?? new InstalledOutfit[0]) if (o.Managed) add(o.SourcePath, o.InstalledUtc);
            foreach (GameModRow r in rows ?? new GameModRow[0]) if (r.Mod != null && !LoaderCostumePak.IsLoaderMod(r.Mod)) add(r.Mod.SourcePath, LibraryUsage.ParseUtc(r.Mod.InstalledUtc));
            foreach (var pair in newest) if (SourceNewerThan(pair.Key, pair.Value)) changed.Add(pair.Key);
            return changed;
        }
        internal bool SourceChanged(string sourcePath) { return !String.IsNullOrEmpty(sourcePath) && changedSources.Contains(sourcePath); }

        // One click: reinstall the selected row from its edited source with the options it was installed with.
        private void ReinstallSelected()
        {
            if (busy) return;
            GameModRow row = GetGameRowSelection();
            if (row != null && row.Mod != null) { ReinstallReplacement(row.Mod); return; }
            InstalledOutfit outfit = GetInstalledSelection();
            if (outfit != null && outfit.Managed) ReinstallOutfit(outfit);
        }
        private void ReinstallReplacement(ReplaceMod mod)
        {
            string blocker = ReplacementUninstallBlocker(new[] { gameRows.First(r => r.Mod != null && r.Mod.Id == mod.Id) });
            if (blocker != null) { SetPageStatus(currentPage, blocker, Palette.Accent); return; }
            if (!GameIdle("reinstall " + mod.Name)) return;
            string source = mod.SourcePath, sevenZip = config.SevenZipPath, id = mod.Id, name = mod.Name;
            List<string> selection = (mod.SelectedIds ?? new List<string>()).ToList();
            ReplaceEngine engine = Engine();
            Work("Reinstalling " + name + "…", delegate
            {
                ModPackage source2 = PackageReader.Load(source, sevenZip, false);
                List<string> dropped; List<string> ids = MapSelection(source2, selection.Select(s => new KeyValuePair<string, string>(s, "")).ToList(), out dropped);
                if (dropped.Count > 0) throw new IOException("Saved options are missing: " + String.Join(", ", dropped) + ". Edit options before reinstalling.");
                if (selection.Count == 0) ids = Composer.DefaultSelection(source2, true);
                CompositionPlan composition = Composer.SavedPlan(source2, ids, true);
                if (!composition.CanInstall) throw new IOException("Couldn't reinstall " + name + ": " + String.Join(" ", composition.Errors));
                ReplacePlan prepared;
                ReplacePlanner.PrepareAndInstall(composition, sevenZip, engine, name, false, id, out prepared);
                return SavedChoiceNote(composition);
            }, delegate(string note) { RefreshInstalled("✓ Reinstalled " + name + " from the edited source." + note + " Restart SF6 to see changes."); });
        }
        // " Saved selection resolves to …" (Composer.SavedPlan), or "" when the saved options were used as they are.
        private static string SavedChoiceNote(CompositionPlan composition)
        {
            string note = String.Join(" ", composition.Warnings.Where(w => w.StartsWith("Saved selection resolves to", StringComparison.Ordinal)));
            return note.Length == 0 ? "" : " " + note;
        }
        private void ReinstallOutfit(InstalledOutfit outfit)
        {
            string source = outfit.SourcePath, sevenZip = config.SevenZipPath, root = config.CostumeRoot, character = outfit.Character, name = outfit.Name;
            List<string> selection = (outfit.SelectedIds ?? new List<string>()).ToList();
            Work("Reinstalling " + name + "…", delegate
            {
                ModPackage source2 = PackageReader.Load(source, sevenZip, false);
                List<string> dropped; List<string> ids = MapSelection(source2, selection.Select(s => new KeyValuePair<string, string>(s, "")).ToList(), out dropped);
                if (dropped.Count > 0) throw new IOException("Saved options are missing: " + String.Join(", ", dropped) + ". Edit options before reinstalling.");
                if (selection.Count == 0) ids = Composer.DefaultSelection(source2, false);
                CompositionPlan composition = Composer.SavedPlan(source2, ids, false);
                if (!composition.CanInstall) throw new IOException("Couldn't reinstall " + name + ": " + String.Join(" ", composition.Errors));
                Installer.Install(composition, root, character, name, sevenZip, true);   // the same name: a switch (1.0.1: the earlier copy is not kept)
                return SavedChoiceNote(composition);
            }, delegate(string note) { RefreshInstalled("✓ Reinstalled " + name + " from the edited source." + note + " Restart SF6 to see changes."); });
        }
    }
}
