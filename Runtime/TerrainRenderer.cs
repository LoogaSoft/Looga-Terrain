using System;
using System.Collections.Generic;
using LoogaSoft.Instancing;
using UnityEngine;
using UnityEngine.Rendering;
using Unity.Profiling;

namespace LoogaSoft.Terrain
{
    /// <summary>Reports selected patch and triangle work for one terrain camera.</summary>
    public readonly struct TerrainQualityMetrics
    {
        public readonly uint VisiblePatches;
        /// <summary>Every patch of the camera selection, before frustum and occlusion culling. The shadow
        /// BatchRendererGroup selects its own patches.</summary>
        public readonly uint ShadowPatches;
        public readonly ulong VisibleTriangles;
        public readonly ulong ShadowTriangles;

        internal TerrainQualityMetrics(uint visiblePatches, uint shadowPatches, int patchResolution)
        {
            VisiblePatches = visiblePatches;
            ShadowPatches = shadowPatches;
            ulong trianglesPerPatch = (ulong)patchResolution * (ulong)patchResolution * 2;
            VisibleTriangles = visiblePatches * trianglesPerPatch;
            ShadowTriangles = shadowPatches * trianglesPerPatch;
        }
    }

    /// <summary>
    /// Renders native TerrainData through GPU-selected patches without changing terrain or collision data.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(UnityEngine.Terrain))]
    public sealed partial class TerrainRenderer : MonoBehaviour, IAdaptiveQualityTarget
    {
        [SerializeField] private ComputeShader _selectionCompute;
        [SerializeField] private Material _surfaceMaterial;
        [Tooltip("Shared settings. When assigned, they replace the patch count, error pixels, frustum culling, " +
                 "shadow and surface batching values of this component.")]
        [SerializeField] private TerrainRendererSettings _settings;
        [SerializeField, Range(8, 128)] private int _patchCount = 64;
        [SerializeField, Range(32f, 512f)] private float _targetPatchPixels = 128f;
        [SerializeField] private bool _useGeometricError = true;
        [SerializeField, Range(0.25f, 16f)] private float _errorPixels = 2f;
        [SerializeField] private bool _frustumCulling = true;
        [SerializeField] private bool _castShadows = true;
        [SerializeField] private bool _batchSurfaceDraws = true;
        [SerializeField, Range(2, 16)] private int _minimumSurfaceBatchTiles = 4;
        [Tooltip("Largest height error of the shadow patches, in shadow texels of each shadow split. Larger " +
                 "values draw coarser shadow geometry.")]
        [SerializeField, Range(0.25f, 8f)] private float _shadowErrorTexels = 1f;
        [Tooltip("Shader of light-space shadow patches. Empty uses the package shader in the editor only.")]
        [SerializeField] private Shader _shadowShader;

        private static readonly ProfilerMarker SubmissionMarker = new(ProfilerCategory.Scripts, "Looga Terrain.SelectAndSubmit");
        private static readonly ProfilerMarker SelectionMarker = new(ProfilerCategory.Scripts, "Looga Terrain.Selection");
        private static readonly ProfilerMarker BindingMarker = new(ProfilerCategory.Scripts, "Looga Terrain.Binding");
        private static readonly ProfilerMarker DrawMarker = new(ProfilerCategory.Scripts, "Looga Terrain.DrawSubmission");
        private static readonly List<TerrainRenderer> Active = new();

