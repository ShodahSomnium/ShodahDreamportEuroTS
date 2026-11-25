using System;
using System.Collections;
using System.Collections.Generic;
using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
using TMPro;
using UnityEngine;

namespace SomniumSpace.Worlds.SomniumEuroTruck
{
    /// <summary>
    /// The game's opening: quick jobs. The whole map is read in the background and its road
    /// network indexed (EtsMapIndex); then offers are made from the depot nearest the truck - the
    /// cargo the company ships (its "out" list), to another company in another city that takes it
    /// (their "in" list), with the route the road graph finds and the pay the game's economy gives
    /// (EtsEconomy: the cargo's reward per unit and km over the game's own km, the quick-job
    /// coefficient and fixed part). Taking an offer stands the truck at the depot's trailer bay with
    /// a laden curtainsider coupled, draws the route, and pays out when the trailer is brought to a
    /// stop in the destination's bay - into the player's saved career (EtsProfileStore), once per
    /// job. Cities, companies, cargo, trailers, pay: all from the game's own definitions and map.
    ///
    /// Owner G (PLAN.md §6). Phase 1 (G1): the map read moved to EtsMapIndex unchanged; offers carry
    /// an id, the pay and XP come from EtsEconomy, a delivery is credited to EtsProfileStore, the
    /// run no longer ends after one delivery (DemoRun off), and a job starts on the depot's trailer
    /// bay as the game does (Bay, PLAN C23). With EtsFeatures.NewFlow off it still drives today's one
    /// scene truck (Truck) and still asks EtsNet.Claim before a job.
    ///
    /// Phase 2 (G2), the new flow (EtsFeatures.NewFlow): one EtsJobs on 'Ets Net' (EtsNet.Awake), bound to
    /// the player's own truck, EtsLocalTruck.Own, and rebound on OwnChanged - no own truck, no job (CanTake,
    /// PLAN §3.5); a truck that goes away takes its job, its start and its trailer with it. The home city is
    /// the session's chosen headquarters (EtsSession.Choice.City), kept in the career by the master who chose
    /// it. Offers are made when the own truck stands on the ground, after a delivery or an abandoned job,
    /// and when the parked truck is far from where they were made - still today's rule (the depot nearest
    /// the truck, three destinations 2.5-45 km away), now a slice per frame (OffersRun: at most ~1 ms of
    /// work a frame, one Dijkstra to every destination, EtsRoadGraph.Search) and among the destinations a
    /// road actually reaches: probe 2110 found 5 of the 72 HQ cities with no offer at all (cambridge,
    /// dover, liverpool, plymouth: the three picks all on the continent, which the demo's UK roads do not
    /// reach; liege: its depot's nearest node is a 4-node dead end) and a median 588 ms MakeOffers frame
    /// (.NET). A delivery pays the career (money, XP, the log) and reports the experience and level before
    /// and after it (DeliveryStats) for the Delivery page; driving the own truck adds distance and the
    /// game's free-roam XP (EtsProfileStore.AddDriving). Distances stay the game's km throughout
    /// (Offer.ShownKm, ToGameKm).
    ///
    /// Phase 3 (G3), several players: jobs stay per player (PLAN §1 row 17). Only what the own slot shows crosses
    /// the wire - Published (the cargo and destination while a job runs, EtsNet.SetJob/ClearJob; the trailer's
    /// identity while the own rig pulls it), and JobLine/CityLabel name other players' jobs for the tablet. A
    /// passenger in another player's truck may not take a job ("Get out of {name}'s truck first") and is never
    /// credited: no puppet is ever bound, and a delivery waits while the player rides elsewhere
    /// (EtsJobRules.TakeBlock, DeliveryCounts; probes 2280-2282).
    /// </summary>
    public class EtsJobs : MonoBehaviour
    {
        public sealed class Offer
        {
            public EtsMapCompany From;
            public EtsMapCompany To;

            /// <summary>
            /// Where the job starts: the trailer's place (a type-1 point, the tractor coupled ahead), or a truck's
            /// (10 parking, 14 garage) when the depot has no trailer point; else the depot's own point, as before.
            /// </summary>
            public Vector3 FromBay;
            public float FromYaw;

            /// <summary>The spawn point type FromBay is (EtsPrefabDescriptor.Spawn*); 0 for the depot's own point.</summary>
            public int FromBayType;

            /// <summary>
            /// The depot's own point (its company marker, type 13, else parking or garage, else its map node): where
            /// the route starts and what "nearest depot" measures, exactly as before G1. Not where a truck stands.
            /// </summary>
            public Vector3 FromDepot;

            public Vector3 ToBay;
            public float ToYaw;
            public string CargoUnit;
            public string CargoName;
            public float UnitMass;
            public float UnitVolume;
            public float RewardPerKmUnit;
            public int Units;

            /// <summary>Metres of road, as driven in this world (the route line, the route advisor).</summary>
            public float RouteLength;

            /// <summary>Euros, from EtsEconomy.QuickJobRevenue.</summary>
            public int Reward;

            public List<EtsRoadGraph.Step> Path;
            public string ToCompanyName;
            public string ToCityName;
            public string FromCompanyName;
            public string FromCityName;

            /// <summary>
            /// A fresh GUID ("N" form) per offer: EtsProfileStore.Credit pays a job id once only, so a delivery can
            /// never be paid twice (PLAN §7.G, [persist §5.4]).
            /// </summary>
            public string Id = "";

            /// <summary>
            /// 0 standard, 1 important, 2 urgent (economy_data delivery_window 400/250/90 min, coefficient 1.0/1.15/1.4).
            /// Always 0 until the game clock can hold a deadline (PLAN §3.11, Phase 2): a higher coefficient
            /// without a deadline would be pay for nothing. The player's 1.61 first job was urgency 0 too.
            /// </summary>
            public int Urgency;

            /// <summary>Delivery XP: one per game km (EtsEconomy.DeliveryXp, C16).</summary>
            public int Xp;

            /// <summary>The route in the game's km (map metres x 19, x 3 inside city areas: EtsEconomy.RouteGameKm), what pay and XP are counted on.</summary>
            public float GameKm;

            public float CargoMass => Units * UnitMass;

            /// <summary>
            /// The km the offer is shown with: the game's km, the ones the pay and XP are counted on. Showing map
            /// km beside game-km pay read "7 km, 2,032 EUR" on the first start offer (Phase 1 integration).
            /// </summary>
            public float ShownKm => GameKm > 0f ? GameKm : RouteLength / 1000f;

            /// <summary>
            /// Metres of this world's road as the game's km, by this route's own ratio, so a distance left counts
            /// down in the same km the offer was shown in (PLAN §12, the D2 display rule).
            /// </summary>
            public float ToGameKm(float metres) => RouteLength > 1f && GameKm > 0f ? metres * (GameKm / RouteLength) : metres / 1000f;

            public string Line => CargoName + " from " + FromCompanyName + " to " + ToCompanyName + ", " + ToCityName + " - " + ShownKm.ToString("0") + " km, " + Reward.ToString("N0") + " EUR";
        }

        public EtsTruckController Truck;
        public TMP_Text Hud;
        public string HomeCity = "nurnberg";

        /// <summary>Metres from the destination bay within which a stopped trailer counts as delivered.</summary>
        public float ArrivalRadius = 30f;

        public float TrailerVolume = 87f;

        public readonly List<Offer> Offers = new List<Offer>();
        public Offer Current;
        public bool MapIndexed;

        [Tooltip("Play it as the demo does: one delivery, then the game's own demo-ended screen. " +
                 "Off (the career keeps what is earned, PLAN §8 G1): keep taking jobs.")]
        public bool DemoRun = false;
        public string Status = "Reading the map ...";

        private EtsMapIndex _index;
        private EtsRoadGraph _graph;
        private EtsTrailer _trailer;
        private EtsRouteLine _route;
        private EtsSiiFile _cargo;
        private IReadOnlyList<EtsMapCompany> _companies = Array.Empty<EtsMapCompany>();
        private readonly List<string> _log = new List<string>();
        private float _stoppedTimer;
        private string _message;
        private float _messageUntil;
        private float _hudTimer;
        private int _sessionCompleted;
        private int _sessionEarnings;

        /// <summary>What a finished delivery is scored on.</summary>
        public sealed class DeliveryStats
        {
            public Offer Job;

            /// <summary>Metres driven in this world on the job (a jump of 50 m or more in a frame is not counted).</summary>
            public float DistanceMetres;

            public float Seconds;
            public float FuelLitres;
            public int Reward;

            /// <summary>The XP credited (Offer.Xp).</summary>
            public int Xp;

            /// <summary>The career balance after the credit (EtsProfileStore), or 2000 + this visit's pay without a store.</summary>
            public long Balance;

            /// <summary>
            /// The career's experience just before and just after this delivery (the Delivery page's XP bar,
            /// EtsPageDelivery.Record). While the career is still opening the credit is queued, and After is Before + Xp.
            /// </summary>
            public long ExperienceBefore;
            public long ExperienceAfter;

            /// <summary>EtsXpTable levels of those two (0 = no threshold passed yet); LevelAfter &gt; LevelBefore is a level-up.</summary>
            public int LevelBefore;
            public int LevelAfter;

            /// <summary>
            /// The driven distance in the game's km, by the job's own ratio (Offer.ToGameKm), the same km the offer and
            /// the route advisor showed. The Delivery page shows the route's GameKm (PLAN §12: the driven value waits
            /// for a comparison with the game's own advisor in the client).
            /// </summary>
            public float DrivenGameKm;

            /// <summary>Paid into the saved career now (false: queued until the career opens, or no store).</summary>
            public bool Credited;
        }

        /// <summary>Raised as a taken job begins moving the truck to the depot - the front end shows the loading screen.</summary>
        public event Action<Offer> JobStarting;

        /// <summary>Raised once the truck stands in the depot with the trailer coupled and the route drawn.</summary>
        public event Action<Offer> JobStarted;

        /// <summary>Raised when the trailer is brought to rest in the destination's bay.</summary>
        public event Action<DeliveryStats> Delivered;

        /// <summary>A job's depot never built, so it could not start; the truck is back where it was.</summary>
        public event Action<Offer> JobFailed;

        private float _jobStartTime;
        private float _jobDistance;
        private Vector3 _lastTruckPosition;
        private GameObject _marker;

        public bool HasJob => Current != null;
        public EtsTrailer Trailer => _trailer;

        /// <summary>The route drawn on the road for the current job, for the advisor's map and turn notes.</summary>
        public EtsRouteLine Route => _route;

        /// <summary>
        /// Loads delivered in this world. Once the career is open (EtsProfileStore) it is the career's count, over
        /// every visit; before that, this visit's. Read by the route advisor's job page.
        /// </summary>
        public int Completed => Career != null ? Career.Profile.JobsDelivered : _sessionCompleted;

        /// <summary>
        /// Euros earned in this world: the career's lifetime earnings once it is open, this visit's before. The front
        /// end's desktop shows Balance (EtsFrontEnd.cs:533), which equals 2000 + this - nothing spends
        /// money yet, so balance = the game's starting 2000 + everything earned.
        /// </summary>
        public int Earnings => Career != null ? (int)Math.Min(int.MaxValue, Career.Profile.LifetimeEarnings) : _sessionEarnings;

