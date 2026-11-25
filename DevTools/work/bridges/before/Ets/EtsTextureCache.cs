using System;
using System.Collections.Generic;
using UnityEngine;

namespace SomniumSpace.Worlds.SomniumEuroTruck.Ets
{
    /// <summary>
    /// Turns the game's textures into Unity textures, once each.
    ///
    /// A material names a tobj; the tobj names a DDS; the DDS holds compressed pixels in a format
    /// the GPU reads as-is. So the whole chain is two archive reads and one upload, with no
    /// decoding in between for the common formats - which is what makes loading a truck with
    /// sixty textures take a fraction of a second rather than a minute.
    /// </summary>
    public sealed class EtsTextureCache
    {
        private readonly EtsFileSystem _files;
        private readonly Dictionary<string, Texture2D> _byPath = new Dictionary<string, Texture2D>(StringComparer.Ordinal);
        private readonly HashSet<string> _reported = new HashSet<string>(StringComparer.Ordinal);

        public int Count => _byPath.Count;

        public EtsTextureCache(EtsFileSystem files)
        {
            _files = files;
        }

        /// <summary>
        /// The texture a tobj points at. Null when the tobj or its DDS is missing or in a form
        /// this cannot upload, in which case the reason is logged once per path.
        /// </summary>
        /// <param name="tobjPath">Absolute archive path of the tobj.</param>
        /// <param name="linear">True for data textures - normal maps - that must not be treated
        /// as sRGB colour.</param>
        public Texture2D Load(string tobjPath, bool linear)
        {
            if (string.IsNullOrEmpty(tobjPath))
            {
                return null;
            }

            string key = "/" + EtsHashFs.Normalize(tobjPath) + (linear ? "|linear" : "");

            if (_byPath.TryGetValue(key, out Texture2D cached))
            {
                return cached;
            }

            Texture2D texture = null;

            try
            {
                texture = Build(tobjPath, linear);
            }
            catch (Exception e)
            {
                Report(tobjPath, e.Message);
            }

            _byPath[key] = texture;
            return texture;
        }

        /// <summary>
        /// Game 1.61 on stores no tobj and no DDS as files: every .tobj path is a packed image in a
        /// HashFS v2 archive, its pixels GDeflate-compressed and its geometry and address modes in
        /// the archive's own metadata. True when the path is one, with the texture in dds - or dds
        /// null and the reason in failure when its format cannot be read here. False on a HashFS v1
        /// install, where the tobj -> DDS path below runs exactly as it always has.
        /// </summary>
        private static bool TryReadPacked(EtsFileSystem files, string tobjPath, out EtsDds dds, out bool repeatU, out bool repeatV, out string failure)
        {
            dds = null;
            repeatU = true;
            repeatV = true;
            failure = null;

            if (!files.TryGetEntry(tobjPath, out EtsHashFs archive, out EtsHashFs.Entry entry) || !entry.IsPackedImage)
            {
                return false;
            }

            byte[] surface = archive.ReadPackedImage(entry);
            dds = EtsDds.FromPackedSurface(surface, entry.ImageWidth, entry.ImageHeight, entry.DxgiFormat,
                                           entry.MipCount, entry.FaceCount, entry.IsCubeMap,
                                           entry.PitchAlign, entry.ImageAlign);

            if (dds == null)
            {
                failure = "packed image in DXGI format " + entry.DxgiFormat + " is not supported";
                return true;
            }

            // Bits 4-6 and 7-9 of the sample flags are the address modes in the tobj's own
            // numbering, so the rule is RepeatU's: repeat and mirror wrap, the clamps clamp.
            int addressU = (int)((entry.SampleFlags >> 4) & 7);
            int addressV = (int)((entry.SampleFlags >> 7) & 7);
            repeatU = addressU == EtsTobj.AddressRepeat || addressU == EtsTobj.AddressMirror;
            repeatV = addressV == EtsTobj.AddressRepeat || addressV == EtsTobj.AddressMirror;
            return true;
        }

        /// <summary>
        /// The pixels a tobj path names, as a DDS, whichever way the install stores them: a packed
        /// image in a HashFS v2 archive (game 1.61 on) or a tobj pointing at a DDS file (1.48).
        /// Null when absent or unreadable. For the CPU readers - the font and the game's UI images -
        /// that decode pixels themselves rather than upload them.
        /// </summary>
        public static EtsDds ReadDds(EtsFileSystem files, string tobjPath)
        {
            if (TryReadPacked(files, tobjPath, out EtsDds packed, out _, out _, out _))
            {
                return packed;
            }

            byte[] tobj = files.Read(tobjPath);

            if (tobj == null)
            {
                return null;
            }

            byte[] dds = files.Read(EtsTobj.Parse(tobj, tobjPath).TexturePath);
            return dds == null ? null : EtsDds.Parse(dds);
        }

        /// <summary>
        /// The CPU half of loading a texture - archive read, GDeflate, surface layout, the mean
        /// alpha and, when asked, the reflection mask's alpha plane - with no Unity call in it, so
        /// a worker thread can do it (EtsWorker; the traffic's cars pull in 144 textures whose read
        /// costs 508 ms, up to 50 ms for one, harness Program1401). Adopt then uploads it on the
        /// main thread. Load does exactly these two halves back to back.
        /// </summary>
        public sealed class Prepared
        {
            public string Path;
            public bool Linear;

            /// <summary>The pixels to upload; null when there is nothing to (see Failure; a cube map has neither).</summary>
            public EtsDds Dds;

            public bool RepeatU = true, RepeatV = true;
            public string Name;
            public string Failure;
            public float MeanAlpha = 1f;
            public bool HasMeanAlpha;

            /// <summary>Set when the mask plane was asked for (withMask): null Mask.Alpha means the texture has no usable mask.</summary>
            public MaskPlane Mask;
        }

        /// <summary>The alpha plane a reflection mask is made from: the first mip no larger than MaskSize.</summary>
        public sealed class MaskPlane
        {
            public byte[] Alpha;
            public int Width, Height;
            public string Name;
            public bool RepeatU, RepeatV;
        }

