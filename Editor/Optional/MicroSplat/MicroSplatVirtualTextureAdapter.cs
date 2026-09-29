using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using JBooth.MicroSplat;
using UnityEditor;
using UnityEngine;

namespace LoogaSoft.Terrain.Integrations.Editor
{
    /// <summary>
    /// Exports MicroSplat surfaces with cached base layers and live detail effects.
    /// Hybrid height transitions retain direct shading until their distance blend is complete.
    /// </summary>
    public static class MicroSplatVirtualTextureAdapter
    {
        [MenuItem("Tools/Looga/Terrain/Export Selected MicroSplat Virtual Surface")]
        private static void ExportSelected()
        {
            var source = Selection.activeGameObject ? Selection.activeGameObject.GetComponent<MicroSplatTerrain>() : null;
            if (!source) return;
            string path = EditorUtility.SaveFilePanelInProject("Export Virtual Surface", "LoogaVirtualSurface", "mat", "Choose a new material path.");
            if (string.IsNullOrEmpty(path)) return;
            Selection.activeObject = Export(source, path.Substring(0, path.Length - 4));
        }

        /// <summary>
        /// Creates a virtual surface and a matching producer at outputStem plus PageProducer.
        /// Existing exports can retain their asset GUIDs.
        /// </summary>
        public static Material Export(MicroSplatTerrain source, string outputStem, bool updateExisting = false)
        {
            if (!source || !source.matInstance)
            {
                throw new ArgumentException("A synchronized MicroSplat terrain is required.");
            }
            string sourceCode = File.ReadAllText(AssetDatabase.GetAssetPath(source.matInstance.shader));
            ValidateFeatures(sourceCode);
            string producerStem = outputStem + "PageProducer";
            if (!updateExisting && (File.Exists(producerStem + ".shader") || File.Exists(producerStem + ".mat")))
            {
                throw new IOException("The producer output path already exists.");
            }
            Shader previousShader = AssetDatabase.LoadAssetAtPath<Shader>(producerStem + ".shader");
            Material previousMaterial = AssetDatabase.LoadAssetAtPath<Material>(producerStem + ".mat");
            string producerName = "Hidden/Looga/Terrain/MicroSplat/" + Path.GetFileName(producerStem);
            if (updateExisting && ((previousShader && previousShader.name != producerName)
                || (previousMaterial && previousMaterial.shader != previousShader)))
            {
                throw new InvalidOperationException("Only matching page producer exports can be updated.");
            }
            Material surface = MicroSplatShaderAdapter.Export(source, outputStem, updateExisting);
            string code = File.ReadAllText(outputStem + ".shader");
            int programStart = code.IndexOf("HLSLPROGRAM", StringComparison.Ordinal) + "HLSLPROGRAM".Length;
            int programEnd = code.IndexOf("ENDHLSL", programStart, StringComparison.Ordinal);
            int propertiesStart = code.IndexOf("Properties", StringComparison.Ordinal);
            int propertiesEnd = code.IndexOf("SubShader", propertiesStart, StringComparison.Ordinal);
            if (programStart < 11 || programEnd < 0 || propertiesStart < 0 || propertiesEnd < 0)
            {
                throw new NotSupportedException("The generated surface layout is not supported.");
            }
            string program = code.Substring(programStart, programEnd - programStart);
            program = Regex.Replace(program, @"^\s*#pragma\s+(vertex|fragment|multi_compile\S*|shader_feature\S*|instancing_options).*$",
                "", RegexOptions.Multiline, TimeSpan.FromSeconds(1));
            program = PreparePageProgram(program);
            string bake = "Shader \"Hidden/Looga/Terrain/MicroSplat/" + Path.GetFileName(producerStem) + "\"\n{\n"
                + code.Substring(propertiesStart, propertiesEnd - propertiesStart)
                + "\nSubShader { Tags { \"RenderPipeline\"=\"UniversalPipeline\" }\n"
                + "Pass { Name \"LoogaVTBake\" Tags { \"LightMode\"=\"LoogaVTBake\" } Cull Off ZWrite Off ZTest Always\nHLSLPROGRAM\n"
                + "#pragma vertex LoogaBakeVertex\n#pragma fragment LoogaBakeFragment\n"
                + program + BakeProgram + "\nENDHLSL\n}\n}\n}\n";
            const string include = "#include \"Packages/com.loogasoft.loogaterrain/Runtime/Shaders/VirtualTexturing/VirtualTextureSampling.hlsl\"\n";
            code = code.Replace("void SurfaceFunction(inout Surface o, inout ShaderData d)",
                include + "void SurfaceFunction(inout Surface o, inout ShaderData d)");
            const string marker = "Input i = DescToInput(d);";
            if (!code.Contains(marker))
            {
                throw new NotSupportedException("The generated material sampling hook is absent.");
            }
            code = code.Replace(marker, CachedSurface + marker);
            const string surfaceEnd = "o.Alpha = l.Alpha;";
            if (!code.Contains(surfaceEnd))
            {
                throw new NotSupportedException("The MicroSplat surface-output hook is absent.");
            }
            code = code.Replace(surfaceEnd, surfaceEnd + CachedBlend);
            code = code.Replace("[HideInInspector] _LoogaTerrainSurface",
                "[HideInInspector] _LoogaVirtualTextureSurface(\"Virtual Surface\", Float) = 1\n[HideInInspector] _LoogaTerrainSurface");
            File.WriteAllText(outputStem + ".shader", code);
            File.WriteAllText(producerStem + ".shader", bake);
            AssetDatabase.ImportAsset(outputStem + ".shader", ImportAssetOptions.ForceSynchronousImport);
            AssetDatabase.ImportAsset(producerStem + ".shader", ImportAssetOptions.ForceSynchronousImport);
            Shader bakeShader = AssetDatabase.LoadAssetAtPath<Shader>(producerStem + ".shader");
            if (!bakeShader || ShaderUtil.ShaderHasError(bakeShader) || ShaderUtil.ShaderHasError(surface.shader))
            {
                throw new InvalidOperationException("The virtual surface export failed compilation.");
            }
            Material producer = AssetDatabase.LoadAssetAtPath<Material>(producerStem + ".mat");
            bool create = !producer;
            if (create)
            {
                producer = new Material(bakeShader);
            }
            producer.CopyPropertiesFromMaterial(source.matInstance);
            producer.shader = bakeShader;
            using (var hash = System.Security.Cryptography.SHA256.Create())
            {
                string schema = Convert.ToBase64String(hash.ComputeHash(System.Text.Encoding.UTF8.GetBytes(sourceCode)));
                surface.SetOverrideTag(TerrainVirtualTexture.SchemaTag, schema);
                producer.SetOverrideTag(TerrainVirtualTexture.SchemaTag, schema);
            }
            EditorUtility.SetDirty(surface);
            AssetDatabase.SaveAssetIfDirty(surface);
            if (create)
            {
                AssetDatabase.CreateAsset(producer, producerStem + ".mat");
            }
            EditorUtility.SetDirty(producer);
            AssetDatabase.SaveAssetIfDirty(producer);
            return surface;
        }

