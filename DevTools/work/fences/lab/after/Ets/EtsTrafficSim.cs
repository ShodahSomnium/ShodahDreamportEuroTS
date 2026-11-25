using System;
using System.Collections.Generic;
using UnityEngine;

namespace SomniumSpace.Worlds.SomniumEuroTruck.Ets
{
    /// <summary>
    /// The traffic itself: cars on the lanes of EtsLaneGraph, run by ONE client (the room's
    /// master, or this client alone). Pure apart from Vector3 arithmetic, so the offline
    /// harness drives it on the real map.
    ///
    ///   - Following: the Intelligent Driver Model on the path ahead (this lane and the lanes
    ///     the car has chosen after it): the nearest car, a player's truck or trailer, a walker
    ///     on the lane, a junction the car may not enter yet, a slower bend or limit ahead.
    ///   - Junctions: a car chooses its way through a prefab when it reaches it (one of the
    ///     prefab's own curve chains, EtsPrefabNav.ChainsFrom) and must reserve the whole chain
    ///     before entering: granted when no car holds a curve that conflicts with it
    ///     (EtsPrefabNav.Conflict: crosses, merges, or passes within 2.2 m) and no earlier
    ///     waiting car asked for one (first come, first served). Curves are given back one by
    ///     one as the car leaves them. A car never waits for a reservation while holding one
    ///     inside the junction, and only the first car before a mouth asks (a car behind it
    ///     waits for that car, not for the junction), so reservations cannot deadlock. A car
    ///     inside a junction without a grant (adopted from the wire, or run in because it could
    ///     not stop) holds its way out as if granted.
    ///   - Spawning and removal follow the game's own distances (/def/traffic_data.sii:
    ///     spawn_distance_forward 600, backward 200, min 80; remove_distance_forward 700,
    ///     backward 250, min 150): a car appears out of sight of every player and goes when it
    ///     is far from all of them. 'Out of sight' is by distance here (the game also uses the
    ///     view cone and cut planes; not reproduced). Where a car may appear round each focus is
    ///     gathered a few hundred cells a step and kept for GatherEvery seconds (Candidates),
    ///     and only once every lane sector round the focus is built (EtsLaneGraph.IsAreaBuilt).
    ///     A step never reads a file: the country's cars' bounds come from
    ///     EtsTrafficCatalog.Prepare. Offline over 300 s round the start (traffic-install
    ///     TrafficTest, median of three cold runs) no step after the first second took 1 ms.
    /// Traffic lights (4,282 curves name a semaphore; profiles in semaphore_profile.sii) and
    /// priority rules are a second step: a light's phase can come from a shared clock, so it
    /// would cost no bandwidth.
    ///
    /// Players' vehicles and wrecks (2026-09-23, "fix the collisions with cars"):
    ///   - Every obstacle is a box (Boxes): a player's truck or trailer at its collider's real
    ///     size, swept SweepSeconds along its own velocity - forwards or reversing - so a car
    ///     keeps clear of where the truck is about to be, not only where it is. A car tests its
    ///     own footprint (its bounds, ObstacleMargin round the box) at its place and every 2 m of
    ///     its path (Overlap2D, a separating-axis test), and brakes for the first box it would
    ///     touch, the gap refined to half a metre. The old test took the path's centre line
    ///     against a box widened by the car and guessed the gap with at most 2 m of the box's
    ///     length, and never looked at where a reversing truck was going.
    ///   - A hit car is a WRECK (Wreck): it leaves its lane (its junction holds are given back),
    ///     brakes with its hazards on (WreckFlags on the wire), and its pose is no longer the
    ///     lane's: the host's physics moves it (SetWreckPose - EtsTraffic makes the drawn car a
    ///     dynamic Rigidbody with the car's own vehicle_mass), or, where nobody's physics holds
    ///     it, it slides to rest here (WreckFriction). It is an obstacle to the other cars, which
    ///     queue behind it or move over in their lane to pass it (Nudge, up to MaxNudge). Once it
    ///     has stood WreckRecover seconds still on a lane, lined up with it, with no player's
    ///     vehicle near, it drives on (Recover); one knocked off its lane or turned round stays
    ///     until it is far from every player, as any stuck car.
    ///   - The host tells the sim about hits by its own trucks (EtsTraffic: before the physics
    ///     meets them). Another player's truck, drawn here a network delay late and kinematic, is
    ///     found here: a car its box overlaps by HitDepth is hit (Collide), pushed by the closing
    ///     speed shared by the masses, and reported in Hits.
    /// </summary>
    public sealed class EtsTrafficSim
    {
        public sealed class Settings
        {
            public int Capacity = 60;
            public int MaxCars = 48;
            public int PerFocus = 24;
            public float SpawnFar = 650f;
            public float SpawnHidden = 320f;
            public float SpawnNearAtStart = 60f;
            public float Remove = 760f;
            public float HiddenForRemoval = 160f;
            public float SpawnsPerSecond = 4f;
            public float StuckSeconds = 45f;

            /// <summary>Whose traffic.sii the cars come from: the start (Szczecin) is in Poland.</summary>
            public string Country = "poland";

            /// <summary>A focus's spawn candidates are gathered again at most this often, seconds.</summary>
            public float GatherEvery = 2f;

            /// <summary>A focus that moved this far from where its candidates were gathered gets its own list, metres.</summary>
            public float GatherMove = 150f;

            /// <summary>Work per step for the gather in progress: 16 m cells swept, lanes checked (LeadsOn).</summary>
            public int GatherCellsPerStep = 700;
            public int GatherLanesPerStep = 120;

            /// <summary>
            /// A car that has waited this long at a junction gets first come, first served: no later
            /// car may take a curve it asked for. Sooner than that the junction is greedy. Measured
            /// offline round the start (TrafficTest, 300 s, ~30 cars): strict first come, first served
            /// had cars stopped 27 % of the time, after 8 s 34 %, after 20 s 24.5 %, after 30 s 17.9 %,
            /// never (greedy) 16 %, with no overlapping cars in any.
            /// </summary>
            public float FifoAfter = 30f;

            // IDM (Treiber); car figures, not from the game's files.
            public float MaxAcceleration = 1.6f;
            public float ComfortBraking = 2.5f;
            public float HardBraking = 8f;
            public float Headway = 1.4f;
            public float Standstill = 2.5f;

            // Around players' vehicles: more room, because the truck's pose reaches the master
            // a network delay late (EtsNetTimeline, 30-400 ms) and the car reaches the driver later still.
            public float PlayerHeadway = 2.2f;
            public float PlayerStandstill = 6f;

            /// <summary>An obstacle's box reaches this many seconds along its own velocity (either way), so a car keeps clear of where a truck is going.</summary>
            public float SweepSeconds = 0.8f;

            /// <summary>Room a car keeps round an obstacle's box, metres (its footprint may not come nearer).</summary>
            public float ObstacleMargin = 0.35f;

            /// <summary>Most a car moves sideways in its lane to pass a wreck, metres (a 4.5 m lane leaves a 1.8 m car 1.35 m each side), and how fast, m/s.</summary>
            public float MaxNudge = 1.3f;
            public float NudgeSpeed = 0.8f;

            /// <summary>Another player's truck or trailer overlapping a car by this much in these boxes hits it (Collide), metres.</summary>
            public float HitDepth = 0.15f;

            /// <summary>What the sim takes a player's truck to weigh in its own push (Collide), kg, and the bounce. The host's physics uses the real masses.</summary>
            public float PlayerMass = 12000f;
            public float Bounce = 0.2f;

            /// <summary>A wreck nobody's physics holds slides to rest at this deceleration, m/s2 (brakes locked).</summary>
            public float WreckFriction = 6f;

            /// <summary>A wreck drives on after standing still this long, seconds, when it still stands on a lane (Recover).</summary>
            public float WreckRecover = 15f;

            /// <summary>How near its lane's line (metres) and how well lined up with it (cosine) a wreck must stand to drive on.</summary>
            public float WreckRecoverOffset = 1.2f;
            public float WreckRecoverDot = 0.9f;

            /// <summary>No wreck drives on with a player's vehicle's box within this many metres of it.</summary>
            public float WreckRecoverClear = 8f;
        }

        public sealed class Car
        {
            public int Slot;
            public int Serial;
            public int Vehicle;
            public int Colour;
            public int Lane;
            public float S;
            public float V;
            public float Accel;
            public float Factor = 1f;
            public float HalfLength = 2.1f;
            public float HalfWidth = 0.9f;
            public readonly List<int> Route = new List<int>(8);
            public float Stuck;
            public int Flags;

            /// <summary>What held the car back on the last step (Why*), for the log.</summary>
            public int Why;

            // Junction bookkeeping: the prefab lanes this car holds, and a request waiting.
            public readonly List<int> Held = new List<int>(6);
            public List<int> Waiting;
            public double WaitingSince;

            /// <summary>The light-controlled lane this car has decided to stop before (it no longer asks whether it could).</summary>
            public int StopFor = -1;

            public Vector3 Position;
            public Vector3 Direction = Vector3.forward;
            public float Yaw;

