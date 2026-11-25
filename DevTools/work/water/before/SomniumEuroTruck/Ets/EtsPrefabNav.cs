using System;
using System.Collections.Generic;

namespace SomniumSpace.Worlds.SomniumEuroTruck.Ets
{
    /// <summary>
    /// The AI navigation of a prefab descriptor (.ppd): the curves the game's traffic drives
    /// through a junction, which curve each road end feeds, and the game's own precomputed
    /// routes between the prefab's nodes. Pure (no Unity), so the harness runs it on the files.
    ///
    /// Layout (TruckLib.Models Ppd/NavCurve.cs Deserialize16to19, fetched from
    /// github.com/sk-zk/TruckLib.Models for this design), checked on the demo 1.61.1.0 by
    /// harness Program1373 over every descriptor the prefab definitions name (3,341, all
    /// version 25):
    ///   - nav curve record = 132 bytes on all 3,048 descriptors that have curves, taken from the
    ///     file's own table ((offset@56 - offset@52) / count@8);
    ///     +0 name token, +8 u32 flags, +12 four bytes (end node, end lane, start node, start
    ///     lane), +16 start float3, +28 end float3, +40 start rotation w,x,y,z, +56 end rotation
    ///     w,x,y,z, +72 f32 length, +76 4 x i32 next, +92 4 x i32 previous, +108 next used,
    ///     +112 previous used, +116 i32 semaphore id, +120 u64 traffic rule token, +128 u32 nav
    ///     node index;
    ///   - 60,111 curves: every rotation a unit quaternion, every next/previous index in range,
    ///     every 'next' link joined end-to-start within 5 cm (55,998 of 55,998, Program1377);
    ///   - node record (104 bytes): +40 eight i32 input lanes, +72 eight i32 output lanes (curve
    ///     indices or -1); 21,919 references, none out of range; an input lane's curve STARTS
    ///     within 30 m of its node 10,959 times of 10,986, an output lane's curve ENDS there
    ///     10,893 of 10,933;
    ///   - nav node record = 188 bytes on all 3,341 (u8 type, u16 index, u8 used, 8 x {u16
    ///     target, f32 length, u8 used, 8 x u16 curve});
    ///   - following 'next' from every input lane: 20,074 chains, 19,176 end on an output lane,
    ///     0 dead ends, 898 revisit a curve (roundabouts), deepest 40.
    ///   - flags: bits 5-6 allowed vehicles (player only 511, small 1,986, large 880, all 56,734),
    ///     bits 2-4 blinker (0: 49,665, 1: 1,129, right 2: 4,752, left 4: 4,565), 4,282 curves
    ///     carry a semaphore id.
    /// Shape: the file gives two poses and a length, not the interpolation. A Hermite through
    /// the poses with tangents = chord x forward reproduces the stored length within 1 % on
    /// 48,192 of 60,106 curves; on the rest the stored length is barely more than the chord
    /// even for 45-100 degree turns (e.g. 12.30 m stored for a 12.20 m chord turning 83
    /// degrees), so the stored length is not an arc length there and the true shape is NOT
    /// DETERMINED. Traffic uses the Hermite and measures its own arc length.
    /// </summary>
    public sealed class EtsPrefabNav
    {
        public const int CurveBytes = 132;
        public const int NodeBytes = 104;
        public const int NavNodeBytes = 188;

        public const int AllowPlayerOnly = 0;
        public const int AllowSmall = 1;
        public const int AllowLarge = 2;
        public const int AllowAll = 3;

        private static readonly int[] Empty = new int[0];

        public sealed class Curve
        {
            public int Index;
            public float[] Start = new float[3];
            public float[] End = new float[3];

            /// <summary>Rotation applied to (0, 0, -1), in the descriptor's (game) frame.</summary>
            public float[] StartForward = new float[3];
            public float[] EndForward = new float[3];

            public float StoredLength;
            public uint Flags;
            public int[] Next = Empty;
            public int[] Previous = Empty;
            public int Semaphore = -1;
            public ulong TrafficRule;

            public int AllowedVehicles => (int)((Flags >> 5) & 3);
            public int Blinker => (int)((Flags >> 2) & 7);
            public bool LowProbability => (Flags & (1u << 13)) != 0;
            public bool ForCars => AllowedVehicles == AllowSmall || AllowedVehicles == AllowAll;

            /// <summary>Large vehicles (trucks, buses) may drive it: bit 6 (57,614 of 60,111 curves; 880 large only).</summary>
            public bool ForLarge => AllowedVehicles == AllowLarge || AllowedVehicles == AllowAll;

