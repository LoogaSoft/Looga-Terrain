using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using JBooth.MicroSplat;
using UnityEditor;
using UnityEngine;

namespace LoogaSoft.Terrain.Integrations.Editor
{
    /// <summary>
    /// Exports a separate MicroSplat 3.9 URP shader with Looga patch geometry.
    /// Source materials, generated shaders, and vendor packages remain unchanged.
    /// </summary>
    public static class MicroSplatShaderAdapter
    {
        [MenuItem("Tools/Looga/Terrain/Export Selected MicroSplat Surface")]
        private static void ExportSelected()
        {
            MicroSplatTerrain source = Selection.activeGameObject ? Selection.activeGameObject.GetComponent<MicroSplatTerrain>() : null;
            if (!source)
            {
                EditorUtility.DisplayDialog("Looga Terrain", "Select a terrain with a synchronized MicroSplatTerrain component.", "OK");
                return;
            }
            string path = EditorUtility.SaveFilePanelInProject("Export Looga Terrain Surface", "LoogaMicroSplat", "mat", "Choose a new path for the material and shader.");
            if (string.IsNullOrEmpty(path)) return;
            Material material = Export(source, path.Substring(0, path.Length - 4));
            Selection.activeObject = material;
        }

        private const string GeometryMarker = "void ApplyMeshModification(inout VertexData input)";
        private const string GeometryInclude = "\n#define UNITY_INDIRECT_DRAW_ARGS IndirectDrawIndexedArgs\n#include \"UnityIndirect.cginc\"\n#include \"Packages/com.loogasoft.loogaterrain/Runtime/Shaders/TerrainGeometry.hlsl\"\n";
        private const string GeometryBody = @"
{
    InitIndirectDrawArgs(0);
    uint instance = GetIndirectInstanceID(input.instanceID);
    float2 uv = LoogaPatchUV(input.vertex.xz, instance);
    input.vertex = float4(LoogaPosition(uv), 1);
    input.normal = LoogaNormal(uv);
    input.texcoord0.xy = uv;
}
";

