using System;
using System.Globalization;
using UnityEngine;
using UnityEngine.Rendering;

namespace SomniumSpace.Worlds.SomniumEuroTruck.Ets
{
    /// <summary>
    /// The game's own sky, as the climate profile names it.
    ///
    /// skybox_texture of the clear-day slot (default.nice.15 slot 0:
    /// /asset/skybox/s_nice_00/s_nice00_09_50_g.tobj) is a 4096 x 1024 packed image in DXGI 67 =
    /// R9G9B9E5_SHAREDEXP - HDR radiance. Decoded (harness Program969, research/env960/
    /// p969_sky.txt and png/sky_nice00_09_50.png) it is the UPPER HEMISPHERE: row 0 the zenith, the
    /// last row the horizon, the sun's glow round row 400 (elevation ~55) in the middle column.
    ///
    /// The scene's sky until now was the SDK template's Skybox/Procedural
    /// (SomniumSpaceTemplates/SimpleWorldTemplate/Materials/Skybox.mat) with _GroundColor
    /// 0.645/0.84/1.0: the lower half of the sky is PALE BLUE, which is what shows through any gap
    /// in the ground and past the edge of the built world.
    ///
    /// Needs the "SomniumEuroTruck/Sky" shader referenced from the scene (as the decal and
    /// two-layer shaders are) so the world bundle carries it; without it the procedural sky is
    /// kept and only its ground colour is changed.
    ///
    /// 2026-09-23 (lighting): the panorama is the one of the sun profile nearest the sun's real elevation
    /// (EtsClimate: 10:00 at the start is 44 degrees, default.nice.14's s_nice00_08_35_g), turned so its glow
    /// stands at the sun's azimuth; the disc takes the file's sun_color.
    ///
    /// 2026-09-25 (lighting, round 2), as the game's sky effect and model draw it:
    /// - the weather slot's panoramas (EtsClimate.PickSlot: slots 5-11 carry painted cumulus), BOTH profiles either
    ///   side of the sun, crossfaded by the profile blend (eut2.sky, /effect/eut2/_shd/05b00a9aa2e6201b0d0a525f7f96e02d
    ///   .glsl.fso: mix = base_a + _sky_blend_setup.x (base_b - base_a)); at full size (mip 0, 4096 x 1024, 11 px a
    ///   degree) for a slot with clouds, mip 1 for the plain clear gradients (slots whose skycloud mask is m_cl_sky);
    /// - below the horizon the panorama's last row, as the game's dome stretches it to -15 degrees
    ///   (/asset/skybox/skybox.pmd piece 2: v = 1 from -1.9 down), under the "bottom_ring" (piece 1) that fades
    ///   sky_bottom_color in: vertex alpha 0 at the horizon, 133/255 at -1.86, 255 from -5.35 degrees. Before, the fog
    ///   colour was laid there half a degree under the horizon: the lavender stripe;
    /// - the disc at sun_color x exposure as HDR (4.4 at 10:00; it was 20 x a normalised colour, a bloom blob), 0.27
    ///   degrees across, and the additive halo of /asset/skybox/sun_halo.pmd (a 2.5-unit quad on the ~34-unit dome:
    ///   4.2 degrees) in sun_halo_color x exposure x its texture's colour (sRGB 195,193,173) and alpha, whose radial
    ///   falloff fits 0.82 (1 - r)^2.25 (row means of /asset/skybox/sun_halo.tobj);
    /// - a sky reflection for the materials that reflect (glass, water, chrome, the eut2.window rooms): EtsClimate
    ///   sets the environment reflection intensity to 0 (URP's "sheen", EtsClimate notes), which also darkens the
    ///   default skybox reflection, so a custom-cube ReflectionProbe (importance 0, 100 km box) carries the same sky,
    ///   built on the CPU from the panoramas' small mips - not a realtime probe, so it works on quality levels with
    ///   realtime probes off (the project's Medium). The truck's own realtime probe (EtsTruckRig, importance 2) wins
    ///   inside its box;
    /// - DynamicGI.UpdateEnvironment is no longer called: it would recompute the ambient probe EtsClimate writes
    ///   (the game's directional ambient) from the Trilight colours.
    /// Panoramas already decoded for the same texture are kept when the climate is applied again.
    /// </summary>
    public static class EtsSky
    {
        public static Shader SkyShader;