            /// <summary>
            /// The priority modifier, flags bits 16-19 (TruckLib NavCurve; 17,819 of 60,111 curves carry one). Measured on
            /// ger_r1_plus_1_to_r2_no_offset_tshape_tmpl: the main road's straight curves 12, the side road's turns 6 and
            /// 8. A vehicle on a lower one gives way to one on a higher one where their curves conflict (EtsTrafficSim).
            /// </summary>
            public int Priority => (int)((Flags >> 16) & 15);

            /// <summary>Bit 15 (193 curves): additive priority. Its rule is NOT DETERMINED; read only for the record.</summary>
            public bool AdditivePriority => (Flags & (1u << 15)) != 0;
        }

        public int Version;
        public Curve[] Curves = new Curve[0];
        public float[][] NodePosition = new float[0][];
        public float[][] NodeDirection = new float[0][];
        public int[][] NodeInputs = new int[0][];
        public int[][] NodeOutputs = new int[0][];

        /// <summary>For each curve that is some node's output lane: that node; otherwise -1.</summary>
        public int[] OutputNodeOf = Empty;

        private sbyte[] _conflicts;
        private readonly Dictionary<int, List<int[]>> _chains = new Dictionary<int, List<int[]>>();

        public static EtsPrefabNav Parse(byte[] b, out string failure)
        {
            failure = null;

            if (b == null || b.Length < 96)
            {
                failure = "descriptor too short";
                return null;
            }

            int version = BitConverter.ToInt32(b, 0);
            int nodes = BitConverter.ToInt32(b, 4), count = BitConverter.ToInt32(b, 8);
            int nodeOffset = BitConverter.ToInt32(b, 48), curveOffset = BitConverter.ToInt32(b, 52), signOffset = BitConverter.ToInt32(b, 56);

            if (nodes < 0 || nodes > 64 || nodeOffset < 0 || nodeOffset + nodes * NodeBytes > b.Length)
            {
                failure = "node table out of range";
                return null;
            }

            // Only the stride that was measured. Version 21 (0x15) had a 128-byte record without
            // the nav node index (TruckLib Deserialize15); none of those ship with 1.61.
            if (count > 0 && (signOffset - curveOffset) / count != CurveBytes)
            {
                failure = "nav curve stride " + ((signOffset - curveOffset) / count) + " is not the measured " + CurveBytes + " (version " + version + ")";
                return null;
            }

            if (count < 0 || curveOffset + count * CurveBytes > b.Length)
            {
                failure = "nav curve table out of range";
                return null;
            }

            var nav = new EtsPrefabNav { Version = version, Curves = new Curve[count] };

            for (int c = 0; c < count; c++)
            {
                int o = curveOffset + c * CurveBytes;
                var curve = new Curve { Index = c, Flags = BitConverter.ToUInt32(b, o + 8) };

                for (int k = 0; k < 3; k++)
                {
                    curve.Start[k] = BitConverter.ToSingle(b, o + 16 + k * 4);
                    curve.End[k] = BitConverter.ToSingle(b, o + 28 + k * 4);
                }

                curve.StartForward = Forward(b, o + 40);
                curve.EndForward = Forward(b, o + 56);
                curve.StoredLength = BitConverter.ToSingle(b, o + 72);
                curve.Next = Links(b, o + 76, BitConverter.ToInt32(b, o + 108), count);
                curve.Previous = Links(b, o + 92, BitConverter.ToInt32(b, o + 112), count);
                curve.Semaphore = BitConverter.ToInt32(b, o + 116);
                curve.TrafficRule = BitConverter.ToUInt64(b, o + 120);
                nav.Curves[c] = curve;
            }

            nav.NodePosition = new float[nodes][];
            nav.NodeDirection = new float[nodes][];
            nav.NodeInputs = new int[nodes][];
            nav.NodeOutputs = new int[nodes][];
            nav.OutputNodeOf = new int[count];

            for (int c = 0; c < count; c++)
            {
                nav.OutputNodeOf[c] = -1;
            }

            for (int n = 0; n < nodes; n++)
            {
                int o = nodeOffset + n * NodeBytes;
                nav.NodePosition[n] = new[] { BitConverter.ToSingle(b, o + 16), BitConverter.ToSingle(b, o + 20), BitConverter.ToSingle(b, o + 24) };
                nav.NodeDirection[n] = new[] { BitConverter.ToSingle(b, o + 28), BitConverter.ToSingle(b, o + 32), BitConverter.ToSingle(b, o + 36) };
                nav.NodeInputs[n] = Lanes(b, o + 40, count);
                nav.NodeOutputs[n] = Lanes(b, o + 72, count);

                foreach (int output in nav.NodeOutputs[n])
                {
                    nav.OutputNodeOf[output] = n;
                }
            }

            return nav;
        }

