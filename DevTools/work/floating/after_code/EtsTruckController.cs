using System;
using System.Collections.Generic;
using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
using TMPro;
using UnityEngine;

namespace SomniumSpace.Worlds.SomniumEuroTruck
{
    /// <summary>
    /// Drives the truck. A rigid body with one raycast wheel per wheel locator: each wheel is a
    /// spring and damper along the chassis up axis, a tyre that pushes along its rolling direction
    /// (engine through the gearbox and differential on the powered axle, brakes on every wheel,
    /// rolling resistance) and sideways against slip, with the two held inside a friction circle.
    /// The numbers are the game's: kerb weight per axle, powered axles and residual travel from the
    /// chassis definition, torque and rev limit from the engine, ratios and final drive from the
    /// gearbox, wet grip from the tyres, steering lock and its fall-off with speed, air resistance
    /// and brake factor from physics.sii. What the game does not state - spring sag, damping ratio,
    /// tyre cornering stiffness - is chosen once here and named.
    ///
    /// Controls: W/S or arrows throttle and brake (S also reverses from a standstill), A/D steer,
    /// Space brake, P parking brake, C cruise control and T trailer attach/detach (the game's keys; in a
    /// headset the right stick up and down), R back to the spawn point (not once the app is on the client's
    /// tablet, EtsTabletLink.KeysRetired, where R is the client's key and the respawn is on the
    /// tablet's Drive page).
    /// </summary>
    public class EtsTruckController : MonoBehaviour
    {
        public sealed class Wheel
        {
            public string Name;
            public Transform Visual;
            public Quaternion VisualRest;
            public float SpinSign = 1f;
            public Vector3 LocalHub;
            public int Axle;
            public bool Steered;
            public bool Powered;
            public float Radius;
            public float Spring;
            public float Damper;
            public float RestLength;
            public bool Grounded;
            public float Load;
            public float Compression;
            public float Travel;
            public float SpinDegrees;
            public float SteerDegrees;
            public RaycastHit Hit;
        }

        /// <summary>Spring sag under the kerb weight; the chassis definition's residual travel is
        /// what is left below that, so travel is sag plus residual.</summary>
        public const float Sag = 0.10f;

        public const float DampingRatio = 0.4f;

        /// <summary>Cornering stiffness per radian of slip, as a multiple of the wheel load.</summary>
        public const float CorneringStiffness = 9f;

        public const float IdleRpm = 650f;
        public const float ShiftUpRpm = 1550f;
        public const float ShiftDownRpm = 950f;
        public const float ShiftTime = 0.35f;
        public const float DrivelineEfficiency = 0.9f;

        public EtsVehicleSpec Spec { get; private set; }
        public IReadOnlyList<Wheel> Wheels => _wheels;
        public Rigidbody Body => _body;

        /// <summary>Driver's eye position in the truck's local frame, for the interior camera.</summary>
        public Vector3 EyeLocal = new Vector3(-0.79f, 2.25f, 2.35f);

        /// <summary>
        /// The driver's seat (the hip point the seated avatar's root goes to) in the truck's frame.
        /// The game has no seat definition (probe 1623: 24,355 text files under /def/vehicle, /def/camera
        /// and /ui name no seat); its seats are the chassis or cabin model's pair of "driver*" locators,
        /// mirrored in x. The truck builder (EtsTruckFactory) reads them by the rule in
        /// EtsTruckSeats.Find ([elev §5]): the driver* locator at x &lt; 0 in the parts the cabin's
        /// left-hand-drive variant shows is the driver's (every preview truck is left-hand drive); the one
        /// at x &gt; 0 in its right-hand-drive twin's parts is the passenger's; a lone one is mirrored. All 18
        /// preview trucks have both (probe 1862). The default, to the millimetre, is the DAF XF Euro 6's
        /// driver_0, game (-0.792, 1.471, -2.378) in /vehicle/truck/daf_xf_euro6/truck.pmd (probe 1622), z
        /// negated for Unity; the factory writes the exact value, the same floats EtsTruckSeat.SeatLocal
        /// has always been given from that locator (probe 1864: equal to the last bit).
        /// </summary>
        public Vector3 DriverSeatLocal = new Vector3(-0.792f, 1.471f, 2.378f);

        /// <summary>
        /// The passenger's seat in the truck's frame: the right-hand "driver*" locator (the seat a
        /// right-hand-drive cab is driven from). The default is the DAF XF Euro 6's driver_1, game
        /// (0.835, 1.463, -2.375) (probe 1622): 1.627 m right of the driver, 8 mm lower. The interior's
        /// codrv_plate (the passenger's dashboard plate, 1.564 m right of the eye) agrees with it.
        /// </summary>
        public Vector3 PassengerSeatLocal = new Vector3(0.835f, 1.463f, 2.375f);

        /// <summary>
        /// The seat's hip point in the truck's frame: 0 is the driver's, any other index the one
        /// passenger seat (one per truck, PLAN Q-B3; the bunk is not a seat).
        /// </summary>
        public Vector3 SeatLocalFor(int seat)
        {
            return seat == 0 ? DriverSeatLocal : PassengerSeatLocal;
        }

        /// <summary>
        /// The eye in the truck's frame for a seat. Seat 0 is DriverEyeLocal, the point the interior
        /// camera, the headset placement, the mirrors and the route advisor all use today. The
        /// passenger's is the driver's eye moved by the seat-to-seat offset: the game gives the co-driver
        /// no camera of its own, and both seats are the same seat model mirrored (on the DAF the two
        /// locators differ by 8 mm in height and 3 mm in depth).
        /// </summary>
        public Vector3 EyeLocalFor(int seat)
        {
            Vector3 eye = DriverEyeLocal;
            return seat == 0 ? eye : eye + (PassengerSeatLocal - DriverSeatLocal);
        }

        /// <summary>
        /// In a headset, ignore the pedals until both triggers have been let go - as on getting in,
        /// where the trigger that pointed at the truck is also the accelerator. The tablet app calls
        /// this when it closes, so the trigger that pressed its last button does not drive off.
        /// </summary>
        public void DisarmVrPedals()
        {
            _vrPedalsArmed = false;
        }

        public TMP_Text Hud;
        public bool InputEnabled = true;
        public bool Placed => _placed;

        /// <summary>
        /// Whether somebody is driving. Not driven, the truck is parked: kinematic, parking brake on,
        /// solid to anyone walking into it and immovable by them. Driven, it runs its physics and
        /// its box ignores the avatar sitting inside it (EtsLayers.SeatedBodyExcludes). True by
        /// default, which is the editor, where there is no avatar and the camera drives; in
        /// Somnium EtsTruckSeat turns it on when the player gets in and off when they get out.
        /// </summary>
        public bool Driven { get; private set; } = true;

        /// <summary>The trailer coupled to the fifth wheel, or null. Set by EtsTrailer.Attach and Detach.</summary>
        public EtsTrailer Trailer { get; set; }

        /// <summary>The body's one collider (built by Configure).</summary>
        public BoxCollider BodyBox => _box;

        /// <summary>The steering wheel's mount inside the cab; it turns with the front wheels, two and a half turns lock to lock.</summary>
        public Transform SteeringWheel;

        /// <summary>The fifth wheel (the chassis locator hook_t), where a trailer's kingpin couples.</summary>
        public Transform FifthWheel;

        /// <summary>Extra mass hanging on the fifth wheel (a trailer's share), added to the rear axle load for grip.</summary>
        public float TrailerLoad;

        private Quaternion _steeringWheelRest;
        private bool _steeringWheelRestKnown;

        /// <summary>Steering wheel degrees per unit of steering input: two and a half turns lock to lock.</summary>
        public const float WheelDegreesPerUnit = 450f;

        /// <summary>
        /// How fast a released wheel returns at the least, as a share of the full turning speed.
        /// The game's autocentring (physics.sii, below) is zero at a standstill; this keeps a
        /// wheel let go in a yard from staying turned for ever.
        /// </summary>
        public const float ReleasedCentreFloor = 0.15f;

        /// <summary>The steering input, -1 (full left lock) to 1 (full right lock).</summary>
        public float SteerInput => _steerInput;

        /// <summary>
        /// Whether a hand holds the steering wheel (EtsSteeringGrab, in a headset). Held, the hands
        /// set the steering outright: the stick and the keys are ignored and nothing centres it.
        /// </summary>
        public bool WheelHeld { get; private set; }

        // 0 while steering is relative (keys, stick), 1 while it is absolute (hands on the wheel);
        // eased over 0.3 s so taking or leaving the wheel at speed does not jerk the front wheels.
        private float _absoluteSteering;

        // The hands have let go and nothing else is steering: the wheel returns as the game's
        // autocentring says, until it is centred or a key or the stick takes over.
        private bool _handsOffCentring;

        public float Rpm = IdleRpm;
        public float Throttle;
        public float Brake;
        public float SteerDegrees;
        public int Gear;
        public bool ParkingBrake = true;

        /// <summary>
        /// 2026-10-14 (headlights): the light switch as the game's "light" control cycles it (0 off, 1 parking lights, 2 dipped
        /// beam), the main beam ("hblight"), and whether the player set them (L / K) - else EtsHeadlights switches them with the
        /// lamps (dusk, rain). The wipers: on, set by hand (Shift+P), and the blades' phase 0 parked .. 1 across (wiper_anim).
        /// On a puppet these come from the wire (EtsPlayerWire TruckLowBeam / TruckHighBeam / TruckParkingLights).
        /// </summary>
        public int Lights;
        public bool HighBeam;
        public bool LightsByHand;
        public bool Wipers;
        public bool WipersByHand;
        public float WiperPhase;
        public Vector3 SpawnPosition;
        public Quaternion SpawnRotation = Quaternion.identity;

        private Rigidbody _body;
        private readonly List<Wheel> _wheels = new List<Wheel>();
        private readonly List<List<Wheel>> _axles = new List<List<Wheel>>();
        private float _steerInput;
        private float _shiftTimer;
        private bool _placed;
        private float _hudTimer;
        private BoxCollider _box;
        private float _solidAgainAt = -1f;
        private float _airborne;
        private bool _vrPedalsArmed;
        private bool _reportBody;

        // The last place the truck stood safely on its wheels, about a second ago at most. The fall
        // recovery goes back here first: the spawn point is the job's pickup yard, which the stream
        // unloads once the truck is 2.6 km away, and most of every job is further than that.
        private Vector3 _lastGoodPosition;
        private float _lastGoodYaw;
        private bool _haveLastGood;
        private float _lastGoodTimer;

        public float ForwardSpeed => _body == null ? 0f : Vector3.Dot(Puppet ? _puppetVelocity : _body.linearVelocity, transform.forward);
        public float SpeedKmh => Mathf.Abs(ForwardSpeed) * 3.6f;

        /// <summary>
        /// Driven from another client (EtsNet): no physics, no keys; pose, steering, engine and
        /// wheels are set from the network every frame by ApplyPuppet. Kinematic and solid, like
        /// a parked truck, so a player on foot cannot walk through it or push it.
        /// </summary>
        public bool Puppet { get; private set; }

        private Vector3 _puppetVelocity;

        /// <summary>A puppet's drawn velocity (ApplyPuppet's); zero otherwise. The Q-A3 contact rule reads it (EtsTruckRig.UpdateVehicleContacts).</summary>
        public Vector3 PuppetVelocity => _puppetVelocity;

        /// <summary>
        /// Whether a puppet's engine is heard (its own EtsTruckAudio). True as built, so a parked puppet idles as the own truck
        /// left parked always has; A may clear it from the wire's "engine on" flag ([net §2.4] int 9 bit 11), and ApplyPuppet
        /// follows the driver's engine by the wire's revs (off under PuppetEngineOffRpm, started rising through PuppetEngineOnRpm).
        /// The local driver's: off until sitting in starts it (StartEngineWhenAsked), off again on getting out.
        /// </summary>
        public bool EngineRunning = true;

        /// <summary>
        /// Time.time when this client last started the engine (Update: the driver sitting in, or in a headset the driver's
        /// first grab of the wheel, pedal or stick); the start sound plays on the same edge (UpdateSounds). The wheel's
        /// starter rumble runs from it (EtsWheelHaptics). Far in the past until then.
        /// </summary>
        public float EngineStartedAt { get; private set; } = -1000f;

