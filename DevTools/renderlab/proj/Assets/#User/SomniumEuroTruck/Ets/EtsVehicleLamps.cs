using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.Rendering;

namespace SomniumSpace.Worlds.SomniumEuroTruck.Ets
{
    /// <summary>
    /// A vehicle's own lamps lit the way the game lights them (2026-10-04, vehicle indicator lamps): on the model's lens
    /// pieces, through each lens's lamp mask, in the colours of the vehicle's lamp setup (EtsLampSetup has the game's
    /// rules and where they were read).
    ///
    /// How it is drawn here. Every piece EtsModelBuilder builds with an "eut2.lamp" material is noted (NoteLampPiece:
    /// its mesh, the source piece, which uv set is texcoord1 - the pmg piece's texcoord mask, nibble 1 - and its
    /// texture_mask). For a vehicle, each such piece gets one small overlay mesh per wire this world drives (front/rear
    /// left/right turn signal, brake, reverse): the piece's triangles whose texcoord1 group has that wire on some mask
    /// channel, with uv = texcoord1 less the group (the shader's fract) and the vertex colour selecting those channels.
    /// The overlay is drawn with the scene's decal shader in its lamp mode (Shaders/EtsDecal.shader, _LampMode 1):
    /// additive, mask^contrast x the wire's colour x the world's exposure x the game's directionality - the game's
    /// "defpost" lamp program. An overlay renderer is enabled while its wire is on; the lens underneath stays the lit
    /// eut2.lamp surface it always was. Overlay meshes are cached per piece and setup, materials per mask and colour.
    ///
    /// Not done: the animated (".anim", sequential LED) lamps - 9 of 1,662 lamp pieces - are left dark (their masks are
    /// flip-book sheets per channel); the beams, positional and daytime-running wires (this world has no headlights).
    /// </summary>
    public sealed class EtsVehicleLamps
    {
        private sealed class LampPiece
        {
            public EtsModelPiece Piece;
            public int UvSet;
            public string Mask;
            public bool Animated;
        }

        private static readonly Dictionary<Mesh, LampPiece> Pieces = new Dictionary<Mesh, LampPiece>();
        private static readonly Dictionary<Mesh, Dictionary<string, Mesh[]>> Overlays = new Dictionary<Mesh, Dictionary<string, Mesh[]>>();
        private static readonly Dictionary<string, Material> Materials = new Dictionary<string, Material>(StringComparer.Ordinal);
        private static readonly List<KeyValuePair<Material, float[]>> Coloured = new List<KeyValuePair<Material, float[]>>();
        private static float _exposure = -1f;
        private static bool _shaderReported;

        private static readonly int BaseMapId = Shader.PropertyToID("_BaseMap");
        private static readonly int LampModeId = Shader.PropertyToID("_LampMode");
        private static readonly int LampColorId = Shader.PropertyToID("_LampColor");
        private static readonly int LampContrastId = Shader.PropertyToID("_LampContrast");
        private static readonly int SrcBlendId = Shader.PropertyToID("_SrcBlend");
        private static readonly int DstBlendId = Shader.PropertyToID("_DstBlend");
        private static readonly int CullId = Shader.PropertyToID("_Cull");

        /// <summary>The overlay objects' name prefix ("lamp:" + the game's wire name).</summary>
        public const string OverlayPrefix = "lamp:";

        /// <summary>
        /// The engine's lamp_contrast is not in any file of the demo (an engine constant). 1 is the value under which the
        /// program's "alpha x 2.2" makes the alpha channel decode like the sRGB colour channels - the reading that makes
        /// the program self-consistent - so 1 it is.
        /// </summary>
        public const float LampContrast = 1f;

        /// <summary>2026-10-14: the brightest an exposed lamp colour gets (linear; the tone curve's white is near 8).</summary>
        public const float LampCap = 8f;

