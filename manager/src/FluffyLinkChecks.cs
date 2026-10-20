using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;

namespace SlotsInstaller
{
    // 2.2.0 engine checks (part of --replace-check): the Fluffy link and game files other tools change, on
    // generated game folders and a fake Fluffy instance that installs mods the way Fluffy's Log.txt shows
    // (existing files copied to Games\SF6\Backup\<path>, installed.ini sections appended).
    internal static class FluffyLinkChecks
    {
        private static void Require(bool condition, string message) { ReplaceChecks.Require(condition, message); }
        private static byte[] B(string text) { return Encoding.UTF8.GetBytes(text); }
        private static string G(ReplaceChecks.Fixture f, string rel) { return Path.Combine(f.Game, rel.Replace('/', '\\')); }
        private static string Text(string file) { return File.Exists(file) ? File.ReadAllText(file) : null; }

        // A Fluffy instance: <root>\Fluffy\Games\SF6 with Mods, Backup and installed.ini.
        internal sealed class FakeFluffy
        {
            internal string Root, Game;
            internal readonly List<string> Sections = new List<string>();      // installed.ini text per section, in order
            internal readonly Dictionary<string, List<string>> Listed = new Dictionary<string, List<string>>();
            internal string Ini { get { return Path.Combine(Root, "installed.ini"); } }
            internal FakeFluffy(string root, string game) { Root = root; Game = game; Directory.CreateDirectory(Path.Combine(Root, "Mods")); }

            // Installs like Fluffy: a file that exists and that no installed Fluffy mod lists is backed up first;
            // the crash reporter is renamed; the section is appended. `lines` maps the written path to the file line.
            internal string Install(string section, string name, string installType, Dictionary<string, byte[]> files, bool zip, Dictionary<string, string> lines)
            {
                string source = Path.Combine(Root, "Mods", section);
                if (zip)
                    using (var archive = ZipFile.Open(source + ".zip", ZipArchiveMode.Create))
                        foreach (var kv in files) using (Stream s = archive.CreateEntry(kv.Key).Open()) s.Write(kv.Value, 0, kv.Value.Length);
                else foreach (var kv in files) ReplaceChecks.Put(source, kv.Key, kv.Value);
                foreach (var kv in files)
                {
                    string full = Path.Combine(Game, kv.Key.Replace('/', '\\'));
                    bool listed = Listed.Values.Any(l => l.Contains(FluffyInstalls.Key(kv.Key), StringComparer.OrdinalIgnoreCase));
                    if (File.Exists(full) && !listed) ReplaceChecks.Put(Path.Combine(Root, "Backup"), kv.Key, File.ReadAllBytes(full));
                    ReplaceChecks.Put(Game, kv.Key, kv.Value);
                }
                string crash = Path.Combine(Game, CrashReporter.Name), renamed = Path.Combine(Game, CrashReporter.RenamedName);
                if (File.Exists(crash) && !File.Exists(renamed)) File.Move(crash, renamed);
                var sb = new StringBuilder();
                sb.Append("[" + section + "]\r\nModID_v1=" + (1000 + Sections.Count) + "\r\nModName=" + name + "\r\nShortModID_v1=" + (10 + Sections.Count) + "\r\ninstalltype=" + installType + "\r\n");
                foreach (string k in files.Keys) { string line; sb.Append("file=" + (lines != null && lines.TryGetValue(k, out line) ? line : k) + "\r\n"); }
                Sections.Add(sb.ToString());
                Listed[section] = files.Keys.Select(FluffyInstalls.Key).ToList();
                Write();
                return sb.ToString();
            }
            // Fluffy writes installed.ini.new, then replaces installed.ini.
            internal void Write()
            {
                string next = Ini + ".new";
                File.WriteAllText(next, String.Concat(Sections), new UTF8Encoding(false));
                if (File.Exists(Ini)) File.Replace(next, Ini, null); else File.Move(next, Ini);
            }
            internal FluffyInstalls Load() { return FluffyInstalls.Load(new[] { Root }); }
        }

