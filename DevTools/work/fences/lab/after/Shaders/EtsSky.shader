// The game's own sky for the Euro Truck world (see Ets/EtsSky.cs).
//
// _SkyTex and _SkyTexB are the weather slot's skybox_texture of the two sun profiles either side of the
// sun, R9G9B9E5 HDR panoramas of the UPPER hemisphere: row 0 the zenith, the last row the horizon, the
// sun's glow in the middle column (measured, harness Program969). They are crossfaded by _SkyBlend as
// the game's eut2.sky does (/effect/eut2/_shd/05b00a9aa2e6201b0d0a525f7f96e02d.glsl.fso) and scaled by
// sky_color x the climate's exposure. Below the horizon: the panorama's last row (the game's dome
// stretches it to -15 degrees) under the "bottom_ring" fading sky_bottom_color in (alpha 0 at the
// horizon, 0.52 at -1.86 degrees, 1 from -5.35: /asset/skybox/skybox.pmd piece 1). The disc is
// sun_color x exposure (HDR), the additive halo sun_halo_color x exposure x its texture (0.82 (1 - r)^2.25
// over 4.2 degrees).
//
// XR: single-pass instanced like everything else in Somnium's headset path - instance id in,
// stereo output initialised, eye index set up in the fragment (URP's own skybox shaders do the
// same). Compiles to nothing extra outside XR. Cost: two panorama samples a sky pixel (one before).
Shader "SomniumEuroTruck/Sky"
{
    Properties
    {
        _SkyTex ("Game sky panorama, lower profile (HDR)", 2D) = "black" {}
        _SkyTexB ("Game sky panorama, upper profile (HDR)", 2D) = "black" {}
        _SkyBlend ("Profile blend (0 lower, 1 upper)", Range(0, 1)) = 0
        _SkyScale ("sky_color x exposure", Float) = 2.2
        _BelowLinear ("Below the horizon: sky_bottom_color x exposure (linear)", Vector) = (0.076, 0.148, 0.277, 1)
        _SunDirection ("Direction to the sun", Vector) = (0, 0.766, 0.643, 0)
        _SunLinear ("Sun disc: sun_color x exposure (linear)", Vector) = (4.4, 4.0, 3.4, 0)
        _HaloLinear ("Halo: sun_halo_color x exposure x texture (linear)", Vector) = (0.5, 0.33, 0.17, 0)
        _SunDisc ("Sun disc intensity", Float) = 1
    }

    SubShader
    {
        Tags { "Queue" = "Background" "RenderType" = "Background" "PreviewType" = "Skybox" "RenderPipeline" = "UniversalPipeline" }
        Cull Off
        ZWrite Off

        Pass
        {
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_SkyTex);
            SAMPLER(sampler_SkyTex);
            TEXTURE2D(_SkyTexB);

            CBUFFER_START(UnityPerMaterial)
                float4 _SkyTex_ST;
                float4 _SkyTexB_ST;
                float _SkyBlend;
                float _SkyScale;
                float4 _BelowLinear;
                float4 _SunDirection;
                float4 _SunLinear;
                float4 _HaloLinear;
                float _SunDisc;
            CBUFFER_END

            // EtsClimate.ScaleLights: 1 - the light scale of the camera drawing (the old exposure's level for the full-view
            // phases and the offscreen pictures without a tone curve - mirrors, the route map); 0 (unset) in the world.
            float _EtsLookDim;

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 direction : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings Vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.direction = input.positionOS.xyz;
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float3 d = normalize(input.direction);
                float3 sun = normalize(_SunDirection.xyz);

                // v: elevation 90 -> 0 over the texture (clamped below: the last row); u: 0.5 at the sun's azimuth.
                float elevation = asin(saturate(d.y));
                float azimuth = atan2(d.x, d.z) - atan2(sun.x, sun.z);
                float2 uv = float2(frac(azimuth / (2.0 * PI) + 0.5), 1.0 - elevation / (0.5 * PI));
                float3 a = SAMPLE_TEXTURE2D_LOD(_SkyTex, sampler_SkyTex, uv, 0).rgb;
                float3 b = SAMPLE_TEXTURE2D_LOD(_SkyTexB, sampler_SkyTex, uv, 0).rgb;
                float3 colour = lerp(a, b, _SkyBlend) * _SkyScale;

                // The bottom ring: sky_bottom_color over the stretched last row, 0 at the horizon, full from -5.35 degrees.
                float x = saturate(-d.y / 0.0932);
                float ramp = 1.0 - pow(1.0 - x, 1.7);
                colour = lerp(colour, _BelowLinear.rgb, ramp);

                // The disc (0.27 degrees across, soft over 0.20-0.33) and the halo (4.2 degrees), both hidden under the ring.
                float c = dot(d, sun);
                float disc = smoothstep(0.9999834, 0.9999939, c);
                float r = sqrt(max(0.0, 2.0 * (1.0 - c))) / 0.0733;
                float halo = pow(saturate(1.0 - r), 2.25);
                colour += (_SunLinear.rgb * (disc * _SunDisc) + _HaloLinear.rgb * halo) * (1.0 - ramp);
                colour *= 1.0 - _EtsLookDim;
                return half4(colour, 1.0);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
