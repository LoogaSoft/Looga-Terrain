#ifndef LOOGA_TERRAIN_GEOMETRY_INCLUDED
#define LOOGA_TERRAIN_GEOMETRY_INCLUDED

StructuredBuffer<float4> _LoogaPatches;
StructuredBuffer<uint> _LoogaLodMap;
StructuredBuffer<uint> _LoogaLodLeft;
StructuredBuffer<uint> _LoogaLodRight;
StructuredBuffer<uint> _LoogaLodBottom;
StructuredBuffer<uint> _LoogaLodTop;
uint _LoogaNeighborMask;
uint _LoogaPatchOffset;
uint _LoogaLodOffset;
float4 _LoogaNeighborOffsets;
Texture2D<float4> _LoogaHeightmap;
Texture2D<float4> _LoogaHoles;
SamplerState sampler_linear_clamp;
SamplerState sampler_point_clamp;
float4 _LoogaOrigin;
float4 _LoogaSize;
float4 _LoogaHeightInfo;
uint _LoogaPatchCount;
uint _LoogaPatchResolution;

#if defined(LOOGA_TERRAIN_BATCHED)
struct LoogaDrawTile { float4 origin; float4 size; float4 quality; };
StructuredBuffer<LoogaDrawTile> _DrawTiles;
StructuredBuffer<int> _DrawSlices;
StructuredBuffer<float4> _DrawNeighbors;
StructuredBuffer<float4> _DrawPatches;
StructuredBuffer<uint> _DrawLod;
Texture2DArray<float> _DrawHeights;
Texture2DArray<float> _DrawHoles;
uint _DrawPatchCount;
uint _DrawHeightResolution;
static uint _LoogaDrawTile;
#endif

uint LoogaNeighborSize(float2 cell)
{
#if defined(LOOGA_TERRAIN_BATCHED)
    int tile = _LoogaDrawTile;
    float4 neighbors = _DrawNeighbors[tile];
    if (cell.x < 0 && neighbors.x >= 0.0) { tile = (int)neighbors.x; cell.x = _DrawPatchCount - 1; }
    else if (cell.x >= (float)_DrawPatchCount && neighbors.y >= 0.0) { tile = (int)neighbors.y; cell.x = 0; }
    else if (cell.y < 0 && neighbors.z >= 0.0) { tile = (int)neighbors.z; cell.y = _DrawPatchCount - 1; }
    else if (cell.y >= (float)_DrawPatchCount && neighbors.w >= 0.0) { tile = (int)neighbors.w; cell.y = 0; }
    uint2 batchedIndex = (uint2)clamp(floor(cell), 0, _DrawPatchCount - 1);
    return _DrawLod[tile * _DrawPatchCount * _DrawPatchCount
        + batchedIndex.y * _DrawPatchCount + batchedIndex.x];
#else
    uint alongY = (uint)clamp(floor(cell.y), 0, _LoogaPatchCount - 1);
    uint alongX = (uint)clamp(floor(cell.x), 0, _LoogaPatchCount - 1);
    if (cell.x < 0 && (_LoogaNeighborMask & 1) != 0)
    {
        return _LoogaLodLeft[(uint)_LoogaNeighborOffsets.x + alongY * _LoogaPatchCount + _LoogaPatchCount - 1];
    }
    if (cell.x >= (float)_LoogaPatchCount && (_LoogaNeighborMask & 2) != 0)
    {
        return _LoogaLodRight[(uint)_LoogaNeighborOffsets.y + alongY * _LoogaPatchCount];
    }
    if (cell.y < 0 && (_LoogaNeighborMask & 4) != 0)
    {
        return _LoogaLodBottom[(uint)_LoogaNeighborOffsets.z + (_LoogaPatchCount - 1) * _LoogaPatchCount + alongX];
    }
    if (cell.y >= (float)_LoogaPatchCount && (_LoogaNeighborMask & 8) != 0)
    {
        return _LoogaLodTop[(uint)_LoogaNeighborOffsets.w + alongX];
    }
    uint2 index = (uint2)clamp(floor(cell), 0, _LoogaPatchCount - 1);
    return _LoogaLodMap[_LoogaLodOffset + index.y * _LoogaPatchCount + index.x];
#endif
}

