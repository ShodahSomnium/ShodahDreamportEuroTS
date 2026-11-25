using System.Collections.Generic;
using UnityEngine;

namespace SomniumSpace.Worlds.SomniumEuroTruck.Ets
{
    /// <summary>
    /// Ground around a prefab (a junction, a company yard), the way the map describes it. The map
    /// item carries one terrain per descriptor node - size, profile, brushes and a quad grid - and
    /// the descriptor gives that node its border: a run of terrain points, one run per model
    /// variant. The terrain's columns are that border's segments (the map stores exactly points - 1
    /// columns) and its rows are the profile steps out to the size, so a strip grown outward from
    /// the border IS the prefab's terrain. This replaces an earlier guess that borrowed the
    /// connected road's terrain and split borders by distance; the descriptor's positions and
    /// normals had also been read interleaved, which scrambled them.
    ///
    /// "Outward" is the border's own perpendicular. The descriptor's per-point terrain NORMALS are
    /// the ground's surface normal there, not a direction to grow in (see BuildBorder).
    /// </summary>
    public static class EtsPrefabTerrainBuilder
    {
        private static readonly float[] DefaultSteps = { 1, 1, 1, 2, 6, 6, 10, 10, 10, 20, 40, 50, 50, 50, 100, 100 };
        private static readonly Dictionary<ulong, Material> MaterialsByToken = new Dictionary<ulong, Material>();

        public static void Build(EtsMap map, EtsMapPrefab prefab, EtsPrefabDescriptor ppd, int variantIndex, Vector3 position, Quaternion rotation,
            EtsMapBuilder.Options options, Transform parent, EtsMapBuilder.Stats stats)
        {
            // The prefab's own yard and road surface are ground, whether or not it has terrain corners: the ground fill
            // measures the tiles under it again once the stream settles there (EtsGroundFill.NoteGround).
            NotePrefabGround(ppd, position, rotation);

            if (!options.Terrain || ppd == null || ppd.TerrainPoints.Length < 6 || ppd.Nodes.Length == 0 || prefab.Corners == null)
            {
                return;
            }

            // The prefab's own map node - the first in its node list, whichever descriptor node
            // the origin index names (TruckLib, Prefab.Nodes: "The origin node will always be the
            // first entry in this list"). The vertex-tool records below are positions relative to it.
            EtsMapNode originNode = prefab.NodeUids.Length > 0 ? map.FindNode(prefab.NodeUids[0]) : null;
            Vector3 origin = originNode != null ? EtsMapBuilder.ToWorld(originNode.Position, options.CenterGame) : position;

            for (int k = 0; k < ppd.Nodes.Length && k < prefab.Corners.Length; k++)
            {
                EtsTerrainSide side = prefab.Corners[k];

                if (side == null || side.Size <= 0.01f)
                {
                    continue;
                }

                ppd.NodePointRange(k, variantIndex, out int start, out int count);

                if (count >= 2)
                {
                    BuildBorder(map, side, ppd, start, count, position, rotation, origin, options, parent, stats, prefab.Uid, k);
                }
            }
        }

        /// <summary>The box round a prefab's nodes and terrain border points (every variant's), in world space, 30 m at least.</summary>
        private static void NotePrefabGround(EtsPrefabDescriptor ppd, Vector3 position, Quaternion rotation)
        {
            Vector3 min = position - new Vector3(30f, 5f, 30f), max = position + new Vector3(30f, 5f, 30f);

            if (ppd != null)
            {
                for (int t = 0; t + 2 < ppd.TerrainPoints.Length; t += 3)
                {
                    Vector3 p = position + rotation * EtsModelBuilder.ToUnity(ppd.TerrainPoints[t], ppd.TerrainPoints[t + 1], ppd.TerrainPoints[t + 2]);
                    min = Vector3.Min(min, p);
                    max = Vector3.Max(max, p);
                }

                foreach (EtsPrefabDescriptor.Node node in ppd.Nodes)
                {
                    Vector3 p = position + rotation * EtsModelBuilder.ToUnity(node.Position);
                    min = Vector3.Min(min, p);
                    max = Vector3.Max(max, p);
                }
            }

            EtsGroundFill.NoteGround(null, min, max);
        }

