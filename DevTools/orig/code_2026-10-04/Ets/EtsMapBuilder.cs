using System;
using System.Collections.Generic;
using UnityEngine;

namespace SomniumSpace.Worlds.SomniumEuroTruck.Ets
{
    /// <summary>
    /// Turns map items into Unity geometry: roads bent along their splines, prefabs stood on
    /// their nodes, models placed where the map puts them. Positions come out in Unity's frame,
    /// shifted so that a chosen game position sits at the world origin - the map is sixty
    /// kilometres across and Unity's floats are happiest near zero.
    ///
    /// Roads are the interesting part. A road is one cubic curve between two nodes: the node
    /// positions are its ends and the node forwards its tangents, scaled by the stored length.
    /// Most looks are "template" roads whose surface is a small model - a slice of road a few
    /// metres long with lane markings - that the game repeats along the curve. That is done
    /// here too: every vertex of the slice is carried to its distance along the curve and offset
    /// sideways and upward in the curve's frame, so the markings follow the bends. The older
    /// procedural looks, which have no model, get a flat ribbon of their stated width.
    /// </summary>
    public static class EtsMapBuilder
    {
        public sealed class Options
        {
            /// <summary>Game-frame position placed at Unity's origin.</summary>
            public Vector3 CenterGame;

            public bool Roads = true;
            public bool Prefabs = true;
            public bool Models = true;
            public bool Colliders = true;
            public int MaxModels = 4000;

            /// <summary>Build the ground beside roads and the terrain items, from the sector's .data file.</summary>
            public bool Terrain = true;

            /// <summary>Scatter the vegetation bands of road sides and terrain items on that ground.</summary>
            public bool Vegetation = true;

            /// <summary>Metres between spline samples for roads.</summary>
            public float RoadStep = 3f;

            /// <summary>Default material when a road has no template model.</summary>
            public string RibbonMaterial = "/material/road/road_template/ger/ger_01.mat";

            /// <summary>
            /// The small details a prefab carries: its traffic lights (EtsSemaphores) and the lamps and signs its
            /// descriptor lists (EtsSignBuilder), built a few a frame after the prefab itself.
            /// </summary>
            public bool Details = true;
        }

        public sealed class Stats
        {
            public int Roads, RoadsSkipped, RoadsRibbon, Prefabs, PrefabsSkipped, Models, ModelsSkipped, Vertices;
            public int TerrainSides, TerrainQuads, TerrainItems, TerrainSkipped, Plants;
            public readonly List<string> Notes = new List<string>();

            public override string ToString()
            {
                return "roads " + Roads + " (" + RoadsRibbon + " ribbons, " + RoadsSkipped + " skipped), prefabs " + Prefabs +
                       " (" + PrefabsSkipped + " skipped), models " + Models + " (" + ModelsSkipped + " skipped), terrain " + TerrainSides +
                       " strips / " + TerrainQuads + " quads (" + TerrainItems + " items, " + TerrainSkipped + " skipped), " + Plants + " plants, " + Vertices + " vertices";
            }
        }

        /// <summary>A game-frame position into Unity, relative to the chosen centre.</summary>
        public static Vector3 ToWorld(float[] gamePosition, Vector3 centerGame)
        {
            return EtsModelBuilder.ToUnity(gamePosition[0] - centerGame.x, gamePosition[1] - centerGame.y, gamePosition[2] - centerGame.z);
        }

        public static Vector3 ToWorldDirection(float[] gameDirection)
        {
            return EtsModelBuilder.ToUnity(gameDirection[0], gameDirection[1], gameDirection[2]).normalized;
        }

        // ------------------------------------------------------------------ roads

        /// <summary>
        /// Builds one road. Null when either node is unknown - a road whose far end is in a
        /// sector that was not loaded.
        /// </summary>
        public static GameObject BuildRoad(EtsMap map, EtsMapRoad road, Options options, Transform parent, Stats stats)
        {
            EtsMapNode start = map.FindNode(road.StartNode);
            EtsMapNode end = map.FindNode(road.EndNode);

            if (start == null || end == null)
            {
                stats.RoadsSkipped++;
                return null;
            }

            Vector3 p0 = ToWorld(start.Position, options.CenterGame);
            Vector3 p1 = ToWorld(end.Position, options.CenterGame);
            Vector3 t0 = ToWorldDirection(start.Forward()) * road.Length;
            Vector3 t1 = ToWorldDirection(end.Forward()) * road.Length;

            EtsSiiUnit look = map.RoadLook(road.LookToken);
            var roadObject = new GameObject("road " + road.Look);
            roadObject.transform.SetParent(parent, false);

            bool built = false;
            float halfRight = 4.5f, halfLeft = 4.5f;
            float terrainLift = 0f;

            if (look != null && look.Has("template_right"))
            {
                // The road's material token names the template's look (ger_1 .. ger_5 on the
                // German template), and its variant token the centre-line variant.
                string lookName = EtsToken.Decode(road.MaterialToken);
                string[] variants = { EtsToken.Decode(road.VariantToken), "default" };

                EtsModelData template = EtsEngine.LoadModel(look.GetString("template_right"), out string failure);

                // Divided roads. A look with a road_offset is two carriageways: the right template
                // stands offset metres right of the centre line and the left one (template_left, or
                // the right one again when the look names none) the same distance left of it,
                // MIRRORED. "ger r2 tram" is one 9.5 m slab modelled on x 0..9.5 with road_offset
                // 3.25; built once at zero it was a right carriageway lying across the centre and no
                // left carriageway at all - a hole with the sky in it beside the DomDepo bay, where
                // the map paints the terrain invisible because the road is meant to cover it.
                float offset = look.GetFloat("road_offset", 0f);
                bool divided = offset > 0.01f || look.Has("template_left");

                if (template != null)
                {
                    built = BuildTemplateAlong(template, lookName, variants, offset, false, road.Length, p0, t0, p1, t1, options, roadObject.transform, stats);

                    // Shoulders: one-metre models hung on each edge of the slab, built for the
                    // right side and mirrored for the left. The edge tokens name them.
                    float halfWidth = divided ? offset + template.BoundsMax[0] : Mathf.Max(-template.BoundsMin[0], template.BoundsMax[0]);
                    float slabSlice = template.BoundsMax[2] - template.BoundsMin[2];
                    halfRight = halfLeft = halfWidth;
                    BuildEdge(map, road.RightEdgeToken, lookName, slabSlice, halfWidth, false, road, p0, t0, p1, t1, options, roadObject.transform, stats);

                    if (divided)
                    {
                        EtsModelData left = look.Has("template_left") ? EtsEngine.LoadModel(look.GetString("template_left"), out failure) : template;

                        if (left != null)
                        {
                            BuildTemplateAlong(left, lookName, variants, -offset, true, road.Length, p0, t0, p1, t1, options, roadObject.transform, stats);
                            halfLeft = offset + left.BoundsMax[0];
                        }

                        if (offset > 0.01f)
                        {
                            BuildMedian(map, road, p0, t0, p1, t1, offset, road.Length, options, roadObject.transform, stats);

                            // The inner edges, facing the median: the right carriageway's left
                            // edge turned round at the right slab's inner side, and the left
                            // carriageway's right edge as modelled at the left slab's inner side.
                            // On the tram road both are the tram track, which is how the game
                            // puts the tram in the middle of the avenue.
                            BuildEdge(map, road.RightInnerEdgeToken, lookName, slabSlice, offset, true, road, p0, t0, p1, t1, options, roadObject.transform, stats);
                            BuildEdge(map, road.LeftInnerEdgeToken, lookName, slabSlice, -offset, false, road, p0, t0, p1, t1, options, roadObject.transform, stats);
                        }
                    }

                    // A single-carriageway road is all "right carriageway", so its left shoulder is
                    // that carriageway's left edge; a divided road's outer left is the left
                    // carriageway's own left edge.
                    ulong outerLeft = divided && road.LeftOuterEdgeToken != 0 ? road.LeftOuterEdgeToken : road.LeftEdgeToken;
                    BuildEdge(map, outerLeft, lookName, slabSlice, -halfLeft, true, road, p0, t0, p1, t1, options, roadObject.transform, stats);
                }
                else if (stats.Notes.Count < 20)
                {
                    stats.Notes.Add("template " + look.GetString("template_right") + ": " + failure);
                }
            }

            if (!built)
            {
                // A legacy look's carriageways sit road_offset out from the centre line ("City 2
                // lanes 1m offset" is look8, round the Szczecin start): the game's own header box
                // for these roads covers it, and leaving it out built every such road 2 m narrow.
                float offset = look != null ? look.GetFloat("road_offset", 0f) : 0f;
                float left = offset + (look != null ? look.GetFloat("road_size_left", 4.5f) + look.GetFloat("shoulder_size_left", 0f) : 4.5f);
                float right = offset + (look != null ? look.GetFloat("road_size_right", 4.5f) + look.GetFloat("shoulder_size_right", 0f) : 4.5f);
                stats.RoadsRibbon++;
                LegacyRoadsBuilt++;

                // Every legacy road owns ONE MORE METRE each side than offset + size + shoulder
                // (+ sidewalk), and its terrain starts outside that metre. Three independent readings:
                //  - the item's own five-axis box (geometry + 0.125 m): fitted over the symmetric legacy
                //    roads with no terrain, the half-width is offset + size + shoulder + sidewalk + 1.000 m
                //    on city and rural roads alike, fit error 0.002 m, wherever the box top shows no
                //    railing (0.02 m flat, 0.20 m at a kerb) - round the start and map-wide (Program1009);
                //  - the mappers' vertex records on road terrain, which are absolute positions, sit
                //    +1.00 m outside where BuildSide puts their row: 126 of 132 row-1 and 124 of 134
                //    row-2 records on rural roads map-wide, and the mode on city roads (Program1010);
                //  - where a city road meets a prefab, the prefab's own model continues the road's
                //    cross-section exactly: asphalt to w, kerb_tile gutter w..w+0.5 at road level, a
                //    0.20 m kerb face at w+0.5, kerb top w+0.5..w+1.0 and the sidewalk w+1.0..w+1.0+s at
                //    +0.20 m, and the prefab's terrain border starts at w+1.0+s at +0.20 m (all 41 city
                //    road ends within 150 m of the drive, Program1006/1008/1011).
                // Laid flat at road level and one metre short, as before, every one of those joints
                // showed a 0.20 m open step across both sidewalks, and the road's terrain met the
                // prefab's corner terrain 1 m sideways and 0.2 m down (a vertical crack, median 0.41 m
                // tall across the 18 seams where both exist; 0.00 m with this layout, Program1016).
                // The surface is the look's cross-section drawn the game's way (BuildLegacySurface): the road material's
                // atlas addressed band by band with the look's tex0/tex1, shoulder and transition values, the lines of
                // /material/road/lines.mat on top, and the brush between divided carriageways. It used to be ONE strip
                // of the German template asphalt stretched edge to edge (u 0..1 over 13-40 m, v every 10 m) with no
                // lines at all - every road between the towns of the UK and Poland (ProgramRd2/Rd5).
                LegacyLook legacy = LegacyLookOf(look);
                BuildLegacySurface(map, road, legacy, !road.IsCityRoad, p0, t0, p1, t1, options, roadObject.transform, stats);
                BuildLegacyLines(map, road, legacy, p0, t0, p1, t1, options, roadObject.transform, stats);
                LegacyRoadsByLook.TryGetValue(road.Look, out int seen);
                LegacyRoadsByLook[road.Look] = seen + 1;

                if (road.IsCityRoad)
                {
                    BuildKerb(p0, t0, p1, t1, right, false, road.Length, options, roadObject.transform, stats);
                    BuildKerb(p0, t0, p1, t1, left, true, road.Length, options, roadObject.transform, stats);

                    float outerRight = right + LegacyEdgeBand, outerLeft = left + LegacyEdgeBand;

                    if (road.Right.SidewalkSize > 0f)
                    {
                        BuildSidewalk(map, road.Right.SidewalkMaterial, p0, t0, p1, t1, -outerRight, outerRight + road.Right.SidewalkSize, road.Length, options, roadObject.transform, stats);
                        outerRight += road.Right.SidewalkSize;
                    }

                    if (road.Left.SidewalkSize > 0f)
                    {
                        BuildSidewalk(map, road.Left.SidewalkMaterial, p0, t0, p1, t1, outerLeft + road.Left.SidewalkSize, -outerLeft, road.Length, options, roadObject.transform, stats);
                        outerLeft += road.Left.SidewalkSize;
                    }

                    // Where no terrain follows, the raised edge would stand open over whatever the
                    // ground fill lays there (0.3 m under the ground beside it): close it with a face.
                    if (road.Right.Size <= 0.01f) BuildLip(p0, t0, p1, t1, outerRight, false, road.Length, options, roadObject.transform);
                    if (road.Left.Size <= 0.01f) BuildLip(p0, t0, p1, t1, outerLeft, true, road.Length, options, roadObject.transform);

                    halfRight = outerRight;
                    halfLeft = outerLeft;
                    terrainLift = KerbHeight;
                }
                else
                {
                    // A rural legacy road: the metre is flat (the box's top is 0.02 m over the
                    // nodes); BuildLegacySurface lays the atlas's transition band over it.
                    halfRight = right + LegacyEdgeBand;
                    halfLeft = left + LegacyEdgeBand;
                }
            }
            else
            {
                TemplateRoadsBuilt++;
            }

            // Crash barriers, bridge rails and walls (every road can carry them; the world built none), and the lamps
            // and reflective posts repeated along it.
            BuildRailings(map, road, look, p0, t0, p1, t1, options, roadObject.transform, stats);
            BuildSideModels(map, road, p0, t0, p1, t1, options, roadObject.transform, stats);

            // The ground on both sides, from the slab's edge outward.
            if (options.Terrain)
            {
                EtsTerrainBuilder.BuildRoadTerrain(map, road, p0, t0, p1, t1, halfRight, halfLeft, options, roadObject.transform, stats, terrainLift);
            }

            stats.Roads++;
            NoteRoadBuilt();
            return roadObject;
        }