        private static float[] Forward(byte[] b, int o)
        {
            // Stored w first (as every SCS quaternion); EtsMapNode.Rotate takes x, y, z, w.
            var q = new[] { BitConverter.ToSingle(b, o + 4), BitConverter.ToSingle(b, o + 8), BitConverter.ToSingle(b, o + 12), BitConverter.ToSingle(b, o) };
            return EtsMapNode.Rotate(q, 0f, 0f, -1f);
        }

        private static int[] Links(byte[] b, int o, int used, int count)
        {
            used = Math.Max(0, Math.Min(4, used));
            var list = new List<int>(used);

            for (int k = 0; k < used; k++)
            {
                int index = BitConverter.ToInt32(b, o + k * 4);
                if (index >= 0 && index < count) list.Add(index);
            }

            return list.Count == 0 ? Empty : list.ToArray();
        }

        private static int[] Lanes(byte[] b, int o, int count)
        {
            var list = new List<int>(2);

            for (int k = 0; k < 8; k++)
            {
                int index = BitConverter.ToInt32(b, o + k * 4);
                if (index >= 0 && index < count) list.Add(index);
            }

            return list.Count == 0 ? Empty : list.ToArray();
        }

        // ------------------------------------------------------------------ routes

        /// <summary>
        /// Every way through the prefab from one input curve: the curve lists that follow 'next'
        /// until a curve that is some node's output lane. Cached per descriptor. Revisits are cut
        /// (a roundabout is left at the first way out it offers each time round).
        /// </summary>
        public List<int[]> ChainsFrom(int inputCurve)
        {
            return ChainsFrom(inputCurve, false);
        }

        /// <summary>As ChainsFrom, for a large vehicle (large: the curves with bit 6) or a small one (bit 5).</summary>
        public List<int[]> ChainsFrom(int inputCurve, bool large)
        {
            int key = large ? inputCurve | (1 << 30) : inputCurve;

            if (_chains.TryGetValue(key, out List<int[]> cached))
            {
                return cached;
            }

            var result = new List<int[]>();
            var path = new List<int>();
            var onPath = new HashSet<int>();
            Walk(inputCurve, path, onPath, result, large);
            _chains[key] = result;
            return result;
        }

        private void Walk(int curve, List<int> path, HashSet<int> onPath, List<int[]> result, bool large)
        {
            if (path.Count > 24 || result.Count > 32 || !onPath.Add(curve))
            {
                return;
            }

            path.Add(curve);

            if (OutputNodeOf[curve] >= 0 || Curves[curve].Next.Length == 0)
            {
                if (OutputNodeOf[curve] >= 0) result.Add(path.ToArray());
            }
            else
            {
                foreach (int next in Curves[curve].Next)
                {
                    // Cars keep to curves allowed for small vehicles or all; large-only (880) are the
                    // trucks' and buses' (which leave the small-only 1,986), player-only (511) the player's.
                    if (large ? Curves[next].ForLarge : Curves[next].ForCars)
                    {
                        Walk(next, path, onPath, result, large);
                    }
                }
            }

            path.RemoveAt(path.Count - 1);
            onPath.Remove(curve);
        }

        // ------------------------------------------------------------------ conflicts

        /// <summary>
        /// Whether a car on curve a and a car on curve b can meet: they cross, they merge into one
        /// end, or they pass closer than a car's width - measured on the Hermite polylines in the
        /// descriptor's frame. Curves that leave from the same point (a split) and curves that
        /// follow each other never conflict: cars on them are one queue, and car-following keeps
        /// them apart. Worked out per pair when first asked, and remembered.
        /// </summary>
        public bool Conflict(int a, int b)
        {
            int n = Curves.Length;

            if (a == b || a < 0 || b < 0 || a >= n || b >= n)
            {
                return false;
            }

            // Pair by pair, remembered: the whole n x n table up front cost 2.1 s for the
            // 551-curve hw2-2_x_city2-2_uk and over 2 ms for 1,121 of the 3,048 descriptors
            // (harness Program1400), and it was built inside a simulation step. A car asks about a
            // handful of pairs; each costs a few microseconds the first time.
            if (_conflicts == null)
            {
                _conflicts = new sbyte[n * n];
                _lines = new float[n][];
                _boxes = new float[n][];
            }

            sbyte known = _conflicts[a * n + b];

            if (known != 0)
            {
                return known > 0;
            }

            bool conflict = Pair(a, b);
            _conflicts[a * n + b] = _conflicts[b * n + a] = conflict ? (sbyte)1 : (sbyte)-1;
            return conflict;
        }

        /// <summary>Closest two curves may pass without conflicting (a car's width and a little). Tuned by the harness.</summary>
        public static float ConflictClearance = 2.2f;

        private float[][] _lines;
        private float[][] _boxes;

