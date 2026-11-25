// P2: offline replica of how the world lays runs (EtsRunBuilder) and road railings (EtsMapBuilder.BuildRailingCopy)
// round a point, then the coplanar-overlap scan. Usage: P2.exe gx gz radius [variant]
//   variant "cur" = the code as it is (2026-10-04 snapshot)
using System;
using System.Collections.Generic;
using System.Linq;
using SomniumSpace.Worlds.SomniumEuroTruck.Ets;

static class P2
{
    static string Variant = "cur";
    static List<Tri> tris = new List<Tri>();
    static int group = 0;
    static D3 centre; static int Runs, BentRunsN;
    static List<string> log = new List<string>();

    static void Main(string[] a)
    {
        double gx = double.Parse(a[0]), gz = double.Parse(a[1]), r = double.Parse(a[2]);
        if (a.Length > 3) Variant = a[3];
        bool verbose = a.Length > 4;
        Lib.Mount(); typeof(EtsRailingTwin).GetField("_logged", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).SetValue(null, true);
        if (Variant == "dstest" || Variant == "dstwin")
        {
            var use = new Dictionary<ulong, int>(); Lib.Map.Railing(1);
            foreach (var name in Lib.Map.AvailableSectors.ToList())
            {
                int sx = int.Parse(name.Substring(3, 5)), sz = int.Parse(name.Substring(8, 5));
                EtsMapSector s; try { s = EtsMapSector.Parse(sx, sz, Lib.Fs.Read(EtsMapSector.FileName(sx, sz, "base")), Lib.Fs.Read(EtsMapSector.FileName(sx, sz, "aux")), Lib.Fs.Read(EtsMapSector.FileName(sx, sz, "data"))); } catch { continue; }
                if (s == null) continue;
                foreach (var rd in s.Roads) if (rd.Railings != null) foreach (var rl in rd.Railings) if (rl.Left ? rd.LeftRailingDoubleSided : rd.RightRailingDoubleSided) use[rl.Model] = (use.TryGetValue(rl.Model, out int u) ? u : 0) + 1;
            }
            foreach (var kv in use.OrderByDescending(k => k.Value))
            {
                var unit = Lib.Map.Railing(kv.Key); string path = unit?.GetString("model_desc"); var m = Lib.Model(path);
                if (m == null) continue; var parts = PartsOf(m, 45f); if (parts == null) continue;
                tris.Clear();
                D3 q0 = new D3(0, 0, 0), q1 = new D3(0, 0, 45), tq = new D3(0, 0, 45);
                RailingCopy(m, parts, 3f, false, true, true, 45f, q0, tq, q1, tq, 1, "a");
                RailingCopy(m, parts, 3f, true, true, true, 45f, q0, tq, q1, tq, 2, "b", Variant == "dstwin");
                var od = Lib.Scan(tris);
                double cross = od.Area - od.SameItemArea; int crossPairs = od.Pairs - od.SameItemPairs;
                Console.WriteLine(string.Format("{0,5} DS sides  {1,-60} copy-vs-turned-copy overlap {2,5} pairs {3,8:F3} m2 per 45 m", kv.Value, path, crossPairs, cross));
            }
            return;
        }
        var sectors = Lib.Sectors(gx, gz, r);
        centre = new D3(gx, 0, -gz);
        int curves = 0, buildings = 0, roads = 0, item = 0;
        foreach (var s in sectors)
        {
            foreach (var c in s.Curves)
            {
                var n = Lib.Map.FindNode(c.NodeUid);
                if (n == null || Sq(n.Position[0] - gx) + Sq(n.Position[2] - gz) > r * r) continue;
                item++; if (Curve(c, item)) curves++;
            }
            foreach (var b in s.Buildings)
            {
                var n = Lib.Map.FindNode(b.StartNode);
                if (n == null || Sq(n.Position[0] - gx) + Sq(n.Position[2] - gz) > r * r) continue;
                item++; if (Building(b, item)) buildings++;
            }
            foreach (var rd in s.Roads)
            {
                var n = Lib.Map.FindNode(rd.StartNode);
                if (n == null || rd.Railings == null || Sq(n.Position[0] - gx) + Sq(n.Position[2] - gz) > r * r) continue;
                item++; if (Railings(rd, item)) roads++;
            }
        }
        Console.WriteLine("bent runs " + BentRunsN + " of " + Runs); Console.WriteLine("variant " + Variant + ": " + curves + " curves, " + buildings + " buildings, " + roads + " railed roads, " + tris.Count + " triangles, " + group + " copies");
        if (Variant.StartsWith("kdop"))
        {
            foreach (int typ in new[] { 2, 44 }) foreach (double minTurn in new[] { 0.0, 10.0, 30.0 })
            {
                var rr = new List<double>(); var bb = new List<double>(); int bentWins = 0, n = 0;
                foreach (var kv in Items)
                {
                    if (kv.Value.Type != typ || !Turn.TryGetValue(kv.Key, out double tn) || tn < minTurn || !KRigid.ContainsKey(kv.Key)) continue;
                    var it = kv.Value; double er = 0, eb = 0;
                    foreach (int ax in new[] { 0, 2, 3, 4 })
                    {
                        er = Math.Max(er, Math.Max(Math.Abs(KRigid[kv.Key][ax] - 0.125 - it.KdopMin[ax]), Math.Abs(KRigid[kv.Key][ax + 5] + 0.125 - it.KdopMax[ax])));
                        eb = Math.Max(eb, Math.Max(Math.Abs(KBent[kv.Key][ax] - 0.125 - it.KdopMin[ax]), Math.Abs(KBent[kv.Key][ax + 5] + 0.125 - it.KdopMax[ax])));
                    }
                    rr.Add(er); bb.Add(eb); n++; if (eb < er - 0.01) bentWins++;
                    if (minTurn >= 60 && n <= 12) Console.WriteLine(string.Format("   item {0} {1} turn {2:F0} rigid err {3:F3} bent err {4:F3}", kv.Key, it.Type, tn, er, eb));
                }
                rr.Sort(); bb.Sort();
                if (n > 0) Console.WriteLine("type " + typ + " " + string.Format("kdop turn>={0}: {1} items; worst-axis residual median rigid {2:F3} bent {3:F3}; p90 rigid {4:F3} bent {5:F3}; bent closer on {6}",
                    minTurn, n, rr[n / 2], bb[n / 2], rr[n * 9 / 10], bb[n * 9 / 10], bentWins));
            }
        }
        var o = Lib.Scan(tris);
        Console.WriteLine(Lib.Report(o, 40));
        if (verbose) { foreach (var l in log) Console.WriteLine(l); foreach (var kv in o.ByPair.OrderByDescending(k => k.Value).Take(1500)) Console.WriteLine(string.Format("  PAIR {0,9:F3} m2 {1} {2}", kv.Value, kv.Key, o.PairWhere[kv.Key])); }
    }

