using System;
using System.IO;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace LoogaSoft.Terrain.Integrations.Editor
{
    /// <summary>Adapts an existing MicroSplat page producer to the Graphics Pro clipmap surface contract.</summary>
    public static class MicroSplatClipmapAdapter
    {
        /// <summary>Exports a writer from a matching native shader and existing page producer. Source assets remain unchanged.</summary>
        public static Material Export(Material pageProducer, Shader nativeShader, string outputStem)
        {
            if (!pageProducer || !nativeShader || pageProducer.FindPass("LoogaVTBake") < 0)
            {
                throw new ArgumentException("Select an exported MicroSplat page producer and its matching native shader.");
            }
            if (string.IsNullOrWhiteSpace(outputStem) || !outputStem.StartsWith("Assets/", StringComparison.Ordinal)
                || outputStem.Contains("..") || !Directory.Exists(Path.GetDirectoryName(outputStem)))
            {
                throw new ArgumentException("Choose an existing project folder under Assets.");
            }
            if (File.Exists(outputStem + ".shader") || File.Exists(outputStem + ".mat"))
            {
                throw new IOException("Choose a new writer path.");
            }
            string code = File.ReadAllText(AssetDatabase.GetAssetPath(pageProducer.shader));
            string nativeCode = File.ReadAllText(AssetDatabase.GetAssetPath(nativeShader));
            MicroSplatVirtualTextureAdapter.ValidateFeatures(nativeCode);
            var features = new HashSet<string>(MicroSplatCompatibilityEditor.ReadFeatures(nativeCode));
            if (!features.SetEquals(MicroSplatCompatibilityEditor.ReadFeatures(code.Replace("#define _URP 1", "#pragma instancing_options"))))
            {
                throw new InvalidOperationException("The page producer features do not match the native shader. Re-export the producer.");
            }
            string name = "Hidden/Looga/Terrain/Clipmap/" + Path.GetFileName(outputStem);
            code = code.Replace(pageProducer.shader.name, name);
            code = code.Replace("Name \"LoogaVTBake\"", "Name \"LoogaRvtTerrain\"");
            code = code.Replace("Cull Off ZWrite Off ZTest Always", "Cull Off ZWrite On ZTest LEqual");
            code = code.Replace("\"RenderPipeline\"=\"UniversalPipeline\"",
                "\"RenderPipeline\"=\"UniversalPipeline\" \"LoogaRvtSourceShader\"=\"" + nativeShader.name + "\"");
            code = code.Replace("float4 emission : SV_Target2;", "float4 emission : SV_Target2;\nfloat depth : SV_Depth;");
            code = code.Replace("ShaderData data = (ShaderData)0;", "clip(input.uv); clip(1 - input.uv); LoogaClipHoles(input.uv);\nShaderData data = (ShaderData)0;");
            const string marker = "output.emission = float4(surface.Emission, surface.Metallic);";
            if (!code.Contains(marker))
            {
                throw new NotSupportedException("The page producer output layout is not supported.");
            }
            code = code.Replace("float4 _LoogaVTPageRect;", "float4 _LoogaVTPageRect;\nfloat4 _LoogaRvtHeightRange;");
            code = code.Replace(marker, Output);
            File.WriteAllText(outputStem + ".shader", code.Replace("\r\n", "\n"));
            AssetDatabase.ImportAsset(outputStem + ".shader", ImportAssetOptions.ForceSynchronousImport);
            Shader shader = AssetDatabase.LoadAssetAtPath<Shader>(outputStem + ".shader");
            if (!shader || ShaderUtil.ShaderHasError(shader))
            {
                throw new InvalidOperationException("The clipmap writer did not compile.");
            }
            var material = new Material(pageProducer) { shader = shader };
            AssetDatabase.CreateAsset(material, outputStem + ".mat");
            AssetDatabase.SaveAssetIfDirty(material);
            return material;
        }

        private const string Output = @"
            float3 normalWS = normalize(mul(surface.Normal, data.TBNMatrix));
            normalWS /= abs(normalWS.x) + abs(normalWS.y) + abs(normalWS.z);
            float2 oct = normalWS.xz;
            if (normalWS.y < 0)
                oct = (1 - abs(oct.yx)) * float2(oct.x >= 0 ? 1 : -1, oct.y >= 0 ? 1 : -1);
            float height = min(saturate((data.worldSpacePosition.y - _LoogaRvtHeightRange.x) * _LoogaRvtHeightRange.z), 1 - 1.0 / 65535.0);
            float2 encoded = frac(height * float2(1, 255));
            encoded.x -= encoded.y / 255;
            output.albedo = float4(surface.Albedo, 1);
            output.normal = float4(oct * 0.5 + 0.5, surface.Smoothness, surface.Metallic);
            output.emission = float4(encoded, 1, 1);
            float4 positionCS = TransformWorldToHClip(data.worldSpacePosition);
            output.depth = positionCS.z / positionCS.w;
            #if defined(SHADER_API_GLCORE) || defined(SHADER_API_GLES) || defined(SHADER_API_GLES3)
                output.depth = output.depth * 0.5 + 0.5;
            #endif
";
    }
}
