using System;
using System.Collections.Generic;
using System.Text;
using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
using UnityEngine;

namespace SomniumSpace.Worlds.SomniumEuroTruck
{
    /// <summary>
    /// What is under the mouse, read back from the scene: the object, whether anything is drawn
    /// there, and what its material binds. F10 in play. A wrong-looking patch of ground can then
    /// be named in the console instead of guessed at from a screenshot.
    /// </summary>
    public static class EtsProbe
    {
        public static string UnderCursor()
        {
            Camera camera = Camera.main;

            if (camera == null)
            {
                return "no main camera";
            }

            Ray ray = camera.ScreenPointToRay(Input.mousePosition);

            // Every layer, IgnoreRaycast included: a hole is a collider with nothing drawn on it,
            // and that is exactly the thing worth finding.
            if (!Physics.Raycast(ray, out RaycastHit hit, 3000f, ~0, QueryTriggerInteraction.Collide))
            {
                return "nothing with a collider under the cursor within 3 km - what shows there is the sky or the camera background";
            }

            var sb = new StringBuilder();
            sb.Append("hit ").Append(Path(hit.transform)).Append(" at ").Append(hit.point.ToString("F1")).Append(", ")
              .Append(hit.distance.ToString("0.0")).Append(" m, layer ").Append(LayerMask.LayerToName(hit.collider.gameObject.layer));

            try
            {
                var renderer = hit.collider.GetComponent<Renderer>();

                if (renderer == null)
                {
                    sb.Append("; NO RENDERER - a collider with nothing drawn on it (a hole)");
                }
                else
                {
                    sb.Append("; renderer ").Append(renderer.enabled ? "enabled" : "DISABLED");
                    Describe(sb, renderer.sharedMaterial);
                }

                var filter = hit.collider.GetComponent<MeshFilter>();

                if (filter != null && filter.sharedMesh != null)
                {
                    Mesh mesh = filter.sharedMesh;
                    sb.Append("; mesh '").Append(mesh.name).Append("' ").Append(mesh.vertexCount).Append(" verts");

                    if (mesh.isReadable)
                    {
                        sb.Append(", uv ").Append(mesh.uv.Length).Append(", uv2 ").Append(mesh.uv2.Length)
                          .Append(", colours ").Append(mesh.colors32.Length).Append(", normals ").Append(mesh.normals.Length);
                    }
                }
            }
            catch (System.Exception e)
            {
                sb.Append("; probe error: ").Append(e.Message);
            }

            return sb.ToString();
        }

        private static void Describe(StringBuilder sb, Material material)
        {
            if (material == null)
            {
                sb.Append(", NO MATERIAL");
                return;
            }

            sb.Append(", material '").Append(material.name).Append("' shader ").Append(material.shader != null ? material.shader.name : "none");

            foreach (string property in new[] { "_BaseMap", "_MultMap", "_BumpMap", "_MetallicGlossMap" })
            {
                if (!material.HasProperty(property))
                {
                    continue;
                }

                Texture texture = material.GetTexture(property);

                if (texture != null)
                {
                    sb.Append(", ").Append(property).Append('=').Append(texture.name).Append(' ').Append(texture.width).Append('x').Append(texture.height);

                    if (texture is Texture2D flat)
                    {
                        sb.Append(' ').Append(flat.format);
                    }
                }
                else if (property == "_BaseMap")
                {
                    sb.Append(", NO BASE TEXTURE (draws as its plain colour)");
                }
            }

            if (material.HasProperty("_BaseColor")) sb.Append(", colour ").Append(material.GetColor("_BaseColor"));
            if (material.HasProperty("_Smoothness")) sb.Append(", smooth ").Append(material.GetFloat("_Smoothness").ToString("0.00"));
            if (material.HasProperty("_Metallic")) sb.Append(", metal ").Append(material.GetFloat("_Metallic").ToString("0.00"));
            if (material.HasProperty("_LayerMode")) sb.Append(", layer mode ").Append(material.GetFloat("_LayerMode"));
            if (material.HasProperty("_Surface")) sb.Append(", surface ").Append(material.GetFloat("_Surface"));
            sb.Append(", queue ").Append(material.renderQueue);

            string[] keywords = material.shaderKeywords;

            if (keywords != null && keywords.Length > 0)
            {
                sb.Append(", keywords ").Append(string.Join(" ", keywords));
            }
        }

