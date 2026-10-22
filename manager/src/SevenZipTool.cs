using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
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

        // 1.0.11 (the user: "SLM should be smart enough to deal with this and not annoy users"): the release zip can't
        // carry 7-Zip, because the updater of 1.0.10 and older refuses any file besides the app's own. So the Manager
        // fetches it: when RAR/7Z need a 7-Zip and none is usable (none installed, or only one too old for RAR5), the
        // first read downloads this project's pinned 7-Zip release (size and SHA-256 checked, like the REFramework and
        // Slotsloader pins) and unpacks its three files into "7-Zip\" beside the exe. A failed download is not tried
        // again for five minutes; its reason is in FetchFailure.
        internal sealed class Release { internal string Version, Asset, Sha256, Url; internal long Size; }
        internal static readonly Release Supported = new Release { Version = "26.00", Asset = "7-Zip-26.00-x64.zip", Size = 1921731,
            Sha256 = "D5DCEF65301960D16BC0E7848523A2E7830AC06AAC57D1E599C960CCBF519C7E",
            Url = "https://github.com/ZZtaii/slotsloader-manager/releases/download/tool-7zip-26.00/7-Zip-26.00-x64.zip" };
        internal static Release Pin = Supported;   // tests swap in a loopback fixture
        internal static string DownloadDirOverride;   // tests
        private static readonly Version Rar5 = new Version(15, 6);
        private static readonly object FetchLock = new object();
        private static DateTime fetchFailedUtc = DateTime.MinValue;
        internal static string FetchFailure;
        internal static int Fetches;   // tests: downloads attempted
        internal static void ResetFetch() { lock (FetchLock) { fetchFailedUtc = DateTime.MinValue; FetchFailure = null; Fetches = 0; } }   // tests

        // Whether RAR/7Z need the fetched copy: nothing usable, or only an installed 7-Zip too old for RAR5. A path the
        // user chose in Settings is never second-guessed.
        internal static bool NeedsFetch(string configured)
        {
            string source, older, exe = Resolve(configured, out source, out older);
            if (source == "none") return true;
            if (source != "installed") return false;
            Version version = VersionOf(exe);
            return version != null && version < Rar5;
        }

        // The 7z.exe to run a RAR/7Z with, fetching the pinned copy first when it is needed ("" when that failed too).
        internal static string ResolveOrFetch(string configured)
        {
            if (!NeedsFetch(configured)) return Resolve(configured);
            lock (FetchLock)
            {
                ClearCache();
                if (!NeedsFetch(configured)) return Resolve(configured);   // another reader fetched it meanwhile
                if (DateTime.UtcNow - fetchFailedUtc < TimeSpan.FromMinutes(5)) return Resolve(configured);
                try { Fetches++; Fetch(Pin, DownloadDirOverride ?? Path.Combine(AppConfig.DataDir, "downloads")); FetchFailure = null; }
                catch (Exception ex)
                {
                    if (!(ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException)) throw;
                    FetchFailure = ex.Message; fetchFailedUtc = DateTime.UtcNow; Program.Log(ex);
                }
                ClearCache();
                return Resolve(configured);
            }
        }

        // The verified zip (Data\downloads, else an HTTPS download), unpacked beside "7-Zip\" and then moved into place;
        // an unusable "7-Zip\" folder already there is replaced.
        private static void Fetch(Release pin, string downloadDir)
        {
            string zip = Path.Combine(downloadDir, pin.Asset);
            if (!(File.Exists(zip) && new FileInfo(zip).Length == pin.Size && SafeHttp.HashFile(zip) == pin.Sha256))
            {
                if (File.Exists(zip)) File.Delete(zip);
                try { zip = SafeHttp.Download(pin.Url, downloadDir, pin.Asset, pin.Size, pin.Sha256, "Getting 7-Zip " + pin.Version + " for RAR and 7Z mods", "pinned"); }
                catch (DownloadFailure f) { throw new IOException("Couldn't download 7-Zip " + pin.Version + ": " + f.Reason + ".", f); }
            }
            string folder = Path.GetDirectoryName(BundledExe), parent = Path.GetDirectoryName(folder);
            string tag = Guid.NewGuid().ToString("N").Substring(0, 8), fresh = Path.Combine(parent, BundleFolder + ".download-" + tag), old = Path.Combine(parent, BundleFolder + ".old-" + tag);
            try
            {
                Directory.CreateDirectory(fresh);
                using (ZipArchive archive = ZipFile.OpenRead(zip))
                    foreach (string name in BundleFiles)
                    {
                        ZipArchiveEntry entry = archive.Entries.FirstOrDefault(e => String.Equals(e.FullName, name, StringComparison.OrdinalIgnoreCase));
                        if (entry == null) throw new InvalidDataException("The 7-Zip download has no " + name + ".");
                        entry.ExtractToFile(Path.Combine(fresh, name), true);
                    }
                if (Directory.Exists(folder)) Directory.Move(folder, old);
                Directory.Move(fresh, folder);
            }
            finally
            {
                try { if (!Directory.Exists(folder) && Directory.Exists(old)) Directory.Move(old, folder); } catch (IOException) { } catch (UnauthorizedAccessException) { }   // the new one didn't land
                try { if (Directory.Exists(fresh)) Directory.Delete(fresh, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                try { if (Directory.Exists(old)) Directory.Delete(old, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        // One line for Settings: which 7-Zip RAR/7Z use.
        internal static string Describe(string configured)
        {
            string source, older, exe = Resolve(configured, out source, out older);
            switch (source)
            {
                case "settings": return "Found";
                case "installed": return "Using your installed 7-Zip · " + exe;
                case "bundled": return "Using the Manager's own 7-Zip" + (older != null ? " · newer than your installed 7-Zip " + Label(VersionOf(older)) : "");
                default: return FetchFailure != null ? "Not found · " + FetchFailure.TrimEnd('.') : "Not found · the Manager gets it the first time a RAR or 7Z needs it";
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
