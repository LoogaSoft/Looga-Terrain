Shader "Hidden/Looga/Water Surface Depth"
{
    // Copies the opaque depth (_LoogaOpaqueDepth) into the bound depth target. LoogaWaterReflectionFeature then draws
    // the water surfaces on top of it (pass LoogaWaterDepth of Looga/Water), so the transparent objects after the water
    // read the depth of the water surface.
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" }

        Pass
        {
            Name "CopyOpaqueDepth"
            ZWrite On
            ZTest Always
            Cull Off
            ColorMask 0

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex CopyVertex
            #pragma fragment CopyFragment

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D_X_FLOAT(_LoogaOpaqueDepth);

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
            };

            Varyings CopyVertex(uint vertexID : SV_VertexID)
            {
                Varyings output;
                output.positionCS = GetFullScreenTriangleVertexPosition(vertexID);
                return output;
            }

            float CopyFragment(Varyings input) : SV_Depth
            {
                return LOAD_TEXTURE2D_X(_LoogaOpaqueDepth, uint2(input.positionCS.xy)).r;
            }
            ENDHLSL
        }
    }
    FallBack Off
}