        // ------------------------------------------------------------------ the engine start in a headset (lead decision 2026-09-25)
        //
        // On a keyboard the engine starts the moment the driver sits in, as it always has. In a headset it stays off after
        // sitting in until the driver first takes the wheel (EtsSteeringGrab), presses a pedal (once the pedals are armed:
        // the trigger that pointed at the truck does not count) or steers with the stick - whichever comes first - so the
        // hand on the rim feels the whole starter (EtsWheelHaptics.Starter). That start follows the start sound's own
        // timeline (EtsTruckAudio.StartWave): the revs crank, catch at 0.85 s and flare to 1150 by 1.2 s, settle to idle by
        // 1.7 s (StartRevs); the engine drives nothing, and the accelerator does not release the parking brake, until it
        // has caught (StartCatchSeconds, EtsCabState's "starting"). Engine off, the revs run down to 0, the loops are silent
        // (EtsTruckAudio), the dash keeps its electricity (EtsInstruments), cruise control cannot engage (it needs 20 km/h
        // in a forward gear) and the wheel does not vibrate.

        /// <summary>A headset start: how long until the engine has caught and drives (s; EtsCabState's "starting", the sound's flare).</summary>
        public const float StartCatchSeconds = 1.2f;

        /// <summary>A headset start: how long the revs follow the start sound's timeline (s; EtsTruckAudio.StartWave settles by then).</summary>
        public const float StartRevsSeconds = 1.7f;

        /// <summary>
        /// A puppet's engine from the wire's revs (ApplyPuppet): under Off it is off (a running engine turns at 180 rpm and up,
        /// StartRevs from 33 ms in, idle 650); rising through On it starts.
        /// </summary>
        public const float PuppetEngineOffRpm = 60f, PuppetEngineOnRpm = 150f;

        // Why the engine may start now (a headset, after sitting in), null until the driver asks; whether this start follows
        // the start sound's revs and waits for the catch (a headset start); when it has caught; the "engine off" line logged.
        private string _startWhy;
        private bool _startShaped;
        private float _engineCatchAt = -1000f;
        private bool _startWaitLogged;
        private float _puppetRpm = IdleRpm;

        /// <summary>Whether the driver sits in a headset with the engine off, waiting for the first grab, pedal or stick.</summary>
        public bool WaitingForStart => Driven && !Puppet && !EngineRunning && _placed && EtsVr.Present;

        /// <summary>
        /// The driver asks for the engine (EtsSteeringGrab on the first grab of the wheel): in a headset it starts next
        /// frame (Update), with its sound, the dash's start and the wheel's starter rumble. `why` goes in the log line.
        /// Nothing while the engine runs, nobody drives, or another client does.
        /// </summary>
        public void RequestEngineStart(string why)
        {
            if (Puppet || !Driven || EngineRunning || _startWhy != null)
            {
                return;
            }

            _startWhy = string.IsNullOrEmpty(why) ? "the driver" : why;
        }

        /// <summary>
        /// The revs t seconds into a headset start, on EtsTruckAudio.StartWave's timeline: the starter turning the engine
        /// over (up to 180 rpm in 0.1 s, then 180-220 with each compression, 0.5 + 0.5 sin(8t + 1.5t^2)), the catch from
        /// 0.85 s (300 rpm) flaring to 1150 at 1.2 s, then settling to idle (650) by 1.7 s.
        /// </summary>
        public static float StartRevs(float t)
        {
            if (t < 0.85f)
            {
                float compression = 0.5f + 0.5f * Mathf.Sin(8f * t + 1.5f * t * t);
                return Mathf.Clamp01(t / 0.1f) * (180f + 40f * compression);
            }

            float u = t - 0.85f;
            return u < 0.35f ? Mathf.Lerp(300f, 1150f, u / 0.35f) : Mathf.Lerp(1150f, IdleRpm, Mathf.Clamp01((u - 0.35f) / 0.5f));
        }

        /// <summary>Whether the engine can drive: running and, after a headset start, caught (StartCatchSeconds).</summary>
        private bool EngineCaught => EngineRunning && Time.time >= _engineCatchAt;

        /// <summary>A driving key held (W A S D, the arrows, Space): on a keyboard in a headset, a way to start the engine.</summary>
        private static bool DrivingKeyHeld()
        {
            return Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.D) ||
                   Input.GetKey(KeyCode.UpArrow) || Input.GetKey(KeyCode.DownArrow) || Input.GetKey(KeyCode.LeftArrow) || Input.GetKey(KeyCode.RightArrow) ||
                   Input.GetKey(KeyCode.Space);
        }

        /// <summary>
        /// Update, the local driver's truck with the engine off: on a keyboard it starts at once (as it always has); in a
        /// headset once the driver asks - the wheel (RequestEngineStart), a pedal, the stick or a driving key.
        /// </summary>
        private void StartEngineWhenAsked()
        {
            bool headset = EtsVr.Present;

            if (headset && _startWhy == null && InputEnabled)
            {
                if (_vrPedalsArmed && EtsVr.Throttle > 0.05f) _startWhy = "the throttle";
                else if (_vrPedalsArmed && EtsVr.BrakePedal > 0.05f) _startWhy = "the brake pedal";
                else if (EtsVr.Steer != 0f) _startWhy = "the stick";
                else if (DrivingKeyHeld()) _startWhy = "the keyboard";
            }

            if (headset && _startWhy == null)
            {
                if (!_startWaitLogged)
                {
                    _startWaitLogged = true;
                    Debug.Log("[SomniumEuroTruck] Engine: off after sitting in (headset) - it starts on the first grab of the wheel, a pedal or the stick.");
                }

                return;
            }

            EngineRunning = true;
            EngineStartedAt = Time.time;
            _startShaped = headset;
            _engineCatchAt = headset ? Time.time + StartCatchSeconds : Time.time;

            if (headset)
            {
                Debug.Log("[SomniumEuroTruck] Engine: started by " + _startWhy + " (headset).");
            }
        }

        // Shown on the HUD and the VR dash plate while the engine waits (not in the locale).
        private const string StartPrompt = "Engine off - take the wheel or press a pedal to start";

        // ------------------------------------------------------------------ indicators (demo fix 2026-09-23)
        //
        // The shared contract with EtsTruckAudio: Indicator is -1 left, 0 off, +1 right, 2 hazards; IndicatorLit is the
        // lamps' current phase; IndicatorBlink is raised on every lamp on/off edge with the new phase (the relay tick).
        // Driven here: the right thumbstick flicked left/right in a headset, [ ] and F on a keyboard (the game's keys).
        // The same direction again cancels, the other switches sides, and a turn cancels it as the game does
        // (/def/game_data.sii: blinker_auto_off_limit 0.22 and blinker_auto_off_trigger 0.05 of the steering lock).

        /// <summary>The indicator: -1 left, 0 off, +1 right, 2 hazard lights.</summary>
        public int Indicator => _indicator;

        /// <summary>Whether the indicator lamps are lit this moment (they blink at BlinkHz while Indicator is not 0).</summary>
        public bool IndicatorLit => _indicatorLit;

        /// <summary>Raised on every lamp on/off edge with the new state; EtsTruckAudio ticks the relay on it.</summary>
        public event Action<bool> IndicatorBlink;

        /// <summary>
        /// Flashes a second. The game's rate sits in its vehicle flare units (blink_pattern, blink_step_length), which
        /// this world does not read; 1.5 Hz is the legal 90 a minute, half on, half off.
        /// </summary>
        public const float BlinkHz = 1.5f;

        /// <summary>game_data.sii blinker_auto_off_limit: the turn (share of full lock) that arms the self-cancel.</summary>
        public const float BlinkerAutoOffLimit = 0.22f;

        /// <summary>game_data.sii blinker_auto_off_trigger: back within this of centre, an armed indicator switches off.</summary>
        public const float BlinkerAutoOffTrigger = 0.05f;

        private int _indicator;
        private bool _indicatorLit;
        private float _blinkClock;
        private bool _autoOffArmed;
        private bool _stickFlicked;
        private EtsIndicatorLamps _lamps;
        private EtsTrailer _lampTrailer;

        private static UnityEngine.XR.InputDevice _rightHand;
        private static float _nextRightHandSearch;
        private static readonly List<UnityEngine.XR.InputDevice> RightHandFound = new List<UnityEngine.XR.InputDevice>();

        /// <summary>
        /// Sets the indicator outright (-1 left, 0 off, +1 right, 2 hazards): the network for a puppet (EtsNetVehicle,
        /// from EtsPlayerWire.TruckBlinkLeft/Right/Hazard), or anything else. A new state lights the lamps at once.
        /// </summary>
        public void SetIndicator(int state)
        {
            state = state == 2 ? 2 : Math.Sign(state);

            if (state == _indicator)
            {
                return;
            }

            _indicator = state;
            _autoOffArmed = false;
            _blinkClock = 0f;
            SetLit(state != 0);
        }

        /// <summary>The driver's flick or key: the same side again cancels, the other side switches, hazards toggle.</summary>
        public void ToggleIndicator(int side)
        {
            if (side == 2)
            {
                SetIndicator(_indicator == 2 ? 0 : 2);
            }
            else
            {
                SetIndicator(_indicator == side ? 0 : side);
            }
        }

        /// <summary>The indicator as EtsPlayerWire's truck flags (for EtsNet's packing).</summary>
        public int IndicatorWireFlags => _indicator == 2 ? EtsPlayerWire.TruckHazard : _indicator < 0 ? EtsPlayerWire.TruckBlinkLeft : _indicator > 0 ? EtsPlayerWire.TruckBlinkRight : 0;

        private void SetLit(bool lit)
        {
            if (_indicatorLit != lit)
            {
                _indicatorLit = lit;
                ApplyLamps();

                try
                {
                    IndicatorBlink?.Invoke(lit);
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[SomniumEuroTruck] Indicator listener failed: " + e.Message);
                }
            }
            else
            {
                ApplyLamps();
            }
        }

        private void ApplyLamps()
        {
            bool left = _indicatorLit && (_indicator == -1 || _indicator == 2);
            bool right = _indicatorLit && (_indicator == 1 || _indicator == 2);

            if (_lamps == null && (left || right))
            {
                _lamps = new EtsIndicatorLamps();

                try
                {
                    _lamps.BuildTruck(this);
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[SomniumEuroTruck] Indicator lamps could not be built: " + e.Message);
                }
            }

            _lamps?.Set(left, right);

            // The coupled trailer's lamps follow; one uncoupled since the last edge is switched off.
            if (_lampTrailer != Trailer)
            {
                if (_lampTrailer != null)
                {
                    _lampTrailer.SetIndicatorLamps(false, false);
                }

                _lampTrailer = Trailer;
            }

            if (_lampTrailer != null)
            {
                _lampTrailer.SetIndicatorLamps(left, right);
            }
        }

        /// <summary>The right thumbstick (x right, y up = pushed forward), or zero without a right controller.</summary>
        private static Vector2 RightStick()
        {
            if (!_rightHand.isValid)
            {
                if (Time.unscaledTime < _nextRightHandSearch)
                {
                    return Vector2.zero;
                }

                _nextRightHandSearch = Time.unscaledTime + 1f;
                RightHandFound.Clear();
                UnityEngine.XR.InputDevices.GetDevicesWithCharacteristics(UnityEngine.XR.InputDeviceCharacteristics.Controller | UnityEngine.XR.InputDeviceCharacteristics.Right, RightHandFound);

                if (RightHandFound.Count == 0)
                {
                    return Vector2.zero;
                }

                _rightHand = RightHandFound[0];
            }

            return _rightHand.TryGetFeatureValue(UnityEngine.XR.CommonUsages.primary2DAxis, out Vector2 stick) ? stick : Vector2.zero;
        }

