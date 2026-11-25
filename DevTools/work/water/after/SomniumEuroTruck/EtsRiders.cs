using System;
using System.Collections.Generic;
using Fusion;
using SomniumSpace.Bridge;
using SomniumSpace.Bridge.Player;
using SomniumSpace.Bridge.Player.Components;
using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
using UnityEngine;

namespace SomniumSpace.Worlds.SomniumEuroTruck
{
    /// <summary>
    /// Riders: players carried by the truck, trailer or AI car they stand or sit on, and every other client's
    /// bodies put on this client's drawn vehicle - the Main Build's elevator method rebuilt for vehicles
    /// (owner B; [elev §7], PLAN.md §3.3, §5.4, §7.B). The math is EtsRiderMath (pure, tested offline by
    /// probe 1842); this is where it meets the scene.
    ///
    /// Three jobs, all per frame and all allocation-free once riders are known:
    ///  1. Publish the local player's rider record through EtsNet.SetLocalRider: which vehicle, which seat
    ///     (0 driver, 1 passenger, 255 standing), and the body root's pose in the vehicle's frame as THIS client
    ///     draws the vehicle. Written at most 20 Hz, only while it changes, plus EtsRiderMath.SettleWrites
    ///     records after it stops (probe 1842 T3b: without them a stopped walker is drawn 277 mm past the spot).
    ///  2. Carry the local standing rider by the vehicle's motion since last frame (EtsRiderMath.CarryRoot) and
    ///     Physics.SyncTransforms(), as the client's teleports do (NonXR/NonXRMotionFeature.cs:32-48); decide
    ///     locally whether they are still on it (EtsRiderMath.InsideBox/Stays), and step on by a ray down.
    ///  3. Put every remote rider's body on this client's drawn vehicle: the rigid correction EtsRiderMath.Correct
    ///     written to BOTH the Fusion NetworkTransforms the client's receive drivers copy from AND the Bridge
    ///     transforms they drive, exactly what MobilePlatformMover.UseLocalOffsetFromDriver does
    ///     (MB/DynamicObjects/Additional/MobilePlatformMover.cs:167-189; its comment at :184-186: writing only one
    ///     makes trackers lag a frame and jitter against the head).
    ///
    /// EarlyPass and LatePass are called by EtsNet (Update after the puppets are drawn, and LateUpdate), not by
    /// an Update of this component's own at order -95: EtsNet is the one place the frame order is written down
    /// (PLAN §3.3, §5.4). EarlyPass must land before the client's order-0 scripts - the receive drivers and
    /// NonXRRootsMovementHandler, which places a desktop avatar in Update ([elev §1.6]) - and LatePass after
    /// Fusion's Render (probe 1621: Render runs after Update) and before VRIK at order 9998.
    ///
    /// Frames are poses without scale (EtsRiderMath). A rider record names its vehicle the way EtsNet draws it
    /// (PLAN §7.A): kind, index and serial, and EtsNet.TryGetVehicleFrame(kind, index, serial) gives every client
    /// its own drawn transform for that key. What the local player sits in or stands on is named here (KeyOf):
    ///   - a truck, own or puppet: kind Tractor, index = the owner's slot (EtsTruckRig.OwnerSlot; the own truck's is
    ///     EtsNet.MySlot);
    ///   - a trailer: kind Trailer, index = the slot of the truck it belongs to (EtsLocalTruck.Own, EtsNet.AllVehicles,
    ///     or the trailer's own truck);
    ///   - an AI car: kind TrafficCar, EtsTraffic.TryIdentifyCar's global slot and 4-bit serial.
    /// A truck's and a trailer's serial is not on any contract member, so it is found by asking TryGetVehicleFrame
    /// which serial answers with exactly this transform (at most 256 cheap calls, once per new vehicle), and
    /// re-checked with one call every frame, so a new truck under the same transform is renamed at once. A vehicle
    /// EtsNet does not draw under any key is carried locally and published as nothing; the log says so once.
    ///
    /// The passenger's side (PLAN §5.5): EtsTruckDoor asks EtsNet.RequestSeat and holds this publishing meanwhile
    /// (HoldPublishing), because EtsNet writes the requesting record itself; when the answer comes the record is
    /// written afresh at once, or cleared, so a request never lingers in the slot. A remote record that is still
    /// requesting is never placed: its offset is not a seat yet.
    /// </summary>
    public sealed class EtsRiders : MonoBehaviour
    {
        public static EtsRiders Instance { get; private set; }

        /// <summary>Gets or adds the one EtsRiders on the bootstrap object (PLAN §3.2; EtsNet.Awake calls it only with EtsFeatures.NewFlow).</summary>
        public static EtsRiders Ensure(GameObject host)
        {
            if (host == null)
            {
                return null;
            }

            EtsRiders riders = host.GetComponent<EtsRiders>();
            return riders != null ? riders : host.AddComponent<EtsRiders>();
        }

        /// <summary>
        /// VR players standing on a turning truck turn with it (Q-B1, settled yes). On a monitor the client owns
        /// the facing (NonXRMovementProvider.cs:105-116), as it does on its own elevators, so it is never turned.
        /// </summary>
        public bool CarryYawInVr = true;

        /// <summary>A passenger may get out only below this speed (Q-B2, settled: 10 km/h). EtsTruckSeat.CanGetOut reads it.</summary>
        public float PassengerExitMaxKmh = 10f;

        /// <summary>The most rider records written per second ([elev §7.2]; the slot itself is written at most 30 Hz, [net §2.4]).</summary>
        public float MaxWriteHz = 20f;

        private const float BindEvery = 1f;
        private const float ReportEvery = 5f;
        private const float SeatLookupEvery = 1f;

        /// <summary>The step-on ray starts this far above the feet and reaches this far down ([elev §7.3.1 step 4]).</summary>
        private const float EnterRayUp = 0.6f;
        private const float EnterRayLength = 1.5f;

        /// <summary>The seat value of a standing rider (EtsRider.Seat).</summary>
        public const int Standing = 255;

        private sealed class Remote
        {
            public int PlayerId;
            public ISomniumPlayer Player;
            public bool Xr;
            public int Seen;