        /// <summary>
        /// Raised when a renderer is enabled, before it initializes. A component on the same object that was enabled
        /// first, such as a Looga terrain surface binding above the renderer, supplies its surface then.
        /// </summary>
        internal static event Action<TerrainRenderer> Enabling;
        private static readonly List<TerrainRenderer> Prepared = new();
        private static readonly Dictionary<Vector2, TerrainRenderer> TileLookup = new();
        private static readonly Dictionary<Camera, SelectionCommands> _cameraCommands = new();
        private static readonly List<Camera> _expiredCommandCameras = new();
        private static readonly Plane[] Planes = new Plane[6];
        private static readonly Vector4[] CameraValues = new Vector4[8];
        private static GraphicsBuffer _cameraParameters;
        // Shore detail: x = water level, y = scale of the error pixels for patches at that level (0 = off).
        private static Vector2 _shoreDetail;
        private static readonly int CameraParametersId = Shader.PropertyToID("CameraParameters");
        private static readonly string[] NeighborNames = { "_LoogaLodLeft", "_LoogaLodRight", "_LoogaLodBottom", "_LoogaLodTop" };
        private static readonly int[] NeighborIds = Array.ConvertAll(NeighborNames, Shader.PropertyToID);
        private static readonly int NeighborMaskId = Shader.PropertyToID("_LoogaNeighborMask");
        private static readonly int BuildGroupsId = Shader.PropertyToID("_BuildGroups");
        private static readonly int BuildLevelId = Shader.PropertyToID("_BuildLevel");
        private static readonly int CullId = Shader.PropertyToID("_Cull");
        private static readonly int ErrorPixelsId = Shader.PropertyToID("_ErrorPixels");
        private static readonly int FixedEdgesId = Shader.PropertyToID("_FixedEdges");
        private static readonly int HeightResolutionId = Shader.PropertyToID("_HeightResolution");
        private static readonly int LodMapId = Shader.PropertyToID("_LodMap");
        private static readonly int LoogaHeightInfoId = Shader.PropertyToID("_LoogaHeightInfo");
        private static readonly int LoogaHeightmapId = Shader.PropertyToID("_LoogaHeightmap");
        private static readonly int LoogaHolesId = Shader.PropertyToID("_LoogaHoles");
        private static readonly int HoleSourceId = Shader.PropertyToID("_HoleSource");
        private static readonly int HoleResultId = Shader.PropertyToID("_HoleResult");
        private static readonly int HoleSizeId = Shader.PropertyToID("_HoleSize");
        private static readonly uint[] NoHoles = { 0 };
        private static readonly int LoogaLodMapId = Shader.PropertyToID("_LoogaLodMap");
        private static readonly int LoogaOriginId = Shader.PropertyToID("_LoogaOrigin");
        private static readonly int LoogaPatchCountId = Shader.PropertyToID("_LoogaPatchCount");
        private static readonly int LoogaPatchResolutionId = Shader.PropertyToID("_LoogaPatchResolution");
        private static readonly int LoogaPatchesId = Shader.PropertyToID("_LoogaPatches");
        private static readonly int LoogaSizeId = Shader.PropertyToID("_LoogaSize");
        private static readonly int NodeCountId = Shader.PropertyToID("_NodeCount");
        private static readonly int NodeDataId = Shader.PropertyToID("_NodeData");
        private static readonly int PatchCountId = Shader.PropertyToID("_PatchCount");
        private static readonly int ShadowPatchesId = Shader.PropertyToID("_ShadowPatches");
        private static readonly int SourceHeightId = Shader.PropertyToID("_SourceHeight");
        private static readonly int TargetPixelsId = Shader.PropertyToID("_TargetPixels");
        private static readonly int TerrainOriginId = Shader.PropertyToID("_TerrainOrigin");
        private static readonly int TerrainSizeId = Shader.PropertyToID("_TerrainSize");
        private static readonly int UseErrorId = Shader.PropertyToID("_UseError");
        private static readonly int VisiblePatchesId = Shader.PropertyToID("_VisiblePatches");
        internal TerrainVirtualTexture VirtualTexture { get; set; }
        internal int SurfaceVersion => _sourceVersion;
        internal bool SupportsVirtualTexturing => ActiveSurfaceMaterial && ActiveSurfaceMaterial.HasProperty("_LoogaVirtualTextureSurface");
        internal bool SupportsSurfaceBatching => BatchSurfaceDraws && ActiveSurfaceMaterial
            && ActiveSurfaceMaterial.shader && ActiveSurfaceMaterial.shader.name == "Looga/Terrain/Lit"
            && !_materialSource && _bindMaterialProperties == null && _bindDynamicMaterialProperties == null
            && !VirtualTexture;
        private const int PatchResolution = 16;
        private const float UnityHeightScale = 65535f / 32766f;
        private readonly Dictionary<Camera, CameraBuffers> _cameras = new();
        private readonly List<Camera> _expiredCameras = new();
        private readonly TerrainRenderer[] _neighbors = new TerrainRenderer[4];
        private GraphicsBuffer _nodeData;
        private TerrainData _sourceData;
        private Texture _heightTexture;
        private uint _heightTextureRevision;
        private int _heightResolution;
        private Texture2D[] _controlTextures;
        private int _controlResolution;
        private ulong _controlRevision;
        private bool _refreshControlReferences = true;
        private Vector3 _origin;
        private Vector3 _size;
        private Bounds _bounds;
        private bool _metadataDirty = true;
        private Rect _metadataRegion = new(0, 0, 1, 1);
        private int _clearRegionKernel;
        /// <summary>Gets the node count rebuilt by the last height update.</summary>
        public int LastMetadataNodeCount { get; private set; }
        private bool _initializationFailed;
        private int _sourceVersion;
        private int _selectionVersion;
        private int _metadataVersion;
        private int _buildKernel;
        private int _clearKernel;
        // Holes state of the source terrain, from a GPU check after each hole change. Until the first result the
        // renderer assumes holes, so a terrain with holes never draws solid.
        private int _holeKernel = -1;
        private GraphicsBuffer _holeResult;
        private int _holeRequest;
        private bool _hasHoles = true;
        private Action<MaterialPropertyBlock> _bindMaterialProperties;
        private UnityEngine.Terrain _terrain;
        private ComputeShader _compute;
        private Material _material;
        private Mesh _mesh;
        private bool _ownsVisibility;
        [SerializeField, HideInInspector] private bool _originalDrawHeightmap;
        [SerializeField, HideInInspector] private bool _hasCapturedVisibility;
        private int _kernel;
        private int _activePatchCount;
        private float _adaptiveQualityScale = 1;
        internal float EffectiveTargetPatchPixels => Mathf.Clamp(_targetPatchPixels / _adaptiveQualityScale, 32, 512);
        internal float EffectiveErrorPixels => Mathf.Clamp(ErrorPixels / _adaptiveQualityScale, 0.25f, 16);
        // Values of the settings asset when one is assigned, otherwise the values of this component.
        private int SettingsPatchCount => _settings ? _settings.PatchCount : _patchCount;
        private float ErrorPixels => _settings ? _settings.ErrorPixels : _errorPixels;
        internal bool FrustumCulling => _settings ? _settings.FrustumCulling : _frustumCulling;
        internal bool CastShadows => _settings ? _settings.CastShadows : _castShadows;
        internal float ShadowErrorTexels => _settings ? _settings.ShadowErrorTexels : _shadowErrorTexels;
        private bool BatchSurfaceDraws => _settings ? _settings.BatchSurfaceDraws : _batchSurfaceDraws;
        internal int MinimumSurfaceBatchTiles =>
            _settings ? _settings.MinimumSurfaceBatchTiles : _minimumSurfaceBatchTiles;
        private TerrainRendererSettings _appliedSettings;
        private int _appliedSettingsRevision;

        /// <summary>
        /// Gets the native terrain used for authoring and collision.
        /// </summary>
        public UnityEngine.Terrain Source => _terrain ? _terrain : GetComponent<UnityEngine.Terrain>();

        /// <summary>
        /// Gets the shared settings. Null means that the values of this component apply.
        /// </summary>
        public TerrainRendererSettings Settings => _settings;

        /// <summary>
        /// Gets the number of minimum patches along each tile edge.
        /// </summary>
        public int PatchCount => IsReady ? _activePatchCount : SettingsPatchCount;

        /// <summary>
        /// Gets whether the renderer owns active GPU resources.
        /// </summary>
        public bool IsReady => _compute && _material && _mesh;

