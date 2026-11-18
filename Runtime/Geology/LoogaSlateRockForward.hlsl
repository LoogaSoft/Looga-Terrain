#ifndef LOOGA_SLATE_ROCK_FORWARD_INCLUDED
#define LOOGA_SLATE_ROCK_FORWARD_INCLUDED

// Lit pass of the slate rock. Vertex color: R = ridge, G = cavity, B = occlusion from the field. Maps project in object space with
// V along the height, so the layers in the texture stay level with the layers of the rock on all sides.
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

struct Attributes
{
    float4 positionOS : POSITION;
    float4 normalOS : NORMAL;
    half4 color : COLOR;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct Varyings
{
    float4 positionCS : SV_POSITION;
    float3 positionWS : TEXCOORD0;
    float3 positionOS : TEXCOORD1;
    half3 normalWS : TEXCOORD2;
    half4 color : TEXCOORD3;
    half fogFactor : TEXCOORD4;
    DECLARE_LIGHTMAP_OR_SH(staticLightmapUV, vertexSH, 5);
#ifdef USE_APV_PROBE_OCCLUSION
    float4 probeOcclusion : TEXCOORD6;
#endif
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

Varyings SlateVertex(Attributes input)
{
    Varyings output = (Varyings)0;
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_TRANSFER_INSTANCE_ID(input, output);
    VertexPositionInputs position = GetVertexPositionInputs(input.positionOS.xyz);
    output.positionCS = position.positionCS;
    output.positionWS = position.positionWS;
    output.positionOS = input.positionOS.xyz;
    output.normalWS = TransformObjectToWorldNormal(input.normalOS.xyz);
    output.color = input.color;
    output.fogFactor = ComputeFogFactor(position.positionCS.z);
    OUTPUT_SH4(position.positionWS, output.normalWS.xyz, GetWorldSpaceNormalizeViewDir(position.positionWS), output.vertexSH,
        output.probeOcclusion);
    return output;
}

half3 UnpackSlateNormal(half4 sample)
{
    half2 xy = (sample.rg * 2.0h - 1.0h) * _NormalStrength;
    return half3(xy, sqrt(saturate(1.0h - dot(xy, xy))));
}

void SlateFragment(Varyings input, out half4 outColor : SV_Target0
#ifdef _WRITE_RENDERING_LAYERS
    , out uint outRenderingLayers : SV_Target1
#endif
)
{
    UNITY_SETUP_INSTANCE_ID(input);
    float3 positionWS = input.positionWS;

    // Each facet reads as flat cleaved stone.
    float3 geometryNormal = normalize(cross(ddx(positionWS), ddy(positionWS)));
    float3 vertexNormal = normalize(input.normalWS);
    if (dot(geometryNormal, vertexNormal) < 0.0) geometryNormal = -geometryNormal;
    float3 macroNormal = normalize(lerp(vertexNormal, geometryNormal, _GeoBlend));
    float3 macroOS = normalize(TransformWorldToObjectDir(macroNormal));

    float3 weights = pow(abs(macroOS), _BlendSharpness);
    weights /= weights.x + weights.y + weights.z;

    float2 uvX = input.positionOS.zy / _TileSize + float2(0.31, 0.0);
    float2 uvY = input.positionOS.xz / _TileSize + float2(0.17, 0.53);
    float2 uvZ = input.positionOS.xy / _TileSize;

    half4 albedoX = SAMPLE_TEXTURE2D(_AlbedoMap, sampler_AlbedoMap, uvX);
    half4 albedoY = SAMPLE_TEXTURE2D(_AlbedoMap, sampler_AlbedoMap, uvY);
    half4 albedoZ = SAMPLE_TEXTURE2D(_AlbedoMap, sampler_AlbedoMap, uvZ);
    half4 normalX = SAMPLE_TEXTURE2D(_NormalMap, sampler_AlbedoMap, uvX);
    half4 normalY = SAMPLE_TEXTURE2D(_NormalMap, sampler_AlbedoMap, uvY);
    half4 normalZ = SAMPLE_TEXTURE2D(_NormalMap, sampler_AlbedoMap, uvZ);
    half4 detailX = SAMPLE_TEXTURE2D(_DetailMap, sampler_AlbedoMap, uvX);
    half4 detailY = SAMPLE_TEXTURE2D(_DetailMap, sampler_AlbedoMap, uvY);
    half4 detailZ = SAMPLE_TEXTURE2D(_DetailMap, sampler_AlbedoMap, uvZ);

    half4 albedoSample = albedoX * weights.x + albedoY * weights.y + albedoZ * weights.z;
    half4 normalSample = normalX * weights.x + normalY * weights.y + normalZ * weights.z;
    half4 detail = detailX * weights.x + detailY * weights.y + detailZ * weights.z;

    // Whiteout blend of the three tangent normals, in object space.
    half3 tangentX = UnpackSlateNormal(normalX);
    half3 tangentY = UnpackSlateNormal(normalY);
    half3 tangentZ = UnpackSlateNormal(normalZ);
    tangentX = half3(tangentX.xy + macroOS.zy, abs(tangentX.z) * macroOS.x);
    tangentY = half3(tangentY.xy + macroOS.xz, abs(tangentY.z) * macroOS.y);
    tangentZ = half3(tangentZ.xy + macroOS.xy, abs(tangentZ.z) * macroOS.z);
    float3 normalOS = normalize(tangentX.zyx * weights.x + tangentY.xzy * weights.y + tangentZ.xyz * weights.z);
    half3 normalWS = (half3)TransformObjectToWorldNormal(normalOS);

    half vertexRidge = input.color.r;
    half vertexCavity = input.color.g;
    half textureCavity = normalSample.a;
    half microHeight = normalSample.b;

    half3 albedo = albedoSample.rgb;
    // Tone follows the facets of the rock, so it never comes from a noise cloud.
    half facetTone = saturate(macroOS.x * 0.35h - macroOS.z * 0.35h + 0.5h);
    albedo *= lerp(0.90h, 1.10h, facetTone);

    // Iron oxide gathers in upright joints and deep cavities.
    half creviceRust = saturate(vertexCavity * 1.35h) * saturate(1.0h - abs(macroNormal.y));
    albedo = lerp(albedo, half3(0.36h, 0.27h, 0.18h), creviceRust * 0.42h);

    // Wear lands on ridges and plate lips, and only the high grains there take it, so a corner has no painted line.
    // Plate lips add little, because on a flat face they read as painted lines.
    half combinedRidge = saturate(vertexRidge * 0.95h + detail.r * 0.15h);
    half microPeak = smoothstep(0.28h, 0.58h, microHeight);
    half edgeWear = smoothstep(_EdgeWearThreshold, 0.80h, combinedRidge) * lerp(0.12h, 1.0h, microPeak);
    albedo = lerp(albedo, _EdgeTint.rgb, edgeWear * _EdgeWearStrength);

    // Open shelves bleach and overhangs darken, so the range of tone grows.
    half macroOcclusion = smoothstep(0.12h, 0.82h, input.color.b);
    half upShelf = smoothstep(0.25h, 0.75h, macroNormal.y) * macroOcclusion;
    half downLedge = smoothstep(-0.05h, -0.55h, macroNormal.y);
    albedo = lerp(albedo, half3(0.42h, 0.39h, 0.34h), upShelf * 0.32h * lerp(0.1h, 1.0h, microPeak));
    albedo *= lerp(1.0h, 0.52h, downLedge);

    // Open faces stay at 1, and deep overhangs and crevices fall to about 0.1.
    half creviceMask = saturate(vertexCavity * 0.65h + (1.0h - textureCavity) * 0.75h);
    half fullOcclusion = saturate(macroOcclusion * (1.0h - creviceMask * 0.75h) * lerp(1.0h, 0.60h, downLedge));
    albedo *= lerp(_CavityFloor, 1.0h, saturate(macroOcclusion * (1.0h - creviceMask * 0.65h)));

    SurfaceData surface = (SurfaceData)0;
    surface.albedo = albedo;
    surface.metallic = 0;
    // A stronger reflectance on flat faces, removed in crevices so no highlight leaks there.
    surface.specular = (half3)(0.052h * saturate(fullOcclusion * 1.6h));
    // Undersides are matte and dim, or a flat soffit mirrors the blue sky.
    surface.smoothness = saturate(albedoSample.a * 1.15h) * lerp(1.0h, 0.3h, downLedge);
    // A floor, so a deep slit keeps some bounce light and is not pure black.
    surface.occlusion = max(fullOcclusion, lerp(0.3h, 0.22h, downLedge));
    surface.alpha = 1;
    surface.normalTS = half3(0, 0, 1);

    InputData inputData = (InputData)0;
    inputData.positionWS = positionWS;
    inputData.normalWS = normalWS;
    inputData.viewDirectionWS = GetWorldSpaceNormalizeViewDir(positionWS);
    inputData.shadowCoord = TransformWorldToShadowCoord(positionWS);
    inputData.fogCoord = InitializeInputDataFog(float4(positionWS, 1.0), input.fogFactor);
    inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);
#if defined(PROBE_VOLUMES_L1) || defined(PROBE_VOLUMES_L2)
    inputData.bakedGI = SAMPLE_GI(input.vertexSH, GetAbsolutePositionWS(positionWS), normalWS, inputData.viewDirectionWS, input.positionCS.xy,
        input.probeOcclusion, inputData.shadowMask);
#else
    inputData.bakedGI = SampleSHPixel(input.vertexSH, normalWS);
    inputData.shadowMask = half4(1, 1, 1, 1);
#endif

    half4 color = UniversalFragmentPBR(inputData, surface);
    color.rgb = MixFog(color.rgb, inputData.fogCoord);
    outColor = half4(color.rgb, 1);
#ifdef _WRITE_RENDERING_LAYERS
    outRenderingLayers = EncodeMeshRenderingLayer();
#endif
}

#endif
