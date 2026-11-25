using System;
using System.Collections.Generic;
using Fusion;
using SomniumSpace.Bridge;
using SomniumSpace.Bridge.Player.Components;
using SomniumSpace.Network.Bridge;
using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
using UnityEngine;

namespace SomniumSpace.Worlds.SomniumEuroTruck
{
    // EtsPhase and EtsHqChoice (PLAN §7.A) live in EtsSessionWire.cs beside the pure EtsSessionLogic that uses
    // them, so the offline tests compile without Fusion (S/phase0/PENDING_MEMBERS.md: "A may move them").

    /// <summary>
    /// What a joiner loads before the arrival teleport: the map at the HQ, the remote trucks prebuilt, the traffic
    /// prewarm (owner A; [net §2.9], PLAN §1 row 16, §7.A). RemoteTrucks: the other players' trucks in the room when Loading
    /// began (EtsNet.RemoteTruckIdentities, from their slots); EtsNet builds their puppets as their slots count, whatever this says.
    /// </summary>
    public sealed class EtsLoadPlan
    {
        public EtsHqChoice Hq;
        public readonly List<EtsTruckIdentity> RemoteTrucks = new List<EtsTruckIdentity>();
        public bool Traffic;
    }

    /// <summary>
    /// The session: phases, the master, the HQ choice, the load plan, the arrival (owner A; [net §2.2, §2.9, §2.11],
    /// PLAN.md §3.2, §3.3, §5.2, §5.5, §7.A). The rules are EtsSessionLogic's and EtsLoadTracker's (pure, tested
    /// offline by probes 1824, 1832 and 2001); this component feeds them what the room and the scene say and carries
    /// out what they ask: it finds "Ets Net Session", reads its 64 ints, asks for its state authority when the logic
    /// has something to write, writes the ints that differ, raises the events, and runs the Loading phase.
    ///
    /// Ticked by EtsNet.Update (Tick, internal), not by an Update of its own: EtsNet, the only scene-referenced world
    /// script with an order (-100), is the one place the world sequences its passes (PLAN §3.3; the client honours
    /// [DefaultExecutionOrder] for runtime-added components too - Player.log 2026-09-23 01:55 "Order: EtsNet first
    /// yes" - but one tick owner keeps the order readable). EtsNet adds and ticks it only while EtsFeatures.NewFlow
    /// is on; with the flag off nothing here runs.
    ///
    /// The scene start (PLAN §5.5 step 2): Start opens the game's map (EtsMapViewer.Open: the archives and the map
    /// index files, no building), because the scene's Ets Map Viewer no longer loads on its own (_loadOnStart 0 since
    /// Phase 2) and the HQ stage, the map index and the HQ catalogue all read the opened map. Open is idempotent, so
    /// the HQ stage calling it too is harmless.
    ///
    /// Loading (PLAN §5.5 step 6, [net §2.9], U17, U19): on entering it, the map viewer is moved to the chosen
    /// origin (LoadAt: the old build, if any, is thrown away) and streams round an "Ets Arrival Focus" transform at
    /// the arrival point (StreamFocusOverride) - the player stands in the HQ void 600 m up, and EtsGroundFill's rays
    /// start 400 m above their focus. When the map has built there, the arrival ring ([net §2.9]: 8 places 45
    /// degrees apart at 2.5 m round the session's arrival point, this player's own place first, then an outer ring
    /// at 5 m) is probed for a spot where the ray from above meets the drive surface first (open sky first, under a
    /// canopy only when no place has it; no truck) and a standing capsule touches no building or vehicle; once D's HQ
    /// stage has put the player in the void (so its teleport can never land after this one), the player is teleported
    /// there facing the yard's first stand, the Bridge's call-back (or the body standing there) confirms it, and only
    /// then does the phase become InWorld - so the HQ stage (D) is never removed from under a player still in the void.
    /// Leaving Loading hands the stream back to its usual focus. In the editor there is no Bridge player: the camera is
    /// moved instead.
    ///
    /// Log lines ("[SomniumEuroTruck] Network: session: ..."): every phase change with the master, epoch, city and
    /// map signature; every master change; the offline notices; the Loading stages with their times; the arrival.
    /// </summary>
    public sealed class EtsSession : MonoBehaviour
    {
        public static EtsSession Instance { get; private set; }

