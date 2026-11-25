using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace SomniumSpace.Worlds.SomniumEuroTruck.Ets
{
    /// <summary>
    /// Where nothing may grow: every road's carriageway (both carriageways and the median of a divided road, its
    /// shoulders, kerbs, sidewalks and edge models) and every prefab's lanes (nav curves widened to a lane and its
    /// shoulder) and parking/trailer/unloading spots, as 2D footprints with the road's height along them - from the
    /// MAP DATA, never from what has been built (2026-10-23, "no trees on roads"; README).
    ///
    /// Why: a tree band belongs to one road side or terrain item and is planted into that side's strip, but another
    /// road can run through the strip - the other carriageway of a motorway built as a separate road item, a slip road,
    /// a flyover, a junction prefab. The only guard was EtsVegetationBuilder's down ray, which keeps a plant off a
    /// hard surface only when (a) that road's collider has ALREADY been built when the side plants (a big road is
    /// built to 2150 m, a small one to 1200 m, the trees plant to 2150 m - and either can stream in first), (b) the
    /// road is the top hit within 3 m above the plant's ground (a flyover deck 6 m up, or a carriageway in a cutting
    /// under the side's strip, never counted). Probe DevTools/work/trees/probe (README) lists what that let through.
    ///
    /// Every road and prefab of the demo has every node in its own sector (64,580 roads, 16,681 prefabs, probe p1), and
    /// none reaches more than 449 m out of its sector's square (kdop, probe p1). So a sector's footprints are made from
    /// that sector alone (<see cref="Compute"/>, pure, on a worker thread), and a point is fully covered once every
    /// sector within <see cref="SectorReach"/> of it has been read and computed (<see cref="Registry.Ensure"/>) - the
    /// planting waits for that, so the order things stream in no longer matters.
    ///
    /// Widths, as EtsMapBuilder.BuildRoad builds the road (same look values): a template look is offset + the template
    /// model's x extent per side (template_left on the left of a divided look), plus the edge model hung outside it
    /// (shoulder 1 m, sidewalk 3 m: the edge model's own x extent); a legacy look is road_offset + road_size + shoulder_size
    /// + the 1 m every legacy road owns (+ the sidewalk on a city road). A prefab lane is its nav curve +- <see
    /// cref="PrefabLaneHalfWidth"/>. A tree is tested with <see cref="TrunkMargin"/> more all round (no trunk touches the asphalt), a grass card with <see cref="GrassMargin"/>.
    ///
    /// Height: a plant is kept out when the surface over or under its foot lies between <see cref="ClearBelow"/> below the
    /// foot (a road in a tunnel, or a cutting far below, leaves the ground over it free) and <see cref="ClearAbove"/>
    /// above it (a viaduct deck higher than a tree leaves the ground under it free). Coordinates: the "U-frame", Unity's
    /// axes on the game's origin: x = game x, y = game y, z = -game z.
    /// </summary>
    public static class EtsRoadClearance
    {
        /// <summary>Roads and prefabs reach at most 449 m out of their sector's square (probe p1, all 282 sectors): read this far.</summary>
        public const float SectorReach = 500f;

        /// <summary>Metres added round every footprint: a trunk and its lowest branches clear of the asphalt edge.</summary>
        public static float TrunkMargin = 1.0f;

        /// <summary>Metres added round every footprint for a grass card: the verge grows up to the shoulder's edge.</summary>
        public static float GrassMargin = 0.25f;

        /// <summary>The largest margin a caller may ask for (the grid is grown by it).</summary>
        public const float MaxMargin = 2f;

        /// <summary>
        /// Half the width a prefab nav curve stands for: half a lane (legacy looks: road_size 4.5 m a lane, look0) and
        /// a shoulder metre - the descriptor has the lanes' centre lines, not their widths.
        /// </summary>
        public static float PrefabLaneHalfWidth = 3.25f;

        /// <summary>Radius kept clear round a prefab spawn point (parking, trailer, unloading, fuel): a truck and trailer's half length.</summary>
        public static float SpawnRadius = 9f;

        /// <summary>A road this far or more BELOW a plant's foot does not keep it out (a tunnel, ground over a cutting).</summary>
        public static float ClearBelow = 8f;

        /// <summary>A deck this far or more ABOVE a plant's foot does not keep it out (a tree fits under a viaduct this high).</summary>
        public static float ClearAbove = 22f;

        public const byte KindRoad = 1, KindPrefabLane = 2, KindPrefabSpot = 3;

        /// <summary>One sector's footprints: segments (a to b, widths left and right of a->b, heights), a 32 m grid over them.</summary>
        public sealed class SectorShapes
        {
            public int SectorX, SectorZ;
            public int Count;
            public float[] Ax, Ay, Az, Bx, By, Bz, Left, Right;
            public ulong[] Uid;
            public byte[] Kind;
            public float MinX, MinZ, MaxX, MaxZ;
            public const float CellSize = 32f;
            public int GridW, GridH;
            public int[] CellStart, CellItems;
            public int Roads, Prefabs, Skipped;
            public double Milliseconds;

            /// <summary>Whether (x, z) in the U-frame lies within this sector's footprint bounds at all.</summary>
            public bool Covers(float x, float z)
            {
                return Count > 0 && x >= MinX && x <= MaxX && z >= MinZ && z <= MaxZ;
            }
        }

        /// <summary>What kept a plant out.</summary>
        public struct Hit
        {
            public ulong Uid;
            public byte Kind;
            public float SurfaceY;
        }

        // ------------------------------------------------------------------ definitions (one worker thread, or a probe)

        /// <summary>The widths and prefab shapes the sectors' items name, read once each from the game's own files.</summary>
        public sealed class Defs
        {
            private readonly EtsFileSystem _files;
            private readonly object _gate = new object();
            private EtsSiiFile _looks, _edges, _prefabs;
            private readonly Dictionary<ulong, float[]> _lookWidths = new Dictionary<ulong, float[]>();
            private readonly Dictionary<ulong, float> _edgeWidths = new Dictionary<ulong, float>();
            private readonly Dictionary<string, float[]> _bounds = new Dictionary<string, float[]>(StringComparer.Ordinal);
            private readonly Dictionary<ulong, PrefabShape> _prefabShapes = new Dictionary<ulong, PrefabShape>();

            public Defs(EtsFileSystem files)
            {
                _files = files;
            }

            /// <summary>A prefab's lanes and spots in its descriptor's (game) frame, and its nodes for standing it on the map.</summary>
            public sealed class PrefabShape
            {
                public float[][] NodePosition, NodeDirection;
                public List<float[]> Lanes = new List<float[]>(); // x, y, z per point
                public List<float[]> Spots = new List<float[]>(); // x, y, z
            }

            /// <summary>
            /// Half widths [right, left] of a road look, the way BuildRoad lays it: a template slab (+ offset when divided) or the
            /// legacy bands. Edges come on top (<see cref="EdgeWidth"/>). [2] is 0 for a legacy look, 1 for a template look, 2 for a divided template look.
            /// </summary>
            public float[] LookWidths(ulong token)
            {
                lock (_gate)
                {
                    if (_lookWidths.TryGetValue(token, out float[] cached))
                    {
                        return cached;
                    }

                    if (_looks == null)
                    {
                        _looks = _files.ReadDefinitionFamily("/def/world", "road_look");
                    }

                    EtsSiiUnit look = _looks.Find("road." + EtsToken.Decode(token));
                    float[] result = null;

                    if (look != null && look.Has("template_right"))
                    {
                        float[] right = Bounds(look.GetString("template_right"));

                        if (right != null)
                        {
                            float offset = look.GetFloat("road_offset", 0f);
                            bool divided = offset > 0.01f || look.Has("template_left");
                            float half = divided ? offset + right[3] : Math.Max(-right[0], right[3]);
                            float left = half;

                            if (divided)
                            {
                                float[] l = look.Has("template_left") ? Bounds(look.GetString("template_left")) : right;

                                if (l != null)
                                {
                                    left = offset + l[3];
                                }
                            }

                            result = new[] { half, left, divided ? 2f : 1f };
                        }
                    }

                    if (result == null)
                    {
                        float offset = look != null ? look.GetFloat("road_offset", 0f) : 0f;
                        float left = offset + (look != null ? look.GetFloat("road_size_left", 4.5f) + look.GetFloat("shoulder_size_left", 0f) : 4.5f);
                        float right = offset + (look != null ? look.GetFloat("road_size_right", 4.5f) + look.GetFloat("shoulder_size_right", 0f) : 4.5f);
                        result = new[] { right + 1f, left + 1f, 0f }; // + EtsMapBuilder.LegacyEdgeBand
                    }

                    _lookWidths[token] = result;
                    return result;
                }
            }

            /// <summary>How far an edge model (road_edge.&lt;token&gt;: shoulder, kerb, sidewalk) reaches out of the slab, metres.</summary>
            public float EdgeWidth(ulong token)
            {
                if (token == 0)
                {
                    return 0f;
                }

                lock (_gate)
                {
                    if (_edgeWidths.TryGetValue(token, out float cached))
                    {
                        return cached;
                    }

                    if (_edges == null)
                    {
                        _edges = _files.ReadDefinitionFamily("/def/world", "road_edge");
                    }

                    EtsSiiUnit edge = _edges.Find("road_edge." + EtsToken.Decode(token));
                    float width = 0f;

                    if (edge != null && edge.Has("model"))
                    {
                        float[] b = Bounds(edge.GetString("model"));

                        if (b != null)
                        {
                            width = Math.Min(6f, Math.Max(0f, Math.Max(-b[0], b[3])));
                        }
                    }

                    _edgeWidths[token] = width;
                    return width;
                }
            }

            /// <summary>A model's bounding box (min x y z, max x y z) from its .pmg header (bytes 48-71, as EtsModelData reads it), or null.</summary>
            private float[] Bounds(string pmdPath)
            {
                if (string.IsNullOrEmpty(pmdPath))
                {
                    return null;
                }

                string key = "/" + EtsHashFs.Normalize(pmdPath);

                if (_bounds.TryGetValue(key, out float[] cached))
                {
                    return cached;
                }

                string pmg = key.EndsWith(".pmd", StringComparison.OrdinalIgnoreCase) ? key.Substring(0, key.Length - 4) + ".pmg" : key + ".pmg";
                byte[] b = _files.Read(pmg);
                float[] result = null;

                if (b != null && b.Length >= 72)
                {
                    result = new float[6];

                    for (int i = 0; i < 6; i++)
                    {
                        result[i] = BitConverter.ToSingle(b, 48 + i * 4);
                    }
                }

                _bounds[key] = result;
                return result;
            }

            /// <summary>The prefab's lanes and spots (from its .ppd: nav curves and spawn points), or null when it has no descriptor.</summary>
            public PrefabShape Prefab(ulong token)
            {
                lock (_gate)
                {
                    if (_prefabShapes.TryGetValue(token, out PrefabShape cached))
                    {
                        return cached;
                    }

                    if (_prefabs == null)
                    {
                        _prefabs = _files.ReadDefinitionFamily("/def/world", "prefab");
                    }

                    EtsSiiUnit def = _prefabs.Find("prefab." + EtsToken.Decode(token));
                    string path = def != null ? def.GetString("prefab_desc", null) : null;
                    PrefabShape shape = null;
                    byte[] bytes = string.IsNullOrEmpty(path) ? null : _files.Read(path);

                    if (bytes != null)
                    {
                        try
                        {
                            EtsPrefabDescriptor ppd = EtsPrefabDescriptor.Parse(bytes);
                            EtsPrefabNav nav = EtsPrefabNav.Parse(bytes, out string failure);
                            shape = new PrefabShape
                            {
                                NodePosition = new float[ppd.Nodes.Length][],
                                NodeDirection = new float[ppd.Nodes.Length][]
                            };

                            for (int i = 0; i < ppd.Nodes.Length; i++)
                            {
                                shape.NodePosition[i] = ppd.Nodes[i].Position;
                                shape.NodeDirection[i] = ppd.Nodes[i].Direction;
                            }

                            if (nav != null)
                            {
                                foreach (EtsPrefabNav.Curve c in nav.Curves)
                                {
                                    shape.Lanes.Add(CurvePoints(c));
                                }
                            }

                            foreach (EtsPrefabDescriptor.SpawnPoint s in ppd.SpawnPoints)
                            {
                                if (IsPavedSpot(s.Type))
                                {
                                    shape.Spots.Add(s.Position);
                                }
                            }
                        }
                        catch (Exception)
                        {
                            shape = null;
                        }
                    }

                    _prefabShapes[token] = shape;
                    return shape;
                }
            }

            /// <summary>
            /// Spawn point types that stand on a paved spot (TruckLib SpawnPointType, EtsPrefabDescriptor's constants): trailer 1,
            /// unload 2/19/20/21/23, fuel 3, service 4, truck stop 5, weigh station 6/22, parking 10, company 13, garage 14,
            /// trailer spawn 24, long trailer 25. Left out: dealer, hotel, recruitment, custom, camera points and the rest.
            /// </summary>
            private static bool IsPavedSpot(int type)
            {
                switch (type)
                {
                    case 1: case 2: case 3: case 4: case 5: case 6: case 10: case 13: case 14:
                    case 19: case 20: case 21: case 22: case 23: case 24: case 25:
                        return true;
                    default:
                        return false;
                }
            }

            /// <summary>A nav curve as points (x, y, z, descriptor frame), the Hermite EtsPrefabNav.Polyline draws, one point per ~6 m.</summary>
            private static float[] CurvePoints(EtsPrefabNav.Curve curve)
            {
                float dx = curve.End[0] - curve.Start[0], dy = curve.End[1] - curve.Start[1], dz = curve.End[2] - curve.Start[2];
                float chord = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
                int segments = Math.Max(1, Math.Min(24, (int)Math.Ceiling(chord / 6f)));
                var points = new float[(segments + 1) * 3];

                for (int i = 0; i <= segments; i++)
                {
                    float u = i / (float)segments, u2 = u * u, u3 = u2 * u;
                    float h00 = 2 * u3 - 3 * u2 + 1, h10 = u3 - 2 * u2 + u, h01 = -2 * u3 + 3 * u2, h11 = u3 - u2;

                    for (int k = 0; k < 3; k++)
                    {
                        points[i * 3 + k] = h00 * curve.Start[k] + h10 * curve.StartForward[k] * chord + h01 * curve.End[k] + h11 * curve.EndForward[k] * chord;
                    }
                }

                return points;
            }
        }

        // ------------------------------------------------------------------ one sector (pure)

        private sealed class Builder
        {
            public readonly List<float> Ax = new List<float>(), Ay = new List<float>(), Az = new List<float>(), Bx = new List<float>(), By = new List<float>(), Bz = new List<float>();
            public readonly List<float> Left = new List<float>(), Right = new List<float>();
            public readonly List<ulong> Uid = new List<ulong>();
            public readonly List<byte> Kind = new List<byte>();

            public void Add(float ax, float ay, float az, float bx, float by, float bz, float left, float right, ulong uid, byte kind)
            {
                Ax.Add(ax); Ay.Add(ay); Az.Add(az); Bx.Add(bx); By.Add(by); Bz.Add(bz);
                Left.Add(left); Right.Add(right); Uid.Add(uid); Kind.Add(kind);
            }
        }

        /// <summary>A game-frame position into the U-frame.</summary>
        private static void U(float[] game, out float x, out float y, out float z)
        {
            x = game[0];
            y = game[1];
            z = -game[2];
        }

        /// <summary>Every road's and prefab's footprint of one sector, from that sector's own items and nodes. Pure; any thread.</summary>
        public static SectorShapes Compute(Defs defs, EtsMapSector sector)
        {
            var clock = Stopwatch.StartNew();
            var shapes = new SectorShapes { SectorX = sector.SectorX, SectorZ = sector.SectorZ };
            var b = new Builder();
            const float m = 0f; // margins are the caller's (Blocked), so trees and grass can keep different distances

            foreach (EtsMapRoad road in sector.Roads)
            {
                if (!sector.NodesByUid.TryGetValue(road.StartNode, out EtsMapNode start) || !sector.NodesByUid.TryGetValue(road.EndNode, out EtsMapNode end))
                {
                    shapes.Skipped++;
                    continue;
                }

                float[] w = defs.LookWidths(road.LookToken);
                float right = w[0], left = w[1];

                if (w[2] > 0.5f)
                {
                    // A template road's edge models hang outside the slab (BuildRoad: the right edge, and the outer left).
                    bool divided = w[2] > 1.5f;
                    right += defs.EdgeWidth(road.RightEdgeToken);
                    left += defs.EdgeWidth(divided && road.LeftOuterEdgeToken != 0 ? road.LeftOuterEdgeToken : road.LeftEdgeToken);
                }
                else if (road.IsCityRoad)
                {
                    right += road.Right.SidewalkSize;
                    left += road.Left.SidewalkSize;
                }

                AddRoad(b, road, start, end, right + m, left + m);
                shapes.Roads++;
            }

            foreach (EtsMapPrefab prefab in sector.Prefabs)
            {
                if (prefab.NodeUids.Length == 0 || !sector.NodesByUid.TryGetValue(prefab.NodeUids[0], out EtsMapNode node))
                {
                    shapes.Skipped++;
                    continue;
                }

                Defs.PrefabShape shape = defs.Prefab(prefab.PrefabToken);

                if (shape == null || shape.NodePosition.Length == 0 || !AddPrefab(b, prefab, node, shape, m))
                {
                    shapes.Skipped++;
                    continue;
                }

                shapes.Prefabs++;
            }

            Seal(shapes, b);
            shapes.Milliseconds = clock.Elapsed.TotalMilliseconds;
            return shapes;
        }

        /// <summary>The road's curve (EtsMapBuilder.Hermite over the nodes, tangents forward x length) cut into ~8 m segments.</summary>
        private static void AddRoad(Builder b, EtsMapRoad road, EtsMapNode start, EtsMapNode end, float right, float left)
        {
            U(start.Position, out float p0x, out float p0y, out float p0z);
            U(end.Position, out float p1x, out float p1y, out float p1z);
            Direction(start.Forward(), road.Length, out float t0x, out float t0y, out float t0z);
            Direction(end.Forward(), road.Length, out float t1x, out float t1y, out float t1z);
            int n = Math.Max(1, Math.Min(128, (int)Math.Ceiling(Math.Max(1f, road.Length) / 8f)));
            float lx = p0x, ly = p0y, lz = p0z;

            for (int i = 1; i <= n; i++)
            {
                float s = i / (float)n, s2 = s * s, s3 = s2 * s;
                float h00 = 2f * s3 - 3f * s2 + 1f, h10 = s3 - 2f * s2 + s, h01 = -2f * s3 + 3f * s2, h11 = s3 - s2;
                float x = h00 * p0x + h10 * t0x + h01 * p1x + h11 * t1x;
                float y = h00 * p0y + h10 * t0y + h01 * p1y + h11 * t1y;
                float z = h00 * p0z + h10 * t0z + h01 * p1z + h11 * t1z;
                b.Add(lx, ly, lz, x, y, z, left, right, road.Uid, KindRoad);
                lx = x; ly = y; lz = z;
            }
        }

        /// <summary>A game direction into the U-frame, normalised and scaled (EtsMapBuilder.ToWorldDirection x length).</summary>
        private static void Direction(float[] game, float length, out float x, out float y, out float z)
        {
            float gx = game[0], gy = game[1], gz = -game[2];
            float len = (float)Math.Sqrt(gx * gx + gy * gy + gz * gz);

            if (len < 1e-6f)
            {
                x = y = z = 0f;
                return;
            }

            x = gx / len * length;
            y = gy / len * length;
            z = gz / len * length;
        }

        /// <summary>
        /// A prefab's lanes and spots stood on the map as EtsMapBuilder.TryPrefabFrame stands the model, yaw only: the
        /// anchor node (descriptor node Origin) on map node 0, its flattened direction turned onto node 0's forward.
        /// </summary>
        private static bool AddPrefab(Builder b, EtsMapPrefab prefab, EtsMapNode node, Defs.PrefabShape shape, float margin)
        {
            int origin = Math.Max(0, Math.Min(shape.NodePosition.Length - 1, prefab.Origin));
            float[] ap = shape.NodePosition[origin], ad = shape.NodeDirection[origin];
            float ax = ad[0], az = -ad[2];
            float[] f = node.Forward();
            float fx = f[0], fz = -f[2];
            float al = (float)Math.Sqrt(ax * ax + az * az), fl = (float)Math.Sqrt(fx * fx + fz * fz);

            if (al < 1e-4f || fl < 1e-4f)
            {
                return false;
            }

            ax /= al; az /= al; fx /= fl; fz /= fl;
            float cos = ax * fx + az * fz, sin = ax * fz - az * fx;
            U(node.Position, out float mx, out float my, out float mz);
            float apx = ap[0], apy = ap[1], apz = -ap[2];
            float ox = mx - (apx * cos - apz * sin), oz = mz - (apx * sin + apz * cos), oy = my - apy;
            float lane = PrefabLaneHalfWidth + margin;

            foreach (float[] points in shape.Lanes)
            {
                float lx = 0, ly = 0, lz = 0;

                for (int i = 0; i * 3 + 2 < points.Length; i++)
                {
                    float px = points[i * 3], py = points[i * 3 + 1], pz = -points[i * 3 + 2];
                    float x = ox + px * cos - pz * sin, z = oz + px * sin + pz * cos, y = oy + py;

                    if (i > 0)
                    {
                        b.Add(lx, ly, lz, x, y, z, lane, lane, prefab.Uid, KindPrefabLane);
                    }

                    lx = x; ly = y; lz = z;
                }
            }

            foreach (float[] p in shape.Spots)
            {
                float px = p[0], pz = -p[2];
                float x = ox + px * cos - pz * sin, z = oz + px * sin + pz * cos, y = oy + p[1];
                b.Add(x, y, z, x, y, z, SpawnRadius + margin, SpawnRadius + margin, prefab.Uid, KindPrefabSpot);
            }

            return true;
        }

        /// <summary>The builder's lists into arrays and the 32 m grid over every segment's widened box.</summary>
        private static void Seal(SectorShapes s, Builder b)
        {
            int n = b.Ax.Count;
            s.Count = n;
            s.Ax = b.Ax.ToArray(); s.Ay = b.Ay.ToArray(); s.Az = b.Az.ToArray();
            s.Bx = b.Bx.ToArray(); s.By = b.By.ToArray(); s.Bz = b.Bz.ToArray();
            s.Left = b.Left.ToArray(); s.Right = b.Right.ToArray(); s.Uid = b.Uid.ToArray(); s.Kind = b.Kind.ToArray();

            if (n == 0)
            {
                s.CellStart = new int[1];
                s.CellItems = new int[0];
                return;
            }

            float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;

            for (int i = 0; i < n; i++)
            {
                float r = Math.Max(s.Left[i], s.Right[i]) + MaxMargin;
                minX = Math.Min(minX, Math.Min(s.Ax[i], s.Bx[i]) - r);
                maxX = Math.Max(maxX, Math.Max(s.Ax[i], s.Bx[i]) + r);
                minZ = Math.Min(minZ, Math.Min(s.Az[i], s.Bz[i]) - r);
                maxZ = Math.Max(maxZ, Math.Max(s.Az[i], s.Bz[i]) + r);
            }

            s.MinX = minX; s.MinZ = minZ; s.MaxX = maxX; s.MaxZ = maxZ;
            float c = SectorShapes.CellSize;
            s.GridW = Math.Max(1, (int)Math.Ceiling((maxX - minX) / c));
            s.GridH = Math.Max(1, (int)Math.Ceiling((maxZ - minZ) / c));
            var counts = new int[s.GridW * s.GridH + 1];

            for (int pass = 0; pass < 2; pass++)
            {
                for (int i = 0; i < n; i++)
                {
                    float r = Math.Max(s.Left[i], s.Right[i]) + MaxMargin;
                    int x0 = Clamp((int)((Math.Min(s.Ax[i], s.Bx[i]) - r - minX) / c), s.GridW), x1 = Clamp((int)((Math.Max(s.Ax[i], s.Bx[i]) + r - minX) / c), s.GridW);
                    int z0 = Clamp((int)((Math.Min(s.Az[i], s.Bz[i]) - r - minZ) / c), s.GridH), z1 = Clamp((int)((Math.Max(s.Az[i], s.Bz[i]) + r - minZ) / c), s.GridH);

                    for (int gz = z0; gz <= z1; gz++)
                    {
                        for (int gx = x0; gx <= x1; gx++)
                        {
                            int cell = gz * s.GridW + gx;

                            if (pass == 0)
                            {
                                counts[cell + 1]++;
                            }
                            else
                            {
                                s.CellItems[counts[cell]++] = i;
                            }
                        }
                    }
                }

                if (pass == 0)
                {
                    for (int k = 1; k < counts.Length; k++)
                    {
                        counts[k] += counts[k - 1];
                    }

                    s.CellStart = (int[])counts.Clone();
                    s.CellItems = new int[counts[counts.Length - 1]];
                }
            }
        }

        private static int Clamp(int v, int size)
        {
            return v < 0 ? 0 : v >= size ? size - 1 : v;
        }

        /// <summary>
        /// Whether the U-frame point (x, z) with its foot at height y is kept out by this sector's footprints, and by what.
        /// <paramref name="margin"/>: metres added round every footprint (TrunkMargin, GrassMargin; at most MaxMargin).
        /// <paramref name="ignoreUid"/>: a footprint not to test (0 = test all).
        /// </summary>
        public static bool Blocked(SectorShapes s, float x, float y, float z, float margin, ulong ignoreUid, out Hit hit)
        {
            hit = default;
            margin = Math.Max(0f, Math.Min(MaxMargin, margin));

            if (s == null || !s.Covers(x, z))
            {
                return false;
            }

            float c = SectorShapes.CellSize;
            int gx = Clamp((int)((x - s.MinX) / c), s.GridW), gz = Clamp((int)((z - s.MinZ) / c), s.GridH);
            int cell = gz * s.GridW + gx;

            for (int k = s.CellStart[cell], end = s.CellStart[cell + 1]; k < end; k++)
            {
                int i = s.CellItems[k];

                if (ignoreUid != 0 && s.Uid[i] == ignoreUid)
                {
                    continue;
                }

                float ax = s.Ax[i], az = s.Az[i];
                float dx = s.Bx[i] - ax, dz = s.Bz[i] - az;
                float len = (float)Math.Sqrt(dx * dx + dz * dz);
                float px = x - ax, pz = z - az;
                float surface;

                if (len < 1e-3f)
                {
                    float r = s.Right[i] + margin;

                    if (px * px + pz * pz > r * r)
                    {
                        continue;
                    }

                    surface = s.Ay[i];
                }
                else
                {
                    dx /= len;
                    dz /= len;
                    float along = px * dx + pz * dz;

                    // A little past each end, so the joints between segments and items leave no slit.
                    if (along < -0.5f || along > len + 0.5f)
                    {
                        continue;
                    }

                    // Right of a->b in Unity's frame: Cross(up, (dx, 0, dz)) = (dz, 0, -dx).
                    float lateral = px * dz - pz * dx;

                    if (lateral > s.Right[i] + margin || lateral < -s.Left[i] - margin)
                    {
                        continue;
                    }

                    float t = Math.Max(0f, Math.Min(1f, along / len));
                    surface = s.Ay[i] + (s.By[i] - s.Ay[i]) * t;
                }

                float rise = surface - y;

                if (rise <= -ClearBelow || rise >= ClearAbove)
                {
                    continue;
                }

                hit.Uid = s.Uid[i];
                hit.Kind = s.Kind[i];
                hit.SurfaceY = surface;
                return true;
            }

            return false;
        }

        /// <summary>The sectors whose footprints can reach (x, z) (U-frame): its own and the neighbours within SectorReach.</summary>
        public static void SectorsNear(float x, float z, float reach, Action<int, int> each)
        {
            float gx = x, gz = -z;
            EtsMapSector.SectorOf(gx - reach, gz - reach, out int x0, out int z0);
            EtsMapSector.SectorOf(gx + reach, gz + reach, out int x1, out int z1);

            for (int sx = x0; sx <= x1; sx++)
            {
                for (int sz = z0; sz <= z1; sz++)
                {
                    each(sx, sz);
                }
            }
        }

        // ------------------------------------------------------------------ the run-time registry (main thread)

        /// <summary>
        /// The footprints of the sectors round the planting, computed on a worker thread of its own as the sectors are read,
        /// and dropped again far away. Ensure says whether an area is fully known; Blocked tests a point against it.
        /// Main thread only (the worker sees only a sector and the Defs).
        /// </summary>
        public sealed class Registry : IDisposable
        {
            private readonly EtsMap _map;
            private readonly Defs _defs;
            private EtsWorker _worker;
            private readonly Dictionary<long, SectorShapes> _ready = new Dictionary<long, SectorShapes>();
            private readonly HashSet<long> _inFlight = new HashSet<long>();
            private readonly HashSet<long> _absent = new HashSet<long>();
            private readonly List<long> _drop = new List<long>();
            private readonly Stopwatch _clock = new Stopwatch();
            private Func<bool> _more;

            public int Computed, Segments, Roads, Prefabs, Skipped;
            public double WorstMs, TotalMs;

            public Registry(EtsMap map, EtsFileSystem files)
            {
                _map = map;
                _defs = new Defs(files);
            }

            public EtsMap Map => _map;
            public int Held => _ready.Count;

            private static long Key(int x, int z)
            {
                return ((long)x << 32) ^ (uint)z;
            }

            /// <summary>Adopts finished sectors (under ~1 ms; the adopt is a dictionary insert).</summary>
            public void Pump()
            {
                if (_worker == null)
                {
                    return;
                }

                if (_more == null)
                {
                    _more = () => _clock.ElapsedTicks < Stopwatch.Frequency / 1000;
                }

                _clock.Restart();
                _worker.Drain(_more);
            }

            private int _tick;
            private readonly Dictionary<long, int> _askedAt = new Dictionary<long, int>();

            /// <summary>Once a round (the planting's 0.25 s promote, the grass's post): adopts finished footprints, and lets Ensure ask the map again.</summary>
            public void Tick()
            {
                _tick++;
                Pump();
            }

            /// <summary>The map's read-ahead for a sector, asked at most once a tick (EtsMap.ReadAhead pumps the map's own reader each call).</summary>
            private bool AskMap(long key, int sx, int sz)
            {
                if (_askedAt.TryGetValue(key, out int at) && at == _tick)
                {
                    return false;
                }

                _askedAt[key] = _tick;
                return _map.ReadAhead(sx, sz);
            }

            /// <summary>
            /// True when every sector within SectorReach of the U-frame rectangle has been read and its footprints computed
            /// (a sector the map does not have counts as known: nothing stands there). Otherwise starts what is missing
            /// - the map's own read-ahead, then this registry's compute - and returns false: ask again later.
            /// </summary>
            public bool Ensure(float minX, float minZ, float maxX, float maxZ)
            {
                // No Pump here: the caller calls Tick once a round (a Pump adopts finished work, up to 1 ms).
                float gx0 = minX - SectorReach, gx1 = maxX + SectorReach, gz0 = -maxZ - SectorReach, gz1 = -minZ + SectorReach;
                EtsMapSector.SectorOf(gx0, gz0, out int x0, out int z0);
                EtsMapSector.SectorOf(gx1, gz1, out int x1, out int z1);
                bool all = true;

                for (int sx = x0; sx <= x1; sx++)
                {
                    for (int sz = z0; sz <= z1; sz++)
                    {
                        long key = Key(sx, sz);

                        if (_ready.ContainsKey(key) || _absent.Contains(key))
                        {
                            continue;
                        }

                        all = false;

                        if (_inFlight.Contains(key) || !AskMap(key, sx, sz))
                        {
                            continue;
                        }

                        EtsMapSector sector = _map.GetSector(sx, sz);

                        if (sector == null)
                        {
                            _absent.Add(key);
                            continue;
                        }

                        if (_worker == null)
                        {
                            _worker = new EtsWorker("Ets road clearance");
                        }

                        _inFlight.Add(key);
                        Defs defs = _defs;
                        _worker.Post(() => Compute(defs, sector), (result, failure) => Landed(key, result as SectorShapes, failure));
                    }
                }

                return all;
            }

            private void Landed(long key, SectorShapes shapes, Exception failure)
            {
                _inFlight.Remove(key);

                if (shapes == null)
                {
                    // Never leave the area waiting for ever: an empty set (the hard-surface ray still guards it).
                    shapes = new SectorShapes { CellStart = new int[1], CellItems = new int[0] };

                    if (Skipped++ < 3 && failure != null)
                    {
                        UnityEngine.Debug.LogWarning("[SomniumEuroTruck] Road clearance: a sector failed: " + failure.GetType().Name + ": " + failure.Message);
                    }
                }

                _ready[key] = shapes;
                Computed++;
                Segments += shapes.Count;
                Roads += shapes.Roads;
                Prefabs += shapes.Prefabs;
                Skipped += shapes.Skipped;
                TotalMs += shapes.Milliseconds;
                WorstMs = Math.Max(WorstMs, shapes.Milliseconds);
            }

            /// <summary>Whether the U-frame point is kept out by any held sector's footprint (see the class notes for the height rule).</summary>
            public bool Blocked(float x, float y, float z, float margin, out Hit hit)
            {
                hit = default;
                float gx = x, gz = -z;
                EtsMapSector.SectorOf(gx - SectorReach, gz - SectorReach, out int x0, out int z0);
                EtsMapSector.SectorOf(gx + SectorReach, gz + SectorReach, out int x1, out int z1);

                for (int sx = x0; sx <= x1; sx++)
                {
                    for (int sz = z0; sz <= z1; sz++)
                    {
                        if (_ready.TryGetValue(Key(sx, sz), out SectorShapes s) && EtsRoadClearance.Blocked(s, x, y, z, margin, 0, out hit))
                        {
                            return true;
                        }
                    }
                }

                return false;
            }

            /// <summary>The held sectors that can reach the U-frame rectangle, for a worker that tests points off the main thread.</summary>
            public SectorShapes[] Snapshot(float minX, float minZ, float maxX, float maxZ)
            {
                var list = new List<SectorShapes>();

                foreach (SectorShapes s in _ready.Values)
                {
                    if (s.Count > 0 && s.MaxX >= minX && s.MinX <= maxX && s.MaxZ >= minZ && s.MinZ <= maxZ)
                    {
                        list.Add(s);
                    }
                }

                return list.ToArray();
            }

            /// <summary>Drops sectors whose square lies further than <paramref name="beyond"/> from the U-frame point (x, z).</summary>
            public void Forget(float x, float z, float beyond)
            {
                _drop.Clear();
                float gx = x, gz = -z;

                foreach (KeyValuePair<long, SectorShapes> pair in _ready)
                {
                    int sx = (int)(pair.Key >> 32), sz = (int)(uint)pair.Key;
                    float cx = Math.Max(sx * EtsMapSector.SectorSize, Math.Min(gx, (sx + 1) * EtsMapSector.SectorSize));
                    float cz = Math.Max(sz * EtsMapSector.SectorSize, Math.Min(gz, (sz + 1) * EtsMapSector.SectorSize));

                    if ((cx - gx) * (cx - gx) + (cz - gz) * (cz - gz) > beyond * beyond)
                    {
                        _drop.Add(pair.Key);
                    }
                }

                foreach (long key in _drop)
                {
                    _ready.Remove(key);
                }
            }

            public void Dispose()
            {
                if (_worker != null)
                {
                    _worker.Dispose();
                    _worker = null;
                }

                _inFlight.Clear();
            }
        }

        /// <summary>Whether a U-frame point is kept out by any of these footprints (a worker's snapshot).</summary>
        public static bool Blocked(SectorShapes[] shapes, float x, float y, float z, float margin)
        {
            if (shapes == null)
            {
                return false;
            }

            for (int i = 0; i < shapes.Length; i++)
            {
                if (Blocked(shapes[i], x, y, z, margin, 0, out _))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
