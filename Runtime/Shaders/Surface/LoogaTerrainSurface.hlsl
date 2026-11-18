#ifndef LOOGA_TERRAIN_SURFACE_INCLUDED
#define LOOGA_TERRAIN_SURFACE_INCLUDED

// Surface function of the Looga terrain surface. This file has no pass code. A shader target fills
// LoogaSurfaceInput, calls EvaluateLoogaTerrainSurface and writes LoogaSurfaceOutput to its own pass outputs.
// Stages in order: control-map reader (with control UV noise), triplanar projections (LOOGA_SURFACE_TRIPLANAR,
// slope-gated), layer sampling per projection (with stochastic sampling and distance resample), per-layer adjustments
// and noise height, blend, detail, distance and normal noises (on the same projections), world colour map and ground
// cover, debug views.
// Without LOOGA_SURFACE_HEIGHT_BLEND (linear blend, plain triplanar) each layer samples albedo and material in one pass
// and the projections run in a loop. The height blend of layers or projections needs the heights first, so it samples
// the albedo of all layers before the material.
// The surface shades every distance live: there is no far-mode cache (user decision 2026-10-15).

#include "Packages/com.loogasoft.loogaterrain/Runtime/Shaders/Surface/LoogaTerrainSurfaceInput.hlsl"
#include "Packages/com.loogasoft.loogaterrain/Runtime/Shaders/Surface/LoogaTerrainSurfaceWorld.hlsl"

// The derivatives come from the pass, outside all branches. A pass can then evaluate the surface inside a branch, for
// example where a cache page does not cover a pixel.
struct LoogaSurfaceInput
{
    float3 positionWS;
    float3 normalWS;       // Smooth terrain normal, normalized
    float2 controlUV;      // Terrain UV, 0 to 1 over the tile
    float cameraDistance;
    float curvature;       // Curvature of the terrain heights for the far detail (1 / m, positive in hollows), or 0
    float3 positionDx;     // Screen derivatives of positionWS
    float3 positionDy;
    float2 controlDx;      // Screen derivatives of controlUV
    float2 controlDy;
};

struct LoogaSurfaceOutput
{
    half3 albedo;
    float3 normalWS;
    half smoothness;
    half occlusion;
    half metallic;
    half height;
    half sunVisibility; // Main light visibility under a plant canopy, 1 = full main light.
    bool debug;
    half3 debugColor;
    // Cache pages only (LOOGA_SURFACE_VT_PAGE): x ground cover layer strength, y soil luminance ratio / 3, z 0, w canopy
    // detail luminance offset (LoogaCanopyDetailOffsetRange). The region colour map and the ground cover of a page pixel
    // are applied per screen pixel.
    // BC3 shares the RGB indices of a block, so z stays constant and the noisy offset uses the alpha channel.
    half4 pageCover;
};

// The strongest layers of a pixel, strongest first.
struct LoogaLayerSelection
{
    int4 indices;
    float4 weights;
};

// Triplanar projections of a pixel. Index 0 projects along the world X axis (side), 1 along Y (top), 2 along Z
// (side). The side projections flip their U axis by the sign of the normal, so a texture is not mirrored on opposite
// faces. Each projection is linear in the world position, so its UV and derivatives come from the position and its
// derivatives, and the sign changes do not give mip spikes. Only the position data is kept: the projection UVs are
// computed where they are used, which keeps the register count low.
// Without LOOGA_SURFACE_TRIPLANAR the shader compiles only the top projection. With it, the projection loops that can
// stay dynamic are real loops: three unrolled copies of the projection code made the variant slower on every pixel,
// also where only the top projection has weight.
#if defined(LOOGA_SURFACE_TRIPLANAR)
    #define LOOGA_PROJECTION_FIRST 0
    #define LOOGA_PROJECTION_LAST 2
    #define LOOGA_PROJECTION_LOOP [loop]
#else
    #define LOOGA_PROJECTION_FIRST 1
    #define LOOGA_PROJECTION_LAST 1
    #define LOOGA_PROJECTION_LOOP [unroll]
#endif

struct LoogaTriplanar
{
    half3 weights;           // Sum 1. On gentle slopes only the top projection has weight.
    float3 positionWS;
    float3 positionDx;
    float3 positionDy;
    float signX;             // Sign of the weight normal on X and Z: the U direction of the side projections
    float signZ;
};

// UV, derivatives and the world directions of the texture U and V axes of one layer on one projection.
struct LoogaProjection
{
    float2 uv;
    float2 dx;
    float2 dy;
    float3 tangentWS;
    float3 bitangentWS;
};

// One layer of a pixel between the albedo stage and the normal stage. The weights are packed (10 bits each), because
// with the height blend this state of up to four layers is kept until the normal stage.
struct LoogaLayerSample
{
    int layer;
    half4 albedo;              // rgb colour, a adjusted height
    half interpolationContrast;
    uint projectionWeights;    // Packed. Height-blended triplanar changes the weights per layer.
    uint3 stochasticWeights;   // Packed final stochastic weights of the X, top and Z projections
};

uint PackLoogaWeights(float3 weights)
{
    uint3 quantized = (uint3)round(saturate(weights) * 1023.0);
    return quantized.x | (quantized.y << 10) | (quantized.z << 20);
}

half3 UnpackLoogaWeights(uint packed)
{
    return (half3)(float3(packed & 1023u, (packed >> 10) & 1023u, packed >> 20) * (1.0 / 1023.0));
}

float4 LoadLoogaLayer(int layer, int row)
{
    return LOAD_TEXTURE2D(_LoogaLayerProps, int2(layer, row));
}

// ---------------------------------------------------------------------------------------------------------------
// Procedural noise for the anti-tiling features. The hash reads integer cells, so the noise keeps its shape far from
// the world origin. A sine hash of large world coordinates gives straight-edged blocks.
// ---------------------------------------------------------------------------------------------------------------

uint2 LoogaPcg2(uint2 value)
{
    value = value * 1664525u + 1013904223u;
    value.x += value.y * 1664525u;
    value.y += value.x * 1664525u;
    value ^= value >> 16u;
    value.x += value.y * 1664525u;
    value.y += value.x * 1664525u;
    value ^= value >> 16u;
    return value;
}

float2 LoogaNoiseDirection(float2 cell)
{
    uint2 hash = LoogaPcg2(asuint((int2)cell));
    return float2(hash) * (2.0 / 4294967295.0) - 1.0;
}

// Gradient noise in about the -0.7 to 0.7 range.
float LoogaGradientNoise(float2 position)
{
    float2 cell = floor(position);
    float2 local = position - cell;
    float2 fade = local * local * (3.0 - 2.0 * local);
    float n00 = dot(LoogaNoiseDirection(cell), local);
    float n10 = dot(LoogaNoiseDirection(cell + float2(1, 0)), local - float2(1, 0));
    float n01 = dot(LoogaNoiseDirection(cell + float2(0, 1)), local - float2(0, 1));
    float n11 = dot(LoogaNoiseDirection(cell + float2(1, 1)), local - float2(1, 1));
    return lerp(lerp(n00, n10, fade.x), lerp(n01, n11, fade.x), fade.y);
}

// Three octaves with the octave steps of the MicroSplat FBM, so imported amplitudes keep their meaning.
float LoogaFbm(float2 position)
{
    float value = 0.5 * LoogaGradientNoise(position);
    position *= 2.01;
    value += 0.25 * LoogaGradientNoise(position);
    position *= 1.96;
    value += 0.125 * LoogaGradientNoise(position);
    return value;
}

// ---------------------------------------------------------------------------------------------------------------
// Control-map reader. This is the only function that knows the splat format. Index and weight maps can replace it
// later without changes to the other stages.
// ---------------------------------------------------------------------------------------------------------------

float4 SampleLoogaControl(int control, float2 uv, float2 dx, float2 dy)
{
    switch (control)
    {
        case 0: return SAMPLE_TEXTURE2D_GRAD(_Control0, sampler_LinearClamp, uv, dx, dy);
        case 1: return SAMPLE_TEXTURE2D_GRAD(_Control1, sampler_LinearClamp, uv, dx, dy);
        case 2: return SAMPLE_TEXTURE2D_GRAD(_Control2, sampler_LinearClamp, uv, dx, dy);
        case 3: return SAMPLE_TEXTURE2D_GRAD(_Control3, sampler_LinearClamp, uv, dx, dy);
        case 4: return SAMPLE_TEXTURE2D_GRAD(_Control4, sampler_LinearClamp, uv, dx, dy);
        case 5: return SAMPLE_TEXTURE2D_GRAD(_Control5, sampler_LinearClamp, uv, dx, dy);
        case 6: return SAMPLE_TEXTURE2D_GRAD(_Control6, sampler_LinearClamp, uv, dx, dy);
        default: return SAMPLE_TEXTURE2D_GRAD(_Control7, sampler_LinearClamp, uv, dx, dy);
    }
}

// Inserts one layer into the sorted selection. On equal weights the later layer wins, as in the native terrain order.
void InsertLoogaLayer(inout LoogaLayerSelection selection, int layer, float weight)
{
    if (weight >= selection.weights.x)
    {
        selection.weights = float4(weight, selection.weights.xyz);
        selection.indices = int4(layer, selection.indices.xyz);
    }
    else if (weight >= selection.weights.y)
    {
        selection.weights.yzw = float3(weight, selection.weights.yz);
        selection.indices.yzw = int3(layer, selection.indices.yz);
    }
    else if (weight >= selection.weights.z)
    {
        selection.weights.zw = float2(weight, selection.weights.z);
        selection.indices.zw = int2(layer, selection.indices.z);
    }
    else if (weight >= selection.weights.w)
    {
        selection.weights.w = weight;
        selection.indices.w = layer;
    }
}

// The control-map sample UV and derivatives of a terrain UV, with the control UV noise.
void GetLoogaControlSampleUV(float2 controlUV, float2 controlDx, float2 controlDy, float2 positionXZ,
    out float2 uv, out float2 dx, out float2 dy)
{
    // Control UV noise: a small world-space offset makes the layer edges follow an organic line instead of the
    // control-map texels. The offset fades out within two texels of the tile border, where the neighbour tile reads
    // its own control map, so the edges stay continuous over the border.
    UNITY_BRANCH
    if (_LoogaControlNoise.y > 0)
    {
        float2 noisePosition = positionXZ * _LoogaControlNoise.x;
        float2 offset = float2(LoogaGradientNoise(noisePosition), LoogaGradientNoise(noisePosition + float2(31.7, 17.3)));
        float2 texel = 1.0 / max(_LoogaControlTexelSize.zw - 1.0, 1.0);
        float2 edge = min(controlUV, 1.0 - controlUV) / (2.0 * texel);
        float edgeFade = saturate(min(edge.x, edge.y));
        // The noise peaks at about 0.7, so the factor 1.4 makes the strength the largest offset in meters.
        controlUV += offset * (1.4 * _LoogaControlNoise.y) * _LoogaTerrainSize.zw * edgeFade;
    }

    // Map the 0 to 1 terrain UV to the texel centres at the tile borders, as the native terrain shader does.
    float2 controlScale = (_LoogaControlTexelSize.zw - 1.0) * _LoogaControlTexelSize.xy;
    uv = (controlUV * (_LoogaControlTexelSize.zw - 1.0) + 0.5) * _LoogaControlTexelSize.xy;
    dx = controlDx * controlScale;
    dy = controlDy * controlScale;
}

