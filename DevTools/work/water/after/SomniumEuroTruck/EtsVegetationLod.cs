using System;
using System.Collections.Generic;
using System.Diagnostics;
using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
using UnityEngine;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;

namespace SomniumSpace.Worlds.SomniumEuroTruck
{
    /// <summary>
    /// The detail-vegetation cells (grass owner, 2026-09-25; environment DESIGN.md §C.3): the game's grass tufts,
    /// flowers and weeds round the eye, built from the terrain sides' quad grids (EtsDetailPatch) into one combined
    /// mesh per 32 m world cell and material, drawn by SomniumEuroTruck/Leaves in band 3 (a dithered fade to nothing
    /// by each card's pivot, over EtsVegetationLook.Fade.z +- .w: 89-105 m on a desktop, the file's max_distance 105;
    /// 52-68 m in a headset, where EtsVegetationLook clamps the end to 60) and casting no shadow, as the game's
    /// eut2.leaves grass does.
    ///
    /// Every 0.25 s the cells whose square comes within the fade's end of the eye (the last screen camera,
    /// EtsVegetationLook.ViewPosition, else the stream's focus) are wanted; cells more than a cell further are dropped.
    /// A wanted cell that is new, or whose patches changed (an item streamed in or out), is generated on a worker
    /// thread (EtsDetailVegetation.BuildCell: pure, integer-hashed, the same cards on every client), nearest first, one
    /// at a time, and its meshes are uploaded on the main thread within <see cref="UploadBudgetMs"/> a frame (one
    /// always). At most <see cref="CardCap"/> cards live: a cell that would pass it keeps a hashed share of its cards.
    ///
    /// Per frame this does no allocation: the cell scan runs 4 times a second over a fixed grid round the eye, and the
    /// only allocations are a job's snapshot and its result arrays, made when a cell is (re)built.
    /// Made on the map viewer's object by the first patch that registers (<see cref="Ensure"/>), so it is in the
    /// world's scene and goes with it; the patch registry is static so a patch built first is kept.
    /// </summary>
    public sealed class EtsVegetationLod : MonoBehaviour
    {
        /// <summary>Whether detail vegetation is built at all.</summary>
        public static bool Enabled = true;

        /// <summary>
        /// The density's unit is not in the files; cards per m2 = def density / 100 x this (EtsDetailVegetation
        /// .CardsPerSquareMetre): 35 x 2.5 / 100 = 0.875 cards a square metre. Calibrated in the render lab against the
        /// game's own photographs of its verges (u021's by the Dover HQ, u024's on the Munich motorway): at 1.0 the Dover
        /// verge showed separate tufts with bare ground between them (N/grass/lab/d1_b_road.png), at 2.5 it is covered
        /// with grass and flowers as the game's are (N/grass2/lab/g25_b_road.png, crop N/grass2/crops/g25_b_road_verge.png
        /// against N/grass/crops/u021_verge.png; grass owner, 2026-09-25).
        /// </summary>
        public static float DensityScale = 2.5f;

        /// <summary>World cell edge, metres.</summary>
        public const float CellSize = 32f;

        /// <summary>
        /// Cards alive at most on a desktop (out to 105 m). The Dover motorway verge (lab view b_road) wants about 19,800
        /// at <see cref="DensityScale"/> 2.5, so this is not reached there.
        /// </summary>
        public static int CardCap = 30000;

        /// <summary>
        /// Cards alive at most in a headset (out to 68 m): the same verge wants about 11,000 there (the census's "within
        /// 60 m" count). Cells are built nearest first, so a cap that bites thins the farthest cells.
        /// </summary>
        public static int CardCapHeadset = 16000;

        /// <summary>The cap in force (headset or desktop).</summary>
        public static int Cap => UnityEngine.XR.XRSettings.enabled ? CardCapHeadset : CardCap;

        /// <summary>A thinned cell is built again once this many cards of the cap are free (so it fills in as the truck moves on).</summary>
        public const int RefillHeadroom = 500;

        /// <summary>Main-thread milliseconds of mesh upload a frame (one cell always goes in).</summary>
        public static float UploadBudgetMs = 1f;

        /// <summary>Half the dithered fade band, metres (EtsVegetationLook.Fade.w).</summary>
        public static float FadeHalf = 8f;

