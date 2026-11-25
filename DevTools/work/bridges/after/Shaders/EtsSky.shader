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
// 2026-10-14 (time of day and weather):
// - BAD WEATHER: _SkyTexC/_SkyTexD are the bad slot's pair (bad.sii), crossfaded by _SkyBlendBad, each pair at its own
//   sky_color x exposure (_SkyScale, _SkyScaleBad), the two mixed by _Weather (0 nice .. 1 bad) - the game blends the
//   whole profiles. Skipped (a uniform branch) while _Weather is 0.
// - NIGHT: the game draws its sky in two passes, eut2.sky.stars (opaque: the stars layer) and eut2.sky over it
//   (/effect/deferred/rs/sky_over_premula.rs: one, one_minus_src_alpha, with the panorama as colour and the slot's
//   skycloud_mask_texture as alpha: /effect/eut2/_shd/05b00a9a...fso), so the stars and the moon show through
//   1 - cloud mask. Here: the panorama + (1 - mask) x (stars + Milky Way + moon). _StarTex holds the star list
//   (/asset/skybox/star_list.txt, 8,786 stars, intensity x colour x 2, square-root coded) and _MilkyTex the slot's
//   stars_texture (stars01.tobj, its third mip), both in the list's celestial frame (_StarRot turns the world into it);
//   scaled by stars_color and starmap_color x exposure. The moon: /asset/skybox/shared/moon.tobj on a 0.152 degree disc
//   (moon.pmd: a 0.18-unit quad on the 34-unit dome) x moon_color x exposure, its halo the sun halo's law over 4.2 degrees x
//   moon_halo_scale in moon_halo_color x exposure. Skipped (a uniform branch) while _NightOn is 0.
// - LIGHTNING: _FlashLinear x the bad pair's cloud mask (the clouds light up), set by the day/night driver for a few
//   frames per strike.
//
// XR: single-pass instanced like everything else in Somnium's headset path - instance id in,
// stereo output initialised, eye index set up in the fragment (URP's own skybox shaders do the
// same). Compiles to nothing extra outside XR. Cost: two panorama samples a sky pixel by a clear day,
// four in changing weather, five more at night.
Shader "SomniumEuroTruck/Sky"
{
    Properties
    {
        _SkyTex ("Game sky panorama, lower profile (HDR)", 2D) = "black" {}
        _SkyTexB ("Game sky panorama, upper profile (HDR)", 2D) = "black" {}
        _SkyTexC ("Bad weather panorama, lower profile (HDR)", 2D) = "black" {}
        _SkyTexD ("Bad weather panorama, upper profile (HDR)", 2D) = "black" {}
        _MaskA ("Cloud mask, lower profile", 2D) = "black" {}
        _MaskB ("Cloud mask, upper profile", 2D) = "black" {}
        _MaskC ("Bad cloud mask, lower profile", 2D) = "white" {}
        _MaskD ("Bad cloud mask, upper profile", 2D) = "white" {}
        _StarTex ("Star list, celestial frame (sqrt coded)", 2D) = "black" {}
        _MilkyTex ("stars_texture, celestial frame (HDR)", 2D) = "black" {}
        _MoonTex ("Moon", 2D) = "black" {}
        _SkyBlend ("Profile blend (0 lower, 1 upper)", Range(0, 1)) = 0
        _SkyBlendBad ("Bad profile blend", Range(0, 1)) = 0
        _Weather ("Weather (0 nice, 1 bad)", Range(0, 1)) = 0
        _SkyScale ("sky_color x exposure", Float) = 2.2
        _SkyScaleBad ("bad sky_color x exposure", Float) = 2.2
        _BelowLinear ("Below the horizon: sky_bottom_color x exposure (linear)", Vector) = (0.076, 0.148, 0.277, 1)
        _SunDirection ("Direction to the sun", Vector) = (0, 0.766, 0.643, 0)
        _SunLinear ("Sun disc: sun_color x exposure (linear)", Vector) = (4.4, 4.0, 3.4, 0)
        _HaloLinear ("Halo: sun_halo_color x exposure x texture (linear)", Vector) = (0.5, 0.33, 0.17, 0)
        _SunDisc ("Sun disc intensity", Float) = 1
        _NightOn ("Stars and moon drawn", Float) = 0
        _StarScale ("stars_color x exposure", Float) = 0
        _StarmapLinear ("starmap_color x exposure", Vector) = (0, 0, 0, 0)
        _MoonDirection ("Direction to the moon", Vector) = (0, -0.766, -0.643, 0)
        _MoonLinear ("moon_color x exposure", Vector) = (0, 0, 0, 0)
        _MoonHaloLinear ("moon_halo_color x exposure x texture", Vector) = (0, 0, 0, 0)
        _MoonHaloRadius ("Moon halo radius (sine)", Float) = 0.18
        _FlashLinear ("Lightning flash (linear)", Vector) = (0, 0, 0, 0)
        _StarRow0 ("World to star frame, row 0", Vector) = (1, 0, 0, 0)
        _StarRow1 ("World to star frame, row 1", Vector) = (0, 1, 0, 0)
        _StarRow2 ("World to star frame, row 2", Vector) = (0, 0, 1, 0)
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
            TEXTURE2D(_SkyTexC);
            TEXTURE2D(_SkyTexD);
            TEXTURE2D(_MaskA);
            SAMPLER(sampler_MaskA);
            TEXTURE2D(_MaskB);
            TEXTURE2D(_MaskC);
            TEXTURE2D(_MaskD);
            TEXTURE2D(_StarTex);
            SAMPLER(sampler_StarTex);
            TEXTURE2D(_MilkyTex);
            TEXTURE2D(_MoonTex);
            SAMPLER(sampler_MoonTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _SkyTex_ST;
                float4 _SkyTexB_ST;
                float4 _SkyTexC_ST;
                float4 _SkyTexD_ST;
                float4 _MaskA_ST;
                float4 _MaskB_ST;
                float4 _MaskC_ST;
                float4 _MaskD_ST;
                float4 _StarTex_ST;
                float4 _MilkyTex_ST;
                float4 _MoonTex_ST;
                float _SkyBlend;
                float _SkyBlendBad;
                float _Weather;
                float _SkyScale;
                float _SkyScaleBad;
                float4 _BelowLinear;
                float4 _SunDirection;
                float4 _SunLinear;
                float4 _HaloLinear;
                float _SunDisc;
                float _NightOn;
                float _StarScale;
                float4 _StarmapLinear;
                float4 _MoonDirection;
                float4 _MoonLinear;
                float4 _MoonHaloLinear;
                float _MoonHaloRadius;
                float4 _FlashLinear;
                float4 _StarRow0;
                float4 _StarRow1;
                float4 _StarRow2;
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

                UNITY_BRANCH
                if (_Weather > 0.0)
                {
                    float3 c = SAMPLE_TEXTURE2D_LOD(_SkyTexC, sampler_SkyTex, uv, 0).rgb;
                    float3 e = SAMPLE_TEXTURE2D_LOD(_SkyTexD, sampler_SkyTex, uv, 0).rgb;
                    colour = lerp(colour, lerp(c, e, _SkyBlendBad) * _SkyScaleBad, _Weather);
                }

                // The bottom ring: sky_bottom_color over the stretched last row, 0 at the horizon, full from -5.35 degrees.
                float x = saturate(-d.y / 0.0932);
                float ramp = 1.0 - pow(1.0 - x, 1.7);

                // The slot's cloud masks: what lies behind the clouds (stars, moon, sun) shows through 1 - mask.
                float mask = lerp(SAMPLE_TEXTURE2D_LOD(_MaskA, sampler_MaskA, uv, 0).r, SAMPLE_TEXTURE2D_LOD(_MaskB, sampler_MaskA, uv, 0).r, _SkyBlend);
                float maskBad = 1.0;

                UNITY_BRANCH
                if (_Weather > 0.0 || _FlashLinear.w > 0.0)
                {
                    maskBad = lerp(SAMPLE_TEXTURE2D_LOD(_MaskC, sampler_MaskA, uv, 0).r, SAMPLE_TEXTURE2D_LOD(_MaskD, sampler_MaskA, uv, 0).r, _SkyBlendBad);
                    mask = lerp(mask, maskBad, _Weather);
                }

                float clear = (1.0 - saturate(mask)) * (1.0 - ramp);

                // Stars, Milky Way and moon, behind the clouds, as the game's two sky passes lay them.
                UNITY_BRANCH
                if (_NightOn > 0.5)
                {
                    float3 s = float3(dot(_StarRow0.xyz, d), dot(_StarRow1.xyz, d), dot(_StarRow2.xyz, d));
                    float ra = atan2(s.x, s.z) + 0.5 * PI;
                    float2 suv = float2(frac(0.536 - ra / (2.0 * PI)), acos(clamp(s.y, -1.0, 1.0)) / PI);
                    float coded = SAMPLE_TEXTURE2D_LOD(_StarTex, sampler_StarTex, suv, 0).r;
                    float3 night = coded * coded * 4.0 * _StarScale;
                    night += SAMPLE_TEXTURE2D_LOD(_MilkyTex, sampler_StarTex, suv, 0).rgb * _StarmapLinear.rgb;

                    // The moon disc and its halo (the halo is additive, over the clouds as its model is).
                    float3 m = normalize(_MoonDirection.xyz);
                    float cm = dot(d, m);
                    float3 right = normalize(cross(float3(0.0, 1.0, 0.0), m) + float3(1e-5, 0.0, 0.0));
                    float3 up = cross(m, right);
                    float2 muv = float2(dot(d, right), dot(d, up)) / 0.00265 * 0.5 + 0.5;

                    if (cm > 0.0 && all(muv > 0.0) && all(muv < 1.0))
                    {
                        half4 moon = SAMPLE_TEXTURE2D_LOD(_MoonTex, sampler_MoonTex, muv, 0);
                        night += moon.rgb * moon.a * _MoonLinear.rgb;
                    }

                    colour += night * clear;
                    float mr = sqrt(max(0.0, 2.0 * (1.0 - cm))) / max(_MoonHaloRadius, 1e-4);
                    colour += _MoonHaloLinear.rgb * pow(saturate(1.0 - mr), 2.25) * (1.0 - ramp);
                }

                // Lightning: the bad slot's clouds lit for a moment.
                colour += _FlashLinear.rgb * (_FlashLinear.w > 0.0 ? maskBad : 0.0) * (1.0 - ramp);

                colour = lerp(colour, _BelowLinear.rgb, ramp);

                // The disc (0.27 degrees across, soft over 0.20-0.33) and the halo (4.2 degrees), both hidden under the ring and the clouds.
                float c0 = dot(d, sun);
                float disc = smoothstep(0.9999834, 0.9999939, c0);
                float r = sqrt(max(0.0, 2.0 * (1.0 - c0))) / 0.0733;
                float halo = pow(saturate(1.0 - r), 2.25);
                colour += (_SunLinear.rgb * (disc * _SunDisc) + _HaloLinear.rgb * halo) * clear;
                colour *= 1.0 - _EtsLookDim;
                return half4(colour, 1.0);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