    static double Sq(double v) => v * v;

    // ------------------------------------------------------------------ curves and buildings (EtsRunBuilder)

    static bool Curve(EtsMapCurve item, int id)
    {
        var start = Lib.Map.FindNode(item.NodeUid); var end = Lib.Map.FindNode(item.ForwardNodeUid);
        if (start == null || end == null) return false;
        var def = Lib.Map.CurveModel(item.ModelToken);
        string path = def?.GetString("model_desc");
        var m = Lib.Model(path);
        if (m == null) return false;
        if (m.Locators.Any(l => l.Name != null && l.Name.StartsWith("w_") && l.Name.Length >= 5)) return false; // pole lines
        string variation = EtsToken.Decode(item.VariationToken);
        string[] parts = Variation(def, variation, out int vi) ?? new[] { "center1" };
        string element = null; float step = 0; string flat = null;
        foreach (string nm in parts)
        {
            var f = Lib.Part(m, nm); if (f == null) continue;
            float len = PartLength(m, f.Name);
            if (len > 0.01f) { element = f.Name; step = len; break; }
            flat = flat ?? f.Name;
        }
        if (element == null) { element = ShortestCentre(m); step = PartLength(m, element); }
        if (element == null && flat != null) { element = flat; step = 0; }
        float partLen = step;
        if (item.FixedStep > 0.01f) step = item.FixedStep;
        else if (def.GetFloat("fixed_step", 0) > 0.01f) step = def.GetFloat("fixed_step", 0);
        if (element == null || step <= 0.01f) return false;
        string first = Cap(def, "start_part", vi, EtsToken.Decode(item.FirstPart), m);
        string last = Cap(def, "end_part", vi, EtsToken.Decode(item.LastPart), m);
        float scale = item.Scale > 0.01f && item.Scale < 50f ? item.Scale : 1f;
        log.Add(string.Format("curve {0} {1} var {2} el {3} partLen {4:F2} step {5:F2} (item fs {6:F2} def fs {7:F2}) first {8} last {9} len {10:F1} stretch {11:F2} scale {12:F2} sub {13}",
            id, path, variation, element, partLen, step, item.FixedStep, def.GetFloat("fixed_step", 0), first, last, item.Length, item.Stretch, scale, item.SubcurveFlags));
        Items[id] = item; Lay(m, start, end, item.Length, item.Stretch, item.Heights, step, element, first, last, scale, true, id, "crv " + Short(path) + ":" + element);
        return true;
    }

