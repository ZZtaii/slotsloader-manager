using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace SlotsInstaller
{
    // 2.4.0 --updates-check <new dir> (test.ps1 suite "updates"): the update check, toasts, SlotsLoader update in
    // place and the Manager self-update against a loopback GitHub-like server (no real network). The
    // self-update runs only on a temporary copy of the app folder.
    internal static class UpdateChecks
    {
        // The newer Manager the fixture catalogue offers: the running version + 1 (patch), so a version bump never breaks the check.
        private static readonly Version NextVersion = new Version(UpdateCheck.Running.Major, UpdateCheck.Running.Minor, Math.Max(0, UpdateCheck.Running.Build) + 1, 0);
        private static readonly string Next = NextVersion.Major + "." + NextVersion.Minor + "." + NextVersion.Build;
        private const BindingFlags Any = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
        private static void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
        private static byte[] B(string s) { return Encoding.UTF8.GetBytes(s); }
        private static string Sha(byte[] b) { using (var sha = System.Security.Cryptography.SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(b)).Replace("-", ""); }

        // A SlotsLoader-shaped zip whose modinfo.ini says the version.
        internal static byte[] LoaderZip(string version)
        {
            using (var memory = new MemoryStream())
            {
                using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
                {
                    Action<string, string> add = delegate(string name, string text) { using (var w = new StreamWriter(zip.CreateEntry(name).Open())) w.Write(text); };
                    add("modinfo.ini", "name=SF6 Slots Loader\r\nversion=" + version + "-custom\r\nauthor=Fixture\r\nNameAsBundle=SF6 Slots Loader\r\n");
                    add("amd_ags_x64.dll", "fixture loader proxy " + version + new string('x', 2048));
                    add("amd_ags_x64_real.dll", "genuine AMD AGS library");
                    add("reframework/plugins/SF6_CostumeSlotsNative.dll", "fixture plugin " + version);
                    add("reframework/data/SF6_Costumes_Data/loader/version.txt", version);
                }
                return memory.ToArray();
            }
        }

        private sealed class Asset { internal string Name; internal byte[] Body; internal bool Digest = true; }
        private static Dictionary<string, object> Release(LoaderChecks.LoopbackServer server, string tag, bool pre, bool draft, params Asset[] assets)
        {
            var list = new List<object>();
            foreach (Asset a in assets)
            {
                string path = "/dl/" + tag + "/" + a.Name;
                server.Routes[path] = new LoaderChecks.LoopbackServer.Route { Body = a.Body };
                var d = new Dictionary<string, object> { { "name", a.Name }, { "size", a.Body.Length }, { "browser_download_url", server.Base + path } };
                if (a.Digest) d["digest"] = "sha256:" + Sha(a.Body).ToLowerInvariant();
                list.Add(d);
            }
            return new Dictionary<string, object> { { "tag_name", tag }, { "name", tag }, { "draft", draft }, { "prerelease", pre }, { "html_url", "https://github.com/" + LoaderPin.Repo + "/releases/tag/" + tag }, { "assets", list.ToArray() } };
        }
        private static void Publish(LoaderChecks.LoopbackServer server, params Dictionary<string, object>[] releases)
        {
            server.Routes["/releases"] = new LoaderChecks.LoopbackServer.Route { Body = B(new JavaScriptSerializer().Serialize(releases)) };
        }

        internal static int Run(string root)
        {
            root = Path.GetFullPath(root);
            if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any()) throw new IOException("Choose a new folder for the update checks.");
            Directory.CreateDirectory(root);
            var report = new List<string>(); int failures = 0;
            Action<string, Action> check = delegate(string name, Action body)
            {
                var clock = Stopwatch.StartNew();
                try { body(); report.Add("PASS " + name + " (" + clock.ElapsedMilliseconds + " ms)"); }
                catch (Exception ex) { failures++; report.Add("FAIL " + name + ": " + ex.Message); Program.Log(ex); }
            };
            string previousApi = UpdateCheck.ApiUrl; bool previousHttp = SafeHttp.AllowLoopbackHttp;
            string settingsFile = UpdateSettings.FilePath; byte[] savedSettings = File.Exists(settingsFile) ? File.ReadAllBytes(settingsFile) : null;
            using (var server = new LoaderChecks.LoopbackServer())
            try
            {
                SafeHttp.AllowLoopbackHttp = true; UpdateCheck.ApiUrl = server.Base + "/releases";
                // The fixture catalogue is about loaders 1.9.6 to 2.0.0: pin 1.9.7 for these checks, whatever the build pins.
                UpdateCheck.PinOverride = LoaderPin.FromJson("{\"tag\":\"loader-v1.9.7\",\"version\":\"1.9.7\",\"asset\":\"SF6_SlotsLoader_v1.9.7.zip\",\"size\":1,\"sha256\":\"" + new string('A', 64) + "\"}");
                byte[] l197 = LoaderZip("1.9.7"), l198 = LoaderZip("1.9.8"), l199 = LoaderZip("1.9.9"), l200 = LoaderZip("2.0.0"), l196 = LoaderZip("1.9.6");
                byte[] portable = B("not a real zip"), other = B("other zip");
                string sums = Sha(portable) + "  Slotsloader-Manager-" + Next + "-portable.zip\n" + Sha(other) + "  extras.zip\n";
                Func<Dictionary<string, object>[]> catalogue = delegate
                {
                    return new[] {
                        Release(server, "loader-v1.9.7", false, false, new Asset { Name = "SF6_SlotsLoader_v1.9.7.zip", Body = l197 }),
                        Release(server, "loader-v1.9.8", true, false, new Asset { Name = "SF6_SlotsLoader_v1.9.8.zip", Body = l198 }),
                        Release(server, "loader-v1.9.9", false, true, new Asset { Name = "SF6_SlotsLoader_v1.9.9.zip", Body = l199 }),
                        Release(server, "loader-v2.0.0", false, false, new Asset { Name = "SF6_SlotsLoader_v2.0.0.zip", Body = l200, Digest = false }),
                        Release(server, "manager-v" + Next, false, false, new Asset { Name = "Slotsloader-Manager-" + Next + "-portable.zip", Body = portable, Digest = false }, new Asset { Name = "extras.zip", Body = other, Digest = false }, new Asset { Name = "SHA256SUMS.txt", Body = B(sums), Digest = false }),
                        Release(server, "manager-v9.0.0", false, true, new Asset { Name = "Slotsloader-Manager-9.0.0-portable.zip", Body = portable }),
                        Release(server, "installer-v2.0.0", false, false, new Asset { Name = "SF6-SlotsLoader-Installer-2.0.0-portable.zip", Body = other }),
                        Release(server, "something-else-v5", false, false, new Asset { Name = "x.zip", Body = other })
                    };
                };
                Publish(server, catalogue());

                check("releases: highest tagged version per product; drafts skipped; pre-releases only with the setting; a release without a verifiable checksum (no digest, no SHA256SUMS.txt) refused and logged; the Manager's *-portable.zip chosen and verified through SHA256SUMS.txt", delegate
                {
                    UpdateCheckResult off = UpdateCheck.Check(false), on = UpdateCheck.Check(true);
                    Require(off.Ok && off.Loader != null && off.Loader.Version == "1.9.7" && !off.Loader.Prerelease && off.Loader.DigestSource == "GitHub digest", "pre-releases off: " + (off.Loader == null ? "none" : off.Loader.Tag));
                    Require(on.Loader != null && on.Loader.Version == "1.9.8" && on.Loader.Prerelease && on.Loader.Sha256 == Sha(l198), "pre-releases on: " + (on.Loader == null ? "none" : on.Loader.Tag));
                    Require(off.Refused.Any(r => r.StartsWith("loader-v2.0.0: ") && r.Contains("no SHA-256 digest and the release has no SHA256SUMS.txt")), "refused: " + String.Join("; ", off.Refused));
                    string ignoredProduct, ignoredVersion;   // 1.0.0: a private-build tag is never a Manager release
                    Require(!UpdateCheck.ProductOf("installer-v2.0.0", out ignoredProduct, out ignoredVersion) && UpdateCheck.ProductOf("manager-v1.0.0", out ignoredProduct, out ignoredVersion) && ignoredProduct == UpdateProducts.Manager, "installer-v* tags must be ignored");
                    Require(off.Manager != null && off.Manager.Version == Next && off.Manager.Asset == "Slotsloader-Manager-" + Next + "-portable.zip" && off.Manager.DigestSource == "SHA256SUMS.txt" && off.Manager.Sha256 == Sha(portable), "manager: " + (off.Manager == null ? "none" : off.Manager.Tag + " " + off.Manager.Asset));
                    string log = File.ReadAllText(Path.Combine(AppConfig.DataDir, "log.txt"));
                    Require(log.Contains("Update check: refused loader-v2.0.0"), "the refusal was not logged");
                    report.Add("  pre off: loader " + off.Loader.Version + ", manager " + off.Manager.Version + " (" + off.Manager.DigestSource + "); pre on: loader " + on.Loader.Version + "; refused: " + String.Join("; ", off.Refused));
                });
                check("what is offered: Slotsloader only when newer than the recorded version and never below the pin (1.9.7); the Manager only when newer than the running build; same and older versions are not offered", delegate
                {
                    UpdateCheckResult on = UpdateCheck.Check(true);
                    Require(UpdateCheck.LoaderUpdate(on, "1.9.7-custom-game-derived").Version == "1.9.8" && UpdateCheck.LoaderUpdate(on, "1.9.8") == null && UpdateCheck.LoaderUpdate(on, "1.9.9") == null && UpdateCheck.LoaderUpdate(on, null) == null, "loader offers");
                    var low = new UpdateCheckResult { Ok = true, Loader = new ReleaseOffer { Version = "1.9.6" } };
                    Require(UpdateCheck.LoaderUpdate(low, "1.9.5") == null && UpdateCheck.InstallPin(low) == UpdateCheck.PinOverride, "below the pin");
                    Require(UpdateCheck.InstallPin(on).Version == "1.9.8" && UpdateCheck.InstallPin(new UpdateCheckResult()) == UpdateCheck.PinOverride, "install link: newest known release, else the pin");
                    Require(UpdateCheck.ManagerUpdate(on, new Version(0, 1, 0, 0)).Version == Next && UpdateCheck.ManagerUpdate(on, NextVersion) == null && UpdateCheck.ManagerUpdate(on, new Version(UpdateCheck.Running.Major, UpdateCheck.Running.Minor + 1, 0, 0)) == null, "manager offers");
                });
                check("a 404 (private repository) and an offline server give a reason and no exception; the 24 h throttle", delegate
                {
                    UpdateCheck.ApiUrl = server.Base + "/missing";
                    UpdateCheckResult r404 = UpdateCheck.Check(false);
                    Require(!r404.Ok && r404.Status == 404 && r404.Reason.StartsWith("HTTP 404"), "404: " + r404.Reason);
                    UpdateCheck.ApiUrl = "http://127.0.0.1:" + LoaderChecks.LoopbackServer.ClosedPort() + "/releases";
                    UpdateCheckResult offline = UpdateCheck.Check(false);
                    Require(!offline.Ok && offline.Reason == "could not connect to 127.0.0.1", "offline: " + offline.Reason);
                    UpdateCheck.ApiUrl = server.Base + "/releases";
                    var s = new UpdateSettings { LastCheckUtc = DateTime.UtcNow.AddHours(-2).ToString("o") };
                    Require(!s.Due(DateTime.UtcNow), "checked 2 h ago is not due");
                    s.LastCheckUtc = DateTime.UtcNow.AddHours(-25).ToString("o");
                    Require(s.Due(DateTime.UtcNow) && new UpdateSettings().Due(DateTime.UtcNow), "25 h ago / never must be due");
                });
                check("downloads are verified: a wrong published digest is refused and nothing is kept; a correct one lands in Data\\downloads", delegate
                {
                    string dir = Path.Combine(root, "dl");
                    DownloadFailure failure = null;
                    try { SafeHttp.Download(server.Base + "/dl/loader-v1.9.8/SF6_SlotsLoader_v1.9.8.zip", dir, "x.zip", l198.Length, Sha(l197), "Downloading", "published"); } catch (DownloadFailure f) { failure = f; }
                    Require(failure != null && failure.Reason.Contains("does not match the published build") && !Directory.EnumerateFiles(dir).Any(), "wrong digest: " + (failure == null ? "accepted" : failure.Reason));
                    string ok = SafeHttp.Download(server.Base + "/dl/loader-v1.9.8/SF6_SlotsLoader_v1.9.8.zip", dir, "x.zip", l198.Length, Sha(l198), "Downloading", "published");
                    Require(File.ReadAllBytes(ok).SequenceEqual(l198), "good download");
                });
                check("Manager self-update on a temporary copy of the app folder: stage (7 files, FileVersion = tag), apply (previous kept, running exes renamed to .old, new files in), .old cleanup and log at the next start, update-rollback, a failure mid-copy restores every file, a missing folder is refused; nothing else in Data changes", delegate { SelfUpdate(root); });
                check("window: toasts for both products (bottom-right above the status strip, stacked), keyboard reachable with accessible names, no tooltips; Skip this version is remembered; ✕ comes back at the next check; a 404 background check is silent and logged; Settings → Updates reports up to date and the 404 reason", delegate { Window(root, server, catalogue, l197, l198); });
                check("CLI update-check prints both products with source, asset, size and digest", delegate
                {
                    string text = ReplaceChecks.Cli("update-check", "--include-prereleases");
                    Require(text.StartsWith("0\n") && text.Contains("Slotsloader Manager installed: " + SafeHttp.AppVersion() + " · latest: " + Next) && text.Contains("SHA-256 " + Sha(l198)) && text.Contains("Source: " + server.Base + "/releases"), "update-check: " + text);
                    report.Add("  " + String.Join(" | ", text.Split('\n').Where(l => l.StartsWith("Slotsloader")).Select(l => l.Trim())));
                });
            }
            finally
            {
                UpdateCheck.ApiUrl = previousApi; SafeHttp.AllowLoopbackHttp = previousHttp; UpdateCheck.PinOverride = null; MainForm.AppDirOverride = null; MainForm.NoRelaunch = false; AppUpdater.FailAfterCopies = -1;
                if (savedSettings != null) File.WriteAllBytes(settingsFile, savedSettings); else if (File.Exists(settingsFile)) File.Delete(settingsFile);
            }
            File.WriteAllLines(Path.Combine(root, "update-check.txt"), report.Concat(new[] { failures == 0 ? "ALL PASS" : failures + " FAILED" }));
            foreach (string line in report) Console.WriteLine(line);
            Console.WriteLine(failures == 0 ? "ALL PASS" : failures + " FAILED");
            return failures == 0 ? 0 : 1;
        }

        private static Dictionary<string, string> Snapshot(string dir, params string[] skip)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!Directory.Exists(dir)) return d;
            foreach (string f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
            {
                string rel = f.Substring(dir.Length).TrimStart('\\');
                if (skip.Any(s => rel.StartsWith(s, StringComparison.OrdinalIgnoreCase))) continue;
                try { d[rel] = LoaderRelease.HashFile(f); } catch (IOException) { d[rel] = "locked"; }
            }
            return d;
        }

        private static void SelfUpdate(string root)
        {
            string app = Path.Combine(root, "app-copy"), data = Path.Combine(root, "app-copy", "Data");
            Directory.CreateDirectory(data);
            string here = AppDomain.CurrentDomain.BaseDirectory;
            foreach (string f in AppUpdater.ReleaseFiles)
            {
                string from = Path.Combine(here, f);
                if (File.Exists(from)) File.Copy(from, Path.Combine(app, f)); else File.WriteAllText(Path.Combine(app, f), "old " + f);
            }
            File.WriteAllText(Path.Combine(data, "settings.json"), "{}"); File.WriteAllText(Path.Combine(data, "replace-state.json"), "{\"keep\":true}");
            // 2.5.9: the installed copy bundles an older SlotsLoader zip; the release bundles a newer one.
            string bundle = Path.Combine(app, LoaderRelease.BundleFolder); Directory.CreateDirectory(bundle);
            File.WriteAllText(Path.Combine(bundle, "SF6_SlotsLoader_v1.9.7.zip"), "old loader");
            var before = Snapshot(app);
            // The release: the same exe (FileVersion of this build), new docs.
            string version = UpdateCheck.Running.ToString(3), zip = Path.Combine(root, "Slotsloader-Manager-" + version + "-portable.zip");
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
                foreach (string f in AppUpdater.ReleaseFiles)
                {
                    if (f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".config", StringComparison.OrdinalIgnoreCase)) archive.CreateEntryFromFile(Path.Combine(app, f), f);
                    else using (var w = new StreamWriter(archive.CreateEntry(f).Open())) w.Write("new " + f);
                }
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Update)) using (var w = new StreamWriter(archive.CreateEntry(LoaderRelease.BundleFolder + "/SF6_SlotsLoader_v1.10.0.zip").Open())) w.Write("new loader");
            // A second bundled zip, or one in a deeper folder, is refused.
            Func<string, string> refused = delegate(string extra)
            {
                string bad = Path.Combine(root, "bad-" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".zip"); File.Copy(zip, bad);
                using (var archive = ZipFile.Open(bad, ZipArchiveMode.Update)) using (var w = new StreamWriter(archive.CreateEntry(extra).Open())) w.Write("x");
                try { AppUpdater.Stage(bad, version, data); return null; } catch (IOException ex) { return ex.Message; }
            };
            string twoLoaders = refused(LoaderRelease.BundleFolder + "/other.zip"), deeper = refused(LoaderRelease.BundleFolder + "/sub/x.zip"), notZip = refused(LoaderRelease.BundleFolder + "/x.dll");
            Require(twoLoaders != null && twoLoaders.Contains("more than one bundled Slotsloader zip") && deeper != null && deeper.Contains("unexpected files") && notZip != null && notZip.Contains("unexpected files"), "bundled zip rules: " + twoLoaders + " | " + deeper + " | " + notZip);
            Exception wrong = null;
            try { AppUpdater.Stage(zip, "9.9.9", data); } catch (IOException ex) { wrong = ex; }
            Require(wrong != null && wrong.Message.Contains("not 9.9.9") && !Directory.Exists(Path.Combine(AppUpdater.UpdatesDir(data), "9.9.9")), "a FileVersion that does not match the tag must be refused: " + (wrong == null ? "accepted" : wrong.Message));
            string staged = AppUpdater.Stage(zip, version, data);
            Require(AppUpdater.ReleaseFiles.All(f => File.Exists(Path.Combine(staged, f))) && File.Exists(Path.Combine(staged, LoaderRelease.BundleFolder, "SF6_SlotsLoader_v1.10.0.zip")), "staged files");
            // A failure after 3 copied files puts everything back.
            AppUpdater.FailAfterCopies = 3;
            Exception mid = null; try { AppUpdater.Apply(app, staged, AppUpdater.PreviousDir(data), "old", version, data); } catch (IOException ex) { mid = ex; }
            AppUpdater.FailAfterCopies = -1;
            var afterFailure = Snapshot(app, "Data\\updates");
            Require(mid != null && before.All(kv => afterFailure.ContainsKey(kv.Key) && afterFailure[kv.Key] == kv.Value) && !Directory.GetFiles(app, "*.old").Any(), "a failed update left changes: " + String.Join(", ", before.Where(kv => !afterFailure.ContainsKey(kv.Key) || afterFailure[kv.Key] != kv.Value).Select(kv => kv.Key)));
            // A folder that cannot be written is refused without changes.
            Exception notWritable = null; try { AppUpdater.Apply(Path.Combine(root, "missing-app"), staged, null, "old", version, data); } catch (IOException ex) { notWritable = ex; }
            Require(notWritable != null && notWritable.Message.Contains("is not writable") && !Directory.Exists(Path.Combine(root, "missing-app")), "unwritable folder: " + (notWritable == null ? "accepted" : notWritable.Message));
            // The real apply.
            AppUpdater.Apply(app, staged, AppUpdater.PreviousDir(data), "old", version, data);
            Require(File.ReadAllText(Path.Combine(app, "README.md")) == "new README.md" && File.Exists(Path.Combine(app, "Slotsloader Manager.exe.old")) && File.Exists(Path.Combine(app, "Slotsloader CLI.exe.old")), "apply: new files and .old exes");
            Require(AppUpdater.ReleaseFiles.All(f => File.Exists(Path.Combine(AppUpdater.PreviousDir(data), f))) && LoaderRelease.HashFile(Path.Combine(AppUpdater.PreviousDir(data), "README.md")) == before["README.md"], "the previous version was not kept");
            Require(Directory.GetFiles(bundle).Select(Path.GetFileName).SequenceEqual(new[] { "SF6_SlotsLoader_v1.10.0.zip" }) && File.ReadAllText(Path.Combine(bundle, "SF6_SlotsLoader_v1.10.0.zip")) == "new loader"
                && File.ReadAllText(Path.Combine(AppUpdater.PreviousDir(data), LoaderRelease.BundleFolder, "SF6_SlotsLoader_v1.9.7.zip")) == "old loader", "bundled loader: the new zip replaces the old one, which is kept for rollback: " + String.Join(", ", Directory.GetFiles(bundle).Select(Path.GetFileName)));
            Require(File.ReadAllText(Path.Combine(data, "settings.json")) == "{}" && File.ReadAllText(Path.Combine(data, "replace-state.json")) == "{\"keep\":true}", "other Data content changed");
            // Next start: .old removed, logged once.
            string line = AppUpdater.CleanupAfterUpdate(app, data);
            Require(line != null && line.Contains("old -> " + version) && !Directory.GetFiles(app, "*.old").Any() && AppUpdater.CleanupAfterUpdate(app, data) == null, "cleanup at the next start: " + line);
            // Rollback restores the kept files exactly.
            AppUpdater.Rollback(app, data);
            var afterRollback = Snapshot(app, "Data");
            Require(before.Where(kv => !kv.Key.StartsWith("Data")).All(kv => afterRollback[kv.Key] == kv.Value), "rollback did not restore the previous files");
            AppUpdater.CleanupAfterUpdate(app, data);
        }

        private static void Window(string root, LoaderChecks.LoopbackServer server, Func<Dictionary<string, object>[]> catalogue, byte[] l197, byte[] l198)
        {
            ReplaceChecks.Fixture f = ReplaceChecks.Create(Path.Combine(root, "window"), true, ReplaceMethods.Loose);
            File.Delete(Path.Combine(f.Game, "re_chunk_000.pak.patch_001.pak"));
            var config = new AppConfig { GameRoot = f.Game, Libraries = new List<string>(), SevenZipPath = "" };
            string loader197 = Path.Combine(root, "SF6_SlotsLoader_v1.9.7.zip"); File.WriteAllBytes(loader197, l197);
            var probe = new FakeProcessProbe(); IProcessProbe previousProbe = GameProcesses.Probe; GameProcesses.Probe = probe;
            ReplaceEngine engine = ReplaceEngine.ForConfig(config);
            string ignored; foreach (ReplaceMod m in engine.LoadState().Mods.ToList()) engine.Uninstall(m.Id, false, out ignored);
            SynchronizationContext previous = SynchronizationContext.Current; bool previousAuto = WindowsFormsSynchronizationContext.AutoInstall; WindowsFormsSynchronizationContext.AutoInstall = false;
            var asked = new List<string>(); DialogResult answer = DialogResult.Yes; string opened = null;
            ThemedMessageBox.TestResponder = delegate(ThemedMessageForm box) { asked.Add(box.Caption + "|" + box.MessageText); return answer; };
            MainForm.OpenUrlHook = delegate(string url) { opened = url; return true; };
            using (var ui = new WindowsFormsSynchronizationContext())
            try
            {
                SynchronizationContext.SetSynchronizationContext(ui);
                // SlotsLoader 1.9.7 installed by this app (its record's source names the version in modinfo.ini).
                ModPackage package = PackageReader.Load(loader197, "");
                CompositionPlan plan = Composer.Plan(package, Composer.DefaultSelection(package, true), true);
                ReplacePlan prepared; ReplaceMod installed = ReplacePlanner.PrepareAndInstall(plan, "", engine, "SF6 Slots Loader", false, null, out prepared);
                string source; Require(InstalledLoader.Version(installed, out source) == "1.9.7-custom" && source == "modinfo.ini", "installed version: " + source);
                string proxy197 = File.ReadAllText(Path.Combine(f.Game, "amd_ags_x64.dll"));
                var settings = new UpdateSettings { IncludePrereleases = true }; settings.Save();
                using (var form = new MainForm(config))
                {
                    typeof(MainForm).GetField("firstLoad", Any).SetValue(form, false);
                    form.ShowInTaskbar = false; form.Opacity = 0; form.MinimumSize = Size.Empty; form.Show();
                    form.ClientSize = new Size(DpiMetrics.Px(form, 1232), DpiMetrics.Px(form, 640)); Application.DoEvents();
                    Action<Func<bool>, string> wait = delegate(Func<bool> done, string what) { DateTime end = DateTime.UtcNow.AddSeconds(60); while (!done() && DateTime.UtcNow < end) { Application.DoEvents(); Thread.Sleep(10); } Require(done(), what + " did not finish"); Application.DoEvents(); };
                    // Background check (pre-releases on): both toasts.
                    int runs = form.UpdateChecksRun; form.RunUpdateCheck(false); wait(() => form.UpdateChecksRun > runs && !form.Working, "the background check");
                    Panel loaderToast = form.UpdateToast(UpdateProducts.Loader), managerToast = form.UpdateToast(UpdateProducts.Manager);
                    Require(loaderToast != null && managerToast != null && loaderToast.AccessibleName == "Slotsloader 1.9.8 is available (you have 1.9.7) · test build" && managerToast.AccessibleName.StartsWith("Slotsloader Manager " + Next + " is available"), "toasts: " + (loaderToast == null ? "no loader toast" : loaderToast.AccessibleName) + " / " + (managerToast == null ? "no manager toast" : managerToast.AccessibleName));
                    var buttons = loaderToast.Controls.OfType<FlowLayoutPanel>().Single().Controls.OfType<FlatButton>().ToList();
                    Require(buttons.Select(b => b.Text).SequenceEqual(new[] { "Update", "What's new", "Skip this version", "✕" }) && buttons.All(b => b.TabStop && b.CanFocus && !String.IsNullOrEmpty(b.AccessibleName)) && buttons[3].AccessibleName.StartsWith("Dismiss"), "toast buttons: " + String.Join(", ", buttons.Select(b => b.Text + "=" + b.AccessibleName)));
                    Panel host = (Panel)loaderToast.Parent; Panel status = (Panel)typeof(MainForm).GetField("statusPanel", Any).GetValue(form);
                    Rectangle hb = host.Bounds;
                    Require(hb.Bottom <= status.Top && hb.Right <= form.ClientSize.Width && hb.Right >= form.ClientSize.Width - DpiMetrics.Px(form, 40) && host.Controls.Count == 2 && form.Controls.GetChildIndex(host) == 0, "toast placement " + hb + " status top " + status.Top);
                    Require(UiAudit.ToolTipFindings(form).Count == 0, "tooltips: " + String.Join("; ", UiAudit.ToolTipFindings(form)));
                    buttons[0].Focus(); Application.DoEvents(); Require(buttons[0].Focused, "the Update button cannot take the keyboard focus");
                    buttons[1].PerformClick(); Require(opened == "https://github.com/" + LoaderPin.Repo + "/releases/tag/loader-v1.9.8", "What's new: " + opened);
                    Render(form, Path.Combine(root, "toasts-stacked.png"));
                    // SF6 running: the update waits and says so.
                    probe.Game = true; typeof(MainForm).GetMethod("PollGame", Any).Invoke(form, null); Application.DoEvents();
                    int requests = server.Requests; buttons[0].PerformClick(); Application.DoEvents();
                    Label toastText = loaderToast.Controls.Find("toastText", true).OfType<Label>().First();
                    Require(toastText.Text == "Close SF6 to update Slotsloader to 1.9.8." && server.Requests == requests && form.UpdateToast(UpdateProducts.Loader) != null, "while SF6 runs: " + toastText.Text);
                    probe.Game = false; typeof(MainForm).GetMethod("PollGame", Any).Invoke(form, null); Application.DoEvents();
                    // Update in place: download + verify, health check, no question (2.4.1), the same record updated.
                    asked.Clear(); answer = DialogResult.Yes;
                    buttons[0].PerformClick();
                    Func<string> loaded = delegate { var p = (ModPackage)typeof(MainForm).GetField("package", Any).GetValue(form); return p == null ? "" : p.SourcePath; };
                    wait(() => form.UpdateToast(UpdateProducts.Loader) == null || asked.Any(a => a.StartsWith("Update Slotsloader|")) || (!form.Working && loaded().EndsWith("SF6_SlotsLoader_v1.9.8.zip") && typeof(MainForm).GetField("replacePlan", Any).GetValue(form) != null), "the Slotsloader update");
                    wait(() => !form.Working, "the in-place install");
                    string afterReview = ((Label)typeof(MainForm).GetField("statusText", Any).GetValue(form)).Text;
                    if (form.UpdateToast(UpdateProducts.Loader) != null) { ReplacePlan health = (ReplacePlan)typeof(MainForm).GetField("replacePlan", Any).GetValue(form); Program.LogLine("updates check: the health check had warnings (" + afterReview + "): " + String.Join(" | ", health.Items.Where(i => i.Level == "warn" || i.Level == "error").Select(i => i.Text)) + "; Install replacement"); typeof(MainForm).GetMethod("InstallReplacement", Any).Invoke(form, null); wait(() => !form.Working, "the install after the report"); }
                    ReplaceState state = engine.LoadState();
                    ReplaceMod record = InstalledLoader.Record(state);
                    Require(state.Mods.Count == 1 && state.Mods[0] == record && state.Find(installed.Id) == null && InstalledLoader.Version(record, out source) == "1.9.8-custom", "update in place: " + state.Mods.Count + " mods, version " + InstalledLoader.Version(record, out source));
                    Require(File.ReadAllText(Path.Combine(f.Game, "amd_ags_x64.dll")) != proxy197 && state.Originals.Any(o => o.Path.Equals("amd_ags_x64.dll", StringComparison.OrdinalIgnoreCase)), "the proxy was not updated or the genuine backup is gone");
                    Require(asked.Count == 0, "2.4.1: the toast's Update click is the consent, no question: " + String.Join(" / ", asked));
                    // Skip this version / dismiss on the Manager toast.
                    var mbuttons = managerToast.Controls.OfType<FlowLayoutPanel>().Single().Controls.OfType<FlatButton>().ToList();
                    mbuttons[3].PerformClick(); Application.DoEvents();
                    Require(form.UpdateToast(UpdateProducts.Manager) == null && UpdateSettings.Load().DismissedManager == Next, "dismiss");
                    runs = form.UpdateChecksRun; form.RunUpdateCheck(false); wait(() => form.UpdateChecksRun > runs, "the next check");
                    Require(form.UpdateToast(UpdateProducts.Manager) != null, "a dismissed update must come back at the next check");
                    form.UpdateToast(UpdateProducts.Manager).Controls.OfType<FlowLayoutPanel>().Single().Controls.OfType<FlatButton>().ToList()[2].PerformClick(); Application.DoEvents();
                    runs = form.UpdateChecksRun; form.RunUpdateCheck(false); wait(() => form.UpdateChecksRun > runs, "the check after Skip");
                    Require(form.UpdateToast(UpdateProducts.Manager) == null && UpdateSettings.Load().SkipManager == Next, "Skip this version is not remembered");
                    // Background 404: silent, logged.
                    UpdateCheck.ApiUrl = server.Base + "/missing"; asked.Clear();
                    runs = form.UpdateChecksRun; form.RunUpdateCheck(false); wait(() => form.UpdateChecksRun > runs, "the 404 check");
                    Require(asked.Count == 0 && File.ReadAllText(Path.Combine(AppConfig.DataDir, "log.txt")).Contains("Update check: HTTP 404"), "a 404 must be silent and logged");
                    // Settings → Updates: the 404 reason, then (with no newer release) one line "Up to date · Slotsloader 1.9.8 · Manager 2.4.0".
                    typeof(MainForm).GetMethod("ShowPage", Any).Invoke(form, new object[] { typeof(MainForm).GetField("settingsPage", Any).GetValue(form) });
                    typeof(MainForm).GetMethod("ShowUpdatesTab", Any).Invoke(form, null); Application.DoEvents();
                    runs = form.UpdateChecksRun; form.RunUpdateCheck(true); wait(() => form.UpdateChecksRun > runs, "the manual 404 check");
                    Require(form.LastUpdateMessage.StartsWith("Couldn't check for updates: HTTP 404"), "manual 404: " + form.LastUpdateMessage);
                    UpdateCheck.ApiUrl = server.Base + "/releases";
                    Publish(server, catalogue().Where(r => !((string)r["tag_name"]).StartsWith("manager-v" + Next)).ToArray());
                    runs = form.UpdateChecksRun; form.RunUpdateCheck(true); wait(() => form.UpdateChecksRun > runs, "the manual check");
                    string upToDate = "Up to date · Slotsloader 1.9.8 · Manager " + UpdateCheck.ShortVersion(SafeHttp.AppVersion());
                    Require(form.LastUpdateMessage == upToDate + "\nSkipped loader-v2.0.0: no checksum", "up to date: " + form.LastUpdateMessage);
                    Label result = (Label)typeof(MainForm).GetField("settingsUpdateResult", Any).GetValue(form);
                    Label skipped = (Label)typeof(MainForm).GetField("settingsUpdateSkipped", Any).GetValue(form);
                    Require(result.Text == upToDate && result.Visible && skipped.Text == "Skipped loader-v2.0.0: no checksum" && skipped.ForeColor == Palette.Muted, "the Settings result: " + result.Text + " / " + skipped.Text);
                    Require(File.ReadAllText(Path.Combine(AppConfig.DataDir, "log.txt")).Contains("refused loader-v2.0.0: SF6_SlotsLoader_v2.0.0.zip has no SHA-256 digest"), "the full reason of a skipped release belongs in the log");
                    // 2.4.0 copy and spacing: no heading in the tab, "Check now" is a normal button, a clear gap before the one-line result.
                    FlatButton checkNow = (FlatButton)typeof(MainForm).GetField("settingsCheckUpdates", Any).GetValue(form);
                    int gap = result.PointToScreen(Point.Empty).Y - checkNow.PointToScreen(new Point(0, checkNow.Height)).Y;
                    Require(checkNow.Text == "Check now" && !checkNow.Primary && gap >= DpiMetrics.Px(form, 12) && ((NoteLabel)result).LineCount == 1, "Check now button and result spacing: gap " + gap + " px, result lines " + ((NoteLabel)result).LineCount);
                    Panel updatesPanel = (Panel)typeof(MainForm).GetField("settingsUpdatesPanel", Any).GetValue(form);
                    Require(!AllControls(updatesPanel).OfType<Label>().Any(l => l.Text == "Updates"), "the Updates tab must not repeat its name as a heading");
                    Render(form, Path.Combine(root, "settings-updates-up-to-date.png"));
                    Publish(server, catalogue());
                    form.Close();
                }
                engine.RestoreAll();
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous); WindowsFormsSynchronizationContext.AutoInstall = previousAuto;
                ThemedMessageBox.TestResponder = null; MainForm.OpenUrlHook = null; GameProcesses.Probe = previousProbe;
            }
        }
        private static IEnumerable<Control> AllControls(Control root) { foreach (Control c in root.Controls) { yield return c; foreach (Control d in AllControls(c)) yield return d; } }
        private static void Render(Form form, string file)
        {
            using (var b = new Bitmap(form.ClientSize.Width, form.ClientSize.Height))
            {
                form.DrawToBitmap(b, new Rectangle(Point.Empty, b.Size));
                // DrawToBitmap paints overlapping siblings in reverse z-order: draw the toasts (front-most) on top.
                Control host = form.Controls.OfType<FlowLayoutPanel>().FirstOrDefault(c => c.AccessibleName == "Updates available");
                if (host != null && host.Visible)
                    using (var t = new Bitmap(host.Width, host.Height)) using (Graphics g = Graphics.FromImage(b))
                    {
                        host.DrawToBitmap(t, new Rectangle(Point.Empty, t.Size));
                        Point at = form.PointToClient(host.PointToScreen(Point.Empty));
                        g.DrawImage(t, at.X + (form.Width - form.ClientSize.Width) / 2, at.Y + (form.Height - form.ClientSize.Height) - (form.Width - form.ClientSize.Width) / 2);
                    }
                using (var half = new Bitmap(b, b.Width / 2, b.Height / 2)) half.Save(file, ImageFormat.Png);
            }
        }
    }
}
