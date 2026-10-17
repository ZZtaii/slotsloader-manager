using System;
using System.IO;

namespace SlotsInstaller
{
    // Managed Zstandard decoder (RFC 8878), enough to read the game's compressed
    // PAK entries (vanilla reference files). No dictionaries; checksums ignored.
    // Validated against the reference zstddeclib on real base-PAK entries.
    internal static class Zstd
    {
        private const uint FrameMagic = 0xFD2FB528;
        private static readonly int[] LlBase = { 0,1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16,18,20,22,24,28,32,40,48,64,128,256,512,1024,2048,4096,8192,16384,32768,65536 };
        private static readonly int[] LlBits = { 0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,1,1,1,1,2,2,3,3,4,6,7,8,9,10,11,12,13,14,15,16 };
        private static readonly int[] MlBase = { 3,4,5,6,7,8,9,10,11,12,13,14,15,16,17,18,19,20,21,22,23,24,25,26,27,28,29,30,31,32,33,34,35,37,39,41,43,47,51,59,67,83,99,131,259,515,1027,2051,4099,8195,16387,32771,65539 };
        private static readonly int[] MlBits = { 0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,1,1,1,1,2,2,3,3,4,4,5,7,8,9,10,11,12,13,14,15,16 };
        private static readonly short[] LlDefault = { 4,3,2,2,2,2,2,2,2,2,2,2,2,1,1,1,2,2,2,2,2,2,2,2,2,3,2,1,1,1,1,1,-1,-1,-1,-1 };
        private static readonly short[] MlDefault = { 1,4,3,2,2,2,2,2,2,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,-1,-1,-1,-1,-1,-1,-1 };
        private static readonly short[] OfDefault = { 1,1,1,1,1,1,2,2,2,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,-1,-1,-1,-1,-1 };

        private sealed class FseTable { internal int Log; internal byte[] Symbol; internal byte[] Bits; internal int[] Base; }
        private sealed class HufTable { internal int MaxBits; internal byte[] Symbol; internal byte[] Bits; }
        private sealed class FrameState
        {
            internal int[] Rep = { 1, 4, 8 };
            internal HufTable Huffman;
            internal FseTable Ll, Of, Ml;
            internal int FrameStart;
        }

        internal static byte[] Decompress(byte[] source, long expectedSize)
        {
            return Decompress(source, 0, source.Length, expectedSize);
        }

        internal static byte[] Decompress(byte[] src, int offset, int length, long expectedSize)
        {
            if (expectedSize < 0 || expectedSize > Int32.MaxValue - 64) throw new InvalidDataException("Unsupported zstd output size.");
            var output = new byte[expectedSize];
            int outPos = 0, pos = offset, end = offset + length;
            bool any = false;
            while (pos < end)
            {
                if (end - pos < 4) throw new InvalidDataException("Truncated zstd frame.");
                uint magic = U32(src, pos);
                if ((magic & 0xFFFFFFF0u) == 0x184D2A50u)
                {
                    if (end - pos < 8) throw new InvalidDataException("Truncated skippable frame.");
                    long skip = U32(src, pos + 4);
                    if (skip > end - pos - 8) throw new InvalidDataException("Truncated skippable frame.");
                    pos += 8 + (int)skip; continue;
                }
                if (magic != FrameMagic) throw new InvalidDataException("Not a zstd frame.");
                pos = DecodeFrame(src, pos + 4, end, output, ref outPos);
                any = true;
            }
            if (!any || outPos != expectedSize) throw new InvalidDataException("zstd output size mismatch (" + outPos + " of " + expectedSize + ").");
            return output;
        }

        private static uint U32(byte[] d, int p) { return (uint)(d[p] | (d[p + 1] << 8) | (d[p + 2] << 16) | (d[p + 3] << 24)); }
        private static void Need(int pos, int count, int end) { if (count < 0 || pos > end - count) throw new InvalidDataException("Truncated zstd data."); }
        private static int HighBit(uint v) { int n = -1; while (v != 0) { v >>= 1; n++; } return n; }