            /// <summary>Hit (Wreck): off its lane, hazards on, its pose the host's physics' or its own slide's, until Recover or removal.</summary>
            public bool Wrecked;
            public double WreckedAt;
            public Vector3 WreckVelocity;

            /// <summary>Seconds the wreck has stood still, and when it may next try to drive on.</summary>
            public float RestTime;
            public double NextRecover;

            /// <summary>The host set this step's pose (SetWreckPose): the wreck does not slide on its own.</summary>
            public bool HostPose;

            /// <summary>Sideways offset from the lane's line (metres, + right), where it is heading, and how much farther it keeps it (metres of travel).</summary>
            public float Lateral;
            public float NudgeTarget;
            public float NudgeHold;
        }

        public struct Focus
        {
            public Vector3 Position;
            public bool Fresh;
        }

        public struct Obstacle
        {
            /// <summary>The box's centre (a vehicle's collider centre, a walker's feet).</summary>
            public Vector3 Position;
            public Vector3 Forward;
            public Vector3 Velocity;
            public float HalfLength;
            public float HalfWidth;
            public bool Player;

            /// <summary>Another player's truck or trailer (a puppet drawn here): Collide finds its hits; the own truck's are the host's.</summary>
            public bool Remote;

            public bool Trailer;
        }

        /// <summary>A car hit by another player's vehicle in the sim's own boxes (Collide), for the host's log.</summary>
        public struct Hit
        {
            public int Slot;
            public bool Trailer;
            public float Closing;
        }

        public const int FlagBrake = 1;
        public const int FlagBlinkLeft = 2;
        public const int FlagBlinkRight = 4;

        /// <summary>A wreck (the wire's fourth flag bit; the wire carries six).</summary>
        public const int FlagWreck = 8;

        /// <summary>A wreck's flags: braking, both blinkers (hazards), wrecked.</summary>
        public const int WreckFlags = FlagBrake | FlagBlinkLeft | FlagBlinkRight | FlagWreck;

        public const int WhyFree = 0, WhyCar = 1, WhyLimit = 2, WhyJunction = 3, WhyDeadEnd = 4, WhyObstacle = 5, WhyLight = 6, WhyWreck = 7;

        /// <summary>What a traffic light shows a car, from the host's Light hook.</summary>
        public const int LightGo = 0, LightStop = 1, LightStopIfAble = 2;

        /// <summary>
        /// Host hook: what the light (prefab item, semaphore id) - the id a prefab curve names, EtsLaneGraph.Lane.Semaphore
        /// - shows: LightGo (green, off, blinking, unknown), LightStop (red, red-amber) or LightStopIfAble (amber). The
        /// world answers from EtsSemaphores' shared clock, so every client's cars and lamps agree with no traffic on the
        /// wire. Null: no lights (offline, and the harness), junctions run on reservations alone.
        /// </summary>
        public Func<ulong, int, int> Light;

        /// <summary>Steps on which a car was held at a light, for the log.</summary>
        public int LightWaits;

        public readonly Settings Config;
        public readonly Car[] Slots;
        private readonly EtsLaneGraph _graph;
        private readonly EtsTrafficCatalog _catalog;
        private readonly System.Random _random;
        private readonly Dictionary<int, List<Car>> _onLane = new Dictionary<int, List<Car>>();
        private readonly Dictionary<int, List<Car>> _holders = new Dictionary<int, List<Car>>();
        private readonly List<Car> _waiting = new List<Car>();
        private readonly int[] _serials;
        private float _spawnCredit;
        private double _time;

        public int Count { get; private set; }
        public int Spawned, RemovedFar, RemovedStuck, RemovedDeadEnd, RemovedLost;
        public int JunctionWaits;

        /// <summary>Cars hit so far, wrecks that drove on, wrecks removed (far or out of sight).</summary>
        public int Wrecks, Recovered, RemovedWreck;

        /// <summary>Hits Collide found since the host last cleared the list (the host logs and clears it).</summary>
        public readonly List<Hit> Hits = new List<Hit>();

        /// <summary>Wrecks on the road now.</summary>
        public int WreckCount
        {
            get
            {
                int n = 0;
                foreach (Car car in Slots) if (car != null && car.Wrecked) n++;
                return n;
            }
        }

        /// <summary>Cars that drove onto a junction curve they had not been granted (they could not stop), for the log.</summary>
        public int EnteredUnheld;

        /// <summary>Complete candidate gathers so far.</summary>
        public int Gathers;

        /// <summary>
        /// Offline: reads what spawning needs (the country's cars' bounds) at once. In the world the
        /// host runs EtsTrafficCatalog.Prepare on its worker thread instead.
        /// </summary>
        public void Prepare()
        {
            _catalog.Prepare(Config.Country);
            MakePool();
        }

        public EtsTrafficSim(EtsLaneGraph graph, EtsTrafficCatalog catalog, Settings settings, int seed)
        {
            _graph = graph;
            _catalog = catalog;
            Config = settings ?? new Settings();
            Slots = new Car[Config.Capacity];
            _serials = new int[Config.Capacity];
            _random = new System.Random(seed);
        }

        // ------------------------------------------------------------------ the step

        public void Step(float dt, IList<Focus> foci, IList<Obstacle> obstacles, double time)
        {
            _time = time;
            dt = Mathf.Clamp(dt, 0f, 0.1f);
            if (_steps++ % 10 == 0) Claim(obstacles);

            // Hits first, so a car hit now is a wreck - off its lane, an obstacle - for everyone's step.
            Collide(obstacles);
            Occupancy();
            Boxes(obstacles);

            for (int i = 0; i < Slots.Length; i++)
            {
                Car car = Slots[i];
                if (car == null || car.Wrecked) continue;

                EtsLaneGraph.Lane lane = _graph.Get(car.Lane);

                if (lane == null)
                {
                    Remove(car, ref RemovedLost);
                    continue;
                }

                car.Accel = Acceleration(car, lane);
            }

            for (int i = 0; i < Slots.Length; i++)
            {
                Car car = Slots[i];
                if (car == null) continue;

                if (car.Wrecked)
                {
                    Settle(car, dt);
                }
                else
                {
                    Advance(car, dt);
                }
            }

            Retire(foci);
            Spawn(dt, foci, obstacles);
        }

        private readonly HashSet<ulong> _claimed = new HashSet<ulong>();
        private int _steps;
        private readonly List<int> _claimNear = new List<int>();

        /// <summary>
        /// Junctions a player's vehicle is in or about to enter (its heading, up to three seconds or
        /// 10 m ahead): no car may start through them. The player never reserves, and a car must
        /// not pull out in front of a truck it cannot see coming the way it sees other cars.
        /// A vehicle standing still (under 1 m/s) claims only what it stands on, to 1 m past its
        /// front: reaching 10 m ahead, a truck stopped behind a car queued at a mouth claimed that
        /// car's junction, so the car was refused for as long as the truck stood there, within
        /// 160 m of a player it was never removed, and the two blocked each other for good
        /// (harness 1540: the car ahead stood 221 s with the junction claimed; harness 1750: 62
        /// of the stopped truck's 10 s samples over four 1,800 s runs, none since). A stopped
        /// tractor (half length 3.05 m) still claims a junction whose curves come within about 5 m
        /// of its front bumper: its last probe is 1.1 m behind the bumper, and a curve within 6 m
        /// of a probe claims. One standing inside a junction claims it, as before.
        /// </summary>
        private void Claim(IList<Obstacle> obstacles)
        {
            _claimed.Clear();
            if (obstacles == null) return;

            foreach (Obstacle o in obstacles)
            {
                if (!o.Player || o.HalfLength < 1f) continue;
                Vector3 forward = o.Forward.sqrMagnitude > 1e-4f ? o.Forward.normalized : Vector3.forward;
                float speed = o.Velocity.magnitude;
                float reach = speed < 1f ? o.HalfLength + 1f : Math.Max(10f, speed * 3f) + o.HalfLength;

                for (float d = -o.HalfLength; d <= reach; d += 5f)
                {
                    Vector3 p = o.Position + forward * d;
                    _graph.LanesNear(p, 8f, _claimNear);

                    foreach (int id in _claimNear)
                    {
                        EtsLaneGraph.Lane lane = _graph.Get(id);
                        if (lane == null || !lane.InPrefab || _claimed.Contains(lane.Item)) continue;

                        // No point of a lane is nearer p than half of (|p - Start| + |p - End| -
                        // Length), Length being at least its polyline's: a lane that far off cannot
                        // pass Project's 6 m, so the claimed set is the same (checked against the
                        // old loop on 9,000 claims, harness 1750). Every probe re-tests the lanes
                        // round it, up to 198 Project calls a claim; this leaves 8.6 of the mean
                        // 48, and a claim took 0.066 ms mean, 0.21 ms p99 against the old loop's
                        // 0.13 and 0.55 on the same inputs (mono JIT).
                        if (((p - lane.Start).magnitude + (p - lane.End).magnitude - lane.Length) * 0.5f >= 6.01f) continue;
                        _graph.Project(lane, p, out float distance);
                        if (distance < 6f) _claimed.Add(lane.Item);
                    }
                }
            }
        }

