#ifndef LOOGA_STAMP_FALLOFF_INCLUDED
#define LOOGA_STAMP_FALLOFF_INCLUDED

#include "StampNoise.hlsl"

// Raster passes (MicroVerse parity) define LOOGA_STAMP_RASTER. They sample with screen-space derivatives like
// MicroVerse, so the sampler footprint matches, also with forced anisotropic filtering. Compute kernels have no
// derivatives and sample mip level 0.
#ifdef LOOGA_STAMP_RASTER
    #define LOOGA_SAMPLE(textureName, uv) textureName.Sample(sampler##textureName, uv)
#else
    #define LOOGA_SAMPLE(textureName, uv) textureName.SampleLevel(sampler##textureName, uv, 0)
#endif

// Falloff weight of a stamp in stamp space. Stamp UV (0.5, 0.5) is the stamp centre.
// The formulas reproduce the MicroVerse 1.7 falloff behavior that MEASUREMENTS.md records.
// Values must agree with StampFalloff.cs (FalloffShape, FalloffEasing, StampNoiseSpace).

#define LOOGA_FALLOFF_GLOBAL 0
#define LOOGA_FALLOFF_BOX 1
#define LOOGA_FALLOFF_RANGE 2
#define LOOGA_FALLOFF_TEXTURE 3
#define LOOGA_FALLOFF_SPLINE_AREA 4
// A water area uses the spline area formula. Its signed distance field is bound to _AreaDistance.
#define LOOGA_FALLOFF_WATER_AREA 5
#define LOOGA_IS_AREA_FALLOFF(shape) ((shape) == LOOGA_FALLOFF_SPLINE_AREA || (shape) == LOOGA_FALLOFF_WATER_AREA)

#define LOOGA_EASING_LINEAR 0
#define LOOGA_EASING_SMOOTHSTEP 1
#define LOOGA_EASING_EASE_IN 2
#define LOOGA_EASING_EASE_OUT 3
#define LOOGA_EASING_EASE_IN_OUT 4

// StampNoise.hlsl defines the LOOGA_NOISE_ type values.
#define LOOGA_NOISE_SPACE_WORLD 0
#define LOOGA_NOISE_SPACE_STAMP 1

int _FalloffShape;
float2 _FalloffRange;
int _FalloffEasing;
Texture2D<float4> _FalloffTexture;
SamplerState sampler_FalloffTexture;
float4 _FalloffTextureChannel;
float2 _FalloffTextureParams;
float4 _FalloffTextureTransform;

Texture2D<float4> _AreaDistance;
SamplerState sampler_AreaDistance;
float _AreaRange;
float _AreaBoost;
// Values must agree with StampFalloff.SplineAreaSide.
#define LOOGA_AREA_SIDE_INSIDE 0
#define LOOGA_AREA_SIDE_OUTSIDE 1
#define LOOGA_AREA_SIDE_EDGE 2
// Inside: the weight grows inward from the boost distance. Outside: the weight grows outward from the boost distance.
// Edge: the weight grows toward the spline line from the boost distance, on both sides.
int _AreaSide;

int _UsePaintedArea;
Texture2D<float> _PaintedArea;
SamplerState looga_linear_clamp_sampler;
float4x4 _PaintedAreaWorldToLocal;
float2 _TerrainSizeXZ;

int _NoiseType;
int _NoiseSpace;
// x frequency, y amplitude, z offset, w balance.
float4 _NoiseParams;

struct FalloffInput
{
    float2 stampUv;
    // Change of the stamp UV across one heightmap sample in x and z.
    float2 stampUvDx;
    float2 stampUvDy;
    // Sample-centre UV of the tile, (i + 0.5) / resolution.
    float2 tileUv;
    // Tile noise UV: (tile UV + tile position / tile size) * tile size / 1000 m.
    float2 worldNoiseUv;
};

// Box edge weight. Inner 1 gives no edge. Smaller values widen the edge up to 3 % of the stamp size.
float BoxFalloff(float2 stampUv, float inner)
{
    if (inner == 1) return 1;
    float2 edgeDistance = 0.5 - abs(saturate(stampUv) - 0.5);
    float2 weight = smoothstep(edgeDistance, 0, 0.03 * (1 - inner));
    return min(weight.x, weight.y);
}

// Radial weight. The range gives the inner and outer diameter as a fraction of the stamp size.
float RangeFalloff(float2 stampUv, float noise)
{
    float2 radii = saturate(_FalloffRange * 0.5 - saturate(noise) * 0.5);
    float radius = length(stampUv - 0.5);
    return 1.0 - saturate((radius - radii.x) / max(0.001, radii.y - radii.x));
}

// Turns and scales the falloff texture UV around the stamp centre.
float2 FalloffTextureUv(float2 stampUv)
{
    float2 uv = (stampUv - 0.5) * _FalloffTextureTransform.y;
    float angle = _FalloffTextureTransform.x;
    if (angle != 0)
    {
        float s = sin(angle);
        float c = cos(angle);
        uv = float2(uv.x * c + uv.y * s, uv.y * c - uv.x * s);
    }
    return uv + 0.5 + _FalloffTextureTransform.zw;
}

