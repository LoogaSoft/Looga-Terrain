#ifndef LOOGA_HEIGHT_STAMP_CORE_INCLUDED
#define LOOGA_HEIGHT_STAMP_CORE_INCLUDED

// Per-sample math of height stamps and spline paths. The compute kernels (World Aligned) and the raster passes
// (MicroVerse parity) both use it. Heights are in heightmap texture units: one unit is twice the terrain height.

#include "StampFalloff.hlsl"

#define MAXIMUM_HEIGHT (32766.0 / 65535.0)
#define COMBINE_OVERRIDE 0
#define COMBINE_MAX 1
#define COMBINE_MIN 2
#define COMBINE_ADD 3

// Tile UV (sample centre) to stamp UV. Row 0 and row 1 give the stamp u and v.
float4x4 _TileToStamp;
// World noise UV = (tile UV + xy) * z, written as MicroVerse does. z = tile size / 1000 m.
float3 _NoiseTransform;
float _RealHeight;
float _TerrainHeight;
int _CombineMode;

Texture2D<float4> _StampTexture;
SamplerState sampler_StampTexture;
float _MipBias;
int _UsePowerTilt;
float _Power;
float2 _Tilt;
// Stamp bottom and top height in heightmap texture units.
float2 _HeightRange;

// Spline path. The distance texture holds x = distance outside the spline width (metres) and y = world height of the
// nearest polyline vertex. It covers the tile with tile UV 0..1.
Texture2D<float4> _PathDistance;
SamplerState sampler_PathDistance;
float _PathWidth;
float _PathSmoothness;
float _PathTrench;
int _PathUseTrenchCurve;
Texture2D<float> _PathTrenchCurve;
SamplerState sampler_PathTrenchCurve;
int _PathEasing;
int _PathNoiseType;
float4 _PathNoiseParams;
// Extra full-effect width (metres) where the path stands above the old ground, reached at _PathFillRamp metres of fill.
float _PathFillShoulder;
float _PathFillRamp;

// Snapshot limit offset in heightmap texture units.
float _SnapshotOffset;

// Water bed. The field holds x = signed distance past the water edge (negative inside) and y = water level of the
// nearest edge, in metres. Field UV = tile UV * scale. Params: x = max depth, y = 3 / shelf width, z = beach width,
// w = beach max height. All in metres.
Texture2D<float4> _WaterBedField;
SamplerState sampler_WaterBedField;
float2 _WaterBedFieldScale;
float4 _WaterBedParams;
float _WaterBedStrength;
float _WaterBedNotFound;
int _WaterBedNoiseType;
float4 _WaterBedNoiseParams;
// Drop-off beyond the shelf: x = extra depth, y = start distance, z = width, all in metres. Noise moves the start.
float4 _WaterBedDrop;
// Ground this far below the bed keeps its height, in metres.
float _WaterBedKeepDepth;
// Optional drop area: signed distance to a spline area in metres (negative inside), over the tile with tile UV 0..1.
int _WaterBedUseDropArea;
float _WaterBedDropAreaFade;
Texture2D<float4> _WaterBedDropArea;
SamplerState sampler_WaterBedDropArea;
int _WaterBedDropNoiseType;
float4 _WaterBedDropNoiseParams;

float CombineHeight(float oldHeight, float newHeight)
{
    if (_CombineMode == COMBINE_OVERRIDE) return newHeight;
    if (_CombineMode == COMBINE_MAX) return max(oldHeight, newHeight);
    if (_CombineMode == COMBINE_MIN) return min(oldHeight, newHeight);
    return oldHeight + newHeight;
}

float2 NoiseUv(float2 tileUv)
{
    return tileUv * _NoiseTransform.z + _NoiseTransform.xy * _NoiseTransform.z;
}

// New height of a sample inside the stamp square.
float StampHeight(float oldHeight, FalloffInput input)
{
    float falloff = StampFalloff(input, _RealHeight);
    float stampSample = _StampTexture.SampleLevel(sampler_StampTexture, input.stampUv, _MipBias).r;
    if (_UsePowerTilt)
    {
        // A UNORM sample is never negative; max only tells the compiler so.
        stampSample = pow(max(stampSample, 0.0), _Power);
        float2 tilt = lerp(float2(-1, -1), float2(1, 1), input.stampUv) * _Tilt;
        stampSample += tilt.x + tilt.y;
    }
    float newHeight = saturate(_HeightRange.x + stampSample * (_HeightRange.y - _HeightRange.x));
    return clamp(lerp(oldHeight, CombineHeight(oldHeight, newHeight), falloff), 0, MAXIMUM_HEIGHT);
}

// New height of a sample for a snapshot limit (Override, Max or Min). The snapshot height is in heightmap texture units.
float SnapshotLimitHeight(float oldHeight, float snapshotHeight, FalloffInput input)
{
    float falloff = StampFalloff(input, _RealHeight);
    float limit = saturate(snapshotHeight + _SnapshotOffset);
    return clamp(lerp(oldHeight, CombineHeight(oldHeight, limit), falloff), 0, MAXIMUM_HEIGHT);
}

