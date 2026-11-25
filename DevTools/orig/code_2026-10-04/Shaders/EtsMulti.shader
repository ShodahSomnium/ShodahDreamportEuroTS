// Two-layer surface for the Euro Truck world: buildings, trailers, containers, walls.
//
// A third of everything in view uses one of the game's multi-texture effects -
// eut2.dif.spec.mult.dif.spec, .amod., .iamod., eut2.building.* - and every one of them lays a
// second texture over the first. Near Frankfurt, 634 of 1788 materials do it. URP's Lit shader
// has no second layer, so all of those were drawn with only their first texture, which is why
// the city came out as flat white and grey blocks: a building's first texture is a small tiled
// concrete or brick, and all of its character - the weathering, the window bands, the panel
// lines - lives in the second one.
//
// Measured on /model2/building/administration/admin_13.pmd, whose walls are
// eut2.dif.spec.mult.dif.spec.iamod.dif.spec.tsnmapuv: uv set 0 covers its texture 4.63 times
// (the tiled concrete_gs_01), set 1 covers it 0.99 times and set 2 0.68 - so set 0 tiles the
// base and set 1 is a true unwrap for the layer above it, matching the order the effect name
// reads in, dif.spec then mult.dif.spec. concrete_over_01, the mult texture, is a 512x4096
// atlas of grime and streaking bands, which is exactly what an unwrap into a strip wants.
//
// So: base is sampled through uv0 and the mult layer through uv1, and they multiply. The
// iamod layer, a third texture blended by its own alpha, is not applied - the two-layer
// product is the part that carries the look.
//
// XR. Somnium renders the headset single-pass instanced (OpenXR m_renderMode 1; the client log
// says "Render Mode: Single Pass Instanced", Direct3D11): every draw is issued twice, instance
// bit 0 picks the eye, and the eye has to be written out as SV_RenderTargetArrayIndex or both
// copies land in the left eye's slice and the right eye gets nothing. That is what
// UNITY_VERTEX_OUTPUT_STEREO / UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO do, exactly as URP's own
// SimpleLitForwardPass.hlsl and DepthOnlyPass.hlsl do it; UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX
// gives the fragment the same eye (camera position, screen-space shadow slice). In a desktop
// session all of these macros compile to nothing, so the flat-screen path is unchanged.
// The depth passes matter as much as the colour pass: the client's renderer forces depth
// priming (URP_Renderer.asset m_DepthPrimingMode: 2), so opaque colour is drawn with
// ZTest Equal against what DepthOnly (or DepthNormals) wrote - a depth pass that fills only
// the left eye leaves this surface missing from the right eye.
Shader "SomniumEuroTruck/Multi"
{
    Properties
    {
        _BaseMap ("Base (uv0)", 2D) = "white" {}
        _MultMap ("Multiplied over it (uv1)", 2D) = "white" {}
        _BaseColor ("Colour", Color) = (1, 1, 1, 1)
        _Metallic ("Metallic", Range(0, 1)) = 0
        _Smoothness ("Smoothness", Range(0, 1)) = 0.2
        // How much of the second layer to lay on. One is the game's own multiply.
        _MultStrength ("Second layer strength", Range(0, 1)) = 1
        _Cutoff ("Alpha cutoff", Range(0, 1)) = 0.4
        _AlphaClipOn ("Clip by alpha", Float) = 0
        // 0 multiplies the second layer on, 1 blends it by its alpha, 2 by the inverse,
        // 3 and 4 by vertex alpha (terrain pairs, and the prefab ground's weight.weight effect;
        // 4 also draws a step later with a depth bias, being co-planar with other slabs).
        _LayerMode ("Second layer mode", Float) = 0
        // Mode 5 (amod): the game's decal mask (texture_mask, second uv set, red) and its
        // amod_decal_blending_factors (x below vertex alpha 0.5, y above). EtsMaterialCache looks for
        // _AmodMask to know this shader has modes 5 and 6 (6 = multiply by the second layer's red: oclu).
        _AmodMask ("amod decal mask (uv1, red)", 2D) = "white" {}
        _AmodFactors ("amod decal blending factors", Vector) = (1, 1, 0, 0)
        // Polygon offset for surfaces authored on top of another in the same plane.
        _DepthBias ("Depth bias", Float) = 0
        // Face culling (URP Lit's own property name): the material cache sets 0 (off) on cut-outs, as it does on Lit,
        // so a fence or a grille read from both sides stays two-sided when it is drawn here (the per-vertex
        // colour twins of Lit materials, EtsMaterialCache.WithVertexAo). Every pass culls alike (depth priming).
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 2
        // The switch for _ENVIRONMENTREFLECTIONS_OFF, declared as URP's Lit declares it: the material
        // cache turns the keyword on only for shaders that have this property. Not read in HLSL, so
        // it stays out of UnityPerMaterial.
        [ToggleOff] _EnvironmentReflections ("Environment reflections", Float) = 1
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "Forward"
            Tags { "LightMode" = "UniversalForward" }
            Cull [_Cull]
            Offset [_DepthBias], [_DepthBias]

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BOX_PROJECTION
            #pragma multi_compile_fog
            #pragma multi_compile_instancing

            // EtsMaterialCache sets this on every material whose effect has no environment term;
            // without the declaration it did nothing and matte metal halls mirrored the sky. A
            // multi_compile rather than a shader_feature: every material here is made at runtime,
            // so a shader_feature variant would be stripped from the bundle as unused.
            #pragma multi_compile_local_fragment _ _ENVIRONMENTREFLECTIONS_OFF

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);
            TEXTURE2D(_MultMap);
            SAMPLER(sampler_MultMap);
            TEXTURE2D(_AmodMask);
            SAMPLER(sampler_AmodMask);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                float4 _MultMap_ST;
                half4 _BaseColor;
                half _Metallic;
                half _Smoothness;
                half _MultStrength;
                half _Cutoff;
                half _AlphaClipOn;
                half _LayerMode;
                half4 _AmodFactors;
            CBUFFER_END

            // World globals from EtsClimate.Apply (outside the material buffer, so the SRP batcher is unaffected; all
            // zero until the climate is applied, which turns both off).
            // _EtsAmbientNear: the game's near-field ambient (sun pass 6d0cdbb303e85feb8393a9f6504143fd.glsl.fso),
            //   x 0.6 near, y 1 / 150^2, z the grazing power (5), w on. Ambient x sat(near + (1 - near) sat(d^2 / 150^2))
            //   lifted toward 1 by (1 - n.v)^5 - the same law EtsDecal and EtsLeaves use (the ambient contract).
            // _EtsFogColor1/2, _EtsFogSunDir: the game's fog colour, lerp(fog_color, fog_color2, sat(dot(view, toSun))^2)
            //   (fog pass 49f94099c3c3639c12cc48552413d810.glsl.fso), linear x exposure; _EtsFogColor2.w on.
            float4 _EtsAmbientNear;
            float4 _EtsFogColor1;
            float4 _EtsFogColor2;
            float4 _EtsFogSunDir;

            half EtsNearAmbient(float3 positionWS, half3 normalWS, half3 viewDirWS)
            {
                float3 toCamera = GetCameraPositionWS() - positionWS;
                half nearAmbient = (half)_EtsAmbientNear.x;
                half distanceAmbient = nearAmbient + (1.0h - nearAmbient) * (half)saturate(dot(toCamera, toCamera) * _EtsAmbientNear.y);
                half grazing = 1.0h - saturate(dot(normalWS, viewDirWS));
                half grazing2 = grazing * grazing;
                half factor = saturate(distanceAmbient + (1.0h - distanceAmbient) * grazing2 * grazing2 * grazing);
                return lerp(1.0h, factor, (half)_EtsAmbientNear.w);
            }

            half3 EtsFogColour(float3 positionWS)
            {
                float3 view = normalize(positionWS - GetCameraPositionWS());
                half t = (half)saturate(dot(view, _EtsFogSunDir.xyz));
                half3 directional = lerp((half3)_EtsFogColor1.rgb, (half3)_EtsFogColor2.rgb, t * t);
                return _EtsFogColor2.w > 0.5 ? directional : (half3)unity_FogColor.rgb;
            }

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
                float2 uv2 : TEXCOORD1;
                half4 color : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float2 uv2 : TEXCOORD1;
                float3 positionWS : TEXCOORD2;
                float3 normalWS : TEXCOORD3;
                float fogFactor : TEXCOORD4;
                half4 color : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings Vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                VertexPositionInputs position = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = position.positionCS;
                output.positionWS = position.positionWS;
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                output.uv2 = TRANSFORM_TEX(input.uv2, _MultMap);
                output.color = input.color;
                output.fogFactor = ComputeFogFactor(position.positionCS.z);
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                half4 baseTexel = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv) * _BaseColor;
                half4 over = SAMPLE_TEXTURE2D(_MultMap, sampler_MultMap, input.uv2);

                // How the second layer joins the first is what the effect name says it is.
                // "mult" multiplies it on - a grime and weathering pass over clean concrete.
                // "amod" lays it over, masked by its own alpha; "iamod" by the inverse of it.
                // At strength zero nothing changes, so a material with no second texture is
                // drawn exactly as the single-texture path drew it.
                half3 albedo;

                if (_LayerMode < 0.5h)
                {
                    albedo = baseTexel.rgb * lerp(half3(1, 1, 1), over.rgb, _MultStrength);
                }
                else if (_LayerMode < 1.5h)
                {
                    albedo = lerp(baseTexel.rgb, over.rgb, saturate(over.a * _MultStrength));
                }
                else if (_LayerMode < 2.5h)
                {
                    albedo = lerp(baseTexel.rgb, over.rgb, saturate((1.0h - over.a) * _MultStrength));
                }
                else if (_LayerMode < 3.5h)
                {
                    // Terrain pairs: two brushes, and how far the second covers each quad is painted
                    // into the vertices by the terrain builder - alpha is the second brush's cover.
                    albedo = lerp(baseTexel.rgb, over.rgb, saturate(input.color.a * _MultStrength));
                }
                else if (_LayerMode < 4.5h)
                {
                    // Prefab ground, eut2.dif.spec.weight.weight.dif.spec.weight: texture_base and
                    // texture_over mixed by the vertex alpha the model file carries, alpha 1 = over.
                    // That is SCS's own definition - their Blender tools' shader for this effect
                    // wires "Vertex Color Alpha" to a MIX node's factor with base as colour 1 and
                    // over as colour 2, and removes the opacity node: the surface is opaque. (An
                    // earlier reading from the DomDepo yard's rim alphas had it the other way.)
                    albedo = lerp(baseTexel.rgb, over.rgb, saturate(input.color.a));
                }
                else if (_LayerMode < 5.5h)
                {
                    // amod, as the game's own program draws it (fwdopaque of
                    // eut2.dif.spec.amod.dif.spec.tsnmapuv): f = vertex alpha * 2 - 1, scaled by
                    // amod_decal_blending_factors (.y above 0.5, .x below); strength = |f| * mask.r, the
                    // mask (texture_mask) on the same second uv set as the layer; the layer is
                    // base * over above 0.5 and over itself below. Vertex alpha 0.5 = no decal.
                    half f = input.color.a * 2.0h - 1.0h;
                    half factor = f > 0.0h ? _AmodFactors.y : _AmodFactors.x;
                    half strength = saturate(abs(f * factor) * SAMPLE_TEXTURE2D(_AmodMask, sampler_AmodMask, input.uv2).r);
                    half3 layer = f > 0.0h ? baseTexel.rgb * over.rgb : over.rgb;
                    albedo = lerp(baseTexel.rgb, layer, strength);
                }
                else
                {
                    // oclu: base x texture_oclu.x through the second uv set (fwdopaque of
                    // eut2.dif.spec.oclu.*). The map is BC4, which samples as (r, 0, 0, 1): red only.
                    albedo = baseTexel.rgb * lerp(1.0h, over.r, _MultStrength);
                }

                // The game multiplies diffuse by vertex colour x 2 in every dif / dif.spec program
                // (/effect/eut2/_shd/30a5626a....glsl.fso: tex * diffuse * cg_FrontColor.xyz * 2), so
                // 128 is x1 and a mesh without colours reads white = x1. Not mode 3: the terrain
                // pairs' vertex colour is the builder's 255,255,255,cover, not the game's.
                if (_LayerMode < 2.5h || _LayerMode > 3.5h)
                {
                    albedo *= saturate(input.color.rgb * 2.0h);
                }

                if (_AlphaClipOn > 0.5)
                {
                    clip(baseTexel.a - _Cutoff);
                }

                InputData inputData = (InputData)0;
                inputData.positionWS = input.positionWS;
                inputData.normalWS = normalize(input.normalWS);
                inputData.viewDirectionWS = GetWorldSpaceNormalizeViewDir(input.positionWS);
                inputData.shadowCoord = TransformWorldToShadowCoord(input.positionWS);
                inputData.fogCoord = input.fogFactor;
                inputData.bakedGI = SampleSH(inputData.normalWS) * EtsNearAmbient(input.positionWS, inputData.normalWS, inputData.viewDirectionWS);
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);
                inputData.shadowMask = half4(1, 1, 1, 1);

                SurfaceData surface = (SurfaceData)0;
                surface.albedo = albedo;
                surface.metallic = _Metallic;
                surface.smoothness = _Smoothness;
                surface.occlusion = 1.0h;
                surface.alpha = 1.0h;

                half4 colour = UniversalFragmentPBR(inputData, surface);
                colour.rgb = MixFogColor(colour.rgb, EtsFogColour(input.positionWS), input.fogFactor);
                colour.a = 1.0h;
                return colour;
            }
            ENDHLSL
        }

        // Shadow maps are not per eye: URP's own ShadowCasterPass.hlsl carries only the instance
        // id, no stereo output, and so does this one.
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            Cull [_Cull]

            ZWrite On
            ZTest LEqual
            ColorMask 0

            HLSLPROGRAM
            #pragma vertex ShadowVert
            #pragma fragment ShadowFrag
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                float4 _MultMap_ST;
                half4 _BaseColor;
                half _Metallic;
                half _Smoothness;
                half _MultStrength;
                half _Cutoff;
                half _AlphaClipOn;
                half _LayerMode;
                half4 _AmodFactors;
            CBUFFER_END

            float3 _LightDirection;

            struct ShadowAttributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct ShadowVaryings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            ShadowVaryings ShadowVert(ShadowAttributes input)
            {
                ShadowVaryings output = (ShadowVaryings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);

                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                float3 normalWS = TransformObjectToWorldNormal(input.normalOS);
                float4 positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, _LightDirection));

                #if UNITY_REVERSED_Z
                    positionCS.z = min(positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #else
                    positionCS.z = max(positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #endif

                output.positionCS = positionCS;
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                return output;
            }

            half4 ShadowFrag(ShadowVaryings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);

                if (_AlphaClipOn > 0.5)
                {
                    clip(SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv).a - _Cutoff);
                }

                return 0;
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            Cull [_Cull]

            ZWrite On
            ColorMask 0
            Offset [_DepthBias], [_DepthBias]

            HLSLPROGRAM
            #pragma vertex DepthVert
            #pragma fragment DepthFrag
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                float4 _MultMap_ST;
                half4 _BaseColor;
                half _Metallic;
                half _Smoothness;
                half _MultStrength;
                half _Cutoff;
                half _AlphaClipOn;
                half _LayerMode;
                half4 _AmodFactors;
            CBUFFER_END

            struct DepthAttributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct DepthVaryings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            DepthVaryings DepthVert(DepthAttributes input)
            {
                DepthVaryings output = (DepthVaryings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                return output;
            }

            half4 DepthFrag(DepthVaryings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                if (_AlphaClipOn > 0.5)
                {
                    clip(SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv).a - _Cutoff);
                }

                return 0;
            }
            ENDHLSL
        }

        // The depth prepass URP runs instead of DepthOnly when something asks for the camera normals
        // texture (SSAO, DBuffer decals). The client's renderer asks for none of that today (HBAO off,
        // decals in Screen Space mode), but with depth priming forced this pass would then be the one
        // the colour pass is Equal-tested against, so it carries the same depth bias and the same
        // alpha clip as Forward and DepthOnly, and the same stereo output. Pattern: URP 17.3
        // Shaders/DepthNormalsPass.hlsl and Lit.shader's DepthNormals pass.
        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }
            Cull [_Cull]

            ZWrite On
            Offset [_DepthBias], [_DepthBias]

            HLSLPROGRAM
            #pragma vertex DepthNormalsVert
            #pragma fragment DepthNormalsFrag
            #pragma multi_compile_instancing
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/RenderingLayers.hlsl"

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                float4 _MultMap_ST;
                half4 _BaseColor;
                half _Metallic;
                half _Smoothness;
                half _MultStrength;
                half _Cutoff;
                half _AlphaClipOn;
                half _LayerMode;
                half4 _AmodFactors;
            CBUFFER_END

            struct DepthNormalsAttributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct DepthNormalsVaryings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            DepthNormalsVaryings DepthNormalsVert(DepthNormalsAttributes input)
            {
                DepthNormalsVaryings output = (DepthNormalsVaryings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                output.normalWS = NormalizeNormalPerVertex(TransformObjectToWorldNormal(input.normalOS));
                return output;
            }

            void DepthNormalsFrag(DepthNormalsVaryings input
                , out half4 outNormalWS : SV_Target0
            #ifdef _WRITE_RENDERING_LAYERS
                , out uint outRenderingLayers : SV_Target1
            #endif
                )
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                if (_AlphaClipOn > 0.5)
                {
                    clip(SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv).a - _Cutoff);
                }

                outNormalWS = half4(NormalizeNormalPerPixel(input.normalWS), 0.0);

                #ifdef _WRITE_RENDERING_LAYERS
                    outRenderingLayers = EncodeMeshRenderingLayer();
                #endif
            }
            ENDHLSL
        }
    }

    Fallback "Universal Render Pipeline/Lit"
}
