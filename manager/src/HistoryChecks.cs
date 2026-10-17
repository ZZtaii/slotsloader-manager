using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;

namespace SlotsInstaller
{
    // 2.4.1 install/usage history: test.ps1 suite "history" (--history-check <dir>) on generated fixtures, and
    // "history-real" (--history-real-check <dir> <game>, -RealArchives) reading the user's SlotsLoader log read-only.
    internal static class HistoryChecks
    {
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        private static T Get<T>(object o, string name) { return (T)o.GetType().GetField(name, PrivateInstance).GetValue(o); }
        private static void Set(object o, string name, object value) { o.GetType().GetField(name, PrivateInstance).SetValue(o, value); }
        private static object Call(object o, string name, params object[] args) { return o.GetType().GetMethod(name, PrivateInstance).Invoke(o, args); }
        private static List<HistoryEvent> Events(string dir, string kind) { HistoryStore s = HistoryStore.For(dir); s.Reload(); return s.Events().Where(e => kind == null || e.Kind == kind).ToList(); }
        private static string Kinds(string dir) { return String.Join(",", Events(dir, null).Select(e => e.Kind)); }
        private static readonly List<string> Report = new List<string>();
        private static void Note(string line) { Report.Add("NOTE " + line); Console.WriteLine("NOTE " + line); }

