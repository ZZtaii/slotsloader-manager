using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace SlotsInstaller
{
    // Running processes that must not overlap PAK or game-root changes. Tests replace Current.
    internal interface IProcessProbe
    {
        bool IsGameRunning();
        bool IsFluffyRunning();
    }

    internal sealed class SystemProcessProbe : IProcessProbe
    {
        internal static readonly string[] GameNames = { "StreetFighter6" };
        internal static readonly string[] FluffyNames = { "Modmanager", "Modmanagerv2", "FluffyModManager" };
        private static bool Any(string[] names)
        {
            foreach (string name in names)
            {
                Process[] found = Process.GetProcessesByName(name);
                bool any = found.Length > 0;
                foreach (Process p in found) p.Dispose();
                if (any) return true;
            }
            return false;
        }
        public bool IsGameRunning() { return Any(GameNames); }
        public bool IsFluffyRunning() { return Any(FluffyNames); }
    }

    internal sealed class FakeProcessProbe : IProcessProbe
    {
        internal bool Game, Fluffy;
        public bool IsGameRunning() { return Game; }
        public bool IsFluffyRunning() { return Fluffy; }
    }

    // Starts SF6. Tests replace Current with a recording launcher.
    internal interface IGameLauncher { void Launch(string gameRoot); }
    internal sealed class SteamLauncher : IGameLauncher
    {
        internal const string SteamUrl = "steam://rungameid/1364780";
        public void Launch(string gameRoot) { Process.Start(new ProcessStartInfo(SteamUrl) { UseShellExecute = true }); }
    }

    internal static class GameProcesses
    {
        internal static IProcessProbe Probe = new SystemProcessProbe();
        internal static IGameLauncher Launcher = new SteamLauncher();

        // Throws when SF6 or Fluffy Mod Manager runs: PAK and game-root files must not change then.
        internal static void RequireIdle(string action)
        {
            if (Probe.IsGameRunning()) throw new IOException("Close SF6 to " + action + ".");
            if (Probe.IsFluffyRunning()) throw new IOException(FluffyPakMessage(action));
        }
        // 2.1.0: only the game blocks this (REFramework config edits, loose and tool-mod files).
        internal static void RequireGameClosed(string action)
        {
            if (Probe.IsGameRunning()) throw new IOException("Close SF6 to " + action + ".");
        }
        // Fluffy Mod Manager edits the same PAK entries with its own method: PAK changes wait until it is closed.
        internal static string FluffyPakMessage(string action) { return "Close the external mod manager to " + action + ": it edits the same PAK entries (loose file and tool mod changes work while it is open)."; }
    }

    // Mods Fluffy Mod Manager has installed (Games\SF6\installed.ini). A missing file means none.
    internal sealed class FluffyMod
    {
        internal string Section, Name, InstallType, Root, ModId;
        internal int Index;                                   // order in installed.ini (Fluffy appends each install)
        internal List<string> Files = new List<string>();
        internal string IniPath { get { return Path.Combine(Root ?? "", "installed.ini"); } }
        // installtype=normal (or none): copied files the Manager can remove like Fluffy does. Anything else
        // (invalidate, pakcopy_*) changed PAK entries with Fluffy's own record (ArchiveList.bin): Fluffy only.
        internal bool CopiedFiles { get { return String.IsNullOrEmpty(InstallType) || InstallType.Equals("normal", StringComparison.OrdinalIgnoreCase); } }
        // 2.4.2: the two PAK methods Fluffy uses for SF6 (seen 2026-10-14): invalidate (copied files + zeroed base rows)
        // and pakcopy_reengine_invalidate (also a patch PAK with empty dummies below it). The Manager uninstalls both
        // (FluffyPakUninstall.cs); any other installtype stays Fluffy's.
        internal bool PakInstall { get { return !CopiedFiles && (InstallType.Equals("invalidate", StringComparison.OrdinalIgnoreCase) || InstallType.Equals("pakcopy_reengine_invalidate", StringComparison.OrdinalIgnoreCase)); } }
        // 1.0.1 (user decision): the Manager no longer uninstalls another manager's mods (each install type had its own
        // edge cases); that manager uninstalls them. Undo of an earlier uninstall (FluffyRemoved) still works.
        internal string UninstallBlocker { get { return "Uninstall it in the external mod manager."; } }
    }

    internal sealed class FluffyInstalls
    {
        internal List<string> Roots = new List<string>();
        internal List<FluffyMod> Mods = new List<FluffyMod>();
        internal List<string> Warnings = new List<string>();
        private readonly Dictionary<string, FluffyMod> owners = new Dictionary<string, FluffyMod>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<FluffyMod>> allOwners = new Dictionary<string, List<FluffyMod>>(StringComparer.OrdinalIgnoreCase);
        // Every Fluffy mod listing the path, in installed.ini order (the last one's copy is in the game folder).
        internal List<FluffyMod> OwnersOf(string relativePath) { List<FluffyMod> list; return allOwners.TryGetValue(Key(relativePath), out list) ? list : new List<FluffyMod>(); }
        internal string Stamp;   // installed.ini length + write time of every root (changes when Fluffy installs or uninstalls)

        internal static string Key(string relativePath)
        {
            string p = PakHash.Normalize(relativePath).TrimStart('/');
            return p;
        }

        // The Fluffy mod whose copy of the path is in the game folder: the last section listing it.
        internal FluffyMod Owner(string relativePath)
        {
            FluffyMod mod; return owners.TryGetValue(Key(relativePath), out mod) ? mod : null;
        }
        internal FluffyMod Find(string root, string section)
        {
            return Mods.FirstOrDefault(m => String.Equals(m.Section, section, StringComparison.Ordinal) && (root == null || String.Equals(Path.GetFullPath(m.Root).TrimEnd('\\'), Path.GetFullPath(root).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)));
        }

        // installed.ini changes when Fluffy installs or uninstalls: length and write time of every root's file.
        internal static string StampOf(IEnumerable<string> roots)
        {
            var parts = new List<string>();
            foreach (string root in roots ?? new string[0])
            {
                try { var info = new FileInfo(Path.Combine(root, "installed.ini")); parts.Add(info.Exists ? info.Length + "@" + info.LastWriteTimeUtc.Ticks : "-"); }
                catch (Exception ex) { if (ex is OutOfMemoryException) throw; parts.Add("?"); }
            }
            return String.Join("|", parts);
        }

        // Fluffy rewrites installed.ini through installed.ini.new: a read may meet the file locked or absent for
        // a moment. Shared read with a few retries; absent means no mods.
        internal static byte[] ReadShared(string path)
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    if (!File.Exists(path)) return null;
                    using (var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var ms = new MemoryStream()) { s.CopyTo(ms); return ms.ToArray(); }
                }
                catch (FileNotFoundException) { return null; }
                catch (IOException) { if (attempt >= 5) throw; System.Threading.Thread.Sleep(60); }
                catch (UnauthorizedAccessException) { if (attempt >= 5) throw; System.Threading.Thread.Sleep(60); }
            }
        }
        internal IEnumerable<string> OwnedPaths { get { return owners.Keys; } }

        // Fluffy instance folders: <Fluffy>\Games\SF6 next to a configured Mods library.
        internal static List<string> DetectRoots(IEnumerable<string> libraries, IEnumerable<string> extra)
        {
            var roots = new List<string>();
            foreach (string lib in (libraries ?? new string[0]).Concat(extra ?? new string[0]))
            {
                try
                {
                    if (String.IsNullOrWhiteSpace(lib)) continue;
                    string full = Path.GetFullPath(lib).TrimEnd(Path.DirectorySeparatorChar);
                    string candidate = Path.GetFileName(full).Equals("Mods", StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(full) : full;
                    if (candidate == null || !Directory.Exists(candidate)) continue;
                    bool looksFluffy = File.Exists(Path.Combine(candidate, "installed.ini")) || File.Exists(Path.Combine(candidate, "ModinfoCache.bin")) || File.Exists(Path.Combine(candidate, "ArchiveList.bin")) || Directory.Exists(Path.Combine(candidate, "Backup"));
                    if (looksFluffy && !roots.Contains(candidate, StringComparer.OrdinalIgnoreCase)) roots.Add(candidate);
                }
                catch (Exception ex) { if (ex is OutOfMemoryException) throw; }
            }
            return roots;
        }

        internal static FluffyInstalls Load(IEnumerable<string> roots)
        {
            var result = new FluffyInstalls();
            foreach (string root in roots ?? new string[0])
            {
                result.Roots.Add(root);
                string ini = Path.Combine(root, "installed.ini");
                try
                {
                    byte[] bytes = ReadShared(ini);
                    if (bytes == null) continue;
                    foreach (FluffyMod mod in Parse(DecodeIni(bytes), root))
                    {
                        result.Mods.Add(mod);
                        foreach (string f in mod.Files)
                        {
                            string key = Key(f);
                            result.owners[key] = mod;
                            List<FluffyMod> list; if (!result.allOwners.TryGetValue(key, out list)) result.allOwners[key] = list = new List<FluffyMod>();
                            if (!list.Contains(mod)) list.Add(mod);
                        }
                    }
                }
                catch (Exception ex) { if (ex is OutOfMemoryException) throw; result.Warnings.Add("Could not read " + ini + ": " + ex.Message); }
            }
            result.Stamp = StampOf(result.Roots);
            return result;
        }

        // installed.ini text: UTF-8 (with or without BOM); the same decoding is used to find a section's bytes.
        internal static string DecodeIni(byte[] bytes)
        {
            int skip = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
            return new UTF8Encoding(false).GetString(bytes, skip, bytes.Length - skip);
        }

        internal static List<FluffyMod> Parse(string text, string root)
        {
            var mods = new List<FluffyMod>(); FluffyMod current = null;
            foreach (string raw in (text ?? "").Replace("\r", "").Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith(";")) continue;
                if (line.StartsWith("[") && line.EndsWith("]")) { current = new FluffyMod { Section = line.Substring(1, line.Length - 2), Root = root, Index = mods.Count }; current.Name = current.Section; mods.Add(current); continue; }
                if (current == null) continue;
                int eq = line.IndexOf('='); if (eq < 1) continue;
                string key = line.Substring(0, eq).Trim(), value = line.Substring(eq + 1).Trim();
                if (key.Equals("ModName", StringComparison.OrdinalIgnoreCase) && value.Length > 0) current.Name = value;
                else if (key.Equals("installtype", StringComparison.OrdinalIgnoreCase)) current.InstallType = value;
                else if (key.Equals("ModID_v1", StringComparison.OrdinalIgnoreCase)) current.ModId = value;
                else if (key.Equals("file", StringComparison.OrdinalIgnoreCase) && value.Length > 0) current.Files.Add(value.Replace('\\', '/'));
            }
            return mods;
        }
    }

    // What is installed in the game folder: REFramework, SlotsLoader and the crash reporter state.
    internal sealed class ToolStatus
    {
        internal bool GameFound, ReFramework, SlotsLoader, SlotsLoaderData;
        internal string ReFrameworkDetail = "", SlotsLoaderDetail = "", CrashReporterState = "";
        // 1.0.8 (ReFramework.cs): dinput8.dll present; its build (null when unreadable); older than the supported nightly
        // (never for an upscaler build). ReFrameworkOffer: missing or old, so the one-click install applies.
        // 1.0.10: ReFrameworkFluffy = the external mod manager installed dinput8.dll, so REFramework is updated there (no offer).
        internal bool ReFrameworkFile, ReFrameworkOutdated, ReFrameworkFluffy;
        internal ReFrameworkBuild ReFrameworkBuild;
        internal bool ReFrameworkOffer { get { return (!ReFrameworkFile || ReFrameworkOutdated) && !ReFrameworkFluffy; } }

        internal static ToolStatus Detect(string gameRoot) { return Detect(gameRoot, null); }
        internal static ToolStatus Detect(string gameRoot, FluffyInstalls fluffy)
        {
            var s = new ToolStatus { ReFrameworkFluffy = ReplaceEngine.ReFrameworkByFluffy(fluffy) };
            try
            {
                s.GameFound = Directory.Exists(gameRoot) && File.Exists(Path.Combine(gameRoot, "StreetFighter6.exe"));
                bool dinput = File.Exists(Path.Combine(gameRoot, "dinput8.dll")), refDir = Directory.Exists(Path.Combine(gameRoot, "reframework"));
                s.ReFramework = dinput && refDir; s.ReFrameworkFile = dinput;
                s.ReFrameworkBuild = dinput ? ReFrameworkBuild.Read(Path.Combine(gameRoot, "dinput8.dll")) : null;
                ReFrameworkPin pin = ReFrameworkRelease.Pin;
                s.ReFrameworkOutdated = s.ReFrameworkBuild != null && s.ReFrameworkBuild.OlderThan(pin);
                string built = s.ReFrameworkBuild == null || !s.ReFrameworkBuild.BuildDate.HasValue ? "" : " · built " + s.ReFrameworkBuild.DateText + (s.ReFrameworkBuild.Upscaler ? " (upscaler build)" : "");
                s.ReFrameworkDetail = !dinput ? "REFramework not installed: Slotsloader needs it, so extra outfits won't load"
                    : !refDir ? "REFramework incomplete: no reframework folder yet (start SF6 once)"
                    : s.ReFrameworkOutdated ? "REFramework out of date" + built + "; Slotsloader is tested with " + pin.Label + " (" + pin.BuildDate.ToString("d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture) + ")"
                    : "REFramework installed" + built;
                if (s.ReFrameworkFluffy && (!dinput || s.ReFrameworkOutdated)) s.ReFrameworkDetail += " · installed by the external mod manager: update it there";
                string plugin = Path.Combine(gameRoot, "reframework", "plugins", "SF6_CostumeSlotsNative.dll");
                string ags = Path.Combine(gameRoot, "amd_ags_x64.dll"), real = Path.Combine(gameRoot, "amd_ags_x64_real.dll");
                bool proxy = File.Exists(ags) && File.Exists(real) && new FileInfo(ags).Length > 200 * 1024;
                s.SlotsLoaderData = Directory.Exists(Path.Combine(gameRoot, "reframework", "data", "SF6_Costumes_Data", "loader"));
                s.SlotsLoader = proxy && File.Exists(plugin) && s.SlotsLoaderData;
                s.SlotsLoaderDetail = s.SlotsLoader ? "Slotsloader installed" :
                    proxy || File.Exists(plugin) ? "Slotsloader incomplete (missing " + String.Join(", ", new[] { proxy ? null : "amd_ags_x64 proxy", File.Exists(plugin) ? null : "SF6_CostumeSlotsNative.dll", s.SlotsLoaderData ? null : "data folder" }.Where(x => x != null)) + ")" :
                    "Slotsloader not installed: extra outfits won't appear in game";
                bool crash = File.Exists(Path.Combine(gameRoot, "CrashReport.exe")), renamed = File.Exists(Path.Combine(gameRoot, SlotsInstaller.CrashReporter.RenamedName));
                s.CrashReporterState = crash ? "Crash reporter active (CrashReport.exe)" : renamed ? "Crash reporter renamed to " + SlotsInstaller.CrashReporter.RenamedName + " (as external mod managers do)" : "Crash reporter not found";
            }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; }
            return s;
        }

        // 1.0.10: what Detect reads (sizes, write times, folders), cheap enough for every poll, so a change made outside the
        // Manager (REFramework copied in by hand or by the external mod manager) refreshes the notice without a restart.
        internal static string Stamp(string gameRoot)
        {
            var parts = new List<string>();
            foreach (string rel in new[] { "StreetFighter6.exe", "dinput8.dll", "amd_ags_x64.dll", "amd_ags_x64_real.dll", @"reframework\plugins\SF6_CostumeSlotsNative.dll" })
            {
                try { var info = new FileInfo(Path.Combine(gameRoot ?? "", rel)); parts.Add(info.Exists ? info.Length + "@" + info.LastWriteTimeUtc.Ticks : "-"); }
                catch (Exception ex) { if (ex is OutOfMemoryException) throw; parts.Add("?"); }
            }
            foreach (string dir in new[] { "reframework", @"reframework\data\SF6_Costumes_Data\loader" })
            {
                try { parts.Add(Directory.Exists(Path.Combine(gameRoot ?? "", dir)) ? "d" : "-"); }
                catch (Exception ex) { if (ex is OutOfMemoryException) throw; parts.Add("?"); }
            }
            return String.Join("|", parts);
        }
    }

    internal static class CrashReporter
    {
        internal const string Name = "CrashReport.exe", RenamedName = "CrashReport_FMMBACKUP.exe";
    }

    // REFramework's loose file loader: LooseFileLoader_Enabled in re2_fw_config.txt.
    internal enum LooseLoaderState { ReFrameworkMissing, Disabled, Enabled, Unreadable }

    internal static class LooseLoader
    {
        internal const string ConfigName = "re2_fw_config.txt", EnabledKey = "LooseFileLoader_Enabled",
            LogAccessedKey = "LooseFileLoader_LogAccessedFiles", LogLooseKey = "LooseFileLoader_LogLooseFiles";
        internal const string AccessedLog = "reframework_accessed_files.txt", LooseLog = "reframework_loose_files.txt", FrameworkLog = "re2_framework_log.txt";

        internal static string ConfigPath(string gameRoot) { return Path.Combine(gameRoot, ConfigName); }

        internal static LooseLoaderState State(string gameRoot, out string detail)
        {
            ToolStatus tools = ToolStatus.Detect(gameRoot);
            if (!File.Exists(Path.Combine(gameRoot, "dinput8.dll"))) { detail = "REFramework not installed (no dinput8.dll)"; return LooseLoaderState.ReFrameworkMissing; }
            string config = ConfigPath(gameRoot);
            try
            {
                if (!File.Exists(config)) { detail = "Loose file loader off (no REFramework config yet)"; return LooseLoaderState.Disabled; }
                string value = Read(gameRoot, EnabledKey);
                bool on = String.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
                detail = on ? "Loose file loader on (" + EnabledKey + "=true)" : "Loose file loader off (" + EnabledKey + "=" + (value ?? "missing") + ")";
                if (!tools.ReFramework) detail += " · reframework folder missing";
                return on ? LooseLoaderState.Enabled : LooseLoaderState.Disabled;
            }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; detail = "REFramework config unreadable: " + ex.Message; return LooseLoaderState.Unreadable; }
        }

        internal static string Read(string gameRoot, string key)
        {
            string config = ConfigPath(gameRoot);
            if (!File.Exists(config)) return null;
            foreach (string line in File.ReadAllLines(config))
            {
                int eq = line.IndexOf('=');
                if (eq > 0 && line.Substring(0, eq).Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) return line.Substring(eq + 1).Trim();
            }
            return null;
        }

        // Changes only `key`, preserving every other line, order, encoding and line endings;
        // adds the key when absent. The previous file is copied to backupDirectory first.
        // Returns the previous value (null when the key or file was absent).
        internal static string Write(string gameRoot, string key, string value, string backupDirectory)
        {
            GameProcesses.RequireGameClosed("change REFramework's config (it rewrites the file on exit)");
            string config = ConfigPath(gameRoot);
            Safety.NoReparseChain(config);
            byte[] original = File.Exists(config) ? File.ReadAllBytes(config) : new byte[0];
            bool bom = original.Length >= 3 && original[0] == 0xEF && original[1] == 0xBB && original[2] == 0xBF;
            string text = new UTF8Encoding(false).GetString(original, bom ? 3 : 0, original.Length - (bom ? 3 : 0));
            string newline = text.Contains("\r\n") ? "\r\n" : text.Contains("\n") ? "\n" : "\r\n";
            string[] lines = text.Replace("\r\n", "\n").Split('\n');
            bool trailing = text.EndsWith("\n");
            var list = lines.ToList(); if (trailing && list.Count > 0 && list[list.Count - 1].Length == 0) list.RemoveAt(list.Count - 1);
            string previous = null; bool found = false;
            for (int i = 0; i < list.Count; i++)
            {
                int eq = list[i].IndexOf('=');
                if (eq > 0 && list[i].Substring(0, eq).Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                {
                    previous = list[i].Substring(eq + 1).Trim();
                    list[i] = list[i].Substring(0, eq + 1) + value; found = true;
                }
            }
            if (!found) list.Add(key + "=" + value);
            if (found && previous == value) return previous;
            if (original.Length > 0)
            {
                Directory.CreateDirectory(backupDirectory);
                string backup = Path.Combine(backupDirectory, "re2_fw_config-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".txt");
                File.WriteAllBytes(backup, original);
            }
            string result = String.Join(newline, list) + (trailing || original.Length == 0 ? newline : "");
            byte[] body = new UTF8Encoding(false).GetBytes(result);
            if (bom) body = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(body).ToArray();
            DurableFile.Write(config, body);
            return previous;
        }

        // Loose files REFramework logged as opened (best effort: any natives path in its logs).
        internal static List<string> LoggedPaths(string gameRoot, DateTime sinceUtc)
        {
            var result = new List<string>();
            foreach (string name in new[] { AccessedLog, LooseLog, FrameworkLog })
            {
                string file = Path.Combine(gameRoot, name);
                try
                {
                    if (!File.Exists(file) || File.GetLastWriteTimeUtc(file) < sinceUtc) continue;
                    using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var reader = new StreamReader(stream))
                    {
                        string line;
                        while ((line = reader.ReadLine()) != null) result.AddRange(ParseLogLine(line));
                    }
                }
                catch (Exception ex) { if (ex is OutOfMemoryException) throw; }
            }
            return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        internal static IEnumerable<string> ParseLogLine(string line)
        {
            foreach (Match m in Regex.Matches(line ?? "", @"(?:[A-Za-z]:)?[\\/]?(?:[^\s""'<>|:]*[\\/])?(natives[\\/][^\s""'<>|]+)", RegexOptions.IgnoreCase))
                yield return ModFixes.NormalizeNativePath(m.Groups[1].Value);
        }

        // Which of the given loose paths appear in the logged set.
        internal static List<string> Matched(IEnumerable<string> modPaths, IEnumerable<string> logged)
        {
            var set = new HashSet<string>(logged.Select(ModFixes.NormalizeNativePath), StringComparer.OrdinalIgnoreCase);
            return modPaths.Where(p => set.Contains(ModFixes.NormalizeNativePath(p))).ToList();
        }
    }

    internal static class DurableFile
    {
        internal const int ReplaceRetries = 6;
        // Writes through a temporary file flushed to disk, then replaces the target.
        internal static void Write(string path, byte[] bytes)
        {
            string tmp = path + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
            bool complete = false;
            try
            {
                using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                { fs.Write(bytes, 0, bytes.Length); fs.Flush(true); }
                complete = true;
                Publish(tmp, path);
            }
            finally { Discard(tmp, path, complete); }
        }
        // Puts a fully written temporary file in place of `path`. 2.4.2: another program reading the target without
        // Delete sharing (an older Manager, a scanner) makes the replace fail for a moment; the temporary file is intact
        // then, so it is tried again briefly. Json.Write uses it too (1.0.11).
        internal static void Publish(string tmp, string path)
        {
            for (int attempt = 0; ; attempt++)
            {
                try { if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path); return; }
                catch (IOException) { if (attempt >= ReplaceRetries || !File.Exists(tmp)) throw; }
                catch (UnauthorizedAccessException) { if (attempt >= ReplaceRetries || !File.Exists(tmp)) throw; }
                System.Threading.Thread.Sleep(15 + 15 * attempt);
            }
        }
        // After a write (failed or not): a temporary file left over is deleted, except when it was written in full and the
        // target is gone. A replace can fail after removing the target (ERROR_UNABLE_TO_MOVE_REPLACEMENT); the new
        // content then takes the target's place instead of being lost with it (1.0.11, PR #12 review).
        internal static void Discard(string tmp, string path, bool complete)
        {
            try
            {
                if (!File.Exists(tmp)) return;
                if (complete && !File.Exists(path)) File.Move(tmp, path); else File.Delete(tmp);
            }
            catch { }
        }
        internal static void WriteJson(string path, object value)
        {
            Write(path, new UTF8Encoding(false).GetBytes(Json.Serializer().Serialize(value)));
        }
    }
}
