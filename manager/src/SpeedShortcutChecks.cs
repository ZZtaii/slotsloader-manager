using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace SlotsInstaller
{
    // 1.0.16: the install/uninstall speed-ups give the results of the code they replaced, also when the data changes.
    internal static class SpeedShortcutChecks
    {
        private static void Require(bool condition, string message) { ReplaceChecks.Require(condition, message); }

        // The 1.0.15 layout hash: one 48-byte row at a time.
        private static string RowByRowLayout(PakIndex p)
        {
            using (var sha = FastHash.Sha256())
            {
                sha.TransformBlock(p.Header, 0, p.Header.Length, null, 0);
                var row = new byte[PakIndex.EntrySize];
                for (int i = 0; i < p.Count; i++) { Buffer.BlockCopy(p.Table, i * PakIndex.EntrySize, row, 0, PakIndex.EntrySize); for (int k = 0; k < 8; k++) row[k] = 0; sha.TransformBlock(row, 0, PakIndex.EntrySize, null, 0); }
                sha.TransformFinalBlock(new byte[0], 0, 0);
                return BitConverter.ToString(sha.Hash).Replace("-", "");
            }
        }

        // PAK lookups (one int per hash, repeated hashes in table order, a new list per call) and the blocked layout hash.
        internal static void PakTable(string root)
        {
            Directory.CreateDirectory(root);
            string pak = Path.Combine(root, "rows.pak");
            const int count = 9000;   // two full 4,096-row blocks and a partial one
            var rows = new List<KeyValuePair<string, byte[]>>();
            for (int i = 0; i < count; i++)
            {
                string key = i == 10 || i == 4500 || i == 8999 ? "#hash:1111111122222222" : i % 1000 == 7 ? "#hash:0000000000000000" : "natives/stm/check/row" + i + ".bin";
                rows.Add(new KeyValuePair<string, byte[]>(key, new[] { (byte)i }));
            }
            PakWriter.WriteFixture(pak, rows, 0x1234, 0);
            PakIndex p = PakIndex.Open(pak);
            List<int> repeated = p.Find(0x1111111122222222UL);
            Require(repeated.SequenceEqual(new[] { 10, 4500, 8999 }), "repeated hash rows: " + String.Join(",", repeated));
            repeated.Clear();
            Require(p.Find(0x1111111122222222UL).Count == 3, "Find returned the index's own list");
            Require(p.Find(PakHash.PathHash("natives/stm/check/row5.bin")).SequenceEqual(new[] { 5 }) && p.Find(PakHash.PathHash("natives/stm/check/missing.bin")).Count == 0, "single and missing hashes");
            Require(p.Contains(PakHash.PathHash("natives/stm/check/row8998.bin")) && !p.Contains(0) && p.CountZeroed() == 9, "Contains / CountZeroed");
            Require(PakRepair.ZeroedRows(p).SequenceEqual(Enumerable.Range(0, count).Where(i => i % 1000 == 7)), "zeroed rows");
            string layout = p.LayoutSha256();
            Require(layout == RowByRowLayout(p), "layout hash differs from the row-by-row hash");
            p.Table[(8998) * PakIndex.EntrySize] ^= 1;   // a hash field: ignored
            Require(p.LayoutSha256() == layout && p.LayoutSha256() == RowByRowLayout(p), "a hash field changed the layout");
            p.Table[(8998) * PakIndex.EntrySize + 8] ^= 1;   // an offset in the last, partial block: counted
            Require(p.LayoutSha256() != layout && p.LayoutSha256() == RowByRowLayout(p), "an offset in the last block was ignored");
            p.Table[(8998) * PakIndex.EntrySize + 8] ^= 1; p.Table[4096 * PakIndex.EntrySize + 47] ^= 1;   // first row of the second block
            Require(p.LayoutSha256() != layout && p.LayoutSha256() == RowByRowLayout(p), "a block boundary row was ignored");
            Require(PakIndex.Open(pak).LayoutSha256() == layout, "a fresh read of the file");
        }

        // Folders checked once per pass: a link anywhere not checked before in the pass is still refused.
        internal static void ReparseFolders(string root)
        {
            string game = Path.Combine(root, "tree"), outside = Path.Combine(root, "outside");
            ReplaceChecks.Put(game, "a/b/one.bin", new byte[] { 1 });
            ReplaceChecks.Put(outside, "two.bin", new byte[] { 2 });
            if (!LibraryDeleteChecks.Junction(Path.Combine(game, "a", "link"), outside)) { Console.WriteLine("junctions unavailable (mklink /J failed): link pass skipped"); return; }
            var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Safety.NoReparseChain(Path.Combine(game, "a", "b", "one.bin"), folders);
            Require(folders.Contains(Path.Combine(game, "a")) && folders.Contains(Path.Combine(game, "a", "b")), "checked folders not remembered");
            for (int i = 0; i < 2; i++) ReplaceChecks.Refused(delegate { Safety.NoReparseChain(Path.Combine(game, "a", "link", "two.bin"), folders); }, "Links/junctions");
            Require(!folders.Contains(Path.Combine(game, "a", "link")), "a refused link was remembered");
            ReplaceChecks.Refused(delegate { Safety.NoReparseChain(Path.Combine(game, "a", "link"), folders); }, "Links/junctions");   // the path itself, always
            Directory.Delete(Path.Combine(game, "a", "link"));
        }

        // Reconcile with the remembered folders: a game folder made a link after install is still refused, after other
        // paths of the same pass checked its parents; nothing changes.
        internal static void ReconcileLink(string root)
        {
            var f = ReplaceChecks.Create(root, false, ReplaceMethods.Pak);
            var clean = ReplaceChecks.Snapshot(f.Game);
            ReplaceMod a = ReplaceChecks.Install(f, "modA", "Colors"); ReplaceMod dlc = ReplaceChecks.Install(f, "modDlc", "DLC");
            string folder = Path.GetDirectoryName(f.Engine.GamePath(ReplaceChecks.D1)), real = Path.Combine(root, "real-004");
            Directory.Move(folder, real);
            if (!LibraryDeleteChecks.Junction(folder, real)) { Directory.Move(real, folder); Console.WriteLine("junctions unavailable: reconcile link pass skipped"); }
            else
            {
                long generation = f.Engine.LoadState().Generation;
                ReplaceChecks.Refused(delegate { f.Engine.SetEnabled(dlc.Id, false); }, "Links/junctions");
                Require(f.Engine.LoadState().Generation == generation && !f.Engine.JournalPending, "a refused change committed");
                Directory.Delete(folder); Directory.Move(real, folder);
            }
            f.Engine.UninstallForGood(dlc.Id, DriftChoice.Refuse, null); f.Engine.UninstallForGood(a.Id, DriftChoice.Refuse, null);
            ReplaceChecks.SameSnapshot(clean, ReplaceChecks.Snapshot(f.Game), "after the link check");
        }

        // The rollback copy of an archive mod's file reuses Reconcile's hash; a file changed after that read is never
        // stored: the copy is checked, the archive gives the stored bytes, and the change is refused as before.
        internal static void HydrateAfterChange(string root)
        {
            var f = ReplaceChecks.Create(root, false, ReplaceMethods.Pak);
            f.Engine.SourceConfig = new AppConfig { GameRoot = f.Game, Libraries = new List<string> { f.Mods } };
            string zip = f.Mod("Hydrate.zip");
            System.IO.Compression.ZipFile.CreateFromDirectory(f.Mod("modB"), zip);
            ReplaceMod mod = ReplaceChecks.Install(f, "Hydrate.zip", "Hydrate");
            ModFile file = mod.Files.Single(x => x.Path == ReplaceChecks.V5);
            string live = f.Engine.GamePath(ReplaceChecks.V5), store = f.Engine.StoreFile(f.Engine.LoadState(), mod, file);
            Require(file.ArchiveEntry != null && !File.Exists(store), "fixture is not archive-backed");
            byte[] original = File.ReadAllBytes(live), foreign = (byte[])original.Clone(); foreign[0] ^= 0x55;
            long generation = f.Engine.LoadState().Generation;
            ReplaceEngine.BeforeArchivePayloads = delegate { File.WriteAllBytes(live, foreign); };
            try { ReplaceChecks.Refused(delegate { f.Engine.UninstallForGood(mod.Id, DriftChoice.Refuse, null); }, "changed outside this operation"); }
            finally { ReplaceEngine.BeforeArchivePayloads = null; }
            Require(File.ReadAllBytes(live).SequenceEqual(foreign), "the changed game file was replaced");
            Require(File.Exists(store) && ReplaceEngine.Sha(store) == file.Sha256, "the stored rollback copy is not the installed version");
            Require(f.Engine.LoadState().Generation == generation && f.Engine.LoadState().Find(mod.Id) != null, "the refused uninstall committed");
            File.WriteAllBytes(live, original);
            if (f.Engine.JournalPending) f.Engine.Recover();
            Require(!f.Engine.JournalPending && ReplaceEngine.Sha(live) == file.Sha256, "recovery after the refused uninstall");
            f.Engine.UninstallForGood(mod.Id, DriftChoice.Refuse, null);
            Require(!File.Exists(live) && f.Engine.LoadState().Mods.Count == 0 && !Directory.Exists(Path.Combine(f.Engine.StoreRoot(f.Engine.LoadState()), mod.Id)), "uninstall after the recovery");
        }

        // The base PAK stamps written after a change come from one fresh read.
        internal static void Stamps(string root)
        {
            var f = ReplaceChecks.Create(root, false, ReplaceMethods.Pak);
            string basePath = Path.Combine(f.Game, "re_chunk_000.pak");
            GamePaks.Cached(basePath);   // a cached copy from before the change
            ReplaceMod a = ReplaceChecks.Install(f, "modA", "Colors");
            ReplaceState s = f.Engine.LoadState(); PakIndex now = PakIndex.Open(basePath);
            PakStamp held = s.Paks.Single(x => x.Pak == "re_chunk_000.pak");
            foreach (PakStamp stamp in new[] { held, s.BaseAtInstall })
                Require(stamp.Length == now.Length && stamp.Fingerprint == now.Fingerprint && stamp.LayoutSha == now.LayoutSha256() && stamp.TocSha == now.TocSha256(), "stamp differs from the PAK on disk: " + stamp.Pak);
            Require(!String.IsNullOrEmpty(held.Backup) && String.IsNullOrEmpty(s.BaseAtInstall.Backup), "stamp backups");
            GameFilesStatus status = f.Engine.Status();
            Require(!status.PakChanged && !status.BaseChanged && status.RowsNotZero == 0, "status after install: " + String.Join("; ", status.Details));
            f.Engine.UninstallForGood(a.Id, DriftChoice.Refuse, null);
        }
    }
}
