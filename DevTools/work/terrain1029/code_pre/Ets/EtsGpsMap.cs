using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using UnityEngine;

namespace SomniumSpace.Worlds.SomniumEuroTruck.Ets
{
    /// <summary>
    /// The route advisor's GPS map (2026-10-05, "navigation like the game"): a small top-down picture of the roads
    /// round the truck, turned so the truck's heading is up, the job's route over them in the game's navigation
    /// colour. Drawn on the CPU into a reused Color32 buffer (the advisor uploads it to a Texture2D a few times a
    /// second); no allocation once the buffers have grown.
    ///
    /// Every number is the game's own, read from /def/map_data.sii (colours stored ABGR, as /locale/ui_colors.sui):
    ///   road_discovered_color 0xFF4cc6ff, prefab_discovered_color 0xFF98CAEB ("discovered prefab driveable areas"),
    ///   outline_color 0xFF000000 ("route outline"), navigation_color 0xFF0C0CCF ("navigation path color" - red);
    ///   the minimap's two zooms zoom_uplift[2] 900 / [3] 4000 (camera height over the player, so the player is at the
    ///   map's centre), player_size[2] 120 / [3] 500 m, road_size_factor[2] 1.5 / [3] 3.0.
    /// Not in the files: the minimap camera's field of view. ViewPerUplift (half the map's height = 0.3 x uplift) is
    /// chosen so player_size draws the same ~22% of the map height at both zooms, as the file's two pairs imply
    /// (120/900 = 0.133, 500/4000 = 0.125). Junction (prefab) areas are drawn as spokes from each prefab node to the
    /// prefab's centre - the game fills them from the prefab's map points, which the world does not parse.
    /// </summary>
    public sealed class EtsGpsMap
    {
        public const float ViewPerUplift = 0.3f;

        /// <summary>EtsLaneGraph.LaneWidth (kept here so offline probes compile this file alone with the map parsers).</summary>
        public const float LaneWidth = 4.5f;

        public Color32 Background = new Color32(26, 26, 26, 235);
        public Color32 RoadColour = Abgr(0xFF4cc6ffu);
        public Color32 PrefabColour = Abgr(0xFF98CAEBu);
        public Color32 OutlineColour = Abgr(0xFF000000u);
        public Color32 RouteColour = Abgr(0xFF0C0CCFu);
        public float[] Uplift = { 900f, 4000f };
        public float[] PlayerSize = { 120f, 500f };
        public float[] RoadFactor = { 1.5f, 3.0f };

        public readonly int Width, Height;
        public readonly Color32[] Pixels;

        /// <summary>Cached segments in Unity metres: x0, z0, x1, z1, half width; kind 0 road, 1 prefab.</summary>
        private float[] _seg = new float[5 * 4096];
        private byte[] _kind = new byte[4096];
        private int _count;

        public int Segments => _count;
        public Vector3 CollectedAround { get; private set; }
        public bool HasCollected { get; private set; }

        public EtsGpsMap(int width, int height)
        {
            Width = width;
            Height = height;
            Pixels = new Color32[width * height];
        }

        public static Color32 Abgr(uint abgr)
        {
            return new Color32((byte)(abgr & 0xFF), (byte)((abgr >> 8) & 0xFF), (byte)((abgr >> 16) & 0xFF), (byte)(abgr >> 24));
        }

        /// <summary>Reads the colours and minimap zoom numbers from /def/map_data.sii; keeps the defaults above (the same values) on a miss.</summary>
        public string LoadDefinitions(EtsFileSystem files)
        {
            string text = files != null ? files.ReadText("/def/map_data.sii") : null;

            if (text == null)
            {
                return "map_data.sii not read, built-in copies of its values used";
            }

            RoadColour = Colour(text, "road_discovered_color", RoadColour);
            PrefabColour = Colour(text, "prefab_discovered_color", PrefabColour);
            OutlineColour = Colour(text, "outline_color", OutlineColour);
            RouteColour = Colour(text, "navigation_color", RouteColour);
            Uplift[0] = Number(text, "zoom_uplift[2]", Uplift[0]);
            Uplift[1] = Number(text, "zoom_uplift[3]", Uplift[1]);
            PlayerSize[0] = Number(text, "player_size[2]", PlayerSize[0]);
            PlayerSize[1] = Number(text, "player_size[3]", PlayerSize[1]);
            RoadFactor[0] = Number(text, "road_size_factor[2]", RoadFactor[0]);
            RoadFactor[1] = Number(text, "road_size_factor[3]", RoadFactor[1]);
            return "navigation " + RouteColour + ", roads " + RoadColour + ", zoom uplift " + Uplift[0] + "/" + Uplift[1] + ", player " + PlayerSize[0] + "/" + PlayerSize[1] + " m";
        }

