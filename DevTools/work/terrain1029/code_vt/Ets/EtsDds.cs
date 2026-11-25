using System;
using System.Text;

namespace SomniumSpace.Worlds.SomniumEuroTruck.Ets
{
    /// <summary>
    /// A DirectDraw Surface file, which is what every texture in the game is: a 128-byte header,
    /// an optional 20-byte DX10 extension, then the mip chain largest first.
    ///
    /// The pixel formats are left compressed. DXT1, DXT5, BC4, BC5 and BC7 are formats the GPU
    /// reads directly and Unity uploads verbatim, and the mip order on disk is the order Unity
    /// wants, so the whole file after the header goes up as one block. The one format Unity
    /// will not take, DXT3, is decoded here into plain RGBA because it appears in a handful of
    /// old textures and a black square is a worse answer than a slow one.
    ///
    /// Formats, measured over every one of the 66,723 packed images in the 1.61 demo (harness
    /// details/textures ProgramTexCensus, 2026-09-23): BC3 sRGB 19,084, BC1 sRGB 18,045, BGRA sRGB
    /// 9,522, BC5 6,887, BC3 5,488, BC4 4,272, BC1 1,815, BGRX 479 + 415, R8 414, R9G9B9E5 197 (the
    /// skies), R16G16 11 (truck paint, chrome and lamp normal maps of the DAF XF, Iveco Hi-Way,
    /// Volvo FH16 2012 and Actros 2014), BGRA 24, BC2 sRGB 1; 70 of them cube maps, no arrays.
    /// R16G16 and R9G9B9E5 were the two that could not be read at all ("DXGI format 35 is not
    /// supported"); both are uploaded as the GPU formats they are (RG32, RGB9e5Float). The other
    /// uncompressed DXGI formats a later install might hold are read too, the 16-bit packed ones
    /// expanded to RGBA.
    /// </summary>
    public sealed class EtsDds
    {
        public enum PixelFormat
        {
            Unknown,
            Dxt1,
            Dxt5,
            Bc4,
            Bc5,
            Bc7,
            Rgba32,
            Bgra32,

            /// <summary>DXGI 35 R16G16_UNORM: two 16-bit channels (Unity RG32). High-precision normal maps.</summary>
            Rg32,

            /// <summary>DXGI 67 R9G9B9E5_SHAREDEXP: HDR colour, 32 bits a pixel (Unity RGB9e5Float).</summary>
            Rgb9e5,

            /// <summary>DXGI 49 R8G8_UNORM (Unity RG16).</summary>
            Rg16,

            /// <summary>DXGI 56 R16_UNORM (Unity R16).</summary>
            R16,

            /// <summary>DXGI 10 R16G16B16A16_FLOAT (Unity RGBAHalf).</summary>
            RgbaHalf,

            /// <summary>DXGI 2 R32G32B32A32_FLOAT (Unity RGBAFloat).</summary>
            RgbaFloat,

            /// <summary>DXGI 11 R16G16B16A16_UNORM (Unity RGBA64).</summary>
            Rgba64,

            /// <summary>DXGI 95 BC6H_UF16 (Unity BC6H).</summary>
            Bc6h,

            /// <summary>DXGI 65 A8_UNORM (Unity Alpha8).</summary>
            Alpha8
        }

        /// <summary>The DXGI format number the pixels came in, or -1 for a file described by a FourCC or bit masks.</summary>
        public int Dxgi = -1;

        /// <summary>True when the DXGI format is one of the _SRGB ones: the GPU decodes it to linear as it samples.</summary>
        public bool IsSrgb => Dxgi == 29 || Dxgi == 72 || Dxgi == 75 || Dxgi == 78 || Dxgi == 91 || Dxgi == 93 || Dxgi == 99;

        private const uint DdpfAlphaPixels = 0x1;
        private const uint DdpfFourCc = 0x4;
        private const uint DdpfRgb = 0x40;
        private const uint DdpfLuminance = 0x20000;
        private const uint DdsCaps2CubeMap = 0x200;

        public int Width;
        public int Height;
        public int MipCount;
        public PixelFormat Format;
        public bool IsCubeMap;

        /// <summary>The mip chain, largest first, in <see cref="Format"/>.</summary>
        public byte[] Data;

