using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace SlotsInstaller
{
    // 25 Nov 2026 (the user, after an old stage mod stopped loading: "ideally, we can fix it via slotsloader manager"):
    // outdated type signatures in scene, user and prefab files, loose or inside a mod's .pak (a stage mod is often
    // one PAK). As SwapRepairs: only documented migrations, each a type whose fields did not change, with its old and
    // current signature, and only when a game file confirms the game uses the current one. Anything else stays as it is.
    internal static class PakModFixes
    {
        internal sealed class Migration
        {
            internal uint Type, Old, Current;
            internal string Name;
            internal string[] Proof;   // game files that hold the type; the first readable one must show Current
        }

        internal static readonly Migration[] Migrations =
        {
            // Aokigahara (Training Room stage mod, June 2026): 4 World Tour object setters in ess0000_00_level.scn.
            // Its fields read end to end with the current layout (REasy dump of 8 Nov 2026), so only the signature moved.
            new Migration { Type = 0x6F2DDD5A, Old = 0x2282AF4F, Current = 0x630E72C4, Name = "app.worldtour.om.WTCityOMSetter",
                Proof = new[] { "natives/stm/product/level/scene/wtc/wtc0400/control/area/wtc0400_48.scn.20",
                                "natives/stm/product/level/scene/wtc/wtc0400/control/area/wtc0400_42.scn.20",
                                "natives/stm/product/level/scene/wtc/wtc0100/control/area/wtc0100_00_night.scn.20" } },
        };

        internal const long PakReadLimit = 512L * 1024 * 1024;   // as PakIndex.ReadRaw
        private const long EntryReadLimit = 64L * 1024 * 1024;   // scene, user and prefab files are far smaller

        private static uint U32(byte[] b, long o) { return BitConverter.ToUInt32(b, (int)o); }
        private static ulong U64(byte[] b, long o) { return BitConverter.ToUInt64(b, (int)o); }

        // The RSZ instance table (type, signature pairs) of a scene, user or prefab file; false when it doesn't verify.
        internal static bool InstanceTable(byte[] b, out long table, out uint count)
        {
            table = 0; count = 0;
            if (b == null || b.Length < 64 || b[3] != 0) return false;
            int pointer;
            if (b[0] == 'S' && b[1] == 'C' && b[2] == 'N') pointer = 56;
            else if (b[0] == 'U' && b[1] == 'S' && b[2] == 'R') pointer = 32;
            else if (b[0] == 'P' && b[1] == 'F' && b[2] == 'B') pointer = 48;
            else return false;
            ulong r = U64(b, pointer);
            if (r < 32 || r > (ulong)b.Length - 48) return false;
            long R = (long)r;
            if (b[R] != 'R' || b[R + 1] != 'S' || b[R + 2] != 'Z' || b[R + 3] != 0 || U32(b, R + 4) != 16) return false;
            uint instances = U32(b, R + 12);
            ulong inst = U64(b, R + 24), data = U64(b, R + 32);
            ulong remaining = (ulong)b.Length - r;
            if (instances == 0 || instances > 1000000 || inst > remaining || 8UL * instances > remaining - inst || data < inst + 8UL * instances || data > remaining) return false;
            table = R + (long)inst; count = instances;
            return U32(b, table) == 0 && U32(b, table + 4) == 0;
        }

        // The migrations the installed game confirms (its proof file holds the type with the current signature),
        // and the ones it doesn't (left as they are, with a warning when a mod needs them).
        internal static List<Migration> Confirmed(GameReference reference, List<Migration> unconfirmed)
        {
            var result = new List<Migration>();
            foreach (Migration m in Migrations)
            {
                bool ok = false;
                if (reference != null)
                    foreach (string proof in m.Proof)
                    {
                        byte[] game = reference.ReadGameFile(proof);
                        long table; uint count;
                        if (!InstanceTable(game, out table, out count)) continue;
                        bool seen = false, current = true;
                        for (uint i = 1; i < count; i++)
                        {
                            if (U32(game, table + 8L * i) != m.Type) continue;
                            seen = true; if (U32(game, table + 8L * i + 4) != m.Current) current = false;
                        }
                        if (!seen) continue;
                        ok = current; break;
                    }
                if (ok) result.Add(m); else if (unconfirmed != null) unconfirmed.Add(m);
            }
            return result;
        }

        // Rewrites old signatures in one file's instance table; counts per migration. Returns a new array when changed.
        private static byte[] UpgradeTable(byte[] data, IList<Migration> confirmed, IList<Migration> unconfirmed, Dictionary<Migration, int> done, HashSet<Migration> needed)
        {
            long table; uint count;
            if (!InstanceTable(data, out table, out count)) return null;
            byte[] output = null;
            for (uint i = 1; i < count; i++)
            {
                long at = table + 8L * i;
                uint type = U32(data, at), crc = U32(data, at + 4);
                foreach (Migration m in confirmed)
                {
                    if (type != m.Type || crc != m.Old) continue;
                    if (output == null) output = (byte[])data.Clone();
                    Buffer.BlockCopy(BitConverter.GetBytes(m.Current), 0, output, (int)at + 4, 4);
                    int n; done.TryGetValue(m, out n); done[m] = n + 1;
                }
                if (unconfirmed != null) foreach (Migration m in unconfirmed) if (type == m.Type && crc == m.Old) needed.Add(m);
            }
            return output;
        }

        private static List<FixResult> Results(Dictionary<Migration, int> done, HashSet<Migration> needed, int files, bool inPak)
        {
            var results = new List<FixResult>();
            foreach (var pair in done.OrderBy(p => p.Key.Name, StringComparer.Ordinal))
                results.Add(new FixResult { Kind = "signature", Count = pair.Value, Changed = true,
                    Message = "Outdated type signature updated (" + pair.Value + " instance" + (pair.Value == 1 ? "" : "s") + " of " + pair.Key.Name + (inPak ? ", in " + files + " file" + (files == 1 ? "" : "s") + " inside the PAK" : "") + ")" });
            foreach (Migration m in needed.OrderBy(m => m.Name, StringComparer.Ordinal))
                results.Add(new FixResult { Kind = "stale", Warning = true,
                    Message = "Older signature of " + m.Name + " left as it is (the game's current one could not be confirmed); the mod may not load" });
            return results;
        }

        // A loose scene, user or prefab file.
        internal static List<FixResult> UpgradeFile(ref byte[] data, GameReference reference)
        {
            long table; uint count;
            if (!InstanceTable(data, out table, out count)) return new List<FixResult>();
            if (!NeedsAny(data, table, count)) return new List<FixResult>();
            var unconfirmed = new List<Migration>();
            List<Migration> confirmed = Confirmed(reference, unconfirmed);
            var done = new Dictionary<Migration, int>(); var needed = new HashSet<Migration>();
            byte[] output = UpgradeTable(data, confirmed, unconfirmed, done, needed);
            if (output != null) data = output;
            return Results(done, needed, 1, false);
        }

        private static bool NeedsAny(byte[] data, long table, uint count)
        {
            for (uint i = 1; i < count; i++)
            {
                uint type = U32(data, table + 8L * i), crc = U32(data, table + 8L * i + 4);
                foreach (Migration m in Migrations) if (type == m.Type && crc == m.Old) return true;
            }
            return false;
        }

        // A mod's .pak (KPKA v4 without features): every entry that is a scene, user or prefab file with an old signature
        // is rewritten (raw deflate, appended at the end, its table row repointed); every other byte stays as it was.
        // An encrypted, unreadable or odd PAK is left unchanged.
        internal static List<FixResult> UpgradePak(ref byte[] pak, GameReference reference)
        {
            var results = new List<FixResult>();
            if (pak == null || pak.Length < PakIndex.HeaderSize || pak[0] != 'K' || pak[1] != 'P' || pak[2] != 'K' || pak[3] != 'A') return results;
            if (pak[4] != 4 || BitConverter.ToUInt16(pak, 6) != 0) return results;   // another version, or an encrypted/extended table
            uint entries = U32(pak, 8);
            long dataStart = PakIndex.HeaderSize + (long)entries * PakIndex.EntrySize;
            if (dataStart > pak.Length) return results;
            List<Migration> confirmed = null, unconfirmed = null;
            var done = new Dictionary<Migration, int>(); var needed = new HashSet<Migration>();
            var replaced = new List<KeyValuePair<int, byte[]>>();
            for (int i = 0; i < entries; i++)
            {
                long row = PakIndex.HeaderSize + (long)i * PakIndex.EntrySize;
                long offset = BitConverter.ToInt64(pak, (int)row + 8), size = BitConverter.ToInt64(pak, (int)row + 16), full = BitConverter.ToInt64(pak, (int)row + 24), attributes = BitConverter.ToInt64(pak, (int)row + 32);
                if (offset < dataStart || size < 0 || full < 0 || offset > pak.Length - size) return new List<FixResult>();   // the table doesn't parse: leave it
                if (full < 64 || full > EntryReadLimit) continue;
                int comp = (int)(attributes & 0xF);
                // Only scene, user and prefab files are unpacked: a stored entry shows its magic, a deflated one after
                // 4 inflated bytes; a zstd entry has to be unpacked whole, so only up to 16 MB (scenes are far smaller).
                if (comp == 0 || size == full) { if (!Tagged(pak, offset)) continue; }
                else if (comp == 1) { if (!DeflatedTagged(pak, offset, size)) continue; }
                else if (full > 16L * 1024 * 1024) continue;
                byte[] data;
                try { data = PakIndex.Decompress(Slice(pak, offset, size), attributes, full); }
                catch (Exception ex) { if (ex is OutOfMemoryException) throw; continue; }   // an entry this reader can't unpack is never changed
                if (data.LongLength != full || !Tagged(data, 0)) continue;
                long table; uint count;
                if (!InstanceTable(data, out table, out count) || !NeedsAny(data, table, count)) continue;
                if (confirmed == null) { unconfirmed = new List<Migration>(); confirmed = Confirmed(reference, unconfirmed); }
                byte[] output = UpgradeTable(data, confirmed, unconfirmed, done, needed);
                if (output != null) replaced.Add(new KeyValuePair<int, byte[]>(i, output));
            }
            if (replaced.Count > 0)
            {
                using (var ms = new MemoryStream(pak.Length + 4096))
                {
                    ms.Write(pak, 0, pak.Length);
                    var updates = new List<KeyValuePair<long, long[]>>();
                    foreach (var r in replaced)
                    {
                        byte[] packed = Deflate(r.Value);
                        long at = ms.Length;
                        ms.Write(packed, 0, packed.Length);
                        long row = PakIndex.HeaderSize + (long)r.Key * PakIndex.EntrySize;
                        long attributes = BitConverter.ToInt64(pak, (int)row + 32);
                        updates.Add(new KeyValuePair<long, long[]>(row, new[] { at, packed.LongLength, r.Value.LongLength, (attributes & ~0xFL) | 1L }));
                    }
                    byte[] output = ms.ToArray();
                    foreach (var u in updates)
                        for (int k = 0; k < 4; k++) Buffer.BlockCopy(BitConverter.GetBytes(u.Value[k]), 0, output, (int)u.Key + 8 + 8 * k, 8);
                    pak = output;
                }
            }
            return Results(done, needed, replaced.Count, true);
        }

        private static bool Tagged(byte[] b, long at)
        {
            if (at > b.Length - 4 || b[at + 3] != 0) return false;
            byte a = b[at], c = b[at + 1], d = b[at + 2];
            return (a == 'S' && c == 'C' && d == 'N') || (a == 'U' && c == 'S' && d == 'R') || (a == 'P' && c == 'F' && d == 'B');
        }
        private static bool DeflatedTagged(byte[] pak, long offset, long size)
        {
            for (int skip = 0; skip <= 2 && skip < size; skip += 2)   // raw deflate, or with a zlib header (as PakIndex.Decompress)
                try
                {
                    using (var input = new MemoryStream(pak, (int)offset + skip, (int)size - skip))
                    using (var z = new DeflateStream(input, CompressionMode.Decompress))
                    {
                        var head = new byte[4]; int got = 0;
                        while (got < 4) { int n = z.Read(head, got, 4 - got); if (n <= 0) break; got += n; }
                        if (got == 4 && Tagged(head, 0)) return true;
                    }
                }
                catch (InvalidDataException) { }
            return false;
        }
        private static byte[] Slice(byte[] b, long offset, long size) { var s = new byte[size]; Buffer.BlockCopy(b, (int)offset, s, 0, (int)size); return s; }
        internal static byte[] Deflate(byte[] data)
        {
            using (var ms = new MemoryStream())
            {
                using (var z = new DeflateStream(ms, CompressionLevel.Optimal, true)) z.Write(data, 0, data.Length);
                return ms.ToArray();
            }
        }

        internal static bool IsSceneLike(string path)
        {
            string low = (path ?? "").ToLowerInvariant();
            return low.EndsWith(".scn.20") || low.EndsWith(".user.2") || low.EndsWith(".pfb.17");
        }

        // The game's reference for these fixes when only the game folder is known (Extra stage): its base and DLC PAKs.
        internal static GameReference Reference(string gameRoot)
        {
            GamePaks paks = GamePaks.Load(gameRoot);
            return GameReference.Load(gameRoot, paks.Base, paks.Dlc);
        }

        // Every .pak (outside natives) and every loose scene, user and prefab file in a folder, fixed in place
        // (Extra stage calls it on its staging folder before the move into stage_mods). One result list for all.
        internal static List<FixResult> FixFolder(string folder, GameReference reference)
        {
            var all = new List<FixResult>();
            if (String.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return all;
            foreach (string file in Directory.GetFiles(folder, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                OperationContext.ThrowIfCancellationRequested();
                string rel = file.Substring(folder.TrimEnd('\\', '/').Length + 1).Replace('\\', '/');
                bool inNatives = rel.Split('/').Any(p => p.Equals("natives", StringComparison.OrdinalIgnoreCase));
                bool isPak = !inNatives && rel.EndsWith(".pak", StringComparison.OrdinalIgnoreCase);
                if (!isPak && !(inNatives && IsSceneLike(rel))) continue;
                var info = new FileInfo(file);
                if (info.Length > (isPak ? PakReadLimit : EntryReadLimit)) continue;
                byte[] data = File.ReadAllBytes(file);
                List<FixResult> fixes = isPak ? UpgradePak(ref data, reference) : UpgradeFile(ref data, reference);
                if (fixes.Any(f => f.Changed)) File.WriteAllBytes(file, data);
                all.AddRange(fixes);
            }
            return all;
        }
    }
}
