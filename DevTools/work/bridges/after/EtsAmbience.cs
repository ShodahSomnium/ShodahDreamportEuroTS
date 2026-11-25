using System.Collections.Generic;
using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
using UnityEngine;

namespace SomniumSpace.Worlds.SomniumEuroTruck
{
    /// <summary>
    /// SINCE 2026-10-14 ("no more synthesised sounds") the ambience is the GAME'S OWN: the map's sound items, the default
    /// beds, rain and thunder (UpdateGame, below). What follows describes the synthesised FALLBACK, built only when the
    /// game's banks do not load within 25 s.
    /// The world's ambience round the listener, synthesised like the truck's fallback sounds (EtsTruckAudio; OnAudioFilterRead never runs for world code in the Somnium client), so every sound is a
    /// float array built once per client - here on a worker thread (EtsSynth) - turned into an AudioClip and played by
    /// ordinary AudioSources.
    ///
    /// Six flat loops crossfade by where the listener is: a wind bed (always, lighter in town), birdsong by day and
    /// crickets by night where the ground is green, a distant traffic hum in towns, the roar of fast traffic near a
    /// motorway, and lapping water with gulls by rivers and lakes. Where comes from the map itself, measured cheaply:
    ///  - the ground round the listener: ten rays a second straight down at points on rings 12-110 m out, each hit
    ///    classified once per collider (EtsGroundSurface: water, road, gravel/dirt, grass/forest, buildings) and kept as
    ///    running shares over the last ~4 s. Rays that hit no map collider mean "no map here" and fade everything out;
    ///  - the game's own city areas (EtsMapIndex.Cities, the boxes EtsEconomy's city scale uses): inside one is town,
    ///    fading out over 350 m;
    ///  - the traffic the lanes carry (EtsCarAudio.FastTraffic: cars within 250 m doing 55-90+ km/h) for the motorway;
    ///  - the sun (EtsMapViewer.Current.Climate.ToSun) for day and night.
    /// 3D one-shots add life at random spots round the listener: bird calls, an owl at night, gulls by water, and in
    /// town a car horn, a bus's air brakes, a clank of metal and, rarely, a far siren.
    /// Inside the cab everything is damped by the cab's interior factor (EtsTruckAudio.ListenerOutsideGain). Nothing
    /// here allocates per frame; the per-collider classification allocates a few strings the first time a collider is
    /// seen.
    /// Added by the per-player EtsTruckAudio (Awake), so it lives on the same host ('Ets Net').
    /// </summary>
    public sealed class EtsAmbience : MonoBehaviour
    {
        [Range(0f, 1f)]
        public float Volume = 0.5f;

        public static EtsAmbience Instance { get; private set; }

        public static EtsAmbience Ensure(GameObject host)
        {
            if (Instance != null)
            {
                return Instance;
            }

            if (host == null)
            {
                return null;
            }

            EtsAmbience a = host.GetComponent<EtsAmbience>();
            return a != null ? a : host.AddComponent<EtsAmbience>();
        }

        // ---------------------------------------------------------------- clips (one set per client)

        private const bool BirdCalls = false; // no bird sounds (user request, 2026-10-15)
        private const int Wind = 0, Birds = 1, Crickets = 2, Town = 3, Motorway = 4, Water = 5, LoopCount = 6;
        private static readonly string[] LoopNames = { "wind", "birds", "crickets", "town", "motorway", "water" };
        private static readonly AudioClip[] s_loops = new AudioClip[LoopCount];
        private static readonly AudioClip[] s_birdCalls = new AudioClip[3];
        private static AudioClip s_gull, s_owl, s_horn, s_airBrake, s_clank, s_siren;
        private static bool s_requested;
        private static int s_expected, s_arrived;

        private static bool ClipsReady => s_requested && s_arrived >= s_expected;

        // ---------------------------------------------------------------- per client

        private GameObject _holder;
        private AudioSource[] _loops;
        private AudioSource[] _shots;
        private readonly float[] _level = new float[LoopCount];
        private readonly float[] _target = new float[LoopCount];

        // The ground round the listener: running shares of what the probe rays hit.
        private float _cover, _road, _loose, _nature, _water, _built;
        private float _city;
        private float _day = 1f, _dayTarget = 1f;

        // Smoothed mixes.
        private float _townS, _natureS, _waterS, _motorwayS;
        private float _master;

        private float _nextProbe, _nextSlow, _nextBird = 3f, _nextTown = 5f, _nextGull = 8f, _nextOwl = 20f, _lastSiren = -1000f;
        private float _probeAngle;
        private int _probeStep;
        private string _label = "";
        private float _nextLabelLog;
        private bool _loggedPlay;

        private static readonly float[] ProbeRadii = { 12f, 30f, 60f, 110f };

        /// <summary>Everything but vehicles, avatars, UI and the ignore-raycast layer.</summary>
        private const int ProbeMask = ~((1 << EtsLayers.Vehicle) | (1 << EtsLayers.Player) | (1 << EtsLayers.Ui) | (1 << EtsLayers.IgnoreRaycast) | (1 << EtsLayers.PhysicalObject));

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                enabled = false;
                return;
            }

            Instance = this;
            // The synthesised clips are built only if the game's ambience does not come (Update: the fallback).

            _holder = new GameObject("ambience");
            _holder.transform.SetParent(transform, false);
            _loops = new AudioSource[LoopCount];

            for (int i = 0; i < LoopCount; i++)
            {
                AudioSource s = _holder.AddComponent<AudioSource>();
                s.playOnAwake = false;
                s.loop = true;
                s.volume = 0f;
                s.spatialBlend = 0f;
                s.dopplerLevel = 0f;
                s.priority = 150; // below the truck (64) and, by default, voices (128)
                _loops[i] = s;
            }

            _shots = new AudioSource[3];

