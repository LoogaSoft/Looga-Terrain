Shader "Hidden/Looga/Terrain/BatchedShadows"
{
    // Terrain shadow casters of the shadow BatchRendererGroup (TerrainShadowGroup). One tile per draw. The visible
    // instance value of each instance is a packed light-space patch. The SRP Batcher, which BatchRendererGroup draws
    // use, needs each UnityPerMaterial value as a property.
    Properties
    {
        [HideInInspector] _DrawPatchCount ("Patch Count", Integer) = 64
        [HideInInspector] _DrawHeightResolution ("Height Resolution", Integer) = 1025
        [HideInInspector] _ShadowTile ("Shadow Tile", Integer) = 0
        [HideInInspector] _ShadowLevelStride ("Shadow Level Stride", Integer) = 0
    }
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
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            // Only tiles with holes use the clip variant. Without a clip the shadow pass needs no pixel work, so the
            // GPU keeps its fast depth-only path.
            #pragma multi_compile_fragment _ _ALPHATEST_ON
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
            struct Tile { float4 origin; float4 size; float4 quality; };
            StructuredBuffer<Tile> _DrawTiles;
            StructuredBuffer<float4> _DrawNeighbors;
            StructuredBuffer<uint> _DrawLod;
            Texture2D<float> _ShadowHeight;
            Texture2D<float> _ShadowHoles;
            SamplerState sampler_linear_clamp;
            SamplerState sampler_point_clamp;
            CBUFFER_START(UnityPerMaterial)
                uint _DrawPatchCount;
                uint _DrawHeightResolution;
                uint _ShadowTile;
                uint _ShadowLevelStride;
            CBUFFER_END
            static uint _DrawLodBase;
            float3 _LightDirection;
            float3 _LightPosition;
            uint LoogaNeighborSize(float2 cell)
            {
                int tile = _ShadowTile;
                float4 neighbors = _DrawNeighbors[tile];
                if (cell.x < 0 && neighbors.x >= 0.0) { tile = (int)neighbors.x; cell.x = _DrawPatchCount - 1; }
                else if (cell.x >= (float)_DrawPatchCount && neighbors.y >= 0.0) { tile = (int)neighbors.y; cell.x = 0; }
                else if (cell.y < 0 && neighbors.z >= 0.0) { tile = (int)neighbors.z; cell.y = _DrawPatchCount - 1; }
                else if (cell.y >= (float)_DrawPatchCount && neighbors.w >= 0.0) { tile = (int)neighbors.w; cell.y = 0; }
                uint2 index = (uint2)clamp(floor(cell), 0, _DrawPatchCount - 1);
                return _DrawLod[_DrawLodBase + tile * _DrawPatchCount * _DrawPatchCount + index.y * _DrawPatchCount + index.x];
            }
            // Packed patch: x 7 bits, y 7 bits, size 8 bits, split from bit 22. Each split has its own LOD map.
            float2 LoogaPatchUV(float2 grid, uint packed)
            {
                _DrawLodBase = (packed >> 22) * _ShadowLevelStride;
                float3 patch = float3(packed & 127, (packed >> 7) & 127, (packed >> 14) & 255);
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
                return _ShadowHeight.SampleLevel(sampler_linear_clamp, coordinate, 0) * (65535.0 / 32766.0);
            }
            struct Attributes
            {
                float3 position : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            struct Varyings { float4 position : SV_POSITION; float2 uv : TEXCOORD0; };
            Varyings Vertex(Attributes input)
            {
                Varyings output = (Varyings)0;
                #if defined(DOTS_INSTANCING_ON)
                UNITY_SETUP_INSTANCE_ID(input);
                output.uv = LoogaPatchUV(input.position.xz, GetDOTSInstanceIndex());
                Tile tile = _DrawTiles[_ShadowTile];
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
                #endif
                // Without DOTS instancing (no BatchRendererGroup) the position stays zero, so nothing draws.
                return output;
            }
            half4 Fragment(Varyings input) : SV_Target
            {
                #if defined(_ALPHATEST_ON)
                clip(_ShadowHoles.SampleLevel(sampler_point_clamp, saturate(input.uv), 0)-0.5);
                #endif
                return 0;
            }
            ENDHLSL
        }
    }
}