        private static EtsVegetationLod _instance;
        private static readonly Dictionary<int, EtsDetailVegetation.Patch> Patches = new Dictionary<int, EtsDetailVegetation.Patch>();
        private static readonly Dictionary<long, List<EtsDetailVegetation.Patch>> ByCell = new Dictionary<long, List<EtsDetailVegetation.Patch>>();
        private static readonly List<long> DirtyKeys = new List<long>();

        private sealed class SlotPiece
        {
            public int Slot;
            public Mesh Mesh;
            public MeshRenderer Renderer;
            public int Cards;
        }

        private sealed class Cell
        {
            public long Key;
            public int X, Z;
            public readonly List<SlotPiece> Pieces = new List<SlotPiece>();
            public int Cards;
            public bool Dirty = true;
            public bool InFlight;
            public int Version;
            public int Job;
            public float Distance;

            /// <summary>Built with fewer cards than it grows (the cap bit); built again when room frees up.</summary>
            public bool Thinned;
        }

        private readonly Dictionary<long, Cell> _cells = new Dictionary<long, Cell>();
        private readonly List<Cell> _queue = new List<Cell>();
        private readonly List<long> _drop = new List<long>();
        private readonly List<Material> _materials = new List<Material>();
        private readonly Stopwatch _clock = new Stopwatch();
        private EtsWorker _worker;
        private Transform _root;
        private Cell _inFlight;
        private float _tickAt = -10f, _reportedAt = -100f;
        private int _liveCards, _thinned, _failures, _reportedCards = -1, _uploads;
        private double _slowestBuildMs, _slowestUploadMs;
        private Vector3 _eye;
        private bool _haveEye;
        private float _end;
        private int _jobs;
        private Func<bool> _more;

        /// <summary>Cards drawn now.</summary>
        public static int CardsLive => _instance != null ? _instance._liveCards : 0;

        /// <summary>Makes sure the cells run: on the map viewer's object when there is one, else on an object of its own.</summary>
        public static void Ensure()
        {
            if (_instance != null)
            {
                return;
            }

            EtsMapViewer viewer = EtsMapViewer.Current;
            GameObject host = viewer != null ? viewer.gameObject : null;

            // Not "??": in the editor GetComponent hands back a fake-null object that only == null sees.
            EtsVegetationLod found = host != null ? host.GetComponent<EtsVegetationLod>() : null;

            if (found == null)
            {
                found = host != null ? host.AddComponent<EtsVegetationLod>() : new GameObject("Ets grass").AddComponent<EtsVegetationLod>();
            }

            _instance = found;
        }

        private static long KeyOf(int x, int z)
        {
            return ((long)x << 32) ^ (uint)z;
        }

        /// <summary>A side's grid joins the cells it overlaps (EtsDetailPatch.OnEnable).</summary>
        public static void Register(EtsDetailVegetation.Patch patch)
        {
            if (patch == null || Patches.ContainsKey(patch.Id))
            {
                return;
            }

            Patches[patch.Id] = patch;
            int x0 = Mathf.FloorToInt(patch.Min.x / CellSize), x1 = Mathf.FloorToInt(patch.Max.x / CellSize);
            int z0 = Mathf.FloorToInt(patch.Min.y / CellSize), z1 = Mathf.FloorToInt(patch.Max.y / CellSize);

            // A grid more than 2 km across is corrupt data, not ground.
            if (x1 - x0 > 64 || z1 - z0 > 64)
            {
                return;
            }

            for (int x = x0; x <= x1; x++)
            {
                for (int z = z0; z <= z1; z++)
                {
                    long key = KeyOf(x, z);

                    if (!ByCell.TryGetValue(key, out List<EtsDetailVegetation.Patch> list))
                    {
                        list = new List<EtsDetailVegetation.Patch>();
                        ByCell[key] = list;
                    }

                    list.Add(patch);
                    DirtyKeys.Add(key);
                }
            }

            Ensure();
        }