        private static Color32 Colour(string text, string key, Color32 fallback)
        {
            Match m = Regex.Match(text, "^\\s*" + Regex.Escape(key) + ":\\s*0x([0-9A-Fa-f]{8})", RegexOptions.Multiline);
            return m.Success ? Abgr(uint.Parse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)) : fallback;
        }

        private static float Number(string text, string key, float fallback)
        {
            Match m = Regex.Match(text, "^\\s*" + Regex.Escape(key) + ":\\s*([-0-9.]+)", RegexOptions.Multiline);
            return m.Success && float.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : fallback;
        }

        /// <summary>Half the map's height in metres at a zoom (0 close, 1 far).</summary>
        public float HalfHeightMetres(int zoom)
        {
            return Uplift[Mathf.Clamp(zoom, 0, 1)] * ViewPerUplift;
        }

        /// <summary>The player arrow's length in map pixels at a zoom (player_size).</summary>
        public float PlayerPixels(int zoom)
        {
            return PlayerSize[Mathf.Clamp(zoom, 0, 1)] / (2f * HalfHeightMetres(zoom)) * Height;
        }

        /// <summary>The radius the cache must cover for the far zoom's corners, metres.</summary>
        public float NeededRadius()
        {
            float h = HalfHeightMetres(1), w = h * Width / Height;
            return Mathf.Sqrt(h * h + w * w) + 400f;
        }

        // ------------------------------------------------------------------ the roads round the truck

        /// <summary>
        /// Rebuilds the segment cache from every loaded map sector touching the circle: each road along its own
        /// Hermite curve (as EtsRouteLine and the road builder do) at its look's full width, each prefab as spokes.
        /// Occasional (the advisor calls it when the truck has moved far or new sectors came in), not per frame.
        /// </summary>
        public void Collect(EtsMap map, IEnumerable<EtsMapSector> sectors, Vector3 centerGame, Vector3 around, float radius)
        {
            _count = 0;
            CollectedAround = around;
            HasCollected = true;
            float gx = around.x + centerGame.x, gz = -around.z + centerGame.z;
            float r2 = radius * radius;

            foreach (EtsMapSector sector in sectors)
            {
                if (sector == null) continue;
                float sx0 = sector.SectorX * EtsMapSector.SectorSize, sz0 = sector.SectorZ * EtsMapSector.SectorSize;
                float dx = Mathf.Max(0f, Mathf.Max(sx0 - gx, gx - (sx0 + EtsMapSector.SectorSize)));
                float dz = Mathf.Max(0f, Mathf.Max(sz0 - gz, gz - (sz0 + EtsMapSector.SectorSize)));
                if (dx * dx + dz * dz > r2) continue;

                foreach (EtsMapRoad road in sector.Roads)
                {
                    EtsMapNode a = Node(map, sector, road.StartNode), b = Node(map, sector, road.EndNode);
                    if (a == null || b == null) continue;
                    Vector3 p0 = ToUnity(a.Position, centerGame), p1 = ToUnity(b.Position, centerGame);
                    float reach = radius + road.Length;
                    if ((p0 - around).sqrMagnitude > reach * reach) continue;

                    float half = HalfWidth(map, road);
                    float length = Mathf.Max(1f, road.Length);
                    Vector3 t0 = ToUnityDirection(a.Forward()) * length, t1 = ToUnityDirection(b.Forward()) * length;
                    int samples = Mathf.Clamp(Mathf.CeilToInt(length / 15f), 1, 64);
                    Vector3 prev = p0;

                    for (int i = 1; i <= samples; i++)
                    {
                        Vector3 p = Hermite(p0, t0, p1, t1, (float)i / samples);
                        Add(prev, p, half, 0);
                        prev = p;
                    }
                }

                foreach (EtsMapPrefab prefab in sector.Prefabs)
                {
                    int n = prefab.NodeUids.Length;
                    if (n < 2 || n > 12) continue;
                    Vector3 sum = Vector3.zero;
                    int found = 0;

                    for (int i = 0; i < n; i++)
                    {
                        EtsMapNode node = Node(map, sector, prefab.NodeUids[i]);
                        if (node == null) continue;
                        sum += ToUnity(node.Position, centerGame);
                        found++;
                    }

                    if (found < 2) continue;
                    Vector3 centre = sum / found;
                    if ((centre - around).sqrMagnitude > r2) continue;

                    for (int i = 0; i < n; i++)
                    {
                        EtsMapNode node = Node(map, sector, prefab.NodeUids[i]);
                        if (node == null) continue;
                        Vector3 p = ToUnity(node.Position, centerGame);
                        if ((p - centre).sqrMagnitude > 150f * 150f) continue;   // a yard's far gate: not a junction spoke
                        Add(p, centre, LaneWidth, 1);
                    }
                }
            }
        }