        /// <summary>The saved balance (EtsProfileStore.Money), or the game's starting 2000 plus this visit's pay without a store.</summary>
        public long Balance => Career != null ? Career.Money : EtsProfileCodec.StartingMoney + _sessionEarnings;

        private EtsMapViewer Viewer => EtsMapViewer.Current;

        // The open career, or null (the store not there yet, or still waiting for the account id).
        private static EtsProfileStore Career => EtsProfileStore.Current != null && EtsProfileStore.Current.IsReady && EtsProfileStore.Current.Profile != null ? EtsProfileStore.Current : null;

        // ---------------------------------------------------------------- Phase 2 (G2) state

        /// <summary>Work an offer run may do in one frame, ms (VR at 90 Hz has ~11 ms for everything).</summary>
        private const double OfferSliceMs = 1.0;

        /// <summary>Settled nodes between two looks at the clock in a search (about 0.05 ms on .NET; probe 2113).</summary>
        private const int SettlesPerCheck = 256;

        /// <summary>
        /// Quick jobs are local (the player, 2026-09-23: "it should only show quick jobs in the area I am in"): the area is
        /// the city whose area box holds the truck, else the city of the nearest depot within this many metres of it; a
        /// depot this near the truck also belongs to the area whatever its city.
        /// </summary>
        private const float AreaReach = 1500f;

        /// <summary>At most this many companies of the area give offers, nearest to the truck first (one depot per company).</summary>
        private const int MaxAreaCompanies = 6;

        /// <summary>Seconds between two looks at which area the parked truck is in (EtsJobs.AreaAt, no allocation).</summary>
        private const float AreaLookEvery = 1f;

        /// <summary>Metres the parked truck must have moved from where the offers were made before the area is looked at again.</summary>
        private const float AreaMoveMin = 50f;

        /// <summary>How far (metres) a route may start from its depot when the nearest node leads nowhere (the retry, Liege).</summary>
        private const float StartNodeReach = 1000f;

        /// <summary>The own truck (new flow): EtsLocalTruck.Own, null before one is spawned.</summary>
        private EtsTruckRig _rig;
        private bool _subscribed;

        private Coroutine _offerRun;
        private bool _offering;
        private bool _offersWanted;
        private bool _offersMade;
        private Vector3 _offersMadeAt;
        private float _parkedFor;
        private EtsRoadGraph.Search _search;

        // The area (a city token, 0 = none) the offers were made for, and when the parked truck's area is next looked at.
        private ulong _offersArea;
        private float _nextAreaLook;

        // Each depot's own point (DepotSide) in the game's x/z, read once a visit (Bay is not free); NaN x = no point.
        private readonly Dictionary<EtsMapCompany, Vector2> _depotGame = new Dictionary<EtsMapCompany, Vector2>();

        // What each company ships in a curtainsider and takes, by company name: the folder listings, read once a visit.
        private readonly Dictionary<string, List<string>> _shipsCurtain = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        private readonly Dictionary<string, List<string>> _takes = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        /// <summary>The trailer of the last delivery, left in its bay until the next job starts (new flow).</summary>
        private EtsTrailer _delivered;

        // Driving not yet handed to the career (EtsProfileStore.AddDriving takes whole metres and XP).
        private Vector3 _drivePosition;
        private bool _haveDrivePosition;
        private double _drivenGameMetres;
        private double _drivenXp;
        private float _flushAt;

        private float _nextSessionLook;
        private bool _choseHq;
        private string _savedHq = "";

        // The session's HQ city token last decoded (LookAtSession runs twice a second; a decode is a new string).
        private ulong _homeToken;

        /// <summary>
        /// Offers are being made (a run over several frames); Offers is empty until it ends. The Quick Job page may show
        /// the game's busy state meanwhile.
        /// </summary>
        public bool OffersPending => _offering;

        /// <summary>0..1 while a taken job moves the truck to its depot (the job's loading screen): 0.1 moved, 0.1-0.8 the yard building, 0.9 set down, 1 started.</summary>
        public float StartProgress { get; private set; }

        /// <summary>The local player is driving the own truck faster than 5 km/h (new flow). The career holds its minute autosave meanwhile (a save is 8-16 ms under Mono).</summary>
        public bool DrivingFast => _rig != null && Truck != null && EtsLocalTruck.Occupied == _rig && Truck.SpeedKmh > 5f;

        // ---------------------------------------------------------------- the integrated plan's contract

        /// <summary>The per-player jobs of this visit (PLAN.md §3.1, §7.G). Replaced in Awake, cleared in OnDestroy.</summary>
        public static EtsJobs Instance { get; private set; }

        /// <summary>The jobs on this object, added if missing (PLAN §3.2). Unity's null test, not ??.</summary>
        public static EtsJobs Ensure(GameObject host)
        {
            if (host == null)
            {
                return null;
            }

            EtsJobs jobs = host.GetComponent<EtsJobs>();

            if (jobs == null)
            {
                jobs = host.AddComponent<EtsJobs>();
            }

            return jobs;
        }

        /// <summary>
        /// Whether a job may be taken now, with the reason when not (PLAN §3.5): one job at a time (none running or
        /// starting), the map read, a truck on the road; with the new flow that truck is the player's own, placed, and
        /// the player is not sitting in someone else's. Today's Take does not ask it (the old front end hides the
        /// offers instead); the tablet's Quick Job page asks it before Take, and the new flow's Take asks it again.
        /// </summary>
        public bool CanTake(out string reason)
        {
            // The rule is EtsJobRules.TakeBlock (pure, probe 2280); the words are PLAN §3.5's, not the game's locale. In
            // Phase 3 a passenger sits in another player's puppet (PLAN §5.5 "EtsLocalTruck.Occupied = the puppet"), so
            // "seated, and not in the own truck" is a passenger whichever way they got in (a click on the truck, or the
            // tablet's Convoys "Ride along").
            EtsTruckRig own = EtsLocalTruck.Own;
            EtsTruckRig seat = EtsLocalTruck.Occupied;
            bool newFlow = EtsFeatures.NewFlow;
            EtsTakeBlock block = EtsJobRules.TakeBlock(Current != null, Relocating, MapIndexed, newFlow,
                                                       newFlow && PassengerIn(seat, own),
                                                       own != null && own.Controller != null && own.Controller.Placed,
                                                       Truck != null && Truck.Placed);

            // The one reason that names someone is built once per rig and owner (GetOutReason); the others are literals.
            reason = block == EtsTakeBlock.Passenger ? GetOutReason(seat) : EtsJobRules.TakeReason(block, null);
            return block == EtsTakeBlock.None;
        }

        /// <summary>
        /// Seated in a truck that is not the own one (EtsJobRules.SeatedInOthersTruck): Unity's null test for a rig that
        /// may have been despawned under the player, then by reference.
        /// </summary>
        private static bool PassengerIn(EtsTruckRig seat, EtsTruckRig own)
        {
            return EtsJobRules.SeatedInOthersTruck(seat != null, ReferenceEquals(seat, own));
        }

        /// <summary>
        /// The local player rides in another player's truck now (PLAN §3.5): no job may be taken, and none of theirs is
        /// delivered while they ride (EtsJobRules.DeliveryCounts).
        /// </summary>
        public bool RidingAsPassenger => EtsFeatures.NewFlow && PassengerIn(EtsLocalTruck.Occupied, EtsLocalTruck.Own);

        // The "get out" reason for the rig last asked about. CanTake runs every frame while the tablet's Quick Job page is
        // open (EtsPageQuickJob.Tick asks it to grey Take Job), and EtsNet.NameOf walks the Bridge's player list through
        // an enumerator: a new string and an enumerator a frame for as long as a passenger looks at that page.
        private EtsTruckRig _getOutOf;
        private int _getOutOwner = int.MinValue;
        private string _getOutReason = "";

        /// <summary>"Get out of {name}'s truck first" (PLAN §3.5), built once per rig and owner; "that truck" when the owner has no name.</summary>
        private string GetOutReason(EtsTruckRig seat)
        {
            int owner = seat.OwnerPlayerId;

            if (!ReferenceEquals(seat, _getOutOf) || owner != _getOutOwner)
            {
                _getOutOf = seat;
                _getOutOwner = owner;
                string name = owner >= 0 ? EtsNet.NameOf(owner) : null;
                _getOutReason = string.IsNullOrEmpty(name) ? "Get out of that truck first" : "Get out of " + name + "'s truck first"; // not in the locale (PLAN §3.5)
            }

            return _getOutReason;
        }

        private void Awake()
        {
            // The new flow has one EtsJobs, on 'Ets Net' (EtsNet.Awake). Another one (an old-flow wiring left in a new-flow
            // path, e.g. EtsModelViewer.SetUpDriving at EtsModelViewer.cs:315) would take Instance from it and drive
            // nothing: it stands aside instead, and says so once.
            if (EtsFeatures.NewFlow && Instance != null && Instance != this && EtsNet.Instance != null && Instance.gameObject == EtsNet.Instance.gameObject)
            {
                Debug.LogWarning("[SomniumEuroTruck] Jobs: a second EtsJobs on '" + name + "' is switched off - the new flow's is on 'Ets Net'.");
                enabled = false;
                return;
            }

            Instance = this;
        }

        private void OnDestroy()
        {
            if (_index != null)
            {
                _index.Indexed -= OnIndexed;
            }

            if (_subscribed)
            {
                _subscribed = false;
                EtsLocalTruck.OwnChanged -= OnOwnChanged;
            }

            if (Instance == this)
            {
                Instance = null;
            }
        }

        private void Start()
        {
            // The career and the map index live on 'Ets Net' (PLAN §5.1), the one scene object that always exists;
            // EtsNet.Awake adds them there itself once EtsFeatures.NewFlow is on. Until then they are added here, so
            // money is kept from Phase 1 on, and the index is begun now: the whole-map read starts on the same frame
            // EtsJobs.IndexMap used to start it.
            GameObject home = EtsNet.Instance != null ? EtsNet.Instance.gameObject : gameObject;
            EtsProfileStore.Ensure(home);
            _index = EtsMapIndex.Instance != null ? EtsMapIndex.Instance : EtsMapIndex.Ensure(home);

            if (_index.Ready)
            {
                OnIndexed();
            }
            else
            {
                _index.Indexed += OnIndexed;
                _index.Begin();
            }

            // The new flow: bound to the player's own truck from now on (PLAN §3.1 "EtsJobs (G) rebinds on those
            // events"), starting with whatever EtsLocalTruck already holds.
            if (EtsFeatures.NewFlow && enabled && !_subscribed)
            {
                _subscribed = true;
                EtsLocalTruck.OwnChanged += OnOwnChanged;
                Bind(EtsLocalTruck.Own);
            }
        }

        /// <summary>The map is read (EtsMapIndex): take its graph, depots and cargo, read the economy, make offers.</summary>
        private void OnIndexed()
        {
            _index.Indexed -= OnIndexed;
            _graph = _index.Graph;
            _companies = _index.Companies;
            _cargo = _index.Cargo;
            EtsEconomy.Load(EtsEngine.Files);
            MapIndexed = true;
            Status = _index.Status;
            MakeOffers();
        }

        // ------------------------------------------------------------------ the own truck (new flow)

