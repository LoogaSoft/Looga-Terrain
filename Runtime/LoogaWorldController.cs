using System;
using LoogaSoft.Instancing;
using LoogaSoft.Terrain.Instances;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace LoogaSoft.Terrain
{
    /// <summary>Assigns shared terrain renderer settings to the terrains below one hierarchy root.</summary>
    [Serializable]
    public struct TerrainSettingsGroup
    {
        [SerializeField] private Transform _root;
        [SerializeField] private TerrainRendererSettings _settings;

        public TerrainSettingsGroup(Transform root, TerrainRendererSettings settings)
        {
            _root = root;
            _settings = settings;
        }

        public Transform Root => _root;
        public TerrainRendererSettings Settings => _settings;
    }

    /// <summary>Stores one scene's Looga Terrain setup policy and owned objects.</summary>
    [AddComponentMenu("Looga/World Controller")]
    [DisallowMultipleComponent]
    public sealed class LoogaWorldController : MonoBehaviour
    {
        [Header("Terrain")]
        [SerializeField] private bool _renderTerrain = true;
        [SerializeField] private bool _renderVegetation = true;
        [Tooltip("Renderer settings of the terrains that no group contains. Empty keeps the values of each " +
                 "TerrainRenderer.")]
        [SerializeField] private TerrainRendererSettings _terrainSettings;
        [Tooltip("Renderer settings of the terrains below each root. The first group that contains a terrain applies.")]
        [SerializeField] private TerrainSettingsGroup[] _terrainGroups = Array.Empty<TerrainSettingsGroup>();

        [Header("Vegetation")]
        [Tooltip("Shared detail, tree and object defaults with source overrides. " +
                 "Empty keeps the local settings of each terrain, InstanceSet and InstanceContainer.")]
        [SerializeField] private VegetationSettings _vegetationSettings;
        [Tooltip("Use the full-quality Looga vegetation renderer in Scene view. Terrain editing temporarily restores native rendering.")]
        [SerializeField] private bool _previewVegetationInEditor;

        [Header("Terrain Surface Cache")]
        [SerializeField] private bool _useTerrainVirtualTexturing;
        [SerializeField] private VirtualTextureSettings _virtualTextureSettings;

        [Header("Visibility")]
        [SerializeField] private bool _configureVisibility = true;
        [SerializeField] private bool _useHiZ = true;
        [SerializeField] private bool _configureWaterCameras = true;
        [SerializeField] private Camera[] _cameras = Array.Empty<Camera>();
        [SerializeField] private ScriptableRendererData _rendererData;

        [Header("Scene Instances")]
        [Tooltip("Approved static or LODGroup roots rendered through Looga Instancing. Unsupported roots stay native.")]
        [SerializeField] private GameObject[] _instanceRoots = Array.Empty<GameObject>();
        [SerializeField] private InstanceMaterialProfile _instanceMaterialProfile;
        [SerializeField, Min(1)] private float _instanceMaxDistance = 1000f;

        [Header("Integrations")]
        [SerializeField] private bool _configureMicroSplat = true;
        [SerializeField] private string _generatedAssetFolder = "Assets/Looga/Generated/Terrain";

        [SerializeField, HideInInspector] private Component[] _ownedComponents = Array.Empty<Component>();
        [SerializeField, HideInInspector] private ScriptableRendererFeature _ownedRendererFeature;
        [SerializeField, HideInInspector] private string _lastReport = "This world has not been configured.";

        public bool RenderTerrain => _renderTerrain;
        public bool RenderVegetation => _renderVegetation;
        public TerrainRendererSettings TerrainSettings => _terrainSettings;
        public TerrainSettingsGroup[] TerrainGroups => _terrainGroups;
        public bool UseTerrainVirtualTexturing => _useTerrainVirtualTexturing;
        public VirtualTextureSettings VirtualTextureSettings => _virtualTextureSettings;
        public bool ConfigureVisibility => _configureVisibility;
        public bool UseHiZ => _useHiZ;
        public bool ConfigureWaterCameras => _configureWaterCameras;
        public Camera[] Cameras => _cameras;
        public ScriptableRendererData RendererData => _rendererData;
        public GameObject[] InstanceRoots => _instanceRoots;
        public InstanceMaterialProfile InstanceMaterialProfile => _instanceMaterialProfile;
        public float InstanceMaxDistance => _instanceMaxDistance;
        public bool ConfigureMicroSplat => _configureMicroSplat;
        public string GeneratedAssetFolder => _generatedAssetFolder;
        public Component[] OwnedComponents => _ownedComponents;
        public ScriptableRendererFeature OwnedRendererFeature => _ownedRendererFeature;
        public string LastReport => _lastReport;
        public VegetationSettings VegetationSettings => _vegetationSettings;
        public bool PreviewVegetationInEditor => _previewVegetationInEditor;

        /// <summary>Sets the editor preview preference. Runtime vegetation settings remain unchanged.</summary>
        public void SetEditorVegetationPreview(bool enabled)
        {
            _previewVegetationInEditor = enabled;
        }

        private void OnEnable()
        {
            WarnIfAnotherControllerIsActive();
            if (Application.isPlaying)
            {
                ApplyTerrainSettings();
                try
                {
                    ApplyVegetationSettings();
                }
                catch (Exception exception)
                {
                    Debug.LogError("Cannot apply vegetation settings: " + exception.Message, this);
                }
            }
        }

        /// <summary>
        /// Stores the terrain renderer settings and assigns them to the terrain renderers in this world scene.
        /// </summary>
        public void SetTerrainSettings(TerrainRendererSettings settings, TerrainSettingsGroup[] groups = null)
        {
            _terrainSettings = settings;
            _terrainGroups = groups == null
                ? Array.Empty<TerrainSettingsGroup>()
                : (TerrainSettingsGroup[])groups.Clone();
            ApplyTerrainSettings();
        }

        /// <summary>
        /// Gets the settings of one terrain: those of the first group whose root contains it, otherwise the shared
        /// terrain settings. Null keeps the values of the TerrainRenderer.
        /// </summary>
        public TerrainRendererSettings GetTerrainSettings(Transform terrain)
        {
            foreach (TerrainSettingsGroup group in _terrainGroups)
            {
                if (group.Root && terrain && terrain.IsChildOf(group.Root)) return group.Settings;
            }
            return _terrainSettings;
        }

        /// <summary>Finds this scene's terrain renderers, including inactive terrain objects.</summary>
        public TerrainRenderer[] GetTerrainRenderers()
        {
            return FindInScene<TerrainRenderer>();
        }

        /// <summary>Assigns the terrain settings to every terrain renderer in this world scene.</summary>
        public void ApplyTerrainSettings()
        {
            foreach (TerrainRenderer renderer in GetTerrainRenderers())
            {
                renderer.SetSettings(GetTerrainSettings(renderer.transform));
            }
        }

        /// <summary>
        /// Assigns shared vegetation settings to the terrain providers, InstanceSets and InstanceContainers in this
        /// world scene.
        /// </summary>
        public void SetVegetationSettings(VegetationSettings settings)
        {
            ApplyToSources(settings);
            _vegetationSettings = settings;
        }

        /// <summary>Finds this scene's terrain providers, including inactive terrain objects.</summary>
        public TerrainVegetationProvider[] GetVegetationProviders()
        {
            return FindInScene<TerrainVegetationProvider>();
        }

        /// <summary>Finds this scene's InstanceSets, including inactive objects.</summary>
        public InstanceSet[] GetInstanceSets()
        {
            return FindInScene<InstanceSet>();
        }

        /// <summary>Finds this scene's InstanceContainers, including inactive objects.</summary>
        public InstanceContainer[] GetInstanceContainers()
        {
            return FindInScene<InstanceContainer>();
        }

        /// <summary>Applies the shared policy without changing source terrain data or placements.</summary>
        public void ApplyVegetationSettings()
        {
            ApplyToSources(_vegetationSettings);
        }

        private T[] FindInScene<T>() where T : Component
        {
            var scene = gameObject.scene;
            if (!scene.IsValid() || !scene.isLoaded) return Array.Empty<T>();
            var components = new List<T>();
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                components.AddRange(root.GetComponentsInChildren<T>(true));
            }
            return components.ToArray();
        }

        // A failed assignment restores the earlier settings of every source, so the world does not mix two policies.
        private void ApplyToSources(VegetationSettings settings)
        {
            var rollback = new List<Action>();
            try
            {
                foreach (TerrainVegetationProvider provider in GetVegetationProviders())
                {
                    VegetationSettings previous = provider.VegetationSettings;
                    provider.SetVegetationSettings(settings);
                    rollback.Add(() => provider.SetVegetationSettings(previous));
                }
                foreach (InstanceSet set in GetInstanceSets())
                {
                    ScriptableObject previous = set.QualitySource;
                    set.SetQualitySource(settings);
                    rollback.Add(() => set.SetQualitySource(previous));
                }
                foreach (InstanceContainer container in GetInstanceContainers())
                {
                    ScriptableObject previous = container.QualitySource;
                    container.SetQualitySource(settings);
                    rollback.Add(() => container.SetQualitySource(previous));
                }
            }
            catch (Exception assignmentError)
            {
                Exception rollbackError = null;
                for (int i = rollback.Count - 1; i >= 0; i--)
                {
                    try
                    {
                        rollback[i]();
                    }
                    catch (Exception exception)
                    {
                        rollbackError ??= exception;
                    }
                }
                if (rollbackError != null)
                {
                    throw new AggregateException("Vegetation settings and rollback both failed.", assignmentError,
                        rollbackError);
                }
                throw;
            }
        }

        private void WarnIfAnotherControllerIsActive()
        {
            var scene = gameObject.scene;
            if (!scene.IsValid() || !scene.isLoaded) return;
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                foreach (LoogaWorldController controller in root.GetComponentsInChildren<LoogaWorldController>(true))
                {
                    if (controller != this && controller.isActiveAndEnabled)
                    {
                        Debug.LogWarning("More than one active Looga World Controller uses this scene. " +
                            "Vegetation settings can conflict.", this);
                        return;
                    }
                }
            }
        }

        private void OnValidate()
        {
            _terrainGroups ??= Array.Empty<TerrainSettingsGroup>();
            _instanceMaxDistance = Mathf.Max(1, _instanceMaxDistance);
            _cameras ??= Array.Empty<Camera>();
            _instanceRoots ??= Array.Empty<GameObject>();
            _ownedComponents ??= Array.Empty<Component>();
        }

        /// <summary>Assigns explicit cameras. An empty array selects base cameras in this scene.</summary>
        public void SetCameras(Camera[] cameras)
        {
            _cameras = cameras == null ? Array.Empty<Camera>() : (Camera[])cameras.Clone();
        }

        /// <summary>Assigns approved scene roots for shared BRG rendering.</summary>
        public void SetInstanceRoots(GameObject[] roots, InstanceMaterialProfile profile = null,
            float maxDistance = 1000f)
        {
            if (!float.IsFinite(maxDistance) || maxDistance <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxDistance));
            }
            _instanceRoots = roots == null ? Array.Empty<GameObject>() : (GameObject[])roots.Clone();
            _instanceMaterialProfile = profile;
            _instanceMaxDistance = maxDistance;
        }

        /// <summary>Enables or disables the terrain surface cache for scripted setup.</summary>
        public void ConfigureVirtualTexturing(bool enabled, VirtualTextureSettings settings = null)
        {
            _useTerrainVirtualTexturing = enabled;
            _virtualTextureSettings = settings;
        }

        /// <summary>Stores an automatically generated shared terrain-cache asset.</summary>
        public void SetVirtualTextureSettings(VirtualTextureSettings settings)
        {
            _virtualTextureSettings = settings;
        }

        /// <summary>Sets the main setup features for scripted scene installation.</summary>
        public void ConfigureFeatures(bool terrain, bool vegetation, bool visibility, bool hiZ,
            bool waterCameras, bool microSplat = true)
        {
            _renderTerrain = terrain;
            _renderVegetation = vegetation;
            _configureVisibility = visibility;
            _useHiZ = hiZ;
            _configureWaterCameras = waterCameras;
            _configureMicroSplat = microSplat;
        }

        /// <summary>Sets the folder for consumer-owned generated assets.</summary>
        public void SetGeneratedAssetFolder(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder) || !folder.Replace('\\', '/').StartsWith("Assets/", StringComparison.Ordinal))
            {
                throw new ArgumentException("The generated asset folder must be below Assets.", nameof(folder));
            }
            _generatedAssetFolder = folder.Replace('\\', '/').TrimEnd('/');
        }

        /// <summary>Stores setup ownership. Editor setup tools call this method.</summary>
        public void SetSetupState(Component[] components, ScriptableRendererData rendererData,
            ScriptableRendererFeature ownedFeature, string report)
        {
            _ownedComponents = components == null ? Array.Empty<Component>() : (Component[])components.Clone();
            _rendererData = rendererData;
            _ownedRendererFeature = ownedFeature;
            _lastReport = string.IsNullOrEmpty(report) ? "No setup result is available." : report;
        }

        /// <summary>Clears editor-owned setup records after native rendering is restored.</summary>
        public void ClearSetupState(string report)
        {
            _ownedComponents = Array.Empty<Component>();
            _ownedRendererFeature = null;
            _lastReport = report;
        }
    }
}
