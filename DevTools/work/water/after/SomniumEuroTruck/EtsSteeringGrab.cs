using SomniumSpace.Bridge;
using SomniumSpace.Bridge.Player.Components;
using UnityEngine;
using UnityEngine.XR;

namespace SomniumSpace.Worlds.SomniumEuroTruck
{
    /// <summary>
    /// Turning the steering wheel with your hands, in a headset. Squeeze the grip with a hand on
    /// the rim and the wheel follows that hand round its axle; with both hands on it, it follows
    /// the line between them. Let go and it returns the way the game's own autocentring does
    /// (physics.sii). The left stick still steers whenever no hand is on the wheel.
    ///
    /// No XRI and no collider. The client's grip is XRI's Select on three interactors per hand
    /// (Player.XR.Body.prefab): "Grab" (interaction layer 3 Grabbable; near sphere 7.5 cm on
    /// physics layers 0, 7, 14, 23), "Near-Far Interactor" (layers 4 XRInteractable and 7 XRRay;
    /// near sphere 15 cm on physics layer 6 only) and "Climb Grab" (layer 1 Climbing). The cab is
    /// on the Vehicle layer (28) and carries no interactable, so a grip at the wheel selects
    /// nothing in the client and this is the only thing that answers it - nothing to fight. A
    /// collider on the wheel would also have become part of the truck's compound body (see
    /// EtsTruckDoor for what a stray collider did to its inertia).
    ///
    /// The hands are the Bridge's Body.LeftHand / RightHand: the client's LeftHandAnchor /
    /// RightHandAnchor, which carry its TrackedPoseDriver (the controller's pose) and hang under
    /// XR.Body - Body.Root, the rig EtsTruckSeat moves every LateUpdate. So the hands are only in
    /// the cab once the seat has placed the rig, and the seat calls <see cref="AfterRigPlaced"/>
    /// at the end of its LateUpdate. Read any earlier, a hand is where the truck was last frame
    /// (0.28 m behind at 90 km/h and 90 Hz).
    ///
    /// Taking hold is decided on where the hand is now. Turning is decided on how the hand moved
    /// INSIDE THE RIG (tracking space) since last frame, carried into the wheel's frame through
    /// this frame's rig and truck: the rig follows the truck's yaw only (the seat), so the cab
    /// pitching and rolling under a level rig, or the seat re-taking the head's rest, moves the
    /// hands against the wheel without the player moving them - and must not turn it.
    ///
    /// Angles are measured in the wheel's REST frame (its mount's parent times the rest
    /// rotation): +Y the axle toward the driver, +Z the top of the rim, +X the driver's right.
    /// atan2(x, z) is then 0 at twelve o'clock and grows clockwise as the driver sees it, the
    /// same sense as the controller's Euler(0, +a, 0) turn, so a hand angle is a wheel angle.
    ///
    /// After the hands (2026-09-25), the same call steps the cab's switches worked by a free hand's trigger
    /// (EtsCabControls: the stalk, the hazard switch, the parking brake) and the wheel's vibration on the hands that hold
    /// the rim (EtsWheelHaptics); both are put away with the hands (LetGo) and whenever this call stops coming (Update).
    /// The first grab of the wheel after sitting in starts the engine, which in a headset waits for it (or a pedal, or the
    /// stick - EtsTruckController.RequestEngineStart), so the hand on the rim feels the starter.
    /// </summary>
    public sealed class EtsSteeringGrab : MonoBehaviour
    {
        public EtsTruckController Truck;
        public EtsTruckSeat Seat;

        /// <summary>How far a hand may be from the rim's centre line and still take it (m).</summary>
        public float Reach = 0.12f;

        /// <summary>A holding hand further than this from the rim has let go (tracking lost, pulled away).</summary>
        public float LetGoDistance = 0.35f;

        /// <summary>Grip hysteresis: squeezed past On takes hold, eased below Off lets go.</summary>
        public float GripOn = 0.55f;
        public float GripOff = 0.35f;

        /// <summary>A hand turning more than this in one frame is a tracking jump, not a turn; the frame is skipped.</summary>
        private const float MaxStepDegrees = 60f;

        /// <summary>Two hands closer together than this give no usable line; their angles are averaged instead.</summary>
        private const float ChordMin = 0.15f;

        /// <summary>A hand this near the axle has no meaningful angle.</summary>
        private const float HubDeadRadius = 0.05f;

