using System;
using System.Collections.Generic;
using UnityEngine;

namespace SomniumSpace.Worlds.SomniumEuroTruck.Ets
{
    /// <summary>
    /// Builds the ground the way the game does: a strip beside each road side and terrain item,
    /// shaped across by the side's terrain profile (height per step, scaled by the coefficient)
    /// and painted quad by quad from the sector's .data file. Vertex (row i, column j) sits at the
    /// curve point for column j, moved sideways by the profile's cumulative step i (clipped to the
    /// strip's size) and up by the profile height there. Quads are grouped by their main brush
    /// material into one mesh each; textures tile in world metres.
    /// </summary>
    public static class EtsTerrainBuilder
    {
        /// <summary>Metres per texture repeat. Terrain textures in the game tile at about this size.</summary>
        public const float TextureTile = 16f;

        public const string FallbackMaterial = "/material/terrain/grass_ger_main.mat";

        /// <summary>
        /// Metres per repeat of the fallback grass: grass_ger_main.mat says aux[0] {13, 13}, and its
        /// 1.61 successor grass_ger_main_height.mat - 83 % of the terrain quads round the start - says
        /// texgen_0_gen {13, 13}. Whatever lays the fallback (the ground fill, a prefab corner with no
        /// brush) tiles at this, so it meets the real grass beside it at the same scale.
        /// </summary>
        public const float FallbackTile = 13f;

        private static readonly float[] DefaultSteps = { 1, 1, 1, 2, 6, 6, 10, 10, 10, 20, 40, 50, 50, 50, 100, 100 };

        /// <summary>
        /// Both terrain sides of a road, outside the slab of the given half widths, <paramref name="lift"/>
        /// metres above the road: a legacy CITY road's ground starts at the top of its kerb and sidewalk
        /// (+0.20 m). Measured two ways: at all 42 legacy road ends that meet a prefab within 150 m of the
        /// drive, the prefab's own terrain border begins at +0.20 m, 1.0 m outside offset + size + shoulder
        /// + sidewalk (harness Program1011); and the mappers' vertex records on city road terrain sit a
        /// median +0.20 m above the profile (Program1012, 302 records of row 3 and out, map-wide).
        /// </summary>
        public static void BuildRoadTerrain(EtsMap map, EtsMapRoad road, Vector3 p0, Vector3 t0, Vector3 p1, Vector3 t1,
            float rightWidth, float leftWidth, EtsMapBuilder.Options options, Transform parent, EtsMapBuilder.Stats stats, float lift = 0f)
        {
            int columns = Mathf.Max(1, Mathf.CeilToInt(road.Length / 15f)) * (road.Superfine ? 15 : road.HighPoly ? 3 : 1);

            // The road before this one on the same side: its profile is blended into ours over
            // the transition length (16, 8, 4 or 32 quads by the flag), so the ground is continuous.
            EtsTerrainSide previousRight = null, previousLeft = null;
            EtsMapNode startNode = map.FindNode(road.StartNode);

            if (startNode != null)
            {
                ulong previousUid = startNode.BackwardItem == road.Uid ? startNode.ForwardItem : startNode.BackwardItem;
                EtsMapRoad previous = previousUid != 0 ? map.FindRoad(previousUid) : null;

                if (previous != null)
                {
                    bool sameDirection = previous.EndNode == road.StartNode;
                    previousRight = sameDirection ? previous.Right : previous.Left;
                    previousLeft = sameDirection ? previous.Left : previous.Right;
                }
            }

            BuildSide(map, road.Right, false, rightWidth, road.Length, columns, p0, t0, p1, t1, p0, options, parent, stats, previousRight, TransitionColumns(road.Right.Transition), lift, road.Uid, 0);
            BuildSide(map, road.Left, true, leftWidth, road.Length, columns, p0, t0, p1, t1, p0, options, parent, stats, previousLeft, TransitionColumns(road.Left.Transition), lift, road.Uid, 1);

            // The slab itself is ground too, with or without terrain beside it: the ground fill measures the tiles
            // under it again once the stream settles there (EtsGroundFill.NoteGround).
            NoteRoadGround(p0, t0, p1, t1, Mathf.Max(rightWidth, leftWidth) + 2f, parent);
            EtsVegetationBuilder.Scatter(map, road.Right, false, rightWidth, road.Length, road.Seed, p0, t0, p1, t1, options, parent, stats);
            EtsVegetationBuilder.Scatter(map, road.Left, true, leftWidth, road.Length, road.Seed, p0, t0, p1, t1, options, parent, stats);
        }

        /// <summary>A road's slab for the ground fill: the box round five points of its curve, widened by the half width.</summary>
        private static void NoteRoadGround(Vector3 p0, Vector3 t0, Vector3 p1, Vector3 t1, float halfWidth, Transform parent)
        {
            Vector3 min = p0, max = p0;

            for (int i = 1; i <= 4; i++)
            {
                Vector3 p = EtsMapBuilder.Hermite(p0, t0, p1, t1, i / 4f);
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }

            var pad = new Vector3(halfWidth, 1f, halfWidth);
            EtsGroundFill.NoteGround(parent, min - pad, max + pad);
        }

        /// <summary>A terrain item: ground on both sides of the line between its two nodes.</summary>
        public static GameObject BuildTerrainItem(EtsMap map, EtsMapTerrainItem item, EtsMapBuilder.Options options, Transform parent, EtsMapBuilder.Stats stats)
        {
            EtsMapNode start = map.FindNode(item.StartNode);
            EtsMapNode end = map.FindNode(item.EndNode);

            if (start == null || end == null)
            {
                stats.TerrainSkipped++;
                return null;
            }

            var startPosition = new[] { start.Position[0] + item.StartOffset[0], start.Position[1] + item.StartOffset[1], start.Position[2] + item.StartOffset[2] };
            var endPosition = new[] { end.Position[0] + item.EndOffset[0], end.Position[1] + item.EndOffset[1], end.Position[2] + item.EndOffset[2] };
            Vector3 p0 = EtsMapBuilder.ToWorld(startPosition, options.CenterGame);
            // The vertex-tool records are positions relative to the node itself, offsets aside.
            Vector3 nodeOrigin = EtsMapBuilder.ToWorld(start.Position, options.CenterGame);
            Vector3 p1 = EtsMapBuilder.ToWorld(endPosition, options.CenterGame);
            float length = Mathf.Max(0.5f, item.Length);
            Vector3 t0 = EtsMapBuilder.ToWorldDirection(start.Forward()) * length;
            Vector3 t1 = EtsMapBuilder.ToWorldDirection(end.Forward()) * length;

            var root = new GameObject("terrain " + item.Right.Profile);
            root.transform.SetParent(parent, false);

            int columns = Mathf.Max(1, Mathf.CeilToInt(length / item.ColumnStep));

            // The item's head flags switch its sides' detail vegetation off (bits 11 right, 28 left; EtsMapTerrainItem).
            item.Right.DetailVegetation = item.RightDetailVegetation;
            item.Left.DetailVegetation = item.LeftDetailVegetation;
            BuildSide(map, item.Right, false, 0f, length, columns, p0, t0, p1, t1, nodeOrigin, options, root.transform, stats, detailUid: item.Uid, detailSide: 0);
            BuildSide(map, item.Left, true, 0f, length, columns, p0, t0, p1, t1, nodeOrigin, options, root.transform, stats, detailUid: item.Uid, detailSide: 1);
            EtsVegetationBuilder.Scatter(map, item.Right, false, 0f, length, item.Seed, p0, t0, p1, t1, options, root.transform, stats);
            EtsVegetationBuilder.Scatter(map, item.Left, true, 0f, length, item.Seed, p0, t0, p1, t1, options, root.transform, stats);
            stats.TerrainItems++;
            return root;
        }

        /// <summary>
        /// A bezier patch: the bicubic surface through its sixteen control points, sampled on
        /// its quad grid (rows along the control points' x index, columns along their z index)
        /// and painted from its quad data like a road side. The control points are relative to
        /// the item's node in the game's frame; so are the vertex-tool records.
        /// </summary>
        public static GameObject BuildBezierPatch(EtsMap map, EtsMapBezierPatch patch, EtsMapBuilder.Options options, Transform parent, EtsMapBuilder.Stats stats)
        {
            EtsMapNode node = map.FindNode(patch.NodeUid);

            if (node == null || patch.Quads == null)
            {
                stats.TerrainSkipped++;
                return null;
            }

            int rows = patch.Quads.Rows > 0 ? patch.Quads.Rows : patch.TesselationX + 1;
            int cols = patch.Quads.Cols > 0 ? patch.Quads.Cols : patch.TesselationZ + 1;

            if (rows <= 0 || cols <= 0 || rows > 256 || cols > 256)
            {
                stats.TerrainSkipped++;
                return null;
            }

            Vector3 origin = EtsMapBuilder.ToWorld(node.Position, options.CenterGame);
            var control = new Vector3[16];

            for (int i = 0; i < 16; i++)
            {
                control[i] = EtsModelBuilder.ToUnity(patch.ControlPoints[i * 3], patch.ControlPoints[i * 3 + 1], patch.ControlPoints[i * 3 + 2]);
            }

            int stride = cols + 1;
            var vertices = new Vector3[(rows + 1) * stride];

            for (int i = 0; i <= rows; i++)
            {
                for (int j = 0; j <= cols; j++)
                {
                    vertices[i * stride + j] = origin + Bezier(control, (float)i / rows, (float)j / cols);
                }
            }

            EtsTerrainQuadData quads = patch.Quads;

            for (int k = 0; k < quads.OffsetCount; k++)
            {
                int col = (int)quads.Offsets[k * 5], row = (int)quads.Offsets[k * 5 + 1];

                if (row <= rows && col <= cols)
                {
                    vertices[row * stride + col] = origin + EtsModelBuilder.ToUnity(quads.Offsets[k * 5 + 2], quads.Offsets[k * 5 + 3], quads.Offsets[k * 5 + 4]);
                }
            }

            var root = new GameObject("bezier patch");
            root.transform.SetParent(parent, false);
            EmitQuads(map, quads, vertices, rows, cols, stride, "patch", options, root.transform, stats, patch.Uid, 0);
            stats.TerrainItems++;
            return root;
        }

