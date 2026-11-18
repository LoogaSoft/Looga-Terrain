// Mist and spray particles at water, for example at the foot of a waterfall. A particle fades out softly in front of
// the ground (camera depth), in front of the soft depth of LoogaWaterReflectionFeature (for example the jets of
// Looga/Water fall sheets) and just above the water level. The water and the jets write no hard depth cut into a large
// particle that reaches into the water or behind a jet. The mist scatters the main light like a cloud (more forward
// than back) and takes the mean sky light, and the
// particle system colors tint it.
// With _WATER_EFFECTS on, LoogaWaterEffects draws the particles without a particle system. The vertex stage then
// calculates each particle from its emitter, its number and the world clock _LoogaWaterEffectTime. The particles have
// no state, so they cannot restart, and they are at their steady state at all times.
Shader "Looga/Water Mist"
{
    Properties
    {
        _BaseMap("Base Map", 2D) = "white" {}
        [HDR] _BaseColor("Base Color", Color) = (1, 1, 1, 1)
        // Light that the mist adds to its lit color.
        [HDR] _EmissionColor("Emission Color", Color) = (0, 0, 0, 1)
        // The mist fades out over this distance in front of the ground, in meters.
        _SoftDistance("Soft Distance (m)", Float) = 6
        // The mist fades out over this distance in front of the soft depth, such as waterfall jets, in meters.
        _SoftDepthDistance("Soft Depth Distance (m)", Float) = 3
        // The mist fades out near the camera between these distances, in meters.
        _CameraFadeNear("Camera Fade Near (m)", Float) = 1
        _CameraFadeFar("Camera Fade Far (m)", Float) = 4
        // World height of the water surface, and the height above it over which the mist fades in, in meters.
        _WaterLevel("Water Level (m)", Float) = 0
        _WaterFade("Water Fade (m)", Float) = 2.5
        // Share of the main light that the mist scatters toward the camera, on average over all directions.
        _SunScatter("Sun Scatter", Range(0, 2)) = 0.6
        // Forward scattering: 0 scatters the sun light equally to all sides; higher values make mist in front of the
        // sun brighter and mist with the sun behind the camera darker.
        _ForwardScatter("Forward Scatter", Range(0, 0.9)) = 0.35
    }

    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Transparent" "Queue"="Transparent" "IgnoreProjector"="True" "PreviewType"="Plane" }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode"="UniversalForward" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex MistVertex
            #pragma fragment MistFragment
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fog
            #pragma multi_compile_local _ _WATER_EFFECTS

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);
            // Set by LoogaWaterReflectionFeature: the soft depth and 1 while it is valid for the camera.
            TEXTURE2D(_LoogaSoftDepth);
            float _LoogaSoftDepthOn;
            // Set by LoogaWaterReflectionFeature with Surface Depth: the camera depth texture then holds the water
            // surfaces, and this texture the opaque depth (1 while it is valid). The ground fade uses the opaque
            // depth: water sheets fade the mist only over the soft depth distance.
            TEXTURE2D_X_FLOAT(_LoogaOpaqueDepth);
            float _LoogaOpaqueDepthOn;

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                half4 _BaseColor;
                half4 _EmissionColor;
                float _SoftDistance;
                float _SoftDepthDistance;
                float _CameraFadeNear;
                float _CameraFadeFar;
                float _WaterLevel;
                float _WaterFade;
                half _SunScatter;
                half _ForwardScatter;
            CBUFFER_END

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float2 uv : TEXCOORD2;
                float fogFactor : TEXCOORD3;
                // Water level (x), the height above it over which the mist fades in (y) and the ground soft
                // distance (z).
                float3 water : TEXCOORD4;
                // Sun shadow of the whole particle, 0 to 1, or -1 when the fragment samples the shadow.
                float shadow : TEXCOORD5;
                half4 color : COLOR;
            };

        #if defined(_WATER_EFFECTS)
            // Same layout as WaterEffectEmitterData in LoogaWaterEffects.
            struct WaterEffectEmitter
            {
                float4 centre;
                float4 across;
                float4 forward;
                float4 velocityMin;
                float4 velocityMax;
                float4 life;
                float4 shape;
                float4 color;
                float4 fade;
                float4 extra;
            };

            // The clock wraps after this time, in seconds (ClockPeriod in LoogaWaterEffects).
            #define WATER_EFFECT_PERIOD 1200.0

            StructuredBuffer<WaterEffectEmitter> _WaterEffectEmitters;
            // Per particle: emitter index in the high 16 bits, particle number in the emitter in the low 16 bits.
            StructuredBuffer<uint> _WaterEffectParticles;
            float _LoogaWaterEffectTime;

            struct Attributes
            {
                uint vertexID : SV_VertexID;
            };

            uint WaterEffectHash(uint value)
            {
                uint state = value * 747796405u + 2891336453u;
                uint word = ((state >> ((state >> 28u) + 4u)) ^ state) * 277803737u;
                return (word >> 22u) ^ word;
            }

            float WaterEffectRandom(inout uint state)
            {
                state = WaterEffectHash(state);
                return state * (1.0 / 4294967296.0);
            }

            // Opacity over the life: 0 at birth, 1 at 25 %, 0.7 at 65 % and 0 at death.
            float WaterEffectAlpha(float progress)
            {
                if (progress < 0.25) return progress / 0.25;
                if (progress < 0.65) return lerp(1.0, 0.7, (progress - 0.25) / 0.4);
                return lerp(0.7, 0.0, (progress - 0.65) / 0.35);
            }

            // Sun shadow of a whole particle: the mean of samples over its area, in the vertex stage. A shadow per
            // pixel cut the flat, camera-facing quads along straight lines where the edge of a shadow, for example of
            // a canyon rim, crossed the mist. -1 with screen space shadows, which have no lookup by world position.
            float MistParticleShadow(float3 centre, float size)
            {
            #if defined(_MAIN_LIGHT_SHADOWS_SCREEN)
                return -1;
            #else
                static const float2 taps[9] =
                {
                    float2(0, 0), float2(0.6, 0), float2(-0.6, 0), float2(0, 0.6), float2(0, -0.6),
                    float2(0.42, 0.42), float2(-0.42, 0.42), float2(0.42, -0.42), float2(-0.42, -0.42)
                };
                float shadow = 0;
                [unroll] for (int index = 0; index < 9; index++)
                {
                    float3 position = centre + (UNITY_MATRIX_V[0].xyz * taps[index].x +
                        UNITY_MATRIX_V[1].xyz * taps[index].y) * (0.5 * size);
                    shadow += GetMainLight(TransformWorldToShadowCoord(position), position, half4(1, 1, 1, 1))
                        .shadowAttenuation;
                }
                return shadow / 9;
            #endif
            }

            Varyings MistVertex(Attributes input)
            {
                Varyings output = (Varyings)0;
                static const float2 corners[6] =
                {
                    float2(-1, -1), float2(1, -1), float2(1, 1), float2(-1, -1), float2(1, 1), float2(-1, 1)
                };
                uint particle = input.vertexID / 6u;
                float2 corner = corners[input.vertexID % 6u];
                uint packed = _WaterEffectParticles[particle];
                uint emitterIndex = packed >> 16;
                uint number = packed & 0xFFFFu;
                WaterEffectEmitter emitter = _WaterEffectEmitters[emitterIndex];

                // Distance LOD for this camera: the emitter keeps fewer particles, which get more opacity, then fades
                // out. A particle fades as its rank leaves the kept share, so no particle pops.
                float distance = length(GetCameraPositionWS() - emitter.centre.xyz);
                float keep = lerp(1.0, emitter.fade.w, smoothstep(emitter.fade.x, emitter.fade.z, distance));
                float rank = frac(number * 0.6180339887 + emitterIndex * 0.3819660113);
                float weight = (1.0 - smoothstep(emitter.fade.y, emitter.fade.z, distance)) *
                    saturate((keep - rank) / 0.1) * min(1.0 / keep, 2.0);

                // The life of the particle fits a whole number of times into the clock period. Each life is a new
                // cycle with new random values, and the cycle number wraps with the clock.
                uint state = (uint)emitter.extra.x * 65537u + number;
                float life = lerp(emitter.life.x, emitter.life.y, WaterEffectRandom(state));
                float cycles = max(1.0, round(WATER_EFFECT_PERIOD / life));
                life = WATER_EFFECT_PERIOD / cycles;
                float position = _LoogaWaterEffectTime / life + WaterEffectRandom(state);
                float cycle = floor(position);
                float progress = position - cycle;
                state = WaterEffectHash(state ^ WaterEffectHash((uint)fmod(cycle, cycles) + 1013904223u));
                float age = progress * life;

                float3 up = float3(0, 1, 0);
                float3 across = emitter.across.xyz;
                float3 forward = emitter.forward.xyz;
                float3 spawn = emitter.centre.xyz +
                    across * ((WaterEffectRandom(state) - 0.5) * emitter.across.w) +
                    up * ((WaterEffectRandom(state) - 0.5) * emitter.velocityMin.w) +
                    forward * ((WaterEffectRandom(state) - 0.5) * emitter.forward.w);
                float3 random = float3(WaterEffectRandom(state), WaterEffectRandom(state), WaterEffectRandom(state));
                float3 local = lerp(emitter.velocityMin.xyz, emitter.velocityMax.xyz, random);
                float3 velocity = across * local.x + up * local.y + forward * local.z;
                // Drift: a slow random velocity and a sway, like the noise of a particle system.
                float3 drift = (float3(WaterEffectRandom(state), WaterEffectRandom(state),
                    WaterEffectRandom(state)) * 2.0 - 1.0) * float3(0.6, 0.3, 0.6) * emitter.shape.z;
                float sway = WaterEffectRandom(state) * 6.2831853;
                float3 swayOffset = emitter.shape.z * float3(sin(age * 0.8 + sway) - sin(sway), 0,
                    cos(age * 0.7 + sway) - cos(sway));
                float3 centre = spawn + (velocity + drift) * age - up * (0.5 * emitter.velocityMax.w * age * age) +
                    swayOffset;
                float size = lerp(emitter.life.z, emitter.life.w, WaterEffectRandom(state)) *
                    lerp(emitter.shape.x, emitter.shape.y, progress);
                float angle = WaterEffectRandom(state) * 6.2831853 +
                    (WaterEffectRandom(state) * 2.0 - 1.0) * emitter.shape.w * age;

                half4 color = half4(emitter.color);
                color.a *= WaterEffectAlpha(progress) * weight;
                if (color.a < 0.002)
                {
                    // A collapsed quad draws nothing.
                    output.positionCS = float4(0, 0, 0, 0);
                    return output;
                }

                // A quad that faces the camera, turned by the particle angle.
                float sine, cosine;
                sincos(angle, sine, cosine);
                float2 offset = float2(corner.x * cosine - corner.y * sine, corner.x * sine + corner.y * cosine) *
                    (0.5 * size);
                output.positionWS = centre + UNITY_MATRIX_V[0].xyz * offset.x + UNITY_MATRIX_V[1].xyz * offset.y;
                output.normalWS = UNITY_MATRIX_V[2].xyz;
                output.positionCS = TransformWorldToHClip(output.positionWS);
                float2 uv = corner * 0.5 + 0.5;
                output.uv = TRANSFORM_TEX(uv, _BaseMap);
                output.fogFactor = ComputeFogFactor(output.positionCS.z);
                // Large particles need wider soft fades, or the ground and the water cut them along a line.
                float soft = max(emitter.extra.z, 1.0);
                float waterFade = emitter.extra.y > 0.0 ? emitter.extra.y : _WaterFade * max(emitter.extra.w, 1.0);
                output.water = float3(emitter.centre.w, waterFade, _SoftDistance * soft);
                output.shadow = MistParticleShadow(centre, size);
                output.color = color;
                return output;
            }
        #else
            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                half4 color : COLOR;
                float2 uv : TEXCOORD0;
            };

            Varyings MistVertex(Attributes input)
            {
                Varyings output;
                output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.positionCS = TransformWorldToHClip(output.positionWS);
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                output.fogFactor = ComputeFogFactor(output.positionCS.z);
                output.water = float3(_WaterLevel, _WaterFade, _SoftDistance);
                output.shadow = -1;
                output.color = input.color;
                return output;
            }
        #endif

            half4 MistFragment(Varyings input, bool frontFace : SV_IsFrontFace) : SV_Target
            {
                half4 albedo = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv) * _BaseColor * input.color;

                // Soft fades: in front of the ground, in front of the soft depth, near the camera and near the water.
                float2 screenUV = GetNormalizedScreenSpaceUV(input.positionCS);
                float eye = LinearEyeDepth(input.positionWS, GetWorldToViewMatrix());
                float rawGround = _LoogaOpaqueDepthOn > 0.5
                    ? LOAD_TEXTURE2D_X(_LoogaOpaqueDepth, uint2(input.positionCS.xy)).r
                    : SampleSceneDepth(screenUV);
                float ground = LinearEyeDepth(rawGround, _ZBufferParams);
                half fade = saturate((ground - eye) / max(input.water.z, 0.01));
                [branch] if (_LoogaSoftDepthOn > 0.5)
                {
                    // Large particles fade over a longer distance, as in front of the ground.
                    float sizeScale = input.water.z / max(_SoftDistance, 0.01);
                    float soft = LinearEyeDepth(LOAD_TEXTURE2D(_LoogaSoftDepth, uint2(input.positionCS.xy)).r,
                        _ZBufferParams);
                    fade *= saturate((soft - eye) / max(_SoftDepthDistance * sizeScale, 0.01));
                }
                fade *= saturate((eye - _CameraFadeNear) / max(_CameraFadeFar - _CameraFadeNear, 0.01));
                fade *= smoothstep(0, max(input.water.y, 0.01), input.positionWS.y - input.water.x);

                // Mist is a medium, not a surface. It scatters the sun to all sides, more forward than back
                // (Henyey-Greenstein phase with a mean of 1), and takes the mean of the sky light from above and from
                // the camera side. A surface light on the camera-facing quad left mist with the sun behind it dark.
                float3 toCamera = normalize(GetCameraPositionWS() - input.positionWS);
                Light light = GetMainLight(TransformWorldToShadowCoord(input.positionWS));
                half g = _ForwardScatter;
                half scatter = dot(-light.direction, toCamera);
                half phase = (1 - g * g) / pow(max(1 + g * g - 2 * g * scatter, 1e-3), 1.5);
                half shadow = input.shadow >= 0 ? input.shadow : light.shadowAttenuation;
                half3 ambient = 0.5 * (SampleSH(half3(0, 1, 0)) + SampleSH(toCamera));
                half3 color = albedo.rgb * (light.color * (_SunScatter * phase * shadow) + ambient) +
                    _EmissionColor.rgb;
                color = MixFog(color, input.fogFactor);
                return half4(color, albedo.a * fade);
            }
            ENDHLSL
        }
    }
}
