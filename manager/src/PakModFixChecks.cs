using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace SlotsInstaller
{
    // 25 Nov 2026: PakModFixes.cs on generated files and a generated game folder. Nothing here touches the real game.
    internal static class PakModFixChecks
    {
        private static void Require(bool condition, string message) { ReplaceChecks.Require(condition, message); }
        private static readonly PakModFixes.Migration Setter = PakModFixes.Migrations[0];
        private const uint OtherType = 0x12345678, OtherCrc = 0x9ABCDEF0;
        internal const string LevelPath = "natives/stm/product/level/scene/ess/ess0000_00/ess0000_00_level.scn.20";

        // A minimal scene / user / prefab file: magic, RSZ pointer, RSZ header, one object, the instance table, a data block.
        internal static byte[] Rsz(string magic, params uint[] typeCrc)
        {
            int pointer = magic == "SCN" ? 56 : magic == "USR" ? 32 : 48;
            int ic = typeCrc.Length / 2 + 1; long R = 64, inst = 52, data = (inst + 8 * ic + 15) / 16 * 16;
            var b = new byte[R + data + 16];
            Encoding.ASCII.GetBytes(magic).CopyTo(b, 0);
            BitConverter.GetBytes((ulong)R).CopyTo(b, pointer);
            Encoding.ASCII.GetBytes("RSZ").CopyTo(b, R);
            BitConverter.GetBytes(16u).CopyTo(b, R + 4); BitConverter.GetBytes(1u).CopyTo(b, R + 8); BitConverter.GetBytes((uint)ic).CopyTo(b, R + 12);
            BitConverter.GetBytes((ulong)inst).CopyTo(b, R + 24); BitConverter.GetBytes((ulong)data).CopyTo(b, R + 32);
            BitConverter.GetBytes(1u).CopyTo(b, R + 48);
            for (int i = 0; i < typeCrc.Length; i += 2) { BitConverter.GetBytes(typeCrc[i]).CopyTo(b, R + inst + 8 + 4 * i); BitConverter.GetBytes(typeCrc[i + 1]).CopyTo(b, R + inst + 12 + 4 * i); }
            for (int i = 0; i < 16; i++) b[R + data + i] = (byte)(0xA0 + i);
            return b;
        }
        private static byte[] OldScene() { return Rsz("SCN", OtherType, OtherCrc, Setter.Type, Setter.Old, Setter.Type, Setter.Old); }
        private static uint Crc(byte[] b, int instance) { long t; uint c; PakModFixes.InstanceTable(b, out t, out c); return BitConverter.ToUInt32(b, (int)(t + 8L * instance + 4)); }

        private static GameReference Reference(uint gameCrc)
        {
            byte[] proof = Rsz("SCN", Setter.Type, gameCrc);
            var r = new GameReference();
            r.GameFile = delegate(string path) { return path == Setter.Proof[1] ? proof : null; };   // the first proof is missing: the next one counts
            return r;
        }

        // A PAK with: the old scene stored, the old scene deflated, an old user file deflated with a zlib header,
        // other data deflated, and a tiny stored entry.
        private static byte[] Pak(out byte[][] contents, out long[] attributes)
        {
            byte[] scene = OldScene(), user = Rsz("USR", Setter.Type, Setter.Old), other = Encoding.ASCII.GetBytes(new string('x', 5000)), tiny = Encoding.ASCII.GetBytes("tiny");
            byte[] zlibUser = new byte[] { 0x78, 0x9C }.Concat(PakModFixes.Deflate(user)).ToArray();
            contents = new[] { scene, scene, user, other, tiny };
            var raw = new[] { scene, PakModFixes.Deflate(scene), zlibUser, PakModFixes.Deflate(other), tiny };
            attributes = new long[] { 0, 1, 1, 1 | 0x100, 0 };
            int n = raw.Length; long offset = PakIndex.HeaderSize + (long)n * PakIndex.EntrySize;
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(new byte[] { (byte)'K', (byte)'P', (byte)'K', (byte)'A', 4, 0, 0, 0 }); w.Write((uint)n); w.Write(0u);
                for (int i = 0; i < n; i++) { w.Write((uint)(0x1000 + i)); w.Write((uint)(0x2000 + i)); w.Write(offset); w.Write((long)raw[i].Length); w.Write((long)contents[i].Length); w.Write(attributes[i]); w.Write(0L); offset += raw[i].Length; }
                foreach (byte[] r in raw) w.Write(r);
                w.Flush(); return ms.ToArray();
            }
        }
        private static byte[] Entry(byte[] pak, int i, out long attributes)
        {
            int row = PakIndex.HeaderSize + i * PakIndex.EntrySize;
            long offset = BitConverter.ToInt64(pak, row + 8), size = BitConverter.ToInt64(pak, row + 16), full = BitConverter.ToInt64(pak, row + 24);
            attributes = BitConverter.ToInt64(pak, row + 32);
            var raw = new byte[size]; Buffer.BlockCopy(pak, (int)offset, raw, 0, (int)size);
            return PakIndex.Decompress(raw, attributes, full);
        }

        internal static void Run(string root)
        {
            Directory.CreateDirectory(root);
            GameReference current = Reference(Setter.Current);

            // Loose scene: only the old signatures change, the other type and every other byte stay.
            byte[] scene = OldScene(), before = (byte[])scene.Clone();
            List<FixResult> fixes = ModFixes.UpgradeModFile(ref scene, current, LevelPath);
            FixResult sig = fixes.SingleOrDefault(x => x.Kind == "signature");
            Require(sig != null && sig.Changed && sig.Count == 2 && sig.Message.Contains(Setter.Name), "loose scene: " + String.Join(" | ", fixes.Select(x => x.Kind + ": " + x.Message)));
            Require(Crc(scene, 1) == OtherCrc && Crc(scene, 2) == Setter.Current && Crc(scene, 3) == Setter.Current, "loose scene signatures");
            Require(Enumerable.Range(0, before.Length).Count(i => before[i] != scene[i]) <= 8 && scene.Length == before.Length, "loose scene: more than the signatures changed");
            // Again: nothing to do.
            byte[] again = (byte[])scene.Clone();
            Require(!ModFixes.UpgradeModFile(ref again, current, LevelPath).Any(x => x.Kind == "signature") && again.SequenceEqual(scene), "loose scene: a second pass changed it");

            // Loose prefab and user file.
            byte[] pfb = Rsz("PFB", Setter.Type, Setter.Old), usr = Rsz("USR", Setter.Type, Setter.Old);
            Require(ModFixes.UpgradeModFile(ref pfb, current, "natives/stm/product/om/test/x.pfb.17").Any(x => x.Kind == "signature") && Crc(pfb, 1) == Setter.Current, "loose prefab");
            Require(ModFixes.UpgradeModFile(ref usr, current, "natives/stm/product/om/test/x.user.2").Any(x => x.Kind == "signature") && Crc(usr, 1) == Setter.Current, "loose user file");

            // The game doesn't confirm the current signature (another one, or no game file): left as it is, with a warning.
            foreach (GameReference unsure in new[] { Reference(0xDEADBEEF), new GameReference(), null })
            {
                byte[] s = OldScene(), copy = (byte[])s.Clone();
                List<FixResult> r = PakModFixes.UpgradeFile(ref s, unsure);
                Require(s.SequenceEqual(copy) && r.Count == 1 && r[0].Warning && r[0].Kind == "stale" && !r[0].Changed, "unconfirmed signature: " + String.Join(" | ", r.Select(x => x.Message)));
            }
            // Not a scene, or a scene without old signatures: nothing.
            byte[] plain = Encoding.ASCII.GetBytes("SCN\0 not really a scene, too short?").Concat(new byte[64]).ToArray(), plainCopy = (byte[])plain.Clone();
            Require(PakModFixes.UpgradeFile(ref plain, current).Count == 0 && plain.SequenceEqual(plainCopy), "a broken scene was touched");

            // PAK: scene-like entries fixed (stored, deflated, zlib-wrapped), other entries and every original byte kept.
            byte[][] contents; long[] attributes;
            byte[] pak = Pak(out contents, out attributes), original = (byte[])pak.Clone();
            List<FixResult> pakFixes = ModFixes.UpgradeModFile(ref pak, current, "re_chunk_000.pak.patch_004.pak");
            FixResult pakSig = pakFixes.SingleOrDefault(x => x.Kind == "signature");
            Require(pakSig != null && pakSig.Count == 5 && pakSig.Message.Contains("in 3 files inside the PAK"), "PAK fixes: " + String.Join(" | ", pakFixes.Select(x => x.Message)));
            Require(pak.Length > original.Length, "PAK: nothing appended");
            int tableEnd = PakIndex.HeaderSize + 5 * PakIndex.EntrySize;
            Require(Enumerable.Range(0, original.Length).Where(i => pak[i] != original[i]).All(i => i >= PakIndex.HeaderSize && i < tableEnd), "PAK: bytes outside the entry table changed");
            for (int i = 0; i < 5; i++)
            {
                long attr; byte[] e = Entry(pak, i, out attr);
                int row = PakIndex.HeaderSize + i * PakIndex.EntrySize;
                Require(BitConverter.ToUInt64(pak, row) == BitConverter.ToUInt64(original, row), "PAK: entry " + i + " hash changed");
                if (i < 3)
                {
                    Require(Crc(e, i < 2 ? 2 : 1) == Setter.Current && (i == 2 || Crc(e, 1) == OtherCrc) && (attr & 0xF) == 1, "PAK: entry " + i + " not fixed");
                    byte[] expected = (byte[])contents[i].Clone(); PakModFixes.UpgradeFile(ref expected, current);
                    Require(e.SequenceEqual(expected), "PAK: entry " + i + " differs beyond its signatures");
                }
                else Require(e.SequenceEqual(contents[i]) && attr == attributes[i] && BitConverter.ToInt64(pak, row + 8) == BitConverter.ToInt64(original, row + 8), "PAK: entry " + i + " changed");
            }
            string pakFile = Path.Combine(root, "fixed.pak"); File.WriteAllBytes(pakFile, pak);
            PakIndex opened = PakIndex.Open(pakFile);
            Require(opened.Count == 5 && Crc(opened.Read(opened.Entry(1)), 2) == Setter.Current, "the fixed PAK does not open with PakIndex");
            byte[] pakAgain = (byte[])pak.Clone();
            Require(PakModFixes.UpgradePak(ref pakAgain, current).Count == 0 && pakAgain.SequenceEqual(pak), "PAK: a second pass changed it");
            // Unconfirmed: unchanged with a warning; encrypted or another version: unchanged, silent.
            byte[] unsurePak = (byte[])original.Clone();
            List<FixResult> unsureFixes = PakModFixes.UpgradePak(ref unsurePak, Reference(0xDEADBEEF));
            Require(unsurePak.SequenceEqual(original) && unsureFixes.Count == 1 && unsureFixes[0].Kind == "stale", "PAK unconfirmed: " + String.Join(" | ", unsureFixes.Select(x => x.Message)));
            foreach (int at in new[] { 6, 4 })
            {
                byte[] odd = (byte[])original.Clone(); odd[at] = (byte)(at == 6 ? 8 : 2); byte[] oddCopy = (byte[])odd.Clone();
                Require(PakModFixes.UpgradePak(ref odd, current).Count == 0 && odd.SequenceEqual(oddCopy), "an encrypted or other-version PAK was touched");
            }
            // A table row outside the file: the PAK is left alone.
            byte[] broken = (byte[])original.Clone(); BitConverter.GetBytes((long)broken.Length + 10).CopyTo(broken, PakIndex.HeaderSize + 8);
            byte[] brokenCopy = (byte[])broken.Clone();
            Require(PakModFixes.UpgradePak(ref broken, current).Count == 0 && broken.SequenceEqual(brokenCopy), "a PAK whose table doesn't parse was touched");

            // A folder (Extra stage's staging): its PAK and loose scene fixed; a .pak inside natives is not a PAK mod.
            string folder = Path.Combine(root, "stage-folder");
            string optionPak = Path.Combine(folder, "Option", "Stage.pak"), loose = Path.Combine(folder, "Option", LevelPath.Replace('/', '\\')), nativePak = Path.Combine(folder, "Option", "natives", "stm", "odd.pak");
            foreach (string d in new[] { optionPak, loose, nativePak }) Directory.CreateDirectory(Path.GetDirectoryName(d));
            File.WriteAllBytes(optionPak, original); File.WriteAllBytes(loose, OldScene()); File.WriteAllBytes(nativePak, original);
            List<FixResult> folderFixes = PakModFixes.FixFolder(folder, current);
            Require(folderFixes.Count(x => x.Kind == "signature") == 2 && !File.ReadAllBytes(optionPak).SequenceEqual(original) && Crc(File.ReadAllBytes(loose), 2) == Setter.Current && File.ReadAllBytes(nativePak).SequenceEqual(original), "folder fixes: " + String.Join(" | ", folderFixes.Select(x => x.Message)));
        }

        // A stage PAK mod installed with Replace original on a generated game whose base PAK holds the proof scene:
        // the patch PAK in the game is the fixed one, the plan says so, and the library copy is untouched.
        // The fixture game's base PAK rebuilt with one more file (the game file that proves the current signature).
        private static void AddProofToBase(ReplaceChecks.Fixture f)
        {
            string basePath = Path.Combine(f.Game, "re_chunk_000.pak");
            PakIndex old = PakIndex.Open(basePath);
            var files = new List<KeyValuePair<string, byte[]>>();
            for (int i = 0; i < old.Count; i++) { PakEntry e = old.Entry(i); files.Add(new KeyValuePair<string, byte[]>("#hash:" + (((ulong)e.HashUpper << 32) | e.HashLower).ToString("X16"), old.Read(e))); }
            files.Add(new KeyValuePair<string, byte[]>(Setter.Proof[0], Rsz("SCN", Setter.Type, Setter.Current)));
            uint fingerprint = old.Fingerprint;
            File.Delete(basePath); PakWriter.WriteFixture(basePath, files, fingerprint, 0);
        }
        private static string OldStageMod(ReplaceChecks.Fixture f)
        {
            string modDir = Path.Combine(f.Mods, "aoki", "Aoki"); Directory.CreateDirectory(modDir);
            File.WriteAllText(Path.Combine(modDir, "modinfo.ini"), "name=Old stage\r\n", new UTF8Encoding(false));
            string modPak = Path.Combine(modDir, "Aoki.pak");
            PakWriter.WriteFixture(modPak, new List<KeyValuePair<string, byte[]>> { new KeyValuePair<string, byte[]>(LevelPath, OldScene()), new KeyValuePair<string, byte[]>("natives/stm/product/other.bin", Encoding.ASCII.GetBytes("other data")) }, 0, 0);
            return modPak;
        }
        private static byte[] InstalledLevel(ReplaceChecks.Fixture f, ReplaceMod mod, out string other)
        {
            string installed = Path.Combine(f.Game, mod.Files.Select(x => x.Path).Single(ReplaceEngine.IsPatchPakPath));
            PakIndex p = PakIndex.Open(installed);
            byte[] o = p.ReadByPath("natives/stm/product/other.bin"); other = o == null ? null : Encoding.ASCII.GetString(o);
            return p.ReadByPath(LevelPath);
        }

        internal static void Engine(string root)
        {
            ReplaceChecks.Fixture f = ReplaceChecks.Create(root, false, ReplaceMethods.Pak);
            AddProofToBase(f);
            string modPak = OldStageMod(f);
            byte[] library = File.ReadAllBytes(modPak);
            ReplacePlan plan;
            ReplaceMod mod = ReplaceChecks.Install(f, "aoki", "Old stage", false, null, out plan);
            string other; byte[] level = InstalledLevel(f, mod, out other);
            Require(level != null && Crc(level, 2) == Setter.Current && Crc(level, 3) == Setter.Current && other == "other data", "installed patch PAK is not the fixed one");
            Require(plan.Files.Any(x => x.Fixes.Any(y => y.Kind == "signature" && y.Changed)), "the plan does not report the signature fix: " + String.Join(" | ", plan.Files.SelectMany(x => x.Fixes).Select(x => x.Message)));
            Require(File.ReadAllBytes(modPak).SequenceEqual(library), "the library PAK was changed");
        }

        // A stage PAK mod installed while the game could not confirm the current signature goes in as it is (warned);
        // after a game update that can, Reapply fixes the stored copy and the game's patch PAK. The library copy stays.
        internal static void Refix(string root)
        {
            ReplaceChecks.Fixture f = ReplaceChecks.Create(root, false, ReplaceMethods.Pak);
            string modPak = OldStageMod(f);
            byte[] library = File.ReadAllBytes(modPak);
            ReplacePlan plan;
            ReplaceMod mod = ReplaceChecks.Install(f, "aoki", "Old stage", false, null, out plan);
            string other; byte[] level = InstalledLevel(f, mod, out other);
            Require(level != null && Crc(level, 2) == Setter.Old && plan.Files.Any(x => x.Fixes.Any(y => y.Kind == "stale" && y.Warning)), "before the update: " + String.Join(" | ", plan.Files.SelectMany(x => x.Fixes).Select(x => x.Message)));
            AddProofToBase(f);
            string result = f.Engine.Reapply();
            ReplaceMod after = f.Engine.LoadState().Find(mod.Id);
            level = InstalledLevel(f, after, out other);
            Require(level != null && Crc(level, 2) == Setter.Current && Crc(level, 3) == Setter.Current && other == "other data", "Reapply did not fix the installed patch PAK: " + result);
            Require(result.Contains("updated for the current game version"), "Reapply result: " + result);
            Require(File.ReadAllBytes(modPak).SequenceEqual(library), "the library PAK was changed");
            string again = f.Engine.Reapply();
            Require(!again.Contains("updated for the current game version"), "a second Reapply changed it again: " + again);
        }
    }
}
