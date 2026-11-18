#ifndef LOOGA_WATER_SURFACE_INCLUDED
#define LOOGA_WATER_SURFACE_INCLUDED

// Surface of Looga/Water: the water of bodies and channels and, with _WATER_FALL, the sheets of falling water. Both use
// the same lighting, reflection, refraction and foam code, so a body and the fall that it feeds look like one surface.

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareOpaqueTexture.hlsl"
#include "Packages/com.loogasoft.loogaterrain/Runtime/Water/Shaders/LoogaWaterWaves.hlsl"
#include "Packages/com.loogasoft.loogaterrain/Runtime/Water/Shaders/LoogaWaterFoam.hlsl"
#include "Packages/com.loogasoft.loogaterrain/Runtime/Water/Shaders/LoogaShoreSwash.hlsl"

TEXTURE2D(_NormalMap);
SAMPLER(sampler_NormalMap);
TEXTURE2D(_FoamMap);
SAMPLER(sampler_FoamMap);
TEXTURE2D(_FoamDetailMap);
SAMPLER(sampler_FoamDetailMap);
TEXTURE2D(_ShoreMap);
SAMPLER(sampler_ShoreMap);
TEXTURE2D(_OceanCoverageMap);
SAMPLER(sampler_OceanCoverageMap);
TEXTURE2D(_FoamSim);
SAMPLER(sampler_FoamSim);
TEXTURE2D(_ShoreWaveMap);
// Read with sampler_ShoreMap (the same grid): the ocean variant uses all 16 sampler slots.
TEXTURE2D(_PlungeWaveMap);
TEXTURE2D(_PlungeAlongMap);
TEXTURE2D(_ShoreFineMap);
SAMPLER(sampler_ShoreFineMap);
SAMPLER(sampler_ShoreWaveMap);
// Set by LoogaOceanWaves: one layer for each cascade. Displacement xyz in meters, w = Jacobian.
// Derivatives xy = height slope along x and z (damped by the horizontal compression), zw = horizontal
// stretch along x and z. Foam r = whitecap foam from 0 to 1.
TEXTURE2D_ARRAY(_LoogaOceanDisplacement);
SAMPLER(sampler_LoogaOceanDisplacement);
TEXTURE2D_ARRAY(_LoogaOceanDerivatives);
SAMPLER(sampler_LoogaOceanDerivatives);
TEXTURE2D_ARRAY(_LoogaOceanFoam);
SAMPLER(sampler_LoogaOceanFoam);
// x = cascade count (0 = no ocean waves), y = texels along each side of a cascade tile.
float4 _LoogaOceanParams;
// For each cascade: x = 1 / tile size, y = displacement scale, z = normal scale, w = tile size in meters.
float4 _LoogaOceanCascades[4];
// xy = wind direction (unit vector in world XZ), zw = total wind drift of the foam in world XZ, in meters.
float4 _LoogaOceanWind;
// _LoogaOceanGrid (LoogaShoreSwash.hlsl): inside its radius the near grid draws the water and the surface
// mesh does not.
// 1 on the near grid draw (material property block), 0 on the surface mesh.
float _OceanGridMesh;
// Width of the dry ground next to the water where the near grid also draws, in meters.
static const float GridDryMargin = LOOGA_SWASH_DRY_MARGIN;
// In the dry margin the near grid draws only over ground that is at most this far below the water, in
// metres. Past a crest or a cliff edge the ground falls away and cannot hide the grid.
static const float GridDryDrop = 3.0;
// Flow speeds where the ocean foam detail starts to move with a current and moves fully with it, in meters
// per second.
static const float FoamFlowStart = 0.4;
static const float FoamFlowFull = 1.2;
// Size of the current foam detail, as a share of the foam detail size.
static const float CurrentFoamScale = 0.45;
// Contrast of the clumps in the current foam detail, from 0 (flat) to 1.
static const float CurrentFoamClumps = 0.3;
// Hex tiling: cells per texture tile (1 = a triangle side of about a third of a tile) and the power that
// sharpens the blend between the cells.
static const float HexFoamCells = 0.6;
static const float HexFoamSharpness = 4.0;
// Cell size of the noise that shifts the flow phase over the surface, in meters.
static const float FlowJitterSize = 48.0;
// Flow cycle of the current foam detail in seconds, and the contrast of the detail.
static const float CurrentFoamCycle = 1.5;
static const float CurrentFoamContrast = 1.15;
// Height range of the noise that breaks up the water edge in the swash band, in meters. On a gentle beach
// a few centimeters move the edge by tens of centimeters.
static const float EdgeBreakupHeight = 0.06;

CBUFFER_START(UnityPerMaterial)
    float _WaveScale;
    float4 _WaveA;
    float4 _WaveB;
    float4 _WaveC;
    float4 _WaveD;
    float _OceanHeight;
    float _OceanShoreDepth;
    float _OceanRippleDepth;
    float _OceanNormalStrength;
    float _OceanCrestHeight;
    float _OceanFoam;
    float _OceanNormalFalloff;
    float _OceanFoamFalloff;
    float _ShoreWaveHeight;
    float _ShoreWavePeriod;
    float _ShoreWaveReach;
    float _ShoreWaveSteepness;
    float _SurfFoam;
    float _SurfFoamTime;
    float _PlungeWaveHeight;
    float _PlungeWavePeriod;
    float _PlungeWaveReach;
    float _PlungeFoam;
    float _PlungeFoamTime;
    float _SwashHeight;
    float _FlowCycle;
    float _FastSpeed;
    float _FastNormal;
    float _FastFoam;
    float _FastFoamStart;
    float _FlowWaveDamping;
    float _WaveCalmStart;
    float _WaveCalmSpeed;
    float _Turbulence;
    float _TurbulenceStart;
    float _TurbulenceFull;
    float _RegimeTurbulence;
    float _RegimeBoils;
    float _RegimeWhiteWater;
    float _DetailSizeA;
    float _DetailSizeB;
    float _DetailStrengthA;
    float _DetailStrengthB;
    float4 _DetailDrift;
    float _MacroSize;
    float _MacroStrength;
    float _SwellSize;
    float _SwellStrength;
    float _DetailFadeDistance;
    float _FarDetail;
    float _FarDetailScale;
    float _SlickSize;
    float _SlickStrength;
    float _WaveWarp;
    float _WaveWarpSize;
    float _FarWaveNormal;
    float4 _Absorption;
    float _AbsorptionScale;
    half4 _DeepColor;
    float _MaxPath;
    float _RefractionStrength;
    float _Smoothness;
    float _FarSmoothness;
    float _ReflectionStrength;
    float _ScreenReflection;
    float _SunStrength;
    float _FresnelNormalFlatten;
    half4 _SSSColor;
    float _SSSStrength;
    float _SSSPower;
    float _SSSDistortion;
    half4 _FoamColor;
    float _FoamDetailSize;
    float _ShoreFoamWidth;
    float _ShoreFoam;
    float _ShoreFoamPulse;
    float _ShoreFoamDepth;
    float _ContactFoamDepth;
    float _ContactFoam;
    float _SurfaceFoam;
    float _CrestFoamStart;
    float _CrestFoam;
    float _FoamSoftness;
    float _FoamTranslucency;
    float _FoamOcclusion;
    float _SubFoam;
    float _SubFoamDepth;
    float _FoamFadeDistance;
    float4 _ShoreMapRect;
    float4 _OceanCoverageRect;
    float _OceanCoverageOpen;
    float4 _ShoreFineRect;
    float4 _FoamSimRect;
    float4 _FoamSimFade;
    // Time since the last foam simulation step, in seconds.
    float _FoamSimAge;
    float _EdgeFade;
    float _DepthOffset;
    float _WaterZWrite;
    float _WaterColorMask;
    // Set by the Looga water body for each renderer: the weld line where a fall sheet starts (WaterWeldField).
    // xy = world XZ origin, zw = 1 / size. Zero = no weld. Params: x = distance from the weld line to the brink,
    // y = calm distance before the weld line, z = share of the ripples at the brink, all in meters.
    float4 _WaterWeldRect;
    float4 _WaterWeldParams;

    // Fall mode (_WATER_FALL). The fall properties of a fall material are copied onto the material of the water
    // that feeds the fall, so all other values are the same on both sides of the weld.
    float _FallStreakWidth;
    float _FallNormalStrength;
    float _FallBulgeHeight;
    float _FallBoilHeight;
    float _FallEndPin;
    float _FallEndSink;
    float _FallSeaBoil;
    float _FallShellBulge;
    float _FallShellCoverage;
    float _FallShellSoftness;
    float _FallFoamNormal;
    float _FallClumpSize;
    float _FallClumpPeriod;
    float _FallClumpHeight;
    float _FallShellClump;
    float _FallClumpShadow;
    float _FallClumpNormal;
    float _FallFoamRim;
    float _FallStartAir;
    float _FallAirStart;
    float _FallAirOnset;
    float _FallFullAirProgress;
    float _FallAirSoftness;
    float _FallMaxAir;
    float _FallWallFoam;
    float _FallWallFoamWidth;
    float _FallPoolFoamSize;
    float _FallPoolFoamSoftness;
    float _FallPoolRaftCover;
    float _FallPoolLength;
    float _FallPoolBoilLength;
    float _FallPoolStreakWidth;
    float _FallPoolStreakLength;
    float _FallPoolFoamReach;
    float _FallPoolAeration;
    half4 _FallAeratedColor;
    float _FallPoolBasinAeration;
    float _FallPoolMilkyReach;
    float _FallPoolBasinCover;
    float _FallPoolMinPath;
    float _FallPoolOpenStart;
    float _FallPoolOpenFade;
    float _FallPoolWarp;
    float _FallPoolTileSpeed;
    float _FallPoolSpread;
    half4 _FallFoamColor;
    float _FallFoamShadow;
    float _FallFoamWrap;
    float _FallTranslucency;
    half4 _FallTranslucencyColor;
    float _FallMinPath;
    float4 _FallClearHandoff;
    float _FallRefraction;
    float _FallStartFade;
    float _FallEndFade;
    float _FallDepthEnd;
    float _FallDepthPushBack;
    float _FallPoolDepthBias;
    // Variation along the lip and break-up of the curtain (0 = an even sheet).
    float _FallJetVariation;
    float _FallJetSize;
    float _FallJetBulge;
    float _FallBreakup;
    float _FallGapShare;
    float _FallGapSpray;
    float _FallStrandFray;
    float _FallCometStrength;
    float _FallCometSize;
    float _FallCometLength;
CBUFFER_END

// The opaque depth, when LoogaWaterReflectionFeature puts the water surfaces into the camera depth texture for
// the transparent objects after the water (for example a height fog overlay). The water itself reads the depth of
// the opaque objects. Read with loads: the ocean variant has no free sampler slot.
TEXTURE2D_X_FLOAT(_LoogaOpaqueDepth);
float _LoogaOpaqueDepthOn;

float WaterLoadSceneDepth(int2 pixel)
{
    float depth;
    [branch] if (_LoogaOpaqueDepthOn > 0.5)
    {
        depth = LOAD_TEXTURE2D_X(_LoogaOpaqueDepth, clamp(pixel, 0, int2(_ScreenSize.xy) - 1)).r;
    }
    else
    {
        depth = LoadSceneDepth(pixel);
    }
    return depth;
}

float WaterSceneDepth(float2 uv)
{
    float depth;
    [branch] if (_LoogaOpaqueDepthOn > 0.5)
    {
        depth = WaterLoadSceneDepth(int2(uv * _ScreenSize.xy));
    }
    else
    {
        depth = SampleSceneDepth(uv);
    }
    return depth;
}

// Read with the shared linear clamp sampler: the ocean variant has no free sampler slot.
TEXTURE2D(_WaterWeldMap);

// Weld line of a fall sheet at a world XZ point: x = signed distance in meters (positive past the line, where the
// sheet draws), y = 1 within the span of the line. A body without a weld gets a far distance and no span.
float2 WaterWeld(float2 grid)
{
    float2 uv = (grid - _WaterWeldRect.xy) * _WaterWeldRect.zw;
    if (_WaterWeldRect.z <= 0 || any(uv <= 0) || any(uv >= 1)) return float2(-1e4, 0);
    return SAMPLE_TEXTURE2D_LOD(_WaterWeldMap, sampler_LinearClamp, uv, 0).rg;
}

// Span value of the weld field at an end of the weld line. Past the end it falls to 0 (WaterWeldField).
static const float WeldEndSpan = 0.45;
// Past an end of the weld line the distance to the brink grows by up to about this distance, in meters, so the calm
// and the rushing water fade out beside the end of the fall.
static const float WeldEndReach = 30.0;

// Distance upstream of the brink, in meters, for a weld field sample (WaterWeld). The weld line lies the sheet
// start upstream of the brink.
float WeldLipDistance(float2 weld)
{
    float end = max(saturate(weld.y / WeldEndSpan), 0.01);
    return _WaterWeldParams.x - weld.x + (1 / end - 1) * WeldEndReach;
}

// The waves move the surface less toward the weld line and not at all on it, so the body and the sheet meet with
// the same vertex positions. 1 without a weld.
float WeldDisplacementCalm(float lipDistance)
{
    if (_WaterWeldRect.z <= 0) return 1;
    return smoothstep(_WaterWeldParams.x, _WaterWeldParams.x + max(_WaterWeldParams.y, 0.01), lipDistance);
}

// Toward the brink the ripples in the light change to a multiple of their strength (Lip Ripples, params z): below 1
// the water smooths out as it speeds up over a smooth crest, above 1 it gets choppy. 1 without a weld.
float WeldNormalCalm(float lipDistance)
{
    if (_WaterWeldRect.z <= 0) return 1;
    float reach = max(_WaterWeldParams.x + _WaterWeldParams.y, 0.01);
    return lerp(_WaterWeldParams.z, 1, smoothstep(0, reach, lipDistance));
}

// Rushing water before the brink, 0 to 1 (Lip Rush, params w): the fast water breaks up into boils and white
// streaks, and its bubbles make it cloudy, so the bed under the lip does not show. 0 without a weld.
float WeldRush(float lipDistance)
{
    if (_WaterWeldRect.z <= 0) return 0;
    float reach = max(_WaterWeldParams.x + _WaterWeldParams.y, 0.01);
    return _WaterWeldParams.w * (1 - smoothstep(0, reach, lipDistance));
}

// Rushing water at the brink: the height of its boils in meters, its white water, and how fast its bubbles hide the
// scene behind it (cloud = rush times this, up to 1).
static const float WeldRushBoils = 0.35;
static const float WeldRushWhiteWater = 0.25;
static const float WeldRushCloud = 3.0;

// Within this distance before the weld line the near ocean grid draws the water even where the coarse shore map
// has no water, so the body reaches the weld line exactly.
static const float WeldGridReach = 24.0;
// Shore data of open water near a weld line: the distance to the water edge and the water depth, in meters.
static const float WeldOpenShoreDistance = 100.0;
static const float WeldOpenDepth = 10.0;

// Width of the fade at the edge of the foam simulation window, in metres.
static const float FoamSimEdgeFade = 24.0;
// Boils of fast water (LoogaBoils in LoogaWaterFoam.hlsl). The mesh moves with the boils where its vertices
// are closer than the first spacing and not past the second. The boils have their full height in water of
// the full depth, in meters. The light of the boils fades out between two view distances in meters.
static const float BoilMeshSpacing = 0.6;
static const float BoilMeshSpacingEnd = 1.6;
static const float BoilFullDepth = 2.0;
static const float BoilFadeStart = 80.0;
static const float BoilFadeEnd = 250.0;
// Blur radius of the simulated foam per metre of view distance, and the largest radius, in metres.
static const float FoamSimBlur = 0.04;
static const float FoamSimMaximumBlur = 12.0;
// Share of the fast speed where the simulated foam starts to replace the shader foam away from a shore,
// and where it replaces it fully. A sea drift is below the start, a river is above it.
static const float FoamSimFastStart = 0.2;
static const float FoamSimFastFull = 0.4;

// Set by LoogaWaterReflectionFeature for the current camera. Each pyramid texel keeps the closest depth of
// a 2 x 2 texel block. Trace level 0 is the camera depth texture, and level n is pyramid mip n - 1.
TEXTURE2D_FLOAT(_LoogaWaterHiZ);
// xy: camera depth size in pixels. z: trace level count. w: 1 = the pyramid is valid for this camera.
float4 _LoogaWaterHiZSize;
// x: maximum trace steps. y: hit thickness in meters. z: screen edge fade. w: maximum ray length in meters.
float4 _LoogaWaterReflection;

// Set by LoogaPlanarReflection for the current camera. x: plane height in meters. y: weight, 0 = off.
// z: mip count. w: distortion by the surface normals, as a share of the screen.
TEXTURE2D(_LoogaPlanarReflection);
float4 _LoogaPlanarReflectionParams;
// 1 while the planar reflection camera renders.
float _LoogaWaterPlanarPass;
// Water within this height of the reflection plane uses the planar reflection, in meters.
static const float PlanarHeightTolerance = 0.25;

// The trace compares signed device depth, so a larger value is always farther from the camera. A sign
// change keeps the full precision of the reversed depth.
// Below this visible share of the reflection, the water does not trace a screen-space ray.
static const float MinimumScreenReflection = 0.04;

#if UNITY_REVERSED_Z
    static const float DepthSign = -1;
#else
    static const float DepthSign = 1;
#endif

