using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace SlotsInstaller
{
    // 2.4.2 engine checks (part of --replace-check): Fluffy PAK installs uninstalled by the Manager and game entries
    // left invalidated by another tool, on generated games whose base PAK is sorted by hash like SF6's (the live
    // evidence of 2026-10-14: rows 11886 and 244293 of the A.K.I outfit stayed zeroed after Fluffy's own uninstall).
    internal static class FluffyPakChecks
    {
        internal const string AkiMesh = "natives/stm/product/model/esf/esf013/005/01/esf013_005_01.mesh.230110883";
        internal const string AkiMdf = "natives/stm/product/model/esf/esf013/005/01/esf013_005_01_v00.mdf2.31";
        internal const string ChunMesh = "natives/stm/product/model/esf/esf004/001/01/esf004_001_01.mesh.230110883";
        internal const ulong AkiMeshHash = 0x0A8B7E5CC61699F9, AkiMdfHash = 0xDA2CCD0CC5341D1E, ChunMeshHash = 0x7DCC0FAF21907D3D;
        // The three new textures "1. Aki Default" shipped (Fluffy's Log.txt).
        internal static readonly string[] AkiTextures = {
            "natives/stm/product/model/esf/esf013/005/01/esf013_000_01_Body_1_CMASK.tex.241101895",
            "natives/stm/product/model/esf/esf013/005/01/esf013_000_01_Body_ALBD.tex.241101895",
            "natives/stm/product/model/esf/esf013/005/01/esf013_000_01_Body_NRRC.tex.241101895" };
        internal const string TsvRelative = @"reframework\data\SF6_Costumes_Data\loader\vanilla_costume_index.tsv";

        private static void Require(bool condition, string message) { ReplaceChecks.Require(condition, message); }
        private static byte[] B(string text) { return Encoding.UTF8.GetBytes(text); }
        internal static byte[] Mesh(string tag) { return B("MESH\0\0\0\0" + tag); }
        internal static byte[] Mdf(string tag) { return B("MDF\0\0\0\0\0" + tag); }
        private static string G(ReplaceChecks.Fixture f, string rel) { return Path.Combine(f.Game, rel.Replace('/', '\\')); }
        internal static string BasePak(ReplaceChecks.Fixture f) { return Path.Combine(f.Game, "re_chunk_000.pak"); }

        // ---- Fixtures
        // A Replace fixture game whose base PAK holds the fixture's files plus `extra`, sorted ascending by the u64 hash
        // with `fillers` rows of other (unknown) hashes spread over the range, as in SF6's re_chunk_000.pak.
        internal static ReplaceChecks.Fixture SortedGame(string root, Dictionary<string, byte[]> extra, int fillers)
        {
            ReplaceChecks.Fixture f = ReplaceChecks.Create(root, false, ReplaceMethods.Pak);
            string pak = BasePak(f);
            PakIndex old = PakIndex.Open(pak);
            var entries = new Dictionary<ulong, byte[]>();
            foreach (string p in new[] { ReplaceChecks.V1, ReplaceChecks.V2, ReplaceChecks.V3, ModFixes.ReferenceCmdPath, ReplaceChecks.V5, ReplaceChecks.V6 }) entries[PakHash.PathHash(p)] = old.ReadByPath(p);
            foreach (var kv in extra) entries[PakHash.PathHash(kv.Key)] = kv.Value;
            ulong step = UInt64.MaxValue / (ulong)(fillers + 2);
            for (int i = 0; i < fillers; i++) { ulong h = step * (ulong)(i + 1) + 0x1234567UL; if (!entries.ContainsKey(h)) entries[h] = new[] { (byte)i }; }
            WriteSorted(pak, entries);
            return f;
        }
        internal static void WriteSorted(string pak, Dictionary<ulong, byte[]> entries)
        {
            if (File.Exists(pak)) File.Delete(pak);
            GamePaks.Forget(pak);
            PakWriter.WriteFixture(pak, entries.OrderBy(e => e.Key).Select(e => new KeyValuePair<string, byte[]>("#hash:" + e.Key.ToString("X16"), e.Value)).ToList(), 0x28376913, 0);
        }
        // Fluffy's invalidation: the row's first 8 bytes zeroed (offset, sizes, flags, checksum untouched).
        internal static int Zero(string pak, ulong hash)
        {
            PakIndex p = PakIndex.Open(pak, false);
            List<int> rows = p.Find(hash);
            Require(rows.Count == 1, "fixture: " + hash.ToString("X16") + " is in " + rows.Count + " rows");
            PakWriter.WriteHashes(pak, p.Length, p.Header, new List<PakHashWrite> { new PakHashWrite { Index = rows[0], Expected = BitConverter.GetBytes(hash), Value = new byte[8] } });
            GamePaks.Forget(pak);
            return rows[0];
        }
        internal static int Zero(ReplaceChecks.Fixture f, string path) { return Zero(BasePak(f), PakHash.PathHash(path)); }
        internal static ulong RowHash(string pak, int index) { PakIndex p = PakIndex.Open(pak, false); return BitConverter.ToUInt64(p.Table, index * PakIndex.EntrySize); }
        // SlotsLoader's vanilla index (#FILES: hash, character, costume, kind, path), the game's own known paths.
        internal static void WriteTsv(string game, IEnumerable<string> paths)
        {
            var sb = new StringBuilder("#SUFFIXES\nmesh\t230110883\n#COSTUMES\n#FILES\n");
            foreach (string p in paths) sb.Append(PakHash.PathHash(p).ToString("X16") + "\t13\t4\tx\t" + p + "\n");
            ReplaceChecks.Put(game, TsvRelative, B(sb.ToString()));
        }
        // Fluffy's ArchiveList.bin layout (as read from the user's file): header, names, per archive u32 0, u32 count,
        // count x {u64 0, u64 row offset, u64 hash}.
        internal static void WriteArchiveList(string file, List<KeyValuePair<string, ulong[]>> archives)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            using (var w = new BinaryWriter(File.Create(file)))
            {
                byte[] names = Encoding.ASCII.GetBytes(String.Join("\0", archives.Select(a => a.Key)) + "\0");
                w.Write(Encoding.ASCII.GetBytes("ARCH")); w.Write(7); w.Write(archives.Count); w.Write(names.Length); w.Write(2180400); w.Write(2);
                w.Write(names);
                while (w.BaseStream.Position < 164) w.Write((byte)0);
                foreach (var a in archives)
                {
                    w.Write(0); w.Write((uint)a.Value.Length);
                    for (int i = 0; i < a.Value.Length; i++) { w.Write(0UL); w.Write((ulong)(PakIndex.HeaderSize + (long)PakIndex.EntrySize * i)); w.Write(a.Value[i]); }
                }
            }
        }
        internal static ulong[] Hashes(string pak) { PakIndex p = PakIndex.Open(pak, false); var h = new ulong[p.Count]; for (int i = 0; i < p.Count; i++) h[i] = BitConverter.ToUInt64(p.Table, i * PakIndex.EntrySize); return h; }
        // A new path (not in the PAK) whose hash lies strictly between the neighbours of `index`.
        internal static string PathInGap(string pak, int index, string stem)
        {
            ulong[] h = Hashes(pak);
            int a = index - 1; while (a >= 0 && h[a] == 0) a--;
            int c = index + 1; while (c < h.Length && h[c] == 0) c++;
            ulong low = a < 0 ? 0 : h[a], high = c >= h.Length ? UInt64.MaxValue : h[c];
            for (int i = 0; i < 200000; i++)
            {
                string p = "natives/stm/product/model/esf/esf013/005/01/" + stem + "_" + i + ".tex.241101895";
                ulong x = PakHash.PathHash(p);
                if (x > low && x < high) return p;
            }
            throw new InvalidOperationException("no path found in the gap");
        }
        // Snapshots equal, ignoring the natives and natives/stm folders Fluffy and the Manager leave in place.
        internal static void Same(Dictionary<string, string> a, Dictionary<string, string> b, string context)
        {
            Func<Dictionary<string, string>, Dictionary<string, string>> strip = delegate(Dictionary<string, string> d)
            { return d.Where(kv => !(kv.Value == "dir" && (kv.Key.Equals("natives\\", StringComparison.OrdinalIgnoreCase) || kv.Key.Equals("natives\\stm\\", StringComparison.OrdinalIgnoreCase)))).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase); };
            ReplaceChecks.SameSnapshot(strip(a), strip(b), context);
        }
        private static FluffyRemovedRecord Record(string folder) { return Json.Read<FluffyRemovedRecord>(Path.Combine(folder, "record.json")); }

        // ---- The live evidence: rows 11886 and 244293 left zeroed (the A.K.I outfit's mesh and material)
        internal static void OrphanedEvidence(string root)
        {
            Require(PakHash.PathHash(AkiMesh) == AkiMeshHash && PakHash.PathHash(AkiMdf) == AkiMdfHash && PakHash.PathHash(ChunMesh) == ChunMeshHash, "the real path hashes (paktool hash) changed");
            ReplaceChecks.Fixture f = ReplaceChecks.Create(Path.Combine(root, "game"), false, ReplaceMethods.Pak);
            // 286 816 rows sorted by hash with the three real paths at their real rows (11886, 140505, 244293).
            var entries = new Dictionary<ulong, byte[]>();
            entries[AkiMeshHash] = Mesh("vanilla A.K.I outfit 5"); entries[ChunMeshHash] = Mesh("vanilla Chun-Li outfit 1"); entries[AkiMdfHash] = Mdf("vanilla A.K.I outfit 5");
            Action<ulong, ulong, int> fill = delegate(ulong low, ulong high, int count)
            {
                ulong step = (high - low) / (ulong)(count + 1);
                for (int i = 1; i <= count; i++) entries[low + step * (ulong)i] = new[] { (byte)(i & 0xFF) };
            };
            fill(0, AkiMeshHash, 11886); fill(AkiMeshHash, ChunMeshHash, 140505 - 11886 - 1); fill(ChunMeshHash, AkiMdfHash, 244293 - 140505 - 1); fill(AkiMdfHash, UInt64.MaxValue, 286816 - 244293 - 1);
            Require(entries.Count == 286816, "fixture rows: " + entries.Count);
            string pak = BasePak(f);
            WriteSorted(pak, entries);
            PakIndex clean = PakIndex.Open(pak);
            Require(clean.Count == 286816 && clean.Entry(11886).Hash == AkiMeshHash && clean.Entry(140505).Hash == ChunMeshHash && clean.Entry(244293).Hash == AkiMdfHash && PakRepair.IsSorted(clean), "the rows are not where the evidence has them");
            string cleanSha = ReplaceEngine.Sha(pak);
            string toc = f.Engine.EnsureTocBackup(clean);   // the Manager's TOC backup (kept aside until round 2)
            string backups = f.Engine.PakBackupDir; Directory.Move(backups, backups + ".off");
            // Fluffy's uninstall of "1. Aki Default" left both rows zeroed; no installed.ini owns them.
            Require(Zero(f, AkiMesh) == 11886 && Zero(f, AkiMdf) == 244293, "zeroed rows");
            GameFilesStatus status = f.Engine.Status();
            Require(status.OrphanedRows == 2 && status.OrphanedRowsWhere.SequenceEqual(new[] { "re_chunk_000.pak #11886", "re_chunk_000.pak #244293" }) && status.Summary == "2 game entries left invalidated by another tool.", "status: " + status.OrphanedRows + " / " + status.Summary);
            // Nothing proves them yet (no backup, no known path): Repair says so and changes nothing.
            PakRepairResult none = f.Engine.RepairOrphanedRows(false);
            Require(none.Repaired.Count == 0 && none.Unproven.Count == 2 && none.Unproven.All(r => r.Problem == "no known path fits between its neighbours") && none.Message.StartsWith("Couldn't repair 2 game entries: nothing proves the original values."), "unproven: " + none.Message);
            ReplaceChecks.Refused(delegate { f.Engine.RepairOrphanedRows(true); }, "Verify SF6's files in Steam");
            // Round 1: the game's own known paths (SlotsLoader's vanilla index) prove both by hash(path), through the CLI.
            WriteTsv(f.Game, new[] { AkiMesh, AkiMdf, ChunMesh });
            ReplaceSettings saved = ReplaceSettings.Load();
            // The CLI uses the shared Data folder: another check's record (for its own game) is set aside meanwhile.
            string sharedState = Path.Combine(AppConfig.DataDir, "replace-state.json"), keptState = sharedState + ".fluffypak-" + Guid.NewGuid().ToString("N").Substring(0, 6);
            bool setAside = File.Exists(sharedState);
            if (setAside) File.Move(sharedState, keptState);
            try
            {
                var settings = ReplaceSettings.Load(); settings.FluffyRoots = new List<string>(); settings.Method = ""; settings.Save();
                string rs = ReplaceChecks.Cli("replace-status", "--game", f.Game);
                Require(rs.StartsWith("0\n") && rs.Contains("2 game entries left invalidated by another tool: re_chunk_000.pak #11886, re_chunk_000.pak #244293 (repair: pak-repair, or the Installed page's Repair).") && rs.Contains("Status: 2 game entries left invalidated by another tool.") && !rs.Contains("Status: OK"), "replace-status: " + rs);
                string dry = ReplaceChecks.Cli("pak-repair", "--game", f.Game);
                Require(dry.StartsWith("0\n") && dry.Contains("re_chunk_000.pak #11886 → 0A8B7E5CC61699F9 " + AkiMesh + " · proved by hash(path): the only known path between the neighbouring entries") && dry.Contains("re_chunk_000.pak #244293 → DA2CCD0CC5341D1E " + AkiMdf) && dry.Contains("(path from Slotsloader's vanilla index)") && dry.Contains("Dry run: nothing was changed. Run again with --yes to restore 2 proven entries.") && dry.Contains("entry table sorted by hash"), "pak-repair dry run: " + dry);
                Require(RowHash(pak, 11886) == 0 && RowHash(pak, 244293) == 0, "the dry run changed the PAK");
                string yes = ReplaceChecks.Cli("pak-repair", "--game", f.Game, "--yes");
                Require(yes.StartsWith("0\n") && yes.Contains("Repaired 2 game entries."), "pak-repair --yes: " + yes);
                Require(ReplaceEngine.Sha(pak) == cleanSha, "the repaired PAK is not byte-identical to the clean one");
                string after = ReplaceChecks.Cli("replace-status", "--game", f.Game);
                Require(after.Contains("Status: OK") && !after.Contains("left invalidated"), "replace-status after the repair: " + after);
                string again = ReplaceChecks.Cli("pak-repair", "--game", f.Game, "--yes");
                Require(again.StartsWith("0\n") && again.Contains("No game entries need repair."), "pak-repair with nothing to do: " + again);
            }
            finally
            {
                saved.Save();
                if (setAside) { if (File.Exists(sharedState)) File.Delete(sharedState); File.Move(keptState, sharedState); }
            }
            // Round 2: the Manager's TOC backup proves them (no known path needed).
            File.Delete(G(f, TsvRelative.Replace('\\', '/')));
            Directory.Move(backups + ".off", backups);
            Zero(f, AkiMesh); Zero(f, AkiMdf);
            PakRepairResult dryRun = f.Engine.RepairOrphanedRows(false);
            Require(dryRun.Repaired.Count == 2 && dryRun.Repaired.All(r => r.How == "the TOC backup") && dryRun.Evidence.Any(e => e.Contains("TOC backup matches")), "TOC proof: " + String.Join(" | ", dryRun.Repaired.Select(r => r.How)) + " / " + String.Join(" | ", dryRun.Evidence));
            PakRepairResult applied = f.Engine.RepairOrphanedRows(true);
            Require(applied.Message == "Repaired 2 game entries." && ReplaceEngine.Sha(pak) == cleanSha && File.Exists(toc), "TOC repair: " + applied.Message);
            Require(Directory.GetFiles(f.Engine.PakRepairDir, "repair-*.tsv").Length == 1, "the repair was not recorded in Data\\pak-repairs");
            // Round 3: Fluffy's own log names the paths ("Deleted file: <game>\natives/..."), the user's Log.txt lines.
            Directory.Move(backups, backups + ".off");
            Zero(f, AkiMesh); Zero(f, AkiMdf);
            string fluffyRoot = Path.Combine(root, "Fluffy", "Games", "SF6");
            Directory.CreateDirectory(Path.Combine(fluffyRoot, "Backup"));
            ReplaceChecks.Put(Path.Combine(root, "Fluffy"), "Data/Log.txt", B("Deleted file: " + f.Game + "\\re_chunk_000.pak.patch_006.pak\r\n" +
                String.Join("", AkiTextures.Concat(new[] { AkiMesh, AkiMdf }).Select(p => "Deleted file: " + f.Game + "\\" + p + "\r\n")) + "Deleted 6 files.\r\nUninstalled mod: 1. Aki Default\r\n"));
            f.Engine.FluffyProvider = delegate { return FluffyInstalls.Load(new[] { fluffyRoot }); };
            PakRepairResult byLog = f.Engine.RepairOrphanedRows(true);
            Require(byLog.Repaired.Count == 2 && byLog.Repaired.All(r => r.How == "hash(path) between its neighbours" && r.PathSource == "the mod manager's log") && ReplaceEngine.Sha(pak) == cleanSha, "Fluffy's log as the path source: " + byLog.Message + " / " + String.Join(" | ", byLog.Repaired.Select(r => r.PathSource)));
        }

        // ---- Each proof source and each refusal on a small sorted table
        internal static void ProofSources(string root)
        {
            var extra = new Dictionary<string, byte[]> { { AkiMesh, Mesh("aki") }, { AkiMdf, Mdf("aki") }, { ChunMesh, Mesh("chun") } };
            ReplaceChecks.Fixture f = SortedGame(Path.Combine(root, "game"), extra, 40);
            string pak = BasePak(f), cleanSha = ReplaceEngine.Sha(pak);
            ulong[] cleanHashes = Hashes(pak);
            string fluffyRoot = Path.Combine(root, "Fluffy", "Games", "SF6"); Directory.CreateDirectory(Path.Combine(fluffyRoot, "Backup"));
            f.Engine.FluffyProvider = delegate { return FluffyInstalls.Load(new[] { fluffyRoot }); };
            // Fluffy's archive list proves a row no path is known for (a filler row).
            int filler = Enumerable.Range(0, cleanHashes.Length).First(i => cleanHashes[i] != AkiMeshHash && cleanHashes[i] != AkiMdfHash && cleanHashes[i] != ChunMeshHash && i > 3);
            string list = Path.Combine(fluffyRoot, "ArchiveList.bin");
            WriteArchiveList(list, new List<KeyValuePair<string, ulong[]>> { new KeyValuePair<string, ulong[]>("re_chunk_000.pak", cleanHashes), new KeyValuePair<string, ulong[]>("dlc\\re_dlc_stm_1.pak", Hashes(Path.Combine(f.Game, "dlc", "re_dlc_stm_1.pak"))) });
            Zero(pak, cleanHashes[filler]);
            PakRepairResult r = f.Engine.RepairOrphanedRows(false);
            Require(r.Repaired.Count == 1 && r.Repaired[0].Index == filler && r.Repaired[0].How == "the mod manager's archive list" && r.Repaired[0].Value == cleanHashes[filler], "archive list proof: " + r.Message + " / " + String.Join(" | ", r.Evidence));
            // A stale archive list (one live row differs: made before a game update) is not used.
            ulong[] stale = cleanHashes.ToArray(); stale[filler + 1] ^= 1;
            WriteArchiveList(list, new List<KeyValuePair<string, ulong[]>> { new KeyValuePair<string, ulong[]>("re_chunk_000.pak", stale) });
            r = f.Engine.RepairOrphanedRows(false);
            Require(r.Repaired.Count == 0 && r.Unproven.Count == 1 && r.Evidence.Any(e => e.Contains("disagrees with 1 entry")), "a stale archive list was used: " + String.Join(" | ", r.Evidence));
            WriteArchiveList(list, new List<KeyValuePair<string, ulong[]>> { new KeyValuePair<string, ulong[]>("re_chunk_000.pak", cleanHashes) });
            f.Engine.RepairOrphanedRows(true);
            Require(ReplaceEngine.Sha(pak) == cleanSha, "archive list repair not byte-exact");
            File.Delete(list);
            // Fluffy's installed.ini history (a copy the Manager kept in Data\fluffy-ini-backups) proves a path.
            Zero(f, ChunMesh);
            ReplaceChecks.Put(f.Engine.FluffyIniBackupDir, "installed-old.ini", B("[Chun Li Bare Legs C1 - No head ribbon]\r\nModName=Chun Li Bare Legs - No head ribbon\r\ninstalltype=invalidate\r\nfile=" + ChunMesh + "\r\n"));
            r = f.Engine.RepairOrphanedRows(true);
            Require(r.Repaired.Count == 1 && r.Repaired[0].PathSource.StartsWith("installed.ini history") && r.Repaired[0].Path == ChunMesh && ReplaceEngine.Sha(pak) == cleanSha, "installed.ini history proof: " + r.Message);
            // Ambiguity: two known paths fit between the row's neighbours: nothing is written, Repair says why.
            int aki = Zero(f, AkiMesh);
            string other = PathInGap(pak, aki, "new_texture");
            WriteTsv(f.Game, new[] { AkiMesh, other });
            r = f.Engine.RepairOrphanedRows(false);
            Require(r.Repaired.Count == 0 && r.Unproven.Count == 1 && r.Unproven[0].Problem == "2 known paths fit between its neighbours", "ambiguity: " + (r.Unproven.Count > 0 ? r.Unproven[0].Problem : r.Message));
            string before = ReplaceEngine.Sha(pak);
            ReplaceChecks.Refused(delegate { f.Engine.RepairOrphanedRows(true); }, "Couldn't repair 1 game entry");
            Require(ReplaceEngine.Sha(pak) == before, "an ambiguous row was written");
            // One known path: proven. A row whose data is plainly another type (TEX data for a .mesh path) is refused.
            WriteTsv(f.Game, new[] { AkiMesh });
            Require(f.Engine.RepairOrphanedRows(false).Repaired.Count == 1, "one known path must prove the row");
            f.Engine.RepairOrphanedRows(true);
            Require(ReplaceEngine.Sha(pak) == cleanSha, "hash(path) repair not byte-exact");
            ReplaceChecks.Fixture g = SortedGame(Path.Combine(root, "typed"), new Dictionary<string, byte[]> { { AkiMesh, B("TEX\0 not a mesh") } }, 20);
            Zero(g, AkiMesh); WriteTsv(g.Game, new[] { AkiMesh });
            r = g.Engine.RepairOrphanedRows(false);
            Require(r.Repaired.Count == 0 && r.Unproven.Count == 1 && r.Unproven[0].Problem.EndsWith("its data is a .tex file, not .mesh"), "type check: " + (r.Unproven.Count > 0 ? r.Unproven[0].Problem : r.Message));
            // A run of adjacent zeroed rows: as many known paths as rows between the nearest known rows assigns them in order.
            ReplaceChecks.Fixture h = SortedGame(Path.Combine(root, "run"), new Dictionary<string, byte[]>(), 0);
            var real = new[] { ReplaceChecks.V1, ReplaceChecks.V2, ReplaceChecks.V3, ModFixes.ReferenceCmdPath, ReplaceChecks.V5, ReplaceChecks.V6 }.OrderBy(PakHash.PathHash).ToList();
            string hpak = BasePak(h), hclean = ReplaceEngine.Sha(hpak);
            foreach (string p in real.Skip(1).Take(3)) Zero(h, p);
            WriteTsv(h.Game, real);
            r = h.Engine.RepairOrphanedRows(true);
            Require(r.Repaired.Count == 3 && r.Repaired.All(x => x.How == "hash(path) between its neighbours") && ReplaceEngine.Sha(hpak) == hclean, "a run of three rows: " + r.Message);
            // An unsorted table: hash(path) proves nothing; the TOC backup still does.
            ReplaceChecks.Fixture u = ReplaceChecks.Create(Path.Combine(root, "unsorted"), false, ReplaceMethods.Pak);
            string upak = BasePak(u);
            var rows = Hashes(upak).Select((x, i) => new { x, i }).ToList();
            var unsorted = new Dictionary<ulong, byte[]>(); PakIndex ui = PakIndex.Open(upak);
            foreach (string p in real) unsorted[PakHash.PathHash(p)] = ui.ReadByPath(p);
            var order = unsorted.OrderBy(e => e.Key).ToList(); var swapped = order[0]; order[0] = order[order.Count - 1]; order[order.Count - 1] = swapped;
            File.Delete(upak); GamePaks.Forget(upak);
            PakWriter.WriteFixture(upak, order.Select(e => new KeyValuePair<string, byte[]>("#hash:" + e.Key.ToString("X16"), e.Value)).ToList(), 0x28376913, 0);
            Require(!PakRepair.IsSorted(PakIndex.Open(upak)) && rows.Count == 6, "fixture must be unsorted");
            string uclean = ReplaceEngine.Sha(upak);
            u.Engine.EnsureTocBackup(PakIndex.Open(upak));
            Directory.Move(u.Engine.PakBackupDir, u.Engine.PakBackupDir + ".off");
            Zero(u, real[2]); WriteTsv(u.Game, real);
            r = u.Engine.RepairOrphanedRows(false);
            Require(r.Repaired.Count == 0 && r.Unproven.Single().Problem == "the entry table isn't sorted, so only a backup can prove it", "unsorted: " + r.Message);
            Directory.Move(u.Engine.PakBackupDir + ".off", u.Engine.PakBackupDir);
            r = u.Engine.RepairOrphanedRows(true);
            Require(r.Repaired.Single().How == "the TOC backup" && ReplaceEngine.Sha(upak) == uclean, "unsorted with the TOC backup: " + r.Message);
        }

        // ---- Owned rows are never repaired: the Manager's own rows and rows any Fluffy installed.ini (either root) owns
        internal static void OwnedRows(string root, FakeProcessProbe probe)
        {
            var extra = new Dictionary<string, byte[]> { { AkiMesh, Mesh("aki") }, { AkiMdf, Mdf("aki") }, { ChunMesh, Mesh("chun") } };
            ReplaceChecks.Fixture f = SortedGame(Path.Combine(root, "game"), extra, 40);
            string pak = BasePak(f);
            var clean = ReplaceChecks.Snapshot(f.Game);
            // The Manager's PAK-method replacement (zeroes V1 and V5 in the base and V1 in the DLC).
            ReplaceMod mod = ReplaceChecks.Install(f, "modB", "Cammy Alt");
            int managerRows = f.Engine.LoadState().Deployed.Sum(d => d.Zeroed.Count);
            Require(managerRows == 3, "fixture: the Manager zeroed " + managerRows + " rows");
            // Two Fluffy instances (they don't know each other), each with a PAK install of one natives file.
            var fluffyA = new FluffyLinkChecks.FakeFluffy(Path.Combine(root, "Fluffy", "Games", "SF6"), f.Game);
            var fluffyB = new FluffyLinkChecks.FakeFluffy(Path.Combine(root, "Fluffy - Copy", "Games", "SF6"), f.Game);
            f.Engine.FluffyProvider = delegate { return FluffyInstalls.Load(new[] { fluffyA.Root, fluffyB.Root }); };
            fluffyA.Install("Chun", "Chun Li Bare Legs", "invalidate", new Dictionary<string, byte[]> { { ChunMesh, Mesh("bare legs") } }, false, null); Zero(f, ChunMesh);
            fluffyB.Install("Aki", "A.K.I mesh", "invalidate", new Dictionary<string, byte[]> { { AkiMesh, Mesh("aki mod") } }, false, null); Zero(f, AkiMesh);
            Require(f.Engine.Status().OrphanedRows == 0 && !f.Engine.Status().Summary.Contains("left invalidated"), "owned rows counted as orphaned: " + f.Engine.Status().Summary);
            PakRepairResult r = f.Engine.RepairOrphanedRows(false);
            Require(r.Message == "No game entries need repair." && r.Owned.Count == 5 && r.Owned.Count(o => o.Owner == "the Manager") == 3 && r.Owned.Any(o => o.Owner == "the external mod manager's mod 'Chun Li Bare Legs'") && r.Owned.Any(o => o.Owner == "the external mod manager's mod 'A.K.I mesh'"), "owners: " + String.Join(" | ", r.Owned.Select(o => o.Where + " " + o.Owner)));
            // One row no one owns (the mdf2, proven by the game's index) is repaired; every owned row stays zeroed.
            Zero(f, AkiMdf); WriteTsv(f.Game, new[] { AkiMdf });
            Require(f.Engine.Status().OrphanedRows == 1, "the orphaned row is not counted");
            probe.Fluffy = true; ReplaceChecks.Refused(delegate { f.Engine.RepairOrphanedRows(true); }, "Close the external mod manager"); probe.Fluffy = false;
            probe.Game = true; ReplaceChecks.Refused(delegate { f.Engine.RepairOrphanedRows(true); }, "Close SF6"); probe.Game = false;
            r = f.Engine.RepairOrphanedRows(true);
            ulong[] now = Hashes(pak);
            Require(r.Repaired.Count == 1 && r.Repaired[0].Path == AkiMdf && now.Contains(AkiMdfHash) && !now.Contains(AkiMeshHash) && !now.Contains(ChunMeshHash) && !now.Contains(PakHash.PathHash(ReplaceChecks.V5)) && !now.Contains(PakHash.PathHash(ReplaceChecks.V1)), "Repair touched an owned row: " + r.Message);
            // 1.0.1: the external manager's mods are uninstalled there, never here; the Manager's own mod still
            // uninstalls during the lockout and gives back only its own rows.
            ReplaceChecks.Refused(delegate { string folder; f.Engine.UninstallFluffy(fluffyA.Root, "Chun", out folder); }, "Uninstall it in the external mod manager.");
            f.Engine.Uninstall(mod.Id);
            now = Hashes(pak);
            Require(now.Contains(PakHash.PathHash(ReplaceChecks.V5)) && now.Contains(PakHash.PathHash(ReplaceChecks.V1)) && !now.Contains(ChunMeshHash) && !now.Contains(AkiMeshHash) && File.Exists(G(f, ChunMesh)), "the Manager's uninstall must restore only its own rows");
        }

        // ---- The Manager uninstalls an installtype=pakcopy_reengine_invalidate mod ("1. Aki Default", the first Fluffy)
        private static byte[] ModPak(string root, string name, params string[] paths)
        {
            string file = Path.Combine(root, name);
            if (File.Exists(file)) File.Delete(file);
            Directory.CreateDirectory(root);
            PakWriter.WriteFixture(file, paths.Select(p => new KeyValuePair<string, byte[]>(p, B("pak copy of " + p))).ToList(), 0, 0);
            return File.ReadAllBytes(file);
        }

        // ---- Both Fluffy instances are watched (two Fluffy folders, like Fluffy and Fluffy - Copy), also one that appears later
        internal static void WatchBothRoots(string root)
        {
            string a = Path.Combine(root, "Fluffy", "Games", "SF6"), b = Path.Combine(root, "Games", "Fluffy - Copy", "Games", "SF6");
            Directory.CreateDirectory(a);
            using (var watcher = new FluffyWatcher(new[] { a, b }, 100))
            {
                Require(watcher.Watched.Count == 1 && watcher.Missing.Count == 1 && !watcher.NeedsRestart, "a missing root must be remembered");
                Directory.CreateDirectory(b);
                Require(watcher.NeedsRestart, "a root that appeared must restart the watcher");
            }
            using (var watcher = new FluffyWatcher(new[] { a, b }, 100))
            using (var changed = new AutoResetEvent(false))
            {
                watcher.Changed += delegate { changed.Set(); };
                Require(watcher.Watched.Count == 2 && !watcher.NeedsRestart, "both roots must be watched: " + String.Join(", ", watcher.Watched));
                foreach (string r in new[] { a, b })
                {
                    changed.Reset();
                    File.WriteAllText(Path.Combine(r, "installed.ini.new"), "[M]\r\nModName=M\r\nfile=x.txt\r\n");
                    if (File.Exists(Path.Combine(r, "installed.ini"))) File.Replace(Path.Combine(r, "installed.ini.new"), Path.Combine(r, "installed.ini"), null); else File.Move(Path.Combine(r, "installed.ini.new"), Path.Combine(r, "installed.ini"));
                    Require(changed.WaitOne(2000), "no change reported for " + r);
                    changed.Reset(); File.Delete(Path.Combine(r, "installed.ini"));
                    Require(changed.WaitOne(2000), "no removal reported for " + r);
                }
            }
            Require(FluffyInstalls.StampOf(new[] { a, b }).Split('|').Length == 2, "the stamp covers both roots");
        }
    }
}
