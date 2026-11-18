#ifndef LOOGA_TERRAIN_SURFACE_LAYERS_INCLUDED
#define LOOGA_TERRAIN_SURFACE_LAYERS_INCLUDED

// One layer of a Looga terrain surface: the rows of the per-layer property texture, the projection UVs, the colour
// adjustment and the decoding of the texture array layouts. The surface library and other shaders that read the
// terrain layers, such as the rock faces, use the same functions, so they show the layers in the same way. This file
// declares no textures and no material buffer.

// Rows of the per-layer property texture. The column is the layer index.
// Keep these rows the same as LoogaTerrainSurface.PropertyRow.
#define LOOGA_LAYER_ROW_UV 0        // xy UV scale, zw UV offset
#define LOOGA_LAYER_ROW_COLOR 1     // rgb tint, a saturation
#define LOOGA_LAYER_ROW_ADJUST 2    // x brightness, y contrast, z UV rotation in radians, w stochastic (0 or 1)
#define LOOGA_LAYER_ROW_MATERIAL 3  // x normal strength, y smoothness offset, z occlusion strength, w metallic
#define LOOGA_LAYER_ROW_HEIGHT 4    // x height offset, y height contrast, z interpolation contrast, w parallax depth
#define LOOGA_LAYER_ROW_NOISE 5     // strength multipliers: x detail noise, y distance noise, z normal noise 1, w normal noise 2
#define LOOGA_LAYER_ROW_NOISE2 6    // x normal noise 3 strength, y noise height amplitude, z resample strength, w resample UV scale
#define LOOGA_LAYER_ROW_COVER 7     // x world colour strength, y ground cover strength, z soil mean, w bare far cover
#define LOOGA_LAYER_ROW_OBJECT 8    // x object dust, yzw linear mean albedo after the colour adjustments (0 = not measured)

// Colour adjustments of a layer in this order: saturation, tint, contrast, brightness.
half3 AdjustLoogaSurfaceLayerColor(half3 albedo, float4 colorRow, float4 adjustRow, half3 luminanceWeights)
{
    half luminance = dot(albedo, luminanceWeights);
    albedo = lerp(luminance.xxx, albedo, (half)colorRow.a);
    albedo *= (half3)colorRow.rgb;
    albedo = saturate((albedo - 0.5h) * (half)adjustRow.y + 0.5h);
    return saturate(albedo + (half)adjustRow.x);
}

// World directions of the surface U and V axes of a projection: 0 projects along X (side), 1 along Y (top), 2 along
// Z (side). The side projections flip their U axis by the sign of the normal on their axis, so a texture is not
// mirrored on opposite faces. The noise axes are the unit directions; the surface axes include the surface UV scale.
void GetLoogaSurfaceProjectionAxes(int projection, float normalSign, float2 surfaceScale, out float3 noiseU,
    out float3 noiseV, out float3 axisU, out float3 axisV)
{
    if (projection == 0)
    {
        noiseU = float3(0, 0, normalSign);
        noiseV = float3(0, 1, 0);
    }
    else if (projection == 1)
    {
        noiseU = float3(1, 0, 0);
        noiseV = float3(0, 0, 1);
    }
    else
    {
        noiseU = float3(-normalSign, 0, 0);
        noiseV = float3(0, 1, 0);
    }
    float2 scale = projection == 1 ? surfaceScale : abs(surfaceScale);
    axisU = noiseU * scale.x;
    axisV = noiseV * scale.y;
}

float2x2 LoogaLayerRotation(float radians)
{
    float sine;
    float cosine;
    sincos(radians, sine, cosine);
    return float2x2(cosine, -sine, sine, cosine);
}

// The layer UV of a projection, its derivatives and the world directions of its U and V axes: surface UV
// (surfaceUV: xy scale, zw offset), then the layer scale and offset, then the layer rotation.
void GetLoogaSurfaceLayerUV(float3 positionWS, float3 positionDx, float3 positionDy, int projection, float normalSign,
    float4 surfaceUV, float4 uvRow, float2x2 rotation, out float2 uv, out float2 dx, out float2 dy, out float3 tangentWS,
    out float3 bitangentWS)
{
    float3 noiseU;
    float3 noiseV;
    float3 axisU;
    float3 axisV;
    GetLoogaSurfaceProjectionAxes(projection, normalSign, surfaceUV.xy, noiseU, noiseV, axisU, axisV);
    float2 layerUV = float2(dot(positionWS, axisU), dot(positionWS, axisV)) + surfaceUV.zw;
    uv = mul(rotation, layerUV * uvRow.xy + uvRow.zw);
    dx = mul(rotation, float2(dot(positionDx, axisU), dot(positionDx, axisV)) * uvRow.xy);
    dy = mul(rotation, float2(dot(positionDy, axisU), dot(positionDy, axisV)) * uvRow.xy);
    // The rows of rotation x layer scale x surface axes.
    float3 scaledU = axisU * uvRow.x;
    float3 scaledV = axisV * uvRow.y;
    tangentWS = SafeNormalize(rotation[0][0] * scaledU + rotation[0][1] * scaledV);
    bitangentWS = SafeNormalize(rotation[1][0] * scaledU + rotation[1][1] * scaledV);
}

// Normal and material of a layer from its texture samples. Quality layout: normal X and Y in RG, and a mask array with
// smoothness, occlusion and metallic in RGB. Compact layout: normal X in A, Y in G, smoothness in R, occlusion in B.
void DecodeLoogaSurfaceMaterial(half4 normalSample, half4 maskSample, bool quality, out half2 normalXY,
    out half smoothness, out half occlusion, out half metallic)
{
    if (quality)
    {
        normalXY = normalSample.rg * 2.0h - 1.0h;
        smoothness = maskSample.r;
        occlusion = maskSample.g;
        metallic = maskSample.b;
    }
    else
    {
        normalXY = normalSample.ag * 2.0h - 1.0h;
        smoothness = normalSample.r;
        occlusion = normalSample.b;
        metallic = 0.0h;
    }
}

#endif
