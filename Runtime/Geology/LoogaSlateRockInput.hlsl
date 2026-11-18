#ifndef LOOGA_SLATE_ROCK_INPUT_INCLUDED
#define LOOGA_SLATE_ROCK_INPUT_INCLUDED

// Material inputs of the slate rock shader. Every pass includes this file, so the buffer is the same in all passes.
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

TEXTURE2D(_AlbedoMap);
SAMPLER(sampler_AlbedoMap);
TEXTURE2D(_NormalMap);
TEXTURE2D(_DetailMap);

CBUFFER_START(UnityPerMaterial)
float _TileSize;
half _NormalStrength;
half _BlendSharpness;
half _GeoBlend;
half4 _EdgeTint;
half _EdgeWearThreshold;
half _EdgeWearStrength;
half _CavityFloor;
CBUFFER_END

#endif
