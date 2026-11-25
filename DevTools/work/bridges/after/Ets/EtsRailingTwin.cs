using System;
using System.Collections.Generic;
using UnityEngine;

namespace SomniumSpace.Worlds.SomniumEuroTruck.Ets
{
    /// <summary>
    /// The second copy of a double-sided road railing, without the faces the first copy already draws.
    ///
    /// A road side whose railing is flagged double-sided (road flags bit 24 / 25) is built twice by
    /// EtsMapBuilder.BuildRailing: the model as it is and the model turned round, at the same offset. On a one-sided
    /// crash barrier that is the point - the rail on the other face of the posts - and the two copies barely touch
    /// (crash_barrier_nosidecoll.pmd, 565 of the 2,084 double-sided sides in the demo map: 0.001 m2 of shared faces per
    /// 45 m). But most railing models are already two-faced or symmetric across their own centre line, and turned
    /// round they land face on face on themselves: per 45 m of road, railing_01_ger 51 m2, railing_01_est_balt 132 m2,
    /// antinoise_wall_glass_metal_03_it 412 m2, it_road_1_deep_it (a whole bridge deck and both parapets) 1,412 m2 of
    /// coplanar, same-facing, overlapping triangles (probe DevTools/work/fences/probe P2 "dstest", every
    /// double-sided railing model in the map). Overlapping coplanar faces z-fight: the flashing.
    ///
    /// So the turned copy keeps only what the first copy does not already draw: a triangle is left out when at least
    /// half its area lies within 5 mm of, and facing the same way as, triangles of the first copy's part in the same
    /// slice. The test runs once per model, part pair and piece, on the straight slice (both copies are then bent along
    /// the road by the same BentRun, so what coincides straight coincides bent), and is cached.
    /// </summary>
    public static class EtsRailingTwin
    {
        public static int PiecesTrimmed, TrianglesDropped, TrianglesKept;
        private static bool _logged;

        private static readonly Dictionary<string, EtsModelPiece> Cache = new Dictionary<string, EtsModelPiece>(StringComparer.Ordinal);
        private static readonly EtsModelPiece Nothing = new EtsModelPiece();

        private struct Face
        {
            public Vector3 A, B, C, Normal;
            public float D;
        }

        /// <summary>
        /// The piece of the turned copy (part <paramref name="turnedPart"/>, piece <paramref name="pieceIndex"/>) with the
        /// triangles the first copy's part <paramref name="firstPart"/> already draws left out. The piece itself when
        /// nothing coincides, null when everything does. <paramref name="drawn"/> says which of the model's pieces the
        /// first copy draws.
        /// </summary>
        public static EtsModelPiece Uncovered(EtsModelData model, int turnedPart, int firstPart, int pieceIndex, float sliceStart, float sliceLength, Func<EtsModelPiece, bool> drawn)
        {
            string key = model.Path + "|" + turnedPart + "|" + firstPart + "|" + pieceIndex + "|" + sliceStart.ToString("0.000") + "|" + sliceLength.ToString("0.000");

            if (Cache.TryGetValue(key, out EtsModelPiece cached))
            {
                return ReferenceEquals(cached, Nothing) ? null : cached;
            }

            EtsModelPiece piece = model.Pieces[pieceIndex];
            EtsModelPiece result = piece;

            try
            {
                result = Trim(model, piece, firstPart, sliceStart, sliceLength, drawn);
            }
            catch (Exception e)
            {
                // Never cost the railing: keep the whole turned piece, as before.
                Debug.LogWarning("[SomniumEuroTruck] Railing twin test failed on " + model.Path + ": " + e.Message);
            }

            Cache[key] = result ?? Nothing;
            return result;
        }

        public static string Report()
        {
            return PiecesTrimmed + " turned railing pieces trimmed (" + TrianglesDropped + " triangles left out, " + TrianglesKept + " kept)";
        }

