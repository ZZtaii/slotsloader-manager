using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace SlotsInstaller
{
    // 1.0.20 Settings → General → Convert RAR to ZIP (ArchiveConvert, ConvertForm). The first question says how
    // many mods convert, how many are left out and why, and the space; the window does the rest. A mod the external mod
    // manager has installed is left out (the user's choice: that manager knows its mods by file name).
    internal sealed partial class MainForm
    {
        internal const string ConvertFluffySkip = "installed in the external mod manager";
        internal int ConvertRuns;   // checks: how many conversions started
        internal ConvertForm LastConvert;   // checks: the last window's results

        internal List<ArchiveConvert.Candidate> ConvertCandidates()
        {
            IEnumerable<string> sources = library.Where(e => e != null && e.Kind != "Library" && !String.IsNullOrEmpty(e.SourcePath) && ArchiveConvert.Convertible(e.SourcePath)).Select(e => e.SourcePath);
            return ArchiveConvert.Survey(sources, delegate(string s) { return LinkedFluffy(s).Count > 0 ? ConvertFluffySkip : null; });
        }

        // The first question's text (null when nothing converts; then why is in `none`).
        internal static string ConvertQuestion(List<ArchiveConvert.Candidate> candidates, Func<string, long> freeSpace, out string none)
        {
            List<ArchiveConvert.Candidate> go = candidates.Where(c => c.Skip == null).ToList(), out_ = candidates.Where(c => c.Skip != null).ToList();
            none = null;
            if (go.Count == 0)
            {
                none = candidates.Count == 0 ? "There are no RAR or 7Z mods in your libraries." : "None of your " + candidates.Count + " RAR and 7Z mods can be converted. " + LeftOut(out_);
                return null;
            }
            int rar = go.Count(c => c.Source.EndsWith(".rar", StringComparison.OrdinalIgnoreCase)), sevenZ = go.Count - rar;
            string kinds = rar > 0 && sevenZ > 0 ? rar + " RAR and " + sevenZ + " 7Z mods" : rar > 0 ? rar + " RAR " + (rar == 1 ? "mod" : "mods") : sevenZ + " 7Z " + (sevenZ == 1 ? "mod" : "mods");
            long bytes = go.Sum(c => c.Size);
            string text = "Convert " + kinds + " to ZIP?\n\nZIP mods open and preview much faster. Each mod gets a ZIP of the same name beside it, checked file by file before it counts. Installed copies, recipes and presets switch to the ZIPs.";
            text += "\n\nThe originals take " + ConvertForm.SizeText(bytes) + ". The ZIPs need about as much space again, often a little more, until you remove the originals (you can at the end).";
            foreach (var drive in go.GroupBy(c => DriveOf(c.Source), StringComparer.OrdinalIgnoreCase))
            {
                long need = drive.Sum(c => c.Size), free = freeSpace(drive.First().Source);
                if (free >= 0 && free < need + need / 4) text += "\n\n" + drive.Key + " has " + ConvertForm.SizeText(free) + " free, which may not be enough. Mods that don't fit are skipped.";
            }
            if (out_.Count > 0) text += "\n\n" + out_.Count + " left out. " + LeftOut(out_);
            text += "\n\nYou can cancel at any time; the mod being converted finishes first.";
            return text;
        }
        // "7: installed in the external mod manager. 2: a ZIP of the same name is already there."
        private static string LeftOut(List<ArchiveConvert.Candidate> left)
        {
            var groups = left.GroupBy(c => c.Skip).OrderByDescending(g => g.Count()).ToList();
            string text = String.Join(" ", groups.Take(4).Select(g => g.Count() + ": " + g.Key.TrimEnd('.') + "."));
            return groups.Count > 4 ? text + " " + groups.Skip(4).Sum(g => g.Count()) + ": other reasons." : text;
        }
        private static string DriveOf(string path)
        {
            try { return Path.GetPathRoot(Path.GetFullPath(path)).TrimEnd('\\'); } catch (Exception ex) { if (ex is OutOfMemoryException) throw; return ""; }
        }

        internal void ConvertArchives()
        {
            if (busy) { SetPageStatus(settingsPage, "Wait for the current task to finish first.", Palette.Accent); return; }
            if (!libraryScanned) { SetPageStatus(settingsPage, "The Mod library is still being read. Try again in a moment.", Palette.Accent); return; }
            ReplaceEngine engine = null;
            if (!String.IsNullOrWhiteSpace(config.GameRoot)) try { engine = Engine(); } catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); }
            var records = new ConvertRecords.Context { CostumeRoot = config.CostumeRoot, DataDir = AppConfig.DataDir, Engine = engine };
            string blocker = ConvertRecords.Blocker(records);
            if (blocker != null) { ThemedMessageBox.Show(this, blocker, "Convert RAR and 7Z to ZIP", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            List<ArchiveConvert.Candidate> candidates = ConvertCandidates();
            string none, question = ConvertQuestion(candidates, ArchiveConvert.FreeSpace, out none);
            if (question == null) { ThemedMessageBox.Show(this, none, "Convert RAR and 7Z to ZIP", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            bool ignored;
            if (ThemedMessageBox.Ask(this, question, "Convert RAR and 7Z to ZIP", MessageBoxIcon.Question, new[] { "Convert", "Cancel" }, 0, null, out ignored) != 0) return;
            ConvertRuns++;
            // The open mod and the background reads may hold an archive: stop them; the library is read again after.
            StopIndexing();
            if (package != null && ArchiveConvert.Convertible(package.SourcePath)) ClosePackage();
            var host = new ConvertForm.Host { SevenZip = config.SevenZipPath, Records = records, Libraries = config.Libraries.ToArray(), Protected = DeleteProtectedPaths(), Bin = RecycleBin.Current };
            using (var form = new ConvertForm(host, candidates)) { form.ShowDialog(this); LastConvert = form; }
            InvalidateInstalledSearch();
            if (LastConvert.Changed) RecipesChanged();   // recipes name the ZIPs now
            RefreshLibrary();   // the ZIPs, without the recycled originals; also restarts the background reads
        }
    }
}
