#ifndef LOOGA_TERRAIN_CONTROL_PACKED_INCLUDED
#define LOOGA_TERRAIN_CONTROL_PACKED_INCLUDED

// Reader of the packed control maps (LoogaPackedControls). Each texel holds the 4 strongest splat layers: the layer
// indices / 255 in the index map and their weights in the weight map. An index cannot be filtered, so the reader loads
// the 4 texels of the bilinear footprint and adds the weights of each layer, as a bilinear sample of the control maps
// does. It keeps the 4 strongest layers of the footprint. The terrain surface and the terrain ground include this file.

struct LoogaPackedFootprint
{
    int4 indices[4];
    // Texel weights times the bilinear weight of the texel.
    float4 weights[4];
};

// The mip that a bilinear sample with these UV derivatives reads. Bilinear samplers use the nearest mip. size is the
// mip 0 size in texels.
int GetLoogaPackedMip(float2 dx, float2 dy, float2 size, float mipCount)
{
    float2 x = dx * size;
    float2 y = dy * size;
    float lod = 0.5 * log2(max(max(dot(x, x), dot(y, y)), 1e-12));
    return (int)clamp(floor(lod + 0.5), 0.0, max(mipCount - 1.0, 0.0));
}

// The 4 texels of the bilinear footprint at a UV, with clamped addressing. The UV is a sampler UV: texel centres are
// at (i + 0.5) / size. size is the mip 0 size in texels.
LoogaPackedFootprint LoadLoogaPackedFootprint(TEXTURE2D_PARAM(indexMap, indexSampler),
    TEXTURE2D_PARAM(weightMap, weightSampler), float2 uv, float2 size, int mip)
{
    int2 mipSize = max((int2)size >> mip, 1);
    float2 position = uv * (float2)mipSize - 0.5;
    float2 lowerPosition = floor(position);
    float2 fraction = position - lowerPosition;
    int2 lower = clamp((int2)lowerPosition, 0, mipSize - 1);
    int2 upper = clamp((int2)lowerPosition + 1, 0, mipSize - 1);
    int2 texels[4] = { lower, int2(upper.x, lower.y), int2(lower.x, upper.y), upper };
    float4 bilinear = float4((1.0 - fraction.x) * (1.0 - fraction.y), fraction.x * (1.0 - fraction.y),
        (1.0 - fraction.x) * fraction.y, fraction.x * fraction.y);

    LoogaPackedFootprint footprint;
    [unroll]
    for (int texel = 0; texel < 4; texel++)
    {
        footprint.indices[texel] = (int4)round(LOAD_TEXTURE2D_LOD(indexMap, texels[texel], mip) * 255.0);
        footprint.weights[texel] = LOAD_TEXTURE2D_LOD(weightMap, texels[texel], mip) * bilinear[texel];
    }
    return footprint;
}

// Sorted insert. On equal weights the higher layer wins, as in the native terrain order.
void InsertLoogaPackedLayer(inout int4 indices, inout float4 weights, int layer, float weight)
{
    // 1 where the new layer goes before the slot. Float math keeps the vector logic valid in every HLSL version.
    float4 before = max((float4)(weight > weights), (float4)(weight == weights) * (float4)(layer > indices));
    if (before.x > 0)
    {
        weights = float4(weight, weights.xyz);
        indices = int4(layer, indices.xyz);
    }
    else if (before.y > 0)
    {
        weights.yzw = float3(weight, weights.yz);
        indices.yzw = int3(layer, indices.yz);
    }
    else if (before.z > 0)
    {
        weights.zw = float2(weight, weights.z);
        indices.zw = int2(layer, indices.z);
    }
    else if (before.w > 0)
    {
        weights.w = weight;
        indices.w = layer;
    }
}

// The filtered weight of one layer: the sum of its weights over the footprint.
float GetLoogaPackedLayerWeight(LoogaPackedFootprint footprint, int layer)
{
    return dot(footprint.weights[0], (float4)(footprint.indices[0] == layer))
        + dot(footprint.weights[1], (float4)(footprint.indices[1] == layer))
        + dot(footprint.weights[2], (float4)(footprint.indices[2] == layer))
        + dot(footprint.weights[3], (float4)(footprint.indices[3] == layer));
}

// The 4 strongest layers of the footprint, sorted by weight. The weights are not normalized. Unused slots have
// weight 0.
void SelectLoogaPackedLayers(LoogaPackedFootprint footprint, out int4 indices, out float4 weights)
{
    indices = 0;
    weights = 0;
    [unroll]
    for (int texel = 0; texel < 4; texel++)
    {
        [unroll]
        for (int slot = 0; slot < 4; slot++)
        {
            int layer = footprint.indices[texel][slot];
            // A slot without weight adds nothing. A layer that is already selected has its full weight.
            bool selected = dot(weights, (float4)(indices == layer)) > 0;
            UNITY_BRANCH
            if (footprint.weights[texel][slot] > 0 && !selected)
            {
                InsertLoogaPackedLayer(indices, weights, layer, GetLoogaPackedLayerWeight(footprint, layer));
            }
        }
    }
}

#endif