struct Attributes
{
    float4 positionOS : POSITION;
    // xy: world XZ flow in meters per second. z: surface alpha. w: mesh vertex spacing in meters.
    float4 flow : TEXCOORD0;
    #if defined(_WATER_FALL)
    // x: turbulence, y: aeration of the flow regime, 0 to 1, of the water at the weld. z: the pattern shift of the
    // grid, so the distance along the lip is sheet.x - z.
    float4 regime : TEXCOORD1;
    #else
    // x: turbulence, y: aeration of the flow regime, 0 to 1. Zero without a water network.
    float2 regime : TEXCOORD1;
    #endif
    #if defined(_WATER_FALL)
    // The sheet frame: the tangent points across the sheet and the flow runs down the bitangent.
    float3 normalOS : NORMAL;
    float4 tangentOS : TANGENT;
    // x = streak texture tiles per second of travel time (one value per sheet), y = flow speed, z = distance along
    // the surface past the brink
    // (negative before it), w = travel time of the water from the sheet start (seconds).
    float4 fallFlow : TEXCOORD2;
    // x = distance across the sheet, y = texture tiles along the flow from the start, z = progress (below 0 before
    // the brink, 0 to 1 on the drop, 1 to 2 over the pool), w = distance from the sheet start.
    float4 sheet : TEXCOORD3;
    // x = distance to the nearest wall or pier wake, y = water thickness, z = distance to the sheet end, w = 1 on a
    // foam shell over the jet. Distances in sheet units (meters times the frame scale).
    float4 extra : TEXCOORD4;
    #endif
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct Varyings
{
    float4 positionCS : SV_POSITION;
    float3 positionWS : TEXCOORD0;
    float3 normalWS : TEXCOORD1;
    // xy: world XZ before the waves move the vertex. The detail textures stay on the wave. z: crest
    // height from -1 to 1 (on a fall sheet: the distance along the lip). w: fog factor.
    float4 surface : TEXCOORD2;
    // xy: world XZ flow in meters per second. z: surface alpha. w: surface height before the waves.
    float4 flow : TEXCOORD3;
    // x: turbulence, y: aeration of the flow regime, 0 to 1.
    float2 regime : TEXCOORD4;
    #if defined(_WATER_FALL)
    float4 tangentWS : TEXCOORD5;
    float4 sheet : TEXCOORD6;
    float4 extra : TEXCOORD7;
    // x = tiles per second, y = speed, z = distance past the brink, w = travel time.
    float4 fallFlow : TEXCOORD8;
    #endif
    UNITY_VERTEX_INPUT_INSTANCE_ID
    UNITY_VERTEX_OUTPUT_STEREO
};

// Wind waves do not cross fast water in a fixed pattern. They fade with the flow speed and with the
// turbulence of the flow regime.
float FlowWaveWeight(float2 flow, float turbulence)
{
    float calmSpeed = _WaveCalmSpeed > 0 ? _WaveCalmSpeed : _FastSpeed;
    float range = max(calmSpeed - _WaveCalmStart, 0.01);
    float calming = max(saturate((length(flow) - _WaveCalmStart) / range),
        turbulence * _RegimeTurbulence);
    return 1 - _FlowWaveDamping * calming;
}

// Shore data at a point (R signed distance to the water edge, G depth), from a cubic sample of the shore map.
// Near the camera the fine shore map (half metre texels) follows the ground, and it blends to the shore map
// over the outer 15 % of its window.
float2 OceanShore(float2 grid, float2 shoreUV)
{
    float2 shore = LoogaSampleCubic(TEXTURE2D_ARGS(_ShoreMap, sampler_ShoreMap), shoreUV).rg;
    [branch] if (_ShoreFineRect.z > 0)
    {
        float2 fineUV = (grid - _ShoreFineRect.xy) * _ShoreFineRect.zw;
        float2 border = min(fineUV, 1 - fineUV);
        float weight = saturate(min(border.x, border.y) / 0.15);
        [branch] if (weight > 0)
        {
            shore = lerp(shore, SAMPLE_TEXTURE2D_LOD(_ShoreFineMap, sampler_ShoreFineMap, fineUV, 0).rg,
                weight);
        }
    }
    // Toward a weld line the water is open water, not a shore: the outline of the body need not follow the lip,
    // and the coarse shore map has no water past the outline. The waves calm toward the weld line instead.
    float2 weld = WaterWeld(grid);
    float span = saturate((weld.y - WeldEndSpan) / (1 - WeldEndSpan));
    float open = span * (1 - smoothstep(0.5 * WeldGridReach, WeldGridReach, -weld.x));
    return lerp(shore, float2(WeldOpenShoreDistance, WeldOpenDepth), open);
}

// Water depth at a point from the shore map, in meters. The shore map covers the detail area of the body.
// Outside it the water counts as deep. Outside the mesh (shore map depth -1) the depth is 0.
float OceanWaterDepth(float2 grid)
{
    float2 shoreUV = (grid - _ShoreMapRect.xy) * _ShoreMapRect.zw;
    if (_ShoreMapRect.z <= 0 || any(shoreUV <= 0) || any(shoreUV >= 1)) return 10000;
    float depth = OceanShore(grid, shoreUV).g;
    return depth < -0.5 ? 0 : depth;
}

// Boiling surface of fast water: x = height in meters, yz = its gradient in world XZ. A current over the
// turbulence start speed gets boils of two sizes and rates. Calm water and materials without turbulence get
// none. Shallow water boils less (full height from BoilFullDepth), so the troughs do not show the ground.
float3 Turbulence(float2 grid, float2 flow, float turbulence, float time)
{
    float full = max(_TurbulenceFull, _TurbulenceStart + 0.01);
    float strength = max(_Turbulence * smoothstep(_TurbulenceStart, full, length(flow)),
        _RegimeBoils * turbulence);
    float3 boils = 0;
    [branch] if (strength > 0)
    {
        strength *= saturate(OceanWaterDepth(grid) / BoilFullDepth);
        boils = LoogaBoils(grid, time);
    }
    return boils * strength;
}

// Share of the wave height of a cascade at a water depth: 0 at the water edge, 1 at the full wave depth of
// the cascade. The full wave depth scales with the tile size, so short waves keep moving in shallow water.
float OceanDepthWeight(float depth, float tileSize)
{
    float fullDepth = _OceanShoreDepth * tileSize / max(_LoogaOceanCascades[0].w, 1);
    return saturate(depth / max(fullDepth, 0.01));
}

// Set by LoogaWaterFall: the water clock as whole 1024 s periods (x) and the seconds in the period (y), and 1 while
// it is set (z). _Time.y is a float: after hours of editor time the fall textures moved in uneven steps.
float4 _LoogaWaterClock;

// frac(time x rate) without the precision loss of a large time: whole periods x frac(1024 x rate) stays small.
float WaterClockPhase(float rate)
{
    float phase;
    [branch] if (_LoogaWaterClock.z > 0.5)
    {
        phase = frac(_LoogaWaterClock.x * frac(1024.0 * rate) + _LoogaWaterClock.y * rate);
    }
    else
    {
        phase = frac(_Time.y * rate);
    }
    return phase;
}

// A wave group that comes to the shore at a point: the height, the horizontal movement and the slope in
// world XZ, the surf foam behind breaking crests (0 to 1), the swash at the water edge (1 when a crest
// arrives, 0 in a trough, 0.5 without shore waves) and the share of the shore waves (0 to 1).
struct ShoreWave
{
    float height;
    float2 offset;
    float2 slope;
    float surf;
    float swash;
    float weight;
    // Rise of the water at the shore from the swash run-up, in meters.
    float runUp;
    // Movement of the water up the shore with the swash, in world XZ meters.
    float2 runUpShift;
    // Weight of the swash band at the water edge, 0 to 1.
    float edge;
    // Unit vector toward the shore in world XZ.
    float2 toShore;
};

// Adds one wave of the group. The phase grows with the arrival time and with the time, so each crest moves
// to smaller arrival times, which is toward the shore. Gerstner movement toward the crests sharpens them.
// The numerator and the denominator of the slope along the wave direction add over the waves.
void AddShoreWave(inout ShoreWave wave, float amplitude, float period, float arrival, float slowness,
    float2 toShore, float phaseShift, float broken, float time, inout float numerator,
    inout float denominator)
{
    // A wave group of five waves: the group height moves with the waves (in shallow water the group speed
    // is the wave speed), so a set of large waves comes in and then smaller ones.
    float omega0 = 2 * PI / period;
    float group = smoothstep(0.15, 0.85, 0.5 + 0.5 * cos((omega0 * (arrival + time) + phaseShift) / 5));
    amplitude *= lerp(0.45, 1, group);
    broken *= group;
    float omega = 2 * PI / period;
    float phase = omega * (arrival + time) + phaseShift;
    float k = omega * slowness;
    float steepness = min(_ShoreWaveSteepness, 0.85 / max(amplitude * k, 1e-4));
    float s, c;
    sincos(phase, s, c);
    wave.height += amplitude * c;
    wave.offset += toShore * (steepness * amplitude * s);
    numerator += amplitude * k * s;
    denominator -= steepness * amplitude * k * c;
    // Time since the last crest went past this point. The surf foam rises over a short time behind the crest
    // and then fades, so the foam has soft edges on both sides.
    float age = frac(phase / (2 * PI)) * period;
    float rise = smoothstep(0, 0.12 * period, age);
    wave.surf = max(wave.surf, broken * rise * exp(-age / max(_SurfFoamTime, 0.1)));
}

ShoreWave ShoreWaveAt(float2 grid, float time)
{
    ShoreWave wave = (ShoreWave)0;
    wave.swash = 0.5;
    float2 shoreUV = (grid - _ShoreMapRect.xy) * _ShoreMapRect.zw;
    if (_ShoreWaveHeight <= 0 || _ShoreMapRect.z <= 0 || any(shoreUV <= 0) || any(shoreUV >= 1)) return wave;
    // Cubic samples, so the crests and the water edge are smooth curves, not texel polygons.
    float4 field = LoogaSampleCubic(TEXTURE2D_ARGS(_ShoreWaveMap, sampler_ShoreWaveMap), shoreUV);
    float slowness = length(field.yz);
    float2 shore = OceanShore(grid, shoreUV);
    // The field also covers a dry margin next to the water, so the swash does not stop where the bilinear
    // filter of the coarse shore texture reads a dry texel (depth -1) under the real water.
    if (slowness < 1e-5) return wave;

    float2 toShore = field.yz / slowness;
    float depth = max(shore.g, 0);
    // 1 in the water, 0 where the shore texture is fully dry.
    float wet = saturate(shore.g + 1);
    // Full waves near the shore and none past the reach. Where the waves of two shores meet, the field
    // fade removes them.
    wave.weight = (1 - smoothstep(0.5, 1, shore.r / max(_ShoreWaveReach, 1))) * field.a;
    // Large slow patches change the wave height along the crests and bend the crests a little.
    // The two samples are turned against each other (0.8 and -1.3 radians), so their tiles do not line up.
    float2 turnedA = float2(0.697 * grid.x - 0.717 * grid.y, 0.717 * grid.x + 0.697 * grid.y);
    float2 turnedB = float2(0.267 * grid.x + 0.964 * grid.y, -0.964 * grid.x + 0.267 * grid.y);
    float variation = SAMPLE_TEXTURE2D_LOD(_FoamDetailMap, sampler_FoamDetailMap,
        turnedA / 260 + time * 0.002, 5).r;
    float phaseShift = LoogaShoreWavePhaseShift(grid);
    // Green's law: the wave grows as its speed falls in shallow water. A wave higher than 0.78 of the depth
    // breaks, so the amplitude stays at 0.39 of the depth and the surplus becomes surf.
    float deepSpeed = 9.81 * _ShoreWavePeriod / (2 * PI);
    float shoaled = 0.5 * _ShoreWaveHeight * lerp(0.6, 1.3, variation) *
        sqrt(clamp(deepSpeed * slowness, 1, 9));
    float limit = 0.39 * depth;
    float amplitude = min(shoaled, limit) * wave.weight;
    // Each crest breaks in segments along its length, not as one even line.
    float segments = smoothstep(0.25, 0.75, SAMPLE_TEXTURE2D_LOD(_FoamDetailMap, sampler_FoamDetailMap,
        turnedB / 55 + time * 0.01, 3).r);
    // The waves break most at the break line, where they first reach the limit. Closer to the shore the
    // broken waves carry less new foam.
    float ratio = shoaled / max(limit, 1e-3);
    float broken = smoothstep(1, 1.4, ratio) * (1 - 0.75 * smoothstep(1.6, 4, ratio)) * wave.weight *
        lerp(0.2, 1, segments) * wet;
    float numerator = 0;
    float denominator = 1;
    // The main wave and a small shorter one that only roughens the surface and makes no surf.
    AddShoreWave(wave, amplitude * 0.8, _ShoreWavePeriod, field.x, slowness, toShore, phaseShift, broken,
        time, numerator, denominator);
    AddShoreWave(wave, amplitude * 0.25, _ShoreWavePeriod * 0.73, field.x, slowness, toShore,
        phaseShift * 1.7 + 2.1, 0, time, numerator, denominator);
    wave.slope = toShore * (numerator / max(denominator, 0.15));
    // The swash keeps a part of its movement where the waves of two shores meet, so the shore foam there
    // does not stop. The direction of the field still changes smoothly there.
    float swashWeight = (1 - smoothstep(0.5, 1, shore.r / max(_ShoreWaveReach, 1))) *
        LoogaSwashFieldWeight(field.a);
    // Swash at the water edge (arrival time 0): after each crest the water runs up the beach over 30 % of
    // the period and drains back more slowly. The larger waves of a group run up farther.
    // The terrain wets the ground with the same timing (LoogaShoreSwash.hlsl).
    // The swash follows the arrival time at the water edge, so it starts when the crest gets there. With
    // swell the crests reach the parts of a beach at different times. The point the distance to the edge
    // toward the shore is on the edge or on its dry margin, where the field holds the edge time. Its
    // bilinear sample matches the terrain wetness, which reads the same field (LoogaShoreSwash.hlsl).
    float2 edgeUV = (grid + toShore * max(shore.r, 0) - _ShoreMapRect.xy) * _ShoreMapRect.zw;
    float edgeArrival = SAMPLE_TEXTURE2D_LOD(_ShoreWaveMap, sampler_ShoreWaveMap, edgeUV, 0).r;
    float edgePhase = LoogaSwashEdgePhase(grid, time + edgeArrival, _ShoreWavePeriod);
    float runUp = LoogaSwashRunUp(frac(edgePhase / (2 * PI)));
    // The water rises in a band from the dry margin to 25 m from the edge.
    float swashZone = LoogaSwashDryZone(shore.r) * (1 - smoothstep(4, 25, shore.r));
    wave.runUp = _SwashHeight * LoogaSwashWaveScale(grid, edgePhase) * runUp * swashWeight * swashZone;
    wave.edge = _SwashHeight > 0 ? swashZone : 0;
    wave.swash = lerp(0.5, runUp, swashWeight);
    // The water surface moves up the shore and back with the run-up, so its ripples and its foam move
    // together.
    wave.runUpShift = toShore * ((runUp - 0.5) * 4 * swashWeight * swashZone);
    wave.toShore = toShore;
    return wave;
}

// Pixel sizes on the water (metres) between which the plunge whitecaps widen and fade. A whitecap band is about 1.5 m
// wide.
static const float PlungeFoamNearPixel = 0.04;
static const float PlungeFoamFarPixel = 0.25;

// The plunge chop: wave trains that run out from the plunge at these angles to the outward direction (radians),
// with these shares of the period and these heights. Their sum has short crests that cross, rise and fade.
static const float4 PlungeChopAngles = float4(-0.62, -0.21, 0.27, 0.71);
static const float4 PlungeChopPeriods = float4(1.0, 0.78, 1.21, 0.64);
static const float4 PlungeChopHeights = float4(1.0, 0.8, 0.7, 0.55);
static const float4 PlungeChopPhases = float4(0.13, 0.57, 0.31, 0.89);

// The waves that run out from the plunge of a fall at a point: the slope in world XZ and the foam on the crests (0 to
// 1).
struct PlungeWave
{
    float2 slope;
    half foam;
};

// Below a plunge the water is a short-crested chop, not a train of long crests. Four wave trains run out from the
// plunge at different angles and lengths, in the coordinates of the plunge wave field: the time from the plunge
// lines (outward) and the distance along them (across). These coordinates follow the plunge, so the crests turn
// around its ends and the shore, and they are exact far from the world origin. Foam forms where the crests add up
// high, so it comes in irregular patches that move out with the waves and change their shape. footprint: the size of
// a pixel on the water, in metres. Where it gets near the width of a whitecap, the whitecaps widen and fade, so far
// away they do not draw thin, hard lines.
PlungeWave PlungeWaveAt(float2 grid, float footprint)
{
    PlungeWave wave = (PlungeWave)0;
    float2 shoreUV = (grid - _ShoreMapRect.xy) * _ShoreMapRect.zw;
    if (_PlungeWaveHeight <= 0 || _ShoreMapRect.z <= 0 || any(shoreUV <= 0) || any(shoreUV >= 1)) return wave;
    // Cubic samples, so the crests are smooth curves, not texel polygons.
    float4 field = LoogaSampleCubic(TEXTURE2D_ARGS(_PlungeWaveMap, sampler_ShoreMap), shoreUV);
    float slowness = length(field.yz);
    if (slowness < 1e-5 || field.a <= 0) return wave;

    float2 outward = -field.yz / slowness;
    float period = max(_PlungeWavePeriod, 0.5);
    // Distance from the plunge: the time at the deep water speed of the period.
    float travelled = field.x * 9.81 * period / (2 * PI);
    float reach = travelled / max(_PlungeWaveReach, 1);
    float fade = exp(-1.5 * reach) * (1 - smoothstep(0.6, 1, reach)) * field.a;
    if (fade <= 0.001) return wave;

    // The distance along the plunge lines and the direction in which it grows.
    float along = SAMPLE_TEXTURE2D_LOD(_PlungeAlongMap, sampler_ShoreMap, shoreUV, 0).r;
    float2 across = float2(-outward.y, outward.x);
    float alongNext = SAMPLE_TEXTURE2D_LOD(_PlungeAlongMap, sampler_ShoreMap,
        shoreUV + across * 2 * _ShoreMapRect.zw, 0).r;
    float2 alongDirection = alongNext >= along ? across : -across;
    // A broad noise bends the crests a little and drifts slowly. Groups of five waves rise and fall.
    float2 turned = float2(0.267 * grid.x + 0.964 * grid.y, -0.964 * grid.x + 0.267 * grid.y);
    float arrival = field.x + (LoogaFoamNoise(turned / 38 + _Time.y * 0.015).x - 0.5) * 0.5 * period;
    float group = smoothstep(0.15, 0.85, 0.5 + 0.5 * cos(2 * PI * (WaterClockPhase(0.2 / period) -
        arrival * 0.2 / period)));

    float height = 0;
    float total = 0;
    float2 slope = 0;
    float mainPhase = 0;
    [unroll] for (int index = 0; index < 4; index++)
    {
        float wavePeriod = period * PlungeChopPeriods[index];
        float waveLength = wavePeriod / slowness;
        float s, c;
        sincos(PlungeChopAngles[index], s, c);
        // A slow noise shifts the phase of each train by up to about 1.5 radians. It breaks the crests also past the
        // ends of the plunge lines, where the along distance does not change and the trains would line up in rings.
        float wobble = 3 * (LoogaFoamNoise(grid / 23 + PlungeChopPhases[index] * 71).x - 0.5);
        float phase = 2 * PI * (WaterClockPhase(1 / wavePeriod) - c * arrival / wavePeriod - s * along / waveLength +
            PlungeChopPhases[index]) + wobble;
        float sinePhase, cosinePhase;
        sincos(phase, sinePhase, cosinePhase);
        if (index == 0) mainPhase = phase;
        float weight = PlungeChopHeights[index];
        height += weight * cosinePhase;
        total += weight;
        slope += (weight * 2 * PI / waveLength * sinePhase) * (c * outward + s * alongDirection);
    }
    height /= total;
    wave.slope = slope * (_PlungeWaveHeight * fade * lerp(0.5, 1, group) / total);
    // Whitecaps: foam in a band just behind the crests of the main wave train, where the sum of all trains is high.
    // The sum cuts the band into short pieces along the crests that grow, move and fade, so the foam comes in
    // short streaks along the crests, not in round spots at the highest points. Near the plunge lower crests break.
    float threshold = lerp(0.1, 0.4, saturate(reach * 2.5));
    half far = smoothstep(PlungeFoamNearPixel, PlungeFoamFarPixel, footprint);
    half band = smoothstep(lerp(0.55, 0.15, far), 0.95, 0.5 + 0.5 * cos(mainPhase - 0.5));
    half crests = band * smoothstep(threshold, threshold + lerp(0.4, 0.8, far), height) * lerp(1, 0.12, far);
    // Past the ends of the plunge lines the along distance does not change, and the trains run out in rings. The
    // whitecaps stay where the along distance grows (about 1 m per metre across).
    crests *= smoothstep(0.2, 0.7, abs(alongNext - along) * 0.5);
    half patches = LoogaFoamNoise(grid / 7 + 5.3).y;
    float foam = _PlungeFoam * fade * crests * lerp(0.5, 1, group) * lerp(0.4, 1, patches);
    // Soft limit: dense foam stays below full white, so the patches keep soft edges.
    wave.foam = 1 - exp(-2 * foam);
    return wave;
}

// Ocean displacement at a vertex. Each cascade reads the mip level whose texels are about one vertex
// spacing wide, so the mesh moves only with the waves that its vertices resolve. A wider texel blurs the
// crests and makes them round. Each cascade fades in shallow water with its own depth weight.
float3 OceanDisplacement(float2 grid, float spacing, float depth)
{
    float3 displacement = 0;
    [unroll] for (int cascade = 0; cascade < 4; cascade++)
    {
        float4 info = _LoogaOceanCascades[cascade];
        [branch] if (cascade < _LoogaOceanParams.x && info.y > 0)
        {
            float mip = max(0, log2(spacing * _LoogaOceanParams.y * info.x));
            displacement += SAMPLE_TEXTURE2D_ARRAY_LOD(_LoogaOceanDisplacement,
                sampler_LoogaOceanDisplacement, grid * info.x, cascade, mip).xyz *
                (info.y * OceanDepthWeight(depth, info.w));
        }
    }
    return displacement;
}

// Outside the shore map, the coverage mask of the body tells where it has water. A body without a mask
// takes all ground outside the shore map for open water.
bool OceanCoverage(float2 grid)
{
    if (_OceanCoverageRect.z <= 0) return true;
    float2 uv = (grid - _OceanCoverageRect.xy) * _OceanCoverageRect.zw;
    if (any(uv <= 0) || any(uv >= 1)) return _OceanCoverageOpen > 0.5;
    return SAMPLE_TEXTURE2D_LOD(_OceanCoverageMap, sampler_OceanCoverageMap, uv, 0).r > 0.5;
}

// True when the opaque ground behind the pixel is near or above the water level. False over sky.
bool GroundNearWater(float4 positionCS, float waterHeight)
{
    float2 screenUV = GetNormalizedScreenSpaceUV(positionCS);
    float rawDepth = WaterSceneDepth(screenUV);
    #if UNITY_REVERSED_Z
    if (rawDepth <= 0) return false;
    #else
    if (rawDepth >= 1) return false;
    #endif
    float groundHeight = ComputeWorldSpacePosition(screenUV, rawDepth, UNITY_MATRIX_I_VP).y;
    return waterHeight - groundHeight < GridDryDrop;
}

// The near grid draws inside its radius and only over the water of the surface mesh (shore map depth -1 =
// outside the mesh). The surface mesh draws outside the radius.
void OceanGridClip(float2 grid, float4 positionCS, float waterHeight, float2 weld)
{
    if (_LoogaOceanGrid.w <= 0) return;
    bool inside = distance(grid, _LoogaOceanGrid.xy) < _LoogaOceanGrid.z;
    if (_OceanGridMesh > 0.5)
    {
        float2 shoreUV = (grid - _ShoreMapRect.xy) * _ShoreMapRect.zw;
        bool covered = _ShoreMapRect.z > 0 && all(shoreUV > 0) && all(shoreUV < 1);
        // The grid also draws over the dry ground next to the water: the ground hides it where it is above
        // the water, so the water edge is where the ground meets the surface and not the outline of the
        // coarse shore texture. It does not draw over open water past the outline of the body, and not
        // past a crest or a cliff edge, where the ground falls away below the water.
        half2 shore = covered ? SAMPLE_TEXTURE2D_LOD(_ShoreMap, sampler_ShoreMap, shoreUV, 0).rg : 0;
        bool dryMargin = shore.g <= -0.5 && shore.r < 0 && shore.r > -GridDryMargin;
        bool water = covered
            ? shore.g > -0.5 || dryMargin && GroundNearWater(positionCS, waterHeight)
            : OceanCoverage(grid);
        // At a weld the grid ends exactly on the weld line, where the fall sheet starts.
        if (weld.y > 0.5 && weld.x > -WeldGridReach) water = weld.x <= 0;
        clip(inside && water ? 1 : -1);
    }
    else
    {
        clip(inside ? -1 : 1);
    }
}

Varyings BodyVertex(Attributes input)
{
    Varyings output = (Varyings)0;
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_TRANSFER_INSTANCE_ID(input, output);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
    float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
    float2 grid = positionWS.xz;
    // The near grid has no flow of its own. It reads the flow of the surface from the shore map.
    [branch] if (_OceanGridMesh > 0.5)
    {
        float2 shoreUV = (grid - _ShoreMapRect.xy) * _ShoreMapRect.zw;
        bool covered = _ShoreMapRect.z > 0 && all(shoreUV > 0) && all(shoreUV < 1);
        input.flow.xy = covered ? SAMPLE_TEXTURE2D_LOD(_ShoreMap, sampler_ShoreMap, shoreUV, 0).ba : 0;
    }
    // The mesh moves only with the waves that its vertices resolve. The fragment adds all waves that the
    // pixels resolve to the normal.
    float3 offset = 0;
    float3 tangent = float3(1, 0, 0);
    float3 binormal = float3(0, 0, 1);
    float crest = 0;
    float time = _Time.y;
    float spacing = max(input.flow.w, 0.1);
    float calm = FlowWaveWeight(input.flow.xy, input.regime.x);
    // Height of the shore waves and the run-up at the vertex, with the edge lift. The fragment compares it
    // with the same height at the pixel.
    float vertexShoreHeight = 0;
    bool ocean = false;
    #if defined(_OCEAN_WAVES)
    ocean = _LoogaOceanParams.x > 0;
    [branch] if (ocean)
    {
        offset = OceanDisplacement(grid, spacing, OceanWaterDepth(grid)) * (calm * _OceanHeight);
        ShoreWave shoreWave = ShoreWaveAt(grid, _LoogaOceanTime);
        offset += float3(shoreWave.offset.x, shoreWave.height, shoreWave.offset.y) * calm;
        // The run-up fades out before the edge of the near grid, so the surface mesh outside it needs none.
        // It does not calm in a current: the terrain wets the ground for the same run-up.
        float gridFade = LoogaSwashGridFade(grid);
        offset += float3(shoreWave.runUpShift.x, shoreWave.runUp, shoreWave.runUpShift.y) * gridFade;
        // On a flat beach the water edge is where two almost parallel surfaces meet, so it shows the
        // triangles of the mesh. The mesh rises a little at the edge, and the fragment cuts the edge at the
        // height of the waves at the pixel, which gives a smooth curve.
        float edgeLift = 0.06 * shoreWave.edge * gridFade;
        offset.y += edgeLift;
        vertexShoreHeight = shoreWave.height * calm + shoreWave.runUp * gridFade + edgeLift;
    }
    #endif
    [branch] if (!ocean)
    {
        LoogaGerstnerWave(_WaveA, grid, time, _WaveScale, LoogaWaveMeshWeight(_WaveA.w, spacing) * calm,
            offset, tangent, binormal, crest);
        LoogaGerstnerWave(_WaveB, grid, time, _WaveScale, LoogaWaveMeshWeight(_WaveB.w, spacing) * calm,
            offset, tangent, binormal, crest);
        LoogaGerstnerWave(_WaveC, grid, time, _WaveScale, LoogaWaveMeshWeight(_WaveC.w, spacing) * calm,
            offset, tangent, binormal, crest);
        LoogaGerstnerWave(_WaveD, grid, time, _WaveScale, LoogaWaveMeshWeight(_WaveD.w, spacing) * calm,
            offset, tangent, binormal, crest);
    }
    // Boils of fast water. Only a fine mesh follows them; the fragment adds their light everywhere.
    offset.y += Turbulence(grid, input.flow.xy, input.regime.x, time).x *
        (1 - smoothstep(BoilMeshSpacing, BoilMeshSpacingEnd, spacing));
    // Toward a weld line the surface calms, so it meets the fall sheet at the same points.
    float weldCalm = WeldDisplacementCalm(WeldLipDistance(WaterWeld(grid)));
    offset *= weldCalm;
    vertexShoreHeight *= weldCalm;
    positionWS += offset;

    output.positionWS = positionWS;
    output.normalWS = half3(0, 1, 0);
    output.positionCS = TransformWorldToHClip(positionWS);
    output.surface = float4(grid, vertexShoreHeight, ComputeFogFactor(output.positionCS.z));
    output.flow = float4(input.flow.xyz, positionWS.y - offset.y);
    output.regime = input.regime.xy;
    return output;
}

// Slopes (dx / dy, dz / dy) of a tangent space normal sample that projects from above.
float2 DetailSlope(float2 uv, float strength)
{
    half3 normal = UnpackNormal(SAMPLE_TEXTURE2D(_NormalMap, sampler_NormalMap, uv));
    return normal.xy / max(normal.z, 0.2) * strength;
}

// Two texture offsets along the flow, half a cycle apart, and their blend weights. An offset restarts at
// the end of its cycle, while its weight is 0. The phase varies over the surface, so no pulse is visible.
struct FlowPhases
{
    float2 offset0;
    float2 offset1;
    float weight0;
    float weight1;
};

FlowPhases GetFlowPhases(float2 flow, float2 grid, float time, float cycle)
{
    // Smooth noise: the offset changes by the flow times the cycle for each unit of phase, so a grainy
    // phase (the foam mask texture) tore the textures into streaks along a fast flow.
    float jitter = LoogaFoamNoise(grid / FlowJitterSize).x;
    float phase0 = frac(time / cycle + jitter);
    float phase1 = frac(phase0 + 0.5);
    FlowPhases phases;
    phases.offset0 = flow * ((phase0 - 0.5) * cycle);
    phases.offset1 = flow * ((phase1 - 0.5) * cycle);
    phases.weight0 = 1 - abs(1 - 2 * phase0);
    phases.weight1 = 1 - phases.weight0;
    return phases;
}

// Two flow phase pairs for textures of a size: the cycle halves for each step of speed, so the texture moves
// at most about half a tile per cycle and never fades in place. The two pairs are the cycle lengths around
// the needed one, and the blend moves between them smoothly over the surface.
struct FastPhases
{
    FlowPhases slow;
    FlowPhases fast;
    float blend;
};

FastPhases GetFastPhases(float2 flow, float2 grid, float time, float cycle, float size)
{
    float level = clamp(log2(max(length(flow) * cycle / (0.5 * size), 1)), 0, 5);
    float slowCycle = cycle * exp2(-floor(level));
    FastPhases phases;
    phases.slow = GetFlowPhases(flow, grid, time, slowCycle);
    phases.fast = GetFlowPhases(flow, grid, time, slowCycle * 0.5);
    phases.blend = frac(level);
    return phases;
}

float2 Rotate(float2 value, float angle)
{
    float s, c;
    sincos(angle, s, c);
    return float2(c * value.x - s * value.y, s * value.x + c * value.y);
}

// Each layer turns its texture by its own angle, so the tile grids of the layers never line up. The slope
// turns back into world XZ.
float2 FlowDetailSlope(float2 grid, FlowPhases phases, float2 drift, float size, float strength,
    float angle)
{
    float2 slope = DetailSlope(Rotate(grid - phases.offset0 + drift, angle) / size, strength) *
        phases.weight0;
    slope += DetailSlope(Rotate(grid - phases.offset1 + drift, angle) / size, strength) * phases.weight1;
    return Rotate(slope, -angle);
}

float2 FastDetailSlope(float2 grid, FastPhases phases, float2 drift, float size, float strength, float angle)
{
    float2 slope = FlowDetailSlope(grid, phases.slow, drift, size, strength, angle);
    [branch] if (phases.blend > 0.01)
    {
        slope = lerp(slope, FlowDetailSlope(grid, phases.fast, drift, size, strength, angle), phases.blend);
    }
    return slope;
}

// Smooth large-scale offsets, -1 to 1, from two turned samples of the foam mask.
float2 WaveWarp(float2 grid)
{
    float2 uv = grid / max(_WaveWarpSize, 1);
    float a = SAMPLE_TEXTURE2D(_FoamMap, sampler_FoamMap, Rotate(uv, 0.7)).r;
    float b = SAMPLE_TEXTURE2D(_FoamMap, sampler_FoamMap, Rotate(uv, -1.9) * 0.71 + 0.33).r;
    return float2(a, b) * 2 - 1;
}

void AddWarpedWave(float4 wave, float2 grid, float2 warp, float angle, float time, float pixelSize,
    float calm, inout float3 offset, inout float3 tangent, inout float3 binormal, inout float crest)
{
    float2 shifted = grid + Rotate(warp, angle) * (_WaveWarp * wave.w);
    LoogaGerstnerWave(wave, shifted, time, _WaveScale, LoogaWavePixelWeight(wave.w, pixelSize) * calm, offset,
        tangent, binormal, crest);
}

// Wave normal and crest height (-1 to 1) at a pixel. Waves shorter than a few pixels fade out, and a
// large-scale warp shifts each wave differently, so far water shows no regular pattern. The strength
// scales the normal tilt.
half3 WaveNormal(float2 grid, float time, float strength, float calm, out float crest)
{
    float pixelSize = max(length(ddx(grid)), length(ddy(grid)));
    float2 warp = WaveWarp(grid);
    float3 offset = 0;
    float3 tangent = float3(1, 0, 0);
    float3 binormal = float3(0, 0, 1);
    crest = 0;
    AddWarpedWave(_WaveA, grid, warp, 0, time, pixelSize, calm, offset, tangent, binormal, crest);
    AddWarpedWave(_WaveB, grid, warp, 1.7, time, pixelSize, calm, offset, tangent, binormal, crest);
    AddWarpedWave(_WaveC, grid, warp, 3.1, time, pixelSize, calm, offset, tangent, binormal, crest);
    AddWarpedWave(_WaveD, grid, warp, 4.6, time, pixelSize, calm, offset, tangent, binormal, crest);
    float totalSteepness = (_WaveA.z + _WaveB.z + _WaveC.z + _WaveD.z) * _WaveScale;
    crest /= max(totalSteepness, 1e-4);
    half3 normal = normalize(cross(binormal, tangent));
    return normalize(half3(normal.x * strength, normal.y, normal.z * strength));
}

// Ocean wave normal, crest height (-1 to 1) and summed cascade foam at a pixel. The slopes of the cascades
// add, each with its normal scale. The mip levels filter the waves that a pixel cannot show. The weight
// scales the wave height. The height and the foam of each cascade fade in shallow water with its depth
// weight, like the vertices. The slopes fade only at the water edge, so shallow water keeps its ripples.
// The gradients come from the grid once, so the samples in the cascade branch need no derivatives of their
// own.
half3 OceanNormal(float2 grid, float strength, float weight, float depth, out float crest, out float foam)
{
    float2 slope = 0;
    float height = 0;
    foam = 0;
    float2 gridDx = ddx(grid);
    float2 gridDy = ddy(grid);
    [unroll] for (int cascade = 0; cascade < 4; cascade++)
    {
        float4 info = _LoogaOceanCascades[cascade];
        [branch] if (cascade < _LoogaOceanParams.x)
        {
            float2 uv = grid * info.x;
            float2 uvDx = gridDx * info.x;
            float2 uvDy = gridDy * info.x;
            float depthWeight = OceanDepthWeight(depth, info.w);
            slope += SAMPLE_TEXTURE2D_ARRAY_GRAD(_LoogaOceanDerivatives, sampler_LoogaOceanDerivatives, uv,
                cascade, uvDx, uvDy).xy * info.z;
            height += SAMPLE_TEXTURE2D_ARRAY_GRAD(_LoogaOceanDisplacement, sampler_LoogaOceanDisplacement,
                uv, cascade, uvDx, uvDy).y * (info.y * depthWeight);
            foam += SAMPLE_TEXTURE2D_ARRAY_GRAD(_LoogaOceanFoam, sampler_LoogaOceanFoam, uv, cascade, uvDx,
                uvDy).r * depthWeight;
        }
    }
    crest = clamp(height * weight / max(_OceanCrestHeight, 0.01), -1, 1);
    foam *= saturate(weight);
    slope *= weight * strength * saturate(depth / max(_OceanRippleDepth, 0.01));
    return normalize(half3(-slope.x, 1, -slope.y));
}

// 0 = calm patch, 1 = rough patch. Two turned octaves of the foam mask on a large scale.
float WindSlickAt(float2 grid)
{
    float2 uv = Rotate(grid, 0.31) / max(_SlickSize, 1);
    float mask = 0.6 * SAMPLE_TEXTURE2D(_FoamMap, sampler_FoamMap, uv).r;
    mask += 0.4 * SAMPLE_TEXTURE2D(_FoamMap, sampler_FoamMap, Rotate(uv, -0.83) * 2.3 + 0.41).r;
    return smoothstep(0.3, 0.7, mask);
}

// The slicks drift with the wind on still water and with the flow on moving water.
float WindSlick(float2 grid, float2 flow, float fast, float time)
{
    float2 wind = float2(0.7, 0.4) * time * (1 - fast);
    FlowPhases phases = GetFlowPhases(flow, grid, time, max(_FlowCycle, 0.1) * 6);
    return WindSlickAt(grid + wind - phases.offset0) * phases.weight0 +
        WindSlickAt(grid + wind - phases.offset1) * phases.weight1;
}

half4 FoamDetail(float2 grid)
{
    return SAMPLE_TEXTURE2D(_FoamDetailMap, sampler_FoamDetailMap, Rotate(grid, 0.37) / _FoamDetailSize);
}

// One sample of the foam detail for a vertex of the hex grid: the texture turned by a random angle and
// shifted by a random offset around the vertex center. The coordinates stay small near the center, and the
// gradients come from the full coordinates, so the mip level is right.
half4 HexFoamSample(float2 uv, float2 uvDx, float2 uvDy, float2 vertex)
{
    float2 random = LoogaFoamHash(vertex + 311.0);
    float s, c;
    sincos(random.x * 6.2831853, s, c);
    float2x2 turn = float2x2(c, -s, s, c);
    // The center of the vertex in texture coordinates (inverse of the skew in HexFoamDetail).
    float2 center = float2(vertex.x + vertex.y * 0.5, vertex.y * 0.8660254) / (3.4641016 * HexFoamCells);
    return SAMPLE_TEXTURE2D_GRAD(_FoamDetailMap, sampler_FoamDetailMap, mul(turn, uv - center) + random,
        mul(turn, uvDx), mul(turn, uvDy));
}

// Foam detail without a visible repeat (hex tiling, Mikkelsen 2022). Each vertex of a triangle grid shows
// the texture with its own turn and offset, and the three vertices around a point blend with sharpened
// weights, so most of the surface shows one sample with clear bubbles. The result has the contrast of one
// sample. A tiling texture at this small size showed the same clump shapes in rows.
half4 HexFoamDetail(float2 uv)
{
    float2 uvDx = ddx(uv);
    float2 uvDy = ddy(uv);
    float2 skewed = float2(uv.x - uv.y * 0.57735027, uv.y * 1.15470054) * (3.4641016 * HexFoamCells);
    float2 base = floor(skewed);
    float3 local = float3(skewed - base, 0);
    local.z = 1 - local.x - local.y;
    float upper = step(0, -local.z);
    float flip = 2 * upper - 1;
    float3 weights = float3(-local.z * flip, upper - local.y * flip, upper - local.x * flip);
    weights = pow(max(weights, 0), HexFoamSharpness);
    weights /= weights.x + weights.y + weights.z;
    half4 detail = (HexFoamSample(uv, uvDx, uvDy, base + upper) - 0.5) * weights.x;
    detail += (HexFoamSample(uv, uvDx, uvDy, base + float2(upper, 1 - upper)) - 0.5) * weights.y;
    detail += (HexFoamSample(uv, uvDx, uvDy, base + float2(1 - upper, upper)) - 0.5) * weights.z;
    return detail * rsqrt(dot(weights, weights)) + 0.5;
}

// One flow phase of the current foam detail, moved by an offset along the flow. The hex grid moves with
// the texture, so the shapes do not change while they move.
half4 CurrentFoamPhase(float2 grid, float2 offset)
{
    return HexFoamDetail(Rotate(grid - offset, 0.37) / (_FoamDetailSize * CurrentFoamScale));
}

// Foam detail in a current. All its samples use one flow cycle of fixed length. A cycle that became
// shorter in fast water (to move the texture at most half a tile) restarted the detail up to 16 times per
// second, and the foam jittered back and forth. The clumps (R) are mostly flat, so the foam shapes come
// from the foam simulation, which carries them with the current. The bubbles (G) keep their fine detail.
half4 CurrentFoamDetail(float2 grid, FlowPhases phases)
{
    half4 detail = (CurrentFoamPhase(grid, phases.offset0) - 0.5) * phases.weight0 +
        (CurrentFoamPhase(grid, phases.offset1) - 0.5) * phases.weight1;
    // The blend of two phases has less contrast. Raise it again, so the contrast does not pulse.
    detail = saturate(detail * (CurrentFoamContrast *
        rsqrt(phases.weight0 * phases.weight0 + phases.weight1 * phases.weight1)) + 0.5);
    detail.r = lerp(0.5, detail.r, CurrentFoamClumps);
    return detail;
}

// Foam coverage from 0 to 1: the shore band, the contact line, open water patches, the wave crests and
// fast water. The shore band uses the distance to the water edge from the shore map of the water body.
// Where the outline of the body cuts through water (shore map depth -1 outside the mesh), there is no foam,
// so the straight outline does not show. In the foam simulation window of the body, the simulated foam
// replaces the shore band and the fast water foam. The surge (0 to 1, 0.5 = calm) is the wave height at the
// pixel: a crest that reaches an object or the shore widens the shore band and thickens the contact line,
// and a trough thins them.
// shorePoint: world XZ where the shore band is measured. On the ocean it is the moved position of the pixel, so
// the band stays at the real water edge when the surface slides up and down the shore with the swash.
// Simulated foam blurred over a radius in metres: the point and two rings of four samples.
half BlurredFoamSim(float2 uv, float radius)
{
    half foam = SAMPLE_TEXTURE2D_LOD(_FoamSim, sampler_FoamSim, uv, 0).r;
    [branch] if (radius > 0.25)
    {
        float2 inner = radius * 0.5 * _FoamSimRect.zw;
        float2 outer = radius * 0.7071 * _FoamSimRect.zw;
        half ring = SAMPLE_TEXTURE2D_LOD(_FoamSim, sampler_FoamSim, uv + float2(inner.x, 0), 0).r +
            SAMPLE_TEXTURE2D_LOD(_FoamSim, sampler_FoamSim, uv - float2(inner.x, 0), 0).r +
            SAMPLE_TEXTURE2D_LOD(_FoamSim, sampler_FoamSim, uv + float2(0, inner.y), 0).r +
            SAMPLE_TEXTURE2D_LOD(_FoamSim, sampler_FoamSim, uv - float2(0, inner.y), 0).r +
            SAMPLE_TEXTURE2D_LOD(_FoamSim, sampler_FoamSim, uv + outer, 0).r +
            SAMPLE_TEXTURE2D_LOD(_FoamSim, sampler_FoamSim, uv - outer, 0).r +
            SAMPLE_TEXTURE2D_LOD(_FoamSim, sampler_FoamSim, uv + float2(outer.x, -outer.y), 0).r +
            SAMPLE_TEXTURE2D_LOD(_FoamSim, sampler_FoamSim, uv - float2(outer.x, -outer.y), 0).r;
        foam = foam / 9 + ring / 9;
    }
    return foam;
}

// simBlur: radius in metres over which the simulated foam blurs. Far away a plume edge was only a few pixels
// wide and looked cut out; the blur grows with the view distance, so the foam fades out softly.
half FoamCoverage(float2 grid, float2 shorePoint, float2 flow, float time, float contactDepth,
    float waterDepth, float crestHeight, float fast, half whiteWater, half surge, bool oceanWaves,
    float simBlur)
{
    half surgeScale = lerp(0.4, 1.6, surge);
    // Large patches along the shore and on open water, so the band width is not the same everywhere. A
    // low mip of the clump channel is smooth, and its values spread evenly around 0.5.
    half patches = LoogaFoamPatches(SAMPLE_TEXTURE2D_LOD(_FoamDetailMap, sampler_FoamDetailMap,
        LoogaFoamPatchUV(grid, _FoamDetailSize), 3).r);
    half shore;
    half outline = 1;
    float2 shoreUV = (shorePoint - _ShoreMapRect.xy) * _ShoreMapRect.zw;
    // A body that runs far past its shores has the texture only in its detail area.
    [branch] if (_ShoreMapRect.z > 0 && all(shoreUV > 0) && all(shoreUV < 1))
    {
        // On the ocean the shore band moves with the swash, so its edge must be a smooth curve.
        half2 shoreData = oceanWaves ?
            OceanShore(shorePoint, shoreUV) :
            SAMPLE_TEXTURE2D_LOD(_ShoreMap, sampler_ShoreMap, shoreUV, 0).rg;
        half shoreDistance = shoreData.r;
        // Outside the mesh (depth -1) the foam stops only where the ground is still under water, which
        // is an outline through open water. Over dry ground the foam goes on to the water edge, so a coarse
        // shore texture does not cut it in texel steps.
        outline = lerp(1, saturate(shoreData.g * 4 + 2), saturate(shoreDistance * 0.5));
        shore = LoogaShoreBand(shoreDistance, patches, time, _ShoreFoamWidth * surgeScale,
            _ShoreFoamPulse);
    }
    else
    {
        shore = 1 - saturate(waterDepth / max(_ShoreFoamDepth, 0.01));
    }
    // On the ocean the contact foam stays at its full width when the swash drains back, so the receding
    // water edge keeps a foam rim like the edge that runs up. Only the run-up widens it.
    half contactScale = oceanWaves ? lerp(1, 1.6, surge) : surgeScale;
    half contact = 1 - saturate(contactDepth / max(_ContactFoamDepth * contactScale, 0.01));
    half crest = saturate((crestHeight - _CrestFoamStart) / max(1 - _CrestFoamStart, 0.01)) * _CrestFoam;
    half open = _SurfaceFoam * smoothstep(0.35, 0.85, patches);
    half moving = max(shore * _ShoreFoam, fast * _FastFoam);
    [branch] if (_FoamSimRect.z > 0)
    {
        // The simulation steps 20 times per second. Its foam moves on with the flow since the last step, so
        // fast foam does not move in jumps.
        float2 simUV = (grid - flow * _FoamSimAge - _FoamSimRect.xy) * _FoamSimRect.zw;
        // The simulation fades out over the last metres inside its window, so its edge does not show.
        // A side at the body edge does not fade (_FoamSimFade 0), so the foam goes on to a joined body.
        float2 fadeWidth = _FoamSimRect.zw * FoamSimEdgeFade;
        float2 low = lerp(1, simUV / fadeWidth, _FoamSimFade.xy);
        float2 high = lerp(1, (1 - simUV) / fadeWidth, _FoamSimFade.zw);
        float2 edge = saturate(min(low, high));
        half simulated = BlurredFoamSim(simUV, simBlur);
        // Slow open water far from a shore, such as a sea drift, has no trails to show. There the
        // simulated spots only made a patch around the camera, so the shader foam stays.
        half active = saturate(max(shore * 4, smoothstep(FoamSimFastStart, FoamSimFastFull, fast)));
        // With ocean waves the simulation carries the foam of fast water and of falling water with the
        // current, so in its window it replaces the fast water foam of the shader. The shore band of the
        // swash stays.
        moving = oceanWaves ? max(shore * _ShoreFoam, lerp(fast * _FastFoam, simulated, edge.x * edge.y)) :
            lerp(moving, simulated, edge.x * edge.y * active);
    }
    // White water of rapids and cascades stays also where the simulation replaces the shader foam. The
    // large patches break it up, so a cascade shows white streaks with darker water between them.
    moving = max(moving, whiteWater * lerp(0.45, 1.2, patches));
    half coverage = max(max(moving, contact * _ContactFoam), max(crest, open));
    return saturate(coverage) * outline;
}

// Foam amount from the foam detail texture for a coverage from 0 to 1. All foam uses it: whitecaps, the
// shore band, the contact line and fast water. The clumps (R) and the bubbles (G) use the coverage as their
// threshold, so the foam breaks into clumps and loose bubbles at its edges and is solid only where it is
// dense. Clumps grow faster than the coverage (between the coverage and its square), so thin foam is mostly
// bubbles and small clumps, not large blotches. The edges are soft, thin foam is partly clear, and the
// bubbles vary the brightness.
half FoamFromDetail(half4 detail, half coverage)
{
    half softness = max(_FoamSoftness, 0.05) * 2;
    half clumps = saturate((detail.r - 1 + coverage * (0.6 + 0.55 * coverage)) / softness);
    half bubbles = saturate((detail.g - 1 + coverage) / softness);
    return max(clumps, bubbles * 0.9) * saturate(coverage * 5) * lerp(0.55, 1, coverage) *
        lerp(0.8, 1, detail.g);
}

// GGX sun highlight in the URP light convention (the light color is the radiance times pi).
half3 SunSpecular(half3 normal, half3 view, Light light, float perceptualRoughness, half f0)
{
    half3 halfDir = SafeNormalize(light.direction + view);
    float nh = saturate(dot(normal, halfDir));
    float nl = saturate(dot(normal, light.direction));
    float nv = max(dot(normal, view), 1e-4);
    float vh = saturate(dot(view, halfDir));
    float roughness = max(perceptualRoughness * perceptualRoughness, 0.002);
    float a2 = roughness * roughness;
    float d = nh * nh * (a2 - 1) + 1;
    float distribution = a2 / (PI * d * d);
    float visibility = 0.5 / (nl * sqrt(nv * nv * (1 - a2) + a2) + nv * sqrt(nl * nl * (1 - a2) + a2) + 1e-5);
    float fresnel = f0 + (1 - f0) * pow(1 - vh, 5);
    float specular = min(distribution * visibility * fresnel * PI * nl, 64);
    return specular * light.color * (light.shadowAttenuation * light.distanceAttenuation);
}

// Signed device depth of the closest surface in a cell of a trace level.
float HiZDepth(int2 cell, int level)
{
    int2 levelSize = max(int2(_LoogaWaterHiZSize.xy) >> level, 1);
    cell = clamp(cell, 0, levelSize - 1);
    float depth;
    [branch] if (level == 0)
    {
        depth = WaterLoadSceneDepth(cell);
    }
    else
    {
        depth = LOAD_TEXTURE2D_LOD(_LoogaWaterHiZ, cell, level - 1).r;
    }
    return depth * DepthSign;
}

// Traces a reflection ray in screen space through the closest-depth pyramid (hierarchical Z). The ray
// climbs to coarser levels over empty cells and goes down to finer levels near a surface. Returns the
// screen UV of the hit in xy and the hit confidence in z. The confidence is 0 where the ray leaves the
// screen, passes behind an object or ends without a hit.
float3 TraceScreenReflection(float3 positionWS, float3 directionWS)
{
    float rayLength = _LoogaWaterReflection.w;
    float3 startVS = TransformWorldToView(positionWS);
    float3 directionVS = TransformWorldToViewDir(directionWS);
    // A ray toward the camera ends in front of the near plane, so its end projects correctly.
    if (directionVS.z > 0)
    {
        rayLength = min(rayLength, (-_ProjectionParams.y * 1.01 - startVS.z) / directionVS.z);
    }
    if (rayLength <= 0) return 0;

    float2 size = _LoogaWaterHiZSize.xy;
    float3 start = ComputeNormalizedDeviceCoordinatesWithZ(positionWS, UNITY_MATRIX_VP);
    float3 end = ComputeNormalizedDeviceCoordinatesWithZ(positionWS + directionWS * rayLength,
        UNITY_MATRIX_VP);
    // Pixel position and signed depth. Both change linearly along the ray on the screen.
    float3 origin = float3(start.xy * size, start.z * DepthSign);
    float3 ray = float3(end.xy * size, end.z * DepthSign) - origin;
    float2 direction = ray.xy >= 0 ? 1 : -1;
    float2 inverseRay = 1 / (direction * max(abs(ray.xy), 1e-4));

    // The ray stops at the screen edge and at the far plane.
    float2 screenExit = ((ray.xy >= 0 ? size - 0.5 : 0.5) - origin.xy) * inverseRay;
    float endT = min(1, min(screenExit.x, screenExit.y));
    if (ray.z > 0)
    {
        float farDepth = DepthSign > 0 ? 1 : 0;
        endT = min(endT, (farDepth - origin.z) / ray.z * 0.999);
    }
    float pixelT = 1 / max(max(abs(ray.x), abs(ray.y)), 1e-4);
    if (endT < pixelT) return 0;

    int maxLevel = (int)_LoogaWaterHiZSize.z - 1;
    int maxSteps = (int)_LoogaWaterReflection.x;
    float2 crossStep = ray.xy >= 0 ? 1 : 0;
    // The ray moves this many pixels past a cell boundary, so the next cell lookup is in the new cell.
    float2 crossOffset = direction * 0.005;
    float t = 0.5 * pixelT;
    int level = 0;
    float confidence = 0;
    [loop] for (int step = 0; step < maxSteps; step++)
    {
        float3 position = origin + ray * t;
        float cellSize = exp2(level);
        float2 cell = floor(position.xy / cellSize);
        float2 boundaryT = ((cell + crossStep) * cellSize + crossOffset - origin.xy) * inverseRay;
        float cellT = min(boundaryT.x, boundaryT.y);
        float sceneDepth = HiZDepth(int2(cell), level);
        if (position.z < sceneDepth)
        {
            // The ray is in front of every surface in the cell. It reaches the closest surface in the
            // cell or goes on to the next cell.
            float surfaceT = ray.z > 0 ? (sceneDepth - origin.z) / ray.z : cellT + 1;
            if (surfaceT < cellT)
            {
                t = surfaceT;
                if (level == 0)
                {
                    confidence = 1;
                    break;
                }
                level--;
            }
            else
            {
                t = cellT;
                level = min(level + 1, maxLevel);
            }
        }
        else if (level > 0)
        {
            level--;
        }
        else
        {
            // The ray is behind the surface in this pixel. A thin gap counts as a hit. Otherwise the ray
            // passes behind the object.
            float sceneEye = LinearEyeDepth(sceneDepth * DepthSign, _ZBufferParams);
            float behind = LinearEyeDepth(position.z * DepthSign, _ZBufferParams) - sceneEye;
            float thickness = _LoogaWaterReflection.y + sceneEye * 0.01;
            if (behind < thickness)
            {
                confidence = 1 - 0.5 * behind / thickness;
                break;
            }
            t = cellT;
        }
        if (t > endT) return 0;
    }

    float2 uv = (origin.xy + ray.xy * t) / size;
    float2 edge = saturate(min(uv, 1 - uv) / _LoogaWaterReflection.z);
    confidence *= smoothstep(0, 1, edge.x) * smoothstep(0, 1, edge.y);
    return float3(uv, confidence);
}

// Turns a normal from the frame of a flat surface (up = +Y) into the frame of a surface with the normal up. A flat
// surface keeps the normal. The fall sheet uses it for the body look on its bend.
half3 RotateFromUp(half3 value, half3 up)
{
    float3 axis = float3(up.z, 0, -up.x);
    return normalize(value * up.y + cross(axis, value) + axis * (dot(axis, value) / max(1 + up.y, 1e-3)));
}

// Values of the water light at a pixel. Bodies and fall sheets use the same light.
struct WaterLightInput
{
    float3 positionWS;
    float2 screenUV;
    half3 view;
    // World normals: the lit surface, the mirror normal and the normal of the Fresnel term.
    half3 normal;
    half3 reflectionNormal;
    half3 fresnelNormal;
    // The scene behind, refracted, and the length of the light path through the water in meters.
    float2 refractedUV;
    float path;
    // Share of the scene behind that bubbles hide, 0 to 1: cloudy water shows only its scattered light.
    half cloud;
    // Light through the crests, 0 to 1.
    half crestLight;
    half subFoam;
    float perceptualRoughness;
    // Foam area that blurs the reflection, foam that hides the screen reflection and the share of the gloss.
    half foamArea;
    half foam;
    half gloss;
    // Weight of the planar reflection, 0 to 1.
    half planar;
};

// Light of the water without the surface foam: the scene behind absorbed along the path and the light scattered in
// the water, the light through the crests, the foam under the surface, the reflection (probes, screen-space trace,
// planar mirror) with Fresnel and the sun highlight.
half3 WaterLight(WaterLightInput input, Light light, half3 sun)
{
    float3 transmittance = exp(-_Absorption.rgb * _AbsorptionScale * input.path) * (1 - input.cloud);
    half3 scatter = _DeepColor.rgb * (SampleSH(half3(0, 1, 0)) + sun * saturate(light.direction.y));
    half3 refracted = SampleSceneColor(input.refractedUV) * transmittance + scatter * (1 - transmittance);
    half3 sssDir = normalize(light.direction + input.normal * _SSSDistortion);
    half sssView = pow(saturate(dot(input.view, -sssDir)), _SSSPower);
    refracted += _SSSColor.rgb * sun * (sssView * input.crestLight * _SSSStrength);
    // Under-water foam is lit through the water above it.
    half3 subFoamLit = _FoamColor.rgb * (SampleSH(half3(0, 1, 0)) + sun * saturate(light.direction.y)) *
        exp(-_Absorption.rgb * _AbsorptionScale * _SubFoamDepth * 2);
    refracted = lerp(refracted, subFoamLit, input.subFoam * 0.6);

    half3 reflectDir = reflect(-input.view, input.reflectionNormal);
    reflectDir.y = abs(reflectDir.y);
    half3 reflection = GlossyEnvironmentReflection(reflectDir, input.positionWS, input.perceptualRoughness, 1,
        input.screenUV);
    const half f0 = 0.02;
    half fresnel = f0 + (1 - f0) * pow(1 - saturate(dot(input.fresnelNormal, input.view)), 5);
    // Screen-space reflection of the opaque scene. The probe reflection stays where the ray misses. No ray
    // is traced where the reflection is almost invisible: steep views (low Fresnel) and dense foam.
    // The screen reflection fades in above the limit, so the edge of the traced area does not show.
    // Water at the height of a planar reflection shows the mirrored scene, which also holds what is not on
    // the screen. The screen trace then runs only while the planar reflection fades in.
    half visibleReflection = _ScreenReflection * fresnel * (1 - input.foam);
    [branch] if (_LoogaWaterHiZSize.w > 0 && visibleReflection > MinimumScreenReflection && input.planar < 1)
    {
        float3 screenHit = TraceScreenReflection(input.positionWS, reflectDir);
        [branch] if (screenHit.z > 0)
        {
            half fadeIn = saturate(visibleReflection / MinimumScreenReflection - 1);
            reflection = lerp(reflection, SampleSceneColor(screenHit.xy),
                screenHit.z * _ScreenReflection * fadeIn * (1 - input.foamArea * 0.6));
        }
    }
    [branch] if (input.planar > 0)
    {
        float3 tilt = mul((float3x3)UNITY_MATRIX_V, float3(input.reflectionNormal.x, 0, input.reflectionNormal.z));
        float2 planarUV = input.screenUV + tilt.xy * _LoogaPlanarReflectionParams.w;
        // Rough far water reads a blurred mip.
        float lod = input.perceptualRoughness * 0.5 * _LoogaPlanarReflectionParams.z;
        half3 mirrored = SAMPLE_TEXTURE2D_LOD(_LoogaPlanarReflection, sampler_LinearClamp, planarUV, lod).rgb;
        reflection = lerp(reflection, mirrored, input.planar * (1 - input.foamArea * 0.6));
    }
    reflection *= _ReflectionStrength;
    half3 color = lerp(refracted, reflection, fresnel * input.gloss);
    color += SunSpecular(input.reflectionNormal, input.view, light, input.perceptualRoughness, f0) *
        (_SunStrength * input.gloss);
    return color;
}

// Values of the body surface at a pixel.
struct BodyInput
{
    float3 positionWS;
    float4 positionCS;
    // World XZ before the waves move the vertex.
    float2 grid;
    float2 flow;
    float flowAlpha;
    // Surface height before the waves.
    float surfaceHeight;
    float2 regime;
    // Height of the shore waves at the vertex (see the edge lift).
    float vertexShoreHeight;
    // Distance upstream of the brink of a welded fall, in meters. Large without a weld.
    float lipDistance;
    // World normal of the flat surface: up for a body, the sheet normal on a fall sheet.
    half3 up;
};

// Color (without fog) and alpha of the body surface. cut = the pixel is past the water edge.
half3 ShadeBody(BodyInput input, out half alpha, out bool cut)
{
    float3 positionWS = input.positionWS;
    float2 grid = input.grid;
    float time = _Time.y;
    half3 view = GetWorldSpaceNormalizeViewDir(positionWS);
    float distance = length(_WorldSpaceCameraPos - positionWS);
    float far = saturate(distance / max(_DetailFadeDistance, 1));
    float2 flow = input.flow;
    float fast = max(saturate(length(flow) / max(_FastSpeed, 0.01)), input.regime.x * _RegimeTurbulence);
    float slickMask = WindSlick(grid, flow, fast, time);
    // Toward the brink of a welded fall the waves calm, the same on the body and on the sheet.
    float normalCalm = WeldNormalCalm(input.lipDistance);
    float displacementCalm = WeldDisplacementCalm(input.lipDistance);
    float rush = WeldRush(input.lipDistance);

    // Detail normals: two drifting layers and a large layer move with the flow. Slopes add (partial
    // derivative blending). Fast water gets stronger detail normals and loses its own wind drift.
    float cycle = max(_FlowCycle, 0.1);
    FastPhases phases = GetFastPhases(flow, grid, time, cycle, _DetailSizeA);
    float slick = lerp(1 - _SlickStrength, 1, slickMask);
    float detailFade = lerp(1, _FarDetail, far) * lerp(1, _FastNormal, fast) * slick;
    // The ocean waves give every slope from their spectra, so the ocean path has no tiling detail
    // normals. Their fixed patterns made the sea look lumpy.
    bool oceanWaves = false;
    #if defined(_OCEAN_WAVES)
    oceanWaves = _LoogaOceanParams.x > 0;
    #endif
    float2 slope = 0;
    [branch] if (!oceanWaves)
    {
        float4 drift = _DetailDrift * time * (1 - fast);
        slope = FastDetailSlope(grid, phases, drift.xy, _DetailSizeA, _DetailStrengthA, 0);
        slope += FastDetailSlope(grid, phases, drift.zw, _DetailSizeB, _DetailStrengthB, 0.61);
        // Far away the near layers blend into the large copy of layer A.
        float farBlend = smoothstep(0.1, 0.8, far);
        float2 farSlope = FastDetailSlope(grid, phases, drift.xy, _DetailSizeA * _FarDetailScale,
            _DetailStrengthA, 1.13);
        slope = lerp(slope, farSlope, farBlend);
        slope *= detailFade;
        // The large layers move at the full flow speed over a longer cycle.
        FastPhases macroPhases = GetFastPhases(flow, grid, time, cycle * 2, _MacroSize);
        slope += FastDetailSlope(grid, macroPhases, 0, _MacroSize, _MacroStrength, -0.43) *
            lerp(0.75, 1, slick);
        slope += FastDetailSlope(grid, macroPhases, 0, _SwellSize, _SwellStrength, 2.29) *
            lerp(0.7, 1, slick);
    }
    float crestHeight;
    // The crest foam rule uses this height. The ocean waves get whitecap foam from the folds instead.
    float crestFoamHeight;
    half oceanCoverage = 0;
    // Swash at the water edge from the shore waves, 0.5 without them.
    half swash = 0.5;
    half shoreWaveWeight = 0;
    // Height of the water at the pixel above the interpolated mesh, in meters (see the edge lift).
    float edgeHeight = 0;
    float waveStrength = lerp(1, _FarWaveNormal, smoothstep(0.1, 0.8, far)) * lerp(0.6, 1, slickMask);
    float waveWeight = FlowWaveWeight(flow, input.regime.x) * normalCalm;
    half3 waveNormal;
    #if defined(_OCEAN_WAVES)
    [branch] if (oceanWaves)
    {
        // The slopes fade with the distance to a small share, and the whitecaps fade too.
        float oceanFade = lerp(0.015, 1, exp(-_OceanNormalFalloff * distance));
        float cascadeFoam;
        waveNormal = OceanNormal(grid, oceanFade * lerp(0.6, 1, slickMask) * _OceanNormalStrength,
            waveWeight * _OceanHeight, OceanWaterDepth(grid), crestHeight, cascadeFoam);
        oceanCoverage = smoothstep(0, 1, 0.75 * cascadeFoam) * exp(-_OceanFoamFalloff * distance) *
            _OceanFoam;
        // The shore waves add their slope, their height to the crest light and their surf foam.
        ShoreWave shoreWave = ShoreWaveAt(grid, _LoogaOceanTime);
        float2 shoreSlope = shoreWave.slope * (oceanFade * _OceanNormalStrength * normalCalm);
        waveNormal = normalize(half3(waveNormal.x / waveNormal.y - shoreSlope.x, 1,
            waveNormal.z / waveNormal.y - shoreSlope.y));
        crestHeight = clamp(crestHeight + shoreWave.height / max(_OceanCrestHeight, 0.01), -1, 1);
        oceanCoverage = max(oceanCoverage, saturate(shoreWave.surf * 0.8 * _SurfFoam));
        swash = shoreWave.swash;
        shoreWaveWeight = shoreWave.weight;
        edgeHeight = (shoreWave.height * FlowWaveWeight(flow, input.regime.x) +
            shoreWave.runUp * LoogaSwashGridFade(grid)) * displacementCalm - input.vertexShoreHeight;
        // A noise of foam clumps raises and lowers the water a little at the edge, so the edge is ragged and
        // curved even where the ground has straight contours. The noise rides on the moving surface.
        [branch] if (shoreWave.edge > 0)
        {
            half breakup = SAMPLE_TEXTURE2D_LOD(_FoamDetailMap, sampler_FoamDetailMap,
                Rotate(grid, 0.83) / (_FoamDetailSize * 2), 1).r * 0.6 +
                SAMPLE_TEXTURE2D_LOD(_FoamDetailMap, sampler_FoamDetailMap,
                Rotate(grid, -0.41) / (_FoamDetailSize * 0.7), 0).r * 0.4;
            edgeHeight += (breakup - 0.5) * EdgeBreakupHeight * shoreWave.edge;
        }
        crestFoamHeight = -1;
    }
    else
    {
        waveNormal = WaveNormal(grid, time, waveStrength, waveWeight, crestHeight);
        crestFoamHeight = crestHeight;
    }
    #else
    waveNormal = WaveNormal(grid, time, waveStrength, waveWeight, crestHeight);
    crestFoamHeight = crestHeight;
    #endif
    float2 waveSlope = waveNormal.xz / max(waveNormal.y, 0.2);
    // The waves that run out from the plunge of a fall tilt the surface and carry surf foam.
    // The size of a pixel on the water: its size across the view, stretched by the grazing angle.
    float pixelSize = 2 * distance / (UNITY_MATRIX_P._m11 * _ScreenParams.y) / max(abs(view.y), 0.2);
    PlungeWave plungeWave = PlungeWaveAt(grid, pixelSize);
    waveSlope -= plungeWave.slope * normalCalm;
    // Boils of fast water tilt the surface. Far away they fade, so they do not shimmer.
    waveSlope -= Turbulence(grid, flow, input.regime.x, time).yz *
        (1 - smoothstep(BoilFadeStart, BoilFadeEnd, distance)) * normalCalm;
    // Rushing water before a brink boils. Only the light follows the boils, so the weld line keeps its positions.
    [branch] if (rush > 0)
    {
        waveSlope -= LoogaBoils(grid, time).yz * (WeldRushBoils * rush) *
            (1 - smoothstep(BoilFadeStart, BoilFadeEnd, distance));
    }
    half3 normal = normalize(float3(waveSlope.x + slope.x, 1, waveSlope.y + slope.y));
    // The ocean keeps its full normal for the Fresnel term, so the slopes facing the sky stay bright.
    half3 fresnelNormal = normalize(lerp(normal, waveNormal, oceanWaves ? 0 : _FresnelNormalFlatten));

    // Depth behind the surface. Refraction moves the sample along the surface slope, but never onto an
    // object in front of the water.
    float2 screenUV = GetNormalizedScreenSpaceUV(input.positionCS);
    float surfaceEye = LinearEyeDepth(positionWS, GetWorldToViewMatrix());
    float rawDepth = WaterSceneDepth(screenUV);
    float thickness = LinearEyeDepth(rawDepth, _ZBufferParams) - surfaceEye;
    // The pixel water height moves the surface along the view ray, which changes the eye depth too.
    thickness += edgeHeight / max(view.y, 0.05) * surfaceEye / max(distance, 1e-3);
    float2 refraction = (normal.xz - waveNormal.xz) * _RefractionStrength / max(surfaceEye, 0.5);
    refraction *= float2(UNITY_MATRIX_P._m00, UNITY_MATRIX_P._m11) * saturate(thickness);
    float2 refractedUV = screenUV + refraction;
    float refractedRaw = WaterSceneDepth(refractedUV);
    if (LinearEyeDepth(refractedRaw, _ZBufferParams) < surfaceEye)
    {
        refractedUV = screenUV;
        refractedRaw = rawDepth;
    }
    float3 sceneWS = ComputeWorldSpacePosition(refractedUV, refractedRaw, UNITY_MATRIX_I_VP);
    float path = min(length(sceneWS - positionWS), _MaxPath);
    float waterDepth = max(positionWS.y + edgeHeight - sceneWS.y, 0);
    // The contact line uses the depth straight behind the pixel, so refraction does not move it.
    float contactHeight = positionWS.y + edgeHeight - ComputeWorldSpacePosition(screenUV, rawDepth,
        UNITY_MATRIX_I_VP).y;
    // Below the ground at the pixel water height: this is past the water edge.
    cut = edgeHeight != 0 && contactHeight < 0;
    float contactDepth = max(contactHeight, 0);
    contactDepth = min(contactDepth, max(thickness, 0));

    // Foam coverage and detail. The detail moves with the flow.
    float foamFast = saturate((length(flow) - _FastFoamStart) / max(_FastSpeed, 0.01));
    half whiteWater = max(input.regime.y * _RegimeWhiteWater, rush * WeldRushWhiteWater);
    // At the water edge the shore waves push the foam up and down the shore with each crest.
    half surge = oceanWaves ? lerp(saturate(0.5 + 0.5 * crestHeight), swash, shoreWaveWeight) : 0.5;
    half coverage = FoamCoverage(grid, oceanWaves ? positionWS.xz : grid, flow, time, contactDepth,
        waterDepth, crestFoamHeight, foamFast, whiteWater, surge, oceanWaves,
        min(distance * FoamSimBlur, FoamSimMaximumBlur));
    coverage = max(coverage, plungeWave.foam);
    half4 foamDetail = 0;
    half foam = 0;
    half subFoam = 0;
    // Far away the detail becomes its average.
    half foamFar = smoothstep(0.2, 1, distance / max(_FoamFadeDistance, 1));
    [branch] if (coverage > 0.003 && oceanWaves)
    {
        // On the ocean the shore and contact foam rides the water surface, which moves up the shore with
        // the swash and back. A slow sea drift does not carry it away from the shore. In a current, such as
        // the outflow below a waterfall, the detail moves with the flow. The foam simulation gives that
        // foam its shape: patches that boil up at the impact and thin out downstream.
        foamDetail = FoamDetail(grid);
        half streak = smoothstep(FoamFlowStart, FoamFlowFull, length(flow));
        FlowPhases currentPhases = GetFlowPhases(flow, grid, time, CurrentFoamCycle);
        [branch] if (streak > 0.01)
        {
            foamDetail = lerp(foamDetail, CurrentFoamDetail(grid, currentPhases), streak);
        }
        foam = lerp(FoamFromDetail(foamDetail, coverage), coverage * 0.6, foamFar);
        float2 subGrid = grid - view.xz / max(view.y, 0.25) * _SubFoamDepth;
        half subDetail = SAMPLE_TEXTURE2D_BIAS(_FoamDetailMap, sampler_FoamDetailMap,
            Rotate(subGrid, 1.21) / (_FoamDetailSize * 1.7), 2).r;
        // In a current the foam under the surface moves with the flow too.
        [branch] if (streak > 0.01)
        {
            half flowing = SAMPLE_TEXTURE2D_BIAS(_FoamDetailMap, sampler_FoamDetailMap,
                Rotate(subGrid - currentPhases.offset0, 1.21) / (_FoamDetailSize * 1.7), 2).r *
                currentPhases.weight0 + SAMPLE_TEXTURE2D_BIAS(_FoamDetailMap, sampler_FoamDetailMap,
                Rotate(subGrid - currentPhases.offset1, 1.21) / (_FoamDetailSize * 1.7), 2).r *
                currentPhases.weight1;
            subDetail = lerp(subDetail, flowing, streak);
        }
        subFoam = saturate((subDetail - 1 + coverage * 1.2) / 0.5) * coverage * _SubFoam;
    }
    else if (coverage > 0.003)
    {
        // In a current the foam detail is the current foam of the ocean path: hex tiles without a repeat and one
        // flow cycle of fixed length. The detail of slow water shortened its cycle in fast water, so the foam
        // pulsed, and its plain tiles repeated in rows over the wide foam below a fall.
        half streak = smoothstep(FoamFlowStart, FoamFlowFull, length(flow));
        FlowPhases currentPhases = GetFlowPhases(flow, grid, time, CurrentFoamCycle);
        [branch] if (streak < 0.99)
        {
            foamDetail = FoamDetail(grid - phases.slow.offset0) * phases.slow.weight0 +
                FoamDetail(grid - phases.slow.offset1) * phases.slow.weight1;
            [branch] if (phases.blend > 0.01)
            {
                half4 fastDetail = FoamDetail(grid - phases.fast.offset0) * phases.fast.weight0 +
                    FoamDetail(grid - phases.fast.offset1) * phases.fast.weight1;
                foamDetail = lerp(foamDetail, fastDetail, phases.blend);
            }
        }
        [branch] if (streak > 0.01)
        {
            foamDetail = lerp(foamDetail, CurrentFoamDetail(grid, currentPhases), streak);
        }
        // Far away the foam is about its average share of the coverage.
        foam = lerp(FoamFromDetail(foamDetail, coverage), coverage * 0.6, foamFar);
        // Foam under the surface: the same coverage seen lower along the view, soft and wider. In a current it
        // moves with the two current phases, so it does not jump back at the end of a cycle.
        float2 subGrid = grid - view.xz / max(view.y, 0.25) * _SubFoamDepth;
        half subDetail = SAMPLE_TEXTURE2D_BIAS(_FoamDetailMap, sampler_FoamDetailMap,
            Rotate(subGrid - phases.slow.offset0, 1.21) / (_FoamDetailSize * 1.7), 2).r;
        [branch] if (streak > 0.01)
        {
            half flowing = SAMPLE_TEXTURE2D_BIAS(_FoamDetailMap, sampler_FoamDetailMap,
                Rotate(subGrid - currentPhases.offset0, 1.21) / (_FoamDetailSize * 1.7), 2).r *
                currentPhases.weight0 + SAMPLE_TEXTURE2D_BIAS(_FoamDetailMap, sampler_FoamDetailMap,
                Rotate(subGrid - currentPhases.offset1, 1.21) / (_FoamDetailSize * 1.7), 2).r *
                currentPhases.weight1;
            subDetail = lerp(subDetail, flowing, streak);
        }
        subFoam = saturate((subDetail - 1 + coverage * 1.2) / 0.5) * coverage * _SubFoam;
    }
    #if defined(_OCEAN_WAVES)
    // Whitecaps: the shapes come from the cascade foam, the look from the shared foam detail. Far away the
    // foam is its coverage. The detail drifts with the foam. Bubbles under dense foam make the water below
    // it light.
    [branch] if (oceanCoverage > 0.003)
    {
        half4 whitecapDetail = FoamDetail(grid - _LoogaOceanWind.zw);
        half coverageNow = saturate(oceanCoverage);
        half whitecap = lerp(FoamFromDetail(whitecapDetail, coverageNow), coverageNow * 0.7, foamFar);
        foamDetail.a = whitecap > foam ? whitecapDetail.a : foamDetail.a;
        foam = max(foam, whitecap);
        subFoam = max(subFoam, coverageNow * coverageNow * _SubFoam * 0.5);
    }
    #endif
    // Rushing water before a brink is milky all through, near as far. Its white water is thin, so near the camera
    // the detail showed only a few bubbles, and the band was lighter only far away, where the foam is its average.
    // Near the camera that average is the least foam. The bubbles vary it.
    half rushFoam = min(coverage, rush * WeldRushWhiteWater * 1.2) * 0.6 * lerp(0.7, 1.3, foamDetail.g);
    foam = max(foam, lerp(rushFoam, 0, foamFar));

    // On a fall sheet the flat-surface normals turn with the sheet.
    half3 litNormal = normal;
    half3 litFresnelNormal = fresnelNormal;
    #if defined(_WATER_FALL)
    litNormal = RotateFromUp(normal, input.up);
    litFresnelNormal = RotateFromUp(fresnelNormal, input.up);
    #endif

    // Light.
    float4 shadowCoord = TransformWorldToShadowCoord(positionWS);
    Light light = GetMainLight(shadowCoord);
    half3 sun = light.color * (light.shadowAttenuation * light.distanceAttenuation);
    half3 ambient = SampleSH(litNormal);

    WaterLightInput lightInput;
    lightInput.positionWS = positionWS;
    lightInput.screenUV = screenUV;
    lightInput.view = view;
    lightInput.normal = litNormal;
    lightInput.fresnelNormal = litFresnelNormal;
    lightInput.refractedUV = refractedUV;
    lightInput.path = path;
    // The bubbles of rushing water before a brink hide the ground under it and the drop past it.
    lightInput.cloud = saturate(rush * WeldRushCloud);
    lightInput.crestLight = saturate(crestHeight * 0.5 + 0.5);
    lightInput.subFoam = subFoam;
    // Light from the surface.
    float perceptualRoughness = 1 - lerp(_Smoothness, _FarSmoothness, far);
    // Foam breaks up the reflection. Between the bubbles the water is rough and tilts in all directions,
    // so the mirror image there is blurred and distorted, and dense foam hides it.
    half foamArea = saturate(max(coverage, oceanCoverage)) * (1 - foamFar * 0.5);
    lightInput.perceptualRoughness = lerp(perceptualRoughness, 0.5, foamArea);
    half2 foamTilt = (foamDetail.rg - 0.5) * (foamArea * 0.6);
    half3 reflectionNormal = normalize(half3(normal.x + foamTilt.x, normal.y, normal.z + foamTilt.y));
    #if defined(_WATER_FALL)
    reflectionNormal = RotateFromUp(reflectionNormal, input.up);
    #endif
    lightInput.reflectionNormal = reflectionNormal;
    lightInput.foamArea = foamArea;
    lightInput.foam = foam;
    // Foam is matte: dense foam covers the reflection and the sun highlight of the water below it.
    lightInput.gloss = 1 - saturate(foam * 1.5);
    #if defined(_WATER_FALL)
    // On a sheet the planar mirror fades out smoothly as the water drops below the reflection plane.
    float planarOffset = abs(input.surfaceHeight - _LoogaPlanarReflectionParams.x);
    lightInput.planar = _LoogaPlanarReflectionParams.y *
        saturate(1 - (planarOffset - PlanarHeightTolerance) / PlanarHeightTolerance);
    #else
    lightInput.planar = abs(input.surfaceHeight - _LoogaPlanarReflectionParams.x) < PlanarHeightTolerance ?
        _LoogaPlanarReflectionParams.y : 0;
    #endif
    half3 color = WaterLight(lightInput, light, sun);

    // Surface foam: rough and diffuse, so it covers the reflection and the sun highlight. The light wraps
    // around the bubbles, the low parts between them are darker, and thin foam lets the sun through.
    half foamOcclusion = lerp(1 - _FoamOcclusion, 1, foamDetail.a);
    half wrap = saturate((dot(litNormal, light.direction) + 0.5) / 1.5);
    half backLight = pow(saturate(dot(view, -light.direction)), 4) * (1 - foam) * _FoamTranslucency;
    half3 foamLit = _FoamColor.rgb * ((ambient + sun * wrap) * foamOcclusion + sun * backLight);
    color = lerp(color, foamLit, foam);

    // Foam floats on top, so it stays visible where the thin water at the edge fades out.
    // On a gentle beach a few centimeters of water are a meter of sand, so the foam fades only over the last
    // 2.5 cm and reaches the water edge.
    alpha = max(saturate(thickness / max(_EdgeFade, 1e-3)), foam * saturate(thickness / 0.025)) *
        saturate(input.flowAlpha);
    return color;
}

#if defined(_WATER_FALL)
TEXTURE2D(_FallHeightMap);
TEXTURE2D(_FallNormalMap);
TEXTURE2D(_FallFoamMask);
TEXTURE2D(_FallNoiseMap);
TEXTURE2D(_FallPoolFoamMap);
// All fall textures use the sampler of the noise texture, which every fall stage reads: the ocean and the body
// textures use most of the 16 sampler slots.
SAMPLER(sampler_FallNoiseMap);

// The foam shell of a sheet that ends in water fades out over this many times End Pin from its end.
static const float ShellEndFade = 1.0;

// A broad, smooth noise from a small mip of the noise texture: about a third of a texture tile per feature.
// The noise texture is a fine foam mask, so its small mips hold values from about 0.1 to 0.4 (median 0.24),
// and the result is spread over 0 to 1.
float FallBroadNoise(float2 uv, float mip)
{
    return smoothstep(0.1, 0.38, SAMPLE_TEXTURE2D_LOD(_FallNoiseMap, sampler_FallNoiseMap, uv, mip).r);
}

// Clump field from 0 (hollow) to 1 (top of a clump), across the flow (meters) and along it in travel time. It
// moves exactly with the water. Two turned octaves of the broad noise, so the clumps are round and soft.
float FallClump(float across, float travel)
{
    float period = max(_FallClumpPeriod, 0.01);
    float x = across / max(_FallClumpSize, 0.1);
    float2 first = float2(x * 0.33, travel * 0.33 / period - WaterClockPhase(0.33 / period)) + 0.13;
    float2 second = float2(travel * 0.66 / period - WaterClockPhase(0.66 / period), -x * 0.66) + 0.71;
    return 0.7 * FallBroadNoise(first, 7) + 0.3 * FallBroadNoise(second, 7);
}

// Only the broad octave of the clump field. The vertices follow it, so the surface swells smoothly. Finer
// detail than the mesh grid would turn into spikes.
float FallBroadClump(float across, float travel)
{
    float period = max(_FallClumpPeriod, 0.01);
    float x = across / max(_FallClumpSize, 0.1);
    return FallBroadNoise(float2(x * 0.33, travel * 0.33 / period - WaterClockPhase(0.33 / period)) + 0.13, 7);
}

// Clump movement in meters: it grows down the drop and fades out over the pool.
float FallClumpAmount(float progress, float shell)
{
    return _FallClumpHeight * smoothstep(0.05, 0.9, progress) * (1 - saturate((progress - 1) * 3)) *
        lerp(1, _FallShellClump, shell);
}

// 1 on the sheet, 0 at the end of a sheet with End Pin: a smooth ramp over End Pin times the scale from the end
// (mesh meters, UV4.z). 1 everywhere without End Pin.
float FallEndFree(float endDistance, float scale)
{
    return _FallEndPin > 0 ? smoothstep(0, 1, saturate(endDistance / (_FallEndPin * scale))) : 1;
}

// 0 on the drop, 1 at the plunge, 0 again some way out over the pool.
float FallBoil(float progress)
{
    return smoothstep(0.97, 1.03, progress) * (1 - smoothstep(1.06, 1.5, progress));
}

// A texture coordinate along the flow that moves with the water: the travel time of the water minus the time,
// in tiles. The texture repeats every tile, so only the phase of the time counts. A speed scale above 1 moves the
// texture faster than the water.
float FallAlong(float travel, float tile, float speed)
{
    return travel / tile - WaterClockPhase(speed / tile);
}

// The streak textures sit on a grid of triangles (hex tiling, as HexFoamDetail): each grid point shows the texture
// with its own random shift, mirrored across the flow or not, and the three points around a pixel blend with
// sharpened weights. The streaks stay along the flow, and the texture shows no repeat in rows or lanes. Rows of the
// grid are SheetRowHeight tiles apart along the flow. The streak position along the flow wraps every
// SheetPeriodRows rows, so the random values of the grid repeat with the same period and the texture moves without
// a jump.
static const float SheetRowHeight = 0.75;
static const float SheetPeriodRows = 64;
static const float SheetHexSharpness = 3.0;
// Cells along the flow after which the noise fields of the curtain repeat. The field positions along the flow wrap
// with this period, so the fields move without a jump.
static const float FallFieldPeriod = 64;

// A position along the flow that moves with the water: the travel time in cells of a length (seconds of travel
// time) minus the time. It wraps every period cells, so a texture or noise that repeats with that period moves
// without a jump.
float FallAlongPeriodic(float travel, float length, float period)
{
    return travel / length - period * WaterClockPhase(1 / (length * period));
}

// A streak texture position on the sheet: across the flow in streak widths, along it in travel time with rate tiles
// per second of travel time. The texture moves exactly with the water and stretches where the water speeds up. A
// speed scale moves a layer faster or slower than the water. The time term is the same over the whole sheet, so the
// texture does not drift, shear or pulse. Textures that flowed in phases from a fixed start pattern changed in
// place and did not move down with the water. uv is the continuous position for the texture gradients, uv0 to uv2
// the shifted positions of the three grid points, mirror their sign across the flow.
struct SheetUV
{
    float2 uv;
    float2 uv0;
    float2 uv1;
    float2 uv2;
    float3 mirror;
    float3 weights;
};

void SheetHexPoint(float2 gridPoint, float layer, float2 uv, out float2 shifted, out float mirror)
{
    // Points with the same position across the flow have the same 2 x column + row.
    float row = gridPoint.y - SheetPeriodRows * floor(gridPoint.y / SheetPeriodRows);
    float2 key = float2(2 * gridPoint.x + gridPoint.y, row + layer * SheetPeriodRows);
    float2 random = LoogaFoamHash(key);
    mirror = LoogaFoamHash(key + float2(0, 4 * SheetPeriodRows)).x < 0.5 ? -1 : 1;
    shifted = float2(uv.x * mirror, uv.y) + random;
}

SheetUV SheetFlow(float across, float travel, float rate, float speedScale, float widthScale, float lengthScale,
    float layer)
{
    float tile = lengthScale / max(rate, 0.01);
    float u = across / max(_FallStreakWidth * widthScale, 0.01) + layer * 0.37;
    float period = SheetPeriodRows * SheetRowHeight;
    float v = travel / tile - period * WaterClockPhase(speedScale / (tile * period));
    SheetUV result;
    result.uv = float2(u, v);
    float row = v / SheetRowHeight;
    float2 skewed = float2(u - 0.5 * row, row);
    float2 base = floor(skewed);
    float3 local = float3(skewed - base, 0);
    local.z = 1 - local.x - local.y;
    float upper = step(0, -local.z);
    float flip = 2 * upper - 1;
    float3 weights = float3(-local.z * flip, upper - local.y * flip, upper - local.x * flip);
    weights = pow(max(weights, 0), SheetHexSharpness);
    result.weights = weights / (weights.x + weights.y + weights.z);
    SheetHexPoint(base + upper, layer, result.uv, result.uv0, result.mirror.x);
    SheetHexPoint(base + float2(upper, 1 - upper), layer, result.uv, result.uv1, result.mirror.y);
    SheetHexPoint(base + float2(1 - upper, upper), layer, result.uv, result.uv2, result.mirror.z);
    return result;
}

// Blend of the three grid samples that keeps the contrast of one sample: a plain blend of different samples is
// flatter, so the cell edges showed as soft patches.
float SheetBlend(float3 values, float3 weights, float mean)
{
    return mean + dot(weights, values - mean) * rsqrt(dot(weights, weights));
}

// The texture gradients come from the continuous position: the shifts step at the cell edges and would select a
// blurred mip there.
#define SHEET_SAMPLE(tex, sheetUV) SheetBlend(float3( \
    SAMPLE_TEXTURE2D_GRAD(tex, sampler_FallNoiseMap, sheetUV.uv0, ddx(sheetUV.uv) * float2(sheetUV.mirror.x, 1), \
        ddy(sheetUV.uv) * float2(sheetUV.mirror.x, 1)).r, \
    SAMPLE_TEXTURE2D_GRAD(tex, sampler_FallNoiseMap, sheetUV.uv1, ddx(sheetUV.uv) * float2(sheetUV.mirror.y, 1), \
        ddy(sheetUV.uv) * float2(sheetUV.mirror.y, 1)).r, \
    SAMPLE_TEXTURE2D_GRAD(tex, sampler_FallNoiseMap, sheetUV.uv2, ddx(sheetUV.uv) * float2(sheetUV.mirror.z, 1), \
        ddy(sheetUV.uv) * float2(sheetUV.mirror.z, 1)).r), sheetUV.weights, 0.5)