        public bool IsCompressed => Format == PixelFormat.Dxt1 || Format == PixelFormat.Dxt5 ||
                                    Format == PixelFormat.Bc4 || Format == PixelFormat.Bc5 || Format == PixelFormat.Bc7 ||
                                    Format == PixelFormat.Bc6h;

        public static EtsDds Parse(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 128 || bytes[0] != 'D' || bytes[1] != 'D' || bytes[2] != 'S' || bytes[3] != ' ')
            {
                throw new ArgumentException("Not a DDS file.");
            }

            var dds = new EtsDds
            {
                Height = BitConverter.ToInt32(bytes, 12),
                Width = BitConverter.ToInt32(bytes, 16),
                MipCount = Math.Max(1, BitConverter.ToInt32(bytes, 28))
            };

            uint pfFlags = BitConverter.ToUInt32(bytes, 80);
            string fourCc = Encoding.ASCII.GetString(bytes, 84, 4);
            int bitCount = BitConverter.ToInt32(bytes, 88);
            uint rMask = BitConverter.ToUInt32(bytes, 92);
            uint gMask = BitConverter.ToUInt32(bytes, 96);
            uint bMask = BitConverter.ToUInt32(bytes, 100);
            uint aMask = BitConverter.ToUInt32(bytes, 104);
            uint caps2 = BitConverter.ToUInt32(bytes, 112);

            dds.IsCubeMap = (caps2 & DdsCaps2CubeMap) != 0;

            int dataOffset = 128;
            bool dxt3 = false;
            bool rgb24 = false;
            bool bgr = false;
            bool luminance = false;
            bool opaque = false;
            int packed16 = 0;

            if ((pfFlags & DdpfFourCc) != 0)
            {
                switch (fourCc)
                {
                    case "DXT1": dds.Format = PixelFormat.Dxt1; break;
                    case "DXT3": dds.Format = PixelFormat.Rgba32; dxt3 = true; break;
                    case "DXT5": dds.Format = PixelFormat.Dxt5; break;
                    case "ATI1":
                    case "BC4U": dds.Format = PixelFormat.Bc4; break;
                    case "ATI2":
                    case "BC5U": dds.Format = PixelFormat.Bc5; break;
                    case "DX10":
                        {
                            int dxgi = BitConverter.ToInt32(bytes, 128);
                            dataOffset = 148;
                            dds.Dxgi = dxgi;

                            switch (dxgi)
                            {
                                case 71: case 72: dds.Format = PixelFormat.Dxt1; break;
                                case 77: case 78: dds.Format = PixelFormat.Dxt5; break;
                                case 80: dds.Format = PixelFormat.Bc4; break;
                                case 83: dds.Format = PixelFormat.Bc5; break;
                                case 98: case 99: dds.Format = PixelFormat.Bc7; break;
                                case 28: case 29: dds.Format = PixelFormat.Rgba32; break;
                                case 87: case 91: dds.Format = PixelFormat.Bgra32; break;

                                // B8G8R8X8, linear and sRGB: laid out as BGRA but the fourth byte
                                // is padding, not alpha, so it is forced opaque after the copy.
                                case 88: case 93: dds.Format = PixelFormat.Bgra32; opaque = true; break;

                                // R8: one channel, expanded the way 8-bit luminance already is, so
                                // the red channel a shader samples is the stored value.
                                case 61: dds.Format = PixelFormat.Rgba32; luminance = true; break;

                                case 74: case 75: dds.Format = PixelFormat.Rgba32; dxt3 = true; break;

                                // Uploaded as the GPU formats they are (see PixelFormat).
                                case 35: dds.Format = PixelFormat.Rg32; break;
                                case 67: dds.Format = PixelFormat.Rgb9e5; break;
                                case 49: dds.Format = PixelFormat.Rg16; break;
                                case 56: dds.Format = PixelFormat.R16; break;
                                case 10: dds.Format = PixelFormat.RgbaHalf; break;
                                case 2: dds.Format = PixelFormat.RgbaFloat; break;
                                case 11: dds.Format = PixelFormat.Rgba64; break;
                                case 95: dds.Format = PixelFormat.Bc6h; break;
                                case 65: dds.Format = PixelFormat.Alpha8; break;

                                // 16-bit packed colour, which Unity takes in other bit orders only:
                                // expanded to RGBA, as 24-bit is.
                                case 85: dds.Format = PixelFormat.Rgba32; packed16 = 85; break;
                                case 86: dds.Format = PixelFormat.Rgba32; packed16 = 86; break;
                                case 115: dds.Format = PixelFormat.Rgba32; packed16 = 115; break;

                                default: throw new ArgumentException("DDS DXGI format " + dxgi + " is not supported.");
                            }

                            break;
                        }
                    default:
                        throw new ArgumentException("DDS four-character code \"" + fourCc + "\" is not supported.");
                }
            }
            else if ((pfFlags & DdpfRgb) != 0)
            {
                if (bitCount == 32)
                {
                    // Order by the red mask: 0x00FF0000 is the common BGRA layout.
                    dds.Format = rMask == 0x000000FF ? PixelFormat.Rgba32 : PixelFormat.Bgra32;
                }
                else if (bitCount == 24)
                {
                    dds.Format = PixelFormat.Rgba32;
                    rgb24 = true;
                    bgr = rMask != 0x000000FF;
                }
                else
                {
                    throw new ArgumentException("DDS uncompressed " + bitCount + "-bit is not supported.");
                }
            }
            else if ((pfFlags & DdpfLuminance) != 0 && bitCount == 8)
            {
                dds.Format = PixelFormat.Rgba32;
                luminance = true;
            }
            else
            {
                throw new ArgumentException("DDS pixel format flags 0x" + pfFlags.ToString("X") + " are not supported.");
            }