        /// <summary>A point of the bicubic Bezier surface: Bernstein weights along u (x index) and v (z index).</summary>
        public static Vector3 Bezier(Vector3[] control, float u, float v)
        {
            float u1 = 1f - u, v1 = 1f - v;
            float[] bu = { u1 * u1 * u1, 3f * u * u1 * u1, 3f * u * u * u1, u * u * u };
            float[] bv = { v1 * v1 * v1, 3f * v * v1 * v1, 3f * v * v * v1, v * v * v };
            Vector3 p = Vector3.zero;

            for (int i = 0; i < 4; i++)
            {
                for (int j = 0; j < 4; j++)
                {
                    p += bu[i] * bv[j] * control[i * 4 + j];
                }
            }

            return p;
        }

        /// <summary>Transition length in quad columns, from the two-bit flag: 16, 8, 4 or 32.</summary>
        public static int TransitionColumns(int flag)
        {
            return flag == 1 ? 8 : flag == 2 ? 4 : flag == 3 ? 32 : 16;
        }

        /// <summary>Profile heights per step index, scaled by the side's coefficient; index 0 is the road edge.</summary>
        public static float[] ProfileHeights(EtsMap map, EtsTerrainSide side, int stepCount)
        {
            EtsSiiUnit profile = map.TerrainProfile(side.ProfileToken);
            float[] h = profile != null ? Floats(profile.GetArray("height")) : new float[0];
            var result = new float[stepCount + 1];

            for (int i = 0; i <= stepCount; i++)
            {
                result[i] = (i < h.Length ? h[i] : 0f) * side.Coefficient;
            }

            return result;
        }

        private static void BuildSide(EtsMap map, EtsTerrainSide side, bool left, float startOffset, float length, int defaultColumns,
            Vector3 p0, Vector3 t0, Vector3 p1, Vector3 t1, Vector3 origin, EtsMapBuilder.Options options, Transform parent, EtsMapBuilder.Stats stats,
            EtsTerrainSide previous = null, int transitionColumns = 0, float lift = 0f, ulong detailUid = 0, int detailSide = 0)
        {
            if (side == null || side.Size <= 0.01f)
            {
                return;
            }

            float[] steps = DefaultSteps;
            float[] heights = null;
            EtsSiiUnit profile = map.TerrainProfile(side.ProfileToken);

            if (profile != null)
            {
                float[] s = Floats(profile.GetArray("step"));
                float[] h = Floats(profile.GetArray("height"));

                if (s.Length > 0)
                {
                    steps = s;
                }

                if (h.Length > 0)
                {
                    heights = h;
                }
            }

            // The previous road's profile, blended into this one over the first columns.
            float[] previousHeights = null;

            if (previous != null && previous.Size > 0.01f && transitionColumns > 0)
            {
                previousHeights = ProfileHeights(map, previous, steps.Length);
            }

            EtsTerrainQuadData quads = side.Quads;
            int cols = quads != null && quads.Cols > 0 ? quads.Cols : defaultColumns;

            // Rows: one per profile step inside the size, the last clipped to the size.
            var lateral = new List<float> { 0f };
            var height = new List<float> { 0f };
            float cumulative = 0f;

            // When the profile's steps run out before the size, the LAST step repeats until the
            // size is reached. Measured: profile113 is the single step [5], and a 40 m side on it
            // is stored with an 8-row grid whose vertex records sit 5, 10, 15 ... 36 m out; a 35 m
            // side has 7 rows, a 45 m side 9, profile121 [4] at 12 m has 3. Stopping at the end
            // of the array built such a side one step wide and left the rest bare - the 46 x 52 m
            // hole beside the DomDepo was exactly that. Heights repeat their last value likewise.
            for (int i = 0; cumulative < side.Size - 0.001f && i < 512; i++)
            {
                float step = steps[Mathf.Min(i, steps.Length - 1)];
                float next = cumulative + step;
                float h0 = heights != null && heights.Length > 0 ? heights[Mathf.Min(i, heights.Length - 1)] : 0f;
                float h1 = heights != null && heights.Length > 0 ? heights[Mathf.Min(i + 1, heights.Length - 1)] : h0;

                if (next > side.Size)
                {
                    float f = (side.Size - cumulative) / step;
                    lateral.Add(side.Size);
                    height.Add(Mathf.Lerp(h0, h1, f) * side.Coefficient);
                    break;
                }

                lateral.Add(next);
                height.Add(h1 * side.Coefficient);
                cumulative = next;
            }

            int rows = lateral.Count - 1;

            if (quads != null && quads.Rows > 0 && quads.Rows < rows)
            {
                rows = quads.Rows;
            }

            if (rows <= 0 || cols <= 0)
            {
                return;
            }

            float sign = left ? -1f : 1f;
            int stride = cols + 1;
            var vertices = new Vector3[(rows + 1) * stride];
            var uv = new Vector2[vertices.Length];

            for (int j = 0; j <= cols; j++)
            {
                float s = (float)j / cols;
                Vector3 point = EtsMapBuilder.Hermite(p0, t0, p1, t1, s);
                Vector3 forward = EtsMapBuilder.HermiteTangent(p0, t0, p1, t1, s);
                forward.y = 0f;

                if (forward.sqrMagnitude < 1e-6f)
                {
                    forward = p1 - p0;
                    forward.y = 0f;
                }

                forward.Normalize();
                Vector3 right = Vector3.Cross(Vector3.up, forward).normalized * sign;
                float blend = previousHeights == null ? 1f : Mathf.Clamp01((float)j / transitionColumns);

                for (int i = 0; i <= rows; i++)
                {
                    float h = height[i];
                    float lat = lateral[i];

                    // Ease the previous road's profile into this one over the transition. Only the
                    // SHAPE eases: the strip keeps its full width the whole way. Narrowing the
                    // width toward the previous road's, as this used to, cut a triangular wedge out
                    // of the start of every strip whose neighbour was narrower, which is what left
                    // the ground looking like scattered shards with gaps between them.
                    if (blend < 1f)
                    {
                        h = Mathf.Lerp(previousHeights[Mathf.Min(i, previousHeights.Length - 1)], height[i], blend);
                    }

                    // The vertex records below are absolute positions and are not lifted.
                    Vector3 v = point + right * (startOffset + lat) + Vector3.up * (h + lift);
                    int o = i * stride + j;
                    vertices[o] = v;
                    uv[o] = new Vector2(v.x / TextureTile, v.z / TextureTile);
                }
            }

            // Vertices the mapper moved with the vertex tool. The record is NOT a nudge to add to
            // the grid vertex: it is the vertex's POSITION, relative to the item's backward (start)
            // node, in the game's frame - TruckLib, TerrainQuadData: "Offsets of terrain vertices.
            // Positions are relative to the position of the backward node." Measured on the 164
            // sides round the bay that carry them: read as deltas the median vertex moved 58 m
            // (max 286 m), strips folded over themselves and over their neighbours (the flashing)
            // and the shaped strips left the ground they were meant to cover bare (the holes);
            // read as positions the median move is 2.4 m, the neighbours no longer share a plane
            // and the rail's hand-shaped strip covers the hole beside it. Because the record is a
            // position, it is right by index whatever grid we built, so it is applied whenever
            // its indices exist rather than only when the grids are the same size.
            if (quads != null && quads.OffsetCount > 0)
            {
                for (int k = 0; k < quads.OffsetCount; k++)
                {
                    int col = (int)quads.Offsets[k * 5], row = (int)quads.Offsets[k * 5 + 1];

                    if (row <= rows && col <= cols)
                    {
                        Vector3 v = origin + EtsModelBuilder.ToUnity(quads.Offsets[k * 5 + 2], quads.Offsets[k * 5 + 3], quads.Offsets[k * 5 + 4]);
                        int o = row * stride + col;
                        vertices[o] = v;
                        uv[o] = new Vector2(v.x / TextureTile, v.z / TextureTile);
                    }
                }
            }

            // A terrain item's side (inner edge at its centre line) kept out of the lanes' clearance (ClampOverRoads).
            if (startOffset == 0f && detailUid != 0)
            {
                ClampOverRoads(map, vertices, rows, cols, stride, options.CenterGame);
            }

            // The grass grid: row line i lies lateral[i] metres from the side's inner edge (the detail band's measure).
            EmitQuads(map, quads, vertices, rows, cols, stride, left ? "left" : "right", options, parent, stats, detailUid, detailSide, side, lateral);
        }