        /// <summary>Get or add (PLAN §3.2). EtsNet.Awake calls it only while EtsFeatures.NewFlow is on.</summary>
        public static EtsSession Ensure(GameObject host)
        {
            if (host == null)
            {
                return null;
            }

            // Unity's null test, not ??: a missing component is a fake null in the editor that ?? keeps.
            EtsSession session = host.GetComponent<EtsSession>();

            if (session == null)
            {
                session = host.AddComponent<EtsSession>();
            }

            return session;
        }

        /// <summary>Seconds without a runner before the client plays Offline (its own master) [net §2.2]; independent of the tablet's FallbackAfter (PLAN C19).</summary>
        public float OfflineAfter = 20f;

        private readonly EtsSessionLogic _logic = new EtsSessionLogic();
        private readonly EtsLoadTracker _loader = new EtsLoadTracker();

        public EtsPhase Current => _logic.Phase;

        /// <summary>True when Offline; false while Connecting.</summary>
        public bool IsMaster => _logic.IsMaster;

        /// <summary>-1 when unknown or offline.</summary>
        public int MasterPlayerId => _logic.MasterId;

        public string MasterName { get; private set; } = "";
        public bool HasChoice => _logic.HasChoice;
        public EtsHqChoice Choice => _logic.Choice;
        public ulong PreviewCityToken => _logic.PreviewCity;

        /// <summary>Minutes since Monday 00:00; 600 at the start (/def/initial_save/normal/game.sii game_time: 600; PLAN §3.11).</summary>
        public int GameClockMinutes => _logic.ClockMinutes;

        /// <summary>
        /// Game minutes a real minute for the master's clock (EtsSessionLogic.ClockScale; 0 = the clock stands). Since
        /// 2026-10-14 EtsDayNight sets the map's time compression (19, /map/europe.mbd), so the master writes each new minute.
        /// </summary>
        public float ClockScale
        {
            get => _logic.ClockScale;
            set => _logic.ClockScale = value;
        }

        /// <summary>0..1 while Loading or Relocating: the map build is 90 % of it, the ground and the teleport the rest.</summary>
        public float LoadProgress { get; private set; }

        /// <summary>For the log only.</summary>
        public string LoadStage { get; private set; } = "";

        public EtsLoadPlan Plan { get; private set; }

        /// <summary>The master's install has other map sectors than this one (the HQ stage may say so).</summary>
        public bool MapMismatch => _logic.MapMismatch;

        /// <summary>The session epoch (1 from the first choice; 0 before it and for an offline choice): the player slot's header carries it.</summary>
        public int Epoch => _logic.Epoch;

        /// <summary>This install's map signature (EtsSessionWire.MapSignature; 0 until the map is open): the player slot's int 42.</summary>
        public int MapSignature => _logic.LocalMapSignature;

        /// <summary>
        /// Where this player arrives, in Unity coordinates (the map's centre is the chosen origin from Loading on):
        /// the standing spot the arrival found, or the session's raw arrival point before that. B's §3.5 fallback
        /// ("no ground within 10 s: teleport to the session arrival point") reads it. False until Loading began.
        /// </summary>
        public bool HasArrival { get; private set; }
        public Vector3 ArrivalUnity { get; private set; }

        /// <summary>The Unity yaw the player was turned to on arrival (towards the yard's first stand).</summary>
        public float ArrivalYaw { get; private set; }

        public event Action<EtsPhase> PhaseChanged;

        /// <summary>(isMe, masterName).</summary>
        public event Action<bool, string> MasterChanged;

        private NetworkBridgeData _session;
        private float _nextFind;
        private float _nextAsk;
        private readonly int[] _live = new int[EtsSessionWire.Ints];
        private readonly int[] _out = new int[EtsSessionWire.Ints];
        private bool _hadMaster;
        private float _nextNameCheck;

        // ------------------------------------------------------------------ Loading's state
        private EtsMapViewer _viewer;
        private bool _loading;
        private bool _mapOpened;
        private Transform _focus;
        private readonly List<Vector3> _ring = new List<Vector3>();
        private int _ringIndex;
        private bool _haveSpot;
        private int _spotIndex = -1;
        private Vector3 _spot;
        private float _spotYaw;
        private int _teleportCalls;
        private int _teleportCallsAtAsk;
        private Action _onTeleported;
        private bool _sawBuilding;
        private double _loadingSince;

        /// <summary>What the arrival probe's ray may hit first: the drive surface, a building, a vehicle.</summary>
        private const int ProbeMask = EtsLayers.GroundMask | (1 << EtsLayers.Environment) | (1 << EtsLayers.Vehicle);

