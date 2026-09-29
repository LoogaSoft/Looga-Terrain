#ifndef LOOGA_VEGETATION_MOTION_INCLUDED
#define LOOGA_VEGETATION_MOTION_INCLUDED
#include "VegetationInput.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/MotionVectorsCommon.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/LODCrossFade.hlsl"
struct VegetationMotionVaryings
{
    float4 positionCS : SV_POSITION;
    float4 currentCS : TEXCOORD0;
    float4 previousCS : TEXCOORD1;
    float2 uv : TEXCOORD2;
    UNITY_VERTEX_INPUT_INSTANCE_ID
    UNITY_VERTEX_OUTPUT_STEREO
};
// Intersect the previous projection planes to recover the previous perspective camera position.
float4 VegetationPreviousView()
{
    float4 x = _PrevViewProjMatrix[0];
    float4 y = _PrevViewProjMatrix[1];
    float4 w = _PrevViewProjMatrix[3];
    if (dot(w.xyz, w.xyz) < 0.000001)
    {
        return float4(normalize(_PrevViewProjMatrix[2].xyz) * sign(UNITY_MATRIX_P[2][2]), 0);
    }
    float3 yz = cross(y.xyz, w.xyz);
    float determinant = dot(x.xyz, yz);
    if (abs(determinant) < 0.000001) return float4(GetCameraPositionWS(), 1);
    float3 position = -(x.w * yz + y.w * cross(w.xyz, x.xyz) + w.w * cross(x.xyz, y.xyz)) / determinant;
    return float4(position, 1);
}

VegetationMotionVaryings VegetationMotionVertex(LoogaVegetationAttributes input)
{
    VegetationMotionVaryings output = (VegetationMotionVaryings)0;
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_TRANSFER_INSTANCE_ID(input, output);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
    float3 current = VegetationOrient(input.positionOS.xyz, UNITY_MATRIX_I_M);
    float3 previous = _Billboard > 0.5 ? VegetationOrientToView(input.positionOS.xyz, UNITY_PREV_MATRIX_I_M, VegetationPreviousView()) : input.positionOS.xyz;
    float time = _WindTime >= 0 ? _WindTime : _Time.y;
    float oldTime = _WindTime >= 0 ? _WindTime : _LastTimeParameters.x;
    VegetationBendAtTime(current, input.color, time, mul(UNITY_MATRIX_M, float4(0,0,0,1)).xyz);
    VegetationBendAtTime(previous, input.color, oldTime, mul(UNITY_PREV_MATRIX_M, float4(0,0,0,1)).xyz, true);
    float4 world = mul(UNITY_MATRIX_M, float4(current, 1));
    output.positionCS = mul(UNITY_MATRIX_VP, world);
    output.currentCS = mul(_NonJitteredViewProjMatrix, world);
    output.previousCS = mul(_PrevViewProjMatrix, mul(UNITY_PREV_MATRIX_M, float4(previous, 1)));
    output.uv = TRANSFORM_TEX(input.texcoord, _BaseMap);
    return output;
}
half4 VegetationMotionFragment(VegetationMotionVaryings input) : SV_Target
{
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
    Alpha(SampleAlbedoAlpha(input.uv, TEXTURE2D_ARGS(_BaseMap, sampler_BaseMap)).a, _BaseColor, _Cutoff);
#if defined(LOD_FADE_CROSSFADE)
    LODFadeCrossFade(input.positionCS);
#endif
    return half4(CalcNdcMotionVectorFromCsPositions(input.currentCS, input.previousCS), 0, 0);
}
#endif
