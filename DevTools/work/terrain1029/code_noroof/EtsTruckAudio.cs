using System.Collections.Generic;
using UnityEngine;

namespace SomniumSpace.Worlds.SomniumEuroTruck
{
    /// <summary>
    /// The truck's sounds: the game's own samples when they load (see "The game's real sounds" below), otherwise
    /// synthesised as follows (written when the banks were wrongly thought undecodable; kept as the fallback). The sound is built from the engine's own numbers - six cylinders firing three times per
    /// revolution give the fundamental, with harmonics and a turbo whistle that rise with load; tyre and wind noise
    /// grow with speed; the horn is a two-tone air horn - but NOT on the audio thread: OnAudioFilterRead is never
    /// called for world code in the Somnium client (the 2026-09-23 client heard nothing at all). Instead this takes
    /// the route the sibling worlds prove plays there (Quake's Q3SoundSystem/Q3Ambience, Age of Mythology's
    /// AomSounds): short loops and one-shots are built ONCE per client with AudioClip.Create + SetData (BuildClips,
    /// timed and logged), and ordinary AudioSources play them. The engine is three one-second loops at fixed base
    /// revs (640/1200/1920 rpm for six cylinders), each in an off-load and an on-load version; every frame the main
    /// thread sets their pitch (rpm / base rpm) and crossfades them by Truck.Rpm and Truck.Throttle. Turbo, road,
    /// wind, horn and reversing beeper are further loops; relay tick, air brake, gear clunk, engine start/stop and
    /// doors are one-shots (PlayOneShot). Inside the cab the sources are flat (2D) and the outside sounds are damped
    /// by the game's interior volume; a truck heard from outside is 3D with a 1/d fade from 12 m, silent past 300 m.
    ///
    /// The extras (2026-09-23, "add the rest of the sounds"), built on EtsSynth's worker thread so the 88 ms build above
    /// does not grow, and played only by the per-player sound (a puppet's stays engine-only): what the tyres roll on
    /// (EtsGroundSurface on each grounded wheel's hit collider, five times a second - gravel/dirt crunch and a soft
    /// grass rumble crossfade against the asphalt roar); suspension thuds from how fast any wheel's compression rises
    /// (kerbs, potholes, landings); a knock from any sudden change of the body's velocity (a crash, a car hit); the
    /// trailer's rattle over rough road and creak when it swings, and the coupling clunk and air lines when it is hooked
    /// or unhooked (Trailer.Attached edges); the engine brake's rasp when the driver is off the throttle in gear; and a
    /// cruise-control chime (PlayCruise). The world ambience (EtsAmbience) and the traffic (EtsCarAudio) are added by
    /// the per-player sound's Awake, and read this sound's cab damping (ListenerOutsideGain).
    ///
    /// The game's real sounds (2026-10-14): everything above is now the FALLBACK. The 2026-09 conclusion that the banks
    /// are undecodable was wrong - FSB5 leaves out the Vorbis headers, but the setup header is one of six FMOD encoder
    /// headers named by CRC-32, so they rebuild (EtsFmodBank, EtsVorbisSetups, vendored NVorbis). Each truck reads its own
    /// sound list from its definitions (EtsGameTruckSounds: engine/transmission/chassis "sounds[]", cabin and interior
    /// sound.sui) and EtsGameSound decodes the events from the player's install on a worker thread. When the truck's
    /// engine event is ready its loops replace the synthesised engine: engine/engine (the rpm sheet - DAF XF Euro 6: 12
    /// loaded/unloaded/engine-brake loops crossfaded over 350-3000 rpm, each auto-pitched from its recorded revs, the
    /// loaded track faded in by load, the brake track by the engine brake), engine/exhaust and engine/turbo, played by
    /// EtsGameEventPlayer exactly as the bank's regions, fades and track automations say; its timeline's start and stop
    /// samples replace the synthesised crank and run-down. The per-player sound also takes the horn (cabin sound.sui),
    /// the reversing beeper, air brake, air gear-shift, the interior relay ticks and parking-brake lever, the coupling
    /// clunk and air lines, the cab's aero noise and the tyres (/sound/tires/{asphalt,gravel,grass}.soundref - the
    /// exe's own path) from the game. Anything not loaded, or failing, stays synthesised; the log says which
    /// ("Truck sound: game samples from ..." / "Truck sound: synthesised (...)").
    /// 2026-10-14 "no more synthesised sounds": the air horn (N / the headset's stick click; H stays the truck's own
    /// horn), suspension noise and the cab's rattle on bumps, crashes (/sound/collisions) for knocks and car hits, brake
    /// squeal, rain on the cab, skids (the tyre events' skid_speed from the tyres' sideways sliding), and the coupled
    /// trailer's tyres, suspension, brake squeal and support legs are the game's too. Only the door, the cruise-control
    /// chime and the fifth-wheel creak stay synthesised: the game's banks hold no recording of them.
    /// </summary>
    public class EtsTruckAudio : MonoBehaviour
    {
        public EtsTruckController Truck;
        public EtsTruckCamera Camera;

        [Range(0f, 1f)]
        public float Volume = 0.5f;

        /// <summary>Read once, by the first sound made in this client, when the shared clips are built.</summary>
        public int Cylinders = 6;

        /// <summary>From game_data.sii: outside sounds heard at this fraction inside the cab.</summary>
        public float InteriorAttenuation = 0.35f;

        /// <summary>Past this a puppet's engine is silent and its sources stopped; the own truck heard from outside too.</summary>
        public const float PuppetAudibleMetres = 300f;

        /// <summary>A truck heard from outside is at full volume within this, then falls as 1/d (Unity's logarithmic rolloff).</summary>
        public const float NearMetres = 12f;

        // ---------------------------------------------------------------- shared clips (one set per client)

        private const int Rate = 44100;
        private const int Layers = 3;
        private const float Silent = 0.0005f;
        private static readonly float[] LayerTargetRpm = { 650f, 1200f, 1900f };
        private static readonly float[] s_layerRpm = new float[Layers];
        private static AudioClip[] s_engine; // [layer * 2] off load, [layer * 2 + 1] on load
        private static AudioClip s_turbo, s_road, s_wind, s_horn, s_beep;
        private static AudioClip s_tick, s_tock, s_brakeSet, s_brakeRelease, s_gear, s_start, s_stop, s_doorOpen, s_doorClose;
        private static uint s_noise = 0x12345678;
        private static readonly List<EtsTruckAudio> s_all = new List<EtsTruckAudio>();

        // The extras (EtsSynth, worker thread; null until built - every use is null-safe).
        private static AudioClip s_gravel, s_grass, s_engineBrake, s_rattle;
        private static AudioClip s_creak, s_bump, s_impact, s_couple, s_uncouple, s_cruiseOn, s_cruiseOff;
        private static bool s_extrasRequested;

        /// <summary>The engine-brake loop is six cylinders at this many rpm (60 Hz firing).</summary>
        private const float EngineBrakeRpm = 1200f;

        /// <summary>A wheel's compression rising faster than this (m/s) is a bump worth a thud.</summary>
        private const float BumpRate = 0.9f;

        /// <summary>The body's velocity changing by more than this in one frame (m/s) is a knock (the controller's knock report is 1.5).</summary>
        private const float ImpactDeltaV = 1.4f;

        /// <summary>
        /// How loud the outside world is for the local listener: the interior factor (InteriorAttenuation) while this
        /// client sits in a cab with the interior view, else 1. Written every frame by the per-player sound; read by
        /// EtsAmbience and EtsCarAudio so the ambience and traffic are damped in the cab as the truck's outside sounds are.
        /// </summary>
        public static float ListenerOutsideGain { get; private set; } = 1f;

        /// <summary>The diesel note for EtsCarAudio's heavy traffic: the middle engine layer, on load (null before the first truck sound).</summary>
        internal static AudioClip HeavyEngineClip => s_engine != null && s_engine.Length > 3 ? s_engine[3] : null;

        /// <summary>The revs HeavyEngineClip was built at.</summary>
        internal static float HeavyEngineRpm => s_layerRpm[1] > 0f ? s_layerRpm[1] : 1200f;

        // Loop sources, in this order: 2 per engine layer, then the rest.
        private const int LoopTurbo = Layers * 2, LoopRoad = LoopTurbo + 1, LoopWind = LoopTurbo + 2, LoopHorn = LoopTurbo + 3, LoopBeep = LoopTurbo + 4;
        private const int LoopGravel = LoopTurbo + 5, LoopGrass = LoopTurbo + 6, LoopEngineBrake = LoopTurbo + 7, LoopRattle = LoopTurbo + 8;
        private const int LoopCount = LoopRattle + 1;

        private GameObject _holder;
        private AudioSource[] _loops;
        private AudioSource _sfxCab; // cab sounds (relay, gear, doors, parking-brake valve): no interior damping
        private AudioSource _sfxOut; // outside sounds (engine start/stop): damped in the cab like the loops
        private readonly float[] _w = new float[Layers];
        private bool _anyPlaying;
        private bool _threeD;
        private bool _spatialSet;
        private bool _loggedPlay;

        private float _rpmS = EtsTruckController.IdleRpm;
        private float _loadS;
        private float _speedS;
        private float _hornEnv;
        private float _engineGate = 1f;
        private float _startAt = -1000f;
        private bool _stopHeld;
        private EtsTruckController _stopTruck;
        private bool _wasRunning = true;

        private EtsTruckController _blinkTruck;
        private System.Action<bool> _onBlink;

        // The extras' state (per-player sound only).
        private bool _extrasAssigned;
        private EtsTruckController _trackTruck;
        private int _trackPlacements = -1;
        private float _quietUntil;
        private float[] _wheelPrev;
        private int _wheelPrevCount;
        private float _roughS;
        private float _looseT, _natureT, _looseS, _natureS;
        private float _nextSurface, _nextBump, _nextImpact, _nextCreak;
        private float _overrunS;
        private bool _wasAttached;
        private bool _haveVelocity;
        private Vector3 _lastVelocity;
        private readonly bool[] _loggedSurface = new bool[8];
        private int _bumpLogs, _impactLogs;

        // The game's own sounds (EtsGameSound). Null until loaded; every use falls back to the synthesised sound.
        /// <summary>Level of the game's engine, exhaust and turbo loops against the master (their own dB are applied first).</summary>
        public float GameEngineLevel = 0.8f;
        /// <summary>Level of the game's tyre and aero-noise loops: the engine's, so the bank's own balance holds (offline mixes: engine+exhaust RMS 0.26 at half load, asphalt 0.025, aero 0.047 at 20-110 km/h).</summary>
        public float GameRoadLevel = 0.8f;
        private Ets.EtsVehicleSpec _gameSpec;
        private EtsGameTruckSounds _gameSet;
        private bool _gameRequested;
        private string _gameLogged;
        private EtsGameEvent _gEngine, _gExhaust, _gTurbo, _gHorn, _gReverse, _gAero, _gAirBrake, _gAirGear;
        private EtsGameEvent _gBlinkOn, _gBlinkOff, _gParkOn, _gParkOff, _gHookOn, _gHookOff, _gCables;
        private readonly EtsGameEvent[] _gTyres = new EtsGameEvent[3]; // asphalt, gravel, grass
        private EtsGameEventPlayer _pEngine, _pExhaust, _pTurbo, _pHorn, _pReverse, _pAero;
        private readonly EtsGameEventPlayer[] _pTyres = new EtsGameEventPlayer[3];
        private float _turboS;
        private bool _hornWas;
        private int _gameStartInstrument = -2, _gameStopInstrument = -2;
        private static readonly string[] TyreNames = { "tires_asphalt", "tires_gravel", "tires_grass" };
        private static readonly string[] TrailerTyreNames = { "trailer_tires_asphalt", "trailer_tires_gravel", "trailer_tires_grass" };