        /// <summary>
        /// Creates consumer-owned shader and material assets for the current MicroSplat material.
        /// Unsupported generator layouts fail before asset creation.
        /// </summary>
        public static Material Export(MicroSplatTerrain source, string outputStem, bool updateExisting = false)
        {
            if (!source || !source.matInstance)
            {
                throw new ArgumentException("A synchronized MicroSplat terrain is required.", nameof(source));
            }
            if (!outputStem.StartsWith("Assets/", StringComparison.Ordinal) || outputStem.Contains(".."))
            {
                throw new ArgumentException("Output must be inside consumer Assets.", nameof(outputStem));
            }
            string shaderPath = outputStem + ".shader";
            string materialPath = outputStem + ".mat";
            if (!updateExisting && (File.Exists(shaderPath) || File.Exists(materialPath)))
            {
                throw new IOException("Export paths already exist. Select a new version path.");
            }
            string sourcePath = AssetDatabase.GetAssetPath(source.matInstance.shader);
            string code = File.ReadAllText(sourcePath);
            if (!code.Contains("MicroSplat Version: 3.9") || !code.Contains("#define _URP 1"))
            {
                throw new NotSupportedException("Only generated MicroSplat 3.9 URP shaders are supported.");
            }
            if (code.Contains("#define _TESSDISTANCE 1") || code.Contains("#define _TESSEDGE 1") || code.Contains("#define _TESSONLYDISPLACE 1"))
            {
                throw new NotSupportedException("Disable MicroSplat vertex displacement before export. GPU patch displacement owns the terrain surface.");
            }

            string[] features = MicroSplatCompatibilityEditor.ReadFeatures(code);
            if (Array.IndexOf(features, "_TERRAINBLENDABLESHADER") >= 0)
            {
                throw new NotSupportedException("Keep the native object-blend shader on meshes. Export the terrain surface instead.");
            }
            string shaderName = "Looga/Terrain/MicroSplat/" + Path.GetFileName(outputStem);
            Material existingMaterial = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            Shader existingShader = AssetDatabase.LoadAssetAtPath<Shader>(shaderPath);
            if (updateExisting && ((existingShader && existingShader.name != shaderName) || (existingMaterial && existingMaterial.shader != existingShader)))
            {
                throw new InvalidOperationException("Only assets previously exported at this path can be updated.");
            }
            code = Regex.Replace(code, "Shader\\s+\"[^\"]+\"", "Shader \"" + shaderName + "\"", RegexOptions.None, TimeSpan.FromSeconds(1));
            // Indirect draws supply world positions and an explicit instance ID.
            code = code.Replace("#pragma target 3.5", "#pragma target 4.5")
                .Replace("#pragma multi_compile_instancing", string.Empty)
                .Replace("#pragma multi_compile _ DOTS_INSTANCING_ON", string.Empty)
                .Replace("UNITY_VERTEX_INPUT_INSTANCE_ID", "uint instanceID : SV_InstanceID;")
                .Replace("float3 positionWS = TransformObjectToWorld(v.vertex.xyz);", "float3 positionWS = v.vertex.xyz;")
                .Replace("float3 normalWS = TransformObjectToWorldNormal(v.normal);", "float3 normalWS = v.normal;")
                .Replace("VertexPositionInputs vertexInput = GetVertexPositionInputs(v.vertex.xyz);",
                    "VertexPositionInputs vertexInput = (VertexPositionInputs)0; vertexInput.positionWS = positionWS; vertexInput.positionVS = TransformWorldToView(positionWS); vertexInput.positionCS = TransformWorldToHClip(positionWS);")
                .Replace("float hole = SAMPLE_TEXTURE2D(_TerrainHolesTexture, shared_linear_clamp_sampler, uv).r;",
                    "float hole = SAMPLE_TEXTURE2D(_TerrainHolesTexture, sampler_PointClamp, uv).r;");
            // Retain terrain normals per pixel when indirect geometry bypasses native instancing.
            code = code.Replace("#elif (defined(UNITY_INSTANCING_ENABLED) && _MICROTERRAIN && !_TERRAINBLENDABLESHADER)",
                    "#elif (_MICROTERRAIN && !_TERRAINBLENDABLESHADER)")
                .Replace("(d.texcoord0.xy / _TerrainHeightmapRecipSize.zw + 0.5f) * _TerrainHeightmapRecipSize.xy",
                    "(d.texcoord0.xy * (_LoogaHeightInfo.x - 1) + 0.5f) / _LoogaHeightInfo.x");
            code = Regex.Replace(code, @"Properties\s*\{", "Properties\n{\n[HideInInspector] _LoogaTerrainSurface(\"Looga Terrain Surface\", Float) = 1", RegexOptions.None, TimeSpan.FromSeconds(1));
            int offset = 0;
            int replaced = 0;
            var result = new StringBuilder(code.Length);
            while (true)
            {
                int start = code.IndexOf(GeometryMarker, offset, StringComparison.Ordinal);
                if (start < 0)
                {
                    result.Append(code, offset, code.Length - offset);
                    break;
                }
                int opening = code.IndexOf('{', start);
                int depth = 1;
                int end = opening + 1;
                while (depth > 0 && end < code.Length)
                {
                    if (code[end] == '{')
                    {
                        depth++;
                    }
                    else if (code[end] == '}')
                    {
                        depth--;
                    }
                    end++;
                }
                if (depth != 0)
                {
                    throw new FormatException("The generated geometry function is incomplete.");
                }
                result.Append(code, offset, start - offset);
                result.Append(GeometryInclude);
                result.Append(GeometryMarker);
                result.Append(GeometryBody);
                offset = end;
                replaced++;
            }
            if (replaced < 4)
            {
                throw new NotSupportedException("Expected forward, shadow, depth, and normal geometry functions were not found.");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(shaderPath));
            File.WriteAllText(shaderPath, result.ToString().Replace("\r\n", "\n"));
            AssetDatabase.ImportAsset(shaderPath, ImportAssetOptions.ForceSynchronousImport);
            Shader shader = AssetDatabase.LoadAssetAtPath<Shader>(shaderPath);
            if (!shader || ShaderUtil.ShaderHasError(shader))
            {
                throw new InvalidOperationException("The exported shader failed compilation. The source shader remains unchanged.");
            }
            Material material = existingMaterial ? existingMaterial : new Material(shader);
            material.CopyPropertiesFromMaterial(source.matInstance);
            material.SetOverrideTag(MicroSplatCompatibility.FeaturesTag, string.Join("|", features));
            material.SetOverrideTag(MicroSplatCompatibility.ShaderTag, source.matInstance.shader.name);
            bool nativeDepth = Array.IndexOf(features, "_ALPHAHOLE") < 0 && Array.IndexOf(features, "_ALPHAHOLETEXTURE") < 0
                && Array.IndexOf(features, "_ALPHABELOWHEIGHT") < 0;
            material.SetOverrideTag("LoogaTerrainDepth", nativeDepth ? "NativeHeightsAndHoles" : "Material");
            material.name = Path.GetFileName(outputStem);
            material.shader = shader;
            material.enableInstancing = true;
            material.EnableKeyword("_ALPHATEST_ON");
            if (!existingMaterial)
            {
                AssetDatabase.CreateAsset(material, materialPath);
            }
            EditorUtility.SetDirty(material);
            AssetDatabase.SaveAssetIfDirty(material);
            return material;
        }
    }
}
