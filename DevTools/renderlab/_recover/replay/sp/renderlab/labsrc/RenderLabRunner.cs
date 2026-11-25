// Render lab (throwaway project only): runs in play mode after RenderLabEditor.Go has opened the world's real scene,
// switched off everything that is not the map (network, HQ flow, truck, spawn platform) and entered play mode. The map
// viewer loads round the site's centre with the world's own code; this waits for the stream to settle round each view
// and renders a URP camera (the project's pipeline asset, the scene's Global Volume and the world's EtsPostFx volume,
// the climate's fog, sun, ambient and EtsSky skybox) into an sRGB render texture, saved as PNG with a sidecar .txt.
//
// Hole tools (2026-09-25, N/holes; run through N/holes/render.sh, which also knows the sites "doverh" and "prague"):
//   - sites doverh / prague: a survey of the user's VR drive points (SurveyFor): per point a 2 m ground census 60 m
//     round, 8 compass views at cab eye height (2.6 m, pitch 2) and 4 from 30 m up (pitch 25), 1280x720. Each view
//     is rendered normally and once more with post off and the sky cleared to pure magenta (Holes): every magenta
//     pixel below the horizon is a hole (nothing drawn). Every 3rd one is ray-cast (FirstDrawnHit, Locate) and
//     binned in 6 m cells with what stands round it (Around), what should draw over it (RenderersOver) and every
//     collider along the ray (AllAlong). Out: <prefix>_<point>_<cab|up>_y<yaw>.png, _void.png when >= 30 hole px,
//     <prefix>_<site>_holes.txt. RL_VIEWS=p5,p9 limits the points; RL_PAIRS_AB=1 repeats each view with the blended
//     terrain pairs swapped for their flat stand-ins.
//   - RL_SHOTS="name,x,y,z,yaw,pitch;...": fixed poses, normal + void render + analysis (<prefix>_shot_<name>*.png,
//     <prefix>_<site>_shots.txt).
//   - RL_PROBE="x,z,r,step;x0,z0,x1,z1,step;...": every collider under a grid or a line (all layers, back faces too)
//     and every renderer over its middle (<prefix>_<site>_probe.txt).
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using SomniumSpace.Worlds.SomniumEuroTruck;
using SomniumSpace.Worlds.SomniumEuroTruck.Ets;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public class RenderLabRunner : MonoBehaviour
{
    public static bool Done;
    public static int ExitCode = 3;
    public static string OutPrefix = "C:/Temp/rl";
    public static string Site = "dover";
    public static string OnlyViews = "";
    public static float Fov = 60f;
    public static int Width = 1920, Height = 1080;
    public static float TimeoutSeconds = 1500f;
    public static bool NoPost;

    static readonly CultureInfo I = CultureInfo.InvariantCulture;
    static readonly StringBuilder Log = new StringBuilder();

    public sealed class View
    {
        public string Name;
        public Vector3 Position;      // Unity, y ignored when Ground is set
        public bool Ground = true;    // put the eye EyeHeight above the ground under Position
        public float EyeHeight = 2.52f;
        public float Yaw, Pitch;      // degrees; pitch positive = looking down
        public string Auto = "";      // "trees": yaw toward the most trees; "tree": close-up of a tree and a bush
        public float GroundGuessY;    // where to start the ground ray (Unity y)
    }

    Camera _camera;
    Transform _focus;
    float _start;

    public static List<View> ViewsFor(string site)
    {
        var views = new List<View>();

        if (site == "dover")
        {
            // The user's 2026-09-24 run: map centre game (-33391.4, 24.0, -7420.0); the truck stood at Unity (0, 0.08, 0)
            // facing 90 degrees. Eye 2.52 m above the ground there (cab eye ~2.6 m).
            // Unity (0, 0, 0) is inside the HQ garage (the spawn; first render showed the door frame), so the yard views
            // stand where the user drove to, about (-16, -2, 40), facing on along that line (yaw -22).
            views.Add(new View { Name = "a_spawn", Position = new Vector3(0f, 0f, 0f), Yaw = 90f, Pitch = 4f, GroundGuessY = 0f });
            views.Add(new View { Name = "a_yard", Position = new Vector3(-16f, 0f, 40f), Yaw = -22f, Pitch = 4f, GroundGuessY = -2f });
            views.Add(new View { Name = "a_trees", Position = new Vector3(-16f, 0f, 40f), Pitch = 3f, Auto = "trees", GroundGuessY = -2f });

            // Probe RL1 (renderlab/probe/rl1_dover.txt): road 2935dee5f0a8cd, look8, 87 m, 345 m from the centre, v2_14 bands
            // 8-30 m (right) and 6-15 m (left), terrain strips 67 m both sides. Mid game (-33730.9, 32.7, -7356.8) = Unity
            // (-339.5, 8.7, -63.2); its a->b heading is Unity yaw 117. Eye on the left lane (UK), 2.52 m up.
            // It is a divided road: the chord's middle is the median (first render: on the median, a lamp post in the
            // middle of the view), so the eye goes 6 m to the left, onto the left carriageway's inner lane.
            Vector3 road = new Vector3(-339.5f, 8.7f, -63.2f);
            Vector3 left = Quaternion.Euler(0f, 117f, 0f) * Vector3.left * 6f;
            views.Add(new View { Name = "b_road", Position = road + left, Yaw = 117f, Pitch = 3f, GroundGuessY = road.y });
            views.Add(new View { Name = "c_sky", Position = road + left, Yaw = 117f, Pitch = -20f, GroundGuessY = road.y });
            views.Add(new View { Name = "d_tree", Position = road, Auto = "tree", GroundGuessY = road.y, EyeHeight = 1.7f });
        }
        else if (site == "szczecin")
        {
            // The game's own start: "[actor] Initialized at [14994.0;46.8;-15235.2]" (TransiNet yard, Szczecin).
            views.Add(new View { Name = "e_yard", Position = Vector3.zero, Pitch = 3f, Auto = "busy", GroundGuessY = 0f });
            views.Add(new View { Name = "e_trees", Position = Vector3.zero, Pitch = 3f, Auto = "trees", GroundGuessY = 0f });

            // The first 1.2 km round the start plants big_bush among the trees (checker probes 1936/1937, EtsVegetationBuilder
            // notes); round the Dover road spot there is none within 600 m (lab log, 2026-09-25), so the bush close-up is here.
            views.Add(new View { Name = "e_tree", Position = Vector3.zero, Auto = "tree", GroundGuessY = 0f, EyeHeight = 1.7f });
        }

        return views;
    }

    public static Vector3 CentreFor(string site)
    {
        if (site == "prague")
        {
            // The client's Prague HQ (Player.log 2026-09-25): "map centre (14501.1, 73.1, 3624.6)".
            return new Vector3(14501.1f, 73.1f, 3624.6f);
        }

        return site == "szczecin" ? new Vector3(14994.0f, 46.8f, -15235.2f) : new Vector3(-33391.4f, 24.0f, -7420.0f);
    }

    // ------------------------------------------------------------------ hole survey (2026-09-25, N/holes)

    /// <summary>A place the user drove in the 2026-09-25 VR test (Player.log "in scania_s_2016 at (x, y, z)", Unity).</summary>
    public sealed class SurveyPoint
    {
        public string Name;
        public float X, Z, GuessY;
    }

    /// <summary>
    /// Sites "doverh" and "prague": at each drive point, 8 compass directions at cab eye height (2.6 m) and 4 from 30 m
    /// up; each view is rendered normally and once more with the sky replaced by pure magenta and post off, so every
    /// pixel below the horizon where NOTHING is drawn (a hole: sky or void where ground, road or building should be)
    /// is counted, ray-cast and located. Plus a 2 m ground census 60 m round the point (what the truck can fall into).
    /// </summary>
    public static List<SurveyPoint> SurveyFor(string site)
    {
        var p = new List<SurveyPoint>();

        if (site == "doverh")
        {
            p.Add(new SurveyPoint { Name = "d1", X = 0f, Z = -6f, GuessY = 0f });      // arrival
            p.Add(new SurveyPoint { Name = "d2", X = -53f, Z = -80f, GuessY = -1f });
            p.Add(new SurveyPoint { Name = "d3", X = -13f, Z = -106f, GuessY = -1f });
            p.Add(new SurveyPoint { Name = "d4", X = 43f, Z = -98f, GuessY = -1f });
        }
        else if (site == "prague")
        {
            p.Add(new SurveyPoint { Name = "p1", X = 0f, Z = 0f, GuessY = 0f });
            p.Add(new SurveyPoint { Name = "p2", X = -39f, Z = 59f, GuessY = -1f });
            p.Add(new SurveyPoint { Name = "p3", X = -306f, Z = 112f, GuessY = 0f });
            p.Add(new SurveyPoint { Name = "p4", X = -372f, Z = -29f, GuessY = -1f });
            p.Add(new SurveyPoint { Name = "p5", X = -391f, Z = 124f, GuessY = -1f });   // "fell through the world at (-396, -99, 123)"
            p.Add(new SurveyPoint { Name = "p6", X = -619f, Z = -121f, GuessY = -3f });
            p.Add(new SurveyPoint { Name = "p7", X = -376f, Z = 549f, GuessY = -2f });
            p.Add(new SurveyPoint { Name = "p8", X = -712f, Z = 1159f, GuessY = -11f });
            p.Add(new SurveyPoint { Name = "p9", X = -947f, Z = 1442f, GuessY = -11f });
        }

        return p;
    }

    static readonly StringBuilder HoleSummary = new StringBuilder();

    IEnumerator Survey(List<SurveyPoint> points)
    {
        Width = 1280;
        Height = 720;
        Vector3 centre = CentreFor(Site);
        int rendered = 0;
        HoleSummary.AppendLine("hole survey, site " + Site + ", centre game " + centre.ToString("0.0") + " (game x = centre x + unity x, game z = centre z - unity z)");

        foreach (SurveyPoint pt in points)
        {
            if (!string.IsNullOrEmpty(OnlyViews) && Array.IndexOf(OnlyViews.Split(','), pt.Name) < 0)
            {
                continue;
            }

            _focus.position = new Vector3(pt.X, pt.GuessY, pt.Z);
            yield return Settle(pt.Name, 25f, 300f);
            bool found = GroundAt(new Vector3(pt.X, 0f, pt.Z), pt.GuessY, out Vector3 ground);
            HoleSummary.AppendLine();
            HoleSummary.AppendLine("== " + pt.Name + " unity (" + pt.X.ToString("0", I) + ", " + pt.Z.ToString("0", I) + ") game (" + (centre.x + pt.X).ToString("0", I) + ", " + (centre.z - pt.Z).ToString("0", I) + "): ground " +
                                   (found ? ground.y.ToString("0.00", I) : "NONE under the point"));
            HoleSummary.AppendLine("   " + Census(ground, 60f, 2f));
            _camera.transform.position = ground + Vector3.up * 2.6f;
            _focus.position = ground;
            yield return Settle(pt.Name + " (camera placed)", 8f, 120f);

            for (int pass = 0; pass < 2; pass++)
            {
                float lift = pass == 0 ? 2.6f : 30f, pitch = pass == 0 ? 2f : 25f;
                int count = pass == 0 ? 8 : 4;

                for (int k = 0; k < count; k++)
                {
                    float yaw = k * 360f / count;
                    Vector3 eye = ground + Vector3.up * lift;
                    _camera.transform.SetPositionAndRotation(eye, Quaternion.Euler(pitch, yaw, 0f));
                    yield return null;
                    string view = pt.Name + (pass == 0 ? "_cab" : "_up") + "_y" + yaw.ToString("000", I);

                    try
                    {
                        RenderTo(OutPrefix + "_" + view + ".png", true);
                        string line = Holes(view, eye, ground.y, centre, out bool save, out Texture2D voidTex);

                        if (save)
                        {
                            File.WriteAllBytes(OutPrefix + "_" + view + "_void.png", voidTex.EncodeToPNG());
                        }

                        Destroy(voidTex);
                        HoleSummary.AppendLine("   " + line);
                        Say("wrote " + view + ": " + line);
                        rendered++;

                        // RL_PAIRS_AB=1: the same view once more with the blended terrain pairs swapped for their
                        // single-brush stand-ins (EtsTerrainBuilder.SetPairsVisible, the F9 key): are the pairs the holes?
                        if (Environment.GetEnvironmentVariable("RL_PAIRS_AB") == "1")
                        {
                            EtsTerrainBuilder.SetPairsVisible(false);
                            string flat = Holes(view + "_flatpairs", eye, ground.y, centre, out bool saveFlat, out Texture2D flatTex);

                            if (saveFlat)
                            {
                                File.WriteAllBytes(OutPrefix + "_" + view + "_flatpairs_void.png", flatTex.EncodeToPNG());
                            }

                            Destroy(flatTex);
                            EtsTerrainBuilder.SetPairsVisible(true);
                            HoleSummary.AppendLine("   " + flat);
                        }
                    }
                    catch (Exception e)
                    {
                        Say("view " + view + " render failed: " + e);
                    }
                }
            }

            File.WriteAllText(OutPrefix + "_" + Site + "_holes.txt", HoleSummary.ToString());
        }

        File.WriteAllText(OutPrefix + "_" + Site + "_holes.txt", HoleSummary.ToString());
        Finish(rendered > 0 ? 0 : 5);
    }

    /// <summary>
    /// RL_SHOTS="name,x,y,z,yaw,pitch;..." (Unity, degrees; pitch 90 = straight down): after the stream settles round
    /// each pose, a normal render and the magenta void render with its hole analysis (&lt;prefix&gt;_shot_&lt;name&gt;.png,
    /// _void.png, lines in &lt;prefix&gt;_&lt;site&gt;_shots.txt).
    /// </summary>
    IEnumerator Shots(string spec)
    {
        int w = Width, h = Height;
        Width = 1280;
        Height = 720;
        var s = new StringBuilder();
        Vector3 centre = CentreFor(Site);

        foreach (string one in spec.Split(';'))
        {
            string[] f = one.Split(',');

            if (f.Length < 6)
            {
                continue;
            }

            var eye = new Vector3(float.Parse(f[1], I), float.Parse(f[2], I), float.Parse(f[3], I));
            _focus.position = new Vector3(eye.x, 0f, eye.z);
            yield return Settle("shot " + f[0], 30f, 300f);
            _camera.transform.SetPositionAndRotation(eye, Quaternion.Euler(float.Parse(f[5], I), float.Parse(f[4], I), 0f));
            yield return Settle("shot " + f[0] + " (camera placed)", 8f, 120f);
            yield return null;

            try
            {
                RenderTo(OutPrefix + "_shot_" + f[0] + ".png", true);
                GroundAt(eye, eye.y - 2f, out Vector3 ground);
                string line = Holes("shot_" + f[0], eye, ground.y, centre, out bool save, out Texture2D voidTex);
                File.WriteAllBytes(OutPrefix + "_shot_" + f[0] + "_void.png", voidTex.EncodeToPNG());
                Destroy(voidTex);
                s.AppendLine(line);
                Say("wrote shot " + f[0]);
            }
            catch (Exception e)
            {
                Say("shot " + f[0] + " failed: " + e);
            }

            File.WriteAllText(OutPrefix + "_" + Site + "_shots.txt", s.ToString());
        }

        Width = w;
        Height = h;
    }

    /// <summary>
    /// RL_PROBE="x,z,r,step;x,z,r,step..." (Unity): after the stream settles round each point, every collider under a
    /// grid of points (all layers, back faces too): path, height, which way it faces, layer, drawn or not. Written to
    /// &lt;prefix&gt;_&lt;site&gt;_probe.txt.
    /// </summary>
    IEnumerator Probe(string spec)
    {
        var s = new StringBuilder();

        foreach (string one in spec.Split(';'))
        {
            string[] f = one.Split(',');

            if (f.Length < 4)
            {
                continue;
            }

            // 4 fields: a square grid x,z,r,step. 5 fields: a line x0,z0,x1,z1,step.
            bool isLine = f.Length >= 5;
            float x = float.Parse(f[0], I), z = float.Parse(f[1], I);
            float r = isLine ? 0f : float.Parse(f[2], I), step = float.Parse(f[isLine ? 4 : 3], I);
            float x1 = isLine ? float.Parse(f[2], I) : x, z1 = isLine ? float.Parse(f[3], I) : z;
            _focus.position = new Vector3((x + x1) * 0.5f, 0f, (z + z1) * 0.5f);
            yield return Settle("probe " + one, 30f, 300f);
            Physics.SyncTransforms();
            bool back = Physics.queriesHitBackfaces;
            Physics.queriesHitBackfaces = true;
            s.AppendLine("== probe " + one);
            var pts = new List<Vector2>();

            if (isLine)
            {
                float len = new Vector2(x1 - x, z1 - z).magnitude;

                for (float t = 0f; t <= len + 0.001f; t += step)
                {
                    pts.Add(Vector2.Lerp(new Vector2(x, z), new Vector2(x1, z1), len > 0f ? t / len : 0f));
                }
            }
            else
            {
                for (float dz = -r; dz <= r + 0.001f; dz += step)
                {
                    for (float dx = -r; dx <= r + 0.001f; dx += step)
                    {
                        pts.Add(new Vector2(x + dx, z + dz));
                    }
                }
            }

            {
                foreach (Vector2 pt in pts)
                {
                    float dx = pt.x - x, dz = pt.y - z;
                    var o = new Vector3(x + dx, 400f, z + dz);
                    RaycastHit[] hits = Physics.RaycastAll(o, Vector3.down, 1200f, ~0, QueryTriggerInteraction.Ignore);
                    Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
                    var line = new StringBuilder("  (" + (x + dx).ToString("0.0", I) + ", " + (z + dz).ToString("0.0", I) + "): " + (hits.Length == 0 ? "NOTHING" : ""));

                    foreach (RaycastHit h in hits)
                    {
                        Renderer rend = h.collider.GetComponent<Renderer>();
                        line.Append(" [" + PathOf(h.collider.transform, 3) + " y " + h.point.y.ToString("0.00", I) + (h.normal.y < 0f ? " FACES DOWN" : "") + " L" + h.collider.gameObject.layer +
                                    (rend == null ? " no-renderer" : rend.enabled ? "" : " renderer-off") + "]");
                    }

                    s.AppendLine(line.ToString());
                }
            }

            Physics.queriesHitBackfaces = back;

            // Every renderer whose bounds hold the probe's middle column: what is (or should be) drawn there.
            var mid = new Vector2((x + x1) * 0.5f, (z + z1) * 0.5f);
            s.AppendLine("  renderers over (" + mid.x.ToString("0.0", I) + ", " + mid.y.ToString("0.0", I) + "):");

            foreach (Renderer rr in FindObjectsByType<Renderer>(FindObjectsSortMode.None))
            {
                Bounds b = rr.bounds;

                if (mid.x < b.min.x || mid.x > b.max.x || mid.y < b.min.z || mid.y > b.max.z || b.size.x > 3000f)
                {
                    continue;
                }

                Material m = rr.sharedMaterial;
                MeshFilter mf = rr.GetComponent<MeshFilter>();
                Mesh mesh = mf != null ? mf.sharedMesh : null;
                string tex = "";

                if (m != null)
                {
                    foreach (string prop in new[] { "_BaseMap", "_MultMap" })
                    {
                        if (m.HasProperty(prop))
                        {
                            Texture t = m.GetTexture(prop);
                            tex += " " + prop + "=" + (t != null ? t.name + (t is Texture2D t2 ? " " + t2.format : "") : "null");
                        }
                    }
                }

                s.AppendLine("    " + PathOf(rr.transform, 3) + (rr.enabled ? "" : " [OFF]") + (rr.gameObject.activeInHierarchy ? "" : " [INACTIVE]") + " y " + b.min.y.ToString("0.0", I) + ".." + b.max.y.ToString("0.0", I) +
                             " mat " + (m != null ? m.name + " / " + m.shader.name + " queue " + m.renderQueue + (m.HasProperty("_LayerMode") ? " layer " + m.GetFloat("_LayerMode") : "") +
                                                    (m.HasProperty("_Cutoff") ? " cutoff " + m.GetFloat("_Cutoff").ToString("0.00", I) : "") + " kw [" + string.Join(" ", m.shaderKeywords) + "]" + tex : "none") +
                             (mesh != null ? " mesh " + mesh.name + " v " + mesh.vertexCount + (mesh.isReadable ? "" : " (not readable)") + " colors " + mesh.HasVertexAttribute(VertexAttribute.Color) + " uv1 " + mesh.HasVertexAttribute(VertexAttribute.TexCoord1) : ""));
            }

            File.WriteAllText(OutPrefix + "_" + Site + "_probe.txt", s.ToString());
        }

        File.WriteAllText(OutPrefix + "_" + Site + "_probe.txt", s.ToString());
        Finish(0);
    }

    /// <summary>Cells (step m) within radius of a point with no ground-mask collider under them at all.</summary>
    static string Census(Vector3 at, float radius, float step)
    {
        Physics.SyncTransforms();
        int cells = 0, none = 0;
        var clusters = new Dictionary<long, int>();

        for (float z = -radius; z < radius; z += step)
        {
            for (float x = -radius; x < radius; x += step)
            {
                cells++;
                var o = new Vector3(at.x + x + step * 0.5f, at.y + 400f, at.z + z + step * 0.5f);

                if (!Physics.Raycast(o, Vector3.down, 1200f, EtsLayers.GroundMask, QueryTriggerInteraction.Ignore))
                {
                    none++;
                    long key = ((long)Mathf.FloorToInt(o.x / 10f) << 32) ^ (uint)Mathf.FloorToInt(o.z / 10f);
                    clusters.TryGetValue(key, out int n);
                    clusters[key] = n + 1;
                }
            }
        }

        var parts = new List<KeyValuePair<long, int>>(clusters);
        parts.Sort((p, q) => q.Value.CompareTo(p.Value));
        var s = new StringBuilder("census " + radius.ToString("0", I) + " m round at " + step.ToString("0", I) + " m: " + none + " of " + cells + " cells have NO ground");

        for (int i = 0; i < parts.Count && i < 8; i++)
        {
            int cx = (int)(parts[i].Key >> 32), cz = (int)(uint)(parts[i].Key & 0xFFFFFFFF);
            s.Append(i == 0 ? "; 10 m blocks: " : ", ").Append("(" + (cx * 10 + 5) + ", " + (cz * 10 + 5) + ") x" + parts[i].Value);
        }

        return s.ToString();
    }

    static string PathOf(Transform t, int depth)
    {
        string s = t.name;

        for (int i = 0; i < depth && t.parent != null; i++)
        {
            t = t.parent;
            s = t.name + "/" + s;
        }

        return s;
    }

    /// <summary>
    /// Where a hole ray goes through the ground: marching along it 1.5 m at a time (horizontally), the first point
    /// with no ground-mask collider within 40 m under it (the ray is over the gap there).
    /// </summary>
    static bool Locate(Vector3 eye, Vector3 dir, out Vector3 at)
    {
        at = eye;
        float h = new Vector2(dir.x, dir.z).magnitude;

        if (h < 1e-4f)
        {
            return false;
        }

        float step = 1.5f / h;

        for (float t = step; t * h < 2300f; t += step)
        {
            Vector3 q = eye + dir * t;

            if (!Physics.Raycast(q + Vector3.up * 3f, Vector3.down, 43f, EtsLayers.GroundMask, QueryTriggerInteraction.Ignore))
            {
                at = q;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The first collider along the ray that has a renderer (drawn from somewhere: a hole pixel on it means it is not
    /// drawn from this side). Colliders with no renderer are passed and named; the first of them that is ground (the
    /// ground mask) is handed back: a hole over it is invisible ground.
    /// </summary>
    static bool FirstDrawnHit(Vector3 eye, Vector3 dir, out RaycastHit hit, out string passed, out Vector3? invisibleGround)
    {
        RaycastHit[] hits = Physics.RaycastAll(eye, dir, 3000f, ~0, QueryTriggerInteraction.Ignore);
        Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
        var names = new List<string>();
        invisibleGround = null;
        hit = default(RaycastHit);

        foreach (RaycastHit h in hits)
        {
            Renderer r = h.collider.GetComponent<Renderer>();

            Material hm = r != null ? r.sharedMaterial : null;
            bool seeThrough = hm != null && (hm.renderQueue >= 2450 || (hm.HasProperty("_AlphaClipOn") && hm.GetFloat("_AlphaClipOn") > 0.5f) || hm.IsKeywordEnabled("_ALPHATEST_ON"));

            if (r != null && r.enabled && !seeThrough)
            {
                hit = h;
                passed = string.Join(", ", names.ToArray());
                return true;
            }

            if (!invisibleGround.HasValue && ((1 << h.collider.gameObject.layer) & EtsLayers.GroundMask) != 0 && h.normal.y > 0.3f &&
                !h.collider.name.StartsWith("railing", StringComparison.Ordinal) && !h.collider.name.StartsWith("coll", StringComparison.Ordinal))
            {
                invisibleGround = h.point;
                names.Insert(0, PathOf(h.collider.transform, 3));
                continue;
            }

            string n = PathOf(h.collider.transform, 2);

            if (!names.Contains(n) && names.Count < 3)
            {
                names.Add(n);
            }
        }

        passed = string.Join(", ", names.ToArray());
        return false;
    }

    /// <summary>The map renderers whose bounds hold a point's column (not plants, not the whole-tile fill): what should draw there.</summary>
    static string RenderersOver(Vector3 p)
    {
        var names = new List<string>();

        foreach (Renderer rr in FindObjectsByType<Renderer>(FindObjectsSortMode.None))
        {
            Bounds b = rr.bounds;

            if (p.x < b.min.x - 1f || p.x > b.max.x + 1f || p.z < b.min.z - 1f || p.z > b.max.z + 1f || b.size.x > 3000f || IsLeaves(rr))
            {
                continue;
            }

            Material m = rr.sharedMaterial;
            string n = PathOf(rr.transform, 2) + (rr.enabled && rr.gameObject.activeInHierarchy ? "" : " OFF") + " y " + b.min.y.ToString("0", I) + ".." + b.max.y.ToString("0", I) +
                       (m != null && m.HasProperty("_LayerMode") ? " L" + m.GetFloat("_LayerMode").ToString("0", I) : "") + (m != null ? " q" + m.renderQueue : "") + (rr.isPartOfStaticBatch ? " SB" : "") + (rr.isVisible ? " vis" : " NOTVIS");

            if (!names.Contains(n))
            {
                names.Add(n);
            }

            if (names.Count >= 12)
            {
                break;
            }
        }

        return string.Join(" | ", names.ToArray());
    }

    /// <summary>Every collider along a ray, back faces included, nearest first: path, distance, height, facing, drawn or not.</summary>
    static string AllAlong(Vector3 eye, Vector3 dir)
    {
        bool back = Physics.queriesHitBackfaces;
        Physics.queriesHitBackfaces = true;
        RaycastHit[] hits = Physics.RaycastAll(eye, dir, 3000f, ~0, QueryTriggerInteraction.Ignore);
        Physics.queriesHitBackfaces = back;
        Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
        var parts = new List<string>();

        foreach (RaycastHit h in hits)
        {
            Renderer r = h.collider.GetComponent<Renderer>();
            parts.Add(PathOf(h.collider.transform, 2) + " @" + h.distance.ToString("0.0", I) + "m y" + h.point.y.ToString("0.0", I) + (Vector3.Dot(h.normal, dir) > 0f ? " BACK" : "") +
                      (r == null ? " no-r" : r.enabled ? "" : " r-off"));

            if (parts.Count >= 8)
            {
                break;
            }
        }

        return parts.Count == 0 ? "nothing" : string.Join(" | ", parts.ToArray());
    }

    /// <summary>What stands round a hole: the ground-mask colliders within 8 m (for naming the map item).</summary>
    static string Around(Vector3 p)
    {
        var names = new List<string>();

        foreach (Collider c in Physics.OverlapSphere(p, 8f, EtsLayers.GroundMask, QueryTriggerInteraction.Ignore))
        {
            string n = PathOf(c.transform, 2);

            if (!names.Contains(n))
            {
                names.Add(n);
            }

            if (names.Count >= 5)
            {
                break;
            }
        }

        return string.Join(" | ", names.ToArray());
    }

    sealed class HoleCell
    {
        public int Samples;
        public float MinDist = float.MaxValue;
        public Vector3 Sum;
        public string Near = "";
        public Vector3 Dir;
    }

    /// <summary>
    /// Renders the view once more with post off and the sky replaced by magenta; every magenta pixel below the horizon
    /// is a hole. Every 3rd pixel of them is ray-cast: a collider hit means something solid there is not drawn from
    /// this side (a culled underside, an invisible collider); no hit means nothing at all is there, located where the
    /// ray crosses the eye's ground level, in 20 m cells.
    /// </summary>
    string Holes(string view, Vector3 eye, float groundY, Vector3 centre, out bool save, out Texture2D tex)
    {
        UniversalAdditionalCameraData data = _camera.GetUniversalAdditionalCameraData();
        data.renderPostProcessing = false;
        CameraClearFlags flags = _camera.clearFlags;
        Color background = _camera.backgroundColor;
        _camera.clearFlags = CameraClearFlags.SolidColor;
        _camera.backgroundColor = new Color(1f, 0f, 1f, 1f);
        var rt = new RenderTexture(new RenderTextureDescriptor(Width, Height, GraphicsFormat.R8G8B8A8_SRGB, 24) { msaaSamples = 1 });
        rt.Create();
        RenderTexture previous = RenderTexture.active;

        try
        {
            _camera.targetTexture = rt;
            _camera.Render();
            _camera.Render();
            RenderTexture.active = rt;
            tex = new Texture2D(Width, Height, TextureFormat.RGBA32, false, false);
            tex.ReadPixels(new Rect(0, 0, Width, Height), 0, 0);
            tex.Apply(false);
        }
        finally
        {
            RenderTexture.active = previous;
            _camera.targetTexture = null;
            _camera.clearFlags = flags;
            _camera.backgroundColor = background;
            data.renderPostProcessing = true;
            rt.Release();
            Destroy(rt);
        }

        Color32[] px = tex.GetPixels32();
        Transform t = _camera.transform;
        float tanV = Mathf.Tan(_camera.fieldOfView * 0.5f * Mathf.Deg2Rad), tanH = tanV * Width / Height;
        int below = 0, holes = 0, sampled = 0, hitSolid = 0, invisibleHits = 0;
        int[] bands = new int[5]; // <100, 100-400, 400-1200, 1200-2150, beyond (or no crossing)
        var cells = new Dictionary<long, HoleCell>();
        var solids = new Dictionary<string, int>();
        Physics.SyncTransforms();

        for (int y = 0; y < Height; y++)
        {
            float v = ((y + 0.5f) / Height) * 2f - 1f;

            for (int x = 0; x < Width; x++)
            {
                float u = ((x + 0.5f) / Width) * 2f - 1f;
                Vector3 dir = (t.forward + t.right * (u * tanH) + t.up * (v * tanV)).normalized;

                if (dir.y > -0.002f)
                {
                    continue;
                }

                below++;
                Color32 c = px[y * Width + x];

                if (!(c.r > 250 && c.g < 6 && c.b > 250))
                {
                    continue;
                }

                holes++;

                if (x % 3 != 0 || y % 3 != 0)
                {
                    continue;
                }

                sampled++;

                if (FirstDrawnHit(eye, dir, out RaycastHit hit, out string passed, out Vector3? invisibleGround))
                {
                    hitSolid++;
                    string group = PathOf(hit.collider.transform, 3) + (passed.Length > 0 ? " (behind " + passed + ")" : "");
                    solids.TryGetValue(group, out int n);
                    solids[group] = n + 1;
                    continue;
                }

                if (invisibleGround.HasValue)
                {
                    invisibleHits++;
                    string ig = "INVISIBLE GROUND " + passed;
                    solids.TryGetValue(ig, out int ni);
                    solids[ig] = ni + 1;
                }

                Vector3 p;

                if (invisibleGround.HasValue)
                {
                    p = invisibleGround.Value;
                }
                else if (!Locate(eye, dir, out p))
                {
                    float tt = (groundY - eye.y) / dir.y;
                    p = eye + dir * tt;
                }

                float d = new Vector2(p.x - eye.x, p.z - eye.z).magnitude;
                int band = d < 100f ? 0 : d < 400f ? 1 : d < 1200f ? 2 : d < 2150f ? 3 : 4;
                bands[band]++;

                if (band <= 3)
                {
                    long key = ((long)Mathf.FloorToInt(p.x / 6f) << 32) ^ (uint)Mathf.FloorToInt(p.z / 6f);

                    if (!cells.TryGetValue(key, out HoleCell cell))
                    {
                        cells[key] = cell = new HoleCell { Near = Around(p), Dir = dir };
                    }

                    cell.Samples++;
                    cell.Sum += p;
                    cell.MinDist = Mathf.Min(cell.MinDist, d);
                }
            }
        }

        save = holes >= 30;
        var s = new StringBuilder(view + ": " + holes + " hole px of " + below + " below the horizon (" + (below > 0 ? 100.0 * holes / below : 0).ToString("0.000", I) + " %); sampled " + sampled +
                                  ": solid-but-undrawn " + hitSolid + ", over invisible ground " + invisibleHits + ", nothing at <100 m " + bands[0] + ", 100-400 " + bands[1] + ", 400-1200 " + bands[2] + ", 1200-2150 " + bands[3] + ", beyond " + bands[4]);
        var list = new List<KeyValuePair<long, HoleCell>>(cells);
        list.Sort((a, b) => b.Value.Samples.CompareTo(a.Value.Samples));

        for (int i = 0; i < list.Count && i < 10; i++)
        {
            HoleCell cell = list[i].Value;
            Vector3 m = cell.Sum / cell.Samples;
            s.Append(i == 0 ? "\n      void cells: " : "; ").Append("unity (" + m.x.ToString("0", I) + ", " + m.z.ToString("0", I) + ") game (" + (centre.x + m.x).ToString("0", I) + ", " + (centre.z - m.z).ToString("0", I) + ") " +
                                                          cell.MinDist.ToString("0", I) + " m x" + cell.Samples + " y " + m.y.ToString("0.0", I) + " near [" + cell.Near + "]");

            if (i < 3 && cell.Samples >= 5)
            {
                s.Append(" over it {" + RenderersOver(m) + "}");
                s.Append(" ray [" + AllAlong(eye, cell.Dir) + "]");
            }
        }

        var sl = new List<KeyValuePair<string, int>>(solids);
        sl.Sort((a, b) => b.Value.CompareTo(a.Value));

        for (int i = 0; i < sl.Count && i < 6; i++)
        {
            s.Append(i == 0 ? "\n      undrawn solids: " : "; ").Append(sl[i].Key + " x" + sl[i].Value);
        }

        return s.ToString();
    }

    static void Say(string line)
    {
        string text = "[RenderLab] " + line;
        Debug.Log(text);
        Log.AppendLine(DateTime.Now.ToString("HH:mm:ss", I) + " " + line);
    }

    void Start()
    {
        _start = Time.realtimeSinceStartup;
        Application.runInBackground = true;
        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = -1;
        GameObject focus = GameObject.Find("RL Focus");
        _focus = focus != null ? focus.transform : new GameObject("RL Focus").transform;
        StartCoroutine(Run());
    }

    void Update()
    {
        if (!Done && Time.realtimeSinceStartup - _start > TimeoutSeconds)
        {
            Say("TIMEOUT after " + TimeoutSeconds + " s");
            Finish(4);
        }
    }

    void Finish(int code)
    {
        ExitCode = code;

        try
        {
            File.WriteAllText(OutPrefix + "_" + Site + "_runlog.txt", Log.ToString());
        }
        catch (Exception e)
        {
            Debug.LogWarning("[RenderLab] could not write the run log: " + e.Message);
        }

        Done = true;
    }

    IEnumerator Run()
    {
        Say("site " + Site + " centre " + CentreFor(Site).ToString("0.0") + " out " + OutPrefix + " gfx " + SystemInfo.graphicsDeviceType + " " + SystemInfo.graphicsDeviceName +
            " colour space " + QualitySettings.activeColorSpace + " quality " + QualitySettings.names[QualitySettings.GetQualityLevel()] + " pipeline " +
            (GraphicsSettings.currentRenderPipeline != null ? GraphicsSettings.currentRenderPipeline.name : "none"));
        MakeCamera();
        List<View> views = ViewsFor(Site);

        if (views.Count > 0)
        {
            _focus.position = views[0].Position;
        }

        // 1. The first build.
        while (!EtsMapViewer.AnyCompleted)
        {
            yield return new WaitForSeconds(2f);
            Say("loading " + (EtsMapViewer.LoadProgress * 100f).ToString("0", I) + " % " + EtsMapViewer.LoadStage);
        }

        Say("first build complete at " + (Time.realtimeSinceStartup - _start).ToString("0", I) + " s");

        string shots = Environment.GetEnvironmentVariable("RL_SHOTS");

        if (!string.IsNullOrEmpty(shots))
        {
            yield return Shots(shots);
        }

        string probe = Environment.GetEnvironmentVariable("RL_PROBE");

        if (!string.IsNullOrEmpty(probe))
        {
            yield return Probe(probe);
            yield break;
        }

        if (!string.IsNullOrEmpty(shots))
        {
            Finish(0);
            yield break;
        }

        List<SurveyPoint> survey = SurveyFor(Site);

        if (survey.Count > 0)
        {
            yield return Survey(survey);
            yield break;
        }

        int rendered = 0;

        foreach (View view in views)
        {
            if (!string.IsNullOrEmpty(OnlyViews) && Array.IndexOf(OnlyViews.Split(','), view.Name) < 0)
            {
                continue;
            }

            _focus.position = view.Position;
            yield return Settle(view.Name, 25f, 300f);

            string how;
            Pose pose;

            try
            {
                pose = Place(view, out how);
            }
            catch (Exception e)
            {
                Say("view " + view.Name + " could not be placed: " + e);
                continue;
            }

            _camera.transform.SetPositionAndRotation(pose.position, pose.rotation);
            _focus.position = pose.position;

            // The camera moved: let the stream, the LODs and the vegetation catch up round it once more.
            yield return Settle(view.Name + " (camera placed)", 8f, 120f);

            // Not WaitForEndOfFrame: it never resumes in a batchmode editor (nothing presents a frame).
            yield return null;

            try
            {
                string file = OutPrefix + "_" + view.Name + ".png";
                RenderTo(file, true);
                File.WriteAllText(OutPrefix + "_" + view.Name + ".txt", Describe(view, how));
                Say("wrote " + file + " (" + how + ")");
                rendered++;

                // Diagnostic: the same frame with the camera's post-processing off (no tone curve, grading or bloom):
                // what the lighting and the sky put out before the post volume.
                if (NoPost)
                {
                    string raw = OutPrefix + "_" + view.Name + "_nopost.png";
                    RenderTo(raw, false);
                    Say("wrote " + raw);
                }
            }
            catch (Exception e)
            {
                Say("view " + view.Name + " render failed: " + e);
            }
        }

        Finish(rendered > 0 ? 0 : 5);
    }

    void MakeCamera()
    {
        var go = new GameObject("RL Camera");
        go.tag = "MainCamera";
        _camera = go.AddComponent<Camera>();
        _camera.fieldOfView = Fov;
        _camera.nearClipPlane = 0.1f;
        _camera.farClipPlane = Mathf.Max(1000f, EtsClimate.Visibility);
        _camera.allowHDR = true;
        _camera.allowMSAA = true;
        _camera.clearFlags = CameraClearFlags.Skybox;
        UniversalAdditionalCameraData data = go.AddComponent<UniversalAdditionalCameraData>();
        data.renderPostProcessing = true;
        data.antialiasing = AntialiasingMode.None;
        data.volumeLayerMask = 1 << 0;
        data.volumeTrigger = go.transform;
        data.renderShadows = true;
        data.requiresColorOption = CameraOverrideOption.UsePipelineSettings;
        data.requiresDepthOption = CameraOverrideOption.UsePipelineSettings;
        data.SetRenderer(-1);
        _camera.targetTexture = null;
    }

    // ------------------------------------------------------------------ settling

    static FieldInfo _fCandidates, _fCandHead, _fBuilt, _fCombine;

    static int Private(object owner, ref FieldInfo field, string name, bool countOnly)
    {
        try
        {
            if (field == null)
            {
                field = owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            }

            if (field == null)
            {
                return -1;
            }

            object value = field.GetValue(owner);

            if (value == null)
            {
                return -1;
            }

            if (value is int i)
            {
                return i;
            }

            PropertyInfo count = value.GetType().GetProperty("Count");
            return count != null ? (int)count.GetValue(value) : -1;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    string Signature(out string text)
    {
        EtsMapViewer viewer = EtsMapViewer.Current;
        int built = -1, queued = -1, combine = -1;

        if (viewer != null)
        {
            built = Private(viewer, ref _fBuilt, "_built", true);
            int cands = Private(viewer, ref _fCandidates, "_candidates", true);
            int head = Private(viewer, ref _fCandHead, "_candHead", false);
            queued = cands >= 0 && head >= 0 ? cands - head : -1;
            combine = Private(viewer, ref _fCombine, "_combineQueue", true);
        }

        int plants = EtsVegetationBuilder.PlantsPlanted;
        int details = EtsDetailDriver.Pending;
        int fill = EtsGroundFill.Builds;
        text = "built " + built + ", queued " + queued + ", batching " + combine + ", plants " + plants + " (sides waiting " + EtsVegetationBuilder.SidesWaiting + "), detail jobs " + details +
               ", ground fill builds " + fill;
        return built + "/" + queued + "/" + combine + "/" + plants + "/" + details;
    }

    IEnumerator Settle(string what, float minSeconds, float maxSeconds)
    {
        float from = Time.realtimeSinceStartup;
        string last = null;
        float stableSince = from;
        string text = "";

        while (true)
        {
            yield return new WaitForSeconds(1f);
            float now = Time.realtimeSinceStartup;
            string sig = Signature(out text);

            if (sig != last)
            {
                last = sig;
                stableSince = now;
            }

            if (now - from >= minSeconds && now - stableSince >= 6f)
            {
                Say("settled for " + what + " after " + (now - from).ToString("0", I) + " s: " + text);
                yield break;
            }

            if (now - from >= maxSeconds)
            {
                Say("NOT settled for " + what + " after " + maxSeconds + " s (rendering anyway): " + text);
                yield break;
            }
        }
    }

    // ------------------------------------------------------------------ placing

    static bool GroundAt(Vector3 at, float guessY, out Vector3 point)
    {
        Physics.SyncTransforms();

        // Every surface under the point; the one nearest the expected height (a gantry, a canopy or a roof above the
        // yard is not the ground the truck stands on).
        RaycastHit[] hits = Physics.RaycastAll(new Vector3(at.x, guessY + 60f, at.z), Vector3.down, 400f, EtsLayers.GroundMask, QueryTriggerInteraction.Ignore);
        float best = float.MaxValue;
        point = new Vector3(at.x, guessY, at.z);

        foreach (RaycastHit hit in hits)
        {
            float d = Mathf.Abs(hit.point.y - guessY);

            if (d < best)
            {
                best = d;
                point = hit.point;
            }
        }

        return hits.Length > 0;
    }

    static bool IsLeaves(Renderer r)
    {
        Material m = r.sharedMaterial;
        return m != null && m.shader != null && m.shader.name == "SomniumEuroTruck/Leaves";
    }

    /// <summary>One entry per plant: a LODGroup's bounds, or a leaves renderer's own when it has none.</summary>
    /// <summary>Plant names seen by the last Plants call, with counts (for the log).</summary>
    static readonly Dictionary<string, int> PlantKinds = new Dictionary<string, int>();

    /// <summary>
    /// One box per plant EtsVegetationBuilder planted (root "plant &lt;model path&gt;"), standing on its foot and as tall as
    /// the stored model (LODGroup.size x scale: a leaves renderer's own bounds are grown by the reach of its cards, about
    /// twice the tree), plus the map-placed vegetation models (leaves renderers outside any plant root, their renderer
    /// bounds). Bushes are told by height, and by "bush" in the model path.
    /// </summary>
    static List<Bounds> Plants(Vector3 near, float radius)
    {
        var list = new List<Bounds>();
        var seen = new HashSet<int>();
        PlantKinds.Clear();

        foreach (Renderer r in FindObjectsByType<Renderer>(FindObjectsSortMode.None))
        {
            if (!r.enabled || !r.gameObject.activeInHierarchy || !IsLeaves(r))
            {
                continue;
            }

            Transform root = r.transform;

            while (root != null && !root.name.StartsWith("plant ", StringComparison.Ordinal))
            {
                root = root.parent;
            }

            Bounds b;
            string kind;

            if (root != null)
            {
                if (!seen.Add(root.GetInstanceID()))
                {
                    continue;
                }

                LODGroup group = root.GetComponent<LODGroup>();
                float scale = root.lossyScale.y;
                float height = group != null ? group.size * scale : r.bounds.size.y * 0.5f;
                kind = root.name.Substring(6);

                if (kind.IndexOf("bush", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    height = Mathf.Min(height, 4f);
                }

                Vector3 foot = root.position;
                b = new Bounds(foot + Vector3.up * height * 0.5f, new Vector3(height * 0.6f, height, height * 0.6f));
            }
            else
            {
                b = r.bounds;
                kind = "(map model) " + r.gameObject.name;
            }

            if (Flat(b.center - near) <= radius)
            {
                list.Add(b);
                PlantKinds.TryGetValue(kind, out int n);
                PlantKinds[kind] = n + 1;
            }
        }

        return list;
    }

    static string KindsText()
    {
        var parts = new List<string>();

        foreach (KeyValuePair<string, int> k in PlantKinds)
        {
            parts.Add(k.Key + " x" + k.Value);
        }

        parts.Sort(StringComparer.Ordinal);
        return string.Join(", ", parts.ToArray());
    }

    Pose Place(View view, out string how)
    {
        GroundAt(view.Position, view.GroundGuessY, out Vector3 ground);
        Vector3 eye = view.Ground ? ground + Vector3.up * view.EyeHeight : view.Position;
        float yaw = view.Yaw;
        how = "ground " + ground.ToString("0.00");

        if (view.Auto == "trees" || view.Auto == "busy")
        {
            // The yaw (15 degree steps) whose 60 degree cone holds the most plants (trees) or renderers (busy) 25-400 m away.
            var points = new List<Vector3>();

            if (view.Auto == "trees")
            {
                foreach (Bounds b in Plants(eye, 400f))
                {
                    points.Add(b.center);
                }
            }
            else
            {
                foreach (Renderer r in FindObjectsByType<Renderer>(FindObjectsSortMode.None))
                {
                    if (r.enabled && r.gameObject.activeInHierarchy && r.gameObject != _camera.gameObject)
                    {
                        points.Add(r.bounds.center);
                    }
                }
            }

            int best = 0;
            float bestYaw = yaw;

            for (int step = 0; step < 24; step++)
            {
                float y = step * 15f;
                Vector3 dir = Quaternion.Euler(0f, y, 0f) * Vector3.forward;
                int count = 0;

                foreach (Vector3 p in points)
                {
                    Vector3 d = p - eye;
                    d.y = 0f;
                    float m = d.magnitude;

                    if (m < 25f || m > 400f)
                    {
                        continue;
                    }

                    if (Vector3.Angle(dir, d) <= 30f)
                    {
                        count++;
                    }
                }

                if (count > best)
                {
                    best = count;
                    bestYaw = y;
                }
            }

            yaw = bestYaw;
            how += ", auto " + view.Auto + " yaw " + yaw.ToString("0", I) + " (" + best + " of " + points.Count + " in the cone)";
        }
        else if (view.Auto == "tree")
        {
            Pose closeUp;

            if (TreeCloseUp(view, out closeUp, out string found))
            {
                how += ", " + found;
                return closeUp;
            }

            how += ", " + found;
        }

        return new Pose(eye, Quaternion.Euler(view.Pitch, yaw, 0f));
    }

    /// <summary>
    /// A tree (5 m or taller) with a bush (0.4-4.5 m) beside it, nearest the spot first: the camera 11 m from the pair at
    /// 1.7 m above ground within 3 m of the tree's foot, from the first of twelve directions with a clear line of sight
    /// (no collider in between, not inside another plant), looking at the pair.
    /// </summary>
    bool TreeCloseUp(View view, out Pose pose, out string found)
    {
        pose = default(Pose);
        List<Bounds> plants = Plants(view.Position, 1500f);
        var trees = new List<Bounds>();
        var bushes = new List<Bounds>();
        var heights = new int[8];

        foreach (Bounds b in plants)
        {
            heights[Mathf.Clamp((int)(b.size.y / 5f), 0, 7)]++;

            if (b.size.y >= 5f && b.size.y <= 40f)
            {
                trees.Add(b);
            }
            else if (b.size.y >= 0.4f && b.size.y < 4.5f)
            {
                bushes.Add(b);
            }
        }

        trees.Sort((p, q) => Flat(p.center - view.Position).CompareTo(Flat(q.center - view.Position)));
        found = plants.Count + " plants within 1500 m (heights by 5 m: " + string.Join("/", Array.ConvertAll(heights, h => h.ToString())) + "), " + trees.Count + " trees, " + bushes.Count + " bushes" + "; kinds: " + KindsText();

        for (int pass = 0; pass < 2; pass++)
        {
            int looked = 0;

            foreach (Bounds tree in trees)
            {
                if (++looked > 3000)
                {
                    break;
                }

                Bounds bush = tree;
                bool hasBush = false;
                float nearest = 15f;

                foreach (Bounds b in bushes)
                {
                    float d = Flat(b.center - tree.center);

                    if (d < nearest)
                    {
                        nearest = d;
                        bush = b;
                        hasBush = true;
                    }
                }

                if (pass == 0 && !hasBush)
                {
                    continue;
                }

                Vector3 foot = new Vector3(tree.center.x, tree.min.y, tree.center.z);
                Vector3 target = Vector3.Lerp(new Vector3(tree.center.x, tree.min.y + tree.size.y * 0.35f, tree.center.z), bush.center, hasBush ? 0.4f : 0f);
                Vector3 pairMid = hasBush ? (foot + new Vector3(bush.center.x, bush.min.y, bush.center.z)) * 0.5f : foot;

                for (int step = 0; step < 12; step++)
                {
                    Vector3 dir = Quaternion.Euler(0f, step * 30f, 0f) * Vector3.forward;
                    Vector3 at = pairMid + dir * 11f;

                    if (!GroundAt(at, tree.min.y, out Vector3 ground) || Mathf.Abs(ground.y - tree.min.y) > 3f)
                    {
                        continue;
                    }

                    Vector3 eye = ground + Vector3.up * view.EyeHeight;
                    Vector3 toTarget = target - eye;

                    if (Physics.Raycast(eye, toTarget.normalized, toTarget.magnitude - 1.5f, ~0, QueryTriggerInteraction.Ignore))
                    {
                        continue;
                    }

                    bool inside = false;

                    foreach (Bounds p in plants)
                    {
                        Bounds grown = p;
                        grown.Expand(0.6f);

                        if (grown.Contains(eye))
                        {
                            inside = true;
                            break;
                        }
                    }

                    if (inside)
                    {
                        continue;
                    }

                    pose = new Pose(eye, Quaternion.LookRotation(toTarget, Vector3.up));
                    found += "; tree " + tree.size.y.ToString("0.0", I) + " m tall at " + tree.center.ToString("0.0") + (hasBush ? ", bush " + bush.size.y.ToString("0.0", I) + " m at " + bush.center.ToString("0.0") + " (" + nearest.ToString("0.0", I) + " m from the tree)" : ", no bush beside it") +
                             ", camera " + Flat(foot - eye).ToString("0.0", I) + " m from the tree, from " + (step * 30) + " deg, " + Flat(tree.center - view.Position).ToString("0", I) + " m from the spot";
                    return true;
                }
            }
        }

        found += "; NO clear view of a tree";
        return false;
    }

    static float Flat(Vector3 v)
    {
        v.y = 0f;
        return v.magnitude;
    }

    // ------------------------------------------------------------------ rendering

    void RenderTo(string file, bool post)
    {
        _camera.GetUniversalAdditionalCameraData().renderPostProcessing = post;
        var rt = new RenderTexture(new RenderTextureDescriptor(Width, Height, GraphicsFormat.R8G8B8A8_SRGB, 24) { msaaSamples = 1 });
        rt.name = "RL target";
        rt.Create();
        RenderTexture previous = RenderTexture.active;

        try
        {
            _camera.targetTexture = rt;
            _camera.farClipPlane = Mathf.Max(1000f, EtsClimate.Visibility);

            // Twice: the first frame at a new place warms per-camera state (LOD choice, shadow cascades, the leaves' camera frame).
            _camera.Render();
            _camera.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(Width, Height, TextureFormat.RGBA32, false, false);
            tex.ReadPixels(new Rect(0, 0, Width, Height), 0, 0);
            tex.Apply(false);
            File.WriteAllBytes(file, tex.EncodeToPNG());
            Destroy(tex);
        }
        finally
        {
            RenderTexture.active = previous;
            _camera.targetTexture = null;
            rt.Release();
            Destroy(rt);
        }
    }

    string Describe(View view, string how)
    {
        var s = new StringBuilder();
        Transform t = _camera.transform;
        s.AppendLine("view " + view.Name + " site " + Site + " centre game " + CentreFor(Site).ToString("0.0"));
        s.AppendLine("camera unity " + t.position.ToString("0.00") + " euler " + t.eulerAngles.ToString("0.0") + " fov(v) " + _camera.fieldOfView + " near " + _camera.nearClipPlane +
                     " far " + _camera.farClipPlane + " " + Width + "x" + Height + " hdr " + _camera.allowHDR + " post on");
        s.AppendLine("placement: " + how);
        s.AppendLine("fog " + RenderSettings.fog + " " + RenderSettings.fogMode + " " + RenderSettings.fogStartDistance.ToString("0") + "-" + RenderSettings.fogEndDistance.ToString("0") +
                     " density " + RenderSettings.fogDensity.ToString("0.000000", I) + " colour " + RenderSettings.fogColor.ToString("0.000"));
        SphericalHarmonicsL2 sh = RenderSettings.ambientProbe;
        s.AppendLine("ambient " + RenderSettings.ambientMode + " sky " + RenderSettings.ambientSkyColor.ToString("0.000") + " equator " + RenderSettings.ambientEquatorColor.ToString("0.000") +
                     " ground " + RenderSettings.ambientGroundColor.ToString("0.000") + " intensity " + RenderSettings.ambientIntensity.ToString("0.00", I) + " probe DC " +
                     sh[0, 0].ToString("0.000", I) + "/" + sh[1, 0].ToString("0.000", I) + "/" + sh[2, 0].ToString("0.000", I));
        Light sun = RenderSettings.sun;
        s.AppendLine("sun " + (sun != null ? sun.name + " intensity " + sun.intensity.ToString("0.000", I) + " colour " + sun.color.ToString("0.000") + " shadows " + sun.shadows + " strength " +
                                             sun.shadowStrength.ToString("0.00", I) + " forward " + sun.transform.forward.ToString("0.000") + " euler " + sun.transform.eulerAngles.ToString("0.0") : "none"));
        s.AppendLine("skybox " + (RenderSettings.skybox != null ? RenderSettings.skybox.name + " / " + RenderSettings.skybox.shader.name : "none") + " reflection " + RenderSettings.defaultReflectionMode +
                     " intensity " + RenderSettings.reflectionIntensity.ToString("0.00", I));
        EtsClimate climate = EtsMapViewer.Current != null ? EtsMapViewer.Current.Climate : null;
        s.AppendLine("climate " + (climate != null ? climate.ToString() : "none"));
        s.AppendLine("post " + (EtsPostFx.Current != null ? EtsPostFx.Current.Summary : "no EtsPostFx"));

        foreach (Volume v in FindObjectsByType<Volume>(FindObjectsSortMode.None))
        {
            var comps = new List<string>();

            if (v.sharedProfile != null)
            {
                foreach (VolumeComponent c in v.sharedProfile.components)
                {
                    comps.Add(c.GetType().Name + (c.active ? "" : "(off)"));
                }
            }

            s.AppendLine("volume '" + v.name + "' global " + v.isGlobal + " priority " + v.priority + " weight " + v.weight.ToString("0.00", I) + " layer " + v.gameObject.layer + ": " +
                         string.Join(", ", comps.ToArray()));
        }

        Signature(out string sig);
        s.AppendLine("stream: " + sig);
        s.AppendLine("lod bias " + QualitySettings.lodBias + ", shadow distance (pipeline) " +
                     (GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset urp ? urp.shadowDistance.ToString("0", I) + " cascades " + urp.shadowCascadeCount + " msaa " + urp.msaaSampleCount + " hdr " + urp.supportsHDR + " render scale " + urp.renderScale : "?"));
        return s.ToString();
    }
}
