using System;
using System.Collections.Generic;
using System.Globalization;
using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
using UnityEngine;
using UnityEngine.Rendering;

namespace SomniumSpace.Worlds.SomniumEuroTruck
{
    /// <summary>
    /// The running day and the weather (2026-10-14): the game's clock, its sun profiles through the whole day, nice and bad
    /// weather with rain, wet roads, stars, the moon and the lamps going on at dusk - the same for every player.
    ///
    /// SHARED. The time is the session's game clock (EtsSessionWire int 21): the instance master's EtsSessionLogic counts it
    /// at the map's time compression (19 game minutes a real minute, /map/europe.mbd; ClockScale is set here) and writes each
    /// new minute; every client draws between the writes at the same rate. The weather is a pure function of that time and of
    /// the session's place (EtsWeatherClock), so nothing else goes over the wire. The day's nice slot is the one the map
    /// viewer picked for the session's first day (centre + UTC day, as before) and a hash of the place and the game day after.
    ///
    /// EVERY QUARTER SECOND (only in the world phase - the HQ stage and loading screens keep the session's start light):
    /// EtsClimate.Evaluate (the nice and bad profiles either side of the sun, blended) and ApplyContinuous (sun or moon, ambient
    /// probe, fog), EtsSky.Continuous (panoramas read on a worker, uploaded when the sun reaches them), the rain (EtsRain), the
    /// wet roads (EtsWetSurfaces), the lamps (EtsNightLights; vehicle lamps read <see cref="LampsOn"/>), and every 2 s the post
    /// curve. Cost on the main thread: about 0.1-0.2 ms a quarter second, nothing allocated (logs aside); one panorama upload
    /// (16 MB) when the sun crosses a profile, every 2-6 real minutes.
    ///
    /// LOCAL CHOICES (the tablet's Options, this visit only): <see cref="FixedTime"/> keeps 10:00 for this player (comfort);
    /// <see cref="WeatherChoice"/> 0 the game's, 1 always clear, 2 always rain.
    /// </summary>
    public sealed class EtsDayNight : MonoBehaviour
    {
        /// <summary>The shared game clock, minutes since Monday 00:00 (fractional).</summary>
        public static double GameMinutes { get; private set; } = EtsClimate.DefaultGameMinutes;

        /// <summary>The sun under env_data's lamps_on_elevation (5 degrees): street, building and vehicle lamps on.</summary>
        public static bool LampsOn { get; private set; }

        /// <summary>
        /// AI cars' dipped beams: with the lamps (sun under 5 degrees) or in rain above a quarter of full (the game's own AI
        /// switches with g_auto_traffic_headlights 1 in config.cfg; when exactly is the engine's).
        /// </summary>
        public static bool TrafficHeadlights => LampsOn || Rain > 0.25f;

        /// <summary>rain_intensity now (0 dry), the weather (0 nice .. 1 bad), the road's wetness.</summary>
        public static float Rain { get; private set; }
        public static float WeatherMix { get; private set; }
        public static float Wetness { get; private set; }

        /// <summary>The climate as the running day has it (null until the driver ran).</summary>
        public static EtsClimate Day { get; private set; }

        /// <summary>Tablet (local): keep 10:00 for this player.</summary>
        public static bool FixedTime;

        /// <summary>Tablet (local): 0 the game's weather, 1 always clear, 2 always rain.</summary>
        public static int WeatherChoice;

        /// <summary>The render lab: a fixed clock (game minutes, -1 off), a fixed weather (0..1, -1 off) and bad slot (-1: the hash's).</summary>
        public static double LabMinutes = -1.0;
        public static float LabWeather = -1f;
        public static int LabBadSlot = -1;

        /// <summary>A lightning strike lit the clouds (direction from the head, rough distance in metres): for the thunder sound.</summary>
        public static event Action<Vector3, float> LightningStruck;

        public static string WeatherLabel => WeatherChoice == 1 ? "always clear" : WeatherChoice == 2 ? "always rain" : "the game's";
        public static string TimeLabel => FixedTime ? "fixed at 10:00" : "the game clock (shared)";