        private void OnOwnChanged(EtsTruckRig rig)
        {
            Bind(rig);
        }

        /// <summary>
        /// Drives jobs with this rig from now on (null: none). Whatever belonged to the truck before goes with it: a job
        /// starting is reported failed (so the loading screen comes down), a running job ends, the coupled trailer and
        /// the last delivered one are removed, offers are dropped; the new truck gets offers once it stands.
        /// </summary>
        private void Bind(EtsTruckRig rig)
        {
            // Jobs stay per player (PLAN §1 row 17): they drive only the player's own truck, never another player's puppet
            // (EtsTruckRig.IsPuppet, set by the factory for EtsNetVehicle's copies). EtsLocalTruck.Own is only ever the own
            // rig (EtsNet.SpawnOwnTruck); should a puppet be announced as Own, the jobs keep what they have rather than pay
            // this client for someone else's driving. By IsPuppet, not OwnerPlayerId: A may give the own rig its slot too.
            if (rig != null && rig.IsPuppet)
            {
                Debug.LogWarning("[SomniumEuroTruck] Jobs: '" + rig.name + "' is another player's truck (a puppet) - not bound; jobs stay with the own truck.");
                return;
            }

            EtsTruckController controller = rig != null ? rig.Controller : null;

            if (rig == _rig && controller == Truck)
            {
                return;
            }

            EtsTruckRig was = _rig;
            bool hadJob = Current != null || _startJob != null;
            CutJob(was);
            _rig = rig;
            Truck = controller;
            Hud = controller != null ? controller.Hud : null;
            Offers.Clear();
            _offersMade = false;
            _offersWanted = controller != null;
            _haveDrivePosition = false;
            Status = controller != null ? "Looking for jobs near your truck ..." : "Choose your truck first (Company)";
            Debug.Log("[SomniumEuroTruck] Jobs: bound to " + (rig != null ? "own truck '" + rig.name + "'" : "no truck") +
                      (hadJob ? " - the job of the last truck ended with it" : "") + ".");

            if (hadJob && controller == null)
            {
                Say("Your truck is gone, and its job with it.");
            }
        }

        /// <summary>Ends everything of the job and trailers of a truck that is going away (Bind), and stops a run of offers.</summary>
        private void CutJob(EtsTruckRig was)
        {
            StopOffers();
            Offer cut = null;

            if (_startJob != null)
            {
                StopCoroutine(_startJob);
                _startJob = null;
                cut = _starting;
                _starting = null;
            }

            bool hadJob = Current != null;
            Current = null;
            if (_route != null) _route.Hide();
            RemoveMarker();

            if (was != null && was.Trailer != null && was.Trailer == _trailer)
            {
                was.Trailer = null;
            }

            RemoveTrailer(ref _trailer);
            RemoveTrailer(ref _delivered);

            if (hadJob || cut != null || _jobShown)
            {
                UnpublishJob();
            }

            if (cut != null)
            {
                StartProgress = 0f;
                JobFailed?.Invoke(cut);
            }
        }

        private static void RemoveTrailer(ref EtsTrailer trailer)
        {
            if (trailer != null)
            {
                trailer.Detach();
                Destroy(trailer.gameObject);
            }

            trailer = null;
        }

        // ------------------------------------------------------------------ the session's headquarters (new flow)

        /// <summary>
        /// Twice a second: the home city follows the session's HQ choice (EtsSession.Choice.City, the city token decoded
        /// as the map's companies name their city, "szczecin"), and the master who chose it keeps it in the career
        /// (EtsProfileStore.SetHqCity, [persist §5.7]: only the chooser's own client; the HQ map preselects it next time).
        /// </summary>
        private void LookAtSession()
        {
            if (Time.unscaledTime < _nextSessionLook)
            {
                return;
            }

            _nextSessionLook = Time.unscaledTime + 0.5f;
            EtsSession session = EtsSession.Instance;

            if (session == null)
            {
                return;
            }

            // The chooser: master (ChoosingHq, or Offline - its own master) while nothing was chosen yet. A player who only
            // becomes master later (a hand-over) did not choose, and does not overwrite their own preselect.
            if (!session.HasChoice && session.IsMaster)
            {
                _choseHq = true;
            }

            if (!session.HasChoice || session.Choice.City == 0)
            {
                return;
            }

            if (session.Choice.City != _homeToken)
            {
                _homeToken = session.Choice.City;
                string city = EtsToken.Decode(_homeToken);

                if (!string.IsNullOrEmpty(city) && city != HomeCity)
                {
                    HomeCity = city;
                    Debug.Log("[SomniumEuroTruck] Jobs: home city '" + city + "' (the session's headquarters).");
                }
            }

            EtsProfileStore store = Career;

            if (_choseHq && store != null && _savedHq != HomeCity)
            {
                _savedHq = HomeCity;
                store.SetHqCity(HomeCity);
            }
        }

        // ------------------------------------------------------------------ definitions

        private static string Pretty(string localized, string fallback)
        {
            if (string.IsNullOrEmpty(localized)) return fallback;
            string s = localized.Replace("@@", "");
            if (s.StartsWith("cn_", StringComparison.Ordinal)) s = s.Substring(3);
            s = s.Replace('_', ' ');
            return s.Length > 1 ? char.ToUpper(s[0]) + s.Substring(1) : s;
        }

        private string CompanyName(EtsMapCompany company)
        {
            EtsSiiUnit unit = Viewer != null && Viewer.Map != null ? Viewer.Map.Company(company.CompanyToken) : null;
            string name = unit != null ? unit.GetString("name", null) : null;
            return string.IsNullOrEmpty(name) ? company.Name : name;
        }

        private string CityName(ulong cityToken)
        {
            EtsSiiUnit unit = Viewer != null && Viewer.Map != null ? Viewer.Map.City(cityToken) : null;
            string name = unit != null ? unit.GetString("city_name", null) : null;
            return string.IsNullOrEmpty(name) ? EtsToken.Decode(cityToken) : name;
        }

        /// <summary>Cargo units a company ships or takes: the file names of /def/company/&lt;name&gt;/out or /in.</summary>
        private List<string> CompanyCargo(EtsMapCompany company, string direction)
        {
            var result = new List<string>();

            if (EtsEngine.Files.TryList("/def/company/" + company.Name + "/" + direction, out List<string> files, out _))
            {
                foreach (string file in files)
                {
                    if (file.EndsWith(".sii", StringComparison.Ordinal)) result.Add("cargo." + file.Substring(0, file.Length - 4));
                }
            }

            return result;
        }

        /// <summary>What a company ships that goes in a curtainsider (its "out" list, filtered), read once per company a visit. Do not change the list.</summary>
        private List<string> ShipsInCurtainsider(EtsMapCompany company)
        {
            string key = company.Name;

            if (!_shipsCurtain.TryGetValue(key, out List<string> list))
            {
                list = CompanyCargo(company, "out");
                list.RemoveAll(u => !CurtainsideCargo(u, out _));
                _shipsCurtain[key] = list;
            }

            return list;
        }

        /// <summary>What a company takes (its "in" list), read once per company a visit. Do not change the list.</summary>
        private List<string> Takes(EtsMapCompany company)
        {
            string key = company.Name;

            if (!_takes.TryGetValue(key, out List<string> list))
            {
                list = CompanyCargo(company, "in");
                _takes[key] = list;
            }

            return list;
        }

        private bool CurtainsideCargo(string cargoUnit, out EtsSiiUnit cargo)
        {
            cargo = _cargo != null ? _cargo.Find(cargoUnit) : null;
            return cargo != null && cargo.GetArray("body_types").Contains("curtainside");
        }

        // A job starts where the game starts one: the depot's trailer bay, the tractor coupled ahead of it. Only a
        // depot without a trailer point falls back to a truck's place, parking then garage. No truck is stood on type
        // 13, the company's map marker (PLAN C23; EtsModelViewer.cs:430-433). Probe 1953 (S/phase1/G/probe/out1953.txt),
        // the TransiNet job the player's 1.61 save starts (game.log "[actor] Initialized at [14994.0;46.8;-15235.2]"):
        // the tractor this order stands 0.03 m from the game's; type 13 (before G1) 9.34 m; parking first (the order
        // C23 lists, 14 -> 10 -> 1) 38.85 m, on the depot's parking bay. It is the verified start's own order
        // (EtsModelViewer.cs:435-443). Two more logged job starts (game.log.txt of 2026-09-22 15:57, lines 1482 and 1484
        // dfh.berlin, 2606 and 2608 quarry.hannover; checker probe 1960, S/phase1/GX/probe/out1960.txt) also stand the
        // tractor ahead of a type-1 point (0.09 m, and on the axis at a shorter trailer's 9.24 m), but on the prefab's
        // SECOND type-1 point, while this takes the first (49.96 m and 6.87 m off there): trailer points first is
        // confirmed 3 of 3, which one the game picks is undetermined. Demo map (probes 1950, 1964): 323 of the 365
        // depots start on a trailer bay; 42 keep today's point - 27 with none of the three types, 7 whose type-1 point
        // cannot be placed (the prefab's first node is not on the demo map: Munchen, Trieste, Zurich, Dijon), 7 without
        // spawn points, 1 without a prefab.
        private static readonly int[] StartSide = { EtsPrefabDescriptor.SpawnTrailer, EtsPrefabDescriptor.SpawnParking, EtsPrefabDescriptor.SpawnGarage };

        // Where a depot IS, for "nearest depot", the offer distances and the route's first node: today's truck-side
        // point, unchanged. Not the start bay: probe 1955 (out1955.txt) - the road node nearest TransiNet's trailer bay
        // is the depot prefab's own inner node, from which 2 nodes can be reached, and ITCC's is a 7-node yard loop;
        // routed from there both logged jobs find no way to Berlin, while from the marker's node 59,751 nodes are
        // reachable and the routes are today's 9.11 and 8.93 km.
        private static readonly int[] DepotSide = { EtsPrefabDescriptor.SpawnCompany, EtsPrefabDescriptor.SpawnParking, EtsPrefabDescriptor.SpawnGarage };
        private static readonly int[] DeliverySide = { EtsPrefabDescriptor.SpawnUnloadEasy, EtsPrefabDescriptor.SpawnUnloadMedium, EtsPrefabDescriptor.SpawnUnloadHard, EtsPrefabDescriptor.SpawnTrailer };

