using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace SlotsInstaller
{
    // 2.4.3: first-start defaults are detected, never compiled in. Existing users are unaffected: settings.json
    // stores GameRoot and Libraries explicitly, and detection runs only when there is no settings.json.
    internal static class InstallDetection
    {
        internal const string AppId = "1364780";
        internal const string GameFolderName = "Street Fighter 6";
        // Checks replace these so detection never looks at the real machine.
        internal static Func<IEnumerable<string>> SteamRoots = RegistrySteamRoots;
        internal static Func<IEnumerable<string>> FluffyCandidates = DefaultFluffyCandidates;

        // Steam (HKCU SteamPath, then HKLM InstallPath), its libraryfolders.vdf, the library holding
        // appmanifest_1364780.acf (its installdir), else steamapps\common\Street Fighter 6 in any library; null if none.
        internal static string FindGame() { return FindGame(SteamRoots()); }
        internal static string FindGame(IEnumerable<string> steamRoots)
        {
            var libraries = new List<string>();
            foreach (string root in steamRoots ?? new string[0])
            {
                if (String.IsNullOrWhiteSpace(root)) continue;
                string steam = Normalize(root);
                if (steam == null || !Directory.Exists(steam)) continue;
                Add(libraries, steam);
                foreach (string lib in LibraryFolders(Path.Combine(steam, "steamapps", "libraryfolders.vdf"))) Add(libraries, lib);
                foreach (string lib in LibraryFolders(Path.Combine(steam, "config", "libraryfolders.vdf"))) Add(libraries, lib);
            }
            foreach (string lib in libraries)
            {
                string manifest = Path.Combine(lib, "steamapps", "appmanifest_" + AppId + ".acf");
                if (!File.Exists(manifest)) continue;
                string dir = VdfValue(SafeRead(manifest), "installdir") ?? GameFolderName;
                string game = Path.Combine(lib, "steamapps", "common", dir);
                if (Directory.Exists(game)) return game;
            }
            foreach (string lib in libraries)
            {
                string game = Path.Combine(lib, "steamapps", "common", GameFolderName);
                if (Directory.Exists(game)) return game;
            }
            return null;
        }

        // "path" entries of libraryfolders.vdf (both the old "1" "D:\\Lib" and the new "path" "D:\\Lib" forms).
        internal static List<string> LibraryFolders(string vdf)
        {
            var result = new List<string>();
            string text = SafeRead(vdf);
            if (text == null) return result;
            foreach (Match m in Regex.Matches(text, "\"(?:path|\\d+)\"\\s+\"((?:[^\"\\\\]|\\\\.)+)\""))
            {
                string value = m.Groups[1].Value.Replace("\\\\", "\\");
                if (value.Length > 2 && value[1] == ':') Add(result, value);
            }
            return result;
        }
        internal static string VdfValue(string text, string key)
        {
            if (text == null) return null;
            Match m = Regex.Match(text, "\"" + Regex.Escape(key) + "\"\\s+\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.IgnoreCase);
            return m.Success && m.Groups[1].Value.Length > 0 ? m.Groups[1].Value.Replace("\\\\", "\\") : null;
        }

        // <Fluffy>\Games\SF6\Mods for every Fluffy install found: a running Fluffy's folder, the Fluffy folders the
        // replace settings already know, and folders named like Fluffy at the top of each fixed drive (and one level
        // under it, e.g. <drive>\Games\Fluffy), in the user's Downloads, Desktop and Documents.
        internal static List<string> FindLibraries() { return FindLibraries(FluffyCandidates()); }
        internal static List<string> FindLibraries(IEnumerable<string> candidates)
        {
            var result = new List<string>();
            foreach (string candidate in candidates ?? new string[0])
            {
                try
                {
                    string root = Normalize(candidate);
                    if (root == null) continue;
                    // A candidate may be the Fluffy folder, its Games\SF6 folder or the Mods folder itself.
                    foreach (string mods in new[] { Path.Combine(root, "Games", "SF6", "Mods"), Path.Combine(root, "Mods"), root })
                    {
                        if (!Directory.Exists(mods) || !Path.GetFileName(mods).Equals("Mods", StringComparison.OrdinalIgnoreCase)) continue;
                        string sf6 = Path.GetDirectoryName(mods);
                        if (sf6 == null || !Path.GetFileName(sf6).Equals("SF6", StringComparison.OrdinalIgnoreCase)) continue;
                        Add(result, mods); break;
                    }
                }
                catch (Exception ex) { if (ex is OutOfMemoryException) throw; }
            }
            return result;
        }

        private static IEnumerable<string> RegistrySteamRoots()
        {
            var roots = new List<string>();
            foreach (var probe in new[] {
                new { Hive = RegistryHive.CurrentUser, Key = @"Software\Valve\Steam", Value = "SteamPath" },
                new { Hive = RegistryHive.LocalMachine, Key = @"SOFTWARE\WOW6432Node\Valve\Steam", Value = "InstallPath" },
                new { Hive = RegistryHive.LocalMachine, Key = @"SOFTWARE\Valve\Steam", Value = "InstallPath" } })
            {
                try
                {
                    using (RegistryKey hive = RegistryKey.OpenBaseKey(probe.Hive, RegistryView.Registry64))
                    using (RegistryKey key = hive.OpenSubKey(probe.Key))
                    {
                        string value = key == null ? null : key.GetValue(probe.Value) as string;
                        if (!String.IsNullOrWhiteSpace(value)) roots.Add(value.Replace('/', '\\'));
                    }
                }
                catch (Exception ex) { if (ex is OutOfMemoryException) throw; }
            }
            return roots;
        }

        private static IEnumerable<string> DefaultFluffyCandidates()
        {
            var list = new List<string>();
            try
            {
                foreach (string name in SystemProcessProbe.FluffyNames)
                    foreach (Process p in Process.GetProcessesByName(name))
                        using (p) { try { string exe = p.MainModule.FileName; if (exe != null) list.Add(Path.GetDirectoryName(exe)); } catch (Exception ex) { if (ex is OutOfMemoryException) throw; } }
            }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; }
            try { list.AddRange(ReplaceSettings.Load().FluffyRoots ?? new List<string>()); } catch (Exception ex) { if (ex is OutOfMemoryException) throw; }
            var tops = new List<string>();
            try { tops.AddRange(DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).Select(d => d.RootDirectory.FullName)); } catch (Exception ex) { if (ex is OutOfMemoryException) throw; }
            foreach (Environment.SpecialFolder folder in new[] { Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.Desktop, Environment.SpecialFolder.MyDocuments })
                try { string f = Environment.GetFolderPath(folder); if (!String.IsNullOrEmpty(f)) { tops.Add(f); if (folder == Environment.SpecialFolder.UserProfile) tops.Add(Path.Combine(f, "Downloads")); } } catch (Exception ex) { if (ex is OutOfMemoryException) throw; }
            foreach (string top in tops)
            {
                foreach (string dir in SafeDirs(top))
                {
                    string name = Path.GetFileName(dir);
                    if (name.IndexOf("fluffy", StringComparison.OrdinalIgnoreCase) >= 0) list.Add(dir);
                    else if (name.Equals("Games", StringComparison.OrdinalIgnoreCase) || name.Equals("Mods", StringComparison.OrdinalIgnoreCase) || name.Equals("Tools", StringComparison.OrdinalIgnoreCase))
                        list.AddRange(SafeDirs(dir).Where(d => Path.GetFileName(d).IndexOf("fluffy", StringComparison.OrdinalIgnoreCase) >= 0));
                }
            }
            return list;
        }

        private static IEnumerable<string> SafeDirs(string dir)
        {
            try { return Directory.Exists(dir) ? Directory.GetDirectories(dir) : new string[0]; }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; return new string[0]; }
        }
        private static string SafeRead(string path)
        {
            try { return File.Exists(path) ? File.ReadAllText(path) : null; }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; return null; }
        }
        private static string Normalize(string path)
        {
            try { string full = Path.GetFullPath(path.Trim().Trim('"')); return full.Length > 3 ? full.TrimEnd('\\') : full; }   // keep "D:\" (not "D:")
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; return null; }
        }
        private static void Add(List<string> list, string path)
        {
            string n = Normalize(path);
            if (n != null && !list.Contains(n, StringComparer.OrdinalIgnoreCase)) list.Add(n);
        }
    }
}
