// Looga Stamps water surface pass. Draws a water mesh top-down into a water field tile: r = 1 inside water,
// g = highest water surface height in world metres. Max blending keeps the highest surface where meshes overlap.
Shader "Hidden/Looga/WaterSurface"
{
    SubShader
    {
        Pass
        {
            ZTest Always
            ZWrite Off
            Cull Off
            BlendOp Max
            Blend One One

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            #include "UnityCG.cginc"

            // xy = world XZ of the field corner, zw = 1 / field size in metres.
            float4 _FieldRect;

            struct FragmentInput
            {
                float4 position : SV_POSITION;
                float height : TEXCOORD0;
            };

            FragmentInput vert(float4 vertex : POSITION)
            {
                float3 world = mul(unity_ObjectToWorld, float4(vertex.xyz, 1)).xyz;
                float2 uv = (world.xz - _FieldRect.xy) * _FieldRect.zw;
                FragmentInput output;
                // Field texel row 0 is at the field corner, as for texture reads with UV 0.
                output.position = float4(uv * 2 - 1, 0.5, 1);
            #if UNITY_UV_STARTS_AT_TOP
                output.position.y = -output.position.y;
            #endif
                output.height = world.y;
                return output;
            }

            float4 frag(FragmentInput input) : SV_Target
            {
                return float4(1, input.height, 0, 0);
            }
            ENDCG
        }
    }
}