    static bool Building(EtsMapBuilding item, int id)
    {
        var start = Lib.Map.FindNode(item.StartNode); var end = Lib.Map.FindNode(item.EndNode);
        if (start == null || end == null) return false;
        var scheme = Lib.Map.BuildingScheme(item.SchemeToken);
        var models = scheme?.GetArray("models");
        if (models == null || models.Count == 0) return false;
        var def = Lib.Map.BuildingModel(models[0].Trim());
        string path = def?.GetString("model_desc");
        var m = Lib.Model(path);
        if (m == null) return false;
        float width = def.GetFloat("width", -1);
        bool wide = def.GetFloat("use_15m_variant", 0) > 0.5f;
        string c = wide ? Lib.Part(m, "center15m")?.Name : null;
        c = c ?? Lib.Part(m, "center")?.Name ?? ShortestCentre(m) ?? BasePart(m, item.Seed);
        float step = width > 0.01f ? width : PartLength(m, c);
        if (step <= 0.01f) return false;
        log.Add(string.Format("bld {0} {1} el {2} partLen {3:F2} width {4:F2} start {5} end {6} len {7:F1} stretch {8:F2}", id, path, c, PartLength(m, c), width, Lib.Part(m, "start")?.Name, Lib.Part(m, "end")?.Name, item.Length, item.Stretch));
        Items[id] = item; Lay(m, start, end, item.Length, item.Stretch, item.Heights, step, c, Lib.Part(m, "start")?.Name, Lib.Part(m, "end")?.Name, 1f, false, id, "bld " + Short(path) + ":" + c);
        return true;
    }

    static string Short(string p) { int i = p.LastIndexOf('/'); return i >= 0 ? p.Substring(i + 1) : p; }

    public static Dictionary<int, double[]> KRigid = new Dictionary<int, double[]>(), KBent = new Dictionary<int, double[]>();
    public static Dictionary<int, EtsMapItem> Items = new Dictionary<int, EtsMapItem>();
    public static Dictionary<int, double> Turn = new Dictionary<int, double>();

    static void Grow(Dictionary<int, double[]> k, int id, D3 u)
    {
        double x = u.x + centre.x, y = u.y, z = -(u.z + centre.z);
        if (!k.TryGetValue(id, out var b)) { b = new double[10]; for (int i = 0; i < 5; i++) { b[i] = 1e18; b[i + 5] = -1e18; } k[id] = b; }
        double[] v = { x, y, z, (x + z) / 2, (x - z) / 2 };
        for (int i = 0; i < 5; i++) { b[i] = Math.Min(b[i], v[i]); b[i + 5] = Math.Max(b[i + 5], v[i]); }
    }