        private static int DecodeFrame(byte[] src, int pos, int end, byte[] output, ref int outPos)
        {
            Need(pos, 1, end);
            int fhd = src[pos++];
            if ((fhd & 0x08) != 0) throw new InvalidDataException("Reserved zstd frame bit set.");
            int fcsFlag = fhd >> 6; bool single = (fhd & 0x20) != 0; bool checksum = (fhd & 0x04) != 0; int dictFlag = fhd & 3;
            if (!single) { Need(pos, 1, end); pos++; }
            int dictBytes = dictFlag == 0 ? 0 : dictFlag == 1 ? 1 : dictFlag == 2 ? 2 : 4;
            Need(pos, dictBytes, end);
            long dictId = 0; for (int i = 0; i < dictBytes; i++) dictId |= (long)src[pos + i] << (8 * i);
            if (dictId != 0) throw new InvalidDataException("zstd dictionaries are not supported.");
            pos += dictBytes;
            int fcsBytes = fcsFlag == 0 ? (single ? 1 : 0) : fcsFlag == 1 ? 2 : fcsFlag == 2 ? 4 : 8;
            Need(pos, fcsBytes, end); pos += fcsBytes;
            var state = new FrameState { FrameStart = outPos };
            while (true)
            {
                Need(pos, 3, end);
                int header = src[pos] | (src[pos + 1] << 8) | (src[pos + 2] << 16); pos += 3;
                bool last = (header & 1) != 0; int type = (header >> 1) & 3; int size = header >> 3;
                if (type == 0)
                {
                    Need(pos, size, end); if (size > output.Length - outPos) throw new InvalidDataException("zstd output overflow.");
                    Buffer.BlockCopy(src, pos, output, outPos, size); outPos += size; pos += size;
                }
                else if (type == 1)
                {
                    Need(pos, 1, end); if (size > output.Length - outPos) throw new InvalidDataException("zstd output overflow.");
                    byte b = src[pos++]; for (int i = 0; i < size; i++) output[outPos++] = b;
                }
                else if (type == 2)
                {
                    Need(pos, size, end);
                    DecodeBlock(src, pos, pos + size, output, ref outPos, state);
                    pos += size;
                }
                else throw new InvalidDataException("Reserved zstd block type.");
                if (last) break;
            }
            if (checksum) { Need(pos, 4, end); pos += 4; }
            return pos;
        }

