using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;

namespace SlotsInstaller
{
    // 2.3.1: the one SlotsLoader build the "install Slotsloader" offer and `loader-fetch` install. 1.0.19: the approved
    // `slots_loader` entry of the repository's dependencies.json (DependencyList below), embedded by build.ps1 from the
    // repo root: the same file the update check reads online, so there is one source of truth. The files stay on the
    // upstream releases (LoaderPin.Source); a download is allowed only from there (DownloadRefusal).
    internal sealed class LoaderPin
    {
        internal string Tag, Version, Asset, Sha256, Url, ReleasePage, Origin, Name = "";
        internal long Size;

        // Repo: this Manager's repository (manager-v* releases and the approved list). Source: where the approved Slots
        // Loader zips are published.
        internal const string Repo = "ZZtaii/slotsloader-manager", Source = "Wael3rd/SF6-ModSuite", ResourceName = "SlotsInstaller.Dependencies.json";
        internal const string DownloadPrefix = "https://github.com/" + Source + "/releases/download/";
        internal static readonly LoaderPin Release = FromList(ReadResource());

        // dependencies.json -> its slots_loader entry (InvalidDataException with the reason when it can't be used).
        internal static LoaderPin FromList(string json) { return DependencyList.Parse(json).Loader; }
        private static string ReadResource()
        {
            using (Stream s = typeof(LoaderPin).Assembly.GetManifestResourceStream(ResourceName))
            {
                if (s == null) throw new InvalidDataException("The build has no " + ResourceName + " (build.ps1 embeds the repository's dependencies.json).");
                using (var r = new StreamReader(s)) return r.ReadToEnd();
            }
        }

        // 1.0.19: null when a loader may be downloaded from url, else why not. HTTPS addresses must be a release download
        // of Source on github.com (SafeHttp then follows GitHub's HTTPS redirects); anything that is not HTTPS is left
        // to SafeHttp, which refuses it (a test's loopback http aside).
        internal static string DownloadRefusal(string url)
        {
            Uri u;
            if (!Uri.TryCreate(url ?? "", UriKind.Absolute, out u)) return "the download address is not valid";
            if (u.Scheme != Uri.UriSchemeHttps) return null;
            string[] parts = u.AbsolutePath.Split('/');
            bool ok = u.IsDefaultPort && u.UserInfo.Length == 0 && u.Query.Length == 0 && u.Fragment.Length == 0 && u.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                && u.AbsolutePath.StartsWith("/" + Source + "/releases/download/", StringComparison.OrdinalIgnoreCase) && parts.Length == 7 && parts.Skip(1).All(p => p.Length > 0 && p != "." && p != "..");
            return ok ? null : "only release downloads of " + Source + " on github.com are allowed, not " + u.Host + u.AbsolutePath;
        }
    }

    // 1.0.19: the repository's dependencies.json, written by the maintainer's approval (a merged pull request):
    // {"format":1,"source":"Wael3rd/SF6-ModSuite","packages":{"slots_loader":{name,version,release,file,size,sha256},...}}.
    // Only slots_loader is used; every other package (native_core) and every unknown key is ignored. A missing or
    // malformed slots_loader entry, another format or another source is refused with the reason (never a crash).
    internal sealed class DependencyList
    {
        internal int Format;
        internal LoaderPin Loader;
        internal List<string> IgnoredPackages = new List<string>();
        internal static string TestDownloadBase;   // tests: download URLs on a loopback server instead of github.com

