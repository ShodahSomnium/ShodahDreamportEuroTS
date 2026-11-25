using System.Collections;
using SomniumSpace.Bridge;
using SomniumSpace.Bridge.Player;
using SomniumSpace.Bridge.Player.Components;
using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
using UnityEngine;

namespace SomniumSpace.Worlds.SomniumEuroTruck
{
    /// <summary>
    /// The driver's seat, and the ground beside the driver's door.
    ///
    /// The player starts on foot and gets in by clicking the truck (EtsMenu hands the click here).
    /// Seated, the pattern is the Quake world's: take the local player from the Bridge, switch
    /// off its own locomotion, collision and teleport, and write its body position every frame
    /// after everything else has moved. In the editor there is no Bridge player and none of this
    /// does anything. Destroying or disabling the component hands the player back.
    ///
    /// Whether anyone is in the seat is also whether the truck is driven (EtsTruckController
    /// .SetDriven): empty, it is parked, kinematic and solid; occupied, it runs, and its box
    /// ignores the avatar's layers - the Bridge's collision switch does not stop the avatar's
    /// capsule pushing the truck, and in the first client run that push sent it through the road.
    ///
    /// On a monitor the body goes to the driver locator and EtsTruckCamera places the camera.
    /// In a headset the camera is the headset's and cannot be written, so the rig is moved
    /// instead, by the difference between where the eyes are and where the driver's eye is
    /// (Q3Vr.PlaceHead). The head's own movement since the player sat down is kept, so leaning
    /// and looking round the cab still work.
    ///
    /// The seated player stays upright: the body root - the XR rig, or the desktop body - takes the truck's heading
    /// only, never its pitch or roll (SeatRotation). The cab-tilt follow added on 2026-09-23 was removed on 2026-09-25:
    /// the user found the view rolling and pitching with the truck "VERY disorientating" in the headset. The head is
    /// put exactly on the eye point after the turn (PlaceHead). On the way out the rig is still levelled round the
    /// head first (LevelRig), which is harmless on an upright rig.
    ///
    /// Getting out, and being stood beside the truck, go through DoTeleportToPoint, which moves
    /// the character controller and the body together (setting a transform does not; AomBridge).
    /// The ground is solid for the avatar: see EtsLayers for the collision rows.
    ///
    /// Two seats (PLAN.md §7.B, [elev §7.2], owner B). Seat 0 is the driver's, seat 1 the passenger's -
    /// one per truck (Q-B3): the game's cabs have a right-hand "driver*" locator beside the left one
    /// (EtsTruckController.SeatLocalFor/EyeLocalFor, owner C) and no other seat. GetIn(rig, seat) is the
    /// integrated plan's way in, for any rig; GetIn() stays today's, the one scene truck's driver seat,
    /// which the old flow uses unchanged. A passenger (PLAN §3.5, C10, C11):
    ///   - never calls SetDriven - a passenger getting in or out must not wake or park somebody else's
    ///     truck; only the owner drives, and only their own truck (a puppet's seat 0 is refused);
    ///   - has the Vehicle layer added to their own capsule's excludeLayers while seated
    ///     (EtsLayers.SeatedCapsuleExcludes), because the puppet they sit in stays kinematic and solid;
    ///   - is held in the sitting pose exactly as the driver is (KeepSitting), which the client networks
    ///     itself (AvatarNetworkObject.cs:17-28), so everyone sees them sit;
    ///   - gets no keys and no wheel (EtsSteeringGrab needs IsDriver; EtsMenu's keys are E's);
    ///   - may get out only below EtsRiders.PassengerExitMaxKmh (10 km/h, Q-B2), and is stood 1.6 m outside
    ///     the RIGHT door - StandBeside takes the side from the seat's x;
    ///   - is let out where the truck was last drawn if the truck vanishes (owner left, despawned): the
    ///     client's gravity comes back, so it waits for ground under that spot (10 s at most).
    /// All of it is reached only through GetIn(rig, seat), OwnChanged and OccupiedChanged, which nothing
    /// raises while EtsFeatures.NewFlow is false: the old flow's seat index is always 0 and SeatLocal and
    /// DriverEyeLocal are used exactly as before.
    ///
    /// The new flow's truck comes late (PLAN §5.5 steps 7-8, B2). The player arrives at the yard on foot with no
    /// truck at all; one appears only when they confirm a truck in the tablet (EtsNet.SpawnOwnTruck ->
    /// EtsLocalTruck.SetOwn), and it can be replaced or taken away again. So the seat is created once on 'Ets Net'
    /// with no truck, and every truck it knows comes from EtsLocalTruck:
    ///   - on foot it watches Own (OwnChanged): where "get out" stands you, the parking of a truck the factory
    ///     built running (it is Driven until a seat parks it, and its door handle only shows once it is parked),
    ///     the left-behind rule after a job moves it;
    ///   - seated it holds the rig it sat down in and announces it as Occupied (SetOccupied), which is what the
    ///     camera, the wheel, the sound, the menu and the tablet rebind on;
    ///   - with no truck every path is a no-op: GetIn() refuses, StandBeside and the checks in Update return,
    ///     StreamFocus is the player;
    ///   - the truck taken from under a seated player (destroyed, or Occupied moved by anyone but this seat)
    ///     releases the seat and lands the player beside where it was last drawn (PLAN §3.5).
    /// </summary>
    public class EtsTruckSeat : MonoBehaviour
    {
        public EtsTruckController Truck;

        /// <summary>
        /// The hands on the steering wheel (headset only). Driven from the end of LateUpdate here,
        /// because the hands ride on the rig this moves and are only in the cab once it has.
        /// </summary>
        public EtsSteeringGrab Wheel;

        /// <summary>The seat (hip point) in the truck's local frame; the driver locator of the chassis.</summary>
        public Vector3 SeatLocal = new Vector3(-0.79f, 1.48f, 2.38f);

        /// <summary>Whether the player should be in the truck. False at the start: they walk to it and click it.</summary>
        public bool WantSeated;

        /// <summary>How far the head may lean from where it sat down before the lean stops growing, in metres.</summary>
        private const float LeanLimit = 0.45f;

        /// <summary>
        /// How far from the truck a player on foot can be when the truck is set down somewhere new
        /// (a job's depot) before they are brought to it. The stream follows the truck, so a player
        /// left behind at the old yard would soon have no ground under them.
        /// </summary>
        private const float LeftBehind = 25f;

        /// <summary>How far a player on foot can walk from the truck before the streamed world (1200 m round it) runs out.</summary>
        private const float WanderLimit = 1000f;

        /// <summary>
        /// The pose the driver is held in. SittingStraight is the client's chair pose: head at 0.7267 of the
        /// avatar's height (Player.XR.Features / Player.NonXR.Features, _allPosesRefs PoseID 0).
        /// </summary>
        public PoseType SeatPose = PoseType.SittingStraight;