    static void Lay(EtsModelData m, EtsMapNode start, EtsMapNode end, float length, float stretch, float[] heights, float step, string centrePart, string first, string last, float scale, bool roundUp, int id, string tag)
    {
        float span = stretch > 0.01f && stretch < 50f ? stretch : 1f;
        bool bend = Variant.Contains("bend") || Variant == "kdop" || Variant == "kdopcaps";
        bool ownCaps = Variant.Contains("caps") || Variant.StartsWith("final");
        // slots: part, distance of the slot's middle, the part's own z scale
        var slots = new List<Tuple<string, double, double>>();
        if (!ownCaps)
        {
            float raw = length / (step * span);
            int count = Math.Max(1, roundUp ? (int)Math.Ceiling(raw - 0.001f) : (int)Math.Round(raw, MidpointRounding.ToEven));
            if (count > 200) count = 200;
            float spacing = length / count;
            float along = spacing / (step * span);
            for (int i = 0; i < count; i++)
            {
                string part = centrePart;
                if (i == 0 && first != null) part = first;
                else if (i == count - 1 && last != null && count > 1) part = last;
                slots.Add(Tuple.Create(part, (i + 0.5) * spacing, (double)scale * along));
            }
        }
        else
        {
            // the caps at their own lengths, the centre part repeated between, all stretched alike to the item's length
            double ls = first != null ? PartLength(m, first) : 0, le = last != null ? PartLength(m, last) : 0;
            double free = length / span - (ls + le) * scale;
            int n = (int)Math.Max(0, roundUp ? Math.Ceiling(free / (step * scale) - 0.001) : Math.Round(free / (step * scale), MidpointRounding.ToEven));
            if (n == 0 && ls + le <= 0.01) n = 1;
            if (n > 200) n = 200;
            double nominal = (ls + le) * scale + n * step * scale;
            double k = length / nominal;           // metres of path per nominal metre
            double cursor = 0;
            var seq = new List<Tuple<string, double>>();
            if (first != null) seq.Add(Tuple.Create(first, ls));
            for (int i = 0; i < n; i++) seq.Add(Tuple.Create(centrePart, (double)step));
            if (last != null) seq.Add(Tuple.Create(last, le));
            foreach (var s in seq)
            {
                double ext = s.Item2 * scale * k;
                double pl = PartLength(m, s.Item1);
                // a part shorter or longer than its nominal step (fixed_step) keeps its own centre on the slot's
                slots.Add(Tuple.Create(s.Item1, cursor + 0.5 * ext, scale * k * (s.Item2 > 0.01 ? 1.0 : 1.0)));
                cursor += ext;
            }
        }
        Lib.Path(start, end, length, centre, out D3[] pts, out double[] arc, out D3 p0, out D3 p1);
        { var f0 = (pts[1] - pts[0]).Norm; var f1 = (pts[pts.Length - 1] - pts[pts.Length - 2]).Norm; Turn[id] = Math.Acos(Math.Max(-1, Math.Min(1, f0.x * f1.x + f0.z * f1.z))) * 180 / Math.PI; }
        if (Variant.StartsWith("final")) { double turning = 0; for (int q = 2; q < pts.Length; q++) { D3 a1 = pts[q - 1] - pts[q - 2], a2 = pts[q] - pts[q - 1]; turning += Math.Atan2(a1.Cross(a2).Len, a1.Dot(a2)) * 180 / Math.PI; } double th = Variant == "final" ? 0.2 : double.Parse(Variant.Substring(5), System.Globalization.CultureInfo.InvariantCulture); bend = turning > th; Runs++; if (bend) BentRunsN++; }
        for (int i = 0; i < slots.Count; i++)
        {
            string part = slots[i].Item1; double middle = slots[i].Item2, zs = slots[i].Item3;
            var pp = Lib.Part(m, part);
            if (pp == null) continue;
            Lib.ZRange(m, part, out float zMin, out float zMax);
            double mid = 0.5 * (zMin + zMax);
            double h = heights != null && i < heights.Length ? heights[i] : 0;
            Lib.AtArc(pts, arc, middle, p1 - p0, out D3 point, out D3 fwd);
            point = point - fwd * (mid * zs);
            point.y += h;
            Lib.Look(fwd * -1, out D3 X, out D3 Y, out D3 Z);
            int g = group++;
            for (int q = pp.PieceStart; q < pp.PieceStart + pp.PieceCount && q < m.Pieces.Length; q++)
            {
                var piece = m.Pieces[q]; var P = piece.Positions;
                bool drawn = Lib.Drawn(m, piece);
                var R = new D3[piece.VertexCount]; var B = new D3[piece.VertexCount];
                for (int v = 0; v < piece.VertexCount; v++)
                {
                    double x = P[v * 3], y = P[v * 3 + 1], zg = P[v * 3 + 2];
                    R[v] = point + X * (x * scale) + Y * (y * scale) + Z * (-zg * zs);
                    double d = middle + (zg - mid) * zs;
                    double total = arc[arc.Length - 1];
                    Lib.AtArc(pts, arc, d, p1 - p0, out D3 bp, out D3 bf);
                    if (d < 0) bp = bp + bf * d; else if (d > total) bp = bp + bf * (d - total);
                    Lib.Look(bf * -1, out D3 bX, out D3 bY, out D3 bZ);
                    B[v] = bp + bX * (x * scale) + bY * (y * scale); B[v].y += h;
                    Grow(KRigid, id, R[v]); Grow(KBent, id, B[v]);
                }
                if (!drawn) continue;
                var V = bend ? B : R;
                for (int t = 0; t + 2 < piece.Indices.Length; t += 3)
                    Lib.AddTri(tris, V[piece.Indices[t]], V[piece.Indices[t + 1]], V[piece.Indices[t + 2]], g, id, tag);
            }
        }
    }

