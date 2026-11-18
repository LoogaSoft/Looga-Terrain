#ifndef LOOGA_TEXTURE_STAMP_CORE_INCLUDED
#define LOOGA_TEXTURE_STAMP_CORE_INCLUDED

// Shared math of the texture passes. Layer maps hold layer index / 32 in each channel, weight maps the weights of the
// four strongest layers, strongest first.

#define LAYER_INDEX_SCALE 32.0
#define LOOGA_PI 3.14159265358979

struct LayerOutput
{
    float4 layers : SV_Target0;
    float4 weights : SV_Target1;
};

// Takes only the weight that is left, then inserts the layer into the sorted top four. An equal weight does not move an
// entry down. The layers are layer indices (not divided by 32).
LayerOutput InsertLayer(float weight, float layer, float4 weights, float4 layers)
{
    float total = weights.x + weights.y + weights.z + weights.w;
    weight = min(weight, 1.0 - saturate(total));
    if (weight > weights.x)
    {
        weights = float4(weight, weights.xyz);
        layers = float4(layer, layers.xyz);
    }
    else if (weight > weights.y)
    {
        weights = float4(weights.x, weight, weights.yz);
        layers = float4(layers.x, layer, layers.yz);
    }
    else if (weight > weights.z)
    {
        weights = float4(weights.xy, weight, weights.z);
        layers = float4(layers.xy, layer, layers.z);
    }
    else if (weight > weights.w)
    {
        weights.w = weight;
        layers.w = layer;
    }
    LayerOutput output;
    output.layers = layers / LAYER_INDEX_SCALE;
    output.weights = weights;
    return output;
}

// 1 inside the range. The smoothness widens the range on each side with a smoothstep edge, like MicroVerse.
float RangeWeight(float2 range, float2 smoothness, float value)
{
    smoothness = max(0.00001, smoothness);
    range.x -= smoothness.x;
    range.y += smoothness.y;
    float lower = smoothstep(range.x, range.x + smoothness.x, value);
    float upper = 1 - smoothstep(range.y - smoothness.y, range.y, value);
    return lower * upper;
}

#endif
