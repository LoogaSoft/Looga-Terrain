#ifndef LOOGA_ROCK_FACE_INPUT_INCLUDED
#define LOOGA_ROCK_FACE_INPUT_INCLUDED

// Material inputs of the Looga rock face shaders. Every pass includes this file, so the per-material buffer is the
// same in all passes.
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "Packages/com.loogasoft.loogaterrain/Runtime/Shaders/Surface/LoogaTerrainSurfaceLayers.hlsl"

TEXTURE2D(_BaseMap);
SAMPLER(sampler_BaseMap);
TEXTURE2D(_NormalMap);
SAMPLER(sampler_NormalMap);
TEXTURE2D(_MaskMap);
SAMPLER(sampler_MaskMap);
// Moss texture set (RockMoss): albedo with smoothness in A, normal, mask (G occlusion, B height, A smoothness).
TEXTURE2D(_MossMap);
SAMPLER(sampler_MossMap);
TEXTURE2D(_MossNormalMap);
TEXTURE2D(_MossMaskMap);
// Stain map of a generated rock (RockStain): R rust, G olive, B dark streaks, A mottle.
TEXTURE2D(_StainMap);
// Line map of a generated rock (RockLines): R pale veins, G dark cracks, B halo.
TEXTURE2D(_LineMap);
// Fracture volume of a generated rock (RockFractureSurface): R signed distance, G distance, B block tone, A run below a ledge.
TEXTURE3D(_FractureVolume);
SAMPLER(sampler_FractureVolume);
// Tangent normals of the steps along the cracks of the line map.
TEXTURE2D(_LineStepMap);
// Baked relief of the four upright sides (RockFaceDetail): RG slope, B dark cracks, A pale veins.
TEXTURE2D_ARRAY(_FaceDetail);
SAMPLER(sampler_FaceDetail);
// Two more normal layers of a generated rock (RockDetailLayer): large surface fractures, and fine grit.
TEXTURE2D(_FractureNormalMap);
TEXTURE2D(_GritNormalMap);
// Tiled rock layer of the asset texture mode: albedo, normal, mask (G occlusion, A smoothness), and the area of the
// layer in A at the asset UV.
TEXTURE2D(_RockDetailMap);
SAMPLER(sampler_RockDetailMap);
TEXTURE2D(_RockDetailNormalMap);
TEXTURE2D(_RockDetailMaskMap);
TEXTURE2D(_RockDetailAreaMap);

#include "Packages/com.loogasoft.loogaterrain/Runtime/Shaders/Surface/LoogaTerrainGroundInput.hlsl"
#include "Packages/com.loogasoft.loogaterrain/Runtime/Geology/LoogaCaveEntranceClip.hlsl"

