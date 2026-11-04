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

        internal static void ReassignedRow(string root)
        {
            var f = ReplaceChecks.Create(root, false, ReplaceMethods.Pak);
            var mod = ReplaceChecks.Install(f, "modB", "Body");
            var row = f.Engine.LoadState().Deployed.Single(d => d.Path == ReplaceChecks.V5).Zeroed.Single();
            string path = Path.Combine(f.Game, row.Pak);
            var pak = PakIndex.Open(path);
            ulong foreign = PakHash.PathHash("natives/stm/another-asset.bin");
            PakWriter.WriteHashes(path, pak.Length, pak.Header, new[] { new PakHashWrite { Index = row.Index, Expected = new byte[8], Value = BitConverter.GetBytes(foreign) } });
            GamePaks.Forget(path);
            ReplaceChecks.Install(f, "modDlc", "Unrelated");
            ReplaceChecks.Require(PakIndex.Open(path).Entry(row.Index).Hash == foreign && !f.Engine.LoadState().Deployed.Single(d => d.Path == ReplaceChecks.V5).Zeroed.Any(), "foreign row still claimed");
            f.Engine.Uninstall(mod.Id);
            ReplaceChecks.Require(PakIndex.Open(path).Entry(row.Index).Hash == foreign, "foreign row changed on uninstall");
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
            File.Delete(target);
            WritePak(target, ReplaceChecks.V5, 2);  // same size as our file
            GamePaks.Forget(target);
            var changed = ReplaceChecks.Check(f, "modB");
            ReplaceChecks.Require(!changed.CanInstall && changed.Errors.Any(s => s.Contains("patch PAK")), "different same-size PAK trusted");
            File.Copy(src, target, true); GamePaks.Forget(target);
            f.Engine.SetEnabled(mod.Id, false);
            WritePak(target, ReplaceChecks.V5, 3);
            var original = ReplaceChecks.Snapshot(f.Game);
            ReplaceChecks.Refused(delegate { ReplaceChecks.Install(f, "modB", "Blocked"); }, "patch PAK");
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
    }
}