        /// <summary>A side's grid leaves its cells (EtsDetailPatch.OnDisable: the item was unloaded or switched off).</summary>
        public static void Unregister(EtsDetailVegetation.Patch patch)
        {
            if (patch == null || !Patches.Remove(patch.Id))
            {
                return;
            }

            int x0 = Mathf.FloorToInt(patch.Min.x / CellSize), x1 = Mathf.FloorToInt(patch.Max.x / CellSize);
            int z0 = Mathf.FloorToInt(patch.Min.y / CellSize), z1 = Mathf.FloorToInt(patch.Max.y / CellSize);

            if (x1 - x0 > 64 || z1 - z0 > 64)
            {
                return;
            }

            for (int x = x0; x <= x1; x++)
            {
                for (int z = z0; z <= z1; z++)
                {
                    long key = KeyOf(x, z);

                    if (ByCell.TryGetValue(key, out List<EtsDetailVegetation.Patch> list))
                    {
                        list.Remove(patch);

                        if (list.Count == 0)
                        {
                            ByCell.Remove(key);
                        }
                    }

                    DirtyKeys.Add(key);
                }
            }
        }

        private void Awake()
        {
            if (_instance == null)
            {
                _instance = this;
            }
        }

        private void Start()
        {
            if (_instance != this)
            {
                return;
            }

            _worker = new EtsWorker("Ets grass");
            _more = () => _clock.Elapsed.TotalMilliseconds < UploadBudgetMs;
            _reportedAt = Time.unscaledTime;
            var root = new GameObject("grass cells");
            root.transform.SetParent(transform, false);
            _root = root.transform;

            // The fade the leaves shader's band 3 reads: out by the file's max_distance on a desktop (EtsVegetationLook
            // clamps the end to 60 m in a headset).
            ApplyFade();
        }

        /// <summary>
        /// The band-3 window in EtsVegetationLook.Fade (z the fade's middle, w half its width): out by the file's
        /// max_distance (105 m) on a desktop; EtsVegetationLook clamps the middle to 60 m in a headset. Only z and w are
        /// written (the trees own x and y), again each tick in case the whole vector was set elsewhere.
        /// </summary>
        private static void ApplyFade()
        {
            float end = EtsDetailVegetation.MaxDistance > 10f ? EtsDetailVegetation.MaxDistance : 105f;
            Vector4 fade = EtsVegetationLook.Fade;

            if (fade.z != end - FadeHalf || fade.w != FadeHalf)
            {
                EtsVegetationLook.Fade = new Vector4(fade.x, fade.y, end - FadeHalf, FadeHalf);
            }
        }

        private void OnDestroy()
        {
            if (_worker != null)
            {
                _worker.Dispose();
                _worker = null;
            }

            foreach (Cell cell in _cells.Values)
            {
                DestroyPieces(cell);
            }

            _cells.Clear();

            foreach (Material m in _materials)
            {
                if (m != null)
                {
                    Destroy(m);
                }
            }

            _materials.Clear();

            if (_instance == this)
            {
                _instance = null;
            }
        }

        private void Update()
        {
            if (_instance != this || _worker == null)
            {
                return;
            }

            try
            {
                _clock.Restart();
                _worker.Drain(_more);

                float now = Time.unscaledTime;

                if (now - _tickAt >= 0.25f)
                {
                    _tickAt = now;
                    Tick();
                }

                if (_inFlight == null)
                {
                    PostNext();
                }

                Report(now);
            }
            catch (Exception e)
            {
                if (_failures++ < 3)
                {
                    Debug.LogWarning("[SomniumEuroTruck] Grass: the cells failed this frame: " + e.GetType().Name + ": " + e.Message);
                }
            }
        }