#define SHEET_SAMPLE_LOD(tex, sheetUV) SheetBlend(float3( \
    SAMPLE_TEXTURE2D_LOD(tex, sampler_FallNoiseMap, sheetUV.uv0, 0).r, \
    SAMPLE_TEXTURE2D_LOD(tex, sampler_FallNoiseMap, sheetUV.uv1, 0).r, \
    SAMPLE_TEXTURE2D_LOD(tex, sampler_FallNoiseMap, sheetUV.uv2, 0).r), sheetUV.weights, 0.5)

// One grid sample of the streak normal. A mirrored sample has its slope across the flow mirrored too.
half2 SheetNormalSample(float2 uv, float mirror, float2 dx, float2 dy, float strength)
{
    float2 scale = float2(mirror, 1);
    half3 normal = UnpackNormalScale(SAMPLE_TEXTURE2D_GRAD(_FallNormalMap, sampler_FallNoiseMap, uv, dx * scale,
        dy * scale), strength);
    return normal.xy * half2(mirror, 1);
}

half3 SheetNormal(SheetUV sheetUV, float strength)
{
    float2 dx = ddx(sheetUV.uv);
    float2 dy = ddy(sheetUV.uv);
    half2 a = SheetNormalSample(sheetUV.uv0, sheetUV.mirror.x, dx, dy, strength);
    half2 b = SheetNormalSample(sheetUV.uv1, sheetUV.mirror.y, dx, dy, strength);
    half2 c = SheetNormalSample(sheetUV.uv2, sheetUV.mirror.z, dx, dy, strength);
    float3 w = sheetUV.weights;
    return half3(SheetBlend(float3(a.x, b.x, c.x), w, 0), SheetBlend(float3(a.y, b.y, c.y), w, 0), 1);
}

