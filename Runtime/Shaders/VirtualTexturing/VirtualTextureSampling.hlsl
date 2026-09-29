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

uint LoogaVTPageIndex(uint2 page, uint mip)
{
    uint width = (uint)_LoogaVTInfo.y >> mip;
    uint baseWidth = (uint)_LoogaVTInfo.y;
    uint offset = (baseWidth * baseWidth * 4 - width * width * 4) / 3;
    return offset + page.y * width + page.x;
}

float LoogaVTMip(float2 uv, float derivativeScale)
{
    float2 dx = ddx(uv) * _LoogaVTInfo.x * derivativeScale;
    float2 dy = ddy(uv) * _LoogaVTInfo.x * derivativeScale;
    return clamp(0.5 * log2(max(1, max(dot(dx, dx), dot(dy, dy)))), 0, _LoogaVTInfo.z);
}

bool LoogaVTCanCache(float2 uv, float derivativeScale)
{
    float2 dx = ddx(uv) * _LoogaVTInfo.x * derivativeScale;
    float2 dy = ddy(uv) * _LoogaVTInfo.x * derivativeScale;
    return _LoogaVTInfo.w == 0 || min(dot(dx, dx), dot(dy, dy)) >= 1;
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
#endif