        /// <summary>Lamp pieces noted since the start (all models, map lamps included).</summary>
        public static int NotedPieces => Pieces.Count;

        /// <summary>
        /// EtsModelBuilder.Build, for every drawn piece whose material is an eut2.lamp effect. Main thread; cheap (a
        /// dictionary entry per shared mesh).
        /// </summary>
        public static void NoteLampPiece(Mesh mesh, EtsModelPiece piece, EtsMaterialDef def)
        {
            if (mesh == null || piece == null || def == null)
            {
                return;
            }

            // The world's lamp driver (the trucks' and trailers' lamps follow their truck), made with the first lens (and
            // again if a world reload took it).
            try
            {
                EtsVehicleLampDriver.Ensure();
            }
            catch (Exception)
            {
                // The driver's trouble is never the model builder's.
            }

            if (Pieces.ContainsKey(mesh))
            {
                return;
            }

            def.Textures.TryGetValue("texture_mask", out string mask);

            // The pmg piece's texcoord mask: nibble k is the uv set the effect's texcoord k reads (F = none). The lamp mask
            // is texcoord1 (/effect/def/eut2/eut2_lamp.sui: texture_mask uv_stream texcoord1). Measured over every lamp
            // piece of the demo's vehicles: set 1 on 1,256, set 0 on 406 (one-set pieces read the base uv for both).
            int set = (int)((piece.TexcoordMask >> 4) & 0xF);

            if (set >= piece.Uvs.Length)
            {
                set = 0;
            }

            Pieces[mesh] = new LampPiece { Piece = piece, UvSet = set, Mask = mask, Animated = def.EffectHas(".anim") };
        }

        // ------------------------------------------------------------------ one vehicle

        private readonly List<Renderer>[] _wires = new List<Renderer>[EtsLampSetup.WireCount];
        private readonly bool[] _on = new bool[EtsLampSetup.WireCount];

        /// <summary>Lamp pieces seen on this vehicle, how many got overlays, and how many were animated or maskless.</summary>
        public int LampPieces, LitPieces, AnimatedPieces, MasklessPieces;

        /// <summary>Where the colours came from.</summary>
        public string SetupSource = "";

        private EtsVehicleLamps()
        {
            for (int w = 0; w < _wires.Length; w++)
            {
                _wires[w] = new List<Renderer>();
            }
        }

        /// <summary>Overlay renderers for a wire (EtsLampSetup.Wire*).</summary>
        public int Count(int wire)
        {
            return wire >= 0 && wire < _wires.Length ? _wires[wire].Count : 0;
        }

        /// <summary>True when the vehicle has any turn-signal overlay.</summary>
        public bool HasTurnSignals => _wires[0].Count + _wires[1].Count + _wires[2].Count + _wires[3].Count > 0;

