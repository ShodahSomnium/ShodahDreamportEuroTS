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
//
// LAMP MODE (2026-10-04, vehicle indicator lamps; _LampMode 1, set by Ets/EtsVehicleLamps): the game's eut2.lamp "defpost"
// glow (/effect/eut2/_shd/505acf8833b01726fe5018c637660f14.glsl.fso), drawn over a lamp lens as an additive overlay
// (Blend One One, from _SrcBlend/_DstBlend). _BaseMap is the lens's texture_mask (sRGB, as the game samples it), the
// overlay mesh's uv is fract(texcoord1) and its vertex colour selects the mask channels this wire lights (1 per
// channel). Glow = _LampColor (the wire's linear colour x the world's exposure) x dot(pow(mask, contrast; alpha x 2.2),
// selector) x (0.125 + 0.875 nz^2), nz = the view-space normal's z, as the game's vertex program computes it; faded by
// the fog like every other surface. Everything else (and every existing material: mode 0, SrcAlpha/OneMinusSrcAlpha,
// Cull Off) is drawn exactly as before.
//
// 2026-10-14 (time of day and weather): mode 2 RAIN (Ets/EtsRain: drops falling through a box round the head, built in the
// vertex program, facing the head's centre - one quad for both eyes), mode 3 NIGHT LIGHT (a street lamp's light pool or the
// game's eut2.lightmap.night: texture x vertex colour x 2 x _LampColor, added), mode 4 GLOW (a street lamp's round glow,
// camera-facing). Modes 0 and 1 are untouched; the new branches are uniform per material.
Shader "SomniumEuroTruck/Decal"
{
    Properties
    {
        _BaseMap ("Base", 2D) = "white" {}
        _BaseColor ("Colour", Color) = (1, 1, 1, 1)
        // The game stores vertex colour so that 128 means one; alpha is read the same way.
        _VertexAlphaScale ("Vertex alpha scale", Float) = 2
        [HideInInspector] _LampMode ("Lamp mode", Float) = 0
        [HideInInspector] _LampColor ("Lamp colour, linear x exposure", Vector) = (0, 0, 0, 0)
        [HideInInspector] _LampContrast ("Lamp contrast", Float) = 1
        [HideInInspector] _SrcBlend ("Src blend", Float) = 5
        [HideInInspector] _DstBlend ("Dst blend", Float) = 10
        [HideInInspector] _Cull ("Cull", Float) = 0
        // 2026-10-27: alpha x vertex alpha x this when above 0 (the game's .over/.add effects other than decal.over: x 1; colour keeps x 2).
        [HideInInspector] _AlphaVertexScale ("Vertex alpha scale for alpha only", Float) = 0
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent-100" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }

        Pass
        {
            Name "Forward"
            Tags { "LightMode" = "UniversalForward" }

            Blend [_SrcBlend] [_DstBlend]
            ZWrite Off
            Cull [_Cull]
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
                half _LampMode;
                float4 _LampColor;
                half _LampContrast;
                half _SrcBlend;
                half _DstBlend;
                half _Cull;
                half _AlphaVertexScale;
            CBUFFER_END

            // World globals from EtsClimate.Apply, as in EtsMulti.shader (zero until the climate is applied: both off):
            // the game's near-field ambient factor and its fog colour by the view's angle to the sun.
            float4 _EtsAmbientNear;
            float4 _EtsFogColor1;
            float4 _EtsFogColor2;
            float4 _EtsFogSunDir;

            // Rain (Ets/EtsRain, 2026-10-14): the box round the head (xyz metres), the head's centre (xyz) and the share of drops
            // shown (w = rain_intensity), the fall velocity (xyz), how far the drops have moved (xyz, wrapped by the box), a drop's width
            // and length (xy), and the cab the rain must not fall into (world to the cab's frame, half extents, w on).
            float4 _EtsRainBox;
            float4 _EtsRainCentre;
            float4 _EtsRainFall;
            float4 _EtsRainDrift;
            float4 _EtsRainSize;
            float4x4 _EtsRainCabFromWorld;
            float4 _EtsRainCabExtent;

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
                half lampFacing : TEXCOORD4;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings Vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                // RAIN MODE (_LampMode 2, Ets/EtsRain): every quad of the mesh is one drop; its position is a seed in the unit box,
                // its uv the corner. The drops fall through a box that follows the head and wraps, so they stay put in the world;
                // each faces the head's centre (one orientation for both eyes) and is stretched along its fall.
                UNITY_BRANCH
                if (_LampMode > 1.5h && _LampMode < 2.5h)
                {
                    float3 box = _EtsRainBox.xyz;
                    float3 centre = _EtsRainCentre.xyz;
                    float3 drift = _EtsRainDrift.xyz;
                    float3 world = centre + (frac((input.positionOS.xyz * box + drift - centre) / box) - 0.5) * box;
                    float3 toHead = centre - world;
                    float3 fall = normalize(_EtsRainFall.xyz);
                    float3 across = normalize(cross(fall, toHead) + float3(1e-4, 0.0, 0.0));
                    float3 local = mul(_EtsRainCabFromWorld, float4(world, 1.0)).xyz;
                    bool inCab = _EtsRainCabExtent.w > 0.5 && all(abs(local) < _EtsRainCabExtent.xyz);
                    bool shown = input.color.a < _EtsRainCentre.w && dot(toHead, toHead) > 0.36 && !inCab;
                    world += across * (input.uv.x - 0.5) * _EtsRainSize.x + fall * (input.uv.y - 0.5) * _EtsRainSize.y;
                    output.positionWS = world;
                    output.positionCS = shown ? TransformWorldToHClip(world) : float4(0.0, 0.0, -2.0, 1.0);
                    output.normalWS = normalize(toHead);
                    output.uv = input.uv;
                    output.color = half4(1.0h, 1.0h, 1.0h, 1.0h);
                    output.fogFactor = ComputeFogFactor(output.positionCS.z);
                    output.lampFacing = 1.0h;
                    return output;
                }

                // GLOW MODE (_LampMode 4, Ets/EtsStreetLamps): a street lamp's glow - the quad's corners all sit on the lamp, its uv
                // is the corner and normal.x the radius; it faces the camera (a round glow: the eye's own camera is fine) and is
                // pulled 0.4 m toward it, out of the lamp head.
                UNITY_BRANCH
                if (_LampMode > 3.5h)
                {
                    float3 lampWS = TransformObjectToWorld(input.positionOS.xyz);
                    float3 toCamera = normalize(GetCameraPositionWS() - lampWS);
                    float3 side = normalize(cross(float3(0.0, 1.0, 0.0), toCamera) + float3(1e-4, 0.0, 0.0));
                    float3 up = cross(toCamera, side);
                    float radius = input.normalOS.x;
                    float3 glowWS = lampWS + toCamera * 0.4 + (side * (input.uv.x - 0.5) + up * (input.uv.y - 0.5)) * 2.0 * radius;
                    output.positionWS = glowWS;
                    output.positionCS = TransformWorldToHClip(glowWS);
                    output.normalWS = toCamera;
                    output.uv = input.uv;
                    output.color = input.color;
                    output.fogFactor = ComputeFogFactor(output.positionCS.z);
                    output.lampFacing = 1.0h;
                    return output;
                }

                VertexPositionInputs position = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = position.positionCS;
                output.positionWS = position.positionWS;
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                output.color = input.color;
                output.fogFactor = ComputeFogFactor(position.positionCS.z);

                // The game's lamp directionality: 0.125 + 0.875 nz^2, nz the normal's z in view space.
                half nz = (half)TransformWorldToViewDir(output.normalWS, true).z;
                output.lampFacing = 0.125h + 0.875h * nz * nz;
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                // Rain (2): rain.tobj, lit by the ambient x rain_additional_ambient (_LampColor), over what is behind it.
                if (_LampMode > 1.5h && _LampMode < 2.5h)
                {
                    half4 drop = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv);
                    half3 lit = drop.rgb * (half3)_LampColor.rgb;
                    return half4(MixFogColor(lit, EtsFogColour(input.positionWS), input.fogFactor), drop.a * (half)_LampColor.a);
                }

                // Night light (3): the game's eut2.lightmap.night pool a street lamp throws (texture x vertex colour, added to the
                // light under it), drawn additively at _LampColor (a light level x exposure) and faded by the fog.
                // Street-lamp glow (4): the sun halo's falloff, (1 - r)^2.25, in the lamp's colour x _LampColor, faded by the fog.
                if (_LampMode > 3.5h)
                {
                    float r = length(input.uv - 0.5) * 2.0;
                    half fall = (half)pow(saturate(1.0 - r), 2.25);
                    half3 glow = input.color.rgb * 2.0h * (half3)_LampColor.rgb * fall;
                    return half4(MixFogColor(glow, half3(0.0h, 0.0h, 0.0h), input.fogFactor), 1.0h);
                }

                if (_LampMode > 2.5h)
                {
                    half4 pool = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv) * input.color * 2.0h;
                    return half4(MixFogColor(pool.rgb * (half3)_LampColor.rgb, half3(0.0h, 0.0h, 0.0h), input.fogFactor), 1.0h);
                }

                if (_LampMode > 0.5h)
                {
                    half4 mask = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv);
                    half contrast = max(_LampContrast, 0.01h);
                    mask = pow(max(mask, 0.0h), half4(contrast, contrast, contrast, contrast * 2.2h));
                    half3 glow = (half3)_LampColor.rgb * dot(mask, input.color) * input.lampFacing;
                    return half4(MixFogColor(glow, half3(0.0h, 0.0h, 0.0h), input.fogFactor), 1.0h);
                }

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

                half alpha = texel.a * saturate(input.color.a * (_AlphaVertexScale > 0.0h ? _AlphaVertexScale : _VertexAlphaScale));
                half3 colour = MixFogColor(texel.rgb * lighting, EtsFogColour(input.positionWS), input.fogFactor);
                return half4(colour, alpha);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