        /// <summary>
        /// A point at a depot, as the game marks it: a spawn point of the first wanted type in the depot prefab's
        /// descriptor - the job's start bay (StartSide), the depot itself (DepotSide), or the unloading point a
        /// delivery is dropped on (DeliverySide: 2, 19 or 20 - easy, medium, hard). Those points sit at height zero in
        /// the prefab's own frame, on its paved yard, so put through the prefab's transform they are exactly where the
        /// trailer should end up. When a depot's prefab has none of them, its map node stands in (type 0).
        /// </summary>
        private bool Bay(EtsMapCompany company, int[] wanted, out Vector3 world, out float yaw, out EtsMapNode node, out int type)
        {
            world = Vector3.zero;
            yaw = 0f;
            node = null;
            type = 0;
            EtsMap map = Viewer != null ? Viewer.Map : null;
            if (map == null) return false;

            // The index's dictionary gives the same prefab as map.FindPrefab (probe 1954), without walking every prefab.
            EtsMapPrefab prefab = company.PrefabUid == 0 ? null : _index != null ? _index.Prefab(company.PrefabUid) : map.FindPrefab(company.PrefabUid);

            if (prefab != null)
            {
                if (EtsMapBuilder.TryPrefabSpawn(map, prefab, Viewer.CenterGame, wanted, out world, out yaw, out type))
                {
                    node = map.FindNode(company.NodeUid);
                    return true;
                }
            }

            type = 0;
            node = company.SpawnPoints.Length > 0 ? map.FindNode(company.SpawnPoints[0]) : null;
            if (node == null) node = map.FindNode(company.NodeUid);
            if (node == null) return false;

            world = EtsMapBuilder.ToWorld(node.Position, Viewer.CenterGame);
            Vector3 forward = EtsMapBuilder.ToWorldDirection(node.Forward());
            yaw = forward.sqrMagnitude > 0.01f ? Quaternion.LookRotation(forward, Vector3.up).eulerAngles.y : 0f;
            return true;
        }

        // ------------------------------------------------------------------ offers

        /// <summary>
        /// The quick jobs of the area the truck is in: a few deliveries from each of its companies (OffersRun). Starts a
        /// run over several frames; Offers is emptied now and grows by a company's offers at a time. While a run is going
        /// this does nothing (the old menus ask again every 2 s while there are none, EtsMenu.cs:435, EtsFrontEnd.cs:600).
        /// </summary>
        public void MakeOffers()
        {
            if (_offering)
            {
                return;
            }

            Offers.Clear();

            if (!MapIndexed || Truck == null || Viewer == null || Viewer.Map == null || _graph == null)
            {
                if (EtsFeatures.NewFlow && MapIndexed && Truck == null)
                {
                    Status = "Choose your truck first (Company)"; // not in the locale (PLAN §3.5)
                }

                return;
            }

            _offersWanted = false;
            _offering = true;
            Coroutine run = StartCoroutine(OffersRun());

            // A run that ended inside StartCoroutine (no depot, no destination: before its first yield) has already said so.
            _offerRun = _offering ? run : null;
        }

        private void StopOffers()
        {
            if (_offerRun != null)
            {
                StopCoroutine(_offerRun);
                _offerRun = null;
            }

            _offering = false;
        }

        /// <summary>
        /// Quick jobs of the area the truck is in (the player, 2026-09-23: "it should only show quick jobs in the area I am
        /// in"), a slice per frame (at most ~OfferSliceMs of work, then a yield):
        /// 1. the area (AreaAt): the city whose area box holds the truck, else the city of the nearest depot within
        ///    AreaReach; neither - no offers ("drive into a city");
        /// 2. its companies: every depot of that city, and any depot within AreaReach of the truck, that ships something
        ///    that goes in a curtainsider - one depot per company, nearest the truck first, at most MaxAreaCompanies;
        /// 3. for each of them, today's rule for one depot: every depot in another city 2.5-45 km away (straight) that takes
        ///    one of those, sorted by that distance; one search from the road node nearest the depot to the node nearest
        ///    each destination's bay (EtsRoadGraph.Search), stepping past a dead-end start node up to three times (Liege,
        ///    probe 2112); then the nearest, middle and farthest reached (the nearest and farthest when the area has more
        ///    than two companies). A company's offers are added to Offers as soon as they are made, so the first show as
        ///    quickly as the single depot's did; the list only grows during a run (the tablet's chosen row stays valid).
        /// A job starts at its pickup company's bay (StartJob), which is in this area: a short hop, never another city
        /// (Take checks the truck is still in the offers' area). Pay and km are counted as before (Complete).
        /// </summary>
        private IEnumerator OffersRun()
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            float startedAt = Time.realtimeSinceStartup;
            int frames = 1;
            Vector3 truck = Truck.transform.position;
            Vector3 centre = Viewer.CenterGame;
            float truckX = truck.x + centre.x, truckZ = centre.z - truck.z;
            Status = "Looking for quick jobs near your truck ...";

            // 1. Every depot's own point (read once a visit, a slice at a time), then the area.
            for (int c = 0; c < _companies.Count; c++)
            {
                if (clock.Elapsed.TotalMilliseconds > OfferSliceMs)
                {
                    yield return null;
                    frames++;
                    clock.Restart();
                    if (!CanStillOffer()) { EndOffers(); yield break; }
                }

                DepotGame(_companies[c], out _);
            }

            ulong area = AreaAt(truckX, truckZ, out bool insideCity, out float nearestDepot);
            _offersArea = area;
            string how = area == 0UL ? "no city area here and no depot within " + AreaReach.ToString("0") + " m"
                : insideCity ? "inside the city's area" : "nearest depot " + nearestDepot.ToString("0") + " m away";

            if (area == 0UL)
            {
                Status = "No companies near your truck. Drive into a city and stop there for its quick jobs.";
                FinishOffers("", startedAt, frames, how);
                yield break;
            }

            string areaName = CityName(area);

            // 2. The area's companies that ship curtainsider cargo: one depot per company, nearest the truck first.
            var sources = new List<KeyValuePair<float, EtsMapCompany>>();

            for (int c = 0; c < _companies.Count; c++)
            {
                if (clock.Elapsed.TotalMilliseconds > OfferSliceMs)
                {
                    yield return null;
                    frames++;
                    clock.Restart();
                    if (!CanStillOffer()) { EndOffers(); yield break; }
                }

                EtsMapCompany company = _companies[c];
                if (!DepotGame(company, out Vector2 at)) continue;
                float dx = at.x - truckX, dz = at.y - truckZ;
                float away = Mathf.Sqrt(dx * dx + dz * dz);
                if (company.CityToken != area && away > AreaReach) continue;
                if (ShipsInCurtainsider(company).Count == 0) continue;
                sources.Add(new KeyValuePair<float, EtsMapCompany>(away, company));
            }

            sources.Sort((a, b) => a.Key.CompareTo(b.Key));
            var areaCompanies = new List<KeyValuePair<float, EtsMapCompany>>(MaxAreaCompanies);
            var brands = new HashSet<ulong>();

            foreach (KeyValuePair<float, EtsMapCompany> source in sources)
            {
                if (areaCompanies.Count >= MaxAreaCompanies) break;
                if (brands.Add(source.Value.CompanyToken)) areaCompanies.Add(source);
            }

            var listed = new System.Text.StringBuilder();

            foreach (KeyValuePair<float, EtsMapCompany> source in areaCompanies)
            {
                if (listed.Length > 0) listed.Append(", ");
                listed.Append(source.Value.Name).Append('.').Append(source.Value.CityId).Append(' ').Append(source.Key.ToString("0")).Append(" m");
            }

            Debug.Log("[SomniumEuroTruck] Jobs: quick jobs for the area '" + EtsToken.Decode(area) + "' (" + areaName + "; " + how + "; truck at game " + truckX.ToString("0") + ", " +
                      truckZ.ToString("0") + "): " + areaCompanies.Count + " compan" + (areaCompanies.Count == 1 ? "y" : "ies") + " of " + sources.Count +
                      " curtainsider depot(s) - " + (listed.Length > 0 ? listed.ToString() : "none") + ".");

            if (areaCompanies.Count == 0)
            {
                Status = "No company in " + areaName + " ships cargo for a curtainsider.";
                FinishOffers(EtsToken.Decode(area), startedAt, frames, how + "; no company ships curtainsider cargo");
                yield break;
            }

            // 3. Each company's offers, today's rule for one depot, added as soon as they are made.
            int perCompany = areaCompanies.Count <= 2 ? 3 : 2;
            var offeredBy = new List<string>(areaCompanies.Count);
            var deliveryBays = new Dictionary<EtsMapCompany, Vector4>(); // this run's delivery bays: x, y, z, yaw (NaN x = none)
            int destinationsTotal = 0, reachedTotal = 0;

            if (_search == null)
            {
                _search = new EtsRoadGraph.Search(_graph);
            }