LoogaLayerSelection ReadLoogaControlMaps(float2 controlUV, float2 controlDx, float2 controlDy, float2 positionXZ,
    inout uint sampleCount)
{
    float2 uv;
    float2 dx;
    float2 dy;
    GetLoogaControlSampleUV(controlUV, controlDx, controlDy, positionXZ, uv, dx, dy);

    LoogaLayerSelection selection;
    selection.indices = 0;
    selection.weights = 0;
    UNITY_BRANCH
    if (_LoogaControlPacked.x > 0)
    {
        // The packed maps have the size of the control maps, so the sampler UV is the same.
        int mip = GetLoogaPackedMip(dx, dy, _LoogaControlTexelSize.zw, _LoogaControlPacked.y);
        LoogaPackedFootprint footprint = LoadLoogaPackedFootprint(
            TEXTURE2D_ARGS(_LoogaControlIndex, sampler_PointClamp),
            TEXTURE2D_ARGS(_LoogaControlWeight, sampler_PointClamp), uv, _LoogaControlTexelSize.zw, mip);
        // 8 texel loads.
        sampleCount += 8;
        SelectLoogaPackedLayers(footprint, selection.indices, selection.weights);
    }
    else
    {
        int controls = (int)_LoogaControlCount;
        [unroll]
        for (int control = 0; control < LOOGA_SURFACE_MAX_CONTROLS; control++)
        {
            UNITY_BRANCH
            if (control < controls)
            {
                float4 weights = SampleLoogaControl(control, uv, dx, dy);
                sampleCount++;
                InsertLoogaLayer(selection, control * 4 + 0, weights.x);
                InsertLoogaLayer(selection, control * 4 + 1, weights.y);
                InsertLoogaLayer(selection, control * 4 + 2, weights.z);
                InsertLoogaLayer(selection, control * 4 + 3, weights.w);
            }
        }
    }

    // A pixel with no weight shows the first layer instead of black.
    if (selection.weights.x <= 0)
    {
        selection.weights = float4(1, 0, 0, 0);
        selection.indices = 0;
    }
    int layersPerPixel = (int)_LoogaSurfaceBlend.y;
    if (layersPerPixel < 4)
    {
        selection.weights.w = 0;
    }
    if (layersPerPixel < 3)
    {
        selection.weights.z = 0;
    }
    selection.weights /= max(dot(selection.weights, 1.0), 1e-5);
    return selection;
}

// ---------------------------------------------------------------------------------------------------------------
// Triplanar projections.
// ---------------------------------------------------------------------------------------------------------------

// The normal for the projection weights. Near sharp slope changes the smooth terrain normal bends over the change,
// so the side projection fades in too early. The face normal of the triangle follows the geometry exactly.
// The normal for the projection weights. Near sharp slope changes the smooth terrain normal bends over the change,
// so the side projection fades in too early. The face normal of the triangle follows the geometry exactly.
float3 LoogaWeightNormal(float3 normalWS, float3 positionDx, float3 positionDy)
{
    // _LoogaTriplanar2.z: normal difference (1 - cos) where the face normal starts to replace the smooth normal; 1 or
    // more turns the face normal off. _LoogaTriplanar2.w: difference range of the replacement.
    if (_LoogaTriplanar2.z >= 1.0) return normalWS;
    float3 faceNormal = SafeNormalize(cross(positionDy, positionDx));
    faceNormal = dot(faceNormal, normalWS) < 0 ? -faceNormal : faceNormal;
    float difference = 1.0 - dot(normalWS, faceNormal);
    float blend = saturate((difference - _LoogaTriplanar2.z) / max(_LoogaTriplanar2.w, 1e-4));
    return SafeNormalize(lerp(normalWS, faceNormal, blend));
}

half3 LoogaTriplanarWeights(float3 normal)
{
    float3 weights = pow(abs(normal), _LoogaTriplanar.y);
    weights /= max(weights.x + weights.y + weights.z, 1e-5);
    // Slope gate: below the start slope only the top projection, so most ground needs one sample set. The side
    // weights fade in over the gate range. _LoogaTriplanar.z and w are the cosines of the start and end slopes.
    float gate = saturate((_LoogaTriplanar.z - abs(normal.y)) / max(_LoogaTriplanar.z - _LoogaTriplanar.w, 1e-4));
    weights = lerp(float3(0, 1, 0), weights, gate);
    // A projection below 1 % costs samples but does not show.
    weights = weights > 0.01 ? weights : 0;
    return (half3)(weights / max(weights.x + weights.y + weights.z, 1e-5));
}

LoogaTriplanar PrepareLoogaTriplanar(float3 positionWS, float3 positionDx, float3 positionDy, float3 normalWS)
{
    LoogaTriplanar triplanar;
    triplanar.positionWS = positionWS;
    triplanar.positionDx = positionDx;
    triplanar.positionDy = positionDy;
    #if defined(LOOGA_SURFACE_TRIPLANAR)
        float3 weightNormal = LoogaWeightNormal(normalWS, positionDx, positionDy);
        triplanar.weights = LoogaTriplanarWeights(weightNormal);
        triplanar.signX = weightNormal.x < 0 ? -1.0 : 1.0;
        triplanar.signZ = weightNormal.z < 0 ? -1.0 : 1.0;
    #else
        triplanar.weights = half3(0, 1, 0);
        triplanar.signX = 1.0;
        triplanar.signZ = 1.0;
    #endif
    return triplanar;
}

// The weight of a projection for a loop index. A dynamic index into a vector would need indexable registers.
half LoogaProjectionWeight(half3 weights, int projection)
{
    return projection == 0 ? weights.x : projection == 1 ? weights.y : weights.z;
}

// The first and the last projection with weight. A pixel with only the top projection runs one loop iteration.
void GetLoogaProjectionRange(half3 weights, out int first, out int last)
{
    #if defined(LOOGA_SURFACE_TRIPLANAR)
        first = weights.x > 0 ? 0 : 1;
        last = weights.z > 0 ? 2 : 1;
    #else
        first = 1;
        last = 1;
    #endif
}

// Unit directions of the noise U and V axes of a projection, and the surface axes with the surface UV scale. The
// surface UV scale can mirror the top projection (MicroSplat maps the top with -x). The sides use the size of the
// scale only, as MicroSplat does.
void GetLoogaProjectionAxes(LoogaTriplanar triplanar, int projection, out float3 noiseU, out float3 noiseV,
    out float3 axisU, out float3 axisV)
{
    GetLoogaSurfaceProjectionAxes(projection, projection == 0 ? triplanar.signX : triplanar.signZ, _LoogaSurfaceUV.xy,
        noiseU, noiseV, axisU, axisV);
}

// The layer UV of a projection: surface UV, then the layer scale and offset, then the layer rotation.
LoogaProjection GetLoogaLayerProjection(LoogaTriplanar triplanar, int projection, float4 uvRow, float2x2 rotation)
{
    LoogaProjection result;
    GetLoogaSurfaceLayerUV(triplanar.positionWS, triplanar.positionDx, triplanar.positionDy, projection,
        projection == 0 ? triplanar.signX : triplanar.signZ, _LoogaSurfaceUV, uvRow, rotation, result.uv, result.dx,
        result.dy, result.tangentWS, result.bitangentWS);
    return result;
}

// Height-blended triplanar: the projection with the higher texture height wins, as in MicroSplat.
half3 LoogaTriplanarHeightWeights(half3 weights, half3 heights)
{
    float3 blended = heights + weights * 3.0;
    float start = max(max(blended.x, blended.y), blended.z) - _LoogaTriplanar2.y;
    float3 result = max(blended - start, 0.0) * (weights > 0);
    return (half3)(result / max(result.x + result.y + result.z, 1e-5));
}

// ---------------------------------------------------------------------------------------------------------------
// Stochastic sampling: a triangle grid in UV space gives each cell a random UV offset. Three samples blend over
// each triangle, so the texture repetition becomes invisible (Heitz and Neyret 2018, Deliot and Heitz 2019).
// ---------------------------------------------------------------------------------------------------------------

float2 LoogaStochasticHash(float2 cell)
{
    return frac(sin(mul(float2x2(127.1, 311.7, 269.5, 183.3), cell)) * 43758.5453);
}

void PrepareLoogaStochastic(float2 uv, out float2 uv0, out float2 uv1, out float2 uv2, out half3 weights)
{
    // The cell edge is 1 / (2 sqrt 3 x cell scale) texture repeats.
    float2 grid = uv * (3.4641016 * _LoogaSurfaceStochastic.y);
    float2 skewed = mul(float2x2(1.0, 0.0, -0.57735027, 1.15470054), grid);
    float2 cell = floor(skewed);
    float3 local = float3(frac(skewed), 0);
    local.z = 1.0 - local.x - local.y;
    float2 vertex0;
    float2 vertex1;
    float2 vertex2;
    if (local.z > 0.0)
    {
        weights = half3(local.z, local.y, local.x);
        vertex0 = cell;
        vertex1 = cell + float2(0, 1);
        vertex2 = cell + float2(1, 0);
    }
    else
    {
        weights = half3(-local.z, 1.0 - local.y, 1.0 - local.x);
        vertex0 = cell + float2(1, 1);
        vertex1 = cell + float2(1, 0);
        vertex2 = cell + float2(0, 1);
    }
    uv0 = uv + LoogaStochasticHash(vertex0);
    uv1 = uv + LoogaStochasticHash(vertex1);
    uv2 = uv + LoogaStochasticHash(vertex2);

    // A low contrast keeps more samples. The remap removes samples whose weight is too small to see.
    half contrast = (half)_LoogaSurfaceStochastic.z;
    half floorWeight = min(0.0h, contrast - 0.45h);
    weights = saturate(lerp(floorWeight.xxx, 1.0h, weights));
}

// Blends the three stochastic samples by weight and height, so the higher texture features win at the cell edges.
half3 LoogaStochasticBlendWeights(half3 weights, half3 heights, half contrast)
{
    half3 weighted = weights * (heights + 1.0h / 1024.0h);
    half maximum = max(weighted.x, max(weighted.y, weighted.z));
    half transition = max(contrast * maximum, 1e-4h);
    weighted = saturate((weighted - (maximum - transition)) / transition);
    return weighted / max(weighted.x + weighted.y + weighted.z, 1e-4h);
}

// ---------------------------------------------------------------------------------------------------------------
// Texture packing. Both layouts store albedo in rgb and height in alpha of the albedo array.
// Compact layout (DXT5): normal array r smoothness, g normal y, b occlusion, a normal x. No metallic map.
// Quality layout (LOOGA_SURFACE_PACK_QUALITY): normal array BC5 r normal x, g normal y. Mask array BC7 r smoothness,
// g occlusion, b metallic.
// ---------------------------------------------------------------------------------------------------------------

// Material data of one layer sample. The normal is tangent-space x and y in the -1 to 1 range.
struct LoogaMaterialData
{
    half2 normalXY;
    half smoothness;
    half occlusion;
    half metallic;
};

half4 SampleLoogaAlbedo(float2 uv, int layer, float2 dx, float2 dy)
{
    return SAMPLE_TEXTURE2D_ARRAY_GRAD(_LoogaAlbedoArray, sampler_LoogaAlbedoArray, uv, layer, dx, dy);
}

LoogaMaterialData SampleLoogaMaterial(float2 uv, int layer, float2 dx, float2 dy, inout uint sampleCount)
{
    LoogaMaterialData data;
    half4 normal = SAMPLE_TEXTURE2D_ARRAY_GRAD(_LoogaNormalArray, sampler_LoogaNormalArray, uv, layer, dx, dy);
    #if defined(LOOGA_SURFACE_PACK_QUALITY)
        half4 mask = SAMPLE_TEXTURE2D_ARRAY_GRAD(_LoogaMaskArray, sampler_LoogaNormalArray, uv, layer, dx, dy);
        sampleCount += 2;
        DecodeLoogaSurfaceMaterial(normal, mask, true, data.normalXY, data.smoothness, data.occlusion, data.metallic);
    #else
        sampleCount++;
        DecodeLoogaSurfaceMaterial(normal, 0, false, data.normalXY, data.smoothness, data.occlusion, data.metallic);
    #endif
    return data;
}