        private static void DecodeBlock(byte[] src, int pos, int blockEnd, byte[] output, ref int outPos, FrameState state)
        {
            // ---- Literals section
            Need(pos, 1, blockEnd);
            int litType = src[pos] & 3, sizeFormat = (src[pos] >> 2) & 3;
            byte[] literals; int litCount;
            if (litType == 0 || litType == 1)
            {
                int regen, hdr;
                if (sizeFormat == 0 || sizeFormat == 2) { regen = src[pos] >> 3; hdr = 1; }
                else if (sizeFormat == 1) { Need(pos, 2, blockEnd); regen = (src[pos] >> 4) + (src[pos + 1] << 4); hdr = 2; }
                else { Need(pos, 3, blockEnd); regen = (src[pos] >> 4) + (src[pos + 1] << 4) + (src[pos + 2] << 12); hdr = 3; }
                pos += hdr;
                literals = new byte[regen]; litCount = regen;
                if (litType == 0) { Need(pos, regen, blockEnd); Buffer.BlockCopy(src, pos, literals, 0, regen); pos += regen; }
                else { Need(pos, 1, blockEnd); byte b = src[pos++]; for (int i = 0; i < regen; i++) literals[i] = b; }
            }
            else
            {
                int regen, comp, hdr; bool four;
                if (sizeFormat <= 1)
                {
                    Need(pos, 3, blockEnd); uint v = (uint)(src[pos] | (src[pos + 1] << 8) | (src[pos + 2] << 16));
                    regen = (int)((v >> 4) & 0x3FF); comp = (int)((v >> 14) & 0x3FF); hdr = 3; four = sizeFormat == 1;
                }
                else if (sizeFormat == 2)
                {
                    Need(pos, 4, blockEnd); uint v = U32(src, pos);
                    regen = (int)((v >> 4) & 0x3FFF); comp = (int)((v >> 18) & 0x3FFF); hdr = 4; four = true;
                }
                else
                {
                    Need(pos, 5, blockEnd); ulong v = U32(src, pos) | ((ulong)src[pos + 4] << 32);
                    regen = (int)((v >> 4) & 0x3FFFF); comp = (int)((v >> 22) & 0x3FFFF); hdr = 5; four = true;
                }
                pos += hdr;
                Need(pos, comp, blockEnd);
                int streamsStart = pos, literalsEnd = pos + comp;
                if (litType == 2) { state.Huffman = ReadHuffman(src, ref streamsStart, literalsEnd); }
                else if (state.Huffman == null) throw new InvalidDataException("Treeless zstd literals without a previous table.");
                literals = new byte[regen]; litCount = regen;
                DecodeLiterals(src, streamsStart, literalsEnd, literals, four, state.Huffman);
                pos = literalsEnd;
            }

            // ---- Sequences section
            Need(pos, 1, blockEnd);
            int nbSeq; int b0 = src[pos++];
            if (b0 == 0) nbSeq = 0;
            else if (b0 < 128) nbSeq = b0;
            else if (b0 < 255) { Need(pos, 1, blockEnd); nbSeq = ((b0 - 128) << 8) + src[pos++]; }
            else { Need(pos, 2, blockEnd); nbSeq = src[pos] + (src[pos + 1] << 8) + 0x7F00; pos += 2; }
            if (nbSeq == 0)
            {
                CopyLiterals(literals, 0, litCount, output, ref outPos);
                return;
            }
            Need(pos, 1, blockEnd);
            int modes = src[pos++];
            if ((modes & 3) != 0) throw new InvalidDataException("Reserved zstd sequence mode bits.");
            state.Ll = ReadSequenceTable((modes >> 6) & 3, src, ref pos, blockEnd, state.Ll, LlDefault, 6, 35, 9);
            state.Of = ReadSequenceTable((modes >> 4) & 3, src, ref pos, blockEnd, state.Of, OfDefault, 5, 31, 8);
            state.Ml = ReadSequenceTable((modes >> 2) & 3, src, ref pos, blockEnd, state.Ml, MlDefault, 6, 52, 9);
            var bits = new BackwardBits(src, pos, blockEnd - pos);
            int llState = (int)bits.Read(state.Ll.Log), ofState = (int)bits.Read(state.Of.Log), mlState = (int)bits.Read(state.Ml.Log);
            int litPos = 0;
            int[] rep = state.Rep;
            for (int i = 0; i < nbSeq; i++)
            {
                int ofCode = state.Of.Symbol[ofState], llCode = state.Ll.Symbol[llState], mlCode = state.Ml.Symbol[mlState];
                if (ofCode > 31 || llCode >= LlBase.Length || mlCode >= MlBase.Length) throw new InvalidDataException("Invalid zstd sequence code.");
                long offsetValue = (1L << ofCode) + (long)bits.Read(ofCode);
                int ml = MlBase[mlCode] + (int)bits.Read(MlBits[mlCode]);
                int ll = LlBase[llCode] + (int)bits.Read(LlBits[llCode]);
                long offset;
                if (offsetValue > 3)
                {
                    offset = offsetValue - 3;
                    rep[2] = rep[1]; rep[1] = rep[0]; rep[0] = (int)offset;
                }
                else
                {
                    int index = (int)offsetValue - 1 + (ll == 0 ? 1 : 0);
                    if (index == 0) offset = rep[0];
                    else if (index == 3) { offset = rep[0] - 1; rep[2] = rep[1]; rep[1] = rep[0]; rep[0] = (int)offset; }
                    else if (index == 1) { offset = rep[1]; rep[1] = rep[0]; rep[0] = (int)offset; }
                    else { offset = rep[2]; rep[2] = rep[1]; rep[1] = rep[0]; rep[0] = (int)offset; }
                }
                if (ll > litCount - litPos) throw new InvalidDataException("zstd literal length exceeds literals.");
                CopyLiterals(literals, litPos, ll, output, ref outPos); litPos += ll;
                if (offset <= 0 || offset > outPos - state.FrameStart) throw new InvalidDataException("Invalid zstd match offset.");
                if (ml > output.Length - outPos) throw new InvalidDataException("zstd output overflow.");
                int from = outPos - (int)offset;
                for (int k = 0; k < ml; k++) output[outPos++] = output[from + k];
                if (i != nbSeq - 1)
                {
                    llState = state.Ll.Base[llState] + (int)bits.Read(state.Ll.Bits[llState]);
                    mlState = state.Ml.Base[mlState] + (int)bits.Read(state.Ml.Bits[mlState]);
                    ofState = state.Of.Base[ofState] + (int)bits.Read(state.Of.Bits[ofState]);
                }
            }
            if (bits.Position != 0) throw new InvalidDataException("zstd sequence bitstream not fully consumed.");
            CopyLiterals(literals, litPos, litCount - litPos, output, ref outPos);
        }