// Smooth value noise along a line, 0 to 1, with a cell of 1. It does not repeat.
float FallLineNoise(float x, float seed)
{
    float cell = floor(x);
    float f = x - cell;
    f = f * f * (3 - 2 * f);
    return lerp(LoogaFoamHash(float2(cell, seed)).x, LoogaFoamHash(float2(cell + 1, seed)).x, f);
}

// Smooth value noise, 0 to 1, with a cell of 1, that repeats every FallFieldPeriod cells along y and not across.
float FallFieldNoise(float2 position, float seed)
{
    float2 cell = floor(position);
    float2 f = position - cell;
    f = f * f * (3 - 2 * f);
    float y0 = cell.y - FallFieldPeriod * floor(cell.y / FallFieldPeriod);
    float y1 = y0 + 1 < FallFieldPeriod ? y0 + 1 : 0;
    y0 += seed * FallFieldPeriod;
    y1 += seed * FallFieldPeriod;
    float a = LoogaFoamHash(float2(cell.x, y0)).x;
    float b = LoogaFoamHash(float2(cell.x + 1, y0)).x;
    float c = LoogaFoamHash(float2(cell.x, y1)).x;
    float d = LoogaFoamHash(float2(cell.x + 1, y1)).x;
    return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
}

// Two octaves of FallFieldNoise, 0 to 1: one octave of value noise shows the square grid of its cells. The second
// octave has twice the cells along y, so it repeats with the same period.
float FallFieldNoise2(float2 position, float seed)
{
    float noise = 0.65 * FallFieldNoise(position, seed) +
        0.35 * FallFieldNoise(position * float2(2.3, 2) + float2(0.5, 0.5), seed + 4);
    return saturate((noise - 0.5) * 1.35 + 0.5);
}

