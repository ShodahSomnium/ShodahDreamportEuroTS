using System;
using System.Collections.Generic;
using UnityEngine;

namespace SomniumSpace.Worlds.SomniumEuroTruck.Ets
{
    /// <summary>
    /// The game's detail vegetation - the grass tufts, flowers and weeds it scatters on the ground - read from its own
    /// files (grass owner, 2026-09-25; environment DESIGN.md Phase C, N/wf1/vegetation.md finding 1).
    ///
    /// What the files say (measured with the world's parsers, probes N/grass/G1-G3 and N/veg/V1702, V6):
    ///  - /def/world/detail_vegetation.sii holds one unit, ".grass": density 35, min_distance 0.5, max_distance 105,
    ///    and for prefabs model_scale_tweak_coef 1.0 and density_tweak_coef 1.
    ///  - /def/world/detail_vegetation_def*.sii hold 458 detail_vegetation_def units, one per terrain BASE TEXTURE
    ///    ("texture:"), each with model[] / occurrence[] pairs and an optional density. A quad grows the def of its main
    ///    brush: terrain_mat.&lt;token&gt; -> its .mat -> texture_base -> the def with that texture.
    ///  - Every grass model round Dover and Szczecin is ONE eut2.leaves piece of 4 vertices at one point with uv set 1
    ///    the card's corner offsets in metres and uv set 2 the lock flags (0.6, 0.6 = faces the camera on both axes;
    ///    flower_03/04 (0.6, 0) stay upright): grass_green2_2 is 1.92 x 0.55 m, its centre 0.19 m up. All of them on
    ///    one atlas, /model/vegetation/grass/grass.tobj, through two materials (grass_new_mask, sc_grass_new_mask).
    ///    Seen through uv set 1 every card's two triangles run clockwise (screen area -0.25 .. -1.36 m2), so they
    ///    face the camera. Models that are not leaves (stones, "generic" parts) are left out and counted.
    ///  - The game draws them through eut2.leaves.instanced.detveg (/effect/eut2/_shd/5aa84f47...glsl.vso): the
    ///    instance's position, a scale (x ATTR14.y*2, applied to the model AND to the card), the TERRAIN NORMAL under it
    ///    as the lighting normal (ATTR14.zw), a random wind phase and an engine-supplied RGB (not in the files; left
    ///    white here). No rotation, no shadow pass (eut2.leaves, not .shadow).
    ///
    /// Placement is an integer hash of (item uid, side, quad row, quad column, card number) - never System.Random - so
    /// any quad regenerates the same cards on its own, on every client, in any order. The density's unit is not in the
    /// files; <see cref="CardsPerSquareMetre"/> reads it as cards per 100 m2 times EtsVegetationLod.DensityScale.
    ///
    /// Pure C# apart from Vector types, so the cell generator (<see cref="BuildCell"/>) runs on a worker thread; the
    /// def lookups and <see cref="Resolve"/> (which reads models) run on the main thread.
    /// </summary>
    public static class EtsDetailVegetation
    {
        /// <summary>One leaves piece of a detail model, in Unity's frame: the card's corners as the leaves shader reads them.</summary>
        public sealed class Card
        {
            public Vector3[] Positions;
            public Vector2[] Uv0;
            public Vector2[] Uv1;
            public Vector2[] Uv2;
            public Color32[] Colors;
            public int[] Indices;

            /// <summary>Index into <see cref="MaterialPaths"/> (and EtsVegetationLod's band-3 materials).</summary>
            public int Slot;

            /// <summary>The farthest the card reaches from its pivot, metres, stored offset and card push together.</summary>
            public float Reach;
        }

        /// <summary>A detail def: the models a brush's texture grows and their weights.</summary>
        public sealed class Def
        {
            public string Name;
            public string Texture;

            /// <summary>The def's own density, else the global .grass density (35).</summary>
            public float Density;

            public string[] ModelPaths = new string[0];

            /// <summary>The "|25|model" style number after a model path when there is one (meaning not in the files; kept, unused).</summary>
            public float[] ModelNumber = new float[0];

            public int[] Cumulative = new int[0];
            public int TotalWeight;

            /// <summary>Each model's cards (leaves pieces), null for a model that is not a card; set by <see cref="Resolve"/>.</summary>
            public Card[][] Cards;

            /// <summary>Set once Resolve has run (main thread); the worker only reads defs with Ready set.</summary>
            public volatile bool Ready;