        private readonly Dictionary<string, MaskPlane> _maskPlanes = new Dictionary<string, MaskPlane>(StringComparer.Ordinal);

        private static string Key(string tobjPath, bool linear)
        {
            return "/" + EtsHashFs.Normalize(tobjPath) + (linear ? "|linear" : "");
        }

        /// <summary>Whether Load would find this texture without reading it.</summary>
        public bool IsLoaded(string tobjPath, bool linear)
        {
            return string.IsNullOrEmpty(tobjPath) || _byPath.ContainsKey(Key(tobjPath, linear));
        }

        /// <summary>
        /// Whether the reflection-mask plane of this texture is kept already (a Prepare with the mask adopted, or a
        /// LoadReflectionMask that read it), so LoadReflectionMask reads nothing from the archives for it.
        /// </summary>
        public bool HasMaskPlane(string tobjPath)
        {
            return string.IsNullOrEmpty(tobjPath) || _maskPlanes.ContainsKey("/" + EtsHashFs.Normalize(tobjPath));
        }

        /// <summary>Pure: see Prepared. Never throws; a failure is carried in the result.</summary>
        public static Prepared Prepare(EtsFileSystem files, string tobjPath, bool linear, bool withMask)
        {
            var prepared = new Prepared { Path = tobjPath, Linear = linear };

            try
            {
                EtsDds dds;
                bool repeatU, repeatV;
                string name;

                if (TryReadPacked(files, tobjPath, out dds, out repeatU, out repeatV, out string packedFailure))
                {
                    if (dds == null)
                    {
                        prepared.Failure = packedFailure;
                        return prepared;
                    }

                    name = tobjPath;
                }
                else
                {
                    byte[] tobjBytes = files.Read(tobjPath);

                    if (tobjBytes == null)
                    {
                        prepared.Failure = "tobj not found";
                        return prepared;
                    }

                    EtsTobj tobj = EtsTobj.Parse(tobjBytes, tobjPath);
                    byte[] ddsBytes = files.Read(tobj.TexturePath);

                    if (ddsBytes == null)
                    {
                        prepared.Failure = "DDS not found: " + tobj.TexturePath;
                        return prepared;
                    }

                    dds = EtsDds.Parse(ddsBytes);
                    repeatU = tobj.RepeatU;
                    repeatV = tobj.RepeatV;
                    name = tobj.TexturePath;
                }

                prepared.RepeatU = repeatU;
                prepared.RepeatV = repeatV;
                prepared.Name = name;

                if (withMask)
                {
                    prepared.Mask = PlaneOf(dds, name, repeatU, repeatV) ?? new MaskPlane();
                }

                if (dds.IsCubeMap)
                {
                    // Environment maps for reflections. Nothing here reflects yet.
                    return prepared;
                }

                // The specular mask, while the pixels are in memory (see MeanAlpha).
                if (!linear)
                {
                    prepared.MeanAlpha = MeanAlphaOf(dds);
                    prepared.HasMeanAlpha = true;
                }

                prepared.Dds = dds;
            }
            catch (Exception e)
            {
                prepared.Dds = null;
                prepared.Failure = e.Message;
            }

            return prepared;
        }

        /// <summary>
        /// Main thread: a Prepared texture into the cache, uploaded - unless the cache has it
        /// already, which is then returned. Its mask plane is kept for LoadReflectionMask.
        /// </summary>
        public Texture2D Adopt(Prepared prepared)
        {
            if (prepared == null || string.IsNullOrEmpty(prepared.Path))
            {
                return null;
            }

            if (prepared.Mask != null)
            {
                _maskPlanes["/" + EtsHashFs.Normalize(prepared.Path)] = prepared.Mask;
            }

            string key = Key(prepared.Path, prepared.Linear);

            if (_byPath.TryGetValue(key, out Texture2D cached))
            {
                return cached;
            }

            Texture2D texture = null;

            try
            {
                texture = Upload(prepared);
            }
            catch (Exception e)
            {
                Report(prepared.Path, e.Message);
            }

            _byPath[key] = texture;
            return texture;
        }

        private Texture2D Build(string tobjPath, bool linear)
        {
            return Upload(Prepare(_files, tobjPath, linear, false));
        }

        private Texture2D Upload(Prepared prepared)
        {
            string tobjPath = prepared.Path;
            bool linear = prepared.Linear;

            if (prepared.Failure != null)
            {
                Report(tobjPath, prepared.Failure);
                return null;
            }

            EtsDds dds = prepared.Dds;

            if (dds == null)
            {
                // A cube map: nothing here reflects yet.
                return null;
            }

            bool repeatU = prepared.RepeatU, repeatV = prepared.RepeatV;
            string name = prepared.Name;
            TextureFormat format;
            byte[] pixels = dds.Data;

            switch (dds.Format)
            {
                case EtsDds.PixelFormat.Dxt1: format = TextureFormat.DXT1; break;
                case EtsDds.PixelFormat.Dxt5: format = TextureFormat.DXT5; break;
                case EtsDds.PixelFormat.Bc4: format = TextureFormat.BC4; break;
                case EtsDds.PixelFormat.Bc5: format = TextureFormat.BC5; break;
                case EtsDds.PixelFormat.Bc7: format = TextureFormat.BC7; break;
                case EtsDds.PixelFormat.Rgba32: format = TextureFormat.RGBA32; break;
                case EtsDds.PixelFormat.Bgra32: format = TextureFormat.BGRA32; break;
                default:
                    // The formats that are not plain 8-bit colour or BCn: DXGI 35 R16G16 (the truck
                    // paint normal maps), 67 R9G9B9E5 and the rest EtsDds reads. Each goes up as the GPU
                    // format it is when the device samples it, else widened here once (see Widened).
                    if (!UploadFormat(dds, out format, ref pixels, out string why))
                    {
                        Report(tobjPath, why);
                        return null;
                    }

                    // Once per kind, so a log shows the new formats arriving (e.g. the truck paint's R16G16).
                    if (_formatsLogged.Add((int)dds.Format))
                    {
                        Debug.Log("[SomniumEuroTruck] texture: DXGI " + dds.Dxgi + " (" + dds.Format + ") uploaded as " + format + ", first " + tobjPath);
                    }

                    break;
            }

            // The specular mask, worked out while the pixels were in memory (see MeanAlpha).
            if (prepared.HasMeanAlpha)
            {
                _meanAlpha["/" + EtsHashFs.Normalize(tobjPath)] = prepared.MeanAlpha;
            }

            // Anisotropy: the client runs AnisotropicFiltering.Enable (Player.log "Set texture
            // filter quality: 1" -> GraphicsControl.cs:446), so each texture's own level counts.
            // Roads and ground are seen at a few degrees from a cab; 8 keeps them sharp into the
            // distance where 4 blurs them. Normal maps stay at 4.
            var texture = new Texture2D(dds.Width, dds.Height, format, dds.MipCount, linear)
            {
                name = name,
                wrapModeU = repeatU ? TextureWrapMode.Repeat : TextureWrapMode.Clamp,
                wrapModeV = repeatV ? TextureWrapMode.Repeat : TextureWrapMode.Clamp,
                filterMode = FilterMode.Trilinear,
                anisoLevel = linear ? 4 : 8
            };

            texture.LoadRawTextureData(pixels);

            // Uploaded and then dropped from managed memory: the truck alone is tens of
            // megabytes of pixels, and nothing reads them back.
            texture.Apply(false, true);
            return texture;
        }

