using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace SlotsInstaller
{
    // 2.4.0 update check. Manager releases: the GitHub Releases API of this repository (unauthenticated), tags
    // manager-v* (the private builds' installer-v* tags and the old loader-v* tags are ignored). Drafts are skipped,
    // pre-releases only with the setting; the highest version whose single .zip (*-portable.zip preferred) can be
    // verified (the API's sha256 digest + size, else a SHA256SUMS.txt asset of the release) wins. Unverifiable releases
    // are refused and logged. 1.0.19: the Slots Loader is no longer a release here: the offer is the approved entry of
    // the repository's dependencies.json (raw.githubusercontent.com, ReadList), whose file is on the upstream releases.
    // Nothing here installs anything.
    internal static class UpdateProducts { internal const string Loader = "loader", Manager = "manager"; }

    public sealed class ReleaseOffer
    {
        public string Product = "", Tag = "", Version = "", Name = "", HtmlUrl = "", Asset = "", Url = "", Sha256 = "", DigestSource = "";
        public long Size;
        public bool Prerelease;
        internal Version Parsed { get { return UpdateCheck.ParseVersion(Version); } }
        internal LoaderPin AsPin() { return new LoaderPin { Tag = Tag, Name = Name, Version = Version, Asset = Asset, Size = Size, Sha256 = Sha256, Url = Url, ReleasePage = HtmlUrl, Origin = "github.com/" + LoaderPin.Source }; }
        // 1.0.19: the approved Slots Loader as an offer (DigestSource names the list, so an offer an older Manager saved
        // from a loader-v* release is never taken for an approval).
        internal static ReleaseOffer Approved(LoaderPin pin)
        {
            return new ReleaseOffer { Product = UpdateProducts.Loader, Tag = pin.Tag, Version = pin.Version, Name = pin.Name, HtmlUrl = pin.ReleasePage, Asset = pin.Asset, Url = pin.Url, Size = pin.Size, Sha256 = pin.Sha256, DigestSource = UpdateCheck.ListMarker };
        }
        internal bool IsApproval { get { return Product == UpdateProducts.Loader && DigestSource == UpdateCheck.ListMarker && Parsed != null && LoaderPin.DownloadRefusal(Url) == null; } }
    }

    public sealed class UpdateCheckResult
    {
        public bool Ok;
        public string Reason = "", CheckedUtc = "";
        public int Status;
        public ReleaseOffer Loader, Manager;
        public List<string> Refused = new List<string>();
        public string ListReason = "";   // 1.0.19: why the approved list could not be used (Loader is then null)
    }

    // Data\update-settings.json: the two settings, the last check and what the user chose per product.
    public sealed class UpdateSettings
    {
        public string Format = "sf6-update-settings-v1";
        public bool AutoCheck = true, IncludePrereleases;
        public string LastCheckUtc = "";
        public UpdateCheckResult Last;
        public string SkipLoader = "", SkipManager = "", DismissedLoader = "", DismissedManager = "";
        // 1.0.19: the last approved Slots Loader read from the list (at start or with a check), when, and by which Manager
        // version. A list read by another version (before a Manager update) is not used: the next read replaces it.
        public ReleaseOffer Approved;
        public string ApprovedUtc = "", ApprovedBy = "";
        // Every approved zip's SHA-256 this Manager has read (UpdateCheck.FromApproval: a loader installed from one of
        // them may be offered going back; one the user installed themselves never is).
        public List<string> ApprovedSeen = new List<string>();
        internal ReleaseOffer ApprovedLoader() { return Approved != null && Approved.IsApproval && ApprovedBy == SafeHttp.AppVersion() ? Approved : null; }
        // True when the approved version changed (a ✕ on the old offer then no longer hides the new one).
        internal bool RememberApproved(ReleaseOffer approved, string utc)
        {
            if (approved == null) return false;
            ReleaseOffer before = ApprovedLoader();
            bool changed = before == null || before.Version != approved.Version || before.Sha256 != approved.Sha256;
            Approved = approved; ApprovedUtc = utc ?? ""; ApprovedBy = SafeHttp.AppVersion();
            if (ApprovedSeen == null) ApprovedSeen = new List<string>();
            if (!ApprovedSeen.Contains(approved.Sha256.ToUpperInvariant())) ApprovedSeen.Add(approved.Sha256.ToUpperInvariant());
            if (changed) DismissedLoader = "";
            return changed;
        }
        internal static string FilePath { get { return Path.Combine(AppConfig.DataDir, "update-settings.json"); } }
        internal static UpdateSettings Load()
        {
            try { if (File.Exists(FilePath)) return new JavaScriptSerializer().Deserialize<UpdateSettings>(File.ReadAllText(FilePath)) ?? new UpdateSettings(); }
            catch (Exception ex) { Program.Log(ex); }
            return new UpdateSettings();
        }
        internal void Save()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, new JavaScriptSerializer().Serialize(this));
            DurableFile.Publish(tmp, FilePath);
        }
        internal bool Due(DateTime nowUtc)
        {
            DateTime last;
            return !DateTime.TryParse(LastCheckUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out last) || nowUtc - last.ToUniversalTime() >= UpdateCheck.Interval;
        }
    }

    internal static class UpdateCheck
    {
        internal const string DefaultApiUrl = "https://api.github.com/repos/" + LoaderPin.Repo + "/releases?per_page=100";
        internal static string ApiUrl = DefaultApiUrl;   // tests: a loopback URL
        // 1.0.19: the approved list (the maintainer approves a Slots Loader by merging the change to this file).
        internal const string DefaultListUrl = "https://raw.githubusercontent.com/" + LoaderPin.Repo + "/main/dependencies.json";
        internal static string ListUrl = DefaultListUrl;   // tests: a loopback URL
        internal const long MaxListBytes = 64 * 1024;
        internal const string ListMarker = "dependencies.json";
        internal static int TimeoutMs = 10000;
        internal static readonly TimeSpan Interval = TimeSpan.FromHours(24);
        private static readonly Regex VersionPart = new Regex(@"^v?(\d+)\.(\d+)(?:\.(\d+))?(?:\.(\d+))?", RegexOptions.IgnoreCase);

        internal static Version ParseVersion(string text)
        {
            Match m = VersionPart.Match((text ?? "").Trim());
            if (!m.Success) return null;
            int a = Int32.Parse(m.Groups[1].Value), b = Int32.Parse(m.Groups[2].Value), c = m.Groups[3].Success ? Int32.Parse(m.Groups[3].Value) : 0, d = m.Groups[4].Success ? Int32.Parse(m.Groups[4].Value) : 0;
            return new Version(a, b, c, d);
        }
        internal static Version Running { get { return ParseVersion(SafeHttp.AppVersion()); } }
        // What the UI shows: "1.9.8-custom-loose-colours" -> "1.9.8", "v2.4.0" -> "2.4.0". Logs and CLI keep the full string.
        internal static string ShortVersion(string text)
        {
            if (String.IsNullOrEmpty(text)) return text;
            Match m = VersionPart.Match(text.Trim());
            return m.Success ? m.Value.TrimStart('v', 'V') : text;
        }

        // "loader-v1.9.8" -> (loader, "1.9.8"); "manager-v1.0.0" -> manager. Public versions restart at 1.0.0, so the
        // tags of the private builds ("installer-v2.0.0") are never read: they would look newer than every 1.x.
        internal static bool ProductOf(string tag, out string product, out string version)
        {
            product = null; version = null; tag = (tag ?? "").Trim();
            foreach (var p in new[] { new[] { "loader-", UpdateProducts.Loader }, new[] { "manager-", UpdateProducts.Manager } })
                if (tag.StartsWith(p[0], StringComparison.OrdinalIgnoreCase))
                {
                    string rest = tag.Substring(p[0].Length);
                    if (ParseVersion(rest) == null) return false;
                    product = p[1]; version = rest.TrimStart('v', 'V'); return true;
                }
            return false;
        }

        // 1.0.19: the approved Slots Loader from the list, or null with the reason (logged: offline, a refused list).
        internal static ReleaseOffer ReadList(out string reason)
        {
            reason = "";
            string json;
            try { json = SafeHttp.GetText(ListUrl, MaxListBytes, null, TimeoutMs); }
            catch (DownloadFailure f) { reason = f.Reason; Program.LogLine("Update check: the approved Slotsloader list could not be read: " + f.Reason + "."); return null; }
            try
            {
                DependencyList list = DependencyList.Parse(json);
                if (list.IgnoredPackages.Count > 0) Program.LogLine("Update check: approved Slotsloader " + list.Loader.Version + " (" + list.Loader.Asset + "); not used here: " + String.Join(", ", list.IgnoredPackages) + ".");
                return ReleaseOffer.Approved(list.Loader);
            }
            catch (InvalidDataException ex) { reason = ex.Message.TrimEnd('.'); Program.LogLine("Update check: refused the approved Slotsloader list: " + ex.Message); return null; }
        }

        internal static UpdateCheckResult Check(bool includePrereleases)
        {
            var result = new UpdateCheckResult { CheckedUtc = DateTime.UtcNow.ToString("o") };
            string listReason; result.Loader = ReadList(out listReason); result.ListReason = listReason;
            string json;
            try { json = SafeHttp.GetText(ApiUrl, 8L * 1024 * 1024, "application/vnd.github+json", TimeoutMs); }
            catch (DownloadFailure f) { result.Reason = f.Reason; result.Status = f.Status; return result; }
            List<Dictionary<string, object>> releases;
            try { releases = ((object[])new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.DeserializeObject(json)).OfType<Dictionary<string, object>>().ToList(); }
            catch (Exception ex) { if (ex is InvalidCastException || ex is ArgumentException || ex is InvalidOperationException) { result.Reason = "the releases list could not be read"; return result; } throw; }
            result.Ok = true;
            foreach (string product in new[] { UpdateProducts.Manager })   // 1.0.19: loader-v* releases are no longer read
            {
                var candidates = new List<KeyValuePair<Version, Dictionary<string, object>>>();
                foreach (var r in releases)
                {
                    string p, v;
                    if (!ProductOf(Str(r, "tag_name"), out p, out v) || p != product) continue;
                    if (Bool(r, "draft")) continue;
                    if (Bool(r, "prerelease") && !includePrereleases) continue;
                    candidates.Add(new KeyValuePair<Version, Dictionary<string, object>>(ParseVersion(v), r));
                }
                foreach (var c in candidates.OrderByDescending(c => c.Key).ThenBy(c => Bool(c.Value, "prerelease")))
                {
                    string why; ReleaseOffer offer = Offer(product, c.Value, out why);
                    if (offer == null) { string line = Str(c.Value, "tag_name") + ": " + why; result.Refused.Add(line); Program.LogLine("Update check: refused " + line + "."); continue; }
                    result.Manager = offer;
                    break;
                }
            }
            return result;
        }

        // The release's asset with a verifiable SHA-256 and size, or null with the reason.
        private static ReleaseOffer Offer(string product, Dictionary<string, object> r, out string why)
        {
            why = "";
            string p, v; ProductOf(Str(r, "tag_name"), out p, out v);
            var assets = (r.ContainsKey("assets") && r["assets"] is object[] ? (object[])r["assets"] : new object[0]).OfType<Dictionary<string, object>>().ToList();
            var zips = assets.Where(a => Str(a, "name").EndsWith(".zip", StringComparison.OrdinalIgnoreCase)).ToList();
            Dictionary<string, object> asset = product == UpdateProducts.Manager ? zips.FirstOrDefault(a => Str(a, "name").EndsWith("-portable.zip", StringComparison.OrdinalIgnoreCase)) : null;
            if (asset == null)
            {
                if (zips.Count != 1) { why = zips.Count == 0 ? "no .zip asset" : zips.Count + " .zip assets (expected one" + (product == UpdateProducts.Manager ? ", or a *-portable.zip" : "") + ")"; return null; }
                asset = zips[0];
            }
            long size = Long(asset, "size");
            string url = Str(asset, "browser_download_url"), name = Str(asset, "name");
            if (size <= 0 || url.Length == 0) { why = name + " has no size or download URL"; return null; }
            string sha = "", source = "";
            string digest = Str(asset, "digest");
            if (digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) && Regex.IsMatch(digest.Substring(7), "^[0-9a-fA-F]{64}$")) { sha = digest.Substring(7).ToUpperInvariant(); source = "GitHub digest"; }
            else
            {
                var sums = assets.FirstOrDefault(a => Str(a, "name").Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase));
                if (sums == null) { why = name + " has no SHA-256 digest and the release has no SHA256SUMS.txt"; return null; }
                try
                {
                    string text = SafeHttp.GetText(Str(sums, "browser_download_url"), 256 * 1024, null, TimeoutMs);
                    foreach (string line in text.Split('\n'))
                    {
                        Match m = Regex.Match(line.Trim(), @"^([0-9a-fA-F]{64})\s+\*?(.+)$");
                        if (m.Success && m.Groups[2].Value.Trim().Equals(name, StringComparison.OrdinalIgnoreCase)) { sha = m.Groups[1].Value.ToUpperInvariant(); source = "SHA256SUMS.txt"; break; }
                    }
                }
                catch (DownloadFailure f) { why = "SHA256SUMS.txt could not be read (" + f.Reason + ")"; return null; }
                if (sha.Length == 0) { why = "SHA256SUMS.txt does not list " + name; return null; }
            }
            return new ReleaseOffer { Product = product, Tag = Str(r, "tag_name"), Version = v, Name = Str(r, "name"), HtmlUrl = Str(r, "html_url"), Asset = name, Url = url, Size = size, Sha256 = sha, DigestSource = source, Prerelease = Bool(r, "prerelease") };
        }

        private static string Str(Dictionary<string, object> d, string key) { object v; return d != null && d.TryGetValue(key, out v) && v != null ? Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) : ""; }
        private static bool Bool(Dictionary<string, object> d, string key) { object v; return d.TryGetValue(key, out v) && v is bool && (bool)v; }
        private static long Long(Dictionary<string, object> d, string key) { object v; if (!d.TryGetValue(key, out v) || v == null) return 0; try { return Convert.ToInt64(v, System.Globalization.CultureInfo.InvariantCulture); } catch (FormatException) { return 0; } }

        // What to offer. SlotsLoader (1.0.19): only when this app installed it (a tool-mod record) and the approved
        // version differs from that record's: newer is an update, older means the maintainer took an approval back and
        // is offered as going back (GoesBack). Equal: nothing. Manager: newer than the running build.
        // The pin the install link falls back to: the build's own, or a test's fixture pin (the updates suite).
        internal static LoaderPin PinOverride;
        private static LoaderPin Pin { get { return PinOverride ?? LoaderPin.Release; } }
        // fromApproval (1.0.19, the user): going back is offered only over a loader the Manager installed from an approved
        // entry (FromApproval). One the user installed themselves (a zip they are testing before it is approved) is
        // never offered a downgrade; a newer approved version is still offered over it.
        internal static ReleaseOffer LoaderUpdate(ReleaseOffer approved, string installedVersion, bool fromApproval)
        {
            if (approved == null || approved.Parsed == null) return null;
            Version installed = ParseVersion(installedVersion);
            if (installed == null || approved.Parsed == installed) return null;
            if (approved.Parsed < installed && !fromApproval) return null;
            return approved;
        }
        internal static ReleaseOffer LoaderUpdate(UpdateCheckResult r, string installedVersion, bool fromApproval) { return LoaderUpdate(r == null ? null : r.Loader, installedVersion, fromApproval); }
        // The recorded loader came from an approved zip: the build's pin, or one this Manager read from the list (by
        // the SHA-256 of the installed archive, ReplaceMod.ArchiveSha).
        internal static bool FromApproval(ReplaceMod record, UpdateSettings s)
        {
            string sha = record == null ? null : record.ArchiveSha;
            if (String.IsNullOrEmpty(sha)) return false;
            if (sha.Equals(Pin.Sha256, StringComparison.OrdinalIgnoreCase) || sha.Equals(LoaderPin.Release.Sha256, StringComparison.OrdinalIgnoreCase)) return true;
            return s != null && s.ApprovedSeen != null && s.ApprovedSeen.Any(x => sha.Equals(x, StringComparison.OrdinalIgnoreCase));
        }
        internal static bool GoesBack(ReleaseOffer offer, string installedVersion)
        {
            Version installed = ParseVersion(installedVersion);
            return offer != null && offer.Parsed != null && installed != null && offer.Parsed < installed;
        }
        internal static ReleaseOffer ManagerUpdate(UpdateCheckResult r, Version running)
        {
            if (r == null || r.Manager == null || r.Manager.Parsed == null || running == null) return null;
            return r.Manager.Parsed > running ? r.Manager : null;
        }
        // The install link without a loader (1.0.19): the approved version last read from the list, else the build's pin
        // (the same entry as it was when this Manager was built; its zip is bundled beside the exe).
        internal static LoaderPin InstallPin(ReleaseOffer approved)
        {
            if (approved == null || !approved.IsApproval) return Pin;
            if (approved.Sha256.Equals(Pin.Sha256, StringComparison.OrdinalIgnoreCase) && approved.Asset == Pin.Asset) return Pin;
            return approved.AsPin();
        }

        // A 404 (the repository is private), offline or a time-out is not an error for the background check.
        internal static string Describe(UpdateCheckResult r)
        {
            if (r == null) return "Not checked yet";
            if (!r.Ok) return "Could not check for updates: " + r.Reason;
            return "Checked " + (DateTime.Parse(r.CheckedUtc, null, System.Globalization.DateTimeStyles.RoundtripKind).ToLocalTime().ToString("d MMM HH:mm"));
        }
    }

    // The SlotsLoader this app installed (its tool-mod record) and its version: the modinfo.ini "version" of the
    // recorded source zip/folder when it still exists, else the version in the source's file name.
    internal static class InstalledLoader
    {
        internal static ReplaceMod Record(ReplaceState state) { return state == null ? null : state.Mods.FirstOrDefault(LoaderCostumePak.IsLoaderMod); }
        internal static string Version(ReplaceMod mod, out string source)
        {
            source = "";
            if (mod == null) return null;
            string path = mod.SourcePath ?? "";
            try
            {
                string text = null;
                if (File.Exists(path) && path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    using (var zip = System.IO.Compression.ZipFile.OpenRead(path))
                    {
                        var entry = zip.Entries.Where(e => e.Name.Equals("modinfo.ini", StringComparison.OrdinalIgnoreCase)).OrderBy(e => e.FullName.Length).FirstOrDefault();
                        if (entry != null) using (var r = new StreamReader(entry.Open())) text = r.ReadToEnd();
                    }
                else if (Directory.Exists(path) && File.Exists(Path.Combine(path, "modinfo.ini"))) text = File.ReadAllText(Path.Combine(path, "modinfo.ini"));
                if (text != null)
                {
                    Match m = Regex.Match(text, @"^\s*version\s*=\s*(.+?)\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
                    if (m.Success && UpdateCheck.ParseVersion(m.Groups[1].Value) != null) { source = "modinfo.ini"; return m.Groups[1].Value; }
                }
            }
            catch (Exception ex) { if (!(ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException)) throw; }
            Match n = Regex.Match(Path.GetFileName(path), @"v(\d+\.\d+(?:\.\d+){0,2})", RegexOptions.IgnoreCase);   // 1.0.19: four parts too (v1.27.3.14)
            if (n.Success) { source = "file name"; return n.Groups[1].Value; }
            return null;
        }
    }
}