        private static EtsMapNode Node(EtsMap map, EtsMapSector sector, ulong uid)
        {
            return sector.NodesByUid.TryGetValue(uid, out EtsMapNode node) ? node : map != null ? map.FindNode(uid) : null;
        }

        /// <summary>Half the paved width: the look's lanes both ways at 4.5 m (LaneWidth) plus road_offset; 3 m without a look.</summary>
        private static float HalfWidth(EtsMap map, EtsMapRoad road)
        {
            EtsSiiUnit look = map != null ? map.RoadLook(road.LookToken) : null;
            if (look == null) return 3f;
            int lanes = look.GetArray("lanes_right").Count + look.GetArray("lanes_left").Count;
            return Mathf.Max(3f, lanes * LaneWidth * 0.5f + Mathf.Abs(look.GetFloat("road_offset", 0f)));
        }

        private void Add(Vector3 a, Vector3 b, float half, byte kind)
        {
            if (_count >= _kind.Length)
            {
                Array.Resize(ref _kind, _kind.Length * 2);
                Array.Resize(ref _seg, _seg.Length * 2);
            }

            int o = _count * 5;
            _seg[o] = a.x; _seg[o + 1] = a.z; _seg[o + 2] = b.x; _seg[o + 3] = b.z; _seg[o + 4] = half;
            _kind[_count++] = kind;
        }

        private static Vector3 ToUnity(float[] game, Vector3 centerGame)
        {
            return new Vector3(game[0] - centerGame.x, game[1] - centerGame.y, -(game[2] - centerGame.z));
        }

        private static Vector3 ToUnityDirection(float[] d)
        {
            return new Vector3(d[0], d[1], -d[2]).normalized;
        }

        private static Vector3 Hermite(Vector3 p0, Vector3 t0, Vector3 p1, Vector3 t1, float s)
        {
            float s2 = s * s, s3 = s2 * s;
            return (2f * s3 - 3f * s2 + 1f) * p0 + (s3 - 2f * s2 + s) * t0 + (-2f * s3 + 3f * s2) * p1 + (s3 - s2) * t1;
        }

        // ------------------------------------------------------------------ drawing

        // The view of the current Render call: map pixel = centre + (dot(rel, right), dot(rel, forward)) * scale.
        private float _tx, _tz, _fx, _fz, _scale, _cx, _cy;