        private static void CopyLiterals(byte[] literals, int from, int count, byte[] output, ref int outPos)
        {
            if (count > output.Length - outPos) throw new InvalidDataException("zstd output overflow.");
            Buffer.BlockCopy(literals, from, output, outPos, count); outPos += count;
        }

        private static FseTable ReadSequenceTable(int mode, byte[] src, ref int pos, int end, FseTable previous, short[] defaults, int defaultLog, int maxSymbol, int maxLog)
        {
            if (mode == 0) return BuildFse(defaults, defaults.Length, defaultLog);
            if (mode == 1)
            {
                Need(pos, 1, end); int symbol = src[pos++];
                if (symbol > maxSymbol) throw new InvalidDataException("Invalid zstd RLE symbol.");
                return new FseTable { Log = 0, Symbol = new[] { (byte)symbol }, Bits = new byte[1], Base = new int[1] };
            }
            if (mode == 2)
            {
                int log; short[] norm; int count;
                pos += ReadNCount(src, pos, end, maxSymbol, maxLog, out norm, out count, out log);
                return BuildFse(norm, count, log);
            }
            if (previous == null) throw new InvalidDataException("Repeat zstd table without a previous table.");
            return previous;
        }

        // FSE normalized counts (forward bitstream). Returns bytes consumed.
        private static int ReadNCount(byte[] src, int pos, int end, int maxSymbol, int maxLog, out short[] norm, out int symbolCount, out int log)
        {
            var reader = new ForwardBits(src, pos, end);
            log = (int)reader.Read(4) + 5;
            if (log > maxLog) throw new InvalidDataException("zstd FSE accuracy log too large.");
            norm = new short[maxSymbol + 1];
            int remaining = (1 << log) + 1, threshold = 1 << log, nbBits = log + 1, symbol = 0;
            bool previousZero = false;
            while (remaining > 1 && symbol <= maxSymbol)
            {
                if (previousZero)
                {
                    int zeros = symbol;
                    while (true)
                    {
                        int repeat = (int)reader.Read(2);
                        zeros += repeat;
                        if (repeat != 3) break;
                    }
                    if (zeros > maxSymbol + 1) throw new InvalidDataException("zstd FSE zero run too long.");
                    while (symbol < zeros) norm[symbol++] = 0;
                    if (symbol > maxSymbol) break;
                }
                int max = (2 * threshold - 1) - remaining;
                int count;
                int low = (int)reader.Peek(nbBits - 1);
                if (low < max) { count = low; reader.Skip(nbBits - 1); }
                else
                {
                    count = (int)reader.Peek(nbBits);
                    if (count >= threshold) count -= max;
                    reader.Skip(nbBits);
                }
                count--;
                remaining -= count < 0 ? -count : count;
                norm[symbol++] = (short)count;
                previousZero = count == 0;
                while (remaining < threshold) { nbBits--; threshold >>= 1; }
            }
            if (remaining != 1) throw new InvalidDataException("Corrupt zstd FSE distribution.");
            symbolCount = symbol;
            int consumed = (int)((reader.Position + 7) >> 3);
            if (consumed > end - pos) throw new InvalidDataException("Truncated zstd FSE header.");
            return consumed;
        }

