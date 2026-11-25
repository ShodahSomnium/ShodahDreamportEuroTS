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
    /// The cars as things to see and hit, built from the game's own AI models on the player's
    /// install, pooled per model so a car that leaves and a car that arrives cost no building.
    ///
    /// Measured on the 20 cars Poland spawns (harness Program1379/1400): LOD0 is 7-13 pieces of
    /// 4,172-10,521 vertices, LOD1 5-10 pieces of 1,266-6,361, LOD2 5-9 of 681-2,608, LOD3 1-3
    /// pieces of 192-476; wheels are 2-piece models hung on 'wheel_*' locators (wheel_f_0 at x
    /// -0.76 is the left front). Every body is eut2.truckpaint.rfx over the car's color.tobj, and
    /// the car's colour[] (linear floats in its traffic_vehicle unit) paints it; eut2.shadowonly
    /// pieces are skipped by EtsMaterialCache as for the truck.
    ///
    /// Wheels ride on LOD0 only. LOD1 and LOD2 carry wheels of their own: round each wheel
    /// locator's axle, their vertices at the tyre's radius fill 11.3 and 10.5 of twelve 30-degree
    /// sectors on average, LOD0's (which has no wheels, only the arch) 5.9 (Program1405, all 20).
    /// Hung on LOD1-2 too, the separate wheels would be drawn over the baked ones.
    ///
    /// Everything is shared with the rest of the world: meshes through EtsModelBuilder's cache
    /// (EtsModelBuilder.IsShared - the map's unload never destroys one), materials and textures
    /// through EtsEngine's caches, the parsed models through EtsEngine.LoadModel's. The body
    /// paint is built with EtsMaterialCache.PaintColor set to white, restored at once, and the
    /// material cache keys truckpaint materials by that colour, so neither the truck's paint nor
    /// the cars' can reach the other whichever is built first; each car's colour goes on a copy
    /// per (material, colour).
    ///
    /// Building without a hitch. A car model is 5-6 model files (1-6 ms each to read and parse)
    /// and 6-11 textures (up to 50 ms each to read and GDeflate, Program1401). So a car model is
    /// a TEMPLATE built by a job over many frames: the files are read and the textures decoded on
    /// the worker thread (EtsWorker) and uploaded one per step; then each material, each mesh
    /// and each lod's objects is one step, never more than one car model at a time; the template
    /// sits inactive. A car on the road is a clone of its template (Object.Instantiate, one a
    /// frame) or a pooled clone.
    ///
    /// A car's root carries a Rigidbody (interpolated, the car's own vehicle_mass from its
    /// traffic_vehicle unit) and two BoxColliders on the Vehicle layer (28, which collides with
    /// Vehicle and with the avatar's Ignore Raycast capsule, EtsLayers), fitted to the body
    /// (FitBody): the lower body from the ground to the belt line, bumper to bumper and door to
    /// door without the mirrors, and the cabin from the belt line to the roof, as wide and long
    /// as the roof. The single box it replaces was the model's whole bounds - mirrors, aerial and
    /// all - so a truck passing a car was stopped by air (the knock of 2026-09-23: 26.1 kN s at
    /// 45 km/h off a qashqai's box 2.2 m wide). On the road the body is kinematic - the traffic's
    /// authority moves it; hit, EtsTraffic makes it dynamic (MakeDynamic), so the truck pushes and
    /// spins a car of its real weight instead of stopping dead against an immovable one, and its
    /// four amber hazard lamps (Lamps) blink. Vehicle is outside EtsLayers.GroundMask, so the
    /// wheel rays, the ground fill and the spawn rays never take a car for ground. Colliders are
    /// switched on only near the local player or truck (EtsTraffic).
    /// </summary>
    public sealed class EtsTrafficModels
    {
        public sealed class Visual
        {
            public int Vehicle;
            public int Colour = -1;
            public GameObject Root;
            public Rigidbody Body;

            /// <summary>The lower body's box (ground to belt line) and the cabin's (belt line to roof; null for a car whose body could not be fitted).</summary>
            public BoxCollider Box;
            public BoxCollider Cabin;

            public LODGroup Lods;
            public readonly List<Transform> Wheels = new List<Transform>(4);
            public readonly List<bool> Steers = new List<bool>(4);
            public readonly List<Renderer> Painted = new List<Renderer>();
            public readonly List<Material> PaintBase = new List<Material>();
            public float WheelRadius = 0.3f;
            public float Spin;

            /// <summary>The car's vehicle_mass, kg, and where its weight sits (root space: low, as a car's).</summary>
            public float Mass = 1500f;
            public Vector3 MassCentre;

            /// <summary>One box round both colliders, root space (centre, half size): what EtsTraffic tests against the trucks.</summary>
            public Vector3 HullCentre;
            public Vector3 HullHalf = new Vector3(0.9f, 0.75f, 2.1f);

            /// <summary>Physics moves it (a wreck): MakeDynamic, MakeKinematic.</summary>
            public bool Dynamic;

            public GameObject[] Lamps = new GameObject[0];
            public bool LampsLit;
        }

        /// <summary>Screen heights at which a car drops to the next lod (the last one culls). Not from the game; to be tuned in the headset.</summary>
        public static float[] LodHeights = { 0.12f, 0.045f, 0.012f, 0.0015f };

        private static readonly int BaseColor = Shader.PropertyToID("_BaseColor");
        private const string SpinFront = "spin f", SpinRear = "spin r";

        private sealed class Template
        {
            public int Vehicle;
            public GameObject Root;
            public bool Ready, Failed;
            public string Failure;
            public IEnumerator<bool> Job;
            public readonly HashSet<Material> PaintBase = new HashSet<Material>();
            public float WheelRadius = 0.3f;
            public int[] RenderersPerLod = new int[0];
            public Vector3 HullCentre, HullHalf = new Vector3(0.9f, 0.75f, 2.1f), MassCentre;
            public float Belt;
            public bool Fitted;
        }

        private const string LampName = "hazard lamp";

        private readonly EtsTrafficCatalog _catalog;
        private readonly Transform _parent;
        private readonly EtsWorker _worker;
        private readonly Dictionary<int, Template> _templates = new Dictionary<int, Template>();
        private readonly List<Template> _queue = new List<Template>();
        private readonly Dictionary<int, Stack<Visual>> _pool = new Dictionary<int, Stack<Visual>>();
        private readonly Dictionary<long, Material> _paint = new Dictionary<long, Material>();
        private readonly HashSet<string> _modelsAsked = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _modelsFailed = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _texturesAsked = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _texturesPending = new HashSet<string>(StringComparer.Ordinal);
        private int _frame = -1, _shownThisFrame, _newThisFrame;

        /// <summary>Car objects made (clones), ever.</summary>
        public int Built { get; private set; }

        public int TemplatesReady { get; private set; }
        public int TemplatesFailed { get; private set; }

        /// <summary>Templates waiting or in progress.</summary>
        public int TemplatesPending => _queue.Count;

        /// <summary>Most clones made in one frame, and most cars shown (clones plus pooled ones woken) in one frame.</summary>
        public int MaxNewPerFrame = 1;
        public int MaxShownPerFrame = 2;

        public EtsTrafficModels(EtsTrafficCatalog catalog, Transform parent, EtsWorker worker)
        {
            _catalog = catalog;
            _parent = parent;
            _worker = worker;
        }

        public bool IsReady(int vehicle)
        {
            return _templates.TryGetValue(vehicle, out Template t) && t.Ready;
        }

        /// <summary>Queues a car model's template (the prewarm, or a car arriving whose model is not ready).</summary>
        public void Want(int vehicle)
        {
            if (vehicle < 0 || vehicle >= _catalog.Vehicles.Count || _templates.ContainsKey(vehicle))
            {
                return;
            }

            var template = new Template { Vehicle = vehicle };
            _templates[vehicle] = template;
            _queue.Add(template);
        }

        /// <summary>
        /// Runs the first queued template's job until the clock passes the budget, one car model
        /// at a time. Returns true while templates remain.
        /// </summary>
        public bool Work(Stopwatch clock, double budgetMs)
        {
            while (_queue.Count > 0 && clock.Elapsed.TotalMilliseconds < budgetMs)
            {
                Template t = _queue[0];

                if (t.Job == null)
                {
                    t.Job = Steps(t);
                }

                bool more;

                try
                {
                    more = t.Job.MoveNext();
                }
                catch (Exception e)
                {
                    more = false;
                    t.Failed = true;
                    t.Failure = e.GetType().Name + ": " + e.Message;
                }

                if (!more)
                {
                    _queue.RemoveAt(0);
                    t.Job = null;

                    if (t.Ready)
                    {
                        TemplatesReady++;
                    }
                    else
                    {
                        t.Failed = true;
                        TemplatesFailed++;

                        if (t.Root != null)
                        {
                            UnityEngine.Object.Destroy(t.Root);
                            t.Root = null;
                        }

                        Debug.LogWarning("[SomniumEuroTruck] Traffic: car model " + _catalog.Vehicles[t.Vehicle].Unit + " could not be built: " + (t.Failure ?? "no lod built") + ".");
                    }

                    // One car model a frame: the next starts next frame.
                    return _queue.Count > 0;
                }

                if (!t.Job.Current)
                {
                    // Waiting on the worker: nothing else to do for this template this frame.
                    return true;
                }
            }

            return _queue.Count > 0;
        }

        // ---------------------------------------------------------------- a template, step by step

        private sealed class PieceUse
        {
            public EtsModelData Model;
            public EtsModelPiece Piece;
            public int Index;
            public string Material;
        }

        private IEnumerator<bool> Steps(Template t)
        {
            EtsTrafficCatalog.Vehicle v = _catalog.Vehicles[t.Vehicle];

            if (string.IsNullOrEmpty(v.Model) || !EtsEngine.IsMounted)
            {
                t.Failure = "no chassis model";
                yield break;
            }

            var bodyPaths = new List<string> { v.Model };
            bodyPaths.AddRange(v.Lods);
            var wheelPaths = new List<string>();
            if (!string.IsNullOrEmpty(v.FrontWheel)) wheelPaths.Add(v.FrontWheel);
            if (!string.IsNullOrEmpty(v.RearWheel) && v.RearWheel != v.FrontWheel) wheelPaths.Add(v.RearWheel);

            // 1. The model files, read and parsed on the worker.
            var all = new List<string>(bodyPaths);
            all.AddRange(wheelPaths);
            foreach (string path in all) AskModel(path);

            while (!AllModels(all))
            {
                yield return false;
            }

            EtsModelData lod0 = Loaded(v.Model);

            if (lod0 == null)
            {
                t.Failure = "chassis model " + v.Model + " could not be read";
                yield break;
            }

            // 2. Which pieces will be drawn, and their materials' definitions (one small text file
            // each), then every texture those materials will load, prepared on the worker.
            var uses = new List<PieceUse>();

            foreach (string path in bodyPaths)
            {
                EtsModelData model = Loaded(path);
                if (model != null) Visible(model, v.Look, new[] { v.Variant, "default" }, uses);
            }

            foreach (string path in wheelPaths)
            {
                EtsModelData model = Loaded(path);
                if (model != null) Visible(model, "default", new[] { "left", "right", "default" }, uses);
            }

            var materials = new List<string>();
            var seenMaterials = new HashSet<string>(StringComparer.Ordinal);
            int defsThisStep = 0;

            foreach (PieceUse use in uses)
            {
                if (use.Material == null || !seenMaterials.Add(use.Material)) continue;
                materials.Add(use.Material);
                EtsMaterialDef def = EtsEngine.Materials.GetDefinition(use.Material);
                if (def != null) AskTextures(def);
                if (++defsThisStep % 4 == 0) yield return true;
            }

            while (_texturesPending.Count > 0)
            {
                yield return false;
            }

            // 3. The materials, one a step, built with a white paint job (see the class notes).
            // A material the cache answers with null is one the game does not draw (shadow-only
            // and the like); EtsModelBuilder.Build makes no mesh for its pieces, and neither does
            // step 4.
            var notDrawn = new HashSet<string>(StringComparer.Ordinal);

            foreach (string material in materials)
            {
                if (LoadWhite(material) == null) notDrawn.Add(material);
                yield return true;
            }

            // 4. The meshes, one a step (5-10 k vertices at LOD0).
            foreach (PieceUse use in uses)
            {
                if (use.Material != null && notDrawn.Contains(use.Material)) continue;
                MakeMesh(use);
                yield return true;
            }

            // 5. The objects, a lod a step, under an inactive root.
            var root = new GameObject("Ets car " + v.Unit);
            root.SetActive(false);
            root.transform.SetParent(_parent, false);
            t.Root = root;
            var levels = new List<LOD>();
            EtsModelBuilder.Result built0 = BuildWhite(lod0, new EtsModelBuilder.Options
            {
                Look = v.Look,
                Variants = new[] { v.Variant, "default" },
                CreateLocators = true,
                AddColliders = false,
                CastShadows = true
            }, root.transform);
            yield return true;

            // LOD0 is the body and the wheels hung on its locators (which sit inside it, so its
            // renderers are collected once the wheels are on).
            HangWheels(t, built0, v);
            levels.Add(new LOD(LodHeights[0], built0.Root.GetComponentsInChildren<Renderer>(true)));
            yield return true;

            for (int i = 1; i < bodyPaths.Count; i++)
            {
                EtsModelData model = Loaded(bodyPaths[i]);

                if (model == null)
                {
                    continue;
                }

                EtsModelBuilder.Result built = BuildWhite(model, new EtsModelBuilder.Options
                {
                    Look = v.Look,
                    Variants = new[] { v.Variant, "default" },
                    CreateLocators = false,
                    AddColliders = false,

                    // Shadows from LOD0 and LOD1 only.
                    CastShadows = i <= 1
                }, root.transform);

                levels.Add(new LOD(LodHeights[Math.Min(levels.Count, LodHeights.Length - 1)], built.Root.GetComponentsInChildren<Renderer>(true)));
                yield return true;
            }

            // The locators that hold nothing (lights, mirrors, hooks): every clone would copy them.
            foreach (KeyValuePair<string, Transform> entry in built0.Locators)
            {
                if (entry.Value != null && entry.Value.childCount == 0)
                {
                    UnityEngine.Object.Destroy(entry.Value.gameObject);
                }
            }

            // The group's size and centre from the model's own bounds: RecalculateBounds reads the
            // renderers' bounds, which are empty while the template is inactive.
            Vector3 a = EtsModelBuilder.ToUnity(lod0.BoundsMin), b = EtsModelBuilder.ToUnity(lod0.BoundsMax);
            LODGroup group = root.AddComponent<LODGroup>();
            group.SetLODs(levels.ToArray());
            group.localReferencePoint = (a + b) * 0.5f;
            group.size = Mathf.Max(Mathf.Abs(b.x - a.x), Mathf.Max(Mathf.Abs(b.y - a.y), Mathf.Abs(b.z - a.z)));
            t.RenderersPerLod = new int[levels.Count];
            for (int i = 0; i < levels.Count; i++) t.RenderersPerLod[i] = levels[i].renderers.Length;

            yield return true;

            // 6. The body's two boxes, fitted to LOD0's own vertices (FitBody), standing on the wheels' contact (the
            // wheel locators less the tyre radius: a wreck rests on its lower box, so it must reach the road), on the
            // Vehicle layer. A body that cannot be fitted keeps the model's bounds, as before.
            Vector3 lo3 = Vector3.Min(a, b), hi3 = Vector3.Max(a, b);
            float ground = float.MaxValue;

            foreach (KeyValuePair<string, Transform> entry in built0.Locators)
            {
                if (entry.Value != null && entry.Key.StartsWith("wheel_", StringComparison.Ordinal))
                {
                    ground = Math.Min(ground, root.transform.InverseTransformPoint(entry.Value.position).y - t.WheelRadius);
                }
            }

            if (ground == float.MaxValue || ground < lo3.y - 0.5f || ground > lo3.y + 0.5f)
            {
                ground = lo3.y;
            }

            BoxCollider box = root.AddComponent<BoxCollider>();

            if (FitBody(uses, lod0, notDrawn, ground, out Vector3 lowCentre, out Vector3 lowSize, out Vector3 cabinCentre, out Vector3 cabinSize))
            {
                box.center = lowCentre;
                box.size = lowSize;
                BoxCollider cabin = root.AddComponent<BoxCollider>();
                cabin.center = cabinCentre;
                cabin.size = cabinSize;
                cabin.enabled = false;
                float bottom = lowCentre.y - lowSize.y * 0.5f, top = cabinCentre.y + cabinSize.y * 0.5f;
                t.HullCentre = new Vector3(lowCentre.x, (bottom + top) * 0.5f, lowCentre.z);
                t.HullHalf = new Vector3(lowSize.x * 0.5f, (top - bottom) * 0.5f, lowSize.z * 0.5f);
                t.Belt = lowCentre.y + lowSize.y * 0.5f;
                t.Fitted = true;
            }
            else
            {
                box.center = (lo3 + hi3) * 0.5f;
                box.size = hi3 - lo3;
                t.HullCentre = box.center;
                t.HullHalf = box.size * 0.5f;
                t.Belt = lo3.y + 0.55f * (hi3.y - lo3.y);
            }

            box.enabled = false;

            // The weight sits low, as a car's does (a third of the way up): a box's own centre would roll it over at a touch.
            t.MassCentre = new Vector3(t.HullCentre.x, t.HullCentre.y - t.HullHalf.y * 0.4f, t.HullCentre.z);
            Rigidbody body = root.AddComponent<Rigidbody>();
            body.mass = Mathf.Max(300f, v.Mass);
            body.isKinematic = true;
            body.interpolation = RigidbodyInterpolation.Interpolate;
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            EtsLayers.SetRecursively(root.transform, EtsLayers.Vehicle);

            foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                Material material = renderer.sharedMaterial;
                if (material != null && IsPaintedPath(StripVcol(material.name))) t.PaintBase.Add(material);
            }

            // After the paint scan: the lamps' material is not a game material to look up.
            AddLamps(root.transform, t);

            // A step for the Destroy calls above to land before the first clone copies the tree.
            yield return true;
            t.Ready = true;
        }

        /// <summary>The pieces EtsModelBuilder.Build will draw, by its own rules (look, variants, no 'coll').</summary>
        private static void Visible(EtsModelData model, string lookName, string[] variantNames, List<PieceUse> into)
        {
            int look = Math.Max(0, model.LookIndex(lookName));
            var variants = new List<int>();

            foreach (string name in variantNames)
            {
                int index = model.VariantIndex(name.Trim());
                if (index >= 0 && !variants.Contains(index)) variants.Add(index);
            }

            if (variants.Count == 0 && model.Variants.Length > 0)
            {
                variants.Add(0);
            }

            string[] lookMaterials = look < model.LookMaterials.Length ? model.LookMaterials[look] : new string[0];

            foreach (EtsModelPart part in model.Parts)
            {
                bool visible = variants.Count == 0;
                foreach (int variant in variants) visible |= part.IsVisible(variant);

                if (!visible || part.Name == "coll")
                {
                    continue;
                }

                for (int p = part.PieceStart; p < part.PieceStart + part.PieceCount && p < model.Pieces.Length; p++)
                {
                    EtsModelPiece piece = model.Pieces[p];
                    into.Add(new PieceUse
                    {
                        Model = model,
                        Piece = piece,
                        Index = p,
                        Material = piece.MaterialIndex < lookMaterials.Length ? lookMaterials[piece.MaterialIndex] : null
                    });
                }
            }
        }

        /// <summary>
        /// The body's two boxes from LOD0's drawn vertices (root space; the wheels are separate models and not in it):
        /// the roof is the 99.5th percentile of height (a whip aerial is a handful of vertices), the belt line 55 % of the
        /// way up from the ground. The lower box is every vertex below the belt line - bumpers, doors, arches; the
        /// mirrors sit above it - from the ground up; the cabin is as wide and long as the vertices in the top 45 % above
        /// the belt line (above the mirrors), from the belt line to the roof, kept within the lower box's footprint.
        /// False for a body that does not look like a car (too few vertices, under 0.8 m or over 4 m high, under 1 x 2 m).
        /// </summary>
        private static bool FitBody(List<PieceUse> uses, EtsModelData lod0, HashSet<string> notDrawn, float ground,
                                    out Vector3 lowCentre, out Vector3 lowSize, out Vector3 cabinCentre, out Vector3 cabinSize)
        {
            lowCentre = lowSize = cabinCentre = cabinSize = Vector3.zero;
            int n = 0;

            foreach (PieceUse use in uses)
            {
                if (BodyPiece(use, lod0, notDrawn)) n += use.Piece.Positions.Length / 3;
            }

            if (n < 64)
            {
                return false;
            }

            var heights = new float[n];
            int count = 0;

            foreach (PieceUse use in uses)
            {
                if (!BodyPiece(use, lod0, notDrawn)) continue;
                float[] p = use.Piece.Positions;
                for (int k = 0; k + 2 < p.Length && count < n; k += 3) heights[count++] = p[k + 1];
            }

            float top = Quantile(heights, count, 0.995f);
            float height = top - ground;

            if (height < 0.8f || height > 4f)
            {
                return false;
            }

            float belt = ground + 0.55f * height;
            float roofFrom = belt + 0.55f * (top - belt);
            float lx0 = float.MaxValue, lx1 = float.MinValue, lz0 = float.MaxValue, lz1 = float.MinValue;
            float cx0 = float.MaxValue, cx1 = float.MinValue, cz0 = float.MaxValue, cz1 = float.MinValue;
            int lower = 0, upper = 0;

            foreach (PieceUse use in uses)
            {
                if (!BodyPiece(use, lod0, notDrawn)) continue;
                float[] p = use.Piece.Positions;

                for (int k = 0; k + 2 < p.Length; k += 3)
                {
                    // Unity's frame: z turned round (EtsModelBuilder.ToUnity).
                    float x = p[k], y = p[k + 1], z = -p[k + 2];

                    if (y < belt)
                    {
                        lx0 = Math.Min(lx0, x); lx1 = Math.Max(lx1, x);
                        lz0 = Math.Min(lz0, z); lz1 = Math.Max(lz1, z);
                        lower++;
                    }
                    else if (y > roofFrom && y <= top)
                    {
                        cx0 = Math.Min(cx0, x); cx1 = Math.Max(cx1, x);
                        cz0 = Math.Min(cz0, z); cz1 = Math.Max(cz1, z);
                        upper++;
                    }
                }
            }

            if (lower < 32 || upper < 16 || lx1 - lx0 < 1f || lz1 - lz0 < 2f)
            {
                return false;
            }

            cx0 = Math.Max(cx0, lx0); cx1 = Math.Min(cx1, lx1);
            cz0 = Math.Max(cz0, lz0); cz1 = Math.Min(cz1, lz1);

            if (cx1 - cx0 < 0.5f || cz1 - cz0 < 0.8f)
            {
                return false;
            }

            // Two centimetres off the road: made dynamic, a wreck settles onto it rather than being pushed up out of it.
            float bottom = ground + 0.02f;
            lowCentre = new Vector3((lx0 + lx1) * 0.5f, (bottom + belt) * 0.5f, (lz0 + lz1) * 0.5f);
            lowSize = new Vector3(lx1 - lx0, belt - bottom, lz1 - lz0);
            cabinCentre = new Vector3((cx0 + cx1) * 0.5f, (belt + top) * 0.5f, (cz0 + cz1) * 0.5f);
            cabinSize = new Vector3(cx1 - cx0, top - belt, cz1 - cz0);
            return true;
        }

        private static bool BodyPiece(PieceUse use, EtsModelData lod0, HashSet<string> notDrawn)
        {
            return use.Model == lod0 && use.Piece != null && use.Piece.Positions != null && (use.Material == null || !notDrawn.Contains(use.Material));
        }

        /// <summary>The q-quantile of the first 'count' values (reorders them): Hoare's selection, linear on average.</summary>
        public static float Quantile(float[] values, int count, float q)
        {
            int k = Mathf.Clamp((int)(q * (count - 1)), 0, count - 1);
            int lo = 0, hi = count - 1;

            while (lo < hi)
            {
                float pivot = values[(lo + hi) >> 1];
                int i = lo, j = hi;

                while (i <= j)
                {
                    while (values[i] < pivot) i++;
                    while (values[j] > pivot) j--;

                    if (i <= j)
                    {
                        float swap = values[i];
                        values[i] = values[j];
                        values[j] = swap;
                        i++;
                        j--;
                    }
                }

                if (k <= j) hi = j;
                else if (k >= i) lo = i;
                else break;
            }

            return values[k];
        }

        // ---------------------------------------------------------------- hazard lamps

        private static Mesh _lampMesh;
        private static Material _lampMaterial;
        private static bool _lampMissing;

        /// <summary>
        /// Four amber lamps at the lower box's corners (front and back, a little under the belt line), off until the car
        /// is a wreck (SetLamps). Not in the LOD group: small, and lit only on wrecks. None when the client has no URP
        /// shader for them.
        /// </summary>
        private static void AddLamps(Transform root, Template t)
        {
            Material material = LampMaterial();

            if (material == null)
            {
                return;
            }

            Mesh mesh = LampMesh();
            float bottom = t.HullCentre.y - t.HullHalf.y;
            float y = bottom + 0.8f * (t.Belt - bottom);
            float side = Mathf.Max(0.2f, t.HullHalf.x - 0.15f);

            for (int k = 0; k < 4; k++)
            {
                var lamp = new GameObject(LampName) { layer = EtsLayers.Vehicle };
                lamp.SetActive(false);
                lamp.transform.SetParent(root, false);
                float z = t.HullCentre.z + (k < 2 ? 1f : -1f) * (t.HullHalf.z - 0.03f);
                lamp.transform.localPosition = new Vector3(t.HullCentre.x + (k % 2 == 0 ? -side : side), y, z);
                lamp.transform.localScale = new Vector3(0.18f, 0.08f, 0.08f);
                lamp.AddComponent<MeshFilter>().sharedMesh = mesh;
                MeshRenderer renderer = lamp.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = material;
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = false;
            }
        }

        private static Mesh LampMesh()
        {
            if (_lampMesh != null)
            {
                return _lampMesh;
            }

            var corners = new Vector3[8];

            for (int i = 0; i < 8; i++)
            {
                corners[i] = new Vector3((i & 1) != 0 ? 0.5f : -0.5f, (i & 2) != 0 ? 0.5f : -0.5f, (i & 4) != 0 ? 0.5f : -0.5f);
            }

            _lampMesh = new Mesh
            {
                name = "traffic hazard lamp",
                vertices = corners,
                triangles = new[] { 0, 2, 3, 0, 3, 1, 4, 5, 7, 4, 7, 6, 0, 1, 5, 0, 5, 4, 2, 6, 7, 2, 7, 3, 0, 4, 6, 0, 6, 2, 1, 3, 7, 1, 7, 5 }
            };

            _lampMesh.RecalculateBounds();
            return _lampMesh;
        }

        /// <summary>URP Unlit amber, both faces (as the traffic lights' lenses, EtsSemaphores.LensMaterial).</summary>
        private static Material LampMaterial()
        {
            if (_lampMaterial != null || _lampMissing)
            {
                return _lampMaterial;
            }

            Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) shader = Shader.Find("Universal Render Pipeline/Simple Lit");
            if (shader == null) shader = Shader.Find("Universal Render Pipeline/Lit");

            if (shader == null)
            {
                _lampMissing = true;
                Debug.LogWarning("[SomniumEuroTruck] Traffic: no URP shader for the cars' hazard lamps; wrecks show none.");
                return null;
            }

            var amber = new Color(1f, 0.55f, 0.05f, 1f);
            _lampMaterial = new Material(shader) { name = "traffic hazard lamp" };
            if (_lampMaterial.HasProperty("_BaseColor")) _lampMaterial.SetColor("_BaseColor", amber);
            if (_lampMaterial.HasProperty("_Color")) _lampMaterial.SetColor("_Color", amber);
            if (_lampMaterial.HasProperty("_Cull")) _lampMaterial.SetFloat("_Cull", 0f);

            if (_lampMaterial.HasProperty("_EmissionColor"))
            {
                _lampMaterial.EnableKeyword("_EMISSION");
                _lampMaterial.SetColor("_EmissionColor", amber * 2f);
            }

            return _lampMaterial;
        }

        /// <summary>The hazard lamps on or off (EtsTraffic blinks them on wrecks).</summary>
        public static void SetLamps(Visual visual, bool lit)
        {
            if (visual == null || visual.LampsLit == lit)
            {
                return;
            }

            visual.LampsLit = lit;

            foreach (GameObject lamp in visual.Lamps)
            {
                if (lamp != null) lamp.SetActive(lit);
            }
        }

        // ---------------------------------------------------------------- solid, kinematic, dynamic

        /// <summary>Both boxes on or off.</summary>
        public static void SetSolid(Visual visual, bool solid)
        {
            if (visual.Box != null && visual.Box.enabled != solid) visual.Box.enabled = solid;
            if (visual.Cabin != null && visual.Cabin.enabled != solid) visual.Cabin.enabled = solid;
        }

        public static bool IsSolid(Visual visual)
        {
            return visual.Box != null && visual.Box.enabled;
        }

        /// <summary>
        /// A hit car: physics moves it from here, with its own mass and a low centre of mass, at the velocity it had
        /// (a truck's contact then shares the momentum by the real masses instead of stopping dead against an immovable
        /// box). Depenetration is held to 3 m/s, so a car switched while touching is eased out, not fired off.
        /// </summary>
        public static void MakeDynamic(Visual visual, Vector3 velocity, Vector3 angularVelocity)
        {
            Rigidbody body = visual.Body;

            if (body == null || visual.Dynamic)
            {
                return;
            }

            SetSolid(visual, true);
            visual.Dynamic = true;
            body.mass = Mathf.Max(300f, visual.Mass);
            body.linearDamping = 0.05f;
            body.angularDamping = 0.5f;
            body.maxDepenetrationVelocity = 3f;
            body.useGravity = true;
            body.isKinematic = false;
            body.centerOfMass = visual.MassCentre;
            body.linearVelocity = Vector3.ClampMagnitude(velocity, 40f);
            body.angularVelocity = Vector3.ClampMagnitude(angularVelocity, 3f);
            body.WakeUp();
        }

        /// <summary>Back under the traffic's authority (a wreck that drives on, is handed back, or is pooled).</summary>
        public static void MakeKinematic(Visual visual)
        {
            if (!visual.Dynamic)
            {
                return;
            }

            visual.Dynamic = false;
            Rigidbody body = visual.Body;

            if (body != null)
            {
                body.linearVelocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
                body.isKinematic = true;
            }
        }

        /// <summary>For the prewarm log: the fitted lower box's width x length and the roof's height, metres ("bounds" when not fitted).</summary>
        public string FitSummary(int vehicle)
        {
            if (!_templates.TryGetValue(vehicle, out Template t) || !t.Ready)
            {
                return "";
            }

            return (t.HullHalf.x * 2f).ToString("0.00") + "x" + (t.HullHalf.z * 2f).ToString("0.00") + "/" + (t.HullCentre.y + t.HullHalf.y).ToString("0.00") + (t.Fitted ? "" : " bounds");
        }

        private static void MakeMesh(PieceUse use)
        {
            EtsMaterialDef def = EtsEngine.Materials.GetDefinition(use.Material);

            // Exactly the call EtsModelBuilder.Build makes, so it finds this mesh in its cache.
            if (def != null && def.EffectHas(".tg0"))
            {
                EtsModelBuilder.BuildTexgenMesh(use.Piece, "piece " + use.Index, def);
            }
            else
            {
                EtsModelBuilder.BuildMesh(use.Piece, "piece " + use.Index, EtsMaterialCache.BaseUvChannel(def, use.Piece.UvChannels));
            }
        }

        /// <summary>A model already in the cache (read by the worker), or null: never a read on the frame.</summary>
        private static EtsModelData Loaded(string path)
        {
            return !string.IsNullOrEmpty(path) && EtsEngine.IsModelLoaded(path) ? EtsEngine.LoadModel(path, out _) : null;
        }

        private void HangWheels(Template t, EtsModelBuilder.Result built, EtsTrafficCatalog.Vehicle v)
        {
            EtsModelData front = Loaded(v.FrontWheel);
            EtsModelData rear = string.IsNullOrEmpty(v.RearWheel) ? front : Loaded(v.RearWheel);

            if (front != null && front.BoundsMax[1] - front.BoundsMin[1] > 0.2f)
            {
                t.WheelRadius = (front.BoundsMax[1] - front.BoundsMin[1]) * 0.5f;
            }

            foreach (KeyValuePair<string, Transform> entry in built.Locators)
            {
                if (!entry.Key.StartsWith("wheel_", StringComparison.Ordinal))
                {
                    continue;
                }

                bool isRear = entry.Key.StartsWith("wheel_r", StringComparison.Ordinal);
                EtsModelData wheel = isRear ? rear : front;

                if (wheel == null)
                {
                    continue;
                }

                // As EtsTruckFactory.HangWheels: the wheel model carries left and right variants,
                // the locator's x sign says which (the tourer's front right is wheel_f_4).
                string side = entry.Value.localPosition.x < 0f ? "left" : "right";
                var spin = new GameObject(isRear ? SpinRear : SpinFront).transform;
                spin.SetParent(entry.Value, false);
                BuildWhite(wheel, new EtsModelBuilder.Options { Variants = new[] { side, "default" }, CreateLocators = false, CastShadows = true }, spin);
            }
        }

        // ---------------------------------------------------------------- paint

        /// <summary>
        /// PaintColor white for exactly one call and back at once - never across a frame, so the
        /// truck (or anything else) built in between still gets its own colour. The cache keys
        /// truckpaint materials by the colour as well (EtsMaterialCache.Load), so even a truck
        /// built during the call would not share the car's material.
        /// </summary>
        private static Material LoadWhite(string material)
        {
            Color was = EtsMaterialCache.PaintColor;
            EtsMaterialCache.PaintColor = Color.white;

            try
            {
                return EtsEngine.Materials.Load(material);
            }
            finally
            {
                EtsMaterialCache.PaintColor = was;
            }
        }

        private static EtsModelBuilder.Result BuildWhite(EtsModelData model, EtsModelBuilder.Options options, Transform parent)
        {
            Color was = EtsMaterialCache.PaintColor;
            EtsMaterialCache.PaintColor = Color.white;

            try
            {
                return EtsModelBuilder.Build(model, options, parent, EtsEngine.Materials);
            }
            finally
            {
                EtsMaterialCache.PaintColor = was;
            }
        }

        private static string StripVcol(string name)
        {
            return name != null && name.EndsWith(" vcol", StringComparison.Ordinal) ? name.Substring(0, name.Length - 5) : name;
        }

        private static bool IsPaintedPath(string materialPath)
        {
            EtsMaterialDef def = string.IsNullOrEmpty(materialPath) ? null : EtsEngine.Materials.GetDefinition(materialPath);
            return def != null && def.EffectHas("truckpaint");
        }

        /// <summary>The car's colour over its white-painted materials; one material per (material, colour).</summary>
        private void Paint(Visual visual, int colour)
        {
            if (visual.Colour == colour)
            {
                return;
            }

            visual.Colour = colour;
            EtsTrafficCatalog.Vehicle v = _catalog.Vehicles[visual.Vehicle];
            float[] rgb = colour >= 0 && colour < v.Colours.Length ? v.Colours[colour] : null;

            for (int i = 0; i < visual.Painted.Count; i++)
            {
                Material basis = visual.PaintBase[i];

                if (rgb == null)
                {
                    visual.Painted[i].sharedMaterial = basis;
                    continue;
                }

                long key = ((long)basis.GetInstanceID() << 8) | (uint)(colour & 0xFF);

                if (!_paint.TryGetValue(key, out Material painted) || painted == null)
                {
                    painted = new Material(basis) { name = basis.name + " paint " + colour };

                    if (painted.HasProperty(BaseColor))
                    {
                        // The file's colours are linear (as the truck's paint is taken to be,
                        // README 'Colour'); multiply in linear space and hand Unity gamma.
                        Color was = painted.GetColor(BaseColor).linear;
                        var linear = new Color(was.r * rgb[0], was.g * rgb[1], was.b * rgb[2], 1f);
                        Color gamma = linear.gamma;
                        gamma.a = painted.GetColor(BaseColor).a;
                        painted.SetColor(BaseColor, gamma);
                    }

                    _paint[key] = painted;
                }

                visual.Painted[i].sharedMaterial = painted;
            }
        }

        // ---------------------------------------------------------------- the worker's side

        private void AskModel(string path)
        {
            if (string.IsNullOrEmpty(path) || EtsEngine.IsModelLoaded(path) || !_modelsAsked.Add(path))
            {
                return;
            }

            EtsFileSystem files = EtsEngine.Files;

            if (_worker == null)
            {
                if (EtsEngine.LoadModel(path, out string failure) == null) ModelFailed(path, failure);
                return;
            }

            // The job hands back the model or ReadModel's own reason ('out _' used to drop it). Its
            // body stays in the lambda: a static method of this class would be a first touch of
            // its statics (Shader.PropertyToID) that could land on the worker thread.
            _worker.Post(() =>
            {
                EtsModelData read = EtsEngine.ReadModel(files, path, out string failure);
                return (object)read ?? failure ?? "no reason given";
            }, (result, error) =>
            {
                EtsModelData model = result as EtsModelData;

                if (!EtsEngine.IsMounted)
                {
                    // Unmounted meanwhile (leaving the world): nothing to report.
                    _modelsFailed.Add(path);
                    return;
                }

                if (model == null)
                {
                    ModelFailed(path, error != null ? error.GetType().Name + ": " + error.Message : result as string);
                    return;
                }

                EtsEngine.AdoptModel(path, model);
            });
        }

        /// <summary>
        /// Once per path (each is asked once, _modelsAsked): a failed lod or wheel is otherwise
        /// only seen as a car missing a lod or its wheels, a failed chassis as "could not be read".
        /// </summary>
        private void ModelFailed(string path, string why)
        {
            if (_modelsFailed.Add(path))
            {
                Debug.LogWarning("[SomniumEuroTruck] Traffic: car model file " + path + " could not be read (" + (why ?? "no reason given") + ").");
            }
        }

        private bool AllModels(List<string> paths)
        {
            foreach (string path in paths)
            {
                if (!EtsEngine.IsModelLoaded(path) && !_modelsFailed.Contains(path)) return false;
            }

            return true;
        }

        /// <summary>
        /// The textures EtsMaterialCache.Build will load for this material - base (with its
        /// reflection-mask plane where the material is masked), second layer, normal map - asked
        /// of the worker. Materials Build skips (shadow-only, night, helpers) ask for nothing.
        /// </summary>
        private void AskTextures(EtsMaterialDef def)
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

            AskTexture(def.BaseTexture, false, masked);

            string second = EtsMaterialCache.SecondLayer(def, out _);
            if (second != null && !def.EffectHas("decal.over")) AskTexture(second, false, false);

            // amod's decal mask (layer mode 5, EtsMaterialCache.Build), so it is not read on the frame.
            if (second != null && def.EffectHas("amod") && !def.EffectHas("iamod") && def.Textures.TryGetValue("texture_mask", out string amodMask)) AskTexture(amodMask, false, false);

            if (def.NormalTexture != null && !def.EffectHas("tsnmapuv")) AskTexture(def.NormalTexture, true, false);
        }

        private void AskTexture(string tobjPath, bool linear, bool withMask)
        {
            if (string.IsNullOrEmpty(tobjPath))
            {
                return;
            }

            // A texture already uploaded is asked for again only for its mask plane (Adopt then keeps
            // the plane and hands back the texture it has), so the mask is never read on the frame.
            string key = tobjPath + (linear ? "|linear" : "") + (withMask ? "|mask" : "");

            if ((!withMask && EtsEngine.Textures.IsLoaded(tobjPath, linear)) || !_texturesAsked.Add(key))
            {
                return;
            }

            if (_worker == null)
            {
                return; // EtsMaterialCache.Load will read it on the main thread.
            }

            EtsFileSystem files = EtsEngine.Files;
            _texturesPending.Add(key);
            _worker.Post(() => EtsTextureCache.Prepare(files, tobjPath, linear, withMask), (result, error) =>
            {
                _texturesPending.Remove(key);

                if (result is EtsTextureCache.Prepared prepared && EtsEngine.IsMounted)
                {
                    // A texture that could not be read says so in Prepared.Failure, which the cache reports.
                    EtsEngine.Textures.Adopt(prepared);
                }
                else if (error != null && EtsEngine.IsMounted)
                {
                    // Once per key (_texturesAsked). Only a throw lands here, and nothing else would say.
                    Debug.LogWarning("[SomniumEuroTruck] Traffic: texture " + tobjPath + " could not be prepared (" + error.GetType().Name + ": " + error.Message + "); the material reads it on the frame.");
                }
            });
        }

        // ---------------------------------------------------------------- cars on the road

        /// <summary>
        /// A car to show: a pooled one of this model, or a new clone of its template. Null when the
        /// template is not ready yet (it is queued) or this frame has shown its share; the caller
        /// asks again next step.
        /// </summary>
        public Visual Take(int vehicle, int colour)
        {
            if (!_templates.TryGetValue(vehicle, out Template t))
            {
                Want(vehicle);
                return null;
            }

            if (!t.Ready || t.Root == null)
            {
                return null;
            }

            if (_frame != Time.frameCount)
            {
                _frame = Time.frameCount;
                _shownThisFrame = 0;
                _newThisFrame = 0;
            }

            if (_shownThisFrame >= MaxShownPerFrame)
            {
                return null;
            }

            Visual visual = null;

            if (_pool.TryGetValue(vehicle, out Stack<Visual> stack))
            {
                while (stack.Count > 0 && visual == null)
                {
                    visual = stack.Pop();
                    if (visual.Root == null) visual = null;
                }
            }

            if (visual == null)
            {
                if (_newThisFrame >= MaxNewPerFrame)
                {
                    return null;
                }

                visual = Clone(t);
                _newThisFrame++;
            }

            _shownThisFrame++;
            Paint(visual, colour);
            visual.Root.SetActive(true);
            return visual;
        }

        private Visual Clone(Template t)
        {
            GameObject root = UnityEngine.Object.Instantiate(t.Root, _parent, false);
            root.name = t.Root.name;
            BoxCollider[] boxes = root.GetComponents<BoxCollider>();
            BoxCollider lowerBox = boxes.Length > 0 ? boxes[0] : null, cabinBox = boxes.Length > 1 ? boxes[1] : null;

            if (cabinBox != null && cabinBox.center.y < lowerBox.center.y)
            {
                BoxCollider swap = lowerBox;
                lowerBox = cabinBox;
                cabinBox = swap;
            }

            var visual = new Visual
            {
                Vehicle = t.Vehicle,
                Root = root,
                Body = root.GetComponent<Rigidbody>(),
                Box = lowerBox,
                Cabin = cabinBox,
                Lods = root.GetComponent<LODGroup>(),
                WheelRadius = t.WheelRadius,
                Mass = Mathf.Max(300f, _catalog.Vehicles[t.Vehicle].Mass),
                MassCentre = t.MassCentre,
                HullCentre = t.HullCentre,
                HullHalf = t.HullHalf
            };

            var lamps = new List<GameObject>(4);

            foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
            {
                if (child.name == SpinFront || child.name == SpinRear)
                {
                    visual.Wheels.Add(child);
                    visual.Steers.Add(child.name == SpinFront);
                }
                else if (child.name == LampName)
                {
                    lamps.Add(child.gameObject);
                }
            }

            visual.Lamps = lamps.ToArray();

            foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer.sharedMaterial != null && t.PaintBase.Contains(renderer.sharedMaterial))
                {
                    visual.Painted.Add(renderer);
                    visual.PaintBase.Add(renderer.sharedMaterial);
                }
            }

            Built++;
            return visual;
        }

        public void Give(Visual visual)
        {
            if (visual == null || visual.Root == null)
            {
                return;
            }

            // A wreck goes back to the pool as any car: kinematic, dark, not solid.
            MakeKinematic(visual);
            SetLamps(visual, false);
            visual.Root.SetActive(false);
            SetSolid(visual, false);

            if (!_pool.TryGetValue(visual.Vehicle, out Stack<Visual> stack))
            {
                stack = new Stack<Visual>();
                _pool[visual.Vehicle] = stack;
            }

            stack.Push(visual);
        }

        /// <summary>Renderers per lod of a ready template, for the log ("" when not ready).</summary>
        public string RendererCounts(int vehicle)
        {
            return _templates.TryGetValue(vehicle, out Template t) && t.Ready ? string.Join("/", Array.ConvertAll(t.RenderersPerLod, n => n.ToString())) : "";
        }

        /// <summary>Turns the wheels by the distance driven and the front ones by the bend.</summary>
        public static void Roll(Visual visual, float speed, float yawRateDegrees, float dt)
        {
            visual.Spin = (visual.Spin + speed * dt / Mathf.Max(0.1f, visual.WheelRadius) * Mathf.Rad2Deg) % 360f;
            float steer = speed > 0.5f ? Mathf.Clamp(yawRateDegrees * 2.6f / Mathf.Max(speed, 1f), -35f, 35f) : 0f;

            for (int i = 0; i < visual.Wheels.Count; i++)
            {
                visual.Wheels[i].localRotation = Quaternion.Euler(0f, visual.Steers[i] ? steer : 0f, 0f) * Quaternion.Euler(visual.Spin, 0f, 0f);
            }
        }

        /// <summary>Everything this made: clones, templates, paint copies (the shared meshes and materials stay, they are the world's).</summary>
        public void Destroy()
        {
            foreach (Template t in _templates.Values)
            {
                if (t.Root != null) UnityEngine.Object.Destroy(t.Root);
            }

            foreach (Stack<Visual> stack in _pool.Values)
            {
                foreach (Visual visual in stack) if (visual.Root != null) UnityEngine.Object.Destroy(visual.Root);
            }

            foreach (Material material in _paint.Values)
            {
                if (material != null) UnityEngine.Object.Destroy(material);
            }

            if (_lampMaterial != null) UnityEngine.Object.Destroy(_lampMaterial);
            if (_lampMesh != null) UnityEngine.Object.Destroy(_lampMesh);
            _lampMaterial = null;
            _lampMesh = null;

            _templates.Clear();
            _queue.Clear();
            _pool.Clear();
            _paint.Clear();
        }
    }
}
