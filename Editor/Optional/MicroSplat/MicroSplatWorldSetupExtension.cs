using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using JBooth.MicroSplat;
using LoogaSoft.Terrain.Editor;
using UnityEditor;
using UnityEngine;

namespace LoogaSoft.Terrain.Integrations.Editor
{
    /// <summary>Exports and connects compatible MicroSplat terrain surfaces during world setup.</summary>
    internal sealed class MicroSplatWorldSetupExtension : LoogaWorldSetupExtension
    {
        private readonly Dictionary<string, Material> _surfaces = new(StringComparer.Ordinal);
        private readonly Dictionary<UnityEngine.Terrain, Material> _producers = new();

        public override int Order => -100;

        public override bool HandlesSurface(UnityEngine.Terrain terrain)
        {
            return terrain && terrain.GetComponent<MicroSplatTerrain>();
        }

        public override bool TryCreateSurface(UnityEngine.Terrain terrain, LoogaWorldSetupContext context,
            out Material surface, out string diagnostic)
        {
            surface = null;
            diagnostic = null;
            MicroSplatTerrain source = terrain.GetComponent<MicroSplatTerrain>();
            if (!source || !source.matInstance || !source.keywordSO)
            {
                diagnostic = "MicroSplat must synchronize its material before Looga setup.";
                return false;
            }
            try
            {
                string shaderPath = AssetDatabase.GetAssetPath(source.matInstance.shader);
                string shaderGuid = AssetDatabase.AssetPathToGUID(shaderPath);
                string features = string.Join("|", source.keywordSO.keywords.OrderBy(feature => feature, StringComparer.Ordinal));
                string signature = Hash128.Compute(source.matInstance.shader.name + "|" + features).ToString().Substring(0, 8);
                string stem = context.OutputFolder + "/Looga MicroSplat " +
                    (string.IsNullOrEmpty(shaderGuid) ? signature : shaderGuid.Substring(0, 8) + " " + signature);
                if (context.Controller.UseTerrainVirtualTexturing)
                {
                    stem += " Virtual";
                }
                if (_surfaces.TryGetValue(stem, out surface) && surface)
                {
                    if (context.Controller.UseTerrainVirtualTexturing)
                    {
                        _producers[terrain] = AssetDatabase.LoadAssetAtPath<Material>(stem + "PageProducer.mat");
                    }
                    return true;
                }
                bool materialExists = File.Exists(stem + ".mat");
                bool shaderExists = File.Exists(stem + ".shader");
                bool producerMaterialExists = File.Exists(stem + "PageProducer.mat");
                bool producerShaderExists = File.Exists(stem + "PageProducer.shader");
                bool virtualOutputComplete = materialExists && shaderExists && producerMaterialExists && producerShaderExists;
                bool partialVirtualOutput = materialExists || shaderExists || producerMaterialExists || producerShaderExists;
                if ((!context.Controller.UseTerrainVirtualTexturing && materialExists != shaderExists) ||
                    (context.Controller.UseTerrainVirtualTexturing && partialVirtualOutput && !virtualOutputComplete))
                {
                    diagnostic = "A partial generated MicroSplat surface exists at " + stem + ". Remove or repair that pair.";
                    return false;
                }
                surface = context.Controller.UseTerrainVirtualTexturing
                    ? MicroSplatVirtualTextureAdapter.Export(source, stem, virtualOutputComplete)
                    : MicroSplatShaderAdapter.Export(source, stem, materialExists && shaderExists);
                _surfaces[stem] = surface;
                if (context.Controller.UseTerrainVirtualTexturing)
                {
                    _producers[terrain] = AssetDatabase.LoadAssetAtPath<Material>(stem + "PageProducer.mat");
                }
                return surface;
            }
            catch (Exception exception)
            {
                diagnostic = exception.Message;
                return false;
            }
        }

        public override void ConfigureTerrain(UnityEngine.Terrain terrain, TerrainRenderer renderer,
            LoogaWorldSetupContext context)
        {
            if (!terrain.GetComponent<MicroSplatTerrain>()) return;
            MicroSplatTerrainBridge bridge = context.GetOrAddOwned<MicroSplatTerrainBridge>(terrain.gameObject);
            bridge.SynchronizeMaterial();
            if (!string.IsNullOrEmpty(bridge.Diagnostic))
            {
                context.AddWarning(terrain.name + ": " + bridge.Diagnostic);
            }
            if (context.Controller.UseTerrainVirtualTexturing)
            {
                if (!_producers.TryGetValue(terrain, out Material producer) || !producer)
                {
                    context.AddWarning(terrain.name + ": The generated MicroSplat page producer is unavailable.");
                    return;
                }
                TerrainVirtualTexture cache = context.GetOrAddOwned<TerrainVirtualTexture>(terrain.gameObject);
                if (context.IsOwned(cache))
                {
                    Undo.RecordObject(cache, "Configure MicroSplat Terrain Cache");
                    cache.Configure(context.Controller.VirtualTextureSettings, producer);
                    EditorUtility.SetDirty(cache);
                }
                else
                {
                    context.AddInfo(terrain.name + ": Preserved the existing terrain virtual-texture settings.");
                }
            }
        }

        public override void ValidateWorld(LoogaWorldSetupContext context)
        {
            foreach (UnityEngine.Terrain terrain in FindTerrains(context.Controller.gameObject.scene))
            {
                if (!terrain.GetComponent<MicroSplatTerrain>()) continue;
                MicroSplatTerrainBridge bridge = terrain.GetComponent<MicroSplatTerrainBridge>();
                if (!bridge)
                {
                    context.AddWarning(terrain.name + ": The MicroSplat bridge is absent.");
                }
                else if (!string.IsNullOrEmpty(bridge.Diagnostic))
                {
                    context.AddWarning(terrain.name + ": " + bridge.Diagnostic);
                }
            }
        }

        private static UnityEngine.Terrain[] FindTerrains(UnityEngine.SceneManagement.Scene scene)
        {
            return scene.GetRootGameObjects().SelectMany(root =>
                root.GetComponentsInChildren<UnityEngine.Terrain>(true)).ToArray();
        }
    }
}