            /// <summary>Resolve found no card among the models: nothing to grow.</summary>
            public bool Empty;
        }

        /// <summary>The global .grass unit's values (defaults = the demo's file).</summary>
        public static float GlobalDensity = 35f, MinDistance = 0.5f, MaxDistance = 105f, ModelScaleTweak = 1f, DensityTweak = 1f;

        /// <summary>The band across a side, metres from its inner edge, that grows detail vegetation when the map stores none (the editor's 5 .. 100; 97 % of 1,418 sides measured).</summary>
        public const float DefaultBandFrom = 5f, DefaultBandTo = 100f;

        /// <summary>Units read, and model entries that were not cards.</summary>
        public static int DefCount, NonCardModels, CardModels;

        /// <summary>Slot -> the .mat path of a card's material (grass_new_mask, sc_grass_new_mask, ...).</summary>
        public static readonly List<string> MaterialPaths = new List<string>();

        private static readonly Dictionary<string, Def> ByTexture = new Dictionary<string, Def>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<ulong, Def> ByToken = new Dictionary<ulong, Def>();
        private static readonly Dictionary<string, int> SlotByPath = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static EtsFileSystem _loadedFrom;

        public static bool Loaded => _loadedFrom != null;

        /// <summary>How a .mat is read (the material cache's parse, main thread); a harness sets its own.</summary>
        public static Func<string, EtsMaterialDef> MaterialReader = path => EtsEngine.Materials != null ? EtsEngine.Materials.GetDefinition(path) : null;

        /// <summary>How a model is read (the engine's cache, main thread); a harness sets its own.</summary>
        public static Func<string, EtsModelData> ModelReader = path => EtsEngine.LoadModel(path, out string failure);

        /// <summary>Reads .grass and every detail_vegetation_def unit (main thread, once per mount).</summary>
        public static void Load(EtsFileSystem files)
        {
            if (files == null || files == _loadedFrom)
            {
                return;
            }

            _loadedFrom = files;
            ByTexture.Clear();
            ByToken.Clear();
            MaterialPaths.Clear();
            SlotByPath.Clear();
            DefCount = NonCardModels = CardModels = 0;

            EtsSiiFile global = files.ReadSii("/def/world/detail_vegetation.sii");
            EtsSiiUnit grass = global != null ? global.Find(".grass") : null;

            if (grass == null && global != null && global.Units.Count > 0)
            {
                grass = global.Units[0];
            }

            if (grass != null)
            {
                GlobalDensity = grass.GetFloat("density", 35f);
                MinDistance = grass.GetFloat("min_distance", 0.5f);
                MaxDistance = grass.GetFloat("max_distance", 105f);
                ModelScaleTweak = grass.GetFloat("model_scale_tweak_coef", 1f);
                DensityTweak = grass.GetFloat("density_tweak_coef", 1f);
            }

            EtsSiiFile family = files.ReadDefinitionFamily("/def/world", "detail_vegetation_def");

            foreach (EtsSiiUnit unit in family.Units)
            {
                string texture = EtsSiiValue.Unquote(unit.GetString("texture", null) ?? "");

                if (texture.Length == 0)
                {
                    continue;
                }

                DefCount++;
                List<string> models = unit.GetArray("model");
                List<string> weights = unit.GetArray("occurrence");
                var def = new Def
                {
                    Name = unit.Name,
                    Texture = texture,
                    Density = unit.Has("density") ? unit.GetFloat("density", GlobalDensity) : GlobalDensity,
                    ModelPaths = new string[models.Count],
                    ModelNumber = new float[models.Count],
                    Cumulative = new int[models.Count]
                };

                for (int m = 0; m < models.Count; m++)
                {
                    // "path.pmd|25|model": field 0 is the path; a number after it is kept (its meaning is not in the files).
                    string[] fields = EtsSiiValue.Unquote(models[m]).Split('|');
                    def.ModelPaths[m] = fields[0].Trim();
                    def.ModelNumber[m] = fields.Length > 1 ? EtsSiiValue.ParseFloat(fields[1], 0f) : 0f;
                    int weight = 1;

                    if (m < weights.Count && int.TryParse(EtsSiiValue.Unquote(weights[m]).Trim(), out int parsed) && parsed > 0)
                    {
                        weight = parsed;
                    }

                    def.TotalWeight += weight;
                    def.Cumulative[m] = def.TotalWeight;
                }

                ByTexture[texture] = def;
            }
        }

