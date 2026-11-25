using System;
using System.Collections.Generic;
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
    ///
    /// 2026-10-14 (time of day and weather): <see cref="Continuous"/> is called by the day/night driver every quarter
    /// second. Panoramas are read and unpacked on a worker thread (EtsWorker "Ets sky decoder") and only uploaded on the main
    /// thread (one 16 MB texture when the sun crosses a profile, every few real minutes); the material keeps the previous
    /// pair until the new one is in. The bad weather's pair, the cloud masks (stars, moon and sun show through 1 - mask),
    /// the star list baked into a celestial texture on the worker, the Milky Way (stars_texture) and the moon join them.
    /// The sky reflection cube is recomputed on the worker every 20 s and uploaded in one go. Unused panoramas are freed
    /// after 40 s.
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

        /// <summary>The star texture's size (the star list baked into the celestial frame; 0.088 degrees a texel) and the Milky Way's mip.</summary>
        public const int StarWidth = 4096, StarHeight = 2048, MilkyMip = 2;

        private sealed class Panorama
        {
            public string Name;
            public int Skip;
            public Texture2D Texture;
            public uint[] Small;
            public int SmallWidth;
            public int SmallHeight;
            public float LastUsed;
        }

        /// <summary>What the worker hands back: the GPU mip packed (4 bytes a texel, or 1 for a mask) and the small mip.</summary>
        private sealed class Decoded
        {
            public string Name;
            public bool Mask;
            public int Skip;
            public byte[] Packed;
            public int Width, Height;
            public uint[] Small;
            public int SmallWidth, SmallHeight;
            public string Why;
        }

        private sealed class StarBake
        {
            public byte[] Points;
            public byte[] Milky;
            public int MilkyWidth, MilkyHeight;
            public int Stars;
            public string Why;
        }

        private static Material _material;
        private static Panorama _a;
        private static Panorama _b;
        private static Panorama _c;
        private static Panorama _d;
        private static Cubemap _cube;
        private static ReflectionProbe _probe;

        private static readonly Dictionary<string, Panorama> Cache = new Dictionary<string, Panorama>(StringComparer.Ordinal);
        private static readonly Dictionary<string, Panorama> Masks = new Dictionary<string, Panorama>(StringComparer.Ordinal);
        private static readonly HashSet<string> Loading = new HashSet<string>(StringComparer.Ordinal);
        private static readonly List<string> Evict = new List<string>();
        private static EtsWorker _worker;
        private static EtsFileSystem _files;
        private static Texture2D _stars, _milky, _moon;
        private static bool _starsAsked, _moonAsked;
        private static float _nextReflection, _nextEvict;

        // The exposure the reflection cube was filled at, and the one of the fill under way: the probe's intensity carries the
        // change between fills (the exposure moves x1000 from day to night), so wet roads and glass never mirror a stale sky.
        private static float _cubeExposure, _fillExposure;
        private static bool _cubeBad, _fillBad;
        private static bool _reflectionBusy;
        private static Color[] _reflectionPixels;
        private static string _lastNote = "";
        private static int _uploads;
        private static long _uploadTicks;

        /// <summary>Panoramas uploaded by the continuous path and the main-thread time they took (for the driver's log line).</summary>
        public static int Uploads => _uploads;
        public static double UploadMs => _uploadTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

        private static class Id
        {
            public static readonly int SkyTex = Shader.PropertyToID("_SkyTex");
            public static readonly int SkyTexB = Shader.PropertyToID("_SkyTexB");
            public static readonly int SkyTexC = Shader.PropertyToID("_SkyTexC");
            public static readonly int SkyTexD = Shader.PropertyToID("_SkyTexD");
            public static readonly int MaskA = Shader.PropertyToID("_MaskA");
            public static readonly int MaskB = Shader.PropertyToID("_MaskB");
            public static readonly int MaskC = Shader.PropertyToID("_MaskC");
            public static readonly int MaskD = Shader.PropertyToID("_MaskD");
            public static readonly int StarTex = Shader.PropertyToID("_StarTex");
            public static readonly int MilkyTex = Shader.PropertyToID("_MilkyTex");
            public static readonly int MoonTex = Shader.PropertyToID("_MoonTex");
            public static readonly int SkyBlend = Shader.PropertyToID("_SkyBlend");
            public static readonly int SkyBlendBad = Shader.PropertyToID("_SkyBlendBad");
            public static readonly int Weather = Shader.PropertyToID("_Weather");
            public static readonly int SkyScale = Shader.PropertyToID("_SkyScale");
            public static readonly int SkyScaleBad = Shader.PropertyToID("_SkyScaleBad");
            public static readonly int BelowLinear = Shader.PropertyToID("_BelowLinear");
            public static readonly int SunDirection = Shader.PropertyToID("_SunDirection");
            public static readonly int SunLinear = Shader.PropertyToID("_SunLinear");
            public static readonly int HaloLinear = Shader.PropertyToID("_HaloLinear");
            public static readonly int SunDisc = Shader.PropertyToID("_SunDisc");
            public static readonly int NightOn = Shader.PropertyToID("_NightOn");
            public static readonly int StarScale = Shader.PropertyToID("_StarScale");
            public static readonly int StarmapLinear = Shader.PropertyToID("_StarmapLinear");
            public static readonly int MoonDirection = Shader.PropertyToID("_MoonDirection");
            public static readonly int MoonLinear = Shader.PropertyToID("_MoonLinear");
            public static readonly int MoonHaloLinear = Shader.PropertyToID("_MoonHaloLinear");
            public static readonly int MoonHaloRadius = Shader.PropertyToID("_MoonHaloRadius");
            public static readonly int FlashLinear = Shader.PropertyToID("_FlashLinear");
            public static readonly int StarRow0 = Shader.PropertyToID("_StarRow0");
            public static readonly int StarRow1 = Shader.PropertyToID("_StarRow1");
            public static readonly int StarRow2 = Shader.PropertyToID("_StarRow2");
        }

        // The halo quad's texture: colour sRGB 195,193,173 (linear 0.546, 0.533, 0.418), alpha 0.82 (1 - r)^2.25 at its peak.
        private static readonly Vector4 HaloTexture = new Vector4(0.546f * 0.82f, 0.533f * 0.82f, 0.418f * 0.82f, 0f);

        public static string Apply(EtsFileSystem files, EtsClimate climate, Light sun)
        {
            return Apply(files, climate, sun, null);
        }

        /// <summary>The panorama mip a sky texture is drawn at: the clear slots' plain gradients (s_nice_00..02, the shared night skies) gain nothing from mip 0.</summary>
        public static int SkipFor(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return 1;
            }

            return name.IndexOf("/s_nice_00/", StringComparison.Ordinal) >= 0 || name.IndexOf("/s_nice_01/", StringComparison.Ordinal) >= 0 ||
                   name.IndexOf("/s_nice_02/", StringComparison.Ordinal) >= 0 || name.IndexOf("/shared/", StringComparison.Ordinal) >= 0 ? 1 : 0;
        }

        /// <summary>The sky for the climate; <paramref name="probeParent"/> holds the sky reflection probe (the world's scene).</summary>
        public static string Apply(EtsFileSystem files, EtsClimate climate, Light sun, Transform probeParent)
        {
            if (climate == null)
            {
                return "Sky: no climate.";
            }

            _files = files;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            Shader shader = SkyShader != null ? SkyShader : Shader.Find("SomniumEuroTruck/Sky");
            string why = null;
            string nameA = !string.IsNullOrEmpty(climate.SkyTextureLow) ? climate.SkyTextureLow : climate.SkyTexture;
            string nameB = !string.IsNullOrEmpty(climate.SkyTextureHigh) ? climate.SkyTextureHigh : nameA;

            // The plain clear gradients (skycloud mask m_cl_sky) buy nothing from the full 16 MB; a cloud slot does.
            bool clear = climate.CloudTexture != null && climate.CloudTexture.EndsWith("m_cl_sky.tobj", StringComparison.Ordinal);
            int skip = clear ? 1 : 0;
            bool keptA = false;
            Panorama a = null, b = null;

            if (shader != null && shader.isSupported)
            {
                a = Take(nameA, skip, ref keptA);
                bool keptB = false;
                b = nameB == nameA ? a : Take(nameB, skip, ref keptB);

                if (a == null)
                {
                    a = LoadPanoramaWithSmall(files, nameA, skip, out why);
                    Remember(a);
                }

                if (b == null)
                {
                    b = nameB == nameA ? a : LoadPanoramaWithSmall(files, nameB, skip, out string whyB) ?? a;
                    Remember(b);
                }

                _a = a;
                _b = b;
                _c = a;
                _d = b;
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

            if (_material == null || _a == null || _a.Texture == null || (_a.Name != wantA && !Cache.ContainsKey(wantA ?? "")))
            {
                return Apply(files, climate, sun, probeParent);
            }

            if (_a.Name == wantA)
            {
                SetMaterial(climate, sun);
            }

            RenderSettings.skybox = _material;
            string reflection = UpdateReflection(climate, probeParent);
            return "Sky: " + _a.Name + " " + _a.Texture.width + "x" + _a.Texture.height + " set again on the active scene; " + reflection + ".";
        }

        private static void SetMaterial(EtsClimate climate, Light sun)
        {
            // The sun where the climate puts it (EtsClimate.Apply turned the light the same way).
            Vector3 toSun = climate.ToSun.sqrMagnitude > 0.5f ? climate.ToSun : sun != null ? -sun.transform.forward : new Vector3(0f, 0.766f, 0.643f);
            Color disc = climate.SunDiscLinear.maxColorComponent > 0f ? climate.SunDiscLinear : climate.SunLinear;
            Color halo = climate.SunHaloLinear;

            _material.SetTexture(Id.SkyTex, _a.Texture);
            _material.SetTexture(Id.SkyTexB, (_b != null && _b.Texture != null ? _b : _a).Texture);
            _material.SetTexture(Id.SkyTexC, (_c != null && _c.Texture != null ? _c : _a).Texture);
            _material.SetTexture(Id.SkyTexD, (_d != null && _d.Texture != null ? _d : _a).Texture);
            _material.SetFloat(Id.SkyBlend, _b != null && _b != _a ? Mathf.Clamp01(climate.Blend) : 0f);
            _material.SetFloat(Id.SkyScale, climate.SkyScaleNice > 0f ? climate.SkyScaleNice : climate.SkyScale);
            _material.SetFloat(Id.SkyScaleBad, climate.SkyScaleBad > 0f ? climate.SkyScaleBad : climate.SkyScale);
            _material.SetFloat(Id.SkyBlendBad, Mathf.Clamp01(climate.BadBlend));
            _material.SetVector(Id.BelowLinear, new Vector4(climate.SkyBottomLinear.r, climate.SkyBottomLinear.g, climate.SkyBottomLinear.b, 1f));
            _material.SetVector(Id.SunDirection, toSun.normalized);
            _material.SetVector(Id.SunLinear, new Vector4(disc.r, disc.g, disc.b, 0f));
            _material.SetVector(Id.HaloLinear, new Vector4(halo.r * HaloTexture.x, halo.g * HaloTexture.y, halo.b * HaloTexture.z, 0f));
            _material.SetFloat(Id.SunDisc, 1f);
        }

        // ------------------------------------------------------------------ 2026-10-14: the running day

        /// <summary>The lightning flash colour (linear, x exposure; w &gt; 0 while a strike lights the clouds). The driver sets it.</summary>
        public static Vector4 FlashLinear;

        /// <summary>
        /// The sky for this moment of the running day (EtsClimate.Evaluate filled the climate). Returns false while the first
        /// textures are still being read (the sky then stays as it was). Main thread, about 0.05 ms; nothing allocated once
        /// the textures are in, apart from the worker jobs' own buffers.
        /// </summary>
        public static bool Continuous(EtsFileSystem files, EtsClimate climate, Transform probeParent)
        {
            if (climate == null || files == null)
            {
                return false;
            }

            _files = files;
            Shader shader = SkyShader != null ? SkyShader : Shader.Find("SomniumEuroTruck/Sky");

            if (shader == null || !shader.isSupported)
            {
                return false;
            }

            if (_material == null)
            {
                _material = new Material(shader) { name = "ets sky" };
            }

            if (_worker != null)
            {
                _worker.Drain(Always);
            }

            float now = Time.realtimeSinceStartup;
            bool bad = climate.WeatherMix > 0f;
            Panorama a = Get(climate.SkyTextureLow, now);
            Panorama b = Get(climate.SkyTextureHigh, now);
            Panorama c = bad ? Get(climate.BadTextureLow, now) : null;
            Panorama d = bad ? Get(climate.BadTextureHigh, now) : null;
            float blend = Mathf.Clamp01(climate.Blend);
            float weather = climate.WeatherMix;

            if (a != null && b != null)
            {
                _a = a;
                _b = b;
            }
            else if (_a != null && _b != null && _b.Name == climate.SkyTextureLow)
            {
                // The sun moved on a profile and the next panorama is still on its way: the old pair, all the way to its upper end.
                blend = 1f;
            }
            else if (_a == null)
            {
                return false;
            }
            else
            {
                blend = _material.GetFloat(Id.SkyBlend);
            }

            if (bad && c != null && d != null)
            {
                _c = c;
                _d = d;
            }
            else if (bad)
            {
                weather = 0f;
            }

            if (!bad)
            {
                _c = _a;
                _d = _b;
            }

            if (RenderSettings.skybox != _material)
            {
                RenderSettings.skybox = _material;
            }

            Vector3 toSun = climate.ToSun.sqrMagnitude > 0.5f ? climate.ToSun.normalized : new Vector3(0f, 0.766f, 0.643f);
            Color disc = climate.SunDiscLinear;
            Color halo = climate.SunHaloLinear;
            _material.SetTexture(Id.SkyTex, _a.Texture);
            _material.SetTexture(Id.SkyTexB, _b.Texture);
            _material.SetTexture(Id.SkyTexC, _c.Texture);
            _material.SetTexture(Id.SkyTexD, _d.Texture);
            _material.SetFloat(Id.SkyBlend, blend);
            _material.SetFloat(Id.SkyBlendBad, Mathf.Clamp01(climate.BadBlend));
            _material.SetFloat(Id.Weather, weather);
            _material.SetFloat(Id.SkyScale, climate.SkyScaleNice);
            _material.SetFloat(Id.SkyScaleBad, climate.SkyScaleBad);
            _material.SetVector(Id.BelowLinear, new Vector4(climate.SkyBottomLinear.r, climate.SkyBottomLinear.g, climate.SkyBottomLinear.b, 1f));
            _material.SetVector(Id.SunDirection, toSun);
            _material.SetVector(Id.SunLinear, new Vector4(disc.r, disc.g, disc.b, 0f));
            _material.SetVector(Id.HaloLinear, new Vector4(halo.r * HaloTexture.x, halo.g * HaloTexture.y, halo.b * HaloTexture.z, 0f));
            _material.SetFloat(Id.SunDisc, 1f);

            // The cloud masks (what hides the stars, the moon and the sun).
            _material.SetTexture(Id.MaskA, MaskTexture(climate.MaskLow, now, Texture2D.blackTexture));
            _material.SetTexture(Id.MaskB, MaskTexture(climate.MaskHigh, now, Texture2D.blackTexture));
            _material.SetTexture(Id.MaskC, weather > 0f || FlashLinear.w > 0f ? MaskTexture(climate.BadMaskLow, now, Texture2D.whiteTexture) : Texture2D.whiteTexture);
            _material.SetTexture(Id.MaskD, weather > 0f || FlashLinear.w > 0f ? MaskTexture(climate.BadMaskHigh, now, Texture2D.whiteTexture) : Texture2D.whiteTexture);
            _material.SetVector(Id.FlashLinear, FlashLinear);

            // Night: stars, Milky Way and the moon (the night profiles state them; by day stars_color is 0).
            bool night = climate.Elevation < 3f && (climate.StarsScale > 1e-5f || climate.MoonLinear.maxColorComponent > 1e-5f);
            _material.SetFloat(Id.NightOn, night ? 1f : 0f);

            if (night)
            {
                AskStars();
                AskMoon();
                Matrix4x4 rotation = climate.CelestialToWorld();
                _material.SetTexture(Id.StarTex, _stars != null ? (Texture)_stars : Texture2D.blackTexture);
                _material.SetTexture(Id.MilkyTex, _milky != null ? (Texture)_milky : Texture2D.blackTexture);
                _material.SetTexture(Id.MoonTex, _moon != null ? (Texture)_moon : Texture2D.blackTexture);
                _material.SetFloat(Id.StarScale, climate.StarsScale);
                _material.SetVector(Id.StarmapLinear, new Vector4(climate.StarmapLinear.r, climate.StarmapLinear.g, climate.StarmapLinear.b, 0f));
                _material.SetVector(Id.StarRow0, rotation.GetColumn(0));
                _material.SetVector(Id.StarRow1, rotation.GetColumn(1));
                _material.SetVector(Id.StarRow2, rotation.GetColumn(2));
                Vector3 moon = climate.MoonDirection.normalized;
                _material.SetVector(Id.MoonDirection, new Vector4(moon.x, moon.y, moon.z, 0f));
                _material.SetVector(Id.MoonLinear, new Vector4(climate.MoonLinear.r, climate.MoonLinear.g, climate.MoonLinear.b, 0f));
                Color mh = climate.MoonHaloLinear;
                _material.SetVector(Id.MoonHaloLinear, new Vector4(mh.r * HaloTexture.x, mh.g * HaloTexture.y, mh.b * HaloTexture.z, 0f));

                // moon_halo.pmd is the sun halo's 2.5-unit quad (4.2 degrees) scaled by moon_halo_scale.
                _material.SetFloat(Id.MoonHaloRadius, 0.0733f * Mathf.Max(0.1f, climate.MoonHaloScale));
            }

            float ratio = _cubeExposure > 0f ? climate.Exposure / _cubeExposure : 1f;

            if (_probe != null)
            {
                _probe.intensity = Mathf.Clamp(ratio, 1e-3f, 1e3f);
            }

            if ((now >= _nextReflection || ratio > 1.5f || ratio < 0.67f || (weather > 0f) != _cubeBad) && !_reflectionBusy)
            {
                _nextReflection = now + 20f;
                StartReflection(climate, probeParent, blend, weather);
            }

            if (now >= _nextEvict)
            {
                _nextEvict = now + 5f;
                EvictUnused(now);
            }

            return true;
        }

        /// <summary>Starts reading a panorama (and a mask) the day will want soon, so it is in before the sun gets there.</summary>
        public static void Prefetch(string sky, string mask)
        {
            float now = Time.realtimeSinceStartup;
            Get(sky, now);
            MaskTexture(mask, now, null);
        }

        /// <summary>For the log: what is held and what is being read.</summary>
        public static string Note()
        {
            return Cache.Count + " panoramas held, " + Masks.Count + " masks, " + Loading.Count + " being read; stars " + (_stars != null ? "in" : _starsAsked ? "being baked" : "not needed yet") +
                   "; " + _uploads + " uploads (" + UploadMs.ToString("0.0", CultureInfo.InvariantCulture) + " ms on the main thread in all)" + (_lastNote.Length > 0 ? "; " + _lastNote : "");
        }

        private static readonly Func<bool> Always = () => true;

        private static EtsWorker Worker()
        {
            if (_worker == null)
            {
                _worker = new EtsWorker("Ets sky decoder");
            }

            return _worker;
        }

        /// <summary>A loaded panorama, or null after asking for it (read on the worker).</summary>
        private static Panorama Get(string name, float now)
        {
            if (string.IsNullOrEmpty(name))
            {
                return null;
            }

            if (Cache.TryGetValue(name, out Panorama p))
            {
                p.LastUsed = now;
                return p.Texture != null ? p : null;
            }

            Ask(name, false);
            return null;
        }

        private static Texture MaskTexture(string name, float now, Texture fallback)
        {
            if (string.IsNullOrEmpty(name))
            {
                return fallback;
            }

            if (Masks.TryGetValue(name, out Panorama p))
            {
                p.LastUsed = now;
                return p.Texture != null ? (Texture)p.Texture : fallback;
            }

            Ask(name, true);
            return fallback;
        }

        private static void Ask(string name, bool mask)
        {
            if (Loading.Contains(name) || _files == null)
            {
                return;
            }

            Loading.Add(name);
            EtsFileSystem files = _files;
            int skip = mask ? 0 : SkipFor(name);
            Worker().Post(() => mask ? DecodeMask(files, name) : DecodePanorama(files, name, skip), (result, error) =>
            {
                Loading.Remove(name);
                var decoded = result as Decoded;

                if (error != null || decoded == null || decoded.Packed == null)
                {
                    _lastNote = "could not read " + name + ": " + (error != null ? error.Message : decoded != null ? decoded.Why : "nothing");
                    return;
                }

                long start = System.Diagnostics.Stopwatch.GetTimestamp();
                Panorama p = Upload(decoded);
                _uploadTicks += System.Diagnostics.Stopwatch.GetTimestamp() - start;
                _uploads++;
                p.LastUsed = Time.realtimeSinceStartup;
                (decoded.Mask ? Masks : Cache)[name] = p;
            });
        }

        private static Panorama Upload(Decoded d)
        {
            Texture2D texture;

            if (d.Mask)
            {
                texture = new Texture2D(d.Width, d.Height, TextureFormat.R8, false, true);
                texture.LoadRawTextureData(d.Packed);
            }
            else
            {
                texture = PanoramaTexture(d.Packed, d.Width, d.Height);
            }

            texture.name = d.Name;
            texture.wrapModeU = TextureWrapMode.Repeat;
            texture.wrapModeV = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Bilinear;
            texture.Apply(false, true);
            return new Panorama { Name = d.Name, Skip = d.Skip, Texture = texture, Small = d.Small, SmallWidth = d.SmallWidth, SmallHeight = d.SmallHeight };
        }

        private static void Remember(Panorama p)
        {
            if (p != null && p.Texture != null && !string.IsNullOrEmpty(p.Name))
            {
                p.LastUsed = Time.realtimeSinceStartup;
                Cache[p.Name] = p;
            }
        }

        /// <summary>Frees panoramas and masks nobody drew for 40 s (the pair in use and anything just asked for stay).</summary>
        private static void EvictUnused(float now)
        {
            Evict.Clear();

            foreach (KeyValuePair<string, Panorama> entry in Cache)
            {
                Panorama p = entry.Value;

                if (now - p.LastUsed > 40f && p != _a && p != _b && p != _c && p != _d)
                {
                    Evict.Add(entry.Key);
                }
            }

            for (int i = 0; i < Evict.Count; i++)
            {
                Panorama p = Cache[Evict[i]];
                Cache.Remove(Evict[i]);

                if (p.Texture != null)
                {
                    UnityEngine.Object.Destroy(p.Texture);
                }
            }

            Evict.Clear();

            foreach (KeyValuePair<string, Panorama> entry in Masks)
            {
                if (now - entry.Value.LastUsed > 40f)
                {
                    Evict.Add(entry.Key);
                }
            }

            for (int i = 0; i < Evict.Count; i++)
            {
                Panorama p = Masks[Evict[i]];
                Masks.Remove(Evict[i]);

                if (p.Texture != null)
                {
                    UnityEngine.Object.Destroy(p.Texture);
                }
            }
        }

        private static void AskMoon()
        {
            if (_moonAsked)
            {
                return;
            }

            _moonAsked = true;

            try
            {
                _moon = EtsEngine.Textures != null ? EtsEngine.Textures.Load("/asset/skybox/shared/moon.tobj", false) : null;

                if (_moon != null)
                {
                    _moon.wrapMode = TextureWrapMode.Clamp;
                }
            }
            catch (Exception e)
            {
                _lastNote = "moon: " + e.Message;
            }
        }

        private static void AskStars()
        {
            if (_starsAsked || _files == null)
            {
                return;
            }

            _starsAsked = true;
            EtsFileSystem files = _files;
            Worker().Post(() => BakeStars(files), (result, error) =>
            {
                var bake = result as StarBake;

                if (error != null || bake == null || bake.Points == null)
                {
                    _lastNote = "stars: " + (error != null ? error.Message : bake != null ? bake.Why : "nothing");
                    return;
                }

                long start = System.Diagnostics.Stopwatch.GetTimestamp();
                _stars = new Texture2D(StarWidth, StarHeight, TextureFormat.R8, false, true) { name = "ets stars (star_list.txt)" };
                _stars.LoadRawTextureData(bake.Points);
                _stars.wrapModeU = TextureWrapMode.Repeat;
                _stars.wrapModeV = TextureWrapMode.Clamp;
                _stars.filterMode = FilterMode.Bilinear;
                _stars.Apply(false, true);

                if (bake.Milky != null)
                {
                    _milky = PanoramaTexture(bake.Milky, bake.MilkyWidth, bake.MilkyHeight);
                    _milky.name = "ets milky way (stars01.tobj)";
                    _milky.wrapModeU = TextureWrapMode.Repeat;
                    _milky.wrapModeV = TextureWrapMode.Clamp;
                    _milky.filterMode = FilterMode.Bilinear;
                    _milky.Apply(false, true);
                }

                _uploadTicks += System.Diagnostics.Stopwatch.GetTimestamp() - start;
                Debug.Log("[SomniumEuroTruck] Sky: " + bake.Stars + " stars of /asset/skybox/star_list.txt baked into a " + StarWidth + "x" + StarHeight +
                          " celestial texture, Milky Way from stars01.tobj mip " + MilkyMip + (bake.Milky != null ? " (" + bake.MilkyWidth + "x" + bake.MilkyHeight + ")" : " (missing)") + ".");
            });
        }

        /// <summary>
        /// Worker: the star list (/asset/skybox/star_list.txt: per star three lines - x y z on a 35.6-unit sphere, r g b
        /// intensity, a twinkle phase) into an R8 texture in the celestial frame the sky shader reads (u = 0.536 - RA / 360,
        /// v = polar angle / 180; RA = atan2(x, z) + 90 degrees), each star's intensity x its colour's luminance x 2 (the star
        /// program's vertex colour x 2) laid bilinearly over 2 x 2 texels (wider toward the poles, where a texel is narrower),
        /// square-root coded (v = sqrt(value / 4)); and stars01.tobj's mip 2 as the Milky Way in the same frame.
        /// </summary>
        private static StarBake BakeStars(EtsFileSystem files)
        {
            var bake = new StarBake();
            string text = files.ReadText("/asset/skybox/star_list.txt");

            if (string.IsNullOrEmpty(text))
            {
                bake.Why = "no star_list.txt";
                return bake;
            }

            var value = new Dictionary<int, float>(65536);
            var numbers = new List<float>(16);
            int line = 0;
            float x = 0, y = 0, z = 0;

            foreach (string raw in text.Split('\n'))
            {
                string s = raw.Trim();

                if (s.Length == 0)
                {
                    continue;
                }

                numbers.Clear();

                foreach (string part in s.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (float.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out float f))
                    {
                        numbers.Add(f);
                    }
                }

                int kind = line % 3;
                line++;

                if (kind == 0 && numbers.Count >= 3)
                {
                    x = numbers[0];
                    y = numbers[1];
                    z = numbers[2];
                }
                else if (kind == 1 && numbers.Count >= 4)
                {
                    float length = Mathf.Sqrt(x * x + y * y + z * z);

                    if (length < 1e-3f)
                    {
                        continue;
                    }

                    float cy = Mathf.Clamp(y / length, -1f, 1f);
                    float ra = Mathf.Atan2(x / length, z / length) + 0.5f * Mathf.PI;
                    float u = 0.536f - ra / (2f * Mathf.PI);
                    u -= Mathf.Floor(u);
                    float v = Mathf.Acos(cy) / Mathf.PI;
                    float lum = 0.2126f * numbers[0] + 0.7152f * numbers[1] + 0.0722f * numbers[2];
                    float amount = numbers[3] * lum * 2f;
                    float px = u * StarWidth - 0.5f, py = v * StarHeight - 0.5f;
                    int x0 = Mathf.FloorToInt(px), y0 = Mathf.FloorToInt(py);
                    float tx = px - x0, ty = py - y0;

                    // Toward the poles a texel covers less sky across: the star spreads over more of them, its total kept.
                    float sin = Mathf.Max(0.05f, Mathf.Sqrt(Mathf.Max(0f, 1f - cy * cy)));
                    int spread = Mathf.Clamp(Mathf.RoundToInt(1f / sin), 1, 16);

                    for (int dy = 0; dy < 2; dy++)
                    {
                        int row = Mathf.Clamp(y0 + dy, 0, StarHeight - 1);
                        float wy = dy == 0 ? 1f - ty : ty;

                        for (int dx = 0; dx < 2; dx++)
                        {
                            float wx = dx == 0 ? 1f - tx : tx;

                            for (int k = 0; k < spread; k++)
                            {
                                int col = ((x0 + dx + k - spread / 2) % StarWidth + StarWidth) % StarWidth;
                                int at = row * StarWidth + col;
                                value.TryGetValue(at, out float sum);
                                value[at] = sum + amount * wx * wy;
                            }
                        }
                    }

                    bake.Stars++;
                }
            }

            var points = new byte[StarWidth * StarHeight];

            foreach (KeyValuePair<int, float> texel in value)
            {
                float coded = Mathf.Sqrt(Mathf.Clamp01(texel.Value * 0.25f));
                points[texel.Key] = (byte)Mathf.Clamp(Mathf.RoundToInt(coded * 255f), 0, 255);
            }

            bake.Points = points;

            // The Milky Way: stars01.tobj (stars_texture of every default slot), a 4096 x 2048 R9G9B9E5 sphere, at mip 2.
            Decoded milky = DecodePanorama(files, "/asset/skybox/shared/stars01.tobj", MilkyMip);

            if (milky != null && milky.Packed != null)
            {
                bake.Milky = milky.Packed;
                bake.MilkyWidth = milky.Width;
                bake.MilkyHeight = milky.Height;
            }

            return bake;
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
                EnsureProbe(parent);
                Vector3 toSun = climate.ToSun.sqrMagnitude > 0.5f ? climate.ToSun.normalized : new Vector3(0f, 0.766f, 0.643f);
                Panorama b = _b != null && _b.Small != null ? _b : _a;
                float blend = b != _a ? Mathf.Clamp01(climate.Blend) : 0f;
                Color[] pixels = new Color[6 * ReflectionSize * ReflectionSize];
                FillReflection(pixels, toSun, _a, b, blend, climate.SkyScale, null, null, 0f, 0f, 0f, climate.SkyBottomLinear);
                _cubeExposure = climate.Exposure;
                _probe.intensity = 1f;
                UploadReflection(pixels);
                return "sky reflection cube " + ReflectionSize + " (custom probe, importance 0; reflection intensity " + RenderSettings.reflectionIntensity.ToString("0.##", CultureInfo.InvariantCulture) + ")";
            }
            catch (Exception e)
            {
                return "sky reflection failed: " + e.GetType().Name + ": " + e.Message;
            }
        }

        private static void EnsureProbe(Transform parent)
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
        }

        private static void UploadReflection(Color[] pixels)
        {
            int face = ReflectionSize * ReflectionSize;

            if (_faceBuffer == null)
            {
                _faceBuffer = new Color[face];
            }

            for (int f = 0; f < 6; f++)
            {
                Array.Copy(pixels, f * face, _faceBuffer, 0, face);
                _cube.SetPixels(_faceBuffer, (CubemapFace)f);
            }

            _cube.Apply(true, false);
            _probe.customBakedTexture = _cube;
        }

        private static Color[] _faceBuffer;

        /// <summary>The running day's reflection: computed on the worker into one reused buffer, uploaded when it lands.</summary>
        private static void StartReflection(EtsClimate climate, Transform parent, float blend, float weather)
        {
            if (_a == null || _a.Small == null || _b == null || _b.Small == null)
            {
                return;
            }

            try
            {
                EnsureProbe(parent);
            }
            catch (Exception)
            {
                return;
            }

            if (_reflectionPixels == null)
            {
                _reflectionPixels = new Color[6 * ReflectionSize * ReflectionSize];
            }

            _reflectionBusy = true;
            _fillExposure = climate.Exposure;
            _fillBad = weather > 0f;
            Color[] pixels = _reflectionPixels;
            Vector3 toSun = climate.ToSun.sqrMagnitude > 0.5f ? climate.ToSun.normalized : new Vector3(0f, 0.766f, 0.643f);
            Panorama a = _a, b = _b;
            Panorama c = weather > 0f && _c != null && _c.Small != null ? _c : null;
            Panorama d = weather > 0f && _d != null && _d.Small != null ? _d : null;
            float scale = climate.SkyScaleNice, scaleBad = climate.SkyScaleBad, badBlend = climate.BadBlend;
            Color below = climate.SkyBottomLinear;

            Worker().Post(() =>
            {
                FillReflection(pixels, toSun, a, b, blend, scale, c, d, badBlend, weather, scaleBad, below);
                return pixels;
            }, (result, error) =>
            {
                _reflectionBusy = false;

                _cubeBad = _fillBad;

                if (error == null && _cube != null && _probe != null && (_cubeExposure = _fillExposure) > 0f)
                {
                    UploadReflection(pixels);
                    _probe.intensity = Mathf.Clamp(EtsClimate.AppliedExposure / _cubeExposure, 1e-3f, 1e3f);
                }
            });
        }

        private static void FillReflection(Color[] pixels, Vector3 toSun, Panorama a, Panorama b, float blend, float scale, Panorama c, Panorama d, float badBlend, float weather, float scaleBad, Color below)
        {
            float sunAzimuth = Mathf.Atan2(toSun.x, toSun.z);
            int n = ReflectionSize;

            for (int face = 0; face < 6; face++)
            {
                for (int y = 0; y < n; y++)
                {
                    float t = 2f * (y + 0.5f) / n - 1f;

                    if (!CubeRowsTopDown)
                    {
                        t = -t;
                    }

                    for (int x = 0; x < n; x++)
                    {
                        float s = 2f * (x + 0.5f) / n - 1f;
                        Vector3 dir;

                        switch (face)
                        {
                            case 0: dir = new Vector3(1f, -t, -s); break;
                            case 1: dir = new Vector3(-1f, -t, s); break;
                            case 2: dir = new Vector3(s, 1f, t); break;
                            case 3: dir = new Vector3(s, -1f, -t); break;
                            case 4: dir = new Vector3(s, -t, 1f); break;
                            default: dir = new Vector3(-s, -t, -1f); break;
                        }

                        dir.Normalize();
                        Color sky = SkyAt(dir, sunAzimuth, a, b, blend, scale, below);

                        if (c != null && d != null && weather > 0f)
                        {
                            sky = Color.Lerp(sky, SkyAt(dir, sunAzimuth, c, d, badBlend, scaleBad, below), weather);
                        }

                        pixels[face * n * n + y * n + x] = sky;
                    }
                }
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

        /// <summary>An already decoded panorama of that name and size; null when there is none.</summary>
        private static Panorama Take(string name, int skip, ref bool kept)
        {
            if (!string.IsNullOrEmpty(name) && Cache.TryGetValue(name, out Panorama p) && p.Texture != null && p.Skip == skip)
            {
                kept = true;
                return p;
            }

            return null;
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
            Decoded d = DecodePanorama(files, tobj, skipMips);
            why = d.Why;

            if (d.Packed == null)
            {
                return null;
            }

            Panorama p = Upload(d);
            return p;
        }

        /// <summary>Pure (worker): the GPU mip and the small mip of a panorama, unpadded.</summary>
        private static Decoded DecodePanorama(EtsFileSystem files, string tobj, int skipMips)
        {
            var d = new Decoded { Name = tobj, Skip = skipMips };

            if (string.IsNullOrEmpty(tobj) || !files.TryGetEntry(tobj, out EtsHashFs archive, out EtsHashFs.Entry entry) || !entry.IsPackedImage)
            {
                d.Why = "sky texture " + tobj + " not found";
                return d;
            }

            if (entry.DxgiFormat != 67)
            {
                d.Why = "sky texture in DXGI " + entry.DxgiFormat + ", expected 67";
                return d;
            }

            byte[] surface = archive.ReadPackedImage(entry);
            int pitchAlign = Math.Max(1, entry.PitchAlign), imageAlign = Math.Max(1, entry.ImageAlign);
            int w = entry.ImageWidth, h = entry.ImageHeight, at = 0;
            int lastMip = Math.Max(1, entry.MipCount) - 1;
            int gpuMip = Math.Min(skipMips, lastMip);
            int cpuMip = Math.Min(ReflectionMip, lastMip);

            for (int m = 0; m <= Math.Max(gpuMip, cpuMip); m++)
            {
                at = Align(at, imageAlign);
                bool gpu = m == gpuMip, cpu = m == cpuMip;

                if (gpu)
                {
                    d.Packed = new byte[w * h * 4];
                    d.Width = w;
                    d.Height = h;
                }

                if (cpu)
                {
                    d.Small = new uint[w * h];
                    d.SmallWidth = w;
                    d.SmallHeight = h;
                }

                for (int r = 0; r < h; r++)
                {
                    at = Align(at, pitchAlign);

                    if ((gpu || cpu) && (surface == null || at + w * 4 > surface.Length))
                    {
                        d.Why = "sky texture shorter than its mips";
                        d.Packed = null;
                        return d;
                    }

                    if (gpu)
                    {
                        Buffer.BlockCopy(surface, at, d.Packed, r * w * 4, w * 4);
                    }

                    if (cpu)
                    {
                        Buffer.BlockCopy(surface, at, d.Small, r * w * 4, w * 4);
                    }

                    at += w * 4;
                }

                w = Math.Max(1, w / 2);
                h = Math.Max(1, h / 2);
            }

            return d;
        }

        /// <summary>
        /// Pure (worker): a skycloud mask as one byte a texel at 1024 wide or less - R8 (DXGI 61, the cumulus masks) or the red
        /// byte of B8G8R8A8/X8 (87/88: m_cl_sky, m_over_sky).
        /// </summary>
        private static Decoded DecodeMask(EtsFileSystem files, string tobj)
        {
            var d = new Decoded { Name = tobj, Mask = true };

            if (string.IsNullOrEmpty(tobj) || !files.TryGetEntry(tobj, out EtsHashFs archive, out EtsHashFs.Entry entry) || !entry.IsPackedImage)
            {
                d.Why = "mask " + tobj + " not found";
                return d;
            }

            int bytes = entry.DxgiFormat == 61 ? 1 : entry.DxgiFormat == 87 || entry.DxgiFormat == 88 || entry.DxgiFormat == 28 ? 4 : 0;

            if (bytes == 0)
            {
                d.Why = "mask in DXGI " + entry.DxgiFormat;
                return d;
            }

            byte[] surface = archive.ReadPackedImage(entry);
            int pitchAlign = Math.Max(1, entry.PitchAlign), imageAlign = Math.Max(1, entry.ImageAlign);
            int w = entry.ImageWidth, h = entry.ImageHeight, at = 0;
            int lastMip = Math.Max(1, entry.MipCount) - 1;
            int mip = 0;

            while (mip < lastMip && (w >> mip) > 1024)
            {
                mip++;
            }

            int red = entry.DxgiFormat == 28 ? 0 : 2;

            for (int m = 0; m <= mip; m++)
            {
                at = Align(at, imageAlign);

                if (m == mip)
                {
                    d.Packed = new byte[w * h];
                    d.Width = w;
                    d.Height = h;
                }

                for (int r = 0; r < h; r++)
                {
                    at = Align(at, pitchAlign);

                    if (m == mip)
                    {
                        if (surface == null || at + w * bytes > surface.Length)
                        {
                            d.Why = "mask shorter than its mips";
                            d.Packed = null;
                            return d;
                        }

                        if (bytes == 1)
                        {
                            Buffer.BlockCopy(surface, at, d.Packed, r * w, w);
                        }
                        else
                        {
                            for (int x = 0; x < w; x++)
                            {
                                d.Packed[r * w + x] = surface[at + x * 4 + red];
                            }
                        }
                    }

                    at += w * bytes;
                }

                w = Math.Max(1, w / 2);
                h = Math.Max(1, h / 2);
            }

            return d;
        }

        /// <summary>Main thread: an R9G9B9E5 texture (decoded to half floats on a device without the format).</summary>
        private static Texture2D PanoramaTexture(byte[] packed, int width, int height)
        {
            Texture2D texture;

            if (SystemInfo.SupportsTextureFormat(TextureFormat.RGB9e5Float))
            {
                texture = new Texture2D(width, height, TextureFormat.RGB9e5Float, false, true);
                texture.LoadRawTextureData(packed);
            }
            else
            {
                // Decode on the CPU: 9-bit mantissas, shared 5-bit exponent, bias 15.
                var half = new ushort[width * height * 4];

                for (int i = 0; i < width * height; i++)
                {
                    uint v = BitConverter.ToUInt32(packed, i * 4);
                    float scale = Mathf.Pow(2f, (int)(v >> 27) - 24);
                    half[i * 4] = Mathf.FloatToHalf((v & 0x1FF) * scale);
                    half[i * 4 + 1] = Mathf.FloatToHalf(((v >> 9) & 0x1FF) * scale);
                    half[i * 4 + 2] = Mathf.FloatToHalf(((v >> 18) & 0x1FF) * scale);
                    half[i * 4 + 3] = Mathf.FloatToHalf(1f);
                }

                texture = new Texture2D(width, height, TextureFormat.RGBAHalf, false, true);
                texture.SetPixelData(half, 0);
            }

            return texture;
        }
    }
}