            public EtsTargetKind Kind;
            public int Index;
            public int Seat;
            public Vector3 Offset;
            public float YawLocal;

            public Transform Frame;
            public Pose FrameEarly;
            public Pose FramePrev;
            public bool HaveFramePrev;
            public float FrameSpeed;

            public Vector3 Target;
            public float TargetYaw;

            // The client's Fusion transforms for this player (Player.*.Part.Network: Body/Root, Head, LeftHand,
            // RightHand, each a PlayerTransformNetworkObject plus a Fusion NetworkTransform:
            // Player.XR.Part.Network.prefab:409,423) and their poses as Fusion last rendered them.
            public Transform NtRoot, NtHead, NtLeft, NtRight;
            public Pose RawRoot, RawHead, RawLeft, RawRight;
            public bool HaveRaw;
            public float NextBind;
            public bool BindReported;

            public bool LegsOff;
            public bool FallingOff;

            public float WorstBefore;
            public float WorstAfter;
        }

        private readonly Dictionary<int, Remote> _remotes = new Dictionary<int, Remote>();
        private readonly List<int> _stale = new List<int>();
        private int _pass;

        // The local player.
        private ISomniumPlayer _local;
        private Transform _root;
        private CharacterController _capsule;
        private EtsTruckSeat _seat;
        private float _nextSeatLookup;

        // What the local player stands on, and the frame the last pass saw it at.
        private Transform _platform;
        private BoxCollider _platformBox;
        private Transform _beforeOf;
        private Pose _before;
        private Pose _early;
        private float _airborneSince = -1f;
        private bool _legsOff;

        // The carry this frame, for the carry-lost count.
        private int _carriedFrame = -1;
        private Vector3 _carriedTo;
        private Vector3 _travel;
        private float _carryDt;
        private int _carryLost;
        private int _carryFrames;
        private int _carryChecked;

        // Publishing.
        private EtsRiderMath.WriteGate _gate;
        private bool _published;
        private EtsRider _last;
        private int _epoch;
        private bool _hold;
        private bool _republish;

        // The network key of the frame the local player rides (KeyOf): found once per frame transform, re-checked
        // every write. _keyNextTry throttles the search while EtsNet has no key for it (1 s).
        private Transform _keyFrame;
        private bool _keyKnown;
        private EtsTargetKind _keyKind;
        private int _keyIndex;
        private int _keySerial;
        private float _keyNextTry;
        private Transform _unidentifiedReported;
        private EtsTraffic _traffic;

        /// <summary>How often a frame EtsNet does not draw under any key is looked up again, seconds.</summary>
        private const float KeyRetryEvery = 1f;

        /// <summary>
        /// Truck and trailer serials are one byte on the wire (PLAN §5.3 int 33 bits 12-19), so the search for the one
        /// TryGetVehicleFrame answers to is bounded; a car's is its 4-bit serial from EtsTraffic.TryIdentifyCar.
        /// </summary>
        private const int SerialCount = 256;

        // Remote bodies: one scene search serves every unbound rider (Bind), at most once per BindEvery.
        private float _nextSearch;
        private NetworkBehaviour[] _found;

        private float _nextReport;
        private bool _frameMovedReported;

        /// <summary>
        /// What the local player rides or stands on (the truck they sit in, or the vehicle under their feet), or null.
        /// The tablet reads it every frame to carry a free-standing tablet with the cab (EtsTabletLink.RidingOn, PLAN C21).
        /// The seat is looked up here as well as in EarlyPass: until EtsNet calls the passes (Phase 3) nothing else would
        /// find it, and a seated driver's tablet would be left behind on the road.
        /// </summary>
        public Transform LocalPlatformFrame
        {
            get
            {
                if (_seat == null && Time.unscaledTime >= _nextSeatLookup)
                {
                    _nextSeatLookup = Time.unscaledTime + SeatLookupEvery;
                    _seat = GetComponent<EtsTruckSeat>();
                }

                if (_seat != null && _seat.Seated && _seat.Truck != null)
                {
                    return _seat.Truck.transform;
                }

                return _platform != null ? _platform : null;
            }
        }

        /// <summary>
        /// While a passenger seat request is out (EtsTruckDoor -> EtsNet.RequestSeat), EtsNet writes the requesting
        /// rider record itself [net §2.7], so this stops publishing until the answer. The request is answered or
        /// times out within 3 s ([net §2.7] step 3); the hold lapses by itself after HoldLimit, so an answer that
        /// never comes cannot silence this player's rider record for the rest of the visit.
        /// When the hold ends the next EarlyPass writes the record afresh, or clears it for a player who rides nothing:
        /// otherwise a refused request from a player on foot would stay in the slot as "requesting" (nothing else
        /// clears a record this component did not write), and the owner would keep answering it. Anyone who calls
        /// EtsNet.RequestSeat (the door, the tablet's "Ride along") should hold around it.
        /// </summary>
        internal void HoldPublishing(bool hold)
        {
            if (_hold && !hold)
            {
                _republish = true;
            }

            _hold = hold;
            _holdUntil = hold ? Time.unscaledTime + HoldLimit : 0f;
        }

        /// <summary>Longer than RequestSeat's 3 s wait ([net §2.7]) plus a round trip.</summary>
        private const float HoldLimit = 5f;

        private float _holdUntil;

        /// <summary>Whether publishing is held now (HoldPublishing, until the answer or HoldLimit).</summary>
        private bool Held
        {
            get
            {
                if (_hold && Time.unscaledTime >= _holdUntil)
                {
                    _hold = false;
                    _republish = true;
                    Debug.Log("[SomniumEuroTruck] Riders: the seat request got no answer in " + HoldLimit.ToString("0") + " s; publishing again.");
                }

                return _hold;
            }
        }

        private void Awake()
        {
            Instance = this;
        }

        private void OnDestroy()
        {
            foreach (KeyValuePair<int, Remote> pair in _remotes)
            {
                Restore(pair.Value);
            }

            _remotes.Clear();
            SetLocalLegs(false);

            if (Instance == this)
            {
                Instance = null;
            }
        }

        // ---------------------------------------------------------------- the passes