        /// <summary>
        /// The driver's switches, read only while the local player drives: the indicators ([ ] F), cruise control (C) and
        /// the trailer (T) on the keyboard - the game's own keys - and in a headset the right thumbstick flicked: left or
        /// right the indicators, up cruise control, down the trailer.
        /// </summary>
        private void ReadIndicatorInput()
        {
            if (Input.GetKeyDown(KeyCode.LeftBracket)) ToggleIndicator(-1);
            if (Input.GetKeyDown(KeyCode.RightBracket)) ToggleIndicator(1);
            if (Input.GetKeyDown(KeyCode.F)) ToggleIndicator(2);
            if (Input.GetKeyDown(KeyCode.C)) ToggleCruise();
            if (Input.GetKeyDown(KeyCode.T)) ToggleTrailer();

            if (!EtsVr.Present)
            {
                _stickFlicked = false;
                return;
            }

            // One flick, one switch: past 0.7 fires, back inside 0.35 (all directions) re-arms. Mostly sideways (x beyond
            // y) is the indicators; mostly up or down (x under half of y, within about 27 degrees of straight) is cruise
            // control or the trailer. A push in between does nothing, so a sloppy sideways flick never drops the trailer.
            Vector2 stick = RightStick();
            float ax = Mathf.Abs(stick.x), ay = Mathf.Abs(stick.y);

            if (!_stickFlicked)
            {
                if (ax > 0.7f && ax > ay)
                {
                    _stickFlicked = true;
                    ToggleIndicator(stick.x < 0f ? -1 : 1);
                }
                else if (ay > 0.7f && ax < ay * 0.5f)
                {
                    _stickFlicked = true;

                    if (stick.y > 0f)
                    {
                        ToggleCruise();
                    }
                    else
                    {
                        ToggleTrailer();
                    }
                }
            }
            else if (ax < 0.35f && ay < 0.35f)
            {
                _stickFlicked = false;
            }
        }

        // ------------------------------------------------------------------ cruise control (2026-09-23)
        //
        // As the game's: the switch (C, the right stick up) sets the target to the speed the truck is doing and holds it
        // with the throttle - easing off lets the engine brake it down - until the brake pedal, the parking brake or the
        // switch again cancels it. The accelerator overrides it while pressed and hands back on release. There is no
        // clutch in this world (the gearbox is automatic), so the game's clutch cancel has nothing to answer.
        //
        // The minimum is not in the game's data: /def/game_data.sii and /def/vehicle/physics.sii name no cruise value
        // (the exe carries only the settings g_cruise_control_grid, g_cruise_control_smart and the key
        // truck.cruise_control); CruiseMinKmh is the brief's 20 km/h.

        /// <summary>The lowest speed cruise control takes (km/h).</summary>
        public const float CruiseMinKmh = 20f;

        /// <summary>Held on but slowed below this (a wall, a jam), it lets go rather than push on (km/h).</summary>
        public const float CruiseDropKmh = 10f;

        /// <summary>Throttle per km/h short of the target, and the hold's integral rate (throttle per km/h per second).</summary>
        public const float CruiseGain = 0.3f;
        public const float CruiseIntegralRate = 0.08f;

        /// <summary>Whether cruise control is holding a speed.</summary>
        public bool CruiseOn => _cruiseOn;

        /// <summary>The speed cruise control holds (km/h), 0 while it is off.</summary>
        public float CruiseKmh => _cruiseOn ? _cruiseKmh : 0f;

        /// <summary>Raised when cruise control goes on (true) or off (false), for a sound or a dash lamp.</summary>
        public event Action<bool> CruiseChanged;

        private bool _cruiseOn;
        private float _cruiseKmh;
        private float _cruiseHold;

        /// <summary>The driver's cruise switch: off when on; on at the present speed when driving forward fast enough.</summary>
        public void ToggleCruise()
        {
            if (Puppet || Spec == null || !_placed)
            {
                return;
            }

            if (_cruiseOn)
            {
                SetCruise(false, "switched off");
                return;
            }

            float kmh = ForwardSpeed * 3.6f;

            if (Gear <= 0 || kmh < CruiseMinKmh)
            {
                Say(CruiseWord() + ": above " + CruiseMinKmh.ToString("0") + " km/h, driving forward");
                Debug.Log("[SomniumEuroTruck] Cruise control refused at " + kmh.ToString("0.0") + " km/h in gear " + GearLabel + " (it takes " + CruiseMinKmh.ToString("0") + " km/h and up, forward).");
                return;
            }

            _cruiseKmh = Mathf.Round(kmh);

            // The hold starts from the pedal the driver had down (at most half), so taking the foot off does not surge.
            _cruiseHold = Mathf.Clamp(Throttle, 0.1f, 0.5f);
            SetCruise(true, null);
        }

        private void SetCruise(bool on, string why)
        {
            if (_cruiseOn == on)
            {
                return;
            }

            _cruiseOn = on;
            Say(on ? CruiseWord() + " " + _cruiseKmh.ToString("0") + " km/h" : CruiseWord() + " off" + (why != null && why != "switched off" ? " (" + why + ")" : ""));
            Debug.Log("[SomniumEuroTruck] Cruise control " + (on ? "on at " + _cruiseKmh.ToString("0") + " km/h" : "off: " + (why ?? "reset")) + ".");

            try
            {
                // The dashboard chime, rising on and falling off (every way in and out passes here, the automatic cancels too).
                EtsTruckAudio.For(this)?.PlayCruise(on);
                CruiseChanged?.Invoke(on);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[SomniumEuroTruck] Cruise control listener failed: " + e.Message);
            }
        }

        /// <summary>Off without a word (a placement, getting out, the network taking the truck).</summary>
        private void DropCruise(string why)
        {
            if (_cruiseOn)
            {
                SetCruise(false, why);
            }
        }

        /// <summary>
        /// Every frame the local driver has the truck: cancel on the brake, the parking brake, a gear that is not a forward
        /// one, the controls taken away (a menu), or the truck slowed right down; otherwise hold the speed. `speed` in m/s.
        /// </summary>
        private void UpdateCruise(float speed)
        {
            if (!_cruiseOn)
            {
                return;
            }

            float kmh = speed * 3.6f;
            string why = !Driven || !InputEnabled ? "controls released" : Brake > 0.01f ? "brake" : ParkingBrake ? "parking brake" : Gear <= 0 ? "out of gear" : kmh < CruiseDropKmh ? "too slow" : null;

            if (why != null)
            {
                SetCruise(false, why);
                return;
            }

            // A proportional push on an integral that finds the throttle the road needs; the integral waits while the
            // driver's own foot is further down (the override), so letting go does not come back to a wound-up pedal,
            // and while the pedal is already flat out on a climb, so the crest is not overshot. (A rough 27 t / 7 t model
            // holds 80 and 30 km/h to 0.1 km/h on the flat; hills are the engine's and the engine brake's to win or lose.)
            float error = _cruiseKmh - kmh;
            float hold = Mathf.Clamp01(_cruiseHold + error * CruiseGain);

            if (Throttle < hold)
            {
                if (hold < 1f || error < 0f)
                {
                    _cruiseHold = Mathf.Clamp01(_cruiseHold + error * CruiseIntegralRate * Time.deltaTime);
                }

                hold = Mathf.Clamp01(_cruiseHold + error * CruiseGain);
                Throttle = hold;
            }
        }

        private static string CruiseWord()
        {
            // The game's legend for its cruise control lamp (adv_hud_lgd_cc, "Cruise Control").
            return EtsLocale.TryWord("adv_hud_lgd_cc", out string word) && !string.IsNullOrEmpty(word) ? word : "Cruise Control";
        }

        // ------------------------------------------------------------------ the trailer: hook and unhook (2026-09-23)
        //
        // The game's T ("Trailer Attach/Detach", o_im_attach). Coupled and (almost) stopped: uncouple, and the trailer stays
        // where it stands, legs down (EtsTrailer.Uncouple). Uncoupled: the parked trailer whose kingpin (its "hook" locator)
        // is nearest the fifth wheel (the chassis locator "hook_t", FifthWheel) is coupled when it is within CoupleReach
        // across the ground, CoupleHeight up or down and CoupleAngle of the truck's heading, and the truck is creeping. The
        // game's simple coupling tolerance is not in its data (hard-coded in the exe); these are chosen to be as forgiving.
        // A job's trailer left standing is still the job's (EtsJobs keeps it and delivers it where it stands).

        /// <summary>The fastest the truck may roll to uncouple (km/h): stopped, to a walking pace's fraction.</summary>
        public const float UncoupleMaxKmh = 2f;

        /// <summary>The fastest the truck may roll to couple (km/h): backing in under the nose.</summary>
        public const float CoupleMaxKmh = 5f;

        /// <summary>Kingpin to fifth wheel across the ground (m), the height difference (m) and the heading difference (degrees) that couple.</summary>
        public const float CoupleReach = 1.5f;
        public const float CoupleHeight = 0.5f;
        public const float CoupleAngle = 45f;

        /// <summary>A parked trailer further than this from the fifth wheel is not "the trailer" of the messages (m).</summary>
        public const float CoupleSearch = 40f;

        /// <summary>Raised when the driver couples (true) or uncouples (false) a trailer, for a sound.</summary>
        public event Action<bool> TrailerHooked;

        /// <summary>The driver's trailer switch (T, the right stick down): uncouple the coupled trailer, or couple the parked one in reach.</summary>
        public void ToggleTrailer()
        {
            if (Puppet || Spec == null || !_placed)
            {
                return;
            }

            float kmh = SpeedKmh;
            EtsTrailer coupled = Trailer;

            if (coupled != null)
            {
                if (kmh > UncoupleMaxKmh)
                {
                    Say("Stop the truck to detach the trailer");
                    Debug.Log("[SomniumEuroTruck] Trailer not detached: the truck is doing " + kmh.ToString("0.0") + " km/h (it must be under " + UncoupleMaxKmh.ToString("0") + ").");
                    return;
                }

                Vector3 at = coupled.transform.position;

                if (!coupled.Uncouple())
                {
                    return;
                }

                DropCruise("trailer detached");
                Say("Trailer detached - parked here");
                Debug.Log("[SomniumEuroTruck] Trailer detached by the driver at " + at.ToString("0.0") + ": parked where it stands (kinematic, legs down)" +
                          (EtsJobs.Instance != null && EtsJobs.Instance.HasJob && EtsJobs.Instance.Trailer == coupled ? "; it carries the current job, which goes on." : "."));
                Hooked(false);
                return;
            }

            EtsTrailer best = NearestParkedTrailer(out float across, out float height, out float angle);

            if (best == null)
            {
                Say("No trailer to attach here");
                Debug.Log("[SomniumEuroTruck] Trailer not attached: no parked trailer within " + CoupleSearch.ToString("0") + " m of the fifth wheel (" + EtsTrailer.ParkedTrailers.Count + " parked in all).");
                return;
            }

            string refusal = kmh > CoupleMaxKmh ? "Slow down to attach the trailer"
                : across > CoupleReach ? "Too far from the trailer (" + across.ToString("0.0") + " m) - back the fifth wheel under it"
                : height > CoupleHeight ? "The fifth wheel is " + height.ToString("0.0") + " m off the kingpin's height"
                : angle > CoupleAngle ? "Line up with the trailer (" + angle.ToString("0") + " degrees off)"
                : null;

            string measured = "kingpin " + across.ToString("0.00") + " m from the fifth wheel across, " + height.ToString("0.00") + " m in height, " + angle.ToString("0") + " degrees off line, at " + kmh.ToString("0.0") + " km/h";

            if (refusal != null)
            {
                Say(refusal);
                Debug.Log("[SomniumEuroTruck] Trailer not attached: " + measured + " (couples within " + CoupleReach.ToString("0.0") + " m, " + CoupleHeight.ToString("0.0") + " m, " + CoupleAngle.ToString("0") + " degrees, under " + CoupleMaxKmh.ToString("0") + " km/h).");
                return;
            }

            best.Couple(this, FifthWheel != null ? FifthWheel : transform);
            Say("Trailer attached");
            Debug.Log("[SomniumEuroTruck] Trailer attached by the driver: " + measured + (EtsJobs.Instance != null && EtsJobs.Instance.HasJob && EtsJobs.Instance.Trailer == best ? "; the job goes on." : "."));
            Hooked(true);
        }

        /// <summary>The parked trailer (EtsTrailer.ParkedTrailers, not puppets) whose kingpin is nearest the fifth wheel within CoupleSearch, or null.</summary>
        private EtsTrailer NearestParkedTrailer(out float across, out float height, out float angle)
        {
            across = height = angle = float.MaxValue;
            EtsTrailer best = null;
            IReadOnlyList<EtsTrailer> parked = EtsTrailer.ParkedTrailers;

            for (int i = 0; i < parked.Count; i++)
            {
                EtsTrailer t = parked[i];

                if (t == null || t.IsPuppet || t.Attached || !t.MeasureCoupling(this, out float a, out float h, out float g) || a > CoupleSearch || a >= across)
                {
                    continue;
                }

                best = t;
                across = a;
                height = h;
                angle = g;
            }

            return best;
        }