        /// <summary>What a standing player's capsule must not touch at the spot: buildings and props, trucks and cars.</summary>
        private const int BlockMask = (1 << EtsLayers.Environment) | (1 << EtsLayers.Vehicle);

        /// <summary>How far above and below the arrival point a spot's ground may lie (a yard is flat; a bridge deck or a ditch is not the yard).</summary>
        private const float SpotHeightRange = 10f;

        /// <summary>FindSpot's first pass casts from this far above the arrival's height: open sky only.</summary>
        private const float SpotRayOpenSky = 40f;

        /// <summary>FindSpot's second pass: from just above head height, under whatever canopy covers the yard.</summary>
        private const float SpotRayUnderRoof = 2.5f;

        /// <summary>The first pass takes a hit at most this far above the arrival's height (map heights are exact to centimetres); higher is a roof.</summary>
        private const float SpotOpenSkyAbove = 1.5f;

        private int _spotPass; // FindSpot's pass that found the spot: 0 open sky, 1 under a roof, 2 the first hit whatever it is

        /// <summary>Distance from the arrival point to the yard's first stand ([gameui §3.1], EtsHqCatalogBuilder.ArrivalSideStep): the player is turned to face it.</summary>
        private const float StandDistance = 6f;

        /// <summary>Master or Offline only; ignored otherwise (logged).</summary>
        public void ChooseHeadquarters(EtsHqChoice choice)
        {
            if (!_logic.Choose(choice))
            {
                Debug.LogWarning("[SomniumEuroTruck] Network: session: a headquarters choice (city " + choice.City + ") was ignored in phase " + _logic.Phase + (_logic.IsMaster ? "" : " (not the master)") + ".");
                return;
            }

            // Raised now, not at the next Tick, so the HQ stage and the loading screen swap in the same frame.
            Changed();
        }

        /// <summary>Master while ChoosingHq: the hovered city, lit on the watchers' maps.</summary>
        public void PreviewCity(ulong cityToken)
        {
            _logic.Preview(cityToken);
        }

        /// <summary>Called by EtsNet.Update first thing (PLAN §3.3), and only while EtsFeatures.NewFlow is on.</summary>
        internal void Tick()
        {
            _logic.OfflineAfter = OfflineAfter;
            Find();
            NetworkRunner runner = SceneNetworking.NetworkRunnerRef;
            bool roomReady = SceneNetworking.IsNetworkReady && runner != null;
            bool valid = _session != null && _session.Object != null && _session.Object.IsValid;

            if (valid)
            {
                NetworkArray<int> ints = _session.IntArray;

                for (int i = 0; i < _live.Length && i < ints.Length; i++)
                {
                    _live[i] = ints.Get(i);
                }
            }

            if (_logic.LocalMapSignature == 0)
            {
                EtsMapViewer viewer = EtsMapViewer.Current;

                if (viewer != null && viewer.Map != null && viewer.Map.AvailableSectors.Count > 0)
                {
                    // The game version string is not read anywhere in the world yet, so "" (both sides alike).
                    _logic.LocalMapSignature = EtsSessionWire.MapSignature(viewer.Map.AvailableSectors, "");
                }
            }

            EtsNet net = EtsNet.Instance;
            _logic.MaxTrucksToWrite = net != null ? net.MaxTrucks : 16;

            // The master the rules go by: the room's (IsSharedModeMasterClient), unless it has no game - then the longest-present
            // player with the game acts for it (EtsNet.ActsAsMaster, EtsSlotRules.ActingMaster; integrator, Phase 3: a no-game
            // visitor first in could never choose, and every later joiner waited until they left). The acting master asks the
            // session object's authority (WantAuthority, RequestStateAuthority below) exactly as a new master after a hand-over.
            bool roomMaster = roomReady && SceneNetworking.IsMasterClient;
            bool acting = net != null ? roomReady && net.ActsAsMaster(roomMaster) : roomMaster;

            // The loader first: what it says is this frame's LoadDone.
            if (_logic.Phase == EtsPhase.Loading && _loading)
            {
                StepLoading(runner);
            }

            var input = new EtsSessionLogic.Inputs
            {
                Now = Time.unscaledTimeAsDouble,
                NoClient = Application.isEditor,
                RoomReady = roomReady,
                SessionFound = _session != null,
                SessionValid = valid,
                HasAuthority = valid && _session.Object.HasStateAuthority,
                IsMaster = acting,
                LocalId = runner != null ? runner.LocalPlayer.PlayerId : -1,
                // Who the master is when it is not this client: the player whose slot carries the "I am the room master" bit
                // (EtsNet.SlotMasterId, each player's own IsSharedModeMasterClient; PLAN §12, A3). -1 when no slot says so yet,
                // and then EtsSessionLogic.Master names the session's writer, as in Phase 2. This client as master is the
                // runner's own answer (IsMaster), never a slot's, so a stale bit during a hand-over cannot override it.
                MasterId = roomReady && !acting && net != null ? net.SlotMasterId : -1,
                Live = valid ? _live : null,
                LoadDone = _logic.Phase == EtsPhase.Loading && _loading && _loader.Done,

                // True while another present slot shows a player loading or in the world under a chosen session (EtsNet
                // reads the slots, EtsSlotRules.OthersHaveSession); the 1.5 s settle guards the join snapshot before that.
                OthersHaveSession = net != null && net.OthersHaveSession
            };

            _logic.Step(input);

            if (_logic.WantAuthority && Time.unscaledTime >= _nextAsk)
            {
                _nextAsk = Time.unscaledTime + 1f;
                _session.Object.RequestStateAuthority();
            }

            if (_logic.HasWrite && input.HasAuthority)
            {
                _logic.WriteInto(_out);
                NetworkArray<int> ints = _session.IntArray;

                for (int i = 0; i < _out.Length && i < ints.Length; i++)
                {
                    if (ints.Get(i) != _out[i])
                    {
                        ints.Set(i, _out[i]);
                    }
                }
            }

            if (_logic.Phase == EtsPhase.Loading && _loading)
            {
                // Never backwards within one Loading: EtsMapViewer.LoadProgress is a static that still reads 1 from a
                // previous visit until this build's first stage writes it (EtsMapViewer resets it only when a build
                // replaces another in the same scene), so it counts only once this build has been seen running.
                LoadProgress = Mathf.Max(LoadProgress, _loader.Progress(_sawBuilding ? EtsMapViewer.LoadProgress : 0f));
            }

            Changed();
            RefreshMasterName();
        }

