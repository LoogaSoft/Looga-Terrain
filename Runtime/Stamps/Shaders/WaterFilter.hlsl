#ifndef LOOGA_WATER_FILTER_INCLUDED
#define LOOGA_WATER_FILTER_INCLUDED

// Water filter of the texture and placement passes. Values must agree with StampWaterFilter.cs and
// HeightStampEvaluator.SetWaterFilter. Include after StampFalloff.hlsl (noise, linear clamp sampler) and
// TextureStampCore.hlsl (RangeWeight).

#define LOOGA_WATER_EDGE_BAND 0
#define LOOGA_WATER_LOW_DISTANCE 1
#define LOOGA_WATER_LOW_HEIGHT 2

int _UseWaterFilter;
// x = signed distance past the water edge, y = water level of the nearest edge. Both in metres.
Texture2D<float4> _WaterField;
// Field UV = tile UV (0 to 1 over the terrain size) * scale.
float2 _WaterFieldScale;
// World height of the terrain position, and the height of one heightmap texture unit, in metres.
float _WaterTerrainHeight;
float _WaterRealHeight;
// For each band: x = enabled, y = filter weight, z = noise type. Range: xy = range, zw = smoothness. Noise: the noise
// parameters, in metres.
float4 _WaterBandSettings[3];
float4 _WaterBandRanges[3];
float4 _WaterBandNoises[3];

float WaterBand(int band, float value, float2 noiseUv)
{
    float4 settings = _WaterBandSettings[band];
    int noiseType = (int)round(settings.z);
    if (IsFbmNoise(noiseType))
    {
        value += TypedNoiseValue(noiseType, noiseUv, _WaterBandNoises[band]);
    }
    float4 range = _WaterBandRanges[band];
    return lerp(1, RangeWeight(range.xy, range.zw, value), settings.y);
}

bool WaterBandEnabled(int band)
{
    return _WaterBandSettings[band].x > 0.5;
}

// The larger of the edge band (distance) and the low ground band (distance and height above the water). A disabled
// band gives 0. The heightmap value is the terrain height in heightmap texture units.
float WaterFilterWeight(float2 tileUv, float2 noiseUv, float heightmapValue)
{
    if (!_UseWaterFilter) return 1;
    float2 water = _WaterField.SampleLevel(looga_linear_clamp_sampler, tileUv * _WaterFieldScale, 0).xy;
    float edge = WaterBandEnabled(LOOGA_WATER_EDGE_BAND) ? WaterBand(LOOGA_WATER_EDGE_BAND, water.x, noiseUv) : 0;
    float low = 0;
    if (WaterBandEnabled(LOOGA_WATER_LOW_DISTANCE) || WaterBandEnabled(LOOGA_WATER_LOW_HEIGHT))
    {
        low = 1;
        if (WaterBandEnabled(LOOGA_WATER_LOW_DISTANCE))
        {
            low *= WaterBand(LOOGA_WATER_LOW_DISTANCE, water.x, noiseUv);
        }
        if (WaterBandEnabled(LOOGA_WATER_LOW_HEIGHT))
        {
            float height = heightmapValue * _WaterRealHeight + _WaterTerrainHeight - water.y;
            low *= WaterBand(LOOGA_WATER_LOW_HEIGHT, height, noiseUv);
        }
    }
    return max(edge, low);
}

#endif
