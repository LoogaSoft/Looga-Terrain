#ifndef LOOGA_ROAD_SURFACE_INCLUDED
#define LOOGA_ROAD_SURFACE_INCLUDED

// Surface of the Looga road shader. Every texture projects from above in world XZ, so the slabs of all roads, pads
// and junctions line up. The asphalt texture repeats on a grid of triangles, each with a random offset and rotation,
// and the triangles blend by the albedo alpha (height) to hide the tiling. Two wear layers add a detail normal and
// change the smoothness and occlusion where the red (A) or green (B) channel of a large noise texture is high. Patches
// of a second asphalt cover the road where the green channel of the noise is high at a larger size. Road wear
// (LoogaRoadWear.hlsl) places tracks, dust, cracks, tar seals and repairs in the road coordinates of the slabs.
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

struct Attributes
{
    float4 positionOS : POSITION;
    float3 normalOS : NORMAL;
    float4 tangentOS : TANGENT;
    // Road coordinates (LoogaRoadSlabs): across, along, half width, random value.
    float4 road : TEXCOORD1;
    // Red: a road side along a pad. Green: the place on the slab lip, 0 at the rim and 1 on the ground
    // (LoogaRoadSlabs).
    half4 joins : COLOR;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct Varyings
{
    float4 positionCS : SV_POSITION;
    float3 positionWS : TEXCOORD0;
    half3 normalWS : TEXCOORD1;
    half4 tangentWS : TEXCOORD2;
    half fogFactor : TEXCOORD3;
    DECLARE_LIGHTMAP_OR_SH(staticLightmapUV, vertexSH, 4);
#ifdef USE_APV_PROBE_OCCLUSION
    float4 probeOcclusion : TEXCOORD5;
#endif
    float4 road : TEXCOORD6;
    // X: a road side along a pad. Y: the place on the slab lip.
    half2 joins : TEXCOORD7;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

Varyings RoadVertex(Attributes input)
{
    Varyings output = (Varyings)0;
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_TRANSFER_INSTANCE_ID(input, output);
    float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
    VertexNormalInputs normal = GetVertexNormalInputs(input.normalOS, input.tangentOS);
    // The textures and the lighting use the true position. Only the depth moves (RoadLipPosition).
    output.positionCS = TransformWorldToHClip(RoadLipPosition(positionWS, input.joins.g));
    output.positionWS = positionWS;
    output.normalWS = normal.normalWS;
    output.tangentWS = half4(normal.tangentWS, input.tangentOS.w * GetOddNegativeScale());
    output.fogFactor = ComputeFogFactor(output.positionCS.z);
    output.road = input.road;
    output.joins = input.joins.rg;
    OUTPUT_SH4(positionWS, output.normalWS.xyz, GetWorldSpaceNormalizeViewDir(positionWS), output.vertexSH,
        output.probeOcclusion);
    return output;
}

// A random offset for one cell of the triangle grid.
float2 RoadCellOffset(float2 cell)
{
    float3 p = frac(float3(cell.xyx) * float3(0.1031, 0.1030, 0.0973));
    p += dot(p, p.yzx + 33.33);
    return frac((p.xx + p.yz) * p.zy);
}

#include "Packages/com.loogasoft.loogaterrain/Runtime/Roads/Shaders/LoogaRoadWear.hlsl"
#if defined(_GROUND_TERRAIN)
    #include "Packages/com.loogasoft.loogaterrain/Runtime/Shaders/Surface/LoogaTerrainGround.hlsl"
#endif

// A random rotation for one cell of the triangle grid. The asphalt has no direction, so any angle fits.
float2x2 RoadCellRotation(float2 cell)
{
    float angle = RoadHash(cell + 71.3) * 6.2831853;
    float s, c;
    sincos(angle, s, c);
    return float2x2(c, -s, s, c);
}

// Samples the normal map at a rotated UV and turns the normal back into the surface frame.
half3 RoadRotatedNormal(float2 uv, float2x2 rotation, float2 uvDx, float2 uvDy)
{
    half3 normal = UnpackNormalScale(SAMPLE_TEXTURE2D_GRAD(_NormalMap, sampler_NormalMap, mul(rotation, uv),
        mul(rotation, uvDx), mul(rotation, uvDy)), _NormalStrength);
    normal.xy = half2(mul(float2(normal.xy), rotation));
    return normal;
}

// The three corners of the triangle that holds the point, on a grid of equilateral triangles with edge length one,
// and the barycentric weight of each corner.
void RoadTriangleGrid(float2 position, out float2 cornerA, out float2 cornerB, out float2 cornerC,
    out float3 weights)
{
    // Skew the plane so the triangles become half squares.
    float2 skewed = float2(position.x - position.y * 0.57735027, position.y * 1.15470054);
    float2 base = floor(skewed);
    float3 local = float3(frac(skewed), 0.0);
    local.z = 1.0 - local.x - local.y;
    if (local.z > 0.0)
    {
        weights = float3(local.z, local.y, local.x);
        cornerA = base;
        cornerB = base + float2(0.0, 1.0);
        cornerC = base + float2(1.0, 0.0);
    }
    else
    {
        weights = float3(-local.z, 1.0 - local.y, 1.0 - local.x);
        cornerA = base + 1.0;
        cornerB = base + float2(1.0, 0.0);
        cornerC = base + float2(0.0, 1.0);
    }
}

// Raises the weights of the samples whose height stands above the others, over a band of the given width.
half3 RoadHeightWeights(half3 weights, half3 heights, half band)
{
    half3 levels = weights + heights * weights;
    half top = max(levels.x, max(levels.y, levels.z));
    half3 blend = max(levels - (top - band), 0.0h);
    return blend / max(blend.x + blend.y + blend.z, 1e-4h);
}

// The anti-tiling cells at one point: the three corners of the triangle grid, each with a random rotation and UV
// offset, and the barycentric weight of each corner. The asphalt and the wear layers use the same cells.
struct RoadCells
{
    float2x2 rotationA;
    float2x2 rotationB;
    float2x2 rotationC;
    float2 offsetA;
    float2 offsetB;
    float2 offsetC;
    float3 weights;
};

RoadCells RoadGetCells(float2 positionXZ)
{
    RoadCells cells;
    float2 cornerA, cornerB, cornerC;
    RoadTriangleGrid(positionXZ / _CellSize, cornerA, cornerB, cornerC, cells.weights);
    cells.rotationA = RoadCellRotation(cornerA);
    cells.rotationB = RoadCellRotation(cornerB);
    cells.rotationC = RoadCellRotation(cornerC);
    cells.offsetA = RoadCellOffset(cornerA);
    cells.offsetB = RoadCellOffset(cornerB);
    cells.offsetC = RoadCellOffset(cornerC);
    return cells;
}

// One normal map sample in a cell: the rotated and offset UV, and the normal turned back into the surface frame.
half3 RoadCellNormal(TEXTURE2D_PARAM(normalMap, normalSampler), float2 uv, float2 uvDx, float2 uvDy,
    float2x2 rotation, float2 offset)
{
    half3 normal = UnpackNormal(SAMPLE_TEXTURE2D_GRAD(normalMap, normalSampler, mul(rotation, uv) + offset,
        mul(rotation, uvDx), mul(rotation, uvDy)));
    normal.xy = half2(mul(float2(normal.xy), rotation));
    return normal;
}

// A tiled detail normal map without visible repetition: three cell samples, blended so that the detail keeps its
// strength in the middle of each triangle (the weights are divided by their length, not their sum).
half3 RoadSampleDetailNormal(TEXTURE2D_PARAM(normalMap, normalSampler), float2 uv, RoadCells cells)
{
#if defined(_ANTI_TILING)
    float2 uvDx = ddx(uv);
    float2 uvDy = ddy(uv);
    half3 weights = half3(cells.weights) * rsqrt(max(dot(cells.weights, cells.weights), 1e-4));
    half2 slope = RoadCellNormal(TEXTURE2D_ARGS(normalMap, normalSampler), uv, uvDx, uvDy, cells.rotationA,
        cells.offsetA).xy * weights.x;
    slope += RoadCellNormal(TEXTURE2D_ARGS(normalMap, normalSampler), uv, uvDx, uvDy, cells.rotationB,
        cells.offsetB).xy * weights.y;
    slope += RoadCellNormal(TEXTURE2D_ARGS(normalMap, normalSampler), uv, uvDx, uvDy, cells.rotationC,
        cells.offsetC).xy * weights.z;
    return half3(slope, sqrt(saturate(1.0h - dot(slope, slope))));
#else
    return UnpackNormal(SAMPLE_TEXTURE2D(normalMap, normalSampler, uv));
#endif
}

// The asphalt texture set: albedo, height (albedo alpha), tangent space normal, smoothness, occlusion and metallic.
void RoadSampleAsphalt(float2 positionXZ, RoadCells cells, out half3 albedo, out half height, out half3 normalTS,
    out half smoothness, out half occlusion, out half metallic)
{
    float2 uv = positionXZ / _TextureSize;
    float2 uvDx = ddx(uv);
    float2 uvDy = ddy(uv);
    half4 color;
    half4 mask;
#if defined(_ANTI_TILING)
    float3 grid = cells.weights;
    float2x2 rotationA = cells.rotationA;
    float2x2 rotationB = cells.rotationB;
    float2x2 rotationC = cells.rotationC;
    float2 uvA = mul(rotationA, uv) + cells.offsetA;
    float2 uvB = mul(rotationB, uv) + cells.offsetB;
    float2 uvC = mul(rotationC, uv) + cells.offsetC;
    float2 dxA = mul(rotationA, uvDx), dyA = mul(rotationA, uvDy);
    float2 dxB = mul(rotationB, uvDx), dyB = mul(rotationB, uvDy);
    float2 dxC = mul(rotationC, uvDx), dyC = mul(rotationC, uvDy);
    half4 colorA = SAMPLE_TEXTURE2D_GRAD(_BaseMap, sampler_BaseMap, uvA, dxA, dyA);
    half4 colorB = SAMPLE_TEXTURE2D_GRAD(_BaseMap, sampler_BaseMap, uvB, dxB, dyB);
    half4 colorC = SAMPLE_TEXTURE2D_GRAD(_BaseMap, sampler_BaseMap, uvC, dxC, dyC);
    half3 weights = RoadHeightWeights(half3(grid), half3(colorA.a, colorB.a, colorC.a), _CellBlend);
    color = colorA * weights.x + colorB * weights.y + colorC * weights.z;
    // The normals turn back by the rotation of their cell before they blend.
    normalTS = RoadRotatedNormal(uv, rotationA, uvDx, uvDy) * weights.x +
        RoadRotatedNormal(uv, rotationB, uvDx, uvDy) * weights.y +
        RoadRotatedNormal(uv, rotationC, uvDx, uvDy) * weights.z;
    mask = SAMPLE_TEXTURE2D_GRAD(_MaskMap, sampler_NormalMap, uvA, dxA, dyA) * weights.x +
        SAMPLE_TEXTURE2D_GRAD(_MaskMap, sampler_NormalMap, uvB, dxB, dyB) * weights.y +
        SAMPLE_TEXTURE2D_GRAD(_MaskMap, sampler_NormalMap, uvC, dxC, dyC) * weights.z;
#else
    color = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, uv);
    normalTS = UnpackNormalScale(SAMPLE_TEXTURE2D(_NormalMap, sampler_NormalMap, uv), _NormalStrength);
    mask = SAMPLE_TEXTURE2D(_MaskMap, sampler_NormalMap, uv);
#endif
    albedo = color.rgb * _BaseColor.rgb;
    height = color.a;
    smoothness = saturate(mask.a + _SmoothnessOffset);
    occlusion = mask.g;
    metallic = mask.r * _Metallic;
}

// One wear layer. The noise sets its weight. The detail normal replaces part of the asphalt normal, and its slope
// darkens the albedo by the tint. The anti-tiling cells hide the repetition of the detail normal map.
void RoadApplyWear(TEXTURE2D_PARAM(normalMap, normalSampler), float2 positionXZ, RoadCells cells, float size,
    half noise, half amount, half contrast, half normalStrength, half smoothnessChange, half occlusionChange,
    half3 tint, inout half3 albedo, inout half3 normalTS, inout half smoothness, inout half occlusion)
{
    half weight = amount * saturate(lerp(0.5h, noise, contrast));
    half3 wear = RoadSampleDetailNormal(TEXTURE2D_ARGS(normalMap, normalSampler), positionXZ / size, cells);
    half slope = saturate(1.0h - wear.z) * 4.0h;
    albedo = lerp(albedo, albedo * tint * 2.0h, saturate(weight * slope));
    normalTS = lerp(normalTS, wear, saturate(normalStrength * weight));
    smoothness += weight * smoothnessChange;
    occlusion += weight * occlusionChange;
}

// The road surface at one pixel, with the normal in world space.
void RoadSurface(Varyings input, out half3 albedo, out half3 normalWS, out half smoothness, out half occlusion,
    out half metallic)
{
    float2 positionXZ = input.positionWS.xz;
    half3 normalTS;
    RoadCells cells = RoadGetCells(positionXZ);
    half asphaltHeight;
    RoadSampleAsphalt(positionXZ, cells, albedo, asphaltHeight, normalTS, smoothness, occlusion, metallic);

    half4 noise = SAMPLE_TEXTURE2D(_NoiseMap, sampler_NoiseMap, positionXZ / _NoiseSize);
#if defined(_WEAR_A)
    RoadApplyWear(TEXTURE2D_ARGS(_WearNormalA, sampler_WearNormalA), positionXZ, cells, _WearSizeA, noise.r,
        _WearAmountA, _WearContrastA, _WearNormalStrengthA, _WearSmoothnessA, _WearOcclusionA, _WearTintA.rgb, albedo,
        normalTS, smoothness, occlusion);
#endif
#if defined(_WEAR_B)
    RoadApplyWear(TEXTURE2D_ARGS(_WearNormalB, sampler_WearNormalB), positionXZ, cells, _WearSizeB, noise.g,
        _WearAmountB, _WearContrastB, _WearNormalStrengthB, _WearSmoothnessB, _WearOcclusionB, _WearTintB.rgb, albedo,
        normalTS, smoothness, occlusion);
#endif
#if defined(_PATCHES) || defined(_ROAD_WEAR)
    // The second asphalt: noise patches, and the new asphalt of the road wear repairs.
    float2 patchUV = positionXZ / _PatchSize;
    half4 patchColor = SAMPLE_TEXTURE2D(_PatchMap, sampler_NormalMap, patchUV);
    half3 patchNormal = UnpackNormal(SAMPLE_TEXTURE2D(_PatchNormal, sampler_NormalMap, patchUV));
    half4 patchMask = SAMPLE_TEXTURE2D(_PatchMask, sampler_NormalMap, patchUV);
#endif
#if defined(_PATCHES)
    half spread = SAMPLE_TEXTURE2D(_NoiseMap, sampler_NoiseMap, positionXZ / _PatchSpread).g;
    half cover = saturate(patchColor.a * spread * _PatchAmount);
    albedo = lerp(albedo, patchColor.rgb, cover);
    normalTS = lerp(normalTS, patchNormal, cover);
    smoothness = lerp(smoothness, patchMask.a, cover);
    occlusion = lerp(occlusion, patchMask.g, cover);
    metallic = lerp(metallic, patchMask.r * _Metallic, cover);
#endif
#if defined(_ROAD_WEAR)
    // Large brightness and smoothness changes over 30 to 150 m, from the blue noise channel at two sizes.
    half macroA = SAMPLE_TEXTURE2D(_NoiseMap, sampler_NoiseMap, positionXZ / 37.0).b;
    half macroB = SAMPLE_TEXTURE2D(_NoiseMap, sampler_NoiseMap, positionXZ.yx / 151.0).b;
    half macro = (macroA + macroB) - 0.62h;
    albedo *= 1.0h + _MacroVariation * macro * 2.0h;
    smoothness += _MacroVariation * macro;
    // Old asphalt oxidizes to a lighter gray. The age varies with the large noise, and repairs stay new. The detail
    // is the brightness relative to the mean of the texture (its last mip level).
    half age = saturate(_Age * (1.0h + macro * 0.6h));
    half3 average = SAMPLE_TEXTURE2D_LOD(_BaseMap, sampler_BaseMap, float2(0.5, 0.5), 16.0).rgb * _BaseColor.rgb;
    half detail = Luminance(albedo) / max(Luminance(average), 1e-3h);
    albedo = lerp(albedo, _AgeColor.rgb * detail, age);

    half4 fine = SAMPLE_TEXTURE2D(_NoiseMap, sampler_NoiseMap, positionXZ / 3.0);
    half tracks, oil, dust;
    float edge;
    float2 edgeNormal;
    RoadWear wear = RoadEvaluateWear(input.road, input.joins.x, positionXZ, noise, fine, tracks, oil, dust, edge,
        edgeNormal);
    // The outer part of the lip is always ground, so it meets the terrain without a line. In the band border the
    // ground fills the low parts of the asphalt first, so the border is grainy and not a soft gradient.
    dust = max(dust, half(smoothstep(0.35, 0.65, input.joins.y)));
    dust = saturate(dust + dust * (1.0h - dust) * (0.5h - asphaltHeight) * 3.0h);
    // Repairs: newer asphalt with few marks of traffic. Each repair has aged part of the way.
    half3 repairColor = patchColor.rgb * _BaseColor.rgb;
    half3 repairAverage = SAMPLE_TEXTURE2D_LOD(_PatchMap, sampler_NormalMap, float2(0.5, 0.5), 16.0).rgb *
        _BaseColor.rgb;
    half repairDetail = Luminance(repairColor) / max(Luminance(repairAverage), 1e-3h);
    repairColor = lerp(repairColor, _AgeColor.rgb * repairDetail, age * wear.repairAge);
    albedo = lerp(albedo, repairColor, wear.repair);
    normalTS = lerp(normalTS, patchNormal, wear.repair);
    smoothness = lerp(smoothness, saturate(patchMask.a + _SmoothnessOffset), wear.repair);
    occlusion = lerp(occlusion, patchMask.g, wear.repair);
    tracks *= 1.0h - 0.6h * wear.repair;
    // Polished wheel tracks, oil down the lane centers, dust along the edges.
    albedo *= 1.0h - _TrackDarken * tracks;
    smoothness += _TrackSmoothness * tracks;
    albedo *= 1.0h - _OilDarken * oil;
    smoothness += 0.5h * _TrackSmoothness * oil;
    // Dark, rough cracks. Cracks are shaded and dusty, so they reflect little of the sky.
    albedo *= 1.0h - _CrackDarken * wear.crack;
    occlusion *= 1.0h - 0.9h * wear.crack;
    smoothness *= 1.0h - 0.8h * wear.crack;
    // The ravelled rims show lighter, rough aggregate. Sunken blocks hold a little dirt and shade. Keep it small:
    // more darkening shows as dark patches.
    albedo *= 1.0h + _RimBrighten * wear.rim;
    smoothness *= 1.0h - 0.6h * wear.rim;
    albedo *= 1.0h - 0.07h * wear.sunken;
    occlusion *= 1.0h - 0.12h * wear.sunken;
    // Glossy tar seals.
    albedo = lerp(albedo, _SealColor.rgb, wear.seal * _SealColor.a);
    smoothness = lerp(smoothness, _SealSmoothness, wear.seal);
    // The crack grooves and block tilts bend the normal. The tangent frame follows world X and Z.
    normalTS.xy -= half2(wear.slope);

    // The edge band: the ground beside the road creeps onto it.
    half groundCover = 0.0h;
#if defined(_GROUND_TERRAIN)
    float3 positionDx = ddx(input.positionWS);
    float3 positionDy = ddy(input.positionWS);
    [branch]
    if (dust > 0.001h && _GroundLayerCount > 0.5)
    {
        // The layers of the terrain just outside the slab edge, so the band shows the ground that the road stands
        // on. The textures stay at the pixel, so they do not stretch across the band. On the rounded lip the mesh
        // normal points outward. Down the lip the layers move to the pixel itself, so where the lip meets the ground
        // they are the layers of the terrain there and no line shows.
        float2 inward = edgeNormal;
        float2 outward = input.normalWS.xz;
        if (dot(inward, inward) < 0.5 && dot(outward, outward) > 1e-6)
        {
            inward = -normalize(outward);
        }
        float2 controlPosition = positionXZ - inward * (max(edge, 0.0) + _GroundReach);
        controlPosition = lerp(controlPosition, positionXZ, smoothstep(0.3, 1.0, input.joins.y));
        half3 groundAlbedo;
        half3 groundNormal;
        half groundSmoothness;
        LoogaSampleTerrainGround(input.positionWS, controlPosition, positionDx, positionDy, half3(0.0h, 1.0h, 0.0h),
            groundAlbedo, groundNormal, groundSmoothness);
        albedo = lerp(albedo, groundAlbedo * _GroundColor.rgb, dust);
        normalTS = lerp(normalTS, groundNormal.xzy, dust);
        smoothness = lerp(smoothness, groundSmoothness, dust);
        occlusion = lerp(occlusion, 1.0h, dust);
        groundCover = 1.0h;
    }
#endif
    // Without terrain ground, the band takes the edge color.
    albedo = lerp(albedo, _EdgeColor.rgb, dust * _EdgeColor.a * (1.0h - groundCover));
    smoothness = lerp(smoothness, smoothness * 0.5h, dust * (1.0h - groundCover));
#endif
    albedo = saturate(albedo);
    smoothness = saturate(smoothness);
    occlusion = saturate(occlusion);

    // The slab UVs follow world X and Z, so the mesh tangent frame matches the projection from above.
    half3 bitangentWS = input.tangentWS.w * cross(input.normalWS, input.tangentWS.xyz);
    half3x3 tangentToWorld = half3x3(input.tangentWS.xyz, bitangentWS, input.normalWS);
    normalWS = NormalizeNormalPerPixel(TransformTangentToWorld(normalize(normalTS), tangentToWorld));
}

void RoadForwardFragment(Varyings input, out half4 outColor : SV_Target0
#ifdef _WRITE_RENDERING_LAYERS
    , out uint outRenderingLayers : SV_Target1
#endif
)
{
    UNITY_SETUP_INSTANCE_ID(input);
    float3 positionWS = input.positionWS;
    half3 albedo;
    half3 normalWS;
    half smoothness;
    half occlusion;
    half metallic;
    RoadSurface(input, albedo, normalWS, smoothness, occlusion, metallic);

    SurfaceData surface = (SurfaceData)0;
    surface.albedo = albedo;
    surface.metallic = metallic;
    surface.smoothness = smoothness;
    surface.occlusion = occlusion;
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
    inputData.bakedGI = SAMPLE_GI(input.vertexSH, GetAbsolutePositionWS(positionWS), normalWS,
        inputData.viewDirectionWS, input.positionCS.xy, input.probeOcclusion, inputData.shadowMask);
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
