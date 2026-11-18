// Looga Stamps height passes for MicroVerse parity mode. Each pass is a full-screen blit from one 16-bit tile texture
// into another. The stamp UV comes from the vertices and the rasterizer, as in MicroVerse, so samples on a stamp edge
// and texture sampling positions match MicroVerse exactly.
Shader "Hidden/Looga/HeightStampRaster"
{
    Properties
    {
        [HideInInspector] _MainTex ("Heights", 2D) = "black" {}
    }
    SubShader
    {
        ZTest Always
        ZWrite Off
        Cull Off

        // Pass 0: height stamp.
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            #define LOOGA_STAMP_RASTER
            #include "UnityCG.cginc"
            #include "../Shaders/HeightStampCore.hlsl"

            Texture2D _MainTex;
            SamplerState looga_point_clamp_sampler;
            // Always (1, 1). A uniform keeps the operation order of the MicroVerse vertex stage.
            float2 _StampUvScale;

            struct VertexInput
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct FragmentInput
            {
                float4 vertex : SV_POSITION;
                float2 uv : TEXCOORD0;
                float2 stampUv : TEXCOORD1;
            };

            FragmentInput vert(VertexInput input)
            {
                FragmentInput output;
                output.vertex = UnityObjectToClipPos(input.vertex);
                output.uv = input.uv;
                output.stampUv = mul(_TileToStamp, float4(input.uv, 0, 1)).xy;
                output.stampUv -= 0.5;
                output.stampUv *= _StampUvScale;
                output.stampUv += 0.5;
                return output;
            }

            float4 frag(FragmentInput input) : SV_Target
            {
                float4 source = _MainTex.SampleLevel(looga_point_clamp_sampler, input.uv, 0);
                if (input.stampUv.x < 0 || input.stampUv.x > 1 || input.stampUv.y < 0 || input.stampUv.y > 1)
                {
                    return source;
                }
                FalloffInput falloff;
                falloff.stampUv = input.stampUv;
                falloff.stampUvDx = ddx(input.stampUv);
                falloff.stampUvDy = ddy(input.stampUv);
                falloff.tileUv = input.uv;
                falloff.worldNoiseUv = NoiseUv(input.uv);
                return StampHeight(source.r, falloff);
            }
            ENDCG
        }

        // Pass 1: spline path.
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            #define LOOGA_STAMP_RASTER
            #include "UnityCG.cginc"
            #include "../Shaders/HeightStampCore.hlsl"

            Texture2D _MainTex;
            SamplerState sampler_MainTex;
            float _Resolution;

            struct VertexInput
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct FragmentInput
            {
                float4 vertex : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            FragmentInput vert(VertexInput input)
            {
                FragmentInput output;
                output.vertex = UnityObjectToClipPos(input.vertex);
                output.uv = input.uv;
                return output;
            }

            float4 frag(FragmentInput input) : SV_Target
            {
                float height = _MainTex.Sample(sampler_MainTex, input.uv).r;
                return PathHeight(height, input.uv, _Resolution);
            }
            ENDCG
        }

        // Pass 2: snapshot limit. The snapshot is a copy of an earlier source texture, so a texel read keeps its exact
        // 16-bit value.
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            #define LOOGA_STAMP_RASTER
            #include "UnityCG.cginc"
            #include "../Shaders/HeightStampCore.hlsl"

            Texture2D _MainTex;
            Texture2D<float> _Snapshot;
            SamplerState looga_point_clamp_sampler;

            struct VertexInput
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct FragmentInput
            {
                float4 vertex : SV_POSITION;
                float2 uv : TEXCOORD0;
                float2 stampUv : TEXCOORD1;
            };

            FragmentInput vert(VertexInput input)
            {
                FragmentInput output;
                output.vertex = UnityObjectToClipPos(input.vertex);
                output.uv = input.uv;
                output.stampUv = mul(_TileToStamp, float4(input.uv, 0, 1)).xy;
                return output;
            }

            float4 frag(FragmentInput input) : SV_Target
            {
                float source = _MainTex.SampleLevel(looga_point_clamp_sampler, input.uv, 0).r;
                if (input.stampUv.x < 0 || input.stampUv.x > 1 || input.stampUv.y < 0 || input.stampUv.y > 1)
                {
                    return source;
                }
                FalloffInput falloff;
                falloff.stampUv = input.stampUv;
                falloff.stampUvDx = ddx(input.stampUv);
                falloff.stampUvDy = ddy(input.stampUv);
                falloff.tileUv = input.uv;
                falloff.worldNoiseUv = NoiseUv(input.uv);
                float snapshot = _Snapshot.Load(int3((int2)input.vertex.xy, 0));
                return SnapshotLimitHeight(source, snapshot, falloff);
            }
            ENDCG
        }

        // Pass 3: water bed.
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            #define LOOGA_STAMP_RASTER
            #include "UnityCG.cginc"
            #include "../Shaders/HeightStampCore.hlsl"

            Texture2D _MainTex;
            SamplerState looga_point_clamp_sampler;

            struct VertexInput
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct FragmentInput
            {
                float4 vertex : SV_POSITION;
                float2 uv : TEXCOORD0;
                float2 stampUv : TEXCOORD1;
            };

            FragmentInput vert(VertexInput input)
            {
                FragmentInput output;
                output.vertex = UnityObjectToClipPos(input.vertex);
                output.uv = input.uv;
                output.stampUv = mul(_TileToStamp, float4(input.uv, 0, 1)).xy;
                return output;
            }

            float4 frag(FragmentInput input) : SV_Target
            {
                float height = _MainTex.SampleLevel(looga_point_clamp_sampler, input.uv, 0).r;
                FalloffInput falloff;
                falloff.stampUv = input.stampUv;
                falloff.stampUvDx = ddx(input.stampUv);
                falloff.stampUvDy = ddy(input.stampUv);
                falloff.tileUv = input.uv;
                falloff.worldNoiseUv = NoiseUv(input.uv);
                return WaterBedHeight(height, falloff);
            }
            ENDCG
        }
    }
}