        /// <summary>
        /// A template model (the road slab or a shoulder) repeated along the curve. The slice's own z
        /// runs along the road (the game's forward is -z, so in Unity the slice runs toward +z); its
        /// x is sideways and its y up. Each repetition covers one slice length of curve, and the last
        /// is stretched or squeezed so the road ends exactly at the far node. The road-to-terrain
        /// transition parts (vis_tran, tran) and the two-texture terrain blend pieces are skipped:
        /// they only make sense over real terrain, and drawn plainly they cover the asphalt with
        /// dirt patches. Shoulders are built for the right side and mirrored for the left.
        /// </summary>
        private static bool BuildTemplateAlong(EtsModelData model, string lookName, string[] variantNames, float lateralOffset, bool mirrorX,
            float roadLength, Vector3 p0, Vector3 t0, Vector3 p1, Vector3 t1, Options options, Transform parent, Stats stats)
        {
            var variants = new List<int>();

            foreach (string name in variantNames)
            {
                if (string.IsNullOrEmpty(name)) continue;
                int v = model.VariantIndex(name);
                if (v >= 0 && !variants.Contains(v)) variants.Add(v);
            }

            if (variants.Count == 0 && model.Variants.Length > 0) variants.Add(0);

            // Slice length along Unity z (game z negated), measured on the parts actually drawn: an
            // edge model holds a 5 m and a 15 m variant, so the whole-model bounds would be wrong.
            float zMin = float.MaxValue, zMax = float.MinValue;

            foreach (EtsModelPart part in model.Parts)
            {
                bool shown = variants.Count == 0;
                foreach (int index in variants) shown |= part.IsVisible(index);
                if (!shown || IsSkippedTemplatePart(part.Name)) continue;

                for (int p = part.PieceStart; p < part.PieceStart + part.PieceCount && p < model.Pieces.Length; p++)
                {
                    zMin = Mathf.Min(zMin, model.Pieces[p].BoundsMin[2]);
                    zMax = Mathf.Max(zMax, model.Pieces[p].BoundsMax[2]);
                }
            }

            if (zMax - zMin < 0.5f)
            {
                return false;
            }

            float sliceStart = -zMax;
            float sliceLength = zMax - zMin;

            roadLength = Mathf.Max(1f, roadLength);
            int repeats = Mathf.Max(1, Mathf.RoundToInt(roadLength / sliceLength));
            float stretch = roadLength / (repeats * sliceLength);

            int look = 0;

            for (int i = 0; i < model.Looks.Length; i++)
            {
                if (model.Looks[i] == lookName)
                {
                    look = i;
                    break;
                }
            }

            string[] materials = look < model.LookMaterials.Length ? model.LookMaterials[look] : new string[0];
            bool any = false;

            foreach (EtsModelPart part in model.Parts)
            {
                bool visible = variants.Count == 0;
                foreach (int index in variants) visible |= part.IsVisible(index);
                if (!visible || IsSkippedTemplatePart(part.Name)) continue;

                for (int p = part.PieceStart; p < part.PieceStart + part.PieceCount && p < model.Pieces.Length; p++)
                {
                    EtsModelPiece piece = model.Pieces[p];
                    string matPath = piece.MaterialIndex < materials.Length ? materials[piece.MaterialIndex] : null;
                    EtsMaterialDef def = EtsEngine.Materials.GetDefinition(matPath);

                    // Terrain blend pieces (effect "...weight.weight...") fade the slab into the ground.
                    if (def != null && def.Effect != null && def.Effect.Contains("weight.weight")) continue;

                    Material material = EtsEngine.Materials.Load(matPath);
                    if (material == null) continue;

                    Mesh mesh = BentSlice(piece, sliceStart, sliceLength, repeats, stretch, lateralOffset, mirrorX, p0, t0, p1, t1, roadLength,
                        EtsMaterialCache.BaseUvChannel(def, piece.UvChannels));

                    var pieceObject = new GameObject((mirrorX ? "left " : lateralOffset != 0f ? "right " : "") + part.Name + " " + p);
                    pieceObject.transform.SetParent(parent, false);
                    pieceObject.AddComponent<MeshFilter>().sharedMesh = mesh;
                    var pieceRenderer = pieceObject.AddComponent<MeshRenderer>();
                    pieceRenderer.sharedMaterial = material;
                    pieceRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

                    bool decal = def != null && def.Effect != null && def.Effect.Contains("decal");

                    if (options.Colliders && !decal)
                    {
                        pieceObject.AddComponent<MeshCollider>().sharedMesh = mesh;
                    }

                    stats.Vertices += mesh.vertexCount;
                    any = true;
                }
            }

            return any;
        }

        /// <summary>Collision-only parts and the road-to-terrain transition parts of a template.</summary>
        private static bool IsSkippedTemplatePart(string name)
        {
            return name.StartsWith("coll") || name == "tran" || name.StartsWith("vis_tran") || name.StartsWith("tran_") || name.EndsWith("_tran");
        }

        /// <summary>The shoulder model named by a road-edge token, hung along one side of the slab.</summary>
        private static void BuildEdge(EtsMap map, ulong edgeToken, string lookName, float slabSlice, float lateralOffset, bool mirrorX, EtsMapRoad road,
            Vector3 p0, Vector3 t0, Vector3 p1, Vector3 t1, Options options, Transform parent, Stats stats)
        {
            if (edgeToken == 0) return;

            EtsSiiUnit edge = map.RoadEdge(edgeToken);
            if (edge == null || !edge.Has("model")) return;

            string modelPath = edge.GetString("model");
            EtsModelData model = EtsEngine.LoadModel(modelPath, out string failure);

            if (model == null)
            {
                if (stats.Notes.Count < 20) stats.Notes.Add("edge " + modelPath + ": " + failure);
                return;
            }

            // Edge models carry a 5 m and a 15 m variant to match the slab's slice length.
            string variantKey = slabSlice >= 10f ? "variant_15" : "variant_5";
            string[] variants = { edge.Has(variantKey) ? edge.GetString(variantKey) : null, "default" };

            BuildTemplateAlong(model, lookName, variants, lateralOffset, mirrorX, road.Length, p0, t0, p1, t1, options, parent, stats);
        }

        private static Mesh BentSlice(EtsModelPiece piece, float sliceStart, float sliceLength, int repeats, float stretch, float lateralOffset, bool mirrorX,
            Vector3 p0, Vector3 t0, Vector3 p1, Vector3 t1, float roadLength, int uvChannel)
        {
            return BentRun(piece, sliceStart, sliceLength, 0, repeats, stretch, lateralOffset, mirrorX, p0, t0, p1, t1, roadLength, uvChannel);
        }

