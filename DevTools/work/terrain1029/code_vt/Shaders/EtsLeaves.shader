// The game's leaves, drawn the way the game draws them: every tree, bush and grass card of Euro Truck
// Simulator 2 (owner F; environment DESIGN.md Phase A, PLAN.md §8 F1).
//
// What the game does, read from the demo's own compiled programs for eut2.leaves.shadow
// (/effect/eut2/_shd/ef447134a6749386441b43ba1987cd28.glsl.vso and d98ecb96da47988d6532e49c773a71dc.glsl.fso;
// research copies in the environment design's probes/files):
//  - Every crown is a set of CAMERA-FACING CARDS. The vertex program pushes each vertex along two eye-space axes
//    by uv set 1 (a size in metres), on each axis where uv set 2 (the lock flags, 0 or 0.6) is 0.5 or more. The
//    world used to draw the stored positions only: 4-20 % of a deciduous crown's leaf area and none of a sprite
//    LOD, which is a strip 1 cm wide (harness Program1705). Here the push is made in world space along the
//    camera's roll-free right and up (_EtsVegCamRight / _EtsVegCamUp, set per camera by EtsVegetationLook),
//    which is the same offset.
//  - The vertex colour is DATA, not colour, except red: the colour is (r, r, r) x 2 x texture x "diffuse".
//    Green, blue and alpha carry fade flags, wind strength, phase and wiggle, decoded exactly as the game does
//    (packed when green and blue are both 0.5 or more, else the plain reading) - see LeavesColour.
//  - Wind: horizontal sway strength x wind vector x cos(t + phase), vertical sway on cos(1.618 x), and a wiggle
//    of 2 % of the card offset on cos(2x) and cos(4x). The amplitudes are engine constants not in the files; the
//    wind vector here is EtsVegetationLook's (_EtsWind, 0.12 m by default).
//  - The discard is at 0.05 alpha (the world's Lit path used 0.4). Leaf texels (texture_mask.x) are lit with a
//    "crown" normal bent from up toward the sun by ((0.5 N.L + 0.5)^2)^2; what the engine's leaves_data[4] holds is
//    not in the files and is taken as world up.
//
// Lighting (trees owner, 2026-09-25; research N/wf1/vegetation.md findings 2-4, the round's AMBIENT CONTRACT). The
// game's forward leaves program (/effect/eut2/_shd/2aad9e28db25763cde31ea0b20c47e4d.glsl.fso) and its deferred sun
// pass (/effect/deferred/_shd/44ef718a5263cda3e2794db5a383137b.glsl.fso, 6d0cdbb3...) light a leaf with
// sun x saturate(N.L) on the crown normal + ambient_env, and nothing else:
//  - no wrap and no translucency (the world had 0.5 wrap and 0.25 back light, "look choices": crowns came out
//    1.23 : 1 across where the game's are 1.85 : 1);
//  - the ambient is NOT scaled by 2 x mask.x: that factor multiplies only additional_ambient_color, the climate's
//    rain/snow ambient (0 in clear weather). The world gave leaf texels x0.18-0.24 of the ambient (mask.x means
//    0.089-0.121), so shaded crowns were ~4x too dark and speckled where mask.x is 0;
//  - the ambient is SampleSH(normal) (the lighting owner's directional ambient) x the sun pass's near-field factor
//    saturate(dist_amb + (1 - dist_amb) (1 - N.V)^5), dist_amb = 0.6 + 0.4 saturate(d^2 / 22500), d the distance
//    from the eye (the same formula EtsMulti and EtsDecal use);
//  - the shadow is looked up at the texel pushed toward the sun by its shadow offset, texture_mask.z metres
//    (_es_position + to_light x shadow_offset in the sun pass), so a crown does not shadow itself where cards meet.
//
// Shadows (same date). The game's shadow pass for eut2.leaves.shadow (958b44ae...glsl.vso) turns the cards to the
// LIGHT (leaves_data[3] in the shadow view) and drops every vertex flagged fade-in (a sprite) or shadow-disabled
// (the vertex colour's blue bit); ShadowCaster here does the same (LeavesVertexAxes with the light's axes; the other
// passes keep the camera's). Its fragment keeps mask.a >= (0.5 N.L + 0.5)^2, and mask.a is a 1-bit copy of the base
// alpha (37.2 % against 37.6 % coverage), so the base-alpha discard here is the same test.
//
// The cut-out at a distance (_CutoffFar, _CutoffMips; trees owner, 2026-09-23, after "some trees are not cut out
// correctly in the distance"). The discard is the game's 0.05 where the texture is magnified or at full size, and
// rises with the mip level the pixel samples to 0.4 at mip 2 and beyond. Measured on the demo's own textures
// (harness Programs 2311, 2315-2317): most leaf atlases keep a 1-bit alpha down their mip chain (buk_tree, sc_lime,
// poplar, spruce2, tree1_fr: only 0 or >= 0.75 below mip 0), but the far-tree and tree-wall atlases the route's sets
// v2_16fr and v2_49 draw at every distance have box-filtered mips: trees_far1_fr.tobj holds 41.9 % of texels at
// alpha >= 0.05 at mip 0 and 59 / 70 / 91 % at mips 5 / 6 / 7, treewall_dense1.tobj 72.7 % and 84 / 88 / 100 %,
// while the share at >= 0.5 stays at 41-42 % and 72-73 %. So 0.05 on those mips cuts a card out as its whole
// rectangle. The previous rule here made it worse: it raised the alpha x (1 + mip / 4) before the 0.05 test (built
// for mips that lose coverage, which none of these do). Drawn on the CPU against the truth (the mip-0 alpha averaged
// over each pixel, >= 0.5), a far tree 16 px tall covered 83 % against 52 % (32 % of its pixels wrong), 8 px tall
// 100 % - a solid box - and a tree wall 16 px tall 95 % against 78 %; with the rise to 0.4 they come to 54 % and 81 %
// (2-3 % wrong), and the 1-bit atlases stay within 1-5 % of their truth (they were 3-11 % over at the same sizes).
// The same rule runs in every pass, so depth, shadow and colour cut the same pixels in both eyes.
//
// Distance fade (_Band): 0 draws at every distance (map-placed trees, whose LODGroups pick near or sprite); 1 = near
// tree, fading out over _EtsVegFade.x +- .y by the tree's pivot (uv3.xyz, else the object origin); 2 = far sprite,
// fading in over the same band, exactly the complement (the game's in/out discard on one shared threshold,
// interleaved gradient noise per pixel); 3 = detail card, out over .z +- .w. Outside its band a card collapses onto
// its pivot, so it costs no fragments. The planted trees (EtsTreeField, GPU instanced) draw their near and hi-poly
// models on band 1 and their sprites on band 2, with the game's /def/game_data.sii leaves_lod 240-300 m
// (EtsVegetationLook.Fade.x 270, .y 30).
//
// XR. The headset is single-pass instanced (EtsMulti.shader, header): every pass here carries the instance id
// and, except the shadow caster (shadow maps are not per eye, as URP's own ShadowCasterPass.hlsl), the stereo
// output and the eye set-up in the fragment. The client forces depth priming (URP_Renderer.asset
// m_DepthPrimingMode 2), so opaque colour is drawn ZTest Equal against DepthOnly or DepthNormals: all passes push
// the vertices through the same LeavesVertex function and clip through the same LeavesClip, or a card would be
// missing in colour where depth has it.
Shader "SomniumEuroTruck/Leaves"
{
    Properties
    {
        _BaseMap ("Base (texture_base)", 2D) = "white" {}
        _MaskMap ("Mask (texture_mask: x leaf, y specular, z shadow push)", 2D) = "white" {}
        _BaseColor ("Colour (.mat diffuse)", Color) = (1, 1, 1, 1)
        _Cutoff ("Alpha discard (the game's 0.05)", Range(0, 1)) = 0.05
        _CutoffFar ("Alpha discard at a distance (reached at _CutoffMips)", Range(0, 1)) = 0.4
        _CutoffMips ("Mip levels over which the discard rises", Range(0.25, 8)) = 2
        _Band ("Band (0 plain, 1 near tree, 2 far sprite, 3 detail card)", Float) = 0
        _CastsShadow ("Casts shadow (informational)", Float) = 1
        _LodSelector ("LOD class weights (the .mat lod_selector; band 0 only)", Vector) = (0, 0, 0, 0)
    }

    SubShader
    {
        Tags { "Queue" = "AlphaTest" "RenderType" = "TransparentCutout" "RenderPipeline" = "UniversalPipeline" }
        Cull Off

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        TEXTURE2D(_BaseMap);
        SAMPLER(sampler_BaseMap);
        TEXTURE2D(_MaskMap);
        SAMPLER(sampler_MaskMap);

        CBUFFER_START(UnityPerMaterial)
            float4 _BaseMap_ST;
            float4 _BaseMap_TexelSize;
            float4 _MaskMap_ST;
            half4 _BaseColor;
            half _Cutoff;
            half _CutoffFar;
            float _CutoffMips;
            float _Band;
            float _CastsShadow;
            float4 _LodSelector;
        CBUFFER_END

        // The planted trees (EtsTreeField, Graphics.RenderMeshPrimitives): their matrices live in a buffer uploaded when
        // a list changes (a far cell's sprites when a side joins or goes, the near cells' models 4 times a second), read
        // by instance id under _ETS_TREE_BUFFER, which only the field's own material copies enable. It is a plain
        // multi_compile keyword on purpose: the field's materials are made at runtime, so no material in the bundle asks
        // for Unity's instancing variants and the project's Instancing Variants "Strip Unused" (GraphicsSettings
        // m_InstancingStripping 0) would drop INSTANCING_ON / PROCEDURAL_INSTANCING_ON from the build; a multi_compile
        // variant is always kept, and the editor's render lab draws exactly the variant the client will.
        // _EtsTreeCount is the list's length: EtsTreeField asks for twice as many instances in a headset (whether Unity
        // doubles an instanced count for the two eyes itself is not documented), and every id at or past the length
        // collapses to one point (a zero matrix: no area, no fragments), so each eye draws the list exactly once.
        #if defined(_ETS_TREE_BUFFER)
            StructuredBuffer<float4x4> _EtsTreeMatrices;
            float _EtsTreeCount;
        #endif

        // Per camera, from EtsVegetationLook (zero when nothing sets them: no cards, no wind, no fade).
        float4 _EtsVegCamPos;
        float4 _EtsVegCamRight;
        float4 _EtsVegCamUp;
        float4 _EtsWind;
        float4 _EtsVegFade;

        struct LeavesAttributes
        {
            float4 positionOS : POSITION;
            float3 normalOS : NORMAL;
            half4 color : COLOR;
            float2 uv0 : TEXCOORD0;   // the atlas (game set 0)
            float2 uv1 : TEXCOORD1;   // the card size in metres (game set 1)
            float2 uv2 : TEXCOORD2;   // the lock flags (game set 2)
            float4 uv3 : TEXCOORD3;   // optional: pivot in object space, random 0..1 (combined tree meshes)
            UNITY_VERTEX_INPUT_INSTANCE_ID
        #if defined(_ETS_TREE_BUFFER) && !UNITY_ANY_INSTANCING_ENABLED
            uint treeInstance : SV_InstanceID; // the field's draws in a variant without Unity's instance id (mono, no instancing keyword)
        #endif
        };

        // The object-to-world matrix: the field's buffer by instance id (after UNITY_SETUP_INSTANCE_ID, which splits the
        // eyes off the id in a headset), else the renderer's own.
        float4x4 LeavesObjectToWorld(LeavesAttributes input)
        {
        #if defined(_ETS_TREE_BUFFER)
            #if UNITY_ANY_INSTANCING_ENABLED
                uint index = unity_InstanceID;
            #else
                uint index = input.treeInstance;
            #endif
            return (float)index < _EtsTreeCount ? _EtsTreeMatrices[index] : (float4x4)0;
        #else
            return GetObjectToWorldMatrix();
        #endif
        }

        struct LeavesVertexOut
        {
            float3 positionWS;
            float3 normalWS;
            float fade;          // 0 near .. 1 far through the band; -1 for band 0 (no dither)
            float shadowStrength;
            float3 pivotWS;      // where a collapsed vertex goes
            float noShadow;      // 1 where the game's shadow pass drops the vertex: fade-in (a sprite) or shadow-disabled
        };

        // The game's vertex colour reading (ef447134...glsl.vso and the shadow pass 958b44ae...glsl.vso, main): packed
        // when g and b are both >= 0.5. shadowDisabled is the packed blue's top bit, else the alpha flag.
        void LeavesColour(half4 c, out float fadeIn, out float fadeOut, out float shadowDisabled, out float phase,
                          out float horizontalPhase, out float strength, out float verticalStrength, out float wiggle)
        {
            float4 flags = step(0.5, (float4)c);

            if (flags.y > 0.5 && flags.z > 0.5)
            {
                float g1 = round(c.y * 255.0) - 128.0;
                float fadeInOrOut = floor(g1 / 42.0);
                fadeIn = fadeInOrOut == 1.0 ? 1.0 : 0.0;
                fadeOut = fadeInOrOut == 2.0 ? 1.0 : 0.0;
                verticalStrength = ((g1 - 42.0 * fadeInOrOut) / 41.0) * 1.5;
                float b11 = round(c.z * 255.0) - 128.0;
                shadowDisabled = b11 >= 64.0 ? 1.0 : 0.0;
                strength = ((b11 - 64.0 * shadowDisabled) / 63.0) * 1.5;
                float a11 = round(c.w * 255.0);
                float aHigh = floor(a11 / 16.0);
                phase = (aHigh / 15.0) * PI;
                wiggle = (a11 - 16.0 * aHigh) / 15.0;
                horizontalPhase = 0.0;
            }
            else
            {
                fadeIn = flags.y;
                fadeOut = flags.z;
                shadowDisabled = flags.w;
                float4 x = saturate(2.0 * (float4)c - 1.004 * flags);
                phase = x.y;
                horizontalPhase = x.y;
                strength = x.z;
                verticalStrength = 0.0;
                wiggle = x.w;
            }
        }

        // One function for every pass, so depth and colour see the same card to the bit; the shadow caster gives the
        // light's axes (the game's shadow pass faces the cards to the light), the others the camera's.
        LeavesVertexOut LeavesVertexAxes(LeavesAttributes input, float3 right, float3 up)
        {
            LeavesVertexOut o;
            float4x4 objectToWorld = LeavesObjectToWorld(input);
            float3 originWS = float3(objectToWorld._m03, objectToWorld._m13, objectToWorld._m23);
            float scale = length(float3(objectToWorld._m00, objectToWorld._m10, objectToWorld._m20));

            float3 positionWS = mul(objectToWorld, float4(input.positionOS.xyz, 1.0)).xyz;
            float3 pivotWS = mul(objectToWorld, float4(input.uv3.xyz, 1.0)).xyz;

            // The card: size x lock, along the given right and up.
            float2 lockAxis = step(0.5, input.uv2);
            float2 size = input.uv1 * lockAxis * scale;
            float2 cardOffset = size;

            float fadeIn, fadeOut, shadowDisabled, phase, horizontalPhase, strength, verticalStrength, wiggle;
            LeavesColour(input.color, fadeIn, fadeOut, shadowDisabled, phase, horizontalPhase, strength, verticalStrength, wiggle);

            // Per-tree variety: uv3.w for combined meshes, plus a hash of the object's origin.
            float random = frac(input.uv3.w + frac(sin(dot(frac(originWS.xz * 0.0137), float2(12.9898, 78.233))) * 43758.5453));
            float x = _EtsWind.w + phase + random * 6.2831853;
            float c2 = cos(2.0 * x);
            float wave4x = 2.0 * c2 * c2 - 1.0;

            // The game's wiggle: 2 % of the card offset, by the sign of the card corner.
            float2 quadrant = sign(input.uv1);
            float2 wiggleLimit = abs(0.02 * cardOffset);
            float2 wiggleOffset = quadrant.yx * float2(-wiggleLimit.x, wiggleLimit.y) * wave4x;
            wiggleOffset.y += wiggleLimit.y * c2;
            float2 card = cardOffset + wiggle * wiggleOffset;

            positionWS += right * card.x + up * card.y;

            // Wind: sideways on cos(t + phase), vertical on cos(1.618 x), scaled to the wind's own size.
            float windSize = length(_EtsWind.xyz);
            positionWS += _EtsWind.xyz * strength * cos(_EtsWind.w + horizontalPhase + random * 6.2831853);
            positionWS.y += verticalStrength * windSize * 0.8 * cos(1.618034 * x);

            // Distance fade by the pivot, in the band this material draws.
            float distanceToEye = distance(_EtsVegCamPos.xyz, pivotWS);
            float t = -1.0;
            bool collapse = false;

            if (_Band > 0.5 && _Band < 2.5 && _EtsVegFade.y > 0.0)
            {
                t = saturate((distanceToEye - (_EtsVegFade.x - _EtsVegFade.y)) / (2.0 * _EtsVegFade.y));

                // A near model's fade-in vertices are its own sprite (buk_tree_1_med v312-319 are
                // buk_tree_1_sprite's 8 vertices, flagged fade-in; probe N/trees/probe/t2_*.txt): the game shows
                // them only from the band on, as the sprite (band 2) here does, so the near model never draws them.
                collapse = _Band < 1.5 ? (t >= 1.0 || fadeIn > 0.5) : t <= 0.0;
            }
            else if (_Band > 2.5 && _EtsVegFade.w > 0.0)
            {
                t = saturate((distanceToEye - (_EtsVegFade.z - _EtsVegFade.w)) / (2.0 * _EtsVegFade.w));
                collapse = t >= 1.0;
            }
            else if (_Band < 0.5 && _EtsVegFade.y > 0.0 && dot(_LodSelector.xyz, float3(1.0, 1.0, 1.0)) > 0.0)
            {
                // Band 0 with the material's own LOD class (its .mat lod_selector, e.g. {1, 0, 0} on the tree mats,
                // over /def/game_data.sii leaves_lod_start (240, 410, 50) / leaves_lod_end (300, 460, 100)): the game's
                // per-vertex rule (ef447134...vso, 72555ed8...vso): fade-out vertices go past the end, fade-in
                // vertices (the model's own sprite) before the start, dithered in between; unflagged ones always
                // draw. A map-placed tree (a med model, no LOD swap) then shows its sprite far and its crown near,
                // as the game does, instead of both at every distance (trees owner, 2026-09-25).
                float lodStart = dot(_LodSelector.xyz, float3(240.0, 410.0, 50.0));
                float lodEnd = max(dot(_LodSelector.xyz, float3(300.0, 460.0, 100.0)), lodStart + 1.0);
                float lodT = saturate((distanceToEye - lodStart) / (lodEnd - lodStart));

                if (fadeOut > 0.5)
                {
                    t = lodT;
                    collapse = lodT >= 1.0;
                }
                else if (fadeIn > 0.5)
                {
                    t = 2.0 + lodT; // LeavesClip: 2 + t dithers IN (the far rule)
                    collapse = lodT <= 0.0;
                }
            }

            if (collapse)
            {
                positionWS = pivotWS;
            }

            #if defined(_ETS_TREE_BUFFER)
                float3 normalWS = normalize(mul((float3x3)objectToWorld, input.normalOS)); // the field's scale is uniform
            #else
                float3 normalWS = TransformObjectToWorldNormal(input.normalOS);
            #endif

            // The game's self-shadow strength: up-facing texels high in the crown take half the shadow. Its height is the
            // MODEL's (ATTR0.y); a detail card's combined cell mesh is in world units, so band 3 measures from the card's
            // pivot (uv3), which gives the game's 0.2-0.5 m and a full shadow (grass owner, 2026-09-25).
            float modelY = _Band > 2.5 ? input.positionOS.y - input.uv3.y : input.positionOS.y;
            float heightFactor = saturate(modelY * 0.2 - 1.0);
            o.shadowStrength = 1.0 - 0.5 * saturate(normalWS.y) * heightFactor;
            o.positionWS = positionWS;
            o.normalWS = normalWS;
            o.fade = t;
            o.pivotWS = pivotWS;
            o.noShadow = max(fadeIn, shadowDisabled);
            return o;
        }

        LeavesVertexOut LeavesVertex(LeavesAttributes input)
        {
            return LeavesVertexAxes(input, _EtsVegCamRight.xyz, _EtsVegCamUp.xyz);
        }

        // Interleaved gradient noise (the game's threshold map: fract(52.98 fract(0.0671 x + 0.00584 y))), per pixel.
        float LeavesNoise(float2 pixel)
        {
            return frac(52.9829189 * frac(dot(pixel, float2(0.06711056, 0.00583715))));
        }

        // The same discard in every pass: the base alpha against a cutoff that rises from the game's 0.05 (mip 0 and
        // magnified) to _CutoffFar at _CutoffMips mip levels (header), then the band's dither. The mip is the one the
        // sampler picks without anisotropy (the longer screen axis of the texel footprint); the derivatives are of the
        // same uv in every pass, so depth, shadow and colour agree. The caller samples the texture first, so no
        // sample follows the discard.
        void LeavesClip(half alpha, float2 uv, float fade, float4 positionCS)
        {
            float2 texel = uv * _BaseMap_TexelSize.zw;
            float2 dx = ddx(texel);
            float2 dy = ddy(texel);
            float mip = 0.5 * log2(max(max(dot(dx, dx), dot(dy, dy)), 1e-8));
            half far = (half)saturate(mip / max(_CutoffMips, 0.25));
            half cutoff = lerp(_Cutoff, max(_Cutoff, _CutoffFar), far);
            clip(alpha - cutoff);

            if (fade >= 0.0)
            {
                float noise = LeavesNoise(positionCS.xy);
                // near (1) and detail (3) keep the pixel while the noise is at or above t; far (2), and a band-0
                // fade-in vertex (fade 2 + t), while below it
                bool fadingIn = fade >= 2.0 || (_Band > 1.5 && _Band < 2.5);
                float t = fade >= 2.0 ? fade - 2.0 : fade;
                clip(fadingIn ? t - noise - 1e-5 : noise - t);
            }
        }
        ENDHLSL

        Pass
        {
            Name "Forward"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fog
            #pragma multi_compile_instancing
            #pragma multi_compile_local_vertex _ _ETS_TREE_BUFFER
            // SM 5.0: the tree field reads a StructuredBuffer in the vertex stage (every PC VR GPU has it)
            #pragma target 4.5

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            // The near-field ambient factor's constants and switch, from EtsClimate.Apply (x near 0.6, y 1 / 150^2, w on).
            float4 _EtsAmbientNear;

            // The game's fog colour, from EtsClimate.Apply as EtsMulti and EtsDecal read it: lerp(fog_color, fog_color2,
            // sat(dot(view, toSun))^2) (fog pass 49f94099c3c3639c12cc48552413d810.glsl.fso), _EtsFogColor2.w on; else
            // Unity's fog colour. The same colour as the ground and buildings behind a tree, so a far tree line fades
            // into the haze exactly as the hill it stands on (trees owner, 2026-09-25).
            float4 _EtsFogColor1;
            float4 _EtsFogColor2;
            float4 _EtsFogSunDir;

            half3 LeavesFogColour(float3 positionWS)
            {
                float3 view = normalize(positionWS - GetCameraPositionWS());
                half t = (half)saturate(dot(view, _EtsFogSunDir.xyz));
                half3 directional = lerp((half3)_EtsFogColor1.rgb, (half3)_EtsFogColor2.rgb, t * t);
                return _EtsFogColor2.w > 0.5 ? directional : (half3)unity_FogColor.rgb;
            }

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                float3 normalWS : TEXCOORD2;
                float4 data : TEXCOORD3;     // x fade, y shadow strength, z fog, w red x 2
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings Vert(LeavesAttributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                LeavesVertexOut v = LeavesVertex(input);
                output.positionCS = TransformWorldToHClip(v.positionWS);
                output.positionWS = v.positionWS;
                output.normalWS = v.normalWS;
                output.uv = TRANSFORM_TEX(input.uv0, _BaseMap);
                output.data = float4(v.fade, v.shadowStrength, ComputeFogFactor(output.positionCS.z), input.color.r * 2.0);
                return output;
            }

            half4 Frag(Varyings input, FRONT_FACE_TYPE frontFace : FRONT_FACE_SEMANTIC) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                half4 texel = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv);
                half4 mask = SAMPLE_TEXTURE2D(_MaskMap, sampler_MaskMap, input.uv);
                LeavesClip(texel.a, input.uv, input.data.x, input.positionCS);
                half3 albedo = texel.rgb * _BaseColor.rgb * (half)input.data.w;

                // Cards are two-sided: the back face lights with the normal turned round. A detail card (band 3) keeps
                // its normal whichever side is seen: it is the TERRAIN's normal under it, as the game's detveg program
                // passes it (5aa84f47...glsl.vso, ATTR14.zw; the game's leaves fragment has no front-face term).
                float3 vertexNormal = normalize(input.normalWS) * (_Band > 2.5 ? 1.0 : IS_FRONT_VFACE(frontFace, 1.0, -1.0));
                float3 toLight = _MainLightPosition.xyz;

                // The receiver's lookup pushed toward the sun by the texel's shadow offset (texture_mask.z metres, the
                // game's sun pass: _es_position + to_light x shadow_offset), so a crown does not shadow itself where
                // its cards cross.
                Light light = GetMainLight(TransformWorldToShadowCoord(input.positionWS + toLight * (float)mask.z));

                // The game's crown normal on leaf texels: bent from up toward the sun by the crown factor,
                // blended back toward the vertex normal with distance (vertex_normal_coef).
                float scaled = 0.5 * dot(vertexNormal, toLight) + 0.5;
                float crown = scaled * scaled;
                crown *= crown;
                float3 crownNormal = normalize(lerp(float3(0.0, 1.0, 0.0), toLight, crown));
                float distanceToEye = distance(GetCameraPositionWS(), input.positionWS);
                float nearCoef = lerp(1.0, saturate(distanceToEye * 0.0025 - 0.125), 0.75);
                float normalCoef = mask.x > 0.0 ? nearCoef : 1.0;
                float3 normal = normalize(lerp(crownNormal, vertexNormal, normalCoef));

                half shadow = lerp(1.0h, light.shadowAttenuation, (half)input.data.y) * light.distanceAttenuation;

                // The game's sun: plain Lambert on the crown normal (no wrap, no back light).
                half diffuse = (half)saturate(dot(normal, toLight));

                // The game's ambient, unscaled (2 x mask.x multiplies only the weather's additional ambient, 0 when
                // clear), times its sun pass's near-field factor: 0.6 near the eye, 1 by 150 m, lifted toward 1 where
                // the surface is seen edge-on (AMBIENT CONTRACT). The factor's constants and its on switch are the
                // lighting owner's global _EtsAmbientNear (EtsClimate.Apply: x 0.6, y 1 / 150^2, w on), read exactly as
                // EtsMulti's EtsNearAmbient reads them, so leaves, ground and buildings always agree (all zero before
                // the climate is applied, which leaves the ambient flat).
                float3 toEye = (GetCameraPositionWS() - input.positionWS) / max(distanceToEye, 1e-4);
                half nearAmbient = (half)_EtsAmbientNear.x;
                half distanceAmbient = nearAmbient + (1.0h - nearAmbient) * (half)saturate(distanceToEye * distanceToEye * _EtsAmbientNear.y);
                half grazing = (half)(1.0 - saturate(dot(normal, toEye)));
                half grazing2 = grazing * grazing;
                half nearField = lerp(1.0h, saturate(distanceAmbient + (1.0h - distanceAmbient) * grazing2 * grazing2 * grazing), (half)_EtsAmbientNear.w);

                half3 colour = albedo * (light.color * diffuse * shadow + SampleSH(normal) * nearField);
                colour = MixFogColor(colour, LeavesFogColour(input.positionWS), input.data.z);
                return half4(colour, 1.0h);
            }
            ENDHLSL
        }

        // Shadow maps are not per eye: instance id only, as URP's ShadowCasterPass.hlsl. The cards turn to the LIGHT,
        // as the game's shadow pass turns them (958b44ae...glsl.vso: leaves_data[3] in the shadow view): right is the
        // light's view direction turned a quarter in the ground plane, up = forward x right (EtsVegetationLook.CameraAxes
        // for a camera looking along the light). Vertices the game drops from the shadow (fade-in: a sprite LOD;
        // shadow-disabled) collapse onto the pivot, so they cast nothing.
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ZWrite On
            ZTest LEqual
            ColorMask 0

            HLSLPROGRAM
            #pragma vertex ShadowVert
            #pragma fragment ShadowFrag
            #pragma multi_compile_instancing
            #pragma multi_compile_local_vertex _ _ETS_TREE_BUFFER
            // SM 5.0: the tree field reads a StructuredBuffer in the vertex stage (every PC VR GPU has it)
            #pragma target 4.5

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float3 _LightDirection;

            struct ShadowVaryings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float fade : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            ShadowVaryings ShadowVert(LeavesAttributes input)
            {
                ShadowVaryings output = (ShadowVaryings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);

                float3 lightForward = -_LightDirection;
                float3 lightRight = normalize(float3(lightForward.z, 0.0, -lightForward.x) + float3(1e-5, 0.0, 0.0));
                float3 lightUp = cross(lightForward, lightRight);
                LeavesVertexOut v = LeavesVertexAxes(input, lightRight, lightUp);
                float3 casterWS = ApplyShadowBias(v.positionWS, v.normalWS, _LightDirection);
                float4 positionCS = TransformWorldToHClip(v.noShadow > 0.5 ? v.pivotWS : casterWS);

                #if UNITY_REVERSED_Z
                    positionCS.z = min(positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #else
                    positionCS.z = max(positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #endif

                output.positionCS = positionCS;
                output.uv = TRANSFORM_TEX(input.uv0, _BaseMap);
                output.fade = v.fade;
                return output;
            }

            half4 ShadowFrag(ShadowVaryings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                LeavesClip(SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv).a, input.uv, input.fade, input.positionCS);
                return 0;
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }

            ZWrite On
            ColorMask 0

            HLSLPROGRAM
            #pragma vertex DepthVert
            #pragma fragment DepthFrag
            #pragma multi_compile_instancing
            #pragma multi_compile_local_vertex _ _ETS_TREE_BUFFER
            // SM 5.0: the tree field reads a StructuredBuffer in the vertex stage (every PC VR GPU has it)
            #pragma target 4.5

            struct DepthVaryings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float fade : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            DepthVaryings DepthVert(LeavesAttributes input)
            {
                DepthVaryings output = (DepthVaryings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                LeavesVertexOut v = LeavesVertex(input);
                output.positionCS = TransformWorldToHClip(v.positionWS);
                output.uv = TRANSFORM_TEX(input.uv0, _BaseMap);
                output.fade = v.fade;
                return output;
            }

            half4 DepthFrag(DepthVaryings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                LeavesClip(SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv).a, input.uv, input.fade, input.positionCS);
                return 0;
            }
            ENDHLSL
        }

        // The depth prepass URP runs instead of DepthOnly when something asks for the camera normals (SSAO, DBuffer
        // decals). With depth priming forced it is then the pass colour is Equal-tested against, so it pushes and
        // clips exactly as the others. Pattern: EtsMulti.shader's DepthNormals, URP 17.3 DepthNormalsPass.hlsl.
        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }

            ZWrite On

            HLSLPROGRAM
            #pragma vertex DepthNormalsVert
            #pragma fragment DepthNormalsFrag
            #pragma multi_compile_instancing
            #pragma multi_compile_local_vertex _ _ETS_TREE_BUFFER
            // SM 5.0: the tree field reads a StructuredBuffer in the vertex stage (every PC VR GPU has it)
            #pragma target 4.5
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/RenderingLayers.hlsl"

            struct DepthNormalsVaryings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float fade : TEXCOORD2;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            DepthNormalsVaryings DepthNormalsVert(LeavesAttributes input)
            {
                DepthNormalsVaryings output = (DepthNormalsVaryings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                LeavesVertexOut v = LeavesVertex(input);
                output.positionCS = TransformWorldToHClip(v.positionWS);
                output.uv = TRANSFORM_TEX(input.uv0, _BaseMap);
                output.normalWS = NormalizeNormalPerVertex(v.normalWS);
                output.fade = v.fade;
                return output;
            }

            void DepthNormalsFrag(DepthNormalsVaryings input, FRONT_FACE_TYPE frontFace : FRONT_FACE_SEMANTIC
                , out half4 outNormalWS : SV_Target0
            #ifdef _WRITE_RENDERING_LAYERS
                , out uint outRenderingLayers : SV_Target1
            #endif
                )
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                LeavesClip(SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv).a, input.uv, input.fade, input.positionCS);
                outNormalWS = half4(NormalizeNormalPerPixel(input.normalWS * (_Band > 2.5 ? 1.0 : IS_FRONT_VFACE(frontFace, 1.0, -1.0))), 0.0);

                #ifdef _WRITE_RENDERING_LAYERS
                    outRenderingLayers = EncodeMeshRenderingLayer();
                #endif
            }
            ENDHLSL
        }
    }

    Fallback Off
}
