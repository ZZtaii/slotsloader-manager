using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace SlotsInstaller
{
    // 2.1.0: the app folder (executable + Data) may be moved. Records in Data keep absolute
    // paths into their own Data folder (replace-state.json, replace-journal.json, pak-backups\*.json,
    // a custom store location). When such a path names a Data folder other than the current one,
    // does not exist, and the same relative path exists under the current Data folder, it is read
    // as that current path. Paths that exist, are outside a "...\Data\" root, or whose rebased
    // file is missing too are left exactly as recorded (a missing backup is then reported).
    internal sealed class RelocationReport
    {
        internal readonly string DataDir;
        internal int Rebased, Missing, Foreign;
        internal readonly HashSet<string> OldRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        internal readonly HashSet<string> ForeignRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        internal readonly List<string> Lines = new List<string>();
        internal string FirstMissing;
        internal RelocationReport(string dataDir) { DataDir = dataDir; }
        internal bool Any { get { return Rebased > 0 || Missing > 0 || Foreign > 0; } }
        internal void AddRebased(RelocationReport other) { Rebased += other.Rebased; OldRoots.UnionWith(other.OldRoots); Foreign += other.Foreign; ForeignRoots.UnionWith(other.ForeignRoots); }

        internal string Summary(string file)
        {
            var parts = new List<string>();
            if (Rebased > 0) parts.Add(Rebased + " recorded path" + (Rebased == 1 ? "" : "s") + " rebased from " + String.Join(", ", OldRoots) + " to " + DataDir);
            if (Missing > 0) parts.Add(Missing + " recorded path" + (Missing == 1 ? "" : "s") + " from a moved Data folder missing in both places (kept as recorded; first: " + FirstMissing + ")");
            if (Foreign > 0) parts.Add(Foreign + " recorded path" + (Foreign == 1 ? "" : "s") + " still " + (Foreign == 1 ? "points" : "point") + " into another app folder's Data that exists (" + String.Join(", ", ForeignRoots) + "); left as recorded");
            return file + ": " + String.Join("; ", parts) + ".";
        }
    }

    internal static class DataRelocation
    {
        // The current Data folder's full path without a trailing separator.
        internal static string Normalize(string dataDir) { return Path.GetFullPath(dataDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }

        private static bool Exists(string path) { try { return File.Exists(path) || Directory.Exists(path); } catch { return false; } }

        internal static bool IsUnderOrSame(string path, string root)
        {
            if (String.IsNullOrEmpty(path) || String.IsNullOrEmpty(root)) return false;
            try
            {
                string p = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return p.Equals(r, StringComparison.OrdinalIgnoreCase) || p.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        // Splits a recorded path at each folder named like the current Data folder (outermost first):
        // (old Data root, path relative to it). Not rooted, unparsable, or inside the current Data
        // folder: none.
        internal static List<KeyValuePair<string, string>> DataRoots(string path, string dataDir)
        {
            var result = new List<KeyValuePair<string, string>>();
            if (String.IsNullOrWhiteSpace(path) || String.IsNullOrWhiteSpace(dataDir)) return result;
            string full, current;
            try
            {
                if (!Path.IsPathRooted(path)) return result;
                full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                current = Normalize(dataDir);
            }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; return result; }
            if (IsUnderOrSame(full, current)) return result;
            string leaf = Path.GetFileName(current);
            if (String.IsNullOrEmpty(leaf)) return result;
            string separator = Path.DirectorySeparatorChar.ToString();
            string[] parts = full.Split(Path.DirectorySeparatorChar);
            for (int i = 1; i < parts.Length - 1; i++)
            {
                if (!parts[i].Equals(leaf, StringComparison.OrdinalIgnoreCase)) continue;
                string relative = String.Join(separator, parts, i + 1, parts.Length - i - 1);
                if (relative.Length == 0 || relative.Split(Path.DirectorySeparatorChar).Any(x => x.Length == 0 || x == ".." || x == ".")) continue;
                result.Add(new KeyValuePair<string, string>(String.Join(separator, parts, 0, i + 1), relative));
            }
            return result;
        }
        // The current-Data equivalent of a recorded path that does not exist, or null.
        internal static string Candidate(string path, string dataDir, out string oldRoot)
        {
            oldRoot = null;
            string current = Normalize(dataDir);
            foreach (var split in DataRoots(path, dataDir))
            {
                string candidate = Path.Combine(current, split.Value);
                if (Exists(candidate)) { oldRoot = split.Key; return candidate; }
            }
            return null;
        }

        // Rebases one recorded path (see the class comment). `excluded` roots (the game folder) are never touched.
        internal static string Rebase(string path, string dataDir, RelocationReport report, params string[] excluded)
        {
            if (String.IsNullOrWhiteSpace(path)) return path;
            foreach (string root in excluded ?? new string[0]) if (IsUnderOrSame(path, root)) return path;
            if (!DataRoots(path, dataDir).Any()) return path;
            string oldRoot;
            string candidate = Candidate(path, dataDir, out oldRoot);
            if (Exists(path))
            {
                // Copied rather than moved: the old folder is still there and still used.
                if (candidate != null && report != null) { report.Foreign++; report.ForeignRoots.Add(oldRoot); }
                return path;
            }
            if (candidate == null)
            {
                if (report != null) { report.Missing++; if (report.FirstMissing == null) report.FirstMissing = path; }
                return path;
            }
            if (report != null) { report.Rebased++; report.OldRoots.Add(oldRoot); }
            return candidate;
        }

        // A path that may not exist yet (a folder about to be created, or already deleted): rebased
        // through its nearest ancestor that exists under the current Data folder. Left as recorded
        // when an ancestor still exists at the recorded location or nothing matches.
        internal static string RebaseTarget(string path, string dataDir, RelocationReport report, params string[] excluded)
        {
            if (String.IsNullOrWhiteSpace(path)) return path;
            foreach (string root in excluded ?? new string[0]) if (IsUnderOrSame(path, root)) return path;
            if (DataRoots(path, dataDir).Count == 0 || Exists(path)) return Rebase(path, dataDir, report, excluded);
            string current = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), suffix = "";
            while (!String.IsNullOrEmpty(current) && DataRoots(current, dataDir).Count > 0)
            {
                if (Exists(current)) return path;
                string oldRoot, candidate = Candidate(current, dataDir, out oldRoot);
                if (candidate != null)
                {
                    if (report != null) { report.Rebased++; report.OldRoots.Add(oldRoot); }
                    return suffix.Length == 0 ? candidate : Path.Combine(candidate, suffix);
                }
                suffix = suffix.Length == 0 ? Path.GetFileName(current) : Path.Combine(Path.GetFileName(current), suffix);
                current = Path.GetDirectoryName(current);
            }
            return path;
        }

        // ---- Logging: one line per record file, old root and outcome per process ("once per load").
        private static readonly object logLock = new object();
        private static readonly HashSet<string> logged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        internal static void Log(RelocationReport report, string file)
        {
            if (report == null || !report.Any) return;
            string line = report.Summary(file);
            lock (logLock) { if (!logged.Add(report.DataDir + "|" + line)) return; }
            WriteLog(report.DataDir, "Relocation: " + line);
        }
        internal static void WriteLog(string dataDir, string text)
        {
            try
            {
                Directory.CreateDirectory(dataDir);
                File.AppendAllText(Path.Combine(dataDir, "log.txt"), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + Environment.NewLine + text + Environment.NewLine + Environment.NewLine, new UTF8Encoding(false));
            }
            catch { }
        }
    }
}