        /// <summary>
        /// Overlays on every eut2.lamp piece under `root` for which `exclude` is false (another vehicle hung under this
        /// one). Pieces that already carry overlays (a second call, a clone of a built template) keep theirs. Main thread.
        /// </summary>
        public static EtsVehicleLamps Build(Transform root, EtsLampSetup setup, Func<Transform, bool> exclude)
        {
            var lamps = new EtsVehicleLamps { SetupSource = setup != null ? setup.Source : "" };

            if (root == null)
            {
                return lamps;
            }

            setup = setup ?? EtsLampSetup.For(null, null);
            MeshFilter[] filters = root.GetComponentsInChildren<MeshFilter>(true);

            foreach (MeshFilter filter in filters)
            {
                if (filter == null || filter.sharedMesh == null || !Pieces.TryGetValue(filter.sharedMesh, out LampPiece piece))
                {
                    continue;
                }

                Transform at = filter.transform;

                if (at.name.StartsWith(OverlayPrefix, StringComparison.Ordinal) || (exclude != null && exclude(at)))
                {
                    continue;
                }

                lamps.LampPieces++;

                if (piece.Animated)
                {
                    lamps.AnimatedPieces++;
                    continue;
                }

                if (string.IsNullOrEmpty(piece.Mask))
                {
                    lamps.MasklessPieces++;
                    continue;
                }

                // Already done (a template's clone, or a vehicle built twice): take what is there.
                bool existing = false;

                for (int c = 0; c < at.childCount; c++)
                {
                    Transform child = at.GetChild(c);

                    if (child.name.StartsWith(OverlayPrefix, StringComparison.Ordinal))
                    {
                        existing = true;
                        lamps.Adopt(child);
                    }
                }

                if (existing)
                {
                    lamps.LitPieces++;
                    continue;
                }

                Mesh[] meshes = OverlayMeshes(filter.sharedMesh, piece, setup);
                bool any = false;

                for (int w = 0; w < meshes.Length; w++)
                {
                    if (meshes[w] == null)
                    {
                        continue;
                    }

                    Material material = MaterialFor(piece.Mask, setup, w);

                    if (material == null)
                    {
                        continue;
                    }

                    var overlay = new GameObject(OverlayPrefix + EtsLampSetup.WireNames[w]) { layer = at.gameObject.layer };
                    overlay.transform.SetParent(at, false);
                    overlay.AddComponent<MeshFilter>().sharedMesh = meshes[w];
                    MeshRenderer renderer = overlay.AddComponent<MeshRenderer>();
                    renderer.sharedMaterial = material;
                    renderer.shadowCastingMode = ShadowCastingMode.Off;
                    renderer.receiveShadows = false;
                    renderer.lightProbeUsage = LightProbeUsage.Off;
                    renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
                    renderer.enabled = false;
                    lamps._wires[w].Add(renderer);
                    any = true;
                }

                if (any)
                {
                    lamps.LitPieces++;
                }
            }

            return lamps;
        }

        /// <summary>The overlays already under `root` (a clone of a template Build ran on).</summary>
        public static EtsVehicleLamps Collect(Transform root)
        {
            var lamps = new EtsVehicleLamps();

            if (root == null)
            {
                return lamps;
            }

            foreach (MeshRenderer renderer in root.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (renderer != null && renderer.name.StartsWith(OverlayPrefix, StringComparison.Ordinal))
                {
                    lamps.Adopt(renderer.transform);
                }
            }

            return lamps;
        }

        private void Adopt(Transform overlay)
        {
            int wire = Array.IndexOf(EtsLampSetup.WireNames, overlay.name.Substring(OverlayPrefix.Length));
            Renderer renderer = overlay.GetComponent<Renderer>();

            if (wire >= 0 && renderer != null && !_wires[wire].Contains(renderer))
            {
                renderer.enabled = false;
                _wires[wire].Add(renderer);
            }
        }

        /// <summary>
        /// Puts every overlay into the LOD level its lens is in, so a car's lamps go with the model they belong to
        /// (EtsTrafficModels' templates; their LOD meshes carry lamp pieces of their own).
        /// </summary>
        public void JoinLods(LODGroup group)
        {
            if (group == null)
            {
                return;
            }

            LOD[] lods = group.GetLODs();
            bool changed = false;

            for (int l = 0; l < lods.Length; l++)
            {
                var renderers = new List<Renderer>(lods[l].renderers ?? new Renderer[0]);
                int before = renderers.Count;

                foreach (List<Renderer> wire in _wires)
                {
                    foreach (Renderer overlay in wire)
                    {
                        Renderer lens = overlay != null && overlay.transform.parent != null ? overlay.transform.parent.GetComponent<Renderer>() : null;

                        if (lens != null && Array.IndexOf(lods[l].renderers, lens) >= 0 && !renderers.Contains(overlay))
                        {
                            renderers.Add(overlay);
                        }
                    }
                }

                if (renderers.Count != before)
                {
                    lods[l].renderers = renderers.ToArray();
                    changed = true;
                }
            }

            if (changed)
            {
                group.SetLODs(lods);
            }
        }

