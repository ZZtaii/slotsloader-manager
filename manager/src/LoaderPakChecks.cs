using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace SlotsInstaller
{
    // 2.4.0 (replace suite): the costume pak SlotsLoader generates is taken out of the patch row when this app
    // uninstalls SlotsLoader; foreign paks never change; the orphan case is reported and removable; the
    // owned-outfit marker file is restored after a loader install/update and Reapply, and never deleted.
    internal static class LoaderPakChecks
    {
        private static void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
        private static byte[] B(string s) { return Encoding.UTF8.GetBytes(s); }
        private static string Pak(ReplaceChecks.Fixture f, int n) { return Path.Combine(f.Game, "re_chunk_000.pak.patch_" + n.ToString("000") + ".pak"); }
        private static void LoaderPak(string path, int files)
        {
            var list = new List<KeyValuePair<string, byte[]>> { new KeyValuePair<string, byte[]>(LoaderCostumePak.CostumeMarker, LoaderCostumePak.MarkerBytes) };
            for (int i = 0; i < files; i++) list.Add(new KeyValuePair<string, byte[]>("natives/stm/product/model/esf/esf030/0" + (10 + i) + "/esf030_0" + (10 + i) + ".mesh.2109108288", B("slot " + i)));
            if (File.Exists(path)) File.Delete(path);
            PakWriter.WriteFixture(path, list, 0, 0);
        }
        private static void ForeignPak(string path, string marker)
        {
            if (File.Exists(path)) File.Delete(path);
            PakWriter.WriteFixture(path, new List<KeyValuePair<string, byte[]>> { new KeyValuePair<string, byte[]>(marker ?? "natives/stm/product/ui/other_mod.tex.241106027", B("other mod")) }, 0, 0);
        }
        private static void Registry(ReplaceChecks.Fixture f, string fingerprint)
        {
            string path = Path.Combine(f.Game, LoaderCostumePak.RegistryRelative);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, "{\n  \"version\": 3,\n  \"fingerprint\": \"" + fingerprint + "\",\n  \"slots\": [\n    {\n      \"mod_id\": \"x\",\n      \"new_costume_no\": 10\n    }\n  ]\n}\n");
        }
        private static string Fingerprint(ReplaceChecks.Fixture f) { string t = File.ReadAllText(Path.Combine(f.Game, LoaderCostumePak.RegistryRelative)); int i = t.IndexOf("\"fingerprint\": \"") + 16; return t.Substring(i, t.IndexOf('"', i) - i); }
        private static string Hash(string path) { return LoaderRelease.HashFile(path); }

        // 1.0.18: Slotsloader's own costume pak never refuses a PAK-method replacement (GamePaks.IsLoaderCache). It
        // holds an outfit's copy of a new shared path (Sakura as Ryu's DriveTech mesh, 04/11: left from an uninstalled
        // outfit, or the outfit still installed), a vanilla path an older loader let through, and a slot table.
        // Install, update, disable, enable and uninstall all go through, the health check says what the game reads,
        // and no patch pak ever changes. Without the loader (orphan), a stage pak or a Manager PAK mod carrying the
        // marker, the pak is not Slotsloader's cache and the old rules hold.
        internal static void SharedPaths(string root)
        {
            var f = ReplaceChecks.Create(root, true, ReplaceMethods.Pak);
            const string drive = "natives/stm/product/model/esf/esf001/005/00/esf001_005_00.mesh.230110883";
            const string table = "natives/stm/product/charparam/esf/esf.scn.20";
            ReplaceChecks.Put(f.Game, LoaderCostumePak.PluginPath, B("fixture loader"));
            string cache = Pak(f, 2);
            Func<string, List<KeyValuePair<string, byte[]>>> cacheRows = marker => new List<KeyValuePair<string, byte[]>> {
                new KeyValuePair<string, byte[]>(marker, marker == LoaderCostumePak.CostumeMarker ? LoaderCostumePak.MarkerBytes : B("stage")),
                new KeyValuePair<string, byte[]>(drive, B("outfit copy of a new path")),
                new KeyValuePair<string, byte[]>(ReplaceChecks.V5, B("outfit copy an older loader let through")),
                new KeyValuePair<string, byte[]>(table, B("slot table")) };
            PakWriter.WriteFixture(cache, cacheRows(LoaderCostumePak.CostumeMarker), 0, 0);
            string cacheHash = Hash(cache);
            ReplaceChecks.Put(f.Mods, "drive/" + drive, B("drive tech fix 1"));
            ReplaceChecks.Put(f.Mods, "drive/" + ReplaceChecks.V5, B("replacement mesh 1"));
            ReplaceChecks.Put(f.Mods, "drive2/" + drive, B("drive tech fix 2"));
            ReplaceChecks.Put(f.Mods, "drive2/" + ReplaceChecks.V5, B("replacement mesh 2"));
            ReplaceChecks.Put(f.Mods, "table/" + table, B("costume scene mod"));
            var initial = ReplaceChecks.Snapshot(f.Game);
            Func<ReplacePlan, string, string, bool> has = (p, level, text) => p.Items.Any(i => i.Level == level && i.Text.Contains(text));

            // An older Slotsloader (no outfits.json flag, no recorded 1.10.9): a warning, never an error.
            ReplacePlan check = ReplaceChecks.Check(f, "drive");
            Require(check.CanInstall && has(check, "warn", "esf001_005_00.mesh.230110883 and esf009_002_01.mesh.230110883 are also in Slotsloader's outfit PAK (re_chunk_000.pak.patch_002.pak)")
                && has(check, "warn", "Until Slotsloader is updated to 1.10.9 or later") && !check.Items.Any(i => i.Level == "error"),
                "older loader: " + String.Join(" | ", check.Items.Select(i => i.Level + ": " + i.Text)));
            // The last build was by a 1.10.9 loader: what the game reads afterwards, as information.
            string report = Path.Combine(f.Game, "reframework", "data", "SF6_Costumes_Data", "outfits.json");
            Directory.CreateDirectory(Path.GetDirectoryName(report));
            File.WriteAllText(report, "{\n  \"generated\": \"2026-11-04 20:00\",\n  \"loader\": \"2026-11-04-1.10.9-slotsloader-fork\",\n  \"loose_files_kept\": true,\n  \"pak\": \"re_chunk_000.pak.patch_002.pak\",\n  \"outfits\": []\n}\n");
            initial = ReplaceChecks.Snapshot(f.Game);
            check = ReplaceChecks.Check(f, "drive");
            Require(check.CanInstall && has(check, "info", "leaves these files to this mod") && !has(check, "warn", "Until Slotsloader"), "1.10.9 loader: " + String.Join(" | ", check.Items.Select(i => i.Level + ": " + i.Text)));

            // Install, update (every file changes), disable, enable: no refusal, the cache never changes.
            ReplaceMod mod = ReplaceChecks.Install(f, "drive", "Sakura as Ryu C1");
            Require(File.ReadAllBytes(Path.Combine(f.Game, drive.Replace('/', '\\'))).SequenceEqual(B("drive tech fix 1")) && !PakIndex.Open(Path.Combine(f.Game, "re_chunk_000.pak")).Contains(PakHash.PathHash(ReplaceChecks.V5)), "install");
            ReplacePlan plan;
            mod = ReplaceChecks.Install(f, "drive2", "Sakura as Ryu C1", false, mod.Id, out plan);
            Require(File.ReadAllBytes(Path.Combine(f.Game, drive.Replace('/', '\\'))).SequenceEqual(B("drive tech fix 2")), "update");
            f.Engine.SetEnabled(mod.Id, false);
            Require(!File.Exists(Path.Combine(f.Game, drive.Replace('/', '\\'))), "disable");
            f.Engine.SetEnabled(mod.Id, true);
            Require(File.ReadAllBytes(Path.Combine(f.Game, ReplaceChecks.V5.Replace('/', '\\'))).SequenceEqual(B("replacement mesh 2")), "re-enable");
            Require(Hash(cache) == cacheHash, "Slotsloader's pak changed");
            // A slot table: installs, with the warning that Slotsloader's copy wins while it has extra outfits.
            check = ReplaceChecks.Check(f, "table");
            Require(check.CanInstall && has(check, "warn", "esf.scn.20 is a table Slotsloader rewrites in its outfit PAK"), "slot table: " + String.Join(" | ", check.Items.Select(i => i.Level + ": " + i.Text)));
            f.Engine.Uninstall(mod.Id);
            // The Manager's own list for the loader (ManagerInstalls.cs) is rewritten by every change.
            Func<Dictionary<string, string>, Dictionary<string, string>> game = s => s.Where(kv => !kv.Key.EndsWith("manager_installs.json", StringComparison.OrdinalIgnoreCase)).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
            ReplaceChecks.SameSnapshot(game(initial), game(ReplaceChecks.Snapshot(f.Game)), "after uninstalling the replacement");

            // A Manager PAK mod carrying the marker is a PAK mod (it wins in game), not Slotsloader's cache.
            string marked = f.Mod("markedPak"); Directory.CreateDirectory(marked);
            PakWriter.WriteFixture(Path.Combine(marked, "marked.pak"), new List<KeyValuePair<string, byte[]>> {
                new KeyValuePair<string, byte[]>(LoaderCostumePak.CostumeMarker, LoaderCostumePak.MarkerBytes), new KeyValuePair<string, byte[]>(drive, B("pak mod copy")) }, 0, 0);
            ReplaceMod pakMod = ReplaceChecks.Install(f, "markedPak", "Marked PAK mod");
            check = ReplaceChecks.Check(f, "drive");
            Require(check.CanInstall && has(check, "warn", "is also in the PAK mod 'Marked PAK mod'"), "marked PAK mod: " + String.Join(" | ", check.Items.Select(i => i.Level + ": " + i.Text)));
            f.Engine.Uninstall(pakMod.Id);
            // Without the loader nothing rebuilds the pak: a patch pak like any other.
            File.Delete(Path.Combine(f.Game, LoaderCostumePak.PluginPath.Replace('/', '\\')));
            check = ReplaceChecks.Check(f, "drive");
            Require(!check.CanInstall && has(check, "error", "is also in the patch PAK re_chunk_000.pak.patch_002.pak, which is never modified"), "orphan pak");
            Require(!f.Engine.LoadState().Mods.Any(), "records left");
            // The stage loader's pak is another tool's.
            ReplaceChecks.Put(f.Game, LoaderCostumePak.PluginPath, B("fixture loader"));
            File.Delete(cache); PakWriter.WriteFixture(cache, cacheRows(LoaderCostumePak.StageMarker), 0, 0);
            check = ReplaceChecks.Check(f, "drive");
            Require(!check.CanInstall && has(check, "error", "is also in the patch PAK re_chunk_000.pak.patch_002.pak"), "stage pak");
        }

        // Uninstall: sole pak removed, registry stamp cleared (slots kept), costume_mods and other files untouched.
        internal static void SolePak(string root)
        {
            ReplaceChecks.Fixture f = ReplaceChecks.Create(root, true, ReplaceMethods.Loose);
            File.Delete(Pak(f, 1));   // the fixture's other-mod pak: this game has only the loader's
            ReplaceMod loader = ReplaceChecks.Install(f, "tool", "SF6 Slots Loader");
            Require(LoaderCostumePak.IsLoaderMod(loader), "the fixture tool mod is not recognised as Slotsloader");
            LoaderPak(Pak(f, 1), 3); Registry(f, "abc123");
            string mods = Path.Combine(f.Game, "reframework", "costume_mods", "C. Viper"); Directory.CreateDirectory(mods); File.WriteAllText(Path.Combine(mods, "Grace Noir.zip"), "zip");
            string flag = Path.Combine(f.Game, UnownedSlotsFlag.TargetFolder, "fixture-flag.bin"); Directory.CreateDirectory(Path.GetDirectoryName(flag)); File.WriteAllText(flag, "");
            Require(LoaderCostumePak.Scan(f.Game).Single().Kind == PatchPakKind.Costume && f.Engine.Status().OrphanCostumePaks.Count == 0, "with Slotsloader installed the pak is not an orphan");
            string message = f.Engine.Uninstall(loader.Id);
            Require(!File.Exists(Pak(f, 1)) && message.Contains("Removed Slotsloader's costume pak (re_chunk_000.pak.patch_001.pak)"), "sole pak: " + message);
            Require(Fingerprint(f) == "" && File.ReadAllText(Path.Combine(f.Game, LoaderCostumePak.RegistryRelative)).Contains("\"new_costume_no\": 10"), "registry: the stamp must be cleared, the slots kept");
            Require(File.Exists(Path.Combine(mods, "Grace Noir.zip")) && File.Exists(flag), "costume_mods or the opt-out file was touched");
            Require(File.ReadAllText(Path.Combine(f.Game, "amd_ags_x64.dll")) == "genuine AMD AGS library", "the genuine amd_ags_x64.dll was not restored");
            // Reinstall: no pak and an empty stamp, so the loader rebuilds at the next game start.
            ReplaceMod again = ReplaceChecks.Install(f, "tool", "SF6 Slots Loader");
            Require(LoaderCostumePak.Scan(f.Game).Count == 0 && Fingerprint(f) == "" && f.Engine.Status().OrphanCostumePaks.Count == 0, "reinstall");
            f.Engine.Uninstall(again.Id);
        }

        // A mod pak above: a marker-only placeholder keeps the row unbroken; the foreign pak is byte-identical.
        internal static void Placeholder(string root)
        {
            ReplaceChecks.Fixture f = ReplaceChecks.Create(root, true, ReplaceMethods.Loose);
            File.Delete(Pak(f, 1));
            ReplaceMod loader = ReplaceChecks.Install(f, "tool", "SF6 Slots Loader");
            LoaderPak(Pak(f, 1), 2); ForeignPak(Pak(f, 2), null); ForeignPak(Pak(f, 3), LoaderCostumePak.StageMarker);
            string foreign2 = Hash(Pak(f, 2)), stage3 = Hash(Pak(f, 3));
            string message = f.Engine.Uninstall(loader.Id);
            var paks = LoaderCostumePak.Scan(f.Game);
            Require(paks.Select(p => p.Number).SequenceEqual(new[] { 1, 2, 3 }), "the patch row has a hole: " + String.Join(",", paks.Select(p => p.Number)));
            Require(paks[0].Kind == PatchPakKind.Costume && paks[0].Placeholder && new FileInfo(Pak(f, 1)).Length < 200, "patch_001 is not the loader's marker-only placeholder");
            Require(Hash(Pak(f, 2)) == foreign2 && Hash(Pak(f, 3)) == stage3 && paks[1].Kind == PatchPakKind.Mod && paks[2].Kind == PatchPakKind.Stage, "a foreign pak changed");
            Require(message.Contains("Emptied Slotsloader's costume pak (re_chunk_000.pak.patch_001.pak)") && LoaderCostumePak.Generated(f.Game).Count == 0 && f.Engine.Status().OrphanCostumePaks.Count == 0, "placeholder: " + message);
        }

        // Foreign paks (another mod's, Fluffy's, a stage pak) never change; the loader's pak above them is deleted.
        internal static void ForeignUntouched(string root)
        {
            ReplaceChecks.Fixture f = ReplaceChecks.Create(root, true, ReplaceMethods.Loose);
            ReplaceMod loader = ReplaceChecks.Install(f, "tool", "SF6 Slots Loader");
            string fixturePak = Hash(Pak(f, 1));   // the fixture's other-mod pak (Fluffy-style, no marker)
            LoaderPak(Pak(f, 2), 2);
            f.Engine.RestoreAll();
            Require(!File.Exists(Pak(f, 2)) && Hash(Pak(f, 1)) == fixturePak, "restore-all: the loader pak must go and patch_001 stay byte-identical");
            Require(LoaderCostumePak.Remove(f.Game) == "" && Hash(Pak(f, 1)) == fixturePak, "Remove with only a foreign pak changed something");
            // An uninstall of another tool mod (not SlotsLoader) never touches the loader's pak.
            LoaderPak(Pak(f, 2), 2); string loaderPak = Hash(Pak(f, 2));
            ReplaceMod other = ReplaceChecks.Install(f, "modC", "Costume with helper");
            f.Engine.Uninstall(other.Id);
            Require(File.Exists(Pak(f, 2)) && Hash(Pak(f, 2)) == loaderPak, "uninstalling another mod removed the loader's pak");
        }

        // SlotsLoader isn't installed but its pak is: status (Summary, OrphanCostumePaks), replace-status, Remove it.
        internal static void Orphan(string root)
        {
            ReplaceChecks.Fixture f = ReplaceChecks.Create(root, true, ReplaceMethods.Loose);
            LoaderPak(Pak(f, 2), 4);
            GameFilesStatus status = f.Engine.Status();
            Require(status.OrphanCostumePaks.SequenceEqual(new[] { "re_chunk_000.pak.patch_002.pak" }) && status.Summary == LoaderCostumePak.OrphanText + ".", "status: " + status.Summary);
            string cli = ReplaceChecks.Cli("replace-status", "--game", f.Game);
            // 1.0.11 review: no external mod manager is found here, so the foreign PAK is not called a leftover.
            Require(cli.Contains("re_chunk_000.pak.patch_002.pak (generated by Slotsloader, 5 entries") && cli.Contains("re_chunk_000.pak.patch_001.pak (another mod's, left alone)") && cli.Contains(LoaderCostumePak.OrphanText + ": re_chunk_000.pak.patch_002.pak"), "replace-status: " + cli);
            string removed = ReplaceChecks.Cli("loader-pak-remove", "--game", f.Game);
            Require(removed.StartsWith("0\n") && removed.Contains("Removed Slotsloader's costume pak (re_chunk_000.pak.patch_002.pak)") && !File.Exists(Pak(f, 2)) && File.Exists(Pak(f, 1)), "loader-pak-remove: " + removed);
            Require(f.Engine.Status().OrphanCostumePaks.Count == 0 && f.Engine.RemoveOrphanedCostumePak().StartsWith("No costume pak"), "after removal");
        }

        // The owned-outfit marker: Data\show_unowned_slots.txt names a file (a made-up name here) that is
        // restored after a loader install (CLI replace-install / update) and replace-reapply, never deleted by an
        // uninstall, and ignored when its first line is not a plain file name. Never logged by name.
        internal static void OwnedOutfitMarker(string root)
        {
            ReplaceChecks.Fixture f = ReplaceChecks.Create(root, true, ReplaceMethods.Loose);
            string marker = Path.Combine(AppConfig.DataDir, UnownedSlotsFlag.MarkerName), target = Path.Combine(f.Game, UnownedSlotsFlag.TargetFolder, "made-up-flag.dat");
            Require(!File.Exists(marker), "this run's Data already has a marker");
            try
            {
                File.WriteAllText(marker, "made-up-flag.dat\r\nsecond line ignored\r\n");
                string install = ReplaceChecks.Cli("replace-install", f.Mod("tool"), "--game", f.Game, "--name", "SF6 Slots Loader");
                Require(install.StartsWith("0\n") && File.Exists(target), "the marker file was not created by a loader install: " + install);
                File.Delete(target);
                Require(ReplaceChecks.Cli("replace-reapply", "--game", f.Game).StartsWith("0\n") && File.Exists(target), "Reapply did not restore it");
                ReplaceEngine app = ReplaceEngine.ForConfig(new AppConfig { GameRoot = f.Game });   // the CLI's engine (this run's Data)
                Require(!app.LoadState().Deployed.Any(d => d.Path.IndexOf("made-up-flag", StringComparison.OrdinalIgnoreCase) >= 0), "the marker file was recorded as deployed");
                string un = ReplaceChecks.Cli("replace-uninstall", "SF6 Slots Loader", "--game", f.Game);
                Require(un.StartsWith("0\n") && File.Exists(target) && app.LoadState().Mods.Count == 0, "the uninstall removed the marker file or failed: " + un);
                string log = File.Exists(Path.Combine(AppConfig.DataDir, "log.txt")) ? File.ReadAllText(Path.Combine(AppConfig.DataDir, "log.txt")) : "";
                Require(log.Contains("Owned-outfit opt-out marker restored.") && !log.Contains("made-up-flag"), "log: the restore must be logged without the name");
                foreach (string bad in new[] { "..\\escape.txt", "sub/file.txt", "CON", "nul.txt", "", new string('a', 65), "x:y" })
                {
                    File.WriteAllText(marker, bad); File.Delete(target);
                    Require(!UnownedSlotsFlag.Ensure(f.Game) && !File.Exists(target) && !Directory.EnumerateFiles(Path.Combine(f.Game, UnownedSlotsFlag.TargetFolder)).Any(), "an invalid marker created a file: '" + bad + "'");
                }
                File.Delete(marker);
                Require(!UnownedSlotsFlag.Ensure(f.Game) && !File.Exists(target), "without a marker nothing may happen");
            }
            finally { if (File.Exists(marker)) File.Delete(marker); }
        }
    }
}