void AddLoogaMaterial(inout LoogaMaterialData total, LoogaMaterialData data, half weight)
{
    total.normalXY += data.normalXY * weight;
    total.smoothness += data.smoothness * weight;
    total.occlusion += data.occlusion * weight;
    total.metallic += data.metallic * weight;
}

// ---------------------------------------------------------------------------------------------------------------
// Distance resample: a second sample at a larger UV scale fades in with distance, so the repetition of the layer
// textures is not visible on far ground.
// ---------------------------------------------------------------------------------------------------------------

// The fade of the second sample for a pixel, before the per-layer strength. 0 = no resample.
float LoogaResampleFade(float cameraDistance, float2 positionXZ)
{
    if (_LoogaResampleBlend.x <= 0 && _LoogaResampleBlend.y <= 0) return 0;
    int mode = (int)_LoogaResample.w;
    if (mode == 2) return 1;
    float fade = saturate((cameraDistance - _LoogaResample.y) / max(_LoogaResample.z - _LoogaResample.y, 0.001));
    UNITY_BRANCH
    if (mode == 1 && fade > 0)
    {
        // Noise fade: the far sample shows in irregular patches, which hides the second repetition too.
        fade *= saturate(1.0 + LoogaFbm(positionXZ * _LoogaResampleBlend.w) * 2.0 * _LoogaResampleNoise.x);
    }
    return fade;
}

half3 LoogaResampleAlbedo(half3 nearColor, half3 farColor, half blend)
{
    int mode = (int)_LoogaResampleBlend.z;
    half3 combined = farColor;
    if (mode == 1)
    {
        // Overlay keeps the near colour and adds the large-scale light and dark variation of the far sample.
        combined = nearColor < 0.5h ? 2.0h * nearColor * farColor : 1.0h - 2.0h * (1.0h - nearColor) * (1.0h - farColor);
    }
    else if (mode == 2)
    {
        combined = dot(farColor, 1.0h) > dot(nearColor, 1.0h) ? farColor : nearColor;
    }
    return lerp(nearColor, combined, blend);
}

// ---------------------------------------------------------------------------------------------------------------
// Layer sampling and per-layer adjustments. Each projection with weight samples the layer; stochastic sampling and
// the distance resample work inside each projection.
// ---------------------------------------------------------------------------------------------------------------

// Per-layer values that every projection of a layer uses.
struct LoogaLayerSetup
{
    int layer;
    bool stochastic;
    float4 uvRow;
    float2x2 rotation;
    float resampleScale;
    half resampleAlbedoBlend;  // 0 = no albedo resample
    half resampleNormalBlend;  // 0 = no normal resample
};

// Weighted sum of the normal and material data of the projections of one layer.
struct LoogaLayerMaterial
{
    float3 gradient;           // Surface gradient before the normal strength of the layer
    half smoothness;
    half occlusion;
    half metallic;
};

LoogaLayerSetup GetLoogaLayerSetup(int layer, float resampleFade)
{
    LoogaLayerSetup setup;
    setup.layer = layer;
    setup.uvRow = LoadLoogaLayer(layer, LOOGA_LAYER_ROW_UV);
    float4 adjustRow = LoadLoogaLayer(layer, LOOGA_LAYER_ROW_ADJUST);
    float4 noiseRow2 = LoadLoogaLayer(layer, LOOGA_LAYER_ROW_NOISE2);
    setup.stochastic = _LoogaSurfaceStochastic.x > 0.5 && adjustRow.w > 0.5;
    setup.rotation = LoogaLayerRotation(adjustRow.z);
    // Distance resample: the per-layer strength and UV scale. The blend is the same on every projection.
    setup.resampleScale = _LoogaResample.x * noiseRow2.w;
    half resampleBlend = (half)(resampleFade * noiseRow2.z);
    setup.resampleAlbedoBlend = resampleBlend * (half)_LoogaResampleBlend.x;
    setup.resampleNormalBlend = resampleBlend * (half)_LoogaResampleBlend.y;
    return setup;
}

// Samples the albedo of one layer on one projection, with stochastic sampling when the layer uses it, and the
// distance resample. The final stochastic weights are an output, so the material samples use the same blend.
half4 SampleLoogaProjectionAlbedo(LoogaLayerSetup setup, LoogaProjection uvs, out half3 stochasticWeights,
    inout uint sampleCount)
{
    half4 albedo = 0;
    stochasticWeights = 0;
    UNITY_BRANCH
    if (setup.stochastic)
    {
        float2 uv0;
        float2 uv1;
        float2 uv2;
        half3 weights;
        PrepareLoogaStochastic(uvs.uv, uv0, uv1, uv2, weights);
        half4 sample0 = 0;
        half4 sample1 = 0;
        half4 sample2 = 0;
        UNITY_BRANCH
        if (weights.x > 0.001h)
        {
            sample0 = SampleLoogaAlbedo(uv0, setup.layer, uvs.dx, uvs.dy);
            sampleCount++;
        }
        UNITY_BRANCH
        if (weights.y > 0.001h)
        {
            sample1 = SampleLoogaAlbedo(uv1, setup.layer, uvs.dx, uvs.dy);
            sampleCount++;
        }
        UNITY_BRANCH
        if (weights.z > 0.001h)
        {
            sample2 = SampleLoogaAlbedo(uv2, setup.layer, uvs.dx, uvs.dy);
            sampleCount++;
        }
        stochasticWeights = LoogaStochasticBlendWeights(weights, half3(sample0.a, sample1.a, sample2.a),
            (half)_LoogaSurfaceStochastic.z);
        albedo = sample0 * stochasticWeights.x + sample1 * stochasticWeights.y + sample2 * stochasticWeights.z;
    }
    else
    {
        albedo = SampleLoogaAlbedo(uvs.uv, setup.layer, uvs.dx, uvs.dy);
        sampleCount++;
    }

    // Distance resample before the colour adjustments, so both samples get the same adjustments.
    UNITY_BRANCH
    if (setup.resampleAlbedoBlend > 0)
    {
        half4 farSample = SampleLoogaAlbedo(uvs.uv * setup.resampleScale, setup.layer, uvs.dx * setup.resampleScale,
            uvs.dy * setup.resampleScale);
        sampleCount++;
        albedo.rgb = LoogaResampleAlbedo(albedo.rgb, farSample.rgb, setup.resampleAlbedoBlend);
    }
    return albedo;
}

// Adds the weighted normal and material data of one layer on one projection. The stochastic weights come from the
// albedo sample of the same projection.
void AddLoogaProjectionMaterial(inout LoogaLayerMaterial material, LoogaLayerSetup setup, LoogaProjection uvs,
    half3 stochasticWeights, half weight, inout uint sampleCount)
{
    LoogaMaterialData data = (LoogaMaterialData)0;
    UNITY_BRANCH
    if (setup.stochastic)
    {
        // The stochastic UVs are cheap to compute again.
        float2 uvs3[3];
        half3 unused;
        PrepareLoogaStochastic(uvs.uv, uvs3[0], uvs3[1], uvs3[2], unused);
        [unroll]
        for (int index = 0; index < 3; index++)
        {
            UNITY_BRANCH
            if (stochasticWeights[index] > 0.001h)
            {
                AddLoogaMaterial(data, SampleLoogaMaterial(uvs3[index], setup.layer, uvs.dx, uvs.dy, sampleCount),
                    stochasticWeights[index]);
            }
        }
    }
    else
    {
        data = SampleLoogaMaterial(uvs.uv, setup.layer, uvs.dx, uvs.dy, sampleCount);
    }

    UNITY_BRANCH
    if (setup.resampleNormalBlend > 0)
    {
        LoogaMaterialData farData = SampleLoogaMaterial(uvs.uv * setup.resampleScale, setup.layer,
            uvs.dx * setup.resampleScale, uvs.dy * setup.resampleScale, sampleCount);
        half blend = setup.resampleNormalBlend;
        data.normalXY = lerp(data.normalXY, farData.normalXY, blend);
        data.smoothness = lerp(data.smoothness, farData.smoothness, blend);
        data.occlusion = lerp(data.occlusion, farData.occlusion, blend);
        data.metallic = lerp(data.metallic, farData.metallic, blend);
    }

    // Surface gradient of the projection (Mikkelsen 2020).
    half3 normalTS = half3(data.normalXY, sqrt(saturate(1.0h - dot(data.normalXY, data.normalXY))));
    float2 derivative = -normalTS.xy / max(normalTS.z, 1.0 / 128.0);
    material.gradient += (derivative.x * uvs.tangentWS + derivative.y * uvs.bitangentWS) * weight;
    material.smoothness += data.smoothness * weight;
    material.occlusion += data.occlusion * weight;
    material.metallic += data.metallic * weight;
}

// Colour adjustments in this order: saturation, tint, contrast, brightness. Then the height adjustments. The rows
// load here, after the samples, so they do not hold registers during the sampling.
half4 AdjustLoogaLayerAlbedo(int layer, half4 albedo, float3 positionWS)
{
    float4 colorRow = LoadLoogaLayer(layer, LOOGA_LAYER_ROW_COLOR);
    float4 adjustRow = LoadLoogaLayer(layer, LOOGA_LAYER_ROW_ADJUST);
    float4 heightRow = LoadLoogaLayer(layer, LOOGA_LAYER_ROW_HEIGHT);
    albedo.rgb = AdjustLoogaSurfaceLayerColor(albedo.rgb, colorRow, adjustRow, (half3)_LoogaSurfaceLuminance.rgb);

    // Height offset and contrast decide which layer wins a height blend.
    albedo.a = saturate((albedo.a - 0.5h) * (half)heightRow.y + 0.5h + (half)heightRow.x);

    // Noise height makes height-blend transitions irregular. Each layer reads the noise at its own offset, so the
    // edges of two layers do not follow the same line.
    UNITY_BRANCH
    if (_LoogaNoiseHeight.y > 0)
    {
        float amplitude = _LoogaNoiseHeight.y * LoadLoogaLayer(layer, LOOGA_LAYER_ROW_NOISE2).y;
        float noise = LoogaFbm(positionWS.xz * _LoogaNoiseHeight.x + layer * float2(17.31, 23.17));
        albedo.a = saturate(albedo.a + (half)(noise * amplitude));
    }
    return albedo;
}

// Adds the material of one layer with its blend weight to the output.
void AddLoogaLayerMaterial(inout LoogaSurfaceOutput output, inout float3 surfaceGradient, LoogaLayerMaterial material,
    int layer, half blendWeight, float3 normalWS)
{
    float4 materialRow = LoadLoogaLayer(layer, LOOGA_LAYER_ROW_MATERIAL);
    float3 gradient = material.gradient * materialRow.x;
    surfaceGradient += (gradient - dot(normalWS, gradient) * normalWS) * blendWeight;
    output.smoothness += saturate(material.smoothness + (half)materialRow.y) * blendWeight;
    output.occlusion += pow(max(material.occlusion, 1e-4h), (half)materialRow.z) * blendWeight;
    output.metallic += saturate(material.metallic + (half)materialRow.w) * blendWeight;
}