            for (int i = 0; i < _shots.Length; i++)
            {
                var shot = new GameObject("ambience call " + i);
                shot.transform.SetParent(transform, false);
                AudioSource s = shot.AddComponent<AudioSource>();
                s.playOnAwake = false;
                s.loop = false;
                s.spatialBlend = 1f;
                s.rolloffMode = AudioRolloffMode.Logarithmic;
                s.minDistance = 12f;
                s.maxDistance = 400f;
                s.dopplerLevel = 0f;
                s.priority = 170;
                _shots[i] = s;
            }
        }

        private void OnDisable()
        {
            foreach (Emitter e in _emitters)
            {
                e.Player?.Stop();
                if (e.Sfx != null) foreach (EtsGameEventPlayer p in e.Sfx) p.Stop();
            }
            _country?.Player?.Stop();
            _cityBed?.Player?.Stop();
            _rainPlayer?.Stop();
            _gameShots?.StopAll();

            if (_loops == null)
            {
                return;
            }

            for (int i = 0; i < LoopCount; i++)
            {
                _level[i] = 0f;
                if (_loops[i] != null && _loops[i].isPlaying) _loops[i].Stop();
            }
        }

        private void OnDestroy()
        {
            OnGameDestroyed();

            if (Instance == this)
            {
                Instance = null;
            }
        }

        // ---------------------------------------------------------------- per frame (no allocation)

        private void Update()
        {
            EtsSynth.Pump();
            EtsGameSound.Pump();
            float dt = Mathf.Min(Time.deltaTime, 0.1f);
            float now = Time.time;
            Transform ear = EtsTruckRig.LocalView();

            // The game's own ambience (map sound items, default beds, weather) when it runs; the synthesised beds below are
            // only the fallback for an install whose banks do not load.
            bool game = UpdateGame(ear, dt, now);
            if (!game && !s_requested && now > 25f && (!EtsGameSound.Enabled || EtsEngine.Files == null || _gameFailed || (_gameSince >= 0f && now - _gameSince > 25f)))
            {
                RequestClips();
                Debug.Log("[SomniumEuroTruck] Ambience: synthesised fallback (" + (!EtsGameSound.Enabled ? "game sounds off" : EtsEngine.Files == null ? "archives not mounted" : _gameFailed ? "the game's default bed failed" : "the game's beds not ready after 25 s") + ").");
            }

            if (_loops == null || !ClipsReady)
            {
                return;
            }

            AssignLoopClips();

            if (game)
            {
                // The game's ambience plays: the synthesised loops fade out and the synthesised calls stay quiet.
                _master = 0f;
                float f = 1f - Mathf.Exp(-dt / 1.2f);
                for (int i = 0; i < LoopCount; i++)
                {
                    _level[i] += (0f - _level[i]) * f;
                    if (_level[i] < 0.0008f) { if (_loops[i].isPlaying) _loops[i].Stop(); }
                    else if (_loops[i].isPlaying) _loops[i].volume = _level[i];
                }
                return;
            }

            if (ear == null)
            {
                _cover += (0f - _cover) * (1f - Mathf.Exp(-dt / 1f));
                Mix(dt);
                return;
            }

            Vector3 at = ear.position;

            if (now >= _nextProbe)
            {
                _nextProbe = now + 0.2f;
                Probe(at);
                Probe(at);
            }

            if (now >= _nextSlow)
            {
                _nextSlow = now + 1f;
                Slow(at, now);
            }

            Mix(dt);
            Calls(now, at);
        }

        private void AssignLoopClips()
        {
            for (int i = 0; i < LoopCount; i++)
            {
                if (_loops[i].clip == null && s_loops[i] != null)
                {
                    _loops[i].clip = s_loops[i];
                }
            }
        }

        // ---------------------------------------------------------------- the game's own ambience (EtsGameSound, EtsMapSounds)

        /// <summary>
        /// The game's ambience, as its files place it (2026-10-14, "no more synthesised sounds"):
        ///  - the map's own sound items (EtsMapSounds: the sectors' .snd files, 7,215 in the demo) round the listener.
        ///    An ambient AREA (field_grown, foresttmed1, city_resid, coast_rivb3 ...) plays its sound_item_ambient event as
        ///    a flat bed, full inside its rectangle and fading to nothing max_dist (50-100 m) past its edge; up to three
        ///    areas at once, crossfading. Its sound_sfx (the forests' birds_2_eu_temp: birds by day, owls at night) runs
        ///    sfx_count times - here round the listener, each with its own spawn timers (INTERPRETATION: the def does not
        ///    say where the game puts its sfx instances). A POINT sound (church_mdl1, dogs_2, river_mid_1, crickets_3,
        ///    brds_seagl1 ...) is 3D at its node, silent at its max_dist; the nearest eight play;
        ///  - where no area covers the listener: ambient.def_country, or ambient.def_city inside a city area (the defaults
        ///    of sound_item_ambient.sii), crossfading by the share the areas leave;
        ///  - rain (weather_rain.soundref, by the rain intensity) and thunder (weather_thunder.soundref on each
        ///    EtsDayNight.LightningStruck, its "Distance" the strike's, heard after the sound's travel time).
        /// Every event gets daytime (the shared game clock's hour) and its Distance* parameters (the listener's distance to
        /// the item); FMOD's sheets do the rest (EtsGameEventPlayer: loops, playlists with silence, scatterers, one-shots
        /// entering their region - the church bells by the hour). Long beds are streamed (EtsGameStream). All of it is
        /// damped in the cab like the truck's outside sounds (EtsTruckAudio.ListenerOutsideGain).
        /// </summary>
        public float GameLevel = 1f;
        public const int MaxAreas = 3, MaxPoints = 8;
        private const string CountryUnit = "ambient.def_country", CityUnit = "ambient.def_city";
        private const string RainRef = "/sound/world/weather_rain.soundref", ThunderRef = "/sound/world/weather_thunder.soundref";

        private sealed class Emitter
        {
            public EtsMapSoundItem Item;
            public EtsMapSoundDef Def;
            public EtsGameEvent Ev, SfxEv;
            public EtsGameEventPlayer Player;
            public EtsGameEventPlayer[] Sfx;
            public GameObject Holder;
            public float Target, Level;
            public bool Keep;
            public float Distance;
            public float NextAnchor;
        }

        private static Dictionary<string, EtsMapSoundDef> s_defs;
        private static bool s_defsRequested;
        private static EtsWorker s_mapWorker;
        private static readonly System.Func<bool> s_drainAll = () => true;
        private readonly Dictionary<long, List<EtsMapSoundItem>> _sectors = new Dictionary<long, List<EtsMapSoundItem>>();
        private readonly HashSet<long> _sectorsAsked = new HashSet<long>();
        private readonly List<Emitter> _emitters = new List<Emitter>();
        private readonly List<EtsMapSoundItem> _candidates = new List<EtsMapSoundItem>();
        private readonly List<float> _candidateD = new List<float>();
        private Emitter _country, _cityBed;
        private EtsGameEvent _rain, _thunder;
        private EtsGameEventPlayer _rainPlayer;
        private AudioSource _thunderSource;
        private EtsGameShotPool _gameShots;
        private GameObject _gameHolder;
        private float _nextSelect, _gameSince = -1f, _thunderAt = -1f, _thunderDistance, _cityS, _nextGameLog;
        private bool _gameFailed, _gameLogged, _thunderBound;
        private System.Action<Vector3, float> _onLightning;
        private Vector3 _ear;
        private float _gx, _gz;

        private static long SectorKey(int x, int z) => ((long)x << 32) ^ (uint)z;

        private bool UpdateGame(Transform ear, float dt, float now)
        {
            if (!EtsGameSound.Enabled || EtsEngine.Files == null || _gameFailed) return false;
            if (_gameSince < 0f) _gameSince = now;
            EtsFileSystem files = EtsEngine.Files;
            if (s_mapWorker == null) s_mapWorker = new EtsWorker("Ets map sounds");
            s_mapWorker.Drain(s_drainAll);

            if (!s_defsRequested)
            {
                s_defsRequested = true;
                s_mapWorker.Post(() => EtsMapSounds.ReadDefs(files), (result, error) =>
                {
                    s_defs = result as Dictionary<string, EtsMapSoundDef> ?? new Dictionary<string, EtsMapSoundDef>();
                    Debug.Log("[SomniumEuroTruck] Ambience: " + s_defs.Count + " sound_item_data units read (/def/world/sound_item_data*.sii, sound_item_ambient.sii)" + (error != null ? " - " + error.Message : "") + ".");
                });
            }

            if (s_defs == null) return false;

            if (_gameHolder == null)
            {
                _gameHolder = new GameObject("game ambience");
                _gameHolder.transform.SetParent(transform, false);
                _gameShots = new EtsGameShotPool(_gameHolder.transform, 12, 170);
            }

            if (!_thunderBound)
            {
                _thunderBound = true;
                _onLightning = OnLightning;
                EtsDayNight.LightningStruck += _onLightning;
            }

            // The default beds: needed before anything counts as the game's ambience.
            if (_country == null) _country = DefaultBed(CountryUnit);
            if (_cityBed == null) _cityBed = DefaultBed(CityUnit);
            if (_country == null || _country.Ev == null) return false;
            if (_country.Ev.Failed) { _gameFailed = true; return false; }
            if (_country.Player == null) TryPlayer(_country, false);
            if (_country.Player == null) return false;
            if (_cityBed != null && _cityBed.Player == null && _cityBed.Ev != null && _cityBed.Ev.Ready) TryPlayer(_cityBed, false);

            EtsMapViewer viewer = EtsMapViewer.Current;
            float master = Mathf.Clamp01(Volume * 2f) * GameLevel * EtsTruckAudio.ListenerOutsideGain * (ear != null && viewer != null ? 1f : 0f);
            _gameShots.Gain = master;
            float hour = (float)(EtsDayNight.GameMinutes % 1440.0 / 60.0);

            if (ear != null && viewer != null)
            {
                _ear = ear.position;
                Vector3 centre = viewer.CenterGame;
                _gx = _ear.x + centre.x;
                _gz = centre.z - _ear.z;
                if (now >= _nextSelect)
                {
                    _nextSelect = now + 0.5f;
                    Select(files, centre);
                    _cityS = CityFactor(_ear);
                }
            }

            // Levels: each area by its weight, the defaults by what the areas leave.
            float k = 1f - Mathf.Exp(-dt / 1.5f);
            float areaMax = 0f;
            for (int i = 0; i < _emitters.Count; i++)
            {
                Emitter e = _emitters[i];
                e.Level += (e.Target - e.Level) * k;
                if (e.Item.Area && e.Level > areaMax) areaMax = e.Level;
            }
            float rest = Mathf.Clamp01(1f - areaMax);
            float cityShare = _cityBed != null && _cityBed.Player != null ? _cityS : 0f;
            DriveBed(_country, rest * (1f - cityShare), master, hour, k);
            if (_cityBed != null && _cityBed.Player != null) DriveBed(_cityBed, rest * cityShare, master, hour, k);

            for (int i = _emitters.Count - 1; i >= 0; i--)
            {
                Emitter e = _emitters[i];
                if (e.Player == null) TryPlayer(e, !e.Item.Area);
                if (e.Sfx == null && e.SfxEv != null && e.SfxEv.Ready) MakeSfx(e);
                if (!e.Keep && e.Level < 0.004f)
                {
                    DropEmitter(e);
                    _emitters.RemoveAt(i);
                    continue;
                }
                if (e.Player != null)
                {
                    SetCommon(e.Ev, hour, e.Distance);
                    e.Player.Drive(master * e.Level);
                }
                if (e.Sfx != null)
                {
                    SetCommon(e.SfxEv, hour, 0f);
                    if (now >= e.NextAnchor) MoveAnchors(e, now);
                    for (int s = 0; s < e.Sfx.Length; s++) e.Sfx[s].Drive(master * e.Level);
                }
            }

            Weather(master, now);

            if (!_gameLogged)
            {
                _gameLogged = true;
                Debug.Log("[SomniumEuroTruck] Ambience: the game's own ambience plays (default beds " + _country.Ev.Source + (_cityBed != null && _cityBed.Ev != null ? " / " + _cityBed.Ev.Source : "")
                    + "; the map's sound items from the sectors' .snd files; rain and thunder from weather.bank) - the synthesised beds and calls are off.");
            }

            if (now >= _nextGameLog && ear != null)
            {
                _nextGameLog = now + 60f;
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < _emitters.Count; i++)
                {
                    Emitter e = _emitters[i];
                    if (e.Level < 0.05f) continue;
                    if (sb.Length > 0) sb.Append(", ");
                    sb.Append(e.Item.Area ? "area " : "point ").Append(e.Item.Token).Append(' ').Append(e.Level.ToString("0.00")).Append(e.Item.Area ? "" : " at " + e.Distance.ToString("0") + " m");
                }
                Debug.Log("[SomniumEuroTruck] Ambience: " + (sb.Length > 0 ? sb.ToString() : "no map sound item here") + "; default " + (rest * (1f - cityShare)).ToString("0.00") + " country / "
                    + (rest * cityShare).ToString("0.00") + " city; " + hour.ToString("0.0") + " h; rain " + EtsDayNight.Rain.ToString("0.00") + "; streams " + EtsGameStream.Live
                    + " (" + (EtsGameStream.PacketBytes / 1048576.0).ToString("0.0") + " MB packets); game PCM " + (EtsGameSound.PcmBytes / 1048576.0).ToString("0.0") + " MB; shots " + _gameShots.Played + ".");
            }

            return true;
        }

        private Emitter DefaultBed(string unit)
        {
            if (!s_defs.TryGetValue(unit, out EtsMapSoundDef def)) return null;
            EtsGameEvent ev = EtsGameSound.Get(def.Path, 20f, false, null, true);
            return ev == null ? null : new Emitter { Def = def, Ev = ev, Keep = true };
        }

        private void DriveBed(Emitter bed, float target, float master, float hour, float k)
        {
            bed.Level += (target - bed.Level) * k;
            SetCommon(bed.Ev, hour, 0f);
            bed.Player.Drive(master * bed.Level);
        }

        private static void SetCommon(EtsGameEvent ev, float hour, float distance)
        {
            if (ev == null || !ev.Ready) return;
            List<int> ps = ev.Event.Params;
            for (int i = 0; i < ps.Count; i++)
            {
                string name = ev.Studio.Params[ps[i]].Name;
                if (name == "daytime") ev.Values[ps[i]] = hour;
                else if (name.StartsWith("Distance", System.StringComparison.Ordinal)) ev.Values[ps[i]] = distance;
            }
        }

        /// <summary>Twice a second: the sectors round the listener, the areas covering it, the point sounds in reach.</summary>
        private void Select(EtsFileSystem files, Vector3 centre)
        {
            EtsMapSector.SectorOf(_gx, _gz, out int sx, out int sz);
            _candidates.Clear();
            _candidateD.Clear();
            for (int x = sx - 1; x <= sx + 1; x++)
            {
                for (int z = sz - 1; z <= sz + 1; z++)
                {
                    // Neighbours only when the listener is within 1 km of their side.
                    float ox = x < sx ? _gx - sx * EtsMapSector.SectorSize : x > sx ? (sx + 1) * EtsMapSector.SectorSize - _gx : 0f;
                    float oz = z < sz ? _gz - sz * EtsMapSector.SectorSize : z > sz ? (sz + 1) * EtsMapSector.SectorSize - _gz : 0f;
                    if (ox > 1000f || oz > 1000f) continue;
                    long key = SectorKey(x, z);
                    if (!_sectors.TryGetValue(key, out List<EtsMapSoundItem> items))
                    {
                        if (_sectorsAsked.Add(key))
                        {
                            int cx = x, cz = z;
                            Dictionary<string, EtsMapSoundDef> defs = s_defs;
                            s_mapWorker.Post(() => EtsMapSounds.ReadSector(files, cx, cz, defs), (result, error) =>
                            {
                                var list = result as List<EtsMapSoundItem> ?? new List<EtsMapSoundItem>();
                                _sectors[SectorKey(cx, cz)] = list;
                                Debug.Log("[SomniumEuroTruck] Ambience: sector " + cx + "," + cz + " has " + list.Count + " map sound items (.snd)" + (error != null ? " - " + error.Message : "") + ".");
                            });
                        }
                        continue;
                    }
                    for (int i = 0; i < items.Count; i++)
                    {
                        EtsMapSoundItem it = items[i];
                        float d = it.DistanceTo(_gx, _gz);
                        if (d < it.Def.MaxDist)
                        {
                            _candidates.Add(it);
                            _candidateD.Add(d);
                        }
                    }
                }
            }

            for (int i = 0; i < _emitters.Count; i++) { _emitters[i].Keep = false; _emitters[i].Target = 0f; }
            int areas = 0, points = 0;
            // Best first (nearest against its reach); the list is short.
            for (int pass = 0; pass < _candidates.Count; pass++)
            {
                int best = -1;
                float bestScore = float.MaxValue;
                for (int i = 0; i < _candidates.Count; i++)
                {
                    EtsMapSoundItem c = _candidates[i];
                    if (c == null) continue;
                    float score = _candidateD[i] / Mathf.Max(1f, c.Def.MaxDist);
                    if (score < bestScore) { bestScore = score; best = i; }
                }
                if (best < 0) break;
                EtsMapSoundItem it = _candidates[best];
                float dist = _candidateD[best];
                _candidates[best] = null;
                if (it.Area ? areas >= MaxAreas : points >= MaxPoints) continue;
                if (it.Area) areas++; else points++;
                Emitter e = FindEmitter(it) ?? AddEmitter(it, centre);
                if (e == null) continue;
                e.Keep = true;
                e.Distance = dist;
                e.Target = it.Area ? Mathf.Clamp01(1f - dist / Mathf.Max(1f, it.Def.MaxDist)) : 1f; // a point's fade is its 3D rolloff
            }
        }

        private Emitter FindEmitter(EtsMapSoundItem it)
        {
            for (int i = 0; i < _emitters.Count; i++) if (ReferenceEquals(_emitters[i].Item, it)) return _emitters[i];
            return null;
        }

        /// <summary>Bird events (the forests' birds_2_eu_temp and the like) are left out: the user asked for no bird sounds.</summary>
        private static bool IsBird(string path) => path != null && path.IndexOf("bird", System.StringComparison.OrdinalIgnoreCase) >= 0;

        private Emitter AddEmitter(EtsMapSoundItem it, Vector3 centre)
        {
            if (IsBird(it.Def.Path)) return null;
            EtsGameEvent ev = EtsGameSound.Get(it.Def.Path, 20f, false, null, true);
            if (ev == null) return null;
            var e = new Emitter { Item = it, Def = it.Def, Ev = ev };
            if (it.Area && it.Def.SfxCount > 0 && !string.IsNullOrEmpty(it.Def.Sfx) && !IsBird(it.Def.Sfx)) e.SfxEv = EtsGameSound.Get(it.Def.Sfx, 0f, false, null, true);
            if (!it.Area)
            {
                e.Holder = new GameObject("map sound " + it.Token);
                e.Holder.transform.SetParent(_gameHolder.transform, false);
                e.Holder.transform.position = new Vector3(it.X - centre.x, it.Y - centre.y + 1.5f, centre.z - it.Z);
            }
            _emitters.Add(e);
            return e;
        }

        private void TryPlayer(Emitter e, bool threeD)
        {
            if (e.Player != null || e.Ev == null || !e.Ev.Ready) return;
            GameObject host = e.Holder != null ? e.Holder : _gameHolder;
            float reach = e.Def != null ? e.Def.MaxDist : 100f;
            e.Player = new EtsGameEventPlayer(e.Ev, () =>
            {
                AudioSource s = host.AddComponent<AudioSource>();
                s.playOnAwake = false;
                s.loop = true;
                s.volume = 0f;
                s.dopplerLevel = 0f;
                s.priority = 150;
                s.spatialBlend = threeD ? 1f : 0f;
                if (threeD) EtsGameShotPool.ApplyRolloff(s, reach);
                return s;
            });
            if (threeD)
            {
                e.Player.Shots = _gameShots;
                e.Player.Anchor = e.Holder.transform.position;
                e.Player.ShotMaxDistance = reach;
            }
        }

        /// <summary>The area's sfx event, sfx_count times, each its own scatterer timers round its own spot.</summary>
        private void MakeSfx(Emitter e)
        {
            int n = Mathf.Clamp(e.Def.SfxCount, 1, 8);
            e.Sfx = new EtsGameEventPlayer[n];
            for (int i = 0; i < n; i++)
            {
                e.Sfx[i] = new EtsGameEventPlayer(e.SfxEv, null) { Shots = _gameShots, ShotMaxDistance = 120f };
            }
            e.NextAnchor = 0f;
        }

        /// <summary>The sfx instances round the listener (20-80 m out, a fresh spot a minute).</summary>
        private void MoveAnchors(Emitter e, float now)
        {
            e.NextAnchor = now + 60f;
            uint seed = (uint)(e.Item.Uid ^ (e.Item.Uid >> 32)) ^ (uint)Mathf.FloorToInt(now / 60f) * 2654435761u;
            for (int i = 0; i < e.Sfx.Length; i++)
            {
                seed = seed * 1664525u + 1013904223u;
                float angle = (seed >> 8) / 16777216f * Mathf.PI * 2f;
                seed = seed * 1664525u + 1013904223u;
                float d = 20f + 60f * ((seed >> 8) / 16777216f);
                e.Sfx[i].Anchor = new Vector3(_ear.x + Mathf.Cos(angle) * d, _ear.y, _ear.z + Mathf.Sin(angle) * d);
            }
        }

        private void DropEmitter(Emitter e)
        {
            e.Player?.Stop();
            e.Player?.Destroy();
            e.Player = null;
            if (e.Sfx != null) foreach (EtsGameEventPlayer p in e.Sfx) { p.Stop(); p.Destroy(); }
            e.Sfx = null;
            if (e.Holder != null) Destroy(e.Holder);
            e.Holder = null;
        }

        private void Weather(float master, float now)
        {
            if (_rain == null) _rain = EtsGameSound.GetRef(RainRef, 0f, false, null, true);
            // The thunder (12 takes, 118 s - 22 MB of PCM) only once it rains: storms are rain (EtsDayNight's thunder runs in rain).
            if (_thunder == null && EtsDayNight.Rain > 0.01f) _thunder = EtsGameSound.GetRef(ThunderRef);
            if (_rainPlayer == null && _rain != null && _rain.Ready)
            {
                _rainPlayer = new EtsGameEventPlayer(_rain, () =>
                {
                    AudioSource s = _gameHolder.AddComponent<AudioSource>();
                    s.playOnAwake = false;
                    s.loop = true;
                    s.volume = 0f;
                    s.spatialBlend = 0f;
                    s.priority = 140;
                    return s;
                });
                Debug.Log("[SomniumEuroTruck] Ambience: rain from the game (" + _rain.Source + "), thunder loads with the first rain (/sound/world/weather_thunder.soundref).");
            }

            float rain = EtsDayNight.Rain;
            if (_rainPlayer != null)
            {
                _rainPlayer.TimelineActive = rain > 0.01f;
                _rainPlayer.Drive(master * Mathf.Clamp01(rain));
            }

            if (_thunderAt > 0f && now >= _thunderAt)
            {
                _thunderAt = -1f;
                if (_thunder != null && _thunder.Ready)
                {
                    if (_thunderSource == null)
                    {
                        _thunderSource = _gameHolder.AddComponent<AudioSource>();
                        _thunderSource.playOnAwake = false;
                        _thunderSource.spatialBlend = 0f;
                        _thunderSource.priority = 120;
                    }
                    _thunder.Set("Distance", _thunderDistance);
                    _thunder.Set("Distance Thunder", _thunderDistance);
                    _thunder.PlayOnce(_thunderSource, master);
                    Debug.Log("[SomniumEuroTruck] Ambience: thunder from the game, " + _thunderDistance.ToString("0") + " m away.");
                }
            }
        }

        /// <summary>EtsDayNight's lightning: the thunder, after the sound has come that far (343 m/s, at most 8 s).</summary>
        private void OnLightning(Vector3 direction, float distance)
        {
            _thunderDistance = distance;
            _thunderAt = Time.time + Mathf.Min(distance / 343f, 8f);
        }

        private void OnGameDestroyed()
        {
            if (_onLightning != null) EtsDayNight.LightningStruck -= _onLightning;
            _onLightning = null;
            _thunderBound = false;
            foreach (Emitter e in _emitters) DropEmitter(e);
            _emitters.Clear();
            if (_country != null) DropEmitter(_country);
            if (_cityBed != null) DropEmitter(_cityBed);
            _rainPlayer?.Destroy();
            _rainPlayer = null;
        }

        /// <summary>One ray straight down at the next point of the rings round the listener.</summary>
        private void Probe(Vector3 at)
        {
            _probeStep++;
            _probeAngle += 2.39996f; // the golden angle: the points never line up
            float radius = ProbeRadii[_probeStep & 3];
            var origin = new Vector3(at.x + Mathf.Cos(_probeAngle) * radius, at.y + 150f, at.z + Mathf.Sin(_probeAngle) * radius);
            byte kind = EtsGroundSurface.Unknown;

            if (Physics.Raycast(origin, Vector3.down, out RaycastHit hit, 450f, ProbeMask, QueryTriggerInteraction.Ignore))
            {
                kind = EtsGroundSurface.Of(hit.collider);
            }

            const float a = 1f / 40f; // about the last four seconds of rays
            _cover += ((kind != EtsGroundSurface.Unknown ? 1f : 0f) - _cover) * a;
            _road += ((kind == EtsGroundSurface.Hard ? 1f : 0f) - _road) * a;
            _loose += ((kind == EtsGroundSurface.Loose ? 1f : 0f) - _loose) * a;
            _nature += ((kind == EtsGroundSurface.Nature ? 1f : 0f) - _nature) * a;
            _water += ((kind == EtsGroundSurface.Water ? 1f : 0f) - _water) * a;
            _built += ((kind == EtsGroundSurface.Built ? 1f : 0f) - _built) * a;
        }

        /// <summary>Once a second: the city areas, the sun, and the log line when the kind of place changes.</summary>
        private void Slow(Vector3 at, float now)
        {
            _city = CityFactor(at);
            EtsMapViewer viewer = EtsMapViewer.Current;
            EtsClimate climate = viewer != null ? viewer.Climate : null;
            _dayTarget = climate != null ? Mathf.SmoothStep(0f, 1f, (climate.ToSun.y + 0.08f) / 0.18f) : 1f;

            string label = _cover < 0.15f ? "off the map" : _townS > 0.5f ? "town" : _motorwayS > 0.45f ? "motorway" : _waterS > 0.4f ? "waterside" : "countryside";

            if (!ReferenceEquals(label, _label) && now >= _nextLabelLog)
            {
                _label = label;
                _nextLabelLog = now + 30f;
                Debug.Log("[SomniumEuroTruck] Ambience: " + label + " (map " + _cover.ToString("0.00") + ", road " + _road.ToString("0.00") + ", gravel " + _loose.ToString("0.00")
                    + ", green " + _nature.ToString("0.00") + ", water " + _water.ToString("0.00") + ", buildings " + _built.ToString("0.00") + ", city area " + _city.ToString("0.00")
                    + ", fast traffic " + EtsCarAudio.FastTraffic.ToString("0.00") + " of " + EtsCarAudio.NearCars + " cars, " + (_dayTarget > 0.5f ? "day" : "night")
                    + (EtsTruckAudio.ListenerOutsideGain < 1f ? ", in the cab" : "") + ") at " + at.ToString("0") + ".");
            }
        }

        /// <summary>1 inside one of the game's city areas, falling to 0 at 350 m outside the nearest.</summary>
        private static float CityFactor(Vector3 at)
        {
            EtsMapViewer viewer = EtsMapViewer.Current;
            EtsMapIndex index = EtsMapIndex.Instance;

            if (viewer == null || index == null || !index.Ready)
            {
                return 0f;
            }

            IReadOnlyList<EtsMapCity> cities = index.Cities;
            Vector3 centre = viewer.CenterGame;
            float gx = at.x + centre.x, gz = centre.z - at.z; // as EtsJobs.AccountDriving hands EtsEconomy.ScaleAt
            float best = float.MaxValue;

            for (int i = 0; i < cities.Count; i++)
            {
                EtsMapCity city = cities[i];

                if (city == null || city.KdopMin == null || city.KdopMax == null)
                {
                    continue;
                }

                float dx = Mathf.Max(0f, Mathf.Max(city.KdopMin[0] - gx, gx - city.KdopMax[0]));
                float dz = Mathf.Max(0f, Mathf.Max(city.KdopMin[2] - gz, gz - city.KdopMax[2]));
                float d2 = dx * dx + dz * dz;
                if (d2 < best) best = d2;
            }

            return best == float.MaxValue ? 0f : 1f - Mathf.SmoothStep(0f, 1f, Mathf.Sqrt(best) / 350f);
        }

        private void Mix(float dt)
        {
            float k = 1f - Mathf.Exp(-dt / 2.5f);
            _day += (_dayTarget - _day) * (1f - Mathf.Exp(-dt / 5f));
            float town = Mathf.Max(_city, Mathf.Clamp01((_built - 0.04f) * 4f));
            float nature = Mathf.Clamp01((_nature + _loose * 0.5f) * 1.5f);
            float water = Mathf.Clamp01(_water * 4f);
            float motorway = Mathf.Clamp01(EtsCarAudio.FastTraffic) * (1f - 0.6f * town);
            _townS += (town - _townS) * k;
            _natureS += (nature - _natureS) * k;
            _waterS += (water - _waterS) * k;
            _motorwayS += (motorway - _motorwayS) * k;

            float cover = Mathf.Clamp01((_cover - 0.1f) / 0.5f); // 60 % of the rings on the map is a full picture
            _master = Mathf.Clamp01(Volume * 2f) * cover * EtsTruckAudio.ListenerOutsideGain;
            float night = 1f - _day;

            _target[Wind] = 0.2f + 0.12f * (1f - _townS);
            _target[Birds] = 0f; // no bird sounds (user request)
            _target[Crickets] = (0.05f + 0.17f * night) * _natureS * (1f - 0.7f * _townS);
            _target[Town] = 0.34f * _townS;
            _target[Motorway] = 0.36f * _motorwayS;
            _target[Water] = 0.32f * _waterS;

            float f = 1f - Mathf.Exp(-dt / 1.2f);

            for (int i = 0; i < LoopCount; i++)
            {
                _level[i] += (_target[i] * _master - _level[i]) * f;
                AudioSource s = _loops[i];

                if (_level[i] > 0.0008f && s.clip != null)
                {
                    s.volume = Mathf.Min(_level[i], 1f);

                    if (!s.isPlaying)
                    {
                        s.time = Random.value * s.clip.length * 0.9f;
                        s.Play();

                        if (!_loggedPlay)
                        {
                            _loggedPlay = true;
                            Debug.Log("[SomniumEuroTruck] Ambience: playing (" + LoopNames[i] + " first; six crossfaded loops and 3D calls round the listener).");
                        }
                    }
                }
                else if (s.isPlaying)
                {
                    s.Stop();
                }
            }
        }

        /// <summary>The 3D one-shots at random spots round the listener.</summary>
        private void Calls(float now, Vector3 at)
        {
            if (_master < 0.01f)
            {
                return;
            }

            if (now >= _nextBird)
            {
                _nextBird = now + Random.Range(2.5f, 8f) / Mathf.Max(0.25f, _natureS * _day);

                if (BirdCalls && _day > 0.5f && _natureS > 0.25f && _townS < 0.8f)
                {
                    Shot(s_birdCalls[Random.Range(0, s_birdCalls.Length)], at, 15f, 45f, 3f, 12f, 0.3f, Random.Range(0.92f, 1.1f));
                }
            }

            if (now >= _nextGull)
            {
                _nextGull = now + Random.Range(7f, 18f);

                if (_day > 0.5f && _waterS > 0.2f)
                {
                    Shot(s_gull, at, 25f, 70f, 12f, 30f, 0.55f, Random.Range(0.9f, 1.1f));
                }
            }

            if (now >= _nextOwl)
            {
                _nextOwl = now + Random.Range(25f, 60f);

                if (_day < 0.4f && _natureS > 0.35f && _townS < 0.5f)
                {
                    Shot(s_owl, at, 40f, 90f, 5f, 15f, 0.5f, Random.Range(0.95f, 1.05f));
                }
            }

            if (now >= _nextTown)
            {
                _nextTown = now + Random.Range(6f, 16f) / Mathf.Max(0.3f, _townS);

                if (_townS > 0.35f)
                {
                    float roll = Random.value;

                    if (roll < 0.08f && now - _lastSiren > 150f)
                    {
                        _lastSiren = now;
                        Shot(s_siren, at, 150f, 300f, 2f, 6f, 0.9f, Random.Range(0.97f, 1.03f));
                    }
                    else if (roll < 0.45f)
                    {
                        Shot(s_horn, at, 40f, 140f, 0.5f, 2f, 0.45f, Random.Range(0.9f, 1.12f));
                    }
                    else if (roll < 0.75f)
                    {
                        Shot(s_airBrake, at, 40f, 120f, 0.5f, 2f, 0.35f, Random.Range(0.9f, 1.1f));
                    }
                    else
                    {
                        Shot(s_clank, at, 30f, 100f, 0.5f, 4f, 0.4f, Random.Range(0.85f, 1.15f));
                    }
                }
            }
        }

        private void Shot(AudioClip clip, Vector3 at, float near, float far, float low, float high, float level, float pitch)
        {
            if (clip == null)
            {
                return;
            }

            for (int i = 0; i < _shots.Length; i++)
            {
                AudioSource s = _shots[i];

                if (s == null || s.isPlaying)
                {
                    continue;
                }

                float angle = Random.value * Mathf.PI * 2f;
                float d = Random.Range(near, far);
                s.transform.position = new Vector3(at.x + Mathf.Cos(angle) * d, at.y + Random.Range(low, high), at.z + Mathf.Sin(angle) * d);
                s.clip = clip;
                s.volume = Mathf.Clamp01(level * _master);
                s.pitch = pitch;
                s.Play();
                return;
            }
        }

        // ---------------------------------------------------------------- synthesis (worker thread)

        private static void Request(string name, System.Func<float[]> make, System.Action<AudioClip> done)
        {
            s_expected++;
            EtsSynth.Request(name, EtsSynth.AmbientRate, make, clip =>
            {
                done(clip);
                s_arrived++;
            });
        }

        private static void RequestClips()
        {
            if (s_requested)
            {
                return;
            }

            s_requested = true;
            int r = EtsSynth.AmbientRate;
            Request("ets amb wind", () => WindLoop(r), c => s_loops[Wind] = c);
            Request("ets amb birds", () => BirdsLoop(r), c => s_loops[Birds] = c);
            Request("ets amb crickets", () => CricketsLoop(r), c => s_loops[Crickets] = c);
            Request("ets amb town", () => TownLoop(r), c => s_loops[Town] = c);
            Request("ets amb motorway", () => MotorwayLoop(r), c => s_loops[Motorway] = c);
            Request("ets amb water", () => WaterLoop(r), c => s_loops[Water] = c);

            for (int i = 0; i < s_birdCalls.Length; i++)
            {
                int type = i;
                Request("ets amb bird " + i, () => BirdCall(r, type), c => s_birdCalls[type] = c);
            }

            Request("ets amb gull", () => GullCall(r), c => s_gull = c);
            Request("ets amb owl", () => OwlCall(r), c => s_owl = c);
            Request("ets amb horn", () => HornWave(r, 0x40A1u), c => s_horn = c);
            Request("ets amb air brake", () => AirBrakeWave(r), c => s_airBrake = c);
            Request("ets amb clank", () => ClankWave(r), c => s_clank = c);
            Request("ets amb siren", () => SirenWave(r), c => s_siren = c);
        }

        /// <summary>Wind over open ground: a low rumble and a band of air that gust together, leaves rustling on the gusts.</summary>
        private static float[] WindLoop(int rate)
        {
            const double length = 8.0;
            int n = (int)(rate * length);
            var rng = new EtsSynth.Rng(0x57A1Du);
            float[] white = EtsSynth.White(n, rng);
            float[] low = EtsSynth.Lowpass(EtsSynth.Lowpass(white, EtsSynth.Coef(70, rate)), EtsSynth.Coef(120, rate));
            float[] body = EtsSynth.Band(white, EtsSynth.Coef(420, rate), EtsSynth.Coef(90, rate));
            float[] leaves = EtsSynth.Highpass(EtsSynth.White(n, rng), EtsSynth.Coef(1800, rate));
            float[] flutter = EtsSynth.Lowpass(EtsSynth.White(n, rng), EtsSynth.Coef(4, rate));
            float[] wander = EtsSynth.Lowpass(EtsSynth.White(n, rng), EtsSynth.Coef(0.4, rate));
            EtsSynth.Normalise(low, 1f);
            EtsSynth.Normalise(body, 1f);
            EtsSynth.Normalise(leaves, 1f);
            EtsSynth.Normalise(flutter, 1f);
            EtsSynth.Normalise(wander, 1f);
            var d = new float[n];

            for (int j = 0; j < n; j++)
            {
                double t = (double)j / rate;
                float gust = 0.62f + 0.2f * EtsSynth.Sin(t / length + 0.1) + 0.1f * EtsSynth.Sin(3.0 * t / length + 0.37) + 0.08f * wander[j];
                float g2 = gust * gust;
                float rustle = Mathf.Max(0f, 0.4f + 0.6f * flutter[j]) * g2;
                d[j] = low[j] * 0.55f * gust + body[j] * 0.35f * g2 + leaves[j] * 0.16f * rustle;
            }

            return d;
        }

        /// <summary>A morning chorus: twenty near phrases and three dozen faint far ones, wrapped round a 12 s loop.</summary>
        private static float[] BirdsLoop(int rate)
        {
            const double length = 12.0;
            int n = (int)(rate * length);
            var rng = new EtsSynth.Rng(0xB12D5u);
            var d = new float[n];

            for (int k = 0; k < 20; k++)
            {
                BirdPhrase(d, rate, rng.Unit() * length, rng, rng.Range(0.3f, 1f), rng.Int(4));
            }

            for (int k = 0; k < 36; k++)
            {
                BirdPhrase(d, rate, rng.Unit() * length, rng, rng.Range(0.06f, 0.18f), rng.Int(4));
            }

            return d;
        }

        private static float[] BirdCall(int rate, int type)
        {
            var rng = new EtsSynth.Rng(0xCA11u + (uint)type * 7919u);
            var d = new float[(int)(rate * 2.2)];
            double took = BirdPhrase(d, rate, 0.05, rng, 1f, type);

            if (took < 0.9)
            {
                BirdPhrase(d, rate, 0.05 + took + rng.Range(0.15f, 0.35f), rng, 0.8f, type);
            }

            return d;
        }

        /// <summary>One bird phrase of the given type (0 trill, 1 whistle, 2 warble, 3 chirps); returns how long it lasts.</summary>
        private static double BirdPhrase(float[] d, int rate, double at, EtsSynth.Rng rng, float amp, int type)
        {
            double t = at;

            switch (type)
            {
                case 0:
                {
                    int notes = 4 + rng.Int(6);
                    double f = rng.Range(3200f, 5000f), dur = rng.Range(0.03f, 0.06f), gap = rng.Range(0.02f, 0.05f), drop = rng.Range(0.6f, 0.85f);

                    for (int i = 0; i < notes; i++)
                    {
                        EtsSynth.Tone(d, rate, t, dur, f, f * drop, amp * rng.Range(0.7f, 1f), 0.004, 0.012, f * 2 < rate * 0.45 ? 0.1f : 0f);
                        t += dur + gap;
                        f *= rng.Range(0.97f, 1.03f);
                    }

                    break;
                }
                case 1:
                {
                    int notes = 2 + rng.Int(2);
                    double f = rng.Range(2000f, 3100f);

                    for (int i = 0; i < notes; i++)
                    {
                        double dur = rng.Range(0.12f, 0.25f);
                        EtsSynth.Tone(d, rate, t, dur, f, f * rng.Range(1f, 1.15f), amp, 0.02, 0.04, 0.08f, 0f, rng.Range(9f, 14f), rng.Range(0.02f, 0.04f));
                        t += dur + rng.Range(0.08f, 0.2f);
                        f *= rng.Range(0.9f, 1.12f);
                    }

                    break;
                }
                case 2:
                {
                    int notes = 5 + rng.Int(4);
                    double f = rng.Range(1600f, 3200f);

                    for (int i = 0; i < notes; i++)
                    {
                        double next = rng.Range(1600f, 3200f);
                        double dur = rng.Range(0.06f, 0.14f);
                        EtsSynth.Tone(d, rate, t, dur, f, f + (next - f) * 0.5, amp * rng.Range(0.75f, 1f), 0.01, 0.02, 0.2f, 0.05f);
                        t += dur + rng.Range(0.01f, 0.04f);
                        f = next;
                    }

                    break;
                }
                default:
                {
                    int notes = 2 + rng.Int(2);
                    double f = rng.Range(2400f, 3000f);

                    for (int i = 0; i < notes; i++)
                    {
                        double dur = rng.Range(0.02f, 0.035f);
                        EtsSynth.Tone(d, rate, t, dur, f, f * 1.7, amp, 0.003, 0.008);
                        t += dur + rng.Range(0.035f, 0.06f);
                    }

                    break;
                }
            }

            return t - at;
        }

        /// <summary>Five field crickets chirping at their own rates and one tree cricket's steady trill, in a 6 s loop.</summary>
        private static float[] CricketsLoop(int rate)
        {
            const double length = 6.0;
            int n = (int)(rate * length);
            var rng = new EtsSynth.Rng(0xC41C7u);
            var d = new float[n];

            for (int c = 0; c < 5; c++)
            {
                double fc = rng.Range(3900f, 4700f);
                int chirps = 9 + rng.Int(10);
                double period = length / chirps;
                int pulses = 3 + rng.Int(2);
                double pulseRate = rng.Range(28f, 36f);
                double pulse = rng.Range(0.011f, 0.016f);
                float amp = rng.Range(0.3f, 1f);
                double offset = rng.Unit() * period;

                for (int i = 0; i < chirps; i++)
                {
                    for (int p = 0; p < pulses; p++)
                    {
                        EtsSynth.Tone(d, rate, offset + i * period + p / pulseRate, pulse, fc, fc * 0.98, amp, pulse * 0.3, pulse * 0.5);
                    }
                }
            }

            double ft = rng.Range(2800f, 3200f);
            const int trill = 270;

            for (int i = 0; i < trill; i++)
            {
                double t = i * length / trill;
                EtsSynth.Tone(d, rate, t, 0.009, ft, ft, 0.18f * (0.6f + 0.4f * EtsSynth.Sin(t / length)), 0.003, 0.004);
            }

            return d;
        }

        /// <summary>A town heard from its streets: a low traffic rumble, a murmur, and distant vehicles swelling past.</summary>
        private static float[] TownLoop(int rate)
        {
            const double length = 10.0;
            int n = (int)(rate * length);
            var rng = new EtsSynth.Rng(0x70E1u);
            float[] rumble = EtsSynth.Lowpass(EtsSynth.Lowpass(EtsSynth.White(n, rng), EtsSynth.Coef(55, rate)), EtsSynth.Coef(90, rate));
            float[] mid = EtsSynth.Band(EtsSynth.White(n, rng), EtsSynth.Coef(420, rate), EtsSynth.Coef(110, rate));
            float[] murmur = EtsSynth.Band(EtsSynth.White(n, rng), EtsSynth.Coef(1600, rate), EtsSynth.Coef(380, rate));
            EtsSynth.Normalise(rumble, 1f);
            EtsSynth.Normalise(mid, 1f);
            EtsSynth.Normalise(murmur, 1f);
            var d = new float[n];

            for (int j = 0; j < n; j++)
            {
                double t = (double)j / rate;
                d[j] = rumble[j] * 0.5f * (0.85f + 0.15f * EtsSynth.Sin(t / length)) + mid[j] * 0.2f + murmur[j] * 0.05f;
            }

            for (int k = 0; k < 8; k++)
            {
                PassBy(d, rate, rng.Unit() * length, rng.Range(0.8f, 2f), rng.Range(65f, 140f), rng.Range(0.08f, 0.25f), mid, 0.8f, 0.04);
            }

            return d;
        }

        /// <summary>A motorway: a broadband tyre roar with vehicles swelling past, two of them lorries with an engine note.</summary>
        private static float[] MotorwayLoop(int rate)
        {
            const double length = 10.0;
            int n = (int)(rate * length);
            var rng = new EtsSynth.Rng(0x3A7u);
            float[] white = EtsSynth.White(n, rng);
            float[] a = EtsSynth.Lowpass(white, EtsSynth.Coef(900, rate));
            float[] b = EtsSynth.Lowpass(white, EtsSynth.Coef(250, rate));
            float[] c = EtsSynth.Lowpass(EtsSynth.White(n, rng), EtsSynth.Coef(60, rate));
            EtsSynth.Normalise(a, 1f);
            EtsSynth.Normalise(b, 1f);
            EtsSynth.Normalise(c, 1f);
            var d = new float[n];

            for (int j = 0; j < n; j++)
            {
                double t = (double)j / rate;
                d[j] = (a[j] * 0.3f + b[j] * 0.55f + c[j] * 0.45f) * (0.8f + 0.1f * EtsSynth.Sin(t / length) + 0.1f * EtsSynth.Sin(2.0 * t / length + 0.3));
            }

            for (int k = 0; k < 9; k++)
            {
                double centre = rng.Unit() * length, sigma = rng.Range(0.6f, 1.4f);
                float amp = rng.Range(0.35f, 0.9f);
                int from = (int)((centre - 3.0 * sigma) * rate), to = (int)((centre + 3.0 * sigma) * rate);

                for (int j = from; j < to; j++)
                {
                    double t = (double)j / rate;
                    double x = (t - centre) / sigma;
                    int i = ((j % n) + n) % n;
                    d[i] += amp * (float)System.Math.Exp(-0.5 * x * x) * (a[i] * 0.7f + b[i] * 0.5f);
                }

                if (k < 2)
                {
                    PassBy(d, rate, centre, sigma, rng.Range(55f, 80f), amp * 0.5f, b, 0f, 0.05);
                }
            }

            return d;
        }

        /// <summary>A vehicle passing at a distance: a Gaussian swell of an engine tone (with a little Doppler) and a band of noise.</summary>
        private static void PassBy(float[] d, int rate, double centre, double sigma, double f, float amp, float[] noise, float noiseShare, double doppler)
        {
            int n = d.Length;
            int from = (int)((centre - 3.0 * sigma) * rate), to = (int)((centre + 3.0 * sigma) * rate);
            double phase = 0.0;

            for (int j = from; j < to; j++)
            {
                double t = (double)j / rate;
                double x = (t - centre) / sigma;
                float g = (float)System.Math.Exp(-0.5 * x * x);
                phase += f * (1.0 + doppler * System.Math.Tanh((centre - t) / sigma)) / rate;
                int i = ((j % n) + n) % n;
                float tone = (EtsSynth.Sin(phase) + 0.5f * EtsSynth.Sin(2.0 * phase) + 0.25f * EtsSynth.Sin(3.0 * phase)) * 0.5f;
                d[i] += amp * g * (tone + noise[i] * noiseShare);
            }
        }

        /// <summary>Water lapping at a bank: slow swells of filtered noise, a low wash and small bubbles.</summary>
        private static float[] WaterLoop(int rate)
        {
            const double length = 8.0;
            int n = (int)(rate * length);
            var rng = new EtsSynth.Rng(0x3A7E2u);
            float[] band = EtsSynth.Band(EtsSynth.White(n, rng), EtsSynth.Coef(1300, rate), EtsSynth.Coef(160, rate));
            float[] low = EtsSynth.Lowpass(EtsSynth.White(n, rng), EtsSynth.Coef(90, rate));
            EtsSynth.Normalise(band, 1f);
            EtsSynth.Normalise(low, 1f);
            var d = new float[n];

            for (int j = 0; j < n; j++)
            {
                double t = (double)j / rate;
                d[j] = low[j] * 0.22f * (0.6f + 0.4f * EtsSynth.Sin(2.0 * t / length));
            }

            for (int k = 0; k < 8; k++)
            {
                double at = rng.Unit() * length, attack = rng.Range(0.25f, 0.5f), tau = rng.Range(0.4f, 0.9f);
                float amp = rng.Range(0.35f, 1f);
                EtsSynth.Burst(d, rate, at, band, attack, tau, amp * 0.7f);
                EtsSynth.Burst(d, rate, at, low, attack * 1.2, tau * 1.3, amp * 0.35f);
            }

            for (int k = 0; k < 70; k++)
            {
                double f = rng.Range(300f, 900f), dur = rng.Range(0.015f, 0.045f);
                EtsSynth.Tone(d, rate, rng.Unit() * length, dur, f, f * 2.2, rng.Range(0.04f, 0.16f), 0.002, dur * 0.7);
            }

            return d;
        }

        /// <summary>A herring gull's "kyow" three times: a rising-falling nasal tone, its harmonics peaked round 2.3 kHz, a little rasp.</summary>
        private static float[] GullCall(int rate)
        {
            int n = (int)(rate * 1.8);
            var d = new float[n];
            var rng = new EtsSynth.Rng(0x6011u);
            double t = 0.05;

            for (int c = 0; c < 3; c++)
            {
                double dur = c == 0 ? rng.Range(0.28f, 0.34f) : rng.Range(0.22f, 0.28f);
                double baseF = rng.Range(820f, 980f);
                int start = (int)(t * rate), len = (int)(dur * rate);
                double phase = 0.0;

                for (int j = 0; j < len && start + j < n; j++)
                {
                    double u = (double)j / len, tt = (double)j / rate;
                    double f = baseF * (1.0 + 0.4 * System.Math.Sin(System.Math.PI * u) - 0.2 * u);
                    phase += f / rate;
                    double s = 0.0;

                    for (int k = 1; k <= 10; k++)
                    {
                        double fk = k * f;
                        if (fk > rate * 0.45) break;
                        double x = (fk - 2300.0) / 1300.0;
                        s += System.Math.Exp(-x * x) / System.Math.Sqrt(k) * System.Math.Sin(2.0 * System.Math.PI * k * phase);
                    }

                    double env = System.Math.Min(1.0, tt / 0.02) * System.Math.Min(1.0, (dur - tt) / 0.06);
                    d[start + j] += (float)(env * (1.0 + 0.25 * System.Math.Sin(2.0 * System.Math.PI * 75.0 * tt)) * s);
                }

                t += dur + rng.Range(0.12f, 0.22f);
            }

            return d;
        }

        /// <summary>A tawny owl: "hoo ... hoo-hooo".</summary>
        private static float[] OwlCall(int rate)
        {
            var d = new float[(int)(rate * 2.0)];
            var rng = new EtsSynth.Rng(0x0771u);
            float[] breath = EtsSynth.Band(EtsSynth.White(d.Length, rng), EtsSynth.Coef(900, rate), EtsSynth.Coef(250, rate));
            EtsSynth.Normalise(breath, 1f);
            EtsSynth.Tone(d, rate, 0.05, 0.38, 385, 372, 1f, 0.07, 0.12, 0.12f);
            EtsSynth.Tone(d, rate, 0.75, 0.2, 380, 390, 0.8f, 0.05, 0.05, 0.1f);
            EtsSynth.Tone(d, rate, 1.0, 0.6, 390, 360, 0.9f, 0.05, 0.2, 0.12f, 0f, 7.0, 0.015);
            EtsSynth.Burst(d, rate, 0.05, breath, 0.05, 0.15, 0.08f);
            EtsSynth.Burst(d, rate, 0.75, breath, 0.04, 0.3, 0.06f);
            return d;
        }

        /// <summary>A car horn: two beeps of a two-note (415/520 Hz) reed tone. Also EtsCarAudio's horn.</summary>
        internal static float[] HornWave(int rate, uint seed)
        {
            var d = new float[(int)(rate * 0.8)];
            var rng = new EtsSynth.Rng(seed);
            double lo = rng.Range(400f, 430f), hi = lo * 1.25;
            HornBeep(d, rate, 0.03, 0.2, lo, hi);
            HornBeep(d, rate, 0.33, 0.28, lo, hi);
            return d;
        }

        private static void HornBeep(float[] d, int rate, double at, double dur, double lo, double hi)
        {
            int start = (int)(at * rate), len = (int)(dur * rate);

            for (int j = 0; j < len && start + j < d.Length; j++)
            {
                double t = (double)j / rate;
                double env = System.Math.Min(1.0, t / 0.008) * System.Math.Min(1.0, (dur - t) / 0.02);
                double s = 0.0;

                for (int k = 1; k <= 7; k += 2)
                {
                    if (k * hi < rate * 0.45) s += (System.Math.Sin(2.0 * System.Math.PI * k * lo * t) + System.Math.Sin(2.0 * System.Math.PI * k * hi * t)) / k;
                }

                float v = (float)(s * 0.5);
                d[start + j] += (float)env * v / (1f + 0.6f * System.Math.Abs(v));
            }
        }

        /// <summary>A bus's air brakes letting go: a thump and a sharp hiss.</summary>
        private static float[] AirBrakeWave(int rate)
        {
            var d = new float[(int)(rate * 1.0)];
            var rng = new EtsSynth.Rng(0xA12Bu);
            float[] hiss = EtsSynth.Highpass(EtsSynth.White(d.Length, rng), EtsSynth.Coef(2200, rate));
            EtsSynth.Normalise(hiss, 1f);
            EtsSynth.Burst(d, rate, 0.0, hiss, 0.005, 0.22, 1f);
            EtsSynth.Tone(d, rate, 0.0, 0.08, 90, 60, 0.4f, 0.002, 0.06);
            return d;
        }

        /// <summary>A clank of metal (a skip, a scaffold, a manhole lid): struck inharmonic partials, twice.</summary>
        private static float[] ClankWave(int rate)
        {
            var d = new float[(int)(rate * 0.8)];
            var rng = new EtsSynth.Rng(0xC1A4u);
            float[] click = EtsSynth.White(d.Length, rng);
            double[] f = { 523, 1311, 2230, 3197, 4410 };
            double[] tau = { 0.3, 0.22, 0.15, 0.1, 0.07 };
            float[] amp = { 1f, 0.7f, 0.5f, 0.35f, 0.2f };
            EtsSynth.Strike(d, rate, 0.01, f, tau, amp, 1f);
            EtsSynth.Burst(d, rate, 0.01, click, 0.0005, 0.002, 0.6f);

            for (int i = 0; i < f.Length; i++) f[i] *= 1.05;

            EtsSynth.Strike(d, rate, 0.18, f, tau, amp, 0.4f);
            return d;
        }

        /// <summary>A distant two-tone siren (440/587 Hz), fading in and out.</summary>
        private static float[] SirenWave(int rate)
        {
            const double length = 6.0;
            int n = (int)(rate * length);
            var d = new float[n];
            double phase = 0.0;

            for (int j = 0; j < n; j++)
            {
                double t = (double)j / rate;
                double f = ((int)(t / 0.55) & 1) == 0 ? 440.0 : 587.0;
                phase += f / rate;
                double s = 0.0;
                for (int k = 1; k <= 5; k++) s += System.Math.Sin(2.0 * System.Math.PI * k * phase) / k;
                double env = System.Math.Min(1.0, t / 1.2) * System.Math.Min(1.0, (length - t) / 1.5);
                d[j] = (float)(s * env);
            }

            return EtsSynth.Lowpass(d, EtsSynth.Coef(1800, rate));
        }
    }

    /// <summary>
    /// What a collider is, as ground: water, a hard surface (road, pavement, yard), loose ground (gravel, dirt, a field),
    /// green ground (grass, forest), a building, or not part of the map at all. Worked out once per collider from the
    /// names the map builders give (EtsTerrainBuilder: "terrain &lt;side&gt; &lt;brush&gt;"; EtsMapBuilder: road pieces under
    /// "road &lt;look&gt;"; EtsGroundFill: "fill x z" under "ground fill") and the material's game path (materials are named by
    /// their path, e.g. /umatlib/legacy/terrain/water/water_river.mat), then cached - a lookup is a dictionary hit.
    /// Used by EtsAmbience's probe rays and by EtsTruckAudio for what the tyres roll on.
    /// </summary>
    internal static class EtsGroundSurface
    {
        public const byte Unknown = 0, Hard = 1, Loose = 2, Nature = 3, Water = 4, Built = 5;
        public static readonly string[] Names = { "unknown", "hard", "loose", "green", "water", "built" };

        private static readonly Dictionary<Collider, byte> s_cache = new Dictionary<Collider, byte>(1024);
        private static GameObject s_mapRoot;

        // Substrings of lower-cased object names and material paths. Chosen to avoid the obvious traps: no "tree"
        // (street), "corn" (corner), "rape" (grape), "shed" (crushed) or "ground" (background, underground).
        private static readonly string[] Waters = { "water", "river", "lake", "sea_", "pond" };
        private static readonly string[] Looses = { "gravel", "dirt", "mud", "sand", "soil", "field", "offroad", "earth", "rubble" };
        private static readonly string[] Hards = { "asph", "concrete", "pave", "cobble", "tile", "brick", "parking", "sidewalk", "kerb", "curb" };
        private static readonly string[] Greens = { "grass", "forest", "meadow", "moss", "leaf", "leaves", "bush", "/tree", "tree_", "trees", "veget", "fallen", "hedge", "crop", "barley", "wheat", "rapeseed", "flower", "garden", "lawn" };
        private static readonly string[] Buildings = { "building", "house", "roof", "warehouse", "tower", "factory" };

        public static byte Of(Collider c)
        {
            if (ReferenceEquals(c, null) || c == null)
            {
                return Unknown;
            }

            EtsMapViewer viewer = EtsMapViewer.Current;
            GameObject root = viewer != null ? viewer.Root : null;

            if (!ReferenceEquals(root, s_mapRoot))
            {
                s_mapRoot = root;
                s_cache.Clear();
            }

            if (s_cache.TryGetValue(c, out byte kind))
            {
                return kind;
            }

            if (s_cache.Count > 4096)
            {
                s_cache.Clear();
            }

            kind = Classify(c, root);
            s_cache[c] = kind;
            return kind;
        }

        private static byte Classify(Collider c, GameObject root)
        {
            Transform t = c.transform;
            Transform parent = t.parent;

            if (root == null || !t.IsChildOf(root.transform))
            {
                // Ground-fill tiles (EtsGroundFill) sit outside the map root: the grassland under everything.
                return parent != null && parent.name == "ground fill" ? Nature : Unknown;
            }

            string own = t.name.ToLowerInvariant();
            string material = "";

            if (c.TryGetComponent(out Renderer renderer) && renderer.sharedMaterial != null)
            {
                material = renderer.sharedMaterial.name.ToLowerInvariant();
            }

            string text = own + " | " + material;

            if (Has(text, Waters))
            {
                return Water;
            }

            bool terrain = Starts(own, "terrain") || Starts(own, "prefab terrain") || Starts(own, "bezier") || Starts(own, "median") || Starts(own, "fill ");

            if (terrain)
            {
                // "terrain x invisible": terrain_mat.inv, substance "road" - drivable ground under dirt decals.
                if (own.Contains("invisible")) return Hard;
                if (Has(text, Looses)) return Loose;
                if (Has(text, Hards)) return Hard;
                return Nature;
            }

            if (Has(text, Looses)) return Loose;
            if (AncestorStarts(t, "building ", 4)) return Built; // EtsRunBuilder's runs
            if (AncestorStarts(t, "road ", 4) || text.Contains("road") || Has(text, Hards)) return Hard;
            if (Has(text, Greens)) return Nature;
            if (Has(text, Buildings)) return Built;

            // Anything else on the map: tall and boxy is a building; wide and flat (a junction on a slope, a yard) is ground.
            Vector3 size = c.bounds.size;
            return size.y > 6f && size.y > 0.25f * Mathf.Max(size.x, size.z) ? Built : Hard;
        }

        private static bool Starts(string text, string prefix)
        {
            return text.StartsWith(prefix, System.StringComparison.Ordinal);
        }

        /// <summary>Whether this transform or one of its first few ancestors has a name starting with the prefix (first sight only: names allocate).</summary>
        private static bool AncestorStarts(Transform t, string prefix, int levels)
        {
            for (int i = 0; i < levels && t != null; i++, t = t.parent)
            {
                if (t.name.StartsWith(prefix, System.StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool Has(string text, string[] words)
        {
            for (int i = 0; i < words.Length; i++)
            {
                if (text.IndexOf(words[i], System.StringComparison.Ordinal) >= 0)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Sound synthesis off the main thread. A request is a function that fills a float array (pure maths, no Unity API);
    /// one background thread runs the requests in order and normalises each to 0.9 peak; the main thread (Pump, called
    /// every frame by EtsAmbience, EtsCarAudio and EtsTruckAudio - once a frame whoever calls first) turns at most one
    /// finished array a frame into a mono AudioClip (AudioClip.Create + SetData, the route the Somnium client plays) and
    /// hands it to the request's callback. If no thread can be started it builds one request a frame on the main thread.
    /// The DSP helpers below are all thread-safe (their state is their arguments).
    /// </summary>
    internal static class EtsSynth
    {
        /// <summary>Ambience, traffic and the truck's extras: 22.05 kHz is ample for noise beds, birds and thuds, and halves the memory.</summary>
        public const int AmbientRate = 22050;

        private sealed class Job
        {
            public string Name;
            public int Rate;
            public System.Func<float[]> Make;
            public System.Action<AudioClip> Done;
            public float[] Data;
            public string Error;
            public double Ms;
        }

        private static readonly object s_lock = new object();
        private static readonly Queue<Job> s_todo = new Queue<Job>();
        private static readonly Queue<Job> s_done = new Queue<Job>();
        private static System.Threading.Thread s_thread;
        private static bool s_threadFailed;
        private static int s_outstanding;
        private static int s_pumpFrame = -1;
        private static int s_built;
        private static double s_seconds, s_ms;
        private static float s_firstRequest = -1f;

        public static void Request(string name, int rate, System.Func<float[]> make, System.Action<AudioClip> done)
        {
            var job = new Job { Name = name, Rate = rate, Make = make, Done = done };

            lock (s_lock)
            {
                if (s_outstanding == 0)
                {
                    s_firstRequest = Time.realtimeSinceStartup;
                }

                s_outstanding++;
                s_todo.Enqueue(job);

                if (s_thread == null && !s_threadFailed)
                {
                    try
                    {
                        s_thread = new System.Threading.Thread(Run) { Name = "Ets sound synth", IsBackground = true, Priority = System.Threading.ThreadPriority.BelowNormal };
                        s_thread.Start();
                    }
                    catch (System.Exception e)
                    {
                        s_thread = null;
                        s_threadFailed = true;
                        Debug.Log("[SomniumEuroTruck] Sound synth: no worker thread (" + e.GetType().Name + "); building one sound a frame on the main thread.");
                    }
                }
            }
        }

        private static void Run()
        {
            while (true)
            {
                Job job;

                lock (s_lock)
                {
                    if (s_todo.Count == 0)
                    {
                        s_thread = null;
                        return;
                    }

                    job = s_todo.Dequeue();
                }

                Make(job);

                lock (s_lock)
                {
                    s_done.Enqueue(job);
                }
            }
        }

        private static void Make(Job job)
        {
            System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                job.Data = job.Make();
                if (job.Data != null) Normalise(job.Data, 0.9f);
            }
            catch (System.Exception e)
            {
                job.Error = e.GetType().Name + ": " + e.Message;
            }

            job.Ms = clock.Elapsed.TotalMilliseconds;
        }

        /// <summary>Main thread, every frame (any caller; runs once a frame): at most one finished sound becomes a clip.</summary>
        public static void Pump()
        {
            int frame = Time.frameCount;

            if (frame == s_pumpFrame || s_outstanding == 0)
            {
                return;
            }

            s_pumpFrame = frame;
            Job job = null;

            lock (s_lock)
            {
                if (s_done.Count > 0)
                {
                    job = s_done.Dequeue();
                }
                else if (s_threadFailed && s_todo.Count > 0)
                {
                    job = s_todo.Dequeue();
                }
            }

            if (job == null)
            {
                return;
            }

            if (job.Data == null && job.Error == null)
            {
                Make(job); // the main-thread fallback
            }

            AudioClip clip = null;

            if (job.Data != null && job.Data.Length > 0)
            {
                clip = AudioClip.Create(job.Name, job.Data.Length, 1, job.Rate, false);
                clip.SetData(job.Data, 0);
                s_built++;
                s_seconds += job.Data.Length / (double)job.Rate;
                s_ms += job.Ms;
            }
            else
            {
                Debug.Log("[SomniumEuroTruck] Sound synth: '" + job.Name + "' failed: " + (job.Error ?? "no data") + ".");
            }

            job.Data = null;

            try
            {
                job.Done?.Invoke(clip);
            }
            catch (System.Exception e)
            {
                Debug.LogException(e);
            }

            lock (s_lock)
            {
                s_outstanding--;
            }

            if (s_outstanding == 0)
            {
                Debug.Log("[SomniumEuroTruck] Sound synth: " + s_built + " sounds (" + s_seconds.ToString("0.0") + " s of audio) built " + (s_threadFailed ? "on the main thread" : "on a worker thread")
                    + " in " + s_ms.ToString("0") + " ms of work, all ready " + (Time.realtimeSinceStartup - s_firstRequest).ToString("0.0") + " s after the first request (ambience, traffic, truck extras).");
            }
        }

        // ---------------------------------------------------------------- DSP helpers (thread-safe)

        /// <summary>xorshift32: the synthesis's own noise, so threads never share a generator.</summary>
        public sealed class Rng
        {
            private uint _s;

            public Rng(uint seed)
            {
                _s = seed != 0 ? seed : 0x9E3779B9u;
            }

            /// <summary>[0, 1)</summary>
            public float Unit()
            {
                _s ^= _s << 13;
                _s ^= _s >> 17;
                _s ^= _s << 5;
                return (_s & 0xFFFFFF) / 16777216f;
            }

            /// <summary>[-1, 1)</summary>
            public float Next() => Unit() * 2f - 1f;

            public float Range(float a, float b) => a + (b - a) * Unit();

            public int Int(int count) => count <= 1 ? 0 : (int)(Unit() * count);
        }

        /// <summary>The one-pole low-pass coefficient for a cut-off.</summary>
        public static float Coef(double hz, int rate) => (float)(1.0 - System.Math.Exp(-2.0 * System.Math.PI * hz / rate));

        public static float Sin(double cycles) => (float)System.Math.Sin(cycles * 2.0 * System.Math.PI);

        public static float Decay(double t, double tau) => t < 0.0 ? 0f : (float)System.Math.Exp(-t / tau);

        public static float[] White(int n, Rng rng)
        {
            var d = new float[n];
            for (int i = 0; i < n; i++) d[i] = rng.Next();
            return d;
        }

        /// <summary>One-pole low-pass run twice round the buffer, so its end state meets its start and a loop has no seam.</summary>
        public static float[] Lowpass(float[] x, float a)
        {
            var y = new float[x.Length];
            float s = 0f;

            for (int pass = 0; pass < 2; pass++)
            {
                for (int i = 0; i < x.Length; i++)
                {
                    s += (x[i] - s) * a;
                    if (pass == 1) y[i] = s;
                }
            }

            return y;
        }

        public static float[] Highpass(float[] x, float a)
        {
            float[] y = Lowpass(x, a);
            for (int i = 0; i < y.Length; i++) y[i] = x[i] - y[i];
            return y;
        }

        /// <summary>A band: low-passed at the upper edge, minus its own low-pass at the lower edge.</summary>
        public static float[] Band(float[] x, float upper, float lower)
        {
            float[] y = Lowpass(x, upper);
            float[] z = Lowpass(y, lower);
            for (int i = 0; i < y.Length; i++) y[i] -= z[i];
            return y;
        }

        public static void Normalise(float[] d, float peak)
        {
            float max = 0f;

            for (int i = 0; i < d.Length; i++)
            {
                float a = d[i] < 0f ? -d[i] : d[i];
                if (a > max) max = a;
            }

            if (max < 1e-6f)
            {
                return;
            }

            float g = peak / max;
            for (int i = 0; i < d.Length; i++) d[i] *= g;
        }

        /// <summary>
        /// A note gliding from f0 to f1, with an optional vibrato and second/third harmonic, a linear attack and release,
        /// added into d from <paramref name="at"/> seconds, wrapping round the end (so loops stay seamless).
        /// </summary>
        public static void Tone(float[] d, int rate, double at, double dur, double f0, double f1, float amp, double attack, double release,
            float h2 = 0f, float h3 = 0f, double vibratoHz = 0.0, double vibratoDepth = 0.0)
        {
            int n = d.Length;
            int start = (int)(at * rate);
            int len = (int)(dur * rate);
            double nyquist = rate * 0.45, phase = 0.0;

            for (int j = 0; j < len; j++)
            {
                double t = (double)j / rate, u = (double)j / len;
                double f = f0 + (f1 - f0) * u;
                if (vibratoDepth > 0.0) f *= 1.0 + vibratoDepth * System.Math.Sin(2.0 * System.Math.PI * vibratoHz * t);
                phase += f / rate;
                double env = System.Math.Min(1.0, t / System.Math.Max(attack, 1e-4)) * System.Math.Min(1.0, (dur - t) / System.Math.Max(release, 1e-4));
                double s = System.Math.Sin(2.0 * System.Math.PI * phase);
                if (h2 != 0f && 2.0 * f < nyquist) s += h2 * System.Math.Sin(4.0 * System.Math.PI * phase);
                if (h3 != 0f && 3.0 * f < nyquist) s += h3 * System.Math.Sin(6.0 * System.Math.PI * phase);
                d[((start + j) % n + n) % n] += (float)(amp * env * s);
            }
        }

        /// <summary>Struck partials, each decaying on its own time constant, from <paramref name="at"/> seconds (wrapping).</summary>
        public static void Strike(float[] d, int rate, double at, double[] freqs, double[] taus, float[] amps, float gain)
        {
            int n = d.Length;
            int start = (int)(at * rate);
            double longest = 0.0;
            for (int k = 0; k < taus.Length; k++) if (taus[k] > longest) longest = taus[k];
            int len = System.Math.Min(n, (int)(longest * 6.0 * rate));
            double nyquist = rate * 0.45;

            for (int j = 0; j < len; j++)
            {
                double t = (double)j / rate, s = 0.0;

                for (int k = 0; k < freqs.Length; k++)
                {
                    if (freqs[k] < nyquist) s += amps[k] * System.Math.Exp(-t / taus[k]) * System.Math.Sin(2.0 * System.Math.PI * freqs[k] * t);
                }

                d[(start + j) % n] += (float)(gain * s);
            }
        }

        /// <summary>A burst of a noise buffer: a linear attack, then an exponential decay (wrapping).</summary>
        public static void Burst(float[] d, int rate, double at, float[] noise, double attack, double tau, float gain)
        {
            int n = d.Length;
            int start = (int)(at * rate);
            int len = System.Math.Min(n, (int)((attack + tau * 6.0) * rate));

            for (int j = 0; j < len; j++)
            {
                double t = (double)j / rate;
                double env = t < attack ? t / attack : System.Math.Exp(-(t - attack) / tau);
                int i = (start + j) % n;
                d[i] += (float)(gain * env) * noise[i % noise.Length];
            }
        }
    }
}