        /// <summary>Whether the collider's object draws an opaque (or cut-out) surface.</summary>
        private static bool IsDrawnOpaque(Collider collider)
        {
            if (collider == null) return false;
            var renderer = collider.GetComponent<Renderer>();
            if (renderer == null) return false;

            // Blended ground is a PAIR: the blended mesh carries no collider, and its single-brush
            // stand-in carries the collider with its renderer switched off while the blended one
            // shows. Asking only the collider's own renderer called all of that ground undrawn,
            // which put 500 cells of perfectly good terrain in the census's "nothing drawn" list.
            if (!renderer.enabled)
            {
                var flat = renderer as MeshRenderer;

                if (flat == null) return false;

                foreach (EtsTerrainBuilder.PairRenderers pair in EtsTerrainBuilder.Pairs)
                {
                    if (pair.Flat == flat) return pair.Blended != null && pair.Blended.enabled && Opaque(pair.Blended.sharedMaterial);
                }

                return false;
            }

            return Opaque(renderer.sharedMaterial);
        }

        private static bool Opaque(Material material)
        {
            return material != null && material.shader != null && material.renderQueue <= 2500;
        }

        /// <summary>Connected patches (8-neighbours) of the set cells: count, minI, maxI, minJ, maxJ, sumI, sumJ, touches-edge.</summary>
        private static List<int[]> Patches(bool[,] grid, int n)
        {
            var patchOf = new int[n, n];
            var patches = new List<int[]>();
            var stack = new Stack<int>();

            for (int i = 0; i < n; i++)
            {
                for (int j = 0; j < n; j++)
                {
                    if (!grid[i, j] || patchOf[i, j] != 0) continue;
                    int id = patches.Count + 1;
                    var patch = new[] { 0, i, i, j, j, 0, 0, 0 };
                    patches.Add(patch);
                    stack.Push(i * n + j);
                    patchOf[i, j] = id;

                    while (stack.Count > 0)
                    {
                        int c = stack.Pop(), ci = c / n, cj = c % n;
                        patch[0]++; patch[5] += ci; patch[6] += cj;
                        if (ci < patch[1]) patch[1] = ci; if (ci > patch[2]) patch[2] = ci;
                        if (cj < patch[3]) patch[3] = cj; if (cj > patch[4]) patch[4] = cj;
                        if (ci == 0 || cj == 0 || ci == n - 1 || cj == n - 1) patch[7] = 1;

                        for (int di = -1; di <= 1; di++)
                        {
                            for (int dj = -1; dj <= 1; dj++)
                            {
                                int ni = ci + di, nj = cj + dj;
                                if (ni < 0 || nj < 0 || ni >= n || nj >= n || !grid[ni, nj] || patchOf[ni, nj] != 0) continue;
                                patchOf[ni, nj] = id;
                                stack.Push(ni * n + nj);
                            }
                        }
                    }
                }
            }

            patches.Sort((x, y) => y[0].CompareTo(x[0]));
            return patches;
        }

        private static void AppendPatches(StringBuilder sb, string what, List<int[]> patches, int n, Vector3 centre, float radius, float cell, Func<int, int, string> describe)
        {
            int total = 0;
            foreach (int[] patch in patches) total += patch[0];
            sb.Append("\n  ").Append(what).Append(": ").Append(total).Append(" cells in ").Append(patches.Count).Append(" patches; the largest:");

            for (int p = 0; p < patches.Count && p < 10; p++)
            {
                int[] patch = patches[p];
                float cx = centre.x - radius + ((float)patch[5] / patch[0] + 0.5f) * cell;
                float cz = centre.z - radius + ((float)patch[6] / patch[0] + 0.5f) * cell;
                sb.Append("\n   ").Append(patch[0]).Append(" cells, centre (").Append(cx.ToString("0")).Append(", ").Append(cz.ToString("0")).Append("), ")
                  .Append((patch[2] - patch[1] + 1) * cell).Append(" x ").Append((patch[4] - patch[3] + 1) * cell).Append(" m")
                  .Append(patch[7] == 1 ? ", touches the window's edge" : "")
                  .Append(", ").Append(describe(patch[5] / patch[0], patch[6] / patch[0]));
            }
        }