        internal static int Run(string output)
        {
            string root = Path.GetFullPath(output); Safety.NoReparseChain(root);
            string run = Path.Combine(root, "history-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(run); Console.WriteLine("History fixture root: " + run);
            IProcessProbe previousProbe = GameProcesses.Probe; var probe = new FakeProcessProbe(); GameProcesses.Probe = probe;
            string previousDir = HistoryStore.DefaultDir; long previousCap = HistoryStore.CapBytes;
            bool previousHeadless = Program.Headless, previousBackfill = MainForm.HistoryAutoBackfill;
            Program.Headless = true; MainForm.HistoryAutoBackfill = false;
            int failures = 0, count = 0; Report.Clear();
            Action<string, string, Action<string>> check = delegate(string name, string folder, Action<string> action)
            {
                count++; probe.Game = false; probe.Fluffy = false; ReplaceEngine.CrashAfterStep = -1; HistoryStore.CapBytes = previousCap;
                string dir = Path.Combine(run, folder); Directory.CreateDirectory(dir);
                HistoryStore.DefaultDir = Path.Combine(dir, "appdata");
                try { action(dir); Console.WriteLine("PASS " + name); Report.Add("PASS " + name); }
                catch (Exception ex) { failures++; Console.WriteLine("FAIL " + name + ": " + ex); Report.Add("FAIL " + name + ": " + ex.Message); }
                finally { ReplaceEngine.CrashAfterStep = -1; }
            };
            try
            {
                check("event format: one JSON object per line (UTC time, kind, mode, source + identity stamp, name, character/slot or replacement id, options); unknown and half-written lines are skipped; the file is only appended", "format", Format);
                check("replacement events at the engine: install, switch (same source in place: prev_id + prev_options), disable/enable, priority move, uninstall, restore of a removed copy, restore original game files, tool mods, journal recovery of an interrupted change", "replace", ReplaceEvents);
                check("extra outfit events at the store: install, update (same name, other options), disable/enable, uninstall, removed-set uninstall, Undo's restore (recover), preset restore (install); deleted sources keep their rows", "outfits", OutfitEvents);
                check("Undo writes its own event and never deletes history; a failing history write never fails the change", "undo", UndoEvent);
                check("probe sessions (FakeProcessProbe through the window's PollGame): start records the active set (enabled extra outfits + enabled replacements and tool mods), stop the duration; closing the Manager while SF6 runs ends it; one launch seen by the probe and in Slotsloader's log counts once", "sessions", Sessions);
                check("Slotsloader log backfill: fixture log with several runs (built, up to date, a slot left out, variants, an ignored add-on, a manual archive, a stray folder); slots mapped to installed outfits through the history and the current manifests; replacements enabled at the time count; incremental by byte offset; a half-written run waits; a rewritten (truncated) log restarts without recording a launch twice; the log is never written", "backfill", Backfill);
                check("compaction past the size cap: the newest events are kept, older ones fold into one summary line per mod plus one for launches; every per-mod total (plays, last played, install dates, installed and enabled state, event counts) and the launch/slot totals are identical before and after; a launch is never split; uninstalled and deleted sources keep their rows", "compact", Compaction);
                check("Last used sort from history: last played, else the last install or switch, else the older install records; the sort order and the row meta text (\"played 3d ago\" / \"installed 12 Oct\"); 2,667 lookups without file access", "lastused", LastUsedSort);
                check("get_history (Assistant): strict schema (every property required, \"\" / 0 = default), filters by character, kind, install state, not_used_days, used_within_days and query, orders, current installs predating the history, ids for the actions, no local paths; tool count 17", "assistant", AssistantTool);
                check("History view: reachable from the Installed page (History icon button) and gear → History; timeline rows (played, installs, switches, uninstalls, enable/disable, moves, undo); filters by mod (Installed-page matching), character and kind; the × clear; empty state; no tooltips; fits at 1232 and 1110 logical; render at the device DPI", "view", View);
            }
            finally
            {
                GameProcesses.Probe = previousProbe; HistoryStore.DefaultDir = previousDir; HistoryStore.CapBytes = previousCap;
                Program.Headless = previousHeadless; MainForm.HistoryAutoBackfill = previousBackfill;
            }
            Console.WriteLine("History checks: " + (count - failures) + " passed, " + failures + " failed.");
            try { File.WriteAllLines(Path.Combine(run, "history-checks.txt"), Report); Directory.CreateDirectory(AppConfig.DataDir); File.WriteAllLines(Path.Combine(AppConfig.DataDir, "history-checks.txt"), Report); } catch { }
            return failures == 0 ? 0 : 1;
        }

        // ---- format
        private static void Format(string dir)
        {
            string data = Path.Combine(dir, "data");
            string zip = Path.Combine(dir, "Cammy Boots.zip"); File.WriteAllText(zip, "zip bytes");
            DateTime t = new DateTime(2026, 10, 1, 8, 30, 0, DateTimeKind.Utc);
            History.Record(data, new HistoryEvent { Utc = t, Kind = HistoryKinds.Install, Mode = HistoryModes.Extra, Source = zip, Stamp = History.Stamp(zip), Name = "Boots", Mod = "Cammy Boots", Character = "Cammy", Slot = "Cammy/Boots", Id = "abc", Options = new List<string> { "", "Color/Red" } });
            string file = Path.Combine(data, HistoryStore.FileName);
            string first = File.ReadAllLines(file)[0];
            Require(first.StartsWith("{\"v\":1,\"t\":\"2026-10-01T08:30:00.0000000Z\",\"kind\":\"install\",\"mode\":\"extra_outfit\",\"source\":") && first.Contains("\"stamp\":\"9:") && first.Contains("\"slot\":\"Cammy/Boots\"") && first.Contains("\"options\":[\"\",\"Color/Red\"]") && !first.Contains("null"), "line: " + first);
            Note("example line: " + first.Replace(zip.Replace("\\", "\\\\"), "<library>\\\\Cammy Boots.zip"));
            File.AppendAllText(file, "not json\n{\"kind\":\"install\"}\n");
            History.Record(data, new HistoryEvent { Utc = t.AddMinutes(1), Kind = HistoryKinds.SessionStart, Mods = new List<HistoryMod> { new HistoryMod { Source = zip, Name = "Boots", Mode = HistoryModes.Extra, Character = "Cammy", Slot = "Cammy/Boots" } } });
            File.AppendAllText(file, "{\"v\":1,\"t\":\"2026-10-01T09:00:00Z\",\"kind\":\"uninst");   // half-written
            HistoryStore s = HistoryStore.For(data); s.Reload();
            List<HistoryEvent> all = s.Events();
            Require(all.Count == 2 && s.BadLines == 2 && all[0].Options.Count == 2 && all[0].Options[0] == "" && all[0].Stamp == History.Stamp(zip) && all[1].Mods.Count == 1 && all[1].Mods[0].Slot == "Cammy/Boots", "read back: " + all.Count + " events, " + s.BadLines + " bad");
            File.AppendAllText(file, "all\",\"name\":\"Boots\",\"source\":\"" + zip.Replace("\\", "\\\\") + "\",\"mode\":\"extra_outfit\",\"slot\":\"Cammy/Boots\"}\n");
            s.Reload();
            Require(s.Events().Count == 3 && s.Index().Find(History.Normalize(zip)).Plays == 1 && !s.Index().Find(History.Normalize(zip)).Installed, "a completed line is read once it ends: " + Kinds(data));
            long before = new FileInfo(file).Length;
            History.Record(data, new HistoryEvent { Kind = HistoryKinds.Install, Mode = HistoryModes.Extra, Source = zip, Name = "Boots", Character = "Cammy", Slot = "Cammy/Boots" });
            Require(new FileInfo(file).Length > before && File.ReadAllText(file).StartsWith(first), "append-only");
            // A write that cannot happen (a folder where the file should be) is logged, not thrown.
            string blocked = Path.Combine(dir, "blocked"); Directory.CreateDirectory(Path.Combine(blocked, HistoryStore.FileName));
            History.Record(blocked, new HistoryEvent { Kind = HistoryKinds.Install, Name = "x" });
        }

        // ---- replacements
        private static void ReplaceEvents(string dir)
        {
            ReplaceChecks.Fixture f = ReplaceChecks.Create(Path.Combine(dir, "game"), false, ReplaceMethods.Pak);
            ReplaceMod a = ReplaceChecks.Install(f, "modA", "Cammy Colors");
            ReplacePlan plan; ReplaceMod a2 = ReplaceChecks.Install(f, "modA", "Cammy Colors", false, a.Id, out plan);
            f.Engine.SetEnabled(a2.Id, false); f.Engine.SetEnabled(a2.Id, true);
            ReplaceMod b = ReplaceChecks.Install(f, "modB", "Cammy Alt");
            f.Engine.Move(b.Id, 1);
            string folder; f.Engine.Uninstall(a2.Id, true, out folder);
            ReplaceMod back; f.Engine.RestoreRemoved(folder, out back);
            ReplaceMod tool = ReplaceChecks.Install(f, "tool", "Tool Pack");
            f.Engine.RestoreAll();
            string kinds = Kinds(f.Data);
            Require(kinds == "install,update,disable,enable,install,reorder,uninstall,recover,install,uninstall,uninstall,uninstall", "replacement kinds: " + kinds);
            List<HistoryEvent> all = Events(f.Data, null);
            HistoryEvent update = all[1];
            Require(update.Id == a2.Id && update.PrevId == a.Id && update.PrevOptions != null && update.Source == History.Normalize(f.Mod("modA")) && update.Mode == HistoryModes.Replace && update.Character == "Cammy" && update.Stamp == "folder", "update event: " + HistoryJson.Write(update));
            Require(all[5].Detail == "#1 → #2" && all[5].Id == b.Id, "reorder: " + HistoryJson.Write(all[5]));
            Require(all[8].Mode == HistoryModes.Tool && all.Skip(9).All(e => e.Detail == "Restore original game files"), "tool mod / restore all");
            HistoryIndex index = HistoryStore.For(f.Data).Index();
            ModHistory ha = index.Find(History.Normalize(f.Mod("modA")));
            Require(ha != null && !ha.Installed && ha.LastUpdated.HasValue && ha.FirstInstalled == all[0].Utc && ha.LastUninstalled == all[11].Utc && ha.Events == 7, "modA history: installed " + (ha == null ? "?" : ha.Installed + " events " + ha.Events));
            // Journal recovery: a change interrupted after a few steps, rolled back at the next start.
            ReplaceEngine.CrashAfterStep = 2; bool crashed = false;
            try { ReplaceChecks.Install(f, "modB", "Cammy Alt"); } catch (ReplaceEngine.SimulatedCrashException) { crashed = true; }
            ReplaceEngine.CrashAfterStep = -1;
            Require(crashed && f.Engine.JournalPending, "no simulated crash");
            f.Engine.Recover();
            HistoryEvent recovered = Events(f.Data, null).Last();
            Require(recovered.Kind == HistoryKinds.Recover && recovered.Via == "journal" && recovered.Name == "Install Cammy Alt" && recovered.Detail.Contains("rolled back"), "journal recovery: " + HistoryJson.Write(recovered));
            Require(Events(f.Data, HistoryKinds.Install).Count == 3, "the interrupted install was recorded as an install");
        }


        // ---- extra outfits (Installer and OutfitSetStore write the app's Data history: HistoryStore.DefaultDir here)
        private static void OutfitEvents(string dir)
        {
            AssistantFixture fx = AssistantFixture.Create(Path.Combine(dir, "fx"));
            string data = HistoryStore.DefaultDir, root = fx.CostumeRoot;
            Require(Kinds(data) == "install", "fixture Boots install: " + Kinds(data));
            ModPackage juri = PackageReader.Load(fx.JuriPack, "");
            Installer.Install(Composer.Plan(juri, new[] { "Main Files", "Color/Gold" }), root, "Juri", "Gold", "", false);
            Installer.Install(Composer.Plan(juri, new[] { "Main Files", "Color/Pink" }), root, "Juri", "Gold", "", true);
            InstalledOutfit gold = Installer.ListInstalled(root).Single(o => o.Name == "Gold");
            Installer.SetEnabled(gold, root, false);
            gold = Installer.ListInstalled(root).Single(o => o.Name == "Gold");
            Installer.SetEnabled(gold, root, true);
            gold = Installer.ListInstalled(root).Single(o => o.Name == "Gold");
            Installer.Uninstall(gold, root);
            InstalledOutfit boots = Installer.ListInstalled(root).Single(o => o.Name == "Boots");
            OutfitSetInfo removed = OutfitSetStore.RemoveOutfits(root, new[] { boots }, OutfitSetStore.RemovedSetName(DateTime.Now));
            OutfitSetStore.Restore(root, removed);
            OutfitSetInfo preset = OutfitSetStore.CreatePreset(root, "Weekend");
            boots = Installer.ListInstalled(root).Single(o => o.Name == "Boots");
            Installer.Uninstall(boots, root);
            OutfitSetStore.Restore(root, OutfitSetStore.List(root).Entries.Single(e => e.Kind == OutfitSetKind.Preset));
            string kinds = Kinds(data);
            Require(kinds == "install,install,update,disable,enable,uninstall,uninstall,recover,uninstall,install", "outfit kinds: " + kinds);
            List<HistoryEvent> all = Events(data, null);
            HistoryEvent update = all[2];
            Require(update.Slot == "Juri/Gold" && update.Character == "Juri" && update.Mod == "Juri Barefoot Variants" && update.Options.Contains("Color/Pink") && update.PrevOptions.Contains("Color/Gold") && update.Stamp.Contains(":"), "update: " + HistoryJson.Write(update));
            Require(all[7].Slot == "Cammy/Boots" && all[7].Source == History.Normalize(fx.CammyBoots) && all[9].Detail == "from preset “Weekend”", "recover/preset: " + HistoryJson.Write(all[7]) + " / " + HistoryJson.Write(all[9]));
            // The manual Juri folder in the removed set path: recorded without a source, by its folder.
            InstalledOutfit manual = Installer.ListInstalled(root).Single(o => !o.Managed);
            OutfitSetStore.RemoveOutfits(root, new[] { manual }, OutfitSetStore.RemovedSetName(DateTime.Now));
            HistoryEvent m = Events(data, null).Last();
            Require(m.Kind == HistoryKinds.Uninstall && m.Source == "" && m.Slot == "Juri/Manual Juri", "manual outfit: " + HistoryJson.Write(m));
            // Deleting the source keeps its rows.
            File.Delete(fx.JuriPack);
            HistoryStore.For(data).Reload();
            ModHistory h = HistoryStore.For(data).Index().Find(History.Normalize(fx.JuriPack));
            Require(h != null && h.Events == 5 && !h.Installed && h.LastUpdated.HasValue, "deleted source row");
            ModHistory hb = HistoryStore.For(data).Index().Find(History.Normalize(fx.CammyBoots));
            Require(hb != null && hb.Installed && hb.Enabled, "Boots installed again by the preset");
        }

        private static MainForm Window(AppConfig config)
        {
            var form = new MainForm(config);
            Set(form, "firstLoad", false); form.ShowInTaskbar = false; form.Opacity = 0; form.Show(); Application.DoEvents();
            return form;
        }

        private static void UndoEvent(string dir)
        {
            string game = Path.Combine(dir, "game"); Directory.CreateDirectory(Path.Combine(game, "reframework", "costume_mods"));
            var config = new AppConfig { GameRoot = game, Libraries = new List<string>(), SevenZipPath = "" };
            string data = HistoryStore.DefaultDir;
            // 1.0.1: uninstall has no Undo any more; a Disable does (and Restore original game files).
            History.Record(data, new HistoryEvent { Kind = HistoryKinds.Disable, Mode = HistoryModes.Replace, Name = "Boots", Character = "Cammy", Source = Path.Combine(dir, "boots.zip") });
            using (MainForm form = Window(config))
            {
                int ran = 0;
                Call(form, "OfferUndo", "Disabled Boots", (Action)delegate { ran++; }, (Func<string>)null);
                form.PerformUndo();
                List<HistoryEvent> all = Events(data, null);
                Require(ran == 1 && all.Count == 2 && all[1].Kind == HistoryKinds.Undo && all[1].Name == "Disabled Boots" && all[0].Kind == HistoryKinds.Disable, "undo event: " + Kinds(data));
                form.Close();
            }
        }

        private static void Sessions(string dir)
        {
            AssistantFixture fx = AssistantFixture.Create(Path.Combine(dir, "fx"));
            string data = HistoryStore.DefaultDir;
            var probe = (FakeProcessProbe)GameProcesses.Probe;
            using (MainForm form = Window(fx.Config))
            {
                Set(form, "installed", Installer.ListInstalled(fx.CostumeRoot));
                var rep = new ReplaceMod { Id = "r1", Name = "Cammy Alt", SourcePath = Path.Combine(fx.Library, "Cammy Alt.zip"), Kind = "Replacement", Enabled = true, Affects = "Cammy Outfit 2", Files = new List<ModFile> { new ModFile { Path = "natives/stm/product/model/esf/esf009/002/x.mesh" } } };
                var off = new ReplaceMod { Id = "r2", Name = "Disabled Tool", SourcePath = Path.Combine(fx.Library, "Tool.zip"), Kind = "Tool", Enabled = false };
                Set(form, "gameRows", new List<GameModRow> { new GameModRow { Kind = "Replacement", Mod = rep }, new GameModRow { Kind = "Tool", Mod = off } });
                probe.Game = true; Call(form, "PollGame");
                HistoryEvent start = Events(data, HistoryKinds.SessionStart).Single();
                Require(start.Via == "probe" && start.Mods.Count == 3 && start.Mods.Any(m => m.Slot == "Cammy/Boots" && m.Source == History.Normalize(fx.CammyBoots)) && start.Mods.Any(m => m.Slot == "Juri/Manual Juri" && m.Source == "") && start.Mods.Any(m => m.Name == "Cammy Alt" && m.Mode == HistoryModes.Replace && m.Character == "Cammy") && !start.Mods.Any(m => m.Name == "Disabled Tool"), "session start: " + HistoryJson.Write(start));
                Call(form, "PollGame");
                Require(Events(data, HistoryKinds.SessionStart).Count == 1, "a second poll while running started another session");
                probe.Game = false; Call(form, "PollGame");
                HistoryEvent stop = Events(data, HistoryKinds.SessionStop).Single();
                Require(stop.Via == "probe" && stop.Start.HasValue && stop.Detail == "under a minute", "session stop: " + HistoryJson.Write(stop));
                HistoryIndex index = HistoryStore.For(data).Index();
                Require(index.Launches == 1 && index.Find(History.Normalize(fx.CammyBoots)).Plays == 1 && index.Find(History.Normalize(rep.SourcePath)).Plays == 1 && index.Find(History.Normalize(off.SourcePath)) == null, "session plays");
                probe.Game = true; Call(form, "PollGame"); form.Close();   // closing while SF6 runs
                Require(Events(data, HistoryKinds.SessionStop).Last().Via == "manager_closed", "closing the Manager while SF6 ran did not end the session");
                probe.Game = false;
            }
            // One launch seen by both: the probe start and SlotsLoader's run 40 s later count once; mods are united.
            string merge = Path.Combine(dir, "merge");
            DateTime t = DateTime.UtcNow.AddDays(-2);
            History.Record(merge, new HistoryEvent { Utc = t, Kind = HistoryKinds.SessionStart, Via = "probe", Mods = new List<HistoryMod> { new HistoryMod { Source = "C:\\lib\\a.zip", Name = "A", Mode = HistoryModes.Extra, Slot = "Cammy/A" } } });
            History.Record(merge, new HistoryEvent { Utc = t.AddSeconds(40), Kind = HistoryKinds.Launch, Via = "loader_log", Slots = 3, Mods = new List<HistoryMod> { new HistoryMod { Source = "C:\\lib\\a.zip", Name = "A", Mode = HistoryModes.Extra, Slot = "Cammy/A" }, new HistoryMod { Source = "C:\\lib\\b.zip", Name = "B", Mode = HistoryModes.Extra, Slot = "Cammy/B" } } });
            History.Record(merge, new HistoryEvent { Utc = t.AddMinutes(30), Kind = HistoryKinds.Launch, Via = "loader_log", Slots = 2, Mods = new List<HistoryMod> { new HistoryMod { Source = "C:\\lib\\b.zip", Name = "B", Mode = HistoryModes.Extra, Slot = "Cammy/B" } } });
            HistoryIndex mi = HistoryStore.For(merge).Index();
            Require(mi.Launches == 2 && mi.SlotUses == 5 && mi.Find("C:\\lib\\a.zip").Plays == 1 && mi.Find("C:\\lib\\b.zip").Plays == 2 && mi.Plays[0].Probe && mi.Plays[0].Log, "merge: launches " + mi.Launches + ", slot uses " + mi.SlotUses);
        }

        // ---- loader log
        internal static string LogRun(DateTime local, string body)
        {
            string ts = "[" + local.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "] ";
            var sb = new StringBuilder();
            sb.Append(ts + "=== SF6 Costume Slot Loader ===\n" + ts + "Loading vanilla index...\n" + ts + "Scanning patch paks...\n");
            foreach (string line in body.Replace("\r", "").Split('\n')) { if (line.StartsWith("~")) sb.Append(line.Substring(1) + "\n"); else if (line.Length > 0) sb.Append(ts + line + "\n"); }
            return sb.ToString();
        }
        internal static string Built(DateTime local, bool leftOut)
        {
            string body =
                "\n~Scanning costume_mods in X:\\game\\reframework\\costume_mods...\n" +
                "\n~  folder: Cammy/Boots\n    3 costume files\n    mod_id: 1\n    name: Boots\n    -> esf009 costume 1 (folder 002): 3 files\n" +
                "\n~  archive: Juri/Juri Summer.zip\n    archive: Juri Summer.zip (already extracted)\n  option: Juri/Juri Summer.zip/Main\n    2 costume files\n    name: Juri Summer\n    -> esf016 costume 0 (folder 001): 2 files\n" +
                "  option: Juri/Juri Summer.zip/Hair\n    1 costume files\n    name: Optional - Hair  (add-on for Juri Summer)\n  partial option: esf016/001 (no body mesh in part 01, 1 files)\n" +
                "\n~  folder: Cammy/Hair Only\n    1 costume files\n    name: Hair Only\n  partial option: esf009/001\n" +
                "\n~  folder: Luke/Leon\n    5 costume files\n    name: Leon as Luke\n    -> esf002 costume 0 (folder 001): 5 files\n" +
                "\n~  folder: natives/stray\n    1 costume files\n    name: stray\n    -> esf009 costume 0 (folder 001): 1 files\n" +
                "\n~  variant: Juri Summer + Optional - Hair\n    -> esf016 costume 0 (folder 001): 3 files\n" +
                "\n~  add-on Cammy/Hair Only: no costume it can complete was found, ignored\n" +
                "\n~Assigning slots...\n  slot: esf009/v05 -> folder 006, record 1, \"Outfit I\"\n" +
                "\n~Assembling output pak (minimal)...\n    slot esf009/006 (v05, Outfit I): 3 files, 1 patched\n    slot esf016/006 (v05, Outfit I): 2 files, 1 patched\n    slot esf016/007 (v06, Outfit II): 3 files, 1 patched\n    slot esf002/006 (v05, Outfit I): 5 files, 1 patched\n" +
                "\n~Checking slots...\n" + (leftOut ? "  WARN: slot esf002/v05 (Outfit I, from Leon as Luke) left out, the game could not load it: texture missing\n" : "") +
                "  registry saved\nDone in 12.5 ms\n~";
            return LogRun(local, body);
        }
        internal static string UpToDate(DateTime local) { return LogRun(local, "Up to date (fingerprint match, re_chunk_000.pak.patch_001.pak exists) in 35.0 ms"); }
        private static string Hash(string file) { using (var sha = SHA256.Create()) using (var s = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) return BitConverter.ToString(sha.ComputeHash(s)); }

        private static void Backfill(string dir)
        {
            string game = Path.Combine(dir, "game"), data = Path.Combine(dir, "data"), costume = Path.Combine(game, "reframework", "costume_mods");
            Directory.CreateDirectory(costume);
            DateTime now = DateTime.UtcNow, d1 = now.AddDays(-20).ToLocalTime(), d2 = now.AddDays(-15).ToLocalTime(), d3 = now.AddDays(-10).ToLocalTime(), d4 = now.AddDays(-5).ToLocalTime(), d5 = now.AddDays(-1).ToLocalTime();
            d1 = d1.AddTicks(-(d1.Ticks % TimeSpan.TicksPerSecond)); d2 = d2.AddTicks(-(d2.Ticks % TimeSpan.TicksPerSecond)); d3 = d3.AddTicks(-(d3.Ticks % TimeSpan.TicksPerSecond)); d4 = d4.AddTicks(-(d4.Ticks % TimeSpan.TicksPerSecond)); d5 = d5.AddTicks(-(d5.Ticks % TimeSpan.TicksPerSecond));
            // Boots was installed through the Manager 25 days ago (history); Leon predates the history (manifest only, 30 days).
            string boots = "C:\\Mods\\Cammy Boots.zip", leon = "C:\\Mods\\Leon as Luke.zip", alt = "C:\\Mods\\Cammy Alt.zip";
            History.Record(data, new HistoryEvent { Utc = now.AddDays(-25), Kind = HistoryKinds.Install, Mode = HistoryModes.Extra, Source = boots, Name = "Boots", Character = "Cammy", Slot = "Cammy/Boots" });
            History.Record(data, new HistoryEvent { Utc = now.AddDays(-12), Kind = HistoryKinds.Install, Mode = HistoryModes.Replace, Source = alt, Name = "Cammy Alt", Character = "Cammy", Id = "alt1", Slot = "Cammy Outfit 2" });
            History.Record(data, new HistoryEvent { Utc = now.AddDays(-3), Kind = HistoryKinds.Disable, Mode = HistoryModes.Replace, Source = alt, Name = "Cammy Alt", Character = "Cammy", Id = "alt1", Slot = "Cammy Outfit 2" });
            var installed = new List<InstalledOutfit> { new InstalledOutfit { Folder = Path.Combine(costume, "Luke", "Leon"), Name = "Leon", Character = "Luke", Managed = true, Enabled = true, SourcePath = leon, InstalledUtc = now.AddDays(-30) } };
            var tool = new ReplaceMod { Id = "t1", Name = "Old Tool", SourcePath = "C:\\Mods\\Old Tool.zip", Kind = "Tool", Enabled = true, InstalledUtc = now.AddDays(-40).ToString("o") };
            var replacements = new List<ReplaceMod> { tool, new ReplaceMod { Id = "alt1", Name = "Cammy Alt", SourcePath = alt, Kind = "Replacement", Enabled = false, InstalledUtc = now.AddDays(-12).ToString("o") } };
            string log = Path.Combine(game, LoaderLogBackfill.LogName);
            File.WriteAllText(log, "old header line from another tool\n" + Built(d1, false) + UpToDate(d2), new UTF8Encoding(false));
            File.SetLastWriteTimeUtc(log, now.AddHours(-1));
            string hash = Hash(log); DateTime written = File.GetLastWriteTimeUtc(log);
            LoaderLogResult r1 = LoaderLogBackfill.Run(game, data, installed, replacements, now);
            Require(Hash(log) == hash && File.GetLastWriteTimeUtc(log) == written, "the log was written");
            List<HistoryEvent> launches = Events(data, HistoryKinds.Launch);
            Require(r1.Runs == 2 && r1.Recorded == 2 && launches.Count == 2 && launches[0].Slots == 4 && launches[1].Slots == 4 && launches[1].Detail == "up to date", "first read: " + r1.Summary + " slots " + String.Join("/", launches.Select(l => l.Slots)));
            HistoryEvent l1 = launches[0];
            var slots = l1.Mods.Where(m => m.Mode == HistoryModes.Extra).Select(m => m.Slot).ToList();
            Require(slots.SequenceEqual(new[] { "Cammy/Boots", "Juri/Juri Summer.zip", "Luke/Leon" }), "built outfits: " + String.Join(", ", slots));
            Require(l1.Mods.Single(m => m.Slot == "Cammy/Boots").Source == History.Normalize(boots) && l1.Mods.Single(m => m.Slot == "Luke/Leon").Source == History.Normalize(leon) && l1.Mods.Single(m => m.Slot == "Juri/Juri Summer.zip").Source == "" && l1.Mods.Single(m => m.Slot == "Juri/Juri Summer.zip").Name == "Juri Summer", "slot → outfit mapping: " + HistoryJson.Write(l1));
            Require(l1.Mods.Any(m => m.Name == "Old Tool" && m.Mode == HistoryModes.Tool) && !l1.Mods.Any(m => m.Name == "Cammy Alt"), "replacements at d1: the old tool (predates the history), not Cammy Alt (installed later)");
            Require(r1.Offset == new FileInfo(log).Length, "offset after two complete runs");
            // Appended: a run that left a slot out, then a half-written one.
            string third = Built(d3, true), fourth = Built(d4, false);
            File.AppendAllText(log, third + fourth.Substring(0, fourth.Length / 2), new UTF8Encoding(false));
            LoaderLogResult r2 = LoaderLogBackfill.Run(game, data, installed, replacements, now);
            launches = Events(data, HistoryKinds.Launch);
            Require(r2.Runs == 1 && r2.Recorded == 1 && launches.Count == 3 && launches[2].Slots == 3 && !launches[2].Mods.Any(m => m.Slot == "Luke/Leon") && launches[2].Mods.Any(m => m.Name == "Cammy Alt"), "incremental: " + r2.Summary + " · " + HistoryJson.Write(launches[2]));
            LoaderLogState state = LoaderLogBackfill.LoadState(data);
            Require(state.Offset == Encoding.UTF8.GetByteCount("old header line from another tool\n" + Built(d1, false) + UpToDate(d2) + third), "the half-written run must wait: offset " + state.Offset);
            File.AppendAllText(log, fourth.Substring(fourth.Length / 2) + UpToDate(d5), new UTF8Encoding(false));
            LoaderLogResult r3 = LoaderLogBackfill.Run(game, data, installed, replacements, now);
            launches = Events(data, HistoryKinds.Launch);
            Require(r3.Recorded == 2 && launches.Count == 5 && !launches[4].Mods.Any(m => m.Name == "Cammy Alt"), "completed run + up to date: " + r3.Summary);
            LoaderLogResult again = LoaderLogBackfill.Run(game, data, installed, replacements, now);
            Require(again.Runs == 0 && again.Recorded == 0, "nothing new was recorded again");
            // A last run without its end line counts once the log is old enough (SF6 stopped mid-run).
            File.AppendAllText(log, LogRun(now.AddMinutes(-30).ToLocalTime().AddTicks(-(now.AddMinutes(-30).ToLocalTime().Ticks % TimeSpan.TicksPerSecond)), "Loading base pak..."), new UTF8Encoding(false));
            File.SetLastWriteTimeUtc(log, now.AddMinutes(-20));
            LoaderLogResult settled = LoaderLogBackfill.Run(game, data, installed, replacements, now);
            Require(settled.Recorded == 1 && Events(data, HistoryKinds.Launch).Last().Slots == 0, "a settled unfinished run: " + settled.Summary);
            // The log rewritten (SlotsLoader trimmed it): read from the start; old runs are not recorded twice.
            File.WriteAllText(log, UpToDate(d5) + Built(now.AddMinutes(-10).ToLocalTime().AddTicks(-(now.AddMinutes(-10).ToLocalTime().Ticks % TimeSpan.TicksPerSecond)), false), new UTF8Encoding(false));
            File.SetLastWriteTimeUtc(log, now.AddMinutes(-9));
            LoaderLogResult restarted = LoaderLogBackfill.Run(game, data, installed, replacements, now);
            Require(restarted.Restarted && restarted.Runs == 2 && restarted.Skipped == 1 && restarted.Recorded == 1 && Events(data, HistoryKinds.Launch).Count == 7, "rewritten log: " + restarted.Summary + " skipped " + restarted.Skipped);
            HistoryIndex index = HistoryStore.For(data).Index();
            ModHistory hb = index.Find(History.Normalize(boots));
            Require(hb.Plays == 6 && hb.LastPlayed.HasValue && index.Launches == 7, "plays from the log: Boots " + hb.Plays + ", launches " + index.Launches);
            Note("backfill fixture: " + index.Launches + " launches, " + index.SlotUses + " slot-uses, Boots played " + hb.Plays + " times");
        }

        // ---- compaction
        private static void Compaction(string dir)
        {
            string data = Path.Combine(dir, "data");
            HistoryStore.CapBytes = 64L * 1024 * 1024;
            var rnd = new Random(7); DateTime t = DateTime.UtcNow.AddDays(-200);
            var batch = new List<HistoryEvent>();
            for (int i = 0; i < 600; i++)
            {
                t = t.AddMinutes(rnd.Next(5, 400));
                int m = rnd.Next(12); string source = "C:\\Mods\\mod" + m + ".zip", slot = "Cammy/mod" + m;
                int k = rnd.Next(10);
                if (k < 3)
                {
                    var mods = Enumerable.Range(0, 12).Where(x => rnd.Next(3) == 0).Select(x => new HistoryMod { Source = "C:\\Mods\\mod" + x + ".zip", Name = "mod" + x, Mode = HistoryModes.Extra, Character = "Cammy", Slot = "Cammy/mod" + x }).ToList();
                    batch.Add(new HistoryEvent { Utc = t, Kind = HistoryKinds.SessionStart, Via = "probe", Mods = mods });
                    if (rnd.Next(2) == 0) batch.Add(new HistoryEvent { Utc = t.AddSeconds(50), Kind = HistoryKinds.Launch, Via = "loader_log", Slots = mods.Count, Mods = mods.Take(3).ToList() });
                }
                else batch.Add(new HistoryEvent { Utc = t, Kind = new[] { HistoryKinds.Install, HistoryKinds.Update, HistoryKinds.Uninstall, HistoryKinds.Disable, HistoryKinds.Enable, HistoryKinds.Install, HistoryKinds.Recover }[k - 3], Mode = HistoryModes.Extra, Source = source, Name = "mod" + m, Character = "Cammy", Slot = slot, Options = new List<string> { "main" } });
            }
            History.Record(data, batch);
            HistoryStore store = HistoryStore.For(data);
            HistoryIndex before = store.Index();
            long size = new FileInfo(store.FilePath).Length;
            HistoryStore.CapBytes = size / 3;
            store.Compact();
            store.Reload();
            HistoryIndex after = store.Index();
            long compacted = new FileInfo(store.FilePath).Length;
            List<HistoryEvent> events = store.Events();
            Require(store.Compactions >= 0 && compacted < size && compacted <= HistoryStore.CapBytes && events.Count(e => e.Kind == HistoryKinds.Summary) >= 2, "compaction: " + size + " → " + compacted + " bytes, cap " + HistoryStore.CapBytes);
            Require(events.Last().Utc == batch.Max(e => e.Utc), "the newest event was not kept");
            Same(before, after, "after compaction");
            Require(before.Launches == after.Launches && before.SlotUses == after.SlotUses, "launch totals: " + before.Launches + "/" + before.SlotUses + " vs " + after.Launches + "/" + after.SlotUses);
            // Appending past the cap compacts by itself; totals still match an index built from the full story.
            var more = new List<HistoryEvent>();
            for (int i = 0; i < 120; i++) { t = t.AddMinutes(30); more.Add(new HistoryEvent { Utc = t, Kind = HistoryKinds.Install, Mode = HistoryModes.Extra, Source = "C:\\Mods\\mod" + (i % 12) + ".zip", Name = "mod" + (i % 12), Character = "Cammy", Slot = "Cammy/mod" + (i % 12), Detail = new string('x', 40) }); }
            HistoryIndex full = HistoryIndex.Build(batch.Concat(more));
            foreach (HistoryEvent e in more) History.Record(data, e);
            store.Reload();
            Require(new FileInfo(store.FilePath).Length <= HistoryStore.CapBytes + 400, "the file grew past the cap: " + new FileInfo(store.FilePath).Length);
            Same(full, store.Index(), "after automatic compaction");
            Note("compaction: " + size + " bytes → " + compacted + " (cap " + HistoryStore.CapBytes + "), " + events.Count(e => e.Kind == HistoryKinds.Summary) + " summary lines, " + before.Mods.Count + " mods, " + before.Launches + " launches");
        }
        private static void Same(HistoryIndex a, HistoryIndex b, string context)
        {
            foreach (ModHistory x in a.Mods.Values)
            {
                ModHistory y = b.Find(x.Key);
                Require(y != null, context + ": " + x.Key + " lost");
                Require(x.Plays == y.Plays && x.LastPlayed == y.LastPlayed && x.FirstInstalled == y.FirstInstalled && x.LastInstalled == y.LastInstalled && x.LastUpdated == y.LastUpdated && x.LastUninstalled == y.LastUninstalled && x.Installed == y.Installed && x.Enabled == y.Enabled && x.Events == y.Events,
                    context + ": " + x.Key + " plays " + x.Plays + "/" + y.Plays + " last played " + x.LastPlayed + "/" + y.LastPlayed + " installed " + x.Installed + "/" + y.Installed + " events " + x.Events + "/" + y.Events);
            }
            Require(a.Mods.Count == b.Mods.Count, context + ": mod count " + a.Mods.Count + " vs " + b.Mods.Count);
            Require(a.Launches == b.Launches, context + ": launches " + a.Launches + " vs " + b.Launches);
        }

        // ---- Last used
        private static void LastUsedSort(string dir)
        {
            string game = Path.Combine(dir, "game"); Directory.CreateDirectory(Path.Combine(game, "reframework", "costume_mods"));
            var config = new AppConfig { GameRoot = game, Libraries = new List<string>(), SevenZipPath = "" };
            string data = HistoryStore.DefaultDir; DateTime now = DateTime.UtcNow;
            Func<string, LibraryEntry> entry = delegate(string name) { return new LibraryEntry { Name = name, SourcePath = Path.Combine(dir, name + ".zip"), Kind = "ZIP", Character = "Cammy" }; };
            LibraryEntry played = entry("Played"), installedOnly = entry("Installed only"), switched = entry("Switched"), never = entry("Never");
            History.Record(data, new HistoryEvent { Utc = now.AddDays(-30), Kind = HistoryKinds.Install, Mode = HistoryModes.Extra, Source = played.SourcePath, Name = "Played", Character = "Cammy", Slot = "Cammy/Played" });
            History.Record(data, new HistoryEvent { Utc = now.AddDays(-3), Kind = HistoryKinds.Launch, Via = "loader_log", Slots = 1, Mods = new List<HistoryMod> { new HistoryMod { Source = played.SourcePath, Name = "Played", Mode = HistoryModes.Extra, Slot = "Cammy/Played" } } });
            History.Record(data, new HistoryEvent { Utc = now.AddDays(-2), Kind = HistoryKinds.Install, Mode = HistoryModes.Extra, Source = installedOnly.SourcePath, Name = "Installed only", Character = "Cammy", Slot = "Cammy/Installed only" });
            History.Record(data, new HistoryEvent { Utc = now.AddDays(-40), Kind = HistoryKinds.Install, Mode = HistoryModes.Replace, Source = switched.SourcePath, Name = "Switched", Id = "s1" });
            History.Record(data, new HistoryEvent { Utc = now.AddDays(-1), Kind = HistoryKinds.Update, Mode = HistoryModes.Replace, Source = switched.SourcePath, Name = "Switched", Id = "s2", PrevId = "s1" });
            using (MainForm form = Window(config))
            {
                Set(form, "library", new List<LibraryEntry> { never, played, installedOnly, switched });
                Require(form.LibraryLastUsed(played).Value == HistoryStore.Default.Index().Find(History.Normalize(played.SourcePath)).LastPlayed.Value && Math.Abs((form.LibraryLastUsed(played).Value - now.AddDays(-3)).TotalSeconds) < 2, "played: last played");
                Require(Math.Abs((form.LibraryLastUsed(installedOnly).Value - now.AddDays(-2)).TotalSeconds) < 2, "installed, never played: the install time");
                Require(Math.Abs((form.LibraryLastUsed(switched).Value - now.AddDays(-1)).TotalSeconds) < 2, "switched: the switch time");
                Require(!form.LibraryLastUsed(never).HasValue, "never used");
                var ranked = new List<KeyValuePair<LibraryEntry, int>> { new KeyValuePair<LibraryEntry, int>(never, 0), new KeyValuePair<LibraryEntry, int>(played, 0), new KeyValuePair<LibraryEntry, int>(installedOnly, 0), new KeyValuePair<LibraryEntry, int>(switched, 0) };
                Func<LibraryEntry, long> key = delegate(LibraryEntry e) { DateTime? u = form.LibraryLastUsed(e); return u.HasValue ? u.Value.Ticks : 0; };
                string desc = String.Join(", ", LibrarySorting.Order(ranked, LibrarySortKey.LastUsed, false, x => false, key).Select(x => x.Name));
                string asc = String.Join(", ", LibrarySorting.Order(ranked, LibrarySortKey.LastUsed, true, x => false, key).Select(x => x.Name));
                Require(desc == "Switched, Installed only, Played, Never" && asc == "Never, Played, Installed only, Switched", "Last used order: " + desc + " / " + asc);
                form.SetLibrarySort(LibrarySortKey.LastUsed, false);
                string meta = form.UsageMeta(played.SourcePath, true), meta2 = form.UsageMeta(installedOnly.SourcePath, true), meta3 = form.UsageMeta(installedOnly.SourcePath, false);
                Require(meta == "played 3d ago" && meta2 == "installed " + History.Date(now.AddDays(-2), now) && meta3 == null && form.UsageMeta(never.SourcePath, true) == null, "meta: " + meta + " / " + meta2 + " / " + meta3);
                // 2,667 mods: in-memory lookups only.
                var many = Enumerable.Range(0, 2667).Select(i => entry("Mod " + i)).ToList();
                var watch = Stopwatch.StartNew();
                int hits = 0; foreach (LibraryEntry e in many.Concat(new[] { played })) if (form.LibraryLastUsed(e).HasValue) hits++;
                watch.Stop();
                Require(watch.ElapsedMilliseconds < 250, "2,667 Last used lookups took " + watch.ElapsedMilliseconds + " ms");
                Note("Last used: 2,668 lookups in " + watch.ElapsedMilliseconds + " ms");
                form.Close();
            }
        }

        // ---- Assistant
        private static void AssistantTool(string dir)
        {
            AssistantFixture fx = AssistantFixture.Create(Path.Combine(dir, "fx"));
            string data = HistoryStore.DefaultDir; DateTime now = DateTime.UtcNow;
            // Boots (installed by the fixture): played 2 days ago; the Cammy pack: played 40 days ago, uninstalled;
            // Juri: installed 50 days ago, never played; a deleted source: uninstalled long ago.
            HistoryEvent bootsInstall = Events(data, HistoryKinds.Install).Single();
            History.Record(data, new HistoryEvent { Utc = now.AddDays(-45), Kind = HistoryKinds.Install, Mode = HistoryModes.Extra, Source = fx.CammyPack, Name = "Crimson", Character = "Cammy", Slot = "Cammy/Crimson" });
            History.Record(data, new HistoryEvent { Utc = now.AddDays(-40), Kind = HistoryKinds.Launch, Via = "loader_log", Slots = 2, Mods = new List<HistoryMod> { new HistoryMod { Source = fx.CammyPack, Name = "Crimson", Mode = HistoryModes.Extra, Character = "Cammy", Slot = "Cammy/Crimson" } } });
            History.Record(data, new HistoryEvent { Utc = now.AddDays(-39), Kind = HistoryKinds.Launch, Via = "loader_log", Slots = 2, Mods = new List<HistoryMod> { new HistoryMod { Source = fx.CammyPack, Name = "Crimson", Mode = HistoryModes.Extra, Character = "Cammy", Slot = "Cammy/Crimson" } } });
            History.Record(data, new HistoryEvent { Utc = now.AddDays(-35), Kind = HistoryKinds.Uninstall, Mode = HistoryModes.Extra, Source = fx.CammyPack, Name = "Crimson", Character = "Cammy", Slot = "Cammy/Crimson" });
            History.Record(data, new HistoryEvent { Utc = now.AddDays(-2), Kind = HistoryKinds.SessionStart, Via = "probe", Mods = new List<HistoryMod> { new HistoryMod { Source = fx.CammyBoots, Name = "Boots", Mode = HistoryModes.Extra, Character = "Cammy", Slot = "Cammy/Boots" } } });
            History.Record(data, new HistoryEvent { Utc = now.AddDays(-50), Kind = HistoryKinds.Install, Mode = HistoryModes.Replace, Source = fx.JuriPack, Name = "Juri Barefoot Variants", Character = "Juri", Id = "j1", Slot = "Juri Outfit 1" });
            History.Record(data, new HistoryEvent { Utc = now.AddDays(-90), Kind = HistoryKinds.Uninstall, Mode = HistoryModes.Tool, Source = Path.Combine(fx.Library, "Deleted Tool.zip"), Name = "Deleted Tool", Id = "d1" });
            var workspace = new AssistantWorkspace(fx.Config, new AssistantSettings());
            var tb = new AssistantToolbox(workspace);
            Require(tb.Specs.Count == 17 && tb.Specs.Last().Name == "get_history" && !tb.Specs.Last().Mutating, "tool count / order");
            JsonObj def = tb.Find("get_history").Definition();
            string schema = AssistantJson.Write(def);
            Require(schema.Contains("\"strict\":true") && schema.Contains("\"additionalProperties\":false") && schema.Contains("\"required\":[\"character\",\"kind\",\"install_state\",\"not_used_days\",\"used_within_days\",\"query\",\"order\",\"limit\"]"), "schema: " + schema);
            Func<string, string, string, int, int, string, string, int, List<Dictionary<string, object>>> run = delegate(string character, string kind, string state, int notUsed, int within, string query, string order, int limit)
            {
                var input = new Dictionary<string, object> { { "character", character }, { "kind", kind }, { "install_state", state }, { "not_used_days", notUsed }, { "used_within_days", within }, { "query", query }, { "order", order }, { "limit", limit } };
                ToolOutcome o = tb.Execute("get_history", input);
                string text = AssistantJson.Write((JsonObj)o.Content);
                Require(!o.IsError, "get_history error: " + text);
                Require(!System.Text.RegularExpressions.Regex.IsMatch(text, @"[A-Za-z]:\\\\"), "a local path in the result: " + text);
                var parsed = AssistantJson.Serializer().Deserialize<Dictionary<string, object>>(text);
                return ((System.Collections.ArrayList)parsed["results"]).Cast<Dictionary<string, object>>().ToList();
            };
            Func<List<Dictionary<string, object>>, string> names = delegate(List<Dictionary<string, object>> rows) { return String.Join(", ", rows.Select(r => (string)r["name"])); };
            var all = run("", "", "", 0, 0, "", "", 0);
            Require(names(all) == "Deleted Tool, Manual Juri, Juri Barefoot Variants, Crimson, Boots", "default order (least recent first, never-used first): " + names(all));
            var cammy = run("cammy", "", "", 0, 0, "", "", 0);
            Require(names(cammy) == "Crimson, Boots", "character: " + names(cammy));
            var stale = run("Cammy", "extra_outfit", "", 30, 0, "", "", 0);
            Require(names(stale) == "Crimson", "not_used_days 30: " + names(stale));
            Dictionary<string, object> crimson = stale[0];
            Require((int)crimson["play_count"] == 2 && ((string)crimson["last_played"]).EndsWith(" UTC") && (bool)crimson["installed"] == false && (string)crimson["mod_id"] == fx.CammyPackId && (bool)crimson["in_library"] && ((string)crimson["last_uninstalled"]).EndsWith(" UTC") && (int)crimson["days_since_used"] >= 38, "Crimson row: " + AssistantJson.Serializer().Serialize(crimson));
            var recent = run("", "", "installed", 0, 7, "", "most_recent", 0);
            Require(names(recent) == "Boots" && ((object[])((System.Collections.ArrayList)recent[0]["installed_ids"]).ToArray()).Length == 1 && (int)recent[0]["play_count"] == 1, "installed, used within 7 days: " + names(recent));
            var notInstalled = run("", "", "not_installed", 0, 0, "", "", 0);
            Require(names(notInstalled) == "Deleted Tool, Juri Barefoot Variants, Crimson", "not installed now (deleted sources keep their rows; the Juri record the history names is not in the game): " + names(notInstalled));
            Require(!(bool)notInstalled[0]["in_library"] && (string)notInstalled[0]["kind"] == "tool", "deleted source row");
            Require(names(run("", "replace", "", 0, 0, "", "", 0)) == "Juri Barefoot Variants" && names(run("", "", "", 0, 0, "manual", "", 0)) == "Manual Juri" && names(run("", "", "", 0, 0, "", "most_played", 2)) == "Crimson, Boots", "kind / query / most_played + limit");
            var bad = new Dictionary<string, object> { { "character", "Nobody" }, { "kind", "" }, { "install_state", "" }, { "not_used_days", 0 }, { "used_within_days", 0 }, { "query", "" }, { "order", "" }, { "limit", 0 } };
            Require(tb.Execute("get_history", bad).IsError, "an unknown character was accepted");
            bad["character"] = ""; bad.Remove("limit");
            Require(tb.Execute("get_history", bad).IsError, "a missing property was accepted (strict: every property required)");
            bad["limit"] = 0; bad["kind"] = "outfit";
            Require(tb.Execute("get_history", bad).IsError, "an unknown kind was accepted");
            Require(AssistantPrompt.System.Contains("get_history") && AssistantPrompt.System.Contains("not used in a month") && AssistantPrompt.System.Contains("100 extra outfits per fighter"), "rotation guidance missing from the prompt");
            // search_mods sort last_used follows the same rule (history first).
            var search = new Dictionary<string, object> { { "query", "" }, { "character", "" }, { "kind", "any" }, { "install_state", "any" }, { "limit", 0 }, { "offset", 0 }, { "costume", "" }, { "sort", "last_used" }, { "order", "" } };
            string s = AssistantJson.Write((JsonObj)tb.Execute("search_mods", search).Content);
            Require(s.IndexOf("Cammy Classic Boots", StringComparison.Ordinal) < s.IndexOf("Cammy Barefoot Pack", StringComparison.Ordinal) && s.IndexOf("Cammy Barefoot Pack", StringComparison.Ordinal) < s.IndexOf("Ingrid Evening Dress", StringComparison.Ordinal), "search_mods last_used order: " + s);
            Note("bootsInstall " + bootsInstall.Utc.ToString("o"));
        }

        // ---- the window
        private static void View(string dir)
        {
            AssistantFixture fx = AssistantFixture.Create(Path.Combine(dir, "fx"));
            string data = HistoryStore.DefaultDir; DateTime now = DateTime.UtcNow;
            var ev = new List<HistoryEvent>();
            Func<string, int, HistoryEvent> outfit = delegate(string kind, int hoursAgo) { return new HistoryEvent { Utc = now.AddHours(-hoursAgo), Kind = kind, Mode = HistoryModes.Extra, Source = fx.CammyPack, Name = "Crimson", Mod = "Cammy Barefoot Pack", Character = "Cammy", Slot = "Cammy/Crimson", Options = new List<string> { "Main Files", "Color/Crimson" } }; };
            ev.Add(outfit(HistoryKinds.Install, 300)); ev.Add(outfit(HistoryKinds.Update, 200)); ev.Last().PrevOptions = new List<string> { "Main Files", "Color/Black" };
            ev.Add(new HistoryEvent { Utc = now.AddHours(-150), Kind = HistoryKinds.Install, Mode = HistoryModes.Tool, Source = Path.Combine(fx.Library, "Mini Window.zip"), Name = "SF6 Mini Window", Id = "t1" });
            ev.Add(new HistoryEvent { Utc = now.AddHours(-100), Kind = HistoryKinds.SessionStart, Via = "probe", Mods = new List<HistoryMod> { new HistoryMod { Source = fx.CammyPack, Name = "Crimson", Mode = HistoryModes.Extra, Character = "Cammy", Slot = "Cammy/Crimson" }, new HistoryMod { Source = Path.Combine(fx.Library, "Mini Window.zip"), Name = "SF6 Mini Window", Mode = HistoryModes.Tool } } });
            ev.Add(new HistoryEvent { Utc = now.AddHours(-100).AddSeconds(30), Kind = HistoryKinds.Launch, Via = "loader_log", Slots = 2, Mods = new List<HistoryMod> { new HistoryMod { Source = fx.CammyPack, Name = "Crimson", Mode = HistoryModes.Extra, Character = "Cammy", Slot = "Cammy/Crimson" }, new HistoryMod { Slot = "A.K.I/Aki Bare Legs.zip", Name = "Aki Bare Legs", Mode = HistoryModes.Extra, Character = "A.K.I" } } });
            ev.Add(new HistoryEvent { Utc = now.AddHours(-99), Kind = HistoryKinds.SessionStop, Via = "probe", Start = now.AddHours(-100), Detail = "1 h 0 min" });
            ev.Add(new HistoryEvent { Utc = now.AddHours(-50), Kind = HistoryKinds.Reorder, Mode = HistoryModes.Replace, Source = Path.Combine(fx.Library, "Juri Alt.zip"), Name = "Juri Alt", Character = "Juri", Id = "j1", Detail = "#2 → #1" });
            ev.Add(new HistoryEvent { Utc = now.AddHours(-30), Kind = HistoryKinds.Disable, Mode = HistoryModes.Extra, Source = fx.JuriPack, Name = "Gold", Character = "Juri", Slot = "Juri/Gold" });
            ev.Add(new HistoryEvent { Utc = now.AddHours(-3), Kind = HistoryKinds.Uninstall, Mode = HistoryModes.Extra, Source = fx.CammyPack, Name = "Crimson", Character = "Cammy", Slot = "Cammy/Crimson" });
            ev.Add(new HistoryEvent { Utc = now.AddHours(-2), Kind = HistoryKinds.Undo, Name = "Uninstalled Crimson", Detail = "Undo: Uninstalled Crimson" });
            ev.Add(new HistoryEvent { Utc = now.AddHours(-2).AddSeconds(5), Kind = HistoryKinds.Recover, Mode = HistoryModes.Extra, Source = fx.CammyPack, Name = "Crimson", Character = "Cammy", Slot = "Cammy/Crimson" });
            ev.Add(new HistoryEvent { Utc = now.AddMinutes(-20), Kind = HistoryKinds.FluffyUninstall, Mode = HistoryModes.Fluffy, Name = "Fluffy Tools", Slot = "Tools", Detail = "1 file deleted, 0 restored" });
            History.Record(data, ev);
            using (MainForm form = Window(fx.Config))
            {
                Set(form, "installed", Installer.ListInstalled(fx.CostumeRoot)); Call(form, "FillInstalled");
                Call(form, "ShowPage", Get<Panel>(form, "installedPage"));
                FlatButton historyButton = Get<FlatButton>(form, "historyButton");
                // 1.0.1: History and Presets are icon buttons (no text; the name is the accessible name and the hover hint).
                FlatButton presetsButton = Get<FlatButton>(form, "presetsButton");
                Require(historyButton.Visible && historyButton.Text == "" && historyButton.Glyph == ButtonGlyph.History && historyButton.AccessibleName == "History" && presetsButton.Glyph == ButtonGlyph.Presets && presetsButton.AccessibleName == "Presets" && historyButton.Parent.Right <= presetsButton.Parent.Left + 1,
                    "History icon button left of the Presets icon: text '" + historyButton.Text + "', glyph " + historyButton.Glyph + ", name " + historyButton.AccessibleName + ", " + historyButton.Parent.Bounds + " vs " + presetsButton.Parent.Bounds);
                var gear = Get<FlatButton>(form, "navSettings");
                Require(gear.ContextMenuStrip.Items.OfType<ToolStripMenuItem>().Any(i => i.Text == "History"), "gear → History missing");
                historyButton.PerformClick(); Application.DoEvents();
                Panel page = Get<Panel>(form, "historyPage");
                ListView list = Get<ListView>(form, "historyList");
                Require(page.Visible && !Get<Panel>(form, "installedPage").Visible && Get<FlatButton>(form, "navInstalled").Active, "History page not shown");
                List<MainForm.HistoryRow> rows = list.Items.Cast<ListViewItem>().Select(i => (MainForm.HistoryRow)i.Tag).ToList();
                string verbs = String.Join(",", rows.Select(r => r.Verb));
                Require(verbs == "Installed,Uninstalled (external manager),Restored,Undo,Uninstalled,Disabled,Moved #2 → #1,Played SF6,Installed,Switched,Installed", "timeline: " + verbs);
                MainForm.HistoryRow play = rows.Single(r => r.Kind == "play");
                Require(play.Title == "2 extra outfits, 1 replacement or tool mod" && play.Detail.StartsWith("1 h 0 min · 2 slots built by Slotsloader · Crimson"), "played row: " + play.Title + " | " + play.Detail);
                Require(rows.Single(r => r.Kind == HistoryKinds.Update).Detail.Contains("options changed"), "switch row");
                var kind = Get<ComboBox>(form, "historyKind"); var character = Get<ComboBox>(form, "historyCharacter"); var search = Get<PaddedTextField>(form, "historySearch");
                kind.SelectedItem = "Played"; Application.DoEvents();
                Require(list.Items.Count == 1, "kind filter Played: " + list.Items.Count);
                kind.SelectedIndex = 0; character.SelectedItem = "Juri"; Application.DoEvents();
                Require(list.Items.Count == 2, "character Juri: " + list.Items.Count);
                character.SelectedIndex = 0; search.Text = "aki"; Application.DoEvents();
                Require(list.Items.Count == 1 && ((MainForm.HistoryRow)list.Items[0].Tag).Kind == "play", "search 'aki' (A.K.I) finds the launch that built Aki Bare Legs: " + list.Items.Count);
                Label clear = Get<Label>(form, "historySearchClear");
                Require(clear.Visible && clear.AccessibleName == "Clear search", "the × clear is not shown");
                search.Text = "crimson"; Application.DoEvents();
                Require(list.Items.Count == 6, "search 'crimson': " + list.Items.Count);
                search.Text = "nothing like it"; Application.DoEvents();
                Require(list.Items.Count == 0 && Get<Label>(form, "historyEmpty").Visible && Get<Label>(form, "historyEmpty").Text == "Nothing matches these filters.", "empty filtered state");
                search.Clear(); Application.DoEvents();
                Require(list.Items.Count == rows.Count && !clear.Visible, "cleared");
                Require(UiAudit.ToolTipFindings(page).Count == 0, "tooltips: " + String.Join("; ", UiAudit.ToolTipFindings(page)));
                foreach (int width in new[] { 1232, 1110 })
                {
                    form.MinimumSize = Size.Empty; form.ClientSize = new Size(DpiMetrics.Px(form, width), DpiMetrics.Px(form, 640)); Application.DoEvents();
                    foreach (string name in new[] { "historySearch", "historyCharacter", "historyKind", "historyBackButton" })
                    {
                        Control c = Get<Control>(form, name);
                        Rectangle inPage = page.RectangleToClient(c.RectangleToScreen(c.ClientRectangle));
                        Require(c.Visible && inPage.Left >= 0 && inPage.Right <= page.ClientSize.Width, name + " clipped at " + width + ": " + inPage + " in " + page.ClientSize);
                    }
                    Control back = Get<Control>(form, "historyBackButton"), kindBox = Get<Control>(form, "historyKind");
                    Require(page.RectangleToClient(kindBox.RectangleToScreen(kindBox.ClientRectangle)).Right <= page.RectangleToClient(back.RectangleToScreen(back.ClientRectangle)).Left, "filters overlap Back at " + width);
                    Require(list.Items.Count > 0 && Math.Abs(list.Items[0].Bounds.Height - DpiMetrics.Px(form, MainForm.HistoryRowHeight)) <= 2, "row height " + list.Items[0].Bounds.Height + " at " + form.DeviceDpi + " DPI");
                    Save(form, dir, "history-" + width);
                }
                // The Installed page with its History button and the meta text.
                Call(form, "ShowPage", Get<Panel>(form, "installedPage")); Application.DoEvents();
                Save(form, dir, "installed-with-history");
                Get<FlatButton>(form, "historyBackButton").PerformClick();
                // The Mod library rows: "played 4d ago" on the played pack, "installed …" on the installed Boots.
                Set(form, "library", Library.Scan(fx.Config.Libraries, "")); Call(form, "ShowPage", Get<Panel>(form, "libraryPage")); Call(form, "ApplyFilters"); Application.DoEvents();
                Require(form.UsageMeta(fx.CammyPack, false) == "played 4d ago", "library meta for the played pack: " + form.UsageMeta(fx.CammyPack, false));
                Save(form, dir, "library-with-history");
                Note("History view rendered at " + form.DeviceDpi + " DPI: " + Path.Combine(dir, "history-1232-" + form.DeviceDpi + ".png"));
                form.Close();
            }
        }
        private static void Save(Control c, string path, string name) { using (var bitmap = new Bitmap(c.Width, c.Height)) { c.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(Path.Combine(path, name + "-" + c.DeviceDpi + ".png"), ImageFormat.Png); } }

        // ---- real, read-only: the user's SlotsLoader log
        internal static int RunReal(string output, string gameRoot)
        {
            string root = Path.GetFullPath(output); Directory.CreateDirectory(root);
            string data = Path.Combine(root, "data-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            var lines = new List<string>();
            try
            {
                string log = Path.Combine(gameRoot, LoaderLogBackfill.LogName);
                Require(File.Exists(log), "no Slotsloader log at " + log);
                var info = new FileInfo(log); long length = info.Length; DateTime written = info.LastWriteTimeUtc; string hash = Hash(log);
                string costume = Path.Combine(gameRoot, "reframework", "costume_mods");
                List<InstalledOutfit> outfits = Directory.Exists(costume) ? Installer.ListInstalled(costume) : new List<InstalledOutfit>();
                var watch = Stopwatch.StartNew();
                LoaderLogResult r = LoaderLogBackfill.Run(gameRoot, data, outfits, new List<ReplaceMod>(), DateTime.UtcNow);
                watch.Stop();
                var after = new FileInfo(log);
                bool unchanged = after.Length >= length && after.LastWriteTimeUtc >= written && (after.Length != length || Hash(log) == hash);
                Require(unchanged, "the log changed in an unexpected way during the read");
                HistoryIndex index = HistoryStore.For(data).Index();
                List<HistoryEvent> launches = HistoryStore.For(data).Events().Where(e => e.Kind == HistoryKinds.Launch).ToList();
                int outfitUses = launches.Sum(e => e.Mods == null ? 0 : e.Mods.Count(m => m.Mode == HistoryModes.Extra));
                lines.Add("Read-only backfill of " + log + " (" + length + " bytes, last written " + written.ToString("o") + ")");
                lines.Add("Runs (launches) found: " + r.Runs + "; recorded: " + r.Recorded + "; up to date: " + launches.Count(e => e.Detail == "up to date") + "; built: " + launches.Count(e => e.Detail == "built"));
                lines.Add("Slot-uses (slots Slotsloader built per launch, up-to-date launches reuse the last build): " + r.SlotUses);
                lines.Add("Outfit uses (an outfit built in a launch): " + outfitUses + "; mapped to a Manager install: " + r.Mapped + "; manual or older than its manifest: " + r.Unmapped);
                lines.Add("Distinct outfits played: " + index.Mods.Count + "; first launch " + (launches.Count > 0 ? launches.Min(e => e.Utc).ToString("o") : "-") + ", last " + (launches.Count > 0 ? launches.Max(e => e.Utc).ToString("o") : "-"));
                foreach (ModHistory m in index.Mods.Values.OrderByDescending(m => m.Plays).Take(12)) lines.Add("  " + m.Plays + " × " + m.Slot + (m.Source.Length > 0 ? " (Manager install)" : ""));
                lines.Add("Second read (incremental): " + LoaderLogBackfill.Run(gameRoot, data, outfits, new List<ReplaceMod>(), DateTime.UtcNow).Summary);
                lines.Add("Time: " + watch.ElapsedMilliseconds + " ms. The log was opened read-only (FileShare.ReadWrite); nothing was written to the game folder; history in " + data);
                lines.Add("PASS history-real");
                File.WriteAllLines(Path.Combine(root, "history-real.txt"), lines);
                Console.WriteLine(String.Join(Environment.NewLine, lines));
                return 0;
            }
            catch (Exception ex)
            {
                lines.Add("FAIL " + ex);
                File.WriteAllLines(Path.Combine(root, "history-real.txt"), lines);
                Console.WriteLine(String.Join(Environment.NewLine, lines));
                return 1;
            }
        }
    }
}
