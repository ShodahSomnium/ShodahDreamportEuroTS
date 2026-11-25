using System;
using System.Collections.Generic;

namespace SomniumSpace.Worlds.SomniumEuroTruck.Ets
{
    /// <summary>
    /// The road network as a graph over the map's nodes, for the route advisor. A road joins its
    /// two nodes with its stored length the way its lanes run - lanes_right start -> end,
    /// lanes_left end -> start, the rule EtsLaneGraph measured and drives its cars by - and both
    /// ways when its look has no lanes at all (the "narrow way no lanes" tracks) or is unknown.
    /// Every prefab joins all of its nodes to each other by straight distance (a junction lets you
    /// go from any road to any other, which is true often enough for guidance). Shortest routes
    /// come from Dijkstra with a binary heap. Sectors are indexed as they are read, so the graph
    /// grows with the map.
    ///
    /// Why directed (review 2026-09-22, MAPGEO-1): with every road both ways, both logged jobs
    /// (TransiNet and ITCC Szczecin -> Sanbuilders Berlin) ran 15 one-way motorway roads end ->
    /// start, 1.58 km of 8.57, down the oncoming carriageway - across the median barriers and
    /// into the AI cars driving it. Directed, both take the right carriageway (9.11 and 8.93 km).
    /// One-way roads leave nodes with edges in but none out (975 on the whole demo map) or out but
    /// none in (969); NearestNode only answers nodes with both, so a route can start and end on the
    /// node it is given. Probe 1770 (whole map, all 282 sectors, as EtsJobs reads it): of the
    /// 85,860 inter-city depot pairs the undirected graph connected, none is lost; 6 get more than
    /// 1.5x longer (worst 1.65, 4.1 -> 6.8 km, a motorway exit that must be taken the right way).
    ///
    /// Phase 2 (G2): once the whole map is indexed, <see cref="Freeze"/> copies the graph into arrays (nodes by a
    /// dense index, edges in the lists' own order) and puts the nodes NearestNode may answer into a 500 m grid. Why:
    /// the offers made at the 72 HQ yards (probe 2110, .NET) spent a median 571 ms on three FindPath calls and
    /// a few ms per linear NearestNode scan, in one frame - at a spawn or a delivery, in VR. On the frozen form
    /// NearestNode gives exactly the scan's answer (same float distance, the first node in scan order on a tie;
    /// probe 2113) from a few cells, and <see cref="Search"/> - one Dijkstra from the depot to every destination
    /// at once, resumable, so EtsJobs spreads it over frames - settles nodes in exactly FindPath's order and so
    /// returns FindPath's own path and length for every node it reaches (probe 2113), with no allocation once
    /// its arrays exist. Nothing adds edges after the index (IndexLoadedSectors is called once, by
    /// EtsMapIndex.Reader.Finish), so the frozen copy cannot go stale; IndexLoadedSectors refuses once freezing has begun.
    /// </summary>
    public sealed class EtsRoadGraph
    {
        public struct Edge
        {
            public ulong To;
            public float Length;
            public EtsMapRoad Road;
            public EtsMapPrefab Prefab;
        }

        /// <summary>One node of a route and how it was reached from the previous one.</summary>
        public sealed class Step
        {
            public ulong NodeUid;
            public EtsMapRoad Road;
            public bool RoadReversed;
            public EtsMapPrefab Prefab;
        }

        private readonly EtsMap _map;
        private readonly Dictionary<ulong, List<Edge>> _edges = new Dictionary<ulong, List<Edge>>();

        // Nodes some edge leads into. _edges only holds nodes with a way out.
        private readonly HashSet<ulong> _entered = new HashSet<ulong>();
        private readonly Dictionary<ulong, int> _lookWays = new Dictionary<ulong, int>();
        private readonly HashSet<EtsMapSector> _indexed = new HashSet<EtsMapSector>();

        public EtsRoadGraph(EtsMap map)
        {
            _map = map;
        }

        public int NodeCount => _edges.Count;

        /// <summary>Indexes every sector read since the last call. Nothing once freezing has begun (its copy would go stale).</summary>
        public int IndexLoadedSectors()
        {
            if (_frozen || _freezeStage > 0)
            {
                return 0;
            }

            int added = 0;

            foreach (EtsMapSector sector in _map.LoadedSectors)
            {
                if (_indexed.Add(sector))
                {
                    Index(sector);
                    added++;
                }
            }

            return added;
        }