        private void Changed()
        {
            if (_logic.TakeMasterChanged())
            {
                // A room blink while waiting makes the master unknown (-1) for its length (probe 1824 case 8b: 15 s):
                // the waiting banner keeps the name it had rather than "Waiting for  to choose".
                bool blink = _logic.MasterId < 0 && !_logic.IsMaster && _logic.Phase == EtsPhase.WaitingForMaster;
                MasterName = _logic.MasterId >= 0 ? EtsNet.NameOf(_logic.MasterId) : _logic.IsMaster ? LocalName() : blink ? MasterName : "";
                _nextNameCheck = Time.unscaledTime + 2f;
                Debug.Log("[SomniumEuroTruck] Network: session: master " + (_logic.IsMaster ? "me" : _logic.MasterId >= 0 ? "player " + _logic.MasterId + " (" + MasterName + ")" : "unknown") + " in phase " + _logic.Phase + ".");

                // The news line (PLAN §3.7 item 5), from the new master only, and not for the room's first one.
                if (_logic.IsMaster && _logic.MasterId >= 0 && _hadMaster && EtsNet.Instance != null)
                {
                    EtsNet.Instance.Announce(MasterName + " is now the instance master"); // not in the locale
                }

                _hadMaster |= _logic.MasterId >= 0;
                MasterChanged?.Invoke(_logic.IsMaster, MasterName);
            }

            if (_logic.TakeNotice())
            {
                Debug.Log("[SomniumEuroTruck] Network: session: " + _logic.Reason + ".");
            }

            if (!_logic.TakePhaseChanged())
            {
                return;
            }

            if (_logic.Phase == EtsPhase.Loading)
            {
                StartLoading();
            }
            else if (_loading)
            {
                EndLoading();
            }

            Debug.Log("[SomniumEuroTruck] Network: session: phase " + _logic.Phase + " (" + _logic.Reason + "), master " + (_logic.IsMaster ? "me" : _logic.MasterId >= 0 ? "player " + _logic.MasterId : "unknown") +
                      ", epoch " + _logic.Epoch + (_logic.HasChoice ? ", city " + _logic.Choice.City : "") + ", map signature " + (_logic.MapMismatch ? "MISMATCH" : _logic.LocalMapSignature == 0 ? "not known yet" : "ok") + ".");
            PhaseChanged?.Invoke(_logic.Phase);
        }

