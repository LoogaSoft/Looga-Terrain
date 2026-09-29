Shader "Hidden/Looga/Terrain/BatchedShadows"
{
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" }
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode"="ShadowCaster" }
            ColorMask 0
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vertex
            #pragma fragment Fragment
            #pragma multi_compile_instancing
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
            #define UNITY_INDIRECT_DRAW_ARGS IndirectDrawIndexedArgs
            #include "UnityIndirect.cginc"
            struct Tile { float4 origin; float4 size; float4 quality; };
            StructuredBuffer<Tile> _DrawTiles;
            StructuredBuffer<int> _DrawSlices;
            StructuredBuffer<float4> _DrawNeighbors;
            StructuredBuffer<float4> _DrawPatches;
            StructuredBuffer<uint> _DrawLod;
            Texture2DArray<float> _DrawHeights;
            Texture2DArray<float> _DrawHoles;
            SamplerState sampler_linear_clamp;
            SamplerState sampler_point_clamp;
            uint _DrawPatchCount;
            uint _DrawHeightResolution;
            static uint _DrawTile;
            float3 _LightDirection;
            float3 _LightPosition;
            uint LoogaNeighborSize(float2 cell)
            {
                int tile = _DrawTile;
                float4 neighbors = _DrawNeighbors[tile];
                if (cell.x < 0 && neighbors.x >= 0.0) { tile = (int)neighbors.x; cell.x = _DrawPatchCount - 1; }
                else if (cell.x >= (float)_DrawPatchCount && neighbors.y >= 0.0) { tile = (int)neighbors.y; cell.x = 0; }
                else if (cell.y < 0 && neighbors.z >= 0.0) { tile = (int)neighbors.z; cell.y = _DrawPatchCount - 1; }
                else if (cell.y >= (float)_DrawPatchCount && neighbors.w >= 0.0) { tile = (int)neighbors.w; cell.y = 0; }
                uint2 index = (uint2)clamp(floor(cell), 0, _DrawPatchCount - 1);
                return _DrawLod[tile * _DrawPatchCount * _DrawPatchCount + index.y * _DrawPatchCount + index.x];
            }
            float2 LoogaPatchUV(float2 grid, uint instance)
            {
                float4 patch = _DrawPatches[instance];
                _DrawTile = (uint)patch.w;
                float2 cell = patch.xy + grid * patch.z;
                if (grid.x < 0.0001 || grid.x > 0.9999)
                {
                    float side = grid.x < 0.5 ? -0.001 : 0.001;
                    uint neighbor = LoogaNeighborSize(cell + float2(side, -0.001));
                    if ((float)neighbor > patch.z)
                    {
                        float step = (float)neighbor / 16;
                        cell.y = floor(cell.y / step + 0.0001) * step;
                    }
                }
                if (grid.y < 0.0001 || grid.y > 0.9999)
                {
                    float side = grid.y < 0.5 ? -0.001 : 0.001;
                    uint neighbor = LoogaNeighborSize(cell + float2(-0.001, side));
                    if ((float)neighbor > patch.z)
                    {
                        float step = (float)neighbor / 16;
                        cell.x = floor(cell.x / step + 0.0001) * step;
                    }
                }
                return cell / _DrawPatchCount;
            }


            float Height(float2 uv)
            {
                float2 coordinate = (saturate(uv) * (_DrawHeightResolution - 1) + 0.5) / _DrawHeightResolution;
                return _DrawHeights.SampleLevel(sampler_linear_clamp, float3(coordinate, _DrawSlices[_DrawTile]), 0) * (65535.0 / 32766.0);
            }
            struct Varyings { float4 position : SV_POSITION; float2 uv : TEXCOORD0; nointerpolation uint tile : TEXCOORD1; };
            Varyings Vertex(float3 position : POSITION, uint instance : SV_InstanceID)
            {
                InitIndirectDrawArgs(0);
                Varyings output;
                output.uv = LoogaPatchUV(position.xz, GetIndirectInstanceID(instance));
                output.tile = _DrawTile;
                Tile tile = _DrawTiles[_DrawTile];
                float3 world = tile.origin.xyz + float3(output.uv.x * tile.size.x, Height(output.uv) * tile.size.y, output.uv.y * tile.size.z);
                float step = 1.0 / (_DrawHeightResolution - 1);
                float2 low = max(0, output.uv-step), high = min(1, output.uv+step);
                float dx = (Height(float2(high.x, output.uv.y))-Height(float2(low.x, output.uv.y))) * tile.size.y;
                float dz = (Height(float2(output.uv.x, high.y))-Height(float2(output.uv.x, low.y))) * tile.size.y;
                float3 normal = normalize(float3(-dx/max(0.00001,(high.x-low.x)*tile.size.x),1,-dz/max(0.00001,(high.y-low.y)*tile.size.z)));
                #if defined(_CASTING_PUNCTUAL_LIGHT_SHADOW)
                float3 direction = normalize(_LightPosition-world);
                #else
                float3 direction = _LightDirection;
                #endif
                output.position = TransformWorldToHClip(ApplyShadowBias(world, normal, direction));
                #if UNITY_REVERSED_Z
                output.position.z = min(output.position.z, output.position.w * UNITY_NEAR_CLIP_VALUE);
                #else
                output.position.z = max(output.position.z, output.position.w * UNITY_NEAR_CLIP_VALUE);
                #endif
                return output;
            }
            half4 Fragment(Varyings input) : SV_Target
            {
                clip(_DrawHoles.SampleLevel(sampler_point_clamp, float3(saturate(input.uv), _DrawSlices[input.tile]), 0)-0.5);
                return 0;
            }
            ENDHLSL
        }
    }
}