        /// <summary>
        /// Hold the avatar in SeatPose while seated, putting it back the same frame whenever the client drops it.
        /// The client ends a pose on its own cancel action (PosesFeature.CheckQuitInput): left X in a headset -
        /// the button the old flow opens this world's menu with, and the client's own again once the tablet is linked
        /// (EtsTabletLayout.MenuKey) - and, on a monitor, most likely Escape (likewise).
        /// An avatar reload or a scale change ends it too (PosesFeature.OnSetupModelComplete, *PosesFeature.ChangeScale).
        /// </summary>
        public bool ForceSitting = true;

        /// <summary>The client's pose offset above the camera and the head anchor (Player.*.Body prefabs).</summary>
        private const string PoseOffsetName = "HeadCustomPoseOffset";


        private Transform _root;
        private Transform _rig;
        private Transform _head;
        private Transform _poseOffset;
        private Transform _poseOffsetFrom;
        private ISomniumPlayerMotion _motion;
        private ISomniumPlayerAnimation _animation;
        private ISomniumPlayerPoses _poses;
        private ISomniumPlayerAvatar _avatar;
        private bool _poseReported;
        private bool _seated;
        private bool _reported;
        private bool _restKnown;
        private Vector3 _headRest;
        private Coroutine _finding;
        private float _nextCheck;
        private bool _knowPlacement;
        private int _lastPlacements;

        // The integrated plan's seats (GetIn(rig, seat)); all at their defaults on the old flow.
        private int _seatIndex;
        private EtsTruckRig _truckRig;
        private bool _onRig;
        private int _rigOwner = -1;
        private CharacterController _capsule;
        private bool _capsuleExcluded;
        private Pose _lastDrawn;
        private Vector3 _lastSeatLocal;
        private bool _haveLastDrawn;
        private Coroutine _landing;
        private bool _subscribed;

        /// <summary>
        /// Set by OnOccupiedChanged when EtsLocalTruck.Occupied moved away from the rig this seat holds, and acted on in the
        /// next Update: releasing raises OccupiedChanged again, and a raise nested inside another would reach the other
        /// listeners (camera, wheel, sound, menu) with the older value last.
        /// </summary>
        private bool _occupiedTakenAway;

        /// <summary>A passenger may leave a truck moving slower than this when there is no EtsRiders to ask (Q-B2).</summary>
        private const float DefaultPassengerExitKmh = 10f;

        /// <summary>How long a passenger whose truck vanished waits for ground under the spot before giving up (PLAN §3.5).</summary>
        private const float LandingWait = 10f;

        public bool Seated => _seated;

        /// <summary>The local player's body root, or null when there is no client (the editor).</summary>
        private static Transform PlayerRoot => SomniumBridge.PlayersContainer?.LocalPlayer?.References?.Body?.Root;

        /// <summary>Whether there is a Somnium player at all (false in the editor).</summary>
        public bool HasPlayer => PlayerRoot != null;

        /// <summary>
        /// What the map streams round (EtsMapViewer, EtsGroundFill): the truck while this player
        /// sits in it, or when there is no Somnium player (the editor); the player while they walk.
        /// In a room another client may drive the truck away, and a walker must keep the ground
        /// under their own feet rather than the truck's.
        /// </summary>
        public Transform StreamFocus
        {
            get
            {
                Transform player = PlayerRoot;

                if (EtsFeatures.NewFlow)
                {
                    return NewFlowStreamFocus(player);
                }

                if (!_seated && player != null)
                {
                    // Except while a job is moving the truck to its depot: that yard must build round
                    // the truck, or StartJob waits for ground that never comes and the job fails. Once
                    // it is set down the left-behind check (Update) stands the walker beside it, and the
                    // stream goes back to following them.
                    if (_jobs == null)
                    {
                        _jobs = FindFirstObjectByType<EtsJobs>();
                    }

                    if (Truck != null && !Truck.Puppet && _jobs != null && _jobs.Relocating)
                    {
                        return Truck.transform;
                    }

                    return player;
                }

                return Truck != null ? Truck.transform : player;
            }
        }

        private EtsJobs _jobs;

        /// <summary>
        /// The integrated plan's stream focus (PLAN §6 "StreamFocus follows EtsLocalTruck.Occupied"): the truck
        /// the player sits in, own or another player's; the own truck while a job moves it to its depot (the
        /// old rule, kept for the new flow); the player on foot; the watched truck in the editor.
        /// </summary>
        private Transform NewFlowStreamFocus(Transform player)
        {
            EtsTruckRig occupied = EtsLocalTruck.Occupied;

            if (_seated && occupied != null)
            {
                return occupied.transform;
            }

            if (player == null)
            {
                return Truck != null ? Truck.transform : null;
            }

            EtsTruckRig own = EtsLocalTruck.Own;

            if (own != null)
            {
                if (_jobs == null)
                {
                    _jobs = EtsJobs.Instance != null ? EtsJobs.Instance : FindFirstObjectByType<EtsJobs>();
                }

                if (_jobs != null && _jobs.Relocating)
                {
                    return own.transform;
                }
            }

            return player;
        }

        private void OnEnable()
        {
            if (EtsFeatures.NewFlow && !_subscribed)
            {
                _subscribed = true;
                EtsLocalTruck.OwnChanged += OnOwnChanged;
                EtsLocalTruck.OccupiedChanged += OnOccupiedChanged;
            }

            if (WantSeated)
            {
                _finding = StartCoroutine(FindPlayer());
            }
        }

        private IEnumerator FindPlayer()
        {
            float deadline = Time.time + 15f;

            while (WantSeated && !_seated && Time.time < deadline)
            {
                TrySeat();

                // Seated, or refused (the truck gone or no longer the player's): done now, not half a second later, so
                // the lines below take the current own truck up at once.
                if (!WantSeated || _seated)
                {
                    break;
                }

                yield return new WaitForSeconds(0.5f);
            }

            _finding = null;

            if (WantSeated && !_seated && !_reported)
            {
                _reported = true;
                Debug.Log("[SomniumEuroTruck] No Somnium player to seat (editor run, or the Bridge gave no body); the camera alone follows the truck.");
            }

            // New flow, given up (or let go by GetOut meanwhile): the wish ends here - left set, re-enabling the seat
            // would sit the player down in whatever the seat is bound to by then. OnOwnChanged does not rebind while a
            // GetIn waits, so a truck spawned meanwhile is taken up now; without this the seat stayed on the truck it
            // was asked for until the next get in and out (Phase 1 check B, risk 3).
            if (EtsFeatures.NewFlow && !_seated)
            {
                WantSeated = false;

                if (_truckRig != EtsLocalTruck.Own)
                {
                    Bind(EtsLocalTruck.Own, 0);
                    _nextCheck = 0f;
                }
            }
        }