        /// <summary>
        /// Draws the map: background, roads, junctions, then the route (from index 'routeFrom' on, the part
        /// still to drive) with its outline. The truck sits at the centre, heading up. Pixels row 0 is the bottom.
        /// </summary>
        public void Render(Vector3 truck, Vector3 forward, int zoom, IReadOnlyList<Vector3> route, int routeFrom)
        {
            Color32 bg = Background;
            for (int i = 0; i < Pixels.Length; i++) Pixels[i] = bg;

            forward.y = 0f;
            if (forward.sqrMagnitude < 1e-6f) forward = Vector3.forward;
            forward.Normalize();
            _tx = truck.x; _tz = truck.z; _fx = forward.x; _fz = forward.z;
            float halfH = HalfHeightMetres(zoom);
            _scale = Height / (2f * halfH);
            _cx = Width * 0.5f; _cy = Height * 0.5f;
            float factor = RoadFactor[Mathf.Clamp(zoom, 0, 1)];
            float view = Mathf.Sqrt(_cx * _cx + _cy * _cy) / _scale + 30f;

            // Roads, then junctions over them. outline_color is the "route outline" only: roads get none.
            for (int pass = 1; pass < 3; pass++)
            {
                for (int s = 0; s < _count; s++)
                {
                    byte kind = _kind[s];
                    if ((pass == 2) != (kind == 1)) continue;
                    int o = s * 5;
                    float mx = (_seg[o] + _seg[o + 2]) * 0.5f - _tx, mz = (_seg[o + 1] + _seg[o + 3]) * 0.5f - _tz;
                    if (mx * mx + mz * mz > view * view) continue;
                    float half = Mathf.Max(0.8f, _seg[o + 4] * factor * _scale);

                    Segment(_seg[o], _seg[o + 1], _seg[o + 2], _seg[o + 3], half, kind == 1 ? PrefabColour : RoadColour);
                }
            }

            if (route == null || route.Count < 2) return;
            float routeHalf = Mathf.Max(1.5f, 4.5f * factor * _scale);
            int from = Mathf.Clamp(routeFrom, 0, route.Count - 2);
            int stride = Mathf.Max(1, (int)(2f / (8f * _scale)));

            for (int pass = 0; pass < 2; pass++)
            {
                // Route points are ~8 m apart (EtsRouteLine): at the far zoom that is under a pixel, so step over some.
                for (int i = from; i + 1 < route.Count; i += stride)
                {
                    Vector3 a = route[i], b = route[Mathf.Min(i + stride, route.Count - 1)];
                    float mx = (a.x + b.x) * 0.5f - _tx, mz = (a.z + b.z) * 0.5f - _tz;
                    if (mx * mx + mz * mz > view * view) continue;
                    if (pass == 0) Segment(a.x, a.z, b.x, b.z, routeHalf + 1f, OutlineColour);
                    else Segment(a.x, a.z, b.x, b.z, routeHalf, RouteColour);
                }
            }
        }

        /// <summary>One thick antialiased segment between two world points, half width in pixels.</summary>
        private void Segment(float wx0, float wz0, float wx1, float wz1, float half, Color32 colour)
        {
            float rx0 = wx0 - _tx, rz0 = wz0 - _tz, rx1 = wx1 - _tx, rz1 = wz1 - _tz;
            // right = (fz, -fx) on the ground, forward = (fx, fz)
            float ax = _cx + (rx0 * _fz - rz0 * _fx) * _scale, ay = _cy + (rx0 * _fx + rz0 * _fz) * _scale;
            float bx = _cx + (rx1 * _fz - rz1 * _fx) * _scale, by = _cy + (rx1 * _fx + rz1 * _fz) * _scale;
            int x0 = Mathf.Max(0, (int)(Mathf.Min(ax, bx) - half - 1f)), x1 = Mathf.Min(Width - 1, (int)(Mathf.Max(ax, bx) + half + 1f));
            int y0 = Mathf.Max(0, (int)(Mathf.Min(ay, by) - half - 1f)), y1 = Mathf.Min(Height - 1, (int)(Mathf.Max(ay, by) + half + 1f));
            if (x0 > x1 || y0 > y1) return;
            float dx = bx - ax, dy = by - ay, len2 = dx * dx + dy * dy;
            float inv = len2 > 1e-6f ? 1f / len2 : 0f;
            float outer2 = (half + 0.5f) * (half + 0.5f), inner = Mathf.Max(0f, half - 0.5f), inner2 = inner * inner;

            for (int y = y0; y <= y1; y++)
            {
                float py = y + 0.5f - ay;
                int row = y * Width;

                for (int x = x0; x <= x1; x++)
                {
                    float px = x + 0.5f - ax;
                    float t = (px * dx + py * dy) * inv;
                    t = t < 0f ? 0f : t > 1f ? 1f : t;
                    float ex = px - dx * t, ey = py - dy * t;
                    float d2 = ex * ex + ey * ey;
                    if (d2 >= outer2) continue;
                    float cover = d2 <= inner2 ? 1f : half + 0.5f - Mathf.Sqrt(d2);
                    if (cover <= 0f) continue;

                    if (cover >= 1f)
                    {
                        Pixels[row + x] = colour;
                    }
                    else
                    {
                        Color32 d = Pixels[row + x];
                        Pixels[row + x] = new Color32((byte)(d.r + (colour.r - d.r) * cover), (byte)(d.g + (colour.g - d.g) * cover),
                                                      (byte)(d.b + (colour.b - d.b) * cover), (byte)(d.a + (colour.a - d.a) * cover));
                    }
                }
            }
        }
    }
}