            for (int s = 0; s < areaCompanies.Count; s++)
            {
                EtsMapCompany from = areaCompanies[s].Value;
                float fromAway = areaCompanies[s].Key;
                List<string> shipped = ShipsInCurtainsider(from);
                if (!Bay(from, DepotSide, out Vector3 fromDepot, out float fromDepotYaw, out _, out _)) continue;

                // The job's start bay at that depot (StartSide); a depot with none of those keeps its own point.
                if (!Bay(from, StartSide, out Vector3 fromBay, out float fromYaw, out _, out int fromType) || fromType == 0)
                {
                    fromBay = fromDepot;
                    fromYaw = fromDepotYaw;
                    fromType = 0;
                }

                // Every depot in another city that takes something this one ships.
                var candidates = new List<KeyValuePair<float, Offer>>();

                for (int c = 0; c < _companies.Count; c++)
                {
                    if (clock.Elapsed.TotalMilliseconds > OfferSliceMs)
                    {
                        yield return null;
                        frames++;
                        clock.Restart();
                        if (!CanStillOffer()) { EndOffers(); yield break; }
                    }

                    EtsMapCompany company = _companies[c];
                    if (company == from || company.CityToken == from.CityToken) continue;

                    if (!deliveryBays.TryGetValue(company, out Vector4 known))
                    {
                        known = Bay(company, DeliverySide, out Vector3 found, out float foundYaw, out _, out _) ? new Vector4(found.x, found.y, found.z, foundYaw) : new Vector4(float.NaN, 0f, 0f, 0f);
                        deliveryBays[company] = known;
                    }

                    if (float.IsNaN(known.x)) continue;
                    var bay = new Vector3(known.x, known.y, known.z);
                    float straight = Vector3.Distance(fromDepot, bay);
                    if (straight < 2500f || straight > 45000f) continue;

                    List<string> incoming = Takes(company);
                    string cargoUnit = null;

                    foreach (string u in shipped)
                    {
                        if (incoming.Contains(u)) { cargoUnit = u; break; }
                    }

                    if (cargoUnit == null) continue;
                    candidates.Add(new KeyValuePair<float, Offer>(straight, new Offer { From = from, To = company, FromBay = fromBay, FromYaw = fromYaw, FromBayType = fromType, FromDepot = fromDepot, ToBay = bay, ToYaw = known.w, CargoUnit = cargoUnit }));
                }

                if (candidates.Count == 0)
                {
                    Debug.Log("[SomniumEuroTruck] Jobs:   " + from.Name + "." + from.CityId + " (" + fromAway.ToString("0") + " m from the truck): no depot 2.5-45 km away takes what it ships.");
                    continue;
                }

                candidates.Sort((a, b) => a.Key.CompareTo(b.Key));

                // The road nodes: nearest the depot's own point (as before G1: the node nearest the start bay can be a yard's
                // dead end, probe 1955) and nearest each destination's bay.
                centre = Viewer.CenterGame;
                float fromX = fromDepot.x + centre.x, fromZ = centre.z - fromDepot.z;
                EtsMapNode start = _graph.NearestNode(fromX, fromZ, out float startDistance);
                var ends = new EtsMapNode[candidates.Count];
                var endDistances = new float[candidates.Count];

                for (int i = 0; i < candidates.Count; i++)
                {
                    if (clock.Elapsed.TotalMilliseconds > OfferSliceMs)
                    {
                        yield return null;
                        frames++;
                        clock.Restart();
                        if (!CanStillOffer()) { EndOffers(); yield break; }
                    }

                    Offer o = candidates[i].Value;
                    ends[i] = _graph.NearestNode(o.ToBay.x + centre.x, centre.z - o.ToBay.z, out endDistances[i]);
                }

                int reachable = 0;
                HashSet<ulong> deadEnd = null;

                for (int attempt = 0; start != null; attempt++)
                {
                    _search.Begin(start.Uid);

                    for (int i = 0; i < ends.Length; i++)
                    {
                        if (ends[i] != null) _search.Target(ends[i].Uid);
                    }

                    while (!_search.Run(SettlesPerCheck))
                    {
                        if (clock.Elapsed.TotalMilliseconds > OfferSliceMs)
                        {
                            yield return null;
                            frames++;
                            clock.Restart();
                            if (!CanStillOffer()) { EndOffers(); yield break; }
                        }
                    }

                    reachable = 0;

                    for (int i = 0; i < ends.Length; i++)
                    {
                        if (ends[i] != null && _search.Reached(ends[i].Uid, out _)) reachable++;
                    }

                    if (reachable > 0 || attempt >= 3)
                    {
                        break;
                    }

                    // Nothing reachable from this node: step past the dead end it belongs to.
                    if (deadEnd == null) deadEnd = new HashSet<ulong>();
                    _search.CollectSettled(deadEnd);
                    EtsMapNode next = _graph.NearestNodeExcept(fromX, fromZ, deadEnd, out float nextDistance);
                    Debug.Log("[SomniumEuroTruck] Jobs: the road node nearest " + from.Name + "." + from.CityId + " (" + startDistance.ToString("0") + " m) reaches " +
                              _search.Settled + " nodes and no destination; " + (next != null && nextDistance <= StartNodeReach ? "trying the next one, " + nextDistance.ToString("0") + " m away." : "no other node within " + StartNodeReach.ToString("0") + " m."));

                    if (next == null || nextDistance > StartNodeReach)
                    {
                        break;
                    }

                    start = next;
                    startDistance = nextDistance;
                    yield return null;
                    frames++;
                    clock.Restart();
                    if (!CanStillOffer()) { EndOffers(); yield break; }
                }

                // Nearest, middle and farthest of those reached (nearest and farthest with many companies), in the sorted order.
                var reached = new List<int>(reachable);

                for (int i = 0; i < ends.Length && start != null; i++)
                {
                    if (ends[i] != null && _search.Reached(ends[i].Uid, out _)) reached.Add(i);
                }

                destinationsTotal += candidates.Count;
                reachedTotal += reached.Count;
                int settled = start != null ? _search.Settled : 0;
                var picks = new List<Offer>(perCompany);

                if (reached.Count > 0)
                {
                    int[] indices = perCompany >= 3 ? new[] { 0, reached.Count / 2, reached.Count - 1 } : new[] { 0, reached.Count - 1 };

                    foreach (int n in indices)
                    {
                        int i = reached[Mathf.Clamp(n, 0, reached.Count - 1)];
                        Offer offer = candidates[i].Value;
                        if (picks.Contains(offer)) continue;

                        if (Complete(offer, start, startDistance, fromX, fromZ, ends[i], endDistances[i]))
                        {
                            offer.Id = Guid.NewGuid().ToString("N");
                            picks.Add(offer);
                            Offers.Add(offer);
                        }

                        // One offer's route, names and km a frame.
                        yield return null;
                        frames++;
                        clock.Restart();
                        if (!CanStillOffer()) { EndOffers(); yield break; }
                    }
                }

                Debug.Log("[SomniumEuroTruck] Jobs:   " + from.Name + "." + from.CityId + " (" + CompanyName(from) + ", " + fromAway.ToString("0") + " m from the truck): " + picks.Count +
                          " offer(s); " + reached.Count + " of " + candidates.Count + " destinations by road, " + settled + " nodes searched.");

                if (picks.Count > 0)
                {
                    offeredBy.Add(CompanyName(from));
                    Status = "Quick jobs in " + areaName + ": " + Offers.Count + " so far ...";
                }
            }

            Status = Offers.Count > 0
                ? Offers.Count + " quick job" + (Offers.Count == 1 ? "" : "s") + " in " + areaName + " from " + string.Join(", ", offeredBy) + "."
                : "No road leads from " + areaName + "'s companies to a depot that takes their cargo.";
            FinishOffers(EtsToken.Decode(area), startedAt, frames, how + "; from " + (offeredBy.Count > 0 ? string.Join(", ", offeredBy) : "no company") + "; " + reachedTotal + " of " +
                         destinationsTotal + " destinations by road");
        }

        /// <summary>
        /// A depot's own point (DepotSide, what "nearest depot" always measured) in the game's x/z, read once a visit.
        /// False for a depot without one, and while there is no map (nothing is kept then).
        /// </summary>
        private bool DepotGame(EtsMapCompany company, out Vector2 at)
        {
            if (company == null)
            {
                at = new Vector2(float.NaN, float.NaN);
                return false;
            }

            if (!_depotGame.TryGetValue(company, out at))
            {
                if (Viewer == null || Viewer.Map == null)
                {
                    at = new Vector2(float.NaN, float.NaN);
                    return false;
                }

                Vector3 centre = Viewer.CenterGame;
                at = Bay(company, DepotSide, out Vector3 world, out _, out _, out _) ? new Vector2(world.x + centre.x, centre.z - world.z) : new Vector2(float.NaN, float.NaN);
                _depotGame[company] = at;
            }

            return !float.IsNaN(at.x);
        }

        /// <summary>
        /// The area a game position is in: the city whose area box holds it (the smallest box when several do), else the
        /// city of the nearest depot within AreaReach; 0 when neither. nearestDepot is that depot's distance (metres).
        /// No allocation (NewFlowFrame asks it once a second while the truck is parked); only depots DepotGame has read count.
        /// </summary>
        private ulong AreaAt(float x, float z, out bool insideCity, out float nearestDepot)
        {
            insideCity = false;
            IReadOnlyList<EtsMapCity> cities = _index != null ? _index.Cities : null;
            ulong boxCity = 0UL;
            float boxSize = float.MaxValue;

            if (cities != null)
            {
                for (int i = 0; i < cities.Count; i++)
                {
                    EtsMapCity city = cities[i];
                    if (city == null || city.CityToken == 0UL || city.KdopMin == null || city.KdopMax == null || city.KdopMin.Length < 3 || city.KdopMax.Length < 3) continue;
                    if (x < city.KdopMin[0] || x > city.KdopMax[0] || z < city.KdopMin[2] || z > city.KdopMax[2]) continue;
                    float size = (city.KdopMax[0] - city.KdopMin[0]) * (city.KdopMax[2] - city.KdopMin[2]);

                    if (size < boxSize)
                    {
                        boxSize = size;
                        boxCity = city.CityToken;
                    }
                }
            }

            float best = float.MaxValue;
            ulong nearCity = 0UL;

            foreach (KeyValuePair<EtsMapCompany, Vector2> depot in _depotGame)
            {
                if (float.IsNaN(depot.Value.x) || depot.Key == null) continue;
                float dx = depot.Value.x - x, dz = depot.Value.y - z;
                float d = dx * dx + dz * dz;

                if (d < best)
                {
                    best = d;
                    nearCity = depot.Key.CityToken;
                }
            }

            nearestDepot = best < float.MaxValue ? Mathf.Sqrt(best) : float.PositiveInfinity;

            if (boxCity != 0UL)
            {
                insideCity = true;
                return boxCity;
            }

            return nearestDepot <= AreaReach ? nearCity : 0UL;
        }

        /// <summary>The own truck's area now (AreaAt), 0 without a truck or map.</summary>
        private ulong TruckArea()
        {
            if (Truck == null || Viewer == null)
            {
                return 0UL;
            }

            Vector3 at = Truck.transform.position;
            Vector3 centre = Viewer.CenterGame;
            return AreaAt(at.x + centre.x, centre.z - at.z, out _, out _);
        }

        private static string AreaLabel(ulong area)
        {
            return area == 0UL ? "(none)" : EtsToken.Decode(area);
        }

        /// <summary>
        /// The run may go on: the truck it was made for is still there and the map still open, and (new flow) no job has
        /// been taken from the offers already shown.
        /// </summary>
        private bool CanStillOffer()
        {
            return Truck != null && Viewer != null && Viewer.Map != null && _graph != null && !(EtsFeatures.NewFlow && (Current != null || _startJob != null));
        }

        private void EndOffers()
        {
            _offering = false;
            _offerRun = null;
        }

        private void FinishOffers(string area, float startedAt, int frames, string detail)
        {
            EndOffers();
            _offersMade = true;
            _offersMadeAt = Truck != null ? Truck.transform.position : Vector3.zero;
            _parkedFor = 0f;
            Debug.Log("[SomniumEuroTruck] Jobs: " + Offers.Count + " quick job offer(s)" + (!string.IsNullOrEmpty(area) ? " in the area '" + area + "'" : "") + " (home city '" + HomeCity + "'; " + detail + ") in " +
                      (Time.realtimeSinceStartup - startedAt).ToString("0.00") + " s over " + frames + " frames.");
        }

        /// <summary>
        /// Fills in cargo, names, the route (the search's, from the node nearest the depot's point), the game's km, pay
        /// and XP. False when the destination was not reached.
        /// </summary>
        private bool Complete(Offer offer, EtsMapNode start, float da, float fromX, float fromZ, EtsMapNode end, float db)
        {
            if (!CurtainsideCargo(offer.CargoUnit, out EtsSiiUnit cargo)) return false;
            if (start == null || end == null) return false;

            offer.Path = _search.PathTo(end.Uid, out float length);
            if (offer.Path == null) return false;

            offer.CargoName = Pretty(cargo.GetString("name", null), offer.CargoUnit.Replace("cargo.", ""));
            offer.UnitMass = cargo.GetFloat("mass", 500f);
            offer.UnitVolume = Mathf.Max(0.1f, cargo.GetFloat("volume", 2.6f));
            offer.RewardPerKmUnit = cargo.GetFloat("unit_reward_per_km", 0.6f);
            offer.Units = Mathf.Max(1, Mathf.FloorToInt(TrailerVolume / offer.UnitVolume));
            offer.FromCompanyName = CompanyName(offer.From);
            offer.FromCityName = CityName(offer.From.CityToken);
            offer.ToCompanyName = CompanyName(offer.To);
            offer.ToCityName = CityName(offer.To.CityToken);

            Vector3 centre = Viewer.CenterGame;
            float toX = offer.ToBay.x + centre.x, toZ = centre.z - offer.ToBay.z;
            offer.RouteLength = length + da + db;

            // The game counts km at its map scales (19, and 3 inside cities), not map metres: EtsEconomy's header.
            IReadOnlyList<EtsMapCity> cities = _index != null ? _index.Cities : null;
            offer.GameKm = EtsEconomy.RouteGameKm(Viewer.Map, offer.Path, cities) +
                           (da * EtsEconomy.ScaleAt(cities, fromX, fromZ) + db * EtsEconomy.ScaleAt(cities, toX, toZ)) / 1000f;
            offer.Urgency = 0;
            offer.Reward = EtsEconomy.QuickJobRevenue(offer.Units, offer.RewardPerKmUnit, offer.GameKm, offer.Urgency, Career != null ? Career.Level : 0);
            offer.Xp = EtsEconomy.DeliveryXp(offer.GameKm);
            return true;
        }