        // 2026-10-14 "no more synthesised sounds": the air horn, suspension and cab noise, brake squeal, rain on the cab,
        // skids (the tyre events' skid_speed), crashes (/sound/collisions) and the trailer's tyres, suspension, brake
        // squeal and support legs - all the game's (EtsGameTruckSounds.Defaults names their soundrefs).
        private EtsGameEvent _gAirHorn, _gSusp, _gCabNoise, _gSqueal, _gRainCab, _gCrashHard, _gCrashMetal, _gCrashSoft;
        private EtsGameEvent _gTrSusp, _gTrSqueal, _gBraceUp, _gBraceDown;
        private readonly EtsGameEvent[] _gTrTyres = new EtsGameEvent[3];
        private EtsGameEventPlayer _pAirHorn, _pSqueal, _pRainCab, _pTrSqueal;
        private readonly EtsGameEventPlayer[] _pTrTyres = new EtsGameEventPlayer[3];
        private GameObject _trailerHolder;
        private AudioSource _sfxTrailer;
        private bool _airHornWas;
        private float _airHornEnv, _skidT, _skidS;
        private bool _loggedExtrasGame;

        /// <summary>
        /// The per-player truck sound (PLAN.md §3.1, §3.2): the host's one EtsTruckAudio, added if missing.
        /// EtsNet.Awake calls it only with EtsFeatures.NewFlow, so today's scene truck keeps the one
        /// EtsModelViewer.SetUpDriving adds and binds itself.
        /// </summary>
        public static EtsTruckAudio Ensure(GameObject host)
        {
            if (host == null)
            {
                return null;
            }

            EtsTruckAudio audio = host.GetComponent<EtsTruckAudio>();
            return audio != null ? audio : host.AddComponent<EtsTruckAudio>();
        }

        /// <summary>
        /// The sound that plays <paramref name="truck"/>, for the controller's SFX calls (PlayAirBrake, PlayGearChange,
        /// PlayEngineStart/Stop): the per-player sound when it is bound to that truck, else that truck's puppet sound,
        /// else null (always safe to call).
        /// </summary>
        public static EtsTruckAudio For(EtsTruckController truck)
        {
            if (truck == null)
            {
                return null;
            }

            EtsTruckAudio fallback = null;

            for (int i = 0; i < s_all.Count; i++)
            {
                EtsTruckAudio a = s_all[i];

                if (a == null || !ReferenceEquals(a.Truck, truck))
                {
                    continue;
                }

                if (a._puppet == null)
                {
                    return a;
                }

                fallback = a;
            }

            return fallback;
        }

        private bool _bound;

        /// <summary>The puppet this sound belongs to (ForPuppet), or null for the per-player sound on 'Ets Net'.</summary>
        private EtsTruckRig _puppet;

        /// <summary>
        /// A remote player's truck heard from where the local player is (C3, [net §2.6] "no audio beyond the engine loop"):
        /// its own sound on a child of the puppet, fixed to that truck (it never rebinds on the local events), 3D with the
        /// fade of any truck nobody here drives. Its sources are stopped beyond <see cref="PuppetAudibleMetres"/>, while
        /// the engine is off (EtsTruckController.EngineRunning), and while the local player sits in that truck, when the
        /// per-player sound on 'Ets Net' plays it (it binds to EtsLocalTruck.Occupied) with the cab's damping. The horn and
        /// the keys are the driver's client's, never this one's (Truck.InputEnabled is false on a puppet).
        /// </summary>
        public static EtsTruckAudio ForPuppet(EtsTruckRig rig)
        {
            if (rig == null || rig.Controller == null)
            {
                return null;
            }

            // Made inactive and filled in before Awake/OnEnable can run, so it never subscribes to the local truck's events.
            var holder = new GameObject("engine sound");
            holder.SetActive(false);
            holder.transform.SetParent(rig.transform, false);
            EtsTruckAudio audio = holder.AddComponent<EtsTruckAudio>();
            audio._puppet = rig;
            audio.Truck = rig.Controller;
            holder.SetActive(true);
            return audio;
        }

        private void Awake()
        {
            s_all.Add(this);
            _onBlink = OnIndicatorBlink;
            BuildClips(Cylinders);

            _holder = new GameObject("truck sound");
            _holder.transform.SetParent(transform, false);
            _loops = new AudioSource[LoopCount];

            for (int i = 0; i < Layers * 2; i++)
            {
                _loops[i] = NewSource(s_engine[i], true);
            }

            _loops[LoopTurbo] = NewSource(s_turbo, true);
            _loops[LoopRoad] = NewSource(s_road, true);
            _loops[LoopWind] = NewSource(s_wind, true);
            _loops[LoopHorn] = NewSource(s_horn, true);
            _loops[LoopBeep] = NewSource(s_beep, true);
            _loops[LoopGravel] = NewSource(s_gravel, true);
            _loops[LoopGrass] = NewSource(s_grass, true);
            _loops[LoopEngineBrake] = NewSource(s_engineBrake, true);
            _loops[LoopRattle] = NewSource(s_rattle, true);
            _sfxCab = NewSource(null, false);
            _sfxOut = NewSource(null, false);
            _sfxCab.volume = Mathf.Clamp01(Volume * 2f);
            _sfxOut.volume = Mathf.Clamp01(Volume * 2f);

            // The per-player sound brings the rest of the world's sound with it (a puppet's never does).
            if (_puppet == null)
            {
                _trailerHolder = new GameObject("trailer sound");
                _trailerHolder.transform.SetParent(transform, false);
                _sfxTrailer = NewTrailerSource(false);
                RequestExtraClips();
                EtsAmbience.Ensure(gameObject);
                EtsCarAudio.Ensure(gameObject);
            }
        }

        private AudioSource NewSource(AudioClip clip, bool loop)
        {
            AudioSource s = _holder.AddComponent<AudioSource>();
            s.playOnAwake = false;
            s.clip = clip;
            s.loop = loop;
            s.volume = 0f;
            s.spatialBlend = 0f;
            s.rolloffMode = AudioRolloffMode.Logarithmic;
            s.minDistance = NearMetres;
            s.maxDistance = PuppetAudibleMetres;
            s.dopplerLevel = 0f;
            s.priority = _puppet == null ? 64 : 160; // the player's own truck wins voices over remote ones
            return s;
        }

        /// <summary>A source on the trailer's own holder (it follows the coupled trailer), set up like the truck's.</summary>
        private AudioSource NewTrailerSource(bool loop)
        {
            AudioSource s = _trailerHolder.AddComponent<AudioSource>();
            s.playOnAwake = false;
            s.loop = loop;
            s.volume = 0f;
            s.spatialBlend = _threeD ? 1f : 0f;
            s.rolloffMode = AudioRolloffMode.Logarithmic;
            s.minDistance = NearMetres;
            s.maxDistance = PuppetAudibleMetres;
            s.dopplerLevel = 0f;
            s.priority = 70;
            return s;
        }

        /// <summary>
        /// In the new flow the sound follows the truck the local player sits in (EtsLocalTruck.Occupied,
        /// own or a puppet), else their own truck (Own): a player who walks away from the parked truck
        /// still hears its idle, fading with distance (Update). The host's truck camera gives the cab/outside
        /// switch. Without the flag nothing subscribes and the viewer's wiring stays the only writer of Truck
        /// and Camera. A puppet's own sound (ForPuppet) never subscribes.
        /// </summary>
        private void OnEnable()
        {
            if (!EtsFeatures.NewFlow || _bound || _puppet != null)
            {
                return;
            }

            _bound = true;
            EtsLocalTruck.OccupiedChanged += Bind;
            EtsLocalTruck.OwnChanged += Bind;
            Bind(null);
        }

        private void OnDisable()
        {
            SilenceLoops();
            UnbindBlink();

            if (_puppet == null)
            {
                ListenerOutsideGain = 1f;
            }

            if (!_bound)
            {
                return;
            }

            _bound = false;
            EtsLocalTruck.OccupiedChanged -= Bind;
            EtsLocalTruck.OwnChanged -= Bind;
        }

        private void OnDestroy()
        {
            UnbindBlink();
            s_all.Remove(this);

            if (_holder != null)
            {
                Destroy(_holder);
            }

            if (_trailerHolder != null)
            {
                Destroy(_trailerHolder);
            }
        }

        /// <summary>Either event: re-read both, the occupied truck winning.</summary>
        private void Bind(EtsTruckRig changed)
        {
            EtsTruckRig rig = EtsLocalTruck.Occupied != null ? EtsLocalTruck.Occupied : EtsLocalTruck.Own;
            Truck = rig != null ? rig.Controller : null;
            Camera = GetComponent<EtsTruckCamera>();
            RebindBlink();
        }

        // ---------------------------------------------------------------- indicator relay (shared contract with EtsTruckController)

        private void RebindBlink()
        {
            if (ReferenceEquals(_blinkTruck, Truck))
            {
                return;
            }

            UnbindBlink();
            _blinkTruck = Truck;

            if (!ReferenceEquals(_blinkTruck, null))
            {
                _blinkTruck.IndicatorBlink += _onBlink;
            }
        }

        private void UnbindBlink()
        {
            if (!ReferenceEquals(_blinkTruck, null) && _onBlink != null)
            {
                _blinkTruck.IndicatorBlink -= _onBlink;
            }

            _blinkTruck = null;
        }

        /// <summary>
        /// Every lamp edge: the relay's tick (lamp on) or tock (lamp off); faint when heard from outside. Only the
        /// per-player sound ticks: a puppet's has no audio beyond its loops, and when the local player sits in that
        /// puppet the per-player sound (bound to it) already ticks.
        /// </summary>
        private void OnIndicatorBlink(bool lit)
        {
            if (!isActiveAndEnabled || _puppet != null)
            {
                return;
            }

            float level = _threeD ? 0.3f : 1f;
            EtsGameEvent relay = lit ? _gBlinkOn : _gBlinkOff;
            if (relay != null && relay.PlayOnce(_sfxCab, 0.8f * level)) return;
            OneShot(_sfxCab, lit ? s_tick : s_tock, (lit ? 0.45f : 0.36f) * level);
        }

        // ---------------------------------------------------------------- SFX hooks

        /// <summary>A cab door: latch click and thunk opening, a heavier thud closing.</summary>
        public void PlayDoor(bool open)
        {
            OneShot(_sfxCab, open ? s_doorOpen : s_doorClose, open ? 0.6f : 0.75f);
        }

        /// <summary>The parking-brake valve: a short sharp hiss setting it, a longer one releasing it.</summary>
        public void PlayAirBrake(bool set)
        {
            // The game: the lever's click (interior stick_park_brake / _off) and, setting it, the air brake's release.
            EtsGameEvent lever = set ? _gParkOn : _gParkOff;
            bool game = lever != null && lever.PlayOnce(_sfxCab, 0.7f);
            if (set && _gAirBrake != null) game |= _gAirBrake.PlayOnce(_sfxOut, 0.8f);
            if (game) return;
            OneShot(_sfxCab, set ? s_brakeSet : s_brakeRelease, set ? 0.5f : 0.4f);
        }

        /// <summary>
        /// The gearbox's clunk: a soft thunk under the engine. At 0.45 it was "VERY loud" in the headset (the user,
        /// 2026-09-23): the clip is normalised to full scale and the cab source has no interior damping, while the
        /// engine inside sits at about a third of full scale - so it now plays at a fifth of the engine's level.
        /// </summary>
        public void PlayGearChange()
        {
            // The game's gearbox sound is the air shift (transmission sound.sui "air_gear"), heard from outside.
            if (_gAirGear != null && _gAirGear.PlayOnce(_sfxOut, 0.5f)) return;
            OneShot(_sfxCab, s_gear, 0.07f);
        }

        /// <summary>Starter crank and catch; the engine loops fade in under its tail.</summary>
        public void PlayEngineStart()
        {
            _stopHeld = false;
            _startAt = Time.time;
            if (_pEngine != null && _gEngine.PlayInstrument(_sfxOut, GameInstrument(ref _gameStartInstrument, "start"), GameEngineLevel)) return;
            OneShot(_sfxOut, s_start, 0.85f);
        }