        private static EtsDayNight _instance;

        private EtsWeatherTable _nice, _bad;
        private string _tablesFor;
        private bool _tablesAsked;
        private EtsWorker _worker;
        private EtsClimate _day;
        private EtsClimate _dayFor;
        private double _clock = EtsClimate.DefaultGameMinutes;
        private int _seenMinute = int.MinValue;
        private float _nextStep, _nextPost, _nextLog;
        private bool _applied;
        private uint _seed;
        private int _baseSlot;
        private bool _loggedStart, _loggedNight, _loggedRain, _loggedLightning;
        private int _lastLoggedHour = -1;
        private float _flashUntil;
        private int _flashMinute = int.MinValue;
        private string _env = "";

        /// <summary>EtsMapViewer, when it applied a climate: makes the driver (once) under the viewer.</summary>
        public static void Ensure(EtsMapViewer viewer)
        {
            if (_instance != null || viewer == null)
            {
                return;
            }

            var host = new GameObject("Ets Day Night");
            host.transform.SetParent(viewer.transform, false);
            _instance = host.AddComponent<EtsDayNight>();
            EtsRain.Ensure(host.transform);
            EtsHeadlights.Ensure(host.transform);
        }

        private void OnDestroy()
        {
            if (_instance == this)
            {
                _instance = null;
                Day = null;
            }

            _worker?.Dispose();
            _worker = null;
        }

        private void Update()
        {
            EtsMapViewer viewer = EtsMapViewer.Current;

            if (viewer == null || viewer.Climate == null || EtsEngine.Files == null)
            {
                return;
            }

            try
            {
                Tick(viewer);
            }
            catch (Exception e)
            {
                enabled = false;
                Debug.LogWarning("[SomniumEuroTruck] Day/night: stopped after an error (the light stays as it is): " + e.GetType().Name + ": " + e.Message + "\n" + e.StackTrace);
            }
        }

        private void Tick(EtsMapViewer viewer)
        {
            EtsClimate start = viewer.Climate;
            AskTables(start);
            AdvanceClock();

            if (_worker != null)
            {
                _worker.Drain(Always);
            }

            if (_nice == null || _nice.Empty)
            {
                return;
            }

            // The full-view phases (HQ stage, loading screens) keep the session start's light (the void and avatars are tuned on it).
            if (EtsPostFx.PhaseWeight < 0.5f)
            {
                if (_applied)
                {
                    _applied = false;
                    start.Apply(EtsClimate.FindSun(), RenderSettings.fogMode);
                    EtsSky.Reapply(EtsEngine.Files, start, RenderSettings.sun, viewer.transform);
                    EtsRain.Intensity = 0f;
                }

                return;
            }

            Flash();

            float now = Time.unscaledTime;

            if (now < _nextStep)
            {
                return;
            }

            _nextStep = now + 0.25f;
            Step(viewer, start, now);
        }

        private static readonly Func<bool> Always = () => true;

        // ------------------------------------------------------------------ the tables (once per climate, on a worker)