        /// <summary>The sky reflection cube's face size (1.4 px a degree, as the panorama's third mip).</summary>
        public const int ReflectionSize = 128;

        /// <summary>The panorama mip the reflection cube is sampled from (512 x 128 of a 4096 x 1024 panorama).</summary>
        private const int ReflectionMip = 3;

        /// <summary>Row 0 of a cube face's pixel array is the face's top (the D3D layout). The render lab checks it with a chrome sphere.</summary>
        public static bool CubeRowsTopDown = true;

        private sealed class Panorama
        {
            public string Name;
            public int Skip;
            public Texture2D Texture;
            public uint[] Small;
            public int SmallWidth;
            public int SmallHeight;
        }

        private static Material _material;
        private static Panorama _a;
        private static Panorama _b;
        private static Cubemap _cube;
        private static ReflectionProbe _probe;

        public static string Apply(EtsFileSystem files, EtsClimate climate, Light sun)
        {
            return Apply(files, climate, sun, null);
        }

        /// <summary>The sky for the climate; <paramref name="probeParent"/> holds the sky reflection probe (the world's scene).</summary>
        public static string Apply(EtsFileSystem files, EtsClimate climate, Light sun, Transform probeParent)
        {
            if (climate == null)
            {
                return "Sky: no climate.";
            }

            var clock = System.Diagnostics.Stopwatch.StartNew();
            Shader shader = SkyShader != null ? SkyShader : Shader.Find("SomniumEuroTruck/Sky");
            string why = null;
            string nameA = !string.IsNullOrEmpty(climate.SkyTextureLow) ? climate.SkyTextureLow : climate.SkyTexture;
            string nameB = !string.IsNullOrEmpty(climate.SkyTextureHigh) ? climate.SkyTextureHigh : nameA;

            // The plain clear gradients (skycloud mask m_cl_sky) buy nothing from the full 16 MB; a cloud slot does.
            bool clear = climate.CloudTexture != null && climate.CloudTexture.EndsWith("m_cl_sky.tobj", StringComparison.OrdinalIgnoreCase);
            int skip = clear ? 1 : 0;
            bool keptA = false, keptB = false;
            Panorama a = null, b = null;

            if (shader != null && shader.isSupported)
            {
                a = Take(nameA, skip, ref keptA);
                b = nameB == nameA ? a : Take(nameB, skip, ref keptB);
                Release(_a, a, b);
                Release(_b, a, b);
                _a = null;
                _b = null;

                if (a == null)
                {
                    a = LoadPanoramaWithSmall(files, nameA, skip, out why);
                }

                if (b == null)
                {
                    b = nameB == nameA ? a : LoadPanoramaWithSmall(files, nameB, skip, out string whyB) ?? a;
                }

                _a = a;
                _b = b;
            }

            long decodeMs = clock.ElapsedMilliseconds;

            string result;

            if (a != null && a.Texture != null)
            {
                if (_material == null)
                {
                    _material = new Material(shader) { name = "ets sky" };
                }

                SetMaterial(climate, sun);
                RenderSettings.skybox = _material;
                string reflection = UpdateReflection(climate, probeParent);
                long cubeMs = clock.ElapsedMilliseconds - decodeMs;
                CultureInfo i = CultureInfo.InvariantCulture;
                result = "Sky: " + a.Name + (b != a ? " + " + b.Name + " crossfaded " + (climate.Blend * 100f).ToString("0", i) + " %" : "") + " " + a.Texture.width + "x" + a.Texture.height + " " +
                         a.Texture.format + (keptA ? " (kept)" : "") + " (slot " + climate.Slot + (clear ? ", clear: mip 1" : ", clouds: full size") + "), x" + climate.SkyScale.ToString("0.00", i) +
                         ", below the horizon sky_bottom_color " + climate.SkyBottomLinear.ToString("0.000") + ", sun disc " + climate.SunDiscLinear.ToString("0.00") + " halo " +
                         climate.SunHaloLinear.ToString("0.00") + " at " + climate.Elevation.ToString("0.0", i) + " deg azimuth " + climate.Azimuth.ToString("0", i) + "; " + reflection + " (" + decodeMs + " ms decoding, " + cubeMs + " ms the cube).";
            }
            else if (RenderSettings.skybox != null && RenderSettings.skybox.HasProperty("_GroundColor"))
            {
                var procedural = new Material(RenderSettings.skybox) { name = "ets procedural sky" };
                procedural.SetColor("_GroundColor", EtsClimate.ToGamma(climate.SkyBottomLinear));
                RenderSettings.skybox = procedural;
                result = "Sky: procedural kept (" + (shader == null ? "SomniumEuroTruck/Sky not in the build" : why ?? "panorama not loaded") + "), ground colour set to sky_bottom_color.";
            }
            else
            {
                result = "Sky: unchanged (" + (why ?? "no sky shader") + ").";
            }

            return result;
        }