        // Measured (probe 9771) on /vehicle/truck/upgrade/steering_w/daf_xf_euro6/default.pmd,
        // the showroom DAF's wheel: hand slots slot_0..3 at radius 0.219 m and y -0.016, outermost
        // vertex 0.239 m. Across the demo's wheels the slots sit 0.018-0.022 m inside the outermost
        // vertex, so the rim is measured from the built mesh at run time and these are the fallback.
        private const float FallbackRimRadius = 0.22f;
        private const float FallbackRimHeight = -0.016f;
        private const float TubeInset = 0.02f;

        private sealed class Hand
        {
            public bool Right;
            public bool GripDown;
            public bool Holding;
            public bool InReach;
            public bool HaveLast;
            public Vector3 LastInRig;

            /// <summary>This frame's distance from the rim's centre line (m); MaxValue when untracked.</summary>
            public float FromRim = float.MaxValue;
        }

        private readonly Hand _left = new Hand { Right = false };
        private readonly Hand _right = new Hand { Right = true };

        // The cab's switches by hand (a free hand's trigger) and the wheel's vibration (the holding hands), both stepped at
        // the end of AfterRigPlaced; the seat stops calling that when the player gets out, so Update also puts them away.
        private readonly EtsCabControls _controls = new EtsCabControls();
        private readonly EtsWheelHaptics _haptics = new EtsWheelHaptics();
        private int _placedFrame = -10;
        private int _heldMask;
        private float _wheelDegrees;
        private float _rimRadius = -1f;
        private float _rimHeight = FallbackRimHeight;
        private Transform _measuredFor;
        private float _nextLockBump;
        private bool _reported;
        private bool _described;

        /// <summary>Whether any hand is on the wheel.</summary>
        public bool Holding => _heldMask != 0;

        /// <summary>
        /// Gets or adds the local player's one wheel grab on the bootstrap object and hands it to the seat there
        /// (PLAN §3.2; EtsNet.Awake calls it only with EtsFeatures.NewFlow, after EtsTruckSeat.Ensure). The seat
        /// rebinds its Truck whenever it binds a rig (EtsTruckSeat.Bind), and drives it from its LateUpdate.
        /// </summary>
        public static EtsSteeringGrab Ensure(GameObject host)
        {
            if (host == null)
            {
                return null;
            }

            EtsSteeringGrab grab = host.GetComponent<EtsSteeringGrab>();

            if (grab == null)
            {
                grab = host.AddComponent<EtsSteeringGrab>();
            }

            EtsTruckSeat seat = host.GetComponent<EtsTruckSeat>();

            if (seat != null)
            {
                grab.Seat = seat;
                seat.Wheel = grab;

                if (grab.Truck == null)
                {
                    grab.Truck = seat.Truck;
                }
            }

            return grab;
        }

        /// <summary>
        /// The driver only (a passenger has no wheel, PLAN §5.5), and not while the tablet app is up on the new
        /// flow: the hand holding the tablet is near the rim ([tablet §4.5]). On the old flow IsDriver is Seated
        /// (the seat index is always 0) and the tablet app does not exist, so the rule is today's.
        /// </summary>
        private bool Usable => EtsVr.Present && UsableInCab;

        /// <summary>
        /// Usable less the headset test, which nothing off a headset can make true (the play-mode tests read this one).
        /// New flow: the wheel is only ever the truck the local player sits in as its driver (EtsTruckSeat.Rig, which the
        /// seat announces as EtsLocalTruck.Occupied) - never a truck it was bound to before, and never with the app up.
        /// </summary>
        private bool UsableInCab =>
            Truck != null && Seat != null && Seat.IsDriver &&
            Truck.Driven && Truck.Placed && Truck.InputEnabled && Truck.SteeringWheel != null &&
            (!EtsFeatures.NewFlow || (!EtsTabletApp.IsVisible && Seat.Rig != null && Seat.Rig.Controller == Truck));

        private bool _subscribed;

        private void OnEnable()
        {
            if (EtsFeatures.NewFlow && !_subscribed)
            {
                _subscribed = true;
                EtsLocalTruck.OccupiedChanged += OnOccupiedChanged;
            }
        }

        /// <summary>
        /// New flow (PLAN §3.1, B2): the wheel follows the truck the local player sits in. Hands on the old truck's wheel
        /// let go first, so its steering goes back to its own stick and autocentring (ReleaseWheel) rather than staying
        /// held by hands that are no longer in it. Getting out (null) keeps the truck the seat binds next (its own, or
        /// none): the seat sets Truck again whenever it binds (EtsTruckSeat.Bind).
        /// </summary>
        private void OnOccupiedChanged(EtsTruckRig rig)
        {
            LetGo();

            if (rig != null && rig.Controller != null)
            {
                Truck = rig.Controller;
            }
        }

