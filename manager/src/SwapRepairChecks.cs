using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace SlotsInstaller
{
    internal static class SwapRepairChecks
    {
        private const string ScenePath = "natives/stm/product/charparam/esf/esf009/esf009v01.scn.20";
        private static void Require(bool value, string message) { ReplaceChecks.Require(value, message); }
        internal static byte[] Scene(params uint[] types)
        {
            byte[] user = ReplaceChecks.Usr(types), scene = new byte[user.Length + 16];
            Encoding.ASCII.GetBytes("SCN").CopyTo(scene, 0);
            BitConverter.GetBytes(64UL).CopyTo(scene, 56);
            Buffer.BlockCopy(user, 48, scene, 64, user.Length - 48);
            BitConverter.GetBytes(SwapRepairs.TimelineOld).CopyTo(scene, scene.Length - 4); // identical payload bytes must stay
            return scene;
        }
        internal static void Run(string root) { Scenes(); Users(); Preparation(root); Reapply(Path.Combine(root, "reapply")); Flag(Path.Combine(root, "flag")); }

        // Retail files align instance data in the whole file, not relative to the RSZ header.
        private static byte[] ShiftRsz(byte[] input, bool scene)
        {
            byte[] output = (byte[])input.Clone(); int pointer = scene ? 56 : 32;
            int r = (int)BitConverter.ToUInt64(input, pointer), data = (int)BitConverter.ToUInt64(input, r + 32);
            Buffer.BlockCopy(input, r, output, r + 2, data - 2);
            BitConverter.GetBytes((ulong)(r + 2)).CopyTo(output, pointer);
            BitConverter.GetBytes((ulong)(data - 2)).CopyTo(output, r + 2 + 32);
            BitConverter.GetBytes((ulong)(data - 2)).CopyTo(output, r + 2 + 40);
            return output;
        }

        private static void Scenes()
        {
            byte[] old = Scene(SwapRepairs.TimelineType, SwapRepairs.TimelineOld), current = Scene(SwapRepairs.TimelineType, SwapRepairs.TimelineCurrent);
            // 1.0.5: off by default (Settings): no scene is touched and nothing is reported.
            byte[] data = (byte[])old.Clone();
            Require(ModFixes.UpgradeModFile(ref data, new GameReference { GameFile = delegate { return current; } }, ScenePath).Count == 0 && data.SequenceEqual(old), "swap repair ran while the setting is off");
            var reference = new GameReference { GameFile = delegate { return current; }, SwapRepairs = true };
            var fixes = ModFixes.UpgradeModFile(ref data, reference, ScenePath);
            Require(fixes.Count == 1 && fixes[0].Changed && fixes[0].Kind == "swap-scene" && data.SequenceEqual(current), "scene CRC repair changed something beyond the typed signature");
            Require(old.SequenceEqual(Scene(SwapRepairs.TimelineType, SwapRepairs.TimelineOld)), "repair changed source input buffer");
            Require(ModFixes.UpgradeModFile(ref data, reference, ScenePath).Count == 0 && data.SequenceEqual(current), "scene repair was not idempotent");
            byte[] shiftedOld = ShiftRsz(old, true), shiftedCurrent = ShiftRsz(current, true);
            var shiftedRef = new GameReference { GameFile = delegate { return shiftedCurrent; } };
            data = (byte[])shiftedOld.Clone();
            Require(SwapRepairs.RepairScene(ref data, shiftedRef, ScenePath).Changed && data.SequenceEqual(shiftedCurrent), "real-style nonaligned RSZ header was refused");
            byte[] other = Scene(0x12345678, SwapRepairs.TimelineOld), before = (byte[])other.Clone();
            Require(ModFixes.UpgradeModFile(ref other, reference, ScenePath).Count == 0 && other.SequenceEqual(before), "same CRC on another type was rewritten");
            var absent = new GameReference { GameFile = delegate(string path) { return path == SwapRepairs.ReferenceScene ? current : null; } };
            data = (byte[])old.Clone(); Require(SwapRepairs.RepairScene(ref data, absent, ScenePath).Changed && data.SequenceEqual(current), "missing target scene did not use the approved reference");
            var changedGame = new GameReference { GameFile = delegate(string path) { return path == ScenePath ? Scene(SwapRepairs.TimelineType, 0x11223344) : current; } };
            data = (byte[])old.Clone(); Require(SwapRepairs.RepairScene(ref data, changedGame, ScenePath).Warning && data.SequenceEqual(old), "unknown official scene was bypassed by fallback");
            data = (byte[])old.Clone(); Require(SwapRepairs.RepairScene(ref data, new GameReference(), ScenePath).Warning && data.SequenceEqual(old), "scene changed without verified official reference");
            data = Scene(SwapRepairs.TimelineType, 0xAABBCCDD); before = (byte[])data.Clone();
            Require(SwapRepairs.RepairScene(ref data, reference, ScenePath).Warning && data.SequenceEqual(before), "unknown mod CRC was upgraded");
            foreach (Action<byte[]> corrupt in new Action<byte[]>[] {
                b => BitConverter.GetBytes(UInt64.MaxValue).CopyTo(b,56), b => BitConverter.GetBytes(65UL).CopyTo(b,56),
                b => BitConverter.GetBytes(UInt32.MaxValue).CopyTo(b,76), b => BitConverter.GetBytes(0UL).CopyTo(b,88),
                b => BitConverter.GetBytes(17u).CopyTo(b,68), b => BitConverter.GetBytes(99u).CopyTo(b,112),
                b => BitConverter.GetBytes(1u).CopyTo(b,80), b => BitConverter.GetBytes(1u).CopyTo(b,4),
                b => BitConverter.GetBytes(1u).CopyTo(b,8), b => BitConverter.GetBytes(1u).CopyTo(b,12),
                b => BitConverter.GetBytes(1u).CopyTo(b,16), b => BitConverter.GetBytes(1u).CopyTo(b,20) })
            {
                data = (byte[])old.Clone(); corrupt(data); before = (byte[])data.Clone();
                FixResult result = SwapRepairs.RepairScene(ref data, reference, ScenePath);
                Require(result.Warning && !result.Changed && data.SequenceEqual(before), "malformed scene was modified");
            }
            foreach (int length in new[] { 0, 63, 64, 111, old.Length - 17 })
            {
                data = old.Take(length).ToArray(); before = (byte[])data.Clone();
                Require(SwapRepairs.RepairScene(ref data, reference, ScenePath).Warning && data.SequenceEqual(before), "truncated scene was modified");
            }
        }

        private static byte[] User(string prefix, byte marker)
        {
            byte[] head = ReplaceChecks.Usr(0x12345678, 0x87654321);
            head[head.Length - 1] = marker;
            return head.Concat(Encoding.Unicode.GetBytes(prefix + "/esf017_001_30_chain.chain\0Product/Model/esf/esf017/000/shared.chain\0")).ToArray();
        }
        private static void Users()
        {
            const string donorPath = "natives/stm/product/model/esf/esf017/001/esf017_001_30_chain.user.2";
            const string targetPath = "natives/stm/product/model/esf/esf030/002/esf017_001_30_chain.user.2";
            const string donor = "Product/Model/esf/esf017/001", target = "Product/Model/esf/esf030/002";
            byte[] old = User(target, 1), current = User(donor, 2);
            var recipe = new SwapRepairs.UserRecipe { Name = "Fixture chain", Basename = "esf017_001_30_chain.user.2", CurrentPath = donorPath, OldNormalizedSha256 = SwapRepairs.NormalizedSha(old, target), CurrentSha256 = ReplaceEngine.Sha(current) };
            var recipes = new[] { recipe };
            var reference = new GameReference { GameFile = delegate(string path) { return path == donorPath ? current : null; } };
            int n; byte[] expected = SwapRepairs.RemapPrefix(current, donor, target, out n), data = (byte[])old.Clone();
            Require(n == 1 && SwapRepairs.RepairUser(ref data, reference, targetPath, recipes).Changed && data.SequenceEqual(expected), "recognized chain was not rebuilt and remapped");
            Require(SwapRepairs.RepairUser(ref data, reference, targetPath, recipes) == null && data.SequenceEqual(expected), "chain repair is not idempotent");
            Require(current.SequenceEqual(User(donor, 2)) && old.SequenceEqual(User(target, 1)), "source or official buffer mutated");
            byte[] shiftedOld = ShiftRsz(old, false), shiftedCurrent = ShiftRsz(current, false);
            var shiftedRecipe = new SwapRepairs.UserRecipe { Name = "Nonaligned fixture chain", Basename = recipe.Basename, CurrentPath = donorPath, OldNormalizedSha256 = SwapRepairs.NormalizedSha(shiftedOld, target), CurrentSha256 = ReplaceEngine.Sha(shiftedCurrent) };
            var shiftedRef = new GameReference { GameFile = delegate { return shiftedCurrent; } };
            data = (byte[])shiftedOld.Clone();
            Require(SwapRepairs.RepairUser(ref data, shiftedRef, targetPath, new[] { shiftedRecipe }).Changed && data.SequenceEqual(SwapRepairs.RemapPrefix(shiftedCurrent, donor, target, out n)), "real-style USER RSZ header was refused");
            data = expected;
            Require(Encoding.Unicode.GetString(data).Contains("Product/Model/esf/esf017/000/shared.chain"), "unrelated/shared donor reference changed");
            byte[] normalized = SwapRepairs.RemapPrefix(old, target, "Product/Model/esf/esf000/000", out n);
            Require(ReplaceEngine.Sha(normalized) == recipe.OldNormalizedSha256 && n == 1, "canonical fingerprint differs");
            data = (byte[])old.Clone(); data[data.Length - 3] ^= 1; byte[] before = (byte[])data.Clone();
            Require(SwapRepairs.RepairUser(ref data, reference, targetPath, recipes).Warning && data.SequenceEqual(before), "custom physics settings overwritten");
            data = (byte[])old.Clone(); var changed = new GameReference { GameFile = delegate { return User(donor, 3); } };
            Require(SwapRepairs.RepairUser(ref data, changed, targetPath, recipes).Warning && data.SequenceEqual(old), "unapproved future game file was used");
            data = (byte[])old.Clone(); Require(SwapRepairs.RepairUser(ref data, new GameReference(), targetPath, recipes).Warning && data.SequenceEqual(old), "missing current file modified mod");
            data = (byte[])old.Clone(); Require(SwapRepairs.RepairUser(ref data, reference, targetPath.Replace("30_chain", "31_chain"), recipes) == null && data.SequenceEqual(old), "unlisted filename was repaired");
            data = (byte[])old.Clone(); data[0] = 0; before = (byte[])data.Clone();
            Require(SwapRepairs.RepairUser(ref data, reference, targetPath, recipes).Warning && data.SequenceEqual(before), "malformed USER was repaired");
            data = User("Product/Model/esf/esf031/002", 1); before = (byte[])data.Clone();
            Require(SwapRepairs.RepairUser(ref data, reference, targetPath, recipes).Warning && data.SequenceEqual(before), "path/target mismatch was accepted");

            const string ownDonor = "Product/Model/esf/esf028/001", ownTarget = "Product/Model/esf/esf028/002";
            const string ownPath = "natives/stm/product/model/esf/esf028/002/esf028_001_30_chain.user.2";
            byte[] ownOld = User(ownTarget, 1), ownCurrent = User(ownDonor, 2);
            var own = new SwapRepairs.UserRecipe { Name = "Fixture own-slot throw", Basename = "esf028_001_30_chain.user.2", OwnSlotOnly = true,
                CurrentPath = "natives/stm/product/model/esf/esf028/001/esf028_001_30_chain.user.2", OldNormalizedSha256 = SwapRepairs.NormalizedSha(ownOld, ownTarget), CurrentSha256 = ReplaceEngine.Sha(ownCurrent) };
            var ownRef = new GameReference { GameFile = delegate { return ownCurrent; } };
            data = (byte[])ownOld.Clone(); Require(SwapRepairs.RepairUser(ref data, ownRef, ownPath, new[] { own }).Changed, "Mai own-slot throw recipe did not apply");
            data = User("Product/Model/esf/esf030/002", 1); before = (byte[])data.Clone();
            Require(SwapRepairs.RepairUser(ref data, ownRef, ownPath.Replace("/esf028/002/", "/esf030/002/"), new[] { own }) == null && data.SequenceEqual(before), "Mai throw recipe incorrectly applied across fighters");
            data = User(ownDonor, 1); before = (byte[])data.Clone();
            Require(SwapRepairs.RepairUser(ref data, ownRef, own.CurrentPath, new[] { own }) == null && data.SequenceEqual(before), "Mai throw recipe applied to its unchanged original slot");

            const string partsPath = "natives/stm/product/charparam/esf/esf030/battle_ud/esf030v01_model_parts.user.2";
            const string partsDonor = "natives/stm/product/charparam/esf/esf017/battle_ud/esf017v00_model_parts.user.2";
            byte[] partsOld = ReplaceShared(User(target, 1), "esf017", "esf030"), partsCurrent = User(donor, 2);
            var partsRecipe = new SwapRepairs.UserRecipe { Name = "Fixture model parts", ModelParts = true, CurrentPath = partsDonor, OldNormalizedSha256 = SwapRepairs.NormalizedSha(partsOld, target), CurrentSha256 = ReplaceEngine.Sha(partsCurrent) };
            var partsRef = new GameReference { GameFile = delegate(string path) { return path == partsDonor ? partsCurrent : null; } };
            data = (byte[])partsOld.Clone();
            Require(SwapRepairs.RepairUser(ref data, partsRef, partsPath).Warning && data.SequenceEqual(partsOld), "unvalidated documented model-parts recipe was not reported");
            data = (byte[])partsOld.Clone();
            Require(SwapRepairs.RepairUser(ref data, partsRef, partsPath, new[] { partsRecipe }).Changed, "documented model-parts recipe failed");
            Require(SwapRepairs.RepairUser(ref data, partsRef, partsPath, new[] { partsRecipe }) == null, "model-parts repair was not idempotent");
            data = partsOld.Concat(Encoding.Unicode.GetBytes("Product/Model/esf/esf030/003/ambiguous.mesh\0")).ToArray(); before = (byte[])data.Clone();
            Require(SwapRepairs.RepairUser(ref data, partsRef, partsPath, new[] { partsRecipe }).Warning && data.SequenceEqual(before), "ambiguous target costume guessed");
        }
        private static byte[] ReplaceShared(byte[] data, string from, string to)
        {
            int changed; return SwapRepairs.RemapPrefix(data, "Product/Model/esf/" + from + "/000", "Product/Model/esf/" + to + "/000", out changed);
        }

        private static void Preparation(string root)
        {
            var f = ReplaceChecks.Create(root, false, ReplaceMethods.Pak);
            string pak = Path.Combine(f.Game, "re_chunk_000.pak"), mod = f.Mod("scene");
            byte[] old = Scene(SwapRepairs.TimelineType, SwapRepairs.TimelineOld), current = Scene(SwapRepairs.TimelineType, SwapRepairs.TimelineCurrent);
            Action<byte[], uint> write = delegate(byte[] scene, uint fingerprint)
            {
                File.Delete(pak);   // this generated fixture only; WriteFixture deliberately refuses overwrites
                PakWriter.WriteFixture(pak, new[] { new KeyValuePair<string, byte[]>(ScenePath, scene), new KeyValuePair<string, byte[]>(ModFixes.ReferenceCmdPath, ReplaceChecks.ReferenceCmd()) }, fingerprint, 0);
                GamePaks.Forget(pak);
            };
            write(current, 1);
            ReplaceChecks.Put(mod, ScenePath, old);
            ModPackage package = PackageReader.Load(mod, "");
            CompositionPlan composition = Composer.Plan(package, Composer.DefaultSelection(package, true), true);
            ReplacePlan unset = ReplacePlanner.Prepare(composition, "", f.Engine, Path.Combine(root, "off"));
            Require(!unset.Files.Single().Fixes.Any() && File.ReadAllBytes(unset.Files.Single().Source).SequenceEqual(old), "preparation repaired a scene with the setting off");
            f.Engine.Settings.SwapRepairs = true;
            ReplacePlan direct = ReplacePlanner.Prepare(composition, "", f.Engine, Path.Combine(root, "direct"));
            Require(direct.Files.Single().Fixes.Any(x => x.Kind == "swap-scene" && x.Changed) && File.ReadAllBytes(direct.Files.Single().Source).SequenceEqual(current), "uncached preparation skipped SCN");
            using (var cache = new PrepareCache())
            {
                ReplacePlan first = ReplacePlanner.Prepare(composition, "", f.Engine, Path.Combine(root, "cached"), cache, null);
                int extracted = cache.Extracted;
                ReplacePlan second = ReplacePlanner.Prepare(composition, "", f.Engine, Path.Combine(root, "cached2"), cache, null);
                Require(cache.Extracted == extracted && first.Files.Single().Sha256 == second.Files.Single().Sha256 && File.ReadAllBytes(second.Files.Single().Source).SequenceEqual(current), "cached scene result differs or was needlessly recopied");
                write(Scene(SwapRepairs.TimelineType, 0xDEADBEEF), 2);
                ReplacePlan changed = ReplacePlanner.Prepare(composition, "", f.Engine, Path.Combine(root, "changed"), cache, null);
                Require(cache.Extracted == extracted + 1 && changed.Files.Single().Fixes.Any(x => x.Warning) && File.ReadAllBytes(changed.Files.Single().Source).SequenceEqual(old), "cached repair survived an unsupported game update");
            }
            Require(File.ReadAllBytes(Safety.Under(mod, ScenePath)).SequenceEqual(old), "source scene changed during preparation");
        }

        private static void Reapply(string root)
        {
            IProcessProbe previous = GameProcesses.Probe; GameProcesses.Probe = new FakeProcessProbe();
            try
            {
                var f = ReplaceChecks.Create(root, false, ReplaceMethods.Pak);
                string pak = Path.Combine(f.Game, "re_chunk_000.pak");
                byte[] old = Scene(SwapRepairs.TimelineType, SwapRepairs.TimelineOld), current = Scene(SwapRepairs.TimelineType, SwapRepairs.TimelineCurrent);
                Action<byte[], uint> write = delegate(byte[] scene, uint fingerprint)
                {
                    File.Delete(pak);
                    PakWriter.WriteFixture(pak, new[] { new KeyValuePair<string, byte[]>(ScenePath, scene), new KeyValuePair<string, byte[]>(ModFixes.ReferenceCmdPath, ReplaceChecks.ReferenceCmd()) }, fingerprint, 0);
                    GamePaks.Forget(pak);
                };
                write(old, 100);
                ReplaceChecks.Put(f.Mod("old-scene"), ScenePath, old);
                ReplaceMod installed = ReplaceChecks.Install(f, "old-scene", "Old scene swap");
                string deployed = Safety.Under(f.Game, ScenePath);
                Require(File.ReadAllBytes(deployed).SequenceEqual(old), "fixture could not retain a scene installed before the update");
                write(current, 101);
                f.Engine.Settings.SwapRepairs = true;
                string cleanPak = ReplaceEngine.Sha(pak);
                Require(f.Engine.Status().NeedsReapply, "fixture game update was not detected");
                string message = f.Engine.Reapply();
                ReplaceState state = f.Engine.LoadState(); ReplaceMod mod = state.Find(installed.Id); ModFile file = mod.Files.Single();
                Require(message.Contains("stored file") && File.ReadAllBytes(deployed).SequenceEqual(current), "Reapply did not deploy the repaired stored scene");
                Require(file.Sha256 == ReplaceEngine.Sha(current) && File.ReadAllBytes(f.Engine.StoreFile(state, mod, file)).SequenceEqual(current), "stored scene/hash was not refreshed");
                f.Engine.Reapply();
                Require(File.ReadAllBytes(deployed).SequenceEqual(current) && !f.Engine.Status().NeedsReapply, "repeated scene Reapply was not stable");
                Require(File.ReadAllBytes(Safety.Under(f.Mod("old-scene"), ScenePath)).SequenceEqual(old), "Reapply changed the source library fixture");
                f.Engine.RestoreAll();
                Require(ReplaceEngine.Sha(pak) == cleanPak && !File.Exists(deployed), "restore after scene repair did not restore the new game baseline");
            }
            finally { GameProcesses.Probe = previous; }
        }

        // The Slotsloader marker follows the setting; nothing is written without a reframework folder.
        private static void Flag(string root)
        {
            string game = Path.Combine(root, "game"), file = SwapRepairFlag.FilePath(game);
            Directory.CreateDirectory(game);
            File.WriteAllText(Path.Combine(game, "StreetFighter6.exe"), "synthetic fixture executable");
            SwapRepairFlag.Sync(game, true);
            Require(!Directory.Exists(Path.Combine(game, "reframework")), "the marker created a reframework folder");
            Directory.CreateDirectory(Path.Combine(game, "reframework"));
            SwapRepairFlag.Sync(game, true); Require(File.Exists(file) && new FileInfo(file).Length == 0, "setting on: no marker for Slotsloader");
            SwapRepairFlag.Sync(game, true); Require(File.Exists(file), "repeated sync lost the marker");
            SwapRepairFlag.Sync(game, false); Require(!File.Exists(file), "setting off: the marker stayed");
        }
    }
}