        /// <summary>The border polyline re-cut into the given number of equal-length columns.</summary>
        private static Vector3[] Resample(Vector3[] points, int cols)
        {
            var cumulative = new float[points.Length];

            for (int i = 1; i < points.Length; i++)
            {
                cumulative[i] = cumulative[i - 1] + Vector3.Distance(points[i], points[i - 1]);
            }

            float total = cumulative[points.Length - 1];
            var result = new Vector3[cols + 1];

            for (int c = 0; c <= cols; c++)
            {
                float target = total * c / cols;
                int s = 1;

                while (s < points.Length - 1 && cumulative[s] < target)
                {
                    s++;
                }

                float segment = cumulative[s] - cumulative[s - 1];
                float f = segment > 1e-6f ? Mathf.Clamp01((target - cumulative[s - 1]) / segment) : 0f;
                result[c] = Vector3.Lerp(points[s - 1], points[s], f);
            }

            return result;
        }

        private static void BuildBorder(EtsMap map, EtsTerrainSide side, EtsPrefabDescriptor ppd, int start, int count, Vector3 position, Quaternion rotation,
            Vector3 origin, EtsMapBuilder.Options options, Transform parent, EtsMapBuilder.Stats stats, ulong prefabUid, int corner)
        {
            var pointList = new List<Vector3>(count);

            for (int j = 0; j < count; j++)
            {
                int t = start + j;
                Vector3 point = position + rotation * EtsModelBuilder.ToUnity(ppd.TerrainPoints[t * 3], ppd.TerrainPoints[t * 3 + 1], ppd.TerrainPoints[t * 3 + 2]);

                // A point on top of the previous one would make a zero-width column of zero-area
                // triangles, whose normals come out as nothing at all.
                if (pointList.Count > 0 && (point - pointList[pointList.Count - 1]).sqrMagnitude < 0.05f * 0.05f)
                {
                    continue;
                }

                pointList.Add(point);
            }

            if (pointList.Count < 2)
            {
                return;
            }

            count = pointList.Count;
            int cols = count - 1;
            Vector3[] points = pointList.ToArray();
            EtsTerrainQuadData quads = side.Quads;

            // The stored grid's column count is the mapper's, not the border's: the same descriptor
            // corner is stored with 2 columns in one placement and 4 in another at much the same
            // size, a 14-point arc with 4, a 6-point straight run with 1. The game spaces the stored
            // count evenly along the border. Measured on the 43 corners round the bay whose point
            // count differs from the stored count: resampled so, the mapper's vertex records sit a
            // median 3.3 m from the default vertex they name, against 6.0 m on the raw border - the
            // same fit as the 49 corners that agree already.
            if (quads != null && quads.Cols > 0 && quads.Cols != cols)
            {
                points = Resample(points, quads.Cols);
                cols = quads.Cols;
                count = cols + 1;
            }

            // The direction the rows grow in: the border's own perpendicular, Cross(up, tangent) in
            // Unity's frame - which is (tangent x up) in the game's, the side every border faces
            // (road_1x_sidewalk_end_4m: node 0's border runs -z at x +10.5 and its ground lies at +x,
            // node 1's runs +z at x -10.5 with its ground at -x).
            //
            // It used to come from the descriptor's per-point "terrain normals" with y zeroed. Those
            // are the ground's SURFACE normals: y > 0.9 on 289,722 of the 290,249 in the demo's
            // 3,341 descriptors, horizontal on 12 (harness Program1460). Flattening one keeps only its
            // tilt, and normalising blew that up into a full outward direction - 13,799 points - so
            // those columns of the 67-207 m strip grew wherever the ground happened to lean: across
            // the prefab's own road and on over the next one. On the ITCC -> Bern job that stood 1.6-6.5 m of
            // grass over the road between the last moving sample and the stop (prefab
            // road_sidewalk_end_4m 002935DE62403598, corner 1, U(-398..-417, -616..-649)) and 14-21 m
            // over the next 100 m of it at a gas station (274D4A84504D003F) (Program1461). Round the
            // drive, prefab terrain over the drivable way went from 15,974 half-metre cells to none, and on
            // the corners with a tilted normal the mapper's vertex records (absolute positions of grid
            // vertices) sit a median 3.1 m from the default vertex they replace against 6.3 m before,
            // p90 34 m against 105 m (Program1458/1459). Never flipped: the "away from the prefab's
            // centre" flip the old fallback applied fits those records worse (p90 56 m).
            var outward = new Vector3[count];

            for (int j = 0; j < count; j++)
            {
                Vector3 tangent = points[Mathf.Min(j + 1, cols)] - points[Mathf.Max(j - 1, 0)];
                tangent.y = 0f;

                if (tangent.sqrMagnitude < 1e-6f)
                {
                    // A border folding back on itself: grow away from the prefab rather than nowhere.
                    Vector3 away = points[j] - position;
                    away.y = 0f;
                    outward[j] = away.sqrMagnitude > 1e-6f ? away.normalized : Vector3.forward;
                    continue;
                }

                outward[j] = Vector3.Cross(Vector3.up, tangent.normalized);
            }

            // Profile rows, as for a road side.
            float[] steps = DefaultSteps;
            EtsSiiUnit profile = map.TerrainProfile(side.ProfileToken);

            if (profile != null)
            {
                float[] s = Floats(profile.GetArray("step"));
                if (s.Length > 0) steps = s;
            }

            float[] heights = EtsTerrainBuilder.ProfileHeights(map, side, steps.Length);
            var lateral = new List<float> { 0f };
            var height = new List<float> { 0f };
            float cumulative = 0f;

            // The last step repeats until the size is reached, as for a road side (see
            // EtsTerrainBuilder.BuildSide for the measurement).
            for (int i = 0; cumulative < side.Size - 0.001f && i < 512; i++)
            {
                float step = steps[Mathf.Min(i, steps.Length - 1)];
                float next = cumulative + step;
                float h0 = heights[Mathf.Min(i, heights.Length - 1)];
                float h1 = heights[Mathf.Min(i + 1, heights.Length - 1)];

                if (next > side.Size)
                {
                    float f = (side.Size - cumulative) / step;
                    lateral.Add(side.Size);
                    height.Add(Mathf.Lerp(h0, h1, f));
                    break;
                }

                lateral.Add(next);
                height.Add(h1);
                cumulative = next;
            }

            int rows = lateral.Count - 1;

            if (rows <= 0 || cols <= 0)
            {
                return;
            }

            int stride = cols + 1;
            var vertices = new Vector3[(rows + 1) * stride];
            var uv = new Vector2[vertices.Length];

            for (int j = 0; j <= cols; j++)
            {
                for (int i = 0; i <= rows; i++)
                {
                    Vector3 v = points[j] + outward[j] * lateral[i] + Vector3.up * height[i];
                    int o = i * stride + j;
                    vertices[o] = v;
                    uv[o] = new Vector2(v.x / EtsTerrainBuilder.FallbackTile, v.z / EtsTerrainBuilder.FallbackTile);
                }
            }

            // Vertices the mapper moved with the vertex tool: positions relative to the prefab's
            // own map node, in the game's frame, like a road's are to its start node. Measured on
            // the 49 corners round the bay whose default grid has the stored grid's shape, the
            // records sit a median 2.5 m from the default vertex they name; relative to any other
            // node of the prefab they land 10 to 60 m away. Applied only when our border has the
            // stored column count, so a record cannot land on a column that is not the game's.
            if (quads != null && quads.OffsetCount > 0 && quads.Cols == cols)
            {
                for (int k = 0; k < quads.OffsetCount; k++)
                {
                    int col = (int)quads.Offsets[k * 5], row = (int)quads.Offsets[k * 5 + 1];

                    if (row <= rows && col <= cols)
                    {
                        Vector3 v = origin + EtsModelBuilder.ToUnity(quads.Offsets[k * 5 + 2], quads.Offsets[k * 5 + 3], quads.Offsets[k * 5 + 4]);
                        int o = row * stride + col;
                        vertices[o] = v;
                        uv[o] = new Vector2(v.x / EtsTerrainBuilder.FallbackTile, v.z / EtsTerrainBuilder.FallbackTile);
                    }
                }
            }

            var byMaterial = new Dictionary<int, List<int>>();

            for (int i = 0; i < rows; i++)
            {
                for (int j = 0; j < cols; j++)
                {
                    int material = 0;

                    if (quads != null && quads.Rows > 0 && quads.Cols > 0)
                    {
                        int qc = Mathf.Clamp(j * quads.Cols / cols, 0, quads.Cols - 1);
                        material = quads.MainMaterial(Mathf.Min(i, quads.Rows - 1), qc);
                    }

                    if (!byMaterial.TryGetValue(material, out List<int> indices))
                    {
                        indices = new List<int>();
                        byMaterial[material] = indices;
                    }

                    int a = i * stride + j, b = i * stride + j + 1, c = (i + 1) * stride + j, d = (i + 1) * stride + j + 1;
                    bool flip = Vector3.Dot(Vector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]), Vector3.up) < 0f;
                    indices.Add(a); indices.Add(flip ? c : b); indices.Add(flip ? b : c);
                    indices.Add(b); indices.Add(flip ? c : d); indices.Add(flip ? d : c);
                }
            }

