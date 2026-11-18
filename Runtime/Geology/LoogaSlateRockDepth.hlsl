#ifndef LOOGA_SLATE_ROCK_DEPTH_INCLUDED
#define LOOGA_SLATE_ROCK_DEPTH_INCLUDED

// Shadow, depth and depth-normal passes of the slate rock shader.
struct SlateDepthAttributes
{
    float4 positionOS : POSITION;
    float4 normalOS : NORMAL;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct SlateDepthVaryings
{
    float4 positionCS : SV_POSITION;
    half3 normalWS : TEXCOORD0;
};

SlateDepthVaryings DepthVertex(SlateDepthAttributes input)
{
    UNITY_SETUP_INSTANCE_ID(input);
    SlateDepthVaryings output;
    output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
    output.normalWS = TransformObjectToWorldNormal(input.normalOS.xyz);
    return output;
}

half DepthFragment(SlateDepthVaryings input) : SV_Target
{
    return 0;
}

#if defined(LOOGA_SLATE_SHADOW_PASS)

// Set by URP for each shadow-casting light.
float3 _LightDirection;
float3 _LightPosition;

// The vertices of a rock split on hard edges. A move along the normal opens the seams to light, so the move is along the light.
SlateDepthVaryings ShadowVertex(SlateDepthAttributes input)
{
    UNITY_SETUP_INSTANCE_ID(input);
    SlateDepthVaryings output;
    float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
    #if defined(_CASTING_PUNCTUAL_LIGHT_SHADOW)
        float3 lightDirectionWS = normalize(_LightPosition - positionWS);
    #else
        float3 lightDirectionWS = _LightDirection;
    #endif
    float3 biased = positionWS + lightDirectionWS * (_ShadowBias.x + 0.5 * _ShadowBias.y);
    output.positionCS = ApplyShadowClamping(TransformWorldToHClip(biased));
    output.normalWS = TransformObjectToWorldNormal(input.normalOS.xyz);
    return output;
}

half4 ShadowFragment(SlateDepthVaryings input) : SV_Target
{
    return 0;
}

#endif

void DepthNormalsFragment(SlateDepthVaryings input, out half4 outNormalWS : SV_Target0
#ifdef _WRITE_RENDERING_LAYERS
    , out uint outRenderingLayers : SV_Target1
#endif
)
{
    float3 normalWS = normalize(input.normalWS);
#if defined(_GBUFFER_NORMALS_OCT)
    float2 octNormalWS = PackNormalOctQuadEncode(normalWS);
    float2 remappedOctNormalWS = saturate(octNormalWS * 0.5 + 0.5);
    outNormalWS = half4(PackFloat2To888(remappedOctNormalWS), 0.0);
#else
    outNormalWS = half4(normalWS, 0.0);
#endif
#ifdef _WRITE_RENDERING_LAYERS
    outRenderingLayers = EncodeMeshRenderingLayer();
#endif
}

#endif
