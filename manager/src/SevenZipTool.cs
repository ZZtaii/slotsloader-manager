using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace SlotsInstaller
{
    // 1.0.11: RAR and 7Z archives are read with 7-Zip. Which 7z.exe runs: the one chosen in Settings, else an
    // installed 7-Zip (its registry key, then Program Files), else the copy the release ships in "7-Zip\" beside
    // the exe. An empty or missing Settings path means "find one", so a PC without 7-Zip still reads RAR/7Z.
    // An installed 7-Zip older than the bundled copy gives way to it (7-Zip before 15.06 can't read RAR5).
    internal static class SevenZipTool
    {
        internal const string BundleFolder = "7-Zip";
        // All three ship together (7z.exe needs 7z.dll; License.txt is 7-Zip's licence, which must travel with it).
        internal static readonly string[] BundleFiles = { "7z.exe", "7z.dll", "License.txt" };
        internal static string AppDirOverride;                    // tests: the folder that holds "7-Zip\"
        internal static Func<IEnumerable<string>> InstalledOverride;   // tests: installed candidates instead of the PC's
        private static readonly object CacheLock = new object();
        private static string cachedFor, cachedPath, cachedSource, cachedOlder;

        internal static string BundledExe { get { return Path.Combine(AppDirOverride ?? AppDomain.CurrentDomain.BaseDirectory, BundleFolder, "7z.exe"); } }

        internal static string Resolve(string configured) { string source; return Resolve(configured, out source); }
        internal static string Resolve(string configured, out string source) { string older; return Resolve(configured, out source, out older); }

        // The 7z.exe to run, or "" when none is found. source: "settings", "installed", "bundled" or "none";
        // older: the installed 7z.exe passed over because the bundled copy is newer (else null).
        private static string Resolve(string configured, out string source, out string older)
        {
            configured = (configured ?? "").Trim();
            older = null;
            if (configured.Length > 0 && File.Exists(configured)) { source = "settings"; return configured; }
            lock (CacheLock)
            {
                if (cachedFor == configured && cachedPath != null && (cachedPath.Length == 0 || File.Exists(cachedPath))) { source = cachedSource; older = cachedOlder; return cachedPath; }
            }
            string found = Installed().FirstOrDefault(Usable), how = "installed";
            if (found != null && Usable(BundledExe) && IsOlder(VersionOf(found), VersionOf(BundledExe))) { older = found; found = null; }
            if (found == null) { found = Usable(BundledExe) ? BundledExe : ""; how = found.Length > 0 ? "bundled" : "none"; }
            lock (CacheLock) { cachedFor = configured; cachedPath = found; cachedSource = how; cachedOlder = older; }
            source = how; return found;
        }
        internal static void ClearCache() { lock (CacheLock) { cachedFor = cachedPath = cachedSource = cachedOlder = null; } }

        // One line for Settings: which 7-Zip RAR/7Z use.
        internal static string Describe(string configured)
        {
            string source, older, exe = Resolve(configured, out source, out older);
            switch (source)
            {
                case "settings": return "Found";
                case "installed": return "Using your installed 7-Zip · " + exe;
                case "bundled": return "Using the 7-Zip that comes with the Manager" + (older != null ? " · newer than your installed 7-Zip " + Label(VersionOf(older)) : "");
                default: return "Not found · ZIP and folders still work";
            }
        }

        // A 7z.exe's 7-Zip version, read from the 7z.dll beside it (the formats live there); null when it has none.
        internal static Version VersionOf(string exe)
        {
            try
            {
                FileVersionInfo info = FileVersionInfo.GetVersionInfo(Path.Combine(Path.GetDirectoryName(exe), "7z.dll"));
                var version = new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart);
                return version == new Version(0, 0, 0, 0) ? null : version;
            }
            catch (Exception ex) { if (ex is IOException || ex is ArgumentException || ex is NotSupportedException) return null; throw; }
        }
        // Unknown versions never count as older: the installed copy keeps its place.
        private static bool IsOlder(Version installed, Version bundled) { return installed != null && bundled != null && installed < bundled; }
        internal static string Label(Version version) { return version.Major + "." + version.Minor.ToString("00"); }

        private static bool Usable(string exe)
        {
            try { return !String.IsNullOrEmpty(exe) && File.Exists(exe) && File.Exists(Path.Combine(Path.GetDirectoryName(exe), "7z.dll")); }
            catch (Exception ex) { if (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException) return false; throw; }
        }

        private static IEnumerable<string> Installed()
        {
            if (InstalledOverride != null) { foreach (string p in InstalledOverride()) yield return p; yield break; }
            foreach (RegistryHive hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
                foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                    foreach (string value in new[] { "Path64", "Path" })
                    {
                        string folder = RegistryFolder(hive, view, value);
                        if (!String.IsNullOrWhiteSpace(folder)) yield return Path.Combine(folder.Trim(), "7z.exe");
                    }
            foreach (string variable in new[] { "ProgramW6432", "ProgramFiles", "ProgramFiles(x86)" })
            {
                string root = Environment.GetEnvironmentVariable(variable);
                if (!String.IsNullOrWhiteSpace(root)) yield return Path.Combine(root, "7-Zip", "7z.exe");
            }
        }

        private static string RegistryFolder(RegistryHive hive, RegistryView view, string value)
        {
            try
            {
                using (RegistryKey root = RegistryKey.OpenBaseKey(hive, view))
                using (RegistryKey key = root.OpenSubKey(@"SOFTWARE\7-Zip"))
                    return key == null ? null : key.GetValue(value) as string;
            }
            catch (Exception ex) { if (ex is System.Security.SecurityException || ex is UnauthorizedAccessException || ex is IOException) return null; throw; }
        }
    }
}