        /// <summary>
        /// Called by EtsNet.Update after the puppets are drawn (PLAN §5.4 step 2.4): publish the local rider from
        /// last frame's final pose, carry the local standing rider, then write every remote rider's body onto this
        /// client's drawn vehicle before the client's order-0 drivers copy it.
        /// </summary>
        public void EarlyPass()
        {
            _pass++;
            FindLocal();
            LocalEarly();
            RemotesEarly();
        }

        /// <summary>
        /// Called by EtsNet.LateUpdate (PLAN §5.4 step 6): after Fusion's Render has interpolated the remote
        /// transforms again, re-place the remote riders (so what hangs under them - Voice.Speaker under Body/Head -
        /// is heard from the truck), count lost carries, and remember the frames' poses for the next EarlyPass.
        /// </summary>
        public void LatePass()
        {
            LocalLate();
            RemotesLate();
            Report();
        }

        /// <summary>One line for the log.</summary>
        public string Describe()
        {
            string local = _seat != null && _seat.Seated ? "seat " + _seat.SeatIndex + " of " + (_seat.Truck != null ? _seat.Truck.name : "a truck")
                : _platform != null ? "standing on " + _platform.name : "on foot";
            int bound = 0;

            foreach (KeyValuePair<int, Remote> pair in _remotes)
            {
                if (pair.Value.NtRoot != null)
                {
                    bound++;
                }
            }

            return "riders: local " + local + (_published ? " (published, epoch " + _epoch + ")" : "") + "; remote " + _remotes.Count + " (" + bound + " bound)";
        }

        /// <summary>
        /// The remote riders on a monitor put on a vehicle by this frame's EarlyPass (EtsAvatarSync carries their drawn
        /// avatars in LateUpdate, as the client does on its elevators). Fills the caller's list; no allocation.
        /// </summary>
        public void PlacedDesktopRemotes(List<ISomniumPlayer> into)
        {
            into.Clear();

            foreach (KeyValuePair<int, Remote> pair in _remotes)
            {
                Remote remote = pair.Value;

                if (remote.Seen == _pass && remote.Player != null && remote.Frame != null && !remote.Xr)
                {
                    into.Add(remote.Player);
                }
            }
        }

        /// <summary>
        /// Every remote rider (monitor or headset) on a vehicle drawn here this pass, with that vehicle's frame
        /// (EtsAvatarCloth anchors their cloth and bones to it). Fills the caller's lists; no allocation.
        /// </summary>
        public void RidingRemotes(List<ISomniumPlayer> players, List<Transform> frames)
        {
            players.Clear();
            frames.Clear();

            foreach (KeyValuePair<int, Remote> pair in _remotes)
            {
                Remote remote = pair.Value;

                if (remote.Seen == _pass && remote.Player != null && remote.Frame != null)
                {
                    players.Add(remote.Player);
                    frames.Add(remote.Frame);
                }
            }
        }

        // ---------------------------------------------------------------- the local rider

        private void FindLocal()
        {
            ISomniumPlayer local = SomniumBridge.PlayersContainer?.LocalPlayer;
            Transform root = local?.References?.Body?.Root;

            if (root != _root)
            {
                // A new body (arrival, avatar reload): nothing learnt about the old one holds.
                LeavePlatform(null);
                _root = root;
                _capsule = root != null ? root.GetComponent<CharacterController>() : null;
                _beforeOf = null;
            }

            _local = local;

            if (_seat == null && Time.unscaledTime >= _nextSeatLookup)
            {
                _nextSeatLookup = Time.unscaledTime + SeatLookupEvery;
                _seat = GetComponent<EtsTruckSeat>();
            }
        }

        private bool Seated => _seat != null && _seat.Seated && _seat.Truck != null;

        private void LocalEarly()
        {
            _carriedFrame = -1;

            if (_root == null)
            {
                return;
            }

            bool seated = Seated;

            if (seated && _platform != null)
            {
                LeavePlatform("sat down");
            }

            Transform frame = seated ? _seat.Truck.transform : _platform;
            bool continuous = frame != null && frame == _beforeOf;

            // 0. A seat request has just been answered (HoldPublishing). EtsNet wrote the requesting record itself and
            // keeps it through a grant (no longer requesting, the seat's own offset: watchers draw the body in the seat
            // until this record replaces it) and drops it on a refusal (EtsNet.AnswerSeatRequest). So: a player who sits
            // or stands on something now writes the truth as a new target on the next continuous frame; one who rides
            // nothing clears, in case the answer left anything behind (EtsNet ignores a clear while a request is open).
            // The epoch steps once here as well: EtsNet numbered its request record "last epoch + 1" (RequestSeat), which is
            // exactly what this component's next new target would take, and a granted record and the first seated one then
            // share every field receivers key on (target, seat, serial, epoch; EtsRiderTrack.Key) - the watcher slid the
            // desktop body from the seat locator to its measured place instead of snapping (checker probe 2240 E: 9 frames,
            // up to 86 mm off both; 0 with this step). Advancing is always safe: epochs are only compared for equality.
            if (_republish && !Held)
            {
                _republish = false;
                _published = false;
                _gate = default;
                _epoch = (_epoch + 1) & 0xFF;

                if (frame == null)
                {
                    EtsNet.Instance?.ClearLocalRider();
                }
            }

            // 1. The record, from last frame's final pose against the frame as it stood then [elev §7.3.1 step 2].
            if (continuous)
            {
                Publish(seated, _before);
            }
            else if (frame == null)
            {
                Unpublish();
            }

            // A seated rider goes wherever the truck is set down (PLAN §3.5: its owner's job start, a respawn): the seat
            // places them in it every LateUpdate, the stream follows it (EtsTruckSeat.StreamFocus) and D's loading screen
            // covers the wait for ground. Nothing to do here but say so.
            if (seated && continuous && EtsRiderMath.Jumped(_before, new Pose(frame.position, frame.rotation)))
            {
                Debug.Log("[SomniumEuroTruck] Riders: the truck this player sits in (seat " + _seat.SeatIndex + ") was set down " +
                          Mathf.RoundToInt(Vector3.Distance(frame.position, _before.position)) + " m away; staying seated.");
            }

            // 2. The carry (standing only; a seat is placed by EtsTruckSeat.LateUpdate).
            if (!seated && continuous)
            {
                Pose now = new Pose(frame.position, frame.rotation);

                if (EtsRiderMath.Jumped(_before, now))
                {
                    LeavePlatform("the platform jumped " + Mathf.RoundToInt(Vector3.Distance(now.position, _before.position)) + " m");
                }
                else
                {
                    bool turn = CarryYawInVr && EtsVr.Present;
                    Pose carried = EtsRiderMath.CarryRoot(_before, now, new Pose(_root.position, _root.rotation), turn);
                    _root.SetPositionAndRotation(carried.position, carried.rotation);
                    Physics.SyncTransforms();
                    _carriedTo = carried.position;
                    _travel = now.position - _before.position;
                    _carriedFrame = Time.frameCount;
                    float dt = Time.deltaTime;
                    _carryDt = dt;
                    SetLocalLegs(dt > 0f && _travel.magnitude / dt > EtsRiderMath.LegsOffSpeed);
                }
            }

            // 3. Staying on, stepping off, stepping on [elev §7.3.1 step 4].
            if (!seated)
            {
                Membership();
            }

            Transform current = seated ? frame : _platform;
            Remember(current);
            _early = _before;
        }