        private bool Pair(int i, int j)
        {
            Curve a = Curves[i], c = Curves[j];
            bool sameStart = Near(a.Start, c.Start, 0.3f);
            bool sameEnd = Near(a.End, c.End, 0.3f);
            bool sequential = Near(a.End, c.Start, 0.3f) || Near(c.End, a.Start, 0.3f);

            if (sameStart || sequential)
            {
                return false;
            }

            if (sameEnd)
            {
                return true;
            }

            float[] li = LineOf(i), lj = LineOf(j);
            float[] bi = _boxes[i], bj = _boxes[j];

            // Boxes further apart than the clearance cannot hold two points closer than it: the
            // same answer MinDistance would give, without its 64 segment pairs.
            float gapX = Math.Max(bi[0] - bj[2], bj[0] - bi[2]);
            float gapZ = Math.Max(bi[1] - bj[3], bj[1] - bi[3]);

            if (gapX >= ConflictClearance || gapZ >= ConflictClearance)
            {
                return false;
            }

            return MinDistance(li, lj) < ConflictClearance;
        }

        private float[] LineOf(int i)
        {
            if (_lines[i] == null)
            {
                float[] line = Polyline(Curves[i], 8);
                float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;

                for (int k = 0; k + 1 < line.Length; k += 2)
                {
                    minX = Math.Min(minX, line[k]);
                    maxX = Math.Max(maxX, line[k]);
                    minZ = Math.Min(minZ, line[k + 1]);
                    maxZ = Math.Max(maxZ, line[k + 1]);
                }

                _lines[i] = line;
                _boxes[i] = new[] { minX, minZ, maxX, maxZ };
            }

            return _lines[i];
        }

        /// <summary>The curve as a polyline of segments + 1 points, x/z pairs, in the descriptor frame.</summary>
        public static float[] Polyline(Curve curve, int segments)
        {
            float dx = curve.End[0] - curve.Start[0], dy = curve.End[1] - curve.Start[1], dz = curve.End[2] - curve.Start[2];
            float chord = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
            var points = new float[(segments + 1) * 2];

            for (int i = 0; i <= segments; i++)
            {
                float u = i / (float)segments, u2 = u * u, u3 = u2 * u;
                float h00 = 2 * u3 - 3 * u2 + 1, h10 = u3 - 2 * u2 + u, h01 = -2 * u3 + 3 * u2, h11 = u3 - u2;
                points[i * 2] = h00 * curve.Start[0] + h10 * curve.StartForward[0] * chord + h01 * curve.End[0] + h11 * curve.EndForward[0] * chord;
                points[i * 2 + 1] = h00 * curve.Start[2] + h10 * curve.StartForward[2] * chord + h01 * curve.End[2] + h11 * curve.EndForward[2] * chord;
            }

            return points;
        }

        private static bool Near(float[] a, float[] b, float within)
        {
            float dx = a[0] - b[0], dy = a[1] - b[1], dz = a[2] - b[2];
            return dx * dx + dy * dy + dz * dz < within * within;
        }

        private static float MinDistance(float[] a, float[] b)
        {
            float best = float.MaxValue;

            for (int i = 0; i + 3 < a.Length; i += 2)
            {
                for (int j = 0; j + 3 < b.Length; j += 2)
                {
                    best = Math.Min(best, SegmentDistance(a[i], a[i + 1], a[i + 2], a[i + 3], b[j], b[j + 1], b[j + 2], b[j + 3]));
                    if (best <= 0f) return 0f;
                }
            }

            return best;
        }

        private static float SegmentDistance(float ax, float az, float bx, float bz, float cx, float cz, float dx, float dz)
        {
            // Proper crossing first, then the four point-to-segment distances.
            float d1 = Cross(cx, cz, dx, dz, ax, az), d2 = Cross(cx, cz, dx, dz, bx, bz);
            float d3 = Cross(ax, az, bx, bz, cx, cz), d4 = Cross(ax, az, bx, bz, dx, dz);

            if (((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0)))
            {
                return 0f;
            }

            return Math.Min(Math.Min(PointSegment(ax, az, cx, cz, dx, dz), PointSegment(bx, bz, cx, cz, dx, dz)),
                            Math.Min(PointSegment(cx, cz, ax, az, bx, bz), PointSegment(dx, dz, ax, az, bx, bz)));
        }

        private static float Cross(float ax, float az, float bx, float bz, float px, float pz)
        {
            return (bx - ax) * (pz - az) - (bz - az) * (px - ax);
        }

        private static float PointSegment(float px, float pz, float ax, float az, float bx, float bz)
        {
            float vx = bx - ax, vz = bz - az, len = vx * vx + vz * vz;
            float t = len > 1e-8f ? Math.Max(0f, Math.Min(1f, ((px - ax) * vx + (pz - az) * vz) / len)) : 0f;
            float qx = ax + vx * t - px, qz = az + vz * t - pz;
            return (float)Math.Sqrt(qx * qx + qz * qz);
        }
    }
}