        /// <summary>
        /// True when the source terrain has holes, so surface shaders draw their clip variant. A GPU check updates it
        /// after each hole change. It is true until the first check ends.
        /// </summary>
        public bool HasHoles => _hasHoles;

        /// <summary>
        /// Gets the most recent setup failure. An empty value indicates no reported failure.
        /// </summary>
        public string Diagnostic { get; private set; } = string.Empty;

        /// <summary>
        /// Lets integrations bind tile textures when the source changes. Call RefreshSource after reference changes.
        /// </summary>
        public event Action<MaterialPropertyBlock> BindMaterialProperties
        {
            add
            {
                _bindMaterialProperties += value;
                MarkSurfaceChanged();
            }
            remove
            {
                _bindMaterialProperties -= value;
                MarkSurfaceChanged();
            }
        }

        #region Built-in
        private void OnEnable()
        {
            _initializationFailed = false;
            _terrain = GetComponent<UnityEngine.Terrain>();
            _appliedSettings = _settings;
            _appliedSettingsRevision = _settings ? _settings.Revision : 0;
            Enabling?.Invoke(this);
            if (Active.Count == 0)
            {
                RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
            }
            Active.Add(this);
            RefreshShadowGroup();
            NotifyShadowCastersChanged();
            TerrainCallbacks.heightmapChanged += OnHeightmapChanged;
#if UNITY_EDITOR
            UnityEditor.Undo.undoRedoPerformed += OnUndoRedo;
#endif
            TerrainCallbacks.textureChanged += OnTextureChanged;
            RefreshCompatibility();
        }

        private void OnDisable()
        {
            Active.Remove(this);
            RefreshShadowGroup();
            NotifyShadowCastersChanged();
            if (Active.Count == 0)
            {
                RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
                Prepared.Clear();
                TileLookup.Clear();
                foreach (SelectionCommands commands in _cameraCommands.Values)
                {
                    commands.Dispose();
                }
                _cameraCommands.Clear();
                _cameraParameters?.Dispose();
                _cameraParameters = null;
                TerrainVirtualTexture.ReleaseFallback();
            }
            TerrainCallbacks.heightmapChanged -= OnHeightmapChanged;
#if UNITY_EDITOR
            UnityEditor.Undo.undoRedoPerformed -= OnUndoRedo;
#endif
            TerrainCallbacks.textureChanged -= OnTextureChanged;
            Release();
            _hasCapturedVisibility = false;
        }

        private void OnValidate()
        {
            _patchCount = Mathf.Clamp(Mathf.ClosestPowerOfTwo(_patchCount), 8, 128);
            _targetPatchPixels = Mathf.Clamp(_targetPatchPixels, 32f, 512f);
            _errorPixels = Mathf.Clamp(_errorPixels, 0.25f, 16f);
            _minimumSurfaceBatchTiles = Mathf.Clamp(_minimumSurfaceBatchTiles, 2, 16);
            _shadowErrorTexels = Mathf.Clamp(_shadowErrorTexels, 0.25f, 8f);
            _selectionVersion++;
            if (isActiveAndEnabled)
            {
                RefreshShadowGroup();
            }
            NotifyShadowCastersChanged();
            RefreshSource();
        }
        #endregion

        #region Configuration
        /// <summary>
        /// Assigns rendering assets and rebuilds resources. The source terrain data remains unchanged. Player builds
        /// need the shadow shader (TerrainBatchedShadows.shader) for terrain shadows.
        /// </summary>
        public void Configure(ComputeShader selection, Material surface, int patchCount = 64, Shader shadowShader = null)
        {
            Release();
            _initializationFailed = false;
            _selectionCompute = selection;
            _surfaceMaterial = surface;
            if (shadowShader)
            {
                _shadowShader = shadowShader;
            }
            _patchCount = Mathf.Clamp(Mathf.ClosestPowerOfTwo(patchCount), 8, 128);
            if (isActiveAndEnabled)
            {
                RefreshCompatibility();
            }
        }

        /// <summary>
        /// Assigns shared settings. Null uses the values of this component again. The renderer builds its resources
        /// again when the patch count changes.
        /// </summary>
        public void SetSettings(TerrainRendererSettings settings)
        {
            if (_settings == settings) return;
            _settings = settings;
            SyncSettings();
        }

        // Applies a changed settings reference or a changed settings asset. Prepare calls it each camera render.
        private void SyncSettings()
        {
            int revision = _settings ? _settings.Revision : 0;
            if (_appliedSettings == _settings && _appliedSettingsRevision == revision) return;
            _appliedSettings = _settings;
            _appliedSettingsRevision = revision;
            _selectionVersion++;
            if (isActiveAndEnabled)
            {
                RefreshShadowGroup();
            }
            NotifyShadowCastersChanged();
            if (IsReady && _activePatchCount != SettingsPatchCount)
            {
                Release();
                _initializationFailed = false;
                RefreshCompatibility();
            }
        }

        /// <summary>
        /// Sets projected height-error tolerance and optional frustum culling of this component. Assigned settings
        /// replace these values. This method does not change source terrain data.
        /// </summary>
        public void SetLodQuality(float errorPixels, bool frustumCulling = true)
        {
            if (float.IsNaN(errorPixels) || float.IsInfinity(errorPixels)) throw new ArgumentOutOfRangeException(nameof(errorPixels));
            _errorPixels = Mathf.Clamp(errorPixels, 0.25f, 16f);
            _useGeometricError = true;
            _frustumCulling = frustumCulling;
            _selectionVersion++;
        }

        /// <summary>Sets projected patch size for controlled native-quality comparisons.</summary>
        public void SetProjectedQuality(float targetPatchPixels, bool frustumCulling = true)
        {
            if (!float.IsFinite(targetPatchPixels)) throw new ArgumentOutOfRangeException(nameof(targetPatchPixels));
            _targetPatchPixels = Mathf.Clamp(targetPatchPixels, 32f, 512f);
            _useGeometricError = false;
            _frustumCulling = frustumCulling;
            _selectionVersion++;
        }