        private void LocalLate()
        {
            if (_root == null)
            {
                return;
            }

            if (_carriedFrame == Time.frameCount)
            {
                _carryFrames++;

                if (_travel.magnitude > 2f * EtsRiderMath.MaxOwnSpeed * _carryDt)
                {
                    _carryChecked++;
                }

                // Only where a drag-back can be told from the player's own step (above 52 km/h at any frame rate).
                if (EtsRiderMath.CarryLost(_carriedTo, _root.position, _travel, EtsRiderMath.MaxOwnSpeed * _carryDt))
                {
                    _carryLost++;
                }
            }

            Transform frame = Seated ? _seat.Truck.transform : _platform;

            if (frame != null && frame == _beforeOf && !_frameMovedReported && (frame.position - _early.position).sqrMagnitude > 1e-6f)
            {
                // [elev R6]: the rider pass assumes every vehicle is where this frame draws it by Update. If not,
                // the pass must move to LateUpdate for that kind of vehicle.
                _frameMovedReported = true;
                Debug.Log("[SomniumEuroTruck] Riders: platform '" + frame.name + "' moved " + (Vector3.Distance(frame.position, _early.position) * 100f).ToString("0.0") +
                          " cm after the rider pass (Update -> LateUpdate); the carry and the record are a frame late for it.");
            }

            // The pose the next EarlyPass carries from and measures against: the last one this frame drew.
            Remember(frame);
        }

        private void Remember(Transform frame)
        {
            _beforeOf = frame;

            if (frame != null)
            {
                _before = new Pose(frame.position, frame.rotation);
            }
        }

        private Vector3 ProbePoint()
        {
            // A headset player's body is where the head is; the root lags it in room-scale (the XR controller
            // follows the head). Head x/z at the root's height ([elev §7.3.1 step 4]).
            Transform head = EtsVr.Present ? _local?.References?.Body?.Head : null;
            Vector3 root = _root.position;
            return head != null ? new Vector3(head.position.x, root.y, head.position.z) : root;
        }

        private void Membership()
        {
            bool airborne = _capsule != null && !_capsule.isGrounded;

            if (airborne)
            {
                if (_airborneSince < 0f)
                {
                    _airborneSince = Time.time;
                }
            }
            else
            {
                _airborneSince = -1f;
            }

            // ReferenceEquals, not Unity's ==: a vehicle destroyed under the walker (a car despawned, a truck
            // gone) compares equal to null but must still be left, so the legs come back.
            if (!ReferenceEquals(_platform, null))
            {
                if (_platform == null || _platformBox == null || !_platformBox.enabled)
                {
                    LeavePlatform("it is gone");
                    return;
                }

                Vector3 local = _platformBox.transform.InverseTransformPoint(ProbePoint());
                bool inside = EtsRiderMath.InsideBox(local, _platformBox.center, _platformBox.size, EtsRiderMath.SideMargin, EtsRiderMath.UpMargin);

                if (!EtsRiderMath.Stays(inside, airborne, airborne ? Time.time - _airborneSince : 0f))
                {
                    LeavePlatform(airborne ? "jumped off" : "walked off");
                }

                return;
            }

            Vector3 probe = ProbePoint();
            Vector3 from = new Vector3(probe.x, _root.position.y + EnterRayUp, probe.z);

            // Every vehicle here - the trucks, own or puppet (EtsTruckController), the trailers (EtsTrailer.cs:156-169)
            // and the AI cars (EtsTrafficModels.cs:365-372) - has one BoxCollider on the Vehicle layer on the same object
            // as its Rigidbody, and that object is the frame EtsNet draws it by (KeyOf).
            if (Physics.Raycast(from, Vector3.down, out RaycastHit hit, EnterRayLength, EtsLayers.PlatformMask, QueryTriggerInteraction.Ignore))
            {
                BoxCollider box = hit.collider as BoxCollider;
                Rigidbody body = hit.rigidbody;

                if (box != null && body != null && box.transform == body.transform)
                {
                    _platform = body.transform;
                    _platformBox = box;
                    _airborneSince = -1f;
                    bool named = KeyOf(_platform, out EtsRider key);
                    Debug.Log("[SomniumEuroTruck] Riders: riding '" + _platform.name + "' (standing" + (CarryYawInVr && EtsVr.Present ? ", turning with it" : "") +
                              (named ? "; " + key.Kind + " " + key.Index + " serial " + key.Serial + " on the network" : "; no network key, riding it locally") + ").");
                }
            }
        }

        private void LeavePlatform(string why)
        {
            if (!ReferenceEquals(_platform, null) && why != null)
            {
                Debug.Log("[SomniumEuroTruck] Riders: left '" + (_platform != null ? _platform.name : "a vehicle") + "': " + why + ".");
            }

            _platform = null;
            _platformBox = null;
            _airborneSince = -1f;
            SetLocalLegs(false);
        }

