// Looga TerrainRenderer target of the Looga terrain surface. The renderer draws GPU-selected patches of the native
// terrain data (TerrainGeometry.hlsl). LoogaTerrainSurface creates the material at run time and
// LoogaTerrainSurfaceBinding gives it to the renderer as a surface override. The pass code and the surface library are
// the same as in the native target "Looga/Terrain/Surface".
Shader "Looga/Terrain/Surface (Patches)"
{
    Properties
    {
        // The Looga TerrainRenderer accepts a material with this property.
        [HideInInspector] _LoogaTerrainSurface("Looga Terrain Surface", Float) = 1
        // A TerrainVirtualTexture accepts the material as its own page producer (LoogaVTBake pass).
        [HideInInspector] _LoogaVirtualTextureSurface("Virtual Surface", Float) = 1
        // Meters around a page texel whose terrain data the page reads (LoogaTerrainSurface.ApplyTo).
        [HideInInspector] _LoogaVTDependencyRadius("Cache Dependency Radius", Float) = 0
        [HideInInspector] _LoogaAlbedoArray("Albedo and Height Array", 2DArray) = "" {}
        [HideInInspector] _LoogaNormalArray("Normal Array", 2DArray) = "" {}
        [HideInInspector] _LoogaMaskArray("Smoothness, Occlusion and Metallic Array (Quality)", 2DArray) = "" {}
        [HideInInspector] _LoogaLayerProps("Layer Properties", 2D) = "black" {}
        [HideInInspector] _LoogaSurfaceUV("World UV Scale and Offset", Vector) = (1, 1, 0, 0)
        [HideInInspector] _LoogaSurfaceBlend("Blend", Vector) = (0, 4, 0.2, 0)
        [HideInInspector] _LoogaSurfaceHybrid("Hybrid Blend Distances", Vector) = (40, 120, 0, 0)
        [HideInInspector] _LoogaSurfaceEdge("Blend Edge", Vector) = (1.5, 0.5, 0.25, 1.5)
        [HideInInspector] _LoogaSurfaceStochastic("Stochastic", Vector) = (1, 1, 0.2, 0)
        [HideInInspector] _LoogaSurfaceLuminance("Saturation Luminance", Vector) = (0.2126, 0.7152, 0.0722, 0)
        [HideInInspector] _LoogaSurfaceDebug("Debug", Vector) = (0, 0, 0, 0)

        // Anti-tiling. LoogaTerrainSurface.ApplyTo writes these. A strength of 0 turns a feature off.
        [HideInInspector] _LoogaDetailNoise("Detail Noise", Vector) = (0.25, 0, 80, 0)
        [HideInInspector] _LoogaDistanceNoise("Distance Noise", Vector) = (0.005, 0, 60, 300)
        [HideInInspector] _LoogaNormalNoise1("Normal Noise 1", Vector) = (0.03125, 0, 0, 0)
        [HideInInspector] _LoogaNormalNoise2("Normal Noise 2", Vector) = (0.03125, 0, 0, 0)
        [HideInInspector] _LoogaNormalNoise3("Normal Noise 3", Vector) = (0.03125, 0, 0, 0)
        [HideInInspector] _LoogaNoiseHeight("Noise Height", Vector) = (0.25, 0, 0, 0)
        [HideInInspector] _LoogaControlNoise("Control UV Noise", Vector) = (0.333, 0, 0, 0)
        [HideInInspector] _LoogaResample("Distance Resample", Vector) = (0.25, 60, 250, 0)
        [HideInInspector] _LoogaResampleBlend("Distance Resample Blend", Vector) = (0, 0, 0, 0.02)
        [HideInInspector] _LoogaResampleNoise("Distance Resample Noise", Vector) = (0.5, 0, 0, 0)
        [HideInInspector] _LoogaTriplanar("Triplanar", Vector) = (0, 4, 0.978, 0.906)
        [HideInInspector] _LoogaTriplanar2("Triplanar 2", Vector) = (0, 0.2, 1, 0.05)
        [HideInInspector] _LoogaParallax("Parallax", Vector) = (0, 8, 25, 12)
        [HideInInspector] _LoogaWorldColor("World Colour", Vector) = (0, 0, 50, 500)
        [HideInInspector] _LoogaWorldColor2("World Colour 2", Vector) = (0, -1, -2, 0)
        [HideInInspector] _LoogaGroundCover("Ground Cover", Vector) = (0, 30, 130, 2.5)
        [HideInInspector] _LoogaGroundCoverShade("Ground Cover Shade", Vector) = (0.55, 0.3, 0.1, 1)
        [HideInInspector] _LoogaGroundCoverSoil("Ground Cover Soil", Vector) = (0.2, 0.6, 0.75, 0)
        [HideInInspector] _LoogaGroundCoverView("Ground Cover View", Vector) = (0.35, 0.6, 1, 25)
        [HideInInspector] _LoogaGroundCoverDense("Ground Cover Dense", Vector) = (0.3, 0.65, 1, 0)
        [HideInInspector] _LoogaGroundCoverDetail("Ground Cover Detail", Vector) = (0, 0, 0, 0)
        [HideInInspector] _LoogaFarDetail("Far Detail", Vector) = (0, 0, 1, 1)
        [HideInInspector] _LoogaSurfaceFade("Surface Fade", Vector) = (0, 1, 0, 0)
        [HideInInspector] _LoogaFarStrata("Far Strata", Vector) = (0, 0, 0, 0)
        [HideInInspector] _LoogaFarCurvature("Far Curvature", Vector) = (0, 4, 1, 0)
        [HideInInspector] _LoogaFieldWetness("Field Wetness", Vector) = (0, 0, 0, 1)
        [HideInInspector] _LoogaFieldChannel("Field Channel", Vector) = (0, 0, 0, 1)
        [HideInInspector] _LoogaFieldRim("Field Rim", Vector) = (0, 0, 0, 1)
        [HideInInspector] _LoogaFieldFoot("Field Foot", Vector) = (0, 0, 0, 0)
        [HideInInspector] _LoogaFieldFootRange("Field Foot Range", Vector) = (0, 1, 0, 0)
        [HideInInspector] _LoogaDetailNoiseTex("Detail Noise Texture", 2D) = "gray" {}
        [HideInInspector] _LoogaDistanceNoiseTex("Distance Noise Texture", 2D) = "gray" {}
        [HideInInspector] _LoogaNormalNoiseTex1("Normal Noise Texture 1", 2D) = "bump" {}
        [HideInInspector] _LoogaNormalNoiseTex2("Normal Noise Texture 2", 2D) = "bump" {}
        [HideInInspector] _LoogaNormalNoiseTex3("Normal Noise Texture 3", 2D) = "bump" {}

        // Per tile. LoogaTerrainSurfaceBinding sets these through the TerrainRenderer property block.
        [HideInInspector] _LoogaControlTexelSize("Control Texel Size", Vector) = (0.0009765625, 0.0009765625, 1024, 1024)
        [HideInInspector] _LoogaTerrainSize("Terrain Size", Vector) = (1000, 1000, 0.001, 0.001)
        [HideInInspector] _LoogaControlCount("Control Count", Float) = 0
        [HideInInspector] _LoogaSurfaceFields("Surface Fields", 2D) = "gray" {}
        [HideInInspector] _Control0("Control 0", 2D) = "red" {}
        [HideInInspector] _Control1("Control 1", 2D) = "black" {}
        [HideInInspector] _Control2("Control 2", 2D) = "black" {}
        [HideInInspector] _Control3("Control 3", 2D) = "black" {}
        [HideInInspector] _Control4("Control 4", 2D) = "black" {}
        [HideInInspector] _Control5("Control 5", 2D) = "black" {}
        [HideInInspector] _Control6("Control 6", 2D) = "black" {}
        [HideInInspector] _Control7("Control 7", 2D) = "black" {}
        [HideInInspector] _LoogaControlPacked("Control Packed", Vector) = (0, 0, 0, 0)
        [HideInInspector] _LoogaControlIndex("Control Index", 2D) = "black" {}
        [HideInInspector] _LoogaControlWeight("Control Weight", 2D) = "black" {}
    }

    // The TerrainRenderer turns this on only for terrains with holes.
    HLSLINCLUDE
    #pragma multi_compile_fragment __ _ALPHATEST_ON
    ENDHLSL

    SubShader
    {
        // The queue matches the native target, so A/B captures keep the same draw order. The renderer reads the
        // depth tag: the depth comes from the terrain heights and holes only. Cache pages store world normals.
        Tags
        {
            "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "UniversalMaterialType" = "Lit"
            "Queue" = "Geometry+100" "LoogaTerrainDepth" = "NativeHeightsAndHoles" "LoogaVTNormalSpace" = "World"
        }

        Pass
        {
            Name "GBuffer"
            Tags { "LightMode" = "UniversalGBuffer" }
            Stencil
            {
                Ref 96
                Comp Always
                Pass Replace
                WriteMask 96
            }

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex LoogaTerrainLitVertex
            #pragma fragment LoogaTerrainGBufferFragment
            #pragma shader_feature_local_fragment _ LOOGA_SURFACE_PACK_QUALITY
            #pragma shader_feature_local_fragment _ LOOGA_SURFACE_TRIPLANAR
            #pragma shader_feature_local_fragment _ LOOGA_SURFACE_HEIGHT_BLEND
            #pragma exclude_renderers gles3 glcore
            #pragma multi_compile_fragment _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ EVALUATE_SH_MIXED EVALUATE_SH_VERTEX
            #pragma multi_compile_fragment _ _SCREEN_SPACE_IRRADIANCE
            #pragma multi_compile_fragment _ _RENDER_PASS_ENABLED
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT
            #pragma multi_compile_fragment _ _DBUFFER_MRT1 _DBUFFER_MRT2 _DBUFFER_MRT3
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/RenderingLayers.hlsl"
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/ProbeVolumeVariants.hlsl"
            #pragma require cubearray
            #pragma multi_compile_instancing
            #define LOOGA_TERRAIN_PATCHES 1

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.loogasoft.loogagraphicspro/Includes/LoogaModelParameters.hlsl"
            #define LOOGA_DISABLE_MODEL_REFLECTIONS 1
            #define LOOGA_LITE_MATERIAL 1
            #include "Packages/com.loogasoft.loogagraphicspro/Includes/LoogaLightingHelpers.hlsl"
            #define LOOGA_TERRAIN_LIT_PASS 1
            #define LOOGA_TERRAIN_GBUFFER 1
            #include "Packages/com.loogasoft.loogaterrain/Runtime/Shaders/Surface/LoogaTerrainSurfaceNativePasses.hlsl"
            ENDHLSL
        }

        // Fills one virtual texture page of the surface (TerrainVirtualTexture). The page holds the surface without
        // the view: no parallax, and the ground cover is applied per screen pixel.
        Pass
        {
            Name "LoogaVTBake"
            Tags { "LightMode" = "LoogaVTBake" }
            Cull Off ZTest Always ZWrite Off

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex LoogaTerrainPageVertex
            #pragma fragment LoogaTerrainPageFragment
            #pragma shader_feature_local_fragment _ LOOGA_SURFACE_PACK_QUALITY
            #pragma shader_feature_local_fragment _ LOOGA_SURFACE_TRIPLANAR
            #pragma shader_feature_local_fragment _ LOOGA_SURFACE_HEIGHT_BLEND
            #pragma exclude_renderers gles3 glcore
            #define LOOGA_TERRAIN_PATCHES 1

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #define LOOGA_SURFACE_VT_PAGE 1
            #define LOOGA_TERRAIN_VT_PAGE_PASS 1
            #include "Packages/com.loogasoft.loogaterrain/Runtime/Shaders/Surface/LoogaTerrainSurfaceNativePasses.hlsl"
            ENDHLSL
        }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex LoogaTerrainLitVertex
            #pragma fragment LoogaTerrainForwardFragment
            #pragma shader_feature_local_fragment _ LOOGA_SURFACE_PACK_QUALITY
            #pragma shader_feature_local_fragment _ LOOGA_SURFACE_TRIPLANAR
            #pragma shader_feature_local_fragment _ LOOGA_SURFACE_HEIGHT_BLEND
            #pragma exclude_renderers gles3 glcore
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile _ _LIGHT_LAYERS
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile _ EVALUATE_SH_MIXED EVALUATE_SH_VERTEX
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BOX_PROJECTION
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Fog.hlsl"
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/ProbeVolumeVariants.hlsl"
            #pragma multi_compile_instancing
            #define LOOGA_TERRAIN_PATCHES 1

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #define LOOGA_TERRAIN_LIT_PASS 1
            #define LOOGA_TERRAIN_FORWARD 1
            #include "Packages/com.loogasoft.loogaterrain/Runtime/Shaders/Surface/LoogaTerrainSurfaceNativePasses.hlsl"
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            ZWrite On
            ColorMask 0

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex LoogaTerrainShadowVertex
            #pragma fragment LoogaTerrainDepthFragment
            #pragma exclude_renderers gles3 glcore
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            #pragma multi_compile_instancing
            #define LOOGA_TERRAIN_PATCHES 1

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
            #define LOOGA_TERRAIN_DEPTH_PASS 1
            #include "Packages/com.loogasoft.loogaterrain/Runtime/Shaders/Surface/LoogaTerrainSurfaceNativePasses.hlsl"
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On
            ColorMask R

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex LoogaTerrainDepthVertex
            #pragma fragment LoogaTerrainDepthFragment
            #pragma exclude_renderers gles3 glcore
            #pragma multi_compile_instancing
            #define LOOGA_TERRAIN_PATCHES 1

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
            #define LOOGA_TERRAIN_DEPTH_PASS 1
            #include "Packages/com.loogasoft.loogaterrain/Runtime/Shaders/Surface/LoogaTerrainSurfaceNativePasses.hlsl"
            ENDHLSL
        }

        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }
            ZWrite On

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex LoogaTerrainDepthVertex
            #pragma fragment LoogaTerrainDepthNormalsFragment
            #pragma exclude_renderers gles3 glcore
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/RenderingLayers.hlsl"
            #pragma multi_compile_instancing
            #define LOOGA_TERRAIN_PATCHES 1

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
            #define LOOGA_TERRAIN_DEPTH_PASS 1
            #include "Packages/com.loogasoft.loogaterrain/Runtime/Shaders/Surface/LoogaTerrainSurfaceNativePasses.hlsl"
            ENDHLSL
        }
    }
}