        private void AskTables(EtsClimate start)
        {
            string key = start.Path;

            if (_tablesFor == key || _tablesAsked && _tablesFor == null)
            {
                return;
            }

            if (_tablesAsked && _tablesFor != key && _nice != null)
            {
                // Another climate (a new centre in an arid sector): read again.
                _tablesAsked = false;
            }

            if (_tablesAsked)
            {
                return;
            }

            _tablesAsked = true;
            _tablesFor = null;
            EtsFileSystem files = EtsEngine.Files;
            string nicePath = start.Path;
            string badPath = nicePath != null ? nicePath.Replace("/nice.sii", "/bad.sii") : null;
            string climateName = start.Climate;

            if (_worker == null)
            {
                _worker = new EtsWorker("Ets day/night tables");
            }

            _worker.Post(() =>
            {
                var result = new object[4];
                EtsSiiFile climates = files.ReadSii("/def/climate.sii");
                EtsSiiUnit profile = climates != null ? (climates.Find("climate." + climateName) ?? climates.Find("climate.default")) : null;
                List<string> weights = profile != null ? profile.GetArray("weights") : null;

                if (weights != null && weights.Count == 0)
                {
                    weights = null;
                }

                EtsWeatherTable nice = EtsWeatherTable.Parse(files.ReadSii(nicePath), nicePath, weights, 0);
                EtsWeatherTable bad = badPath != null && files.Exists(badPath) ? EtsWeatherTable.Parse(files.ReadSii(badPath), badPath, weights, nice.SlotCount) : null;
                result[0] = nice;
                result[1] = bad;

                var env = new System.Text.StringBuilder();
                env.Append(EtsWeatherClock.ReadTimeCompression(files.Read("/map/europe.mbd")));

                if (profile != null)
                {
                    EtsWeatherClock.BadFactor = EtsSiiValue.ParseFloat(profile.GetString("bad_weather_factor", "0.07"), 0.07f);
                    EtsWeatherClock.WettingFactor = EtsSiiValue.ParseFloat(profile.GetString("wetting_factor", "0.1"), 0.1f);
                    EtsWeatherClock.DryingFactor = EtsSiiValue.ParseFloat(profile.GetString("drying_factor", "0.01"), 0.01f);
                }

                EtsSiiFile envFile = files.ReadSii("/def/env_data.sii");
                EtsSiiUnit envData = envFile != null ? envFile.Find("env.data") : null;

                if (envData != null)
                {
                    EtsWeatherClock.LampsOnElevation = EtsSiiValue.ParseFloat(envData.GetString("lamps_on_elevation", "5"), 5f);
                    EtsWeatherClock.ThunderProbability = EtsSiiValue.ParseFloat(envData.GetString("thunderstorm_probability", "0.37"), 0.37f);
                }

                env.Append("; climate '").Append(climateName).Append("' bad_weather_factor ").Append(EtsWeatherClock.BadFactor.ToString("0.###", CultureInfo.InvariantCulture))
                   .Append(", wetting ").Append(EtsWeatherClock.WettingFactor.ToString("0.###", CultureInfo.InvariantCulture))
                   .Append(" / drying ").Append(EtsWeatherClock.DryingFactor.ToString("0.###", CultureInfo.InvariantCulture))
                   .Append(" a game minute; lamps on below ").Append(EtsWeatherClock.LampsOnElevation.ToString("0.#", CultureInfo.InvariantCulture))
                   .Append(" deg, thunderstorm probability ").Append(EtsWeatherClock.ThunderProbability.ToString("0.##", CultureInfo.InvariantCulture));
                result[2] = env.ToString();
                result[3] = key;
                return result;
            }, (result, error) =>
            {
                var parts = result as object[];

                if (error != null || parts == null)
                {
                    Debug.LogWarning("[SomniumEuroTruck] Day/night: the weather files could not be read (" + (error != null ? error.Message : "nothing") + "); the light stays at the session's start.");
                    return;
                }

                _nice = parts[0] as EtsWeatherTable;
                _bad = parts[1] as EtsWeatherTable;
                _env = parts[2] as string ?? "";
                _tablesFor = parts[3] as string;
                _loggedStart = false;
            });
        }

        // ------------------------------------------------------------------ the clock

        private void AdvanceClock()
        {
            float rate = EtsWeatherClock.TimeCompression / 60f;
            EtsSession session = EtsSession.Instance;

            if (session != null)
            {
                if (session.ClockScale != EtsWeatherClock.TimeCompression)
                {
                    session.ClockScale = EtsWeatherClock.TimeCompression;
                }

                int minute = session.GameClockMinutes;

                if (minute != _seenMinute)
                {
                    if (_seenMinute == int.MinValue || Math.Abs(_clock - minute) > 3.0)
                    {
                        _clock = minute;
                    }

                    _seenMinute = minute;
                }

                // Between the master's writes the clock runs on at the same rate; it never runs more than a minute ahead of the
                // last written minute, nor behind it.
                _clock += Time.unscaledDeltaTime * rate;
                _clock = Math.Max(minute, Math.Min(minute + 1.25, _clock));
            }
            else
            {
                _clock += Time.unscaledDeltaTime * rate;
            }

            if (LabMinutes >= 0.0)
            {
                _clock = LabMinutes;
            }

            GameMinutes = _clock;
        }

