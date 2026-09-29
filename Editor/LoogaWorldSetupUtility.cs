using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LoogaSoft.Instancing;
using LoogaSoft.Instancing.Universal;
using LoogaSoft.Terrain.Instances;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace LoogaSoft.Terrain.Editor
{
    /// <summary>Configures one scene without changing TerrainData or source materials.</summary>
    public static class LoogaWorldSetupUtility
    {
        private const string PackagePath = "Packages/com.loogasoft.loogaterrain";

        public static string Configure(LoogaWorldController controller)
        {
            if (!controller) throw new ArgumentNullException(nameof(controller));
            if (!controller.gameObject.scene.IsValid()) throw new InvalidOperationException("The controller must be in a loaded scene.");
            EnsureFolder(controller.GeneratedAssetFolder);

            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Configure Looga World");
            Undo.RecordObject(controller, "Configure Looga World");
            var owned = controller.OwnedComponents.Where(component => component).Distinct().ToList();
            GameObject[] requestedInstanceRoots = controller.InstanceRoots?.Where(root => root &&
                root.scene == controller.gameObject.scene).Distinct().ToArray() ?? Array.Empty<GameObject>();
            ReconcileDisabledFeatures(controller, owned, requestedInstanceRoots.Length > 0);
            var context = new LoogaWorldSetupContext(controller, owned);
            List<LoogaWorldSetupExtension> extensions = LoadExtensions(context);
            Material baseSurface = EnsureBaseSurface(controller.GeneratedAssetFolder);
            VirtualTextureSettings virtualSettings = controller.UseTerrainVirtualTexturing
                ? EnsureVirtualTextureSettings(controller, context)
                : null;
            ComputeShader selection = AssetDatabase.LoadAssetAtPath<ComputeShader>(
                PackagePath + "/Runtime/Shaders/TerrainSelection.compute");
            Shader shadowBatch = AssetDatabase.LoadAssetAtPath<Shader>(
                PackagePath + "/Runtime/Shaders/TerrainBatchedShadows.shader");
            if (!selection) context.AddError("TerrainSelection.compute is unavailable.");
            if (!baseSurface) context.AddError("The default terrain surface is unavailable.");

            UnityEngine.Terrain[] terrains = FindSceneObjects<UnityEngine.Terrain>(controller.gameObject.scene);
            var renderers = new List<TerrainRenderer>(terrains.Length);
            var vegetation = new List<TerrainVegetationProvider>(terrains.Length);
            int nativeFallbacks = 0;
            foreach (UnityEngine.Terrain terrain in terrains)
            {
                Material surface = baseSurface;
                bool blocked = false;
                if (controller.ConfigureMicroSplat)
                {
                    foreach (LoogaWorldSetupExtension extension in extensions)
                    {
                        if (!extension.HandlesSurface(terrain)) continue;
                        if (!extension.TryCreateSurface(terrain, context, out surface, out string diagnostic) || !surface)
                        {
                            context.AddWarning(terrain.name + ": " + (diagnostic ?? "The optional surface stayed native."));
                            blocked = true;
                        }
                        break;
                    }
                }

                TerrainRenderer renderer = terrain.GetComponent<TerrainRenderer>();
                if (controller.RenderTerrain && !blocked && selection && surface)
                {
                    bool existed = renderer;
                    renderer = context.GetOrAddOwned<TerrainRenderer>(terrain.gameObject);
                    if (!existed || context.IsOwned(renderer))
                    {
                        Undo.RecordObject(renderer, "Configure Looga Terrain");
                        renderer.Configure(selection, surface, controller.PatchCount);
                        renderer.SetLodQuality(controller.ErrorPixels);
                        renderer.SetShadowCasting(controller.CastTerrainShadows);
                        EditorUtility.SetDirty(renderer);
                    }
                    else
                    {
                        context.AddInfo(terrain.name + ": Preserved the existing TerrainRenderer settings.");
                    }
                    renderers.Add(renderer);
                    foreach (LoogaWorldSetupExtension extension in extensions)
                    {
                        extension.ConfigureTerrain(terrain, renderer, context);
                    }
                    if (controller.UseTerrainVirtualTexturing && surface.FindPass("LoogaVTBake") >= 0)
                    {
                        TerrainVirtualTexture cache = context.GetOrAddOwned<TerrainVirtualTexture>(terrain.gameObject);
                        if (context.IsOwned(cache))
                        {
                            Undo.RecordObject(cache, "Configure Looga Terrain Cache");
                            cache.Configure(virtualSettings, surface);
                            EditorUtility.SetDirty(cache);
                        }
                        else
                        {
                            context.AddInfo(terrain.name + ": Preserved the existing terrain virtual-texture settings.");
                        }
                    }
                }
                else if (blocked)
                {
                    nativeFallbacks++;
                }

                if (controller.RenderVegetation)
                {
                    TerrainVegetationProvider provider = context.GetOrAddOwned<TerrainVegetationProvider>(terrain.gameObject);
                    if (context.IsOwned(provider))
                    {
                        Undo.RecordObject(provider, "Assign Looga Vegetation Settings");
                        provider.SetVegetationSettings(controller.VegetationSettings);
                        EditorUtility.SetDirty(provider);
                    }
                    vegetation.Add(provider);
                }
            }

            foreach (LoogaWorldSetupExtension extension in extensions)
            {
                extension.ConfigureWorld(renderers, vegetation, context);
            }

            if (requestedInstanceRoots.Length > 0)
            {
                SceneInstanceProvider provider = context.GetOrAddOwned<SceneInstanceProvider>(controller.gameObject);
                if (context.IsOwned(provider))
                {
                    Undo.RecordObject(provider, "Configure Looga Scene Instances");
                    provider.Configure(requestedInstanceRoots, controller.InstanceMaterialProfile, controller.InstanceMaxDistance);
                    EditorUtility.SetDirty(provider);
                }
                else
                {
                    context.AddInfo("Preserved the existing scene-instance provider settings.");
                }
                if (!string.IsNullOrEmpty(provider.Diagnostic))
                {
                    context.AddWarning("Scene instances: " + provider.Diagnostic);
                }
            }

            Camera[] cameras = ResolveCameras(controller);
            foreach (Camera camera in cameras)
            {
                if (controller.ConfigureVisibility)
                {
                    TerrainOcclusionCamera visibility = context.GetOrAddOwned<TerrainOcclusionCamera>(camera.gameObject);
                    if (context.IsOwned(visibility))
                    {
                        Undo.RecordObject(visibility, "Configure Looga Visibility");
                        visibility.Configure(null, shadowBatch, controller.CullTerrainShadows, controller.UseHiZ);
                        EditorUtility.SetDirty(visibility);
                    }
                }
                if (controller.ConfigureWaterCameras)
                {
                    context.GetOrAddOwned<TerrainWaterCamera>(camera.gameObject);
                }
            }

            ScriptableRendererData rendererData = ResolveRendererData(controller, cameras, context);
            ScriptableRendererFeature ownedFeature = controller.OwnedRendererFeature;
            if ((!controller.ConfigureVisibility || !controller.UseHiZ) && ownedFeature && rendererData &&
                rendererData.rendererFeatures.Contains(ownedFeature))
            {
                Undo.RecordObject(rendererData, "Remove Looga Visibility Feature");
                rendererData.rendererFeatures.Remove(ownedFeature);
                Undo.DestroyObjectImmediate(ownedFeature);
                EditorUtility.SetDirty(rendererData);
                AssetDatabase.SaveAssetIfDirty(rendererData);
                ownedFeature = null;
            }
            if (controller.ConfigureVisibility && controller.UseHiZ && rendererData)
            {
                InstanceOcclusionRendererFeature feature = rendererData.rendererFeatures
                    .OfType<InstanceOcclusionRendererFeature>().FirstOrDefault();
                if (!feature)
                {
                    Undo.RecordObject(rendererData, "Add Looga Visibility Feature");
                    feature = ScriptableObject.CreateInstance<InstanceOcclusionRendererFeature>();
                    feature.name = "Looga Temporal Hi-Z";
                    AssetDatabase.AddObjectToAsset(feature, rendererData);
                    rendererData.rendererFeatures.Add(feature);
                    feature.Create();
                    ownedFeature = feature;
                    EditorUtility.SetDirty(rendererData);
                    EditorUtility.SetDirty(feature);
                    AssetDatabase.SaveAssetIfDirty(rendererData);
                }
                else if (feature != ownedFeature)
                {
                    context.AddInfo("Preserved the existing Looga Hi-Z renderer feature settings.");
                }
            }

            string report = BuildReport(terrains.Length, renderers.Count, vegetation.Count, cameras.Length,
                nativeFallbacks, context.Messages);
            controller.SetSetupState(owned.Where(component => component).Distinct().ToArray(),
                rendererData, ownedFeature, report);
            EditorUtility.SetDirty(controller);
            EditorSceneManager.MarkSceneDirty(controller.gameObject.scene);
            Undo.CollapseUndoOperations(undoGroup);
            return report;
        }

        public static string Validate(LoogaWorldController controller)
        {
            if (!controller) throw new ArgumentNullException(nameof(controller));
            var owned = controller.OwnedComponents.Where(component => component).Distinct().ToList();
            var context = new LoogaWorldSetupContext(controller, owned);
            UnityEngine.Terrain[] terrains = FindSceneObjects<UnityEngine.Terrain>(controller.gameObject.scene);
            int ready = 0;
            int providers = 0;
            foreach (UnityEngine.Terrain terrain in terrains)
            {
                TerrainRenderer renderer = terrain.GetComponent<TerrainRenderer>();
                TerrainVegetationProvider provider = terrain.GetComponent<TerrainVegetationProvider>();
                if (renderer && string.IsNullOrEmpty(renderer.Diagnostic)) ready++;
                if (renderer && !string.IsNullOrEmpty(renderer.Diagnostic))
                {
                    context.AddWarning(terrain.name + ": " + renderer.Diagnostic);
                }
                if (provider) providers++;
            }
            foreach (LoogaWorldSetupExtension extension in LoadExtensions(context))
            {
                extension.ValidateWorld(context);
            }
            string report = BuildReport(terrains.Length, ready, providers, ResolveCameras(controller).Length,
                terrains.Length - ready, context.Messages);
            Undo.RecordObject(controller, "Validate Looga World");
            controller.SetSetupState(owned.ToArray(), controller.RendererData, controller.OwnedRendererFeature, report);
            EditorUtility.SetDirty(controller);
            return report;
        }

        public static string RestoreNative(LoogaWorldController controller)
        {
            if (!controller) throw new ArgumentNullException(nameof(controller));
            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Restore Native World Rendering");
            Undo.RecordObject(controller, "Restore Native World Rendering");
            Component[] owned = controller.OwnedComponents;
            int removed = 0;
            for (int index = owned.Length - 1; index >= 0; index--)
            {
                if (!owned[index]) continue;
                Undo.DestroyObjectImmediate(owned[index]);
                removed++;
            }

            ScriptableRendererFeature feature = controller.OwnedRendererFeature;
            ScriptableRendererData rendererData = controller.RendererData;
            if (feature && rendererData && rendererData.rendererFeatures.Contains(feature))
            {
                Undo.RecordObject(rendererData, "Remove Looga Visibility Feature");
                rendererData.rendererFeatures.Remove(feature);
                Undo.DestroyObjectImmediate(feature);
                EditorUtility.SetDirty(rendererData);
                AssetDatabase.SaveAssetIfDirty(rendererData);
            }

            string report = "Restored native rendering. Removed " + removed +
                " controller-owned components. Generated assets remain available for later setup.";
            controller.ClearSetupState(report);
            EditorUtility.SetDirty(controller);
            EditorSceneManager.MarkSceneDirty(controller.gameObject.scene);
            Undo.CollapseUndoOperations(undoGroup);
            return report;
        }

        private static List<LoogaWorldSetupExtension> LoadExtensions(LoogaWorldSetupContext context)
        {
            var extensions = new List<LoogaWorldSetupExtension>();
            foreach (Type type in TypeCache.GetTypesDerivedFrom<LoogaWorldSetupExtension>())
            {
                if (type.IsAbstract) continue;
                try
                {
                    extensions.Add((LoogaWorldSetupExtension)Activator.CreateInstance(type));
                }
                catch (Exception exception)
                {
                    context.AddWarning(type.Name + " could not start: " + exception.Message);
                }
            }
            extensions.Sort((left, right) => left.Order.CompareTo(right.Order));
            return extensions;
        }

        private static void ReconcileDisabledFeatures(LoogaWorldController controller, List<Component> owned,
            bool hasInstanceRoots)
        {
            // Remove dependants first so Unity never has to break a RequireComponent relationship.
            if (!controller.UseTerrainVirtualTexturing)
            {
                RemoveOwned<TerrainVirtualTexture>(owned);
            }
            if (!controller.RenderVegetation)
            {
                RemoveOwned<TerrainVegetationProvider>(owned);
            }
            if (!controller.RenderTerrain)
            {
                RemoveOwned<TerrainVirtualTexture>(owned);
                RemoveOwned<TerrainRenderer>(owned);
            }
            if (!controller.ConfigureVisibility)
            {
                RemoveOwned<TerrainOcclusionCamera>(owned);
            }
            if (!controller.ConfigureWaterCameras)
            {
                RemoveOwned<TerrainWaterCamera>(owned);
            }
            if (!hasInstanceRoots)
            {
                for (int index = owned.Count - 1; index >= 0; index--)
                {
                    if (owned[index] is SceneInstanceProvider provider &&
                        provider.gameObject == controller.gameObject)
                    {
                        Undo.DestroyObjectImmediate(provider);
                        owned.RemoveAt(index);
                    }
                }
            }
        }

        private static void RemoveOwned<T>(List<Component> owned) where T : Component
        {
            for (int index = owned.Count - 1; index >= 0; index--)
            {
                if (owned[index] is not T component) continue;
                Undo.DestroyObjectImmediate(component);
                owned.RemoveAt(index);
            }
        }

        private static T[] FindSceneObjects<T>(Scene scene) where T : Component
        {
            var results = new List<T>();
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                results.AddRange(root.GetComponentsInChildren<T>(true));
            }
            return results.Where(result => result).Distinct().ToArray();
        }

        private static Camera[] ResolveCameras(LoogaWorldController controller)
        {
            Camera[] explicitCameras = controller.Cameras?.Where(camera => camera &&
                camera.gameObject.scene == controller.gameObject.scene).Distinct().ToArray();
            if (explicitCameras != null && explicitCameras.Length > 0) return explicitCameras;
            return FindSceneObjects<Camera>(controller.gameObject.scene).Where(camera =>
            {
                return !camera.TryGetComponent(out UniversalAdditionalCameraData data) ||
                    data.renderType == CameraRenderType.Base;
            }).ToArray();
        }

        private static ScriptableRendererData ResolveRendererData(LoogaWorldController controller,
            Camera[] cameras, LoogaWorldSetupContext context)
        {
            if (controller.RendererData) return controller.RendererData;
            var pipeline = QualitySettings.renderPipeline as UniversalRenderPipelineAsset;
            pipeline ??= GraphicsSettings.defaultRenderPipeline as UniversalRenderPipelineAsset;
            if (!pipeline)
            {
                context.AddWarning("The active URP asset is unavailable. The Hi-Z renderer feature was not installed.");
                return null;
            }
            var serialized = new SerializedObject(pipeline);
            SerializedProperty list = serialized.FindProperty("m_RendererDataList");
            SerializedProperty defaultIndex = serialized.FindProperty("m_DefaultRendererIndex");
            int index = defaultIndex?.intValue ?? 0;
            if (cameras.Length > 0)
            {
                if (cameras[0].TryGetComponent(out UniversalAdditionalCameraData cameraData))
                {
                    SerializedProperty cameraIndex = new SerializedObject(cameraData).FindProperty("m_RendererIndex");
                    if (cameraIndex != null && cameraIndex.intValue >= 0)
                    {
                        index = cameraIndex.intValue;
                    }
                }
            }
            if (list == null || index < 0 || index >= list.arraySize)
            {
                context.AddWarning("The active URP renderer could not be resolved. Assign Renderer Data on the controller.");
                return null;
            }
            return list.GetArrayElementAtIndex(index).objectReferenceValue as ScriptableRendererData;
        }

        private static Material EnsureBaseSurface(string folder)
        {
            string path = folder + "/Looga Terrain Surface.mat";
            Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material) return material;
            Shader shader = AssetDatabase.LoadAssetAtPath<Shader>(
                PackagePath + "/Runtime/Shaders/TerrainLit.shader");
            if (!shader) return null;
            material = new Material(shader) { name = "Looga Terrain Surface", enableInstancing = true };
            AssetDatabase.CreateAsset(material, path);
            return material;
        }

        private static VirtualTextureSettings EnsureVirtualTextureSettings(LoogaWorldController controller,
            LoogaWorldSetupContext context)
        {
            if (controller.VirtualTextureSettings) return controller.VirtualTextureSettings;
            string path = controller.GeneratedAssetFolder + "/Looga Terrain Virtual Texture Settings.asset";
            VirtualTextureSettings settings = AssetDatabase.LoadAssetAtPath<VirtualTextureSettings>(path);
            if (!settings)
            {
                Shader feedback = AssetDatabase.LoadAssetAtPath<Shader>(PackagePath +
                    "/Runtime/Shaders/VirtualTexturing/VirtualTextureFeedback.shader");
                if (!feedback)
                {
                    context.AddError("The terrain virtual-texture feedback shader is unavailable.");
                    return null;
                }
                settings = ScriptableObject.CreateInstance<VirtualTextureSettings>();
                settings.name = "Looga Terrain Virtual Texture Settings";
                settings.Configure(feedback);
                settings.ConfigureOptimizations(AssetDatabase.LoadAssetAtPath<ComputeShader>(PackagePath +
                    "/Runtime/Shaders/VirtualTexturing/VirtualTextureCompression.compute"), true, true);
                settings.ConfigureFeedbackCompaction(AssetDatabase.LoadAssetAtPath<ComputeShader>(PackagePath +
                    "/Runtime/Shaders/VirtualTexturing/VirtualTextureCompact.compute"));
                AssetDatabase.CreateAsset(settings, path);
            }
            controller.SetVirtualTextureSettings(settings);
            EditorUtility.SetDirty(controller);
            return settings;
        }

        private static void EnsureFolder(string folder)
        {
            string normalized = folder?.Replace('\\', '/').TrimEnd('/');
            if (string.IsNullOrEmpty(normalized) || !normalized.StartsWith("Assets/", StringComparison.Ordinal) || normalized.Contains(".."))
            {
                throw new ArgumentException("The generated asset folder must be below Assets.", nameof(folder));
            }
            Directory.CreateDirectory(normalized);
            AssetDatabase.ImportAsset(normalized, ImportAssetOptions.ForceSynchronousImport);
        }

        private static string BuildReport(int terrains, int renderers, int vegetation, int cameras,
            int nativeFallbacks, IReadOnlyList<string> messages)
        {
            string summary = "Terrains: " + terrains + " | Looga surfaces: " + renderers +
                " | Vegetation providers: " + vegetation + " | Cameras: " + cameras +
                " | Native surface fallbacks: " + nativeFallbacks;
            return messages.Count == 0 ? summary : summary + "\n\n" + string.Join("\n", messages);
        }
    }
}