        /// <summary>Switches the wires: the left and right turn signals (front and rear), the brake and the reverse lamps.</summary>
        public void Set(bool left, bool right, bool brake, bool reverse)
        {
            RefreshExposure();
            Switch(EtsLampSetup.WireFrontLeftTurn, left);
            Switch(EtsLampSetup.WireRearLeftTurn, left);
            Switch(EtsLampSetup.WireFrontRightTurn, right);
            Switch(EtsLampSetup.WireRearRightTurn, right);
            Switch(EtsLampSetup.WireBrake, brake);
            Switch(EtsLampSetup.WireReverse, reverse);
        }

        /// <summary>The turn signals only; the brake and reverse lamps keep their state.</summary>
        public void SetTurn(bool left, bool right)
        {
            Set(left, right, _on[EtsLampSetup.WireBrake], _on[EtsLampSetup.WireReverse]);
        }

        /// <summary>
        /// 2026-10-14 (headlights): the light switch as the game's wires take it - <paramref name="lights"/> 0 off, 1 parking
        /// (front, rear and side positional), 2 dipped (low beam + positional); <paramref name="high"/> the main beam (with the
        /// dipped beam on, or flashed); <paramref name="daytime"/> the daytime-running lamps (engine running, beams off).
        /// </summary>
        public void SetHead(int lights, bool high, bool daytime)
        {
            RefreshExposure();
            Switch(EtsLampSetup.WireFrontPositional, lights >= 1);
            Switch(EtsLampSetup.WireRearPositional, lights >= 1);
            Switch(EtsLampSetup.WireSidePositional, lights >= 1);
            Switch(EtsLampSetup.WireLowBeam, lights >= 2);
            Switch(EtsLampSetup.WireHighBeam, high);
            Switch(EtsLampSetup.WireDaytime, daytime && lights < 2);
        }

        /// <summary>Overlay renderers that light the beams or positional lamps (0: a vehicle without them).</summary>
        public int HeadCount => _wires[EtsLampSetup.WireLowBeam].Count + _wires[EtsLampSetup.WireHighBeam].Count + _wires[EtsLampSetup.WireFrontPositional].Count +
                                _wires[EtsLampSetup.WireRearPositional].Count;

