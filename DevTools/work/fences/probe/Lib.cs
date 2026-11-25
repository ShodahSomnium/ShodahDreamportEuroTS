// Fence-overlap probes: shared helpers. Offline replica of EtsRunBuilder.Lay / EtsMapBuilder.BuentRun geometry in
// the Unity frame, plus a coplanar-overlap scanner over world triangles.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SomniumSpace.Worlds.SomniumEuroTruck.Ets;

public struct D3
{
    public double x, y, z;
    public D3(double x, double y, double z) { this.x = x; this.y = y; this.z = z; }
    public static D3 operator +(D3 a, D3 b) => new D3(a.x + b.x, a.y + b.y, a.z + b.z);
    public static D3 operator -(D3 a, D3 b) => new D3(a.x - b.x, a.y - b.y, a.z - b.z);
    public static D3 operator *(D3 a, double s) => new D3(a.x * s, a.y * s, a.z * s);
    public static D3 operator *(double s, D3 a) => new D3(a.x * s, a.y * s, a.z * s);
    public double Dot(D3 b) => x * b.x + y * b.y + z * b.z;
    public D3 Cross(D3 b) => new D3(y * b.z - z * b.y, z * b.x - x * b.z, x * b.y - y * b.x);
    public double Len => Math.Sqrt(x * x + y * y + z * z);
    public D3 Norm { get { double l = Len; return l > 1e-12 ? this * (1 / l) : new D3(0, 0, 0); } }
    public override string ToString() => string.Format("({0:F2},{1:F2},{2:F2})", x, y, z);
}

public sealed class Tri
{
    public D3 a, b, c, n;
    public double d, area;
    public int group;   // one copy / one mesh
    public int item;    // one map item
    public string tag;
}

public static class Lib
{
    public static EtsFileSystem Fs;
    public static EtsMap Map;
    static readonly Dictionary<string, EtsModelData> models = new Dictionary<string, EtsModelData>();
    static readonly Dictionary<string, EtsMaterialDef> mats = new Dictionary<string, EtsMaterialDef>();

    public static void Mount()
    {
        Fs = EtsFileSystem.Mount("E:/SteamLibrary/steamapps/common/Euro Truck Simulator 2 Demo", out _);
        Map = new EtsMap(Fs);
    }

    public static List<EtsMapSector> Sectors(double gx, double gz, double r)
    {
        var list = new List<EtsMapSector>();
        int x0 = (int)Math.Floor((gx - r - 500) / 4000), x1 = (int)Math.Floor((gx + r + 500) / 4000);
        int z0 = (int)Math.Floor((gz - r - 500) / 4000), z1 = (int)Math.Floor((gz + r + 500) / 4000);
        for (int x = x0 - 1; x <= x1 + 1; x++) for (int z = z0 - 1; z <= z1 + 1; z++) { var s = Map.GetSector(x, z); if (s != null && x >= x0 && x <= x1 && z >= z0 && z <= z1) list.Add(s); }
        return list;
    }

    public static EtsModelData Model(string path)
    {
        if (path == null) return null;
        if (models.TryGetValue(path, out var m)) return m;
        try { m = EtsModelData.Parse(Fs.Read(path), Fs.Read(path.Substring(0, path.Length - 4) + ".pmg"), path); } catch { m = null; }
        models[path] = m;
        return m;
    }

    public static EtsMaterialDef Mat(string path)
    {
        if (path == null) return null;
        if (mats.TryGetValue(path, out var d)) return d;
        try { byte[] b = Fs.Read(path); d = b == null ? null : EtsMaterialDef.Parse(Encoding.UTF8.GetString(b), path); } catch { d = null; }
        mats[path] = d;
        return d;
    }

    /// <summary>Whether a piece is drawn (as EtsModelBuilder / BuildRailingCopy decide).</summary>
    public static bool Drawn(EtsModelData m, EtsModelPiece piece, int look = 0)
    {
        string[] lm = look < m.LookMaterials.Length ? m.LookMaterials[look] : new string[0];
        string mp = piece.MaterialIndex < lm.Length ? lm[piece.MaterialIndex] : null;
        var def = Mat(mp);
        if (def == null || def.Effect == null) return false;
        if (def.EffectHas("shadowonly") || def.EffectHas("shadowmap") || def.EffectHas("fakeshadow") || def.EffectHas(".night") || def.EffectHas("mlaaweight")) return false;
        if (def.Effect.StartsWith("eut2.none", StringComparison.Ordinal)) return false;
        return true;
    }