        /// <summary>
        /// The ground round a point, surveyed from above: every cell of a grid is ray-cast down
        /// through everything solid. A cell that hits nothing is a HOLE (sky where ground should
        /// be); a cell whose top two hits lie within three centimetres of each other is two
        /// surfaces in one plane, which is what flickers. Holes are listed by world position and
        /// the co-planar pairs by name and count, so each can be traced to the map item that made
        /// it. 120 m at 2 m cells is 14,400 rays - once, on F11 or shortly after the truck lands.
        /// </summary>
        public static string GroundCensus(Vector3 centre, float radius, float cell)
        {
            int n = Mathf.Max(1, Mathf.CeilToInt(radius * 2f / cell));
            int mask = ~((1 << EtsLayers.Vehicle) | (1 << EtsLayers.Player) | (1 << 2));
            var hits = new RaycastHit[24];
            var holes = new List<Vector3>();
            var holeGrid = new bool[n, n];
            var bareGrid = new bool[n, n];
            var topName = new string[n, n];
            var bareName = new string[n, n];
            var pairs = new Dictionary<string, int>();
            var pairSample = new Dictionary<string, Vector3>();
            var coverage = new Dictionary<string, int>();
            var coverageSample = new Dictionary<string, Vector3>();
            int cells = 0, holeCount = 0, overlapCells = 0;

            for (int i = 0; i < n; i++)
            {
                for (int j = 0; j < n; j++)
                {
                    var origin = new Vector3(centre.x - radius + (i + 0.5f) * cell, centre.y + 250f, centre.z - radius + (j + 0.5f) * cell);
                    cells++;
                    int count = Physics.RaycastNonAlloc(origin, Vector3.down, hits, 500f, mask, QueryTriggerInteraction.Ignore);

                    if (count == 0)
                    {
                        holeCount++;
                        holeGrid[i, j] = true;
                        if (holes.Count < 16) holes.Add(new Vector3(origin.x, centre.y, origin.z));
                        continue;
                    }

                    int top = 0;
                    for (int k = 1; k < count; k++) if (hits[k].distance < hits[top].distance) top = k;
                    topName[i, j] = Name(hits[top].collider);
                    bool overlapped = false;

                    // Is anything actually DRAWN here, opaque, within 3 m under the top hit? A
                    // collider with no renderer (a collision box, a yard piece with no material)
                    // and a transparent decal count for nothing: with no opaque surface the sky
                    // shows through, which is what a blue sheet on the ground is.
                    bool drawn = false;
                    string there = null;

                    for (int k = 0; k < count; k++)
                    {
                        if (hits[k].distance > hits[top].distance + 3f) continue;
                        if (IsDrawnOpaque(hits[k].collider)) { drawn = true; break; }
                        if (there == null || k == top) there = Name(hits[k].collider) + " [" + Surface(hits[k].collider) + "]";
                    }

                    if (!drawn)
                    {
                        bareGrid[i, j] = true;
                        bareName[i, j] = there;
                    }

                    // What is on top here, by material: the surfaces covering the most ground
                    // near the truck, so a wrongly coloured one can be named from the log.
                    string topMaterial = Surface(hits[top].collider);
                    coverage.TryGetValue(topMaterial, out int covered);
                    coverage[topMaterial] = covered + 1;
                    if (covered == 0) coverageSample[topMaterial] = hits[top].point;

                    for (int k = 0; k < count; k++)
                    {
                        if (k == top || Mathf.Abs(hits[k].distance - hits[top].distance) > 0.03f) continue;
                        string a = Name(hits[top].collider), b = Name(hits[k].collider);
                        if (a == b) continue;
                        string key = string.CompareOrdinal(a, b) < 0 ? a + "  ~  " + b : b + "  ~  " + a;
                        pairs.TryGetValue(key, out int seen);
                        pairs[key] = seen + 1;
                        if (seen == 0) pairSample[key] = hits[top].point;
                        overlapped = true;
                    }

                    if (overlapped) overlapCells++;
                }
            }

            var sb = new StringBuilder();
            sb.Append("ground census ").Append(radius).Append(" m round ").Append(centre.ToString("F0")).Append(" at ").Append(cell).Append(" m cells: ")
              .Append(holeCount).Append(" of ").Append(cells).Append(" cells have NO ground (").Append((100f * holeCount / Mathf.Max(1, cells)).ToString("0.0")).Append(" %), ")
              .Append(overlapCells).Append(" cells have two surfaces within 3 cm.");

            // Holes (no collider at all) and bare cells (a collider, but nothing drawn opaque
            // within 3 m under the top hit) grouped into connected patches, largest first, each
            // with its cell count, centre, extent, whether it touches the window's edge (then it
            // may just continue outside) and what is there or beside it - so a patch can be found
            // and named rather than read off as sixteen scattered coordinates.
            if (holeCount > 0)
            {
                AppendPatches(sb, "holes", Patches(holeGrid, n), n, centre, radius, cell, (mi, mj) =>
                {
                    for (int ring = 1; ring <= 4; ring++)
                        for (int di = -ring; di <= ring; di++)
                            for (int dj = -ring; dj <= ring; dj++)
                            {
                                int ni = mi + di, nj = mj + dj;
                                if (ni < 0 || nj < 0 || ni >= n || nj >= n) continue;
                                if (topName[ni, nj] != null) return "beside " + topName[ni, nj];
                            }
                    return "beside nothing named";
                });
            }

            List<int[]> bare = Patches(bareGrid, n);

            if (bare.Count > 0)
            {
                AppendPatches(sb, "cells with a collider but NOTHING DRAWN OPAQUE under it (the sky shows through)", bare, n, centre, radius, cell,
                    (mi, mj) => "there: " + (bareName[mi, mj] ?? "?"));
            }

            var ranked = new List<KeyValuePair<string, int>>(pairs);
            ranked.Sort((x, y) => y.Value.CompareTo(x.Value));

            for (int i = 0; i < ranked.Count && i < 14; i++)
            {
                Vector3 at = pairSample[ranked[i].Key];
                sb.Append("\n  co-planar x").Append(ranked[i].Value).Append(" e.g. at (").Append(at.x.ToString("0")).Append(", ").Append(at.y.ToString("0.0")).Append(", ").Append(at.z.ToString("0")).Append("): ").Append(ranked[i].Key);
            }

            var covering = new List<KeyValuePair<string, int>>(coverage);
            covering.Sort((x, y) => y.Value.CompareTo(x.Value));

            for (int i = 0; i < covering.Count && i < 12; i++)
            {
                Vector3 at = coverageSample[covering[i].Key];
                sb.Append("\n  on top in ").Append(covering[i].Value).Append(" cells, e.g. (").Append(at.x.ToString("0")).Append(", ").Append(at.y.ToString("0.0")).Append(", ").Append(at.z.ToString("0")).Append("): ").Append(covering[i].Key);
            }

            return sb.ToString();
        }

