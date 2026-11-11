using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace SlotsInstaller
{
    internal static partial class ReplaceUiChecks
    {
        // 1.0.16: Reorder bundle on a 7Z. Its layout is saved in the Data folder and the archive keeps its stamp, so the
        // package kept for that stamp must not be shown again: the reopen after Save (what the editor does) reads the
        // archive again and shows the new groups and names. An installed copy's selection with a layout group (Edit
        // options) ticks the same options.
        private static void StoredLayoutOpen(MainForm form, ReplaceChecks.Fixture f, string root)
        {
            string sevenZip = SevenZipTool.Resolve("");
            if (String.IsNullOrEmpty(sevenZip)) return;   // no 7-Zip on this machine
            string dir = Path.Combine(root, "stored-open"), path = Path.Combine(dir, "Stored levels.7z");
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            string source = InstallImprovementChecks.OptionsFixture(Path.Combine(dir, "src"));
            ProcessUtil.RunText(sevenZip, "a -t7z -y " + ProcessUtil.Quote(path) + " " + ProcessUtil.Quote(Path.Combine(source, "*")));
            var entry = new LibraryEntry { SourcePath = path, Name = "Stored levels" };
            Func<ModPackage> shown = delegate { return Get<ModPackage>(form, "package"); };
            Action open = delegate
            {
                Call(form, "LoadPackage", entry);
                DateTime until = DateTime.UtcNow.AddSeconds(30);
                while ((Get<bool>(form, "busy") || shown() == null || !String.Equals(shown().SourcePath, path, StringComparison.OrdinalIgnoreCase)) && DateTime.UtcNow < until) { Application.DoEvents(); Thread.Sleep(5); }
                Require(shown() != null && String.Equals(shown().SourcePath, path, StringComparison.OrdinalIgnoreCase), "the 7Z did not open");
                WaitIdle(form); WaitReview(form);
            };
            string previous = BundleStore.DataDirOverride; BundleStore.DataDirOverride = Path.Combine(dir, "data");
            try
            {
                Call(form, "SetInstallMode", InstallModes.Replace, true); ShowLibrary(form);
                open();
                ModPackage author = shown();
                Require(!author.HasBundleLayout, "a fresh 7Z has a layout");
                int hits = form.PackageCacheHits, reads = form.PackageReads;
                open();
                Require(form.PackageCacheHits == hits + 1 && form.PackageReads == reads, "the 7Z was not kept (this check needs the kept copy)");
                var rows = BundleLayout.Options(author);
                var group = rows.Single(r => author.Find(r.Id).InferredChoice); group.Rule = "Choose up to one";
                var levels = new BundleOption { Id = BundleLayout.GroupPrefix + Guid.NewGuid().ToString("N"), Name = "Brightness levels", Rule = "Choose any" };
                rows.Add(levels); group.Parent = levels.Id;
                var renamed = rows.First(r => !author.Find(r.Id).IsDummy); renamed.Name = "Brightest level";
                string stamp = SourceReader.Stamp(path);
                BundleLayout.Save(path, stamp, rows);
                Require(SourceReader.Stamp(path) == stamp, "the 7Z was written");
                reads = form.PackageReads;
                open();   // as the editor does after Save
                ModPackage edited = shown();
                var nodes = Get<Dictionary<string, OptionNode>>(form, "componentNodes");
                Require(form.PackageReads == reads + 1 && edited.HasBundleLayout && edited.Find(renamed.Id).Name == "Brightest level" && nodes.ContainsKey(levels.Id) && nodes[levels.Id].Text.Contains("Brightness levels"),
                    "the open window did not show the saved layout: reads +" + (form.PackageReads - reads) + ", layout " + edited.HasBundleLayout + ", groups " + String.Join(", ", nodes.Values.Select(n => n.Text)));
                // Edit options on an installed copy whose selection names the layout group: the same ticks, no review note.
                string member = edited.Components.First(c => c.ParentId == group.Id).Id;
                var installed = new InstalledOutfit { Name = "Stored levels", Character = "", SourcePath = path, Managed = true, SelectedIds = new List<string> { levels.Id, member } };
                Call(form, "LoadPackage", entry, installed);
                DateTime settle = DateTime.UtcNow.AddSeconds(30); while (Get<bool>(form, "busy") && DateTime.UtcNow < settle) { Application.DoEvents(); Thread.Sleep(5); }
                WaitIdle(form);
                List<string> ticked = Get<List<string>>(form, "selectedOrder").ToList();
                string status = Get<Label>(form, "statusText").Text;
                Require(ticked.Contains(member) && status.IndexOf("Saved options changed", StringComparison.OrdinalIgnoreCase) < 0, "an installed selection with a layout group: ticked " + String.Join(", ", ticked) + ", status '" + status + "'");
            }
            finally
            {
                BundleStore.DataDirOverride = previous;
                WaitIdle(form); WaitReview(form);
                Call(form, "SetInstallMode", InstallModes.Replace, true);
                Call(form, "ClosePackage");
            }
        }
    }
}