            int available = bytes.Length - dataOffset;

            if (dxt3)
            {
                dds.Data = DecodeDxt3Chain(bytes, dataOffset, dds.Width, dds.Height, ref dds.MipCount);
                return dds;
            }

            if (rgb24)
            {
                dds.Data = Expand24(bytes, dataOffset, dds.Width, dds.Height, ref dds.MipCount, bgr);
                return dds;
            }

            if (luminance)
            {
                dds.Data = ExpandLuminance(bytes, dataOffset, dds.Width, dds.Height, ref dds.MipCount);
                return dds;
            }

            if (packed16 != 0)
            {
                dds.Data = Expand16(bytes, dataOffset, dds.Width, dds.Height, ref dds.MipCount, packed16);
                return dds;
            }

            // Keep only as many mips as the file actually holds. A header that promises more
            // than is there would make Unity read past the end of the array.
            int needed = ChainSize(dds.Format, dds.Width, dds.Height, dds.MipCount, dds.IsCubeMap ? 6 : 1);

            while (needed > available && dds.MipCount > 1)
            {
                dds.MipCount--;
                needed = ChainSize(dds.Format, dds.Width, dds.Height, dds.MipCount, dds.IsCubeMap ? 6 : 1);
            }

            if (needed > available)
            {
                throw new ArgumentException("DDS is truncated: needs " + needed + " bytes of pixels, has " + available + ".");
            }

            dds.Data = new byte[needed];
            Buffer.BlockCopy(bytes, dataOffset, dds.Data, 0, needed);

            if (opaque)
            {
                for (int i = 3; i < dds.Data.Length; i += 4)
                {
                    dds.Data[i] = 255;
                }
            }

            return dds;
        }