        private void TrySeat()
        {
            // New flow: the truck asked for can be gone by the time the Bridge has a body (FindPlayer retries for 15 s),
            // or there was none (GetIn with no own truck). Seating then would switch off the player's gravity and
            // locomotion with nothing to carry them - stuck where they stand.
            if (EtsFeatures.NewFlow && (Truck == null || (_onRig && _truckRig == null)))
            {
                WantSeated = false;
                Debug.Log("[SomniumEuroTruck] Seat: the truck is gone; nobody was seated.");
                return;
            }

            // Likewise the driver's seat of a truck that stopped being this player's own while the body was awaited
            // (replaced in the tablet meanwhile): only the owner drives (C10), and A is about to destroy it.
            if (EtsFeatures.NewFlow && _onRig && _seatIndex == 0 && _truckRig != EtsLocalTruck.Own)
            {
                WantSeated = false;
                Debug.Log("[SomniumEuroTruck] Seat: that truck is no longer this player's own; nobody was seated.");
                return;
            }

            var player = SomniumBridge.PlayersContainer?.LocalPlayer;
            _root = player?.References?.Body?.Root;

            if (_root == null)
            {
                return;
            }

            _motion = player?.Features?.Motion;
            _animation = player?.Features?.Animation;
            _poses = player?.Features?.Poses;
            _avatar = player?.Features?.Avatar;
            _head = player?.References?.Body?.Head;
            _leftHand = player?.References?.Body?.LeftHand;
            _rightHand = player?.References?.Body?.RightHand;
            _poseOffset = null;
            _poseOffsetFrom = null;
            _leftHandOffset = null;
            _rightHandOffset = null;
            _poseReported = false;

            // The truck first, so its box is already ignoring the avatar when the avatar arrives in it.
            // The driver's seat only: a passenger must not wake somebody else's truck (PLAN C10).
            if (Truck != null && _seatIndex == 0)
            {
                Truck.SetDriven(true);
            }

            // A passenger sits in a kinematic, solid puppet: their own capsule stops seeing vehicles instead.
            if (_seatIndex != 0)
            {
                ExcludeVehicles(true);
            }

            if (_motion != null)
            {
                _motion.SetCollisionDisableState(true);
                _motion.SetFlyModeDisableState(true);
                _motion.SetTeleportationDisableState(true);
                _motion.SetMovementSpeed(0f, 0f, 0f);
                _motion.SetGravity(0f, 0f);
                _motion.SetJump(0f, 0f);
            }

            _animation?.SetFallingAnimationDisabled(true);

            // Headset only (XRAvatarFeature): no IK stepping while the rig is carried along at road speed.
            // Sitting, the legs are the pose animation's anyway; this covers a pose the client refuses.
            _avatar?.SetVRIKLegMovementDisabled(true);

            _seated = true;
            _restKnown = false;
            _rig = null;
            _occupiedTakenAway = false;

            if (_seatIndex == 0)
            {
                Debug.Log("[SomniumEuroTruck] Player seated in the truck.");
            }
            else
            {
                Debug.Log("[SomniumEuroTruck] Player seated as the passenger in " + EtsNet.NameOf(_rigOwner) + "'s truck" + (_capsuleExcluded ? "; the capsule ignores vehicles while seated." : "; no capsule found to keep off the cab."));
            }

            Debug.Log("[SomniumEuroTruck] Seat: upright, the truck's heading only (no cab tilt).");

            if (_onRig)
            {
                EtsLocalTruck.SetOccupied(_truckRig);
            }

            // The cab door shutting behind the player (demo fix 2026-09-23); the engine start follows from the controller.
            EtsTruckAudio.For(Truck)?.PlayDoor(false);
        }

        /// <summary>
        /// Takes the player into the driver's seat. New flow: refused while there is no truck - the player arrives on foot
        /// and a truck exists only once they choose one in the tablet (ETS 2 - Company - Truck Preference); seated with
        /// none, their locomotion and gravity would be off with nothing to carry them.
        /// </summary>
        public void GetIn()
        {
            if (EtsFeatures.NewFlow && Truck == null)
            {
                Debug.Log("[SomniumEuroTruck] Seat: no truck to get into yet (choose one in the tablet: ETS 2 - Company - Truck Preference).");
                return;
            }

            WantSeated = true;

            if (_seated)
            {
                return;
            }

            TrySeat();

            if (!_seated && _finding == null && isActiveAndEnabled)
            {
                _finding = StartCoroutine(FindPlayer());
            }
        }

        // ---------------------------------------------------------------- the integrated plan's seats (PLAN.md §7.B)

        /// <summary>0 driver, 1 passenger. Always 0 on the old flow, where only GetIn() is used.</summary>
        public int SeatIndex => _seatIndex;

        /// <summary>Seated in the driver's seat: the one who steers, has the keys and the wheel, and whose truck is driven.</summary>
        public bool IsDriver => _seated && _seatIndex == 0;

        /// <summary>The truck sat in, or null on foot and on the old flow (there Truck is the scene's one truck and has no rig).</summary>
        public EtsTruckRig Rig => _seated && _onRig ? _truckRig : null;

        /// <summary>
        /// The hip point in the truck's frame for this seat: the rig controller's (C's locator rule, [elev §5]) once a
        /// rig is bound; else SeatLocal, the driver locator EtsModelViewer gives the old flow's truck.
        /// </summary>
        private Vector3 SeatLocalNow => _onRig && Truck != null ? Truck.SeatLocalFor(_seatIndex) : SeatLocal;

        /// <summary>The eye in the truck's frame for this seat (seat 0 is DriverEyeLocal on either flow).</summary>
        private Vector3 EyeLocalNow => _onRig ? Truck.EyeLocalFor(_seatIndex) : Truck.DriverEyeLocal;

        /// <summary>
        /// Into a seat of a rig: 0 = the driver's (own truck only), 1 = the passenger's (PLAN §5.5; EtsMenu.EnterTruck
        /// calls it once a door click or a granted RequestSeat says so). Any other index is the one passenger seat
        /// (Q-B3). Refused, with a log line, for a puppet's driver seat (only the owner drives, C10) and while already
        /// seated - changing seats is getting out and in again ([elev §7.5] step 4).
        /// </summary>
        public void GetIn(EtsTruckRig rig, int seat)
        {
            if (rig == null || rig.Controller == null)
            {
                Debug.Log("[SomniumEuroTruck] Seat: no truck to get into.");
                return;
            }

            // C's rule (EtsTruckController.SeatLocalFor/EyeLocalFor): 0 is the driver's, any other index the passenger's.
            int index = seat == 0 ? 0 : 1;

            if (index == 0 && rig.IsPuppet)
            {
                Debug.Log("[SomniumEuroTruck] Seat: only " + EtsNet.NameOf(rig.OwnerPlayerId) + " drives their truck; the passenger seat is the way in (PLAN C10).");
                return;
            }

            if (_seated)
            {
                if (_onRig && rig == _truckRig && index == _seatIndex)
                {
                    return;
                }

                Debug.Log("[SomniumEuroTruck] Seat: already seated; get out first to change seats.");
                return;
            }

            StopLanding();
            Bind(rig, index);
            GetIn();
        }