        private void Update()
        {
            // The seat stops calling once the player is out, the menu is up or there is no headset.
            if (_heldMask != 0 && !Usable)
            {
                LetGo();
            }

            // Likewise the switches' lights and the wheel's vibration: put away when AfterRigPlaced has not run lately.
            if ((_controls.Active || _haptics.Active) && (Time.frameCount - _placedFrame > 2 || !Usable))
            {
                _controls.Clear();
                _haptics.Stop();
            }
        }

        private void OnDisable()
        {
            if (_subscribed)
            {
                _subscribed = false;
                EtsLocalTruck.OccupiedChanged -= OnOccupiedChanged;
            }

            LetGo();
        }

        /// <summary>Drops both hands and hands the steering back to the stick and the autocentring; the vibration stops, the switches' lights go out.</summary>
        public void LetGo()
        {
            _left.Holding = _right.Holding = false;
            _left.InReach = _right.InReach = false;
            _left.HaveLast = _right.HaveLast = false;

            if (_heldMask != 0 && Truck != null)
            {
                Truck.ReleaseWheel();
            }

            _heldMask = 0;
            _haptics.Stop();
            _controls.Clear();
        }

        /// <summary>
        /// Called by EtsTruckSeat at the end of its LateUpdate, after the rig (and so both hands)
        /// has been put in the cab for this frame.
        /// </summary>
        public void AfterRigPlaced()
        {
            if (!Usable || !Truck.TryGetSteeringWheelFrame(out Vector3 hub, out Quaternion rest))
            {
                LetGo();
                return;
            }

            MeasureRim();

            // Once, the first time a headset player sits at the wheel: whether the runtime reports
            // the grip at all. A grip that is never reported reads as never squeezed, silently.
            if (!_described)
            {
                _described = true;
                Debug.Log("[SomniumEuroTruck] VR wheel ready. " + EtsVr.DescribeGrip());
            }

            // World -> wheel rest frame, centred on the rim's plane.
            Matrix4x4 toWheel = Matrix4x4.TRS(hub + rest * new Vector3(0f, _rimHeight, 0f), rest, Vector3.one).inverse;

            bool haveLeft = HandInRig(false, out Vector3 leftInRig, out Matrix4x4 leftRig);
            bool haveRight = HandInRig(true, out Vector3 rightInRig, out Matrix4x4 rightRig);
            Vector3 left = toWheel.MultiplyPoint3x4(leftRig.MultiplyPoint3x4(leftInRig));
            Vector3 right = toWheel.MultiplyPoint3x4(rightRig.MultiplyPoint3x4(rightInRig));

            Step(_left, haveLeft, left, EtsVr.GripLeft);
            Step(_right, haveRight, right, EtsVr.GripRight);

            // The switches by hand: only a free hand (tracked, not holding the rim, grip open) works one. Then the
            // vibration, on the hands holding the rim now.
            _placedFrame = Time.frameCount;
            _controls.Step(Truck, toWheel, _rimRadius, rest * Vector3.up,
                           haveLeft, leftInRig, leftRig, haveLeft && !_left.Holding && !_left.GripDown, _left.FromRim,
                           haveRight, rightInRig, rightRig, haveRight && !_right.Holding && !_right.GripDown, _right.FromRim);
            _haptics.Step(Truck, _left.Holding, _right.Holding);

            int mask = (_left.Holding ? 1 : 0) | (_right.Holding ? 2 : 0);

            if (mask == 0)
            {
                if (_heldMask != 0)
                {
                    Truck.ReleaseWheel();
                }

                _heldMask = 0;
                Remember(_left, haveLeft, leftInRig);
                Remember(_right, haveRight, rightInRig);
                return;
            }

            // First hand on: the wheel starts from wherever the stick or the autocentring left it.
            if (_heldMask == 0)
            {
                _wheelDegrees = Truck.SteerInput * EtsTruckController.WheelDegreesPerUnit;
            }

            float delta = 0f;

            // A hand taking hold or letting go this frame only re-bases; the wheel does not jump.
            if (mask == _heldMask)
            {
                // Last frame's hand positions, carried through THIS frame's rig and wheel: what is
                // left between then and now is the hands' own movement.
                Vector2 leftNow = Flat(left);
                Vector2 rightNow = Flat(right);
                Vector2 leftThen = Flat(toWheel.MultiplyPoint3x4(leftRig.MultiplyPoint3x4(_left.LastInRig)));
                Vector2 rightThen = Flat(toWheel.MultiplyPoint3x4(rightRig.MultiplyPoint3x4(_right.LastInRig)));

                if (mask == 3 && _left.HaveLast && _right.HaveLast &&
                    (rightNow - leftNow).magnitude > ChordMin && (rightThen - leftThen).magnitude > ChordMin)
                {
                    // Both hands: the line between them turns exactly as the wheel does, wherever
                    // on the rim they hold it.
                    delta = Mathf.DeltaAngle(Angle(rightThen - leftThen), Angle(rightNow - leftNow));
                }
                else
                {
                    float sum = 0f;
                    int count = 0;
                    Accumulate(_left, leftThen, leftNow, ref sum, ref count);
                    Accumulate(_right, rightThen, rightNow, ref sum, ref count);
                    delta = count > 0 ? sum / count : 0f;
                }

                if (Mathf.Abs(delta) > MaxStepDegrees)
                {
                    delta = 0f;
                }
            }

            Remember(_left, haveLeft, leftInRig);
            Remember(_right, haveRight, rightInRig);
            _heldMask = mask;

            float limit = EtsTruckController.WheelDegreesPerUnit;
            float wanted = _wheelDegrees + delta;
            _wheelDegrees = Mathf.Clamp(wanted, -limit, limit);
            Truck.HoldWheel(_wheelDegrees / limit);

            // Past the lock the hands slide on the rim; say so through the controllers.
            if (Mathf.Abs(wanted) > limit && Time.unscaledTime >= _nextLockBump)
            {
                _nextLockBump = Time.unscaledTime + 0.15f;
                if (_left.Holding) EtsVr.Pulse(false, 0.5f, 0.04f);
                if (_right.Holding) EtsVr.Pulse(true, 0.5f, 0.04f);
            }
        }

