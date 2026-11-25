using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
using UnityEngine;

namespace SomniumSpace.Worlds.SomniumEuroTruck
{
    /// <summary>
    /// The cab's switches worked by hand in a headset (2026-09-25): the blinker stalk, the hazard switch and the parking
    /// brake lever of the game's own animated dashboard, for the driver of the local player's own truck.
    ///
    ///  - A free hand (tracked, not holding the rim, grip open) that comes onto a control's zone lights it
    ///    (EtsInstruments.SetControlHighlight) and gets a tick (TickAmplitude for TickSeconds, firmer and longer than the
    ///    rim's own 0.1 / 20 ms tick, at most one every TickGap): the trigger will act on it. Only shown while that hand's
    ///    trigger is up or already the control's: a pedal held from elsewhere never acts on a control, so none is promised.
    ///    Overlapping zones: the nearest control wins, the one already lit counting HoverBias nearer (no flicker).
    ///  - A trigger press that STARTS there is the control's (EtsVr captures it: that hand's pedal reads 0 until the
    ///    trigger is let go - no blip of brake or throttle). A press that has done nothing yet is handed back to its pedal
    ///    only where that pedal is a brake right now (IsBrake: the left trigger rolling forward, the right rolling back -
    ///    EtsTruckController.Update turns those into Brake = 1); anywhere else it would be the throttle or reverse at once,
    ///    so the press stays the control's and the pedal waits for the trigger to be let go and pressed again.
    ///  - Past half pull: the parking brake lever toggles the parking brake (as right A - its air hiss, lamp and rules; the
    ///    accelerator still releases it), the hazard switch the hazard lights (as F).
    ///  - The stalk, with the trigger held: the hand moved UP in the truck's frame by StalkCommit (3.5 cm) moves the stalk up
    ///    one place, DOWN one place down - off to RIGHT or left to off going up, off to LEFT or right to off going down -
    ///    the way the game's stalk moves (blinker_anim t = 1 is up = right on all 18 demo trucks, cabscout/out1.txt).
    ///    Pushed further the way it already is, nothing. One sweep on through 2 x StalkCommit the same way takes a second
    ///    place (right, through off, to left); coming back in the same press takes none, so a flick that relaxes back does
    ///    not undo itself. The drawn stalk follows the hand, never against it.
    ///    A press on the stalk that has switched nothing yet gives the brake back (IsBrake) once it has sat still for
    ///    StalkBrakeWait, or once the hand has left the zone: a hand resting there that squeezes to brake loses under a
    ///    second of it at worst.
    ///
    /// Where the controls are comes from the game's files (EtsInstruments.TryGetControl); the zones are sized from each
    /// control's own geometry plus the reach of a hand: the tracked point (the Bridge hand anchor, the controller's grip
    /// pose) sits about 6-7 cm toward the driver from the fingers (the logged grabs: 0.06-0.07 m off the rim), so every zone
    /// is at least 8.5 cm round, and a stalk that sits deep behind the rim has its zone brought toward the driver along the
    /// wheel's axle (StalkTowardDriver). The stalk is 7-17 cm from the rim's centre line, inside the 12 cm grab reach, so a
    /// hand clearly nearer the rim than the control (RimMargin) is on the wheel and keeps its trigger as a pedal. That and
    /// the free-hand rule (a hand that grips never counts) and the fresh-press rule (EtsVr: a trigger already held is never
    /// captured) are what keep braking and accelerating on the wheel as they were. Measured on all 15 demo cabs
    /// (vrcab2/reach): a palm 5 / 6.5 / 8 cm in front of the stalk's outer half is caught 100 / 100 / 100 % on the DAF XF
    /// Euro 6 and 100 / 83 / 20 % on the Scania S 2016; an open hand resting on the rim at most 0.67 % of the time.
    ///
    /// The hand's motion is measured inside the rig (tracking space) and carried into the world by this frame's rig - the
    /// rule EtsSteeringGrab keeps, so the cab moving under the hands moves nothing. Stepped by EtsSteeringGrab at the end of
    /// AfterRigPlaced (the driver's seat, in a headset, after the hands are in the cab and the grab has decided which hands
    /// hold the rim), under its Usable rule: driven, placed, InputEnabled, the tablet app not up. The controls work with the
    /// engine off (the dash has electricity while the driver sits in). Nothing else answers the trigger while seated: the
    /// cab is on layer 28, which the client's pointer does not see, and the door handle only works on foot
    /// (EtsTruckDoor.Usable). No allocation per frame.
    /// </summary>
    public sealed class EtsCabControls
    {
        /// <summary>Stalk zone: a capsule along the outer 65 % of the stalk and 2 cm past its tip, radius its thickness plus this, clamped.</summary>
        public float StalkPad = 0.05f, StalkRadiusMin = 0.085f, StalkRadiusMax = 0.095f;

