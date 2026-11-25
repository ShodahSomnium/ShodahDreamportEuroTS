using System;
using System.IO;
using SomniumSpace.Worlds.SomniumEuroTruck.Ets.GisDeflate;

namespace SomniumSpace.Worlds.SomniumEuroTruck.Ets
{
    /// <summary>
    /// GDeflate, the compression game 1.61 uses for every packed texture in its HashFS v2 archives
    /// (plain files stay zlib). The decoder itself is vendored in GDeflate/ from sk-zk's GisDeflate -
    /// the package TruckLib.HashFs depends on for the same job - under the MIT licence; see
    /// GDeflate/LICENSE.md.
    ///
    /// The stream is an 8-byte header (id, id ^ 0xff, u16 tile count, then a bit field whose low
    /// two bits are a tile size index and next eighteen the last tile's length), a table of u32
    /// tile offsets, then the tiles. Every tile inflates to 64 KiB but the last. Upstream decodes
    /// the tiles on Environment.ProcessorCount Task.Run workers; this decodes them on the calling
    /// thread, so nothing is started on the thread pool from inside a Unity coroutine.
    ///
    /// Verified end to end against the 1.61 demo's own archives: 880 of 880 real textures decode
    /// through this decoder to exactly the length their entry declares.
    /// </summary>
    public static class EtsGDeflate
    {
        public static byte[] Decompress(byte[] packed, int expectedLength)
        {
            if (packed == null || packed.Length < 8)
            {
                throw new InvalidDataException("GDeflate stream too short.");
            }

            var header = new TileStream();

            using (var reader = new BinaryReader(new MemoryStream(packed)))
            {
                header.Deserialize(reader);
            }

            header.ValidateStream();

            if (header.UncompressedSize != (uint)expectedLength)
            {
                throw new InvalidDataException("GDeflate stream would give " + header.UncompressedSize +
                                               " bytes, the entry declares " + expectedLength + ".");
            }

            int tiles = header.NumTiles;
            int tableEnd = 8 + tiles * 4;

            if (tableEnd > packed.Length)
            {
                throw new InvalidDataException("GDeflate tile table runs past the stream.");
            }

            var offsets = new int[tiles];
            Buffer.BlockCopy(packed, 8, offsets, 0, tiles * 4);

            var inData = new byte[packed.Length - tableEnd];
            Buffer.BlockCopy(packed, tableEnd, inData, 0, inData.Length);

            var context = new DecompressionContext
            {
                Input = packed,
                Output = new byte[header.UncompressedSize],
                GlobalIndex = 0,
                NumItems = header.NumTiles
            };

            // The upstream job loop, run once: it takes tiles off the shared index until none are
            // left, so a single call decodes the lot.
            Deflate.TileDecompressionJob(context, inData, offsets);
            return context.Output;
        }
    }
}
