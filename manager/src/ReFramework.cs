using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace SlotsInstaller
{
    // 1.0.8 (the user): Slotsloader is a REFramework plugin, so extra outfits need REFramework as much as Slotsloader.
    // The Manager flags it when it is missing, when SF6 never ran with it (no reframework folder), or when its build is
    // older than the supported nightly below, and installs that nightly in one click (verified like the Slotsloader pin).
    // External mod managers download praydog/REFramework-nightly's latest REFramework.zip (one dinput8.dll for every
    // game and reframework_revision.txt); this pins one such release. Builds of the upscaler branch (the user's) are
    // recognised and never called old. Bump: a newer nightly tested with Slotsloader in game, its tag, size, SHA-256
    // (the release asset digest), commit and the build date printed in its dinput8.dll.
    internal sealed class ReFrameworkPin
    {
        internal string Tag, Nightly, Asset, Sha256, Commit, Url, ReleasePage;
        internal long Size;
        internal DateTime BuildDate;
        internal string Label { get { return "REFramework nightly " + Nightly; } }
        internal string CacheName { get { return "REFramework-nightly-" + Nightly + ".zip"; } }
    }

    internal static class ReFrameworkRelease
    {
        internal const string Repo = "praydog/REFramework-nightly";
        internal static readonly ReFrameworkPin Supported = Make("nightly-01432-d052e8fd3fd41d0a1a33394fcf6e6c19be39b1ee", "01432", 13272178,
            "27376E1DA177DDCDE9C46B3541BDC6FA1AD54A11D0CAB9F7782EFF0481455E94", "d052e8fd3fd41d0a1a33394fcf6e6c19be39b1ee", new DateTime(2026, 10, 11));
        internal static ReFrameworkPin Pin = Supported;   // tests swap in a fixture pin
        internal static ReFrameworkPin Make(string tag, string nightly, long size, string sha256, string commit, DateTime built)
        {
            return new ReFrameworkPin
            {
                Tag = tag, Nightly = nightly, Asset = "REFramework.zip", Size = size, Sha256 = sha256.ToUpperInvariant(), Commit = commit, BuildDate = built,
                Url = "https://github.com/" + Repo + "/releases/download/" + tag + "/REFramework.zip", ReleasePage = "https://github.com/" + Repo + "/releases/tag/" + tag
            };
        }

        // The verified copy in Data\downloads, else an HTTPS download of the pinned asset (SafeHttp: size and SHA-256
        // checked, nothing kept on failure).
        internal static LoaderFile Fetch(ReFrameworkPin pin, string downloadDir)
        {
            string cached = Path.Combine(downloadDir, pin.CacheName);
            if (File.Exists(cached))
            {
                long length = new FileInfo(cached).Length;
                string hash = length == pin.Size ? SafeHttp.HashFile(cached) : null;
                if (hash == pin.Sha256) return new LoaderFile { Path = Path.GetFullPath(cached), Sha256 = hash, Size = length, Source = LoaderSource.Cache };
                File.Delete(cached);
            }
            try
            {
                string path = SafeHttp.Download(pin.Url, downloadDir, pin.CacheName, pin.Size, pin.Sha256, "Downloading " + pin.Label, "pinned");
                return new LoaderFile { Path = path, Sha256 = pin.Sha256, Size = pin.Size, Source = LoaderSource.Download };
            }
            catch (DownloadFailure f) { throw new IOException("Couldn't download " + pin.Label + ": " + f.Reason + ".\n\nDownload REFramework.zip from " + pin.ReleasePage + " and drop it on the window.", f); }
        }
    }

    // What an installed dinput8.dll says about itself. REFramework has no version resource; its build date
    // ("31.08.2026"), commit hash and, for the upscaler branch, the word "Upscaler" are plain ASCII in the file.
    internal sealed class ReFrameworkBuild
    {
        internal DateTime? BuildDate;
        internal string Commit = "";
        internal bool Upscaler;
        internal bool OlderThan(ReFrameworkPin pin) { return !Upscaler && BuildDate.HasValue && BuildDate.Value.Date < pin.BuildDate.Date; }
        internal string DateText { get { return BuildDate.HasValue ? BuildDate.Value.ToString("d MMM yyyy", CultureInfo.InvariantCulture) : ""; } }

        // A constant 40-hex string every build carries (not the build's commit).
        private const string SharedHash = "961c151d2e87f2686a955a9be24d316f1362bf21";
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, KeyValuePair<string, ReFrameworkBuild>> Cache = new Dictionary<string, KeyValuePair<string, ReFrameworkBuild>>(StringComparer.OrdinalIgnoreCase);

        // Cached by size and write time (the file is 20+ MB); null when it can't be read.
        internal static ReFrameworkBuild Read(string dll)
        {
            try
            {
                var info = new FileInfo(dll);
                if (!info.Exists) return null;
                string stamp = info.Length + ":" + info.LastWriteTimeUtc.Ticks;
                lock (Gate) { KeyValuePair<string, ReFrameworkBuild> hit; if (Cache.TryGetValue(info.FullName, out hit) && hit.Key == stamp) return hit.Value; }
                ReFrameworkBuild build = Parse(File.ReadAllBytes(info.FullName));
                lock (Gate) Cache[info.FullName] = new KeyValuePair<string, ReFrameworkBuild>(stamp, build);
                return build;
            }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); return null; }
        }

        internal static ReFrameworkBuild Parse(byte[] b)
        {
            var build = new ReFrameworkBuild { Upscaler = IndexOf(b, Encoding.ASCII.GetBytes("Upscaler")) >= 0 };
            for (int i = 0; i + 10 <= b.Length; i++)
            {
                // dd.MM.yyyy standing alone (not inside a longer run of digits or dots).
                if (b[i + 2] != '.' || b[i + 5] != '.' || b[i + 6] != '2' || b[i + 7] != '0') continue;
                if (!Digit(b[i]) || !Digit(b[i + 1]) || !Digit(b[i + 3]) || !Digit(b[i + 4]) || !Digit(b[i + 8]) || !Digit(b[i + 9])) continue;
                if ((i > 0 && (Digit(b[i - 1]) || b[i - 1] == '.')) || (i + 10 < b.Length && (Digit(b[i + 10]) || b[i + 10] == '.'))) continue;
                DateTime date;
                if (DateTime.TryParseExact(Encoding.ASCII.GetString(b, i, 10), "dd.MM.yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out date)) { build.BuildDate = date; break; }
            }
            for (int i = 0; i + 40 <= b.Length; i++)
            {
                if (!Hex(b[i]) || (i > 0 && Hex(b[i - 1]))) continue;
                int n = 0; while (i + n < b.Length && Hex(b[i + n])) n++;
                if (n == 40) { string hash = Encoding.ASCII.GetString(b, i, 40); if (hash != SharedHash) { build.Commit = hash; break; } }
                i += n;
            }
            return build;
        }
        private static bool Digit(byte c) { return c >= '0' && c <= '9'; }
        private static bool Hex(byte c) { return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'); }
        private static int IndexOf(byte[] data, byte[] word)
        {
            for (int i = 0; i + word.Length <= data.Length; i++)
            {
                if (data[i] != word[0]) continue;
                int j = 1; while (j < word.Length && data[i + j] == word[j]) j++;
                if (j == word.Length) return i;
            }
            return -1;
        }
    }
}