    public static string Effect(EtsModelData m, EtsModelPiece piece, int look = 0)
    {
        string[] lm = look < m.LookMaterials.Length ? m.LookMaterials[look] : new string[0];
        string mp = piece.MaterialIndex < lm.Length ? lm[piece.MaterialIndex] : null;
        var def = Mat(mp);
        return def == null ? "?" : def.Effect;
    }

    public static EtsModelPart Part(EtsModelData m, string name)
    {
        if (name == null) return null;
        foreach (var p in m.Parts) if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p;
        return null;
    }

    public static void ZRange(EtsModelData m, string name, out float lo, out float hi)
    {
        lo = float.MaxValue; hi = float.MinValue;
        var part = Part(m, name);
        if (part != null)
            for (int p = part.PieceStart; p < part.PieceStart + part.PieceCount && p < m.Pieces.Length; p++)
            { lo = Math.Min(lo, m.Pieces[p].BoundsMin[2]); hi = Math.Max(hi, m.Pieces[p].BoundsMax[2]); }
        if (hi < lo) lo = hi = 0;
    }

    // ---------------------------------------------------------------- Unity-frame helpers (as the world code)

    public static D3 ToUnity(float[] g) => new D3(g[0], g[1], -g[2]);
    public static D3 Hermite(D3 p0, D3 t0, D3 p1, D3 t1, double s)
    {
        double s2 = s * s, s3 = s2 * s;
        return (2 * s3 - 3 * s2 + 1) * p0 + (s3 - 2 * s2 + s) * t0 + (-2 * s3 + 3 * s2) * p1 + (s3 - s2) * t1;
    }
    public static D3 HermiteTangent(D3 p0, D3 t0, D3 p1, D3 t1, double s)
    {
        double s2 = s * s;
        return (6 * s2 - 6 * s) * p0 + (3 * s2 - 4 * s + 1) * t0 + (-6 * s2 + 6 * s) * p1 + (3 * s2 - 2 * s) * t1;
    }

    /// <summary>Unity's LookRotation basis (x, y, z axes) for forward f and up = world up.</summary>
    public static void Look(D3 f, out D3 X, out D3 Y, out D3 Z)
    {
        Z = f.Norm;
        X = new D3(0, 1, 0).Cross(Z).Norm;
        if (X.Len < 0.5) X = new D3(1, 0, 0);
        Y = Z.Cross(X);
    }

    public static void Path(EtsMapNode start, EtsMapNode end, double length, D3 centre, out D3[] pts, out double[] arc, out D3 p0, out D3 p1)
    {
        p0 = ToUnity(start.Position) - centre; p1 = ToUnity(end.Position) - centre;
        D3 t0 = ToUnity(start.Forward()).Norm * length, t1 = ToUnity(end.Forward()).Norm * length;
        const int S = 64;
        pts = new D3[S + 1]; arc = new double[S + 1];
        for (int k = 0; k <= S; k++) { pts[k] = Hermite(p0, t0, p1, t1, (double)k / S); arc[k] = k == 0 ? 0 : arc[k - 1] + (pts[k] - pts[k - 1]).Len; }
    }

    public static void AtArc(D3[] pts, double[] arc, double dist, D3 chord, out D3 point, out D3 fwd)
    {
        int last = pts.Length - 1; double total = arc[last];
        double want = Math.Max(0, Math.Min(total, dist)); int k = 0;
        while (k < last - 1 && arc[k + 1] < want) k++;
        double span = arc[k + 1] - arc[k]; double t = span > 1e-5 ? (want - arc[k]) / span : 0;
        point = pts[k] + (pts[k + 1] - pts[k]) * t;
        fwd = pts[k + 1] - pts[k];
        if (fwd.Len < 1e-4) fwd = chord;
        fwd = fwd.Len < 1e-4 ? new D3(0, 0, 1) : fwd.Norm;
    }