        /// <summary>
        /// Watch a rig: the seat's truck, the wheel's truck, and which seat. The seat index stays until the next
        /// GetIn(rig, seat) or getting out, so SeatLocalNow and StandBeside use the seat just left.
        ///
        /// The placement count (the left-behind check in Update) is forgotten only for a different rig. Getting into
        /// the truck already watched on foot rebinds the same rig (GetIn(rig, 0)); forgetting it there made the first
        /// on-foot check after getting out take a placement from the half second before it (Update checks every 0.5 s)
        /// as the first one ever seen, and a walker whose truck was set down elsewhere in that window was left behind
        /// (checker probe 2029 case b: LEFT BEHIND before this, brought beside the truck after). The old flow never
        /// rebinds, so it always kept the count.
        /// </summary>
        private void Bind(EtsTruckRig rig, int index)
        {
            if (rig != _truckRig || !_onRig)
            {
                _knowPlacement = false;
            }

            _truckRig = rig;
            _onRig = rig != null;
            _seatIndex = index;
            _rigOwner = rig != null ? rig.OwnerPlayerId : -1;
            _haveLastDrawn = false;
            Truck = rig != null ? rig.Controller : null;

            if (Wheel != null)
            {
                Wheel.LetGo();
                Wheel.Truck = Truck;
                Wheel.Seat = this;
            }
        }

        /// <summary>
        /// The own truck changed (A spawned or despawned it). On foot, the seat watches the own truck - the
        /// left-behind and out-of-the-world checks in Update, and where "get out" stands you are about it. Seated,
        /// nothing changes until the player is out again (see GetOut).
        /// </summary>
        private void OnOwnChanged(EtsTruckRig own)
        {
            // Driving the truck that has just stopped being this player's own: A replaces or despawns it (a truck
            // confirmed in the tablet, DespawnOwnTruck) and destroys it right after. Seat 0 is only ever the own truck
            // (C10), so the driver is let out now, while that truck still stands there to be let out beside - waiting
            // for the next Update would find it destroyed, and a destroyed rig can no longer be cleared from
            // EtsLocalTruck.Occupied (SetOccupied(null) compares with Unity's ==, which calls a destroyed rig null, and
            // returns without raising OccupiedChanged: the camera, wheel, sound and menu would never hear of it).
            // Raising OccupiedChanged from inside OwnChanged is safe: they are different events, and every listener
            // reads the current statics (see _occupiedTakenAway for the nesting that is not).
            if (_seated && _onRig && _seatIndex == 0 && _truckRig != null && _truckRig != own)
            {
                LetOutOfFormerOwn(own);
                return;
            }

            // A truck spawned while the player rides in somebody else's (Phase 3): nobody is in its driver's seat, so it
            // is parked at once. The factory builds it driven, and on foot Update would park it; seated elsewhere nothing
            // else would, and it would stand there dynamic with its keys on (EtsMenu only switches off the keys of the
            // truck the player sits in).
            if (_seated && own != null && own != _truckRig && own.Controller != null && own.Controller.Driven && HasPlayer)
            {
                own.Controller.SetDriven(false);
            }

            // Not while a GetIn is still waiting for the Bridge's body (FindPlayer, 15 s at most): rebinding then
            // would sit the player in their own driver's seat instead of the seat they asked for. FindPlayer takes the
            // new truck up when it ends.
            if (!_seated && _finding == null)
            {
                Bind(own, 0);

                // Checked on the next Update rather than up to 0.5 s later: the factory's truck is Driven - dynamic, its
                // box ignoring the avatar - until a seat with a player on foot parks it (Update), and its door handle
                // only shows once it is parked (EtsTruckDoor.UsableOnRig, PLAN C11).
                _nextCheck = 0f;
            }
        }

        /// <summary>
        /// The driver of a truck that is no longer the player's own (OnOwnChanged): out of the seat and onto the ground
        /// beside the driver's door of that truck, which still exists in this call, then watching the new own truck (or
        /// none). The same steps as GetOut, without its passenger speed rule (this is the driver's seat).
        /// </summary>
        private void LetOutOfFormerOwn(EtsTruckRig own)
        {
            Debug.Log("[SomniumEuroTruck] Riders: seat released - truck of this player " + (own == null ? "despawned" : "replaced by another") + ".");
            WantSeated = false;
            StopLanding();
            Release();
            bool moved = _root != null && StandBeside();
            Debug.Log("[SomniumEuroTruck] Player got out of the truck" + (moved ? "." : ", but the Bridge would not move them."));
            Bind(own, 0);
            _nextCheck = 0f;
        }

        /// <summary>
        /// EtsLocalTruck.Occupied changed. This seat is its only writer (PLAN §7.C: SetOccupied is "B, on seat and
        /// release"), and it raises it only as it sits down (TrySeat) or after it has let go (Release), when this finds
        /// nothing to do. Any other change while seated - another component releasing the rig, or pointing Occupied at a
        /// different one - means this seat no longer holds the truck it sits in: the player is let out beside where it
        /// was last drawn, in the next Update (see _occupiedTakenAway for why not here).
        /// </summary>
        private void OnOccupiedChanged(EtsTruckRig occupied)
        {
            if (_seated && _onRig && occupied != _truckRig)
            {
                _occupiedTakenAway = true;
            }
        }

        /// <summary>
        /// Whether getting out is allowed now, and if not, why (for the tablet's toast and the menu). A passenger may
        /// not get out of a truck moving faster than EtsRiders.PassengerExitMaxKmh (Q-B2, settled 10 km/h): they would
        /// be left on the road beside it. The driver may always get out; the truck is parked where it stands.
        /// </summary>
        public bool CanGetOut(out string reason)
        {
            reason = null;

            if (!_seated)
            {
                reason = "You are not in a truck."; // not in the locale
                return false;
            }

            if (_seatIndex != 0 && Truck != null)
            {
                EtsRiders riders = EtsRiders.Instance;
                float limit = riders != null ? riders.PassengerExitMaxKmh : DefaultPassengerExitKmh;

                if (Truck.SpeedKmh > limit)
                {
                    reason = "Wait until the truck slows down to get out."; // not in the locale
                    return false;
                }
            }

            return true;
        }