        /// <summary>The eye and the fade's end; which cells are wanted, which go; the build queue, nearest first.</summary>
        private void Tick()
        {
            ApplyFade();

            if (EtsVegetationLook.ViewFrame >= Time.frameCount - 2)
            {
                _eye = EtsVegetationLook.ViewPosition;
                _haveEye = true;
            }
            else
            {
                Transform focus = EtsGroundFill.FocusTarget(null);
                _haveEye = focus != null;

                if (_haveEye)
                {
                    _eye = focus.position;
                }
            }

            Vector4 fade = EtsVegetationLook.Fade;
            float fadeEnd = fade.z;

            if (UnityEngine.XR.XRSettings.enabled && fadeEnd > 60f)
            {
                fadeEnd = 60f; // as EtsVegetationLook does for the shader
            }

            _end = fadeEnd + Mathf.Max(0f, fade.w);

            // Patches that came or went: their cells are built again.
            for (int i = 0; i < DirtyKeys.Count; i++)
            {
                if (_cells.TryGetValue(DirtyKeys[i], out Cell dirty))
                {
                    dirty.Dirty = true;
                    dirty.Version++;
                }
            }

            DirtyKeys.Clear();

            // Cells the cap thinned are built again (nearest first, below) once cards have been freed, e.g. by the cells
            // left behind: a cell never stays thin for having been built while the budget was short.
            if (Cap - _liveCards >= RefillHeadroom)
            {
                foreach (Cell cell in _cells.Values)
                {
                    if (cell.Thinned && !cell.InFlight)
                    {
                        cell.Thinned = false;
                        cell.Dirty = true;
                    }
                }
            }

            if (!_haveEye)
            {
                return;
            }

            float keep = _end + CellSize;
            _drop.Clear();

            foreach (KeyValuePair<long, Cell> pair in _cells)
            {
                Cell cell = pair.Value;
                cell.Distance = CellDistance(cell.X, cell.Z);

                if (cell.Distance > keep || !ByCell.ContainsKey(cell.Key))
                {
                    _drop.Add(pair.Key);
                }
            }

            for (int i = 0; i < _drop.Count; i++)
            {
                Cell cell = _cells[_drop[i]];

                if (cell == _inFlight)
                {
                    _inFlight = null; // its result is ignored when it comes
                }

                _liveCards -= cell.Cards;
                DestroyPieces(cell);
                _cells.Remove(_drop[i]);
            }

            int x0 = Mathf.FloorToInt((_eye.x - _end) / CellSize), x1 = Mathf.FloorToInt((_eye.x + _end) / CellSize);
            int z0 = Mathf.FloorToInt((_eye.z - _end) / CellSize), z1 = Mathf.FloorToInt((_eye.z + _end) / CellSize);

            for (int x = x0; x <= x1; x++)
            {
                for (int z = z0; z <= z1; z++)
                {
                    long key = KeyOf(x, z);

                    if (_cells.ContainsKey(key) || !ByCell.ContainsKey(key))
                    {
                        continue;
                    }

                    float d = CellDistance(x, z);

                    if (d <= _end)
                    {
                        _cells[key] = new Cell { Key = key, X = x, Z = z, Distance = d };
                    }
                }
            }

            _queue.Clear();

            foreach (Cell cell in _cells.Values)
            {
                if (cell.Dirty && !cell.InFlight)
                {
                    _queue.Add(cell);
                }
            }

            // Nearest last: PostNext takes from the end.
            _queue.Sort(FartherFirst);
        }

        /// <summary>Farther cells first (a comparer object, so sorting allocates nothing).</summary>
        private sealed class FartherFirstComparer : IComparer<Cell>
        {
            public int Compare(Cell a, Cell b)
            {
                return b.Distance.CompareTo(a.Distance);
            }
        }

        private static readonly FartherFirstComparer FartherFirst = new FartherFirstComparer();