        private void Occupancy()
        {
            // One list per lane ever driven on; start again now and then so it stays small.
            if (_onLane.Count > 4096) _onLane.Clear();
            foreach (List<Car> list in _onLane.Values) list.Clear();
            Count = 0;

            foreach (Car car in Slots)
            {
                if (car == null) continue;
                Count++;

                // A wreck is off its lane: the others see it as a box (Boxes), which they may pass.
                if (car.Wrecked) continue;

                if (!_onLane.TryGetValue(car.Lane, out List<Car> list))
                {
                    list = new List<Car>(4);
                    _onLane[car.Lane] = list;
                }

                list.Add(car);
            }
        }

        // ------------------------------------------------------------------ following

        private float Acceleration(Car car, EtsLaneGraph.Lane lane)
        {
            Settings c = Config;
            float v = car.V;
            float desired = Math.Max(1f, Math.Min(lane.SpeedLimit * car.Factor, Math.Min(lane.CurveLimit, _catalog.Vehicles[car.Vehicle].MaxSpeedKmh / 3.6f)));
            float free = c.MaxAcceleration * (1f - Mathf.Pow(Math.Min(v / desired, 2f), 4f));
            float best = free;
            int why = WhyFree;
            float lookahead = 25f + v * 4f;
            EnsureRoute(car, lookahead);

            // Walk the path: this lane from S, then the route.
            float along = -car.S;
            int flags = 0;
            EtsLaneGraph.Lane walk = lane;
            int routeIndex = -1;

            // A car ahead on a road lane before the next mouth: this one is not first in its queue.
            bool queued = false;

            while (walk != null && along < lookahead)
            {
                float fromS = walk.Id == car.Lane ? car.S : 0f;

                // Other cars on this piece of path.
                if (_onLane.TryGetValue(walk.Id, out List<Car> cars))
                {
                    foreach (Car other in cars)
                    {
                        if (other == car || other.S <= fromS && walk.Id == car.Lane) continue;
                        if (walk.Id == car.Lane && other.S <= car.S) continue;
                        if (!walk.InPrefab) queued = true;
                        float gap = along + other.S - car.HalfLength - other.HalfLength;
                        Take(ref best, ref why, Idm(v, desired, gap, other.V, c.Headway, c.Standstill), WhyCar);
                    }
                }

                // A slower limit or a bend ahead: brake in time for it.
                float limit = Math.Min(walk.SpeedLimit * car.Factor, walk.CurveLimit);
                if (walk.Id != car.Lane && v > limit)
                {
                    float distance = Math.Max(1f, along);
                    Take(ref best, ref why, (limit * limit - v * v) / (2f * distance), WhyLimit);
                }

                // A junction's mouth (a prefab lane after a road lane).
                EtsLaneGraph.Lane before = walk.InPrefab && walk.Id != car.Lane ? (routeIndex <= 0 ? lane : _graph.Get(car.Route[routeIndex - 1])) : null;

                // A traffic light on this curve (its stop line is the curve's start): hold there on red and
                // red-amber, and on amber when the car can still stop comfortably; a car that can no longer stop
                // goes on, committed at the amber, and one that has decided to stop keeps to it (StopFor). Held at
                // a junction's mouth it gives up any request or grant through it - the queued branch's rule - so a
                // car waiting at red never holds curves that cross the way with green. Only with the host's hook.
                if (Light != null && walk.InPrefab && walk.Id != car.Lane && walk.Semaphore >= 0)
                {
                    int shows = Light(walk.Item, walk.Semaphore);

                    if (shows == LightGo)
                    {
                        if (car.StopFor == walk.Id) car.StopFor = -1;
                    }
                    else
                    {
                        float gap = along - car.HalfLength - 0.5f;
                        float brake = c.ComfortBraking * (shows == LightStop ? 1.5f : 1f);

                        if (car.StopFor == walk.Id || v < 1f || gap > v * v / (2f * brake))
                        {
                            car.StopFor = walk.Id;
                            Take(ref best, ref why, Idm(v, desired, gap, 0f, c.Headway, 1f), WhyLight);
                            LightWaits++;

                            if (before != null && !before.InPrefab)
                            {
                                if (car.Waiting != null && car.Waiting.Count > 0 && car.Waiting[0] == walk.Id)
                                {
                                    car.Waiting = null;
                                    _waiting.Remove(car);
                                }

                                for (int k = routeIndex; k < car.Route.Count && car.Held.Count > 0; k++)
                                {
                                    Release(car, car.Route[k]);
                                }
                            }

                            break;
                        }
                    }
                }

                if (before != null && !before.InPrefab && queued)
                {
                    // Only the first car before a mouth asks; the one ahead holds this one back
                    // until it is inside, and then this one asks. A car behind that asked while the
                    // first waited could be granted a chain crossing only the first's: the first was
                    // then refused for it, the second could not pass the first, and near a player
                    // neither was ever removed (4 to 18 such pairs per 1,800 s run, none since,
                    // harness 1750; the class comment's argument missed holders still on the
                    // approach). A request for this mouth, or a grant through it, from before a car
                    // got in front (a merge onto the approach) is given up for the same reason:
                    // this car cannot reach any lane from this mouth on before that car is inside.
                    // Only those lanes go - a car still inside the junction before keeps the curves
                    // it is on.
                    if (car.Waiting != null && car.Waiting.Count > 0 && car.Waiting[0] == walk.Id)
                    {
                        car.Waiting = null;
                        _waiting.Remove(car);
                    }

                    for (int k = routeIndex; k < car.Route.Count && car.Held.Count > 0; k++)
                    {
                        Release(car, car.Route[k]);
                    }
                }
                else if (before != null && !before.InPrefab && !car.Held.Contains(walk.Id))
                {
                    // A junction this car may not enter yet: stop at its mouth. Asked only once the
                    // car must decide (its braking distance plus a margin): asking from 100 m out
                    // made far cars block near ones in the queue.
                    float decide = Math.Max(15f, v * v / (2f * c.ComfortBraking) + 8f);

                    if (along <= decide && !Reserve(car, routeIndex))
                    {
                        float gap = along - car.HalfLength - 0.5f;
                        Take(ref best, ref why, Idm(v, desired, gap, 0f, c.Headway, 1f), WhyJunction);
                        JunctionWaits++;
                        break;
                    }
                }

                if (walk.InPrefab && walk.Blinker != 0 && along < 40f)
                {
                    flags |= (walk.Blinker & 4) != 0 ? FlagBlinkLeft : (walk.Blinker & 2) != 0 ? FlagBlinkRight : 0;
                }

                along += walk.Length;
                routeIndex++;

                if (routeIndex >= car.Route.Count)
                {
                    // The end of what is known: a dead end stops the car at the lane's end.
                    if (walk.Next.Length == 0)
                    {
                        Take(ref best, ref why, Idm(v, desired, along - car.HalfLength - 1f, 0f, c.Headway, 1f), WhyDeadEnd);
                    }

                    break;
                }

                walk = _graph.Get(car.Route[routeIndex]);
            }

            // Players' vehicles, walkers and wrecks on the path. A pass round a wreck (Nudge) is kept until the car is by it.
            if (car.NudgeHold <= 0f)
            {
                car.NudgeTarget = 0f;
            }

            if (_boxes.Count > 0)
            {
                Take(ref best, ref why, Obstacles(car, lane, lookahead, desired), WhyObstacle);
            }

            best = Mathf.Clamp(best, -c.HardBraking, c.MaxAcceleration);
            car.Flags = flags | (best < -1f ? FlagBrake : 0);
            car.Why = why;
            return best;
        }

        private static void Take(ref float best, ref int why, float candidate, int reason)
        {
            if (candidate < best)
            {
                best = candidate;
                why = reason;
            }
        }

        private float Idm(float v, float desired, float gap, float leaderSpeed, float headway, float standstill)
        {
            Settings c = Config;
            gap = Math.Max(0.1f, gap);
            float wanted = standstill + Math.Max(0f, v * headway + v * (v - leaderSpeed) / (2f * Mathf.Sqrt(c.MaxAcceleration * c.ComfortBraking)));
            return c.MaxAcceleration * (1f - Mathf.Pow(Math.Min(v / desired, 2f), 4f) - (wanted / gap) * (wanted / gap));
        }