// Thickness of the water along the lip, from 0 (a thin, glassy veil) to 1 (a thick jet), at a distance along the
// lip in sheet units. A real lip is not even: the water gathers in jets where the lip is low and runs thin between
// them. Three octaves give jets and gaps of many widths. The field is fixed in place, as the lip is.
float FallJet(float lipArc)
{
    float x = lipArc / max(_FallJetSize, 0.1);
    float noise = 0.55 * FallLineNoise(x, 3) + 0.3 * FallLineNoise(x * 2.7 + 0.31, 5) +
        0.15 * FallLineNoise(x * 7.3 + 0.67, 7);
    return smoothstep(0.28, 0.72, noise);
}

// Side movement of the strands from -1 to 1. It moves with the water, so the strand edges fray and wander as
// they fall.
float FallFray(float lipArc, float travel)
{
    return FallFieldNoise2(float2(lipArc / 7.0, FallAlongPeriodic(travel, 0.45, FallFieldPeriod)), 1) * 2 - 1;
}

// Falling clusters of water, from 0 to 1: a dense head with a tail above it. They move exactly with the water, so
// they stretch as the water speeds up.
float FallComet(float lipArc, float travel)
{
    float2 position = float2(lipArc / max(_FallCometSize, 0.1),
        FallAlongPeriodic(travel, max(_FallCometLength, 0.01), FallFieldPeriod));
    // The head is the lowest part (largest travel time): a point sees the clusters below it as its tail.
    float head = FallFieldNoise2(position, 2);
    float tail = max(FallFieldNoise2(position + float2(0, 0.35), 2) * 0.8,
        FallFieldNoise2(position + float2(0, 0.7), 2) * 0.6);
    return smoothstep(0.5, 0.85, max(head, tail));
}

