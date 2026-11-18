#ifndef LOOGA_TERRAIN_FIELD_FILTER_INCLUDED
#define LOOGA_TERRAIN_FIELD_FILTER_INCLUDED

// Terrain field filters of the texture and placement passes (TerrainFieldMaps.cs). The two field maps cover the tile
// with texel centres at tile UV (i + 0.5) / size, like the heightmap reads of the other filters. The include order must
// be StampFalloff.hlsl, then TextureStampCore.hlsl, then this file.

// R steepness, G relative height 4 m, B relative height 20 m, A northness.
Texture2D<float4> _TerrainFieldShape;
// R face above distance, G face above angle, B face below distance, A face below drop.
Texture2D<float4> _TerrainFieldFaces;
// R log10 upstream area (square metres), G wetness index. It has its own resolution (about 2 m texels).
Texture2D<float4> _TerrainFieldWater;
// R canopy, G object foot distance (metres), from the placements of the field sources (PlacementFieldMaps.cs).
Texture2D<float4> _TerrainFieldPlacements;
// For each filter: x = 1 if enabled, y = field index (TerrainField), z = weight, w = noise type.
float4 _FieldFilterSettings[2];
// For each filter: xy = range, zw = smoothness, in the unit of the field.
float4 _FieldFilterRanges[2];
float4 _FieldFilterNoise[2];

float TerrainFieldValue(int field, float2 tileUv)
{
    float4 values = field < 4
        ? _TerrainFieldShape.SampleLevel(looga_linear_clamp_sampler, tileUv, 0)
        : (field < 8
            ? _TerrainFieldFaces.SampleLevel(looga_linear_clamp_sampler, tileUv, 0)
            : (field < 10
                ? _TerrainFieldWater.SampleLevel(looga_linear_clamp_sampler, tileUv, 0)
                : _TerrainFieldPlacements.SampleLevel(looga_linear_clamp_sampler, tileUv, 0)));
    int channel = field < 10 ? field & 3 : field - 10;
    return channel == 0 ? values.x : (channel == 1 ? values.y : (channel == 2 ? values.z : values.w));
}

// Product of the enabled field filters.
float FieldFilterWeight(float2 tileUv, float2 noiseUv)
{
    float result = 1;
    [unroll]
    for (int i = 0; i < 2; i++)
    {
        float4 settings = _FieldFilterSettings[i];
        if (settings.x < 0.5) continue;
        float value = TerrainFieldValue((int)settings.y, tileUv);
        if ((int)settings.w == LOOGA_NOISE_FBM)
        {
            value += NoiseValue(noiseUv, _FieldFilterNoise[i]);
        }
        float4 range = _FieldFilterRanges[i];
        result *= lerp(1, RangeWeight(range.xy, range.zw, value), settings.z);
    }
    return result;
}

#endif