        /// <summary>A stalk tip further than From behind the rim's centre line has its zone brought toward the driver by the rest, at most Max (m).</summary>
        public float StalkTowardDriverFrom = 0.10f, StalkTowardDriverMax = 0.05f;

        /// <summary>Lever zone: a capsule along its outer 70 %, radius its thickness plus this, clamped; and a sphere round the knob.</summary>
        public float LeverPad = 0.035f, LeverRadiusMin = 0.055f, LeverRadiusMax = 0.08f, LeverKnobRadius = 0.085f;

        /// <summary>Hazard zone: a sphere round the switch, its reach plus this, clamped; a fixed point (the decal) gets DecalRadius.</summary>
        public float HazardPad = 0.04f, HazardRadiusMin = 0.085f, HazardRadiusMax = 0.095f, DecalRadius = 0.085f;

        /// <summary>A hand on a control stays on it out to this share of the zone (hysteresis).</summary>
        public float Stay = 1.25f;

        /// <summary>Between two zones, the one a hand is already on counts as this share of its distance (hysteresis).</summary>
        public float HoverBias = 0.8f;

        /// <summary>A hand counts as on the wheel, not the control, when the control is further than the rim by more than this (m); plus RimStay for the control it is on.</summary>
        public float RimMargin = 0.03f, RimStay = 0.02f;

        /// <summary>A captured press acts once it passes this pull.</summary>
        public float EngagePull = 0.5f;

        /// <summary>How far the hand moves up or down (truck frame, m) to move the stalk one place.</summary>
        public float StalkCommit = 0.035f;

        /// <summary>A stalk press that has switched nothing, on a trigger that is the brake now, gives it back after this long with the hand this still (s, m).</summary>
        public float StalkBrakeWait = 0.8f, StalkStill = 0.015f;

        /// <summary>Coming onto a control: this tick (the rim's is 0.1 for 0.02 s), at most one per hand every TickGap seconds.</summary>
        public float TickAmplitude = 0.18f, TickSeconds = 0.03f, TickGap = 0.25f;

        private const int None = -1;
        private const int MaxActionLogs = 40;
        private const int MaxStalkMoves = 2;

        private sealed class Hand
        {
            public bool Right;
            public int Hover = None;
            public int Engaged = None;
            public bool Acted;
            public int StalkMoves;
            public int StalkWay;
            public float EngagedAt;
            public float NextTick;
            public Vector3 EngageInRig;
            public float StalkBase;
        }

        private readonly Hand _left = new Hand { Right = false };
        private readonly Hand _right = new Hand { Right = true };
        private readonly bool[] _have = new bool[EtsInstruments.ControlCount];
        private readonly bool[] _point = new bool[EtsInstruments.ControlCount];
        private readonly Vector3[] _a = new Vector3[EtsInstruments.ControlCount];
        private readonly Vector3[] _b = new Vector3[EtsInstruments.ControlCount];
        private readonly float[] _radius = new float[EtsInstruments.ControlCount];
        private readonly int[] _level = new int[EtsInstruments.ControlCount];
        private readonly int[] _shown = { None, None };
        private EtsTruckController _truck;
        private EtsInstruments _dash;
        private int _nextSearch;
        private bool _reported;
        private int _actionLogs;
        private float _stalkShift, _stalkTipFromRim;
        private Matrix4x4 _toWheel = Matrix4x4.identity;
        private float _rimRadius = 0.22f;

