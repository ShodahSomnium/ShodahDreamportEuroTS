using System;
using System.Collections.Generic;
using System.Globalization;

namespace SomniumSpace.Worlds.SomniumEuroTruck.Ets
{
    /// <summary>
    /// One climate weather file - /def/climate/&lt;climate&gt;/nice.sii or bad.sii - read once into plain arrays, so the day can
    /// run through it every few frames without the SII parser, strings or allocations (2026-10-14, time of day and weather).
    ///
    /// What the files hold (1.61 demo, probe daynight/clim.exe; README "2026-10-14"): 34 sun_profile units per file, one per
    /// sun elevation on each side of noon (-40, -30, -25, -20, -15, -9/-10, -7, -4, 0, 6, 10, 20, 25, 40, 50, 65, 75, 89;
    /// sun_direction 1 rising, -1 setting, 0 the two ends); every field is an array over the weather slots (nice 13, bad 10)
    /// with that slot's weight. The night profiles (elevation -4 and below) carry the moon light as "diffuse", the stars
    /// (stars_texture /asset/skybox/shared/stars01.tobj x starmap_color, the star list x stars_color), the moon (moon_color,
    /// moon_halo_color, moon_halo_scale) and an adaptation window of min_scale 456 .. max_scale 1744 (day: 0.37 .. 1.1).
    /// bad.sii adds rain_intensity (0.2 .. 0.94), rain_max_wetness (0.19 .. 1), lightning_intensity, a lightning_mask per
    /// slot and fog of 0.0037 from 12 m (nice: 0.0009 from 139 m).
    ///
    /// Pure: no engine call, so it parses on a worker thread and the harness can read it offline.
    /// </summary>
    public sealed class EtsWeatherTable
    {
        // Field layout of one slot's numbers (colours take three).
        public const int Ambient = 0, Diffuse = 3, SkyColor = 6, SkyBottom = 7, SunColor = 10, SunHalo = 13, Shadow = 16,
                         MoonColor = 17, MoonHalo = 20, MoonHaloScale = 23, Stars = 24, Starmap = 25, Fog1 = 28, Fog2 = 31,
                         FogGradient = 34, FogOffset = 35, FogDensity = 36, Rain = 37, Lightning = 38, MaxWetness = 39,
                         RainAmbient = 40, CloudShadow = 41, MinScale = 42, MaxScale = 43, TargetGray = 44, Contrast = 45,
                         Shoulder = 46, Saturation = 47, BloomThreshold = 48, BloomLimit = 49, BloomIntensity = 50, BloomSigma = 51,
                         Balance = 52, DofStart = 55, DofTransition = 56;

        public const int FieldCount = 57;

        // Texture names per slot.
        public const int TextSky = 0, TextMask = 1, TextStars = 2, TextLightning = 3, TextCount = 4;

        private static readonly string[] Keys =
        {
            "ambient", "diffuse", "sky_color", "sky_bottom_color", "sun_color", "sun_halo_color", "sun_shadow_strength",
            "moon_color", "moon_halo_color", "moon_halo_scale", "stars_color", "starmap_color", "fog_color", "fog_color2",
            "fog_vgradient", "fog_offset", "fog_density", "rain_intensity", "lightning_intensity", "rain_max_wetness",
            "rain_additional_ambient", "cloud_shadow_weight", "min_scale", "max_scale", "target_gray", "contrast",
            "shoulder_length", "color_saturation", "bloom_threshold", "bloom_limit", "bloom_intensity", "bloom_standard_deviation",
            "color_balance", "dof_start", "dof_transition"
        };

        // Where each key lands, how many numbers it takes (3 colour, 1 scalar; sky_color and stars_color are greys, their max),
        // and the value used when a profile does not state it (the same fallbacks EtsClimate.Read always used).
        private static readonly int[] At = { Ambient, Diffuse, SkyColor, SkyBottom, SunColor, SunHalo, Shadow, MoonColor, MoonHalo, MoonHaloScale, Stars, Starmap, Fog1, Fog2, FogGradient, FogOffset, FogDensity, Rain, Lightning, MaxWetness, RainAmbient, CloudShadow, MinScale, MaxScale, TargetGray, Contrast, Shoulder, Saturation, BloomThreshold, BloomLimit, BloomIntensity, BloomSigma, Balance, DofStart, DofTransition };
        private static readonly int[] Width = { 3, 3, -1, 3, 3, 3, 1, 3, 3, 1, -1, 3, 3, 3, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 3, 1, 1 };
        private static readonly float[] Fallback = { 0, 0, 0, 0, 0, 0, 1, 0, 0, 2.4f, 0, 0, 0, 0, 0, 100, 0.0009f, 0, 0, 0, 5, 0, 0.63f, 1.11f, 0.2585f, 0, 1.28f, 1, 0, 26, 0.1f, 3.46f, 0, 250, 820 };