        /// <summary>Gets or adds the local player's one seat on the bootstrap object (PLAN §3.2; EtsNet.Awake calls it only with EtsFeatures.NewFlow).</summary>
        public static EtsTruckSeat Ensure(GameObject host)
        {
            if (host == null)
            {
                return null;
            }

            EtsTruckSeat seat = host.GetComponent<EtsTruckSeat>();

            if (seat == null)
            {
                seat = host.AddComponent<EtsTruckSeat>();

                // The one truck exists before its seat on the old flow; on the new one the own truck may too.
                if (EtsFeatures.NewFlow && EtsLocalTruck.Own != null)
                {
                    seat.Bind(EtsLocalTruck.Own, 0);
                }
            }

            // The marker over the player's own truck (2026-09-23) lives beside the seat and watches EtsLocalTruck itself.
            EtsTruckMarker.Ensure(host);
            return seat;
        }

        /// <summary>
        /// Lets the player out onto the ground beside their door (the driver's on the left, the passenger's on the
        /// right). Returns false when there is no Somnium player to let out (the editor), and for a passenger while
        /// CanGetOut says no.
        /// </summary>
        public bool GetOut()
        {
            if (_seated && _seatIndex != 0 && !CanGetOut(out string why))
            {
                Debug.Log("[SomniumEuroTruck] Seat: getting out refused at " + Mathf.RoundToInt(Truck.SpeedKmh) + " km/h: " + why);
                return false;
            }

            WantSeated = false;

            if (!_seated || _root == null || Truck == null)
            {
                return false;
            }

            bool passenger = _seatIndex != 0;

            // The cab door opening as the player climbs out (demo fix 2026-09-23; EtsTruckAudio.PlayDoor, safe with no sound).
            EtsTruckAudio.For(Truck)?.PlayDoor(true);
            Release();
            bool moved = StandBeside();
            Debug.Log("[SomniumEuroTruck] Player got out of the " + (passenger ? "passenger seat" : "truck") + (moved ? "." : ", but the Bridge would not move them."));

            // Back on foot, the seat watches the own truck again (the passenger's ride was somebody else's).
            if (_onRig && (passenger || _truckRig != EtsLocalTruck.Own))
            {
                Bind(EtsLocalTruck.Own, 0);
            }

            return true;
        }

        /// <summary>
        /// Puts a player on foot on the ground 1.6 m outside their door, facing the truck. The driver
        /// sits on the truck's left (the locator's x is negative), the passenger on its right, so the
        /// seat's own x gives the side.
        /// </summary>
        public bool StandBeside()
        {
            if (Truck == null || _seated)
            {
                return false;
            }

            Transform truck = Truck.transform;
            Vector3 seatLocal = SeatLocalNow;
            float side = seatLocal.x < 0f ? -1f : 1f;
            Vector3 door = truck.TransformPoint(new Vector3(seatLocal.x + side * 1.6f, seatLocal.y, seatLocal.z));
            Vector3 standAt = door;
            int solid = EtsLayers.GroundMask | (1 << EtsLayers.Environment);

            if (Physics.Raycast(door + Vector3.up * 3f, Vector3.down, out RaycastHit hit, 30f, solid, QueryTriggerInteraction.Ignore))
            {
                standAt = hit.point + Vector3.up * 0.05f;
            }

            float yaw = truck.eulerAngles.y - side * 90f;
            bool moved = EtsVr.Teleport(standAt, yaw);

            if (moved)
            {
                Debug.Log("[SomniumEuroTruck] Player stood beside the truck at (" + standAt.x.ToString("0.0") + ", " + standAt.y.ToString("0.0") + ", " + standAt.z.ToString("0.0") + ").");
            }

            return moved;
        }

        private void Update()
        {
            // A rig destroyed under a seated player (its owner left or despawned it, a new truck serial), or
            // EtsLocalTruck.Occupied moved off it by someone else: only ever true on the new flow, where the seat
            // is bound to a rig (PLAN §3.5 "Truck vanishes").
            if (_onRig && _seated && (_truckRig == null || Truck == null || _occupiedTakenAway))
            {
                Vanished(_truckRig == null || Truck == null ? "gone" : "no longer this player's to sit in (EtsLocalTruck.Occupied moved)");
                return;
            }

            if (Truck == null || _seated || Time.time < _nextCheck)
            {
                return;
            }

            _nextCheck = Time.time + 0.5f;
            Transform player = PlayerRoot;

            if (player == null)
            {
                return;
            }

            // A Somnium player on foot and a driven truck cannot both be true: the truck is parked
            // (this also covers a player whose body arrived after the truck was set down).
            if (Truck.Driven)
            {
                Truck.SetDriven(false);
            }

            // The truck set down again - a job's depot, a respawn - with the player far from it.
            // Counted rather than compared by spawn point: a respawn lands on the same spawn point
            // but may be kilometres from where the player left the truck.
            if (Truck.Placed && (!_knowPlacement || Truck.Placements != _lastPlacements))
            {
                bool first = !_knowPlacement;
                _knowPlacement = true;
                _lastPlacements = Truck.Placements;

                if (!first && !Truck.Puppet && (player.position - Truck.transform.position).sqrMagnitude > LeftBehind * LeftBehind)
                {
                    StandBeside();
                    return;
                }
            }

            // The world streams round a walker (StreamFocus), so walking far from the truck no
            // longer walks off the edge of it. Dropping out of the world still happens: a player
            // well below a truck standing near them is brought back to it. Not a truck another
            // client is driving - it is not this player's to be brought to.
            if (Truck.Placed && !Truck.Puppet)
            {
                Vector3 offset = player.position - Truck.transform.position;
                float across = new Vector2(offset.x, offset.z).magnitude;

                if (across < WanderLimit && offset.y < -60f)
                {
                    Debug.Log("[SomniumEuroTruck] Player was " + Mathf.RoundToInt(-offset.y) + " m below the truck, out of the world; brought back to it.");
                    StandBeside();
                }
            }
        }

