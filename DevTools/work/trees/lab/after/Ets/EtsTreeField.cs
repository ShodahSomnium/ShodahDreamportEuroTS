using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Debug = UnityEngine.Debug;

namespace SomniumSpace.Worlds.SomniumEuroTruck.Ets
{
    /// <summary>
    /// The trees EtsVegetationBuilder plants along roads and terrain, drawn with GPU instancing and no GameObject per
    /// tree (trees owner, 2026-09-25; research N/wf1/vegetation.md findings 4-7, environment DESIGN Phase B).
    ///
    /// <b>Why.</b> The map asks for about 480 trees per hectare of band (u16 / 10 on 70+ of 82 bands round Dover): 15,721
    /// within 1.2 km of the Dover HQ and 56,038 within the 2.15 km the land streams to (probe N/veg/v2_dover.txt). One
    /// GameObject, a LODGroup and two renderers per tree held the world to a fifth of that (Thinning 0.2, 60 a band), so
    /// forest belts came out as lines of single lollipop trees with sky between them (lab before_e_trees.png).
    ///
    /// <b>How it is drawn.</b> Each planted road or terrain side is a <see cref="Stand"/> (its trees' matrices, species
    /// and the band's high_poly_distance). Stands are grouped by the far cell (<see cref="FarCellSize"/>) their middle
    /// lies in; each far cell keeps one matrix list per species for the SPRITES, kept in a GPU buffer and drawn every
    /// frame by one Graphics.RenderMeshPrimitives call with the cell's bounds (EtsLeaves' _ETS_TREE_BUFFER variant; see
    /// MatricesId for why not Unity's instancing variants), so Unity culls each call per camera (the headset, every
    /// mirror, every shadow cascade) natively and allocation-free. Every <see cref="RefreshSeconds"/>
    /// the trees within the near reach of the eye are sorted into near cells (<see cref="NearCellSize"/>) by LOD, again
    /// one list per species and slot: the hi_poly_model within the band's high_poly_distance (50 m on every band), the
    /// med_poly_model beyond, casting sun shadows only within the pipeline's shadow distance (the user's client 124 m,
    /// 4 cascades). The sprite never casts, as in the game.
    ///
    /// <b>LOD, the game's way.</b> /def/game_data.sii leaves_lod_start/end (240, 300) m "first lod class for trees" (tree
    /// .mat lod_selector {1, 0, 0}): the game's vertex program drops a near model's fade-out vertices past 300 m and its
    /// fade-in vertices (its own sprite, the sprite model's 8 vertices; probe N/trees/probe/t2_*.txt) before 240 m, with
    /// an ordered dither in between. Here the near models draw on EtsLeaves band 1 and the sprites on band 2 with
    /// EtsVegetationLook.Fade = 270 +- 30 m, the shader's complementary per-pixel dither: the same cross-fade.
    ///
    /// <b>Multiplayer.</b> Nothing here is random: the placement is EtsVegetationBuilder's, seeded by the map item and
    /// drawn in a fixed order, so every client grows the same trees.
    ///
    /// <b>Cost</b> (measured in the render lab, see EtsVegetationBuilder.DensityScale): the submission is one
    /// RenderMeshInstanced call per species and far cell plus one per species, slot and near cell, measured and logged
    /// (Report). The refresh walks only the stands within the near reach.
    /// </summary>
    public static class EtsTreeField
    {
        /// <summary>Instances per RenderMeshInstanced call at most.</summary>
        public const int MaxBatch = 1023;

        /// <summary>The trees this field holds at most (a safety cap far above any 2.15 km disc of the demo's map).</summary>
        public static int MaxTrees = 250000;

        /// <summary>Drawing on (a switch for the render lab's A/B timing; the trees stay planted).</summary>
        public static bool Enabled = true;

        /// <summary>The far cells' sprites drawn (a render-lab switch for timing them apart).</summary>
        public static bool DrawFar = true;

        /// <summary>The near models drawn (a render-lab switch for timing them apart).</summary>
        public static bool DrawNear = true;

        /// <summary>Side of the square cells the sprites are grouped by, metres (512 gave 296-408 calls a frame round Dover).</summary>
        public static float FarCellSize = 1024f;

        /// <summary>Side of the square cells the near models are grouped by, metres.</summary>
        public static float NearCellSize = 64f;

        /// <summary>Far cells whose trees all lie beyond this from the eye are not drawn, metres (the land streams to 2150 m).</summary>
        public static float DrawRadius = 2250f;

        /// <summary>How often the near models are sorted again round the eye, seconds.</summary>
        public static float RefreshSeconds = 0.25f;

        /// <summary>
        /// Past the end of the near band (Fade.x + Fade.y) a near model is still listed this far, metres: the eye moves
        /// between refreshes (33 m/s at 120 km/h is 8 m in 0.25 s), and the shader collapses it there anyway.
        /// </summary>
        public static float NearMargin = 20f;

        /// <summary>Near models cast shadows within the pipeline's shadow distance plus this, metres.</summary>
        public static float ShadowMargin = 15f;