        /// <summary>
        /// A walker carried faster than LegsOffSpeed gets no IK stepping (the substitute for the client's
        /// VRIKAddPlatformMotion, [elev §3]) and no falling animation: the character controller on a moving deck can lose
        /// ground contact for a frame (isGrounded flips, checker probe 1850 X2), and the client would start its fall
        /// clip, which the network then shows everyone. Both come back when the carry slows or the walker leaves.
        /// </summary>
        private void SetLocalLegs(bool off)
        {
            if (_legsOff == off)
            {
                return;
            }

            _legsOff = off;

            // A seated player's legs and falling animation are the seat's (EtsTruckSeat.TrySeat turns both off for the
            // whole ride and Release turns them back on).
            if (!off && Seated)
            {
                return;
            }

            ISomniumPlayerFeatures features = _local?.Features;
            features?.Avatar?.SetVRIKLegMovementDisabled(off);
            features?.Animation?.SetFallingAnimationDisabled(off);
        }

        // ---------------------------------------------------------------- publishing

        private void Publish(bool seated, Pose frame)
        {
            EtsNet net = EtsNet.Instance;

            if (net == null || Held)
            {
                return;
            }

            Transform frameTransform = seated ? _seat.Truck.transform : _platform;

            if (!KeyOf(frameTransform, out EtsRider rider))
            {
                if (_unidentifiedReported != frameTransform)
                {
                    _unidentifiedReported = frameTransform;
                    Debug.Log("[SomniumEuroTruck] Riders: '" + (frameTransform != null ? frameTransform.name : "?") + "' is not drawn by EtsNet under any vehicle key here (MySlot " + net.MySlot +
                              "); " + (seated ? "seated in it" : "riding it") + " locally, others draw this player uncorrected.");
                }

                Unpublish();
                return;
            }

            rider.Seat = seated ? _seat.SeatIndex : Standing;
            EtsRiderMath.Measure(frame, _root.position, _root.rotation, out rider.Root, out rider.Yaw);
            rider.Xr = EtsVr.Present;
            rider.Airborne = !seated && _capsule != null && !_capsule.isGrounded;
            rider.YawCarried = !seated && rider.Xr && CarryYawInVr;

            bool newTarget = !_published || rider.Kind != _last.Kind || rider.Index != _last.Index || rider.Serial != _last.Serial || rider.Seat != _last.Seat;

            if (newTarget)
            {
                // Receivers snap to a new target rather than slide to it (EtsNetTimeline, probe 1842 T4).
                _epoch = (_epoch + 1) & 0xFF;
            }

            rider.Epoch = _epoch;

            // The flags change nothing receivers snap on (EtsRiderTrack's key), so they wait for the interval like a
            // move: isGrounded can flip every frame on a moving deck (checker probe 1850 X2).
            bool flags = _published && (rider.Airborne != _last.Airborne || rider.Xr != _last.Xr || rider.YawCarried != _last.YawCarried);

            double now = Time.unscaledTimeAsDouble;

            if (!EtsRiderMath.WriteDue(ref _gate, rider.Root, rider.Yaw, newTarget, flags, now, 1f / Mathf.Max(1f, MaxWriteHz)))
            {
                // A rider at rest writes nothing (a seated passenger costs no bandwidth, [net §2.10]) - except this
                // restatement every KeepAliveEvery: the slot is EtsNet's, and a record it lost (a re-claimed slot, a
                // reset block) would otherwise stay lost for as long as the passenger sits still. The same values
                // again: receivers see a sample with zero velocity, and the ints that change are the stamp's.
                if (now < _lastWriteAt + KeepAliveEvery)
                {
                    return;
                }
            }

            net.SetLocalRider(in rider);
            _last = rider;
            _published = true;
            _lastWriteAt = now;
        }

        /// <summary>A published record at rest is restated this often, seconds (Publish).</summary>
        private const double KeepAliveEvery = 3.0;

        private double _lastWriteAt;

        private void Unpublish()
        {
            if (!_published || Held)
            {
                return;
            }

            _published = false;
            _gate = default;
            EtsNet.Instance?.ClearLocalRider();
        }

        /// <summary>
        /// The rider record's key (Kind, Index, Serial) for a vehicle frame the local player rides: true only when
        /// EtsNet.TryGetVehicleFrame answers that key with exactly this transform, which is what every other client
        /// looks the key up with - so a record never names a vehicle the receivers would draw somewhere else. The key
        /// is kept per frame transform and re-checked with one TryGetVehicleFrame call each time; a truck respawned under
        /// the same transform (a new serial) fails the check and is named again. While no key is found the search is
        /// repeated once a second (the slot may not be claimed yet, EtsNet.MySlot -1).
        /// </summary>
        private bool KeyOf(Transform frame, out EtsRider rider)
        {
            rider = default;
            EtsNet net = EtsNet.Instance;

            if (net == null || frame == null)
            {
                return false;
            }

            if (frame == _keyFrame)
            {
                if (_keyKnown && net.TryGetVehicleFrame(_keyKind, _keyIndex, _keySerial, out Transform drawn) && drawn == frame)
                {
                    rider.Kind = _keyKind;
                    rider.Index = _keyIndex;
                    rider.Serial = _keySerial;
                    return true;
                }

                if (!_keyKnown && Time.unscaledTime < _keyNextTry)
                {
                    return false;
                }
            }

            bool wasKnown = frame == _keyFrame && _keyKnown;
            _keyFrame = frame;
            _keyNextTry = Time.unscaledTime + KeyRetryEvery;
            _keyKnown = Resolve(net, frame, out _keyKind, out _keyIndex, out _keySerial);

            if (_keyKnown)
            {
                rider.Kind = _keyKind;
                rider.Index = _keyIndex;
                rider.Serial = _keySerial;

                if (_unidentifiedReported == frame)
                {
                    _unidentifiedReported = null;
                }
            }
            else if (wasKnown)
            {
                Debug.Log("[SomniumEuroTruck] Riders: '" + frame.name + "' is no longer drawn by EtsNet under its key; the record is withdrawn until it is named again.");
            }

            return _keyKnown;
        }

