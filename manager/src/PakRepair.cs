using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace SlotsInstaller
{
    // 2.4.2: zeroed rows in the base and DLC PAKs (Fluffy-style invalidation: the first 8 bytes of a row, the u64
    // path hash, set to zero) and the evidence that proves the hash a row held. Only a proven value is ever written.
    //   1. The Manager's TOC backup (Data\pak-backups) when the PAK still has its identity: same name, length, header
    //      and every non-hash field of every row, and no non-zero row disagreeing with the backup.
    //   2. Fluffy's archive list (<Fluffy>\Games\SF6\ArchiveList.bin, every row's original hash; Fluffy re-validates
    //      from it), only when it lists this PAK with the same row count and offsets and agrees with every non-zero row.
    //   3. hash(path) of a known path. The entry table is sorted ascending by the u64 hash (checked, never assumed), so
    //      the zeroed rows between two known rows held exactly the known paths whose hashes lie strictly between those
    //      rows' hashes: as many paths as rows assigns them in order; any other count proves nothing (ambiguous). A row
    //      whose data is plainly another file type (MESH, MDF, TEX, USR magic) than the path's is refused.
    // A row the Manager zeroed (replace-state.json) or that a path in a current Fluffy installed.ini fits is owned and
    // never repaired (see Analyze).
    internal sealed class RowProof
    {
        internal string Pak = "";                         // relative to the game root
        internal int Index;
        internal ulong Value;                             // the proven original hash; 0 = not proven
        internal string Path = "", PathSource = "";       // the known path whose hash it is, and where that path came from
        internal string How = "", Problem = "", Owner = "";
        internal bool ByBackup;                           // proven by the TOC backup or Fluffy's archive list
        internal ulong Low, High; internal bool HighOpen = true;   // hash bounds of the unproven rows around it (exclusive)
        internal bool Proven { get { return Value != 0; } }
        internal bool Orphan { get { return Owner.Length == 0; } }
        internal bool Fits(ulong hash) { return hash > Low && (HighOpen || hash < High); }
        internal string Where { get { return Pak + " #" + Index; } }
    }

    // Paths whose hash may prove a zeroed row (natives/ only), with where each came from. The first source wins.
    internal sealed class KnownPaths
    {
        internal readonly Dictionary<ulong, KeyValuePair<string, string>> ByHash = new Dictionary<ulong, KeyValuePair<string, string>>();
        private ulong[] sorted;
        internal int Count { get { return ByHash.Count; } }
        internal void Add(string path, string source)
        {
            if (String.IsNullOrWhiteSpace(path)) return;
            string p = FluffyInstalls.Key(path);
            if (!p.StartsWith("natives/", StringComparison.OrdinalIgnoreCase) || p.IndexOf("..", StringComparison.Ordinal) >= 0) return;
            ulong h = PakHash.PathHash(p);
            if (h == 0 || ByHash.ContainsKey(h)) return;
            ByHash[h] = new KeyValuePair<string, string>(p, source); sorted = null;
        }
        internal ulong[] Sorted { get { if (sorted == null) { sorted = ByHash.Keys.ToArray(); Array.Sort(sorted); } return sorted; } }
        // Known hashes h with low < h < high (no upper bound when highOpen), ascending.
        internal List<ulong> Between(ulong low, ulong high, bool highOpen)
        {
            ulong[] s = Sorted; var result = new List<ulong>();
            int lo = 0, hi = s.Length;
            while (lo < hi) { int mid = (lo + hi) / 2; if (s[mid] <= low) lo = mid + 1; else hi = mid; }
            for (int i = lo; i < s.Length && (highOpen || s[i] < high); i++) result.Add(s[i]);
            return result;
        }
    }

    internal sealed class ZeroedRowReport
    {
        internal List<RowProof> Rows = new List<RowProof>();
        internal List<string> Evidence = new List<string>();     // per PAK with zeroed rows: which evidence is usable
        internal int KnownPathCount;
        internal List<RowProof> Orphans { get { return Rows.Where(r => r.Orphan).ToList(); } }
    }

    internal sealed class PakRepairResult
    {
        internal List<RowProof> Repaired = new List<RowProof>(), Unproven = new List<RowProof>(), Owned = new List<RowProof>();
        internal List<string> Evidence = new List<string>();
        internal string Message = "";
        internal bool Applied;
    }

    internal static class PakRepair
    {
        internal static string OrphanText(int count) { return ReplacePlanner.N(count, "game entry") + " left invalidated by another tool"; }

        // Non-zero rows strictly ascending (zeroed rows skipped).
        internal static bool IsSorted(PakIndex p)
        {
            ulong previous = 0; bool any = false;
            for (int i = 0; i < p.Count; i++)
            {
                ulong h = BitConverter.ToUInt64(p.Table, i * PakIndex.EntrySize);
                if (h == 0) continue;
                if (any && h <= previous) return false;
                previous = h; any = true;
            }
            return true;
        }

        internal static List<int> ZeroedRows(PakIndex p)
        {
            var list = new List<int>();
            if (p.CountZeroed() == 0) return list;
            for (int i = 0; i < p.Count; i++) if (BitConverter.ToUInt64(p.Table, i * PakIndex.EntrySize) == 0) list.Add(i);
            return list;
        }

        // The Manager's TOC copy of this PAK identity (EnsureTocBackup), compared field by field; null when none matches.
        internal static ulong[] ReadBackup(string backupDir, PakIndex p, out string file, out string note)
        {
            file = ""; note = "no TOC backup of this PAK";
            if (String.IsNullOrEmpty(backupDir) || !Directory.Exists(backupDir)) return null;
            string name = System.IO.Path.GetFileName(p.Path);
            foreach (string toc in Directory.GetFiles(backupDir, name + "-" + p.Length + "-" + p.Fingerprint.ToString("X8") + "-*.toc"))
            {
                try
                {
                    if (new FileInfo(toc).Length != PakIndex.HeaderSize + p.Table.LongLength) { note = "the TOC backup is for another PAK layout"; continue; }
                    byte[] b = File.ReadAllBytes(toc);
                    bool same = true;
                    for (int k = 0; k < PakIndex.HeaderSize && same; k++) same = b[k] == p.Header[k];
                    var values = new ulong[p.Count];
                    for (int i = 0; i < p.Count && same; i++)
                    {
                        int t = PakIndex.HeaderSize + i * PakIndex.EntrySize, c = i * PakIndex.EntrySize;
                        for (int k = 8; k < PakIndex.EntrySize; k++) if (b[t + k] != p.Table[c + k]) { same = false; break; }
                        ulong bh = BitConverter.ToUInt64(b, t), ch = BitConverter.ToUInt64(p.Table, c);
                        if (bh != 0 && ch != 0 && bh != ch) same = false;
                        values[i] = bh;
                    }
                    if (!same) { note = "the TOC backup doesn't match this PAK (it was updated)"; continue; }
                    file = toc; note = "TOC backup matches (" + System.IO.Path.GetFileName(toc) + ")";
                    return values;
                }
                catch (Exception ex) { if (ex is OutOfMemoryException) throw; note = "the TOC backup could not be read (" + ex.Message.TrimEnd('.') + ")"; }
            }
            return null;
        }

        // Fluffy's ArchiveList.bin: "ARCH", u32 version, u32 archive count, u32 name block length, 8 bytes, the archive
        // names (NUL-separated, game-relative), then per archive (after padding) u32 0, u32 row count and per row
        // u64 0, u64 row offset in the PAK (16 + 48 * index), u64 hash. Read only; used only when it agrees with the PAK.
        internal static ulong[] ReadArchiveList(string listFile, string relative, PakIndex p, out string note)
        {
            note = "";
            byte[] b;
            try
            {
                if (!File.Exists(listFile)) { note = "no archive list"; return null; }
                using (var s = new FileStream(listFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var ms = new MemoryStream()) { s.CopyTo(ms); b = ms.ToArray(); }
            }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; note = "the archive list could not be read (" + ex.Message.TrimEnd('.') + ")"; return null; }
            if (b.Length < 24 || b[0] != (byte)'A' || b[1] != (byte)'R' || b[2] != (byte)'C' || b[3] != (byte)'H') { note = "not an archive list"; return null; }
            int archives = BitConverter.ToInt32(b, 8), nameLength = BitConverter.ToInt32(b, 12);
            if (archives < 1 || archives > 256 || nameLength < 1 || 24L + nameLength > b.Length) { note = "unreadable archive list"; return null; }
            List<string> names = Encoding.UTF8.GetString(b, 24, nameLength).Split('\0').Where(n => n.Length > 0).ToList();
            if (names.Count != archives) { note = "unreadable archive list"; return null; }
            string want = (relative ?? "").Replace('\\', '/');
            int which = names.FindIndex(n => String.Equals(n.Replace('\\', '/'), want, StringComparison.OrdinalIgnoreCase));
            if (which < 0) { note = "the archive list doesn't list " + relative; return null; }
            long pos = 24 + nameLength;
            for (int k = 0; k < archives; k++)
            {
                long start = -1;
                for (long q = (pos + 3) / 4 * 4; q + 20 <= b.Length && q < pos + 4096; q += 4)
                {
                    uint count = BitConverter.ToUInt32(b, (int)q);
                    if (count == 0 || BitConverter.ToUInt64(b, (int)q + 12) != PakIndex.HeaderSize || q + 4 + (long)count * 24 > b.Length) continue;
                    start = q; break;
                }
                if (start < 0) { note = "unreadable archive list"; return null; }
                uint rows = BitConverter.ToUInt32(b, (int)start);
                if (k == which)
                {
                    if (rows != p.Count) { note = "the archive list is for another version of " + relative + " (" + rows + " rows)"; return null; }
                    var values = new ulong[rows]; int disagree = 0;
                    for (int i = 0; i < rows; i++)
                    {
                        long r = start + 4 + (long)i * 24;
                        if (BitConverter.ToUInt64(b, (int)r + 8) != (ulong)(PakIndex.HeaderSize + (long)i * PakIndex.EntrySize)) { note = "unreadable archive list"; return null; }
                        values[i] = BitConverter.ToUInt64(b, (int)r + 16);
                        ulong now = BitConverter.ToUInt64(p.Table, i * PakIndex.EntrySize);
                        if (now != 0 && now != values[i]) disagree++;
                    }
                    if (disagree > 0) { note = "the archive list disagrees with " + ReplacePlanner.N(disagree, "entry") + " (made before a game update)"; return null; }
                    note = "The mod manager's archive list matches (" + listFile + ")";
                    return values;
                }
                pos = start + 4 + (long)rows * 24;
            }
            note = "unreadable archive list";
            return null;
        }

        // "mesh" for ...mesh.230110883, "tex" for ...tex.241101895, "mdf2", "user"; null when unknown.
        internal static string FileType(string path)
        {
            string leaf = (path ?? "").Replace('\\', '/'); leaf = leaf.Substring(leaf.LastIndexOf('/') + 1);
            string[] parts = leaf.Split('.');
            if (parts.Length >= 3 && parts[parts.Length - 1].All(Char.IsDigit)) return parts[parts.Length - 2].ToLowerInvariant();
            return parts.Length >= 2 ? parts[parts.Length - 1].ToLowerInvariant() : null;
        }
        private static readonly KeyValuePair<string, string>[] Magics = { new KeyValuePair<string, string>("MESH", "mesh"), new KeyValuePair<string, string>("MDF\0", "mdf2"), new KeyValuePair<string, string>("TEX\0", "tex"), new KeyValuePair<string, string>("USR\0", "user") };
        internal static string MagicType(byte[] data)
        {
            if (data == null || data.Length < 4) return null;
            string head = Encoding.ASCII.GetString(data, 0, 4);
            foreach (var m in Magics) if (head == m.Key) return m.Value;
            return null;
        }
        // A reason when the row's data is plainly another file type than the path's; null otherwise (unreadable or
        // unknown data is no contradiction).
        internal static string TypeProblem(PakIndex p, int index, string path)
        {
            string want = FileType(path);
            if (want == null) return null;
            byte[] data;
            try { PakEntry e = p.Entry(index); if (e.CompressedSize > 64L * 1024 * 1024 || e.DecompressedSize > 256L * 1024 * 1024) return null; data = p.Read(e); }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; return null; }
            string got = MagicType(data);
            return got != null && got != want ? "its data is a ." + got + " file, not ." + want : null;
        }

        // Proofs for the zeroed rows of one PAK. backup / archive: the original hashes (null = not usable).
        internal static List<RowProof> Prove(string relative, PakIndex p, List<int> zeroed, ulong[] backup, ulong[] archive, bool sorted, KnownPaths known, bool typeCheck)
        {
            var proofs = new Dictionary<int, RowProof>();
            foreach (int z in zeroed) proofs[z] = new RowProof { Pak = relative, Index = z };
            Func<int, ulong> hash = delegate(int i) { return BitConverter.ToUInt64(p.Table, i * PakIndex.EntrySize); };
            var blocked = new HashSet<int>();
            // 1. Backups. In a sorted table the value must also lie between the live neighbours.
            foreach (int z in zeroed)
            {
                RowProof r = proofs[z];
                int a = z - 1; while (a >= 0 && hash(a) == 0) a--;
                int c = z + 1; while (c < p.Count && hash(c) == 0) c++;
                ulong low = a < 0 ? 0 : hash(a), high = c >= p.Count ? 0 : hash(c); bool open = c >= p.Count;
                foreach (var source in new[] { new KeyValuePair<ulong[], string>(backup, "the TOC backup"), new KeyValuePair<ulong[], string>(archive, "the mod manager's archive list") })
                {
                    if (source.Key == null || r.Proven) continue;
                    ulong v = source.Key[z];
                    if (v == 0) continue;
                    if (sorted && !(v > low && (open || v < high))) { r.Problem = source.Value + " doesn't fit between its neighbours"; blocked.Add(z); continue; }
                    r.Value = v; r.How = source.Value; r.ByBackup = true; r.Problem = "";
                    KeyValuePair<string, string> path;
                    if (known.ByHash.TryGetValue(v, out path)) { r.Path = path.Key; r.PathSource = path.Value; }
                }
                if (r.Proven) blocked.Remove(z);
            }
            // 2. hash(path) over each run of rows still unproven, between the nearest known rows.
            var ordered = zeroed.OrderBy(i => i).ToList();
            for (int k = 0; k < ordered.Count; )
            {
                int s = ordered[k];
                if (proofs[s].Proven) { k++; continue; }
                int e = s; int next = k + 1;
                while (next < ordered.Count && ordered[next] == e + 1 && !proofs[ordered[next]].Proven) { e = ordered[next]; next++; }
                k = next;
                ulong low = s == 0 ? 0 : (proofs.ContainsKey(s - 1) ? proofs[s - 1].Value : hash(s - 1));
                bool open = e + 1 >= p.Count; ulong high = open ? 0 : (proofs.ContainsKey(e + 1) ? proofs[e + 1].Value : hash(e + 1));
                var run = Enumerable.Range(s, e - s + 1).Select(i => proofs[i]).ToList();
                // Unsorted: no bounds (any path may be any of these rows).
                if (!sorted) { foreach (RowProof r in run) if (r.Problem.Length == 0) r.Problem = "the entry table isn't sorted, so only a backup can prove it"; continue; }
                foreach (RowProof r in run) { r.Low = low; r.High = high; r.HighOpen = open; }
                if (run.Any(r => blocked.Contains(r.Index))) { foreach (RowProof r in run) if (r.Problem.Length == 0) r.Problem = "a backup disagrees with the neighbouring entries"; continue; }
                List<ulong> candidates = known.Between(low, high, open);
                if (candidates.Count != run.Count)
                {
                    string problem = candidates.Count == 0 ? "no known path fits between its neighbours" : candidates.Count + " known paths fit between its neighbours" + (run.Count > 1 ? " for " + ReplacePlanner.N(run.Count, "entry") : "");
                    foreach (RowProof r in run) r.Problem = problem;
                    continue;
                }
                string typeProblem = null;
                for (int i = 0; i < run.Count; i++)
                {
                    KeyValuePair<string, string> path = known.ByHash[candidates[i]];
                    if (typeCheck && typeProblem == null) { string t = TypeProblem(p, run[i].Index, path.Key); if (t != null) typeProblem = path.Key + ": " + t; }
                }
                if (typeProblem != null) { foreach (RowProof r in run) r.Problem = typeProblem; continue; }
                for (int i = 0; i < run.Count; i++)
                {
                    KeyValuePair<string, string> path = known.ByHash[candidates[i]];
                    RowProof r = run[i];
                    r.Value = candidates[i]; r.Path = path.Key; r.PathSource = path.Value; r.How = "hash(path) between its neighbours"; r.Problem = "";
                }
            }
            return ordered.Select(i => proofs[i]).ToList();
        }
    }

    // CLI pak-repair: the dry run lists every orphaned entry with its proof (or why it has none) and the owned ones
    // left alone; --yes then restores the proven ones (refused while SF6 or Fluffy runs).
    internal static class PakRepairCommandLine
    {
        internal static string ProofText(RowProof p)
        {
            string from = p.PathSource.Length > 0 ? " (path from " + p.PathSource + ")" : "";
            if (p.How == "hash(path) between its neighbours")
                return "hash(path): the only known path between the neighbouring entries " + p.Low.ToString("X16") + " and " + (p.HighOpen ? "the end of the table" : p.High.ToString("X16")) + from;
            return p.How + from;
        }
        internal static int Run(ReplaceEngine engine, bool yes)
        {
            PakRepairResult r = engine.RepairOrphanedRows(false);
            foreach (string e in r.Evidence) Console.WriteLine("Evidence: " + e);
            int orphans = r.Repaired.Count + r.Unproven.Count;
            Console.WriteLine(orphans == 0 ? "No game entries left invalidated by another tool." : PakRepair.OrphanText(orphans) + ":");
            foreach (RowProof p in r.Repaired) Console.WriteLine("  " + p.Where + " → " + p.Value.ToString("X16") + (p.Path.Length > 0 ? " " + p.Path : "") + " · proved by " + ProofText(p));
            foreach (RowProof p in r.Unproven) Console.WriteLine("  " + p.Where + " · not proven: " + p.Problem + " (left as it is)");
            foreach (var g in r.Owned.GroupBy(o => o.Owner)) Console.WriteLine("Left alone: " + ReplacePlanner.N(g.Count(), "zeroed entry") + " owned by " + g.Key + " (" + String.Join(", ", g.Take(6).Select(o => o.Where)) + (g.Count() > 6 ? ", …" : "") + ")");
            if (!yes)
            {
                Console.WriteLine("Dry run: nothing was changed." + (r.Repaired.Count > 0 ? " Run again with --yes to restore " + ReplacePlanner.N(r.Repaired.Count, "proven entry") + "." : ""));
                return 0;
            }
            Console.WriteLine(engine.RepairOrphanedRows(true).Message);
            return 0;
        }
    }

    internal sealed partial class ReplaceEngine
    {
        internal string PakRepairDir { get { return Path.Combine(DataDir, "pak-repairs"); } }

        // Base PAK first, then the DLC PAKs (the PAKs the PAK method writes); patch PAKs are never touched.
        private List<KeyValuePair<string, PakIndex>> WritablePaks()
        {
            var list = new List<KeyValuePair<string, PakIndex>>();
            string basePath = Path.Combine(GameRoot, "re_chunk_000.pak");
            if (File.Exists(basePath)) list.Add(new KeyValuePair<string, PakIndex>("re_chunk_000.pak", GamePaks.Cached(basePath)));
            string dlc = Path.Combine(GameRoot, "dlc");
            if (Directory.Exists(dlc))
                foreach (string f in Directory.GetFiles(dlc, "*.pak").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    try { list.Add(new KeyValuePair<string, PakIndex>("dlc/" + Path.GetFileName(f), GamePaks.Cached(f))); }
                    catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); }
                }
            return list;
        }

        // <Fluffy>\Data\Log.txt for the root <Fluffy>\Games\SF6 (Fluffy logs "Deleted file: <game>\natives/..." there).
        internal static string FluffyLogPath(string root)
        {
            string games = Path.GetDirectoryName(Path.GetFullPath(root).TrimEnd('\\'));   // <Fluffy>\Games
            string fluffy = games == null ? null : Path.GetDirectoryName(games);
            return fluffy == null ? null : Path.Combine(fluffy, "Data", "Log.txt");
        }

        // Paths whose hash may prove a zeroed row. Always: every current Fluffy installed.ini. With history: installed.ini
        // copies the Manager kept (fluffy-ini-backups, fluffy-removed), Fluffy's own log (<Fluffy>\Data\Log.txt), the
        // Manager's records (replace-state.json, removed replacements) and SlotsLoader's vanilla index of the game's files.
        internal KnownPaths KnownPakPaths(FluffyInstalls fluffy, ReplaceState state, bool history)
        {
            var known = new KnownPaths();
            foreach (FluffyMod m in fluffy.Mods) foreach (string f in m.Files) known.Add(f, "installed.ini (" + m.Name + ")");
            if (!history) return known;
            Action<string, Action> guard = delegate(string what, Action read) { try { read(); } catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.LogLine("Known paths: " + what + " skipped (" + ex.Message + ")"); } };
            guard("installed.ini copies", delegate
            {
                var inis = new List<string>();
                if (Directory.Exists(FluffyIniBackupDir)) inis.AddRange(Directory.GetFiles(FluffyIniBackupDir, "*.ini"));
                if (Directory.Exists(FluffyRemovedDir)) foreach (string d in Directory.GetDirectories(FluffyRemovedDir)) { string b = Path.Combine(d, "installed.ini.before"); if (File.Exists(b)) inis.Add(b); }
                foreach (string ini in inis)
                    foreach (FluffyMod m in FluffyInstalls.Parse(FluffyInstalls.DecodeIni(File.ReadAllBytes(ini)), Path.GetDirectoryName(ini)))
                        foreach (string f in m.Files) known.Add(f, "installed.ini history (" + m.Name + ")");
            });
            foreach (string root in fluffy.Roots)
            {
                string log = null;
                try { log = FluffyLogPath(root); } catch { }
                if (log == null || !File.Exists(log)) continue;
                guard(log, delegate
                {
                    using (var s = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var reader = new StreamReader(s))
                    {
                        string line;
                        while ((line = reader.ReadLine()) != null) foreach (string p in LooseLoader.ParseLogLine(line)) known.Add(p, "the mod manager's log");
                    }
                });
            }
            guard("Manager records", delegate
            {
                foreach (ReplaceMod m in state.Mods) foreach (ModFile f in m.Files) known.Add(f.Path, "Manager record (" + m.Name + ")");
                foreach (DeployedFile d in state.Deployed) known.Add(d.Path, "Manager record");
                foreach (RemovedReplacement r in ListRemoved()) foreach (ModFile f in r.Record.Mod.Files) known.Add(f.Path, "Manager record (" + r.Name + ", removed)");
            });
            guard("Slotsloader's vanilla index", delegate
            {
                string tsv = Path.Combine(GameRoot, "reframework", "data", "SF6_Costumes_Data", "loader", "vanilla_costume_index.tsv");
                if (!File.Exists(tsv)) return;
                bool files = false;
                foreach (string line in File.ReadLines(tsv))
                {
                    if (line.StartsWith("#")) { files = line.Trim() == "#FILES"; continue; }
                    if (!files) continue;
                    string[] cols = line.Split('\t');
                    if (cols.Length < 2) continue;
                    string path = cols[cols.Length - 1].Trim();
                    ulong listed;
                    // The loader lists each path with its hash; a line whose hash doesn't match is not used.
                    if (UInt64.TryParse(cols[0].Trim(), System.Globalization.NumberStyles.HexNumber, null, out listed) && listed == PakHash.PathHash(path)) known.Add(path, "Slotsloader's vanilla index");
                }
            });
            return known;
        }

        // Every zeroed row of the base and DLC PAKs with its proof and owner. `owners`: the Fluffy mods whose rows stay
        // (all current mods; an uninstall passes the remaining ones). Without history only current Fluffy paths are
        // known (enough to tell owned rows apart: the same rule decides ownership in both modes).
        internal ZeroedRowReport AnalyzeZeroedRows(ReplaceState state, FluffyInstalls fluffy, IEnumerable<FluffyMod> owners, bool history, bool typeCheck)
        {
            var report = new ZeroedRowReport();
            var paks = WritablePaks().Select(kv => new { Relative = kv.Key, Pak = kv.Value, Zeroed = PakRepair.ZeroedRows(kv.Value) }).Where(x => x.Zeroed.Count > 0).ToList();
            if (paks.Count == 0) return report;
            KnownPaths known = KnownPakPaths(fluffy, state, history);
            report.KnownPathCount = known.Count;
            var owned = new Dictionary<ulong, FluffyMod>();
            foreach (FluffyMod m in owners) foreach (string f in m.Files) { string k = FluffyInstalls.Key(f); if (k.StartsWith("natives/", StringComparison.OrdinalIgnoreCase)) owned[PakHash.PathHash(k)] = m; }
            var ownedKnown = new KnownPaths();
            foreach (FluffyMod m in owners) foreach (string f in m.Files) ownedKnown.Add(f, m.Name);
            var managerRows = new HashSet<string>(state.Deployed.SelectMany(d => d.Zeroed).Select(z => (z.Pak ?? "").Replace('\\', '/').ToLowerInvariant() + "#" + z.Index));
            foreach (var x in paks)
            {
                string backupFile, backupNote, archiveNote = "no archive list";
                ulong[] backup = PakRepair.ReadBackup(PakBackupDir, x.Pak, out backupFile, out backupNote);
                ulong[] archive = null;
                foreach (string root in fluffy.Roots)
                {
                    string note; archive = PakRepair.ReadArchiveList(Path.Combine(root, "ArchiveList.bin"), x.Relative, x.Pak, out note);
                    if (archive != null || archiveNote == "no archive list") archiveNote = note;
                    if (archive != null) break;
                }
                bool sorted = PakRepair.IsSorted(x.Pak);
                report.Evidence.Add(x.Relative + ": " + ReplacePlanner.N(x.Zeroed.Count, "zeroed entry") + " · " + backupNote + " · " + archiveNote + " · entry table " + (sorted ? "sorted by hash" : "not sorted"));
                foreach (RowProof r in PakRepair.Prove(x.Relative, x.Pak, x.Zeroed, backup, archive, sorted, known, typeCheck))
                {
                    FluffyMod mod;
                    if (managerRows.Contains(x.Relative.ToLowerInvariant() + "#" + r.Index)) r.Owner = "the Manager";
                    else if (r.Proven && owned.TryGetValue(r.Value, out mod)) r.Owner = "the external mod manager's mod '" + mod.Name + "'";
                    else if (!r.Proven)
                    {
                        // Unproven: a current Fluffy path that fits these rows may be theirs.
                        List<ulong> fits = sorted ? ownedKnown.Between(r.Low, r.High, r.HighOpen) : ownedKnown.Sorted.ToList();
                        if (fits.Count > 0) r.Owner = "the external mod manager's mod '" + ownedKnown.ByHash[fits[0]].Value + "'";
                    }
                    report.Rows.Add(r);
                }
            }
            return report;
        }

        // ---- Status: the orphan count is cached until a PAK, installed.ini, the Manager's record or a backup changes.
        private static readonly object orphanLock = new object();
        private static string orphanKey;
        private static int orphanCount;
        private static List<string> orphanWhere = new List<string>();
        internal int OrphanedRows(out List<string> where)
        {
            where = new List<string>();
            if (JournalPending) return 0;
            ReplaceState state;
            try { state = LoadState(); } catch (Exception ex) { if (ex is OutOfMemoryException) throw; return 0; }
            FluffyInstalls fluffy = Fluffy();
            var key = new StringBuilder(GameRoot).Append('|').Append(DataDir).Append('|').Append(String.Join(";", fluffy.Roots)).Append('|').Append(fluffy.Stamp).Append('|');
            foreach (string f in new[] { Path.Combine(GameRoot, "re_chunk_000.pak") }.Concat(Directory.Exists(Path.Combine(GameRoot, "dlc")) ? Directory.GetFiles(Path.Combine(GameRoot, "dlc"), "*.pak") : new string[0]))
            { var info = new FileInfo(f); key.Append(info.Exists ? info.Length + "@" + info.LastWriteTimeUtc.Ticks : "-").Append('|'); }
            // The same analysis as Repair (history and type check included), so the banner and Repair agree.
            var stamps = new List<string> { StatePath, PakBackupDir, FluffyIniBackupDir, FluffyRemovedDir, Path.Combine(GameRoot, "reframework", "data", "SF6_Costumes_Data", "loader", "vanilla_costume_index.tsv") };
            foreach (string r in fluffy.Roots) { stamps.Add(Path.Combine(r, "ArchiveList.bin")); try { string log = FluffyLogPath(r); if (log != null) stamps.Add(log); } catch { } }
            foreach (string f in stamps)
            { try { key.Append(File.Exists(f) ? new FileInfo(f).Length + "@" + File.GetLastWriteTimeUtc(f).Ticks : Directory.Exists(f) ? "d@" + Directory.GetLastWriteTimeUtc(f).Ticks : "-").Append('|'); } catch { key.Append("?|"); } }
            string k = key.ToString();
            lock (orphanLock) if (k == orphanKey) { where = orphanWhere.ToList(); return orphanCount; }
            ZeroedRowReport report = AnalyzeZeroedRows(state, fluffy, fluffy.Mods, true, true);
            List<RowProof> orphans = report.Orphans;
            lock (orphanLock) { orphanKey = k; orphanCount = orphans.Count; orphanWhere = orphans.Select(r => r.Where).ToList(); where = orphanWhere.ToList(); }
            return orphans.Count;
        }

        // The Installed page's Repair and CLI pak-repair: restores the zeroed rows no installed mod owns, each only with
        // a proof. apply = false is the dry run (nothing is written, no lock, no process check).
        internal PakRepairResult RepairOrphanedRows(bool apply)
        {
            IDisposable held = null;
            try
            {
                if (apply)
                {
                    held = Lock();
                    GameProcesses.RequireIdle("repair game entries");
                    if (JournalPending) Recover();
                }
                ReplaceState state = LoadState();
                FluffyInstalls fluffy = Fluffy();
                ZeroedRowReport report = AnalyzeZeroedRows(state, fluffy, fluffy.Mods, true, true);
                var result = new PakRepairResult { Evidence = report.Evidence };
                result.Owned = report.Rows.Where(r => !r.Orphan).ToList();
                result.Unproven = report.Orphans.Where(r => !r.Proven).ToList();
                List<RowProof> repair = report.Orphans.Where(r => r.Proven).ToList();
                if (apply && repair.Count > 0)
                {
                    foreach (var group in repair.GroupBy(r => r.Pak))
                    {
                        string full = GamePath(group.Key);
                        PakIndex p = PakIndex.Open(full, false);
                        var writes = group.Select(r => new PakHashWrite { Index = r.Index, Expected = new byte[8], Value = BitConverter.GetBytes(r.Value) }).ToList();
                        PakWriter.WriteHashes(full, p.Length, p.Header, writes);
                        GamePaks.Forget(full);
                        result.Repaired.AddRange(group);
                    }
                    result.Applied = true;
                    try
                    {
                        Directory.CreateDirectory(PakRepairDir);
                        var lines = new List<string> { "# " + DateTime.UtcNow.ToString("o") + " · " + GameRoot };
                        lines.AddRange(result.Repaired.Select(r => r.Where + "\t" + r.Value.ToString("X16") + "\t" + r.How + "\t" + r.Path + "\t" + r.PathSource));
                        File.WriteAllLines(Path.Combine(PakRepairDir, "repair-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".tsv"), lines);
                    }
                    catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); }
                    Program.LogLine("Repaired " + ReplacePlanner.N(result.Repaired.Count, "game entry") + ": " + String.Join(", ", result.Repaired.Select(r => r.Where + " (" + r.How + ")")));
                }
                else if (!apply) result.Repaired.AddRange(repair);
                int orphans = report.Orphans.Count;
                if (orphans == 0) result.Message = "No game entries need repair.";
                else if (repair.Count == 0)
                {
                    result.Message = "Couldn't repair " + ReplacePlanner.N(orphans, "game entry") + ": nothing proves the original values. Verify SF6's files in Steam to restore them.";
                    if (apply) throw new IOException(result.Message);
                }
                else
                {
                    result.Message = (apply ? "Repaired " : "Would repair ") + ReplacePlanner.N(repair.Count, "game entry") + "." +
                        (result.Unproven.Count > 0 ? " " + result.Unproven.Count + " couldn't be proven (no backup or known path matches " + (result.Unproven.Count == 1 ? "it" : "them") + ")." : "");
                }
                return result;
            }
            finally { if (held != null) held.Dispose(); }
        }
    }
}