        /// <summary>A tree swaps between hi- and med-poly this far either side of the band's high_poly_distance, metres.</summary>
        public static float HighPolyHysteresis = 2f;

        /// <summary>The layer the trees draw on (the planted GameObjects were on Default).</summary>
        public static int Layer = 0;

        // Every leaves piece of the field draws from a GPU buffer of its list's matrices (a far cell's sprites per species,
        // uploaded when a side joins or goes; a near cell's models per species and slot, uploaded after each 4-a-second
        // sort) with Graphics.RenderMeshPrimitives, through EtsLeaves' _ETS_TREE_BUFFER variant (a plain multi_compile
        // the field's own material copies enable). Not Unity's instancing variants: the field's materials are made at
        // runtime, so with the project's Instancing Variants "Strip Unused" (GraphicsSettings m_InstancingStripping 0)
        // the bundle would carry no INSTANCING_ON / PROCEDURAL_INSTANCING_ON variant of the leaves shader, and nothing
        // would say so. Measured in the render lab (c1, Dover 56k trees): the buffers also cost far less than handing the
        // sprites to RenderMeshInstanced every frame (0.24 against 1.18 ms a frame pipelined). A piece of another shader
        // (none of the demo's tree models has one) still goes through RenderMeshInstanced. A draw that throws sends the
        // builder back to one GameObject per tree (logged).
        private static readonly int MatricesId = Shader.PropertyToID("_EtsTreeMatrices");
        private static readonly int CountId = Shader.PropertyToID("_EtsTreeCount");
        private const string BufferKeyword = "_ETS_TREE_BUFFER";
        private static int _uploads;

        /// <summary>
        /// Instances asked of each buffer draw per matrix: 2 while XR renders, 1 otherwise. Whether Unity doubles a
        /// RenderMeshPrimitives count for single-pass instanced stereo itself is not documented, so the count is doubled
        /// here and the shader drops every instance id at or past the list's length (_EtsTreeCount): the list is drawn
        /// whole in each eye either way (at worst each vertex of it runs once more and collapses). Read once a frame in Draw.
        /// </summary>
        private static int _instanceMultiplier = 1;

        private const int Slots = 4;
        private const int SlotHigh = 0, SlotShadow = 1, SlotNear = 2, SlotBand = 3;

        /// <summary>One drawable part of a model: a mesh's submesh with the material it draws with here.</summary>
        public sealed class Piece
        {
            public Mesh Mesh;
            public int Submesh;
            public Material Material;
            public bool Leaves;
            public ShadowCastingMode Shadow;
        }

        /// <summary>One vegetation model unit (vegetmodel.*): its hi-, med-poly and sprite models as instanced pieces.</summary>
        public sealed class Kind
        {
            public string Unit;
            public int Index;
            public Piece[] High, Medium, Sprite;
            public bool HasSprite;

            /// <summary>The drawing bounds round the foot at scale 1, any yaw: horizontal radius and height range.</summary>
            public float Radius, MinY, MaxY;

            /// <summary>The stored med-poly model's top above its origin at scale 1, metres (the tree's height).</summary>
            public float Top;

            public Bounds BoundsAt(Vector3 foot, float scale)
            {
                float r = Radius * scale;
                var min = new Vector3(foot.x - r, foot.y + MinY * scale, foot.z - r);
                var max = new Vector3(foot.x + r, foot.y + MaxY * scale, foot.z + r);
                var b = new Bounds();
                b.SetMinMax(min, max);
                return b;
            }
        }

        public struct Tree
        {
            public Matrix4x4 Matrix;
            public int Kind;
            public float Scale;

            /// <summary>The band's high_poly_distance, metres (0: never hi-poly).</summary>
            public float HighPoly;
        }

        /// <summary>The trees of one planted road or terrain side; gone when its map item is unloaded (Owner destroyed).</summary>
        public sealed class Stand
        {
            public Transform Owner;
            public Tree[] Trees = new Tree[16];
            public byte[] HighState = new byte[16];
            public int Count;
            public Bounds Bounds;
            public bool HasBounds;
            internal Bucket Bucket;
            internal bool Listed;

            public void Add(Vector3 foot, float yawDegrees, float scale, Kind kind, float highPoly)
            {
                if (Count == Trees.Length)
                {
                    Array.Resize(ref Trees, Count * 2);
                    Array.Resize(ref HighState, Count * 2);
                }

                Trees[Count] = new Tree
                {
                    Matrix = Matrix4x4.TRS(foot, Quaternion.Euler(0f, yawDegrees, 0f), new Vector3(scale, scale, scale)),
                    Kind = kind.Index,
                    Scale = scale,
                    HighPoly = highPoly
                };
                HighState[Count] = 0;
                Count++;

                Bounds b = kind.BoundsAt(foot, scale);

                if (!HasBounds)
                {
                    Bounds = b;
                    HasBounds = true;
                }
                else
                {
                    Bounds.Encapsulate(b);
                }
            }
        }