        /// <summary>Finds the key EtsNet draws this frame under (KeyOf). Called on a new frame, or once a second while none is found.</summary>
        private bool Resolve(EtsNet net, Transform frame, out EtsTargetKind kind, out int index, out int serial)
        {
            kind = EtsTargetKind.None;
            index = -1;
            serial = 0;

            // A truck, own or another player's puppet: the rig, controller and Rigidbody share the root
            // (EtsTruckFactory.cs:541, :932; EtsTruckController.cs:412).
            EtsTruckRig rig = frame.GetComponent<EtsTruckRig>();

            if (rig != null)
            {
                kind = EtsTargetKind.Tractor;
                index = SlotOf(net, rig);
                return index >= 0 && SerialOf(net, kind, index, frame, out serial);
            }

            EtsTrailer trailer = frame.GetComponent<EtsTrailer>();

            if (trailer != null)
            {
                kind = EtsTargetKind.Trailer;
                index = TrailerSlot(net, trailer);
                return index >= 0 && SerialOf(net, kind, index, frame, out serial);
            }

            if (_traffic == null)
            {
                _traffic = EtsTraffic.Instance != null ? EtsTraffic.Instance : GetComponent<EtsTraffic>();
            }

            Rigidbody body = frame.GetComponent<Rigidbody>();

            if (_traffic != null && body != null && _traffic.TryIdentifyCar(body, out int car, out int carSerial) &&
                net.TryGetVehicleFrame(EtsTargetKind.TrafficCar, car, carSerial, out Transform drawn) && drawn == frame)
            {
                kind = EtsTargetKind.TrafficCar;
                index = car;
                serial = carSerial;
                return true;
            }

            return false;
        }

        /// <summary>A rig's owner slot: the local player's claimed slot for the own truck, the owner's for a puppet.</summary>
        private static int SlotOf(EtsNet net, EtsTruckRig rig)
        {
            return rig.IsPuppet ? rig.OwnerSlot : net.MySlot;
        }

        /// <summary>
        /// The slot of the truck a trailer belongs to: the own truck's (its rig or controller holds it), else the
        /// vehicle view EtsNet lists it in (EtsNet.AllVehicles: own + puppets), else the trailer's own truck.
        /// </summary>
        private static int TrailerSlot(EtsNet net, EtsTrailer trailer)
        {
            EtsTruckRig own = EtsLocalTruck.Own;

            if (own != null && (own.Trailer == trailer || (own.Controller != null && own.Controller.Trailer == trailer)))
            {
                return net.MySlot;
            }

            try
            {
                foreach (EtsVehicleView view in net.AllVehicles)
                {
                    if (view.Trailer != trailer || view.Tractor == null)
                    {
                        continue;
                    }

                    EtsTruckRig rig = view.Tractor.GetComponent<EtsTruckRig>();

                    if (rig != null)
                    {
                        return SlotOf(net, rig);
                    }

                    if (!view.Remote)
                    {
                        return net.MySlot;
                    }
                }
            }
            catch (Exception e)
            {
                // Read on a step-on only; a list changing under the walk is tried again with the next key search.
                Debug.Log("[SomniumEuroTruck] Riders: reading EtsNet.AllVehicles threw " + e.GetType().Name + ": " + e.Message);
            }

            EtsTruckRig owner = trailer.Truck != null ? trailer.Truck.GetComponent<EtsTruckRig>() : null;
            return owner != null ? SlotOf(net, owner) : -1;
        }

        /// <summary>
        /// The serial EtsNet draws this frame under for (kind, index): the one TryGetVehicleFrame answers with exactly this
        /// transform. A truck's or trailer's serial is one byte (PLAN §5.3 int 33), so at most SerialCount calls, once per
        /// new vehicle; each is a lookup in EtsNet's drawn vehicles. 1-255 first and 0 last: a spawned truck's serial is
        /// 1-255, and A's slot rules read a rider serial of 0 as "whatever truck that slot has now" (EtsSlotRules
        /// .SerialMatches), so 0 is taken only when EtsNet answers to nothing else - the exact serial lets a respawned
        /// truck (a new serial) drop its riders, as [net §2.7] "Target vanishes" asks.
        /// </summary>
        private static bool SerialOf(EtsNet net, EtsTargetKind kind, int index, Transform frame, out int serial)
        {
            for (int i = 1; i <= SerialCount; i++)
            {
                int s = i & (SerialCount - 1);

                if (net.TryGetVehicleFrame(kind, index, s, out Transform drawn) && drawn == frame)
                {
                    serial = s;
                    return true;
                }
            }

            serial = 0;
            return false;
        }

        // ---------------------------------------------------------------- remote riders

        private void RemotesEarly()
        {
            EtsNet net = EtsNet.Instance;

            if (net != null)
            {
                IReadOnlyList<EtsRemoteRider> riders = net.RemoteRiders;
                int count = riders != null ? riders.Count : 0;

                for (int i = 0; i < count; i++)
                {
                    EtsRemoteRider rider = riders[i];

                    // A record still asking for a seat is not a seat yet ([net §2.7] step 1): EtsNet writes it with the
                    // seat locator as its offset (the truck's origin when the cab is not built), so placing it would sit the
                    // player down before the owner said yes, or refused. EtsNet.SampleRiders already leaves these out; kept
                    // here so this pass does not depend on it.
                    if (rider.Player == null || rider.Sampled.Kind == EtsTargetKind.None || rider.Sampled.Requesting ||
                        !net.TryGetVehicleFrame(rider.Sampled.Kind, rider.Sampled.Index, rider.Sampled.Serial, out Transform frame) || frame == null)
                    {
                        // Not drawn here (another part of the map, still loading): nothing to put them on.
                        continue;
                    }

                    if (!_remotes.TryGetValue(rider.PlayerId, out Remote remote))
                    {
                        remote = new Remote { PlayerId = rider.PlayerId };
                        _remotes.Add(rider.PlayerId, remote);
                    }

                    if (remote.Player != rider.Player)
                    {
                        // A new player object under the same id (avatar or mode switch): its network body is new too.
                        remote.NtRoot = remote.NtHead = remote.NtLeft = remote.NtRight = null;
                        remote.HaveRaw = false;
                        remote.NextBind = 0f;
                        remote.LegsOff = false;
                        remote.FallingOff = false;
                    }

                    if (remote.Player != rider.Player || remote.Frame != frame)
                    {
                        remote.HaveFramePrev = false;
                    }

                    remote.Player = rider.Player;
                    remote.Xr = rider.Xr;
                    remote.Seen = _pass;
                    remote.Kind = rider.Sampled.Kind;
                    remote.Index = rider.Sampled.Index;
                    remote.Seat = rider.Sampled.Seat;
                    remote.Offset = rider.Sampled.Root;
                    remote.YawLocal = rider.Sampled.Yaw;
                    remote.Frame = frame;

                    var pose = new Pose(frame.position, frame.rotation);
                    float dt = Time.deltaTime;
                    remote.FrameSpeed = remote.HaveFramePrev && dt > 0f ? (pose.position - remote.FramePrev.position).magnitude / dt : 0f;
                    remote.FramePrev = pose;
                    remote.HaveFramePrev = true;
                    remote.FrameEarly = pose;
                    EtsRiderMath.Target(pose, remote.Offset, remote.YawLocal, out remote.Target, out remote.TargetYaw);

                    Bind(remote);

                    if (!remote.HaveRaw)
                    {
                        CacheRaw(remote);
                    }

                    // "network body was D m from its place" [elev §7.6]: the Fusion pose before any correction.
                    remote.WorstBefore = Mathf.Max(remote.WorstBefore, Vector3.Distance(remote.RawRoot.position, remote.Target));
                    Apply(remote);
                    Limbs(remote);
                }
            }

            // Riders who got off, left, or whose vehicle is no longer drawn here: their avatar is the client's again.
            _stale.Clear();

            foreach (KeyValuePair<int, Remote> pair in _remotes)
            {
                if (pair.Value.Seen != _pass)
                {
                    _stale.Add(pair.Key);
                }
            }

            for (int i = 0; i < _stale.Count; i++)
            {
                Restore(_remotes[_stale[i]]);
                _remotes.Remove(_stale[i]);
            }
        }