// Share of the curtain relief along the drop, 0 to 1.4: none before the bend over the brink, so the sheet start
// stays on the weld line, then growing as the curtain spreads, and gone before the bend into the water below.
float FallReliefShape(float progress)
{
    return smoothstep(0.02, 0.3, progress) * (1 - smoothstep(0.85, 1, progress)) * lerp(0.6, 1.4, saturate(progress));
}

// The foam shells move this many times more over the falling clusters than the sheet.
static const float ShellClusterScale = 3.0;

// Height of the falling clusters over the curtain, in meters.
float FallClusterHeight()
{
    return 0.5 * _FallJetBulge * _FallCometStrength;
}

// Share of the falling clusters along the drop: they form where the curtain breaks up.
float FallClusterShare(float progress)
{
    return smoothstep(_FallBreakup * 0.5, _FallBreakup * 1.5, progress);
}

// Pool foam in flow coordinates: across the flow (meters, wider toward the pool end, so the foam spreads out)
// and along the flow in travel time, so it moves exactly with the water. Like the foam below real falls, it
// is stretched along the flow into streaks and lace, and a slowly changing large noise bends the streaks like
// eddies. Returns the streak pattern (0 to 1), the fine lace (0 to 1) and a broad patch noise (0 to 1).
float3 FallPoolFoam(float across, float travel, float2 position, float pool, float time)
{
    float2 warpUv = position / 47.0 + time * float2(0.004, -0.006);
    float2 warp = float2(FallBroadNoise(warpUv, 6), FallBroadNoise(Rotate(warpUv, 1.3) + 0.37, 6)) * 2 - 1;
    float speedTile = max(_FallPoolTileSpeed, 0.1);
    float x = across / (1 + _FallPoolSpread * pool) + warp.x * _FallPoolWarp;
    float t = travel + warp.y * _FallPoolWarp / speedTile;
    float width = max(_FallPoolStreakWidth, 0.1);
    float streakLength = max(_FallPoolStreakLength, 0.1) / speedTile;
    // Smooth lanes along the flow decide where the foam gathers. The foam itself is a lace of bubble cells
    // at two scales, mildly stretched along the flow.
    float streaks = FallBroadNoise(float2(x / (width * 3) + 0.17, FallAlong(t, streakLength, 1)), 6);
    // The lace takes only a slight wobble from the eddy noise: a shift of a whole tile or more smears any
    // texture into marble swirls. Its cells are about round at 7 m/s and bunch up where the water slows.
    float xLace = across / (1 + _FallPoolSpread * pool) + warp.x * width * 0.25;
    float tLace = travel + warp.y * 0.05;
    // A mip bias blurs the cell walls, so the foam looks soft, not like a sharp net.
    float lace = 0.6 * SAMPLE_TEXTURE2D_BIAS(_FallPoolFoamMap, sampler_FallNoiseMap,
            float2(xLace / (width * 3.2) + 0.71, FallAlong(tLace, width * 3.2 / 7.0, 1) + 0.53), 1.5).r +
        0.4 * SAMPLE_TEXTURE2D_BIAS(_FallPoolFoamMap, sampler_FallNoiseMap,
            float2(xLace / (width * 1.3) + 0.29, FallAlong(tLace, width * 1.3 / 7.0, 1) + 0.11), 1.5).r;
    float patches = FallBroadNoise(float2(x / 37.0 + 0.61, FallAlong(t, 37.0 / speedTile, 1)), 7);
    return float3(streaks, lace, patches);
}