        private static void Remember(Hand hand, bool tracked, Vector3 inRig)
        {
            hand.HaveLast = tracked;

            if (tracked)
            {
                hand.LastInRig = inRig;
            }
        }

        private static void Accumulate(Hand hand, Vector2 then, Vector2 now, ref float sum, ref int count)
        {
            if (!hand.Holding || !hand.HaveLast || now.magnitude < HubDeadRadius || then.magnitude < HubDeadRadius)
            {
                return;
            }

            sum += Mathf.DeltaAngle(Angle(then), Angle(now));
            count++;
        }

        private static Vector2 Flat(Vector3 inWheel)
        {
            return new Vector2(inWheel.x, inWheel.z);
        }

        /// <summary>0 at the top of the rim, growing clockwise as the driver sees it (toward +X).</summary>
        private static float Angle(Vector2 flat)
        {
            return Mathf.Atan2(flat.x, flat.y) * Mathf.Rad2Deg;
        }

        private void Step(Hand hand, bool tracked, Vector3 inWheel, float grip)
        {
            bool wasDown = hand.GripDown;
            hand.GripDown = grip >= (wasDown ? GripOff : GripOn);
            bool squeezed = hand.GripDown && !wasDown;

            if (!tracked)
            {
                hand.Holding = false;
                hand.InReach = false;
                hand.FromRim = float.MaxValue;
                return;
            }

            float radial = Flat(inWheel).magnitude;
            float fromRim = Mathf.Sqrt((radial - _rimRadius) * (radial - _rimRadius) + inWheel.y * inWheel.y);
            hand.FromRim = fromRim;
            bool inReach = fromRim <= Reach;

            if (hand.Holding)
            {
                if (!hand.GripDown || fromRim > LetGoDistance)
                {
                    hand.Holding = false;
                }
            }
            else if (squeezed && inReach)
            {
                hand.Holding = true;
                EtsVr.Pulse(hand.Right, 0.35f, 0.05f);

                // In a headset the engine waits, after sitting in, for the driver to take the wheel (or a pedal, or the stick:
                // EtsTruckController.Update): it starts next frame, and this hand feels the starter (EtsWheelHaptics).
                if (!Truck.EngineRunning)
                {
                    Truck.RequestEngineStart("the first grab of the wheel");
                }

                if (!_reported)
                {
                    _reported = true;
                    Debug.Log("[SomniumEuroTruck] VR wheel: taken with the " + (hand.Right ? "right" : "left") + " hand, " +
                              fromRim.ToString("0.00") + " m from the rim (rim radius " + _rimRadius.ToString("0.000") + " m).");
                }
            }

            // Coming within reach with the hand open: a light tick, so the rim can be found by feel.
            if (inReach && !hand.InReach && !hand.Holding && !hand.GripDown)
            {
                EtsVr.Pulse(hand.Right, 0.1f, 0.02f);
            }

            hand.InReach = inReach;
        }