        /// <summary>
        /// Puts the sky already built by <see cref="Apply"/> on the ACTIVE scene again (Unity keeps render
        /// settings per scene): the new flow opens the map in Start, while the client still has its loader scene
        /// active, and only activates the world's scene afterwards (Player.log 2026-09-23: "Render settings ...
        /// skybox SomniumEuroTruck/Sky" before "World scene is loaded and activated"), so the sky went to the
        /// loader scene and was unloaded with it. Reuses the panoramas, material and reflection cube; builds them when
        /// there are none.
        /// </summary>
        public static string Reapply(EtsFileSystem files, EtsClimate climate, Light sun)
        {
            return Reapply(files, climate, sun, null);
        }

        public static string Reapply(EtsFileSystem files, EtsClimate climate, Light sun, Transform probeParent)
        {
            if (climate == null)
            {
                return "Sky: no climate.";
            }

            string wantA = !string.IsNullOrEmpty(climate.SkyTextureLow) ? climate.SkyTextureLow : climate.SkyTexture;

            if (_material == null || _a == null || _a.Texture == null || _a.Name != wantA)
            {
                return Apply(files, climate, sun, probeParent);
            }

            SetMaterial(climate, sun);
            RenderSettings.skybox = _material;
            string reflection = UpdateReflection(climate, probeParent);
            return "Sky: " + _a.Name + " " + _a.Texture.width + "x" + _a.Texture.height + " set again on the active scene; " + reflection + ".";
        }

        private static void SetMaterial(EtsClimate climate, Light sun)
        {
            // The sun where the climate puts it (EtsClimate.Apply turned the light the same way).
            Vector3 toSun = climate.ToSun.sqrMagnitude > 0.5f ? climate.ToSun : sun != null ? -sun.transform.forward : new Vector3(0f, 0.766f, 0.643f);
            Color disc = climate.SunDiscLinear.maxColorComponent > 0f ? climate.SunDiscLinear : climate.SunLinear;

            // The halo quad's texture: colour sRGB 195,193,173 (linear 0.546, 0.533, 0.418), alpha 0.82 (1 - r)^2.25 at its peak.
            Color halo = climate.SunHaloLinear;
            var haloTexture = new Vector4(0.546f * 0.82f, 0.533f * 0.82f, 0.418f * 0.82f, 0f);

            _material.SetTexture("_SkyTex", _a.Texture);
            _material.SetTexture("_SkyTexB", (_b != null && _b.Texture != null ? _b : _a).Texture);
            _material.SetFloat("_SkyBlend", _b != null && _b != _a ? Mathf.Clamp01(climate.Blend) : 0f);
            _material.SetFloat("_SkyScale", climate.SkyScale);
            _material.SetVector("_BelowLinear", new Vector4(climate.SkyBottomLinear.r, climate.SkyBottomLinear.g, climate.SkyBottomLinear.b, 1f));
            _material.SetVector("_SunDirection", toSun.normalized);
            _material.SetVector("_SunLinear", new Vector4(disc.r, disc.g, disc.b, 0f));
            _material.SetVector("_HaloLinear", new Vector4(halo.r * haloTexture.x, halo.g * haloTexture.y, halo.b * haloTexture.z, 0f));
            _material.SetFloat("_SunDisc", 1f);
        }