        private void Switch(int wire, bool on)
        {
            if (_on[wire] == on)
            {
                return;
            }

            _on[wire] = on;
            List<Renderer> list = _wires[wire];

            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] != null)
                {
                    list[i].enabled = on;
                }
            }
        }

        /// <summary>"N lamp pieces lit the game's way (overlays: left turn a+b, ...)" for the log lines.</summary>
        public string Describe()
        {
            return LitPieces + " lamp pieces lit the game's way (of " + LampPieces + " eut2.lamp pieces" +
                   (AnimatedPieces > 0 ? ", " + AnimatedPieces + " animated LED left dark" : "") +
                   (MasklessPieces > 0 ? ", " + MasklessPieces + " without a mask" : "") + "; overlays: left turn " +
                   _wires[EtsLampSetup.WireFrontLeftTurn].Count + " front + " + _wires[EtsLampSetup.WireRearLeftTurn].Count + " rear, right turn " +
                   _wires[EtsLampSetup.WireFrontRightTurn].Count + " front + " + _wires[EtsLampSetup.WireRearRightTurn].Count + " rear, brake " +
                   _wires[EtsLampSetup.WireBrake].Count + ", reverse " + _wires[EtsLampSetup.WireReverse].Count + ", low beam " + _wires[EtsLampSetup.WireLowBeam].Count +
                   ", high beam " + _wires[EtsLampSetup.WireHighBeam].Count + ", positional " + (_wires[EtsLampSetup.WireFrontPositional].Count + _wires[EtsLampSetup.WireRearPositional].Count + _wires[EtsLampSetup.WireSidePositional].Count) +
                   ", daytime " + _wires[EtsLampSetup.WireDaytime].Count + "; colours from " +
                   (string.IsNullOrEmpty(SetupSource) ? "the default setup" : SetupSource) + ", x exposure " +
                   EtsClimate.AppliedExposure.ToString("0.000", CultureInfo.InvariantCulture) + ")";
        }

        // ------------------------------------------------------------------ shared meshes and materials

        /// <summary>One overlay mesh per wire (null where the piece has no texel on it) for a lamp piece under a setup.</summary>
        private static Mesh[] OverlayMeshes(Mesh lens, LampPiece lamp, EtsLampSetup setup)
        {
            if (!Overlays.TryGetValue(lens, out Dictionary<string, Mesh[]> bySetup))
            {
                bySetup = new Dictionary<string, Mesh[]>(StringComparer.Ordinal);
                Overlays[lens] = bySetup;
            }

            if (bySetup.TryGetValue(setup.Key, out Mesh[] cached))
            {
                return cached;
            }

            var result = new Mesh[EtsLampSetup.WireCount];
            bySetup[setup.Key] = result;
            EtsModelPiece piece = lamp.Piece;
            float[] uv = piece.Uvs.Length > lamp.UvSet ? piece.Uvs[lamp.UvSet] : null;

            if (uv == null || piece.Positions == null || piece.Indices == null)
            {
                return result;
            }

            int[] indices = piece.Indices;
            var positions = new List<Vector3>();
            var normals = new List<Vector3>();
            var uvs = new List<Vector2>();
            var colours = new List<Color32>();
            var triangles = new List<int>();

            for (int w = 0; w < EtsLampSetup.WireCount; w++)
            {
                positions.Clear();
                normals.Clear();
                uvs.Clear();
                colours.Clear();
                triangles.Clear();

                for (int t = 0; t + 2 < indices.Length; t += 3)
                {
                    int a = indices[t], b = indices[t + 1], c = indices[t + 2];

                    // The group is the whole part of texcoord1.u (the vertex program: uint(u), clamped to 5); one
                    // group per triangle, read at its centre. A negative u has no group the game defines: left dark.
                    float centre = (uv[a * 2] + uv[b * 2] + uv[c * 2]) / 3f;

                    if (centre < 0f)
                    {
                        continue;
                    }

                    int group = Mathf.Min((int)Mathf.Floor(centre), EtsLampSetup.Groups - 1);
                    byte r = setup.WireOf[group * 4] == w ? (byte)255 : (byte)0;
                    byte g = setup.WireOf[group * 4 + 1] == w ? (byte)255 : (byte)0;
                    byte bl = setup.WireOf[group * 4 + 2] == w ? (byte)255 : (byte)0;
                    byte al = setup.WireOf[group * 4 + 3] == w ? (byte)255 : (byte)0;

                    if ((r | g | bl | al) == 0)
                    {
                        continue;
                    }

                    var selector = new Color32(r, g, bl, al);

                    foreach (int v in new[] { a, b, c })
                    {
                        triangles.Add(positions.Count);
                        positions.Add(new Vector3(piece.Positions[v * 3], piece.Positions[v * 3 + 1], -piece.Positions[v * 3 + 2]));
                        normals.Add(piece.Normals != null
                            ? new Vector3(piece.Normals[v * 3], piece.Normals[v * 3 + 1], -piece.Normals[v * 3 + 2])
                            : Vector3.forward);
                        uvs.Add(new Vector2(uv[v * 2] - group, uv[v * 2 + 1]));
                        colours.Add(selector);
                    }
                }

                if (triangles.Count == 0)
                {
                    continue;
                }

                var mesh = new Mesh { name = OverlayPrefix + EtsLampSetup.WireNames[w] };
                mesh.SetVertices(positions);
                mesh.SetNormals(normals);
                mesh.SetUVs(0, uvs);
                mesh.SetColors(colours);
                mesh.SetTriangles(triangles, 0);
                mesh.RecalculateBounds();
                result[w] = mesh;
            }

            return result;
        }

        private static Material MaterialFor(string mask, EtsLampSetup setup, int wire)
        {
            float[] colour = setup.Colours[wire];
            string key = mask + "|" + colour[0].ToString("R", CultureInfo.InvariantCulture) + "," + colour[1].ToString("R", CultureInfo.InvariantCulture) + "," +
                         colour[2].ToString("R", CultureInfo.InvariantCulture);

            if (Materials.TryGetValue(key, out Material cached))
            {
                return cached;
            }

            Material material = null;

            try
            {
                Shader shader = EtsMaterialCache.DecalShader != null ? EtsMaterialCache.DecalShader : Shader.Find("SomniumEuroTruck/Decal");

                if (shader == null || shader.FindPropertyIndex("_LampMode") < 0)
                {
                    if (!_shaderReported)
                    {
                        _shaderReported = true;
                        Debug.LogWarning("[SomniumEuroTruck] Indicator lamps: the scene's decal shader " + (shader == null ? "is missing" : "has no lamp mode (an older EtsDecal)") +
                                         "; vehicle lamps stay dark.");
                    }
                }
                else
                {
                    Texture2D texture = EtsEngine.Textures != null ? EtsEngine.Textures.Load(mask, false) : null;

                    if (texture != null)
                    {
                        material = new Material(shader) { name = "lamp " + EtsLampSetup.WireNames[wire] + " " + mask };
                        material.SetTexture(BaseMapId, texture);
                        material.SetFloat(LampModeId, 1f);
                        material.SetFloat(LampContrastId, LampContrast);
                        material.SetFloat(SrcBlendId, (float)BlendMode.One);
                        material.SetFloat(DstBlendId, (float)BlendMode.One);
                        material.SetFloat(CullId, (float)CullMode.Back);
                        material.renderQueue = (int)RenderQueue.Transparent;
                        material.SetVector(LampColorId, Exposed(colour));
                        Coloured.Add(new KeyValuePair<Material, float[]>(material, colour));
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[SomniumEuroTruck] Indicator lamps: lamp material for " + mask + " failed: " + e.Message);
                material = null;
            }

            Materials[key] = material;
            return material;
        }

        /// <summary>The wire's linear colour in this world's units: every light and colour carries EtsClimate's exposure.</summary>
        private static Vector4 Exposed(float[] colour)
        {
            float e = EtsClimate.AppliedExposure;

            // 2026-10-14: at night the exposure is ~1000 x the day's, and a brake lamp's 35 / 0.35 / 0.025 x 230 would clip every
            // channel to white under the tone curve; the lamp keeps its hue, capped at LampCap (by day, 0.2 x 35 = 7, nothing changes).
            float peak = Mathf.Max(colour[0], Mathf.Max(colour[1], colour[2])) * e;

            if (peak > LampCap)
            {
                e *= LampCap / peak;
            }

            return new Vector4(colour[0] * e, colour[1] * e, colour[2] * e, 1f);
        }

        /// <summary>The climate moved the exposure (a new weather slot, another site): every lamp colour follows.</summary>
        private static void RefreshExposure()
        {
            // 2026-10-14: the exposure now follows the day (0.2 .. ~310): the colours move when it changed by 2 %, not every frame.
            if (Mathf.Abs(_exposure - EtsClimate.AppliedExposure) <= 0.02f * Mathf.Max(1e-4f, _exposure))
            {
                return;
            }

            _exposure = EtsClimate.AppliedExposure;

            for (int i = Coloured.Count - 1; i >= 0; i--)
            {
                if (Coloured[i].Key == null)
                {
                    Coloured.RemoveAt(i);
                    continue;
                }

                Coloured[i].Key.SetVector(LampColorId, Exposed(Coloured[i].Value));
            }
        }
    }
}
