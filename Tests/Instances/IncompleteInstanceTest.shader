Shader "Hidden/Looga/Tests/Incomplete BRG"
{
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" }
        Pass
        {
            Tags { "LightMode"="UniversalForwardOnly" }
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            float4 Vert(float4 position : POSITION) : SV_POSITION { return TransformObjectToHClip(position.xyz); }
            half4 Frag() : SV_Target { return 1; }
            ENDHLSL
        }
        UsePass "Universal Render Pipeline/Unlit/MotionVectors"
    }
}