        /// <summary>Whether a hand is on a control or a control is lit (so Clear has something to undo).</summary>
        public bool Active => _left.Hover != None || _right.Hover != None || _left.Engaged != None || _right.Engaged != None ||
                              _level[0] != 0 || _level[1] != 0 || _level[2] != 0;

        /// <summary>
        /// One frame, after the rig and the hands are placed. The wheel: world to its rest frame centred on the rim's plane
        /// (EtsSteeringGrab's toWheel: +Y the axle toward the driver), the rim's radius and the axle toward the driver in the
        /// world. Per hand: whether it is tracked, where it is inside its rig and that rig's local-to-world
        /// (EtsSteeringGrab.HandInRig), whether it is free (not holding, grip open) and how far it is from the rim's centre
        /// line (m).
        /// </summary>
        public void Step(EtsTruckController truck, Matrix4x4 toWheel, float rimRadius, Vector3 towardDriver,
                         bool haveLeft, Vector3 leftInRig, Matrix4x4 leftRig, bool leftFree, float leftFromRim,
                         bool haveRight, Vector3 rightInRig, Matrix4x4 rightRig, bool rightFree, float rightFromRim)
        {
            if (truck != _truck)
            {
                Clear();
                _truck = truck;
                _dash = null;
                _nextSearch = 0;
                _reported = false;
            }

            if (_truck == null)
            {
                return;
            }

            if (_dash == null && Time.frameCount >= _nextSearch)
            {
                _nextSearch = Time.frameCount + 30;
                _dash = _truck.GetComponentInChildren<EtsInstruments>(true);
            }

            _toWheel = toWheel;
            _rimRadius = rimRadius;

            if (_dash == null || !_dash.Running || !Zones(towardDriver))
            {
                Clear();
                return;
            }

            StepHand(_left, haveLeft, leftInRig, leftRig, leftFree, leftFromRim);
            StepHand(_right, haveRight, rightInRig, rightRig, rightFree, rightFromRim);

            for (int c = 0; c < EtsInstruments.ControlCount; c++)
            {
                int level = _left.Engaged == c || _right.Engaged == c ? 2 : _shown[0] == c || _shown[1] == c ? 1 : 0;

                if (level != _level[c])
                {
                    _level[c] = level;
                    _dash.SetControlHighlight(c, level);
                }
            }
        }

        /// <summary>Nothing on any control: hovers off, lights off, a press that did nothing yet handed back where its pedal is a brake.</summary>
        public void Clear()
        {
            ClearHand(_left);
            ClearHand(_right);

            for (int c = 0; c < EtsInstruments.ControlCount; c++)
            {
                if (_level[c] != 0)
                {
                    _level[c] = 0;

                    if (_dash != null)
                    {
                        _dash.SetControlHighlight(c, 0);
                    }
                }
            }
        }

        private void ClearHand(Hand hand)
        {
            EtsVr.SetControlHover(hand.Right, false);

            if (!hand.Acted && EtsVr.TriggerCaptured(hand.Right) && IsBrake(hand))
            {
                EtsVr.ReleaseCapture(hand.Right);
            }

            hand.Hover = None;
            hand.Engaged = None;
            _shown[hand.Right ? 1 : 0] = None;
        }

        /// <summary>
        /// Whether this hand's trigger, as a pedal, is a brake this moment: EtsTruckController.Update makes the left trigger
        /// (drive &lt; 0) Brake = 1 while rolling forward faster than 0.3 m/s, and the right (drive &gt; 0) while rolling back.
        /// Anywhere else a pedal is the throttle, or reverse from a standstill.
        /// </summary>
        private bool IsBrake(Hand hand)
        {
            if (_truck == null)
            {
                return false;
            }

            float speed = _truck.ForwardSpeed;
            return hand.Right ? speed < -0.3f : speed > 0.3f;
        }

        /// <summary>How far a point is from the rim's centre line (m), as EtsSteeringGrab measures a hand.</summary>
        private float RimDistance(Vector3 world)
        {
            Vector3 p = _toWheel.MultiplyPoint3x4(world);
            float radial = Mathf.Sqrt(p.x * p.x + p.z * p.z);
            return Mathf.Sqrt((radial - _rimRadius) * (radial - _rimRadius) + p.y * p.y);
        }