        /// <summary>
        /// The first obstacle box the car's footprint would touch along its path (its place, then every 2 m to the
        /// look-ahead): IDM behind it at the distance the car can still go, refined to half a metre on the same lane.
        /// A box behind the car's centre is left to whoever drives it. A standing wreck the car can pass by moving over
        /// in its lane (Nudge) is passed, braking only while the move is not yet made.
        /// </summary>
        private float Obstacles(Car car, EtsLaneGraph.Lane lane, float lookahead, float desired)
        {
            float best = float.MaxValue;
            float reach = lookahead + 30f;
            bool anyNear = false;

            for (int i = 0; i < _boxes.Count && !anyNear; i++)
            {
                Box b = _boxes[i];
                if (b.Slot == car.Slot) continue;
                float r = reach + b.Radius;
                anyNear = (b.C - car.Position).sqrMagnitude < r * r;
            }

            if (!anyNear)
            {
                return best;
            }

            Settings c = Config;
            Vector3 heading = Flat(car.Direction);
            float carReach = car.HalfLength + car.HalfWidth + c.ObstacleMargin;
            EtsLaneGraph.Lane walk = lane;
            float s = car.S, along = 0f, clear = 0f;
            int routeIndex = -1;

            while (walk != null && along <= lookahead)
            {
                Vector3 p = _graph.PointAt(walk, s);
                Vector3 d = Flat(_graph.DirectionAt(walk, s));
                Vector3 right = new Vector3(d.z, 0f, -d.x);
                Vector3 at = p + right * car.Lateral;

                for (int i = 0; i < _boxes.Count; i++)
                {
                    Box b = _boxes[i];
                    if (b.Slot == car.Slot) continue;
                    Vector3 rel = b.C - at;
                    float r = b.Radius + carReach;
                    if (rel.x * rel.x + rel.z * rel.z > r * r || Math.Abs(rel.y) > 4f) continue;
                    if (Vector3.Dot(b.C - car.Position, heading) < 0f) continue;
                    if (!Overlap2D(at, d, car.HalfLength, car.HalfWidth, b.C, b.F, b.HL, b.HW, c.ObstacleMargin, out _, out _)) continue;

                    if (b.Wreck && b.V.sqrMagnitude < 1f && Nudge(car, p, d, right, b, along))
                    {
                        continue;
                    }

                    // How far the car can still go: between the last clear sample and this one, halved twice where both
                    // lie on this lane.
                    float go = clear;

                    if (along > 0f && s - (along - clear) >= 0f)
                    {
                        float lo = clear, hi = along;

                        for (int k = 0; k < 2; k++)
                        {
                            float mid = (lo + hi) * 0.5f;
                            float ms = s - (along - mid);
                            Vector3 mp = _graph.PointAt(walk, ms);
                            Vector3 md = Flat(_graph.DirectionAt(walk, ms));
                            Vector3 mat = mp + new Vector3(md.z, 0f, -md.x) * car.Lateral;

                            if (Overlap2D(mat, md, car.HalfLength, car.HalfWidth, b.C, b.F, b.HL, b.HW, c.ObstacleMargin, out _, out _)) hi = mid;
                            else lo = mid;
                        }

                        go = lo;
                    }

                    // A truck reversing towards the car counts as coming at it (to -8 m/s), which widens the gap wanted.
                    float leader = Math.Max(-8f, Vector3.Dot(b.V, d));
                    best = Math.Min(best, Idm(car.V, desired, go, leader, b.Player ? c.PlayerHeadway : c.Headway, b.Player ? c.PlayerStandstill : c.Standstill));
                    return best;
                }

                clear = along;
                s += 2f;
                along += 2f;

                while (walk != null && s > walk.Length)
                {
                    s -= walk.Length;
                    routeIndex++;
                    walk = routeIndex < car.Route.Count ? _graph.Get(car.Route[routeIndex]) : null;
                }
            }

            return best;
        }

        /// <summary>
        /// Whether the car can pass a standing wreck (box b, met 'along' metres ahead) by moving over in its lane, away
        /// from it, by at most MaxNudge: the offset is taken (NudgeTarget, kept NudgeHold metres until the car's back is
        /// past the wreck) and true is answered once the car will be over in time - until then it brakes for the wreck,
        /// and moves over as it slows. False when no offset clears it, or the car is already moving over the other way.
        /// </summary>
        private bool Nudge(Car car, Vector3 p, Vector3 d, Vector3 right, Box b, float along)
        {
            Settings c = Config;
            float side = Vector3.Dot(b.C - p, right) > 0f ? -1f : 1f;

            if (car.NudgeTarget * side < -1e-3f)
            {
                return false;
            }

            for (int k = 1; k <= 3; k++)
            {
                float offset = side * c.MaxNudge * k / 3f;

                if (Overlap2D(p + right * offset, d, car.HalfLength, car.HalfWidth, b.C, b.F, b.HL, b.HW, c.ObstacleMargin * 0.6f, out _, out _))
                {
                    continue;
                }

                if (Math.Abs(offset) > Math.Abs(car.NudgeTarget)) car.NudgeTarget = offset;
                car.NudgeHold = Math.Max(car.NudgeHold, along + 2f * b.Radius + 2f * car.HalfLength + 2f);

                // Over in time: the move takes |offset - Lateral| / NudgeSpeed seconds, in which the car covers V times that.
                float needed = Math.Abs(car.NudgeTarget - car.Lateral) / c.NudgeSpeed * car.V;
                return needed <= Math.Max(0f, along - car.HalfLength);
            }

            return false;
        }

        // ------------------------------------------------------------------ routes and junctions

        /// <summary>Chooses lanes ahead until the route covers the look-ahead (or reaches a dead end).</summary>
        private void EnsureRoute(Car car, float lookahead)
        {
            EnsureRoute(car, lookahead, false);
        }

        /// <summary>throughJunctions: keep planning while the route ends inside a prefab, so a run of
        /// junctions joined node to node is reserved as one.</summary>
        private void EnsureRoute(Car car, float lookahead, bool throughJunctions)
        {
            EtsLaneGraph.Lane lane = _graph.Get(car.Lane);
            float covered = lane != null ? lane.Length - car.S : 0f;

            foreach (int id in car.Route)
            {
                EtsLaneGraph.Lane l = _graph.Get(id);
                if (l == null) break;
                covered += l.Length;
            }

            int guard = 0;

            while ((covered < lookahead || throughJunctions && EndsInPrefab(car, lane)) && guard++ < 16)
            {
                EtsLaneGraph.Lane last = car.Route.Count > 0 ? _graph.Get(car.Route[car.Route.Count - 1]) : lane;

                if (last == null || last.Next.Length == 0)
                {
                    return;
                }

                // Entering a prefab: take one of its whole chains, as the game's routes do.
                int entry = -1;

                foreach (int n in last.Next)
                {
                    EtsLaneGraph.Lane next = _graph.Get(n);

                    if (next != null && next.InPrefab && (!last.InPrefab || last.Item != next.Item))
                    {
                        entry = n;
                        break;
                    }
                }

                if (entry >= 0 && AppendChain(car, last, _graph.Get(entry), ref covered))
                {
                    continue;
                }

                int pick = PickNext(last);

                if (pick < 0)
                {
                    return;
                }

                car.Route.Add(pick);
                covered += _graph.Get(pick).Length;
            }
        }

        private bool EndsInPrefab(Car car, EtsLaneGraph.Lane lane)
        {
            EtsLaneGraph.Lane last = car.Route.Count > 0 ? _graph.Get(car.Route[car.Route.Count - 1]) : lane;
            return last != null && last.InPrefab && last.Next.Length > 0;
        }

        private int PickNext(EtsLaneGraph.Lane last)
        {
            // Ways that go on first; a dead end only when there is nothing else.
            for (int pass = 0; pass < 2; pass++)
            {
                int count = 0;

                foreach (int n in last.Next)
                {
                    if (_graph.Get(n) != null && (pass == 1 || _graph.LeadsOn(n))) count++;
                }

                if (count == 0) continue;
                int k = _random.Next(count);

                foreach (int n in last.Next)
                {
                    if (_graph.Get(n) == null || pass == 0 && !_graph.LeadsOn(n)) continue;
                    if (k-- == 0) return n;
                }
            }

            return -1;
        }

        /// <summary>Whether the lane a chain ends on leads anywhere (follows the placement's lanes by curve index).</summary>
        private bool ChainLeadsOn(EtsLaneGraph.Lane entry, int[] chain)
        {
            EtsLaneGraph.Lane at = entry;

            for (int k = 1; k < chain.Length && at != null; k++)
            {
                EtsLaneGraph.Lane found = null;

                foreach (int n in at.Next)
                {
                    EtsLaneGraph.Lane next = _graph.Get(n);
                    if (next != null && next.Item == entry.Item && next.Curve == chain[k]) { found = next; break; }
                }

                at = found;
            }

            return at != null && _graph.LeadsOn(at.Id);
        }

        private bool AppendChain(Car car, EtsLaneGraph.Lane before, EtsLaneGraph.Lane entry, ref float covered)
        {
            EtsPrefabNav nav = entry.Nav;
            List<int[]> chains = nav.ChainsFrom(entry.Curve);

            if (chains.Count == 0)
            {
                return false;
            }

            // Which prefab node this input belongs to, so a U-turn can be made unlikely.
            int entryNode = -1;

            for (int n = 0; n < nav.NodeInputs.Length && entryNode < 0; n++)
            {
                if (Array.IndexOf(nav.NodeInputs[n], entry.Curve) >= 0) entryNode = n;
            }

            float total = 0f;
            var weights = new float[chains.Count];

            for (int i = 0; i < chains.Count; i++)
            {
                int[] chain = chains[i];
                int exit = nav.OutputNodeOf[chain[chain.Length - 1]];
                float w = 1f;
                foreach (int curve in chain) if (nav.Curves[curve].LowProbability) w *= 0.2f;
                if (exit == entryNode) w *= 0.02f;
                if (!ChainLeadsOn(entry, chain)) w *= 0.001f;
                weights[i] = w;
                total += w;
            }

            double roll = _random.NextDouble() * total;
            int chosen = 0;

            for (int i = 0; i < chains.Count; i++)
            {
                roll -= weights[i];
                if (roll <= 0) { chosen = i; break; }
            }

            // Curve indices to this placement's lanes, by following the lanes' own links.
            var lanes = new List<int> { entry.Id };
            EtsLaneGraph.Lane at = entry;

            for (int k = 1; k < chains[chosen].Length; k++)
            {
                int wanted = chains[chosen][k], found = -1;

                foreach (int n in at.Next)
                {
                    EtsLaneGraph.Lane next = _graph.Get(n);
                    if (next != null && next.Item == entry.Item && next.Curve == wanted) { found = n; break; }
                }

                if (found < 0) return false;
                lanes.Add(found);
                at = _graph.Get(found);
            }

            foreach (int id in lanes)
            {
                car.Route.Add(id);
                covered += _graph.Get(id).Length;
            }

            return true;
        }