        private static ReplaceChecks.Fixture Game(string root, bool reframework)
        {
            ReplaceChecks.Fixture f = ReplaceChecks.Create(root, reframework, reframework ? ReplaceMethods.Loose : ReplaceMethods.Pak);
            Directory.CreateDirectory(Path.Combine(f.Game, "reframework", "plugins")); Directory.CreateDirectory(Path.Combine(f.Game, "reframework", "data"));
            Directory.CreateDirectory(Path.Combine(f.Game, "reframework", "autorun"));
            ReplaceChecks.Put(f.Game, "tools/helper.exe", B("genuine helper"));
            return f;
        }
        private static FakeFluffy Attach(ReplaceChecks.Fixture f, string root)
        {
            var fluffy = new FakeFluffy(Path.Combine(root, "Fluffy", "Games", "SF6"), f.Game);
            f.Engine.FluffyProvider = delegate { return fluffy.Load(); };
            return fluffy;
        }

        // 1.0.1 (user decision): while the external manager lists any mod, only Extra outfit and Slotsloader install;
        // nothing else puts game files in place, its mods are uninstalled there, and the Manager's own mods still go.
        internal static void Lockout(string root, FakeProcessProbe probe)
        {
            ReplaceChecks.Fixture f = ReplaceChecks.Create(Path.Combine(root, "game"), false, ReplaceMethods.Pak);
            FakeFluffy fluffy = Attach(f, root);
            string stageFile = Path.Combine(f.Mods, "stagePak", "Stage", "NULSPACE.pak"); Directory.CreateDirectory(Path.GetDirectoryName(stageFile));
            ReplaceChecks.Put(f.Mods, "stagePak/Stage/modinfo.ini", B("name=Stage PAK\r\n"));
            PakWriter.WriteFixture(stageFile, new List<KeyValuePair<string, byte[]>> { new KeyValuePair<string, byte[]>("natives/stm/product/environment/stage_fixture.user.2", B("stage")) }, 0, 0);
            // Before the external manager has a mod: two replacements (A on top, disabled).
            ReplaceMod b = ReplaceChecks.Install(f, "modB", "Cammy Alt");
            ReplaceMod a = ReplaceChecks.Install(f, "modA", "Cammy Colors");
            f.Engine.SetEnabled(a.Id, false);
            Require(f.Engine.ExternalLockReason() == null, "lockout with nothing in the external manager");
            fluffy.Install("Other Script", "Other Script", "normal", new Dictionary<string, byte[]> { { "reframework/autorun/other.lua", B("-- other") } }, false, null);
            string reason = f.Engine.ExternalLockReason();
            Require(reason != null && reason.Contains("has 1 mod installed"), "lock reason: " + reason);
            var before = ReplaceChecks.Snapshot(f.Game);
            // A replacement, a PAK mod and a tool mod: the health check blocks them and an install is refused.
            foreach (string mod in new[] { "modC", "stagePak", "toolExe" })
            {
                ReplacePlan check = ReplaceChecks.Check(f, mod);
                Require(!check.CanInstall && check.Errors.Contains(reason), mod + ": health check not blocked: " + String.Join(" | ", check.Errors));
                string name = mod;
                ReplaceChecks.Refused(delegate { ReplaceChecks.Install(f, name, name); }, "only Extra outfit, Slotsloader and REFramework install");
            }
            // Changes that put game files in place: enabling, moving, Reapply.
            ReplaceChecks.Refused(delegate { f.Engine.SetEnabled(a.Id, true); }, "only Extra outfit, Slotsloader and REFramework install");
            ReplaceChecks.Refused(delegate { f.Engine.Move(b.Id, -1); }, "only Extra outfit, Slotsloader and REFramework install");
            ReplaceChecks.Refused(delegate { f.Engine.Reapply(); }, "only Extra outfit, Slotsloader and REFramework install");
            ReplaceChecks.SameSnapshot(before, ReplaceChecks.Snapshot(f.Game), "after the refusals");
            // The external manager's mod is uninstalled there.
            ReplaceChecks.Refused(delegate { string folder; f.Engine.UninstallFluffy(fluffy.Root, "Other Script", out folder); }, "Uninstall it in the external mod manager.");
            Require(fluffy.Load().Mods.Count == 1 && File.Exists(G(f, "reframework/autorun/other.lua")), "the refused external uninstall changed something");
            // Slotsloader installs; the Manager's own mods disable and uninstall.
            ReplaceMod loader = ReplaceChecks.Install(f, "tool", "Slotsloader");
            Require(Text(G(f, "amd_ags_x64.dll")) == "slotsloader proxy", "Slotsloader did not install during the lockout");
            // 1.0.10 (the user): plain REFramework installs too, unless the external manager installed dinput8.dll (updated there).
            ReplaceChecks.Put(f.Mods, "refw/dinput8.dll", B("REFramework 12.10.2026"));
            ReplaceChecks.Put(f.Mods, "refw/reframework_revision.txt", B("fixture revision"));
            ReplaceChecks.Put(f.Mods, "refwBundle/dinput8.dll", B("REFramework 12.10.2026"));
            ReplaceChecks.Put(f.Mods, "refwBundle/reframework/autorun/helper.lua", B("-- helper"));
            ReplaceChecks.Refused(delegate { ReplaceChecks.Install(f, "refwBundle", "REFramework bundle"); }, "only Extra outfit, Slotsloader and REFramework install");
            ReplaceMod refw = ReplaceChecks.Install(f, "refw", "REFramework");
            Require(Text(G(f, "dinput8.dll")) == "REFramework 12.10.2026", "REFramework did not install during the lockout");
            f.Engine.Uninstall(refw.Id);
            fluffy.Install("Fluffy REFramework", "Fluffy REFramework", "normal", new Dictionary<string, byte[]> { { "dinput8.dll", B("REFramework 01.01.2025") } }, false, null);
            ReplacePlan owned = ReplaceChecks.Check(f, "refw");
            Require(!owned.CanInstall && owned.Errors.Contains(ReplaceEngine.ReFrameworkFluffyReason), "REFramework over the external manager's: " + String.Join(" | ", owned.Errors));
            ReplaceChecks.Refused(delegate { ReplaceChecks.Install(f, "refw", "REFramework"); }, "update it there");
            Require(Text(G(f, "dinput8.dll")) == "REFramework 01.01.2025", "the external manager's REFramework was replaced");
            ToolStatus tools = ToolStatus.Detect(f.Game, fluffy.Load());
            Require(tools.ReFrameworkFluffy && !tools.ReFrameworkOffer && tools.ReFrameworkDetail.EndsWith("update it there"), "tool status with the external manager's REFramework: " + tools.ReFrameworkDetail);
            f.Engine.SetEnabled(b.Id, false);
            f.Engine.Uninstall(a.Id); f.Engine.Uninstall(b.Id); f.Engine.Uninstall(loader.Id);
            Require(f.Engine.LoadState().Mods.Count == 0 && Text(G(f, "amd_ags_x64.dll")) == "genuine AMD AGS library", "uninstalls during the lockout");
            // Once the external manager is empty, Replace original works again.
            fluffy.Sections.Clear(); fluffy.Listed.Clear(); fluffy.Write();
            Require(f.Engine.ExternalLockReason() == null, "lockout after the external manager was emptied");
            ReplaceMod again = ReplaceChecks.Install(f, "modB", "Cammy Alt");
            Require(f.Engine.LoadState().Find(again.Id) != null, "a replacement after the lockout");
        }