        /// <summary>This frame's zones from the controls' drawn positions; false when the cab has none.</summary>
        private bool Zones(Vector3 towardDriver)
        {
            bool any = false;

            for (int c = 0; c < EtsInstruments.ControlCount; c++)
            {
                _have[c] = _dash.TryGetControl(c, out Vector3 pivot, out Vector3 tip, out Vector3 centre, out float thick, out float extent, out bool point);
                _point[c] = point;

                if (!_have[c])
                {
                    continue;
                }

                any = true;
                Vector3 along = tip - pivot;

                switch (c)
                {
                    case EtsInstruments.StalkControl:
                        // A stalk deep behind the rim (the DAF XF Euro 6's tip is 15.5 cm behind it): the hand on it is in front of
                        // it, so the zone comes toward the driver - by what is past StalkTowardDriverFrom, at most its Max.
                        _stalkTipFromRim = RimDistance(tip);
                        _stalkShift = point ? 0f : Mathf.Clamp(_stalkTipFromRim - StalkTowardDriverFrom, 0f, StalkTowardDriverMax);
                        Vector3 shift = towardDriver * _stalkShift;
                        _a[c] = pivot + along * 0.35f + shift;
                        _b[c] = tip + (along.sqrMagnitude > 1e-8f ? along.normalized * 0.02f : Vector3.zero) + shift;
                        _radius[c] = point ? DecalRadius : Mathf.Clamp(thick + StalkPad, StalkRadiusMin, StalkRadiusMax);
                        break;

                    case EtsInstruments.ParkingControl:
                        _a[c] = pivot + along * 0.3f;
                        _b[c] = tip;
                        _radius[c] = point ? DecalRadius : Mathf.Clamp(thick + LeverPad, LeverRadiusMin, LeverRadiusMax);
                        break;

                    default:
                        _a[c] = _b[c] = centre;
                        _radius[c] = point ? DecalRadius : Mathf.Clamp(extent + HazardPad, HazardRadiusMin, HazardRadiusMax);
                        break;
                }
            }

            if (any && !_reported)
            {
                _reported = true;
                Debug.Log("[SomniumEuroTruck] VR controls: ready in " + _truck.name + " - " + Describe(EtsInstruments.StalkControl, "stalk") + ", " +
                          Describe(EtsInstruments.ParkingControl, "parking brake") + ", " + Describe(EtsInstruments.HazardControl, "hazard switch") +
                          "; a free hand's fresh trigger press on one is the control's (acts at " + EngagePull.ToString("0.0") + " pull), the stalk moves a place per " +
                          (StalkCommit * 100f).ToString("0.0") + " cm up (toward right) / down (toward left); a hand more than " + (RimMargin * 100f).ToString("0") +
                          " cm nearer the rim than the control is on the wheel.");
            }

            return any;
        }

        private string Describe(int c, string name)
        {
            if (!_have[c])
            {
                return name + " none";
            }

            return name + " zone " + _radius[c].ToString("0.000") + " m" +
                   (c == EtsInstruments.ParkingControl && !_point[c] ? " + " + LeverKnobRadius.ToString("0.000") + " m round the knob" : "") +
                   (c == EtsInstruments.StalkControl && !_point[c] ? " (tip " + _stalkTipFromRim.ToString("0.000") + " m from the rim, zone " + _stalkShift.ToString("0.000") + " m toward the driver)" : "") +
                   (_point[c] ? " (fixed point)" : "");
        }

        /// <summary>A hand's distance to a control as a share of its zone (1 = on the edge).</summary>
        private float Share(int c, Vector3 at, out float metres)
        {
            metres = DistanceToSegment(at, _a[c], _b[c]);
            float share = metres / _radius[c];

            if (c == EtsInstruments.ParkingControl && !_point[c])
            {
                float knob = (at - _b[c]).magnitude;

                if (knob / LeverKnobRadius < share)
                {
                    share = knob / LeverKnobRadius;
                    metres = knob;
                }
            }

            return share;
        }