        // ------------------------------------------------------------------ one step

        private void Step(EtsMapViewer viewer, EtsClimate start, float now)
        {
            // Our own climate object, carrying the start's place (the viewer's stays the session start's, for the full-view phases).
            // (A new start climate - another centre, LoadAt - makes a new day object; compared by reference, nothing allocated.)

            if (_day == null || !ReferenceEquals(_dayFor, start))
            {
                _dayFor = start;
                _day = new EtsClimate
                {
                    Climate = start.Climate,
                    Path = start.Path,
                    Sector = start.Sector,
                    Latitude = start.Latitude,
                    Longitude = start.Longitude,
                    DayInYear = start.DayInYear,
                    SummerTime = start.SummerTime,
                    FitReach = start.FitReach
                };
                Vector3 centre = viewer.CenterGame;
                _seed = EtsWeatherClock.Seed(centre.x, centre.z);
                _baseSlot = start.Slot;
                Day = _day;
            }

            double clock = _clock;
            double sunClock = FixedTime ? EtsClimate.DefaultGameMinutes : clock;

            // The nice slot: the session's first day keeps the viewer's pick; every later game day has its own.
            long gameDay = (long)Math.Floor(clock / 1440.0);
            int niceSlot = gameDay == 0 ? _baseSlot : _nice.PickSlot(EtsWeatherClock.Unit(EtsWeatherClock.Hash(_seed, (uint)gameDay, 0x51u)));

            // The weather: the game's (a pure function of the shared time), or the lab's / this player's choice.
            float weather;
            long run = EtsWeatherClock.RunStart(_seed, clock);

            if (LabWeather >= 0f)
            {
                weather = Mathf.Clamp01(LabWeather);
            }
            else if (WeatherChoice == 1)
            {
                weather = 0f;
            }
            else if (WeatherChoice == 2)
            {
                weather = 1f;
            }
            else
            {
                weather = EtsWeatherClock.Mix(_seed, clock);
            }

            if (_bad == null || _bad.Empty)
            {
                weather = 0f;
            }

            int badSlot = _bad != null && !_bad.Empty ? _bad.PickSlot(EtsWeatherClock.Unit(EtsWeatherClock.Hash(_seed, (uint)run, 0xBADu))) : 0;

            if (LabBadSlot >= 0 && _bad != null)
            {
                badSlot = Mathf.Clamp(LabBadSlot, 0, _bad.SlotCount - 1);
            }

            _day.Evaluate(_nice, niceSlot, _bad, badSlot, weather, sunClock);

            float wetness;

            if (LabWeather >= 0f || WeatherChoice != 0)
            {
                wetness = _day.MaxWetness * weather;
            }
            else
            {
                float maxWet = _bad != null && !_bad.Empty ? MaxWetnessOf(badSlot, sunClock) : 0f;
                wetness = EtsWeatherClock.Wetness(_seed, clock, maxWet);
            }

            _day.Wetness = wetness;
            _day.Thunder = weather > 0.5f && _day.LightningIntensity > 0.05f && EtsWeatherClock.Thunder(_seed, run);

            _day.ApplyContinuous(EtsClimate.FindSun(), RenderSettings.fogMode);
            bool sky = EtsSky.Continuous(EtsEngine.Files, _day, viewer.transform);
            Prefetch(niceSlot, badSlot, sunClock, weather, clock);

            if (now >= _nextPost && EtsPostFx.Current != null)
            {
                _nextPost = now + 2f;
                EtsPostFx.Current.Apply(_day, false);
            }

            Rain = _day.RainIntensity;
            WeatherMix = weather;
            Wetness = wetness;
            LampsOn = _day.LampsOn;
            EtsRain.Intensity = Rain;
            EtsRain.Light = _day.RainLightLinear;
            EtsWetSurfaces.Update(wetness);
            EtsNightLights.Update(LampsOn, _day.Exposure);
            _applied = true;

            Log(sky, now);
        }

