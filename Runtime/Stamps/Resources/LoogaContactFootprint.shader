// Looga Stamps contact stamp footprint. Each rock mesh renders straight down onto the apron grid of one tile. A rock
// surface above the ground adds +1 where it faces up and -1 where it faces down. The sum is the winding number of the
// ground point: 1 or more inside the rock, 0 outside, also under an arch or an overhang. Texel (x, y) is the world XZ
// position _FootprintRect.xy + (x + 0.5, y + 0.5) / _FootprintRect.zw. See LoogaContactStamp.cs.
Shader "Hidden/Looga/ContactFootprint"
{
    SubShader
    {
        ZTest Always
        ZWrite Off
        Cull Off
        Blend One One

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            #include "UnityCG.cginc"

            // xy = world XZ of the grid corner, zw = 1 / grid extent in metres.
            float4 _FootprintRect;
            // Heights before the stamp in heightmap texture units, on the same grid.
            Texture2D<float> _GroundApron;
            float _TerrainHeight;
            float _RealHeight;
            float _ContactBand;

            struct Varyings
            {
                float4 position : SV_POSITION;
                float height : TEXCOORD0;
                float normalY : TEXCOORD1;
            };

            Varyings vert(float4 vertex : POSITION, float3 normal : NORMAL)
            {
                float3 world = mul(unity_ObjectToWorld, vertex).xyz;
                float2 uv = (world.xz - _FootprintRect.xy) * _FootprintRect.zw;
                Varyings output;
                output.position = float4(uv * 2 - 1, 0.5, 1);
                // Texel row 0 is world Z of the grid corner, as in the compute passes that read the texture.
            #if UNITY_UV_STARTS_AT_TOP
                output.position.y = -output.position.y;
            #endif
                output.height = world.y;
                output.normalY = UnityObjectToWorldNormal(normal).y;
                return output;
            }

            float4 frag(Varyings input) : SV_Target
            {
                float ground = _TerrainHeight + _GroundApron.Load(int3(input.position.xy, 0)) * _RealHeight;
                if (input.height <= ground + _ContactBand) return 0;
                return input.normalY >= 0 ? 1 : -1;
            }
            ENDCG
        }
    }
}