// New height of a sample for a water bed. Inside the water the bed replaces the height. In the beach band outside
// the water it only lowers low ground. The stamp falloff limits both.
float WaterBedHeight(float oldHeight, FalloffInput input)
{
    float2 tileUv = input.tileUv;
    float2 water = LOOGA_SAMPLE(_WaterBedField, tileUv * _WaterBedFieldScale).xy;
    float distance = water.x;
    // Outside the beach band, or no water in reach.
    if (distance >= _WaterBedParams.z) return oldHeight;
    float level = water.y;
    if (level < -0.5 * _WaterBedNotFound) return oldHeight;

    float oldWorld = oldHeight * _RealHeight + _TerrainHeight;
    float maxDepth = _WaterBedParams.x;
    float newWorld;
    if (distance <= 0)
    {
        float fraction = 1 - exp(distance * _WaterBedParams.y);
        float depth = maxDepth * fraction;
        if (IsFbmNoise(_WaterBedNoiseType))
        {
            depth += TypedNoiseValue(_WaterBedNoiseType, NoiseUv(tileUv), _WaterBedNoiseParams) * fraction;
        }
        if (_WaterBedDrop.x > 0)
        {
            // A smooth step from the drop-off start, so the slope has no crease at either end.
            float start = _WaterBedDrop.y;
            if (IsFbmNoise(_WaterBedDropNoiseType))
            {
                start += TypedNoiseValue(_WaterBedDropNoiseType, NoiseUv(tileUv), _WaterBedDropNoiseParams);
            }
            float inside = distance < -0.5 * _WaterBedNotFound ? _WaterBedNotFound : -distance;
            float drop = smoothstep(0, 1, saturate((inside - start) / max(_WaterBedDrop.z, 1)));
            if (_WaterBedUseDropArea)
            {
                float areaDistance = LOOGA_SAMPLE(_WaterBedDropArea, tileUv).r;
                drop *= smoothstep(0, 1, saturate(-areaDistance / max(_WaterBedDropAreaFade, 1)));
            }
            depth += _WaterBedDrop.x * drop;
        }
        float bed = level - max(depth, 0);
        // Ground well above the water (a cliff or a rock) or far below the bed (a gorge) keeps its height.
        float keep = smoothstep(0.75 * _WaterBedParams.w, _WaterBedParams.w, oldWorld - level);
        keep = max(keep, smoothstep(0.75 * _WaterBedKeepDepth, _WaterBedKeepDepth, bed - oldWorld));
        newWorld = lerp(bed, oldWorld, keep);
    }
    else
    {
        // The beach continues the slope of the bed at the edge, up to the band width.
        float beach = level + distance * maxDepth * _WaterBedParams.y;
        float band = 1 - smoothstep(0, _WaterBedParams.z, distance);
        // Ground well above the beach slope, such as a cliff, keeps its height.
        float low = 1 - smoothstep(0.75 * _WaterBedParams.w, _WaterBedParams.w, oldWorld - beach);
        newWorld = lerp(oldWorld, min(oldWorld, beach), band * low);
    }
    newWorld = lerp(oldWorld, newWorld, _WaterBedStrength * StampFalloff(input, _RealHeight));
    return clamp((newWorld - _TerrainHeight) / _RealHeight, 0, MAXIMUM_HEIGHT);
}

float EasePath(float weight)
{
    if (_PathEasing == LOOGA_EASING_SMOOTHSTEP) return smoothstep(0, 1, weight);
    if (_PathEasing == LOOGA_EASING_EASE_IN) return weight * weight;
    if (_PathEasing == LOOGA_EASING_EASE_OUT) return 1 - (1 - weight) * (1 - weight);
    if (_PathEasing == LOOGA_EASING_EASE_IN_OUT)
    {
        return weight < 0.5 ? 2 * weight * weight : 1 - pow(-2 * weight + 2, 2) / 2;
    }
    return weight;
}

// New height of a sample for a spline path. The resolution is the heightmap resolution of the tile.
float PathHeight(float oldHeight, float2 tileUv, float resolution)
{
    // The patched MicroVerse spline path reads the distance at the sample position, half a sample before the sample
    // centre.
    float2 data = LOOGA_SAMPLE(_PathDistance, tileUv - 0.5 / resolution).xy;
    float distance = data.x;
    float pathHeight = data.y;

    // A fill shoulder widens the level part where the path is above the old ground, so the embankment starts
    // further out. The share grows with the fill, so cut and level ground keep the plain width.
    float fill = (pathHeight - _TerrainHeight) - oldHeight * _RealHeight;
    float width = _PathWidth + _PathFillShoulder * saturate(fill / max(0.001, _PathFillRamp));
    float edge = width + _PathSmoothness;
    float weight = 1 - saturate((distance - width) / max(0.001, edge - width));
    weight = EasePath(weight);
    if (IsFbmNoise(_PathNoiseType))
    {
        // The noise lowers the embankment most at weight 0.5 and not at all at weight 0 or 1.
        float strength = 1.0 - abs(weight - 0.5) * 2;
        strength = 1 - (1 - strength) * (1 - strength);
        pathHeight -= abs(TypedNoiseValue(_PathNoiseType, NoiseUv(tileUv), _PathNoiseParams) * strength);
    }
    pathHeight -= _TerrainHeight;

    float trench = _PathTrench;
    if (_PathUseTrenchCurve)
    {
        trench = LOOGA_SAMPLE(_PathTrenchCurve, float2(distance / edge, 0.5));
    }
    pathHeight = lerp(pathHeight, pathHeight - trench, 1.0 - saturate(smoothstep(width, edge, distance)));

    float newHeight = saturate(lerp(oldHeight, pathHeight / _RealHeight, weight));
    return clamp(lerp(oldHeight, CombineHeight(oldHeight, newHeight), weight), 0, MAXIMUM_HEIGHT);
}

#endif
