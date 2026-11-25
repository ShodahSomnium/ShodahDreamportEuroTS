// Ground decal for the Euro Truck world: lane lines, dirt, tyre marks, shoulder edges.
// The game's "decal.over" effect multiplies the texture alpha by the vertex colour alpha
// (a dirt patch fades to nothing at its edges through the vertices), so this shader does the
// same. URP Lit ignores vertex colour, which is why it is not used for these pieces.
// Lit by the main light with shadows and ambient; blended over the surface below with a
// small depth offset so it never fights the asphalt it lies on.
//
// XR. In the headset Somnium renders single-pass instanced (Direct3D11): each draw is issued
// twice, the instance id's low bit is the eye, and the eye must be written as
// SV_RenderTargetArrayIndex. Without the instance id and the stereo output below, both copies
// were drawn with the left eye's matrices into the left eye's slice - lane lines, dirt and the
// route ribbon (EtsRouteLine uses this shader) twice as strong in the left eye and absent from
// the right. The macros are URP's own (UnlitForwardPass.hlsl does exactly this) and compile to
// nothing outside XR, so the desktop picture is unchanged.
Shader "SomniumEuroTruck/Decal"
{
    Properties
    {
        _BaseMap ("Base", 2D) = "white" {}
        _BaseColor ("Colour", Color) = (1, 1, 1, 1)
        // The game stores vertex colour so that 128 means one; alpha is read the same way.
        _VertexAlphaScale ("Vertex alpha scale", Float) = 2
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent-100" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }

        Pass
        {
            Name "Forward"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Off
            Offset -1, -1

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fog
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                half4 _BaseColor;
                half _VertexAlphaScale;
            CBUFFER_END

            // World globals from EtsClimate.Apply, as in EtsMulti.shader (zero until the climate is applied: both off):
            // the game's near-field ambient factor and its fog colour by the view's angle to the sun.
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
                half4 color : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                float3 normalWS : TEXCOORD2;
                half4 color : COLOR;
                float fogFactor : TEXCOORD3;
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
                output.color = input.color;
                output.fogFactor = ComputeFogFactor(position.positionCS.z);
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                half4 texel = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv) * _BaseColor;

                // The game's vertex colour is a multiplier with 128 meaning one: lane lines carry
                // 128 (neutral), the junction dirt wash carries about 10, so it darkens instead of
                // lightening the asphalt under it.
                texel.rgb *= saturate(input.color.rgb * _VertexAlphaScale);

                float3 normalWS = normalize(input.normalWS);
                float4 shadowCoord = TransformWorldToShadowCoord(input.positionWS);
                Light mainLight = GetMainLight(shadowCoord);
                half lambert = saturate(dot(normalWS, mainLight.direction));
                half3 ambient = SampleSH(normalWS) * EtsNearAmbient(input.positionWS, normalWS, GetWorldSpaceNormalizeViewDir(input.positionWS));
                half3 lighting = mainLight.color * mainLight.shadowAttenuation * lambert + ambient;

                half alpha = texel.a * saturate(input.color.a * _VertexAlphaScale);
                half3 colour = MixFogColor(texel.rgb * lighting, EtsFogColour(input.positionWS), input.fogFactor);
                return half4(colour, alpha);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