float2 LoogaPatchUV(float2 grid, uint instance)
{
#if defined(LOOGA_TERRAIN_BATCHED)
    float4 patch = _DrawPatches[instance];
    _LoogaDrawTile = (uint)patch.w;
    uint patchCount = _DrawPatchCount;
#else
    float4 patch = _LoogaPatches[_LoogaPatchOffset + instance];
    uint patchCount = _LoogaPatchCount;
#endif
    float2 cell = patch.xy + grid * patch.z;
    if (grid.x < 0.0001 || grid.x > 0.9999)
    {
        float side = grid.x < 0.5 ? -0.001 : 0.001;
        uint neighbor = LoogaNeighborSize(cell + float2(side, -0.001));
        if ((float)neighbor > patch.z)
        {
            float step = (float)neighbor / _LoogaPatchResolution;
            cell.y = floor(cell.y / step + 0.0001) * step;
        }
    }
    if (grid.y < 0.0001 || grid.y > 0.9999)
    {
        float side = grid.y < 0.5 ? -0.001 : 0.001;
        uint neighbor = LoogaNeighborSize(cell + float2(-0.001, side));
        if ((float)neighbor > patch.z)
        {
            float step = (float)neighbor / _LoogaPatchResolution;
            cell.x = floor(cell.x / step + 0.0001) * step;
        }
    }
    return cell / patchCount;
}

float LoogaHeight(float2 uv)
{
#if defined(LOOGA_TERRAIN_BATCHED)
    float2 texelUV = (saturate(uv) * (_DrawHeightResolution - 1) + 0.5) / _DrawHeightResolution;
    return _DrawHeights.SampleLevel(sampler_linear_clamp,
        float3(texelUV, _DrawSlices[_LoogaDrawTile]), 0).r * (65535.0 / 32766.0);
#else
    float2 texelUV = (saturate(uv) * (_LoogaHeightInfo.x - 1) + 0.5) / _LoogaHeightInfo.x;
    return _LoogaHeightmap.SampleLevel(sampler_linear_clamp, texelUV, 0).r * _LoogaHeightInfo.y;
#endif
}

float3 LoogaPosition(float2 uv)
{
#if defined(LOOGA_TERRAIN_BATCHED)
    LoogaDrawTile tile = _DrawTiles[_LoogaDrawTile];
    return tile.origin.xyz + float3(uv.x * tile.size.x, LoogaHeight(uv) * tile.size.y, uv.y * tile.size.z);
#else
    return _LoogaOrigin.xyz + float3(uv.x * _LoogaSize.x, LoogaHeight(uv) * _LoogaSize.y, uv.y * _LoogaSize.z);
#endif
}

float3 LoogaNormal(float2 uv)
{
#if defined(LOOGA_TERRAIN_BATCHED)
    float resolution = _DrawHeightResolution;
    float3 size = _DrawTiles[_LoogaDrawTile].size.xyz;
#else
    float resolution = _LoogaHeightInfo.x;
    float3 size = _LoogaSize.xyz;
#endif
    float step = 1.0 / (resolution - 1);
    float2 low = max(0, uv - step);
    float2 high = min(1, uv + step);
    float dx = (LoogaHeight(float2(high.x, uv.y)) - LoogaHeight(float2(low.x, uv.y))) * size.y;
    float dz = (LoogaHeight(float2(uv.x, high.y)) - LoogaHeight(float2(uv.x, low.y))) * size.y;
    return normalize(float3(-dx / max(0.00001, (high.x - low.x) * size.x), 1,
        -dz / max(0.00001, (high.y - low.y) * size.z)));
}

void LoogaClipHoles(float2 uv)
{
#if defined(LOOGA_TERRAIN_BATCHED)
    clip(_DrawHoles.SampleLevel(sampler_point_clamp,
        float3(saturate(uv), _DrawSlices[_LoogaDrawTile]), 0).r - 0.5);
#else
    clip(_LoogaHoles.SampleLevel(sampler_point_clamp, saturate(uv), 0).r - 0.5);
#endif
}
#endif

