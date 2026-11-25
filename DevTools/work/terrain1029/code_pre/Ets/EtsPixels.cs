using System;

namespace SomniumSpace.Worlds.SomniumEuroTruck.Ets
{
    /// <summary>
    /// An image as plain bytes: RGBA, four bytes a pixel, rows from the TOP as the game's files
    /// store them. This is the form the UI compositor and the font loader work in, and it is
    /// what the harness writes to PNG to look at, so one decoder serves both.
    /// </summary>
    public sealed class EtsBitmap
    {
        public int Width;
        public int Height;
        public byte[] Rgba;

        public EtsBitmap(int width, int height)
        {
            Width = Math.Max(1, width);
            Height = Math.Max(1, height);
            Rgba = new byte[Width * Height * 4];
        }

        public int Offset(int x, int y)
        {
            return (y * Width + x) * 4;
        }
    }

    /// <summary>
    /// Unpacks the game's textures to plain RGBA on the CPU, for the few things that need the
    /// pixels themselves rather than a GPU upload: the UI compositor, which cuts window frames
    /// and buttons out of atlases the way the game's own markup does, the font loader, which
    /// has to turn a multi-channel distance field into the single channel TextMeshPro reads, and
    /// the decal shadows, whose strength is the red channel of whatever format they came in.
    ///
    /// DXT1, DXT5, BC4 and BC5 are unpacked here; BGRA and RGBA are copied; R8G8, R16G16, R16 and
    /// R9G9B9E5 are widened. BC6H and BC7 are not (null). Any mip level (DecodeLevel); Decode is
    /// the first. Pure C#, no Unity call, so it runs on a worker as well as the main thread.
    ///
    /// Channel conventions follow what a D3D sampler returns: a one-channel format reads as
    /// (r, 0, 0, 1) on the GPU but is given here as grey (r, r, r, 255), the way R8 always has
    /// been, because every CPU reader wants the value, not a red picture; two-channel formats
    /// are (r, g, 0, 255).
    /// </summary>
    public static class EtsPixels
    {
        /// <summary>The first mip as top-down RGBA, or null for a form not unpacked here.</summary>
        public static EtsBitmap Decode(EtsDds dds)
        {
            return DecodeLevel(dds, 0);
        }

        /// <summary>Mip <paramref name="level"/> as top-down RGBA, or null for a form not unpacked here.</summary>
        public static EtsBitmap DecodeLevel(EtsDds dds, int level)
        {
            if (dds == null || dds.IsCubeMap || dds.Data == null || level < 0 || level >= Math.Max(1, dds.MipCount))
            {
                return null;
            }

            int offset = dds.LevelOffset(level, out int width, out int height);

            switch (dds.Format)
            {
                case EtsDds.PixelFormat.Rgba32: return Copy(dds, offset, width, height, false);
                case EtsDds.PixelFormat.Bgra32: return Copy(dds, offset, width, height, true);
                case EtsDds.PixelFormat.Dxt1: return Block(dds, offset, width, height, false);
                case EtsDds.PixelFormat.Dxt5: return Block(dds, offset, width, height, true);
                case EtsDds.PixelFormat.Bc4: return Bc4(dds, offset, width, height, false);
                case EtsDds.PixelFormat.Bc5: return Bc4(dds, offset, width, height, true);
                case EtsDds.PixelFormat.Rg16:
                case EtsDds.PixelFormat.Rg32:
                case EtsDds.PixelFormat.R16:
                case EtsDds.PixelFormat.Alpha8:
                case EtsDds.PixelFormat.Rgba64:
                    return Widen(dds, offset, width, height);
                case EtsDds.PixelFormat.Rgb9e5: return SharedExponent(dds, offset, width, height);
                default: return null;
            }
        }