        // ---- installed.ini parsing and the watcher
        internal static void ParseAndWatch(string root)
        {
            ReplaceChecks.Fixture f = Game(Path.Combine(root, "game"), true);
            FakeFluffy fluffy = Attach(f, root);
            Require(fluffy.Load().Mods.Count == 0 && fluffy.Load().Warnings.Count == 0, "an absent installed.ini must mean no mods");
            using (var watcher = new FluffyWatcher(new[] { fluffy.Root }, 250))
            using (var changed = new ManualResetEvent(false))
            {
                watcher.Changed += delegate { changed.Set(); };
                Require(watcher.Count == 1, "the Fluffy folder is not watched");
                DateTime start = DateTime.UtcNow;
                fluffy.Install("Scripts", "Updated scripts", "normal", new Dictionary<string, byte[]> { { "reframework/data/rsz/readme.txt", B("a") }, { "reframework/autorun/scripts.lua", B("b") } }, false,
                    new Dictionary<string, string> { { "reframework/autorun/scripts.lua", "reframework\\Autorun\\SCRIPTS.lua" } });
                Require(changed.WaitOne(2000), "the watcher did not report an install within 2 s");
                double installMs = (DateTime.UtcNow - start).TotalMilliseconds;
                FluffyInstalls loaded = fluffy.Load();
                Require(loaded.Mods.Count == 1 && loaded.Mods[0].Name == "Updated scripts" && loaded.Mods[0].ModId == "1000" && loaded.Mods[0].CopiedFiles, "parse: " + String.Join(", ", loaded.Mods.Select(m => m.Name)));
                Require(loaded.Owner("REFRAMEWORK/autorun/scripts.lua") != null && loaded.Owner("reframework\\data\\rsz\\README.TXT") != null, "owners must ignore case and / vs \\");
                fluffy.Install("Suite", "SF6 Mod Suite", "normal", new Dictionary<string, byte[]> { { "reframework/data/rsz/readme.txt", B("c") } }, false, null);
                loaded = fluffy.Load();
                Require(loaded.OwnersOf("reframework/data/rsz/readme.txt").Count == 2 && loaded.Owner("reframework/data/rsz/readme.txt").Name == "SF6 Mod Suite", "shared owners: the later section's copy is in place");
                // Locked for a moment (Fluffy rewriting it): read after a retry.
                using (var hold = new FileStream(fluffy.Ini, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    var release = new Timer(delegate { hold.Dispose(); }, null, 120, Timeout.Infinite);
                    loaded = fluffy.Load();
                    release.Dispose();
                }
                Require(loaded.Mods.Count == 2 && loaded.Warnings.Count == 0, "a briefly locked installed.ini was not read: " + String.Join("; ", loaded.Warnings));
                changed.Reset(); start = DateTime.UtcNow;
                File.Delete(fluffy.Ini);
                Require(changed.WaitOne(2000), "the watcher did not report the removal within 2 s");
                double removeMs = (DateTime.UtcNow - start).TotalMilliseconds;
                Require(fluffy.Load().Mods.Count == 0, "installed.ini removed: no mods");
                Console.WriteLine("NOTE watcher: install seen after " + (int)installMs + " ms, removal after " + (int)removeMs + " ms (250 ms debounce)");
            }
        }

        // ---- Uninstalling Fluffy mods
        private static void SetUp(ReplaceChecks.Fixture f, FakeFluffy fluffy, out string scripts, out string suite)
        {
            scripts = fluffy.Install("Scripts", "Updated scripts + rsz (fixed)", "normal", new Dictionary<string, byte[]> {
                { "reframework/data/rsz/readme.txt", B("scripts readme") }, { "reframework/autorun/scripts.lua", B("-- scripts") } }, true, null);
            // The Mod Suite: overwrites the genuine AGS (backed up), a helper renamed to _FMMBACKUP, the shared readme (no backup:
            // it is Scripts' file), new files in new folders; one line with \ and other case.
            string helper = G(f, "tools/helper.exe");
            File.Move(helper, G(f, "tools/helper_FMMBACKUP.exe"));
            suite = fluffy.Install("SF6_ModSuite_AllInOne", "SF6 Mod Suite", "normal", new Dictionary<string, byte[]> {
                { "amd_ags_x64.dll", B("suite proxy") }, { "reframework/plugins/Tool.dll", B("suite tool") }, { "reframework/data/rsz/readme.txt", B("suite readme") },
                { "reframework/autorun/suite/sub/x.lua", B("-- x") }, { "tools/helper.exe", B("suite helper") }, { "SF6 Mini Window - global tab.zip", B("zip in zip") } }, false,
                new Dictionary<string, string> { { "reframework/plugins/Tool.dll", "reframework\\Plugins\\TOOL.dll" } });
        }



        // ---- Game files other tools change (the user's 2026-10-14 case)
        internal const string Proxy196 = "slotsloader 1.9.6 proxy", Plugin196 = "slotsloader 1.9.6 plugin";
        internal static void RegisterBuilds()
        {
            KnownBuilds.Extra[ReplaceEngine.Sha(B(Proxy196))] = "the Slotsloader 1.9.6 proxy";
            KnownBuilds.Extra[ReplaceEngine.Sha(B(Plugin196))] = "the Slotsloader 1.9.6 plugin";
        }
        private static ReplaceMod SuiteOverTool(ReplaceChecks.Fixture f, FakeFluffy fluffy)
        {
            ReplaceMod tool = ReplaceChecks.Install(f, "tool", "SF6 Slots Loader 1.9.7");
            fluffy.Install("SF6_ModSuite_AllInOne_v1.11.2", "SF6 Mod Suite", "normal", new Dictionary<string, byte[]> {
                { "amd_ags_x64.dll", B(Proxy196) }, { "amd_ags_x64_real.dll", B("genuine AMD AGS library") }, { "reframework/plugins/SF6_CostumeSlotsNative.dll", B(Plugin196) }, { "SF6_SlotsClean.exe", B("clean") } }, false, null);
            return tool;
        }

        // 1.0.1: copies of another tool's files ("changed-*") left in the backups folder.
        internal static int ChangedCopies(string data) { string dir = Path.Combine(data, "replace-backups"); return Directory.Exists(dir) ? Directory.GetFiles(dir, "changed-*").Length : 0; }
        internal static void ForeignOverwrite(string root)
        {
            RegisterBuilds();
            // Keep current file.
            ReplaceChecks.Fixture f = Game(Path.Combine(root, "keep"), true);
            string pak = Path.Combine(f.Game, "re_chunk_000.pak"), pakSha = ReplaceEngine.Sha(pak);
            FakeFluffy fluffy = Attach(f, Path.Combine(root, "keep"));
            ReplaceMod tool = SuiteOverTool(f, fluffy);
            GameFilesStatus status = f.Engine.Status();
            Require(status.FilesChanged == 2 && status.Summary.Contains("SF6 Mod Suite") && status.Details[0].Contains("Slotsloader 1.9.6 proxy"), "status must name the changer: " + status.Summary + " / " + String.Join(" | ", status.Details));
            ReplaceChecks.Refused(delegate { f.Engine.SetEnabled(tool.Id, false); }, "external mod manager's mod 'SF6 Mod Suite' (the Slotsloader 1.9.6 proxy)");
            var before = ReplaceChecks.Snapshot(f.Game);
            DriftException drift = null;
            try { f.Engine.Uninstall(tool.Id); } catch (DriftException ex) { drift = ex; }
            Require(drift != null && drift.Items.Count == 2 && drift.Items.All(i => i.Changer.Contains("SF6 Mod Suite")) && drift.Message.Contains("Restore original") && drift.Message.Contains("Keep current file"), "uninstall must ask with the changer named: " + (drift == null ? "no question" : drift.Message));
            ReplaceChecks.SameSnapshot(before, ReplaceChecks.Snapshot(f.Game), "after the question");
            Require(f.Engine.LoadState().Mods.Count == 1 && !Directory.Exists(Path.Combine(f.Data, "replace-backups")) || Directory.GetFiles(Path.Combine(f.Data, "replace-backups"), "changed-*").Length == 0, "the question left changes behind");
            f.Engine.Uninstall(tool.Id, DriftChoice.KeepCurrent);
            ReplaceState s = f.Engine.LoadState();
            Require(Text(G(f, "amd_ags_x64.dll")) == Proxy196 && Text(G(f, "reframework/plugins/SF6_CostumeSlotsNative.dll")) == Plugin196, "Keep current file did not keep the Suite's files");
            Require(Text(G(f, "amd_ags_x64_real.dll")) == "genuine AMD AGS library", "the shared amd_ags_x64_real.dll (the Suite lists it too) must stay for Fluffy");
            Require(!File.Exists(G(f, "reframework/autorun/test.lua")), "the tool's own file was not removed");
            Require(s.Mods.Count == 0 && s.Deployed.Count == 0 && s.Released.Count == 1 && s.Released[0].Path == "amd_ags_x64.dll" && s.Released[0].Original != null && File.Exists(s.Released[0].Original.Backup) && s.Released[0].Changer.Contains("SF6 Mod Suite"), "the genuine AGS must stay restorable: " + String.Join(", ", s.Released.Select(r => r.Path)));
            var listing = new RecoveryListing(); RecoveryForm.AddGameFileEntries(f.Engine, listing);
            Require(listing.Entries.Any(e => e.Released != null && e.Name == "Original amd_ags_x64.dll" && e.TypeLabel == "Original file"), "Recovery does not list the original");
            Require(!f.Engine.Status().NeedsReapply && f.Engine.Status().Details.Any(d => d.Contains("kept from another tool")), "status after Keep current file: " + f.Engine.Status().Summary);
            // The external manager's own uninstall of the Suite (1.0.1: done there, never here) puts back what it had
            // backed up (this app's 1.9.7 files) and deletes the rest.
            string backup = Path.Combine(fluffy.Root, "Backup");
            foreach (string kept in Directory.GetFiles(backup, "*", SearchOption.AllDirectories)) { File.Copy(kept, Path.Combine(f.Game, kept.Substring(backup.Length + 1)), true); File.Delete(kept); }
            File.Delete(G(f, "SF6_SlotsClean.exe")); fluffy.Sections.Clear(); fluffy.Listed.Clear(); fluffy.Write();
            Require(Text(G(f, "amd_ags_x64.dll")) == "slotsloader proxy" && f.Engine.ExternalLockReason() == null, "fixture: the external manager's uninstall of the Suite");
            f.Engine.RestoreReleasedOriginal("amd_ags_x64.dll");
            s = f.Engine.LoadState();
            // 1.0.1: the file it replaced (another tool's) is not kept once the change commits.
            Require(Text(G(f, "amd_ags_x64.dll")) == "genuine AMD AGS library" && s.Released.Count == 0 && s.ForeignCopies.Count == 0 && ChangedCopies(f.Data) == 0, "Restore original game file: released " + s.Released.Count + ", foreign copies " + s.ForeignCopies.Count + ", changed-* files " + ChangedCopies(f.Data));
            Require(ReplaceEngine.Sha(pak) == pakSha, "a PAK was changed");

            // Restore original.
            ReplaceChecks.Fixture g = Game(Path.Combine(root, "restore"), true);
            FakeFluffy fg = Attach(g, Path.Combine(root, "restore"));
            tool = SuiteOverTool(g, fg);
            g.Engine.Uninstall(tool.Id, DriftChoice.RestoreOriginal);
            s = g.Engine.LoadState();
            Require(Text(G(g, "amd_ags_x64.dll")) == "genuine AMD AGS library" && !File.Exists(G(g, "reframework/plugins/SF6_CostumeSlotsNative.dll")) && !File.Exists(G(g, "reframework/autorun/test.lua")), "Restore original did not restore the genuine file / remove the plugin");
            Require(Text(G(g, "amd_ags_x64_real.dll")) == "genuine AMD AGS library", "amd_ags_x64_real.dll must stay for the Suite");
            // 1.0.1: the Suite's files are copied only as this change's rollback; once it committed the copies are gone.
            Require(s.Mods.Count == 0 && s.Released.Count == 0 && s.ForeignCopies.Count == 0 && ChangedCopies(g.Data) == 0, "the Suite's files were kept after the change: foreign copies " + s.ForeignCopies.Count + ", changed-* files " + ChangedCopies(g.Data));
            Require(!g.Engine.Status().NeedsReapply, "status after Restore original");
        }

        internal static void MissingFiles(string root)
        {
            ReplaceChecks.Fixture f = Game(Path.Combine(root, "game"), true);
            var clean = ReplaceChecks.Snapshot(f.Game);
            ReplaceMod tool = ReplaceChecks.Install(f, "tool", "Tool");
            File.Delete(G(f, "amd_ags_x64.dll"));
            DriftException drift = null;
            try { f.Engine.Uninstall(tool.Id); } catch (DriftException ex) { drift = ex; }
            Require(drift != null && drift.Items.Count == 1 && drift.Items[0].Missing && drift.Items[0].HasOriginal && drift.Message.Contains("is missing"), "a missing file with a backed-up original must be asked about: " + (drift == null ? "none" : drift.Message));
            f.Engine.Uninstall(tool.Id, DriftChoice.RestoreOriginal);
            ReplaceChecks.SameSnapshot(clean, ReplaceChecks.Snapshot(f.Game), "missing + Restore original");
            tool = ReplaceChecks.Install(f, "tool", "Tool");
            File.Delete(G(f, "amd_ags_x64.dll")); File.Delete(G(f, "reframework/autorun/test.lua"));   // test.lua had no original: no question
            f.Engine.Uninstall(tool.Id, DriftChoice.KeepCurrent);
            ReplaceState s = f.Engine.LoadState();
            Require(!File.Exists(G(f, "amd_ags_x64.dll")) && s.Released.Count == 1 && s.Released[0].Original != null && s.Released[0].ForeignSha == "", "missing + Keep current file must keep it missing with the original restorable");
            drift = null;
            try { f.Engine.RestoreAll(); } catch (DriftException ex) { drift = ex; }
            Require(drift != null && drift.Items.Single().Kept, "restore-all must ask about kept files");
            f.Engine.RestoreAll(DriftChoice.RestoreOriginal);
            ReplaceChecks.SameSnapshot(clean, ReplaceChecks.Snapshot(f.Game), "restore-all with Restore original after Keep current file");
            Require(f.Engine.LoadState().Released.Count == 0 && f.Engine.LoadState().Originals.Count == 0, "records left after restore-all");
        }

        internal static void PriorityStack(string root)
        {
            int n = 0;
            foreach (string order in new[] { "BA", "AB", "moved" })
            {
                ReplaceChecks.Fixture f = Game(Path.Combine(root, order), true);
                ReplaceChecks.Put(f.Mods, "tool2/Tool Pack 2/modinfo.ini", B("name=Tool Pack 2\r\n"));
                ReplaceChecks.Put(f.Mods, "tool2/Tool Pack 2/amd_ags_x64.dll", B("slotsloader proxy v2"));
                ReplaceChecks.Put(f.Mods, "tool2/Tool Pack 2/amd_ags_x64_real.dll", B("genuine AMD AGS library"));
                // 2.4.1: another proxy tool, not a SlotsLoader copy (a second SlotsLoader now updates the first in place).
                ReplaceChecks.Put(f.Mods, "tool2/Tool Pack 2/reframework/plugins/OtherProxyTool.dll", B("plugin v2"));
                var clean = ReplaceChecks.Snapshot(f.Game);
                ReplaceMod a = ReplaceChecks.Install(f, "tool", "A"), b = ReplaceChecks.Install(f, "tool2", "B");
                Func<string> proxy = delegate { return Text(G(f, "amd_ags_x64.dll")); };
                Require(proxy() == "slotsloader proxy v2" && File.Exists(G(f, "amd_ags_x64_real.dll")) && f.Engine.LoadState().Originals.Count == 1, order + ": the newest install must win and one genuine backup be kept");
                if (order == "moved") { f.Engine.Move(b.Id, 1); Require(proxy() == "slotsloader proxy" && Text(G(f, "reframework/plugins/SF6_CostumeSlotsNative.dll")) == "plugin", "moved: A must win after Move down"); }
                ReplaceMod first = order == "BA" ? b : a, second = order == "BA" ? a : b;
                if (order == "moved") { first = a; second = b; }
                f.Engine.Uninstall(first.Id);
                string expected = second == a ? "slotsloader proxy" : "slotsloader proxy v2";
                Require(proxy() == expected && File.Exists(G(f, "amd_ags_x64_real.dll")), order + ": after the first uninstall the other tool mod's proxy (and _real) must be in place: " + proxy());
                f.Engine.Uninstall(second.Id);
                ReplaceChecks.SameSnapshot(clean, ReplaceChecks.Snapshot(f.Game), order + ": both tool mods uninstalled (genuine amd_ags_x64.dll, no _real)");
                n++;
            }
            Require(n == 3, "orders");
        }

        // ---- CLI (in-process, same Data as the app): Fluffy commands and the drift flags.
        internal static void CommandLine(string root)
        {
            RegisterBuilds();
            ReplaceChecks.Fixture f = Game(Path.Combine(root, "game"), true);
            FakeFluffy fluffy = new FakeFluffy(Path.Combine(root, "Fluffy", "Games", "SF6"), f.Game);
            ReplaceSettings saved = ReplaceSettings.Load();
            try
            {
                var settings = ReplaceSettings.Load(); settings.FluffyRoots = new List<string> { fluffy.Root }; settings.Method = ReplaceMethods.Loose; settings.Save();
                string install = ReplaceChecks.Cli("replace-install", f.Mod("tool"), "--game", f.Game, "--name", "SF6 Slots Loader 1.9.7");
                Require(install.StartsWith("0\n"), "replace-install: " + install);
                fluffy.Install("SF6_ModSuite_AllInOne_v1.11.2", "SF6 Mod Suite", "normal", new Dictionary<string, byte[]> {
                    { "amd_ags_x64.dll", B(Proxy196) }, { "amd_ags_x64_real.dll", B("genuine AMD AGS library") }, { "reframework/plugins/SF6_CostumeSlotsNative.dll", B(Plugin196) } }, false, null);
                fluffy.Install("Pak", "Cammy PAK mod", "pak_mods", new Dictionary<string, byte[]> { { ReplaceChecks.V5, B("pak mesh") } }, false, null);   // 2.4.2: an installtype the Manager can't undo
                string status = ReplaceChecks.Cli("replace-status", "--game", f.Game);
                Require(status.Contains("amd_ags_x64.dll (by the external mod manager's mod 'SF6 Mod Suite' (the Slotsloader 1.9.6 proxy))"), "replace-status must name the changer: " + status);
                string fs = ReplaceChecks.Cli("fluffy-status", "--game", f.Game);
                Require(fs.StartsWith("0\n") && fs.Contains("[SF6_ModSuite_AllInOne_v1.11.2] SF6 Mod Suite · normal · 3 files · Uninstall it in the external mod manager.") && fs.Contains("Cammy PAK mod · pak_mods · 1 file · Uninstall it in the external mod manager."), "fluffy-status: " + fs);
                var before = ReplaceChecks.Snapshot(f.Game);
                // 1.0.1: the lockout: with mods in the external manager a replacement doesn't install.
                string locked = ReplaceChecks.Cli("replace-install", f.Mod("modA"), "--game", f.Game, "--name", "Cammy Colors", "--no-loader");
                Require(!locked.StartsWith("0\n") && locked.Contains("only Extra outfit, Slotsloader and REFramework install"), "replace-install during the lockout: " + locked);
                ReplaceChecks.SameSnapshot(before, ReplaceChecks.Snapshot(f.Game), "after the refused CLI install");
                string asked = ReplaceChecks.Cli("replace-uninstall", "SF6 Slots Loader 1.9.7", "--game", f.Game);
                Require(asked.StartsWith("1\n") && asked.Contains("amd_ags_x64.dll was replaced by the external mod manager's mod 'SF6 Mod Suite'") && asked.Contains("--restore-originals") && asked.Contains("--keep-current"), "replace-uninstall without a choice: " + asked);
                ReplaceChecks.SameSnapshot(before, ReplaceChecks.Snapshot(f.Game), "after the refused CLI uninstall");
                string both = ReplaceChecks.Cli("replace-uninstall", "SF6 Slots Loader 1.9.7", "--game", f.Game, "--keep-current", "--restore-originals");
                Require(both.StartsWith("2\n") && both.Contains("not both"), "both flags: " + both);
                string keep = ReplaceChecks.Cli("replace-uninstall", "SF6 Slots Loader 1.9.7", "--game", f.Game, "--keep-current");
                Require(keep.StartsWith("0\n") && keep.Contains("Kept from another tool: amd_ags_x64.dll") && Text(G(f, "amd_ags_x64.dll")) == Proxy196, "replace-uninstall --keep-current: " + keep);
                // 1.0.1: the external manager's mods are uninstalled there, never here.
                var kept = ReplaceChecks.Snapshot(f.Game);
                string un = ReplaceChecks.Cli("fluffy-uninstall", "SF6 Mod Suite", "--game", f.Game);
                Require(un.StartsWith("1\n") && un.Contains("Uninstall it in the external mod manager.") && fluffy.Load().Mods.Any(m => m.Name == "SF6 Mod Suite"), "fluffy-uninstall: " + un);
                ReplaceChecks.SameSnapshot(kept, ReplaceChecks.Snapshot(f.Game), "after the refused fluffy-uninstall");
                // Its own uninstall puts back what it had backed up and removes its sections.
                string backup = Path.Combine(fluffy.Root, "Backup");
                foreach (string file in Directory.GetFiles(backup, "*", SearchOption.AllDirectories)) { File.Copy(file, Path.Combine(f.Game, file.Substring(backup.Length + 1)), true); File.Delete(file); }
                fluffy.Sections.Clear(); fluffy.Listed.Clear(); fluffy.Write();
                Require(Text(G(f, "amd_ags_x64.dll")) == "slotsloader proxy", "fixture: the external manager's own uninstall");
                string original = ReplaceChecks.Cli("replace-restore-original", "amd_ags_x64.dll", "--game", f.Game);
                Require(original.StartsWith("0\n") && Text(G(f, "amd_ags_x64.dll")) == "genuine AMD AGS library", "replace-restore-original: " + original);
            }
            finally { saved.Save(); }
        }
    }
}
