using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace SomniumSpace.Worlds.SomniumEuroTruck.Ets
{
    /// <summary>
    /// The lanes cars drive, built from the map without any geometry: every road's lanes as
    /// offset copies of its centre curve, every prefab's navigation curves placed on its nodes,
    /// and the joins between them. In the world's Unity frame (Unity = game - centre, z negated),
    /// using only the managed parts of Vector3 so the offline harness can run it.
    ///
    /// Where a road's lanes are (measured, harness Program1375/1376, whole demo map, 33,149
    /// road ends that sit on a prefab node compared with that prefab's input-lane curves):
    ///   - two-way looks (lanes on both sides): lane i = road_offset + 4.5 (i + 0.5) + the x of
    ///     lane_offsets[i], to the right of the direction of travel: legacy 1,150 of 1,150,
    ///     template 13,969 of 13,983, template with lane_offsets 4,670 of 4,676;
    ///   - one-way looks (lanes on one side only): lane k = road_offset + 4.5 (k + 0.5 -
    ///     ceil(n / 2)) + lane_offsets[k].x: template 11,422 of 11,435, template with offsets
    ///     113 of 113, legacy 6 of 18 (look9 "Exit road" is not regular);
    ///   - roads with header flag bit 15 (TruckLib: LeftHandTraffic; 4,086 roads) mirror it:
    ///     1,769 of 1,774 legacy two-way;
    ///   - lanes_right drive start -> end, lanes_left end -> start (a road that ENDS on a prefab
    ///     node feeds it with its lanes_right; that is how every comparison above was paired).
    ///   - header flag bit 22 (TruckLib: !AiVehicles) is set on 13,477 roads: no cars there.
    /// Which lane TYPE (lanes_right[i]) belongs to which of those positions was not checked
    /// (the comparison sorted both sides); the list order is taken as centre-out for two-way
    /// roads and left-to-right for one-way roads - NOT DETERMINED.
    ///
    /// Roads and prefabs a DLC guards never get here: EtsMapSector drops them from its typed
    /// lists (DropGuarded), so a lane can only lie on what this install shows.
    ///
    /// Road ends on the demo map: 45,999 on a prefab, 90,800 on another road, 12,587 on nothing
    /// (Program1375). Joins are therefore made by position: a lane's end meets every lane start
    /// within 0.75 m across the ground (and 2.5 m in height) that heads the same way, whatever
    /// items they belong to; inside a prefab the curves' own 'next' links are used (they meet
    /// end-to-start within 5 cm, all 55,998).
    ///
    /// Building is incremental. A sector's lanes used to be built in one call - about 23 ms, and
    /// one junction alone (551 curves) could take 11 ms - which a headset at 90 Hz cannot afford.
    /// Now a sector is built by an iterator whose steps are each a road, a slice of a junction's
    /// curves, one junction entry's routes or a batch of joins, and Work() runs steps until a
    /// frame budget is spent. Nothing a car can reach changes until the last step, which joins
    /// the older open ends onto the new lanes and registers the sector (Version is bumped then),
    /// so the simulation never sees half a sector. Run to the end in one go (BuildSector,
    /// EnsureAround) it produces exactly what the one-shot build did: the same lanes in the same
    /// order with the same joins (TrafficTest checks it).
    /// </summary>
    public sealed class EtsLaneGraph
    {
        public const float LaneWidth = 4.5f;
        public const uint NoAiFlag = 1u << 22;
        public const uint LeftHandFlag = 1u << 15;
        public const float JoinDistance = 0.75f;

        /// <summary>Most a join's two ends may differ in height (JoinDistance is measured across the ground).</summary>
        public const float JoinHeight = 2.5f;

        /// <summary>Edge of the cells LanesNear and SampleCell index lane samples by, metres.</summary>
        public const float SampleCellSize = 16f;

        /// <summary>Most sideways acceleration a car takes a bend at, m/s^2 (a comfortable figure, not from the game).</summary>
        public const float LateralAcceleration = 3.0f;

        /// <summary>Curves of one junction placed per build step (the biggest descriptor has 551).</summary>
        public const int CurvesPerStep = 24;

        private static readonly int[] None = new int[0];

        public sealed class Lane
        {
            public int Id;

            /// <summary>The centre curve (road) or the curve itself (prefab), as a Hermite, Unity frame.</summary>
            public Vector3 P0, T0, P1, T1;

            /// <summary>Sideways offset from the centre curve, right of the curve's own direction.</summary>
            public float Offset;

            /// <summary>Travelled end -> start (a road's lanes_left).</summary>
            public bool Reverse;

            public float Length;

            /// <summary>Arc length at q = k / 16 of the travel parameter, 17 entries.</summary>
            public float[] Cum;

            /// <summary>The limit for cars, m/s; SpeedTruck and SpeedBus for those speed classes (speed_limits.sii, mph converted).</summary>
            public float SpeedLimit;
            public float SpeedTruck;
            public float SpeedBus;
            public float CurveLimit;
            public float Density;
            public bool Urban;

            /// <summary>The country (country_data country_id) from the map nodes' flags (Finish), -1 unknown.</summary>
            public int Country = -1;

            /// <summary>A road lane's type (its spawn rules); null inside a prefab.</summary>
            public EtsTrafficCatalog.LaneType Type;

            /// <summary>Inside a prefab: the curve's allowed vehicles (EtsPrefabNav.Allow*) and its priority modifier (flags bits 16-19).</summary>
            public int Allowed = EtsPrefabNav.AllowAll;
            public int Priority;

            public ulong Item;
            public EtsPrefabNav Nav;
            public int Curve = -1;
            public int Blinker;
            public int Semaphore = -1;
            public long Sector;

            public Vector3 Start, End, StartDirection, EndDirection;
            public int[] Next = None;
            public readonly List<int> Previous = new List<int>(2);

            public bool InPrefab => Nav != null;
        }

        public readonly EtsMap Map;
        public readonly Vector3 CenterGame;
        private readonly EtsFileSystem _files;
        private readonly EtsTrafficCatalog _catalog;

        public readonly List<Lane> Lanes = new List<Lane>();
        private readonly Dictionary<long, List<int>> _bySector = new Dictionary<long, List<int>>();
        private readonly Dictionary<long, List<int>> _starts = new Dictionary<long, List<int>>();
        private readonly Dictionary<long, List<int>> _samples = new Dictionary<long, List<int>>();
        private readonly Dictionary<string, EtsPrefabNav> _navs = new Dictionary<string, EtsPrefabNav>(StringComparer.Ordinal);
        private readonly HashSet<string> _navAsked = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<EtsPrefabNav> _routesKnown = new HashSet<EtsPrefabNav>();
        private readonly HashSet<int> _open = new HashSet<int>();

        /// <summary>
        /// Host hook: may the sector at (x, z) be touched now? True when it is already read or
        /// the host allows a read. A sector is only started when its own and its eight
        /// neighbours' answers are all yes, so a build never reads the archives unless the host
        /// said so. Null: always (offline, and the one-shot calls).
        /// </summary>
        public Func<int, int, bool> MayRead;

        /// <summary>
        /// Host hook: have this descriptor parsed somewhere else (the host's worker thread) and
        /// hand it back through AddNav. Null: parse it here, at once.
        /// </summary>
        public Action<string> RequestNav;

        /// <summary>
        /// Host hook: a junction with traffic lights (a curve naming a semaphore) is being placed - the host
        /// registers its lights' programmes (EtsSemaphores.Prepare), so the cars' Light hook answers for it. Called
        /// on the thread that runs Work, once per placement; guarded here. Null: nothing (offline).
        /// </summary>
        public Action<EtsMapPrefab> PrefabWithLights;

        // Work queue: sectors to build (nearest to where they were asked for first), sectors to
        // drop, and the one job in progress.
        private readonly List<long> _toBuild = new List<long>();
        private readonly List<float> _toBuildDistance = new List<float>();
        private readonly List<long> _toDrop = new List<long>();
        private IEnumerator<bool> _job;
        private long _jobSector;
        private bool _jobIsDrop;

        public int Built => _bySector.Count;
        public int LaneCount { get; private set; }
        public int Joins { get; private set; }

        /// <summary>Bumped whenever what a car can reach changes: a sector published or dropped.</summary>
        public int Version { get; private set; }

        /// <summary>Sectors waiting to be built or dropped, and the one in progress.</summary>
        public int Pending => _toBuild.Count + _toDrop.Count + (_job != null ? 1 : 0);

        /// <summary>The job in progress is waiting for a descriptor from the host.</summary>
        public bool Waiting { get; private set; }

        public int Navs => _navs.Count;

        public EtsLaneGraph(EtsMap map, EtsFileSystem files, EtsTrafficCatalog catalog, Vector3 centerGame)
        {
            Map = map;
            _files = files;
            _catalog = catalog;
            CenterGame = centerGame;
        }

        public Lane Get(int id)
        {
            return id >= 0 && id < Lanes.Count ? Lanes[id] : null;
        }

        // ------------------------------------------------------------------ frames

        public Vector3 ToUnity(float[] game)
        {
            return new Vector3(game[0] - CenterGame.x, game[1] - CenterGame.y, -(game[2] - CenterGame.z));
        }

        public static Vector3 ToUnityDirection(float[] game)
        {
            return new Vector3(game[0], game[1], -game[2]);
        }

        public void ToGame(Vector3 unity, out float gx, out float gz)
        {
            gx = unity.x + CenterGame.x;
            gz = CenterGame.z - unity.z;
        }

        public static long SectorKey(int sx, int sz)
        {
            return ((long)sx << 32) ^ (uint)sz;
        }

        public static void SectorOfKey(long key, out int sx, out int sz)
        {
            sx = (int)(key >> 32);
            sz = (int)(uint)key;
        }

        /// <summary>A sector's centre in the Unity frame (y 0).</summary>
        public Vector3 SectorCentre(int sx, int sz)
        {
            float gx = (sx + 0.5f) * EtsMapSector.SectorSize, gz = (sz + 0.5f) * EtsMapSector.SectorSize;
            return new Vector3(gx - CenterGame.x, 0f, CenterGame.z - gz);
        }

        // ------------------------------------------------------------------ what to build

        /// <summary>
        /// Asks for every sector touching a circle round a Unity position, nearest first. Nothing
        /// is built here; Work() builds. Cheap enough to call every frame for every focus.
        /// </summary>
        public void Want(Vector3 unity, float radius)
        {
            ToGame(unity, out float gx, out float gz);
            EtsMapSector.SectorOf(gx - radius, gz - radius, out int x0, out int z0);
            EtsMapSector.SectorOf(gx + radius, gz + radius, out int x1, out int z1);

            for (int sx = x0; sx <= x1; sx++)
            {
                for (int sz = z0; sz <= z1; sz++)
                {
                    long key = SectorKey(sx, sz);

                    // Built, and wanted again: a drop queued while every focus was far (DropFar, then a
                    // job start or Respawn brings the truck back) is cancelled, or the sector would be
                    // dropped under the truck and rebuilt a few frames later (probe 1545).
                    if (_bySector.ContainsKey(key))
                    {
                        _toDrop.Remove(key);
                        continue;
                    }

                    if (_job != null && !_jobIsDrop && _jobSector == key)
                    {
                        continue;
                    }

                    float d = (SectorCentre(sx, sz) - new Vector3(unity.x, 0f, unity.z)).sqrMagnitude;
                    int at = _toBuild.IndexOf(key);

                    if (at >= 0)
                    {
                        // Asked again from nearer: move it up.
                        if (d < _toBuildDistance[at])
                        {
                            _toBuild.RemoveAt(at);
                            _toBuildDistance.RemoveAt(at);
                        }
                        else
                        {
                            continue;
                        }
                    }

                    int insert = 0;
                    while (insert < _toBuildDistance.Count && _toBuildDistance[insert] <= d) insert++;
                    _toBuild.Insert(insert, key);
                    _toBuildDistance.Insert(insert, d);
                    _toDrop.Remove(key);
                }
            }
        }

        /// <summary>
        /// Queues the sectors whose centre is further than 'distance' from every point given for
        /// dropping (keeps memory bounded on a long drive). The job in progress is left alone.
        /// </summary>
        public void DropFar(IList<Vector3> keep, float distance)
        {
            if (keep == null || keep.Count == 0)
            {
                return;
            }

            foreach (long key in _bySector.Keys)
            {
                SectorOfKey(key, out int sx, out int sz);
                Vector3 c = SectorCentre(sx, sz);
                bool far = true;

                foreach (Vector3 p in keep)
                {
                    float dx = c.x - p.x, dz = c.z - p.z;
                    if (dx * dx + dz * dz <= distance * distance) { far = false; break; }
                }

                if (far && !_toDrop.Contains(key))
                {
                    _toDrop.Add(key);
                }
            }
        }

        /// <summary>
        /// Builds (and drops) until the clock passes the budget. Each step is small; the clock is
        /// read between steps. Returns true while work remains.
        /// </summary>
        public bool Work(Stopwatch clock, double budgetMs)
        {
            while (clock.Elapsed.TotalMilliseconds < budgetMs)
            {
                if (_job == null && !StartNext())
                {
                    Waiting = false;
                    return Pending > 0;
                }

                if (!_job.MoveNext())
                {
                    _job = null;
                    Waiting = false;
                    continue;
                }

                if (!_job.Current)
                {
                    // Waiting for a descriptor from the worker: come back next frame.
                    Waiting = true;
                    return true;
                }

                Waiting = false;
            }

            return true;
        }

        private bool StartNext()
        {
            for (int i = 0; i < _toBuild.Count; i++)
            {
                long key = _toBuild[i];

                if (_bySector.ContainsKey(key))
                {
                    _toBuild.RemoveAt(i);
                    _toBuildDistance.RemoveAt(i);
                    i--;
                    continue;
                }

                SectorOfKey(key, out int sx, out int sz);

                if (!NeighboursReadable(sx, sz))
                {
                    continue;
                }

                _toBuild.RemoveAt(i);
                _toBuildDistance.RemoveAt(i);
                _job = BuildSteps(sx, sz);
                _jobSector = key;
                _jobIsDrop = false;
                return true;
            }

            while (_toDrop.Count > 0)
            {
                long key = _toDrop[0];
                _toDrop.RemoveAt(0);

                if (!_bySector.ContainsKey(key))
                {
                    continue;
                }

                _job = DropSteps(key);
                _jobSector = key;
                _jobIsDrop = true;
                return true;
            }

            return false;
        }

        private bool NeighboursReadable(int sx, int sz)
        {
            if (MayRead == null)
            {
                return true;
            }

            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dz = -1; dz <= 1; dz++)
                {
                    if (!MayRead(sx + dx, sz + dz)) return false;
                }
            }

            return true;
        }

        /// <summary>Builds every sector touching a circle round a Unity position at once; at most 'budget' new sectors per call. Offline and tests.</summary>
        public int EnsureAround(Vector3 unity, float radius, int budget)
        {
            ToGame(unity, out float gx, out float gz);
            EtsMapSector.SectorOf(gx - radius, gz - radius, out int x0, out int z0);
            EtsMapSector.SectorOf(gx + radius, gz + radius, out int x1, out int z1);
            int built = 0;

            for (int sx = x0; sx <= x1 && built < budget; sx++)
            {
                for (int sz = z0; sz <= z1 && built < budget; sz++)
                {
                    if (!_bySector.ContainsKey(SectorKey(sx, sz)))
                    {
                        BuildSector(sx, sz);
                        built++;
                    }
                }
            }

            return built;
        }

        public bool IsBuilt(Vector3 unity)
        {
            ToGame(unity, out float gx, out float gz);
            EtsMapSector.SectorOf(gx, gz, out int sx, out int sz);
            return _bySector.ContainsKey(SectorKey(sx, sz));
        }

        /// <summary>Whether every sector touching a circle round a Unity position is built (a focus's spawn area).</summary>
        public bool IsAreaBuilt(Vector3 unity, float radius)
        {
            ToGame(unity, out float gx, out float gz);
            EtsMapSector.SectorOf(gx - radius, gz - radius, out int x0, out int z0);
            EtsMapSector.SectorOf(gx + radius, gz + radius, out int x1, out int z1);

            for (int sx = x0; sx <= x1; sx++)
            {
                for (int sz = z0; sz <= z1; sz++)
                {
                    if (!_bySector.ContainsKey(SectorKey(sx, sz))) return false;
                }
            }

            return true;
        }

        public bool IsSectorBuilt(int sx, int sz)
        {
            return _bySector.ContainsKey(SectorKey(sx, sz));
        }

        /// <summary>Builds one sector completely, now: the incremental build run to its end.</summary>
        public void BuildSector(int sx, int sz)
        {
            IEnumerator<bool> steps = BuildSteps(sx, sz);

            while (steps.MoveNext())
            {
                // With RequestNav set a step may wait for a descriptor; a synchronous caller
                // cannot wait, so the descriptors are parsed here instead (BuildSteps asks
                // through AskNav, which parses at once when RequestNav is null).
                if (!steps.Current && RequestNav != null)
                {
                    Action<string> saved = RequestNav;
                    RequestNav = null;
                    foreach (string path in new List<string>(_navAsked)) if (!_navs.ContainsKey(path)) AskNavNow(path);
                    RequestNav = saved;
                }
            }
        }

        public void DropSector(int sx, int sz)
        {
            long key = SectorKey(sx, sz);

            if (!_bySector.ContainsKey(key))
            {
                return;
            }

            IEnumerator<bool> steps = DropSteps(key);
            while (steps.MoveNext()) { }
        }

        public IEnumerable<long> BuiltSectors => _bySector.Keys;

        // ------------------------------------------------------------------ building a sector, step by step

        /// <summary>
        /// One sector's lanes. Each 'yield return true' ends a step (the caller may stop there
        /// until the next frame); 'yield return false' means waiting on the host (a descriptor
        /// from the worker). The lanes stay unreachable - no index entry, no join from an older
        /// lane - until the last step.
        /// </summary>
        private IEnumerator<bool> BuildSteps(int sx, int sz)
        {
            long key = SectorKey(sx, sz);
            var ids = new List<int>();

            // Roads and prefabs name nodes in the neighbouring sectors. Already read, unless the
            // host allowed a read (MayRead) - offline these are the reads.
            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dz = -1; dz <= 1; dz++)
                {
                    Map.GetSector(sx + dx, sz + dz);
                }
            }

            yield return true;
            EtsMapSector sector = Map.GetSector(sx, sz);

            if (sector != null)
            {
                // Every descriptor this sector needs is asked for first, so the worker parses them
                // while the roads are being laid.
                foreach (EtsMapPrefab prefab in sector.Prefabs)
                {
                    string path = DescriptorPath(prefab);
                    if (path != null) AskNav(path);
                }

                foreach (EtsMapRoad road in sector.Roads)
                {
                    BuildRoad(road, sector, key, ids);
                    yield return true;
                }

                foreach (EtsMapPrefab prefab in sector.Prefabs)
                {
                    string path = DescriptorPath(prefab);

                    if (path == null)
                    {
                        continue;
                    }

                    while (!_navs.ContainsKey(path))
                    {
                        yield return false;
                    }

                    EtsPrefabNav nav = _navs[path];
                    IEnumerator<bool> placing = BuildPrefab(prefab, nav, sector, key, ids);

                    while (placing.MoveNext())
                    {
                        yield return true;
                    }

                    // A descriptor's routes, one entry at a time, the first time it is used, so a
                    // car choosing its way through the junction finds them ready (all of one
                    // descriptor's took up to 3.0 ms, hw3-2_x_city2-1, Program1400).
                    if (nav != null && _routesKnown.Add(nav))
                    {
                        foreach (int[] inputs in nav.NodeInputs)
                        {
                            foreach (int input in inputs)
                            {
                                nav.ChainsFrom(input);
                                yield return true;
                            }
                        }
                    }
                }
            }

            // Starts first (cheap): the joins below look lanes up by where they start.
            foreach (int id in ids)
            {
                Put(_starts, Cell(Lanes[id].Start, 4f), id);
            }

            yield return true;

            // Where each lane runs, for LanesNear: the costly part, done a few lanes a step.
            var cells = new List<long>[ids.Count];

            for (int i = 0; i < ids.Count; i++)
            {
                cells[i] = SampleCells(Lanes[ids[i]]);
                if ((i & 15) == 15) yield return true;
            }

            // Joins among the new lanes and onto older lanes' starts. A new lane's own Next and an
            // older lane's Previous change; nothing a car can reach does.
            for (int i = 0; i < ids.Count; i++)
            {
                Join(Lanes[ids[i]]);
                if ((i & 31) == 31) yield return true;
            }

            // The new lanes into the sample index, a batch a step. LanesNear can return them from
            // here on; they are fully joined already, so a car put on one drives on normally.
            for (int i = 0; i < ids.Count; i++)
            {
                foreach (long cell in cells[i]) Put(_samples, cell, ids[i]);
                if ((i & 63) == 63) yield return true;
            }

            // Publication, in one step: older lanes that led nowhere may now lead onto these.
            var open = new List<int>(_open);

            foreach (int id in open)
            {
                Lane lane = Get(id);

                if (lane != null && lane.Next.Length == 0)
                {
                    Join(lane);
                }
            }

            _bySector[key] = ids;
            Version++;
        }

        private IEnumerator<bool> DropSteps(long key)
        {
            List<int> ids = _bySector[key];
            _bySector.Remove(key);
            var gone = new HashSet<int>(ids);

            // Unreachable first: whoever led here now leads nowhere, until this sector is built again.
            for (int i = 0; i < ids.Count; i++)
            {
                Lane lane = Lanes[ids[i]];
                if (lane == null) continue;

                foreach (int previous in lane.Previous)
                {
                    Lane before = Get(previous);

                    if (before == null || gone.Contains(previous)) continue;

                    var kept = new List<int>();
                    foreach (int n in before.Next) if (!gone.Contains(n)) kept.Add(n);
                    before.Next = kept.Count == 0 ? None : kept.ToArray();
                    if (before.Next.Length == 0) _open.Add(before.Id);
                }

                foreach (int next in lane.Next)
                {
                    if (!gone.Contains(next)) Get(next)?.Previous.Remove(lane.Id);
                }
            }

            Version++;
            yield return true;

            for (int i = 0; i < ids.Count; i++)
            {
                Lane lane = Lanes[ids[i]];
                if (lane != null) Unindex(lane);
                if ((i & 15) == 15) yield return true;
            }

            foreach (int id in ids)
            {
                if (Lanes[id] == null) continue;
                _open.Remove(id);
                Lanes[id] = null;
                LaneCount--;
            }

            Version++;
        }

        private string DescriptorPath(EtsMapPrefab prefab)
        {
            EtsSiiUnit def = Map.PrefabDefinition(prefab.PrefabToken);
            string path = def?.GetString("prefab_desc");
            return string.IsNullOrEmpty(path) ? null : path;
        }

        private void AskNav(string path)
        {
            if (_navs.ContainsKey(path) || !_navAsked.Add(path))
            {
                return;
            }

            if (RequestNav != null)
            {
                RequestNav(path);
            }
            else
            {
                AskNavNow(path);
            }
        }

        private void AskNavNow(string path)
        {
            _navs[path] = EtsPrefabNav.Parse(_files.Read(path), out _);
        }

        /// <summary>A descriptor parsed elsewhere (null when it could not be), for the build waiting on it.</summary>
        public void AddNav(string path, EtsPrefabNav nav)
        {
            _navs[path] = nav;
        }

        public EtsPrefabNav Nav(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            if (!_navs.TryGetValue(path, out EtsPrefabNav nav))
            {
                AskNavNow(path);
                nav = _navs[path];
            }

            return nav;
        }

        private void BuildRoad(EtsMapRoad road, EtsMapSector sector, long key, List<int> ids)
        {
            if ((road.Flags & NoAiFlag) != 0 || road.StartNode == 0 || road.EndNode == 0 || road.StartNode == road.EndNode)
            {
                return;
            }

            EtsMapNode start = Map.FindNode(road.StartNode), end = Map.FindNode(road.EndNode);
            EtsSiiUnit look = Map.RoadLook(road.LookToken);

            if (start == null || end == null || look == null)
            {
                return;
            }

            List<string> right = look.GetArray("lanes_right"), left = look.GetArray("lanes_left");
            float roadOffset = look.GetFloat("road_offset", 0f);
            bool mirrored = (road.Flags & LeftHandFlag) != 0;
            float length = road.Length > 0.5f ? road.Length : 1f;
            Vector3 p0 = ToUnity(start.Position), p1 = ToUnity(end.Position);
            Vector3 t0 = ToUnityDirection(start.Forward()) * length, t1 = ToUnityDirection(end.Forward()) * length;

            int country = RoadCountry(start, end);
            AddSide(road, look, right, "right", right.Count == 0 || left.Count == 0, roadOffset, mirrored, false, p0, t0, p1, t1, sector, key, ids, country);
            AddSide(road, look, left, "left", right.Count == 0 || left.Count == 0, roadOffset, mirrored, true, p0, t0, p1, t1, sector, key, ids, country);
        }

        /// <summary>
        /// The country a road lies in, from its nodes' flags as the map stores it: bits 8-15 are the country id ahead of the
        /// node (along the road that leaves it), bits 16-23 the one behind (country_data country_id; probe nodes2: within
        /// 2.5 km of Dover, Calais, Prague and Szczecin 1,356 of 1,575 roads carry one, 1,569 the same at both ends; the
        /// borders read "8,3 -> 8,8" (Czech | Poland at a start node) and "2,2 -> 4,2" (Belgium | France at an end node).
        /// So a road is its start node's 'ahead' id, else its end node's 'behind' id, else whichever is set. 0: none.
        /// </summary>
        public static int RoadCountry(EtsMapNode start, EtsMapNode end)
        {
            int a = start != null ? (int)((start.Flags >> 8) & 0xFF) : 0;
            if (a != 0) return a;
            int b = end != null ? (int)((end.Flags >> 16) & 0xFF) : 0;
            if (b != 0) return b;
            int c = start != null ? (int)((start.Flags >> 16) & 0xFF) : 0;
            if (c != 0) return c;
            return end != null ? (int)((end.Flags >> 8) & 0xFF) : 0;
        }

        /// <summary>A prefab's country: the first of its nodes that carries one (either side).</summary>
        private int PrefabCountry(EtsMapPrefab prefab)
        {
            foreach (ulong uid in prefab.NodeUids)
            {
                EtsMapNode node = Map.FindNode(uid);
                if (node == null) continue;
                int id = RoadCountry(node, node);
                if (id != 0) return id;
            }

            return 0;
        }

        private void AddSide(EtsMapRoad road, EtsSiiUnit look, List<string> lanes, string side, bool oneWay, float roadOffset, bool mirrored, bool reverse,
            Vector3 p0, Vector3 t0, Vector3 p1, Vector3 t1, EtsMapSector sector, long key, List<int> ids, int country)
        {
            List<string> offsets = look.GetArray("lane_offsets_" + side);
            int n = lanes.Count;

            for (int i = 0; i < n; i++)
            {
                EtsTrafficCatalog.LaneType type = _catalog.Lane(lanes[i]);

                if (type == null || !type.Cars)
                {
                    continue;
                }

                float extra = 0f;

                if (i < offsets.Count)
                {
                    float[] xy = EtsSiiValue.ParseFloats(offsets[i]);
                    if (xy.Length > 0) extra = xy[0];
                }

                float offset = oneWay
                    ? roadOffset + LaneWidth * (i + 0.5f - (float)Math.Ceiling(n / 2f)) + extra
                    : roadOffset + LaneWidth * (i + 0.5f) + extra;

                if (mirrored)
                {
                    offset = -offset;
                }

                var lane = new Lane
                {
                    P0 = p0, T0 = t0, P1 = p1, T1 = t1,
                    Offset = offset,
                    Reverse = reverse,
                    Item = road.Uid,
                    Sector = key,
                    Density = type.Density,
                    Type = type
                };

                Finish(lane, type.SpeedClass, sector, country);
                Add(lane, ids);
            }
        }

        /// <summary>A prefab's curves placed on its nodes, CurvesPerStep a step, then its own links.</summary>
        private IEnumerator<bool> BuildPrefab(EtsMapPrefab prefab, EtsPrefabNav nav, EtsMapSector sector, long key, List<int> ids)
        {
            if (nav == null || nav.Curves.Length == 0 || nav.NodePosition.Length != prefab.NodeUids.Length)
            {
                yield break;
            }

            // The yaw part of EtsMapBuilder's placement: descriptor node Origin on map node 0, turned
            // by the yaw that takes its direction onto the map node's forward; then lifted onto the
            // slope the map nodes give (Slope), which stands in for the rest of node 0's rotation
            // that the drawn model now takes (EtsMapBuilder.TryPrefabFrame).
            EtsMapNode n0 = Map.FindNode(prefab.NodeUids[0]);

            if (n0 == null)
            {
                yield break;
            }

            if (PrefabWithLights != null)
            {
                bool lights = false;

                foreach (EtsPrefabNav.Curve curve in nav.Curves)
                {
                    if (curve.ForCars && curve.Semaphore >= 0)
                    {
                        lights = true;
                        break;
                    }
                }

                if (lights)
                {
                    try
                    {
                        PrefabWithLights(prefab);
                    }
                    catch (Exception)
                    {
                        // The lights are a nicety: without them the junction runs on reservations alone.
                    }
                }
            }

            int origin = Math.Max(0, Math.Min(prefab.Origin, nav.NodePosition.Length - 1));
            float[] anchor = nav.NodePosition[origin], anchorDirection = nav.NodeDirection[origin];
            float[] mapForward = n0.Forward();
            double yaw = Math.Atan2(mapForward[2], mapForward[0]) - Math.Atan2(anchorDirection[2], anchorDirection[0]);
            float c = (float)Math.Cos(yaw), s = (float)Math.Sin(yaw);
            Slope(prefab, nav, origin, anchor, n0, c, s, out float gx, out float gz);
            var first = new int[nav.Curves.Length];
            int country = PrefabCountry(prefab);

            for (int i = 0; i < nav.Curves.Length; i++)
            {
                first[i] = -1;
                EtsPrefabNav.Curve curve = nav.Curves[i];

                // Every curve but the player-only ones (511): the small vehicles' (bit 5) and the large ones' (bit 6), each
                // vehicle keeping to its own (EtsPrefabNav.ChainsFrom, EtsTrafficSim).
                if (curve.AllowedVehicles == EtsPrefabNav.AllowPlayerOnly)
                {
                    continue;
                }

                float[] a = Place(curve.Start, anchor, n0.Position, c, s), b = Place(curve.End, anchor, n0.Position, c, s);
                float[] fa = Turn(curve.StartForward, c, s), fb = Turn(curve.EndForward, c, s);
                float chord = Vector3.Distance(ToUnity(a), ToUnity(b));

                // Onto the prefab's slope: points by the plane, tangents by its gradient, so the
                // curve is the same curve tilted (a Hermite commutes with that shear).
                a[1] += gx * (a[0] - n0.Position[0]) + gz * (a[2] - n0.Position[2]);
                b[1] += gx * (b[0] - n0.Position[0]) + gz * (b[2] - n0.Position[2]);
                fa[1] += gx * fa[0] + gz * fa[2];
                fb[1] += gx * fb[0] + gz * fb[2];
                Vector3 p0 = ToUnity(a), p1 = ToUnity(b);

                var lane = new Lane
                {
                    P0 = p0, P1 = p1,
                    T0 = ToUnityDirection(fa) * chord,
                    T1 = ToUnityDirection(fb) * chord,
                    Item = prefab.Uid,
                    Nav = nav,
                    Curve = i,
                    Blinker = curve.Blinker,
                    Semaphore = curve.Semaphore,
                    Sector = key,
                    Allowed = curve.AllowedVehicles,
                    Priority = curve.Priority
                };

                Finish(lane, "local_road", sector, country);
                Add(lane, ids);
                first[i] = lane.Id;

                if (i % CurvesPerStep == CurvesPerStep - 1)
                {
                    yield return true;
                }
            }

            // The curves' own links.
            for (int i = 0; i < nav.Curves.Length; i++)
            {
                if (first[i] < 0) continue;
                var next = new List<int>();

                foreach (int n in nav.Curves[i].Next)
                {
                    if (first[n] >= 0) next.Add(first[n]);
                }

                Lane lane = Lanes[first[i]];
                lane.Next = next.Count == 0 ? None : next.ToArray();

                foreach (int n in lane.Next)
                {
                    Lanes[n].Previous.Add(lane.Id);
                    Joins++;
                }
            }
        }

        private static float[] Place(float[] p, float[] anchor, float[] node, float c, float s)
        {
            float x = p[0] - anchor[0], z = p[2] - anchor[2];
            return new[] { node[0] + x * c - z * s, node[1] + (p[1] - anchor[1]), node[2] + x * s + z * c };
        }

        private static float[] Turn(float[] d, float c, float s)
        {
            return new[] { d[0] * c - d[2] * s, d[1], d[0] * s + d[2] * c };
        }

        /// <summary>
        /// The slope a prefab stands on, as the height gained per metre along the game's x (gx) and
        /// z (gz) from map node 0; zero when level or when the nodes do not fit one plane.
        ///
        /// The game stands a prefab on node 0 with that node's whole rotation, pitch and roll
        /// included; the yaw-only placement here leaves the other nodes off
        /// their map heights on a slope. Probe 1751, all 949 prefabs within 7 km of the start: 222
        /// of 1,696 nodes (166 prefabs) more than 0.3 m off, p99 1.61 m, worst 3.73 m. Node 0's
        /// whole rotation puts all 1,693 nodes of level descriptors within 5 mm of their map
        /// heights, and a plane through node 0 fits the 27 nodes of the nine tilted prefabs with
        /// four or more nodes within 1 mm. The plane is fitted to the map nodes themselves, so each
        /// road lane meets its prefab lane at the same height: a join, and no 1-2 m jump for a car
        /// crossing it. Across the ground the yaw-only placement stays (it misses the map nodes by
        /// p99 4 cm).
        ///
        /// The drawn junction model (EtsMapBuilder.BuildPrefab) now takes node 0's whole rotation
        /// (EtsMapBuilder.TryPrefabFrame, Phase 1), so the lanes and the drawn surface agree. Before
        /// that, probe 1803 (checker): within 7 km of the start 1,101 of 12,660 junction lanes (166
        /// junctions) passed more than 0.3 m off the yaw-only drawn surface, worst 3.75 m. After it,
        /// probe 1922 (F, reproduced by its checker): 0 lanes more than 0.3 m off, worst 0.084 m.
        /// Whole map: 6,513 of 15,093 junctions are tilted (steepest 17 %).
        /// </summary>
        private void Slope(EtsMapPrefab prefab, EtsPrefabNav nav, int origin, float[] anchor, EtsMapNode n0, float c, float s, out float gx, out float gz)
        {
            gx = 0f;
            gz = 0f;
            int n = nav.NodePosition.Length;
            var dx = new float[n];
            var dz = new float[n];
            var dy = new float[n];
            var known = new bool[n];
            double sxx = 0, sxz = 0, szz = 0, sxy = 0, szy = 0;

            // Map node k stands on descriptor node (Origin + k) mod n (EtsMapPrefab.Origin).
            for (int k = 1; k < n; k++)
            {
                EtsMapNode node = Map.FindNode(prefab.NodeUids[k]);
                if (node == null) continue;
                float[] placed = Place(nav.NodePosition[(origin + k) % n], anchor, n0.Position, c, s);
                float mx = placed[0] - node.Position[0], mz = placed[2] - node.Position[2];

                // Not where this placement puts it at all: the node order is not the one assumed.
                if (mx * mx + mz * mz > 1f) return;

                dx[k] = placed[0] - n0.Position[0];
                dz[k] = placed[2] - n0.Position[2];
                dy[k] = node.Position[1] - placed[1];
                known[k] = true;
                sxx += dx[k] * dx[k]; sxz += dx[k] * dz[k]; szz += dz[k] * dz[k];
                sxy += dx[k] * dy[k]; szy += dz[k] * dy[k];
            }

            double spread = sxx + szz, det = sxx * szz - sxz * sxz;
            if (spread < 1.0) return;
            double px, pz;

            if (det > 1e-6 * spread * spread)
            {
                px = (sxy * szz - szy * sxz) / det;
                pz = (szy * sxx - sxy * sxz) / det;
            }
            else
            {
                // The nodes lie on one line with node 0 (every two-node piece): the slope along it.
                px = sxy / spread;
                pz = szy / spread;
            }

            // A plane that does not explain the nodes, or steeper than any road: leave it level.
            if (px * px + pz * pz > 0.3 * 0.3) return;

            for (int k = 1; k < n; k++)
            {
                if (known[k] && Math.Abs(dy[k] - (px * dx[k] + pz * dz[k])) > 0.25) return;
            }

            gx = (float)px;
            gz = (float)pz;
        }

        private void Add(Lane lane, List<int> ids)
        {
            lane.Id = Lanes.Count;
            Lanes.Add(lane);
            ids.Add(lane.Id);
            LaneCount++;
        }

        /// <summary>Arc length table, end points, directions, the speed limit and the bend limit.</summary>
        private void Finish(Lane lane, string speedClass, EtsMapSector sector, int countryId)
        {
            lane.Cum = new float[17];
            Vector3 previous = PointAtQ(lane, 0f);
            float worst = 0f;
            Vector3 lastDirection = DirectionAtQ(lane, 0f);

            for (int k = 1; k <= 16; k++)
            {
                float q = k / 16f;
                Vector3 p = PointAtQ(lane, q);
                float step = Vector3.Distance(p, previous);
                lane.Cum[k] = lane.Cum[k - 1] + step;
                previous = p;

                Vector3 direction = DirectionAtQ(lane, q);
                float turn = Vector3.Angle(lastDirection, direction) * Mathf.Deg2Rad;
                if (step > 0.01f) worst = Math.Max(worst, turn / step);
                lastDirection = direction;
            }

            lane.Length = Math.Max(0.05f, lane.Cum[16]);
            lane.Start = PointAtQ(lane, 0f);
            lane.End = PointAtQ(lane, 1f);
            lane.StartDirection = DirectionAtQ(lane, 0f);
            lane.EndDirection = DirectionAtQ(lane, 1f);
            lane.CurveLimit = worst > 1e-4f ? Mathf.Sqrt(LateralAcceleration / worst) : 60f;

            Vector3 middle = PointAtQ(lane, 0.5f);
            ToGame(middle, out float gx, out float gz);
            string cityCountry = CountryAt(sector, gx, gz, out bool urban);
            lane.Urban = urban;

            // The country: the map nodes' own id (RoadCountry); else the nearest city item in this and the eight
            // neighbouring sectors (a city's def names its country); else the nearest country_data pos.
            EtsTrafficCatalog.Country country = countryId > 0 ? _catalog.CountryById(countryId) : null;
            if (country == null && cityCountry != null) country = _catalog.CountryNamed(cityCountry);
            if (country == null) country = _catalog.NearestCountry(gx, gz);
            lane.Country = country != null ? country.Id : -1;

            lane.SpeedLimit = _catalog.SpeedLimit(country, speedClass, urban, EtsTrafficCatalog.ClassCar);
            lane.SpeedTruck = _catalog.SpeedLimit(country, speedClass, urban, EtsTrafficCatalog.ClassTruck);
            lane.SpeedBus = _catalog.SpeedLimit(country, speedClass, urban, EtsTrafficCatalog.ClassBus);

            // Inside a junction: at most the urban limit (not from the game; the game's own
            // junction behaviour is not in the files read here).
            if (lane.InPrefab)
            {
                lane.SpeedLimit = Math.Min(lane.SpeedLimit, 50f / 3.6f);
                lane.SpeedTruck = Math.Min(lane.SpeedTruck, 50f / 3.6f);
                lane.SpeedBus = Math.Min(lane.SpeedBus, 50f / 3.6f);
            }
        }

        /// <summary>The limit for a vehicle speed class (EtsTrafficCatalog.ClassCar/Truck/Bus) on a lane, m/s.</summary>
        public static float LimitFor(Lane lane, int speedClass)
        {
            return speedClass == EtsTrafficCatalog.ClassTruck ? lane.SpeedTruck : speedClass == EtsTrafficCatalog.ClassBus ? lane.SpeedBus : lane.SpeedLimit;
        }

        /// <summary>
        /// The country of the nearest city item in the lane's sector and its eight neighbours (null when none), and whether
        /// the point is inside a city area (EtsMapCity: node +- width / 2 in x, height / 2 in z). Which of the game's rules
        /// decides 'urban' is NOT DETERMINED; this is the design's stand-in. Only the fallback for the country now: the map
        /// nodes carry it (RoadCountry). Before 2026-10-14 this looked in the lane's own sector alone and fell back to
        /// Poland, so Dover's motorway lanes came out Polish.
        /// </summary>
        private string CountryAt(EtsMapSector sector, float gx, float gz, out bool urban)
        {
            urban = false;
            string country = null;
            float best = float.MaxValue;

            for (int dx0 = -1; dx0 <= 1; dx0++)
            {
                for (int dz0 = -1; dz0 <= 1; dz0++)
                {
                    EtsMapSector s = dx0 == 0 && dz0 == 0 ? sector : (Map.IsRead(sector.SectorX + dx0, sector.SectorZ + dz0) ? Map.GetSector(sector.SectorX + dx0, sector.SectorZ + dz0) : null);
                    if (s == null) continue;

                    foreach (EtsMapCity city in s.Cities)
                    {
                        EtsMapNode node = Map.FindNode(city.NodeUid);
                        if (node == null) continue;
                        float dx = gx - node.Position[0], dz = gz - node.Position[2];
                        if (Math.Abs(dx) <= city.Width * 0.5f && Math.Abs(dz) <= city.Height * 0.5f) urban = true;
                        float d = dx * dx + dz * dz;

                        if (d < best)
                        {
                            EtsSiiUnit def = Map.City(city.CityToken);
                            string named = def?.GetString("country");

                            if (!string.IsNullOrEmpty(named))
                            {
                                best = d;
                                country = named;
                            }
                        }
                    }
                }
            }

            return country;
        }

        // ------------------------------------------------------------------ joins and indices

        private static long Cell(Vector3 p, float size)
        {
            return ((long)Mathf.FloorToInt(p.x / size) << 32) ^ (uint)Mathf.FloorToInt(p.z / size);
        }

        private static void Put(Dictionary<long, List<int>> hash, long cell, int id)
        {
            if (!hash.TryGetValue(cell, out List<int> list))
            {
                list = new List<int>(2);
                hash[cell] = list;
            }

            if (!list.Contains(id)) list.Add(id);
        }

        /// <summary>The 16 m cells a lane's samples (every 8 m) fall in, in the order Index used to put them.</summary>
        private List<long> SampleCells(Lane lane)
        {
            int steps = Math.Max(1, Mathf.CeilToInt(lane.Length / 8f));
            var cells = new List<long>(steps + 1);

            for (int i = 0; i <= steps; i++)
            {
                cells.Add(Cell(PointAt(lane, lane.Length * i / steps), SampleCellSize));
            }

            return cells;
        }

        private void Unindex(Lane lane)
        {
            if (_starts.TryGetValue(Cell(lane.Start, 4f), out List<int> list)) list.Remove(lane.Id);

            foreach (long cell in SampleCells(lane))
            {
                if (_samples.TryGetValue(cell, out List<int> samples)) samples.Remove(lane.Id);
            }
        }

        private void Join(Lane lane)
        {
            if (lane.Next.Length > 0 && lane.InPrefab && lane.Nav.OutputNodeOf[lane.Curve] < 0)
            {
                return;
            }

            var found = new List<int>(lane.Next);
            int cx = Mathf.FloorToInt(lane.End.x / 4f), cz = Mathf.FloorToInt(lane.End.z / 4f);

            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dz = -1; dz <= 1; dz++)
                {
                    if (!_starts.TryGetValue(((long)(cx + dx) << 32) ^ (uint)(cz + dz), out List<int> starts)) continue;

                    foreach (int id in starts)
                    {
                        Lane other = Lanes[id];

                        if (other == null || other.Id == lane.Id || found.Contains(id)) continue;

                        // Apart sideways by at most JoinDistance, and in height by at most JoinHeight:
                        // on slopes a prefab's far nodes stood 0.78-2.27 m off the road ends (the
                        // 3D test made 258 false dead ends within 7 km of the start). BuildPrefab now
                        // lifts the curves onto the nodes; this keeps a residual miss from cutting
                        // the way, and 2.5 m is far below any over- or underpass clearance.
                        Vector3 gap = other.Start - lane.End;
                        if (gap.x * gap.x + gap.z * gap.z > JoinDistance * JoinDistance || Math.Abs(gap.y) > JoinHeight) continue;
                        if (Vector3.Dot(other.StartDirection, lane.EndDirection) < 0.7f) continue;

                        // A prefab's own curves are joined by its links, not by position.
                        if (other.InPrefab && lane.InPrefab && other.Item == lane.Item) continue;

                        found.Add(id);
                        other.Previous.Add(lane.Id);
                        Joins++;
                    }
                }
            }

            lane.Next = found.Count == 0 ? None : found.ToArray();

            if (lane.Next.Length == 0) _open.Add(lane.Id);
            else _open.Remove(lane.Id);
        }

        public int OpenEnds => _open.Count;

        private readonly Dictionary<int, bool> _leadsOn = new Dictionary<int, bool>();
        private readonly HashSet<int> _visiting = new HashSet<int>();
        private int _leadsOnVersion = -1;

        /// <summary>
        /// Whether driving on from a lane can go somewhere: false when every way on ends at an open
        /// end inside the built area within twelve lanes (a yard exit with no road, a road whose
        /// lanes are for trucks only). An end in a sector not built yet counts as going on. Round
        /// the start 25 open ends lie within 1.7 km (5 road lanes, 20 prefab curves) and, before
        /// routes avoided them, cars queued behind cars standing at them (harness TrafficTest).
        /// Remembered until the published graph changes (Version).
        /// </summary>
        public bool LeadsOn(int id)
        {
            if (_leadsOnVersion != Version)
            {
                _leadsOn.Clear();
                _leadsOnVersion = Version;
            }

            _visiting.Clear();
            return LeadsOn(id, 0);
        }

        private bool LeadsOn(int id, int depth)
        {
            if (_leadsOn.TryGetValue(id, out bool known)) return known;
            Lane lane = Get(id);
            if (lane == null) return false;
            if (depth > 12 || !_visiting.Add(id)) return true;
            bool result;

            if (lane.Next.Length == 0)
            {
                result = !IsBuilt(lane.End + lane.EndDirection * 2f);
            }
            else
            {
                result = false;

                foreach (int next in lane.Next)
                {
                    if (LeadsOn(next, depth + 1)) { result = true; break; }
                }
            }

            _visiting.Remove(id);
            if (depth == 0 || !result) _leadsOn[id] = result;
            return result;
        }

        private readonly HashSet<int> _seen = new HashSet<int>();

        /// <summary>Lane ids with a sample within a radius of a point (cheap, approximate: 16 m cells).</summary>
        public void LanesNear(Vector3 p, float radius, List<int> into)
        {
            into.Clear();
            int r = Mathf.CeilToInt(radius / SampleCellSize);
            int cx = Mathf.FloorToInt(p.x / SampleCellSize), cz = Mathf.FloorToInt(p.z / SampleCellSize);
            _seen.Clear();

            for (int dx = -r; dx <= r; dx++)
            {
                for (int dz = -r; dz <= r; dz++)
                {
                    if (_samples.TryGetValue(((long)(cx + dx) << 32) ^ (uint)(cz + dz), out List<int> list))
                    {
                        foreach (int id in list) if (_seen.Add(id)) into.Add(id);
                    }
                }
            }
        }

        /// <summary>The lane ids sampled in one 16 m cell, or null: for a caller that walks a large area a few cells at a time.</summary>
        public List<int> SampleCell(int cx, int cz)
        {
            return _samples.TryGetValue(((long)cx << 32) ^ (uint)cz, out List<int> list) ? list : null;
        }

        /// <summary>
        /// The lane a pose lies on: within 3 m sideways and heading within 60 degrees of the
        /// direction given (zero = any). How a new master picks up the cars it inherits from the
        /// wire, which carries poses, not lane ids.
        /// </summary>
        public bool Match(Vector3 position, Vector3 heading, out int laneId, out float along)
        {
            return Match(position, heading, 3f, 0.5f, out laneId, out along);
        }

        private readonly List<int> _matchNear = new List<int>();

        /// <summary>
        /// Match with its tolerances given: within maxDistance of the lane's line (horizontally and in height alike, as
        /// Project measures) and a heading whose cosine with the lane's direction is over minDot. How a wrecked car that
        /// has come to rest finds out whether it still stands on a lane it can drive on from (EtsTrafficSim.Recover).
        /// Main thread only (a shared list).
        /// </summary>
        public bool Match(Vector3 position, Vector3 heading, float maxDistance, float minDot, out int laneId, out float along)
        {
            laneId = -1;
            along = 0f;
            float best = maxDistance;
            List<int> candidates = _matchNear;
            LanesNear(position, 16f, candidates);
            Vector3 wanted = heading.sqrMagnitude < 1e-4f ? Vector3.zero : heading.normalized;

            foreach (int id in candidates)
            {
                Lane lane = Lanes[id];
                if (lane == null) continue;

                float s = Project(lane, position, out float distance);

                if (distance < best && (wanted.sqrMagnitude < 1e-4f || Vector3.Dot(DirectionAt(lane, s), wanted) > minDot))
                {
                    best = distance;
                    laneId = id;
                    along = s;
                }
            }

            return laneId >= 0;
        }

        /// <summary>The distance along a lane nearest a point, and how far the point is from it.</summary>
        public float Project(Lane lane, Vector3 point, out float distance)
        {
            float bestS = 0f;
            distance = float.MaxValue;
            Vector3 a = PointAtQ(lane, 0f);

            for (int k = 1; k <= 16; k++)
            {
                Vector3 b = PointAtQ(lane, k / 16f);
                Vector3 ab = b - a;
                float t = ab.sqrMagnitude > 1e-6f ? Mathf.Clamp01(Vector3.Dot(point - a, ab) / ab.sqrMagnitude) : 0f;
                Vector3 onSegment = a + ab * t;
                float d = (point - onSegment).magnitude;

                if (d < distance)
                {
                    distance = d;
                    bestS = Mathf.Lerp(lane.Cum[k - 1], lane.Cum[k], t);
                }

                a = b;
            }

            return bestS;
        }

        // ------------------------------------------------------------------ evaluation

        public Vector3 PointAt(Lane lane, float s)
        {
            return PointAtQ(lane, QOf(lane, s));
        }

        public Vector3 DirectionAt(Lane lane, float s)
        {
            return DirectionAtQ(lane, QOf(lane, s));
        }

        private static float QOf(Lane lane, float s)
        {
            if (lane.Cum == null || s <= 0f) return 0f;
            if (s >= lane.Cum[16]) return 1f;
            int k = 1;
            while (k < 16 && lane.Cum[k] < s) k++;
            float span = lane.Cum[k] - lane.Cum[k - 1];
            float t = span > 1e-5f ? (s - lane.Cum[k - 1]) / span : 0f;
            return (k - 1 + t) / 16f;
        }

        private static Vector3 PointAtQ(Lane lane, float q)
        {
            float u = lane.Reverse ? 1f - q : q;
            Vector3 centre = Hermite(lane.P0, lane.T0, lane.P1, lane.T1, u);

            if (Math.Abs(lane.Offset) < 1e-4f)
            {
                return centre;
            }

            Vector3 tangent = HermiteTangent(lane.P0, lane.T0, lane.P1, lane.T1, u);
            Vector3 right = new Vector3(tangent.z, 0f, -tangent.x);
            float length = right.magnitude;
            right = length > 1e-5f ? right / length : Vector3.zero;
            return centre + right * (lane.Reverse ? -lane.Offset : lane.Offset);
        }

        private static Vector3 DirectionAtQ(Lane lane, float q)
        {
            float u = lane.Reverse ? 1f - q : q;
            Vector3 tangent = HermiteTangent(lane.P0, lane.T0, lane.P1, lane.T1, u);

            if (lane.Reverse)
            {
                tangent = -tangent;
            }

            float length = tangent.magnitude;
            return length > 1e-5f ? tangent / length : Vector3.forward;
        }

        public static Vector3 Hermite(Vector3 p0, Vector3 t0, Vector3 p1, Vector3 t1, float u)
        {
            float u2 = u * u, u3 = u2 * u;
            return p0 * (2f * u3 - 3f * u2 + 1f) + t0 * (u3 - 2f * u2 + u) + p1 * (-2f * u3 + 3f * u2) + t1 * (u3 - u2);
        }

        public static Vector3 HermiteTangent(Vector3 p0, Vector3 t0, Vector3 p1, Vector3 t1, float u)
        {
            float u2 = u * u;
            return p0 * (6f * u2 - 6f * u) + t0 * (3f * u2 - 4f * u + 1f) + p1 * (-6f * u2 + 6f * u) + t1 * (3f * u2 - 2f * u);
        }
    }
}