        /// <summary>Run-down and shudder; the engine loops stop at once and stay off until EngineRunning rises or PlayEngineStart.</summary>
        public void PlayEngineStop()
        {
            _stopHeld = true;
            _stopTruck = Truck;
            if (_pEngine != null && _gEngine.PlayInstrument(_sfxOut, GameInstrument(ref _gameStopInstrument, "stop"), GameEngineLevel)) return;
            OneShot(_sfxOut, s_stop, 0.8f);
        }

        /// <summary>The engine event's timeline sample named so ("start"/"stop", never "start_bad"), found once.</summary>
        private int GameInstrument(ref int cached, string name)
        {
            if (cached == -2 && _gEngine != null && _gEngine.Ready)
            {
                cached = -1;
                for (int i = 0; i < _gEngine.Event.Instruments.Count && cached < 0; i++)
                {
                    if (!_gEngine.Event.Instruments[i].OnTimeline) continue;
                    string[] names = _gEngine.SampleNames[i];
                    for (int k = 0; k < names.Length; k++)
                        if (names[k] != null && names[k].IndexOf(name, System.StringComparison.OrdinalIgnoreCase) >= 0 && names[k].IndexOf("bad", System.StringComparison.OrdinalIgnoreCase) < 0) { cached = i; break; }
                }
            }
            return cached;
        }

        /// <summary>The cruise-control chime: two soft dashboard beeps, rising when it engages, falling when it lets go.</summary>
        public void PlayCruise(bool on)
        {
            OneShot(_sfxCab, on ? s_cruiseOn : s_cruiseOff, 0.08f);
        }

        /// <summary>
        /// This client's truck or trailer is about to touch an AI car (EtsTraffic, the physics step before the contact, as
        /// the car turns into a body of its own weight): the knock, at the strength of the closing speed (m/s). The velocity
        /// knock misses a glancing blow now that the car gives way; the shared 0.3 s gap keeps the two from doubling.
        /// </summary>
        public static void PlayCarHit(float closing)
        {
            for (int i = 0; i < s_all.Count; i++)
            {
                EtsTruckAudio a = s_all[i];

                if (a == null || a._puppet != null || a._loops == null)
                {
                    continue;
                }

                float now = Time.time;

                if (now >= a._nextImpact && now >= a._quietUntil)
                {
                    a._nextImpact = now + 0.3f;
                    if (!a.PlayCrash(closing)) OneShot(a._sfxCab, s_impact, Mathf.Clamp(0.08f + 0.015f * closing, 0.08f, 0.3f));
                }

                return;
            }
        }

        /// <summary>
        /// The game's crash for a knock of <paramref name="metresPerSecond"/> (/sound/collisions: crash_soft under 3 m/s,
        /// crash_metal under 7, crash_hard above - the game's three impact sets; the split by speed is this world's).
        /// </summary>
        private bool PlayCrash(float metresPerSecond)
        {
            EtsGameEvent crash = metresPerSecond < 3f ? _gCrashSoft : metresPerSecond < 7f ? _gCrashMetal : _gCrashHard;
            if (crash == null || !crash.Ready) crash = _gCrashMetal != null && _gCrashMetal.Ready ? _gCrashMetal : crash;
            return crash != null && crash.PlayOnce(_sfxCab, Mathf.Clamp(0.35f + 0.06f * metresPerSecond, 0.35f, 1f));
        }

        private static void OneShot(AudioSource source, AudioClip clip, float level)
        {
            if (source != null && clip != null && source.isActiveAndEnabled)
            {
                source.PlayOneShot(clip, level);
            }
        }

        // ---------------------------------------------------------------- per frame (main thread, no allocation)

        private void Update()
        {
            if (_loops == null)
            {
                return;
            }

            EtsSynth.Pump();
            EtsGameSound.Pump();
            EtsGameTruckSounds.Pump();
            AssignExtraClips();
            RebindBlink();

            if (_puppet != null && !PuppetAudible())
            {
                SilenceLoops();
                return;
            }

            EtsTruckController truck = Truck;

            if (truck == null || !truck.Placed)
            {
                if (_puppet == null)
                {
                    ListenerOutsideGain = 1f;
                }

                SilenceLoops();
                return;
            }

            float dt = Mathf.Min(Time.deltaTime, 0.1f);
            Transform body = truck.transform;
            _holder.transform.position = body.position;

            // In the cab (flat, damped by the interior volume) when this client sits in that truck; otherwise 3D from
            // the truck, as for a puppet or this player's own truck left parked while they walk.
            EtsTruckRig occupied = EtsLocalTruck.Occupied;
            bool inCab = _puppet == null && ((occupied != null && ReferenceEquals(occupied.Controller, truck)) || (!truck.Puppet && truck.Driven));
            SetSpatial(!inCab);

            float distanceGain = 1f;

            if (!inCab)
            {
                Transform ear = EtsTruckRig.LocalView(); // once a frame for every truck (EtsTruckRig.LocalView)

                if (ear != null && (ear.position - body.position).sqrMagnitude > PuppetAudibleMetres * PuppetAudibleMetres)
                {
                    distanceGain = 0f;
                }
            }

            bool interior = inCab && Camera != null && Camera.Active && Camera.View == EtsTruckCamera.Mode.Interior;
            float master = Volume * 2f * distanceGain;
            float outside = master * (interior ? InteriorAttenuation : 1f);
            _sfxCab.volume = Mathf.Clamp01(master);
            _sfxOut.volume = Mathf.Clamp01(outside);

            if (_puppet == null)
            {
                ListenerOutsideGain = interior ? InteriorAttenuation : 1f;
                if (_sfxTrailer != null) _sfxTrailer.volume = Mathf.Clamp01(outside);
                EtsTrailer coupled = truck.Trailer;
                if (_trailerHolder != null) _trailerHolder.transform.position = coupled != null && coupled.Attached ? coupled.transform.position : body.position;
            }

            // Smoothed controls, as the old audio-thread filter did (about 40, 70 and 100 ms).
            _rpmS += (truck.Rpm - _rpmS) * (1f - Mathf.Exp(-dt / 0.04f));
            _loadS += (Mathf.Clamp01(truck.Throttle) - _loadS) * (1f - Mathf.Exp(-dt / 0.07f));
            _speedS += (truck.SpeedKmh - _speedS) * (1f - Mathf.Exp(-dt / 0.1f));

            // The extras' inputs: what the tyres roll on, how rough the road is, bumps (the local, driven truck only).
            bool local = _puppet == null && !truck.Puppet;
            TrackTruck(truck);

            if (local)
            {
                SampleWheels(truck, dt);
            }
            else
            {
                _looseT = 0f;
                _natureT = 0f;
                _skidT = 0f;
                _roughS = 0f;
            }

            _looseS += (_looseT - _looseS) * (1f - Mathf.Exp(-dt / 0.25f));
            _natureS += (_natureT - _natureS) * (1f - Mathf.Exp(-dt / 0.25f));

            bool running = truck.EngineRunning;

            if (running && !_wasRunning)
            {
                _stopHeld = false;
            }

            _wasRunning = running;
            bool held = _stopHeld && ReferenceEquals(_stopTruck, truck);
            float gate = running && !held ? Mathf.Clamp01((Time.time - _startAt - 1.1f) / 0.5f) : 0f;
            _engineGate += (gate - _engineGate) * (1f - Mathf.Exp(-dt / 0.05f));

            float rpm = Mathf.Max(_rpmS, 1f);
            GameUpdate(truck);

            if (_pEngine != null)
            {
                // The game's engine (EtsGameEventPlayer): its parameters as FMOD would get them - rpm, load (throttle),
                // brake (the engine brake, UpdateExtras), turbo (boost building with load above 800 rpm, slower to fall).
                float boost = _loadS * Mathf.Clamp01((rpm - 800f) / 900f);
                _turboS += (boost - _turboS) * (1f - Mathf.Exp(-dt / (boost > _turboS ? 0.8f : 0.4f)));
                float level = GameEngineLevel * _engineGate * outside;
                _gEngine.Set("rpm", rpm);
                _gEngine.Set("load", _loadS);
                _gEngine.Set("brake", _overrunS);
                _pEngine.Drive(level);
                if (_pExhaust != null) { _gExhaust.Set("rpm", rpm); _gExhaust.Set("load", _loadS); _pExhaust.Drive(level); }
                if (_pTurbo != null) { _gTurbo.Set("turbo", _turboS); _pTurbo.Drive(level); }
                if (_pEngine.Playing > 0) Started();
                for (int i = 0; i < Layers; i++) DrivePair(i * 2, false, 0f, 0f, 1f);
                Drive(LoopTurbo, 0f, 1f, false);
            }
            else
            {
                DriveSynthEngine(rpm, outside);
            }

            // Tyres and wind, rising with speed; the asphalt roar gives way to the loose-ground loops (UpdateExtras).
            float road = Mathf.Clamp01(_speedS / 90f);
            float hard = Mathf.Clamp01(1f - 0.85f * (_looseS + _natureS));

            if (_pTyres[0] != null)
            {
                // The game's tyres: asphalt, gravel and grass by what the wheels stand on, each by speed (km/h).
                float moving = _speedS > 0.5f ? 1f : 0f;
                float[] weight = s_tyreWeight;
                weight[0] = hard; weight[1] = _looseS; weight[2] = _natureS;
                // skid_speed: how fast the tyres slide sideways (m/s, SampleWheels) - the bank's skid loops come in past 1 m/s.
                _skidS += (_skidT - _skidS) * (1f - Mathf.Exp(-dt / 0.08f));
                for (int i = 0; i < _pTyres.Length; i++)
                {
                    if (_pTyres[i] == null) continue;
                    _gTyres[i].Set("speed", _speedS);
                    _gTyres[i].Set("skid_speed", _skidS);
                    _pTyres[i].Drive(GameRoadLevel * weight[i] * moving * outside);
                }
                Drive(LoopRoad, 0f, 1f, false);

                // The coupled trailer's own tyres (/sound/trailer/tires), on the same ground, at the trailer.
                EtsTrailer tr = truck.Trailer;
                bool towing = tr != null && tr.Attached;
                for (int i = 0; i < _pTrTyres.Length; i++)
                {
                    if (_pTrTyres[i] == null) continue;
                    _gTrTyres[i].Set("speed", _speedS);
                    _gTrTyres[i].Set("skid_speed", 0f);
                    _pTrTyres[i].Drive(towing ? GameRoadLevel * weight[i] * moving * outside : 0f);
                }
            }
            else
            {
                Drive(LoopRoad, (0.3f * road * road + 0.05f * road) * outside * hard, 0.8f + 0.35f * road, true);
            }

            float wind = Mathf.Clamp01(_speedS / 110f);
            if (_pAero != null)
            {
                _gAero.Set("aero_speed", _speedS);
                _pAero.Drive(GameRoadLevel * outside);
                Drive(LoopWind, 0f, 1f, false);
            }
            else
            {
                Drive(LoopWind, 0.22f * wind * wind * outside, 0.85f + 0.3f * wind, true);
            }

            // The game's two horns, as its keys have them: H the truck's own horn (its cabin sound.sui "horn" - on most
            // trucks the electric city horn), N the air horn ("air_horn": /sound/truck/default/air_horn_default.soundref,
            // horn_air/default - start, loop and stop samples - the one a truck without an air-horn accessory blows). The
            // headset's stick click is the air horn: the big truck horn players reach for.
            bool airHorn = truck.Driven && truck.InputEnabled && (Input.GetKey(KeyCode.N) || EtsVr.Horn) && _pAirHorn != null;
            _airHornEnv += ((airHorn ? 1f : 0f) - _airHornEnv) * (1f - Mathf.Exp(-dt / (airHorn ? 0.01f : 0.03f)));
            if (_pAirHorn != null)
            {
                if (airHorn != _airHornWas)
                {
                    _airHornWas = airHorn;
                    _gAirHorn.PlayInstrument(_sfxOut, _gAirHorn.FindInstrument(airHorn ? "start" : "stop"), 1f);
                }
                _pAirHorn.TimelineActive = _airHornEnv > 0.002f;
                _pAirHorn.Drive(_airHornEnv * outside);
            }

            // Horn: two tones, a quick attack and a short release (the game's horn loop when loaded, on the same envelope).
            bool horn = truck.Driven && truck.InputEnabled && (Input.GetKey(KeyCode.H) || (EtsVr.Horn && _pAirHorn == null));
            _hornEnv += ((horn ? 1f : 0f) - _hornEnv) * (1f - Mathf.Exp(-dt / (horn ? 0.02f : 0.06f)));
            if (_pHorn != null)
            {
                // A horn built as start / loop / stop samples (Scania R 2016's) plays its start and stop on the press edges.
                if (horn != _hornWas)
                {
                    _hornWas = horn;
                    _gHorn.PlayInstrument(_sfxOut, _gHorn.FindInstrument(horn ? "start" : "stop"), 0.9f);
                }

                _pHorn.TimelineActive = _hornEnv > 0.002f;
                _pHorn.Drive(0.9f * _hornEnv * outside);
                Drive(LoopHorn, 0f, 1f, false);
            }
            else
            {
                Drive(LoopHorn, 0.9f * _hornEnv * outside, 1f, false);
            }

            // Reversing alarm (the game's, looping on its timeline, when loaded).
            if (_pReverse != null)
            {
                _pReverse.TimelineActive = truck.Gear < 0;
                _pReverse.Drive(0.5f * outside);
                Drive(LoopBeep, 0f, 1f, false);
            }
            else
            {
                Drive(LoopBeep, truck.Gear < 0 ? 0.5f * outside : 0f, 1f, false);
            }

            if (_puppet == null)
            {
                UpdateExtras(truck, dt, rpm, outside, local, running);
            }
        }