        private static readonly System.Text.RegularExpressions.Regex TagPattern = new System.Text.RegularExpressions.Regex(@"^[A-Za-z0-9][A-Za-z0-9._+-]{0,99}$");
        private static readonly System.Text.RegularExpressions.Regex FilePattern = new System.Text.RegularExpressions.Regex(@"^[A-Za-z0-9][A-Za-z0-9._+-]{0,199}\.zip$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        internal static DependencyList Parse(string json)
        {
            object root;
            try { root = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = 1024 * 1024, RecursionLimit = 16 }.DeserializeObject(json ?? ""); }
            catch (Exception ex) { if (ex is ArgumentException || ex is InvalidOperationException) throw Bad("it is not valid JSON"); throw; }
            var d = root as Dictionary<string, object>;
            if (d == null) throw Bad("it is not a JSON object");
            object value;
            if (!d.TryGetValue("format", out value) || !(value is int) || (int)value != 1) throw Bad("format " + (value ?? "(missing)") + " is not format 1");
            string source = Text(d, "source", "");
            if (!source.Equals(LoaderPin.Source, StringComparison.OrdinalIgnoreCase)) throw Bad("source '" + source + "' is not " + LoaderPin.Source);
            var packages = d.TryGetValue("packages", out value) ? value as Dictionary<string, object> : null;
            if (packages == null) throw Bad("it has no packages");
            var list = new DependencyList { Format = 1 };
            list.IgnoredPackages.AddRange(packages.Keys.Where(k => k != "slots_loader").OrderBy(k => k, StringComparer.Ordinal));
            var e = packages.TryGetValue("slots_loader", out value) ? value as Dictionary<string, object> : null;
            if (e == null) throw Bad("it has no slots_loader entry");
            string version = Text(e, "version", "slots_loader."), release = Text(e, "release", "slots_loader."), file = Text(e, "file", "slots_loader."), sha = Text(e, "sha256", "slots_loader.").ToUpperInvariant();
            if (version.Length > 40 || UpdateCheck.ParseVersion(version) == null) throw Bad("slots_loader.version '" + version + "' is not a version");
            if (!TagPattern.IsMatch(release) || release.Contains("..")) throw Bad("slots_loader.release '" + release + "' is not a release tag");
            if (!FilePattern.IsMatch(file) || file.Contains("..")) throw Bad("slots_loader.file '" + file + "' is not a .zip file name");
            if (sha.Length != 64 || sha.Any(c => !Uri.IsHexDigit(c))) throw Bad("slots_loader.sha256 is not a SHA-256");
            object sizeValue; long size = 0;
            if (e.TryGetValue("size", out sizeValue) && (sizeValue is int || sizeValue is long)) size = Convert.ToInt64(sizeValue, System.Globalization.CultureInfo.InvariantCulture);
            if (size <= 0 || size > 512L * 1024 * 1024) throw Bad("slots_loader.size is not a size in bytes");
            object nameValue; string name = e.TryGetValue("name", out nameValue) && nameValue is string ? ((string)nameValue).Trim() : "";
            string escaped = Uri.EscapeDataString(release) + "/" + Uri.EscapeDataString(file);
            list.Loader = new LoaderPin
            {
                Name = name.Length > 0 ? name : "SF6 Slots Loader", Tag = release, Version = version, Asset = file, Size = size, Sha256 = sha,
                Url = (TestDownloadBase != null ? TestDownloadBase.TrimEnd('/') + "/" : LoaderPin.DownloadPrefix) + escaped,
                ReleasePage = "https://github.com/" + LoaderPin.Source + "/releases/tag/" + Uri.EscapeDataString(release),
                Origin = "github.com/" + LoaderPin.Source
            };
            return list;
        }
        private static string Text(Dictionary<string, object> d, string key, string prefix)
        {
            object v;
            if (!d.TryGetValue(key, out v) || !(v is string) || ((string)v).Trim().Length == 0) throw Bad(prefix + key + " is missing");
            return ((string)v).Trim();
        }
        private static InvalidDataException Bad(string why) { return new InvalidDataException("dependencies.json: " + why + "."); }
    }

    // 2.5.9: Bundled = the zip shipped in the app folder's SlotsLoader\ subfolder (tools\Package-Release.ps1).
    internal enum LoaderSource { Library, Cache, Download, Bundled }

    internal sealed class LoaderFile
    {
        internal string Path, Sha256;
        internal long Size;
        internal LoaderSource Source;
        internal string SourceLabel { get { return Source == LoaderSource.Bundled ? "copy bundled with the Manager" : Source == LoaderSource.Library ? "library" : Source == LoaderSource.Cache ? "cached download" : "download"; } }
    }

    // One error for every failed fetch: the reason, then where to get the file by hand.
    internal sealed class LoaderFetchException : IOException
    {
        internal readonly string Reason;
        internal LoaderFetchException(LoaderPin pin, string reason)
            : base("Couldn't download Slotsloader " + pin.Version + ": " + reason + ".\n\nDownload it from " + pin.ReleasePage + " and drop the zip on the window.")
        { Reason = reason; }
    }

    internal static class LoaderRelease
    {
        // The pin in use. Tests swap in a fixture pin (and may allow plain http to a loopback address); nothing in
        // the settings or on the command line can change it.
        internal static LoaderPin Pin = LoaderPin.Release;
        // 2.4.0: the HTTP code is shared with the update check (SafeHttp); these forward to it.
        internal static bool AllowLoopbackHttp { get { return SafeHttp.AllowLoopbackHttp; } set { SafeHttp.AllowLoopbackHttp = value; } }
        internal static int TimeoutMs { get { return SafeHttp.TimeoutMs; } set { SafeHttp.TimeoutMs = value; } }
        internal const int MaxRedirects = SafeHttp.MaxRedirects;
        internal static int HashedFiles;   // tests: library files hashed (only those with the pinned size are)