        /// <summary>
        /// The Unity format for the pixel formats beyond 8-bit colour and BCn, and the bytes to load.
        /// Measured need (ProgramTexCensus): R16G16_UNORM carries eleven normal maps - carpaint_n of
        /// the DAF XF and Volvo FH16 2012, carpaint_normal(_16) of the Actros 2014 and Iveco Hi-Way,
        /// chrome, lamp and door-handle maps - on 23 materials, truckpaint.tsnmap among them. The
        /// game's normal evaluators read .xy * 2 - 1 and rebuild z (/effect/eut2/_shd/be733167...
        /// .glsl.fso), exactly what URP's UnpackNormalRGorAG does with (r, g, 0, 1), so RG32 needs no
        /// swizzle; on a device without it the pair is narrowed to RG16 (8 bits a channel) or RGBA32.
        /// Main thread (SystemInfo); the widening is a one-off copy.
        /// </summary>
        private static readonly HashSet<int> _formatsLogged = new HashSet<int>();

        private static bool UploadFormat(EtsDds dds, out TextureFormat format, ref byte[] pixels, out string why)
        {
            why = null;
            format = TextureFormat.RGBA32;

            switch (dds.Format)
            {
                case EtsDds.PixelFormat.Rg32:
                    if (SystemInfo.SupportsTextureFormat(TextureFormat.RG32)) { format = TextureFormat.RG32; return true; }
                    if (SystemInfo.SupportsTextureFormat(TextureFormat.RG16)) { format = TextureFormat.RG16; pixels = Narrow16(pixels, 2, 2); return true; }
                    pixels = Widened(dds); return pixels != null;

                case EtsDds.PixelFormat.Rgb9e5:
                    if (SystemInfo.SupportsTextureFormat(TextureFormat.RGB9e5Float)) { format = TextureFormat.RGB9e5Float; return true; }
                    if (SystemInfo.SupportsTextureFormat(TextureFormat.RGBAHalf)) { format = TextureFormat.RGBAHalf; pixels = SharedExponentToHalf(pixels); return true; }
                    pixels = Widened(dds); return pixels != null;

                case EtsDds.PixelFormat.Rg16:
                    if (SystemInfo.SupportsTextureFormat(TextureFormat.RG16)) { format = TextureFormat.RG16; return true; }
                    pixels = Widened(dds); return pixels != null;

                case EtsDds.PixelFormat.R16:
                    if (SystemInfo.SupportsTextureFormat(TextureFormat.R16)) { format = TextureFormat.R16; return true; }
                    pixels = Widened(dds); return pixels != null;

                case EtsDds.PixelFormat.Rgba64:
                    if (SystemInfo.SupportsTextureFormat(TextureFormat.RGBA64)) { format = TextureFormat.RGBA64; return true; }
                    pixels = Widened(dds); return pixels != null;

                case EtsDds.PixelFormat.Alpha8:
                    if (SystemInfo.SupportsTextureFormat(TextureFormat.Alpha8)) { format = TextureFormat.Alpha8; return true; }
                    pixels = Widened(dds); return pixels != null;

                case EtsDds.PixelFormat.RgbaHalf:
                    format = TextureFormat.RGBAHalf;
                    if (SystemInfo.SupportsTextureFormat(format)) return true;
                    why = "RGBA half-float textures are not supported on this device";
                    return false;

                case EtsDds.PixelFormat.RgbaFloat:
                    format = TextureFormat.RGBAFloat;
                    if (SystemInfo.SupportsTextureFormat(format)) return true;
                    why = "RGBA float textures are not supported on this device";
                    return false;

                case EtsDds.PixelFormat.Bc6h:
                    format = TextureFormat.BC6H;
                    if (SystemInfo.SupportsTextureFormat(format)) return true;
                    why = "BC6H is not supported on this device";
                    return false;

                default:
                    why = "unsupported pixel format " + dds.Format;
                    return false;
            }
        }

        /// <summary>Every mip decoded to RGBA32 (EtsPixels), for a device that cannot sample the format itself.</summary>
        private static byte[] Widened(EtsDds dds)
        {
            int total = 0;

            for (int m = 0; m < dds.MipCount; m++)
            {
                dds.LevelOffset(m, out int w, out int h);
                total += w * h * 4;
            }

            var rgba = new byte[total];
            int at = 0;

            for (int m = 0; m < dds.MipCount; m++)
            {
                EtsBitmap level = EtsPixels.DecodeLevel(dds, m);

                if (level == null)
                {
                    return null;
                }

                Buffer.BlockCopy(level.Rgba, 0, rgba, at, level.Rgba.Length);
                at += level.Rgba.Length;
            }

            return rgba;
        }

