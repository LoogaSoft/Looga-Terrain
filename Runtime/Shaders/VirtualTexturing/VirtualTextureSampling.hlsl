#ifndef LOOGA_VIRTUAL_TEXTURE_SAMPLING
#define LOOGA_VIRTUAL_TEXTURE_SAMPLING
StructuredBuffer<uint> _LoogaVTTable;
Texture2DArray<float4> _LoogaVTAlbedo;
Texture2DArray<float4> _LoogaVTNormal;
Texture2DArray<float4> _LoogaVTEmission;
SamplerState sampler_LoogaVT_linear_clamp;
int _LoogaVTEnabled;
int _LoogaVTCompressed;
int _LoogaVTBlendMips;
float4 _LoogaVTInfo; // Virtual resolution, pages per axis, last mip, preserve close detail.
// x: maximum anisotropy of the mip selection, 1 = isotropic. y: smallest terrain normal y of a cached pixel.
// z: square of the largest footprint ratio of a cached pixel, 0 = no limit. w: most mips that a missing page falls back
// to a coarser page (LoogaVTLevelGrad).
float4 _LoogaVTSampling;
// Global (TerrainVirtualTexture.ShadeDirectly). 1 = receivers that check it shade directly. Feedback continues.
float _LoogaVTShadeDirectly;
// Global. Mip bias of an oversubscribed pool (VirtualSurfaceCache.ResidencyBias), applied to reads and feedback.
float _LoogaVTResidencyBias;
// Anisotropic page reads with up to 4 probes. A page has no mipmaps, so the probes stay inside the page and its border.
// Measured on Kubera (2026-11-03): 8 and 16 probes cost about 0.3 ms more at ground level and looked no sharper.
SamplerState sampler_LoogaVT_linear_clamp_aniso4;

uint LoogaVTPageIndex(uint2 page, uint mip)
{
    uint width = (uint)_LoogaVTInfo.y >> mip;
    uint baseWidth = (uint)_LoogaVTInfo.y;
    uint offset = (baseWidth * baseWidth * 4 - width * width * 4) / 3;
    return offset + page.y * width + page.x;
}

// Virtual mip of a pixel from its UV derivatives. The larger footprint divided by the maximum anisotropy sets the
// mip, but never a mip finer than the smaller footprint. An anisotropy of 1 uses the larger footprint.
float LoogaVTMipGrad(float2 uvDx, float2 uvDy)
{
    float2 dx = uvDx * _LoogaVTInfo.x;
    float2 dy = uvDy * _LoogaVTInfo.x;
    float smaller = min(dot(dx, dx), dot(dy, dy));
    float larger = max(dot(dx, dx), dot(dy, dy));
    float anisotropy = max(_LoogaVTSampling.x, 1.0);
    return clamp(0.5 * log2(max(1, max(smaller, larger / (anisotropy * anisotropy)))) + _LoogaVTResidencyBias, 0,
        _LoogaVTInfo.z);
}

// With "Preserve Close Detail", a pixel smaller than one virtual texel shades directly. A pixel with a footprint
// ratio above the limit, at a very low view angle, also shades directly.
bool LoogaVTCanCacheGrad(float2 uvDx, float2 uvDy)
{
    float2 dx = uvDx * _LoogaVTInfo.x;
    float2 dy = uvDy * _LoogaVTInfo.x;
    float smaller = min(dot(dx, dx), dot(dy, dy));
    float larger = max(dot(dx, dx), dot(dy, dy));
    return (_LoogaVTInfo.w == 0 || smaller >= 1) && (_LoogaVTSampling.z <= 0 || larger <= smaller * _LoogaVTSampling.z);
}

float LoogaVTMip(float2 uv, float derivativeScale)
{
    return LoogaVTMipGrad(ddx(uv) * derivativeScale, ddy(uv) * derivativeScale);
}

bool LoogaVTCanCache(float2 uv, float derivativeScale)
{
    return LoogaVTCanCacheGrad(ddx(uv) * derivativeScale, ddy(uv) * derivativeScale);
}

bool LoogaVTLevel(float2 uv, uint mip, out float4 albedo, out float4 normal, out float4 emission)
{
    albedo = normal = emission = 0;
    for (; mip <= (uint)_LoogaVTInfo.z; mip++)
    {
        uint width = (uint)_LoogaVTInfo.y >> mip;
        float2 grid = clamp(uv, 0, 0.99999994) * width;
        uint2 page = (uint2)floor(grid);
        uint entry = _LoogaVTTable[LoogaVTPageIndex(page, mip)];
        if (entry == 0) continue;
        float2 physicalUV = (frac(grid) * 128 + 4) / 136;
        float3 location = float3(physicalUV, entry - 1);
        albedo = _LoogaVTAlbedo.SampleLevel(sampler_LoogaVT_linear_clamp, location, 0);
        normal = _LoogaVTNormal.SampleLevel(sampler_LoogaVT_linear_clamp, location, 0);
        if (_LoogaVTCompressed != 0) normal.xyz = normal.xyz * 2 - 1;
        emission = _LoogaVTEmission.SampleLevel(sampler_LoogaVT_linear_clamp, location, 0);
        return true;
    }
    return false;
}