        /// <summary>
        /// A texture out of a HashFS v2 archive (game 1.61 on): the GDeflate-inflated surface of a
        /// packed image, whose geometry lives in the archive's metadata rather than in a DDS header.
        /// Each mip starts on an imageAlign boundary and each row on a pitchAlign boundary; this
        /// takes the padding out, puts a standard DDS header with the DX10 extension in front, and
        /// hands the result to Parse - so every format path Parse already has applies unchanged.
        /// The un-aligning filled the DDS exactly on 880 of 880 real textures decoded end to end.
        /// Null when the format has no row rule here.
        /// </summary>
        public static EtsDds FromPackedSurface(byte[] surface, int width, int height, int dxgiFormat,
                                               int mips, int faces, bool cube, int pitchAlign, int imageAlign)
        {
            if (!RawLayout(dxgiFormat, out int blockBytes, out int pixelBytes) || surface == null)
            {
                return null;
            }

            mips = Math.Max(1, mips);
            faces = Math.Max(1, faces);
            pitchAlign = Math.Max(1, pitchAlign);
            imageAlign = Math.Max(1, imageAlign);

            // How long the surface is once the padding is gone.
            int unpadded = 0;

            for (int face = 0; face < faces; face++)
            {
                int w = width, h = height;

                for (int mip = 0; mip < mips; mip++)
                {
                    unpadded += RowBytes(blockBytes, pixelBytes, w) * RowCount(blockBytes, h);
                    w = Math.Max(w / 2, 1);
                    h = Math.Max(h / 2, 1);
                }
            }

            const int headerBytes = 148;
            var dds = new byte[headerBytes + unpadded];
            int s = 0, d = headerBytes;

            for (int face = 0; face < faces; face++)
            {
                int w = width, h = height;

                for (int mip = 0; mip < mips; mip++)
                {
                    s = (s + imageAlign - 1) / imageAlign * imageAlign;
                    int rowBytes = RowBytes(blockBytes, pixelBytes, w);
                    int rows = RowCount(blockBytes, h);

                    for (int r = 0; r < rows; r++)
                    {
                        s = (s + pitchAlign - 1) / pitchAlign * pitchAlign;

                        if (s + rowBytes > surface.Length)
                        {
                            return null;
                        }

                        Buffer.BlockCopy(surface, s, dds, d, rowBytes);
                        s += rowBytes;
                        d += rowBytes;
                    }

                    w = Math.Max(w / 2, 1);
                    h = Math.Max(h / 2, 1);
                }
            }

            // "DDS ", header size 124, height, width, mip count, pixel format of 32 bytes marked
            // FOURCC "DX10", caps2 cube when it is one; then the DX10 extension: format, 2D, the
            // cube flag, array size one.
            dds[0] = (byte)'D'; dds[1] = (byte)'D'; dds[2] = (byte)'S'; dds[3] = (byte)' ';
            WriteInt(dds, 4, 124);
            WriteInt(dds, 12, height);
            WriteInt(dds, 16, width);
            WriteInt(dds, 28, mips);
            WriteInt(dds, 76, 32);
            WriteInt(dds, 80, (int)DdpfFourCc);
            dds[84] = (byte)'D'; dds[85] = (byte)'X'; dds[86] = (byte)'1'; dds[87] = (byte)'0';
            WriteInt(dds, 112, cube ? (int)DdsCaps2CubeMap : 0);
            WriteInt(dds, 128, dxgiFormat);
            WriteInt(dds, 132, 3);
            WriteInt(dds, 136, cube ? 4 : 0);
            WriteInt(dds, 140, 1);

            return Parse(dds);
        }

        /// <summary>
        /// Bytes per 4x4 block for the block-compressed DXGI formats, bytes per pixel for the rest.
        /// False for a format Parse cannot take either, so it is reported rather than mis-decoded.
        /// </summary>
        private static bool RawLayout(int dxgi, out int blockBytes, out int pixelBytes)
        {
            blockBytes = 0;
            pixelBytes = 0;

            switch (dxgi)
            {
                case 71: case 72: case 80: blockBytes = 8; return true;                 // BC1, BC4
                case 74: case 75: case 77: case 78: case 83: case 98: case 99:           // BC2, BC3, BC5, BC7
                case 95:                                                                 // BC6H
                    blockBytes = 16; return true;
                case 28: case 29: case 87: case 88: case 91: case 93: pixelBytes = 4; return true;
                case 35: case 67: pixelBytes = 4; return true;                           // R16G16, R9G9B9E5
                case 49: case 56: case 85: case 86: case 115: pixelBytes = 2; return true; // R8G8, R16, 565, 5551, 4444
                case 10: case 11: pixelBytes = 8; return true;                           // RGBA 16-bit
                case 2: pixelBytes = 16; return true;                                    // RGBA 32-bit float
                case 61: case 65: pixelBytes = 1; return true;                           // R8, A8
                default: return false;
            }
        }

        private static int RowBytes(int blockBytes, int pixelBytes, int width)
        {
            return blockBytes > 0 ? Math.Max(1, (width + 3) / 4) * blockBytes : width * pixelBytes;
        }

        private static int RowCount(int blockBytes, int height)
        {
            return blockBytes > 0 ? Math.Max(1, (height + 3) / 4) : height;
        }

        private static void WriteInt(byte[] b, int o, int v)
        {
            b[o] = (byte)v;
            b[o + 1] = (byte)(v >> 8);
            b[o + 2] = (byte)(v >> 16);
            b[o + 3] = (byte)(v >> 24);
        }