        private void Hooked(bool coupled)
        {
            // The fifth wheel's jaw: the gearbox's clunk is the nearest sound the truck has.
            try
            {
                EtsTruckAudio.For(this)?.PlayGearChange();
                TrailerHooked?.Invoke(coupled);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[SomniumEuroTruck] Trailer hook listener failed: " + e.Message);
            }
        }

        // ------------------------------------------------------------------ the driver's notes (HUD, VR dash plate)

        /// <summary>Seconds a message (Say) stays up.</summary>
        public const float NoticeSeconds = 4f;

        private string _notice;
        private float _noticeUntil;
        private bool _couplePrompt;
        private float _nextPromptLook;
        private string _dashNote = "";
        private bool _dashNoteCruise;
        private float _dashNoteKmh = -1f;

        /// <summary>
        /// What the driver should read now, or null: the last message from the switches (cruise control, the trailer) for
        /// NoticeSeconds, else - with no trailer coupled and a parked one in reach - the game's prompt "Attach trailer", else
        /// in a headset with the engine still off, how to start it (StartPrompt).
        /// The HUD shows it on its first line; the VR dash plate is meant to show it too (EtsVrDash).
        /// </summary>
        public string Notice => _notice != null && Time.time < _noticeUntil ? _notice : _couplePrompt ? CouplePrompt : WaitingForStart ? StartPrompt : null;

        /// <summary>The first line's extra for the VR dash plate: "   Cruise 80" while cruise control holds, else empty. No allocation per call.</summary>
        public string DashNote
        {
            get
            {
                if (_dashNoteCruise != _cruiseOn || (_cruiseOn && _dashNoteKmh != _cruiseKmh))
                {
                    _dashNoteCruise = _cruiseOn;
                    _dashNoteKmh = _cruiseKmh;
                    _dashNote = _cruiseOn ? "   Cruise " + _cruiseKmh.ToString("0") : "";
                }

                return _dashNote;
            }
        }

        private static string s_couplePrompt;
        private static bool s_couplePromptVr;

        private static string CouplePrompt
        {
            get
            {
                // The game's overlay label (overlay_att_trailer, "Attach trailer") and its key; the stick in a headset. Kept
                // once the locale has been read (before that the English stands in and is asked again next time).
                bool vr = EtsVr.Present;

                if (s_couplePrompt == null || s_couplePromptVr != vr)
                {
                    bool found = EtsLocale.TryWord("overlay_att_trailer", out string w) && !string.IsNullOrEmpty(w);
                    string prompt = (found ? w : "Attach trailer") + (vr ? ": right stick down" : ": T");

                    if (!found)
                    {
                        return prompt;
                    }

                    s_couplePrompt = prompt;
                    s_couplePromptVr = vr;
                }

                return s_couplePrompt;
            }
        }

        internal void Say(string text)
        {
            _notice = text;
            _noticeUntil = Time.time + NoticeSeconds;
            _hudTimer = 1f;
        }

        // ------------------------------------------------------------------ damage's malfunctions (EtsDamage, 2026-10-04)

        // Until when a stalled engine stays off (it may restart after), and until when nothing drives (a slipping gearbox).
        private float _stallUntil = -1f;
        private float _driveCutUntil = -1f;

        /// <summary>
        /// EtsDamage: a damaged engine stalls (off for at least `seconds`; a keyboard restarts it after that, a headset waits
        /// for the wheel, a pedal or the stick as at the start) or a damaged gearbox slips (no drive for `seconds`), with the
        /// game's message (engine_malfunction).
        /// </summary>
        internal void Malfunction(bool engine, float seconds, string message)
        {
            if (Puppet || !Driven)
            {
                return;
            }

            if (engine)
            {
                EngineRunning = false;
                _startWhy = null;
                _startWaitLogged = false;
                _stallUntil = Time.time + seconds;
                DropCruise("engine stalled");
            }

            _driveCutUntil = Mathf.Max(_driveCutUntil, Time.time + seconds);

            if (!string.IsNullOrEmpty(message))
            {
                Say(message);
            }
        }

        /// <summary>Four times a second while the local driver has no trailer and one is parked: whether it is in reach (the prompt).</summary>
        private void LookForCoupling()
        {
            if (Trailer != null || EtsTrailer.ParkedTrailers.Count == 0 || !Driven)
            {
                _couplePrompt = false;
                return;
            }

            if (Time.time < _nextPromptLook)
            {
                return;
            }

            _nextPromptLook = Time.time + 0.25f;
            _couplePrompt = NearestParkedTrailer(out float across, out float height, out float angle) != null &&
                            across <= CoupleReach && height <= CoupleHeight && angle <= CoupleAngle && SpeedKmh <= CoupleMaxKmh;
        }

        /// <summary>The blink clock and, when this client steers, the game's self-cancel after the turn.</summary>
        private void UpdateIndicators(bool local)
        {
            if (_indicator == 0)
            {
                if (_lampTrailer != Trailer)
                {
                    ApplyLamps();
                }

                return;
            }

            if (local && _indicator != 2)
            {
                float into = _steerInput * _indicator;

                if (into >= BlinkerAutoOffLimit)
                {
                    _autoOffArmed = true;
                }
                else if (_autoOffArmed && Mathf.Abs(_steerInput) <= BlinkerAutoOffTrigger)
                {
                    SetIndicator(0);
                    return;
                }
            }

            _blinkClock = (_blinkClock + Time.deltaTime * BlinkHz) % 1f;
            bool lit = _blinkClock < 0.5f;

            if (lit != _indicatorLit || _lampTrailer != Trailer)
            {
                SetLit(lit);
            }
        }

        // ------------------------------------------------------------------ sound hooks (EtsTruckAudio's contract)

        private int _sfxGear;
        private bool _sfxParkingBrake;
        private bool _sfxEngine;
        private int _sfxPlacements = -1;

