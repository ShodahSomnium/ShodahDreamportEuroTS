using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace SomniumSpace.Worlds.SomniumEuroTruck.Ets
{
    /// <summary>
    /// Trees and bushes beside the roads, from the vegetation bands the map stores per road side
    /// and terrain item: each band names a vegetation set (a list of vegetation models, each with
    /// a relative weight), a density and the distances from the road it covers. A plant is drawn
    /// from the set by those weights. Plants are scattered with the item's own random
    /// seed, so the same road grows the same trees every time, and stood on the terrain by a ray
    /// cast after the strip has been built. The game's density unit is not documented; its values
    /// (u16 / 10, 450-550 on 70+ of 82 bands round Dover) are read as plants per hectare, times DensityScale.
    /// Drawn by EtsTreeField with GPU instancing (hi-poly within the band's high_poly_distance, med-poly to the
    /// game's 240-300 m band, sprites beyond, near models casting sun shadows); where instancing is unavailable or
    /// fails, the old path: one GameObject per plant, med-poly near and the sprite beyond in a LODGroup. Both are cut
    /// out by Shaders/EtsLeaves.shader, which raises its discard with distance (LogCutOut).
    ///
    /// <b>Full density, instanced (trees owner, 2026-09-25).</b> Thinning 0.2 and 60 plants a band held the world to
    /// 14-20 % of the map's trees (Dover 2,596 of 15,721 within 1.2 km, 17 of 82 bands at the cap; mean spacing
    /// 11.2 m where the map asks for 4.6 m and the crowns are 8.7 m wide; N/veg/v2_dover.txt), so belts became lines
    /// of single trees. Every draw a plant takes (along, across, species, scale, yaw) is now made before any test that
    /// can give up on it, in the same order as before, so whether a point finds ground - which can depend on what
    /// has streamed in - no longer shifts the draws of the plants after it: every client grows the same trees.
    ///
    /// <b>Planted by distance, a little a frame (final pass, 2026-09-23).</b> Scatter no longer plants: it queues
    /// the side, and <see cref="Pump"/> (run every frame by EtsVegetationStream) plants the sides within
    /// <see cref="Radius"/> of the stream's focus, nearest first, under <see cref="FrameBudgetMs"/> - one plant is
    /// two model builds and a LODGroup, and a road side's bands came to up to 120 of them inside the frame that built
    /// the road. The map now streams further than trees need: round the start (probe Ter1) the 0-1.2 km ring asks
    /// for 2,596 plants and the 1.2-2.2 km ring for 4,746 more (Cambridge 1,769 and 5,180), in a headset that draws
    /// every one twice. So trees stay within <see cref="Radius"/> (1.2 km, the area planted before), sides further
    /// out wait until the focus comes within reach, and planted sides left more than <see cref="HideBeyond"/> behind
    /// are switched off (and on again on the way back) instead of piling up out to the unload ring.
    ///
    /// A plant stands on ITS OWN side's ground: the highest hit on a collider under the side's own item. The first
    /// hit from 80 m up, as before, could be a roof or a bridge deck, a neighbouring item's strip whose height depends
    /// on which of the two streamed in first, or the ground fill's guess over ground not built yet - a tree floating
    /// over the verge. Only when the side has no collider under the point is the highest other ground (never the
    /// fill) used. The random draws are unchanged and in the same order, so the same trees grow where they grew.
    /// </summary>
    public static class EtsVegetationBuilder
    {
        /// <summary>
        /// Fraction of the map's density planted (was Thinning 0.2 with 60 plants a band at most). The same on every
        /// client - it decides where trees stand - so it is a constant of the world, never a per-client setting.
        /// </summary>
        public static float DensityScale = 1f;

        /// <summary>Plants a band at most: a safety cap far above any band of the demo's map (the largest near Dover asks for ~1,500).</summary>
        public const int BandCap = 5000;

        /// <summary>Screen-height fraction below which a tree swaps to its 6-triangle sprite model (the GameObject path).</summary>
        public const float SpriteBelowScreenHeight = 0.04f;

        /// <summary>
        /// Road and terrain sides this close to the stream's focus (horizontally, nearest reach) are planted on the
        /// GameObject path at the old density 0.2; shrunk by sqrt(0.2 / DensityScale) there so its plant count stays
        /// what it was (<see cref="PlantRadius"/>).
        /// </summary>
        public static float Radius = 1200f;

        /// <summary>Planted sides further than this are switched off until the focus comes back within Radius + 100 m (GameObject path).</summary>
        public static float HideBeyond = 1600f;

        /// <summary>Sides this close are planted when EtsTreeField draws them: the land's own reach (EtsMapViewer far radius 2150 m).</summary>
        public static float InstancedRadius = 2150f;

        /// <summary>Whether the trees are drawn instanced (EtsTreeField) this visit; decided with the first side.</summary>
        public static bool Instanced => Mode() == ModeInstanced;

        /// <summary>The planting radius of the path in use.</summary>
        public static float PlantRadius => Instanced ? InstancedRadius : Radius * GameObjectShrink;

        private static float HideRadius => Instanced ? float.MaxValue : HideBeyond * GameObjectShrink;

        private static float GameObjectShrink => Mathf.Clamp(Mathf.Sqrt(0.2f / Mathf.Max(0.05f, DensityScale)), 0.25f, 1f);

        private const int ModeUndecided = 0, ModeInstanced = 1, ModeGameObjects = 2;
        private static int _mode;
        private static int _speciesFrame = -1;
        private static bool _waitingForSpecies;

        /// <summary>Whether every species of the job's current band is made; makes at most one new one a frame.</summary>
        private static bool SpeciesReady(Job job)
        {
            for (int i = 0; i < job.ModelUnits.Count; i++)
            {
                string unit = job.ModelUnits[i];

                if (EtsTreeField.Knows(unit))
                {
                    continue;
                }

                if (_speciesFrame == Time.frameCount)
                {
                    return false;
                }

                _speciesFrame = Time.frameCount;
                EtsTreeField.KindFor(job.Map, unit);
            }

            return true;
        }

        /// <summary>Milliseconds of planting and switching a frame at most (one plant always goes in).</summary>
        public static float FrameBudgetMs = 1.0f;

        /// <summary>The same while the session loads.</summary>
        public static float LoadingBudgetMs = 3f;

        private static readonly Dictionary<string, EtsModelData> ModelsByUnit = new Dictionary<string, EtsModelData>();

        /// <summary>One road side or terrain side's bands: queued by Scatter, planted by Pump.</summary>
        private sealed class Job
        {
            public EtsMap Map;
            public EtsTerrainSide Side;
            public float StartOffset, Length, Sign;
            public Vector3 P0, T0, P1, T1;
            public Transform Parent;
            public EtsMapBuilder.Stats Stats;
            public Vector3 Centre;
            public float Reach;
            public System.Random Random;
            public int Seed;

            // Progress through the bands.
            public int Band = -1;
            public int PlantIndex, PlantCount;
            public List<string> ModelUnits;
            public int[] Cumulative;
            public int TotalWeight;
            public float From, To;
            public float HighPoly;
            public bool KindsReady;

            // The GameObject path.
            public readonly List<GameObject> Plants = new List<GameObject>();
            public bool Shown = true;
            public int ToggleIndex;

            // The instanced path: the side's trees, handed to EtsTreeField when the side is done.
            public EtsTreeField.Stand Stand;
        }

        private static readonly List<Job> Pending = new List<Job>();
        private static readonly List<Job> Active = new List<Job>();
        private static readonly List<Job> Planted = new List<Job>();
        private static readonly List<Job> InField = new List<Job>();
        private static readonly Stopwatch Clock = new Stopwatch();
        private static readonly RaycastHit[] Hits = new RaycastHit[24];
        private static float _promotedAt = -10f, _reportedAt;
        private static int _visIndex, _reportedPlants = -1;
        private static int _plants, _ownGround, _otherGround, _noGround, _hidden, _queued, _failures;
        private static double _slowestMs;

        /// <summary>Plants standing now (planted this visit and not unloaded with their item): GameObjects plus instanced trees.</summary>
        public static int PlantsPlanted => _plants + EtsTreeField.TreeCount;

        /// <summary>The path this visit draws its trees on: instanced unless the machine cannot (logged once).</summary>
        private static int Mode()
        {
            if (_mode != ModeUndecided)
            {
                return _mode;
            }

            string why = EtsTreeField.Unavailable();

            if (why == null)
            {
                _mode = ModeInstanced;
            }
            else
            {
                _mode = ModeGameObjects;
                Debug.LogWarning("[SomniumEuroTruck] Trees: GPU instancing unavailable (" + why + "); one GameObject per plant (the old path) within " +
                                 (Radius * GameObjectShrink).ToString("0") + " m.");
            }

            return _mode;
        }

        /// <summary>
        /// Instanced drawing failed: every side planted or planting into the field is planted again from its own seed
        /// as GameObjects (the same draws, so the same trees), and the field is dropped.
        /// </summary>
        private static void FallBack()
        {
            _mode = ModeGameObjects;
            int again = 0;

            foreach (Job job in InField)
            {
                if (job.Parent != null && !Active.Contains(job))
                {
                    Active.Add(job);
                }
            }

            foreach (Job job in Active)
            {
                job.Random = new System.Random(job.Seed);
                job.Band = -1;
                job.PlantIndex = 0;
                job.PlantCount = 0;
                job.Stand = null;
                again++;
            }

            InField.Clear();
            EtsTreeField.Reset();
            Debug.LogWarning("[SomniumEuroTruck] Trees: planting " + again + " sides again as one GameObject per plant (the old path) within " +
                             (Radius * GameObjectShrink).ToString("0") + " m.");
        }

        /// <summary>
        /// This frame's instanced tree draws (EtsVegetationStream.Update, after Pump): round the screen camera of the last
        /// frame (EtsVegetationLook.ViewPosition), else the stream's focus. Falls back to GameObjects if a draw throws.
        /// </summary>
        public static void DrawTrees(Vector3 focus)
        {
            if (_mode != ModeInstanced)
            {
                return;
            }

            Vector3 view = EtsVegetationLook.ViewFrame >= Time.frameCount - 3 ? EtsVegetationLook.ViewPosition : focus;

            if (!EtsTreeField.Draw(view))
            {
                FallBack();
            }
        }

        /// <summary>Sides queued and not yet within reach.</summary>
        public static int SidesWaiting => Pending.Count;

        public static void Scatter(EtsMap map, EtsTerrainSide side, bool left, float startOffset, float length, uint seed,
            Vector3 p0, Vector3 t0, Vector3 p1, Vector3 t1, EtsMapBuilder.Options options, Transform parent, EtsMapBuilder.Stats stats)
        {
            if (!options.Vegetation || side == null || side.Size <= 0.5f || side.Vegetation == null || length < 5f || parent == null)
            {
                return;
            }

            bool any = false;

            foreach (EtsVegetationRecord band in side.Vegetation)
            {
                any |= band != null && band.Token != 0 && band.Density > 0f;
            }

            if (!any)
            {
                return;
            }

            // The side's middle and how far its plants can lie from it: half the chord plus the side's width.
            Vector3 middle = EtsMapBuilder.Hermite(p0, t0, p1, t1, 0.5f);
            float reach = (p1 - p0).magnitude * 0.5f + startOffset + side.Size;
            int jobSeed = (int)(seed ^ (left ? 0x9E3779B9u : 0x7F4A7C15u));
            Mode();

            Pending.Add(new Job
            {
                Map = map,
                Side = side,
                StartOffset = startOffset,
                Length = length,
                Sign = left ? -1f : 1f,
                P0 = p0, T0 = t0, P1 = p1, T1 = t1,
                Parent = parent,
                Stats = stats,
                Centre = parent.TransformPoint(middle),
                Reach = reach,
                Seed = jobSeed,
                Random = new System.Random(jobSeed)
            });

            _queued++;
            EtsVegetationStream.Ensure();
        }

        /// <summary>Drops every queued and planted side (the driver went with its scene; the statics outlive a visit).</summary>
        public static void Forget()
        {
            Pending.Clear();
            Active.Clear();
            Planted.Clear();
            InField.Clear();
            EtsTreeField.Reset();
            SoftByCollider.Clear();
            _mode = ModeUndecided;
            _plants = 0;
            _hidden = 0;
            _visIndex = 0;
            _queued = 0;
            _reportedPlants = -1;
        }

        private static float Distance(Job job, Vector3 focus)
        {
            float dx = job.Centre.x - focus.x, dz = job.Centre.z - focus.z;
            return Mathf.Sqrt(dx * dx + dz * dz) - job.Reach;
        }

        /// <summary>
        /// One frame of planting, from EtsVegetationStream.Update: sides within Radius of <paramref name="focus"/>
        /// (world) are planted nearest first, then planted sides are switched off beyond HideBeyond and on again
        /// within Radius + 100 m - all under this frame's budget.
        /// </summary>
        public static void Pump(Vector3 focus, bool loading)
        {
            Clock.Restart();
            float budget = loading ? LoadingBudgetMs : FrameBudgetMs;
            float now = Time.unscaledTime;

            if (now - _promotedAt >= 0.25f)
            {
                _promotedAt = now;
                Promote(focus);

                // Sides whose item was unloaded leave the fallback list (the field drops their trees on its own), or a
                // long drive would keep every side's tree list alive.
                for (int i = InField.Count - 1; i >= 0; i--)
                {
                    if (InField[i].Parent == null)
                    {
                        InField[i] = InField[InField.Count - 1];
                        InField.RemoveAt(InField.Count - 1);
                    }
                }
            }

            // Planting takes three quarters of the frame's share at most; switching the rest.
            float plantBudget = budget * 0.75f;

            while (Active.Count > 0)
            {
                Job job = Active[Active.Count - 1];

                if (job.Parent == null)
                {
                    Active.RemoveAt(Active.Count - 1);
                    continue;
                }

                bool done;
                _waitingForSpecies = false;

                try
                {
                    done = Step(job, plantBudget);
                }
                catch (System.Exception e)
                {
                    // A side that throws is given up (its plants so far stay), or it would throw on every frame.
                    done = true;

                    if (_failures++ < 3)
                    {
                        Debug.LogWarning("[SomniumEuroTruck] Terrain: a vegetation side failed and was dropped: " + e.GetType().Name + ": " + e.Message);
                    }
                }

                if (done)
                {
                    Active.RemoveAt(Active.Count - 1);

                    if (job.Plants.Count > 0)
                    {
                        Planted.Add(job);
                    }

                    // The instanced path: the side's trees join the field in one go.
                    if (job.Stand != null && job.Stand.Count > 0 && EtsTreeField.Add(job.Stand))
                    {
                        InField.Add(job);
                    }
                }

                // A new species was made this frame and the side needs another: planting goes on next frame (asking
                // again now would only spin on the same side until the budget ran out).
                if (_waitingForSpecies || Clock.Elapsed.TotalMilliseconds >= plantBudget)
                {
                    break;
                }
            }

            Switch(focus, budget);

            double ms = Clock.Elapsed.TotalMilliseconds;

            if (ms > _slowestMs)
            {
                _slowestMs = ms;
            }

            Report(budget);
        }

        /// <summary>Queued sides now within reach join the planting, nearest planted first; sides whose item was unloaded go.</summary>
        private static void Promote(Vector3 focus)
        {
            bool added = false;

            for (int i = Pending.Count - 1; i >= 0; i--)
            {
                Job job = Pending[i];

                if (job.Parent == null || Distance(job, focus) <= PlantRadius)
                {
                    Pending[i] = Pending[Pending.Count - 1];
                    Pending.RemoveAt(Pending.Count - 1);

                    if (job.Parent != null)
                    {
                        Active.Add(job);
                        added = true;
                    }
                }
            }

            if (added || Active.Count > 1)
            {
                // Nearest last: Pump takes from the end.
                Active.Sort((a, b) => Distance(b, focus).CompareTo(Distance(a, focus)));
            }
        }

        /// <summary>Plants a side's bands until done (true) or the budget is spent (false).</summary>
        private static bool Step(Job job, float budget)
        {
            while (true)
            {
                if (job.Band < 0 || job.PlantIndex >= job.PlantCount)
                {
                    if (!NextBand(job))
                    {
                        return true;
                    }

                    continue;
                }

                // Instanced: the band's species are made (models built, materials banded) before its first plant, one
                // new species a frame at most, so a region's first trees do not stack their set-up into one frame.
                if (_mode == ModeInstanced && !job.KindsReady)
                {
                    if (!SpeciesReady(job))
                    {
                        _waitingForSpecies = true;
                        return false;
                    }

                    job.KindsReady = true;
                }

                PlantOne(job);
                job.PlantIndex++;

                if (Clock.Elapsed.TotalMilliseconds >= budget)
                {
                    return false;
                }
            }
        }

        /// <summary>Moves to the side's next band that plants anything, exactly as the old loop skipped them (no random draws).</summary>
        private static bool NextBand(Job job)
        {
            EtsTerrainSide side = job.Side;

            while (++job.Band < side.Vegetation.Length)
            {
                EtsVegetationRecord band = side.Vegetation[job.Band];

                if (band == null || band.Token == 0 || band.Density <= 0f)
                {
                    continue;
                }

                float from = Mathf.Max(0f, band.From);
                float to = Mathf.Min(side.Size, band.To);

                if (to - from < 1f)
                {
                    continue;
                }

                EtsSiiUnit set = job.Map.Vegetation(band.Token);

                if (set == null)
                {
                    continue;
                }

                // The key is spelled differently by game version, so ask for both. This is a
                // definition file, not a map sector, so the sector's map version is the wrong thing
                // to branch on; the key that is present is the right one. 1.61 writes "model[]" once per
                // plant with a parallel "occurrence[]" weight: measured over the three files of
                // the family, /def/world/vegetation.sii, .base_share.sii and
                // .mod_benelux_reskin.sii, 2,207 + 714 + 9 = 2,930 "model[" and zero "models[",
                // with "model" and "occurrence" on 441 of 441 veg.* units and 0 units where the
                // two array lengths differ. An older installation that spells it "models" still
                // wins, because that key is tried first.
                List<string> modelUnits = set.GetArray("models");

                if (modelUnits.Count == 0)
                {
                    modelUnits = set.GetArray("model");
                }

                if (modelUnits.Count == 0)
                {
                    continue;
                }

                // Relative weights, one per model, so a set that asks for six pines to one maple
                // gets them. Measured values are small positive integers ("1" x2264, "2" x473,
                // "3" x126, up to 16; 0 non-integer, 0 <= 0). Absent or unreadable means 1, which
                // reproduces the uniform draw exactly.
                List<string> weights = set.GetArray("occurrence");
                var cumulative = new int[modelUnits.Count];
                int totalWeight = 0;

                for (int w = 0; w < modelUnits.Count; w++)
                {
                    int weight = 1;

                    if (w < weights.Count && int.TryParse(weights[w], out int parsed) && parsed > 0)
                    {
                        weight = parsed;
                    }

                    totalWeight += weight;
                    cumulative[w] = totalWeight;
                }

                float area = job.Length * (to - from);
                job.ModelUnits = modelUnits;
                job.Cumulative = cumulative;
                job.TotalWeight = totalWeight;
                job.From = from;
                job.To = to;
                job.PlantIndex = 0;
                job.PlantCount = Mathf.Min(BandCap, Mathf.RoundToInt(area / 10000f * band.Density * DensityScale));
                job.HighPoly = band.HighPolyDistance;
                job.KindsReady = false;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Where a plant at <paramref name="local"/> (the side's parent frame) stands: the highest ground of its own
        /// item under it, else the highest other ground that is not the ground fill.
        /// </summary>
        private static bool GroundUnder(Job job, Vector3 local, out Vector3 point)
        {
            point = Vector3.zero;
            Vector3 origin = job.Parent.TransformPoint(local) + Vector3.up * 80f;
            int count = Physics.RaycastNonAlloc(origin, Vector3.down, Hits, 200f, EtsLayers.GroundMask, QueryTriggerInteraction.Ignore);
            bool own = false, other = false;
            Vector3 best = Vector3.zero, fallback = Vector3.zero, top = Vector3.zero;
            Collider topCollider = null;

            for (int k = 0; k < count; k++)
            {
                Collider collider = Hits[k].collider;

                if (collider == null)
                {
                    continue;
                }

                Vector3 p = Hits[k].point;

                if ((topCollider == null || p.y > top.y) && !EtsGroundFill.IsFill(collider))
                {
                    top = p;
                    topCollider = collider;
                }

                if (collider.transform.IsChildOf(job.Parent))
                {
                    if (!own || p.y > best.y)
                    {
                        best = p;
                        own = true;
                    }
                }
                else if (!EtsGroundFill.IsFill(collider) && (!other || p.y > fallback.y))
                {
                    fallback = p;
                    other = true;
                }
            }

            if (!own && !other)
            {
                _noGround++;
                return false;
            }

            point = own ? best : fallback;

            // Never on a road, a pavement or a yard: when the top surface at the foot is a hard one (a drive-surface
            // collider that is not terrain) at or up to HardSurfaceReach above the ground found, the plant is not planted.
            // A band reaching over a neighbouring carriageway, or a side with no ground of its own over another road,
            // stood trunks in the traffic lanes (render lab Szczecin e_tree, a row of trunks along a lane; five times as
            // many at the map's density as at the old fifth). Its draws were all made already, so the plants after it are
            // unchanged; a tree under a bridge deck higher than that still stands.
            if (topCollider != null && top.y >= point.y - 0.25f && top.y <= point.y + HardSurfaceReach && !IsSoftGround(topCollider))
            {
                _onHardSurface++;
                return false;
            }

            if (own)
            {
                _ownGround++;
            }
            else
            {
                _otherGround++;
            }

            return true;
        }

        /// <summary>A hard surface this far above a plant's ground (or at it) keeps the plant out, metres.</summary>
        public static float HardSurfaceReach = 3f;

        private static readonly Dictionary<int, bool> SoftByCollider = new Dictionary<int, bool>();
        private static int _onHardSurface;

        /// <summary>
        /// Whether a ground collider is terrain (EtsTerrainBuilder's pieces are all named "terrain ...", its bezier patches'
        /// "terrain patch ...", EtsPrefabTerrainBuilder's "prefab terrain ..."): anything else on the ground layers is a road
        /// slab, a median, a pavement strip or a prefab's own surface. Read once per collider (a name read makes a string).
        /// </summary>
        private static bool IsSoftGround(Collider collider)
        {
            int id = collider.GetInstanceID();

            if (!SoftByCollider.TryGetValue(id, out bool soft))
            {
                string name = collider.gameObject.name;
                soft = name.StartsWith("terrain", System.StringComparison.Ordinal) || name.StartsWith("prefab terrain", System.StringComparison.Ordinal);
                SoftByCollider[id] = soft;
            }

            return soft;
        }

        /// <summary>
        /// One plant of the current band. Its five draws (along, across, species, scale, yaw - the old loop's order) are
        /// all made first, so a plant given up for want of ground or of a model never shifts the next plant's draws.
        /// </summary>
        private static void PlantOne(Job job)
        {
            System.Random random = job.Random;
            float s = (float)random.NextDouble();
            float lateral = job.From + (float)random.NextDouble() * (job.To - job.From);
            int roll = random.Next(job.TotalWeight);
            float scale = 0.85f + (float)random.NextDouble() * 0.3f;
            float yaw = (float)random.NextDouble() * 360f;
            Vector3 point = EtsMapBuilder.Hermite(job.P0, job.T0, job.P1, job.T1, s);
            Vector3 forward = EtsMapBuilder.HermiteTangent(job.P0, job.T0, job.P1, job.T1, s);
            forward.y = 0f;

            if (forward.sqrMagnitude < 1e-6f)
            {
                return;
            }

            Vector3 right = Vector3.Cross(Vector3.up, forward.normalized) * job.Sign;
            Vector3 position = point + right * (job.StartOffset + lateral);

            // Stand it on the ground its own terrain builder made.
            if (!GroundUnder(job, position, out Vector3 ground))
            {
                return;
            }

            // One species draw per plant, as before, so the seeded placement stays deterministic;
            // with every weight 1 this picks exactly what random.Next(count) picked
            // (checked over 20,000 draws on list lengths 1..16, 0 differences).
            int pick = 0;

            while (pick < job.Cumulative.Length - 1 && roll >= job.Cumulative[pick])
            {
                pick++;
            }

            if (_mode == ModeInstanced)
            {
                EtsTreeField.Kind kind = EtsTreeField.KindFor(job.Map, job.ModelUnits[pick]);

                if (kind == null)
                {
                    return;
                }

                if (job.Stand == null)
                {
                    job.Stand = new EtsTreeField.Stand { Owner = job.Parent };
                }

                job.Stand.Add(ground, yaw, scale, kind, job.HighPoly);

                if (job.Stats != null)
                {
                    job.Stats.Plants++;
                }

                LogCutOut();
                return;
            }

            EtsModelData model = Model(job.Map, job.ModelUnits[pick]);

            if (model == null)
            {
                return;
            }

            // The near model casts the sun's shadow, as the game's eut2.leaves.shadow trees do; the sprite never.
            EtsModelBuilder.Result built = EtsModelBuilder.Build(model, new EtsModelBuilder.Options
            {
                Look = "default",
                Variants = new[] { "default" },
                CreateLocators = false,
                AddColliders = false,
                CastShadows = true
            }, job.Parent, EtsEngine.Materials);

            if (built == null || built.Root == null)
            {
                return;
            }

            built.Root.name = "plant " + model.Path;
            built.Root.transform.position = ground;
            built.Root.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            built.Root.transform.localScale = Vector3.one * scale;

            // Far away the game draws the unit's sprite_model (two crossed quads, 6
            // triangles); a LODGroup does the same swap here.
            EtsModelData sprite = SpriteModel(job.Map, job.ModelUnits[pick]);

            if (sprite != null && sprite != model)
            {
                EtsModelBuilder.Result far = EtsModelBuilder.Build(sprite, new EtsModelBuilder.Options
                {
                    Look = "default",
                    Variants = new[] { "default" },
                    CreateLocators = false,
                    AddColliders = false,
                    CastShadows = false
                }, built.Root.transform, EtsEngine.Materials);

                if (far != null && far.Root != null)
                {
                    var lod = built.Root.AddComponent<LODGroup>();
                    var nearRenderers = new List<Renderer>();

                    foreach (Renderer r in built.Root.GetComponentsInChildren<Renderer>(true))
                    {
                        if (!r.transform.IsChildOf(far.Root.transform))
                        {
                            nearRenderers.Add(r);
                        }
                    }

                    lod.SetLODs(new[]
                    {
                        new LOD(SpriteBelowScreenHeight, nearRenderers.ToArray()),
                        new LOD(0.002f, far.Root.GetComponentsInChildren<Renderer>(true))
                    });
                    lod.RecalculateBounds();

                    // The switch keeps the tree's own size. A leaves mesh's drawing bounds are grown by
                    // the reach of its cards on every axis (EtsModelBuilder.LeavesMeshStreams: buk_tree_1_med
                    // 8.7 x 13.9 m stored, 22.1 x 27.3 m drawn, harness Program1924), which would double the
                    // size RecalculateBounds measures and so the distance at which the tree fills 4 % of
                    // the screen: near models out to about twice as far. So the size is the stored model's
                    // largest extent, in the root's own space (the LODGroup applies the root's scale): the
                    // height for the tall trees (buk_tree_1_med 13.9 m), which is also what the old
                    // world-space bounds of the stored geometry came to. Checker probes 1936/1937: the same
                    // for all 5 units the first 1.2 km build plants (maple.tree, poplar.tree, poplar.tall,
                    // maple.small, big_bush; ratio 1.00); for the wide ones elsewhere (252 of 1,008 units,
                    // e.g. mesquite.tree.03 9.8 m against 15.3 m, estimated from the renderers' world boxes
                    // averaged over yaw) the old size was the yaw-turned box's diagonal, so it changed with
                    // each plant's random yaw; those now swap to the sprite at 0.64-0.9 of the old distance.
                    if (model.BoundsMax != null && model.BoundsMin != null && model.BoundsMax.Length >= 3 && model.BoundsMin.Length >= 3)
                    {
                        Vector3 min = EtsModelBuilder.ToUnity(model.BoundsMin[0], model.BoundsMin[1], model.BoundsMax[2]);
                        Vector3 max = EtsModelBuilder.ToUnity(model.BoundsMax[0], model.BoundsMax[1], model.BoundsMin[2]);
                        Vector3 size = max - min;
                        lod.localReferencePoint = (min + max) * 0.5f;
                        lod.size = Mathf.Max(size.x, Mathf.Max(size.y, size.z));
                    }
                }
            }

            if (job.Stats != null)
            {
                job.Stats.Plants++;
            }

            job.Plants.Add(built.Root);
            _plants++;
            LogCutOut();
        }

        /// <summary>
        /// Planted sides further than HideBeyond are switched off, a plant at a time under the budget, and on again
        /// within Radius + 100 m; sides whose item was unloaded are forgotten. Up to 64 sides are looked at a frame.
        /// </summary>
        private static void Switch(Vector3 focus, float budget)
        {
            int looked = 0;

            while (Planted.Count > 0 && looked < 64)
            {
                if (_visIndex >= Planted.Count)
                {
                    _visIndex = 0;
                }

                Job job = Planted[_visIndex];

                if (job.Parent == null)
                {
                    if (!job.Shown) _hidden -= job.Plants.Count;
                    _plants -= job.Plants.Count;
                    Planted[_visIndex] = Planted[Planted.Count - 1];
                    Planted.RemoveAt(Planted.Count - 1);
                    looked++;
                    continue;
                }

                float distance = Distance(job, focus);
                bool want = job.Shown ? distance <= HideRadius : distance <= PlantRadius + 100f;

                if (want != job.Shown)
                {
                    job.Shown = want;
                    job.ToggleIndex = 0;
                    _hidden += want ? -job.Plants.Count : job.Plants.Count;
                }

                while (job.ToggleIndex < job.Plants.Count && Clock.Elapsed.TotalMilliseconds < budget)
                {
                    GameObject plant = job.Plants[job.ToggleIndex++];

                    if (plant != null && plant.activeSelf != job.Shown)
                    {
                        plant.SetActive(job.Shown);
                    }
                }

                if (job.ToggleIndex < job.Plants.Count)
                {
                    return; // this side again next frame
                }

                _visIndex++;
                looked++;
            }
        }

        private static void Report(float budget)
        {
            int standing = PlantsPlanted;

            if (Time.unscaledTime - _reportedAt < 30f || (standing == _reportedPlants && Active.Count == 0))
            {
                return;
            }

            _reportedAt = Time.unscaledTime;
            _reportedPlants = standing;
            int placed = _ownGround + _otherGround;
            bool instanced = _mode == ModeInstanced;
            Debug.Log("[SomniumEuroTruck] Terrain: vegetation planted within " + PlantRadius.ToString("0") + " m of the focus, a little a frame, at x" +
                      DensityScale.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " the map's density, " + (instanced ? "GPU-instanced" : "one GameObject per plant") + ": " +
                      standing + " plants standing on " + (instanced ? EtsTreeField.StandCount : Planted.Count) + " sides (" + (placed > 0 ? (100f * _ownGround / placed).ToString("0.0") : "-") +
                      " % on their own side's ground, " + _otherGround + " on other ground, " + _noGround + " points with no ground, " + _onHardSurface +
                      " kept off a road or yard surface), " +
                      (instanced ? EtsTreeField.Summary() : _hidden + " switched off beyond " + HideRadius.ToString("0") + " m") + "; " + Active.Count +
                      " sides planting, " + Pending.Count + " waiting beyond " + PlantRadius.ToString("0") + " m (" + _queued + " queued this visit); slowest frame " +
                      _slowestMs.ToString("0.0") + " ms (budget " + budget.ToString("0.0") + " ms).");
            _slowestMs = 0;
        }

        /// <summary>The leaves shader the cut-out line was written for; a visit that loads it afresh writes it again.</summary>
        private static Shader _cutOutLogged;

        /// <summary>
        /// Once per leaves shader, when the first plant stands: whether the shader drawing the trees carries the
        /// distance cut-out (Shaders/EtsLeaves.shader header: the game's 0.05 near, rising to 0.4 by mip 2, no
        /// mip alpha boost), with the values it starts every leaves material on. Without it the far-tree and
        /// tree-wall cards of sets v2_16fr and v2_49 are cut out as their whole rectangle in the distance, so a
        /// bundle that still carries the old shader says so.
        /// </summary>
        private static void LogCutOut()
        {
            Shader leaves = EtsMaterialCache.LeavesShader;

            if (leaves == null || leaves == _cutOutLogged)
            {
                return;
            }

            _cutOutLogged = leaves;
            int far = leaves.FindPropertyIndex("_CutoffFar");
            int mips = leaves.FindPropertyIndex("_CutoffMips");
            int near = leaves.FindPropertyIndex("_Cutoff");

            if (far >= 0 && mips >= 0)
            {
                Debug.Log("[SomniumEuroTruck] Trees: distance cut-out live on " + leaves.name + " (supported " + leaves.isSupported +
                          "): alpha discard " + (near >= 0 ? leaves.GetPropertyDefaultFloatValue(near) : 0.05f).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) +
                          " near (materials set the game's " + EtsMaterialCache.LeavesCutoff.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + "), rising to " +
                          leaves.GetPropertyDefaultFloatValue(far).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " over " + leaves.GetPropertyDefaultFloatValue(mips).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) +
                          " mip levels; no mip alpha boost. Far trees and tree walls keep their shape at a distance.");
            }
            else
            {
                Debug.LogWarning("[SomniumEuroTruck] Trees: " + leaves.name + " has NO distance cut-out (_CutoffFar/_CutoffMips missing): " +
                                 "the bundle carries the old leaves shader, and far-tree and tree-wall cards will show as solid boxes in the distance.");
            }

            // The 2026-09-25 leaf lighting: the old shader had the wrap / translucency look terms and the 2 x mask.x ambient.
            bool oldLook = leaves.FindPropertyIndex("_WrapLight") >= 0 || leaves.FindPropertyIndex("_LeafAmbient") >= 0;
            Debug.Log("[SomniumEuroTruck] Trees: leaf lighting " + (oldLook
                ? "OLD (wrap 0.5, translucency 0.25, ambient x 2 x mask.x): the bundle carries the previous leaves shader."
                : "the game's: sun x N.L on the crown normal, ambient flat (ambient_env) x the near-field factor, shadow looked up mask.z m toward the sun; cards face the light in the shadow pass, sprites cast none."));
        }

        /// <summary>
        /// The medium-poly model of a vegetation model unit, loaded once. Also used for the map's
        /// placed trees: model.sii units that name a "vegetation_model" instead of a model_desc.
        /// </summary>
        private static readonly Dictionary<string, EtsModelData> SpritesByUnit = new Dictionary<string, EtsModelData>();

        /// <summary>The unit's sprite_model, or null.</summary>
        public static EtsModelData SpriteModel(EtsMap map, string unitName)
        {
            if (SpritesByUnit.TryGetValue(unitName, out EtsModelData cached))
            {
                return cached;
            }

            EtsModelData model = null;
            EtsSiiUnit unit = map.VegetationModel(unitName);
            string path = unit != null ? unit.GetString("sprite_model", null) : null;

            if (!string.IsNullOrEmpty(path))
            {
                model = EtsEngine.LoadModel(path, out string failure);
            }

            SpritesByUnit[unitName] = model;
            return model;
        }

        private static readonly Dictionary<string, EtsModelData> HighByUnit = new Dictionary<string, EtsModelData>();

        /// <summary>
        /// The unit's hi_poly_model, or null: drawn instanced within the band's high_poly_distance (50 m on every band
        /// of the Dover and Cambridge census). buk_tree_1_high is 529 triangles against the med model's 453, the same
        /// 442 card vertices with more branch geometry (probe N/trees/probe/t1_models.txt). Read on first use - the map
        /// viewer's prefetch warms the med and sprite models, not this one.
        /// </summary>
        public static EtsModelData HighModel(EtsMap map, string unitName)
        {
            if (HighByUnit.TryGetValue(unitName, out EtsModelData cached))
            {
                return cached;
            }

            EtsModelData model = null;
            EtsSiiUnit unit = map.VegetationModel(unitName);
            string path = unit != null ? unit.GetString("hi_poly_model", null) : null;

            if (!string.IsNullOrEmpty(path))
            {
                model = EtsEngine.LoadModel(path, out string failure);
            }

            HighByUnit[unitName] = model;
            return model;
        }

        public static EtsModelData Model(EtsMap map, string unitName)
        {
            if (ModelsByUnit.TryGetValue(unitName, out EtsModelData cached))
            {
                return cached;
            }

            EtsModelData model = null;
            EtsSiiUnit unit = map.VegetationModel(unitName);

            if (unit != null)
            {
                foreach (string key in new[] { "med_poly_model", "hi_poly_model", "sprite_model" })
                {
                    string path = unit.GetString(key, null);

                    if (!string.IsNullOrEmpty(path))
                    {
                        model = EtsEngine.LoadModel(path, out string failure);

                        if (model != null)
                        {
                            break;
                        }
                    }
                }
            }

            ModelsByUnit[unitName] = model;
            return model;
        }
    }
}