        /// <summary>The bad slot's rain_max_wetness at the sun's elevation (the wetness cap while it rains).</summary>
        private float MaxWetnessOf(int badSlot, double minutes)
        {
            bool rising = _day.Azimuth < 180f;
            _bad.Find(_day.Elevation, rising, out int lo, out int hi, out float t);

            if (lo < 0)
            {
                return 0f;
            }

            float a = _bad.Profiles[lo].Slots[Mathf.Clamp(badSlot, 0, _bad.SlotCount - 1)][EtsWeatherTable.MaxWetness];
            float b = _bad.Profiles[hi].Slots[Mathf.Clamp(badSlot, 0, _bad.SlotCount - 1)][EtsWeatherTable.MaxWetness];
            return a + (b - a) * t;
        }

        /// <summary>
        /// Starts reading what the sky will want next: the panorama past the upper profile (the sun reaches it within a few real
        /// minutes) and, when bad weather begins within half a game hour, the bad pair.
        /// </summary>
        private void Prefetch(int niceSlot, int badSlot, double sunClock, float weather, double clock)
        {
            bool rising = _day.Azimuth < 180f;
            float ahead = _day.Elevation + (rising ? 3f : -3f);
            _nice.Find(ahead, rising, out int lo, out int hi, out float t);

            if (hi >= 0)
            {
                EtsSky.Prefetch(_nice.Text(rising ? hi : lo, niceSlot, EtsWeatherTable.TextSky), _nice.Text(rising ? hi : lo, niceSlot, EtsWeatherTable.TextMask));
            }

            if (_bad != null && !_bad.Empty && weather <= 0f && WeatherChoice == 0 && LabWeather < 0f && EtsWeatherClock.Mix(_seed, clock + 30.0) > 0f)
            {
                int soonSlot = _bad.PickSlot(EtsWeatherClock.Unit(EtsWeatherClock.Hash(_seed, (uint)EtsWeatherClock.RunStart(_seed, clock + 30.0), 0xBADu)));
                _bad.Find(_day.Elevation, rising, out int blo, out int bhi, out float bt);

                if (blo >= 0)
                {
                    EtsSky.Prefetch(_bad.Text(blo, soonSlot, EtsWeatherTable.TextSky), _bad.Text(blo, soonSlot, EtsWeatherTable.TextMask));
                    EtsSky.Prefetch(_bad.Text(bhi, soonSlot, EtsWeatherTable.TextSky), _bad.Text(bhi, soonSlot, EtsWeatherTable.TextMask));
                }
            }
        }

        // ------------------------------------------------------------------ lightning

        /// <summary>
        /// A thunderstorm's strikes: in each game minute of a storm a strike with the chance 0.3 x lightning_intensity (how often
        /// the engine strikes is not in the files), at a moment and in a direction hashed from the shared time, so every
        /// client flashes together. The flash lights the bad slot's clouds (EtsSky) for a quarter second, flickering.
        /// </summary>
        private void Flash()
        {
            EtsClimate day = _day;
            float now = Time.unscaledTime;

            if (day != null && day.Thunder && LabWeather < 0f)
            {
                int minute = (int)Math.Floor(_clock);
                float into = (float)(_clock - minute);

                if (minute != _flashMinute && EtsWeatherClock.Unit(EtsWeatherClock.Hash(_seed, (uint)minute, 0x11u)) < 0.3 * day.LightningIntensity &&
                    into >= (float)EtsWeatherClock.Unit(EtsWeatherClock.Hash(_seed, (uint)minute, 0x12u)))
                {
                    _flashMinute = minute;
                    _flashUntil = now + 0.28f;
                    float angle = (float)EtsWeatherClock.Unit(EtsWeatherClock.Hash(_seed, (uint)minute, 0x13u)) * Mathf.PI * 2f;
                    float distance = 800f + 4000f * (float)EtsWeatherClock.Unit(EtsWeatherClock.Hash(_seed, (uint)minute, 0x14u));

                    if (!_loggedLightning)
                    {
                        _loggedLightning = true;
                        Debug.Log("[SomniumEuroTruck] Day/night: lightning - a thunderstorm (lightning_intensity " + day.LightningIntensity.ToString("0.00", CultureInfo.InvariantCulture) +
                                  ") lit the clouds at " + day.ClockText() + "; LightningStruck raised for the thunder sound.");
                    }

                    try
                    {
                        LightningStruck?.Invoke(new Vector3(Mathf.Sin(angle), 0.3f, Mathf.Cos(angle)).normalized, distance);
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning("[SomniumEuroTruck] Day/night: a LightningStruck handler failed: " + e.Message);
                    }
                }
            }

            if (now < _flashUntil && day != null)
            {
                // Two pulses in the quarter second, white-blue, x exposure.
                float phase = 1f - (_flashUntil - now) / 0.28f;
                float pulse = phase < 0.3f ? 1f : phase < 0.45f ? 0.15f : phase < 0.7f ? 0.8f : 0.3f * (1f - phase);
                float level = 0.004f * Mathf.Max(0.2f, day.LightningIntensity) * day.Exposure * pulse;
                EtsSky.FlashLinear = new Vector4(0.85f * level, 0.9f * level, level, 1f);
            }
            else if (EtsSky.FlashLinear.w != 0f)
            {
                EtsSky.FlashLinear = Vector4.zero;
            }
        }