        /// <summary>
        /// IndexLoadedSectors a sector at a time: indexes sectors in the same order until about budgetMs has gone (at least
        /// one sector a call), true once every loaded sector is indexed. The same sectors in the same order give the same
        /// edge lists as one IndexLoadedSectors (checker probe 2121 compares them edge for edge). Why: EtsMapIndex indexed
        /// the whole map in the frame the read ended - 48-136 ms on .NET (probe 2113 T1), one frozen frame while a VR
        /// player looks at the HQ map; a sector alone is a few ms (the largest, sec+0002-0003, measured in probe 2121).
        /// </summary>
        public bool IndexStep(double budgetMs)
        {
            if (_frozen || _freezeStage > 0)
            {
                return true;
            }

            var clock = System.Diagnostics.Stopwatch.StartNew();

            foreach (EtsMapSector sector in _map.LoadedSectors)
            {
                if (_indexed.Add(sector))
                {
                    Index(sector);

                    if (clock.Elapsed.TotalMilliseconds > budgetMs)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private void Index(EtsMapSector sector)
        {
            foreach (EtsMapRoad road in sector.Roads)
            {
                if (road.StartNode == 0 || road.EndNode == 0 || road.StartNode == road.EndNode) continue;
                float length = road.Length > 0.5f ? road.Length : 1f;
                int ways = Ways(road.LookToken);
                if ((ways & 1) != 0) AddEdge(road.StartNode, road.EndNode, length, road, null);
                if ((ways & 2) != 0) AddEdge(road.EndNode, road.StartNode, length, road, null);
            }

            foreach (EtsMapPrefab prefab in sector.Prefabs)
            {
                int n = prefab.NodeUids.Length;
                if (n < 2) continue;

                for (int i = 0; i < n; i++)
                {
                    EtsMapNode a = _map.FindNode(prefab.NodeUids[i]);
                    if (a == null) continue;

                    for (int j = 0; j < n; j++)
                    {
                        if (i == j) continue;
                        EtsMapNode b = _map.FindNode(prefab.NodeUids[j]);
                        if (b == null) continue;
                        float dx = a.Position[0] - b.Position[0], dz = a.Position[2] - b.Position[2];
                        AddEdge(a.Uid, b.Uid, Math.Max(1f, (float)Math.Sqrt(dx * dx + dz * dz)), null, prefab);
                    }
                }
            }
        }

        /// <summary>
        /// Which ways a road look is driven: 1 start -> end (it has lanes_right), 2 end -> start
        /// (lanes_left), 3 both - also for a look with no lanes at all or one not found. Lanes of
        /// every type count, as in the verified fix. Worked out once per look: the whole map's
        /// 64,589 roads use 226 looks, and each lookup decodes a token and copies the lane lists.
        /// Indexing the whole map costs 91 ms against 83 before (probe 1774, .NET; 33 against 27 ms
        /// on the editor's Mono for the 99 sectors round the start); its edges match the verified
        /// rule one for one.
        /// </summary>
        private int Ways(ulong lookToken)
        {
            if (_lookWays.TryGetValue(lookToken, out int ways))
            {
                return ways;
            }

            EtsSiiUnit look = _map.RoadLook(lookToken);
            int right = look != null ? look.GetArray("lanes_right").Count : 0;
            int left = look != null ? look.GetArray("lanes_left").Count : 0;
            ways = right == 0 && left == 0 ? 3 : (right > 0 ? 1 : 0) | (left > 0 ? 2 : 0);
            _lookWays[lookToken] = ways;
            return ways;
        }

        private void AddEdge(ulong from, ulong to, float length, EtsMapRoad road, EtsMapPrefab prefab)
        {
            if (!_edges.TryGetValue(from, out List<Edge> list))
            {
                list = new List<Edge>(2);
                _edges[from] = list;
            }

            list.Add(new Edge { To = to, Length = length, Road = road, Prefab = prefab });
            _entered.Add(to);
        }

        public bool Contains(ulong uid)
        {
            return _edges.ContainsKey(uid);
        }

        /// <summary>
        /// The road node nearest a game position, among the nodes the graph knows that a route
        /// can both leave and reach - not the far end of a one-way road that meets nothing going
        /// on, nor the start of one that nothing leads into.
        /// </summary>
        public EtsMapNode NearestNode(float x, float z, out float distance)
        {
            if (_frozen)
            {
                return GridNearest(x, z, null, out distance);
            }

            return ScanNearest(x, z, out distance);
        }

        /// <summary>
        /// NearestNode as a walk over every node of every loaded sector - the rule itself, and what the grid of the
        /// frozen graph is checked against (probe 2113). A few ms on the whole map; use NearestNode.
        /// </summary>
        public EtsMapNode ScanNearest(float x, float z, out float distance)
        {
            EtsMapNode best = null;
            float bestSquared = float.MaxValue;

            foreach (EtsMapSector sector in _map.LoadedSectors)
            {
                foreach (EtsMapNode node in sector.Nodes)
                {
                    if (!_edges.ContainsKey(node.Uid) || !_entered.Contains(node.Uid)) continue;
                    float dx = node.Position[0] - x, dz = node.Position[2] - z;
                    float d = dx * dx + dz * dz;

                    if (d < bestSquared)
                    {
                        bestSquared = d;
                        best = node;
                    }
                }
            }

            distance = best == null ? float.MaxValue : (float)Math.Sqrt(bestSquared);
            return best;
        }

        /// <summary>Shortest route by road length. Null when the two nodes are not connected in the indexed map.</summary>
        public List<Step> FindPath(ulong from, ulong to, out float length)
        {
            length = 0f;

            if (!_edges.ContainsKey(from) || !_edges.ContainsKey(to))
            {
                return null;
            }

            var distance = new Dictionary<ulong, float> { [from] = 0f };
            var previous = new Dictionary<ulong, KeyValuePair<ulong, Edge>>();
            var done = new HashSet<ulong>();
            var heap = new MinHeap();
            heap.Push(0f, from);

            while (heap.Count > 0)
            {
                heap.Pop(out float d, out ulong uid);

                if (!done.Add(uid)) continue;
                if (uid == to) break;
                if (!_edges.TryGetValue(uid, out List<Edge> edges)) continue;

                foreach (Edge edge in edges)
                {
                    if (done.Contains(edge.To)) continue;
                    float candidate = d + edge.Length;

                    if (!distance.TryGetValue(edge.To, out float known) || candidate < known)
                    {
                        distance[edge.To] = candidate;
                        previous[edge.To] = new KeyValuePair<ulong, Edge>(uid, edge);
                        heap.Push(candidate, edge.To);
                    }
                }
            }

            if (!distance.TryGetValue(to, out length) || !done.Contains(to))
            {
                return null;
            }

            var path = new List<Step>();
            ulong current = to;

            while (true)
            {
                if (!previous.TryGetValue(current, out KeyValuePair<ulong, Edge> via))
                {
                    path.Add(new Step { NodeUid = current });
                    break;
                }

                path.Add(new Step
                {
                    NodeUid = current,
                    Road = via.Value.Road,
                    RoadReversed = via.Value.Road != null && via.Value.Road.EndNode == via.Key,
                    Prefab = via.Value.Prefab
                });
                current = via.Key;
            }

            path.Reverse();
            return path;
        }

        // ------------------------------------------------------------------ the frozen form (Phase 2, G2)

        /// <summary>NearestNode's grid cell, metres (game units). A depot's bay is within a few hundred metres of a node.</summary>
        private const float GridCell = 500f;

        private bool _frozen;

        // Nodes by a dense index: every node with an edge out (in _edges' order), then those only entered.
        private ulong[] _uidOf;
        private Dictionary<ulong, int> _denseOf;

        // Edges as arrays (CSR): node i's edges are [_firstEdge[i], _firstEdge[i + 1]), in its list's order.
        private int[] _firstEdge;
        private int[] _edgeFrom;
        private int[] _edgeTo;
        private float[] _edgeLength;
        private Edge[] _edgeOf;

        // NearestNode's candidates in the scan's own order (a node kept in two sectors is two entries, as the scan sees
        // it twice), and a grid over them: cell c holds _cellItems[_cellFirst[c] .. _cellFirst[c + 1]), ascending.
        private EtsMapNode[] _nearNode;
        private float[] _nearX;
        private float[] _nearZ;
        private int[] _cellFirst;
        private int[] _cellItems;
        private float _gridX0;
        private float _gridZ0;
        private int _gridW;
        private int _gridH;

        /// <summary>Whether <see cref="Freeze"/> has run: NearestNode uses the grid, and a <see cref="Search"/> can be made.</summary>
        public bool Frozen => _frozen;

        /// <summary>Nodes in the frozen form (every node with an edge out or in); 0 before Freeze.</summary>
        public int DenseCount => _uidOf != null ? _uidOf.Length : 0;

        /// <summary>Entries in NearestNode's grid (nodes a route can both leave and reach, per sector they are kept in).</summary>
        public int GridEntries => _nearNode != null ? _nearNode.Length : 0;

        /// <summary>Freezes in one go (the offline probes; FreezeStep's slices run to the end). Idempotent.</summary>
        public void Freeze()
        {
            while (!FreezeStep(double.MaxValue))
            {
            }
        }

        // FreezeStep's progress: the stage, a cursor into it, and what the stages hand on.
        private int _freezeStage;
        private int _cursor;
        private int _edgeCursor;
        private ulong[] _keysOut;
        private List<Edge>[] _listsOut;
        private ulong[] _keysIn;
        private List<ulong> _denseList;
        private List<EtsMapSector> _sectorList;
        private List<EtsMapNode> _nearList;

        /// <summary>
        /// Copies the graph into arrays and builds NearestNode's grid, a slice at a time: at most about budgetMs of work
        /// per call (checked every few hundred items), true once frozen. The graph takes no more sectors from the first
        /// call on. EtsMapIndex runs it over frames after the whole map is indexed: done in one go it cost 114-133 ms on
        /// .NET (probe 2113, three runs), on top of that frame's own graph build (43-130 ms). Stages: the dense indices
        /// (nodes with a way out, in _edges' order, then those only entered), the edge arrays, NearestNode's candidates
        /// sector by sector in the scan's order, then the grid in one piece (a counting sort, a few ms).
        /// </summary>
        public bool FreezeStep(double budgetMs)
        {
            if (_frozen)
            {
                return true;
            }

            var clock = System.Diagnostics.Stopwatch.StartNew();

            while (true)
            {
                switch (_freezeStage)
                {
                    case 0:
                        // Snapshots of the finished graph (Keys and Values come in the same order).
                        _keysOut = new ulong[_edges.Count];
                        _edges.Keys.CopyTo(_keysOut, 0);
                        _listsOut = new List<Edge>[_edges.Count];
                        _edges.Values.CopyTo(_listsOut, 0);
                        _keysIn = new ulong[_entered.Count];
                        _entered.CopyTo(_keysIn);
                        _denseList = new List<ulong>(_keysOut.Length + _keysIn.Length / 16 + 16);
                        _denseOf = new Dictionary<ulong, int>(_keysOut.Length + _keysIn.Length / 16 + 16);
                        _cursor = 0;
                        _freezeStage = 1;
                        return false; // the copies are one piece of a few ms: the next slice starts afresh

                    case 1:
                        // Dense index = position: nodes with a way out first.
                        while (_cursor < _keysOut.Length)
                        {
                            ulong uid = _keysOut[_cursor++];
                            _denseOf.Add(uid, _denseList.Count);
                            _denseList.Add(uid);
                            if ((_cursor & 1023) == 0 && clock.Elapsed.TotalMilliseconds > budgetMs) return false;
                        }

                        _cursor = 0;
                        _freezeStage = 2;
                        break;

                    case 2:
                        // Then the nodes only entered (the far ends of one-way roads).
                        while (_cursor < _keysIn.Length)
                        {
                            ulong uid = _keysIn[_cursor++];

                            if (!_denseOf.ContainsKey(uid))
                            {
                                _denseOf.Add(uid, _denseList.Count);
                                _denseList.Add(uid);
                            }

                            if ((_cursor & 1023) == 0 && clock.Elapsed.TotalMilliseconds > budgetMs) return false;
                        }

                        _uidOf = _denseList.ToArray();
                        _denseList = null;
                        _keysIn = null;
                        int edgeCount = 0;

                        for (int i = 0; i < _listsOut.Length; i++)
                        {
                            edgeCount += _listsOut[i].Count;
                        }

                        _firstEdge = new int[_uidOf.Length + 1];
                        _edgeFrom = new int[edgeCount];
                        _edgeTo = new int[edgeCount];
                        _edgeLength = new float[edgeCount];
                        _edgeOf = new Edge[edgeCount];
                        _cursor = 0;
                        _edgeCursor = 0;
                        _freezeStage = 3;
                        return false;

                    case 3:
                        // The edge arrays, node by node, each node's edges in its list's order.
                        while (_cursor < _uidOf.Length)
                        {
                            int i = _cursor++;
                            _firstEdge[i] = _edgeCursor;

                            if (i < _listsOut.Length)
                            {
                                List<Edge> list = _listsOut[i];

                                for (int k = 0; k < list.Count; k++)
                                {
                                    Edge edge = list[k];
                                    int e = _edgeCursor++;
                                    _edgeFrom[e] = i;
                                    _edgeTo[e] = _denseOf[edge.To];
                                    _edgeLength[e] = edge.Length;
                                    _edgeOf[e] = edge;
                                }
                            }

                            if ((_cursor & 511) == 0 && clock.Elapsed.TotalMilliseconds > budgetMs) return false;
                        }

                        _firstEdge[_uidOf.Length] = _edgeCursor;
                        _listsOut = null;
                        _keysOut = null;
                        _sectorList = new List<EtsMapSector>(_map.LoadedSectors);
                        _nearList = new List<EtsMapNode>(_uidOf.Length);
                        _cursor = 0;
                        _freezeStage = 4;
                        break;

                    case 4:
                        // NearestNode's candidates in the scan's own order, a sector at a time.
                        while (_cursor < _sectorList.Count)
                        {
                            foreach (EtsMapNode node in _sectorList[_cursor++].Nodes)
                            {
                                if (_edges.ContainsKey(node.Uid) && _entered.Contains(node.Uid))
                                {
                                    _nearList.Add(node);
                                }
                            }

                            if (clock.Elapsed.TotalMilliseconds > budgetMs) return false;
                        }

                        _freezeStage = 5;
                        return false; // the grid is one piece (a counting sort): its own slice

                    case 5:
                        BuildGrid(_nearList);
                        _nearList = null;
                        _sectorList = null;
                        _freezeStage = 6;
                        _frozen = true;
                        return true;

                    default:
                        return _frozen;
                }
            }
        }

        private void BuildGrid(List<EtsMapNode> near)
        {
            int count = near.Count;
            _nearNode = near.ToArray();
            _nearX = new float[count];
            _nearZ = new float[count];
            float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;

            for (int k = 0; k < count; k++)
            {
                float x = _nearNode[k].Position[0], z = _nearNode[k].Position[2];
                _nearX[k] = x;
                _nearZ[k] = z;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (z < minZ) minZ = z;
                if (z > maxZ) maxZ = z;
            }

            if (count == 0)
            {
                _gridW = 0;
                _gridH = 0;
                _cellFirst = new int[1];
                _cellItems = Array.Empty<int>();
                return;
            }

            _gridX0 = minX;
            _gridZ0 = minZ;
            _gridW = CellOf(maxX, _gridX0) + 1;
            _gridH = CellOf(maxZ, _gridZ0) + 1;
            _cellFirst = new int[_gridW * _gridH + 1];
            var cellOfItem = new int[count];

            for (int k = 0; k < count; k++)
            {
                int c = CellOf(_nearZ[k], _gridZ0) * _gridW + CellOf(_nearX[k], _gridX0);
                cellOfItem[k] = c;
                _cellFirst[c + 1]++;
            }

            for (int c = 0; c < _gridW * _gridH; c++)
            {
                _cellFirst[c + 1] += _cellFirst[c];
            }

            _cellItems = new int[count];
            var fill = new int[_gridW * _gridH];

            // Ascending k within each cell: the scan's order, which a tie is settled by.
            for (int k = 0; k < count; k++)
            {
                int c = cellOfItem[k];
                _cellItems[_cellFirst[c] + fill[c]++] = k;
            }
        }

        private static int CellOf(float v, float origin)
        {
            return (int)Math.Floor(((double)v - origin) / GridCell);
        }

        /// <summary>NearestNode skipping some nodes: the next candidate when the nearest one leads nowhere (EtsJobs' retry).</summary>
        public EtsMapNode NearestNodeExcept(float x, float z, HashSet<ulong> except, out float distance)
        {
            if (_frozen)
            {
                return GridNearest(x, z, except, out distance);
            }

            EtsMapNode best = null;
            float bestSquared = float.MaxValue;

            foreach (EtsMapSector sector in _map.LoadedSectors)
            {
                foreach (EtsMapNode node in sector.Nodes)
                {
                    if (!_edges.ContainsKey(node.Uid) || !_entered.Contains(node.Uid) || (except != null && except.Contains(node.Uid))) continue;
                    float dx = node.Position[0] - x, dz = node.Position[2] - z;
                    float d = dx * dx + dz * dz;

                    if (d < bestSquared)
                    {
                        bestSquared = d;
                        best = node;
                    }
                }
            }

            distance = best == null ? float.MaxValue : (float)Math.Sqrt(bestSquared);
            return best;
        }

        /// <summary>
        /// The scan's answer from the grid. Cells are visited in rings round the point's own cell (ring r: the cells r
        /// steps away in x or z). A node in ring r lies more than (r - 1) cells from the point, so the walk stops once the
        /// best squared distance is below that (less one metre, against float rounding). The distance is the scan's
        /// own float expression, and a tie goes to the entry the scan meets first.
        /// </summary>
        private EtsMapNode GridNearest(float x, float z, HashSet<ulong> except, out float distance)
        {
            distance = float.MaxValue;

            if (_nearNode == null || _nearNode.Length == 0)
            {
                return null;
            }

            int cx = CellOf(x, _gridX0), cz = CellOf(z, _gridZ0);
            int best = -1;
            float bestSquared = float.MaxValue;

            // Rings wholly off the grid hold nothing; past rEnd no cell of the grid is left.
            int rStart = Math.Max(0, Math.Max(Math.Max(-cx, cx - (_gridW - 1)), Math.Max(-cz, cz - (_gridH - 1))));
            int rEnd = Math.Max(Math.Max(cx, _gridW - 1 - cx), Math.Max(cz, _gridH - 1 - cz));

            for (int r = rStart; r <= rEnd; r++)
            {
                if (best >= 0 && r >= 2)
                {
                    double reach = (r - 1) * (double)GridCell - 1.0;

                    if (bestSquared < reach * reach)
                    {
                        break;
                    }
                }

                if (r == 0)
                {
                    VisitCell(cx, cz, x, z, except, ref best, ref bestSquared);
                    continue;
                }

                // Only the ring's cells on the grid (VisitCell ignores the others anyway): a point far off the map used to
                // walk 8r empty cells a ring - 311 ms at 100-500 km out (probe 2113 T2), 620 ms at 30,000 km (checker 2120).
                // The answer does not depend on the visiting order (a tie goes to the lower entry, whichever cell comes first).
                int i0 = Math.Max(cx - r, 0), i1 = Math.Min(cx + r, _gridW - 1);
                int j0 = Math.Max(cz - r + 1, 0), j1 = Math.Min(cz + r - 1, _gridH - 1);

                if (cz - r >= 0 && cz - r < _gridH)
                {
                    for (int i = i0; i <= i1; i++) VisitCell(i, cz - r, x, z, except, ref best, ref bestSquared);
                }

                if (cz + r >= 0 && cz + r < _gridH)
                {
                    for (int i = i0; i <= i1; i++) VisitCell(i, cz + r, x, z, except, ref best, ref bestSquared);
                }

                if (cx - r >= 0 && cx - r < _gridW)
                {
                    for (int j = j0; j <= j1; j++) VisitCell(cx - r, j, x, z, except, ref best, ref bestSquared);
                }

                if (cx + r >= 0 && cx + r < _gridW)
                {
                    for (int j = j0; j <= j1; j++) VisitCell(cx + r, j, x, z, except, ref best, ref bestSquared);
                }
            }

            if (best < 0)
            {
                return null;
            }

            distance = (float)Math.Sqrt(bestSquared);
            return _nearNode[best];
        }

        private void VisitCell(int i, int j, float x, float z, HashSet<ulong> except, ref int best, ref float bestSquared)
        {
            if (i < 0 || j < 0 || i >= _gridW || j >= _gridH)
            {
                return;
            }

            int c = j * _gridW + i;

            for (int s = _cellFirst[c]; s < _cellFirst[c + 1]; s++)
            {
                int k = _cellItems[s];
                if (except != null && except.Contains(_nearNode[k].Uid)) continue;
                float dx = _nearX[k] - x, dz = _nearZ[k] - z;
                float d = dx * dx + dz * dz;

                if (d < bestSquared || (d == bestSquared && k < best))
                {
                    bestSquared = d;
                    best = k;
                }
            }
        }

        /// <summary>
        /// Shortest routes from one node to many, on the frozen graph, a slice at a time: Begin(from), Target(uid) for each
        /// destination, then Run(n) (n settled nodes at most) until it returns true - every target settled, or nothing
        /// more can be reached. It is FindPath's Dijkstra with arrays for dictionaries: the same edges in the same order,
        /// the same float sums, the same strict-less update and the same binary heap, so nodes are settled in the same
        /// order and a settled node's route is fixed when it is settled - PathTo(t) is FindPath(from, t), step for step,
        /// with the same length (probe 2113). One Search is reused: its arrays are the graph's size and stamped per run,
        /// so a run allocates nothing but its heap's growth and the paths asked for.
        /// </summary>
        public sealed class Search
        {
            private readonly EtsRoadGraph _graph;
            private readonly float[] _dist;
            private readonly int[] _viaEdge;
            private readonly int[] _seen;
            private readonly int[] _settled;
            private readonly int[] _wanted;
            private int _stamp;
            private int _start = -1;
            private int _targetsLeft;
            private float[] _keys = new float[4096];
            private int[] _values = new int[4096];
            private int _count;

            /// <summary>Nodes settled in this run.</summary>
            public int Settled { get; private set; }

            /// <summary>The run is over: every target settled, or the reachable part of the graph used up.</summary>
            public bool Finished { get; private set; } = true;

            /// <summary>The start node, or 0 when Begin refused it.</summary>
            public ulong From { get; private set; }

            public Search(EtsRoadGraph graph)
            {
                if (graph == null || !graph._frozen)
                {
                    throw new InvalidOperationException("EtsRoadGraph.Search needs a frozen graph (Freeze).");
                }

                _graph = graph;
                int n = graph._uidOf.Length;
                _dist = new float[n];
                _viaEdge = new int[n];
                _seen = new int[n];
                _settled = new int[n];
                _wanted = new int[n];
            }

            /// <summary>Starts a run from a node. False (and Finished) for a node a route cannot leave - FindPath refuses those too.</summary>
            public bool Begin(ulong from)
            {
                _stamp++;
                _count = 0;
                _targetsLeft = 0;
                _start = -1;
                Settled = 0;
                From = 0;
                Finished = true;

                if (!_graph._edges.ContainsKey(from) || !_graph._denseOf.TryGetValue(from, out int s))
                {
                    return false;
                }

                _start = s;
                From = from;
                _seen[s] = _stamp;
                _dist[s] = 0f;
                _viaEdge[s] = -1;
                Push(0f, s);
                Finished = false;
                return true;
            }

            /// <summary>
            /// Adds a destination before the first Run. False for one a route cannot be found to (not a node with a way
            /// out, as FindPath requires). The run stops when the last target is settled.
            /// </summary>
            public bool Target(ulong uid)
            {
                if (_start < 0 || !_graph._edges.ContainsKey(uid) || !_graph._denseOf.TryGetValue(uid, out int t))
                {
                    return false;
                }

                if (_wanted[t] != _stamp && _settled[t] != _stamp)
                {
                    _wanted[t] = _stamp;
                    _targetsLeft++;
                }

                return true;
            }

            /// <summary>Settles up to maxSettles nodes. True once Finished.</summary>
            public bool Run(int maxSettles)
            {
                if (Finished)
                {
                    return true;
                }

                if (_targetsLeft == 0)
                {
                    Finished = true;
                    return true;
                }

                int[] first = _graph._firstEdge, to = _graph._edgeTo;
                float[] length = _graph._edgeLength;

                while (_count > 0 && maxSettles > 0)
                {
                    Pop(out float d, out int u);

                    if (_settled[u] == _stamp)
                    {
                        continue;
                    }

                    _settled[u] = _stamp;
                    Settled++;
                    maxSettles--;

                    if (_wanted[u] == _stamp && --_targetsLeft == 0)
                    {
                        Finished = true;
                        return true;
                    }

                    for (int e = first[u], end = first[u + 1]; e < end; e++)
                    {
                        int v = to[e];

                        if (_settled[v] == _stamp)
                        {
                            continue;
                        }

                        float candidate = d + length[e];

                        if (_seen[v] != _stamp || candidate < _dist[v])
                        {
                            _seen[v] = _stamp;
                            _dist[v] = candidate;
                            _viaEdge[v] = e;
                            Push(candidate, v);
                        }
                    }
                }

                if (_count == 0)
                {
                    Finished = true;
                }

                return Finished;
            }

            /// <summary>Whether a node was settled in this run (reached, with its final route), and its route length.</summary>
            public bool Reached(ulong uid, out float routeLength)
            {
                routeLength = 0f;

                if (_start < 0 || !_graph._denseOf.TryGetValue(uid, out int t) || _settled[t] != _stamp)
                {
                    return false;
                }

                routeLength = _dist[t];
                return true;
            }

            /// <summary>The route to a settled node, as FindPath builds it (from the start, each step with how it was reached); null otherwise.</summary>
            public List<Step> PathTo(ulong uid, out float routeLength)
            {
                if (!Reached(uid, out routeLength))
                {
                    return null;
                }

                var path = new List<Step>();
                int current = _graph._denseOf[uid];

                while (true)
                {
                    int e = _viaEdge[current];

                    if (e < 0)
                    {
                        path.Add(new Step { NodeUid = _graph._uidOf[current] });
                        break;
                    }

                    Edge edge = _graph._edgeOf[e];
                    int previous = _graph._edgeFrom[e];
                    path.Add(new Step
                    {
                        NodeUid = _graph._uidOf[current],
                        Road = edge.Road,
                        RoadReversed = edge.Road != null && edge.Road.EndNode == _graph._uidOf[previous],
                        Prefab = edge.Prefab
                    });
                    current = previous;
                }

                path.Reverse();
                return path;
            }

            /// <summary>Every node settled in this run (the part of the graph the start leads to, when the run ran dry).</summary>
            public void CollectSettled(HashSet<ulong> into)
            {
                for (int i = 0; i < _settled.Length; i++)
                {
                    if (_settled[i] == _stamp) into.Add(_graph._uidOf[i]);
                }
            }

            // MinHeap's push and pop, line for line, on arrays: the keys decide every swap, so the pops come in its order.
            private void Push(float key, int value)
            {
                if (_count == _keys.Length)
                {
                    Array.Resize(ref _keys, _count * 2);
                    Array.Resize(ref _values, _count * 2);
                }

                _keys[_count] = key;
                _values[_count] = value;
                int i = _count++;

                while (i > 0)
                {
                    int parent = (i - 1) / 2;
                    if (_keys[parent] <= _keys[i]) break;
                    Swap(i, parent);
                    i = parent;
                }
            }

            private void Pop(out float key, out int value)
            {
                key = _keys[0];
                value = _values[0];
                int last = _count - 1;
                _keys[0] = _keys[last];
                _values[0] = _values[last];
                _count = last;
                int i = 0;

                while (true)
                {
                    int left = i * 2 + 1, right = left + 1, smallest = i;
                    if (left < _count && _keys[left] < _keys[smallest]) smallest = left;
                    if (right < _count && _keys[right] < _keys[smallest]) smallest = right;
                    if (smallest == i) break;
                    Swap(i, smallest);
                    i = smallest;
                }
            }

            private void Swap(int a, int b)
            {
                float k = _keys[a]; _keys[a] = _keys[b]; _keys[b] = k;
                int v = _values[a]; _values[a] = _values[b]; _values[b] = v;
            }
        }

        /// <summary>A binary heap of (distance, node) pairs.</summary>
        private sealed class MinHeap
        {
            private readonly List<float> _keys = new List<float>();
            private readonly List<ulong> _values = new List<ulong>();

            public int Count => _keys.Count;

            public void Push(float key, ulong value)
            {
                _keys.Add(key);
                _values.Add(value);
                int i = _keys.Count - 1;

                while (i > 0)
                {
                    int parent = (i - 1) / 2;
                    if (_keys[parent] <= _keys[i]) break;
                    Swap(i, parent);
                    i = parent;
                }
            }

            public void Pop(out float key, out ulong value)
            {
                key = _keys[0];
                value = _values[0];
                int last = _keys.Count - 1;
                _keys[0] = _keys[last];
                _values[0] = _values[last];
                _keys.RemoveAt(last);
                _values.RemoveAt(last);
                int i = 0;

                while (true)
                {
                    int left = i * 2 + 1, right = left + 1, smallest = i;
                    if (left < _keys.Count && _keys[left] < _keys[smallest]) smallest = left;
                    if (right < _keys.Count && _keys[right] < _keys[smallest]) smallest = right;
                    if (smallest == i) break;
                    Swap(i, smallest);
                    i = smallest;
                }
            }

            private void Swap(int a, int b)
            {
                float k = _keys[a]; _keys[a] = _keys[b]; _keys[b] = k;
                ulong v = _values[a]; _values[a] = _values[b]; _values[b] = v;
            }
        }
    }
}