        // ------------------------------------------------------------------ taking and delivering

        public void Take(int index)
        {
            if (index < 0 || index >= Offers.Count || Truck == null || !Truck.Placed) return;

            // The new flow: the truck is the player's own (no EtsNet.Claim, PLAN §7.G), and §3.5's rules hold here too,
            // whoever calls.
            if (EtsFeatures.NewFlow)
            {
                if (!CanTake(out string reason))
                {
                    Say(reason);
                    return;
                }

                // Quick jobs are the area's own: offers made in another area (the truck drove on without stopping long
                // enough for NewFlowFrame to remake them) would move the truck back there. Make this area's instead.
                ulong here = TruckArea();

                if (here != _offersArea)
                {
                    Debug.Log("[SomniumEuroTruck] Jobs: take refused - the truck is in the area '" + AreaLabel(here) + "', the offers are for '" + AreaLabel(_offersArea) + "'; making this area's quick jobs.");
                    Say(here != 0UL ? "Your truck is in " + CityName(here) + " now - finding the quick jobs there ..." : "No companies near your truck. Drive into a city and stop there for its quick jobs.");
                    StopOffers();
                    MakeOffers();
                    return;
                }

                TakeNow(index);
                return;
            }

            // In a room the truck is shared: a job moves it, so the truck has to be this client's
            // first (EtsNet.Claim). Alone, or before the room is up, Claim answers at once.
            EtsNet net = EtsNet.Instance;

            if (net != null)
            {
                Offer wanted = Offers[index];
                net.Claim(() => TakeNow(Offers.IndexOf(wanted)), reason => Say(reason));
                return;
            }

            TakeNow(index);
        }

        /// <summary>A line on the HUD, from outside (EtsNet's news).</summary>
        public void Tell(string message)
        {
            Say(message);
        }

        /// <summary>
        /// The truck has gone to another client (EtsNet.StartPuppet): any job here ends, and its
        /// trailer is uncoupled and left standing where it is, as a delivered one is.
        /// </summary>
        public void LetGoOfTrailer()
        {
            // The job on its way, not the map index (it lives on EtsMapIndex now, but a start that is cut
            // short must still not take anything else with it). Stopped before Abandon, which refuses
            // while a start is running.
            Offer cut = null;

            if (_startJob != null)
            {
                StopCoroutine(_startJob);
                _startJob = null;
                cut = _starting;
                _starting = null;
            }

            if (Current != null)
            {
                Abandon();
            }

            if (_trailer != null)
            {
                _trailer.Detach();
                _trailer = null;
            }

            // A start cut short never raises JobStarted: say it failed, or the front end sits on the
            // job's loading screen for good with the menu and the door locked.
            if (cut != null)
            {
                JobFailed?.Invoke(cut);
            }
        }

        private Coroutine _startJob;

        // The offer StartJob is moving the truck for, so a start that is cut short can still be
        // reported as failed (the front end waits on JobStarted or JobFailed).
        private Offer _starting;

        /// <summary>
        /// A taken job is moving the truck to its depot and waiting for that yard to build. The map
        /// streams round the truck meanwhile, even for a player on foot (EtsTruckSeat.StreamFocus).
        /// </summary>
        public bool Relocating => _startJob != null;

        private void TakeNow(int index)
        {
            if (index < 0 || index >= Offers.Count || Truck == null || !Truck.Placed) return;
            Offer offer = Offers[index];

            // The new flow: the last delivered trailer goes now, behind the job's loading screen. Left, it would stand
            // for good in the bay it was dropped in - which can be the very trailer point the next job from the same
            // depot starts on (DeliverySide falls back to type 1, StartSide begins with it).
            if (EtsFeatures.NewFlow)
            {
                RemoveTrailer(ref _delivered);
            }

            if (_trailer == null)
            {
                _trailer = EtsTrailer.Create(Truck.transform.parent, null, null, Truck.Spec, _log);
                foreach (string line in _log) Debug.Log("[SomniumEuroTruck] " + line);
                _log.Clear();
            }

            if (_trailer == null)
            {
                Say("The trailer could not be built.");
                return;
            }

            Current = offer;
            _trailer.SetCargoMass(offer.CargoMass);

            // What the trailer carries from now on, for the own slot ([net §2.4] ints 19-20): this cargo's index in the sorted
            // /def list and the shipper's company hash, given to the trailer itself (C's EtsTrailer.SetLoadIdentity, so its
            // Identity - which EtsNet packs with the rig's trailer - is whole) and kept here for Published. The family and
            // variant are the trailer's own (EtsTrailer.FamilyOf its chassis: the curtain sider, 0). A trailer kept from an
            // abandoned job is re-described here too.
            EtsTrailerIdentity load = EtsJobRules.JobTrailer(EtsJobRules.CargoIndex(CargoOrder(), offer.CargoUnit), offer.From != null ? offer.From.CompanyToken : 0UL);
            _trailer.SetLoadIdentity(load.Cargo, load.CompanyHash);
            _trailerIdentity = _trailer.Identity;

            // Only a job already on its way is stopped; the map index must never be (it makes the offers).
            if (_startJob != null) StopCoroutine(_startJob);
            _starting = offer;
            _startJob = StartCoroutine(StartJob(offer));
        }

        /// <summary>
        /// Moves the truck to the depot's bay. The map streams in around the truck, so it is first
        /// held above the bay until the ground there has been built, then dropped onto it with the
        /// trailer coupled behind and the route drawn.
        /// </summary>
        private IEnumerator StartJob(Offer offer)
        {
            // On a trailer point (the game's way, and the depot's map node as before) the trailer stands in the bay
            // and the truck ahead of it by the coupling offsets; on a truck's place (parking, garage: a depot with no
            // trailer point) the truck stands on the point and the trailer behind it.
            Transform fifthWheel = Truck.FifthWheel != null ? Truck.FifthWheel : Truck.transform;
            float hookAhead = _trailer.transform.InverseTransformPoint(_trailer.Hook.position).z;
            float fifthWheelBehind = -Truck.transform.InverseTransformPoint(fifthWheel.position).z;
            Vector3 forward = Quaternion.Euler(0f, offer.FromYaw, 0f) * Vector3.forward;
            bool truckPlace = offer.FromBayType == EtsPrefabDescriptor.SpawnParking || offer.FromBayType == EtsPrefabDescriptor.SpawnGarage;
            Vector3 truckSpot = truckPlace ? offer.FromBay : offer.FromBay + forward * (hookAhead + fifthWheelBehind);

            // The game's Quick Job start: a loading screen, then the truck and the laden trailer at the pickup company's bay -
            // in the area the offers were made for (OffersRun), a short hop.
            float hop = Vector3.Distance(Truck.transform.position, truckSpot);
            Debug.Log("[SomniumEuroTruck] Jobs: quick job start - the truck goes " + hop.ToString("0") + " m to the bay of " + offer.FromCompanyName +
                      (offer.From != null ? " (" + offer.From.Name + "." + offer.From.CityId + ")" : "") + " in " + offer.FromCityName + "; offers' area '" + AreaLabel(_offersArea) + "'.");
            Say("Loading at " + offer.FromCompanyName + ", " + offer.FromCityName + " (" + hop.ToString("0") + " m from here) ...");
            StartProgress = 0f;
            JobStarting?.Invoke(offer);
            Truck.Body.isKinematic = true;
            Truck.transform.SetPositionAndRotation(truckSpot + Vector3.up * 3f, Quaternion.Euler(0f, offer.FromYaw, 0f));
            _trailer.Body.isKinematic = true;
            _trailer.transform.position = truckSpot + Vector3.up * 3f;
            StartProgress = 0.1f;

            // The streamer builds the world round the truck, and the truck has only just been moved
            // here, so the yard underneath does not exist yet. Wait for it - generously, because a
            // depot on the far side of a city is a lot of sectors - and then try to set down more
            // than once, since the ground can appear between one attempt and the next. Giving up
            // here used to drop the job on the floor and leave the player with nothing to do.
            float deadline = Time.time + 40f;

            while (Time.time < deadline && !Physics.Raycast(truckSpot + Vector3.up * 60f, Vector3.down, 300f, EtsLayers.GroundMask, QueryTriggerInteraction.Ignore))
            {
                Say("Waiting for " + offer.FromCompanyName + "'s yard to build ...");

                // The map viewer's own build progress while it is building; else creep towards 0.8 over the wait.
                float waited = 1f - (deadline - Time.time) / 40f;
                StartProgress = 0.1f + 0.7f * Mathf.Clamp01(Mathf.Max(waited * 4f, EtsMapViewer.LoadProgress > 0f && EtsMapViewer.LoadProgress < 1f ? EtsMapViewer.LoadProgress : 0f));
                yield return new WaitForSeconds(0.25f);
            }

            StartProgress = 0.8f;
            yield return new WaitForSeconds(0.5f);
            yield return new WaitForFixedUpdate();
            StartProgress = 0.9f;

            bool down = false;

            for (int attempt = 0; attempt < 12 && !down; attempt++)
            {
                down = Truck.PlaceOnGround(truckSpot, offer.FromYaw);

                if (!down)
                {
                    yield return new WaitForSeconds(0.5f);
                }
            }

            if (!down)
            {
                Say("The yard at " + offer.FromCompanyName + " never finished building. The job is still yours - open the menu and take it again.");

                // Not let go over ground that does not exist - that dropped it into the void.
                // Back to the last place it stood; if that has gone too it stays held, and R or
                // the menu tries again. Then the front end is told, or it would sit on the job's
                // loading screen for good with the menu and the keys locked away.
                Current = null;
                Truck.Respawn();
                _startJob = null;
                _starting = null;
                StartProgress = 0f;
                JobFailed?.Invoke(offer);
                yield break;
            }

            _trailer.Attach(Truck, fifthWheel);

            // The new flow: the rig carries its trailer (EtsTruckRig.Trailer, "set by EtsJobs (own truck)"), and the
            // player's slot says what they carry where (PublishJob; jobs are not shared, PLAN §1 row 17).
            if (EtsFeatures.NewFlow)
            {
                if (_rig != null)
                {
                    _rig.Trailer = _trailer;
                }

                PublishJob(offer);
            }

            if (_route == null)
            {
                _route = new GameObject("Route").AddComponent<EtsRouteLine>();
            }

            _route.Show(Viewer.Map, offer.Path, Viewer.CenterGame);
            _stoppedTimer = 0f;
            _jobStartTime = Time.time;
            _jobDistance = 0f;
            _lastTruckPosition = Truck.transform.position;
            PlaceMarker(offer.ToBay, offer.ToYaw);
            Say("Job taken: " + offer.Units + " x " + offer.CargoName + " (" + Mathf.RoundToInt(offer.CargoMass / 1000f) + " t) from " + offer.FromCompanyName + " to " + offer.ToCompanyName + ", " + offer.ToCityName + ". Follow the route on the GPS (bottom right).");
            _startJob = null;
            _starting = null;
            StartProgress = 1f;
            JobStarted?.Invoke(offer);
        }

