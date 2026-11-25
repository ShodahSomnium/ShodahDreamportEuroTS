// LAB ONLY (never in the live code): scans the built world round the site centre for overlapping coplanar
// same-facing triangles where at least one side is a fence/railing/wall run (EtsRunBuilder "runs" or a road's
// "railings"). Writes FENCE_SCAN_OUT (a text report) once the first build is complete and the stream has settled.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

namespace SomniumSpace.Worlds.SomniumEuroTruck
{
    public sealed class FenceScanLab : MonoBehaviour
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FENCE_SCAN_OUT"))) return;
            var go = new GameObject("FenceScanLab");
            DontDestroyOnLoad(go);
            go.AddComponent<FenceScanLab>();
        }

        IEnumerator Start()
        {
            string output = Environment.GetEnvironmentVariable("FENCE_SCAN_OUT");
            float radius = 300f;
            float.TryParse(Environment.GetEnvironmentVariable("FENCE_SCAN_R") ?? "300", System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out radius);
            while (!EtsMapViewer.AnyCompleted) yield return new WaitForSeconds(1f);
            yield return new WaitForSeconds(20f);
            string report;
            try { report = Scan(radius); }
            catch (Exception e) { report = "scan failed: " + e; }
            File.WriteAllText(output, report);
            Debug.Log("[FenceScanLab] wrote " + output);
        }

        struct T { public Vector3 a, b, c, n; public float d, area; public int group, item, cat; public string tag; }

        static string Category(Transform t, out Transform itemRoot)
        {
            itemRoot = t;
            Transform prev = t;
            for (Transform p = t; p != null; prev = p, p = p.parent)
            {
                string n = p.name;
                if (n.StartsWith("railing ", StringComparison.Ordinal)) { itemRoot = p; return "railing"; }
                if (p.parent != null)
                {
                    string pn = p.parent.name;
                    if (pn == "runs" || pn == "prefabs" || pn == "models" || pn == "roads" || pn == "signs" || pn == "terrain items")
                    {
                        itemRoot = p;
                        return pn == "runs" ? "run" : pn;
                    }
                }
            }
            return "other";
        }

        static string Scan(float radius)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var fence = new List<T>();
            var rest = new List<T>();
            int renderers = 0;
            foreach (MeshFilter f in FindObjectsByType<MeshFilter>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                var r = f.GetComponent<MeshRenderer>();
                if (r == null || !r.enabled || f.sharedMesh == null) continue;
                if (r.bounds.SqrDistance(Vector3.zero) > radius * radius) continue;
                string cat = Category(f.transform, out Transform root);
                if (cat == "other") continue;
                bool isFence = cat == "run" || cat == "railing";
                Mesh mesh = f.sharedMesh;
                if (!mesh.isReadable) continue;
                Vector3[] v = mesh.vertices;
                Matrix4x4 m = f.transform.localToWorldMatrix;
                for (int i = 0; i < v.Length; i++) v[i] = m.MultiplyPoint3x4(v[i]);
                int[] idx = mesh.triangles;
                string tag = cat + ":" + root.name + "/" + f.name;
                // one group per laid element: a run copy (the child of the run root), else the item itself
                Transform element = f.transform;
                while (element.parent != null && element.parent != root) element = element.parent;
                int item = root.GetInstanceID(), group = cat == "run" && element.parent == root ? element.GetInstanceID() : cat == "railing" ? f.GetInstanceID() : item;
                var into = isFence ? fence : rest;
                for (int i = 0; i + 2 < idx.Length; i += 3)
                {
                    Vector3 a = v[idx[i]], b = v[idx[i + 1]], c = v[idx[i + 2]];
                    Vector3 n = Vector3.Cross(b - a, c - a);
                    float l = n.magnitude;
                    if (l < 2e-5f) continue;
                    n /= l;
                    into.Add(new T { a = a, b = b, c = c, n = n, d = Vector3.Dot(n, a), area = 0.5f * l, group = group, item = item, cat = isFence ? 1 : 0, tag = tag });
                }
                renderers++;
            }

            // grid: every fence triangle; the rest only where a fence triangle is
            const float cell = 1f;
            var grid = new Dictionary<long, List<int>>();
            var all = new List<T>(fence);
            Func<int, int, int, long> key = (x, y, z) => ((long)(x & 0x1FFFFF) << 42) | ((long)(y & 0x1FFFFF) << 21) | (long)(z & 0x1FFFFF);
            Action<int, bool> insert = (i, onlyIfFence) =>
            {
                T t = all[i];
                int x0 = Mathf.FloorToInt(Mathf.Min(t.a.x, Mathf.Min(t.b.x, t.c.x)) / cell), x1 = Mathf.FloorToInt(Mathf.Max(t.a.x, Mathf.Max(t.b.x, t.c.x)) / cell);
                int y0 = Mathf.FloorToInt(Mathf.Min(t.a.y, Mathf.Min(t.b.y, t.c.y)) / cell), y1 = Mathf.FloorToInt(Mathf.Max(t.a.y, Mathf.Max(t.b.y, t.c.y)) / cell);
                int z0 = Mathf.FloorToInt(Mathf.Min(t.a.z, Mathf.Min(t.b.z, t.c.z)) / cell), z1 = Mathf.FloorToInt(Mathf.Max(t.a.z, Mathf.Max(t.b.z, t.c.z)) / cell);
                if ((long)(x1 - x0 + 1) * (y1 - y0 + 1) * (z1 - z0 + 1) > 4000) return;
                for (int x = x0; x <= x1; x++) for (int y = y0; y <= y1; y++) for (int z = z0; z <= z1; z++)
                {
                    long k = key(x, y, z);
                    if (!grid.TryGetValue(k, out var list)) { if (onlyIfFence) continue; grid[k] = list = new List<int>(); }
                    list.Add(i);
                }
            };
            for (int i = 0; i < all.Count; i++) insert(i, false);
            foreach (T t in rest) { all.Add(t); insert(all.Count - 1, true); }

            int pairs = 0, same = 0, opposite = 0; double area = 0, sameArea = 0;
            var byTag = new Dictionary<string, double>(); var byTagN = new Dictionary<string, int>();
            var byCat = new Dictionary<string, double>(); var byCatN = new Dictionary<string, int>();
            var where = new Dictionary<string, Vector3>();
            var cellArea = new Dictionary<long, double>();
            var done = new HashSet<long>();
            foreach (var list in grid.Values)
            {
                for (int ii = 0; ii < list.Count; ii++)
                for (int jj = ii + 1; jj < list.Count; jj++)
                {
                    int i = list[ii], j = list[jj];
                    T A = all[i], B = all[j];
                    if (A.group == B.group || (A.cat == 0 && B.cat == 0)) continue;
                    float dot = Vector3.Dot(A.n, B.n);
                    if (Mathf.Abs(dot) < 0.9995f) continue;
                    if (Mathf.Abs(Vector3.Dot(A.n, B.a) - A.d) > 0.005f || Mathf.Abs(Vector3.Dot(A.n, B.b) - A.d) > 0.005f || Mathf.Abs(Vector3.Dot(A.n, B.c) - A.d) > 0.005f) continue;
                    long pk = i < j ? ((long)i << 32) | (uint)j : ((long)j << 32) | (uint)i;
                    if (!done.Add(pk)) continue;
                    float ov = Overlap(A, B);
                    if (ov < 0.0005f) continue;
                    if (dot < 0f) { opposite++; continue; }
                    pairs++; area += ov;
                    bool sameItem = A.item == B.item;
                    if (sameItem) { same++; sameArea += ov; }
                    string ca = Cat(A) + (sameItem ? " (same item)" : " x " + Cat(B));
                    byCat[ca] = (byCat.TryGetValue(ca, out double cv) ? cv : 0) + ov; byCatN[ca] = (byCatN.TryGetValue(ca, out int cn) ? cn : 0) + 1;
                    string tg = sameItem ? "same: " + A.tag : "cross: " + A.tag + " | " + B.tag;
                    byTag[tg] = (byTag.TryGetValue(tg, out double tv) ? tv : 0) + ov; byTagN[tg] = (byTagN.TryGetValue(tg, out int tn) ? tn : 0) + 1;
                    Vector3 centre = (A.a + A.b + A.c) / 3f;
                    if (!where.ContainsKey(tg)) where[tg] = centre;
                    long ck = key(Mathf.FloorToInt(centre.x / 10f), 0, Mathf.FloorToInt(centre.z / 10f));
                    cellArea[ck] = (cellArea.TryGetValue(ck, out double ca2) ? ca2 : 0) + ov;
                }
            }

            var sb = new StringBuilder();
            sb.AppendLine("FenceScanLab radius " + radius + " m: " + renderers + " renderers, " + fence.Count + " fence/railing/run triangles, " + rest.Count + " other triangles, " + sw.ElapsedMilliseconds + " ms");
            sb.AppendLine(string.Format("overlapping coplanar same-facing pairs touching a run/railing: {0} ({1:F2} m2); within one item (joints): {2} ({3:F2} m2); opposite-facing coplanar pairs: {4}", pairs, area, same, sameArea, opposite));
            sb.AppendLine("by category:");
            foreach (var kv in byCat.OrderByDescending(k => k.Value)) sb.AppendLine(string.Format("   {0,10:F3} m2 {1,7} pairs  {2}", kv.Value, byCatN[kv.Key], kv.Key));
            sb.AppendLine("by item kind (top 40), with the first place (unity x,y,z):");
            foreach (var kv in byTag.OrderByDescending(k => k.Value).Take(40)) sb.AppendLine(string.Format("   {0,10:F3} m2 {1,7} pairs  {2}  @ {3}", kv.Value, byTagN[kv.Key], Short(kv.Key), where[kv.Key].ToString("F1")));
            sb.AppendLine("worst 10 m cells (unity x, z):");
            foreach (var kv in cellArea.OrderByDescending(k => k.Value).Take(15))
            {
                long x = (kv.Key >> 42) & 0x1FFFFF, z = kv.Key & 0x1FFFFF;
                if (x >= 0x100000) x -= 0x200000; if (z >= 0x100000) z -= 0x200000;
                sb.AppendLine(string.Format("   {0,10:F3} m2 at x {1}..{2}, z {3}..{4}", kv.Value, x * 10, x * 10 + 10, z * 10, z * 10 + 10));
            }
            return sb.ToString();
        }

        static string Cat(T t) => t.tag.Substring(0, t.tag.IndexOf(':'));

        static string Short(string tag)
        {
            // strip piece numbers so one kind of item aggregates
            return tag.Length > 220 ? tag.Substring(0, 220) : tag;
        }

        static float Overlap(T A, T B)
        {
            Vector3 u = (A.b - A.a).normalized, v = Vector3.Cross(A.n, u);
            Func<Vector3, Vector2> P = p => new Vector2(Vector3.Dot(p - A.a, u), Vector3.Dot(p - A.a, v));
            var subject = new List<Vector2> { P(B.a), P(B.b), P(B.c) };
            var clip = new List<Vector2> { P(A.a), P(A.b), P(A.c) };
            if (Signed(clip) < 0) clip.Reverse();
            for (int e = 0; e < 3 && subject.Count > 0; e++)
            {
                Vector2 c0 = clip[e], c1 = clip[(e + 1) % 3];
                var input = subject; subject = new List<Vector2>();
                for (int k = 0; k < input.Count; k++)
                {
                    Vector2 cur = input[k], prev = input[(k + input.Count - 1) % input.Count];
                    float sc = (c1.x - c0.x) * (cur.y - c0.y) - (c1.y - c0.y) * (cur.x - c0.x);
                    float sp = (c1.x - c0.x) * (prev.y - c0.y) - (c1.y - c0.y) * (prev.x - c0.x);
                    if (sc >= 0) { if (sp < 0) subject.Add(Vector2.Lerp(prev, cur, sp / (sp - sc))); subject.Add(cur); }
                    else if (sp >= 0) subject.Add(Vector2.Lerp(prev, cur, sp / (sp - sc)));
                }
            }
            return subject.Count < 3 ? 0f : Mathf.Abs(Signed(subject));
        }

        static float Signed(List<Vector2> p) { float s = 0; for (int i = 0; i < p.Count; i++) { Vector2 a = p[i], b = p[(i + 1) % p.Count]; s += a.x * b.y - a.y * b.x; } return 0.5f * s; }
    }
}
