// Block-compression decoders (BC1, BC2, BC3, BC4, BC5, BC7) and the RE Engine .tex reader used for the
// fighter portraits of the library Filters panel (2.3.0). Nothing here writes game files.
//
// BcDecoder is a C# port of bcdec.h v0.985 by Sergii "iOrange" Kudlai (https://github.com/iOrange/bcdec),
// used under its MIT licence (bcdec is dual MIT / Unlicense):
//
//   Copyright (c) 2022 Sergii Kudlai
//   Permission is hereby granted, free of charge, to any person obtaining a copy of this software
//   and associated documentation files (the "Software"), to deal in the Software without
//   restriction, including without limitation the rights to use, copy, modify, merge, publish,
//   distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the
//   Software is furnished to do so, subject to the following conditions: The above copyright
//   notice and this permission notice shall be included in all copies or substantial portions of
//   the Software. THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND.
//
// Output is 4x4 blocks of 32-bit pixels in B,G,R,A byte order (System.Drawing Format32bppArgb),
// written straight into a destination row-major buffer. C# 5, no unsafe code.
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace SlotsInstaller
{
    internal static class BcDecoder
    {
        // Decodes a whole BC surface of w x h pixels into a BGRA buffer (w*h*4 bytes).
        // fmt: 1..5 = BC1..BC5, 7 = BC7. pitch = bytes per row of blocks in src (0 = tight).
        internal static byte[] DecodeSurface(int fmt, byte[] src, int srcOffset, int w, int h, int pitch)
        {
            int bw = (w + 3) / 4, bh = (h + 3) / 4;
            int blockBytes = BlockBytes(fmt);
            if (pitch <= 0) pitch = bw * blockBytes;
            if (pitch < bw * blockBytes) throw new InvalidDataException("BC row pitch " + pitch + " is shorter than one row of blocks.");
            if (srcOffset < 0 || (long)srcOffset + (long)pitch * (bh - 1) + (long)bw * blockBytes > src.Length) throw new InvalidDataException("BC surface data lies outside the file.");
            var dst = new byte[w * h * 4];
            var block = new byte[64];              // one decoded 4x4 block, BGRA
            for (int by = 0; by < bh; by++)
            {
                int rowStart = srcOffset + by * pitch;
                for (int bx = 0; bx < bw; bx++)
                {
                    DecodeBlock(fmt, src, rowStart + bx * blockBytes, block);
                    for (int y = 0; y < 4; y++)
                    {
                        int py = by * 4 + y; if (py >= h) break;
                        int n = Math.Min(4, w - bx * 4);
                        Buffer.BlockCopy(block, y * 16, dst, (py * w + bx * 4) * 4, n * 4);
                    }
                }
            }
            return dst;
        }

        internal static int BlockBytes(int fmt)
        {
            if (fmt == 1 || fmt == 4) return 8;
            if (fmt == 2 || fmt == 3 || fmt == 5 || fmt == 7) return 16;
            throw new NotSupportedException("BC format " + fmt);
        }

        // One 4x4 block into 64 BGRA bytes.
        internal static void DecodeBlock(int fmt, byte[] src, int at, byte[] block)
        {
            switch (fmt)
            {
                case 1: ColorBlock(src, at, block, false); break;
                case 2: ColorBlock(src, at + 8, block, true); SharpAlpha(src, at, block); break;
                case 3: ColorBlock(src, at + 8, block, true); SmoothAlpha(src, at, block, 3); break;
                case 4: SmoothAlpha(src, at, block, 2); for (int k = 0; k < 16; k++) { block[k * 4] = block[k * 4 + 1] = block[k * 4 + 2]; block[k * 4 + 3] = 255; } break;
                case 5: SmoothAlpha(src, at, block, 2); SmoothAlpha(src, at + 8, block, 1); for (int k = 0; k < 16; k++) { block[k * 4] = 0; block[k * 4 + 3] = 255; } break;
                case 7: Bc7(src, at, block); break;
                default: throw new NotSupportedException("BC format " + fmt);
            }
        }

        private static void Put(byte[] b, int px, int r, int g, int bl, int a)
        {
            b[px * 4] = (byte)bl; b[px * 4 + 1] = (byte)g; b[px * 4 + 2] = (byte)r; b[px * 4 + 3] = (byte)a;
        }

        // bcdec__color_block. onlyOpaque (BC2/BC3): always four colours; otherwise BC1 punch-through
        // (c0 <= c1 => colour 3 is transparent black).
        private static void ColorBlock(byte[] s, int o, byte[] dst, bool onlyOpaque)
        {
            int c0 = s[o] | (s[o + 1] << 8), c1 = s[o + 2] | (s[o + 3] << 8);
            int r0 = (c0 >> 11) & 0x1F, g0 = (c0 >> 5) & 0x3F, b0 = c0 & 0x1F;
            int r1 = (c1 >> 11) & 0x1F, g1 = (c1 >> 5) & 0x3F, b1 = c1 & 0x1F;
            var R = new int[4]; var G = new int[4]; var B = new int[4]; var A = new int[] { 255, 255, 255, 255 };
            R[0] = (r0 * 527 + 23) >> 6; G[0] = (g0 * 259 + 33) >> 6; B[0] = (b0 * 527 + 23) >> 6;
            R[1] = (r1 * 527 + 23) >> 6; G[1] = (g1 * 259 + 33) >> 6; B[1] = (b1 * 527 + 23) >> 6;
            if (c0 > c1 || onlyOpaque)
            {
                R[2] = ((2 * r0 + r1) * 351 + 61) >> 7; G[2] = ((2 * g0 + g1) * 2763 + 1039) >> 11; B[2] = ((2 * b0 + b1) * 351 + 61) >> 7;
                R[3] = ((r0 + r1 * 2) * 351 + 61) >> 7; G[3] = ((g0 + g1 * 2) * 2763 + 1039) >> 11; B[3] = ((b0 + b1 * 2) * 351 + 61) >> 7;
            }
            else
            {
                R[2] = ((r0 + r1) * 1053 + 125) >> 8; G[2] = ((g0 + g1) * 4145 + 1019) >> 11; B[2] = ((b0 + b1) * 1053 + 125) >> 8;
                R[3] = G[3] = B[3] = 0; A[3] = 0;
            }
            uint idx = (uint)(s[o + 4] | (s[o + 5] << 8) | (s[o + 6] << 16) | (s[o + 7] << 24));
            for (int p = 0; p < 16; p++) { int i = (int)(idx & 3); idx >>= 2; Put(dst, p, R[i], G[i], B[i], A[i]); }
        }

        private static void SharpAlpha(byte[] s, int o, byte[] dst)
        {
            for (int row = 0; row < 4; row++)
            {
                int a = s[o + row * 2] | (s[o + row * 2 + 1] << 8);
                for (int j = 0; j < 4; j++) dst[(row * 4 + j) * 4 + 3] = (byte)(((a >> (4 * j)) & 0xF) * 17);
            }
        }

        // bcdec__smooth_alpha_block; channel = byte index within the BGRA pixel to write.
        private static void SmoothAlpha(byte[] s, int o, byte[] dst, int channel)
        {
            ulong block = BitConverter.ToUInt64(s, o);
            var a = new int[8];
            a[0] = (int)(block & 0xFF); a[1] = (int)((block >> 8) & 0xFF);
            if (a[0] > a[1])
            {
                a[2] = (6 * a[0] + a[1]) / 7; a[3] = (5 * a[0] + 2 * a[1]) / 7; a[4] = (4 * a[0] + 3 * a[1]) / 7;
                a[5] = (3 * a[0] + 4 * a[1]) / 7; a[6] = (2 * a[0] + 5 * a[1]) / 7; a[7] = (a[0] + 6 * a[1]) / 7;
            }
            else
            {
                a[2] = (4 * a[0] + a[1]) / 5; a[3] = (3 * a[0] + 2 * a[1]) / 5; a[4] = (2 * a[0] + 3 * a[1]) / 5;
                a[5] = (a[0] + 4 * a[1]) / 5; a[6] = 0; a[7] = 255;
            }
            ulong indices = block >> 16;
            for (int p = 0; p < 16; p++) { dst[p * 4 + channel] = (byte)a[(int)(indices & 7)]; indices >>= 3; }
        }

        // ---- BC7 ---------------------------------------------------------------------------------
        private struct Bits
        {
            internal ulong Low, High;
            internal int Read(int n)
            {
                uint mask = (uint)((1 << n) - 1);
                uint bits = (uint)(Low & mask);
                Low >>= n;
                Low |= (High & mask) << (64 - n);
                High >>= n;
                return (int)bits;
            }
        }

        private static readonly int[,] ActualBits = { { 4, 6, 5, 7, 5, 7, 7, 5 }, { 0, 0, 0, 0, 6, 8, 7, 5 } };
        private static readonly int[] W2 = { 0, 21, 43, 64 }, W3 = { 0, 9, 18, 27, 37, 46, 55, 64 }, W4 = { 0, 4, 9, 13, 17, 21, 26, 30, 34, 38, 43, 47, 51, 55, 60, 64 };
        private const int ModeHasPBits = 0xCB; // 0b11001011

        // From bcdec.h partition_sets (ARB_texture_compression_bptc tables): 16 subset digits per shape
        // (pixel 0 first) and a mask of the fix-up (anchor) pixels whose index has one bit less.
        private static readonly byte[][] Part2 = BuildParts(new string[] { "0011001100110011","0001000100010001","0111011101110111","0001001100110111","0000000100010011","0011011101111111","0001001101111111","0000000100110111","0000000000010011","0011011111111111","0000000101111111","0000000000010111","0001011111111111","0000000011111111","0000111111111111","0000000000001111","0000100011101111","0111000100000000","0000000010001110","0111001100010000","0011000100000000","0000100011001110","0000000010001100","0111001100110001","0011000100010000","0000100010001100","0110011001100110","0011011001101100","0001011111101000","0000111111110000","0111000110001110","0011100110011100","0101010101010101","0000111100001111","0101101001011010","0011001111001100","0011110000111100","0101010110101010","0110100101101001","0101101010100101","0111001111001110","0001001111001000","0011001001001100","0011101111011100","0110100110010110","0011110011000011","0110011010011001","0000011001100000","0100111001000000","0010011100100000","0000001001110010","0000010011100100","0110110010010011","0011011011001001","0110001110011100","0011100111000110","0110110011001001","0110001100111001","0111111010000001","0001100011100111","0000111100110011","0011001111110000","0010001011101110","0100010001110111" });
        private static readonly byte[][] Part3 = BuildParts(new string[] { "0011001102212222","0001001122112221","0000200122112211","0222002200110111","0000000011221122","0011001100220022","0022002211111111","0011001122112211","0000000011112222","0000111111112222","0000111122222222","0012001200120012","0112011201120112","0122012201220122","0011011211221222","0011200122002220","0001001101121122","0111001120012200","0000112211221122","0022002200221111","0111011102220222","0001000122212221","0000001101220122","0000110022102210","0122012200110000","0012001211222222","0110122112210110","0000011012211221","0022110211020022","0110011020022222","0011012201220011","0000200022112221","0000000211221222","0222002200120011","0011001200220222","0120012001200120","0000111122220000","0120120120120120","0120201212010120","0011220011220011","0011112222000011","0101010122222222","0000000021212121","0022112200221122","0022001100220011","0220122102201221","0101222222220101","0000212121212121","0101010101012222","0222011102220111","0002111200021112","0000211221122112","0222011101110222","0002111211120002","0110011001102222","0000000021122112","0110011022222222","0022001100110022","0022112211220022","0000000000002112","0002000100020001","0222122202221222","0101222222222222","0111201122012220" });
        private static readonly int[] AnchorMask2 = { 0x8001,0x8001,0x8001,0x8001,0x8001,0x8001,0x8001,0x8001,0x8001,0x8001,0x8001,0x8001,0x8001,0x8001,0x8001,0x8001,0x8001,0x0005,0x0101,0x0005,0x0005,0x0101,0x0101,0x8001,0x0005,0x0101,0x0005,0x0005,0x0101,0x0101,0x0005,0x0005,0x8001,0x8001,0x0041,0x0101,0x0005,0x0101,0x8001,0x8001,0x0005,0x0101,0x0005,0x0005,0x0005,0x8001,0x8001,0x0041,0x0041,0x0005,0x0041,0x0101,0x8001,0x8001,0x0005,0x0005,0x8001,0x8001,0x8001,0x8001,0x8001,0x0005,0x0005,0x8001 };
        private static readonly int[] AnchorMask3 = { 0x8009,0x0109,0x8101,0x8009,0x8101,0x8009,0x8009,0x8101,0x8101,0x8101,0x8041,0x8041,0x8041,0x8021,0x8009,0x0109,0x8009,0x0109,0x8101,0x8009,0x8009,0x0109,0x8041,0x0501,0x0029,0x8101,0x0141,0x0441,0x8101,0x8021,0x8401,0x8101,0x8101,0x8009,0x8009,0x0421,0x0441,0x0501,0x0301,0x8401,0x8041,0x8009,0x8101,0x8021,0x8009,0x8041,0x8041,0x8101,0x8009,0x8009,0x8021,0x8021,0x8021,0x8101,0x8021,0x8401,0x8021,0x8401,0x8101,0xA001,0x8009,0x9001,0x8009,0x0109 };

        private static byte[][] BuildParts(string[] rows)
        {
            var r = new byte[rows.Length][];
            for (int i = 0; i < rows.Length; i++) { r[i] = new byte[16]; for (int p = 0; p < 16; p++) r[i][p] = (byte)(rows[i][p] - '0'); }
            return r;
        }

        private static int Interp(int a, int b, int[] w, int i) { return (a * (64 - w[i]) + b * w[i] + 32) >> 6; }

        private static void Bc7(byte[] s, int o, byte[] dst)
        {
            var bs = new Bits { Low = BitConverter.ToUInt64(s, o), High = BitConverter.ToUInt64(s, o + 8) };
            int mode = 0;
            while (mode < 8 && bs.Read(1) == 0) mode++;
            if (mode >= 8) { Array.Clear(dst, 0, 64); return; }   // reserved mode: transparent black, as bcdec

            int partition = 0, numPartitions = 1, rotation = 0, indexSelectionBit = 0;
            if (mode == 0 || mode == 1 || mode == 2 || mode == 3 || mode == 7)
            {
                numPartitions = (mode == 0 || mode == 2) ? 3 : 2;
                partition = bs.Read(mode == 0 ? 4 : 6);
            }
            int numEndpoints = numPartitions * 2;
            if (mode == 4 || mode == 5)
            {
                rotation = bs.Read(2);
                if (mode == 4) indexSelectionBit = bs.Read(1);
            }
            var ep = new int[6, 4];
            for (int c = 0; c < 3; c++) for (int e = 0; e < numEndpoints; e++) ep[e, c] = bs.Read(ActualBits[0, mode]);
            if (ActualBits[1, mode] > 0) for (int e = 0; e < numEndpoints; e++) ep[e, 3] = bs.Read(ActualBits[1, mode]);

            if (mode == 0 || mode == 1 || mode == 3 || mode == 6 || mode == 7)
            {
                for (int e = 0; e < numEndpoints; e++) for (int c = 0; c < 4; c++) ep[e, c] <<= 1;
                if (mode == 1)
                {
                    int p0 = bs.Read(1), p1 = bs.Read(1);
                    for (int c = 0; c < 3; c++) { ep[0, c] |= p0; ep[1, c] |= p0; ep[2, c] |= p1; ep[3, c] |= p1; }
                }
                else if ((ModeHasPBits & (1 << mode)) != 0)
                {
                    for (int e = 0; e < numEndpoints; e++) { int p = bs.Read(1); for (int c = 0; c < 4; c++) ep[e, c] |= p; }
                }
            }
            int pb = (ModeHasPBits >> mode) & 1;
            for (int e = 0; e < numEndpoints; e++)
            {
                int j = ActualBits[0, mode] + pb;
                for (int c = 0; c < 3; c++) { ep[e, c] = ep[e, c] << (8 - j); ep[e, c] = ep[e, c] | (ep[e, c] >> j); }
                j = ActualBits[1, mode] + pb;
                ep[e, 3] = ep[e, 3] << (8 - j); ep[e, 3] = ep[e, 3] | (ep[e, 3] >> j);
            }
            if (ActualBits[1, mode] == 0) for (int e = 0; e < numEndpoints; e++) ep[e, 3] = 0xFF;

            int indexBitsBase = (mode == 0 || mode == 1) ? 3 : (mode == 6 ? 4 : 2);
            int indexBits2 = mode == 4 ? 3 : (mode == 5 ? 2 : 0);
            int[] weights = indexBitsBase == 2 ? W2 : (indexBitsBase == 3 ? W3 : W4);
            int[] weights2 = indexBits2 == 2 ? W2 : W3;

            byte[] shape = numPartitions == 2 ? Part2[partition] : (numPartitions == 3 ? Part3[partition] : null);
            var indices = new int[16];
            for (int p = 0; p < 16; p++)
            {
                int bits = indexBitsBase;
                if (IsAnchor(numPartitions, partition, p)) bits--;
                indices[p] = bs.Read(bits);
            }
            for (int p = 0; p < 16; p++)
            {
                int set = shape == null ? 0 : shape[p];
                int index = indices[p], r, g, b, a;
                int e0 = set * 2, e1 = set * 2 + 1;
                if (indexBits2 == 0)
                {
                    r = Interp(ep[e0, 0], ep[e1, 0], weights, index); g = Interp(ep[e0, 1], ep[e1, 1], weights, index);
                    b = Interp(ep[e0, 2], ep[e1, 2], weights, index); a = Interp(ep[e0, 3], ep[e1, 3], weights, index);
                }
                else
                {
                    int index2 = bs.Read(p != 0 ? indexBits2 : indexBits2 - 1);
                    if (indexSelectionBit == 0)
                    {
                        r = Interp(ep[e0, 0], ep[e1, 0], weights, index); g = Interp(ep[e0, 1], ep[e1, 1], weights, index);
                        b = Interp(ep[e0, 2], ep[e1, 2], weights, index); a = Interp(ep[e0, 3], ep[e1, 3], weights2, index2);
                    }
                    else
                    {
                        r = Interp(ep[e0, 0], ep[e1, 0], weights2, index2); g = Interp(ep[e0, 1], ep[e1, 1], weights2, index2);
                        b = Interp(ep[e0, 2], ep[e1, 2], weights2, index2); a = Interp(ep[e0, 3], ep[e1, 3], weights, index);
                    }
                }
                int t;
                switch (rotation)
                {
                    case 1: t = a; a = r; r = t; break;
                    case 2: t = a; a = g; g = t; break;
                    case 3: t = a; a = b; b = t; break;
                }
                Put(dst, p, r, g, b, a);
            }
        }

        private static bool IsAnchor(int numPartitions, int partition, int p)
        {
            if (numPartitions == 1) return p == 0;
            int m = numPartitions == 2 ? AnchorMask2[partition] : AnchorMask3[partition];
            return (m & (1 << p)) != 0;
        }
    }

    // RE Engine .tex reader for Street Fighter 6 (.tex.143230113 and .tex.241101895, same layout):
    //   0  "TEX\0"         4  version u32
    //   8  width u16      10  height u16     12  depth u16
    //  14  image count u8 15  mip-table bytes per image u8 (= mip count * 16)
    //  16  DXGI format u32 20 swizzle control i32  24 cubemap marker u32  28 flags u16 + 2
    //  32  8-byte swizzle block (unused on PC)
    //  40  mip table: per image, per level { u64 offset (from file start), u32 row pitch, u32 size }
    // then raw level data. No GDeflate / packed mips in SF6.
    internal sealed class ReTex
    {
        internal const int HeaderBytes = 40;
        internal int Version, Width, Height, Depth, Images, MipCount, Format;
        internal long[] MipOffset; internal int[] MipPitch, MipSize;
        internal byte[] Data;

        internal static ReTex Parse(byte[] b)
        {
            if (b == null || b.Length < HeaderBytes || b[0] != 'T' || b[1] != 'E' || b[2] != 'X' || b[3] != 0) throw new InvalidDataException("Not an RE Engine texture (no TEX header).");
            var t = new ReTex { Data = b };
            t.Version = BitConverter.ToInt32(b, 4);
            t.Width = BitConverter.ToUInt16(b, 8); t.Height = BitConverter.ToUInt16(b, 10); t.Depth = BitConverter.ToUInt16(b, 12);
            t.Images = b[14] == 0 ? 1 : b[14]; t.MipCount = b[15] / 16; t.Format = BitConverter.ToInt32(b, 16);
            if (t.Width == 0 || t.Height == 0) throw new InvalidDataException("Texture has no size.");
            if (t.MipCount <= 0 || b[15] % 16 != 0) throw new InvalidDataException("Texture mip table is empty or malformed.");
            if (HeaderBytes + 16L * t.MipCount * t.Images > b.Length) throw new InvalidDataException("Texture mip table exceeds the file.");
            t.MipOffset = new long[t.MipCount]; t.MipPitch = new int[t.MipCount]; t.MipSize = new int[t.MipCount];
            for (int i = 0; i < t.MipCount; i++)   // image 0 only
            {
                int at = HeaderBytes + 16 * i;
                t.MipOffset[i] = BitConverter.ToInt64(b, at); t.MipPitch[i] = BitConverter.ToInt32(b, at + 8); t.MipSize[i] = BitConverter.ToInt32(b, at + 12);
                if (t.MipOffset[i] < HeaderBytes || t.MipSize[i] <= 0 || t.MipOffset[i] + t.MipSize[i] > b.Length) throw new InvalidDataException("Texture level " + i + " lies outside the file.");
            }
            return t;
        }

        internal string FormatName
        {
            get
            {
                switch (Format)
                {
                    case 28: return "R8G8B8A8_UNORM"; case 29: return "R8G8B8A8_SRGB";
                    case 71: return "BC1_UNORM"; case 72: return "BC1_SRGB"; case 74: return "BC2_UNORM"; case 75: return "BC2_SRGB";
                    case 77: return "BC3_UNORM"; case 78: return "BC3_SRGB"; case 80: return "BC4_UNORM"; case 83: return "BC5_UNORM";
                    case 87: return "B8G8R8A8_UNORM"; case 91: return "B8G8R8A8_SRGB"; case 95: return "BC6H_UF16";
                    case 98: return "BC7_UNORM"; case 99: return "BC7_SRGB"; case 61: return "R8_UNORM";
                }
                return "DXGI_" + Format;
            }
        }

        internal int LevelWidth(int i) { return Math.Max(1, Width >> i); }
        internal int LevelHeight(int i) { return Math.Max(1, Height >> i); }

        // The smallest level that still covers minW x minH (else the top level).
        internal int PickLevel(int minW, int minH)
        {
            int pick = 0;
            for (int i = 0; i < MipCount; i++) if (LevelWidth(i) >= minW && LevelHeight(i) >= minH) pick = i;
            return pick;
        }

        internal static bool Supported(int fmt)
        {
            switch (fmt) { case 28: case 29: case 87: case 91: case 61: case 71: case 72: case 74: case 75: case 77: case 78: case 80: case 83: case 98: case 99: return true; }
            return false;
        }

        // Decodes one level to BGRA bytes. Throws for unsupported formats and data outside the file.
        internal byte[] DecodeLevel(int level)
        {
            if (level < 0 || level >= MipCount) throw new ArgumentOutOfRangeException("level");
            if (!Supported(Format)) throw new NotSupportedException("Texture format " + FormatName + " is not supported.");
            int w = LevelWidth(level), h = LevelHeight(level); int off = (int)MipOffset[level], pitch = MipPitch[level];
            switch (Format)
            {
                case 71: case 72: return BcDecoder.DecodeSurface(1, Data, off, w, h, pitch);
                case 74: case 75: return BcDecoder.DecodeSurface(2, Data, off, w, h, pitch);
                case 77: case 78: return BcDecoder.DecodeSurface(3, Data, off, w, h, pitch);
                case 80: return BcDecoder.DecodeSurface(4, Data, off, w, h, pitch);
                case 83: return BcDecoder.DecodeSurface(5, Data, off, w, h, pitch);
                case 98: case 99: return BcDecoder.DecodeSurface(7, Data, off, w, h, pitch);
            }
            int bpp = Format == 61 ? 1 : 4;
            if (pitch < w * bpp || (long)off + (long)pitch * (h - 1) + w * bpp > Data.Length) throw new InvalidDataException("Texture level " + level + " lies outside the file.");
            var dst = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int d = (y * w + x) * 4;
                    switch (Format)
                    {
                        case 28: case 29: { int s = off + y * pitch + x * 4; dst[d] = Data[s + 2]; dst[d + 1] = Data[s + 1]; dst[d + 2] = Data[s]; dst[d + 3] = Data[s + 3]; break; }
                        case 87: case 91: { int s = off + y * pitch + x * 4; Buffer.BlockCopy(Data, s, dst, d, 4); break; }
                        case 61: { byte v = Data[off + y * pitch + x]; dst[d] = dst[d + 1] = dst[d + 2] = v; dst[d + 3] = 255; break; }
                    }
                }
            return dst;
        }

        internal static Bitmap ToBitmap(byte[] bgra, int w, int h)
        {
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            var bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try { for (int y = 0; y < h; y++) Marshal.Copy(bgra, y * w * 4, bd.Scan0 + y * bd.Stride, w * 4); }
            finally { bmp.UnlockBits(bd); }
            return bmp;
        }
    }
}