        /// <summary>An already decoded panorama of that name and size, taken out of the cache; null when there is none.</summary>
        private static Panorama Take(string name, int skip, ref bool kept)
        {
            foreach (Panorama p in new[] { _a, _b })
            {
                if (p != null && p.Texture != null && p.Name == name && p.Skip == skip)
                {
                    kept = true;
                    return p;
                }
            }

            return null;
        }

        /// <summary>Destroys a cached panorama that neither of the new pair uses.</summary>
        private static void Release(Panorama old, Panorama a, Panorama b)
        {
            if (old != null && old != a && old != b && old.Texture != null)
            {
                UnityEngine.Object.Destroy(old.Texture);
                old.Texture = null;
                old.Small = null;
            }
        }

        // ------------------------------------------------------------------ the sky reflection

        /// <summary>
        /// The custom-cube probe with the sky as the sky shader draws it (both panoramas crossfaded, sky_color x exposure,
        /// the bottom ring below the horizon; no disc - the sun's highlight is the light's own). Built again when the
        /// climate is applied (a few milliseconds: 6 x 128 x 128 lookups into 512 x 128 panoramas).
        /// </summary>
        private static string UpdateReflection(EtsClimate climate, Transform parent)
        {
            if (_a == null || _a.Small == null)
            {
                return "no sky reflection (no small mip)";
            }

            try
            {
                if (_probe == null)
                {
                    var holder = new GameObject("Ets Sky Reflection");

                    if (parent != null)
                    {
                        holder.transform.SetParent(parent, false);
                    }

                    _probe = holder.AddComponent<ReflectionProbe>();
                    _probe.mode = ReflectionProbeMode.Custom;
                    _probe.size = new Vector3(100000f, 100000f, 100000f);
                    _probe.center = Vector3.zero;
                    _probe.importance = 0;
                    _probe.boxProjection = false;
                    _probe.blendDistance = 0f;
                    _probe.intensity = 1f;
                    _probe.hdr = true;
                }

                if (_cube == null)
                {
                    _cube = new Cubemap(ReflectionSize, TextureFormat.RGBAHalf, true) { name = "ets sky reflection" };
                    _cube.wrapMode = TextureWrapMode.Clamp;
                    _cube.filterMode = FilterMode.Trilinear;
                }

                Vector3 toSun = climate.ToSun.sqrMagnitude > 0.5f ? climate.ToSun.normalized : new Vector3(0f, 0.766f, 0.643f);
                float sunAzimuth = Mathf.Atan2(toSun.x, toSun.z);
                Panorama b = _b != null && _b.Small != null ? _b : _a;
                float blend = b != _a ? Mathf.Clamp01(climate.Blend) : 0f;
                Color below = climate.SkyBottomLinear;
                var pixels = new Color[ReflectionSize * ReflectionSize];

                for (int face = 0; face < 6; face++)
                {
                    for (int y = 0; y < ReflectionSize; y++)
                    {
                        float t = 2f * (y + 0.5f) / ReflectionSize - 1f;

                        if (!CubeRowsTopDown)
                        {
                            t = -t;
                        }

                        for (int x = 0; x < ReflectionSize; x++)
                        {
                            float s = 2f * (x + 0.5f) / ReflectionSize - 1f;
                            Vector3 d;

                            switch (face)
                            {
                                case 0: d = new Vector3(1f, -t, -s); break;
                                case 1: d = new Vector3(-1f, -t, s); break;
                                case 2: d = new Vector3(s, 1f, t); break;
                                case 3: d = new Vector3(s, -1f, -t); break;
                                case 4: d = new Vector3(s, -t, 1f); break;
                                default: d = new Vector3(-s, -t, -1f); break;
                            }

                            d.Normalize();
                            pixels[y * ReflectionSize + x] = SkyAt(d, sunAzimuth, _a, b, blend, climate.SkyScale, below);
                        }
                    }

                    _cube.SetPixels(pixels, (CubemapFace)face);
                }

                _cube.Apply(true, false);
                _probe.customBakedTexture = _cube;
                return "sky reflection cube " + ReflectionSize + " (custom probe, importance 0; reflection intensity " + RenderSettings.reflectionIntensity.ToString("0.##", CultureInfo.InvariantCulture) + ")";
            }
            catch (Exception e)
            {
                return "sky reflection failed: " + e.GetType().Name + ": " + e.Message;
            }
        }