        /// <summary>16-bit little-endian channels narrowed to their high byte: <paramref name="channels"/> of <paramref name="bytesEach"/> bytes a pixel.</summary>
        private static byte[] Narrow16(byte[] data, int channels, int bytesEach)
        {
            int count = data.Length / bytesEach;
            var narrow = new byte[count];

            for (int i = 0; i < count; i++)
            {
                narrow[i] = data[i * bytesEach + 1];
            }

            return narrow;
        }

        /// <summary>R9G9B9E5 to RGBA half floats, alpha 1.</summary>
        private static byte[] SharedExponentToHalf(byte[] data)
        {
            int count = data.Length / 4;
            var half = new byte[count * 8];

            for (int i = 0; i < count; i++)
            {
                uint v = (uint)(data[i * 4] | (data[i * 4 + 1] << 8) | (data[i * 4 + 2] << 16) | (data[i * 4 + 3] << 24));
                EtsPixels.SharedExponentToLinear(v, out float r, out float g, out float b);
                WriteHalf(half, i * 8, r);
                WriteHalf(half, i * 8 + 2, g);
                WriteHalf(half, i * 8 + 4, b);
                WriteHalf(half, i * 8 + 6, 1f);
            }

            return half;
        }

        private static void WriteHalf(byte[] into, int at, float value)
        {
            ushort h = Mathf.FloatToHalf(value);
            into[at] = (byte)h;
            into[at + 1] = (byte)(h >> 8);
        }

        // ------------------------------------------------------------- specular masks

        private readonly Dictionary<string, float> _meanAlpha = new Dictionary<string, float>(StringComparer.Ordinal);

        /// <summary>
        /// The mean of a colour texture's alpha plane, 0..1: the game's specular mask for the
        /// dif.spec family (/effect/eut2/_shd/30a5626a4b2701c1ec1f7fcb719381da.glsl.fso writes
        /// specular = saturate(base.a * s + b) * specular colour). 1 when the format carries no
        /// alpha or one not unpacked here (DXT1, BC7), which leaves those materials as they were.
        /// Measured (harness Program966): asphalt_tiles_eu_01 0.000, ger_01 0.042, asphalt 0.054,
        /// tradeaux 0.054, roof_001 0.486, buildings_002 0.296.
        /// </summary>
        public float MeanAlpha(string tobjPath)
        {
            if (string.IsNullOrEmpty(tobjPath))
            {
                return 1f;
            }

            Load(tobjPath, false);
            return _meanAlpha.TryGetValue("/" + EtsHashFs.Normalize(tobjPath), out float mean) ? mean : 1f;
        }

        private static float MeanAlphaOf(EtsDds dds)
        {
            if (dds == null || dds.IsCubeMap ||
                (dds.Format != EtsDds.PixelFormat.Dxt5 && dds.Format != EtsDds.PixelFormat.Rgba32 && dds.Format != EtsDds.PixelFormat.Bgra32))
            {
                return 1f;
            }

            int width = dds.Width, height = dds.Height, offset = 0, level = 0;

            while (level + 1 < dds.MipCount && (width > MaskSize || height > MaskSize))
            {
                offset += EtsDds.MipSize(dds.Format, width, height);
                width = Mathf.Max(1, width / 2);
                height = Mathf.Max(1, height / 2);
                level++;
            }

            byte[] alpha = ReadAlpha(dds, offset, width, height);

            if (alpha == null || alpha.Length == 0)
            {
                return 1f;
            }

            long sum = 0;

            foreach (byte a in alpha)
            {
                sum += a;
            }

            return sum / (255f * alpha.Length);
        }

        // ------------------------------------------------------------- decal shadows

        /// <summary>
        /// The texture of an eut2.decalshadow piece as the decal shader needs it: the game's
        /// shader reads the shadow's strength from the RED channel and draws black with that as
        /// alpha. Returned as 0,0,0,v in every mip, linear - it is a mask, not a colour.
        ///
        /// Whatever the file's format: of the 32 decalshadow materials in the demo, 27 are BC4
        /// (DXGI 80 - every truck interior's shadows.tobj, the Actros decal, the Cardiff bridge),
        /// 3 are R8 (the factory yards), one BC1 and one BC3 (census, 2026-09-23). Only R8 was read
        /// before ("decal shadow: not a one-channel texture (Bc4)" / "(Dxt5)"), so the other 29 were
        /// left out and a cab's baked contact shadows with them. The red plane is unpacked on the
        /// CPU (EtsPixels.RedPlane) from the first mip no larger than ShadowSize - these are soft
        /// shadows, and it bounds the one-off cost on the frame (about 1.4 M texels at most).
        /// </summary>
        public Texture2D LoadAlphaFromRed(string tobjPath)
        {
            if (string.IsNullOrEmpty(tobjPath))
            {
                return null;
            }

            string key = ShadowKey(tobjPath);

            if (_byPath.TryGetValue(key, out Texture2D cached))
            {
                return cached;
            }

            Texture2D texture = UploadShadow(PrepareAlphaFromRed(_files, tobjPath));
            _byPath[key] = texture;
            return texture;
        }

        private static string ShadowKey(string tobjPath)
        {
            return "/" + EtsHashFs.Normalize(tobjPath) + "|alpha-from-red";
        }

        /// <summary>Whether LoadAlphaFromRed would find this decal shadow without reading it.</summary>
        public bool IsAlphaFromRedLoaded(string tobjPath)
        {
            return string.IsNullOrEmpty(tobjPath) || _byPath.ContainsKey(ShadowKey(tobjPath));
        }

        /// <summary>The CPU half of LoadAlphaFromRed (read, GDeflate, red plane per mip), for a worker thread; AdoptAlphaFromRed uploads it.</summary>
        public sealed class PreparedShadow
        {
            public string Path;
            public byte[] Data;
            public int Width, Height, Levels;
            public bool RepeatU = true, RepeatV = true;
            public EtsDds.PixelFormat Format;
            public bool Missing, CubeMap;
            public string Failure;
        }