        /// <summary>
        /// Reserves the run of prefab lanes starting at route index 'from' (all of the same
        /// placement), or queues the request. True once held.
        /// </summary>
        private readonly List<int> _wanted = new List<int>(8);

        private bool Reserve(Car car, int from)
        {
            if (from < 0 || from >= car.Route.Count)
            {
                return false;
            }

            // Every prefab lane until the route is back on a road, over however many junctions
            // are joined node to node: all or nothing, so no car ever waits while holding.
            EnsureRoute(car, 0f, true);
            List<int> wanted = _wanted;
            wanted.Clear();

            for (int k = from; k < car.Route.Count; k++)
            {
                EtsLaneGraph.Lane l = _graph.Get(car.Route[k]);
                if (l == null || !l.InPrefab) break;
                wanted.Add(l.Id);
            }

            if (wanted.Count == 0)
            {
                return true;
            }

            if (car.Waiting == null || !SameList(car.Waiting, wanted))
            {
                car.Waiting = new List<int>(wanted);
                car.WaitingSince = _time;
                if (!_waiting.Contains(car)) _waiting.Add(car);
            }

            foreach (int id in wanted)
            {
                EtsLaneGraph.Lane mine = _graph.Get(id);

                if (_claimed.Contains(mine.Item))
                {
                    return false;
                }

                // Held by others (a lane is held by every car reserved onto it: a queue shares curves).
                foreach (KeyValuePair<int, List<Car>> held in _holders)
                {
                    if (held.Value.Count == 0 || held.Value.Count == 1 && held.Value[0] == car) continue;
                    EtsLaneGraph.Lane theirs = _graph.Get(held.Key);
                    if (theirs == null || theirs.Item != mine.Item) continue;
                    if (mine.Nav.Conflict(mine.Curve, theirs.Curve)) return false;
                }

                // Asked for earlier.
                foreach (Car other in _waiting)
                {
                    if (other == car || other.Waiting == null || other.WaitingSince >= car.WaitingSince || _time - other.WaitingSince < Config.FifoAfter) continue;

                    foreach (int theirId in other.Waiting)
                    {
                        EtsLaneGraph.Lane theirs = _graph.Get(theirId);
                        if (theirs != null && theirs.Item == mine.Item && mine.Nav.Conflict(mine.Curve, theirs.Curve)) return false;
                    }
                }
            }

            foreach (int id in wanted)
            {
                Hold(id, car);
            }

            car.Waiting = null;
            _waiting.Remove(car);
            return true;
        }

        private void Hold(int laneId, Car car)
        {
            if (!_holders.TryGetValue(laneId, out List<Car> holders))
            {
                holders = new List<Car>(2);
                _holders[laneId] = holders;
            }

            if (!holders.Contains(car)) holders.Add(car);
            if (!car.Held.Contains(laneId)) car.Held.Add(laneId);
        }

        private static bool SameList(List<int> a, List<int> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++) if (a[i] != b[i]) return false;
            return true;
        }

        // A lane nobody holds leaves the table: Reserve walks all of it for every waiting car every
        // step, and every junction lane ever driven through would otherwise stay in it.
        private void Release(Car car, int laneId)
        {
            if (car.Held.Remove(laneId) && _holders.TryGetValue(laneId, out List<Car> holders))
            {
                holders.Remove(car);
                if (holders.Count == 0) _holders.Remove(laneId);
            }
        }

        private void ReleaseAll(Car car)
        {
            foreach (int id in car.Held)
            {
                if (_holders.TryGetValue(id, out List<Car> holders))
                {
                    holders.Remove(car);
                    if (holders.Count == 0) _holders.Remove(id);
                }
            }

            car.Held.Clear();
            car.Waiting = null;
            _waiting.Remove(car);
        }

        // ------------------------------------------------------------------ moving

        private void Advance(Car car, float dt)
        {
            car.V = Math.Max(0f, car.V + car.Accel * dt);
            car.S += car.V * dt;
            car.Stuck = car.V < 0.2f ? car.Stuck + dt : 0f;
            EtsLaneGraph.Lane lane = _graph.Get(car.Lane);
            int guard = 0;

            while (lane != null && car.S > lane.Length && guard++ < 8)
            {
                if (car.Route.Count == 0)
                {
                    // Nowhere to go: stand at the end.
                    car.S = lane.Length;
                    car.V = 0f;
                    break;
                }

                car.S -= lane.Length;
                Release(car, car.Lane);
                car.Lane = car.Route[0];
                car.Route.RemoveAt(0);
                lane = _graph.Get(car.Lane);

                // Onto a junction curve nobody granted it: refused where it could no longer stop.
                // Its request is moot now - left standing, after FifoAfter it refused other cars at
                // a junction this one had already left (577 car-checks, harness 1750 teleport run) -
                // and what it drives from here is held, as a grant would have been.
                if (lane != null && lane.InPrefab && !car.Held.Contains(car.Lane))
                {
                    car.Waiting = null;
                    _waiting.Remove(car);
                    HoldInside(car);
                    EnteredUnheld++;
                }
            }

            if (lane == null)
            {
                return;
            }

            // Moving over to pass a wreck, or back once by it.
            car.NudgeHold = Math.Max(0f, car.NudgeHold - car.V * dt);
            car.Lateral = Mathf.MoveTowards(car.Lateral, car.NudgeTarget, Config.NudgeSpeed * dt);

            car.Position = _graph.PointAt(lane, car.S);
            car.Direction = _graph.DirectionAt(lane, car.S);
            car.Yaw = Mathf.Atan2(car.Direction.x, car.Direction.z) * Mathf.Rad2Deg;

            if (Math.Abs(car.Lateral) > 1e-3f)
            {
                Vector3 d = Flat(car.Direction);
                car.Position += new Vector3(d.z, 0f, -d.x) * car.Lateral;
            }
        }

        // ------------------------------------------------------------------ hits and wrecks

        /// <summary>
        /// Makes the car in a slot a wreck (the class notes), moving at 'velocity' (its push). The host calls it for a hit
        /// by its own truck, before its physics meets the car; Collide for another player's. False when the slot is
        /// empty or the car was a wreck already (its velocity is still taken).
        /// </summary>
        public bool Wreck(int slot, Vector3 velocity)
        {
            Car car = slot >= 0 && slot < Slots.Length ? Slots[slot] : null;

            if (car == null)
            {
                return false;
            }

            velocity.y = 0f;
            car.WreckVelocity = velocity;

            if (car.Wrecked)
            {
                return false;
            }

            car.Wrecked = true;
            car.WreckedAt = _time;
            car.RestTime = 0f;
            car.Stuck = 0f;
            car.NextRecover = _time + Config.WreckRecover;
            car.Accel = 0f;
            car.Lateral = 0f;
            car.NudgeTarget = 0f;
            car.NudgeHold = 0f;
            car.StopFor = -1;
            ReleaseAll(car);
            car.Route.Clear();
            car.Flags = WreckFlags;
            car.Why = WhyWreck;
            car.V = Math.Max(0f, Vector3.Dot(velocity, Flat(car.Direction)));
            Wrecks++;
            return true;
        }

        /// <summary>The host's physics pose of a wreck it holds (a dynamic Rigidbody), given before each Step.</summary>
        public void SetWreckPose(int slot, Vector3 position, Vector3 forward, Vector3 velocity)
        {
            Car car = slot >= 0 && slot < Slots.Length ? Slots[slot] : null;

            if (car == null || !car.Wrecked)
            {
                return;
            }

            Vector3 f = Flat(forward);
            velocity.y = 0f;
            car.Position = position;
            car.Direction = f;
            car.Yaw = Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
            car.WreckVelocity = velocity;

            // The wire's speed is along the heading (the watchers' timeline draws velocity along it): a sideways slide sends 0.
            car.V = Math.Max(0f, Vector3.Dot(velocity, f));
            car.HostPose = true;
        }