// Meters per sheet unit: the scale of the fall object.
float FallFrameScale()
{
    return length(GetObjectToWorldMatrix()._m00_m10_m20);
}

Varyings FallVertex(Attributes input)
{
    Varyings output = (Varyings)0;
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_TRANSFER_INSTANCE_ID(input, output);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
    // Bulges grow with the air on the drop. The plunge boils. Before the brink nothing moves the surface, so the
    // sheet start lies on the weld line of the body.
    float progress = input.sheet.z;
    SheetUV streak = SheetFlow(input.sheet.x, input.fallFlow.w, input.fallFlow.x, 1, 1, 1, 0);
    float height = SHEET_SAMPLE_LOD(_FallHeightMap, streak);
    float amount = _FallBulgeHeight * smoothstep(0, 0.8, saturate(progress)) * (1 - saturate(progress - 1) * 2);
    amount *= lerp(1, _FallShellBulge, input.extra.w);
    // Relief of the curtain: thick jets stand out and thin parts lie back, and the falling clusters are lumps on it.
    float lipArc = input.sheet.x - input.regime.z;
    float jetOffset = 0;
    [branch] if (_FallJetVariation > 0)
    {
        // The foam shells billow out over the clusters more than the sheet, so the white water lies in layers.
        float cluster = FallComet(lipArc, input.fallFlow.w) * FallClusterShare(progress) * FallClusterHeight() *
            lerp(1, ShellClusterScale, input.extra.w);
        jetOffset = ((FallJet(lipArc) - 0.5) * 2 * _FallJetBulge + cluster) * _FallJetVariation *
            FallReliefShape(progress);
    }
    // The plunge boils with the broad clump field only: the pool mesh is coarse, and the fine streak
    // texture made the pool surface ripple in sharp, jumping points.
    float broad = FallBroadClump(input.sheet.x, input.fallFlow.w);
    // 1 on the sheet, 0 at its end: the end of a sheet that falls into water stays still and sinks.
    float free = FallEndFree(input.extra.z, 1);
    float3 positionOS = input.positionOS.xyz + input.normalOS * free * (jetOffset + (height - 0.4) * amount +
        (broad - 0.35) * FallClumpAmount(progress, input.extra.w) + (broad - 0.4) * _FallBoilHeight *
        FallBoil(progress));
    float3 positionWS = TransformObjectToWorld(positionOS);
    positionWS.y += (LoogaBoils(positionWS.xz, _Time.y).x * _FallSeaBoil - _FallEndSink) * (1 - free);
    VertexNormalInputs normal = GetVertexNormalInputs(input.normalOS, input.tangentOS);
    // Toward the plunge and over the pool the depth moves toward the camera. A move along the view ray keeps
    // the screen position, so only the depth test changes.
    float bias = _FallPoolDepthBias * smoothstep(0.95, 1.0, progress);
    float3 towardCamera = normalize(GetCameraPositionWS() - positionWS);
    output.positionCS = TransformWorldToHClip(positionWS + towardCamera * bias);
    output.positionWS = positionWS;
    output.normalWS = normal.normalWS;
    output.tangentWS = float4(normal.tangentWS, input.tangentOS.w * GetOddNegativeScale());
    output.sheet = input.sheet;
    output.extra = input.extra;
    output.fallFlow = input.fallFlow;

    // The body look near the brink takes the same flow and regime as the body at the weld. The near ocean grid
    // reads its flow from the shore map and has no regime values, so the sheet does the same on the ocean.
    float2 flow = input.flow.xy;
    float2 regime = input.regime.xy;
    #if defined(_OCEAN_WAVES)
    [branch] if (_LoogaOceanParams.x > 0)
    {
        float2 shoreUV = (positionWS.xz - _ShoreMapRect.xy) * _ShoreMapRect.zw;
        bool covered = _ShoreMapRect.z > 0 && all(shoreUV > 0) && all(shoreUV < 1);
        flow = covered ? SAMPLE_TEXTURE2D_LOD(_ShoreMap, sampler_ShoreMap, shoreUV, 0).ba : 0;
        regime = 0;
    }
    #endif
    output.surface = float4(positionWS.xz, lipArc, ComputeFogFactor(TransformWorldToHClip(positionWS).z));
    output.flow = float4(flow, input.flow.z, positionWS.y);
    output.regime = regime;
    return output;
}

// The fall look at a pixel, before the light.
struct FallSurface
{
    float2 screenUV;
    float sceneEye;
    float surfaceEye;
    half3 view;
    half3 detail;
    half3 normal;
    float height;
    float mask;
    float clump;
    float clumpAmount;
    float onset;
    float onPool;
    half foam;
    float aeration;
    float plume;
    float runOut;
    // Share of the clear fall water over the body look: 0 before the brink, 1 past the clear handoff.
    float clearShow;
    half alpha;
    // Thickness of the water along the lip (1 without Jet Variation), the falling clusters (0 to 1) and the share of
    // the open spray veil (0 to 1).
    float jet;
    float comet;
    float gap;
};

