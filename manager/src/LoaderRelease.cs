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
    // 2.3.1: the one SlotsLoader build the "install Slotsloader" offer and `loader-fetch` install, pinned to a GitHub
    // release asset of our fork. 2.5.9: the values come from assets\loader-pin.json, embedded at build time;
    // tools\Package-Release.ps1 rewrites it with the newest published loader-v* release (and bundles that zip).
    internal sealed class LoaderPin
    {
        internal string Tag, Version, Asset, Sha256, Url, ReleasePage, Origin;
        internal long Size;

        internal const string Repo = "ZZtaii/slotsloader-manager", ResourceName = "SlotsInstaller.LoaderPin.json";
        internal static readonly LoaderPin Release = FromJson(ReadResource());

        // {"tag","version","asset","size","sha256"}; the download URL, release page and origin follow the tag and repo.
        internal static LoaderPin FromJson(string json)
        {
            var d = new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
            Func<string, string> text = delegate(string key) { object v; if (!d.TryGetValue(key, out v) || !(v is string) || ((string)v).Trim().Length == 0) throw new InvalidDataException("loader-pin.json: '" + key + "' is missing."); return ((string)v).Trim(); };
            string tag = text("tag"), asset = text("asset"), sha = text("sha256").ToUpperInvariant();
            object sizeValue; long size;
            if (!d.TryGetValue("size", out sizeValue) || !Int64.TryParse(Convert.ToString(sizeValue, System.Globalization.CultureInfo.InvariantCulture), out size) || size <= 0) throw new InvalidDataException("loader-pin.json: 'size' is missing.");
            if (sha.Length != 64 || sha.Any(c => !Uri.IsHexDigit(c))) throw new InvalidDataException("loader-pin.json: 'sha256' is not a SHA-256.");
            if (!tag.StartsWith("loader-v", StringComparison.Ordinal) || asset.IndexOfAny(new[] { '/', '\\' }) >= 0 || !asset.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("loader-pin.json: bad tag or asset name.");
            return new LoaderPin
            {
                Tag = tag, Version = text("version"), Asset = asset, Size = size, Sha256 = sha,
                Url = "https://github.com/" + Repo + "/releases/download/" + tag + "/" + Uri.EscapeDataString(asset),
                ReleasePage = "https://github.com/" + Repo + "/releases/tag/" + tag,
                Origin = "github.com/" + Repo
            };
        }
        private static string ReadResource()
        {
            using (Stream s = typeof(LoaderPin).Assembly.GetManifestResourceStream(ResourceName))
            {
                if (s == null) throw new InvalidDataException("The build has no " + ResourceName + " (build.ps1 embeds assets\\loader-pin.json).");
                using (var r = new StreamReader(s)) return r.ReadToEnd();
            }
        }
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
