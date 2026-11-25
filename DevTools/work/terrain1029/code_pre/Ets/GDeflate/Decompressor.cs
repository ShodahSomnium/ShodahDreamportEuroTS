// Vendored from GisDeflate (https://github.com/sk-zk/GisDeflate), MIT License, Copyright (c) 2024 skzk,
// a C# port of Microsoft's GDeflate from DirectStorage, MIT License, Copyright (c) Microsoft Corporation.
// Both notices in full in LICENSE.md beside this file. Changed here: the namespace, moved under the
// world's own, and an unused System.Reflection.Emit import dropped. GDeflate.cs is not vendored - its
// entry point runs Task.Run workers - and EtsGDeflate decodes on the calling thread instead.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace SomniumSpace.Worlds.SomniumEuroTruck.Ets.GisDeflate
{
    /// <summary>
    /// The main DEFLATE decompressor structure. Since this implementation only
    /// supports full buffer decompression, this structure does not store the entire
    /// decompression state, but rather only some arrays that are too large to
    /// comfortably allocate on the stack.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct Decompressor
    {
        // Each ENOUGH number is the maximum number of decode table entries that may be
        // required for the corresponding Huffman code, including the main table and all
        // subtables. Each number depends on three parameters:
        // 
        // (1) the maximum number of symbols in the code (DEFLATE_NUM_*_SYMS)
        // (2) the number of main table bits (the TABLEBITS numbers defined above)
        // (3) the maximum allowed codeword length (DEFLATE_MAX_*_CODEWORD_LEN)
        // 
        // The ENOUGH numbers were computed using the utility program 'enough' from
        // zlib. This program enumerates all possible relevant Huffman codes to find
        // the worst-case usage of decode table entries.

        const int PrecodeEnough = 128; // enough 19 7 7
        const int LitlenEnough = 1334; // enough 288 10 15
        const int OffsetEnough = 402; // enough 32 8 15

        // The arrays aren't all needed at the same time.  'PrecodeLens' and
        // 'PrecodeDecodeTable' are unneeded after 'Lens' has been filled.
        // Furthermore, 'Lens' need not be retained after building the litlen
        // and offset decode tables.  In fact, 'Lens' can be in union with
        // 'LitlenDecodeTable' provided that 'OffsetDecodeTable' is separate
        // and is built first.

        [StructLayout(LayoutKind.Explicit)]
        public struct Union
        {
            [FieldOffset(0)]
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = Deflate.DeflateNumPrecodeSyms)]
            public byte[] PrecodeLens;

            [FieldOffset(0)]
            public InnerStruct L;

            [FieldOffset(0)]
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = LitlenEnough)]
            public uint[] LitlenDecodeTable;

            // Upstream initialises these in field order: PrecodeLens, then L, then LitlenDecodeTable.
            // The three share offset 0, so the ORDER is the behaviour - whatever is assigned last owns
            // the slot, and the others then alias it (a C union, emulated with overlapping array
            // references). Kept exactly: C# 10 field initialisers rewritten as C# 9 assignments.
            public static Union Create()
            {
                Union u = default(Union);
                u.PrecodeLens = new byte[Deflate.DeflateNumPrecodeSyms];
                u.L = InnerStruct.Create();
                u.LitlenDecodeTable = new uint[LitlenEnough];
                return u;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct InnerStruct
        {
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 
                Deflate.DeflateNumLitlenSyms 
                + Deflate.DeflateNumOffsetSyms 
                + Deflate.DeflateMaxLensOverrun)]
            public byte[] Lens;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = PrecodeEnough)]
            public uint[] PrecodeDecodeTable;

            public static InnerStruct Create()
            {
                InnerStruct s = default(InnerStruct);
                s.Lens = new byte[
                    Deflate.DeflateNumLitlenSyms
                    + Deflate.DeflateNumOffsetSyms
                    + Deflate.DeflateMaxLensOverrun];
                s.PrecodeDecodeTable = new uint[PrecodeEnough];
                return s;
            }
        }

        public Union U;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = OffsetEnough)]
        public uint[] OffsetDecodeTable;

        // used only during BuildDecodeTable()
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = Deflate.DeflateMaxNumSyms)]
        public ushort[] SortedSyms;

        public bool StaticCodesLoaded;

        // Upstream's field initialisers and parameterless constructor (C# 10), as C# 9 assignments in
        // the same order. Still a struct and still passed by value, so each tile starts from a copy
        // with StaticCodesLoaded false exactly as before.
        public static Decompressor Create()
        {
            Decompressor d = default(Decompressor);
            d.U = Union.Create();
            d.OffsetDecodeTable = new uint[OffsetEnough];
            d.SortedSyms = new ushort[Deflate.DeflateMaxNumSyms];
            d.StaticCodesLoaded = false;
            return d;
        }
    }
}
