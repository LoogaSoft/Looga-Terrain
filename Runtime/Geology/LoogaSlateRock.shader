Shader "Looga/Slate Rock"
{
    Properties
    {
        [NoScaleOffset] _AlbedoMap("Albedo and Smoothness", 2D) = "gray" {}
        [NoScaleOffset] _NormalMap("Normal, Height and Cavity", 2D) = "gray" {}
        [NoScaleOffset] _DetailMap("Lip and Grit", 2D) = "black" {}
        _TileSize("Tile Size", Float) = 9
        _NormalStrength("Normal Strength", Range(0, 2)) = 1
        _BlendSharpness("Projection Sharpness", Range(1, 16)) = 10
        _GeoBlend("Flat Facet Blend", Range(0, 1)) = 0.7
        _EdgeTint("Edge Tint", Color) = (0.48, 0.447, 0.4, 1)
        _EdgeWearThreshold("Edge Wear Threshold", Range(0, 0.9)) = 0.3
        _EdgeWearStrength("Edge Wear Strength", Range(0, 1)) = 0.8
        _CavityFloor("Cavity Floor", Range(0, 1)) = 0.25
    }

    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Opaque" "Queue"="Geometry" }

        HLSLINCLUDE
        #include "Packages/com.loogasoft.loogaterrain/Runtime/Geology/LoogaSlateRockInput.hlsl"
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            // Forward only: the project renders Deferred+, and this pass has no GBuffer version.
            Tags { "LightMode"="UniversalForwardOnly" }

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex SlateVertex
            #pragma fragment SlateFragment
            // The specular setup lets the shader remove direct specular in crevices.
            #define _SPECULAR_SETUP
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS
            #pragma multi_compile _ EVALUATE_SH_MIXED EVALUATE_SH_VERTEX
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BOX_PROJECTION
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_ATLAS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile_fragment _ _LIGHT_COOKIES
            #pragma multi_compile _ _LIGHT_LAYERS
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile _ SHADOWS_SHADOWMASK
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/RenderingLayers.hlsl"
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Fog.hlsl"
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/ProbeVolumeVariants.hlsl"
            #pragma multi_compile_instancing
            #include "Packages/com.loogasoft.loogaterrain/Runtime/Geology/LoogaSlateRockForward.hlsl"
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode"="ShadowCaster" }
            ZWrite On
            ZTest LEqual
            ColorMask 0

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex ShadowVertex
            #pragma fragment ShadowFragment
            #pragma multi_compile_instancing
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
            #define LOOGA_SLATE_SHADOW_PASS
            #include "Packages/com.loogasoft.loogaterrain/Runtime/Geology/LoogaSlateRockDepth.hlsl"
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode"="DepthOnly" }
            ZWrite On
            ColorMask R

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex DepthVertex
            #pragma fragment DepthFragment
            #pragma multi_compile_instancing
            #include "Packages/com.loogasoft.loogaterrain/Runtime/Geology/LoogaSlateRockDepth.hlsl"
            ENDHLSL
        }

        Pass
        {
            Name "DepthNormalsOnly"
            Tags { "LightMode"="DepthNormalsOnly" }
            ZWrite On

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex DepthVertex
            #pragma fragment DepthNormalsFragment
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/RenderingLayers.hlsl"
            #pragma multi_compile_instancing
            #include "Packages/com.loogasoft.loogaterrain/Runtime/Geology/LoogaSlateRockDepth.hlsl"
            ENDHLSL
        }
    }
}