        /// <summary>Pure (no Unity call; a worker may run it): see PreparedShadow. Never throws; a failure is carried in the result.</summary>
        public static PreparedShadow PrepareAlphaFromRed(EtsFileSystem files, string tobjPath)
        {
            var prepared = new PreparedShadow { Path = tobjPath };

            try
            {
                if (!TryReadPacked(files, tobjPath, out EtsDds dds, out prepared.RepeatU, out prepared.RepeatV, out _))
                {
                    prepared.RepeatU = prepared.RepeatV = true;
                    dds = ReadDds(files, tobjPath);
                }

                prepared.Missing = dds == null;
                prepared.CubeMap = dds != null && dds.IsCubeMap;

                if (dds != null)
                {
                    prepared.Format = dds.Format;
                }

                prepared.Data = AlphaFromRedChain(dds, out _, out prepared.Width, out prepared.Height, out prepared.Levels);
            }
            catch (Exception e)
            {
                prepared.Data = null;
                prepared.Failure = e.Message;
            }

            return prepared;
        }

        /// <summary>Main thread: a PreparedShadow into the cache, uploaded - unless the cache has it already, which is then returned.</summary>
        public Texture2D AdoptAlphaFromRed(PreparedShadow prepared)
        {
            if (prepared == null || string.IsNullOrEmpty(prepared.Path))
            {
                return null;
            }

            string key = ShadowKey(prepared.Path);

            if (_byPath.TryGetValue(key, out Texture2D cached))
            {
                return cached;
            }

            Texture2D texture = UploadShadow(prepared);
            _byPath[key] = texture;
            return texture;
        }

        private Texture2D UploadShadow(PreparedShadow prepared)
        {
            string tobjPath = prepared.Path;

            if (prepared.Failure != null)
            {
                Report(tobjPath, "decal shadow: " + prepared.Failure);
                return null;
            }

            if (prepared.Data == null)
            {
                Report(tobjPath, "decal shadow: red channel not readable (" + (prepared.Missing ? "missing" : prepared.CubeMap ? "cube map" : prepared.Format.ToString()) + ")");
                return null;
            }

            try
            {
                var texture = new Texture2D(prepared.Width, prepared.Height, TextureFormat.RGBA32, prepared.Levels, true)
                {
                    name = tobjPath + " shadow",
                    wrapModeU = prepared.RepeatU ? TextureWrapMode.Repeat : TextureWrapMode.Clamp,
                    wrapModeV = prepared.RepeatV ? TextureWrapMode.Repeat : TextureWrapMode.Clamp,
                    filterMode = FilterMode.Trilinear,
                    anisoLevel = 8
                };

                texture.LoadRawTextureData(prepared.Data);
                texture.Apply(false, true);

                if (_formatsLogged.Add(1000 + (int)prepared.Format))
                {
                    Debug.Log("[SomniumEuroTruck] texture: decal shadow read from " + prepared.Format + " (" + prepared.Width + "x" + prepared.Height + ", " + prepared.Levels + " mips), first " + tobjPath);
                }

                return texture;
            }
            catch (Exception e)
            {
                Report(tobjPath, "decal shadow: " + e.Message);
                return null;
            }
        }

        /// <summary>Largest edge of a decal shadow's top mip as uploaded (see LoadAlphaFromRed).</summary>
        public const int ShadowSize = 1024;

        /// <summary>
        /// Pure: the mip chain from the first level no larger than ShadowSize down, as RGBA32 texels
        /// (0, 0, 0, red). Null when the red channel cannot be read. No Unity call.
        /// </summary>
        public static byte[] AlphaFromRedChain(EtsDds dds, out int top, out int width, out int height, out int levels)
        {
            top = 0;
            width = height = levels = 0;

            if (dds == null || dds.Data == null || dds.IsCubeMap)
            {
                return null;
            }

            int mips = Math.Max(1, dds.MipCount);

            while (top + 1 < mips)
            {
                dds.LevelOffset(top, out int w, out int h);

                if (w <= ShadowSize && h <= ShadowSize)
                {
                    break;
                }

                top++;
            }

            dds.LevelOffset(top, out width, out height);
            levels = mips - top;
            int total = 0;

            for (int m = top; m < mips; m++)
            {
                dds.LevelOffset(m, out int w, out int h);
                total += w * h * 4;
            }

            var data = new byte[total];
            int at = 0;

            for (int m = top; m < mips; m++)
            {
                byte[] red = EtsPixels.RedPlane(dds, m, out int w, out int h);

                if (red == null)
                {
                    return null;
                }

                for (int i = 0; i < red.Length; i++)
                {
                    data[at + i * 4 + 3] = red[i];
                }

                at += w * h * 4;
            }

            return data;
        }

        // ------------------------------------------------------------- baked gloss

        /// <summary>Largest edge of a baked gloss map. Gloss is a broad property; this bounds the one-off decode.</summary>
        public const int GlossSize = 128;

        /// <summary>A baked material's per-texel smoothness, 0..255, before scaling to the material's reference.</summary>
        public sealed class GlossPlane
        {
            public byte[] Smoothness;
            public int Width, Height;
            public byte Max;
            public bool RepeatU = true, RepeatV = true;
            public string Name;
        }

        private readonly Dictionary<string, GlossPlane> _glossPlanes = new Dictionary<string, GlossPlane>(StringComparer.Ordinal);

        /// <summary>The key a baked gloss plane is kept under (spec map and env mask together).</summary>
        public static string GlossKey(string specPath, string envMaskPath)
        {
            return (string.IsNullOrEmpty(specPath) ? "" : "/" + EtsHashFs.Normalize(specPath)) + "|" +
                   (string.IsNullOrEmpty(envMaskPath) ? "" : "/" + EtsHashFs.Normalize(envMaskPath)) + "|gloss";
        }

        /// <summary>Whether the plane for this pair has been prepared (or found unreadable) already, so LoadBakedGloss reads nothing.</summary>
        public bool HasGlossPlane(string specPath, string envMaskPath)
        {
            return _glossPlanes.ContainsKey(GlossKey(specPath, envMaskPath));
        }