        /// <summary>
        /// Sets terrain shadow casting of this component without changing source data or shadow reception. Assigned
        /// settings replace this value.
        /// </summary>
        public void SetShadowCasting(bool enabled)
        {
            if (_castShadows == enabled) return;
            _castShadows = enabled;
            RefreshShadowGroup();
            NotifyShadowCastersChanged();
        }

        /// <summary>
        /// Enables compatible multi-tile surface draws of this component. Integrated materials keep their per-tile
        /// path. Assigned settings replace these values.
        /// </summary>
        public void SetSurfaceBatching(bool enabled, int minimumTiles = 4)
        {
            _batchSurfaceDraws = enabled;
            _minimumSurfaceBatchTiles = Mathf.Clamp(minimumTiles, 2, 16);
        }

        /// <summary>Apply derived terrain quality without changing TerrainData or authored quality values.</summary>
        public void ApplyAdaptiveQuality(AdaptiveQualityState state)
        {
            float next = Mathf.Clamp(state.Scale, 0.25f, 1);
            if (Mathf.Approximately(next, _adaptiveQualityScale))
            {
                return;
            }
            _adaptiveQualityScale = next;
            _selectionVersion++;
        }

        /// <summary>
        /// Rebinds height, hole, and material textures after a public authoring update.
        /// </summary>
        public void RefreshSource()
        {
            if (!RebindSource()) return;
            NotifyShadowCastersChanged();
        }

        private bool RebindSource()
        {
            if (!IsReady || !Source || !Source.terrainData) return false;
            RefreshSurface();
            _refreshControlReferences = true;
            _metadataDirty = true;
            _metadataRegion = new Rect(0, 0, 1, 1);
            RequestHoleCheck();
            return true;
        }

        // Checks the holes texture of the source terrain on the GPU and reads the result back without blocking. Surface
        // shaders draw their clip variant only while the terrain has holes, because a clip stops early depth writes.
        private void RequestHoleCheck()
        {
            Texture holes = Source && Source.terrainData ? Source.terrainData.holesTexture : null;
            if (!holes || !_compute || _holeKernel < 0)
            {
                SetHasHoles(holes);
                return;
            }
            _holeResult ??= new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, sizeof(uint));
            _holeResult.SetData(NoHoles);
            _compute.SetTexture(_holeKernel, HoleSourceId, holes);
            _compute.SetBuffer(_holeKernel, HoleResultId, _holeResult);
            _compute.SetInts(HoleSizeId, holes.width, holes.height);
            _compute.Dispatch(_holeKernel, (holes.width + 7) / 8, (holes.height + 7) / 8, 1);
            int request = ++_holeRequest;
            AsyncGPUReadback.Request(_holeResult, result =>
            {
                if (result.hasError || !this || request != _holeRequest) return;
                SetHasHoles(result.GetData<uint>()[0] != 0);
            });
        }

        private void SetHasHoles(bool hasHoles)
        {
            if (_hasHoles == hasHoles) return;
            _hasHoles = hasHoles;
            RefreshSurface();
            NotifyShadowCastersChanged();
        }

        /// <summary>
        /// Requests visible and selected patch counts without blocking the rendering thread. The second count is the
        /// camera selection before frustum and occlusion culling.
        /// </summary>
        public void RequestPatchCounts(Camera camera, Action<uint, uint> completed)
        {
            if (!_cameras.TryGetValue(camera, out CameraBuffers buffers))
            {
                completed?.Invoke(0, 0);
                return;
            }

            int offset = buffers.ArgumentIndex * GraphicsBuffer.IndirectDrawIndexedArgs.size + sizeof(uint);
            AsyncGPUReadback.Request(buffers.VisibleArguments, sizeof(uint), offset, request =>
            {
                if (request.hasError || !this || !buffers.ShadowArguments.IsValid()) return;
                uint visible = request.GetData<uint>()[0];
                AsyncGPUReadback.Request(buffers.ShadowArguments, sizeof(uint), offset, shadowRequest =>
                {
                    if (shadowRequest.hasError || !this) return;
                    completed?.Invoke(visible, shadowRequest.GetData<uint>()[0]);
                });
            });
        }

        /// <summary>Requests selected patch and triangle work without blocking the rendering thread.</summary>
        public void RequestQualityMetrics(Camera camera, Action<TerrainQualityMetrics> completed)
        {
            RequestPatchCounts(camera, (visible, shadows) =>
                completed?.Invoke(new TerrainQualityMetrics(visible, shadows, PatchResolution)));
        }

        /// <summary>
        /// Gets the indirect draw submissions from the most recent rendering of this camera.
        /// </summary>
        public int GetLastSubmissionCount(Camera camera)
        {
            return _cameras.TryGetValue(camera, out CameraBuffers buffers) ? buffers.SubmissionCount : 0;
        }
        #endregion

        #region Resources
        private void Initialize()
        {
            Material surface = ActiveSurfaceMaterial;
            if (IsReady || _initializationFailed || !_selectionCompute || !surface || !string.IsNullOrEmpty(_integrationDiagnostic)) return;
            _terrain = GetComponent<UnityEngine.Terrain>();
            if (!_terrain || !_terrain.terrainData) return;
            if (!SystemInfo.supportsComputeShaders || !SystemInfo.supportsInstancing || !GraphicsSettings.currentRenderPipeline)
            {
                Diagnostic = "Compute shaders, instancing, and a scriptable render pipeline are required.";
                return;
            }
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D12)
            {
                Diagnostic = "This preview requires Direct3D 12. Direct3D 11 has an unresolved indirect-buffer issue.";
                return;
            }
            if (Quaternion.Angle(transform.rotation, Quaternion.identity) > 0.01f || Vector3.Distance(transform.lossyScale, Vector3.one) > 0.001f)
            {
                Diagnostic = "Terrain must use world scale one and no rotation.";
                return;
            }
            if (!surface.shader.isSupported || !surface.HasProperty("_LoogaTerrainSurface"))
            {
                Diagnostic = "Assign Looga Terrain Lit, a Looga terrain surface or a Looga-exported MicroSplat material.";
                return;
            }

