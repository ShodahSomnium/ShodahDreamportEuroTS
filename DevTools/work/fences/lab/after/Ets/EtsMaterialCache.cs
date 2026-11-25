using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace SomniumSpace.Worlds.SomniumEuroTruck.Ets
{
    /// <summary>
    /// Turns the game's material files into Unity materials, once each.
    ///
    /// The game has its own shader family - eut2.dif, eut2.dif.spec, eut2.dif.spec.tsnmap and so
    /// on, with suffixes for alpha, decals, luminance and glass. None of it exists in Unity, so
    /// every effect is mapped onto the render pipeline's Lit shader and the effect name is read
    /// for the few things that change how a surface must be drawn: whether it is cut out by
    /// alpha, whether it is glass, whether it glows, whether it should be drawn at all.
    ///
    /// The mapping is deliberately coarse. A truck that looks like a truck in Somnium's own
    /// lighting is the goal; matching the game's renderer is not.
    /// </summary>
    public sealed class EtsMaterialCache
    {
        private readonly EtsFileSystem _files;
        private readonly EtsTextureCache _textures;
        private readonly Dictionary<string, Material> _byPath = new Dictionary<string, Material>(StringComparer.Ordinal);
        private readonly HashSet<string> _reported = new HashSet<string>(StringComparer.Ordinal);
        private Shader _shader;
        private Material _fallback;

        private static readonly int BaseMap = Shader.PropertyToID("_BaseMap");
        private static readonly int MainTex = Shader.PropertyToID("_MainTex");
        private static readonly int BaseColor = Shader.PropertyToID("_BaseColor");
        private static readonly int BumpMap = Shader.PropertyToID("_BumpMap");
        private static readonly int Smoothness = Shader.PropertyToID("_Smoothness");
        private static readonly int EnvironmentReflections = Shader.PropertyToID("_EnvironmentReflections");
        private static readonly int Metallic = Shader.PropertyToID("_Metallic");
        private static readonly int DepthBias = Shader.PropertyToID("_DepthBias");
        private static readonly int Surface = Shader.PropertyToID("_Surface");
        private static readonly int Blend = Shader.PropertyToID("_Blend");
        private static readonly int SrcBlend = Shader.PropertyToID("_SrcBlend");
        private static readonly int DstBlend = Shader.PropertyToID("_DstBlend");
        private static readonly int ZWrite = Shader.PropertyToID("_ZWrite");
        private static readonly int AlphaClip = Shader.PropertyToID("_AlphaClip");
        private static readonly int Cutoff = Shader.PropertyToID("_Cutoff");
        private static readonly int MetallicGlossMap = Shader.PropertyToID("_MetallicGlossMap");
        private static readonly int MultMap = Shader.PropertyToID("_MultMap");
        private static readonly int AlphaClipOn = Shader.PropertyToID("_AlphaClipOn");
        private static readonly int LayerMode = Shader.PropertyToID("_LayerMode");
        private static readonly int EmissionMap = Shader.PropertyToID("_EmissionMap");
        private static readonly int EmissionColor = Shader.PropertyToID("_EmissionColor");
        private static readonly int Cull = Shader.PropertyToID("_Cull");
        private static readonly int BumpScale = Shader.PropertyToID("_BumpScale");
        private static readonly int MaskMap = Shader.PropertyToID("_MaskMap");
        private static readonly int CastsShadowId = Shader.PropertyToID("_CastsShadow");
        private static readonly int Band = Shader.PropertyToID("_Band");
        private static readonly int LodSelectorId = Shader.PropertyToID("_LodSelector");
        private static readonly int AmodMask = Shader.PropertyToID("_AmodMask");
        private static readonly int AmodFactors = Shader.PropertyToID("_AmodFactors");

        /// <summary>
        /// Whether the scene's two-layer shader has the game's amod and oclu joins (layer modes 5 and 6,
        /// with _AmodMask and _AmodFactors; see the Multi shader request in details/textures). -1 until
        /// the first material is built on the main thread, which asks the shader once; SecondLayer only
        /// reads the answer, so it stays safe to call from anywhere. Until then (and with an older shader)
        /// amod keeps mode 1 and oclu stays on Lit, exactly as before.
        /// </summary>
        private static int _multiHasMaskModes = -1;
        private static Shader _multiShaderAsked;

        private static bool MultiHasMaskModes()
        {
            if (_multiHasMaskModes < 0 || _multiShaderAsked != MultiShader)
            {
                Shader multi = FindMulti();
                _multiShaderAsked = multi;

                if (multi == null)
                {
                    return false; // Not known yet: asked again once the scene provides it.
                }

                _multiHasMaskModes = multi.FindPropertyIndex("_AmodMask") >= 0 ? 1 : 0;
                Debug.Log("[SomniumEuroTruck] material: two-layer shader " + (_multiHasMaskModes == 1
                    ? "has the amod/oclu modes (5, 6): amod decals by texture_mask and vertex alpha, oclu ambient occlusion on."
                    : "has no amod/oclu modes (no _AmodMask): amod keeps mode 1, oclu stays on Lit."));
            }

            return _multiHasMaskModes == 1;
        }

        /// <summary>
        /// Shader for the game's leaves effects (eut2.leaves.*: every tree, bush and grass card), which
        /// expand camera-facing cards, sway in the wind and are coloured by the vertex colour's red only.
        /// Set by the scene (EtsMapViewer._leavesShader) before the first material is made, with a
        /// Shader.Find fallback, as DecalShader and MultiShader are. Without it leaves stay on URP Lit.
        /// </summary>
        public static Shader LeavesShader;

        /// <summary>
        /// The game's own alpha discard for leaves: its fragment program
        /// (/effect/eut2/_shd/d98ecb96da47988d6532e49c773a71dc.glsl.fso, the non-instanced tree) discards
        /// where base alpha less the fade is under 0.05. The Lit path keeps 0.4 (Build).
        /// </summary>
        public const float LeavesCutoff = 0.05f;

        /// <summary>
        /// The game's alpha test for every effect that has one. Read out of the compiled programs of all 2,884 eut2
        /// effects (harness ProgramBld4): the 727 that discard at all discard where alpha (texture alpha, times the
        /// vertex alpha on most) is under 0.05 - eut2.dif.spec.a, .shadow.a, the amod ...asafew.a chains, add.env.a,
        /// eut2.grass alike - and none uses any other constant. Clipped at 0.4 here, 161,659 of the 1,637,681 m2 of
        /// cut-out surface round Szczecin, Cambridge and Dover had its texels in the band between, drawn by the game and
        /// thrown away here (ProgramBld3): 67 % of the heavy-factory walls (buildings_002), 92 % of the warehouse metal
        /// trims (generic_metal_painted_04_trim), 94 % of admin_07_de's roof trim, 72 % of the port's sheds, 60 % of the
        /// silos - buildings with holes you could see through.
        /// </summary>
        public const float GameAlphaTest = 0.05f;

        /// <summary>The cutoff the world used before, kept where the game's own test was not measured: the non-over
        /// decals (decal pass, no discard found), lamp flares, and leaves drawn on Lit when the leaves shader is missing.</summary>
        public const float LegacyCutoff = 0.4f;

        /// <summary>The alpha test a cut-out material gets (Build). Pure, for the harness.</summary>
        public static float CutoutThreshold(EtsMaterialDef def)
        {
            if (def == null || def.EffectHas("decal") || def.EffectHas("leaves") || def.EffectHas("flare"))
            {
                return LegacyCutoff;
            }

            return GameAlphaTest;
        }

        /// <summary>Materials built so far with the game's window effect (drawn solid), and cut-outs by threshold.</summary>
        public static int WindowMaterials, CutoutsAtGameTest, CutoutsAtLegacy, GlassMaterials;

        private static void CountWindow(string path)
        {
            if (++WindowMaterials == 1)
            {
                Debug.Log("[SomniumEuroTruck] material: eut2.window drawn solid (first " + path + ") - the game's window is an opaque picture of a room, not glass; " +
                          "cut-outs clip at the game's alpha test " + GameAlphaTest.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + ".");
            }
        }

        private static float CountCutout(float threshold)
        {
            if (threshold < LegacyCutoff) CutoutsAtGameTest++;
            else CutoutsAtLegacy++;
            return threshold;
        }

        /// <summary>For the viewer's "Runs:" line (EtsRunBuilder.Report).</summary>
        public static string BuildingsReport()
        {
            return WindowMaterials + " window materials drawn solid (were 35 % glass), " + GlassMaterials + " map glass materials letting through the game's tint^2 (about 34 %, was 85 %), " +
                   CutoutsAtGameTest + " cut-out materials at the game's alpha test " + GameAlphaTest.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) +
                   " (were 0.40; " + CutoutsAtLegacy + " decal/leaves/flare kept at 0.40)";
        }

        /// <summary>
        /// The leaves shader when it can run here, else null (leaves then stay on URP Lit). A shader the
        /// device cannot run would draw every tree, bush and grass card in the error colour, so an
        /// unsupported one counts as missing (EtsMapViewer logs "NOT SUPPORTED").
        /// </summary>
        private static Shader FindLeaves()
        {
            if (LeavesShader == null)
            {
                LeavesShader = Shader.Find("SomniumEuroTruck/Leaves");
            }

            return LeavesShader != null && LeavesShader.isSupported ? LeavesShader : null;
        }

        public int Count => _byPath.Count;

        /// <summary>
        /// Colour for the game's truckpaint effect. In the game the paint job definition supplies
        /// it and the surface's texture is only a mask; here it is set by whoever builds the
        /// truck, before the first material is made, because materials are cached. A truckpaint
        /// material is cached per path and per this colour (Load), so whoever sets it for a build
        /// of their own - the traffic sets white round its cars and puts the truck's colour back -
        /// never takes, nor leaves behind, another's paint.
        /// </summary>
        public static Color PaintColor = Color.white;

        /// <summary>
        /// Shader for "decal.over" pieces (lane lines, dirt, tyre marks): it multiplies texture alpha
        /// by vertex alpha as the game does. Set by the scene before the first material is made;
        /// without it those pieces fall back to URP Lit transparent and show far too strongly.
        /// </summary>
        public static Shader DecalShader;

        /// <summary>
        /// Shader for the game's two-layer effects - mult, amod, iamod, building, window - which lay
        /// a second texture over the first through a second uv set. A third of everything in view
        /// uses one, and URP's Lit has no second layer, so without this they are drawn with only
        /// their base texture: for a building that is the small tiled concrete or brick underneath,
        /// and every bit of its character is in the layer that was being dropped. Set by the scene,
        /// with a Shader.Find fallback, exactly as DecalShader is.
        /// </summary>
        public static Shader MultiShader;

        /// <summary>
        /// The texture a two-layer effect lays over its base, and how it joins - 0 multiply,
        /// 1 over by its own alpha, 2 over by the inverse of it. Null when there is no second
        /// layer, which is the ordinary single-texture case.
        ///
        /// The effect name is a chain read left to right: eut2.dif.spec.mult.dif.spec.iamod.dif.spec
        /// is a base layer, then a layer multiplied on, then one blended by inverse alpha. The
        /// slots in the .mat are named for the same words - texture_base, texture_mult,
        /// texture_iamod - so the name and the file agree and neither has to be guessed at.
        ///
        /// Only the first overlay is taken. Two overlays would need a third uv set and a third
        /// sampler for a much smaller difference than the first one makes.
        /// </summary>
        public static string SecondLayer(EtsMaterialDef def, out float mode)
        {
            mode = 0f;

            if (def.Textures.TryGetValue("texture_mult", out string mult) && !string.IsNullOrEmpty(mult))
            {
                return mult;
            }

            // Prefab ground. "eut2.dif.spec.weight.weight.dif.spec.weight" is two textures, texture_base
            // and texture_over, blended by the vertex colour's alpha - the DomDepo yard is three such
            // pieces, 141 x 116 m of grass_city1 over soil_gravel and asphalt_parklot_base over its
            // var_1 and var_3, with 102 of 139 vertices at a partial alpha. Read from the files, not
            // guessed: the .mat names both slots and the pmg carries the alpha. The tg0/tg1 variants
            // generate their uvs in the game but the exported pieces still carry two uv sets.
            // Alpha 1 = texture_over, per SCS's own Blender-tools shader for the effect (mode 4:
            // the same mix as the terrain pairs, plus a depth bias, being co-planar with other slabs).
            if (def.EffectHas("weight.weight") && def.Textures.TryGetValue("texture_over", out string weighted) && !string.IsNullOrEmpty(weighted))
            {
                mode = 4f;
                return weighted;
            }

            // Grass-topped rocks: eut2.dif.weight.dif (/automat/a0/a06fe141466613ee.mat,
            // rock_grey_big under grass_ger_main, 3.3 % of the model area round the start). The
            // game's attribute pass (/effect/eut2/_shd/8b545251dedfcc425edeaa1dd79672a1.glsl.fso)
            // is mix(base(uv0), over(uv1), vertex alpha) - the same mix as mode 4. It was falling
            // through every test below ("weight", no "weight.weight") and drawn as bare rock.
            if (def.EffectHas("dif.weight.dif") && def.Textures.TryGetValue("texture_over", out string grassOver) && !string.IsNullOrEmpty(grassOver))
            {
                mode = 4f;
                return grassOver;
            }

            // The amod chain does not call its slot "texture_amod" - it calls it "texture_over".
            // Measured near Frankfurt: all 90 eut2.dif.spec.amod.* materials use that name, and
            // nothing else does except the weight (terrain blending, which is driven by vertex
            // weights, not by a second uv) and baked (pre-lit) effects, which are left alone.
            // "iamod" contains "amod", so it has to be asked about first.
            if (def.Textures.TryGetValue("texture_over", out string over) && !string.IsNullOrEmpty(over) &&
                !def.EffectHas("weight") && !def.EffectHas("baked"))
            {
                if (def.EffectHas("iamod"))
                {
                    mode = 2f;
                    return over;
                }

                if (def.EffectHas("amod"))
                {
                    mode = 1f;
                    return over;
                }
            }

            if (def.Textures.TryGetValue("texture_iamod", out string iamod) && !string.IsNullOrEmpty(iamod))
            {
                mode = 2f;
                return iamod;
            }

            // Ambient occlusion. eut2.dif.spec.oclu.* (trailers' scs_box/ao, the AI cars' ao_color, the
            // plastics, the marble blocks - about a hundred materials) multiply base colour, specular and
            // reflection by texture_oclu.x through the SECOND uv set (/effect/eut2/_shd fwdopaque of
            // eut2.dif.spec.oclu.add.env.tsnmap: base at TexCoord4.xy, oclu at TexCoord4.zw). The oclu map
            // is BC4, which samples as (r, 0, 0, 1), so the join is mode 6 - multiply by red only - and only
            // when the two-layer shader has it. The tsnmap variants stay on Lit: the two-layer shader has
            // no normal map, and on those interiors the normal map is the larger part of the look.
            if (_multiHasMaskModes == 1 && !def.EffectHas("tsnmap") &&
                def.Textures.TryGetValue("texture_oclu", out string oclu) && !string.IsNullOrEmpty(oclu))
            {
                mode = 6f;
                return oclu;
            }

            return null;
        }

        private static Shader FindMulti()
        {
            if (MultiShader == null)
            {
                MultiShader = Shader.Find("SomniumEuroTruck/Multi");
            }

            return MultiShader;
        }

        public EtsMaterialCache(EtsFileSystem files, EtsTextureCache textures)
        {
            _files = files;
            _textures = textures;
        }

        /// <summary>
        /// The Lit shader, found by name. A world script cannot reference a shader asset, but the
        /// pipeline's own Lit shader is in every URP build, Somnium's included, because the
        /// client's own content uses it.
        /// </summary>
        private Shader FindShader()
        {
            if (_shader != null)
            {
                return _shader;
            }

            // URP shaders only: the built-in "Standard" has only legacy passes, which a release URP
            // client draws as nothing at all (URP RenderingUtils: legacy passes are drawn only in the
            // editor or a development build).
            foreach (string name in new[] { "Universal Render Pipeline/Lit", "Universal Render Pipeline/Simple Lit", "Universal Render Pipeline/Unlit" })
            {
                _shader = Shader.Find(name);

                if (_shader != null)
                {
                    break;
                }
            }

            return _shader;
        }

        private readonly Dictionary<string, EtsMaterialDef> _definitions = new Dictionary<string, EtsMaterialDef>(StringComparer.Ordinal);

        /// <summary>
        /// The parsed material file, without building a Unity material. The model builder asks
        /// this which effect a piece uses, because the effect decides which uv channel the base
        /// texture is read through. Null when the file is missing.
        /// </summary>
        public EtsMaterialDef GetDefinition(string matPath)
        {
            if (string.IsNullOrEmpty(matPath))
            {
                return null;
            }

            string key = "/" + EtsHashFs.Normalize(matPath);

            if (_definitions.TryGetValue(key, out EtsMaterialDef cached))
            {
                return cached;
            }

            string text = _files.ReadText(key);
            EtsMaterialDef def = text == null ? null : EtsMaterialDef.Parse(text, key);
            _definitions[key] = def;
            return def;
        }

        /// <summary>
        /// Which uv channel an effect reads its base texture through. Every effect seen so far
        /// reads set 0.
        ///
        /// This was measured properly rather than guessed, because an earlier guess put the
        /// painted bodywork on set 2 and smeared the whole cab. The test is area: a unique
        /// unwrap lays each triangle down once, so the area its triangles cover in uv space
        /// comes out near 1, while a tiling set covers the square over and over. Summed across
        /// the DAF's 58 square metres of painted panels, set 0 measures 1.08, set 1 0.38 and
        /// set 2 0.73 - and, decisively, every painted piece's set 2 runs from u 0.01 to about
        /// u 0.9 and stops at v 0.59, all of them on top of one another. That is the layout of
        /// the paint JOB mask, the thing a livery is painted into, not an unwrap; sampling the
        /// bodywork's ambient-occlusion atlas through it gave every panel the same wrong strip.
        /// Set 0 gives each piece its own rectangle of the atlas, which is what an unwrap is.
        ///
        /// The 63 square metres of tsnmapuv plastic measure 3665 on set 0 and 0.92 on set 1:
        /// there the small tiling texture is on set 0 and the unique detail map on set 1.
        /// </summary>
        public static int BaseUvChannel(EtsMaterialDef def, int channelsAvailable)
        {
            return 0;
        }

        /// <summary>
        /// The material for a path. Null means "do not draw this" - the game's shadow-only and
        /// invisible effects - which callers must honour by skipping the piece.
        /// </summary>
        public Material Load(string matPath)
        {
            DrainGloss();

            if (string.IsNullOrEmpty(matPath))
            {
                return Fallback();
            }

            string key = "/" + EtsHashFs.Normalize(matPath);
            string path = key;

            // Painted bodywork is cached per path AND per paint colour: the colour is baked in when
            // the material is made (Build: tint *= PaintColor), and the traffic builds its cars'
            // paint materials with PaintColor set to white (EtsTrafficModels) while the truck's
            // are made with the truck's paint. Keyed by path alone, whichever came first would be
            // handed to the other - a white truck, or cars in the truck's colour.
            if (IsPainted(path))
            {
                key = path + "|paint " + PaintKey(PaintColor);
            }

            if (_byPath.TryGetValue(key, out Material cached))
            {
                return cached;
            }

            Material material;

            try
            {
                material = Build(path);
            }
            catch (Exception e)
            {
                Report(path, e.Message);
                material = Fallback();
            }

            _byPath[key] = material;
            return material;
        }

        /// <summary>Whether a material's effect is the game's truckpaint (the colour comes from PaintColor).</summary>
        private bool IsPainted(string normalisedPath)
        {
            EtsMaterialDef def = GetDefinition(normalisedPath);
            return def != null && def.EffectHas("truckpaint");
        }

        private static string PaintKey(Color c)
        {
            return BitConverter.SingleToInt32Bits(c.r).ToString("x8") + BitConverter.SingleToInt32Bits(c.g).ToString("x8") +
                   BitConverter.SingleToInt32Bits(c.b).ToString("x8") + BitConverter.SingleToInt32Bits(c.a).ToString("x8");
        }

        private Material Build(string path)
        {
            string text = _files.ReadText(path);

            if (text == null)
            {
                Report(path, "material file not found");
                return Fallback();
            }

            EtsMaterialDef def = EtsMaterialDef.Parse(text, path);
            string effect = def.Effect ?? string.Empty;

            // Things the game does not draw as colour: shadow casters, and the "none" effect
            // that marks collision or helper geometry. Skipping them is the correct render.
            // "mlaaweight" is a post-process helper: the DAF's interior lays one over the instrument
            // cluster (piece 3, an empty material with no texture at all) to weight the game's
            // anti-aliasing there. Built as a surface it was a bare white plate over the gauges.
            // Night-only surfaces: eut2.lightmap.night is the pool a street lamp throws on the
            // ground and its walls (decal_light_01, 664 pieces within 1.2 km of the bay), and the
            // building.*.night effects are lit windows. The game adds them after dark; this world
            // has no night yet, and drawn opaque by day they were dark slabs in front of the yards.
            // "fakeshadow" is the blob of contact shadow the game multiplies under a wheel or a
            // prop: /vehicle/truck/upgrade/wheel/tire/front_1.pmd carries one as its third
            // material with no texture of any kind, so drawn as an ordinary surface it came out
            // as the plain white the fallback colour gives - the white on the tyres.
            if (effect.Length == 0 || def.EffectHas("shadowonly") || effect == "eut2.none" || effect.StartsWith("eut2.none", StringComparison.Ordinal) ||
                def.EffectHas("shadowmap") || def.EffectHas("mlaaweight") || def.EffectHas(".night") ||
                def.EffectHas("fakeshadow"))
            {
                return null;
            }

            // A baked contact shadow, not a surface. /effect/eut2/shadowmap/eut2.decalshadow.rfx is
            // declared as effect "eut2.shadowmap.decal.over"; its decal pass
            // (/effect/eut2/_shd/b2c598ad305175a5b644c50191e24351.glsl.fso) writes BLACK with
            // alpha = texture_base.r, blended src_alpha / one_minus_src_alpha with no depth write
            // (/effect/deferred/rs/decal_over.rs). Its texture is R8 (heavy_factory/ground.tobj,
            // DXGI 61). Built as an ordinary opaque Lit surface, the heavy-factory yard beside the
            // start was a flat grey picture of its own shadow map lying in the same plane as the
            // rough asphalt it should darken (client census: "on top in 1998 cells", "co-planar
            // x1998" with /automat/44/44e4fc45f7254580.mat) - wrong colour AND z-fighting.
            if (def.EffectHas("decalshadow"))
            {
                return BuildDecalShadow(def, path);
            }

            // Trees, bushes and grass cards, when the scene provides the leaves shader.
            if (EtsModelBuilder.IsLeaves(def) && FindLeaves() != null)
            {
                return BuildLeaves(def, path);
            }

            Shader shader = FindShader();

            // Ground decals get the world's own shader when the scene provides it.
            if (DecalShader != null && def.EffectHas("decal.over"))
            {
                shader = DecalShader;
            }

            // Two-layer surfaces - the game's mult/amod/iamod chains, and the building and window
            // effects - get the world's two-layer shader, but only when there really is a second
            // texture to lay on. "mult" is the layer that carries a building's weathering and
            // window bands; "detail" is what the building effect calls the same thing.
            // (Asked here, on the main thread, so SecondLayer knows whether modes 5 and 6 exist.)
            MultiHasMaskModes();
            string secondLayer = SecondLayer(def, out float layerMode);
            bool twoLayer = secondLayer != null && !def.EffectHas("decal.over");

            if (twoLayer)
            {
                Shader multi = FindMulti();

                if (multi != null)
                {
                    shader = multi;
                }
                else
                {
                    twoLayer = false;
                }
            }

            if (shader == null)
            {
                return null;
            }

            var material = new Material(shader) { name = path };
            material.enableInstancing = true;

            Texture2D baseMap = def.BaseTexture != null ? _textures.Load(def.BaseTexture, false) : null;

            if (baseMap != null)
            {
                material.SetTexture(BaseMap, baseMap);
                if (material.HasProperty(MainTex)) material.SetTexture(MainTex, baseMap);
            }

            if (twoLayer && material.HasProperty(MultMap))
            {
                Texture2D over = _textures.Load(secondLayer, false);

                if (over != null)
                {
                    material.SetTexture(MultMap, over);
                    material.SetFloat(LayerMode, layerMode);
                }

                // amod as the game draws it (/effect/eut2/_shd fwdopaque of
                // eut2.dif.spec.amod.dif.spec.tsnmapuv, the commonest effect in the game - 9,897
                // materials, 13,000 with its variants): texture_over and texture_mask share the second
                // uv set; f = vertex alpha * 2 - 1 scaled by amod_decal_blending_factors (.y above
                // 0.5, .x below); strength = |f| * mask.r; the layer is base * over where f > 0 and over
                // itself where f < 0; mix(base, layer, strength). Blending by the over's own alpha (mode
                // 1) used its SPECULAR channel - softdecal_metal_05_difspec's alpha averages 15/255 -
                // so the weathering the mask paints on (softdecal_metal_02a_opa, BC4) all but vanished.
                // The vertex alpha is spread, not constant (harness ProgramTexVcol over 2,500 models: 26 %
                // of amod vertices at 112-143, i.e. no decal, 46 % at 144-239, 14 % at 240+), so this
                // needs the shader's mode 5; without it mode 1 stays.
                if (over != null && layerMode > 0.5f && layerMode < 1.5f && def.EffectHas("amod") && !def.EffectHas("iamod") &&
                    material.HasProperty(AmodMask) &&
                    def.Textures.TryGetValue("texture_mask", out string amodMaskPath) && !string.IsNullOrEmpty(amodMaskPath))
                {
                    Texture2D amodMask = _textures.Load(amodMaskPath, false);

                    if (amodMask != null)
                    {
                        float[] factors = def.GetFloats("amod_decal_blending_factors");
                        material.SetTexture(AmodMask, amodMask);
                        material.SetVector(AmodFactors, new Vector4(factors.Length > 0 ? factors[0] : 1f, factors.Length > 1 ? factors[1] : 1f, 0f, 0f));
                        material.SetFloat(LayerMode, 5f);
                    }
                }

                // The weight.weight ground lies in the same plane as a prefab's plain slabs (the
                // tram crossing's ger_01 surface is drawn twice, once each way, at y = 0, with
                // different vertices, so their depths differ by rounding and flicker). Drawn a step
                // later AND pulled a depth unit toward the camera, it wins every pixel every frame.
                if (layerMode > 3.5f && def.EffectHas("weight.weight"))
                {
                    material.renderQueue = (int)RenderQueue.Geometry + 1;
                    if (material.HasProperty(DepthBias)) material.SetFloat(DepthBias, -1f);
                }
            }

            Color tint = Color.white;
            float[] diffuse = def.GetFloats("diffuse");

            // The game multiplies the (sRGB-decoded) texture by "diffuse" in LINEAR space
            // (/effect/eut2/_shd/30a5626a4b2701c1ec1f7fcb719381da.glsl.fso:
            // tex.rgb * material0_data[0].xyz * vcol * 2). Unity linearises a material colour set
            // through SetColor, so the file's number goes in gamma-encoded to come out as itself:
            // 0.93 was arriving as 0.85.
            if (diffuse.Length >= 3)
            {
                tint = new Color(diffuse[0], diffuse[1], diffuse[2], 1f).gamma;
                tint.a = 1f;
            }

            // Painted bodywork. The texture is a mask over which the game lays the paint job's
            // colour, so multiplying by a colour here is the nearest honest equivalent.
            if (def.EffectHas("truckpaint"))
            {
                tint *= PaintColor;
                tint.a = 1f;
            }

            // A normal map is only right when it shares the base texture's uv set, because Unity's
            // Lit samples both through the first. Only tsnmapuv puts its normal map somewhere else
            // - on set 1, the unique unwrap, while its small colour texture tiles over set 0 - so
            // only that one has to go without. Painted bodywork keeps its map: tsnmap16 has no uv
            // of its own and rides on set 0 with the paint, now that the paint is read from set 0.
            bool normalMapUsable = !def.EffectHas("tsnmapuv");

            if (def.NormalTexture != null && normalMapUsable && material.HasProperty(BumpMap))
            {
                Texture2D normal = _textures.Load(def.NormalTexture, true);

                if (normal != null)
                {
                    material.SetTexture(BumpMap, normal);
                    material.EnableKeyword("_NORMALMAP");
                }
            }

            // eut2.window is NOT glass. Its passes are the opaque ones every wall uses
            // (/effect/eut2/window/eut2.window.day.rfx: attrib_render + forward_none, no blend, back faces
            // culled), and its fragment program writes texture_base - /material/special/window_interior_01,
            // a picture of a lit room - as plain albedo, with the room nudged by the view angle in the
            // vertex program and the building reflection added on top. Taken for glass it was drawn at the
            // default 0.35 opacity times the picture's own alpha (72 % of it 0.2-0.4, which the game never
            // reads), about 10 % in all, so every window was a hole into the hollow shell and out the other
            // side: 166 window pieces (12,476 m2) round Dover, 37 (3,844 m2) round Cambridge and 7 at the
            // Szczecin start - logistic_08_fr, admin_15_de, admin_logistic_small_02_ibe, warehouse_port_1_de,
            // the garage_sc headquarters garage (harness ProgramBld1).
            // Only eut2.glass (forward_mult + forward_add) is see-through in the game.
            bool window = def.EffectHas("eut2.window");
            bool glass = def.EffectHas("glass");
            bool overlay = !glass && def.EffectHas("decal.over");
            bool cutout = !glass && !overlay && (def.EffectHas(".a.") || effect.EndsWith(".a", StringComparison.Ordinal) ||
                                     def.EffectHas(".a_") || def.EffectHas("alpha") || def.EffectHas("leaves") ||
                                     def.EffectHas("decal") || def.EffectHas("fence") || def.EffectHas("grass") ||
                                     def.EffectHas("flare"));

            if (window)
            {
                CountWindow(path);
            }

            if (material.HasProperty(Smoothness))
            {
                // The game's specular is MASKED by the colour texture's alpha: eut2.dif.spec's
                // attribute pass (/effect/eut2/_shd/30a5626a4b2701c1ec1f7fcb719381da.glsl.fso)
                // writes specular = saturate(base.a * s + b) * specular colour. Read as one
                // number per material (the mean of the alpha plane - no new shader variant needed),
                // asphalt_tiles_eu_01 has alpha 0.000 over the whole texture: NO highlight in the
                // game, smoothness 0.58 here (census "SHINY smooth 0.58", 1799 cells round the
                // start). Only where alpha is not already spoken for: not cut-outs, not glass or
                // decals, not the env effects whose alpha is the reflection mask (handled below).
                bool alphaIsSpecular = !glass && !overlay && !cutout && def.EffectHas("spec") &&
                                       !def.EffectHas(".env") && !def.EffectHas("nofresnel") && !def.EffectHas("lamp") &&
                                       !def.EffectHas("truckpaint") && !def.EffectHas("weight");
                float specularMask = alphaIsSpecular ? _textures.MeanAlpha(def.BaseTexture) : 1f;

                SurfaceOf(def, out float metallic, out float smoothness, specularMask);
                material.SetFloat(Smoothness, smoothness);

                if (material.HasProperty(Metallic))
                {
                    material.SetFloat(Metallic, metallic);
                }

                // The game's specular is a Blinn-Phong highlight of the sun and nothing else: only
                // the effects that carry an environment term (add.env, glass, truckpaint, lamp,
                // mirror, chrome) reflect their surroundings. URP's Lit reflects the sky off any
                // smooth surface, so a yard piece with "shininess 65, specular 1, reflection 0"
                // (smoothness 0.58 here) became a pale blue sheet at a grazing angle - the sky in
                // it - where the game shows dry asphalt. Environment reflection is switched off on
                // every material without such a term; the highlight keeps its smoothness.
                // TRUCK PAINT IS NOT ONE OF THEM. It carries an environment term, but the game's is
                // not a reflection of the world: its texture_reflection is
                // /material/environment/vehicle_reflection.tobj, which is six references to the
                // same two flat 256x256 images - a sky-over-ground gradient of mean luminance 0.312
                // and a flat 0.158 grey - scaled by env_factor 0.073 and by the base texture's
                // alpha. Read out of the game's own compiled shader for eut2.truckpaint
                // (/effect/eut2/_shd/1eb0f1531e93a55e9f4d969d9fdf2338.glsl.fso) the whole term tops
                // out near 0.019 of white at a grazing angle: a sheen. URP's Lit instead returns
                // 0.043 of a realtime probe of the actual sky at 40 degrees, 0.218 at 72 and 0.428
                // at 81 - which is the environment smeared across the panels. The highlight and the
                // smoothness stay; only the world reflection goes.
                // Water carries an environment term too: water_river.mat states env_factor 0.35,
                // fresnel {0.2, 0.9}, water_mirror 1 and a texture_reflection cube. With the
                // reflection switched off and water_near_color {0,0,0} as its colour, every river
                // and dock round the start was drawn BLACK (census: "colour 0.00/0.00/0.00").
                bool environment = glass || window || def.EffectHas(".env") || def.EffectHas("lamp") ||
                                   def.EffectHas("mirror") || def.EffectHas("chrome") || def.EffectHas("water");

                if (!environment && material.HasProperty(EnvironmentReflections))
                {
                    material.SetFloat(EnvironmentReflections, 0f);
                    material.EnableKeyword("_ENVIRONMENTREFLECTIONS_OFF");
                }

                // Where the surface reflects its surroundings, the game masks that reflection with
                // the colour texture's own alpha channel, so one atlas can carry dull plastic and
                // bright chrome side by side. Read as one number per material, the DAF's mud flaps
                // came out as mirror or its grilles as paint; read as the mask it is, each texel
                // gets what the file says. Only for opaque surfaces, where alpha is not being
                // used for see-through, and not for lamps, whose alpha is the lit part of a lens.
                bool masked = !glass && !overlay && !cutout && !def.EffectHas("lamp") &&
                              (def.EffectHas(".env") || def.EffectHas("nofresnel")) &&
                              material.HasProperty(MetallicGlossMap);

                if (masked)
                {
                    Texture2D mask = _textures.LoadReflectionMask(def.BaseTexture, metallic);

                    if (mask != null)
                    {
                        material.SetTexture(MetallicGlossMap, mask);
                        material.EnableKeyword("_METALLICSPECGLOSSMAP");
                    }
                }

                // The baked family (eut2.baked.*: 9,700 materials - the model2 buildings, bus stops,
                // hydrants, fences, rocks, and the AI traffic's box-baked cars) keeps its gloss in
                // textures, not in the .mat: texture_over is "_bspec" (power x^2 * 1024 and strength y
                // per texel) and on the .env variants texture_mask ("_env") is the per-texel reflection
                // (eut2.baked.spec.rfx / .add.env.rfx fwdopaque, 2026-09-23). These .mat files state no
                // shininess and no specular, so every baked surface was one default gloss - a whole
                // car, tyres and glass alike, or a whole block of flats, windows and render alike.
                // Now a small per-texel map (EtsTextureCache.LoadBakedGloss, 128 px at most): matte
                // walls, glossy glass and paint where the files say so. The reference is capped a
                // little above the old scalar so a device without the map variant draws near the old look.
                if (def.EffectHas("baked") && !glass && !overlay && material.shader == _shader && material.HasProperty(MetallicGlossMap))
                {
                    def.Textures.TryGetValue("texture_over", out string bakedSpec);
                    string bakedEnv = def.EffectHas(".env") && def.Textures.TryGetValue("texture_mask", out string envMask) ? envMask : null;

                    if (!string.IsNullOrEmpty(bakedSpec) || !string.IsNullOrEmpty(bakedEnv))
                    {
                        // Read on a worker (4 ms a material on average, 101 ms at worst in harness
                        // ProgramTexGloss - all of it archive read and GDeflate), laid on when it lands.
                        AskBakedGloss(material, bakedSpec, bakedEnv, Mathf.Min(0.9f, smoothness + 0.25f));
                    }
                }
            }

            if (glass && material.HasProperty(Surface))
            {
                material.SetFloat(Surface, 1f);
                material.SetFloat(Blend, 0f);
                material.SetOverrideTag("RenderType", "Transparent");
                material.SetInt(SrcBlend, (int)BlendMode.SrcAlpha);
                material.SetInt(DstBlend, (int)BlendMode.OneMinusSrcAlpha);
                material.SetInt(ZWrite, 0);
                material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                material.renderQueue = (int)RenderQueue.Transparent;
                material.SetShaderPassEnabled("ShadowCaster", false);

                // The game's glass materials say how dark and how coloured they are. Read them
                // rather than assume one glass for windscreens, sunroofs and lamp covers alike.
                float[] glassTint = def.GetFloats("glass_tint_color");
                float opacity = def.GetFloat("glass_tint_opacity", 0.35f);

                if (glassTint.Length >= 3)
                {
                    tint = new Color(glassTint[0], glassTint[1], glassTint[2], 1f).gamma;
                }

                tint.a = Mathf.Clamp(opacity, 0.15f, 0.9f);
            }
            else if (overlay && material.HasProperty(Surface))
            {
                // Ground decals (lane lines, dirt, tyre marks) lie a centimetre or two above the
                // surface and blend by alpha. Drawn as cutout they turn into hard beige streaks.
                material.SetFloat(Surface, 1f);
                material.SetFloat(Blend, 0f);
                material.SetOverrideTag("RenderType", "Transparent");
                material.SetInt(SrcBlend, (int)BlendMode.SrcAlpha);
                material.SetInt(DstBlend, (int)BlendMode.OneMinusSrcAlpha);
                material.SetInt(ZWrite, 0);
                material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                material.renderQueue = (int)RenderQueue.Transparent - 100;
                material.SetShaderPassEnabled("ShadowCaster", false);
            }
            else if (cutout && material.HasProperty(AlphaClip))
            {
                material.SetFloat(AlphaClip, 1f);
                material.SetFloat(Cutoff, CountCutout(CutoutThreshold(def)));
                material.EnableKeyword("_ALPHATEST_ON");
                material.renderQueue = (int)RenderQueue.AlphaTest;
            }
            else if (cutout && material.HasProperty(AlphaClipOn))
            {
                // The two-layer shader spells its cutout switch differently, having no need of
                // URP Lit's surface-type machinery.
                material.SetFloat(AlphaClipOn, 1f);
                material.SetFloat(Cutoff, CountCutout(CutoutThreshold(def)));
                material.renderQueue = (int)RenderQueue.AlphaTest;
            }

            if (def.EffectHas("lum") && material.HasProperty(EmissionMap) && baseMap != null)
            {
                material.EnableKeyword("_EMISSION");
                material.SetTexture(EmissionMap, baseMap);
                material.SetColor(EmissionColor, Color.white);
                material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
            }

            // Decals and vegetation are authored single-sided in the game but read from both
            // sides in a world you can walk around, so leave them two-sided. (Not the two-layer shader's own cut-outs: it
            // gained a _Cull property on 2026-09-25 for the per-vertex colour twins, WithVertexAo, which copy Lit's; its
            // own materials keep the single-sided drawing they had.)
            if ((cutout || overlay) && material.HasProperty(Cull) && material.shader == _shader)
            {
                material.SetFloat(Cull, (float)CullMode.Off);
            }

            // Water. The effect keeps its two wave textures in texture_layer0 and texture_layer1
            // rather than texture_base, so the ordinary path found no texture at all and fell back
            // to the material's "diffuse" - which is {1,1,1}. Every lake, river and flooded pit in
            // the world was therefore drawn as a flat WHITE sheet, which under a bright sky is the
            // pale blue-grey patches scattered over the fields. The colour water is meant to be is
            // written down beside it: water_near_color and water_horizon_color, both
            // {0.030, 0.074, 0.133} on the lakes here - a deep blue - and the waves tile over it.
            // Game 1.61 (/umatlib/legacy/terrain/water/water_river.mat, the 1486 census cells of
            // river beside the start): water_near_color {0,0,0}, water_horizon_color
            // {0.10, 0.19, 0.42}, water_distances {700, 1650, 10}, env_factor 0.35, fresnel
            // {0.2, 0.9}, water_mirror 1. So the body is black near and deep blue far, and what the
            // eye sees is the REFLECTION. texture_layer0/1 (waves_lake.tobj) are BC5 = NORMAL maps
            // (DXGI 83), not colour: used as the base map they were red/green noise times black.
            if (def.EffectHas("water"))
            {
                material.SetTexture(BaseMap, null);
                if (material.HasProperty(MainTex)) material.SetTexture(MainTex, null);

                float[] near = def.GetFloats("water_near_color");
                float[] far = def.GetFloats("water_horizon_color");
                Color nearColour = near.Length >= 3 ? new Color(near[0], near[1], near[2], 1f) : Color.black;
                Color farColour = far.Length >= 3 ? new Color(far[0], far[1], far[2], 1f) : new Color(0.10f, 0.19f, 0.42f, 1f);

                // Linear values from the file; half way between the near and horizon colours so the
                // water is never black even where the reflection is weak (steep views, or a
                // reflection probe that is not there).
                tint = Color.Lerp(nearColour, farColour, 0.5f).gamma;
                tint.a = 1f;

                if (def.Textures.TryGetValue("texture_layer0", out string waves) && material.HasProperty(BumpMap))
                {
                    Texture2D waveNormals = _textures.Load(waves, true);

                    if (waveNormals != null)
                    {
                        material.SetTexture(BumpMap, waveNormals);
                        material.SetFloat(BumpScale, 0.35f);
                        material.EnableKeyword("_NORMALMAP");
                    }
                }

                // URP caps the grazing reflectance at smoothness + 0.04 (EnvironmentBRDF's
                // grazingTerm), so smoothness here IS the reflection strength at a low angle. The
                // game's fresnel {0.2, 0.9} x env_factor 0.35 plus its planar mirror lands between
                // a third and two thirds; 0.7 keeps the sky readable in the water without turning
                // the river into a white sheet from the cab.
                if (material.HasProperty(Smoothness))
                {
                    material.SetFloat(Smoothness, 0.7f);

                    if (material.HasProperty(Metallic))
                    {
                        material.SetFloat(Metallic, 0f);
                    }
                }
            }

            material.SetColor(BaseColor, tint);
            return material;
        }

        /// <summary>
        /// Metallic and smoothness read off the game's material the way its own shaders use it.
        /// "shininess" is a Blinn-Phong exponent, turned into a roughness and damped by how strong
        /// the "specular" colour is - cloth, carpet and decals have none, so they stay matte, while
        /// paint at 250 is polished. Environment reflection is what makes chrome chrome: the
        /// add.env, truckpaint, lamp and glass effects add the surroundings scaled by env_factor and
        /// a fresnel term whose bias is the reflectivity facing the viewer. Chrome and steel say
        /// "nofresnel" and are true metals, env_factor being their metallic value; the rest are
        /// dielectrics whose extra facing reflectivity becomes a little metallic, and anything
        /// reflective is smoothed toward a mirror. Glass is always smooth and never metallic.
        /// </summary>
        /// <summary>
        /// Ground, by the game's own classification. Every material may name the stuff it is made
        /// of in "substance", which is what the game uses for tyre grip and sound; these are the
        /// ones a truck drives on or past, and none of them shine when dry.
        /// </summary>
        private static bool IsGround(EtsMaterialDef def)
        {
            if (!def.Attributes.TryGetValue("substance", out string raw) || string.IsNullOrEmpty(raw))
            {
                return false;
            }

            string substance = raw.Trim().Trim('"').ToLowerInvariant();

            return substance.Contains("road") || substance.Contains("dirt") || substance.Contains("grass") ||
                   substance.Contains("gravel") || substance.Contains("sand") || substance.Contains("soil") ||
                   substance.Contains("stone") || substance.Contains("concrete") || substance.Contains("asphalt") ||
                   substance.Contains("terrain") || substance.Contains("field") || substance.Contains("snow");
        }

        public static void SurfaceOf(EtsMaterialDef def, out float metallic, out float smoothness, float specularMask = 1f)
        {
            float shininess = def.GetFloat("shininess", 10f);
            float roughness = Mathf.Pow(2f / (Mathf.Max(1f, shininess) + 2f), 0.25f);
            float[] specular = def.GetFloats("specular");
            float spec = specular.Length >= 3 ? (specular[0] + specular[1] + specular[2]) / 3f : (def.EffectHas("spec") ? 0.3f : 0f);

            // The texture's alpha scales the specular colour in the game (see Build).
            spec *= Mathf.Clamp01(specularMask);
            smoothness = (1f - roughness) * (0.25f + 0.75f * Mathf.Sqrt(Mathf.Clamp01(spec)));
            metallic = 0f;

            bool glass = def.EffectHas("glass") || def.EffectHas("window");
            bool reflective = glass || def.EffectHas(".env") || def.EffectHas("truckpaint") || def.EffectHas("lamp");

            if (reflective)
            {
                // env_factor scales the environment term, and reading a MISSING one as 1.0 gave
                // truck paint the reflectivity of a mirror. Measured over every material reachable
                // from the demo's 6,678 vehicle models: of the 306 truckpaint materials, 249 state
                // no env_factor at all - the DAF's own cab paint, /automat/ed/ed82dfb511c1299a.mat,
                // among them - and of the 57 that do state one, 50 state 0.073239. That number is
                // exactly ((0.3 + 0.055) / 1.055) ^ 2.4, the linearisation of an authored 0.3, and
                // the same linearisation explains every other scalar in a .mat, so it is the
                // engine's own value and not a guess. Full strength was 13.7 times too much.
                float[] envFactor = def.GetFloats("env_factor");
                float stated = envFactor.Length >= 3 ? (envFactor[0] + envFactor[1] + envFactor[2]) / 3f : -1f;
                float strength = stated >= 0f ? stated : (def.EffectHas("truckpaint") ? 0.073239f : 1f);
                float[] fresnel = def.GetFloats("fresnel");
                float bias = fresnel.Length >= 2 ? fresnel[0] : 0.2f;
                float facing;

                // "add.env" ADDS a reflection of the surroundings to an ordinary painted surface.
                // It does not mean the surface is metal, and treating it as metal is destructive:
                // metallic takes the albedo away and puts a reflection in its place, so a white
                // car came out with dark mirror-like roof, bonnet and doors while its plastic trim
                // stayed white. Only the materials that say "nofresnel" - chrome and bare steel,
                // which reflect the same from every angle - are true metals.
                if (def.EffectHas("nofresnel"))
                {
                    facing = Mathf.Clamp01(strength);
                    metallic = facing;
                }
                else
                {
                    facing = Mathf.Clamp01(bias * strength);

                    // indenv masks the reflection by the texture's alpha, which is not read here.
                    if (def.EffectHas("indenv"))
                    {
                        facing *= 0.5f;
                    }

                    metallic = 0f;
                }

                // The added reflection reads as gloss, not as a mirror.
                smoothness += (0.85f - smoothness) * Mathf.Clamp01(facing * 1.5f);
            }

            if (glass)
            {
                metallic = 0f;
                smoothness = 0.9f;
            }

            if (def.EffectHas("mirror") || def.EffectHas("chrome"))
            {
                smoothness = Mathf.Max(smoothness, 0.9f);
            }

            // Ground is matte. Terrain and road surfaces use the blended "weight" effects, and they
            // carry a "reflection" of 1.0 and a specular of up to 1.0 - but in the game that is the
            // WET-weather reflectivity, spent only when it rains. Read dry, as here, a smoothness
            // built from those numbers turns every field and yard into a sheet mirroring the sky,
            // which is what made the ground read as pale blue-white next to correctly dark asphalt.
            if (def.EffectHas("weight") || def.EffectHas("decal.over") || IsGround(def))
            {
                metallic = 0f;
                smoothness = Mathf.Min(smoothness, 0.12f);
            }

            smoothness = Mathf.Clamp01(smoothness);
        }

        // ------------------------------------------------------------- baked gloss, off the frame

        private sealed class GlossWait
        {
            public string Spec, Env;
            public readonly List<KeyValuePair<Material, float>> Materials = new List<KeyValuePair<Material, float>>();
        }

        private EtsWorker _glossWorker;
        private readonly Dictionary<string, GlossWait> _glossWaits = new Dictionary<string, GlossWait>(StringComparer.Ordinal);
        private readonly Dictionary<Material, List<Material>> _clonesOf = new Dictionary<Material, List<Material>>();
        private int _glossFrame = -1;
        private long _glossTicks;
        private bool _glossFailureLogged;
        private int _glossApplied;

        /// <summary>Main-thread time a frame may spend laying finished gloss maps on (about 1 ms).</summary>
        private static readonly long GlossBudgetTicks = System.Diagnostics.Stopwatch.Frequency / 1000;

        /// <summary>
        /// A baked material's gloss map (EtsTextureCache.PrepareBakedGloss), read on this cache's own worker
        /// thread and laid on the material - and on every vertex-colour copy of it - when it lands. Until
        /// then the material draws with its scalar smoothness, as it always did. Main thread.
        /// </summary>
        private void AskBakedGloss(Material material, string spec, string env, float cap)
        {
            if (_textures.HasGlossPlane(spec, env))
            {
                SetBakedGloss(material, spec, env, cap);
                return;
            }

            string key = EtsTextureCache.GlossKey(spec, env);

            if (_glossWaits.TryGetValue(key, out GlossWait wait))
            {
                wait.Materials.Add(new KeyValuePair<Material, float>(material, cap));
                return;
            }

            wait = new GlossWait { Spec = spec, Env = env };
            wait.Materials.Add(new KeyValuePair<Material, float>(material, cap));
            _glossWaits[key] = wait;

            if (_glossWorker == null)
            {
                _glossWorker = new EtsWorker("Ets gloss reader");
            }

            EtsFileSystem files = _files;
            EtsTextureCache textures = _textures;
            EtsWorker worker = _glossWorker;

            worker.Post(() => EtsTextureCache.PrepareBakedGloss(files, spec, env), (result, error) =>
            {
                if (!ReferenceEquals(worker, _glossWorker))
                {
                    return; // Cleared since: the materials it was for are gone.
                }

                _glossWaits.Remove(key);

                if (error != null && !_glossFailureLogged)
                {
                    _glossFailureLogged = true;
                    Debug.LogWarning("[SomniumEuroTruck] material: baked gloss could not be read for " + (spec ?? env) + " (" + error.GetType().Name + ": " + error.Message + "); such surfaces keep one gloss. Logged once.");
                }

                textures.AdoptBakedGloss(spec, env, result as EtsTextureCache.GlossPlane);

                foreach (KeyValuePair<Material, float> waiting in wait.Materials)
                {
                    if (waiting.Key != null)
                    {
                        SetBakedGloss(waiting.Key, spec, env, waiting.Value);
                    }
                }
            });
        }

        private void SetBakedGloss(Material material, string spec, string env, float cap)
        {
            try
            {
                Texture2D gloss = _textures.LoadBakedGloss(spec, env, cap, out float reference);

                if (gloss == null || reference <= 0f)
                {
                    return;
                }

                ApplyGloss(material, gloss, reference);
                _glossApplied++;

                if (_glossApplied == 1)
                {
                    Debug.Log("[SomniumEuroTruck] material: baked gloss maps on (first " + material.name + ", " + gloss.width + "x" + gloss.height + ", smoothness reference " + reference.ToString("0.00") + ").");
                }

                if (_clonesOf.TryGetValue(material, out List<Material> clones))
                {
                    foreach (Material clone in clones)
                    {
                        if (clone != null)
                        {
                            ApplyGloss(clone, gloss, reference);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Report(material != null ? material.name : "baked gloss", "baked gloss: " + e.Message);
            }
        }

        private static void ApplyGloss(Material material, Texture2D gloss, float reference)
        {
            material.SetTexture(MetallicGlossMap, gloss);
            material.EnableKeyword("_METALLICSPECGLOSSMAP");
            material.SetFloat(Smoothness, reference);

            if (material.HasProperty(Metallic))
            {
                material.SetFloat(Metallic, 0f);
            }
        }

        /// <summary>
        /// Main thread: lays on the gloss maps the worker has finished, within about a millisecond a frame.
        /// Called from Load and WithVertexColour, which the builders call throughout any build.
        /// </summary>
        private void DrainGloss()
        {
            if (_glossWorker == null || _glossWorker.Waiting == 0)
            {
                return;
            }

            int frame = Time.frameCount;

            if (frame != _glossFrame)
            {
                _glossFrame = frame;
                _glossTicks = 0;
            }

            if (_glossTicks >= GlossBudgetTicks)
            {
                return;
            }

            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            long spent = _glossTicks;

            try
            {
                _glossWorker.Drain(() => spent + (System.Diagnostics.Stopwatch.GetTimestamp() - start) < GlossBudgetTicks);
            }
            catch (Exception e)
            {
                Report("baked gloss", "drain: " + e.Message);
            }

            _glossTicks = spent + (System.Diagnostics.Stopwatch.GetTimestamp() - start);
        }

        private readonly Dictionary<Material, Material> _gameGlass = new Dictionary<Material, Material>();

        /// <summary>
        /// A glass material as the GAME draws it, for map items (EtsModelBuilder.Options.GameGlass: buildings, props,
        /// parked cars - never a truck or a traffic car). eut2.glass is a forward_mult pass then a forward_add pass
        /// (/effect/eut2/_shd/27d67ba27ff19c2b2373496aa0fa409e.glsl.fso and 3b2372efb97b1a818dd3c3318b9f8a0d): what is
        /// behind is MULTIPLIED by tint^2 x (1 - opacity), and on top come opacity x diffuse x tint, lit, the highlight
        /// and the reflection; texture_base's colour is never read (its alpha only masks the highlight). Build takes the
        /// opacity for the alpha (at least 0.15) and multiplies the texture's alpha in, so the common glass (tint
        /// 0.56/0.60/0.59, opacity -0.01: the garage_sc headquarters' doors, bus stations, service stations, dealers)
        /// let 85-90 % through where the game lets 34 %: you saw straight into the empty shells (harness ProgramBld6).
        /// Here alpha = 1 - that transmission, the colour is what the game adds (black at opacity 0), no texture.
        /// Vehicles keep Build's glass: a cab's exterior model carries inward-facing glass (glass_int, the same tint)
        /// that the seated driver looks through, which the game's own interior view does not draw. Main thread; the
        /// material itself when it is not a tinted glass on the Lit shader.
        /// </summary>
        public Material WithGameGlass(Material material, EtsMaterialDef def)
        {
            if (material == null || def == null || !def.EffectHas("glass") || material.shader != _shader || !material.HasProperty(BaseColor))
            {
                return material;
            }

            float[] glassTint = def.GetFloats("glass_tint_color");

            if (glassTint.Length < 3)
            {
                return material;
            }

            if (_gameGlass.TryGetValue(material, out Material cached) && cached != null)
            {
                return cached;
            }

            float r = Mathf.Clamp01(glassTint[0]), g = Mathf.Clamp01(glassTint[1]), b = Mathf.Clamp01(glassTint[2]);
            float body = Mathf.Clamp01(def.GetFloat("glass_tint_opacity", 0f));
            float transmit = (r * r + g * g + b * b) / 3f * (1f - body);
            Color colour = new Color(r * body, g * body, b * body, 1f).gamma;
            colour.a = Mathf.Clamp(1f - transmit, 0.1f, 0.9f);

            var glass = new Material(material) { name = material.name + " game glass" };
            glass.SetTexture(BaseMap, null);
            if (glass.HasProperty(MainTex)) glass.SetTexture(MainTex, null);
            glass.SetColor(BaseColor, colour);
            _gameGlass[material] = glass;

            if (++GlassMaterials == 1)
            {
                Debug.Log("[SomniumEuroTruck] material: map glass as the game draws it (first " + material.name + ": lets " +
                          transmit.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " through, alpha " +
                          colour.a.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + ").");
            }

            return glass;
        }

        /// <summary>
        /// Map pieces (EtsModelBuilder.Options.GameGlass: buildings, props, parked cars) whose vertex colours vary are drawn by
        /// the two-layer shader in its plain mode (_LayerMode 0, _MultStrength 0), which multiplies the game's per-vertex
        /// colour x 2 in - the baked ambient occlusion the exe's help text says vertex colour "usually contains" - where URP
        /// Lit could only take the piece's mean (WithVertexColour). Only for what that shader draws the same as Lit: opaque or
        /// cut-out, no normal map, no emission, no gloss or reflection map, no environment term, not the baked family (its
        /// per-texel gloss is Lit's). Lighting 2026-09-25 (round 2, item 7); see VertexAoPieces for the count.
        /// </summary>
        public static bool VertexAoOnMapPieces = true;

        /// <summary>How many pieces took the per-vertex path (for the log).</summary>
        public static int VertexAoPieces;

        /// <summary>How many two-layer twins were made (one per Lit material that took the path).</summary>
        public static int VertexAoMaterials;

        private readonly Dictionary<Material, Material> _vertexAo = new Dictionary<Material, Material>();

        /// <summary>The two-layer-shader twin of a Lit material for per-vertex colour, or null when the material is not one it draws the same.</summary>
        public Material WithVertexAo(Material material, EtsMaterialDef def)
        {
            if (!VertexAoOnMapPieces || material == null || _shader == null || material.shader != _shader || def == null)
            {
                return null;
            }

            if (_vertexAo.TryGetValue(material, out Material cached))
            {
                return cached;
            }

            Material twin = null;
            Shader multi = FindMulti();
            bool eligible = multi != null && multi.isSupported && multi.FindPropertyIndex("_Cull") >= 0 &&
                            material.renderQueue < (int)RenderQueue.GeometryLast &&
                            !material.IsKeywordEnabled("_SURFACE_TYPE_TRANSPARENT") &&
                            !material.IsKeywordEnabled("_NORMALMAP") &&
                            !material.IsKeywordEnabled("_EMISSION") &&
                            !material.IsKeywordEnabled("_METALLICSPECGLOSSMAP") &&
                            material.IsKeywordEnabled("_ENVIRONMENTREFLECTIONS_OFF") &&
                            !def.EffectHas("baked") && !def.EffectHas("water") && !def.EffectHas("truckpaint") &&
                            !def.EffectHas("glass") && !def.EffectHas("decal") && !def.EffectHas("lamp") && !def.EffectHas("lum");

            if (eligible)
            {
                twin = new Material(multi) { name = material.name + " vao" };
                twin.enableInstancing = true;
                twin.SetTexture(BaseMap, material.GetTexture(BaseMap));
                twin.SetTextureScale(BaseMap, material.GetTextureScale(BaseMap));
                twin.SetTextureOffset(BaseMap, material.GetTextureOffset(BaseMap));
                twin.SetColor(BaseColor, material.GetColor(BaseColor));
                twin.SetFloat(Metallic, material.HasProperty(Metallic) ? material.GetFloat(Metallic) : 0f);
                twin.SetFloat(Smoothness, material.HasProperty(Smoothness) ? material.GetFloat(Smoothness) : 0.2f);
                twin.SetFloat(LayerMode, 0f);
                twin.SetFloat("_MultStrength", 0f);
                twin.SetFloat(EnvironmentReflections, 0f);
                twin.EnableKeyword("_ENVIRONMENTREFLECTIONS_OFF");

                if (material.IsKeywordEnabled("_ALPHATEST_ON"))
                {
                    twin.SetFloat(AlphaClipOn, 1f);
                    twin.SetFloat(Cutoff, material.HasProperty(Cutoff) ? material.GetFloat(Cutoff) : 0.5f);
                }

                if (material.HasProperty(Cull))
                {
                    twin.SetFloat(Cull, material.GetFloat(Cull));
                }

                twin.renderQueue = material.renderQueue;
                VertexAoMaterials++;
            }

            _vertexAo[material] = twin;
            return twin;
        }

        private readonly Dictionary<long, Material> _vertexTinted = new Dictionary<long, Material>();

        /// <summary>
        /// The material with a piece's mean vertex colour folded into its colour (see
        /// EtsModelBuilder.Build). factor is linear, 1 = the game's 128. Only for the plain Lit
        /// path: the decal shader multiplies vertex colour itself and the two-layer shader reads
        /// the alpha. Quantised to 1/32 so pieces share materials; within 6 % of neutral the
        /// material is returned unchanged.
        /// </summary>
        public Material WithVertexColour(Material material, Color factor)
        {
            DrainGloss();

            if (material == null || material.shader != _shader || !material.HasProperty(BaseColor))
            {
                return material;
            }

            int qr = Mathf.Clamp(Mathf.RoundToInt(factor.r * 32f), 0, 64);
            int qg = Mathf.Clamp(Mathf.RoundToInt(factor.g * 32f), 0, 64);
            int qb = Mathf.Clamp(Mathf.RoundToInt(factor.b * 32f), 0, 64);

            if (Mathf.Abs(qr - 32) <= 2 && Mathf.Abs(qg - 32) <= 2 && Mathf.Abs(qb - 32) <= 2)
            {
                return material;
            }

            long key = ((long)(uint)material.GetInstanceID() << 21) | ((long)qr << 14) | ((long)qg << 7) | (long)qb;

            if (_vertexTinted.TryGetValue(key, out Material cached) && cached != null)
            {
                return cached;
            }

            var tinted = new Material(material) { name = material.name + " vcol" };
            Color was = tinted.GetColor(BaseColor);
            Color linear = was.linear;
            linear.r *= qr / 32f;
            linear.g *= qg / 32f;
            linear.b *= qb / 32f;
            Color colour = linear.gamma;
            colour.a = was.a;
            tinted.SetColor(BaseColor, colour);
            _vertexTinted[key] = tinted;

            // So a gloss map that lands later (AskBakedGloss) reaches this copy too.
            if (!_clonesOf.TryGetValue(material, out List<Material> clones))
            {
                _clonesOf[material] = clones = new List<Material>();
            }

            clones.Add(tinted);
            return tinted;
        }

        /// <summary>
        /// eut2.decalshadow as the game draws it: black, alpha from the red channel, blended over
        /// what is already there. The decal shader does exactly that with a black colour: its
        /// rgb is texel x colour x vertex colour (the pieces carry vertex colour 0,0,0,255) and its
        /// alpha texel.a x vertex alpha x 2, saturated. Without the decal shader the piece is left
        /// out - the yard then shows its own asphalt without the baked shadow, which is a far
        /// smaller error than the grey slab.
        /// </summary>
        private Material BuildDecalShadow(EtsMaterialDef def, string path)
        {
            if (DecalShader == null || string.IsNullOrEmpty(def.BaseTexture))
            {
                return null;
            }

            Texture2D mask = _textures.LoadAlphaFromRed(def.BaseTexture);

            if (mask == null)
            {
                return null;
            }

            var material = new Material(DecalShader) { name = path };
            material.enableInstancing = true;
            material.SetTexture(BaseMap, mask);
            material.SetColor(BaseColor, new Color(0f, 0f, 0f, 1f));
            material.renderQueue = (int)RenderQueue.Transparent - 100;
            return material;
        }

        /// <summary>
        /// A leaves material as the game's leaves fragment program reads its .mat: texture_base (sRGB) is
        /// the colour and alpha, texture_mask (linear) the leaf mask in x, the specular mask in y and a
        /// shadow push in z; "diffuse" multiplies the texture (material0_data[0].xyz, gamma-encoded here as
        /// every other colour in this cache so it arrives as the file's number); the discard is the game's
        /// 0.05. Every vertex colour and billboard term is the shader's (EtsModelBuilder builds leaves meshes
        /// with all three uv sets and never folds a mean vertex colour into these materials). The shadow pass
        /// is switched off for the effects without ".shadow" (grass cards), as the game draws them.
        /// Band 0: drawn at every distance, the LODGroups deciding near and far until the tree field of
        /// Phase B takes over.
        /// </summary>
        private Material BuildLeaves(EtsMaterialDef def, string path)
        {
            var material = new Material(LeavesShader) { name = path };
            material.enableInstancing = true;

            Texture2D baseMap = def.BaseTexture != null ? _textures.Load(def.BaseTexture, false) : null;

            if (baseMap != null)
            {
                material.SetTexture(BaseMap, baseMap);
            }

            if (def.Textures.TryGetValue("texture_mask", out string maskPath) && !string.IsNullOrEmpty(maskPath))
            {
                Texture2D mask = _textures.Load(maskPath, true);

                if (mask != null)
                {
                    material.SetTexture(MaskMap, mask);
                }
            }

            Color tint = Color.white;
            float[] diffuse = def.GetFloats("diffuse");

            if (diffuse.Length >= 3)
            {
                tint = new Color(diffuse[0], diffuse[1], diffuse[2], 1f).gamma;
                tint.a = 1f;
            }

            bool shadow = def.EffectHas(".shadow");
            material.SetColor(BaseColor, tint);
            material.SetFloat(Cutoff, LeavesCutoff);
            material.SetFloat(CastsShadowId, shadow ? 1f : 0f);
            material.SetFloat(Band, 0f);

            // The .mat's LOD class (tree mats: lod_selector { 1, 0, 0 }, the /def/game_data.sii 240-300 m class): on band 0
            // the shader drops the model's fade-out vertices past the class's end and its fade-in ones (its own sprite)
            // before its start, as the game's vertex program does (EtsLeaves.shader, LeavesVertexAxes). Absent: no fading.
            float[] lod = def.GetFloats("lod_selector");
            material.SetVector(LodSelectorId, lod.Length >= 3 ? new Vector4(lod[0], lod[1], lod[2], 0f) : Vector4.zero);
            material.SetShaderPassEnabled("ShadowCaster", shadow);
            material.renderQueue = (int)RenderQueue.AlphaTest;
            return material;
        }

        private Material Fallback()
        {
            if (_fallback != null)
            {
                return _fallback;
            }

            Shader shader = FindShader();

            if (shader == null)
            {
                return null;
            }

            _fallback = new Material(shader) { name = "ets fallback" };
            _fallback.SetColor(BaseColor, new Color(0.6f, 0.6f, 0.62f, 1f));
            return _fallback;
        }

        private void Report(string path, string why)
        {
            if (_reported.Add(path))
            {
                Debug.LogWarning("[SomniumEuroTruck] material " + path + ": " + why);
            }
        }

        public void Clear()
        {
            foreach (Material material in _byPath.Values)
            {
                if (material != null)
                {
                    UnityEngine.Object.Destroy(material);
                }
            }

            _byPath.Clear();
            _reported.Clear();

            foreach (Material material in _vertexTinted.Values)
            {
                if (material != null)
                {
                    UnityEngine.Object.Destroy(material);
                }
            }

            _vertexTinted.Clear();

            foreach (Material material in _gameGlass.Values)
            {
                if (material != null)
                {
                    UnityEngine.Object.Destroy(material);
                }
            }

            _gameGlass.Clear();
            _clonesOf.Clear();
            _glossWaits.Clear();

            if (_glossWorker != null)
            {
                _glossWorker.Dispose();
                _glossWorker = null;
            }

            if (_fallback != null)
            {
                UnityEngine.Object.Destroy(_fallback);
                _fallback = null;
            }
        }
    }
}