// Linear blend: samples the albedo and the material of one layer in one pass and adds them with the blend weight.
// No state is kept between the layers, and the projections run in a loop, so the triplanar variant needs few
// registers and little code. Plain triplanar only: the height-blended triplanar mode uses the height blend path.
void SampleLoogaLayer(int layer, half blendWeight, LoogaTriplanar triplanar, float resampleFade, float3 normalWS,
    inout half4 albedoHeight, inout LoogaSurfaceOutput output, inout float3 surfaceGradient, inout uint sampleCount)
{
    LoogaLayerSetup setup = GetLoogaLayerSetup(layer, resampleFade);
    half4 albedo = 0;
    LoogaLayerMaterial material = (LoogaLayerMaterial)0;
    int first;
    int last;
    GetLoogaProjectionRange(triplanar.weights, first, last);
    LOOGA_PROJECTION_LOOP
    for (int projection = first; projection <= last; projection++)
    {
        half weight = LoogaProjectionWeight(triplanar.weights, projection);
        UNITY_BRANCH
        if (weight > 0)
        {
            LoogaProjection uvs = GetLoogaLayerProjection(triplanar, projection, setup.uvRow, setup.rotation);
            half3 stochasticWeights;
            albedo += SampleLoogaProjectionAlbedo(setup, uvs, stochasticWeights, sampleCount) * weight;
            AddLoogaProjectionMaterial(material, setup, uvs, stochasticWeights, weight, sampleCount);
        }
    }
    albedoHeight += AdjustLoogaLayerAlbedo(layer, albedo, triplanar.positionWS) * blendWeight;
    AddLoogaLayerMaterial(output, surfaceGradient, material, layer, blendWeight, normalWS);
}

// Height blend, first stage: the albedo of one projection of a layer. The stochastic weights are packed for the
// second stage.
half4 SampleLoogaLayerProjectionAlbedo(LoogaLayerSetup setup, LoogaTriplanar triplanar, int projection,
    out uint packedStochasticWeights, inout uint sampleCount)
{
    LoogaProjection uvs = GetLoogaLayerProjection(triplanar, projection, setup.uvRow, setup.rotation);
    half3 stochasticWeights;
    half4 albedo = SampleLoogaProjectionAlbedo(setup, uvs, stochasticWeights, sampleCount);
    packedStochasticWeights = PackLoogaWeights(stochasticWeights);
    return albedo;
}

// Height blend, first stage: the adjusted albedo and height of one layer.
LoogaLayerSample SampleLoogaLayerAlbedo(int layer, LoogaTriplanar triplanar, float resampleFade, inout uint sampleCount)
{
    LoogaLayerSetup setup = GetLoogaLayerSetup(layer, resampleFade);
    LoogaLayerSample result = (LoogaLayerSample)0;
    result.layer = layer;

    // Three variables, not an array: the compiler keeps a conditionally written array in indexable registers.
    half3 projectionWeights = triplanar.weights;
    half4 albedo0 = 0;
    half4 albedo1 = 0;
    half4 albedo2 = 0;
    #if defined(LOOGA_SURFACE_TRIPLANAR)
        UNITY_BRANCH
        if (projectionWeights.x > 0)
        {
            albedo0 = SampleLoogaLayerProjectionAlbedo(setup, triplanar, 0, result.stochasticWeights.x, sampleCount);
        }
        UNITY_BRANCH
        if (projectionWeights.z > 0)
        {
            albedo2 = SampleLoogaLayerProjectionAlbedo(setup, triplanar, 2, result.stochasticWeights.z, sampleCount);
        }
    #endif
    UNITY_BRANCH
    if (projectionWeights.y > 0)
    {
        albedo1 = SampleLoogaLayerProjectionAlbedo(setup, triplanar, 1, result.stochasticWeights.y, sampleCount);
    }

    #if defined(LOOGA_SURFACE_TRIPLANAR)
        // Height-blended triplanar: per layer, because the heights belong to the layer.
        if (_LoogaTriplanar2.x > 0.5 && projectionWeights.y < 1.0h)
        {
            projectionWeights = LoogaTriplanarHeightWeights(projectionWeights, half3(albedo0.a, albedo1.a, albedo2.a));
        }
        half4 albedo = albedo0 * projectionWeights.x + albedo1 * projectionWeights.y + albedo2 * projectionWeights.z;
    #else
        half4 albedo = albedo1;
    #endif
    result.projectionWeights = PackLoogaWeights(projectionWeights);
    result.albedo = AdjustLoogaLayerAlbedo(layer, albedo, triplanar.positionWS);
    result.interpolationContrast = (half)LoadLoogaLayer(layer, LOOGA_LAYER_ROW_HEIGHT).z;
    return result;
}

// Height blend, second stage: adds the material of one layer after the blend weights are known.
void SampleLoogaLayerMaterial(LoogaLayerSample layer, half blendWeight, LoogaTriplanar triplanar, float resampleFade,
    float3 normalWS, inout LoogaSurfaceOutput output, inout float3 surfaceGradient, inout uint sampleCount)
{
    LoogaLayerSetup setup = GetLoogaLayerSetup(layer.layer, resampleFade);
    LoogaLayerMaterial material = (LoogaLayerMaterial)0;
    #if defined(LOOGA_SURFACE_TRIPLANAR)
        half3 projectionWeights = UnpackLoogaWeights(layer.projectionWeights);
    #else
        half3 projectionWeights = half3(0, 1, 0);
    #endif
    [unroll]
    for (int projection = LOOGA_PROJECTION_FIRST; projection <= LOOGA_PROJECTION_LAST; projection++)
    {
        half weight = projectionWeights[projection];
        UNITY_BRANCH
        if (weight > 0)
        {
            LoogaProjection uvs = GetLoogaLayerProjection(triplanar, projection, setup.uvRow, setup.rotation);
            AddLoogaProjectionMaterial(material, setup, uvs, UnpackLoogaWeights(layer.stochasticWeights[projection]),
                weight, sampleCount);
        }
    }
    AddLoogaLayerMaterial(output, surfaceGradient, material, layer.layer, blendWeight, normalWS);
}

// ---------------------------------------------------------------------------------------------------------------
// Blend.
// ---------------------------------------------------------------------------------------------------------------

// Height blend: the layer with the higher texture height wins. The contrast sets the width of the transition.
float4 LoogaHeightWeights(float4 weights, float4 heights, float contrast)
{
    float4 weighted = weights * max(heights, 0.001);
    float maximum = max(max(weighted.x, weighted.y), max(weighted.z, weighted.w));
    float transition = max(contrast * maximum, 1e-4);
    weighted = saturate((weighted - (maximum - transition)) / transition);
    return weighted / max(dot(weighted, 1.0), 1e-5);
}

// Offset blend (Mishkinis 2013): the score is the weight plus a height term, not the weight times the height. A layer
// can win only where its weight is less than the height reach below the other weight, so no layer shows far inside
// another layer. The band is the score difference over which the transition goes from one layer to the other.
float4 LoogaOffsetWeights(float4 weights, float4 scores, float band)
{
    // A slot without weight never wins.
    scores = weights > 0 ? scores : -4.0;
    float maximum = max(max(scores.x, scores.y), max(scores.z, scores.w));
    float4 result = max(scores - (maximum - band), 0.0);
    return result / max(dot(result, 1.0), 1e-5);
}

// Crisp edge noise of one layer: two octaves of world noise in about the -0.5 to 0.5 range. Each layer reads the
// noise at its own offset. An octave fades out when its features are smaller than four footprints, so it does not
// shimmer and a cache page shows the same noise as the screen at the same footprint.
float LoogaEdgeNoise(float2 positionXZ, int layer, float footprint)
{
    float size = 1.0 / max(_LoogaSurfaceEdge.y, 1e-4);
    float2 position = positionXZ * _LoogaSurfaceEdge.y + layer * float2(17.31, 23.17);
    float noise = 0.7 * LoogaGradientNoise(position) * saturate(size / (2.0 * footprint) - 1.0);
    noise += 0.35 * LoogaGradientNoise(position * 2.01 + float2(5.3, 9.1))
        * saturate(size / (4.02 * footprint) - 1.0);
    return noise;
}

// Difference of the weights of two layers in the control maps, relative to their sum (-1 to 1).
float LoogaControlPairDifference(int layerA, int layerB, float2 controlUV, float2 controlDx, float2 controlDy,
    float2 positionXZ)
{
    float2 uv;
    float2 dx;
    float2 dy;
    GetLoogaControlSampleUV(controlUV, controlDx, controlDy, positionXZ, uv, dx, dy);
    float weightA;
    float weightB;
    UNITY_BRANCH
    if (_LoogaControlPacked.x > 0)
    {
        int mip = GetLoogaPackedMip(dx, dy, _LoogaControlTexelSize.zw, _LoogaControlPacked.y);
        LoogaPackedFootprint footprint = LoadLoogaPackedFootprint(
            TEXTURE2D_ARGS(_LoogaControlIndex, sampler_PointClamp),
            TEXTURE2D_ARGS(_LoogaControlWeight, sampler_PointClamp), uv, _LoogaControlTexelSize.zw, mip);
        weightA = GetLoogaPackedLayerWeight(footprint, layerA);
        weightB = GetLoogaPackedLayerWeight(footprint, layerB);
    }
    else
    {
        float4 mapA = SampleLoogaControl(layerA >> 2, uv, dx, dy);
        float4 mapB = mapA;
        UNITY_BRANCH
        if ((layerB >> 2) != (layerA >> 2))
        {
            mapB = SampleLoogaControl(layerB >> 2, uv, dx, dy);
        }
        weightA = mapA[layerA & 3];
        weightB = mapB[layerB & 3];
    }
    return (weightA - weightB) / max(weightA + weightB, 1e-5);
}

// Crisp edge blend: the offset blend with a world edge noise in the score, and a band that gives a transition of a
// set number of screen pixels at every distance. The band comes from the change of the score over one pixel between
// the two strongest layers. The surface can shade in divergent flow (cache misses), so the shader cannot use ddx on
// the score. It reads the control maps and the noise one pixel away instead. The texture heights are not in this
// change: their mips already follow the footprint, and the contrast is the smallest band.
float4 LoogaCrispEdgeWeights(LoogaLayerSelection selection, float4 heights, float minimumBand, LoogaSurfaceInput input,
    inout uint sampleCount)
{
    float4 weights = selection.weights;
    float noiseStrength = _LoogaSurfaceEdge.z;
    float footprint = sqrt(max(max(dot(input.positionDx, input.positionDx), dot(input.positionDy, input.positionDy)),
        1e-8));
    float2 positionXZ = input.positionWS.xz;
    float4 noise = 0;
    [unroll]
    for (int slot = 0; slot < 4; slot++)
    {
        UNITY_BRANCH
        if (noiseStrength > 0 && weights[slot] > 0)
        {
            noise[slot] = LoogaEdgeNoise(positionXZ, selection.indices[slot], footprint);
        }
    }
    float4 scores = weights + _LoogaSurfaceBlend.w * (heights - 0.5) + noiseStrength * noise;

    float band = minimumBand;
    UNITY_BRANCH
    if (weights.y > 0)
    {
        int layerA = selection.indices.x;
        int layerB = selection.indices.y;
        float2 offsetX = positionXZ + input.positionDx.xz;
        float2 offsetY = positionXZ + input.positionDy.xz;
        float center = LoogaControlPairDifference(layerA, layerB, input.controlUV, input.controlDx, input.controlDy,
            positionXZ) + noiseStrength * (noise.x - noise.y);
        float stepX = LoogaControlPairDifference(layerA, layerB, input.controlUV + input.controlDx, input.controlDx,
            input.controlDy, offsetX);
        float stepY = LoogaControlPairDifference(layerA, layerB, input.controlUV + input.controlDy, input.controlDx,
            input.controlDy, offsetY);
        sampleCount += 3;
        UNITY_BRANCH
        if (noiseStrength > 0)
        {
            stepX += noiseStrength * (LoogaEdgeNoise(offsetX, layerA, footprint) - LoogaEdgeNoise(offsetX, layerB, footprint));
            stepY += noiseStrength * (LoogaEdgeNoise(offsetY, layerA, footprint) - LoogaEdgeNoise(offsetY, layerB, footprint));
        }
        // The pair difference has the scale of the score difference of two normalized weights.
        float change = abs(stepX - center) + abs(stepY - center);
        // The transition covers a score difference of two bands.
        band = max(minimumBand, 0.5 * _LoogaSurfaceEdge.x * change);
    }
    return LoogaOffsetWeights(weights, scores, band);
}