            try
            {
                _activePatchCount = SettingsPatchCount;
                _compute = Instantiate(_selectionCompute);
                _compute.hideFlags = HideFlags.HideAndDontSave;
                _cameraParameters ??= new GraphicsBuffer(GraphicsBuffer.Target.Constant, 8, 16);
                _compute.SetConstantBuffer(CameraParametersId, _cameraParameters, 0, 128);
                _kernel = _compute.FindKernel("SelectPatches");
                if (!_compute.HasKernel("SelectBatch") || !_compute.HasKernel("ResetBatchArguments") || !_compute.HasKernel("CopyBatchNodes"))
                {
                    throw new InvalidOperationException("The terrain selection shader requires the batch kernels.");
                }
                _buildKernel = _compute.FindKernel("BuildNodeData");
                _clearKernel = _compute.FindKernel("ClearNodeData");
                _clearRegionKernel = _compute.FindKernel("ClearNodeRegion");
                _holeKernel = _compute.HasKernel("DetectHoles") ? _compute.FindKernel("DetectHoles") : -1;
                _hasHoles = true;
                int nodeCount = (_activePatchCount * _activePatchCount * 4 - 1) / 3;
                _nodeData = new GraphicsBuffer(GraphicsBuffer.Target.Structured, nodeCount, 16);
                _compute.SetBuffer(_kernel, NodeDataId, _nodeData);
                _compute.SetBuffer(_buildKernel, NodeDataId, _nodeData);
                _compute.SetBuffer(_clearKernel, NodeDataId, _nodeData);
                _compute.SetBuffer(_clearRegionKernel, NodeDataId, _nodeData);
                _compute.SetInt(NodeCountId, nodeCount);
                _sourceData = null;
                _metadataDirty = true;
                MarkSurfaceChanged();
                _material = new Material(surface) { hideFlags = HideFlags.HideAndDontSave, enableInstancing = true };
                RefreshSurface();
                _mesh = PatchMesh.Create(PatchResolution);
                if (!_hasCapturedVisibility)
                {
                    _originalDrawHeightmap = _terrain.drawHeightmap;
                    _hasCapturedVisibility = true;
                }
                _ownsVisibility = true;
                _terrain.drawHeightmap = false;
                Diagnostic = string.Empty;
                RequestHoleCheck();
            }
            catch (Exception exception)
            {
                Diagnostic = exception.Message;
                _initializationFailed = true;
                Release();
                Debug.LogException(exception, this);
            }
        }

        private void Release(bool restoreVisibility = true)
        {
            // Remove recorded resource references before disposing their buffers.
            foreach (SelectionCommands commands in _cameraCommands.Values)
            {
                commands.Invalidate();
            }
            _cameras.Clear();
            _nodeData?.Dispose();
            _nodeData = null;
            // A pending hole check result belongs to the released resources.
            _holeRequest++;
            _holeResult?.Dispose();
            _holeResult = null;
            DestroyResource(_compute);
            DestroyResource(_material);
            DestroyResource(_mesh);
            _compute = null;
            _material = null;
            _mesh = null;
            if (restoreVisibility && _ownsVisibility && _terrain)
            {
                _terrain.drawHeightmap = _originalDrawHeightmap;
            }
            _ownsVisibility = false;
        }

        // The scene saves the native heightmap as hidden while this renderer draws the tile. When the renderer cannot
        // draw after a load (for example, an integration blocks it), the native terrain must draw again.
        private void RestoreCapturedVisibility()
        {
            if (IsReady || !_hasCapturedVisibility || !_terrain) return;
            if (_terrain.drawHeightmap != _originalDrawHeightmap)
            {
                _terrain.drawHeightmap = _originalDrawHeightmap;
            }
        }

        private static void DestroyResource(UnityEngine.Object resource)
        {
            if (!resource) return;
            if (Application.isPlaying)
            {
                Destroy(resource);
            }
            else
            {
                DestroyImmediate(resource);
            }
        }
        #endregion

        /// <summary>
        /// Draws the terrain finer where its heights reach a water level. On a flat beach a few centimetres of height
        /// error move the water edge by a metre, so the edge jumps when a patch changes its detail. Patches whose height
        /// range reaches the water level use the error pixels times the scale.
        /// </summary>
        public static void SetShoreDetail(float waterLevel, float errorScale)
        {
            _shoreDetail = new Vector2(waterLevel, Mathf.Clamp01(errorScale));
        }

        /// <summary>Removes the finer terrain at a water level.</summary>
        public static void ClearShoreDetail()
        {
            _shoreDetail = Vector2.zero;
        }

        #region Rendering
        private static void OnBeginCameraRendering(ScriptableRenderContext context, Camera camera)
        {
            if (!camera) return;
            // Shadow culls of the BatchRendererGroup run after this, inside the camera's render.
            _shadowGroupCamera = camera;
            SubmissionMarker.Begin();
            try
            {
                Prepared.Clear();
                TileLookup.Clear();
                GeometryUtility.CalculateFrustumPlanes(camera, Planes);
                for (int index = 0; index < Planes.Length; index++)
                {
                    CameraValues[index + 2] = new Vector4(Planes[index].normal.x, Planes[index].normal.y,
                        Planes[index].normal.z, Planes[index].distance);
                }
                float projection = camera.orthographic
                    ? camera.pixelHeight / Mathf.Max(0.01f, 2f * camera.orthographicSize)
                    : camera.pixelHeight / (2f * Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad));
                CameraValues[0] = camera.transform.position;
                CameraValues[1] = new Vector4(projection, camera.orthographic ? 1 : 0, _shoreDetail.x, _shoreDetail.y);
                foreach (TerrainRenderer renderer in Active)
                {
                    if (!renderer.Prepare(camera)) continue;
                    Prepared.Add(renderer);
                    TileLookup[new Vector2(renderer._origin.x, renderer._origin.z)] = renderer;
                }
                _cameraParameters?.SetData(CameraValues);
                // Finish every tile selection before a draw can read its neighbors.
                SelectionMarker.Begin();
                try
                {
                    foreach (TerrainRenderer renderer in Prepared)
                    {
                        renderer.FindNeighbors();
                    }
                    if (!ExecuteSelection(camera)) return;
                }
                finally
                {
                    SelectionMarker.End();
                }
                TerrainVirtualTexture.BeginCamera(camera, Prepared);
                foreach (TerrainRenderer renderer in Prepared)
                {
                    renderer.Bind(camera, renderer._cameras[camera]);
                }
                if (camera.TryGetComponent(out TerrainOcclusionCamera occlusion) && occlusion.isActiveAndEnabled)
                {
                    occlusion.Cull(camera, Prepared);
                }
                SubmitSurfaceBatches(camera);
                foreach (TerrainRenderer renderer in Prepared)
                {
                    renderer.Submit(camera, renderer._cameras[camera]);
                }
                TerrainVirtualTexture.EndCamera(camera, Prepared);
            }
            finally
            {
                SubmissionMarker.End();
            }
        }

        private bool Prepare(Camera camera)
        {
            SyncSettings();
            RefreshCompatibility();
            if (!IsReady || !Source || !Source.terrainData || !string.IsNullOrEmpty(_integrationDiagnostic)) return false;
            if (camera.stereoEnabled)
            {
                Release();
                _initializationFailed = true;
                Diagnostic = "XR rendering is not supported in this preview. Native terrain rendering was restored.";
                return false;
            }
            if ((camera.cullingMask & (1 << gameObject.layer)) == 0) return false;
            TerrainData data = Source.terrainData;
            Vector3 origin = transform.position;
            Vector3 size = data.size;
            if (_sourceData != data || _origin != origin || _size != size)
            {
                _sourceData = data;
                _origin = origin;
                _size = size;
                _selectionVersion++;
                _bounds = new Bounds(origin + size * 0.5f, size + Vector3.one);
                RefreshSource();
                _compute.SetVector(TerrainOriginId, origin);
                _compute.SetVector(TerrainSizeId, size);
                _compute.SetInt(PatchCountId, _activePatchCount);
            }
            RefreshTextureRevisions(data);
            if (_metadataDirty)
            {
                BuildMetadata();
            }
            _expiredCameras.Clear();
            foreach (Camera existingCamera in _cameras.Keys)
            {
                if (!existingCamera)
                {
                    _expiredCameras.Add(existingCamera);
                }
            }
            foreach (Camera expiredCamera in _expiredCameras)
            {
                _cameras.Remove(expiredCamera);
            }
            return true;
        }

        private bool HasDestroyedControl()
        {
            foreach (Texture2D control in _controlTextures)
            {
                if (!control) return true;
            }
            return false;
        }

        private void RefreshTextureRevisions(TerrainData data)
        {
            Texture height = data.heightmapTexture;
            if (_heightTexture != height || _heightResolution != data.heightmapResolution
                || _heightTextureRevision != height.updateCount)
            {
                bool layoutChanged = _heightResolution != data.heightmapResolution;
                _heightTexture = height;
                _heightResolution = data.heightmapResolution;
                _heightTextureRevision = height.updateCount;
                _metadataDirty = true;
                // A height callback already gave the changed region to the shadows.
                if (!_notifiedHeight || layoutChanged)
                {
                    NotifyShadowCastersChanged();
                    _metadataRegion = new Rect(0, 0, 1, 1);
                    MarkSurfaceChanged();
                }
            }
            // A save of the terrain data asset destroys the alphamap textures and makes new ones without a callback.
            if (_refreshControlReferences || _controlTextures == null || HasDestroyedControl()
                || _controlTextures.Length != data.alphamapTextureCount || _controlResolution != data.alphamapResolution)
            {
                bool layoutChanged = _controlTextures == null || _controlTextures.Length != data.alphamapTextureCount
                    || _controlResolution != data.alphamapResolution;
                _controlTextures = data.alphamapTextures;
                _controlResolution = data.alphamapResolution;
                _refreshControlReferences = false;
                if (!_notifiedControls || layoutChanged)
                {
                    MarkSurfaceChanged();
                }
            }
            ulong revision = 0;
            foreach (Texture2D control in _controlTextures)
            {
                revision = unchecked(revision * 397 ^ control.updateCount);
            }
            if (_controlRevision != revision)
            {
                _controlRevision = revision;
                if (!_notifiedControls)
                {
                    MarkSurfaceChanged();
                }
            }
            _notifiedHeight = false;
            _notifiedControls = false;
        }

        private void BuildMetadata()
        {
            _compute.SetTexture(_buildKernel, SourceHeightId, _sourceData.heightmapTexture);
            _compute.SetInt(HeightResolutionId, _sourceData.heightmapResolution);
            LastMetadataNodeCount = 0;
            // Include shared samples and bilinear height support on each side of the changed region.
            float halo = 1f / (_sourceData.heightmapResolution - 1);
            for (int size = 1, level = 0; size <= _activePatchCount; size *= 2, level++)
            {
                int axis = _activePatchCount / size;
                int x = Mathf.Clamp(Mathf.FloorToInt((_metadataRegion.xMin - halo) * axis), 0, axis - 1);
                int y = Mathf.Clamp(Mathf.FloorToInt((_metadataRegion.yMin - halo) * axis), 0, axis - 1);
                int width = Mathf.Clamp(Mathf.CeilToInt((_metadataRegion.xMax + halo) * axis), x + 1, axis) - x;
                int height = Mathf.Clamp(Mathf.CeilToInt((_metadataRegion.yMax + halo) * axis), y + 1, axis) - y;
                int samples = Mathf.Max(1, Mathf.CeilToInt((_sourceData.heightmapResolution - 1f) * size / _activePatchCount));
                int groups = (samples + 63) / 64;
                _compute.SetInt(BuildLevelId, level);
                _compute.SetInt(BuildGroupsId, groups);
                _compute.SetInts("_BuildOrigin", x, y);
                _compute.SetInts("_BuildExtent", width, height);
                _compute.Dispatch(_clearRegionKernel, (width + 7) / 8, (height + 7) / 8, 1);
                _compute.Dispatch(_buildKernel, width * groups, height * groups, 1);
                LastMetadataNodeCount += width * height;
            }
            _metadataRegion = default;
            _metadataDirty = false;
            _metadataVersion++;
        }

        private void FindNeighbors()
        {
            Vector2 position = new(_origin.x, _origin.z);
            _neighbors[0] = FindNeighbor(position - new Vector2(_size.x, 0));
            _neighbors[1] = FindNeighbor(position + new Vector2(_size.x, 0));
            _neighbors[2] = FindNeighbor(position - new Vector2(0, _size.z));
            _neighbors[3] = FindNeighbor(position + new Vector2(0, _size.z));
        }

        private TerrainRenderer FindNeighbor(Vector2 position)
        {
            if (!TileLookup.TryGetValue(position, out TerrainRenderer neighbor)) return null;
            if (neighbor._activePatchCount != _activePatchCount || neighbor._size != _size || neighbor._origin.y != _origin.y) return null;
            return neighbor;
        }

        private static bool ExecuteSelection(Camera camera)
        {
            _expiredCommandCameras.Clear();
            foreach (Camera existingCamera in _cameraCommands.Keys)
            {
                if (!existingCamera)
                {
                    _expiredCommandCameras.Add(existingCamera);
                }
            }
            foreach (Camera expiredCamera in _expiredCommandCameras)
            {
                _cameraCommands[expiredCamera].Dispose();
                _cameraCommands.Remove(expiredCamera);
            }
            if (!_cameraCommands.TryGetValue(camera, out SelectionCommands commands))
            {
                commands = new SelectionCommands();
                _cameraCommands.Add(camera, commands);
            }
            try
            {
                commands.Execute(camera);
                return true;
            }
            catch (Exception exception)
            {
                foreach (TerrainRenderer renderer in Prepared)
                {
                    renderer.Diagnostic = exception.Message;
                    renderer._initializationFailed = true;
                    renderer.Release();
                }
                Debug.LogException(exception);
                return false;
            }
        }

        private static int ClassifyBounds(Bounds bounds)
        {
            bool inside = true;
            foreach (Plane plane in Planes)
            {
                Vector3 normal = plane.normal;
                Vector3 extent = bounds.extents;
                float radius = Mathf.Abs(normal.x) * extent.x + Mathf.Abs(normal.y) * extent.y + Mathf.Abs(normal.z) * extent.z;
                float distance = plane.GetDistanceToPoint(bounds.center);
                if (distance < -radius) return -1;
                inside &= distance >= radius;
            }
            return inside ? 1 : 0;
        }

        private void Bind(Camera camera, CameraBuffers buffers)
        {
            BindingMarker.Begin();
            try
            {
                // A tile that starts or stops casting must also move in or out of the cached static shadows.
                bool shadowsFromGroup = UsesShadowGroup();
                if (buffers.ShadowsFromGroup != shadowsFromGroup)
                {
                    buffers.ShadowsFromGroup = shadowsFromGroup;
                    NotifyShadowCastersChanged();
                }
                if (buffers.SourceVersion != _sourceVersion || buffers.DynamicBindingVersion != _dynamicBindingVersion)
                {
                    buffers.ResetBindings();
                    buffers.DynamicBindingVersion = _dynamicBindingVersion;
                    BindSource(buffers.VisibleProperties);
                    buffers.SourceVersion = _sourceVersion;
                }
                _bindDynamicMaterialProperties?.Invoke(buffers.VisibleProperties);
                object tile = VirtualTexture ? VirtualTexture.Tile : null;
                if (buffers.VirtualTextureTile != tile)
                {
                    if (VirtualTexture)
                    {
                        VirtualTexture.Bind(buffers.VisibleProperties);
                    }
                    else
                    {
                        TerrainVirtualTexture.BindFallback(buffers.VisibleProperties);
                    }
                    buffers.VirtualTextureTile = tile;
                }
                int mask = 0;
                for (int index = 0; index < _neighbors.Length; index++)
                {
                    TerrainRenderer neighbor = _neighbors[index];
                    GraphicsBuffer map = neighbor ? neighbor._cameras[camera].LodMap : buffers.LodMap;
                    if (neighbor)
                    {
                        mask |= 1 << index;
                    }
                    int offset = neighbor ? neighbor._cameras[camera].PatchOffset : buffers.PatchOffset;
                    if (buffers.NeighborMaps[index] == map && buffers.NeighborOffsets[index] == offset) continue;
                    buffers.NeighborOffsets[index] = offset;
                    buffers.VisibleProperties.SetVector("_LoogaNeighborOffsets", buffers.NeighborOffsets);
                    buffers.VisibleProperties.SetBuffer(NeighborIds[index], map);
                    buffers.NeighborMaps[index] = map;
                }
                if (buffers.NeighborMask != mask)
                {
                    buffers.VisibleProperties.SetInt(NeighborMaskId, mask);
                    buffers.NeighborMask = mask;
                }
            }
            finally
            {
                BindingMarker.End();
            }
        }

        private void Submit(Camera camera, CameraBuffers buffers)
        {
            DrawMarker.Begin();
            try
            {
                if (buffers.SurfacesBatched || (FrustumCulling && ClassifyBounds(_bounds) < 0)) return;
                // Shadows come from the shadow BatchRendererGroup, so the surface draw casts none.
                var parameters = new RenderParams(_material)
                {
                    camera = camera, layer = gameObject.layer, worldBounds = _bounds,
                    matProps = buffers.VisibleProperties, shadowCastingMode = ShadowCastingMode.Off,
                    receiveShadows = true, motionVectorMode = MotionVectorGenerationMode.Camera
                };
                Graphics.RenderMeshIndirect(parameters, _mesh, buffers.VisibleArguments, 1, buffers.ArgumentIndex);
                buffers.SubmissionCount++;
            }
            finally
            {
                DrawMarker.End();
            }
        }

        internal void BindVirtualTextureProducer(Material producer, MaterialPropertyBlock properties)
        {
            producer.CopyPropertiesFromMaterial(_materialSource ? _materialSource : ActiveSurfaceMaterial);
            BindSource(properties);
            TerrainVirtualTexture.BindFallback(properties);
        }

        internal void DrawVirtualTextureFeedback(Camera camera, CommandBuffer commands, Material material,
            uint tileId, Vector4 info, Vector4 sampling, float derivativeScale)
        {
            if (!_cameras.TryGetValue(camera, out CameraBuffers buffers) || ClassifyBounds(_bounds) < 0) return;
            buffers.VisibleProperties.SetInt("_LoogaVTTile", (int)tileId);
            buffers.VisibleProperties.SetVector("_LoogaVTInfo", info);
            buffers.VisibleProperties.SetVector("_LoogaVTSampling", sampling);
            buffers.VisibleProperties.SetFloat("_LoogaVTDerivativeScale", derivativeScale);
            commands.DrawMeshInstancedIndirect(_mesh, 0, material, 0, buffers.VisibleArguments,
                buffers.ArgumentIndex * GraphicsBuffer.IndirectDrawIndexedArgs.size, buffers.VisibleProperties);
        }

        private void BindSource(MaterialPropertyBlock properties)
        {
            TerrainData data = Source.terrainData;
            properties.SetVector(LoogaOriginId, transform.position);
            properties.SetVector(LoogaSizeId, data.size);
            properties.SetVector(LoogaHeightInfoId, new Vector4(data.heightmapResolution, UnityHeightScale, 0f, 0f));
            properties.SetInt(LoogaPatchCountId, _activePatchCount);
            properties.SetInt(LoogaPatchResolutionId, PatchResolution);
            properties.SetTexture(LoogaHeightmapId, data.heightmapTexture);
            properties.SetTexture(LoogaHolesId, data.holesTexture ? data.holesTexture : Texture2D.whiteTexture);
            _bindMaterialProperties?.Invoke(properties);
        }