// The gradients give the change of the stamp UV across one heightmap sample in x and z.
float TextureFalloff(float2 stampUv, float2 stampUvDx, float2 stampUvDy, float noise)
{
    float2 uv = FalloffTextureUv(stampUv);
#ifdef LOOGA_STAMP_RASTER
    float4 textureValue = _FalloffTexture.Sample(sampler_FalloffTexture, uv);
#else
    float2 uvDx = FalloffTextureUv(stampUv + stampUvDx) - uv;
    float2 uvDy = FalloffTextureUv(stampUv + stampUvDy) - uv;
    float4 textureValue = _FalloffTexture.SampleGrad(sampler_FalloffTexture, uv, uvDx, uvDy);
#endif
    float textureSample = dot(textureValue, _FalloffTextureChannel);
    float falloff = textureSample * _FalloffTextureParams.x;
    falloff += _FalloffTextureParams.y * textureSample;
    return falloff * BoxFalloff(stampUv, saturate(_FalloffRange.y - noise));
}

float EaseFalloff(float falloff)
{
    if (_FalloffEasing == LOOGA_EASING_SMOOTHSTEP) return smoothstep(0, 1, falloff);
    if (_FalloffEasing == LOOGA_EASING_EASE_IN) return falloff * falloff;
    if (_FalloffEasing == LOOGA_EASING_EASE_OUT) return 1 - (1 - falloff) * (1 - falloff);
    if (_FalloffEasing == LOOGA_EASING_EASE_IN_OUT)
    {
        return falloff < 0.5 ? 2 * falloff * falloff : 1 - pow(-2 * falloff + 2, 2) / 2;
    }
    return falloff;
}

// Area weight before easing. The field is the signed distance in metres (negative inside). The weight is 0 at the
// boost distance and 1 one range further toward the full-weight side.
float AreaWeight(float field, float range)
{
    if (_AreaSide == LOOGA_AREA_SIDE_EDGE) field = abs(field);
    float distance = field - _AreaBoost;
    if (_AreaSide != LOOGA_AREA_SIDE_OUTSIDE) distance *= -1;
    return saturate(distance / max(0.0001, range));
}

// The area texture covers the tile with tile UV 0..1.
float SplineAreaFalloff(float2 tileUv, float noise)
{
    return AreaWeight(LOOGA_SAMPLE(_AreaDistance, tileUv).r, _AreaRange - noise);
}

// MicroVerse reads painted areas at world = tile noise UV * 1000 m. That is the tile corner plus the sample-centre UV
// times the tile size, so it is not the stamp mapping.
float PaintedWeight(float2 worldNoiseUv)
{
    float2 world = worldNoiseUv * _TerrainSizeXZ * (1000 / _TerrainSizeXZ);
    float3 local = mul(_PaintedAreaWorldToLocal, float4(world.x, 0, world.y, 1)).xyz;
    float2 uv = float2(local.x + 0.5, local.z + 0.5);
#ifdef LOOGA_STAMP_RASTER
    return _PaintedArea.Sample(looga_linear_clamp_sampler, uv);
#else
    return _PaintedArea.SampleLevel(looga_linear_clamp_sampler, uv, 0);
#endif
}

float ShapeFalloff(FalloffInput input, float noise)
{
    float falloff = 1;
    if (_FalloffShape == LOOGA_FALLOFF_BOX)
    {
        falloff = BoxFalloff(input.stampUv, saturate(_FalloffRange.y - noise));
    }
    else if (_FalloffShape == LOOGA_FALLOFF_RANGE)
    {
        falloff = RangeFalloff(input.stampUv, noise);
    }
    else if (_FalloffShape == LOOGA_FALLOFF_TEXTURE)
    {
        falloff = TextureFalloff(input.stampUv, input.stampUvDx, input.stampUvDy, noise);
    }
    else if (LOOGA_IS_AREA_FALLOFF(_FalloffShape))
    {
        falloff = SplineAreaFalloff(input.tileUv, noise);
    }
    if (_UsePaintedArea)
    {
        falloff *= PaintedWeight(input.worldNoiseUv);
    }
    return EaseFalloff(falloff);
}

// Noise in metres. The caller converts it to the units of the falloff.
float FalloffNoise(float2 position)
{
    return NoiseValue(position, _NoiseParams);
}

// Complete falloff with optional noise. The noise moves the falloff edge only where the first weight is below 1.
// The noise is divided by noiseScale (the heightmap real height) like MicroVerse does.
// Global ignores easing and noise. A painted area still multiplies it.
float StampFalloff(FalloffInput input, float noiseScale)
{
    float falloff = 1;
    if (_FalloffShape == LOOGA_FALLOFF_GLOBAL)
    {
        if (_UsePaintedArea)
        {
            falloff = PaintedWeight(input.worldNoiseUv);
        }
    }
    else
    {
        falloff = ShapeFalloff(input, 0);
        if (_NoiseType == LOOGA_NOISE_FBM)
        {
            float2 noiseUv = _NoiseSpace == LOOGA_NOISE_SPACE_STAMP ? input.stampUv : input.worldNoiseUv;
            float noise = FalloffNoise(noiseUv) / noiseScale;
            noise *= 1 - falloff;
            falloff = ShapeFalloff(input, noise);
        }
    }
    return falloff;
}

#endif
