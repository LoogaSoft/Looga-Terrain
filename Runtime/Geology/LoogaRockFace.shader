Shader "Looga/Rock Face"
{
    // Rock skin of LoogaRockFace. The lit pass is in LoogaRockFaceForward.hlsl. A project shader can wrap the same
    // includes and add a terrain ground hook.
    Properties
    {
        [Header(Rock Texture)]
        _BaseColor("Tint", Color) = (1, 1, 1, 1)
        [NoScaleOffset] _BaseMap("Albedo", 2D) = "white" {}
        [Normal][NoScaleOffset] _NormalMap("Normal", 2D) = "bump" {}
        [NoScaleOffset] _MaskMap("Mask (R metal, G AO, B height, A smooth)", 2D) = "white" {}
        _Tiling("Texture Size (Meters)", Float) = 6
        _NormalStrength("Normal Strength", Range(0, 2)) = 1
        _TriplanarSharpness("Triplanar Sharpness", Range(1, 16)) = 4
        _TopNormalStrength("Floor Normal Strength", Range(0, 1)) = 1
        _SmoothnessMin("Smoothness Min", Range(0, 1)) = 0
        _SmoothnessMax("Smoothness Max", Range(0, 1)) = 0.35
        _OcclusionStrength("Texture Occlusion", Range(0, 1)) = 1

        [Header(Anti Tiling)]
        _MacroScale("Second Scale", Range(1.5, 6)) = 3.1
        _MacroBlendScale("Blend Noise Size (Meters)", Float) = 14
        _HeightBlend("Height Blend", Range(0, 2)) = 0.8
        _FacetShading("Facet Shading", Range(0, 1)) = 0
        _FacetFade("Facet Shading Fade (Meters)", Float) = 30
        _FractalStrength("Fractal Detail Depth (Meters)", Range(0, 0.2)) = 0
        _FractalScale("Fractal Detail Size (Meters)", Float) = 0.5
        _FractalStretch("Fractal Detail Stretch Along Layers", Range(1, 6)) = 2.5
        _FractalFade("Fractal Detail Fade (Meters)", Float) = 25

        [Header(Variation)]
        _BandColorA("Layer Color A", Color) = (1.06, 0.98, 0.9, 1)
        _BandColorB("Layer Color B", Color) = (0.84, 0.76, 0.68, 1)
        _BandStrength("Layer Color Strength", Range(0, 1)) = 0.7
        _HardBrightness("Hard Layer Brightness", Range(0.5, 1.5)) = 1.1
        _PatchSize("Patch Size (Meters)", Float) = 35
        _PatchStrength("Patch Strength", Range(0, 0.5)) = 0.14
        _CavityStrength("Cavity Darkening", Range(0, 1)) = 0.6

        [Header(World Strata)]
        // On: the layer value and the hardness come from the world position, so meshes that meet show the same layers.
        // Off: they come from the vertex colors of each mesh.
        [ToggleUI] _WorldStrata("World Strata", Float) = 0
        _StrataThickness("Layer Thickness (Meters)", Float) = 2.5
        _StrataWarp("Layer Warp (Meters)", Float) = 2.5
        _StrataHardShare("Hard Layer Share", Range(0, 1)) = 0.4
        _StrataContinuity("Hard Layer Length (Meters)", Float) = 14

        [Header(Dust)]
        _DustColor("Dust Color", Color) = (0.62, 0.5, 0.4, 1)
        _DustAmount("Dust Amount", Range(0, 1)) = 0.55
        _DustStart("Dust Start (Normal Y)", Range(0, 1)) = 0.55
        [Header(Mineral Stains)]
        // Soft patches of mineral color that run down steep faces. 0 strength turns them off.
        _StainStrength("Stain Strength", Range(0, 1)) = 0
        _StainColorA("Stain Color A (Warm)", Color) = (0.78, 0.40, 0.18, 1)
        _StainColorB("Stain Color B (Warm Pale)", Color) = (0.82, 0.66, 0.36, 1)
        _StainColorC("Stain Color C (Cool)", Color) = (0.46, 0.53, 0.42, 1)
        _StainColorD("Stain Color D (Cool Dark)", Color) = (0.40, 0.45, 0.53, 1)
        _StainScale("Stain Size (Meters)", Float) = 14
        _StainStretch("Stain Vertical Stretch", Range(1, 12)) = 5
        _StainContrast("Stain Contrast", Range(0.5, 6)) = 2
        _StainFlat("Stain On Flat Faces", Range(0, 1)) = 0
        _StreakStrength("Wash Streaks", Range(0, 1)) = 0.5

        [Header(Moss)]
        // Moss on faces that look up, in cavities and in drips below ledges. 0 amount turns it off.
        _MossAmount("Moss Amount", Range(0, 1)) = 0
        _MossColorA("Moss Color A", Color) = (0.20, 0.30, 0.10, 1)
        _MossColorB("Moss Color B", Color) = (0.32, 0.38, 0.16, 1)
        _MossStart("Moss Start (Normal Y)", Range(-1, 1)) = 0.35
        _MossCavity("Moss In Cavities", Range(0, 2)) = 0.8
        _MossDrips("Moss Drips", Range(0, 1)) = 0.4
        _MossScale("Moss Patch Size (Meters)", Float) = 4
        [ToggleUI] _MossTextured("Moss Texture Set", Float) = 0
        [NoScaleOffset] _MossMap("Moss Albedo (A Smoothness)", 2D) = "white" {}
        [NoScaleOffset][Normal] _MossNormalMap("Moss Normal", 2D) = "bump" {}
        [NoScaleOffset] _MossMaskMap("Moss Mask (G AO, B Height, A Smoothness)", 2D) = "white" {}
        _MossTiling("Moss Texture Size (Meters)", Float) = 1.5
        _MossTint("Moss Tint", Color) = (1, 1, 1, 1)
        _MossHeightBlend("Moss Height Blend", Range(0, 1)) = 0.5
        _MossEdge("Moss Edge Sharpness", Range(1, 16)) = 6
        _MossNormalStrength("Moss Normal Strength", Range(0, 2)) = 1
        _MossCushion("Moss Cushion Edge", Range(0, 1)) = 0.5
        _MossSheen("Moss Sheen", Range(0, 1)) = 0.4
        _MossSunAvoid("Moss Avoids Sunlit Faces", Range(0, 1)) = 0

        [Header(Generated Rock)]
        // Weathering from the masks of LoogaRockGenerator meshes. Turn this on only on materials of generated rocks.
        [ToggleUI] _RockMasks("Generated Rock Masks", Float) = 0
        _EdgeWear("Edge Wear", Range(0, 1)) = 0.5
        _EdgeColor("Edge Color (Multiply)", Color) = (1.5, 1.45, 1.35, 1)
        _EdgeBreakup("Edge Breakup", Range(0, 1)) = 0.6
        _EdgeBreakupSize("Edge Breakup Size (Meters)", Float) = 1.2
        _EdgeSoftness("Edge Softness", Range(0.02, 0.5)) = 0.15
        _RidgeLight("Ridge Lightening", Range(0, 1)) = 0.3
        _CreviceDarken("Crevice Darkening", Range(0, 1)) = 0.7
        _CreviceColor("Crevice Color (Multiply)", Color) = (0.3, 0.26, 0.22, 1)
        _ShelterDarken("Shelter Darkening", Range(0, 1)) = 0.4
        _ShelterCurve("Shelter Curve", Range(0, 1)) = 0
        _CreviceRough("Crevice Roughness", Range(0, 1)) = 0
        _DepositAmount("Deposit Amount", Range(0, 1)) = 0
        _DepositColor("Deposit Color", Color) = (0.62, 0.56, 0.3, 1)
        _DepositSize("Deposit Patch Size (Meters)", Float) = 2.5
        [NoScaleOffset] _StainMap("Stain (R rust, G olive, B streaks, A mottle)", 2D) = "black" {}
        _StainAmount("Stain Amount", Range(0, 1)) = 0
        _StainSize("Stain Map Size (Meters)", Float) = 22
        _StainRust("Stain Rust", Color) = (1.25, 0.95, 0.72, 1)
        _StainOlive("Stain Olive", Color) = (0.94, 1.03, 0.76, 1)
        _StainStreak("Stain Streak", Color) = (0.55, 0.66, 0.8, 1)
        _StainMottle("Stain Mottle", Range(0, 2)) = 0.5
        _StainHue("Stain Mottle Hue", Range(0, 1)) = 0.12
        _EdgeRound("Edge Round (Meters)", Range(0, 0.2)) = 0
        _MacroAmount("Second Texture Layer", Range(0, 1)) = 1
        _EdgeChip("Edge Chip", Range(0, 1)) = 0
        _EdgeChipSize("Edge Chip Size (Meters)", Float) = 0.15
        [Normal][NoScaleOffset] _FractureNormalMap("Surface Fracture Normal", 2D) = "bump" {}
        _FractureStrength("Surface Fracture Strength", Range(0, 2)) = 0
        _FractureSize("Surface Fracture Size (Meters)", Float) = 6
        _FractureDarken("Surface Fracture Darken", Range(0, 1)) = 0.4
        [Normal][NoScaleOffset] _GritNormalMap("Grit Normal", 2D) = "bump" {}
        _GritStrength("Grit Strength", Range(0, 2)) = 0
        _GritSize("Grit Size (Meters)", Float) = 0.6
        _EdgeFlecks("Edge Mineral Flecks", Range(0, 1)) = 0
        _EdgeFleckColor("Edge Fleck Color", Color) = (0.42, 0.41, 0.38, 1)
        [NoScaleOffset] _LineMap("Line Map (Vein, Crack, Halo)", 2D) = "black" {}
        _LineSize("Line Map Size (Meters)", Float) = 14
        [Normal][NoScaleOffset] _LineStepMap("Line Step Normal", 2D) = "bump" {}
        _LineStep("Line Step Strength", Range(0, 2)) = 0
        _LineVein("Line Vein Strength", Range(0, 1)) = 0
        _LineVeinColor("Line Vein Color", Color) = (0.4, 0.34, 0.27, 1)
        _LineCrack("Line Crack Strength", Range(0, 1)) = 0
        _LineHalo("Line Halo Strength", Range(0, 1)) = 0
        _LineHaloColor("Line Halo Color", Color) = (0.82, 0.62, 0.45, 1)
        [HideInInspector] _FractureVolume("Fracture Volume", 3D) = "black" {}
        [HideInInspector] _FractureVolumeMin("Fracture Volume Corner", Vector) = (0, 0, 0, 0)
        [HideInInspector] _FractureVolumeScale("Fracture Volume Scale", Vector) = (0, 0, 0, 0)
        [HideInInspector] _FractureVolumeRange("Fracture Volume Ranges", Vector) = (1, 1, 1, 1)
        [HideInInspector] _FaceDetail("Face Detail", 2DArray) = "black" {}
        [HideInInspector] _FaceDetailCenter("Face Detail Center", Vector) = (0, 0, 0, 0)
        [HideInInspector] _FaceDetailHalf("Face Detail Half Size", Vector) = (1, 1, 1, 0)
        _FaceDetailSlope("Face Detail Slope", Range(0, 6)) = 3
        _FaceDetailCrack("Face Detail Crack", Range(0, 1)) = 0.8
        _FaceDetailVein("Face Detail Vein", Range(0, 1)) = 0.5
        _FaceDetailVeinColor("Face Detail Vein Color", Color) = (0.5, 0.44, 0.36, 1)
        _FractureVein("Fracture Vein Strength", Range(0, 1)) = 0
        _FractureVeinShare("Fracture Vein Share", Range(0, 1)) = 0.5
        _FractureCluster("Fracture Line Clusters", Range(0, 1)) = 0
        _FractureWander("Fracture Line Wander (Meters)", Float) = 0.05
        _FractureVeinColor("Fracture Vein Color", Color) = (0.6, 0.54, 0.46, 1)
        _FractureCrack("Fracture Crack Strength", Range(0, 1)) = 0
        _FractureRust("Fracture Rust Strength", Range(0, 1)) = 0
        _FractureRustColor("Fracture Rust Color", Color) = (1.12, 0.9, 0.72, 1)
        _BlockTone("Block Tone", Range(0, 0.5)) = 0
        // Thin crack lines of a LoogaRockGenerator Cracks pass. 0 width turns them off.
        _CrackLineWidth("Crack Line Half Width (Meters)", Range(0, 0.05)) = 0
        _CrackLineDarken("Crack Line Darken", Range(0, 1)) = 0.75
        _LedgeStreak("Ledge Streak Strength", Range(0, 1)) = 0
        _LedgeStreakLength("Ledge Streak Length (Meters)", Float) = 2.5
        _FarGrain("Far Grain", Range(0, 2)) = 0
        _FarGrainClump("Far Grain Zones", Range(0, 1)) = 0
        _FarGrainFlecks("Far Grain Flecks", Range(0, 1)) = 1
        _FarGrainStretch("Far Grain Stretch Along Height", Range(1, 4)) = 1
        _PlaneValue("Plane Value", Range(0, 0.5)) = 0
        _PlaneFrequency("Plane Value Frequency", Float) = 14
        _FarGrainSize("Far Grain Tile Size (Meters)", Float) = 30
        _FarGrainStart("Far Grain Start (Meters For Each Pixel)", Float) = 0.003
        _FarGrainEnd("Far Grain End (Meters For Each Pixel)", Float) = 0.009

        [Header(Terrain Ground)]
        [Toggle(_GROUND_TERRAIN)] _GroundTerrain("Blend Into Terrain Ground", Float) = 0
        [NoScaleOffset] _GroundAlbedoArray("Terrain Albedo Array", 2DArray) = "" {}
        [NoScaleOffset] _GroundNormalArray("Terrain Normal Array (normal AG, smooth R)", 2DArray) = "" {}
        [NoScaleOffset] _GroundLayerProps("Terrain Layer Properties", 2D) = "black" {}
        [NoScaleOffset] _GroundMaskArray("Terrain Mask Array (Looga quality layout)", 2DArray) = "" {}
        // The rock face sets these for each renderer when the terrain below draws with a Looga terrain surface.
        [HideInInspector] _GroundLayout("Terrain Ground Layout", Float) = 0
        [HideInInspector] _GroundSurfaceUV("Terrain Surface UV", Vector) = (1, 1, 0, 0)
        [HideInInspector] _GroundLuminance("Terrain Saturation Luminance", Vector) = (0.2126, 0.7152, 0.0722, 0)
        [HideInInspector] _LoogaWorldColor("Terrain World Color", Vector) = (0, 0, 0, 0)
        [HideInInspector] _LoogaWorldColor2("Terrain World Color 2", Vector) = (0, 0, 0, 0)
        [HideInInspector] _LoogaGroundCover("Terrain Ground Cover", Vector) = (0, 0, 0, 0)
        [HideInInspector] _LoogaGroundCoverShade("Terrain Ground Cover Shade", Vector) = (0, 0, 0, 0)
        [HideInInspector] _LoogaGroundCoverSoil("Terrain Ground Cover Soil", Vector) = (1, 0, 0, 0)
        [HideInInspector] _LoogaGroundCoverView("Terrain Ground Cover View", Vector) = (0, 0, 0, 0)
        [HideInInspector] _LoogaGroundCoverDense("Terrain Ground Cover Dense", Vector) = (0, 1, 0, 0)
        [HideInInspector] _LoogaGroundCoverDetail("Terrain Ground Cover Detail", Vector) = (0, 0, 0, 0)
        [HideInInspector] _LoogaCaveShade("Cave Shade", Float) = 0
        _GroundTriplanarScale("Terrain Triplanar UV Scale", Vector) = (10, 10, 0, 0)
        _GroundTriplanarContrast("Terrain Triplanar Contrast", Range(1, 8)) = 4
        _GroundColor("Ground Tint", Color) = (1, 1, 1, 1)
        _GroundNormalStrength("Ground Normal Strength", Range(0, 2)) = 1
        _GroundContrast("Ground Edge Width", Range(0.05, 1)) = 0.7
        _GroundHeightBlend("Ground Height Blend", Range(0, 2)) = 0
        // Dust from the terrain ground on the rock base. Only terrain layers with Object Dust give dust.
        _GroundDustAmount("Ground Dust Strength", Range(0, 1)) = 0.6
        _GroundDustReach("Ground Dust Reach (Share Of The Blend Height)", Range(0.1, 1)) = 0.5

        [Header(Asset Textures)]
        // Draws a rock asset with its own UV textures instead of the triplanar rock texture. Red of the mask map then
        // marks the ground (for example the grass top of the asset): there the shader shows the terrain ground.
        [Toggle(_ROCK_UV)] _RockUV("Asset UV Textures (Mask Red = Ground)", Float) = 0
        // Set by LoogaMeshGroundCover for each renderer: xyz the terrain point beside the asset, w 1. The ground shows
        // the terrain layers of that point.
        [HideInInspector] _RockGroundCenter("Rock Ground Centre", Vector) = (0, 0, 0, 0)
        // Ground also on faces that look up: full above this normal Y, none 0.15 below it. 0 = off. For assets whose
        // grass top comes from the slope and not from a texture.
        _GroundUpStart("Asset Ground On Up Faces (Normal Y, 0 = Off)", Range(0, 1)) = 0
        // Ground fringe: the rock around the asset ground takes a moss and soil tint, so the rock does not meet the
        // ground at a hard color step. A coarse mip of mask red spreads the ground past its edge; the mip sets the
        // width. The tint multiplies the rock at twice its value. 0 = off.
        _GroundFringe("Ground Fringe Strength (0 = Off)", Range(0, 1)) = 0
        _GroundFringeColor("Ground Fringe Tint (x2)", Color) = (0.42, 0.44, 0.3, 1)
        _GroundFringeMip("Ground Fringe Width (Mask Mip)", Range(0, 8)) = 5
        // Tiled rock layer: a rock texture set repeated over the asset UV, as the detail maps of a lit material. The
        // albedo and mask multiply into the asset textures (mid grey albedo = no change); some assets keep only
        // shading in their own albedo and take the color from a tiled rock. The normal adds fine grain. Alpha of
        // the area map at the asset UV limits the layer. Both strengths 0 = off.
        _RockDetailStrength("Tiled Rock Albedo Strength (0 = Off)", Range(0, 1)) = 0
        _RockDetailNormalStrength("Tiled Rock Normal Strength (0 = Off)", Range(0, 2)) = 0
        [NoScaleOffset] _RockDetailMap("Tiled Rock Albedo", 2D) = "grey" {}
        [Normal][NoScaleOffset] _RockDetailNormalMap("Tiled Rock Normal", 2D) = "bump" {}
        [NoScaleOffset] _RockDetailMaskMap("Tiled Rock Mask (G AO, A Smoothness)", 2D) = "white" {}
        [NoScaleOffset] _RockDetailAreaMap("Tiled Rock Area (A, Asset UV)", 2D) = "white" {}
        _RockDetailUV("Tiled Rock UV Scale (XY) And Offset (ZW)", Vector) = (8, 8, 0, 0)
        _RockDetailColor("Tiled Rock Tint", Color) = (1, 1, 1, 1)
    }

    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Opaque" "Queue"="Geometry" }

        HLSLINCLUDE
        #include "Packages/com.loogasoft.loogaterrain/Runtime/Geology/LoogaRockFaceInput.hlsl"
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            // Forward only: the project renders Deferred+, and this pass has no GBuffer version.
            Tags { "LightMode"="UniversalForwardOnly" }

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex RockVertex
            #pragma fragment RockFragment
            #pragma shader_feature_local_fragment _ _GROUND_TERRAIN
            #pragma shader_feature_local _ _ROCK_UV
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
            // Looga instancing (BatchRendererGroup) draws scattered rocks with this variant.
            #pragma multi_compile _ DOTS_INSTANCING_ON
            #include "Packages/com.loogasoft.loogaterrain/Runtime/Geology/LoogaRockFaceForward.hlsl"
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
            // Looga instancing (BatchRendererGroup) draws scattered rocks with this variant.
            #pragma multi_compile _ DOTS_INSTANCING_ON
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
            #define LOOGA_ROCK_SHADOW_PASS 1
            #include "Packages/com.loogasoft.loogaterrain/Runtime/Geology/LoogaRockFaceDepth.hlsl"
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
            // Looga instancing (BatchRendererGroup) draws scattered rocks with this variant.
            #pragma multi_compile _ DOTS_INSTANCING_ON
            #include "Packages/com.loogasoft.loogaterrain/Runtime/Geology/LoogaRockFaceDepth.hlsl"
            ENDHLSL
        }

        Pass
        {
            Name "DepthNormalsOnly"
            // Forward-only objects write their normals here for SSAO and the Deferred+ normal texture.
            Tags { "LightMode"="DepthNormalsOnly" }
            ZWrite On

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex DepthNormalsVertex
            #pragma fragment DepthNormalsFragment
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/RenderingLayers.hlsl"
            #pragma multi_compile_instancing
            // Looga instancing (BatchRendererGroup) draws scattered rocks with this variant.
            #pragma multi_compile _ DOTS_INSTANCING_ON
            #include "Packages/com.loogasoft.loogaterrain/Runtime/Geology/LoogaRockFaceDepth.hlsl"
            ENDHLSL
        }
    }
    FallBack Off
}
