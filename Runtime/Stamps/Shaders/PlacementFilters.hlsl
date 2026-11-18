#ifndef LOOGA_PLACEMENT_FILTERS_INCLUDED
#define LOOGA_PLACEMENT_FILTERS_INCLUDED

// Filters of the placement passes (clear, object and detail stamps), like the MicroVerse filter passes. The filters read the
// tile at tile UV (0 to 1 over the terrain size), without the half-texel offset of the texture stamps. The noise UV is
// tile UV * (tile size / 1000 m) + tile position / tile size. The include order must be StampFalloff.hlsl, then
// TextureStampCore.hlsl, then this file.

#include "WaterFilter.hlsl"
#include "TerrainFieldFilter.hlsl"

// Values must agree with StampNoiseOperation in StampWeightNoise.cs.
#define LOOGA_NOISE_ADD 0
#define LOOGA_NOISE_SUBTRACT 1
#define LOOGA_NOISE_MULTIPLY 2
#define LOOGA_NOISE_MULTIPLY_ONE_PLUS 3
#define LOOGA_NOISE_MIN 4

SamplerState looga_linear_repeat_sampler;
float _Weight;
int _ClampFalloffTexture;

// Weight noise of a type from StampNoise.hlsl. Parameters: x frequency, y amplitude, z offset, w balance. Texture: x, y
// scale and z, w offset.
int _WeightNoiseType;
int _WeightNoiseSpace;
float4 _WeightNoiseParams;
Texture2D<float4> _WeightNoiseTexture;
float4 _WeightNoiseScaleOffset;
float4 _WeightNoiseChannel;
int _Weight2NoiseType;
int _Weight2NoiseSpace;
float4 _Weight2NoiseParams;
Texture2D<float4> _Weight2NoiseTexture;
float4 _Weight2NoiseScaleOffset;
float4 _Weight2NoiseChannel;
int _Weight2NoiseOperation;

// Height filter. Range and smoothness are in heightmap texture units (metres / real height).
int _UseHeightFilter;
float2 _HeightFilterRange;
float2 _HeightFilterSmoothness;
float _HeightFilterWeight;
int _HeightFilterNoiseType;
float4 _HeightFilterNoiseParams;
float _RealHeight;
Texture2D<float> _Heightmap;
float4 _Heightmap_TexelSize;
// Slope filter. Range and smoothness are in radians.
int _UseSlopeFilter;
float2 _SlopeFilterRange;
float2 _SlopeFilterSmoothness;
float _SlopeFilterWeight;
int _SlopeFilterNoiseType;
float4 _SlopeFilterNoiseParams;
int _SlopeFilterMeasure;
float _SlopeFilterReach;
int _SlopeFilterReachFade;
// Texture filter: layer and weight maps of the tile before the edge copy, bilinear, like the MicroVerse maps.
sampler2D _IndexMap;
sampler2D _WeightMap;
int _UseTextureFilter;
float4 _TextureLayerWeights[32];

// MicroVerse filter box edge. Inner 1 is a hard edge that excludes the border itself.
float FilterBox(float2 stampUv, float inner)
{
    if (inner == 1)
    {
        return stampUv.x <= 0 || stampUv.y <= 0 || stampUv.x >= 1 || stampUv.y >= 1 ? 0 : 1;
    }
    return BoxFalloff(stampUv, inner);
}

float FilterShapeFalloff(float2 tileUv, float2 stampUv, float2 noiseUv, float noise)
{
    float falloff = 1;
    if (_FalloffShape == LOOGA_FALLOFF_BOX)
    {
        falloff = FilterBox(stampUv, saturate(_FalloffRange.y - noise));
    }
    else if (_FalloffShape == LOOGA_FALLOFF_RANGE)
    {
        falloff = RangeFalloff(stampUv, noise);
    }
    else if (_FalloffShape == LOOGA_FALLOFF_TEXTURE)
    {
        float4 value;
        if (_ClampFalloffTexture)
        {
            value = _FalloffTexture.Sample(looga_linear_clamp_sampler, stampUv);
        }
        else
        {
            value = _FalloffTexture.Sample(looga_linear_repeat_sampler, FalloffTextureUv(stampUv));
        }
        float textureSample = dot(value, _FalloffTextureChannel);
        falloff *= textureSample;
        falloff *= _FalloffTextureParams.x;
        falloff += _FalloffTextureParams.y * textureSample;
        falloff *= FilterBox(stampUv, saturate(_FalloffRange.y - noise));
    }
    else if (LOOGA_IS_AREA_FALLOFF(_FalloffShape))
    {
        falloff *= AreaWeight(_AreaDistance.Sample(looga_linear_clamp_sampler, tileUv).r, _AreaRange - noise);
    }
    if (_UsePaintedArea)
    {
        falloff *= PaintedWeight(noiseUv);
    }
    return EaseFalloff(falloff);
}