        private static float DistanceToSegment(Vector3 p, Vector3 a, Vector3 b)
        {
            Vector3 ab = b - a;
            float length2 = ab.sqrMagnitude;
            float s = length2 > 1e-10f ? Mathf.Clamp01(Vector3.Dot(p - a, ab) / length2) : 0f;
            return (p - (a + ab * s)).magnitude;
        }

        private void StepHand(Hand hand, bool tracked, Vector3 inRig, Matrix4x4 rig, bool free, float fromRim)
        {
            bool captured = EtsVr.TriggerCaptured(hand.Right);
            float pull = EtsVr.TriggerRaw(hand.Right);
            int h = hand.Right ? 1 : 0;

            if (!captured)
            {
                hand.Engaged = None;
                hand.Acted = false;
                hand.StalkMoves = 0;
            }

            // Tracking lost (a frame or two, the controller hidden by the other hand): nothing is known, so nothing changes -
            // a press is neither handed back nor acted on, and a press that starts now is a pedal.
            if (!tracked)
            {
                EtsVr.SetControlHover(hand.Right, false);
                return;
            }

            // A hand that took hold of the rim mid-press drops the control.
            if (!free)
            {
                hand.Engaged = None;
            }

            int hover = None;
            float engagedShare = 0f;

            if (free)
            {
                Vector3 at = rig.MultiplyPoint3x4(inRig);
                float best = float.MaxValue;

                for (int c = 0; c < EtsInstruments.ControlCount; c++)
                {
                    if (!_have[c])
                    {
                        continue;
                    }

                    bool current = c == hand.Hover || c == hand.Engaged;
                    float share = Share(c, at, out float metres);

                    if (c == hand.Engaged)
                    {
                        engagedShare = share;
                    }

                    // Clearly nearer the rim than the control: the hand is on the wheel, and its trigger is a pedal.
                    float biased = current ? share * HoverBias : share;

                    if (share <= (current ? Stay : 1f) && metres < fromRim + RimMargin + (current ? RimStay : 0f) && biased < best)
                    {
                        best = biased;
                        hover = c;
                    }
                }
            }

            if (hand.Engaged != None)
            {
                hover = hand.Engaged;
            }

            // Lit, with a tick on coming onto it (to find it by feel), only while this trigger would act on it: up (a fresh press
            // will be the control's) or already the control's. A pedal held from elsewhere never acts on a control.
            int shown = pull <= 0f || captured ? hover : None;

            if (shown != None && shown != _shown[h] && hand.Engaged == None && Time.unscaledTime >= hand.NextTick)
            {
                hand.NextTick = Time.unscaledTime + TickGap;
                EtsVr.Pulse(hand.Right, TickAmplitude, TickSeconds);
            }

            _shown[h] = shown;
            hand.Hover = hover;
            EtsVr.SetControlHover(hand.Right, hover != None && hand.Engaged == None && !captured);

            if (!captured)
            {
                return;
            }

            if (hand.Engaged == None)
            {
                if (hand.Acted)
                {
                    return;
                }

                if (hover == None)
                {
                    // Off the control before the press did anything: the pedal gets it back only where it is a brake now.
                    if (IsBrake(hand))
                    {
                        EtsVr.ReleaseCapture(hand.Right);
                    }

                    return;
                }

                if (pull >= EngagePull)
                {
                    Engage(hand, hover, inRig);
                }

                return;
            }

            if (hand.Engaged != EtsInstruments.StalkControl)
            {
                return;
            }

            Vector3 moved = rig.MultiplyVector(inRig - hand.EngageInRig);
            Transform frame = _truck.transform;
            float up = Vector3.Dot(moved, frame.up);
            float side = Vector3.Dot(moved, frame.right);

            // A stalk press that has switched nothing, on a trigger that is the brake now: the brake back after a still
            // moment, or once the hand has left the stalk (a hand resting there, squeezing to brake).
            if (!hand.Acted && IsBrake(hand) &&
                ((Time.time - hand.EngagedAt > StalkBrakeWait && Mathf.Abs(up) < StalkStill) || engagedShare > Stay))
            {
                hand.Engaged = None;
                EtsVr.ReleaseCapture(hand.Right);
                EtsVr.SetControlHover(hand.Right, false);
                Log(hand, "stalk let go: the trigger is the brake again");
                return;
            }

            MoveStalk(hand, up, side);
        }

