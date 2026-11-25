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
    /// Valid block types.
    /// </summary>
    internal enum BlockType
    {
        Uncompressed = 0,
        StaticHuffman = 1,
        DynamicHuffman = 2
    }
}