        /// <summary>The sky in one direction, as Shaders/EtsSky.shader computes it (without the disc and halo).</summary>
        private static Color SkyAt(Vector3 d, float sunAzimuth, Panorama a, Panorama b, float blend, float scale, Color below)
        {
            float elevation = Mathf.Asin(Mathf.Clamp01(d.y));
            float azimuth = Mathf.Atan2(d.x, d.z) - sunAzimuth;
            float u = azimuth / (2f * Mathf.PI) + 0.5f;
            u -= Mathf.Floor(u);
            float v = 1f - elevation / (0.5f * Mathf.PI);
            Color sky = Sample(a, u, v);

            if (blend > 0f)
            {
                sky = Color.Lerp(sky, Sample(b, u, v), blend);
            }

            sky *= scale;
            float ramp = BottomRamp(d.y);
            Color c = Color.Lerp(sky, below, ramp);
            c.a = 1f;
            return c;
        }

        /// <summary>The bottom ring's cover below the horizon (class notes): 0 at the horizon, 0.52 at -1.86 degrees, 1 from -5.35.</summary>
        public static float BottomRamp(float dirY)
        {
            float x = Mathf.Clamp01(-dirY / 0.0932f);
            return 1f - Mathf.Pow(1f - x, 1.7f);
        }

        /// <summary>Bilinear, u wrapping and v clamped (texel centres), from the small mip; v 0 = the zenith row (row 0 of the file).</summary>
        private static Color Sample(Panorama p, float u, float v)
        {
            int w = p.SmallWidth, h = p.SmallHeight;
            float fx = u * w - 0.5f, fy = Mathf.Clamp(v * h - 0.5f, 0f, h - 1);
            int x0 = Mathf.FloorToInt(fx), y0 = Mathf.FloorToInt(fy);
            float tx = fx - x0, ty = fy - y0;
            int x1 = x0 + 1, y1 = Mathf.Min(y0 + 1, h - 1);
            x0 = ((x0 % w) + w) % w;
            x1 = ((x1 % w) + w) % w;
            Color c00 = Texel(p, x0, y0), c10 = Texel(p, x1, y0), c01 = Texel(p, x0, y1), c11 = Texel(p, x1, y1);
            return Color.Lerp(Color.Lerp(c00, c10, tx), Color.Lerp(c01, c11, tx), ty);
        }

        private static Color Texel(Panorama p, int x, int y)
        {
            EtsPixels.SharedExponentToLinear(p.Small[y * p.SmallWidth + x], out float r, out float g, out float b);
            return new Color(r, g, b, 1f);
        }

        // ------------------------------------------------------------------ decoding

        private static int Align(int at, int alignment)
        {
            return (at + alignment - 1) / alignment * alignment;
        }

        /// <summary>
        /// One mip of an R9G9B9E5 packed image, padding removed with the same rules as
        /// EtsDds.FromPackedSurface (each mip starts on ImageAlign, each row on PitchAlign).
        /// </summary>
        public static Texture2D LoadPanorama(EtsFileSystem files, string tobj, int skipMips, out string why)
        {
            Panorama p = LoadPanoramaWithSmall(files, tobj, skipMips, out why);
            return p != null ? p.Texture : null;
        }

