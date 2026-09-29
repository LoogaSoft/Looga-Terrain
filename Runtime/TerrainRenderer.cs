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
        [SerializeField, Range(8, 128)] private int _patchCount = 64;
        [SerializeField, Range(32f, 512f)] private float _targetPatchPixels = 128f;
        [SerializeField] private bool _useGeometricError = true;
        [SerializeField, Range(0.25f, 16f)] private float _errorPixels = 2f;
        [SerializeField] private bool _frustumCulling = true;
        [SerializeField] private bool _castShadows = true;
        [SerializeField] private bool _batchSurfaceDraws = true;
        [SerializeField, Range(2, 16)] private int _minimumSurfaceBatchTiles = 4;

        private static readonly ProfilerMarker SubmissionMarker = new(ProfilerCategory.Scripts, "Looga Terrain.SelectAndSubmit");
        private static readonly ProfilerMarker SelectionMarker = new(ProfilerCategory.Scripts, "Looga Terrain.Selection");
        private static readonly ProfilerMarker BindingMarker = new(ProfilerCategory.Scripts, "Looga Terrain.Binding");
        private static readonly ProfilerMarker DrawMarker = new(ProfilerCategory.Scripts, "Looga Terrain.DrawSubmission");
        private static readonly List<TerrainRenderer> Active = new();
        private static readonly List<TerrainRenderer> Prepared = new();
        private static readonly Dictionary<Vector2, TerrainRenderer> TileLookup = new();
        private static readonly Dictionary<Camera, SelectionCommands> _cameraCommands = new();
        private static readonly List<Camera> _expiredCommandCameras = new();
        private static readonly Plane[] Planes = new Plane[6];
        private static readonly Vector4[] CameraValues = new Vector4[8];
        private static GraphicsBuffer _cameraParameters;
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
        internal bool SupportsVirtualTexturing => _surfaceMaterial && _surfaceMaterial.HasProperty("_LoogaVirtualTextureSurface");
        internal bool SupportsSurfaceBatching => _batchSurfaceDraws && _surfaceMaterial
            && _surfaceMaterial.shader && _surfaceMaterial.shader.name == "Looga/Terrain/Lit"
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
        internal float EffectiveErrorPixels => Mathf.Clamp(_errorPixels / _adaptiveQualityScale, 0.25f, 16);

        /// <summary>
        /// Gets the native terrain used for authoring and collision.
        /// </summary>
        public UnityEngine.Terrain Source => _terrain ? _terrain : GetComponent<UnityEngine.Terrain>();

        /// <summary>
        /// Gets the number of minimum patches along each tile edge.
        /// </summary>
        public int PatchCount => IsReady ? _activePatchCount : _patchCount;

        /// <summary>
        /// Gets whether the renderer owns active GPU resources.
        /// </summary>
        public bool IsReady => _compute && _material && _mesh;

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
            if (Active.Count == 0)
            {
                RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
            }
            Active.Add(this);
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
            _selectionVersion++;
            RefreshSource();
        }
        #endregion

        #region Configuration
        /// <summary>
        /// Assigns rendering assets and rebuilds resources. The source terrain data remains unchanged.
        /// </summary>
        public void Configure(ComputeShader selection, Material surface, int patchCount = 64)
        {
            Release();
            _initializationFailed = false;
            _selectionCompute = selection;
            _surfaceMaterial = surface;
            _patchCount = Mathf.Clamp(Mathf.ClosestPowerOfTwo(patchCount), 8, 128);
            if (isActiveAndEnabled)
            {
                RefreshCompatibility();
            }
        }

        /// <summary>
        /// Sets projected height-error tolerance and optional frustum culling. This method does not change source terrain data.
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
        /// Sets terrain shadow casting without changing source data or shadow reception.
        /// </summary>
        public void SetShadowCasting(bool enabled)
        {
            _castShadows = enabled;
        }

        /// <summary>Enables compatible multi-tile surface draws. Integrated materials keep their per-tile path.</summary>
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
            if (!IsReady || !Source || !Source.terrainData) return;
            RefreshSurface();
            _refreshControlReferences = true;
            _metadataDirty = true;
            _metadataRegion = new Rect(0, 0, 1, 1);
        }

        /// <summary>
        /// Requests visible and shadow patch counts without blocking the rendering thread.
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
            if (IsReady || _initializationFailed || !_selectionCompute || !_surfaceMaterial || !string.IsNullOrEmpty(_integrationDiagnostic)) return;
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
            if (!_surfaceMaterial.shader.isSupported || !_surfaceMaterial.HasProperty("_LoogaTerrainSurface"))
            {
                Diagnostic = "Assign Looga Terrain Lit or a Looga-exported MicroSplat material.";
                return;
            }

            try
            {
                _activePatchCount = _patchCount;
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
                _material = new Material(_surfaceMaterial) { hideFlags = HideFlags.HideAndDontSave, enableInstancing = true };
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

        #region Rendering
        private static void OnBeginCameraRendering(ScriptableRenderContext context, Camera camera)
        {
            if (!camera) return;
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
                CameraValues[1] = new Vector4(projection, camera.orthographic ? 1 : 0, 0, 0);
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
                if (!_notifiedHeight || layoutChanged)
                {
                    _metadataRegion = new Rect(0, 0, 1, 1);
                    MarkSurfaceChanged();
                }
            }
            if (_refreshControlReferences || _controlTextures == null
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
                if (buffers.SourceVersion != _sourceVersion || buffers.DynamicBindingVersion != _dynamicBindingVersion)
                {
                    buffers.ResetBindings();
                    buffers.DynamicBindingVersion = _dynamicBindingVersion;
                    BindSource(buffers.VisibleProperties);
                    BindSource(buffers.ShadowProperties);
                    buffers.SourceVersion = _sourceVersion;
                }
                _bindDynamicMaterialProperties?.Invoke(buffers.VisibleProperties);
                _bindDynamicMaterialProperties?.Invoke(buffers.ShadowProperties);
                object tile = VirtualTexture ? VirtualTexture.Tile : null;
                if (buffers.VirtualTextureTile != tile)
                {
                    if (VirtualTexture)
                    {
                        VirtualTexture.Bind(buffers.VisibleProperties);
                        VirtualTexture.Bind(buffers.ShadowProperties);
                    }
                    else
                    {
                        TerrainVirtualTexture.BindFallback(buffers.VisibleProperties);
                        TerrainVirtualTexture.BindFallback(buffers.ShadowProperties);
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
                    buffers.ShadowProperties.SetVector("_LoogaNeighborOffsets", buffers.NeighborOffsets);
                    buffers.VisibleProperties.SetBuffer(NeighborIds[index], map);
                    buffers.ShadowProperties.SetBuffer(NeighborIds[index], map);
                    buffers.NeighborMaps[index] = map;
                }
                if (buffers.NeighborMask != mask)
                {
                    buffers.VisibleProperties.SetInt(NeighborMaskId, mask);
                    buffers.ShadowProperties.SetInt(NeighborMaskId, mask);
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
                int relation = ClassifyBounds(_bounds);
                bool combined = !buffers.SurfacesBatched && _castShadows && !buffers.ShadowsBatched
                    && !buffers.OcclusionApplied && (relation > 0 || !_frustumCulling);
                var parameters = new RenderParams(_material)
                {
                    camera = camera, layer = gameObject.layer, worldBounds = _bounds,
                    matProps = combined ? buffers.ShadowProperties : buffers.VisibleProperties,
                    shadowCastingMode = combined ? ShadowCastingMode.On : ShadowCastingMode.Off,
                    receiveShadows = true, motionVectorMode = MotionVectorGenerationMode.Camera
                };
                if (!buffers.SurfacesBatched && (relation >= 0 || !_frustumCulling))
                {
                    // A fully visible tile can use one draw for its surface and all shadow casters.
                    Graphics.RenderMeshIndirect(parameters, _mesh, combined ? buffers.ShadowArguments : buffers.VisibleArguments, 1, buffers.ArgumentIndex);
                    buffers.SubmissionCount++;
                }
                if (_castShadows && !combined && !buffers.ShadowsBatched)
                {
                    parameters.matProps = buffers.ShadowProperties;
                    parameters.shadowCastingMode = ShadowCastingMode.ShadowsOnly;
                    Graphics.RenderMeshIndirect(parameters, _mesh, buffers.ShadowArguments, 1, buffers.ArgumentIndex);
                    buffers.SubmissionCount++;
                }
            }
            finally
            {
                DrawMarker.End();
            }
        }

        internal void BindVirtualTextureProducer(Material producer, MaterialPropertyBlock properties)
        {
            producer.CopyPropertiesFromMaterial(_materialSource ? _materialSource : _surfaceMaterial);
            BindSource(properties);
            TerrainVirtualTexture.BindFallback(properties);
        }

        internal void DrawVirtualTextureFeedback(Camera camera, CommandBuffer commands, Material material,
            uint tileId, Vector4 info, float derivativeScale)
        {
            if (!_cameras.TryGetValue(camera, out CameraBuffers buffers) || ClassifyBounds(_bounds) < 0) return;
            buffers.VisibleProperties.SetInt("_LoogaVTTile", (int)tileId);
            buffers.VisibleProperties.SetVector("_LoogaVTInfo", info);
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
        private void OnUndoRedo()
        {
            RefreshCompatibility();
            RefreshSource();
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
                MarkSurfaceChanged(TextureRegion(region, terrain.terrainData.heightmapResolution - 1));
            }
        }

        private void OnTextureChanged(UnityEngine.Terrain terrain, string textureName, RectInt region, bool synchronized)
        {
            if (terrain == Source)
            {
                _refreshControlReferences = true;
                _notifiedControls = true;
                int resolution = textureName == TerrainData.HolesTextureName
                    ? terrain.terrainData.holesResolution : terrain.terrainData.alphamapResolution;
                MarkSurfaceChanged(TextureRegion(region, resolution));
            }
        }
        #endregion

        private sealed class CameraBuffers
        {
            public object VirtualTextureTile = new object();
            public int SubmissionCount;
            public bool OcclusionApplied;
            public bool SurfacesBatched;
            public bool ShadowsBatched;
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
            public readonly MaterialPropertyBlock ShadowProperties = new();

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
                ShadowProperties.Clear();
                SourceVersion = -1;
                VirtualTextureTile = new object();
                NeighborMask = -1;
                NeighborOffsets = Vector4.one * -1;
                Array.Clear(NeighborMaps, 0, NeighborMaps.Length);
                VisibleProperties.SetBuffer(LoogaPatchesId, Visible);
                VisibleProperties.SetBuffer(LoogaLodMapId, LodMap);
                ShadowProperties.SetBuffer(LoogaPatchesId, Shadows);
                ShadowProperties.SetBuffer(LoogaLodMapId, LodMap);
                VisibleProperties.SetInt("_LoogaPatchOffset", PatchOffset);
                ShadowProperties.SetInt("_LoogaPatchOffset", PatchOffset);
                VisibleProperties.SetInt("_LoogaLodOffset", PatchOffset);
                ShadowProperties.SetInt("_LoogaLodOffset", PatchOffset);
            }
        }
    }
}