FallSurface EvaluateFall(Varyings input)
{
    FallSurface fall = (FallSurface)0;
    float progress = input.sheet.z;
    float pool = saturate(progress - 1);
    float onPool = saturate(pool * 40);
    float rate = input.fallFlow.x;
    float speed = input.fallFlow.y;
    float brink = input.fallFlow.z;
    float travel = input.fallFlow.w;
    // Opaque surfaces in front hide the sheet. Transparent water does not, so the pool foam lies on it.
    fall.screenUV = GetNormalizedScreenSpaceUV(input.positionCS);
    fall.sceneEye = LinearEyeDepth(WaterSceneDepth(fall.screenUV), _ZBufferParams);
    fall.surfaceEye = LinearEyeDepth(input.positionWS, GetWorldToViewMatrix());
    clip(fall.sceneEye - fall.surfaceEye + 0.02);
    // Three streak layers: fine streaks, a narrower faster layer and a wide slower layer.
    SheetUV a = SheetFlow(input.sheet.x, travel, rate, 1, 1, 1, 0);
    SheetUV b = SheetFlow(input.sheet.x, travel, rate, 1.37, 0.61, 1.6, 1);
    SheetUV c = SheetFlow(input.sheet.x, travel, rate, 0.73, 2.3, 2.4, 2);

    half3 nA = SheetNormal(a, _FallNormalStrength);
    half3 nB = SheetNormal(b, _FallNormalStrength * 0.7);
    half3 nC = SheetNormal(c, _FallNormalStrength * 0.8);

    // Variation along the lip (Jet Variation): thick jets with thin, glassy water between them. Past Breakup the
    // curtain breaks into strands: their edges fray as they fall, gaps open between them where only spray falls,
    // and clusters of water fall down the strands and across the gaps.
    float variation = _FallJetVariation;
    float jet = 1;
    float comet = 0;
    float gap = 0;
    float breakup = 0;
    float2 reliefSlope = 0;
    [branch] if (variation > 0)
    {
        float lipArc = input.surface.z;
        breakup = FallClusterShare(progress) * (1 - onPool);
        jet = FallJet(lipArc);
        float cluster = FallComet(lipArc, travel);
        comet = cluster * breakup * _FallCometStrength;
        // The slopes of the relief (the vertex offset in FallVertex) turn the normal, so the jets read as round
        // columns and the clusters as lumps.
        float stepAcross = max(_FallCometSize * 0.25, 0.25);
        float stepTravel = max(_FallCometLength * 0.25, 0.01);
        float clusterHeight = FallClusterHeight() * breakup * lerp(1, ShellClusterScale, input.extra.w);
        reliefSlope = float2(
            (FallJet(lipArc + 1) - FallJet(lipArc - 1)) * _FallJetBulge +
                (FallComet(lipArc + stepAcross, travel) - cluster) / stepAcross * clusterHeight,
            (FallComet(lipArc, travel + stepTravel) - cluster) / (stepTravel * max(speed, 1)) * clusterHeight) *
            FallReliefShape(progress) * variation * _FallClumpNormal;
        // Below the break-up the curtain is a veil of spray with denser strands and falling clusters in it. Where
        // both are thin, the veil opens and the scene behind shows.
        float frayed = FallJet(lipArc + FallFray(lipArc, travel) * _FallStrandFray * breakup);
        float density = frayed * 0.8 + comet * 0.9;
        float openShare = _FallGapShare * smoothstep(_FallBreakup, 0.85, progress) * (1 - onPool);
        gap = smoothstep(-0.3, 0.3, openShare - density) * saturate(openShare * 10) * variation;
    }
    // Glassy over the brink: the detail and the air grow over the onset.
    float onset = smoothstep(_FallAirStart, _FallAirStart + max(_FallAirOnset, 0.01), brink);
    // The clump slopes, across the flow and along it (meters of travel at the local speed), turn the normal.
    float clumpAmount = FallClumpAmount(progress, input.extra.w);
    float clump = FallClump(input.sheet.x, travel);
    float stepAcross = _FallClumpSize * 0.15;
    float stepTravel = _FallClumpPeriod * 0.15;
    float2 clumpSlope = float2(
        (FallClump(input.sheet.x + stepAcross, travel) - FallClump(input.sheet.x - stepAcross, travel)) /
            (2 * stepAcross),
        (FallClump(input.sheet.x, travel + stepTravel) - FallClump(input.sheet.x, travel - stepTravel)) /
            (2 * stepTravel * max(speed, 1))) * clumpAmount * _FallClumpNormal;
    // White water gets weaker streak normals: foam is soft, not glassy. The air of the drop sets the share.
    float softFoam = lerp(1, _FallFoamNormal, saturate(progress / max(_FallFullAirProgress, 0.05)) * onset);
    half3 detail = normalize(half3((nA.xy + nB.xy + nC.xy) * lerp(1, 0.35, onPool) * lerp(0.3, 1, onset) *
        softFoam - clumpSlope - reliefSlope, 1));
    float3 bitangent = cross(input.normalWS, input.tangentWS.xyz) * input.tangentWS.w;
    half3x3 frame = half3x3(input.tangentWS.xyz, bitangent, input.normalWS);
    half3 normal = normalize(TransformTangentToWorld(detail, frame));

    float height = 0.5 * SHEET_SAMPLE(_FallHeightMap, a) + 0.5 * SHEET_SAMPLE(_FallHeightMap, c);
    float mask = 0.6 * SHEET_SAMPLE(_FallFoamMask, b) + 0.4 * SHEET_SAMPLE(_FallFoamMask, c);

    // Air starts at the brink, grows along the drop and along the walls.
    float wall = saturate(1 - input.extra.x / max(_FallWallFoamWidth, 0.01)) * _FallWallFoam;
    float air = min(lerp(_FallStartAir, 1, saturate(progress / _FallFullAirProgress)) + wall, _FallMaxAir) * onset;
    // Below the bend over the brink, thick jets carry more air than the thin water between them, until the curtain
    // breaks up. Over the bend the white water stays even: thin foam there showed the bend as deep blue water.
    air *= lerp(1, lerp(0.7, 1.1, jet), variation * (1 - breakup) * smoothstep(10, 25, brink));
    air = max(air, FallBoil(progress) * 0.95);
    float pattern = mask * 0.75 + height * 0.35;
    half foam = smoothstep(1 - air, 1 - air + _FallAirSoftness, pattern + air * 0.25);

    float aeration = 0;
    // Share of the clear pool water that the sheet shows: all of it in the basin, none of it out on the sea.
    float plume = 1;
    // Pool: a solid white boil at each plunge with a ragged edge, then foam that breaks into streaks and
    // lace along the flow and thins out with the distance, with clear water between the streaks.
    [branch] if (onPool > 0)
    {
        float3 poolFoam = FallPoolFoam(input.sheet.x, travel, input.positionWS.xz, pool, _Time.y);
        float poolDistance = pool * _FallPoolLength;
        float boilEdge = _FallPoolBoilLength * (0.6 + 0.8 * poolFoam.x);
        half boil = 1 - smoothstep(boilEdge * 0.7, boilEdge, poolDistance);
        // Past the training walls the milky water mixes into the sea. A slow, smooth noise moves the fade
        // edges, so the plume and the sheet sides have no straight outline. The plume starts to thin a little
        // inside the walls. The sides fade over half the open fade distance.
        float openFade = max(_FallPoolOpenFade, 0.1);
        float2 raggedUv = input.positionWS.xz + _Time.y * float2(0.15, -0.1);
        float ragged = 0.65 * SAMPLE_TEXTURE2D_LOD(_FallNoiseMap, sampler_FallNoiseMap, raggedUv / 61.0, 7).r +
            0.35 * SAMPLE_TEXTURE2D_LOD(_FallNoiseMap, sampler_FallNoiseMap, raggedUv / 23.0 + 0.43, 7).r;
        ragged = saturate((ragged - 0.1) / 0.3) - 0.5;
        plume = 1 - smoothstep(-0.2 * openFade, openFade,
            poolDistance - _FallPoolOpenStart + ragged * openFade * 0.6);
        float sides = smoothstep(0, openFade * 0.5, input.extra.z + ragged * openFade * 0.3);
        plume *= sides;
        // Bubbles under the surface make the water milky near the plunge and in the whole basin.
        aeration = max(boil, exp(-poolDistance / max(_FallPoolAeration, 0.1)) * (0.7 + 0.5 * poolFoam.y));
        float milkyReach = max(_FallPoolMilkyReach, 0.1);
        float milky = 1 - smoothstep(0.3 * milkyReach, milkyReach, poolDistance + ragged * milkyReach * 0.4);
        aeration = saturate(max(aeration, _FallPoolBasinAeration * plume * milky * (0.8 + 0.4 * poolFoam.z))) *
            onPool;
        // The foam amount falls with the distance, patchy across the pool, and fades at the sheet sides.
        float density = exp(-max(poolDistance - _FallPoolBoilLength, 0) / max(_FallPoolFoamReach, 0.1)) *
            lerp(1 - 0.6 * _FallPoolRaftCover, 1, poolFoam.z) * sides;
        density = saturate(density + wall * 0.4);
        // Erosion: a rising threshold eats the streaks from their thin parts, so the foam breaks into
        // lace and trails instead of fading as a whole. The wide edge and the thin, partly clear foam make
        // the foam soft.
        float poolPattern = poolFoam.y * 0.8 + poolFoam.x * 0.6 - 0.1;
        float threshold = 0.95 - density * 0.8;
        half streakFoam = smoothstep(threshold - 0.15, threshold + _FallPoolFoamSoftness * 0.7, poolPattern);
        half poolWhite = max(boil, streakFoam * saturate(density * 2.5) * lerp(0.7, 1, saturate(density * 1.5)));
        foam = lerp(foam, poolWhite, onPool);
        // Flat foam shading: deep cavities make the pool foam look coarse.
        height = lerp(height, 0.55 + 0.45 * saturate(poolPattern), onPool);
        mask = lerp(mask, poolPattern, onPool);

        // Pool ripples in the same coordinates as the foam, so they move with the water. The streak
        // ripples of the drop use time-cycled phases that slide several tiles per cycle at this speed and
        // only shimmer in place.
        float tile = _FallPoolFoamSize / max(_FallPoolTileSpeed, 0.1);
        float across = input.sheet.x / (1 + _FallPoolSpread * pool);
        half3 r0 = UnpackNormalScale(SAMPLE_TEXTURE2D(_FallNormalMap, sampler_FallNoiseMap,
            float2(across / (_FallPoolFoamSize * 0.5), FallAlong(travel, tile * 0.5, 1))), _FallNormalStrength);
        half3 r1 = UnpackNormalScale(SAMPLE_TEXTURE2D(_FallNormalMap, sampler_FallNoiseMap,
            float2(across / (_FallPoolFoamSize * 1.3) + 0.41, FallAlong(travel, tile * 1.3, 1) + 0.27)),
            _FallNormalStrength);
        // Churned, milky water has weaker ripples, so it does not look glassy.
        half3 poolDetail = normalize(half3((r0.xy + r1.xy) * 0.4 * (1 - 0.6 * aeration), 1));
        detail = normalize(lerp(detail, poolDetail, onPool));
        normal = normalize(TransformTangentToWorld(detail, frame));
    }

    half3 view = GetWorldSpaceNormalizeViewDir(input.positionWS);
    half fresnel = 0.02 + 0.98 * pow(1 - saturate(dot(normal, view)), 5);
    // The drop is opaque: its clear water shows the refracted scene. Over the pool the clear water lets
    // the pool surface show. The sheet fades in at its start and out at its end and sides.
    // On the drop the clear water fades in over the handoff, where the body look under it fades out.
    float clearShow = lerp(smoothstep(_FallClearHandoff.x, _FallClearHandoff.y, brink), 1, onPool);
    half poolClear = lerp(lerp(0.75, fresnel * 0.5, smoothstep(0.05, 0.4, pool)), 0.95, aeration);
    poolClear = max(poolClear, _FallPoolBasinCover) * plume;
    half alpha = lerp(lerp(1, poolClear, onPool) * clearShow, 1, foam);
    // The shell starts at the crest, keeps only the large lumps, covers more lower down and fades at the
    // bay walls.
    float coverage = _FallShellCoverage * lerp(0.7, 1.1, saturate(progress));
    float lumps = smoothstep(1 - coverage, 1 - coverage + _FallShellSoftness, pattern * 0.7 + height * 0.5);
    float shellAlpha = foam * lumps * saturate(input.extra.x / 1.2) * smoothstep(0, 0.06, progress);
    // A sheet that runs out onto water: the foam shell fades out before its end, so its loose lower edge does
    // not hang over the water like a curtain. The part past the plunge is white water that runs out onto the
    // water and fades at its end (End Fade), so the sheet and the water meet as one surface.
    float runOut = 0;
    [branch] if (_FallEndPin > 0)
    {
        shellAlpha *= FallEndFree(input.extra.z, ShellEndFade);
        // The white water comes in over the bend, so a clear jet has no line where it turns white.
        runOut = smoothstep(0.9, 1.1, progress);
        alpha = lerp(alpha, 1, runOut);
    }
    // In a gap the curtain has broken up: only a thin spray falls there, streaked along the flow, and the scene
    // behind shows through.
    half spray = _FallGapSpray * saturate(0.5 + mask);
    alpha = lerp(alpha, spray, gap);
    shellAlpha *= 1 - gap;
    foam = lerp(foam, 1, gap);
    // With End Sink, the part past the plunge lies in the water and does not draw.
    alpha *= _FallEndSink > 0 ? 1 - onPool : 1;
    alpha = lerp(alpha, shellAlpha, input.extra.w);
    alpha *= saturate(input.sheet.w / max(_FallStartFade, 0.01));
    alpha *= saturate(input.extra.z / max(_FallEndFade, 0.01));

    fall.view = view;
    fall.detail = detail;
    fall.normal = normal;
    fall.height = height;
    fall.mask = mask;
    fall.clump = clump;
    fall.clumpAmount = clumpAmount;
    fall.onset = onset;
    fall.onPool = onPool;
    fall.foam = foam;
    fall.aeration = aeration;
    fall.plume = plume;
    fall.runOut = runOut;
    fall.clearShow = clearShow;
    fall.alpha = alpha;
    fall.jet = lerp(1, jet, variation);
    fall.comet = comet * variation;
    fall.gap = gap;
    return fall;
}

// Color of the fall look without fog: the clear water with the water light, milky water and the white water.
half3 ShadeFall(Varyings input, FallSurface fall)
{
    Light light = GetMainLight(TransformWorldToShadowCoord(input.positionWS));
    half3 sun = light.color * (light.shadowAttenuation * light.distanceAttenuation);
    half3 normal = fall.normal;
    half3 view = fall.view;
    half3 ambient = SampleSH(normal);
    float nl = dot(normal, light.direction);

    // White water: wrapped diffuse, darker troughs, broad rough highlight and light through it from behind.
    half cavity = lerp(1 - _FallFoamShadow, 1, saturate(fall.height * 1.4 * lerp(1, fall.mask + 0.3, 0.5)));
    half wrap = saturate((nl + _FallFoamWrap) / (1 + _FallFoamWrap));
    // The hollows between the clumps are darker, so the clumps read as round, separate forms.
    float clumpWeight = saturate(fall.clumpAmount / max(_FallClumpHeight, 0.01));
    cavity *= lerp(1, lerp(1 - _FallClumpShadow, 1, smoothstep(0.05, 0.8, fall.clump)), clumpWeight);
    // Thin water and the open spray veil scatter less light than the dense strands and the falling clusters.
    cavity *= lerp(0.7, 1.05, fall.jet) * (1 + 0.4 * fall.comet) * (1 - 0.3 * fall.gap);
    half3 foamLit = _FallFoamColor.rgb * (ambient * (0.65 + 0.35 * fall.height) + sun * wrap) * cavity;
    // Light through the thin water at the silhouette.
    half rim = pow(1 - saturate(dot(normal, view)), 3) * _FallFoamRim;
    foamLit += _FallFoamColor.rgb * (ambient + sun * 0.5) * rim;
    half back = pow(saturate(dot(view, -normalize(light.direction + normal * 0.4))), 3);
    foamLit += _FallTranslucencyColor.rgb * sun * back * _FallTranslucency * (0.5 + 0.5 * (1 - fall.height));
    half3 halfDir = SafeNormalize(light.direction + view);
    float nh = saturate(dot(normal, halfDir));
    foamLit += sun * pow(nh, 24) * 0.08 * saturate(nl);

    // Clear water: the same water light as the body, with the path through the falling water. The bubbles in
    // milky water scatter the light, so it shows no clear, bent image of the scene behind and is rough.
    WaterLightInput lightInput;
    lightInput.positionWS = input.positionWS;
    lightInput.screenUV = fall.screenUV;
    lightInput.view = view;
    lightInput.normal = normal;
    lightInput.reflectionNormal = normal;
    lightInput.fresnelNormal = normal;
    lightInput.refractedUV = fall.screenUV + fall.detail.xy * _FallRefraction * (1 - fall.aeration);
    // At the brink the sheet shows the same shallow water over the crest as the water beside it.
    // Past the brink, thin glassy water is a layer with air behind it, so the light path through it is short.
    float maxPath = lerp(30, 4, smoothstep(10, 25, input.fallFlow.z) * (1 - fall.jet));
    float thickness = clamp(fall.sceneEye - fall.surfaceEye, max(input.extra.y, _FallMinPath * fall.onset), maxPath);
    // The churned basin water is deep and cloudy.
    lightInput.path = max(thickness, _FallPoolMinPath * fall.plume * fall.onPool);
    lightInput.cloud = 0;
    lightInput.crestLight = 0;
    lightInput.subFoam = 0;
    lightInput.perceptualRoughness = lerp(1 - _Smoothness, 0.55, fall.aeration);
    lightInput.foamArea = 0;
    lightInput.foam = fall.foam;
    lightInput.gloss = 1 - fall.aeration * 0.85;
    lightInput.planar = 0;
    half3 clear = WaterLight(lightInput, light, sun);
    // Aerated water scatters the light back like thin foam: pale, milky and soft.
    half3 aerated = _FallAeratedColor.rgb * (ambient * 0.9 + sun * (0.35 + 0.25 * saturate(nl))) *
        lerp(0.85, 1.05, fall.mask);
    clear = lerp(clear, aerated, fall.aeration);

    half3 color = lerp(clear, foamLit, fall.foam);
    color = lerp(color, foamLit, fall.runOut);
    // The shell adds only white water. Its clear water refracted the scene, so the thin edges and folds of the
    // shell lumps showed as glassy slivers.
    return lerp(color, foamLit, input.extra.w);
}

// One surface from the weld to the plunge: the body look of the water that feeds the fall, and over it the fall
// look, which comes in past the brink. At the weld line the sheet draws exactly the body.
half4 FallFragment(Varyings input)
{
    FallSurface fall = EvaluateFall(input);
    #if defined(_WATER_FALL_DEPTH)
    // Depth of the solid parts of the sheet, before its color pass: the white water of the drop and the body look
    // before the brink, which writes depth like the body itself, so effects behind it stay hidden on both sides of
    // the weld line.
    clip(_FallDepthEnd - input.sheet.z);
    half bodyAlpha = input.extra.w < 0.5
        ? saturate((fall.sceneEye - fall.surfaceEye) / max(_EdgeFade, 1e-3)) * saturate(input.flow.z) *
            (1 - fall.clearShow)
        : 0;
    clip(fall.alpha + bodyAlpha * (1 - fall.alpha) - 0.5);
    return 0;
    #else
    half bodyAlpha = 0;
    half3 bodyColor = 0;
    [branch] if (fall.clearShow < 0.999 && input.extra.w < 0.5)
    {
        BodyInput body;
        body.positionWS = input.positionWS;
        body.positionCS = input.positionCS;
        body.grid = input.surface.xy;
        body.flow = input.flow.xy;
        body.flowAlpha = input.flow.z;
        body.surfaceHeight = input.flow.w;
        body.regime = input.regime;
        body.vertexShoreHeight = 0;
        body.lipDistance = -input.fallFlow.z * FallFrameScale();
        body.up = normalize(input.normalWS);
        bool cut;
        bodyColor = ShadeBody(body, bodyAlpha, cut);
        bodyAlpha = cut ? 0 : bodyAlpha * (1 - fall.clearShow);
    }
    half3 fallColor = ShadeFall(input, fall);
    half alpha = fall.alpha + bodyAlpha * (1 - fall.alpha);
    half3 color = (fallColor * fall.alpha + bodyColor * (bodyAlpha * (1 - fall.alpha))) / max(alpha, 1e-4);
    color = MixFog(color, input.surface.w);
    return half4(color, alpha);
    #endif
}

// Depth of the solid parts of the drop: the jet and the lumps of its foam shell, where the white water is
// mostly opaque. Effects behind them stay hidden. Depth Push Back moves the depth along the view ray. Only the
// alpha of the white water is used, so the compiler drops the light.
float FallDepthFragment(Varyings input) : SV_Depth
{
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
    clip(_FallDepthEnd - input.sheet.z);
    clip(EvaluateFall(input).alpha - 0.5);
    float3 fromCamera = input.positionWS - GetCameraPositionWS();
    float distance = length(fromCamera);
    // A pull toward the camera stops half a meter in front of it.
    float push = max(_FallDepthPushBack, 0.5 - distance);
    float4 positionCS = TransformWorldToHClip(input.positionWS + fromCamera / max(distance, 1e-4) * push);
    return positionCS.z / positionCS.w;
}
#else
// Only fall materials draw the soft depth.
float FallDepthFragment(Varyings input) : SV_Depth
{
    clip(-1);
    return 0;
}
#endif

// Depth of the water surface where it hides what is behind it, for the transparent objects after the water
// (LoogaWaterReflectionFeature, Surface Depth). The clips are the same as in the surface pass.
half4 WaterDepthFragment(Varyings input) : SV_Target
{
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
    #if defined(_WATER_FALL)
    FallSurface fall = EvaluateFall(input);
    half bodyAlpha = input.extra.w < 0.5
        ? saturate((fall.sceneEye - fall.surfaceEye) / max(_EdgeFade, 1e-3)) * saturate(input.flow.z) *
            (1 - fall.clearShow)
        : 0;
    clip(fall.alpha + bodyAlpha * (1 - fall.alpha) - 0.5);
    #else
    float2 weld = WaterWeld(input.surface.xy);
    #if defined(_OCEAN_WAVES)
    OceanGridClip(input.surface.xy, input.positionCS, input.positionWS.y, weld);
    #endif
    clip(weld.y > 0.5 && weld.x > 0 ? -1 : 1);
    float2 screenUV = GetNormalizedScreenSpaceUV(input.positionCS);
    float thickness = LinearEyeDepth(WaterSceneDepth(screenUV), _ZBufferParams) -
        LinearEyeDepth(input.positionWS, GetWorldToViewMatrix());
    clip(saturate(thickness / max(_EdgeFade, 1e-3)) * saturate(input.flow.z) - 0.5);
    #endif
    return 0;
}

// The planar reflection camera does not show water: its opaque and depth textures are not for water. A vertex
// outside the clip volume removes the triangle before the waves cost anything.
Varyings WaterVertex(Attributes input)
{
    Varyings output;
    [branch] if (_LoogaWaterPlanarPass > 0.5)
    {
        output = (Varyings)0;
        output.positionCS = float4(2, 2, 2, 1);
    }
    else
    {
        #if defined(_WATER_FALL)
        output = FallVertex(input);
        #else
        output = BodyVertex(input);
        #endif
    }
    return output;
}

half4 WaterFragment(Varyings input) : SV_Target
{
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
    #if defined(_WATER_FALL)
    return FallFragment(input);
    #else
    // The body ends exactly on the weld line of a fall sheet.
    float2 weld = WaterWeld(input.surface.xy);
    #if defined(_OCEAN_WAVES)
    OceanGridClip(input.surface.xy, input.positionCS, input.positionWS.y, weld);
    #endif
    clip(weld.y > 0.5 && weld.x > 0 ? -1 : 1);
    BodyInput body;
    body.positionWS = input.positionWS;
    body.positionCS = input.positionCS;
    body.grid = input.surface.xy;
    body.flow = input.flow.xy;
    body.flowAlpha = input.flow.z;
    body.surfaceHeight = input.flow.w;
    body.regime = input.regime;
    body.vertexShoreHeight = input.surface.z;
    body.lipDistance = WeldLipDistance(weld);
    body.up = half3(0, 1, 0);
    half alpha;
    bool cut;
    half3 color = ShadeBody(body, alpha, cut);
    clip(cut ? -1 : 1);
    color = MixFog(color, input.surface.w);
    return half4(color, alpha);
    #endif
}

#endif