float4 LoogaBlendWeights(LoogaLayerSelection selection, LoogaLayerSample layer0, LoogaLayerSample layer1,
    LoogaLayerSample layer2, LoogaLayerSample layer3, LoogaSurfaceInput input, inout uint sampleCount)
{
    float4 weights = selection.weights;
    float cameraDistance = input.cameraDistance;
    int mode = (int)_LoogaSurfaceBlend.x;
    // The variance-preserving mode blends linearly. It uses this path only with height-blended triplanar.
    if (mode == 0 || mode == 5) return weights;

    float4 heights = float4(layer0.albedo.a, layer1.albedo.a, layer2.albedo.a, layer3.albedo.a);
    // Crisp edge: the global contrast is the smallest band. The per-layer interpolation contrast is not used.
    UNITY_BRANCH
    if (mode == 4)
    {
        return LoogaCrispEdgeWeights(selection, heights, max(_LoogaSurfaceBlend.z, 0.0001), input, sampleCount);
    }
    float4 layerContrast = float4(layer0.interpolationContrast, layer1.interpolationContrast,
        layer2.interpolationContrast, layer3.interpolationContrast);
    float contrast = clamp(_LoogaSurfaceBlend.z + dot(weights, layerContrast), 0.0001, 1.0);
    if (mode == 3)
    {
        return LoogaOffsetWeights(weights, weights + _LoogaSurfaceBlend.w * (heights - 0.5), contrast);
    }
    float4 heightWeights = LoogaHeightWeights(weights, heights, contrast);
    if (mode == 2)
    {
        float fade = saturate((cameraDistance - _LoogaSurfaceHybrid.x)
            / max(_LoogaSurfaceHybrid.y - _LoogaSurfaceHybrid.x, 0.001));
        heightWeights = lerp(heightWeights, weights, fade);
    }
    return heightWeights;
}

// Variance-preserving linear blend (Heitz and Neyret 2018, Burley 2019). A linear blend of two textures has less
// contrast than each texture, so a soft transition shows a flat band. This scales the blended albedo away from the
// blended layer means by 1 / sqrt(sum of the squared weights). The layer gradients get the same scale. The mean of a
// layer is in the object row. A layer without a measured mean turns the scale off for the pixel.
void PreserveLoogaBlendVariance(inout half4 albedoHeight, inout float3 surfaceGradient, LoogaLayerSelection selection,
    float4 weights)
{
    float3 meanMix = 0;
    bool measured = true;
    [unroll]
    for (int slot = 0; slot < 4; slot++)
    {
        if (weights[slot] > 0)
        {
            float3 mean = LoadLoogaLayer(selection.indices[slot], LOOGA_LAYER_ROW_OBJECT).yzw;
            meanMix += mean * weights[slot];
            measured = measured && dot(mean, 1.0) > 0;
        }
    }
    if (measured)
    {
        float gain = min(rsqrt(max(dot(weights, weights), 1e-5)), _LoogaSurfaceEdge.w);
        albedoHeight.rgb = (half3)saturate(meanMix + (albedoHeight.rgb - meanMix) * gain);
        surfaceGradient *= gain;
    }
}

// ---------------------------------------------------------------------------------------------------------------
// Detail, distance and normal noises after the blend. The noise textures use the same projections as the layers.
// The per-layer strengths are blended by the layer weights, so for example a pavement layer can stay flat.
// ---------------------------------------------------------------------------------------------------------------

// The noise UV, derivatives and world axes of one projection.
void GetLoogaNoiseProjection(LoogaTriplanar triplanar, int projection, out float2 uv, out float2 dx, out float2 dy,
    out float3 axisU, out float3 axisV)
{
    float3 surfaceU;
    float3 surfaceV;
    GetLoogaProjectionAxes(triplanar, projection, axisU, axisV, surfaceU, surfaceV);
    uv = float2(dot(triplanar.positionWS, axisU), dot(triplanar.positionWS, axisV));
    dx = float2(dot(triplanar.positionDx, axisU), dot(triplanar.positionDx, axisV));
    dy = float2(dot(triplanar.positionDy, axisU), dot(triplanar.positionDy, axisV));
}

// Detail noise (near) and distance noise (far): rg normal offset, b luminance multiplier. Adds the normal offset at
// the fade strength to the gradient and returns the luminance. 0.5 is no change.
half SampleLoogaLuminanceNoise(inout float3 gradient, TEXTURE2D_PARAM(noiseTexture, noiseSampler),
    LoogaTriplanar triplanar, float uvPerMeter, float fade, inout uint sampleCount)
{
    half luminance = 0;
    int first;
    int last;
    GetLoogaProjectionRange(triplanar.weights, first, last);
    LOOGA_PROJECTION_LOOP
    for (int projection = first; projection <= last; projection++)
    {
        half weight = LoogaProjectionWeight(triplanar.weights, projection);
        UNITY_BRANCH
        if (weight > 0)
        {
            float2 uv;
            float2 dx;
            float2 dy;
            float3 axisU;
            float3 axisV;
            GetLoogaNoiseProjection(triplanar, projection, uv, dx, dy, axisU, axisV);
            half3 noise = SAMPLE_TEXTURE2D_GRAD(noiseTexture, noiseSampler, uv * uvPerMeter, dx * uvPerMeter,
                dy * uvPerMeter).rgb;
            sampleCount++;
            luminance += noise.b * weight;
            // Normal = N - gradient, so a positive offset tilts the normal towards the axis.
            float2 slope = (noise.rg * 0.5 - 0.25) * fade * weight;
            gradient -= slope.x * axisU + slope.y * axisV;
        }
    }
    return luminance;
}

// Multiplies the albedo by a noise luminance at the fade strength.
half3 ApplyLoogaNoiseLuminance(half3 albedo, half luminance, float fade)
{
    return lerp(albedo, albedo * luminance * 2.0h, (half)fade);
}

void ApplyLoogaLuminanceNoise(inout half3 albedo, inout float3 gradient, TEXTURE2D_PARAM(noiseTexture, noiseSampler),
    LoogaTriplanar triplanar, float uvPerMeter, float fade, inout uint sampleCount)
{
    half luminance = SampleLoogaLuminanceNoise(gradient, TEXTURE2D_ARGS(noiseTexture, noiseSampler), triplanar,
        uvPerMeter, fade, sampleCount);
    albedo = ApplyLoogaNoiseLuminance(albedo, luminance, fade);
}

// SampleLoogaLuminanceNoise with stochastic sampling. The far canopy covers wide even areas, where one repeated noise
// texture shows as a regular pattern.
half SampleLoogaStochasticLuminanceNoise(inout float3 gradient, TEXTURE2D_PARAM(noiseTexture, noiseSampler),
    LoogaTriplanar triplanar, float uvPerMeter, float fade, inout uint sampleCount)
{
    half luminance = 0;
    int first;
    int last;
    GetLoogaProjectionRange(triplanar.weights, first, last);
    LOOGA_PROJECTION_LOOP
    for (int projection = first; projection <= last; projection++)
    {
        half weight = LoogaProjectionWeight(triplanar.weights, projection);
        UNITY_BRANCH
        if (weight > 0)
        {
            float2 uv;
            float2 dx;
            float2 dy;
            float3 axisU;
            float3 axisV;
            GetLoogaNoiseProjection(triplanar, projection, uv, dx, dy, axisU, axisV);
            float2 uv0;
            float2 uv1;
            float2 uv2;
            half3 cellWeights;
            PrepareLoogaStochastic(uv * uvPerMeter, uv0, uv1, uv2, cellWeights);
            float2 gradientX = dx * uvPerMeter;
            float2 gradientY = dy * uvPerMeter;
            half3 noise0 = SAMPLE_TEXTURE2D_GRAD(noiseTexture, noiseSampler, uv0, gradientX, gradientY).rgb;
            half3 noise1 = SAMPLE_TEXTURE2D_GRAD(noiseTexture, noiseSampler, uv1, gradientX, gradientY).rgb;
            half3 noise2 = SAMPLE_TEXTURE2D_GRAD(noiseTexture, noiseSampler, uv2, gradientX, gradientY).rgb;
            sampleCount += 3;
            half3 blend = LoogaStochasticBlendWeights(cellWeights, half3(noise0.b, noise1.b, noise2.b),
                (half)_LoogaSurfaceStochastic.z);
            half3 noise = noise0 * blend.x + noise1 * blend.y + noise2 * blend.z;
            luminance += noise.b * weight;
            float2 slope = (noise.rg * 0.5 - 0.25) * fade * weight;
            gradient -= slope.x * axisU + slope.y * axisV;
        }
    }
    return luminance;
}

void ApplyLoogaNormalNoise(inout float3 gradient, TEXTURE2D_PARAM(noiseTexture, noiseSampler), LoogaTriplanar triplanar,
    float uvPerMeter, float strength, inout uint sampleCount)
{
    int first;
    int last;
    GetLoogaProjectionRange(triplanar.weights, first, last);
    LOOGA_PROJECTION_LOOP
    for (int projection = first; projection <= last; projection++)
    {
        half weight = LoogaProjectionWeight(triplanar.weights, projection);
        UNITY_BRANCH
        if (weight > 0)
        {
            float2 uv;
            float2 dx;
            float2 dy;
            float3 axisU;
            float3 axisV;
            GetLoogaNoiseProjection(triplanar, projection, uv, dx, dy, axisU, axisV);
            half4 packed = SAMPLE_TEXTURE2D_GRAD(noiseTexture, noiseSampler, uv * uvPerMeter, dx * uvPerMeter,
                dy * uvPerMeter);
            sampleCount++;
            float2 slope = UnpackNormal(packed).xy * strength * weight;
            gradient -= slope.x * axisU + slope.y * axisV;
        }
    }
}

// layerStrength: detail, distance, normal 1, normal 2. normal3Strength: normal 3. Returns the noise gradient.
float3 ApplyLoogaNoises(inout half3 albedo, LoogaTriplanar triplanar, float cameraDistance, float4 layerStrength,
    float normal3Strength, inout uint sampleCount)
{
    float3 gradient = 0;
    float detail = _LoogaDetailNoise.y * layerStrength.x;
    UNITY_BRANCH
    if (detail > 0 && cameraDistance < _LoogaDetailNoise.z)
    {
        float fade = saturate(cameraDistance / _LoogaDetailNoise.z);
        ApplyLoogaLuminanceNoise(albedo, gradient, TEXTURE2D_ARGS(_LoogaDetailNoiseTex, sampler_TrilinearRepeat),
            triplanar, _LoogaDetailNoise.x, (1.0 - fade * fade) * detail, sampleCount);
    }
    float distanceStrength = _LoogaDistanceNoise.y * layerStrength.y;
    UNITY_BRANCH
    if (distanceStrength > 0 && cameraDistance > _LoogaDistanceNoise.z)
    {
        float fade = saturate((cameraDistance - _LoogaDistanceNoise.z) / max(_LoogaDistanceNoise.w, 0.001));
        ApplyLoogaLuminanceNoise(albedo, gradient, TEXTURE2D_ARGS(_LoogaDistanceNoiseTex, sampler_TrilinearRepeat),
            triplanar, _LoogaDistanceNoise.x, fade * distanceStrength, sampleCount);
    }
    float normal1 = _LoogaNormalNoise1.y * layerStrength.z;
    UNITY_BRANCH
    if (normal1 > 0)
    {
        ApplyLoogaNormalNoise(gradient, TEXTURE2D_ARGS(_LoogaNormalNoiseTex1, sampler_TrilinearRepeat), triplanar,
            _LoogaNormalNoise1.x, normal1, sampleCount);
    }
    float normal2 = _LoogaNormalNoise2.y * layerStrength.w;
    UNITY_BRANCH
    if (normal2 > 0)
    {
        ApplyLoogaNormalNoise(gradient, TEXTURE2D_ARGS(_LoogaNormalNoiseTex2, sampler_TrilinearRepeat), triplanar,
            _LoogaNormalNoise2.x, normal2, sampleCount);
    }
    float normal3 = _LoogaNormalNoise3.y * normal3Strength;
    UNITY_BRANCH
    if (normal3 > 0)
    {
        ApplyLoogaNormalNoise(gradient, TEXTURE2D_ARGS(_LoogaNormalNoiseTex3, sampler_TrilinearRepeat), triplanar,
            _LoogaNormalNoise3.x, normal3, sampleCount);
    }
    return gradient;
}

