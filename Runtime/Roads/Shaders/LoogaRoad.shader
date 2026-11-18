Shader "Looga/Road"
{
    // Asphalt of the road slabs that LoogaRoadSlabs builds. Every texture projects from above in world XZ. The GBuffer
    // pass writes a Looga Lite material, so Looga Lighting lights the road. The surface is in LoogaRoadSurface.hlsl.
    Properties
    {
        [Header(Asphalt)]
        _BaseColor("Tint", Color) = (0.62, 0.62, 0.62, 1)
        [NoScaleOffset] _BaseMap("Albedo (A height)", 2D) = "white" {}
        [Normal][NoScaleOffset] _NormalMap("Normal", 2D) = "bump" {}
        [NoScaleOffset] _MaskMap("Mask (R metal, G AO, A smooth)", 2D) = "white" {}
        _TextureSize("Texture Size (Meters)", Float) = 2.86
        _NormalStrength("Normal Strength", Range(0, 2)) = 1
        _SmoothnessOffset("Smoothness Offset", Range(-1, 1)) = -0.35
        _Metallic("Metallic", Range(0, 1)) = 0

        [Header(Anti Tiling)]
        [Toggle(_ANTI_TILING)] _AntiTiling("Random Offset Cells", Float) = 1
        _CellSize("Cell Size (Meters)", Float) = 4.3
        _CellBlend("Cell Height Blend", Range(0.01, 1)) = 0.3

        [Header(Variation Noise)]
        [NoScaleOffset] _NoiseMap("Noise (R wear A, G wear B and patches)", 2D) = "gray" {}
        _NoiseSize("Wear Noise Size (Meters)", Float) = 50

        [Header(Wear A)]
        [Toggle(_WEAR_A)] _WearA("Wear A", Float) = 0
        [Normal][NoScaleOffset] _WearNormalA("Detail Normal", 2D) = "bump" {}
        _WearSizeA("Detail Size (Meters)", Float) = 2.5
        _WearAmountA("Amount", Range(0, 3)) = 2
        _WearContrastA("Noise Contrast", Range(0, 4)) = 1.6
        _WearNormalStrengthA("Normal Strength", Range(0, 1)) = 0.64
        _WearSmoothnessA("Smoothness Change", Range(-1, 1)) = 0.195
        _WearOcclusionA("Occlusion Change", Range(-1, 1)) = 0.16
        _WearTintA("Tint", Color) = (0.44, 0.44, 0.44, 1)

        [Header(Wear B)]
        [Toggle(_WEAR_B)] _WearB("Wear B", Float) = 0
        [Normal][NoScaleOffset] _WearNormalB("Detail Normal", 2D) = "bump" {}
        _WearSizeB("Detail Size (Meters)", Float) = 4.5
        _WearAmountB("Amount", Range(0, 3)) = 1.4
        _WearContrastB("Noise Contrast", Range(0, 4)) = 0.4
        _WearNormalStrengthB("Normal Strength", Range(0, 1)) = 1
        _WearSmoothnessB("Smoothness Change", Range(-1, 1)) = 0
        _WearOcclusionB("Occlusion Change", Range(-1, 1)) = 0
        _WearTintB("Tint", Color) = (0.5, 0.5, 0.5, 1)

        [Header(Patches)]
        [Toggle(_PATCHES)] _Patches("Patches", Float) = 0
        [NoScaleOffset] _PatchMap("Albedo (A cover)", 2D) = "black" {}
        [Normal][NoScaleOffset] _PatchNormal("Normal", 2D) = "bump" {}
        [NoScaleOffset] _PatchMask("Mask (R metal, G AO, A smooth)", 2D) = "white" {}
        _PatchSize("Texture Size (Meters)", Float) = 7.7
        _PatchSpread("Spread Noise Size (Meters)", Float) = 90
        _PatchAmount("Amount", Range(0, 2)) = 1

        [Header(Road Wear)]
        // Needs the road coordinates of the slabs (second UV set, LoogaRoadSlabs). Repairs use the patch textures.
        [Toggle(_ROAD_WEAR)] _RoadWear("Road Wear", Float) = 0
        _MacroVariation("Large Variation", Range(0, 0.5)) = 0.12
        _Age("Age (Oxidized Gray)", Range(0, 1)) = 0.4
        // The mean color of fully aged asphalt. The asphalt texture keeps its detail around it.
        _AgeColor("Aged Color", Color) = (0.33, 0.32, 0.3, 1)
        _TrackOffset("Wheel Track Offset (Meters)", Float) = 0.9
        _TrackWidth("Wheel Track Width (Meters)", Float) = 0.3
        _TrackDarken("Wheel Track Darkening", Range(-0.5, 0.5)) = 0.05
        _TrackSmoothness("Wheel Track Polish", Range(0, 0.5)) = 0.08
        _OilWidth("Oil Stripe Width (Meters)", Float) = 0.35
        _OilDarken("Oil Stripe Darkening", Range(0, 0.5)) = 0.12
        _EdgeWidth("Edge Band Widest (Meters From Rim)", Float) = 1.8
        _EdgeWidthMin("Edge Band Narrowest (Meters From Rim)", Float) = 0.05
        _EdgeChunkSize("Edge Chunk Size (Meters)", Float) = 0.35
        _EdgeColor("Edge Band Color Without Terrain (A amount)", Color) = (0.42, 0.37, 0.31, 0.6)
        _CrackDistance("Crack Distance Visibility", Range(0, 1)) = 0.7
        _CrackWidth("Crack Width (Meters)", Float) = 0.012
        _CrackDepth("Crack Depth (Meters)", Float) = 0.006
        _CrackDarken("Crack Darkening", Range(0, 1)) = 0.55
        _AlligatorSize("Fatigue Block Size (Meters)", Float) = 0.3
        _AlligatorStart("Fatigue Wear Start", Range(0, 1)) = 0.55
        _AlligatorTilt("Fatigue Block Tilt", Range(0, 0.3)) = 0.08
        _RimBrighten("Ravelled Rim Brightening", Range(0, 1)) = 0.25
        _BlockSize("Block Crack Size (Meters)", Float) = 2
        _BlockStart("Block Crack Wear Start", Range(0, 1)) = 0.55
        _SealWidth("Repair Seam Width (Meters)", Float) = 0.04
        _SealColor("Repair Seam Color (A amount)", Color) = (0.035, 0.033, 0.03, 1)
        _SealSmoothness("Seal Smoothness", Range(0, 1)) = 0.55
        _RepairSpacing("Repair Spacing (Meters)", Float) = 45
        _RepairChance("Repair Chance", Range(0, 1)) = 0.35

        [Header(Terrain Ground)]
        // The edge band shows the terrain beside the road. LoogaRoadNetwork sets the terrain values for each slab.
        [Toggle(_GROUND_TERRAIN)] _GroundTerrain("Edge Band From Terrain", Float) = 0
        [NoScaleOffset] _GroundAlbedoArray("Terrain Albedo Array", 2DArray) = "" {}
        [NoScaleOffset] _GroundNormalArray("Terrain Normal Array (normal AG, smooth R)", 2DArray) = "" {}
        [NoScaleOffset] _GroundLayerProps("Terrain Layer Properties", 2D) = "black" {}
        [NoScaleOffset] _GroundMaskArray("Terrain Mask Array (Looga quality layout)", 2DArray) = "" {}
        [HideInInspector] _GroundLayout("Terrain Ground Layout", Float) = 0
        [HideInInspector] _GroundSurfaceUV("Terrain Surface UV", Vector) = (1, 1, 0, 0)
        [HideInInspector] _GroundLuminance("Terrain Saturation Luminance", Vector) = (0.2126, 0.7152, 0.0722, 0)
        _GroundTriplanarScale("Terrain Triplanar UV Scale", Vector) = (10, 10, 0, 0)
        _GroundTriplanarContrast("Terrain Triplanar Contrast", Range(1, 8)) = 4
        _GroundColor("Ground Tint", Color) = (1, 1, 1, 1)
        _GroundNormalStrength("Ground Normal Strength", Range(0, 2)) = 1
        _GroundReach("Ground Sample Reach Past Edge (Meters)", Float) = 0.75
        // The slab lip moves toward the camera by this part of the view distance at its outer end.
        _LipDepthOffset("Lip Depth Offset (Part Of View Distance)", Range(0, 0.005)) = 0.0005
    }

    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Opaque" "Queue"="Geometry" }

        HLSLINCLUDE
        #include "Packages/com.loogasoft.loogaterrain/Runtime/Roads/Shaders/LoogaRoadInput.hlsl"
        ENDHLSL

        Pass
        {
            Name "GBuffer"
            Tags { "LightMode"="UniversalGBuffer" }
            // Looga material class. URP deferred lighting skips these pixels. Looga Lighting lights them.
            Stencil
            {
                Ref 96
                Comp Always
                Pass Replace
                WriteMask 96
            }

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex RoadVertex
            #pragma fragment RoadGBufferFragment
            #pragma shader_feature_local_fragment _ _ANTI_TILING
            #pragma shader_feature_local_fragment _ _WEAR_A
            #pragma shader_feature_local_fragment _ _WEAR_B
            #pragma shader_feature_local_fragment _ _PATCHES
            #pragma shader_feature_local_fragment _ _ROAD_WEAR
            #pragma shader_feature_local_fragment _ _GROUND_TERRAIN
            #pragma multi_compile_fragment _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ EVALUATE_SH_MIXED EVALUATE_SH_VERTEX
            #pragma multi_compile_fragment _ _SCREEN_SPACE_IRRADIANCE
            #pragma multi_compile _ SHADOWS_SHADOWMASK
            #pragma multi_compile_fragment _ _RENDER_PASS_ENABLED
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT
            #pragma multi_compile_fragment _ _DBUFFER_MRT1 _DBUFFER_MRT2 _DBUFFER_MRT3
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/RenderingLayers.hlsl"
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/ProbeVolumeVariants.hlsl"
            #pragma multi_compile_instancing
            #pragma require cubearray
            #include "Packages/com.loogasoft.loogaterrain/Runtime/Roads/Shaders/LoogaRoadGBuffer.hlsl"
            ENDHLSL
        }

        Pass
        {
            Name "ForwardLit"
            // Forward rendering and the passes that render without the GBuffer, for example reflection probes.
            Tags { "LightMode"="UniversalForward" }

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex RoadVertex
            #pragma fragment RoadForwardFragment
            #pragma shader_feature_local_fragment _ _ANTI_TILING
            #pragma shader_feature_local_fragment _ _WEAR_A
            #pragma shader_feature_local_fragment _ _WEAR_B
            #pragma shader_feature_local_fragment _ _PATCHES
            #pragma shader_feature_local_fragment _ _ROAD_WEAR
            #pragma shader_feature_local_fragment _ _GROUND_TERRAIN
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
            #include "Packages/com.loogasoft.loogaterrain/Runtime/Roads/Shaders/LoogaRoadSurface.hlsl"
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
            #pragma vertex ShadowPassVertex
            #pragma fragment ShadowPassFragment
            #pragma multi_compile_instancing
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            #include "Packages/com.unity.render-pipelines.universal/Shaders/ShadowCasterPass.hlsl"
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
            #pragma vertex RoadDepthVertex
            #pragma fragment RoadDepthFragment
            #pragma multi_compile_instancing

            struct RoadDepthAttributes
            {
                float4 positionOS : POSITION;
                // Green: the place on the slab lip (LoogaRoadSlabs).
                half4 joins : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            float4 RoadDepthVertex(RoadDepthAttributes input) : SV_POSITION
            {
                UNITY_SETUP_INSTANCE_ID(input);
                float3 positionWS = RoadLipPosition(TransformObjectToWorld(input.positionOS.xyz), input.joins.g);
                return TransformWorldToHClip(positionWS);
            }

            half RoadDepthFragment() : SV_Target
            {
                return 0;
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthNormals"
            // The depth normal prepass of forward rendering and SSAO.
            Tags { "LightMode"="DepthNormals" }
            ZWrite On

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex RoadVertex
            #pragma fragment RoadDepthNormalsFragment
            #pragma shader_feature_local_fragment _ _ANTI_TILING
            #pragma shader_feature_local_fragment _ _WEAR_A
            #pragma shader_feature_local_fragment _ _WEAR_B
            #pragma shader_feature_local_fragment _ _PATCHES
            #pragma shader_feature_local_fragment _ _ROAD_WEAR
            #pragma shader_feature_local_fragment _ _GROUND_TERRAIN
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/RenderingLayers.hlsl"
            #pragma multi_compile_instancing
            #include "Packages/com.loogasoft.loogaterrain/Runtime/Roads/Shaders/LoogaRoadSurface.hlsl"

            void RoadDepthNormalsFragment(Varyings input, out half4 outNormalWS : SV_Target0
            #ifdef _WRITE_RENDERING_LAYERS
                , out uint outRenderingLayers : SV_Target1
            #endif
            )
            {
                UNITY_SETUP_INSTANCE_ID(input);
                half3 albedo;
                half3 normalWS;
                half smoothness;
                half occlusion;
                half metallic;
                RoadSurface(input, albedo, normalWS, smoothness, occlusion, metallic);
            #if defined(_GBUFFER_NORMALS_OCT)
                float2 octNormalWS = PackNormalOctQuadEncode(normalWS);
                outNormalWS = half4(PackFloat2To888(saturate(octNormalWS * 0.5 + 0.5)), 0.0);
            #else
                outNormalWS = half4(normalWS, 0.0);
            #endif
            #ifdef _WRITE_RENDERING_LAYERS
                outRenderingLayers = EncodeMeshRenderingLayer();
            #endif
            }
            ENDHLSL
        }
    }
    FallBack Off
}
