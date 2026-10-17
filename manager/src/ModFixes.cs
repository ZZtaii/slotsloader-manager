using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace SlotsInstaller
{
    // The compatibility fixes Fluffy Mod Manager and SlotsLoader apply to outdated
    // SF6 mod files, ported from SlotsLoader's patch.cpp / loader_core.cpp:
    // stale type signatures (CRC) of colour files, the 2023 colour-file layout,
    // old texture suffixes and wrong texture mip-table pitches.
    internal sealed class FixResult
    {
        internal string Kind = "";        // crc, layout, mip, suffix, path, stale, badmip
        internal string Message = "";
        internal int Count;
        internal bool Changed;
        internal bool Warning;
    }

    internal static class ModFixes
    {
        // app.CostumeMaterialData.MaterialData: Type (enum ePartsType) + Clusters; layout unchanged.
        internal static readonly HashSet<uint> CrcUpgradable = new HashSet<uint> { 0x9E32F1F4 };
        internal static readonly string[] OldTexSuffixes = { "143230113" };
        internal const string ReferenceCmdPath = "natives/stm/product/model/esf/esf001/001/esf001_001_cmd_000.user.2";

        internal static string TypeLabel(uint type)
        {
            switch (type)
            {
                case 0x9E32F1F4: return "app.CostumeMaterialData.MaterialData";
                case 0x9F67268B: return "app.CostumeMaterialData.Cloth";
                default: return "type 0x" + type.ToString("X8");
            }
        }

        private static uint U32(byte[] b, long o) { return BitConverter.ToUInt32(b, (int)o); }
        private static int I32(byte[] b, long o) { return BitConverter.ToInt32(b, (int)o); }
        private static ulong U64(byte[] b, long o) { return BitConverter.ToUInt64(b, (int)o); }
        private static void W32(byte[] b, long o, uint v) { Buffer.BlockCopy(BitConverter.GetBytes(v), 0, b, (int)o, 4); }
        private static void WI32(byte[] b, long o, int v) { Buffer.BlockCopy(BitConverter.GetBytes(v), 0, b, (int)o, 4); }
        private static void W64(byte[] b, long o, ulong v) { Buffer.BlockCopy(BitConverter.GetBytes(v), 0, b, (int)o, 8); }
        private static bool Magic(byte[] b, string m) { if (b == null || b.Length < 4) return false; for (int i = 0; i < 4; i++) if (b[i] != (i < m.Length ? (byte)m[i] : 0)) return false; return true; }
        private static long Align(long x, long a) { return (x + a - 1) / a * a; }

        // ---- User file type signatures (user_instance_table / user_type_crcs / upgrade_user_crcs)
        private static bool InstanceTable(byte[] b, out long io, out uint ic)
        {
            io = 0; ic = 0;
            if (b.Length < 48 || !Magic(b, "USR")) return false;
            ulong r = U64(b, 32);
            if (r > (ulong)b.Length || (long)r + 48 > b.Length) return false;
            long R = (long)r;
            if (b[R] != 'R' || b[R + 1] != 'S' || b[R + 2] != 'Z' || b[R + 3] != 0) return false;
            ic = U32(b, R + 12);
            ulong off = U64(b, R + 24);
            if (off > (ulong)b.Length) return false;
            io = R + (long)off;
            if (io + 8L * ic > b.Length) return false;
            return io != 0;
        }

        internal static bool UserTypeCrcs(byte[] b, Dictionary<uint, uint> output)
        {
            long io; uint ic;
            if (!InstanceTable(b, out io, out ic)) return false;
            for (uint i = 1; i < ic; i++) output[U32(b, io + 8L * i)] = U32(b, io + 8L * i + 4);
            return true;
        }

        internal static int UpgradeUserCrcs(byte[] b, Dictionary<uint, uint> current, HashSet<uint> upgradable, HashSet<uint> staleLeft)
        {
            long io; uint ic;
            if (!InstanceTable(b, out io, out ic)) return 0;
            int n = 0;
            for (uint i = 1; i < ic; i++)
            {
                uint type = U32(b, io + 8L * i), crc = U32(b, io + 8L * i + 4), want;
                if (!current.TryGetValue(type, out want) || want == crc) continue;
                if (upgradable.Contains(type)) { W32(b, io + 8L * i + 4, want); n++; }
                else if (staleLeft != null) staleLeft.Add(type);
            }
            return n;
        }

        // ---- Costume colour files (cmd_parse / cmd_write / upgrade_costume_material_layout)
        private static readonly Dictionary<uint, string> CmdTypes = new Dictionary<uint, string>
        {
            {0x6785d3d6, "O"}, {0x9e32f1f4, "wO"}, {0xd01d4c14, "sOoo"}, {0x123a0e06, "bwo"}, {0xac2691c9, "ooo"},
            {0xe6b613fb, "ooo"}, {0x9f67268b, "oooo"}, {0x739a9342, "ooooo"}, {0x87bc25ed, "oo"}, {0x7fb59d14, "ooooooooo"},
            {0x77577f78, "bw"}, {0x016461b0, "bw"}, {0x2c5538dd, "bw"}, {0x982fbf08, "bw"}, {0xb7cfcbed, "bw"},
            {0xf3356698, "bw"}, {0xf4f30deb, "bw"}, {0x9d1c5550, "bwwww"}
        };
        private const uint TCloth = 0x9f67268b, TShellFur = 0x739a9342, ClothCrc2023 = 3731713379u;
        private const int FurBlock = 6;

        private sealed class CmdField { internal char Kind; internal byte[] Raw = new byte[0]; internal List<uint> Refs = new List<uint>(); internal CmdField Clone() { return new CmdField { Kind = Kind, Raw = (byte[])Raw.Clone(), Refs = new List<uint>(Refs) }; } }
        private sealed class CmdInst { internal uint Type, Crc; internal List<CmdField> Fields = new List<CmdField>(); internal CmdInst Clone() { return new CmdInst { Type = Type, Crc = Crc, Fields = Fields.Select(f => f.Clone()).ToList() }; } }
        private sealed class CmdDoc { internal byte[] Head; internal long R; internal List<uint> Objects = new List<uint>(); internal List<CmdInst> Inst = new List<CmdInst>(); }

        private static string CmdLayout(uint type, uint crc)
        {
            if (type == TCloth && crc == ClothCrc2023) return "ooo";
            string f; return CmdTypes.TryGetValue(type, out f) ? f : null;
        }

        private static CmdDoc CmdParse(byte[] b)
        {
            if (b.Length < 48 || !Magic(b, "USR")) return null;
            ulong rr = U64(b, 32); if (rr + 48 > (ulong)b.Length) return null;
            long R = (long)rr;
            if (b[R] != 'R' || b[R + 1] != 'S' || b[R + 2] != 'Z' || b[R + 3] != 0) return null;
            uint oc = U32(b, R + 8), ic = U32(b, R + 12), udc = U32(b, R + 16);
            ulong instOff = U64(b, R + 24), dataOff = U64(b, R + 32);
            if (udc != 0 || dataOff != U64(b, R + 40) || instOff != 48UL + 4UL * oc) return null;
            if (ic == 0 || (ulong)R + instOff + 8UL * ic > (ulong)b.Length || (ulong)R + dataOff > (ulong)b.Length) return null;
            var doc = new CmdDoc { R = R, Head = b.Take((int)R + 48).ToArray() };
            for (uint i = 0; i < oc; i++) doc.Objects.Add(U32(b, R + 48 + 4L * i));
            for (uint i = 0; i < ic; i++) doc.Inst.Add(new CmdInst { Type = U32(b, R + (long)instOff + 8L * i), Crc = U32(b, R + (long)instOff + 8L * i + 4) });
            long D = R + (long)dataOff, pos = 0;
            Func<long, bool> need = delegate(long n) { return n >= 0 && D + pos + n <= b.Length; };
            for (int i = 1; i < ic; i++)
            {
                string lay = CmdLayout(doc.Inst[i].Type, doc.Inst[i].Crc);
                if (lay == null) return null;
                foreach (char k in lay)
                {
                    var f = new CmdField { Kind = k };
                    if (k == 'b')
                    {
                        if (!need(1)) return null;
                        f.Raw = new[] { b[D + pos] }; pos += 1;
                    }
                    else
                    {
                        pos = Align(pos, 4);
                        if (!need(4)) return null;
                        uint v = U32(b, D + pos); pos += 4;
                        if (k == 'w') f.Raw = BitConverter.GetBytes(v);
                        else if (k == 'o') f.Refs.Add(v);
                        else if (k == 'O')
                        {
                            if (!need(4L * v)) return null;
                            for (uint e = 0; e < v; e++) { f.Refs.Add(U32(b, D + pos)); pos += 4; }
                        }
                        else if (k == 's')
                        {
                            if (!need(2L * v)) return null;
                            f.Raw = new byte[2L * v]; Buffer.BlockCopy(b, (int)(D + pos), f.Raw, 0, (int)(2L * v)); pos += 2L * v;
                        }
                        else return null;
                    }
                    doc.Inst[i].Fields.Add(f);
                }
            }
            return D + pos == b.Length ? doc : null;
        }

        private static byte[] CmdWrite(CmdDoc doc, Dictionary<uint, uint> current)
        {
            long R = doc.R; int ic = doc.Inst.Count, oc = doc.Objects.Count;
            long instOff = 48 + 4L * oc, dataOff = Align(instOff + 8L * ic, 16);
            var output = new List<byte>(doc.Head);
            byte[] head = output.ToArray();
            W32(head, R + 12, (uint)ic); W64(head, R + 32, (ulong)dataOff); W64(head, R + 40, (ulong)dataOff);
            output = new List<byte>(head);
            foreach (uint o in doc.Objects) output.AddRange(BitConverter.GetBytes(o));
            foreach (CmdInst inst in doc.Inst)
            {
                uint crc = inst.Crc, want;
                if (current != null && inst.Type != 0 && current.TryGetValue(inst.Type, out want)) crc = want;
                output.AddRange(BitConverter.GetBytes(inst.Type)); output.AddRange(BitConverter.GetBytes(crc));
            }
            while (output.Count < R + dataOff) output.Add(0);
            var d = new List<byte>();
            Action pad4 = delegate { while (d.Count % 4 != 0) d.Add(0); };
            for (int i = 1; i < ic; i++)
                foreach (CmdField f in doc.Inst[i].Fields)
                {
                    if (f.Kind == 'b') { d.Add(f.Raw[0]); continue; }
                    pad4();
                    if (f.Kind == 'w') d.AddRange(f.Raw);
                    else if (f.Kind == 'o') d.AddRange(BitConverter.GetBytes(f.Refs[0]));
                    else if (f.Kind == 'O') { d.AddRange(BitConverter.GetBytes((uint)f.Refs.Count)); foreach (uint r in f.Refs) d.AddRange(BitConverter.GetBytes(r)); }
                    else if (f.Kind == 's') { d.AddRange(BitConverter.GetBytes((uint)(f.Raw.Length / 2))); d.AddRange(f.Raw); }
                }
            output.AddRange(d);
            return output.ToArray();
        }

        internal static bool CostumeMaterialRoundtrip(byte[] data, out byte[] output)
        {
            CmdDoc doc = CmdParse(data); output = doc == null ? null : CmdWrite(doc, null);
            return doc != null;
        }

        internal static bool UpgradeCostumeMaterialLayout(ref byte[] data, Dictionary<uint, uint> current, byte[] reference, out int garments)
        {
            garments = 0;
            CmdDoc doc = CmdParse(data);
            if (doc == null) return false;
            Func<CmdInst, bool> oldCloth = delegate(CmdInst inst) { return inst.Type == TCloth && inst.Fields.Count == 3; };
            int nOld = doc.Inst.Count(oldCloth);
            if (nOld == 0 || reference == null) return false;
            CmdDoc reff = CmdParse(reference);
            if (reff == null) return false;
            CmdInst fur = reff.Inst.FirstOrDefault(i => i.Type == TShellFur && i.Fields.Count == 5);
            if (fur == null) return false;
            var furValues = new List<CmdInst>();
            foreach (CmdField f in fur.Fields)
            {
                if (f.Refs.Count != 1 || f.Refs[0] == 0 || f.Refs[0] >= reff.Inst.Count) return false;
                CmdInst v = reff.Inst[(int)f.Refs[0]];
                if (v.Fields.Any(vf => vf.Refs.Count > 0)) return false;
                furValues.Add(v);
            }
            int ic = doc.Inst.Count;
            var remap = new uint[ic]; int shift = 0;
            for (int i = 0; i < ic; i++) { if (oldCloth(doc.Inst[i])) shift += FurBlock; remap[i] = (uint)(i + shift); }
            Func<uint, uint> mapRef = delegate(uint r) { return r < ic ? remap[r] : uint.MaxValue; };
            var output = new CmdDoc { Head = doc.Head, R = doc.R };
            foreach (uint o in doc.Objects) { uint m = mapRef(o); if (m == uint.MaxValue) return false; output.Objects.Add(m); }
            for (int i = 0; i < ic; i++)
            {
                CmdInst inst = doc.Inst[i].Clone();
                foreach (CmdField f in inst.Fields)
                    for (int k = 0; k < f.Refs.Count; k++) { uint m = mapRef(f.Refs[k]); if (m == uint.MaxValue) return false; f.Refs[k] = m; }
                if (oldCloth(doc.Inst[i]))
                {
                    uint first = remap[i] - FurBlock;
                    foreach (CmdInst v in furValues) output.Inst.Add(v.Clone());
                    CmdInst sf = fur.Clone();
                    for (int k = 0; k < 5; k++) sf.Fields[k].Refs[0] = first + (uint)k;
                    output.Inst.Add(sf);
                    var ff = new CmdField { Kind = 'o' }; ff.Refs.Add(first + 5);
                    inst.Fields.Add(ff);
                }
                output.Inst.Add(inst);
            }
            data = CmdWrite(output, current);
            garments = nOld;
            return true;
        }

        // ---- Texture mip tables (tex_unit / texture_mip_check)
        private static int TexUnit(int format, out bool block)
        {
            block = true;
            switch (format)
            {
                case 70: case 71: case 72: case 79: case 80: case 81: return 8;
                case 73: case 74: case 75: case 76: case 77: case 78: case 82: case 83: case 84:
                case 94: case 95: case 96: case 97: case 98: case 99: return 16;
            }
            block = false;
            switch (format)
            {
                case 2: return 16;
                case 10: return 8;
                case 24: case 26: case 28: case 29: case 87: case 88: case 91: return 4;
                case 49: case 56: return 2;
                case 61: return 1;
            }
            return 0;
        }

        internal static int TextureMipCheck(byte[] b, bool repair, out int repaired) { return TextureMipCheck(b, repair, out repaired, b.Length); }
        // 1.0.1: `b` may be just the header (TextureFileOk); `length` is the whole file's length.
        internal static int TextureMipCheck(byte[] b, bool repair, out int repaired, long length)
        {
            repaired = 0;
            if (b.Length < 40 || !Magic(b, "TEX")) return 0;
            int w = BitConverter.ToUInt16(b, 8), h = BitConverter.ToUInt16(b, 10);
            int n = b[15] / 16, images = b[14] != 0 ? b[14] : 1;
            bool block; int unit = TexUnit(I32(b, 16), out block);
            if (unit == 0 || n <= 0 || 40L + 16L * n * images > Math.Min(b.Length, length)) return 0;
            int bad = 0;
            for (int img = 0; img < images; img++)
                for (int i = 0; i < n; i++)
                {
                    long at = 40 + 16L * ((long)img * n + i);
                    uint mw = (uint)Math.Max(1, w >> i), mh = (uint)Math.Max(1, h >> i);
                    uint row = block ? ((mw + 3) / 4) * (uint)unit : mw * (uint)unit;
                    uint rows = block ? (mh + 3) / 4 : mh;
                    int pitch = I32(b, at + 8), size = I32(b, at + 12);
                    if (pitch >= (int)row && size == unchecked(pitch * (int)rows)) continue;
                    if (repair && size == unchecked((int)(row * rows))) { WI32(b, at + 8, (int)row); repaired++; continue; }
                    bad++;
                }
            if (bad == 0 || !repair || images != 1) return bad;
            ulong off = U64(b, 40);
            for (int i = 0; i < n; i++)
            {
                uint mw = (uint)Math.Max(1, w >> i), mh = (uint)Math.Max(1, h >> i);
                ulong row = block ? ((mw + 3) / 4) * (ulong)unit : (ulong)mw * (ulong)unit;
                off += row * (block ? (mh + 3) / 4 : mh);
            }
            if (off != (ulong)length) return bad;
            off = U64(b, 40);
            for (int i = 0; i < n; i++)
            {
                long at = 40 + 16L * i;
                uint mw = (uint)Math.Max(1, w >> i), mh = (uint)Math.Max(1, h >> i);
                uint row = block ? ((mw + 3) / 4) * (uint)unit : mw * (uint)unit;
                uint size = row * (block ? (mh + 3) / 4 : mh);
                W64(b, at, off); WI32(b, at + 8, (int)row); WI32(b, at + 12, (int)size);
                off += size;
            }
            repaired += bad;
            return 0;
        }

        // ---- Select-screen colour swatches (2.5.5)
        // gui/data/selectfighteruidata/data/<fighter>/selectfighteruidata<fighter>_<outfit>.user.2: one row per colour
        // ID (ID + two 4-byte swatch colours), then the outfit number, then an ID list. A game update changed the root
        // type's signature (02CC614B -> 30F82F2F) and added a 4-byte field after the outfit number, so a mod's older
        // file no longer matches the game. The repair takes the game's current file and copies the mod's two colours
        // for each of its colour IDs into it (the outfit's other current rows stay). The same as the one-off
        // Leon-on-Luke repair script. Any other layout is left as it is.
        internal static readonly Regex SwatchPath = new Regex(@"^natives/stm/product/gui/data/selectfighteruidata/data/\d{3}/selectfighteruidata\d{3}_\d{2}\.user\.\d+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        internal const uint SwatchRootType = 0xA79B54DB, SwatchRowType = 0x5ACE8765, SwatchRowCrc = 0x676CF10B, SwatchCrcOld = 0x02CC614B, SwatchCrcCurrent = 0x30F82F2F;
        // Root signature -> bytes between the outfit number and the ID list.
        private static readonly Dictionary<uint, int> SwatchTail = new Dictionary<uint, int> { { SwatchCrcOld, 4 }, { SwatchCrcCurrent, 8 } };
        private sealed class SwatchDoc { internal uint Crc, Costume; internal readonly Dictionary<uint, long> Rows = new Dictionary<uint, long>(); }

        private static SwatchDoc SwatchParse(byte[] b)
        {
            try
            {
                if (b == null || b.Length < 48 || !Magic(b, "USR")) return null;
                ulong rr = U64(b, 32); if (rr + 48 > (ulong)b.Length) return null;
                long r = (long)rr;
                if (b[r] != 'R' || b[r + 1] != 'S' || b[r + 2] != 'Z' || b[r + 3] != 0) return null;
                uint oc = U32(b, r + 8), ic = U32(b, r + 12), uc = U32(b, r + 16);
                ulong io = U64(b, r + 24), dataOff = U64(b, r + 32);
                if (oc != 1 || uc != 0 || ic < 3 || ic > 4096 || (ulong)r + io + 8UL * ic > (ulong)b.Length) return null;
                long types = r + (long)io;
                if (U32(b, types) != 0 || U32(b, types + 4) != 0) return null;
                for (uint i = 1; i + 1 < ic; i++) if (U32(b, types + 8L * i) != SwatchRowType || U32(b, types + 8L * i + 4) != SwatchRowCrc) return null;
                var doc = new SwatchDoc { Crc = U32(b, types + 8L * (ic - 1) + 4) };
                int tailBytes;
                if (U32(b, types + 8L * (ic - 1)) != SwatchRootType || !SwatchTail.TryGetValue(doc.Crc, out tailBytes)) return null;
                int n = (int)ic - 2; long start = r + (long)dataOff, tail = start + 12L * n, array = tail + tailBytes;
                if (array + 4 + 4L * n != b.Length || U32(b, array) != (uint)n) return null;
                for (int i = 0; i < n; i++) if (U32(b, array + 4 + 4L * i) != (uint)(i + 1)) return null;
                doc.Costume = U32(b, tail);
                for (int i = 0; i < n; i++)
                {
                    long pos = start + 12L * i; uint id = U32(b, pos);
                    if (doc.Rows.ContainsKey(id)) return null;
                    doc.Rows[id] = pos;
                }
                return doc;
            }
            catch (ArgumentException) { return null; }
        }

        // null: not a swatch file the repair knows, or nothing to do. `current` is the game's own file at the same path.
        internal static FixResult UpgradeSwatches(ref byte[] data, byte[] current)
        {
            SwatchDoc mod = SwatchParse(data);
            if (mod == null || mod.Crc == SwatchCrcCurrent) return null;
            SwatchDoc game = SwatchParse(current);
            if (game == null || game.Crc != SwatchCrcCurrent || game.Costume != mod.Costume || mod.Rows.Keys.Any(id => !game.Rows.ContainsKey(id)))
                return new FixResult { Kind = "swatch-stale", Warning = true, Message = "Outdated colour swatch file left as it is (the game's current file " + (game == null ? "couldn't be read" : "doesn't match it") + "); select-screen colours may be wrong" };
            byte[] repaired = (byte[])current.Clone();
            foreach (var row in mod.Rows) Buffer.BlockCopy(data, (int)row.Value + 4, repaired, (int)game.Rows[row.Key] + 4, 8);
            data = repaired;
            return new FixResult { Kind = "swatch", Count = mod.Rows.Count, Changed = true, Message = "Outdated colour swatch file rebuilt for the current game (" + mod.Rows.Count + " colour" + (mod.Rows.Count == 1 ? "" : "s") + " kept)" };
        }

        // SlotsLoader's upgrade_mod_user: textures get the mip-table check/repair; user files the
        // 2023 layout conversion, otherwise the allow-listed CRC upgrade (others reported stale).
        // 2.5.5: with its path, a select-screen swatch file gets the swatch repair instead.
        // 1.0.1 (speed): a texture's fix only looks at its mip table (at most ~60 KB at the start). When that table
        // needs no repair, the file is never read in full: true, with any "broken mip table" warning in `results`.
        // False means "use the full path" (not a texture, unreadable, or a repair to write).
        internal const int TextureHeaderBytes = 64 * 1024;
        internal static bool TextureFileOk(string file, out List<FixResult> results)
        {
            results = new List<FixResult>();
            try
            {
                byte[] head; long length;
                using (var s = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 4096))
                {
                    length = s.Length; head = new byte[(int)Math.Min(length, TextureHeaderBytes)];
                    int got = 0; while (got < head.Length) { int n = s.Read(head, got, head.Length - got); if (n <= 0) break; got += n; }
                    if (got != head.Length) return false;
                }
                if (!Magic(head, "TEX")) return false;
                int repaired; int bad = TextureMipCheck(head, true, out repaired, length);
                if (repaired > 0) return false;   // the full path writes the repair
                if (bad > 0) results.Add(new FixResult { Kind = "badmip", Count = bad, Warning = true, Message = "Broken texture mip table (" + bad + " level" + (bad == 1 ? "" : "s") + "); could not be repaired, may not load" });
                return true;
            }
            catch (IOException) { return false; }
        }
        internal static List<FixResult> UpgradeModFile(ref byte[] data, GameReference reference) { return UpgradeModFile(ref data, reference, null); }
        internal static List<FixResult> UpgradeModFile(ref byte[] data, GameReference reference, string path)
        {
            var results = new List<FixResult>();
            // 1.0.5: swap repairs only when Settings turns them on (GameReference.SwapRepairs); off, scenes are untouched.
            bool swaps = reference != null && reference.SwapRepairs;
            if (path != null && path.EndsWith(".scn.20", StringComparison.OrdinalIgnoreCase))
            {
                FixResult scene = swaps ? SwapRepairs.RepairScene(ref data, reference, ModFixes.NormalizeNativePath(path)) : null;
                if (scene != null) results.Add(scene);
                return results;
            }
            FixResult swap = path == null || !swaps ? null : SwapRepairs.RepairUser(ref data, reference, path);
            if (swap != null) { results.Add(swap); return results; }
            if (path != null && reference != null && Magic(data, "USR") && SwatchPath.IsMatch(ModFixes.NormalizeNativePath(path)))
            {
                FixResult swatch = UpgradeSwatches(ref data, reference.ReadGameFile(ModFixes.NormalizeNativePath(path)));
                if (swatch != null) results.Add(swatch);
                return results;
            }
            if (Magic(data, "TEX"))
            {
                int fixedLevels; int bad = TextureMipCheck(data, true, out fixedLevels);
                if (fixedLevels > 0) results.Add(new FixResult { Kind = "mip", Count = fixedLevels, Changed = true, Message = "Texture mip table repaired (" + fixedLevels + " level" + (fixedLevels == 1 ? "" : "s") + " with a wrong row pitch)" });
                if (bad > 0) results.Add(new FixResult { Kind = "badmip", Count = bad, Warning = true, Message = "Broken texture mip table (" + bad + " level" + (bad == 1 ? "" : "s") + "); could not be repaired, may not load" });
                return results;
            }
            if (reference == null || reference.CurrentCrcs.Count == 0 || !Magic(data, "USR")) return results;
            int garments;
            byte[] converted = data;
            if (UpgradeCostumeMaterialLayout(ref converted, reference.CurrentCrcs, reference.ReferenceCmd, out garments))
            {
                data = converted;
                results.Add(new FixResult { Kind = "layout", Count = garments, Changed = true, Message = "2023 colour file converted to the current layout (" + garments + " garment" + (garments == 1 ? "" : "s") + ", disabled fur block added)" });
                return results;
            }
            var stale = new HashSet<uint>();
            int n = UpgradeUserCrcs(data, reference.CurrentCrcs, CrcUpgradable, stale);
            if (n > 0) results.Add(new FixResult { Kind = "crc", Count = n, Changed = true, Message = "Outdated colour type signature updated (" + n + " instance" + (n == 1 ? "" : "s") + " of " + TypeLabel(0x9E32F1F4) + ")" });
            foreach (uint t in stale.OrderBy(x => x)) results.Add(new FixResult { Kind = "stale", Warning = true, Message = "Older layout of " + TypeLabel(t) + " left as it is; its colours may not load" });
            return results;
        }

        // ".tex.143230113" -> ".tex.<current>"; returns null when no rename applies.
        internal static string UpgradeTexSuffix(string path, string texVersion)
        {
            if (String.IsNullOrEmpty(texVersion)) return null;
            foreach (string old in OldTexSuffixes)
            {
                string suffix = ".tex." + old;
                if (old != texVersion && path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return path.Substring(0, path.Length - old.Length) + texVersion;
            }
            return null;
        }

        // natives/STM/Product/... -> natives/stm/product/... (lower case, forward slashes).
        internal static string NormalizeNativePath(string path)
        {
            string p = PakHash.Normalize(path).TrimStart('/');
            return p.ToLowerInvariant();
        }
    }

    // Facts read from the installed game: the current type signatures of the
    // colour-file types (from a vanilla colour file in the base PAK), the
    // current resource suffixes and the vanilla costume folders.
    internal sealed class GameReference
    {
        internal Dictionary<uint, uint> CurrentCrcs = new Dictionary<uint, uint>();
        internal byte[] ReferenceCmd;
        internal Dictionary<string, string> Suffixes = new Dictionary<string, string>(GameTables.Suffixes, StringComparer.OrdinalIgnoreCase);
        internal Dictionary<string, int> CostumeByFolder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        internal string SuffixSource = "built-in table (October 2026)";
        internal string CrcSource = "unavailable";
        internal string OfficialStamp = "";   // scene/physics recipes depend on files beyond the reference CMD
        internal bool SwapRepairs;            // 1.0.5: ReplaceSettings.SwapRepairs (off by default)
        internal string TexVersion { get { string v; return Suffixes.TryGetValue("tex", out v) ? v : "241101895"; } }
        // 2.5.5: a game file by its natives path (base PAK, then DLC), for the swatch repair; null when unreadable.
        internal Func<string, byte[]> GameFile;
        private readonly Dictionary<string, byte[]> gameFiles = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        internal byte[] ReadGameFile(string path)
        {
            if (GameFile == null || String.IsNullOrEmpty(path)) return null;
            lock (gameFiles)
            {
                byte[] data;
                if (!gameFiles.TryGetValue(path, out data))
                {
                    try { data = GameFile(path); } catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); data = null; }
                    gameFiles[path] = data;
                }
                return data;
            }
        }
        internal static Func<string, byte[]> PakReader(PakIndex basePak, IList<PakIndex> dlc)
        {
            return delegate(string path)
            {
                byte[] found = basePak == null ? null : basePak.ReadByPath(path);
                if (found == null && dlc != null) foreach (PakIndex p in dlc) { found = p.ReadByPath(path); if (found != null) break; }
                return found;
            };
        }

        internal static GameReference FromCmd(byte[] referenceCmd)
        {
            var r = new GameReference(); r.LoadTables(null);
            if (referenceCmd != null && ModFixes.UserTypeCrcs(referenceCmd, r.CurrentCrcs)) { r.ReferenceCmd = referenceCmd; r.CrcSource = "reference colour file"; }
            return r;
        }

        // Outfit numbers and suffixes only (no PAK read): for labels.
        internal static GameReference Tables(string gameRoot) { var r = new GameReference(); r.LoadTables(gameRoot); return r; }

        internal static GameReference Load(string gameRoot, PakIndex basePak, IList<PakIndex> dlc)
        {
            var r = new GameReference();
            var official = new List<PakIndex>(); if (basePak != null) official.Add(basePak); if (dlc != null) official.AddRange(dlc);
            r.OfficialStamp = String.Join(";", official.Select(p => p.Path + "|" + p.Length + "|" + p.LastWriteUtc.Ticks + "|" + p.Fingerprint.ToString("X8")));
            r.LoadTables(gameRoot);
            r.GameFile = PakReader(basePak, dlc);   // 2.5.5: the swatch repair
            try
            {
                byte[] cmd = null;
                if (basePak != null) cmd = basePak.ReadByPath(ModFixes.ReferenceCmdPath);
                if (cmd == null && dlc != null) foreach (PakIndex p in dlc) { cmd = p.ReadByPath(ModFixes.ReferenceCmdPath); if (cmd != null) break; }
                if (cmd != null && ModFixes.UserTypeCrcs(cmd, r.CurrentCrcs)) { r.ReferenceCmd = cmd; r.CrcSource = "vanilla " + Path.GetFileName(ModFixes.ReferenceCmdPath); }
            }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; r.CrcSource = "unavailable: " + ex.Message; }
            return r;
        }

        private void LoadTables(string gameRoot)
        {
            foreach (string pair in GameTables.CostumeFolders.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = pair.IndexOf('='); CostumeByFolder[pair.Substring(0, eq)] = Int32.Parse(pair.Substring(eq + 1));
            }
            if (String.IsNullOrEmpty(gameRoot)) return;
            string tsv = Path.Combine(gameRoot, "reframework", "data", "SF6_Costumes_Data", "loader", "vanilla_costume_index.tsv");
            try
            {
                if (!File.Exists(tsv)) return;
                string section = ""; var folders = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase); var suffixes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string line in File.ReadLines(tsv))
                {
                    if (line.StartsWith("#")) { section = line; if (section.StartsWith("#FILES")) break; continue; }
                    string[] f = line.Split('\t');
                    if (section.StartsWith("#SUFFIXES") && f.Length >= 2) suffixes[f[0]] = f[1].Trim();
                    else if (section.StartsWith("#COSTUMES") && f.Length >= 4 && f[3].Length >= 10)
                    {
                        int no; if (!Int32.TryParse(f[2], out no)) continue;
                        string key = f[3].Substring(f[3].Length - 10); int old;
                        if (!folders.TryGetValue(key, out old) || no < old) folders[key] = no;
                    }
                }
                if (suffixes.Count > 0) { Suffixes = suffixes; SuffixSource = "Slotsloader vanilla index"; }
                foreach (var kv in folders) CostumeByFolder[kv.Key] = kv.Value;
            }
            catch (Exception ex) { if (ex is OutOfMemoryException) throw; Program.Log(ex); }
        }

        // "Cammy Outfit 2 (esf009/002)" for a model folder, or null.
        internal string DescribeFolder(string fighterCode, string folder)
        {
            string name; Characters.ByCode.TryGetValue(fighterCode, out name);
            int costume; string key = "esf" + fighterCode + "/" + folder;
            string who = name ?? ("esf" + fighterCode);
            if (folder == "000") return who + " shared parts (esf" + fighterCode + "/000)";
            if (CostumeByFolder.TryGetValue(key, out costume)) return who + " Outfit " + (costume + 1) + " (" + key + ")";
            return who + " costume folder " + folder + " (" + key + ", not a vanilla outfit)";
        }
    }
}