        private void LateUpdate()
        {
            if (!_seated || _root == null || Truck == null || !Truck.Placed)
            {
                return;
            }

            Transform truck = Truck.transform;

            // First: entering the pose moves and turns the rig (XRPosesFeature.SetPlayerToPose), and the seat
            // writes the rig after it.
            bool entered = ForceSitting && KeepSitting();

            Vector3 seatLocal = SeatLocalNow;
            _root.position = truck.TransformPoint(seatLocal);
            _root.rotation = SeatRotation(truck);
            Vector3 eye = truck.TransformPoint(EyeLocalNow);

            if (_onRig)
            {
                // Where to let a passenger out if the truck vanishes before the next frame (Vanished).
                _lastDrawn = new Pose(truck.position, truck.rotation);
                _lastSeatLocal = seatLocal;
                _haveLastDrawn = true;
            }

            if (EtsVr.Present)
            {
                if (entered)
                {
                    // The pose lowered the eyes in the rig by the difference between standing and sitting
                    // height, often under the 0.7 m that re-takes the rest: that is not a lean.
                    _restKnown = false;
                }

                PlaceHead(eye);

                // Now, and not in the wheel's own LateUpdate: script order between two components
                // is not fixed, and before this point the hands are where the truck was last frame.
                // The driver's seat only: a passenger has no wheel.
                if (Wheel != null && _seatIndex == 0)
                {
                    Wheel.AfterRigPlaced();
                }

                // New flow: the trigger that presses the tablet's buttons is also the accelerator. While the app is on
                // screen the pedals stay disarmed, so they come back only once both triggers have been let go after it
                // closes (EtsTruckController.DisarmVrPedals, the rule the truck keeps for getting in) - whichever of
                // this, the tablet's own input hold (EtsTabletApp.HoldInputWhileVisible) and the truck's Update runs
                // first in a frame. A passenger never has pedals: their seat is somebody else's truck (a puppet reads
                // no input), and a passenger seat in their own truck is not one a pedal may drive from either.
                if (EtsFeatures.NewFlow && (_seatIndex != 0 || EtsTabletApp.IsVisible))
                {
                    Truck.DisarmVrPedals();
                }
            }
            else if (_head != null && PoseLowered)
            {
                // Sitting, the avatar's feet are the body root (the pose's avatar root follows the root, not the
                // head: RootStates/CustomPoseState, RootsMovementHandler.GetDetachedRootPosition), and the pose has
                // lowered the head anchor to sitting height above it. Put that head on the driver's eye - the
                // camera EtsTruckCamera already places there - so the hips come down onto the seat.
                _root.position += eye - _head.position;
            }
        }

        /// <summary>Whether the client's pose has lowered the head anchor (NonXRPosesFeature sets the offset's y to sitting minus standing height).</summary>
        private bool PoseLowered => _poseOffset != null && _poseOffset.localPosition.y < -0.05f;

        /// <summary>
        /// The body root's rotation this frame: the truck's heading only, upright (the cab tilt was removed on 2026-09-25,
        /// see the class notes). No allocation.
        /// </summary>
        private static Quaternion SeatRotation(Transform truck)
        {
            return Quaternion.Euler(0f, truck.rotation.eulerAngles.y, 0f);
        }

        /// <summary>
        /// Stands the tilted body root (and the separate rig, when the camera is outside the body) upright again, keeping its
        /// heading and turning it about the eyes, so the view does not jump. Called as the seat lets go: the client's own
        /// pose exit (XRPosesFeature.ResetPose) only turns the root about its up and keeps any tilt, and a player whose truck
        /// vanished waits up to LandingWait seconds for ground with gravity back on before anything teleports them upright.
        /// </summary>
        private void LevelRig()
        {
            if (_root == null)
            {
                return;
            }

            Camera camera = EtsVr.Present ? EtsVr.Head() : null;
            Transform eyes = camera != null ? camera.transform : _head;
            float was = Vector3.Angle(_root.up, Vector3.up);
            LevelAbout(_root, eyes);

            if (_rig != null && _rig != _root)
            {
                was = Mathf.Max(was, Vector3.Angle(_rig.up, Vector3.up));
                LevelAbout(_rig, eyes);
            }

            if (was > 1f)
            {
                Debug.Log("[SomniumEuroTruck] Seat: body levelled on the way out (it was " + Mathf.RoundToInt(was) + " degrees off upright with the cab).");
            }
        }

        private static void LevelAbout(Transform body, Transform eyes)
        {
            if (body.up.y > 0.99995f)
            {
                return;
            }

            bool pivotOnEyes = eyes != null && eyes.IsChildOf(body);
            Vector3 before = pivotOnEyes ? eyes.position : body.position;
            body.rotation = Quaternion.Euler(0f, body.eulerAngles.y, 0f);

            if (pivotOnEyes)
            {
                body.position += before - eyes.position;
            }
        }

        /// <summary>
        /// Puts the avatar into SeatPose, or keeps it there. Called every seated frame: while the pose holds, the
        /// client only re-applies the same height (PosesFeature.SetCurrentPoseByValue -> ChangePoseParams); when it
        /// has dropped the pose (its cancel action this frame, an avatar reload), this re-enters it before anything
        /// is drawn. Returns true when that moved the eyes within the rig, i.e. the pose began this frame.
        /// </summary>
        private bool KeepSitting()
        {
            if (_poses == null)
            {
                return false;
            }

            Camera camera = EtsVr.Present ? EtsVr.Head() : null;
            Transform eyes = camera != null ? camera.transform : _head;
            FindPoseOffset(eyes);
            Vector3 before = eyes != null ? _root.InverseTransformPoint(eyes.position) : Vector3.zero;

            try
            {
                _poses.SetPose(SeatPose);
            }
            catch (System.Exception e)
            {
                ForceSitting = false;
                Debug.Log("[SomniumEuroTruck] Seat pose: the client threw on SetPose (" + e.GetType().Name + ": " + e.Message + "); sitting is off for this session.");
                return false;
            }

            // The camera and Body.Head (HeadAnchor) are both tracked under HeadCustomPoseOffset/HeadUserPostureOffset/HeadOffset.
            if (EtsVr.Present && eyes != null && _poseOffset != null)
            {
                UndoPoseRecentre(eyes);
            }

            bool moved = eyes != null && (_root.InverseTransformPoint(eyes.position) - before).sqrMagnitude > 0.03f * 0.03f;

            if (moved && !_poseReported)
            {
                _poseReported = true;
                Debug.Log("[SomniumEuroTruck] Seat pose " + SeatPose + " entered; eyes moved " + (_root.InverseTransformPoint(eyes.position) - before).ToString("F2") + " in the rig; pose offset " + (_poseOffset != null ? _poseOffset.localPosition.ToString("F2") : "not found") + ".");
            }

            return moved;
        }

        /// <summary>
        /// Entering a pose in a headset turns the rig so the body faces where the head looks, turns the client's
        /// HeadCustomPoseOffset (the camera's parent) the other way, and slides the rig under the head
        /// (XRPosesFeature.SetPlayerToPose). The seat writes the rig's yaw every frame, so the turn would stay in
        /// the view: whichever way the player looked when the pose began - at the door handle, usually - would
        /// become the truck's forward. The turn is taken out and the camera put back straight above the rig's
        /// origin, which is where the sitting body is drawn. The height the pose set is kept. Leaving the pose
        /// (XRPosesFeature.ResetPose) reads the offset's own position and forward, so it stays consistent.
        /// </summary>
        private void UndoPoseRecentre(Transform eyes)
        {
            if (_poseOffset.localRotation != Quaternion.identity)
            {
                _poseOffset.localRotation = Quaternion.identity;
                Vector3 inRig = _root.InverseTransformPoint(eyes.position);
                _poseOffset.position -= _root.TransformVector(new Vector3(inRig.x, 0f, inRig.z));
            }

            // The client turns the two hand offsets by the same angle as the head's (TargetsOffset's
            // LocalRotation setter writes all three) and only ever copies position to them after
            // that. Left turned, both hands swing round the tracking origin - a 40 degree entry puts
            // them a third of a metre from the real hands, drawn and read (the wheel grab uses these
            // anchors). All three hang under identity parents in one FloorOffset frame, so the head
            // offset's corrected local pose is theirs too.
            UndoHandRecentre(_leftHandOffset);
            UndoHandRecentre(_rightHandOffset);
        }