        private static readonly string[] TextKeys = { "skybox_texture", "skycloud_mask_texture", "stars_texture", "lightning_mask" };

        public sealed class Profile
        {
            public string Name;
            public float Elevation;
            public int Direction;
            public float[][] Slots;
            public string[][] Texts;
        }

        public string Path;
        public readonly List<Profile> Profiles = new List<Profile>();
        public int SlotCount;

        /// <summary>The slot weights (the profile's own "weight", or the climate's "weights" part for this file).</summary>
        public float[] Weights = new float[0];

        /// <summary>
        /// Reads a weather file. <paramref name="climateWeights"/>: the climate_profile's own "weights" (arid and desert state 23:
        /// the 13 nice slots, then the 10 bad ones), <paramref name="weightsOffset"/> where this file's part starts (0 nice, 13 bad).
        /// </summary>
        public static EtsWeatherTable Parse(EtsSiiFile sii, string path, List<string> climateWeights, int weightsOffset)
        {
            var table = new EtsWeatherTable { Path = path };

            if (sii == null)
            {
                return table;
            }

            foreach (EtsSiiUnit unit in sii.Units)
            {
                if (!unit.Has("low_elevation") || !unit.Has("fog_density"))
                {
                    continue;
                }

                List<string> weights = unit.GetArray("weight");
                int slots = Math.Max(1, weights.Count);

                if (table.SlotCount == 0)
                {
                    table.SlotCount = slots;
                    table.Weights = new float[slots];

                    for (int s = 0; s < slots; s++)
                    {
                        string w = climateWeights != null && climateWeights.Count >= weightsOffset + slots ? climateWeights[weightsOffset + s] : s < weights.Count ? weights[s] : "1";
                        table.Weights[s] = Math.Max(0f, EtsSiiValue.ParseFloat(w, 0f));
                    }
                }

                var profile = new Profile
                {
                    Name = unit.Name,
                    Elevation = EtsSiiValue.ParseFloat(unit.GetString("low_elevation"), 0f),
                    Direction = (int)Math.Round(EtsSiiValue.ParseFloat(unit.GetString("sun_direction", "0"), 0f)),
                    Slots = new float[table.SlotCount][],
                    Texts = new string[table.SlotCount][]
                };

                for (int s = 0; s < table.SlotCount; s++)
                {
                    var values = new float[FieldCount];

                    for (int k = 0; k < Keys.Length; k++)
                    {
                        float[] parsed = EtsSiiValue.ParseFloats(Pick(unit, Keys[k], s));
                        int width = Width[k];

                        if (width == 3)
                        {
                            for (int c = 0; c < 3; c++)
                            {
                                values[At[k] + c] = parsed.Length >= 3 ? parsed[c] : Fallback[k];
                            }
                        }
                        else if (width == -1)
                        {
                            // A grey scale written as a colour (sky_color, stars_color): its largest part, as EtsClimate always read it.
                            float max = parsed.Length > 0 ? parsed[0] : Fallback[k];

                            for (int c = 1; c < parsed.Length && c < 3; c++)
                            {
                                max = Math.Max(max, parsed[c]);
                            }

                            values[At[k]] = max;
                        }
                        else
                        {
                            values[At[k]] = parsed.Length > 0 ? parsed[0] : Fallback[k];
                        }
                    }

                    var texts = new string[TextCount];

                    for (int k = 0; k < TextCount; k++)
                    {
                        string raw = Pick(unit, TextKeys[k], s);
                        texts[k] = string.IsNullOrEmpty(raw) ? null : EtsSiiValue.Unquote(raw);

                        if (texts[k] != null && texts[k].Length == 0)
                        {
                            texts[k] = null;
                        }
                    }

                    profile.Slots[s] = values;
                    profile.Texts[s] = texts;
                }

                table.Profiles.Add(profile);
            }

            return table;
        }

        private static string Pick(EtsSiiUnit unit, string key, int slot)
        {
            List<string> values = unit.GetArray(key);

            if (values.Count > 1)
            {
                return values[Math.Min(Math.Max(slot, 0), values.Count - 1)];
            }

            if (values.Count == 1)
            {
                return values[0];
            }

            return unit.GetString(key, null);
        }

        public bool Empty => Profiles.Count == 0 || SlotCount == 0;

