using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace SlotsInstaller
{
    internal static class PakOwnershipChecks
    {
        internal static void LiveJournal(string root)
        {
            var f = ReplaceChecks.Create(root, false, ReplaceMethods.Pak);
            var original = ReplaceChecks.Snapshot(f.Game);
            var mod = ReplaceChecks.Install(f, "modA", "A");
            bool checkedLive = false;
            using (OperationContext.Begin(CancellationToken.None, delegate(OperationProgress progress)
            {
                if (checkedLive || progress.Stage != "Applying game file changes") return;
                var reader = new ReplaceEngine(f.Game, f.Data, f.Engine.Settings);
                Exception failure = null;
                var worker = new Thread(delegate()
                {
                    try
                    {
                        ReplaceChecks.Require(reader.JournalPending && reader.Recover(true) == null && reader.JournalPending, "refresh recovered a live transaction");
                        ReplaceChecks.Refused(delegate { reader.Recover(); }, "Another Manager task");
                    }
                    catch (Exception ex) { failure = ex; }
                });
                worker.Start(); worker.Join();
                if (failure != null) throw failure;
                checkedLive = true;
            })) f.Engine.Uninstall(mod.Id);
            ReplaceChecks.Require(checkedLive && !f.Engine.JournalPending && f.Engine.LoadState().Mods.Count == 0, "uninstall did not complete");
            ReplaceChecks.SameSnapshot(original, ReplaceChecks.Snapshot(f.Game), "after a refresh during uninstall");
        }

        private static void WritePak(string path, string asset, byte value)
        {
            PakWriter.WriteFixture(path, new[] { new KeyValuePair<string, byte[]>(asset, new[] { value }) }, 0, 0);
        }

        internal static void JournalGame(string root)
        {
            var first = ReplaceChecks.Create(Path.Combine(root, "a"), false, ReplaceMethods.Pak);
            var second = ReplaceChecks.Create(Path.Combine(root, "b"), false, ReplaceMethods.Pak);
            var other = new ReplaceEngine(second.Game, first.Data, first.Engine.Settings);
            var a = ReplaceChecks.Snapshot(first.Game);
            var b = ReplaceChecks.Snapshot(second.Game);
            Exception failure = null;
            using (first.Engine.Lock())
            {
                var t = new Thread(delegate()
                {
                    try { ReplaceChecks.Refused(delegate { using (other.Lock()) { } }, "Another Manager task"); }
                    catch (Exception ex) { failure = ex; }
                });
                t.Start(); t.Join();
            }
            if (failure != null) throw failure;
            ReplaceEngine.CrashAfterStep = 1;
            try { ReplaceChecks.Refused(delegate { ReplaceChecks.Install(first, "modB", "First install"); }, null); }
            finally { ReplaceEngine.CrashAfterStep = -1; }
            string journal = File.ReadAllText(first.Engine.JournalPath);
            ReplaceChecks.Refused(delegate { other.Recover(true); }, first.Game);
            ReplaceChecks.Require(File.ReadAllText(first.Engine.JournalPath) == journal, "foreign recovery changed the journal");
            ReplaceChecks.SameSnapshot(b, ReplaceChecks.Snapshot(second.Game), "wrong game after refused recovery");
            // A legacy first-install journal cannot safely infer its owner from the selected game.
            var legacy = ReplaceEngine.ReadJsonShared<ReplaceJournal>(first.Engine.JournalPath);
            legacy.GameRoot = null; DurableFile.WriteJson(first.Engine.JournalPath, legacy);
            ReplaceChecks.Refused(delegate { other.Recover(); }, "does not identify its game folder");
            File.WriteAllText(first.Engine.JournalPath, journal);
            first.Engine.Recover();
            ReplaceChecks.SameSnapshot(a, ReplaceChecks.Snapshot(first.Game), "correct game after recovery");
            // A legacy journal from an established installation can use the recorded state's owner.
            var m = ReplaceChecks.Install(first, "modB", "Established");
            ReplaceEngine.CrashAfterStep = 1;
            try { ReplaceChecks.Refused(delegate { first.Engine.Uninstall(m.Id); }, null); }
            finally { ReplaceEngine.CrashAfterStep = -1; }
            legacy = ReplaceEngine.ReadJsonShared<ReplaceJournal>(first.Engine.JournalPath);
            legacy.GameRoot = null; DurableFile.WriteJson(first.Engine.JournalPath, legacy);
            ReplaceChecks.Refused(delegate { other.Recover(); }, first.Game);
            first.Engine.Recover();
            first.Engine.Uninstall(m.Id);
            ReplaceChecks.SameSnapshot(a, ReplaceChecks.Snapshot(first.Game), "after legacy recovery");
        }

        internal static void ReassignedRow(string root)
        {
            var f = ReplaceChecks.Create(root, false, ReplaceMethods.Pak);
            var mod = ReplaceChecks.Install(f, "modB", "Body");
            var row = f.Engine.LoadState().Deployed.Single(d => d.Path == ReplaceChecks.V5).Zeroed.Single();
            string path = Path.Combine(f.Game, row.Pak);
            var pak = PakIndex.Open(path);
            DateTime writeTime = File.GetLastWriteTimeUtc(path);
            ulong foreign = PakHash.PathHash("natives/stm/another-asset.bin");
            PakWriter.WriteHashes(path, pak.Length, pak.Header, new[] { new PakHashWrite { Index = row.Index, Expected = new byte[8], Value = BitConverter.GetBytes(foreign) } });
            File.SetLastWriteTimeUtc(path, writeTime);
            ReplaceChecks.Install(f, "modDlc", "Unrelated");
            ReplaceChecks.Require(PakIndex.Open(path).Entry(row.Index).Hash == foreign && !f.Engine.LoadState().Deployed.Single(d => d.Path == ReplaceChecks.V5).Zeroed.Any(), "foreign row still claimed");
            f.Engine.Uninstall(mod.Id);
            ReplaceChecks.Require(!f.Engine.JournalPending && PakIndex.Open(path).Entry(row.Index).Hash == foreign, "foreign row changed on uninstall");
        }

        internal static void ReusedName(string root)
        {
            var f = ReplaceChecks.Create(root, false, ReplaceMethods.Pak);
            string src = Path.Combine(f.Mod("first"), "first.pak");
            Directory.CreateDirectory(Path.GetDirectoryName(src));
            WritePak(src, ReplaceChecks.V5, 1);
            var mod = ReplaceChecks.Install(f, "first", "First");
            string target = f.Engine.GamePath(mod.Files.Single().Path);
            ReplaceChecks.Require(ReplaceChecks.Check(f, "modB").CanInstall, "verified PAK not classified as managed");
            DateTime originalTime = File.GetLastWriteTimeUtc(target);
            File.Delete(target);
            WritePak(target, ReplaceChecks.V5, 2);  // same size as our file
            GamePaks.Forget(target);
            var changed = ReplaceChecks.Check(f, "modB");
            ReplaceChecks.Require(!changed.CanInstall && changed.Errors.Any(s => s.Contains("patch PAK")), "different same-size PAK trusted");
            File.Copy(src, target, true); File.SetLastWriteTimeUtc(target, originalTime); GamePaks.Forget(target);
            f.Engine.SetEnabled(mod.Id, false);
            // The foreign archive has a different member, but the same size and modification time.
            WritePak(target, ReplaceChecks.V1, 3);
            File.SetLastWriteTimeUtc(target, originalTime);
            var original = ReplaceChecks.Snapshot(f.Game);
            ReplaceChecks.Require(!ReplaceChecks.Check(f, "modA").CanInstall, "health used the previous patch's member list");
            ReplaceChecks.Refused(delegate { ReplaceChecks.Install(f, "modA", "Blocked"); }, "patch PAK");
            ReplaceChecks.SameSnapshot(original, ReplaceChecks.Snapshot(f.Game), "blocked overlap with disabled mod's former number");
        }

        internal static void NumberAllocation(string root)
        {
            var f = ReplaceChecks.Create(root, false, ReplaceMethods.Pak);
            string folder = f.Mod("addon"); Directory.CreateDirectory(folder);
            WritePak(Path.Combine(folder, "a.pak"), ReplaceChecks.V5, 11);
            WritePak(Path.Combine(folder, "b.pak"), ReplaceChecks.V6, 12);
            var package = PackageReader.Load(folder, "");
            var prepared = ReplacePlanner.PrepareInstall(Composer.Plan(package, Composer.DefaultSelection(package, true), true), "", f.Engine, false, null);
            try
            {
                var main = ReplaceChecks.Install(f, "badPak", "Main");
                var addon = ReplacePlanner.CommitInstall(prepared, f.Engine, "Addon", false, main.Id);
                ReplaceChecks.Require(main.Files.Single().Path == ReplaceEngine.PatchPakName(2) &&
                    addon.Files.Select(x => x.Path).SequenceEqual(new[] { ReplaceEngine.PatchPakName(3), ReplaceEngine.PatchPakName(4) }), "stale staged range collided");
                foreach (var file in main.Files.Concat(addon.Files)) ReplaceChecks.Require(ReplaceEngine.Sha(f.Engine.GamePath(file.Path)) == file.Sha256, file.Path + " is not its recorded payload");
                f.Engine.Uninstall(addon.Id);
                ReplaceChecks.Require(ReplaceEngine.Sha(f.Engine.GamePath(main.Files.Single().Path)) == main.Files.Single().Sha256, "addon uninstall changed main");
                f.Engine.SetEnabled(main.Id, false);
                WritePak(Path.Combine(f.Mod("badPak"), "z.pak"), ReplaceChecks.V6, 13);
                var upgrade = ReplaceChecks.Install(f, "badPak", "Expanded main");
                ReplaceChecks.Require(!upgrade.Enabled && upgrade.Files.Count == 2 && upgrade.Files.Select(x => x.Path).Distinct().Count() == 2,
                    "disabled upgrade lost a PAK payload");
                f.Engine.SetEnabled(upgrade.Id, true);
                foreach (var file in upgrade.Files) ReplaceChecks.Require(ReplaceEngine.Sha(f.Engine.GamePath(file.Path)) == file.Sha256, "upgraded payload differs");
            }
            finally { ReplacePlanner.Discard(prepared); }
        }

        internal static void FailedBackups(string root)
        {
            var f = ReplaceChecks.Create(root, false, ReplaceMethods.Pak);
            ReplaceChecks.Install(f, "modB", "Body");
            WritePak(f.Engine.GamePath(ReplaceEngine.PatchPakName(2)), ReplaceChecks.V5, 3);
            File.Delete(f.Engine.GamePath(ReplaceChecks.V5));
            const string early = "natives/aaa.user.2";
            ReplaceChecks.Put(f.Game, early, Encoding.UTF8.GetBytes("manual original"));
            ReplaceChecks.Put(f.Mods, "incoming/" + early, Encoding.UTF8.GetBytes("incoming"));
            ReplaceChecks.Put(f.Engine.BackupDir, "retained.bin", new byte[] { 55 });
            var before = ReplaceChecks.Snapshot(f.Engine.BackupDir);
            for (int i = 0; i < 2; i++)
            {
                ReplaceChecks.Refused(delegate { ReplaceChecks.Install(f, "incoming", "Incoming"); }, "patch PAK");
                ReplaceChecks.SameSnapshot(before, ReplaceChecks.Snapshot(f.Engine.BackupDir), "after a refused install");
                ReplaceChecks.Require(!f.Engine.JournalPending && f.Engine.LoadState().Originals.Count == 0, "refusal committed a backup");
            }
        }

        internal static void AddonOrder(string root)
        {
            var f = ReplaceChecks.Create(root, false, ReplaceMethods.Pak);
            var main = ReplaceChecks.Install(f, "badPak", "Main");
            string folder = f.Mod("addon"); Directory.CreateDirectory(folder);
            WritePak(Path.Combine(folder, "a.pak"), ReplaceChecks.V5, 1);
            WritePak(Path.Combine(folder, "b.pak"), ReplaceChecks.V5, 2);
            var package = PackageReader.Load(folder, "");
            var composition = Composer.Plan(package, Composer.DefaultSelection(package, true), true);
            var addon = ReplaceChecks.Install(f, "addon", "Addon");
            WritePak(Path.Combine(f.Mod("badPak"), "z.pak"), ReplaceChecks.V5, 3);
            main = ReplaceChecks.Install(f, "badPak", "Expanded main");
            var prepared = ReplacePlanner.PrepareInstall(composition, "", f.Engine, false, addon.Id);
            try { addon = ReplacePlanner.CommitInstall(prepared, f.Engine, "Addon", false, main.Id); }
            finally { ReplacePlanner.Discard(prepared); }
            int highestMain = main.Files.Max(x => ReplaceEngine.PatchNumber(x.Path));
            ReplaceChecks.Require(addon.Files.Count == 2 && addon.Files.All(x => ReplaceEngine.PatchNumber(x.Path) > highestMain), "main PAK shadows addon");
            var order = addon.Files.OrderBy(x => ReplaceEngine.PatchNumber(x.Path)).ToArray();
            ReplaceChecks.Require(ReplaceEngine.Sha(f.Engine.GamePath(order[0].Path)) == ReplaceEngine.Sha(Path.Combine(folder, "a.pak")) &&
                ReplaceEngine.Sha(f.Engine.GamePath(order[1].Path)) == ReplaceEngine.Sha(Path.Combine(folder, "b.pak")), "addon payload order changed");
        }

        private static PresetLayer Layer(string source, string name, int order)
        {
            var package = PackageReader.Load(source, "");
            return new PresetLayer { Package = package, Id = package.Components.Single(c => !c.IsDummy && c.HasFiles(true)).Id,
                Name = name, Stamp = MainForm.SourceStamp(source), Order = order };
        }

        internal static void PresetReimport(string root)
        {
            var f = ReplaceChecks.Create(root, false, ReplaceMethods.Pak);
            string source = f.Mod("owned"); Directory.CreateDirectory(source);
            string pak = Path.Combine(source, "body.pak");
            WritePak(pak, ReplaceChecks.V5, 1);
            Func<List<PresetLayer>> layers = delegate { return new List<PresetLayer> { Layer(source, "PAK", 0), Layer(f.Mod("modB"), "Loose", 1) }; };
            var original = f.Engine.ImportLayers(layers(), "");
            File.Delete(pak); WritePak(pak, ReplaceChecks.V5, 2);
            var updated = f.Engine.ImportLayers(layers(), "");
            ReplaceChecks.Require(updated.Count == 2 && updated[0].Id != original[0].Id && updated[0].Files.Single().Path == original[0].Files.Single().Path &&
                ReplaceEngine.Sha(f.Engine.GamePath(updated[0].Files.Single().Path)) == ReplaceEngine.Sha(pak), "PAK preset update failed");
        }

        internal static void PresetBackups(string root)
        {
            var f = ReplaceChecks.Create(root, false, ReplaceMethods.Pak);
            ReplaceChecks.Put(f.Game, ReplaceChecks.V5, new byte[] { 3 });
            var before = ReplaceChecks.Snapshot(f.Game);
            ReplaceChecks.Put(f.Engine.BackupDir, "existing.bin", new byte[] { 5 });
            var backups = ReplaceChecks.Snapshot(f.Engine.BackupDir);
            FileStream reader = null;
            ReplaceEngine.BeforeArchivePayloads = delegate { reader = new FileStream(f.Engine.JournalPath, FileMode.Open, FileAccess.Read, FileShare.Read); };
            try { ReplaceChecks.Refused(delegate { f.Engine.ImportLayers(new List<PresetLayer> { Layer(f.Mod("modB"), "Body", 0) }, ""); }, null); }
            finally { ReplaceEngine.BeforeArchivePayloads = null; if (reader != null) reader.Dispose(); }
            ReplaceChecks.Require(reader != null, "journal promotion was not exercised");
            f.Engine.Recover();
            ReplaceChecks.SameSnapshot(before, ReplaceChecks.Snapshot(f.Game), "after failed journal promotion");
            ReplaceChecks.SameSnapshot(backups, ReplaceChecks.Snapshot(f.Engine.BackupDir), "after recovering the preparation journal");
        }
    }
}
