using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace SlotsInstaller
{
    // 1.0.11: RAR and 7Z archives are read with 7-Zip. Which 7z.exe runs: the one chosen in Settings, else an
    // installed 7-Zip (its registry key, then Program Files), else the copy the release ships in "7-Zip\" beside
    // the exe. An empty or missing Settings path means "find one", so a PC without 7-Zip still reads RAR/7Z.
    internal static class SevenZipTool
    {
        internal const string BundleFolder = "7-Zip";
        // All three ship together (7z.exe needs 7z.dll; License.txt is 7-Zip's licence, which must travel with it).
        internal static readonly string[] BundleFiles = { "7z.exe", "7z.dll", "License.txt" };
        internal static string AppDirOverride;                    // tests: the folder that holds "7-Zip\"
        internal static Func<IEnumerable<string>> InstalledOverride;   // tests: installed candidates instead of the PC's
        private static readonly object CacheLock = new object();
        private static string cachedFor, cachedPath, cachedSource;

        internal static string BundledExe { get { return Path.Combine(AppDirOverride ?? AppDomain.CurrentDomain.BaseDirectory, BundleFolder, "7z.exe"); } }

        internal static string Resolve(string configured) { string source; return Resolve(configured, out source); }

        // The 7z.exe to run, or "" when none is found. source: "settings", "installed", "bundled" or "none".
        internal static string Resolve(string configured, out string source)
        {
            configured = (configured ?? "").Trim();
            if (configured.Length > 0 && File.Exists(configured)) { source = "settings"; return configured; }
            lock (CacheLock)
            {
                if (cachedFor == configured && cachedPath != null && (cachedPath.Length == 0 || File.Exists(cachedPath))) { source = cachedSource; return cachedPath; }
            }
            string found = Installed().FirstOrDefault(Usable), how = "installed";
            if (found == null) { found = Usable(BundledExe) ? BundledExe : ""; how = found.Length > 0 ? "bundled" : "none"; }
            lock (CacheLock) { cachedFor = configured; cachedPath = found; cachedSource = how; }
            source = how; return found;
        }
        internal static void ClearCache() { lock (CacheLock) { cachedFor = cachedPath = cachedSource = null; } }

        // One line for Settings: which 7-Zip RAR/7Z use.
        internal static string Describe(string configured)
        {
            string source, exe = Resolve(configured, out source);
            switch (source)
            {
                case "settings": return "Found";
                case "installed": return "Using your installed 7-Zip · " + exe;
                case "bundled": return "Using the 7-Zip that comes with the Manager";
                default: return "Not found · ZIP and folders still work";
            }
        }

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
