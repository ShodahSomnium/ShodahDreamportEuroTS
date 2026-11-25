using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.Rendering;

namespace SomniumSpace.Worlds.SomniumEuroTruck.Ets
{
    /// <summary>
    /// The game's own light and weather for one place and time, as far as Unity's render settings and a
    /// post-processing volume can follow them (lighting owner, 2026-09-23). Everything below was read from the
    /// 1.61 demo's files by harness probes 9800-9802 (scratchpad look/lighting/p98xx_*.txt).
    ///
    /// WHICH CLIMATE. Every map sector names one in its .desc file (u32 version, four boundary ints, u32 flags,
    /// then the climate token at byte 24): the demo's 282 sectors are 266 "default", 15 "arid" and 1 "cold", and
    /// the start (sector +0003-0004, Szczecin) is "default". /def/climate.sii's climate_profile units carry the
    /// map projection and "copy: default" (arid, cold and desert have no folder of their own and use
    /// /def/climate/default/nice.sii with other weather weights).
    ///
    /// WHEN. /def/initial_save/normal/game.sii game_time 600 = Monday 10:00; /def/env_data.sii day_in_year 172
    /// (the solstice) and summer_time 1 (+1 hour), so the sun stands where it does at 9:00 solar time. Latitude
    /// and longitude come from the climate's own Lambert conic projection (map_origin 50/15, map_offset
    /// 16660/4150, map_factor, standard parallels 37/65). At the start that is 55.6 N, 18.4 E and the sun 44
    /// degrees up in the east-south-east (azimuth 116).
    ///
    /// WHICH SUN PROFILE. nice.sii has one sun_profile per sun elevation (default.nice.01 = -40 degrees ...
    /// .18 = 89, rising, then back down to .34 = -30, setting). The two either side of the sun's elevation on the
    /// right side of noon are blended by elevation, 13 weather slots each; the slot is a weighted pick shared by every
    /// client (PickSlot; until 2026-09-25 it was the heaviest-weighted clear sky, slot 0, the only cloudless panorama).
    /// 44 degrees rising = default.nice.14 (40) and .15 (50), 40 % of the way.
    ///
    /// UNITS. Every colour in the file is linear HDR radiance for the game's auto-exposure (def_adapt: exposure =
    /// 1 / the histogram's average luminance, times the tone curve's target_gray) and its filmic curve
    /// (def_tonemap). Here one exposure factor scales sun, ambient, fog and sky together, so every RATIO the file
    /// states survives; since 2026-09-25 it is the centre of the game's own adaptation window (see below; before, an
    /// albedo-0.24 key gave 0.0806, 1.3 EV under the game's lowest value).
    /// The curve itself is EtsPostFx's (Neutral tonemapping, the file's saturation, contrast and bloom).
    ///
    /// 2026-09-25 (lighting, round 2). Exposure is now the game's own window, not a keyed estimate: the adapt pass
    /// (/effect/deferred/_shd/e10ad6f18ac883512f7facb14fd0c602.glsl.fso) clamps 1 / mean luminance to min_scale ..
    /// max_scale and the tone map multiplies by target_gray (/def/climate/integrity/nice.sii proves the product:
    /// target_gray 0.18 x scale 5.5554 = 1). The world adapts inside that window as the game does, to the mean of an
    /// in-cab view (CabKeyAlbedo): on the clear 10:00 sky that is the window's geometric centre, target_gray x
    /// sqrt(min_scale x max_scale) = 0.196 (nice.14/.15 40 %: 0.2562 x sqrt(0.568 x 1.032)), against the 0.0806 the old
    /// albedo key gave (1.3 EV too dark); an overcast slot adapts up toward max_scale instead of going 0.5 EV darker.
    /// The weather slot is picked as the game picks it - at random, weighted by the profile's weights, among every
    /// slot with a weight above 0 - from a seed every client shares (the build centre in wire centimetres and the
    /// UTC day, PickSlot), so each day and each headquarters has its own sky, clouds included (slots 5-11 carry
    /// painted cumulus), and all players see the same one. Every slot-dependent field is read from that slot.
    /// Ambient is the game's directional ambient (the sun pass /effect/deferred/_shd/6d0cdbb303e85feb8393a9f6504143fd
    /// .glsl.fso): ambient_env x (1 - 0.75 sat(-n.y)) x (1 - 0.27 sat(0.5 - 0.5 n.toSun)) written as the ambient probe;
    /// the near-field factor 0.6 + 0.4 sat(d^2 / 150^2) (lifted to 1 at grazing angles) is applied by the world's own
    /// shaders from _EtsAmbientNear. Environment reflection intensity is 0: URP otherwise adds the ambient colour as a
    /// Fresnel "sheen" to every surface without an environment term, which the game never does (EtsSky gives the
    /// materials that do reflect a sky cube of their own).
    ///
    /// FOG, the game's own law (the deferred fog pass /effect/deferred/_shd/49f94099c3c3639c12cc48552413d810.glsl.fso,
    /// blend one_minus_src_alpha / src_alpha): transmission = exp2(-fog_density x max(0, distance - fog_offset) x
    /// clamp01(exp2(-fog_vgradient x height))), colour = lerp(fog_color, fog_color2, clamp01(dot(view, sun))^2).
    /// Nothing is fogged inside fog_offset (139 m on a clear day). Unity has one fog colour and no offset for its
    /// exponential modes, so the world uses LINEAR fog from fog_offset to the end that fits the game's curve best
    /// over the built world (least squares to the stream radius): 0 % at 139 m, 9 % at 300 m, 20 % at 500 m, 42 %
    /// at 1 km, against the game's 0 / 9.5 / 20 / 41 %. The old setting (exponential 0.00089 from 0 m) was 23 %
    /// at 300 m and 59 % at 1 km, in the darker fog_color alone. The height term is left out: its reference plane
    /// is set by the engine, not the files, and a camera 2-3 m above flat ground sees factor 1 anyway.
    /// </summary>
    public sealed class EtsClimate
    {
        /// <summary>The start's time: /def/initial_save/normal/game.sii game_time (minutes since Monday 00:00).</summary>
        public const int DefaultGameMinutes = 600;

        /// <summary>Albedo of the "average surface" the exposure was keyed to before 2026-09-25 (see UNITS above; now only logged).</summary>
        public const float KeyAlbedo = 0.2373f;

        /// <summary>
        /// The albedo whose luminance under the profile's light stands for the mean of the view the game adapts to (Read): 0.101,
        /// the in-cab view of the reference shot u025 (a dark cab round a sunlit yard), fitted so the clear 10:00 sky (default.nice
        /// .14/.15 slot 8, the lab's match to u025) lands on its window's geometric centre. Only where a slot's light differs does
        /// the adaptation move: overcast skies up toward max_scale, brighter ones down toward min_scale, never outside the window.
        /// </summary>
        public const float CabKeyAlbedo = 0.1013f;

        /// <summary>The exposure the world was tuned on before (fixed default.nice.15 slot 0); kept as the fallback.</summary>
        public const float DefaultExposure = 0.065f;