        /// <summary>
        /// A collider's surface as the renderer draws it: object, shader, base texture and colour.
        /// Anything drawn in a colour no file asks for shows up here with the texture it lacks.
        /// </summary>
        private static string Surface(Collider collider)
        {
            var renderer = collider.GetComponent<Renderer>();

            if (renderer == null)
            {
                return collider.transform.name + " [no renderer]";
            }

            Material material = renderer.sharedMaterial;

            if (material == null)
            {
                return collider.transform.name + " [no material]";
            }

            var sb = new StringBuilder();
            sb.Append(material.name).Append(" (").Append(material.shader != null ? material.shader.name : "no shader");
            Texture baseMap = material.HasProperty("_BaseMap") ? material.GetTexture("_BaseMap") : null;
            sb.Append(", base ").Append(baseMap != null ? baseMap.name : "NONE");
            if (material.HasProperty("_MultMap")) { Texture over = material.GetTexture("_MultMap"); sb.Append(", over ").Append(over != null ? over.name : "none"); }
            if (material.HasProperty("_BaseColor")) { Color c = material.GetColor("_BaseColor"); sb.Append(", colour ").Append(c.r.ToString("0.00")).Append('/').Append(c.g.ToString("0.00")).Append('/').Append(c.b.ToString("0.00")); }
            if (material.HasProperty("_LayerMode")) sb.Append(", mode ").Append(material.GetFloat("_LayerMode"));
            if (material.IsKeywordEnabled("_EMISSION")) sb.Append(", EMISSIVE");

            // A ground that mirrors the sky reads as a pale blue sheet; name the shine when there is any.
            float smooth = material.HasProperty("_Smoothness") ? material.GetFloat("_Smoothness") : 0f;
            float metal = material.HasProperty("_Metallic") ? material.GetFloat("_Metallic") : 0f;
            if (smooth > 0.3f || metal > 0.3f) sb.Append(", SHINY smooth ").Append(smooth.ToString("0.00")).Append(" metal ").Append(metal.ToString("0.00"));
            if (material.renderQueue > 2500) sb.Append(", transparent queue ").Append(material.renderQueue);
            sb.Append(')');
            return sb.ToString();
        }

        /// <summary>A collider's object with its parent and its material, enough to name the map item.</summary>
        private static string Name(Collider collider)
        {
            Transform t = collider.transform;
            string name = t.name;
            if (t.parent != null) name = t.parent.name + "/" + name;
            if (t.parent != null && t.parent.parent != null) name = t.parent.parent.name + "/" + name;
            var renderer = collider.GetComponent<Renderer>();
            if (renderer == null) return name + " [no renderer]";
            Material material = renderer.sharedMaterial;
            return name + " [" + (material != null ? material.name : "no material") + "]";
        }

        private static string Path(Transform transform)
        {
            var parts = new List<string>();

            while (transform != null && parts.Count < 6)
            {
                parts.Add(transform.name);
                transform = transform.parent;
            }

            parts.Reverse();
            return string.Join("/", parts);
        }
    }
}
