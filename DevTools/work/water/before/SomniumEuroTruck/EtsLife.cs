using System;
using System.Collections.Generic;
using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;
using Random = System.Random;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace SomniumSpace.Worlds.SomniumEuroTruck
{
    /// <summary>
    /// The map's life: its movers (map item type 9, Ets/EtsMovers.cs) - pedestrians on the pavements, cows, sheep,
    /// horses, dogs and chickens in the fields, eagles circling over the country and gulls over the coast, plus the
    /// flags, wind turbines, ships and harvesters the map animates the same way - built from the game's own models
    /// and animations near the player, with nothing invented. What the demo's files hold (living probes 1-9,
    /// scratchpad details/living):
    ///
    ///   - 9,145 movers on the 1.61 map, all decoded (TruckLib's MoverSerializer), 4,666 of them pedestrian groups
    ///     (5,424 people), 1,219 animal items (1,854 animals), 682 bird items (743 flocks and birds), 2,373 others.
    ///     Walkers (type 28) are gone from the map; /def/world has no walker.sii.
    ///   - Near the default start (TransiNet, Szczecin) there is little: one pedestrian path 73-82 m from the yard
    ///     (walker_all, 5 people, 23 nodes, 1 m/s, bouncing), eagles from 1.4 km, cows, horses and a harvester from
    ///     2.1 km, eleven wind turbines from 1.5 km. City centres have hundreds (Leipzig 244 within 3 km, Rostock 257,
    ///     Kiel 255, Dresden 186).
    ///   - The models are skinned (pmg 0x15, four u8 bone indices and weights per vertex: EtsModelSkin) and the
    ///     animations are .pma version 5 with a flag word EtsAnimation does not read (EtsMoverAnim); the walking
    ///     ones carry their root motion in a separate block (man 1.12 m/s, woman 1.27, dog 1.36, cyclist 3.31).
    ///
    /// How it is shown. Each model + look + variant + animation is built once as an inactive TEMPLATE over several
    /// frames (files read and textures decoded on this component's own worker thread, then one material or mesh or a
    /// few bones' curves per step, at most ~1.5 ms a frame): a bone hierarchy, SkinnedMeshRenderers with the file's
    /// bind poses, and a legacy AnimationClip made from the pma keys, played by an Animation component (native
    /// evaluation, culled when not visible - the Somnium client itself builds such clips at runtime through
    /// TriLib). A person, cow or bird near the player is a pooled clone of its template, at most one new clone a
    /// frame. The mover item moves it: along its path of nodes (Hermite when curved) at its speed - or, at speed 0,
    /// the animation's own walking speed - bouncing or restarting at the end, the walk played at the rate that
    /// keeps the feet planted. Per frame the only script work is placing the moving clones and flipping the gulls'
    /// sprite sheets: no allocation.
    ///
    /// Limits for VR: pedestrians within 180 m (the nearest 40), animals within 260 m (40), birds within 1.5 km
    /// (24), other movers within 600 m or 1.5 km for the big ones (30); shadows only within 35 m. The game's own lods
    /// are used at the game's own distances (mover_desc lod_distances: a new-generation worker drops to its lod1 at
    /// 18 m, 6,235 vertices against 14,108 for all its variants, lod2 at 49 m, and is hidden past 160 m), each lod
    /// skinned to the model's own bones (every lod bone has a twin with the same bind matrix, living probe 11), so
    /// one clip drives every level. Everything is guarded: a mover, a model or an animation that fails is logged
    /// once and skipped, and nothing here can stop the map or the truck.
    ///
    /// Verified off the client: the skinning (hierarchy, bind poses, weights, clip curves) on the CPU in the game's
    /// frame against the files (living probe 10: the bind chain reproduces every bind matrix exactly; people stand
    /// 0-1.88 m with their feet at 0, eagles circle, the turbine turns), and the same code inside Unity 6000.3.5f2
    /// in a throwaway project (probe 12: Unity's own skinning through the Animation component gives the same boxes,
    /// z mirrored, frame for frame; a lod1 on the lod0 skeleton poses identically). Not yet seen in the client.
    ///
    /// Started by <see cref="Attach"/> (EtsMapViewer, see the integration request); runs on its own from there,
    /// following EtsMapViewer.Current, waiting for the first build (AnyCompleted) and starting over when the map
    /// is rebuilt round a new centre.
    /// </summary>
    public sealed class EtsLife : MonoBehaviour
    {
        /// <summary>Master switch (a static, not a scene value: the scene overrides serialized defaults).</summary>
        public static bool Enabled = true;

        public const int Pedestrian = 0, Animal = 1, Bird = 2, Other = 3;

        /// <summary>Metres from the player within which each category is shown.</summary>
        public static readonly float[] Radius = { 180f, 260f, 1500f, 600f };

        /// <summary>Wind turbines, ships, trains, balloons: seen from far.</summary>
        public static float BigRadius = 1500f;

        /// <summary>How many of each category may be shown at once (the nearest win).</summary>
        public static readonly int[] Cap = { 40, 40, 24, 30 };

        /// <summary>Instances one mover item may show (its count is capped to this).</summary>
        public static int MaxPerMover = 16;

        /// <summary>Pedestrians and animals cast shadows within this distance only.</summary>
        public static float ShadowDistance = 35f;

        private const float ScanInterval = 0.5f;
        private const float BuildBudgetMs = 1.5f;
        private const float DrainBudgetMs = 1f;
        private const int MaxPicksInFlight = 12;

        public static EtsLife Instance { get; private set; }

        /// <summary>Adds the component to the host's GameObject once (the map viewer calls it). Null when switched off.</summary>
        public static EtsLife Attach(Component host)
        {
            if (!Enabled || host == null)
            {
                return null;
            }

            // One per world: the scene may hold more than one map viewer, and two of these would show everyone twice.
            if (Instance != null)
            {
                return Instance;
            }

            EtsLife life = host.GetComponent<EtsLife>();
            return life != null ? life : host.gameObject.AddComponent<EtsLife>();
        }

        // ------------------------------------------------------------------ state

        private sealed class Mover
        {
            public EtsMapMover Item;
            public EtsMoverKind Kind;
            public int Category;
            public float Radius;
            public Vector3 Min, Max;
            public Quaternion NodeRotation = Quaternion.identity;
            public Vector3 NodeForward = Vector3.forward;
            public Vector3[] Points;
            public float[] Cum;
            public float Length;
            public Slot[] Slots;
            public bool Near;
            public float Distance;
            public bool Moveable;
        }

        private sealed class Slot
        {
            public Mover Owner;
            public int Index, Seed;
            public float S0, Lateral, Along, Yaw;
            public EtsMoverPick Pick;
            public bool Picking, Failed, Wanted, Queued;
            public Template Template;
            public Visual Visual;
            public bool Moving;
            public float Speed, Rate, Travel, Delay, Phase;
            public int Seg;
            public float Distance;
        }

        private sealed class TemplateData
        {
            public EtsModelData Model;
            public EtsModelSkin Skin;
            public EtsMoverAnim Anim;
            public string Failure;

            /// <summary>The game's lods (mover_desc lods / lod_distances): model (null = hide from there, or unreadable), skin, and the distance it starts at.</summary>
            public EtsModelData[] Lods = new EtsModelData[0];
            public EtsModelSkin[] LodSkins = new EtsModelSkin[0];
            public float[] LodFrom = new float[0];
            public bool[] LodHide = new bool[0];
        }

        private sealed class Template
        {
            public string Key, Model, Look, Variant, Anim;
            public int Category;
            public TemplateData Data;
            public GameObject Root;
            public bool Ready, Failed;
            public IEnumerator<bool> Job;
            public int PendingTextures;
            public float NaturalSpeed;
            public string ClipName;

            /// <summary>Level i ("lod&lt;i&gt;" under the model) is drawn from LevelFrom[i] metres; a hidden level draws nothing.</summary>
            public readonly List<float> LevelFrom = new List<float>();
            public readonly List<bool> LevelHidden = new List<bool>();
            public readonly Stack<Visual> Pool = new Stack<Visual>();
            public int Made;
        }

        private sealed class Visual
        {
            public GameObject Root;
            public Transform T;
            public Animation Anim;
            public AnimationState State;
            public Renderer[] Renderers;
            public Renderer[][] Levels;
            public int Level = -2;
            public bool Shadows;
            public Template Template;
        }

        private sealed class FlipSheet
        {
            public Material Material;
            public int TexId;
            public float Fps;
            public int Row, Total;
            public float W, H;
        }

        private sealed class ByDistance : IComparer<Slot>
        {
            public int Compare(Slot a, Slot b) => a.Distance.CompareTo(b.Distance);
        }

        private static readonly ByDistance DistanceOrder = new ByDistance();

        private EtsWorker _worker;
        private EtsMoverCatalog _catalog;
        private bool _catalogAsked;
        private EtsMapViewer _viewer;
        private Vector3 _centre;
        private int _generation;
        private GameObject _root, _templateRoot;
        private Vector3 _focus;
        private bool _haveFocus;

        // Worker-only caches (EtsMoverPick.Choose runs on the worker; nothing on the main thread touches these).
        private readonly Dictionary<string, ulong> _hashes = new Dictionary<string, ulong>(StringComparer.Ordinal);
        private readonly Dictionary<string, float> _speeds = new Dictionary<string, float>(StringComparer.Ordinal);

        private readonly HashSet<long> _sectorsAsked = new HashSet<long>();
        private int _sectorsRead;
        private readonly List<Mover> _movers = new List<Mover>();
        private readonly Dictionary<string, Template> _templates = new Dictionary<string, Template>(StringComparer.Ordinal);
        private readonly List<Template> _buildQueue = new List<Template>();
        private Template _building;
        private readonly Dictionary<string, AnimationClip> _clips = new Dictionary<string, AnimationClip>(StringComparer.Ordinal);
        private readonly List<Slot>[] _candidates = { new List<Slot>(), new List<Slot>(), new List<Slot>(), new List<Slot>() };
        private readonly List<Slot> _shown = new List<Slot>();
        private readonly List<Slot> _lastWanted = new List<Slot>();
        private readonly Queue<Slot> _toShow = new Queue<Slot>();
        private readonly List<FlipSheet> _sheets = new List<FlipSheet>();
        private readonly List<Mesh> _ownedMeshes = new List<Mesh>();
        private readonly HashSet<string> _warned = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _texturesAsked = new HashSet<string>(StringComparer.Ordinal);
        private readonly int[] _shownCount = new int[4];
        private readonly int[] _nearItems = new int[4];
        private int _picksInFlight, _templatesFailed, _roundRobin;
        private float _nextScan, _nextReport;
        private string _lastReport;
        private readonly Stopwatch _clock = new Stopwatch();
        private Func<bool> _drainMore;
        private int _drained;

        // ------------------------------------------------------------------ Unity

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(this);
                return;
            }

            Instance = this;
        }

        private void Update()
        {
            if (!Enabled)
            {
                return;
            }

            try
            {
                Tick();
            }
            catch (Exception e)
            {
                Warn("tick " + e.GetType().Name, "Life: " + e.GetType().Name + ": " + e.Message + " - carrying on.");
            }
        }

        private void OnDestroy()
        {
            try
            {
                _worker?.Dispose();
                _worker = null;
                ClearVisuals();

                foreach (Template t in _templates.Values)
                {
                    if (t.Root != null) Object.Destroy(t.Root);
                }

                _templates.Clear();
                foreach (Mesh m in _ownedMeshes) if (m != null) Object.Destroy(m);
                _ownedMeshes.Clear();
                foreach (FlipSheet s in _sheets) if (s.Material != null) Object.Destroy(s.Material);
                _sheets.Clear();
                foreach (AnimationClip c in _clips.Values) if (c != null) Object.Destroy(c);
                _clips.Clear();
                if (_root != null) Object.Destroy(_root);
                if (_templateRoot != null) Object.Destroy(_templateRoot);
            }
            catch (Exception)
            {
                // Leaving the world: nothing to report to.
            }

            if (Instance == this)
            {
                Instance = null;
            }
        }

        private void Tick()
        {
            EtsMapViewer viewer = EtsMapViewer.Current;

            if (viewer == null || !viewer.IsOpen || !EtsEngine.IsMounted || EtsEngine.Materials == null)
            {
                return;
            }

            if (_worker == null)
            {
                _worker = new EtsWorker("Ets life");
                _drainMore = () => _drained++ == 0 || _clock.Elapsed.TotalMilliseconds < DrainBudgetMs;
            }

            _drained = 0;
            _clock.Restart();
            _worker.Drain(_drainMore);

            if (_catalog == null)
            {
                AskCatalog();
                return;
            }

            if (viewer != _viewer || (viewer.CenterGame - _centre).sqrMagnitude > 1f)
            {
                Reset(viewer);
            }

            if (!EtsMapViewer.AnyCompleted)
            {
                return;
            }

            float now = Time.time;

            if (now >= _nextScan)
            {
                _nextScan = now + ScanInterval;
                Scan();
            }

            BuildStep();
            ShowStep();
            Animate(now);
            Report(now);
        }

        // ------------------------------------------------------------------ setup

        private void AskCatalog()
        {
            if (_catalogAsked)
            {
                return;
            }

            _catalogAsked = true;
            EtsFileSystem files = EtsEngine.Files;
            _worker.Post(() =>
            {
                var watch = Stopwatch.StartNew();
                var catalog = new EtsMoverCatalog(files);
                return new object[] { catalog, watch.ElapsedMilliseconds };
            }, (result, error) =>
            {
                if (result is object[] pair && pair[0] is EtsMoverCatalog catalog)
                {
                    _catalog = catalog;
                    Debug.Log("[SomniumEuroTruck] Life: mover definitions read - " + catalog.MoverUnits + " movers, " + catalog.DescUnits + " models, " +
                              catalog.AnimUnits + " animations (" + pair[1] + " ms, worker thread).");
                }
                else
                {
                    Debug.LogWarning("[SomniumEuroTruck] Life: the mover definitions could not be read (" +
                                     (error != null ? error.GetType().Name + ": " + error.Message : "no reason given") + "); no pedestrians, animals or birds.");
                    enabled = false;
                }
            });
        }

        /// <summary>A new map (or a new centre): every mover and clone goes; the templates and clips stay (they are in model space).</summary>
        private void Reset(EtsMapViewer viewer)
        {
            ClearVisuals();
            _movers.Clear();
            _sectorsAsked.Clear();
            _sectorsRead = 0;
            _toShow.Clear();
            _lastWanted.Clear();
            _viewer = viewer;
            _centre = viewer.CenterGame;
            _generation++;
            _nextScan = 0f;

            if (_root == null)
            {
                _root = new GameObject("Ets Life");
            }

            if (_templateRoot == null)
            {
                _templateRoot = new GameObject("Ets Life templates");
                _templateRoot.SetActive(false);
            }
        }

        private void ClearVisuals()
        {
            foreach (Slot s in _shown)
            {
                if (s.Visual != null && s.Visual.Root != null) Object.Destroy(s.Visual.Root);
                s.Visual = null;
            }

            _shown.Clear();

            foreach (Template t in _templates.Values)
            {
                while (t.Pool.Count > 0)
                {
                    Visual v = t.Pool.Pop();
                    if (v.Root != null) Object.Destroy(v.Root);
                }
            }

            for (int c = 0; c < 4; c++) _shownCount[c] = 0;
        }

        // ------------------------------------------------------------------ what is near

        private Vector3 Focus()
        {
            if (_viewer != null && _viewer.StreamFocusOverride != null)
            {
                return _viewer.StreamFocusOverride.position;
            }

            EtsTruckSeat seat = EtsNet.Instance != null ? EtsNet.Instance.Seat : null;
            Transform focus = seat != null ? seat.StreamFocus : null;

            if (focus != null)
            {
                return focus.position;
            }

            Camera eye = Camera.main;
            return eye != null ? eye.transform.position : Vector3.zero;
        }

        private static long SectorKey(int x, int z) => ((long)x << 32) ^ (uint)z;

        private void Scan()
        {
            _focus = Focus();
            _haveFocus = true;
            float gx = _focus.x + _centre.x, gz = _centre.z - _focus.z;
            float reach = Mathf.Max(BigRadius, Radius[Bird]) + 200f;
            EtsMap map = _viewer.Map;

            if (map == null)
            {
                return;
            }

            // Sectors the map has read (never asked of the map here: it reads what its own stream needs).
            foreach (EtsMapSector sector in map.LoadedSectors)
            {
                long key = SectorKey(sector.SectorX, sector.SectorZ);

                if (_sectorsAsked.Contains(key))
                {
                    continue;
                }

                float x0 = sector.SectorX * EtsMapSector.SectorSize, z0 = sector.SectorZ * EtsMapSector.SectorSize;
                float dx = Mathf.Max(0f, Mathf.Max(x0 - gx, gx - (x0 + EtsMapSector.SectorSize)));
                float dz = Mathf.Max(0f, Mathf.Max(z0 - gz, gz - (z0 + EtsMapSector.SectorSize)));

                if (dx * dx + dz * dz > reach * reach)
                {
                    continue;
                }

                _sectorsAsked.Add(key);
                AskSector(sector);
            }

            for (int c = 0; c < 4; c++)
            {
                _candidates[c].Clear();
                _nearItems[c] = 0;
            }

            // Everything wanted last time is unwanted until this scan says otherwise: shown, queued or waiting on a pick.
            foreach (Slot s in _lastWanted) s.Wanted = false;
            foreach (Slot s in _shown) s.Wanted = false;
            _lastWanted.Clear();

            foreach (Mover m in _movers)
            {
                float dx = Mathf.Max(0f, Mathf.Max(m.Min.x - _focus.x, _focus.x - m.Max.x));
                float dz = Mathf.Max(0f, Mathf.Max(m.Min.z - _focus.z, _focus.z - m.Max.z));
                m.Distance = Mathf.Sqrt(dx * dx + dz * dz);
                bool near = m.Distance < (m.Near ? m.Radius * 1.15f : m.Radius);
                m.Near = near;

                if (!near)
                {
                    continue;
                }

                _nearItems[m.Category]++;

                if (m.Slots == null)
                {
                    try
                    {
                        MakePath(m);
                        MakeSlots(m);
                    }
                    catch (Exception e)
                    {
                        Warn("path " + e.GetType().Name, "Life: mover " + m.Item.Uid.ToString("X") + " (" + m.Item.Model + ") has a path that could not be laid out (" + e.GetType().Name + ": " + e.Message + "); skipped.");
                        m.Slots = new Slot[0];
                    }
                }

                foreach (Slot s in m.Slots)
                {
                    if (s.Failed) continue;
                    s.Distance = s.Moving || m.Length < 1f ? m.Distance : Vector3.Distance(StaticPosition(s), _focus);
                    _candidates[m.Category].Add(s);
                }
            }

            for (int c = 0; c < 4; c++)
            {
                List<Slot> list = _candidates[c];
                list.Sort(DistanceOrder);
                int keep = Mathf.Min(Cap[c], list.Count);

                for (int i = 0; i < keep; i++)
                {
                    Slot s = list[i];
                    s.Wanted = true;
                    _lastWanted.Add(s);

                    if (s.Visual != null || s.Queued)
                    {
                        continue;
                    }

                    if (s.Pick == null)
                    {
                        if (!s.Picking && _picksInFlight < MaxPicksInFlight) AskPick(s);
                        continue;
                    }

                    if (s.Template == null)
                    {
                        s.Template = TemplateFor(s.Pick, s.Owner.Category);
                    }

                    if (s.Template.Failed)
                    {
                        s.Failed = true;
                    }
                    else if (s.Template.Ready)
                    {
                        s.Queued = true;
                        _toShow.Enqueue(s);
                    }
                }
            }

            for (int i = _shown.Count - 1; i >= 0; i--)
            {
                if (!_shown[i].Wanted) Hide(_shown[i], i);
            }
        }

        private void AskSector(EtsMapSector sector)
        {
            EtsFileSystem files = EtsEngine.Files;
            int generation = _generation;
            int sx = sector.SectorX, sz = sector.SectorZ;

            _worker.Post(() =>
            {
                byte[] aux = files.Read(EtsMapSector.FileName(sx, sz, "aux"));
                byte[] baseBytes = files.Read(EtsMapSector.FileName(sx, sz, "base"));
                return EtsMapMover.ReadSector(sector, aux, baseBytes, out _);
            }, (result, error) =>
            {
                if (generation != _generation)
                {
                    return;
                }

                if (!(result is List<EtsMapMover> list))
                {
                    Warn("sector " + sx + "," + sz, "Life: the movers of sector " + sx + "," + sz + " could not be read (" +
                                                   (error != null ? error.GetType().Name + ": " + error.Message : "no reason given") + ").");
                    return;
                }

                _sectorsRead++;

                foreach (EtsMapMover item in list)
                {
                    try
                    {
                        AddMover(item);
                    }
                    catch (Exception e)
                    {
                        Warn("add " + e.GetType().Name, "Life: mover " + item.Uid.ToString("X") + " skipped (" + e.GetType().Name + ": " + e.Message + ").");
                    }
                }
            });
        }

        private void AddMover(EtsMapMover item)
        {
            if (!item.ShownOnClearDay || item.ResolvedNodes == 0)
            {
                return;
            }

            string token = item.Model;
            EtsMoverKind kind = _catalog.Kind(token);

            if (kind == null || kind.Descs.Count == 0)
            {
                Warn("kind " + token, "Life: mover model '" + token + "' is not in /def/world/mover*.sii; its items are skipped.");
                return;
            }

            var m = new Mover { Item = item, Kind = kind, Category = CategoryIndex(kind.Category) };
            m.Radius = m.Category == Other && IsBig(kind) ? BigRadius : Radius[m.Category];
            bool first = true;

            for (int i = 0; i < item.NodePositions.Length; i++)
            {
                if (item.NodePositions[i] == null) continue;
                Vector3 p = EtsMapBuilder.ToWorld(item.NodePositions[i], _centre);

                if (first)
                {
                    m.Min = m.Max = p;
                    m.NodeRotation = EtsModelBuilder.ToUnityRotation(item.NodeRotations[i]);
                    m.NodeForward = m.NodeRotation * Vector3.forward;
                    first = false;
                }
                else
                {
                    m.Min = Vector3.Min(m.Min, p);
                    m.Max = Vector3.Max(m.Max, p);
                }
            }

            _movers.Add(m);
        }

        private static int CategoryIndex(string category)
        {
            switch (category)
            {
                case "pedestrian": return Pedestrian;
                case "animal": return Animal;
                case "bird": return Bird;
                default: return Other;
            }
        }

        private static bool IsBig(EtsMoverKind kind)
        {
            string text = kind.Token + " " + kind.Name;
            foreach (EtsMoverDesc d in kind.Descs) text += " " + d.Model;
            text = text.ToLowerInvariant();

            foreach (string word in new[] { "wind", "ship", "train", "balloon", "barge", "yacht", "boat", "tower", "harvester", "crane", "ferry", "b737", "boe", "plane", "excav", "dredger" })
            {
                if (text.Contains(word)) return true;
            }

            return false;
        }

        // ------------------------------------------------------------------ paths and places

        /// <summary>The mover's path in the world, sampled: straight between nodes, or the map's Hermite curve (node directions scaled by the segment's length, as roads are) when the item says curved.</summary>
        private void MakePath(Mover m)
        {
            EtsMapMover item = m.Item;
            var points = new List<Vector3>();
            var dirs = new List<Vector3>();
            var lengths = new List<float>();

            for (int i = 0; i < item.NodePositions.Length; i++)
            {
                if (item.NodePositions[i] == null) continue;
                points.Add(EtsMapBuilder.ToWorld(item.NodePositions[i], _centre));
                dirs.Add(EtsModelBuilder.ToUnityRotation(item.NodeRotations[i]) * Vector3.forward);
                lengths.Add(i < item.Lengths.Length ? item.Lengths[i] : 0f);
            }

            var path = new List<Vector3> { points[0] };

            for (int i = 0; i + 1 < points.Count; i++)
            {
                Vector3 a = points[i], b = points[i + 1];
                float chord = Vector3.Distance(a, b);
                float length = lengths[i] > 0.01f && lengths[i] < chord * 3f + 5f ? lengths[i] : chord;

                if (item.CurvedPath && length > 0.5f)
                {
                    Vector3 t0 = dirs[i], t1 = dirs[i + 1];
                    if (Vector3.Dot(t0, b - a) < 0f) t0 = -t0;
                    if (Vector3.Dot(t1, b - a) < 0f) t1 = -t1;
                    t0 *= length;
                    t1 *= length;
                    int steps = Mathf.Clamp(Mathf.CeilToInt(length / 2f), 1, 24);

                    for (int k = 1; k <= steps; k++)
                    {
                        path.Add(EtsMapBuilder.Hermite(a, t0, b, t1, k / (float)steps));
                    }
                }
                else
                {
                    path.Add(b);
                }
            }

            m.Points = path.ToArray();
            m.Cum = new float[m.Points.Length];

            for (int i = 1; i < m.Points.Length; i++)
            {
                m.Cum[i] = m.Cum[i - 1] + Vector3.Distance(m.Points[i - 1], m.Points[i]);
            }

            m.Length = m.Cum[m.Cum.Length - 1];
            m.Moveable = m.Length > 1f && !item.PreferStatic;
        }

        private void MakeSlots(Mover m)
        {
            EtsMapMover item = m.Item;
            int count = Mathf.Clamp(item.Count, 1, MaxPerMover);
            int seedBase = (int)(item.Uid ^ (item.Uid >> 32));
            var rng = new Random(seedBase);
            m.Slots = new Slot[count];

            for (int i = 0; i < count; i++)
            {
                var s = new Slot { Owner = m, Index = i, Seed = seedBase + i * 7919 };

                if (m.Length > 0.5f)
                {
                    // Spread along the path: evenly when the item says so, else at random (the game's two modes).
                    s.S0 = item.Uniform || count == 1 ? (count == 1 && !m.Moveable ? 0f : (i + 0.5f) / count * m.Length) : (float)rng.NextDouble() * m.Length;
                    s.Lateral = item.Width > 0f ? ((float)rng.NextDouble() - 0.5f) * item.Width : 0f;
                }
                else if (count > 1)
                {
                    // Several at one node: spread within the item's width (or a few metres) and turned apart, so a
                    // herd is not one cow drawn thirteen times.
                    float spread = item.Width > 0f ? item.Width : 3f;
                    s.Lateral = ((float)rng.NextDouble() - 0.5f) * spread;
                    s.Along = ((float)rng.NextDouble() - 0.5f) * spread;
                    s.Yaw = i == 0 ? 0f : (float)rng.NextDouble() * 360f;
                }

                m.Slots[i] = s;
            }
        }

        private void Evaluate(Mover m, float s, ref int seg, out Vector3 position, out Vector3 direction)
        {
            Vector3[] p = m.Points;

            if (p == null || p.Length < 2)
            {
                position = p != null && p.Length > 0 ? p[0] : m.Min;
                direction = m.NodeForward;
                return;
            }

            s = Mathf.Clamp(s, 0f, m.Length);
            if (seg < 0 || seg > p.Length - 2) seg = 0;
            while (seg < p.Length - 2 && m.Cum[seg + 1] < s) seg++;
            while (seg > 0 && m.Cum[seg] > s) seg--;
            float span = m.Cum[seg + 1] - m.Cum[seg];
            float u = span > 1e-4f ? (s - m.Cum[seg]) / span : 0f;
            position = Vector3.LerpUnclamped(p[seg], p[seg + 1], u);
            direction = p[seg + 1] - p[seg];
            float length = direction.magnitude;
            direction = length > 1e-4f ? direction / length : m.NodeForward;
        }

        private Vector3 StaticPosition(Slot s)
        {
            Evaluate(s.Owner, s.S0, ref s.Seg, out Vector3 position, out Vector3 direction);
            Vector3 flat = new Vector3(direction.x, 0f, direction.z);
            flat = flat.sqrMagnitude > 1e-6f ? flat.normalized : Vector3.forward;
            return position + Vector3.Cross(Vector3.up, flat) * s.Lateral + flat * s.Along;
        }

        private Quaternion Facing(Slot s, Vector3 direction)
        {
            Mover m = s.Owner;
            Quaternion yaw = s.Yaw != 0f ? Quaternion.Euler(0f, s.Yaw, 0f) : Quaternion.identity;

            if (!m.Item.FollowDir || m.Points == null || m.Points.Length < 2)
            {
                return yaw * m.NodeRotation;
            }

            if (m.Category == Pedestrian || m.Category == Animal)
            {
                direction.y = 0f;
            }

            if (direction.sqrMagnitude < 1e-6f)
            {
                return yaw * m.NodeRotation;
            }

            return Quaternion.LookRotation(direction.normalized, Vector3.up);
        }

        // ------------------------------------------------------------------ picks and templates

        private void AskPick(Slot s)
        {
            s.Picking = true;
            _picksInFlight++;
            EtsFileSystem files = EtsEngine.Files;
            EtsMoverKind kind = s.Owner.Kind;
            string look = s.Owner.Item.Look, variant = s.Owner.Item.Variant;
            bool moving = s.Owner.Moveable;
            int seed = s.Seed;
            Dictionary<string, ulong> hashes = _hashes;
            Dictionary<string, float> speeds = _speeds;
            int generation = _generation;

            _worker.Post(() => EtsMoverPick.Choose(files, kind, look, variant, moving, seed, hashes, speeds), (result, error) =>
            {
                _picksInFlight--;
                s.Picking = false;

                if (generation != _generation)
                {
                    return;
                }

                var pick = result as EtsMoverPick;

                if (pick == null || pick.Model == null)
                {
                    s.Failed = true;
                    string why = pick != null ? pick.Failure : error != null ? error.GetType().Name + ": " + error.Message : "no reason given";
                    Warn("pick " + kind.Token, "Life: no model for mover '" + kind.Token + "' (" + why + "); its items are skipped.");
                    return;
                }

                s.Pick = pick;
            });
        }

        private Template TemplateFor(EtsMoverPick pick, int category)
        {
            string key = pick.Model + "|" + pick.Look + "|" + pick.Variant + "|" + pick.Anim;

            if (_templates.TryGetValue(key, out Template t))
            {
                return t;
            }

            t = new Template { Key = key, Model = pick.Model, Look = pick.Look, Variant = pick.Variant, Anim = pick.Anim, Category = category };
            _templates[key] = t;
            EtsFileSystem files = EtsEngine.Files;
            string modelPath = pick.Model, animPath = pick.Anim;
            string[] lodPaths = pick.Lods ?? new string[0];
            float[] lodDistances = pick.LodDistances ?? new float[0];

            _worker.Post(() =>
            {
                var data = new TemplateData();

                try
                {
                    byte[] pmd = files.Read(modelPath), pmg = files.Read(EtsMoverPick.ModelGeometry(modelPath));

                    if (pmd == null || pmg == null)
                    {
                        data.Failure = "model files missing";
                        return data;
                    }

                    data.Model = EtsModelData.Parse(pmd, pmg, modelPath);
                    data.Skin = EtsModelSkin.Read(pmg, data.Model.Bones.Length);

                    // The game's lods: from lod_distances[i] on, lods[i] is drawn ("" = nothing). A lod that cannot be
                    // read is left out (the level before it carries on).
                    int levels = Math.Min(lodPaths.Length, lodDistances.Length);
                    data.Lods = new EtsModelData[levels];
                    data.LodSkins = new EtsModelSkin[levels];
                    data.LodFrom = new float[levels];
                    data.LodHide = new bool[levels];

                    for (int i = 0; i < levels; i++)
                    {
                        data.LodFrom[i] = lodDistances[i];

                        if (string.IsNullOrEmpty(lodPaths[i]))
                        {
                            data.LodHide[i] = true;
                            continue;
                        }

                        try
                        {
                            byte[] lpmd = files.Read(lodPaths[i]), lpmg = files.Read(EtsMoverPick.ModelGeometry(lodPaths[i]));
                            if (lpmd == null || lpmg == null) continue;
                            data.Lods[i] = EtsModelData.Parse(lpmd, lpmg, lodPaths[i]);
                            data.LodSkins[i] = EtsModelSkin.Read(lpmg, data.Lods[i].Bones.Length);
                        }
                        catch (Exception)
                        {
                            data.Lods[i] = null;
                        }
                    }

                    if (!string.IsNullOrEmpty(animPath))
                    {
                        EtsMoverAnim anim = EtsMoverAnim.Parse(files.Read(animPath), animPath);

                        // A clip for another skeleton would twist the model: the pick matched the hashes, this checks again.
                        data.Anim = anim.SkeletonHash == data.Model.SkeletonHash || data.Model.SkeletonHash == 0 ? anim : null;
                    }
                }
                catch (Exception e)
                {
                    data.Failure = e.GetType().Name + ": " + e.Message;
                }

                return data;
            }, (result, error) =>
            {
                var data = result as TemplateData;

                if (data == null || data.Failure != null || data.Model == null)
                {
                    Fail(t, data != null && data.Failure != null ? data.Failure : error != null ? error.GetType().Name + ": " + error.Message : "no reason given");
                    return;
                }

                t.Data = data;
                _buildQueue.Add(t);
            });

            return t;
        }

        private void Fail(Template t, string why)
        {
            t.Failed = true;
            t.Job = null;
            t.Data = null;
            _templatesFailed++;
            Warn("template " + t.Model, "Life: could not build " + t.Model + (t.Anim != null ? " with " + t.Anim : "") + " (" + why + "); skipped.");

            if (t.Root != null)
            {
                Object.Destroy(t.Root);
                t.Root = null;
            }
        }

        private void BuildStep()
        {
            _clock.Restart();

            while (_clock.Elapsed.TotalMilliseconds < BuildBudgetMs)
            {
                if (_building == null)
                {
                    if (_buildQueue.Count == 0)
                    {
                        return;
                    }

                    _building = _buildQueue[0];
                    _buildQueue.RemoveAt(0);
                    _building.Job = BuildTemplate(_building);
                }

                Template t = _building;
                bool more;

                try
                {
                    more = t.Job != null && t.Job.MoveNext();
                }
                catch (Exception e)
                {
                    Fail(t, e.GetType().Name + ": " + e.Message);
                    more = false;
                }

                if (!more)
                {
                    t.Job = null;
                    _building = null;
                }
                else if (t.PendingTextures > 0)
                {
                    // Waiting on the worker's textures: nothing more to do this frame.
                    return;
                }
            }
        }

        /// <summary>
        /// One template, a step at a time: textures (decoded on the worker), the bone hierarchy with the file's bind
        /// pose, one mesh per step (skinned copies of EtsModelBuilder's shared meshes, with the pmg's weights and bind
        /// poses), then the animation's curves a few bones per step.
        /// </summary>
        private IEnumerator<bool> BuildTemplate(Template t)
        {
            TemplateData data = t.Data;
            EtsModelData model = data.Model;
            EtsMaterialCache materials = EtsEngine.Materials;

            if (materials == null)
            {
                throw new InvalidOperationException("the material cache is gone");
            }

            List<int> pieces = PiecesOf(model, t.Look, t.Variant, out string[] mats);
            AskTexturesOf(t, model, pieces, mats, materials);

            for (int i = 0; i < data.Lods.Length; i++)
            {
                if (data.Lods[i] == null) continue;
                List<int> lodPieces = PiecesOf(data.Lods[i], t.Look, t.Variant, out string[] lodMats);
                AskTexturesOf(t, data.Lods[i], lodPieces, lodMats, materials);
            }

            float until = Time.realtimeSinceStartup + 10f;

            while (t.PendingTextures > 0 && Time.realtimeSinceStartup < until)
            {
                yield return true;
            }

            string shortName = t.Model.Substring(t.Model.LastIndexOf('/') + 1);
            var root = new GameObject("life " + shortName);
            root.transform.SetParent(_templateRoot.transform, false);
            t.Root = root;
            var body = new GameObject("model");
            body.transform.SetParent(root.transform, false);

            int boneCount = model.Bones.Length;
            Transform[] bones = EtsLifeSkin.BuildSkeleton(model, body.transform, out string[] paths, out Matrix4x4[] bindposes);

            yield return true;

            // Level 0: the model itself, from 0 m.
            Transform level0 = Level(body.transform, 0);
            t.LevelFrom.Add(0f);
            t.LevelHidden.Add(false);

            foreach (int p in pieces)
            {
                BuildPiece(t, model, data.Skin, p, mats, bones, bindposes, body.transform, level0, materials);
                yield return true;
            }

            // The game's lods. Every bone of a lod skeleton has a twin in the model's own with the same bind matrix
            // (168 of the 169 lods of the busiest movers, living probe 11), so the lod is skinned to those bones and
            // the one animation drives every level. A lod without twins for all its bones is left out.
            for (int i = 0; i < data.Lods.Length && boneCount > 0; i++)
            {
                if (data.LodHide[i])
                {
                    t.LevelFrom.Add(data.LodFrom[i]);
                    t.LevelHidden.Add(true);
                    break;
                }

                EtsModelData lod = data.Lods[i];
                if (lod == null) continue;
                Transform[] lodBones = EtsLifeSkin.MapBones(model, lod, bones);
                if (lodBones == null) continue;
                var lodBind = new Matrix4x4[lod.Bones.Length];
                for (int b = 0; b < lod.Bones.Length; b++) lodBind[b] = EtsLifeSkin.BindPose(lod.Bones[b].World);
                int levelIndex = t.LevelFrom.Count;
                Transform levelObject = Level(body.transform, levelIndex);
                t.LevelFrom.Add(data.LodFrom[i]);
                t.LevelHidden.Add(false);
                List<int> lodPieces = PiecesOf(lod, t.Look, t.Variant, out string[] lodMats);

                foreach (int p in lodPieces)
                {
                    BuildPiece(t, lod, data.LodSkins[i], p, lodMats, lodBones, lodBind, body.transform, levelObject, materials);
                    yield return true;
                }
            }

            if (data.Anim != null && boneCount > 0)
            {
                EtsMoverAnim anim = data.Anim;
                string clipKey = anim.Path + "|" + model.SkeletonHash.ToString("X") + "|" + boneCount;

                if (!_clips.TryGetValue(clipKey, out AnimationClip clip) || clip == null)
                {
                    clip = new AnimationClip { name = "life " + anim.Path.Substring(anim.Path.LastIndexOf('/') + 1), legacy = true, wrapMode = WrapMode.Loop };
                    int step = EtsLifeSkin.KeyStep(anim);

                    for (int k = 0; k < anim.BoneCount; k++)
                    {
                        int bone = anim.BoneIndices[k];
                        if (bone < 0 || bone >= boneCount) continue;
                        EtsLifeSkin.AddCurves(clip, anim, k, paths[bone], step);
                        if (k % 6 == 5) yield return true;
                    }

                    _clips[clipKey] = clip;
                }

                var animation = body.AddComponent<Animation>();
                animation.playAutomatically = false;
                animation.AddClip(clip, clip.name);
                animation.clip = clip;
                animation.wrapMode = WrapMode.Loop;
                animation.cullingType = AnimationCullingType.BasedOnRenderers;
                t.ClipName = clip.name;
                t.NaturalSpeed = anim.NaturalSpeed;
            }

            t.Data = null;
            t.Ready = true;
        }

        /// <summary>The pieces a look and variant draw (the "coll" part never), and the look's material paths.</summary>
        private static List<int> PiecesOf(EtsModelData model, string lookName, string variantName, out string[] mats)
        {
            int look = Mathf.Max(0, model.LookIndex(string.IsNullOrEmpty(lookName) ? "default" : lookName));
            int variant = model.VariantIndex(variantName ?? string.Empty);
            if (variant < 0) variant = model.VariantIndex("default");
            if (variant < 0 && model.Variants.Length > 0) variant = 0;
            mats = look < model.LookMaterials.Length ? model.LookMaterials[look] : new string[0];
            var pieces = new List<int>();

            foreach (EtsModelPart part in model.Parts)
            {
                if (part.Name == "coll" || !part.IsVisible(variant)) continue;
                for (int p = part.PieceStart; p < part.PieceStart + part.PieceCount && p < model.Pieces.Length; p++) pieces.Add(p);
            }

            if (model.Parts.Length == 0)
            {
                for (int p = 0; p < model.Pieces.Length; p++) pieces.Add(p);
            }

            return pieces;
        }

        private void AskTexturesOf(Template t, EtsModelData model, List<int> pieces, string[] mats, EtsMaterialCache materials)
        {
            foreach (int p in pieces)
            {
                int mi = model.Pieces[p].MaterialIndex;
                EtsMaterialDef def = mi < mats.Length ? materials.GetDefinition(mats[mi]) : null;
                if (def != null) AskTextures(t, def);
            }
        }

        private static Transform Level(Transform body, int index)
        {
            var level = new GameObject("lod" + index).transform;
            level.SetParent(body, false);
            return level;
        }


        private void BuildPiece(Template t, EtsModelData model, EtsModelSkin skin, int p, string[] mats, Transform[] bones, Matrix4x4[] bindposes, Transform body, Transform parentObject, EtsMaterialCache materials)
        {
            EtsModelPiece piece = model.Pieces[p];
            string matPath = piece.MaterialIndex < mats.Length ? mats[piece.MaterialIndex] : null;
            EtsMaterialDef def = materials.GetDefinition(matPath);
            Material material = materials.Load(matPath);

            // Null is the game's "do not draw" (shadow-only pieces and the like), as in EtsModelBuilder.
            if (material == null || piece.VertexCount <= 0 || piece.Indices == null || piece.Indices.Length < 3)
            {
                return;
            }

            if (piece.Colors != null)
            {
                material = materials.WithVertexColour(material, MeanColour(piece));
            }

            material = SheetFor(def, material, piece);
            Mesh shared = EtsModelBuilder.BuildMesh(piece, "life piece " + p, EtsMaterialCache.BaseUvChannel(def, piece.UvChannels));
            var go = new GameObject("piece " + p);
            go.transform.SetParent(parentObject, false);
            byte[] indices = skin != null && p < skin.Indices.Length ? skin.Indices[p] : null;
            byte[] weights = skin != null && p < skin.Weights.Length ? skin.Weights[p] : null;
            Renderer renderer;

            if (indices != null && weights != null && bones.Length > 0 && indices.Length >= piece.VertexCount * 4)
            {
                Mesh mesh = Object.Instantiate(shared);
                mesh.name = "life skin " + p;
                _ownedMeshes.Add(mesh);
                mesh.boneWeights = EtsLifeSkin.Weights(indices, weights, piece.VertexCount);
                mesh.bindposes = bindposes;
                var smr = go.AddComponent<SkinnedMeshRenderer>();
                smr.sharedMesh = mesh;
                smr.sharedMaterial = material;
                smr.bones = bones;
                smr.rootBone = body;
                smr.updateWhenOffscreen = false;
                Bounds bounds = mesh.bounds;
                bounds.Expand(t.Category == Bird ? 40f : t.Category == Other ? 4f : 1.5f);
                smr.localBounds = bounds;
                renderer = smr;
            }
            else
            {
                go.AddComponent<MeshFilter>().sharedMesh = shared;
                renderer = go.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = material;
            }

            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = true;
        }


        private static Color MeanColour(EtsModelPiece piece)
        {
            double r = 0, g = 0, b = 0;
            int n = Mathf.Min(piece.VertexCount, piece.Colors.Length / 4);

            for (int v = 0; v < n; v++)
            {
                r += piece.Colors[v * 4];
                g += piece.Colors[v * 4 + 1];
                b += piece.Colors[v * 4 + 2];
            }

            return n == 0 ? Color.white : new Color((float)(r / n / 128.0), (float)(g / n / 128.0), (float)(b / n / 128.0), 1f);
        }

        /// <summary>
        /// The gulls' and ducks' sprite sheets (effects "anim.flipsheet" / "anim.fadesheet": animsheet_cfg_fps,
        /// _frames_row, _frames_total, animsheet_frame_width/_height - the herring gull: 16 frames of 0.25 x 0.25 at
        /// 8 fps). The material is copied for the template and its offset stepped through the frames each frame.
        /// </summary>
        private Material SheetFor(EtsMaterialDef def, Material material, EtsModelPiece piece)
        {
            if (def == null || !(def.EffectHas("flipsheet") || def.EffectHas("fadesheet")))
            {
                return material;
            }

            int row = Mathf.Max(1, Mathf.RoundToInt(def.GetFloat("animsheet_cfg_frames_row", 1f)));
            int total = Mathf.Max(1, Mathf.RoundToInt(def.GetFloat("animsheet_cfg_frames_total", 1f)));
            float w = def.GetFloat("animsheet_frame_width", 1f / row), h = def.GetFloat("animsheet_frame_height", w);

            if (total <= 1 || w <= 0f || h <= 0f)
            {
                return material;
            }

            string property = material.HasProperty("_BaseMap") ? "_BaseMap" : "_MainTex";

            if (!material.HasProperty(property))
            {
                return material;
            }

            var copy = new Material(material) { name = material.name + " (sheet)" };

            // The mesh's uvs are laid out on the sheet already and the frame is an offset added to them, as the game's
            // shader does: a single gull's card spans one frame (0..0.25), the gull group's cards several rows (v to
            // 0.73, living probe 15) - both only shifted. Only a card covering the whole sheet is scaled to one frame.
            float maxU = 0f, maxV = 0f;

            if (piece.Uv0 != null)
            {
                for (int i = 0; i + 1 < piece.Uv0.Length; i += 2)
                {
                    maxU = Mathf.Max(maxU, Mathf.Abs(piece.Uv0[i]));
                    maxV = Mathf.Max(maxV, Mathf.Abs(piece.Uv0[i + 1]));
                }
            }

            int id = Shader.PropertyToID(property);
            copy.SetTextureScale(id, maxU > 0.9f && maxV > 0.9f ? new Vector2(w, h) : Vector2.one);
            _sheets.Add(new FlipSheet { Material = copy, TexId = id, Fps = Mathf.Max(0.1f, def.GetFloat("animsheet_cfg_fps", 8f)), Row = row, Total = total, W = w, H = h });
            return copy;
        }

        // Textures the materials will want, decoded on the worker first (the traffic's own rule set, EtsTrafficModels.AskTextures).
        private void AskTextures(Template t, EtsMaterialDef def)
        {
            string effect = def.Effect ?? string.Empty;

            if (effect.Length == 0 || def.EffectHas("shadowonly") || effect.StartsWith("eut2.none", StringComparison.Ordinal) ||
                def.EffectHas("shadowmap") || def.EffectHas("mlaaweight") || def.EffectHas(".night") || def.EffectHas("fakeshadow") ||
                def.EffectHas("decalshadow") || def.EffectHas("water"))
            {
                return;
            }

            bool glass = def.EffectHas("glass") || def.EffectHas("window");
            bool overlay = !glass && def.EffectHas("decal.over");
            bool cutout = !glass && !overlay && (def.EffectHas(".a.") || effect.EndsWith(".a", StringComparison.Ordinal) ||
                                                 def.EffectHas(".a_") || def.EffectHas("alpha") || def.EffectHas("leaves") ||
                                                 def.EffectHas("decal") || def.EffectHas("fence") || def.EffectHas("grass") ||
                                                 def.EffectHas("flare"));
            bool masked = !glass && !overlay && !cutout && !def.EffectHas("lamp") && (def.EffectHas(".env") || def.EffectHas("nofresnel"));

            AskTexture(t, def.BaseTexture, false, masked);
            string second = EtsMaterialCache.SecondLayer(def, out _);
            if (second != null && !def.EffectHas("decal.over")) AskTexture(t, second, false, false);
            if (def.NormalTexture != null && !def.EffectHas("tsnmapuv")) AskTexture(t, def.NormalTexture, true, false);
        }

        private void AskTexture(Template t, string tobjPath, bool linear, bool withMask)
        {
            EtsTextureCache textures = EtsEngine.Textures;

            if (string.IsNullOrEmpty(tobjPath) || textures == null)
            {
                return;
            }

            string key = tobjPath + (linear ? "|linear" : "") + (withMask ? "|mask" : "");

            if ((!withMask && textures.IsLoaded(tobjPath, linear)) || !_texturesAsked.Add(key))
            {
                return;
            }

            EtsFileSystem files = EtsEngine.Files;
            t.PendingTextures++;

            _worker.Post(() => EtsTextureCache.Prepare(files, tobjPath, linear, withMask), (result, error) =>
            {
                t.PendingTextures--;

                try
                {
                    if (result is EtsTextureCache.Prepared prepared && EtsEngine.IsMounted && EtsEngine.Textures != null)
                    {
                        EtsEngine.Textures.Adopt(prepared);
                    }
                }
                catch (Exception e)
                {
                    Warn("texture " + tobjPath, "Life: texture " + tobjPath + " could not be adopted (" + e.GetType().Name + ": " + e.Message + "); the material reads it itself.");
                }
            });
        }

        // ------------------------------------------------------------------ showing

        private void ShowStep()
        {
            int shown = 0, made = 0;

            while (_toShow.Count > 0 && shown < 2)
            {
                Slot s = _toShow.Peek();

                if (!s.Wanted || s.Visual != null || s.Template == null || !s.Template.Ready || s.Template.Root == null)
                {
                    _toShow.Dequeue();
                    s.Queued = false;
                    continue;
                }

                if (s.Template.Pool.Count == 0 && made >= 1)
                {
                    return;
                }

                _toShow.Dequeue();
                s.Queued = false;

                try
                {
                    Visual v = TakeVisual(s.Template, ref made);
                    if (v == null) continue;
                    Show(s, v);
                    shown++;
                }
                catch (Exception e)
                {
                    s.Failed = true;
                    Warn("show " + s.Template.Model, "Life: could not show " + s.Template.Model + " (" + e.GetType().Name + ": " + e.Message + "); skipped.");
                }
            }
        }

        private Visual TakeVisual(Template t, ref int made)
        {
            while (t.Pool.Count > 0)
            {
                Visual pooled = t.Pool.Pop();
                if (pooled.Root != null) return pooled;
            }

            made++;
            GameObject clone = Object.Instantiate(t.Root, _root.transform, false);
            clone.name = t.Root.name;
            t.Made++;
            var v = new Visual { Root = clone, T = clone.transform, Template = t };
            v.Anim = clone.GetComponentInChildren<Animation>(true);
            v.Renderers = clone.GetComponentsInChildren<Renderer>(true);
            Transform body = clone.transform.Find("model");
            v.Levels = new Renderer[t.LevelFrom.Count][];

            for (int i = 0; i < v.Levels.Length; i++)
            {
                Transform level = body != null ? body.Find("lod" + i) : null;
                v.Levels[i] = level != null ? level.GetComponentsInChildren<Renderer>(true) : new Renderer[0];
            }

            return v;
        }

        private void Show(Slot s, Visual v)
        {
            Template t = s.Template;
            Mover m = s.Owner;
            float natural = t.NaturalSpeed;

            // Speed: the item's own, or at 0 the animation's walking speed; nothing moves on a static item or a
            // path shorter than a metre. The walk is played at the rate that keeps its feet on the ground.
            float speed = m.Moveable ? (m.Item.Speed > 0.01f ? m.Item.Speed : natural) : 0f;
            s.Moving = speed > 0.05f && m.Length > 1f;
            s.Speed = speed;
            s.Rate = s.Moving && natural > 0.05f ? Mathf.Clamp(speed / natural, 0.4f, 2.5f) : 1f;

            // One that stands, alone on a path: at its first node, where the mapper put it (a sitting woman on a two-node
            // path is on her bench at node 0, facing node 1).
            if (!s.Moving && m.Slots != null && m.Slots.Length == 1)
            {
                s.S0 = 0f;
            }

            if (s.Moving)
            {
                s.Travel = m.Length / speed;
                s.Delay = Mathf.Clamp(m.Item.EndDelay, 0f, 600f);
                s.Phase = s.S0 / speed;
            }

            v.Root.SetActive(true);
            s.Visual = v;
            _shown.Add(s);
            _shownCount[m.Category]++;

            if (s.Moving)
            {
                Place(s, Time.time);
            }
            else
            {
                Evaluate(m, s.S0, ref s.Seg, out Vector3 _, out Vector3 direction);
                v.T.SetPositionAndRotation(StaticPosition(s), Facing(s, direction));
            }

            if (v.Anim != null && t.ClipName != null)
            {
                v.Anim.Play(t.ClipName);
                v.State = v.Anim[t.ClipName];

                if (v.State != null)
                {
                    var rng = new Random(s.Seed);
                    v.State.time = (float)rng.NextDouble() * Mathf.Max(0.01f, v.State.length);
                    v.State.speed = s.Rate;
                }
            }

            float distance = Vector3.Distance(v.T.position, _focus);
            SetLevel(v, distance);
            SetShadows(v, m.Category, distance < ShadowDistance);
        }

        private void Hide(Slot s, int index)
        {
            Visual v = s.Visual;
            s.Visual = null;
            int last = _shown.Count - 1;
            _shown[index] = _shown[last];
            _shown.RemoveAt(last);
            _shownCount[s.Owner.Category]--;

            if (v != null && v.Root != null)
            {
                v.Root.SetActive(false);
                v.Template.Pool.Push(v);
            }
        }

        /// <summary>The game's lod for a distance: the last level whose start is passed; a hidden level (the game's "" lod) draws nothing, and the animation then stops too (culled with its renderers).</summary>
        private static void SetLevel(Visual v, float distance)
        {
            Template t = v.Template;

            if (v.Levels == null || t.LevelFrom.Count == 0)
            {
                return;
            }

            int level = 0;

            for (int i = 1; i < t.LevelFrom.Count; i++)
            {
                if (distance >= t.LevelFrom[i]) level = i;
            }

            if (level == v.Level)
            {
                return;
            }

            v.Level = level;

            for (int i = 0; i < v.Levels.Length; i++)
            {
                bool on = i == level && !t.LevelHidden[i];
                Renderer[] renderers = v.Levels[i];

                for (int r = 0; r < renderers.Length; r++)
                {
                    if (renderers[r] != null) renderers[r].enabled = on;
                }
            }
        }

        private static void SetShadows(Visual v, int category, bool on)
        {
            on &= category == Pedestrian || category == Animal || category == Other;

            if (v.Shadows == on || v.Renderers == null)
            {
                return;
            }

            v.Shadows = on;

            foreach (Renderer r in v.Renderers)
            {
                if (r != null) r.shadowCastingMode = on ? ShadowCastingMode.On : ShadowCastingMode.Off;
            }
        }

        /// <summary>Where a moving one is at a time: out along the path, a wait at the end, then back (bounce) or from the start again.</summary>
        private void Place(Slot s, float now)
        {
            Mover m = s.Owner;
            float time = now + s.Phase;
            float travel = s.Travel, delay = s.Delay, along;
            bool back = false;

            if (m.Item.Bounce)
            {
                float cycle = 2f * travel + 2f * delay;
                float u = Mathf.Repeat(time, cycle);

                if (u < travel) along = u * s.Speed;
                else if (u < travel + delay) along = m.Length;
                else if (u < 2f * travel + delay) { along = m.Length - (u - travel - delay) * s.Speed; back = true; }
                else { along = 0f; back = true; }
            }
            else
            {
                float u = Mathf.Repeat(time, travel + delay);
                along = Mathf.Min(u * s.Speed, m.Length);
            }

            Evaluate(m, along, ref s.Seg, out Vector3 position, out Vector3 direction);

            if (back && !m.Item.KeepOrientationOnBounce)
            {
                direction = -direction;
            }

            Vector3 flat = new Vector3(direction.x, 0f, direction.z);
            flat = flat.sqrMagnitude > 1e-6f ? flat.normalized : Vector3.forward;
            s.Visual.T.SetPositionAndRotation(position + Vector3.Cross(Vector3.up, flat) * (back ? -s.Lateral : s.Lateral), Facing(s, direction));
        }

        private void Animate(float now)
        {
            for (int i = 0; i < _shown.Count; i++)
            {
                Slot s = _shown[i];

                if (s.Moving && s.Visual != null)
                {
                    Place(s, now);
                }
            }

            for (int i = 0; i < _sheets.Count; i++)
            {
                FlipSheet sheet = _sheets[i];
                if (sheet.Material == null) continue;
                int frame = (int)(now * sheet.Fps) % sheet.Total;
                sheet.Material.SetTextureOffset(sheet.TexId, new Vector2(frame % sheet.Row * sheet.W, frame / sheet.Row * sheet.H));
            }

            // Shadows by distance, four clones a frame.
            if (_haveFocus && _shown.Count > 0)
            {
                for (int k = 0; k < 4; k++)
                {
                    _roundRobin = (_roundRobin + 1) % _shown.Count;
                    Slot s = _shown[_roundRobin];
                    if (s.Visual == null) continue;
                    float distance = Vector3.Distance(s.Visual.T.position, _focus);
                    SetLevel(s.Visual, distance);
                    SetShadows(s.Visual, s.Owner.Category, distance < ShadowDistance);
                }
            }
        }

        // ------------------------------------------------------------------ the log

        private void Report(float now)
        {
            if (now < _nextReport)
            {
                return;
            }

            _nextReport = now + 10f;
            int ready = 0, building = _buildQueue.Count + (_building != null ? 1 : 0);
            foreach (Template t in _templates.Values) if (t.Ready) ready++;
            int known = _movers.Count;

            string text = "[SomniumEuroTruck] Life: " + _shownCount[Pedestrian] + " pedestrians, " + _shownCount[Animal] + " animals, " + _shownCount[Bird] + " birds (+" +
                          _shownCount[Other] + " other movers: flags, turbines, boats, machines) shown; mover items in reach " +
                          _nearItems[Pedestrian] + " pedestrian / " + _nearItems[Animal] + " animal / " + _nearItems[Bird] + " bird / " + _nearItems[Other] + " other (within " +
                          Radius[Pedestrian].ToString("0") + " / " + Radius[Animal].ToString("0") + " / " + Radius[Bird].ToString("0") + " / " + Radius[Other].ToString("0") + "-" +
                          BigRadius.ToString("0") + " m); " + known + " movers read from " + _sectorsRead + " sectors; models " + ready + " ready, " + building + " building, " +
                          _templatesFailed + " failed; clips " + _clips.Count + ".";

            if (text != _lastReport)
            {
                _lastReport = text;
                Debug.Log(text);
            }
        }

        private bool Warn(string key, string message)
        {
            if (!_warned.Add(key))
            {
                return false;
            }

            Debug.LogWarning("[SomniumEuroTruck] " + message);
            return true;
        }

        /// <summary>One line for other components' reports: what is shown now.</summary>
        public static string Describe()
        {
            EtsLife life = Instance;
            return life == null ? "Life: off" : "Life: " + life._shownCount[Pedestrian] + " pedestrians, " + life._shownCount[Animal] + " animals, " + life._shownCount[Bird] + " birds, " + life._shownCount[Other] + " other movers";
        }
    }
}