        /// <summary>
        /// BentSlice for a run of repetitions starting at <paramref name="firstRepeat"/> (a railing's start cap, its centre
        /// run and its end cap are different parts laid in turn along the same road).
        /// </summary>
        private static Mesh BentRun(EtsModelPiece piece, float sliceStart, float sliceLength, int firstRepeat, int repeats, float stretch, float lateralOffset, bool mirrorX,
            Vector3 p0, Vector3 t0, Vector3 p1, Vector3 t1, float roadLength, int uvChannel)
        {
            int n = piece.VertexCount;
            var vertices = new Vector3[n * repeats];
            var normals = new Vector3[n * repeats];
            var uv = new Vector2[n * repeats];
            var colors = piece.Colors != null ? new Color32[n * repeats] : null;
            var indices = new int[piece.Indices.Length * repeats];
            float[] uvSet = piece.Uvs != null && piece.Uvs.Length > 0 ? piece.Uvs[Mathf.Clamp(uvChannel, 0, piece.Uvs.Length - 1)] : null;

            // The left-hand copy is the model TURNED ROUND, not mirrored: x and z both reversed,
            // a half turn about the slice's centre. A mirror image would flip the markings - the
            // tram road's "BUS" lane read "SUB" on the left carriageway - where the game's left
            // carriageway carries its traffic the other way and its markings face that way too.
            // A half turn keeps the winding, so the triangles are copied as they are.
            float side = mirrorX ? -1f : 1f;
            float zEnd = 2f * sliceStart + sliceLength;

            for (int r = 0; r < repeats; r++)
            {
                for (int i = 0; i < n; i++)
                {
                    float x = piece.Positions[i * 3] * side + lateralOffset;
                    float y = piece.Positions[i * 3 + 1];
                    float zUnity = -piece.Positions[i * 3 + 2];
                    if (mirrorX) zUnity = zEnd - zUnity;
                    float along = ((firstRepeat + r) * sliceLength + (zUnity - sliceStart)) * stretch;
                    float s = Mathf.Clamp01(along / roadLength);

                    Vector3 point = Hermite(p0, t0, p1, t1, s);
                    Vector3 forward = HermiteTangent(p0, t0, p1, t1, s).normalized;
                    Vector3 right = Vector3.Cross(Vector3.up, forward).normalized;
                    Vector3 up = Vector3.Cross(forward, right).normalized;

                    int o = r * n + i;
                    vertices[o] = point + right * x + up * y;

                    if (piece.Normals != null)
                    {
                        float nx = piece.Normals[i * 3] * side, ny = piece.Normals[i * 3 + 1], nz = -piece.Normals[i * 3 + 2] * side;
                        normals[o] = (right * nx + up * ny + forward * nz).normalized;
                    }
                    else
                    {
                        normals[o] = up;
                    }

                    if (uvSet != null)
                    {
                        // As the model builder's ToUv: the game's v addresses the same row here.
                        uv[o] = new Vector2(uvSet[i * 2], uvSet[i * 2 + 1]);
                    }

                    if (colors != null)
                    {
                        colors[o] = new Color32(piece.Colors[i * 4], piece.Colors[i * 4 + 1], piece.Colors[i * 4 + 2], piece.Colors[i * 4 + 3]);
                    }
                }

                int baseIndex = r * piece.Indices.Length;

                for (int i = 0; i + 2 < piece.Indices.Length; i += 3)
                {
                    indices[baseIndex + i] = piece.Indices[i] + r * n;
                    indices[baseIndex + i + 1] = piece.Indices[i + 1] + r * n;
                    indices[baseIndex + i + 2] = piece.Indices[i + 2] + r * n;
                }
            }

            var mesh = new Mesh { name = "road slice" };

            if (vertices.Length > 65535)
            {
                mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            }

            mesh.vertices = vertices;
            mesh.normals = normals;
            mesh.uv = uv;

            if (colors != null)
            {
                mesh.colors32 = colors;
            }

            mesh.triangles = indices;
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>
        /// The ground between the carriageways of a divided road, in the brush the road names as
        /// its centre material - one token per road in the sector body (offset +116; "0", the
        /// dirt brush, on the tram road beside the DomDepo bay). Tiled at the brush's own size and
        /// laid three centimetres below the slabs so it never fights their inner edges.
        ///
        /// <paramref name="tuck"/> (holes, 2026-09-25): on a legacy road the carriageway strips end exactly at
        /// +-halfGap, so the three centimetres were an open vertical slit along both inner edges - seen across the
        /// median it was a line of sky 2-3 px high the whole length of every divided road (render lab, Prague motorway
        /// look1: 2,000-3,200 hole pixels a cab view). The median now reaches that far under each carriageway, so a
        /// ray through the slit lands on it. Template roads pass 0: their slabs' inner edges are modelled (kerbs,
        /// gutters) and need not be flat.
        /// </summary>
        private static bool BuildMedian(EtsMap map, EtsMapRoad road, Vector3 p0, Vector3 t0, Vector3 p1, Vector3 t1, float halfGap, float length,
            Options options, Transform parent, Stats stats, float tuck = 0f)
        {
            Material material = EtsTerrainBuilder.BrushMaterial(map, road.CenterMaterial);

            if (material == null)
            {
                return false;
            }

            int segments = Mathf.Max(2, Mathf.CeilToInt(length / Mathf.Max(0.5f, options.RoadStep)));
            var vertices = new Vector3[(segments + 1) * 2];
            var normals = new Vector3[(segments + 1) * 2];
            var indices = new int[segments * 6];

            for (int i = 0; i <= segments; i++)
            {
                float s = (float)i / segments;
                Vector3 point = Hermite(p0, t0, p1, t1, s) + Vector3.down * 0.03f;
                Vector3 forward = HermiteTangent(p0, t0, p1, t1, s).normalized;
                Vector3 side = Vector3.Cross(Vector3.up, forward).normalized;
                vertices[i * 2] = point - side * (halfGap + tuck);
                vertices[i * 2 + 1] = point + side * (halfGap + tuck);
                normals[i * 2] = Vector3.up;
                normals[i * 2 + 1] = Vector3.up;
            }

            for (int i = 0; i < segments; i++)
            {
                int a = i * 2, b = a + 1, c = a + 2, d = a + 3;
                indices[i * 6] = a;
                indices[i * 6 + 1] = c;
                indices[i * 6 + 2] = b;
                indices[i * 6 + 3] = b;
                indices[i * 6 + 4] = c;
                indices[i * 6 + 5] = d;
            }

            var mesh = new Mesh { name = "median " + EtsToken.Decode(road.CenterMaterial) };
            mesh.vertices = vertices;
            mesh.normals = normals;
            mesh.uv = EtsTerrainBuilder.BrushUvs(map, road.CenterMaterial, vertices);
            mesh.triangles = indices;
            mesh.RecalculateBounds();

            var median = new GameObject(mesh.name);
            median.transform.SetParent(parent, false);
            median.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = median.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            if (options.Colliders)
            {
                median.AddComponent<MeshCollider>().sharedMesh = mesh;
            }

            stats.Vertices += vertices.Length;
            return true;
        }

        /// <summary>The pavement every legacy city sidewalk round the start names (sidewalk.2).</summary>
        private const string DefaultSidewalk = "/material/road/small_block_pavement.mat";

        /// <summary>How many metres one repeat of a sidewalk's paving covers.</summary>
        private const float SidewalkTile = 4f;

        /// <summary>
        /// A legacy city road's sidewalk: a strip along the curve from <paramref name="left"/> to
        /// <paramref name="right"/> (the same signed offsets as a ribbon, so a strip on the right
        /// side is (-inner, outer) and on the left (outer, -inner)), paved in the side's
        /// "sidewalk.&lt;token&gt;" material and tiled in metres rather than stretched, solid to walk on.
        /// </summary>
        private static void BuildSidewalk(EtsMap map, ulong token, Vector3 p0, Vector3 t0, Vector3 p1, Vector3 t1, float left, float right, float length,
            Options options, Transform parent, Stats stats)
        {
            EtsSiiUnit unit = token != 0 ? map.Sidewalk(token) : null;
            string path = unit != null ? unit.GetString("path") : null;
            Material material = EtsEngine.Materials.Load(string.IsNullOrEmpty(path) ? DefaultSidewalk : path);
            BuildRibbon(p0, t0, p1, t1, left, right, length, options, parent, stats, material, SidewalkTile, "sidewalk", KerbHeight);
        }

        /// <summary>The metre every legacy road owns outside offset + size + shoulder (+ sidewalk): see BuildRoad.</summary>
        private const float LegacyEdgeBand = 1.0f;

        /// <summary>How far a legacy road's median reaches under each carriageway (BuildMedian: it lies 3 cm lower, and without it the step was an open slit).</summary>
        private const float MedianTuck = 0.6f;

        /// <summary>Kerb top and sidewalk height over the road, from the prefabs' own models at their legacy joints.</summary>
        private const float KerbHeight = 0.2f;

        /// <summary>
        /// The kerb every legacy road material names (/def/world/road_material.sii: each road_mat unit
        /// gives kerb: "/material/road/kerb_tile.mat").
        /// </summary>
        private const string KerbMaterial = "/material/road/kerb_tile.mat";

        /// <summary>
        /// A legacy city road's kerb on one side, as the prefabs at its ends model it (harness
        /// Program1008/1020, road1_x_road1_t_entrance_4m at road 002935DE36D0326F): a 0.5 m gutter at road
        /// level from the asphalt's edge, a 0.20 m face, and a 0.5 m top at +0.20 m, all kerb_tile, with
        /// u = 0, 0.453, 0.656, 1.0 across those four edges and v running 0.22 per metre along.
        /// </summary>
        private static void BuildKerb(Vector3 p0, Vector3 t0, Vector3 p1, Vector3 t1, float edge, bool leftSide, float length,
            Options options, Transform parent, Stats stats)
        {
            float g = edge + 0.5f * LegacyEdgeBand, o = edge + LegacyEdgeBand;

            // Cross-section points in increasing signed lateral (left to right), so the swept faces
            // point up and the kerb face points at the road on both sides.
            float[] lateral = leftSide ? new[] { -o, -g, -g, -edge } : new[] { edge, g, g, o };
            float[] height = leftSide ? new[] { KerbHeight, KerbHeight, 0f, 0f } : new[] { 0f, 0f, KerbHeight, KerbHeight };
            float[] u = leftSide ? new[] { 1f, 0.656f, 0.453f, 0f } : new[] { 0f, 0.453f, 0.656f, 1f };
            stats.Vertices += BuildSection(p0, t0, p1, t1, lateral, height, u, 0.22f, length, options, parent, EtsEngine.Materials.Load(KerbMaterial), "kerb", true);
        }

        /// <summary>
        /// A face down the outer edge of a raised sidewalk or kerb where no terrain follows it, from the
        /// kerb top to 0.5 m under it, facing away from the road; drawn only, never solid.
        /// </summary>
        private static void BuildLip(Vector3 p0, Vector3 t0, Vector3 p1, Vector3 t1, float outer, bool leftSide, float length, Options options, Transform parent)
        {
            float[] lateral = leftSide ? new[] { -outer, -outer } : new[] { outer, outer };
            float[] height = leftSide ? new[] { KerbHeight - 0.5f, KerbHeight } : new[] { KerbHeight, KerbHeight - 0.5f };
            float[] u = { 0f, 0.2f };
            BuildSection(p0, t0, p1, t1, lateral, height, u, 0.22f, length, options, parent, EtsEngine.Materials.Load(KerbMaterial), "kerb lip", false);
        }

        /// <summary>
        /// A cross-section (signed lateral offsets in increasing order, heights, and a u per point)
        /// swept along the road's curve at RoadStep spacing - the same stations BuildRibbon uses, so the
        /// strips share their edges with the ribbon and the sidewalk exactly. Each face of the section
        /// gets its own vertices, so the kerb's edges stay hard. Returns the vertex count.
        /// </summary>
        private static int BuildSection(Vector3 p0, Vector3 t0, Vector3 p1, Vector3 t1, float[] lateral, float[] height, float[] u, float vPerMetre,
            float length, Options options, Transform parent, Material material, string name, bool collider)
        {
            if (material == null || lateral.Length < 2)
            {
                return 0;
            }

            int faces = lateral.Length - 1;
            int segments = Mathf.Max(2, Mathf.CeilToInt(length / Mathf.Max(0.5f, options.RoadStep)));
            int perFace = (segments + 1) * 2;
            var vertices = new Vector3[faces * perFace];
            var normals = new Vector3[vertices.Length];
            var uv = new Vector2[vertices.Length];
            var indices = new int[faces * segments * 6];

            for (int i = 0; i <= segments; i++)
            {
                float s = (float)i / segments;
                Vector3 point = Hermite(p0, t0, p1, t1, s);
                Vector3 forward = HermiteTangent(p0, t0, p1, t1, s).normalized;
                Vector3 side = Vector3.Cross(Vector3.up, forward).normalized;
                Vector3 up = Vector3.Cross(forward, side).normalized;
                float v = s * length * vPerMetre;

                for (int f = 0; f < faces; f++)
                {
                    Vector3 a = point + side * lateral[f] + Vector3.up * height[f];
                    Vector3 b = point + side * lateral[f + 1] + Vector3.up * height[f + 1];

                    // The face's normal in the section plane: the section edge a->b turned a quarter
                    // turn toward "up" when it runs outward, toward the road when it runs upward.
                    Vector3 across = b - a;
                    Vector3 normal = (up * Vector3.Dot(across, side) - side * Vector3.Dot(across, Vector3.up)).normalized;

                    int o = f * perFace + i * 2;
                    vertices[o] = a;
                    vertices[o + 1] = b;
                    normals[o] = normal;
                    normals[o + 1] = normal;
                    uv[o] = new Vector2(u[f], v);
                    uv[o + 1] = new Vector2(u[f + 1], v);
                }
            }

            int n = 0;

            for (int f = 0; f < faces; f++)
            {
                for (int i = 0; i < segments; i++)
                {
                    int a = f * perFace + i * 2, b = a + 1, c = a + 2, d = a + 3;
                    indices[n++] = a; indices[n++] = c; indices[n++] = b;
                    indices[n++] = b; indices[n++] = c; indices[n++] = d;
                }
            }

            var mesh = new Mesh { name = "road " + name };
            mesh.vertices = vertices;
            mesh.normals = normals;
            mesh.uv = uv;
            mesh.triangles = indices;
            mesh.RecalculateBounds();

            var strip = new GameObject(name);
            strip.transform.SetParent(parent, false);
            strip.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = strip.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            if (collider && options.Colliders)
            {
                Mesh solid = EtsTerrainBuilder.CollisionMesh(mesh);

                if (solid != null)
                {
                    strip.AddComponent<MeshCollider>().sharedMesh = solid;
                }
            }

            return vertices.Length;
        }

        /// <summary>
        /// A flat strip along the curve. With no material it is the road ribbon: the ribbon material,
        /// one texture across its width. With a material and a tile size it is tiled in metres both
        /// ways (a sidewalk), so the paving keeps its scale whatever the strip's width.
        /// </summary>
        private static void BuildRibbon(Vector3 p0, Vector3 t0, Vector3 p1, Vector3 t1, float left, float right, float length,
            Options options, Transform parent, Stats stats, Material material, float tile, string name, float lift = 0f)
        {
            int segments = Mathf.Max(2, Mathf.CeilToInt(length / Mathf.Max(0.5f, options.RoadStep)));
            var vertices = new Vector3[(segments + 1) * 2];
            var uv = new Vector2[(segments + 1) * 2];
            var normals = new Vector3[(segments + 1) * 2];
            var indices = new int[segments * 6];

            for (int i = 0; i <= segments; i++)
            {
                float s = (float)i / segments;
                Vector3 point = Hermite(p0, t0, p1, t1, s);
                Vector3 forward = HermiteTangent(p0, t0, p1, t1, s).normalized;
                Vector3 side = Vector3.Cross(Vector3.up, forward).normalized;

                vertices[i * 2] = point - side * left + Vector3.up * lift;
                vertices[i * 2 + 1] = point + side * right + Vector3.up * lift;
                normals[i * 2] = Vector3.up;
                normals[i * 2 + 1] = Vector3.up;

                if (tile > 0f)
                {
                    uv[i * 2] = new Vector2(-left / tile, s * length / tile);
                    uv[i * 2 + 1] = new Vector2(right / tile, s * length / tile);
                }
                else
                {
                    uv[i * 2] = new Vector2(0f, s * length / 10f);
                    uv[i * 2 + 1] = new Vector2(1f, s * length / 10f);
                }
            }

            for (int i = 0; i < segments; i++)
            {
                int a = i * 2, b = a + 1, c = a + 2, d = a + 3;
                indices[i * 6] = a;
                indices[i * 6 + 1] = c;
                indices[i * 6 + 2] = b;
                indices[i * 6 + 3] = b;
                indices[i * 6 + 4] = c;
                indices[i * 6 + 5] = d;
            }

            var mesh = new Mesh { name = "road " + name };
            mesh.vertices = vertices;
            mesh.normals = normals;
            mesh.uv = uv;
            mesh.triangles = indices;
            mesh.RecalculateBounds();

            var ribbon = new GameObject(name);
            ribbon.transform.SetParent(parent, false);
            ribbon.AddComponent<MeshFilter>().sharedMesh = mesh;
            ribbon.AddComponent<MeshRenderer>().sharedMaterial = material != null ? material : EtsEngine.Materials.Load(options.RibbonMaterial);

            if (options.Colliders)
            {
                ribbon.AddComponent<MeshCollider>().sharedMesh = mesh;
            }

            stats.Vertices += mesh.vertexCount;
        }

        // ------------------------------------------------------------------ legacy roads, drawn the game's way

        // Every road round the UK start and most of Poland's is a LEGACY look (road_look.sii look0 .. look31: 651 km
        // outside the towns map-wide, 20.6 of the 20.6 km between Felixstowe and Cambridge; ProgramRd2/Rd5). The game
        // draws such a look procedurally from four sources, all read here:
        //  - the road material, road_mat.<token> at body +116 (road_material.sii): an ATLAS of one road cross-section,
        //    1024 x 256, 18 m across (u) and 5 m along (v) - measured on the legacy prefabs that continue these roads,
        //    median du 0.0555 per metre and dv 0.20 per metre (ProgramRd8). u 0 .. 0.944 is asphalt with its wheel
        //    tracks, 0.944 .. 1 the dirt edge;
        //  - the look's bands, side by side from road_offset outward: road_size with u tex0 -> tex1, shoulder_size with
        //    shoulder_tex0 -> 1, and the metre every legacy road owns beyond them with trans_tex0 -> 1 (on a UK motorway
        //    end: asphalt u 0.276 at 2.00 m to 0.949 at 14.50 m, then 0.945 -> 1.000 to 15.50 m; ProgramRd9);
        //  - the lines: /material/road/lines.mat (road.sii road.definition), a 128 x 512 texture of three columns -
        //    solid white (u 0 .. 0.266), broken white (0.328 .. 0.578, alpha over half of every 9.13 m) and yellow
        //    (0.736 .. 1) - laid at the look's centre line (its offset, width and style), between the 4.5 m lanes
        //    (inner_line_style, broken_line_width 0.2) and at the last lane's edge (outer_line_style,
        //    full_line_width 0.5). The prefabs at legacy road ends draw exactly that: centre 2.00 .. 2.32, lane line
        //    6.40 .. 6.60 and edge 10.78 .. 11.25 m on look1, edge 4.25 .. 4.75 m on the rural road1 (ProgramRd6/Rd9);
        //  - the brush between divided carriageways (the .data centre material), as on template roads.
        // Styles: 0 none; 2 broken; 3 (and 4) solid; 1, the most common value, is the broken lane line between lanes
        // and the solid edge line at the side - which is how every legacy prefab draws a style-1 look.

        /// <summary>Metres of road one repeat of a legacy road atlas covers (v), from the legacy prefabs' asphalt.</summary>
        private const float LegacyMetresPerV = 5f;

        /// <summary>Metres of road one repeat of lines.tobj covers; the broken column's dash is half of it.</summary>
        private const float LineMetresPerV = 9.13f;

        /// <summary>A legacy lane (EtsLaneGraph.LaneWidth, and the prefabs' lines at 6.5, 11.0 and 15.5 m on look1/look3).</summary>
        private const float LegacyLaneWidth = 4.5f;

        /// <summary>Lines float this far over the asphalt (the decal shader adds its own depth offset).</summary>
        private const float LineLift = 0.01f;

        private const string DefaultLegacyMaterial = "/material/road/asphalt.mat";
        private const string DefaultLinesMaterial = "/material/road/lines.mat";

        private static readonly Vector2 SolidColumn = new Vector2(0f, 0.266f);
        private static readonly Vector2 BrokenColumn = new Vector2(0.328f, 0.578f);

        /// <summary>One side of a legacy look's cross-section.</summary>
        public sealed class LegacySide
        {
            public float Size = 4.5f, Shoulder;
            public float Tex0 = 0.5f, Tex1 = 0.752f, ShoulderTex0 = 0.88f, ShoulderTex1 = 0.944f, TransTex0 = 0.944f, TransTex1 = 1f;
            public int Lanes;
            public float CenterOffset, CenterWidth = 0.2f;
            public int CenterStyle;
        }

        /// <summary>A legacy look (road_look.sii without template_right), as the game's procedural road reads it.</summary>
        public sealed class LegacyLook
        {
            public float Offset;
            public int InnerStyle, OuterStyle;
            public float TransCenter0 = 0.944f, TransCenter1 = 1f;
            public readonly LegacySide Right = new LegacySide();
            public readonly LegacySide Left = new LegacySide();

            /// <summary>Lanes on one side only: they then lie across the centre line (EtsLaneGraph's one-way layout).</summary>
            public bool OneWay => Right.Lanes == 0 || Left.Lanes == 0;
        }

        private static readonly Dictionary<string, LegacyLook> LegacyLooks = new Dictionary<string, LegacyLook>(StringComparer.Ordinal);

        public static LegacyLook LegacyLookOf(EtsSiiUnit look)
        {
            string key = look != null ? look.Name ?? string.Empty : string.Empty;

            lock (LegacyLooks)
            {
                if (LegacyLooks.TryGetValue(key, out LegacyLook cached))
                {
                    return cached;
                }
            }

            var result = new LegacyLook();

            if (look != null)
            {
                result.Offset = look.GetFloat("road_offset", 0f);
                result.InnerStyle = look.GetInt("inner_line_style", 0);
                result.OuterStyle = look.GetInt("outer_line_style", 0);
                result.TransCenter0 = look.GetFloat("trans_tex0_center", 0.944f);
                result.TransCenter1 = look.GetFloat("trans_tex1_center", 1f);
                ReadLegacySide(look, "right", result.Right);
                ReadLegacySide(look, "left", result.Left);
            }

            lock (LegacyLooks)
            {
                LegacyLooks[key] = result;
            }

            return result;
        }

        private static void ReadLegacySide(EtsSiiUnit look, string side, LegacySide into)
        {
            into.Size = look.GetFloat("road_size_" + side, 4.5f);
            into.Shoulder = look.GetFloat("shoulder_size_" + side, 0f);
            into.Tex0 = look.GetFloat("tex0_" + side, 0.5f);
            into.Tex1 = look.GetFloat("tex1_" + side, 0.752f);
            into.ShoulderTex0 = look.GetFloat("shoulder_tex0_" + side, 0.88f);
            into.ShoulderTex1 = look.GetFloat("shoulder_tex1_" + side, 0.944f);
            into.TransTex0 = look.GetFloat("trans_tex0_" + side, 0.944f);
            into.TransTex1 = look.GetFloat("trans_tex1_" + side, 1f);
            into.Lanes = look.GetArray("lanes_" + side).Count;
            into.CenterOffset = look.GetFloat("center_line_" + side + "_offset", 0f);
            into.CenterWidth = look.GetFloat("center_line_" + side + "_width", 0.2f);
            into.CenterStyle = look.GetInt("center_line_" + side + "_style", 0);
        }

        /// <summary>The asphalt atlas a legacy road is drawn with: its road_mat unit's path, else road_mat.0's.</summary>
        public static string LegacyMaterialPath(EtsMap map, EtsMapRoad road)
        {
            EtsSiiUnit unit = road.LegacyMaterialToken != 0 ? map.RoadMaterial(road.LegacyMaterialToken) : null;
            string path = unit != null ? unit.GetString("path") : null;
            return string.IsNullOrEmpty(path) ? DefaultLegacyMaterial : path;
        }

        private static string LinesMaterialPath(EtsMap map)
        {
            EtsSiiUnit definition = map.RoadDefinition();
            string path = definition != null ? definition.GetString("road_lines") : null;
            return string.IsNullOrEmpty(path) ? DefaultLinesMaterial : path;
        }

        /// <summary>
        /// The asphalt of a legacy road: each side's road, shoulder and (outside town) transition band in the road
        /// material's atlas, and between divided carriageways the road's centre brush (or, without one, the atlas's
        /// centre transition). One mesh, solid to drive on, 5 m per v along the road.
        /// </summary>
        private static void BuildLegacySurface(EtsMap map, EtsMapRoad road, LegacyLook look, bool edgeBands, Vector3 p0, Vector3 t0, Vector3 p1, Vector3 t1,
            Options options, Transform parent, Stats stats)
        {
            string path = LegacyMaterialPath(map, road);
            Material material = EtsEngine.Materials.Load(path);

            if (material == null)
            {
                RoadNote("legacy material " + path + " did not load; the template asphalt stands in");
                material = EtsEngine.Materials.Load(options.RibbonMaterial);

                if (material == null)
                {
                    return;
                }
            }

            LegacyMaterialsUsed.Add(path);
            var faces = new List<Vector4>();
            AddLegacySideFaces(faces, look.Right, look.Offset, edgeBands, 1f);
            AddLegacySideFaces(faces, look.Left, look.Offset, edgeBands, -1f);

            if (look.Offset > 0.01f)
            {
                if (road.CenterMaterial != 0 && BuildMedian(map, road, p0, t0, p1, t1, look.Offset, road.Length, options, parent, stats, MedianTuck))
                {
                    MediansBrushed++;
                }
                else
                {
                    AddFace(faces, -look.Offset, 0f, look.TransCenter0, look.TransCenter1);
                    AddFace(faces, 0f, look.Offset, look.TransCenter1, look.TransCenter0);
                }
            }

            LegacyBands += faces.Count;
            stats.Vertices += BuildFlatStrips(p0, t0, p1, t1, faces, 1f / LegacyMetresPerV, road.Length, 0f, options, parent, material, "legacy asphalt", options.Colliders);
        }

        /// <summary>One side's bands, from the carriageway's inner edge (road_offset) outward.</summary>
        private static void AddLegacySideFaces(List<Vector4> faces, LegacySide side, float offset, bool edgeBand, float sign)
        {
            float a = offset;
            float b = a + Mathf.Max(0f, side.Size);
            float c = b + Mathf.Max(0f, side.Shoulder);
            float d = c + (edgeBand ? LegacyEdgeBand : 0f);
            AddSignedFace(faces, a, b, side.Tex0, side.Tex1, sign);
            AddSignedFace(faces, b, c, side.ShoulderTex0, side.ShoulderTex1, sign);
            AddSignedFace(faces, c, d, side.TransTex0, side.TransTex1, sign);
        }

        /// <summary>A band from <paramref name="from"/> to <paramref name="to"/> metres out on one side (u given at each).</summary>
        private static void AddSignedFace(List<Vector4> faces, float from, float to, float uFrom, float uTo, float sign)
        {
            if (sign > 0f)
            {
                AddFace(faces, from, to, uFrom, uTo);
            }
            else
            {
                AddFace(faces, -to, -from, uTo, uFrom);
            }
        }

        /// <summary>A band between two signed lateral offsets (left to right) with its u at each edge.</summary>
        private static void AddFace(List<Vector4> faces, float left, float right, float uLeft, float uRight)
        {
            if (right - left >= 0.005f)
            {
                faces.Add(new Vector4(left, right, uLeft, uRight));
            }
        }

        /// <summary>
        /// The lines of a legacy look (see above), all in one mesh of lines.mat, the dashes coming from the texture's
        /// broken column as it repeats along the road.
        /// </summary>
        private static void BuildLegacyLines(EtsMap map, EtsMapRoad road, LegacyLook look, Vector3 p0, Vector3 t0, Vector3 p1, Vector3 t1,
            Options options, Transform parent, Stats stats)
        {
            EtsSiiUnit definition = map.RoadDefinition();
            float full = definition != null ? definition.GetFloat("full_line_width", 0.5f) : 0.5f;
            float broken = definition != null ? definition.GetFloat("broken_line_width", 0.2f) : 0.2f;
            var faces = new List<Vector4>();
            AddLegacyLines(faces, look, look.Right, 1f, full, broken);
            AddLegacyLines(faces, look, look.Left, -1f, full, broken);

            if (faces.Count == 0)
            {
                return;
            }

            string path = LinesMaterialPath(map);
            Material material = EtsEngine.Materials.Load(path);

            if (material == null)
            {
                RoadNote("lines material " + path + " did not load");
                return;
            }

            foreach (Vector4 face in faces)
            {
                float u = Mathf.Min(face.z, face.w);
                if (u > 0.7f) LineMarksYellow++;
                else if (u > 0.3f) LineMarksBroken++;
                else LineMarksSolid++;
            }

            stats.Vertices += BuildFlatStrips(p0, t0, p1, t1, faces, 1f / LineMetresPerV, road.Length, LineLift, options, parent, material, "legacy lines", false, true);
        }

        private static void AddLegacyLines(List<Vector4> faces, LegacyLook look, LegacySide side, float sign, float full, float broken)
        {
            float offset = look.Offset;

            // The centre line: its offset from the carriageway's inner edge outward, its own width.
            if (side.CenterStyle != 0 && side.CenterWidth > 0.01f)
            {
                AddLine(faces, offset + side.CenterOffset, offset + side.CenterOffset + side.CenterWidth, side.CenterStyle == 2 ? BrokenColumn : SolidColumn, sign);
            }

            int n = side.Lanes;

            if (n <= 0)
            {
                return;
            }

            // Lanes 4.5 m wide from the inner edge (a one-way look's lie across the centre, as EtsLaneGraph lays them),
            // narrowed only when the side is too small to hold them (the city looks: 8 m for two lanes).
            bool oneWay = look.OneWay;
            float width = Mathf.Min(LegacyLaneWidth, (oneWay ? look.Right.Size + look.Left.Size : side.Size) / n);
            int first = oneWay ? Mathf.CeilToInt(n / 2f) : 0;

            for (int k = 1; k <= n; k++)
            {
                float x = offset + (k - first) * width;

                if (k < n)
                {
                    // Between two lanes; a one-way look's boundary on the centre is its centre line's.
                    if (look.InnerStyle == 0 || Mathf.Abs(x - offset) < 0.01f) continue;
                    Vector2 column = look.InnerStyle == 3 || look.InnerStyle == 4 ? SolidColumn : BrokenColumn;
                    AddLine(faces, x - broken * 0.5f, x + broken * 0.5f, column, sign);
                }
                else if (look.OuterStyle != 0)
                {
                    // The edge of the last lane.
                    bool dashed = look.OuterStyle == 2;
                    float w = dashed ? broken : full;
                    AddLine(faces, x - w * 0.5f, x + w * 0.5f, dashed ? BrokenColumn : SolidColumn, sign);
                }
            }
        }

        private static void AddLine(List<Vector4> faces, float from, float to, Vector2 column, float sign)
        {
            if (sign > 0f)
            {
                AddFace(faces, from, to, column.x, column.y);
            }
            else
            {
                AddFace(faces, -to, -from, column.y, column.x);
            }
        }

        /// <summary>
        /// Flat bands swept along the curve at RoadStep spacing: each face x..y (signed lateral, left to right) with u
        /// z at x and w at y, v running <paramref name="vPerMetre"/> along. Each face has its own vertices, so u may jump
        /// between neighbours (road to shoulder). Returns the vertex count.
        /// </summary>
        private static int BuildFlatStrips(Vector3 p0, Vector3 t0, Vector3 p1, Vector3 t1, List<Vector4> faces, float vPerMetre, float length, float lift,
            Options options, Transform parent, Material material, string name, bool collider, bool decalColour = false)
        {
            if (material == null || faces.Count == 0)
            {
                return 0;
            }

            int segments = Mathf.Max(2, Mathf.CeilToInt(length / Mathf.Max(0.5f, options.RoadStep)));
            int perFace = (segments + 1) * 2;
            var vertices = new Vector3[faces.Count * perFace];
            var normals = new Vector3[vertices.Length];
            var uv = new Vector2[vertices.Length];
            var indices = new int[faces.Count * segments * 6];

            for (int i = 0; i <= segments; i++)
            {
                float s = (float)i / segments;
                Vector3 point = Hermite(p0, t0, p1, t1, s) + Vector3.up * lift;
                Vector3 forward = HermiteTangent(p0, t0, p1, t1, s).normalized;
                Vector3 side = Vector3.Cross(Vector3.up, forward).normalized;
                float v = s * length * vPerMetre;

                for (int f = 0; f < faces.Count; f++)
                {
                    Vector4 face = faces[f];
                    int o = f * perFace + i * 2;
                    vertices[o] = point + side * face.x;
                    vertices[o + 1] = point + side * face.y;
                    normals[o] = Vector3.up;
                    normals[o + 1] = Vector3.up;
                    uv[o] = new Vector2(face.z, v);
                    uv[o + 1] = new Vector2(face.w, v);
                }
            }

            int n = 0;

            for (int f = 0; f < faces.Count; f++)
            {
                for (int i = 0; i < segments; i++)
                {
                    int a = f * perFace + i * 2, b = a + 1, c = a + 2, d = a + 3;
                    indices[n++] = a; indices[n++] = c; indices[n++] = b;
                    indices[n++] = b; indices[n++] = c; indices[n++] = d;
                }
            }

            var mesh = new Mesh { name = "road " + name };

            if (vertices.Length > 65535)
            {
                mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            }

            mesh.vertices = vertices;
            mesh.normals = normals;
            mesh.uv = uv;

            if (decalColour)
            {
                // The decal shader reads vertex colour with 128 meaning one, as the game's lane lines carry it.
                var colours = new Color32[vertices.Length];
                for (int i = 0; i < colours.Length; i++) colours[i] = new Color32(128, 128, 128, 255);
                mesh.colors32 = colours;
            }

            mesh.triangles = indices;
            mesh.RecalculateBounds();

            var strip = new GameObject(name);
            strip.transform.SetParent(parent, false);
            strip.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = strip.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            if (collider)
            {
                strip.AddComponent<MeshCollider>().sharedMesh = mesh;
            }

            return vertices.Length;
        }

        // ------------------------------------------------------------------ railings

        /// <summary>The parts of a railing model a run is made of: centre, start and end caps, and the collision strip.</summary>
        private sealed class RailingParts
        {
            public int Center = -1, Start = -1, End = -1, Collision = -1;
            public float SliceStart, SliceLength;
        }

        /// <summary>
        /// A road's railings (body +124). Placed as the game's legacy prefabs place the same models at road ends
        /// (ProgramRd7/Rd9): the model's origin stands road_offset + offset out from the centre line on its side - the
        /// right side's model as it is, the left side's turned round, unless the side's "invert railing" flag turns
        /// it back - and its 15 m (or 5 m) centre part repeats along the curve, with the start and end parts where the
        /// railing does not go on into the next road. Checked on look1 (road_offset 2): the concrete 2x2 barrier at the
        /// left slot's offset 0 puts its concrete wall at -1.9 .. 1.9 m (the 4 m median) and its steel rails at
        /// +/-15.1 m (the carriageways end at 14.5 m + the 1 m band); the 1 m centre barrier at offset -1 lies at
        /// -0.9 .. 0.95 m. The rail's own collision strip (the centrecol part) is made solid for the truck
        /// (Environment) - but only where it stands outside the lanes or in the median, never across a lane.
        /// </summary>
        private static void BuildRailings(EtsMap map, EtsMapRoad road, EtsSiiUnit look, Vector3 p0, Vector3 t0, Vector3 p1, Vector3 t1,
            Options options, Transform parent, Stats stats)
        {
            if (road.Railings == null || road.Length < 1f)
            {
                return;
            }

            float roadOffset = look != null ? look.GetFloat("road_offset", 0f) : 0f;
            float laneEdge = LaneEdge(look, roadOffset);
            Transform container = null;

            foreach (EtsRoadRailing railing in road.Railings)
            {
                try
                {
                    if (container == null)
                    {
                        container = new GameObject("railings").transform;
                        container.SetParent(parent, false);
                    }

                    BuildRailing(map, road, railing, roadOffset, laneEdge, p0, t0, p1, t1, options, container, stats);
                }
                catch (Exception e)
                {
                    RailingsSkipped++;
                    RoadNote("railing " + railing.Name + " on road " + road.Uid.ToString("X16") + ": " + e.GetType().Name + " " + e.Message);
                }
            }

            if (container != null)
            {
                EtsLayers.SetRecursively(container, EtsLayers.Environment);
            }
        }

        /// <summary>How far out the drive lanes reach on either side, for the solid-railing test.</summary>
        private static float LaneEdge(EtsSiiUnit look, float roadOffset)
        {
            if (look == null)
            {
                return 4.5f;
            }

            if (look.Has("template_right"))
            {
                EtsModelData template = EtsEngine.LoadModel(look.GetString("template_right"), out _);
                float half = template != null ? Mathf.Max(-template.BoundsMin[0], template.BoundsMax[0]) : 5f;
                return roadOffset + half - 0.5f;
            }

            LegacyLook legacy = LegacyLookOf(look);
            float edge = 0f;

            foreach (LegacySide side in new[] { legacy.Right, legacy.Left })
            {
                float width = side.Lanes > 0 ? Mathf.Min(LegacyLaneWidth, side.Size / side.Lanes) * side.Lanes : side.Size;
                edge = Mathf.Max(edge, roadOffset + width);
            }

            return edge;
        }

        private static void BuildRailing(EtsMap map, EtsMapRoad road, EtsRoadRailing railing, float roadOffset, float laneEdge, Vector3 p0, Vector3 t0, Vector3 p1, Vector3 t1,
            Options options, Transform parent, Stats stats)
        {
            EtsSiiUnit unit = map.Railing(railing.Model);
            string path = unit != null ? unit.GetString("model_desc") : null;

            if (string.IsNullOrEmpty(path))
            {
                RailingsSkipped++;
                RoadNote("railing." + railing.Name + ": no such railing unit");
                return;
            }

            EtsModelData model = EtsEngine.LoadModel(path, out string failure);

            if (model == null)
            {
                RailingsSkipped++;
                RoadNote("railing model " + path + ": " + failure);
                return;
            }

            RailingParts parts = RailingPartsOf(model, road.Length);

            if (parts == null)
            {
                RailingsSkipped++;
                RoadNote("railing model " + path + ": no centre part");
                return;
            }

            bool inverted = railing.Left ? road.LeftRailingInverted : road.RightRailingInverted;
            bool centreOnly = railing.Left ? road.LeftRailingCenterOnly : road.RightRailingCenterOnly;
            bool doubleSided = railing.Left ? road.LeftRailingDoubleSided : road.RightRailingDoubleSided;
            float origin = roadOffset + railing.Offset;
            float lateral = railing.Left ? -origin : origin;
            bool turned = railing.Left != inverted;
            bool capStart = !centreOnly && !RailingContinues(map, road, road.StartNode, railing.Model);
            bool capEnd = !centreOnly && !RailingContinues(map, road, road.EndNode, railing.Model);
            string look = unit.GetString("look_name", "default");

            // Solid only where the rail stands clear of the lanes (or in the median of a divided road).
            float railX = RailX(model, parts);
            float railLateral = lateral + (turned ? -railX : railX);
            bool solid = options.Colliders && (Mathf.Abs(railLateral) >= laneEdge - 0.05f || Mathf.Abs(railLateral) <= roadOffset - 0.2f);

            var root = new GameObject("railing " + railing.Name + (railing.Left ? " left " : " right ") + railing.Slot);
            root.transform.SetParent(parent, false);
            stats.Vertices += BuildRailingCopy(model, parts, look, lateral, turned, capStart, capEnd, solid, road.Length, p0, t0, p1, t1, root.transform);

            if (doubleSided)
            {
                stats.Vertices += BuildRailingCopy(model, parts, look, lateral, !turned, capStart, capEnd, false, road.Length, p0, t0, p1, t1, root.transform);
            }

            RailingRuns++;
            RailingModelsUsed.Add(path);

            if (solid)
            {
                RailingColliders++;
            }
        }

        /// <summary>The 15 m part set when the road is long enough and the model has one, else the 5 m set.</summary>
        private static RailingParts RailingPartsOf(EtsModelData model, float roadLength)
        {
            foreach (string size in roadLength >= 10f ? new[] { "15m", "5m" } : new[] { "5m", "15m" })
            {
                int center = PartIndex(model, "center" + size);

                if (center < 0)
                {
                    continue;
                }

                var parts = new RailingParts
                {
                    Center = center,
                    Start = PartIndex(model, "start" + size),
                    End = PartIndex(model, "end" + size),
                    Collision = PartIndex(model, "centercol" + size)
                };

                float zMin = float.MaxValue, zMax = float.MinValue;
                EtsModelPart part = model.Parts[center];

                for (int p = part.PieceStart; p < part.PieceStart + part.PieceCount && p < model.Pieces.Length; p++)
                {
                    zMin = Mathf.Min(zMin, model.Pieces[p].BoundsMin[2]);
                    zMax = Mathf.Max(zMax, model.Pieces[p].BoundsMax[2]);
                }

                float nominal = size == "15m" ? 15f : 5f;

                if (zMax - zMin < 0.5f)
                {
                    zMin = -nominal * 0.5f;
                    zMax = nominal * 0.5f;
                }

                parts.SliceStart = -zMax;
                parts.SliceLength = zMax - zMin;
                return parts;
            }

            return null;
        }

        private static int PartIndex(EtsModelData model, string name)
        {
            for (int i = 0; i < model.Parts.Length; i++)
            {
                if (model.Parts[i].Name == name) return i;
            }

            return -1;
        }

        /// <summary>Where across the model the rail runs: the collision strip's middle, else the centre part's.</summary>
        private static float RailX(EtsModelData model, RailingParts parts)
        {
            EtsModelPart part = model.Parts[parts.Collision >= 0 ? parts.Collision : parts.Center];
            float min = float.MaxValue, max = float.MinValue;

            for (int p = part.PieceStart; p < part.PieceStart + part.PieceCount && p < model.Pieces.Length; p++)
            {
                min = Mathf.Min(min, model.Pieces[p].BoundsMin[0]);
                max = Mathf.Max(max, model.Pieces[p].BoundsMax[0]);
            }

            return min <= max ? (min + max) * 0.5f : 0f;
        }

        /// <summary>Whether the item on the other side of this node is a road carrying the same railing (then no cap here).</summary>
        private static bool RailingContinues(EtsMap map, EtsMapRoad road, ulong nodeUid, ulong model)
        {
            EtsMapNode node = map.FindNode(nodeUid);

            if (node == null)
            {
                return false;
            }

            ulong other = node.ForwardItem == road.Uid ? node.BackwardItem : node.ForwardItem;
            EtsMapRoad next = other != 0 && other != road.Uid ? map.FindRoad(other) : null;

            if (next == null || next.Railings == null)
            {
                return false;
            }

            foreach (EtsRoadRailing railing in next.Railings)
            {
                if (railing.Model == model) return true;
            }

            return false;
        }

        /// <summary>
        /// One copy of a railing run along the road: the start cap, the centre part repeated, the end cap (a turned copy
        /// takes the end part first, its geometry being reversed), and the collision strip when solid.
        /// </summary>
        private static int BuildRailingCopy(EtsModelData model, RailingParts parts, string lookName, float lateral, bool turned, bool capStart, bool capEnd, bool solid,
            float length, Vector3 p0, Vector3 t0, Vector3 p1, Vector3 t1, Transform parent)
        {
            int repeats = Mathf.Max(1, Mathf.RoundToInt(length / parts.SliceLength));
            float stretch = length / (repeats * parts.SliceLength);
            int firstCap = turned ? parts.End : parts.Start;
            int lastCap = turned ? parts.Start : parts.End;
            var runs = new List<Vector3Int>();
            int from = 0, to = repeats;

            if (capStart && firstCap >= 0)
            {
                runs.Add(new Vector3Int(firstCap, 0, 1));
                from = 1;
            }

            if (capEnd && lastCap >= 0 && to - from >= 1)
            {
                runs.Add(new Vector3Int(lastCap, repeats - 1, 1));
                to = repeats - 1;
            }

            if (to > from)
            {
                runs.Add(new Vector3Int(parts.Center, from, to - from));
            }

            int lookIndex = Mathf.Max(0, model.LookIndex(lookName));
            string[] materials = lookIndex < model.LookMaterials.Length ? model.LookMaterials[lookIndex] : new string[0];
            int vertices = 0;

            foreach (Vector3Int run in runs)
            {
                EtsModelPart part = model.Parts[run.x];

                for (int p = part.PieceStart; p < part.PieceStart + part.PieceCount && p < model.Pieces.Length; p++)
                {
                    EtsModelPiece piece = model.Pieces[p];
                    string matPath = piece.MaterialIndex < materials.Length ? materials[piece.MaterialIndex] : null;
                    EtsMaterialDef def = EtsEngine.Materials.GetDefinition(matPath);

                    if (def == null || def.Effect == null || def.EffectHas("shadowonly") || def.Effect.StartsWith("eut2.none", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    Material material = EtsEngine.Materials.Load(matPath);

                    if (material == null)
                    {
                        continue;
                    }

                    Mesh mesh = BentRun(piece, parts.SliceStart, parts.SliceLength, run.y, run.z, stretch, lateral, turned, p0, t0, p1, t1, length,
                        EtsMaterialCache.BaseUvChannel(def, piece.UvChannels));
                    var pieceObject = new GameObject(part.Name + " " + p);
                    pieceObject.transform.SetParent(parent, false);
                    pieceObject.AddComponent<MeshFilter>().sharedMesh = mesh;
                    var renderer = pieceObject.AddComponent<MeshRenderer>();
                    renderer.sharedMaterial = material;
                    renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    vertices += mesh.vertexCount;
                }
            }

            if (solid && parts.Collision >= 0)
            {
                EtsModelPart part = model.Parts[parts.Collision];

                for (int p = part.PieceStart; p < part.PieceStart + part.PieceCount && p < model.Pieces.Length; p++)
                {
                    EtsModelPiece walls = UprightNarrow(model.Pieces[p]);

                    if (walls == null)
                    {
                        continue;
                    }

                    Mesh mesh = BentRun(walls, parts.SliceStart, parts.SliceLength, 0, repeats, stretch, lateral, turned, p0, t0, p1, t1, length, 0);
                    var collisionObject = new GameObject("railing collision " + p);
                    collisionObject.transform.SetParent(parent, false);
                    collisionObject.AddComponent<MeshCollider>().sharedMesh = mesh;
                }
            }

            return vertices;
        }

        /// <summary>
        /// The collision strip's upright, narrow triangles only (a wall along the rail: under 0.6 m across and facing
        /// sideways), so no model's collision part can ever lay a floor or a wall across the carriageway. Null when none.
        /// </summary>
        private static EtsModelPiece UprightNarrow(EtsModelPiece piece)
        {
            if (piece == null || piece.Indices == null || piece.Positions == null)
            {
                return null;
            }

            var kept = new List<int>(piece.Indices.Length);

            for (int i = 0; i + 2 < piece.Indices.Length; i += 3)
            {
                int a = piece.Indices[i] * 3, b = piece.Indices[i + 1] * 3, c = piece.Indices[i + 2] * 3;
                var pa = new Vector3(piece.Positions[a], piece.Positions[a + 1], piece.Positions[a + 2]);
                var pb = new Vector3(piece.Positions[b], piece.Positions[b + 1], piece.Positions[b + 2]);
                var pc = new Vector3(piece.Positions[c], piece.Positions[c + 1], piece.Positions[c + 2]);
                float across = Mathf.Max(pa.x, Mathf.Max(pb.x, pc.x)) - Mathf.Min(pa.x, Mathf.Min(pb.x, pc.x));
                Vector3 normal = Vector3.Cross(pb - pa, pc - pa);

                if (across > 0.6f || normal.sqrMagnitude < 1e-8f || Mathf.Abs(normal.normalized.y) > 0.5f)
                {
                    continue;
                }

                kept.Add(piece.Indices[i]);
                kept.Add(piece.Indices[i + 1]);
                kept.Add(piece.Indices[i + 2]);
            }

            if (kept.Count == 0)
            {
                return null;
            }

            return new EtsModelPiece
            {
                MaterialIndex = piece.MaterialIndex,
                VertexCount = piece.VertexCount,
                UvChannels = piece.UvChannels,
                Positions = piece.Positions,
                Normals = piece.Normals,
                Uv0 = piece.Uv0,
                Uvs = piece.Uvs,
                Indices = kept.ToArray(),
                BoundsMin = piece.BoundsMin,
                BoundsMax = piece.BoundsMax
            };
        }

        // ------------------------------------------------------------------ road side models

        /// <summary>
        /// The lamps and reflective posts a road repeats along its sides (EtsMapRoad.SideModels, from the .data payload):
        /// sign units with road_model: true, every Distance metres at Offset metres from the centre line (absolute: the UK
        /// look1's posts at 15 m stand just outside its 14.5 m carriageway, look8's T-lamps at 0 m in its median), the
        /// left side's turned to face the road. Built a few a frame by EtsDetailDriver, on the ground under them, like
        /// the prefabs' own signs.
        /// </summary>
        private static void BuildSideModels(EtsMap map, EtsMapRoad road, Vector3 p0, Vector3 t0, Vector3 p1, Vector3 t1, Options options, Transform parent, Stats stats)
        {
            if (road.SideModels == null || !options.Details || road.Length < 1f)
            {
                return;
            }

            Transform container = null;

            foreach (EtsRoadSideModel slot in road.SideModels)
            {
                if (slot.Model == 0 || slot.Distance < 2f)
                {
                    continue;
                }

                EtsSiiUnit unit = map.SignModel(slot.Model);
                string path = unit != null ? unit.GetString("model_desc", null) : null;

                if (string.IsNullOrEmpty(path))
                {
                    SideModelsSkipped++;
                    RoadNote("road model sign." + slot.Name + ": no such sign unit");
                    continue;
                }

                bool flipped = road.SideModelFlipped(slot.Left, slot.Slot);
                float lateral = slot.Left ? -slot.Offset : slot.Offset;
                float start = road.SideModelShifted(slot.Left, slot.Slot) ? slot.Distance * 0.5f : 0f;
                int placed = 0;

                for (float d = start; d <= road.Length - 0.5f && placed < 256; d += slot.Distance, placed++)
                {
                    float s = d / road.Length;
                    Vector3 forward = HermiteTangent(p0, t0, p1, t1, s).normalized;
                    Vector3 right = Vector3.Cross(Vector3.up, forward).normalized;
                    Vector3 position = Hermite(p0, t0, p1, t1, s) + right * lateral;
                    Quaternion rotation = Quaternion.LookRotation(slot.Left != flipped ? -forward : forward, Vector3.up);

                    if (container == null)
                    {
                        container = new GameObject("detail road models").transform;
                        container.SetParent(parent, false);
                    }

                    Transform into = container;
                    EtsSiiUnit sign = unit;
                    string modelPath = path;
                    Options buildOptions = options;
                    SideModelsQueued++;

                    EtsDetailDriver.Enqueue(() =>
                    {
                        if (into == null)
                        {
                            return;   // the road was unloaded before its turn came
                        }

                        BuildSideModel(sign, modelPath, position, rotation, buildOptions, into);
                    });
                }
            }
        }

        private static void BuildSideModel(EtsSiiUnit unit, string path, Vector3 position, Quaternion rotation, Options options, Transform parent)
        {
            try
            {
                EtsModelData model = EtsEngine.LoadModel(path, out string failure);

                if (model == null)
                {
                    SideModelsSkipped++;
                    RoadNote("road model " + path + ": " + failure);
                    return;
                }

                float height = model.BoundsMax[1] - model.BoundsMin[1];
                bool solid = options.Colliders && !unit.GetBool("dynamic", false) && height >= 0.5f;
                bool shadows = unit.GetInt("drop_sun_shadows", 1) != 0 && height >= 4f;
                string look = unit.GetString("look_name", "default");

                // On the ground under it: the road's own terrain, the band or a junction, whichever is there.
                Vector3 world = parent.TransformPoint(position);

                if (Physics.Raycast(world + Vector3.up * 3f, Vector3.down, out RaycastHit hit, 6f, EtsLayers.GroundMask, QueryTriggerInteraction.Ignore))
                {
                    position.y += hit.point.y - world.y;
                }

                EtsModelBuilder.Result built = EtsModelBuilder.Build(model, new EtsModelBuilder.Options
                {
                    Look = string.IsNullOrEmpty(look) ? "default" : look.Trim(),
                    Variants = new[] { "default" },
                    CreateLocators = false,
                    AddColliders = solid,
                    IncludeCollision = false,
                    CastShadows = shadows
                }, parent, EtsEngine.Materials);

                GameObject root = built.Root;
                root.name = "detail road model " + unit.GetString("sign_name", path);
                root.transform.localPosition = position;
                root.transform.localRotation = rotation;

                if (solid)
                {
                    EtsLayers.SetRecursively(root.transform, EtsLayers.Environment);
                }

                EtsSemaphores.SmallDetailCulling(root, height >= 5f ? 0.004f : 0.005f);
                SideModelsBuilt++;
            }
            catch (Exception e)
            {
                SideModelsSkipped++;
                RoadNote("road model " + path + ": " + e.GetType().Name + " " + e.Message);
            }
        }

        // ------------------------------------------------------------------ what a road reads, and the report

        /// <summary>
        /// The files BuildRoad reads besides a template road's slab and edge models and the terrain, for the map viewer's
        /// worker-thread prefetch: a legacy road's asphalt atlas and the lines material, and every road's railing
        /// models and side models with the look each is drawn in.
        /// </summary>
        public static void RoadReads(EtsMap map, EtsMapRoad road, List<string> materials, List<KeyValuePair<string, string>> models)
        {
            EtsSiiUnit look = map.RoadLook(road.LookToken);

            if (look == null || !look.Has("template_right"))
            {
                materials.Add(LegacyMaterialPath(map, road));
                materials.Add(LinesMaterialPath(map));
            }

            if (road.Railings != null)
            {
                foreach (EtsRoadRailing railing in road.Railings)
                {
                    EtsSiiUnit unit = map.Railing(railing.Model);
                    string path = unit != null ? unit.GetString("model_desc") : null;
                    if (!string.IsNullOrEmpty(path)) models.Add(new KeyValuePair<string, string>(path, unit.GetString("look_name", "default")));
                }
            }

            if (road.SideModels != null)
            {
                foreach (EtsRoadSideModel slot in road.SideModels)
                {
                    EtsSiiUnit unit = slot.Model != 0 ? map.SignModel(slot.Model) : null;
                    string path = unit != null ? unit.GetString("model_desc", null) : null;
                    if (!string.IsNullOrEmpty(path)) models.Add(new KeyValuePair<string, string>(path, unit.GetString("look_name", "default")));
                }
            }
        }

        public static int LegacyRoadsBuilt, TemplateRoadsBuilt, LegacyBands, MediansBrushed, LineMarksSolid, LineMarksBroken, LineMarksYellow;
        public static int RailingRuns, RailingColliders, RailingsSkipped, SideModelsQueued, SideModelsBuilt, SideModelsSkipped;
        private static readonly HashSet<string> LegacyMaterialsUsed = new HashSet<string>(StringComparer.Ordinal);
        private static readonly HashSet<string> RailingModelsUsed = new HashSet<string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, int> LegacyRoadsByLook = new Dictionary<string, int>(StringComparer.Ordinal);
        private static readonly List<string> RoadNotes = new List<string>();
        private static int _nextRoadsReport = 100;

        private static void RoadNote(string note)
        {
            if (RoadNotes.Count < 12 && !RoadNotes.Contains(note))
            {
                RoadNotes.Add(note);
                Debug.LogWarning("[SomniumEuroTruck] Roads: " + note);
            }
        }

        /// <summary>
        /// "Roads: N ribbons, M template roads (...)" - how the legacy looks were drawn (atlas bands, road materials,
        /// line marks by kind, brushed medians, the looks), the railings and the side models.
        /// </summary>
        public static string RoadsReport()
        {
            var looks = new List<KeyValuePair<string, int>>(LegacyRoadsByLook);
            looks.Sort((a, b) => b.Value.CompareTo(a.Value));
            var lookText = new List<string>();
            for (int i = 0; i < looks.Count && i < 8; i++) lookText.Add(looks[i].Key + " x" + looks[i].Value);
            var materials = new List<string>(LegacyMaterialsUsed);
            materials.Sort(StringComparer.Ordinal);

            return "Roads: " + LegacyRoadsBuilt + " ribbons, " + TemplateRoadsBuilt + " template roads (legacy looks drawn the game's way: " +
                   LegacyBands + " atlas bands at 18 m per u and " + LegacyMetresPerV + " m per v in " + materials.Count + " road material(s) " +
                   string.Join(", ", materials.ToArray()) + "; line marks from lines.mat: " + LineMarksSolid + " solid, " + LineMarksBroken + " broken, " +
                   LineMarksYellow + " yellow; " + MediansBrushed + " medians in their centre brush; looks " + string.Join(", ", lookText.ToArray()) +
                   ". Railings: " + RailingRuns + " runs from " + RailingModelsUsed.Count + " models, " + RailingColliders + " solid, " + RailingsSkipped +
                   " skipped. Road models (lamps, posts): " + SideModelsQueued + " queued, " + SideModelsBuilt + " built, " + SideModelsSkipped + " skipped" +
                   (RoadNotes.Count > 0 ? "; first problem: " + RoadNotes[0] : "") + ")";
        }

        /// <summary>Logs the report after the first 100 roads and each time the count doubles.</summary>
        private static void NoteRoadBuilt()
        {
            if (LegacyRoadsBuilt + TemplateRoadsBuilt >= _nextRoadsReport)
            {
                _nextRoadsReport *= 2;
                Debug.Log("[SomniumEuroTruck] " + RoadsReport());
            }
        }

        public static Vector3 Hermite(Vector3 p0, Vector3 t0, Vector3 p1, Vector3 t1, float s)
        {
            float s2 = s * s, s3 = s2 * s;
            return (2f * s3 - 3f * s2 + 1f) * p0 + (s3 - 2f * s2 + s) * t0 + (-2f * s3 + 3f * s2) * p1 + (s3 - s2) * t1;
        }

        public static Vector3 HermiteTangent(Vector3 p0, Vector3 t0, Vector3 p1, Vector3 t1, float s)
        {
            float s2 = s * s;
            return (6f * s2 - 6f * s) * p0 + (3f * s2 - 4f * s + 1f) * t0 + (-6f * s2 + 6f * s) * p1 + (3f * s2 - 2f * s) * t1;
        }

        // ------------------------------------------------------------------ prefabs

        /// <summary>
        /// Prefabs built so far this session (BuildPrefab): stood tilted on node 0's full rotation,
        /// level (up tilted by under 0.26 degrees, the "tilted" test (full x up).y &lt; 0.99999, so all
        /// but the same as yaw-only), and refused the full rotation (yaw-only kept). Diagnostics only.
        /// </summary>
        public static int PrefabsTilted, PrefabsLevel, PrefabsTiltRefused;

        /// <summary>Map models placed with a negative scale product - mirrored copies (BuildModel), for the "Runs:" line.</summary>
        public static int MirroredModels;

        /// <summary>
        /// A prefab stands where the game stands it: descriptor node Origin on map node 0, turned by
        /// <b>map node 0's whole rotation</b> - yaw, pitch and roll - after the descriptor node's own
        /// yaw and pitch are taken out. Returns the prefab's origin and rotation in Unity's frame, so
        /// that <c>position + rotation * EtsModelBuilder.ToUnity(p)</c> is any descriptor-frame point
        /// p on the map. The model (BuildPrefab), its ground (EtsPrefabTerrainBuilder, through the
        /// same transform) and its spawn points (TryPrefabSpawn) all go through this one function,
        /// so they cannot disagree.
        ///
        /// It used to be yaw only, which leaves every other node of a junction on a slope off its
        /// map height. Measured by harness Program1920 over the 949 prefabs in the sectors within
        /// 7 km of the start (the selection of the traffic fixer's probe 1751), against every map
        /// node other than node 0 (1,696): yaw-only misses the node heights by p99 1.61 m, worst
        /// 3.73 m, with 222 nodes on 166 prefabs more than 0.3 m off; node 0's full rotation misses
        /// by p99 0.002 m, worst 0.005 m, none over 0.3 m, and lands the nodes closer across the
        /// ground too (horizontal worst 0.014 m against 0.117 m yaw-only). The one descriptor there
        /// whose anchor direction is not level (/prefab/cross/hw2-2_x_city2-2.ppd: y 0.0262, 1.5 degrees,
        /// over a 518 m span) needs its pitch undone as well as its yaw: with only the yaw undone
        /// (probe 1751; checker probe 1941) that prefab missed by up to 13.5 m.
        ///
        /// A rotation that does not put the map nodes back on themselves - more than 0.25 m up or
        /// down, as EtsLaneGraph.Slope refuses a plane, or 1 m across - is not used when yaw-only
        /// does better: the prefab then stands yaw-only as before (<paramref name="refused"/>). A
        /// prefab whose node 0 is level gets the same rotation yaw-only gave it, to float rounding.
        ///
        /// Pure (no engine calls: the quaternions are built by hand, and Unity's Euler and
        /// LookRotation are native), so the harness can measure it. False when the prefab has no
        /// node 0 or no usable direction; then nothing can be placed.
        /// </summary>
        public static bool TryPrefabFrame(EtsMap map, EtsMapPrefab prefab, EtsPrefabDescriptor ppd, Vector3 centreGame,
            out Vector3 position, out Quaternion rotation, out bool tilted)
        {
            return TryPrefabFrame(map, prefab, ppd, centreGame, out position, out rotation, out tilted, out _);
        }

        /// <summary><see cref="TryPrefabFrame(EtsMap, EtsMapPrefab, EtsPrefabDescriptor, Vector3, out Vector3, out Quaternion, out bool)"/>, saying whether the full rotation was refused.</summary>
        public static bool TryPrefabFrame(EtsMap map, EtsMapPrefab prefab, EtsPrefabDescriptor ppd, Vector3 centreGame,
            out Vector3 position, out Quaternion rotation, out bool tilted, out bool refused)
        {
            return TryPrefabFrame(prefab, ppd, centreGame, map.FindNode, out position, out rotation, out tilted, out refused);
        }

        /// <summary>
        /// The same, with the map nodes looked up by <paramref name="findNode"/> - for a caller that reads sectors itself
        /// (EtsHqCatalog.SpotsOf takes such a lookup), so its spots stand where BuildPrefab stands the yard.
        /// </summary>
        public static bool TryPrefabFrame(EtsMapPrefab prefab, EtsPrefabDescriptor ppd, Vector3 centreGame, Func<ulong, EtsMapNode> findNode,
            out Vector3 position, out Quaternion rotation, out bool tilted, out bool refused)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            tilted = false;
            refused = false;

            if (ppd == null || ppd.Nodes.Length == 0 || prefab.NodeUids.Length == 0)
            {
                return false;
            }

            EtsMapNode mapNode = findNode(prefab.NodeUids[0]);

            if (mapNode == null)
            {
                return false;
            }

            int origin = Mathf.Clamp(prefab.Origin, 0, ppd.Nodes.Length - 1);
            EtsPrefabDescriptor.Node anchor = ppd.Nodes[origin];

            Vector3 anchorPosition = EtsModelBuilder.ToUnity(anchor.Position);
            Vector3 anchorDirection = EtsModelBuilder.ToUnity(anchor.Direction[0], anchor.Direction[1], anchor.Direction[2]);
            Vector3 mapPosition = ToWorld(mapNode.Position, centreGame);
            Quaternion mapRotation = EtsModelBuilder.ToUnityRotation(mapNode.Rotation);
            Vector3 mapForward = mapRotation * Vector3.forward;

            // Yaw only: the turn that lays the anchor's flattened direction on node 0's flattened forward.
            Vector3 flatAnchor = new Vector3(anchorDirection.x, 0f, anchorDirection.z);
            Vector3 flatForward = new Vector3(mapForward.x, 0f, mapForward.z);

            if (flatAnchor.sqrMagnitude < 1e-8f || flatForward.sqrMagnitude < 1e-8f)
            {
                return false;
            }

            float yaw = Vector3.SignedAngle(flatAnchor.normalized, flatForward.normalized, Vector3.up);
            Quaternion yawOnly = YawRotation(yaw * Mathf.Deg2Rad);

            // Node 0's whole rotation, with the anchor's own frame taken out first: the anchor direction is
            // then carried onto node 0's forward exactly, and "up" onto node 0's up (its roll).
            Quaternion full = mapRotation * Conjugate(FrameOf(anchorDirection));
            float fullMiss = NodeMiss(findNode, prefab, ppd, origin, anchorPosition, mapPosition, full, centreGame);

            if (fullMiss <= 0.25f || fullMiss <= NodeMiss(findNode, prefab, ppd, origin, anchorPosition, mapPosition, yawOnly, centreGame))
            {
                rotation = full;
                tilted = (full * Vector3.up).y < 0.99999f;
            }
            else
            {
                rotation = yawOnly;
                refused = true;
            }

            position = mapPosition - rotation * anchorPosition;
            return true;
        }

        /// <summary>
        /// How far a rotation puts the prefab's loaded map nodes (other than node 0, which it stands
        /// on) off their own heights, worst case in metres; a node more than 1 m off across the ground
        /// counts as infinitely far (the node order is then not the one assumed). 0 when there is
        /// nothing to check.
        /// </summary>
        private static float NodeMiss(Func<ulong, EtsMapNode> findNode, EtsMapPrefab prefab, EtsPrefabDescriptor ppd, int origin, Vector3 anchorPosition,
            Vector3 mapPosition, Quaternion rotation, Vector3 centreGame)
        {
            int n = ppd.Nodes.Length;
            float worst = 0f;

            // Map node k stands on descriptor node (Origin + k) mod n (EtsMapPrefab.Origin).
            for (int k = 1; k < n && k < prefab.NodeUids.Length; k++)
            {
                EtsMapNode node = findNode(prefab.NodeUids[k]);

                if (node == null)
                {
                    continue;
                }

                Vector3 placed = mapPosition + rotation * (EtsModelBuilder.ToUnity(ppd.Nodes[(origin + k) % n].Position) - anchorPosition);
                Vector3 actual = ToWorld(node.Position, centreGame);
                float dx = placed.x - actual.x, dz = placed.z - actual.z;

                if (dx * dx + dz * dz > 1f)
                {
                    return float.MaxValue;
                }

                worst = Mathf.Max(worst, Mathf.Abs(placed.y - actual.y));
            }

            return worst;
        }

        /// <summary>A turn of the given radians about Unity's y axis (what Quaternion.Euler(0, degrees, 0) makes).</summary>
        private static Quaternion YawRotation(float radians)
        {
            return new Quaternion(0f, Mathf.Sin(radians * 0.5f), 0f, Mathf.Cos(radians * 0.5f));
        }

        /// <summary>
        /// The rotation that takes Unity's forward (0, 0, 1) onto a direction by a yaw and then a
        /// pitch, no roll - what Quaternion.LookRotation(direction, Vector3.up) gives for a direction
        /// that is not vertical.
        /// </summary>
        private static Quaternion FrameOf(Vector3 direction)
        {
            direction.Normalize();
            float yaw = Mathf.Atan2(direction.x, direction.z);
            float pitch = -Mathf.Asin(Mathf.Clamp(direction.y, -1f, 1f));
            return YawRotation(yaw) * new Quaternion(Mathf.Sin(pitch * 0.5f), 0f, 0f, Mathf.Cos(pitch * 0.5f));
        }

        private static Quaternion Conjugate(Quaternion q)
        {
            return new Quaternion(-q.x, -q.y, -q.z, q.w);
        }

        /// <summary>
        /// Where the game would stand a vehicle on this prefab, in world space.
        ///
        /// The descriptor's spawn points are in the prefab's own frame, at y = 0 on its paved
        /// surface, so they are put through exactly the transform BuildPrefab uses to stand the
        /// prefab up (<see cref="TryPrefabFrame"/>): on a tilted yard the point lands on its tilted
        /// surface. Anything else is a guess, and a guess here drops the truck through the world.
        /// The facing is the spawn point's own, turned with the prefab and read as a heading.
        /// </summary>
        public static bool TryPrefabSpawn(EtsMap map, EtsMapPrefab prefab, Vector3 centreGame, int[] wantedTypes,
            out Vector3 world, out float yawDegrees, out int foundType)
        {
            world = Vector3.zero;
            yawDegrees = 0f;
            foundType = 0;

            EtsSiiUnit def = map.PrefabDefinition(prefab.PrefabToken);

            if (def == null || prefab.NodeUids.Length == 0)
            {
                return false;
            }

            EtsPrefabDescriptor ppd = map.Descriptor(def.GetString("prefab_desc"), out _);

            if (ppd == null || ppd.Nodes.Length == 0 || ppd.SpawnPoints.Length == 0)
            {
                return false;
            }

            EtsPrefabDescriptor.SpawnPoint point = null;

            for (int i = 0; i < wantedTypes.Length && point == null; i++)
            {
                point = ppd.FindSpawn(wantedTypes[i]);
                if (point != null) foundType = wantedTypes[i];
            }

            if (point == null || !TryPrefabFrame(map, prefab, ppd, centreGame, out Vector3 prefabOrigin, out Quaternion rotation, out _))
            {
                return false;
            }

            world = prefabOrigin + rotation * EtsModelBuilder.ToUnity(point.Position);

            // The spawn point's own facing, turned into the same frame as the prefab, as a heading
            // (what Quaternion.LookRotation(facing).eulerAngles.y gave, 0..360).
            Quaternion local = EtsModelBuilder.ToUnityRotation(point.Rotation);
            Vector3 facing = rotation * (local * Vector3.forward);

            if (facing.x * facing.x + facing.z * facing.z < 0.0001f)
            {
                facing = rotation * Vector3.forward;
            }

            yawDegrees = Mathf.Atan2(facing.x, facing.z) * Mathf.Rad2Deg;

            if (yawDegrees < 0f)
            {
                yawDegrees += 360f;
            }

            return true;
        }

        public static GameObject BuildPrefab(EtsMap map, EtsMapPrefab prefab, Options options, Transform parent, Stats stats)
        {
            EtsSiiUnit def = map.PrefabDefinition(prefab.PrefabToken);

            if (def == null || prefab.NodeUids.Length == 0)
            {
                stats.PrefabsSkipped++;
                return null;
            }

            EtsMapNode mapNode = map.FindNode(prefab.NodeUids[0]);
            EtsPrefabDescriptor ppd = map.Descriptor(def.GetString("prefab_desc"), out string ppdFailure);

            if (mapNode == null || ppd == null || ppd.Nodes.Length == 0)
            {
                if (ppd == null && stats.Notes.Count < 20) stats.Notes.Add("prefab " + prefab.Name + ": " + ppdFailure);
                stats.PrefabsSkipped++;
                return null;
            }

            EtsModelData model = EtsEngine.LoadModel(def.GetString("model_desc"), out string failure);

            if (model == null)
            {
                if (stats.Notes.Count < 20) stats.Notes.Add("prefab " + prefab.Name + ": " + failure);
                stats.PrefabsSkipped++;
                return null;
            }

            // Stood on node 0's full rotation (TryPrefabFrame). A node 0 with no usable direction
            // stands untilted at its node, as the old yaw-only code did (its angle then came out 0).
            if (!TryPrefabFrame(map, prefab, ppd, options.CenterGame, out Vector3 position, out Quaternion rotation, out bool tilted, out bool refused))
            {
                rotation = Quaternion.identity;
                position = ToWorld(mapNode.Position, options.CenterGame) - EtsModelBuilder.ToUnity(ppd.Nodes[Mathf.Clamp(prefab.Origin, 0, ppd.Nodes.Length - 1)].Position);
            }

            if (refused) PrefabsTiltRefused++;
            else if (tilted) PrefabsTilted++;
            else PrefabsLevel++;

            // The map names the variant (e.g. "clean") and switches extra parts on by name
            // (_giveroad, _midlines, _parking): those are part names, not variants.
            var parts = new string[prefab.AdditionalParts.Length];

            for (int i = 0; i < parts.Length; i++)
            {
                parts[i] = EtsToken.Decode(prefab.AdditionalParts[i]);
            }

            var built = EtsModelBuilder.Build(model, new EtsModelBuilder.Options
            {
                Look = "default",
                Variants = new[] { EtsToken.Decode(prefab.VariantToken), "default" },
                Parts = parts,
                CreateLocators = false,
                AddColliders = options.Colliders,
                IncludeCollision = false,
                GameGlass = true
            }, parent, EtsEngine.Materials);

            built.Root.name = "prefab " + prefab.Name;
            built.Root.transform.rotation = rotation;
            built.Root.transform.position = position;

            // The ground round the prefab, grown from the descriptor's border points - the run that
            // belongs to the model variant the map placed, as the descriptor keeps one per variant.
            if (options.Terrain)
            {
                int variantIndex = Mathf.Max(0, Array.IndexOf(model.Variants, EtsToken.Decode(prefab.VariantToken)));
                EtsPrefabTerrainBuilder.Build(map, prefab, ppd, variantIndex, built.Root.transform.position, rotation, options, built.Root.transform, stats);
            }

            // Traffic lights and the descriptor's lamps and signs, in the prefab's own frame under the prefab: queued
            // (EtsDetailDriver), so a garage with 105 lamps never lands in one frame. Each is guarded on its own - a
            // detail that fails costs that detail, never the junction.
            if (options.Details)
            {
                try
                {
                    EtsSemaphores.BuildPrefab(map, prefab, ppd, built.Root.transform, options);
                }
                catch (Exception e)
                {
                    EtsSemaphores.Warn("the traffic lights of prefab " + prefab.Name, e);
                }

                try
                {
                    EtsSignBuilder.BuildPrefabSigns(map, prefab, ppd, model, new[] { EtsToken.Decode(prefab.VariantToken), "default" }, parts, built.Root.transform, options);
                }
                catch (Exception e)
                {
                    EtsSemaphores.Warn("the signs of prefab " + prefab.Name, e);
                }
            }

            stats.Prefabs++;
            stats.Vertices += built.Vertices;
            return built.Root;
        }

        // ------------------------------------------------------------------ models

        public static GameObject BuildModel(EtsMap map, EtsMapModel item, Options options, Transform parent, Stats stats)
        {
            EtsSiiUnit def = map.ModelDefinition(item.ModelToken);
            EtsMapNode node = map.FindNode(item.NodeUid);

            if (def == null || node == null)
            {
                stats.ModelsSkipped++;
                return null;
            }

            // Trees. 1,413 of the 7,496 placed "models" in the nine sectors round Frankfurt have no
            // model_desc at all: their unit says vegetation_model = vegetmodel.<name>, and the
            // vegetmodel unit names the tree's high, medium and sprite models. Read as models
            // they all failed with "descriptor not found: /", and every placed tree was missing.
            EtsModelData model;
            string failure = null;

            if (def.Has("vegetation_model"))
            {
                model = EtsVegetationBuilder.Model(map, def.GetString("vegetation_model"));
                if (model == null) failure = "vegetation model " + def.GetString("vegetation_model") + " not found";
            }
            else
            {
                model = EtsEngine.LoadModel(def.GetString("model_desc"), out failure);
            }

            if (model == null)
            {
                if (stats.Notes.Count < 20) stats.Notes.Add("model " + item.Name + ": " + failure);
                stats.ModelsSkipped++;
                return null;
            }

            // The map item's ADDITIONAL PARTS were being parsed and then thrown away, so a model
            // whose detail hangs off named parts came through as a shell: factory_hall_04_it was
            // drawn as 594 of its 6,204 vertices. They are switched on by name the same way a
            // prefab's extra parts are.
            var parts = new string[item.AdditionalParts.Length];

            for (int i = 0; i < parts.Length; i++)
            {
                parts[i] = EtsToken.Decode(item.AdditionalParts[i]);
            }

            var built = EtsModelBuilder.Build(model, new EtsModelBuilder.Options
            {
                Look = EtsToken.Decode(item.LookToken),
                Variants = new[] { EtsToken.Decode(item.VariantToken), "default" },
                Parts = parts.Length > 0 ? parts : null,
                CreateLocators = false,
                AddColliders = options.Colliders,
                IncludeCollision = false,
                GameGlass = true
            }, parent, EtsEngine.Materials);

            built.Root.name = "model " + item.Name;
            built.Root.transform.position = ToWorld(node.Position, options.CenterGame);
            built.Root.transform.rotation = EtsModelBuilder.ToUnityRotation(node.Rotation);
            // The sign is kept: a component of -1 is how the map mirrors a model, and Unity flips
            // the triangle winding itself when a transform's determinant is negative. Checked for the
            // see-through buildings (2026-09-23) rather than assumed: a box with a green near face and a
            // blue far face, drawn in a throwaway Unity 6000.3 project mirrored on x and on z, plain and
            // through StaticBatchingUtility.Combine as EtsMapViewer.CombineStatic does, shows its front
            // faces in all six cases. Round the starts these are 0 (Szczecin), 2 (Cambridge) and 4 (Dover)
            // administration buildings (admin_05_ru_balt, admin_logistic_small_01_ibe; ProgramBld1).
            built.Root.transform.localScale = new Vector3(item.Scale[0], item.Scale[1], item.Scale[2]);

            if (item.Scale[0] * item.Scale[1] * item.Scale[2] < 0f)
            {
                MirroredModels++;
            }

            // Buildings, walls and roadside props are obstacles: on the Environment layer the
            // truck's and trailer's bodies collide with them, so you can no longer drive through.
            if (options.Colliders)
            {
                EtsLayers.SetRecursively(built.Root.transform, EtsLayers.Environment);

                // A ground plate is drive surface, not an obstacle: a plaza, yard or car park laid as
                // one flat piece at the model's own ground level. On Environment the wheels fell
                // through it and the ground fill, which only sees the drive surface, laid grass over
                // it (2,703 m2 of plates lidded within 150 m of the start). Flat and low only: a
                // canopy or a raised slab stays an obstacle.
                if (!def.Has("vegetation_model"))
                {
                    foreach (MeshCollider collider in built.Root.GetComponentsInChildren<MeshCollider>(true))
                    {
                        Mesh shape = collider.sharedMesh;

                        if (shape == null)
                        {
                            continue;
                        }

                        // Model space: pieces sit at identity under their part and the model root.
                        Bounds local = shape.bounds;

                        if ((local.max.y <= 0.6f || (local.size.y <= 0.5f && local.min.y <= 0.3f)) && local.size.x + local.size.z >= 2f)
                        {
                            collider.gameObject.layer = EtsLayers.Default;
                        }
                    }
                }
            }

            stats.Models++;
            stats.Vertices += built.Vertices;
            return built.Root;
        }
    }
}