CBUFFER_START(UnityPerMaterial)
half4 _BaseColor;
float _Tiling;
half _NormalStrength;
// Exponent of the triplanar weights. High values keep the blend in a narrow band on an edge, so flat faces stay crisp.
half _TriplanarSharpness;
// Scale of the normal map on surfaces that face up (the top projection), for example cave floors. Cliff textures show
// ledges, and the top projection laid them on the floors as steps.
half _TopNormalStrength;
half _SmoothnessMin;
half _SmoothnessMax;
half _OcclusionStrength;
float _MacroScale;
float _MacroBlendScale;
half _HeightBlend;
half _FacetShading;
float _FacetFade;
half _FractalStrength;
float _FractalScale;
float _FractalStretch;
float _FractalFade;
half4 _BandColorA;
half4 _BandColorB;
half _BandStrength;
half _HardBrightness;
float _PatchSize;
half _PatchStrength;
half _CavityStrength;
// 1: the layer value and the hardness come from the world position (RockWorldStrata), not from the vertex colors, so
// all rock meshes show the same layer at the same point.
half _WorldStrata;
float _StrataThickness;
float _StrataWarp;
half _StrataHardShare;
float _StrataContinuity;
half4 _DustColor;
half _DustAmount;
half _DustStart;
// Mineral stains (RockMineralStains). 0 strength turns them off.
half _StainStrength;
half4 _StainColorA;
half4 _StainColorB;
half4 _StainColorC;
half4 _StainColorD;
float _StainScale;
float _StainStretch;
half _StainContrast;
// Share of the stain on faces that look up: 0 = steep faces only.
half _StainFlat;
half _StreakStrength;
// Moss (RockMoss). 0 amount turns it off.
half _MossAmount;
half4 _MossColorA;
half4 _MossColorB;
half _MossStart;
half _MossCavity;
half _MossDrips;
float _MossScale;
// 1: the moss shows its texture set, with a height blend against the rock. 0: flat moss colors.
half _MossTextured;
float _MossTiling;
half4 _MossTint;
half _MossHeightBlend;
half _MossEdge;
half _MossNormalStrength;
half _MossCushion;
half _MossSheen;
half _MossSunAvoid;
// Weathering from the masks of a generated rock (RockGeneratedWeathering). 1 in _RockMasks turns it on. Other meshes have
// no masks, so they keep 0.
half _RockMasks;
half _EdgeWear;
half4 _EdgeColor;
half _EdgeBreakup;
float _EdgeBreakupSize;
half _EdgeSoftness;
float _EdgeRound;
half _MacroAmount;
half _EdgeChip;
float _EdgeChipSize;
half _FractureStrength;
float _FractureSize;
half _FractureDarken;
half _GritStrength;
float _GritSize;
half _EdgeFlecks;
half4 _EdgeFleckColor;
float _LineSize;
half _LineStep;
half _LineVein;
half4 _LineVeinColor;
half _LineCrack;
half _LineHalo;
half4 _LineHaloColor;
half _FarGrain;
half _FarGrainClump;
// Set on the renderer. Scale w is 1 on a rock with a volume.
float4 _FractureVolumeMin;
float4 _FractureVolumeScale;
float4 _FractureVolumeRange;
// Set on the renderer. Center w is 1 on a rock with face detail.
float4 _FaceDetailCenter;
float4 _FaceDetailHalf;
float _FaceDetailSlope;
half _FaceDetailCrack;
half _FaceDetailVein;
half4 _FaceDetailVeinColor;
half _FractureVein;
half _FractureVeinShare;
half _FractureCluster;
float _FractureWander;
half4 _FractureVeinColor;
half _FractureCrack;
half _FractureRust;
half4 _FractureRustColor;
half _BlockTone;
half _CrackLineWidth;
half _CrackLineDarken;
half _LedgeStreak;
float _LedgeStreakLength;
half _FarGrainFlecks;
float _FarGrainStretch;
half _PlaneValue;
float _PlaneFrequency;
float _FarGrainSize;
float _FarGrainStart;
float _FarGrainEnd;
half _RidgeLight;
half _CreviceDarken;
half4 _CreviceColor;
half _ShelterDarken;
half _ShelterCurve;
half _CreviceRough;
half _DepositAmount;
half4 _DepositColor;
float _DepositSize;
half _StainAmount;
float _StainSize;
half4 _StainRust;
half4 _StainOlive;
half4 _StainStreak;
half _StainMottle;
half _StainHue;
half4 _GroundColor;
float4 _GroundTriplanarScale;
half _GroundTriplanarContrast;
half _GroundNormalStrength;
half _GroundContrast;
half _GroundHeightBlend;
half _GroundDustReach;
half _GroundDustAmount;
float _GroundLayout;
float4 _GroundSurfaceUV;     // Looga layouts: world UV scale and offset of the terrain surface
half4 _GroundLuminance;      // Looga layouts: luminance weights of the saturation adjustment
// Looga layouts: world colour and ground cover of the terrain surface (LoogaTerrainSurfaceCover.hlsl). The rock face
// sets them for each renderer. 0 turns them off.
float4 _LoogaWorldColor;
float4 _LoogaWorldColor2;
float4 _LoogaGroundCover;
float4 _LoogaGroundCoverShade;
float4 _LoogaGroundCoverSoil;
float4 _LoogaGroundCoverView;
float4 _LoogaGroundCoverDense;
float4 _LoogaGroundCoverDetail;
// 1 on LoogaCaveSystem meshes, which carry the cave shade in UV channel 3. Other rock meshes have no channel 3, so
// they keep 0.
half _LoogaCaveShade;
// Asset texture mode (_ROCK_UV): xyz the terrain point beside the asset whose ground it shows, w 1 when set.
float4 _RockGroundCenter;
// Asset texture mode: the ground also covers faces whose normal Y is above this value. 0 = off.
half _GroundUpStart;
// Asset texture mode: the tiled rock layer. Both strengths 0 = off. UV: scale of the asset UV in xy, offset in zw.
half4 _RockDetailColor;
float4 _RockDetailUV;
half _RockDetailStrength;
half _RockDetailNormalStrength;
// Asset texture mode: moss and soil tint of the rock around the ground. Strength 0 = off. The mip of the mask sets the
// width.
half4 _GroundFringeColor;
half _GroundFringe;
half _GroundFringeMip;
CBUFFER_END

// Smooth value noise in [0, 1] for RockClipShow.
float RockShowNoise(float3 p)
{
    float3 i = floor(p);
    float3 f = frac(p);
    f = f * f * (3.0 - 2.0 * f);
    float corners[8];
    [unroll]
    for (int c = 0; c < 8; c++)
    {
        float3 h = frac((i + float3(c & 1, (c >> 1) & 1, (c >> 2) & 1)) * 0.1031);
        h += dot(h, h.yzx + 33.33);
        corners[c] = frac((h.x + h.y) * h.z);
    }
    float x0 = lerp(lerp(corners[0], corners[1], f.x), lerp(corners[2], corners[3], f.x), f.y);
    float x1 = lerp(lerp(corners[4], corners[5], f.x), lerp(corners[6], corners[7], f.x), f.y);
    return lerp(x0, x1, f.z);
}

// Cuts away the pixels of a surface whose show value is below 1, along a ragged world-space noise edge. All passes use
// it, so the depth and the color agree. The noise is fixed in the world, so the edge does not move with the camera.
// The surface also ends in cave entrance boxes. A show value of 2 marks the mesh of an entrance, which stays.
void RockClipShow(float3 positionWS, half show)
{
    if (show < 1.5h) ClipLoogaCaveEntrances(positionWS);
    [branch]
    if (show < 0.999h)
    {
        float noise = RockShowNoise(positionWS * 1.3) * 0.65 + RockShowNoise(positionWS * 4.1 + 17.3) * 0.35;
        clip(show - noise);
    }
}

#endif