        /// <summary>A wreck's step: its own slide to rest unless the host's physics set its pose; hazards on; drives on in time (Recover).</summary>
        private void Settle(Car car, float dt)
        {
            Vector3 v = car.WreckVelocity;

            if (!car.HostPose)
            {
                float speed = v.magnitude;

                if (speed > 1e-3f)
                {
                    v *= Math.Max(0f, speed - Config.WreckFriction * dt) / speed;
                    car.Position += v * dt;
                    car.WreckVelocity = v;
                    car.V = Math.Max(0f, Vector3.Dot(v, Flat(car.Direction)));
                }
            }

            car.HostPose = false;
            float moving = v.magnitude;
            car.Stuck = moving < 0.2f ? car.Stuck + dt : 0f;
            car.RestTime = moving < 0.3f ? car.RestTime + dt : 0f;
            car.Flags = WreckFlags;
            car.Why = WhyWreck;

            if (car.RestTime >= Config.WreckRecover && _time >= car.NextRecover)
            {
                car.NextRecover = _time + 2.0;
                Recover(car);
            }
        }

        /// <summary>
        /// A wreck at rest drives on from where it stands when that is on a lane (within WreckRecoverOffset of its line,
        /// lined up with it), no player's vehicle is within WreckRecoverClear and no other car within 6 m: back on the
        /// lane at a standstill, holding its way out when inside a junction (as an adopted car).
        /// </summary>
        private void Recover(Car car)
        {
            Settings c = Config;

            for (int i = 0; i < _boxes.Count; i++)
            {
                Box b = _boxes[i];
                if (!b.Player) continue;
                float r = c.WreckRecoverClear + b.Radius;
                if ((b.C - car.Position).sqrMagnitude < r * r) return;
            }

            if (!_graph.Match(car.Position, car.Direction, c.WreckRecoverOffset, c.WreckRecoverDot, out int laneId, out float s))
            {
                return;
            }

            EtsLaneGraph.Lane lane = _graph.Get(laneId);

            if (lane == null)
            {
                return;
            }

            foreach (Car other in Slots)
            {
                if (other != null && other != car && (other.Position - car.Position).sqrMagnitude < 36f) return;
            }

            car.Wrecked = false;
            car.Lane = laneId;
            car.S = s;
            car.V = 0f;
            car.Accel = 0f;
            car.Route.Clear();
            car.WreckVelocity = Vector3.zero;
            car.Flags = 0;
            car.Why = WhyFree;
            car.Stuck = 0f;
            car.RestTime = 0f;
            car.Position = _graph.PointAt(lane, s);
            car.Direction = _graph.DirectionAt(lane, s);
            car.Yaw = Mathf.Atan2(car.Direction.x, car.Direction.z) * Mathf.Rad2Deg;

            if (lane.InPrefab)
            {
                HoldInside(car);
            }

            Recovered++;
        }

        /// <summary>
        /// Another player's truck or trailer (a kinematic puppet here, drawn a network delay late: the host's physics
        /// cannot tell it hit anything) overlapping a car by HitDepth in these boxes hits it: the car is put out of the
        /// box, pushed (Impulse) and made a wreck, and the hit is listed in Hits for the host's log.
        /// </summary>
        private void Collide(IList<Obstacle> obstacles)
        {
            if (obstacles == null || obstacles.Count == 0)
            {
                return;
            }

            for (int k = 0; k < obstacles.Count; k++)
            {
                Obstacle o = obstacles[k];

                if (!o.Player || !o.Remote || o.HalfLength < 1f)
                {
                    continue;
                }

                Vector3 f = Flat(o.Forward);
                float radius = Mathf.Sqrt(o.HalfLength * o.HalfLength + o.HalfWidth * o.HalfWidth);

                for (int i = 0; i < Slots.Length; i++)
                {
                    Car car = Slots[i];
                    if (car == null || car.Wrecked) continue;
                    Vector3 rel = car.Position - o.Position;
                    float r = radius + car.HalfLength + car.HalfWidth;
                    if (rel.x * rel.x + rel.z * rel.z > r * r || Math.Abs(rel.y) > 4f) continue;

                    Vector3 heading = Flat(car.Direction);

                    if (!Overlap2D(o.Position, f, o.HalfLength, o.HalfWidth, car.Position, heading, car.HalfLength, car.HalfWidth, 0f, out float depth, out Vector3 n) || depth < Config.HitDepth)
                    {
                        continue;
                    }

                    Vector3 carVelocity = heading * car.V;
                    car.Position += n * depth;

                    if (Wreck(i, carVelocity + Impulse(o.Velocity, carVelocity, n, _catalog.Vehicles[car.Vehicle].Mass)))
                    {
                        Hits.Add(new Hit { Slot = i, Trailer = o.Trailer, Closing = Vector3.Dot(o.Velocity - carVelocity, n) });
                    }
                }
            }
        }

        /// <summary>
        /// The change of speed a truck of PlayerMass hands a car of 'mass' through a contact whose normal n points from the
        /// truck to the car: the closing speed shared by the masses, with Bounce. A contact not closing parts them at 0.5 m/s.
        /// </summary>
        public Vector3 Impulse(Vector3 truckVelocity, Vector3 carVelocity, Vector3 n, float mass)
        {
            float closing = Vector3.Dot(truckVelocity - carVelocity, n);

            if (closing <= 0f)
            {
                return n * 0.5f;
            }

            float share = Config.PlayerMass / (Config.PlayerMass + Math.Max(300f, mass));
            return n * (closing * (1f + Config.Bounce) * share);
        }

        // ------------------------------------------------------------------ boxes

        private struct Box
        {
            public Vector3 C, F, V;
            public float HL, HW, Radius;
            public bool Player, Wreck;
            public int Slot;
        }

        private readonly List<Box> _boxes = new List<Box>();

        /// <summary>
        /// This step's obstacle boxes: the host's (players' vehicles at their colliders' size, walkers), each reaching
        /// SweepSeconds along its own velocity, forwards or back, and every wreck at its pose.
        /// </summary>
        private void Boxes(IList<Obstacle> obstacles)
        {
            _boxes.Clear();

            if (obstacles != null)
            {
                for (int k = 0; k < obstacles.Count; k++)
                {
                    Obstacle o = obstacles[k];
                    Vector3 f = Flat(o.Forward);
                    float reach = Mathf.Clamp(Vector3.Dot(o.Velocity, f), -30f, 30f) * Config.SweepSeconds * 0.5f;
                    var b = new Box { C = o.Position + f * reach, F = f, V = o.Velocity, HL = o.HalfLength + Math.Abs(reach), HW = o.HalfWidth, Player = o.Player, Slot = -1 };
                    b.Radius = Mathf.Sqrt(b.HL * b.HL + b.HW * b.HW);
                    _boxes.Add(b);
                }
            }

            foreach (Car car in Slots)
            {
                if (car == null || !car.Wrecked) continue;
                var b = new Box { C = car.Position, F = Flat(car.Direction), V = car.WreckVelocity, HL = car.HalfLength, HW = car.HalfWidth, Wreck = true, Slot = car.Slot };
                b.Radius = Mathf.Sqrt(b.HL * b.HL + b.HW * b.HW);
                _boxes.Add(b);
            }
        }

        /// <summary>A direction in the ground plane, unit length (forward when it has none).</summary>
        public static Vector3 Flat(Vector3 v)
        {
            v.y = 0f;
            float m = v.magnitude;
            return m > 1e-4f ? v / m : Vector3.forward;
        }

        /// <summary>
        /// Two boxes in the ground plane (centres, unit flat forwards, half lengths and widths) overlap, with 'margin'
        /// added between them: the separating-axis test on their four edge directions. depth is the least overlap and
        /// normal its axis, pointing from a to b. Heights are the caller's.
        /// </summary>
        public static bool Overlap2D(Vector3 ca, Vector3 fa, float hla, float hwa, Vector3 cb, Vector3 fb, float hlb, float hwb, float margin, out float depth, out Vector3 normal)
        {
            depth = float.MaxValue;
            normal = Vector3.zero;
            float tx = cb.x - ca.x, tz = cb.z - ca.z;

            for (int k = 0; k < 4; k++)
            {
                // fa, its right (fa.z, -fa.x), fb, its right.
                float nx, nz;

                switch (k)
                {
                    case 0: nx = fa.x; nz = fa.z; break;
                    case 1: nx = fa.z; nz = -fa.x; break;
                    case 2: nx = fb.x; nz = fb.z; break;
                    default: nx = fb.z; nz = -fb.x; break;
                }

                float pa = hla * Math.Abs(fa.x * nx + fa.z * nz) + hwa * Math.Abs(fa.z * nx - fa.x * nz);
                float pb = hlb * Math.Abs(fb.x * nx + fb.z * nz) + hwb * Math.Abs(fb.z * nx - fb.x * nz);
                float t = tx * nx + tz * nz;
                float overlap = pa + pb + margin - Math.Abs(t);

                if (overlap <= 0f)
                {
                    return false;
                }

                if (overlap < depth)
                {
                    depth = overlap;
                    normal = t >= 0f ? new Vector3(nx, 0f, nz) : new Vector3(-nx, 0f, -nz);
                }
            }

            return true;
        }

        // ------------------------------------------------------------------ appearing and going