        private void UndoHandRecentre(Transform hand)
        {
            if (hand == null)
            {
                return;
            }

            if (hand.localRotation != Quaternion.identity)
            {
                hand.localRotation = Quaternion.identity;
            }

            hand.localPosition = _poseOffset.localPosition;
        }

        /// <summary>The client's pose offsets above each hand anchor (Player.XR.Body: Left/RightHandCustomPoseOffset).</summary>
        private Transform _leftHandOffset;
        private Transform _rightHandOffset;
        private Transform _leftHand;
        private Transform _rightHand;

        private static Transform AncestorNamed(Transform from, string name, Transform stop)
        {
            for (Transform t = from; t != null && t != stop; t = t.parent)
            {
                if (t.name == name)
                {
                    return t;
                }
            }

            return null;
        }

        /// <summary>Finds the client's HeadCustomPoseOffset among the ancestors of the camera (headset) or the head anchor (monitor).</summary>
        private void FindPoseOffset(Transform from)
        {
            // Searched once per camera (or head anchor): EtsVr.Head can change, and a rebuilt player brings new transforms.
            if (from == null || from == _poseOffsetFrom)
            {
                return;
            }

            _poseOffsetFrom = from;
            _poseOffset = null;

            for (Transform t = from; t != null && t != _root; t = t.parent)
            {
                if (t.name == PoseOffsetName)
                {
                    _poseOffset = t;
                    break;
                }
            }

            _leftHandOffset = AncestorNamed(_leftHand, "LeftHandCustomPoseOffset", _root);
            _rightHandOffset = AncestorNamed(_rightHand, "RightHandCustomPoseOffset", _root);

            Debug.Log("[SomniumEuroTruck] Seat pose: " + (_poseOffset != null
                ? "the client's " + PoseOffsetName + " is above '" + from.name + "'; hand offsets " +
                  (_leftHandOffset != null ? "left found" : "left MISSING") + ", " + (_rightHandOffset != null ? "right found" : "right MISSING") + "."
                : "no " + PoseOffsetName + " above '" + from.name + "'; a headset keeps the client's re-centring on entry and a monitor seat stays on the seat locator."));
        }

        /// <summary>
        /// Shifts the rig so the eyes land at the driver's eye, keeping the head's own movement
        /// since the player sat down. The rig is the body root when the camera hangs off it (the
        /// usual case, and the Quake world's test), else the top of the camera's own hierarchy.
        /// </summary>
        private void PlaceHead(Vector3 eye)
        {
            Camera head = EtsVr.Head();

            if (head == null)
            {
                return;
            }

            if (_rig == null)
            {
                bool carried = head.transform.IsChildOf(_root);
                _rig = carried ? _root : head.transform.root;
                Debug.Log("[SomniumEuroTruck] VR seat: the camera " + (carried ? "rides on the body" : "is outside the body") + "; moving rig '" + _rig.name + "'.");

                if (!carried)
                {
                    _rig.rotation = _root.rotation;
                }
            }
            else if (_rig != _root)
            {
                _rig.rotation = _root.rotation;
            }

            // The head's place in the rig changes only with tracking, so it is the lean.
            Vector3 inRig = _rig.InverseTransformPoint(head.transform.position);

            // Taken again when the head is further from it than any lean: tracking that came up
            // after the player sat down, or the player standing up or recentring in their room.
            if (!_restKnown || (inRig - _headRest).sqrMagnitude > 0.7f * 0.7f)
            {
                _headRest = inRig;
                _restKnown = true;
            }

            Vector3 lean = Vector3.ClampMagnitude(inRig - _headRest, LeanLimit);
            Vector3 wanted = eye + _rig.TransformVector(lean);
            _rig.position += wanted - head.transform.position;
        }

        private void OnDisable()
        {
            if (_subscribed)
            {
                _subscribed = false;
                EtsLocalTruck.OwnChanged -= OnOwnChanged;
                EtsLocalTruck.OccupiedChanged -= OnOccupiedChanged;
            }

            StopLanding();
            Release();
        }

        /// <summary>
        /// Adds (on) or takes back (off) the Vehicle layer in the local capsule's excludeLayers, for a passenger
        /// (EtsLayers.SeatedCapsuleExcludes). Only a bit this added is cleared again, so a mask the client set
        /// meanwhile is left as the client left it.
        /// </summary>
        private void ExcludeVehicles(bool on)
        {
            if (on)
            {
                _capsule = _root != null ? _root.GetComponent<CharacterController>() : null;

                if (_capsule != null && (_capsule.excludeLayers.value & EtsLayers.SeatedCapsuleExcludes) == 0)
                {
                    _capsule.excludeLayers = _capsule.excludeLayers.value | EtsLayers.SeatedCapsuleExcludes;
                    _capsuleExcluded = true;
                }

                return;
            }

            if (_capsuleExcluded)
            {
                _capsuleExcluded = false;

                if (_capsule != null)
                {
                    _capsule.excludeLayers = _capsule.excludeLayers.value & ~EtsLayers.SeatedCapsuleExcludes;
                }
            }
        }

        /// <summary>
        /// The truck under a seated player is gone (PLAN §3.5): release the seat - the client's own motion, gravity
        /// and collision come back - and stand the player beside the door where the truck was last drawn, once a
        /// ray finds ground there. The world streams round a player on foot (StreamFocus), so ground that is not
        /// built yet arrives; after LandingWait seconds without any the player is left to the client.
        /// </summary>
        private void Vanished(string why)
        {
            Debug.Log("[SomniumEuroTruck] Riders: seat released - truck of " + (_rigOwner >= 0 ? EtsNet.NameOf(_rigOwner) : "this player") + " " + why + ".");
            bool drawn = _haveLastDrawn;
            Pose pose = _lastDrawn;
            Vector3 seatLocal = _lastSeatLocal;
            WantSeated = false;
            Release();
            Bind(EtsLocalTruck.Own, 0);

            if (drawn && isActiveAndEnabled)
            {
                StopLanding();
                _landing = StartCoroutine(LandBeside(pose, seatLocal));
            }
        }