        private static FseTable BuildFse(short[] norm, int count, int log)
        {
            int size = 1 << log;
            var table = new FseTable { Log = log, Symbol = new byte[size], Bits = new byte[size], Base = new int[size] };
            var next = new int[count];
            int high = size - 1;
            for (int s = 0; s < count; s++)
            {
                if (norm[s] == -1) { table.Symbol[high--] = (byte)s; next[s] = 1; }
                else next[s] = norm[s];
            }
            int step = (size >> 1) + (size >> 3) + 3, mask = size - 1, position = 0;
            for (int s = 0; s < count; s++)
                for (int i = 0; i < norm[s]; i++)
                {
                    table.Symbol[position] = (byte)s;
                    do { position = (position + step) & mask; } while (position > high);
                }
            if (position != 0) throw new InvalidDataException("Corrupt zstd FSE table.");
            for (int u = 0; u < size; u++)
            {
                int s = table.Symbol[u];
                int x = next[s]++;
                int bits = log - HighBit((uint)x);
                table.Bits[u] = (byte)bits;
                table.Base[u] = (x << bits) - size;
            }
            return table;
        }

        private static HufTable ReadHuffman(byte[] src, ref int pos, int end)
        {
            Need(pos, 1, end);
            int header = src[pos++];
            var weights = new byte[256]; int count;
            if (header >= 128)
            {
                count = header - 127;
                int bytes = (count + 1) / 2; Need(pos, bytes, end);
                for (int i = 0; i < count; i++) weights[i] = (byte)(i % 2 == 0 ? src[pos + i / 2] >> 4 : src[pos + i / 2] & 15);
                pos += bytes;
            }
            else
            {
                Need(pos, header, end);
                int log; short[] norm; int symbols;
                int used = ReadNCount(src, pos, pos + header, 15, 6, out norm, out symbols, out log);
                FseTable table = BuildFse(norm, symbols, log);
                var bits = new BackwardBits(src, pos + used, header - used);
                int s1 = (int)bits.Read(log), s2 = (int)bits.Read(log);
                count = 0;
                while (true)
                {
                    if (count >= 255) throw new InvalidDataException("Too many zstd Huffman weights.");
                    weights[count++] = table.Symbol[s1]; s1 = table.Base[s1] + (int)bits.Read(table.Bits[s1]);
                    if (bits.Position < 0) { weights[count++] = table.Symbol[s2]; break; }
                    if (count >= 255) throw new InvalidDataException("Too many zstd Huffman weights.");
                    weights[count++] = table.Symbol[s2]; s2 = table.Base[s2] + (int)bits.Read(table.Bits[s2]);
                    if (bits.Position < 0) { weights[count++] = table.Symbol[s1]; break; }
                }
                pos += header;
            }
            long sum = 0;
            for (int i = 0; i < count; i++) { if (weights[i] > 12) throw new InvalidDataException("Invalid zstd Huffman weight."); if (weights[i] > 0) sum += 1L << (weights[i] - 1); }
            if (sum == 0) throw new InvalidDataException("Empty zstd Huffman table.");
            int maxBits = HighBit((uint)sum) + 1;
            long rest = (1L << maxBits) - sum;
            if (rest <= 0 || (rest & (rest - 1)) != 0) throw new InvalidDataException("Corrupt zstd Huffman weights.");
            weights[count] = (byte)(HighBit((uint)rest) + 1);
            int symbolsTotal = count + 1;
            if (maxBits > 11) throw new InvalidDataException("zstd Huffman table too deep.");
            var huf = new HufTable { MaxBits = maxBits, Symbol = new byte[1 << maxBits], Bits = new byte[1 << maxBits] };
            var rankCount = new int[13];
            for (int s = 0; s < symbolsTotal; s++) rankCount[weights[s]]++;
            var start = new int[14]; start[1] = 0;
            for (int w = 1; w <= 12; w++) start[w + 1] = start[w] + (rankCount[w] << (w - 1));
            for (int s = 0; s < symbolsTotal; s++)
            {
                int w = weights[s]; if (w == 0) continue;
                int length = 1 << (w - 1), at = start[w];
                if (at + length > huf.Symbol.Length) throw new InvalidDataException("Corrupt zstd Huffman table.");
                for (int k = 0; k < length; k++) { huf.Symbol[at + k] = (byte)s; huf.Bits[at + k] = (byte)(maxBits + 1 - w); }
                start[w] += length;
            }
            return huf;
        }