        /// <summary>
        /// The def a brush token grows (main thread; cached): terrain_mat.&lt;token&gt; -> path -> the .mat's texture_base ->
        /// the def with that texture. Null for brushes with none (water, asphalt, dirt, pebbles ...).
        /// </summary>
        public static Def ForBrush(EtsMap map, ulong token)
        {
            if (token == 0 || map == null)
            {
                return null;
            }

            if (ByToken.TryGetValue(token, out Def cached))
            {
                return cached;
            }

            Def def = null;
            EtsSiiUnit unit = map.TerrainMaterial(token);
            string path = unit != null ? unit.GetString("path", null) : null;

            if (!string.IsNullOrEmpty(path))
            {
                EtsMaterialDef mat = MaterialReader(EtsSiiValue.Unquote(path));

                if (mat != null && !string.IsNullOrEmpty(mat.BaseTexture))
                {
                    ByTexture.TryGetValue(mat.BaseTexture, out def);
                }
            }

            ByToken[token] = def;
            return def;
        }

        /// <summary>
        /// Main thread: reads the def's models and takes the cards out of their leaves pieces (look 0), once. The
        /// materials' slots are numbered here; EtsVegetationLod makes the band-3 material of each slot.
        /// </summary>
        public static void Resolve(Def def)
        {
            if (def == null || def.Ready)
            {
                return;
            }

            var cards = new Card[def.ModelPaths.Length][];
            bool any = false;

            for (int m = 0; m < def.ModelPaths.Length; m++)
            {
                EtsModelData model = ModelReader(def.ModelPaths[m]);
                cards[m] = model != null ? CardsOf(model) : null;

                if (cards[m] != null)
                {
                    CardModels++;
                    any = true;
                }
                else
                {
                    NonCardModels++;
                }
            }

            def.Cards = cards;
            def.Empty = !any;
            def.Ready = true;
        }

        /// <summary>The leaves pieces of a model as cards (null when it has none). Pure.</summary>
        public static Card[] CardsOf(EtsModelData model)
        {
            var result = new List<Card>();
            string[] look = model.LookMaterials.Length > 0 ? model.LookMaterials[0] : new string[0];

            foreach (EtsModelPiece piece in model.Pieces)
            {
                string matPath = piece.MaterialIndex < look.Length ? look[piece.MaterialIndex] : null;
                EtsMaterialDef mat = matPath != null ? MaterialReader(matPath) : null;

                if (!EtsModelBuilder.IsLeaves(mat) || piece.VertexCount == 0 || piece.Indices == null || piece.Indices.Length < 3)
                {
                    continue;
                }

                int n = piece.VertexCount;
                var card = new Card
                {
                    Positions = new Vector3[n],
                    Uv0 = new Vector2[n],
                    Uv1 = new Vector2[n],
                    Uv2 = new Vector2[n],
                    Colors = new Color32[n],
                    Indices = (int[])piece.Indices.Clone(),
                    Slot = SlotOf(matPath)
                };

                float[][] sets = piece.Uvs ?? new float[0][];

                for (int v = 0; v < n; v++)
                {
                    // The game's frame to Unity's: z turned round (as EtsModelBuilder.LeavesMeshStreams).
                    card.Positions[v] = new Vector3(piece.Positions[v * 3], piece.Positions[v * 3 + 1], -piece.Positions[v * 3 + 2]);
                    if (sets.Length > 0) card.Uv0[v] = new Vector2(sets[0][v * 2], sets[0][v * 2 + 1]);
                    if (sets.Length > 1) card.Uv1[v] = new Vector2(sets[1][v * 2], sets[1][v * 2 + 1]);
                    if (sets.Length > 2) card.Uv2[v] = new Vector2(sets[2][v * 2], sets[2][v * 2 + 1]);
                    card.Colors[v] = piece.Colors != null && piece.Colors.Length >= v * 4 + 4
                        ? new Color32(piece.Colors[v * 4], piece.Colors[v * 4 + 1], piece.Colors[v * 4 + 2], piece.Colors[v * 4 + 3])
                        : new Color32(128, 128, 128, 128);

                    float x = card.Uv2[v].x >= 0.5f ? card.Uv1[v].x : 0f;
                    float y = card.Uv2[v].y >= 0.5f ? card.Uv1[v].y : 0f;
                    card.Reach = Mathf.Max(card.Reach, card.Positions[v].magnitude + Mathf.Sqrt(x * x + y * y));
                }

                result.Add(card);
            }

            return result.Count > 0 ? result.ToArray() : null;
        }

