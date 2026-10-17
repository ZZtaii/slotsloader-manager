using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SlotsInstaller
{
    // 2.4.2 follow-ups of the 2.0.1 code review in the window: the replacement store's Settings note (installed
    // copies, removed copies and their keep rule) and the footer's "Saved options are missing" click.
    internal sealed partial class MainForm
    {
        // Settings → Game files & replacements, under the store folder field.
        internal static string StoreStatusText(long installedBytes, int installed, List<RemovedReplacement> removed, string store)
        {
            return Utilities.FriendlyBytes(installedBytes) + " · " + ReplacePlanner.N(installed, "installed replacement") + Environment.NewLine
                + RemovedCopiesLine(removed == null ? 0 : removed.Count, removed == null ? 0 : removed.Sum(r => r.Bytes)) + Environment.NewLine + store;
        }
        internal static string RemovedCopiesLine(int count, long bytes)
        {
            return "Removed copies: " + (count == 0 ? "none" : count + " · " + Utilities.FriendlyBytes(bytes)) + " · kept only while their Undo is available";
        }
        // 1.0.1: what Uninstall does to these outfits (OutfitSetStore.UninstallOutfits): deleted when the library still
        // has the source (reinstall is the way back), Recycle Bin when there's nothing to reinstall from.
        internal static string RemovedCopyHelp(List<InstalledOutfit> outfits)
        {
            var list = outfits ?? new List<InstalledOutfit>();
            int withSource = list.Count(OutfitSetStore.HasSource), without = list.Count - withSource;
            if (list.Count == 1) return withSource == 1 ? "Uninstall deletes it; reinstall it from the Mod library." : "Uninstall moves it to the Recycle Bin: it has no source in your libraries to reinstall from.";
            if (without == 0) return "Uninstall deletes them; reinstall them from the Mod library.";
            if (withSource == 0) return "Uninstall moves them to the Recycle Bin: they have no source in your libraries to reinstall from.";
            return "Uninstall deletes " + ReplacePlanner.N(withSource, "outfit") + " (reinstall from the Mod library) and moves " + without + " without a source to the Recycle Bin.";
        }
        // The store without its removed copies and unfinished staging folders.
        internal static long InstalledStoreBytes(string store)
        {
            long size = 0;
            try
            {
                if (!Directory.Exists(store)) return 0;
                foreach (string dir in Directory.GetDirectories(store))
                {
                    string name = Path.GetFileName(dir);
                    if (String.Equals(name, ReplaceEngine.RemovedFolderName, StringComparison.OrdinalIgnoreCase) || name.StartsWith(".staging-", StringComparison.OrdinalIgnoreCase)) continue;
                    size += new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
                }
            }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); }
            return size;
        }

        // Footer click with saved options missing (both modes): the same as Health / Review files, which review
        // without them and clear the flag (2.0.1-2.4.1 opened the report directly and the footer flag stayed).
        private bool FooterClearsMissingOptions()
        {
            if (package == null || busy || missingRestoredOptions.Count == 0 || unresolvedChoices.Count > 0) return false;
            BuildPlan(true);
            return true;
        }
    }
}