        // ------------------------------------------------------------------ the own slot's job (PLAN §8 Phase 3, G3)

        // The job the own slot shows (PublishJob .. UnpublishJob) and the jobs' trailer's identity (TakeNow).
        private bool _jobShown;
        private ulong _shownCargo;
        private ulong _shownCity;
        private EtsTrailerIdentity _trailerIdentity;
        private string[] _cargoOrder;

        // The "delivered once they are out" line was logged for this job (a passenger at their own bay).
        private bool _heldForRider;

        /// <summary>
        /// What the own slot says about this player's job ([net §2.4] ints 19-20 and 28-31; EtsJobRules.EtsJobSummary):
        /// the cargo and destination city while a job runs (from its start at the depot to its delivery, abandon or the
        /// truck's loss), and the trailer's identity while the own truck pulls the jobs' trailer (EtsTruckRig.Trailer) -
        /// which it still does after an abandon, the trailer staying coupled. A struct, made on each call without
        /// allocating: EtsNet may read it on every slot write. Jobs stay per player; nothing else of them is networked.
        /// </summary>
        public EtsJobSummary Published
        {
            get
            {
                bool pulling = _trailer != null && _rig != null && ReferenceEquals(_rig.Trailer, _trailer);
                return EtsJobRules.Summary(_jobShown, _shownCargo, _shownCity, pulling, _trailerIdentity);
            }
        }

        /// <summary>
        /// The job reaches the own slot when it has started (the truck is at the depot, the trailer coupled): the summary
        /// through EtsNet.SetJob (PLAN §7.A), the trailer's identity through Published (EtsNet reads it with the rig's
        /// trailer). One line in the log per job.
        /// </summary>
        private void PublishJob(Offer offer)
        {
            _heldForRider = false;
            _jobShown = true;
            _shownCargo = EtsJobRules.CargoToken(offer.CargoUnit);
            _shownCity = offer.To != null ? offer.To.CityToken : 0UL;
            EtsNet.Instance?.SetJob(_shownCargo, _shownCity);
            Debug.Log("[SomniumEuroTruck] Jobs: the own slot shows " + offer.CargoUnit + " to " + EtsToken.Decode(_shownCity) + " (trailer family " + _trailerIdentity.Family +
                      ", cargo " + _trailerIdentity.Cargo + ", company hash " + _trailerIdentity.CompanyHash.ToString("x8") + ").");
        }

        /// <summary>The job is over (delivered, abandoned, cut with the truck): the own slot stops showing it (EtsNet.ClearJob).</summary>
        private void UnpublishJob()
        {
            _jobShown = false;
            _shownCargo = 0UL;
            _shownCity = 0UL;
            EtsNet.Instance?.ClearJob();
        }

        /// <summary>The sorted /def cargo list of the trailer identity (EtsJobRules.SortedCargo), built at the first job start of the visit.</summary>
        private string[] CargoOrder()
        {
            if (_cargoOrder == null && _cargo != null)
            {
                var names = new List<string>(_cargo.Units.Count);

                foreach (EtsSiiUnit unit in _cargo.Units)
                {
                    names.Add(unit.Name);
                }

                _cargoOrder = EtsJobRules.SortedCargo(names);
            }

            return _cargoOrder;
        }

        // Display lines for other players' jobs, by token pair (a few players, a few jobs each visit).
        private readonly Dictionary<ulong, string> _cargoLabels = new Dictionary<ulong, string>();
        private readonly Dictionary<ulong, string> _cityLabels = new Dictionary<ulong, string>();
        private readonly Dictionary<JobKey, string> _jobLines = new Dictionary<JobKey, string>();

        // The key of a job line. A struct of its own, not a ValueTuple: nothing else in the world uses tuples, and the upload
        // server compiles against older assemblies than the editor (PLAN §12, U3), so only what is proven there is used.
        private readonly struct JobKey : IEquatable<JobKey>
        {
            private readonly ulong _cargo;
            private readonly ulong _city;

            public JobKey(ulong cargo, ulong city)
            {
                _cargo = cargo;
                _city = city;
            }

            public bool Equals(JobKey other) => _cargo == other._cargo && _city == other._city;
            public override bool Equals(object obj) => obj is JobKey other && Equals(other);
            public override int GetHashCode() => unchecked(_cargo.GetHashCode() * 397 ^ _city.GetHashCode());
        }

        /// <summary>
        /// Another player's job as a line: "{cargo} to {city}" from the summary their slot shows (EtsPlayerView.CargoToken,
        /// DestinationCityToken), for the tablet's Convoys page. Cargo names in the game's words (the /def cargo
        /// "name" through EtsLocale once it is loaded, else as the offers name them), city names from the map. Built once
        /// per pair and kept, so a page may ask every frame; "" for no job.
        /// </summary>
        public string JobLine(ulong cargoToken, ulong destinationCityToken)
        {
            if (cargoToken == 0UL && destinationCityToken == 0UL)
            {
                return "";
            }

            if (!_jobLines.TryGetValue(new JobKey(cargoToken, destinationCityToken), out string line))
            {
                string cargo = CargoLabel(cargoToken);
                string city = CityLabel(destinationCityToken);
                line = cargo.Length > 0 && city.Length > 0 ? cargo + " to " + city : cargo + city; // "to": not in the locale

                // Kept only once both names come from the files (the cargo list read, the map open).
                if (_cargo != null && MapOpen)
                {
                    _jobLines[new JobKey(cargoToken, destinationCityToken)] = line;
                }
            }

            return line;
        }

        /// <summary>A city's name from its token (the map's city_name, else the token itself), kept once made; "" for 0. For "Riding to {city}" and Convoys.</summary>
        public string CityLabel(ulong cityToken)
        {
            if (cityToken == 0UL)
            {
                return "";
            }

            if (!_cityLabels.TryGetValue(cityToken, out string name))
            {
                name = Localized(CityName(cityToken));

                if (MapOpen)
                {
                    _cityLabels[cityToken] = name;
                }
            }

            return name;
        }

        private bool MapOpen => Viewer != null && Viewer.Map != null;

        private string CargoLabel(ulong cargoToken)
        {
            if (cargoToken == 0UL)
            {
                return "";
            }

            if (!_cargoLabels.TryGetValue(cargoToken, out string name))
            {
                string token = EtsToken.Decode(cargoToken);
                EtsSiiUnit cargo = _cargo != null ? _cargo.Find("cargo." + token) : null;
                string raw = cargo != null ? cargo.GetString("name", null) : null;
                string resolved = EtsLocale.Loaded && !string.IsNullOrEmpty(raw) ? EtsLocale.Resolve(EtsSiiValue.Unquote(raw) ?? raw) : null;
                name = !string.IsNullOrEmpty(resolved) && resolved.IndexOf("@@", StringComparison.Ordinal) < 0 ? resolved : Pretty(raw, token.Replace('_', ' '));

                // Only kept once the cargo list is there: asked before the map is read, the next ask may name it properly.
                if (_cargo != null)
                {
                    _cargoLabels[cargoToken] = name;
                }
            }

            return name;
        }

        private static string Localized(string text)
        {
            if (string.IsNullOrEmpty(text) || !EtsLocale.Loaded || text.IndexOf("@@", StringComparison.Ordinal) < 0)
            {
                return text ?? "";
            }

            string resolved = EtsLocale.Resolve(text);
            return string.IsNullOrEmpty(resolved) ? text : resolved;
        }

        // ------------------------------------------------------------------ the bay marker

        /// <summary>
        /// Marks the bay the trailer must be brought to, the way the game does: a translucent
        /// slab the size of a trailer on the yard, and a beam above it that can be seen from the
        /// road. Removed when the job ends either way.
        /// </summary>
        private void PlaceMarker(Vector3 bay, float yaw)
        {
            RemoveMarker();
            _marker = new GameObject("delivery bay");
            _marker.transform.SetPositionAndRotation(bay, Quaternion.Euler(0f, yaw, 0f));

            Material glow = MarkerMaterial(new Color(0.25f, 0.95f, 0.45f, 0.35f));
            Shape(_marker.transform, "slab", new Vector3(0f, 0.2f, 0f), new Vector3(3.4f, 0.35f, 16f), glow);
            Shape(_marker.transform, "beam", new Vector3(0f, 25f, 0f), new Vector3(0.7f, 50f, 0.7f), MarkerMaterial(new Color(0.25f, 0.95f, 0.45f, 0.22f)));
        }

        private static void Shape(Transform parent, string name, Vector3 at, Vector3 size, Material material)
        {
            GameObject shape = GameObject.CreatePrimitive(PrimitiveType.Cube);
            shape.name = name;
            Destroy(shape.GetComponent<Collider>());
            shape.transform.SetParent(parent, false);
            shape.transform.localPosition = at;
            shape.transform.localScale = size;
            shape.layer = EtsLayers.IgnoreRaycast;
            var renderer = shape.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
        }

        private static Material MarkerMaterial(Color colour)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Universal Render Pipeline/Simple Lit") ?? Shader.Find("Universal Render Pipeline/Lit");
            var material = new Material(shader) { name = "delivery bay" };
            material.SetColor("_BaseColor", colour);
            material.SetColor("_Color", colour);

            if (material.HasProperty("_Surface"))
            {
                material.SetFloat("_Surface", 1f);
                material.SetFloat("_Blend", 0f);
                material.SetOverrideTag("RenderType", "Transparent");
                material.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                material.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                material.SetInt("_ZWrite", 0);
                material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            }

