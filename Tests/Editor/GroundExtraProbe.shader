Shader "Hidden/Looga/Ground Extra Probe"
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
            #include "Packages/com.loogasoft.loogainstancing/ShaderLibrary/VegetationGroundData.hlsl"
            float4 _ProbeWorld;
            int _ProbeChannel;
            float4 Vertex(uint id : SV_VertexID) : SV_POSITION
            {
                return GetFullScreenTriangleVertexPosition(id);
            }
            float4 Fragment() : SV_Target
            {
                float4 map, data;
                if (!VegetationGroundExtra(_ProbeWorld.xy, map, data)) return float4(1,0,1,1);
                return _ProbeChannel == 0 ? map : data;
            }
            ENDHLSL
        }
    }
}