// ---------------------------------------------------------------------------------------------------------------
// World colour map and ground cover. LoogaTerrainSurfaceGlobals binds the data. The settings of the surface are
// lighting rules: they decide how the ground shows the data, not where a colour or a plant is.
// ---------------------------------------------------------------------------------------------------------------

#include "Packages/com.loogasoft.loogaterrain/Runtime/Shaders/Surface/LoogaTerrainSurfaceCover.hlsl"

// Size ratio of the second canopy detail sample to the first. Not a whole number, so the two repeats do not line up.
static const float LoogaCanopyDetailOctave = 2.71;

// Pattern of the far plant canopy. The canopy colour and coverage maps have texels of a few metres, and at distance
// the soil texture under the canopy is a mip average, so the far cover reads as a flat colour. Two samples of the
// detail noise at plant patch and clump sizes give it the light and dark patches and the relief of plants seen from
// afar. The mipmaps fade each sample where its features get smaller than the pixels. canopy is the ground cover blend
// times its far share.
// The luminances of the two canopy detail samples. The gradient gets their relief at the given strength, before the
// relief scale.
half2 SampleLoogaCanopyDetail(inout float3 gradient, LoogaTriplanar triplanar, float strength, inout uint sampleCount)
{
    half2 luminance = 0.5h;
    // The coarse octave repeats every few meters and stays visible far away, so it takes stochastic sampling when the
    // surface uses it. The fine octave averages out within a pixel before its repetition can show.
    UNITY_BRANCH
    if (_LoogaSurfaceStochastic.x > 0.5)
    {
        luminance.x = SampleLoogaStochasticLuminanceNoise(gradient,
            TEXTURE2D_ARGS(_LoogaDetailNoiseTex, sampler_TrilinearRepeat), triplanar, _LoogaGroundCoverDetail.x,
            strength, sampleCount);
    }
    else
    {
        luminance.x = SampleLoogaLuminanceNoise(gradient, TEXTURE2D_ARGS(_LoogaDetailNoiseTex, sampler_TrilinearRepeat),
            triplanar, _LoogaGroundCoverDetail.x, strength, sampleCount);
    }
    luminance.y = SampleLoogaLuminanceNoise(gradient, TEXTURE2D_ARGS(_LoogaDetailNoiseTex, sampler_TrilinearRepeat),
        triplanar, _LoogaGroundCoverDetail.x * LoogaCanopyDetailOctave, strength * 0.6, sampleCount);
    return luminance;
}

// The light and dark patches of the canopy detail at a strength.
half3 ApplyLoogaCanopyDetailLuminance(half3 albedo, half2 luminance, float strength)
{
    albedo = ApplyLoogaNoiseLuminance(albedo, luminance.x, strength);
    return ApplyLoogaNoiseLuminance(albedo, luminance.y, strength * 0.6);
}

void ApplyLoogaCanopyDetail(inout half3 albedo, inout float3 surfaceGradient, float3 normalWS,
    LoogaTriplanar triplanar, half canopy, inout uint sampleCount)
{
    float strength = _LoogaGroundCoverDetail.y * canopy;
    UNITY_BRANCH
    if (strength <= 0.001 || _LoogaGroundCoverDetail.x <= 0) return;

    float3 gradient = 0;
    half2 luminance = SampleLoogaCanopyDetail(gradient, triplanar, strength, sampleCount);
    albedo = ApplyLoogaCanopyDetailLuminance(albedo, luminance, strength);
    gradient *= _LoogaGroundCoverDetail.z;
    surfaceGradient += gradient - dot(normalWS, gradient) * normalWS;
}

// ---------------------------------------------------------------------------------------------------------------
// Ground cover of a cached surface. The cover closes over the ground at low view angles, and the canopy shades the
// sun, so the cover depends on the view and the main light. A cache page stores the ground before the cover and the
// cover inputs that cost samples. The screen pixel applies the cover with the same functions as the direct surface.
// ---------------------------------------------------------------------------------------------------------------

// Range of the canopy detail offset (2 L1 - 1) + 0.6 (2 L2 - 1) of the two luminances L1 and L2.
static const half LoogaCanopyDetailOffsetRange = 3.2h;

// Page side. albedo and smoothness are before the cover. The function returns pageCover (LoogaSurfaceOutput) and adds
// the canopy relief of a top-down view to the surface gradient. The relief is divided by the top-down relief scale of
// the cover, because the screen pixel scales the full page relief by its own cover.
half4 BakeLoogaGroundCoverPage(half3 albedo, half smoothness, inout float3 surfaceGradient, float3 positionWS,
    float3 normalWS, float cameraDistance, float2 positionDx, float2 positionDy, LoogaTriplanar triplanar,
    half4 coverStrength, half soilLuminance, inout uint sampleCount)
{
    half soil = coverStrength.z > 0.001h ? soilLuminance / coverStrength.z
        : dot(albedo, LoogaLuminanceWeights) / (half)_LoogaGroundCoverSoil.x;
    // z holds the bare far cover of the layers.
    half4 page = half4(coverStrength.y, saturate(soil / 3.0h), saturate(coverStrength.w), 0.5h);
    UNITY_BRANCH
    if ((coverStrength.y > 0 || coverStrength.w > 0) && _LoogaGroundCoverDetail.x > 0 &&
        _LoogaGroundCoverDetail.y > 0)
    {
        LoogaWorldDebug debug = (LoogaWorldDebug)0;
        float3 unusedGradient = 0;
        ApplyLoogaGroundCoverView(albedo, unusedGradient, smoothness, positionWS, float3(0, 1, 0), cameraDistance,
            positionDx, positionDy, coverStrength.y, coverStrength.w, soilLuminance, coverStrength.z, debug);
        float3 gradient = 0;
        half2 luminance = SampleLoogaCanopyDetail(gradient, triplanar, 1.0, sampleCount);
        half offset = (2.0h * luminance.x - 1.0h) + 0.6h * (2.0h * luminance.y - 1.0h);
        page.w = saturate(offset / LoogaCanopyDetailOffsetRange + 0.5h);
        float strength = _LoogaGroundCoverDetail.y * debug.groundCover.x * debug.groundCover.y;
        float reliefScale = 1.0 - debug.groundCover.x * (1.0 - _LoogaGroundCoverSoil.z);
        gradient *= _LoogaGroundCoverDetail.z * strength / max(reliefScale, 0.05);
        surfaceGradient += gradient - dot(normalWS, gradient) * normalWS;
    }
    return page;
}

// Pixel side of the region colour map. The page stores the albedo before the map, and its soil luminance ratio is the
// layer detail of the map (the luminance relative to the layer mean). Apply it before the cached ground cover, as the
// direct surface does.
void ApplyLoogaCachedRegionColor(inout LoogaSurfaceOutput output, LoogaSurfaceInput input, half4 pageCover)
{
    ApplyLoogaRegionColorDetail(output.albedo, input.positionWS, input.cameraDistance, input.positionDx.xz,
        input.positionDy.xz, pageCover.y * 3.0h);
}

// Pixel side. output holds the page values: albedo, smoothness and occlusion before the cover, and the page normal.
// input.normalWS is the smooth terrain normal of the pixel.
void ApplyLoogaCachedGroundCover(inout LoogaSurfaceOutput output, LoogaSurfaceInput input, half4 pageCover)
{
    UNITY_BRANCH
    if ((_LoogaWorldColor2.x <= 0 && _LoogaGroundCover.x <= 0) || (pageCover.x <= 0 && pageCover.z <= 0)) return;
    LoogaWorldDebug debug = (LoogaWorldDebug)0;
    float3 unusedGradient = 0;
    // The page stores the soil luminance ratio, so the soil mean is 1.
    ApplyLoogaGroundCover(output.albedo, unusedGradient, output.smoothness, input.positionWS, input.cameraDistance,
        input.positionDx.xz, input.positionDy.xz, pageCover.x, pageCover.z, pageCover.y * 3.0h, 1.0h, debug);
    output.sunVisibility = LoogaGroundCanopySunVisibility(input.positionWS, input.cameraDistance, pageCover.x,
        pageCover.z, debug.farShade);
    ApplyLoogaFarCanopyLighting(debug.groundCover.x, debug.groundCover.y, output.occlusion);
    float strength = _LoogaGroundCoverDetail.y * debug.groundCover.x * debug.groundCover.y;
    if (_LoogaGroundCoverDetail.x > 0 && strength > 0.001)
    {
        // The product of the two direct factors without its small strength-squared term.
        half offset = (pageCover.w - 0.5h) * LoogaCanopyDetailOffsetRange;
        output.albedo *= (half)max(1.0 + strength * offset, 0.0);
    }
    // The cover flattens the soil relief. The page normal is N - gradient with the gradient normal to N, so the
    // gradient comes back from the page normal and the terrain normal.
    float3 gradient = input.normalWS - output.normalWS / max(dot(output.normalWS, input.normalWS), 0.05);
    gradient *= 1.0 - debug.groundCover.x * (1.0 - _LoogaGroundCoverSoil.z);
    output.normalWS = normalize(input.normalWS - gradient);
}

// ---------------------------------------------------------------------------------------------------------------
// Debug views.
// ---------------------------------------------------------------------------------------------------------------

half3 LoogaLayerDebugColor(int layer)
{
    static const half3 palette[16] =
    {
        half3(0.90, 0.10, 0.10), half3(0.10, 0.70, 0.10), half3(0.10, 0.30, 0.90), half3(0.95, 0.85, 0.10),
        half3(0.90, 0.10, 0.90), half3(0.10, 0.85, 0.85), half3(0.95, 0.55, 0.10), half3(0.55, 0.20, 0.90),
        half3(0.50, 0.90, 0.30), half3(0.95, 0.60, 0.70), half3(0.20, 0.45, 0.45), half3(0.65, 0.45, 0.25),
        half3(0.95, 0.95, 0.95), half3(0.45, 0.45, 0.45), half3(0.10, 0.10, 0.45), half3(0.45, 0.10, 0.10)
    };
    // Layers 16 to 31 repeat the palette at a lower brightness.
    return palette[layer & 15] * (layer >= 16 ? 0.55h : 1.0h);
}

half3 LoogaSampleCountColor(uint sampleCount)
{
    // Blue = few samples, green = about 12, red = 24 or more.
    half amount = saturate(sampleCount / 24.0h);
    return saturate(half3(amount * 2.0h - 1.0h, 1.0h - abs(amount * 2.0h - 1.0h), 1.0h - amount * 2.0h));
}