        private IEnumerator LandBeside(Pose truck, Vector3 seatLocal)
        {
            float side = seatLocal.x < 0f ? -1f : 1f;
            Vector3 door = truck.position + truck.rotation * new Vector3(seatLocal.x + side * 1.6f, seatLocal.y, seatLocal.z);
            float yaw = EtsRiderMath.YawOf(truck.rotation) - side * 90f;
            int solid = EtsLayers.GroundMask | (1 << EtsLayers.Environment);
            float deadline = Time.time + LandingWait;

            while (Time.time < deadline && !_seated)
            {
                if (Physics.Raycast(door + Vector3.up * 3f, Vector3.down, out RaycastHit hit, 30f, solid, QueryTriggerInteraction.Ignore))
                {
                    Vector3 standAt = hit.point + Vector3.up * 0.05f;
                    bool moved = EtsVr.Teleport(standAt, yaw);
                    Debug.Log("[SomniumEuroTruck] Riders: stood beside where the truck was, at (" + standAt.x.ToString("0.0") + ", " + standAt.y.ToString("0.0") + ", " + standAt.z.ToString("0.0") + ")" + (moved ? "." : ", but the Bridge would not move them."));
                    _landing = null;
                    yield break;
                }

                yield return new WaitForSeconds(0.5f);
            }

            // PLAN §3.5: no ground there within the wait - the session's arrival point, where every player first stood.
            if (!_seated)
            {
                if (TryArrival(out Vector3 arrival, out float arrivalYaw))
                {
                    bool moved = EtsVr.Teleport(arrival, arrivalYaw);
                    Debug.Log("[SomniumEuroTruck] Riders: no ground beside where the truck was within " + LandingWait.ToString("0") + " s; " +
                              (moved ? "sent to the arrival point (" + arrival.x.ToString("0.0") + ", " + arrival.y.ToString("0.0") + ", " + arrival.z.ToString("0.0") + ")." : "the Bridge would not move them to the arrival point."));
                }
                else
                {
                    Debug.Log("[SomniumEuroTruck] Riders: no ground beside where the truck was within " + LandingWait.ToString("0") + " s, and no arrival point yet; the player stays where the client has them.");
                }
            }

            _landing = null;
        }

        /// <summary>
        /// The session's arrival point in Unity coordinates (PLAN §3.5), from the contract members only: EtsSession.Choice
        /// (A: ArrivalGame in game coordinates, ArrivalYaw already a Unity yaw) and the map's centre EtsMapViewer.CenterGame
        /// (F), converted exactly as the map itself is placed - EtsModelBuilder.ToUnity(game - centre), EtsMapBuilder.ToWorld
        /// (Ets/EtsMapBuilder.cs:62-65). Stood on the ground under it when that is built (ray from 50 m above), else 0.05 m
        /// above the arrival height itself. False before the master has chosen or before the map is open.
        /// </summary>
        private static bool TryArrival(out Vector3 position, out float yaw)
        {
            position = Vector3.zero;
            yaw = 0f;
            EtsSession session = EtsSession.Instance;
            EtsMapViewer viewer = EtsMapViewer.Current;

            if (session == null || !session.HasChoice || viewer == null)
            {
                return false;
            }

            EtsHqChoice choice = session.Choice;
            Vector3 centre = viewer.CenterGame;
            position = EtsModelBuilder.ToUnity(choice.ArrivalGame.x - centre.x, choice.ArrivalGame.y - centre.y, choice.ArrivalGame.z - centre.z);
            yaw = choice.ArrivalYaw;
            int solid = EtsLayers.GroundMask | (1 << EtsLayers.Environment);

            // From 2.5 m above the arrival height first (Phase 2 integration): the arrival point lies under garage hall
            // prefab 280's roof at all 72 headquarters yards (F's sweep, probe 2100), and the roof is on Default, so the
            // 50 m ray stood the player on it, 7.4 m up. The 50 m ray stays as the fallback for a point with no floor.
            position = Physics.Raycast(position + Vector3.up * 2.5f, Vector3.down, out RaycastHit hit, 52.5f, solid, QueryTriggerInteraction.Ignore)
                       || Physics.Raycast(position + Vector3.up * 50f, Vector3.down, out hit, 100f, solid, QueryTriggerInteraction.Ignore)
                ? hit.point + Vector3.up * 0.05f
                : position + Vector3.up * 0.05f;
            return true;
        }

        private void StopLanding()
        {
            if (_landing != null)
            {
                StopCoroutine(_landing);
                _landing = null;
            }
        }

        private void Release()
        {
            if (!_seated)
            {
                return;
            }

            _seated = false;
            _occupiedTakenAway = false;

            if (Wheel != null)
            {
                Wheel.LetGo();
            }

            // Upright before the client's pose exit reads the root (a no-op now the seat never tilts; kept as a guard).
            LevelRig();

            // Out of the pose before anything moves the player: in a headset leaving it puts the rig back under
            // the head (XRPosesFeature.ResetPose), and GetOut teleports right after this.
            if (_poses != null)
            {
                try
                {
                    _poses.SetPose(PoseType.None);
                }
                catch (System.Exception e)
                {
                    Debug.Log("[SomniumEuroTruck] Seat pose: the client threw leaving the pose (" + e.GetType().Name + ": " + e.Message + ").");
                }
            }

            _avatar?.SetVRIKLegMovementDisabled(false);

            if (_motion != null)
            {
                _motion.SetCollisionDisableState(false);
                _motion.SetFlyModeDisableState(false);
                _motion.SetTeleportationDisableState(false);
                _motion.SetMovementSpeed(1f, 1f, 1f);
                _motion.SetGravity(1f, 1f);
                _motion.SetJump(1f, 1f);
            }

            _animation?.SetFallingAnimationDisabled(false);
            ExcludeVehicles(false);

            // Parking is the driver's: a passenger leaving must never park somebody else's truck (PLAN C10).
            if (Truck != null && _seatIndex == 0)
            {
                Truck.SetDriven(false);
            }

            // The owner's seat table names this player until their rider record stops naming the seat (PLAN §5.3 ints
            // 26-27, owner-written; [net §2.7] step 4): clearing the record here, not at EtsRiders' next pass, also covers
            // a seat disabled with the whole bootstrap (no pass follows). The owner then frees the seat after its
            // StaleAfter (2 s) - not at once (checker probe 2240 L4: 2.2 s after LeaveSeat). Every way out of a passenger
            // seat ends here - GetOut, the truck vanishing (Vanished), the seat disabled.
            if (_onRig && _seatIndex != 0)
            {
                EtsNet net = EtsNet.Instance;

                if (net != null)
                {
                    net.LeaveSeat();
                }
            }

            if (_onRig)
            {
                EtsLocalTruck.SetOccupied(null);
            }
        }
    }
}
