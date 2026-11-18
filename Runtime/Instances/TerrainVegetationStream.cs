using LoogaSoft.Instancing;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace LoogaSoft.Terrain.Instances
{
    // Share prototype storage across cells. Release inactive slots when cells leave the resident region.
    internal sealed class TerrainVegetationStream : IDisposable
    {
        private sealed class Cell
        {
            internal Vector2Int Patch;
            internal Bounds Bounds;
            internal List<TreeInstance> Trees;
            internal readonly List<InstanceHandle> Handles = new();
            // GPU-resident detail slots, one range for each nonempty layer.
            internal readonly List<InstanceRange> Ranges = new();
            internal bool Complete;
            internal int Cursor;
            internal int Layer;
            internal DetailInstanceTransform[] DetailTransforms;
            internal System.Threading.CancellationTokenSource Cancellation;
            internal System.Threading.Tasks.Task<VegetationPageLoadResult> PageTask;
            internal VegetationPageRecord[] Page;
            internal bool Reading;
            internal float Distance;
            internal bool Required;
            // World height range of a detail patch. An eviction clears it, so a patch that a height change evicted
            // measures it again.
            internal bool HasHeights;
            internal float MinY;
            internal float MaxY;
        }


        private readonly struct ObserverState
        {
            internal readonly Vector3 Position;
            internal readonly float InterestRadius;
            internal readonly Vector3 PredictedPosition;
            internal readonly bool HasPrediction;

            internal ObserverState(Vector3 position, float interestRadius,
                Vector3 predictedPosition, bool hasPrediction)
            {
                Position = position;
                InterestRadius = interestRadius;
                PredictedPosition = predictedPosition;
                HasPrediction = hasPrediction;
            }
        }

        private readonly UnityEngine.Terrain _terrain;
        private readonly IVegetationPageSource _pageSource;
        private bool _trimPending;
        // True after a GPU cell did not fit the GPU reservation. No GPU cell loads until an eviction frees space or
        // the budget changes, so the same cell does not load and unload in each update.
        private bool _gpuReservationFull;
        private long _gpuReservationFullBudget;
        private int _pageRequests;
        private int _pendingReads;
        private readonly TerrainData _data;
        private readonly InstancePrototype[] _trees;
        private readonly InstancePrototype[] _details;
        private readonly UnityEngine.Object[] _treeSources;
        private readonly DetailPrototype[] _detailSources;
        private readonly DetailPrototypeResources _detailResources = new();
        private readonly List<Cell> _cells = new();
        private readonly List<Cell> _pending = new();
        // A cached comparer. Sorting with a method group allocates a delegate and a comparer on each call.
        private static readonly IComparer<Cell> CellOrder = Comparer<Cell>.Create(CompareCells);
        private readonly Dictionary<Vector2Int, Cell> _patches = new();
        private readonly int _instanceLimit;
        private readonly InstanceRenderer _treeRenderer;
        private readonly InstanceRenderer _detailRenderer;
        private readonly int[] _treeTypes;
        private readonly int[] _detailTypes;
        private float _treeViewRange;
        private float _detailViewRange;
        private readonly Dictionary<Vector2Int, Cell> _treeCells = new();
        private readonly HashSet<Cell> _interested = new();
        private readonly List<ObserverState> _observerStates = new();
        private readonly HashSet<Cell> _active = new();
        private readonly List<Cell> _evict = new();
        private readonly System.Diagnostics.Stopwatch _watch = new();
        // GPU work limit for one update. The CPU cost of a GPU page does not depend on its instance count.
        private const int GpuPlacementsPerUpdate = 1 << 20;
        private readonly GpuDetailGenerator _generator;
        private readonly List<InstanceRangeWrite> _rangeWrites = new();
        private bool _hasRendered;

        /// <summary>Detail storage summary for diagnostics.</summary>
        internal string DescribeDetailStorage() => _detailRenderer.DescribeStorage();

        /// <summary>True when detail cells use GPU-resident placement.</summary>
        internal bool GpuDetails => _generator != null;

        /// <summary>True while Looga draws this terrain. GPU-resident details keep drawing while cells load.</summary>
        internal bool IsRendering { get; private set; }
        internal int PreparedRecords { get; private set; }
        internal long UploadedBytes { get; private set; }
        internal double CpuMilliseconds => _watch.Elapsed.TotalMilliseconds;
        internal double NativeReadMilliseconds { get; private set; }
        internal double FlushMilliseconds => _treeRenderer.LastFlushBudgetMilliseconds + _detailRenderer.LastFlushBudgetMilliseconds;
        internal double SpatialOrderMilliseconds => _treeRenderer.LastSpatialOrderMilliseconds + _detailRenderer.LastSpatialOrderMilliseconds;
        internal double WorldCellsMilliseconds => _treeRenderer.LastWorldCellsMilliseconds + _detailRenderer.LastWorldCellsMilliseconds;

        private readonly int _cellLimit;
        private readonly float _treePadding;
        private readonly float _detailPadding;
        private readonly bool _hasLodTrees;
        private readonly bool _hasLegacyTrees;
        private readonly float _treeLodRatio;
        internal bool IsReady { get; private set; }
        internal int ResidentCount { get; private set; }
        internal int ResidentCells { get; private set; }
        internal int PatchReads { get; private set; }
        internal int Loads { get; private set; }
        internal int Evictions { get; private set; }
        internal int DiskReadBytes { get; private set; }
        internal int DecompressedBytes { get; private set; }
        internal int RequiredCells { get; private set; }
        internal int MissingRequiredCells { get; private set; }
        /// <summary>
        /// True when the last update left a required cell missing because of the instance limit, the cell limit or the
        /// GPU reservation.
        /// </summary>
        internal bool BudgetLimited { get; private set; }
        internal double UpdatedAt { get; private set; } = double.NegativeInfinity;
        internal Vector3 ObserverPosition { get; private set; }
        internal long GpuBytes => (_treeRenderer?.GpuBytes ?? 0) + (_detailRenderer?.GpuBytes ?? 0);
        /// <summary>The GPU bytes that the budget check uses: source storage and eight view buffers.</summary>
        internal long ProjectedGpuBytes =>
            (_treeRenderer?.ProjectedGpuBytes() ?? 0) + (_detailRenderer?.ProjectedGpuBytes() ?? 0);

        internal long TotalUploadedBytes => (_treeRenderer?.UploadedBytes ?? 0) + (_detailRenderer?.UploadedBytes ?? 0);

        internal uint[] ReadCameraDrawCounts(Camera camera)
        {
            var counts = new List<uint>();
            if (_treeRenderer != null) counts.AddRange(_treeRenderer.ReadCameraDrawCounts(camera));
            if (_detailRenderer != null) counts.AddRange(_detailRenderer.ReadCameraDrawCounts(camera));
            return counts.ToArray();
        }

        internal void ConfigureQuality(Func<VegetationKind, UnityEngine.Object, InstanceQualitySettings> resolve,
            InstanceVisibilityMode visibility)
        {
            var treeQuality = new InstanceQualitySettings[_treeTypes.Length];
            var detailQuality = new InstanceQualitySettings[_detailTypes.Length];
            float treeRange = 0;
            float detailRange = 0;
            for (int i = 0; i < _treeTypes.Length; i++)
            {
                InstanceQualitySettings quality = resolve(VegetationKind.Tree, _treeSources[i]);
                quality.Validate(_trees[i].HasColliders);
                treeQuality[i] = quality;
                treeRange = Mathf.Max(treeRange, quality.ViewDistance > 0 ? quality.ViewDistance : _terrain.treeDistance);
            }
            for (int i = 0; i < _detailTypes.Length; i++)
            {
                InstanceQualitySettings quality = resolve(VegetationKind.Detail,
                    TerrainVegetationProvider.DetailSource(_detailSources[i]));
                quality.Validate(_details[i].HasColliders);
                detailQuality[i] = quality;
                detailRange = Mathf.Max(detailRange,
                    quality.ViewDistance > 0 ? quality.ViewDistance : _terrain.detailObjectDistance);
            }
            _treeRenderer.VisibilityMode = _detailRenderer.VisibilityMode = visibility;
            for (int i = 0; i < treeQuality.Length; i++) _treeRenderer.SetQuality(_treeTypes[i], treeQuality[i]);
            for (int i = 0; i < detailQuality.Length; i++) _detailRenderer.SetQuality(_detailTypes[i], detailQuality[i]);
            _treeViewRange = treeRange;
            _detailViewRange = detailRange;
        }

        internal int GetResidentCount(VegetationKind kind, int index)
        {
            if (kind == VegetationKind.Tree) return index < _treeTypes.Length ? _treeRenderer.GetResidentCount(_treeTypes[index]) : 0;
            return index < _detailTypes.Length ? _detailRenderer.GetResidentCount(_detailTypes[index]) : 0;
        }

        internal int ReadCameraVisibleCount(Camera camera, VegetationKind kind, int index)
        {
            if (kind == VegetationKind.Tree)
                return index < _treeTypes.Length ? _treeRenderer.ReadCameraVisibleCount(camera, _treeTypes[index]) : 0;
            return index < _detailTypes.Length ? _detailRenderer.ReadCameraVisibleCount(camera, _detailTypes[index]) : 0;
        }

        internal TerrainVegetationStream(UnityEngine.Terrain terrain, InstanceMaterialProfile profile, int instanceLimit, int cellLimit, IVegetationPageSource pageSource = null, string worldSourceId = null, bool gpuDetails = false)
        {
            if (gpuDetails && pageSource != null)
            {
                throw new NotSupportedException("GPU-resident details cannot use a page source.");
            }
            _terrain = terrain;
            _pageSource = pageSource;
            _data = terrain.terrainData;
            _instanceLimit = instanceLimit;
            _cellLimit = cellLimit;
            try
            {
                var treeTypes = _data.treePrototypes;
                var detailTypes = _data.detailPrototypes;
                _detailSources = detailTypes;
                _trees = new InstancePrototype[treeTypes.Length];
                _details = new InstancePrototype[detailTypes.Length];
                _treeSources = new UnityEngine.Object[treeTypes.Length];
                for (int i = 0; i < _trees.Length; i++)
                {
                    _treeSources[i] = treeTypes[i].prefab;
                    if (treeTypes[i].bendFactor != 0)
                    {
                        throw new NotSupportedException("Native bendFactor requires a separate material profile.");
                    }
                    _trees[i] = InstancePrototype.FromPrefab(treeTypes[i].prefab, profile).WithLodCenter(Vector3.zero);
                }
                for (int i = 0; i < _details.Length; i++)
                {
                    DetailPrototype detail = detailTypes[i];
                    // Texture details need a color for each instance. GPU-resident storage has no appearance data.
                    if (gpuDetails && !detail.usePrototypeMesh)
                    {
                        throw new NotSupportedException("GPU-resident details support mesh detail prototypes only.");
                    }
                    _details[i] = _detailResources.Capture(detail, profile);
                    float scale = Mathf.Max(detail.maxWidth, detail.maxHeight);
                    _detailPadding = Mathf.Max(_detailPadding, (_details[i].Bounds.center.magnitude + _details[i].Bounds.extents.magnitude) * scale);
                }

                foreach (TreeInstance tree in _data.treeInstances)
                {
                    if (tree.prototypeIndex < 0 || tree.prototypeIndex >= _trees.Length)
                    {
                        throw new NotSupportedException("The tree references an invalid prototype index.");
                    }
                    Vector3 local = Vector3.Scale(tree.position, _data.size);
                    var key = new Vector2Int(Mathf.FloorToInt(local.x / 64), Mathf.FloorToInt(local.z / 64));
                    if (!_treeCells.TryGetValue(key, out Cell cell))
                    {
                        cell = new Cell { Patch = key, Trees = new(), Bounds = new Bounds(terrain.transform.position + new Vector3(key.x * 64 + 32, 0, key.y * 64 + 32), new Vector3(64, 0, 64)) };
                        _treeCells.Add(key, cell);
                        _cells.Add(cell);
                    }
                    cell.Trees.Add(tree);
                    var prototype = _trees[tree.prototypeIndex];
                    if (prototype.NativeLodDistance)
                    {
                        _hasLodTrees = true;
                        float threshold = prototype.Thresholds[prototype.Thresholds.Length - 1];
                        float ratio = threshold > 0 ? prototype.LodSize * Mathf.Max(tree.widthScale, tree.heightScale) / threshold : float.MaxValue;
                        _treeLodRatio = Mathf.Max(_treeLodRatio, ratio);
                    }
                    else
                    {
                        _hasLegacyTrees = true;
                    }
                    var bounds = prototype.Bounds;
                    _treePadding = Mathf.Max(_treePadding, (bounds.center.magnitude + bounds.extents.magnitude) * Mathf.Max(tree.widthScale, tree.heightScale));
                }
                if (_details.Length > 0)
                {
                    int patches = _data.detailPatchCount;
                    if ((long)patches * patches > 262144)
                    {
                        throw new NotSupportedException("The native detail patch table exceeds the streaming descriptor limit.");
                    }
                    Vector3 size = new Vector3(_data.size.x / patches, 0, _data.size.z / patches);
                    for (int z = 0; z < patches; z++)
                    {
                        for (int x = 0; x < patches; x++)
                        {
                            var key = new Vector2Int(x, z);
                            var cell = new Cell { Patch = key, Bounds = new Bounds(terrain.transform.position + Vector3.Scale(new Vector3(x + 0.5f, 0, z + 0.5f), size), size) };
                            _patches.Add(key, cell);
                            _cells.Add(cell);
                        }
                    }
                }
                _treeTypes = new int[_trees.Length];
                _detailTypes = new int[_details.Length];
                try
                {
                    _treeRenderer = new InstanceRenderer(layer: terrain.gameObject.layer, worldSourceId: worldSourceId + ":streamed-trees", worldContentKind: InstanceWorldContentKind.Tree, staticTransforms: true) { RenderingEnabled = false };
                    _detailRenderer = new InstanceRenderer(layer: terrain.gameObject.layer, worldSourceId: worldSourceId + ":streamed-details", worldContentKind: InstanceWorldContentKind.Detail, staticTransforms: true, gpuResident: gpuDetails) { RenderingEnabled = false };
                    for (int i = 0; i < _trees.Length; i++)
                    {
                        _treeTypes[i] = _treeRenderer.Register(_trees[i]);
                    }
                    for (int i = 0; i < _details.Length; i++)
                    {
                        _detailTypes[i] = _detailRenderer.Register(_details[i]);
                    }
                    if (gpuDetails && _details.Length > 0)
                    {
                        _generator = new GpuDetailGenerator(terrain);
                    }
                }
                catch
                {
                    _generator?.Dispose();
                    _treeRenderer?.Dispose();
                    _detailRenderer?.Dispose();
                    _detailResources.Dispose();
                    throw;
                }
            }
            catch
            {
                _detailResources.Dispose();
                throw;
            }
        }

        internal void Invalidate(Vector2Int patch)
        {
            if (_patches.TryGetValue(patch, out Cell cell))
            {
                Evict(cell);
            }
        }

        /// <summary>
        /// Reads the trees of the tree cells in terrain-local XZ areas again and loads those cells again. A height
        /// change moves the trees on the ground, and the cells keep a copy of their trees.
        /// </summary>
        internal void RefreshTrees(IReadOnlyList<Rect> areas)
        {
            var changed = new HashSet<Cell>();
            foreach (Rect area in areas)
            {
                for (int z = Mathf.FloorToInt(area.yMin / 64); z <= Mathf.FloorToInt(area.yMax / 64); z++)
                {
                    for (int x = Mathf.FloorToInt(area.xMin / 64); x <= Mathf.FloorToInt(area.xMax / 64); x++)
                    {
                        if (_treeCells.TryGetValue(new Vector2Int(x, z), out Cell cell))
                        {
                            changed.Add(cell);
                        }
                    }
                }
            }
            if (changed.Count == 0) return;

            foreach (Cell cell in changed)
            {
                Evict(cell);
                cell.Trees.Clear();
            }
            foreach (TreeInstance tree in _data.treeInstances)
            {
                Vector3 local = Vector3.Scale(tree.position, _data.size);
                var key = new Vector2Int(Mathf.FloorToInt(local.x / 64), Mathf.FloorToInt(local.z / 64));
                if (_treeCells.TryGetValue(key, out Cell cell) && changed.Contains(cell))
                {
                    cell.Trees.Add(tree);
                }
            }
        }

        internal bool Update(Transform[] observers, float margin, int workBudget, float shadowDistance, int minimumShadowLod, VegetationStreamingBudget budget)
        {
            budget = budget.Normalized();
            budget.Validate();
            _watch.Restart();
            PreparedRecords = 0;
            _pageRequests = 0;
            UploadedBytes = 0;
            NativeReadMilliseconds = 0;
            PatchReads = 0;
            Loads = 0;
            Evictions = 0;
            DiskReadBytes = 0;
            DecompressedBytes = 0;
            BudgetLimited = false;
            if (_gpuReservationFull && budget.ResidentGpuBytes != _gpuReservationFullBudget)
            {
                _gpuReservationFull = false;
            }
            int observersFound = 0;
            foreach (Transform observer in observers)
            {
                if (observer)
                {
                    observersFound++;
                }
            }
            if (observersFound == 0)
            {
                throw new InvalidOperationException("Streaming needs at least one explicit observer. Native rendering remains enabled.");
            }
            foreach (Transform observer in observers)
            {
                if (!observer) continue;
                ObserverPosition = observer.position;
                break;
            }
            float treeRange = _hasLegacyTrees ? _terrain.treeDistance : 0;
            if (_hasLodTrees)
            {
                foreach (Transform observer in observers)
                {
                    if (!observer)
                    {
                        continue;
                    }
                    // TryGetComponent makes no editor null-object garbage when the component is missing.
                    if (!observer.TryGetComponent(out Camera camera))
                    {
                        if (observer.TryGetComponent(out VegetationInterest _))
                        {
                            continue;
                        }
                        throw new InvalidOperationException("LOD tree streaming requires a Camera or VegetationInterest observer.");
                    }
                    float tangent = Mathf.Tan(camera.fieldOfView * Mathf.Deg2Rad * 0.5f);
                    float halfHeight = camera.orthographic ? camera.orthographicSize : camera.farClipPlane * tangent;
                    float farRadius = Mathf.Sqrt(camera.farClipPlane * camera.farClipPlane + halfHeight * halfHeight * (1 + camera.aspect * camera.aspect));
                    float lodRadius = camera.orthographic ? farRadius : _treeLodRatio * QualitySettings.lodBias / (2 * tangent);
                    treeRange = Mathf.Max(treeRange, Mathf.Min(farRadius, lodRadius));
                }
            }
            treeRange = Mathf.Max(treeRange, _treeViewRange);
            _observerStates.Clear();
            foreach (Transform observer in observers)
            {
                if (!observer)
                {
                    continue;
                }
                observer.TryGetComponent(out VegetationInterest interest);
                bool active = interest && interest.isActiveAndEnabled;
                float radius = active ? interest.Radius : -1f;
                bool predict = active && budget.PrefetchSeconds > 0f;
                Vector3 predicted = predict ? interest.PredictPosition(budget.PrefetchSeconds) : Vector3.zero;
                _observerStates.Add(new ObserverState(observer.position, radius, predicted, predict));
            }

            float detailRange = _detailViewRange;
            _pending.Clear();
            _interested.Clear();
            foreach (ObserverState observer in _observerStates)
            {
                Gather(_treeCells, observer.Position,
                    RequiredObserverRadius(observer.InterestRadius, treeRange) + _treePadding + margin * 2, 64, 64);
                if (_details.Length > 0)
                {
                    Gather(_patches, observer.Position,
                        RequiredObserverRadius(observer.InterestRadius, detailRange) + _detailPadding + margin * 2,
                        _data.size.x / _data.detailPatchCount, _data.size.z / _data.detailPatchCount);
                }
                if (observer.HasPrediction)
                {
                    Gather(_treeCells, observer.PredictedPosition,
                        RequiredObserverRadius(observer.InterestRadius, treeRange) + _treePadding + margin * 2, 64, 64);
                    if (_details.Length > 0)
                    {
                        Gather(_patches, observer.PredictedPosition,
                            RequiredObserverRadius(observer.InterestRadius, detailRange) + _detailPadding + margin * 2,
                            _data.size.x / _data.detailPatchCount, _data.size.z / _data.detailPatchCount);
                    }
                }
            }
            _evict.Clear();
            foreach (var cell in _active)
            {
                if (!_interested.Contains(cell))
                {
                    // The residency change limit can keep this cell for later updates. It is not required now, so
                    // EvictPrefetch can release it when a required cell needs the space.
                    cell.Required = false;
                    _evict.Add(cell);
                }
            }
            foreach (var cell in _evict)
            {
                if (Evictions >= budget.ResidencyChanges) break;
                Evict(cell);
            }
            foreach (Cell cell in _interested)
            {
                float distance = float.PositiveInfinity;
                float range = cell.Trees != null ? treeRange + _treePadding : detailRange + _detailPadding;
                if (cell.Trees == null)
                {
                    EnsureHeights(cell);
                }
                foreach (ObserverState observer in _observerStates)
                {
                    Vector3 delta = observer.Position - cell.Bounds.center;
                    float x = Mathf.Max(0, Mathf.Abs(delta.x) - cell.Bounds.extents.x);
                    float z = Mathf.Max(0, Mathf.Abs(delta.z) - cell.Bounds.extents.z);
                    // Details are culled by 3D camera distance. An observer high above a patch does not need it.
                    float y = cell.HasHeights ?
                        Mathf.Max(0, Mathf.Max(cell.MinY - observer.Position.y, observer.Position.y - cell.MaxY)) : 0;
                    float observerRange = observer.InterestRadius >= 0f ?
                        RequiredObserverRadius(observer.InterestRadius +
                            (cell.Trees != null ? _treePadding : _detailPadding), range) : range;
                    distance = Mathf.Min(distance, Mathf.Sqrt(x * x + y * y + z * z) - observerRange + range);
                }
                cell.Distance = distance;
                cell.Required = distance <= range;
                if (distance > range + margin * 2)
                {
                    Evict(cell);
                }
                else if (!cell.Complete && distance <= range + margin)
                {
                    _pending.Add(cell);
                }
            }
            _pending.Sort(CellOrder);
            _treeRenderer.RenderingEnabled = _detailRenderer.RenderingEnabled = false;
            foreach (Cell cell in _pending)
            {
                // GPU pages have no CPU work for each instance. The record and cell caps limit CPU staging only.
                bool gpuCell = _generator != null && cell.Trees == null;
                bool cpuLimit = !gpuCell && (Loads + _pageRequests >= workBudget || PreparedRecords >= budget.PlacementRecords);
                bool gpuLimit = gpuCell && _generator.PendingPlacements >= GpuPlacementsPerUpdate;
                if (cpuLimit || gpuLimit || Loads + Evictions >= budget.ResidencyChanges ||
                    _watch.Elapsed.TotalMilliseconds >= budget.CpuMilliseconds)
                {
                    break;
                }
                if (!_active.Contains(cell) && _active.Count >= _cellLimit)
                {
                    if (!cell.Required)
                    {
                        continue;
                    }
                    EvictPrefetch();
                    if (_active.Count >= _cellLimit)
                    {
                        // Over a budget, the missing cells stay empty. Loaded cells keep drawing.
                        BudgetLimited = true;
                        break;
                    }
                }
                if (gpuCell)
                {
                    if (_gpuReservationFull || !StageGpu(cell, budget))
                    {
                        BudgetLimited |= cell.Required;
                        break;
                    }
                }
                else
                {
                    _active.Add(cell);
                    Stage(cell, budget);
                }
            }
            SubmitGpuPages();
            // Eviction can expose unused trailing storage. Live handle indices remain unchanged.
            if (_trimPending)
            {
                _treeRenderer.TrimExcess();
                _detailRenderer.TrimExcess();
                _trimPending = false;
            }
            if (ProjectedGpuBytes > budget.ResidentGpuBytes)
            {
                EvictPrefetch();
                _treeRenderer.TrimExcess();
                _detailRenderer.TrimExcess();
                if (ProjectedGpuBytes > budget.ResidentGpuBytes)
                {
                    // The storage keeps its capacity until the trim finds it four times too large, so evictions here
                    // free no reservation. The loaded cells keep drawing, and no GPU cell loads until an eviction
                    // frees space. The allocated bytes below are the hard limit.
                    BudgetLimited = true;
                    _gpuReservationFull = true;
                    _gpuReservationFullBudget = budget.ResidentGpuBytes;
                }
            }
            long before = _treeRenderer.UploadedBytes;
            _treeRenderer.FlushBudget(budget.UploadBytes);
            UploadedBytes = _treeRenderer.UploadedBytes - before;
            long remaining = budget.UploadBytes - UploadedBytes;
            // GPU-resident flushes upload no source bytes. They apply capacity changes and slot clears.
            if (_generator != null)
            {
                _detailRenderer.FlushBudget(Math.Max(64, remaining));
            }
            else if (remaining >= 64)
            {
                before = _detailRenderer.UploadedBytes;
                _detailRenderer.FlushBudget(remaining);
                UploadedBytes += _detailRenderer.UploadedBytes - before;
            }
            if (GpuBytes > budget.ResidentGpuBytes)
            {
                throw new InvalidOperationException("Vegetation GPU storage exceeds the configured memory budget.");
            }
            RequiredCells = 0;
            MissingRequiredCells = 0;
            foreach (Cell cell in _interested)
            {
                if (!cell.Required) continue;
                RequiredCells++;
                if (!cell.Complete) MissingRequiredCells++;
            }
            IsReady = MissingRequiredCells == 0 && !_treeRenderer.HasPendingUploads && !_detailRenderer.HasPendingUploads;
            UpdatedAt = Time.realtimeSinceStartupAsDouble;
            // After the first complete update, GPU-resident details draw loaded cells while new cells load.
            // A return to native rendering would make Unity rebuild native details on the CPU.
            // A budget-limited stream cannot become ready. Its GPU-resident details draw the loaded cells, because
            // native details would draw every cell, also in every shadow level.
            _hasRendered |= IsReady || (_generator != null && BudgetLimited && ResidentCount > 0);
            IsRendering = IsReady || (_generator != null && _hasRendered);
            _treeRenderer.RenderingEnabled = _detailRenderer.RenderingEnabled = IsRendering;
            _treeRenderer.MaxDistance = _terrain.treeDistance;
            _detailRenderer.MaxDistance = _terrain.detailObjectDistance;
            _treeRenderer.ShadowDistance = _detailRenderer.ShadowDistance = shadowDistance;
            _treeRenderer.MinimumShadowLod = _detailRenderer.MinimumShadowLod = minimumShadowLod;
            _watch.Stop();
            return IsReady;
        }

        private void Gather(Dictionary<Vector2Int, Cell> grid, Vector3 position, float radius, float width, float depth)
        {
            Vector3 local = position - _terrain.transform.position;
            int maxGridX = Mathf.FloorToInt(_data.size.x / width);
            int maxGridZ = Mathf.FloorToInt(_data.size.z / depth);
            int minX = Mathf.Max(0, Mathf.FloorToInt((local.x - radius) / width));
            int maxX = Mathf.Min(maxGridX, Mathf.FloorToInt((local.x + radius) / width));
            int minZ = Mathf.Max(0, Mathf.FloorToInt((local.z - radius) / depth));
            int maxZ = Mathf.Min(maxGridZ, Mathf.FloorToInt((local.z + radius) / depth));
            for (int z = minZ; z <= maxZ; z++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    if (grid.TryGetValue(new Vector2Int(x, z), out var cell))
                    {
                        _interested.Add(cell);
                    }
                }
            }
        }

        // Reads the heightmap texels under a detail patch once, when an observer first gathers the patch.
        private void EnsureHeights(Cell cell)
        {
            if (cell.HasHeights) return;
            int last = _data.heightmapResolution - 1;
            float step = last / (float)_data.detailPatchCount;
            int x0 = Mathf.Clamp(Mathf.FloorToInt(cell.Patch.x * step), 0, last);
            int z0 = Mathf.Clamp(Mathf.FloorToInt(cell.Patch.y * step), 0, last);
            int x1 = Mathf.Clamp(Mathf.CeilToInt((cell.Patch.x + 1) * step), x0, last);
            int z1 = Mathf.Clamp(Mathf.CeilToInt((cell.Patch.y + 1) * step), z0, last);
            float[,] heights = _data.GetHeights(x0, z0, x1 - x0 + 1, z1 - z0 + 1);
            float min = float.PositiveInfinity;
            float max = float.NegativeInfinity;
            foreach (float height in heights)
            {
                min = Mathf.Min(min, height);
                max = Mathf.Max(max, height);
            }
            float origin = _terrain.transform.position.y;
            cell.MinY = origin + min * _data.size.y;
            cell.MaxY = origin + max * _data.size.y;
            cell.HasHeights = true;
        }

        private static int CompareCells(Cell a, Cell b)
        {
            int priority = b.Required.CompareTo(a.Required);
            return priority != 0 ? priority : a.Distance.CompareTo(b.Distance);
        }

        private static float RequiredObserverRadius(float interestRadius, float viewRange)
        {
            return Mathf.Max(interestRadius, viewRange);
        }

        private async System.Threading.Tasks.Task<VegetationPageLoadResult> LoadPage(Vector2Int patch, bool trees,
            int maximumDiskBytes, int maximumDecompressedBytes, System.Threading.CancellationToken cancellation)
        {
            if (_pageSource is IBudgetedVegetationPageSource budgeted)
                return await budgeted.LoadAsync(new VegetationPageRequest(patch, trees, maximumDiskBytes,
                    maximumDecompressedBytes), cancellation).ConfigureAwait(false);
            VegetationPageRecord[] records = await _pageSource.LoadAsync(patch, trees, cancellation).ConfigureAwait(false);
            int estimated = records == null ? 0 : records.Length * 116;
            if (estimated > maximumDecompressedBytes)
                throw new InvalidOperationException("The page source exceeded the estimated decompression budget.");
            return new VegetationPageLoadResult(records, 0, estimated);
        }

        private void Stage(Cell cell, VegetationStreamingBudget budget)
        {
            var renderer = cell.Trees != null ? _treeRenderer : _detailRenderer;
            // Native reads. The stream is rebuilt when the terrain moves or resizes.
            Vector3 origin = _terrain.transform.position;
            Vector3 size = _data.size;
            if (_pageSource != null && cell.Page == null)
            {
                if (cell.PageTask == null)
                {
                    if (_pendingReads >= budget.ConcurrentReads || DiskReadBytes >= budget.DiskReadBytes ||
                        DecompressedBytes >= budget.DecompressionBytes) return;
                    cell.Reading = true;
                    _pendingReads++;
                    cell.Cancellation = new System.Threading.CancellationTokenSource();
                    int remainingDisk = Mathf.Max(1, budget.DiskReadBytes - DiskReadBytes);
                    int remainingDecoded = Mathf.Max(1, budget.DecompressionBytes - DecompressedBytes);
                    cell.PageTask = LoadPage(cell.Patch, cell.Trees != null, remainingDisk, remainingDecoded, cell.Cancellation.Token);
                    if (cell.PageTask == null)
                    {
                        throw new InvalidOperationException("The page source returned no task.");
                    }
                    _pageRequests++;
                }
                if (!cell.PageTask.IsCompleted) return;
                cell.Reading = false;
                _pendingReads--;
                VegetationPageLoadResult loaded = cell.PageTask.GetAwaiter().GetResult();
                DiskReadBytes += loaded.DiskBytes;
                DecompressedBytes += loaded.DecompressedBytes;
                if (DiskReadBytes > budget.DiskReadBytes || DecompressedBytes > budget.DecompressionBytes)
                    throw new InvalidOperationException("Vegetation page work exceeded its I/O or decompression budget.");
                cell.Page = loaded.Records;
                if (cell.Page == null || cell.Page.Length > _instanceLimit)
                {
                    throw new InvalidOperationException("The page exceeds the resident placement limit.");
                }
            }
            while (PreparedRecords < budget.PlacementRecords && _watch.Elapsed.TotalMilliseconds < budget.CpuMilliseconds)
            {
                int type;
                Matrix4x4 matrix;
                InstanceAppearance appearance = InstanceAppearance.Default;
                if (_pageSource != null)
                {
                    if (cell.Cursor == cell.Page.Length)
                    {
                        Complete(cell);
                        return;
                    }
                    var record = cell.Page[cell.Cursor];
                    var types = cell.Trees != null ? _treeTypes : _detailTypes;
                    if (record.Prototype < 0 || record.Prototype >= types.Length)
                    {
                        throw new InvalidOperationException("The page references an unknown prototype.");
                    }
                    type = types[record.Prototype];
                    matrix = Matrix4x4.Translate(origin) * record.LocalTransform;
                    appearance = record.Appearance;
                    Vector3 pivot = matrix.GetColumn(3);
                    Vector3 delta = pivot - cell.Bounds.center;
                    if (Mathf.Abs(delta.x) > cell.Bounds.extents.x + 0.01f || Mathf.Abs(delta.z) > cell.Bounds.extents.z + 0.01f)
                    {
                        throw new InvalidOperationException("The page contains a placement outside its source cell.");
                    }
                    var prototype = cell.Trees != null ? _trees[record.Prototype] : _details[record.Prototype];
                    float scale = Mathf.Max(matrix.GetColumn(0).magnitude, Mathf.Max(matrix.GetColumn(1).magnitude, matrix.GetColumn(2).magnitude));
                    float extent = (prototype.Bounds.center.magnitude + prototype.Bounds.extents.magnitude) * scale;
                    float allowed = cell.Trees != null ? _treePadding : _detailPadding;
                    if (extent > allowed + 0.01f)
                    {
                        throw new InvalidOperationException("The page exceeds its source prototype displacement bounds.");
                    }
                }
                else if (cell.Trees != null)
                {
                    if (cell.Cursor == cell.Trees.Count)
                    {
                        Complete(cell);
                        return;
                    }
                    TreeInstance tree = cell.Trees[cell.Cursor];
                    type = _treeTypes[tree.prototypeIndex];
                    matrix = Matrix4x4.TRS(origin + Vector3.Scale(tree.position, size),
                        Quaternion.Euler(0, tree.rotation * Mathf.Rad2Deg, 0), new Vector3(tree.widthScale, tree.heightScale, tree.widthScale));
                    appearance = new InstanceAppearance(tree.color, tree.lightmapColor, Vector4.zero);
                }
                else
                {
                    if (cell.Layer == _details.Length)
                    {
                        Complete(cell);
                        return;
                    }
                    if (cell.DetailTransforms == null)
                    {
                        double before = _watch.Elapsed.TotalMilliseconds;
                        cell.DetailTransforms = ReplacedDetailLayers.IsReplaced(_terrain, cell.Layer)
                            ? Array.Empty<DetailInstanceTransform>()
                            : _data.ComputeDetailInstanceTransforms(cell.Patch.x, cell.Patch.y, cell.Layer,
                                _terrain.detailObjectDensity, out _);
                        NativeReadMilliseconds += _watch.Elapsed.TotalMilliseconds - before;
                        PatchReads++;
                    }
                    if (cell.Cursor == cell.DetailTransforms.Length)
                    {
                        cell.Layer++;
                        cell.Cursor = 0;
                        cell.DetailTransforms = null;
                        continue;
                    }
                    var item = cell.DetailTransforms[cell.Cursor];
                    type = _detailTypes[cell.Layer];
                    appearance = DetailPrototypeResources.Appearance(new Vector3(item.posX, item.posY, item.posZ), _detailSources[cell.Layer]);
                    matrix = DetailPrototypeResources.Transform(item, _detailSources[cell.Layer], _data, size, origin);
                }
                if (ResidentCount >= _instanceLimit)
                {
                    if (!cell.Required) return;
                    EvictPrefetch();
                    if (ResidentCount >= _instanceLimit)
                    {
                        // The cell keeps its staged part and continues when evictions free space.
                        BudgetLimited = true;
                        return;
                    }
                }
                cell.Handles.Add(renderer.Add(type, matrix, appearance));
                cell.Cursor++;
                PreparedRecords++;
                ResidentCount++;
            }
        }

        // Queue one GPU page for each nonempty layer. The cell is complete when SubmitGpuPages writes the pages.
        // A cell that does not fit the instance limit or the GPU reservation queues nothing, keeps no ranges and
        // stays inactive.
        private bool StageGpu(Cell cell, VegetationStreamingBudget budget)
        {
            int firstPage = _generator.PageCount;
            Span<int> counts = stackalloc int[_details.Length];
            Span<int> outputs = stackalloc int[_details.Length];
            int total = 0;
            for (int layer = 0; layer < _details.Length; layer++)
            {
                counts[layer] = _generator.AddPage(cell.Patch, layer, out outputs[layer]);
                total += counts[layer];
            }
            if (ResidentCount + total > _instanceLimit)
            {
                if (cell.Required)
                {
                    EvictPrefetch();
                }
                if (!cell.Required || ResidentCount + total > _instanceLimit)
                {
                    _generator.Truncate(firstPage);
                    return false;
                }
            }
            // A range can grow a population to the next power of two, and the storage trim keeps that capacity. So the
            // cell must fit the GPU reservation, with eight view buffers, before it allocates.
            if (ProjectedGpuBytesWith(counts) > budget.ResidentGpuBytes)
            {
                if (cell.Required)
                {
                    EvictPrefetch();
                }
                if (!cell.Required || ProjectedGpuBytesWith(counts) > budget.ResidentGpuBytes)
                {
                    _generator.Truncate(firstPage);
                    _gpuReservationFull = true;
                    _gpuReservationFullBudget = budget.ResidentGpuBytes;
                    return false;
                }
            }
            _active.Add(cell);
            Vector3 origin = _terrain.transform.position;
            var pivots = new Bounds(new Vector3(cell.Bounds.center.x, origin.y + _data.size.y * 0.5f, cell.Bounds.center.z),
                new Vector3(cell.Bounds.size.x, _data.size.y, cell.Bounds.size.z));
            for (int layer = 0; layer < _details.Length; layer++)
            {
                if (counts[layer] == 0) continue;
                InstanceRange range = _detailRenderer.AllocateRange(_detailTypes[layer], counts[layer], pivots,
                    _generator.Reach);
                cell.Ranges.Add(range);
                _rangeWrites.Add(new InstanceRangeWrite(range, outputs[layer]));
            }
            ResidentCount += total;
            Complete(cell);
            return true;
        }

        // The projected GPU bytes after the detail ranges of one cell, one count for each layer.
        private long ProjectedGpuBytesWith(ReadOnlySpan<int> counts)
        {
            return _treeRenderer.ProjectedGpuBytes() +
                _detailRenderer.ProjectedGpuBytesAfterRanges(_detailTypes.AsSpan(0, counts.Length), counts);
        }

        // Place all queued pages and expand them into the detail renderer. Both steps run before later GPU work.
        private void SubmitGpuPages()
        {
            if (_generator == null || _generator.PendingPlacements == 0) return;
            _generator.Dispatch();
            if (_rangeWrites.Count > 0)
            {
                _detailRenderer.WriteRanges(_generator.Placements, _rangeWrites);
            }
            _rangeWrites.Clear();
            _generator.Clear();
        }

        private void Complete(Cell cell)
        {
            cell.Complete = true;
            ResidentCells++;
            Loads++;
            cell.DetailTransforms = null;
            ReleasePage(cell);
        }

        private void EvictPrefetch()
        {
            _evict.Clear();
            foreach (Cell cell in _active)
            {
                if (!cell.Required)
                {
                    _evict.Add(cell);
                }
            }
            foreach (var cell in _evict)
            {
                Evict(cell);
            }
        }

        private void Evict(Cell cell)
        {
            if (!_active.Remove(cell)) return;
            var renderer = cell.Trees != null ? _treeRenderer : _detailRenderer;
            foreach (var handle in cell.Handles)
            {
                renderer.Remove(handle);
            }
            ResidentCount -= cell.Handles.Count;
            foreach (InstanceRange range in cell.Ranges)
            {
                // A cell staged in this update can have a queued write. The write must not reach a released range.
                for (int i = _rangeWrites.Count - 1; i >= 0; i--)
                {
                    if (_rangeWrites[i].Range.Equals(range))
                    {
                        _rangeWrites.RemoveAt(i);
                    }
                }
                _detailRenderer.ReleaseRange(range);
                ResidentCount -= range.Count;
            }
            if (cell.Complete)
            {
                ResidentCells--;
            }
            cell.Handles.Clear();
            cell.Ranges.Clear();
            cell.Complete = false;
            cell.Cursor = cell.Layer = 0;
            cell.DetailTransforms = null;
            cell.HasHeights = false;
            ReleasePage(cell);
            Evictions++;
            _trimPending = true;
            // The eviction frees GPU storage, so the next update can try the GPU reservation again.
            _gpuReservationFull = false;
        }

        private void ReleasePage(Cell cell)
        {
            if (cell.Reading)
            {
                _pendingReads--;
                cell.Reading = false;
            }
            cell.Cancellation?.Cancel();
            cell.Cancellation?.Dispose();
            cell.Cancellation = null;
            if (cell.PageTask != null)
            {
                cell.PageTask.ContinueWith(task => { _ = task.Exception; }, System.Threading.CancellationToken.None,
                    System.Threading.Tasks.TaskContinuationOptions.OnlyOnFaulted | System.Threading.Tasks.TaskContinuationOptions.ExecuteSynchronously,
                    System.Threading.Tasks.TaskScheduler.Default);
            }
            cell.PageTask = null;
            cell.Page = null;
        }

        public void Dispose()
        {
            _generator?.Dispose();
            _treeRenderer.Dispose();
            _detailRenderer.Dispose();
            _detailResources.Dispose();
            foreach (var cell in _active)
            {
                ReleasePage(cell);
            }
            _active.Clear();
            ResidentCount = ResidentCells = 0;
            IsReady = IsRendering = false;
        }
    }
}