        /// <summary>
        /// Pure (no Unity call; a worker may run it): the smoothness plane of an eut2.baked.* material, read
        /// the way the game's own baked programs read their textures. Measured in
        /// /effect/eut2/baked/eut2.baked.spec.rfx and eut2.baked.spec.add.env.rfx (harness
        /// details/textures ProgramTexShader): texture_over is the "_bspec" map, and per texel the
        /// specular power is over.x^2 * 1024 and the specular strength over.y; on the .env variants
        /// texture_mask ("_env", BC4) scales the environment reflection per texel (mask.x * 3 *
        /// modulator * fresnel). All on the one baked uv set. A specular power and strength become a
        /// smoothness exactly as SurfaceOf turns a material's shininess and specular into one; the env
        /// mask lifts a texel toward a reflective 0.85 by its value. Null when neither can be read.
        /// </summary>
        public static GlossPlane PrepareBakedGloss(EtsFileSystem files, string specPath, string envMaskPath)
        {
            EtsBitmap spec = null;
            byte[] env = null;
            int envW = 0, envH = 0;
            bool repeatU = true, repeatV = true;

            if (!string.IsNullOrEmpty(specPath))
            {
                EtsDds dds = ReadForPlane(files, specPath, out repeatU, out repeatV);
                int level = dds == null ? 0 : SmallLevel(dds, GlossSize);
                spec = dds == null ? null : EtsPixels.DecodeLevel(dds, level);
            }

            if (!string.IsNullOrEmpty(envMaskPath))
            {
                EtsDds dds = ReadForPlane(files, envMaskPath, out bool u, out bool v);
                env = dds == null ? null : EtsPixels.RedPlane(dds, SmallLevel(dds, GlossSize), out envW, out envH);

                if (spec == null)
                {
                    repeatU = u;
                    repeatV = v;
                }
            }

            if (spec == null && env == null)
            {
                return null;
            }

            int width = Math.Max(spec?.Width ?? 1, env != null ? envW : 1);
            int height = Math.Max(spec?.Height ?? 1, env != null ? envH : 1);
            var smooth = new byte[width * height];
            byte max = 0;

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    float s = 0f;

                    if (spec != null)
                    {
                        int o = spec.Offset(x * spec.Width / width, y * spec.Height / height);
                        s = SpecularSmoothness[spec.Rgba[o] * 256 + spec.Rgba[o + 1]] / 255f;
                    }

                    if (env != null)
                    {
                        float m = env[(y * envH / height) * envW + x * envW / width] / 255f;
                        s = s + (0.85f - s) * Math.Max(0f, Math.Min(1f, m));
                    }

                    byte b = (byte)Math.Max(0, Math.Min(255, (int)Math.Round(s * 255f)));
                    smooth[y * width + x] = b;

                    if (b > max) max = b;
                }
            }

