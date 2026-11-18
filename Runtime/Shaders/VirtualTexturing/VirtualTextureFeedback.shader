Shader "Hidden/Looga/Terrain/VirtualTextureFeedback"
{
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" }
        Pass
        {
            // Unity reverses the depth test and the depth clear value on reversed-Z platforms.
            Cull Off ZWrite On ZTest LEqual
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
            // Camera of the feedback: xyz position, w size of one full-resolution pixel at unit depth (0 =
            // orthographic). Right, up and forward are unit world axes.
            float4 _LoogaVTCameraPosition;
            float4 _LoogaVTCameraRight;
            float4 _LoogaVTCameraUp;
            float4 _LoogaVTCameraForward;
            struct Varyings { float4 position : SV_POSITION; float2 uv : TEXCOORD0; float3 positionWS : TEXCOORD1; };
            Varyings Vertex(float3 position : POSITION, uint instance : SV_InstanceID)
            {
                InitIndirectDrawArgs(0);
                Varyings output;
                output.uv = LoogaPatchUV(position.xz, GetIndirectInstanceID(instance));
                output.positionWS = LoogaPosition(output.uv);
                output.position = mul(_LoogaVTViewProjection, float4(output.positionWS, 1));
                return output;
            }
            // Tile UV derivatives of one full-resolution screen pixel: the ray of the next pixel meets the tangent
            // plane of the terrain. The screen derivatives of the low-resolution feedback span several screen pixels
            // and overestimate the footprint toward the horizon, so they requested a coarser mip than the reads use.
            void GetPixelFootprint(float3 positionWS, float3 normalWS, out float2 uvDx, out float2 uvDy)
            {
                float3 toPoint = positionWS - _LoogaVTCameraPosition.xyz;
                float depth = max(dot(toPoint, _LoogaVTCameraForward.xyz), 1e-4);
                float3 direction = toPoint / depth;
                float facing = dot(normalWS, direction);
                facing = facing < 0 ? min(facing, -1e-3) : max(facing, 1e-3);
                float3 right = _LoogaVTCameraRight.xyz * _LoogaVTCameraPosition.w;
                float3 up = _LoogaVTCameraUp.xyz * _LoogaVTCameraPosition.w;
                float3 dx = depth * (right - direction * dot(normalWS, right) / facing);
                float3 dy = depth * (up - direction * dot(normalWS, up) / facing);
                uvDx = dx.xz / _LoogaSize.xz;
                uvDy = dy.xz / _LoogaSize.xz;
            }
            float2 Fragment(Varyings input) : SV_Target
            {
                LoogaClipHoles(input.uv);
                // The screen derivatives are read before any branch.
                float2 uvDx = ddx(input.uv) * _LoogaVTDerivativeScale;
                float2 uvDy = ddy(input.uv) * _LoogaVTDerivativeScale;
                float3 normalWS = LoogaNormal(input.uv);
                float screenMip = LoogaVTMipGrad(uvDx, uvDy);
                // The reads use the derivatives of the drawn triangles, which can differ from the smooth terrain
                // plane on coarse patches. The finer of both mips is requested.
                float footprintMip = screenMip;
                if (_LoogaVTCameraPosition.w > 0)
                {
                    GetPixelFootprint(input.positionWS, normalWS, uvDx, uvDy);
                    footprintMip = LoogaVTMipGrad(uvDx, uvDy);
                }
                if (!LoogaVTCanCacheGrad(uvDx, uvDy)) return 0;
                // Steep pixels shade directly and need no pages.
                if (_LoogaVTSampling.y > 0 && normalWS.y < _LoogaVTSampling.y) return 0;
                uint mip = (uint)floor(min(screenMip, footprintMip));
                uint width = (uint)_LoogaVTInfo.y >> mip;
                uint2 page = (uint2)(clamp(input.uv, 0, 0.99999994) * width);
                return float2(LoogaVTPageIndex(page, mip) + 1, _LoogaVTTile);
            }
            ENDHLSL
        }
    }
}