        /// <summary>
        /// The RED channel of mip <paramref name="level"/>, one byte a pixel, top-down: what a shader
        /// reading texture.r sees (before any sRGB decode). Null for a form not unpacked here.
        /// BC4 is read directly (no RGBA in between), being the common case.
        /// </summary>
        public static byte[] RedPlane(EtsDds dds, int level, out int width, out int height)
        {
            width = 0;
            height = 0;

            if (dds == null || dds.IsCubeMap || dds.Data == null || level < 0 || level >= Math.Max(1, dds.MipCount))
            {
                return null;
            }

            int offset = dds.LevelOffset(level, out width, out height);

            if (dds.Format == EtsDds.PixelFormat.Bc4 || dds.Format == EtsDds.PixelFormat.Bc5)
            {
                var red = new byte[width * height];
                int blockBytes = dds.Format == EtsDds.PixelFormat.Bc4 ? 8 : 16;
                int blocksX = Math.Max(1, (width + 3) / 4), blocksY = Math.Max(1, (height + 3) / 4);
                var palette = new byte[8];
                byte[] d = dds.Data;

                for (int by = 0; by < blocksY; by++)
                {
                    for (int bx = 0; bx < blocksX; bx++)
                    {
                        int at = offset + (by * blocksX + bx) * blockBytes;

                        if (at + 8 > d.Length)
                        {
                            return red;
                        }

                        AlphaPalette(d[at], d[at + 1], palette);
                        ulong bits = 0;

                        for (int i = 0; i < 6; i++)
                        {
                            bits |= (ulong)d[at + 2 + i] << (8 * i);
                        }

                        for (int texel = 0; texel < 16; texel++)
                        {
                            int x = bx * 4 + (texel & 3), y = by * 4 + (texel >> 2);

                            if (x < width && y < height)
                            {
                                red[y * width + x] = palette[(int)((bits >> (3 * texel)) & 7)];
                            }
                        }
                    }
                }

                return red;
            }

            EtsBitmap image = DecodeLevel(dds, level);

            if (image == null)
            {
                return null;
            }

            var plane = new byte[image.Width * image.Height];

            for (int i = 0; i < plane.Length; i++)
            {
                plane[i] = image.Rgba[i * 4];
            }

            return plane;
        }

        private static EtsBitmap Copy(EtsDds dds, int offset, int width, int height, bool bgra)
        {
            var image = new EtsBitmap(width, height);
            int count = Math.Min(image.Width * image.Height * 4, dds.Data.Length - offset);

            for (int i = 0; i + 3 < count; i += 4)
            {
                image.Rgba[i] = bgra ? dds.Data[offset + i + 2] : dds.Data[offset + i];
                image.Rgba[i + 1] = dds.Data[offset + i + 1];
                image.Rgba[i + 2] = bgra ? dds.Data[offset + i] : dds.Data[offset + i + 2];
                image.Rgba[i + 3] = dds.Data[offset + i + 3];
            }

            return image;
        }

        /// <summary>The eight-entry palette of a BC3 alpha / BC4 / BC5 channel block from its two endpoints.</summary>
        private static void AlphaPalette(int a0, int a1, byte[] palette)
        {
            palette[0] = (byte)a0;
            palette[1] = (byte)a1;

            if (a0 > a1)
            {
                for (int i = 1; i < 7; i++) palette[i + 1] = (byte)(((7 - i) * a0 + i * a1) / 7);
            }
            else
            {
                for (int i = 1; i < 5; i++) palette[i + 1] = (byte)(((5 - i) * a0 + i * a1) / 5);
                palette[6] = 0;
                palette[7] = 255;
            }
        }

