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
        // 1.0.19: one more, published without a checksum (refused): the loader-v* releases no longer give a refusal to show.
        private static readonly string After = NextVersion.Major + "." + NextVersion.Minor + "." + (NextVersion.Build + 1);
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

        // 1.0.19: a loader zip with more files (path, text) beside the usual ones.
        internal static byte[] LoaderZip(string version, params string[] extra)
        {
            using (var memory = new MemoryStream())
            {
                using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
                using (var source = new ZipArchive(new MemoryStream(LoaderZip(version)), ZipArchiveMode.Read))
                {
                    foreach (ZipArchiveEntry e in source.Entries) using (Stream from = e.Open()) using (Stream to = zip.CreateEntry(e.FullName).Open()) from.CopyTo(to);
                    for (int i = 0; i + 1 < extra.Length; i += 2) using (var w = new StreamWriter(zip.CreateEntry(extra[i]).Open())) w.Write(extra[i + 1]);
                }
                return memory.ToArray();
            }
        }
        private static ReplaceMod InstallZip(ReplaceEngine engine, string zip, string name)
        {
            ModPackage package = PackageReader.Load(zip, "");
            CompositionPlan plan = Composer.Plan(package, Composer.DefaultSelection(package, true), true);
            ReplacePlan prepared; return ReplacePlanner.PrepareAndInstall(plan, "", engine, name, false, null, out prepared);
        }
        private static string GameText(string game, string rel) { string p = Path.Combine(game, rel.Replace('/', '\\')); return File.Exists(p) ? File.ReadAllText(p) : null; }

        // The maintainer's case: an old test script was at the fork's path before the fork was first installed; the fork
        // (1.10.9) backed it up; the suite line (no such script) replaced the fork's record and the old script came back.
        private static void LoaderSwitch(string root)
        {
            const string lua = "reframework/autorun/SlotsloaderOpponentOutfitSummary.lua", other = "reframework/autorun/other_tool.lua";
            ReplaceChecks.Fixture f = ReplaceChecks.Create(Path.Combine(root, "loader-switch"), true, ReplaceMethods.Loose);
            var config = new AppConfig { GameRoot = f.Game, Libraries = new List<string>(), SevenZipPath = "" };
            var probe = new FakeProcessProbe(); IProcessProbe previousProbe = GameProcesses.Probe; GameProcesses.Probe = probe;
            try
            {
                ReplaceEngine engine = ReplaceEngine.ForConfig(config);
                string ignored; foreach (ReplaceMod m in engine.LoadState().Mods.ToList()) engine.Uninstall(m.Id, false, out ignored);
                string oldScript = "-- an old test script from before the fork " + new string('o', 1700);
                File.WriteAllText(Path.Combine(f.Game, lua.Replace('/', '\\')), oldScript);
                File.WriteAllText(Path.Combine(f.Game, other.Replace('/', '\\')), "the user's own script");
                string genuine = GameText(f.Game, "amd_ags_x64.dll");
                string zips = Path.Combine(root, "loader-switch-zips"); Directory.CreateDirectory(zips);
                string fork = Path.Combine(zips, "SF6_Slotsloader_Fork_v1.10.9.zip"); File.WriteAllBytes(fork, LoaderZip("1.10.9", lua, "-- the fork's summary script"));
                string suite = Path.Combine(zips, "SF6_SlotsLoader_v1.27.7.zip"); File.WriteAllBytes(suite, LoaderZip("1.27.7"));
                ReplaceMod forkRecord = InstallZip(engine, fork, "Slotsloader Fork");
                ReplaceState s = engine.LoadState();
                // 1.0.19 no longer keeps a file at a Slotsloader's own path as an original (the takeover rule). The state a
                // 1.0.18 install left (the old script backed up as the fork's original) is written as it was.
                Require(GameText(f.Game, lua) == "-- the fork's summary script" && !s.Originals.Any(o => o.Path == lua) && s.Originals.Any(o => o.Path == "amd_ags_x64.dll"), "the fork installed over the old script; 1.0.19 keeps no original of it");
                string oldBackup = Path.Combine(root, "loader-switch-1.0.18-backup.bin"); File.WriteAllText(oldBackup, oldScript);
                s.Originals.Add(new OriginalBackup { Path = lua, Backup = oldBackup, Sha256 = ReplaceEngine.Sha(oldBackup), Size = new FileInfo(oldBackup).Length });
                DurableFile.WriteJson(engine.StatePath, s);
                string source; string from = InstalledLoader.Version(forkRecord, out source);
                ReleaseOffer approved = ReleaseOffer.Approved(LoaderPin.FromList(LoaderChecks.ListJson("1.27.7", "v1.40.0", "SF6_SlotsLoader_v1.27.7.zip", 10, new string('d', 64), "", "")));
                Require(from == "1.10.9-custom" && UpdateCheck.LoaderUpdate(approved, from, false) == approved, "the fork is offered 1.27.7: " + from);
                // The suite line replaces the fork's record in one step (the planner finds the Slotsloader record itself).
                ReplaceMod suiteRecord = InstallZip(engine, suite, "SF6 Slots Loader");
                s = engine.LoadState();
                Require(s.Mods.Count == 1 && s.Mods[0].Id == suiteRecord.Id && s.Find(forkRecord.Id) == null && InstalledLoader.Version(suiteRecord, out source) == "1.27.7-custom", "one step, one record: " + s.Mods.Count + " records");
                Require(GameText(f.Game, lua) == null && !s.Originals.Any(o => o.Path == lua), "the old script came back (or its backup stayed): " + (GameText(f.Game, lua) ?? "gone") + " / " + String.Join(", ", s.Originals.Select(o => o.Path)));
                Require(s.Originals.Count(o => o.Path == "amd_ags_x64.dll") == 1 && GameText(f.Game, "amd_ags_x64.dll") != genuine && GameText(f.Game, other) == "the user's own script", "the genuine library must stay backed up and other files alone");
                Require(File.ReadAllText(Path.Combine(AppConfig.DataDir, "log.txt")).Contains("Slotsloader replaced: " + lua + " removed with the old loader"), "the dropped original must be logged");
                // Uninstall: the game's own library back, the plugin gone, the old script not back.
                engine.Uninstall(suiteRecord.Id, false, out ignored);
                s = engine.LoadState();
                Require(GameText(f.Game, "amd_ags_x64.dll") == genuine && GameText(f.Game, LoaderCostumePak.PluginPath) == null && GameText(f.Game, lua) == null && s.Originals.Count == 0, "uninstall after the switch");
                // An ordinary tool mod: its original comes back on uninstall, and when another build without that file replaces it.
                string toolA = Path.Combine(zips, "Other Tool v1.zip"), toolB = Path.Combine(zips, "Other Tool v2.zip");
                WriteZip(toolA, other, "tool v1", "reframework/autorun/other_tool_data.lua", "data v1"); WriteZip(toolB, "reframework/autorun/other_tool_data.lua", "data v2");
                ReplaceMod a = InstallZip(engine, toolA, "Other Tool");
                Require(GameText(f.Game, other) == "tool v1", "the ordinary tool mod installed");
                engine.Uninstall(a.Id, false, out ignored);
                Require(GameText(f.Game, other) == "the user's own script", "an ordinary mod's original must come back on uninstall");
                a = InstallZip(engine, toolA, "Other Tool");
                ModPackage pb = PackageReader.Load(toolB, ""); CompositionPlan cb = Composer.Plan(pb, Composer.DefaultSelection(pb, true), true);
                ReplacePlan prepared; ReplacePlanner.PrepareAndInstall(cb, "", engine, "Other Tool", false, a.Id, out prepared);
                Require(GameText(f.Game, other) == "the user's own script" && GameText(f.Game, "reframework/autorun/other_tool_data.lua") == "data v2", "an ordinary mod replaced by a build without the file must still give the original back");
            }
            finally { CleanState(config); GameProcesses.Probe = previousProbe; }
        }
        private static void WriteZip(string path, params string[] entries)
        {
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
                for (int i = 0; i + 1 < entries.Length; i += 2) using (var w = new StreamWriter(zip.CreateEntry(entries[i]).Open())) w.Write(entries[i + 1]);
        }

        // A Slotsloader copied in by hand: the approved one installed through the Manager takes over.
        private static void Takeover(string root)
        {
            ReplaceChecks.Fixture f = ReplaceChecks.Create(Path.Combine(root, "takeover"), true, ReplaceMethods.Loose);
            var config = new AppConfig { GameRoot = f.Game, Libraries = new List<string>(), SevenZipPath = "" };
            var probe = new FakeProcessProbe(); IProcessProbe previousProbe = GameProcesses.Probe; GameProcesses.Probe = probe;
            try
            {
                ReplaceEngine engine = ReplaceEngine.ForConfig(config);
                string ignored; foreach (ReplaceMod m in engine.LoadState().Mods.ToList()) engine.Uninstall(m.Id, false, out ignored);
                string genuine = GameText(f.Game, "amd_ags_x64.dll");
                // By hand: the proxy over the game's library, the genuine one as amd_ags_x64_real.dll, the plugin and data, the user's costume mods and settings.
                byte[] handZip = LoaderZip("1.27.3");
                using (var zip = new ZipArchive(new MemoryStream(handZip), ZipArchiveMode.Read))
                    foreach (ZipArchiveEntry e in zip.Entries.Where(e => e.Name.Length > 0 && e.Name != "modinfo.ini"))
                    {
                        string to = Path.Combine(f.Game, e.FullName.Replace('/', '\\')); Directory.CreateDirectory(Path.GetDirectoryName(to));
                        using (Stream s = e.Open()) using (var o = File.Create(to)) s.CopyTo(o);
                    }
                File.WriteAllText(Path.Combine(f.Game, "amd_ags_x64_real.dll"), genuine);
                string costume = Path.Combine(f.Game, @"reframework\costume_mods\Cammy\my outfit.zip"), settings = Path.Combine(f.Game, LoaderFlags.PluginSettingsRelative);
                Directory.CreateDirectory(Path.GetDirectoryName(costume)); File.WriteAllText(costume, "the user's costume mod");
                Directory.CreateDirectory(Path.GetDirectoryName(settings)); File.WriteAllText(settings, "{\"menu_outfit_options\": true}");
                // 1.0.19: what a hand-installed Slotsloader Fork leaves that the new loader does not ship: a file with a known
                // fork file's exact bytes goes (a copy is kept); the same path edited by the user, and other files, stay.
                const string forkLua = "reframework/autorun/SlotsloaderOpponentOutfitSummary.lua", forkDoc = "reframework/costume_mods/README.txt", userLua = "reframework/autorun/my_script.lua";
                string forkText = "-- the fork's summary script (fixture)", editedDoc = "the fork's README, edited by the user";
                File.WriteAllText(Path.Combine(f.Game, forkLua.Replace('/', '\\')), forkText); File.WriteAllText(Path.Combine(f.Game, forkDoc.Replace('/', '\\')), editedDoc); File.WriteAllText(Path.Combine(f.Game, userLua.Replace('/', '\\')), "the user's script");
                ReplaceEngine.ForkLeftovers.Extra[forkLua] = new[] { Sha(B(forkText)) }; ReplaceEngine.ForkLeftovers.Extra[forkDoc] = new[] { Sha(B("the fork's README as shipped")) };
                Require(ReplaceEngine.ForkLeftovers.Table.ContainsKey(forkLua) && ReplaceEngine.ForkLeftovers.Table[forkLua][0] == "601CD2DE240470A700285CBDFC1F91CB9B922A20F11D8A40E0A91438243BBF49", "the real fork 1.10.9 script must be listed");
                string handProxy = GameText(f.Game, "amd_ags_x64.dll");
                Require(LoaderCostumePak.LoaderPresent(f.Game) && InstalledLoader.Record(engine.LoadState()) == null, "the fixture loader installed by hand");
                string approvedZip = Path.Combine(root, "takeover-zips", "SF6_SlotsLoader_v1.27.4.zip"); Directory.CreateDirectory(Path.GetDirectoryName(approvedZip)); File.WriteAllBytes(approvedZip, LoaderZip("1.27.4"));
                ReplaceMod record = InstallZip(engine, approvedZip, "SF6 Slots Loader");
                ReplaceState st = engine.LoadState();
                Require(InstalledLoader.Record(st) != null && InstalledLoader.Record(st).Id == record.Id && GameText(f.Game, LoaderCostumePak.PluginPath) == "fixture plugin 1.27.4" && GameText(f.Game, "amd_ags_x64.dll") != handProxy, "taken over: the approved files are in place");
                Require(st.Originals.Count == 1 && st.Originals[0].Path == "amd_ags_x64.dll" && File.ReadAllText(st.Originals[0].Backup) == genuine, "only the game's own library may be an original, and it must be the genuine one: " + String.Join(", ", st.Originals.Select(o => o.Path + "=" + File.ReadAllText(o.Backup).Substring(0, Math.Min(24, File.ReadAllText(o.Backup).Length)))));
                Require(File.ReadAllText(costume) == "the user's costume mod" && LoaderFlags.Read(settings, "menu_outfit_options") == true, "the user's costume mods and the loader's settings must stay (the Manager adds its own switches to the settings now; the user's key keeps its value)");
                Require(File.ReadAllText(Path.Combine(AppConfig.DataDir, "log.txt")).Contains("Slotsloader taken over: "), "the takeover must be logged");
                string[] kept = Directory.GetFiles(AppConfig.DataDir, "*", SearchOption.AllDirectories).Where(x => x.IndexOf("takeover-leftovers", StringComparison.OrdinalIgnoreCase) >= 0).ToArray();
                Require(GameText(f.Game, forkLua) == null && kept.Length == 1 && File.ReadAllText(kept[0]) == forkText && kept[0].EndsWith(forkLua.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase)
                    && File.ReadAllText(Path.Combine(AppConfig.DataDir, "log.txt")).Contains("Slotsloader taken over: " + forkLua + " (a file of Slotsloader Fork that the new loader does not ship) removed; a copy is kept at "), "the fork's leftover script must go, with a copy and a log line: " + String.Join(", ", kept));
                Require(GameText(f.Game, forkDoc) == editedDoc && GameText(f.Game, userLua) == "the user's script" && !st.Originals.Any(o => o.Path == forkLua), "an edited fork file and the user's own files must stay");
                engine.Uninstall(record.Id, false, out ignored);
                Require(GameText(f.Game, "amd_ags_x64.dll") == genuine && GameText(f.Game, LoaderCostumePak.PluginPath) == null && GameText(f.Game, "amd_ags_x64_real.dll") == null && engine.LoadState().Originals.Count == 0, "uninstall: the game's own library, no loader file of the hand install back");
                Require(File.ReadAllText(costume) == "the user's costume mod" && LoaderFlags.Read(settings, "menu_outfit_options") == true, "the user's data must stay after the uninstall");
                Require(GameText(f.Game, forkLua) == null && GameText(f.Game, forkDoc) == editedDoc, "the fork's leftover must not come back at the uninstall");
            }
            finally { ReplaceEngine.ForkLeftovers.Extra.Clear(); CleanState(config); GameProcesses.Probe = previousProbe; }
        }
        // 1.0.22 (the user's SF6 would not start, 2026-11-23): a Slotsloader left in the game folder without its plugin (a
        // "Restore to vanilla" removes it) and with a proxy build not in KnownBuilds. Taken over, then "Turn outfit mods
        // off": the game must get AMD's own amd_ags_x64.dll back, never the old proxy without its amd_ags_x64_real.dll.
        private static void TakeoverWithoutPlugin(string root)
        {
            ReplaceChecks.Fixture f = ReplaceChecks.Create(Path.Combine(root, "takeover-noplugin"), true, ReplaceMethods.Loose);
            var config = new AppConfig { GameRoot = f.Game, Libraries = new List<string>(), SevenZipPath = "" };
            var probe = new FakeProcessProbe(); IProcessProbe previousProbe = GameProcesses.Probe; GameProcesses.Probe = probe;
            try
            {
                ReplaceEngine engine = ReplaceEngine.ForConfig(config);
                string ignored; foreach (ReplaceMod m in engine.LoadState().Mods.ToList()) engine.Uninstall(m.Id, false, out ignored);
                string genuine = GameText(f.Game, "amd_ags_x64.dll"), handProxy = "fixture loader proxy, a build no table knows" + new string('y', 4096);
                Action handInstall = delegate
                {
                    File.WriteAllText(Path.Combine(f.Game, "amd_ags_x64.dll"), handProxy);
                    File.WriteAllText(Path.Combine(f.Game, "amd_ags_x64_real.dll"), genuine);
                    string data = Path.Combine(f.Game, @"reframework\data\SF6_Costumes_Data\loader"); Directory.CreateDirectory(data); File.WriteAllText(Path.Combine(data, "version.txt"), "1.27.3.14");
                    Require(!File.Exists(Path.Combine(f.Game, LoaderCostumePak.PluginPath.Replace('/', '\\'))), "the fixture's hand install has no plugin");
                };
                string approvedZip = Path.Combine(root, "takeover-noplugin-zips", "SF6_SlotsLoader_v1.27.5.zip"); Directory.CreateDirectory(Path.GetDirectoryName(approvedZip)); File.WriteAllBytes(approvedZip, LoaderZip("1.27.5"));

                // A record made by the 1.0.21 rule (the genuine library not recognised): its kept "original" is the proxy.
                handInstall();
                ReplaceMod record = InstallZip(engine, approvedZip, "SF6 Slots Loader");
                ReplaceState st = engine.LoadState();
                Require(st.Originals.Count == 1 && File.ReadAllText(st.Originals[0].Backup) == handProxy, "fixture: without the genuine library known, the old rule keeps the proxy as the original");
                KnownBuilds.Extra[ReplaceEngine.Sha(B(genuine))] = "the game's own amd_ags_x64.dll (fixture)";
                engine.SetModsOff(true);
                Require(GameText(f.Game, "amd_ags_x64.dll") == genuine && GameText(f.Game, "amd_ags_x64_real.dll") == null, "outfit mods off with the old record: AMD's own amd_ags_x64.dll must be back, not the proxy without its _real file: " + (GameText(f.Game, "amd_ags_x64.dll") ?? "(none)").Substring(0, 20));
                Require(File.ReadAllText(Path.Combine(AppConfig.DataDir, "log.txt")).Contains("is put back from the genuine amd_ags_x64_real.dll"), "the fallback must be logged");
                engine.SetModsOff(false);
                st = engine.LoadState();
                Require(GameText(f.Game, "amd_ags_x64.dll") != genuine && GameText(f.Game, "amd_ags_x64_real.dll") == genuine && st.Originals.Count == 1 && File.ReadAllText(st.Originals[0].Backup) == genuine, "back on: the loader's proxy in place and the genuine library kept as the original");
                engine.Uninstall(record.Id, false, out ignored);
                Require(GameText(f.Game, "amd_ags_x64.dll") == genuine, "uninstall after back on: the game's own library");

                // The new rule: the genuine amd_ags_x64_real.dll beside an unknown non-AMD file marks an outside proxy.
                handInstall();
                record = InstallZip(engine, approvedZip, "SF6 Slots Loader");
                st = engine.LoadState();
                Require(st.Originals.Count == 1 && File.ReadAllText(st.Originals[0].Backup) == genuine, "takeover without the plugin: the genuine library must be the original");
                engine.SetModsOff(true);
                Require(GameText(f.Game, "amd_ags_x64.dll") == genuine && GameText(f.Game, "amd_ags_x64_real.dll") == null, "outfit mods off: the game's own library");
                engine.SetModsOff(false);
                engine.Uninstall(record.Id, false, out ignored);
                Require(GameText(f.Game, "amd_ags_x64.dll") == genuine && engine.LoadState().Originals.Count == 0, "uninstall: the game's own library, no original left");
            }
            finally { KnownBuilds.Extra.Clear(); CleanState(config); GameProcesses.Probe = previousProbe; }
        }
        // The engine's state is this run's one Data folder: what a check installed is uninstalled again (or the record
        // dropped), so the next check can use another fixture game.
        private static void CleanState(AppConfig config)
        {
            try
            {
                ReplaceEngine engine = ReplaceEngine.ForConfig(config); string ignored;
                foreach (ReplaceMod m in engine.LoadState().Mods.ToList()) engine.Uninstall(m.Id, false, out ignored, DriftChoice.RestoreOriginal);
            }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); try { File.Delete(ReplaceEngine.ForConfig(config).StatePath); } catch (IOException) { } }
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
        // 1.0.19: the approved list names this zip (file SF6_SlotsLoader_v<version>.zip in release v<version>), served beside it.
        private static void Approve(LoaderChecks.LoopbackServer server, string version, byte[] zip)
        {
            string file = "SF6_SlotsLoader_v" + version + ".zip";
            server.Routes["/dl/approved/v" + version + "/" + file] = new LoaderChecks.LoopbackServer.Route { Body = zip };
            server.Routes["/dependencies.json"] = new LoaderChecks.LoopbackServer.Route { Body = B(LoaderChecks.ListJson(version, "v" + version, file, zip.Length, Sha(zip).ToLowerInvariant(), ",\"native_core\":{\"version\":\"0.9.14\"}", "")) };
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
                // 1.0.19: the approved list on the fixture server; its download URLs point there too (DependencyList.TestDownloadBase).
                UpdateCheck.ListUrl = server.Base + "/dependencies.json"; DependencyList.TestDownloadBase = server.Base + "/dl/approved";
                // The install link's fallback pin for these checks, whatever the build pins.
                UpdateCheck.PinOverride = LoaderPin.FromList(LoaderChecks.ListJson("1.9.7", "v1.9.7", "SF6_SlotsLoader_v1.9.7.zip", 1, new string('A', 64), "", ""));
                byte[] l197 = LoaderZip("1.9.7"), l198 = LoaderZip("1.9.8"), l199 = LoaderZip("1.9.9"), l200 = LoaderZip("2.0.0"), l196 = LoaderZip("1.9.6");
                byte[] portable = B("not a real zip"), other = B("other zip");
                string sums = Sha(portable) + "  Slotsloader-Manager-" + Next + "-portable.zip\n" + Sha(other) + "  extras.zip\n";
                Func<Dictionary<string, object>[]> catalogue = delegate
                {
                    return new[] {
                        // Old loader-v* releases stay in this repository for Managers 1.0.18 and older: 1.0.19 never reads them.
                        Release(server, "loader-v1.9.7", false, false, new Asset { Name = "SF6_SlotsLoader_v1.9.7.zip", Body = l197 }),
                        Release(server, "loader-v1.9.9", false, false, new Asset { Name = "SF6_SlotsLoader_v1.9.9.zip", Body = l199 }),
                        Release(server, "loader-v2.0.0", false, false, new Asset { Name = "SF6_SlotsLoader_v2.0.0.zip", Body = l200, Digest = false }),
                        Release(server, "manager-v" + Next, false, false, new Asset { Name = "Slotsloader-Manager-" + Next + "-portable.zip", Body = portable, Digest = false }, new Asset { Name = "extras.zip", Body = other, Digest = false }, new Asset { Name = "SHA256SUMS.txt", Body = B(sums), Digest = false }),
                        Release(server, "manager-v" + After, false, false, new Asset { Name = "Slotsloader-Manager-" + After + "-portable.zip", Body = portable, Digest = false }),
                        Release(server, "manager-v9.0.0", false, true, new Asset { Name = "Slotsloader-Manager-9.0.0-portable.zip", Body = portable }),
                        Release(server, "installer-v2.0.0", false, false, new Asset { Name = "SF6-SlotsLoader-Installer-2.0.0-portable.zip", Body = other }),
                        Release(server, "something-else-v5", false, false, new Asset { Name = "x.zip", Body = other })
                    };
                };
                Publish(server, catalogue());
                Approve(server, "1.9.8", l198);

                check("1.0.19: the Slotsloader offer is the approved entry of dependencies.json (the loader-v* releases are not read); the Manager's releases as before: highest tagged version, drafts skipped, pre-releases only with the setting, a release without a verifiable checksum refused and logged, the *-portable.zip verified through SHA256SUMS.txt", delegate
                {
                    UpdateCheckResult off = UpdateCheck.Check(false), on = UpdateCheck.Check(true);
                    Require(off.Ok && off.Loader != null && off.Loader.Version == "1.9.8" && off.Loader.IsApproval && off.Loader.Sha256 == Sha(l198) && off.Loader.Size == l198.Length && off.Loader.Tag == "v1.9.8"
                        && off.Loader.Url == server.Base + "/dl/approved/v1.9.8/SF6_SlotsLoader_v1.9.8.zip" && off.Loader.HtmlUrl == "https://github.com/Wael3rd/SF6-ModSuite/releases/tag/v1.9.8" && off.ListReason == "", "approved: " + (off.Loader == null ? off.ListReason : off.Loader.Tag + " " + off.Loader.Url));
                    Require(on.Loader != null && on.Loader.Version == "1.9.8" && !on.Loader.Prerelease, "pre-releases on: the approved entry still: " + (on.Loader == null ? "none" : on.Loader.Version));
                    Require(!off.Refused.Concat(on.Refused).Any(r => r.StartsWith("loader-")), "a loader-v* release was read: " + String.Join("; ", off.Refused));
                    Require(off.Refused.Any(r => r.StartsWith("manager-v" + After + ": ") && r.Contains("no SHA-256 digest and the release has no SHA256SUMS.txt")), "refused: " + String.Join("; ", off.Refused));
                    string ignoredProduct, ignoredVersion;   // 1.0.0: a private-build tag is never a Manager release
                    Require(!UpdateCheck.ProductOf("installer-v2.0.0", out ignoredProduct, out ignoredVersion) && UpdateCheck.ProductOf("manager-v1.0.0", out ignoredProduct, out ignoredVersion) && ignoredProduct == UpdateProducts.Manager, "installer-v* tags must be ignored");
                    Require(off.Manager != null && off.Manager.Version == Next && off.Manager.Asset == "Slotsloader-Manager-" + Next + "-portable.zip" && off.Manager.DigestSource == "SHA256SUMS.txt" && off.Manager.Sha256 == Sha(portable), "manager: " + (off.Manager == null ? "none" : off.Manager.Tag + " " + off.Manager.Asset));
                    string log = File.ReadAllText(Path.Combine(AppConfig.DataDir, "log.txt"));
                    Require(log.Contains("Update check: refused manager-v" + After), "the refusal was not logged");
                    report.Add("  approved: Slotsloader " + off.Loader.Version + " (" + off.Loader.Asset + "); manager " + off.Manager.Version + " (" + off.Manager.DigestSource + "); refused: " + String.Join("; ", off.Refused));
                });
                check("1.0.19 the list read: unknown packages (native_core) and keys ignored and logged; a list the parser refuses (no slots_loader entry; the malformed lists are the loader group's) or one over 64 KB is refused and logged, and a missing list or an offline server is a reason, never an exception; the Manager part still works", delegate
                {
                    string why;
                    server.Routes["/dependencies.json"] = new LoaderChecks.LoopbackServer.Route { Body = B(LoaderChecks.ListJson("1.9.8", "v1.9.8", "SF6_SlotsLoader_v1.9.8.zip", l198.Length, Sha(l198).ToLowerInvariant(),
                        ",\"native_core\":{\"name\":\"SF6 Native Core\",\"version\":\"0.9.14\",\"release\":\"v1.37.0\",\"file\":\"SF6_NativeCore_0.9.14.zip\",\"size\":684069,\"sha256\":\"" + new string('b', 64) + "\"},\"something_new\":{\"x\":[1]}", ",\"updated\":\"2026-11-16\"")) };
                    ReleaseOffer read = UpdateCheck.ReadList(out why);
                    Require(read != null && read.Version == "1.9.8" && read.Sha256 == Sha(l198) && why == "" && File.ReadAllText(Path.Combine(AppConfig.DataDir, "log.txt")).Contains("not used here: native_core, something_new"), "unknown packages: " + why);
                    // 1.0.21: the malformed lists themselves are the loader group's (20 lists through the same DependencyList.Parse);
                    // here one refused list for the log and the Skipped line, and the 64 KB cap of the read.
                    var bad = new Dictionary<string, string> {
                        { "no slots_loader", "{\"format\":1,\"source\":\"Wael3rd/SF6-ModSuite\",\"packages\":{\"native_core\":{}}}" },
                        { "too big", LoaderChecks.ListJson("1.9.8", "v1.9.8", "a.zip", 5, Sha(l198), "", ",\"pad\":\"" + new string('x', 70000) + "\"") } };
                    foreach (var b in bad)
                    {
                        server.Routes["/dependencies.json"] = new LoaderChecks.LoopbackServer.Route { Body = B(b.Value) };
                        UpdateCheckResult r = UpdateCheck.Check(false);
                        Require(r.Ok && r.Loader == null && r.ListReason.Length > 0 && r.Manager != null && MainForm.SkippedSummary(r).Contains("Couldn't read the approved Slotsloader list: " + r.ListReason), b.Key + ": " + r.ListReason + " / " + MainForm.SkippedSummary(r));
                        report.Add("  " + b.Key + ": " + r.ListReason);
                    }
                    Require(File.ReadAllText(Path.Combine(AppConfig.DataDir, "log.txt")).Contains("Update check: refused the approved Slotsloader list: dependencies.json: it has no slots_loader entry."), "a refused list must be logged");
                    server.Routes.Remove("/dependencies.json");
                    UpdateCheckResult missing = UpdateCheck.Check(false);
                    Require(missing.Ok && missing.Loader == null && missing.ListReason.StartsWith("HTTP 404"), "missing list: " + missing.ListReason);
                    UpdateCheck.ListUrl = "http://127.0.0.1:" + LoaderChecks.LoopbackServer.RefusedPort() + "/dependencies.json";
                    Require(UpdateCheck.ReadList(out why) == null && why == "could not connect to 127.0.0.1", "offline list: " + why);
                    UpdateCheck.ListUrl = server.Base + "/dependencies.json"; SafeHttp.RefusedLoopbackPort = 0;
                    Approve(server, "1.9.8", l198);
                });
                check("1.0.19 what is offered: the approved Slotsloader over a different recorded version (newer approved: an update, whoever installed the loader; older approved: going back, only over a loader the Manager installed from an approved zip, never over one the user installed); nothing when equal; the Manager only when newer than the running build; the install link: the approved entry, else the pin", delegate
                {
                    UpdateCheckResult on = UpdateCheck.Check(true);
                    ReleaseOffer approved = on.Loader;
                    Require(UpdateCheck.LoaderUpdate(approved, "1.9.7-custom-game-derived", false).Version == "1.9.8" && UpdateCheck.LoaderUpdate(approved, "1.9.7", true).Version == "1.9.8" && !UpdateCheck.GoesBack(approved, "1.9.7"), "newer approved");
                    Require(UpdateCheck.LoaderUpdate(approved, "1.9.8", true) == null && UpdateCheck.LoaderUpdate(approved, "1.9.8-custom", false) == null && UpdateCheck.LoaderUpdate(approved, "1.9.8.0", true) == null, "equal: nothing");
                    Require(UpdateCheck.LoaderUpdate(approved, "1.9.9", true) == approved && UpdateCheck.GoesBack(approved, "1.9.9"), "an approval taken back: go back to the approved version");
                    Require(UpdateCheck.LoaderUpdate(approved, "1.9.9", false) == null, "a newer loader the user installed is never offered a downgrade");
                    Require(UpdateCheck.LoaderUpdate(approved, null, true) == null && UpdateCheck.LoaderUpdate((ReleaseOffer)null, "1.9.7", true) == null, "no record or no approval: nothing");
                    // Which installs came from an approval: the build's pin, or a zip this Manager read from the list (the record's archive SHA-256).
                    var seen = new UpdateSettings(); seen.RememberApproved(approved, DateTime.UtcNow.ToString("o"));
                    Require(UpdateCheck.FromApproval(new ReplaceMod { ArchiveSha = Sha(l198) }, seen) && UpdateCheck.FromApproval(new ReplaceMod { ArchiveSha = UpdateCheck.PinOverride.Sha256.ToLowerInvariant() }, new UpdateSettings())
                        && !UpdateCheck.FromApproval(new ReplaceMod { ArchiveSha = Sha(l199) }, seen) && !UpdateCheck.FromApproval(new ReplaceMod { ArchiveSha = null }, seen) && !UpdateCheck.FromApproval(null, seen), "approved installs");
                    Require(seen.ApprovedLoader() == approved && seen.ApprovedSeen.SequenceEqual(new[] { Sha(l198) }), "remembered approval");
                    seen.ApprovedBy = "0.0.1"; Require(seen.ApprovedLoader() == null, "a list read by another Manager version is not used");
                    var oldStyle = new ReleaseOffer { Product = UpdateProducts.Loader, Version = "1.9.9", DigestSource = "GitHub digest", Url = "https://github.com/ZZtaii/slotsloader-manager/releases/download/loader-v1.9.9/x.zip" };
                    Require(!oldStyle.IsApproval && UpdateCheck.InstallPin(oldStyle) == UpdateCheck.PinOverride && UpdateCheck.InstallPin(null) == UpdateCheck.PinOverride && UpdateCheck.InstallPin(approved).Version == "1.9.8" && UpdateCheck.InstallPin(approved).Url == approved.Url, "install link: the approved entry, else the pin");
                    Require(UpdateCheck.ManagerUpdate(on, new Version(0, 1, 0, 0)).Version == Next && UpdateCheck.ManagerUpdate(on, NextVersion) == null && UpdateCheck.ManagerUpdate(on, new Version(UpdateCheck.Running.Major, UpdateCheck.Running.Minor + 1, 0, 0)) == null, "manager offers");
                });
                check("1.0.19 versions skipped in between: from any recorded Slotsloader (the fork 1.10.9, a local SF6_SlotsLoader_v1.27.3.14-manager.zip, 1.27.4; read from modinfo.ini or the file name) the offer is the approved 1.27.7 itself, in one step", delegate
                {
                    ReleaseOffer approved = ReleaseOffer.Approved(LoaderPin.FromList(LoaderChecks.ListJson("1.27.7", "v1.40.0", "SF6_SlotsLoader_v1.27.7.zip", 1234, new string('c', 64), "", "")));
                    string dir = Path.Combine(root, "skipped"); Directory.CreateDirectory(dir);
                    var cases = new[] { new[] { "SF6_Slotsloader_Fork_v1.10.9.zip", "1.10.9" }, new[] { "SF6_SlotsLoader_v1.27.3.14-manager.zip", "1.27.3.14" }, new[] { "SF6_SlotsLoader_v1.27.4.zip", "1.27.4" } };
                    foreach (string[] c in cases)
                    {
                        string zip = Path.Combine(dir, c[0]); File.WriteAllBytes(zip, LoaderZip(c[1]));
                        string source, fromZip = InstalledLoader.Version(new ReplaceMod { SourcePath = zip }, out source), fromName = InstalledLoader.Version(new ReplaceMod { SourcePath = Path.Combine(dir, "gone", c[0]) }, out source);
                        Require(fromZip == c[1] + "-custom" && fromName == c[1] && source == "file name", c[0] + ": " + fromZip + " / " + fromName);
                        foreach (string installed in new[] { fromZip, fromName })
                            foreach (bool fromApproval in new[] { true, false })
                                Require(UpdateCheck.LoaderUpdate(approved, installed, fromApproval) == approved && !UpdateCheck.GoesBack(approved, installed), c[0] + " (" + installed + "): not offered 1.27.7 directly");
                    }
                    report.Add("  1.10.9, 1.27.3.14 and 1.27.4 are each offered 1.27.7 directly");
                });
                check("a 404 (private repository) and an offline server give a reason and no exception; the 24 h throttle", delegate
                {
                    UpdateCheck.ApiUrl = server.Base + "/missing";
                    UpdateCheckResult r404 = UpdateCheck.Check(false);
                    Require(!r404.Ok && r404.Status == 404 && r404.Reason.StartsWith("HTTP 404") && r404.Loader != null, "404: " + r404.Reason + " (the approved list is still read)");
                    UpdateCheck.ApiUrl = "http://127.0.0.1:" + LoaderChecks.LoopbackServer.RefusedPort() + "/releases";
                    UpdateCheckResult offline = UpdateCheck.Check(false);
                    Require(!offline.Ok && offline.Reason == "could not connect to 127.0.0.1", "offline: " + offline.Reason);
                    UpdateCheck.ApiUrl = server.Base + "/releases"; SafeHttp.RefusedLoopbackPort = 0;
                    var s = new UpdateSettings { LastCheckUtc = DateTime.UtcNow.AddHours(-2).ToString("o") };
                    Require(!s.Due(DateTime.UtcNow), "checked 2 h ago is not due");
                    s.LastCheckUtc = DateTime.UtcNow.AddHours(-25).ToString("o");
                    Require(s.Due(DateTime.UtcNow) && new UpdateSettings().Due(DateTime.UtcNow), "25 h ago / never must be due");
                });
                check("1.0.19 replacing one Slotsloader record with another (the fork to the suite line) never brings back files from before the first loader install (the old SlotsloaderOpponentOutfitSummary.lua); the genuine amd_ags_x64.dll stays backed up; an uninstall still restores originals; an ordinary mod's originals come back as before", delegate { LoaderSwitch(root); });
                check("1.0.19 a Slotsloader installed outside the Manager is taken over: its files at the loader's paths are replaced, not kept as originals (the genuine amd_ags_x64.dll is, from its amd_ags_x64_real.dll); costume_mods and the loader's settings are kept; a hand-installed fork's files the new loader does not ship are removed only with a known fork file's exact bytes (a copy kept, logged); an uninstall leaves the game's own library", delegate { Takeover(root); });
                check("1.0.22 a Slotsloader left without its plugin and with an unknown proxy build: the takeover keeps the genuine amd_ags_x64_real.dll as the original, and turning outfit mods off (also with a record made by the old rule) puts AMD's own amd_ags_x64.dll back, never the proxy", delegate { TakeoverWithoutPlugin(root); });                check("downloads are verified: a wrong published digest is refused and nothing is kept; a correct one lands in Data\\downloads", delegate
                {
                    string dir = Path.Combine(root, "dl");
                    DownloadFailure failure = null;
                    try { SafeHttp.Download(server.Base + "/dl/approved/v1.9.8/SF6_SlotsLoader_v1.9.8.zip", dir, "x.zip", l198.Length, Sha(l197), "Downloading", "published"); } catch (DownloadFailure f) { failure = f; }
                    Require(failure != null && failure.Reason.Contains("does not match the published build") && !Directory.EnumerateFiles(dir).Any(), "wrong digest: " + (failure == null ? "accepted" : failure.Reason));
                    string ok = SafeHttp.Download(server.Base + "/dl/approved/v1.9.8/SF6_SlotsLoader_v1.9.8.zip", dir, "x.zip", l198.Length, Sha(l198), "Downloading", "published");
                    Require(File.ReadAllBytes(ok).SequenceEqual(l198), "good download");
                });
                check("Manager self-update on a temporary copy of the app folder: stage (Manager exe, config and README, FileVersion = tag), apply (previous kept, running exes renamed to .old, new files in, 1.0.13: the CLI, CLI.md and VALIDATION.txt of an older install removed), .old cleanup and log at the next start, update-rollback, a failure mid-copy restores every file, a missing folder is refused; nothing else in Data changes", delegate { SelfUpdate(root); });
                check("1.0.11 bundled 7-Zip in a self-update: the 7-Zip folder is accepted only complete (7z.exe, 7z.dll, License.txt); apply replaces the app's copy (old files as .old, kept in previous); a failure after it puts the old copy back; .old removed at the next start; rollback restores it; a release without 7-Zip leaves the app's copy alone", delegate { SelfUpdateSevenZip(root); });
                check("1.0.11 which 7-Zip runs: an existing Settings path first, else an installed 7-Zip (with its 7z.dll), else the Manager's own copy beside the exe; with none usable (or only one too old for RAR5) the first RAR/7Z read fetches the pinned 7-Zip (a failed fetch says why in RAR/7Z errors and Settings and waits before trying again; a good one replaces a broken 7-Zip folder and a real 7Z archive is listed through it); an installed 7-Zip older than the Manager's copy gives way to it (same, newer or unknown version keeps its place)", delegate { SevenZipChoice(root, server); });
                if (TestTier.Full) check("window: toasts for both products (bottom-right above the status strip, stacked), keyboard reachable with accessible names, no tooltips; Skip this version is remembered; ✕ comes back at the next check; a 404 background check is silent and logged; Settings → Updates reports up to date and the 404 reason", delegate { Window(root, server, catalogue, l197, l198); });
                check("CLI update-check prints both products with source, asset, size and digest", delegate
                {
                    string text = ReplaceChecks.Cli("update-check", "--include-prereleases");
                    Require(text.StartsWith("0\n") && text.Contains("Slotsloader Manager installed: " + SafeHttp.AppVersion() + " · latest: " + Next) && text.Contains("SHA-256 " + Sha(l198)) && text.Contains("Source: " + server.Base + "/releases"), "update-check: " + text);
                    report.Add("  " + String.Join(" | ", text.Split('\n').Where(l => l.StartsWith("Slotsloader")).Select(l => l.Trim())));
                });
            }
            finally
            {
                UpdateCheck.ApiUrl = previousApi; UpdateCheck.ListUrl = UpdateCheck.DefaultListUrl; DependencyList.TestDownloadBase = null; SafeHttp.AllowLoopbackHttp = previousHttp; SafeHttp.RefusedLoopbackPort = 0; UpdateCheck.PinOverride = null; MainForm.AppDirOverride = null; MainForm.NoRelaunch = false; AppUpdater.FailAfterCopies = -1;
                AppUpdater.FailAfterSevenZip = false; SevenZipTool.AppDirOverride = null; SevenZipTool.InstalledOverride = null; SevenZipTool.ClearCache();
                SevenZipTool.Pin = SevenZipTool.Supported; SevenZipTool.DownloadDirOverride = null; SevenZipTool.ResetFetch();
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
            foreach (string f in AppUpdater.AppFiles)   // 1.0.13: an older install still has the CLI, CLI.md and VALIDATION.txt
            {
                string from = Path.Combine(here, f);
                if (File.Exists(from)) File.Copy(from, Path.Combine(app, f)); else File.WriteAllText(Path.Combine(app, f), "old " + f);
            }
            File.WriteAllText(Path.Combine(data, "settings.json"), "{}"); File.WriteAllText(Path.Combine(data, "replace-state.json"), "{\"keep\":true}");
            // 2.5.9: the installed copy bundles an older SlotsLoader zip; the release bundles a newer one.
            string bundle = Path.Combine(app, LoaderRelease.BundleFolder); Directory.CreateDirectory(bundle);
            File.WriteAllText(Path.Combine(bundle, "SF6_SlotsLoader_v1.9.7.zip"), "old loader");
            var before = Snapshot(app);
            // The release: the same exe (FileVersion of this build), new README, no CLI (1.0.13).
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
            Require(AppUpdater.OptionalFiles.All(f => !File.Exists(Path.Combine(app, f))), "apply: the CLI, CLI.md and VALIDATION.txt left in the app folder: " + String.Join(", ", AppUpdater.OptionalFiles.Where(f => File.Exists(Path.Combine(app, f)))));
            Require(AppUpdater.AppFiles.All(f => File.Exists(Path.Combine(AppUpdater.PreviousDir(data), f))) && LoaderRelease.HashFile(Path.Combine(AppUpdater.PreviousDir(data), "README.md")) == before["README.md"], "the previous version was not kept");
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

        // 1.0.11: a release zip of this build's exes and configs, placeholder docs, and the given extra entries
        // (all 7 files, as the releases for the updaters of 1.0.12 and older carry them).
        private static string ReleaseZip(string root, string app, string name, params string[] extra)
        {
            string zip = Path.Combine(root, name);
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                foreach (string f in AppUpdater.AppFiles)
                {
                    if (f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".config", StringComparison.OrdinalIgnoreCase)) archive.CreateEntryFromFile(Path.Combine(app, f), f);
                    else using (var w = new StreamWriter(archive.CreateEntry(f).Open())) w.Write("new " + f);
                }
                foreach (string e in extra) using (var w = new StreamWriter(archive.CreateEntry(e).Open())) w.Write("new " + e);
            }
            return zip;
        }

        private static void SelfUpdateSevenZip(string root)
        {
            string app = Path.Combine(root, "app-7z"), data = Path.Combine(app, "Data"), here = AppDomain.CurrentDomain.BaseDirectory;
            Directory.CreateDirectory(data);
            foreach (string f in AppUpdater.AppFiles)
            {
                string from = Path.Combine(here, f);
                if (File.Exists(from)) File.Copy(from, Path.Combine(app, f)); else File.WriteAllText(Path.Combine(app, f), "old " + f);
            }
            string seven = Path.Combine(app, SevenZipTool.BundleFolder); Directory.CreateDirectory(seven);
            foreach (string f in SevenZipTool.BundleFiles) File.WriteAllText(Path.Combine(seven, f), "old " + f);
            var before = Snapshot(app, "Data");
            string version = UpdateCheck.Running.ToString(3);
            string[] full = SevenZipTool.BundleFiles.Select(f => SevenZipTool.BundleFolder + "/" + f).ToArray();
            Func<string[], string> stageError = delegate(string[] extra)
            {
                string bad = ReleaseZip(root, app, "bad7-" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".zip", extra);
                try { AppUpdater.Stage(bad, version, data); return null; } catch (IOException ex) { return ex.Message; }
            };
            string partial = stageError(new[] { full[0] }), unknown = stageError(full.Concat(new[] { SevenZipTool.BundleFolder + "/7zFM.exe" }).ToArray()), deeper = stageError(new[] { SevenZipTool.BundleFolder + "/x/7z.exe" });
            Require(partial != null && partial.Contains("7-Zip folder is incomplete") && unknown != null && unknown.Contains("unexpected files") && deeper != null && deeper.Contains("unexpected files"), "7-Zip folder rules: " + partial + " | " + unknown + " | " + deeper);
            string staged = AppUpdater.Stage(ReleaseZip(root, app, "with7z.zip", full), version, data);
            Require(SevenZipTool.BundleFiles.All(f => File.Exists(Path.Combine(staged, SevenZipTool.BundleFolder, f))), "staged 7-Zip files");
            // A failure once the 7-Zip files are in puts the old copy (and everything else) back.
            AppUpdater.FailAfterSevenZip = true;
            Exception failed = null; try { AppUpdater.Apply(app, staged, AppUpdater.PreviousDir(data), "old", version, data); } catch (IOException ex) { failed = ex; }
            AppUpdater.FailAfterSevenZip = false;
            var afterFailure = Snapshot(app, "Data");
            Require(failed != null && before.All(kv => afterFailure.ContainsKey(kv.Key) && afterFailure[kv.Key] == kv.Value) && afterFailure.Count == before.Count, "a failed update left changes: " + String.Join(", ", afterFailure.Keys.Where(k => !before.ContainsKey(k) || before[k] != afterFailure[k])));
            AppUpdater.Apply(app, staged, AppUpdater.PreviousDir(data), "old", version, data);
            Require(SevenZipTool.BundleFiles.All(f => File.ReadAllText(Path.Combine(seven, f)) == "new 7-Zip/" + f && File.ReadAllText(Path.Combine(seven, f + ".old")) == "old " + f), "apply: new 7-Zip files, old ones as .old");
            Require(SevenZipTool.BundleFiles.All(f => File.ReadAllText(Path.Combine(AppUpdater.PreviousDir(data), SevenZipTool.BundleFolder, f)) == "old " + f), "the previous 7-Zip copy was not kept");
            string line = AppUpdater.CleanupAfterUpdate(app, data);
            Require(line != null && line.Contains("7-Zip\\7z.exe.old") && !Directory.GetFiles(seven, "*.old").Any(), "cleanup at the next start: " + line);
            AppUpdater.Rollback(app, data);
            AppUpdater.CleanupAfterUpdate(app, data);
            Require(SevenZipTool.BundleFiles.All(f => File.ReadAllText(Path.Combine(seven, f)) == "old " + f) && !Directory.GetFiles(seven, "*.old").Any(), "rollback did not restore the 7-Zip copy");
            // A release without 7-Zip (1.0.10 and older) leaves the app's copy as it is.
            string plain = AppUpdater.Stage(ReleaseZip(root, app, "without7z.zip"), version, data);
            AppUpdater.Apply(app, plain, AppUpdater.PreviousDir(data), "old", version, data);
            AppUpdater.CleanupAfterUpdate(app, data);
            Require(SevenZipTool.BundleFiles.All(f => File.ReadAllText(Path.Combine(seven, f)) == "old " + f) && File.ReadAllText(Path.Combine(app, "README.md")) == "new README.md", "a release without 7-Zip changed the app's copy");
        }

        private static void SevenZipChoice(string root, LoaderChecks.LoopbackServer server)
        {
            string real = SevenZipTool.Resolve("");   // the PC's own 7-Zip, before the overrides
            if (String.IsNullOrEmpty(real)) throw new InvalidOperationException("this PC has no 7-Zip to test the Manager's copy with");
            string dir = Path.Combine(root, "7z-choice"), installed = Path.Combine(dir, "installed"), broken = Path.Combine(dir, "broken"), app = Path.Combine(dir, "app"), downloads = Path.Combine(dir, "downloads");
            foreach (string d in new[] { installed, broken, Path.Combine(app, SevenZipTool.BundleFolder) }) Directory.CreateDirectory(d);
            File.WriteAllText(Path.Combine(installed, "7z.exe"), "x"); File.WriteAllText(Path.Combine(installed, "7z.dll"), "x");
            File.WriteAllText(Path.Combine(broken, "7z.exe"), "x");   // no 7z.dll: not usable
            string chosen = Path.Combine(dir, "chosen.exe"); File.WriteAllText(chosen, "x");
            var candidates = new List<string> { Path.Combine(broken, "7z.exe"), Path.Combine(installed, "7z.exe") };
            SevenZipTool.InstalledOverride = delegate { return candidates; }; SevenZipTool.AppDirOverride = app; SevenZipTool.DownloadDirOverride = downloads; SevenZipTool.ResetFetch(); SevenZipTool.ClearCache();
            string source;
            Require(SevenZipTool.Resolve(chosen, out source) == chosen && source == "settings" && SevenZipTool.Describe(chosen) == "Found", "a Settings path that exists comes first: " + source);
            Require(SevenZipTool.Resolve(Path.Combine(dir, "gone.exe"), out source) == Path.Combine(installed, "7z.exe") && source == "installed", "a missing Settings path falls back to the installed 7-Zip (the one without 7z.dll skipped): " + source);
            Require(SevenZipTool.Describe("").StartsWith("Using your installed 7-Zip · ") && !SevenZipTool.NeedsFetch(""), "Settings note for an installed 7-Zip: " + SevenZipTool.Describe(""));
            candidates.Clear(); SevenZipTool.ClearCache();
            Require(SevenZipTool.Resolve("", out source) == "" && source == "none" && SevenZipTool.NeedsFetch("") && SevenZipTool.Describe("") == "Not found · the Manager gets it the first time a RAR or 7Z needs it", "nothing installed or the Manager's own: " + source + " · " + SevenZipTool.Describe(""));
            // The pinned 7-Zip (a fixture zip with this PC's 7z.exe and 7z.dll) is served here; first from a missing address.
            string zipFile = Path.Combine(dir, "7-Zip-fixture.zip");
            using (ZipArchive zip = ZipFile.Open(zipFile, ZipArchiveMode.Create))
                foreach (string f in SevenZipTool.BundleFiles)
                    using (Stream s = zip.CreateEntry(f).Open()) { byte[] bytes = f == "License.txt" ? Encoding.UTF8.GetBytes("7-Zip licence (fixture)") : File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(real), f)); s.Write(bytes, 0, bytes.Length); }
            byte[] zipBytes = File.ReadAllBytes(zipFile);
            server.Routes["/tool/7z.zip"] = new LoaderChecks.LoopbackServer.Route { Body = zipBytes };
            var pin = new SevenZipTool.Release { Version = "26.00", Asset = "7-Zip-fixture.zip", Size = zipBytes.Length, Sha256 = SafeHttp.HashFile(zipFile), Url = server.Base + "/tool/gone.zip" };
            SevenZipTool.Pin = pin;
            string rar = Path.Combine(dir, "mod.rar"); File.WriteAllText(rar, "not really a rar");
            Exception none = null; try { using (new SourceReader(rar, "")) { } } catch (IOException ex) { none = ex; }
            Require(none != null && none.Message.Contains("Couldn't download 7-Zip 26.00: HTTP 404") && none.Message.Contains("tries again in a few minutes") && none.Message.Contains("Install 7-Zip or choose 7z.exe in Settings") && SevenZipTool.Fetches == 1, "a failed fetch: " + (none == null ? "read" : none.Message));
            Require(SevenZipTool.Describe("").StartsWith("Not found · Couldn't download 7-Zip 26.00: HTTP 404") && !File.Exists(Path.Combine(app, SevenZipTool.BundleFolder, "7z.dll")), "Settings after a failed fetch: " + SevenZipTool.Describe(""));
            try { using (new SourceReader(rar, "")) { } } catch (IOException) { }
            Require(SevenZipTool.Fetches == 1, "a failed fetch was tried again at once: " + SevenZipTool.Fetches);
            // Then from the right address: a broken 7-Zip folder (7z.exe alone) is replaced and a real 7Z is listed.
            File.WriteAllText(Path.Combine(app, SevenZipTool.BundleFolder, "7z.exe"), "broken");
            pin.Url = server.Base + "/tool/7z.zip"; SevenZipTool.ResetFetch();
            string payload = Path.Combine(dir, "payload", "natives", "stm"); Directory.CreateDirectory(payload); File.WriteAllText(Path.Combine(payload, "a.txt"), "hello");
            string archive = Path.Combine(dir, "mod.7z");
            ProcessUtil.RunText(real, "a -t7z -y " + ProcessUtil.Quote(archive) + " " + ProcessUtil.Quote(Path.Combine(dir, "payload", "*")));
            using (var reader = new SourceReader(archive, ""))
                Require(reader.SevenZip == SevenZipTool.BundledExe && reader.Entries.Keys.Any(k => k.Replace('\\', '/').EndsWith("natives/stm/a.txt", StringComparison.OrdinalIgnoreCase)), "listed through the fetched 7-Zip: " + String.Join(", ", reader.Entries.Keys));
            Require(SevenZipTool.Fetches == 1 && SevenZipTool.BundleFiles.All(f => File.Exists(Path.Combine(app, SevenZipTool.BundleFolder, f))) && File.ReadAllBytes(SevenZipTool.BundledExe).SequenceEqual(File.ReadAllBytes(real))
                && File.Exists(Path.Combine(downloads, pin.Asset)) && !Directory.GetDirectories(app).Any(d => Path.GetFileName(d).StartsWith(SevenZipTool.BundleFolder + ".")), "the fetched copy: " + SevenZipTool.Fetches + " fetch(es)");
            Require(SevenZipTool.Resolve("", out source) == SevenZipTool.BundledExe && source == "bundled" && SevenZipTool.Describe("") == "Using the Manager's own 7-Zip" && !SevenZipTool.NeedsFetch(""), "the Manager's own copy: " + source);
            using (new SourceReader(archive, "")) { }
            Require(SevenZipTool.Fetches == 1, "a usable copy was fetched again");
            // An installed 7-Zip older than the bundled copy gives way to it; the same or a newer version, or none readable, keeps its place.
            Version realVersion = SevenZipTool.VersionOf(real);
            Require(realVersion != null && SevenZipTool.VersionOf(SevenZipTool.BundledExe) == realVersion, "the 7z.dll version was not read: " + realVersion);
            string old = Path.Combine(dir, "old"), newer = Path.Combine(dir, "newer");
            VersionedDll(Path.Combine(old, "7z.dll"), "9.20.0.0"); VersionedDll(Path.Combine(newer, "7z.dll"), "99.0.0.0");
            foreach (string d in new[] { old, newer }) File.WriteAllText(Path.Combine(d, "7z.exe"), "x");
            candidates.Add(Path.Combine(old, "7z.exe")); SevenZipTool.ClearCache();
            Require(SevenZipTool.Resolve("", out source) == SevenZipTool.BundledExe && source == "bundled" && SevenZipTool.Describe("") == "Using the Manager's own 7-Zip · newer than your installed 7-Zip 9.20", "an installed 9.20 gives way to the Manager's copy: " + source + " · " + SevenZipTool.Describe(""));
            // Without the Manager's copy, an installed 7-Zip too old for RAR5 (before 15.06) makes the first RAR/7Z fetch it.
            SevenZipTool.AppDirOverride = Path.Combine(dir, "app-without-copy"); SevenZipTool.ClearCache();
            Require(SevenZipTool.Resolve("", out source) == candidates[0] && source == "installed" && SevenZipTool.NeedsFetch(""), "an installed 9.20 alone needs the Manager's copy: " + source);
            SevenZipTool.AppDirOverride = app; SevenZipTool.ClearCache();
            candidates[0] = Path.Combine(newer, "7z.exe"); SevenZipTool.ClearCache();
            Require(SevenZipTool.Resolve("", out source) == candidates[0] && source == "installed", "a newer installed 7-Zip comes first: " + source);
            candidates[0] = real; SevenZipTool.ClearCache();
            Require(SevenZipTool.Resolve("", out source) == real && source == "installed", "the same version installed comes first: " + source);
            candidates[0] = Path.Combine(installed, "7z.exe"); SevenZipTool.ClearCache();
            Require(SevenZipTool.Resolve("", out source) == candidates[0] && source == "installed", "an installed 7-Zip without a version keeps its place: " + source);
        }

        // A tiny DLL with the given file version (stands in for an installed 7z.dll of that version).
        private static void VersionedDll(string path, string version)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (var compiler = new Microsoft.CSharp.CSharpCodeProvider())
            {
                var options = new System.CodeDom.Compiler.CompilerParameters { GenerateExecutable = false, OutputAssembly = path };
                var result = compiler.CompileAssemblyFromSource(options, "[assembly: System.Reflection.AssemblyFileVersion(\"" + version + "\")] class V { }");
                Require(!result.Errors.HasErrors && File.Exists(path), "test 7z.dll " + version + ": " + String.Join("; ", result.Errors.Cast<System.CodeDom.Compiler.CompilerError>().Select(e => e.ErrorText)));
            }
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
                    Require(loaderToast != null && managerToast != null && loaderToast.AccessibleName == "Slotsloader 1.9.8 is available (you have 1.9.7)" && managerToast.AccessibleName.StartsWith("Slotsloader Manager " + Next + " is available"), "toasts: " + (loaderToast == null ? "no loader toast" : loaderToast.AccessibleName) + " / " + (managerToast == null ? "no manager toast" : managerToast.AccessibleName));
                    var buttons = loaderToast.Controls.OfType<FlowLayoutPanel>().Single().Controls.OfType<FlatButton>().ToList();
                    Require(buttons.Select(b => b.Text).SequenceEqual(new[] { "Update", "What's new", "Skip this version", "✕" }) && buttons.All(b => b.TabStop && b.CanFocus && !String.IsNullOrEmpty(b.AccessibleName)) && buttons[3].AccessibleName.StartsWith("Dismiss"), "toast buttons: " + String.Join(", ", buttons.Select(b => b.Text + "=" + b.AccessibleName)));
                    Panel host = (Panel)loaderToast.Parent; Panel status = (Panel)typeof(MainForm).GetField("statusPanel", Any).GetValue(form);
                    Rectangle hb = host.Bounds;
                    Require(hb.Bottom <= status.Top && hb.Right <= form.ClientSize.Width && hb.Right >= form.ClientSize.Width - DpiMetrics.Px(form, 40) && host.Controls.Count == 2 && form.Controls.GetChildIndex(host) == 0, "toast placement " + hb + " status top " + status.Top);
                    Require(UiAudit.ToolTipFindings(form).Count == 0, "tooltips: " + String.Join("; ", UiAudit.ToolTipFindings(form)));
                    buttons[0].Focus(); Application.DoEvents(); Require(buttons[0].Focused, "the Update button cannot take the keyboard focus");
                    buttons[1].PerformClick(); Require(opened == "https://github.com/Wael3rd/SF6-ModSuite/releases/tag/v1.9.8", "What's new: " + opened);
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
                    Require(String.Equals(record.ArchiveSha, Sha(l198), StringComparison.OrdinalIgnoreCase) && UpdateCheck.FromApproval(record, UpdateSettings.Load()), "the update installed from the approved zip must be recorded as such: " + record.ArchiveSha);
                    // 1.0.19: the maintainer takes the 1.9.8 approval back (the list approves 1.9.7 again): the list read at start
                    // offers going back, worded so, over this Manager-installed 1.9.8. Equal again: no offer.
                    Approve(server, "1.9.7", l197);
                    int reads = form.ListReadsRun; form.RunListRead(); wait(() => form.ListReadsRun > reads, "the list read");
                    Panel back = form.UpdateToast(UpdateProducts.Loader);
                    Require(back != null && back.AccessibleName == "Go back to Slotsloader 1.9.7, the approved version (you have 1.9.8)", "going back: " + (back == null ? "no toast" : back.AccessibleName));
                    var backButtons = back.Controls.OfType<FlowLayoutPanel>().Single().Controls.OfType<FlatButton>().ToList();
                    Label backText = back.Controls.Find("toastText", true).OfType<Label>().First();
                    Require(backButtons[0].Text == "Go back" && backButtons[0].AccessibleName == "Go back to Slotsloader 1.9.7" && TextRenderer.MeasureText(backText.Text, backText.Font).Width <= backText.Width, "the going-back toast: " + backButtons[0].Text + ", text " + TextRenderer.MeasureText(backText.Text, backText.Font).Width + " of " + backText.Width + " px");
                    Require(form.UpdateSummary(new UpdateCheckResult { Ok = true, CheckedUtc = DateTime.UtcNow.ToString("o") }).Contains("back to Slotsloader 1.9.7 (you have 1.9.8)"), "summary: " + form.UpdateSummary(new UpdateCheckResult { Ok = true, CheckedUtc = DateTime.UtcNow.ToString("o") }));
                    Render(form, Path.Combine(root, "toast-go-back.png"));
                    // The same 1.9.8 installed by the user (not from an approved zip): no downgrade offered; the line says it is newer.
                    UpdateSettings manual = UpdateSettings.Load(); List<string> seenBefore = manual.ApprovedSeen.ToList(); manual.ApprovedSeen = manual.ApprovedSeen.Where(x => !x.Equals(Sha(l198), StringComparison.OrdinalIgnoreCase)).ToList(); manual.Save();
                    form.ShowUpdateToasts(); Application.DoEvents();
                    string manualLine = form.UpdateSummary(new UpdateCheckResult { Ok = true, CheckedUtc = DateTime.UtcNow.ToString("o") });
                    Require(form.UpdateToast(UpdateProducts.Loader) == null && manualLine.Contains("Slotsloader 1.9.8 (newer than the approved 1.9.7)"), "a loader the user installed: " + manualLine);
                    manual = UpdateSettings.Load(); manual.ApprovedSeen = seenBefore; manual.Save();
                    Approve(server, "1.9.8", l198);
                    reads = form.ListReadsRun; form.RunListRead(); wait(() => form.ListReadsRun > reads, "the list read (equal)");
                    Require(form.UpdateToast(UpdateProducts.Loader) == null && UpdateSettings.Load().ApprovedLoader().Version == "1.9.8", "equal to the approved version: no offer");
                    // At every start (the 24 h check not due): the list alone, quietly; offline keeps the last approval.
                    UpdateCheck.ListUrl = "http://127.0.0.1:" + LoaderChecks.LoopbackServer.RefusedPort() + "/dependencies.json";
                    UpdateSettings started = UpdateSettings.Load(); started.LastCheckUtc = DateTime.UtcNow.ToString("o"); started.AutoCheck = true; started.Save();
                    int startRuns = form.UpdateChecksRun; reads = form.ListReadsRun; asked.Clear();
                    int previousDelay = MainForm.UpdateStartDelayMs; MainForm.UpdateStartDelayMs = 1;
                    try { typeof(MainForm).GetMethod("StartUpdates", Any).Invoke(form, null); wait(() => form.ListReadsRun > reads, "the list read at start"); }
                    finally { MainForm.UpdateStartDelayMs = previousDelay; UpdateCheck.ListUrl = server.Base + "/dependencies.json"; SafeHttp.RefusedLoopbackPort = 0; }
                    Require(form.UpdateChecksRun == startRuns && asked.Count == 0 && UpdateSettings.Load().ApprovedLoader().Version == "1.9.8" && File.ReadAllText(Path.Combine(AppConfig.DataDir, "log.txt")).Contains("the approved Slotsloader list could not be read: could not connect to 127.0.0.1"),
                        "at start: the list alone, quiet when offline, the last approval kept");
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
                    // Settings → Slotsloader, upper section (13 Nov 2026, the user: the update settings have no tab of their own; they
                    // stand above the Slotsloader options): the 404 reason, then (with no newer release) one line
                    // "Up to date · Slotsloader 1.9.8 · Manager 2.4.0".
                    typeof(MainForm).GetMethod("ShowPage", Any).Invoke(form, new object[] { typeof(MainForm).GetField("settingsPage", Any).GetValue(form) });
                    FlatButton loaderTab = (FlatButton)typeof(MainForm).GetField("settingsLoaderTab", Any).GetValue(form);
                    Panel loaderPanel = (Panel)typeof(MainForm).GetField("settingsLoaderPanel", Any).GetValue(form);
                    loaderTab.PerformClick(); Application.DoEvents();
                    Require(loaderTab.Text == "Slotsloader" && loaderTab.Active && loaderPanel.Visible && typeof(MainForm).GetField("settingsUpdatesTab", Any) == null && typeof(MainForm).GetField("settingsUpdatesPanel", Any) == null
                        && !AllControls(form).OfType<FlatButton>().Any(b => b.Text == "Updates"), "the Slotsloader tab must open its page, and there must be no Updates tab");
                    runs = form.UpdateChecksRun; form.RunUpdateCheck(true); wait(() => form.UpdateChecksRun > runs, "the manual 404 check");
                    Require(form.LastUpdateMessage.StartsWith("Couldn't check for updates: HTTP 404"), "manual 404: " + form.LastUpdateMessage);
                    UpdateCheck.ApiUrl = server.Base + "/releases";
                    Publish(server, catalogue().Where(r => !((string)r["tag_name"]).StartsWith("manager-v" + Next)).ToArray());
                    runs = form.UpdateChecksRun; form.RunUpdateCheck(true); wait(() => form.UpdateChecksRun > runs, "the manual check");
                    string upToDate = "Up to date · Slotsloader 1.9.8 · Manager " + UpdateCheck.ShortVersion(SafeHttp.AppVersion());
                    Require(form.LastUpdateMessage == upToDate + "\nSkipped manager-v" + After + ": no checksum", "up to date: " + form.LastUpdateMessage);
                    Label result = (Label)typeof(MainForm).GetField("settingsUpdateResult", Any).GetValue(form);
                    Label skipped = (Label)typeof(MainForm).GetField("settingsUpdateSkipped", Any).GetValue(form);
                    Require(result.Text == upToDate && result.Visible && skipped.Text == "Skipped manager-v" + After + ": no checksum" && skipped.ForeColor == Palette.Muted, "the Settings result: " + result.Text + " / " + skipped.Text);
                    Require(File.ReadAllText(Path.Combine(AppConfig.DataDir, "log.txt")).Contains("refused manager-v" + After + ": Slotsloader-Manager-" + After + "-portable.zip has no SHA-256 digest"), "the full reason of a skipped release belongs in the log");
                    // Copy and spacing: a caption names the section, "Check now" is a normal button in the row of the two boxes, a clear
                    // gap before the one-line result.
                    FlatButton checkNow = (FlatButton)typeof(MainForm).GetField("settingsCheckUpdates", Any).GetValue(form);
                    int gap = result.PointToScreen(Point.Empty).Y - checkNow.PointToScreen(new Point(0, checkNow.Height)).Y;
                    Require(checkNow.Text == "Check now" && !checkNow.Primary && gap >= DpiMetrics.Px(form, 12) && ((NoteLabel)result).LineCount == 1, "Check now button and result spacing: gap " + gap + " px, result lines " + ((NoteLabel)result).LineCount);
                    CheckBox autoUpdate = (CheckBox)typeof(MainForm).GetField("settingsAutoUpdate", Any).GetValue(form), prereleases = (CheckBox)typeof(MainForm).GetField("settingsPrereleases", Any).GetValue(form);
                    Require(autoUpdate.Visible && prereleases.Visible && autoUpdate.Top == prereleases.Top && autoUpdate.Left < prereleases.Left && prereleases.Right <= checkNow.Left
                        && autoUpdate.Width >= autoUpdate.PreferredSize.Width && prereleases.Width >= prereleases.PreferredSize.Width && AllControls(loaderPanel).OfType<Label>().Count(l => l.Text == "UPDATES") == 1,
                        "the two update boxes and Check now stand in one row under the UPDATES caption, their words whole: " + autoUpdate.Bounds + " " + prereleases.Bounds + " " + checkNow.Bounds);
                    Render(form, Path.Combine(root, "settings-updates-up-to-date.png"));
                    // 1.0.19: the in-game menus switches (LoaderFlags.cs). The fixture's Slotsloader (1.9.8) adds no menus: the boxes
                    // are shown unticked and not offered, and a choice is saved the moment a box changes. The update check inside
                    // the game is not offered at all (the user: not even as an option).
                    // One box per addition since the split (the user: "so i can pick and choose"), in LoaderMenu.All's order.
                    CheckBox[] gameMenus = (CheckBox[])typeof(MainForm).GetField("settingsGameMenus", Any).GetValue(form);
                    Label gameNote = (Label)typeof(MainForm).GetField("settingsGameNote", Any).GetValue(form);
                    Func<string> chosen = delegate { LoaderFlagChoices c = LoaderFlags.Load(AppConfig.DataDir); return String.Concat(LoaderMenu.All.Select(m => m.Get(c) ? "1" : "0")); };
                    Require(typeof(MainForm).GetField("settingsGameUpdates", Any) == null && !AllControls(loaderPanel).OfType<CheckBox>().Any(c => c.Text.IndexOf("inside the game", StringComparison.OrdinalIgnoreCase) >= 0),
                        "the update check inside the game must not be an option");
                    Require(gameMenus.Length == LoaderMenu.All.Length && gameMenus.Select((b, i) => b.Text == LoaderMenu.All[i].Label).All(same => same)
                        && !AllControls(loaderPanel).OfType<CheckBox>().Any(c => c.Text.IndexOf("Show the menus", StringComparison.OrdinalIgnoreCase) >= 0), "one box per addition, no single switch for all of them");
                    Require(gameMenus.All(b => b.Visible && !b.Checked && !b.AutoCheck) && gameNote.Text == "The installed Slotsloader adds no menus.", "in-game switches with a Slotsloader that adds no menus: " + gameNote.Text);
                    // Not offered is muted text that stays readable, never a disabled control (Windows' own dark grey on this background).
                    Require(gameMenus.All(b => b.Enabled && b.ForeColor == Palette.Muted && !b.TabStop), "a switch not offered must be muted, not disabled");
                    typeof(CheckBox).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(gameMenus[0], new object[] { EventArgs.Empty }); Application.DoEvents();   // a click, without touching the keyboard or the mouse
                    Require(!gameMenus[0].Checked && chosen() == "0000000", "a box that is not offered must not tick");
                    int noteTop = gameNote.PointToScreen(Point.Empty).Y;
                    Require(gameMenus.All(b => b.PointToScreen(new Point(0, b.Height)).Y <= noteTop && b.Height >= b.PreferredSize.Height - DpiMetrics.Px(form, 2) && b.Width >= b.PreferredSize.Width)
                        && gameNote.PointToScreen(new Point(0, gameNote.Height)).Y <= loaderPanel.PointToScreen(new Point(0, loaderPanel.Height)).Y,
                        "the in-game switches must show their words whole and fit the tab with their note: " + String.Join(", ", gameMenus.Select(b => b.Width + "x" + b.Height + " for " + b.PreferredSize.Width + "x" + b.PreferredSize.Height)));
                    Require(gameMenus.Select(b => b.Bounds).Distinct().Count() == gameMenus.Length && gameMenus[0].Top == gameMenus[1].Top && gameMenus[0].Left < gameMenus[1].Left && gameMenus[2].Top > gameMenus[0].Top, "the switches stand in two columns, none over another");
                    // The update settings stand above the options, with a clear gap before the options' caption (the user: "so
                    // the ... options have some breathing space and don't immediately start on the tab").
                    Control privacy = AllControls(loaderPanel).First(c => c.Name == "settingsUpdatePrivacy"), gameCaption = AllControls(loaderPanel).First(c => c.Name == "settingsGameCaption");
                    int breathing = gameCaption.PointToScreen(Point.Empty).Y - privacy.PointToScreen(new Point(0, privacy.Height)).Y;
                    Require(privacy.Visible && breathing >= DpiMetrics.Px(form, 16) && gameCaption.PointToScreen(Point.Empty).Y - loaderPanel.PointToScreen(Point.Empty).Y >= DpiMetrics.Px(form, 100),
                        "the options must start under the update settings, after a gap: " + breathing + " px");
                    // 13 Nov 2026: "Outfit Others See" as two choices under the boxes. With a Slotsloader that does not read it:
                    // muted, neither marked, and a click takes nothing.
                    RadioButton[] othersSee = (RadioButton[])typeof(MainForm).GetField("settingsOthersSee", Any).GetValue(form);
                    Require(othersSee.Length == 2 && othersSee[0].Text == "Outfit 1" && othersSee[1].Text == "DriveTech" && othersSee.All(r => r.Visible && r.Enabled && !r.Checked && !r.AutoCheck && r.ForeColor == Palette.Muted),
                        "the outfit others see, not offered: " + String.Join(", ", othersSee.Select(r => r.Text + " " + r.Checked + "/" + r.AutoCheck)));
                    int menusBottom = gameMenus.Max(b => b.PointToScreen(new Point(0, b.Height)).Y);
                    Require(othersSee[0].Top == othersSee[1].Top && othersSee[0].Left < othersSee[1].Left && othersSee[0].PointToScreen(Point.Empty).X == gameMenus[0].PointToScreen(Point.Empty).X
                        && othersSee.All(r => r.PointToScreen(Point.Empty).Y >= menusBottom && r.PointToScreen(new Point(0, r.Height)).Y <= noteTop && r.Width >= r.PreferredSize.Width && r.Height >= r.PreferredSize.Height - DpiMetrics.Px(form, 2)),
                        "the two choices stand under the boxes, side by side, above the note, their words whole");
                    typeof(RadioButton).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(othersSee[1], new object[] { EventArgs.Empty }); Application.DoEvents();
                    Require(!othersSee[1].Checked && LoaderFlags.Load(AppConfig.DataDir).OthersSeePending == null, "a choice that is not offered must not be taken");
                    // The picture, name and words of one option beside the list (the user: "help users learn what is what"): the
                    // first option when the tab opens, then the one the pointer or the keyboard is on.
                    LoaderPreviewPanel preview = (LoaderPreviewPanel)typeof(MainForm).GetField("settingsLoaderPreview", Any).GetValue(form);
                    Rectangle picture = preview.PictureBounds;
                    Require(preview.Visible && preview.OptionId == LoaderMenu.All[0].Id && preview.Title == LoaderMenu.All[0].Label && preview.About == LoaderMenu.All[0].About, "the first option is shown when the tab opens: " + preview.OptionId);
                    Require(preview.PointToScreen(Point.Empty).X >= gameMenus[1].PointToScreen(new Point(gameMenus[1].Width, 0)).X && picture.Width >= DpiMetrics.Px(form, 120) && picture.Height == picture.Width * 9 / 16
                        && picture.Right <= preview.ClientSize.Width && picture.Bottom + DpiMetrics.Px(form, 60) <= preview.ClientSize.Height && preview.BackColor != Palette.Background,
                        "the picture stands beside the options, 16:9, inside its panel with room for its words: " + picture + " in " + preview.ClientSize);
                    // The whole tab fits the smallest window the app allows (1110 x 650): the note under the options and the
                    // picture with its words.
                    Size usual = form.Size;
                    form.Size = new Size(DpiMetrics.Px(form, 1110), DpiMetrics.Px(form, 650)); Application.DoEvents();
                    Rectangle small = preview.PictureBounds;
                    Require(gameNote.PointToScreen(new Point(0, gameNote.Height)).Y <= loaderPanel.PointToScreen(new Point(0, loaderPanel.Height)).Y && small.Width >= DpiMetrics.Px(form, 240)
                        && small.Bottom + DpiMetrics.Px(form, 60) <= preview.ClientSize.Height && small.Right <= preview.ClientSize.Width,
                        "the tab must fit the smallest window: note bottom " + gameNote.PointToScreen(new Point(0, gameNote.Height)).Y + " in " + loaderPanel.PointToScreen(new Point(0, loaderPanel.Height)).Y + ", picture " + small + " in " + preview.ClientSize);
                    form.Size = usual; Application.DoEvents();
                    typeof(Control).GetMethod("OnMouseEnter", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(gameMenus[3], new object[] { EventArgs.Empty });
                    Require(preview.OptionId == LoaderMenu.All[3].Id && preview.Title == LoaderMenu.All[3].Label && preview.About == LoaderMenu.All[3].About, "the pointer on an option shows that option: " + preview.OptionId);
                    typeof(Control).GetMethod("OnEnter", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(othersSee[0], new object[] { EventArgs.Empty });
                    Require(preview.OptionId == LoaderMenu.OthersSeeId && preview.Title == LoaderMenu.OthersSeeLabel && preview.About == LoaderMenu.OthersSeeAbout, "the keyboard on a choice shows that choice: " + preview.OptionId);
                    Require(LoaderMenu.All.All(m => m.About.Length > 20 && m.About.EndsWith(".") && m.About.IndexOf('—') < 0) && LoaderMenu.All.Select(m => m.About).Distinct().Count() == LoaderMenu.All.Length
                        && gameMenus.Select((b, i) => b.AccessibleDescription == LoaderMenu.All[i].About).All(same => same), "every addition says what it is, to a screen reader too");
                    gameMenus[2].Checked = true; Application.DoEvents();
                    Require(chosen() == "0010000" && gameMenus[2].Checked && gameMenus.Count(b => b.Checked) == 1 && LoaderFlags.Load(AppConfig.DataDir).OutfitOptions == null, "ticking one box saves that choice alone, and does not choose the costume options: " + chosen());
                    gameMenus[5].Checked = true; gameMenus[2].Checked = false; Application.DoEvents();
                    Require(chosen() == "0000010" && gameMenus.Count(b => b.Checked) == 1, "each box is its own choice: " + chosen());
                    gameMenus[5].Checked = false; Application.DoEvents();
                    Require(chosen() == "0000000" && !File.Exists(Path.Combine(engine.GameRoot, LoaderFlags.UpdateStateRelative)) && !File.Exists(Path.Combine(engine.GameRoot, LoaderFlags.PluginSettingsRelative)),
                        "unticked again, and with a Slotsloader that reads none of this nothing is written to the game folder");
                    // 13 Nov 2026: the seventh switch, the in-game costume options, second in the list. Never chosen here, its box
                    // shows what the game folder holds (the user's game had "menu_outfit_options": true set by hand): ticked for
                    // true, and ticking another box does not make it a choice. Its own box does.
                    CheckBox optionsBox = gameMenus[1];
                    string pluginSettings = Path.Combine(engine.GameRoot, LoaderFlags.PluginSettingsRelative);
                    Require(LoaderMenu.All[1].Id == "options" && optionsBox.Text == "Costume options in the game" && !optionsBox.Checked && LoaderFlags.Load(AppConfig.DataDir).OutfitOptions == null,
                        "the costume options box stands second and starts not chosen: " + optionsBox.Text);
                    var madeFolders = new List<string>();
                    for (string dir = Path.GetDirectoryName(pluginSettings); !Directory.Exists(dir); dir = Path.GetDirectoryName(dir)) madeFolders.Add(dir);
                    Directory.CreateDirectory(Path.GetDirectoryName(pluginSettings));
                    File.WriteAllText(pluginSettings, "{\n  \"menu_outfit_options\": true,\n  \"display_nsfw\": false\n}\n");
                    typeof(MainForm).GetMethod("LoadLoaderFlagFields", Any).Invoke(form, null);
                    Require(optionsBox.Checked && gameMenus.Count(b => b.Checked) == 1 && LoaderFlags.Load(AppConfig.DataDir).OutfitOptions == null, "true in the game folder must show the costume options ticked without making it a choice");
                    gameMenus[4].Checked = true; Application.DoEvents();
                    Require(optionsBox.Checked && gameMenus[4].Checked && LoaderFlags.Load(AppConfig.DataDir).OutfitOptions == null && LoaderFlags.Read(pluginSettings, "menu_outfit_options") == true,
                        "another box ticked: the costume options stay not chosen and the game's true stays");
                    gameMenus[4].Checked = false; Application.DoEvents();
                    optionsBox.Checked = false; Application.DoEvents();
                    Require(!optionsBox.Checked && LoaderFlags.Load(AppConfig.DataDir).OutfitOptions == false && LoaderFlags.Read(pluginSettings, "menu_outfit_options") == true,
                        "its own box unticked: a choice now (off); with a Slotsloader that reads none of this the game folder is not written");
                    optionsBox.Checked = true; Application.DoEvents();
                    Require(optionsBox.Checked && LoaderFlags.Load(AppConfig.DataDir).OutfitOptions == true && chosen() == "0100000", "its own box ticked: chosen on: " + chosen());
                    optionsBox.Checked = false; Application.DoEvents();
                    File.Delete(pluginSettings);
                    foreach (string dir in madeFolders) if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
                    Require(chosen() == "0000000" && !File.Exists(pluginSettings), "the costume options check must leave the fixture as it found it");
                    Render(form, Path.Combine(root, "settings-loader-not-offered.png"));
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
            if (!TestTier.Render) return;   // 1.0.21: review PNGs only in a full run or with SLOTSLOADER_RENDER=1
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