        private void Engage(Hand hand, int c, Vector3 inRig)
        {
            hand.Engaged = c;
            hand.Acted = false;
            hand.StalkMoves = 0;
            hand.StalkWay = 0;
            hand.EngagedAt = Time.time;
            hand.EngageInRig = inRig;

            switch (c)
            {
                case EtsInstruments.StalkControl:
                    hand.StalkBase = StalkT(_truck.Indicator);
                    break;

                case EtsInstruments.ParkingControl:
                    _truck.ParkingBrake = !_truck.ParkingBrake;
                    _dash.PlayPress(EtsCabSignal.ParkingStick);
                    hand.Acted = true;
                    EtsVr.Pulse(hand.Right, 0.3f, 0.03f);
                    Log(hand, "parking brake " + (_truck.ParkingBrake ? "set" : "released") + " by hand");
                    break;

                default:
                    _truck.ToggleIndicator(2);
                    _dash.PlayPress(EtsCabSignal.HazardSwitch);
                    hand.Acted = true;
                    EtsVr.Pulse(hand.Right, 0.3f, 0.03f);
                    Log(hand, "hazard lights " + (_truck.Indicator == 2 ? "on" : "off") + " by hand");
                    break;
            }
        }

        /// <summary>Where the stalk is drawn for an indicator state: 0 down (left), 0.5 the middle (off, and with the hazards), 1 up (right).</summary>
        private static float StalkT(int indicator)
        {
            return indicator == -1 ? 0f : indicator == 1 ? 1f : 0.5f;
        }

        /// <summary>
        /// The stalk under a held trigger, `up` and `side` the hand's motion since it took hold (truck frame, m): drawn
        /// following the hand (never back past a place it has switched to), and moved one place at StalkCommit, a second at
        /// twice that the same way.
        /// </summary>
        private void MoveStalk(Hand hand, float up, float side)
        {
            float drawn = hand.StalkBase + 0.5f * Mathf.Clamp(up / StalkCommit, -2f, 2f);

            if (hand.StalkWay != 0)
            {
                float now = StalkT(_truck.Indicator);
                drawn = hand.StalkWay > 0 ? Mathf.Max(drawn, now) : Mathf.Min(drawn, now);
            }

            _dash.HoldStalk(drawn);

            if (hand.StalkMoves >= MaxStalkMoves)
            {
                return;
            }

            // Mostly up or down: a hand swept sideways (to the rim, away) switches nothing.
            int way = up > 0f ? 1 : -1;
            float needed = StalkCommit * (hand.StalkMoves + 1);

            if (Mathf.Abs(up) < needed || Mathf.Abs(up) < Mathf.Abs(side) || (hand.StalkWay != 0 && way != hand.StalkWay))
            {
                return;
            }

            int before = _truck.Indicator;
            int at = before == 1 || before == -1 ? before : 0;
            int to = Mathf.Clamp(at + way, -1, 1);

            if (to == at)
            {
                // Pushed on the way it already is: the stalk is at its end, nothing switches.
                return;
            }

            _truck.SetIndicator(to);
            hand.StalkMoves++;
            hand.StalkWay = way;
            hand.Acted = true;
            EtsVr.Pulse(hand.Right, 0.3f, 0.03f);
            int now2 = _truck.Indicator;
            Log(hand, "stalk " + (way > 0 ? "up" : "down") + " -> " + (now2 == 1 ? "right indicator" : now2 == -1 ? "left indicator" : "indicator off") +
                      (before != 0 && before != now2 ? " (was " + (before == 2 ? "hazards" : before == 1 ? "right" : "left") + ")" : ""));
        }

        private void Log(Hand hand, string what)
        {
            if (_actionLogs >= MaxActionLogs)
            {
                return;
            }

            _actionLogs++;
            Debug.Log("[SomniumEuroTruck] [You] cab: " + what + " (" + (hand.Right ? "right" : "left") + " hand" +
                      (_actionLogs == MaxActionLogs ? "; no more of these lines this visit" : "") + ").");
        }
    }
}
