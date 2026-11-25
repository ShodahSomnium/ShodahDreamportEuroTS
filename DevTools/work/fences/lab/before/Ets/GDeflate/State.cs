// Vendored from GisDeflate (https://github.com/sk-zk/GisDeflate), MIT License, Copyright (c) 2024 skzk,
// a C# port of Microsoft's GDeflate from DirectStorage, MIT License, Copyright (c) Microsoft Corporation.
// Both notices in full in LICENSE.md beside this file. Changed here: the namespace, moved under the
// world's own, and an unused System.Reflection.Emit import dropped. GDeflate.cs is not vendored - its
// entry point runs Task.Run workers - and EtsGDeflate decodes on the calling thread instead.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SomniumSpace.Worlds.SomniumEuroTruck.Ets.GisDeflate
{
    /// <summary>
    /// GDeflate state structure.
    /// </summary>
    internal struct State
    {
        public ulong[] BitBuf;
        public uint[] BitsLeft;
        public DeferredCopy[] Copies;
        public uint Idx;

        // Upstream gives these field initialisers and a parameterless constructor, which are C# 10;
        // the world compiles as C# 9. Same fields, same values, same order.
        public static State Create()
        {
            State s = default(State);
            s.BitBuf = new ulong[Deflate.NumStreams];
            s.BitsLeft = new uint[Deflate.NumStreams];
            s.Copies = new DeferredCopy[Deflate.NumStreams];
            s.Idx = 0;
            return s;
        }
    }
}