        private void Retire(IList<Focus> foci)
        {
            for (int i = 0; i < Slots.Length; i++)
            {
                Car car = Slots[i];
                if (car == null) continue;
                float nearest = Nearest(car.Position, foci);

                // A wreck stays where it came to rest until it drives on (Recover), or it is far from every player, or it
                // has stood StuckSeconds out of every player's sight.
                if (car.Wrecked)
                {
                    if (nearest > Config.Remove || car.Stuck > Config.StuckSeconds && nearest > Config.HiddenForRemoval) Remove(car, ref RemovedWreck);
                    continue;
                }

                if (nearest > Config.Remove)
                {
                    Remove(car, ref RemovedFar);
                }
                else if (car.Stuck > Config.StuckSeconds && nearest > Config.HiddenForRemoval)
                {
                    EtsLaneGraph.Lane lane = _graph.Get(car.Lane);
                    if (lane != null && lane.Next.Length == 0 && car.Route.Count == 0) Remove(car, ref RemovedDeadEnd);
                    else Remove(car, ref RemovedStuck);
                }
            }
        }

        private static float Nearest(Vector3 p, IList<Focus> foci)
        {
            float best = float.MaxValue;

            foreach (Focus f in foci)
            {
                float dx = p.x - f.Position.x, dz = p.z - f.Position.z;
                best = Math.Min(best, dx * dx + dz * dz);
            }

            return best == float.MaxValue ? best : Mathf.Sqrt(best);
        }

        private void Remove(Car car, ref int counter)
        {
            ReleaseAll(car);
            Slots[car.Slot] = null;
            counter++;
        }

        // ------------------------------------------------------------------ where cars may appear, per focus

        /// <summary>
        /// The lanes a car may appear on round one focus: lanes with car density that are not
        /// inside a junction and lead on. Gathering them is a 650 m sweep of 16 m cells (6,889 of
        /// them) and a LeadsOn walk per lane - 1.2 ms plus 2.8 ms cold offline (Program1400), up to
        /// 6 ms in a step when it ran on every spawn. Now each focus keeps its list, a refresh
        /// starts at most every GatherEvery seconds, only one runs at a time, and it walks
        /// GatherCellsPerStep cells and checks GatherLanesPerStep lanes a step. Spawns use the last
        /// complete list; a lane dropped since is simply skipped.
        /// </summary>
        private sealed class Candidates
        {
            public Vector3 At;
            public double Gathered = -1;
            public double Seen;
            public readonly List<int> Lanes = new List<int>();
            public float Total;
        }

        private readonly List<Candidates> _gathered = new List<Candidates>();

        // The one gather in progress.
        private Candidates _gatherFor;
        private Vector3 _gatherAt;
        private int _gatherCell = -1;
        private int _gatherRadius;
        private int _gatherLaneAt;
        private readonly List<int> _gatherFound = new List<int>();
        private readonly List<int> _gatherKept = new List<int>();
        private readonly HashSet<int> _gatherSeen = new HashSet<int>();
        private float _gatherTotal;

        /// <summary>Candidate lists in use (one per focus area) and whether a gather is running, for the log.</summary>
        public int CandidateLists => _gathered.Count;
        public bool Gathering => _gatherFor != null;

        private Candidates CandidatesFor(Vector3 focus)
        {
            Candidates best = null;
            float bestD = Config.GatherMove * Config.GatherMove;

            foreach (Candidates c in _gathered)
            {
                float dx = c.At.x - focus.x, dz = c.At.z - focus.z, d = dx * dx + dz * dz;
                if (d <= bestD) { best = c; bestD = d; }
            }

            if (best == null)
            {
                best = new Candidates { At = focus };
                _gathered.Add(best);
            }

            best.Seen = _time;

            // Stale (older than GatherEvery, or never gathered): gather again round where the
            // focus is now, unless another gather is running - the next step will get to it.
            if (_gatherFor == null && (best.Gathered < 0 || _time - best.Gathered >= Config.GatherEvery))
            {
                _gatherFor = best;
                _gatherAt = focus;
                _gatherCell = 0;
                _gatherRadius = Mathf.CeilToInt(Config.SpawnFar / EtsLaneGraph.SampleCellSize);
                _gatherLaneAt = 0;
                _gatherFound.Clear();
                _gatherKept.Clear();
                _gatherSeen.Clear();
                _gatherTotal = 0f;
            }

            return best;
        }

        /// <summary>A step's share of the gather in progress: some cells, then some lanes checked.</summary>
        private void GatherSome()
        {
            if (_gatherFor == null)
            {
                return;
            }

            int side = _gatherRadius * 2 + 1;
            int cells = side * side;
            int cx = Mathf.FloorToInt(_gatherAt.x / EtsLaneGraph.SampleCellSize), cz = Mathf.FloorToInt(_gatherAt.z / EtsLaneGraph.SampleCellSize);

            for (int n = 0; n < Config.GatherCellsPerStep && _gatherCell < cells; n++, _gatherCell++)
            {
                List<int> list = _graph.SampleCell(cx - _gatherRadius + _gatherCell % side, cz - _gatherRadius + _gatherCell / side);
                if (list == null) continue;
                foreach (int id in list) if (_gatherSeen.Add(id)) _gatherFound.Add(id);
            }

            if (_gatherCell < cells)
            {
                return;
            }

            for (int n = 0; n < Config.GatherLanesPerStep && _gatherLaneAt < _gatherFound.Count; n++, _gatherLaneAt++)
            {
                int id = _gatherFound[_gatherLaneAt];
                EtsLaneGraph.Lane lane = _graph.Get(id);
                if (lane == null || lane.Density <= 0f || lane.InPrefab || !_graph.LeadsOn(id)) continue;
                _gatherKept.Add(id);
                _gatherTotal += lane.Density * lane.Length;
            }

            if (_gatherLaneAt < _gatherFound.Count)
            {
                return;
            }

            Candidates done = _gatherFor;
            done.At = _gatherAt;
            done.Lanes.Clear();
            done.Lanes.AddRange(_gatherKept);
            done.Total = _gatherTotal;
            done.Gathered = _time;
            _gatherFor = null;
            Gathers++;
        }

        private void Spawn(float dt, IList<Focus> foci, IList<Obstacle> obstacles)
        {
            _spawnCredit = Math.Min(_spawnCredit + dt * Config.SpawnsPerSecond, 8f);

            if (foci == null || foci.Count == 0)
            {
                return;
            }

            // Lists nobody has stood near for a while go.
            for (int i = _gathered.Count - 1; i >= 0; i--)
            {
                if (_time - _gathered[i].Seen > 10.0 && _gathered[i] != _gatherFor) _gathered.RemoveAt(i);
            }

            int perFocus = Math.Min(Config.PerFocus, Math.Max(4, Config.MaxCars / foci.Count));

            foreach (Focus focus in foci)
            {
                Candidates candidates = CandidatesFor(focus.Position);

                // Only where the lanes are all there: a half-built area would crowd cars onto the
                // part that is.
                if (_spawnCredit < 1f || Count >= Config.MaxCars || candidates.Gathered < 0 || !_graph.IsAreaBuilt(focus.Position, Config.SpawnFar))
                {
                    continue;
                }

                int near = 0;
                foreach (Car car in Slots) if (car != null && (car.Position - focus.Position).sqrMagnitude < Config.SpawnFar * Config.SpawnFar) near++;
                if (near >= perFocus) continue;

                if (TrySpawn(focus, candidates, foci, obstacles))
                {
                    _spawnCredit -= 1f;
                    Count++;
                }
            }

            GatherSome();
        }

        private bool TrySpawn(Focus focus, Candidates candidates, IList<Focus> foci, IList<Obstacle> obstacles)
        {
            int slot = Array.IndexOf(Slots, null);

            if (slot < 0 || slot >= Config.MaxCars || candidates.Total <= 0f)
            {
                return false;
            }

            float hidden = focus.Fresh ? Config.SpawnNearAtStart : Config.SpawnHidden;

            for (int attempt = 0; attempt < 6; attempt++)
            {
                double roll = _random.NextDouble() * candidates.Total;
                EtsLaneGraph.Lane lane = null;

                foreach (int id in candidates.Lanes)
                {
                    EtsLaneGraph.Lane l = _graph.Get(id);
                    if (l == null) continue;
                    roll -= l.Density * l.Length;
                    if (roll <= 0) { lane = l; break; }
                }

                if (lane == null) continue;

                float s = (float)(_random.NextDouble() * lane.Length);
                Vector3 p = _graph.PointAt(lane, s);
                float nearest = Nearest(p, foci);

                if (nearest < hidden || (p - focus.Position).magnitude > Config.SpawnFar) continue;
                if (!Free(lane, s, 25f) || NearObstacle(p, obstacles, 30f)) continue;

                // Not within its stopping distance (at the fastest spawn speed, plus 10 m) of a
                // junction mouth: refused there, it could not stop and ran in unreserved, across
                // curves granted to others (52 such spawns of 937, and 2 overlapping pairs, harness
                // 1750 teleport run).
                float fastest = Math.Min(lane.SpeedLimit * 1.05f, lane.CurveLimit) * 0.8f;
                if (MouthWithin(lane, s, fastest * fastest / (2f * Config.ComfortBraking) + 10f, 0)) continue;

                // Near a player, only a car whose model this client has ready: the others would
                // appear a moment later, in plain sight.
                int vehicle = PickVehicle(nearest < Config.SpawnHidden);
                if (vehicle < 0) return false;
                EtsTrafficCatalog.Vehicle v = _catalog.Vehicles[vehicle];

                _serials[slot] = (_serials[slot] + 1) & 15;

                var car = new Car
                {
                    Slot = slot,
                    Serial = _serials[slot],
                    Vehicle = vehicle,
                    Colour = v.Colours.Length > 0 ? _random.Next(v.Colours.Length) : 0,
                    Lane = lane.Id,
                    S = s,
                    Factor = 0.9f + (float)_random.NextDouble() * 0.15f,
                    HalfLength = v.Length * 0.5f,
                    HalfWidth = v.Width * 0.5f
                };

                car.V = Math.Min(lane.SpeedLimit * car.Factor, lane.CurveLimit) * 0.8f;
                car.Position = p;
                car.Direction = _graph.DirectionAt(lane, s);
                car.Yaw = Mathf.Atan2(car.Direction.x, car.Direction.z) * Mathf.Rad2Deg;
                Slots[slot] = car;
                Spawned++;
                return true;
            }

            return false;
        }

