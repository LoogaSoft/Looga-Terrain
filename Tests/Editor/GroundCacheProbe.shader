Shader "Hidden/Looga/Ground Cache Probe"
{
    SubShader
    {
        Pass
        {
            ZWrite Off ZTest Always Cull Off
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vertex
            #pragma fragment Fragment
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #define LOOGA_GROUND_CACHE
            #include "Packages/com.loogasoft.loogainstancing/ShaderLibrary/VegetationGroundCache.hlsl"
            float4 _ProbeWorld;
            int _ProbeChannel;
            float4 Vertex(uint id : SV_VertexID) : SV_POSITION
            {
                return GetFullScreenTriangleVertexPosition(id);
            }
            float4 Fragment() : SV_Target
            {
                float4 color, normal, emission;
                bool valid = VegetationGroundCache(_ProbeWorld.xy,color,normal,emission);
                if (!valid) return float4(1,0,1,1);
                return _ProbeChannel == 1 ? normal : _ProbeChannel == 2 ? emission : color;
            }
            ENDHLSL
        }
    }
}