        /// <summary>
        /// The meshes for a vertex grid painted by quad data: one per pair of brushes, with the
        /// single-brush stand-in of each. Shared by road and terrain-item sides and by bezier
        /// patches, whose grids come from different geometry but the same quad data.
        /// </summary>
        public static void EmitQuads(EtsMap map, EtsTerrainQuadData quads, Vector3[] vertices, int rows, int cols, int stride, string sideName,
            EtsMapBuilder.Options options, Transform parent, EtsMapBuilder.Stats stats,
            ulong detailUid = 0, int detailSide = 0, EtsTerrainSide detailSideData = null, IList<float> lateral = null)
        {
            // Quads grouped by the PAIR of brushes on them.
            //
            // Each quad is painted with two brushes - a main, a blend, and how far the blend covers
            // it, 0 to 15. Measured round Frankfurt, 117,821 of 385,392 quads (30.6%) carry a
            // second brush, with the cover spread right across the range. Quads sharing the same
            // two brushes go into one mesh drawn with the two-layer shader, the blend's texture
            // laid over the main's through a second uv set and faded by the cover, which is carried
            // in the vertex alpha. Quads with no blend group by their main brush alone, as before.
            var byPair = new Dictionary<int, List<int>>();
            var quadsOfPair = new Dictionary<int, List<int>>();

            for (int i = 0; i < rows; i++)
            {
                for (int j = 0; j < cols; j++)
                {
                    int main = 0, blend = 0, opacity = 0;

                    if (quads != null && i < quads.Rows && j < quads.Cols)
                    {
                        main = quads.MainMaterial(i, j);
                        blend = quads.BlendMaterial(i, j);
                        opacity = quads.BlendOpacity(i, j);
                    }

                    if (blend == main || opacity <= 0)
                    {
                        blend = main;
                    }

                    int key = (main << 4) | blend;

                    if (!byPair.TryGetValue(key, out List<int> indices))
                    {
                        indices = new List<int>();
                        byPair[key] = indices;
                        quadsOfPair[key] = new List<int>();
                    }

                    quadsOfPair[key].Add(i * 1024 + j);

                    int a = i * stride + j, b = i * stride + j + 1, c = (i + 1) * stride + j, d = (i + 1) * stride + j + 1;
                    // Each triangle turned up on its own (2026-10-30). The mappers' vertex records fold some quads - one
                    // triangle up, the other over - and with one winding for the pair the folded half faced the ground
                    // and was culled: lab probes found such "FACES DOWN" terrain on the hills north of Graz (profile112,
                    // 0.8-1 km from the A9), and the forest-far strips above the fold stood over the sky.
                    bool flip = Vector3.Dot(Vector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]), Vector3.up) < 0f;
                    indices.Add(a); indices.Add(flip ? c : b); indices.Add(flip ? b : c);
                    bool flip2 = Vector3.Dot(Vector3.Cross(vertices[d] - vertices[b], vertices[c] - vertices[b]), Vector3.up) < 0f;
                    indices.Add(b); indices.Add(flip2 ? c : d); indices.Add(flip2 ? d : c);
                    if (flip != flip2) FoldedQuads++;
                }
            }

            // What each quad is drawn with (its single-brush stand-in, or null for invisible ground), for the skirts below.
            var drawnWith = new Dictionary<int, Material>();

            foreach (KeyValuePair<int, List<int>> group in byPair)
            {
                int mainSlot = group.Key >> 4, blendSlot = group.Key & 15;
                ulong token = quads != null && mainSlot < quads.MaterialTokens.Length ? quads.MaterialTokens[mainSlot] : 0;
                ulong blendToken = blendSlot != mainSlot && quads != null && blendSlot < quads.MaterialTokens.Length ? quads.MaterialTokens[blendSlot] : 0;
                string name = token != 0 ? EtsToken.Decode(token) : "default";

                if (name == "inv")
                {
                    // Not a hole in the map: terrain_mat.inv is /material/terrain/invis.mat, whose
                    // whole body is  material : "eut2.none" { substance : "road" }  - the same
                    // effect SCS puts on a model's "coll" part, i.e. surface you can drive on and
                    // cannot see. A substance is a physics property (grip, tyre noise) and would be
                    // meaningless on something with no collision. Skipping these outright left the
                    // truck falling through 368 of the 785 bare cells round the bay, most of them
                    // under dirt decals that are painted over invisible ground on purpose. Built
                    // here with a collider and no renderer.
                    InvisibleQuads += group.Value.Count / 6;
                    Mesh hidden = TerrainMesh("terrain " + sideName + " invisible", vertices, TerrainUvs(vertices, new TerrainLay { SizeU = 16f, SizeV = 16f }), null, null, group.Value.ToArray());

                    // Its collider waits for the map's road footprints (DeferInvisible, 2026-10-27).
                    MeshRenderer hiddenRenderer = Piece(parent, "terrain " + sideName + " invisible", hidden, null, false);
                    hiddenRenderer.enabled = false;

                    if (options.Colliders)
                    {
                        DeferInvisible(map, hiddenRenderer.gameObject, hidden, options.CenterGame);
                    }
                    drawnWith[group.Key] = null;
                    continue;
                }

                Color32 tint = BrushTint(quads, mainSlot);
                Material pairMaterial = blendToken != 0 ? PairMaterial(map, token, blendToken, tint) : null;

                // The single-brush version of this group, drawn whenever the blended one is not:
                // a quad more than half covered by its blend brush is given the blend brush.
                ulong flatToken = token;
                Color32 flatTint = tint;

                if (blendToken != 0 && MostlyBlend(quads, quadsOfPair[group.Key]))
                {
                    flatToken = blendToken;
                    flatTint = BrushTint(quads, blendSlot);
                }

                Material flatMaterial = TerrainMaterial(map, flatToken, flatTint);
                drawnWith[group.Key] = flatMaterial;

                if (flatMaterial == null)
                {
                    continue;
                }

                int[] triangles = group.Value.ToArray();
                string flatName = "terrain " + sideName + " " + EtsToken.Decode(flatToken);

                // Every brush says how large its texture is on the ground, and they differ widely:
                // asphalt repeats every 8 m, most fields every 10 to 13 m, a few backdrops every
                // 100 m or more. 579 of the 583 terrain materials carry it, and all of them were
                // being drawn at a flat 16 m, which stretched or shrank nearly every surface. The
                // third number is a rotation in degrees - barley_field_horiz is the same texture as
                // barley_field with a 90 in that slot.
                Mesh flatMesh = TerrainMesh(flatName, vertices, TerrainUvs(vertices, TerrainScale(map, flatToken)), null, null, triangles);
                MeshRenderer flatRenderer = Piece(parent, flatName, flatMesh, flatMaterial, options.Colliders);

                stats.TerrainQuads += triangles.Length / 6;

                if (pairMaterial == null)
                {
                    PlainMeshes++;
                    continue;
                }

                // The blended version: the main brush's texture through the first uv set, the
                // blend brush's through the second, faded by the cover carried in the vertex alpha.
                // Built alongside the flat one and shown instead of it only while PairsVisible -
                // off until it has been seen to draw correctly, because a mesh that fails to draw
                // is a hole in the ground with the sky showing through it.
                string pairName = "terrain " + sideName + " " + name + " + " + EtsToken.Decode(blendToken);
                Mesh pairMesh = TerrainMesh(pairName, vertices, TerrainUvs(vertices, TerrainScale(map, token)),
                    TerrainUvs(vertices, TerrainScale(map, blendToken)), BlendCover(vertices.Length, stride, quadsOfPair[group.Key], quads), triangles);
                MeshRenderer pairRenderer = Piece(parent, pairName, pairMesh, pairMaterial, false);

                pairRenderer.enabled = PairsVisible;
                flatRenderer.enabled = !PairsVisible;
                Pairs.Add(new PairRenderers { Blended = pairRenderer, Flat = flatRenderer });
                PairMeshes++;
                BlendedQuads += triangles.Length / 6;
            }

            // Skirts under the strip's open edges (holes, 2026-09-25): the quad next to each edge segment names its material.
            var quadKey = new int[rows * cols];

            foreach (KeyValuePair<int, List<int>> pair in quadsOfPair)
            {
                foreach (int id in pair.Value)
                {
                    int qi = id / 1024, qj = id % 1024;

                    if (qi < rows && qj < cols)
                    {
                        quadKey[qi * cols + qj] = pair.Key;
                    }
                }
            }

            AddSkirts(parent, false, vertices, rows, cols, stride, (qi, qj) => drawnWith.TryGetValue(quadKey[qi * cols + qj], out Material m) ? m : null);

            stats.TerrainSides++;
            stats.Vertices += vertices.Length;

            // Every road side, terrain side and bezier patch passes here: the ground fill measures the tiles under it
            // again once the stream has settled there, instead of keeping a sheet guessed before this ground existed.
            EtsGroundFill.NoteGround(parent, vertices);

