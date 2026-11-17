using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace SlotsInstaller
{
    // 2.3.1 --loader-check <new dir> (test.ps1 suite "loader"): the pinned SlotsLoader fetch against a loopback HTTP
    // server (no real network): library hash hit, cached copy, download (through a redirect), wrong hash, oversize,
    // 404, offline, time-out, cancel, non-HTTPS refusals, a renamed non-pinned zip, and the loader-fetch CLI.
    // --loader-real-check <output> (-RealArchives, suite loader-real): read-only on the user's libraries.
    internal static class LoaderChecks
    {
        internal static int Run(string root)
        {
            root = Path.GetFullPath(root);
            if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any()) throw new IOException("Choose a new folder for the loader checks.");
            Directory.CreateDirectory(root);
            var report = new List<string>(); int failures = 0;
            Action<string, Action> check = delegate(string name, Action body)
            {
                var clock = Stopwatch.StartNew();
                try { body(); report.Add("PASS " + name + " (" + clock.ElapsedMilliseconds + " ms)"); }
                catch (Exception ex) { failures++; report.Add("FAIL " + name + ": " + ex.Message); Program.Log(ex); }
                finally { LoaderRelease.Pin = LoaderPin.Release; LoaderRelease.AllowLoopbackHttp = false; LoaderRelease.TimeoutMs = 30000; }
            };
            byte[] good = FixtureZip("good"), other = FixtureZip("other-build");
            using (var server = new LoopbackServer())
            {
                LoaderPin pin = Pin(good, server.Base + "/release/loader.zip");
                server.Routes["/release/loader.zip"] = new LoopbackServer.Route { Status = 302, Location = "/objects/loader.zip" };
                server.Routes["/objects/loader.zip"] = new LoopbackServer.Route { Body = good };

                check("1.0.19: the pin is the approved slots_loader entry of the repository's dependencies.json, embedded by build.ps1 (the same file the update check reads): a release download of Wael3rd/SF6-ModSuite with size and SHA-256; nothing else can change it", delegate
                {
                    LoaderPin r = LoaderPin.Release;
                    string repoList = Path.Combine(Path.GetDirectoryName(Environment.GetEnvironmentVariable("SLOTSLOADER_PROJECT") ?? ""), "dependencies.json");
                    Require(r.Asset.EndsWith(".zip") && r.Size > 0 && r.Sha256.Length == 64 && r.Sha256 == r.Sha256.ToUpperInvariant() && UpdateCheck.ParseVersion(r.Version) != null && r.Tag.Length > 0, "pin values: " + r.Tag + " " + r.Asset + " " + r.Size + " " + r.Sha256);
                    Require(r.Url == "https://github.com/Wael3rd/SF6-ModSuite/releases/download/" + r.Tag + "/" + r.Asset && r.ReleasePage == "https://github.com/Wael3rd/SF6-ModSuite/releases/tag/" + r.Tag && LoaderPin.DownloadRefusal(r.Url) == null, "URLs: " + r.Url);
                    Require(LoaderRelease.Pin == r && !LoaderRelease.AllowLoopbackHttp, "default pin");
                    Require(LoaderRelease.Description(r) == r.Asset + " (" + Utilities.FriendlyBytes(r.Size) + ") from github.com/Wael3rd/SF6-ModSuite", "description: " + LoaderRelease.Description(r));
                    if (File.Exists(repoList))
                    {
                        LoaderPin file = LoaderPin.FromList(File.ReadAllText(repoList));
                        Require(file.Version == r.Version && file.Sha256 == r.Sha256 && file.Size == r.Size && file.Asset == r.Asset && file.Tag == r.Tag, "the embedded pin is not the repository's dependencies.json: " + file.Version + " / " + r.Version);
                        report.Add("  pin: Slots Loader " + r.Version + " · " + r.Tag + " · " + r.Asset + " · " + r.Size + " bytes · SHA-256 " + r.Sha256.Substring(0, 12) + "… (= " + repoList + ")");
                    }
                });
                check("1.0.19 dependencies.json parsing: the good list (native_core and unknown packages and keys ignored); malformed JSON, another format or source, no packages, no slots_loader, and every bad field are refused with the reason, never an exception of another kind", delegate
                {
                    string sha = new string('a', 64);
                    LoaderPin parsed = LoaderPin.FromList(ListJson("1.27.7", "v1.40.0", "SF6_SlotsLoader_v1.27.7.zip", 1400000, sha, ",\"native_core\":{\"name\":\"SF6 Native Core\",\"version\":\"0.9.14\",\"release\":\"v1.37.0\",\"file\":\"SF6_NativeCore_0.9.14.zip\",\"size\":684069,\"sha256\":\"" + sha + "\"},\"future_thing\":{\"anything\":[1,2,3]}", ",\"generated\":\"2026-11-16\",\"notes\":{\"x\":1}"));
                    Require(parsed.Version == "1.27.7" && parsed.Tag == "v1.40.0" && parsed.Sha256 == sha.ToUpperInvariant() && parsed.Size == 1400000 && parsed.Name == "SF6 Slots Loader"
                        && parsed.Url == "https://github.com/Wael3rd/SF6-ModSuite/releases/download/v1.40.0/SF6_SlotsLoader_v1.27.7.zip" && parsed.ReleasePage == "https://github.com/Wael3rd/SF6-ModSuite/releases/tag/v1.40.0", "parsed: " + parsed.Url);
                    DependencyList list = DependencyList.Parse(ListJson("1.27.7", "v1.40.0", "SF6_SlotsLoader_v1.27.7.zip", 1400000, sha, ",\"native_core\":{\"version\":\"0.9.14\"},\"zzz\":5", ""));
                    Require(list.IgnoredPackages.SequenceEqual(new[] { "native_core", "zzz" }), "ignored packages: " + String.Join(", ", list.IgnoredPackages));
                    string goodList = ListJson("1.27.7", "v1.40.0", "SF6_SlotsLoader_v1.27.7.zip", 1400000, sha, "", "");
                    var bad = new Dictionary<string, string>
                    {
                        { "not JSON", "{\"format\":1," }, { "an array", "[1,2]" }, { "empty", "" },
                        { "format 2", goodList.Replace("\"format\":1", "\"format\":2") }, { "format as text", goodList.Replace("\"format\":1", "\"format\":\"1\"") }, { "no format", goodList.Replace("\"format\":1,", "") },
                        { "another source", goodList.Replace("Wael3rd/SF6-ModSuite", "Someone/Else") }, { "no packages", "{\"format\":1,\"source\":\"Wael3rd/SF6-ModSuite\"}" },
                        { "no slots_loader", "{\"format\":1,\"source\":\"Wael3rd/SF6-ModSuite\",\"packages\":{\"native_core\":{\"version\":\"0.9.14\"}}}" },
                        { "slots_loader not an object", "{\"format\":1,\"source\":\"Wael3rd/SF6-ModSuite\",\"packages\":{\"slots_loader\":\"1.27.7\"}}" },
                        { "no version", goodList.Replace("\"version\":\"1.27.7\",", "") }, { "bad version", goodList.Replace("\"1.27.7\"", "\"latest\"") },
                        { "release with a slash", goodList.Replace("\"v1.40.0\"", "\"v1/../x\"") }, { "file with a path", goodList.Replace("\"SF6_SlotsLoader_v1.27.7.zip\"", "\"../SF6_SlotsLoader_v1.27.7.zip\"") },
                        { "file not a zip", goodList.Replace("SF6_SlotsLoader_v1.27.7.zip", "SF6_SlotsLoader_v1.27.7.exe") }, { "size 0", goodList.Replace("1400000", "0") }, { "size as text", goodList.Replace("1400000", "\"1400000\"") },
                        { "size with a fraction", goodList.Replace("1400000", "1400000.5") }, { "short sha256", goodList.Replace(sha, "abc") }, { "sha256 not hex", goodList.Replace(sha, new string('g', 64)) }
                    };
                    foreach (var b in bad)
                    {
                        string why = null; try { LoaderPin.FromList(b.Value); } catch (InvalidDataException ex) { why = ex.Message; }
                        Require(why != null && why.StartsWith("dependencies.json: "), "accepted a bad list (" + b.Key + "): " + b.Value);
                    }
                    report.Add("  " + bad.Count + " malformed lists refused, e.g. " + Reason(bad["no slots_loader"]) + " / " + Reason(bad["another source"]));
                });
                check("1.0.19 download host: an HTTPS loader download is allowed only as a release download of Wael3rd/SF6-ModSuite on github.com (refused before any request: this repository's old loader-v* assets, other repositories, other hosts, odd paths)", delegate
                {
                    foreach (string ok in new[] { "https://github.com/Wael3rd/SF6-ModSuite/releases/download/v1.37.0/SF6_SlotsLoader_v1.27.4.zip", "https://github.com/wael3rd/sf6-modsuite/releases/download/v1.37.0/x.zip" })
                        Require(LoaderPin.DownloadRefusal(ok) == null, "refused " + ok);
                    string[] refused = { "https://github.com/ZZtaii/slotsloader-manager/releases/download/loader-v1.10.9/SF6_Slotsloader_Fork_v1.10.9.zip", "https://github.com/Wael3rd/Other/releases/download/v1/x.zip",
                        "https://objects.githubusercontent.com/Wael3rd/SF6-ModSuite/releases/download/v1/x.zip", "https://github.com.example.invalid/Wael3rd/SF6-ModSuite/releases/download/v1/x.zip", "https://github.com:8443/Wael3rd/SF6-ModSuite/releases/download/v1/x.zip",
                        "https://user@github.com/Wael3rd/SF6-ModSuite/releases/download/v1/x.zip", "https://github.com/Wael3rd/SF6-ModSuite/releases/download/v1/x.zip?y=1", "https://github.com/Wael3rd/SF6-ModSuite/releases/download/x.zip",
                        "https://github.com/Wael3rd/SF6-ModSuite/releases/download/v1/sub/x.zip", "https://github.com/Wael3rd/SF6-ModSuite/releases/tag/v1", "not a url" };
                    int requests = server.Requests; string cache = Dir(root, "host");
                    foreach (string url in refused)
                    {
                        Require(LoaderPin.DownloadRefusal(url) != null, "allowed " + url);
                        if (!url.StartsWith("https://")) continue;
                        LoaderFetchException ex = Fails(delegate { LoaderRelease.Download(With(pin, url), cache); });
                        Require(ex.Reason.StartsWith("only release downloads of Wael3rd/SF6-ModSuite on github.com are allowed") && Empty(cache), url + ": " + ex.Reason);
                    }
                    Require(server.Requests == requests, "a refused address was requested");
                    report.Add("  refused: " + LoaderPin.DownloadRefusal(refused[0]));
                });
                check("2.5.9: the zip bundled in the app folder's Slotsloader\\ is used first (no library hashing, no request); a bundled file that does not verify is skipped and left alone", delegate
                {
                    string bundled = Dir(root, "bundled"), lib = Dir(root, "bundled-lib"), cache = Dir(root, "bundled-cache");
                    File.WriteAllBytes(Path.Combine(lib, "copy.zip"), good);
                    string previous = LoaderRelease.BundledDirOverride; LoaderRelease.BundledDirOverride = bundled;
                    try
                    {
                        File.WriteAllBytes(Path.Combine(bundled, pin.Asset), good);
                        int requests = server.Requests, hashed = LoaderRelease.HashedFiles;
                        LoaderFile found = LoaderRelease.Fetch(pin, new[] { lib }, cache, true);
                        Require(found != null && found.Source == LoaderSource.Bundled && found.Path == Path.Combine(bundled, pin.Asset) && found.SourceLabel == "copy bundled with the Manager" && LoaderRelease.HashedFiles == hashed && server.Requests == requests, "bundled hit: " + (found == null ? "none" : found.Source + " " + found.Path));
                        File.WriteAllBytes(Path.Combine(bundled, pin.Asset), SameSize(other, good.Length));
                        found = LoaderRelease.FindLocal(pin, new[] { lib }, cache);
                        Require(found != null && found.Source == LoaderSource.Library && File.Exists(Path.Combine(bundled, pin.Asset)), "a bundled zip that does not verify: " + (found == null ? "none" : found.Source.ToString()));
                    }
                    finally { LoaderRelease.BundledDirOverride = previous; }
                });
                check("library hit by SHA-256 without any network: only files of the pinned size are hashed; a renamed non-pinned SF6_SlotsLoader_v9.9.zip (same size) is not chosen", delegate
                {
                    string lib = Dir(root, "lib-hit"), lib2 = Dir(root, "lib-hit-2"), cache = Dir(root, "cache-unused");
                    for (int i = 0; i < 25; i++) File.WriteAllBytes(Path.Combine(lib, "mod" + i + ".zip"), new byte[100 + i]);
                    File.WriteAllBytes(Path.Combine(lib, "SF6_SlotsLoader_v9.9.zip"), SameSize(other, good.Length));
                    File.WriteAllBytes(Path.Combine(lib2, "renamed upstream build.zip"), good);
                    LoaderRelease.Pin = pin; int requests = server.Requests, hashed = LoaderRelease.HashedFiles;
                    LoaderFile found = LoaderRelease.Fetch(pin, new[] { lib, lib2 }, cache, true);
                    Require(found != null && found.Source == LoaderSource.Library && found.Path == Path.Combine(lib2, "renamed upstream build.zip") && found.Sha256 == pin.Sha256, "library hit: " + (found == null ? "none" : found.Path));
                    Require(LoaderRelease.HashedFiles - hashed == 2 && server.Requests == requests && !Directory.EnumerateFileSystemEntries(cache).Any(), "hashed " + (LoaderRelease.HashedFiles - hashed) + " files, " + (server.Requests - requests) + " requests");
                    Require(LoaderRelease.FindLocal(pin, new[] { lib }, cache) == null, "the v9.9 zip was chosen");
                });
                check("cached copy in Data\\downloads is used (no request); a cached file that no longer verifies is deleted", delegate
                {
                    string cache = Dir(root, "cache-hit"); File.WriteAllBytes(Path.Combine(cache, pin.Asset), good);
                    int requests = server.Requests;
                    LoaderFile found = LoaderRelease.Fetch(pin, new[] { Dir(root, "empty-lib") }, cache, true);
                    Require(found != null && found.Source == LoaderSource.Cache && server.Requests == requests, "cache hit");
                    File.WriteAllBytes(Path.Combine(cache, pin.Asset), SameSize(other, good.Length));
                    Require(LoaderRelease.FindLocal(pin, new string[0], cache) == null && !File.Exists(Path.Combine(cache, pin.Asset)), "a bad cached copy was kept");
                });
                check("download through an HTTP redirect: .part file, progress, size + SHA-256 verified, renamed to Data\\downloads\\<asset>; the next fetch uses the cache", delegate
                {
                    string cache = Dir(root, "download"); LoaderRelease.AllowLoopbackHttp = true;
                    var progress = new List<OperationProgress>(); LoaderFile file;
                    using (OperationContext.Begin(CancellationToken.None, delegate(OperationProgress p) { progress.Add(p); })) file = LoaderRelease.Fetch(pin, new string[0], cache, true);
                    Require(file.Source == LoaderSource.Download && file.Path == Path.Combine(cache, pin.Asset) && File.ReadAllBytes(file.Path).SequenceEqual(good) && !File.Exists(file.Path + ".part"), "download result");
                    Require(progress.Count > 0 && progress.All(p => p.Stage == "Downloading Slotsloader " + pin.Version && p.Total == pin.Size) && progress.Last().Completed == pin.Size, "progress: " + progress.Count);
                    Require(server.Log.Skip(server.Log.Count - 2).SequenceEqual(new[] { "GET /release/loader.zip 302", "GET /objects/loader.zip 200" }), "requests: " + String.Join(", ", server.Log));
                    int requests = server.Requests;
                    Require(LoaderRelease.Fetch(pin, new string[0], cache, true).Source == LoaderSource.Cache && server.Requests == requests, "second fetch downloaded again");
                });
                check("a download with the wrong SHA-256 is rejected and deleted (nothing left to install)", delegate
                {
                    server.Routes["/wrong.zip"] = new LoopbackServer.Route { Body = SameSize(other, good.Length) };
                    string cache = Dir(root, "wrong"); LoaderRelease.AllowLoopbackHttp = true;
                    LoaderFetchException ex = Fails(delegate { LoaderRelease.Download(With(pin, server.Base + "/wrong.zip"), cache); });
                    Require(ex.Reason.Contains("SHA-256") && ex.Reason.Contains("does not match the pinned build") && Empty(cache), "wrong hash: " + ex.Reason);
                    Require(ex.Message.Contains(pin.ReleasePage) && ex.Message.EndsWith("and drop the zip on the window."), "advice: " + ex.Message);
                });
                check("oversize: a Content-Length above the pin is refused before reading, and a body without a length is stopped once it passes the pinned size", delegate
                {
                    server.Routes["/big.zip"] = new LoopbackServer.Route { Body = good.Concat(new byte[5000]).ToArray() };
                    server.Routes["/big-unsized.zip"] = new LoopbackServer.Route { Body = good.Concat(new byte[400000]).ToArray(), OmitLength = true };
                    string cache = Dir(root, "big"); LoaderRelease.AllowLoopbackHttp = true;
                    LoaderFetchException a = Fails(delegate { LoaderRelease.Download(With(pin, server.Base + "/big.zip"), cache); });
                    Require(a.Reason.Contains("larger than the pinned") && Empty(cache), "sized: " + a.Reason);
                    LoaderFetchException b = Fails(delegate { LoaderRelease.Download(With(pin, server.Base + "/big-unsized.zip"), cache); });
                    Require(b.Reason.Contains("grew past the pinned") && Empty(cache), "unsized: " + b.Reason);
                    server.Routes["/short.zip"] = new LoopbackServer.Route { Body = good.Take(good.Length - 10).ToArray() };
                    LoaderFetchException c = Fails(delegate { LoaderRelease.Download(With(pin, server.Base + "/short.zip"), cache); });
                    Require(c.Reason.Contains("not the pinned") && Empty(cache), "short: " + c.Reason);
                });
                check("failures give one message with the reason and where to get the zip: 404, offline (connection refused), time-out", delegate
                {
                    string cache = Dir(root, "fail"); LoaderRelease.AllowLoopbackHttp = true;
                    LoaderFetchException missing = Fails(delegate { LoaderRelease.Download(With(pin, server.Base + "/missing.zip"), cache); });
                    Require(missing.Message.StartsWith("Couldn't download Slotsloader " + pin.Version + ": HTTP 404 Not Found from 127.0.0.1 (the release isn't public or no longer exists).") && missing.Message.Contains("Download it from " + pin.ReleasePage + " and drop the zip on the window."), "404: " + missing.Message);
                    int closed = LoopbackServer.ClosedPort();   // a real refused connect (about 2 s on Windows); the other offline cases use RefusedPort
                    LoaderFetchException offline = Fails(delegate { LoaderRelease.Download(With(pin, "http://127.0.0.1:" + closed + "/loader.zip"), cache); });
                    Require(offline.Reason == "could not connect to 127.0.0.1", "offline: " + offline.Reason);
                    server.Routes["/hang.zip"] = new LoopbackServer.Route { HangMs = 5000 };
                    LoaderRelease.TimeoutMs = 1000;
                    LoaderFetchException slow = Fails(delegate { LoaderRelease.Download(With(pin, server.Base + "/hang.zip"), cache); });
                    Require(slow.Reason == "no answer from 127.0.0.1 within 1 s" && Empty(cache), "time-out: " + slow.Reason);
                    report.Add("  404: " + missing.Message.Replace("\n", " "));
                    report.Add("  offline: " + offline.Reason + "; time-out: " + slow.Reason);
                });
                check("cancel during the download stops it and removes the .part file", delegate
                {
                    // 320 KB sent in 16 KB chunks every 60 ms (about 1.2 s): cancelled after the first chunk.
                    var large = new byte[320000]; new Random(7).NextBytes(large);
                    server.Routes["/slow.zip"] = new LoopbackServer.Route { Body = large, ChunkDelayMs = 60 };
                    string cache = Dir(root, "cancel"); LoaderRelease.AllowLoopbackHttp = true;
                    using (var cts = new CancellationTokenSource())
                    {
                        bool cancelled = false; long seenBytes = 0; var clock = Stopwatch.StartNew();
                        try
                        {
                            using (OperationContext.Begin(cts.Token, delegate(OperationProgress p) { seenBytes = p.Completed; if (p.Completed > 0) cts.Cancel(); }))
                                LoaderRelease.Download(Pin(large, server.Base + "/slow.zip"), cache);
                        }
                        catch (OperationCanceledException) { cancelled = true; }
                        Require(cancelled && Empty(cache) && seenBytes < large.Length && clock.ElapsedMilliseconds < 1000, "cancel: " + cancelled + " after " + seenBytes + " bytes, " + clock.ElapsedMilliseconds + " ms, files " + String.Join(", ", Directory.GetFiles(cache)));
                        report.Add("  cancelled after " + seenBytes + " of " + large.Length + " bytes, " + clock.ElapsedMilliseconds + " ms");
                    }
                });
                check("HTTPS only: a plain-http address and a redirect to a non-HTTPS address are refused before connecting", delegate
                {
                    string cache = Dir(root, "https");
                    int requests = server.Requests;
                    LoaderFetchException plain = Fails(delegate { LoaderRelease.Download(With(pin, server.Base + "/objects/loader.zip"), cache); });
                    Require(plain.Reason.StartsWith("the address is not HTTPS") && server.Requests == requests, "plain http: " + plain.Reason);
                    LoaderRelease.AllowLoopbackHttp = true;
                    server.Routes["/to-http.zip"] = new LoopbackServer.Route { Status = 302, Location = "http://downloads.example.invalid/loader.zip" };
                    server.Routes["/to-ftp.zip"] = new LoopbackServer.Route { Status = 301, Location = "ftp://127.0.0.1/loader.zip" };
                    foreach (string path in new[] { "/to-http.zip", "/to-ftp.zip" })
                    {
                        LoaderFetchException ex = Fails(delegate { LoaderRelease.Download(With(pin, server.Base + path), cache); });
                        Require(ex.Reason.StartsWith("the server redirected to a non-HTTPS address") && Empty(cache), path + ": " + ex.Reason);
                    }
                    server.Routes["/loop.zip"] = new LoopbackServer.Route { Status = 302, Location = "/loop.zip" };
                    Require(Fails(delegate { LoaderRelease.Download(With(pin, server.Base + "/loop.zip"), cache); }).Reason.StartsWith("too many redirects"), "redirect loop");
                });
                check("loader-fetch (CLI, in-process): prints the source, path and SHA-256; --no-download without a copy and a failed download exit 1", delegate
                {
                    string lib = Dir(root, "cli-lib"); File.WriteAllBytes(Path.Combine(lib, "loader copy.zip"), good);
                    LoaderRelease.Pin = pin;
                    string hit = ReplaceChecks.Cli("loader-fetch", "--library", lib, "--no-download");
                    Require(hit.StartsWith("0\n") && hit.Contains("Source: library") && hit.Contains("Path: " + Path.Combine(lib, "loader copy.zip")) && hit.Contains("SHA-256: " + pin.Sha256), "hit: " + hit);
                    string cached = Path.Combine(LoaderRelease.DownloadDir, pin.Asset);
                    Require(!File.Exists(cached), "this run's Data already has a download");
                    string none = ReplaceChecks.Cli("loader-fetch", "--library", Dir(root, "cli-empty"), "--no-download");
                    Require(none.StartsWith("1\n") && none.Contains("nothing was downloaded") && !File.Exists(cached), "no copy: " + none);
                    LoaderRelease.AllowLoopbackHttp = true;
                    LoaderRelease.Pin = With(pin, server.Base + "/missing.zip");
                    string failed = ReplaceChecks.Cli("loader-fetch", "--library", Dir(root, "cli-empty2"));
                    Require(failed.StartsWith("1\n") && failed.Contains("ERROR: Couldn't download Slotsloader") && failed.Contains("HTTP 404") && !File.Exists(cached) && !File.Exists(cached + ".part"), "failed: " + failed);
                    LoaderRelease.Pin = pin;
                    string downloaded = ReplaceChecks.Cli("loader-fetch", "--library", Dir(root, "cli-empty3"));
                    Require(downloaded.StartsWith("0\n") && downloaded.Contains("Source: download") && File.Exists(cached), "download: " + downloaded);
                    File.Delete(cached);
                    report.Add("  " + String.Join(" | ", hit.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Where(l => l.StartsWith("Source") || l.StartsWith("Hashed"))));
                });
            }
            File.WriteAllLines(Path.Combine(root, "loader-check.txt"), report.Concat(new[] { failures == 0 ? "ALL PASS" : failures + " FAILED" }));
            foreach (string line in report) Console.WriteLine(line);
            Console.WriteLine(failures == 0 ? "ALL PASS" : failures + " FAILED");
            return failures == 0 ? 0 : 1;
        }

        // Read-only on the user's libraries: the pinned zip is found there by SHA-256 (no download, nothing written
        // into a library), the CLI prints the same, and one anonymous GET of the pinned URL records its status.
        internal static int RunReal(string output, string cliFolder)
        {
            output = Path.GetFullPath(output); Directory.CreateDirectory(output);
            var report = new List<string>(); int failures = 0;
            Action<string, Action> check = delegate(string name, Action body)
            {
                var clock = Stopwatch.StartNew();
                try { body(); report.Add("PASS " + name + " (" + clock.ElapsedMilliseconds + " ms)"); }
                catch (Exception ex) { failures++; report.Add("FAIL " + name + ": " + ex.Message); Program.Log(ex); }
            };
            string[] libraries = FilterRealChecks.RealLibraries;
            check("the pinned Slotsloader zip, when a library has it, is found in the user's libraries by SHA-256 (read-only, no network)", delegate
            {
                string emptyCache = Path.Combine(output, "no-cache");
                var before = libraries.Select(Snapshot).ToList();
                int hashed = LoaderRelease.HashedFiles; var clock = Stopwatch.StartNew();
                LoaderFile file = LoaderRelease.FindLocal(LoaderPin.Release, libraries, emptyCache);
                Require(libraries.Select(Snapshot).SequenceEqual(before), "a library changed during the search");
                // 2.4.3: the libraries may not hold the newly pinned zip yet (it is downloaded on first use): that is reported, not failed.
                if (file == null) { report.Add("  no copy of " + LoaderPin.Release.Asset + " in the libraries (hashed " + (LoaderRelease.HashedFiles - hashed) + " file(s) of the pinned size, " + clock.ElapsedMilliseconds + " ms)"); return; }
                Require(file.Source == LoaderSource.Library && file.Sha256 == LoaderPin.Release.Sha256, "found the wrong file: " + file.Path);
                report.Add("  found " + file.Path + " · " + file.Size + " bytes · SHA-256 " + file.Sha256 + " · hashed " + (LoaderRelease.HashedFiles - hashed) + " file(s) of the pinned size · " + clock.ElapsedMilliseconds + " ms");
            });
            check("Slotsloader CLI.exe loader-fetch --no-download on the user's libraries prints the library copy and exits 0, or (no copy yet) exits 1 having downloaded nothing", delegate
            {
                var args = new List<string> { "loader-fetch" }; foreach (string l in libraries) { args.Add("--library"); args.Add(l); } args.Add("--no-download");
                var info = new ProcessStartInfo(Path.Combine(cliFolder, "Slotsloader CLI.exe"), String.Join(" ", args.Select(a => "\"" + a + "\""))) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = cliFolder };
                using (var p = Process.Start(info))
                {
                    var error = p.StandardError.ReadToEndAsync(); string text = p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(120000)) { try { p.Kill(); } catch (InvalidOperationException) { } throw new TimeoutException("loader-fetch timed out"); }
                    File.WriteAllText(Path.Combine(output, "loader-fetch-real.txt"), "exit " + p.ExitCode + Environment.NewLine + text + error.Result);
                    bool local = LoaderRelease.FindLocal(LoaderPin.Release, libraries, Path.Combine(output, "no-cache")) != null;
                    if (local) Require(p.ExitCode == 0 && text.Contains("Source: library") && text.Contains("SHA-256: " + LoaderPin.Release.Sha256), "loader-fetch: exit " + p.ExitCode + " " + text + error.Result);
                    else Require(p.ExitCode == 1 && error.Result.Contains("--no-download: nothing was downloaded"), "loader-fetch without a local copy: exit " + p.ExitCode + " " + text + error.Result);
                    report.Add("  " + String.Join(" | ", text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Where(l => l.StartsWith("Source") || l.StartsWith("Path") || l.StartsWith("Hashed"))));
                }
            });
            check("replace-status on the user's game (read-only, 2.4.0) lists the patch paks and names the one Slotsloader generated; a leftover costume pak is reported", delegate
            {
                string game = LocalTestConfig.Current.GameRoot;   // 2.4.3: from tests.local.json
                var before = LoaderCostumePak.Scan(game).Select(p => p.Name + "|" + p.Length + "|" + File.GetLastWriteTimeUtc(p.Path).Ticks).ToList();
                var info = new ProcessStartInfo(Path.Combine(cliFolder, "Slotsloader CLI.exe"), "replace-status --game \"" + game + "\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = cliFolder };
                using (var p = Process.Start(info))
                {
                    var error = p.StandardError.ReadToEndAsync(); string text = p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(180000)) { try { p.Kill(); } catch (InvalidOperationException) { } throw new TimeoutException("replace-status timed out"); }
                    File.WriteAllText(Path.Combine(output, "replace-status-real.txt"), "exit " + p.ExitCode + Environment.NewLine + text + error.Result);
                    string paks = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(l => l.StartsWith("Patch PAKs:")) ?? "";
                    string orphan = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(l => l.StartsWith(LoaderCostumePak.OrphanText)) ?? "";
                    Require(p.ExitCode == 0 && paks.Length > 0, "replace-status: exit " + p.ExitCode + " " + text + error.Result);
                    report.Add("  " + paks);
                    report.Add("  " + (orphan.Length > 0 ? orphan : "no leftover costume pak reported (Slotsloader present, or no loader pak)"));
                }
                var after = LoaderCostumePak.Scan(game).Select(p => p.Name + "|" + p.Length + "|" + File.GetLastWriteTimeUtc(p.Path).Ticks).ToList();
                Require(before.SequenceEqual(after), "the game's patch paks changed during the read-only status");
            });
            check("one anonymous GET of the pinned URL (no credentials; recorded, not required to succeed while the repository is private)", delegate
            {
                string probe = Path.Combine(output, "download-probe");
                try
                {
                    LoaderFile file = LoaderRelease.Download(LoaderPin.Release, probe);
                    report.Add("  the pinned URL downloaded and verified: " + file.Size + " bytes, SHA-256 " + file.Sha256);
                    File.Delete(file.Path);
                }
                catch (LoaderFetchException ex) { report.Add("  the pinned URL is not available anonymously: " + ex.Reason); }
                Require(!Directory.Exists(probe) || !Directory.EnumerateFiles(probe).Any(), "the probe left a file");
            });
            File.WriteAllLines(Path.Combine(output, "loader-real-check.txt"), report.Concat(new[] { failures == 0 ? "ALL PASS" : failures + " FAILED" }));
            foreach (string line in report) Console.WriteLine(line);
            return failures == 0 ? 0 : 1;
        }

        // The UI offer (called from ReplaceUiChecks with its window and fixture libraries).
        internal static LoaderPin Pin(byte[] bytes, string url)
        {
            string hash; using (var sha = System.Security.Cryptography.SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "");
            return new LoaderPin { Version = "1.9.7", Asset = "SF6_SlotsLoader_fixture.zip", Size = bytes.Length, Sha256 = hash, Url = url, ReleasePage = "https://github.com/ZZtaii/slotsloader-manager/releases/tag/fixture", Origin = "fixture server" };
        }
        // 1.0.19: a dependencies.json with one slots_loader entry (packages: more package entries; top: more top-level keys).
        internal static string ListJson(string version, string release, string file, long size, string sha, string packages, string top)
        {
            return "{\"format\":1,\"source\":\"Wael3rd/SF6-ModSuite\"" + top + ",\"packages\":{\"slots_loader\":{\"name\":\"SF6 Slots Loader\",\"version\":\"" + version + "\",\"release\":\"" + release + "\",\"file\":\"" + file + "\",\"size\":" + size + ",\"sha256\":\"" + sha + "\"}" + packages + "}}";
        }
        private static string Reason(string json) { try { LoaderPin.FromList(json); return "accepted"; } catch (InvalidDataException ex) { return ex.Message; } }
        internal static LoaderPin With(LoaderPin pin, string url) { return new LoaderPin { Version = pin.Version, Asset = pin.Asset, Size = pin.Size, Sha256 = pin.Sha256, Url = url, ReleasePage = pin.ReleasePage, Origin = pin.Origin }; }
        // A small SlotsLoader-shaped tool package (proxy, real AGS, plugin, loader data); the marker makes builds differ.
        internal static byte[] FixtureZip(string marker)
        {
            using (var memory = new MemoryStream())
            {
                using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
                {
                    Action<string, string> add = delegate(string name, string text) { using (var w = new StreamWriter(zip.CreateEntry(name).Open())) w.Write(text); };
                    add("amd_ags_x64.dll", "fixture loader proxy " + marker + new string('x', 4096));
                    add("amd_ags_x64_real.dll", "fixture genuine AGS copy " + marker);
                    add("reframework/plugins/SF6_CostumeSlotsNative.dll", "fixture plugin " + marker);
                    add("reframework/data/SF6_Costumes_Data/loader/version.txt", marker);
                }
                return memory.ToArray();
            }
        }
        private static byte[] SameSize(byte[] bytes, int length) { var b = new byte[length]; Array.Copy(bytes, b, Math.Min(bytes.Length, length)); return b; }
        private static string Dir(string root, string name) { string d = Path.Combine(root, name); Directory.CreateDirectory(d); return d; }
        private static bool Empty(string dir) { return !Directory.Exists(dir) || !Directory.EnumerateFileSystemEntries(dir).Any(); }
        private static string Snapshot(string folder) { var d = new DirectoryInfo(folder); return d.Exists ? d.EnumerateFileSystemInfos().Count() + "|" + d.LastWriteTimeUtc.Ticks : "missing"; }
        private static LoaderFetchException Fails(Action action)
        {
            try { action(); }
            catch (LoaderFetchException ex) { return ex; }
            throw new InvalidOperationException("expected the fetch to fail");
        }
        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

        // A minimal HTTP/1.1 server on 127.0.0.1 (TcpListener: no URL reservation or admin rights needed).
        internal sealed class LoopbackServer : IDisposable
        {
            internal sealed class Route { internal int Status = 200; internal string Location; internal byte[] Body = new byte[0]; internal bool OmitLength; internal int ChunkDelayMs, HangMs; }
            internal readonly Dictionary<string, Route> Routes = new Dictionary<string, Route>();
            internal readonly List<string> Log = new List<string>();
            internal int Requests;
            private readonly TcpListener listener;
            private readonly Thread thread;
            private volatile bool stopping;
            internal string Base { get { return "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port; } }
            internal LoopbackServer()
            {
                listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
                thread = new Thread(Accept) { IsBackground = true, Name = "loader-check server" }; thread.Start();
            }
            internal static int ClosedPort() { var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); int port = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return port; }
            // 1.0.21: the offline cases of the other checks: a closed port that SafeHttp refuses at once with the message of a real
            // refused connection (SafeHttp.RefusedLoopbackPort; the caller sets it back to 0). The loader's "failures" check keeps
            // one real closed-port connect, so the Windows error mapping stays tested.
            internal static int RefusedPort() { int port = ClosedPort(); SafeHttp.RefusedLoopbackPort = port; return port; }
            private void Accept()
            {
                while (!stopping)
                {
                    TcpClient client;
                    try { client = listener.AcceptTcpClient(); } catch (SocketException) { return; } catch (ObjectDisposedException) { return; } catch (InvalidOperationException) { return; }
                    ThreadPool.QueueUserWorkItem(delegate { Serve(client); });
                }
            }
            private void Serve(TcpClient client)
            {
                using (client)
                {
                    try
                    {
                        NetworkStream stream = client.GetStream();
                        string head = ReadHead(stream);
                        string[] first = head.Split('\n')[0].Trim().Split(' ');
                        string path = first.Length > 1 ? first[1] : "/";
                        Route route; lock (Routes) Routes.TryGetValue(path, out route);
                        int status = route == null ? 404 : route.Status;
                        lock (Log) { Requests++; Log.Add(first[0] + " " + path + " " + status); }
                        if (route != null && route.HangMs > 0) { Thread.Sleep(route.HangMs); return; }
                        byte[] body = route == null ? Encoding.ASCII.GetBytes("Not Found") : route.Body;
                        var text = new StringBuilder("HTTP/1.1 " + status + " " + (status == 200 ? "OK" : status == 404 ? "Not Found" : status == 301 ? "Moved Permanently" : status == 302 ? "Found" : "Status") + "\r\n");
                        if (route == null || !route.OmitLength) text.Append("Content-Length: " + body.Length + "\r\n");
                        if (route != null && route.Location != null) text.Append("Location: " + route.Location + "\r\n");
                        text.Append("Connection: close\r\n\r\n");
                        byte[] header = Encoding.ASCII.GetBytes(text.ToString());
                        stream.Write(header, 0, header.Length);
                        for (int offset = 0; offset < body.Length; offset += 16384)
                        {
                            stream.Write(body, offset, Math.Min(16384, body.Length - offset));
                            if (route != null && route.ChunkDelayMs > 0) Thread.Sleep(route.ChunkDelayMs);
                        }
                        stream.Flush();
                        client.Client.Shutdown(SocketShutdown.Send);
                    }
                    catch (IOException) { } catch (SocketException) { } catch (ObjectDisposedException) { }
                }
            }
            private static string ReadHead(NetworkStream stream)
            {
                var bytes = new List<byte>(); int b;
                while ((b = stream.ReadByte()) >= 0)
                {
                    bytes.Add((byte)b);
                    int n = bytes.Count;
                    if (n >= 4 && bytes[n - 4] == '\r' && bytes[n - 3] == '\n' && bytes[n - 2] == '\r' && bytes[n - 1] == '\n') break;
                }
                return Encoding.ASCII.GetString(bytes.ToArray());
            }
            public void Dispose() { stopping = true; try { listener.Stop(); } catch (SocketException) { } }
        }
    }
}
