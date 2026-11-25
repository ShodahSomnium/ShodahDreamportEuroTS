using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace SomniumSpace.Worlds.SomniumEuroTruck.Ets
{
    /// <summary>
    /// Street lamps at night (2026-10-14, time of day and weather).
    ///
    /// How the game lights them: a lamp model's head carries a locator whose hookup names a light source with its own
    /// parameters after '#' (lamp_01_ger.pmd: "template.light.lamp.y.normal.fadelong.deg90#height:9.92;target:10.1"); the unit
    /// (/unit/hookup/lights/lamp/template.light.lamp.sii, spotlight/, omni/) states the light: setup lumen_hue_saturation,
    /// diffuse_color (6500 lumen, hue 41, saturation 54 % for "y" - sodium yellow; "n" 46 / 22 neutral; "b" 190 / 12 blue-white),
    /// range 33 m, outer_angle 130 (or 90). The engine draws them as deferred spot lights with a flare at the lamp - hundreds
    /// of real lights, beyond a VR budget.
    ///
    /// Here, cheaply: under each such lamp a light pool on the ground (a flat quad at the hookup's height below the lamp,
    /// radius height x tan(outer_angle / 2) capped at the range, falling off as cos^3 of the angle off the vertical - a spot
    /// light on a flat floor) drawn additively by the decal shader's night mode (3), and a round glow at the lamp (mode 4).
    /// The pool's level is lumens / 6500 x (10 m / height)^2 in the vertex colour, times EtsNightLights.PoolLevel x exposure;
    /// the engine's lumen-to-radiance scale is not in the files (set by eye in the render lab against base_19, iberia_12).
    /// Both materials draw only while the lamps are on (EtsNightLights). One small mesh per lamp-carrying model part: two
    /// draw calls a lamp post, nothing by day.
    /// </summary>
    public static class EtsStreetLamps
    {
        public static bool Enabled = true;

        /// <summary>Glow radius (m) and level (linear, not exposed: a flare is drawn at a fixed brightness).</summary>
        public static float GlowRadius = 0.9f;
        public static float GlowLevel = 2.2f;

        private sealed class Source
        {
            public float Lumens;
            public float Range;
            public float Outer;
            public Color Colour;
        }

        private static readonly Dictionary<string, string[]> HookupsByModel = new Dictionary<string, string[]>(StringComparer.Ordinal);
        private static Dictionary<string, Source> _sources;
        private static Material _pool, _glow;
        private static Texture2D _poolTexture;
        private static bool _failed;
        private static readonly List<Vector3> Vertices = new List<Vector3>();
        private static readonly List<Vector3> Normals = new List<Vector3>();
        private static readonly List<Vector2> Uvs = new List<Vector2>();
        private static readonly List<Color32> Colours = new List<Color32>();
        private static readonly List<int> Indices = new List<int>();

        public static int Lamps { get; private set; }

        private struct Grounding
        {
            public Transform Pool;
            public float Height;
        }

        private static readonly List<Grounding> Pending = new List<Grounding>();
        private static int _grounded, _ungrounded;

        /// <summary>
        /// While the lamps are on (EtsNightLights.Update, every quarter second): lays up to 40 new pools on the ground under their
        /// lamps - a ray straight down from 0.6 m under the lamp onto the ground layers (roads, terrain) - 6 cm up, so the pool
        /// is not lost under a kerb or a road a little above the lamp's base. A ray that finds nothing keeps the file's height.
        /// </summary>
        public static void Ground()
        {
            int budget = 40;

            for (int i = Pending.Count - 1; i >= 0 && budget > 0; i--, budget--)
            {
                Grounding g = Pending[i];
                Pending.RemoveAt(i);

                if (g.Pool == null)
                {
                    continue;
                }

                Vector3 lamp = g.Pool.position + Vector3.up * g.Height;

                if (Physics.Raycast(lamp - Vector3.up * 0.6f, Vector3.down, out RaycastHit hit, g.Height + 6f, EtsLayers.GroundMask, QueryTriggerInteraction.Ignore))
                {
                    Vector3 p = g.Pool.position;
                    p.y = hit.point.y + 0.06f;
                    g.Pool.position = p;
                    _grounded++;
                }
                else
                {
                    _ungrounded++;
                }
            }
        }

        public static string Describe()
        {
            return Lamps + " street lamps (" + _grounded + " pools on the ground, " + _ungrounded + " at the file's height, " + Pending.Count + " waiting)";
        }

        /// <summary>EtsModelBuilder.Build, for every built part: lamps from the part's locators' hookups, under <paramref name="partObject"/>.</summary>
        public static void NotePart(EtsModelData model, EtsModelPart part, Transform partObject)
        {
            if (!Enabled || _failed || model == null || part == null || partObject == null || part.LocatorCount <= 0 || string.IsNullOrEmpty(model.Path) ||
                model.Path.StartsWith("/vehicle/", StringComparison.Ordinal))
            {
                return;
            }

            try
            {
                string[] hookups = Hookups(model);
                bool any = false;

                for (int l = part.LocatorStart; l < part.LocatorStart + part.LocatorCount && l < hookups.Length; l++)
                {
                    if (hookups[l] != null && hookups[l].StartsWith("template.light.lamp.", StringComparison.Ordinal))
                    {
                        any = true;
                        break;
                    }
                }

                if (!any || !Ensure())
                {
                    return;
                }

                BuildLamps(model, part, partObject, hookups);
            }
            catch (Exception e)
            {
                _failed = true;
                Debug.LogWarning("[SomniumEuroTruck] Street lamps: off after an error: " + e.GetType().Name + ": " + e.Message);
            }
        }

        private static void BuildLamps(EtsModelData model, EtsModelPart part, Transform partObject, string[] hookups)
        {
            Vertices.Clear(); Normals.Clear(); Uvs.Clear(); Colours.Clear(); Indices.Clear();
            int poolQuads = 0;
            var glowVertices = new List<Vector3>();
            var glowNormals = new List<Vector3>();
            var glowUvs = new List<Vector2>();
            var glowColours = new List<Color32>();
            var glowIndices = new List<int>();
            var done = new List<Vector3>();

            for (int l = part.LocatorStart; l < part.LocatorStart + part.LocatorCount && l < model.Locators.Length && l < hookups.Length; l++)
            {
                string hookup = hookups[l];

                if (hookup == null || !hookup.StartsWith("template.light.lamp.", StringComparison.Ordinal))
                {
                    continue;
                }

                int hash = hookup.IndexOf('#');
                string unit = hash >= 0 ? hookup.Substring(0, hash) : hookup;

                if (!_sources.TryGetValue(unit, out Source source))
                {
                    continue;
                }

                Vector3 at = EtsModelBuilder.ToUnity(model.Locators[l].Position);

                // The game's lamps often carry two hookups at one locator (a yellow and a neutral variant): one lamp.
                bool twin = false;

                foreach (Vector3 seen in done)
                {
                    twin |= (seen - at).sqrMagnitude < 0.01f;
                }

                if (twin)
                {
                    continue;
                }

                done.Add(at);
                float height = Mathf.Max(1f, Param(hookup, "height", at.y));
                float radius = Mathf.Min(source.Range, height * Mathf.Tan(Mathf.Clamp(source.Outer, 10f, 170f) * 0.5f * Mathf.Deg2Rad));
                float level = Mathf.Clamp01(source.Lumens / 6500f * (10f / height) * (10f / height) * 0.5f);
                Color c = source.Colour * level;
                var colour = new Color32((byte)Mathf.Clamp(c.r * 255f, 0f, 255f), (byte)Mathf.Clamp(c.g * 255f, 0f, 255f), (byte)Mathf.Clamp(c.b * 255f, 0f, 255f), 255);

                // The pool: a flat quad under the lamp, first at the hookup's height below it; laid on the ground itself by a ray
                // down from the lamp once the lamps are on (Ground: the model stands where the map put it by then).
                Vertices.Clear(); Normals.Clear(); Uvs.Clear(); Colours.Clear(); Indices.Clear();
                Vertices.Add(new Vector3(-radius, 0f, -radius));
                Vertices.Add(new Vector3(-radius, 0f, radius));
                Vertices.Add(new Vector3(radius, 0f, radius));
                Vertices.Add(new Vector3(radius, 0f, -radius));
                Uvs.Add(new Vector2(0f, 0f)); Uvs.Add(new Vector2(0f, 1f)); Uvs.Add(new Vector2(1f, 1f)); Uvs.Add(new Vector2(1f, 0f));

                for (int k = 0; k < 4; k++)
                {
                    Normals.Add(Vector3.up);
                    Colours.Add(colour);
                }

                Indices.Add(0); Indices.Add(1); Indices.Add(2); Indices.Add(0); Indices.Add(2); Indices.Add(3);
                Transform pool = AddRenderer(partObject, "street lamp pool", _pool, Vertices, Normals, Uvs, Colours, Indices, false);
                pool.localPosition = new Vector3(at.x, at.y - height + 0.05f, at.z);
                Pending.Add(new Grounding { Pool = pool, Height = height });
                poolQuads++;

                // The glow at the lamp.
                Color g = source.Colour * 0.5f;
                var glowColour = new Color32((byte)(g.r * 255f), (byte)(g.g * 255f), (byte)(g.b * 255f), 255);
                int gb = glowVertices.Count;

                for (int k = 0; k < 4; k++)
                {
                    glowVertices.Add(at);
                    glowNormals.Add(new Vector3(GlowRadius, 0f, 0f));
                    glowColours.Add(glowColour);
                }

                glowUvs.Add(new Vector2(0f, 0f)); glowUvs.Add(new Vector2(0f, 1f)); glowUvs.Add(new Vector2(1f, 1f)); glowUvs.Add(new Vector2(1f, 0f));
                glowIndices.Add(gb); glowIndices.Add(gb + 1); glowIndices.Add(gb + 2); glowIndices.Add(gb); glowIndices.Add(gb + 2); glowIndices.Add(gb + 3);
                Lamps++;
            }

            if (poolQuads == 0)
            {
                return;
            }

            AddRenderer(partObject, "street lamp glow", _glow, glowVertices, glowNormals, glowUvs, glowColours, glowIndices, true);
        }

        private static Transform AddRenderer(Transform parent, string name, Material material, List<Vector3> v, List<Vector3> n, List<Vector2> uv, List<Color32> c, List<int> i, bool glow)
        {
            var mesh = new Mesh { name = name };
            mesh.SetVertices(v);
            mesh.SetNormals(n);
            mesh.SetUVs(0, uv);
            mesh.SetColors(c);
            mesh.SetTriangles(i, 0);
            mesh.RecalculateBounds();

            if (glow)
            {
                // The vertices all sit on the lamps; the drawn quads reach a radius further (and 0.4 m toward the camera).
                Bounds bounds = mesh.bounds;
                bounds.Expand(GlowRadius * 2f + 1f);
                mesh.bounds = bounds;
            }

            var go = new GameObject(name) { layer = parent.gameObject.layer };
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            MeshRenderer renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = LightProbeUsage.Off;
            renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            return go.transform;
        }

        /// <summary>A number from the hookup's parameters ("#height:9.92;target:10.1").</summary>
        private static float Param(string hookup, string key, float fallback)
        {
            int at = hookup.IndexOf(key + ":", StringComparison.Ordinal);

            if (at < 0)
            {
                return fallback;
            }

            int start = at + key.Length + 1, end = start;

            while (end < hookup.Length && hookup[end] != ';')
            {
                end++;
            }

            return EtsSiiValue.ParseFloat(hookup.Substring(start, end - start), fallback);
        }

        private static bool Ensure()
        {
            if (_pool != null && _glow != null && _sources != null)
            {
                return true;
            }

            Shader shader = EtsMaterialCache.DecalShader != null ? EtsMaterialCache.DecalShader : Shader.Find("SomniumEuroTruck/Decal");

            if (shader == null || shader.FindPropertyIndex("_LampMode") < 0 || EtsEngine.Files == null)
            {
                _failed = true;
                return false;
            }

            _sources = new Dictionary<string, Source>(StringComparer.Ordinal);

            foreach (string folder in new[] { "/unit/hookup/lights/lamp" })
            {
                if (!EtsEngine.Files.TryList(folder, out var files, out _))
                {
                    continue;
                }

                foreach (string file in files)
                {
                    if (!file.EndsWith(".sii", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    EtsSiiFile sii = EtsEngine.Files.ReadSii(folder + "/" + file);

                    if (sii == null)
                    {
                        continue;
                    }

                    foreach (EtsSiiUnit unit in sii.OfType("light_source"))
                    {
                        float[] diffuse = unit.GetFloats("diffuse_color");

                        if (diffuse.Length < 3)
                        {
                            continue;
                        }

                        // lumen_hue_saturation: (lumens, hue degrees, saturation %).
                        Color colour = Color.HSVToRGB(Mathf.Repeat(diffuse[1] / 360f, 1f), Mathf.Clamp01(diffuse[2] / 100f), 1f).linear;
                        _sources[unit.Name] = new Source
                        {
                            Lumens = diffuse[0],
                            Range = unit.GetFloat("range", 33f),
                            Outer = unit.GetFloat("outer_angle", 120f),
                            Colour = colour
                        };
                    }
                }
            }

            // The pool's falloff: a spot light straight down on a flat floor, cos^3 of the angle off the vertical, out to the
            // cone's edge (u, v 0..1 across the quad; the edge at r = 1 is height x tan(outer / 2), so tan = 2.14 r for 130).
            const int Size = 64;
            _poolTexture = new Texture2D(Size, Size, TextureFormat.RGBA32, false, true) { name = "ets street lamp pool", wrapMode = TextureWrapMode.Clamp };
            var pixels = new Color32[Size * Size];

            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    float dx = (x + 0.5f) / Size * 2f - 1f, dy = (y + 0.5f) / Size * 2f - 1f;
                    float r = Mathf.Sqrt(dx * dx + dy * dy);
                    float cos = 1f / Mathf.Sqrt(1f + 4.6f * r * r);
                    float edge = Mathf.Clamp01((1f - r) * 6f);
                    byte v = (byte)Mathf.Clamp(cos * cos * cos * edge * 255f, 0f, 255f);
                    pixels[y * Size + x] = new Color32(v, v, v, 255);
                }
            }

            _poolTexture.SetPixels32(pixels);
            _poolTexture.Apply(false, true);

            _pool = new Material(shader) { name = "ets street lamp pool" };
            _pool.SetTexture("_BaseMap", _poolTexture);
            _pool.SetFloat("_LampMode", 3f);
            _pool.SetFloat("_SrcBlend", (float)BlendMode.One);
            _pool.SetFloat("_DstBlend", (float)BlendMode.One);
            _pool.SetFloat("_Cull", (float)CullMode.Off);
            _pool.renderQueue = (int)RenderQueue.Transparent - 90;

            _glow = new Material(shader) { name = "ets street lamp glow" };
            _glow.SetFloat("_LampMode", 4f);
            _glow.SetFloat("_SrcBlend", (float)BlendMode.One);
            _glow.SetFloat("_DstBlend", (float)BlendMode.One);
            _glow.SetFloat("_Cull", (float)CullMode.Off);
            _glow.SetVector("_LampColor", new Vector4(GlowLevel, GlowLevel, GlowLevel, 1f));
            _glow.renderQueue = (int)RenderQueue.Transparent + 10;

            EtsNightLights.RegisterPool(_pool);
            EtsNightLights.RegisterGlow(_glow);
            Debug.Log("[SomniumEuroTruck] Street lamps: " + _sources.Count + " light sources read from /unit/hookup/lights/lamp; lamp posts get a light pool and a glow, lit with the lamps.");
            return true;
        }

        /// <summary>Each locator's hookup string (the pmg's string pool at +88 / +92, the locator's offset into it), by locator index.</summary>
        private static string[] Hookups(EtsModelData model)
        {
            if (HookupsByModel.TryGetValue(model.Path, out string[] cached))
            {
                return cached;
            }

            var names = new string[model.Locators.Length];
            HookupsByModel[model.Path] = names;
            string pmgPath = model.Path.EndsWith(".pmd", StringComparison.Ordinal) ? model.Path.Substring(0, model.Path.Length - 1) + "g" : null;
            byte[] pmg = pmgPath != null && EtsEngine.Files != null ? EtsEngine.Files.Read(pmgPath) : null;

            if (pmg == null || pmg.Length < 112)
            {
                return names;
            }

            int poolOffset = BitConverter.ToInt32(pmg, 88);
            int poolSize = BitConverter.ToInt32(pmg, 92);

            if (poolOffset <= 0 || poolSize <= 0 || poolOffset + poolSize > pmg.Length)
            {
                return names;
            }

            for (int i = 0; i < names.Length; i++)
            {
                int at = model.Locators[i].Hookup;

                if (at < 0 || at >= poolSize)
                {
                    continue;
                }

                int start = poolOffset + at, end = start;

                while (end < poolOffset + poolSize && pmg[end] != 0)
                {
                    end++;
                }

                names[i] = end > start ? Encoding.ASCII.GetString(pmg, start, end - start) : null;
            }

            return names;
        }
    }
}