            return new GlossPlane { Smoothness = smooth, Width = width, Height = height, Max = max, RepeatU = repeatU, RepeatV = repeatV, Name = specPath ?? envMaskPath };
        }

        /// <summary>
        /// Smoothness (0..255) for every (x, y) byte pair of a _bspec texel: power x^2 * 1024 to a roughness
        /// the way SurfaceOf turns a shininess into one, damped by the strength y as SurfaceOf damps by the
        /// specular colour.
        /// </summary>
        private static readonly byte[] SpecularSmoothness = BuildSpecularSmoothness();

        private static byte[] BuildSpecularSmoothness()
        {
            var table = new byte[256 * 256];

            for (int x = 0; x < 256; x++)
            {
                float gloss = x / 255f;
                float power = gloss * gloss * 1024f;
                float roughness = (float)Math.Pow(2.0 / (Math.Max(1.0, power) + 2.0), 0.25);

                for (int y = 0; y < 256; y++)
                {
                    float strength = y / 255f;
                    float s = (1f - roughness) * (0.25f + 0.75f * (float)Math.Sqrt(strength));
                    table[x * 256 + y] = (byte)Math.Max(0, Math.Min(255, (int)Math.Round(s * 255.0)));
                }
            }

            return table;
        }

        /// <summary>The first mip no larger than size on either edge.</summary>
        private static int SmallLevel(EtsDds dds, int size)
        {
            int level = 0;

            while (level + 1 < dds.MipCount)
            {
                dds.LevelOffset(level, out int w, out int h);

                if (w <= size && h <= size)
                {
                    break;
                }

                level++;
            }

            return level;
        }

        private static EtsDds ReadForPlane(EtsFileSystem files, string tobjPath, out bool repeatU, out bool repeatV)
        {
            if (TryReadPacked(files, tobjPath, out EtsDds dds, out repeatU, out repeatV, out _))
            {
                return dds;
            }

            repeatU = repeatV = true;
            byte[] tobjBytes = files.Read(tobjPath);

            if (tobjBytes == null)
            {
                return null;
            }

            EtsTobj tobj = EtsTobj.Parse(tobjBytes, tobjPath);
            byte[] ddsBytes = files.Read(tobj.TexturePath);
            repeatU = tobj.RepeatU;
            repeatV = tobj.RepeatV;
            return ddsBytes == null ? null : EtsDds.Parse(ddsBytes);
        }

        /// <summary>
        /// Main thread: keeps a plane a worker prepared (PrepareBakedGloss), so LoadBakedGloss does not read
        /// it again. A null plane is kept too - "unreadable" - so nothing asks for it on the frame afterwards.
        /// </summary>
        public void AdoptBakedGloss(string specPath, string envMaskPath, GlossPlane plane)
        {
            _glossPlanes[GlossKey(specPath, envMaskPath)] = plane;
        }

        /// <summary>
        /// The _MetallicGlossMap for a baked material (see PrepareBakedGloss): RGBA32, red 0 (not metal),
        /// alpha = smoothness / reference, where reference = min(the plane's own maximum, cap) and is what
        /// the material must set as _Smoothness (URP multiplies the map's alpha by it). Capping the
        /// reference keeps the material sane on a device whose Lit lacks the map variant: it then draws at
        /// the reference, a little above the scalar it had before. Null when the plane cannot be read or
        /// is flat (the scalar says the same). Main thread.
        /// </summary>
        public Texture2D LoadBakedGloss(string specPath, string envMaskPath, float cap, out float reference)
        {
            reference = 0f;

            if (string.IsNullOrEmpty(specPath) && string.IsNullOrEmpty(envMaskPath))
            {
                return null;
            }

            string planeKey = GlossKey(specPath, envMaskPath);
            string key = planeKey + cap.ToString("0.00");

            if (_byPath.TryGetValue(key, out Texture2D cached))
            {
                reference = cached != null && _glossReference.TryGetValue(key, out float r) ? r : 0f;
                return cached;
            }

            Texture2D texture = null;

            try
            {
                if (!_glossPlanes.TryGetValue(planeKey, out GlossPlane plane))
                {
                    plane = PrepareBakedGloss(_files, specPath, envMaskPath);
                    _glossPlanes[planeKey] = plane;
                }

                if (plane != null && plane.Max >= 8)
                {
                    reference = Mathf.Min(plane.Max / 255f, cap);
                    var pixels = new Color32[plane.Smoothness.Length];

                    for (int i = 0; i < pixels.Length; i++)
                    {
                        float a = plane.Smoothness[i] / 255f / reference;
                        pixels[i] = new Color32(0, 0, 0, (byte)Mathf.Clamp(Mathf.RoundToInt(a * 255f), 0, 255));
                    }

                    texture = new Texture2D(plane.Width, plane.Height, TextureFormat.RGBA32, true, true)
                    {
                        name = plane.Name + " gloss",
                        wrapModeU = plane.RepeatU ? TextureWrapMode.Repeat : TextureWrapMode.Clamp,
                        wrapModeV = plane.RepeatV ? TextureWrapMode.Repeat : TextureWrapMode.Clamp,
                        filterMode = FilterMode.Trilinear,
                        anisoLevel = 4
                    };

                    texture.SetPixels32(pixels);
                    texture.Apply(true, true);
                    _glossReference[key] = reference;
                }
            }
            catch (Exception e)
            {
                texture = null;
                Report(specPath ?? envMaskPath, "baked gloss: " + e.Message);
            }

            _byPath[key] = texture;
            return texture;
        }

        private readonly Dictionary<string, float> _glossReference = new Dictionary<string, float>(StringComparer.Ordinal);

        // ------------------------------------------------------------- reflection masks

        /// <summary>Largest mask edge kept. Reflection masks are broad shapes, not fine detail.</summary>
        private const int MaskSize = 256;

        /// <summary>
        /// The reflection mask the game keeps in the ALPHA channel of a surface's own colour
        /// texture, turned into something URP's Lit shader can read.
        ///
        /// This is read out of the files, not assumed. The DAF's chrome_c is a 1024x1024 DXT5
        /// whose colour is one grey atlas of grilles, steps, mud flaps and light housings - and
        /// whose alpha is a completely different picture: about 0.28 over the mud flaps and the
        /// plastic, and near 1.0 over the grilles, the steps and the polished strips. metalic2's
        /// alpha is black over the whole chassis with two bright patches on the aluminium tank.
        /// Neither is transparency - both materials are opaque - they are "how much of the
        /// surroundings does this texel reflect", which is what eut2.dif.spec.add.env adds.
        ///
        /// Applying one metallic and one smoothness to a whole atlas therefore had to be wrong
        /// everywhere except where the mask happened to be flat: either the plastic came out as
        /// dark mirror or the chrome came out as grey paint. The mask restores the distinction.
        ///
        /// Returned as RGBA32 with red = alpha scaled by the material's metallic and alpha = the
        /// mask itself, because URP reads metallic from the map's red and smoothness from its
        /// alpha times _Smoothness. Null when the texture carries no alpha, or when the mask is
        /// the same value everywhere and the plain scalars already say the same thing.
        /// </summary>
        public Texture2D LoadReflectionMask(string tobjPath, float metallicScale)
        {
            if (string.IsNullOrEmpty(tobjPath))
            {
                return null;
            }

            string key = "/" + EtsHashFs.Normalize(tobjPath) + "|mask" + metallicScale.ToString("0.00");

            if (_byPath.TryGetValue(key, out Texture2D cached))
            {
                return cached;
            }

            Texture2D mask = null;

            try
            {
                mask = BuildMask(tobjPath, metallicScale);
            }
            catch (Exception e)
            {
                Report(tobjPath, "mask: " + e.Message);
            }

            _byPath[key] = mask;
            return mask;
        }

        private Texture2D BuildMask(string tobjPath, float metallicScale)
        {
            // Prepared already (Adopt kept the plane): no second read of the texture. A plane read here is kept too
            // (an empty one when there is none), so HasMaskPlane says so and nothing asks for it again.
            string planeKey = "/" + EtsHashFs.Normalize(tobjPath);

            if (!_maskPlanes.TryGetValue(planeKey, out MaskPlane plane))
            {
                plane = ReadPlane(_files, tobjPath);
                _maskPlanes[planeKey] = plane ?? new MaskPlane();
            }

            if (plane == null || plane.Alpha == null)
            {
                return null;
            }

            byte[] alpha = plane.Alpha;
            int width = plane.Width, height = plane.Height;
            bool repeatU = plane.RepeatU, repeatV = plane.RepeatV;
            string name = plane.Name;
            byte low = 255, high = 0;

            for (int i = 0; i < alpha.Length; i++)
            {
                if (alpha[i] < low) low = alpha[i];
                if (alpha[i] > high) high = alpha[i];
            }

            // A flat mask says nothing the scalars do not already say.
            if (high - low < 16)
            {
                return null;
            }

            var pixels = new Color32[alpha.Length];
            float scale = Mathf.Clamp01(metallicScale);

            for (int i = 0; i < alpha.Length; i++)
            {
                byte a = alpha[i];
                pixels[i] = new Color32((byte)(a * scale), 0, 0, a);
            }

            var mask = new Texture2D(width, height, TextureFormat.RGBA32, true, true)
            {
                name = name + " mask",
                wrapModeU = repeatU ? TextureWrapMode.Repeat : TextureWrapMode.Clamp,
                wrapModeV = repeatV ? TextureWrapMode.Repeat : TextureWrapMode.Clamp,
                filterMode = FilterMode.Trilinear,
                anisoLevel = 4
            };

            mask.SetPixels32(pixels);
            mask.Apply(true, true);
            return mask;
        }

        /// <summary>The mask's alpha plane read straight from the archives. Null when there is no texture.</summary>
        private static MaskPlane ReadPlane(EtsFileSystem files, string tobjPath)
        {
            EtsDds dds;
            bool repeatU, repeatV;
            string name;

            if (TryReadPacked(files, tobjPath, out dds, out repeatU, out repeatV, out _))
            {
                if (dds == null)
                {
                    return null;
                }

                name = tobjPath;
            }
            else
            {
                byte[] tobjBytes = files.Read(tobjPath);

                if (tobjBytes == null)
                {
                    return null;
                }

                EtsTobj tobj = EtsTobj.Parse(tobjBytes, tobjPath);
                byte[] ddsBytes = files.Read(tobj.TexturePath);

                if (ddsBytes == null)
                {
                    return null;
                }

                dds = EtsDds.Parse(ddsBytes);
                repeatU = tobj.RepeatU;
                repeatV = tobj.RepeatV;
                name = tobj.TexturePath;
            }

            return PlaneOf(dds, name, repeatU, repeatV);
        }

        /// <summary>
        /// The alpha of the first mip no larger than MaskSize. Null when there is none to read (a
        /// cube map, or a format whose alpha is not unpacked here).
        /// </summary>
        private static MaskPlane PlaneOf(EtsDds dds, string name, bool repeatU, bool repeatV)
        {
            if (dds.IsCubeMap || dds.Format == EtsDds.PixelFormat.Dxt1 || dds.Format == EtsDds.PixelFormat.Bc4 ||
                dds.Format == EtsDds.PixelFormat.Bc5 || dds.Format == EtsDds.PixelFormat.Bc7)
            {
                // No alpha to read, or a form not unpacked here.
                return null;
            }

            // Walk the mip chain to the first level small enough to keep.
            int width = dds.Width, height = dds.Height, offset = 0, level = 0;

            while (level + 1 < dds.MipCount && (width > MaskSize || height > MaskSize))
            {
                offset += EtsDds.MipSize(dds.Format, width, height);
                width = Mathf.Max(1, width / 2);
                height = Mathf.Max(1, height / 2);
                level++;
            }

            byte[] alpha = ReadAlpha(dds, offset, width, height);

            if (alpha == null)
            {
                return null;
            }

            return new MaskPlane { Alpha = alpha, Width = width, Height = height, Name = name, RepeatU = repeatU, RepeatV = repeatV };
        }

        /// <summary>
        /// The alpha plane of one mip level. DXT5 keeps alpha as two endpoints and sixteen
        /// three-bit indices per four-by-four block, which is the only unpacking needed here.
        /// </summary>
        private static byte[] ReadAlpha(EtsDds dds, int offset, int width, int height)
        {
            var alpha = new byte[width * height];

            if (dds.Format == EtsDds.PixelFormat.Rgba32 || dds.Format == EtsDds.PixelFormat.Bgra32)
            {
                for (int i = 0; i < alpha.Length; i++)
                {
                    int at = offset + i * 4 + 3;

                    if (at >= dds.Data.Length)
                    {
                        return null;
                    }

                    alpha[i] = dds.Data[at];
                }

                return alpha;
            }

            if (dds.Format != EtsDds.PixelFormat.Dxt5)
            {
                return null;
            }

            int blocksX = Mathf.Max(1, (width + 3) / 4);
            int blocksY = Mathf.Max(1, (height + 3) / 4);
            var palette = new byte[8];

            for (int by = 0; by < blocksY; by++)
            {
                for (int bx = 0; bx < blocksX; bx++)
                {
                    int at = offset + (by * blocksX + bx) * 16;

                    if (at + 8 > dds.Data.Length)
                    {
                        return null;
                    }

                    int a0 = dds.Data[at], a1 = dds.Data[at + 1];
                    palette[0] = (byte)a0;
                    palette[1] = (byte)a1;

                    if (a0 > a1)
                    {
                        for (int i = 1; i < 7; i++)
                        {
                            palette[i + 1] = (byte)(((7 - i) * a0 + i * a1) / 7);
                        }
                    }
                    else
                    {
                        for (int i = 1; i < 5; i++)
                        {
                            palette[i + 1] = (byte)(((5 - i) * a0 + i * a1) / 5);
                        }

                        palette[6] = 0;
                        palette[7] = 255;
                    }

                    ulong bits = 0;

                    for (int i = 0; i < 6; i++)
                    {
                        bits |= (ulong)dds.Data[at + 2 + i] << (8 * i);
                    }

                    for (int texel = 0; texel < 16; texel++)
                    {
                        int x = bx * 4 + (texel & 3);
                        int y = by * 4 + (texel >> 2);

                        if (x >= width || y >= height)
                        {
                            continue;
                        }

                        alpha[y * width + x] = palette[(int)((bits >> (3 * texel)) & 7)];
                    }
                }
            }

            return alpha;
        }

        private void Report(string path, string why)
        {
            if (_reported.Add(path))
            {
                Debug.LogWarning("[SomniumEuroTruck] texture " + path + ": " + why);
            }
        }

        public void Clear()
        {
            foreach (Texture2D texture in _byPath.Values)
            {
                if (texture != null)
                {
                    UnityEngine.Object.Destroy(texture);
                }
            }

            _byPath.Clear();
            _meanAlpha.Clear();
            _maskPlanes.Clear();
            _glossPlanes.Clear();
            _glossReference.Clear();
            _reported.Clear();
        }
    }
}
