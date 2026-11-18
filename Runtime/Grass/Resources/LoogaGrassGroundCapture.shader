// Draws the ground parts of meshes from above for GrassGroundMap: the world height of the ground, the offset to the
// terrain point whose ground it shows and the grass density. The vertices hold the offset and the cover weight in
// TEXCOORD2, as for the terrain surface mesh. A part with a ground mask takes the cover from red of the mask at
// TEXCOORD0 for each pixel, so the grass follows the painted ground and not the vertices of a coarse mesh. Parts
// without cover or with steep ground grow no grass, so they are left out.
Shader "Hidden/Looga/Terrain/Grass Ground Capture"
{
    SubShader
    {
        Pass
        {
            Name "Capture"
            Cull Off
            ZWrite On
            ZTest LEqual

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vertex
            #pragma fragment Fragment

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            float4x4 _GrassGroundViewProjection;
            float _GrassGroundMinimumUp;
            float _GrassGroundMaskOn;
            TEXTURE2D(_GrassGroundMask);
            SAMPLER(sampler_GrassGroundMask);

            // Masked ground grows grass from this mask value and is full this far above it, so the grass thins out
            // over the soft edge of the mask.
            #define GRASS_MASK_START 0.25
            #define GRASS_MASK_FADE 0.45

            struct Attributes
            {
                float3 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
                // xy: world XZ offset to the terrain point whose ground the vertex shows. z: cover weight.
                float4 cover : TEXCOORD2;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float height : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float3 cover : TEXCOORD2;
                float2 uv : TEXCOORD3;
            };

            struct Targets
            {
                float height : SV_Target0;
                // xy: offset to the terrain point. z: grass density.
                float4 offset : SV_Target1;
            };

            Varyings Vertex(Attributes input)
            {
                Varyings output;
                float3 positionWS = TransformObjectToWorld(input.positionOS);
                output.positionCS = mul(_GrassGroundViewProjection, float4(positionWS, 1.0));
                output.height = positionWS.y;
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.cover = input.cover.xyz;
                output.uv = input.uv;
                return output;
            }

            Targets Fragment(Varyings input)
            {
                // A cave entrance mesh adds 2 to its cover weights. The mesh ends along a ragged edge where the weight
                // falls; its pixels there hide the terrain blades under them, so all covered parts grow grass.
                float cover = input.cover.z > 1.5 ? input.cover.z - 2.0 : input.cover.z;
                float density = 1.0;
                if (_GrassGroundMaskOn > 0.5)
                {
                    cover = max(cover, SAMPLE_TEXTURE2D(_GrassGroundMask, sampler_GrassGroundMask, input.uv).r);
                    clip(cover - GRASS_MASK_START);
                    density = saturate((cover - GRASS_MASK_START) / GRASS_MASK_FADE);
                }
                clip(cover - 0.01);
                clip(normalize(input.normalWS).y - _GrassGroundMinimumUp);
                Targets output;
                output.height = input.height;
                output.offset = float4(input.cover.xy, density, 0.0);
                return output;
            }
            ENDHLSL
        }
    }
}