bool LoogaVTRead(float2 uv, out float4 albedo, out float4 normal, out float4 emission)
{
    albedo = normal = emission = 0;
    if (_LoogaVTEnabled == 0 || !LoogaVTCanCache(uv, 1)) return false;
    float mip = LoogaVTMip(uv, 1);
    if (!LoogaVTLevel(uv, (uint)floor(mip), albedo, normal, emission)) return false;
    if (_LoogaVTBlendMips == 0) return true;
    float4 nextAlbedo, nextNormal, nextEmission;
    if (LoogaVTLevel(uv, min((uint)ceil(mip), (uint)_LoogaVTInfo.z), nextAlbedo, nextNormal, nextEmission))
    {
        albedo = lerp(albedo, nextAlbedo, frac(mip));
        normal = lerp(normal, nextNormal, frac(mip));
        emission = lerp(emission, nextEmission, frac(mip));
    }
    normal.xyz = normalize(normal.xyz);
    return true;
}

// Gradient of one footprint axis in page texels. The axis is at least 1 texel, so the sampler does not treat the
// read as magnification and keeps its anisotropic probes. It is at most 7 texels, so the probes stay inside the
// 4-texel page border.
float2 LoogaVTClampGradient(float2 gradient)
{
    float texels = sqrt(dot(gradient, gradient));
    return gradient * (clamp(texels, 1.0, 7.0) / max(texels, 1e-6));
}

// LoogaVTLevel with an anisotropic read. uvDx and uvDy are the screen derivatives of the tile UV.
// A missing page falls back at most _LoogaVTSampling.w mips. Beyond that the read fails and the pixel shades directly,
// so a missing page does not show as blur.
bool LoogaVTLevelGrad(float2 uv, float2 uvDx, float2 uvDy, uint mip, out float4 albedo, out float4 normal,
    out float4 emission)
{
    albedo = normal = emission = 0;
    uint lastMip = min((uint)_LoogaVTInfo.z, mip + (uint)_LoogaVTSampling.w);
    for (; mip <= lastMip; mip++)
    {
        uint width = (uint)_LoogaVTInfo.y >> mip;
        float2 grid = clamp(uv, 0, 0.99999994) * width;
        uint2 page = (uint2)floor(grid);
        uint entry = _LoogaVTTable[LoogaVTPageIndex(page, mip)];
        if (entry == 0) continue;
        float3 location = float3((frac(grid) * 128 + 4) / 136, entry - 1);
        // Tile UV to page texels at this mip, then to the physical UV of the 136-texel page.
        float texels = width * 128.0;
        float2 dx = LoogaVTClampGradient(uvDx * texels) / 136.0;
        float2 dy = LoogaVTClampGradient(uvDy * texels) / 136.0;
        albedo = _LoogaVTAlbedo.SampleGrad(sampler_LoogaVT_linear_clamp_aniso4, location, dx, dy);
        normal = _LoogaVTNormal.SampleGrad(sampler_LoogaVT_linear_clamp_aniso4, location, dx, dy);
        if (_LoogaVTCompressed != 0) normal.xyz = normal.xyz * 2 - 1;
        emission = _LoogaVTEmission.SampleGrad(sampler_LoogaVT_linear_clamp_aniso4, location, dx, dy);
        return true;
    }
    return false;
}

// LoogaVTRead with anisotropic page reads and derivatives from the caller. The caller can then read inside a branch.
// The caller checks _LoogaVTEnabled and LoogaVTCanCacheGrad first.
bool LoogaVTReadGrad(float2 uv, float2 uvDx, float2 uvDy, out float4 albedo, out float4 normal, out float4 emission)
{
    float mip = LoogaVTMipGrad(uvDx, uvDy);
    if (!LoogaVTLevelGrad(uv, uvDx, uvDy, (uint)floor(mip), albedo, normal, emission)) return false;
    UNITY_BRANCH
    if (_LoogaVTBlendMips != 0 && frac(mip) > 0.004)
    {
        float4 nextAlbedo = 0, nextNormal = 0, nextEmission = 0;
        if (LoogaVTLevelGrad(uv, uvDx, uvDy, min((uint)ceil(mip), (uint)_LoogaVTInfo.z), nextAlbedo, nextNormal,
            nextEmission))
        {
            albedo = lerp(albedo, nextAlbedo, frac(mip));
            normal = lerp(normal, nextNormal, frac(mip));
            emission = lerp(emission, nextEmission, frac(mip));
        }
    }
    normal.xyz = normalize(normal.xyz);
    return true;
}
#endif