        private static int SlotOf(string matPath)
        {
            string key = "/" + EtsHashFs.Normalize(matPath);

            if (!SlotByPath.TryGetValue(key, out int slot))
            {
                slot = MaterialPaths.Count;
                MaterialPaths.Add(key);
                SlotByPath[key] = slot;
            }

            return slot;
        }

        /// <summary>
        /// Cards per square metre on ground of a def's density: the unit is not in the files, read here as cards per
        /// 100 m2 (35 -> 0.35 / m2) times <paramref name="scale"/> (EtsVegetationLod.DensityScale 2.5, calibrated against
        /// the game's own photographs in the render lab: 0.875 cards a square metre, one per 1.14 m2).
        /// </summary>
        public static float CardsPerSquareMetre(Def def, float scale)
        {
            return def == null ? 0f : def.Density / 100f * scale;
        }

        // ------------------------------------------------------------------ the integer hash

        /// <summary>Murmur3's finaliser: a full-avalanche 32-bit mix.</summary>
        public static uint Mix(uint h)
        {
            h ^= h >> 16;
            h *= 0x85EBCA6Bu;
            h ^= h >> 13;
            h *= 0xC2B2AE35u;
            h ^= h >> 16;
            return h;
        }

        /// <summary>The seed of one side's quads: the item's uid and the side (0 right, 1 left; a prefab's corner index; 0 a patch).</summary>
        public static uint SideSeed(ulong uid, int side)
        {
            return Mix((uint)uid ^ Mix((uint)(uid >> 32) ^ Mix((uint)side * 0x9E3779B9u + 0x7F4A7C15u)));
        }

        /// <summary>A number in [0, 1) from the side's seed, the quad (row, column) and a draw index. Same on every machine.</summary>
        public static float Hash01(uint seed, int row, int col, int draw)
        {
            uint h = Mix(seed ^ Mix((uint)row * 0x27D4EB2Fu ^ Mix((uint)col * 0x165667B1u ^ Mix((uint)draw * 0x9E3779B1u + 0x61C88647u))));
            return (h >> 8) * (1f / 16777216f);
        }

        // ------------------------------------------------------------------ one side's quad grid (built on the main thread, read by the worker)

        /// <summary>
        /// One terrain side's grid as the worker reads it: world-space vertices (row-major, row i across the side, column
        /// j along it), the def of each quad (or -1), the band and the lateral position of each row for the band test.
        /// Never changed after it is registered.
        /// </summary>
        public sealed class Patch
        {
            public int Id;
            public Vector3[] Vertices;
            public int Rows, Cols, Stride;

            /// <summary>Index into <see cref="Defs"/> per quad (i * Cols + j), -1 for none.</summary>
            public sbyte[] QuadDef;

            public Def[] Defs;
            public uint Seed;

            /// <summary>Metres from the side's inner edge of each row line (Rows + 1 values), or null (no band test: a bezier patch).</summary>
            public float[] Lateral;

            public float BandFrom, BandTo;
            public Vector2 Min, Max;

            /// <summary>Quads with a def, and those left out by the quad bit, the side flag and the band (for the census).</summary>
            public int Capable, SkippedQuadBit, SkippedSideFlag, SkippedBand, Kept;
        }

        private static int _nextPatchId = 1;