        /// <summary>
        /// What the albedo key gave at 10:00 before 2026-09-25 (0.0806; the render lab's 0.0782 at Dover): anything whose
        /// brightness was tuned by eye against the old exposure (unlit lamps, emissive panels) can scale by
        /// <see cref="AppliedExposure"/> / this to keep its place relative to the lit world.
        /// </summary>
        public const float LegacyExposure = 0.0806f;

        /// <summary>The exposure the last Apply put into the render settings (LegacyExposure until then).</summary>
        public static float AppliedExposure = LegacyExposure;

        /// <summary>
        /// Fine tuning on top of the game's adapted exposure (1 = as adapted, see CabKeyAlbedo). The game's own window at 10:00
        /// is x0.74 .. x1.35 of its centre; keep it inside that.
        /// </summary>
        public static float ExposureTrim = 1f;

        /// <summary>A weather slot to use instead of the seeded pick (-1: the pick). For the render lab and tests.</summary>
        public static int ForcedSlot = -1;

        /// <summary>
        /// The day the weather pick is seeded with, in days since 1970-01-01 UTC; -1: today's UTC date. The session can
        /// set the master's day here (before LoadAt) so a player who joins after midnight UTC still sees the master's sky.
        /// </summary>
        public static int SharedDay = -1;

        // The game's directional ambient (the sun pass /effect/deferred/_shd/6d0cdbb303e85feb8393a9f6504143fd.glsl.fso):
        // ambient_env x (1 - AmbientDown sat(-n.y)) x (1 - AmbientAverted sat(0.5 - 0.5 n.toSun)), then x the near-field
        // factor AmbientNear + (1 - AmbientNear) sat(d^2 / AmbientNearRange^2), lifted toward 1 by (1 - n.v)^5.
        public const float AmbientDown = 0.75f;
        public const float AmbientAverted = 0.27f;
        public const float AmbientNear = 0.6f;
        public const float AmbientNearRange = 150f;

        /// <summary>Whether the world's shaders apply the near-field ambient factor (EtsMulti, EtsDecal; EtsLeaves by contract).</summary>
        public static bool NearFieldAmbient = true;

        // ------------------------------------------------------------------ where and when

        public string Climate = "default";
        public string Path;
        public string Sector;
        public float Latitude;
        public float Longitude;
        public int GameMinutes = DefaultGameMinutes;
        public int DayInYear = 172;
        public float SummerTime = 1f;

        /// <summary>The sun, degrees: elevation above the horizon, azimuth clockwise from north.</summary>
        public float Elevation;
        public float Azimuth;

        /// <summary>Direction TO the sun in the world's frame (x east, y up, z north: game z runs south).</summary>
        public Vector3 ToSun = new Vector3(0f, 0.766f, 0.643f);

        public string Profile;
        public string ProfileLow;
        public string ProfileHigh;

        /// <summary>0 = all ProfileLow, 1 = all ProfileHigh.</summary>
        public float Blend;
        public int Slot;
        public float Exposure = DefaultExposure;

        /// <summary>The game's adaptation window (min_scale, max_scale of the slot, blended) that Exposure adapts within.</summary>
        public float MinScale = 0.63f;
        public float MaxScale = 1.11f;

        /// <summary>Where in that window the view adapts to (Exposure = target_gray x this x ExposureTrim).</summary>
        public float AdaptScale = 0.84f;

        /// <summary>How the slot was chosen (the weights, the seed's parts), for the log.</summary>
        public string SlotWhy = "";

        /// <summary>What the old albedo key would have given (for the log: the change from before 2026-09-25).</summary>
        public float KeyedExposure;

        // ------------------------------------------------------------------ fog

        public float FogOffset;
        public float FogDensity;
        public float FogGradient;

        /// <summary>Unity linear fog fitted to the game's curve (see FOG above).</summary>
        public float FogStart;
        public float FogEnd;

        /// <summary>Unity exponential density fitted to the same curve, for a build that keeps only FOG_EXP.</summary>
        public float FogDensityExp;

        /// <summary>Unity exponential-squared density fitted to the same curve, for a build that keeps only FOG_EXP2.</summary>
        public float FogDensityExp2;

        /// <summary>The density RenderSettings takes for a mode.</summary>
        public float FogDensityFor(FogMode mode)
        {
            return mode == FogMode.ExponentialSquared ? FogDensityExp2 : FogDensityExp;
        }

        /// <summary>For RenderSettings, which takes gamma-space colours.</summary>
        public Color FogColor = new Color(0.30f, 0.39f, 0.56f, 1f);
        public Color FogColorLinear;
        public Color FogColor1Linear;
        public Color FogColor2Linear;

        /// <summary>How far toward fog_color2 the one Unity colour is: the mean of the game's t^2 round the horizon, cos^2(elevation) / 4.</summary>
        public float FogColor2Share;

        // ------------------------------------------------------------------ light and sky

        public Color SunLinear;
        public Color AmbientLinear;
        public Color SkyBottomLinear;
        public Color SunDiscLinear;

        /// <summary>sun_halo_color x Exposure: the additive glow the game draws round the disc (/asset/skybox/sun_halo.pmd).</summary>
        public Color SunHaloLinear;
        public float SunShadowStrength = 1f;

        /// <summary>sky_color (a grey scale on the panorama) times Exposure.</summary>
        public float SkyScale;

        /// <summary>The nearer profile's panorama (the one the log names).</summary>
        public string SkyTexture;

        /// <summary>The two profiles' panoramas of the slot; the sky crossfades them by Blend as eut2.sky does (_sky_blend_setup.x).</summary>
        public string SkyTextureLow;
        public string SkyTextureHigh;
        public string CloudTexture;

        // ------------------------------------------------------------------ the tone curve and post (EtsPostFx)

        public float TargetGray = 0.2585f;
        public float ShoulderLength = 1.28f;
        public float Contrast;
        public float Saturation = 1f;
        public Color ColorBalance = Color.black;
        public float BloomIntensity;
        public float BloomThreshold;
        public float BloomLimit = 26f;
        public float BloomSigma = 3.46f;
        public float DofStart;
        public float DofTransition;

        /// <summary>How far anything can usefully be seen, for cameras to clip at. Set by Apply.</summary>
        public static float Visibility = 1500f;

        /// <summary>A short key: a new one means the look changed (another climate, profile pair, slot or time).</summary>
        public string Key => Path + "|" + ProfileLow + "|" + ProfileHigh + "|" + Slot + "|" + Elevation.ToString("0.0", CultureInfo.InvariantCulture);

        /// <summary>The old call: the scene's start and time.</summary>
        public static EtsClimate Load(EtsFileSystem files)
        {
            return Load(files, 14994f, -15235f, -1, 1200f);
        }