        private static EtsModelPiece Trim(EtsModelData model, EtsModelPiece piece, int firstPart, float sliceStart, float sliceLength, Func<EtsModelPiece, bool> drawn)
        {
            if (piece.Indices == null || piece.Positions == null || firstPart < 0 || firstPart >= model.Parts.Length)
            {
                return piece;
            }

            // Slice-local frame: lateral x, height y, distance along the slice. BentRun puts the first copy's game z at
            // (zMax - z) and the turned copy's at (z - zMin), with x negated (sliceStart = -zMax).
            float zMax = -sliceStart, zMin = zMax - sliceLength;
            var planes = new Dictionary<long, List<Face>>();
            EtsModelPart part = model.Parts[firstPart];

            for (int p = part.PieceStart; p < part.PieceStart + part.PieceCount && p < model.Pieces.Length; p++)
            {
                EtsModelPiece other = model.Pieces[p];

                if (other.Indices == null || other.Positions == null || !drawn(other))
                {
                    continue;
                }

                for (int i = 0; i + 2 < other.Indices.Length; i += 3)
                {
                    if (MakeFace(Original(other, other.Indices[i], zMax), Original(other, other.Indices[i + 1], zMax), Original(other, other.Indices[i + 2], zMax), out Face face))
                    {
                        long planeKey = PlaneKey(face.Normal, face.D, 0, 0, 0, 0);

                        if (!planes.TryGetValue(planeKey, out List<Face> list))
                        {
                            planes[planeKey] = list = new List<Face>();
                        }

                        list.Add(face);
                    }
                }
            }

            if (planes.Count == 0)
            {
                return piece;
            }

            var kept = new List<int>(piece.Indices.Length);
            int dropped = 0;

            for (int i = 0; i + 2 < piece.Indices.Length; i += 3)
            {
                if (MakeFace(Turned(piece, piece.Indices[i], zMin), Turned(piece, piece.Indices[i + 1], zMin), Turned(piece, piece.Indices[i + 2], zMin), out Face face) &&
                    Covered(face, planes))
                {
                    dropped++;
                    continue;
                }

                kept.Add(piece.Indices[i]);
                kept.Add(piece.Indices[i + 1]);
                kept.Add(piece.Indices[i + 2]);
            }

            if (dropped == 0)
            {
                return piece;
            }

            PiecesTrimmed++;
            TrianglesDropped += dropped;
            TrianglesKept += kept.Count / 3;

            if (!_logged)
            {
                _logged = true;
                Debug.Log("[SomniumEuroTruck] Double-sided railing: the turned copy leaves out the faces the first copy already draws (first: " +
                          model.Path + ", " + dropped + " of " + (piece.Indices.Length / 3) + " triangles of one piece).");
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
                Uv1 = piece.Uv1,
                Uvs = piece.Uvs,
                Colors = piece.Colors,
                Tangents = piece.Tangents,
                Indices = kept.ToArray(),
                BoundsMin = piece.BoundsMin,
                BoundsMax = piece.BoundsMax
            };
        }

        private static Vector3 Original(EtsModelPiece piece, int index, float zMax)
        {
            return new Vector3(piece.Positions[index * 3], piece.Positions[index * 3 + 1], zMax - piece.Positions[index * 3 + 2]);
        }

        private static Vector3 Turned(EtsModelPiece piece, int index, float zMin)
        {
            return new Vector3(-piece.Positions[index * 3], piece.Positions[index * 3 + 1], piece.Positions[index * 3 + 2] - zMin);
        }

        private static bool MakeFace(Vector3 a, Vector3 b, Vector3 c, out Face face)
        {
            Vector3 n = Vector3.Cross(b - a, c - a);
            face = default;

            if (n.sqrMagnitude < 1e-10f)
            {
                return false;
            }

            n.Normalize();
            face = new Face { A = a, B = b, C = c, Normal = n, D = Vector3.Dot(n, a) };
            return true;
        }

