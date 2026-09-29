using System;
using LoogaSoft.Instancing;
using LoogaSoft.Terrain.Instances;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace LoogaSoft.Terrain
{
    /// <summary>Stores one scene's Looga Terrain setup policy and owned objects.</summary>
    [AddComponentMenu("Looga/World Controller")]
    [DisallowMultipleComponent]
    public sealed class LoogaWorldController : MonoBehaviour
    {
        [Header("Terrain")]
        [SerializeField] private bool _renderTerrain = true;
        [SerializeField] private bool _renderVegetation = true;
        [SerializeField, Range(8, 128)] private int _patchCount = 64;
        [SerializeField, Range(0.25f, 16f)] private float _errorPixels = 2f;
        [SerializeField] private bool _castTerrainShadows = true;

        [Header("Vegetation")]
        [Tooltip("Shared tree and detail defaults with source-specific overrides. Null preserves each terrain's local settings.")]
        [SerializeField] private VegetationSettings _vegetationSettings;
        [Tooltip("Use the full-quality Looga vegetation renderer in Scene view. Terrain editing temporarily restores native rendering.")]
        [SerializeField] private bool _previewVegetationInEditor;

        [Header("Terrain Surface Cache")]
        [SerializeField] private bool _useTerrainVirtualTexturing;
        [SerializeField] private VirtualTextureSettings _virtualTextureSettings;

        [Header("Visibility")]
        [SerializeField] private bool _configureVisibility = true;
        [SerializeField] private bool _useHiZ = true;
        [SerializeField] private bool _cullTerrainShadows = true;
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
        [SerializeField] private bool _configureMicroVerse = true;
        [Tooltip("Optional generated-object roots refreshed after MicroVerse synchronization.")]
        [SerializeField] private Transform[] _microVerseObjectOutputs = Array.Empty<Transform>();
        [SerializeField] private string _generatedAssetFolder = "Assets/Looga/Generated/Terrain";

        [SerializeField, HideInInspector] private Component[] _ownedComponents = Array.Empty<Component>();
        [SerializeField, HideInInspector] private ScriptableRendererFeature _ownedRendererFeature;
        [SerializeField, HideInInspector] private string _lastReport = "This world has not been configured.";

        public bool RenderTerrain => _renderTerrain;
        public bool RenderVegetation => _renderVegetation;
        public int PatchCount => _patchCount;
        public float ErrorPixels => _errorPixels;
        public bool CastTerrainShadows => _castTerrainShadows;
        public bool UseTerrainVirtualTexturing => _useTerrainVirtualTexturing;
        public VirtualTextureSettings VirtualTextureSettings => _virtualTextureSettings;
        public bool ConfigureVisibility => _configureVisibility;
        public bool UseHiZ => _useHiZ;
        public bool CullTerrainShadows => _cullTerrainShadows;
        public bool ConfigureWaterCameras => _configureWaterCameras;
        public Camera[] Cameras => _cameras;
        public ScriptableRendererData RendererData => _rendererData;
        public GameObject[] InstanceRoots => _instanceRoots;
        public InstanceMaterialProfile InstanceMaterialProfile => _instanceMaterialProfile;
        public float InstanceMaxDistance => _instanceMaxDistance;
        public bool ConfigureMicroSplat => _configureMicroSplat;
        public bool ConfigureMicroVerse => _configureMicroVerse;
        public Transform[] MicroVerseObjectOutputs => _microVerseObjectOutputs;
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

        /// <summary>Assigns shared vegetation settings to the providers in this world scene.</summary>
        public void SetVegetationSettings(VegetationSettings settings)
        {
            ApplyToProviders(settings);
            _vegetationSettings = settings;
        }

        /// <summary>Finds this scene's terrain providers, including inactive terrain objects.</summary>
        public TerrainVegetationProvider[] GetVegetationProviders()
        {
            var scene = gameObject.scene;
            if (!scene.IsValid() || !scene.isLoaded) return Array.Empty<TerrainVegetationProvider>();
            var providers = new List<TerrainVegetationProvider>();
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                providers.AddRange(root.GetComponentsInChildren<TerrainVegetationProvider>(true));
            }
            return providers.ToArray();
        }

        /// <summary>Applies the shared policy without changing source terrain data or placements.</summary>
        public void ApplyVegetationSettings()
        {
            ApplyToProviders(_vegetationSettings);
        }

        private void ApplyToProviders(VegetationSettings settings)
        {
            TerrainVegetationProvider[] providers = GetVegetationProviders();
            var previous = new VegetationSettings[providers.Length];
            int assigned = 0;
            try
            {
                for (int i = 0; i < providers.Length; i++)
                {
                    previous[i] = providers[i].VegetationSettings;
                    providers[i].SetVegetationSettings(settings);
                    assigned++;
                }
            }
            catch (Exception assignmentError)
            {
                Exception rollbackError = null;
                for (int i = assigned - 1; i >= 0; i--)
                {
                    try
                    {
                        providers[i].SetVegetationSettings(previous[i]);
                    }
                    catch (Exception exception)
                    {
                        rollbackError ??= exception;
                    }
                }
                if (rollbackError != null)
                    throw new AggregateException("Vegetation settings and rollback both failed.", assignmentError, rollbackError);
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
            _patchCount = Mathf.Clamp(Mathf.ClosestPowerOfTwo(_patchCount), 8, 128);
            _errorPixels = Mathf.Clamp(_errorPixels, 0.25f, 16f);
            _instanceMaxDistance = Mathf.Max(1, _instanceMaxDistance);
            _cameras ??= Array.Empty<Camera>();
            _instanceRoots ??= Array.Empty<GameObject>();
            _microVerseObjectOutputs ??= Array.Empty<Transform>();
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

        /// <summary>Assigns public MicroVerse-generated object roots for synchronized BRG rendering.</summary>
        public void SetMicroVerseObjectOutputs(Transform[] outputs)
        {
            _microVerseObjectOutputs = outputs == null ? Array.Empty<Transform>() : (Transform[])outputs.Clone();
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
            bool terrainShadowCulling, bool waterCameras, bool microSplat = true, bool microVerse = true)
        {
            _renderTerrain = terrain;
            _renderVegetation = vegetation;
            _configureVisibility = visibility;
            _useHiZ = hiZ;
            _cullTerrainShadows = terrainShadowCulling;
            _configureWaterCameras = waterCameras;
            _configureMicroSplat = microSplat;
            _configureMicroVerse = microVerse;
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