        private static string PreparePageProgram(string program)
        {
            const string cameraDistance = "float camDist = distance(_WorldSpaceCameraPos, i.worldPos);";
            if (!program.Contains(cameraDistance))
            {
                throw new NotSupportedException("The MicroSplat camera-distance hook is absent.");
            }
            // Bake only the stable endpoint of hybrid height blending.
            program = program.Replace(cameraDistance,
                "float camDist = 0;\n#if _HYBRIDHEIGHTBLEND\ncamDist = max(1.0, _HybridHeightBlendDistance);\n#endif");
            const string liveNoise = "ApplyDetailDistanceNoise(albedo.rgb, normSAO, surfGrad, config, camDist, i.worldPos, worldNormalVertex);";
            bool needsNoise = program.Contains("#define _DETAILNOISE 1") || program.Contains("#define _DISTANCENOISE 1")
                || program.Contains("#define _NORMALNOISE 1");
            if (needsNoise && !program.Contains(liveNoise))
            {
                throw new NotSupportedException("The MicroSplat live-noise hook is absent.");
            }
            // The consumer applies these effects once, using the current camera and derivatives.
            return program.Replace(liveNoise, "");
        }

        /// <summary>Rejects features outside the checked cached-base and live-effect integration.</summary>
        public static void ValidateFeatures(string code)
        {
            foreach (string feature in MicroSplatCompatibilityEditor.ReadFeatures(code))
            {
                if (MicroSplatCompatibility.IsCacheSupported(feature))
                {
                    continue;
                }
                throw new NotSupportedException("Virtual surface caching does not support " + feature
                    + ". Use the direct surface, or export a static material without that feature.");
            }
        }