        private static readonly float[] s_tyreWeight = new float[3];

        /// <summary>The synthesised engine (the fallback): three layers, each in an off-load and an on-load version.</summary>
        private void DriveSynthEngine(float rpm, float outside)
        {
            // Engine: crossfade the two layers either side of the revs (equal power), each pitched to the revs,
            // and inside a layer the off-load and on-load versions by throttle.
            float engineLevel = (0.45f + 0.35f * _loadS) * (0.7f + 0.3f * Mathf.Clamp01(rpm / 2200f)) * _engineGate * outside;

            for (int i = 0; i < Layers; i++)
            {
                _w[i] = 0f;
            }

            if (rpm <= s_layerRpm[0])
            {
                _w[0] = 1f;
            }
            else if (rpm >= s_layerRpm[Layers - 1])
            {
                _w[Layers - 1] = 1f;
            }
            else
            {
                for (int i = 0; i < Layers - 1; i++)
                {
                    if (rpm < s_layerRpm[i + 1])
                    {
                        float x = (rpm - s_layerRpm[i]) / (s_layerRpm[i + 1] - s_layerRpm[i]);
                        _w[i] = Mathf.Cos(x * Mathf.PI * 0.5f);
                        _w[i + 1] = Mathf.Sin(x * Mathf.PI * 0.5f);
                        break;
                    }
                }
            }

            float offLoad = Mathf.Cos(_loadS * Mathf.PI * 0.5f);
            float onLoad = Mathf.Sin(_loadS * Mathf.PI * 0.5f);

            for (int i = 0; i < Layers; i++)
            {
                float level = engineLevel * _w[i];
                DrivePair(i * 2, level > Silent, level * offLoad, level * onLoad, Mathf.Clamp(rpm / s_layerRpm[i], 0.25f, 3f));
            }

            // Turbo whistle, with load and revs (900 Hz + 1.6 Hz per rpm; the clip is 2 kHz).
            Drive(LoopTurbo, 0.12f * _loadS * Mathf.Clamp01(rpm / 2200f) * _engineGate * outside, Mathf.Clamp((900f + rpm * 1.6f) / 2000f, 0.25f, 3f), true);
        }

        // ---------------------------------------------------------------- the game's own sounds (EtsGameSound)

        /// <summary>
        /// Once a frame: follow the truck's definitions to its game events (EtsGameTruckSounds), ask EtsGameSound for them,
        /// and make a player for each looping event as it becomes ready. A different truck (another spec) starts afresh.
        /// A puppet's sound only takes the engine, exhaust and turbo.
        /// </summary>
        private void GameUpdate(EtsTruckController truck)
        {
            if (!EtsGameSound.Enabled)
            {
                return;
            }

            Ets.EtsVehicleSpec spec = truck.Spec;

            if (!ReferenceEquals(spec, _gameSpec))
            {
                DropGame();
                _gameSpec = spec;
                _gameSet = spec != null ? EtsGameTruckSounds.For(spec) : null;

                if (spec == null)
                {
                    LogGame("synthesised (the truck has no definition to read its sounds from)");
                }
                else if (_gameSet == null)
                {
                    LogGame("synthesised (the game's archives are not mounted yet)");
                    _gameSpec = null; // try again next frame
                }
            }

            if (_gameSet == null || !_gameSet.Resolved)
            {
                return;
            }

            if (!_gameRequested)
            {
                _gameRequested = true;
                const string engineFixed = "Event Cone Angle=0,play=1";
                _gEngine = Request("engine", engineFixed);
                _gExhaust = Request("exhaust", engineFixed);
                _gTurbo = Request("turbo", engineFixed);

                if (_puppet == null)
                {
                    _gHorn = Request("horn", "Event Cone Angle=0,play=1");
                    _gReverse = Request("reverse", null);
                    _gAero = Request("aero_noise", "aero_wnd=0", NoiseLoopSeconds);
                    _gAirBrake = Request("air_brake", "air_pressure=1,Event Cone Angle=0");
                    _gAirGear = Request("air_gear", "Event Cone Angle=0");
                    _gBlinkOn = Request("blinker_on", null);
                    _gBlinkOff = Request("blinker_off", null);
                    _gParkOn = Request("stick_park_brake", null);
                    _gParkOff = Request("stick_park_brake_off", null);
                    _gHookOn = Request("hook_attach", null);
                    _gHookOff = Request("hook_detach", null);
                    _gCables = Request("trailer_cables", null);

                    // The tyres with their skid loops (skid_speed is driven now, SampleWheels).
                    for (int i = 0; i < TyreNames.Length; i++)
                    {
                        _gTyres[i] = Request(TyreNames[i], null, NoiseLoopSeconds);
                        _gTrTyres[i] = Request(TrailerTyreNames[i], "skid_speed=0", NoiseLoopSeconds);
                    }

                    _gAirHorn = Request("air_horn", "Event Cone Angle=0,play=1");
                    _gSusp = Request("suspension", null);
                    _gCabNoise = Request("noise", null);
                    _gSqueal = Request("brake_squeal", null);
                    _gRainCab = Request("rain_noise", null, NoiseLoopSeconds);
                    _gCrashHard = Request("crash_hard", null);
                    _gCrashMetal = Request("crash_metal", null);
                    _gCrashSoft = Request("crash_soft", null);
                    _gTrSusp = Request("trailer_suspension", null);
                    _gTrSqueal = Request("trailer_brake_squeal", null);
                    _gBraceUp = Request("trailer_brace_up", null);
                    _gBraceDown = Request("trailer_brace_down", null);
                }

                if (_gEngine == null)
                {
                    LogGame("synthesised (" + (_gameSet.Failure ?? "no engine sound in the truck's definitions") + ")");
                }
            }

            if (_pEngine == null && _gEngine != null)
            {
                if (_gEngine.Ready)
                {
                    _pEngine = NewPlayer(_gEngine);
                    _gameStartInstrument = _gameStopInstrument = -2;
                    LogGame("game samples from " + _gEngine.BankPath + " (engine " + Loops(_pEngine) + " loops over " + Range(_gEngine, "rpm")
                        + (_gExhaust != null && _gExhaust.Ready ? ", exhaust" : "") + (_gTurbo != null && _gTurbo.Ready ? ", turbo" : "")
                        + ", start " + (GameInstrument(ref _gameStartInstrument, "start") >= 0 ? "yes" : "no") + ")");
                }
                else if (_gEngine.Failed)
                {
                    LogGame("synthesised (" + _gEngine.Failure + ")");
                    _gEngine = null;
                }
            }

            if (_pEngine != null)
            {
                MakePlayer(ref _pExhaust, _gExhaust);
                MakePlayer(ref _pTurbo, _gTurbo);
            }

            if (_puppet == null)
            {
                MakePlayer(ref _pHorn, _gHorn);
                MakePlayer(ref _pReverse, _gReverse);
                MakePlayer(ref _pAero, _gAero);
                MakePlayer(ref _pAirHorn, _gAirHorn);
                MakePlayer(ref _pSqueal, _gSqueal);
                MakePlayer(ref _pRainCab, _gRainCab);
                MakeTrailerPlayer(ref _pTrSqueal, _gTrSqueal);
                for (int i = 0; i < _pTrTyres.Length; i++) MakeTrailerPlayer(ref _pTrTyres[i], _gTrTyres[i]);

                // The tyres switch over together (once all three surfaces are loaded or failed), so the surfaces never
                // mix game and synthesised loops; a surface that failed is silent rather than synthesised then.
                if (_pTyres[0] == null && _gTyres[0] != null && _gTyres[0].Ready && Settled(_gTyres[1]) && Settled(_gTyres[2]))
                {
                    for (int i = 0; i < _pTyres.Length; i++)
                    {
                        MakePlayer(ref _pTyres[i], _gTyres[i]);
                    }

                    Debug.Log("[SomniumEuroTruck] Truck sound: tyres from the game (" + (_pTyres[0] != null ? "asphalt " : "") + (_pTyres[1] != null ? "gravel " : "") + (_pTyres[2] != null ? "grass" : "") + "), aero noise " + (_pAero != null ? "from the game" : "synthesised") + ".");
                }
            }
        }

        private static bool Settled(EtsGameEvent ev) => ev == null || ev.Ready || ev.Failed;

        /// <summary>The tyre and aero-noise beds (6-9 s each in the bank) are cut to this and crossfaded into a loop: 104 s of PCM become 54.</summary>
        private const float NoiseLoopSeconds = 6f;

        private EtsGameEvent Request(string name, string fixedParams, float maxSeconds = 0f)
        {
            return _gameSet.Sources.TryGetValue(name, out string source) ? EtsGameSound.Get(source, maxSeconds, false, fixedParams) : null;
        }

        private void MakePlayer(ref EtsGameEventPlayer player, EtsGameEvent ev)
        {
            if (player == null && ev != null && ev.Ready)
            {
                player = NewPlayer(ev);
            }
        }

        private EtsGameEventPlayer NewPlayer(EtsGameEvent ev)
        {
            var p = new EtsGameEventPlayer(ev, () => NewSource(null, true));
            p.SetSpatialBlend(_threeD ? 1f : 0f);
            return p;
        }

        private void MakeTrailerPlayer(ref EtsGameEventPlayer player, EtsGameEvent ev)
        {
            if (player != null || ev == null || !ev.Ready || _trailerHolder == null) return;
            player = new EtsGameEventPlayer(ev, () => NewTrailerSource(true));
            player.SetSpatialBlend(_threeD ? 1f : 0f);
        }

        private static string Has(object o) => o != null ? "yes" : "no";

        private static string Has(EtsGameEvent ev) => ev != null && ev.Ready ? "yes" : ev != null && ev.Failed ? "failed" : "no";

        private static int Loops(EtsGameEventPlayer p)
        {
            int n = 0;
            for (int i = 0; i < p.Sources.Length; i++) if (p.Sources[i] != null) n++;
            return n;
        }

        private static string Range(EtsGameEvent ev, string param)
        {
            int p = ev.Studio.FindParam(param);
            return p >= 0 ? ev.Studio.Params[p].Min.ToString("0") + "-" + ev.Studio.Params[p].Max.ToString("0") + " " + param : param;
        }

        private void LogGame(string what)
        {
            string line = "Truck sound: " + what + " for " + (Truck != null ? Truck.name : "?") + (_puppet != null ? " [puppet]" : "") + ".";
            if (line == _gameLogged) return;
            _gameLogged = line;
            Debug.Log("[SomniumEuroTruck] " + line);
        }