// Falloff with optional noise in UV units. Global ignores easing and noise.
float FilterFalloff(float2 tileUv, float2 stampUv, float2 noiseUv)
{
    float falloff = 1;
    if (_FalloffShape == LOOGA_FALLOFF_GLOBAL)
    {
        if (_UsePaintedArea)
        {
            falloff = PaintedWeight(noiseUv);
        }
    }
    else
    {
        falloff = FilterShapeFalloff(tileUv, stampUv, noiseUv, 0);
        if (_NoiseType == LOOGA_NOISE_FBM)
        {
            float2 noisePosition = _NoiseSpace == LOOGA_NOISE_SPACE_STAMP ? stampUv : noiseUv;
            float noise = NoiseValue(noisePosition, _NoiseParams);
            noise *= 1 - falloff;
            falloff = FilterShapeFalloff(tileUv, stampUv, noiseUv, noise);
        }
        if (_FalloffShape == LOOGA_FALLOFF_BOX || _FalloffShape == LOOGA_FALLOFF_TEXTURE)
        {
            falloff *= FilterBox(stampUv, 1);
        }
    }
    return falloff;
}

float WeightNoiseTexture(Texture2D<float4> noiseTexture, float2 uv, float4 scaleOffset, float4 channel, float4 parameters)
{
    float4 value = noiseTexture.Sample(looga_linear_repeat_sampler, uv * scaleOffset.xy + scaleOffset.zw);
    return dot(value, channel) * parameters.y + parameters.w;
}

// 1 + first noise, then the second noise combined by its operation.
float FilterWeightNoise(float2 noiseUv, float2 stampUv)
{
    float2 uv0 = _WeightNoiseSpace == LOOGA_NOISE_SPACE_STAMP ? stampUv : noiseUv;
    float2 uv1 = _Weight2NoiseSpace == LOOGA_NOISE_SPACE_STAMP ? stampUv : noiseUv;
    float result = 1;
    if (_WeightNoiseType == LOOGA_NOISE_TEXTURE)
    {
        result = WeightNoiseTexture(_WeightNoiseTexture, uv0, _WeightNoiseScaleOffset, _WeightNoiseChannel,
            _WeightNoiseParams);
    }
    else if (_WeightNoiseType != LOOGA_NOISE_NONE)
    {
        result = 1 + TypedNoiseValue(_WeightNoiseType, uv0, _WeightNoiseParams);
    }
    if (_Weight2NoiseType == LOOGA_NOISE_NONE) return result;
    float result2;
    if (_Weight2NoiseType == LOOGA_NOISE_TEXTURE)
    {
        result2 = WeightNoiseTexture(_Weight2NoiseTexture, uv1, _Weight2NoiseScaleOffset, _Weight2NoiseChannel,
            _Weight2NoiseParams);
    }
    else
    {
        result2 = TypedNoiseValue(_Weight2NoiseType, uv1, _Weight2NoiseParams);
    }
    if (_Weight2NoiseOperation == LOOGA_NOISE_ADD)
    {
        result += result2;
    }
    else if (_Weight2NoiseOperation == LOOGA_NOISE_SUBTRACT)
    {
        result -= result2;
    }
    else if (_Weight2NoiseOperation == LOOGA_NOISE_MULTIPLY)
    {
        result *= result2;
    }
    else if (_Weight2NoiseOperation == LOOGA_NOISE_MULTIPLY_ONE_PLUS)
    {
        result *= 1 + result2;
    }
    else if (_Weight2NoiseOperation == LOOGA_NOISE_MIN)
    {
        result = min(result, result2);
    }
    else
    {
        result = max(result, result2);
    }
    return result;
}

// Forward differences of three height samples, like the MicroVerse placement passes. The slope filter uses the
// encoded normal: normalize(dx, dz, 1, height) in the order x, 1, z, * 0.5 + 0.5. Its y also depends on the height.
float2 HeightGradient(float2 uv, out float height)
{
    height = _Heightmap.Sample(looga_linear_clamp_sampler, uv);
    float height1 = _Heightmap.Sample(looga_linear_clamp_sampler, uv + float2(_Heightmap_TexelSize.x, 0));
    float height2 = _Heightmap.Sample(looga_linear_clamp_sampler, uv + float2(0, _Heightmap_TexelSize.y));
    float2 gradient = height - float2(height1, height2);
    gradient = gradient * _Heightmap_TexelSize.zw;
    return gradient;
}

// True steepness in radians: central height differences in world units. The differences use only the texel centres
// that the clamped sampler reaches, so the edge texels of a tile keep the full gradient of a plane.
float GroundSteepness(float2 uv)
{
    float2 texel = _Heightmap_TexelSize.xy;
    float2 low = 0.5 * texel;
    float2 high = 1 - 0.5 * texel;
    float2 minimum = clamp(uv - texel, low, high);
    float2 maximum = clamp(uv + texel, low, high);
    float dx = _Heightmap.Sample(looga_linear_clamp_sampler, float2(maximum.x, uv.y)) -
        _Heightmap.Sample(looga_linear_clamp_sampler, float2(minimum.x, uv.y));
    float dz = _Heightmap.Sample(looga_linear_clamp_sampler, float2(uv.x, maximum.y)) -
        _Heightmap.Sample(looga_linear_clamp_sampler, float2(uv.x, minimum.y));
    float2 gradient = float2(dx, dz) / max(maximum - minimum, 1e-6) * _RealHeight / _TerrainSizeXZ;
    return atan(length(gradient));
}