        /// <summary>A list of instance matrices with the bounds round them.</summary>
        internal sealed class Batch
        {
            public Matrix4x4[] Matrices = new Matrix4x4[16];
            public int Count;
            public Bounds Bounds;

            // The matrices on the GPU (leaves pieces), uploaded when the list changed.
            public GraphicsBuffer Buffer;
            public MaterialPropertyBlock Props;
            public bool Uploaded;

            public void Clear()
            {
                Count = 0;
                Uploaded = false;
            }

            public void Release()
            {
                if (Buffer != null)
                {
                    Buffer.Release();
                    Buffer = null;
                }

                Uploaded = false;
            }

            /// <summary>The list on the GPU; a new buffer only when it outgrew the old one.</summary>
            public void Upload()
            {
                if (Uploaded)
                {
                    return;
                }

                if (Buffer == null || Buffer.count < Count)
                {
                    Release();
                    int capacity = Mathf.NextPowerOfTwo(Mathf.Max(Count, 64));
                    Buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, capacity, 64);
                }

                Buffer.SetData(Matrices, 0, 0, Count);

                if (Props == null)
                {
                    Props = new MaterialPropertyBlock();
                }

                Props.SetBuffer(MatricesId, Buffer);
                Props.SetFloat(CountId, Count);
                Uploaded = true;
                _uploads++;
            }

            public void Add(ref Matrix4x4 m, Bounds b)
            {
                Uploaded = false;

                if (Count == Matrices.Length)
                {
                    Array.Resize(ref Matrices, Count * 2);
                }

                Matrices[Count] = m;

                if (Count == 0)
                {
                    Bounds = b;
                }
                else
                {
                    Bounds.Encapsulate(b);
                }

                Count++;
            }
        }

        /// <summary>The stands whose middle lies in one far cell, with a sprite list per species.</summary>
        internal sealed class Bucket
        {
            public long Key;
            public readonly List<Stand> Stands = new List<Stand>();
            public Batch[] PerKind = new Batch[0];
            public bool Dirty;
            public Bounds Bounds;
            public bool HasBounds;
        }

        /// <summary>The near models of one near cell: a list per species and slot (index kind x 4 + slot).</summary>
        internal sealed class NearCell
        {
            public long Key;
            public Batch[] PerSlot = new Batch[0];
            public bool Any;
        }

        private static readonly List<Kind> Kinds = new List<Kind>();
        private static readonly Dictionary<string, Kind> KindsByUnit = new Dictionary<string, Kind>();
        private static readonly Dictionary<long, Bucket> BucketsByKey = new Dictionary<long, Bucket>();
        private static readonly List<Bucket> Buckets = new List<Bucket>();
        private static readonly Dictionary<long, NearCell> NearByKey = new Dictionary<long, NearCell>();
        private static readonly List<NearCell> NearUsed = new List<NearCell>();
        private static readonly List<NearCell> NearPool = new List<NearCell>();
        private static readonly Dictionary<long, Material> Banded = new Dictionary<long, Material>();
        private static readonly List<Material> MadeMaterials = new List<Material>();
        private static GameObject _templates;
        private static float _refreshedAt = -10f;
        private static Vector3 _view;
        private static bool _failed, _capLogged, _startLogged;
        private static int _trees, _stands, _nearTrees, _highTrees, _shadowTrees, _calls, _maxCalls;
        private static double _submitMsSum, _submitMsMax, _refreshMsMax;
        private static int _submitFrames;

        /// <summary>Trees held now (planted and not unloaded with their item).</summary>
        public static int TreeCount => _trees;

        /// <summary>Stands (planted sides) held now.</summary>
        public static int StandCount => _stands;

        /// <summary>Set when a draw threw: the builder then replants everything as GameObjects (the old path).</summary>
        public static bool Failed => _failed;

        /// <summary>The reason instancing cannot be used on this machine, or null when it can.</summary>
        public static string Unavailable()
        {
            if (!SystemInfo.supportsInstancing)
            {
                return "the GPU does not support instancing";
            }

            if (!SystemInfo.supportsComputeShaders)
            {
                return "the GPU has no structured buffers";
            }

            // The bands (near model out, sprite in) are the leaves shader's; on URP Lit both would draw everywhere.
            Shader leaves = EtsMaterialCache.LeavesShader != null ? EtsMaterialCache.LeavesShader : Shader.Find("SomniumEuroTruck/Leaves");

            if (leaves == null || !leaves.isSupported)
            {
                return "the leaves shader is missing or unsupported";
            }

            return null;
        }

        /// <summary>The near models are listed out to here from the eye: the end of the game's 240-300 m band plus a margin.</summary>
        public static float NearReach => EtsVegetationLook.Fade.x + EtsVegetationLook.Fade.y + NearMargin;

        /// <summary>Whether the unit's species was made already (null if it has no drawable model): EtsVegetationBuilder paces new ones.</summary>
        public static bool Knows(string unit)
        {
            return unit != null && KindsByUnit.ContainsKey(unit);
        }

