#ifndef LOOGA_VEGETATION_INPUT_INCLUDED
#define LOOGA_VEGETATION_INPUT_INCLUDED
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/CommonMaterial.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/SurfaceInput.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DBuffer.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/DebugMipmapStreamingMacros.hlsl"

CBUFFER_START(UnityPerMaterial)
float4 _BaseMap_ST;
float4 _BaseMap_TexelSize;
half4 _BaseColor;
half _Cutoff;
half _Smoothness;
half _BumpScale;
half _Surface;
float4 _WindParams;
float4 _WindDirection;
float _WindTime;
float4 _BranchWind;
float4 _InteractionParams;
float4 _GroundParams;
float4 _LeafWind;
float _VertexWindMask;
float _Billboard;
float4 _LoogaLightmapColor;
float4 _LoogaInstanceData;
float4 _GustSheen;
UNITY_TEXTURE_STREAMING_DEBUG_VARS;
CBUFFER_END

#ifdef UNITY_DOTS_INSTANCING_ENABLED
UNITY_DOTS_INSTANCING_START(MaterialPropertyMetadata)
    UNITY_DOTS_INSTANCED_PROP(float4, _BaseColor)
    UNITY_DOTS_INSTANCED_PROP(float4, _LoogaLightmapColor)
    UNITY_DOTS_INSTANCED_PROP(float4, _LoogaInstanceData)
UNITY_DOTS_INSTANCING_END(MaterialPropertyMetadata)
#define _BaseColor UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float4, _BaseColor)
#define _LoogaLightmapColor UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float4, _LoogaLightmapColor)
#define _LoogaInstanceData UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float4, _LoogaInstanceData)
#endif

struct LoogaVegetationAttributes
{
    float4 positionOS : POSITION;
    float3 normalOS : NORMAL;
    float4 tangentOS : TANGENT;
    float2 texcoord : TEXCOORD0;
    float2 staticLightmapUV : TEXCOORD1;
    float2 dynamicLightmapUV : TEXCOORD2;
    float4 color : COLOR;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

void InitializeStandardLitSurfaceData(float2 uv, out SurfaceData surface)
{
    surface = (SurfaceData)0;
    half4 sample = SampleAlbedoAlpha(uv, TEXTURE2D_ARGS(_BaseMap, sampler_BaseMap));
    surface.alpha = Alpha(sample.a, _BaseColor, _Cutoff);
    surface.albedo = sample.rgb * _BaseColor.rgb;
    surface.smoothness = _Smoothness;
    surface.normalTS = SampleNormal(uv, TEXTURE2D_ARGS(_BumpMap, sampler_BumpMap), _BumpScale);
    surface.occlusion = 1;
}

float3 VegetationOrientToView(float3 value, float4x4 inverseModel, float4 view)
{
    if (_Billboard < 0.5) return value;
    float3 local = mul(inverseModel, view).xyz;
    float2 direction = local.xz / max(length(local.xz), 0.001);
    return float3(value.x * direction.y + value.z * direction.x, value.y, value.z * direction.y - value.x * direction.x);
}

float3 VegetationOrient(float3 value, float4x4 inverseModel)
{
    float4 view = unity_OrthoParams.w > 0 ? float4(UNITY_MATRIX_V[2].xyz, 0) : float4(GetCameraPositionWS(), 1);
    return VegetationOrientToView(value, inverseModel, view);
}

#include "Packages/com.loogasoft.loogainstancing/ShaderLibrary/VegetationDeformation.hlsl"
#endif
