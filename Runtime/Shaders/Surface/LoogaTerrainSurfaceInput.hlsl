#ifndef LOOGA_TERRAIN_SURFACE_INPUT_INCLUDED
#define LOOGA_TERRAIN_SURFACE_INPUT_INCLUDED

// Material inputs of the Looga terrain surface. Both shader targets include this file, so the constant buffer
// layout is the same in every pass. LoogaTerrainSurface.ApplyTo writes the shared values. The terrain binding
// writes the per-tile values through the splat material property block.

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

// The control-map reader reads at most eight control maps (32 layers).
#define LOOGA_SURFACE_MAX_CONTROLS 8

// The reader of the packed control maps.
#include "Packages/com.loogasoft.loogaterrain/Runtime/Shaders/Surface/LoogaTerrainControlPacked.hlsl"

// The rows of the per-layer property texture and the shared layer functions.
#include "Packages/com.loogasoft.loogaterrain/Runtime/Shaders/Surface/LoogaTerrainSurfaceLayers.hlsl"

// Debug views. Keep these values the same as LoogaTerrainSurface.DebugView.
#define LOOGA_SURFACE_DEBUG_ALBEDO 1
#define LOOGA_SURFACE_DEBUG_NORMAL 2
#define LOOGA_SURFACE_DEBUG_SMOOTHNESS_OCCLUSION 3
#define LOOGA_SURFACE_DEBUG_HEIGHT 4
#define LOOGA_SURFACE_DEBUG_LAYER_WEIGHTS 5
#define LOOGA_SURFACE_DEBUG_TOP_LAYER 6
#define LOOGA_SURFACE_DEBUG_TRIPLANAR_WEIGHTS 7
#define LOOGA_SURFACE_DEBUG_SAMPLE_COUNT 8
#define LOOGA_SURFACE_DEBUG_WORLD_COLOR 10
#define LOOGA_SURFACE_DEBUG_GROUND_COVER 11