        /// <summary>
        /// The climate at a map position (game x, z) and time (minutes since Monday 00:00; negative: the new
        /// career's start from /def/initial_save). <paramref name="fitDistance"/>: how far the built world reaches,
        /// for fitting Unity's fog to the game's. Null when the files have no sun profiles.
        /// </summary>
        public static EtsClimate Load(EtsFileSystem files, float gameX, float gameZ, int gameMinutes, float fitDistance)
        {
            var climate = new EtsClimate();

            // 1. The sector's climate token.
            EtsMapSector.SectorOf(gameX, gameZ, out int sx, out int sz);
            climate.Sector = EtsMapSector.FileName(sx, sz, "desc");
            byte[] desc = files.Read(climate.Sector);

            if (desc != null && desc.Length >= 32)
            {
                string token = EtsToken.Decode(BitConverter.ToUInt64(desc, 24));

                if (!string.IsNullOrEmpty(token))
                {
                    climate.Climate = token;
                }
            }

            // 2. Its climate_profile: the folder (or the one it copies), the projection, the weather weights.
            EtsSiiFile climates = files.ReadSii("/def/climate.sii");
            EtsSiiUnit profile = climates != null ? (climates.Find("climate." + climate.Climate) ?? climates.Find("climate.default")) : null;
            string folder = climate.Climate;

            if (!files.Exists("/def/climate/" + folder + "/nice.sii"))
            {
                string copy = profile != null ? profile.GetString("copy", null) : null;
                folder = !string.IsNullOrEmpty(copy) && files.Exists("/def/climate/" + EtsSiiValue.Unquote(copy) + "/nice.sii") ? EtsSiiValue.Unquote(copy) : "default";
            }

            climate.Path = "/def/climate/" + folder + "/nice.sii";
            climate.Geographic(profile, gameX, gameZ);

            // 3. When: the start's game time, the solstice and summer time from env_data.
            EtsSiiFile env = files.ReadSii("/def/env_data.sii");
            EtsSiiUnit envData = env != null ? env.Find("env.data") : null;

            if (envData != null)
            {
                climate.DayInYear = Mathf.RoundToInt(EtsSiiValue.ParseFloat(envData.GetString("day_in_year", "172"), 172f));
                climate.SummerTime = EtsSiiValue.ParseFloat(envData.GetString("summer_time", "1"), 1f);
            }

            climate.GameMinutes = gameMinutes >= 0 ? gameMinutes : InitialGameMinutes(files);
            climate.SunPosition();

            // 4. The two sun profiles either side of the sun, and the slot.
            EtsSiiFile sii = files.ReadSii(climate.Path);

            if (sii == null)
            {
                return null;
            }

            bool rising = climate.SolarHour() < 12f;
            EtsSiiUnit low = null, high = null;
            float lowElevation = float.MinValue, highElevation = float.MaxValue;
            EtsSiiUnit inside = null;

            foreach (EtsSiiUnit unit in sii.Units)
            {
                if (!unit.Has("fog_density") || !unit.Has("low_elevation"))
                {
                    continue;
                }

                // sun_direction: 1 rising, -1 setting, 0 the two ends of the arc (-40 and 89), shared by both.
                int direction = Mathf.RoundToInt(EtsSiiValue.ParseFloat(unit.GetString("sun_direction", "0"), 0f));

                if (direction != 0 && direction != (rising ? 1 : -1))
                {
                    continue;
                }

                float lo = EtsSiiValue.ParseFloat(unit.GetString("low_elevation"), -90f);
                float hi = EtsSiiValue.ParseFloat(unit.GetString("high_elevation"), lo);

                if (climate.Elevation >= lo && climate.Elevation <= hi)
                {
                    inside = unit;
                }

                if (hi <= climate.Elevation && hi > lowElevation)
                {
                    lowElevation = hi;
                    low = unit;
                }

                if (lo >= climate.Elevation && lo < highElevation)
                {
                    highElevation = lo;
                    high = unit;
                }
            }

            if (inside != null)
            {
                low = high = inside;
            }

            low = low ?? high;
            high = high ?? low;

            if (low == null)
            {
                return null;
            }

            climate.ProfileLow = low.Name;
            climate.ProfileHigh = high.Name;
            climate.Blend = low == high || highElevation <= lowElevation ? 0f : Mathf.Clamp01((climate.Elevation - lowElevation) / (highElevation - lowElevation));
            climate.Profile = low == high ? low.Name : low.Name + " + " + high.Name + " " + (climate.Blend * 100f).ToString("0", CultureInfo.InvariantCulture) + " %";
            climate.Slot = PickSlot(profile, low, gameX, gameZ, out climate.SlotWhy);
            climate.Read(low, high);
            climate.FitFog(Mathf.Max(fitDistance, climate.FogOffset + 100f));
            return climate;
        }

        /// <summary>/def/initial_save/normal/game.sii's game_time, or 600.</summary>
        private static int InitialGameMinutes(EtsFileSystem files)
        {
            string text = files.ReadText("/def/initial_save/normal/game.sii");

            if (text != null)
            {
                int at = text.IndexOf("game_time:", StringComparison.Ordinal);

                if (at >= 0)
                {
                    int end = text.IndexOf('\n', at);
                    string value = text.Substring(at + 10, (end < 0 ? text.Length : end) - at - 10).Trim();

                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int minutes) && minutes >= 0)
                    {
                        return minutes;
                    }
                }
            }