        private static EtsBitmap Block(EtsDds dds, int offset, int width, int height, bool dxt5)
        {
            var image = new EtsBitmap(width, height);
            int blocksX = Math.Max(1, (width + 3) / 4), blocksY = Math.Max(1, (height + 3) / 4);
            int blockBytes = dxt5 ? 16 : 8;
            var palette = new byte[16];
            var alpha = new byte[8];
            byte[] d = dds.Data;

            for (int by = 0; by < blocksY; by++)
            {
                for (int bx = 0; bx < blocksX; bx++)
                {
                    int at = offset + (by * blocksX + bx) * blockBytes;

                    if (at + blockBytes > d.Length)
                    {
                        return image;
                    }

                    int colourAt = dxt5 ? at + 8 : at;

                    if (dxt5)
                    {
                        AlphaPalette(d[at], d[at + 1], alpha);
                    }

                    int c0 = d[colourAt] | (d[colourAt + 1] << 8);
                    int c1 = d[colourAt + 2] | (d[colourAt + 3] << 8);
                    Expand565(c0, palette, 0);
                    Expand565(c1, palette, 4);
                    bool fourColours = dxt5 || c0 > c1;

                    for (int k = 0; k < 3; k++)
                    {
                        int p0 = palette[k], p1 = palette[4 + k];
                        palette[8 + k] = fourColours ? (byte)((2 * p0 + p1) / 3) : (byte)((p0 + p1) / 2);
                        palette[12 + k] = fourColours ? (byte)((p0 + 2 * p1) / 3) : (byte)0;
                    }

                    palette[3] = 255;
                    palette[7] = 255;
                    palette[11] = 255;
                    palette[15] = fourColours ? (byte)255 : (byte)0;

                    ulong alphaBits = 0;

                    if (dxt5)
                    {
                        for (int i = 0; i < 6; i++) alphaBits |= (ulong)d[at + 2 + i] << (8 * i);
                    }

                    for (int texel = 0; texel < 16; texel++)
                    {
                        int x = bx * 4 + (texel & 3), y = by * 4 + (texel >> 2);

                        if (x >= image.Width || y >= image.Height)
                        {
                            continue;
                        }

                        int index = (d[colourAt + 4 + (texel >> 2)] >> ((texel & 3) * 2)) & 3;
                        int o = image.Offset(x, y);
                        image.Rgba[o] = palette[index * 4];
                        image.Rgba[o + 1] = palette[index * 4 + 1];
                        image.Rgba[o + 2] = palette[index * 4 + 2];
                        image.Rgba[o + 3] = dxt5 ? alpha[(int)((alphaBits >> (3 * texel)) & 7)] : palette[index * 4 + 3];
                    }
                }
            }

            return image;
        }

        /// <summary>BC4 (grey) or BC5 (red, green, 0): one or two BC3-alpha-style channel blocks per 4x4.</summary>
        private static EtsBitmap Bc4(EtsDds dds, int offset, int width, int height, bool bc5)
        {
            var image = new EtsBitmap(width, height);
            int blocksX = Math.Max(1, (width + 3) / 4), blocksY = Math.Max(1, (height + 3) / 4);
            int blockBytes = bc5 ? 16 : 8;
            var red = new byte[8];
            var green = new byte[8];
            byte[] d = dds.Data;

            for (int by = 0; by < blocksY; by++)
            {
                for (int bx = 0; bx < blocksX; bx++)
                {
                    int at = offset + (by * blocksX + bx) * blockBytes;

                    if (at + blockBytes > d.Length)
                    {
                        return image;
                    }

                    AlphaPalette(d[at], d[at + 1], red);
                    ulong redBits = 0, greenBits = 0;

                    for (int i = 0; i < 6; i++) redBits |= (ulong)d[at + 2 + i] << (8 * i);

                    if (bc5)
                    {
                        AlphaPalette(d[at + 8], d[at + 9], green);
                        for (int i = 0; i < 6; i++) greenBits |= (ulong)d[at + 10 + i] << (8 * i);
                    }

                    for (int texel = 0; texel < 16; texel++)
                    {
                        int x = bx * 4 + (texel & 3), y = by * 4 + (texel >> 2);

                        if (x >= image.Width || y >= image.Height)
                        {
                            continue;
                        }

                        int o = image.Offset(x, y);
                        byte r = red[(int)((redBits >> (3 * texel)) & 7)];
                        image.Rgba[o] = r;
                        image.Rgba[o + 1] = bc5 ? green[(int)((greenBits >> (3 * texel)) & 7)] : r;
                        image.Rgba[o + 2] = bc5 ? (byte)0 : r;
                        image.Rgba[o + 3] = 255;
                    }
                }
            }

            return image;
        }