    /// <summary>Triangles of one model part placed by a transform (origin, basis, scale), drawn pieces only.</summary>
    public static void AddPart(List<Tri> into, EtsModelData m, string part, D3 origin, D3 X, D3 Y, D3 Z, double sx, double sy, double sz, int group, int item, string tag)
    {
        var p = Part(m, part);
        if (p == null) return;
        for (int q = p.PieceStart; q < p.PieceStart + p.PieceCount && q < m.Pieces.Length; q++)
        {
            var piece = m.Pieces[q];
            if (!Drawn(m, piece)) continue;
            var P = piece.Positions;
            Func<int, D3> V = i => origin + X * (P[i * 3] * sx) + Y * (P[i * 3 + 1] * sy) + Z * (-P[i * 3 + 2] * sz);
            for (int i = 0; i + 2 < piece.Indices.Length; i += 3)
                AddTri(into, V(piece.Indices[i]), V(piece.Indices[i + 1]), V(piece.Indices[i + 2]), group, item, tag);
        }
    }

    public static void AddTri(List<Tri> into, D3 a, D3 b, D3 c, int group, int item, string tag)
    {
        D3 n = (b - a).Cross(c - a);
        double l = n.Len;
        if (l < 2e-5) return;
        n = n * (1 / l);
        into.Add(new Tri { a = a, b = b, c = c, n = n, d = n.Dot(a), area = 0.5 * l, group = group, item = item, tag = tag });
    }

    // ---------------------------------------------------------------- the scanner

    public sealed class Overlap
    {
        public int Pairs, SameItemPairs, OppositePairs;
        public double Area, SameItemArea;
        public readonly Dictionary<string, double> ByTag = new Dictionary<string, double>();
        public readonly Dictionary<string, int> ByTagPairs = new Dictionary<string, int>();
        public readonly List<string> Samples = new List<string>();
        public readonly Dictionary<string, double> ByPair = new Dictionary<string, double>();
        public readonly Dictionary<string, string> PairWhere = new Dictionary<string, string>();
    }

    /// <summary>
    /// Pairs of triangles from DIFFERENT groups that face the same way, lie within eps of one plane and overlap by
    /// more than minArea m2 - what z-fights. Opposite-facing coplanar pairs are counted apart (only a two-sided
    /// material would show them).
    /// </summary>
    public static Overlap Scan(List<Tri> tris, double eps = 0.005, double minArea = 0.0005, double cosTol = 0.9995)
    {
        var res = new Overlap();
        const double cell = 1.0;
        var grid = new Dictionary<long, List<int>>();
        Func<int, int, int, long> key = (x, y, z) => ((long)(x & 0x1FFFFF) << 42) | ((long)(y & 0x1FFFFF) << 21) | (long)(z & 0x1FFFFF);
        for (int i = 0; i < tris.Count; i++)
        {
            var t = tris[i];
            int x0 = (int)Math.Floor(Math.Min(t.a.x, Math.Min(t.b.x, t.c.x)) / cell), x1 = (int)Math.Floor(Math.Max(t.a.x, Math.Max(t.b.x, t.c.x)) / cell);
            int y0 = (int)Math.Floor(Math.Min(t.a.y, Math.Min(t.b.y, t.c.y)) / cell), y1 = (int)Math.Floor(Math.Max(t.a.y, Math.Max(t.b.y, t.c.y)) / cell);
            int z0 = (int)Math.Floor(Math.Min(t.a.z, Math.Min(t.b.z, t.c.z)) / cell), z1 = (int)Math.Floor(Math.Max(t.a.z, Math.Max(t.b.z, t.c.z)) / cell);
            if ((long)(x1 - x0 + 1) * (y1 - y0 + 1) * (z1 - z0 + 1) > 4000) continue;
            for (int x = x0; x <= x1; x++) for (int y = y0; y <= y1; y++) for (int z = z0; z <= z1; z++)
                    { long k = key(x, y, z); if (!grid.TryGetValue(k, out var l)) grid[k] = l = new List<int>(); l.Add(i); }
        }
        var done = new HashSet<long>();
        foreach (var l in grid.Values)
        {
            for (int ii = 0; ii < l.Count; ii++)
                for (int jj = ii + 1; jj < l.Count; jj++)
                {
                    int i = l[ii], j = l[jj];
                    var A = tris[i]; var B = tris[j];
                    if (A.group == B.group) continue;
                    double dot = A.n.Dot(B.n);
                    if (Math.Abs(dot) < cosTol) continue;
                    if (Math.Abs(A.n.Dot(B.a) - A.d) > eps || Math.Abs(A.n.Dot(B.b) - A.d) > eps || Math.Abs(A.n.Dot(B.c) - A.d) > eps) continue;
                    long pk = i < j ? ((long)i << 32) | (uint)j : ((long)j << 32) | (uint)i;
                    if (!done.Add(pk)) continue;
                    double area = OverlapArea(A, B);
                    if (area < minArea) continue;
                    if (dot < 0) { res.OppositePairs++; continue; }
                    res.Pairs++; res.Area += area;
                    bool same = A.item == B.item;
                    if (same) { res.SameItemPairs++; res.SameItemArea += area; }
                    string tg = (same ? "same:" : "cross:") + A.tag + (same ? "" : " | " + B.tag);
                    res.ByTag[tg] = (res.ByTag.TryGetValue(tg, out double v) ? v : 0) + area;
                    res.ByTagPairs[tg] = (res.ByTagPairs.TryGetValue(tg, out int c) ? c : 0) + 1;
                    string pkey = "items " + Math.Min(A.item, B.item) + "/" + Math.Max(A.item, B.item) + " " + tg; res.ByPair[pkey] = (res.ByPair.TryGetValue(pkey, out double pv) ? pv : 0) + area; if (!res.PairWhere.ContainsKey(pkey)) res.PairWhere[pkey] = "@" + ((A.a + A.b + A.c) * (1.0 / 3)) + " n " + A.n;
                    if (res.Samples.Count < 400) res.Samples.Add("items " + A.item + "/" + B.item + " groups " + A.group + "/" + B.group + " " + tg + " @" + ((A.a + A.b + A.c) * (1.0 / 3)) + " area " + area.ToString("F4") + " n " + A.n);
                }
        }
        return res;
    }