            return DefaultGameMinutes;
        }

        /// <summary>
        /// Latitude and longitude from the climate's projection: the linear offsets the file's comment gives, taken
        /// as angles on a unit sphere and put through the inverse Lambert conformal conic when map_projection says so.
        /// </summary>
        private void Geographic(EtsSiiUnit profile, float gameX, float gameZ)
        {
            float[] origin = profile != null ? profile.GetFloats("map_origin") : null;
            float[] offset = profile != null ? profile.GetFloats("map_offset") : null;
            float[] factor = profile != null ? profile.GetFloats("map_factor") : null;

            if (origin == null || origin.Length < 2) origin = new[] { 50f, 15f };
            if (offset == null || offset.Length < 2) offset = new[] { 16660f, 4150f };
            if (factor == null || factor.Length < 2) factor = new[] { -1.71570875e-4f, 1.729241463e-4f };

            double north = (gameZ - offset[0]) * factor[0];
            double east = (gameX - offset[1]) * factor[1];
            string projection = profile != null ? profile.GetString("map_projection", "") : "";

            if (projection.IndexOf("lambert", StringComparison.OrdinalIgnoreCase) < 0)
            {
                Latitude = (float)(origin[0] + north);
                Longitude = (float)(origin[1] + east);
                return;
            }

            const double D = Math.PI / 180.0;
            double p1 = EtsSiiValue.ParseFloat(profile.GetString("standard_paralel_1", "37"), 37f) * D;
            double p2 = EtsSiiValue.ParseFloat(profile.GetString("standard_paralel_2", "65"), 65f) * D;
            double p0 = origin[0] * D;
            double n = Math.Abs(p1 - p2) < 1e-6
                ? Math.Sin(p1)
                : Math.Log(Math.Cos(p1) / Math.Cos(p2)) / Math.Log(Math.Tan(Math.PI / 4 + p2 / 2) / Math.Tan(Math.PI / 4 + p1 / 2));
            double f = Math.Cos(p1) * Math.Pow(Math.Tan(Math.PI / 4 + p1 / 2), n) / n;
            double rho0 = f / Math.Pow(Math.Tan(Math.PI / 4 + p0 / 2), n);
            double x = east * D, y = north * D;
            double rho = Math.Sign(n) * Math.Sqrt(x * x + (rho0 - y) * (rho0 - y));
            double theta = Math.Atan2(x, rho0 - y);
            Latitude = (float)((2.0 * Math.Atan(Math.Pow(f / rho, 1.0 / n)) - Math.PI / 2) / D);
            Longitude = (float)(origin[1] + theta / n / D);
        }

        /// <summary>Hours of solar time: the game's clock less summer time (the clock is the same everywhere: g_time_zones 0).</summary>
        private float SolarHour()
        {
            float hour = (GameMinutes % 1440) / 60f - SummerTime;
            return hour < 0f ? hour + 24f : hour;
        }

        /// <summary>The sun for the latitude, day of the year and solar time (the standard declination and hour angle).</summary>
        private void SunPosition()
        {
            const double D = Math.PI / 180.0;
            double declination = 23.44 * D * Math.Sin(2.0 * Math.PI * (284 + DayInYear) / 365.0);
            double hourAngle = (SolarHour() - 12.0) * 15.0 * D;
            double lat = Latitude * D;
            double east = -Math.Cos(declination) * Math.Sin(hourAngle);
            double north = Math.Sin(declination) * Math.Cos(lat) - Math.Cos(declination) * Math.Cos(hourAngle) * Math.Sin(lat);
            double up = Math.Sin(declination) * Math.Sin(lat) + Math.Cos(declination) * Math.Cos(hourAngle) * Math.Cos(lat);
            ToSun = new Vector3((float)east, (float)up, (float)north).normalized;
            Elevation = (float)(Math.Asin(Math.Max(-1.0, Math.Min(1.0, up))) / D);
            Azimuth = (float)(Math.Atan2(east, north) / D);

            if (Azimuth < 0f)
            {
                Azimuth += 360f;
            }
        }

        /// <summary>The slot weights: the sun profile's own, or the climate's "weights" when it states its own (arid, desert).</summary>
        private static List<string> SlotWeights(EtsSiiUnit climateProfile, EtsSiiUnit sunProfile)
        {
            List<string> own = sunProfile.GetArray("weight");
            List<string> overrides = climateProfile != null ? climateProfile.GetArray("weights") : null;
            return overrides != null && overrides.Count >= own.Count && own.Count > 0 ? overrides : own;
        }

        /// <summary>Days since 1970-01-01 UTC: the shared day (SharedDay) or today's.</summary>
        public static int WeatherDay()
        {
            return SharedDay >= 0 ? SharedDay : (int)(DateTime.UtcNow.Date - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalDays;
        }

        /// <summary>
        /// The weather slot, picked as the game picks it: at random, weighted by the profile's weights, among every slot
        /// with a weight above 0 (default.nice.14: 3,3,3,1,0,3,3,3,3,3,3,3,1 - slots 5 to 11 carry painted cumulus, 3 and
        /// 12 are overcast). The "random" is a hash every client computes alike: the build centre as the session wire
        /// carries it (EtsSessionWire.GameToWire: whole centimetres of game x and -z, so the master's own centre and a
        /// joiner's decoded one give the same numbers) and the UTC day (or SharedDay). ForcedSlot overrides it.
        /// </summary>
        private static int PickSlot(EtsSiiUnit climateProfile, EtsSiiUnit sunProfile, float gameX, float gameZ, out string why)
        {
            List<string> weights = SlotWeights(climateProfile, sunProfile);
            int count = Mathf.Max(1, sunProfile.GetArray("weight").Count);

            if (ForcedSlot >= 0 && ForcedSlot < count)
            {
                why = "forced (EtsClimate.ForcedSlot)";
                return ForcedSlot;
            }

            int xcm = (int)Math.Round(gameX * 100.0);
            int zcm = (int)Math.Round(-(double)gameZ * 100.0);
            int day = WeatherDay();
            uint hash = unchecked(Hash((uint)xcm, (uint)zcm, (uint)day));
            double total = 0;

            for (int i = 0; i < count && i < weights.Count; i++)
            {
                total += Math.Max(0f, EtsSiiValue.ParseFloat(weights[i], 0f));
            }

            if (total <= 0)
            {
                why = "no weights: the clear slot";
                return ClearSlot(climateProfile, sunProfile);
            }

            double target = (hash >> 8) * (1.0 / 16777216.0) * total;
            double sum = 0;
            int pick = 0;

            for (int i = 0; i < count && i < weights.Count; i++)
            {
                float w = EtsSiiValue.ParseFloat(weights[i], 0f);

                if (w <= 0f)
                {
                    continue;
                }

                pick = i;
                sum += w;

                if (target < sum)
                {
                    break;
                }
            }

            why = "weighted pick " + (target / total).ToString("0.000", CultureInfo.InvariantCulture) + " of weights " + string.Join(",", weights.GetRange(0, Mathf.Min(count, weights.Count)).ToArray()) +
                  " (seed: centre " + xcm + "/" + zcm + " cm, UTC day " + day + (SharedDay >= 0 ? " shared" : "") + ")";
            return pick;
        }

        /// <summary>A small integer hash (FNV-1a over the words, then a murmur-style finaliser): the same on every machine.</summary>
        private static uint Hash(uint a, uint b, uint c)
        {
            unchecked
            {
                uint h = 2166136261u;
                h = (h ^ a) * 16777619u;
                h = (h ^ b) * 16777619u;
                h = (h ^ c) * 16777619u;
                h ^= h >> 16;
                h *= 0x7feb352du;
                h ^= h >> 15;
                h *= 0x846ca68bu;
                h ^= h >> 16;
                return h;
            }
        }

        /// <summary>The heaviest-weighted of the clear-sky slots (the climate's weights when it has its own): the fallback when no weight is above 0.</summary>
        private static int ClearSlot(EtsSiiUnit climateProfile, EtsSiiUnit sunProfile)
        {
            List<string> own = sunProfile.GetArray("weight");
            List<string> weights = SlotWeights(climateProfile, sunProfile);

            int clear = climateProfile != null ? Mathf.RoundToInt(EtsSiiValue.ParseFloat(climateProfile.GetString("clear_skyboxes_count", "0"), 0f)) : 0;
            int count = clear > 0 ? Mathf.Min(clear, Mathf.Max(1, own.Count)) : Mathf.Max(1, own.Count);
            int best = 0;
            float bestWeight = float.MinValue;

            for (int i = 0; i < count && i < weights.Count; i++)
            {
                float w = EtsSiiValue.ParseFloat(weights[i], 0f);

                if (w > bestWeight)
                {
                    bestWeight = w;
                    best = i;
                }
            }

            return best;
        }

        private void Read(EtsSiiUnit low, EtsSiiUnit high)
        {
            int slot = Slot;
            float t = Blend;

            TargetGray = Mix(low, high, "target_gray", slot, t, 0.2585f);
            ShoulderLength = Mix(low, high, "shoulder_length", slot, t, 1.28f);
            Contrast = Mix(low, high, "contrast", slot, t, 0f);
            Saturation = Mix(low, high, "color_saturation", slot, t, 1f);
            ColorBalance = MixRgb(low, high, "color_balance", slot, t);
            BloomIntensity = Mix(low, high, "bloom_intensity", slot, t, 0.1f);
            BloomThreshold = Mix(low, high, "bloom_threshold", slot, t, 0f);
            BloomLimit = Mix(low, high, "bloom_limit", slot, t, 26f);
            BloomSigma = Mix(low, high, "bloom_standard_deviation", slot, t, 3.46f);
            DofStart = Mix(low, high, "dof_start", slot, t, 250f);
            DofTransition = Mix(low, high, "dof_transition", slot, t, 820f);
            SunShadowStrength = Mathf.Clamp01(Mix(low, high, "sun_shadow_strength", slot, t, 1f));

            Color diffuse = MixRgb(low, high, "diffuse", slot, t);
            Color ambient = MixRgb(low, high, "ambient", slot, t);

            // The game's adaptation (class notes): exposure = target_gray x clamp(1 / mean luminance, min_scale, max_scale).
            // The view's mean luminance is that of a CabKeyAlbedo surface under the profile's sun and ambient (the in-cab
            // view of u025), which puts the clear 10:00 sky (slot 8) at its window's geometric centre, 0.193, where the render
            // lab matched u025; a darker sky adapts up toward max_scale as the game's does (overcast slot 3: 0.181, not the
            // centre's 0.136), a brighter one down toward min_scale. The old albedo key (KeyedExposure) gave 0.0806.
            MinScale = Mix(low, high, "min_scale", slot, t, 0.63f);
            MaxScale = Mix(low, high, "max_scale", slot, t, 1.11f);
            float light = Luminance(diffuse) * Mathf.Max(0f, Mathf.Sin(Elevation * Mathf.Deg2Rad)) + Luminance(ambient);
            KeyedExposure = KeyAlbedo * light > 1e-4f ? TargetGray / (KeyAlbedo * light) : DefaultExposure;
            float window = Mathf.Sqrt(Mathf.Max(1e-4f, MinScale * MaxScale));
            AdaptScale = CabKeyAlbedo * light > 1e-4f ? Mathf.Clamp(1f / (CabKeyAlbedo * light), Mathf.Min(MinScale, MaxScale), Mathf.Max(MinScale, MaxScale)) : window;
            Exposure = Mathf.Clamp(TargetGray * AdaptScale * Mathf.Clamp(ExposureTrim, 0.25f, 4f), 0.005f, 4f);

            FogDensity = Mix(low, high, "fog_density", slot, t, 0.0009f);
            FogOffset = Mix(low, high, "fog_offset", slot, t, 100f);
            FogGradient = Mix(low, high, "fog_vgradient", slot, t, 0f);
            FogColor1Linear = MixRgb(low, high, "fog_color", slot, t) * Exposure;
            FogColor2Linear = MixRgb(low, high, "fog_color2", slot, t) * Exposure;
            float c = Mathf.Cos(Elevation * Mathf.Deg2Rad);
            FogColor2Share = Mathf.Clamp01(c * c * 0.25f);
            FogColorLinear = Color.Lerp(FogColor1Linear, FogColor2Linear, FogColor2Share);
            FogColorLinear.a = 1f;
            FogColor = ToGamma(FogColorLinear);

            SunLinear = diffuse * Exposure;
            AmbientLinear = ambient * Exposure;
            SkyBottomLinear = MixRgb(low, high, "sky_bottom_color", slot, t) * Exposure;
            SunDiscLinear = MixRgb(low, high, "sun_color", slot, t) * Exposure;
            SunHaloLinear = MixRgb(low, high, "sun_halo_color", slot, t) * Exposure;
            SkyScale = MixRgb(low, high, "sky_color", slot, t).maxColorComponent * Exposure;

            // The panoramas of both profiles, crossfaded by the sky shader as eut2.sky does
            // (/effect/eut2/_shd/05b00a9aa2e6201b0d0a525f7f96e02d.glsl.fso: base_a + _sky_blend_setup.x (base_b - base_a)).
            SkyTextureLow = Text(low, "skybox_texture", slot);
            SkyTextureHigh = Text(high, "skybox_texture", slot);
            EtsSiiUnit near = t < 0.5f ? low : high;
            SkyTexture = Text(near, "skybox_texture", slot);
            CloudTexture = Text(near, "skycloud_mask_texture", slot);
        }

        /// <summary>
        /// Linear fog from the game's offset, its end fitted by least squares to the game's curve out to
        /// <paramref name="reach"/>; and an exponential density fitted the same way (for FOG_EXP only builds).
        /// </summary>
        private void FitFog(float reach)
        {
            FogStart = FogOffset;
            double sxy = 0, sxx = 0, ek = 0, ekk = 0, e2 = 0;
            const int Steps = 128;

            for (int i = 1; i <= Steps; i++)
            {
                double d = reach * i / Steps;
                double x = Math.Max(0.0, d - FogOffset);
                double game = 1.0 - Math.Pow(2.0, -FogDensity * x);
                sxy += game * x;
                sxx += x * x;

                // Exponential: -ln(1 - fog) = k d, fitted in that space through the origin; squared:
                // sqrt(-ln(1 - fog)) = k d, the same way.
                double y = -Math.Log(Math.Max(1e-6, 1.0 - game));
                ek += y * d;
                ekk += d * d;
                e2 += Math.Sqrt(y) * d;
            }

            double slope = sxx > 0 ? sxy / sxx : 0;
            FogEnd = slope > 1e-7 ? FogStart + (float)(1.0 / slope) : FogStart + 2000f;
            FogDensityExp = ekk > 0 ? (float)(ek / ekk) : FogDensity;
            FogDensityExp2 = ekk > 0 ? (float)(e2 / ekk) : FogDensity;
        }

        /// <summary>The game's own fog amount (0 clear .. 1 fog) at a distance, height factor 1.</summary>
        public float GameFog(float distance)
        {
            return 1f - Mathf.Pow(2f, -FogDensity * Mathf.Max(0f, distance - FogOffset));
        }

        /// <summary>Unity's fog amount for the mode Apply sets.</summary>
        public float UnityFog(float distance, FogMode mode)
        {
            switch (mode)
            {
                case FogMode.Linear: return Mathf.Clamp01((distance - FogStart) / Mathf.Max(1f, FogEnd - FogStart));
                case FogMode.Exponential: return 1f - Mathf.Exp(-FogDensityExp * distance);
                default: return 1f - Mathf.Exp(-(FogDensityExp2 * distance) * (FogDensityExp2 * distance));
            }
        }

        private static string Pick(EtsSiiUnit unit, string key, int slot)
        {
            List<string> values = unit.GetArray(key);

            if (values.Count > 1)
            {
                return values[Mathf.Clamp(slot, 0, values.Count - 1)];
            }

            if (values.Count == 1)
            {
                return values[0];
            }

            return unit.GetString(key, null);
        }

        private static float Scalar(EtsSiiUnit unit, string key, int slot, float fallback)
        {
            float[] parsed = EtsSiiValue.ParseFloats(Pick(unit, key, slot));
            return parsed.Length > 0 ? parsed[0] : fallback;
        }

        private static float Mix(EtsSiiUnit low, EtsSiiUnit high, string key, int slot, float t, float fallback)
        {
            float a = Scalar(low, key, slot, fallback);
            return low == high ? a : Mathf.Lerp(a, Scalar(high, key, slot, fallback), t);
        }

        private static Color Rgb(EtsSiiUnit unit, string key, int slot)
        {
            float[] rgb = EtsSiiValue.ParseFloats(Pick(unit, key, slot));
            return rgb.Length >= 3 ? new Color(rgb[0], rgb[1], rgb[2], 1f) : Color.black;
        }

        private static Color MixRgb(EtsSiiUnit low, EtsSiiUnit high, string key, int slot, float t)
        {
            Color a = Rgb(low, key, slot);
            Color c = low == high ? a : Color.Lerp(a, Rgb(high, key, slot), t);
            c.a = 1f;
            return c;
        }

        private static string Text(EtsSiiUnit unit, string key, int slot)
        {
            string raw = Pick(unit, key, slot);
            return string.IsNullOrEmpty(raw) ? null : EtsSiiValue.Unquote(raw);
        }

        public static float Luminance(Color c)
        {
            return 0.2126729f * c.r + 0.7151522f * c.g + 0.0721750f * c.b;
        }

        /// <summary>Linear to sRGB, done here rather than by Color.gamma so the file maths also runs outside the engine (probes).</summary>
        public static Color ToGamma(Color linear)
        {
            return new Color(Srgb(linear.r), Srgb(linear.g), Srgb(linear.b), 1f);
        }

        private static float Srgb(float v)
        {
            if (v <= 0f) return 0f;
            return v <= 0.0031308f ? v * 12.92f : 1.055f * Mathf.Pow(v, 1f / 2.4f) - 0.055f;
        }

        /// <summary>Where fog is complete: the end of the linear ramp, or 99 % for the exponential modes.</summary>
        public float FogFull(FogMode mode)
        {
            switch (mode)
            {
                case FogMode.Linear: return FogEnd;
                case FogMode.Exponential: return FogDensityExp > 0f ? 4.6f / FogDensityExp : 3000f;
                default: return FogDensityExp2 > 0f ? 2.15f / FogDensityExp2 : 3000f;
            }
        }

        /// <summary>The existing call site passes nothing: the scene's sun, linear fog.</summary>
        public void Apply()
        {
            Apply(FindSun(), FogMode.Linear);
        }

        public static Light FindSun()
        {
            Light sun = RenderSettings.sun;

            if (sun == null)
            {
                foreach (Light light in UnityEngine.Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
                {
                    if (light.type == LightType.Directional)
                    {
                        sun = light;
                        break;
                    }
                }
            }

            return sun;
        }

        /// <summary>
        /// Fog, sun (direction, colour, intensity, shadow strength) and ambient into Unity's render settings. The
        /// sky is EtsSky's (it needs the panorama and a material); call it after this.
        /// </summary>
        public void Apply(Light sun, FogMode mode)
        {
            RenderSettings.fog = true;
            RenderSettings.fogMode = mode;
            RenderSettings.fogColor = FogColor;
            RenderSettings.fogStartDistance = FogStart;
            RenderSettings.fogEndDistance = FogEnd;
            RenderSettings.fogDensity = FogDensityFor(mode);

            if (sun != null)
            {
                if (ToSun.sqrMagnitude > 0.5f)
                {
                    sun.transform.rotation = Quaternion.LookRotation(-ToSun, Vector3.up);
                }

                float peak = SunLinear.maxColorComponent;

                if (peak > 0f)
                {
                    // URP lights: colour is gamma-space and linearised, intensity is linear.
                    Color hue = SunLinear / peak;
                    hue.a = 1f;
                    sun.color = ToGamma(hue);
                    sun.intensity = peak;
                }

                // The game's shade is lit by the ambient alone (sun_shadow_strength 1).
                sun.shadowStrength = SunShadowStrength;
                RenderSettings.sun = sun;
            }

            // The file gives ONE ambient colour, but the game's sun pass does not add it flat: it scales it by the normal
            // (class notes, AmbientFactor) - a quarter under overhangs and leaves, about 0.77 on faces turned from the
            // sun. Trilight carries the up / horizon / down part for anything that recomputes the environment
            // (DynamicGI.UpdateEnvironment: EtsSky no longer calls it), and the probe is written directly with the
            // full factor, the sun side included, fitted to Unity's own SH evaluation (DirectionalAmbient).
            Vector3 toSun = ToSun.sqrMagnitude > 0.5f ? ToSun.normalized : new Vector3(0f, 0.766f, 0.643f);
            float horizon = 1f - AmbientAverted * 0.5f;
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = ToGamma(AmbientLinear * AmbientFactor(Vector3.up, toSun));
            RenderSettings.ambientEquatorColor = ToGamma(AmbientLinear * horizon);
            RenderSettings.ambientGroundColor = ToGamma(AmbientLinear * AmbientFactor(Vector3.down, toSun));
            RenderSettings.ambientIntensity = 1f;
            _fullProbe = DirectionalAmbient(AmbientLinear, toSun, out AmbientFitNote);
            _haveProbe = true;

            if (sun != null && (sun != _scaledSun || SunLinear.maxColorComponent > 0f))
            {
                _sunPeak = SunLinear.maxColorComponent > 0f ? SunLinear.maxColorComponent : sun.intensity;
            }

            _scaledSun = sun;
            AppliedExposure = Exposure;

            // The world's own shaders (EtsMulti, EtsDecal; EtsLeaves by the ambient contract): the near-field ambient
            // factor and the game's fog colour law, lerp(fog_color, fog_color2, sat(dot(view, toSun))^2). The fog colours
            // themselves are ScaleLights' (they follow the light scale, as the sky does).
            Shader.SetGlobalVector("_EtsAmbientNear", new Vector4(AmbientNear, 1f / (AmbientNearRange * AmbientNearRange), 5f, NearFieldAmbient ? 1f : 0f));
            Shader.SetGlobalVector("_EtsFogSunDir", new Vector4(toSun.x, toSun.y, toSun.z, 0f));
            _fogLinear = FogColorLinear;
            _fog1 = new Vector4(FogColor1Linear.r, FogColor1Linear.g, FogColor1Linear.b, 1f);
            _fog2 = new Vector4(FogColor2Linear.r, FogColor2Linear.g, FogColor2Linear.b, DirectionalFog ? 1f : 0f);
            _haveFog = true;
            _settingsScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            ScaleLights(PhaseScale);

            // No environment sheen (class notes): URP's _GlossyEnvironmentColor is the probe's DC x this, added with
            // Fresnel to every material whose effect has no environment term (_ENVIRONMENTREFLECTIONS_OFF); it also
            // scales the default (skybox) reflection, which EtsSky's own sky probe replaces for glass, water and chrome.
            RenderSettings.reflectionIntensity = 0f;

            Visibility = Mathf.Min(FogFull(mode) + 200f, 3000f);
        }

        // ------------------------------------------------------------------ light scale (full-view phases, the studio)

        private static Light _scaledSun;
        private static float _sunPeak;
        private static SphericalHarmonicsL2 _fullProbe;
        private static bool _haveProbe;
        private static bool _fullView;
        private static Color _fogLinear;
        private static Vector4 _fog1, _fog2;
        private static bool _haveFog;

        // The scene the last Apply wrote its render settings into (the active one then). ScaleLights writes the probe and the
        // fog colour only while that scene is still the active one: EtsFlow resets the phase (PhaseWeight 1) as the world is
        // torn down, when the active scene may already be the client's, whose render settings are not ours to change.
        private static UnityEngine.SceneManagement.Scene _settingsScene;

        /// <summary>
        /// EtsFlow's full-view phases, set through EtsPostFx.PhaseWeight (0 = full view): while the HQ stage or a loading screen
        /// is up the post volume (and with it the tone curve) is off, so the sun, the ambient, the sky and the fog colour go back
        /// to the level the void and the avatars were tuned on (LegacyScale: the old exposure over the climate's, 0.42 at 10:00);
        /// in the world, 1.
        /// </summary>
        public static bool FullViewPhase
        {
            get => _fullView;
            set
            {
                _fullView = value;
                ScaleLights(PhaseScale);
            }
        }

        /// <summary>The light scale of the current phase (see FullViewPhase).</summary>
        public static float PhaseScale => _fullView ? LegacyScale : 1f;

        /// <summary>The scale that puts the climate's lights back where the old exposure had them (1 before any Apply).</summary>
        public static float LegacyScale => AppliedExposure > 1e-4f ? Mathf.Min(1f, LegacyExposure / AppliedExposure) : 1f;

        /// <summary>
        /// The light scale for an offscreen picture that is shown in the world through the main camera's tone curve (a mirror's
        /// glass: the picture as an emissive plus the glass's own light; the route map on its panel): halfway in stops between
        /// the old level and the game's, 0.64 at 10:00. It keeps the sky of the picture under 1 (1.5 x 0.64 = 0.96, where the
        /// LDR target clips) and, with the glass's own light in the cab's shade (about 0.55 of the picture), puts the mirror
        /// image at the brightness of the view through the windscreen once the main tone curve has drawn it.
        /// </summary>
        public static float OffscreenScale => Mathf.Sqrt(LegacyScale);

        /// <summary>
        /// The sun, the ambient probe, the fog colours (Unity's and the world shaders' _EtsFogColor1/2) and the sky (the global
        /// _EtsLookDim = 1 - scale, which Shaders/EtsSky.shader multiplies by) at <paramref name="scale"/> x what the climate gave
        /// them. EtsGarageScene uses it round the truck studio's camera and EtsMapViewer round every other offscreen camera
        /// without a tone curve (the mirrors, the route advisor's map: they see the scene clipped at 1), and both put PhaseScale
        /// back after it. Cheap: one light, one probe, one fog colour and three shader globals; nothing allocated.
        /// </summary>
        public static void ScaleLights(float scale)
        {
            if (_scaledSun != null)
            {
                _scaledSun.intensity = _sunPeak * scale;
            }

            bool ours = _settingsScene.IsValid() && UnityEngine.SceneManagement.SceneManager.GetActiveScene() == _settingsScene;

            if (_haveProbe && ours)
            {
                RenderSettings.ambientProbe = _fullProbe * scale;
            }

            if (_haveFog)
            {
                if (ours)
                {
                    Color fog = _fogLinear * scale;
                    fog.a = 1f;
                    RenderSettings.fogColor = ToGamma(fog);
                }

                Vector4 fog1 = _fog1 * scale, fog2 = _fog2 * scale;
                fog1.w = 1f;
                fog2.w = _fog2.w;
                Shader.SetGlobalVector("_EtsFogColor1", fog1);
                Shader.SetGlobalVector("_EtsFogColor2", fog2);
            }

            Shader.SetGlobalFloat("_EtsLookDim", Mathf.Clamp01(1f - scale));
        }

        /// <summary>Whether the world's shaders colour their fog by the view's angle to the sun (the game's law) or take Unity's one fog colour.</summary>
        public static bool DirectionalFog = true;

        /// <summary>How well the probe fits the game's directional factor (the last Apply), for the log.</summary>
        public string AmbientFitNote = "";

        /// <summary>The game's directional ambient factor for a normal, far from the camera (class notes; 1 = ambient_env as the file states it).</summary>
        public static float AmbientFactor(Vector3 normal, Vector3 toSun)
        {
            float down = Mathf.Clamp01(-normal.y);
            float averted = Mathf.Clamp01(0.5f - 0.5f * Vector3.Dot(normal, toSun));
            return (1f - AmbientDown * down) * (1f - AmbientAverted * averted);
        }

        private static Vector3[] _shDirections;
        private static float[] _shBasis;

        /// <summary>
        /// ambient x AmbientFactor(n) as an L2 probe: least squares over 512 directions spread evenly on the sphere, against the
        /// nine basis functions as Unity itself evaluates them (SphericalHarmonicsL2.Evaluate, which is what SampleSH returns),
        /// so no SH convention is assumed. Runs only when the climate is applied; the basis is computed once.
        /// </summary>
        public static SphericalHarmonicsL2 DirectionalAmbient(Color ambient, Vector3 toSun, out string note)
        {
            const int Count = 512;

            if (_shDirections == null || _shBasis == null)
            {
                var directions = new Vector3[Count];
                double golden = Math.PI * (3.0 - Math.Sqrt(5.0));

                for (int i = 0; i < Count; i++)
                {
                    double y = 1.0 - (i + 0.5) * 2.0 / Count;
                    double r = Math.Sqrt(Math.Max(0.0, 1.0 - y * y));
                    double phi = golden * i;
                    directions[i] = new Vector3((float)(Math.Cos(phi) * r), (float)y, (float)(Math.Sin(phi) * r));
                }

                var basis = new float[9 * Count];
                var results = new Color[Count];

                for (int k = 0; k < 9; k++)
                {
                    var single = new SphericalHarmonicsL2();
                    single[0, k] = 1f;
                    single.Evaluate(directions, results);

                    for (int i = 0; i < Count; i++)
                    {
                        basis[k * Count + i] = results[i].r;
                    }
                }

                _shDirections = directions;
                _shBasis = basis;
            }

            // Normal equations M x = b for the scalar factor; the colour scales the solution per channel.
            var m = new double[9, 10];

            for (int i = 0; i < Count; i++)
            {
                double f = AmbientFactor(_shDirections[i], toSun);

                for (int a = 0; a < 9; a++)
                {
                    double ba = _shBasis[a * Count + i];

                    for (int b = 0; b < 9; b++)
                    {
                        m[a, b] += ba * _shBasis[b * Count + i];
                    }

                    m[a, 9] += ba * f;
                }
            }

            double[] x = Solve(m);
            var probe = new SphericalHarmonicsL2();

            if (x == null)
            {
                // A degenerate basis (should never happen): the old flat ambient at the horizon's mean factor.
                probe.AddAmbientLight(ambient * (1f - AmbientAverted * 0.5f));
                note = "flat (fit failed)";
                return probe;
            }

            for (int k = 0; k < 9; k++)
            {
                probe[0, k] = (float)(x[k] * ambient.r);
                probe[1, k] = (float)(x[k] * ambient.g);
                probe[2, k] = (float)(x[k] * ambient.b);
            }

            // The fit's worst error over the directions, and what it gives at five normals, for the log.
            double worst = 0;

            for (int i = 0; i < Count; i++)
            {
                double s = 0;

                for (int k = 0; k < 9; k++)
                {
                    s += x[k] * _shBasis[k * Count + i];
                }

                worst = Math.Max(worst, Math.Abs(s - AmbientFactor(_shDirections[i], toSun)));
            }

            var probeDirs = new[] { Vector3.up, new Vector3(toSun.x, 0f, toSun.z).normalized, new Vector3(-toSun.x, 0f, -toSun.z).normalized, Vector3.down };
            var probeOut = new Color[probeDirs.Length];
            probe.Evaluate(probeDirs, probeOut);
            CultureInfo inv = CultureInfo.InvariantCulture;
            float lum = Mathf.Max(1e-6f, Luminance(ambient));
            note = "directional fit (worst error " + worst.ToString("0.000", inv) + "): up " + (Luminance(probeOut[0]) / lum).ToString("0.00", inv) + " (game " + AmbientFactor(probeDirs[0], toSun).ToString("0.00", inv) +
                   "), toward the sun " + (Luminance(probeOut[1]) / lum).ToString("0.00", inv) + " (" + AmbientFactor(probeDirs[1], toSun).ToString("0.00", inv) +
                   "), away " + (Luminance(probeOut[2]) / lum).ToString("0.00", inv) + " (" + AmbientFactor(probeDirs[2], toSun).ToString("0.00", inv) +
                   "), down " + (Luminance(probeOut[3]) / lum).ToString("0.00", inv) + " (" + AmbientFactor(probeDirs[3], toSun).ToString("0.00", inv) + ") of ambient_env";
            return probe;
        }

        /// <summary>Gaussian elimination with partial pivoting on an n x (n+1) augmented matrix; null when singular.</summary>
        private static double[] Solve(double[,] a)
        {
            int n = a.GetLength(0);

            for (int c = 0; c < n; c++)
            {
                int pivot = c;

                for (int r = c + 1; r < n; r++)
                {
                    if (Math.Abs(a[r, c]) > Math.Abs(a[pivot, c]))
                    {
                        pivot = r;
                    }
                }

                if (Math.Abs(a[pivot, c]) < 1e-12)
                {
                    return null;
                }

                if (pivot != c)
                {
                    for (int k = c; k <= n; k++)
                    {
                        double swap = a[c, k];
                        a[c, k] = a[pivot, k];
                        a[pivot, k] = swap;
                    }
                }

                for (int r = c + 1; r < n; r++)
                {
                    double f = a[r, c] / a[c, c];

                    for (int k = c; k <= n; k++)
                    {
                        a[r, k] -= f * a[c, k];
                    }
                }
            }

            var x = new double[n];

            for (int r = n - 1; r >= 0; r--)
            {
                double s = a[r, n];

                for (int k = r + 1; k < n; k++)
                {
                    s -= a[r, k] * x[k];
                }

                x[r] = s / a[r, r];
            }

            return x;
        }

        /// <summary>hh:mm of the game clock.</summary>
        public string ClockText()
        {
            int m = ((GameMinutes % 1440) + 1440) % 1440;
            return (m / 60).ToString("00", CultureInfo.InvariantCulture) + ":" + (m % 60).ToString("00", CultureInfo.InvariantCulture);
        }

        public override string ToString()
        {
            CultureInfo i = CultureInfo.InvariantCulture;
            return "Climate '" + Climate + "' (" + Sector + ", " + Latitude.ToString("0.0", i) + " N " + Longitude.ToString("0.0", i) + " E) at " + ClockText() +
                   " day " + DayInYear + ": sun " + Elevation.ToString("0.0", i) + " deg up, azimuth " + Azimuth.ToString("0", i) + "; " + Path + " " + Profile +
                   " slot " + Slot + " (" + SlotWhy + "); exposure " + Exposure.ToString("0.0000", i) + " = target_gray " + TargetGray.ToString("0.000", i) + " x adapted " +
                   AdaptScale.ToString("0.000", i) + " in min_scale " + MinScale.ToString("0.000", i) + " .. max_scale " + MaxScale.ToString("0.000", i) +
                   " (centre " + Mathf.Sqrt(Mathf.Max(0f, MinScale * MaxScale)).ToString("0.000", i) + ")" + (Mathf.Abs(ExposureTrim - 1f) > 1e-3f ? " x trim " + ExposureTrim.ToString("0.00", i) : "") +
                   " (the old albedo key: " + KeyedExposure.ToString("0.0000", i) + ")" +
                   "; fog game exp2(-" + FogDensity.ToString("0.000000", i) + " x (d - " + FogOffset.ToString("0", i) + " m)), vgradient " + FogGradient.ToString("0.000", i) +
                   " -> Unity linear " + FogStart.ToString("0", i) + "-" + FogEnd.ToString("0", i) + " m (exp fit " + FogDensityExp.ToString("0.000000", i) + ", exp2 fit " + FogDensityExp2.ToString("0.000000", i) + "), colour " +
                   FogColor.ToString("0.00") + " (fog_color2 share " + FogColor2Share.ToString("0.00", i) + ")" +
                   "; sun " + SunLinear.ToString("0.00") + " shadow " + SunShadowStrength.ToString("0.00", i) + "; ambient " + AmbientLinear.ToString("0.000") +
                   "; sky x" + SkyScale.ToString("0.00", i) + " " + SkyTextureLow + (SkyTextureHigh != SkyTextureLow ? " + " + SkyTextureHigh : "") +
                   "; sun disc " + SunDiscLinear.ToString("0.00") + " halo " + SunHaloLinear.ToString("0.00") + "; below the horizon " + SkyBottomLinear.ToString("0.000") +
                   "; curve target_gray " + TargetGray.ToString("0.000", i) + " shoulder " + ShoulderLength.ToString("0.00", i) + " contrast " + Contrast.ToString("0.000", i) +
                   " saturation " + Saturation.ToString("0.000", i) + " bloom " + BloomIntensity.ToString("0.000", i) + " (limit " + BloomLimit.ToString("0.0", i) + ").";
        }
    }
}