        public static int BlockBytes(PixelFormat format)
        {
            switch (format)
            {
                case PixelFormat.Dxt1:
                case PixelFormat.Bc4:
                    return 8;
                case PixelFormat.Dxt5:
                case PixelFormat.Bc5:
                case PixelFormat.Bc7:
                case PixelFormat.Bc6h:
                    return 16;
                default:
                    return 0;
            }
        }

        /// <summary>Bytes per pixel of an uncompressed format as it is held in Data (4 for the RGBA/BGRA forms).</summary>
        public static int PixelBytes(PixelFormat format)
        {
            switch (format)
            {
                case PixelFormat.Rg16:
                case PixelFormat.R16:
                    return 2;
                case PixelFormat.RgbaHalf:
                case PixelFormat.Rgba64:
                    return 8;
                case PixelFormat.RgbaFloat:
                    return 16;
                case PixelFormat.Alpha8:
                    return 1;
                default:
                    return 4;
            }
        }

        public static int MipSize(PixelFormat format, int width, int height)
        {
            int block = BlockBytes(format);

            if (block > 0)
            {
                return Math.Max(1, (width + 3) / 4) * Math.Max(1, (height + 3) / 4) * block;
            }

            return Math.Max(1, width) * Math.Max(1, height) * PixelBytes(format);
        }

        /// <summary>Where mip level <paramref name="level"/> starts in Data, and its size in pixels (first face).</summary>
        public int LevelOffset(int level, out int width, out int height)
        {
            int offset = 0;
            width = Math.Max(1, Width);
            height = Math.Max(1, Height);

            for (int m = 0; m < level; m++)
            {
                offset += MipSize(Format, width, height);
                width = Math.Max(1, width / 2);
                height = Math.Max(1, height / 2);
            }

            return offset;
        }

        public static int ChainSize(PixelFormat format, int width, int height, int mips, int faces)
        {
            int total = 0;

            for (int m = 0; m < mips; m++)
            {
                total += MipSize(format, Math.Max(1, width >> m), Math.Max(1, height >> m));
            }

            return total * faces;
        }

        // ---------------------------------------------------------------- odd formats

        private static byte[] Expand24(byte[] src, int offset, int width, int height, ref int mips, bool bgr)
        {
            int total = 0;
            int available = src.Length - offset;
            int count = 0;

            for (int m = 0; m < mips; m++)
            {
                int w = Math.Max(1, width >> m), h = Math.Max(1, height >> m);

                if (total + w * h * 3 > available)
                {
                    break;
                }

                total += w * h * 3;
                count++;
            }

            mips = Math.Max(1, count);
            var dst = new byte[total / 3 * 4];
            int s = offset, d = 0;

            for (int i = 0; i < total / 3; i++)
            {
                byte b0 = src[s++], b1 = src[s++], b2 = src[s++];
                dst[d++] = bgr ? b2 : b0;
                dst[d++] = b1;
                dst[d++] = bgr ? b0 : b2;
                dst[d++] = 255;
            }

            return dst;
        }

        private static byte[] ExpandLuminance(byte[] src, int offset, int width, int height, ref int mips)
        {
            int total = 0;
            int available = src.Length - offset;
            int count = 0;

            for (int m = 0; m < mips; m++)
            {
                int w = Math.Max(1, width >> m), h = Math.Max(1, height >> m);

                if (total + w * h > available)
                {
                    break;
                }

                total += w * h;
                count++;
            }

            mips = Math.Max(1, count);
            var dst = new byte[total * 4];

            for (int i = 0; i < total; i++)
            {
                byte v = src[offset + i];
                dst[i * 4] = v;
                dst[i * 4 + 1] = v;
                dst[i * 4 + 2] = v;
                dst[i * 4 + 3] = 255;
            }

            return dst;
        }

