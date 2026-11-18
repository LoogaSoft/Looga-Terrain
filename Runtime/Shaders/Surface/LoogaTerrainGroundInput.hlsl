#ifndef LOOGA_TERRAIN_GROUND_INPUT_INCLUDED
#define LOOGA_TERRAIN_GROUND_INPUT_INCLUDED

// Inputs of the terrain ground (LoogaTerrainGround.hlsl): the textures and the values that LoogaTerrainGroundBlock sets
// for each renderer. The material buffer of a shader that reads the ground also declares _GroundTriplanarScale,
// _GroundTriplanarContrast, _GroundNormalStrength, _GroundLayout, _GroundSurfaceUV and _GroundLuminance.
#include "Packages/com.loogasoft.loogaterrain/Runtime/Shaders/Surface/LoogaTerrainSurfaceLayers.hlsl"
// The reader of the packed control maps.
#include "Packages/com.loogasoft.loogaterrain/Runtime/Shaders/Surface/LoogaTerrainControlPacked.hlsl"

// Terrain ground: the layer arrays and the per-layer property table of the terrain surface. _GroundLayout selects
// the layout. 0 = MicroSplat: column = layer, row 0 = UV scale and offset, row 1 = tint, row 3 = brightness and
// contrast, row 9 A = saturation. 1 = Looga terrain surface, compact arrays; 2 = Looga terrain surface, quality arrays
// with the mask array (rows in LoogaTerrainSurfaceLayers.hlsl). LoogaTerrainGroundBlock sets the Looga values for each
// renderer when the terrain below it draws with a Looga terrain surface.
TEXTURE2D_ARRAY(_GroundAlbedoArray);
SAMPLER(sampler_GroundAlbedoArray);
TEXTURE2D_ARRAY(_GroundNormalArray);
SAMPLER(sampler_GroundNormalArray);
TEXTURE2D_ARRAY(_GroundMaskArray);
TEXTURE2D(_GroundLayerProps);

// Splat control maps of the terrain tile under the renderer, set for each renderer. Eight maps hold 32 layers.
TEXTURE2D(_GroundControl0);
TEXTURE2D(_GroundControl1);
TEXTURE2D(_GroundControl2);
TEXTURE2D(_GroundControl3);
TEXTURE2D(_GroundControl4);
TEXTURE2D(_GroundControl5);
TEXTURE2D(_GroundControl6);
TEXTURE2D(_GroundControl7);
// Packed control maps of the tile (LoogaPackedControls). When _GroundControlPacked.x is 1 they replace the control maps.
// y is their mip count, zw their size in texels.
TEXTURE2D(_GroundControlIndex);
TEXTURE2D(_GroundControlWeight);
float4 _GroundControlPacked;
// Tile minimum world X and Z, then the inverse tile size.
float4 _GroundTile;
float _GroundControlResolution;
float _GroundLayerCount;
// Stochastic sampling of the terrain surface: x enabled, y cell scale, z contrast.
float4 _GroundStochastic;

#endif