        /// <summary>
        /// The two profiles either side of the sun's elevation on its side of noon and how far between them (0 = all lo), as
        /// EtsClimate.Load has always found them; -1 when the file has none.
        /// </summary>
        public void Find(float elevation, bool rising, out int lo, out int hi, out float t)
        {
            lo = hi = -1;
            t = 0f;
            float loElevation = float.MinValue, hiElevation = float.MaxValue;
            int inside = -1;

            for (int i = 0; i < Profiles.Count; i++)
            {
                Profile p = Profiles[i];

                if (p.Direction != 0 && p.Direction != (rising ? 1 : -1))
                {
                    continue;
                }

                if (Math.Abs(elevation - p.Elevation) < 1e-4f)
                {
                    inside = i;
                }

                if (p.Elevation <= elevation && p.Elevation > loElevation)
                {
                    loElevation = p.Elevation;
                    lo = i;
                }

                if (p.Elevation >= elevation && p.Elevation < hiElevation)
                {
                    hiElevation = p.Elevation;
                    hi = i;
                }
            }

            if (inside >= 0)
            {
                lo = hi = inside;
            }

            if (lo < 0) lo = hi;
            if (hi < 0) hi = lo;

            if (lo >= 0 && lo != hi && hiElevation > loElevation)
            {
                t = Math.Max(0f, Math.Min(1f, (elevation - loElevation) / (hiElevation - loElevation)));
            }
        }

        /// <summary>Every number of the slot, blended between two profiles, into <paramref name="into"/> (FieldCount long).</summary>
        public void Mix(int lo, int hi, float t, int slot, float[] into)
        {
            slot = Math.Max(0, Math.Min(slot, SlotCount - 1));
            float[] a = Profiles[lo].Slots[slot];
            float[] b = Profiles[hi].Slots[slot];

            for (int i = 0; i < FieldCount; i++)
            {
                into[i] = a[i] + (b[i] - a[i]) * t;
            }
        }

        public string Text(int profile, int slot, int which)
        {
            if (profile < 0 || profile >= Profiles.Count)
            {
                return null;
            }

            slot = Math.Max(0, Math.Min(slot, SlotCount - 1));
            return Profiles[profile].Texts[slot][which];
        }

        /// <summary>A slot picked as the game picks one: at random by weight, among the slots weighted above 0 (u: 0..1).</summary>
        public int PickSlot(double u)
        {
            double total = 0;

            for (int i = 0; i < Weights.Length; i++)
            {
                total += Weights[i];
            }

            if (total <= 0)
            {
                return 0;
            }

            double target = u * total, sum = 0;
            int pick = 0;

            for (int i = 0; i < Weights.Length; i++)
            {
                if (Weights[i] <= 0f)
                {
                    continue;
                }

                pick = i;
                sum += Weights[i];

                if (target < sum)
                {
                    break;
                }
            }

            return pick;
        }

        public string WeightsText()
        {
            var parts = new string[Weights.Length];

            for (int i = 0; i < Weights.Length; i++)
            {
                parts[i] = Weights[i].ToString("0.##", CultureInfo.InvariantCulture);
            }

            return string.Join(",", parts);
        }
    }

    /// <summary>
    /// The shared clock and weather, as pure functions of the game time every client already shares (2026-10-14).
    ///
    /// CLOCK. The game's own: /def/initial_save/normal/game.sii game_time 600 (Monday 10:00) at the start, /def/env_data.sii
    /// day_in_year 172 and summer_time 1 for the sun, and the map's time compression: /map/europe.mbd stores
    /// time_compression_normal 19 and time_compresion_city 3 (the exe's names for the two floats at bytes 60 and 64 of the
    /// 69-byte file; the city value is the slower clock in towns, not used: one clock is shared by every player). So 19 game
    /// minutes pass every real minute and a day takes 76 real minutes. The session's master writes the minute
    /// (EtsSessionWire int 21, ClockScale); everyone draws between writes at the same rate.
    ///
    /// WEATHER. The climate's bad_weather_factor (/def/climate.sii: default 0.07, arid 0.03; the exe sets g_bad_weather_factor
    /// from it on entering a climate, game.log "uset g_bad_weather_factor 0.07") is the chance that the weather turns bad. How
    /// long one weather lasts is the engine's ("[env] Regular weather: ... weather %.1fmin" - not in any file): here a weather
    /// period is one game hour (3.2 real minutes), turning over 20 game minutes, rolled from a hash of the session's place and
    /// the hour, so every client computes the same sky without a byte on the wire. A run of bad hours keeps one bad slot
    /// (weighted by bad.sii's weights). Road wetness follows wetting_factor 0.1 and drying_factor 0.01 (per game minute here;
    /// the engine's unit is not in the files) up to the slot's rain_max_wetness.
    /// </summary>
    public static class EtsWeatherClock
    {
        /// <summary>time_compression_normal of /map/europe.mbd (19 in 1.61): game minutes a real minute.</summary>
        public static float TimeCompression = 19f;