        // ------------------------------------------------------------------ the log

        private void Log(bool sky, float now)
        {
            EtsClimate day = _day;
            int hour = (int)Math.Floor(_clock / 60.0);

            if (!_loggedStart)
            {
                _loggedStart = true;
                _lastLoggedHour = hour;
                Debug.Log("[SomniumEuroTruck] Day/night: running - " + _env + "; " + _nice.Profiles.Count + " nice profiles x " + _nice.SlotCount + " slots (weights " + _nice.WeightsText() + ")" +
                          (_bad != null ? ", " + _bad.Profiles.Count + " bad x " + _bad.SlotCount + " (weights " + _bad.WeightsText() + ")" : ", no bad.sii") +
                          "; clock " + (EtsSession.Instance != null ? "the session's (master writes, x" + EtsWeatherClock.TimeCompression.ToString("0.#", CultureInfo.InvariantCulture) + ")" : "local (no session)") +
                          (LabMinutes >= 0.0 ? " held by the render lab" : "") + (FixedTime ? ", this player keeps 10:00" : "") + "; weather seed " + _seed.ToString("x8") +
                          "; " + day.DayLine() + "; sky " + (sky ? "continuous" : "waiting for its panoramas") + "; " + EtsWetSurfaces.Describe() + "; " + EtsNightLights.Describe() + ".");
            }
            else if (hour != _lastLoggedHour)
            {
                _lastLoggedHour = hour;
                Debug.Log("[SomniumEuroTruck] Day/night: " + day.DayLine() + "; sky " + EtsSky.Note() + ".");
            }

            if (!_loggedNight && day.Elevation < -6f)
            {
                _loggedNight = true;
                Debug.Log("[SomniumEuroTruck] Day/night: night - " + day.DayLine() + "; stars x" + day.StarsScale.ToString("0.000", CultureInfo.InvariantCulture) +
                          ", moon " + day.MoonLinear.maxColorComponent.ToString("0.000", CultureInfo.InvariantCulture) + " at " + (Mathf.Asin(Mathf.Clamp(day.MoonDirection.y, -1f, 1f)) * Mathf.Rad2Deg).ToString("0.0", CultureInfo.InvariantCulture) +
                          " deg; " + EtsSky.Note() + ".");
            }

            if (!_loggedRain && Rain > 0.05f)
            {
                _loggedRain = true;
                Debug.Log("[SomniumEuroTruck] Day/night: rain - " + day.DayLine() + "; " + EtsWetSurfaces.Describe() + ".");
            }
        }
    }

