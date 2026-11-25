        /// <summary>
        /// Lays a run along the Hermite between the two nodes: the start cap, the centre part repeated, the end cap,
        /// nose to tail, each at its OWN length and all stretched alike so the run spans exactly the item's length.
        /// One prototype is built per part and the rest are instances of it, so a straight 60 m fence costs one model
        /// load, not sixty; a run whose path bends gets its elements bent with it (BendCopy).
        ///
        /// The fence flashing (2026-10-04, probes DevTools/work/fences/probe P2): two things made neighbouring
        /// elements overlap in the same plane, and overlapping coplanar faces z-fight.
        ///
        /// 1. The caps took a centre slot. A cap is usually longer than the centre part - it carries the end post:
        ///    fence_metal_03_tr_blke_crv start 4.67 / centre 4.47 / end 4.59 m, fence_concrete_01_ibe start1 4.26 /
        ///    centre 4.11, barrier_antinoise_01_ibe end1 5.39 / centre 5.22, admin_18_de start1 8.23 / centre 4.96,
        ///    bank_metal_stone_lod start 13.59 / centre 13.08 - and a cap centred in a centre-sized slot overhung it
        ///    by half the difference at both ends: into its neighbour (the flashing) and past the node.
        /// 2. Every element was laid STRAIGHT, turned to the path's heading at its middle. On a curve two
        ///    neighbours then cross in a thin wedge on the inside of the bend, face on face.
        ///
        /// The game's own record of the geometry settles both: an item's five-axis bounding volume is its generated
        /// geometry plus 0.125 m (README, "The runs were laid wrong"). Against the stored volumes of 2,075 runs round
        /// Dover the worst-axis residual (median / p90) was 0.150 / 1.707 m for buildings and 0.098 / 1.320 m for
        /// curves as laid before; caps at their own length and elements bent along the path take it to
        /// 0.040 / 0.315 and 0.052 / 0.243 m, and on curves turning 30 degrees or more bending alone takes
        /// buildings from 0.326 to 0.097 m. Coplanar overlapping faces between elements of one run, 400 m round
        /// each site: Dover 11,566 pairs (837 m2) to 0, Prague 1,895 (446 m2) to 9 (0.02 m2), Szczecin 2,056
        /// (193 m2) to 90 (0.5 m2).
        ///
        /// The count: centres = (length / stretch - caps) / step, rounded to nearest for a building and up for a
        /// curve, as before (the rule itself is still not established; see the note at the top of the file).
        /// </summary>
        private static void Lay(EtsMap map, EtsModelData model, Transform run, EtsMapBuilder.Options options,
            EtsMapNode start, EtsMapNode end, float length, float stretch, float[] heights,
            float step, string centre, string first, string last, float scale, bool roundUp, EtsMapBuilder.Stats stats)
        {
            float span = stretch > 0.01f && stretch < 50f ? stretch : 1f;
            float capStart = first != null ? PartLength(model, first) : 0f;
            float capEnd = last != null ? PartLength(model, last) : 0f;
            float raw = (length / span - (capStart + capEnd) * scale) / (step * scale);
            int count = Mathf.Max(0, roundUp ? Mathf.CeilToInt(raw - 0.001f) : Mathf.RoundToInt(raw));

            if (count == 0 && capStart + capEnd <= 0.01f)
            {
                count = 1;
            }

            if (count > MaxElements)
            {
                count = MaxElements;
            }

            // Path metres per nominal metre: every part of the run is stretched by the same factor.
            float nominal = (capStart + capEnd + count * step) * scale;
            float k = nominal > 0.01f ? length / nominal : 1f;
            int slots = count + (first != null ? 1 : 0) + (last != null ? 1 : 0);

            Vector3 p0 = EtsMapBuilder.ToWorld(start.Position, options.CenterGame);
            Vector3 p1 = EtsMapBuilder.ToWorld(end.Position, options.CenterGame);
            Vector3 t0 = EtsMapBuilder.ToWorldDirection(start.Forward()) * length;
            Vector3 t1 = EtsMapBuilder.ToWorldDirection(end.Forward()) * length;

            // Equal steps of the Hermite parameter are not equal steps ALONG the path: measured over
            // the curves near the bay the two differ by a median of 0.10 m, p95 0.46 m and up to
            // 3.85 m on a 321 m tree wall. The stored Length is genuinely the arc length - the
            // game's own routine is an adaptive subdivision of this same Hermite - so the path is
            // sampled once here and then walked by DISTANCE.
            const int Samples = 64;
            var points = new Vector3[Samples + 1];
            var arc = new float[Samples + 1];
            float turning = 0f;

            for (int k2 = 0; k2 <= Samples; k2++)
            {
                points[k2] = EtsMapBuilder.Hermite(p0, t0, p1, t1, (float)k2 / Samples);
                arc[k2] = k2 == 0 ? 0f : arc[k2 - 1] + Vector3.Distance(points[k2], points[k2 - 1]);

                if (k2 >= 2)
                {
                    turning += Vector3.Angle(points[k2 - 1] - points[k2 - 2], points[k2] - points[k2 - 1]);
                }
            }

            // A straight run keeps its shared, instanced elements; one that turns (in plan or in slope) by more than a
            // fifth of a degree end to end has every element bent along it, as the game lays it.
            bool bend = turning > 0.2f;
            var prototypes = new Dictionary<string, GameObject>(StringComparer.Ordinal);
            float cursor = 0f;

            for (int i = 0; i < slots; i++)
            {
                string part = centre;
                float own = step;

                if (i == 0 && first != null)
                {
                    part = first;
                    own = capStart;
                }
                else if (i == slots - 1 && last != null)
                {
                    part = last;
                    own = capEnd;
                }

                float extent = own * scale * k;
                float middle = cursor + 0.5f * extent;
                cursor += extent;
                GameObject prototype = Prototype(model, part, options, prototypes);

                if (prototype == null)
                {
                    continue;
                }

                // An element is NOT modelled about the origin. A pmd lays its start, centre and end
                // parts side by side down one long strip, so the part that gets repeated can sit
                // anywhere: fence_metal_conc_white_01_fr_crv's "center1" is a 2 m panel modelled at
                // z -54.2..-52.2, and a curve's "s3" cap at z -36.5. The game throws that offset away
                // and puts the part ON the path, its middle on its slot's middle (harness Program1003:
                // median residual against the stored boxes 5.38 m before, 0.17 m after).
                //
                // BuildMesh writes the game z NEGATED into the mesh, and the copy is turned
                // LookRotation(-forward) and scaled (scale, scale, scale * k), so a model point at game z
                // lands at path offset  origin + z * scale * k.
                PartZRange(model, part, out float zMin, out float zMax);
                float zMid = 0.5f * (zMin + zMax);
                float lift = heights != null && i < heights.Length ? heights[i] : 0f;
                AtArc(points, arc, middle, p1 - p0, out Vector3 point, out Vector3 forward);

                GameObject copy = UnityEngine.Object.Instantiate(prototype, run);
                copy.name = part ?? "element";

                if (bend)
                {
                    copy.transform.position = point + new Vector3(0f, lift, 0f);
                    copy.transform.rotation = Quaternion.identity;
                    copy.transform.localScale = Vector3.one;
                    BendCopy(copy, points, arc, p1 - p0, middle, zMid, scale * k, scale, lift);
                    BentElements++;
                }
                else
                {
                    point -= forward * (zMid * scale * k);
                    point.y += lift;
                    copy.transform.position = point;

                    // A run is modelled along the GAME's +z - "start" sits at a lower z than its "end"
                    // on 2,614 of the 2,908 paired parts across the curve models - and ToUnity negates
                    // z, so in Unity the run's forward is the copy's local MINUS z.
                    copy.transform.rotation = Quaternion.LookRotation(-forward, Vector3.up);
                    copy.transform.localScale = new Vector3(scale, scale, scale * k);
                }

                copy.SetActive(true);
                Elements++;
            }

            foreach (KeyValuePair<string, GameObject> prototype in prototypes)
            {
                UnityEngine.Object.Destroy(prototype.Value);
            }

            if (bend)
            {
                BentRuns++;
            }

            if ((first != null && capStart > 0.01f) || (last != null && capEnd > 0.01f))
            {
                CappedRuns++;
            }

            if (!_layLogged && bend && slots > 1)
            {
                _layLogged = true;
                Debug.Log("[SomniumEuroTruck] Runs: caps at their own length, curved runs bent along their path (first: " + model.Path +
                          ", " + slots + " elements, turning " + turning.ToString("0.0") + " deg).");
            }

            stats.Models += slots;
        }

        public static int BentRuns, BentElements, CappedRuns;
        private static bool _layLogged;

        /// <summary>
        /// Bends one placed copy along the path, vertex by vertex, as the game lays a run: a model point at game z rides
        /// the path at distance  middle + (z - zMid) * zScale  and keeps its x and y in the path's frame there, so two
        /// neighbours meet in one shared cross-section instead of crossing. The copy stands at its slot's middle with no
        /// rotation or scale; each of its meshes (shared with every other copy of the part) is replaced by a bent
        /// copy of its own, and a collider built on the same mesh follows it.
        /// </summary>
        private static void BendCopy(GameObject copy, Vector3[] points, float[] arc, Vector3 chord, float middle, float zMid, float zScale, float scale, float lift)
        {
            var bent = new Dictionary<Mesh, Mesh>();
            MeshFilter[] filters = copy.GetComponentsInChildren<MeshFilter>(true);

            foreach (MeshFilter filter in filters)
            {
                Mesh source = filter.sharedMesh;

                if (source == null)
                {
                    continue;
                }

                if (!bent.TryGetValue(source, out Mesh mesh))
                {
                    mesh = BentMesh(source, filter.transform.worldToLocalMatrix, points, arc, chord, middle, zMid, zScale, scale, lift);
                    bent[source] = mesh;
                }

                filter.sharedMesh = mesh;
            }

            foreach (MeshCollider collider in copy.GetComponentsInChildren<MeshCollider>(true))
            {
                Mesh source = collider.sharedMesh;

                if (source == null)
                {
                    continue;
                }

                if (!bent.TryGetValue(source, out Mesh mesh))
                {
                    mesh = BentMesh(source, collider.transform.worldToLocalMatrix, points, arc, chord, middle, zMid, zScale, scale, lift);
                    bent[source] = mesh;
                }

                collider.sharedMesh = mesh;
            }
        }

        private static Mesh BentMesh(Mesh source, Matrix4x4 worldToLocal, Vector3[] points, float[] arc, Vector3 chord, float middle, float zMid, float zScale, float scale, float lift)
        {
            Mesh mesh = UnityEngine.Object.Instantiate(source);
            mesh.name = source.name + " bent";
            Vector3[] vertices = source.vertices;
            Vector3[] normals = source.normals;
            Vector4[] tangents = source.tangents;
            bool hasNormals = normals != null && normals.Length == vertices.Length;
            bool hasTangents = tangents != null && tangents.Length == vertices.Length;
            Bounds before = vertices.Length > 0 ? new Bounds(vertices[0], Vector3.zero) : new Bounds();
            Bounds after = new Bounds();
            float total = arc[arc.Length - 1];
            float inverseScale = scale > 1e-4f ? 1f / scale : 1f;
            float inverseZ = zScale > 1e-4f ? 1f / zScale : 1f;

            for (int v = 0; v < vertices.Length; v++)
            {
                Vector3 local = vertices[v];
                before.Encapsulate(local);

                // The mesh holds the game's z negated (ToUnity).
                float distance = middle + (-local.z - zMid) * zScale;
                AtArc(points, arc, distance, chord, out Vector3 point, out Vector3 forward);

                if (distance < 0f)
                {
                    point += forward * distance;
                }
                else if (distance > total)
                {
                    point += forward * (distance - total);
                }

                // The frame of LookRotation(-forward, up), as the straight copies are turned.
                Vector3 axisZ = -forward;
                Vector3 axisX = Vector3.Cross(Vector3.up, axisZ);
                axisX = axisX.sqrMagnitude > 1e-8f ? axisX.normalized : Vector3.right;
                Vector3 axisY = Vector3.Cross(axisZ, axisX);

                Vector3 world = point + axisX * (local.x * scale) + axisY * (local.y * scale);
                world.y += lift;
                vertices[v] = worldToLocal.MultiplyPoint3x4(world);

                if (v == 0)
                {
                    after = new Bounds(vertices[v], Vector3.zero);
                }
                else
                {
                    after.Encapsulate(vertices[v]);
                }

                if (hasNormals)
                {
                    Vector3 n = normals[v];
                    Vector3 bentNormal = axisX * (n.x * inverseScale) + axisY * (n.y * inverseScale) + axisZ * (n.z * inverseZ);
                    normals[v] = worldToLocal.MultiplyVector(bentNormal).normalized;
                }

                if (hasTangents)
                {
                    Vector4 t = tangents[v];
                    Vector3 bentTangent = worldToLocal.MultiplyVector(axisX * (t.x * scale) + axisY * (t.y * scale) + axisZ * (t.z * zScale)).normalized;
                    tangents[v] = new Vector4(bentTangent.x, bentTangent.y, bentTangent.z, t.w);
                }
            }

            mesh.vertices = vertices;

            if (hasNormals)
            {
                mesh.normals = normals;
            }

            if (hasTangents)
            {
                mesh.tangents = tangents;
            }

            // A leaves mesh carries drawing bounds grown past its vertices by its cards; keep that growth.
            Vector3 growth = Vector3.Max(Vector3.zero, source.bounds.size - before.size);
            after.Expand(growth * Mathf.Max(scale, zScale));
            mesh.bounds = after;
            return mesh;
        }