        /// <summary>Forget the game events of the previous truck and remove their sources (the clips stay cached).</summary>
        private void DropGame()
        {
            DestroyPlayer(ref _pEngine);
            DestroyPlayer(ref _pExhaust);
            DestroyPlayer(ref _pTurbo);
            DestroyPlayer(ref _pHorn);
            DestroyPlayer(ref _pReverse);
            DestroyPlayer(ref _pAero);
            for (int i = 0; i < _pTyres.Length; i++) DestroyPlayer(ref _pTyres[i]);
            DestroyPlayer(ref _pAirHorn);
            DestroyPlayer(ref _pSqueal);
            DestroyPlayer(ref _pRainCab);
            DestroyPlayer(ref _pTrSqueal);
            for (int i = 0; i < _pTrTyres.Length; i++) DestroyPlayer(ref _pTrTyres[i]);
            _gEngine = _gExhaust = _gTurbo = _gHorn = _gReverse = _gAero = _gAirBrake = _gAirGear = null;
            _gBlinkOn = _gBlinkOff = _gParkOn = _gParkOff = _gHookOn = _gHookOff = _gCables = null;
            _gAirHorn = _gSusp = _gCabNoise = _gSqueal = _gRainCab = _gCrashHard = _gCrashMetal = _gCrashSoft = null;
            _gTrSusp = _gTrSqueal = _gBraceUp = _gBraceDown = null;
            for (int i = 0; i < _gTyres.Length; i++) _gTyres[i] = null;
            for (int i = 0; i < _gTrTyres.Length; i++) _gTrTyres[i] = null;
            _gameSet = null;
            _gameRequested = false;
        }

        private static void DestroyPlayer(ref EtsGameEventPlayer p)
        {
            if (p == null) return;
            p.Stop();
            p.Destroy();
            p = null;
        }

        private void ForEachGamePlayer(System.Action<EtsGameEventPlayer> act)
        {
            if (_pEngine != null) act(_pEngine);
            if (_pExhaust != null) act(_pExhaust);
            if (_pTurbo != null) act(_pTurbo);
            if (_pHorn != null) act(_pHorn);
            if (_pReverse != null) act(_pReverse);
            if (_pAero != null) act(_pAero);
            for (int i = 0; i < _pTyres.Length; i++) if (_pTyres[i] != null) act(_pTyres[i]);
            if (_pAirHorn != null) act(_pAirHorn);
            if (_pSqueal != null) act(_pSqueal);
            if (_pRainCab != null) act(_pRainCab);
            if (_pTrSqueal != null) act(_pTrSqueal);
            for (int i = 0; i < _pTrTyres.Length; i++) if (_pTrTyres[i] != null) act(_pTrTyres[i]);
        }

        // ---------------------------------------------------------------- the extras (per-player sound, no allocation)

        /// <summary>A different truck, or the same one placed again (spawn, respawn, a job): start the extras' tracking afresh, quietly.</summary>
        private void TrackTruck(EtsTruckController truck)
        {
            if (ReferenceEquals(truck, _trackTruck) && truck.Placements == _trackPlacements)
            {
                return;
            }

            _trackTruck = truck;
            _trackPlacements = truck.Placements;
            _haveVelocity = false;
            _wheelPrevCount = 0;
            _roughS = 0f;
            EtsTrailer trailer = truck.Trailer;
            _wasAttached = trailer != null && trailer.Attached;
            _quietUntil = Time.time + 1.5f;
        }

        /// <summary>
        /// Every frame: each wheel's compression against last frame's - the fastest rise is a bump (a thud), the mean is how
        /// rough the road is (the trailer's rattle). Five times a second: what the grounded tyres stand on.
        /// </summary>
        private void SampleWheels(EtsTruckController truck, float dt)
        {
            IReadOnlyList<EtsTruckController.Wheel> wheels = truck.Wheels;
            int n = wheels != null ? wheels.Count : 0;

            if (n == 0)
            {
                return;
            }

            if (_wheelPrev == null || _wheelPrev.Length < n)
            {
                _wheelPrev = new float[Mathf.Max(n, 8)];
                _wheelPrevCount = 0;
            }

            Rigidbody body = truck.Body;
            bool valid = _wheelPrevCount == n && body != null && !body.isKinematic;
            float step = Mathf.Max(dt, Time.fixedDeltaTime); // compression moves only on physics steps
            float maxRate = 0f, sumRate = 0f;
            float skid = 0f;
            Transform frame = truck.transform;

            for (int i = 0; i < n; i++)
            {
                // A grounded tyre's sideways sliding (its contact point's velocity across the wheel), for skid_speed.
                EtsTruckController.Wheel sw = wheels[i];
                if (sw.Grounded && body != null && !body.isKinematic)
                {
                    Vector3 up = frame.up;
                    Vector3 roll = Quaternion.AngleAxis(sw.SteerDegrees, up) * frame.forward;
                    Vector3 side = Vector3.Cross(sw.Hit.normal, roll);
                    float lateral = Mathf.Abs(Vector3.Dot(body.GetPointVelocity(sw.Hit.point), side.normalized));
                    if (lateral > skid) skid = lateral;
                }

                float c = wheels[i].Compression;

                if (valid)
                {
                    float rate = (c - _wheelPrev[i]) / step;
                    if (rate > maxRate) maxRate = rate;
                    sumRate += rate < 0f ? -rate : rate;
                }

                _wheelPrev[i] = c;
            }

            _wheelPrevCount = n;
            _skidT = skid;
            float now = Time.time;

            if (valid)
            {
                _roughS += (Mathf.Min(sumRate / n, 3f) - _roughS) * (1f - Mathf.Exp(-dt / 0.5f));

                if (maxRate > BumpRate && _speedS > 4f && now >= _nextBump && now >= _quietUntil)
                {
                    _nextBump = now + 0.2f;
                    // The game's suspension_noise (susp_speed = the wheel's compression speed, m/s) outside, the cab's own
                    // rattle (interior/noise) inside, the trailer's suspension (trailer_noises.bank) behind; else the thud.
                    bool game = false;
                    if (_gSusp != null) { _gSusp.Set("susp_speed", maxRate); game = _gSusp.PlayOnce(_sfxOut, 0.8f); }
                    if (_gCabNoise != null && !_threeD && ListenerOutsideGain < 1f) game |= _gCabNoise.PlayOnce(_sfxCab, Mathf.Clamp(0.25f + 0.15f * maxRate, 0.25f, 0.8f));
                    EtsTrailer bumpTrailer = truck.Trailer;
                    if (_gTrSusp != null && bumpTrailer != null && bumpTrailer.Attached) { _gTrSusp.Set("susp_speed", maxRate); _gTrSusp.PlayOnce(_sfxTrailer, 0.8f); }
                    if (!game) OneShot(_sfxCab, s_bump, Mathf.Clamp(0.035f + 0.04f * (maxRate - BumpRate), 0.035f, 0.14f));

                    if (_bumpLogs < 5)
                    {
                        _bumpLogs++;
                        Debug.Log("[SomniumEuroTruck] Truck sound: suspension bump (a wheel compressing at " + maxRate.ToString("0.0") + " m/s, " + _speedS.ToString("0") + " km/h).");
                    }
                }
            }

            if (now < _nextSurface)
            {
                return;
            }

            _nextSurface = now + 0.2f;
            int grounded = 0, loose = 0, green = 0;

            for (int i = 0; i < n; i++)
            {
                EtsTruckController.Wheel w = wheels[i];

                if (!w.Grounded)
                {
                    continue;
                }

                Collider collider = w.Hit.collider;

                if (collider == null)
                {
                    continue;
                }

                grounded++;
                byte kind = EtsGroundSurface.Of(collider);

                if (kind == EtsGroundSurface.Loose) loose++;
                else if (kind == EtsGroundSurface.Nature || kind == EtsGroundSurface.Water) green++;

                if (kind < _loggedSurface.Length && !_loggedSurface[kind])
                {
                    _loggedSurface[kind] = true;
                    Debug.Log("[SomniumEuroTruck] Truck sound: tyres on " + EtsGroundSurface.Names[kind] + " ground for the first time ('" + collider.name + "').");
                }
            }

            if (grounded > 0)
            {
                _looseT = loose / (float)grounded;
                _natureT = green / (float)grounded;
            }
        }