        /// <summary>Game minutes one weather lasts, and how long it takes to turn (engine values, not in the files; see the class notes).</summary>
        public const int Period = 60;
        public const float Turn = 20f;

        /// <summary>The climate's bad_weather_factor.</summary>
        public static float BadFactor = 0.07f;

        public static float WettingFactor = 0.1f;
        public static float DryingFactor = 0.01f;

        /// <summary>/def/env_data.sii lamps_on_elevation: street and vehicle lamps on below this sun elevation (degrees).</summary>
        public static float LampsOnElevation = 5f;

        /// <summary>/def/env_data.sii thunderstorm_probability: the share of bad weathers that thunder.</summary>
        public static float ThunderProbability = 0.37f;

        /// <summary>Reads time_compression_normal from /map/europe.mbd (two floats at 60 and 64 in the 1.61 layout); keeps 19 when the bytes do not look like it.</summary>
        public static string ReadTimeCompression(byte[] mbd)
        {
            if (mbd == null || mbd.Length < 68)
            {
                return "time compression 19 (no /map/europe.mbd)";
            }

            float normal = BitConverter.ToSingle(mbd, 60);
            float city = BitConverter.ToSingle(mbd, 64);

            if (normal >= 1f && normal <= 100f && city >= 1f && city <= normal)
            {
                TimeCompression = normal;
                return "time compression " + normal.ToString("0.##", CultureInfo.InvariantCulture) + " (city " + city.ToString("0.##", CultureInfo.InvariantCulture) + ", /map/europe.mbd)";
            }

            return "time compression 19 (europe.mbd bytes 60/64 read " + normal + "/" + city + ")";
        }

        public static uint Hash(uint a, uint b, uint c)
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

        /// <summary>0..1 from a hash.</summary>
        public static double Unit(uint h)
        {
            return (h >> 8) * (1.0 / 16777216.0);
        }

        /// <summary>Is weather period <paramref name="period"/> bad, for this place's seed.</summary>
        public static bool Bad(uint seed, long period)
        {
            return Unit(Hash(seed, (uint)period, 0xB4Du)) < BadFactor;
        }

        /// <summary>How bad the weather is at a game time: 0 nice .. 1 bad, turning smoothly at the edges of a bad run.</summary>
        public static float Mix(uint seed, double minutes)
        {
            long period = (long)Math.Floor(minutes / Period);

            if (!Bad(seed, period))
            {
                return 0f;
            }

            double into = minutes - period * (double)Period;
            float w = 1f;

            if (!Bad(seed, period - 1) && into < Turn)
            {
                w = Smooth((float)(into / Turn));
            }

            if (!Bad(seed, period + 1) && into > Period - Turn)
            {
                w = Math.Min(w, Smooth((float)((Period - into) / Turn)));
            }

            return w;
        }

        /// <summary>The first period of the bad run a time is in (its slot holds for the whole run); the period itself when nice.</summary>
        public static long RunStart(uint seed, double minutes)
        {
            long period = (long)Math.Floor(minutes / Period);

            for (int i = 0; i < 48 && Bad(seed, period - 1); i++)
            {
                period--;
            }

            return period;
        }

        /// <summary>Is the bad run a time is in a thunderstorm (env_data thunderstorm_probability).</summary>
        public static bool Thunder(uint seed, long runStart)
        {
            return Unit(Hash(seed, (uint)runStart, 0x7D3u)) < ThunderProbability;
        }

        /// <summary>
        /// Road wetness at a game time: rises by WettingFactor a minute toward the rain's maximum while it rains, falls by
        /// DryingFactor a minute after, stepped over the last six game hours (cheap: 360 hashes).
        /// </summary>
        public static float Wetness(uint seed, double minutes, float maxWetness)
        {
            float w = 0f;
            double start = Math.Floor(minutes) - 360.0;

            for (double m = start; m <= minutes; m += 1.0)
            {
                float rain = Mix(seed, m);

                if (rain > 0.1f)
                {
                    float cap = maxWetness * rain;
                    w = w < cap ? Math.Min(cap, w + WettingFactor * rain) : Math.Max(cap, w - DryingFactor);
                }
                else
                {
                    w = Math.Max(0f, w - DryingFactor);
                }
            }

            return w;
        }

        private static float Smooth(float x)
        {
            x = Math.Max(0f, Math.Min(1f, x));
            return x * x * (3f - 2f * x);
        }

        /// <summary>The place seed: the build centre in whole game centimetres, as the session wire carries it (EtsClimate.PickSlot).</summary>
        public static uint Seed(float gameX, float gameZ)
        {
            int xcm = (int)Math.Round(gameX * 100.0);
            int zcm = (int)Math.Round(-(double)gameZ * 100.0);
            return Hash((uint)xcm, (uint)zcm, 0x5EEDu);
        }
    }
}
