// Packs the source maps of one terrain layer into the slices of the surface texture arrays. The array builder
// (LoogaTerrainSurfaceArrayBuilder) draws one fullscreen triangle per pass into a render texture.
Shader "Hidden/Looga/Terrain/Surface Pack"
{
    HLSLINCLUDE
    #pragma target 4.5
    #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
    #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Packing.hlsl"

    TEXTURE2D(_Albedo);
    TEXTURE2D(_Normal);
    TEXTURE2D(_Height);
    TEXTURE2D(_Smoothness);
    TEXTURE2D(_Occlusion);
    TEXTURE2D(_Metallic);
    TEXTURE2D_ARRAY(_SourceArray);
    SAMPLER(sampler_linear_repeat);

    float4 _Lods;            // Mip level of the albedo, normal, height and smoothness sources
    float4 _Lods2;           // Mip level of the occlusion and metallic sources
    float4 _Present;         // Source exists (0 or 1): height, smoothness, occlusion, metallic
    float4 _HeightMask;      // Channel selectors: one component is 1
    float4 _SmoothnessMask;
    float4 _OcclusionMask;
    float4 _MetallicMask;
    float4 _Flags;           // x albedo exists, y normal exists, z normal is an imported normal map, w flip green
    float4 _Options;         // x smoothness is roughness, y generate missing maps from the normal
    float4 _Slice;           // x array slice, y mip level (array copy pass)

    struct Varyings
    {
        float4 positionCS : SV_POSITION;
        float2 uv : TEXCOORD0;
    };

    Varyings Vert(uint vertex : SV_VertexID)
    {
        Varyings output;
        float2 uv = float2((vertex << 1) & 2, vertex & 2);
        output.positionCS = float4(uv * 2 - 1, 0, 1);
        // Without a projection matrix, flip the clip space on top-origin APIs. Texel row 0 of the result is then v = 0.
        #if UNITY_UV_STARTS_AT_TOP
            output.positionCS.y = -output.positionCS.y;
        #endif
        output.uv = uv;
        return output;
    }

    // Tangent-space normal in the -1 to 1 range.
    float3 SourceNormal(float2 uv)
    {
        if (_Flags.y < 0.5) return float3(0, 0, 1);
        float4 packed = SAMPLE_TEXTURE2D_LOD(_Normal, sampler_linear_repeat, uv, _Lods.y);
        float3 normal;
        if (_Flags.z > 0.5)
        {
            normal = UnpackNormalMapRGorAG(packed);
        }
        else
        {
            normal = packed.rgb * 2 - 1;
        }
        if (_Flags.w > 0.5)
        {
            normal.y = -normal.y;
        }
        normal.z = sqrt(saturate(1 - dot(normal.xy, normal.xy)));
        return normal;
    }

    float SampleChannel(TEXTURE2D_PARAM(source, samplerSource), float2 uv, float lod, float4 mask)
    {
        return dot(SAMPLE_TEXTURE2D_LOD(source, samplerSource, uv, lod), mask);
    }

    // MicroSplat makes a missing height from the packed normal: an overlay of normal y over normal x.
    float SourceHeight(float2 uv, float3 normal)
    {
        float base = normal.y * 0.5 + 0.5;
        float blend = normal.x * 0.5 + 0.5;
        float generated = base < 0.5 ? 2 * base * blend : 1 - 2 * (1 - base) * (1 - blend);
        float sampled = SampleChannel(TEXTURE2D_ARGS(_Height, sampler_linear_repeat), uv, _Lods.z, _HeightMask);
        return _Present.x > 0.5 ? sampled : _Options.y > 0.5 ? generated : 0.5;
    }

    float SourceSmoothness(float2 uv)
    {
        float value = SampleChannel(TEXTURE2D_ARGS(_Smoothness, sampler_linear_repeat), uv, _Lods.w, _SmoothnessMask);
        value = _Options.x > 0.5 ? 1 - value : value;
        return _Present.y > 0.5 ? value : 0;
    }

    // MicroSplat makes a missing occlusion from the normal: normal z squared.
    float SourceOcclusion(float2 uv, float3 normal)
    {
        float sampled = SampleChannel(TEXTURE2D_ARGS(_Occlusion, sampler_linear_repeat), uv, _Lods2.x, _OcclusionMask);
        return _Present.z > 0.5 ? sampled : _Options.y > 0.5 ? normal.z * normal.z : 1;
    }

    float SourceMetallic(float2 uv)
    {
        float sampled = SampleChannel(TEXTURE2D_ARGS(_Metallic, sampler_linear_repeat), uv, _Lods2.y, _MetallicMask);
        return _Present.w > 0.5 ? sampled : 0;
    }

    // Albedo in rgb (the target is sRGB, so the hardware encodes it), height in alpha.
    float4 AlbedoHeight(Varyings input) : SV_Target
    {
        float3 albedo = _Flags.x > 0.5 ? SAMPLE_TEXTURE2D_LOD(_Albedo, sampler_linear_repeat, input.uv, _Lods.x).rgb : 1;
        return float4(albedo, SourceHeight(input.uv, SourceNormal(input.uv)));
    }

    // Compact layout: r smoothness, g normal y, b occlusion, a normal x.
    float4 NormalCompact(Varyings input) : SV_Target
    {
        float3 normal = SourceNormal(input.uv);
        return float4(SourceSmoothness(input.uv), normal.y * 0.5 + 0.5, SourceOcclusion(input.uv, normal), normal.x * 0.5 + 0.5);
    }

    // Quality layout normal (BC5): r normal x, g normal y.
    float4 NormalQuality(Varyings input) : SV_Target
    {
        float3 normal = SourceNormal(input.uv);
        return float4(normal.xy * 0.5 + 0.5, 0, 1);
    }

    // Quality layout mask (BC7): r smoothness, g occlusion, b metallic.
    float4 MaskQuality(Varyings input) : SV_Target
    {
        float3 normal = SourceNormal(input.uv);
        return float4(SourceSmoothness(input.uv), SourceOcclusion(input.uv, normal), SourceMetallic(input.uv), 1);
    }

    // Copies one array slice for checks and comparisons.
    float4 ArraySlice(Varyings input) : SV_Target
    {
        return SAMPLE_TEXTURE2D_ARRAY_LOD(_SourceArray, sampler_linear_repeat, input.uv, _Slice.x, _Slice.y);
    }
    ENDHLSL

    SubShader
    {
        ZTest Always ZWrite Off Cull Off

        Pass
        {
            Name "AlbedoHeight"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment AlbedoHeight
            ENDHLSL
        }
        Pass
        {
            Name "NormalCompact"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment NormalCompact
            ENDHLSL
        }
        Pass
        {
            Name "NormalQuality"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment NormalQuality
            ENDHLSL
        }
        Pass
        {
            Name "MaskQuality"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment MaskQuality
            ENDHLSL
        }
        Pass
        {
            Name "ArraySlice"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment ArraySlice
            ENDHLSL
        }
    }
}