        /// <summary>
        /// A side's grid as the worker reads it (main thread: the def lookups read .mat files once per brush), or null when
        /// no quad on it can grow grass; the counts are filled either way (<paramref name="counts"/>). A quad grows
        /// nothing when its main brush has no def, its byte 3 has the no-detail bit (0x10), its side's detail flag is off
        /// or its row lies wholly outside the side's detail band (partly outside: each card is tested). <paramref
        /// name="vertices"/> are world positions; <paramref name="quadAt"/> maps grid quad (row, column) to the stored
        /// quad's index (null: column-major col * Rows + row, the file's order); <paramref name="side"/> may be null (a
        /// bezier patch: no flag, no band). Pure apart from the def lookups.
        /// </summary>
        public static Patch MakePatch(EtsMap map, EtsTerrainQuadData quads, Vector3[] vertices, Matrix4x4 toWorld, int rows, int cols, int stride, ulong uid, int sideIndex,
            EtsTerrainSide side, IList<float> lateral, Func<int, int, int> quadAt, out Patch counts)
        {
            counts = new Patch();

            if (quads == null || quads.Rows <= 0 || quads.Cols <= 0 || vertices == null || rows <= 0 || cols <= 0 || vertices.Length < (rows + 1) * stride)
            {
                return null;
            }

            var defs = new List<Def>();
            var quadDef = new sbyte[rows * cols];
            bool sideOn = side == null || side.DetailVegetation;
            float bandFrom = side != null && side.DetailBandFrom >= 0f ? side.DetailBandFrom : DefaultBandFrom;
            float bandTo = side != null && side.DetailBandTo >= 0f ? side.DetailBandTo : DefaultBandTo;
            bool band = lateral != null && lateral.Count >= rows + 1;

            for (int i = 0; i < rows; i++)
            {
                for (int j = 0; j < cols; j++)
                {
                    quadDef[i * cols + j] = -1;
                    int stored = quadAt != null ? quadAt(i, j) : (i < quads.Rows && j < quads.Cols ? j * quads.Rows + i : -1);

                    if (stored < 0 || stored * 4 + 3 >= quads.Quads.Length)
                    {
                        continue;
                    }

                    int slot = quads.Quads[stored * 4] & 15;
                    Def def = ForBrush(map, slot < quads.MaterialTokens.Length ? quads.MaterialTokens[slot] : 0);

                    if (def == null)
                    {
                        continue;
                    }

                    counts.Capable++;

                    if ((quads.Quads[stored * 4 + 3] & 0x10) != 0)
                    {
                        counts.SkippedQuadBit++;
                        continue;
                    }

                    if (!sideOn)
                    {
                        counts.SkippedSideFlag++;
                        continue;
                    }

                    if (band && (lateral[i + 1] < bandFrom || lateral[i] > bandTo))
                    {
                        counts.SkippedBand++;
                        continue;
                    }

                    int index = defs.IndexOf(def);

                    if (index < 0)
                    {
                        if (defs.Count >= 127)
                        {
                            continue;
                        }

                        index = defs.Count;
                        defs.Add(def);
                    }

                    quadDef[i * cols + j] = (sbyte)index;
                    counts.Kept++;
                }
            }

            if (counts.Kept == 0)
            {
                return null;
            }

            var min = new Vector2(float.MaxValue, float.MaxValue);
            var max = new Vector2(float.MinValue, float.MinValue);
            int count = (rows + 1) * stride;
            var world = new Vector3[count];

            for (int k = 0; k < count; k++)
            {
                Vector3 p = toWorld.MultiplyPoint3x4(vertices[k]);
                world[k] = p;

                if (!float.IsNaN(p.x) && !float.IsNaN(p.z) && !float.IsInfinity(p.x) && !float.IsInfinity(p.z))
                {
                    min = Vector2.Min(min, new Vector2(p.x, p.z));
                    max = Vector2.Max(max, new Vector2(p.x, p.z));
                }
            }

            float[] lateralCopy = null;

            if (band)
            {
                lateralCopy = new float[rows + 1];

                for (int i = 0; i <= rows; i++)
                {
                    lateralCopy[i] = lateral[i];
                }
            }

            return new Patch
            {
                Id = _nextPatchId++,
                Vertices = world,
                Rows = rows,
                Cols = cols,
                Stride = stride,
                QuadDef = quadDef,
                Defs = defs.ToArray(),
                Seed = SideSeed(uid, sideIndex),
                Lateral = lateralCopy,
                BandFrom = bandFrom,
                BandTo = bandTo,
                Min = min,
                Max = max,
                Capable = counts.Capable,
                SkippedQuadBit = counts.SkippedQuadBit,
                SkippedSideFlag = counts.SkippedSideFlag,
                SkippedBand = counts.SkippedBand,
                Kept = counts.Kept
            };
        }

        // ------------------------------------------------------------------ the cell generator (worker thread)

        /// <summary>One material's vertex streams of a cell.</summary>
        public sealed class SlotMesh
        {
            public int Slot;
            public int Cards;
            public Vector3[] Positions;
            public Vector3[] Normals;
            public Color32[] Colors;
            public Vector2[] Uv0, Uv1, Uv2;
            public Vector4[] Uv3;
            public int[] Indices;
            public Bounds Bounds;
        }

