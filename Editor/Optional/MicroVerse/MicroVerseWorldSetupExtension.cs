using System.Collections.Generic;
using System.Linq;
using JBooth.MicroVerseCore;
using LoogaSoft.Instancing;
using LoogaSoft.Terrain.Editor;
using LoogaSoft.Terrain.Instances;
using UnityEditor;
using UnityEngine;

namespace LoogaSoft.Terrain.Integrations.Editor
{
    /// <summary>Connects MicroVerse terrain and vegetation updates during world setup.</summary>
    internal sealed class MicroVerseWorldSetupExtension : LoogaWorldSetupExtension
    {
        public override int Order => 100;

        public override void ConfigureWorld(IReadOnlyList<TerrainRenderer> renderers,
            IReadOnlyList<TerrainVegetationProvider> vegetation, LoogaWorldSetupContext context)
        {
            if (!context.Controller.ConfigureMicroVerse) return;
            MicroVerse[] sources = context.Controller.gameObject.scene.GetRootGameObjects()
                .SelectMany(root => root.GetComponentsInChildren<MicroVerse>(true)).Distinct().ToArray();
            foreach (MicroVerse source in sources)
            {
                UnityEngine.Terrain[] terrainSources = source.terrains;
                if (terrainSources == null || terrainSources.Length == 0)
                {
                    terrainSources = source.GetComponentsInChildren<UnityEngine.Terrain>(true);
                }
                var sourceTerrains = new HashSet<UnityEngine.Terrain>(terrainSources);
                TerrainRenderer[] sourceRenderers = renderers.Where(renderer =>
                    renderer && sourceTerrains.Contains(renderer.Source)).ToArray();
                TerrainVegetationProvider[] sourceVegetation = vegetation.Where(provider =>
                    provider && sourceTerrains.Contains(provider.GetComponent<UnityEngine.Terrain>())).ToArray();
                MicroVerseTerrainBridge terrainBridge = context.GetOrAddOwned<MicroVerseTerrainBridge>(source.gameObject);
                if (context.IsOwned(terrainBridge))
                {
                    terrainBridge.Configure(source, sourceRenderers, true);
                }
                else
                {
                    context.AddInfo(source.name + ": Preserved the existing MicroVerse terrain bridge settings.");
                }
                MicroVerseVegetationBridge vegetationBridge =
                    context.GetOrAddOwned<MicroVerseVegetationBridge>(source.gameObject);
                if (context.IsOwned(vegetationBridge))
                {
                    vegetationBridge.Configure(source, sourceVegetation);
                }
                else
                {
                    context.AddInfo(source.name + ": Preserved the existing MicroVerse vegetation bridge settings.");
                }

                Transform[] configuredOutputs = context.Controller.MicroVerseObjectOutputs?
                    .Where(output => output && output.gameObject.scene == context.Controller.gameObject.scene &&
                        (sources.Length == 1 || output.IsChildOf(source.transform))).Distinct().ToArray();
                if (configuredOutputs != null && configuredOutputs.Length > 0)
                {
                    SceneInstanceProvider objects = source.GetComponent<SceneInstanceProvider>();
                    bool providerExisted = objects;
                    objects = context.GetOrAddOwned<SceneInstanceProvider>(source.gameObject);
                    MicroVersePlacementBridge placement = source.GetComponent<MicroVersePlacementBridge>();
                    bool bridgeExisted = placement;
                    placement = context.GetOrAddOwned<MicroVersePlacementBridge>(source.gameObject);
                    if ((!providerExisted || context.IsOwned(objects)) && (!bridgeExisted || context.IsOwned(placement)))
                    {
                        placement.Configure(source, objects, configuredOutputs);
                        EditorUtility.SetDirty(objects);
                        EditorUtility.SetDirty(placement);
                    }
                    else
                    {
                        context.AddInfo(source.name + ": Preserved existing MicroVerse object-output settings.");
                    }
                }
            }
            if (sources.Length > 0)
            {
                context.AddInfo("MicroVerse terrain and vegetation updates are connected." +
                    (context.Controller.MicroVerseObjectOutputs.Length > 0
                        ? " Assigned object outputs refresh through Looga Instancing."
                        : " Object outputs remain native until explicit output roots are assigned."));
            }
        }

        public override void ValidateWorld(LoogaWorldSetupContext context)
        {
            if (!context.Controller.ConfigureMicroVerse) return;
            foreach (MicroVerse source in context.Controller.gameObject.scene.GetRootGameObjects()
                         .SelectMany(root => root.GetComponentsInChildren<MicroVerse>(true)))
            {
                if (!source.GetComponent<MicroVerseTerrainBridge>())
                {
                    context.AddWarning(source.name + ": The MicroVerse terrain bridge is absent.");
                }
                if (!source.GetComponent<MicroVerseVegetationBridge>())
                {
                    context.AddWarning(source.name + ": The MicroVerse vegetation bridge is absent.");
                }
                if (context.Controller.MicroVerseObjectOutputs.Length > 0 &&
                    !source.GetComponent<MicroVersePlacementBridge>())
                {
                    context.AddWarning(source.name + ": The configured MicroVerse object-output bridge is absent.");
                }
            }
        }
    }
}