            foreach (KeyValuePair<int, List<int>> group in byMaterial)
            {
                ulong token = quads != null && group.Key < quads.MaterialTokens.Length ? quads.MaterialTokens[group.Key] : 0;
                string name = token != 0 ? EtsToken.Decode(token) : "default";

                // The "inv" brush is /material/terrain/invis.mat - material : "eut2.none" with
                // substance : "road" - collision without a surface, the same effect as a model's
                // "coll" part. Built with a collider and no renderer rather than skipped.
                bool invisible = name == "inv";
                Material material = invisible ? null : MaterialFor(map, token);

                if (material == null && !invisible)
                {
                    continue;
                }

                var mesh = new Mesh { name = "prefab terrain " + name };
                mesh.vertices = vertices;
                mesh.uv = token != 0 ? EtsTerrainBuilder.BrushUvs(map, token, vertices) : uv;
                mesh.triangles = group.Value.ToArray();
                mesh.RecalculateNormals();
                mesh.RecalculateBounds();

                var piece = new GameObject("prefab terrain " + name);
                piece.transform.SetParent(parent, true);
                piece.AddComponent<MeshFilter>().sharedMesh = mesh;
                var renderer = piece.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = material;
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.enabled = !invisible;

                // PhysX refuses to cook a sliver; a strip thinner than a metre is decoration only.
                Vector3 extent = mesh.bounds.size;

                if (options.Colliders && extent.x + extent.z > 1f && group.Value.Count >= 6)
                {
                    Mesh solid = EtsTerrainBuilder.CollisionMesh(mesh);

                    if (solid != null)
                    {
                        piece.AddComponent<MeshCollider>().sharedMesh = solid;
                    }
                }

                stats.TerrainQuads += group.Value.Count / 6;
            }

