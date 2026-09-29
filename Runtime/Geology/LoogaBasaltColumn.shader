Shader "Looga/Basalt Column"
{
    Properties
    {
        _BaseColor("Base Color", Color) = (0.115, 0.125, 0.14, 1)
        _SecondaryColor("Secondary Color", Color) = (0.26, 0.25, 0.235, 1)
        _CrackColor("Crack Color", Color) = (0.012, 0.014, 0.018, 1)
        [Normal] _DetailNormalMap("Rock Detail Normal", 2D) = "bump" {}
        [Toggle(_PARALLAX_CRACKS)] _ParallaxCracks("Parallax Cracks", Float) = 1
        _CrackHeightMap("Crack Height Map", 2D) = "white" {}
        _CrackTiling("Crack Tiling Per Meter", Range(0.005, 0.2)) = 0.035
        _ParallaxDepth("Crack Depth (Meters)", Range(0, 0.5)) = 0.14
        _ParallaxSteps("Parallax Steps", Range(4, 24)) = 14
        _ParallaxNormalStrength("Crack Normal Strength", Range(0, 4)) = 1.6
        _ParallaxFadeStart("Parallax Fade Start", Float) = 70
        _ParallaxFadeEnd("Parallax Fade End", Float) = 190
        _FarCrackStrength("Distant Crack Strength", Range(0, 2)) = 0.85
        _CrackOcclusion("Crack Occlusion", Range(0, 1)) = 0.65
        _CrackSecondaryScale("Secondary Crack Scale", Range(1.2, 4)) = 2.35
        _CrackSecondaryStrength("Secondary Crack Strength", Range(0, 2)) = 1.1
        _CrackPatchScale("Crack Patch Scale", Range(0.001, 0.05)) = 0.008
        _CrackWarpStrength("Crack Warp Strength", Range(0, 8)) = 2.4
        _CrackIntactAmount("Crack-Free Area", Range(0, 1)) = 0.42
        _CrackDensityVariation("Crack Density Variation", Range(0, 1)) = 0.9
        _DetailTiling("Detail Tiling Per Meter", Range(0.05, 4)) = 0.72
        _DetailNormalStrength("Detail Normal Strength", Range(0, 2)) = 0.85
        _DetailSecondaryScale("Secondary Detail Scale", Range(1.1, 4)) = 1.73
        _DetailBreakupScale("Detail Breakup Scale", Range(0.001, 0.2)) = 0.018
        _MicroNoiseScale("Micro Noise Scale", Range(0.1, 8)) = 1.4
        _MicroNoiseStrength("Micro Color Strength", Range(0, 0.5)) = 0.12
        _FractureScale("Fracture Scale", Range(0.02, 2)) = 0.23
        _NoiseScale("World Noise Scale", Range(0.005, 1)) = 0.075
        _NoiseStrength("Color Variation", Range(0, 1)) = 0.55
        _CrackStrength("Crack Strength", Range(0, 2)) = 1.2
        _WearBrightness("Edge Wear Brightness", Range(0, 1)) = 0.07
        _EdgeHighlightColor("Edge Highlight Color", Color) = (0.32, 0.34, 0.37, 1)
        _EdgeHighlightStrength("Edge Highlight Strength", Range(0, 1)) = 0.42
        _TopEdgeHighlightStrength("Top Edge Highlight Strength", Range(0, 1.5)) = 0.62
        _EdgeHighlightWidth("Vertical Edge Fade Width", Range(0.05, 0.5)) = 0.32
        _EdgeHighlightNoiseScale("Edge Highlight Noise Scale", Range(0.005, 0.2)) = 0.035
        _ContactOcclusionStrength("Contact Occlusion Strength", Range(0, 1)) = 0.46
        _ContactOcclusionWidth("Contact Occlusion Width", Range(0.05, 0.5)) = 0.3
        _ContactOcclusionNoiseScale("Contact Occlusion Noise Scale", Range(0.005, 0.2)) = 0.024
        [HideInInspector] _LoogaInstanceData("Looga Instance Data", Vector) = (0,0,0,0)
        [HideInInspector] _JointOcclusion("Legacy Joint Occlusion", Range(0, 1)) = 0
        _Smoothness("Smoothness", Range(0, 1)) = 0.18
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Opaque" "Queue"="Geometry"
            "LoogaInstanceAppearance"="Vegetation" }
        Cull Back

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/AmbientOcclusion.hlsl"

        TEXTURE2D(_DetailNormalMap);
        SAMPLER(sampler_DetailNormalMap);
        TEXTURE2D(_CrackHeightMap);
        SAMPLER(sampler_CrackHeightMap);
        float4 _CrackHeightMap_TexelSize;
        TEXTURE2D_X(_GTBNTexture);

        float _GTBNDirectLightStrength;
        float _GTBNIndirectLightStrength;
        int _LoogaGTBNEnabled;

        CBUFFER_START(UnityPerMaterial)
        half4 _BaseColor;
        half4 _SecondaryColor;
        half4 _CrackColor;
        half4 _EdgeHighlightColor;
        float _DetailTiling;
        float _DetailNormalStrength;
        float _CrackTiling;
        float _ParallaxDepth;
        float _ParallaxSteps;
        float _ParallaxNormalStrength;
        float _ParallaxFadeStart;
        float _ParallaxFadeEnd;
        float _FarCrackStrength;
        float _CrackOcclusion;
        float _CrackSecondaryScale;
        float _CrackSecondaryStrength;
        float _CrackPatchScale;
        float _CrackWarpStrength;
        float _CrackIntactAmount;
        float _CrackDensityVariation;
        float _MicroNoiseScale;
        float _MicroNoiseStrength;
        float _DetailSecondaryScale;
        float _DetailBreakupScale;
        float _FractureScale;
        float _NoiseScale;
        float _NoiseStrength;
        float _CrackStrength;
        float _WearBrightness;
        float _EdgeHighlightStrength;
        float _TopEdgeHighlightStrength;
        float _EdgeHighlightWidth;
        float _EdgeHighlightNoiseScale;
        float _ContactOcclusionStrength;
        float _ContactOcclusionWidth;
        float _ContactOcclusionNoiseScale;
        float4 _LoogaInstanceData;
        float _JointOcclusion;
        float _Smoothness;
        CBUFFER_END

        float Hash31(float3 p)
        {
            p = frac(p * 0.1031);
            p += dot(p, p.yzx + 33.33);
            return frac((p.x + p.y) * p.z);
        }

        float ValueNoise(float3 p)
        {
            float3 i = floor(p);
            float3 f = frac(p);
            f = f * f * (3.0 - 2.0 * f);
            return lerp(lerp(lerp(Hash31(i), Hash31(i + float3(1,0,0)), f.x),
                             lerp(Hash31(i + float3(0,1,0)), Hash31(i + float3(1,1,0)), f.x), f.y),
                        lerp(lerp(Hash31(i + float3(0,0,1)), Hash31(i + float3(1,0,1)), f.x),
                             lerp(Hash31(i + float3(0,1,1)), Hash31(i + 1.0), f.x), f.y), f.z);
        }

        float FractalValueNoise(float3 position, float phase)
        {
            float first = ValueNoise(position + phase);
            float second = ValueNoise(position * 2.07 + phase * 1.71);
            float third = ValueNoise(position * 4.19 + phase * 3.17);
            return first * 0.57 + second * 0.29 + third * 0.14;
        }

        float3 TriplanarWeights(half3 surfaceNormal)
        {
            float3 weights = pow(abs(surfaceNormal), 4.0);
            weights /= max(weights.x + weights.y + weights.z, 0.0001);
            return weights;
        }

        float2 RotateUv(float2 uv, float2 rotation)
        {
            return float2(uv.x * rotation.x - uv.y * rotation.y,
                uv.x * rotation.y + uv.y * rotation.x);
        }

        half3 SampleTriplanarDetailLayer(float3 positionWS, half3 surfaceNormal,
            float tiling, float2 phase)
        {
            float3 weights = TriplanarWeights(surfaceNormal);
            float3 signs = lerp(-1.0, 1.0, step(0.0, surfaceNormal));
            float2 uvX = RotateUv(positionWS.zy * tiling, float2(0.8, 0.6)) + phase;
            float2 uvY = RotateUv(positionWS.xz * tiling, float2(0.6, -0.8)) +
                phase.yx * float2(-1.0, 1.0);
            float2 uvZ = RotateUv(positionWS.xy * tiling, float2(-0.8, 0.6)) -
                phase * 0.73;

            half3 normalX = UnpackNormalScale(SAMPLE_TEXTURE2D(_DetailNormalMap,
                sampler_DetailNormalMap, uvX), _DetailNormalStrength);
            half3 normalY = UnpackNormalScale(SAMPLE_TEXTURE2D(_DetailNormalMap,
                sampler_DetailNormalMap, uvY), _DetailNormalStrength);
            half3 normalZ = UnpackNormalScale(SAMPLE_TEXTURE2D(_DetailNormalMap,
                sampler_DetailNormalMap, uvZ), _DetailNormalStrength);

            half3 worldX = half3(normalX.z * signs.x, normalX.y, normalX.x);
            half3 worldY = half3(normalY.x, normalY.z * signs.y, normalY.y);
            half3 worldZ = half3(normalZ.x, normalZ.y, normalZ.z * signs.z);
            return normalize(worldX * weights.x + worldY * weights.y + worldZ * weights.z);
        }

        half3 SampleTriplanarDetailNormal(float3 positionWS, half3 surfaceNormal)
        {
            float breakup = ValueNoise(positionWS * _DetailBreakupScale + 41.7);
            float3 warp = float3(
                ValueNoise(positionWS * _DetailBreakupScale + 7.1),
                ValueNoise(positionWS * _DetailBreakupScale + 19.3),
                ValueNoise(positionWS * _DetailBreakupScale + 31.9)) - 0.5;
            half3 primary = SampleTriplanarDetailLayer(
                positionWS + warp * 1.4, surfaceNormal, _DetailTiling, float2(0.0, 0.0));
            half3 secondary = SampleTriplanarDetailLayer(
                positionWS - warp * 2.1, surfaceNormal,
                _DetailTiling * _DetailSecondaryScale, float2(17.37, -9.13));
            float secondaryWeight = lerp(0.22, 0.58, smoothstep(0.25, 0.75, breakup));
            return normalize(lerp(primary, secondary, secondaryWeight));
        }

        float SampleTriplanarCrackLayer(float3 pillarPosition, half3 surfaceNormal,
            float tiling, float2 phase)
        {
            float3 weights = TriplanarWeights(surfaceNormal);
            float2 uvX = RotateUv(pillarPosition.zy * tiling, float2(0.8, 0.6)) + phase;
            float2 uvY = RotateUv(pillarPosition.xz * tiling, float2(0.6, -0.8)) +
                phase.yx * float2(-1.0, 1.0);
            float2 uvZ = RotateUv(pillarPosition.xy * tiling, float2(-0.8, 0.6)) -
                phase * 0.73;
            float heightX = SAMPLE_TEXTURE2D(_CrackHeightMap, sampler_CrackHeightMap, uvX).r;
            float heightY = SAMPLE_TEXTURE2D(_CrackHeightMap, sampler_CrackHeightMap, uvY).r;
            float heightZ = SAMPLE_TEXTURE2D(_CrackHeightMap, sampler_CrackHeightMap, uvZ).r;
            return dot(float3(heightX, heightY, heightZ), weights);
        }

        float EvaluateCrackMask(float3 pillarPosition, half3 surfaceNormal, float pillarSeed)
        {
            // Work in pillar-local metric space. The phase changes for every transform so each
            // pillar receives its own crack layout without changing the physical crack scale.
            float3 seedOffset = float3(31.7, 67.1, 113.9) * pillarSeed +
                float3(7.3, 19.1, 41.9) * frac(pillarSeed * 17.13);
            float3 crackPosition = pillarPosition + seedOffset;
            float3 primaryWarp = float3(
                ValueNoise(crackPosition * _CrackPatchScale + 5.3),
                ValueNoise(crackPosition * _CrackPatchScale + 17.9),
                ValueNoise(crackPosition * _CrackPatchScale + 43.1)) - 0.5;
            float3 secondaryWarp = float3(
                ValueNoise(crackPosition * _CrackPatchScale * 1.73 + 91.7),
                ValueNoise(crackPosition * _CrackPatchScale * 1.73 + 133.1),
                ValueNoise(crackPosition * _CrackPatchScale * 1.73 + 177.3)) - 0.5;
            float primaryHeight = SampleTriplanarCrackLayer(
                crackPosition + primaryWarp * _CrackWarpStrength,
                surfaceNormal, _CrackTiling, float2(0.0, 0.0));
            float secondaryHeight = SampleTriplanarCrackLayer(
                crackPosition + secondaryWarp * _CrackWarpStrength * 0.83, surfaceNormal,
                _CrackTiling * _CrackSecondaryScale, float2(13.71, -5.29));
            float primaryCrack = saturate((0.64 - primaryHeight) * 2.8);
            float secondaryCrack = saturate((0.62 - secondaryHeight) * 4.1);

            float intactThreshold = lerp(0.20, 0.58, _CrackIntactAmount);
            float crackRegionNoise = FractalValueNoise(
                crackPosition * _CrackPatchScale * 0.47, 29.7 + pillarSeed * 53.0);
            float crackRegion = smoothstep(intactThreshold,
                min(0.94, intactThreshold + 0.11), crackRegionNoise);

            float primaryRegion = smoothstep(0.32, 0.58,
                FractalValueNoise(crackPosition * _CrackPatchScale * 0.83,
                    71.3 + pillarSeed * 79.0));
            float secondaryRegion = smoothstep(0.43, 0.68,
                FractalValueNoise(crackPosition * _CrackPatchScale * 1.91,
                    137.9 + pillarSeed * 101.0));
            float denseRegion = smoothstep(0.59, 0.79,
                FractalValueNoise(crackPosition * _CrackPatchScale * 1.23,
                    211.7 + pillarSeed * 131.0));
            primaryRegion = lerp(1.0, primaryRegion, _CrackDensityVariation);
            secondaryRegion = lerp(1.0, secondaryRegion, _CrackDensityVariation);
            float primaryWeight = lerp(0.78, 1.18, Hash31(float3(
                pillarSeed * 17.0, pillarSeed * 31.0, 5.7)));
            float secondaryWeight = lerp(0.92, 1.32, Hash31(float3(
                pillarSeed * 43.0, 11.3, pillarSeed * 61.0)));
            float primaryContribution = primaryCrack * primaryRegion * primaryWeight;
            float secondaryContribution = secondaryCrack * secondaryRegion *
                lerp(0.48, 1.0, denseRegion) * secondaryWeight * _CrackSecondaryStrength;
            return saturate(max(primaryContribution, secondaryContribution) * crackRegion);
        }

        void EvaluateParallaxCracks(float3 pillarPosition, half3 surfaceNormal,
            half3 viewDirection, float pillarSeed, float cameraDistance,
            out float crackMask, out half3 crackNormal)
        {
            float distantCrack = EvaluateCrackMask(
                pillarPosition, surfaceNormal, pillarSeed) * _FarCrackStrength;
            crackMask = distantCrack;
            crackNormal = surfaceNormal;

            float fade = 1.0 - smoothstep(_ParallaxFadeStart,
                max(_ParallaxFadeStart + 1.0, _ParallaxFadeEnd),
                cameraDistance);
            if (fade <= 0.001 || _ParallaxDepth <= 0.0001) return;

            float grazing = 1.0 - saturate(abs(dot(surfaceNormal, viewDirection)));
            int stepCount = (int)clamp(round(lerp(4.0, _ParallaxSteps,
                fade * lerp(0.72, 1.0, grazing))), 4.0, 24.0);
            float layerStep = 1.0 / stepCount;
            float rayLength = _ParallaxDepth /
                max(abs(dot(surfaceNormal, viewDirection)), 0.25);
            float3 positionStep = viewDirection * (rayLength / stepCount);
            float layerDepth = 0.0;
            float3 samplePosition = pillarPosition;
            float height = 1.0 - EvaluateCrackMask(
                samplePosition, surfaceNormal, pillarSeed);
            UNITY_LOOP
            for (int stepIndex = 0; stepIndex < 24; stepIndex++)
            {
                if (stepIndex >= stepCount || layerDepth >= 1.0 - height) break;
                samplePosition -= positionStep;
                layerDepth += layerStep;
                height = 1.0 - EvaluateCrackMask(
                    samplePosition, surfaceNormal, pillarSeed);
            }

            float parallaxCrack = saturate(1.0 - height);
            crackMask = lerp(distantCrack, parallaxCrack, fade);
            float3 referenceAxis = abs(surfaceNormal.y) < 0.95 ? float3(0.0, 1.0, 0.0) :
                float3(1.0, 0.0, 0.0);
            float3 tangent = normalize(cross(referenceAxis, surfaceNormal));
            float3 bitangent = normalize(cross(surfaceNormal, tangent));
            float sampleDistance = max(0.12,
                rcp(max(_CrackTiling * _CrackHeightMap_TexelSize.z, 1.0)));
            float left = EvaluateCrackMask(
                samplePosition - tangent * sampleDistance, surfaceNormal, pillarSeed);
            float right = EvaluateCrackMask(
                samplePosition + tangent * sampleDistance, surfaceNormal, pillarSeed);
            float down = EvaluateCrackMask(
                samplePosition - bitangent * sampleDistance, surfaceNormal, pillarSeed);
            float up = EvaluateCrackMask(
                samplePosition + bitangent * sampleDistance, surfaceNormal, pillarSeed);
            float2 gradient = float2(right - left, up - down) / sampleDistance;
            half3 parallaxNormal = normalize(surfaceNormal -
                tangent * gradient.x * _ParallaxNormalStrength -
                bitangent * gradient.y * _ParallaxNormalStrength);
            crackNormal = normalize(lerp(surfaceNormal, parallaxNormal, fade));
        }

        struct Attributes
        {
            float4 positionOS : POSITION;
            float3 normalOS : NORMAL;
            float2 uv : TEXCOORD0;
            float2 masks : TEXCOORD1;
            float4 pillarData : TEXCOORD2;
            float4 contactData : TEXCOORD3;
            half4 color : COLOR;
            UNITY_VERTEX_INPUT_INSTANCE_ID
        };

        struct Varyings
        {
            float4 positionCS : SV_POSITION;
            float3 positionWS : TEXCOORD0;
            half3 normalWS : TEXCOORD1;
            float2 uv : TEXCOORD2;
            half4 color : TEXCOORD3;
            float2 masks : TEXCOORD4;
            half fog : TEXCOORD5;
            float4 pillarPositionAndSeed : TEXCOORD6;
            half3 normalOS : TEXCOORD7;
            float4 contactData : TEXCOORD8;
            UNITY_VERTEX_INPUT_INSTANCE_ID
        };

        Varyings BasaltVertex(Attributes input)
        {
            Varyings output;
            UNITY_SETUP_INSTANCE_ID(input);
            UNITY_TRANSFER_INSTANCE_ID(input, output);
            VertexPositionInputs position = GetVertexPositionInputs(input.positionOS.xyz);
            VertexNormalInputs normal = GetVertexNormalInputs(input.normalOS);
            output.positionCS = position.positionCS;
            output.positionWS = position.positionWS;
            output.normalWS = normal.normalWS;
            output.uv = input.uv;
            output.color = input.color;
            output.masks = input.masks;
            output.fog = ComputeFogFactor(position.positionCS.z);
            float4x4 objectToWorld = GetObjectToWorldMatrix();
            float3 objectScale = float3(
                length(float3(objectToWorld._m00, objectToWorld._m10, objectToWorld._m20)),
                length(float3(objectToWorld._m01, objectToWorld._m11, objectToWorld._m21)),
                length(float3(objectToWorld._m02, objectToWorld._m12, objectToWorld._m22)));
            float3 originWS = TransformObjectToWorld(float3(0.0, 0.0, 0.0));
            float derivedSeed = Hash31(floor(originWS * 0.173) + 19.7);
            float pillarSeed = input.pillarData.w >= 0.0 ? input.pillarData.w : derivedSeed;
            output.pillarPositionAndSeed = float4(input.pillarData.xyz * objectScale,
                pillarSeed);
            output.normalOS = input.normalOS;
            output.contactData = input.contactData;
            return output;
        }

        half4 BasaltFragment(Varyings input) : SV_Target
        {
            UNITY_SETUP_INSTANCE_ID(input);
            half3 normal = normalize(input.normalWS);
            half3 detailNormal = SampleTriplanarDetailNormal(input.positionWS, normal);
            normal = normalize(lerp(normal, detailNormal, saturate(_DetailNormalStrength)));
            half3 viewDirection = GetWorldSpaceNormalizeViewDir(input.positionWS);
            float3 pillarPosition = input.pillarPositionAndSeed.xyz;
            float pillarSeed = input.pillarPositionAndSeed.w;
            half3 crackSurfaceNormal = normalize(input.normalOS);
            float parallaxCrack = EvaluateCrackMask(
                pillarPosition, crackSurfaceNormal, pillarSeed) *
                _FarCrackStrength;
#if defined(_PARALLAX_CRACKS)
            float4x4 objectToWorld = GetObjectToWorldMatrix();
            float3 objectScale = float3(
                length(float3(objectToWorld._m00, objectToWorld._m10, objectToWorld._m20)),
                length(float3(objectToWorld._m01, objectToWorld._m11, objectToWorld._m21)),
                length(float3(objectToWorld._m02, objectToWorld._m12, objectToWorld._m22)));
            half3 viewDirectionOS = normalize(TransformWorldToObjectDir(viewDirection, false) *
                objectScale);
            half3 parallaxNormalOS;
            EvaluateParallaxCracks(pillarPosition, crackSurfaceNormal, viewDirectionOS,
                pillarSeed, distance(_WorldSpaceCameraPos, input.positionWS),
                parallaxCrack, parallaxNormalOS);
            half3 parallaxNormalWS = TransformObjectToWorldNormal(parallaxNormalOS);
            normal = normalize(lerp(normal, parallaxNormalWS, parallaxCrack));
#endif
            float noise = ValueNoise(input.positionWS * _NoiseScale);
            float fineNoise = ValueNoise(input.positionWS * _NoiseScale * 4.17);
            float variation = saturate(noise * 0.72 + fineNoise * 0.28);
            half3 albedo = lerp(_BaseColor.rgb, _SecondaryColor.rgb,
                saturate(variation * _NoiseStrength + input.color.b * 0.18));
            float geometricCrack = saturate(input.color.g * _CrackStrength);
            float microNoise = ValueNoise(input.positionWS * _MicroNoiseScale);
            float microNoise2 = ValueNoise(input.positionWS * _MicroNoiseScale * 3.71 + 17.31);
            float microVariation = (microNoise * 0.65 + microNoise2 * 0.35 - 0.5) * 2.0;
            albedo *= 1.0 + microVariation * _MicroNoiseStrength;

            float crack = saturate(max(geometricCrack, parallaxCrack));
            albedo = lerp(albedo, _CrackColor.rgb, crack * 0.24);
            float wearBreakup = smoothstep(0.46, 0.8,
                ValueNoise(input.positionWS * 0.31 + 19.7)) *
                lerp(0.35, 1.0, microNoise2);
            albedo += input.color.r * wearBreakup * _WearBrightness * (1.0 - crack);
            float sideFace = step(-0.5, input.masks.x);
            float topCap = (1.0 - sideFace) * step(-1.5, input.masks.x);
            float chamferFace = sideFace * (1.0 - step(1.5, input.masks.x));
            float broadFace = sideFace * step(1.5, input.masks.x);
            float faceCoordinate = saturate(input.masks.x);
            float chamferCenter = 1.0 - saturate(abs(faceCoordinate * 2.0 - 1.0));
            float broadCoordinate = saturate(input.masks.x - 2.0);
            float broadEdgeDistance = min(broadCoordinate, 1.0 - broadCoordinate);
            float broadEdgeProfile = (1.0 - smoothstep(0.0,
                max(0.05, _EdgeHighlightWidth), broadEdgeDistance)) * broadFace;
            float chamferProfile = lerp(0.68, 1.0,
                smoothstep(0.0, 1.0, chamferCenter)) * chamferFace;
            float verticalEdgeProfile = max(chamferProfile, broadEdgeProfile * 0.62);
            float topSideProfile = sideFace * smoothstep(0.72, 1.0, input.masks.y);
            float topCapProfile = topCap * smoothstep(0.52, 1.0, input.masks.y);
            float edgeNoise = FractalValueNoise(
                input.positionWS * _EdgeHighlightNoiseScale, 317.9);
            float edgeBreakup = lerp(0.58, 1.0, smoothstep(0.28, 0.82, edgeNoise));
            float verticalEdgeFade = verticalEdgeProfile * edgeBreakup;
            float topEdgeFade = max(topSideProfile, topCapProfile) *
                lerp(0.72, 1.0, edgeBreakup);
            float edgeFade = max(verticalEdgeFade * _EdgeHighlightStrength,
                topEdgeFade * _TopEdgeHighlightStrength);
            albedo = lerp(albedo, max(albedo, _EdgeHighlightColor.rgb),
                saturate(edgeFade) * (1.0 - crack * 0.72));

            Light mainLight = GetMainLight(TransformWorldToShadowCoord(input.positionWS));
            half ndotl = saturate(dot(normal, mainLight.direction));
            float2 screenUv = GetNormalizedScreenSpaceUV(input.positionCS);
            AmbientOcclusionFactor ambientOcclusion = GetScreenSpaceAmbientOcclusion(screenUv);
            if (_LoogaGTBNEnabled != 0)
            {
                half gtao = SAMPLE_TEXTURE2D_X_LOD(_GTBNTexture, sampler_PointClamp, screenUv, 0).a;
                ambientOcclusion.indirectAmbientOcclusion *= lerp(1.0h, gtao,
                    saturate(_GTBNIndirectLightStrength));
                ambientOcclusion.directAmbientOcclusion *= lerp(1.0h, gtao,
                    saturate(_GTBNDirectLightStrength));
            }
            float useInstanceData = step(input.contactData.x, -0.5);
            float4 contactData = saturate(lerp(input.contactData,
                _LoogaInstanceData, useInstanceData));
            half3 contactNormal = normalize(input.normalOS);
            float4 directionWeights = max(float4(contactNormal.x, -contactNormal.x,
                contactNormal.z, -contactNormal.z), 0.0);
            float directionalContact = dot(contactData, directionWeights) /
                max(0.001, dot(directionWeights, float4(1.0, 1.0, 1.0, 1.0)));
            float contactNoise = FractalValueNoise(
                input.positionWS * _ContactOcclusionNoiseScale, 463.1);
            float contactBroadProfile = (1.0 - smoothstep(0.0,
                max(0.05, _ContactOcclusionWidth), broadEdgeDistance)) * broadFace;
            float contactProfile = max(chamferFace * 0.72,
                contactBroadProfile) * sideFace;
            float contactOcclusion = saturate(directionalContact * contactProfile *
                _ContactOcclusionStrength * lerp(0.72, 1.0, contactNoise));
            ambientOcclusion.indirectAmbientOcclusion *= 1.0h - contactOcclusion;
            ambientOcclusion.directAmbientOcclusion *= 1.0h - contactOcclusion * 0.24h;
            half crackOcclusion = saturate(1.0h - crack * _CrackOcclusion);
            ambientOcclusion.indirectAmbientOcclusion = min(
                ambientOcclusion.indirectAmbientOcclusion, crackOcclusion);
            half3 lighting = SampleSH(normal) * ambientOcclusion.indirectAmbientOcclusion +
                mainLight.color * ndotl * mainLight.shadowAttenuation *
                ambientOcclusion.directAmbientOcclusion;
            lighting = max(lighting, 0.16h);
            half3 color = albedo * lighting;
            half3 halfDirection = SafeNormalize(mainLight.direction + viewDirection);
            half specular = pow(saturate(dot(normal, halfDirection)),
                lerp(8.0h, 96.0h, _Smoothness)) * _Smoothness * 0.35h;
            color += mainLight.color * specular * mainLight.shadowAttenuation;
            color = MixFog(color, input.fog);
            return half4(color, 1);
        }
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            // ForwardOnly is required for this forward-lit material to participate in both
            // URP Forward and Deferred renderers without a separate GBuffer implementation.
            Tags { "LightMode"="UniversalForwardOnly" }
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex BasaltVertex
            #pragma fragment BasaltFragment
            #pragma multi_compile_instancing
            #pragma shader_feature_local_fragment _PARALLAX_CRACKS
            #pragma multi_compile _ DOTS_INSTANCING_ON
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile_fog
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode"="ShadowCaster" }
            ColorMask 0
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex ShadowVertex
            #pragma fragment ShadowFragment
            #pragma multi_compile_instancing
            #pragma multi_compile _ DOTS_INSTANCING_ON
            struct ShadowVaryings { float4 positionCS : SV_POSITION; UNITY_VERTEX_INPUT_INSTANCE_ID };
            ShadowVaryings ShadowVertex(Attributes input)
            {
                ShadowVaryings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                return output;
            }
            half4 ShadowFragment(ShadowVaryings input) : SV_Target { return 0; }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode"="DepthOnly" }
            ColorMask R
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex DepthVertex
            #pragma fragment DepthFragment
            #pragma multi_compile_instancing
            #pragma multi_compile _ DOTS_INSTANCING_ON
            struct DepthVaryings { float4 positionCS : SV_POSITION; UNITY_VERTEX_INPUT_INSTANCE_ID };
            DepthVaryings DepthVertex(Attributes input)
            {
                DepthVaryings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                return output;
            }
            half4 DepthFragment(DepthVaryings input) : SV_Target { return 0; }
            ENDHLSL
        }

        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode"="DepthNormals" }
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex DepthNormalsVertex
            #pragma fragment DepthNormalsFragment
            #pragma multi_compile_instancing
            #pragma multi_compile _ DOTS_INSTANCING_ON
            struct DepthNormalsVaryings
            {
                float4 positionCS : SV_POSITION;
                half3 normalWS : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            DepthNormalsVaryings DepthNormalsVertex(Attributes input)
            {
                DepthNormalsVaryings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                return output;
            }
            half4 DepthNormalsFragment(DepthNormalsVaryings input) : SV_Target
            {
                return half4(normalize(input.normalWS), 0);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