        private void UpdateExtras(EtsTruckController truck, float dt, float rpm, float outside, bool local, bool running)
        {
            float now = Time.time;

            // Brake squeal (noise.bank brake_squeal: brake_int, speed, wheel_wear - silent on unworn wheels, as in the
            // game; wheel_wear is the truck's wheels' condition loss, EtsDamage) and the trailer's (trailer_noises.bank).
            float wear = 0f;
            Ets.EtsDamageState damage = EtsDamage.State;
            if (damage != null) wear = (float)damage.TruckPart(Ets.EtsDamageRules.Wheels);
            float brake = Mathf.Clamp01(truck.Brake);
            if (_pSqueal != null)
            {
                _gSqueal.Set("brake_int", brake);
                _gSqueal.Set("speed", _speedS);
                _gSqueal.Set("wheel_wear", wear);
                _pSqueal.Drive(outside);
            }
            EtsTrailer squealTrailer = truck.Trailer;
            if (_pTrSqueal != null)
            {
                float trailerWear = damage != null ? (float)Mathf.Min(1f, (float)(damage.Trailer[Ets.EtsDamageRules.TrailerWheels] + damage.TrailerUnfixable[Ets.EtsDamageRules.TrailerWheels])) : 0f;
                _gTrSqueal.Set("brake_int", brake);
                _gTrSqueal.Set("speed", _speedS);
                _gTrSqueal.Set("wheel_wear", trailerWear);
                _pTrSqueal.Drive(squealTrailer != null && squealTrailer.Attached ? outside : 0f);
            }

            // Rain on the cab (the truck's "rain_noise": noise.bank rain_noise by rain_int), heard inside; outside, the
            // world's rain (EtsAmbience) is heard instead.
            if (_pRainCab != null)
            {
                _gRainCab.Set("rain_int", EtsDayNight.Rain);
                bool inside = !_threeD && ListenerOutsideGain < 1f;
                _pRainCab.Drive(inside ? _sfxCab.volume : 0f);
            }

            if (!_loggedExtrasGame && (_gSusp != null && _gSusp.Ready))
            {
                _loggedExtrasGame = true;
                Debug.Log("[SomniumEuroTruck] Truck sound: the game's extras - air horn " + Has(_pAirHorn) + ", suspension " + Has(_gSusp) + ", cab noise " + Has(_gCabNoise) + ", brake squeal "
                    + Has(_pSqueal) + ", rain on the cab " + Has(_pRainCab) + ", crashes " + Has(_gCrashMetal) + ", trailer tyres " + Has(_pTrTyres[0]) + ", trailer suspension " + Has(_gTrSusp)
                    + ", trailer squeal " + Has(_pTrSqueal) + ", support legs " + Has(_gBraceDown) + " (door, cruise chime and fifth-wheel creak stay synthesised: the game has no recording of them).");
            }

            // Loose ground: gravel/dirt crunch and a soft grass rumble, rising with speed.
            float mv = Mathf.Clamp01(_speedS / 60f);
            float moving = _speedS > 1f ? 1f : 0f;
            float sq = Mathf.Sqrt(mv);
            bool gameTyres = _pTyres[0] != null; // the game's gravel and grass loops play in Update instead
            Drive(LoopGravel, gameTyres ? 0f : _looseS * (0.5f * mv + 0.12f * sq) * moving * outside, 0.75f + 0.45f * mv, true);
            Drive(LoopGrass, gameTyres ? 0f : _natureS * (0.2f * mv + 0.05f * sq) * moving * outside, 0.8f + 0.3f * mv, true);

            // Engine brake: off the throttle in gear above 12 km/h (the controller brakes the driveline then), with the revs.
            bool overrun = running && truck.Gear > 0 && truck.Throttle < 0.05f && truck.SpeedKmh > 12f && rpm > 900f;
            _overrunS += ((overrun ? 1f : 0f) - _overrunS) * (1f - Mathf.Exp(-dt / (overrun ? 0.25f : 0.08f)));
            // (The game's engine has its own engine-brake track, driven by its "brake" parameter from _overrunS in Update.)
            Drive(LoopEngineBrake, _pEngine != null ? 0f : 0.14f * _overrunS * Mathf.Clamp01((rpm - 850f) / 700f) * _engineGate * outside, Mathf.Clamp(rpm / EngineBrakeRpm, 0.4f, 2.5f), true);

            // The trailer: the coupling on each Attached edge, a rattle over rough road, a creak when it swings.
            EtsTrailer trailer = truck.Trailer;
            bool attached = trailer != null && trailer.Attached;

            if (attached != _wasAttached)
            {
                _wasAttached = attached;

                if (now >= _quietUntil)
                {
                    // The game's hook_attach (+ trailer_cables) / hook_detach when loaded.
                    EtsGameEvent hook = attached ? _gHookOn : _gHookOff;
                    bool game = hook != null && hook.PlayOnce(_sfxOut, 0.7f);
                    if (game && attached && _gCables != null) _gCables.PlayOnce(_sfxOut, 0.6f);
                    // The trailer's support legs: wound up when it is coupled, down when it is left (trailer_brace.bank).
                    EtsGameEvent brace = attached ? _gBraceUp : _gBraceDown;
                    if (brace != null) brace.PlayOnce(_sfxTrailer, 0.7f);
                    if (!game) OneShot(_sfxOut, attached ? s_couple : s_uncouple, attached ? 0.4f : 0.32f);
                    Debug.Log("[SomniumEuroTruck] Truck sound: trailer " + (attached ? "coupled (clunk + air lines)." : "uncoupled (air lines + release)."));
                }
            }

            float rattle = 0f;

            if (attached && local)
            {
                float rough = Mathf.Clamp01(_roughS / 0.4f);
                // The synthesised rattle only without the game's trailer tyres and suspension (which replace it).
                rattle = _pTrTyres[0] != null ? 0f : 0.3f * Mathf.Pow(Mathf.Clamp01(_speedS / 50f), 0.8f) * (0.35f + 0.65f * rough) * outside;
                Rigidbody trailerBody = trailer.Body, body = truck.Body;

                if (trailerBody != null && body != null && !body.isKinematic && now >= _nextCreak && _speedS > 1f && _speedS < 30f)
                {
                    float swing = Mathf.Abs(trailerBody.angularVelocity.y - body.angularVelocity.y) * Mathf.Rad2Deg;

                    if (swing > 6f)
                    {
                        _nextCreak = now + Random.Range(4f, 9f);
                        OneShot(_sfxOut, s_creak, Mathf.Clamp(0.15f + 0.015f * (swing - 6f), 0.15f, 0.3f));
                    }
                }
            }

            Drive(LoopRattle, rattle, 1f, true);

            // A knock: the body's velocity changing by more than ImpactDeltaV in one frame (a crash, a car, a hard landing).
            if (!local)
            {
                _haveVelocity = false;
                return;
            }

            Rigidbody rb = truck.Body;

            if (rb == null || rb.isKinematic)
            {
                _haveVelocity = false;
                return;
            }

            Vector3 v = rb.linearVelocity;

            if (_haveVelocity)
            {
                float dv = (v - _lastVelocity).magnitude;

                if (dv > ImpactDeltaV && now >= _nextImpact && now >= _quietUntil)
                {
                    _nextImpact = now + 0.3f;
                    if (!PlayCrash(dv)) OneShot(_sfxCab, s_impact, Mathf.Clamp(0.06f + 0.05f * (dv - ImpactDeltaV), 0.06f, 0.3f));

                    if (_impactLogs < 20)
                    {
                        _impactLogs++;
                        Debug.Log("[SomniumEuroTruck] Truck sound: knock (velocity changed " + dv.ToString("0.0") + " m/s in a frame at " + _speedS.ToString("0") + " km/h).");
                    }
                }
            }

            _lastVelocity = v;
            _haveVelocity = true;
        }

        /// <summary>The extras' loops get their clips when EtsSynth has built them (a few frames after the first truck sound).</summary>
        private void AssignExtraClips()
        {
            if (_extrasAssigned)
            {
                return;
            }

            bool all = AssignClip(LoopGravel, s_gravel);
            all &= AssignClip(LoopGrass, s_grass);
            all &= AssignClip(LoopEngineBrake, s_engineBrake);
            all &= AssignClip(LoopRattle, s_rattle);
            _extrasAssigned = all;
        }

        private bool AssignClip(int index, AudioClip clip)
        {
            AudioSource s = _loops[index];

            if (s == null)
            {
                return true;
            }

            if (s.clip == null && clip != null)
            {
                s.clip = clip;
            }

            return s.clip != null;
        }

        /// <summary>A puppet's sound: true while it should sound (Update stops its sources otherwise).</summary>
        private bool PuppetAudible()
        {
            bool audible = Truck != null && Truck.Placed && Truck.EngineRunning && !ReferenceEquals(EtsLocalTruck.Occupied, _puppet);

            if (audible)
            {
                Transform ear = EtsTruckRig.LocalView(); // once a frame for every truck (EtsTruckRig.LocalView)
                audible = ear == null || (ear.position - Truck.transform.position).sqrMagnitude < PuppetAudibleMetres * PuppetAudibleMetres;
            }

            return audible;
        }

        private void SetSpatial(bool threeD)
        {
            if (_spatialSet && threeD == _threeD)
            {
                return;
            }

            _spatialSet = true;
            _threeD = threeD;
            float blend = threeD ? 1f : 0f;

            for (int i = 0; i < _loops.Length; i++)
            {
                _loops[i].spatialBlend = blend;
            }

            _sfxCab.spatialBlend = blend;
            _sfxOut.spatialBlend = blend;
            if (_sfxTrailer != null) _sfxTrailer.spatialBlend = blend;
            s_blend = blend;
            ForEachGamePlayer(s_setBlend);
        }

        private static float s_blend;
        private static readonly System.Action<EtsGameEventPlayer> s_setBlend = p => p.SetSpatialBlend(s_blend);
        private static readonly System.Action<EtsGameEventPlayer> s_stopPlayer = p => p.Stop();

        private void Drive(int index, float volume, float pitch, bool randomStart)
        {
            AudioSource s = _loops[index];

            if (s == null || s.clip == null) // an extra whose clip EtsSynth has not built yet
            {
                return;
            }

            if (volume > Silent)
            {
                s.volume = Mathf.Min(volume, 1f);
                s.pitch = pitch;

                if (!s.isPlaying)
                {
                    if (randomStart && s.clip != null)
                    {
                        s.time = Random.value * s.clip.length * 0.9f;
                    }

                    s.Play();
                    Started();
                }
            }
            else if (s.isPlaying)
            {
                s.Stop();
            }
        }

        /// <summary>One engine layer: its two load versions start and stop together so they stay in step.</summary>
        private void DrivePair(int index, bool on, float offVolume, float onVolume, float pitch)
        {
            AudioSource a = _loops[index], b = _loops[index + 1];

            if (a == null || b == null)
            {
                return;
            }

            if (on)
            {
                a.volume = Mathf.Min(offVolume, 1f);
                b.volume = Mathf.Min(onVolume, 1f);
                a.pitch = pitch;
                b.pitch = pitch;

                if (!a.isPlaying || !b.isPlaying)
                {
                    a.Stop();
                    b.Stop();
                    a.Play();
                    b.Play();
                    Started();
                }
            }
            else if (a.isPlaying || b.isPlaying)
            {
                a.Stop();
                b.Stop();
            }
        }

        private void Started()
        {
            _anyPlaying = true;

            if (!_loggedPlay)
            {
                _loggedPlay = true;
                Debug.Log("[SomniumEuroTruck] Truck sound: playing " + (Truck != null ? Truck.name : "?") + (_threeD ? " (3D, heard from outside)" : " (2D, in the cab)") + (_puppet != null ? " [puppet]" : "") + ".");
            }
        }

        private void SilenceLoops()
        {
            _hornEnv = 0f;
            ForEachGamePlayer(s_stopPlayer);

            if (!_anyPlaying || _loops == null)
            {
                return;
            }

            _anyPlaying = false;

            for (int i = 0; i < _loops.Length; i++)
            {
                AudioSource s = _loops[i];

                if (s != null && s.isPlaying)
                {
                    s.Stop();
                }
            }
        }

        // ---------------------------------------------------------------- clip synthesis (once per client)

        private static bool ClipsReady => s_engine != null && s_engine[0] != null && s_engine[Layers * 2 - 1] != null && s_doorClose != null;

        private static void BuildClips(int cylinders)
        {
            if (ClipsReady)
            {
                return;
            }

            float t0 = Time.realtimeSinceStartup;
            int cyl = Mathf.Clamp(cylinders, 2, 16);
            int total = 0, count = 0;
            s_engine = new AudioClip[Layers * 2];

            for (int i = 0; i < Layers; i++)
            {
                // A one-second loop holding a whole number of half-firing cycles (the 0.5p term, rpm * cyl / 240 per
                // second), so every term of the wave comes back to its start and the loop is seamless.
                int k = Mathf.Max(4, Mathf.RoundToInt(LayerTargetRpm[i] * cyl / 240f));
                s_layerRpm[i] = k * 240f / cyl;
                s_engine[i * 2] = Make("ets engine " + i + " off", EngineWave(s_layerRpm[i], cyl, k, 0.35f), ref total, ref count);
                s_engine[i * 2 + 1] = Make("ets engine " + i + " on", EngineWave(s_layerRpm[i], cyl, k, 1f), ref total, ref count);
            }

            s_turbo = Make("ets turbo", TurboWave(), ref total, ref count);
            s_road = Make("ets road", RoadWave(), ref total, ref count);
            s_wind = Make("ets wind", WindWave(), ref total, ref count);
            s_horn = Make("ets horn", HornWave(), ref total, ref count);
            s_beep = Make("ets reverse", BeepWave(), ref total, ref count);
            s_tick = Make("ets relay on", RelayWave(3100.0, 1250.0), ref total, ref count);
            s_tock = Make("ets relay off", RelayWave(2300.0, 900.0), ref total, ref count);
            s_brakeSet = Make("ets brake set", HissWave(0.5f, false), ref total, ref count);
            s_brakeRelease = Make("ets brake release", HissWave(1.4f, true), ref total, ref count);
            s_gear = Make("ets gear", GearWave(), ref total, ref count);
            s_start = Make("ets engine start", StartWave(cyl), ref total, ref count);
            s_stop = Make("ets engine stop", StopWave(cyl), ref total, ref count);
            s_doorOpen = Make("ets door open", DoorWave(true), ref total, ref count);
            s_doorClose = Make("ets door close", DoorWave(false), ref total, ref count);

            float ms = (Time.realtimeSinceStartup - t0) * 1000f;
            Debug.Log("[SomniumEuroTruck] Truck sound: clips built in " + ms.ToString("0") + " ms (" + count + " clips, "
                + (total / (float)Rate).ToString("0.0") + " s at " + Rate + " Hz; engine layers " + s_layerRpm[0].ToString("0") + "/"
                + s_layerRpm[1].ToString("0") + "/" + s_layerRpm[2].ToString("0") + " rpm; output " + AudioSettings.outputSampleRate
                + " Hz) (AudioSource route)");
        }

        private static AudioClip Make(string name, float[] data, ref int total, ref int count)
        {
            Normalise(data, 0.9f);
            AudioClip clip = AudioClip.Create(name, data.Length, 1, Rate, false);
            clip.SetData(data, 0);
            total += data.Length;
            count++;
            return clip;
        }

        private static void Normalise(float[] data, float peak)
        {
            float max = 0f;

            for (int i = 0; i < data.Length; i++)
            {
                float a = data[i] < 0f ? -data[i] : data[i];
                if (a > max) max = a;
            }

            if (max < 1e-6f)
            {
                return;
            }

            float g = peak / max;

            for (int i = 0; i < data.Length; i++)
            {
                data[i] *= g;
            }
        }

