Shader "Looga/Terrain/Lit"
{
    Properties
    {
        [HideInInspector] _LoogaTerrainSurface("Looga Terrain Surface", Float) = 1
        [HideInInspector] _LoogaVirtualTextureSurface("Virtual Surface", Float) = 1
        _BaseColor("Ground Color", Color) = (0.24, 0.32, 0.16, 1)
        _RockColor("Rock Color", Color) = (0.32, 0.29, 0.25, 1)
        _Smoothness("Smoothness", Range(0, 1)) = 0.15
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" "Queue"="Geometry" "LoogaTerrainDepth"="NativeHeightsAndHoles" "LoogaVTNormalSpace"="World" }
        HLSLINCLUDE
        #pragma target 4.5
        #pragma multi_compile_local _ LOOGA_TERRAIN_BATCHED
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
        #define UNITY_INDIRECT_DRAW_ARGS IndirectDrawIndexedArgs
        #include "UnityIndirect.cginc"
        #include "TerrainGeometry.hlsl"
        #include "VirtualTexturing/VirtualTextureSampling.hlsl"
        CBUFFER_START(UnityPerMaterial)
        half4 _BaseColor;
        half4 _RockColor;
        half _Smoothness;
        CBUFFER_END
        float3 _LightDirection;
        float3 _LightPosition;

        struct Attributes
        {
            float3 position : POSITION;
            uint instance : SV_InstanceID;
        };
        struct Varyings
        {
            float4 positionCS : SV_POSITION;
            float3 positionWS : TEXCOORD0;
            float3 normalWS : TEXCOORD1;
            float2 uv : TEXCOORD2;
        };
        Varyings TerrainVertex(Attributes input)
        {
            InitIndirectDrawArgs(0);
            Varyings output;
            output.uv = LoogaPatchUV(input.position.xz, GetIndirectInstanceID(input.instance));
            output.positionWS = LoogaPosition(output.uv);
            output.normalWS = LoogaNormal(output.uv);
            output.positionCS = TransformWorldToHClip(output.positionWS);
            return output;
        }
        Varyings ShadowVertex(Attributes input)
        {
            Varyings output = TerrainVertex(input);
            #if defined(_CASTING_PUNCTUAL_LIGHT_SHADOW)
            float3 direction = normalize(_LightPosition - output.positionWS);
            #else
            float3 direction = _LightDirection;
            #endif
            output.positionCS = TransformWorldToHClip(ApplyShadowBias(output.positionWS, output.normalWS, direction));
            #if UNITY_REVERSED_Z
            output.positionCS.z = min(output.positionCS.z, output.positionCS.w * UNITY_NEAR_CLIP_VALUE);
            #else
            output.positionCS.z = max(output.positionCS.z, output.positionCS.w * UNITY_NEAR_CLIP_VALUE);
            #endif
            return output;
        }
        half4 TerrainFragment(Varyings input) : SV_Target
        {
            LoogaClipHoles(input.uv);
            InputData lighting = (InputData)0;
            lighting.positionWS = input.positionWS;
            lighting.normalWS = normalize(input.normalWS);
            lighting.viewDirectionWS = GetWorldSpaceNormalizeViewDir(input.positionWS);
            lighting.shadowCoord = TransformWorldToShadowCoord(input.positionWS);
            lighting.bakedGI = SampleSH(lighting.normalWS);
            lighting.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);
            lighting.shadowMask = half4(1, 1, 1, 1);
            SurfaceData surface = (SurfaceData)0;
            surface.albedo = lerp(_RockColor.rgb, _BaseColor.rgb, smoothstep(0.5, 0.85, lighting.normalWS.y));
            surface.normalTS = half3(0, 0, 1);
            surface.smoothness = _Smoothness;
            surface.occlusion = 1;
            surface.alpha = 1;
            float4 cachedAlbedo, cachedNormal, cachedEmission;
            if (LoogaVTRead(input.uv, cachedAlbedo, cachedNormal, cachedEmission))
            {
                surface.albedo = cachedAlbedo.rgb;
                surface.smoothness = cachedAlbedo.a;
                surface.occlusion = cachedNormal.a;
                surface.emission = cachedEmission.rgb;
                surface.metallic = cachedEmission.a;
                lighting.normalWS = cachedNormal.xyz;
                lighting.bakedGI = SampleSH(lighting.normalWS);
            }
            half4 color = UniversalFragmentPBR(lighting, surface);
            color.rgb = MixFog(color.rgb, ComputeFogFactor(TransformWorldToHClip(input.positionWS).z));
            return color;
        }
        half4 DepthFragment(Varyings input) : SV_Target
        {
            LoogaClipHoles(input.uv);
            return 0;
        }
        half4 NormalFragment(Varyings input) : SV_Target
        {
            LoogaClipHoles(input.uv);
            #if defined(_GBUFFER_NORMALS_OCT)
            float2 oct = PackNormalOctQuadEncode(normalize(input.normalWS));
            return half4(PackFloat2To888(saturate(oct * 0.5 + 0.5)), 0);
            #else
            return half4(normalize(input.normalWS), 0);
            #endif
        }
        float4 _LoogaVTPageRect;
        struct BakeVaryings { float4 position : SV_POSITION; float2 uv : TEXCOORD0; };
        struct BakeOutput
        {
            float4 albedo : SV_Target0;
            float4 normal : SV_Target1;
            float4 emission : SV_Target2;
        };
        BakeVaryings BakeVertex(uint vertex : SV_VertexID)
        {
            BakeVaryings output;
            float2 uv = float2((vertex << 1) & 2, vertex & 2);
            output.position = float4(uv * 2 - 1, 0, 1);
            #if UNITY_UV_STARTS_AT_TOP
            output.position.y = -output.position.y;
            #endif
            output.uv = _LoogaVTPageRect.xy + uv * _LoogaVTPageRect.zw;
            return output;
        }
        BakeOutput BakeFragment(BakeVaryings input)
        {
            BakeOutput output;
            float3 normal = LoogaNormal(saturate(input.uv));
            output.albedo = float4(lerp(_RockColor.rgb, _BaseColor.rgb, smoothstep(0.5, 0.85, normal.y)), _Smoothness);
            output.normal = float4(normal, 1);
            output.emission = 0;
            return output;
        }
        ENDHLSL
        Pass
        {
            Name "LoogaVTBake"
            Tags { "LightMode"="LoogaVTBake" }
            Cull Off ZTest Always ZWrite Off
            HLSLPROGRAM
            #pragma vertex BakeVertex
            #pragma fragment BakeFragment
            ENDHLSL
        }
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode"="UniversalForwardOnly" }
            HLSLPROGRAM
            #pragma vertex TerrainVertex
            #pragma fragment TerrainFragment
            #pragma multi_compile_instancing
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile_fog
            ENDHLSL
        }
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode"="ShadowCaster" }
            ColorMask 0
            HLSLPROGRAM
            #pragma vertex ShadowVertex
            #pragma fragment DepthFragment
            #pragma multi_compile_instancing
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            ENDHLSL
        }
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode"="DepthOnly" }
            ColorMask R
            HLSLPROGRAM
            #pragma vertex TerrainVertex
            #pragma fragment DepthFragment
            #pragma multi_compile_instancing
            ENDHLSL
        }
        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode"="DepthNormalsOnly" }
            HLSLPROGRAM
            #pragma vertex TerrainVertex
            #pragma fragment NormalFragment
            #pragma multi_compile_instancing
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT
            ENDHLSL
        }
    }
    Fallback Off
}