            return material;
        }

        private void RemoveMarker()
        {
            if (_marker != null)
            {
                Destroy(_marker);
                _marker = null;
            }
        }

        public void Abandon()
        {
            // Not while the job is moving the truck to its depot: StartJob would go on to couple the trailer and raise
            // JobStarted for a job that is no longer there, with the truck held above the bay. (The old front end locks
            // its menu on the job's loading screen; the tablet's Current Job page could reach this.)
            if (Relocating)
            {
                Say("The job is starting - abandon it once you are at the depot.");
                return;
            }

            bool had = Current != null;
            Current = null;
            if (_route != null) _route.Hide();
            RemoveMarker();
            Say("Job abandoned. The trailer stays coupled; take another job to start again.");

            // The new flow: the slot no longer shows the job, and the offers are made again from where the truck is now
            // (the depot nearest it; the old menu has "Look for other jobs" for that, the tablet page has no such button).
            if (EtsFeatures.NewFlow && had)
            {
                UnpublishJob();
                StopOffers();
                Offers.Clear();
                _offersWanted = true;
            }
        }

        private void Deliver()
        {
            Offer job = Current;

            // The cargo's damage takes the game's toll off the pay and XP first (EtsDamage, economy_data.sii; 2026-10-04).
            string damageNote = EtsDamage.Deliver(job);
            _sessionCompleted++;
            _sessionEarnings += job.Reward;
            Current = null;
            if (_route != null) _route.Hide();
            RemoveMarker();

            // Into the career: once per job id (the store refuses a second credit of the same id), with the delivery
            // log's line (PLAN §3.8). Only the client that drove the job ever calls this ([persist §5.7]). The experience
            // before and after is what the Delivery page's bar and level lines are drawn from.
            EtsProfileStore store = EtsProfileStore.Current;
            bool open = store != null && store.IsReady && store.Profile != null;
            long experienceBefore = store != null ? store.Experience : 0;
            bool credited = false;

            if (store != null)
            {
                credited = store.Credit(job.Id, job.Reward, job.Xp,
                                        EtsProfileCodec.DeliverySummary(job.CargoName, job.FromCompanyName + ", " + job.FromCityName, job.ToCompanyName + ", " + job.ToCityName, job.GameKm)) && open;
            }

            // Open: what the career holds now (a refused credit adds nothing). Queued (the career still opening) or no store:
            // what it will hold once the credit lands.
            long experienceAfter = open ? store.Experience : experienceBefore + Math.Max(0, job.Xp);

            if (EtsFeatures.NewFlow)
            {
                UnpublishJob();
            }

            // Holds in the bay while the summary screens are up and nobody has the keys.
            Truck.ParkingBrake = true;

            if (_trailer != null)
            {
                _trailer.Detach();

                // The new flow keeps hold of it, to take it away when the next job starts (TakeNow).
                if (EtsFeatures.NewFlow)
                {
                    RemoveTrailer(ref _delivered);
                    _delivered = _trailer;

                    if (_rig != null && _rig.Trailer == _trailer)
                    {
                        _rig.Trailer = null;
                    }
                }

                _trailer = null;
            }

            var stats = new DeliveryStats
            {
                Job = job,
                DistanceMetres = _jobDistance,
                Seconds = Time.time - _jobStartTime,
                // The game's trucks burn about thirty litres to the hundred kilometres laden.
                FuelLitres = _jobDistance / 1000f * 0.30f,
                Reward = job.Reward,
                Xp = job.Xp,
                Balance = Balance,
                ExperienceBefore = experienceBefore,
                ExperienceAfter = experienceAfter,
                LevelBefore = EtsXpTable.LevelFor(experienceBefore),
                LevelAfter = EtsXpTable.LevelFor(experienceAfter),
                DrivenGameKm = job.ToGameKm(_jobDistance),
                Credited = credited
            };

            if (stats.LevelAfter > stats.LevelBefore)
            {
                Debug.Log("[SomniumEuroTruck] Jobs: level " + stats.LevelBefore + " -> " + stats.LevelAfter + " (" + experienceAfter + " XP).");
            }

            // The demo is one delivery. Its executable is a demo build - it carries the strings
            // -demo, demo_gameplay_mode, screen.demo and /ui/demo_mode_hud_*.sii - and the screen it
            // ends on, /ui/desktop_standalone_demo.sii, is shipped: the game's logo over
            // "demo ended" and "demo complete", with one button to the store page. So the run could
            // stop where the game stops, and that screen is rebuilt from its own layout file. Off by
            // default since the career keeps what is earned (PLAN §8 G1, [gameui §5]).
            bool demoOver = DemoRun && _sessionCompleted >= 1;

            if (demoOver)
            {
                Say("Delivered " + job.CargoName + " to " + job.ToCompanyName + ". Paid " + job.Reward.ToString("N0") + " EUR. " + damageNote + "That is the demo's one delivery.");
                Offers.Clear();
                Status = "Demo complete.";
            }
            else
            {
                Say("Delivered " + job.CargoName + " to " + job.ToCompanyName + ". Paid " + job.Reward.ToString("N0") + " EUR, " + job.Xp + " XP. " + damageNote + "Balance " + Balance.ToString("N0") + " EUR. " +
                    (EtsFeatures.NewFlow ? "Your tablet (ETS 2 - Jobs) has the next job." : "Open the menu (Esc) for the next job."));
                MakeOffers();
            }

            // The front end shows the game's delivery summary and, in the demo, its end screen
            // after that. Without a front end the end screen is shown straight away.
            if (Delivered != null)
            {
                Delivered(stats);
            }
            else if (demoOver)
            {
                EtsDemoEndScreen.Show(this);
            }
        }

        public string Describe()
        {
            string log = Completed > 0 ? "Delivered " + Completed + " load" + (Completed == 1 ? "" : "s") + ", earned " + Earnings.ToString("N0") + " EUR. " : "";

            if (Current != null && Truck != null)
            {
                float remaining = _route != null && _route.Visible ? _route.RemainingDistance(Truck.transform.position) : Vector3.Distance(Truck.transform.position, Current.ToBay);
                return log + "Delivering " + Current.CargoName + " to " + Current.ToCompanyName + ", " + Current.ToCityName + ": " + Current.ToGameKm(remaining).ToString("0.0") + " km to go, " + Current.Reward.ToString("N0") + " EUR.";
            }

            // While the index reads, its own line ("Reading the map: n of m sectors"), as IndexMap used to set it.
            return log + (!MapIndexed && _index != null ? _index.Status : Status);
        }

        private void Update()
        {
            if (EtsFeatures.NewFlow)
            {
                LookAtSession();
                NewFlowFrame();
            }

            if (Truck == null || !Truck.Placed)
            {
                return;
            }

            if (Current != null && _trailer != null)
            {
                // Distance driven on this job, for the delivery summary. A jump of more than fifty
                // metres in a frame is a teleport (respawn, placement), not driving.
                float step = Vector3.Distance(Truck.transform.position, _lastTruckPosition);
                if (step < 50f) _jobDistance += step;
                _lastTruckPosition = Truck.transform.position;

                float d = Vector3.Distance(_trailer.Position, Current.ToBay);

                // PLAN §3.5 / G3: only the own truck's own job counts, and not while the player rides in another player's
                // truck (EtsJobRules.DeliveryCounts): a passenger is never paid for anyone's delivery. Said once per job.
                bool riding = RidingAsPassenger;
                bool ownTruck = _rig != null && ReferenceEquals(EtsLocalTruck.Own, _rig);

                if (riding && d < ArrivalRadius && !_heldForRider)
                {
                    _heldForRider = true;
                    Debug.Log("[SomniumEuroTruck] Jobs: the trailer is in its bay, but the player rides in another player's truck - it is delivered once they are out.");
                }

                if (EtsJobRules.DeliveryCounts(EtsFeatures.NewFlow, ownTruck, _rig != null && _rig.IsPuppet, riding, d, ArrivalRadius, Truck.SpeedKmh))
                {
                    _stoppedTimer += Time.deltaTime;

                    if (_stoppedTimer > 1.5f)
                    {
                        Deliver();
                    }
                }
                else
                {
                    _stoppedTimer = 0f;
                }
            }

            if (Hud != null && Truck.Hud == Hud && (_hudTimer += Time.deltaTime) > 0.2f)
            {
                _hudTimer = 0f;
                string line = _message != null && Time.time < _messageUntil ? _message : Current != null ? Describe() : "";

                if (line.Length > 0)
                {
                    Hud.text = Hud.text.Split('\n')[0] + "\n" + line;
                }
            }
        }

        /// <summary>
        /// The new flow's per-frame part, all cheap (no allocation): the HUD the own truck got after binding; offers once
        /// the truck stands (after a spawn, a delivery, an abandoned job) and again when it has been parked for 3 s in another
        /// area than the one they were made for (AreaAt); and the driving the career is credited with.
        /// </summary>
        private void NewFlowFrame()
        {
            if (Truck == null)
            {
                _haveDrivePosition = false;
                return;
            }

            if (Hud == null && Truck.Hud != null)
            {
                Hud = Truck.Hud;
            }

            if (!Truck.Placed)
            {
                _haveDrivePosition = false;
                return;
            }

            if (Current == null && !Relocating && !_offering && MapIndexed)
            {
                if (_offersWanted)
                {
                    MakeOffers();
                }
                else if (_offersMade && Truck.SpeedKmh < 1f)
                {
                    _parkedFor += Time.deltaTime;

                    // Quick jobs follow the area: parked 3 s somewhere else than where they were made, the area is looked
                    // at once a second (AreaAt, no allocation); another area gets its own offers.
                    if (_parkedFor > 3f && Time.time >= _nextAreaLook && (Truck.transform.position - _offersMadeAt).sqrMagnitude > AreaMoveMin * AreaMoveMin)
                    {
                        _nextAreaLook = Time.time + AreaLookEvery;
                        ulong here = TruckArea();

                        if (here != _offersArea)
                        {
                            Debug.Log("[SomniumEuroTruck] Jobs: the truck is parked in the area '" + AreaLabel(here) + "' (" + Mathf.RoundToInt(Vector3.Distance(Truck.transform.position, _offersMadeAt)) +
                                      " m from where the offers for '" + AreaLabel(_offersArea) + "' were made) - making its quick jobs.");
                            MakeOffers();
                        }
                    }
                }
                else
                {
                    _parkedFor = 0f;
                }
            }

            AccountDriving();
        }

        /// <summary>
        /// Distance and free-roam XP while the local player drives the own truck ([persist §5.8]: "AddDriving(step,
        /// Current == null ? step / 1000 * exp_free_roam : 0)"), counted in the game's metres - world metres times the map
        /// scale where the truck is (EtsEconomy.ScaleAt: 19, or 3 inside a city area), the same km the jobs pay and the
        /// offers show. exp_free_roam is 0.5 on 1.61 (EtsEconomy.ExpFreeRoam); whether the game counts it per km driven
        /// or per km of new road is undetermined. A step of 50 m or more in a frame is a teleport, not driving. Handed
        /// to the career every 2 s in whole units (EtsProfileStore.AddDriving), kept here until the career is open.
        /// </summary>
        private void AccountDriving()
        {
            Vector3 p = Truck.transform.position;
            bool driving = _rig != null && EtsLocalTruck.Occupied == _rig && !Relocating;

            if (driving && _haveDrivePosition && Viewer != null)
            {
                float step = Vector3.Distance(p, _drivePosition);

                if (step > 0.001f && step < 50f)
                {
                    Vector3 centre = Viewer.CenterGame;
                    float scale = EtsEconomy.ScaleAt(_index != null ? _index.Cities : null, p.x + centre.x, centre.z - p.z);
                    double gameMetres = step * (double)scale;
                    _drivenGameMetres += gameMetres;
                    EtsDamage.AddWear(gameMetres / 1000.0, Truck); // damage_data.sii's wear per km of drive (2026-10-04)

                    if (Current == null)
                    {
                        _drivenXp += gameMetres / 1000.0 * EtsEconomy.ExpFreeRoam;
                    }
                }
            }

            _drivePosition = p;
            _haveDrivePosition = true;

            if (Time.unscaledTime >= _flushAt)
            {
                _flushAt = Time.unscaledTime + 2f;
                FlushDriving();
            }
        }

        private void FlushDriving()
        {
            EtsProfileStore store = Career;

            if (store == null)
            {
                return;
            }

            long metres = (long)_drivenGameMetres;
            long xp = (long)_drivenXp;

            if (metres <= 0 && xp <= 0)
            {
                return;
            }

            store.AddDriving(metres, xp);
            _drivenGameMetres -= metres;
            _drivenXp -= xp;
        }

        private void Say(string message)
        {
            _message = message;
            _messageUntil = Time.time + 10f;
            Debug.Log("[SomniumEuroTruck] " + message);
        }
    }
}
