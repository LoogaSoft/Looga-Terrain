Shader "Looga/Terrain/Vegetation Lit"
{
    Properties
    {
        [MainTexture] _BaseMap("Albedo", 2D) = "white" {}
        [MainColor] _BaseColor("Color", Color) = (1,1,1,1)
        [Toggle(_ALPHATEST_ON)] _AlphaClip("Alpha Clip", Float) = 1
        _Cutoff("Alpha Cutoff", Range(0,1)) = 0.5
        _Smoothness("Smoothness", Range(0,1)) = 0.2
        [Toggle(_NORMALMAP)] _NormalMapEnabled("Use Normal Map", Float) = 0
        [Normal] _BumpMap("Normal Map", 2D) = "bump" {}
        _BumpScale("Normal Scale", Float) = 1
        [Enum(UnityEngine.Rendering.CullMode)] _Cull("Cull", Float) = 0
        _WindParams("Wind: amplitude, speed, height, base", Vector) = (0.3,1.5,2,0)
        _WindDirection("Wind: local X, Z", Vector) = (1,0,0,0)
        _WindTime("Wind Time (-1 uses game time)", Float) = -1
        [Toggle] _Billboard("Cylindrical billboard", Float) = 0
        _GroundParams("Ground: tint, normal, conformance limit, blend height", Vector) = (0,0,0,1)
        _InteractionParams("Interaction: maximum local displacement", Vector) = (0,0,0,0)
        _BranchWind("Branch: amplitude, frequency, height phase", Vector) = (0,2,3,0)
        _LeafWind("Leaf: amplitude, frequency", Vector) = (0,5,0,0)
        [Toggle] _VertexWindMask("Vertex RGB stiffness masks", Float) = 0
        _GustSheen("Gust sheen: brightness, gloss, view dependence", Vector) = (0,0,0.6,0)
        [HideInInspector] _LoogaLightmapColor("Baked illumination color", Color) = (1,1,1,1)
        [HideInInspector] _LoogaInstanceData("Instance custom data", Vector) = (0,0,0,0)
        [HideInInspector] _Surface("Surface", Float) = 0
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "LoogaInstanceAppearance"="Vegetation" "LoogaInstanceDeformation"="Vegetation" "RenderType"="TransparentCutout" "Queue"="AlphaTest" "UniversalMaterialType"="Lit" "AlwaysRenderMotionVectors"="true" }
        Cull [_Cull]
        ZWrite On
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode"="UniversalForward" }
            HLSLPROGRAM
            #pragma vertex VegetationVertex
            #pragma fragment LitPassFragment
            #pragma target 4.5
            #pragma multi_compile _ LOOGA_GROUND_CACHE
            #pragma multi_compile_instancing
            #pragma multi_compile _ LOD_FADE_CROSSFADE
            #pragma shader_feature_local _ALPHATEST_ON
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"
            #include "../Shaders/VegetationInput.hlsl"
            #pragma shader_feature_local _NORMALMAP
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ EVALUATE_SH_MIXED EVALUATE_SH_VERTEX
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/RenderingLayers.hlsl"
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Fog.hlsl"
            #pragma multi_compile _ LIGHTMAP_SHADOW_MIXING
            #pragma multi_compile _ SHADOWS_SHADOWMASK
            #pragma multi_compile _ DIRLIGHTMAP_COMBINED
            #pragma multi_compile _ LIGHTMAP_ON
            #pragma multi_compile _ DYNAMICLIGHTMAP_ON
            #pragma multi_compile _ USE_LEGACY_LIGHTMAPS
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BOX_PROJECTION
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_ATLAS
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/ProbeVolumeVariants.hlsl"
            #include "Packages/com.loogasoft.loogainstancing/ShaderLibrary/VegetationLighting.hlsl"
            #define REQUIRES_WORLD_SPACE_POS_INTERPOLATOR
            #define InitializeStandardLitSurfaceData(uv, surface) InitializeStandardLitSurfaceData(uv, surface); VegetationGroundBlend(input.positionWS, input.normalWS, surface); VegetationGustSheen(input.positionWS, _GustSheen, surface)
            #include "Packages/com.unity.render-pipelines.universal/Shaders/LitForwardPass.hlsl"
            Varyings VegetationVertex(LoogaVegetationAttributes source)
            {
                Attributes input = (Attributes)0;
                UNITY_TRANSFER_INSTANCE_ID(source, input);
                input.positionOS = source.positionOS;
                input.normalOS = source.normalOS;
                input.tangentOS = source.tangentOS;
                input.texcoord = source.texcoord;
                input.staticLightmapUV = source.staticLightmapUV;
                input.dynamicLightmapUV = source.dynamicLightmapUV;
                UNITY_SETUP_INSTANCE_ID(input);
                input.positionOS.xyz = VegetationOrient(input.positionOS.xyz, UNITY_MATRIX_I_M);
                float2 derivative = VegetationBend(input.positionOS.xyz, source.color);
                input.normalOS = VegetationOrient(input.normalOS, UNITY_MATRIX_I_M);
                VegetationNormal(input.normalOS, derivative);
                input.tangentOS.xyz = VegetationOrient(input.tangentOS.xyz, UNITY_MATRIX_I_M);
                VegetationTangent(input.tangentOS, derivative);
                VegetationGroundFrame(input.positionOS.xyz, input.normalOS, input.tangentOS);
                return LitPassVertex(input);
            }
            ENDHLSL
        }
        Pass
        {
            Name "GBuffer"
            Tags { "LightMode"="UniversalGBuffer" }
            HLSLPROGRAM
            #pragma vertex VegetationVertex
            #pragma fragment LitGBufferPassFragment
            #pragma target 4.5
            #pragma multi_compile _ LOOGA_GROUND_CACHE
            #pragma multi_compile_instancing
            #pragma multi_compile _ LOD_FADE_CROSSFADE
            #pragma shader_feature_local _ALPHATEST_ON
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"
            #include "../Shaders/VegetationInput.hlsl"
            #pragma shader_feature_local _NORMALMAP
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ EVALUATE_SH_MIXED EVALUATE_SH_VERTEX
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/RenderingLayers.hlsl"
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT
            #pragma multi_compile_fragment _ _RENDER_PASS_ENABLED
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/GBufferOutputFormat.hlsl"
            #pragma multi_compile _ LIGHTMAP_SHADOW_MIXING
            #pragma multi_compile _ SHADOWS_SHADOWMASK
            #pragma multi_compile _ DIRLIGHTMAP_COMBINED
            #pragma multi_compile _ LIGHTMAP_ON
            #pragma multi_compile _ DYNAMICLIGHTMAP_ON
            #pragma multi_compile _ USE_LEGACY_LIGHTMAPS
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BOX_PROJECTION
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_ATLAS
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/ProbeVolumeVariants.hlsl"
            #include "Packages/com.loogasoft.loogainstancing/ShaderLibrary/VegetationLighting.hlsl"
            #define REQUIRES_WORLD_SPACE_POS_INTERPOLATOR
            #define InitializeStandardLitSurfaceData(uv, surface) InitializeStandardLitSurfaceData(uv, surface); VegetationGroundBlend(input.positionWS, input.normalWS, surface); VegetationGustSheen(input.positionWS, _GustSheen, surface)
            #include "Packages/com.unity.render-pipelines.universal/Shaders/LitGBufferPass.hlsl"
            Varyings VegetationVertex(LoogaVegetationAttributes source)
            {
                Attributes input = (Attributes)0;
                UNITY_TRANSFER_INSTANCE_ID(source, input);
                input.positionOS = source.positionOS;
                input.normalOS = source.normalOS;
                input.tangentOS = source.tangentOS;
                input.texcoord = source.texcoord;
                input.staticLightmapUV = source.staticLightmapUV;
                input.dynamicLightmapUV = source.dynamicLightmapUV;
                UNITY_SETUP_INSTANCE_ID(input);
                input.positionOS.xyz = VegetationOrient(input.positionOS.xyz, UNITY_MATRIX_I_M);
                float2 derivative = VegetationBend(input.positionOS.xyz, source.color);
                input.normalOS = VegetationOrient(input.normalOS, UNITY_MATRIX_I_M);
                VegetationNormal(input.normalOS, derivative);
                input.tangentOS.xyz = VegetationOrient(input.tangentOS.xyz, UNITY_MATRIX_I_M);
                VegetationTangent(input.tangentOS, derivative);
                VegetationGroundFrame(input.positionOS.xyz, input.normalOS, input.tangentOS);
                return LitGBufferPassVertex(input);
            }
            ENDHLSL
        }
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode"="ShadowCaster" }
            ColorMask 0
            HLSLPROGRAM
            #pragma vertex VegetationVertex
            #pragma fragment ShadowPassFragment
            #pragma target 4.5
            #pragma multi_compile _ LOOGA_GROUND_CACHE
            #pragma multi_compile_instancing
            #pragma multi_compile _ LOD_FADE_CROSSFADE
            #pragma shader_feature_local _ALPHATEST_ON
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"
            #include "../Shaders/VegetationInput.hlsl"
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            #include "Packages/com.unity.render-pipelines.universal/Shaders/ShadowCasterPass.hlsl"
            Varyings VegetationVertex(LoogaVegetationAttributes source)
            {
                Attributes input = (Attributes)0;
                UNITY_TRANSFER_INSTANCE_ID(source, input);
                input.positionOS = source.positionOS;
                input.normalOS = source.normalOS;
                input.texcoord = source.texcoord;
                UNITY_SETUP_INSTANCE_ID(input);
                input.positionOS.xyz = VegetationOrient(input.positionOS.xyz, UNITY_MATRIX_I_M);
                float2 derivative = VegetationBend(input.positionOS.xyz, source.color);
                input.normalOS = VegetationOrient(input.normalOS, UNITY_MATRIX_I_M);
                VegetationNormal(input.normalOS, derivative);
                return ShadowPassVertex(input);
            }
            ENDHLSL
        }
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode"="DepthOnly" }
            HLSLPROGRAM
            #pragma vertex VegetationVertex
            #pragma fragment DepthOnlyFragment
            #pragma target 4.5
            #pragma multi_compile _ LOOGA_GROUND_CACHE
            #pragma multi_compile_instancing
            #pragma multi_compile _ LOD_FADE_CROSSFADE
            #pragma shader_feature_local _ALPHATEST_ON
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"
            #include "../Shaders/VegetationInput.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/Shaders/DepthOnlyPass.hlsl"
            Varyings VegetationVertex(LoogaVegetationAttributes source)
            {
                Attributes input = (Attributes)0;
                UNITY_TRANSFER_INSTANCE_ID(source, input);
                input.position = source.positionOS;
                input.texcoord = source.texcoord;
                UNITY_SETUP_INSTANCE_ID(input);
                input.position.xyz = VegetationOrient(input.position.xyz, UNITY_MATRIX_I_M);
                float2 derivative = VegetationBend(input.position.xyz, source.color);
                return DepthOnlyVertex(input);
            }
            ENDHLSL
        }
        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode"="DepthNormals" }
            HLSLPROGRAM
            #pragma vertex VegetationVertex
            #pragma fragment DepthNormalsFragment
            #pragma target 4.5
            #pragma multi_compile _ LOOGA_GROUND_CACHE
            #pragma multi_compile_instancing
            #pragma multi_compile _ LOD_FADE_CROSSFADE
            #pragma shader_feature_local _ALPHATEST_ON
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"
            #include "../Shaders/VegetationInput.hlsl"
            #pragma shader_feature_local _NORMALMAP
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/RenderingLayers.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/Shaders/LitDepthNormalsPass.hlsl"
            Varyings VegetationVertex(LoogaVegetationAttributes source)
            {
                Attributes input = (Attributes)0;
                UNITY_TRANSFER_INSTANCE_ID(source, input);
                input.positionOS = source.positionOS;
                input.normal = source.normalOS;
                input.tangentOS = source.tangentOS;
                input.texcoord = source.texcoord;
                UNITY_SETUP_INSTANCE_ID(input);
                input.positionOS.xyz = VegetationOrient(input.positionOS.xyz, UNITY_MATRIX_I_M);
                float2 derivative = VegetationBend(input.positionOS.xyz, source.color);
                input.normal = VegetationOrient(input.normal, UNITY_MATRIX_I_M);
                VegetationNormal(input.normal, derivative);
                input.tangentOS.xyz = VegetationOrient(input.tangentOS.xyz, UNITY_MATRIX_I_M);
                VegetationTangent(input.tangentOS, derivative);
                VegetationGroundFrame(input.positionOS.xyz, input.normal, input.tangentOS);
                return DepthNormalsVertex(input);
            }
            ENDHLSL
        }
        Pass
        {
            Name "MotionVectors"
            Tags { "LightMode"="MotionVectors" }
            ColorMask RG
            HLSLPROGRAM
            #pragma vertex VegetationMotionVertex
            #pragma fragment VegetationMotionFragment
            #pragma target 4.5
            #pragma multi_compile _ LOOGA_GROUND_CACHE
            #pragma multi_compile_instancing
            #pragma multi_compile _ LOD_FADE_CROSSFADE
            #pragma shader_feature_local _ALPHATEST_ON
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"
            #include "../Shaders/VegetationMotion.hlsl"
            ENDHLSL
        }
    }
    Fallback Off
}