// Rings of samples for the reach fade. Each ring counts for less of its steepness than the ring inside it.
#define SLOPE_FADE_RINGS 6
// Height in metres that a sample must rise over the point to count fully in the reach fade.
#define SLOPE_FADE_RISE 2.0

// The steepest ground within _SlopeFilterReach metres: eight directions at the full and at half the reach. With the
// reach fade, the steepness of a ring moves the value only by the share 1 - (ring distance - one step) / reach, so the
// value falls off from a steep face over the reach instead of ending at a hard line. Only ground that rises over the
// point counts, so the value falls off toward the foot of a face and not along its top.
float GroundSteepnessWithin(float2 uv)
{
    float steepness = GroundSteepness(uv);
    if (_SlopeFilterReach <= 0) return steepness;
    float2 reach = _SlopeFilterReach / _TerrainSizeXZ;
    if (_SlopeFilterReachFade)
    {
        float local = steepness;
        float here = _Heightmap.Sample(looga_linear_clamp_sampler, uv) * _RealHeight;
        [loop]
        for (int ring = 1; ring <= SLOPE_FADE_RINGS; ring++)
        {
            float share = (float)ring / SLOPE_FADE_RINGS;
            float weight = 1 - share + 1.0 / SLOPE_FADE_RINGS;
            // Odd rings turn half a direction step, so the directions of two rings fill each other's gaps.
            float turn = (ring & 1) * (LOOGA_PI / 8);
            [unroll]
            for (int direction = 0; direction < 8; direction++)
            {
                float angle = direction * (LOOGA_PI / 4) + turn;
                float2 at = uv + float2(cos(angle), sin(angle)) * reach * share;
                float rise = saturate((_Heightmap.Sample(looga_linear_clamp_sampler, at) * _RealHeight - here) /
                    SLOPE_FADE_RISE);
                steepness = max(steepness, lerp(local, GroundSteepness(at), weight * rise));
            }
        }
        return steepness;
    }
    [unroll]
    for (int direction = 0; direction < 8; direction++)
    {
        float angle = direction * (LOOGA_PI / 4);
        float2 offset = float2(cos(angle), sin(angle)) * reach;
        steepness = max(steepness, GroundSteepness(uv + offset));
        steepness = max(steepness, GroundSteepness(uv + offset * 0.5));
    }
    return steepness;
}

// Texture filter: each of the four strongest layers removes (x * weight) + (z * weight) * y.
float TextureMask(float2 uv)
{
    if (!_UseTextureFilter) return 1;
    float textureMask = 1;
    half4 indices = tex2D(_IndexMap, uv) * 32;
    half4 weights = tex2D(_WeightMap, uv);
    for (int layer = 0; layer < 4; ++layer)
    {
        int index = round(indices[layer]);
        float weight = weights[layer];
        float3 values = _TextureLayerWeights[index].xyz;
        textureMask -= ((values.x * weight) + (values.z * weight) * values.y);
    }
    return saturate(textureMask);
}

// Filters, then falloff, then the stamp weight: the weight before the spawn test.
float FilterWeight(float2 tileUv, float2 stampUv, float2 noiseUv)
{
    float result = FilterWeightNoise(noiseUv, stampUv);
    if (_UseHeightFilter)
    {
        float height = _Heightmap.Sample(looga_linear_clamp_sampler, tileUv);
        if (_HeightFilterNoiseType == LOOGA_NOISE_FBM)
        {
            height += NoiseValue(noiseUv, _HeightFilterNoiseParams) / _RealHeight;
        }
        result *= lerp(1, RangeWeight(_HeightFilterRange, _HeightFilterSmoothness, height), _HeightFilterWeight);
    }
    if (_UseSlopeFilter)
    {
        float slope;
        if (_SlopeFilterMeasure == 1)
        {
            slope = GroundSteepnessWithin(tileUv);
        }
        else
        {
            float height0;
            float2 gradient = HeightGradient(tileUv, height0);
            float3 normal = normalize(float4(gradient.x, gradient.y, 1.0, height0)).xzy * 0.5 + 0.5;
            slope = (LOOGA_PI / 2) * acos(saturate(dot(normal, float3(0, 1, 0))));
        }
        if (_SlopeFilterNoiseType == LOOGA_NOISE_FBM)
        {
            slope += NoiseValue(noiseUv, _SlopeFilterNoiseParams);
        }
        result *= lerp(1, RangeWeight(_SlopeFilterRange, _SlopeFilterSmoothness, slope), _SlopeFilterWeight);
    }
    if (_UseWaterFilter)
    {
        result *= WaterFilterWeight(tileUv, noiseUv, _Heightmap.Sample(looga_linear_clamp_sampler, tileUv));
    }
    result *= FieldFilterWeight(tileUv, noiseUv);
    float falloff = FilterFalloff(tileUv, stampUv, noiseUv);
    return result * _Weight * falloff;
}

#endif