        /// <summary>
        /// Gear changes, the parking brake set or released, the engine started or stopped: each played once, on its
        /// edge, through the truck's own EtsTruckAudio. A placement (spawn, respawn, a job) resets all three quietly.
        /// </summary>
        private void UpdateSounds()
        {
            if (_sfxPlacements != Placements)
            {
                _sfxPlacements = Placements;
                _sfxGear = Gear;
                _sfxParkingBrake = ParkingBrake;
                _sfxEngine = EngineRunning;
                return;
            }

            if (_sfxGear == Gear && _sfxParkingBrake == ParkingBrake && _sfxEngine == EngineRunning)
            {
                return;
            }

            EtsTruckAudio audio = EtsTruckAudio.For(this);

            try
            {
                if (_sfxEngine != EngineRunning)
                {
                    if (EngineRunning) audio?.PlayEngineStart();
                    else audio?.PlayEngineStop();
                }

                if (_sfxParkingBrake != ParkingBrake)
                {
                    audio?.PlayAirBrake(ParkingBrake);
                }

                if (_sfxGear != Gear && EngineRunning)
                {
                    audio?.PlayGearChange();
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[SomniumEuroTruck] Truck sound hook failed: " + e.Message);
            }

            _sfxGear = Gear;
            _sfxParkingBrake = ParkingBrake;
            _sfxEngine = EngineRunning;
        }

        public void SetPuppet(bool on)
        {
            if (Puppet == on)
            {
                return;
            }

            Puppet = on;
            _puppetVelocity = Vector3.zero;

            if (on)
            {
                DropCruise("driven from another client");
                _couplePrompt = false;
            }

            // A puppet idles as it always has; the network may clear it ([net §2.4] int 9 bit 11), and so do its revs (ApplyPuppet).
            if (on)
            {
                EngineRunning = true;
                _puppetRpm = IdleRpm;
            }

            if (_body == null)
            {
                return;
            }

            if (on)
            {
                if (!_body.isKinematic)
                {
                    _body.linearVelocity = Vector3.zero;
                    _body.angularVelocity = Vector3.zero;
                }

                _body.isKinematic = true;

                // Placed by transform every frame; Rigidbody interpolation would draw it a step behind.
                _body.interpolation = RigidbodyInterpolation.None;
                Driven = false;
                InputEnabled = false;
                WheelHeld = false;
                _solidAgainAt = -1f;
                SetBoxExcludes(EtsLayers.BodyExcludes);
            }
            else
            {
                _body.interpolation = RigidbodyInterpolation.Interpolate;
                Throttle = 0f;
                Brake = 0f;
                Rpm = IdleRpm;
            }
        }

        /// <summary>One frame of the truck as the network describes it (EtsNet.Puppet).</summary>
        public void ApplyPuppet(Vector3 position, Quaternion rotation, Vector3 velocity, float steer, float rpm, float throttle, float brake, int gear, bool parkingBrake, float dt)
        {
            if (!Puppet || Spec == null)
            {
                return;
            }

            transform.SetPositionAndRotation(position, rotation);
            _puppetVelocity = velocity;
            _steerInput = steer;
            Rpm = rpm;

            // The driver's engine as the revs on the wire tell it (whatever else reads the wire's engine bit): a running engine
            // never turns under PuppetEngineOffRpm, so revs below it are an engine that is off (a headset driver who has not
            // taken the wheel yet), and revs rising through PuppetEngineOnRpm are its start - the start sound plays on that
            // edge here too (UpdateSounds). Steady revs change nothing, so a parked puppet idles as it always has.
            if (rpm < PuppetEngineOffRpm)
            {
                EngineRunning = false;
            }
            else if (!EngineRunning && rpm > PuppetEngineOnRpm && _puppetRpm <= PuppetEngineOnRpm)
            {
                EngineRunning = true;
            }

            _puppetRpm = rpm;
            Throttle = throttle;
            Brake = brake;
            Gear = gear;
            ParkingBrake = parkingBrake;
            _placed = true;

            float kmh = SpeedKmh;
            float sensitivity = Mathf.Lerp(Spec.SteerSensitivityAt0, Spec.SteerSensitivityAt100, Mathf.Clamp01(kmh / 100f));
            SteerDegrees = _steerInput * Spec.MaxSteerAngle * sensitivity;
            Vector3 up = transform.up;
            float forwardSpeed = ForwardSpeed;

            // The wheels as FixedUpdate hangs them: at the ground under them (cast here, on this
            // client's own road), turned by the steering, spun by the speed.
            foreach (Wheel wheel in _wheels)
            {
                wheel.SteerDegrees = wheel.Steered ? SteerDegrees : 0f;
                wheel.SpinDegrees += forwardSpeed / wheel.Radius * Mathf.Rad2Deg * dt;
                wheel.Grounded = CastWheel(transform.TransformPoint(wheel.LocalHub), up, wheel.Radius, wheel.RestLength + wheel.Travel, out wheel.Hit);

                if (wheel.Visual == null)
                {
                    continue;
                }

                float drop = wheel.Grounded ? Mathf.Clamp(wheel.Hit.distance - wheel.Radius, -wheel.Travel, wheel.Travel) : wheel.Travel;
                wheel.Visual.localPosition = wheel.LocalHub - Vector3.up * drop;
                wheel.Visual.localRotation = Quaternion.Euler(0f, wheel.SteerDegrees, 0f) * wheel.VisualRest * Quaternion.Euler(wheel.SpinDegrees * wheel.SpinSign, 0f, 0f);
            }
        }

        public string GearLabel => Gear > 0 ? Gear.ToString() : Gear < 0 ? "R" + (-Gear) : "N";

        /// <summary>The key line under the speed. Once the app is on the client's tablet R is the client's (see Update).</summary>
        private const string KeysHelp = "\nW/S drive, A/D steer, Space brake, P parking brake, C cruise control, T trailer, [ ] indicators, F hazards, R respawn, 1/2 camera";
        private const string KeysHelpTablet = "\nW/S drive, A/D steer, Space brake, P parking brake, C cruise control, T trailer, [ ] indicators, F hazards, 1/2 camera";

        /// <summary>
        /// The driver's eye in the truck's frame, as the interior view places it: the interior
        /// model's origin plus the camera unit's head offset. A headset's eyes are put here.
        /// </summary>
        public Vector3 DriverEyeLocal
        {
            get
            {
                Vector3 eye = EyeLocal;

                if (Spec != null && Spec.HeadOffset != null && Spec.HeadOffset.Length >= 3)
                {
                    eye += EtsModelBuilder.ToUnity(Spec.HeadOffset[0], Spec.HeadOffset[1], Spec.HeadOffset[2]);
                }

                return eye;
            }
        }

        private float GearRatio => Gear > 0 && Gear <= Spec.ForwardRatios.Length ? Spec.ForwardRatios[Gear - 1]
            : Gear < 0 && -Gear <= Spec.ReverseRatios.Length ? Spec.ReverseRatios[-Gear - 1] : 0f;

        /// <summary>
        /// Sets the truck up from its definitions and the wheel locators the model builder made.
        /// The wheel visuals are the locator transforms (tyre and rim hang under them); their local
        /// positions are the hubs, and their z groups them into axles, front first, matching the
        /// order of the chassis definition's per-axle arrays.
        /// </summary>
        public void Configure(EtsVehicleSpec spec, IEnumerable<KeyValuePair<string, Transform>> wheelLocators, float wheelRadius, Bounds localBounds)
        {
            EtsFines.Ensure();   // the speeding-fines referee (one per visit; it judges only the local player's own driven truck)
            Spec = spec;
            _wheels.Clear();
            _axles.Clear();

            foreach (KeyValuePair<string, Transform> entry in wheelLocators)
            {
                if (!entry.Key.StartsWith("wheel_", StringComparison.Ordinal) || entry.Value == null) continue;

                var wheel = new Wheel
                {
                    Name = entry.Key,
                    Visual = entry.Value,
                    VisualRest = entry.Value.localRotation,
                    LocalHub = transform.InverseTransformPoint(entry.Value.position),
                    Radius = wheelRadius
                };

                wheel.SpinSign = Vector3.Dot(wheel.VisualRest * Vector3.right, Vector3.right) >= 0f ? 1f : -1f;
                _wheels.Add(wheel);
            }

            // Axles: wheels within half a metre of each other along z, front (largest z) first.
            _wheels.Sort((a, b) => b.LocalHub.z.CompareTo(a.LocalHub.z));

            foreach (Wheel wheel in _wheels)
            {
                List<Wheel> axle = _axles.Count > 0 && Mathf.Abs(_axles[_axles.Count - 1][0].LocalHub.z - wheel.LocalHub.z) < 0.5f ? _axles[_axles.Count - 1] : null;

                if (axle == null)
                {
                    axle = new List<Wheel>();
                    _axles.Add(axle);
                }

                axle.Add(wheel);
                wheel.Axle = _axles.Count - 1;
            }

            float totalMass = Mathf.Max(1000f, spec.KerbMass);
            var centre = Vector3.zero;

            for (int a = 0; a < _axles.Count; a++)
            {
                float axleWeight = a < spec.AxleKerbWeights.Length ? spec.AxleKerbWeights[a] : totalMass / _axles.Count;
                bool powered = a < spec.PoweredAxles.Length ? spec.PoweredAxles[a] : a == _axles.Count - 1;
                float residual = a < spec.ResidualTravel.Length ? spec.ResidualTravel[a] : 0.1f;

                foreach (Wheel wheel in _axles[a])
                {
                    float share = axleWeight / _axles[a].Count;
                    wheel.Spring = share * 9.81f / Sag;
                    wheel.Damper = 2f * Mathf.Sqrt(wheel.Spring * share) * DampingRatio;
                    wheel.RestLength = wheel.Radius + Sag;
                    wheel.Travel = Sag + residual;
                    wheel.Steered = a == 0;
                    wheel.Powered = powered;
                    centre += new Vector3(0f, 0f, wheel.LocalHub.z) * share;
                }
            }

            _body = GetComponent<Rigidbody>();

            if (_body == null)
            {
                _body = gameObject.AddComponent<Rigidbody>();
            }

            _body.mass = totalMass;
            _body.linearDamping = 0f;
            _body.angularDamping = 0.8f;
            _body.interpolation = RigidbodyInterpolation.Interpolate;
            _body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            _body.isKinematic = true;
            _body.centerOfMass = new Vector3(0f, 1.1f, centre.z / totalMass);

            // The body's own collider: the model's box, lifted so the suspension, not the box,
            // meets the ground. The truck is on the Vehicle layer, which the wheel rays skip, and
            // the box is told to ignore the drive surface itself (see EtsLayers.BodyExcludes).
            var box = GetComponent<BoxCollider>();

            if (box == null)
            {
                box = gameObject.AddComponent<BoxCollider>();
            }

            float bottom = Mathf.Max(localBounds.min.y, 0.6f);
            box.center = new Vector3(localBounds.center.x, (bottom + localBounds.max.y) * 0.5f, localBounds.center.z);
            box.size = new Vector3(Mathf.Max(0.5f, localBounds.size.x), Mathf.Max(0.5f, localBounds.max.y - bottom), Mathf.Max(0.5f, localBounds.size.z));
            EtsLayers.SetRecursively(transform, EtsLayers.Vehicle);
            _box = box;
            box.excludeLayers = Driven ? EtsLayers.SeatedBodyExcludes : EtsLayers.BodyExcludes;

            // The own truck stands with its engine off until somebody drives it (Update starts it, with its sound);
            // a puppet idles (SetPuppet).
            if (!Puppet)
            {
                EngineRunning = false;
            }
        }

        /// <summary>
        /// Somebody got in (true) or out (false). Out: parking brake, pedals released, the truck and
        /// its trailer frozen where they stand. The box stays see-through to the avatar for a second
        /// more, so the capsule the Bridge is still lifting out of the cab is not shoved against a
        /// solid truck on its way. In: the physics runs again (once the truck is on the ground).
        /// </summary>
        public void SetDriven(bool on)
        {
            // Somebody here got in: whatever the network was drawing, the truck is this client's now.
            if (on && Puppet)
            {
                SetPuppet(false);
            }

            // Parked by a player on foot (EtsTruckSeat.Update) while another client drives: the
            // puppet is already kinematic and must not be set down on this client's ground.
            if (!on && Puppet)
            {
                Driven = false;
                return;
            }

            Driven = on;

            if (on)
            {
                // In a headset the trigger that pointed at the truck is also the accelerator; the
                // pedals wait until both triggers have been let go.
                _vrPedalsArmed = false;

                // In a headset the engine (off since getting out, or since it was built) waits for the driver's first grab
                // of the wheel, pedal or stick (StartEngineWhenAsked).
                _startWhy = null;
                _startWaitLogged = false;

                // Likewise the right stick (snap turn on foot): held while getting in, it must come back to the middle
                // before a flick switches the indicators, cruise control or the trailer.
                _stickFlicked = true;
                _reportBody = true;
                _solidAgainAt = -1f;
                SetBoxExcludes(EtsLayers.SeatedBodyExcludes);

                if (_placed && _body != null && _body.isKinematic)
                {
                    _body.isKinematic = false;
                    _body.linearVelocity = Vector3.zero;
                    _body.angularVelocity = Vector3.zero;
                }

                Trailer?.Follow(this);
                return;
            }

            DropCruise("the driver got out");
            _couplePrompt = false;
            ParkingBrake = true;
            Throttle = 0f;
            Brake = 0f;
            WheelHeld = false;
            _handsOffCentring = false;
            _solidAgainAt = Time.time + 1f;

            // Out of the cab: the engine is switched off (UpdateSounds plays it); getting back in starts it (in a headset on
            // the first grab of the wheel, pedal or stick).
            EngineRunning = false;
            _startWhy = null;
            _startShaped = false;

            if (_body != null && !_body.isKinematic)
            {
                _body.linearVelocity = Vector3.zero;
                _body.angularVelocity = Vector3.zero;
                _body.isKinematic = true;
            }

            // Parked means standing on its wheels. Left mid-jump, tipped, or with nothing under it,
            // freezing it where it is would leave it hanging (the client log: got out while it was
            // tumbling, and it stayed 9 m up). Set it down upright on the ground below instead,
            // without making that its respawn point.
            if (_placed && (transform.up.y < 0.8f || !AnyWheelGrounded()))
            {
                PlaceOnGround(transform.position, transform.eulerAngles.y, false);
            }

            Trailer?.Follow(this);
        }

        private bool AnyWheelGrounded()
        {
            foreach (Wheel wheel in _wheels)
            {
                if (wheel.Grounded)
                {
                    return true;
                }
            }

            return false;
        }

        private void SetBoxExcludes(int layers)
        {
            if (_box != null)
            {
                _box.excludeLayers = layers;
            }

            Trailer?.SetBoxExcludes(layers);
        }

        /// <summary>How many times the truck has been set down (start, jobs, respawns); EtsTruckSeat watches it.</summary>
        public int Placements { get; private set; }

        /// <summary>Drops the truck onto whatever is under a position and lets the physics run.</summary>
        public bool PlaceOnGround(Vector3 position, float yawDegrees)
        {
            return PlaceOnGround(position, yawDegrees, true);
        }

        /// <summary>How far above the asked height the first ground ray starts (Phase 2 integration): under a hall's roof, below it.</summary>
        private const float PlaceRayLow = 3f;

        /// <summary>
        /// As above; remember is whether this becomes the spot Respawn returns to. A coupled
        /// trailer is brought along: left behind on its locked joint, it was yanked back across
        /// the gap and threw the truck about.
        ///
        /// The ground is looked for from 3 m above the asked height first, and only then from 60 m up (the old
        /// ray). Every caller passes a height that is the map's own, to centimetres (a yard stand, a depot bay, the
        /// last good spot, the truck's own position): F's sweep of all 72 headquarters yards (probe 2100,
        /// S/phase2/F/unity/out2100_sweep.tsv) found the first stand inside garage hall prefab 280, whose roof pieces
        /// at +6.92 and +7.38 m are on Default, a drive-surface layer. The 60 m ray took the roof, and the chosen
        /// truck was parked 7.4 m up at every yard. From 3 m it meets the hall's floor (-0.03 m). The 60 m ray stays
        /// as the fallback, so a caller whose height is below the ground (a truck that fell through) is still found.
        /// </summary>
        public bool PlaceOnGround(Vector3 position, float yawDegrees, bool remember)
        {
            var rotation = Quaternion.Euler(0f, yawDegrees, 0f);

            if (Physics.Raycast(position + Vector3.up * PlaceRayLow, Vector3.down, out RaycastHit hit, 300f, EtsLayers.GroundMask, QueryTriggerInteraction.Ignore)
                || Physics.Raycast(position + Vector3.up * 60f, Vector3.down, out hit, 300f, EtsLayers.GroundMask, QueryTriggerInteraction.Ignore))
            {
                position = hit.point + Vector3.up * 0.08f;
            }
            else
            {
                return false;
            }

            if (remember)
            {
                SpawnPosition = position;
                SpawnRotation = rotation;

                // A new spawn point (a job's depot): a safe spot from before it is somewhere else.
                _haveLastGood = false;
            }

            if (!_body.isKinematic)
            {
                _body.linearVelocity = Vector3.zero;
                _body.angularVelocity = Vector3.zero;
            }

            _body.isKinematic = true;
            transform.SetPositionAndRotation(position, rotation);

            // Parked unless somebody is at the wheel; either way on the parking brake, which the
            // accelerator releases. It used to come off here, and with the gearbox in neutral and
            // nobody on the pedals the truck free-rolled through every menu.
            _body.isKinematic = !Driven;
            _airborne = 0f;
            DropCruise("the truck was set down");
            Gear = 0;

            // A driver waiting in a headset with the engine off keeps it at 0 (the wire carries the revs: a jump to idle
            // and back would read on other clients as a start and a stop, ApplyPuppet).
            Rpm = Driven && !EngineRunning && !Puppet && EtsVr.Present ? 0f : IdleRpm;
            ParkingBrake = true;
            _placed = true;
            Placements++;
            Trailer?.Recouple();
            return true;
        }

        public void Respawn()
        {
            if (!PlaceOnGround(SpawnPosition, SpawnRotation.eulerAngles.y) && _haveLastGood)
            {
                // The spawn point's ground has been unloaded (it is far behind): the last safe spot.
                PlaceOnGround(_lastGoodPosition, _lastGoodYaw, false);
            }
        }

        private void Update()
        {
            if (_solidAgainAt > 0f && Time.time >= _solidAgainAt)
            {
                _solidAgainAt = -1f;

                if (!Driven)
                {
                    SetBoxExcludes(EtsLayers.BodyExcludes);
                }
            }

            if (Spec == null || !_placed)
            {
                return;
            }

            // Driven elsewhere: the network set _steerInput this frame (EtsNet runs first); only
            // the steering wheel in the cab is left to turn. No keys, no pedals, no HUD.
            if (Puppet)
            {
                ApplySteeringWheel();
                UpdateIndicators(false);
                UpdateSounds();
                return;
            }

            // Somebody at the wheel (the local driver, or the editor's camera): the engine runs - at once on a keyboard, in a
            // headset from the driver's first grab of the wheel, pedal or stick (StartEngineWhenAsked).
            if (Driven && !EngineRunning && Time.time >= _stallUntil)
            {
                StartEngineWhenAsked();
            }

            // The own truck's damage: the career's figures, the malfunctions, the HUD's damage note (EtsDamage).
            if (Driven)
            {
                EtsDamage.Tick(this);
            }

            float steer = 0f, drive = 0f;

            if (InputEnabled)
            {
                if (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow)) steer -= 1f;
                if (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow)) steer += 1f;
                if (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow)) drive += 1f;
                if (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow)) drive -= 1f;
                bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
                if (Input.GetKeyDown(KeyCode.P) && !shift) ParkingBrake = !ParkingBrake;

                // 2026-10-14 (headlights): the game's keys (controls.sii: "light" keyboard.l cycles off / parking / dipped,
                // "hblight" keyboard.k the main beam, "wipers" keyboard.p - Shift+P here, P being this world's parking brake).
                if (Input.GetKeyDown(KeyCode.L))
                {
                    Lights = (Lights + 1) % 3;
                    LightsByHand = true;
                    if (Lights < 2) HighBeam = false;
                }

                if (Input.GetKeyDown(KeyCode.K))
                {
                    LightsByHand = true;
                    HighBeam = !HighBeam;
                    if (HighBeam && Lights < 2) Lights = 2;
                }

                if (Input.GetKeyDown(KeyCode.P) && shift)
                {
                    WipersByHand = true;
                    Wipers = !Wipers;
                }

                // R is the client's own key once the app is on the client's tablet (tablet §4.9,
                // PLAN §6; EtsTabletLink.KeysRetired: new flow AND linked), and the truck's respawn
                // is then on the tablet's Drive page. Without a tablet, and always with the new flow
                // off, R respawns as today.
                if (Input.GetKeyDown(KeyCode.R) && !EtsTabletLink.KeysRetired) Respawn();
                Brake = Input.GetKey(KeyCode.Space) ? 1f : 0f;

                // In a headset: triggers for the pedals, the left stick for the wheel (EtsVr).
                // The left trigger is the S key, analogue: brakes while rolling, reverses from a stop.
                if (EtsVr.Present)
                {
                    if (!_vrPedalsArmed)
                    {
                        _vrPedalsArmed = EtsVr.Throttle < 0.05f && EtsVr.BrakePedal < 0.05f;
                    }

                    steer = Mathf.Clamp(steer + EtsVr.Steer, -1f, 1f);

                    if (_vrPedalsArmed)
                    {
                        drive = Mathf.Clamp(drive + EtsVr.Throttle - EtsVr.BrakePedal, -1f, 1f);
                    }

                    if (EtsVr.ParkingBrakePressed) ParkingBrake = !ParkingBrake;
                }

                if (Driven)
                {
                    ReadIndicatorInput();
                }
            }

            // Steering follows the keys at the game's steering speed and centres itself. Held in a
            // headset (EtsSteeringGrab), the hands have set it already this frame or last and it is
            // left alone. Let go, it returns by the game's autocentring until something else steers.
            float steerRate = Spec.SteerSpeedCoefficient * 0.6f;

            if (WheelHeld)
            {
                // The hands have it.
            }
            else if (_handsOffCentring && steer == 0f)
            {
                _steerInput = Mathf.MoveTowards(_steerInput, 0f, steerRate * AutoCentreShare() * Time.deltaTime);
                _handsOffCentring = _steerInput != 0f;
            }
            else
            {
                _handsOffCentring = false;
                _steerInput = Mathf.MoveTowards(_steerInput, steer, steerRate * Time.deltaTime);
            }

            ApplySteeringWheel();
            UpdateIndicators(Driven);

            // Drive: forward key throttles in a forward gear, brakes while rolling backward;
            // the back key brakes while rolling forward and reverses from a standstill.
            float speed = ForwardSpeed;
            Throttle = 0f;

            if (drive > 0f)
            {
                if (speed < -0.3f) Brake = 1f;
                else
                {
                    if (Gear <= 0) { Gear = 1; _shiftTimer = 0f; }
                    Throttle = drive;

                    // The accelerator releases the parking brake - once the engine can pull (a headset start: after the catch),
                    // so a truck on a slope does not roll while the starter turns.
                    if (EngineCaught) ParkingBrake = false;
                }
            }
            else if (drive < 0f)
            {
                if (speed > 0.3f) Brake = 1f;
                else
                {
                    if (Gear >= 0) { Gear = -1; _shiftTimer = 0f; }
                    Throttle = -drive;
                    if (EngineCaught) ParkingBrake = false;
                }
            }

            // Cruise control holds its speed over whatever the pedals asked (the accelerator still overrides it), and the
            // coupling prompt is looked for.
            UpdateCruise(speed);
            LookForCoupling();
            UpdateSounds();

            if (Hud != null && (_hudTimer += Time.deltaTime) > 0.1f)
            {
                _hudTimer = 0f;
                string blinker = _indicator == 0 ? "" : _indicator == 2 ? (_indicatorLit ? "   << HAZARD >>" : "   .. HAZARD ..") : _indicator < 0 ? (_indicatorLit ? "   <<" : "   ..") : (_indicatorLit ? "   >>" : "   ..");
                string notice = Notice;

                // One line of state (EtsJobs keeps the first line and writes its own under it): cruise control's speed and
                // the driver's last message or the coupling prompt ride on it.
                Hud.text = Mathf.RoundToInt(SpeedKmh).ToString().PadLeft(3) + " km/h   gear " + GearLabel + "   " + Mathf.RoundToInt(Rpm / 10f) * 10 + " rpm" +
                           (ParkingBrake ? "   PARKING BRAKE" : "") + (_cruiseOn ? "   " + CruiseWord().ToUpperInvariant() + " " + _cruiseKmh.ToString("0") + " km/h" : "") + blinker +
                           (EtsDamage.IsOwn(this) ? EtsDamage.HudNote : "") +
                           (notice != null ? "   - " + notice : "") +
                           (EtsTabletLink.KeysRetired ? KeysHelpTablet : KeysHelp) + (Application.isEditor ? ", F free camera" : "");
            }
        }

        /// <summary>
        /// A hand holds the wheel at this steering input (-1..1). The wheel model is turned at once,
        /// so it stays under the hand in the frame the hand moved (EtsSteeringGrab calls this from
        /// the seat's LateUpdate, after this component's Update has already drawn the wheel).
        /// </summary>
        public void HoldWheel(float input)
        {
            WheelHeld = true;
            _handsOffCentring = false;
            _steerInput = Mathf.Clamp(input, -1f, 1f);
            ApplySteeringWheel();
        }

        /// <summary>The last hand has let go: the wheel returns by the autocentring from here.</summary>
        public void ReleaseWheel()
        {
            if (!WheelHeld)
            {
                return;
            }

            WheelHeld = false;
            _handsOffCentring = true;
        }

        /// <summary>
        /// The steering wheel's hub and its unturned rotation in the world: +Y the axle toward the
        /// driver, +Z the top of the rim, +X the driver's right. False when there is no wheel.
        /// </summary>
        public bool TryGetSteeringWheelFrame(out Vector3 hub, out Quaternion rest)
        {
            hub = default;
            rest = Quaternion.identity;

            if (SteeringWheel == null)
            {
                return false;
            }

            if (!_steeringWheelRestKnown)
            {
                _steeringWheelRest = SteeringWheel.localRotation;
                _steeringWheelRestKnown = true;
            }

            Transform parent = SteeringWheel.parent;
            hub = SteeringWheel.position;
            rest = (parent != null ? parent.rotation : Quaternion.identity) * _steeringWheelRest;
            return true;
        }

        private void ApplySteeringWheel()
        {
            if (SteeringWheel == null)
            {
                return;
            }

            if (!_steeringWheelRestKnown)
            {
                _steeringWheelRest = SteeringWheel.localRotation;
                _steeringWheelRestKnown = true;
            }

            // The wheel model lies flat in its own frame, hub downward, so its axle is its local y;
            // the locator tilts it toward the driver. Positive y is clockwise from the driver's side.
            SteeringWheel.localRotation = _steeringWheelRest * Quaternion.Euler(0f, _steerInput * WheelDegreesPerUnit, 0f);
        }

        /// <summary>
        /// The game's autocentring, as /def/vehicle/physics.sii describes it (its lines 31-44): the
        /// returning speed is the full turning speed times speed_coef (4.0, for 100 km/h, linear)
        /// times angle_coef (2.0, for a 30 degree wheel turn, linear), capped at ratio_coef (0.95).
        /// The two coefficients not read by EtsVehicleSpec are the file's values here. Floored so a
        /// wheel released at a standstill still comes back (see ReleasedCentreFloor).
        /// </summary>
        private float AutoCentreShare()
        {
            const float angleCoefficient = 2.0f;
            const float ratioCoefficient = 0.95f;
            float share = Spec.SteerSpeedCoefficient * (SpeedKmh / 100f) * angleCoefficient * (Mathf.Abs(SteerDegrees) / 30f);
            return Mathf.Clamp(share, ReleasedCentreFloor, ratioCoefficient);
        }

        private void FixedUpdate()
        {
            if (Spec == null || !_placed || _body == null || _body.isKinematic)
            {
                return;
            }

            float dt = Time.fixedDeltaTime;

            // Once, on the first step driven: what the body is made of, so a log shows at once
            // whether mass and inertia are the truck's (a 7 t truck's are in the thousands of
            // kg m^2) - the first spin came from an inertia of a few.
            if (_reportBody)
            {
                _reportBody = false;
                Vector3 inertia = _body.inertiaTensor;
                Debug.Log("[SomniumEuroTruck] Truck driven: mass " + _body.mass.ToString("0") + " kg, inertia (" + inertia.x.ToString("0") + ", " + inertia.y.ToString("0") + ", " + inertia.z.ToString("0") + ") kg m^2" +
                          (_body.automaticInertiaTensor ? " (automatic)" : " (SET BY HAND)") + ", centre of mass " + _body.centerOfMass.ToString("0.00") + ".");
            }

            Vector3 up = transform.up;
            Vector3 forward = transform.forward;
            float speed = ForwardSpeed;
            float kmh = Mathf.Abs(speed) * 3.6f;

            // Steering lock shrinks with speed as the game's physics table says - for keys and the
            // stick only: physics.sii (lines 49-50) gives the speed multipliers "Used only with
            // relative steering (keyboard, joypad)". Hands on the wheel are absolute steering, the
            // way a wheel controller is, so the wheel's angle maps straight to the lock.
            _absoluteSteering = Mathf.MoveTowards(_absoluteSteering, WheelHeld ? 1f : 0f, dt / 0.3f);
            float relative = Mathf.Lerp(Spec.SteerSensitivityAt0, Spec.SteerSensitivityAt100, Mathf.Clamp01(kmh / 100f));
            float sensitivity = Mathf.Lerp(relative, 1f, _absoluteSteering);
            SteerDegrees = _steerInput * Spec.MaxSteerAngle * sensitivity;

            // Gearbox: automatic, with a torque cut while shifting.
            _shiftTimer = Mathf.Max(0f, _shiftTimer - dt);

            if (Gear > 0 && _shiftTimer <= 0f)
            {
                if (Rpm > ShiftUpRpm && Gear < Spec.ForwardRatios.Length && Throttle > 0.05f) { Gear++; _shiftTimer = ShiftTime; }
                else if (Rpm < ShiftDownRpm && Gear > 1) { Gear--; _shiftTimer = ShiftTime; }
            }

            // Engine speed from the powered wheels through the driveline.
            float ratio = GearRatio * Spec.DifferentialRatio;
            int powered = 0;
            float poweredSpeed = 0f;
            foreach (Wheel w in _wheels) if (w.Powered) { powered++; poweredSpeed += Vector3.Dot(_body.GetPointVelocity(transform.TransformPoint(w.LocalHub)), forward); }
            poweredSpeed = powered > 0 ? poweredSpeed / powered : speed;
            float wheelRadius = _wheels.Count > 0 ? _wheels[0].Radius : 0.5f;
            float drivenRpm = Mathf.Abs(poweredSpeed / (2f * Mathf.PI * wheelRadius) * 60f * ratio);
            float targetRpm = Gear == 0 ? IdleRpm + Throttle * 1200f : Mathf.Max(IdleRpm, drivenRpm);

            // Engine off (a headset driver who has not started it yet): the revs run down to nothing.
            if (WaitingForStart || (!EngineRunning && Time.time < _stallUntil)) targetRpm = 0f;
            Rpm = Mathf.Lerp(Rpm, Mathf.Min(targetRpm, Spec.RpmLimit), 1f - Mathf.Exp(-dt * 8f));

            // A headset start: the revs follow the start sound (StartRevs) until it has settled.
            if (EngineRunning && _startShaped && Time.time - EngineStartedAt < StartRevsSeconds)
            {
                Rpm = StartRevs(Mathf.Max(0f, Time.time - EngineStartedAt));
            }

            // In a headset nothing drives, and nothing brakes by the engine, until it runs and has caught (a keyboard as ever).
            // A damaged engine stalled or a damaged gearbox slipping (EtsDamage, Malfunction): nothing drives or brakes by the engine.
            bool caught = (!EtsVr.Present || EngineCaught) && Time.time >= _driveCutUntil;
            float engineTorque = !caught || _shiftTimer > 0f || Gear == 0 || Rpm >= Spec.RpmLimit - 1f ? 0f : Spec.TorqueAt(Rpm) * Throttle;
            float wheelTorque = engineTorque * ratio * DrivelineEfficiency;
            float engineBrakeTorque = caught && Gear != 0 && Throttle < 0.05f ? 0.06f * Spec.Torque * Mathf.Abs(ratio) : 0f;

            // Brakes: full pedal stops a laden truck at about 5 m/s^2, on the game's brake factor.
            float brakePedal = Mathf.Max(Brake, ParkingBrake ? 1f : 0f);
            float brakePerWheel = _body.mass * 5f * Spec.BrakeTorqueFactor / Mathf.Max(1, _wheels.Count) * brakePedal;
            float grip = Spec.WetGrip;

            bool anyGrounded = false;

            foreach (Wheel wheel in _wheels)
            {
                Vector3 hub = transform.TransformPoint(wheel.LocalHub);
                float rayLength = wheel.RestLength + wheel.Travel;
                wheel.Grounded = CastWheel(hub, up, wheel.Radius, rayLength, out wheel.Hit);
                anyGrounded |= wheel.Grounded;
                wheel.SteerDegrees = wheel.Steered ? SteerDegrees : 0f;

                if (!wheel.Grounded)
                {
                    wheel.Load = 0f;
                    wheel.Compression = -wheel.Travel;
                    continue;
                }

                // Suspension along the chassis up axis.
                wheel.Compression = wheel.RestLength - wheel.Hit.distance;
                Vector3 hubVelocity = _body.GetPointVelocity(hub);
                float suspensionVelocity = Vector3.Dot(hubVelocity, up);
                float force = wheel.Spring * wheel.Compression - wheel.Damper * suspensionVelocity;
                float bump = wheel.Compression - wheel.Travel;
                if (bump > 0f) force += wheel.Spring * 6f * bump;
                force = Mathf.Max(0f, force);
                wheel.Load = force;
                _body.AddForceAtPosition(up * force, wheel.Hit.point);

                // Tyre frame on the contact plane.
                Vector3 wheelForward = Quaternion.AngleAxis(wheel.SteerDegrees, up) * forward;
                Vector3 rollDirection = Vector3.ProjectOnPlane(wheelForward, wheel.Hit.normal).normalized;
                Vector3 sideDirection = Vector3.Cross(wheel.Hit.normal, rollDirection).normalized;
                Vector3 contactVelocity = _body.GetPointVelocity(wheel.Hit.point);
                float longitudinalSpeed = Vector3.Dot(contactVelocity, rollDirection);
                float lateralSpeed = Vector3.Dot(contactVelocity, sideDirection);
                float maxFriction = grip * force;
                float wheelMassShare = force / 9.81f;

                // Longitudinal: drive, brakes (never past a standstill), engine braking, rolling resistance.
                float longitudinal = wheel.Powered && powered > 0 ? wheelTorque / wheel.Radius / powered : 0f;
                float stopForce = wheelMassShare * Mathf.Abs(longitudinalSpeed) / dt;
                float resist = brakePerWheel + (wheel.Powered && powered > 0 ? engineBrakeTorque / wheel.Radius / powered : 0f) + force * Spec.RollResistance * 0.001f;
                longitudinal -= Mathf.Sign(longitudinalSpeed) * Mathf.Min(resist, stopForce);

                // Lateral: slip angle against cornering stiffness, damped to a stop at low speed.
                float slipAngle = Mathf.Atan2(lateralSpeed, Mathf.Max(Mathf.Abs(longitudinalSpeed), 0.5f));
                float lateral = -CorneringStiffness * slipAngle * force;
                float lateralStop = wheelMassShare * Mathf.Abs(lateralSpeed) / dt;
                lateral = Mathf.Sign(lateral) * Mathf.Min(Mathf.Abs(lateral), lateralStop);

                float total = Mathf.Sqrt(longitudinal * longitudinal + lateral * lateral);

                if (total > maxFriction && total > 0f)
                {
                    float scale = maxFriction / total;
                    longitudinal *= scale;
                    lateral *= scale;
                }

                _body.AddForceAtPosition(rollDirection * longitudinal + sideDirection * lateral, wheel.Hit.point);
                wheel.SpinDegrees += longitudinalSpeed / wheel.Radius * Mathf.Rad2Deg * dt;
            }

            // Anti-roll bars: the more compressed side of an axle lifts the other.
            foreach (List<Wheel> axle in _axles)
            {
                if (axle.Count != 2 || !axle[0].Grounded || !axle[1].Grounded) continue;
                float bar = axle[0].Spring * 0.5f * (axle[0].Compression - axle[1].Compression);
                _body.AddForceAtPosition(up * bar, transform.TransformPoint(axle[0].LocalHub));
                _body.AddForceAtPosition(-up * bar, transform.TransformPoint(axle[1].LocalHub));
            }

            // Air resistance as the game states it: force = coefficient x speed squared.
            Vector3 velocity = _body.linearVelocity;
            _body.AddForce(-Spec.AirResistance * velocity.magnitude * velocity);

            // Wheel visuals: hang at the contact, spin with the road, steer with the wheel.
            foreach (Wheel wheel in _wheels)
            {
                if (wheel.Visual == null) continue;
                float drop = wheel.Grounded ? Mathf.Clamp(wheel.Hit.distance - wheel.Radius, -wheel.Travel, wheel.Travel) : wheel.Travel;
                wheel.Visual.localPosition = wheel.LocalHub - Vector3.up * drop;
                wheel.Visual.localRotation = Quaternion.Euler(0f, wheel.SteerDegrees, 0f) * wheel.VisualRest * Quaternion.Euler(wheel.SpinDegrees * wheel.SpinSign, 0f, 0f);
            }

            // Nothing under any wheel for three seconds while dropping fast is not a jump off a
            // kerb: the truck has gone through the world. Put it back where it was last set down
            // rather than let it fall for ever (the first client run fell 1.6 km before anyone saw).
            _airborne = anyGrounded || velocity.y > -15f ? 0f : _airborne + dt;

            // Every second on all fours, level and not bouncing: remember the spot.
            if (anyGrounded && Mathf.Abs(velocity.y) < 2f && transform.up.y > 0.9f && (_lastGoodTimer += dt) > 1f)
            {
                _lastGoodTimer = 0f;
                _lastGoodPosition = transform.position;
                _lastGoodYaw = transform.eulerAngles.y;
                _haveLastGood = true;
            }

            if (_airborne > 3f)
            {
                _airborne = 0f;
                Vector3 at = transform.position;

                // The last safe spot first (always inside the streamed world, the truck was just
                // there), then the spawn point; neither becomes the new respawn point.
                bool back = _haveLastGood && PlaceOnGround(_lastGoodPosition, _lastGoodYaw, false);
                string where = "at the last spot it stood safely";

                if (!back)
                {
                    back = PlaceOnGround(SpawnPosition, SpawnRotation.eulerAngles.y, false);
                    where = "where it was last set down";
                }

                if (!back)
                {
                    // No ground at either: hold it still rather than let it fall on.
                    _body.isKinematic = true;
                }

                Debug.Log("[SomniumEuroTruck] The truck fell through the world at (" + at.x.ToString("0") + ", " + at.y.ToString("0") + ", " + at.z.ToString("0") + "); " +
                          (back ? "put back " + where + "." : "no ground at the last safe spot or the spawn point, so it is held where it is."));
            }

            WatchForBlock(dt, kmh);
        }

        // ------------------------------------------------------------------ the blocked-report
        //
        // A diagnostic for the client log. The heartbeat samples the truck every ten seconds, which
        // cannot tell a median barrier from a terrain sheet or a wall: when the driver is pushing and
        // the truck will not move, or the body takes a hard knock, this logs ONCE per episode what
        // the truck is touching, what stands just ahead of its bumper, the ground ahead of it and
        // what each wheel is standing on - with the map item (uid and kind) of every collider the map
        // built. Search the log for "Truck blocked". Rate-limited: one report per 15 s at most, 20 a
        // session (ReportInterval, MaxReports).

        /// <summary>Pushing: throttle over this, speed under BlockedKmh, for BlockedSeconds.</summary>
        private const float BlockedThrottle = 0.3f;
        private const float BlockedKmh = 1.5f;
        private const float BlockedSeconds = 1.5f;

        /// <summary>A knock: a single contact impulse that changes the body's speed by this much (m/s).</summary>
        private const float KnockDeltaV = 1.5f;

        /// <summary>
        /// At most one report every ReportInterval seconds and MaxReports a session. An episode
        /// re-arms after a second of driving, so scraping along a barrier run or being nudged by
        /// the AI cars (kinematic, Vehicle layer) would otherwise log a 3-4 KB report - three
        /// overlap queries, four raycasts, up to ~45 collider descriptions, built inside a physics
        /// callback - about once a second (review DIAG-1). Held-back knocks and pushes are counted
        /// into the next report.
        /// </summary>
        private const float ReportInterval = 15f;
        private const int MaxReports = 20;

        private float _blockedTimer;
        private float _movingTimer;
        private bool _blockedReported;
        private Vector3 _blockedAt;
        private float _nextReportTime;
        private int _reports;
        private int _reportsHeldBack;

        // What the body box touched lately (OnCollisionEnter/Stay), kept without allocating.
        private const int TouchSlots = 8;
        private readonly Collider[] _touched = new Collider[TouchSlots];
        private readonly float[] _touchedImpulse = new float[TouchSlots];
        private readonly float[] _touchedAt = new float[TouchSlots];
        private readonly Vector3[] _touchedPoint = new Vector3[TouchSlots];
        private static readonly Collider[] OverlapBuffer = new Collider[32];

        /// <summary>Everything but the truck, the trailer, the avatar and the UI.</summary>
        private const int ReportMask = ~((1 << EtsLayers.Vehicle) | (1 << EtsLayers.IgnoreRaycast) | (1 << EtsLayers.PhysicalObject) | (1 << EtsLayers.Player) | (1 << EtsLayers.Ui));

        private void WatchForBlock(float dt, float kmh)
        {
            if (!Driven || Puppet)
            {
                _blockedTimer = 0f;
                return;
            }

            // A new episode once the truck has got going again or has been moved off the spot.
            if (_blockedReported)
            {
                _movingTimer = kmh > 5f ? _movingTimer + dt : 0f;

                if (_movingTimer > 1f || (transform.position - _blockedAt).sqrMagnitude > 9f)
                {
                    _blockedReported = false;
                    _movingTimer = 0f;
                }
            }

            bool wasBlocked = _blockedTimer >= BlockedSeconds;
            _blockedTimer = Throttle > BlockedThrottle && kmh < BlockedKmh ? _blockedTimer + dt : 0f;

            if (!_blockedReported && _blockedTimer >= BlockedSeconds && MayReport(!wasBlocked))
            {
                ReportBlocked("pushing at throttle " + Throttle.ToString("0.00") + " for " + _blockedTimer.ToString("0.0") + " s at " + kmh.ToString("0.0") + " km/h");
            }
        }

        /// <summary>
        /// Whether a report may be written now (ReportInterval, MaxReports). A refused one only
        /// counts itself (<paramref name="count"/>: a push is asked again every step while it
        /// lasts, so only its first step counts) and leaves the episode open: a truck still stuck
        /// when the interval runs out is reported then, with how long it has pushed. Marking the
        /// episode reported instead lost it - a knock reported, the truck backed off and stuck
        /// again within 15 s logged nothing however long it pushed (check probe 1812, scenarios 6
        /// and 7). The refusal is two comparisons; nothing is described or allocated.
        /// </summary>
        private bool MayReport(bool count)
        {
            if (_reports < MaxReports && Time.time >= _nextReportTime)
            {
                return true;
            }

            if (count)
            {
                _reportsHeldBack++;
            }

            return false;
        }

        private void OnCollisionEnter(Collision collision)
        {
            Touch(collision, true);
            EtsDamage.TruckCollision(this, collision); // the own truck's damage (2026-10-04)
        }

        private void OnCollisionStay(Collision collision)
        {
            Touch(collision, false);
        }

        private void Touch(Collision collision, bool entered)
        {
            if (_body == null || _body.isKinematic || !Driven || Puppet)
            {
                return;
            }

            Collider other = collision.collider;
            float impulse = collision.impulse.magnitude;
            Vector3 point = collision.contactCount > 0 ? collision.GetContact(0).point : other.bounds.center;

            // The slot this collider already has, else the stalest one.
            int slot = 0;

            for (int i = 0; i < TouchSlots; i++)
            {
                if (_touched[i] == other) { slot = i; break; }
                if (_touchedAt[i] < _touchedAt[slot]) slot = i;
            }

            _touched[slot] = other;
            _touchedImpulse[slot] = impulse;
            _touchedAt[slot] = Time.time;
            _touchedPoint[slot] = point;

            if (entered && !_blockedReported && impulse / Mathf.Max(1f, _body.mass) > KnockDeltaV && MayReport(true))
            {
                ReportBlocked("knocked: " + (impulse / 1000f).ToString("0.0") + " kN s from " + DescribeCollider(other) + " at " + point.ToString("0.0"));
            }
        }

        private void ReportBlocked(string why)
        {
            _blockedReported = true;
            _blockedAt = transform.position;
            _movingTimer = 0f;
            _reports++;
            _nextReportTime = Time.time + ReportInterval;
            int heldBack = _reportsHeldBack;
            _reportsHeldBack = 0;

            // The line still starts "Truck blocked (" - the text the log is searched for - with the
            // report count at the end of the first line.
            var text = new System.Text.StringBuilder(2048);
            Vector3 p = transform.position, f = transform.forward;
            text.Append("[SomniumEuroTruck] Truck blocked (").Append(why).Append(") at ").Append(p.ToString("0.0"))
                .Append(" heading ").Append(transform.eulerAngles.y.ToString("0")).Append(" deg, up ").Append(transform.up.ToString("0.00"))
                .Append(", ").Append(SpeedKmh.ToString("0.0")).Append(" km/h, gear ").Append(GearLabel).Append(", throttle ").Append(Throttle.ToString("0.00"))
                .Append(", brake ").Append(Brake.ToString("0.00")).Append(ParkingBrake ? ", PARKING BRAKE" : "").Append(", steer ").Append(SteerDegrees.ToString("0")).Append(" deg")
                .Append(Trailer != null ? ", trailer coupled" : ", no trailer")
                .Append("; report ").Append(_reports).Append(" of at most ").Append(MaxReports)
                .Append(_reports >= MaxReports ? ", the last this session" : "")
                .Append(heldBack > 0 ? ", " + heldBack + " knock" + (heldBack == 1 ? "" : "s") + " or push" + (heldBack == 1 ? "" : "es") + " held back since the last" : "")
                .Append('.');

            // What the body box has touched in the last second.
            text.Append("\n  touching:");
            int touching = 0;

            for (int i = 0; i < TouchSlots; i++)
            {
                if (_touched[i] == null || Time.time - _touchedAt[i] > 1f) continue;
                text.Append("\n    ").Append(DescribeCollider(_touched[i])).Append(", impulse ").Append((_touchedImpulse[i] / 1000f).ToString("0.0")).Append(" kN s at ").Append(_touchedPoint[i].ToString("0.0"));
                touching++;
            }

            if (touching == 0) text.Append(" nothing");

            // What stands in the metre and a half ahead of the bumper, and inside the body box itself
            // (drive surface and terrain included: a terrain sheet over the road shows here).
            if (_box != null)
            {
                Vector3 centre = transform.TransformPoint(_box.center);
                Vector3 half = Vector3.Scale(_box.size, transform.lossyScale) * 0.5f;
                Vector3 ahead = centre + f * (half.z + 0.75f);
                int n = Physics.OverlapBoxNonAlloc(ahead, new Vector3(half.x + 0.25f, half.y, 0.75f), OverlapBuffer, transform.rotation, ReportMask, QueryTriggerInteraction.Ignore);
                AppendColliders(text, "ahead of the bumper", n);
                n = Physics.OverlapBoxNonAlloc(centre, half + Vector3.one * 0.1f, OverlapBuffer, transform.rotation, ReportMask & ~EtsLayers.GroundMask, QueryTriggerInteraction.Ignore);
                AppendColliders(text, "inside the body box (not drive surface)", n);
            }

            // The trailer's box, whose own knocks this component does not hear.
            BoxCollider trailerBox = Trailer != null ? Trailer.GetComponent<BoxCollider>() : null;

            if (trailerBox != null)
            {
                Transform tt = trailerBox.transform;
                Vector3 half = Vector3.Scale(trailerBox.size, tt.lossyScale) * 0.5f + Vector3.one * 0.15f;
                int n = Physics.OverlapBoxNonAlloc(tt.TransformPoint(trailerBox.center), half, OverlapBuffer, tt.rotation, ReportMask & ~EtsLayers.GroundMask, QueryTriggerInteraction.Ignore);
                AppendColliders(text, "against the trailer's box", n);
            }

            // The ground ahead, as a wheel would find it: a ramp, a step or a sheet of terrain.
            text.Append("\n  ground ahead of the front axle:");
            Vector3 front = _wheels.Count > 0 ? transform.TransformPoint(_wheels[0].LocalHub) : p + f * 3f;

            for (int d = 1; d <= 7; d += 2)
            {
                Vector3 from = front + f * d + Vector3.up * 6f;

                if (Physics.Raycast(from, Vector3.down, out RaycastHit hit, 12f, EtsLayers.GroundMask, QueryTriggerInteraction.Ignore))
                {
                    text.Append("\n    +").Append(d).Append(" m: ").Append((hit.point.y - p.y).ToString("+0.00;-0.00")).Append(" m, slope ")
                        .Append(Vector3.Angle(hit.normal, Vector3.up).ToString("0")).Append(" deg, ").Append(DescribeCollider(hit.collider));
                }
                else
                {
                    text.Append("\n    +").Append(d).Append(" m: no ground");
                }
            }

            text.Append("\n  wheels:");

            foreach (Wheel wheel in _wheels)
            {
                text.Append("\n    ").Append(wheel.Name).Append(wheel.Grounded ? " on " + DescribeCollider(wheel.Hit.collider) + ", compression " + wheel.Compression.ToString("0.00") + " m, slope " + Vector3.Angle(wheel.Hit.normal, Vector3.up).ToString("0") + " deg" : " in the air");
            }

            Debug.Log(text.ToString());
        }

        private static void AppendColliders(System.Text.StringBuilder text, string where, int count)
        {
            text.Append("\n  ").Append(where).Append(':');

            if (count == 0)
            {
                text.Append(" nothing");
                return;
            }

            for (int i = 0; i < count && i < 12; i++)
            {
                text.Append("\n    ").Append(DescribeCollider(OverlapBuffer[i]));
            }

            if (count > 12) text.Append("\n    ... and ").Append(count - 12).Append(" more");
        }

        /// <summary>A collider for the log: its path, layer, the map item it belongs to, and its world bounds.</summary>
        private static string DescribeCollider(Collider collider)
        {
            if (collider == null)
            {
                return "(none)";
            }

            string path = collider.name;
            Transform t = collider.transform.parent;

            for (int i = 0; i < 4 && t != null; i++, t = t.parent)
            {
                path = t.name + "/" + path;
            }

            int layer = collider.gameObject.layer;
            string item = EtsMapViewer.DescribeMapItem(collider.transform);
            Bounds b = collider.bounds;
            return "'" + path + "' layer " + layer + " " + LayerMask.LayerToName(layer) + (item != null ? ", " + item : ", not a map item") +
                   ", bounds " + b.min.ToString("0.0") + " .. " + b.max.ToString("0.0");
        }

        /// <summary>
        /// A wheel's suspension ray, cast from a little above the hub so that a hub pushed a short
        /// way under the road still finds it and the bump stop lifts it back out. Started at the
        /// hub itself, the ray began below the surface, found nothing, and the wheel stopped
        /// holding the truck up at exactly the moment it was needed. The hit's distance is given
        /// back measured from the hub, as the springs and the wheel visuals expect (so it can be
        /// negative).
        ///
        /// Only 12 cm. It was a whole tyre radius (half a metre) at first, and that is enough to
        /// find a second ground surface lying over the one the wheel is on - the ground census
        /// counts a thousand-odd cells where two surfaces overlap - which the bump stop answers
        /// with a force fit to throw the truck. (The first client run with it, the truck spun and
        /// flew the moment it went dynamic; see EtsTruckDoor for the inertia fault found beside it.)
        /// </summary>
        public static bool CastWheel(Vector3 hub, Vector3 up, float radius, float rayLength, out RaycastHit hit)
        {
            const float lift = 0.12f;

            if (!Physics.Raycast(hub + up * lift, -up, out hit, rayLength + lift, EtsLayers.GroundMask, QueryTriggerInteraction.Ignore))
            {
                return false;
            }

            hit.distance -= lift;
            return true;
        }
    }

    /// <summary>
    /// A vehicle's indicator lamps, lit ON THE MODEL the way the game lights them (2026-10-04; they were orange cubes):
    /// the lens pieces with the eut2.lamp effect glow through their lamp masks in the colours of the vehicle's lamp
    /// setup - the front and rear turn signals, the side repeaters, any mirror lamps the model has (Ets/EtsLampSetup,
    /// Ets/EtsVehicleLamps). The controller calls Set on every blink edge so a blink is never late; the world's
    /// EtsVehicleLampDriver keeps the brake and reverse lamps and any truck without an edge (a puppet) in step. Built on
    /// the first lit phase (shared with the driver: one set of overlays per vehicle); the build is logged
    /// ("Indicator lamps: &lt;truck&gt; - N lamp pieces lit the game's way ..."). A model without lamp lenses shows none,
    /// as in the game. The cluster's own arrows are EtsInstruments'.
    /// </summary>
    internal sealed class EtsIndicatorLamps
    {
        private EtsVehicleLamps _lamps;

        public void Set(bool left, bool right)
        {
            _lamps?.SetTurn(left, right);
        }

        public void BuildTruck(EtsTruckController truck)
        {
            _lamps = EtsVehicleLampDriver.For(truck);
        }

        public void BuildTrailer(EtsTrailer trailer, BoxCollider box)
        {
            _lamps = EtsVehicleLampDriver.For(trailer);
        }
    }
}