        /// <summary>The 8- and 16-bit unorm channel formats, narrowed to bytes (16-bit keeps the high byte).</summary>
        private static EtsBitmap Widen(EtsDds dds, int offset, int width, int height)
        {
            var image = new EtsBitmap(width, height);
            byte[] d = dds.Data;
            int n = image.Width * image.Height;
            int stride = EtsDds.PixelBytes(dds.Format);

            for (int i = 0; i < n; i++)
            {
                int s = offset + i * stride;

                if (s + stride > d.Length)
                {
                    break;
                }

                int o = i * 4;

                switch (dds.Format)
                {
                    case EtsDds.PixelFormat.Rg16:
                        image.Rgba[o] = d[s]; image.Rgba[o + 1] = d[s + 1]; image.Rgba[o + 2] = 0; image.Rgba[o + 3] = 255;
                        break;
                    case EtsDds.PixelFormat.Rg32:
                        image.Rgba[o] = d[s + 1]; image.Rgba[o + 1] = d[s + 3]; image.Rgba[o + 2] = 0; image.Rgba[o + 3] = 255;
                        break;
                    case EtsDds.PixelFormat.R16:
                        image.Rgba[o] = image.Rgba[o + 1] = image.Rgba[o + 2] = d[s + 1]; image.Rgba[o + 3] = 255;
                        break;
                    case EtsDds.PixelFormat.Alpha8:
                        image.Rgba[o] = image.Rgba[o + 1] = image.Rgba[o + 2] = 0; image.Rgba[o + 3] = d[s];
                        break;
                    default: // Rgba64
                        image.Rgba[o] = d[s + 1]; image.Rgba[o + 1] = d[s + 3]; image.Rgba[o + 2] = d[s + 5]; image.Rgba[o + 3] = d[s + 7];
                        break;
                }
            }

            return image;
        }

        /// <summary>
        /// R9G9B9E5_SHAREDEXP: three 9-bit mantissas and a shared 5-bit exponent (bias 15), linear
        /// HDR. Clamped to 1 and sRGB-encoded, which is what a picture of it wants.
        /// </summary>
        private static EtsBitmap SharedExponent(EtsDds dds, int offset, int width, int height)
        {
            var image = new EtsBitmap(width, height);
            byte[] d = dds.Data;
            int n = image.Width * image.Height;

            for (int i = 0; i < n; i++)
            {
                int s = offset + i * 4;

                if (s + 4 > d.Length)
                {
                    break;
                }

                uint v = (uint)(d[s] | (d[s + 1] << 8) | (d[s + 2] << 16) | (d[s + 3] << 24));
                float scale = ExponentScale[v >> 27];
                int o = i * 4;
                image.Rgba[o] = Encode((v & 511) * scale);
                image.Rgba[o + 1] = Encode(((v >> 9) & 511) * scale);
                image.Rgba[o + 2] = Encode(((v >> 18) & 511) * scale);
                image.Rgba[o + 3] = 255;
            }

            return image;
        }

        /// <summary>2^(e - 15 - 9) for the 32 shared exponents.</summary>
        private static readonly float[] ExponentScale = BuildExponentScale();

        private static float[] BuildExponentScale()
        {
            var table = new float[32];

            for (int e = 0; e < 32; e++)
            {
                table[e] = (float)Math.Pow(2.0, e - 15 - 9);
            }

            return table;
        }

        /// <summary>Linear 0..1 in 4096 steps to an sRGB byte.</summary>
        private static readonly byte[] SrgbEncode = BuildSrgbEncode();

        private static byte[] BuildSrgbEncode()
        {
            var table = new byte[4096];

            for (int i = 0; i < table.Length; i++)
            {
                double c = i / 4095.0;
                c = c <= 0.0031308 ? c * 12.92 : 1.055 * Math.Pow(c, 1.0 / 2.4) - 0.055;
                table[i] = (byte)(c * 255.0 + 0.5);
            }

            return table;
        }

        /// <summary>Unpacks one R9G9B9E5 texel to linear floats.</summary>
        public static void SharedExponentToLinear(uint v, out float r, out float g, out float b)
        {
            float scale = ExponentScale[v >> 27];
            r = (v & 511) * scale;
            g = ((v >> 9) & 511) * scale;
            b = ((v >> 18) & 511) * scale;
        }

        private static byte Encode(float linear)
        {
            float c = linear <= 0f ? 0f : linear >= 1f ? 1f : linear;
            return SrgbEncode[(int)(c * 4095f + 0.5f)];
        }

        private static void Expand565(int c, byte[] into, int at)
        {
            int r = (c >> 11) & 31, g = (c >> 5) & 63, b = c & 31;
            into[at] = (byte)((r << 3) | (r >> 2));
            into[at + 1] = (byte)((g << 2) | (g >> 4));
            into[at + 2] = (byte)((b << 3) | (b >> 2));
            into[at + 3] = 255;
        }
    }
}