        /// <summary>
        /// A master named before the Bridge knew their nickname is "Player N" (EtsNet.NameOf's fallback): asked again
        /// every 2 s until the name arrives, and the change announced (MasterChanged) so the banners follow. NameOf
        /// walks the Bridge's player list, which is copied on each call ([net F10]), hence the slow rate.
        /// </summary>
        private void RefreshMasterName()
        {
            if (_logic.MasterId < 0 || Time.unscaledTime < _nextNameCheck)
            {
                return;
            }

            _nextNameCheck = Time.unscaledTime + 2f;

            if (MasterName.Length > 0 && !MasterName.StartsWith("Player ", StringComparison.Ordinal))
            {
                return;
            }

            string name = EtsNet.NameOf(_logic.MasterId);

            if (name != MasterName)
            {
                MasterName = name;
                Debug.Log("[SomniumEuroTruck] Network: session: the master's name is " + name + ".");
                MasterChanged?.Invoke(_logic.IsMaster, MasterName);
            }
        }

        // ------------------------------------------------------------------ Loading and the arrival

        /// <summary>
        /// The yard at the chosen origin (F's EtsMapViewer.LoadAt, PLAN §7.X), streamed round the arrival point while
        /// the player waits in the void (StreamFocusOverride, U19), and this player's place on the arrival ring.
        /// </summary>
        private void StartLoading()
        {
            EtsHqChoice choice = _logic.Choice;
            Plan = new EtsLoadPlan { Hq = choice, Traffic = (_logic.Options & EtsSessionWire.OptionTraffic) != 0 };
            EtsNet.Instance?.RemoteTruckIdentities(Plan.RemoteTrucks);
            LoadProgress = 0f;
            LoadStage = "map";
            _loading = true;
            _haveSpot = false;
            _spotPass = 0;
            _spotIndex = -1;
            _sawBuilding = false;
            _loadingSince = Time.unscaledTimeAsDouble;
            _teleportCallsAtAsk = _teleportCalls;

            ArrivalUnity = EtsArrival.GameToUnity(choice.ArrivalGame, choice.OriginGame);
            ArrivalYaw = choice.ArrivalYaw;
            HasArrival = true;

            EtsNet net = EtsNet.Instance;
            _ringIndex = net != null && net.MySlot >= 0 ? net.MySlot : Math.Max(0, net != null ? net.LocalId : 0);
            EtsArrival.RingCandidates(ArrivalUnity, ArrivalYaw, _ringIndex, _ring);

            if (_focus == null)
            {
                _focus = new GameObject("Ets Arrival Focus").transform;
                _focus.SetParent(transform, false);
            }

            _focus.position = ArrivalUnity;
            _loader.Begin(_loadingSince);
            EtsMapViewer viewer = MapViewer();

            if (viewer != null)
            {
                viewer.StreamFocusOverride = _focus;
                viewer.LoadAt(choice.OriginGame);
            }

            Debug.Log("[SomniumEuroTruck] Network: session: loading the headquarters (city " + choice.City + ", yard prefab " + choice.YardPrefabUid.ToString("x16") + "): map centre " + Game(choice.OriginGame) +
                      ", arrival at Unity " + ArrivalUnity.ToString("0.0") + " facing " + ArrivalYaw.ToString("0") + " degrees, ring place " + (_ringIndex % EtsArrival.RingPlaces) +
                      (viewer != null ? "; streaming round the arrival point." : "; NO MAP VIEWER in the scene."));
        }