        private void RemotesLate()
        {
            foreach (KeyValuePair<int, Remote> pair in _remotes)
            {
                Remote remote = pair.Value;

                if (remote.Seen != _pass || remote.Player == null || remote.Frame == null)
                {
                    continue;
                }

                // "after placement E m" (U2): where the body root is now, after the client's own Update, against
                // where EarlyPass put it. Nonzero means the client moved it again after the rider pass.
                Transform bodyRoot = remote.Player.References?.Body?.Root;

                if (bodyRoot != null)
                {
                    remote.WorstAfter = Mathf.Max(remote.WorstAfter, Vector3.Distance(bodyRoot.position, remote.Target));
                }

                if (!_frameMovedReported && (remote.Frame.position - remote.FrameEarly.position).sqrMagnitude > 1e-6f)
                {
                    _frameMovedReported = true;
                    Debug.Log("[SomniumEuroTruck] Riders: platform '" + remote.Frame.name + "' moved " + (Vector3.Distance(remote.Frame.position, remote.FrameEarly.position) * 100f).ToString("0.0") +
                              " cm after the rider pass (Update -> LateUpdate); its riders are placed a frame late in Update.");
                }

                // This frame's target on the vehicle as it stands now, and the poses Fusion just rendered.
                var pose = new Pose(remote.Frame.position, remote.Frame.rotation);
                EtsRiderMath.Target(pose, remote.Offset, remote.YawLocal, out remote.Target, out remote.TargetYaw);
                CacheRaw(remote);
                Apply(remote);
            }
        }

        /// <summary>
        /// Finds this player's Fusion transforms: the NetworkTransforms whose state authority is the player, named
        /// Root/Head/LeftHand/RightHand under "Body" (Player.XR.Part.Network and Player.NonXR.Part.Network,
        /// [elev §3]). Once when a rider appears, then once a second while not found.
        /// </summary>
        private void Bind(Remote remote)
        {
            if (remote.NtRoot != null || Time.unscaledTime < remote.NextBind)
            {
                return;
            }

            remote.NextBind = Time.unscaledTime + BindEvery;
            NetworkRunner runner = SceneNetworking.NetworkRunnerRef;

            if (runner == null)
            {
                return;
            }

            remote.NtHead = remote.NtLeft = remote.NtRight = null;

            try
            {
                // Any Fusion behaviour on those objects will do (the NetworkTransform and the client's
                // PlayerTransformNetworkObject share each one): only its transform and state authority are read.
                // Not runner.GetAllBehaviours<NetworkTransform>: the Somnium server compiles worlds against an
                // older Fusion than the local copies (the upload of 2026-09-23 failed on GetMasterClient), and
                // neither that call nor the NetworkTransform type is used by any world that has passed it. A
                // scene search, once a second at most - shared by every unbound rider - is the proven route.
                if (_found == null || Time.unscaledTime >= _nextSearch)
                {
                    _nextSearch = Time.unscaledTime + BindEvery;
                    _found = FindObjectsByType<NetworkBehaviour>(FindObjectsSortMode.None);
                }

                NetworkBehaviour[] behaviours = _found;

                for (int i = 0; i < behaviours.Length; i++)
                {
                    NetworkBehaviour nt = behaviours[i];

                    if (nt == null || nt.Object == null || nt.Object.StateAuthority.PlayerId != remote.PlayerId)
                    {
                        continue;
                    }

                    Transform t = nt.transform;

                    if (t.parent == null || t.parent.name != "Body")
                    {
                        continue;
                    }

                    switch (t.name)
                    {
                        case "Root": remote.NtRoot = t; break;
                        case "Head": remote.NtHead = t; break;
                        case "LeftHand": remote.NtLeft = t; break;
                        case "RightHand": remote.NtRight = t; break;
                    }
                }
            }
            catch (Exception e)
            {
                Debug.Log("[SomniumEuroTruck] Riders: finding " + EtsNet.NameOf(remote.PlayerId) + "'s network body threw " + e.GetType().Name + ": " + e.Message);
            }

            remote.HaveRaw = false;

            if (!remote.BindReported || remote.NtRoot != null)
            {
                remote.BindReported = true;
                Debug.Log("[SomniumEuroTruck] Riders: " + EtsNet.NameOf(remote.PlayerId) + ": " + (remote.NtRoot != null
                    ? "bound: Root" + (remote.NtHead != null ? ",Head" : "") + (remote.NtLeft != null ? ",LeftHand" : "") + (remote.NtRight != null ? ",RightHand" : "") + (remote.Xr ? " (XR)" : " (desktop)")
                    : "unbound: transform-only fallback (the body root alone is moved; a desktop avatar will lag the truck)") + ".");
            }
        }