#if UNITY_EDITOR
        // Most undo steps change no terrain. Changed heights and holes reach the shadows through the terrain callbacks
        // and the height texture revision.
        private void OnUndoRedo()
        {
            RefreshCompatibility();
            RebindSource();
        }
#endif

        private void OnHeightmapChanged(UnityEngine.Terrain terrain, RectInt region, bool synchronized)
        {
            if (terrain == Source)
            {
                Rect changed = TextureRegion(region, terrain.terrainData.heightmapResolution - 1);
                _metadataRegion = _metadataDirty ? Rect.MinMaxRect(Mathf.Min(_metadataRegion.xMin, changed.xMin),
                    Mathf.Min(_metadataRegion.yMin, changed.yMin), Mathf.Max(_metadataRegion.xMax, changed.xMax),
                    Mathf.Max(_metadataRegion.yMax, changed.yMax)) : changed;
                _metadataDirty = true;
                _notifiedHeight = true;
                MarkSurfaceChanged(changed);
                NotifyShadowCastersChanged(changed);
            }
        }

        private void OnTextureChanged(UnityEngine.Terrain terrain, string textureName, RectInt region, bool synchronized)
        {
            if (terrain == Source)
            {
                _refreshControlReferences = true;
                _notifiedControls = true;
                bool holes = textureName == TerrainData.HolesTextureName;
                int resolution = holes ? terrain.terrainData.holesResolution : terrain.terrainData.alphamapResolution;
                Rect changed = TextureRegion(region, resolution);
                MarkSurfaceChanged(changed);
                if (holes)
                {
                    RequestHoleCheck();
                    NotifyShadowCastersChanged(changed);
                }
            }
        }
        #endregion

        private sealed class CameraBuffers
        {
            public object VirtualTextureTile = new object();
            public int SubmissionCount;
            public bool OcclusionApplied;
            public bool SurfacesBatched;
            public bool ShadowsFromGroup;
            public int SourceVersion = -1;
            public int DynamicBindingVersion = -1;
            public int NeighborMask = -1;
            public Vector4 NeighborOffsets = Vector4.one * -1;
            public readonly int PatchOffset;
            public readonly int ArgumentIndex;
            public readonly GraphicsBuffer[] NeighborMaps = new GraphicsBuffer[4];
            public readonly GraphicsBuffer Visible;
            public readonly GraphicsBuffer Shadows;
            public readonly GraphicsBuffer LodMap;
            public readonly GraphicsBuffer VisibleArguments;
            public readonly GraphicsBuffer ShadowArguments;
            public readonly MaterialPropertyBlock VisibleProperties = new();

            public CameraBuffers(SelectionBatch batch, int index, int capacity)
            {
                ArgumentIndex = index;
                PatchOffset = index * capacity;
                Visible = batch.Visible;
                Shadows = batch.Shadows;
                LodMap = batch.LodMap;
                VisibleArguments = batch.VisibleArguments;
                ShadowArguments = batch.ShadowArguments;
                ResetBindings();
            }

            public void ResetBindings()
            {
                VisibleProperties.Clear();
                SourceVersion = -1;
                VirtualTextureTile = new object();
                NeighborMask = -1;
                NeighborOffsets = Vector4.one * -1;
                Array.Clear(NeighborMaps, 0, NeighborMaps.Length);
                VisibleProperties.SetBuffer(LoogaPatchesId, Visible);
                VisibleProperties.SetBuffer(LoogaLodMapId, LodMap);
                VisibleProperties.SetInt("_LoogaPatchOffset", PatchOffset);
                VisibleProperties.SetInt("_LoogaLodOffset", PatchOffset);
            }
        }
    }
}