        /// <summary>A bucket per plane: the normal to 1/20 and the distance to 1 cm (neighbouring buckets are searched too).</summary>
        private static long PlaneKey(Vector3 n, float d, int dx, int dy, int dz, int dd)
        {
            long x = Mathf.RoundToInt(n.x * 20f) + dx + 64, y = Mathf.RoundToInt(n.y * 20f) + dy + 64, z = Mathf.RoundToInt(n.z * 20f) + dz + 64;
            long w = Mathf.FloorToInt(d * 100f) + dd + (1L << 30);
            return (x << 56) | (y << 48) | (z << 40) | (w & 0xFFFFFFFFFFL);
        }

        private static bool Covered(Face face, Dictionary<long, List<Face>> planes)
        {
            float area = 0.5f * Vector3.Cross(face.B - face.A, face.C - face.A).magnitude;

            if (area < 1e-6f)
            {
                return false;
            }

            float covered = 0f;

            for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
            for (int dz = -1; dz <= 1; dz++)
            for (int dd = -1; dd <= 1; dd++)
            {
                if (!planes.TryGetValue(PlaneKey(face.Normal, face.D, dx, dy, dz, dd), out List<Face> list))
                {
                    continue;
                }

                foreach (Face other in list)
                {
                    if (Vector3.Dot(face.Normal, other.Normal) < 0.999f || Mathf.Abs(face.D - other.D) > 0.005f)
                    {
                        continue;
                    }

                    covered += OverlapArea(face, other);

                    if (covered >= 0.5f * area)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static readonly List<Vector2> ClipA = new List<Vector2>(12), ClipB = new List<Vector2>(12);

        /// <summary>The area two coplanar triangles share, by clipping one with the other in the first one's plane.</summary>
        private static float OverlapArea(Face a, Face b)
        {
            Vector3 u = (a.B - a.A).normalized, v = Vector3.Cross(a.Normal, u);
            Vector2 a0 = Flat(a.A, a.A, u, v), a1 = Flat(a.B, a.A, u, v), a2 = Flat(a.C, a.A, u, v);
            ClipA.Clear();
            ClipA.Add(Flat(b.A, a.A, u, v));
            ClipA.Add(Flat(b.B, a.A, u, v));
            ClipA.Add(Flat(b.C, a.A, u, v));

            Vector2[] clip = Signed(a0, a1, a2) >= 0f ? new[] { a0, a1, a2 } : new[] { a0, a2, a1 };
            List<Vector2> subject = ClipA, output = ClipB;

            for (int e = 0; e < 3 && subject.Count > 0; e++)
            {
                Vector2 c0 = clip[e], c1 = clip[(e + 1) % 3];
                output.Clear();

                for (int k = 0; k < subject.Count; k++)
                {
                    Vector2 current = subject[k], previous = subject[(k + subject.Count - 1) % subject.Count];
                    float sc = Side(c0, c1, current), sp = Side(c0, c1, previous);

                    if (sc >= 0f)
                    {
                        if (sp < 0f) output.Add(Vector2.Lerp(previous, current, sp / (sp - sc)));
                        output.Add(current);
                    }
                    else if (sp >= 0f)
                    {
                        output.Add(Vector2.Lerp(previous, current, sp / (sp - sc)));
                    }
                }

                List<Vector2> swap = subject;
                subject = output;
                output = swap;
            }

            if (subject.Count < 3)
            {
                return 0f;
            }

            float twice = 0f;

            for (int k = 0; k < subject.Count; k++)
            {
                Vector2 p = subject[k], q = subject[(k + 1) % subject.Count];
                twice += p.x * q.y - p.y * q.x;
            }

            return Mathf.Abs(0.5f * twice);
        }

        private static Vector2 Flat(Vector3 p, Vector3 origin, Vector3 u, Vector3 v)
        {
            Vector3 d = p - origin;
            return new Vector2(Vector3.Dot(d, u), Vector3.Dot(d, v));
        }

        private static float Signed(Vector2 a, Vector2 b, Vector2 c)
        {
            return (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);
        }

        private static float Side(Vector2 c0, Vector2 c1, Vector2 p)
        {
            return (c1.x - c0.x) * (p.y - c0.y) - (c1.y - c0.y) * (p.x - c0.x);
        }
    }
}
