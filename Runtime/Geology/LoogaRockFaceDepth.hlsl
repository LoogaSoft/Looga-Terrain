#ifndef LOOGA_ROCK_FACE_DEPTH_INCLUDED
#define LOOGA_ROCK_FACE_DEPTH_INCLUDED

// Shadow, depth and depth-normal passes of the Looga rock face shaders.
struct RockDepthAttributes
{
    float4 positionOS : POSITION;
    // w is the show value (RockClipShow).
    float4 normalOS : NORMAL;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct RockDepthVaryings
{
    float4 positionCS : SV_POSITION;
    half3 normalWS : TEXCOORD0;
    float3 positionWS : TEXCOORD1;
    half show : TEXCOORD2;
};

RockDepthVaryings DepthVertex(RockDepthAttributes input)
{
    UNITY_SETUP_INSTANCE_ID(input);
    RockDepthVaryings output;
    output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
    output.positionCS = TransformWorldToHClip(output.positionWS);
    output.normalWS = TransformObjectToWorldNormal(input.normalOS.xyz);
    output.show = input.normalOS.w;
    return output;
}

half DepthFragment(RockDepthVaryings input) : SV_Target
{
    RockClipShow(input.positionWS, input.show);
    return 0;
}

RockDepthVaryings DepthNormalsVertex(RockDepthAttributes input)
{
    return DepthVertex(input);
}

#if defined(LOOGA_ROCK_SHADOW_PASS)

// Set by URP for each shadow-casting light (ShadowUtils.SetupShadowCasterConstantBuffer).
float3 _LightDirection;
float3 _LightPosition;

// The shadow caster vertex, as ApplyShadowBias of URP, but a surface turned away from the light gets no normal inset.
// NdotL already darkens such a surface. The inset moved a single rock sheet, such as a cliff wall, into the ground behind
// it, and at a low sun the ground next to the wall top then got a shadow.
RockDepthVaryings ShadowVertex(RockDepthAttributes input)
{
    UNITY_SETUP_INSTANCE_ID(input);
    RockDepthVaryings output;
    float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
    float3 normalWS = TransformObjectToWorldNormal(input.normalOS.xyz);
    #if defined(_CASTING_PUNCTUAL_LIGHT_SHADOW)
        float3 lightDirectionWS = normalize(_LightPosition - positionWS);
    #else
        float3 lightDirectionWS = _LightDirection;
    #endif
    float facing = dot(lightDirectionWS, normalWS);
    float inset = facing > 0.0 ? (1.0 - facing) * _ShadowBias.y : 0.0;
    float3 biased = positionWS + lightDirectionWS * _ShadowBias.x + normalWS * inset;
    // The vertices of a generated rock split on hard edges. Copies with other normals move apart with a normal inset, and
    // light comes through the seams. A move along the light keeps the copies together.
    if (_RockMasks > 0.5)
    {
        biased = positionWS + lightDirectionWS * (_ShadowBias.x + 0.5 * _ShadowBias.y);
    }
    output.positionCS = ApplyShadowClamping(TransformWorldToHClip(biased));
    output.positionWS = positionWS;
    output.normalWS = normalWS;
    output.show = input.normalOS.w;
    return output;
}

half4 ShadowFragment(RockDepthVaryings input) : SV_Target
{
    RockClipShow(input.positionWS, input.show);
    return 0;
}

#endif

void DepthNormalsFragment(RockDepthVaryings input, out half4 outNormalWS : SV_Target0
#ifdef _WRITE_RENDERING_LAYERS
    , out uint outRenderingLayers : SV_Target1
#endif
)
{
    RockClipShow(input.positionWS, input.show);
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