        private void StepLoading(NetworkRunner runner)
        {
            EtsMapViewer viewer = MapViewer();
            double now = Time.unscaledTimeAsDouble;

            // The build at the new centre is watched through progress, not AnyCompleted alone: the statics outlive a
            // world visit, so on a second visit AnyCompleted can still be true from the first one before this build
            // has laid a single road (EtsMapViewer resets it only when a build replaces another in the same scene).
            bool centred = viewer != null && (viewer.CenterGame - _logic.Choice.OriginGame).sqrMagnitude < 1f;

            if (!EtsMapViewer.AnyCompleted || EtsMapViewer.LoadProgress < 0.999f)
            {
                _sawBuilding = true;
            }

            bool mapReady = centred && EtsMapViewer.AnyCompleted && EtsMapViewer.LoadProgress >= 0.999f && (_sawBuilding || now - _loadingSince > 20.0);
            ISomniumPlayerMotion motion = null;
            bool noClient = false;
            bool teleported = false;

            bool stageDone = true;

            if (_loader.Current == EtsLoadTracker.Stage.Teleport)
            {
                motion = EtsVr.Motion;
                noClient = Application.isEditor && motion == null;
                teleported = _teleportCalls > _teleportCallsAtAsk || BodyAtSpot();

                // D's HQ stage puts the player into the void as soon as the Bridge player exists (EtsHqStage.PlacePlayer,
                // retried at 4 Hz whatever the phase). A player who appears only after the yard is built would get both
                // teleports within a quarter second, and the later one wins: the void's, and then the void goes (the phase
                // is InWorld) under them. So the arrival waits until the stage has placed the player, and comes after it.
                EtsHqStage stage = EtsHqStage.Current;
                stageDone = stage == null || stage.Placed;
            }

            var input = new EtsLoadTracker.Inputs
            {
                Now = now,
                MapReady = mapReady,
                MapFailed = viewer == null || (now - _loadingSince > 2.0 && !viewer.IsOpen),
                GroundFound = _haveSpot,
                HasPlayer = motion != null && stageDone,
                NoClient = noClient,
                Teleported = teleported
            };

            _loader.Step(input);

            if (_loader.ProbeGround)
            {
                _haveSpot = FindSpot();
            }

            if (_loader.AskTeleport)
            {
                Teleport(motion);
            }

            if (_loader.TakeChanged())
            {
                // Named on a change only: an enum's ToString is a new string every call.
                LoadStage = _loader.Current.ToString();
                Debug.Log("[SomniumEuroTruck] Network: session: loading - " + _loader.Why + " after " + (now - _loadingSince).ToString("0.0") + " s.");

                if (_loader.Done)
                {
                    Debug.Log("[SomniumEuroTruck] Network: session: arrival " + (_loader.Unconfirmed ? "NOT CONFIRMED" : "done") + " at Unity " + ArrivalUnity.ToString("0.0") + " facing " + ArrivalYaw.ToString("0") +
                              " degrees" + (_haveSpot ? " (ring place " + _spotIndex + " of " + _ring.Count + (_spotPass == 1 ? ", under a roof: no ring place had open sky" : _spotPass == 2 ? ", ON THE FIRST SURFACE FROM ABOVE: no ring place had open sky or a floor under a roof" : "") + (_spotSight ? ", the first stand in view" : ", NO VIEW of the first stand from any place of that pass") + ")" : _loader.GroundTimedOut ? " (the raw arrival point: no ground found)" : "") +
                              "; map " + Seconds(_loader.MapAt) + ", ground " + Seconds(_loader.GroundAt) + ", done " + Seconds(_loader.DoneAt) + " after loading began; " + _loader.Asks + " teleport ask(s).");
                }
            }
        }

        private string Seconds(double at)
        {
            return double.IsNaN(at) ? "-" : (at - _loadingSince).ToString("0.0") + " s";
        }