        /// <summary>Horizontal distance from the eye to the nearest point of a cell's square.</summary>
        private float CellDistance(int x, int z)
        {
            float minX = x * CellSize, minZ = z * CellSize;
            float dx = Mathf.Max(0f, Mathf.Max(minX - _eye.x, _eye.x - (minX + CellSize)));
            float dz = Mathf.Max(0f, Mathf.Max(minZ - _eye.z, _eye.z - (minZ + CellSize)));
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        /// <summary>The nearest queued cell goes to the worker, once its defs' cards and materials are ready.</summary>
        private void PostNext()
        {
            while (_queue.Count > 0)
            {
                Cell cell = _queue[_queue.Count - 1];

                if (!_cells.TryGetValue(cell.Key, out Cell live) || live != cell || !cell.Dirty || cell.InFlight)
                {
                    _queue.RemoveAt(_queue.Count - 1);
                    continue;
                }

                if (!ByCell.TryGetValue(cell.Key, out List<EtsDetailVegetation.Patch> list) || list.Count == 0)
                {
                    _queue.RemoveAt(_queue.Count - 1);
                    continue;
                }

                // Main thread: the models and materials of every def the cell's patches use (once per def; one def a
                // frame at most, so a first sight of a new def costs one model read, not ten).
                if (!Prepare(list))
                {
                    return;
                }

                // Not before every road and junction that can reach the cell is known from the map (EtsRoadClearance,
                // 2026-10-23): no card stands on a road, whatever has streamed in yet. Asked again next frame until then.
                if (!KeepOutFor(cell, out EtsRoadClearance.SectorShapes[] keepOut, out Vector3 keepOutShift))
                {
                    return;
                }

                _queue.RemoveAt(_queue.Count - 1);
                EtsDetailVegetation.Patch[] snapshot = list.ToArray();
                cell.Dirty = false;
                cell.InFlight = true;
                _inFlight = cell;

                int version = cell.Version;
                int job = ++_jobs;
                cell.Job = job;
                long key = cell.Key;
                float minX = cell.X * CellSize, minZ = cell.Z * CellSize;
                float density = DensityScale;
                int allowed = Mathf.Max(0, Cap - (_liveCards - cell.Cards));
                Vector3 origin = _root != null ? _root.position : Vector3.zero;

                _worker.Post(() =>
                {
                    EtsDetailVegetation.CellResult r = EtsDetailVegetation.BuildCell(key, minX, minZ, CellSize, snapshot, density, 1f, origin, keepOut, keepOutShift);

                    if (r.Cards > allowed)
                    {
                        double first = r.Milliseconds;
                        float share = allowed / (float)r.Cards;
                        r = EtsDetailVegetation.BuildCell(key, minX, minZ, CellSize, snapshot, density, share, origin, keepOut, keepOutShift);
                        r.Milliseconds += first;
                    }

                    r.Version = version;
                    r.Job = job;
                    return r;
                }, Apply);
                return;
            }
        }

        private float _keepOutTickAt = -10f;
        private int _offRoad;

        /// <summary>
        /// The road and junction footprints a cell's cards are tested against (a snapshot the worker may read), and the shift
        /// from world to their U-frame. False while a sector that can reach the cell is not read or footprinted yet; true with
        /// none when there is no map (the old behaviour).
        /// </summary>
        private bool KeepOutFor(Cell cell, out EtsRoadClearance.SectorShapes[] keepOut, out Vector3 shift)
        {
            keepOut = null;
            shift = Vector3.zero;
            EtsMapViewer viewer = EtsMapViewer.Current;
            EtsRoadClearance.Registry clearance = viewer != null ? EtsVegetationBuilder.Clearance(viewer.Map) : null;

            if (clearance == null)
            {
                return true;
            }

            if (Time.unscaledTime - _keepOutTickAt >= 0.25f)
            {
                _keepOutTickAt = Time.unscaledTime;
                clearance.Tick();
            }

            Vector3 c = viewer.CenterGame;
            shift = new Vector3(c.x, c.y, -c.z);
            float minX = cell.X * CellSize + shift.x, minZ = cell.Z * CellSize + shift.z;

            if (!clearance.Ensure(minX, minZ, minX + CellSize, minZ + CellSize))
            {
                return false;
            }

            keepOut = clearance.Snapshot(minX - EtsRoadClearance.MaxMargin, minZ - EtsRoadClearance.MaxMargin, minX + CellSize + EtsRoadClearance.MaxMargin, minZ + CellSize + EtsRoadClearance.MaxMargin);
            return true;
        }

        private bool Prepare(List<EtsDetailVegetation.Patch> patches)
        {
            foreach (EtsDetailVegetation.Patch patch in patches)
            {
                foreach (EtsDetailVegetation.Def def in patch.Defs)
                {
                    if (!def.Ready)
                    {
                        EtsDetailVegetation.Resolve(def);
                        EnsureMaterials();
                        return false; // the next frame goes on
                    }
                }
            }

            EnsureMaterials();
            return true;
        }

        /// <summary>A band-3 copy of each card material: the game's leaves material, faded as detail, no shadow pass.</summary>
        private void EnsureMaterials()
        {
            while (_materials.Count < EtsDetailVegetation.MaterialPaths.Count)
            {
                string path = EtsDetailVegetation.MaterialPaths[_materials.Count];
                Material source = EtsEngine.Materials != null ? EtsEngine.Materials.Load(path) : null;
                Material grass = null;

                if (source != null && EtsMaterialCache.LeavesShader != null && source.shader == EtsMaterialCache.LeavesShader)
                {
                    grass = new Material(source) { name = source.name + " (grass cells)" };
                    grass.SetFloat(BandId, 3f);
                    grass.SetFloat(CastsShadowId, 0f);
                    grass.SetShaderPassEnabled("ShadowCaster", false);
                    grass.renderQueue = (int)RenderQueue.AlphaTest;
                }
                else if (_failures++ < 3)
                {
                    Debug.LogWarning("[SomniumEuroTruck] Grass: " + path + " is not on the leaves shader (" + (source == null ? "no material" : source.shader.name) + "); its cards are not drawn.");
                }

                _materials.Add(grass);
            }
        }

        private static readonly int BandId = Shader.PropertyToID("_Band");
        private static readonly int CastsShadowId = Shader.PropertyToID("_CastsShadow");

        /// <summary>Main thread, from the worker's Drain: a cell's meshes, one per material.</summary>
        private void Apply(object value, Exception error)
        {
            var result = value as EtsDetailVegetation.CellResult;

            if (error != null || result == null)
            {
                if (_failures++ < 3)
                {
                    Debug.LogWarning("[SomniumEuroTruck] Grass: building a cell failed: " + (error != null ? error.GetType().Name + ": " + error.Message : "no result"));
                }

                // Only one job is ever in flight (a dropped cell's may still finish; it failed or not, it is not ours now).
                if (_inFlight != null)
                {
                    _inFlight.InFlight = false;
                    _inFlight = null;
                }

                return;
            }

            if (_inFlight != null && _inFlight.Job == result.Job)
            {
                _inFlight = null;
            }

            // Dropped while it was being built, or dropped and wanted again (a new cell with a new job): not this one's.
            if (!_cells.TryGetValue(result.Key, out Cell cell) || cell.Job != result.Job)
            {
                return;
            }

            cell.InFlight = false;

            if (result.Version != cell.Version)
            {
                cell.Dirty = true; // patches changed while it was built: shown now, built again next tick
            }

            var upload = Stopwatch.StartNew();
            _liveCards -= cell.Cards;
            cell.Cards = 0;

            foreach (SlotPiece piece in cell.Pieces)
            {
                piece.Cards = 0;
            }

            foreach (EtsDetailVegetation.SlotMesh sm in result.Slots)
            {
                Material material = sm.Slot < _materials.Count ? _materials[sm.Slot] : null;

                if (material == null)
                {
                    continue;
                }

                SlotPiece piece = null;

                foreach (SlotPiece p in cell.Pieces)
                {
                    if (p.Slot == sm.Slot)
                    {
                        piece = p;
                        break;
                    }
                }

                if (piece == null)
                {
                    piece = NewPiece(cell, sm.Slot, material);
                    cell.Pieces.Add(piece);
                }

                Mesh mesh = piece.Mesh;
                mesh.Clear();
                mesh.indexFormat = sm.Positions.Length > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
                mesh.SetVertices(sm.Positions);
                mesh.SetNormals(sm.Normals);
                mesh.SetColors(sm.Colors);
                mesh.SetUVs(0, sm.Uv0);
                mesh.SetUVs(1, sm.Uv1);
                mesh.SetUVs(2, sm.Uv2);
                mesh.SetUVs(3, sm.Uv3);
                mesh.SetTriangles(sm.Indices, 0, false);
                mesh.bounds = sm.Bounds;
                piece.Cards = sm.Cards;
                piece.Renderer.enabled = true;
                cell.Cards += sm.Cards;
            }

            foreach (SlotPiece piece in cell.Pieces)
            {
                if (piece.Cards == 0 && piece.Renderer.enabled)
                {
                    piece.Mesh.Clear();
                    piece.Renderer.enabled = false;
                }
            }

            _liveCards += cell.Cards;
            _thinned += result.Thinned;
            _offRoad += result.OffRoad;
            cell.Thinned = result.Thinned > 0;
            _uploads++;

            double ms = upload.Elapsed.TotalMilliseconds;

            if (ms > _slowestUploadMs)
            {
                _slowestUploadMs = ms;
            }

            if (result.Milliseconds > _slowestBuildMs)
            {
                _slowestBuildMs = result.Milliseconds;
            }
        }

        private SlotPiece NewPiece(Cell cell, int slot, Material material)
        {
            var go = new GameObject("grass " + cell.X + "," + cell.Z + " #" + slot);
            go.transform.SetParent(_root, false);
            var mesh = new Mesh { name = go.name };
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = true;
            renderer.lightProbeUsage = LightProbeUsage.Off;
            renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            renderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
            renderer.allowOcclusionWhenDynamic = false;
            return new SlotPiece { Slot = slot, Mesh = mesh, Renderer = renderer };
        }

        private static void DestroyPieces(Cell cell)
        {
            foreach (SlotPiece piece in cell.Pieces)
            {
                if (piece.Mesh != null)
                {
                    Destroy(piece.Mesh);
                }

                if (piece.Renderer != null)
                {
                    Destroy(piece.Renderer.gameObject);
                }
            }

            cell.Pieces.Clear();
        }

        /// <summary>
        /// "[SomniumEuroTruck] Grass: ..." every 30 s while it changes: cards and cells round the eye, the ones within
        /// 60 m (the headset's budget), the defs, the quads left out by the no-detail switches, the cap, and the costs.
        /// </summary>
        private void Report(float now)
        {
            // When the round has settled (nothing queued or building), at most every 30 s and only when the count
            // changed; the first as soon as the first round settles; every 120 s regardless while it never does.
            bool settled = _inFlight == null && _queue.Count == 0;
            bool first = _reportedCards < 0;

            if (_uploads == 0 || _liveCards == _reportedCards || (!settled && now - _reportedAt < 120f) || (settled && !first && now - _reportedAt < 30f))
            {
                return;
            }

            _reportedAt = now;
            _reportedCards = _liveCards;
            int cells = 0, near = 0, pieces = 0;

            foreach (Cell cell in _cells.Values)
            {
                if (cell.Cards > 0)
                {
                    cells++;

                    foreach (SlotPiece p in cell.Pieces)
                    {
                        if (p.Cards > 0) pieces++;
                    }
                }

                if (cell.Distance <= 60f)
                {
                    near += cell.Cards;
                }
            }

            int ready = 0;
            var seen = new HashSet<EtsDetailVegetation.Def>();

            foreach (EtsDetailVegetation.Patch patch in Patches.Values)
            {
                foreach (EtsDetailVegetation.Def def in patch.Defs)
                {
                    if (seen.Add(def) && def.Ready) ready++;
                }
            }

            var inv = System.Globalization.CultureInfo.InvariantCulture;
            Debug.Log("[SomniumEuroTruck] Grass: " + _liveCards + " cards in " + cells + " cells (" + pieces + " meshes) within " + _end.ToString("0", inv) + " m of the eye (" +
                      (UnityEngine.XR.XRSettings.enabled ? "headset" : "desktop") + "), " + near + " in cells within 60 m; " + seen.Count + " detail defs in use (" + ready + " read) of " +
                      EtsDetailVegetation.DefCount + ", " + EtsDetailVegetation.CardModels + " card models, " + EtsDetailVegetation.NonCardModels + " non-card models left out; " + Patches.Count +
                      " sides registered from " + EtsDetailPatch.CapableQuads + " quads that could grow grass: " + EtsDetailPatch.SkippedQuadBit + " skipped by the quad no-detail bit, " +
                      EtsDetailPatch.SkippedSideFlag + " by the side's detail flag, " + EtsDetailPatch.SkippedBand + " outside the side's detail band; density " +
                      EtsDetailVegetation.GlobalDensity.ToString("0", inv) + " x " + DensityScale.ToString("0.00", inv) + " / 100 m2, cap " + Cap + (_thinned > 0 ? " (" + _thinned + " thinned by it)" : "") + ", " + _offRoad + " kept off roads and junctions by the map's footprints" +
                      "; slowest cell " + _slowestBuildMs.ToString("0.0", inv) + " ms on the worker, slowest upload " + _slowestUploadMs.ToString("0.00", inv) + " ms (budget " +
                      UploadBudgetMs.ToString("0.0", inv) + " ms a frame).");
            _slowestBuildMs = 0;
            _slowestUploadMs = 0;
        }
    }
}
