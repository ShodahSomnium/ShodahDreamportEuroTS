// A terrain brush's heightmap turned into a normal map, once per brush with Graphics.Blit (owner F;
// environment DESIGN.md Phase E, PLAN.md §6 "New files"; Phase E is optional and later).
//
// PHASE 0 STUB. The name exists so the file and its GUID are there from the first day; the one pass writes
// a flat normal (0.5, 0.5 in RG, i.e. (0, 0, 1)) whatever the input, and nothing uses it yet. F replaces
// this file in Phase E (keeping its GUID) with the [env] Phase E derivative of the height.
Shader "SomniumEuroTruck/HeightToNormal"
{
    Properties
    {
        _MainTex ("Height (the brush's heightmap)", 2D) = "black" {}
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }
        Cull Off
        ZWrite Off
        ZTest Always

        Pass
        {
            Name "Phase0Stub"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.uv = input.uv;
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                return half4(0.5, 0.5, 1.0, 1.0);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
