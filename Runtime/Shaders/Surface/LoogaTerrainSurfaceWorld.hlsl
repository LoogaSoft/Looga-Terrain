#ifndef LOOGA_TERRAIN_SURFACE_WORLD_INCLUDED
#define LOOGA_TERRAIN_SURFACE_WORLD_INCLUDED

// World data of the terrain surfaces: the world colour map and the ground cover maps. LoogaTerrainSurfaceGlobals
// binds them as global properties, so terrain, grass and other shaders read the same data. Other shaders can include
// this file to read the world colour map.

// World colour map. One texture over an area, or tiles: one array slice per square cell and an index texture.
TEXTURE2D(_LoogaWorldColorMap);
SAMPLER(sampler_LoogaWorldColorMap);
TEXTURE2D_ARRAY(_LoogaWorldColorTiles);
SAMPLER(sampler_LoogaWorldColorTiles);
// Slice of each cell, or -1 for a cell without a slice. Read with loads, not with a sampler.
TEXTURE2D(_LoogaWorldColorTileIndex);
float4 _LoogaWorldColorState;        // x 0 none, 1 one map, 2 tiles; y colour scale
float4 _LoogaWorldColorArea;         // One map: minimum world X and Z, then the inverse area size
float4 _LoogaWorldColorTileGrid;     // Tiles: minimum world X and Z of the cell of index texel (0, 0), inverse cell size
float4 _LoogaWorldColorTileSlice;    // Tiles: border and cell width as shares of the slice width, index size X and Z

// Ground cover. The coverage map holds the plant coverage seen from above in R, and in G the share of the ground that
// takes a plant colour cast where it is bare. The canopy map holds in A the share of the plants with their own colour,
// in RGB their linear colour times that share. The other plants take the world colour map.
TEXTURE2D(_LoogaGroundCoverMap);
SAMPLER(sampler_LoogaGroundCoverMap);
TEXTURE2D(_LoogaGroundCoverCanopy);
SAMPLER(sampler_LoogaGroundCoverCanopy);
float4 _LoogaGroundCoverState;       // x 1 when the maps are bound, y canopy colour scale
float4 _LoogaGroundCoverArea;        // Minimum world X and Z, then the inverse area size
float4 _LoogaGroundCoverMapShade;    // Plant shading of the world colour map part: RGB factor, A saturation
float4 _LoogaGroundCoverBare;        // RGB linear canopy colour of the bare far cover, where the maps have no plants

// Region colour map (LoogaRegionColor): one colour map over one area, for example the colour output of a terrain tool.
// An optional weight map sets where the map applies. The terrain layers keep their own fine brightness variation.
TEXTURE2D(_LoogaRegionColorMap);
SAMPLER(sampler_LoogaRegionColorMap);
TEXTURE2D(_LoogaRegionColorWeight);
SAMPLER(sampler_LoogaRegionColorWeight);
float4 _LoogaRegionColorState;       // x 1 when bound, y layer detail strength, z 1 when a weight map is bound
float4 _LoogaRegionColorArea;        // Minimum world X and Z, then the inverse area size
float4 _LoogaRegionColorFade;        // x strength near, y strength far, z fade start, w fade end (camera distance)
float4 _LoogaRegionColorGrade;       // x brightness, y saturation of the map colour

// Map coordinates of a world position: UV in XY and the array slice in Z. W is 1 where a bound map covers the
// position, else 0.
float4 LoogaWorldColorCoord(float2 positionXZ)
{
    float4 coord = 0;
    UNITY_BRANCH
    if (_LoogaWorldColorState.x > 1.5)
    {
        float2 grid = (positionXZ - _LoogaWorldColorTileGrid.xy) * _LoogaWorldColorTileGrid.z;
        float2 cell = floor(grid);
        bool inside = all(cell >= 0) && all(cell < _LoogaWorldColorTileSlice.zw);
        float slice = inside ? LOAD_TEXTURE2D(_LoogaWorldColorTileIndex, int2(cell)).r : -1;
        coord = float4(_LoogaWorldColorTileSlice.x + (grid - cell) * _LoogaWorldColorTileSlice.y, max(slice, 0),
            slice >= 0 ? 1 : 0);
    }
    else
    {
        float2 uv = (positionXZ - _LoogaWorldColorArea.xy) * _LoogaWorldColorArea.zw;
        coord = float4(uv, 0, _LoogaWorldColorState.x > 0.5 && all(uv >= 0) && all(uv <= 1) ? 1 : 0);
    }
    return coord;
}

// The filtered map colour, scaled. The gradients are the screen derivatives of the world XZ position. The
// derivatives of the slice UV jump at a cell edge and would select the smallest mip level there.
half3 LoogaWorldColorGrad(float4 coord, float2 positionDx, float2 positionDy)
{
    half3 color;
    UNITY_BRANCH
    if (_LoogaWorldColorState.x > 1.5)
    {
        float scale = _LoogaWorldColorTileGrid.z * _LoogaWorldColorTileSlice.y;
        color = SAMPLE_TEXTURE2D_ARRAY_GRAD(_LoogaWorldColorTiles, sampler_LoogaWorldColorTiles, coord.xy, coord.z,
            positionDx * scale, positionDy * scale).rgb;
    }
    else
    {
        color = SAMPLE_TEXTURE2D_GRAD(_LoogaWorldColorMap, sampler_LoogaWorldColorMap, coord.xy,
            positionDx * _LoogaWorldColorArea.zw, positionDy * _LoogaWorldColorArea.zw).rgb;
    }
    return color * (half)_LoogaWorldColorState.y;
}

#endif
