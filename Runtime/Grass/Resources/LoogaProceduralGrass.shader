// Draws the blades that LoogaProceduralGrass.compute places. Each blade is a cubic Bezier strip built from the vertex
// ID: 7 row pairs and a tip near the camera, 3 row pairs and a tip farther away. The pass writes the deferred
// G-buffer as a Looga Lite material and marks the pixels as thin leaves. ProceduralGrassRenderer draws it directly.
Shader "Hidden/Looga/Terrain/Procedural Grass"
{
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Opaque" }
        Pass
        {
            Name "GBuffer"
            Tags { "LightMode"="LoogaProceduralGrass" }
            Cull Off
            ZWrite On
            ZTest LEqual
            // Looga material class. URP deferred lighting skips these pixels. Looga Lighting lights them.
            Stencil
            {
                Ref 96
                Comp Always
                Pass Replace
                WriteMask 96
            }

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex GrassVertex
            #pragma fragment GrassFragment
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT

            #define LOOGA_DISABLE_MODEL_REFLECTIONS 1
            #define LOOGA_LITE_MATERIAL 1
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/GBufferOutput.hlsl"
            #include "Packages/com.loogasoft.loogagraphicspro/Includes/LoogaLightingHelpers.hlsl"
            #include "Packages/com.loogasoft.loogagraphicspro/Shaders/Foliage/VegetationWindField.hlsl"
            #include "Packages/com.loogasoft.loogaterrain/Runtime/Shaders/Grass/ProceduralGrassCommon.hlsl"
            #include "Packages/com.loogasoft.loogaterrain/Runtime/Shaders/Grass/ProceduralGrassBlade.hlsl"

            float4 _GrassAmbientProbe[7];

            // Distance in meters over which the normals turn to up and the gloss falls.
            #define GRASS_SMOOTH_DISTANCE 60.0

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 normalWS : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                float4 surface : TEXCOORD2; // Position along the blade, side from -1 to 1, clump value, distance blend.
                nointerpolation uint type : TEXCOORD3;
                float3 bladeNormalWS : TEXCOORD4;
                // Base color of the clump (world color map mix) and the coverage fullness.
                nointerpolation float4 baseColor : TEXCOORD5;
                // Share of the detail across the blade width that the pixels can show: 0 at 1 pixel, 1 from 4 pixels.
                nointerpolation float detail : TEXCOORD6;
                nointerpolation float smoothnessScale : TEXCOORD7;
                float3 tangentWS : TEXCOORD8;
            };

            Varyings GrassVertex(uint vertexID : SV_VertexID, uint instanceID : SV_InstanceID)
            {
                Varyings output = (Varyings)0;
                GrassBlade blade;
                uint vertex;
                // Blades after the last one in the final instance collapse outside the clip volume.
                if (!GrassFindBlade(vertexID, instanceID, blade, vertex))
                {
                    output.positionCS = float4(0, 0, -1, 1);
                    return output;
                }
                uint typeIndex = asuint(blade.data.z) & GRASS_TYPE_MASK;
                GrassType type = _GrassTypes[typeIndex];
                GrassBladeVertex shape = GrassBuildVertex(blade, type, vertex, _Time.y, false);

                output.positionWS = shape.position;
                output.positionCS = GrassClipPosition(shape.position, blade);
                output.normalWS = shape.normal;
                output.detail = shape.detail;
                output.bladeNormalWS = shape.bladeNormal;
                output.tangentWS = shape.tangent;
                output.surface = float4(shape.t, shape.sideSign, blade.data.w,
                    saturate(shape.cameraDistance / GRASS_SMOOTH_DISTANCE));
                output.smoothnessScale = 1.0 + (frac(blade.data.y * 13.7) * 2.0 - 1.0) * type.detail.y;
                output.type = typeIndex;
                float4 mapColor = GrassUnpackColor(asuint(blade.ground.z));
                float share = type.shade.w * mapColor.a;
                output.baseColor = float4(lerp(type.base.rgb, mapColor.rgb * type.normal.w, share), blade.ground.w);
                return output;
            }

            GBufferFragOutput GrassFragment(Varyings input)
            {
                GrassType type = _GrassTypes[input.type];
                float t = saturate(input.surface.x);
                float across = abs(input.surface.y);
                float distanceBlend = input.surface.w;

                // Each clump mixes the two tip colors. The root is dark and the color lightens toward the tip.
                half3 baseColor = input.baseColor.rgb;
                half3 tipColor = baseColor * type.shade.y;
                tipColor = lerp(tipColor, tipColor * type.tint.rgb, saturate(input.surface.z * 0.5 + 0.5));
                half3 albedo = lerp(baseColor * type.shade.x, tipColor, smoothstep(0, 0.85, t));
                half detail = input.detail;
                // Thin blade smoothing: a blade only one to four pixels wide reaches the pixels only through temporal
                // antialiasing, which shows its root to tip contrast as static. Its color and occlusion move to the
                // average of the blade, as the mip levels of a texture average its texels. Only these linear inputs
                // are averaged: averaged normals and gloss removed the sun sheen of the field and darkened it.
                half smoothing = (1.0h - detail) * (half)_GrassDrawParameters2.y;
                albedo = lerp(albedo, lerp(baseColor * type.shade.x, tipColor, 0.6h), smoothing);
                albedo *= (1 + input.surface.z * type.wind.z) * lerp(1.0, lerp(1.06, 0.9, across), detail);
                half luminance = dot(albedo, half3(0.2126, 0.7152, 0.0722));
                albedo = saturate(lerp(luminance.xxx, albedo, type.shade.z));
                // Dense grass shades its lower part more, because the other blades hide the sky there.
                half rootOcclusion = lerp(1, type.wind.w, saturate(input.baseColor.a));
                half occlusion = lerp(rootOcclusion, 1, pow(t, 0.6));
                occlusion = lerp(occlusion, lerp(rootOcclusion, 1, 0.6h), smoothing);
                // The grass above a blade point hides the sun from it (Beer-Lambert through the canopy). The path runs
                // from the point to the canopy top toward the sun, so the lower part of dense grass is in shade. Thin
                // blades take the mean visibility of the blade, as the occlusion above.
                half canopyDepth = (half)(type.detail.z * saturate(input.baseColor.a) / max(_MainLightPosition.y, 0.2));
                half sunVisibility = exp(-canopyDepth * (1.0h - (half)t));
                half meanVisibility = canopyDepth > 0.001h ? (1.0h - exp(-canopyDepth)) / canopyDepth : 1.0h;
                sunVisibility = lerp(sunVisibility, meanVisibility, smoothing);
                // The midrib is glossy and the edges are dull. Far blades lose gloss, so they do not glitter.
                half smoothness = saturate(type.base.a * input.smoothnessScale) * lerp(1.0, lerp(1.0, 0.65, across), detail) *
                    lerp(1.0, 0.6, distanceBlend);

                // The visible side of the thin blade faces the camera. The rounded normal turns with it.
                float3 normal = normalize(input.normalWS);
                float3 view = GetWorldSpaceNormalizeViewDir(input.positionWS);
                normal = dot(input.bladeNormalWS, view) >= 0 ? normal : -normal;
                // A blade is folded at the midrib and curved across its width, so its normals cover an arc around the
                // blade axis. A thin blade shows the full arc in each pixel. The pixel then uses the arc normal nearest
                // to the sun highlight, so blades of all facings can reflect the sun toward the camera.
                float3 tangent = normalize(input.tangentWS);
                float3 arcSide = cross(tangent, normal);
                float3 highlight = normalize(_MainLightPosition.xyz + view);
                float arcAngle = atan2(dot(highlight, arcSide), dot(highlight, normal));
                float arcLimit = atan(type.normal.y);
                float arcShare = arcLimit * (1.0 - detail);
                float arcTurn = clamp(arcAngle, -arcShare, arcShare);
                normal = normalize(normal * cos(arcTurn) + arcSide * sin(arcTurn));
                normal = normalize(lerp(normal, float3(0, 1, 0),
                    saturate(type.normal.x + 0.5 * distanceBlend + 0.2 * (1.0 - detail))));
                // Geometric specular antialiasing (Tokuyoshi and Kaplanyan 2019): the normal change across the pixel
                // widens the specular lobe, so thin blades do not sparkle.
                float3 normalDx = ddx(normal);
                float3 normalDy = ddy(normal);
                float variance = 0.25 * (dot(normalDx, normalDx) + dot(normalDy, normalDy));
                float roughness = (1.0 - smoothness) * (1.0 - smoothness);
                // The pixel also averages the arc of the thin blade. The arc variance widens the lobe, so the highlight
                // keeps the energy of the full blade width and does not glow.
                variance += arcShare * arcShare / 3.0;
                float filteredRoughness = sqrt(saturate(roughness * roughness + min(2.0 * variance, 0.3)));
                smoothness = (half)(1.0 - sqrt(filteredRoughness));

                BRDFData brdfData;
                half alpha = 1;
                InitializeBRDFData(albedo, 0, half3(0, 0, 0), smoothness, alpha, brdfData);
                half3 bakedGI = SampleSH9(_GrassAmbientProbe, normal);
                half3 backGI = SampleSH9(_GrassAmbientProbe, -normal);
                half3 color = EvaluateLoogaGBufferAmbient(brdfData.diffuse, bakedGI, occlusion);
                color += EvaluateLoogaLeafAmbientTransmission(albedo * type.tint.a, backGI, occlusion) *
                    LoogaGBufferAmbientWeight();

                InputData inputData = (InputData)0;
                inputData.positionWS = input.positionWS;
                inputData.positionCS = input.positionCS;
                inputData.normalWS = normal;
                GBufferFragOutput output = PackGBuffersBRDFData(brdfData, inputData, smoothness, color, occlusion);
                output.gBuffer0.a = PackLoogaMaterialFlags(GetLoogaCommonMaterialFlags());
                // Thin leaf marker in GBuffer1 G (metallic workflow) and the main light leaf term in GBuffer3 alpha.
                output.gBuffer1.g = PackLoogaThinLeafStrength(type.tint.a);
                output.gBuffer1.b = PackLoogaCanopyShadow(sunVisibility);
                output.color.a = type.tint.a * LoogaLeafTransmissionGeometry(normal, _MainLightPosition.xyz);
                return output;
            }
            ENDHLSL
        }

        // Writes the camera and wind motion of the visible blades into the URP motion vector texture after the URP
        // motion vector pass, so temporal antialiasing follows the swaying blades. The depth test against the camera
        // depth keeps only the blades that the G-buffer pass left visible.
        Pass
        {
            Name "MotionVectors"
            Tags { "LightMode"="LoogaProceduralGrassMotion" }
            Cull Off
            ZWrite Off
            ZTest LEqual
            Offset -1, -1

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex GrassMotionVertex
            #pragma fragment GrassMotionFragment

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.loogasoft.loogagraphicspro/Shaders/Foliage/VegetationWindField.hlsl"
            #include "Packages/com.loogasoft.loogaterrain/Runtime/Shaders/Grass/ProceduralGrassCommon.hlsl"
            #include "Packages/com.loogasoft.loogaterrain/Runtime/Shaders/Grass/ProceduralGrassBlade.hlsl"

            struct MotionVaryings
            {
                float4 positionCS : SV_POSITION;
                float4 currentCS : TEXCOORD0;
                float4 previousCS : TEXCOORD1;
            };

            MotionVaryings GrassMotionVertex(uint vertexID : SV_VertexID, uint instanceID : SV_InstanceID)
            {
                MotionVaryings output = (MotionVaryings)0;
                GrassBlade blade;
                uint vertex;
                if (!GrassFindBlade(vertexID, instanceID, blade, vertex))
                {
                    output.positionCS = float4(0, 0, -1, 1);
                    return output;
                }
                GrassType type = _GrassTypes[asuint(blade.data.z) & GRASS_TYPE_MASK];
                // The previous vertex uses the wind time and wind field of the previous frame and the current camera
                // distance, because the camera distance only sets the width and the level of detail.
                float3 current = GrassBuildVertex(blade, type, vertex, _Time.y, false).position;
                float3 previous = GrassBuildVertex(blade, type, vertex, _LastTimeParameters.x, true).position;
                output.positionCS = GrassClipPosition(current, blade);
                output.currentCS = mul(_NonJitteredViewProjMatrix, float4(current, 1.0));
                output.previousCS = mul(_PrevViewProjMatrix, float4(previous, 1.0));
                return output;
            }

            // URP motion in UV units, as CalcNdcMotionVectorFromCsPositions without its per-object no-motion test:
            // procedural draws have no per-object motion parameters.
            float4 GrassMotionFragment(MotionVaryings input) : SV_Target
            {
                float2 motion = input.currentCS.xy / input.currentCS.w - input.previousCS.xy / input.previousCS.w;
                #if UNITY_UV_STARTS_AT_TOP
                    motion.y = -motion.y;
                #endif
                return float4(motion * 0.5, 0, 0);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