        private static float Noise()
        {
            s_noise ^= s_noise << 13;
            s_noise ^= s_noise >> 17;
            s_noise ^= s_noise << 5;
            return ((s_noise & 0xFFFF) / 32768f) - 1f;
        }

        private static float[] White(int n)
        {
            var d = new float[n];
            for (int i = 0; i < n; i++) d[i] = Noise();
            return d;
        }

        /// <summary>One-pole low-pass run twice round the buffer, so its end state meets its start and the loop has no seam.</summary>
        private static float[] Lowpass(float[] x, float a)
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

        /// <summary>White minus its low-passed self: a bright hiss.</summary>
        private static float[] Hiss(float[] white, float a)
        {
            float[] lp = Lowpass(white, a);
            var y = new float[white.Length];
            for (int i = 0; i < y.Length; i++) y[i] = white[i] - lp[i];
            return y;
        }

        private static float Sin(double cycles) => (float)System.Math.Sin(cycles * 2.0 * System.Math.PI);

        private static float Decay(double t, double tau) => t < 0.0 ? 0f : (float)System.Math.Exp(-t / tau);

        /// <summary>The old audio-thread engine note at fixed revs and load, with a little per-firing combustion rasp.</summary>
        private static float[] EngineWave(float rpm, int cyl, int halfCycles, float load)
        {
            int n = Rate;
            var d = new float[n];
            float[] white = White(n);
            float[] rasp = Lowpass(white, 0.25f);
            Normalise(rasp, 1f);
            var jitter = new float[halfCycles];
            for (int c = 0; c < halfCycles; c++) jitter[c] = Noise();
            double firing = rpm / 60.0 * cyl * 0.5;

            for (int j = 0; j < n; j++)
            {
                double t = (double)j / Rate;
                double p = firing * t; // in cycles
                float s1 = Sin(p);
                int c = (int)(p * 0.5) % halfCycles;
                float e = (s1 * 0.6f + Sin(2 * p) * 0.3f * load + Sin(3 * p) * 0.15f * load + Sin(0.5 * p) * 0.35f) * (1f + 0.1f * jitter[c]);
                float pulse = 0.5f + 0.5f * s1;
                pulse *= pulse;
                pulse *= pulse;
                pulse *= pulse;
                e += pulse * rasp[j] * (0.15f + 0.25f * load);
                d[j] = e / (1f + 0.6f * System.Math.Abs(e)); // soft clipping, the old "rasp"
            }

            return d;
        }

        private static float[] TurboWave()
        {
            int n = Rate / 2; // 1000 whole cycles of 2 kHz
            float[] hiss = Hiss(White(n), 0.3f);
            Normalise(hiss, 1f);
            var d = new float[n];
            for (int j = 0; j < n; j++) d[j] = Sin(2000.0 * j / Rate) * 0.85f + hiss[j] * 0.12f;
            return d;
        }

        private static float[] RoadWave()
        {
            int n = Rate * 2;
            float[] white = White(n);
            float[] lp = Lowpass(white, 0.08f);
            float[] rumble = Lowpass(white, 0.015f);
            Normalise(lp, 1f);
            Normalise(rumble, 1f);
            var d = new float[n];
            for (int j = 0; j < n; j++) d[j] = lp[j] * 0.8f + rumble[j] * 0.4f + white[j] * 0.1f;
            return d;
        }

        private static float[] WindWave()
        {
            int n = Rate * 3;
            float[] white = White(n);
            float[] a = Lowpass(white, 0.03f);
            float[] b = Lowpass(a, 0.03f);
            Normalise(b, 1f);
            var d = new float[n];

            for (int j = 0; j < n; j++)
            {
                double t = (double)j / Rate;
                float gust = 0.7f + 0.3f * Sin(t * 2.0 / 3.0); // two gusts per loop, so it wraps
                d[j] = b[j] * gust + (white[j] - a[j]) * 0.04f;
            }

            return d;
        }

        private static float[] HornWave()
        {
            int n = Rate; // 311 and 415 whole cycles
            var d = new float[n];

            for (int j = 0; j < n; j++)
            {
                double t = (double)j / Rate;
                float h = (Sin(311.0 * t) + Sin(415.0 * t) + 0.3f * Sin(622.0 * t)) * 0.25f;
                d[j] = h / (1f + 0.8f * System.Math.Abs(h));
            }

            return d;
        }

        private static float[] BeepWave()
        {
            int n = Rate;
            var d = new float[n];
            int on = Rate / 2;

            for (int j = 0; j < on; j++)
            {
                float tone = Sin(1000.0 * j / Rate);
                float edge = Mathf.Clamp01(j / (Rate * 0.005f)) * Mathf.Clamp01((on - j) / (Rate * 0.005f));
                d[j] = ((tone > 0f ? 1f : -1f) * 0.5f + tone * 0.5f) * edge;
            }

            return d;
        }

        private static float[] RelayWave(double ring, double body)
        {
            int n = (int)(Rate * 0.03f);
            float[] hiss = Hiss(White(n), 0.3f);
            var d = new float[n];

            for (int j = 0; j < n; j++)
            {
                double t = (double)j / Rate;
                d[j] = hiss[j] * Decay(t, 0.0015) * 0.8f + Sin(ring * t) * Decay(t, 0.004) * 0.6f + Sin(body * t) * Decay(t, 0.006) * 0.35f;
            }

            return d;
        }

        /// <summary>The parking-brake valve: set is a short sharp "pssht", release a longer, softer rush of air.</summary>
        private static float[] HissWave(float seconds, bool release)
        {
            int n = (int)(Rate * seconds);
            float[] white = White(n);
            float[] hiss = Hiss(white, 0.12f);
            float[] air = Lowpass(white, 0.05f);
            Normalise(hiss, 1f);
            Normalise(air, 1f);
            var d = new float[n];

            for (int j = 0; j < n; j++)
            {
                double t = (double)j / Rate;

                if (release)
                {
                    float env = (1f - Decay(t, 0.03)) * (t < 0.3 ? 1f : Decay(t - 0.3, 0.3));
                    d[j] = hiss[j] * env * 0.8f + air[j] * env * 0.35f;
                }
                else
                {
                    float env = (1f - Decay(t, 0.004)) * Decay(t, 0.11);
                    d[j] = hiss[j] * env + air[j] * Decay(t, 0.025) * 0.6f;
                }
            }

            return d;
        }

        private static void Clunk(float[] d, float[] low, float[] high, double at, double freq, float amp)
        {
            int start = (int)(at * Rate);

            for (int j = start; j < d.Length; j++)
            {
                double u = (double)(j - start) / Rate;
                d[j] += amp * (Sin(freq * u) * Decay(u, 0.035) + low[j] * Decay(u, 0.012) * 0.8f + high[j] * Decay(u, 0.002) * 0.5f);
            }
        }

        private static float[] GearWave()
        {
            int n = (int)(Rate * 0.3f);
            float[] white = White(n);
            float[] low = Lowpass(white, 0.1f);
            float[] high = Hiss(white, 0.3f);
            Normalise(low, 1f);
            Normalise(high, 1f);
            var d = new float[n];
            Clunk(d, low, high, 0.0, 85.0, 1f);
            Clunk(d, low, high, 0.085, 110.0, 0.6f);
            return d;
        }

        private static float[] DoorWave(bool open)
        {
            int n = (int)(Rate * (open ? 0.35f : 0.45f));
            float[] white = White(n);
            float[] low = Lowpass(white, 0.1f);
            float[] high = Hiss(white, 0.3f);
            Normalise(low, 1f);
            Normalise(high, 1f);
            var d = new float[n];

            for (int j = 0; j < n; j++)
            {
                double t = (double)j / Rate;

                if (open)
                {
                    double u = t - 0.03;
                    d[j] = high[j] * Decay(t, 0.002) * 0.7f + Sin(1800.0 * t) * Decay(t, 0.006) * 0.3f
                        + (u < 0 ? 0f : Sin(140.0 * u) * Decay(u, 0.04) * 0.5f + low[j] * Decay(u, 0.02) * 0.5f)
                        + high[j] * Decay(t - 0.12, 0.004) * 0.25f;
                }
                else
                {
                    double u = t - 0.012;
                    d[j] = Sin(70.0 * t) * Decay(t, 0.07) * 0.9f + Sin(150.0 * t) * Decay(t, 0.03) * 0.4f + low[j] * Decay(t, 0.03) * 0.7f
                        + high[j] * Decay(u, 0.003) * 0.4f + high[j] * Decay(t, 0.12) * 0.06f;
                }
            }

            return d;
        }

        private static float EngineNote(double p, float load)
        {
            float e = Sin(p) * 0.6f + Sin(2 * p) * 0.3f * load + Sin(3 * p) * 0.15f * load + Sin(0.5 * p) * 0.35f;
            return e / (1f + 0.6f * System.Math.Abs(e));
        }

        /// <summary>Starter motor cranking against compression for 0.9 s, then the engine catching, flaring and settling.</summary>
        private static float[] StartWave(int cyl)
        {
            int n = Rate * 2;
            float[] white = White(n);
            float[] chug = Lowpass(white, 0.08f);
            Normalise(chug, 1f);
            var d = new float[n];
            double phase = 0.0;

            for (int j = 0; j < n; j++)
            {
                double t = (double)j / Rate;
                float s = 0f;

                if (t < 0.95)
                {
                    float env = Mathf.Clamp01((float)t / 0.05f) * Mathf.Clamp01((0.95f - (float)t) / 0.08f);
                    float comp = 0.5f + 0.5f * Sin(8.0 * t + 1.5 * t * t);
                    float whine = Sin(150.0 * t + 20.0 * t * t) * 0.5f + Sin(300.0 * t + 40.0 * t * t) * 0.25f + Sin(450.0 * t + 60.0 * t * t) * 0.12f;
                    s += env * (whine * (0.3f + 0.3f * comp) + chug[j] * comp * comp * comp * 0.8f);
                }

                if (t > 0.85)
                {
                    float u = (float)(t - 0.85);
                    float rpm = u < 0.35f ? Mathf.Lerp(300f, 1150f, u / 0.35f) : Mathf.Lerp(1150f, 650f, Mathf.Clamp01((u - 0.35f) / 0.5f));
                    phase += rpm / 60.0 * cyl * 0.5 / Rate;
                    float env = Mathf.Clamp01(u / 0.08f) * (t < 1.5 ? 1f : Mathf.Clamp01((2f - (float)t) / 0.5f));
                    s += EngineNote(phase, u < 0.35f ? 1f : 0.5f) * env * 0.9f;
                }

                d[j] = s;
            }

            return d;
        }

        /// <summary>Revs falling from idle to nothing in a second, a last shudder, and the turbo spinning down.</summary>
        private static float[] StopWave(int cyl)
        {
            int n = (int)(Rate * 1.3f);
            var d = new float[n];
            double phase = 0.0, turbo = 0.0;

            for (int j = 0; j < n; j++)
            {
                double t = (double)j / Rate;
                float left = Mathf.Clamp01(1f - (float)t);
                float rpm = 650f * Mathf.Pow(left, 1.3f);
                phase += rpm / 60.0 * cyl * 0.5 / Rate;
                turbo += (700.0 + 1200.0 * left) / Rate;
                float env = Mathf.Clamp01(1f - (float)t / 1.05f);
                env *= env;
                double u = t - 0.85;
                d[j] = EngineNote(phase, 0.35f) * env * 0.9f
                    + (u < 0 ? 0f : Sin(42.0 * u) * Decay(u, 0.1) * 0.5f)
                    + Sin(turbo) * Decay(t, 0.5) * 0.06f;
            }

            return d;
        }

        // ---------------------------------------------------------------- the extras' synthesis (EtsSynth worker thread)

