using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace SomniumSpace.Worlds.SomniumEuroTruck.Ets
{
    /// <summary>
    /// Builds Unity objects out of a parsed model: one child per drawn piece, one empty child per
    /// locator, in Unity's frame.
    ///
    /// The frame is the one decision here that everything else depends on. The game is
    /// right-handed with Y up; Unity is left-handed with Y up. The conversion is a mirror on Z:
    /// positions and normals negate z, a rotation (x, y, z, w) becomes (-x, -y, z, w), which is
    /// what a mirror does to the axis of a rotation and its sense, and the tangent's handedness
    /// sign flips. Triangle order is kept: the game winds front faces clockwise in its
    /// right-handed frame, and the mirror alone turns that into Unity's clockwise-front (see
    /// <see cref="BuildMesh"/> for the measurement). Texture coordinates are kept as they are: a
    /// DDS stores its top row first, and Direct3D and Unity's raw upload both put the first row
    /// at v = 0, so the game's v addresses the same row here (see <see cref="ToUv"/>). Every
    /// consumer - models, locators, and later the map's nodes - goes through the same functions
    /// so they cannot disagree.
    /// </summary>
    public static class EtsModelBuilder
    {
        /// <summary>What to show of a model.</summary>
        public sealed class Options
        {
            /// <summary>Look name; the first look when not found.</summary>
            public string Look = "default";

            /// <summary>Variants whose parts are shown, together. A truck is a chassis variant
            /// plus a cabin variant of the same model.</summary>
            public string[] Variants = { "default" };

            /// <summary>Parts shown by name whatever the variant says: a prefab's additional
            /// parts (_giveroad, _midlines, ...) are enabled this way by the map.</summary>
            public string[] Parts;

            /// <summary>
            /// When set, ONLY parts with these names are drawn and the variants are ignored. A
            /// fence model holds start, center and end side by side and the map repeats one of
            /// them along a run, so the builder has to be able to ask for a single part.
            /// </summary>
            public string[] OnlyParts;

            /// <summary>Show parts named "coll" - the collision hulls. Off for display.</summary>
            public bool IncludeCollision;

            /// <summary>Put a mesh collider on every drawn piece.</summary>
            public bool AddColliders;

            /// <summary>Create an empty child transform for every locator.</summary>
            public bool CreateLocators = true;

            /// <summary>Renderers cast shadows.</summary>
            public bool CastShadows = true;

            /// <summary>
            /// Draw eut2.glass as the game does (EtsMaterialCache.WithGameGlass: a third of what is behind let
            /// through, not 85 %). For map items - buildings, props, parked cars. Off for trucks and traffic, whose
            /// exterior model carries inward-facing glass that the seated driver looks through.
            /// </summary>
            public bool GameGlass;
        }

        /// <summary>What a build produced, for reporting.</summary>
        public sealed class Result
        {
            public GameObject Root;
            public int PiecesDrawn;
            public int PiecesSkipped;
            public int Vertices;
            public int Triangles;
            public readonly Dictionary<string, Transform> Locators = new Dictionary<string, Transform>(StringComparer.Ordinal);
        }

        public static Vector3 ToUnity(float x, float y, float z)
        {
            return new Vector3(x, y, -z);
        }

        public static Vector3 ToUnity(float[] xyz)
        {
            return new Vector3(xyz[0], xyz[1], -xyz[2]);
        }

        /// <summary>Rotation stored as x, y, z, w in the game's frame.</summary>
        public static Quaternion ToUnity(float x, float y, float z, float w)
        {
            var q = new Quaternion(-x, -y, z, w);
            return q.normalized;
        }

        public static Quaternion ToUnityRotation(float[] xyzw)
        {
            return ToUnity(xyzw[0], xyzw[1], xyzw[2], xyzw[3]);
        }

        /// <summary>
        /// A uv set, unchanged. This used to flip v "because the game counts rows from the top",
        /// which double-counted: a DDS does store its top row first, but Direct3D puts that first
        /// row at v = 0 and so does Unity's raw upload, so the same v reads the same row in both.
        /// Measured on the models within 500 m of the bay: of 1380 upright pieces with a v range,
        /// v shrinks with height on 593 and grows on 165; on the advert boards (posters with
        /// writing, decoded upright with the first row at the top) the correlation is -1.0, v = 0
        /// at the top of the board. Flipped, every poster, sign and dial hung upside down.
        /// </summary>
        private static Vector2[] ToUv(float[] set, int count)
        {
            var uv = new Vector2[count];

            for (int v = 0; v < count; v++)
            {
                uv[v] = new Vector2(set[v * 2], set[v * 2 + 1]);
            }

            return uv;
        }

        public static Result Build(EtsModelData model, Options options, Transform parent, EtsMaterialCache materials)
        {
            options = options ?? new Options();
            var result = new Result();

            string name = model.Path ?? "model";
            int slash = name.LastIndexOf('/');
            var root = new GameObject(slash < 0 ? name : name.Substring(slash + 1));
            root.transform.SetParent(parent, false);
            result.Root = root;

            int look = Mathf.Max(0, model.LookIndex(options.Look));
            var variants = new List<int>();

            foreach (string variant in options.Variants ?? new string[0])
            {
                int index = model.VariantIndex(variant.Trim());

                if (index >= 0)
                {
                    variants.Add(index);
                }
            }

            // No named variant matched. Showing nothing would read as a broken model; show the
            // first variant, which is what the game's own viewer defaults to.
            if (variants.Count == 0 && model.Variants.Length > 0)
            {
                variants.Add(0);
            }

            string[] lookMaterials = look < model.LookMaterials.Length ? model.LookMaterials[look] : new string[0];

            foreach (EtsModelPart part in model.Parts)
            {
                bool visible = variants.Count == 0;

                foreach (int variant in variants)
                {
                    visible |= part.IsVisible(variant);
                }

                if (options.Parts != null)
                {
                    foreach (string forced in options.Parts)
                    {
                        visible |= forced == part.Name;
                    }
                }

                if (options.OnlyParts != null)
                {
                    visible = false;

                    foreach (string wanted in options.OnlyParts)
                    {
                        visible |= wanted == part.Name;
                    }
                }

                if (!visible)
                {
                    continue;
                }

                bool collision = part.Name == "coll";

                if (collision && !options.IncludeCollision)
                {
                    continue;
                }

                var partObject = new GameObject(part.Name ?? "part");
                partObject.transform.SetParent(root.transform, false);

                for (int p = part.PieceStart; p < part.PieceStart + part.PieceCount && p < model.Pieces.Length; p++)
                {
                    EtsModelPiece piece = model.Pieces[p];
                    string matPath = piece.MaterialIndex < lookMaterials.Length ? lookMaterials[piece.MaterialIndex] : null;
                    EtsMaterialDef def = materials?.GetDefinition(matPath);

                    // The "weight.weight" pieces are NOT skirts to be left out: in the DomDepo prefab
                    // they are the whole yard - 141 x 116 m of ground, asphalt_parklot_base blended
                    // with its variants and grass_city1 with gravel by vertex alpha. Skipping them
                    // (an earlier guess) left a collider with nothing drawn on it, so the yard was a
                    // hole showing the fog-coloured background: the pale cyan patches. They draw
                    // through the two-layer shader; see EtsMaterialCache.SecondLayer.
                    Material material = collision ? null : materials?.Load(matPath);

                    if (options.GameGlass && material != null && materials != null)
                    {
                        material = materials.WithGameGlass(material, def);
                    }

                    // Leaves (every tree, bush and grass card: IsLeaves) are camera-facing
                    // cards whose size is in uv set 1 and whose green, blue and alpha are wind and fade data.
                    bool leaves = IsLeaves(def);

                    // The game multiplies the diffuse colour by the vertex colour x 2
                    // (/effect/eut2/_shd/30a5626a4b2701c1ec1f7fcb719381da.glsl.fso: tex * diffuse *
                    // cg_FrontColor.xyz * 2); URP's Lit has no vertex colour input at all. Most
                    // pieces sit at 128 (x1), but some are baked much darker - the grey rocks round
                    // the start average 79 (x0.62), the parking-lot ground 71, a metal-plate hall 51
                    // (harness Program967). The piece's MEAN is applied as a tint: it loses the
                    // variation across the piece (baked AO) but not the overall level. A leaves piece
                    // on the leaves shader gets no tint (WithVertexColour leaves any shader but Lit
                    // alone): the shader reads each vertex's red itself. On Lit (the leaves shader
                    // missing) only the red is a colour, as the game's leaves program has it
                    // (value0347 = (r, r, r, 1), then x 2): buk_tree_1_med's mean rgb was folded in as
                    // (0.70, 0.47, 1.19), a purple cast, where the game draws 0.70 grey (Program1705).
                    // Map pieces whose colours VARY (baked AO: darker corners, eaves, undersides) go to the two-layer
                    // shader's plain mode, which reads each vertex's colour x 2 as the game does, when their material is
                    // one it draws the same (EtsMaterialCache.WithVertexAo); the mean tint below then leaves them alone.
                    // Not the texgen (.tg0) meshes: they carry no colours.
                    if (material != null && materials != null && piece.Colors != null && !leaves && options.GameGlass &&
                        (def == null || !def.EffectHas(".tg0")) && VertexColoursVary(piece))
                    {
                        Material ao = materials.WithVertexAo(material, def);

                        if (ao != null)
                        {
                            material = ao;
                            EtsMaterialCache.VertexAoPieces++;
                        }
                    }

                    if (material != null && materials != null && piece.Colors != null)
                    {
                        material = materials.WithVertexColour(material, leaves ? LeavesTint(piece) : MeanVertexColour(piece));
                    }

                    // A piece drawn for light alone - a shadow caster, a building's lit windows - is
                    // never solid in the game: its collision lives in dedicated parts ("coll" on all 37
                    // prefab models and 78 of the 152 map models round the start; startcol / centercol
                    // / endcol on the run models, e.g. conc_barrier, hedge, rail_redwhite, whose
                    // shadow pieces are "eut2.shadowonly" twins of the drawn ones; harness
                    // Program1452/1453). Given a collider anyway it was a second, invisible solid, and
                    // wherever the shadow mesh reaches past the drawn one an invisible wall: within 60 m
                    // of the 2026-09-22 drive, 4,264 of the 10,847 collider samples that stand over a
                    // lane or a prefab's driving surface came from shadow-only pieces (Program1454),
                    // and the client's census found colliders with nothing drawn over them (model 269,
                    // "piece 5 [no renderer]"). The "eut2.none" collision material is not render-only
                    // and keeps its collider.
                    bool renderOnly = def != null && def.Effect != null &&
                                      (def.EffectHas("shadowonly") || def.EffectHas("shadowmap") || def.EffectHas("fakeshadow") ||
                                       def.EffectHas(".night") || def.EffectHas("mlaaweight"));

                    if (material == null && (!options.AddColliders || renderOnly))
                    {
                        result.PiecesSkipped++;
                        continue;
                    }

                    // The effect decides which uv set the base texture is read through, and
                    // Unity's Lit reads the first, so the right set is copied into the first.
                    int baseUv = EtsMaterialCache.BaseUvChannel(def, piece.UvChannels);

                    // "tg0" / "tg1" effects make their uvs from the vertex POSITION in the game's
                    // shader and never read the mesh's own set (see BuildTexgenMesh). Leaves carry all
                    // three sets and bounds grown by their cards (BuildLeavesMesh).
                    Bounds storedBounds;
                    Mesh mesh;

                    if (leaves)
                    {
                        mesh = BuildLeavesMesh(piece, "piece " + p, out storedBounds);
                    }
                    else
                    {
                        mesh = def != null && def.EffectHas(".tg0") ? BuildTexgenMesh(piece, "piece " + p, def) : BuildMesh(piece, "piece " + p, baseUv);
                        storedBounds = mesh.bounds;
                    }

                    var pieceObject = new GameObject(collision ? "coll " + p : "piece " + p);
                    pieceObject.transform.SetParent(partObject.transform, false);

                    var filter = pieceObject.AddComponent<MeshFilter>();
                    filter.sharedMesh = mesh;

                    if (material != null)
                    {
                        // Leaves cast a shadow only where the game's effect is the ".shadow" variant: trees
                        // yes, grass cards no (LeavesCastShadow). The builder's own switch
                        // still wins: the scattered plants are built with CastShadows off.
                        bool shadow = options.CastShadows && (!leaves || LeavesCastShadow(def));
                        var renderer = pieceObject.AddComponent<MeshRenderer>();
                        renderer.sharedMaterial = material;
                        renderer.shadowCastingMode = shadow ? ShadowCastingMode.On : ShadowCastingMode.Off;
                        result.PiecesDrawn++;
                        result.Vertices += piece.VertexCount;
                        result.Triangles += piece.Indices.Length / 3;
                    }
                    else
                    {
                        result.PiecesSkipped++;
                    }

                    // A collider on every piece with a triangle in it and some size to it. The old test
                    // also wanted 4 vertices and 12 indices; that predates CollisionMesh (below), which
                    // drops the triangles PhysX cannot clean and returns null when none are left. All
                    // the index test still did was take the collider off every ground plate modelled
                    // as ONE quad: /model/plaza/pavement_plaza1.pmd (40 x 50 m, 4 vertices, 6 indices)
                    // and /model/plaza/grass_square.pmd beside the Szczecin start were drawn with
                    // nothing under them - 386 + 122 census cells you could see and fall through.
                    // Measured on the stored geometry, not on a leaves mesh's grown drawing bounds.
                    Bounds meshBounds = storedBounds;
                    bool solid = piece.Indices.Length >= 3 && !renderOnly &&
                                 meshBounds.size.x + meshBounds.size.y + meshBounds.size.z > 0.3f;

                    if (options.AddColliders && solid)
                    {
                        // Through the same cleaner the ground uses: one degenerate triangle makes
                        // PhysX refuse the whole mesh, and the piece then has no collision at all
                        // rather than a little less.
                        Mesh cooked = EtsTerrainBuilder.CollisionMesh(mesh);

                        // CollisionMesh hands back the mesh itself when no triangle is degenerate, and a
                        // leaves mesh carries its grown drawing bounds. EtsMapBuilder.BuildModel's
                        // ground-plate test reads the collider mesh's bounds, so with the drawn mesh 7 flat
                        // leaves pieces on map models within 20 km of the start (reeds_cattails_big_1:
                        // 20.1 x 0.27 x 21.0 m stored, 0.24 m high; dung_pile_big2; a fallen tree) turned
                        // from drive surface into obstacles (checker probe 1937). The collider gets the
                        // stored geometry's own bounds, as it had before the leaves meshes.
                        if (leaves && cooked == mesh)
                        {
                            cooked = LeavesColliderMesh(piece, mesh);
                        }

                        if (cooked != null)
                        {
                            var collider = pieceObject.AddComponent<MeshCollider>();
                            collider.sharedMesh = cooked;
                        }
                    }
                }

                if (options.CreateLocators)
                {
                    for (int l = part.LocatorStart; l < part.LocatorStart + part.LocatorCount && l < model.Locators.Length; l++)
                    {
                        EtsModelLocator locator = model.Locators[l];
                        var locatorObject = new GameObject("loc:" + locator.Name);
                        locatorObject.transform.SetParent(partObject.transform, false);
                        locatorObject.transform.localPosition = ToUnity(locator.Position);
                        locatorObject.transform.localRotation = ToUnityRotation(locator.Rotation);
                        locatorObject.transform.localScale = Vector3.one * (locator.Scale > 0f ? locator.Scale : 1f);

                        if (!result.Locators.ContainsKey(locator.Name))
                        {
                            result.Locators[locator.Name] = locatorObject.transform;
                        }
                    }
                }
            }

            return result;
        }

        /// <summary>One piece as a Unity mesh, in Unity's frame.</summary>
        /// <param name="baseUvChannel">Which of the piece's uv sets becomes the mesh's first uv,
        /// the one the Lit shader reads its base texture through. See
        /// <see cref="EtsMaterialCache.BaseUvChannel"/>.</param>
        private static readonly Dictionary<EtsModelPiece, Color> MeanColours = new Dictionary<EtsModelPiece, Color>();

        private static readonly Dictionary<EtsModelPiece, bool> ColoursVary = new Dictionary<EtsModelPiece, bool>();

        /// <summary>
        /// Whether a piece's vertex colours carry shading worth drawing per vertex: their grey (r + g + b) / 3 spans at least
        /// 24 of 255 (x0.19 of the game's multiplier), and none brightens past 144 (x1.125; the two-layer shader clamps the
        /// multiplier at 1, where the mean tint could go to 2).
        /// </summary>
        private static bool VertexColoursVary(EtsModelPiece piece)
        {
            if (ColoursVary.TryGetValue(piece, out bool cached))
            {
                return cached;
            }

            int n = Mathf.Min(piece.VertexCount, piece.Colors.Length / 4);
            int lo = 255, hi = 0;

            for (int v = 0; v < n; v++)
            {
                int grey = (piece.Colors[v * 4] + piece.Colors[v * 4 + 1] + piece.Colors[v * 4 + 2]) / 3;
                if (grey < lo) lo = grey;
                if (grey > hi) hi = grey;
            }

            bool vary = n > 0 && hi - lo >= 24 && hi <= 144;
            ColoursVary[piece] = vary;
            return vary;
        }

        /// <summary>A piece's mean vertex colour as the game's multiplier (byte / 128), linear.</summary>
        private static Color MeanVertexColour(EtsModelPiece piece)
        {
            if (MeanColours.TryGetValue(piece, out Color cached))
            {
                return cached;
            }

            double r = 0, g = 0, b = 0;
            int n = Mathf.Min(piece.VertexCount, piece.Colors.Length / 4);

            for (int v = 0; v < n; v++)
            {
                r += piece.Colors[v * 4];
                g += piece.Colors[v * 4 + 1];
                b += piece.Colors[v * 4 + 2];
            }

            Color mean = n == 0 ? Color.white : new Color((float)(r / n / 128.0), (float)(g / n / 128.0), (float)(b / n / 128.0), 1f);
            MeanColours[piece] = mean;
            return mean;
        }

        /// <summary>
        /// Whether a material is one of the game's leaves effects (EtsMaterialCache gives these the leaves
        /// shader, and this builder the leaves mesh). Measured over the 4,768 models the demo's 2,660
        /// vegetation_model units name (harness Program1923): 5,113 of their 5,457 pieces use
        /// eut2.leaves.tsnmapcalc.shadow (2,426), eut2.leaves.shadow (2,308), eut2.leaves.tsnmapcalc (248)
        /// or eut2.leaves (131); the rest are trunks and props on ordinary effects. No vegetation piece uses
        /// the older eut2.grass, so it is left out rather than guessed into the same vertex layout. Here
        /// rather than in the material cache so the harness can call it: that class's static property ids
        /// need the engine.
        /// </summary>
        public static bool IsLeaves(EtsMaterialDef def)
        {
            return def != null && def.Effect != null && def.EffectHas("eut2.leaves");
        }

        /// <summary>Whether a leaves material casts a shadow in the game: its effect is the ".shadow" variant (trees; grass cards are not).</summary>
        public static bool LeavesCastShadow(EtsMaterialDef def)
        {
            return IsLeaves(def) && def.EffectHas(".shadow");
        }

        /// <summary>
        /// The tint a leaves piece takes on URP Lit (the leaves shader missing): the mean of the vertex
        /// colour's RED only, as a grey, byte / 128. The game's leaves vertex program passes (r, r, r, 1) on
        /// as the colour and the fragment multiplies it by 2 (value0347 in
        /// /effect/eut2/_shd/ef447134a6749386441b43ba1987cd28.glsl.vso; cg_FrontColor.xyz * 2 in d98ecb96...);
        /// green, blue and alpha are fade flags, wind strength, phase and wiggle. Pure, for the harness.
        /// </summary>
        public static Color LeavesTint(EtsModelPiece piece)
        {
            if (piece.Colors == null)
            {
                return Color.white;
            }

            double r = 0;
            int n = Mathf.Min(piece.VertexCount, piece.Colors.Length / 4);

            for (int v = 0; v < n; v++)
            {
                r += piece.Colors[v * 4];
            }

            float grey = n == 0 ? 1f : (float)(r / n / 128.0);
            return new Color(grey, grey, grey, 1f);
        }

        /// <summary>
        /// How much more than the wind the drawing bounds of a leaves mesh are grown, metres in the model's
        /// own units: the shader sways a card by up to 1.5 (the vertex colour's strength) times
        /// EtsVegetationLook.WindAmplitude (0.12 m) sideways and 1.5 x 0.1 m up and down, so 0.33 m at the
        /// defaults; 0.5 m leaves room to raise the amplitude to 0.25 m.
        /// </summary>
        public const float LeavesWindMargin = 0.5f;

        /// <summary>A leaves piece's vertex streams, in Unity's frame, ready for a Mesh. Pure (no engine calls).</summary>
        public sealed class LeavesMeshData
        {
            public Vector3[] Positions;
            public Vector3[] Normals;
            public Vector4[] Tangents;

            /// <summary>uv 0: the texture atlas (the game's set 0).</summary>
            public Vector2[] Uv0;

            /// <summary>uv 1: the card's size in metres, x along the camera's right and y along its up (the game's set 1).</summary>
            public Vector2[] Uv1;

            /// <summary>uv 2: the lock flags, x and y each 0 or 0.6 - an axis faces the camera where it is 0.5 or more (the game's set 2).</summary>
            public Vector2[] Uv2;

            public Color32[] Colors;
            public int[] Indices;

            /// <summary>The stored geometry's bounds; for a sprite LOD a strip 1 cm wide.</summary>
            public Bounds Stored;

            /// <summary>The drawing bounds: Stored grown on every axis by the farthest a card corner can be pushed, plus the wind.</summary>
            public Bounds Drawn;

            /// <summary>The farthest any vertex is pushed by its card, metres: |lock x size| over the vertices.</summary>
            public float CardReach;
        }

        /// <summary>
        /// A leaves piece's streams as the leaves shader reads them. Every set goes to the GPU as the game
        /// sends it (its vertex program reads tex_coord_0, _1 and _2 as ATTR8, 9 and 10): the atlas, the card
        /// size and the lock flags. BuildMesh writes only two sets, so the lock flags never reached the GPU
        /// and the cards could not be expanded. A piece without a set gets zeros there (lock 0: drawn as
        /// stored, no card), and one without colours gets 128 in every channel (x1 colour, no wind).
        ///
        /// The drawing bounds are grown by the farthest a card can reach, on every axis: the push is along the
        /// camera's horizontal right and its up, and looking down the up axis leans toward the horizontal, so
        /// the size along y can land on x or z too. Without it Unity culls a sprite LOD - stored as a strip
        /// 1 cm wide (buk_tree_1_sprite: 0.106 x 13.8 m side-on, probe 1705) - while its 12 m card is on screen.
        /// Also 2 % for the game's wiggle (wiggle_limit = 0.02 x the card offset) and LeavesWindMargin. The wiggle
        /// has two terms on the vertical axis (_wiggle_offset.y += _wiggle_limit.y * cos 2x), so up to 4 % there;
        /// the margin covers the rest: checker probe 1936 pushed all 944,436 leaves vertices of the demo's
        /// vegetation models through the shader's exact formula at object scales 0.85-1.15 and found none
        /// outside these bounds at wind 0.12 or 0.25 m (at the 0.5 m slider maximum, 4 vertices by up to 0.11 m).
        /// </summary>
        public static LeavesMeshData LeavesMeshStreams(EtsModelPiece piece)
        {
            int count = piece.VertexCount;
            var data = new LeavesMeshData
            {
                Positions = new Vector3[count],
                Uv0 = new Vector2[count],
                Uv1 = new Vector2[count],
                Uv2 = new Vector2[count],
                Colors = new Color32[count],
                Indices = new int[piece.Indices.Length]
            };

            Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 max = new Vector3(float.MinValue, float.MinValue, float.MinValue);

            for (int v = 0; v < count; v++)
            {
                var position = new Vector3(piece.Positions[v * 3], piece.Positions[v * 3 + 1], -piece.Positions[v * 3 + 2]);
                data.Positions[v] = position;
                min = Vector3.Min(min, position);
                max = Vector3.Max(max, position);
            }

            if (count == 0)
            {
                min = max = Vector3.zero;
            }

            if (piece.Normals != null)
            {
                data.Normals = new Vector3[count];

                for (int v = 0; v < count; v++)
                {
                    data.Normals[v] = new Vector3(piece.Normals[v * 3], piece.Normals[v * 3 + 1], -piece.Normals[v * 3 + 2]);
                }
            }

            if (piece.Tangents != null)
            {
                data.Tangents = new Vector4[count];

                for (int v = 0; v < count; v++)
                {
                    data.Tangents[v] = new Vector4(piece.Tangents[v * 4], piece.Tangents[v * 4 + 1], -piece.Tangents[v * 4 + 2], -piece.Tangents[v * 4 + 3]);
                }
            }

            float[][] sets = piece.Uvs ?? new float[0][];
            float reach = 0f;

            for (int v = 0; v < count; v++)
            {
                if (sets.Length > 0) data.Uv0[v] = new Vector2(sets[0][v * 2], sets[0][v * 2 + 1]);
                if (sets.Length > 1) data.Uv1[v] = new Vector2(sets[1][v * 2], sets[1][v * 2 + 1]);
                if (sets.Length > 2) data.Uv2[v] = new Vector2(sets[2][v * 2], sets[2][v * 2 + 1]);

                float x = data.Uv2[v].x >= 0.5f ? data.Uv1[v].x : 0f;
                float y = data.Uv2[v].y >= 0.5f ? data.Uv1[v].y : 0f;
                reach = Mathf.Max(reach, Mathf.Sqrt(x * x + y * y));

                data.Colors[v] = piece.Colors != null && piece.Colors.Length >= v * 4 + 4
                    ? new Color32(piece.Colors[v * 4], piece.Colors[v * 4 + 1], piece.Colors[v * 4 + 2], piece.Colors[v * 4 + 3])
                    : new Color32(128, 128, 128, 128);
            }

            Array.Copy(piece.Indices, data.Indices, data.Indices.Length);

            float grow = reach * 1.02f + LeavesWindMargin;
            data.CardReach = reach;
            data.Stored = new Bounds((min + max) * 0.5f, max - min);
            data.Drawn = new Bounds((min + max) * 0.5f, max - min + new Vector3(2f * grow, 2f * grow, 2f * grow));
            return data;
        }

        /// <summary>The leaves meshes handed out so far, one per piece, and their stored-geometry bounds.</summary>
        private static readonly Dictionary<EtsModelPiece, KeyValuePair<Mesh, Bounds>> LeavesMeshCache = new Dictionary<EtsModelPiece, KeyValuePair<Mesh, Bounds>>();

        /// <summary>
        /// A leaves piece as a Unity mesh (LeavesMeshStreams): uv sets 0, 1 and 2, the colours, and the grown
        /// bounds set explicitly after the triangles (setting triangles recalculates them). Once per piece,
        /// shared by every instance, and in SharedMeshes so the stream's unload never destroys it.
        /// </summary>
        public static Mesh BuildLeavesMesh(EtsModelPiece piece, string name, out Bounds stored)
        {
            if (LeavesMeshCache.TryGetValue(piece, out KeyValuePair<Mesh, Bounds> cached) && cached.Key != null)
            {
                stored = cached.Value;
                return cached.Key;
            }

            LeavesMeshData data = LeavesMeshStreams(piece);
            var mesh = new Mesh { name = name + " leaves" };

            if (data.Positions.Length > 65535)
            {
                mesh.indexFormat = IndexFormat.UInt32;
            }

            mesh.vertices = data.Positions;

            if (data.Normals != null)
            {
                mesh.normals = data.Normals;
            }

            if (data.Tangents != null)
            {
                mesh.tangents = data.Tangents;
            }

            mesh.SetUVs(0, data.Uv0);
            mesh.SetUVs(1, data.Uv1);
            mesh.SetUVs(2, data.Uv2);
            mesh.colors32 = data.Colors;
            mesh.triangles = data.Indices;

            if (data.Normals == null)
            {
                mesh.RecalculateNormals();
            }

            mesh.bounds = data.Drawn;
            LeavesMeshCache[piece] = new KeyValuePair<Mesh, Bounds>(mesh, data.Stored);
            SharedMeshes.Add(mesh);
            stored = data.Stored;
            return mesh;
        }

        /// <summary>The collider copies of leaves meshes, one per piece.</summary>
        private static readonly Dictionary<EtsModelPiece, Mesh> LeavesColliderCache = new Dictionary<EtsModelPiece, Mesh>();

        /// <summary>
        /// A leaves piece's collider mesh: the drawn mesh's positions and triangles with bounds recalculated from
        /// them - the stored geometry's bounds, which is what a collider carried before leaves meshes had grown
        /// drawing bounds (BuildMesh recalculates them the same way). Once per piece, shared by every instance.
        /// </summary>
        private static Mesh LeavesColliderMesh(EtsModelPiece piece, Mesh drawn)
        {
            if (LeavesColliderCache.TryGetValue(piece, out Mesh cached) && cached != null)
            {
                return cached;
            }

            var mesh = new Mesh { name = drawn.name + " collision", indexFormat = drawn.indexFormat };
            mesh.vertices = drawn.vertices;
            mesh.triangles = drawn.triangles;
            mesh.RecalculateBounds();
            LeavesColliderCache[piece] = mesh;
            SharedMeshes.Add(mesh);
            return mesh;
        }

        private static readonly Dictionary<EtsModelPiece, Dictionary<string, Mesh>> TexgenMeshCache = new Dictionary<EtsModelPiece, Dictionary<string, Mesh>>();

        /// <summary>
        /// A piece whose material generates its texture coordinates. The game's vertex program
        /// for eut2.dif.spec.weight.tg0 (/effect/eut2/_shd/970f0d18a277d2a92992bf8902aa82d3.glsl.vso)
        /// sets the base texture's uv to (dot(position, texgen_0_gen_x), dot(position,
        /// texgen_0_gen_y)) from the OBJECT-space position and ignores the stored uv set - and the
        /// stored sets do not agree with it (harness Program975): /model/plaza/grassy_ground.pmd
        /// stores 10 m per repeat against texgen 13, the TransiNet tradeaux yard 10.6 against 5,
        /// hotel_b 17.3 against 5, the junction asphalt of road1_x_road1_t_sidewalk_all_4m
        /// anything from 0.7 to 20 m against 5. 21 % of the model and prefab area round the start
        /// is a tg0 effect. The plane is taken as the ground plane (x, z), rotated by
        /// texgen_0_rot degrees, at texgen_0_gen metres per repeat; the exact plane vectors the
        /// engine derives from those two numbers are not in the files, and for a tiling ground
        /// texture only scale and rotation show.
        /// </summary>
        public static Mesh BuildTexgenMesh(EtsModelPiece piece, string name, EtsMaterialDef def)
        {
            float[] gen0 = def.GetFloats("texgen_0_gen");

            if (gen0.Length < 2 || gen0[0] <= 0.01f || gen0[1] <= 0.01f)
            {
                return BuildMesh(piece, name, 0);
            }

            float rot0 = def.GetFloat("texgen_0_rot", 0f);
            float[] gen1 = def.EffectHas(".tg1") ? def.GetFloats("texgen_1_gen") : new float[0];
            float rot1 = def.GetFloat("texgen_1_rot", 0f);
            string key = gen0[0] + "," + gen0[1] + "," + rot0 + (gen1.Length >= 2 ? "|" + gen1[0] + "," + gen1[1] + "," + rot1 : "");

            if (!TexgenMeshCache.TryGetValue(piece, out Dictionary<string, Mesh> byKey))
            {
                byKey = new Dictionary<string, Mesh>();
                TexgenMeshCache[piece] = byKey;
            }

            if (byKey.TryGetValue(key, out Mesh cached) && cached != null)
            {
                return cached;
            }

            Mesh mesh = UnityEngine.Object.Instantiate(BuildMesh(piece, name, 0));
            mesh.name = name + " texgen";
            mesh.uv = Texgen(piece, gen0[0], gen0[1], rot0);

            if (gen1.Length >= 2 && gen1[0] > 0.01f && gen1[1] > 0.01f)
            {
                mesh.uv2 = Texgen(piece, gen1[0], gen1[1], rot1);
            }

            byKey[key] = mesh;
            SharedMeshes.Add(mesh);
            return mesh;
        }

        private static Vector2[] Texgen(EtsModelPiece piece, float sizeU, float sizeV, float degrees)
        {
            float radians = degrees * Mathf.Deg2Rad;
            float cos = Mathf.Cos(radians), sin = Mathf.Sin(radians);
            var uv = new Vector2[piece.VertexCount];

            for (int v = 0; v < piece.VertexCount; v++)
            {
                // The game's own frame, before the Z mirror, as the game's shader sees it.
                float x = piece.Positions[v * 3], z = piece.Positions[v * 3 + 2];
                uv[v] = new Vector2((x * cos - z * sin) / sizeU, (x * sin + z * cos) / sizeV);
            }

            return uv;
        }

        /// <summary>Meshes once per piece and uv choice: every birch on the map is the same mesh, and Unity batches and instances it.</summary>
        private static readonly Dictionary<EtsModelPiece, Mesh[]> MeshCache = new Dictionary<EtsModelPiece, Mesh[]>();

        // Every mesh the two caches above hand out, shared by every instance of the model. The map
        // stream must never destroy one of these when it unloads an item (EtsMapViewer): it used to,
        // and every other tree of that species, every other copy of that junction, still in view,
        // stopped drawing and lost its collider (which is the same mesh when nothing needed cleaning).
        private static readonly HashSet<Mesh> SharedMeshes = new HashSet<Mesh>();

        /// <summary>Whether a mesh belongs to the model caches and is shared between instances (so not to be destroyed).</summary>
        public static bool IsShared(Mesh mesh)
        {
            return mesh != null && SharedMeshes.Contains(mesh);
        }

        public static Mesh BuildMesh(EtsModelPiece piece, string name, int baseUvChannel = 0)
        {
            if (!MeshCache.TryGetValue(piece, out Mesh[] cached))
            {
                cached = new Mesh[4];
                MeshCache[piece] = cached;
            }

            int cacheSlot = Mathf.Clamp(baseUvChannel, 0, 3);

            if (cached[cacheSlot] != null)
            {
                return cached[cacheSlot];
            }

            int count = piece.VertexCount;
            var mesh = new Mesh { name = name };

            if (count > 65535)
            {
                mesh.indexFormat = IndexFormat.UInt32;
            }

            var vertices = new Vector3[count];

            for (int v = 0; v < count; v++)
            {
                vertices[v] = new Vector3(piece.Positions[v * 3], piece.Positions[v * 3 + 1], -piece.Positions[v * 3 + 2]);
            }

            mesh.vertices = vertices;

            if (piece.Normals != null)
            {
                var normals = new Vector3[count];

                for (int v = 0; v < count; v++)
                {
                    normals[v] = new Vector3(piece.Normals[v * 3], piece.Normals[v * 3 + 1], -piece.Normals[v * 3 + 2]);
                }

                mesh.normals = normals;
            }

            float[][] sets = piece.Uvs ?? new float[0][];

            if (sets.Length > 0)
            {
                int primary = Mathf.Clamp(baseUvChannel, 0, sets.Length - 1);
                mesh.uv = ToUv(sets[primary], count);

                // The other set is the second uv: the two-layer shader reads its second texture
                // through it (mult, iamod, amod chains and the weight.weight ground). A piece with
                // one set gets that set twice, so a second texture still lands somewhere sane.
                int secondary = primary == 0 ? 1 : 0;
                mesh.uv2 = ToUv(sets[secondary < sets.Length ? secondary : primary], count);
            }

            if (piece.Colors != null)
            {
                var colors = new Color32[count];

                for (int v = 0; v < count; v++)
                {
                    colors[v] = new Color32(piece.Colors[v * 4], piece.Colors[v * 4 + 1], piece.Colors[v * 4 + 2], piece.Colors[v * 4 + 3]);
                }

                mesh.colors32 = colors;
            }

            if (piece.Tangents != null)
            {
                var tangents = new Vector4[count];

                for (int v = 0; v < count; v++)
                {
                    // Mirrored along with everything else; the handedness sign flips with it.
                    tangents[v] = new Vector4(piece.Tangents[v * 4], piece.Tangents[v * 4 + 1], -piece.Tangents[v * 4 + 2], -piece.Tangents[v * 4 + 3]);
                }

                mesh.tangents = tangents;
            }

            // Winding kept as stored, and this was measured rather than reasoned: across the DAF's
            // pieces, 0 of 200 sampled triangles have a right-hand-rule cross product along their
            // stored normal, so the game winds its front faces clockwise in its right-handed
            // frame, the Direct3D way. A Z mirror turns that into clockwise in Unity's
            // left-handed frame, which is Unity's front face - so the mirror alone is the whole
            // conversion. Reversing the indices as well, as the first build did, rendered every
            // panel inside out and made the cabin look hollow.
            var indices = new int[piece.Indices.Length];
            Array.Copy(piece.Indices, indices, indices.Length);
            mesh.triangles = indices;

            if (piece.Normals == null)
            {
                mesh.RecalculateNormals();
            }

            mesh.RecalculateBounds();
            cached[cacheSlot] = mesh;
            SharedMeshes.Add(mesh);
            return mesh;
        }
    }
}