        /// <summary>Whether a junction mouth lies less than 'distance' ahead of s, on any way on (road lanes followed four deep).</summary>
        private bool MouthWithin(EtsLaneGraph.Lane lane, float s, float distance, int depth)
        {
            float left = distance - (lane.Length - s);

            if (left <= 0f)
            {
                return false;
            }

            foreach (int n in lane.Next)
            {
                EtsLaneGraph.Lane next = _graph.Get(n);
                if (next == null) continue;
                if (next.InPrefab) return true;
                if (depth < 4 && MouthWithin(next, 0f, left, depth + 1)) return true;
            }

            return false;
        }

        private bool Free(EtsLaneGraph.Lane lane, float s, float room)
        {
            foreach (Car car in Slots)
            {
                if (car == null) continue;
                if (car.Lane == lane.Id && Math.Abs(car.S - s) < room) return false;
                if ((car.Position - _graph.PointAt(lane, s)).sqrMagnitude < room * room) return false;
            }

            return true;
        }

        private static bool NearObstacle(Vector3 p, IList<Obstacle> obstacles, float within)
        {
            if (obstacles == null) return false;
            foreach (Obstacle o in obstacles) if ((o.Position - p).sqrMagnitude < within * within) return true;
            return false;
        }

        private List<int> _pool;
        private List<float> _poolWeights;

        /// <summary>
        /// Host hook: whether this client can draw a car now (its model is built). Asked only for
        /// a car appearing within SpawnHidden of a player. Null: every car is ready (offline).
        /// </summary>
        public Func<int, bool> VehicleReady;

        /// <summary>The cars this country spawns, most likely first (the order a host should prepare their models in).</summary>
        public IList<int> Pool
        {
            get
            {
                MakePool();
                return _pool;
            }
        }

        private void MakePool()
        {
            if (_pool != null)
            {
                return;
            }

            var entries = new List<KeyValuePair<int, float>>(_catalog.Ratios(Config.Country));
            entries.Sort((a, b) => b.Value.CompareTo(a.Value) != 0 ? b.Value.CompareTo(a.Value) : a.Key.CompareTo(b.Key));
            _pool = new List<int>();
            _poolWeights = new List<float>();

            foreach (KeyValuePair<int, float> kv in entries)
            {
                if (_catalog.Vehicles[kv.Key].Model == null || kv.Key > 127) continue;
                _pool.Add(kv.Key);
                _poolWeights.Add(kv.Value);
            }
        }

        /// <summary>
        /// A car by the country's spawn ratios (Poland's for now: the start is Szczecin). Never
        /// reads a file: a car whose bounds are not known yet (EtsTrafficCatalog.Prepare) is not
        /// picked. 'near': only cars VehicleReady says can be drawn.
        /// </summary>
        private int PickVehicle(bool near)
        {
            MakePool();
            float total = 0f;

            for (int i = 0; i < _pool.Count; i++)
            {
                if (Pickable(_pool[i], near)) total += _poolWeights[i];
            }

            if (total <= 0f) return -1;
            double roll = _random.NextDouble() * total;
            int last = -1;

            for (int i = 0; i < _pool.Count; i++)
            {
                if (!Pickable(_pool[i], near)) continue;
                last = _pool[i];
                roll -= _poolWeights[i];
                if (roll <= 0) return _pool[i];
            }

            return last;
        }

        private bool Pickable(int vehicle, bool near)
        {
            return _catalog.Vehicles[vehicle].BoundsKnown && (!near || VehicleReady == null || VehicleReady(vehicle));
        }

        // ------------------------------------------------------------------ taking over

        /// <summary>
        /// A car inherited from the wire (a new master): put on the lane its pose lies on, with
        /// its speed, and given the prefab lanes it is already inside. False when no lane fits;
        /// the caller frees the slot.
        /// </summary>
        public bool Adopt(int slot, int serial, int vehicle, int colour, Vector3 position, float yawDegrees, float speed, int flags = 0)
        {
            float yaw = yawDegrees * Mathf.Deg2Rad;
            var heading = new Vector3(Mathf.Sin(yaw), 0f, Mathf.Cos(yaw));

            if (vehicle < 0 || vehicle >= _catalog.Vehicles.Count)
            {
                return false;
            }

            // A wreck on the wire stays a wreck where it stands, lane or no lane (it tries to drive on in time, Recover).
            if ((flags & FlagWreck) != 0)
            {
                EtsTrafficCatalog.Vehicle w = _catalog.Vehicles[vehicle];
                _serials[slot] = serial;

                Slots[slot] = new Car
                {
                    Slot = slot, Serial = serial, Vehicle = vehicle, Colour = colour,
                    Lane = -1, V = speed, Factor = 1f,
                    HalfLength = w.Length * 0.5f, HalfWidth = w.Width * 0.5f,
                    Position = position, Direction = heading, Yaw = yawDegrees,
                    Wrecked = true, WreckedAt = _time, WreckVelocity = heading * speed, NextRecover = _time + Config.WreckRecover,
                    Flags = WreckFlags, Why = WhyWreck
                };

                return true;
            }

            if (!_graph.Match(position, heading, out int laneId, out float s))
            {
                return false;
            }

            // Bounds not read yet (a car of another country's pool): the catalog's defaults, a 3 x 1.5 m car.
            EtsTrafficCatalog.Vehicle v = _catalog.Vehicles[vehicle];
            _serials[slot] = serial;

            var car = new Car
            {
                Slot = slot, Serial = serial, Vehicle = vehicle, Colour = colour,
                Lane = laneId, S = s, V = speed, Factor = 1f,
                HalfLength = v.Length * 0.5f, HalfWidth = v.Width * 0.5f,
                Position = position, Direction = heading, Yaw = yawDegrees
            };

            EtsLaneGraph.Lane lane = _graph.Get(laneId);

            if (lane.InPrefab)
            {
                // Already inside a junction: hold what it is on and the rest of its way out, without
                // asking. Holding only the curve it was on, it drove the rest unreserved (a car asks
                // only at a mouth) and others were granted curves crossing it: 14,838 car-checks on
                // unheld curves by 59 adopted cars in 29 take-overs, 7 overlapping pairs (harness
                // 1750, take-over mode, 1,800 s, seed 5). With this, 0 checks, and 2 overlapping
                // pairs against 10 over seeds 5-7, both between cars adopted at a hand-off (the
                // wire carries poses, not grants or routes, so a way out is chosen afresh here).
                HoldInside(car);
            }

            // A car on a road lane asks at its mouth on the first step, like any other, once every
            // car inside is adopted and holding. Granting one too near its mouth to stop at once
            // (the old master had let it in) was tried: done here, before the cars inside were
            // adopted, it was granted curves they then held too - 6 overlapping pairs against 2
            // over three 1,800 s take-over runs. Refused, it brakes hard and, if it still runs in,
            // holds its way out (Advance).
            Slots[slot] = car;
            return true;
        }

        /// <summary>
        /// A car inside a junction it may not have been granted (adopted, or run in because it could
        /// not stop): holds the curve it is on and the rest of its way to the next road, as a grant
        /// would, so no conflicting curve is given out while it drives them. It never waits there,
        /// so holding cannot block it.
        /// </summary>
        private void HoldInside(Car car)
        {
            Hold(car.Lane, car);
            EnsureRoute(car, 0f, true);

            foreach (int id in car.Route)
            {
                EtsLaneGraph.Lane l = _graph.Get(id);
                if (l == null || !l.InPrefab) break;
                Hold(id, car);
            }
        }

        public void Clear()
        {
            for (int i = 0; i < Slots.Length; i++) Slots[i] = null;
            _holders.Clear();
            _waiting.Clear();
            Count = 0;
        }

        /// <summary>Removes the car in a slot, if any (a slot the wire cannot carry).</summary>
        public void Drop(int slot)
        {
            if (slot >= 0 && slot < Slots.Length && Slots[slot] != null)
            {
                Remove(Slots[slot], ref RemovedLost);
            }
        }
    }
}