        /// <summary>Everything dropped (the stream went with its scene, or the builder fell back to GameObjects).</summary>
        public static void Reset()
        {
            foreach (Bucket bucket in Buckets)
            {
                for (int k = 0; k < bucket.PerKind.Length; k++)
                {
                    bucket.PerKind[k].Release();
                }
            }

            Buckets.Clear();
            BucketsByKey.Clear();
            ReleaseAll(NearUsed);
            ReleaseAll(NearPool);
            NearUsed.Clear();
            NearByKey.Clear();
            NearPool.Clear();
            Kinds.Clear();
            KindsByUnit.Clear();
            Banded.Clear();

            foreach (Material m in MadeMaterials)
            {
                if (m != null)
                {
                    UnityEngine.Object.Destroy(m);
                }
            }

            MadeMaterials.Clear();

            if (_templates != null)
            {
                UnityEngine.Object.Destroy(_templates);
                _templates = null;
            }

            _trees = 0;
            _stands = 0;
            _nearTrees = _highTrees = _shadowTrees = 0;
            _refreshedAt = -10f;
            _failed = false;
            _capLogged = false;
            _startLogged = false;
        }

        private static void ReleaseAll(List<NearCell> cells)
        {
            for (int c = 0; c < cells.Count; c++)
            {
                for (int k = 0; k < cells[c].PerSlot.Length; k++)
                {
                    cells[c].PerSlot[k].Release();
                }
            }
        }

        // ------------------------------------------------------------------ species

        /// <summary>
        /// The instanced pieces of a vegetation model unit, built once: the models through EtsModelBuilder (the same
        /// meshes and materials the planted GameObjects had; the meshes are the model caches' shared ones), leaves
        /// materials copied onto band 1 (near) and band 2 (sprite). Null when the unit has no drawable model.
        /// </summary>
        public static Kind KindFor(EtsMap map, string unit)
        {
            if (KindsByUnit.TryGetValue(unit, out Kind cached))
            {
                return cached;
            }

            Kind kind = null;
            long started = Stopwatch.GetTimestamp();

            try
            {
                EtsModelData med = EtsVegetationBuilder.Model(map, unit);

                if (med != null)
                {
                    EtsModelData high = EtsVegetationBuilder.HighModel(map, unit);
                    EtsModelData sprite = EtsVegetationBuilder.SpriteModel(map, unit);
                    kind = new Kind { Unit = unit, Index = Kinds.Count };
                    var bounds = new Bounds();
                    bool hasBounds = false;
                    bool hasSprite = sprite != null && sprite != med;

                    // Without a sprite the near model draws at every distance (band 0), as the planted LODGroup did.
                    kind.Medium = Pieces(med, hasSprite ? 1f : 0f, ref bounds, ref hasBounds);
                    kind.High = high != null && high != med && hasSprite ? Pieces(high, 1f, ref bounds, ref hasBounds) : null;
                    kind.Sprite = hasSprite ? Pieces(sprite, 2f, ref bounds, ref hasBounds) : null;
                    kind.HasSprite = hasSprite && kind.Sprite != null && kind.Sprite.Length > 0;

                    if (kind.High != null && kind.High.Length == 0)
                    {
                        kind.High = null;
                    }

                    if (kind.Medium == null || kind.Medium.Length == 0 || !hasBounds)
                    {
                        kind = null;
                    }
                    else
                    {
                        float x = Mathf.Max(Mathf.Abs(bounds.min.x), Mathf.Abs(bounds.max.x));
                        float z = Mathf.Max(Mathf.Abs(bounds.min.z), Mathf.Abs(bounds.max.z));
                        kind.Radius = Mathf.Sqrt(x * x + z * z);
                        kind.MinY = bounds.min.y;
                        kind.MaxY = bounds.max.y;
                        kind.Top = med.BoundsMax != null && med.BoundsMax.Length >= 3 ? med.BoundsMax[1] : bounds.max.y * 0.5f;
                        Kinds.Add(kind);
                    }
                }
            }
            catch (Exception e)
            {
                kind = null;
                Debug.LogWarning("[SomniumEuroTruck] Trees: species " + unit + " could not be made instanced: " + e.GetType().Name + ": " + e.Message);
            }

            KindsByUnit[unit] = kind;
            double ms = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;

            if (kind != null && Kinds.Count <= 64)
            {
                Debug.Log("[SomniumEuroTruck] Trees: species " + unit + " instanced in " + ms.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " ms: hi-poly " +
                          (kind.High != null ? kind.High.Length + " pieces" : "none") + ", med " + kind.Medium.Length + ", sprite " + (kind.HasSprite ? kind.Sprite.Length + " pieces" : "none (med drawn at every distance)") +
                          ", reach " + kind.Radius.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " m, top " + kind.Top.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " m.");
            }

            return kind;
        }