    static string[] Variation(EtsSiiUnit def, string name, out int index)
    {
        index = 0; var entries = def.GetArray("variation"); if (entries == null) return null;
        for (int i = 0; i < entries.Count; i++)
        {
            string[] f = entries[i].Split('|');
            if (f.Length == 0 || !string.Equals(f[0].Trim(), name, StringComparison.OrdinalIgnoreCase)) continue;
            index = i; if (f.Length < 2) return null;
            string head = f[1]; int w = head.IndexOf(':'); if (w >= 0) head = head.Substring(0, w);
            return head.Split(',').Select(x => x.Trim()).ToArray();
        }
        return null;
    }

    static string Cap(EtsSiiUnit def, string key, int index, string wanted, EtsModelData m)
    {
        if (string.IsNullOrEmpty(wanted) || wanted == "0") return null;
        return Lib.Part(m, wanted)?.Name;
    }

    static float PartLength(EtsModelData m, string name) { if (name == null) return 0; Lib.ZRange(m, name, out float lo, out float hi); return hi > lo ? hi - lo : 0; }

    static string ShortestCentre(EtsModelData m)
    {
        string best = null; float sh = float.MaxValue;
        foreach (var p in m.Parts)
        {
            string n = p.Name ?? "";
            if (!n.StartsWith("center", StringComparison.OrdinalIgnoreCase) || n.IndexOf("col", StringComparison.OrdinalIgnoreCase) >= 0) continue;
            float l = PartLength(m, n); if (l > 0.01f && l < sh) { sh = l; best = p.Name; }
        }
        return best;
    }

    static string BasePart(EtsModelData m, uint seed)
    {
        var b = new List<string>();
        for (int i = 0; i < 16; i++) { var p = Lib.Part(m, "base" + i); if (p == null) break; b.Add(p.Name); }
        return b.Count == 0 ? null : b[(int)(seed % (uint)b.Count)];
    }