        /// <summary>What a cell job hands back.</summary>
        public sealed class CellResult
        {
            public long Key;
            public int Version;
            public int Job;
            public int Cards;
            public int CardsInBand;
            public int Thinned;
            public double Milliseconds;
            public readonly List<SlotMesh> Slots = new List<SlotMesh>();
        }

        private sealed class SlotBuilder
        {
            public int Slot, Cards;
            public readonly List<Vector3> Positions = new List<Vector3>();
            public readonly List<Vector3> Normals = new List<Vector3>();
            public readonly List<Color32> Colors = new List<Color32>();
            public readonly List<Vector2> Uv0 = new List<Vector2>(), Uv1 = new List<Vector2>(), Uv2 = new List<Vector2>();
            public readonly List<Vector4> Uv3 = new List<Vector4>();
            public readonly List<int> Indices = new List<int>();
            public Vector3 Min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue), Max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            public float Reach;
        }

        /// <summary>
        /// Every card whose pivot falls in the square cell [minX, minX + size) x [minZ, minZ + size): for each quad of each
        /// patch that overlaps it, n = floor(area x density + h0) cards at hashed points on the quad's two triangles (the
        /// terrain mesh's own diagonal, so they stand on the drawn surface), each with its model drawn by occurrence, a
        /// scale of 0.8-1.2, and the triangle's normal as its lighting normal. <paramref name="keep"/> below 1 keeps that
        /// hashed share (the card cap). Positions are made relative to <paramref name="origin"/> (the cells' root). Pure.
        /// </summary>
        public static CellResult BuildCell(long key, float minX, float minZ, float size, Patch[] patches, float densityScale, float keep, Vector3 origin)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var result = new CellResult { Key = key };
            var builders = new List<SlotBuilder>();
            float maxX = minX + size, maxZ = minZ + size;
            float scaleTweak = ModelScaleTweak > 0f ? ModelScaleTweak : 1f;