        private static void DecodeLiterals(byte[] src, int pos, int end, byte[] literals, bool four, HufTable huf)
        {
            if (!four) { DecodeStream(src, pos, end - pos, literals, 0, literals.Length, huf); return; }
            Need(pos, 6, end);
            int s1 = src[pos] | (src[pos + 1] << 8), s2 = src[pos + 2] | (src[pos + 3] << 8), s3 = src[pos + 4] | (src[pos + 5] << 8);
            int total = end - pos - 6, s4 = total - s1 - s2 - s3;
            if (s4 < 1) throw new InvalidDataException("Corrupt zstd literal jump table.");
            int segment = (literals.Length + 3) / 4, start = pos + 6;
            DecodeStream(src, start, s1, literals, 0, segment, huf); start += s1;
            DecodeStream(src, start, s2, literals, segment, segment, huf); start += s2;
            DecodeStream(src, start, s3, literals, 2 * segment, segment, huf); start += s3;
            int lastCount = literals.Length - 3 * segment;
            if (lastCount < 0) throw new InvalidDataException("Corrupt zstd literal segment.");
            DecodeStream(src, start, s4, literals, 3 * segment, lastCount, huf);
        }

        private static void DecodeStream(byte[] src, int pos, int length, byte[] output, int at, int count, HufTable huf)
        {
            if (count > output.Length - at) throw new InvalidDataException("Corrupt zstd literal segment.");
            var bits = new BackwardBits(src, pos, length);
            int maxBits = huf.MaxBits;
            for (int i = 0; i < count; i++)
            {
                int index = (int)bits.Peek(maxBits);
                output[at + i] = huf.Symbol[index];
                bits.Skip(huf.Bits[index]);
            }
            if (bits.Position != 0) throw new InvalidDataException("zstd literal stream not fully consumed.");
        }

        // Bits written forward, read from the end: the last byte's highest set bit is a marker.
        private sealed class BackwardBits
        {
            private readonly byte[] data; private readonly int start, length;
            internal long Position;   // bits still available below this index
            internal BackwardBits(byte[] data, int start, int length)
            {
                if (length <= 0 || start + length > data.Length) throw new InvalidDataException("Empty zstd bitstream.");
                this.data = data; this.start = start; this.length = length;
                byte last = data[start + length - 1];
                if (last == 0) throw new InvalidDataException("zstd bitstream without end marker.");
                Position = (long)(length - 1) * 8 + HighBit(last);
            }
            internal ulong Read(int n) { if (n == 0) return 0; Position -= n; return Get(Position, n); }
            internal ulong Peek(int n) { return n == 0 ? 0 : Get(Position - n, n); }
            internal void Skip(int n) { Position -= n; }
            private ulong Get(long at, int n)
            {
                if (at < 0)
                {
                    if (at + n <= 0) return 0;
                    int shift = (int)(-at);
                    return Get(0, n - shift) << shift;
                }
                int byteIndex = (int)(at >> 3), offset = (int)(at & 7);
                int need = (offset + n + 7) >> 3;
                ulong v = 0;
                for (int i = 0; i < need; i++) { int p = byteIndex + i; if (p < length) v |= (ulong)data[start + p] << (8 * i); }
                return (v >> offset) & (n >= 64 ? ulong.MaxValue : ((1UL << n) - 1));
            }
        }

        private sealed class ForwardBits
        {
            private readonly byte[] data; private readonly int start, end;
            internal long Position;
            internal ForwardBits(byte[] data, int start, int end) { this.data = data; this.start = start; this.end = end; }
            internal ulong Peek(int n)
            {
                if (n == 0) return 0;
                int byteIndex = (int)(Position >> 3), offset = (int)(Position & 7);
                int need = (offset + n + 7) >> 3;
                ulong v = 0;
                for (int i = 0; i < need; i++) { int p = start + byteIndex + i; if (p < end) v |= (ulong)data[p] << (8 * i); }
                return (v >> offset) & ((1UL << n) - 1);
            }
            internal ulong Read(int n) { ulong v = Peek(n); Position += n; return v; }
            internal void Skip(int n) { Position += n; }
        }
    }
}
