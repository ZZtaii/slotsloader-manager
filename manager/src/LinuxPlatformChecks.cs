using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace SlotsInstaller
{
    // Generated fixtures only. These checks exercise the Windows/Linux dispatch and
    // refusal boundaries; native /proc, Steam discovery and trash have host-side tests.
    internal static class LinuxPlatformChecks
    {
        private static Dictionary<string, object> Reply()
        { return new Dictionary<string, object> { { "protocol", 1 }, { "ok", true }, { "game_running", false }, { "fluffy_running", false } }; }
        private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
        private static void Refuses(Action action, string message)
        { try { action(); } catch (IOException) { return; } throw new Exception(message); }
        private static void InvokePrivate(ReplaceEngine engine, string name, params object[] args)
        {
            try { typeof(ReplaceEngine).GetMethod(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, null, args.Select(a => a.GetType()).ToArray(), null).Invoke(engine, args); }
            catch (System.Reflection.TargetInvocationException ex) { throw ex.InnerException; }
        }
        private static void RefusePath(string denied)
        {
            LinuxPlatform.TestRequest = delegate(string method, string route, Dictionary<string, object> body)
            {
                if (route == "/contained" && String.Equals((string)body["path"], denied, StringComparison.OrdinalIgnoreCase)) throw new IOException("Fixture native containment refusal");
                var reply = Reply(); if (route == "/validate") reply["path"] = body["path"]; return reply;
            };
        }
        private sealed class NoMoveBin : IRecycleBin
        {
            public string Unavailable(string path, bool folder, long bytes) { return null; }
            public void Recycle(string path, bool folder) { }
            public void DeletePermanently(string path, bool folder) { throw new Exception("Permanent deletion must never be attempted"); }
        }

        internal static int Run(string output)
        {
            string root = Path.Combine(Path.GetFullPath(output), "linux-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(root);
            bool? oldLinux = LinuxPlatform.TestLinux;
            var oldRequest = LinuxPlatform.TestRequest;
            string oldUrl = Environment.GetEnvironmentVariable("SLOTSLOADER_BRIDGE_URL"), oldToken = Environment.GetEnvironmentVariable("SLOTSLOADER_BRIDGE_TOKEN");
            int count = 0, failed = 0;
            var report = new List<string>();
            Action<string, Action> check = delegate(string name, Action action)
            {
                count++;
                try { action(); report.Add("PASS " + name); }
                catch (Exception ex) { failed++; report.Add("FAIL " + name + ": " + ex.Message); }
                Console.WriteLine(report[report.Count - 1]);
            };
            try
            {
                LinuxPlatform.TestLinux = true;
                check("fresh native process state blocks both game and external manager across calls", delegate
                {
                    int calls = 0;
                    LinuxPlatform.TestRequest = delegate(string method, string route, Dictionary<string, object> body)
                    { Require(route == "/status", "wrong endpoint"); calls++; var r = Reply(); r["game_running"] = calls == 1; r["fluffy_running"] = calls == 3; return r; };
                    var probe = new SystemProcessProbe();
                    Require(probe.IsGameRunning(), "native game missed"); Require(!probe.IsGameRunning(), "stale game state"); Require(probe.IsFluffyRunning(), "external process missed"); Require(calls == 3, "status was cached");
                });
                check("launcher lifecycle reports started and exit status exactly once", delegate
                {
                    var events = new List<string>();
                    LinuxPlatform.TestRequest = delegate(string method, string route, Dictionary<string, object> body)
                    { Require(method == "POST" && route == "/session", "wrong lifecycle operation"); events.Add((string)body["event"]); if ((string)body["event"] == "exited") Require((int)body["exit_code"] == 7, "exit code lost"); return Reply(); };
                    LinuxPlatform.Initialize(); LinuxPlatform.Shutdown(7); LinuxPlatform.Shutdown(0);
                    Require(events.SequenceEqual(new[] { "started", "exited" }), "wrong lifecycle events");
                });
                check("unknown, malformed and incompatible native status never authorizes writes", delegate
                {
                    LinuxPlatform.TestRequest = delegate { return new Dictionary<string, object> { { "protocol", 1 }, { "ok", true } }; };
                    Refuses(delegate { LinuxPlatform.RequireClosed("install"); }, "missing status allowed mutation");
                    LinuxPlatform.TestRequest = delegate { var r = Reply(); r["protocol"] = 2; return r; };
                    Refuses(delegate { LinuxPlatform.RequireClosed("install"); }, "new protocol allowed mutation");
                    LinuxPlatform.TestRequest = delegate { var r = Reply(); r["ok"] = false; r["error"] = "Process state unavailable"; return r; };
                    Refuses(delegate { LinuxPlatform.RequireClosed("install"); }, "host refusal allowed mutation");
                });
                check("game-running and unavailable helper preserve pending journal before parsing or recovery", delegate
                {
                    string game = Path.Combine(root, "game"), data = Path.Combine(root, "Data"); Directory.CreateDirectory(game); Directory.CreateDirectory(data);
                    var engine = new ReplaceEngine(game, data, new ReplaceSettings());
                    File.WriteAllText(engine.JournalPath, "fixture journal must remain byte-identical");
                    byte[] before = File.ReadAllBytes(engine.JournalPath);
                    LinuxPlatform.TestRequest = delegate { var r = Reply(); r["game_running"] = true; return r; };
                    Refuses(delegate { engine.Recover(); }, "running game allowed recovery");
                    LinuxPlatform.TestRequest = delegate { throw new IOException("Host unavailable"); };
                    Refuses(delegate { engine.Recover(); }, "unavailable helper allowed recovery");
                    Require(before.SequenceEqual(File.ReadAllBytes(engine.JournalPath)), "journal changed");
                    Require(!Directory.EnumerateFileSystemEntries(game).Any(), "game files were written");
                });
                check("extra-outfit mutation refuses unavailable helper before creating target", delegate
                {
                    string costume = Path.Combine(root, "no-write", "reframework", "costume_mods");
                    LinuxPlatform.TestRequest = delegate { throw new IOException("Host unavailable"); };
                    Refuses(delegate { using (Installer.AcquireMutation(costume)) { } }, "mutation lock allowed unavailable host");
                    Require(!Directory.Exists(Path.Combine(root, "no-write")), "target was created");
                });
                check("background loader metadata sync cannot write with unavailable process state", delegate
                {
                    string game = Path.Combine(root, "metadata-game"); Directory.CreateDirectory(ManagerInstalls.Folder(game));
                    LinuxPlatform.TestRequest = delegate { throw new IOException("Host unavailable"); };
                    Require(!ManagerInstalls.Write(game, new ReplaceState { GameRoot = game }), "metadata sync succeeded without host");
                    SwapRepairFlag.Sync(game, true);
                    Require(!File.Exists(ManagerInstalls.FilePath(game)) && !File.Exists(SwapRepairFlag.FilePath(game)), "background metadata was written");
                });
                check("native discovery and Unix Steam library paths retain spaces and removable-storage paths", delegate
                {
                    LinuxPlatform.TestRequest = delegate(string method, string route, Dictionary<string, object> body)
                    { Require(route == "/discover", "unexpected discovery route"); var r = Reply(); r["game_root"] = @"Z:\run\media\deck\SD Card\steamapps\common\Street Fighter 6"; r["libraries"] = new[] { @"Z:\home\deck\Mods" }; return r; };
                    Require(InstallDetection.FindGame().Contains("SD Card"), "game discovery missing"); Require(InstallDetection.FindLibraries().Single() == @"Z:\home\deck\Mods", "library discovery missing");
                    string vdf = Path.Combine(root, "libraryfolders.vdf"); File.WriteAllText(vdf, "\"libraryfolders\" { \"1\" { \"path\" \"/run/media/deck/SD Card\" } }");
                    Require(InstallDetection.LibraryFolders(vdf).Single() == @"Z:\run\media\deck\SD Card", "Unix library rejected");
                });
                check("selected-root canonicalization is explicit and refuses malformed results", delegate
                {
                    string canonical = Path.Combine(root, "canonical-game");
                    LinuxPlatform.TestRequest = delegate(string method, string route, Dictionary<string, object> body)
                    { Require(route == "/validate" && (string)body["kind"] == "game", "wrong validation"); var r = Reply(); r["path"] = canonical; return r; };
                    Require(LinuxPlatform.CanonicalRoot(@"Z:\home\deck\.steam\steam\game", "game") == canonical, "canonical root not used");
                    LinuxPlatform.TestRequest = delegate { var r = Reply(); r["path"] = "relative"; return r; };
                    Refuses(delegate { LinuxPlatform.CanonicalRoot(canonical, "game"); }, "relative host path accepted");
                });
                check("Linux mount/link checks use native containment and preserve refusal", delegate
                {
                    int calls = 0; string path = Path.Combine(root, "mount", "future", "file.bin");
                    LinuxPlatform.TestRequest = delegate(string method, string route, Dictionary<string, object> body)
                    { Require(route == "/contained" && method == "POST", "native containment skipped"); Require((string)body["path"] == path && (string)body["root"] == Path.GetPathRoot(path), "containment path changed"); calls++; return Reply(); };
                    Safety.NoReparseChain(path); Safety.NoReparse(path); Require(calls == 2, "stale path inspection reused");
                    LinuxPlatform.TestRequest = delegate { var r = Reply(); r["ok"] = false; r["error"] = "A symbolic link is inside the managed path"; return r; };
                    Refuses(delegate { Safety.NoReparseChain(path); }, "contained symbolic link accepted");
                });
                check("native folder reveal and Steam launch use fixed operations", delegate
                {
                    var routes = new List<string>();
                    LinuxPlatform.TestRequest = delegate(string method, string route, Dictionary<string, object> body)
                    { routes.Add(route); if (route == "/open") Require((bool)body["reveal"] && Path.IsPathRooted((string)body["path"]), "reveal request invalid"); return Reply(); };
                    LinuxPlatform.OpenPath(Path.Combine(root, "archive.zip"), true); new SteamLauncher().Launch(root);
                    Require(routes.SequenceEqual(new[] { "/open", "/launch" }), "unexpected native operations");
                });
                check("nonexistent variant target is contained before parent creation", delegate
                {
                    string costume = Path.Combine(root, "variant-no-create", "reframework", "costume_mods"), file = VariantChoices.ChoicesFile(costume);
                    RefusePath(file);
                    Refuses(delegate { VariantChoices.Restore(costume, "Cammy/fixture.zip", new Dictionary<string, bool> { { "One", false } }); }, "variant target refusal ignored");
                    Require(!Directory.Exists(Path.Combine(root, "variant-no-create")), "variant write created a parent before containment");
                });
                check("loader registry and placeholder refuse unsafe targets and temporary files", delegate
                {
                    string game = Path.Combine(root, "loader-guards"), registry = Path.Combine(game, LoaderCostumePak.RegistryRelative);
                    Directory.CreateDirectory(Path.GetDirectoryName(registry)); File.WriteAllText(registry, "{\"fingerprint\":\"keep\"}");
                    RefusePath(registry); Require(!LoaderCostumePak.ClearFingerprint(game) && File.ReadAllText(registry).Contains("keep"), "registry changed after refusal");
                    string pak = Path.Combine(game, "fixture.pak"); File.WriteAllText(pak, "pak sentinel");
                    RefusePath(pak); Refuses(delegate { LoaderCostumePak.WritePlaceholder(pak); }, "placeholder target accepted");
                    RefusePath(pak + ".tmp"); Refuses(delegate { LoaderCostumePak.WritePlaceholder(pak); }, "placeholder temporary path accepted");
                    Require(File.ReadAllText(pak) == "pak sentinel" && !File.Exists(pak + ".tmp"), "placeholder touched a protected path");
                });
                check("PAK hash writes and both rename sides use fresh native containment", delegate
                {
                    string game = Path.Combine(root, "pak-guards"); Directory.CreateDirectory(game);
                    var engine = new ReplaceEngine(game, Path.Combine(root, "pak-guard-data"), new ReplaceSettings());
                    string pak = Path.Combine(game, "re_chunk_000.pak"), target = Path.Combine(game, "moved.pak"); File.WriteAllText(pak, "original PAK bytes");
                    RefusePath(pak); Refuses(delegate { InvokePrivate(engine, "WriteRows", new JournalOp { Pak = "re_chunk_000.pak" }, false); }, "PAK write skipped containment");
                    Refuses(delegate { InvokePrivate(engine, "Move", "re_chunk_000.pak", "moved.pak"); }, "rename source skipped containment");
                    RefusePath(target); Refuses(delegate { InvokePrivate(engine, "Move", "re_chunk_000.pak", "moved.pak"); }, "rename destination skipped containment");
                    Require(File.ReadAllText(pak) == "original PAK bytes" && !File.Exists(target), "PAK changed after refusal");
                });
                check("patch gap repair refuses a nonexistent destination without creating temporary files", delegate
                {
                    string game = Path.Combine(root, "gap-guards"); Directory.CreateDirectory(game); File.WriteAllText(Path.Combine(game, "re_chunk_000.pak.patch_002.pak"), "higher PAK");
                    RefusePath(Path.Combine(game, "re_chunk_000.pak.patch_001.pak"));
                    var engine = new ReplaceEngine(game, Path.Combine(root, "gap-data"), new ReplaceSettings());
                    Refuses(delegate { engine.FillPatchGaps(); }, "gap destination accepted");
                    Require(Directory.GetFiles(game).Length == 1, "gap repair created a file before containment");
                });
                check("failed source-less uninstall retains the only copy across automatic cleanup", delegate
                {
                    RefusePath(null);
                    var fixture = ReplaceChecks.Create(Path.Combine(root, "source-less-trash"), false, ReplaceMethods.Pak);
                    ReplaceMod mod = ReplaceChecks.Install(fixture, "modA", "Cammy Colors");
                    Directory.Delete(mod.SourcePath, true);
                    string message = fixture.Engine.UninstallForGood(mod.Id, DriftChoice.Ask, new NoMoveBin());
                    RemovedReplacement copy = fixture.Engine.ListRemoved().Single();
                    Require(message.Contains("kept at " + copy.Folder) && !message.Contains("were deleted"), "retention message is inaccurate");
                    Require(File.Exists(Path.Combine(copy.Folder, ReplaceEngine.LinuxTrashPendingMarker)), "retention marker missing");
                    Require(fixture.Engine.DeleteRemovedExcept(null) == 0 && Directory.Exists(Path.Combine(copy.Folder, "files")), "automatic cleanup deleted the only copy");
                    Require(fixture.Engine.LoadState().Find(mod.Id) == null, "uninstall did not finish");
                });
                check("leftover PAK stays recoverable when Trash reports success without moving it", delegate
                {
                    RefusePath(null); string game = Path.Combine(root, "leftover-game"), data = Path.Combine(root, "leftover-data"); Directory.CreateDirectory(game);
                    var engine = new ReplaceEngine(game, data, new ReplaceSettings());
                    string copy = Path.Combine(engine.LeftoverCopyDir, "fixture"), pak = Path.Combine(copy, "re_chunk_000.pak.patch_001.pak"); Directory.CreateDirectory(copy); File.WriteAllText(pak, "retained PAK");
                    Refuses(delegate { engine.DiscardLeftoverCopy(copy, new NoMoveBin()); }, "leftover trash failure ignored");
                    Require(File.ReadAllText(pak) == "retained PAK", "leftover PAK permanently deleted");
                });
                check("failed Linux trash retains source and never falls back to permanent deletion", delegate
                {
                    string file = Path.Combine(root, "keep-source.zip"); File.WriteAllText(file, "fixture source");
                    LinuxPlatform.TestRequest = delegate(string method, string route, Dictionary<string, object> body) { if (route == "/trash") throw new IOException("Trash unavailable"); return Reply(); };
                    var bin = new LinuxRecycleBin(); Require(bin.Unavailable(file, false, 14) == null, "trash preflight unexpectedly destructive");
                    Refuses(delegate { bin.Recycle(file, false); }, "trash failure swallowed");
                    Refuses(delegate { bin.DeletePermanently(file, false); }, "permanent deletion allowed");
                    Require(File.ReadAllText(file) == "fixture source", "source lost");
                });
                check("Linux key is session-only, preserves pre-existing key file and supports explicit environment fallback", delegate
                {
                    string data = Path.Combine(root, "assistant"); Directory.CreateDirectory(data);
                    string file = Path.Combine(data, AssistantKeyStore.FileName); File.WriteAllText(file, "fixture encrypted file");
                    var key = new AssistantKeyStore(data, delegate { return "FAKE-ENV-KEY-12345678"; }); string source;
                    Require(key.Load(out source) == "FAKE-ENV-KEY-12345678" && source == "environment", "read existing DPAPI file under Wine");
                    key.Save("FAKE-SESSION-KEY-12345678"); Require(key.Load(out source) == "FAKE-SESSION-KEY-12345678" && source == "session", "session key missing");
                    Require(File.ReadAllText(file) == "fixture encrypted file", "saved key file overwritten"); key.Remove();
                    Require(!key.HasSavedKey && File.ReadAllText(file) == "fixture encrypted file", "remove touched protected file");
                });
                check("Windows self-update cannot overwrite Linux integration", delegate
                {
                    var result = new UpdateCheckResult { Ok = true, Manager = new ReleaseOffer { Version = "99.0.0" } };
                    Require(UpdateCheck.ManagerUpdate(result, new Version(1, 0, 0, 0)) == null, "Windows release offered");
                    string app = Path.Combine(root, "untouched-app");
                    Refuses(delegate { AppUpdater.Apply(app, root, null, "1", "2", root); }, "Windows updater allowed");
                    Require(!Directory.Exists(app), "updater touched app");
                });
                check("missing or non-loopback bridge configuration fails before network access", delegate
                {
                    LinuxPlatform.TestRequest = null;
                    Environment.SetEnvironmentVariable("SLOTSLOADER_BRIDGE_URL", "https://example.invalid/"); Environment.SetEnvironmentVariable("SLOTSLOADER_BRIDGE_TOKEN", new string('x', 64));
                    Refuses(delegate { LinuxPlatform.Status(); }, "remote bridge allowed");
                    Environment.SetEnvironmentVariable("SLOTSLOADER_BRIDGE_URL", "http://127.0.0.1:1/"); Environment.SetEnvironmentVariable("SLOTSLOADER_BRIDGE_TOKEN", null);
                    Refuses(delegate { LinuxPlatform.Status(); }, "missing bridge token allowed");
                });
                check("HTTP bridge sends authentication, parses bounded JSON and refuses protocol errors", delegate
                {
                    var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
                    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                    string token = new string('t', 64); Exception serverError = null; int authenticated = 0;
                    var server = new Thread(delegate()
                    {
                        try
                        {
                            for (int i = 0; i < 3; i++)
                                using (TcpClient client = listener.AcceptTcpClient())
                                using (NetworkStream stream = client.GetStream())
                                using (var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true))
                                {
                                    client.ReceiveTimeout = client.SendTimeout = 4000;
                                    string first = reader.ReadLine(); Require(first != null && first.StartsWith("GET /status "), "wrong HTTP request");
                                    string line; bool accepted = false;
                                    while (!String.IsNullOrEmpty(line = reader.ReadLine())) if (line.Equals("X-Slotsloader-Token: " + token, StringComparison.OrdinalIgnoreCase)) accepted = true;
                                    if (accepted) authenticated++;
                                    string body = i == 0 ? "{\"protocol\":1,\"ok\":true,\"game_running\":false,\"fluffy_running\":true}" : i == 1 ? "{\"protocol\":2,\"ok\":true,\"game_running\":false,\"fluffy_running\":false}" : "{\"protocol\":1,\"ok\":false,\"error\":\"Reconnect the game drive\"}";
                                    byte[] response = Encoding.ASCII.GetBytes("HTTP/1.1 " + (i == 2 ? "409 Conflict" : "200 OK") + "\r\nContent-Type: application/json\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n" + body);
                                    stream.Write(response, 0, response.Length); stream.Flush();
                                }
                        }
                        catch (Exception ex) { serverError = ex; }
                    }) { IsBackground = true };
                    try
                    {
                        server.Start(); LinuxPlatform.TestRequest = null;
                        Environment.SetEnvironmentVariable("SLOTSLOADER_BRIDGE_URL", "http://127.0.0.1:" + port + "/"); Environment.SetEnvironmentVariable("SLOTSLOADER_BRIDGE_TOKEN", token);
                        Require(LinuxPlatform.Status().FluffyRunning, "HTTP status not parsed");
                        Refuses(delegate { LinuxPlatform.Status(); }, "HTTP incompatible protocol accepted");
                        string refusal = null; try { LinuxPlatform.Status(); } catch (IOException ex) { refusal = ex.Message; }
                        Require(refusal != null && refusal.Contains("Reconnect the game drive"), "actionable HTTP refusal discarded");
                        Require(server.Join(4000), "HTTP server did not finish");
                        if (serverError != null) throw serverError;
                        Require(authenticated == 3, "authentication header missing");
                    }
                    finally { listener.Stop(); if (server.IsAlive) server.Join(1000); }
                });
                check("Windows mode leaves existing process and update contracts unchanged", delegate
                {
                    LinuxPlatform.TestLinux = false; LinuxPlatform.TestRequest = delegate { throw new Exception("Windows invoked Linux helper"); };
                    LinuxPlatform.RequireClosed("fixture"); Require(LinuxPlatform.CanonicalRoot(root, "path") == root, "Windows path changed");
                    var result = new UpdateCheckResult { Ok = true, Manager = new ReleaseOffer { Version = "99.0.0" } };
                    Require(UpdateCheck.ManagerUpdate(result, new Version(1, 0, 0, 0)) == result.Manager, "Windows updater suppressed");
                });
            }
            finally
            {
                LinuxPlatform.TestLinux = oldLinux; LinuxPlatform.TestRequest = oldRequest;
                Environment.SetEnvironmentVariable("SLOTSLOADER_BRIDGE_URL", oldUrl); Environment.SetEnvironmentVariable("SLOTSLOADER_BRIDGE_TOKEN", oldToken);
            }
            report.Add(count + " checks, " + failed + " failures");
            File.WriteAllLines(Path.Combine(root, "linux-check.txt"), report, new UTF8Encoding(false));
            Console.WriteLine(report[report.Count - 1]); return failed == 0 ? 0 : 1;
        }
    }
}