            foreach (Patch patch in patches)
            {
                if (patch.Max.x < minX || patch.Min.x >= maxX || patch.Max.y < minZ || patch.Min.y >= maxZ)
                {
                    continue;
                }

                Vector3[] v = patch.Vertices;
                int stride = patch.Stride;

                for (int i = 0; i < patch.Rows; i++)
                {
                    for (int j = 0; j < patch.Cols; j++)
                    {
                        int defIndex = patch.QuadDef[i * patch.Cols + j];

                        if (defIndex < 0)
                        {
                            continue;
                        }

                        Def def = patch.Defs[defIndex];

                        if (def == null || !def.Ready || def.Empty)
                        {
                            continue;
                        }

                        Vector3 a = v[i * stride + j], b = v[i * stride + j + 1], c = v[(i + 1) * stride + j], d = v[(i + 1) * stride + j + 1];
                        float qMinX = Mathf.Min(Mathf.Min(a.x, b.x), Mathf.Min(c.x, d.x)), qMaxX = Mathf.Max(Mathf.Max(a.x, b.x), Mathf.Max(c.x, d.x));
                        float qMinZ = Mathf.Min(Mathf.Min(a.z, b.z), Mathf.Min(c.z, d.z)), qMaxZ = Mathf.Max(Mathf.Max(a.z, b.z), Mathf.Max(c.z, d.z));

                        if (qMaxX < minX || qMinX >= maxX || qMaxZ < minZ || qMinZ >= maxZ)
                        {
                            continue;
                        }

                        // The terrain mesh's two triangles: (a, b, c) and (b, d, c), the b-c diagonal (EmitQuads).
                        Vector3 n1 = Vector3.Cross(b - a, c - a), n2 = Vector3.Cross(c - d, b - d);
                        float area = 0.5f * (n1.magnitude + n2.magnitude);

                        if (!(area > 0.01f) || float.IsInfinity(area))
                        {
                            continue;
                        }

                        n1 = n1.y < 0f ? -n1.normalized : n1.normalized;
                        n2 = n2.y < 0f ? -n2.normalized : n2.normalized;

                        float count = area * CardsPerSquareMetre(def, densityScale) + Hash01(patch.Seed, i, j, 0);

                        if (count > 4096f)
                        {
                            count = 4096f;
                        }

                        int cards = (int)count;

                        for (int k = 0; k < cards; k++)
                        {
                            int draw = k * 8;
                            float u = Hash01(patch.Seed, i, j, draw + 1), w = Hash01(patch.Seed, i, j, draw + 2);
                            bool first = u + w <= 1f;
                            Vector3 point = first ? a + u * (b - a) + w * (c - a) : d + (1f - u) * (c - d) + (1f - w) * (b - d);

                            if (point.x < minX || point.x >= maxX || point.z < minZ || point.z >= maxZ)
                            {
                                continue;
                            }

                            // The side's detail band, by the row lines' distance from the inner edge.
                            if (patch.Lateral != null)
                            {
                                float lateral = Mathf.Lerp(patch.Lateral[i], patch.Lateral[i + 1], w);

                                if (lateral < patch.BandFrom || lateral > patch.BandTo)
                                {
                                    result.CardsInBand++;
                                    continue;
                                }
                            }

                            if (keep < 1f && Hash01(patch.Seed, i, j, draw + 6) >= keep)
                            {
                                result.Thinned++;
                                continue;
                            }

                            float roll = Hash01(patch.Seed, i, j, draw + 3) * def.TotalWeight;
                            int pick = 0;

                            while (pick < def.Cumulative.Length - 1 && roll >= def.Cumulative[pick])
                            {
                                pick++;
                            }

                            Card[] modelCards = def.Cards[pick];

                            if (modelCards == null)
                            {
                                continue; // a stone or a generic part: not drawn (counted at Resolve)
                            }

                            float scale = (0.8f + 0.4f * Hash01(patch.Seed, i, j, draw + 4)) * scaleTweak;
                            float phase = Hash01(patch.Seed, i, j, draw + 5);
                            Vector3 normal = first ? n1 : n2;
                            Vector3 pivot = point - origin;

                            foreach (Card card in modelCards)
                            {
                                SlotBuilder sb = null;

                                for (int s = 0; s < builders.Count; s++)
                                {
                                    if (builders[s].Slot == card.Slot)
                                    {
                                        sb = builders[s];
                                        break;
                                    }
                                }

                                if (sb == null)
                                {
                                    sb = new SlotBuilder { Slot = card.Slot };
                                    builders.Add(sb);
                                }

                                int baseVertex = sb.Positions.Count;
                                var uv3 = new Vector4(pivot.x, pivot.y, pivot.z, phase);

                                for (int q = 0; q < card.Positions.Length; q++)
                                {
                                    sb.Positions.Add(pivot + card.Positions[q] * scale);
                                    sb.Normals.Add(normal);
                                    sb.Colors.Add(card.Colors[q]);
                                    sb.Uv0.Add(card.Uv0[q]);
                                    sb.Uv1.Add(card.Uv1[q] * scale);
                                    sb.Uv2.Add(card.Uv2[q]);
                                    sb.Uv3.Add(uv3);
                                }

                                for (int q = 0; q < card.Indices.Length; q++)
                                {
                                    sb.Indices.Add(baseVertex + card.Indices[q]);
                                }

                                sb.Min = Vector3.Min(sb.Min, pivot);
                                sb.Max = Vector3.Max(sb.Max, pivot);
                                sb.Reach = Mathf.Max(sb.Reach, card.Reach * scale);
                                sb.Cards++;
                            }

                            result.Cards++;
                        }
                    }
                }
            }

            foreach (SlotBuilder sb in builders)
            {
                if (sb.Cards == 0)
                {
                    continue;
                }

                // Drawn bounds: the pivots grown by the farthest card corner (the push turns with the camera, so on
                // every axis) and the wind (EtsModelBuilder.LeavesWindMargin).
                float grow = sb.Reach * 1.04f + EtsModelBuilder.LeavesWindMargin;
                var bounds = new Bounds((sb.Min + sb.Max) * 0.5f, sb.Max - sb.Min + new Vector3(2f * grow, 2f * grow, 2f * grow));

                result.Slots.Add(new SlotMesh
                {
                    Slot = sb.Slot,
                    Cards = sb.Cards,
                    Positions = sb.Positions.ToArray(),
                    Normals = sb.Normals.ToArray(),
                    Colors = sb.Colors.ToArray(),
                    Uv0 = sb.Uv0.ToArray(),
                    Uv1 = sb.Uv1.ToArray(),
                    Uv2 = sb.Uv2.ToArray(),
                    Uv3 = sb.Uv3.ToArray(),
                    Indices = sb.Indices.ToArray(),
                    Bounds = bounds
                });
            }

            result.Milliseconds = clock.Elapsed.TotalMilliseconds;
            return result;
        }
    }
}
