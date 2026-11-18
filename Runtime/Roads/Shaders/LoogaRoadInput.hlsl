#ifndef LOOGA_ROAD_INPUT_INCLUDED
#define LOOGA_ROAD_INPUT_INCLUDED

// Material inputs of the Looga road shader. Every pass includes this file, so the per-material buffer is the same in
// all passes.
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

TEXTURE2D(_BaseMap);
SAMPLER(sampler_BaseMap);
TEXTURE2D(_NormalMap);
// The normal map is in every pass, so its sampler also serves the textures that the depth normal pass reads.
SAMPLER(sampler_NormalMap);
TEXTURE2D(_MaskMap);
TEXTURE2D(_NoiseMap);
SAMPLER(sampler_NoiseMap);
TEXTURE2D(_WearNormalA);
SAMPLER(sampler_WearNormalA);
TEXTURE2D(_WearNormalB);
SAMPLER(sampler_WearNormalB);
TEXTURE2D(_PatchMap);
TEXTURE2D(_PatchNormal);
TEXTURE2D(_PatchMask);

#include "Packages/com.loogasoft.loogaterrain/Runtime/Shaders/Surface/LoogaTerrainGroundInput.hlsl"

CBUFFER_START(UnityPerMaterial)
half4 _BaseColor;
float _TextureSize;
half _NormalStrength;
half _SmoothnessOffset;
half _Metallic;
float _CellSize;
half _CellBlend;
float _NoiseSize;
half4 _WearTintA;
float _WearSizeA;
half _WearAmountA;
half _WearContrastA;
half _WearNormalStrengthA;
half _WearSmoothnessA;
half _WearOcclusionA;
half4 _WearTintB;
float _WearSizeB;
half _WearAmountB;
half _WearContrastB;
half _WearNormalStrengthB;
half _WearSmoothnessB;
half _WearOcclusionB;
float _PatchSize;
float _PatchSpread;
half _PatchAmount;
half _MacroVariation;
half _Age;
half4 _AgeColor;
float _TrackOffset;
float _TrackWidth;
half _TrackDarken;
half _TrackSmoothness;
float _OilWidth;
half _OilDarken;
float _EdgeWidth;
float _EdgeWidthMin;
float _EdgeChunkSize;
half _CrackDistance;
half4 _EdgeColor;
float _CrackWidth;
float _CrackDepth;
half _CrackDarken;
float _AlligatorSize;
half _AlligatorStart;
float _SealWidth;
half4 _SealColor;
half _SealSmoothness;
float _RepairSpacing;
half _RepairChance;
half _RimBrighten;
float _BlockSize;
half _BlockStart;
float _AlligatorTilt;
// Terrain ground of the edge band (LoogaTerrainGround.hlsl).
half4 _GroundColor;
float _GroundReach;
float4 _GroundTriplanarScale;
half _GroundTriplanarContrast;
half _GroundNormalStrength;
float _GroundLayout;
float4 _GroundSurfaceUV;
half4 _GroundLuminance;
float _LipDepthOffset;
CBUFFER_END

// The rounded lip of a slab ends just above the terrain (LoogaRoadSlabs). It moves toward the camera by a part of the
// view distance that grows to its outer end, so the terrain does not show through it where the depth precision and
// the terrain level of detail fall with distance. The move is along the view ray, so the lip keeps its place on the
// screen. Lip is 0 at the rim and 1 on the ground (green vertex color). Every pass that writes depth uses it.
float3 RoadLipPosition(float3 positionWS, half lip)
{
    return positionWS + (GetCameraPositionWS() - positionWS) * (lip * _LipDepthOffset);
}

#endif
