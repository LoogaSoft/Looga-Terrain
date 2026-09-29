Shader "Hidden/Looga/Terrain/OcclusionDepth"
{
    Properties { [HideInInspector] _DepthTest("Depth test", Float) = 4 }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" }
        Pass
        {
            Cull Front ZWrite On ZTest [_DepthTest]
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vertex
            #pragma fragment Fragment
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #define UNITY_INDIRECT_DRAW_ARGS IndirectDrawIndexedArgs
            #include "UnityIndirect.cginc"
            #include "TerrainGeometry.hlsl"
            float4x4 _DepthViewProjection;
            struct Varyings { float4 position : SV_POSITION; float2 uv : TEXCOORD0; };
            Varyings Vertex(float3 position : POSITION, uint instance : SV_InstanceID)
            {
                InitIndirectDrawArgs(0);
                Varyings output;
                output.uv = LoogaPatchUV(position.xz, GetIndirectInstanceID(instance));
                output.position = mul(_DepthViewProjection, float4(LoogaPosition(output.uv), 1));
                return output;
            }
            float Fragment(Varyings input) : SV_Target
            {
                LoogaClipHoles(input.uv);
                return input.position.z;
            }
            ENDHLSL
        }
    }
}
