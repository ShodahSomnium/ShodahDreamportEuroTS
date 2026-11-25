using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace SomniumSpace.Worlds.SomniumEuroTruck.Ets
{
    /// <summary>
    /// The writing on the map's signs (2026-10-14, sign texts). The boards of most direction signs are blank in their
    /// models; the game draws what they say at run time, in two ways, and neither was drawn before:
    ///
    /// 1. LEGACY BOARDS (6,642 signs of the demo map; round the Dover HQ every direction sign within 2 km): the map
    ///    item carries boards of three tokens - road, city1, city2 - and the model has locators hooked to
    ///    /unit/hookup/sign_*.sii sign_overlay units (model_type road/city1/city2, board_index, scale 0.85 for the
    ///    "small" ones). Each shows /material/overlay/&lt;token&gt;.mat - a ready-made picture of the name or road number
    ///    ("uk_dover_b" = city_uk_dover_b.tobj, 256 x 64; "uk_a2" = road_uk_a2.tobj, 128 x 128) - centred on the
    ///    locator. The overlay models the exe names (/model/sign/overlay/city.pmd ...) are not in the 1.61 archives, so
    ///    the quad is sized one centimetre per texel: that fits every board measured - the UK small-road board's two
    ///    lines are 0.68 m apart for 0.64 m pictures, the A20 plate (1.39 x 0.77 m) takes the 1.28 m picture's lettering
    ///    (0.90 x 0.53 m) inside it.
    /// 2. TEMPLATES (12,503 signs; the French signs at Calais, the Czech distance boards at Prague, gas price boards):
    ///    composed by EtsSignPlanner/EtsSignRaster on a worker thread into one picture per area, uploaded once per
    ///    distinct sign (same template + same overrides share it) and released when the last sign using it unloads.
    ///
    /// Faces are quads in the locator's frame (it faces its -Z and the text runs along its -X; the board locators
    /// measured - sr_left_uk, 3hw_crs_exit_g_uk, sr_right_fr - are turned 180 degrees about Y, so the face looks out of the board's front), drawn with the world's decal
    /// shader (the game's eut2.retroreflective.decal.over is a decal over the board), and join the sign's LOD group.
    /// </summary>
    public static class EtsSignFaces
    {
        public static bool Enabled = true;

        public static int Overlays, OverlaysMissing, TemplateSigns, TemplateFaces, SharedFaces, FaceTextures, FaceFailures;
        public static double WorkerMs, UploadMs;

        private static EtsSignLibrary _library;
        private static EtsSignArt _art;
        private static EtsWorker _worker;
        private static EtsFileSystem _files;
        private static bool _loggedOverlay, _loggedTemplate, _loggedFailure;
        private static readonly Dictionary<string, string[]> HookupsByModel = new Dictionary<string, string[]>(StringComparer.Ordinal);
        private static readonly Dictionary<string, Entry> Entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private static readonly Stopwatch Clock = new Stopwatch();

        private sealed class Face
        {
            public Vector3 Position;
            public Quaternion Rotation;
            public float Width, Height;
            public Material Material;
            public Texture2D Texture;
            public string Area;
        }

        private sealed class Entry
        {
            public readonly List<Face> Faces = new List<Face>();
            public readonly List<GameObject> Waiting = new List<GameObject>();
            public bool Pending;
            public int References;
        }

        public static string Report()
        {
            return Overlays + " sign overlays" + (OverlaysMissing > 0 ? " (" + OverlaysMissing + " pictures missing)" : "") + ", " + TemplateSigns +
                   " template signs (" + TemplateFaces + " faces, " + SharedFaces + " shared, " + FaceTextures + " live textures" +
                   (FaceFailures > 0 ? ", " + FaceFailures + " failed" : "") + ")";
        }

        private static bool Ready()
        {
            if (EtsEngine.Files == null || EtsEngine.Materials == null) return false;

            if (_files != EtsEngine.Files)
            {
                _files = EtsEngine.Files;
                _library = new EtsSignLibrary(_files);
                _art = new EtsSignArt(_files);
                HookupsByModel.Clear();
            }

            return true;
        }

        /// <summary>
        /// Main thread, right after a map sign's model is built under <paramref name="root"/> (before its LOD group is made):
        /// adds the legacy overlays now and queues the template faces.
        /// </summary>
        public static void Decorate(GameObject root, EtsModelData model, string modelPath, EtsMapSign item)
        {
            if (!Enabled || root == null || model == null || item == null || model.Locators.Length == 0) return;
            bool legacy = false;

            foreach (ulong token in item.Boards)
            {
                if (token != 0) { legacy = true; break; }
            }

            if (!legacy && string.IsNullOrEmpty(item.Template)) return;

            try
            {
                if (!Ready()) return;
                string[] hookups = Hookups(model, modelPath);
                if (legacy) AddOverlays(root, model, hookups, item);
                if (!string.IsNullOrEmpty(item.Template)) AddTemplate(root, model, hookups, item);
            }
            catch (Exception e)
            {
                FaceFailures++;

                if (!_loggedFailure)
                {
                    _loggedFailure = true;
                    Debug.LogWarning("[SomniumEuroTruck] Sign texts: failed on sign " + EtsToken.Decode(item.ModelToken) + " (" + e.GetType().Name + ": " + e.Message + "); logged once.");
                }
            }
        }

        private static string[] Hookups(EtsModelData model, string modelPath)
        {
            string key = EtsEngine.ModelKey(modelPath);
            if (HookupsByModel.TryGetValue(key, out string[] cached)) return cached;
            string pmg = key.EndsWith(".pmd", StringComparison.OrdinalIgnoreCase) ? key.Substring(0, key.Length - 4) + ".pmg" : key + ".pmg";
            string[] names = EtsSignPlanner.Hookups(_files.Read(pmg), model);
            HookupsByModel[key] = names;
            return names;
        }

        // ------------------------------------------------------------------ legacy overlays

        private static void AddOverlays(GameObject root, EtsModelData model, string[] hookups, EtsMapSign item)
        {
            for (int i = 0; i < model.Locators.Length && i < hookups.Length; i++)
            {
                if (!_library.Overlay(hookups[i], out EtsSignOverlayHookup overlay)) continue;
                int slot = overlay.Board * 3 + overlay.Type;
                if (slot < 0 || slot >= item.Boards.Length || item.Boards[slot] == 0) continue;
                string token = EtsToken.Decode(item.Boards[slot]);
                string matPath = "/material/overlay/" + token + ".mat";

                if (!_files.Exists(matPath))
                {
                    OverlaysMissing++;
                    continue;
                }

                Material material = EtsEngine.Materials.Load(matPath);
                Texture texture = material != null ? (material.HasProperty("_BaseMap") ? material.GetTexture("_BaseMap") : material.mainTexture) : null;

                if (texture == null)
                {
                    OverlaysMissing++;
                    continue;
                }

                float scale = overlay.Scale > 0f ? overlay.Scale : 1f;
                float width = texture.width * 0.01f * scale, height = texture.height * 0.01f * scale;
                EtsModelLocator l = model.Locators[i];
                AddQuad(root, "sign overlay " + token, ToUnity(l.Position), ToUnityRotation(l.Rotation), width, height, material);
                Overlays++;

                if (!_loggedOverlay)
                {
                    _loggedOverlay = true;
                    Debug.Log("[SomniumEuroTruck] Sign texts: legacy board overlays on (first: sign." + EtsToken.Decode(item.ModelToken) + " " + hookups[i] +
                              " = " + token + ", " + texture.width + "x" + texture.height + " -> " + width.ToString("0.00") + " x " + height.ToString("0.00") + " m); " +
                              _library.OverlayHookupCount + " sign_overlay hookups.");
                }
            }
        }

        // ------------------------------------------------------------------ templates

        private static void AddTemplate(GameObject root, EtsModelData model, string[] hookups, EtsMapSign item)
        {
            string key = Key(model.Path, item);
            TemplateSigns++;

            if (Entries.TryGetValue(key, out Entry entry))
            {
                if (entry.Pending)
                {
                    entry.Waiting.Add(root);
                }
                else
                {
                    SharedFaces += entry.Faces.Count;
                    Attach(root, key, entry);
                }

                return;
            }

            entry = new Entry { Pending = true };
            entry.Waiting.Add(root);
            Entries[key] = entry;

            if (_worker == null)
            {
                _worker = new EtsWorker("Ets sign faces");
            }

            EtsSignFacePump.Ensure();
            EtsSignLibrary library = _library;
            EtsSignArt art = _art;
            string template = item.Template;
            EtsMapSignArea[] areas = item.Areas;
            EtsMapSignOverride[] overrides = item.Overrides;
            string signName = EtsToken.Decode(item.ModelToken);

            _worker.Post(() =>
            {
                var watch = Stopwatch.StartNew();
                List<EtsSignFacePlan> plans = EtsSignPlanner.Plan(library, art, model, hookups, template, areas, overrides, out string note);
                return new object[] { plans, note, watch.Elapsed.TotalMilliseconds };
            }, (result, failure) => Finish(key, entry, signName, template, result as object[], failure));
        }

        private static void Finish(string key, Entry entry, string signName, string template, object[] result, Exception failure)
        {
            entry.Pending = false;
            var plans = result != null ? result[0] as List<EtsSignFacePlan> : null;

            if (failure != null || plans == null)
            {
                FaceFailures++;

                if (!_loggedFailure)
                {
                    _loggedFailure = true;
                    Debug.LogWarning("[SomniumEuroTruck] Sign texts: " + template + " failed (" + (failure != null ? failure.GetType().Name + ": " + failure.Message : "no result") + "); logged once.");
                }
            }
            else
            {
                WorkerMs += (double)result[2];
                Clock.Restart();
                Shader shader = EtsMaterialCache.DecalShader;

                foreach (EtsSignFacePlan plan in plans)
                {
                    if (shader == null) break;
                    var texture = new Texture2D(plan.PixelsWide, plan.PixelsHigh, TextureFormat.RGBA32, true, false)
                    {
                        name = "sign face " + template + " " + plan.Area,
                        wrapMode = TextureWrapMode.Clamp,
                        filterMode = FilterMode.Trilinear,
                        anisoLevel = 4
                    };
                    texture.SetPixelData(plan.Rgba, 0);
                    texture.Apply(true, false);
                    texture.Compress(true);
                    texture.Apply(false, true);
                    var material = new Material(shader) { name = texture.name };
                    material.SetTexture("_BaseMap", texture);
                    if (material.HasProperty("_Cull")) material.SetFloat("_Cull", 2f);
                    entry.Faces.Add(new Face
                    {
                        Position = ToUnity(plan.Position),
                        Rotation = ToUnityRotation(plan.Rotation),
                        Width = plan.Width,
                        Height = plan.Height,
                        Material = material,
                        Texture = texture,
                        Area = plan.Area
                    });
                    FaceTextures++;
                    TemplateFaces++;
                }

                UploadMs += Clock.Elapsed.TotalMilliseconds;

                if (!_loggedTemplate && entry.Faces.Count > 0)
                {
                    _loggedTemplate = true;
                    EtsSignFacePlan first = plans[0];
                    Debug.Log("[SomniumEuroTruck] Sign texts: template faces on (first: sign." + signName + " " + template + ", " + plans.Count + " area(s), " + first.Area + " " +
                              first.Width.ToString("0.00") + " x " + first.Height.ToString("0.00") + " m at " + first.PixelsWide + "x" + first.PixelsHigh + ", " + first.Items +
                              " items; " + ((double)result[2]).ToString("0") + " ms on the worker, " + Clock.Elapsed.TotalMilliseconds.ToString("0.0") + " ms upload)" +
                              (result[1] != null ? "; " + result[1] : "") + (shader == null ? "; NO decal shader, faces skipped" : "") + ".");
                }
            }

            foreach (GameObject root in entry.Waiting)
            {
                if (root != null) Attach(root, key, entry);
            }

            entry.Waiting.Clear();

            // Nothing to keep (no sign left, or nothing drawn - e.g. a fuel price whose @@locale@@ word was not loaded yet): forgotten, so the
            // next time such a sign streams in it is composed again.
            if (entry.References == 0)
            {
                Release(key, entry);
            }
        }

        private static void Attach(GameObject root, string key, Entry entry)
        {
            if (entry.Faces.Count == 0) return;

            foreach (Face face in entry.Faces)
            {
                AddQuad(root, "sign face " + face.Area, face.Position, face.Rotation, face.Width, face.Height, face.Material);
            }

            entry.References++;
            root.AddComponent<EtsSignFaceRef>().Key = key;

            // A face that arrives after the model joins its LOD group (built in EtsSignBuilder with the same fraction).
            LODGroup group = root.GetComponent<LODGroup>();

            if (group != null)
            {
                LOD[] lods = group.GetLODs();
                if (lods.Length > 0) EtsSemaphores.SmallDetailCulling(root, lods[0].screenRelativeTransitionHeight);
            }
        }

        /// <summary>A sign carrying shared faces was destroyed (EtsSignFaceRef).</summary>
        public static void Unref(string key)
        {
            if (key == null || !Entries.TryGetValue(key, out Entry entry)) return;
            entry.References--;
            if (entry.References <= 0 && !entry.Pending) Release(key, entry);
        }

        private static void Release(string key, Entry entry)
        {
            foreach (Face face in entry.Faces)
            {
                if (face.Material != null) UnityEngine.Object.Destroy(face.Material);
                if (face.Texture != null) UnityEngine.Object.Destroy(face.Texture);
                FaceTextures--;
            }

            entry.Faces.Clear();
            Entries.Remove(key);
        }

        /// <summary>Main thread, every frame (EtsSignFacePump): finished faces are uploaded, about 2 ms a frame at most.</summary>
        public static void Pump()
        {
            if (_worker == null) return;
            var watch = Stopwatch.StartNew();
            _worker.Drain(() => watch.Elapsed.TotalMilliseconds < 2.0);
        }

        public static int Waiting => _worker != null ? _worker.Waiting : 0;

        /// <summary>The sign's identity for sharing: model, template, area records and every override value.</summary>
        private static string Key(string modelPath, EtsMapSign item)
        {
            ulong h = 14695981039346656037UL;

            void Mix(ulong v)
            {
                for (int i = 0; i < 8; i++)
                {
                    h ^= (v >> (i * 8)) & 0xFF;
                    h *= 1099511628211UL;
                }
            }

            foreach (EtsMapSignArea a in item.Areas)
            {
                Mix(a.Area);
                Mix(a.Flags);
                Mix(a.Value);
                Mix(a.Board);
            }

            foreach (EtsMapSignOverride o in item.Overrides)
            {
                Mix(o.Id);
                Mix(o.Area);

                foreach (EtsMapSignAttribute a in o.Attributes)
                {
                    Mix(a.Type);
                    Mix(a.Index);
                    Mix(a.Number);
                    Mix(BitConverter.ToUInt32(BitConverter.GetBytes(a.Float), 0));
                    Mix(a.Token);

                    if (a.Text != null)
                    {
                        foreach (char c in a.Text) Mix(c);
                    }
                }
            }

            return modelPath + "|" + item.Template + "|" + h.ToString("x16");
        }

        // ------------------------------------------------------------------ quads

        private static Vector3 ToUnity(float[] p)
        {
            return EtsModelBuilder.ToUnity(p);
        }

        private static Quaternion ToUnityRotation(float[] q)
        {
            return EtsModelBuilder.ToUnityRotation(q);
        }

        private static void AddQuad(GameObject root, string name, Vector3 position, Quaternion rotation, float width, float height, Material material)
        {
            AddQuad(root, name, position, rotation, Quad(width, height), material);
        }

        private static void AddQuad(GameObject root, string name, Vector3 position, Quaternion rotation, Mesh mesh, Material material)
        {
            var go = new GameObject(name);
            go.layer = root.layer;
            go.transform.SetParent(root.transform, false);
            go.transform.localPosition = position;
            go.transform.localRotation = rotation;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = true;
            renderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            renderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
        }

        /// <summary>
        /// A width x height quad in a locator's Unity frame: it faces local +Z (the game's -Z) and its picture runs along
        /// local -X, so a locator turned 180 degrees about Y shows it upright and readable from the board's front. uv v = 0
        /// is the picture's top row (the world uploads every texture top row first). Vertex colour 128 = 1 for the decal
        /// shader's x2 vertex colour. Every quad owns its 4-vertex mesh: the map viewer frees every non-shared mesh of an
        /// item it unloads (EtsMapViewer), which is what reclaims these - a cached quad shared by several signs was freed
        /// with the first sign to unload and every other sign's text vanished (lab, first pass of 2026-10-14).
        /// </summary>
        private static Mesh Quad(float width, float height)
        {
            string key = width.ToString("0.000") + "x" + height.ToString("0.000");
            Mesh mesh;
            float hw = width * 0.5f, hh = height * 0.5f;
            mesh = new Mesh { name = "sign face " + key };
            mesh.vertices = new[] { new Vector3(hw, -hh, 0f), new Vector3(hw, hh, 0f), new Vector3(-hw, hh, 0f), new Vector3(-hw, -hh, 0f) };
            mesh.uv = new[] { new Vector2(0f, 1f), new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f) };
            mesh.normals = new[] { Vector3.forward, Vector3.forward, Vector3.forward, Vector3.forward };
            var grey = new Color32(128, 128, 128, 128);
            mesh.colors32 = new[] { grey, grey, grey, grey };
            mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