        private const string CachedSurface = @"
            LoogaClipHoles(d.texcoord0.xy);
            float cacheDistance = distance(_WorldSpaceCameraPos, d.worldSpacePosition);
            float cacheBlend = 1;
            #if _HYBRIDHEIGHTBLEND
                float cacheStart = max(1.0, _HybridHeightBlendDistance);
                cacheBlend = saturate((cacheDistance - cacheStart) / max(1.0, cacheStart * 0.1));
            #endif
            float4 cachedAlbedo = 0, cachedNormal = 0, cachedEmission = 0;
            bool cacheResident = false;
            // Direct hybrid shading does not use cached samples before its transition starts.
            [branch] if (cacheBlend > 0)
            {
                cacheResident = LoogaVTRead(d.texcoord0.xy, cachedAlbedo, cachedNormal, cachedEmission);
            }
            Surface cachedSurface = (Surface)0;
            if (cacheResident)
            {
                // Recover MicroSplat's unnormalized XY normal before live noise modifies it.
                half4 liveNormal = half4(cachedNormal.xy / max(cachedNormal.z, 0.0001), cachedAlbedo.a, cachedNormal.a);
                half3 liveAlbedo = cachedAlbedo.rgb;
                #if _DETAILNOISE || _DISTANCENOISE || _NORMALNOISE
                    Config liveConfig = (Config)0;
                    liveConfig.uv = d.texcoord0.xy;
                    half3 liveGradient = 0;
                    ApplyDetailDistanceNoise(liveAlbedo, liveNormal, liveGradient, liveConfig,
                        cacheDistance, d.worldSpacePosition, worldNormalVertex);
                #endif
                cachedSurface.Albedo = liveAlbedo;
                cachedSurface.Smoothness = liveNormal.z;
                cachedSurface.Normal = half3(liveNormal.xy, 1);
                cachedSurface.Occlusion = liveNormal.w;
                cachedSurface.Emission = cachedEmission.rgb;
                cachedSurface.Metallic = cachedEmission.a;
                cachedSurface.Alpha = 1;
                if (cacheBlend >= 1)
                {
                    o = cachedSurface;
                    return;
                }
            }
";
        private const string CachedBlend = @"
            if (cacheResident)
            {
                // Hide filtering differences across the direct-to-cache transition.
                o.Albedo = lerp(o.Albedo, cachedSurface.Albedo, cacheBlend);
                o.Smoothness = lerp(o.Smoothness, cachedSurface.Smoothness, cacheBlend);
                o.Normal = lerp(o.Normal, cachedSurface.Normal, cacheBlend);
                o.Occlusion = lerp(o.Occlusion, cachedSurface.Occlusion, cacheBlend);
                o.Emission = lerp(o.Emission, cachedSurface.Emission, cacheBlend);
                o.Metallic = lerp(o.Metallic, cachedSurface.Metallic, cacheBlend);
            }
";
        private const string BakeProgram = @"
float4 _LoogaVTPageRect;
struct LoogaBakeVaryings
{
    float4 position : SV_POSITION;
    float2 uv : TEXCOORD0;
};
struct LoogaBakeOutput
{
    float4 albedo : SV_Target0;
    float4 normal : SV_Target1;
    float4 emission : SV_Target2;
};
LoogaBakeVaryings LoogaBakeVertex(uint vertex : SV_VertexID)
{
    LoogaBakeVaryings output;
    float2 uv = float2((vertex << 1) & 2, vertex & 2);
    output.position = float4(uv * 2 - 1, 0, 1);
    #if UNITY_UV_STARTS_AT_TOP
    output.position.y = -output.position.y;
    #endif
    output.uv = _LoogaVTPageRect.xy + uv * _LoogaVTPageRect.zw;
    return output;
}
LoogaBakeOutput LoogaBakeFragment(LoogaBakeVaryings input)
{
    ShaderData data = (ShaderData)0;
    data.texcoord0 = float4(input.uv, 0, 0);
    data.worldSpacePosition = LoogaPosition(input.uv);
    data.worldSpaceNormal = LoogaNormal(saturate(input.uv));
    data.worldSpaceTangent = ConstructTerrainTangent(data.worldSpaceNormal, float3(0,0,1)).xyz;
    float3 bitangent = -normalize(cross(data.worldSpaceNormal, data.worldSpaceTangent));
    data.TBNMatrix = float3x3(data.worldSpaceTangent, bitangent, data.worldSpaceNormal);
    data.worldSpaceViewDir = float3(0,1,0);
    data.tangentSpaceViewDir = mul(data.worldSpaceViewDir, data.TBNMatrix);
    Surface surface = (Surface)0;
    surface.Normal = float3(0,0,1);
    surface.Occlusion = surface.Alpha = 1;
    SurfaceFunction(surface, data);
    LoogaBakeOutput output;
    output.albedo = float4(surface.Albedo, surface.Smoothness);
    output.normal = float4(surface.Normal, surface.Occlusion);
    output.emission = float4(surface.Emission, surface.Metallic);
    return output;
}
";
    }
}