        private static void RequestExtraClips()
        {
            if (s_extrasRequested)
            {
                return;
            }

            s_extrasRequested = true;
            int r = EtsSynth.AmbientRate;
            EtsSynth.Request("ets tyres gravel", r, () => GravelWave(r), c => s_gravel = c);
            EtsSynth.Request("ets tyres grass", r, () => GrassWave(r), c => s_grass = c);
            EtsSynth.Request("ets engine brake", r, () => EngineBrakeWave(r), c => s_engineBrake = c);
            EtsSynth.Request("ets trailer rattle", r, () => RattleWave(r), c => s_rattle = c);
            EtsSynth.Request("ets trailer creak", r, () => CreakWave(r), c => s_creak = c);
            EtsSynth.Request("ets bump", r, () => BumpWave(r), c => s_bump = c);
            EtsSynth.Request("ets impact", r, () => ImpactWave(r), c => s_impact = c);
            EtsSynth.Request("ets couple", r, () => CoupleWave(r, true), c => s_couple = c);
            EtsSynth.Request("ets uncouple", r, () => CoupleWave(r, false), c => s_uncouple = c);
            EtsSynth.Request("ets cruise on", r, () => ChimeWave(r, true), c => s_cruiseOn = c);
            EtsSynth.Request("ets cruise off", r, () => ChimeWave(r, false), c => s_cruiseOff = c);
        }

        /// <summary>Tyres on gravel: a low rumble under a dense crunch of tiny stones (2600 grains of bright noise), two seconds.</summary>
        private static float[] GravelWave(int rate)
        {
            int n = rate * 2;
            var rng = new EtsSynth.Rng(0x6A7E1u);
            float[] rumble = EtsSynth.Lowpass(EtsSynth.White(n, rng), EtsSynth.Coef(110, rate));
            float[] grit = EtsSynth.Highpass(EtsSynth.White(n, rng), EtsSynth.Coef(1400, rate));
            EtsSynth.Normalise(rumble, 1f);
            EtsSynth.Normalise(grit, 1f);
            var d = new float[n];
            for (int j = 0; j < n; j++) d[j] = rumble[j] * 0.35f;

            for (int g = 0; g < 2600; g++)
            {
                float amp = rng.Unit();
                EtsSynth.Burst(d, rate, rng.Unit() * 2.0, grit, 0.0002, rng.Range(0.0004f, 0.002f), amp * amp * amp);
            }

            return d;
        }

        /// <summary>Tyres on grass and earth: a soft thumping rumble and a swish of stalks, two seconds.</summary>
        private static float[] GrassWave(int rate)
        {
            int n = rate * 2;
            var rng = new EtsSynth.Rng(0x62A55u);
            float[] thump = EtsSynth.Lowpass(EtsSynth.Lowpass(EtsSynth.White(n, rng), EtsSynth.Coef(90, rate)), EtsSynth.Coef(140, rate));
            float[] swish = EtsSynth.Band(EtsSynth.White(n, rng), EtsSynth.Coef(2600, rate), EtsSynth.Coef(450, rate));
            float[] mod = EtsSynth.Lowpass(EtsSynth.White(n, rng), EtsSynth.Coef(3, rate));
            EtsSynth.Normalise(thump, 1f);
            EtsSynth.Normalise(swish, 1f);
            EtsSynth.Normalise(mod, 1f);
            var d = new float[n];
            for (int j = 0; j < n; j++) d[j] = thump[j] * 0.6f + swish[j] * 0.3f * (0.45f + 0.55f * Mathf.Abs(mod[j]));

            for (int g = 0; g < 350; g++)
            {
                float amp = rng.Unit();
                EtsSynth.Burst(d, rate, rng.Unit() * 2.0, swish, 0.002, 0.01, amp * amp * 0.5f);
            }

            return d;
        }

        /// <summary>The engine brake's rasp: six cylinders at 1200 rpm (60 Hz), each firing a sharp crack over a square-ish note. One second, seamless.</summary>
        private static float[] EngineBrakeWave(int rate)
        {
            int n = rate;
            var rng = new EtsSynth.Rng(0xE4Bu);
            float[] crack = EtsSynth.Highpass(EtsSynth.White(n, rng), EtsSynth.Coef(900, rate));
            EtsSynth.Normalise(crack, 1f);
            var jitter = new float[60];
            for (int i = 0; i < jitter.Length; i++) jitter[i] = rng.Range(0.7f, 1.3f);
            var d = new float[n];

            for (int j = 0; j < n; j++)
            {
                double p = 60.0 * j / rate;
                int cycle = (int)p % 60;
                double frac = p - System.Math.Floor(p);
                float body = EtsSynth.Sin(p) * 0.5f + EtsSynth.Sin(3.0 * p) * 0.22f + EtsSynth.Sin(5.0 * p) * 0.12f + EtsSynth.Sin(0.5 * p) * 0.25f;
                float burst = (float)System.Math.Exp(-frac / 0.12) * jitter[cycle];
                float e = body * 0.6f + crack[j] * burst * 0.9f;
                d[j] = e / (1f + 0.6f * Mathf.Abs(e));
            }

            return d;
        }

        /// <summary>A coupled trailer on a rough road: a low body rumble and 110 small clanks of loose metal, three seconds.</summary>
        private static float[] RattleWave(int rate)
        {
            int n = rate * 3;
            var rng = new EtsSynth.Rng(0x7A77u);
            float[] low = EtsSynth.Lowpass(EtsSynth.White(n, rng), EtsSynth.Coef(90, rate));
            EtsSynth.Normalise(low, 1f);
            var d = new float[n];
            for (int j = 0; j < n; j++) d[j] = low[j] * 0.25f;
            var f = new double[3];
            var tau = new double[3];
            float[] amp = { 1f, 0.6f, 0.35f };

            for (int k = 0; k < 110; k++)
            {
                double baseF = rng.Range(600f, 2800f);
                f[0] = baseF;
                f[1] = baseF * 1.47;
                f[2] = baseF * 2.09;
                tau[0] = rng.Range(0.02f, 0.04f);
                tau[1] = 0.015;
                tau[2] = 0.01;
                float g = rng.Unit();
                EtsSynth.Strike(d, rate, rng.Unit() * 3.0, f, tau, amp, g * g * 0.9f + 0.1f);
            }

            return d;
        }

        /// <summary>The fifth wheel creaking as the trailer swings: a stick-slip squeal round 190 Hz over a low groan.</summary>
        private static float[] CreakWave(int rate)
        {
            const double dur = 1.0;
            int n = (int)(rate * 1.1);
            var rng = new EtsSynth.Rng(0xC2EAu);
            var d = new float[n];
            double phase = 0.0;

            for (int j = 0; j < (int)(dur * rate); j++)
            {
                double t = (double)j / rate;
                double f = 190.0 + 45.0 * System.Math.Sin(2.0 * System.Math.PI * 1.1 * t) + 2.4 * rng.Next();
                phase += f / rate;
                double stick = System.Math.Pow(0.5 + 0.5 * System.Math.Sin(2.0 * System.Math.PI * 23.0 * t), 3.0);
                double s = 0.0;
                for (int k = 1; k <= 6; k++) s += System.Math.Sin(2.0 * System.Math.PI * k * phase) / k;
                double env = System.Math.Min(1.0, t / 0.12) * System.Math.Min(1.0, (dur - t) / 0.3);
                d[j] = (float)(env * ((stick * 0.8 + 0.2) * s + 0.3 * System.Math.Sin(2.0 * System.Math.PI * 70.0 * t)));
            }

            return d;
        }

        /// <summary>A suspension thud: a low thump, a burst of dull noise and a faint rattle of the cab.</summary>
        private static float[] BumpWave(int rate)
        {
            var d = new float[(int)(rate * 0.45)];
            var rng = new EtsSynth.Rng(0xB0A1u);
            float[] dull = EtsSynth.Lowpass(EtsSynth.White(d.Length, rng), EtsSynth.Coef(200, rate));
            EtsSynth.Normalise(dull, 1f);
            EtsSynth.Strike(d, rate, 0.0, new double[] { 52, 104, 640, 1310 }, new double[] { 0.07, 0.04, 0.05, 0.03 }, new float[] { 1f, 0.3f, 0.12f, 0.06f }, 1f);
            EtsSynth.Burst(d, rate, 0.0, dull, 0.001, 0.025, 0.7f);
            return d;
        }

        /// <summary>A knock: a heavy thump, a crash of noise and ringing sheet metal.</summary>
        private static float[] ImpactWave(int rate)
        {
            var d = new float[(int)(rate * 1.0)];
            var rng = new EtsSynth.Rng(0x1A9Au);
            float[] white = EtsSynth.White(d.Length, rng);
            float[] dull = EtsSynth.Lowpass(white, EtsSynth.Coef(300, rate));
            EtsSynth.Normalise(dull, 1f);
            EtsSynth.Strike(d, rate, 0.0, new double[] { 36, 72, 183, 412, 877, 1493, 2207, 3120 },
                new double[] { 0.12, 0.08, 0.35, 0.3, 0.25, 0.2, 0.15, 0.1 }, new float[] { 1f, 0.4f, 0.3f, 0.28f, 0.24f, 0.2f, 0.15f, 0.1f }, 1f);
            EtsSynth.Burst(d, rate, 0.0, white, 0.0005, 0.03, 0.8f);
            EtsSynth.Burst(d, rate, 0.0, dull, 0.001, 0.12, 0.6f);
            return d;
        }

        /// <summary>
        /// Coupling: the jaw's clack, the kingpin's heavy clunk, then the air lines hissing as they fill. Uncoupling: the
        /// lines' release first, then the jaw letting go.
        /// </summary>
        private static float[] CoupleWave(int rate, bool couple)
        {
            var d = new float[(int)(rate * (couple ? 1.5 : 1.3))];
            var rng = new EtsSynth.Rng(couple ? 0xC0F1u : 0x0C0Fu);
            float[] white = EtsSynth.White(d.Length, rng);
            float[] air = EtsSynth.Highpass(EtsSynth.White(d.Length, rng), EtsSynth.Coef(1800, rate));
            float[] dull = EtsSynth.Lowpass(white, EtsSynth.Coef(250, rate));
            EtsSynth.Normalise(air, 1f);
            EtsSynth.Normalise(dull, 1f);

            if (couple)
            {
                EtsSynth.Strike(d, rate, 0.0, new double[] { 900, 1800, 2750 }, new double[] { 0.02, 0.015, 0.01 }, new float[] { 1f, 0.6f, 0.4f }, 0.6f);
                EtsSynth.Burst(d, rate, 0.0, white, 0.0003, 0.003, 0.5f);
                EtsSynth.Strike(d, rate, 0.09, new double[] { 68, 240, 520, 1100 }, new double[] { 0.09, 0.15, 0.12, 0.08 }, new float[] { 1f, 0.5f, 0.35f, 0.2f }, 1f);
                EtsSynth.Burst(d, rate, 0.09, dull, 0.001, 0.03, 0.6f);
                EtsSynth.Burst(d, rate, 0.5, air, 0.04, 0.3, 0.22f);
            }
            else
            {
                EtsSynth.Burst(d, rate, 0.0, air, 0.01, 0.25, 0.4f);
                EtsSynth.Strike(d, rate, 0.55, new double[] { 80, 300, 700, 1500 }, new double[] { 0.07, 0.1, 0.08, 0.05 }, new float[] { 1f, 0.5f, 0.35f, 0.2f }, 1f);
                EtsSynth.Strike(d, rate, 0.62, new double[] { 1100, 2100 }, new double[] { 0.015, 0.01 }, new float[] { 0.5f, 0.3f }, 0.6f);
            }

            return d;
        }

        /// <summary>The cruise-control chime: 880 then 1175 Hz engaging, the other way round letting go.</summary>
        private static float[] ChimeWave(int rate, bool up)
        {
            var d = new float[(int)(rate * 0.45)];
            double a = up ? 880.0 : 1175.0, b = up ? 1175.0 : 880.0;
            EtsSynth.Tone(d, rate, 0.01, 0.13, a, a, 1f, 0.005, 0.03, 0f, 0.15f);
            EtsSynth.Tone(d, rate, 0.19, 0.16, b, b, 1f, 0.005, 0.05, 0f, 0.15f);
            return d;
        }
    }
}
