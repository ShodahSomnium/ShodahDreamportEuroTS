// Vendored from GisDeflate (https://github.com/sk-zk/GisDeflate), MIT License, Copyright (c) 2024 skzk,
// a C# port of Microsoft's GDeflate from DirectStorage, MIT License, Copyright (c) Microsoft Corporation.
// Both notices in full in LICENSE.md beside this file. Changed here: the namespace, moved under the
// world's own, and an unused System.Reflection.Emit import dropped. GDeflate.cs is not vendored - its
// entry point runs Task.Run workers - and EtsGDeflate decodes on the calling thread instead.

namespace SomniumSpace.Worlds.SomniumEuroTruck.Ets.GisDeflate
{
    /// <summary>
    /// GDeflate deferred copy state structure.
    /// </summary>
    internal struct DeferredCopy
    {
        public uint Length;
        public uint OutNextIdx;
    }
}