void ApplyLoogaSurfaceDebug(inout LoogaSurfaceOutput output, LoogaLayerSelection selection, float4 weights,
    half3 triplanarWeights, uint sampleCount, LoogaWorldDebug worldDebug)
{
    int view = (int)_LoogaSurfaceDebug.x;
    output.debug = view > 0;
    switch (view)
    {
        case LOOGA_SURFACE_DEBUG_ALBEDO:
            output.debugColor = output.albedo;
            break;
        case LOOGA_SURFACE_DEBUG_NORMAL:
            output.debugColor = output.normalWS * 0.5 + 0.5;
            break;
        case LOOGA_SURFACE_DEBUG_SMOOTHNESS_OCCLUSION:
            output.debugColor = half3(output.smoothness, output.occlusion, 0);
            break;
        case LOOGA_SURFACE_DEBUG_HEIGHT:
            output.debugColor = output.height.xxx;
            break;
        case LOOGA_SURFACE_DEBUG_LAYER_WEIGHTS:
            output.debugColor = LoogaLayerDebugColor(selection.indices.x) * weights.x
                + LoogaLayerDebugColor(selection.indices.y) * weights.y
                + LoogaLayerDebugColor(selection.indices.z) * weights.z
                + LoogaLayerDebugColor(selection.indices.w) * weights.w;
            break;
        case LOOGA_SURFACE_DEBUG_TOP_LAYER:
        {
            float strongest = max(max(weights.x, weights.y), max(weights.z, weights.w));
            int index = weights.x == strongest ? selection.indices.x : weights.y == strongest ? selection.indices.y
                : weights.z == strongest ? selection.indices.z : selection.indices.w;
            output.debugColor = LoogaLayerDebugColor(index);
            break;
        }
        case LOOGA_SURFACE_DEBUG_TRIPLANAR_WEIGHTS:
            // Red, green and blue are the X, Y (top) and Z projections.
            output.debugColor = triplanarWeights;
            break;
        case LOOGA_SURFACE_DEBUG_SAMPLE_COUNT:
            output.debugColor = LoogaSampleCountColor(sampleCount);
            break;
        case LOOGA_SURFACE_DEBUG_WORLD_COLOR:
            output.debugColor = worldDebug.worldColor;
            break;
        case LOOGA_SURFACE_DEBUG_GROUND_COVER:
            // Red = ground cover blend, green = far share, blue = bare cast.
            output.debugColor = worldDebug.groundCover;
            break;
        default:
            output.debugColor = 0;
            break;
    }
}

// ---------------------------------------------------------------------------------------------------------------
// Parallax occlusion mapping on the top projection. The view ray steps down into the height field of the strongest
// layer (albedo alpha, 1 = terrain surface, 0 = parallax depth below it). The hit point moves the world position of
// the projections in X and Z, so all layer, noise and normal samples show the ground features with depth. The offset
// fades with the top projection weight, with distance, and where the two strongest layers have similar weights,
// because the height field of one layer does not match a blend of layers.
// ---------------------------------------------------------------------------------------------------------------

// Texture height of a layer on the top projection. Stochastic layers use the cell offsets and weights of the pixel at
// every step, so the height field stays continuous along the ray.
half SampleLoogaParallaxHeight(int layer, float2 uv, LoogaProjection uvs, float2 offset0, float2 offset1,
    float2 offset2, half3 stochasticWeights, bool stochastic, inout uint sampleCount)
{
    half height = 0;
    UNITY_BRANCH
    if (stochastic)
    {
        UNITY_BRANCH
        if (stochasticWeights.x > 0.001h)
        {
            height += SampleLoogaAlbedo(uv + offset0, layer, uvs.dx, uvs.dy).a * stochasticWeights.x;
            sampleCount++;
        }
        UNITY_BRANCH
        if (stochasticWeights.y > 0.001h)
        {
            height += SampleLoogaAlbedo(uv + offset1, layer, uvs.dx, uvs.dy).a * stochasticWeights.y;
            sampleCount++;
        }
        UNITY_BRANCH
        if (stochasticWeights.z > 0.001h)
        {
            height += SampleLoogaAlbedo(uv + offset2, layer, uvs.dx, uvs.dy).a * stochasticWeights.z;
            sampleCount++;
        }
    }
    else
    {
        height = SampleLoogaAlbedo(uv, layer, uvs.dx, uvs.dy).a;
        sampleCount++;
    }
    return height;
}

// The world X and Z offset of the projections.
float2 LoogaParallaxOffset(LoogaTriplanar triplanar, LoogaLayerSelection selection, float cameraDistance,
    inout uint sampleCount)
{
    int layer = selection.indices.x;
    float depth = _LoogaParallax.x * triplanar.weights.y * LoadLoogaLayer(layer, LOOGA_LAYER_ROW_HEIGHT).w
        * (1.0 - smoothstep(_LoogaParallax.y, _LoogaParallax.z, cameraDistance))
        * saturate((selection.weights.x - selection.weights.y) * 4.0);
    float2 offset = 0;
    UNITY_BRANCH
    if (depth > 0.0005)
    {
        // The ray offset at the full depth. A minimum view elevation limits the offset at grazing angles.
        float3 view = normalize(triplanar.positionWS - GetCameraPositionWS());
        float elevation = max(-view.y, 0.25);
        float2 rayXZ = view.xz / elevation * depth;

        // The layer UV is linear in the world position, so the UV of the ray is linear in the step too.
        LoogaLayerSetup setup = GetLoogaLayerSetup(layer, 0);
        LoogaProjection uvs = GetLoogaLayerProjection(triplanar, 1, setup.uvRow, setup.rotation);
        LoogaTriplanar end = triplanar;
        end.positionWS.xz += rayXZ;
        float2 uvRay = GetLoogaLayerProjection(end, 1, setup.uvRow, setup.rotation).uv - uvs.uv;

        float2 offset0 = 0;
        float2 offset1 = 0;
        float2 offset2 = 0;
        half3 stochasticWeights = 0;
        UNITY_BRANCH
        if (setup.stochastic)
        {
            float2 uv0;
            float2 uv1;
            float2 uv2;
            PrepareLoogaStochastic(uvs.uv, uv0, uv1, uv2, stochasticWeights);
            stochasticWeights /= max(stochasticWeights.x + stochasticWeights.y + stochasticWeights.z, 1e-4h);
            offset0 = uv0 - uvs.uv;
            offset1 = uv1 - uvs.uv;
            offset2 = uv2 - uvs.uv;
        }

        // A steep view needs fewer steps. The ray and the surface depth are fractions of the full depth.
        int steps = (int)round(lerp(_LoogaParallax.w, 4.0, saturate((elevation - 0.25) / 0.75)));
        float stepSize = 1.0 / steps;
        float previousRay = 0;
        float previousSurface = 1.0 - SampleLoogaParallaxHeight(layer, uvs.uv, uvs, offset0, offset1, offset2,
            stochasticWeights, setup.stochastic, sampleCount);
        float hit = 1.0;
        [loop]
        for (int index = 1; index <= steps; index++)
        {
            float ray = index * stepSize;
            float surface = 1.0 - SampleLoogaParallaxHeight(layer, uvs.uv + uvRay * ray, uvs, offset0, offset1,
                offset2, stochasticWeights, setup.stochastic, sampleCount);
            if (ray >= surface)
            {
                // The hit is between the last two steps, where the ray crosses the linear surface between them. At
                // the surface top the first sample is already a hit.
                float before = max(previousSurface - previousRay, 0.0);
                float after = ray - surface;
                hit = lerp(previousRay, ray, before / max(before + after, 1e-5));
                break;
            }
            previousRay = ray;
            previousSurface = surface;
        }
        offset = rayXZ * hit;
    }
    return offset;
}

// ---------------------------------------------------------------------------------------------------------------
// Far detail.
// ---------------------------------------------------------------------------------------------------------------

// Slope of smoothstep(low, high, value) over value.
float LoogaSmoothstepSlope(float low, float high, float value)
{
    float t = saturate((value - low) / (high - low));
    return 6.0 * t * (1.0 - t) / (high - low);
}

// Lightness of one far strata band, -1 to 1. The band contrast changes along the band, so no band forms a ring.
float LoogaFarBand(float band, float2 positionXZ)
{
    uint hash = LoogaPcg2(uint2(asuint((int)band), 7919u)).x;
    float value = hash * (2.0 / 4294967295.0) - 1.0;
    float along = saturate(0.5 + 1.2 * LoogaGradientNoise(positionXZ / 240.0 + band * 3.17));
    return value * lerp(0.3, 1.0, along);
}

// Detail of the far ground, where the texture detail is smaller than a pixel. Steep ground gets coarse world strata:
// light and dark bands, with a ledge relief at the band edges. The curvature of the terrain shape darkens hollows and
// lightens crests. Both fade in from the far detail start distance.
void ApplyLoogaFarDetail(inout half3 albedo, inout float3 surfaceGradient, float3 positionWS, float3 normalWS,
    float cameraDistance, float curvature, float heightDx, float heightDy)
{
    float fade = saturate((cameraDistance - _LoogaFarDetail.x) * _LoogaFarDetail.y);
    UNITY_BRANCH
    if (fade <= 0.0) return;
    UNITY_BRANCH
    if (_LoogaFarCurvature.x > 0.0)
    {
        float shape = clamp(curvature / max(_LoogaFarCurvature.z, 1e-4), -1.0, 1.0);
        albedo *= (half)(1.0 - _LoogaFarCurvature.x * fade * shape);
    }
    float steep = saturate((_LoogaFarDetail.z - normalWS.y) / max(_LoogaFarDetail.z - _LoogaFarDetail.w, 1e-3));
    float strength = fade * steep;
    UNITY_BRANCH
    if (strength <= 0.0 || (_LoogaFarStrata.z <= 0.0 && _LoogaFarStrata.w <= 0.0)) return;
    float warp = LoogaGradientNoise(positionWS.xz / 140.0) * 1.4 * _LoogaFarStrata.y;
    float height = (positionWS.y + warp) * _LoogaFarStrata.x;
    float band = floor(height);
    float local = height - band;
    float here = LoogaFarBand(band, positionWS.xz);
    float below = LoogaFarBand(band - 1.0, positionWS.xz);
    float above = LoogaFarBand(band + 1.0, positionWS.xz);
    // Each band edge blends over at least about one pixel, so the edges do not alias. The slow warp adds almost
    // nothing to the screen change of the band height.
    float width = clamp((abs(heightDx) + abs(heightDy)) * _LoogaFarStrata.x * 1.5, 0.05, 0.5);
    float layer = here + (above - here) * 0.5 * smoothstep(1.0 - width, 1.0, local) +
        (below - here) * 0.5 * (1.0 - smoothstep(0.0, width, local));
    albedo *= (half)(1.0 + layer * _LoogaFarStrata.z * strength);
    // Ledges: the band lightness is also a relief height, so a lighter band stands out. Its slope across the band
    // edges tilts the normal: up on the top of a ledge, down under it.
    float rise = (above - here) * 0.5 * LoogaSmoothstepSlope(1.0 - width, 1.0, local) -
        (below - here) * 0.5 * LoogaSmoothstepSlope(0.0, width, local);
    float3 gradient = float3(0.0, rise * _LoogaFarStrata.x * _LoogaFarStrata.w * strength, 0.0);
    surfaceGradient += gradient - dot(normalWS, gradient) * normalWS;
}

// ---------------------------------------------------------------------------------------------------------------
// Surface fields.
// ---------------------------------------------------------------------------------------------------------------

// Texels per side of a field map. Keep this value the same as SurfaceFieldMaps.MapSize. The edge texels lie on the tile
// edge, so two tiles read the same values at their seam.
#define LOOGA_SURFACE_FIELD_SIZE 512.0

