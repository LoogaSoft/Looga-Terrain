using LoogaSoft.Instancing;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace LoogaSoft.Terrain.Instances
{
    /// <summary>Renders supported native terrain vegetation without changing TerrainData.</summary>
    [ExecuteAlways, DisallowMultipleComponent, RequireComponent(typeof(UnityEngine.Terrain))]
    public sealed class TerrainVegetationProvider : MonoBehaviour, IInstanceSourceAdapter, IAdaptiveQualityTarget
    {
        private static readonly Dictionary<UnityEngine.Terrain, TerrainVegetationProvider> Owners = new();
        [SerializeField, Min(1)] private int _instanceLimit = 250000;
        [SerializeField] private InstanceMaterialProfile _materialProfile;
        [SerializeField] private string _worldSourceId = Guid.NewGuid().ToString("N");
        [SerializeField] private InstanceQualitySettings _treeQuality = InstanceQualitySettings.Default;
        [SerializeField] private InstanceQualitySettings _detailQuality = InstanceQualitySettings.Default;
        [SerializeField] private InstanceVisibilityMode _visibilityMode;
        [SerializeField] private VegetationSettings _vegetationSettings;
        private VegetationSettings _subscribedSettings;
        private float _adaptiveQualityScale = 1;
#if UNITY_EDITOR
        private Transform[] _editorObservers;

        /// <summary>True while an editor preview owns the temporary observer list.</summary>
        public bool EditorPreviewActive => _editorObservers != null && !Application.IsPlaying(gameObject);

        /// <summary>Uses editor cameras for temporary streaming without changing the serialized runtime observers.</summary>
        public void BeginEditorPreview(Transform[] observers)
        {
            if (Application.IsPlaying(gameObject))
            {
                throw new InvalidOperationException("Editor preview cannot replace runtime observers.");
            }
            if (observers == null || observers.Length == 0)
            {
                throw new ArgumentException("Editor preview requires at least one camera.", nameof(observers));
            }
            foreach (Transform observer in observers)
            {
                if (!observer || !observer.GetComponent<Camera>())
                {
                    throw new ArgumentException("Each preview observer must have a camera.", nameof(observers));
                }
            }
            bool unchanged = _editorObservers != null && _editorObservers.Length == observers.Length;
            for (int i = 0; unchanged && i < observers.Length; i++)
            {
                unchanged = _editorObservers[i] == observers[i];
            }
            if (unchanged) return;
            Release();
            _editorObservers = (Transform[])observers.Clone();
            InvalidateAll();
        }

        /// <summary>Releases preview resources and restores the previous native foliage visibility.</summary>
        public void EndEditorPreview()
        {
            if (_editorObservers == null) return;
            Release();
            _editorObservers = null;
            InvalidateAll();
        }
#endif

        /// <summary>Assigned shared policy, or null when local quality controls this provider.</summary>
        public VegetationSettings VegetationSettings => _vegetationSettings;
        public InstanceQualitySettings LocalTreeQuality => _treeQuality;
        public InstanceQualitySettings LocalDetailQuality => _detailQuality;
        public InstanceVisibilityMode VisibilityMode => _visibilityMode;

        /// <summary>Get the authored policy for a prefab or detail texture.</summary>
        public InstanceQualitySettings GetEffectiveQuality(VegetationKind kind, UnityEngine.Object source)
        {
            if (_vegetationSettings) return _vegetationSettings.Resolve(kind, source);
            if (kind == VegetationKind.Tree) return _treeQuality;
            if (kind == VegetationKind.Detail) return _detailQuality;
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        /// <summary>Get the current policy after adaptive quality scaling.</summary>
        public InstanceQualitySettings GetCurrentQuality(VegetationKind kind, UnityEngine.Object source)
        {
            return ResolveQuality(kind, source);
        }

        /// <summary>Assign a shared policy. Null restores the local serialized quality.</summary>
        public void SetVegetationSettings(VegetationSettings settings)
        {
            if (_vegetationSettings == settings)
            {
                ApplyQualityPolicy();
                return;
            }
            VegetationSettings previous = _vegetationSettings;
            _vegetationSettings = settings;
            try
            {
                ApplyQualityPolicy();
                SubscribeToSettings();
                _qualityDiagnostic = string.Empty;
                if (Diagnostic.StartsWith("Vegetation settings were not applied:", StringComparison.Ordinal))
                    Diagnostic = string.Empty;
            }
            catch
            {
                _vegetationSettings = previous;
                SubscribeToSettings();
                throw;
            }
        }

        /// <summary>Set separate tree and detail quality policies. Existing terrain data remains unchanged.</summary>
        public void ConfigureQuality(InstanceQualitySettings trees, InstanceQualitySettings details, InstanceVisibilityMode visibility)
        {
            trees.Validate(false);
            details.Validate(false);
            _treeQuality = trees;
            _detailQuality = details;
            _visibilityMode = visibility;
            ApplyQualityPolicy();
        }

        /// <summary>Source bytes uploaded by the current renderer lifetime, including streaming.</summary>
        public long TotalUploadedBytes => (_population?.Trees.UploadedBytes ?? 0) +
            (_population?.Details.UploadedBytes ?? 0) + (_stream?.TotalUploadedBytes ?? 0);

        /// <summary>Read current camera indirect counts for diagnostics. This allocates and stalls the GPU.</summary>
        public uint[] ReadCameraDrawCounts(Camera camera)
        {
            if (_stream != null) return _stream.ReadCameraDrawCounts(camera);
            var counts = new List<uint>();
            if (_population != null)
            {
                counts.AddRange(_population.Trees.ReadCameraDrawCounts(camera));
                counts.AddRange(_population.Details.ReadCameraDrawCounts(camera));
            }
            return counts.ToArray();
        }

        [SerializeField, Min(0)] private float _shadowDistance = 1000;
        /// <summary>Maximum renderer shadow range before a source policy applies.</summary>
        public float ShadowDistanceLimit => _shadowDistance;
        [SerializeField, Range(0, 7)] private int _minimumShadowLod;
        private readonly Dictionary<UnityEngine.Object, string> _blocks = new();
        private readonly List<Behaviour> _sourceComponents = new();
        private bool _externalOwner;
        private readonly HashSet<Vector2Int> _dirtyPatches = new();
        private UnityEngine.Terrain _terrain;
        private TerrainData _data;
        private Population _population;
        private TerrainVegetationStream _stream;
        [SerializeField] private bool _streaming;
        [SerializeField] private Transform[] _observers = Array.Empty<Transform>();
        [SerializeField, Min(0)] private float _prefetchMargin = 32;
        [SerializeField, Min(1)] private int _cellsPerUpdate = 4;
        [SerializeField, Min(1)] private int _residentCellLimit = 256;
        /// <summary>Currently resident native tree cells and detail patches.</summary>
        public int ResidentCells => _stream?.ResidentCells ?? 0;
        /// <summary>Cells loaded in the most recent streaming update.</summary>
        public int LastCellLoads => _stream?.Loads ?? 0;
        /// <summary>Cells evicted in the most recent streaming update.</summary>
        public int LastCellEvictions => _stream?.Evictions ?? 0;
        /// <summary>Cells that the observers required at the most recent streaming update.</summary>
        public int LastRequiredCells => _stream?.RequiredCells ?? 0;
        /// <summary>Required cells that were not complete at the most recent streaming update.</summary>
        public int LastMissingRequiredCells => _stream?.MissingRequiredCells ?? 0;
        /// <summary>Realtime seconds of the most recent streaming update. Compare with the current time to find stale counts.</summary>
        public double LastStreamingUpdateTime => _stream?.UpdatedAt ?? double.NegativeInfinity;
        /// <summary>First observer position at the most recent streaming update. Coverage counts apply to this position.</summary>
        public Vector3 LastStreamingObserverPosition => _stream?.ObserverPosition ?? new Vector3(float.PositiveInfinity, 0f, 0f);
        private bool _nativeState;
        private bool _ownsNativeState;
        private bool _fullRefresh;
        private bool _heightPending;
        private string _qualityDiagnostic = string.Empty;
        private bool _surfaceDirty;
        private bool _treesDirty;
        private Vector3 _position;
        private float _density;
        private int _treeCount;
        private UnityEngine.Object[] _treeSources = Array.Empty<UnityEngine.Object>();
        private UnityEngine.Object[] _detailSources = Array.Empty<UnityEngine.Object>();

        /// <summary>Reason for native fallback, or an empty string while Looga owns rendering.</summary>
        public string Diagnostic { get; private set; } = string.Empty;
        /// <summary>Current complete-population handoff for this TerrainData source.</summary>
        public InstanceSourceAdapterStatus SourceStatus
        {
            get
            {
                int requested = _terrain && _terrain.terrainData && _terrain.isActiveAndEnabled ? 1 : 0;
                int owned = requested > 0 && isActiveAndEnabled && IsRendering && !_terrain.drawTreesAndFoliage ? 1 : 0;
                int native = requested > owned && _terrain && _terrain.drawTreesAndFoliage ? 1 : 0;
                bool waiting = requested > owned + native || _heightPending || (_streaming && _stream != null && !_stream.IsReady);
                return new InstanceSourceAdapterStatus(requested, owned, native, waiting, Diagnostic);
            }
        }

        /// <summary>Count resident placements for one terrain source.</summary>
        public int GetResidentCount(VegetationKind kind, UnityEngine.Object source)
        {
            if (!source) return 0;
            int result = 0;
            if (kind == VegetationKind.Tree)
            {
                for (int i = 0; i < _treeSources.Length; i++)
                {
                    if (_treeSources[i] != source) continue;
                    if (_stream != null) result += _stream.GetResidentCount(kind, i);
                    else if (_population != null && i < _population.TreeTypes.Length)
                        result += _population.Trees.GetResidentCount(_population.TreeTypes[i]);
                }
            }
            else if (kind == VegetationKind.Detail)
            {
                for (int i = 0; i < _detailSources.Length; i++)
                {
                    if (_detailSources[i] != source) continue;
                    if (_stream != null) result += _stream.GetResidentCount(kind, i);
                    else if (_population != null && i < _population.DetailTypes.Length)
                        result += _population.Details.GetResidentCount(_population.DetailTypes[i]);
                }
            }
            else throw new ArgumentOutOfRangeException(nameof(kind));
            return result;
        }

        /// <summary>Read unique visible placements for one source. This stalls the GPU.</summary>
        public int ReadCameraVisibleCount(Camera camera, VegetationKind kind, UnityEngine.Object source)
        {
            if (!source) return 0;
            int result = 0;
            if (kind == VegetationKind.Tree)
            {
                for (int i = 0; i < _treeSources.Length; i++)
                {
                    if (_treeSources[i] != source) continue;
                    if (_stream != null) result += _stream.ReadCameraVisibleCount(camera, kind, i);
                    else if (_population != null && i < _population.TreeTypes.Length)
                        result += _population.Trees.ReadCameraVisibleCount(camera, _population.TreeTypes[i]);
                }
            }
            else if (kind == VegetationKind.Detail)
            {
                for (int i = 0; i < _detailSources.Length; i++)
                {
                    if (_detailSources[i] != source) continue;
                    if (_stream != null) result += _stream.ReadCameraVisibleCount(camera, kind, i);
                    else if (_population != null && i < _population.DetailTypes.Length)
                        result += _population.Details.ReadCameraVisibleCount(camera, _population.DetailTypes[i]);
                }
            }
            else throw new ArgumentOutOfRangeException(nameof(kind));
            return result;
        }
        /// <summary>True after the complete population is ready and native rendering is disabled.</summary>
        public bool IsRendering => _population != null || (_stream?.IsRendering ?? false);
        /// <summary>Detail storage for each prototype: resident, high-water mark, capacity, views and size.</summary>
        public string DetailStorageSummary => _stream?.DescribeDetailStorage() ?? string.Empty;
        /// <summary>True when the active stream generates detail placements on the GPU.</summary>
        public bool GpuResidentDetailsActive => _stream?.GpuDetails ?? false;
        /// <summary>Number of resident trees and mesh details.</summary>
        public int InstanceCount => _stream?.ResidentCount ?? _population?.Count ?? 0;
        /// <summary>Number of detail patches read during the last successful refresh.</summary>
        public int LastPatchReads { get; private set; }
        /// <summary>Owned source and view buffer capacity.</summary>
        public int LastPreparedRecords => _stream?.PreparedRecords ?? 0;
        /// <summary>Source bytes uploaded by the last streaming update.</summary>
        public long LastUploadedBytes => _stream?.UploadedBytes ?? 0;
        /// <summary>Duration of native placement API calls that cannot be interrupted.</summary>
        public double LastNativeReadMilliseconds => _stream?.NativeReadMilliseconds ?? 0;

        /// <summary>CPU time of the latest streaming update, including uploads and spatial indexing.</summary>
        public double LastStreamingMilliseconds => _stream?.CpuMilliseconds ?? 0;
        /// <summary>CPU time of the latest renderer flush.</summary>
        public double LastFlushMilliseconds => _stream?.FlushMilliseconds ?? 0;
        /// <summary>CPU time used to order spatial groups during the latest flush.</summary>
        public double LastSpatialOrderMilliseconds => _stream?.SpatialOrderMilliseconds ?? 0;
        /// <summary>CPU time used to rebuild world cells during the latest flush.</summary>
        public double LastWorldCellsMilliseconds => _stream?.WorldCellsMilliseconds ?? 0;
        private IVegetationPageSource _pageSource;
        [SerializeField] private bool _generatedDetails;
        [SerializeField] private DetailGenerationBackend _detailGenerationBackend = DetailGenerationBackend.Compute;
        [SerializeField, Min(1)] private int _generatedDetailLimit = 262144;
        private GeneratedVegetationPageSource _generatedPageSource;
        private bool _streamGpuDetails;
        [SerializeField] private VegetationStreamingBudget _streamingBudget = VegetationStreamingBudget.Default;
        /// <summary>Current source and view GPU storage.</summary>
        public long GpuBytes => (_population?.Trees.GpuBytes ?? 0) + (_population?.Details.GpuBytes ?? 0) + (_stream?.GpuBytes ?? 0);
        /// <summary>Reject larger populations before taking rendering ownership.</summary>
        public int InstanceLimit
        {
            get => _instanceLimit;
            set
            {
                if (value < 1)
                {
                    throw new ArgumentOutOfRangeException(nameof(value));
                }
                _instanceLimit = value;
                InvalidateAll();
            }
        }

        /// <summary>Use deterministic generated detail pages while streaming. TerrainData remains the authoring source.</summary>
        public void ConfigureGeneratedDetails(bool enabled,
            DetailGenerationBackend backend = DetailGenerationBackend.Compute, int maximumRecordsPerPage = 262144)
        {
            if (maximumRecordsPerPage < 1) throw new ArgumentOutOfRangeException(nameof(maximumRecordsPerPage));
            Release();
            _generatedDetails = enabled;
            _detailGenerationBackend = backend;
            _generatedDetailLimit = maximumRecordsPerPage;
            _generatedPageSource = null;
            InvalidateAll();
        }

        /// <summary>Apply derived vegetation quality without changing TerrainData or authored quality values.</summary>
        public void ApplyAdaptiveQuality(AdaptiveQualityState state)
        {
            _adaptiveQualityScale = state.Scale;
            ApplyQualityPolicy();
        }

        #region Built-in
        private void OnEnable()
        {
            _terrain = GetComponent<UnityEngine.Terrain>();
            SubscribeToSettings();
            TerrainCallbacks.heightmapChanged += OnHeightChanged;
            TerrainCallbacks.textureChanged += OnTextureChanged;
            _fullRefresh = true;
        }

        private void OnDisable()
        {
#if UNITY_EDITOR
            _editorObservers = null;
#endif
            if (_subscribedSettings) _subscribedSettings.Changed -= OnVegetationSettingsChanged;
            _subscribedSettings = null;
            TerrainCallbacks.heightmapChanged -= OnHeightChanged;
            TerrainCallbacks.textureChanged -= OnTextureChanged;
            Release();
        }

        private void OnValidate()
        {
            SubscribeToSettings();
            InvalidateAll();
        }

        private void LateUpdate()
        {
            if (!Application.IsPlaying(gameObject))
            {
#if UNITY_EDITOR
                if (EditorPreviewActive) return;
#endif
                Release();
                _fullRefresh = true;
                return;
            }
            ProcessChanges();
        }
        #endregion

        #region Source updates
        /// <summary>Enable bounded cell residency around all assigned observers. Missing required cells use native rendering.</summary>
        public void ConfigureStreaming(Transform[] observers, int cellsPerUpdate = 4, float prefetchMargin = 32, int residentCells = 256)
        {
            if (observers == null || observers.Length == 0 || cellsPerUpdate < 1 || residentCells < 1 ||
                !float.IsFinite(prefetchMargin) || prefetchMargin < 0)
            {
                throw new ArgumentException("Streaming needs observers and finite positive budgets.");
            }
            Release();
            _observers = (Transform[])observers.Clone();
            _cellsPerUpdate = cellsPerUpdate;
            _prefetchMargin = prefetchMargin;
            _residentCellLimit = residentCells;
            _streaming = true;
            InvalidateAll();
        }

        /// <summary>Use baked placement pages. Pass null to restore native live placement extraction.</summary>
        /// <remarks>Rebake pages after source edits. The caller owns the page source and its asset-loading dependencies.</remarks>
        public void SetPageSource(IVegetationPageSource source)
        {
            Release();
            _pageSource = source;
            if (source != null) _generatedPageSource = null;
            InvalidateAll();
        }

        /// <summary>Set independent preparation, upload, time and GPU memory limits.</summary>
        public void SetStreamingBudget(VegetationStreamingBudget budget)
        {
            budget = budget.Normalized();
            budget.Validate();
            _streamingBudget = budget;
            InvalidateAll();
        }

        /// <summary>Return to the fully resident provider path on its next update.</summary>
        public void DisableStreaming()
        {
            Release();
            _streaming = false;
            InvalidateAll();
        }

        /// <summary>Assign an explicit shader integration and refresh captured prototypes.</summary>
        public void SetMaterialProfile(InstanceMaterialProfile profile)
        {
            Release();
            _materialProfile = profile;
            InvalidateAll();
        }

        /// <summary>Queue a complete refresh after prototype, tree or detail schema changes.</summary>
        public void InvalidateAll()
        {
            _fullRefresh = true;
        }

        /// <summary>Queue detail paint changes in native detail-map coordinates. All layers use the region.</summary>
        public void InvalidateDetails(RectInt region)
        {
            if (!_terrain || !_terrain.terrainData) return;
            TerrainData data = _terrain.terrainData;
            if (region.width <= 0 || region.height <= 0 || data.detailResolutionPerPatch <= 0) return;
            int step = data.detailResolutionPerPatch;
            int x0 = Mathf.Clamp(region.xMin, 0, data.detailWidth);
            int z0 = Mathf.Clamp(region.yMin, 0, data.detailHeight);
            int x1 = Mathf.Clamp(region.xMax, 0, data.detailWidth);
            int z1 = Mathf.Clamp(region.yMax, 0, data.detailHeight);
            if (x1 <= x0 || z1 <= z0) return;
            for (int z = z0 / step; z <= (z1 - 1) / step; z++)
            {
                for (int x = x0 / step; x <= (x1 - 1) / step; x++)
                {
                    _dirtyPatches.Add(new Vector2Int(x, z));
                }
            }
        }

        /// <summary>Queue tree changes. Native tree arrays do not expose stable edit callbacks.</summary>
        public void InvalidateTrees()
        {
            _treesDirty = true;
        }

        /// <summary>Suspend ownership during authoring or external rendering. Remove a block with a null reason.</summary>
        public void SetIntegrationBlock(UnityEngine.Object owner, string reason)
        {
            if (!owner)
            {
                throw new ArgumentNullException(nameof(owner));
            }
            if (string.IsNullOrEmpty(reason))
            {
                if (_blocks.Remove(owner))
                {
                    _heightPending = false;
                    InvalidateAll();
                }
            }
            else
            {
                _blocks[owner] = reason;
                Diagnostic = reason;
                Release();
                InvalidateAll();
            }
        }

        /// <summary>Apply queued changes before camera rendering. Failure restores native rendering for the complete population.</summary>
        public bool ProcessChanges()
        {
            if (!isActiveAndEnabled) return false;
#if UNITY_EDITOR
            if (!Application.IsPlaying(gameObject) && !EditorPreviewActive &&
                !UnityEditor.SceneManagement.EditorSceneManager.IsPreviewScene(gameObject.scene))
            {
                Release();
                Diagnostic = "Native terrain remains the authoring renderer. Enter Play mode to enable Looga vegetation.";
                return false;
            }
#endif
            if (!_terrain)
            {
                _terrain = GetComponent<UnityEngine.Terrain>();
            }
            try
            {
                string blocked = GetBlock();
                if (blocked != null)
                {
                    _fullRefresh = true;
                    Diagnostic = blocked;
                    Release(!_externalOwner);
                    return false;
                }
                if (_heightPending)
                {
                    Release();
                    _fullRefresh = true;
                    Diagnostic = "Waiting for native terrain height synchronization.";
                    return false;
                }
                TerrainData data = _terrain.terrainData;
                if (_data != data || _position != _terrain.transform.position || _density != _terrain.detailObjectDensity)
                {
                    _fullRefresh = true;
                }
                if (!_fullRefresh && PrototypeSourcesChanged(data))
                {
                    _fullRefresh = true;
                }
                if (_treeCount != data.treeInstanceCount)
                {
                    _treesDirty = true;
                }
                bool streaming = _streaming;
                Transform[] observers = _observers;
#if UNITY_EDITOR
                if (EditorPreviewActive)
                {
                    streaming = true;
                    observers = _editorObservers;
                }
#endif
                if (streaming)
                {
                    // GPU-resident details replace generated and native detail pages. An explicit page source wins.
                    bool gpuDetails = _vegetationSettings && _vegetationSettings.GpuResidentDetails && _pageSource == null;
                    if (_stream != null && _streamGpuDetails != gpuDetails)
                    {
                        _fullRefresh = true;
                    }
                    IVegetationPageSource activePageSource = gpuDetails ? null : GetPageSource();
                    if (_surfaceDirty && !_fullRefresh && !_treesDirty && activePageSource is GeneratedVegetationPageSource surfaceSource)
                    {
                        // Affected page requests are cancelled below; unrelated requests keep their snapshot.
                        surfaceSource.RefreshSurface(false);
                    }
                    _surfaceDirty = false;
                    if ((_stream?.IsRendering ?? false) && _terrain.drawTreesAndFoliage)
                    {
                        Release();
                        _fullRefresh = false;
                        Diagnostic = "Native rendering was enabled externally. Request a full refresh to resume.";
                        return false;
                    }
                    if (_population != null)
                    {
                        Release();
                    }
                    if (_fullRefresh || _treesDirty)
                    {
                        _stream?.Dispose();
                        _stream = null;
                        _data = data;
                        _position = _terrain.transform.position;
                        _density = _terrain.detailObjectDensity;
                        _treeCount = data.treeInstanceCount;
                        activePageSource = gpuDetails ? null : GetPageSource();
                        if (activePageSource is GeneratedVegetationPageSource generated)
                        {
                            generated.RefreshSurface();
                        }
                        _streamGpuDetails = gpuDetails;
                        _stream = new TerrainVegetationStream(_terrain, _materialProfile, _instanceLimit, _residentCellLimit,
                            activePageSource, _worldSourceId, gpuDetails);
                        _stream.ConfigureQuality(ResolveQuality, _visibilityMode);
                        CapturePrototypeSources(data);
                        _fullRefresh = false;
                    }
                    if (_stream == null) return false;
                    foreach (Vector2Int patch in _dirtyPatches)
                    {
                        _stream.Invalidate(patch);
                    }
                    if (!_ownsNativeState)
                    {
                        _nativeState = _terrain.drawTreesAndFoliage;
                        _ownsNativeState = true;
                        Owners[_terrain] = this;
                    }
                    VegetationStreamingBudget updateBudget = _streamingBudget;
#if UNITY_EDITOR
                    if (EditorPreviewActive)
                    {
                        // Bound Scene view work by CPU time only. The preview gives nearer terrains this time first.
                        updateBudget.CpuMilliseconds = Math.Min(updateBudget.CpuMilliseconds, 3f);
                    }
#endif
                    bool ready = _stream.Update(observers, _prefetchMargin, _cellsPerUpdate,
                        _shadowDistance, _minimumShadowLod, updateBudget);
                    _terrain.drawTreesAndFoliage = _stream.IsRendering ? false : _nativeState;
                    _treesDirty = false;
                    _dirtyPatches.Clear();
                    LastPatchReads = _stream.PatchReads;
                    Diagnostic = ready ? _qualityDiagnostic : _stream.IsRendering ?
                        "Required vegetation cells are loading. Looga draws the loaded cells." :
                        "Required vegetation cells are loading. Native rendering remains active.";
                    return ready;
                }
                if (_stream != null)
                {
                    Release();
                }
                if (_population != null && _terrain.drawTreesAndFoliage)
                {
                    Diagnostic = "Native rendering was enabled externally. Disable this provider or request a full refresh.";
                    Release();
                    return false;
                }
                LastPatchReads = 0;
                if (_fullRefresh)
                {
                    _data = data;
                    _position = _terrain.transform.position;
                    _density = _terrain.detailObjectDensity;
                    _treeCount = data.treeInstanceCount;
                    Rebuild(data);
                    CapturePrototypeSources(data);
                }
                else if (_population != null)
                {
                    if (_treesDirty)
                    {
                        ReplaceTrees(_population, data);
                    }
                    foreach (Vector2Int patch in _dirtyPatches)
                    {
                        ReplacePatch(_population, data, patch);
                    }
                    _population.Trees.Flush();
                    _population.Details.Flush();
                }
                if (_population == null) return false;
                _population.Trees.MaxDistance = _terrain.treeDistance;
                _population.Details.MaxDistance = _terrain.detailObjectDistance;
                _population.Trees.ShadowDistance = _population.Details.ShadowDistance = _shadowDistance;
                _population.Trees.MinimumShadowLod = _population.Details.MinimumShadowLod = _minimumShadowLod;
                _treeCount = data.treeInstanceCount;
                _treesDirty = false;
                _dirtyPatches.Clear();
                Diagnostic = _qualityDiagnostic;
                return true;
            }
            catch (Exception exception)
            {
                Release();
                _fullRefresh = false;
                _treesDirty = false;
                _dirtyPatches.Clear();
                Diagnostic = exception.Message;
                return false;
            }
        }
        #endregion

        private string GetBlock()
        {
            _externalOwner = false;
            foreach (var block in _blocks)
            {
                if (block.Key) return block.Value;
            }
            if (!_terrain || !_terrain.terrainData || !_terrain.isActiveAndEnabled) return "The source terrain is unavailable.";
            if (_terrain.transform.rotation != Quaternion.identity || _terrain.transform.lossyScale != Vector3.one)
                return "Rotated or scaled native terrains require a separate placement profile.";
            if (Owners.TryGetValue(_terrain, out TerrainVegetationProvider owner) && owner != this)
                return "Another Looga provider owns this terrain.";
            _terrain.GetComponents(_sourceComponents);
            foreach (Behaviour component in _sourceComponents)
            {
                if (!component || !component.isActiveAndEnabled) continue;
                string type = component.GetType().FullName;
                if (type == "BRGInstancedRenderer.TerrainBRGRegisterer" || type == "MA.Flora.FloraTerrainProvider" ||
                    type.StartsWith("JBooth.FoliageRendering."))
                {
                    _externalOwner = true;
                    return "An active vendor vegetation component already uses this terrain: " + type;
                }
            }
            if (!_ownsNativeState && !_terrain.drawTreesAndFoliage) return "Native vegetation is already disabled by another owner.";
            return null;
        }

        private IVegetationPageSource GetPageSource()
        {
            if (_pageSource != null || !_generatedDetails) return _pageSource;
            if (_generatedPageSource == null || _generatedPageSource.Backend != _detailGenerationBackend)
            {
                _generatedPageSource = new GeneratedVegetationPageSource(_terrain,
                    _detailGenerationBackend, _generatedDetailLimit);
            }
            return _generatedPageSource;
        }

        internal static UnityEngine.Object DetailSource(DetailPrototype prototype)
        {
            return prototype.usePrototypeMesh ? prototype.prototype : prototype.prototypeTexture;
        }

        private InstanceQualitySettings ResolveQuality(VegetationKind kind, UnityEngine.Object source)
        {
            float scale = _adaptiveQualityScale;
#if UNITY_EDITOR
            if (EditorPreviewActive)
            {
                scale = 1;
            }
#endif
            return InstanceQualityScaler.Scale(GetEffectiveQuality(kind, source), scale);
        }

        private bool PrototypeSourcesChanged(TerrainData data)
        {
            TreePrototype[] trees = data.treePrototypes;
            DetailPrototype[] details = data.detailPrototypes;
            if (trees.Length != _treeSources.Length || details.Length != _detailSources.Length) return true;
            for (int i = 0; i < trees.Length; i++)
                if (trees[i].prefab != _treeSources[i]) return true;
            for (int i = 0; i < details.Length; i++)
                if (DetailSource(details[i]) != _detailSources[i]) return true;
            return false;
        }

        private void CapturePrototypeSources(TerrainData data)
        {
            TreePrototype[] trees = data.treePrototypes;
            DetailPrototype[] details = data.detailPrototypes;
            _treeSources = new UnityEngine.Object[trees.Length];
            _detailSources = new UnityEngine.Object[details.Length];
            for (int i = 0; i < trees.Length; i++) _treeSources[i] = trees[i].prefab;
            for (int i = 0; i < details.Length; i++) _detailSources[i] = DetailSource(details[i]);
        }

        private void OnVegetationSettingsChanged()
        {
            try
            {
                ApplyQualityPolicy();
                _qualityDiagnostic = string.Empty;
                if (Diagnostic.StartsWith("Vegetation settings were not applied:", StringComparison.Ordinal))
                    Diagnostic = string.Empty;
            }
            catch (Exception exception)
            {
                _qualityDiagnostic = "Vegetation settings were not applied: " + exception.Message;
                Diagnostic = _qualityDiagnostic;
            }
        }

        private void SubscribeToSettings()
        {
            if (_subscribedSettings == _vegetationSettings) return;
            if (_subscribedSettings) _subscribedSettings.Changed -= OnVegetationSettingsChanged;
            _subscribedSettings = null;
            if (!isActiveAndEnabled || !_vegetationSettings) return;
            _vegetationSettings.Changed += OnVegetationSettingsChanged;
            _subscribedSettings = _vegetationSettings;
        }

        private void ApplyQualityPolicy()
        {
            ValidateQualityPolicy();
            if (_stream != null) _stream.ConfigureQuality(ResolveQuality, _visibilityMode);
            if (_population == null) return;
            _population.Trees.VisibilityMode = _population.Details.VisibilityMode = _visibilityMode;
            for (int i = 0; i < Mathf.Min(_treeSources.Length, _population.TreeTypes.Length); i++)
                _population.Trees.SetQuality(_population.TreeTypes[i], ResolveQuality(VegetationKind.Tree, _treeSources[i]));
            for (int i = 0; i < Mathf.Min(_detailSources.Length, _population.DetailTypes.Length); i++)
                _population.Details.SetQuality(_population.DetailTypes[i], ResolveQuality(VegetationKind.Detail, _detailSources[i]));
        }

        private void ValidateQualityPolicy()
        {
            if (!_terrain || !_terrain.terrainData) return;
            TerrainData data = _terrain.terrainData;
            UnityEngine.Object[] trees = _population != null ? _treeSources : Array.ConvertAll(data.treePrototypes,
                prototype => (UnityEngine.Object)prototype.prefab);
            UnityEngine.Object[] details = _population != null ? _detailSources : Array.ConvertAll(data.detailPrototypes,
                DetailSource);
            for (int i = 0; i < trees.Length; i++)
            {
                GameObject source = trees[i] as GameObject;
                ResolveQuality(VegetationKind.Tree, source).Validate(source &&
                    source.GetComponentsInChildren<Collider>(true).Length > 0);
            }
            for (int i = 0; i < details.Length; i++)
            {
                UnityEngine.Object source = details[i];
                GameObject prefab = source as GameObject;
                ResolveQuality(VegetationKind.Detail, source).Validate(prefab &&
                    prefab.GetComponentsInChildren<Collider>(true).Length > 0);
            }
        }

        private void Rebuild(TerrainData data)
        {
            var next = new Population(_terrain.gameObject.layer, _worldSourceId);
            try
            {
                TreePrototype[] trees = data.treePrototypes;
                DetailPrototype[] details = data.detailPrototypes;
                next.TreeTypes = new int[trees.Length];
                next.DetailTypes = new int[details.Length];
                for (int i = 0; i < trees.Length; i++)
                {
                    if (trees[i].bendFactor != 0)
                    {
                        throw new NotSupportedException("Tree bending needs a qualified wind shader. Native vegetation remains enabled.");
                    }
                    next.TreeTypes[i] = next.Trees.Register(InstancePrototype.FromPrefab(trees[i].prefab, _materialProfile).WithLodCenter(Vector3.zero));
                }
                for (int i = 0; i < details.Length; i++)
                {
                    DetailPrototype detail = details[i];
                    next.DetailTypes[i] = next.Details.Register(next.DetailResources.Capture(detail, _materialProfile));
                }
                next.Trees.VisibilityMode = next.Details.VisibilityMode = _visibilityMode;
                for (int i = 0; i < next.TreeTypes.Length; i++)
                    next.Trees.SetQuality(next.TreeTypes[i], ResolveQuality(VegetationKind.Tree, trees[i].prefab));
                for (int i = 0; i < next.DetailTypes.Length; i++)
                    next.Details.SetQuality(next.DetailTypes[i], ResolveQuality(VegetationKind.Detail, DetailSource(details[i])));
                ReplaceTrees(next, data);
                if (details.Length > 0)
                {
                    int patches = data.detailPatchCount;
                    for (int z = 0; z < patches; z++)
                    {
                        for (int x = 0; x < patches; x++)
                        {
                            ReplacePatch(next, data, new Vector2Int(x, z));
                        }
                    }
                }
                next.Trees.Flush();
                next.Details.Flush();
                if (!_ownsNativeState)
                {
                    _nativeState = _terrain.drawTreesAndFoliage;
                    _ownsNativeState = true;
                }
                _population?.Dispose();
                _population = next;
                next = null;
                Owners[_terrain] = this;
                _terrain.drawTreesAndFoliage = false;
                _data = data;
                _position = _terrain.transform.position;
                _density = _terrain.detailObjectDensity;
                _fullRefresh = false;
            }
            finally
            {
                next?.Dispose();
            }
        }

        private void ReplaceTrees(Population population, TerrainData data)
        {
            TreeInstance[] source = data.treeInstances;
            CheckLimit(population.Count - population.TreeHandles.Count + source.Length);
            var matrices = new Matrix4x4[source.Length];
            for (int i = 0; i < source.Length; i++)
            {
                TreeInstance tree = source[i];
                if (tree.prototypeIndex < 0 || tree.prototypeIndex >= population.TreeTypes.Length)
                {
                    throw new NotSupportedException("Tree prototype indices and color channels require a supported profile. Native vegetation remains enabled.");
                }
                matrices[i] = Matrix4x4.TRS(_terrain.transform.position + Vector3.Scale(tree.position, data.size),
                    Quaternion.Euler(0, tree.rotation * Mathf.Rad2Deg, 0), new Vector3(tree.widthScale, tree.heightScale, tree.widthScale));
            }
            foreach (InstanceHandle handle in population.TreeHandles)
            {
                population.Trees.Remove(handle);
            }
            population.TreeHandles.Clear();
            for (int i = 0; i < source.Length; i++)
            {
                population.TreeHandles.Add(population.Trees.Add(population.TreeTypes[source[i].prototypeIndex], matrices[i], new InstanceAppearance(source[i].color, source[i].lightmapColor, Vector4.zero)));
            }
        }

        private void ReplacePatch(Population population, TerrainData data, Vector2Int patch)
        {
            if (population.DetailTypes.Length == 0) return;
            if (patch.x < 0 || patch.y < 0 || patch.x >= data.detailPatchCount || patch.y >= data.detailPatchCount) return;
            var next = new List<(int type, Matrix4x4 matrix, InstanceAppearance appearance)>();
            var detailSources = data.detailPrototypes;
            population.Patches.TryGetValue(patch, out List<InstanceHandle> old);
            Vector3 size = data.size;
            Vector3 origin = _terrain.transform.position;
            for (int layer = 0; layer < population.DetailTypes.Length; layer++)
            {
                DetailInstanceTransform[] transforms = data.ComputeDetailInstanceTransforms(patch.x, patch.y, layer,
                    _terrain.detailObjectDensity, out _);
                CheckLimit(population.Count - (old?.Count ?? 0) + next.Count + transforms.Length);
                foreach (DetailInstanceTransform item in transforms)
                {
                    next.Add((population.DetailTypes[layer], DetailPrototypeResources.Transform(item, detailSources[layer], data, size, origin),
                        DetailPrototypeResources.Appearance(new Vector3(item.posX, item.posY, item.posZ), detailSources[layer])));
                }
            }
            if (old != null)
            {
                foreach (InstanceHandle handle in old)
                {
                    population.Details.Remove(handle);
                }
                old.Clear();
            }
            else
            {
                old = new List<InstanceHandle>(next.Count);
                population.Patches.Add(patch, old);
            }
            foreach (var item in next)
            {
                old.Add(population.Details.Add(item.type, item.matrix, item.appearance));
            }
            LastPatchReads++;
        }

        private void CheckLimit(int count)
        {
            if (count > _instanceLimit)
            {
                throw new NotSupportedException("The resident vegetation limit was exceeded. Native vegetation remains enabled.");
            }
        }

        private void OnHeightChanged(UnityEngine.Terrain terrain, RectInt region, bool synchronized)
        {
            if (terrain != _terrain) return;
            _heightPending = !synchronized;
            _surfaceDirty = true;
            if (synchronized && GetPageSource() is GeneratedVegetationPageSource generated)
            {
                generated.RefreshHeights(region, false);
                _surfaceDirty = false;
            }
            TerrainData data = terrain.terrainData;
            float scale = data.detailResolution / (float)(data.heightmapResolution - 1);
            InvalidateDetails(new RectInt(Mathf.FloorToInt((region.xMin - 1) * scale), Mathf.FloorToInt((region.yMin - 1) * scale),
                Mathf.CeilToInt((region.width + 2) * scale) + 1, Mathf.CeilToInt((region.height + 2) * scale) + 1));
            _treesDirty = true;
        }

        private void OnTextureChanged(UnityEngine.Terrain terrain, string textureName, RectInt region, bool synchronized)
        {
            if (terrain != _terrain || textureName != TerrainData.HolesTextureName) return;
            _heightPending = !synchronized;
            _surfaceDirty = true;
            if (synchronized && GetPageSource() is GeneratedVegetationPageSource generated)
            {
                generated.RefreshHoles(region, false);
                _surfaceDirty = false;
            }
            float scale = terrain.terrainData.detailResolution / (float)terrain.terrainData.holesResolution;
            InvalidateDetails(new RectInt(Mathf.FloorToInt(region.xMin * scale), Mathf.FloorToInt(region.yMin * scale),
                Mathf.CeilToInt(region.width * scale) + 1, Mathf.CeilToInt(region.height * scale) + 1));
        }

        private void Release(bool restoreNative = true)
        {
            _population?.Dispose();
            _population = null;
            _stream?.Dispose();
            _stream = null;
            if (_terrain && _ownsNativeState && restoreNative)
            {
                _terrain.drawTreesAndFoliage = _nativeState;
            }
            if (_terrain && Owners.TryGetValue(_terrain, out TerrainVegetationProvider owner) && owner == this)
            {
                Owners.Remove(_terrain);
            }
            _ownsNativeState = false;
        }

        private sealed class Population : IDisposable
        {
            internal readonly InstanceRenderer Trees;
            internal readonly InstanceRenderer Details;
            internal readonly DetailPrototypeResources DetailResources = new();
            internal readonly List<InstanceHandle> TreeHandles = new();
            internal readonly Dictionary<Vector2Int, List<InstanceHandle>> Patches = new();
            internal int[] TreeTypes;
            internal int[] DetailTypes;
            internal int Count => Trees.InstanceCount + Details.InstanceCount;

            internal Population(int layer, string worldSourceId)
            {
                Trees = new InstanceRenderer(layer: layer, worldSourceId: worldSourceId + ":trees", worldContentKind: InstanceWorldContentKind.Tree);
                Details = new InstanceRenderer(layer: layer, worldSourceId: worldSourceId + ":details", worldContentKind: InstanceWorldContentKind.Detail);
            }

            public void Dispose()
            {
                Trees.Dispose();
                Details.Dispose();
                DetailResources.Dispose();
            }
        }
    }
}
