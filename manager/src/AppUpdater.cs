using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace SlotsInstaller
{
    // 2.4.0 Manager self-update. Stage: the verified portable zip is extracted to Data\updates\<version>, which
    // must hold exactly the release's 7 files with an exe whose FileVersion matches the tag. Apply: the current
    // 7 files are copied to Data\updates\previous, the running exes are renamed to *.old (Windows allows renaming
    // a running exe), the new 7 files are copied in; any failure puts every file back. The *.old files are
    // deleted at the next start. Rollback applies Data\updates\previous the same way. Nothing else in Data or in
    // the app folder is touched.
    internal static class AppUpdater
    {
        internal static readonly string[] ReleaseFiles = { "Slotsloader Manager.exe", "Slotsloader Manager.exe.config", "Slotsloader CLI.exe", "Slotsloader CLI.exe.config", "README.md", "CLI.md", "VALIDATION.txt" };
        internal static readonly string[] Executables = { "Slotsloader Manager.exe", "Slotsloader CLI.exe" };
        internal static int FailAfterCopies = -1;   // tests: throw after this many files were copied in
        internal static bool FailAfterSevenZip;     // tests: throw once the 7-Zip files are in (1.0.11)
        internal static string UpdatesDir(string dataDir) { return Path.Combine(dataDir, "updates"); }
        internal static string PreviousDir(string dataDir) { return Path.Combine(UpdatesDir(dataDir), "previous"); }
        private static string AppliedRecord(string dataDir) { return Path.Combine(UpdatesDir(dataDir), "applied.txt"); }

        // Extracts and checks a downloaded portable zip; returns the staged folder.
        internal static string Stage(string zipPath, string version, string dataDir)
        {
            string staged = Path.Combine(UpdatesDir(dataDir), Safe(version));
            if (Directory.Exists(staged)) Directory.Delete(staged, true);
            Directory.CreateDirectory(staged);
            try
            {
                using (ZipArchive zip = ZipFile.OpenRead(zipPath))
                {
                    var files = zip.Entries.Where(e => e.Name.Length > 0).ToList();
                    // The release zip holds the 7 files at its root (a single wrapper folder is accepted).
                    string prefix = files.Count > 0 && files.All(e => e.FullName.Contains('/')) && files.Select(e => e.FullName.Substring(0, e.FullName.IndexOf('/') + 1)).Distinct().Count() == 1 ? files[0].FullName.Substring(0, files[0].FullName.IndexOf('/') + 1) : "";
                    var names = files.Select(e => e.FullName.Substring(prefix.Length)).ToList();
                    var missing = ReleaseFiles.Where(f => !names.Contains(f, StringComparer.OrdinalIgnoreCase)).ToList();
                    if (missing.Count > 0) throw new IOException("The update zip is not a Slotsloader Manager release (missing " + String.Join(", ", missing) + ").");
                    var extra = names.Where(n => !ReleaseFiles.Contains(n, StringComparer.OrdinalIgnoreCase) && !IsBundledLoader(n) && !IsBundledSevenZip(n)).ToList();
                    if (extra.Count > 0) throw new IOException("The update zip has unexpected files: " + String.Join(", ", extra.Take(5)) + ".");
                    if (names.Count(IsBundledLoader) > 1) throw new IOException("The update zip has more than one bundled Slotsloader zip.");
                    int seven = names.Count(IsBundledSevenZip);
                    if (seven != 0 && seven != SevenZipTool.BundleFiles.Length) throw new IOException("The update zip's 7-Zip folder is incomplete (it needs " + String.Join(", ", SevenZipTool.BundleFiles) + ").");
                    foreach (ZipArchiveEntry e in files)
                    {
                        string name = e.FullName.Substring(prefix.Length), target = Path.Combine(staged, name.Replace('/', '\\'));
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        e.ExtractToFile(target, true);
                    }
                }
                Version want = UpdateCheck.ParseVersion(version);
                string fileVersion = FileVersionInfo.GetVersionInfo(Path.Combine(staged, "Slotsloader Manager.exe")).FileVersion ?? "";
                Version got = UpdateCheck.ParseVersion(fileVersion);
                if (want == null || got == null || Normalize(got) != Normalize(want)) throw new IOException("The update's Slotsloader Manager.exe is version " + fileVersion + ", not " + version + "; it was not installed.");
                return staged;
            }
            catch { try { Directory.Delete(staged, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } throw; }
        }
        private static Version Normalize(Version v) { return new Version(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision)); }

        // 2.5.9: a release may also carry the SlotsLoader zip it installs, as "Slotsloader/<name>.zip" (one plain file
        // name, no deeper folders). Apply replaces the app folder's bundled zips with it; the previous ones are kept for
        // rollback and put back on failure. A release without one leaves the bundled zips as they are.
        internal static bool IsBundledLoader(string name)
        {
            string prefix = LoaderRelease.BundleFolder + "/";
            if (name == null || !name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
            string file = name.Substring(prefix.Length);
            return file.Length > 4 && file.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && file.IndexOfAny(new[] { '/', '\\', ':' }) < 0 && file.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && file != ".." && !file.StartsWith(".");
        }
        private static List<string> BundledZips(string folder) { return Directory.Exists(folder) ? Directory.GetFiles(folder, "*.zip").ToList() : new List<string>(); }
        private static void CopyBundled(string from, string to)
        {
            var zips = BundledZips(Path.Combine(from, LoaderRelease.BundleFolder));
            if (zips.Count == 0) return;
            string target = Path.Combine(to, LoaderRelease.BundleFolder); Directory.CreateDirectory(target);
            foreach (string zip in zips) File.Copy(zip, Path.Combine(target, Path.GetFileName(zip)), true);
        }
        // The app folder's bundled zips become exactly the source's (only when the source has one).
        private static bool ReplaceBundled(string source, string appDir)
        {
            var incoming = BundledZips(Path.Combine(source, LoaderRelease.BundleFolder));
            if (incoming.Count == 0) return false;
            string target = Path.Combine(appDir, LoaderRelease.BundleFolder); Directory.CreateDirectory(target);
            var keep = new HashSet<string>(incoming.Select(Path.GetFileName), StringComparer.OrdinalIgnoreCase);
            foreach (string old in BundledZips(target)) if (!keep.Contains(Path.GetFileName(old))) File.Delete(old);
            foreach (string zip in incoming) File.Copy(zip, Path.Combine(target, Path.GetFileName(zip)), true);
            return true;
        }
        // 1.0.11: a release may also carry 7-Zip for RAR/7Z as "7-Zip/<file>" (exactly SevenZipTool.BundleFiles, all or
        // none). Apply replaces the app folder's copy with it; files in use (a running 7z.exe) are renamed to *.old
        // first, like the exes, and removed at the next start. A release without it leaves the app's copy as it is.
        internal static bool IsBundledSevenZip(string name)
        {
            string prefix = SevenZipTool.BundleFolder + "/";
            if (name == null || !name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
            return SevenZipTool.BundleFiles.Contains(name.Substring(prefix.Length), StringComparer.OrdinalIgnoreCase);
        }
        private static bool HasSevenZip(string folder) { return SevenZipTool.BundleFiles.All(f => File.Exists(Path.Combine(folder, SevenZipTool.BundleFolder, f))); }
        private static void CopySevenZip(string from, string to)
        {
            if (!HasSevenZip(from)) return;
            string target = Path.Combine(to, SevenZipTool.BundleFolder); Directory.CreateDirectory(target);
            foreach (string f in SevenZipTool.BundleFiles) File.Copy(Path.Combine(from, SevenZipTool.BundleFolder, f), Path.Combine(target, f), true);
        }
        // Moves the app's current 7-Zip files aside (*.old) and copies the source's in; `moved`/`added` record it for undo.
        private static void ReplaceSevenZip(string source, string appDir, List<string> moved, List<string> added)
        {
            if (!HasSevenZip(source)) return;
            string target = Path.Combine(appDir, SevenZipTool.BundleFolder); Directory.CreateDirectory(target);
            foreach (string f in SevenZipTool.BundleFiles)
            {
                string path = Path.Combine(target, f), old = path + ".old";
                if (File.Exists(old)) File.Delete(old);
                if (File.Exists(path)) { File.Move(path, old); moved.Add(path); }
                File.Copy(Path.Combine(source, SevenZipTool.BundleFolder, f), path, true); added.Add(path);
            }
            SevenZipTool.ClearCache();
        }
        private static void UndoSevenZip(List<string> moved, List<string> added)
        {
            foreach (string path in added) TryDelete(path);
            foreach (string path in moved) try { File.Move(path + ".old", path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            SevenZipTool.ClearCache();
        }
        private static string Safe(string version) { return new string((version ?? "").Where(c => Char.IsLetterOrDigit(c) || c == '.' || c == '-').ToArray()); }

        // Copies source's 7 files into appDir; previous (when not null) receives the current 7 first.
        internal static void Apply(string appDir, string source, string previous, string fromVersion, string toVersion, string dataDir)
        {
            if (LinuxPlatform.IsLinux) throw new IOException(LinuxPlatform.UpdateMessage);
            appDir = Path.GetFullPath(appDir);
            if (ReleaseFiles.Any(f => !File.Exists(Path.Combine(source, f)))) throw new IOException("The files to install are incomplete (" + source + ").");
            // Writable? (a probe file, removed again)
            string probe = Path.Combine(appDir, ".update-probe-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try { File.WriteAllText(probe, ""); File.Delete(probe); }
            catch (Exception ex) { if (ex is IOException || ex is UnauthorizedAccessException) throw new IOException("The Manager folder " + appDir + " is not writable (" + ex.Message.TrimEnd('.') + "); nothing was changed."); throw; }
            if (previous != null)
            {
                if (Directory.Exists(previous)) Directory.Delete(previous, true);
                Directory.CreateDirectory(previous);
                foreach (string f in ReleaseFiles) { string from = Path.Combine(appDir, f); if (File.Exists(from)) File.Copy(from, Path.Combine(previous, f), true); }
                CopyBundled(appDir, previous);   // 2.5.9
                CopySevenZip(appDir, previous);   // 1.0.11
            }
            // Backups of the non-exe files to put back on failure (the exes are kept as *.old).
            string undo = Path.Combine(UpdatesDir(dataDir), "undo-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(undo);
            var renamed = new List<string>(); var copied = new List<string>(); bool bundleReplaced = false;
            var sevenMoved = new List<string>(); var sevenAdded = new List<string>();
            try
            {
                foreach (string f in ReleaseFiles.Except(Executables)) { string from = Path.Combine(appDir, f); if (File.Exists(from)) File.Copy(from, Path.Combine(undo, f), true); }
                CopyBundled(appDir, undo);
                foreach (string exe in Executables)
                {
                    string path = Path.Combine(appDir, exe), old = path + ".old";
                    if (File.Exists(old)) File.Delete(old);
                    if (File.Exists(path)) { File.Move(path, old); renamed.Add(exe); }
                }
                int n = 0;
                foreach (string f in ReleaseFiles)
                {
                    if (FailAfterCopies >= 0 && n >= FailAfterCopies) throw new IOException("Test failure after " + n + " copied files.");
                    File.Copy(Path.Combine(source, f), Path.Combine(appDir, f), true); copied.Add(f); n++;
                }
                bundleReplaced = true; ReplaceBundled(source, appDir);
                ReplaceSevenZip(source, appDir, sevenMoved, sevenAdded);   // 1.0.11
                if (FailAfterSevenZip) throw new IOException("Test failure after the 7-Zip files.");
                File.WriteAllText(AppliedRecord(dataDir), (fromVersion ?? "") + " -> " + (toVersion ?? "") + " · " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            }
            catch
            {
                UndoSevenZip(sevenMoved, sevenAdded);
                foreach (string f in copied.Where(f => Executables.Contains(f))) TryDelete(Path.Combine(appDir, f));
                foreach (string exe in renamed) { string path = Path.Combine(appDir, exe); TryDelete(path); try { File.Move(path + ".old", path); } catch (IOException) { } }
                foreach (string f in ReleaseFiles.Except(Executables)) { string saved = Path.Combine(undo, f); if (File.Exists(saved)) try { File.Copy(saved, Path.Combine(appDir, f), true); } catch (IOException) { } else if (copied.Contains(f)) TryDelete(Path.Combine(appDir, f)); }
                if (bundleReplaced && BundledZips(Path.Combine(source, LoaderRelease.BundleFolder)).Count > 0)
                {
                    // Back to the bundled zips from before the update.
                    foreach (string zip in BundledZips(Path.Combine(appDir, LoaderRelease.BundleFolder))) TryDelete(zip);
                    try { CopyBundled(undo, appDir); } catch (IOException) { }
                }
                throw;
            }
            finally { try { Directory.Delete(undo, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        }
        private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

        // At start-up: remove the *.old executables of an applied update and log it once.
        internal static string CleanupAfterUpdate(string appDir, string dataDir)
        {
            var removed = new List<string>();
            foreach (string exe in Executables)
            {
                string old = Path.Combine(appDir, exe + ".old");
                if (!File.Exists(old)) continue;
                try { File.Delete(old); removed.Add(Path.GetFileName(old)); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            foreach (string f in SevenZipTool.BundleFiles)   // 1.0.11
            {
                string old = Path.Combine(appDir, SevenZipTool.BundleFolder, f + ".old");
                if (!File.Exists(old)) continue;
                try { File.Delete(old); removed.Add(SevenZipTool.BundleFolder + "\\" + f + ".old"); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            string record = AppliedRecord(dataDir), line = null;
            if (File.Exists(record))
            {
                line = "Slotsloader Manager updated (" + File.ReadAllText(record).Trim() + ")" + (removed.Count > 0 ? "; removed " + String.Join(", ", removed) : "");
                Program.LogLine(line);
                try { File.Delete(record); } catch (IOException) { }
            }
            return line;
        }

        internal static void Rollback(string appDir, string dataDir)
        {
            string previous = PreviousDir(dataDir);
            if (!Directory.Exists(previous) || ReleaseFiles.Any(f => !File.Exists(Path.Combine(previous, f)))) throw new IOException("No previous version is kept in " + previous + ".");
            string version = FileVersionInfo.GetVersionInfo(Path.Combine(previous, "Slotsloader Manager.exe")).FileVersion;
            string temp = Path.Combine(UpdatesDir(dataDir), "rollback-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(temp);
            try
            {
                foreach (string f in ReleaseFiles) File.Copy(Path.Combine(previous, f), Path.Combine(temp, f), true);
                CopyBundled(previous, temp);   // 2.5.9
                CopySevenZip(previous, temp);  // 1.0.11
                Apply(appDir, temp, null, SafeHttp.AppVersion(), version + " (rollback)", dataDir);
            }
            finally { try { Directory.Delete(temp, true); } catch (IOException) { } }
        }
    }
}