    /// <summary>
    /// The map's own night lights (2026-10-14): the street-lamp bulbs and other "lum" surfaces the files light only at night
    /// (luminance_night above 0 and luminance_output 0 - e.g. /material/special/lights_01.tobj on lamp heads: 10 or 2048 at
    /// night), which this world drew glowing at noon too, now dark by day; and the light pools street lamps throw on the
    /// ground (eut2.lightmap.night, /effect/eut2/light_tex: texture x vertex colour added to the light under it; 426 automat
    /// materials), drawn additively by the decal shader's night mode while the lamps are on. The pools' level is the engine's
    /// diffuse_multiplier (not in the files): PoolLevel x exposure, set by eye in the render lab against the game's night
    /// loading pictures (base_19, iberia_12).
    /// </summary>
    public static class EtsNightLights
    {
        private static readonly List<Material> NightOnly = new List<Material>();
        private static readonly List<Material> Pools = new List<Material>();
        private static bool _on;
        private static bool _known;
        private static float _exposure = -1f;

        /// <summary>The light-pool level before exposure (see the class notes).</summary>
        public static float PoolLevel = 0.003f;

        private static readonly int EmissionColor = Shader.PropertyToID("_EmissionColor");
        private static readonly int LampColor = Shader.PropertyToID("_LampColor");

        /// <summary>EtsMaterialCache: a "lum" material; registered only when the file lights it at night alone.</summary>
        public static void RegisterLum(Material material, float luminanceNight, float luminanceOutput)
        {
            if (material == null || !(luminanceNight > 0f) || luminanceOutput != 0f || !material.HasProperty(EmissionColor))
            {
                return;
            }

            NightOnly.Add(material);
            material.SetColor(EmissionColor, _on ? Color.white : Color.black);
        }

        /// <summary>EtsMaterialCache: a light pool (decal shader, night mode 3).</summary>
        public static void RegisterPool(Material material)
        {
            if (material == null)
            {
                return;
            }

            Pools.Add(material);
            material.SetShaderPassEnabled("UniversalForward", _on);
            float level = PoolLevel * Mathf.Max(0f, _exposure);
            material.SetVector(LampColor, new Vector4(level, level * 0.93f, level * 0.8f, 1f));
        }

        /// <summary>EtsStreetLamps: the lamps' glow (drawn while the lamps are on, at its own fixed level).</summary>
        public static void RegisterGlow(Material material)
        {
            if (material == null)
            {
                return;
            }

            Glows.Add(material);
            material.SetShaderPassEnabled("UniversalForward", _on);
        }

        private static readonly List<Material> Glows = new List<Material>();

        public static void Update(bool on, float exposure)
        {
            bool changed = on != _on || !_known;
            _on = on;
            _known = true;

            if (changed)
            {
                for (int i = Glows.Count - 1; i >= 0; i--)
                {
                    if (Glows[i] == null)
                    {
                        Glows.RemoveAt(i);
                        continue;
                    }

                    Glows[i].SetShaderPassEnabled("UniversalForward", on);
                }

                for (int i = NightOnly.Count - 1; i >= 0; i--)
                {
                    if (NightOnly[i] == null)
                    {
                        NightOnly.RemoveAt(i);
                        continue;
                    }

                    NightOnly[i].SetColor(EmissionColor, on ? Color.white : Color.black);
                }

                for (int i = Pools.Count - 1; i >= 0; i--)
                {
                    if (Pools[i] == null)
                    {
                        Pools.RemoveAt(i);
                        continue;
                    }

                    Pools[i].SetShaderPassEnabled("UniversalForward", on);
                }
            }

            if (on)
            {
                EtsStreetLamps.Ground();
            }

            if (on && Mathf.Abs(exposure - _exposure) > 0.02f * Mathf.Max(_exposure, 1e-3f))
            {
                _exposure = exposure;
                float level = PoolLevel * exposure;
                var colour = new Vector4(level, level * 0.93f, level * 0.8f, 1f);

                for (int i = Pools.Count - 1; i >= 0; i--)
                {
                    if (Pools[i] != null)
                    {
                        Pools[i].SetVector(LampColor, colour);
                    }
                }
            }
        }

        public static string Describe()
        {
            return NightOnly.Count + " night-only lamp surfaces, " + Pools.Count + " light-pool materials, " + EtsStreetLamps.Describe() + ", lamps " + (_on ? "on" : "off");
        }
    }
}