    // ------------------------------------------------------------------ railings (EtsMapBuilder)

    static bool Railings(EtsMapRoad road, int id)
    {
        var start = Lib.Map.FindNode(road.StartNode); var end = Lib.Map.FindNode(road.EndNode);
        if (start == null || end == null || road.Length < 1f) return false;
        D3 p0 = Lib.ToUnity(start.Position) - centre, p1 = Lib.ToUnity(end.Position) - centre;
        D3 t0 = Lib.ToUnity(start.Forward()).Norm * road.Length, t1 = Lib.ToUnity(end.Forward()).Norm * road.Length;
        var look = Lib.Map.RoadLook(road.LookToken);
        float roadOffset = look != null ? look.GetFloat("road_offset", 0) : 0;
        bool any = false;
        foreach (var railing in road.Railings)
        {
            var unit = Lib.Map.Railing(railing.Model);
            string path = unit?.GetString("model_desc");
            var m = Lib.Model(path);
            if (m == null) continue;
            var parts = PartsOf(m, road.Length);
            if (parts == null) continue;
            bool inverted = railing.Left ? road.LeftRailingInverted : road.RightRailingInverted;
            bool centreOnly = railing.Left ? road.LeftRailingCenterOnly : road.RightRailingCenterOnly;
            bool doubleSided = railing.Left ? road.LeftRailingDoubleSided : road.RightRailingDoubleSided;
            float origin = roadOffset + railing.Offset;
            float lateral = railing.Left ? -origin : origin;
            bool turned = railing.Left != inverted;
            bool capStart = !centreOnly && !Continues(road, road.StartNode, railing.Model);
            bool capEnd = !centreOnly && !Continues(road, road.EndNode, railing.Model);
            string tag = "rail " + Short(path) + (doubleSided ? " DS" : "");
            log.Add(string.Format("road {0} {1:X16} len {2:F1} railing {3} {4} slot {5} off {6:F2} lateral {7:F2} turned {8} DS {9} centreOnly {10} caps {11}/{12}",
                id, road.Uid, road.Length, path, railing.Left ? "L" : "R", railing.Slot, railing.Offset, lateral, turned, doubleSided, centreOnly, capStart, capEnd));
            RailingCopy(m, parts, lateral, turned, capStart, capEnd, road.Length, p0, t0, p1, t1, id, tag);
            if (doubleSided) RailingCopy(m, parts, lateral, !turned, capStart, capEnd, road.Length, p0, t0, p1, t1, id, tag + " (2nd)");
            any = true;
        }
        return any;
    }

    sealed class RParts { public int Center = -1, Start = -1, End = -1; public float SliceStart, SliceLength; }

    static RParts PartsOf(EtsModelData m, float roadLength)
    {
        foreach (string size in roadLength >= 10f ? new[] { "15m", "5m" } : new[] { "5m", "15m" })
        {
            int c = Index(m, "center" + size); if (c < 0) continue;
            var p = new RParts { Center = c, Start = Index(m, "start" + size), End = Index(m, "end" + size) };
            Lib.ZRange(m, m.Parts[c].Name, out float zMin, out float zMax);
            float nominal = size == "15m" ? 15f : 5f;
            if (zMax - zMin < 0.5f) { zMin = -nominal * 0.5f; zMax = nominal * 0.5f; }
            p.SliceStart = -zMax; p.SliceLength = zMax - zMin;
            return p;
        }
        return null;
    }

    static int Index(EtsModelData m, string n) { for (int i = 0; i < m.Parts.Length; i++) if (m.Parts[i].Name == n) return i; return -1; }

    static bool Continues(EtsMapRoad road, ulong nodeUid, ulong model)
    {
        var node = Lib.Map.FindNode(nodeUid); if (node == null) return false;
        ulong other = node.ForwardItem == road.Uid ? node.BackwardItem : node.ForwardItem;
        var next = other != 0 && other != road.Uid ? Lib.Map.FindRoad(other) : null;
        if (next == null || next.Railings == null) return false;
        return next.Railings.Any(r => r.Model == model);
    }