        /// <summary>
        /// The first ring place (this player's own first) where a ray from above meets the drive surface before any
        /// building or vehicle, within 10 m of the arrival's height, and a standing capsule (0.3 m, up to 1.7 m) touches
        /// no building or vehicle. Three passes: first under open sky (the ray from 40 m up, its first hit no more than
        /// 1.5 m above the arrival's height, so nobody arrives on a roof or inside a shed), then - only when no place of
        /// the 17 has open sky, a yard under a canopy - from 2.5 m above the arrival's height, which a canopy or a roof is
        /// above. Without the second pass such a yard would hold the player in the void for GroundWait (30 s) and then
        /// drop them on the raw point. The third pass is the first pass without the roof rule (the Phase 1 behaviour), for
        /// a yard whose ground neither of the others finds: a roof is still better than the raw point. At most 51 rays and
        /// capsules, at 4 Hz, while Loading only.
        /// </summary>
        private bool FindSpot()
        {
            // The yard's first stand, where the player's truck will stand (StandDistance ahead of the arrival point along its
            // yaw; D's catalogue puts the arrival 6 m to the side of it): what the arriving player should be able to see.
            Vector3 stand = ArrivalUnity + EtsArrival.Forward(ArrivalYaw) * StandDistance + Vector3.up * SightHeight;

            for (int pass = 0; pass < 3; pass++)
            {
                int blind = -1;
                Vector3 blindFeet = Vector3.zero;

                for (int i = 0; i < _ring.Count; i++)
                {
                    Vector3 p = _ring[i];
                    float above = pass == 1 ? SpotRayUnderRoof : SpotRayOpenSky;
                    Vector3 from = new Vector3(p.x, ArrivalUnity.y + above, p.z);

                    if (!Physics.Raycast(from, Vector3.down, out RaycastHit hit, above + SpotHeightRange + 20f, ProbeMask, QueryTriggerInteraction.Ignore))
                    {
                        continue;
                    }

                    if (((1 << hit.collider.gameObject.layer) & EtsLayers.GroundMask) == 0 || Mathf.Abs(hit.point.y - ArrivalUnity.y) > SpotHeightRange)
                    {
                        continue;
                    }

                    // Open sky means the first thing the ray meets is the yard itself, not a roof over it: garage hall
                    // prefab 280's roof pieces (+6.92, +7.38 m) are on Default, a drive-surface layer, and sit over the
                    // arrival point at all 72 headquarters yards (F's sweep, probe 2100). Without this the first pass
                    // stood every arriving player on the hall's roof; the second pass, from 2.5 m, finds its floor.
                    if (pass == 0 && hit.point.y - ArrivalUnity.y > SpotOpenSkyAbove)
                    {
                        continue;
                    }

                    Vector3 feet = hit.point + Vector3.up * 0.05f;

                    if (Physics.CheckCapsule(feet + Vector3.up * 0.4f, feet + Vector3.up * 1.7f, 0.3f, BlockMask, QueryTriggerInteraction.Ignore))
                    {
                        continue;
                    }

                    // Phase 2's request (D, integrate.md): a place from which the first stand can be seen comes first - a
                    // wall, a pillar or a fence between the player and their truck's stand is the wrong first sight of the
                    // yard. Buildings and the ground itself block the view; vehicles do not (they move, and a truck at the
                    // stand is what is to be seen). A place without the view is kept in case no place of this pass has it.
                    if (Physics.Linecast(feet + Vector3.up * SightHeight, stand, SightMask, QueryTriggerInteraction.Ignore))
                    {
                        if (blind < 0)
                        {
                            blind = i;
                            blindFeet = feet;
                        }

                        continue;
                    }

                    TakeSpot(feet, i, pass, true);
                    return true;
                }

                if (blind >= 0)
                {
                    TakeSpot(blindFeet, blind, pass, false);
                    return true;
                }
            }

            return false;
        }

        /// <summary>Eye height for the sight line to the first stand, and the height it aims at above the stand.</summary>
        private const float SightHeight = 1.6f;

        /// <summary>What blocks the sight line: the drive surface and terrain (a hump), and buildings and props (Environment).</summary>
        private const int SightMask = EtsLayers.GroundMask | (1 << EtsLayers.Environment);

        /// <summary>The found place saw the first stand (the log line says so when it did not).</summary>
        private bool _spotSight;

        private void TakeSpot(Vector3 feet, int index, int pass, bool sight)
        {
            _spot = feet;
            _spotIndex = index;
            _spotPass = pass;
            _spotSight = sight;

            // Facing the yard's first stand, which lies StandDistance ahead of the arrival point along its yaw.
            Vector3 look = ArrivalUnity + EtsArrival.Forward(ArrivalYaw) * StandDistance - feet;
            _spotYaw = look.x * look.x + look.z * look.z > 0.01f ? Mathf.Atan2(look.x, look.z) * Mathf.Rad2Deg : ArrivalYaw;
        }

        private void Teleport(ISomniumPlayerMotion motion)
        {
            Vector3 target = _haveSpot ? _spot : ArrivalUnity + Vector3.up * 0.05f;
            float yaw = _haveSpot ? _spotYaw : ArrivalYaw;
            ArrivalUnity = target;
            ArrivalYaw = yaw;

            if (motion != null)
            {
                // The Bridge teleports at the end of this frame and calls back then (NonXR) or a frame later (XR).
                _teleportCallsAtAsk = _teleportCalls;
                motion.DoTeleportToPoint(target, new Vector3(0f, yaw, 0f), _onTeleported);
                Debug.Log("[SomniumEuroTruck] Network: session: arrival teleport asked (" + _loader.Asks + ") to " + target.ToString("0.00") + " facing " + yaw.ToString("0") + " degrees.");
                return;
            }

            // The editor (the tracker asks only when there is no client at all): its camera goes to the spot instead.
            Camera eye = Camera.main;

            if (eye != null)
            {
                eye.transform.SetPositionAndRotation(target + Vector3.up * 1.7f, Quaternion.Euler(10f, yaw, 0f));
            }

            Debug.Log("[SomniumEuroTruck] Network: session: no Bridge player (the editor): " + (eye != null ? "the camera '" + eye.name + "' was moved to the arrival spot." : "no camera to move."));
        }