        /// <summary>
        /// 16-bit packed colour to RGBA, every mip: B5G6R5 (DXGI 85, blue in the low bits),
        /// B5G5R5A1 (86) and B4G4R4A4 (115), little-endian words as D3D lays them out.
        /// </summary>
        private static byte[] Expand16(byte[] src, int offset, int width, int height, ref int mips, int dxgi)
        {
            int total = 0;
            int available = src.Length - offset;
            int count = 0;

            for (int m = 0; m < mips; m++)
            {
                int w = Math.Max(1, width >> m), h = Math.Max(1, height >> m);

                if ((total + w * h) * 2 > available)
                {
                    break;
                }

                total += w * h;
                count++;
            }

            mips = Math.Max(1, count);
            var dst = new byte[total * 4];

            for (int i = 0; i < total; i++)
            {
                int v = src[offset + i * 2] | (src[offset + i * 2 + 1] << 8);
                int r, g, b, a;

                if (dxgi == 85)
                {
                    r = (v >> 11) & 31; g = (v >> 5) & 63; b = v & 31;
                    r = (r << 3) | (r >> 2); g = (g << 2) | (g >> 4); b = (b << 3) | (b >> 2); a = 255;
                }
                else if (dxgi == 86)
                {
                    r = (v >> 10) & 31; g = (v >> 5) & 31; b = v & 31;
                    r = (r << 3) | (r >> 2); g = (g << 3) | (g >> 2); b = (b << 3) | (b >> 2); a = (v & 0x8000) != 0 ? 255 : 0;
                }
                else
                {
                    b = v & 15; g = (v >> 4) & 15; r = (v >> 8) & 15; a = (v >> 12) & 15;
                    r *= 17; g *= 17; b *= 17; a *= 17;
                }

                dst[i * 4] = (byte)r;
                dst[i * 4 + 1] = (byte)g;
                dst[i * 4 + 2] = (byte)b;
                dst[i * 4 + 3] = (byte)a;
            }

            return dst;
        }

        /// <summary>
        /// DXT3 to RGBA, every mip. DXT3 is DXT1 colour with four bits of explicit alpha per
        /// pixel in front of each block.
        /// </summary>
        private static byte[] DecodeDxt3Chain(byte[] src, int offset, int width, int height, ref int mips)
        {
            int available = src.Length - offset;
            int count = 0;
            int packed = 0;
            int unpacked = 0;

            for (int m = 0; m < mips; m++)
            {
                int w = Math.Max(1, width >> m), h = Math.Max(1, height >> m);
                int size = Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * 16;

                if (packed + size > available)
                {
                    break;
                }

                packed += size;
                unpacked += w * h * 4;
                count++;
            }

            mips = Math.Max(1, count);
            var dst = new byte[unpacked];
            int s = offset, d = 0;

            for (int m = 0; m < mips; m++)
            {
                int w = Math.Max(1, width >> m), h = Math.Max(1, height >> m);
                int bw = Math.Max(1, (w + 3) / 4), bh = Math.Max(1, (h + 3) / 4);

                for (int by = 0; by < bh; by++)
                {
                    for (int bx = 0; bx < bw; bx++)
                    {
                        ulong alphaBits = BitConverter.ToUInt64(src, s);
                        ushort c0 = BitConverter.ToUInt16(src, s + 8);
                        ushort c1 = BitConverter.ToUInt16(src, s + 10);
                        uint lookup = BitConverter.ToUInt32(src, s + 12);
                        s += 16;

                        Rgb565(c0, out int r0, out int g0, out int b0);
                        Rgb565(c1, out int r1, out int g1, out int b1);

                        for (int py = 0; py < 4; py++)
                        {
                            for (int px = 0; px < 4; px++)
                            {
                                int x = bx * 4 + px, y = by * 4 + py;

                                if (x >= w || y >= h)
                                {
                                    continue;
                                }

                                int index = (int)((lookup >> ((py * 4 + px) * 2)) & 3);
                                int r, g, b;

                                switch (index)
                                {
                                    case 0: r = r0; g = g0; b = b0; break;
                                    case 1: r = r1; g = g1; b = b1; break;
                                    case 2: r = (2 * r0 + r1) / 3; g = (2 * g0 + g1) / 3; b = (2 * b0 + b1) / 3; break;
                                    default: r = (r0 + 2 * r1) / 3; g = (g0 + 2 * g1) / 3; b = (b0 + 2 * b1) / 3; break;
                                }

                                int a = (int)((alphaBits >> ((py * 4 + px) * 4)) & 0xF) * 17;
                                int o = d + (y * w + x) * 4;
                                dst[o] = (byte)r;
                                dst[o + 1] = (byte)g;
                                dst[o + 2] = (byte)b;
                                dst[o + 3] = (byte)a;
                            }
                        }
                    }
                }

                d += w * h * 4;
            }

            return dst;
        }

        private static void Rgb565(ushort c, out int r, out int g, out int b)
        {
            r = ((c >> 11) & 0x1F) * 255 / 31;
            g = ((c >> 5) & 0x3F) * 255 / 63;
            b = (c & 0x1F) * 255 / 31;
        }
    }
}
