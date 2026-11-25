using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace SomniumSpace.Worlds.SomniumEuroTruck
{
    /// <summary>
    /// Ground where the game describes none.
    ///
    /// Measured on the demo: the map only carries terrain beside roads and terrain items, so the
    /// ground it describes covers 72 % of a 1200 m square at the map centre but only 28 % round the
    /// Tree-ET depot. The rest is genuinely absent from the files. The game gets away with it
    /// because you only ever see the world from a cab two and a half metres up, where the 100 to
    /// 350 m of terrain beside your own road hides everything past it. Stand anywhere higher, as a
    /// Somnium world lets you, and the gaps are plain - which is exactly what the sky-blue holes in
    /// the scene view were.
    ///
    /// So the gaps are filled here rather than left open. Nothing is invented about the shape: a
    /// coarse grid is dropped over the area and every vertex ray-cast down onto the ground that
    /// really was built. Vertices that land on it take its exact height, vertices over a gap take a
    /// height smoothed out from their neighbours, which follows the surrounding land. Quads wholly
    /// over a gap are drawn whole; every quad that touches real ground is re-sampled finely and its
    /// open sub-cells drawn, tucked under the real ground beside them so the two never meet in one
    /// plane.
    ///
    /// What "real ground" means to the rays matters as much as the grid (measured offline, harness
    /// Program801, against the client's own census round the Szczecin start):
    ///   - the fill itself is not ground. It sits on Default, inside GroundMask; counted as ground
    ///     it made the second build see 8 % gap instead of 74 % (539 holes round the start).
    ///   - a roof is not ground. A prefab's buildings are on Default like its yard, so vertices
    ///     landed on warehouse roofs at 14 to 24 m and seeded the land beside them.
    ///
    /// <b>Tiles (final pass, 2026-09-23).</b> The fill used to be ONE mesh over a 2.2 km square, measured
    /// with 500 rays a frame for about ten seconds (463,000 rays: 5,625 border quads x 81) and then
    /// built, collider cooked and swapped in a single frame every 300 m - immediately followed by the
    /// front end's 14,400-ray census, which runs whenever <see cref="Builds"/> changes (client log
    /// 2026-09-23 18:05: every "Ground fill:" line is followed by an "auto N: ground census" line).
    /// Every sheet moved at once, the frame hitched, and terrain that streamed in after the
    /// measurement stood inside a sheet guessed without it until the next rebuild 300 m later.
    ///
    /// Now the fill is 250 m tiles on a world grid, built nearest first out to <see cref="Extent"/>
    /// round the focus under <see cref="FrameBudgetMs"/> of work a frame (ray casting and mesh
    /// making alike), each swapped on its own. A tile is re-measured when ground is built across it
    /// (<see cref="NoteGround(Transform, Vector3, Vector3)"/>, called by the terrain builders for every
    /// road, terrain side, bezier patch and prefab corner), once that area's streaming has settled,
    /// so what is drawn follows what has loaded instead of a guess made before it arrived. The
    /// border quads are re-sampled at 4.2 m within <see cref="FineRadius"/>, 8.3 m within
    /// <see cref="MidRadius"/> and 12.5 m beyond, and a tile is refined as the focus nears it. Tiles
    /// share their edge heights with the tiles already standing, so the joins close exactly.
    /// </summary>
    public class EtsGroundFill : MonoBehaviour
    {
        [Tooltip("What to keep the filled area centred on. Empty: the stream's own focus (the truck, or the walking player).")]
        public Transform Follow;

        [Tooltip("How far round the focus the fill reaches, metres: every tile any of which lies this close is built.")]
        public float Extent = 1100f;

        [Tooltip("Coarse grid spacing, metres.")]
        public float Cell = 25f;

        /// <summary>Coarse cells along a tile's side: 10 x 25 m = 250 m tiles.</summary>
        public const int TileCells = 10;

        /// <summary>Milliseconds of fill work a frame at most while driving or walking (one ray always runs).</summary>
        public static float FrameBudgetMs = 1.0f;

        /// <summary>The same while the session loads (the stream focus is overridden to the arrival point).</summary>
        public static float LoadingBudgetMs = 4f;

        /// <summary>Tiles this close to the focus re-sample their border quads at 4.2 m (the census and a walker see these).</summary>
        public static float FineRadius = 600f;

        /// <summary>Tiles this close re-sample at 8.3 m; beyond, at 12.5 m (a cab sees them through the fog).</summary>
        public static float MidRadius = 1200f;

        /// <summary>A tile ground was built across waits this long after the last such note before it is measured again.</summary>
        public static float DirtySettleSeconds = 1.5f;

        /// <summary>Tiles further than Extent plus this are taken down.</summary>
        public const float DropMargin = 400f;

        /// <summary>Coarse points measured round a tile on every side, for the smoothing's context.</summary>
        private const int Margin = 2;

        /// <summary>The fill that is running, for the vegetation stream's focus and for <see cref="IsFill"/>.</summary>
        public static EtsGroundFill Current { get; private set; }

        /// <summary>Set after each ring round the focus is complete: how much of it had no ground of its own.</summary>
        public float LastGapShare { get; private set; }

        /// <summary>
        /// How many times the fill has completed round a new centre: once when the tiles within FineRadius of the
        /// first focus are all standing, and once more after each Invalidate (a new centre). The front end runs its
        /// census whenever this changes (EtsFrontEnd, "refilled"); it no longer changes every 300 m, because the fill
        /// no longer rebuilds everything every 300 m.
        /// </summary>
        public static int Builds { get; private set; }

        private sealed class Tile
        {
            public int Tx, Tz;
            public GameObject Object;
            public Mesh Mesh;
            public MeshCollider Collider;

            /// <summary>The smoothed coarse heights, (TileCells + 1)^2, and which of them lay on real ground.</summary>
            public float[] Height;
            public bool[] Known;

            /// <summary>Measured at least once; its heights mean something (it found ground or a neighbour to follow).</summary>
            public bool Built;
            public bool HasHeights;

            /// <summary>Border sub-divisions it was measured with.</summary>
            public int Detail;

            public bool Dirty;
            public float DirtyAt;
            public int Triangles;
            public int KnownPoints;
        }

        private readonly Dictionary<long, Tile> _tiles = new Dictionary<long, Tile>();
        private Transform _tileRoot;
        private readonly RaycastHit[] _hits = new RaycastHit[32];
        private Material _material;
        private int _generation;
        private readonly Stopwatch _frame = new Stopwatch();
        private float _budget = 1f;

        // Reused buffers: a tile measure allocates nothing but its mesh data (the old fill made an int[4] per quad and
        // a sub-grid object per border quad; the ~200 ms "slowest item" stalls round the client log look like collections).
        private float[] _wHeight;
        private bool[] _wKnown;
        private bool[] _wFixed;
        private bool[] _wSeed;
        private bool[] _wDropped;
        private float[] _wLow;
        private bool[] _wWeak;
        private float[] _wWeakY;
        private float[] _wNext;
        private readonly Queue<int> _queue = new Queue<int>();
        private bool[] _subKnown;
        private float[] _subHeight;
        private bool[] _subFill;
        private bool[] _border;
        private int[] _coarseIndex;
        private int[] _subIndex;
        private readonly List<Vector3> _vertices = new List<Vector3>();
        private readonly List<Vector2> _uv = new List<Vector2>();
        private readonly List<int> _triangles = new List<int>();

        // What the fill has done, for the "Terrain: ground fill ..." lines.
        private bool _ringDone;
        private float _startedAt;
        private int _built, _remeasured, _refined, _dropped, _empty, _dirtyMarks, _cliffSeeds, _sheltered, _weakSeeds, _overhangCells, _underDecks;
        private long _rays;
        private double _slowestSliceMs, _slowestSwapMs;
        private float _reportedAt;
        private int _reportedWork;
        private float _droppedAt;

        // ------------------------------------------------------------------ ground notes from the builders

        /// <summary>World boxes of ground built since the fill last looked (x min, z min, x max, z max).</summary>
        private static readonly List<Vector4> PendingGround = new List<Vector4>();
        private static bool _allGroundChanged;

        /// <summary>Ground notes received this session (all builders).</summary>
        public static int GroundNotes { get; private set; }

        /// <summary>
        /// Main thread, from the terrain builders: ground was built over this box (in <paramref name="space"/>'s
        /// local frame, or the world's when it is null). The tiles it crosses are measured again once the stream
        /// has settled there.
        /// </summary>
        public static void NoteGround(Transform space, Vector3 min, Vector3 max)
        {
            if (space != null)
            {
                Matrix4x4 m = space.localToWorldMatrix;

                if (!m.isIdentity)
                {
                    var box = new Bounds((min + max) * 0.5f, max - min);
                    Vector3 c = m.MultiplyPoint3x4(box.center), e = box.extents;
                    var r = new Vector3(
                        Mathf.Abs(m.m00) * e.x + Mathf.Abs(m.m01) * e.y + Mathf.Abs(m.m02) * e.z,
                        Mathf.Abs(m.m10) * e.x + Mathf.Abs(m.m11) * e.y + Mathf.Abs(m.m12) * e.z,
                        Mathf.Abs(m.m20) * e.x + Mathf.Abs(m.m21) * e.y + Mathf.Abs(m.m22) * e.z);
                    min = c - r;
                    max = c + r;
                }
            }

            GroundNotes++;

            if (PendingGround.Count >= 8192)
            {
                // Nobody is consuming (no fill yet): remember only that everything changed.
                PendingGround.Clear();
                _allGroundChanged = true;
            }

            PendingGround.Add(new Vector4(min.x, min.z, max.x, max.z));
        }

        /// <summary>The same for a vertex set.</summary>
        public static void NoteGround(Transform space, Vector3[] vertices)
        {
            if (vertices == null || vertices.Length == 0)
            {
                return;
            }

            Vector3 min = vertices[0], max = vertices[0];

            for (int i = 1; i < vertices.Length; i++)
            {
                min = Vector3.Min(min, vertices[i]);
                max = Vector3.Max(max, vertices[i]);
            }

            NoteGround(space, min, max);
        }

        /// <summary>Whether a collider is one of the fill's own tiles (never ground to anything that measures ground).</summary>
        public static bool IsFill(Collider collider)
        {
            return collider != null && Current != null && Current._tileRoot != null && collider.transform.parent == Current._tileRoot;
        }

        // ------------------------------------------------------------------ focus

        private static Transform _foundTarget;
        private static float _foundAt = -10f;

        /// <summary>
        /// What the map streams round, looked up as the stream does: the map viewer's focus override (the arrival
        /// point while the session is Loading, PLAN U19 - the player may stand in the HQ void 600 m up, and the rays
        /// start 400 m above the focus), else <paramref name="follow"/> (or the running fill's Follow), else the
        /// truck while somebody here drives it or the player while they walk (EtsTruckSeat.StreamFocus), else the
        /// truck. The scene search runs at most once a second.
        /// </summary>
        public static Transform FocusTarget(Transform follow)
        {
            EtsMapViewer viewer = EtsMapViewer.Current;

            if (viewer != null && viewer.StreamFocusOverride != null)
            {
                return viewer.StreamFocusOverride;
            }

            if (follow == null && Current != null)
            {
                follow = Current.Follow;
            }

            if (follow != null)
            {
                return follow;
            }

            if (_foundTarget != null && Time.unscaledTime - _foundAt < 1f)
            {
                return _foundTarget;
            }

            _foundAt = Time.unscaledTime;
            EtsTruckSeat seat = FindFirstObjectByType<EtsTruckSeat>();
            Transform target = seat != null ? seat.StreamFocus : null;

            if (target == null)
            {
                EtsTruckController truck = FindFirstObjectByType<EtsTruckController>();
                target = truck != null ? truck.transform : null;
            }

            _foundTarget = target;
            return target;
        }

        /// <summary>Whether the session is loading (the map viewer streams round the arrival point): more work a frame is fine then.</summary>
        public static bool SessionLoading
        {
            get
            {
                EtsMapViewer viewer = EtsMapViewer.Current;
                return viewer != null && viewer.StreamFocusOverride != null;
            }
        }

        // ------------------------------------------------------------------ life

        private void Start()
        {
            Current = this;
            PendingGround.Clear();
            _allGroundChanged = false;
            _tileRoot = new GameObject("ground fill").transform;
            _tileRoot.SetParent(transform, false);
            _startedAt = Time.unscaledTime;
            StartCoroutine(Run());
        }

        private void OnDestroy()
        {
            ClearTiles();

            if (Current == this)
            {
                Current = null;
            }
        }

        /// <summary>
        /// Measure again from nothing: the map viewer calls it when it builds a new centre (EtsMapViewer.LoadAt),
        /// which puts the new ground at Unity's origin again - where the old tiles still stand, measured against
        /// ground the new centre has thrown away. They go at once.
        /// </summary>
        public void Invalidate()
        {
            _generation++;
            ClearTiles();
            PendingGround.Clear();
            _allGroundChanged = false;
            _ringDone = false;
            _startedAt = Time.unscaledTime;
        }

        private void ClearTiles()
        {
            foreach (Tile tile in _tiles.Values)
            {
                DestroyTile(tile);
            }

            _tiles.Clear();
        }

        private static void DestroyTile(Tile tile)
        {
            if (tile.Mesh != null) Destroy(tile.Mesh);
            if (tile.Object != null) Destroy(tile.Object);
            tile.Mesh = null;
            tile.Object = null;
            tile.Collider = null;
        }

        private float TileSize => TileCells * Cell;

        private static long Key(int tx, int tz)
        {
            return ((long)tx << 32) ^ (uint)tz;
        }

        private static int FloorDiv(int a, int b)
        {
            return a >= 0 ? a / b : -((-a + b - 1) / b);
        }

        /// <summary>Horizontal distance from the focus to the nearest point of a tile.</summary>
        private float TileDistance(int tx, int tz, Vector3 focus)
        {
            float size = TileSize;
            float x0 = tx * size, z0 = tz * size;
            float dx = Mathf.Max(0f, Mathf.Max(x0 - focus.x, focus.x - (x0 + size)));
            float dz = Mathf.Max(0f, Mathf.Max(z0 - focus.z, focus.z - (z0 + size)));
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        private static int DetailFor(float distance)
        {
            return distance <= FineRadius ? 6 : distance <= MidRadius ? 3 : 2;
        }

        private IEnumerator Run()
        {
            yield return null;
            Debug.Log("[SomniumEuroTruck] Terrain: ground fill streams in " + TileSize.ToString("0") + " m tiles out to " + Extent.ToString("0") +
                      " m round the focus, nearest first, under " + FrameBudgetMs.ToString("0.0") + " ms a frame (" + LoadingBudgetMs.ToString("0.0") +
                      " ms while the session loads); border quads re-sampled at " + (Cell / 6f).ToString("0.0") + " m within " + FineRadius.ToString("0") +
                      " m, " + (Cell / 3f).ToString("0.0") + " m within " + MidRadius.ToString("0") + " m, " + (Cell / 2f).ToString("0.0") +
                      " m beyond; a tile is measured again " + DirtySettleSeconds.ToString("0.0") + " s after ground streams in across it.");

            while (true)
            {
                Transform target = FocusTarget(Follow);

                if (target == null || !EtsEngine.IsMounted || !EtsMapViewer.AnyCompleted)
                {
                    yield return new WaitForSeconds(0.5f);
                    continue;
                }

                Vector3 focus = target.position;
                _budget = SessionLoading ? LoadingBudgetMs : FrameBudgetMs;
                ConsumeGroundNotes();

                // The invisible ground pieces waiting for the road footprints get their colliders (EtsTerrainBuilder.DeferInvisible).
                EtsTerrainBuilder.PumpInvisible(SessionLoading ? 2f : 0.5f);
                EtsTerrainBuilder.PumpSkirts(SessionLoading ? 1f : 0.3f);

                if (Time.unscaledTime - _droppedAt > 1f)
                {
                    _droppedAt = Time.unscaledTime;
                    DropFarTiles(focus);
                }

                Tile next = PickNext(focus, out int detail, out int reason);

                if (next == null)
                {
                    CheckRing(focus);
                    Report(false);
                    yield return new WaitForSeconds(0.25f);
                    continue;
                }

                _frame.Restart();
                yield return Measure(next, focus, detail, reason);
                CheckRing(focus);
                Report(false);

                // One tile a frame at most, whatever the budget left: the swap (mesh and collider) is the one step
                // that cannot be cut up.
                yield return null;
            }
        }

        private void ConsumeGroundNotes()
        {
            if (_allGroundChanged)
            {
                _allGroundChanged = false;
                PendingGround.Clear();

                foreach (Tile tile in _tiles.Values)
                {
                    MarkDirty(tile);
                }

                return;
            }

            if (PendingGround.Count == 0)
            {
                return;
            }

            float size = TileSize;

            foreach (Vector4 box in PendingGround)
            {
                // A cell of slack: ground ending on a tile's edge changes its neighbour's edge points too.
                int tx0 = Mathf.FloorToInt((box.x - Cell) / size), tx1 = Mathf.FloorToInt((box.z + Cell) / size);
                int tz0 = Mathf.FloorToInt((box.y - Cell) / size), tz1 = Mathf.FloorToInt((box.w + Cell) / size);

                if ((long)(tx1 - tx0 + 1) * (tz1 - tz0 + 1) > 400)
                {
                    // Something huge (or nonsense coordinates): every tile standing is measured again.
                    foreach (Tile tile in _tiles.Values) MarkDirty(tile);
                    continue;
                }

                for (int tz = tz0; tz <= tz1; tz++)
                {
                    for (int tx = tx0; tx <= tx1; tx++)
                    {
                        if (_tiles.TryGetValue(Key(tx, tz), out Tile tile))
                        {
                            MarkDirty(tile);
                        }
                    }
                }
            }

            PendingGround.Clear();
        }

        private void MarkDirty(Tile tile)
        {
            if (!tile.Built)
            {
                return;
            }

            tile.Dirty = true;
            tile.DirtyAt = Time.unscaledTime;
            _dirtyMarks++;
        }

        private readonly List<long> _dropKeys = new List<long>();

        private void DropFarTiles(Vector3 focus)
        {
            _dropKeys.Clear();

            foreach (KeyValuePair<long, Tile> entry in _tiles)
            {
                if (TileDistance(entry.Value.Tx, entry.Value.Tz, focus) > Extent + DropMargin)
                {
                    _dropKeys.Add(entry.Key);
                }
            }

            foreach (long key in _dropKeys)
            {
                DestroyTile(_tiles[key]);
                _tiles.Remove(key);
                _dropped++;
            }
        }

        /// <summary>
        /// The next tile to measure: the nearest one not built yet or with ground built across it that has settled;
        /// a tile to refine (the focus came closer than its detail was measured for) only when nothing like that is
        /// within 300 m of it.
        /// </summary>
        private Tile PickNext(Vector3 focus, out int detail, out int reason)
        {
            detail = 0;
            reason = 0;
            float size = TileSize;
            int tx0 = Mathf.FloorToInt((focus.x - Extent) / size), tx1 = Mathf.FloorToInt((focus.x + Extent) / size);
            int tz0 = Mathf.FloorToInt((focus.z - Extent) / size), tz1 = Mathf.FloorToInt((focus.z + Extent) / size);
            float best = float.MaxValue;
            int bestTx = 0, bestTz = 0, bestDetail = 0, bestReason = 0;
            float now = Time.unscaledTime;

            for (int tz = tz0; tz <= tz1; tz++)
            {
                for (int tx = tx0; tx <= tx1; tx++)
                {
                    float distance = TileDistance(tx, tz, focus);

                    if (distance > Extent)
                    {
                        continue;
                    }

                    int want = DetailFor(distance);
                    float score;
                    int why;
                    _tiles.TryGetValue(Key(tx, tz), out Tile tile);

                    if (tile == null || !tile.Built)
                    {
                        score = distance;
                        why = 0;
                    }
                    else if (tile.Dirty && now - tile.DirtyAt >= DirtySettleSeconds)
                    {
                        score = distance;
                        why = 1;
                        want = Mathf.Max(want, tile.Detail);
                    }
                    else if (!tile.Dirty && tile.Detail < want)
                    {
                        score = distance + 300f;
                        why = 2;
                    }
                    else
                    {
                        continue;
                    }

                    if (score < best)
                    {
                        best = score;
                        bestTx = tx;
                        bestTz = tz;
                        bestDetail = want;
                        bestReason = why;
                    }
                }
            }

            if (best == float.MaxValue)
            {
                return null;
            }

            long key = Key(bestTx, bestTz);

            if (!_tiles.TryGetValue(key, out Tile picked))
            {
                picked = new Tile { Tx = bestTx, Tz = bestTz, Height = new float[(TileCells + 1) * (TileCells + 1)], Known = new bool[(TileCells + 1) * (TileCells + 1)] };
                _tiles[key] = picked;
            }

            detail = bestDetail;
            reason = bestReason;
            return picked;
        }

        /// <summary>
        /// The ground under (x, z) as the fill must measure it. Returns whether anything at all stands in the column
        /// (a drive-surface hit that is neither one of the fill's own tiles nor a model or prefab piece standing more
        /// than <see cref="RoofClearance"/> above its own model's origin): such a column is never filled.
        ///
        /// <paramref name="seed"/> says whether the column may lend its height to the gaps round it, and
        /// <paramref name="y"/> is that height: the GROUND layer, not whatever is on top (2026-10-27, floating ground).
        /// Taking the highest hit, as the fill did, seeded the gaps beside a motorway viaduct with the deck's height
        /// and the gaps beside the map's overhanging hillsides (Knoten St. Michael: terrain item 42f33d6841c50001 rises
        /// 80-107 m over the older town roads, its own kdop says so) with the hill's - flat khaki sheets 20-90 m up in
        /// the air over the motorway, the user's screenshots. Now:
        ///   - one layer (everything within <see cref="LayerGap"/>): its lowest surface;
        ///   - several layers: the lowest surface (the valley under a deck, the town under a hill, the road under a
        ///     flyover) - except terrain over a road or model surface lower down (a mountain over a tunnel): no height
        ///     at all, so no pit at tunnel depth is dug beside the mountain;
        /// <paramref name="low"/> is the column's lowest surface either way (for <see cref="DropCliffSeeds"/>).
        /// </summary>
        private bool GroundAt(float x, float z, float top, out float y, out bool seed, out float low)
        {
            return GroundAt(x, z, top, out y, out seed, out low, out _);
        }

        /// <summary>As above; <paramref name="weak"/>: the seed is a road, yard or model surface with nothing under it, not terrain.</summary>
        private bool GroundAt(float x, float z, float top, out float y, out bool seed, out float low, out bool weak)
        {
            y = float.MinValue;
            low = float.MaxValue;
            seed = false;
            weak = false;
            bool anyTerrain = false;
            _rays++;
            int count = Physics.RaycastNonAlloc(new Vector3(x, top, z), Vector3.down, _hits, 1200f, EtsLayers.GroundMask | (1 << EtsLayers.Environment), QueryTriggerInteraction.Ignore);
            float high = float.MinValue;
            bool highTerrain = false, lowTerrain = false;
            float lowestOther = float.MaxValue;
            float environment = float.MaxValue;
            float roof = float.MaxValue;

            for (int k = 0; k < count; k++)
            {
                RaycastHit hit = _hits[k];

                // A map model on the Environment layer (a mountain model such as /model2/rock/hills/mountain8.pmd, a building, a
                // fence) is not drive surface, but nothing is filled through or over it (render-lab survey 2026-10-27: sheets
                // 140-190 m over the Alpine mountain models by Salzburg and Innsbruck). It lends no height.
                if (hit.collider.gameObject.layer == EtsLayers.Environment)
                {
                    environment = Mathf.Min(environment, hit.point.y);
                    continue;
                }

                if (hit.collider.transform.parent == _tileRoot)
                {
                    continue;
                }

                if (StandsAboveItsModel(hit))
                {
                    if (IsPrefabPiece(hit.collider))
                    {
                        roof = Mathf.Min(roof, hit.point.y);
                    }

                    continue;
                }

                bool terrain = IsTerrainPiece(hit.collider);
                anyTerrain |= terrain;
                float h = hit.point.y;

                if (h > high)
                {
                    high = h;
                    highTerrain = terrain;
                }

                if (h < low)
                {
                    low = h;
                    lowTerrain = terrain;
                }

                if (!terrain && h < lowestOther)
                {
                    lowestOther = h;
                }
            }

            if (high == float.MinValue)
            {
                if (environment < float.MaxValue)
                {
                    y = environment;
                    low = environment;
                    return true;
                }

                // 2026-10-30 (grass over a Birmingham roundabout and the M6 junctions): only "roof" pieces in the column - a
                // model or prefab piece more than RoofClearance over its own origin. A prefab's y = 0 is NOT always its road
                // level: on a slope its far roads stand metres above it, so every road piece of the junction read as a roof,
                // the junction as empty ground, and the fill laid grass over the tarmac (lab probe: fill at y -0.62 over
                // prefab 217's surfaces at -0.73 .. 0.36 all across the roundabout). Such a column holds something and is
                // never filled; it lends no height either (a roof or a deck must not pull the fill up to it).
                if (roof < float.MaxValue && System.Environment.GetEnvironmentVariable("RL_NOROOF") == null)
                {
                    y = roof;
                    low = roof;
                    return true;
                }

                low = 0f;
                return false;
            }

            if (high - low <= LayerGap)
            {
                y = low;
                seed = true;
                weak = !anyTerrain;
                return true;
            }

            // Terrain over a road or a model surface more than LayerGap below it: a mountain over a tunnel.
            if (highTerrain && lowestOther < high - LayerGap)
            {
                y = high;
                return true;
            }

            y = low;
            seed = true;
            weak = !lowTerrain;
            return true;
        }

        /// <summary>The same, for the callers that only ask whether the column holds anything.</summary>
        private bool GroundAt(float x, float z, float top, out float y)
        {
            return GroundAt(x, z, top, out y, out _, out _);
        }

        /// <summary>A terrain strip of a road, a terrain item, a bezier patch or a prefab corner (EtsTerrainBuilder, EtsPrefabTerrainBuilder names).</summary>
        private static bool IsTerrainPiece(Collider collider)
        {
            string name = collider.gameObject.name;
            return name.StartsWith("terrain ", System.StringComparison.Ordinal) || name.StartsWith("prefab terrain ", System.StringComparison.Ordinal);
        }

        /// <summary>
        /// A ground layer more than this far over the lowest surface of a column next to it (one coarse cell, 25 m:
        /// steeper than 50 degrees) is the edge of an overhang hanging in the air, not ground to continue: its height
        /// is not lent to the gaps (the hillside crests over St. Michael end 70-90 m over the valley beside them).
        /// </summary>
        private const float CliffDrop = 30f;

        /// <summary>Surfaces within this of each other in one column are one layer (a road and the terrain at its edge).</summary>
        private const float LayerGap = 6f;

        /// <summary>See <see cref="CliffDrop"/>: takes the seed off window points standing that far over a known neighbour's lowest surface.</summary>
        private void DropCliffSeeds(int w)
        {
            for (int j = 0; j < w; j++)
            {
                for (int i = 0; i < w; i++)
                {
                    int k = j * w + i;

                    if (!_wSeed[k])
                    {
                        continue;
                    }

                    for (int d = 0; d < 4; d++)
                    {
                        int ni = i + (d == 0 ? -1 : d == 1 ? 1 : 0), nj = j + (d == 2 ? -1 : d == 3 ? 1 : 0);

                        if (ni < 0 || nj < 0 || ni >= w || nj >= w)
                        {
                            continue;
                        }

                        int m = nj * w + ni;

                        if (_wKnown[m] && _wHeight[k] - _wLow[m] > CliffDrop)
                        {
                            _wDropped[k] = true;
                            break;
                        }
                    }
                }
            }

            for (int k = 0; k < w * w; k++)
            {
                if (_wDropped[k])
                {
                    _wSeed[k] = false;
                    _wDropped[k] = false;
                    _cliffSeeds++;
                }
            }
        }

        /// <summary>A piece of a prefab (a junction, a roundabout, a yard): "piece N" under part under "prefab ..." (EtsMapBuilder.BuildPrefab).</summary>
        private static bool IsPrefabPiece(Collider collider)
        {
            Transform root = collider.transform.parent != null ? collider.transform.parent.parent : null;
            return root != null && root.name.StartsWith("prefab ", System.StringComparison.Ordinal);
        }

        /// <summary>
        /// A roof, a canopy, the top of a wall or an elevated ramp: a model piece (EtsModelBuilder
        /// names them "piece N" under part under root) whose hit lies well above the model's y = 0,
        /// which for a model is its node; for a prefab it is NOT always road level (see GroundAt).
        /// </summary>
        private static bool StandsAboveItsModel(RaycastHit hit)
        {
            Transform piece = hit.collider.transform;

            if (!piece.name.StartsWith("piece ", System.StringComparison.Ordinal))
            {
                return false;
            }

            Transform root = piece.parent != null ? piece.parent.parent : null;
            return root != null && root.InverseTransformPoint(hit.point).y > RoofClearance;
        }

        /// <summary>Whether this frame's share of fill work is used up.</summary>
        private bool FrameFull()
        {
            return _frame.Elapsed.TotalMilliseconds >= _budget;
        }

        private void NoteSlice()
        {
            double ms = _frame.Elapsed.TotalMilliseconds;

            if (ms > _slowestSliceMs)
            {
                _slowestSliceMs = ms;
            }
        }

        private void EnsureBuffers(int detail)
        {
            int w = TileCells + 1 + 2 * Margin;

            if (_wHeight == null || _wHeight.Length != w * w)
            {
                _wHeight = new float[w * w];
                _wKnown = new bool[w * w];
                _wFixed = new bool[w * w];
                _wSeed = new bool[w * w];
                _wDropped = new bool[w * w];
                _wLow = new float[w * w];
                _wWeak = new bool[w * w];
                _wWeakY = new float[w * w];
                _wNext = new float[w * w];
                _coarseIndex = new int[(TileCells + 1) * (TileCells + 1)];
                _border = new bool[TileCells * TileCells];
            }

            int points = (detail + 1) * (detail + 1);

            if (_subKnown == null || _subKnown.Length < TileCells * TileCells * points)
            {
                _subKnown = new bool[TileCells * TileCells * 49];
                _subHeight = new float[TileCells * TileCells * 49];
                _subFill = new bool[TileCells * TileCells * 36];
                _subIndex = new int[49];
            }
        }

        private IEnumerator Measure(Tile tile, Vector3 focus, int detail, int reason)
        {
            int generation = _generation;
            float startedAt = Time.unscaledTime;
            int n = TileCells, w = n + 1 + 2 * Margin;
            int gx0 = tile.Tx * n - Margin, gz0 = tile.Tz * n - Margin;
            float top = focus.y + 400f; // 400 m up, 1200 m down: enough for anything in this map's relief
            EnsureBuffers(detail);

            // 1. The coarse window: the tile's points and a margin round it.
            for (int wj = 0; wj < w; wj++)
            {
                for (int wi = 0; wi < w; wi++)
                {
                    int k = wj * w + wi;
                    _wKnown[k] = GroundAt((gx0 + wi) * Cell, (gz0 + wj) * Cell, top, out _wHeight[k], out _wSeed[k], out _wLow[k], out _wWeak[k]);

                    if (FrameFull())
                    {
                        NoteSlice();
                        yield return null;
                        _frame.Restart();

                        if (generation != _generation) yield break;
                    }
                }
            }

            // 2. Heights for the gaps: the tiles standing beside this one lend their own smoothed gap heights, so the
            // joins close; then every other gap point is seeded from the nearest ground and relaxed.
            // Only the ground layer lends heights (GroundAt), and not where it is the crest of an overhang (DropCliffSeeds);
            // a known point that lends none is never drawn but takes a relaxed height like a gap (the border sub-cells
            // next to it interpolate from it).
            DropCliffSeeds(w);

            // A road, yard or model surface with nothing under it (a deck over nothing, a yard with no terrain round it) lends its
            // height only where no terrain in the window does: beside the A9 viaduct by Graz the deck alone seeded sheets at
            // deck height 18-30 m over the valley (render-lab survey, 2026-10-27).
            bool anyStrong = false;

            for (int k = 0; k < w * w; k++)
            {
                if (_wKnown[k] && _wSeed[k] && !_wWeak[k]) { anyStrong = true; break; }
            }

            if (anyStrong)
            {
                for (int k = 0; k < w * w; k++)
                {
                    if (_wKnown[k] && _wSeed[k] && _wWeak[k]) { _wSeed[k] = false; _wWeakY[k] = _wHeight[k]; _weakSeeds++; }
                }
            }

            bool anyFixed = false;
            int knownPoints = 0;

            for (int wj = 0; wj < w; wj++)
            {
                for (int wi = 0; wi < w; wi++)
                {
                    int k = wj * w + wi;
                    _wFixed[k] = _wKnown[k] && _wSeed[k];

                    if (_wKnown[k])
                    {
                        anyFixed |= _wSeed[k];

                        if (!_wSeed[k])
                        {
                            _sheltered++;
                        }

                        // Only the tile's own (TileCells + 1)^2 points: counting the measuring margin round it too gave
                        // up to 225 known of 121 and the "-40 % of their points with no ground" of the 2026-09-25 log.
                        if (wi >= Margin && wi <= Margin + n && wj >= Margin && wj <= Margin + n)
                        {
                            knownPoints++;
                        }
                    }
                    else if (TryNeighbourHeight(tile, gx0 + wi, gz0 + wj, out float h))
                    {
                        _wHeight[k] = h;
                        _wFixed[k] = true;
                        anyFixed = true;
                    }
                }
            }

            if (!anyFixed)
            {
                // Nothing built within 50 m and no neighbour to follow: nothing to fill FROM. Measured again when
                // ground streams in here or a neighbour stands.
                if (tile.Object != null) tile.Object.SetActive(false);
                tile.Built = true;
                tile.HasHeights = false;
                tile.Detail = detail;
                tile.Triangles = 0;
                tile.KnownPoints = 0;
                if (tile.Dirty && tile.DirtyAt <= startedAt) tile.Dirty = false;
                Count(reason, true);
                yield break;
            }

            Smooth(w);

            // A deck: a road, yard or model surface with nothing under it that stands more than UnderDeck over the ground the
            // terrain round it gives this point is a viaduct or a bridge over a gap, not ground - the gap under it is filled at
            // the ground's height (the median slit between the two decks of the A9/S6 bridge at St. Michael showed the void once
            // the deck no longer seeded sheets at its own height, 2026-10-27).
            if (anyStrong)
            {
                for (int k = 0; k < w * w; k++)
                {
                    if (_wKnown[k] && _wWeak[k] && !_wSeed[k] && _wWeakY[k] - _wHeight[k] > UnderDeck)
                    {
                        _wKnown[k] = false;
                        _underDecks++;
                    }
                }
            }

            // 3. Every quad that touches real ground is re-sampled on a grid of its own, and a sub-cell is filled
            // unless its four corners AND its centre all land on ground (Program801: the rim and the full-quad rules
            // this replaced left the census's holes).
            int s = detail, p = s + 1, points = p * p;

            for (int j = 0; j < n; j++)
            {
                for (int i = 0; i < n; i++)
                {
                    int q = j * n + i;
                    int a = (j + Margin) * w + i + Margin, b = a + 1, c = a + w, d = c + 1;
                    _border[q] = false;

                    if (!_wKnown[a] && !_wKnown[b] && !_wKnown[c] && !_wKnown[d])
                    {
                        continue; // wholly over a gap: drawn whole
                    }

                    int sub = q * points;

                    for (int sj = 0; sj < p; sj++)
                    {
                        for (int si = 0; si < p; si++)
                        {
                            int sk = sub + sj * p + si;

                            if ((si == 0 || si == s) && (sj == 0 || sj == s))
                            {
                                int ck = a + (si == 0 ? 0 : 1) + (sj == 0 ? 0 : w);
                                _subKnown[sk] = _wKnown[ck];
                                _subHeight[sk] = _wHeight[ck];
                                continue;
                            }

                            float x = (gx0 + Margin + i + (float)si / s) * Cell;
                            float z = (gz0 + Margin + j + (float)sj / s) * Cell;
                            _subKnown[sk] = GroundAt(x, z, top, out _subHeight[sk], out _, out _, out bool subWeak);

                            if (_subKnown[sk] && subWeak && anyStrong)
                            {
                                float est = Mathf.Lerp(Mathf.Lerp(_wHeight[a], _wHeight[b], (float)si / s), Mathf.Lerp(_wHeight[c], _wHeight[d], (float)si / s), (float)sj / s);

                                if (_subHeight[sk] - est > UnderDeck)
                                {
                                    _subKnown[sk] = false;
                                    _subHeight[sk] = est;
                                }
                            }

                            if (FrameFull())
                            {
                                NoteSlice();
                                yield return null;
                                _frame.Restart();

                                if (generation != _generation) yield break;
                            }
                        }
                    }

                    bool any = false;
                    int cells = q * s * s;

                    for (int sj = 0; sj < s; sj++)
                    {
                        for (int si = 0; si < s; si++)
                        {
                            int s0 = sub + sj * p + si;
                            bool open = !(_subKnown[s0] && _subKnown[s0 + 1] && _subKnown[s0 + p] && _subKnown[s0 + p + 1]);

                            if (!open)
                            {
                                // Four corners on ground; one more ray settles a gap narrower than the sub-cell.
                                float x = (gx0 + Margin + i + (si + 0.5f) / s) * Cell;
                                float z = (gz0 + Margin + j + (sj + 0.5f) / s) * Cell;
                                open = !GroundAt(x, z, top, out float centreY, out _, out _, out bool centreWeak);

                                if (!open && centreWeak && anyStrong)
                                {
                                    float est = Mathf.Lerp(Mathf.Lerp(_wHeight[a], _wHeight[b], (si + 0.5f) / s), Mathf.Lerp(_wHeight[c], _wHeight[d], (si + 0.5f) / s), (sj + 0.5f) / s);
                                    open = centreY - est > UnderDeck;
                                }

                                if (FrameFull())
                                {
                                    NoteSlice();
                                    yield return null;
                                    _frame.Restart();

                                    if (generation != _generation) yield break;
                                }
                            }

                            _subFill[cells + sj * s + si] = open;
                            any |= open;
                        }
                    }

                    _border[q] = any;
                }
            }

            NoteSlice();

            // 4. The swap, on a frame of its own.
            yield return null;
            _frame.Restart();

            if (generation != _generation) yield break;

            Emit(tile, detail, gx0, gz0, w);

            // Its heights, for the neighbours that follow it.
            for (int j = 0; j <= n; j++)
            {
                for (int i = 0; i <= n; i++)
                {
                    int k = (j + Margin) * w + i + Margin;
                    tile.Height[j * (n + 1) + i] = _wHeight[k];
                    tile.Known[j * (n + 1) + i] = _wKnown[k];
                }
            }

            tile.Built = true;
            tile.HasHeights = true;
            tile.Detail = detail;
            tile.KnownPoints = knownPoints;

            if (tile.Dirty && tile.DirtyAt <= startedAt)
            {
                tile.Dirty = false;
            }

            double swap = _frame.Elapsed.TotalMilliseconds;

            if (swap > _slowestSwapMs)
            {
                _slowestSwapMs = swap;
            }

            Count(reason, false);
        }

        private void Count(int reason, bool empty)
        {
            if (reason == 0) _built++;
            else if (reason == 1) _remeasured++;
            else _refined++;

            if (empty) _empty++;
        }

        /// <summary>
        /// The smoothed gap height a standing neighbour gave global coarse point (gx, gz), if one holds it as a gap
        /// point: the joins then close exactly. A neighbour waiting to be measured again is not followed (its heights
        /// are stale; it follows this one when its turn comes).
        /// </summary>
        private bool TryNeighbourHeight(Tile self, int gx, int gz, out float height)
        {
            int n = TileCells;
            int tx = FloorDiv(gx, n), tz = FloorDiv(gz, n);
            int lx = gx - tx * n, lz = gz - tz * n;

            for (int c = 0; c < 4; c++)
            {
                int dx = c & 1, dz = c >> 1;

                if ((dx == 1 && lx != 0) || (dz == 1 && lz != 0))
                {
                    continue;
                }

                int cx = tx - dx, cz = tz - dz;

                if (cx == self.Tx && cz == self.Tz)
                {
                    continue;
                }

                if (!_tiles.TryGetValue(Key(cx, cz), out Tile other) || !other.Built || !other.HasHeights || other.Dirty)
                {
                    continue;
                }

                int k = (lz + dz * n) * (n + 1) + lx + dx * n;

                if (!other.Known[k])
                {
                    height = other.Height[k];
                    return true;
                }
            }

            height = 0f;
            return false;
        }

        /// <summary>
        /// Gives the window's gap points a height: every one starts at the height of the NEAREST fixed point (real
        /// ground or a neighbour's edge), found by walking outwards from all of them at once, then 40 passes average
        /// each with its neighbours. Seeded from the nearest ground, each gap starts at its own surroundings' height
        /// (a map-wide average made flat sheets through hillsides), and the passes only smooth the joins.
        /// </summary>
        private void Smooth(int w)
        {
            _queue.Clear();

            for (int k = 0; k < w * w; k++)
            {
                if (_wFixed[k]) _queue.Enqueue(k);
            }

            // _wNext doubles as the "seeded" marks during the walk (1 = seeded).
            for (int k = 0; k < w * w; k++)
            {
                _wNext[k] = _wFixed[k] ? 1f : 0f;
            }

            while (_queue.Count > 0)
            {
                int k = _queue.Dequeue();
                int i = k % w, j = k / w;

                for (int d = 0; d < 4; d++)
                {
                    int ni = i + (d == 0 ? -1 : d == 1 ? 1 : 0);
                    int nj = j + (d == 2 ? -1 : d == 3 ? 1 : 0);

                    if (ni < 0 || nj < 0 || ni >= w || nj >= w) continue;

                    int m = nj * w + ni;

                    if (_wNext[m] > 0f) continue;

                    _wNext[m] = 1f;
                    _wHeight[m] = _wHeight[k];
                    _queue.Enqueue(m);
                }
            }

            for (int pass = 0; pass < 40; pass++)
            {
                for (int j = 0; j < w; j++)
                {
                    for (int i = 0; i < w; i++)
                    {
                        int k = j * w + i;

                        if (_wFixed[k])
                        {
                            _wNext[k] = _wHeight[k];
                            continue;
                        }

                        float sum = 0f;
                        int count = 0;

                        if (i > 0) { sum += _wHeight[k - 1]; count++; }
                        if (i < w - 1) { sum += _wHeight[k + 1]; count++; }
                        if (j > 0) { sum += _wHeight[k - w]; count++; }
                        if (j < w - 1) { sum += _wHeight[k + w]; count++; }

                        _wNext[k] = count > 0 ? sum / count : _wHeight[k];
                    }
                }

                System.Array.Copy(_wNext, _wHeight, w * w);
            }
        }

        /// <summary>The most a fill quad may rise across one cell before it is a wall rather than ground.</summary>
        private const float MaxFillSlope = 20f;

        /// <summary>How far under the lowest real ground round it a border vertex is laid.</summary>
        private const float TuckDepth = 0.3f;

        /// <summary>The most a border vertex is pulled under its interpolated height to tuck under the ground beside it.</summary>
        private const float MaxTuck = 4f;

        /// <summary>A road, yard or model surface with nothing under it this far over the ground round it is a deck: the gap under it is filled.</summary>
        private const float UnderDeck = 3f;

        /// <summary>A model piece hit this far above its model's origin is a roof, not ground.</summary>
        private const float RoofClearance = 1.5f;

        private void Emit(Tile tile, int detail, int gx0, int gz0, int w)
        {
            int n = TileCells, s = detail, p = s + 1, points = p * p;
            _vertices.Clear();
            _uv.Clear();
            _triangles.Clear();

            for (int k = 0; k < _coarseIndex.Length; k++)
            {
                _coarseIndex[k] = -1;
            }

            // Quads lying WHOLLY over a gap are drawn whole; the rest are the border's.
            for (int j = 0; j < n; j++)
            {
                for (int i = 0; i < n; i++)
                {
                    int a = (j + Margin) * w + i + Margin, b = a + 1, c = a + w, d = c + 1;

                    if (_wKnown[a] || _wKnown[b] || _wKnown[c] || _wKnown[d])
                    {
                        continue;
                    }

                    // A quad standing on end is a wall through the world, not ground.
                    float low = Mathf.Min(Mathf.Min(_wHeight[a], _wHeight[b]), Mathf.Min(_wHeight[c], _wHeight[d]));
                    float high = Mathf.Max(Mathf.Max(_wHeight[a], _wHeight[b]), Mathf.Max(_wHeight[c], _wHeight[d]));

                    if (high - low > MaxFillSlope)
                    {
                        continue;
                    }

                    int va = CoarseVertex(i, j, a, gx0, gz0);
                    int vb = CoarseVertex(i + 1, j, b, gx0, gz0);
                    int vc = CoarseVertex(i, j + 1, c, gx0, gz0);
                    int vd = CoarseVertex(i + 1, j + 1, d, gx0, gz0);
                    _triangles.Add(va); _triangles.Add(vc); _triangles.Add(vb);
                    _triangles.Add(vb); _triangles.Add(vc); _triangles.Add(vd);
                }
            }

            // The border quads' open sub-cells. Heights come from the coarse corners by bilinear interpolation, which
            // along a shared edge is the straight line the coarse quad already has; then every vertex is pulled down
            // under the lowest real ground measured in its 3 x 3 neighbourhood, so an open sub-cell stays under the
            // ground beside it rather than level with it (flicker) or over it (a lid).
            for (int j = 0; j < n; j++)
            {
                for (int i = 0; i < n; i++)
                {
                    int q = j * n + i;

                    if (!_border[q])
                    {
                        continue;
                    }

                    int a = (j + Margin) * w + i + Margin;
                    float h00 = _wHeight[a], h10 = _wHeight[a + 1], h01 = _wHeight[a + w], h11 = _wHeight[a + w + 1];
                    int sub = q * points, cells = q * s * s;

                    for (int k = 0; k < points; k++)
                    {
                        _subIndex[k] = -1;
                    }

                    for (int sj = 0; sj < s; sj++)
                    {
                        for (int si = 0; si < s; si++)
                        {
                            if (!_subFill[cells + sj * s + si])
                            {
                                continue;
                            }

                            int sa = sj * p + si, sb = sa + 1, sc = sa + p, sd = sc + 1;

                            // A cell that would have to be pulled more than MaxTuck down to tuck under the ground beside it hangs
                            // over much lower ground: left open rather than drawn as a lid in the air or stood on end (2026-10-27).
                            if (TuckOf(sa, p, s, sub, h00, h10, h01, h11) > MaxTuck || TuckOf(sb, p, s, sub, h00, h10, h01, h11) > MaxTuck ||
                                TuckOf(sc, p, s, sub, h00, h10, h01, h11) > MaxTuck || TuckOf(sd, p, s, sub, h00, h10, h01, h11) > MaxTuck)
                            {
                                _overhangCells++;
                                continue;
                            }

                            int va = SubVertex(sa, p, s, sub, i, j, gx0, gz0, h00, h10, h01, h11);
                            int vb = SubVertex(sb, p, s, sub, i, j, gx0, gz0, h00, h10, h01, h11);
                            int vc = SubVertex(sc, p, s, sub, i, j, gx0, gz0, h00, h10, h01, h11);
                            int vd = SubVertex(sd, p, s, sub, i, j, gx0, gz0, h00, h10, h01, h11);
                            _triangles.Add(va); _triangles.Add(vc); _triangles.Add(vb);
                            _triangles.Add(vb); _triangles.Add(vc); _triangles.Add(vd);
                        }
                    }
                }
            }

            tile.Triangles = _triangles.Count / 3;

            if (_triangles.Count == 0)
            {
                if (tile.Object != null) tile.Object.SetActive(false);
                return;
            }

            if (_material == null)
            {
                _material = EtsEngine.Materials.Load(EtsTerrainBuilder.FallbackMaterial);
            }

            if (tile.Object == null)
            {
                tile.Object = new GameObject("fill " + tile.Tx + " " + tile.Tz);
                tile.Object.transform.SetParent(_tileRoot, false);
                tile.Mesh = new Mesh { name = "ground fill " + tile.Tx + " " + tile.Tz };
                tile.Object.AddComponent<MeshFilter>().sharedMesh = tile.Mesh;
                var renderer = tile.Object.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = _material;
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                tile.Collider = tile.Object.AddComponent<MeshCollider>();
            }

            // The collider lets go of the mesh first, so assigning it again cooks the new shape.
            tile.Collider.sharedMesh = null;
            tile.Mesh.Clear();
            tile.Mesh.SetVertices(_vertices);
            tile.Mesh.SetUVs(0, _uv);
            tile.Mesh.SetTriangles(_triangles, 0);
            tile.Mesh.RecalculateNormals();
            tile.Mesh.RecalculateBounds();
            tile.Collider.sharedMesh = tile.Mesh;

            if (!tile.Object.activeSelf)
            {
                tile.Object.SetActive(true);
            }
        }

        private int CoarseVertex(int i, int j, int windowIndex, int gx0, int gz0)
        {
            int k = j * (TileCells + 1) + i;

            if (_coarseIndex[k] >= 0)
            {
                return _coarseIndex[k];
            }

            var v = new Vector3((gx0 + Margin + i) * Cell, _wHeight[windowIndex] - 0.15f, (gz0 + Margin + j) * Cell);
            _coarseIndex[k] = _vertices.Count;
            _vertices.Add(v);
            _uv.Add(new Vector2(v.x / EtsTerrainBuilder.FallbackTile, v.z / EtsTerrainBuilder.FallbackTile));
            return _coarseIndex[k];
        }

        /// <summary>How far a border vertex would be pulled under its interpolated height to tuck under the ground measured round it.</summary>
        private float TuckOf(int corner, int p, int s, int sub, float h00, float h10, float h01, float h11)
        {
            int ci = corner % p, cj = corner / p;
            float u = (float)ci / s, v = (float)cj / s;
            float lerped = Mathf.Lerp(Mathf.Lerp(h00, h10, u), Mathf.Lerp(h01, h11, u), v) - 0.15f;
            float y = lerped;

            for (int dj = -1; dj <= 1; dj++)
            {
                for (int di = -1; di <= 1; di++)
                {
                    int ni = ci + di, nj = cj + dj;
                    if (ni < 0 || nj < 0 || ni > s || nj > s) continue;
                    int nk = sub + nj * p + ni;
                    if (_subKnown[nk]) y = Mathf.Min(y, _subHeight[nk] - TuckDepth);
                }
            }

            return lerped - y;
        }

        private int SubVertex(int corner, int p, int s, int sub, int i, int j, int gx0, int gz0, float h00, float h10, float h01, float h11)
        {
            if (_subIndex[corner] >= 0)
            {
                return _subIndex[corner];
            }

            int ci = corner % p, cj = corner / p;
            float u = (float)ci / s, v = (float)cj / s;
            float y = Mathf.Lerp(Mathf.Lerp(h00, h10, u), Mathf.Lerp(h01, h11, u), v) - 0.15f;
            float lerped = y;

            for (int dj = -1; dj <= 1; dj++)
            {
                for (int di = -1; di <= 1; di++)
                {
                    int ni = ci + di, nj = cj + dj;
                    if (ni < 0 || nj < 0 || ni > s || nj > s) continue;
                    int nk = sub + nj * p + ni;
                    if (_subKnown[nk]) y = Mathf.Min(y, _subHeight[nk] - TuckDepth);
                }
            }

            // Never pulled more than MaxTuck under its own surface: a vertex dragged 40 m down to the ground beside it
            // stood the sub-cell on end - the khaki "pillars" hanging from the old sheets (2026-10-27).
            y = Mathf.Max(y, lerped - MaxTuck);

            var point = new Vector3((gx0 + Margin + i + u) * Cell, y, (gz0 + Margin + j + v) * Cell);
            _subIndex[corner] = _vertices.Count;
            _vertices.Add(point);
            _uv.Add(new Vector2(point.x / EtsTerrainBuilder.FallbackTile, point.z / EtsTerrainBuilder.FallbackTile));
            return _subIndex[corner];
        }

        /// <summary>
        /// Once the tiles within FineRadius of the focus all stand at full detail with nothing waiting: the fill is
        /// complete round a new centre (Builds, the census) - once per centre.
        /// </summary>
        private void CheckRing(Vector3 focus)
        {
            if (_ringDone)
            {
                return;
            }

            float size = TileSize;
            float reach = Mathf.Min(FineRadius, Extent);
            int tx0 = Mathf.FloorToInt((focus.x - reach) / size), tx1 = Mathf.FloorToInt((focus.x + reach) / size);
            int tz0 = Mathf.FloorToInt((focus.z - reach) / size), tz1 = Mathf.FloorToInt((focus.z + reach) / size);
            int tiles = 0, points = 0, known = 0;

            for (int tz = tz0; tz <= tz1; tz++)
            {
                for (int tx = tx0; tx <= tx1; tx++)
                {
                    if (TileDistance(tx, tz, focus) > reach) continue;

                    if (!_tiles.TryGetValue(Key(tx, tz), out Tile tile) || !tile.Built || tile.Dirty || tile.Detail < DetailFor(TileDistance(tx, tz, focus)))
                    {
                        return;
                    }

                    tiles++;
                    points += (TileCells + 1) * (TileCells + 1);
                    known += tile.KnownPoints;
                }
            }

            _ringDone = true;
            LastGapShare = points > 0 ? 1f - (float)known / points : 0f;
            Builds++;
            int triangles = 0;

            foreach (Tile tile in _tiles.Values) triangles += tile.Triangles;

            Debug.Log("[SomniumEuroTruck] Terrain: ground fill ready round the focus in " + (Time.unscaledTime - _startedAt).ToString("0.0") + " s - " + tiles +
                      " tiles within " + reach.ToString("0") + " m at " + (Cell / 6f).ToString("0.0") + " m, " + (LastGapShare * 100f).ToString("0") +
                      " % of their points with no ground of their own; " + _tiles.Count + " tiles standing, " + triangles + " triangles; " + _rays +
                      " rays so far, slowest frame of fill work " + _slowestSliceMs.ToString("0.0") + " ms, slowest tile swap " + _slowestSwapMs.ToString("0.0") +
                      " ms; heights from the ground layer only: " + _sheltered + " covered points lent none (terrain over a tunnel), " + _cliffSeeds + " overhang crests and " + _weakSeeds + " road or yard surfaces with terrain round them lent none, " + _overhangCells + " border cells over much lower ground left open, " + _underDecks + " points under a deck filled at the ground's height (2026-10-27). The map only describes terrain beside roads and terrain items.");
        }

        /// <summary>At most every 30 s, when there was work: what the tiles did since the last line.</summary>
        private void Report(bool force)
        {
            int work = _built + _remeasured + _refined + _dropped;

            if (!force && (work == _reportedWork || Time.unscaledTime - _reportedAt < 30f))
            {
                return;
            }

            _reportedAt = Time.unscaledTime;
            _reportedWork = work;
            int standing = 0, triangles = 0, waiting = 0;

            foreach (Tile tile in _tiles.Values)
            {
                if (tile.Triangles > 0) standing++;
                if (tile.Dirty) waiting++;
                triangles += tile.Triangles;
            }

            Debug.Log("[SomniumEuroTruck] Terrain: ground fill tiles - " + _built + " built, " + _remeasured + " measured again after ground streamed in across them (" +
                      _dirtyMarks + " marks from " + GroundNotes + " ground notes), " + _refined + " refined as the focus neared, " + _dropped + " dropped, " + _empty +
                      " with nothing to fill from; " + _tiles.Count + " tiles (" + standing + " drawn, " + triangles + " triangles, " + waiting + " waiting to settle); " +
                      _rays + " rays; slowest frame of fill work " + _slowestSliceMs.ToString("0.0") + " ms (budget " + _budget.ToString("0.0") +
                      " ms), slowest tile swap " + _slowestSwapMs.ToString("0.0") + " ms.");
            _slowestSliceMs = 0;
            _slowestSwapMs = 0;
        }
    }
}