// Erosion variation from the field map of the tile: wet ground and channel floors are darker and smoother, convex rims
// are lighter, and concave feet take a deposit colour. A cached page and a direct pixel run the same code, so it comes
// before the ground cover. Returns the scale of the ground cover: the cover wears off on rims.
half ApplyLoogaSurfaceFields(inout half3 albedo, inout half smoothness, float2 uv, float2 uvDx, float2 uvDy,
    float normalY)
{
    half coverScale = 1.0h;
    UNITY_BRANCH
    if (_LoogaFieldFootRange.z > 0.5)
    {
        float scale = (LOOGA_SURFACE_FIELD_SIZE - 1.0) / LOOGA_SURFACE_FIELD_SIZE;
        half4 fields = (half4)SAMPLE_TEXTURE2D_GRAD(_LoogaSurfaceFields, sampler_LinearClamp,
            uv * scale + 0.5 / LOOGA_SURFACE_FIELD_SIZE, uvDx * scale, uvDy * scale);
        half2 relative = fields.gb * 2.0h - 1.0h;
        half wetness = (half)saturate((fields.r - _LoogaFieldWetness.z) * _LoogaFieldWetness.w);
        half channel = (half)saturate((fields.a - _LoogaFieldChannel.z) * _LoogaFieldChannel.w);
        // A wall has both signs of the relative height over its height, so rims and feet end on steep ground.
        half gentle = (half)saturate((normalY - _LoogaFieldFootRange.w) * 6.0);
        half rim = gentle * (half)saturate((max(relative.x, relative.y) - _LoogaFieldRim.z) * _LoogaFieldRim.w);
        half foot = gentle * (half)saturate((max(-relative.x, -relative.y) - _LoogaFieldFootRange.x)
            * _LoogaFieldFootRange.y);
        albedo = lerp(albedo, (half3)_LoogaFieldFoot.rgb, foot * (half)_LoogaFieldFoot.a);
        albedo *= (1.0h + rim * (half)_LoogaFieldRim.x) * (1.0h - wetness * (half)_LoogaFieldWetness.x)
            * (1.0h - channel * (half)_LoogaFieldChannel.x);
        half gain = saturate(wetness * (half)_LoogaFieldWetness.y + channel * (half)_LoogaFieldChannel.y);
        smoothness += (1.0h - smoothness) * gain;
        coverScale = 1.0h - rim * (half)_LoogaFieldRim.y;
    }
    return coverScale;
}

// ---------------------------------------------------------------------------------------------------------------
// Surface function.
// ---------------------------------------------------------------------------------------------------------------


LoogaSurfaceOutput EvaluateLoogaTerrainSurface(LoogaSurfaceInput input)
{
    uint sampleCount = 0;
    LoogaLayerSelection selection = ReadLoogaControlMaps(input.controlUV, input.controlDx, input.controlDy,
        input.positionWS.xz, sampleCount);

    // World-space projections make the texture continuous over tile borders.
    LoogaTriplanar triplanar = PrepareLoogaTriplanar(input.positionWS, input.positionDx, input.positionDy,
        input.normalWS);
    // A cache page has no view, so it has no parallax. Parallax ends close to the camera, where pixels shade directly.
    #if !defined(LOOGA_SURFACE_VT_PAGE)
        UNITY_BRANCH
        if (_LoogaParallax.x > 0)
        {
            triplanar.positionWS.xz += LoogaParallaxOffset(triplanar, selection, input.cameraDistance, sampleCount);
        }
    #endif
    float resampleFade = LoogaResampleFade(input.cameraDistance, input.positionWS.xz);

    LoogaSurfaceOutput output = (LoogaSurfaceOutput)0;
    output.sunVisibility = 1.0h;
    output.pageCover = half4(0.0h, 0.0h, 0.0h, 0.5h);
    half4 albedoHeight = 0;
    float3 surfaceGradient = 0;
    #if defined(LOOGA_SURFACE_HEIGHT_BLEND)
        // Height blend: the blend weights need the heights of all layers, so the albedo of every layer comes first.
        // The material reads come after the blend, so a layer that the height blend removes costs no material samples.
        LoogaLayerSample layer0 = SampleLoogaLayerAlbedo(selection.indices.x, triplanar, resampleFade, sampleCount);
        LoogaLayerSample layer1 = (LoogaLayerSample)0;
        LoogaLayerSample layer2 = (LoogaLayerSample)0;
        LoogaLayerSample layer3 = (LoogaLayerSample)0;
        UNITY_BRANCH
        if (selection.weights.y > 0)
        {
            layer1 = SampleLoogaLayerAlbedo(selection.indices.y, triplanar, resampleFade, sampleCount);
        }
        UNITY_BRANCH
        if (selection.weights.z > 0)
        {
            layer2 = SampleLoogaLayerAlbedo(selection.indices.z, triplanar, resampleFade, sampleCount);
        }
        UNITY_BRANCH
        if (selection.weights.w > 0)
        {
            layer3 = SampleLoogaLayerAlbedo(selection.indices.w, triplanar, resampleFade, sampleCount);
        }

        float4 weights = LoogaBlendWeights(selection, layer0, layer1, layer2, layer3, input, sampleCount);
        albedoHeight = layer0.albedo * weights.x + layer1.albedo * weights.y + layer2.albedo * weights.z
            + layer3.albedo * weights.w;
        UNITY_BRANCH
        if (weights.x > 0)
        {
            SampleLoogaLayerMaterial(layer0, weights.x, triplanar, resampleFade, input.normalWS, output,
                surfaceGradient, sampleCount);
        }
        UNITY_BRANCH
        if (weights.y > 0)
        {
            SampleLoogaLayerMaterial(layer1, weights.y, triplanar, resampleFade, input.normalWS, output,
                surfaceGradient, sampleCount);
        }
        UNITY_BRANCH
        if (weights.z > 0)
        {
            SampleLoogaLayerMaterial(layer2, weights.z, triplanar, resampleFade, input.normalWS, output,
                surfaceGradient, sampleCount);
        }
        UNITY_BRANCH
        if (weights.w > 0)
        {
            SampleLoogaLayerMaterial(layer3, weights.w, triplanar, resampleFade, input.normalWS, output,
                surfaceGradient, sampleCount);
        }
    #else
        // Linear blend: the weights are known before the sampling, so each layer samples its albedo and material in
        // one pass and adds its weighted result.
        float4 weights = selection.weights;
        [unroll]
        for (int slot = 0; slot < 4; slot++)
        {
            UNITY_BRANCH
            if (slot == 0 || weights[slot] > 0)
            {
                SampleLoogaLayer(selection.indices[slot], weights[slot], triplanar, resampleFade, input.normalWS,
                    albedoHeight, output, surfaceGradient, sampleCount);
            }
        }
    #endif
    UNITY_BRANCH
    if ((int)_LoogaSurfaceBlend.x == 5)
    {
        PreserveLoogaBlendVariance(albedoHeight, surfaceGradient, selection, weights);
    }
    output.albedo = albedoHeight.rgb;
    output.height = albedoHeight.a;

    float4 layerNoise = LoadLoogaLayer(selection.indices.x, LOOGA_LAYER_ROW_NOISE) * weights.x
        + LoadLoogaLayer(selection.indices.y, LOOGA_LAYER_ROW_NOISE) * weights.y
        + LoadLoogaLayer(selection.indices.z, LOOGA_LAYER_ROW_NOISE) * weights.z
        + LoadLoogaLayer(selection.indices.w, LOOGA_LAYER_ROW_NOISE) * weights.w;
    float normal3Strength = dot(weights, float4(LoadLoogaLayer(selection.indices.x, LOOGA_LAYER_ROW_NOISE2).x,
        LoadLoogaLayer(selection.indices.y, LOOGA_LAYER_ROW_NOISE2).x, LoadLoogaLayer(selection.indices.z, LOOGA_LAYER_ROW_NOISE2).x,
        LoadLoogaLayer(selection.indices.w, LOOGA_LAYER_ROW_NOISE2).x));
    float3 noiseGradient = ApplyLoogaNoises(output.albedo, triplanar, input.cameraDistance, layerNoise, normal3Strength,
        sampleCount);
    surfaceGradient += noiseGradient - dot(input.normalWS, noiseGradient) * input.normalWS;
    ApplyLoogaFarDetail(output.albedo, surfaceGradient, input.positionWS, input.normalWS, input.cameraDistance,
        input.curvature, input.positionDx.y, input.positionDy.y);
    half fieldCover = ApplyLoogaSurfaceFields(output.albedo, output.smoothness, input.controlUV, input.controlDx,
        input.controlDy, input.normalWS.y);

    // World colour map, region colour map, then ground cover. The layers scale the world colour map and the ground
    // cover with their own strengths. The region colour map applies to all layers.
    // A cache page always stores its cover inputs: the screen pixel applies the region colour map from the soil
    // luminance ratio of the page, so a map that is bound later needs no page refill.
    LoogaWorldDebug worldDebug = (LoogaWorldDebug)0;
    #if defined(LOOGA_SURFACE_VT_PAGE)
        const bool pageCoverInputs = true;
    #else
        const bool pageCoverInputs = false;
    #endif
    UNITY_BRANCH
    if (pageCoverInputs || _LoogaWorldColor2.x > 0 || _LoogaGroundCover.x > 0 || _LoogaRegionColorState.x > 0)
    {
        half4 coverStrength = (half4)(LoadLoogaLayer(selection.indices.x, LOOGA_LAYER_ROW_COVER) * weights.x
            + LoadLoogaLayer(selection.indices.y, LOOGA_LAYER_ROW_COVER) * weights.y
            + LoadLoogaLayer(selection.indices.z, LOOGA_LAYER_ROW_COVER) * weights.z
            + LoadLoogaLayer(selection.indices.w, LOOGA_LAYER_ROW_COVER) * weights.w);
        coverStrength.yw *= fieldCover;
        half soilLuminance = dot(output.albedo, LoogaLuminanceWeights);
        ApplyLoogaWorldColor(output.albedo, input.positionWS, input.normalWS, input.cameraDistance,
            triplanar.positionDx.xz, triplanar.positionDy.xz, coverStrength.x, worldDebug);
    #if defined(LOOGA_SURFACE_VT_PAGE)
        // The region colour map fades with the camera distance. A page has no camera: each mip would hold the map at
        // the strength of its own footprint, and a mip change or a missing page would shift the colour. The screen
        // pixel applies the map (ApplyLoogaCachedRegionColor).
    #else
        ApplyLoogaRegionColor(output.albedo, input.positionWS, input.cameraDistance, triplanar.positionDx.xz,
            triplanar.positionDy.xz, coverStrength.z);
    #endif
    #if defined(LOOGA_SURFACE_VT_PAGE)
        output.pageCover = BakeLoogaGroundCoverPage(output.albedo, output.smoothness, surfaceGradient, input.positionWS,
            input.normalWS, input.cameraDistance, triplanar.positionDx.xz, triplanar.positionDy.xz, triplanar,
            coverStrength, soilLuminance, sampleCount);
    #else
        ApplyLoogaGroundCover(output.albedo, surfaceGradient, output.smoothness, input.positionWS, input.cameraDistance,
            triplanar.positionDx.xz, triplanar.positionDy.xz, coverStrength.y, coverStrength.w, soilLuminance,
            coverStrength.z, worldDebug);
        output.sunVisibility = LoogaGroundCanopySunVisibility(input.positionWS, input.cameraDistance, coverStrength.y,
            coverStrength.w, worldDebug.farShade);
        ApplyLoogaFarCanopyLighting(worldDebug.groundCover.x, worldDebug.groundCover.y, output.occlusion);
        ApplyLoogaCanopyDetail(output.albedo, surfaceGradient, input.normalWS, triplanar,
            worldDebug.groundCover.x * worldDebug.groundCover.y, sampleCount);
    #endif
    }
    output.normalWS = normalize(input.normalWS - surfaceGradient);

    ApplyLoogaSurfaceDebug(output, selection, weights, triplanar.weights, sampleCount, worldDebug);
    return output;
}

#endif