        internal static string DownloadDir { get { return System.IO.Path.Combine(AppConfig.DataDir, "downloads"); } }
        internal static string Description(LoaderPin pin) { return pin.Asset + " (" + Utilities.FriendlyBytes(pin.Size) + ") from " + pin.Origin; }

        // 2.5.9: the app folder's SlotsLoader\ subfolder (the release zip ships the pinned loader there). Tests point it elsewhere.
        internal const string BundleFolder = "Slotsloader";
        internal static string BundledDirOverride;
        internal static string BundledDir { get { return BundledDirOverride ?? System.IO.Path.Combine(MainForm.AppDir, BundleFolder); } }

        // (0) the zip bundled with the Manager when it verifies, (a) a library file with the pinned SHA-256 (only files
        // of the pinned size are hashed), then (b) the copy kept in Data\downloads. A cached copy that no longer
        // verifies is deleted (a bundled one is left alone). Null: nothing local.
        internal static LoaderFile FindLocal(LoaderPin pin, IEnumerable<string> libraries, string downloadDir)
        {
            string bundled = System.IO.Path.Combine(BundledDir, pin.Asset);
            try
            {
                if (File.Exists(bundled) && new FileInfo(bundled).Length == pin.Size)
                {
                    string hash = HashFile(bundled);
                    if (hash == pin.Sha256.ToUpperInvariant()) return new LoaderFile { Path = System.IO.Path.GetFullPath(bundled), Sha256 = hash, Size = pin.Size, Source = LoaderSource.Bundled };
                }
            }
            catch (Exception ex) { if (!(ex is IOException || ex is UnauthorizedAccessException)) throw; }
            foreach (string folder in (libraries ?? new string[0]).Where(l => !String.IsNullOrWhiteSpace(l)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                IEnumerable<string> files;
                try { if (!Directory.Exists(folder)) continue; files = Directory.EnumerateFiles(folder).ToList(); }
                catch (Exception ex) { if (ex is IOException || ex is UnauthorizedAccessException) continue; throw; }
                foreach (string file in files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    OperationContext.ThrowIfCancellationRequested();
                    try
                    {
                        var info = new FileInfo(file);
                        if ((info.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Hidden)) != 0 || info.Length != pin.Size) continue;
                        Interlocked.Increment(ref HashedFiles);
                        string hash = HashFile(file);
                        if (hash == pin.Sha256.ToUpperInvariant()) return new LoaderFile { Path = info.FullName, Sha256 = hash, Size = info.Length, Source = LoaderSource.Library };
                    }
                    catch (Exception ex) { if (ex is IOException || ex is UnauthorizedAccessException) continue; throw; }
                }
            }
            string cached = System.IO.Path.Combine(downloadDir, pin.Asset);
            if (File.Exists(cached))
            {
                long length = new FileInfo(cached).Length;
                string hash = length == pin.Size ? HashFile(cached) : null;
                if (hash == pin.Sha256.ToUpperInvariant()) return new LoaderFile { Path = System.IO.Path.GetFullPath(cached), Sha256 = hash, Size = length, Source = LoaderSource.Cache };
                File.Delete(cached);
            }
            return null;
        }

        internal static string HashFile(string path) { return SafeHttp.HashFile(path); }

        // (c) HTTPS download into Data\downloads\<asset>.part through SafeHttp (system proxy, HTTPS-only redirects,
        // time-out, cancellable, stopped as soon as it is longer than the pin), then size + SHA-256 verified and
        // renamed to <asset>. Any failure deletes the .part file and throws LoaderFetchException.
        internal static LoaderFile Download(LoaderPin pin, string downloadDir)
        {
            string refused = LoaderPin.DownloadRefusal(pin.Url);   // 1.0.19: only from the approved source's releases
            if (refused != null) { Program.LogLine("Slotsloader download refused: " + refused + "."); throw new LoaderFetchException(pin, refused); }
            try
            {
                string path = SafeHttp.Download(pin.Url, downloadDir, pin.Asset, pin.Size, pin.Sha256, "Downloading Slotsloader " + pin.Version, "pinned");
                return new LoaderFile { Path = path, Sha256 = pin.Sha256.ToUpperInvariant(), Size = pin.Size, Source = LoaderSource.Download };
            }
            catch (DownloadFailure f) { throw new LoaderFetchException(pin, f.Reason); }
        }

        // The offer and loader-fetch: local first, then (when allowed) the download.
        internal static LoaderFile Fetch(LoaderPin pin, IEnumerable<string> libraries, string downloadDir, bool download)
        {
            LoaderFile local = FindLocal(pin, libraries, downloadDir);
            if (local != null || !download) return local;
            return Download(pin, downloadDir);
        }
    }
}