        /// <summary>The body already stands at the spot (within 1.5 m across, 2.5 m in height): a teleport whose call-back was lost.</summary>
        private bool BodyAtSpot()
        {
            if (_loader.Asks == 0)
            {
                return false;
            }

            Transform root = null;

            try
            {
                root = SomniumBridge.PlayersContainer?.LocalPlayer?.References?.Body?.Root;
            }
            catch (Exception)
            {
                // No client (the editor).
            }

            if (root == null)
            {
                return false;
            }

            Vector3 d = root.position - ArrivalUnity;
            return d.x * d.x + d.z * d.z < 1.5f * 1.5f && Mathf.Abs(d.y) < 2.5f;
        }

        private void OnTeleported()
        {
            _teleportCalls++;
        }

        /// <summary>The stream goes back to its usual focus (the player, or the truck they sit in); the focus object goes.</summary>
        private void EndLoading()
        {
            _loading = false;
            _loader.Reset();
            LoadProgress = _logic.Phase == EtsPhase.InWorld ? 1f : 0f;

            if (_viewer != null && _viewer.StreamFocusOverride == _focus)
            {
                _viewer.StreamFocusOverride = null;
            }

            if (_focus != null)
            {
                Destroy(_focus.gameObject);
                _focus = null;
            }
        }

        /// <summary>The scene's map viewer: the one opened or loaded last, else the scene's (found once).</summary>
        private EtsMapViewer MapViewer()
        {
            if (_viewer == null)
            {
                _viewer = EtsMapViewer.Current != null ? EtsMapViewer.Current : FindFirstObjectByType<EtsMapViewer>();
            }

            return _viewer;
        }

        private static string Game(Vector3 g)
        {
            return "(" + g.x.ToString("0.0") + ", " + g.y.ToString("0.0") + ", " + g.z.ToString("0.0") + ")";
        }

        /// <summary>This player's nickname for an Offline master (no PlayerId to name them by); "" without a client.</summary>
        private static string LocalName()
        {
            try
            {
                string nick = SomniumBridge.PlayersContainer?.LocalPlayer?.Properties?.NickName;
                return string.IsNullOrEmpty(nick) ? "" : nick;
            }
            catch (Exception)
            {
                return "";
            }
        }

        /// <summary>By name, as EtsNet.Find does, once a second until found (Fusion makes it live only once the room is joined).</summary>
        private void Find()
        {
            if (_session != null || Time.unscaledTime < _nextFind)
            {
                return;
            }

            _nextFind = Time.unscaledTime + 1f;

            foreach (NetworkBridgeData data in FindObjectsByType<NetworkBridgeData>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (data.name == EtsSessionWire.ObjectName)
                {
                    if (data.IntCount < EtsSessionWire.Ints)
                    {
                        Debug.LogWarning("[SomniumEuroTruck] Network: session: '" + data.name + "' holds " + data.IntCount + " ints; it must be a NetworkBridgeData64.");
                        continue;
                    }

                    _session = data;
                }
            }
        }

        private void Awake()
        {
            // Replaced in Awake, cleared in OnDestroy (PLAN §3.2): statics outlive a world visit (persist §2.3).
            Instance = this;
            _onTeleported = OnTeleported;
        }

        /// <summary>
        /// Opens the game's map once the scene's objects exist (PLAN §5.5 step 2): the HQ stage draws the map from
        /// the mounted archives, and EtsMapIndex and the HQ catalogue wait for EtsMapViewer.Current.Map
        /// (EtsMapIndex.Run). Synchronous: the mount took 262 ms in the client (Player.log 2026-09-23 01:55,
        /// "Mounted 18 archives ... in 262 ms"), in the scene's first frame, as the old load's first frame did.
        /// </summary>
        private void Start()
        {
            EtsMapViewer viewer = MapViewer();

            if (viewer == null)
            {
                Debug.LogWarning("[SomniumEuroTruck] Network: session: the scene has no Ets Map Viewer; there is no map to choose from or arrive on.");
                return;
            }

            if (!viewer.IsOpen)
            {
                viewer.Open();
            }

            _mapOpened = viewer.IsOpen;
            Debug.Log("[SomniumEuroTruck] Network: session: the game's map is " + (_mapOpened ? "open" : "NOT open (the archives could not be mounted)") + " for the headquarters choice.");
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }

            if (_viewer != null && _focus != null && _viewer.StreamFocusOverride == _focus)
            {
                _viewer.StreamFocusOverride = null;
            }
        }
    }
}