            // Walls under the corner's open edges where the ground beside it lies lower (EtsTerrainBuilder.AddSkirts: the
            // 9 m step at the Prague headquarters was a corner of this kind). The quad beside each edge names the brush.
            int skirtRows = quads != null ? quads.Rows : 0, skirtCols = quads != null ? quads.Cols : 0;
            EtsTerrainBuilder.AddSkirts(parent, true, vertices, rows, cols, stride, (qi, qj) =>
            {
                int slot = 0;

                if (skirtRows > 0 && skirtCols > 0)
                {
                    slot = quads.MainMaterial(Mathf.Min(qi, skirtRows - 1), Mathf.Clamp(qj * skirtCols / cols, 0, skirtCols - 1));
                }

                ulong token = quads != null && slot < quads.MaterialTokens.Length ? quads.MaterialTokens[slot] : 0;
                return token != 0 && EtsToken.Decode(token) == "inv" ? null : MaterialFor(map, token);
            });

            stats.TerrainSides++;
            stats.Vertices += vertices.Length;

            // World positions (the pieces keep them: SetParent(parent, true)); a strip can reach 200 m past the prefab.
            EtsGroundFill.NoteGround(null, vertices);

            // The corner's detail vegetation (grass, flowers), on the same stored quads the brushes above were read from
            // (the grid is resampled to the stored columns, so quad (i, j) is stored quad (min(i, rows - 1), j * stored / cols)),
            // inside the corner's own band (EtsTerrainSide.DetailBandFrom, 5 .. 100 m from the border by default).
            if (quads != null && quads.Rows > 0 && quads.Cols > 0)
            {
                int storedRows = quads.Rows, storedCols = quads.Cols, gridCols = cols;
                EtsDetailPatch.AddSide(map, quads, vertices, rows, cols, stride, parent, true, prefabUid, corner, side, lateral,
                    (i, j) => Mathf.Clamp(j * storedCols / gridCols, 0, storedCols - 1) * storedRows + Mathf.Min(i, storedRows - 1));
            }
        }

        private static Material MaterialFor(EtsMap map, ulong token)
        {
            if (MaterialsByToken.TryGetValue(token, out Material cached))
            {
                return cached;
            }

            Material material = null;
            EtsSiiUnit unit = token != 0 ? map.TerrainMaterial(token) : null;

            if (unit != null && unit.Has("path"))
            {
                material = EtsEngine.Materials.Load(unit.GetString("path"));
            }

            if (material == null)
            {
                material = EtsEngine.Materials.Load(EtsTerrainBuilder.FallbackMaterial);
            }

            MaterialsByToken[token] = material;
            return material;
        }

        private static float[] Floats(List<string> values)
        {
            var result = new float[values.Count];

            for (int i = 0; i < values.Count; i++)
            {
                float[] parsed = EtsSiiValue.ParseFloats(values[i]);
                result[i] = parsed.Length > 0 ? parsed[0] : 0f;
            }

            return result;
        }
    }
}