    /// <summary>Area of the overlap of two (near) coplanar triangles, in A's plane.</summary>
    public static double OverlapArea(Tri A, Tri B)
    {
        D3 u = (A.b - A.a).Norm, v = A.n.Cross(u);
        Func<D3, double[]> P = p => new[] { (p - A.a).Dot(u), (p - A.a).Dot(v) };
        var subject = new List<double[]> { P(B.a), P(B.b), P(B.c) };
        var clip = new List<double[]> { P(A.a), P(A.b), P(A.c) };
        if (Signed(clip) < 0) clip.Reverse();
        if (Signed(subject) < 0) subject.Reverse();
        for (int e = 0; e < 3 && subject.Count > 0; e++)
        {
            var c0 = clip[e]; var c1 = clip[(e + 1) % 3];
            var input = subject; subject = new List<double[]>();
            Func<double[], double> side = p => (c1[0] - c0[0]) * (p[1] - c0[1]) - (c1[1] - c0[1]) * (p[0] - c0[0]);
            for (int k = 0; k < input.Count; k++)
            {
                var cur = input[k]; var prev = input[(k + input.Count - 1) % input.Count];
                double sc = side(cur), sp = side(prev);
                if (sc >= 0)
                {
                    if (sp < 0) subject.Add(Lerp(prev, cur, sp / (sp - sc)));
                    subject.Add(cur);
                }
                else if (sp >= 0) subject.Add(Lerp(prev, cur, sp / (sp - sc)));
            }
        }
        return subject.Count < 3 ? 0 : Math.Abs(Signed(subject));
    }

    static double[] Lerp(double[] a, double[] b, double t) => new[] { a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t };
    static double Signed(List<double[]> p) { double s = 0; for (int i = 0; i < p.Count; i++) { var a = p[i]; var b = p[(i + 1) % p.Count]; s += a[0] * b[1] - a[1] * b[0]; } return 0.5 * s; }

    public static string Report(Overlap o, int top = 25)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Format("overlapping coplanar same-facing pairs: {0} ({1:F2} m2); within one item (joints): {2} ({3:F2} m2); opposite-facing coplanar pairs: {4}",
            o.Pairs, o.Area, o.SameItemPairs, o.SameItemArea, o.OppositePairs));
        foreach (var kv in o.ByTag.OrderByDescending(k => k.Value).Take(top))
            sb.AppendLine(string.Format("   {0,8:F3} m2 {1,6} pairs  {2}", kv.Value, o.ByTagPairs[kv.Key], kv.Key));
        return sb.ToString();
    }
}