CBUFFER_START(UnityPerMaterial)
    float4 _LoogaSurfaceUV;          // xy world-to-UV scale, zw UV offset
    float4 _LoogaSurfaceBlend;       // x mode (0 normalized linear, 1 height, 2 hybrid, 3 height offset, 4 crisp edge,
                                     // 5 variance-preserving linear), y layers per pixel, z height contrast, w height reach
    float4 _LoogaSurfaceHybrid;      // x start distance, y end distance of the height-to-linear fade
    float4 _LoogaSurfaceEdge;        // x crisp edge width in pixels, y edge noise frequency per meter, z edge noise
                                     // strength, w largest contrast gain of the variance-preserving blend
    float4 _LoogaSurfaceFade;        // x fade distance mode (0 camera distance, 1 screen footprint), y meters of
                                     // fade distance per meter of footprint
    float4 _LoogaSurfaceStochastic;  // x enabled, y cell scale, z contrast
    float4 _LoogaSurfaceLuminance;   // rgb luminance weights of the saturation adjustment
    float4 _LoogaSurfaceDebug;       // x debug view
    // Anti-tiling. A strength of 0 turns a feature off. Sizes are in world meters.
    float4 _LoogaDetailNoise;        // x UV per meter, y strength, z fade distance
    float4 _LoogaDistanceNoise;      // x UV per meter, y strength, z fade start, w fade range
    float4 _LoogaNormalNoise1;       // x UV per meter, y strength
    float4 _LoogaNormalNoise2;
    float4 _LoogaNormalNoise3;
    float4 _LoogaNoiseHeight;        // x frequency per meter, y amplitude
    float4 _LoogaControlNoise;       // x frequency per meter, y offset in meters
    float4 _LoogaResample;           // x UV scale, y fade start, z fade end, w fade mode (0 cross-fade, 1 noise, 2 constant)
    float4 _LoogaResampleBlend;      // x albedo strength, y normal strength, z albedo blend (0 linear, 1 overlay,
                                     // 2 lighter colour), w noise frequency per meter
    float4 _LoogaResampleNoise;      // x noise strength
    float4 _LoogaTriplanar;          // x enabled, y weight exponent, z cosine of the gate start slope, w of the gate end slope
    float4 _LoogaTriplanar2;         // x mode (0 plain, 1 height blended), y height blend contrast, z face normal
                                     // threshold (1 - cos, 1 = off), w face normal blend range
    float4 _LoogaParallax;           // x depth in meters (0 = off), y fade start, z fade end distance, w maximum steps
    // World colour tint. A strength of 0 or a blend mode of 0 turns it off.
    float4 _LoogaWorldColor;         // x near strength, y far strength, z fade start, w fade end distance
    float4 _LoogaWorldColor2;        // x blend mode (0 off, 1 multiply x2, 2 overlay, 3 cross-fade), y cosine of the
                                     // slope where the fade-out starts, z where it ends
    // Ground cover lighting rules. A strength of 0 turns it off.
    float4 _LoogaGroundCover;        // x strength, y canopy start, z canopy end distance, w far coverage mip
    float4 _LoogaGroundCoverShade;   // x under-canopy shade, y under-canopy tint, z under-canopy albedo, w canopy albedo
    float4 _LoogaGroundCoverSoil;    // x soil luminance, y soil detail, z soil relief, w canopy shadow density
    float4 _LoogaGroundCoverView;    // x bare cast near, y bare cast far, z plant aspect, w maximum grazing term
    float4 _LoogaGroundCoverDense;   // x, y coverage range where the view angle term starts, z canopy occlusion,
                                     // w canopy sheen
    float4 _LoogaGroundCoverDetail;  // x canopy detail UV per meter, y luminance strength, z normal strength,
                                     // w canopy sun sheen
    // Far detail beyond the texture detail. A strength of 0 turns each part off.
    float4 _LoogaFarDetail;          // x fade start distance, y 1 / fade range, z cosine of the slope where the strata
                                     // start, w cosine of the slope where they are full
    float4 _LoogaFarStrata;          // x bands per meter (1 / band thickness), y height warp in meters, z albedo
                                     // strength, w ledge relief strength
    float4 _LoogaFarCurvature;       // x albedo strength, y sample step in meters, z curvature (1 / m) of full strength
    // Surface fields. Ranges are a start and 1 / width on the stored 0 to 1 values, except the shape ranges, which use
    // the relative heights as -1 to 1. A strength of 0 turns each part off.
    float4 _LoogaFieldWetness;       // x darkening, y smoothness gain, zw wetness range
    float4 _LoogaFieldChannel;       // x darkening, y smoothness gain, zw flow area range
    float4 _LoogaFieldRim;           // x lightening, y ground cover loss, zw rim range
    float4 _LoogaFieldFoot;          // rgb linear deposit colour, a strength
    float4 _LoogaFieldFootRange;     // xy foot range, z 1 when a field part is on, w cosine of the slope where rim
                                     // and foot end
    float4 _LoogaControlTexelSize;   // Per tile: 1 / width, 1 / height, width, height of the control maps
    float4 _LoogaTerrainSize;        // Per tile: x and z size in meters, then 1 / x size, 1 / z size
    float4 _LoogaControlPacked;      // Per tile: x 1 when the packed control maps hold the layers, y their mip count
    float _LoogaControlCount;        // Per tile: number of bound control maps
CBUFFER_END

TEXTURE2D_ARRAY(_LoogaAlbedoArray);
SAMPLER(sampler_LoogaAlbedoArray);
TEXTURE2D_ARRAY(_LoogaNormalArray);
SAMPLER(sampler_LoogaNormalArray);
// Quality layout only: smoothness, occlusion and metallic. It uses the sampler of the normal array.
TEXTURE2D_ARRAY(_LoogaMaskArray);
TEXTURE2D(_LoogaLayerProps);

// Anti-tiling textures. They use sampler_TrilinearRepeat from the core global samplers.
TEXTURE2D(_LoogaDetailNoiseTex);     // rg normal x and y, b luminance (0.5 = no change)
TEXTURE2D(_LoogaDistanceNoiseTex);   // Same packing as the detail noise
TEXTURE2D(_LoogaNormalNoiseTex1);    // Normal maps
TEXTURE2D(_LoogaNormalNoiseTex2);
TEXTURE2D(_LoogaNormalNoiseTex3);

// Per tile: wetness, relative height 4 m, relative height 20 m, flow area (LoogaTerrainSurfaceFields).
TEXTURE2D(_LoogaSurfaceFields);

TEXTURE2D(_Control0);
TEXTURE2D(_Control1);
TEXTURE2D(_Control2);
TEXTURE2D(_Control3);
TEXTURE2D(_Control4);
TEXTURE2D(_Control5);
TEXTURE2D(_Control6);
TEXTURE2D(_Control7);
// The control maps use sampler_LinearClamp from the core global samplers.
// Packed control maps (LoogaPackedControls): the 4 strongest layers of each texel. The shader loads them and filters
// itself. When _LoogaControlPacked.x is 1 they replace the control maps.
TEXTURE2D(_LoogaControlIndex);
TEXTURE2D(_LoogaControlWeight);

#endif
