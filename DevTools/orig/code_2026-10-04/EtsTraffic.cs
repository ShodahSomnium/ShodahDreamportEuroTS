using System;
using System.Collections.Generic;
using System.Diagnostics;
using Fusion;
using SomniumSpace.Bridge;
using SomniumSpace.Bridge.Player;
using SomniumSpace.Network.Bridge;
using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace SomniumSpace.Worlds.SomniumEuroTruck
{
    /// <summary>
    /// AI traffic, networked the way the truck is (EtsNet) and the AoM units are (AomNet's
    /// "Aom Net Slots n"): ONE client runs every car and writes them into the networked ints of
    /// "Ets Net Traffic 0..3" (four NetworkBridgeData64, fifteen cars each, EtsTrafficWire);
    /// everyone else draws them a beat behind on the truck's own interpolation (EtsNetTimeline).
    /// EtsNet adds this component to its own object ('Ets Net') in Awake.
    ///
    /// Who: any client that has the traffic built and holds state authority over all four
    /// blocks - normally the room's shared-mode master, because SceneNetworking gives the master
    /// every object nobody holds (ReassignNullObjectsAuthority, every second and when a player
    /// leaves), and this script asks for any block nobody holds. But the master may have no
    /// ETS2 install (EtsNet.Placeholder), may still be loading, or its car table may not read:
    /// its traffic never starts, yet it holds the blocks. So authority follows readiness (the
    /// rules, pure and tested offline, are EtsTrafficAuthority's; the times are its constants):
    ///   - the runner rewrites every block at least once a second (a heartbeat), so a quiet
    ///     block (cars queued at a junction) can be told from a runner that has gone;
    ///   - every client notes when each block last changed. A block unchanged for 4 s
    ///     (StaleSeconds) has its cars taken off the road on watchers, not held frozen with
    ///     their boxes on;
    ///   - when no block has changed for 3 s, plus 1.5 s for each player in the room with a
    ///     lower PlayerId that holds no block, a client whose traffic is built asks for all four
    ///     (the blocks allow a state-authority override). The first in line takes over before
    ///     the watchers let go of the cars, so it carries them on; it is writing before the next
    ///     one's turn, so normally one client asks. Two that ask at once split the blocks: the
    ///     higher PlayerId stands aside for a lower one that took a block within 2 s, and the
    ///     lower one takes the rest;
    ///   - nobody takes blocks from a runner that is writing them: not the master, not anyone,
    ///     and a runner that finds another writing its blocks (it hitched past the takeover)
    ///     stands down. One that lost a block to nobody, or to a higher PlayerId not writing it
    ///     yet, carries on and takes it back.
    /// A client without the game never gets as far as asking, so it never holds the room up
    /// for longer than the takeover wait. Alone, in the editor, before the room is joined or
    /// with no blocks in the scene, this client runs the traffic for itself and writes nothing
    /// (role Local) - the old single-player world with cars in it.
    ///
    /// Around what: every player. Each client streams the map ~1.2 km round its own focus
    /// (EtsMapViewer), but the lanes need no geometry (EtsLaneGraph reads the map's items only;
    /// EtsJobs has read every sector at start), so the master keeps lanes round every player's
    /// avatar and the truck, spawns cars out of each one's sight and removes them when they are
    /// far from all of them (EtsTrafficSim). Watchers keep lanes round the same foci, so that one
    /// of them taking over finds the cars' lanes built.
    ///
    /// When the runner leaves: SceneNetworking hands the blocks to the master; if its traffic
    /// is built it sees authority arrive, puts every car it was drawing onto the lane its pose
    /// lies on (EtsLaneGraph.Match), and carries on writing with a new generation, which every
    /// watcher's timeline treats as a new epoch (a snap of at most the drawing delay, not a
    /// slide). A car no lane fits is removed. If the master cannot run it, the blocks fall
    /// silent and the takeover above hands them on. Whoever starts writing first takes the
    /// block's live ints as what the watchers have, so every slot it has no car for is cleared
    /// on the wire, not left holding the old runner's car.
    ///
    /// The frame. The master runs in a headset at 90 Hz, so nothing here may take a frame's
    /// worth at once. Everything that is not the few-hundred-microsecond simulation step is
    /// background work under FrameBudgetMs a frame: lane building (a road, a slice of a
    /// junction's curves, a batch of joins a step - EtsLaneGraph), car model templates (a
    /// material, a mesh, a lod a step - EtsTrafficModels) and the worker's results (a texture
    /// upload each). Archive reads, GDeflate and parsing - the catalog, the junction descriptors,
    /// the car models and their textures - run on one background thread (EtsWorker). Spawn
    /// candidates are gathered a few hundred cells a step and kept per focus (EtsTrafficSim).
    /// At most one car is cloned and two are shown a frame. The log says every 10 s what the
    /// traffic cost at worst in a single frame.
    ///
    /// Drawing: each car is a pooled clone of its model's template (EtsTrafficModels) with a
    /// kinematic interpolated Rigidbody, moved in FixedUpdate (MovePosition): Unity interpolates
    /// it to the 90 Hz frame, and the truck's physics meets it at the same instants. Only cars
    /// within DrawRadius of this client's own focus are shown, and only those within
    /// ColliderRadius of it can be hit.
    ///
    /// Hitting a car (2026-09-23, "fix the collisions with cars"). A kinematic body is an
    /// immovable one to PhysX, so the truck used to stop dead against a car as against a wall
    /// (26.1 kN s at 45 km/h off a qashqai's corner, Player.log 19:34), a car on the wire could be
    /// drawn into the truck and shove it, and a box switched on inside the truck fired it off.
    /// Now, as in ETS2:
    ///   - The own truck, the own trailer and any wreck this client's physics is moving are
    ///     HITTERS (BuildHitters, each FixedUpdate from their bodies). Before a car is moved
    ///     (Place), its next two steps are tested against theirs (WillHit: its fitted hull and
    ///     their boxes, EtsTrafficSim.Overlap2D, at half-step instants); a car that would touch
    ///     one becomes a dynamic Rigidbody of its own vehicle_mass there and then, moving as it
    ///     was (EtsTrafficModels.MakeDynamic), so the contact PhysX then makes is between two
    ///     bodies with real masses: the truck shoves, spins or rolls a 1.3 t car and loses what a
    ///     1.3 t car costs it. "Traffic: car hit ..." is logged.
    ///   - Runner (Master or Local): the sim is told (EtsTrafficSim.Wreck) and gets the wreck's
    ///     pose from its body before every step (SyncWrecks), so the wire carries the pushed car
    ///     with its hazards flag to everyone, the other cars queue behind it or pass it, and it
    ///     drives on when it has stood still on its lane (EtsTrafficSim.Recover), or goes when far.
    ///     A wreck beyond ColliderRadius + 20 m goes back to the sim's own slide; one within
    ///     ColliderRadius again is dynamic again. Another player's truck hits a car in the sim's
    ///     boxes (EtsTrafficSim.Collide), logged from its Hits.
    ///   - Watcher: the hit car leaves the wire here (Slot.Local): this client's physics moves it
    ///     until it has rested 2 s with none of this client's vehicles within 25 m, then it takes
    ///     the wire's pose again (snapped, its boxes held off until clear). So a car is never
    ///     drawn through this client's truck. The runner learns of it only if EtsNet carries
    ///     HitReport to it (ApplyHitReport); without that, the room's copy of the car drives on.
    ///   - No box comes on inside one of this client's vehicles (Place: held off, "ghost", and
    ///     tried again each step), so a car coming into ColliderRadius never shoves the truck.
    ///   - A wreck's four hazard lamps blink (Blink), from the sim, the wire's flag or a local hit.
    /// </summary>
    [DefaultExecutionOrder(-90)]
    public sealed class EtsTraffic : MonoBehaviour
    {
        public const string BlockPrefix = "Ets Net Traffic ";
        public const int MaxBlocks = 4;

        public enum Role { Off, Local, Master, Watcher }

        [Tooltip("Writes a second. Fusion's own send rate in the client is 16 Hz (NetworkProjectConfig.fusion: client tick 32, send index 1).")]
        public float WriteRate = 10f;

        [Tooltip("Cars farther than this from this client's own focus are not shown here (hidden again 50 m further out).")]
        public float DrawRadius = 700f;

        [Tooltip("Car boxes collide only this close to this client's truck or player.")]
        public float ColliderRadius = 120f;

        [Tooltip("Most cars in the world at once, over all players (the wire carries 60).")]
        public int MaxCars = 48;

        [Tooltip("Most cars round one player or truck.")]
        public int PerFocus = 24;

        [Tooltip("Background work (lanes, car models, uploads) a frame, milliseconds.")]
        public float FrameBudgetMs = 1.5f;

        [Tooltip("Lanes are built for every map sector touching a circle this big round every player and the truck.")]
        public float LaneRadius = 1000f;

        [Tooltip("Lane sectors whose centre is further than this from every player and the truck are dropped again.")]
        public float LaneDropDistance = 8000f;

        [Tooltip("Build every car model the country spawns once the map is ready, a step at a time, before the first car needs one.")]
        public bool Prewarm = true;

        [Tooltip("Read file data and textures on a background thread. Off: the same work on the main thread, one job a frame.")]
        public bool UseWorkerThread = true;

        [Tooltip("A map sector nobody else has read after this many seconds is read by the traffic itself (EtsJobs normally reads them all at start).")]
        public float SectorWait = 20f;

        public static EtsTraffic Instance { get; private set; }

        public Role Current { get; private set; } = Role.Off;

        private EtsTrafficCatalog _catalog;
        private EtsLaneGraph _graph;
        private EtsTrafficSim _sim;
        private EtsTrafficModels _models;
        private EtsWorker _worker;
        private Transform _root;
        private bool _starting;
        private float _startedAt = -1f;

        private readonly List<NetworkBridgeData> _blocks = new List<NetworkBridgeData>();
        private int[][] _read = new int[0][];
        private int[][] _shadow = new int[0][];
        private long[] _seen = new long[0];
        private int[] _seq = new int[0];
        private readonly int[] _scratch = new int[EtsTrafficWire.Ints];
        private float _nextFind;
        private float _nextAsk;
        private float _nextWrite;
        private int _generation;

        // Runner, per block: when the next heartbeat is due, and whether the shadow was taken
        // from the block's live ints since this client last gained authority over it.
        private float[] _beatAt = new float[0];
        private bool[] _seeded = new bool[0];

        // Every client: who holds each block and when each last changed, never reset by a role
        // change (EtsTrafficAuthority); which blocks this client holds this frame; and whether
        // a block's cars were taken off the road for silence.
        private EtsTrafficAuthority _authority = new EtsTrafficAuthority(0);
        private bool[] _mine = new bool[0];
        private bool[] _silent = new bool[0];

        // Drain's tests and the takeover queue's count, made once: a lambda built in Update is a
        // delegate (and, capturing a local, a closure) allocated every frame.
        private Func<bool> _untilBegun;
        private Func<bool> _withinBudget;
        private Func<int> _playersBelow;
        private double _budgetMs;

        private readonly HashSet<string> _navFailed = new HashSet<string>(StringComparer.Ordinal);

        private sealed class Slot
        {
            public readonly EtsNetTimeline Timeline = new EtsNetTimeline { MaxDelay = 0.5f, LeadIn = true };
            public EtsTrafficModels.Visual Visual;

            /// <summary>The serial (0-15) of the car the Visual was taken for: another serial is another car.</summary>
            public int VisualSerial = -1;

            public int Key = -1;
            public bool Alive;
            public EtsTrafficWire.Car Last;
            public double LastStamp;
            public float LastYaw;

            /// <summary>Its global index (block * 15 + car).</summary>
            public int Index;

            /// <summary>Watcher: this client's vehicle hit the car and its physics moves it here, off the wire (the class notes).</summary>
            public bool Local;
            public float LocalSince;
            public float RestFor;

            /// <summary>The next placement is snapped, not swept (a local wreck handed back to the wire, a fallen wreck put back).</summary>
            public bool Rejoin;

            /// <summary>Where it was hit (on the road): a wreck 8 m below it has fallen through the world and is put back there.</summary>
            public Vector3 WreckFrom;

            /// <summary>Runner: its physics failed (fell or was flung); the sim's own slide moves it from here.</summary>
            public bool NoPhysics;

            /// <summary>Its hazard lamps blink: a wreck by the sim, the wire's flag, or a local hit.</summary>
            public bool Hazard;

            /// <summary>Its boxes are held off: they would have come on inside one of this client's vehicles.</summary>
            public bool Ghost;
        }

        /// <summary>
        /// What can hit a car on this client: the own truck and trailer (bodies this client simulates) and wrecks this
        /// client's physics moves faster than 1.5 m/s. Box centre, flat forward, velocity, half length and width, heights.
        /// </summary>
        private struct Hitter
        {
            public Vector3 C, F, V;
            public float HL, HW, Y0, Y1, Radius;

            /// <summary>A wreck's global slot; -1 for a truck or trailer.</summary>
            public int Slot;

            public bool Trailer;
        }

        /// <summary>One of this client's own vehicles (from Gather): its box and body.</summary>
        private struct Solid
        {
            public Transform T;
            public BoxCollider Box;
            public Rigidbody Body;
            public bool Trailer;
        }

        private readonly List<Solid> _solids = new List<Solid>();
        private readonly List<Hitter> _hitters = new List<Hitter>();
        private Hitter _lastHitter;
        private Vector3 _lastHitVelocity;
        private int _hitsLogged, _hitsHere, _ghosts;

        private const int Shown = 0, NotShown = 1, WasHit = 2;

        private Slot[] _slots = new Slot[0];
        private readonly List<EtsTrafficSim.Focus> _foci = new List<EtsTrafficSim.Focus>();
        private readonly List<EtsTrafficSim.Obstacle> _obstacles = new List<EtsTrafficSim.Obstacle>();
        private readonly List<Vector3> _keep = new List<Vector3>();
        private int _intsWritten;
        private float _reportAt;
        private float _dropAt;
        private float _playersRetryAt;
        private EtsTruckController _truckFound;
        private float _truckSearchAt;
        private Vector3 _me;
        private int _meFrame = -1;
        private bool _prewarmReported;
        private float _prewarmStart;

        // Sectors: which exist on the map (from its listing), which are read, and when a build
        // first wanted one that was not.
        private HashSet<long> _existing;
        private readonly HashSet<long> _loaded = new HashSet<long>();
        private int _loadedFrame = -1;
        private readonly Dictionary<long, float> _wanted = new Dictionary<long, float>();
        private float _nextOwnRead;
        private int _ownReads;

        // What the traffic spent: this frame so far, the worst whole frame, the worst step.
        private readonly Stopwatch _spent = new Stopwatch();
        private readonly Stopwatch _clock = new Stopwatch();
        private readonly Stopwatch _step = new Stopwatch();
        private int _spentFrame = -1;
        private double _worstFrameMs, _worstStepMs, _worstWorkMs;

        private void Awake()
        {
            _untilBegun = () => _graph == null;
            _withinBudget = () => _clock.Elapsed.TotalMilliseconds < _budgetMs;
            _playersBelow = () => PlayersBelow(LocalPlayerId());

            if (Instance != null && Instance != this)
            {
                Debug.LogWarning("[SomniumEuroTruck] Traffic: a second EtsTraffic on '" + name + "' is switched off; the one on '" + Instance.name + "' runs the cars.");
                enabled = false;
                return;
            }

            Instance = this;
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
            _worker?.Dispose();
            _models?.Destroy();

            if (_root != null)
            {
                Destroy(_root.gameObject);
            }
        }

        // ---------------------------------------------------------------- what it cost

        private void Enter()
        {
            if (Time.frameCount != _spentFrame)
            {
                _worstFrameMs = Math.Max(_worstFrameMs, _spent.Elapsed.TotalMilliseconds);
                _spent.Reset();
                _spentFrame = Time.frameCount;
            }

            _spent.Start();
        }

        private void Exit()
        {
            _spent.Stop();
        }

        // ---------------------------------------------------------------- setting up

        /// <summary>
        /// Once the archives are mounted and the map is built: the catalog is read on the worker
        /// (37 ms offline, Program1402, with the country's cars' bounds), then the lanes, the
        /// pool and the simulation are made here.
        /// </summary>
        private void TryStart()
        {
            EtsMapViewer viewer = EtsMapViewer.Current;

            if (_starting || !EtsEngine.IsMounted || viewer == null || viewer.Map == null || !EtsMapViewer.AnyCompleted)
            {
                return;
            }

            _starting = true;
            _worker = UseWorkerThread ? new EtsWorker("Ets traffic reader") : null;

            if (_worker != null && !_worker.Threaded)
            {
                Debug.LogWarning("[SomniumEuroTruck] Traffic: no background thread (" + _worker.Failure + "); file reads run on the frame, one a frame.");
            }

            EtsFileSystem files = EtsEngine.Files;
            string country = new EtsTrafficSim.Settings().Country;
            var clock = Stopwatch.StartNew();

            Func<object> readCatalog = () =>
            {
                var catalog = new EtsTrafficCatalog(files);
                catalog.Prepare(country);
                return catalog;
            };

            if (_worker == null)
            {
                Begin((EtsTrafficCatalog)readCatalog(), viewer, clock);
                return;
            }

            _worker.Post(readCatalog, (result, error) =>
            {
                if (result is EtsTrafficCatalog catalog)
                {
                    Begin(catalog, viewer, clock);
                }
                else
                {
                    Debug.LogWarning("[SomniumEuroTruck] Traffic: the car table could not be read" + (error != null ? ": " + error.Message : "") + ". No traffic.");
                }
            });
        }

        private void Begin(EtsTrafficCatalog catalog, EtsMapViewer viewer, Stopwatch clock)
        {
            if (viewer == null || viewer.Map == null || !EtsEngine.IsMounted)
            {
                return;
            }

            _catalog = catalog;
            _existing = ExistingSectors(viewer.Map);
            _graph = new EtsLaneGraph(viewer.Map, EtsEngine.Files, _catalog, viewer.CenterGame) { MayRead = MayRead };

            // Traffic lights: each signalised junction's programmes are registered as its lanes are placed (the map
            // viewer registers the ones it draws too), so the cars stop at the lights the players see (LightAt).
            EtsMap lightsMap = viewer.Map;
            _graph.PrefabWithLights = prefab => EtsSemaphores.Prepare(lightsMap, prefab);

            if (_worker != null)
            {
                EtsFileSystem files = EtsEngine.Files;
                EtsLaneGraph graph = _graph;
                _graph.RequestNav = path => _worker.Post(() => ReadNav(files, path), (result, error) =>
                {
                    var nav = result as EtsPrefabNav;
                    if (nav == null) NavFailed(path, error != null ? error.GetType().Name + ": " + error.Message : result as string);
                    graph.AddNav(path, nav);
                });
            }

            _root = new GameObject("Ets Traffic").transform;
            _models = new EtsTrafficModels(_catalog, _root, _worker);
            int capacity = MaxBlocks * EtsTrafficWire.CarsPerBlock;
            _slots = new Slot[capacity];
            for (int i = 0; i < capacity; i++) _slots[i] = new Slot { Index = i };

            _sim = new EtsTrafficSim(_graph, _catalog, new EtsTrafficSim.Settings
            {
                Capacity = capacity,
                MaxCars = Math.Min(MaxCars, capacity),
                PerFocus = PerFocus
            }, Environment.TickCount) { VehicleReady = _models.IsReady, Light = LightAt };

            if (Prewarm)
            {
                foreach (int vehicle in _sim.Pool) _models.Want(vehicle);
            }

            _startedAt = Time.time;
            _prewarmStart = Time.time;
            Debug.Log("[SomniumEuroTruck] Traffic: " + _catalog.Vehicles.Count + " cars in the table (hash " + _catalog.TableHash + "), " + _sim.Pool.Count + " in " + _sim.Config.Country + "'s mix, read in " +
                      clock.ElapsedMilliseconds + " ms; background reads " + (_worker == null ? "on the frame" : _worker.Threaded ? "on a worker thread" : "on the frame (no thread)") +
                      "; budget " + FrameBudgetMs.ToString("0.0") + " ms a frame.");
        }

        private bool _lightsFailed;

        /// <summary>
        /// The cars' Light hook (EtsTrafficSim.Light): what a junction's light shows now, from EtsSemaphores' shared clock
        /// - the lamps the map draws run on the same programme, so a car stops at the red the player sees. A programme
        /// that could strand a car (under 3 s of green, or a red over two minutes) counts as no light. Any failure turns
        /// the lights off for the cars, logged once; the junctions still run on their reservations.
        /// </summary>
        private int LightAt(ulong item, int semaphore)
        {
            if (_lightsFailed)
            {
                return EtsTrafficSim.LightGo;
            }

            try
            {
                if (!EtsSemaphores.TryGetProgram(item, semaphore, out EtsSemaphores.Program program) || program == null || program.Green < 3f || program.Red > 120f)
                {
                    return EtsTrafficSim.LightGo;
                }

                switch (EtsSemaphores.State(item, semaphore))
                {
                    case EtsSemaphores.Light.Red:
                    case EtsSemaphores.Light.RedYellow:
                        return EtsTrafficSim.LightStop;
                    case EtsSemaphores.Light.YellowToRed:
                        return EtsTrafficSim.LightStopIfAble;
                    default:
                        return EtsTrafficSim.LightGo;
                }
            }
            catch (Exception e)
            {
                _lightsFailed = true;
                Debug.LogWarning("[SomniumEuroTruck] Traffic: the traffic lights could not be read, so the cars ignore them from now on: " + e.GetType().Name + ": " + e.Message);
                return EtsTrafficSim.LightGo;
            }
        }

        /// <summary>Worker: a junction descriptor, or why not (Parse's own text, which 'out _' used to drop).</summary>
        private static object ReadNav(EtsFileSystem files, string path)
        {
            EtsPrefabNav nav = EtsPrefabNav.Parse(files.Read(path), out string failure);
            return (object)nav ?? failure ?? "no reason given";
        }

        /// <summary>
        /// A descriptor that could not be read builds its junction with no lanes (a descriptor
        /// with no curves is not a failure: Parse returns it empty). Once per path, the first ten
        /// in the log, the count in the 10 s report.
        /// </summary>
        private void NavFailed(string path, string why)
        {
            if (!_navFailed.Add(path) || _navFailed.Count > 10)
            {
                return;
            }

            Debug.LogWarning("[SomniumEuroTruck] Traffic: junction descriptor " + path + " could not be read (" + why + "); its junction gets no lanes." +
                             (_navFailed.Count == 10 ? " Further ones are only counted (10 s report)." : ""));
        }

        private static HashSet<long> ExistingSectors(EtsMap map)
        {
            var set = new HashSet<long>();

            foreach (string name in map.AvailableSectors)
            {
                // "sec+0003-0004", as EtsJobs reads them.
                if (name.Length >= 13 && int.TryParse(name.Substring(3, 5), out int x) && int.TryParse(name.Substring(8, 5), out int z))
                {
                    set.Add(EtsLaneGraph.SectorKey(x, z));
                }
            }

            return set;
        }

        /// <summary>
        /// EtsLaneGraph.MayRead: a sector may be touched when it is read already or the map has no
        /// such sector (nothing to read). One not read yet is left to EtsJobs (it reads the whole
        /// map a few sectors a frame at start) or the map's streaming, and read here only after
        /// SectorWait seconds, at most one every two seconds (a sector parse is 43 ms on average,
        /// up to 308 ms, Program1400 - once, where nothing else would have read it).
        /// </summary>
        private bool MayRead(int sx, int sz)
        {
            long key = EtsLaneGraph.SectorKey(sx, sz);

            if (!_existing.Contains(key))
            {
                return true;
            }

            if (_loadedFrame != Time.frameCount)
            {
                _loadedFrame = Time.frameCount;
                _loaded.Clear();
                foreach (EtsMapSector sector in _graph.Map.LoadedSectors) _loaded.Add(EtsLaneGraph.SectorKey(sector.SectorX, sector.SectorZ));
            }

            if (_loaded.Contains(key))
            {
                return true;
            }

            if (!_wanted.TryGetValue(key, out float since))
            {
                _wanted[key] = Time.time;
                return false;
            }

            if (Time.time - since < SectorWait || Time.time < _nextOwnRead)
            {
                return false;
            }

            _nextOwnRead = Time.time + 2f;
            _ownReads++;
            var read = Stopwatch.StartNew();
            _graph.Map.GetSector(sx, sz);
            _loaded.Add(key);
            Debug.Log("[SomniumEuroTruck] Traffic: sector " + sx + "," + sz + " was not read by anything else in " + SectorWait + " s; read it for the lanes (" + read.ElapsedMilliseconds + " ms).");
            return true;
        }

        private static bool Valid(NetworkBehaviour behaviour)
        {
            return behaviour != null && behaviour.Object != null && behaviour.Object.IsValid;
        }

        private bool Online
        {
            get
            {
                if (!SceneNetworking.IsNetworkReady || _blocks.Count == 0) return false;
                foreach (NetworkBridgeData block in _blocks) if (!Valid(block)) return false;
                return true;
            }
        }

        /// <summary>The blocks by name, as EtsNet.Find does, retried until the room makes them live.</summary>
        private void Find()
        {
            if (_blocks.Count > 0 || Time.time < _nextFind)
            {
                return;
            }

            _nextFind = Time.time + 1f;
            var found = new SortedDictionary<int, NetworkBridgeData>();

            foreach (NetworkBridgeData data in FindObjectsByType<NetworkBridgeData>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (data.name.StartsWith(BlockPrefix, StringComparison.Ordinal) && int.TryParse(data.name.Substring(BlockPrefix.Length), out int n) && n >= 0 && n < MaxBlocks)
                {
                    if (data.IntCount < EtsTrafficWire.Ints)
                    {
                        Debug.LogWarning("[SomniumEuroTruck] Traffic: '" + data.name + "' holds " + data.IntCount + " ints; it must be a NetworkBridgeData64.");
                        continue;
                    }

                    found[n] = data;
                }
            }

            if (found.Count == 0)
            {
                return;
            }

            _blocks.AddRange(found.Values);
            int count = _blocks.Count;
            _read = new int[count][];
            _shadow = new int[count][];
            _seen = new long[count];
            _seq = new int[count];
            _beatAt = new float[count];
            _seeded = new bool[count];
            _authority = new EtsTrafficAuthority(count);
            _mine = new bool[count];
            _silent = new bool[count];

            for (int i = 0; i < count; i++)
            {
                _read[i] = new int[EtsTrafficWire.Ints];
                _shadow[i] = new int[EtsTrafficWire.Ints];
                _seen[i] = long.MinValue;
                _silent[i] = true;
            }

            Debug.Log("[SomniumEuroTruck] Traffic: " + _blocks.Count + " of " + MaxBlocks + " network blocks found (" + _blocks.Count * EtsTrafficWire.CarsPerBlock + " car slots).");
        }

        /// <summary>
        /// Who runs the traffic (the class notes; the rules are EtsTrafficAuthority's). Only
        /// reached once this client's traffic is built (Update returns before it until then), so
        /// holding every block is enough to run it, master or not: a client that cannot run it
        /// never asks, and only gets blocks as the room's master (SceneNetworking), which the
        /// takeover then overrides.
        /// </summary>
        private Role Decide()
        {
            if (!Online)
            {
                _authority.Offline();
                return Role.Local;
            }

            for (int b = 0; b < _blocks.Count; b++)
            {
                _mine[b] = _blocks[b].Object.HasStateAuthority;
            }

            // Looked at four times a second (the player list is walked only while the room is
            // silent), asked at most once a second.
            bool due = Time.time >= _nextAsk;
            if (due) _nextAsk = Time.time + 0.25f;

            bool run = _authority.Decide(_mine, LocalPlayerId(), Current == Role.Master, SceneNetworking.IsMasterClient, Time.time, due ? _playersBelow : null, out int ask, out bool began);

            if (began)
            {
                Debug.Log("[SomniumEuroTruck] Traffic: no traffic block has changed for " + _authority.Silence(Time.time).ToString("0.0") + " s (its runner left, stopped, or cannot run it); player " +
                          LocalPlayerId() + " (" + _playersBelow() + " in the room before it) takes the traffic over.");
            }

            if (ask != 0)
            {
                _nextAsk = Time.time + 1f;

                for (int b = 0; b < _blocks.Count; b++)
                {
                    if ((ask & (1 << b)) != 0) _blocks[b].Object.RequestStateAuthority();
                }
            }

            return run ? Role.Master : Role.Watcher;
        }

        private static int LocalPlayerId()
        {
            NetworkRunner runner = SceneNetworking.NetworkRunnerRef;
            return runner != null ? runner.LocalPlayer.PlayerId : 0;
        }

        /// <summary>
        /// This client's place in the takeover queue: players in the room with a lower PlayerId,
        /// except those holding a block (while the room is silent, that is the runner that went
        /// quiet or a master that cannot run the traffic).
        /// </summary>
        private int PlayersBelow(int me)
        {
            int below = 0;

            try
            {
                NetworkRunner runner = SceneNetworking.NetworkRunnerRef;

                if (runner != null)
                {
                    foreach (PlayerRef player in runner.ActivePlayers)
                    {
                        if (player.IsRealPlayer && player.PlayerId < me && !_authority.HoldsAny(player.PlayerId)) below++;
                    }
                }
            }
            catch (Exception)
            {
                // No list yet: first in line; the contest rule settles a tie.
            }

            return below;
        }

        /// <summary>
        /// Every frame, every role: each block's header (three ints) and holder, for
        /// EtsTrafficAuthority, which notes when they change.
        /// </summary>
        private void Watch()
        {
            float now = Time.time;

            for (int b = 0; b < _blocks.Count; b++)
            {
                NetworkBridgeData block = _blocks[b];

                if (!Valid(block))
                {
                    continue;
                }

                NetworkArray<int> ints = block.IntArray;
                PlayerRef holder = block.Object.StateAuthority;
                _authority.See(b, ints.Get(EtsTrafficWire.AtMagic) == EtsTrafficWire.Magic, ints.Get(EtsTrafficWire.AtWriter), ints.Get(EtsTrafficWire.AtSeq), holder.IsNone ? -1 : holder.PlayerId, now);
            }

            if (Online)
            {
                _authority.Online(now);
            }
        }

        /// <summary>The block changed within StaleSeconds: somebody is writing it.</summary>
        private bool Fresh(int b)
        {
            return _authority.Fresh(b, Time.time);
        }

        // ---------------------------------------------------------------- the frame

        private void Update()
        {
            Enter();

            try
            {
                if (_graph == null)
                {
                    TryStart();

                    // The catalog comes back through the worker.
                    _worker?.Drain(_untilBegun);
                    return;
                }

                Find();
                Watch();
                Role role = Decide();

                if (role != Current)
                {
                    Change(Current, role);
                }

                Gather();

                if (Current == Role.Watcher)
                {
                    Read();
                }

                Blink();
                Background();
                Report();
            }
            finally
            {
                Exit();
            }
        }

        /// <summary>Everything that is not the step, under FrameBudgetMs: the worker's results, lanes, car models.</summary>
        private void Background()
        {
            _clock.Restart();
            double budget = FrameBudgetMs;
            _budgetMs = budget;

            _worker?.Drain(_withinBudget);

            // Lanes round every focus, on every client (a watcher may have to take over).
            foreach (EtsTrafficSim.Focus focus in _foci)
            {
                _graph.Want(focus.Position, LaneRadius);
            }

            if (Time.time >= _dropAt)
            {
                _dropAt = Time.time + 5f;
                _keep.Clear();
                foreach (EtsTrafficSim.Focus focus in _foci) _keep.Add(focus.Position);
                _keep.Add(LocalFocus());
                _graph.DropFar(_keep, LaneDropDistance);
            }

            // Turn about: lanes first on even frames, car models first on odd ones.
            if ((Time.frameCount & 1) == 0)
            {
                _graph.Work(_clock, budget);
                _models.Work(_clock, budget);
            }
            else
            {
                _models.Work(_clock, budget);
                _graph.Work(_clock, budget);
            }

            _worstWorkMs = Math.Max(_worstWorkMs, _clock.Elapsed.TotalMilliseconds);

            if (!_prewarmReported && Prewarm && _models.TemplatesPending == 0 && _models.TemplatesReady + _models.TemplatesFailed > 0)
            {
                _prewarmReported = true;
                var counts = new List<string>();
                foreach (int vehicle in _sim.Pool) counts.Add(_catalog.Vehicles[vehicle].Unit.Replace("traffic.", "") + " " + _models.RendererCounts(vehicle));
                Debug.Log("[SomniumEuroTruck] Traffic: " + _models.TemplatesReady + " car models ready (" + _models.TemplatesFailed + " failed) in " + (Time.time - _prewarmStart).ToString("0.0") +
                          " s; renderers per lod: " + string.Join(", ", counts.ToArray()) + ".");

                // The boxes the trucks meet: the lower body's width x length and the roof's height, metres.
                counts.Clear();
                foreach (int vehicle in _sim.Pool) counts.Add(_catalog.Vehicles[vehicle].Unit.Replace("traffic.", "") + " " + _models.FitSummary(vehicle) + " " + _catalog.Vehicles[vehicle].Mass.ToString("0") + " kg");
                Debug.Log("[SomniumEuroTruck] Traffic: car boxes fitted to the bodies (width x length / roof, mass): " + string.Join(", ", counts.ToArray()) + ".");
            }
        }

        private void FixedUpdate()
        {
            if (_graph == null)
            {
                return;
            }

            Enter();

            try
            {
                double now = Time.timeAsDouble;

                if (Current == Role.Local || Current == Role.Master)
                {
                    _step.Restart();
                    SyncWrecks();
                    _sim.Step(Time.fixedDeltaTime, _foci, _obstacles, now);
                    _worstStepMs = Math.Max(_worstStepMs, _step.Elapsed.TotalMilliseconds);
                    LogSimHits();
                    BuildHitters();
                    DrawSimulated();

                    if (Current == Role.Master && Time.time >= _nextWrite)
                    {
                        _nextWrite = Time.time + 1f / Mathf.Max(1f, WriteRate);
                        Write(now);
                    }
                }
                else if (Current == Role.Watcher)
                {
                    BuildHitters();
                    DrawWatched(now);
                }
            }
            finally
            {
                Exit();
            }
        }

        private void Change(Role from, Role to)
        {
            Debug.Log("[SomniumEuroTruck] Traffic: " + from + " -> " + to + ".");

            if (to == Role.Master)
            {
                _generation = (_generation + 1) & 0xFF;

                // Only slots that have a block behind them are networked.
                int networked = _blocks.Count * EtsTrafficWire.CarsPerBlock;
                _sim.Config.MaxCars = Math.Min(MaxCars, networked);

                // Carry on with the cars the room already has (a watcher that became master),
                // or with this client's own (a local run that joined as master).
                if (from == Role.Watcher)
                {
                    Adopt();
                }

                for (int i = networked; i < _sim.Slots.Length; i++)
                {
                    _sim.Drop(i);
                }

                // Every path in (from Watcher, Off, or Local - including Master -> Local -> Master
                // on an Online flicker, where cars removed meanwhile were never cleared on the
                // wire): the first Write takes each block's live ints as its shadow (Seed), so
                // every slot this client has no car for is written empty. A zeroed shadow left
                // the old runner's cars there, and the new header made watchers draw them as new
                // and frozen (Program1500: 10 of 10 slots, still 10 after 50 writes).
                for (int i = 0; i < _seeded.Length; i++)
                {
                    _seeded[i] = false;
                    _beatAt[i] = 0f;
                }
            }
            else if (to == Role.Watcher)
            {
                // Somebody else runs the traffic: this client's own cars go, the room's arrive.
                if (from == Role.Local || from == Role.Master)
                {
                    if (from == Role.Local)
                    {
                        foreach (Slot slot in _slots)
                        {
                            Release(slot);
                            slot.Alive = false;
                        }
                    }

                    _sim?.Clear();
                }

                foreach (Slot slot in _slots)
                {
                    slot.Timeline.Clear();
                    slot.Key = -1;

                    // A wreck this client's physics was moving as the runner stays with it here until it rests.
                    if (from == Role.Master && slot.Visual != null && slot.Visual.Dynamic)
                    {
                        slot.Local = true;
                        slot.LocalSince = Time.time;
                        slot.RestFor = 0f;
                    }
                }

                for (int i = 0; i < _seen.Length; i++) _seen[i] = long.MinValue;
            }
            else if (to == Role.Local && from == Role.Watcher)
            {
                // The room went away: the room's cars go, this client's own arrive.
                foreach (Slot slot in _slots)
                {
                    Release(slot);
                    slot.Alive = false;
                }
            }

            Current = to;
        }

        private void Adopt()
        {
            _sim.Clear();
            int kept = 0, lost = 0;

            for (int i = 0; i < _slots.Length; i++)
            {
                Slot slot = _slots[i];

                if (!slot.Alive)
                {
                    continue;
                }

                Vector3 at = slot.Visual != null ? slot.Visual.Body.position : slot.Last.Position;
                float yaw = slot.Visual != null ? slot.Visual.Body.rotation.eulerAngles.y : slot.Last.Yaw;

                // A wreck on the wire, or one this client's truck hit, stays a wreck (its dynamic body carries on under
                // the runner's rules: DrawSimulated).
                int flags = slot.Last.Flags | (slot.Local ? EtsTrafficSim.FlagWreck : 0);

                if (_sim.Adopt(i, slot.Last.Serial, slot.Last.Vehicle, slot.Last.Colour, at, yaw, slot.Last.Speed, flags))
                {
                    slot.Local = false;
                    kept++;
                }
                else
                {
                    Release(slot);
                    slot.Alive = false;
                    lost++;
                }
            }

            Debug.Log("[SomniumEuroTruck] Traffic: took over as master; " + kept + " cars carried on, " + lost + " could not be put on a lane (lanes built: " + _graph.Built + " sectors).");
        }

        // ---------------------------------------------------------------- who is where

        private void Gather()
        {
            _foci.Clear();
            _obstacles.Clear();
            _solids.Clear();
            bool fresh = _startedAt >= 0f && Time.time - _startedAt < 60f;

            if (Time.time >= _playersRetryAt)
            {
                try
                {
                    // The Bridge's List itself: through IEnumerable its enumerator was boxed every frame.
                    List<ISomniumPlayer> all = SomniumBridge.PlayersContainer?.AllPlayers;

                    if (all != null)
                    {
                        foreach (ISomniumPlayer player in all)
                        {
                            Transform root = player?.References?.Body?.Root;
                            if (root == null) continue;
                            AddFocus(root.position, fresh);

                            // A walker on the road: the cars stop for them.
                            _obstacles.Add(new EtsTrafficSim.Obstacle { Position = root.position, Forward = root.forward, HalfLength = 0.4f, HalfWidth = 0.4f, Player = true });
                        }
                    }
                }
                catch (Exception)
                {
                    // No client (the editor): do not throw every frame.
                    _playersRetryAt = Time.time + 5f;
                }
            }

            // Every player's truck and trailer ([net §2.8], PLAN §8 A3): the own truck (its Rigidbody's velocity) and each
            // other player's puppet (EtsNetVehicle: kinematic here, so its velocity is the drawn one, and its trailer is
            // rebuilt from the articulation), each a focus the cars stream round and an obstacle they stop for. One list,
            // indexed (no boxed enumerator).
            EtsNet net = EtsNet.Instance;
            IReadOnlyList<EtsVehicleView> vehicles = net != null ? net.AllVehicles : null;
            int count = vehicles != null ? vehicles.Count : 0;

            for (int i = 0; i < count; i++)
            {
                EtsVehicleView v = vehicles[i];

                if (v.Tractor == null)
                {
                    continue;
                }

                AddFocus(v.Tractor.position, fresh);
                AddVehicle(v.Tractor, v.Box, v.Velocity, v.Remote, false);

                // This client's own truck and trailer can hit cars here (BuildHitters); a puppet's hits are the sim's.
                if (!v.Remote)
                {
                    AddSolid(v.Tractor, v.Box, v.Tractor.GetComponent<Rigidbody>(), false);
                }

                if (v.Trailer != null)
                {
                    BoxCollider trailerBox = v.Trailer.GetComponent<BoxCollider>();
                    AddVehicle(v.Trailer.transform, trailerBox, v.Remote || v.Trailer.Body == null ? v.Velocity : Velocity(v.Trailer.Body), v.Remote, true);

                    if (!v.Remote)
                    {
                        AddSolid(v.Trailer.transform, trailerBox, v.Trailer.Body, true);
                    }
                }
            }

            // The old flow's scene truck when EtsNet has none (no EtsNet in the scene): found by a search every 2 s. Never in
            // the new flow, where it would find another player's puppet and count it a second time, standing still.
            if (count == 0 && !EtsFeatures.NewFlow)
            {
                if (Time.time >= _truckSearchAt)
                {
                    _truckSearchAt = Time.time + 2f;
                    _truckFound = FindFirstObjectByType<EtsTruckController>();
                }

                EtsTruckController truck = _truckFound;

                if (truck != null && truck.Placed)
                {
                    AddFocus(truck.transform.position, fresh);
                    AddVehicle(truck.transform, truck.BodyBox, Velocity(truck.Body), false, false);
                    AddSolid(truck.transform, truck.BodyBox, truck.Body, false);

                    if (truck.Trailer != null)
                    {
                        BoxCollider trailerBox = truck.Trailer.GetComponent<BoxCollider>();
                        AddVehicle(truck.Trailer.transform, trailerBox, Velocity(truck.Trailer.Body), false, true);
                        AddSolid(truck.Trailer.transform, trailerBox, truck.Trailer.Body, true);
                    }
                }
            }

            if (_foci.Count == 0)
            {
                Camera eye = Camera.main;
                if (eye != null) AddFocus(eye.transform.position, fresh);
            }
        }

        private static Vector3 Velocity(Rigidbody body)
        {
            return body != null && !body.isKinematic ? body.linearVelocity : Vector3.zero;
        }

        private void AddFocus(Vector3 p, bool fresh)
        {
            foreach (EtsTrafficSim.Focus f in _foci)
            {
                if ((f.Position - p).sqrMagnitude < 150f * 150f) return;
            }

            _foci.Add(new EtsTrafficSim.Focus { Position = p, Fresh = fresh });
        }

        private void AddVehicle(Transform t, BoxCollider box, Vector3 velocity, bool remote, bool trailer)
        {
            Vector3 size = box != null ? Vector3.Scale(box.size, t.lossyScale) : new Vector3(2.5f, 3.3f, 6.1f);
            Vector3 centre = box != null ? t.TransformPoint(box.center) : t.position;

            _obstacles.Add(new EtsTrafficSim.Obstacle
            {
                Position = centre,
                Forward = t.forward,
                Velocity = velocity,
                HalfLength = Mathf.Abs(size.z) * 0.5f,
                HalfWidth = Mathf.Abs(size.x) * 0.5f,
                Player = true,
                Remote = remote,
                Trailer = trailer
            });
        }

        private void AddSolid(Transform t, BoxCollider box, Rigidbody body, bool trailer)
        {
            if (t != null && box != null)
            {
                _solids.Add(new Solid { T = t, Box = box, Body = body, Trailer = trailer });
            }
        }

        // ---------------------------------------------------------------- hitting cars (the class notes)

        /// <summary>
        /// This step's hitters: the own truck and trailer at their bodies' poses (the physics pose, not the interpolated
        /// transform) with their collider boxes, and every wreck this client's physics moves at over 1.5 m/s.
        /// </summary>
        private void BuildHitters()
        {
            _hitters.Clear();

            for (int i = 0; i < _solids.Count; i++)
            {
                Solid s = _solids[i];

                if (s.T == null || s.Box == null || !s.Box.enabled || !s.Box.gameObject.activeInHierarchy)
                {
                    continue;
                }

                bool onBody = s.Body != null && s.Body.transform == s.Box.transform;
                Vector3 position = onBody ? s.Body.position : s.T.position;
                Quaternion rotation = onBody ? s.Body.rotation : s.T.rotation;
                Vector3 scale = s.T.lossyScale;
                Vector3 half = new Vector3(Mathf.Abs(s.Box.size.x * scale.x), Mathf.Abs(s.Box.size.y * scale.y), Mathf.Abs(s.Box.size.z * scale.z)) * 0.5f;
                Vector3 centre = position + rotation * Vector3.Scale(s.Box.center, scale);
                Vector3 velocity = s.Body != null && !s.Body.isKinematic ? s.Body.linearVelocity : Vector3.zero;
                AddHitter(centre, rotation * Vector3.forward, half, velocity, -1, s.Trailer);
            }

            for (int i = 0; i < _slots.Length; i++)
            {
                EtsTrafficModels.Visual visual = _slots[i].Visual;

                if (visual == null || !visual.Dynamic || visual.Body == null)
                {
                    continue;
                }

                Vector3 velocity = visual.Body.linearVelocity;

                if (velocity.sqrMagnitude < 1.5f * 1.5f)
                {
                    continue;
                }

                Quaternion rotation = visual.Body.rotation;
                AddHitter(visual.Body.position + rotation * visual.HullCentre, rotation * Vector3.forward, visual.HullHalf, velocity, i, false);
            }
        }

        private void AddHitter(Vector3 centre, Vector3 forward, Vector3 half, Vector3 velocity, int slot, bool trailer)
        {
            _hitters.Add(new Hitter
            {
                C = centre,
                F = EtsTrafficSim.Flat(forward),
                V = velocity,
                HL = half.z,
                HW = half.x,
                Y0 = centre.y - half.y,
                Y1 = centre.y + half.y,
                Radius = Mathf.Sqrt(half.x * half.x + half.z * half.z),
                Slot = slot,
                Trailer = trailer
            });
        }

        /// <summary>
        /// Whether a car moving from one pose to the next this step touches a hitter at any of the instants 0, half a
        /// step, ... two steps ahead (the car carrying on as it moves, the hitter along its velocity): its hull against
        /// their boxes in the ground plane, 3 cm apart, and their heights overlapping. Two steps, so the switch to a
        /// dynamic body comes before the physics step in which they would meet, whatever the frame rate.
        /// </summary>
        private bool WillHit(int self, EtsTrafficModels.Visual visual, Vector3 fromPosition, Quaternion fromRotation, Vector3 toPosition, Quaternion toRotation, float dt, out Hitter by)
        {
            by = default;
            Vector3 half = visual.HullHalf;
            Vector3 from = fromPosition + fromRotation * visual.HullCentre;
            Vector3 step = toPosition + toRotation * visual.HullCentre - from;
            Vector3 forwardFrom = EtsTrafficSim.Flat(fromRotation * Vector3.forward), forwardTo = EtsTrafficSim.Flat(toRotation * Vector3.forward);
            float radius = Mathf.Sqrt(half.x * half.x + half.z * half.z);

            for (int k = 0; k < _hitters.Count; k++)
            {
                Hitter o = _hitters[k];

                if (o.Slot == self)
                {
                    continue;
                }

                Vector3 rel = o.C - from;
                float reach = radius + o.Radius + (o.V.magnitude * dt + step.magnitude) * 2f + 0.5f;

                if (rel.x * rel.x + rel.z * rel.z > reach * reach)
                {
                    continue;
                }

                for (int n = 0; n <= 4; n++)
                {
                    float u = n * 0.5f;
                    Vector3 c = from + step * u;
                    Vector3 f = u < 1f ? EtsTrafficSim.Flat(Vector3.Lerp(forwardFrom, forwardTo, u)) : forwardTo;
                    Vector3 oc = o.C + o.V * (u * dt);
                    float rise = o.V.y * u * dt;

                    if (c.y + half.y < o.Y0 + rise || c.y - half.y > o.Y1 + rise)
                    {
                        continue;
                    }

                    if (EtsTrafficSim.Overlap2D(c, f, half.z, half.x, oc, o.F, o.HL, o.HW, 0.03f, out _, out _))
                    {
                        by = o;
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>Whether a car at this pose would be inside a hitter (with 'margin'): its boxes are not switched on there.</summary>
        private bool Inside(int self, EtsTrafficModels.Visual visual, Vector3 position, Quaternion rotation, float margin)
        {
            Vector3 half = visual.HullHalf;
            Vector3 c = position + rotation * visual.HullCentre;
            Vector3 f = EtsTrafficSim.Flat(rotation * Vector3.forward);

            for (int k = 0; k < _hitters.Count; k++)
            {
                Hitter o = _hitters[k];

                if (o.Slot == self || c.y + half.y < o.Y0 || c.y - half.y > o.Y1)
                {
                    continue;
                }

                if (EtsTrafficSim.Overlap2D(c, f, half.z, half.x, o.C, o.F, o.HL, o.HW, margin, out _, out _))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Whether any of this client's own vehicles has its box within 'distance' of a point.</summary>
        private bool OwnVehicleWithin(Vector3 p, float distance)
        {
            for (int k = 0; k < _hitters.Count; k++)
            {
                Hitter o = _hitters[k];
                if (o.Slot >= 0) continue;
                float r = distance + o.Radius;
                if ((o.C - p).sqrMagnitude < r * r) return true;
            }

            return false;
        }

        /// <summary>Runner: every wreck this client's physics moves hands the sim its pose before the step.</summary>
        private void SyncWrecks()
        {
            EtsTrafficSim.Car[] cars = _sim.Slots;

            for (int i = 0; i < _slots.Length && i < cars.Length; i++)
            {
                Slot slot = _slots[i];
                EtsTrafficModels.Visual visual = slot.Visual;
                EtsTrafficSim.Car car = cars[i];

                if (visual == null || !visual.Dynamic || car == null || !car.Wrecked || visual.Vehicle != car.Vehicle || slot.VisualSerial != (car.Serial & 0xF))
                {
                    continue;
                }

                Rigidbody body = visual.Body;
                Vector3 p = body.position, v = body.linearVelocity;

                // Through the world or flung: back where it was hit, standing, and never under physics again (the sim holds it).
                if (float.IsNaN(p.x) || p.y < slot.WreckFrom.y - 8f || v.sqrMagnitude > 45f * 45f)
                {
                    Debug.LogWarning("[SomniumEuroTruck] Traffic: the wreck in slot " + i + " left the road under physics (" + p.ToString("0.0") + ", " + v.magnitude.ToString("0.0") + " m/s); it is put back where it was hit.");
                    EtsTrafficModels.MakeKinematic(visual);
                    slot.NoPhysics = true;
                    slot.Rejoin = true;
                    _sim.SetWreckPose(i, slot.WreckFrom, car.Direction, Vector3.zero);
                    continue;
                }

                _sim.SetWreckPose(i, p, body.rotation * Vector3.forward, v);
            }
        }

        /// <summary>Hits the sim found with another player's truck (EtsTrafficSim.Collide), logged.</summary>
        private void LogSimHits()
        {
            List<EtsTrafficSim.Hit> hits = _sim.Hits;

            for (int k = 0; k < hits.Count; k++)
            {
                EtsTrafficSim.Hit hit = hits[k];
                EtsTrafficSim.Car car = hit.Slot >= 0 && hit.Slot < _sim.Slots.Length ? _sim.Slots[hit.Slot] : null;

                if (car != null)
                {
                    LogHit(hit.Slot, car.Vehicle, car.Serial, "another player's " + (hit.Trailer ? "trailer" : "truck") + " (in its box as drawn here)", hit.Closing,
                           "it brakes with its hazards on where the push leaves it; the room sees it on the wire");
                }
            }

            hits.Clear();
        }

        private void LogHit(int index, int vehicle, int serial, string by, float closing, string then)
        {
            if (++_hitsLogged > 40)
            {
                return;
            }

            EtsTrafficCatalog.Vehicle v = vehicle >= 0 && vehicle < _catalog.Vehicles.Count ? _catalog.Vehicles[vehicle] : null;
            Debug.Log("[SomniumEuroTruck] Traffic: car hit - " + (v != null ? v.Unit.Replace("traffic.", "") + ", " + v.Mass.ToString("0") + " kg" : "car " + vehicle) + " (slot " + index + ", serial " + (serial & 0xF) +
                      ") by " + by + " at " + (Mathf.Abs(closing) * 3.6f).ToString("0") + " km/h; " + then + "." + (_hitsLogged == 40 ? " Further hits are only counted (10 s report)." : ""));
        }

        private static string HitterName(Hitter by)
        {
            return by.Slot >= 0 ? "the wreck in slot " + by.Slot : by.Trailer ? "this client's trailer" : "this client's truck";
        }

        /// <summary>The hazard lamps of every wreck drawn here, about 75 blinks a minute.</summary>
        private void Blink()
        {
            bool on = Time.time % 0.8f < 0.42f;

            for (int i = 0; i < _slots.Length; i++)
            {
                Slot slot = _slots[i];

                if (slot.Visual != null)
                {
                    EtsTrafficModels.SetLamps(slot.Visual, slot.Hazard && on);
                }
            }
        }

        // ---------------------------------------------------------------- telling the runner (the Requests hook)

        /// <summary>
        /// Watcher: the last car this client's vehicle hit, packed for this player's own slot so the runner can make it a
        /// wreck in the room's traffic too (EtsNet writes it into a spare int of its slot and hands every other slot's to
        /// ApplyHitReport): bits 0-7 the car's global slot, 8-11 its serial, 16-23 a count that changes with every hit,
        /// bit 30 set. 0 until this client hits a car.
        /// </summary>
        public int HitReport { get; private set; }

        private int _hitReports;
        private readonly Dictionary<int, int> _hitSeen = new Dictionary<int, int>();

        private void ReportHit(Slot slot)
        {
            _hitReports = (_hitReports + 1) & 0xFF;
            HitReport = (1 << 30) | ((_hitReports & 0xFF) << 16) | ((slot.VisualSerial & 0xF) << 8) | (slot.Index & 0xFF);
        }

        /// <summary>
        /// Another player's HitReport as read from their slot, every time it is read (any role; only the runner acts). A
        /// report is acted on when it changes: the first value seen from a player is only noted (it may be a hit from
        /// before this client ran the traffic). The car must still be the one hit (its serial); it becomes a wreck,
        /// braking to a stop, and the wire tells the room.
        /// </summary>
        public void ApplyHitReport(int playerId, int packed)
        {
            int count = (packed & (1 << 30)) != 0 ? (packed >> 16) & 0xFF : -1;

            if (!_hitSeen.TryGetValue(playerId, out int seen))
            {
                _hitSeen[playerId] = count;
                return;
            }

            if (seen == count)
            {
                return;
            }

            _hitSeen[playerId] = count;

            if (count < 0 || _sim == null || !(Current == Role.Master || Current == Role.Local))
            {
                return;
            }

            int index = packed & 0xFF, serial = (packed >> 8) & 0xF;
            EtsTrafficSim.Car car = index < _sim.Slots.Length ? _sim.Slots[index] : null;

            if (car == null || (car.Serial & 0xF) != serial)
            {
                return;
            }

            if (_sim.Wreck(index, EtsTrafficSim.Flat(car.Direction) * car.V * 0.3f))
            {
                LogHit(index, car.Vehicle, car.Serial, "player " + playerId + "'s truck (their report)", car.V, "it stops with its hazards on; the room sees it on the wire");
            }
        }

        // ---------------------------------------------------------------- the runner draws and writes

        private void DrawSimulated()
        {
            Vector3 me = LocalFocus();
            EtsTrafficSim.Car[] cars = _sim.Slots;

            for (int i = 0; i < _slots.Length && i < cars.Length; i++)
            {
                Slot slot = _slots[i];
                EtsTrafficSim.Car car = cars[i];

                if (car == null)
                {
                    Release(slot);
                    slot.Alive = false;
                    slot.Hazard = false;
                    continue;
                }

                slot.Alive = true;
                slot.Local = false;
                slot.Hazard = car.Wrecked;
                slot.Last = new EtsTrafficWire.Car { Alive = true, Position = car.Position, Yaw = car.Yaw, Speed = car.V, Vehicle = car.Vehicle, Colour = car.Colour, Serial = car.Serial, Flags = car.Flags };
                EtsTrafficModels.Visual visual = slot.Visual;
                bool snap = slot.Rejoin;
                slot.Rejoin = false;

                if (visual != null && visual.Dynamic)
                {
                    if (car.Wrecked && visual.Vehicle == car.Vehicle && slot.VisualSerial == (car.Serial & 0xF))
                    {
                        // Physics moves it (SyncWrecks gave the sim its pose), within ColliderRadius + 20 m of this client.
                        float far = ColliderRadius + 20f;

                        if ((visual.Body.position - me).sqrMagnitude <= far * far)
                        {
                            Rigidbody body = visual.Body;
                            EtsTrafficModels.Roll(visual, Vector3.Dot(body.linearVelocity, body.rotation * Vector3.forward), 0f, Time.fixedDeltaTime);
                            continue;
                        }
                    }

                    // A wreck that drives on (Recover put it back on its lane), another car in the slot, or a wreck out of
                    // reach (the sim's slide from its last pose): kinematic again, placed where the sim has it.
                    EtsTrafficModels.MakeKinematic(visual);
                    snap = true;
                }

                int placed = Place(slot, car.Position, car.Yaw, car.Direction.y, car.V, car.Vehicle, car.Colour, car.Serial, snap, me);

                if (placed == WasHit)
                {
                    slot.Hazard = true;
                    slot.WreckFrom = car.Position;
                    _hitsHere++;

                    if (_sim.Wreck(i, _lastHitVelocity))
                    {
                        LogHit(i, car.Vehicle, car.Serial, HitterName(_lastHitter), (_lastHitter.V - _lastHitVelocity).magnitude,
                               "physics pushes it at its own weight from here, braking with its hazards on; the room sees it on the wire");
                    }
                }
                else if (placed == Shown && car.Wrecked && !slot.NoPhysics && slot.Visual != null && !slot.Visual.Dynamic && EtsTrafficModels.IsSolid(slot.Visual))
                {
                    // A wreck the sim moved (another player's truck hit it, it was adopted, or it came back within reach):
                    // physics takes it on from here.
                    slot.WreckFrom = car.Position;
                    EtsTrafficModels.MakeDynamic(slot.Visual, car.WreckVelocity, Vector3.zero);
                }
            }
        }

        /// <summary>
        /// The block as the watchers have it becomes this writer's shadow (EtsTrafficWire.Seed:
        /// the header is rewritten at once, and every slot this client has no car for is sent
        /// empty), and the block's sequence carries on, so no watcher mistakes the first write
        /// for one it has seen. _read is free here: only a watcher reads into it.
        /// </summary>
        private void Seed(int b)
        {
            NetworkArray<int> live = _blocks[b].IntArray;
            int[] copy = _read[b];

            for (int j = 0; j < EtsTrafficWire.Ints; j++)
            {
                copy[j] = live.Get(j);
            }

            _seq[b] = EtsTrafficWire.Seed(copy, _shadow[b]);
            _beatAt[b] = 0f;
            _seeded[b] = true;
        }

        private void Write(double now)
        {
            EtsTrafficSim.Car[] cars = _sim.Slots;
            int writer = LocalPlayerId();
            int[] scratch = _scratch;

            for (int b = 0; b < _blocks.Count; b++)
            {
                NetworkBridgeData block = _blocks[b];

                if (!Valid(block) || !block.Object.HasStateAuthority)
                {
                    // Somebody else may write it meanwhile: seeded again when it comes back.
                    _seeded[b] = false;
                    continue;
                }

                NetworkArray<int> ints = block.IntArray;

                // Also seeded again when the block's header is not the one this client last wrote:
                // the block went and came back between two 10 Hz writes, so the test above never
                // saw it gone, and another runner wrote it meanwhile (it took over during a hitch
                // and this runner, back, asked for it again before that one's header arrived; a
                // higher PlayerId not writing yet is not deferred to). Unseeded, the other's writer
                // int and its cars in slots this sim has empty stayed on the wire under this
                // runner's writes, and watchers refused every sample on the other's later clock
                // (Program1791: header writer 3 under runner 2, a ghost car, 0 of 30 samples taken;
                // with this test 30 of 30, no ghost). A runner's own ints read back as written.
                if (!_seeded[b] || ints.Get(EtsTrafficWire.AtSeq) != _shadow[b][EtsTrafficWire.AtSeq] || ints.Get(EtsTrafficWire.AtWriter) != _shadow[b][EtsTrafficWire.AtWriter])
                {
                    Seed(b);
                }

                for (int k = 0; k < EtsTrafficWire.CarsPerBlock; k++)
                {
                    EtsTrafficSim.Car car = cars[b * EtsTrafficWire.CarsPerBlock + k];
                    var packed = new EtsTrafficWire.Car();

                    if (car != null)
                    {
                        packed = new EtsTrafficWire.Car { Alive = true, Position = car.Position, Yaw = car.Yaw, Speed = car.V, Vehicle = car.Vehicle, Colour = car.Colour, Serial = car.Serial, Flags = car.Flags };
                    }

                    EtsTrafficWire.PackCar(packed, scratch, EtsTrafficWire.At(k));
                }

                // Nothing moved: written anyway once a second (a new seq and stamp, 2 ints), or a
                // block of standing cars could not be told from a runner that has gone.
                if (!EtsTrafficWire.Due(scratch, _shadow[b], Time.time >= _beatAt[b]))
                {
                    continue;
                }

                _beatAt[b] = Time.time + EtsTrafficAuthority.HeartbeatSeconds;
                _seq[b]++;
                EtsTrafficWire.PackHeader(new EtsTrafficWire.Header { Seq = _seq[b], StampMs = (int)(now * 1000.0), Writer = writer, Generation = _generation, TableHash = _catalog.TableHash }, scratch);

                for (int j = 0; j < EtsTrafficWire.Ints; j++)
                {
                    if (scratch[j] != _shadow[b][j])
                    {
                        ints.Set(j, scratch[j]);
                        _shadow[b][j] = scratch[j];
                        _intsWritten++;
                    }
                }
            }
        }

        // ---------------------------------------------------------------- watchers read and draw

        private float _hashWarnedAt = -100f;

        private void Read()
        {
            double now = Time.timeAsDouble;

            for (int b = 0; b < _blocks.Count; b++)
            {
                if (!Valid(_blocks[b]))
                {
                    continue;
                }

                if (!Fresh(b))
                {
                    Silenced(b);
                    continue;
                }

                _silent[b] = false;
                NetworkArray<int> ints = _blocks[b].IntArray;
                int[] read = _read[b];

                for (int j = 0; j < EtsTrafficWire.Ints; j++)
                {
                    read[j] = ints.Get(j);
                }

                if (!EtsTrafficWire.UnpackHeader(read, out EtsTrafficWire.Header header))
                {
                    continue;
                }

                // A new write is a new sequence or a new writer (a new master may repeat a number).
                long seen = ((long)read[EtsTrafficWire.AtWriter] << 32) | (uint)header.Seq;

                if (seen == _seen[b])
                {
                    continue;
                }

                _seen[b] = seen;

                if (header.TableHash != _catalog.TableHash && Time.time - _hashWarnedAt > 60f)
                {
                    _hashWarnedAt = Time.time;
                    Debug.LogWarning("[SomniumEuroTruck] Traffic: the runner's car table differs from this install's (" + header.TableHash + " / " + _catalog.TableHash + "); models may not match.");
                }

                for (int k = 0; k < EtsTrafficWire.CarsPerBlock; k++)
                {
                    Slot slot = _slots[b * EtsTrafficWire.CarsPerBlock + k];
                    EtsTrafficWire.Car car = EtsTrafficWire.UnpackCar(read, EtsTrafficWire.At(k));

                    if (!car.Alive || car.Vehicle >= _catalog.Vehicles.Count)
                    {
                        slot.Alive = false;
                        continue;
                    }

                    EtsNetWire.State state = EtsTrafficWire.ToTimeline(car, header);

                    // The wire has no pitch: take the climb from the last sample of the same car,
                    // or the Hermite would flatten every slope into steps.
                    if (slot.Alive && slot.Key == state.Epoch && header.StampMs / 1000.0 > slot.LastStamp)
                    {
                        state.Velocity.y = (car.Position.y - slot.Last.Position.y) / (float)(header.StampMs / 1000.0 - slot.LastStamp);
                    }

                    slot.Key = state.Epoch;
                    slot.Alive = true;
                    slot.Last = car;
                    slot.LastStamp = header.StampMs / 1000.0;
                    slot.Timeline.Add(state, now);
                }
            }
        }

        /// <summary>
        /// A block nobody has written for StaleSeconds: its runner left, stopped, or never could
        /// run the traffic. Its cars leave the road here instead of standing frozen in their lanes
        /// with their boxes on (the timeline holds a car 0.25 s past its last sample, then for
        /// ever: Program1501, x 53.75 at 1 s, 60 s and 3600 s). They come back with the block's
        /// next write; a new runner that took over in time has carried them on.
        /// </summary>
        private void Silenced(int b)
        {
            bool had = false;

            for (int k = 0; k < EtsTrafficWire.CarsPerBlock; k++)
            {
                Slot slot = _slots[b * EtsTrafficWire.CarsPerBlock + k];

                if (!slot.Alive && slot.Key == -1)
                {
                    continue;
                }

                had |= slot.Alive;
                slot.Alive = false;
                slot.Key = -1;
                slot.Timeline.Clear();
            }

            _seen[b] = long.MinValue;

            if (!_silent[b])
            {
                _silent[b] = true;
                Debug.Log("[SomniumEuroTruck] Traffic: '" + _blocks[b].name + "' has not changed for " + EtsTrafficAuthority.StaleSeconds.ToString("0") + " s" + (had ? "; its cars leave the road here until it is written again." : "."));
            }
        }

        private void DrawWatched(double now)
        {
            Vector3 me = LocalFocus();

            foreach (Slot slot in _slots)
            {
                if (slot.Local && slot.Visual != null && KeepLocal(slot))
                {
                    continue;
                }

                if (!slot.Alive)
                {
                    Release(slot);
                    slot.Hazard = false;
                    continue;
                }

                if (!slot.Timeline.Sample(now, out EtsNetWire.State s))
                {
                    continue;
                }

                // A new epoch (another writer or generation: a takeover) or a jump of 50 m is
                // snapped to, not slid to (EtsNetTimeline). A new car, a new serial, is renewed in Place.
                // A local wreck handed back to the wire is snapped too.
                bool snap = slot.Timeline.WasSnap || slot.Rejoin;
                slot.Rejoin = false;
                slot.Hazard = (s.Flags & EtsTrafficSim.FlagWreck) != 0;
                float yaw = s.Rotation.eulerAngles.y;
                float climb = s.Velocity.magnitude > 0.5f ? s.Velocity.y / s.Velocity.magnitude : 0f;
                int serial = s.Epoch & 0xF;

                if (Place(slot, s.Position, yaw, climb, new Vector2(s.Velocity.x, s.Velocity.z).magnitude, (int)s.Rpm, s.Gear, serial, snap, me) == WasHit)
                {
                    // Off the wire here until it rests (KeepLocal); the runner hears of it through HitReport, if EtsNet carries it.
                    slot.Local = true;
                    slot.LocalSince = Time.time;
                    slot.RestFor = 0f;
                    slot.Hazard = true;
                    slot.WreckFrom = s.Position;
                    _hitsHere++;
                    ReportHit(slot);
                    LogHit(slot.Index, (int)s.Rpm, serial, HitterName(_lastHitter), (_lastHitter.V - _lastHitVelocity).magnitude,
                           "this client's physics pushes it at its own weight until it rests with none of this client's vehicles within 25 m, then it follows the traffic runner again");
                }
            }
        }

        /// <summary>
        /// A watcher's local wreck: kept under this client's physics (true) until it has rested 2 s, 4 s after the hit at
        /// the soonest, with none of this client's vehicles within 25 m (or it fell through the world); then it follows
        /// the wire again, snapped, its boxes off until clear of this client's vehicles (false). A car the wire no longer
        /// has in the slot, or another car there, releases it at once (false: the slot is drawn from the wire as usual).
        /// </summary>
        private bool KeepLocal(Slot slot)
        {
            EtsTrafficModels.Visual visual = slot.Visual;

            if (!slot.Alive || visual.Vehicle != slot.Last.Vehicle || slot.VisualSerial != (slot.Last.Serial & 0xF))
            {
                slot.Local = false;
                Release(slot);
                return false;
            }

            Rigidbody body = visual.Body;

            if (!visual.Dynamic || body == null)
            {
                slot.Local = false;
                slot.Rejoin = true;
                return false;
            }

            float dt = Time.fixedDeltaTime;
            Vector3 v = body.linearVelocity;
            slot.RestFor = v.sqrMagnitude < 0.09f ? slot.RestFor + dt : 0f;
            slot.Hazard = true;
            bool fell = body.position.y < slot.WreckFrom.y - 8f || float.IsNaN(body.position.x);

            if (fell || slot.RestFor > 2f && Time.time - slot.LocalSince > 4f && !OwnVehicleWithin(body.position, 25f))
            {
                EtsTrafficModels.MakeKinematic(visual);
                EtsTrafficModels.SetSolid(visual, false);
                slot.Local = false;
                slot.Rejoin = true;
                return false;
            }

            EtsTrafficModels.Roll(visual, Vector3.Dot(v, body.rotation * Vector3.forward), 0f, dt);
            return true;
        }

        // ---------------------------------------------------------------- the cars in the scene

        /// <summary>
        /// Draws a car at its pose: Shown, NotShown (too far, or no model yet), or WasHit - it was about to touch one of
        /// this client's hitters (WillHit) and is a dynamic body now, left where it is for the physics (the class notes).
        /// </summary>
        private int Place(Slot slot, Vector3 position, float yaw, float climb, float speed, int vehicle, int colour, int serial, bool snap, Vector3 me)
        {
            float d2 = (position - me).sqrMagnitude;
            float keep = slot.Visual != null ? DrawRadius + 50f : DrawRadius;

            if (d2 > keep * keep)
            {
                Release(slot);
                return NotShown;
            }

            // Another car in the slot: the sim retires a car and spawns the next into the same
            // slot in one step (Retire before Spawn, lowest free slot), and a watcher can miss the
            // empty write between. Same model or not, it gets a visual of its own, placed and
            // painted - kept, the old one was slid up to 1,070 m across the map in the old car's
            // colour (Program1735, the installed sim: 6 times in 8 x 900 s, 5 in the wrong colour;
            // now 0, and the most a visual moves in one step is 0.29 m). Serials survive a
            // takeover (EtsTrafficSim.Adopt), so a new runner does not renew every car.
            serial &= 0xF;

            if (slot.Visual != null && (slot.Visual.Vehicle != vehicle || slot.Visual.Colour != colour || slot.VisualSerial != serial))
            {
                Release(slot);
            }

            bool arrived = slot.Visual == null;

            if (arrived)
            {
                // Null until its model is ready, or while this frame has shown its share of cars:
                // asked again next step. The pool may hand the same object back: it is placed and
                // painted as an arrival.
                slot.Visual = _models.Take(vehicle, colour);
                if (slot.Visual == null) return NotShown;
                slot.VisualSerial = serial;
                slot.Ghost = false;
                slot.NoPhysics = false;
            }

            EtsTrafficModels.Visual visual = slot.Visual;
            float pitch = -Mathf.Asin(Mathf.Clamp(climb, -0.5f, 0.5f)) * Mathf.Rad2Deg;
            Quaternion rotation = Quaternion.Euler(pitch, yaw, 0f);
            Rigidbody body = visual.Body;
            float dt = Mathf.Max(Time.fixedDeltaTime, 1e-3f);

            if (arrived || snap)
            {
                // A new car, or one the network snapped, is placed, not moved there: MovePosition
                // would sweep the interpolated body across the gap and steer the wheels to the stops.
                body.position = position;
                body.rotation = rotation;
                visual.Root.transform.SetPositionAndRotation(position, rotation);
                slot.LastYaw = yaw;
            }

            float yawRate = Mathf.DeltaAngle(slot.LastYaw, yaw) / dt;
            slot.LastYaw = yaw;

            if (!arrived && !snap)
            {
                // Before the physics meets them: a car whose next move would touch this client's truck, trailer or a
                // moving wreck is a dynamic body of its own weight from now, moving as it was, so the contact is a real
                // one (the class notes). It is not moved; the physics does that.
                if (_hitters.Count > 0 && EtsTrafficModels.IsSolid(visual) && WillHit(slot.Index, visual, body.position, body.rotation, position, rotation, dt, out Hitter by))
                {
                    Vector3 velocity = Vector3.ClampMagnitude((position - body.position) / dt, 40f);
                    EtsTrafficModels.MakeDynamic(visual, velocity, new Vector3(0f, yawRate * Mathf.Deg2Rad, 0f));
                    _lastHitter = by;
                    _lastHitVelocity = velocity;

                    // This client's own truck or trailer (not a wreck shoving another car): the crash in the cab.
                    if (by.Slot < 0)
                    {
                        EtsTruckAudio.PlayCarHit((by.V - velocity).magnitude);
                    }

                    return WasHit;
                }

                body.MovePosition(position);
                body.MoveRotation(rotation);
            }

            EtsTrafficModels.Roll(visual, speed, yawRate, dt);
            bool solid = d2 < ColliderRadius * ColliderRadius;

            // No box comes on inside one of this client's vehicles: PhysX would push the truck out of it at up to the
            // depenetration speed. Held off ("ghost") and asked again each step, it comes on once they are apart.
            if (solid && !EtsTrafficModels.IsSolid(visual))
            {
                bool inside = _hitters.Count > 0 && Inside(slot.Index, visual, position, rotation, 0.1f);

                if (inside && !slot.Ghost)
                {
                    _ghosts++;
                }

                slot.Ghost = inside;
                solid = !inside;
            }

            EtsTrafficModels.SetSolid(visual, solid);
            return Shown;
        }

        private void Release(Slot slot)
        {
            if (slot.Visual != null)
            {
                _models.Give(slot.Visual);
                slot.Visual = null;
            }

            slot.VisualSerial = -1;
            slot.Local = false;
            slot.Ghost = false;
            slot.NoPhysics = false;
        }

        // ---------------------------------------------------------------- riders on cars (PLAN.md §7.A, for B's EtsRiders)

        /// <summary>
        /// The car drawn in a global slot (block * 15 + car, the wire index [net §2.8]) on this client, if it is the
        /// car with that serial: its body (the pooled clone's root, which the kinematic interpolated Rigidbody
        /// moves) and its box. False when the slot is empty here, not drawn here (farther than DrawRadius from this
        /// client's focus), or holds another car: a new car in a slot comes with the next 4-bit serial
        /// (EtsTrafficWire, int +3 bits 21-24), so a rider falls off rather than being carried off by the next
        /// car (Q-A2: riders ride AI cars too). The serial is compared in its 4 wire bits. Every role: the slot's
        /// visual and serial are set in Place, which the runner (DrawSimulated) and the watchers (DrawWatched)
        /// both use.
        /// </summary>
        public bool TryGetCarFrame(int globalSlot, int serial, out Transform body, out BoxCollider box)
        {
            body = null;
            box = null;

            // A negative serial ("none", as TryIdentifyCar's miss reports it) must not match serial 15 through the mask.
            if (globalSlot < 0 || globalSlot >= _slots.Length || serial < 0)
            {
                return false;
            }

            Slot slot = _slots[globalSlot];

            if (slot == null || !slot.Alive || slot.Visual == null || slot.Visual.Root == null || slot.VisualSerial != (serial & 0xF))
            {
                return false;
            }

            body = slot.Visual.Root.transform;
            box = slot.Visual.Box;
            return true;
        }

        /// <summary>
        /// Which drawn car a Rigidbody is (a ground ray under a walker hit it): its global slot and 4-bit serial,
        /// the pair TryGetCarFrame takes back and the rider record carries (kind 3, int 33). A walk over the 60
        /// slots, done on a hit, not every frame.
        /// </summary>
        public bool TryIdentifyCar(Rigidbody rb, out int globalSlot, out int serial)
        {
            globalSlot = -1;
            serial = -1;

            if (rb == null)
            {
                return false;
            }

            for (int i = 0; i < _slots.Length; i++)
            {
                Slot slot = _slots[i];

                if (slot != null && slot.Alive && slot.Visual != null && slot.Visual.Body == rb && slot.VisualSerial >= 0)
                {
                    globalSlot = i;
                    serial = slot.VisualSerial;
                    return true;
                }
            }

            return false;
        }

        /// <summary>Where this client stands: what its own map streams round (EtsTruckSeat.StreamFocus). Once a frame.</summary>
        private Vector3 LocalFocus()
        {
            if (_meFrame == Time.frameCount)
            {
                return _me;
            }

            _meFrame = Time.frameCount;
            EtsTruckSeat seat = EtsNet.Instance != null ? EtsNet.Instance.Seat : null;
            Transform focus = seat != null ? seat.StreamFocus : null;

            if (focus != null)
            {
                _me = focus.position;
            }
            else
            {
                Camera eye = Camera.main;
                _me = eye != null ? eye.transform.position : Vector3.zero;
            }

            return _me;
        }

        private void Report()
        {
            if (Time.time < _reportAt)
            {
                return;
            }

            bool first = _reportAt <= 0f;
            _reportAt = Time.time + 10f;

            if (first)
            {
                return;
            }

            int drawn = 0, alive = 0;

            foreach (Slot slot in _slots)
            {
                if (slot.Alive) alive++;
                if (slot.Visual != null) drawn++;
            }

            double worstFrame = Math.Max(_worstFrameMs, _spent.Elapsed.TotalMilliseconds);
            bool runs = Current == Role.Local || Current == Role.Master;

            // Cars standing at a red light now (their last step's reason), and whether the lights are live at all.
            int atLights = 0;

            if (runs && _sim != null)
            {
                foreach (EtsTrafficSim.Car car in _sim.Slots)
                {
                    if (car != null && car.Why == EtsTrafficSim.WhyLight) atLights++;
                }
            }

            string lights = runs ? ", " + atLights + " at red lights" + (_lightsFailed ? " (lights off: failed)" : "") : "";

            // Hits and wrecks: on the road now (the runner's), hit by this client ever, pushed here now, held see-through.
            int pushed = 0, local = 0;

            foreach (Slot slot in _slots)
            {
                if (slot.Visual != null && slot.Visual.Dynamic) pushed++;
                if (slot.Local) local++;
            }

            string wrecks = (runs && _sim != null ? ", " + _sim.WreckCount + " wrecks (" + _sim.Wrecks + " hit, " + _sim.Recovered + " drove on, " + _sim.RemovedWreck + " removed)" : "") +
                            (_hitsHere > 0 || pushed > 0 ? ", " + _hitsHere + " hit by this client, " + pushed + " moved by its physics" + (local > 0 ? " (" + local + " off the wire)" : "") : "") +
                            (_ghosts > 0 ? ", " + _ghosts + " car boxes held off inside a truck so far" : "");

            // Who writes the room's traffic: the writer of the newest block, or how long none has changed.
            string runner = "";

            if (Current == Role.Watcher)
            {
                int newest = -1;

                for (int b = 0; b < _authority.Count; b++)
                {
                    if (Fresh(b) && (newest < 0 || _authority.ChangedAt(b) > _authority.ChangedAt(newest))) newest = b;
                }

                runner = newest >= 0 ? " (written by player " + _authority.Writer(newest) + ")" : " (no block changed for " + _authority.Silence(Time.time).ToString("0") + " s)";
            }

            Debug.Log("[SomniumEuroTruck] Traffic: " + Current + runner + ", " + alive + " cars, " + drawn + " drawn here, " + _models.Built + " built (" +
                      _models.TemplatesReady + "/" + _sim.Pool.Count + " models ready" + (_models.TemplatesPending > 0 ? ", " + _models.TemplatesPending + " to go" : "") + "), lanes " +
                      _graph.LaneCount + " in " + _graph.Built + " sectors" + (_graph.Pending > 0 ? " (" + _graph.Pending + " to build)" : "") +
                      (Current == Role.Master ? ", " + (_intsWritten / 10) + " ints/s written" : "") +
                      (runs ? ", spawn lists " + _sim.CandidateLists + ", spawned " + _sim.Spawned : "") + lights + wrecks +
                      "; worst frame " + worstFrame.ToString("0.00") + " ms (step " + _worstStepMs.ToString("0.00") + ", background " + _worstWorkMs.ToString("0.00") + ")" +
                      (_worker != null && _worker.Waiting > 0 ? ", " + _worker.Waiting + " reads queued" : "") +
                      (_ownReads > 0 ? ", " + _ownReads + " sectors read here" : "") +
                      (_navFailed.Count > 0 ? ", " + _navFailed.Count + " junction descriptors unreadable" : "") + ".");

            _intsWritten = 0;
            _worstFrameMs = 0;
            _worstStepMs = 0;
            _worstWorkMs = 0;
        }
    }
}