        private static Piece[] Pieces(EtsModelData model, float band, ref Bounds bounds, ref bool hasBounds)
        {
            if (_templates == null)
            {
                _templates = new GameObject("Ets tree templates");
                _templates.SetActive(false);
            }

            EtsModelBuilder.Result built = EtsModelBuilder.Build(model, new EtsModelBuilder.Options
            {
                Look = "default",
                Variants = new[] { "default" },
                CreateLocators = false,
                AddColliders = false,
                CastShadows = true
            }, _templates.transform, EtsEngine.Materials);

            var pieces = new List<Piece>();

            if (built != null && built.Root != null)
            {
                Matrix4x4 toRoot = built.Root.transform.worldToLocalMatrix;

                foreach (MeshRenderer renderer in built.Root.GetComponentsInChildren<MeshRenderer>(true))
                {
                    MeshFilter filter = renderer.GetComponent<MeshFilter>();
                    Mesh mesh = filter != null ? filter.sharedMesh : null;

                    if (mesh == null)
                    {
                        continue;
                    }

                    // EtsModelBuilder never moves a part or a piece off the model's root; the instance matrix is the root's.
                    Matrix4x4 local = toRoot * renderer.transform.localToWorldMatrix;

                    if (!local.isIdentity)
                    {
                        Debug.LogWarning("[SomniumEuroTruck] Trees: " + model.Path + " has a piece off its root; drawn at the root.");
                    }

                    Material[] materials = renderer.sharedMaterials;

                    for (int s = 0; s < materials.Length && s < mesh.subMeshCount; s++)
                    {
                        Material source = materials[s];

                        if (source == null)
                        {
                            continue;
                        }

                        bool leaves = source.shader == EtsMaterialCache.LeavesShader;
                        pieces.Add(new Piece
                        {
                            Mesh = mesh,
                            Submesh = s,
                            Leaves = leaves,
                            Material = leaves ? BandedCopy(source, band) : Instancing(source),
                            Shadow = band > 1.5f ? ShadowCastingMode.Off : renderer.shadowCastingMode
                        });

                        if (!hasBounds)
                        {
                            bounds = mesh.bounds;
                            hasBounds = true;
                        }
                        else
                        {
                            bounds.Encapsulate(mesh.bounds);
                        }
                    }
                }

                UnityEngine.Object.Destroy(built.Root);
            }

            return pieces.ToArray();
        }

        /// <summary>
        /// A leaves material's copy for the field, once per material and band (1 near, 2 sprite, 0 a species without a
        /// sprite, drawn whole at every distance): its matrices from the field's buffers (_ETS_TREE_BUFFER), so the
        /// shared material the map's own trees draw with is never touched.
        /// </summary>
        private static Material BandedCopy(Material source, float band)
        {
            long key = ((long)source.GetInstanceID() << 4) | (long)Mathf.RoundToInt(band);

            if (Banded.TryGetValue(key, out Material cached) && cached != null)
            {
                return cached;
            }

            var copy = new Material(source) { name = source.name + " band " + band.ToString("0") };
            copy.SetFloat("_Band", band);
            copy.enableInstancing = true;
            copy.EnableKeyword(BufferKeyword);

            // A sprite never casts in the game (its vertices are fade-in in the near model; the shadow pass drops them).
            copy.SetShaderPassEnabled("ShadowCaster", band < 1.5f && source.GetShaderPassEnabled("ShadowCaster"));
            Banded[key] = copy;
            MadeMaterials.Add(copy);
            return copy;
        }

        private static Material Instancing(Material source)
        {
            if (source.enableInstancing)
            {
                return source;
            }

            long key = ((long)source.GetInstanceID() << 4) | 15L;

            if (Banded.TryGetValue(key, out Material cached) && cached != null)
            {
                return cached;
            }

            var copy = new Material(source) { name = source.name + " instanced" };
            copy.enableInstancing = true;
            Banded[key] = copy;
            MadeMaterials.Add(copy);
            return copy;
        }

        // ------------------------------------------------------------------ stands

        /// <summary>A planted side's trees join the field (its far cell's sprite lists at once; near lists at the next refresh).</summary>
        public static bool Add(Stand stand)
        {
            if (stand == null || stand.Count == 0 || stand.Listed || !stand.HasBounds)
            {
                return false;
            }

            if (_trees + stand.Count > MaxTrees)
            {
                if (!_capLogged)
                {
                    _capLogged = true;
                    Debug.LogWarning("[SomniumEuroTruck] Trees: the instanced field is full (" + _trees + " trees, cap " + MaxTrees + "); further sides are not drawn.");
                }

                return false;
            }

            long key = CellKey(stand.Bounds.center, FarCellSize);

            if (!BucketsByKey.TryGetValue(key, out Bucket bucket))
            {
                bucket = new Bucket { Key = key };
                BucketsByKey[key] = bucket;
                Buckets.Add(bucket);
            }

            stand.Bucket = bucket;
            stand.Listed = true;
            bucket.Stands.Add(stand);
            _trees += stand.Count;
            _stands++;

            if (!bucket.Dirty)
            {
                AppendSprites(bucket, stand);
            }

            // Near lists again next frame when the side lies within the near reach, so it shows at once (a side planted out
            // at the 2150 m edge only joins its far cell's sprites and waits for the next timed refresh).
            float reach = NearReach;

            if (stand.Bounds.SqrDistance(_view) <= reach * reach)
            {
                _refreshedAt = -10f;
            }

            if (!_startLogged)
            {
                _startLogged = true;
                Debug.Log("[SomniumEuroTruck] Trees: GPU-instanced field on: density x" + EtsVegetationBuilder.DensityScale.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) +
                          " of the map's, planted to " + EtsVegetationBuilder.InstancedRadius.ToString("0") + " m, sprites drawn to " + DrawRadius.ToString("0") +
                          " m, near models to " + NearReach.ToString("0") + " m with the game's " + (EtsVegetationLook.Fade.x - EtsVegetationLook.Fade.y).ToString("0") + "-" +
                          (EtsVegetationLook.Fade.x + EtsVegetationLook.Fade.y).ToString("0") + " m dither, hi-poly within the band's high_poly_distance, sun shadows from near models within " +
                          (ShadowDistance() + ShadowMargin).ToString("0") + " m (sprites never), leaves drawn from GPU buffers (EtsLeaves _ETS_TREE_BUFFER, " + (UnityEngine.XR.XRSettings.enabled ? "x2 instances for the eyes" : "mono") + ").");
            }