        /// <summary>
        /// A hand's position inside the frame that carries it, and that frame's local-to-world.
        /// First the Bridge's hand (the client's hand anchor - the transform its own interactors
        /// follow, AomBridge.HandOf) inside Body.Root; else the controller's device position in
        /// tracking space, carried into the world through the headset's (AomVr.ToWorld).
        /// </summary>
        private static bool HandInRig(bool right, out Vector3 inRig, out Matrix4x4 rigToWorld)
        {
            ISomniumPlayerBody body = SomniumBridge.PlayersContainer?.LocalPlayer?.References?.Body;
            Transform hand = body == null ? null : right ? body.RightHand : body.LeftHand;
            Transform root = body?.Root;

            if (hand != null && root != null)
            {
                rigToWorld = root.localToWorldMatrix;
                inRig = root.InverseTransformPoint(hand.position);
                return true;
            }

            inRig = default;
            rigToWorld = Matrix4x4.identity;
            Camera head = EtsVr.Head();
            InputDevice headset = InputDevices.GetDeviceAtXRNode(XRNode.Head);

            if (head == null || !headset.isValid ||
                !headset.TryGetFeatureValue(CommonUsages.devicePosition, out Vector3 headAt) ||
                !headset.TryGetFeatureValue(CommonUsages.deviceRotation, out Quaternion headTurn) ||
                !EtsVr.TryGetDevicePosition(right, out inRig))
            {
                return false;
            }

            rigToWorld = head.transform.localToWorldMatrix * Matrix4x4.TRS(headAt, headTurn, Vector3.one).inverse;
            return true;
        }

        /// <summary>
        /// The rim's radius and height in the wheel's own frame, from the built mesh: its outermost
        /// vertex less the tube's half thickness. The mesh is the showroom wheel hung under the
        /// mount (EtsTruckFactory.MountInterior); a turn about the axle changes neither number.
        /// </summary>
        private void MeasureRim()
        {
            Transform wheel = Truck.SteeringWheel;

            if (_measuredFor == wheel && _rimRadius > 0f)
            {
                return;
            }

            _measuredFor = wheel;
            float outer = 0f, heightSum = 0f;
            int heightCount = 0;
            Matrix4x4 toWheel = wheel.worldToLocalMatrix;
            MeshFilter[] filters = wheel.GetComponentsInChildren<MeshFilter>(true);

            foreach (MeshFilter filter in filters)
            {
                Mesh mesh = filter.sharedMesh;

                if (mesh == null || !mesh.isReadable)
                {
                    continue;
                }

                Matrix4x4 toLocal = toWheel * filter.transform.localToWorldMatrix;

                foreach (Vector3 v in mesh.vertices)
                {
                    Vector3 p = toLocal.MultiplyPoint3x4(v);
                    outer = Mathf.Max(outer, new Vector2(p.x, p.z).magnitude);
                }
            }

            foreach (MeshFilter filter in filters)
            {
                Mesh mesh = filter.sharedMesh;

                if (mesh == null || !mesh.isReadable)
                {
                    continue;
                }

                Matrix4x4 toLocal = toWheel * filter.transform.localToWorldMatrix;

                foreach (Vector3 v in mesh.vertices)
                {
                    Vector3 p = toLocal.MultiplyPoint3x4(v);

                    if (new Vector2(p.x, p.z).magnitude > outer - 2f * TubeInset)
                    {
                        heightSum += p.y;
                        heightCount++;
                    }
                }
            }

            bool measured = outer > 0.12f && outer < 0.4f;
            _rimRadius = measured ? outer - TubeInset : FallbackRimRadius;
            _rimHeight = measured && heightCount > 0 ? heightSum / heightCount : FallbackRimHeight;
            Debug.Log("[SomniumEuroTruck] VR wheel: rim radius " + _rimRadius.ToString("0.000") + " m at height " + _rimHeight.ToString("0.000") +
                      (measured ? " (from the wheel mesh, outermost vertex " + outer.ToString("0.000") + " m)." : " (fallback; the wheel mesh gave " + outer.ToString("0.000") + " m)."));
        }
    }
}