        /// <summary>The body's poses as Fusion rendered them (bound), or the Bridge root as the client drove it (unbound).</summary>
        private static void CacheRaw(Remote remote)
        {
            if (remote.NtRoot != null)
            {
                remote.RawRoot = new Pose(remote.NtRoot.position, remote.NtRoot.rotation);
                if (remote.NtHead != null) remote.RawHead = new Pose(remote.NtHead.position, remote.NtHead.rotation);
                if (remote.NtLeft != null) remote.RawLeft = new Pose(remote.NtLeft.position, remote.NtLeft.rotation);
                if (remote.NtRight != null) remote.RawRight = new Pose(remote.NtRight.position, remote.NtRight.rotation);
                remote.HaveRaw = true;
                return;
            }

            Transform root = remote.Player?.References?.Body?.Root;

            if (root != null)
            {
                remote.RawRoot = new Pose(root.position, root.rotation);
                remote.HaveRaw = true;
            }
        }

        /// <summary>
        /// Moves the body rigidly onto the target: the Fusion transforms the receive drivers copy from, and the
        /// Bridge transforms they drive. Root first - the head and hand anchors hang under it in the client's
        /// Simulator prefabs [elev §3] - then head and hands at their own corrected world poses.
        /// </summary>
        private static void Apply(Remote remote)
        {
            if (!remote.HaveRaw)
            {
                return;
            }

            ISomniumPlayerBody body = remote.Player?.References?.Body;
            Vector3 rawRoot = remote.RawRoot.position;
            float rawYaw = EtsRiderMath.YawOf(remote.RawRoot.rotation);
            Pose root = EtsRiderMath.Correct(remote.RawRoot, rawRoot, rawYaw, remote.Target, remote.TargetYaw);

            if (remote.NtRoot != null)
            {
                remote.NtRoot.SetPositionAndRotation(root.position, root.rotation);
            }

            Transform bodyRoot = body?.Root;

            if (bodyRoot != null)
            {
                bodyRoot.SetPositionAndRotation(root.position, root.rotation);
            }

            if (remote.NtRoot == null)
            {
                return;
            }

            Place(remote.NtHead, body?.Head, remote.RawHead, rawRoot, rawYaw, remote);
            Place(remote.NtLeft, body?.LeftHand, remote.RawLeft, rawRoot, rawYaw, remote);
            Place(remote.NtRight, body?.RightHand, remote.RawRight, rawRoot, rawYaw, remote);
        }

        private static void Place(Transform network, Transform driven, Pose raw, Vector3 rawRoot, float rawYaw, Remote remote)
        {
            if (network == null)
            {
                return;
            }

            Pose part = EtsRiderMath.Correct(raw, rawRoot, rawYaw, remote.Target, remote.TargetYaw);
            network.SetPositionAndRotation(part.position, part.rotation);

            if (driven != null)
            {
                driven.SetPositionAndRotation(part.position, part.rotation);
            }
        }

        /// <summary>
        /// A seated rider, or one on a vehicle moving faster than walking, gets no IK leg stepping and no falling
        /// animation - both switches are local to each client for a remote player ([elev §3]). This is the only place
        /// they are set for remote players: EtsNet's old driver's-avatar hack was retired in Phase 3 (A3).
        /// </summary>
        private static void Limbs(Remote remote)
        {
            bool still = remote.Seat != Standing || remote.FrameSpeed > EtsRiderMath.LegsOffSpeed;
            ISomniumPlayerFeatures features = remote.Player?.Features;

            if (still != remote.LegsOff)
            {
                remote.LegsOff = still;
                features?.Avatar?.SetVRIKLegMovementDisabled(still);
            }

            if (!remote.FallingOff)
            {
                remote.FallingOff = true;
                features?.Animation?.SetFallingAnimationDisabled(true);
            }
        }

        private static void Restore(Remote remote)
        {
            try
            {
                ISomniumPlayerFeatures features = remote.Player?.Features;

                if (remote.LegsOff)
                {
                    features?.Avatar?.SetVRIKLegMovementDisabled(false);
                }

                if (remote.FallingOff)
                {
                    features?.Animation?.SetFallingAnimationDisabled(false);
                }
            }
            catch (Exception e)
            {
                // A player leaving the room takes their features with them; nothing is left to restore.
                Debug.Log("[SomniumEuroTruck] Riders: restoring " + remote.PlayerId + "'s avatar threw " + e.GetType().Name + " (they have left).");
            }

            remote.LegsOff = false;
            remote.FallingOff = false;
        }

        // ---------------------------------------------------------------- the log

        private void Report()
        {
            if (Time.unscaledTime < _nextReport)
            {
                return;
            }

            _nextReport = Time.unscaledTime + ReportEvery;

            if (_platform != null)
            {
                Debug.Log("[SomniumEuroTruck] Riders: riding '" + _platform.name + "' (standing); carry lost " + _carryLost + " times in 5 s (" + _carryFrames + " frames carried, " + _carryChecked + " fast enough to tell).");
            }

            _carryLost = 0;
            _carryFrames = 0;
            _carryChecked = 0;

            foreach (KeyValuePair<int, Remote> pair in _remotes)
            {
                Remote remote = pair.Value;
                Debug.Log("[SomniumEuroTruck] Riders: " + EtsNet.NameOf(remote.PlayerId) + " on " + remote.Kind + " " + remote.Index + " (" +
                          (remote.Seat == Standing ? "standing" : "seat " + remote.Seat) + "): network body was up to " + remote.WorstBefore.ToString("0.00") +
                          " m from its place before correction; after placement " + remote.WorstAfter.ToString("0.000") + " m; " +
                          (remote.NtRoot != null ? "bound" + (remote.Xr ? " (XR)" : " (desktop)") : "unbound (transform-only fallback)") + ".");
                remote.WorstBefore = 0f;
                remote.WorstAfter = 0f;
            }
        }
    }
}