            return true;
        }

        private static void AppendSprites(Bucket bucket, Stand stand)
        {
            EnsureKinds(ref bucket.PerKind, 1);

            for (int i = 0; i < stand.Count; i++)
            {
                Kind kind = Kinds[stand.Trees[i].Kind];
                Bounds b = kind.BoundsAt(Position(ref stand.Trees[i].Matrix), stand.Trees[i].Scale);
                bucket.PerKind[kind.Index].Add(ref stand.Trees[i].Matrix, b);

                if (!bucket.HasBounds)
                {
                    bucket.Bounds = b;
                    bucket.HasBounds = true;
                }
                else
                {
                    bucket.Bounds.Encapsulate(b);
                }
            }
        }

        private static void Rebuild(Bucket bucket)
        {
            for (int k = 0; k < bucket.PerKind.Length; k++)
            {
                bucket.PerKind[k].Clear();
            }

            bucket.HasBounds = false;

            for (int s = bucket.Stands.Count - 1; s >= 0; s--)
            {
                Stand stand = bucket.Stands[s];

                if (stand.Owner == null)
                {
                    Drop(bucket, s);
                    continue;
                }

                AppendSprites(bucket, stand);
            }

            bucket.Dirty = false;
        }

        private static void Drop(Bucket bucket, int index)
        {
            Stand stand = bucket.Stands[index];
            _trees -= stand.Count;
            _stands--;
            stand.Listed = false;
            stand.Bucket = null;
            int last = bucket.Stands.Count - 1;
            bucket.Stands[index] = bucket.Stands[last];
            bucket.Stands.RemoveAt(last);
        }

        private static void EnsureKinds(ref Batch[] batches, int perKind)
        {
            int want = Kinds.Count * perKind;

            if (batches.Length >= want)
            {
                return;
            }

            int had = batches.Length;
            Array.Resize(ref batches, want);

            for (int i = had; i < want; i++)
            {
                batches[i] = new Batch();
            }
        }

        private static Vector3 Position(ref Matrix4x4 m)
        {
            return new Vector3(m.m03, m.m13, m.m23);
        }

        private static long CellKey(Vector3 p, float size)
        {
            long x = (long)Mathf.Floor(p.x / size) + (1L << 20);
            long z = (long)Mathf.Floor(p.z / size) + (1L << 20);
            return (x << 32) | (z & 0xffffffffL);
        }

        private static float ShadowDistance()
        {
            var urp = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            return urp != null ? urp.shadowDistance : QualitySettings.shadowDistance;
        }

        // ------------------------------------------------------------------ the near lists

        /// <summary>
        /// Unloaded stands go (their far cells are listed again), and the near models round the eye are sorted into near
        /// cells: hi-poly within the band's high_poly_distance (with hysteresis), med-poly casting shadows within the
        /// shadow distance, med-poly without shadows to the band's middle, and the leaves pieces alone to the reach.
        /// </summary>
        private static void Refresh(Vector3 view)
        {
            long start = Stopwatch.GetTimestamp();
            _view = view;

            for (int b = Buckets.Count - 1; b >= 0; b--)
            {
                Bucket bucket = Buckets[b];

                for (int s = 0; s < bucket.Stands.Count; s++)
                {
                    if (bucket.Stands[s].Owner == null)
                    {
                        bucket.Dirty = true;
                        break;
                    }
                }

                if (bucket.Dirty)
                {
                    Rebuild(bucket);
                }

                // A far cell left behind with nothing in it goes (a long drive would pile them up).
                if (bucket.Stands.Count == 0)
                {
                    for (int k = 0; k < bucket.PerKind.Length; k++)
                    {
                        bucket.PerKind[k].Release();
                    }

                    BucketsByKey.Remove(bucket.Key);
                    Buckets[b] = Buckets[Buckets.Count - 1];
                    Buckets.RemoveAt(Buckets.Count - 1);
                }
            }

            for (int i = 0; i < NearUsed.Count; i++)
            {
                NearCell cell = NearUsed[i];

                for (int k = 0; k < cell.PerSlot.Length; k++)
                {
                    cell.PerSlot[k].Clear();
                }

                cell.Any = false;
                NearPool.Add(cell);
            }

            NearUsed.Clear();
            NearByKey.Clear();
            _nearTrees = _highTrees = _shadowTrees = 0;

            float reach = NearReach;
            float reachSq = reach * reach;
            float shadowReach = ShadowDistance() + ShadowMargin;
            float middle = EtsVegetationLook.Fade.x;

            for (int b = 0; b < Buckets.Count; b++)
            {
                Bucket bucket = Buckets[b];

                if (!bucket.HasBounds || bucket.Bounds.SqrDistance(view) > reachSq)
                {
                    continue;
                }

                for (int s = 0; s < bucket.Stands.Count; s++)
                {
                    Stand stand = bucket.Stands[s];

                    if (stand.Bounds.SqrDistance(view) > reachSq)
                    {
                        continue;
                    }

                    for (int i = 0; i < stand.Count; i++)
                    {
                        Kind kind = Kinds[stand.Trees[i].Kind];

                        if (!kind.HasSprite)
                        {
                            continue; // drawn whole at every distance by its far cell
                        }

                        Vector3 foot = Position(ref stand.Trees[i].Matrix);
                        float d = Vector3.Distance(foot, view);

                        if (d >= reach)
                        {
                            stand.HighState[i] = 0;
                            continue;
                        }

                        float hp = stand.Trees[i].HighPoly;
                        bool high = kind.High != null && hp > 0f && d < hp + (stand.HighState[i] != 0 ? HighPolyHysteresis : -HighPolyHysteresis);
                        stand.HighState[i] = (byte)(high ? 1 : 0);
                        int slot = high ? SlotHigh : d < shadowReach ? SlotShadow : d < middle ? SlotNear : SlotBand;

                        long key = CellKey(foot, NearCellSize);

                        if (!NearByKey.TryGetValue(key, out NearCell cell))
                        {
                            if (NearPool.Count > 0)
                            {
                                cell = NearPool[NearPool.Count - 1];
                                NearPool.RemoveAt(NearPool.Count - 1);
                            }
                            else
                            {
                                cell = new NearCell();
                            }

                            cell.Key = key;
                            NearByKey[key] = cell;
                            NearUsed.Add(cell);
                        }

                        EnsureKinds(ref cell.PerSlot, Slots);
                        cell.PerSlot[kind.Index * Slots + slot].Add(ref stand.Trees[i].Matrix, kind.BoundsAt(foot, stand.Trees[i].Scale));
                        cell.Any = true;
                        _nearTrees++;

                        if (high) _highTrees++;
                        if (slot <= SlotShadow) _shadowTrees++;
                    }
                }
            }

            double ms = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;

            if (ms > _refreshMsMax)
            {
                _refreshMsMax = ms;
            }
        }

        // ------------------------------------------------------------------ drawing

        /// <summary>
        /// This frame's draws, from EtsVegetationStream.Update (before any camera renders): the near lists are refreshed
        /// round <paramref name="view"/> every RefreshSeconds, then every list is submitted. False when a draw threw
        /// (Failed is then set and the builder falls back to GameObjects).
        /// </summary>
        public static bool Draw(Vector3 view)
        {
            if (!Enabled || _failed || _stands == 0)
            {
                return true;
            }

            long start = Stopwatch.GetTimestamp();

            try
            {
                if (Time.unscaledTime - _refreshedAt >= RefreshSeconds)
                {
                    _refreshedAt = Time.unscaledTime;
                    Refresh(view);
                }

                _calls = 0;
                _instanceMultiplier = UnityEngine.XR.XRSettings.enabled ? 2 : 1;
                float far = DrawRadius;
                float farSq = far * far;

                for (int b = 0; b < Buckets.Count && DrawFar; b++)
                {
                    Bucket bucket = Buckets[b];

                    if (!bucket.HasBounds || bucket.Bounds.SqrDistance(_view) > farSq)
                    {
                        continue;
                    }

                    for (int k = 0; k < bucket.PerKind.Length && k < Kinds.Count; k++)
                    {
                        Batch batch = bucket.PerKind[k];

                        if (batch.Count == 0)
                        {
                            continue;
                        }

                        Kind kind = Kinds[k];
                        Piece[] pieces = kind.HasSprite ? kind.Sprite : kind.Medium;

                        for (int p = 0; p < pieces.Length; p++)
                        {
                            Submit(pieces[p], batch, kind.HasSprite ? ShadowCastingMode.Off : pieces[p].Shadow);
                        }
                    }
                }

                for (int c = 0; c < NearUsed.Count && DrawNear; c++)
                {
                    NearCell cell = NearUsed[c];

                    if (!cell.Any)
                    {
                        continue;
                    }

                    for (int i = 0; i < cell.PerSlot.Length && i < Kinds.Count * Slots; i++)
                    {
                        Batch batch = cell.PerSlot[i];

                        if (batch.Count == 0)
                        {
                            continue;
                        }

                        Kind kind = Kinds[i / Slots];
                        int slot = i % Slots;
                        Piece[] pieces = slot == SlotHigh ? kind.High : kind.Medium;

                        for (int p = 0; p < pieces.Length; p++)
                        {
                            // Past the band's middle only the leaves pieces (they dither out); a separate trunk would stand bare.
                            if (slot == SlotBand && !pieces[p].Leaves)
                            {
                                continue;
                            }

                            Submit(pieces[p], batch, slot <= SlotShadow ? pieces[p].Shadow : ShadowCastingMode.Off);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                _failed = true;
                Debug.LogWarning("[SomniumEuroTruck] Trees: GPU-instanced drawing failed (" + e.GetType().Name + ": " + e.Message + "); falling back to one GameObject per tree (the old path).");
                return false;
            }

            double ms = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
            _submitMsSum += ms;
            _submitFrames++;

            if (ms > _submitMsMax)
            {
                _submitMsMax = ms;
            }

            if (_calls > _maxCalls)
            {
                _maxCalls = _calls;
            }

            return true;
        }

        /// <summary>
        /// One list's piece: a leaves piece from the list's GPU buffer (uploaded here when the list changed; one call
        /// whatever its size), anything else through RenderMeshInstanced in batches of 1023. Either way Unity culls the
        /// call by the list's bounds for every camera (the headset, the mirrors) and shadow cascade, allocation-free.
        /// </summary>
        private static void Submit(Piece piece, Batch batch, ShadowCastingMode shadow)
        {
            if (piece.Leaves)
            {
                batch.Upload();
                var buffered = new RenderParams(piece.Material)
                {
                    worldBounds = batch.Bounds,
                    shadowCastingMode = shadow,
                    receiveShadows = true,
                    layer = Layer,
                    lightProbeUsage = LightProbeUsage.Off,
                    reflectionProbeUsage = ReflectionProbeUsage.BlendProbes,
                    matProps = batch.Props
                };

                Graphics.RenderMeshPrimitives(buffered, piece.Mesh, piece.Submesh, batch.Count * _instanceMultiplier);
                _calls++;
                return;
            }

            var rp = new RenderParams(piece.Material)
            {
                worldBounds = batch.Bounds,
                shadowCastingMode = shadow,
                receiveShadows = true,
                layer = Layer,
                lightProbeUsage = LightProbeUsage.Off,
                reflectionProbeUsage = ReflectionProbeUsage.BlendProbes
            };

            for (int start = 0; start < batch.Count; start += MaxBatch)
            {
                int n = Mathf.Min(MaxBatch, batch.Count - start);
                Graphics.RenderMeshInstanced(rp, piece.Mesh, piece.Submesh, batch.Matrices, n, start);
                _calls++;
            }
        }

        /// <summary>
        /// One line for the builder's 30 s report: trees, stands, far cells, near trees (hi-poly, casting), draw calls
        /// and the main-thread cost of the submission since the last report.
        /// </summary>
        public static string Summary()
        {
            double mean = _submitFrames > 0 ? _submitMsSum / _submitFrames : 0.0;
            string text = "instanced field: " + _trees + " trees on " + _stands + " sides in " + Buckets.Count + " far cells, " + Kinds.Count + " species; near " + _nearTrees +
                          " (hi-poly " + _highTrees + ", casting shadows " + _shadowTrees + ") in " + NearUsed.Count + " near cells; " + _calls + " draw calls a frame (most " + _maxCalls + "; leaves from GPU buffers, " + _uploads + " list uploads" +
                          "), submit " + mean.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) + " ms a frame (slowest " +
                          _submitMsMax.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " ms), near sort slowest " +
                          _refreshMsMax.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " ms";
            _submitMsSum = 0;
            _submitFrames = 0;
            _submitMsMax = 0;
            _refreshMsMax = 0;
            _maxCalls = 0;
            _uploads = 0;
            return text;
        }

        /// <summary>
        /// The foot and height of every tree the field holds within <paramref name="radius"/> of <paramref name="near"/>
        /// (for the render lab's tree views; not per frame). Height is the stored model's at the tree's scale.
        /// </summary>
        public static void CopyTrees(Vector3 near, float radius, List<Vector4> into)
        {
            float rSq = radius * radius;

            for (int b = 0; b < Buckets.Count; b++)
            {
                List<Stand> stands = Buckets[b].Stands;

                for (int s = 0; s < stands.Count; s++)
                {
                    Stand stand = stands[s];

                    for (int i = 0; i < stand.Count; i++)
                    {
                        Vector3 p = Position(ref stand.Trees[i].Matrix);
                        float dx = p.x - near.x, dz = p.z - near.z;

                        if (dx * dx + dz * dz <= rSq)
                        {
                            into.Add(new Vector4(p.x, p.y, p.z, Kinds[stand.Trees[i].Kind].Top * stand.Trees[i].Scale));
                        }
                    }
                }
            }
        }
    }
}
