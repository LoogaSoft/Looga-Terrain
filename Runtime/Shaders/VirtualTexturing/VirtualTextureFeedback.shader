Shader "Hidden/Looga/Terrain/VirtualTextureFeedback"
{
    Properties { [HideInInspector] _LoogaVTZTest("Depth test", Float) = 4 }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" }
        Pass
        {
            Cull Off ZWrite On ZTest [_LoogaVTZTest]
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vertex
            #pragma fragment Fragment
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #define UNITY_INDIRECT_DRAW_ARGS IndirectDrawIndexedArgs
            #include "UnityIndirect.cginc"
            #include "../TerrainGeometry.hlsl"
            #include "VirtualTextureSampling.hlsl"
            float4x4 _LoogaVTViewProjection;
            uint _LoogaVTTile;
            float _LoogaVTDerivativeScale;
            struct Varyings { float4 position : SV_POSITION; float2 uv : TEXCOORD0; };
            Varyings Vertex(float3 position : POSITION, uint instance : SV_InstanceID)
            {
                InitIndirectDrawArgs(0);
                Varyings output;
                output.uv = LoogaPatchUV(position.xz, GetIndirectInstanceID(instance));
                output.position = mul(_LoogaVTViewProjection, float4(LoogaPosition(output.uv), 1));
                return output;
            }
            float2 Fragment(Varyings input) : SV_Target
            {
                LoogaClipHoles(input.uv);
                if (!LoogaVTCanCache(input.uv, _LoogaVTDerivativeScale)) return 0;
                uint mip = (uint)floor(LoogaVTMip(input.uv, _LoogaVTDerivativeScale));
                uint width = (uint)_LoogaVTInfo.y >> mip;
                uint2 page = (uint2)(clamp(input.uv, 0, 0.99999994) * width);
                return float2(LoogaVTPageIndex(page, mip) + 1, _LoogaVTTile);
            }
            ENDHLSL
        }
    }
}