    static void RailingCopy(EtsModelData m, RParts parts, float lateral, bool turned, bool capStart, bool capEnd, float length, D3 p0, D3 t0, D3 p1, D3 t1, int id, string tag, bool twin = false)
    {
        int repeats = Math.Max(1, (int)Math.Round(length / parts.SliceLength, MidpointRounding.ToEven));
        float stretch = length / (repeats * parts.SliceLength);
        int firstCap = turned ? parts.End : parts.Start, lastCap = turned ? parts.Start : parts.End;
        var runs = new List<int[]>(); int from = 0, to = repeats;
        if (capStart && firstCap >= 0) { runs.Add(new[] { firstCap, 0, 1 }); from = 1; }
        if (capEnd && lastCap >= 0 && to - from >= 1) { runs.Add(new[] { lastCap, repeats - 1, 1 }); to = repeats - 1; }
        if (to > from) runs.Add(new[] { parts.Center, from, to - from });
        foreach (var run in runs)
        {
            var part = m.Parts[run[0]];
            for (int p = part.PieceStart; p < part.PieceStart + part.PieceCount && p < m.Pieces.Length; p++)
            {
                var piece = m.Pieces[p];
                if (!Lib.Drawn(m, piece)) continue;
                if (twin) { piece = EtsRailingTwin.Uncovered(m, run[0], TwinPart(parts, !turned, capStart, capEnd, repeats, run[1]), p, parts.SliceStart, parts.SliceLength, o => Lib.Drawn(m, o)); if (piece == null) continue; }
                int g = group++;
                BentRun(piece, parts.SliceStart, parts.SliceLength, run[1], run[2], stretch, lateral, turned, p0, t0, p1, t1, length, g, id, tag + ":" + part.Name);
            }
        }
    }

    static void BentRun(EtsModelPiece piece, float sliceStart, float sliceLength, int firstRepeat, int repeats, float stretch, float lateralOffset, bool mirrorX,
        D3 p0, D3 t0, D3 p1, D3 t1, float roadLength, int g, int id, string tag)
    {
        int n = piece.VertexCount; float side = mirrorX ? -1 : 1; float zEnd = 2 * sliceStart + sliceLength;
        for (int r = 0; r < repeats; r++)
        {
            var V = new D3[n];
            for (int i = 0; i < n; i++)
            {
                float x = piece.Positions[i * 3] * side + lateralOffset, y = piece.Positions[i * 3 + 1];
                float zU = -piece.Positions[i * 3 + 2]; if (mirrorX) zU = zEnd - zU;
                float al = ((firstRepeat + r) * sliceLength + (zU - sliceStart)) * stretch;
                double s = Math.Max(0, Math.Min(1, al / roadLength));
                D3 point = Lib.Hermite(p0, t0, p1, t1, s);
                D3 fwd = Lib.HermiteTangent(p0, t0, p1, t1, s).Norm;
                D3 right = new D3(0, 1, 0).Cross(fwd).Norm; D3 up = fwd.Cross(right).Norm;
                V[i] = point + right * x + up * y;
            }
            // one group per repeat: the joint between repeats is what the scan looks at
            int gg = r == 0 ? g : group++;
            for (int i = 0; i + 2 < piece.Indices.Length; i += 3)
                Lib.AddTri(tris, V[piece.Indices[i]], V[piece.Indices[i + 1]], V[piece.Indices[i + 2]], gg, id, tag);
        }
    }
    static int TwinPart(RParts parts, bool turned, bool capStart, bool capEnd, int repeats, int slot)
    {
        int firstCap = turned ? parts.End : parts.Start, lastCap = turned ? parts.Start : parts.End;
        int from = capStart && firstCap >= 0 ? 1 : 0;
        if (from == 1 && slot == 0) return firstCap;
        if (capEnd && lastCap >= 0 && repeats - from >= 1 && slot == repeats - 1) return lastCap;
        return parts.Center;
    }
}