            // The game's detail vegetation on this grid (grass, flowers): its quads' defs, kept on the item's root and
            // drawn in cells round the eye by EtsVegetationLod while the item is loaded.
            EtsDetailPatch.AddSide(map, quads, vertices, rows, cols, stride, parent, false, detailUid, detailSide, detailSideData, lateral);
        }

        /// <summary>
        /// How a brush material lies on the ground: metres per texture repeat across and along,
        /// and a rotation in degrees. The game keeps these in the material's first auxiliary
        /// vector, "aux[0]" - { u_size, v_size, rotation }.
        /// </summary>
        private struct TerrainLay
        {
            public float SizeU;
            public float SizeV;
            public float Rotation;
        }

        private static readonly Dictionary<ulong, TerrainLay> LayByToken = new Dictionary<ulong, TerrainLay>();

        private static TerrainLay TerrainScale(EtsMap map, ulong token)
        {
            if (LayByToken.TryGetValue(token, out TerrainLay cached))
            {
                return cached;
            }

            var lay = new TerrainLay { SizeU = TextureTile, SizeV = TextureTile, Rotation = 0f };
            EtsSiiUnit unit = token != 0 ? map.TerrainMaterial(token) : null;
            string path = unit != null ? unit.GetString("path", null) : null;
            string text = path != null && EtsEngine.Files != null ? EtsEngine.Files.ReadText(path) : null;

            if (text != null)
            {
                EtsMaterialDef def = EtsMaterialDef.Parse(text, path);
                float[] aux = def.GetFloats("aux[0]");

                if (aux.Length >= 2 && aux[0] > 0.01f && aux[1] > 0.01f)
                {
                    lay.SizeU = aux[0];
                    lay.SizeV = aux[1];
                }

                if (aux.Length >= 3)
                {
                    lay.Rotation = aux[2];
                }

                // Game 1.61's terrain brushes are /umatlib/legacy/terrain/*.mat and write the same
                // numbers as texgen_0_gen { u, v } and texgen_0_rot instead of aux[0]: the old
                // /material/terrain/grass_ger_main.mat says aux[0] {13, 13}, its 1.61 successor
                // grass_ger_main_height.mat says texgen_0_gen {13, 13} over the same texture. Every
                // brush within 400 m of the start is one of these (harness Program975: grass 13 m,
                // pebbles 6, fallen leaves 8, asphalt tiles 8, mud 4, dirt 3), and all of them were
                // falling back to 16 m - the small-detail ones drawn up to 5 times too large.
                if (aux.Length < 2)
                {
                    float[] texgen = def.GetFloats("texgen_0_gen");

                    if (texgen.Length >= 2 && texgen[0] > 0.01f && texgen[1] > 0.01f)
                    {
                        lay.SizeU = texgen[0];
                        lay.SizeV = texgen[1];
                        lay.Rotation = def.GetFloat("texgen_0_rot", 0f);
                    }
                }
            }

            LayByToken[token] = lay;
            return lay;
        }

        /// <summary>Ground texture coordinates: world metres divided by the brush's own repeat size.</summary>
        private static Vector2[] TerrainUvs(Vector3[] vertices, TerrainLay lay)
        {
            var uv = new Vector2[vertices.Length];
            float radians = lay.Rotation * Mathf.Deg2Rad;
            float cos = Mathf.Cos(radians), sin = Mathf.Sin(radians);

            for (int i = 0; i < vertices.Length; i++)
            {
                float x = vertices[i].x, z = vertices[i].z;
                uv[i] = new Vector2((x * cos - z * sin) / lay.SizeU, (x * sin + z * cos) / lay.SizeV);
            }

            return uv;
        }

        /// <summary>The Unity material for a brush token, for callers outside the terrain builder (the road median).</summary>
        public static Material BrushMaterial(EtsMap map, ulong token)
        {
            return TerrainMaterial(map, token);
        }

        /// <summary>World-tiled uvs for a brush at the size its material asks for.</summary>
        public static Vector2[] BrushUvs(EtsMap map, ulong token, Vector3[] vertices)
        {
            return TerrainUvs(vertices, TerrainScale(map, token));
        }

        private static readonly Dictionary<ulong, Material> MaterialsByToken = new Dictionary<ulong, Material>();

        /// <summary>The Unity material for a brush material token, via terrain_material.sii.</summary>
        private static Material TerrainMaterial(EtsMap map, ulong token)
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
                material = EtsEngine.Materials.Load(FallbackMaterial);
            }

            MaterialsByToken[token] = material;
            return material;
        }

        private static readonly Dictionary<long, Material> TintedByToken = new Dictionary<long, Material>();

        /// <summary>
        /// The brush as the map painted it, tint included.
        ///
        /// Beside every brush the sector stores a colour, and 28.6% of them are not white: warm
        /// cream (255,242,219) over stubble, greys from 216 down to 164 on worn concrete, pale
        /// green (215,227,212), a yellow (255,250,148). That is how one grass or asphalt texture
        /// becomes a dozen different-looking fields and yards, and dropping it is a good part of
        /// why the ground read as one flat repeated sheet.
        ///
        /// A tint makes a second material, so they are cached by brush and colour, quantised to
        /// 32 steps a channel - the colours are broad washes and nothing needs finer than that.
        /// </summary>
        private static Material TerrainMaterial(EtsMap map, ulong token, Color32 tint)
        {
            Material plain = TerrainMaterial(map, token);

            if (plain == null || (tint.r > 247 && tint.g > 247 && tint.b > 247))
            {
                return plain;
            }

            long key = (long)(token & 0xFFFFFFFFUL) << 16 | (long)(tint.r >> 3) << 10 | (long)(tint.g >> 3) << 5 | (long)(tint.b >> 3);

            if (TintedByToken.TryGetValue(key, out Material cached))
            {
                return cached;
            }

            var tinted = new Material(plain) { name = plain.name + " tinted" };

            if (tinted.HasProperty(BaseColorId))
            {
                Color was = tinted.GetColor(BaseColorId);
                tinted.SetColor(BaseColorId, new Color(was.r * tint.r / 255f, was.g * tint.g / 255f, was.b * tint.b / 255f, was.a));
            }

            TintedByToken[key] = tinted;
            return tinted;
        }

        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

        /// <summary>
        /// How the ground came out, for the build report: meshes drawn with one brush, meshes
        /// drawn with a blended pair, and quads in the latter. If the pair count is zero the
        /// two-layer shader was not found and the ground fell back to single brushes.
        /// </summary>
        public static int PlainMeshes;
        public static int PairMeshes;
        public static int BlendedQuads;
        private static int _pairsReported;

        /// <summary>A blended-pair group's two renderings: the two-layer mesh and its single-brush stand-in.</summary>
        public sealed class PairRenderers
        {
            public MeshRenderer Blended;
            public MeshRenderer Flat;
        }

        public static readonly List<PairRenderers> Pairs = new List<PairRenderers>();

        /// <summary>
        /// Whether blended-pair groups draw with the two-layer shader or with their single-brush
        /// stand-in. The blended meshes were suspected of being the holes in the ground and hidden
        /// for a run; the holes turned out to be prefab ground pieces that were never built (see
        /// EtsModelBuilder), and the build report showed the blended meshes whole - so they are on.
        /// F9 swaps them live so the difference can be looked at.
        /// </summary>
        public static bool PairsVisible = true;

        public static void SetPairsVisible(bool on)
        {
            PairsVisible = on;
            Pairs.RemoveAll(p => p.Blended == null || p.Flat == null);

            foreach (PairRenderers pair in Pairs)
            {
                pair.Blended.enabled = on;
                pair.Flat.enabled = !on;
            }
        }

        /// <summary>
        /// What the first blended mesh is actually made of, for the console: its counts and what
        /// its material binds. Read this when the blended ground looks wrong.
        /// </summary>
        public static string Diagnose()
        {
            Pairs.RemoveAll(p => p.Blended == null || p.Flat == null);

            if (Pairs.Count == 0)
            {
                return "Blended ground: none built.";
            }

            MeshRenderer r = Pairs[0].Blended;
            Mesh mesh = r.GetComponent<MeshFilter>() != null ? r.GetComponent<MeshFilter>().sharedMesh : null;
            Material m = r.sharedMaterial;

            // Every built item is static-batched as it is registered (EtsMapViewer.Register -> CombineStatic), so by the time
            // the load's summary asks, this filter's mesh is the batch's "Combined Mesh (root: ...)", which Unity makes
            // unreadable: reading its triangles, uv, uv2 and colours logged four "Not allowed to access ..." errors per load
            // (checker probes 2100/2108 in a Unity 6000.3.5f2 player loop). Such a mesh is only named, not read.
            if (mesh != null && !mesh.isReadable)
            {
                return "Blended ground sample '" + r.name + "': static-batched into '" + mesh.name + "' (not readable; " + mesh.vertexCount + " verts in the batch); " +
                       (m == null ? "no material" : m.shader.name + (m.shader.isSupported ? "" : " (NOT SUPPORTED)")) + "; blended " + (PairsVisible ? "SHOWN" : "hidden, flat stand-ins shown") + " (F9 swaps).";
            }

            string meshLine = mesh == null ? "no mesh" : mesh.vertexCount + " verts, " + mesh.triangles.Length / 3 + " tris, uv " + mesh.uv.Length + ", uv2 " + mesh.uv2.Length + ", colours " + mesh.colors32.Length + ", bounds " + mesh.bounds.size.ToString("0");
            string matLine = m == null ? "no material" : m.shader.name + (m.shader.isSupported ? "" : " (NOT SUPPORTED)") + ", base " + (m.GetTexture("_BaseMap") != null ? m.GetTexture("_BaseMap").name : "NONE") +
                             ", over " + (m.GetTexture("_MultMap") != null ? m.GetTexture("_MultMap").name : "NONE") + ", mode " + m.GetFloat("_LayerMode") + ", strength " + m.GetFloat("_MultStrength") +
                             ", colour " + m.GetColor("_BaseColor").ToString("0.00") + ", forward pass " + (m.FindPass("Forward") >= 0 ? "present" : "MISSING") + ", queue " + m.renderQueue;
            return "Blended ground sample '" + r.name + "': " + meshLine + "; " + matLine + "; blended " + (PairsVisible ? "SHOWN" : "hidden, flat stand-ins shown") + " (F9 swaps).";
        }

        /// <summary>A terrain mesh from shared vertices, its own uv sets and colours, and a group's triangles.</summary>
        /// <summary>The deepest a skirt hangs below the edge it closes, metres.</summary>
        public const float SkirtDepth = 20f;

        /// <summary>Skirt segments built (edges left open over lower terrain or nothing), for the build report.</summary>
        public static int SkirtSegments;

        /// <summary>Edge segments looked at for a skirt and left alone (closed, or over a road, a yard or a model).</summary>
        public static int SkirtsNotNeeded;

        /// <summary>Edge segments left open because nothing lies within SkirtDepth under them: a wall there would hang in the air (2026-10-27).</summary>
        public static int SkirtsInAir;

        private static readonly RaycastHit[] SkirtHits = new RaycastHit[16];

        private sealed class SkirtMesh
        {
            public readonly List<Vector3> Positions = new List<Vector3>();
            public readonly List<Vector3> Normals = new List<Vector3>();
            public readonly List<Vector2> Uvs = new List<Vector2>();
            public readonly List<int> Triangles = new List<int>();
        }

        /// <summary>
        /// Walls under a terrain strip's open edges (holes, 2026-09-25).
        ///
        /// Terrain strips meet each other and the prefabs in plan, but not always in height: a strip ends in mid-air
        /// where its neighbour carries on lower down. Measured in the render lab at the Prague headquarters: the west
        /// corner of prefab road_sidewalk_end_4m (002935DEE4604D6A) grows on profile14 and is 8-9 m up where the road
        /// that ends at it (002935DE11704D6C, left side profile0, flat) is at road level - a 9 m step along 90 m with
        /// nothing in it. From the road the ray went through the step and under the higher strip, whose underside is
        /// not drawn, into the void: a "lake" of sky 100-200 m from the cab, 1.3-1.6 % of the view below the horizon
        /// (6,000-8,000 pixels of a 1280 x 720 cab view), and the same at the other ends of the drive. From above the
        /// ground is whole, so the ground fill (which fills only where a column has no ground at all) never saw it.
        ///
        /// So each strip's outer row and both end columns get a skirt: for every edge segment one ray looks down just
        /// outside it; where the first ground below is terrain more than 0.4 m lower, a wall in the adjacent quad's
        /// brush hangs from the edge to 0.6 m into that ground; where there is nothing below, it hangs
        /// <see cref="SkirtDepth"/>. Never onto a road, a yard or a model below (a wall must not stand on a road or
        /// across a tunnel mouth), never where the neighbour meets the edge. Visual only: no collider, no shadow. The
        /// inner edge (row 0) is the road's or the prefab's and is left alone.
        /// </summary>
        public static void AddSkirts(Transform parent, bool worldVertices, Vector3[] v, int rows, int cols, int stride, Func<int, int, Material> materialOf)
        {
            if (v == null || rows <= 0 || cols <= 0 || parent == null)
            {
                return;
            }

            // The strip's own colliders and its neighbours', just built, must be where the rays look.
            Physics.SyncTransforms();
            Dictionary<Material, SkirtMesh> meshes = null;

            // The outer row: quad (rows - 1, j) under edge (rows, j) - (rows, j + 1).
            for (int j = 0; j < cols; j++)
            {
                SkirtEdge(ref meshes, parent, worldVertices, v[rows * stride + j], v[rows * stride + j + 1],
                    v[rows * stride + j] + v[rows * stride + j + 1] - v[(rows - 1) * stride + j] - v[(rows - 1) * stride + j + 1], materialOf(rows - 1, j));
            }

            // Both end columns: quad (i, 0) and quad (i, cols - 1).
            for (int i = 0; i < rows; i++)
            {
                SkirtEdge(ref meshes, parent, worldVertices, v[i * stride], v[(i + 1) * stride],
                    v[i * stride] + v[(i + 1) * stride] - v[i * stride + 1] - v[(i + 1) * stride + 1], materialOf(i, 0));
                SkirtEdge(ref meshes, parent, worldVertices, v[i * stride + cols], v[(i + 1) * stride + cols],
                    v[i * stride + cols] + v[(i + 1) * stride + cols] - v[i * stride + cols - 1] - v[(i + 1) * stride + cols - 1], materialOf(i, cols - 1));
            }

            if (meshes == null)
            {
                return;
            }

            foreach (KeyValuePair<Material, SkirtMesh> entry in meshes)
            {
                SkirtMesh s = entry.Value;
                var mesh = new Mesh { name = "terrain skirt" };

                if (s.Positions.Count > 65000)
                {
                    mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                }

                mesh.SetVertices(s.Positions);
                mesh.SetNormals(s.Normals);
                mesh.SetUVs(0, s.Uvs);
                mesh.SetTriangles(s.Triangles, 0);
                mesh.RecalculateBounds();

                var piece = new GameObject("terrain skirt");
                piece.transform.SetParent(parent, worldVertices);
                piece.AddComponent<MeshFilter>().sharedMesh = mesh;
                var renderer = piece.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = entry.Key;
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }
        }

        private static void SkirtEdge(ref Dictionary<Material, SkirtMesh> meshes, Transform parent, bool worldVertices, Vector3 a, Vector3 b, Vector3 outward, Material material)
        {
            if (material == null)
            {
                return;
            }

            // Everything below in world space: the strip's vertices are the parent's local ones unless worldVertices.
            Vector3 wa = worldVertices ? a : parent.TransformPoint(a);
            Vector3 wb = worldVertices ? b : parent.TransformPoint(b);
            Vector3 wo = worldVertices ? outward : parent.TransformDirection(outward);
            wo.y = 0f;
            Vector3 along = wb - wa;
            along.y = 0f;

            if (wo.sqrMagnitude < 1e-6f || along.sqrMagnitude < 1e-4f)
            {
                return;
            }

            wo.Normalize();
            float top = Mathf.Min(wa.y, wb.y);
            float bottom = float.MaxValue;
            bool needed = false;

            // 2026-10-27 (floating ground): three rays along the edge, not one at its middle. Where the middle found nothing
            // below, the wall used to hang SkirtDepth (20 m) into the air: the khaki panels standing over the forested slopes
            // and beside the A9/S6 in the user's screenshots - now no wall. Where an end stood over a road, a yard, a tunnel
            // portal or a model, the wall reached across it - now no wall either. Otherwise the wall hangs to the lowest
            // terrain the rays found (an end over nothing takes the middle's).
            bool middle = false;

            for (int ray = 0; ray < 3; ray++)
            {
                Vector3 probe = Vector3.Lerp(wa, wb, ray == 0 ? 0.1f : ray == 1 ? 0.5f : 0.9f) + wo * 0.35f;
                probe.y = Mathf.Max(wa.y, wb.y) + 0.3f;
                int count = Physics.RaycastNonAlloc(probe, Vector3.down, SkirtHits, SkirtDepth + 1f, EtsLayers.GroundMask, QueryTriggerInteraction.Ignore);
                float nearest = float.MaxValue;
                RaycastHit below = default(RaycastHit);

                for (int k = 0; k < count; k++)
                {
                    if (EtsGroundFill.IsFill(SkirtHits[k].collider) || SkirtHits[k].distance >= nearest)
                    {
                        continue;
                    }

                    nearest = SkirtHits[k].distance;
                    below = SkirtHits[k];
                }

                if (nearest == float.MaxValue)
                {
                    continue;
                }

                string name = below.collider.gameObject.name;

                // A road, a yard, a portal or a model below: no wall onto it.
                if (!(name.StartsWith("terrain", StringComparison.Ordinal) || name.StartsWith("prefab terrain", StringComparison.Ordinal)))
                {
                    SkirtsNotNeeded++;
                    return;
                }

                middle |= ray == 1;
                needed |= top - below.point.y >= 0.4f;
                bottom = Mathf.Min(bottom, below.point.y - 0.6f);
            }

            if (!middle)
            {
                // Nothing within SkirtDepth under the middle of the edge: a wall here would hang in the air.
                SkirtsInAir++;
                return;
            }

            // The ground outside meets the edge all along: nothing to close.
            if (!needed)
            {
                SkirtsNotNeeded++;
                return;
            }

            // 2026-10-30 (floating forest bands): the ground the rays found lies deeper than a wall may hang - a hillside item
            // 22-50 m over the A9 north of Graz found the motorway's verge below (lab probe at Unity (-1306, 2986): terrain
            // profile114 at 37-65 m, the road's terrain at 13-15 m). Cut at SkirtDepth, the wall stopped in mid-air: the
            // 20 m curtains in the forest-far brush, straight bottoms with the sky under them, that read as floating bands of
            // trees on the hills. A wall that cannot reach the ground is not built.
            if (bottom < top - SkirtDepth)
            {
                SkirtsInAir++;
                return;
            }

            meshes = meshes ?? new Dictionary<Material, SkirtMesh>();

            if (!meshes.TryGetValue(material, out SkirtMesh s))
            {
                s = new SkirtMesh();
                meshes[material] = s;
            }

            // Both bottom corners at one height (the ground below, or SkirtDepth under the lower end); in the strip's own space from here.
            Vector3 wc = new Vector3(wa.x, bottom, wa.z), wd = new Vector3(wb.x, bottom, wb.z);
            Vector3 c = worldVertices ? wc : parent.InverseTransformPoint(wc), d = worldVertices ? wd : parent.InverseTransformPoint(wd);
            Vector3 o = worldVertices ? wo : parent.InverseTransformDirection(wo);
            int i0 = s.Positions.Count;
            s.Positions.Add(a);
            s.Positions.Add(b);
            s.Positions.Add(c);
            s.Positions.Add(d);

            // Lit much like the ground beside it rather than as a dark cliff.
            Vector3 n = (o + Vector3.up * 1.5f).normalized;
            Vector3 dir = along.normalized;

            for (int k = 0; k < 4; k++)
            {
                s.Normals.Add(n);
                Vector3 p = worldVertices ? s.Positions[i0 + k] : parent.TransformPoint(s.Positions[i0 + k]);
                s.Uvs.Add(new Vector2(Vector3.Dot(p, dir) / FallbackTile, p.y / FallbackTile));
            }

            // Facing out of the strip: the front of (p, q, r) is Cross(q - p, r - p), as for the ground quads.
            if (Vector3.Dot(Vector3.Cross(b - a, c - a), o) > 0f)
            {
                s.Triangles.Add(i0); s.Triangles.Add(i0 + 1); s.Triangles.Add(i0 + 2);
                s.Triangles.Add(i0 + 1); s.Triangles.Add(i0 + 3); s.Triangles.Add(i0 + 2);
            }
            else
            {
                s.Triangles.Add(i0); s.Triangles.Add(i0 + 2); s.Triangles.Add(i0 + 1);
                s.Triangles.Add(i0 + 1); s.Triangles.Add(i0 + 2); s.Triangles.Add(i0 + 3);
            }

            SkirtSegments++;
        }

        private static Mesh TerrainMesh(string name, Vector3[] vertices, Vector2[] uv, Vector2[] uv2, Color32[] colours, int[] triangles)
        {
            var mesh = new Mesh { name = name };

            if (vertices.Length > 65535)
            {
                mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            }

            mesh.vertices = vertices;
            mesh.uv = uv;

            if (uv2 != null)
            {
                mesh.uv2 = uv2;
            }

            if (colours != null)
            {
                mesh.colors32 = colours;
            }

            mesh.triangles = triangles;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        private static MeshRenderer Piece(Transform parent, string name, Mesh mesh, Material material, bool collider)
        {
            var piece = new GameObject(name);
            piece.transform.SetParent(parent, false);
            piece.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = piece.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            if (collider)
            {
                Mesh solid = CollisionMesh(mesh);

                if (solid != null)
                {
                    piece.AddComponent<MeshCollider>().sharedMesh = solid;
                }
            }

            return renderer;
        }

        /// <summary>How many ground meshes had degenerate triangles taken out before cooking, and how many were left with none.</summary>
        public static int CollidersCleaned, CollidersEmpty;

        /// <summary>Quads painted with the "inv" brush, built as collision without a surface.</summary>
        public static int InvisibleQuads;

        /// <summary>Quads whose two triangles face opposite ways (folded by vertex records), each now turned up on its own.</summary>
        public static int FoldedQuads;

        // ------------------------------------------------------------------ invisible ground off the roads (2026-10-27)

        /// <summary>An "inv" brush piece (terrain strip or prefab corner) whose collider waits for the map's road footprints.</summary>
        private sealed class InvisiblePiece
        {
            public EtsMap Map;
            public GameObject Piece;
            public Mesh Mesh;
            public Vector3 CentreGame;
            public float Since;
        }

        private static readonly List<InvisiblePiece> PendingInvisible = new List<InvisiblePiece>();
        private static float _invisiblePumpAt;
        private static bool _invisibleLogged;

        /// <summary>Invisible ground triangles left without collision because they lie on or over a road or junction surface, and pieces cooked.</summary>
        public static int InvisibleTrianglesOffRoad, InvisiblePiecesCooked;

        /// <summary>A road or junction surface this far or less under an invisible ground triangle (or up to InvisibleUnder over it) takes its collision away.</summary>
        public const float InvisibleOverRoad = 5f, InvisibleUnder = 0.25f;

        /// <summary>
        /// "inv" quads are /material/terrain/invis.mat (eut2.none, substance road): ground the truck drives on but nobody sees -
        /// under the dirt decals round the bay they are the only ground there is, so they keep their collision. But the mappers
        /// also paint inv wherever a strip pokes through a road or a viaduct deck, to hide it: render-lab survey 2026-10-27, 4
        /// squares of 1.4 km round Knoten St. Michael and the A9 viaduct by Graz - 218 cells of 16 m2 over roads with an
        /// invisible terrain surface 0.3-4.75 m above the asphalt (terrain items 'profile112 right invisible' 0.3-3.6 m over
        /// the A9's lanes, ger11), i.e. invisible kerbs and walls on the motorway. Those triangles get no collision: a triangle
        /// whose middle stands within InvisibleOverRoad over (or InvisibleUnder under) a road or junction surface of the map's
        /// own footprints (EtsRoadClearance: computed from the map data, so it does not matter which streams in first). The
        /// collider is cooked once every road that can reach the piece is known; until then the piece has none.
        /// </summary>
        private static void DeferInvisible(EtsMap map, GameObject piece, Mesh mesh, Vector3 centreGame)
        {
            if (map == null || EtsVegetationBuilder.Clearance(map) == null)
            {
                Mesh solid = CollisionMesh(mesh);

                if (solid != null)
                {
                    piece.AddComponent<MeshCollider>().sharedMesh = solid;
                }

                return;
            }

            PendingInvisible.Add(new InvisiblePiece { Map = map, Piece = piece, Mesh = mesh, CentreGame = centreGame, Since = Time.unscaledTime });
        }

        /// <summary>A piece's own space to the map's (the space of its "Ets Map" ancestor, or the world).</summary>
        private static Matrix4x4 MapFrame(Transform piece)
        {
            Transform root = piece;

            while (root != null && root.name != "Ets Map")
            {
                root = root.parent;
            }

            return root != null ? root.worldToLocalMatrix * piece.localToWorldMatrix : piece.localToWorldMatrix;
        }

        /// <summary>The same for a prefab corner's inv piece (EtsPrefabTerrainBuilder: world vertices).</summary>
        public static void DeferInvisiblePrefab(EtsMap map, GameObject piece, Mesh mesh, Vector3 centreGame)
        {
            DeferInvisible(map, piece, mesh, centreGame);
        }

        /// <summary>Terrain-item vertices lowered out of a lane's clearance (ClampOverRoads), sides done, sides built before their footprints were known.</summary>
        public static int ClampedVertices, ClampedSides, ClampSidesUnready, ClampedQuads;
        private static bool _clampSaid;

        /// <summary>A terrain-item vertex this far or more over a road or junction surface (and less than ClampTop) is in its lanes.</summary>
        public const float ClampFrom = 0.3f;
        public const float ClampTop = 6f;

        /// <summary>A quad's middle or edge middle at least this far over a road surface is over the lanes (a verge rising gently from the
        /// road edge is not: lowered at 0.3 m, those quads dipped under the slab edge and opened a seam, lab 2026-10-30).</summary>
        public const float ClampQuadFrom = 1f;

        /// <summary>
        /// A terrain item's vertices that stand 0.3-6 m over a road or junction surface (the map's footprints, EtsRoadClearance)
        /// go to 0.3 m under it. The map's own data lifts some sides over lanes where their profile climbs and the mapper did
        /// not hand-lower the vertices: terrain item 42bf29e0c38f62d3 (profile29uk, coefficient 0.8: +8 m at 11 m out) stands
        /// 1-4.8 m over the A9 exit slip road at game (17628-17660, 19418-19422) - 87 of its vertices are hand-placed under the
        /// interchange, the 2-3 columns over the slip road are not (probe DevTools/work/terrain1029/probe/m3; lab ground survey
        /// 2026-10-29 "terrain over open road"). Nothing that low over a lane is ground a truck could pass; vertices at road
        /// level stay (the road edges keep meeting their terrain), anything higher stays (a mountain over a gallery). Only when
        /// the footprints round the side are known already - else the side is left as the map has it (counted).
        /// </summary>
        private static void ClampOverRoads(EtsMap map, Vector3[] vertices, int rows, int cols, int stride, Vector3 centreGame)
        {
            EtsRoadClearance.Registry clearance = EtsVegetationBuilder.Clearance(map);

            if (clearance == null || vertices.Length == 0)
            {
                return;
            }

            float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;

            foreach (Vector3 v in vertices)
            {
                Vector3 u = EtsVegetationBuilder.ToClearance(v, centreGame);
                minX = Mathf.Min(minX, u.x); maxX = Mathf.Max(maxX, u.x);
                minZ = Mathf.Min(minZ, u.z); maxZ = Mathf.Max(maxZ, u.z);
            }

            if (!clearance.Ensure(minX, minZ, maxX, maxZ))
            {
                ClampSidesUnready++;
                return;
            }

            int moved = 0;

            for (int i = 0; i < vertices.Length; i++)
            {
                Vector3 u = EtsVegetationBuilder.ToClearance(vertices[i], centreGame);

                if (!clearance.Blocked(u.x, u.y, u.z, 0f, out EtsRoadClearance.Hit hit))
                {
                    continue;
                }

                float rise = u.y - hit.SurfaceY;

                if (rise >= ClampFrom && rise < ClampTop)
                {
                    vertices[i].y -= rise + ClampFrom;
                    moved++;
                }
            }

            // A quad can lie over a lane with all four corners off it (the corners either side of a slip road): its middle and
            // its edges' middles are tested too, and when one stands 1-6 m over a road surface the quad's corners go down to
            // 0.3 m under that surface (2026-10-30: the 7 cells over the A9 exit the vertex test left, lab survey g8).
            int quadsLowered = 0;

            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < cols; c++)
                {
                    int a = r * stride + c, b = a + 1, d = a + stride, e = d + 1;

                    if (e >= vertices.Length)
                    {
                        continue;
                    }

                    float target = float.MaxValue;

                    for (int s = 0; s < 5; s++)
                    {
                        Vector3 p = s == 0 ? (vertices[a] + vertices[b] + vertices[d] + vertices[e]) * 0.25f
                            : s == 1 ? (vertices[a] + vertices[b]) * 0.5f
                            : s == 2 ? (vertices[d] + vertices[e]) * 0.5f
                            : s == 3 ? (vertices[a] + vertices[d]) * 0.5f
                            : (vertices[b] + vertices[e]) * 0.5f;
                        Vector3 u = EtsVegetationBuilder.ToClearance(p, centreGame);

                        if (!clearance.Blocked(u.x, u.y, u.z, 0f, out EtsRoadClearance.Hit hit))
                        {
                            continue;
                        }

                        float rise = u.y - hit.SurfaceY;

                        if (rise >= ClampQuadFrom && rise < ClampTop)
                        {
                            target = Mathf.Min(target, hit.SurfaceY - ClampFrom - centreGame.y);
                        }
                    }

                    if (target == float.MaxValue)
                    {
                        continue;
                    }

                    quadsLowered++;
                    if (vertices[a].y > target) { vertices[a].y = target; moved++; }
                    if (vertices[b].y > target) { vertices[b].y = target; moved++; }
                    if (vertices[d].y > target) { vertices[d].y = target; moved++; }
                    if (vertices[e].y > target) { vertices[e].y = target; moved++; }
                }
            }

            ClampedQuads += quadsLowered;
            ClampedSides++;
            ClampedVertices += moved;

            if (moved > 0 && !_clampSaid)
            {
                _clampSaid = true;
                Debug.Log("[SomniumEuroTruck] Terrain: terrain-item ground kept out of the lanes - " + moved + " vertices of a side stood 0.3-6 m over a road or junction surface and now lie 0.3 m under it (the map lifts some sides over slip roads, 2026-10-29).");
            }
        }

        /// <summary>A point of an invisible piece (its mesh frame) standing 0.3 m to InvisibleOverRoad over a road or junction surface.</summary>
        private static bool OverLane(EtsRoadClearance.Registry clearance, Matrix4x4 toMap, Vector3 centreGame, Vector3 local)
        {
            Vector3 u = EtsVegetationBuilder.ToClearance(toMap.MultiplyPoint3x4(local), centreGame);

            if (!clearance.Blocked(u.x, u.y, u.z, 0f, out EtsRoadClearance.Hit hit))
            {
                return false;
            }

            float over = u.y - hit.SurfaceY;
            return over >= ClampFrom && over < InvisibleOverRoad;
        }

        /// <summary>
        /// Main thread, a few times a second (EtsGroundFill's loop): cooks the colliders of the waiting invisible pieces whose
        /// footprints are known, within about <paramref name="budgetMs"/>.
        /// </summary>
        public static void PumpInvisible(float budgetMs)
        {
            if (PendingInvisible.Count == 0 || Time.unscaledTime - _invisiblePumpAt < 0.25f)
            {
                return;
            }

            _invisiblePumpAt = Time.unscaledTime;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            EtsRoadClearance.Registry clearance = null;

            for (int i = PendingInvisible.Count - 1; i >= 0 && clock.Elapsed.TotalMilliseconds < budgetMs; i--)
            {
                InvisiblePiece p = PendingInvisible[i];

                if (p.Piece == null || p.Mesh == null)
                {
                    PendingInvisible.RemoveAt(i);
                    continue;
                }

                if (clearance == null)
                {
                    clearance = EtsVegetationBuilder.Clearance(p.Map);

                    if (clearance == null)
                    {
                        return;
                    }

                    clearance.Tick();
                }

                // Into the map frame the footprints use (the "Ets Map" root's own space; it stands at the world origin, but say so).
                Matrix4x4 toMap = MapFrame(p.Piece.transform);
                Bounds b = p.Mesh.bounds;
                Vector3 e0 = toMap.MultiplyPoint3x4(b.min), e1 = toMap.MultiplyPoint3x4(b.max);
                Vector3 lo = EtsVegetationBuilder.ToClearance(Vector3.Min(e0, e1) - Vector3.one * 2f, p.CentreGame);
                Vector3 hi = EtsVegetationBuilder.ToClearance(Vector3.Max(e0, e1) + Vector3.one * 2f, p.CentreGame);

                if (!clearance.Ensure(Mathf.Min(lo.x, hi.x), Mathf.Min(lo.z, hi.z), Mathf.Max(lo.x, hi.x), Mathf.Max(lo.z, hi.z)))
                {
                    continue;
                }

                PendingInvisible.RemoveAt(i);
                Vector3[] v = p.Mesh.vertices;
                int[] tri = p.Mesh.triangles;
                var kept = new List<int>(tri.Length);

                for (int k = 0; k + 2 < tri.Length; k += 3)
                {
                    Vector3 c = toMap.MultiplyPoint3x4((v[tri[k]] + v[tri[k + 1]] + v[tri[k + 2]]) / 3f);
                    Vector3 u = EtsVegetationBuilder.ToClearance(c, p.CentreGame);

                    if (clearance.Blocked(u.x, u.y, u.z, 0f, out EtsRoadClearance.Hit hit))
                    {
                        float rise = hit.SurfaceY - u.y;

                        if (rise > -InvisibleOverRoad && rise < InvisibleUnder)
                        {
                            InvisibleTrianglesOffRoad++;
                            continue;
                        }
                    }

                    // A triangle straddling a lane edge has its centre off the lane: its edges' middles are tested too, but
                    // only for standing 0.3-5 m OVER the surface (a wall in the lane), so invisible ground at road level
                    // beside a road keeps its collision (2026-10-30: 4 cells over the A9 exit, lab survey g8).
                    if (OverLane(clearance, toMap, p.CentreGame, (v[tri[k]] + v[tri[k + 1]]) * 0.5f) ||
                        OverLane(clearance, toMap, p.CentreGame, (v[tri[k + 1]] + v[tri[k + 2]]) * 0.5f) ||
                        OverLane(clearance, toMap, p.CentreGame, (v[tri[k + 2]] + v[tri[k]]) * 0.5f))
                    {
                        InvisibleTrianglesOffRoad++;
                        continue;
                    }

                    kept.Add(tri[k]);
                    kept.Add(tri[k + 1]);
                    kept.Add(tri[k + 2]);
                }

                if (kept.Count == 0)
                {
                    continue;
                }

                Mesh source = p.Mesh;

                if (kept.Count < tri.Length)
                {
                    source = new Mesh { name = p.Mesh.name + " off the roads", indexFormat = p.Mesh.indexFormat };
                    source.vertices = v;
                    source.triangles = kept.ToArray();
                    source.RecalculateBounds();
                }

                Mesh solid = CollisionMesh(source);

                if (solid != null)
                {
                    p.Piece.AddComponent<MeshCollider>().sharedMesh = solid;
                    InvisiblePiecesCooked++;
                }

                if (source != p.Mesh && source != solid)
                {
                    UnityEngine.Object.Destroy(source);
                }
            }

            if (!_invisibleLogged && InvisiblePiecesCooked > 0)
            {
                _invisibleLogged = true;
                Debug.Log("[SomniumEuroTruck] Terrain: invisible ground ('inv' brush) gets its collision once the map's road footprints are known; " +
                          InvisibleTrianglesOffRoad + " of its triangles so far lie on or within " + InvisibleOverRoad.ToString("0") +
                          " m over a road or junction surface and stay without (invisible kerbs and walls on the lanes, 2026-10-27).");
            }
        }

        /// <summary>
        /// A mesh PhysX will accept. A triangle with two corners at the same point, or an area of
        /// a few square microns, makes cooking fail outright - Unity logs "cleaning the mesh
        /// failed" and the piece ends up with NO collider, which the ground census then reads as a
        /// hole and the truck falls through. The mapper's vertex-tool records routinely put two
        /// neighbouring grid vertices on the same spot (that is how a strip is tapered to a
        /// point), so this is ordinary map data, not corruption: the degenerate triangles are
        /// dropped from the collision copy only, and the drawn mesh keeps every one of them.
        /// </summary>
        public static Mesh CollisionMesh(Mesh mesh)
        {
            Vector3[] vertices = mesh.vertices;
            int[] triangles = mesh.triangles;
            var kept = new List<int>(triangles.Length);

            for (int t = 0; t + 2 < triangles.Length; t += 3)
            {
                int a = triangles[t], b = triangles[t + 1], c = triangles[t + 2];

                if (a == b || b == c || a == c)
                {
                    continue;
                }

                Vector3 va = vertices[a], vb = vertices[b], vc = vertices[c];

                if (!Finite(va) || !Finite(vb) || !Finite(vc) || Vector3.Cross(vb - va, vc - va).sqrMagnitude < 1e-10f)
                {
                    continue;
                }

                kept.Add(a);
                kept.Add(b);
                kept.Add(c);
            }

            if (kept.Count == triangles.Length)
            {
                return mesh;
            }

            if (kept.Count == 0)
            {
                CollidersEmpty++;
                return null;
            }

            CollidersCleaned++;
            var solid = new Mesh { name = mesh.name + " collision", indexFormat = mesh.indexFormat };
            solid.vertices = vertices;
            solid.triangles = kept.ToArray();
            solid.RecalculateBounds();
            return solid;
        }

        private static bool Finite(Vector3 v)
        {
            return !float.IsNaN(v.x) && !float.IsNaN(v.y) && !float.IsNaN(v.z) &&
                   !float.IsInfinity(v.x) && !float.IsInfinity(v.y) && !float.IsInfinity(v.z);
        }

        private static readonly Dictionary<string, Material> PairByKey = new Dictionary<string, Material>(StringComparer.Ordinal);

        /// <summary>
        /// The two-layer material for a pair of brushes: the main brush's texture on the first
        /// uv set, the blend brush's on the second, faded between by the cover in the vertex
        /// alpha - the two-layer shader's fourth mode. Cached per pair and tint. Null without the
        /// shader or a texture, in which case the caller falls back to one brush.
        /// </summary>
        private static Material PairMaterial(EtsMap map, ulong mainToken, ulong blendToken, Color32 tint)
        {
            Shader shader = EtsMaterialCache.MultiShader != null ? EtsMaterialCache.MultiShader : Shader.Find("SomniumEuroTruck/Multi");

            if (shader == null)
            {
                if (_pairsReported++ == 0)
                {
                    Debug.LogWarning("[SomniumEuroTruck] terrain: no two-layer shader, so blended brushes fall back to one brush per quad.");
                }

                return null;
            }

            string key = mainToken + ":" + blendToken + ":" + (tint.r >> 3) + ":" + (tint.g >> 3) + ":" + (tint.b >> 3);

            if (PairByKey.TryGetValue(key, out Material cached))
            {
                return cached;
            }

            Texture2D baseMap = BrushTexture(map, mainToken);
            Texture2D over = BrushTexture(map, blendToken);

            if (baseMap == null || over == null)
            {
                if (_pairsReported++ < 3)
                {
                    Debug.LogWarning("[SomniumEuroTruck] terrain pair " + EtsToken.Decode(mainToken) + " + " + EtsToken.Decode(blendToken) + ": " + (baseMap == null ? "main" : "blend") + " texture missing; one brush used.");
                }

                PairByKey[key] = null;
                return null;
            }

            if (_pairsReported++ < 3)
            {
                Debug.Log("[SomniumEuroTruck] terrain pair " + EtsToken.Decode(mainToken) + " + " + EtsToken.Decode(blendToken) + ": " + baseMap.name + " under " + over.name + ", tint " + tint.r + "," + tint.g + "," + tint.b + ".");
            }

            var material = new Material(shader) { name = "terrain " + EtsToken.Decode(mainToken) + " + " + EtsToken.Decode(blendToken) };
            material.SetTexture("_BaseMap", baseMap);
            material.SetTexture("_MultMap", over);
            material.SetFloat("_LayerMode", 3f);
            material.SetFloat("_MultStrength", 1f);
            material.SetFloat("_Metallic", 0f);
            material.SetFloat("_Smoothness", 0.1f);
            material.SetColor(BaseColorId, new Color(tint.r / 255f, tint.g / 255f, tint.b / 255f, 1f));

            // The game's terrain effect (eut2.dif.spec.weight.tg0, /material/terrain/grass_ger_main.mat) has no
            // environment term: without this the pair reflected the truck's realtime probe or the sky at grazing
            // angles (about 6.6 % of the sky at 30 m; lighting 2026-09-25), as EtsMaterialCache already stops for
            // every other material without one.
            material.SetFloat("_EnvironmentReflections", 0f);
            material.EnableKeyword("_ENVIRONMENTREFLECTIONS_OFF");
            PairByKey[key] = material;
            return material;
        }

        /// <summary>The colour texture of a terrain brush, or null.</summary>
        private static Texture2D BrushTexture(EtsMap map, ulong token)
        {
            EtsSiiUnit unit = token != 0 ? map.TerrainMaterial(token) : null;
            string path = unit != null ? unit.GetString("path", null) : null;

            if (string.IsNullOrEmpty(path) || EtsEngine.Materials == null || EtsEngine.Textures == null)
            {
                return null;
            }

            EtsMaterialDef def = EtsEngine.Materials.GetDefinition(path);
            return def != null && !string.IsNullOrEmpty(def.BaseTexture) ? EtsEngine.Textures.Load(def.BaseTexture, false) : null;
        }

        /// <summary>
        /// How far the blend brush covers each vertex: the cover of every quad in the group that
        /// touches it, averaged, in the alpha. The map paints cover per quad; averaging it onto the
        /// shared corners is what makes one brush fade into the next instead of stepping.
        /// </summary>
        private static Color32[] BlendCover(int vertexCount, int stride, List<int> quadIds, EtsTerrainQuadData quads)
        {
            var sum = new float[vertexCount];
            var count = new int[vertexCount];

            foreach (int id in quadIds)
            {
                int i = id / 1024, j = id % 1024;
                float cover = quads != null && i < quads.Rows && j < quads.Cols ? quads.BlendOpacity(i, j) / 15f : 0f;
                int a = i * stride + j, c = (i + 1) * stride + j;

                // The quad's four corners, unrolled (it made an int[4] per quad: garbage on every blended strip).
                AddCover(sum, count, a, cover);
                AddCover(sum, count, a + 1, cover);
                AddCover(sum, count, c, cover);
                AddCover(sum, count, c + 1, cover);
            }

            var colours = new Color32[vertexCount];

            for (int v = 0; v < vertexCount; v++)
            {
                byte alpha = count[v] > 0 ? (byte)Mathf.RoundToInt(255f * sum[v] / count[v]) : (byte)0;
                colours[v] = new Color32(255, 255, 255, alpha);
            }

            return colours;
        }

        private static void AddCover(float[] sum, int[] count, int v, float cover)
        {
            if (v >= 0 && v < sum.Length)
            {
                sum[v] += cover;
                count[v]++;
            }
        }

        /// <summary>Whether the quads of a pair group are, on average, more than half covered by their blend brush.</summary>
        private static bool MostlyBlend(EtsTerrainQuadData quads, List<int> quadIds)
        {
            if (quads == null || quadIds.Count == 0)
            {
                return false;
            }

            float total = 0f;

            foreach (int id in quadIds)
            {
                int i = id / 1024, j = id % 1024;
                total += i < quads.Rows && j < quads.Cols ? quads.BlendOpacity(i, j) : 0;
            }

            return total / quadIds.Count >= 8f;
        }

        /// <summary>The colour the sector stores beside a brush slot; white when it stores none.</summary>
        private static Color32 BrushTint(EtsTerrainQuadData quads, int slot)
        {
            int at = slot * 4;

            if (quads == null || at + 2 >= quads.Colors.Length)
            {
                return new Color32(255, 255, 255, 255);
            }

            return new Color32(quads.Colors[at], quads.Colors[at + 1], quads.Colors[at + 2], 255);
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
