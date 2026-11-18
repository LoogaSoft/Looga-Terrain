#ifndef LOOGA_ROAD_GBUFFER_INCLUDED
#define LOOGA_ROAD_GBUFFER_INCLUDED

// GBuffer pass of the Looga road shader. It writes a Looga Lite material, so Looga Lighting lights the road. A project
// shader can define LOOGA_TERRAIN_GROUND_HOOK before it includes this file.
#include "Packages/com.loogasoft.loogaterrain/Runtime/Roads/Shaders/LoogaRoadSurface.hlsl"
#define LOOGA_DISABLE_MODEL_REFLECTIONS 1
#define LOOGA_LITE_MATERIAL 1
#include "Packages/com.loogasoft.loogagraphicspro/Includes/LoogaLightingHelpers.hlsl"

LoogaGBufferOutput RoadGBufferFragment(Varyings input)
{
    UNITY_SETUP_INSTANCE_ID(input);
    half3 albedo;
    half3 normalWS;
    half smoothness;
    half occlusion;
    half metallic;
    RoadSurface(input, albedo, normalWS, smoothness, occlusion, metallic);

    half3 specularF0 = kDielectricSpec.rgb;
    ApplyLoogaDBuffer(input.positionCS, albedo, normalWS, metallic, specularF0, occlusion, smoothness);

    // GBuffer3 gets the diffuse GI only. Looga Lighting adds the indirect specular of marked materials.
    half3 bakedGI;
    half4 shadowMask;
    LOOGA_SAMPLE_BAKED_LIGHTING(input, normalWS, 1.0h - smoothness, GetDefaultLoogaModelParameters(), bakedGI,
        shadowMask);
    half3 diffuseColor = GetLoogaDiffuseColor(albedo, metallic, specularF0);

    LoogaGBufferOutput output;
    output.GBuffer0 = half4(albedo, PackLoogaMaterialFlags(GetLoogaCommonMaterialFlags()));
    output.GBuffer1 = half4(PackLoogaGBufferSpecular(metallic, specularF0), occlusion);
    output.GBuffer2 = half4(PackGBufferNormal(normalWS), smoothness);
    output.GBuffer3 = half4(EvaluateLoogaGBufferAmbient(diffuseColor, bakedGI, occlusion), 0.0h);
    FillLoogaGBufferExtraOutputs(output, input.positionCS.z, shadowMask);
    return output;
}

#endif