        /// <summary>The panorama at <paramref name="skipMips"/> for the GPU and its ReflectionMip as packed words for the reflection cube, from ONE read of the surface.</summary>
        private static Panorama LoadPanoramaWithSmall(EtsFileSystem files, string tobj, int skipMips, out string why)
        {
            why = null;

            if (string.IsNullOrEmpty(tobj) || !files.TryGetEntry(tobj, out EtsHashFs archive, out EtsHashFs.Entry entry) || !entry.IsPackedImage)
            {
                why = "sky texture " + tobj + " not found";
                return null;
            }

            if (entry.DxgiFormat != 67)
            {
                why = "sky texture in DXGI " + entry.DxgiFormat + ", expected 67";
                return null;
            }

            byte[] surface = archive.ReadPackedImage(entry);
            int pitchAlign = Mathf.Max(1, entry.PitchAlign), imageAlign = Mathf.Max(1, entry.ImageAlign);
            int w = entry.ImageWidth, h = entry.ImageHeight, at = 0;
            byte[] packed = null;
            int packedW = 0, packedH = 0;
            uint[] small = null;
            int smallW = 0, smallH = 0;
            int lastMip = Mathf.Max(1, entry.MipCount) - 1;
            int gpuMip = Mathf.Min(skipMips, lastMip);
            int cpuMip = Mathf.Min(ReflectionMip, lastMip);

            for (int m = 0; m <= Mathf.Max(gpuMip, cpuMip); m++)
            {
                at = Align(at, imageAlign);
                bool gpu = m == gpuMip, cpu = m == cpuMip;

                if (gpu)
                {
                    packed = new byte[w * h * 4];
                    packedW = w;
                    packedH = h;
                }

                if (cpu)
                {
                    small = new uint[w * h];
                    smallW = w;
                    smallH = h;
                }

                for (int r = 0; r < h; r++)
                {
                    at = Align(at, pitchAlign);

                    if ((gpu || cpu) && (surface == null || at + w * 4 > surface.Length))
                    {
                        why = "sky texture shorter than its mips";
                        return null;
                    }

                    if (gpu)
                    {
                        Buffer.BlockCopy(surface, at, packed, r * w * 4, w * 4);
                    }

                    if (cpu)
                    {
                        Buffer.BlockCopy(surface, at, small, r * w * 4, w * 4);
                    }

                    at += w * 4;
                }

                w = Mathf.Max(1, w / 2);
                h = Mathf.Max(1, h / 2);
            }

            Texture2D texture;

            if (SystemInfo.SupportsTextureFormat(TextureFormat.RGB9e5Float))
            {
                texture = new Texture2D(packedW, packedH, TextureFormat.RGB9e5Float, false, true);
                texture.LoadRawTextureData(packed);
            }
            else
            {
                // Decode on the CPU: 9-bit mantissas, shared 5-bit exponent, bias 15.
                var half = new ushort[packedW * packedH * 4];

                for (int i = 0; i < packedW * packedH; i++)
                {
                    uint v = BitConverter.ToUInt32(packed, i * 4);
                    float scale = Mathf.Pow(2f, (int)(v >> 27) - 24);
                    half[i * 4] = Mathf.FloatToHalf((v & 0x1FF) * scale);
                    half[i * 4 + 1] = Mathf.FloatToHalf(((v >> 9) & 0x1FF) * scale);
                    half[i * 4 + 2] = Mathf.FloatToHalf(((v >> 18) & 0x1FF) * scale);
                    half[i * 4 + 3] = Mathf.FloatToHalf(1f);
                }

                texture = new Texture2D(packedW, packedH, TextureFormat.RGBAHalf, false, true);
                texture.SetPixelData(half, 0);
            }

            texture.name = tobj;
            texture.wrapModeU = TextureWrapMode.Repeat;
            texture.wrapModeV = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Bilinear;
            texture.Apply(false, true);
            return new Panorama { Name = tobj, Skip = skipMips, Texture = texture, Small = small, SmallWidth = smallW, SmallHeight = smallH };
        }
    }
}
