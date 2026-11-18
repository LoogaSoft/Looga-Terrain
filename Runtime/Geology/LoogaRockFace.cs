using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Splines;
using LoogaSoft.Terrain.Surface;

namespace LoogaSoft.Terrain.Geology
{
    /// <summary>
    /// A passage through the rock, for example under a bridge deck. Within the half width of the segment from
    /// <see cref="Start"/> to <see cref="End"/> the rock stays below the segment height. Outside it the allowed height
    /// rises <see cref="SideSlope"/> meters for each meter of distance, so the rock forms a notch.
    /// </summary>
    [Serializable]
    public struct RockFaceOpening
    {
        public Vector3 Start;
        public Vector3 End;
        [Min(0f)] public float HalfWidth;
        [Min(0f), Tooltip("Rise of the allowed rock height for each meter beside the opening. 0 = 1 m per meter.")]
        public float SideSlope;

        public RockFaceOpening(Vector3 start, Vector3 end, float halfWidth, float sideSlope = 1f)
        {
            Start = start;
            End = end;
            HalfWidth = halfWidth;
            SideSlope = sideSlope;
        }

        /// <summary>Highest rock surface at a world XZ position.</summary>
        public float HeightLimit(float x, float z)
        {
            var point = new Vector2(x, z);
            var a = new Vector2(Start.x, Start.z);
            Vector2 segment = new Vector2(End.x, End.z) - a;
            float length = segment.sqrMagnitude;
            float t = length > 1e-6f ? Mathf.Clamp01(Vector2.Dot(point - a, segment) / length) : 0f;
            float distance = Vector2.Distance(point, a + segment * t);
            // Openings saved before the side slope existed have 0, which keeps their 1 m per meter notch.
            float sideSlope = SideSlope > 0f ? SideSlope : 1f;
            return Mathf.Lerp(Start.y, End.y, t) + Mathf.Max(0f, distance - HalfWidth) * sideSlope;
        }
    }

    /// <summary>
    /// Generated rock skin over the steep terrain next to a cliff-top spline.
    /// The mesh is built from the live terrain heights and is not saved with the scene.
    /// </summary>
    /// <remarks>
    /// In edit mode a build waits in a queue. The editor builds several rock faces at a time on worker threads, and the
    /// old meshes stay until the new meshes are ready. In edit mode the rock face also builds again when its spline
    /// changes and when its ground source reports new heights in its region. In play mode the rock face builds when it
    /// is enabled.
    /// </remarks>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [AddComponentMenu("Looga Terrain/Rock Face")]
    public sealed class LoogaRockFace : MonoBehaviour, IGeneratedMeshSource
    {
        // Edit mode builds that run at the same time. Each build also samples its columns in parallel.
        private const int ParallelBuilds = 4;
        // Faces at the start of the queue whose ground sources read ahead. Their GPU reads run while other faces build.
        private const int PrefetchWindow = 2 * ParallelBuilds;
        // Longest wait of edit mode builds for a ground source that is not ready, in seconds. Then they build anyway.
        private const double GroundWaitLimit = 10.0;
        // Edit mode builds start only after the watched ground heights stayed unchanged for this time, in seconds. A
        // dragged stamp then builds the rock once at the end and not on each height write.
        private const double GroundQuietTime = 0.3;
        // Length of spline that one rectangle of the watched ground covers, in meters.
        private const float WatchSegmentLength = 50f;
        // Time when the builds started to wait for a ground source, or -1.
        private static double _groundWaitStart = -1.0;
        // Time of the last ground height change that queued a build.
        private static double _lastGroundChange = double.NegativeInfinity;
        private static readonly List<LoogaRockFace> PendingBuilds = new();
        private static readonly List<BuildJob> RunningBuilds = new();
        private static int[] _columnTerrains = new int[0];
        // Ground sources of the builds since the queue was last empty. They keep heights for the next builds.
        private static readonly HashSet<RockFaceGroundSource> BuildSources = new();

        [Header("Spline")]
        [Tooltip("Spline along the cliff top.")]
        [SerializeField] private SplineContainer _spline;
        [SerializeField] private bool _buildOnEnable = true;
        [Tooltip("Optional heights that the rock builds on, for example a snapshot before a talus stamp. " +
                 "Empty = the terrain heights.")]
        [SerializeField] private RockFaceGroundSource _groundSource;

        [Header("Shape")]
        [SerializeField] private RockFaceSettings _settings = new();
        [Tooltip("Raised terrain around the foot of the rock. A talus stamp that links to this face applies it.")]
        [SerializeField] private RockFaceApron _apron = new();
        [Tooltip("Passages where the rock stays low, for example under bridge decks.")]
        [SerializeField] private List<RockFaceOpening> _openings = new();

        [Header("Surface")]
        [SerializeField] private Material _material;
        [SerializeField] private ShadowCastingMode _shadowCasting = ShadowCastingMode.On;

        [Header("Levels of Detail")]
        [Tooltip("Largest length of one chunk along the cliff, in meters. Each chunk culls and changes detail alone.")]
        [SerializeField, Min(4f)] private float _chunkLength = 48f;
        [Tooltip("Screen height of a chunk below which it uses the second and the third level of detail.")]
        [SerializeField] private Vector2 _lodTransitions = new(0.3f, 0.08f);

        private readonly List<Mesh> _meshes = new();
        // The renderers of the chunks and the terrain below each, so the ground values follow a surface change.
        private readonly List<(UnityEngine.Terrain Terrain, MeshRenderer Renderer)> _groundRenderers = new();
        // The ground block of each terrain below the chunks.
        private readonly List<(UnityEngine.Terrain Terrain, MaterialPropertyBlock Block)> _groundBlocks = new();
        private GameObject _output;
        // A running build gives its meshes only while no newer build started.
        private int _buildVersion;
        // World XZ region that the last build read, or null when the spline or the settings changed after it.
        private Rect? _region;
        private RockFaceGroundSource _watchedSource;
        [System.NonSerialized] private RockFaceApron _appliedApron;
        // A build waited or ran when the scripts unloaded, so the meshes of the last domain are old. A script reload
        // keeps private fields, so the flag must not stay into the next domain.
        [NonSerialized] private bool _staleAtReload;
#if UNITY_EDITOR
        // True after this enable looked for a cache file.
        [NonSerialized] private bool _cacheChecked;
        // The chunks of the build that the cache file will hold.
        private readonly List<(RockFaceChunks.Chunk Chunk, bool Collider, string Terrain)> _builtChunks = new();
#endif
        // World XZ region of the ground read ahead while the face waits in the queue.
        private Rect? _prefetchRegion;
        // World XZ rectangles along the spline, each grown by the search reach, or null until the next height change
        // needs them. A height change outside them does not change the rock, also when it is inside the bounding
        // region of a long face.
        [NonSerialized] private List<Rect> _watchRects;

        public SplineContainer Spline => _spline;
        public RockFaceSettings Settings => _settings;

        /// <summary>The raised ground around the foot of the rock. Call <see cref="NotifyApronChanged"/> after a change
        /// from a script.</summary>
        public RockFaceApron Apron => _apron;

        /// <summary>The apron changed. Linked talus stamps apply it again.</summary>
        public event System.Action ApronChanged;

        /// <summary>Tells the linked talus stamps that the apron changed.</summary>
        public void NotifyApronChanged()
        {
            _appliedApron = _apron.Copy();
            ApronChanged?.Invoke();
        }
        public IReadOnlyList<RockFaceOpening> Openings => _openings;
        public int ColumnCount { get; private set; }
        public int ActiveColumnCount { get; private set; }
        public int StripCount { get; private set; }
        public int ChunkCount { get; private set; }
        public int VertexCount { get; private set; }
        public int TriangleCount { get; private set; }
        public int CorrectionCount { get; private set; }
        /// <summary>CPU time of the last build: the main-thread reads, the strips and the chunks.</summary>
        public float BuildMilliseconds { get; private set; }
        public float ProfileMilliseconds { get; private set; }
        public float SurfaceMilliseconds { get; private set; }
        public float ChunkMilliseconds { get; private set; }
        public Bounds GeneratedBounds { get; private set; }
        public string Diagnostic { get; private set; }
        /// <summary>
        /// Changes when the generated meshes are replaced or removed. A reader of GetGeneratedRenderers, such as a ray
        /// tracing scene, reads the renderers again when this value changes.
        /// </summary>
        public int OutputVersion { get; private set; }

        /// <summary>Number of rock faces that wait for an edit mode build or build now.</summary>
        public static int PendingCount => PendingBuilds.Count + RunningBuilds.Count;

        #region Built-in
        private void OnEnable()
        {
            LoogaTerrainSurfaceBinding.SurfaceChanged += OnTerrainSurfaceChanged;
            UnityEngine.Splines.Spline.Changed += OnSplineChanged;
            WatchGround();
#if UNITY_EDITOR
            _cacheChecked = false;
#endif
            if (AdoptAfterReload() || LoadPlayerMeshes()) return;
            if (_buildOnEnable) RequestBuild();
        }

        // A terrain can replace its splat maps. The chunks on it then read the new maps.
        private void Update()
        {
            for (int index = 0; index < _groundBlocks.Count; index++)
            {
                (UnityEngine.Terrain terrain, MaterialPropertyBlock block) = _groundBlocks[index];
                if (terrain && !LoogaTerrainGroundBlock.HasControlMaps(block)) OnTerrainSurfaceChanged(terrain);
            }
        }

        private void OnDisable()
        {
            LoogaTerrainSurfaceBinding.SurfaceChanged -= OnTerrainSurfaceChanged;
            UnityEngine.Splines.Spline.Changed -= OnSplineChanged;
            UnwatchGround();
            PendingBuilds.Remove(this);
            _buildVersion++;
            // A script reload keeps the meshes. The next domain takes them back if the code did not change.
            if (GeneratedReloadHandover.Unloading && StoreForReload()) return;
            Release();
        }

        private void OnDestroy()
        {
            PendingBuilds.Remove(this);
            Release();
        }

        // OnValidate must not create objects, so it only queues the build. Unity also calls it when the scripts load,
        // without a settings change. OnEnable then decides whether the face builds.
        private void OnValidate()
        {
            // The first call after a load only records the apron. The terrain already has it.
            if (_appliedApron == null) _appliedApron = _apron.Copy();
            else if (!_apron.Same(_appliedApron)) NotifyApronChanged();
            if (!isActiveAndEnabled || GeneratedReloadHandover.LoadingScripts) return;
            _region = null;
            _prefetchRegion = null;
            _watchRects = null;
            WatchGround();
            if (!PendingBuilds.Contains(this)) PendingBuilds.Add(this);
        }

        private void LateUpdate()
        {
            if (Application.isPlaying && PendingBuilds.Contains(this)) Rebuild();
        }
        #endregion

        #region Live Rebuilds
        // In edit mode the rock follows its spline and the ground heights. A build waits until the ground is ready,
        // so a request during a terrain update builds on the new heights.
        private void OnSplineChanged(UnityEngine.Splines.Spline spline, int knotIndex, SplineModification modification)
        {
            if (Application.isPlaying || !_spline) return;
            foreach (UnityEngine.Splines.Spline own in _spline.Splines)
            {
                if (own != spline) continue;
                _region = null;
                _prefetchRegion = null;
                _watchRects = null;
                RequestBuild();
                return;
            }
        }

        private void OnGroundHeightsChanged(Rect worldRegion)
        {
            if (Application.isPlaying || !WatchesRegion(worldRegion)) return;
            // A change that the queue waits for restarts the waits, also while this face waits already.
            _lastGroundChange = Time.realtimeSinceStartupAsDouble;
            _groundWaitStart = -1.0;
            RequestBuild();
        }

        // True when the region reaches the ground near the spline. Without a build there is no known region, so every
        // change counts.
        private bool WatchesRegion(Rect worldRegion)
        {
            if (!_region.HasValue) return true;
            if (!_region.Value.Overlaps(worldRegion)) return false;
            if (!_spline || _spline.Spline == null || _spline.Spline.Count < 2) return true;
            _watchRects ??= GetWatchRects();
            foreach (Rect rect in _watchRects)
            {
                if (rect.Overlaps(worldRegion)) return true;
            }
            return false;
        }

        private void WatchGround()
        {
            if (_groundSource == _watchedSource) return;
            UnwatchGround();
            _watchedSource = _groundSource;
            if (_watchedSource) _watchedSource.HeightsChanged += OnGroundHeightsChanged;
        }

        private void UnwatchGround()
        {
            if (!ReferenceEquals(_watchedSource, null)) _watchedSource.HeightsChanged -= OnGroundHeightsChanged;
            _watchedSource = null;
        }
        #endregion

        #region Build
        /// <summary>
        /// Builds the rock face again. In play mode it builds now. In edit mode it waits for
        /// <see cref="UpdateBuilds"/>.
        /// </summary>
        public void RequestBuild()
        {
            if (Application.isPlaying)
            {
                Rebuild();
                return;
            }
            if (!PendingBuilds.Contains(this)) PendingBuilds.Add(this);
        }

        /// <summary>
        /// Advances the edit mode builds: gives the meshes of the finished builds and starts waiting builds on worker
        /// threads. Main thread. Returns true when generated meshes changed. The GPU reads of the ground and the terrain
        /// heights run in the background, so the main thread does not wait for them.
        /// </summary>
        public static bool UpdateBuilds()
        {
            bool changed = false;
            for (int index = 0; index < RunningBuilds.Count;)
            {
                BuildJob job = RunningBuilds[index];
                // A build goes to a worker thread after its terrain heights arrive from the GPU.
                if (job.Task == null && job.IsReady)
                {
                    job.Start();
                }
                if (job.Task == null || !job.Task.IsCompleted)
                {
                    index++;
                    continue;
                }
                RunningBuilds.RemoveAt(index);
                changed |= job.Face.FinishBuild(job);
            }
            // The faces build in queue order when their ground is read. A face that waits for its read lets the next
            // faces of the window start.
            for (int index = 0; index < PendingBuilds.Count && index < PrefetchWindow;)
            {
                LoogaRockFace face = PendingBuilds[index];
                if (!face || !face.isActiveAndEnabled)
                {
                    PendingBuilds.RemoveAt(index);
                    continue;
                }
#if UNITY_EDITOR
                // One face for each update, so the loads do not freeze the editor.
                if (!face._cacheChecked)
                {
                    face._cacheChecked = true;
                    if (face.LoadFromCache())
                    {
                        PendingBuilds.RemoveAt(index);
                        EndBuildsIfIdle();
                        return true;
                    }
                }
#endif
                if (!GroundReady(face)) break;
                if (!face.PrefetchGround() || RunningBuilds.Count >= ParallelBuilds)
                {
                    index++;
                    continue;
                }
                PendingBuilds.RemoveAt(index);
                BuildJob job = face.StartBuild();
                if (job == null)
                {
                    changed = true;
                    continue;
                }
                RunningBuilds.Add(job);
            }
            if (PendingBuilds.Count == 0) _groundWaitStart = -1.0;
            EndBuildsIfIdle();
            return changed;
        }

        /// <summary>
        /// Reads the terrain heights again and replaces the generated meshes now.
        /// </summary>
        [ContextMenu("Rebuild")]
        public void Rebuild()
        {
            using ProfilerMarker.AutoScope build = LoogaGeologyMarkers.RockFaceBuild.Auto();
            PendingBuilds.Remove(this);
            BuildJob job = StartBuild();
            if (job != null)
            {
                job.Run();
                FinishBuild(job);
            }
            EndBuildsIfIdle();
        }

        // Reads the spline, the ground and the terrain heights on the main thread. Returns null when the build ends
        // here; the rock face then has no meshes and a diagnostic.
        private BuildJob StartBuild()
        {
            _buildVersion++;
            _prefetchRegion = null;
            Diagnostic = null;
            if (!isActiveAndEnabled)
            {
                Release();
                Diagnostic = "Enable the GameObject before building the rock face.";
                return null;
            }
            if (!_spline || _spline.Spline == null || _spline.Spline.Count < 2)
            {
                Release();
                Diagnostic = "Assign a spline with at least two knots.";
                return null;
            }

            var job = new BuildJob
            {
                Face = this,
                Version = _buildVersion,
                Settings = _settings.Copy(),
                Openings = new List<RockFaceOpening>(_openings)
            };
            Stopwatch timer = Stopwatch.StartNew();
            try
            {
                Rect region = GetRegion();
                _region = region;
                _watchRects = null;
                if (_groundSource)
                {
                    BuildSources.Add(_groundSource);
                    job.Ground = _groundSource.Open(region);
                    if (job.Ground == null)
                    {
                        Release();
                        Diagnostic = "The ground source has no heights. Check its snapshot.";
                        return null;
                    }
                }
                job.Workspace = RockFaceBuilder.RentWorkspace();
                job.Input = RockFaceBuilder.Prepare(_spline, region, job.Workspace);
            }
            catch (Exception exception)
            {
                job.Dispose();
                job.ReturnWorkspace();
                Release();
                Diagnostic = exception.Message;
                return null;
            }
            job.ReadMilliseconds = (float)timer.Elapsed.TotalMilliseconds;
            return job;
        }

        // Replaces the meshes with the strips of a build. Returns true when the generated meshes changed.
        private bool FinishBuild(BuildJob job)
        {
            job.Dispose();
            try
            {
                return ApplyBuild(job);
            }
            finally
            {
                // The meshes are copies, so the next build can use the strip buffers again.
                job.ReturnWorkspace();
            }
        }

        private bool ApplyBuild(BuildJob job)
        {
            // A newer build replaces this one. A disabled rock face has no meshes.
            if (job.Version != _buildVersion || !this || !isActiveAndEnabled) return false;

            Release();
            if (job.Error != null)
            {
                Diagnostic = job.Error.Message;
                return true;
            }
            RockFaceStats stats = job.Stats;
            ColumnCount = stats.ColumnCount;
            ActiveColumnCount = stats.ActiveColumnCount;
            StripCount = stats.StripCount;
            VertexCount = stats.VertexCount;
            TriangleCount = stats.TriangleCount;
            CorrectionCount = stats.CorrectionCount;
            ProfileMilliseconds = stats.ProfileMilliseconds;
            SurfaceMilliseconds = stats.SurfaceMilliseconds;
            float buildMilliseconds = job.ReadMilliseconds + stats.ProfileMilliseconds + stats.SurfaceMilliseconds;
            if (job.Strips.Count == 0)
            {
                Diagnostic = "No terrain face in the spline range is steep and high enough.";
                BuildMilliseconds = buildMilliseconds;
#if UNITY_EDITOR
                // The empty result is cached too, so a player loads it and does not read the ground at the scene load.
                RockFaceChunks.BeginRecording();
                _builtChunks.Clear();
                WriteCache();
#endif
                return true;
            }

            try
            {
                Stopwatch chunkTimer = Stopwatch.StartNew();
#if UNITY_EDITOR
                RockFaceChunks.BeginRecording();
#endif
                CreateOutput(job.Strips);
                ChunkMilliseconds = (float)chunkTimer.Elapsed.TotalMilliseconds;
                BuildMilliseconds = buildMilliseconds + ChunkMilliseconds;
#if UNITY_EDITOR
                WriteCache();
#endif
            }
            catch (Exception exception)
            {
#if UNITY_EDITOR
                RockFaceChunks.EndRecording();
#endif
                Diagnostic = exception.Message;
                Release();
            }
            return true;
        }

#if UNITY_EDITOR
        // Writes the meshes of the finished build. Meshes from other sources keep the face out of the cache.
        private void WriteCache()
        {
            Dictionary<Mesh, RockFaceChunks.MeshRecord> records = RockFaceChunks.EndRecording();
            if (Application.isPlaying || !_spline) return;
            var chunks = new List<GeneratedMeshCache.ChunkEntry>(_builtChunks.Count);
            foreach ((RockFaceChunks.Chunk chunk, bool collider, string terrain) in _builtChunks)
            {
                var levels = new RockFaceChunks.MeshRecord[chunk.Levels.Length];
                for (int level = 0; level < levels.Length; level++)
                {
                    if (!records.TryGetValue(chunk.Levels[level], out levels[level])) return;
                }
                chunks.Add(new GeneratedMeshCache.ChunkEntry
                {
                    Terrain = terrain, Collider = collider, Bounds = chunk.Bounds, Levels = levels
                });
            }
            _builtChunks.Clear();
            GeneratedMeshCache.Write(GeneratedMeshCache.PathOf(this), GeneratedMeshCache.Key(this, _spline),
                JsonUtility.ToJson(CurrentState()), chunks);
        }

        // Takes the meshes of an earlier editor session. Returns false when the face must build.
        private bool LoadFromCache()
        {
            if (Application.isPlaying || !_spline || _spline.Spline == null) return false;
            return LoadMeshes(GeneratedMeshCache.PathOf(this), GeneratedMeshCache.Key(this, _spline));
        }

        bool IGeneratedMeshSource.GetCacheFile(out string path, out Hash128 key)
        {
            path = null;
            key = default;
            if (!isActiveAndEnabled || !_spline || _spline.Spline == null) return false;
            path = GeneratedMeshCache.PathOf(this);
            key = GeneratedMeshCache.Key(this, _spline);
            return true;
        }

        GeneratedMeshBuild IGeneratedMeshSource.BuildForPlayer()
        {
            Rebuild();
            return GeneratedMeshBuild.Built;
        }

        bool IGeneratedMeshSource.PlayerCanBuild => !_groundSource || _groundSource.ReadableInPlayer;
#endif

        // Takes the meshes that the player build shipped. Returns false in the editor and when the face must build.
        private bool LoadPlayerMeshes()
        {
            string path = GeneratedMeshCache.PlayerPath(this);
            if (path == null) return false;
            using ProfilerMarker.AutoScope load = LoogaGeologyMarkers.ShippedMeshLoad.Auto();
            return LoadMeshes(path, null);
        }

        // Takes the meshes of a cache file. Returns false when there is no file, or when a key is given and the file has
        // another key.
        private bool LoadMeshes(string path, Hash128? key)
        {
            UnityEngine.Terrain[] terrains = GeneratedMeshCache.Terrains();
            if (!GeneratedMeshCache.TryRead(path, key, terrains, LoogaCaveEntrance.DetailAreas(), out string meta,
                    out List<(RockFaceChunks.Chunk Chunk, bool Collider)> chunks))
            {
                return false;
            }
            Release();
            // A file without chunks holds a build that found no rock face.
            if (chunks.Count > 0)
            {
                CreateOutput(null, chunks, terrains);
            }
#if UNITY_EDITOR
            _builtChunks.Clear();
#endif
            ApplyState(JsonUtility.FromJson<ReloadState>(meta));
            return true;
        }

        // Starts the GPU read of the ground region of a waiting face. Returns true when the build can read the ground
        // without a wait. StartBuild reports the errors of the spline and the ground source.
        private bool PrefetchGround()
        {
            if (!_groundSource || !_spline || _spline.Spline == null || _spline.Spline.Count < 2) return true;
            BuildSources.Add(_groundSource);
            try
            {
                _prefetchRegion ??= GetRegion();
                return _groundSource.Prefetch(_prefetchRegion.Value);
            }
            catch (Exception)
            {
                return true;
            }
        }

        // A build on incomplete ground heights would stay wrong until the next build, so the queue waits for them. It
        // also waits while the heights still change.
        private static bool GroundReady(LoogaRockFace face)
        {
            double now = Time.realtimeSinceStartupAsDouble;
            if (face._groundSource && now - _lastGroundChange < GroundQuietTime) return false;
            if (!face._groundSource || face._groundSource.IsReady)
            {
                _groundWaitStart = -1.0;
                return true;
            }
            if (_groundWaitStart < 0.0) _groundWaitStart = now;
            return now - _groundWaitStart >= GroundWaitLimit;
        }

        // The ground sources release the heights that they kept for the builds of the queue.
        private static void EndBuildsIfIdle()
        {
            if (PendingBuilds.Count > 0 || RunningBuilds.Count > 0 || BuildSources.Count == 0) return;
            foreach (RockFaceGroundSource source in BuildSources)
            {
                if (source) source.EndBuilds();
            }
            BuildSources.Clear();
        }

#if UNITY_EDITOR
        [UnityEditor.InitializeOnLoadMethod]
        private static void WatchScriptReload()
        {
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += EndBuildsBeforeReload;
        }

        // A script reload loses the queues. The running builds finish first, then the ground sources release the
        // heights that they keep. Rock faces with Build On Enable build again after the reload.
        private static void EndBuildsBeforeReload()
        {
            foreach (LoogaRockFace face in PendingBuilds)
            {
                if (face) face._staleAtReload = true;
            }
            foreach (BuildJob job in RunningBuilds)
            {
                if (job.Face) job.Face._staleAtReload = true;
                job.Task?.Wait();
                job.Dispose();
                job.ReturnWorkspace();
            }
            RunningBuilds.Clear();
            PendingBuilds.Clear();
            EndBuildsIfIdle();
        }
#endif

        #region Script Reload
        // The output of the last domain, stored for the next domain. Instance IDs stay the same over a script reload.
        [Serializable]
        private struct ReloadState
        {
            public int Output;
            public int[] Renderers;
            public int[] Terrains;
            public bool HasRegion;
            public Rect Region;
            public Bounds Bounds;
            public string Diagnostic;
            public int ColumnCount;
            public int ActiveColumnCount;
            public int StripCount;
            public int ChunkCount;
            public int VertexCount;
            public int TriangleCount;
            public int CorrectionCount;
            public float BuildMilliseconds;
            public float ProfileMilliseconds;
            public float SurfaceMilliseconds;
            public float ChunkMilliseconds;
        }

        private string ReloadKey => "LoogaSoft.Terrain.RockFace." + GetInstanceID();

        // Returns false when the meshes are old and must be released.
        private bool StoreForReload()
        {
#if UNITY_EDITOR
            if (_staleAtReload) return false;
            var renderers = new int[_groundRenderers.Count];
            var terrains = new int[_groundRenderers.Count];
            for (int index = 0; index < _groundRenderers.Count; index++)
            {
                (UnityEngine.Terrain terrain, MeshRenderer renderer) = _groundRenderers[index];
                renderers[index] = renderer ? renderer.GetInstanceID() : 0;
                terrains[index] = terrain ? terrain.GetInstanceID() : 0;
            }
            ReloadState state = CurrentState();
            state.Output = _output ? _output.GetInstanceID() : 0;
            state.Renderers = renderers;
            state.Terrains = terrains;
            GeneratedReloadHandover.Store(ReloadKey, state);
            return true;
#else
            return false;
#endif
        }

        private ReloadState CurrentState()
        {
            return new ReloadState
            {
                HasRegion = _region.HasValue,
                Region = _region ?? default,
                Bounds = GeneratedBounds,
                Diagnostic = Diagnostic,
                ColumnCount = ColumnCount,
                ActiveColumnCount = ActiveColumnCount,
                StripCount = StripCount,
                ChunkCount = ChunkCount,
                VertexCount = VertexCount,
                TriangleCount = TriangleCount,
                CorrectionCount = CorrectionCount,
                BuildMilliseconds = BuildMilliseconds,
                ProfileMilliseconds = ProfileMilliseconds,
                SurfaceMilliseconds = SurfaceMilliseconds,
                ChunkMilliseconds = ChunkMilliseconds
            };
        }

        private void ApplyState(ReloadState state)
        {
            _region = state.HasRegion ? state.Region : null;
            GeneratedBounds = state.Bounds;
            Diagnostic = state.Diagnostic;
            ColumnCount = state.ColumnCount;
            ActiveColumnCount = state.ActiveColumnCount;
            StripCount = state.StripCount;
            ChunkCount = state.ChunkCount;
            VertexCount = state.VertexCount;
            TriangleCount = state.TriangleCount;
            CorrectionCount = state.CorrectionCount;
            BuildMilliseconds = state.BuildMilliseconds;
            ProfileMilliseconds = state.ProfileMilliseconds;
            SurfaceMilliseconds = state.SurfaceMilliseconds;
            ChunkMilliseconds = state.ChunkMilliseconds;
        }

        // Takes the output of the last domain after a script reload. Returns false when the rock face must build.
        private bool AdoptAfterReload()
        {
#if UNITY_EDITOR
            if (!GeneratedReloadHandover.TryTake(ReloadKey, out ReloadState state)) return false;
            var output = GeneratedReloadHandover.Find<GameObject>(state.Output);
            bool valid = GeneratedReloadHandover.CanAdopt && (state.Output == 0 || output) &&
                state.Renderers != null && state.Terrains != null;
            if (!valid)
            {
                DestroyOutput(output);
                return false;
            }

            _output = output;
            OutputVersion++;
            _meshes.Clear();
            if (output)
            {
                foreach (MeshFilter filter in output.GetComponentsInChildren<MeshFilter>(true))
                {
                    if (filter.sharedMesh) _meshes.Add(filter.sharedMesh);
                }
            }
            _groundRenderers.Clear();
            _groundBlocks.Clear();
            var blocks = new Dictionary<UnityEngine.Terrain, MaterialPropertyBlock>();
            for (int index = 0; index < state.Renderers.Length; index++)
            {
                var renderer = GeneratedReloadHandover.Find<MeshRenderer>(state.Renderers[index]);
                var terrain = GeneratedReloadHandover.Find<UnityEngine.Terrain>(state.Terrains[index]);
                if (!renderer || !terrain) continue;
                if (!blocks.TryGetValue(terrain, out MaterialPropertyBlock block))
                {
                    block = LoogaTerrainGroundBlock.Create(terrain);
                    blocks.Add(terrain, block);
                    _groundBlocks.Add((terrain, block));
                }
                renderer.SetPropertyBlock(block);
                _groundRenderers.Add((terrain, renderer));
            }
            ApplyState(state);
            return true;
#else
            return false;
#endif
        }

        // Destroys an output of the last domain that this domain cannot use. Its meshes are separate objects.
        private static void DestroyOutput(GameObject output)
        {
            if (!output) return;
            foreach (MeshFilter filter in output.GetComponentsInChildren<MeshFilter>(true))
            {
                if (filter.sharedMesh) DestroyGenerated(filter.sharedMesh);
            }
            DestroyGenerated(output);
        }
        #endregion

        // One build. The main thread prepares the input and makes the meshes. The strips build on any thread from
        // copies of the settings and the openings.
        private sealed class BuildJob : IDisposable
        {
            public LoogaRockFace Face;
            public int Version;
            public RockFaceSettings Settings;
            public List<RockFaceOpening> Openings;
            public IRockFaceGroundReader Ground;
            public RockFaceInput Input;
            public float ReadMilliseconds;
            public Task Task;
            public RockFaceBuilder.Workspace Workspace;
            public List<RockFaceStrip> Strips;
            public RockFaceStats Stats;
            public Exception Error;

            /// <summary>True when the terrain heights arrived from the GPU. Main thread.</summary>
            public bool IsReady => Input.Terrain.IsReady;

            // Starts the build on a worker thread after IsReady. Main thread.
            public void Start()
            {
                Task = TakeTerrainHeights() ? Task.Run(Execute) : Task.CompletedTask;
            }

            // Builds on this thread. Main thread. Waits for the terrain heights.
            public void Run()
            {
                if (TakeTerrainHeights()) Execute();
            }

            // Waits for the terrain heights. Returns false and keeps the error when a GPU read failed.
            private bool TakeTerrainHeights()
            {
                try
                {
                    Input.Terrain.WaitForReads();
                    return true;
                }
                catch (Exception exception)
                {
                    Error = exception;
                    return false;
                }
            }

            private void Execute()
            {
                try
                {
                    Strips = RockFaceBuilder.Build(Input, Settings, Openings, Ground, Workspace, out Stats);
                }
                catch (Exception exception)
                {
                    Error = exception;
                }
            }

            public void Dispose()
            {
                Ground?.Dispose();
                Ground = null;
                Input?.Dispose();
                Input = null;
            }

            // The input points and the strips are part of the workspace and are not valid after this.
            public void ReturnWorkspace()
            {
                if (Workspace == null) return;
                RockFaceBuilder.ReturnWorkspace(Workspace);
                Workspace = null;
                Strips = null;
            }
        }

        public void ConfigureSpline(SplineContainer spline)
        {
            _spline = spline;
            _region = null;
        }

        public void SetGroundSource(RockFaceGroundSource groundSource)
        {
            _groundSource = groundSource;
            if (isActiveAndEnabled) WatchGround();
        }

        /// <summary>Replaces the openings. Returns false when they did not change. The caller requests the build.</summary>
        public bool SetOpenings(IEnumerable<RockFaceOpening> openings)
        {
            var next = new List<RockFaceOpening>(openings);
            if (next.Count == _openings.Count && next.TrueForAll(opening => _openings.Contains(opening))) return false;
            _openings = next;
            return true;
        }

        public void SetMaterial(Material material)
        {
            _material = material;
            if (!_output) return;
            foreach (MeshRenderer meshRenderer in _output.GetComponentsInChildren<MeshRenderer>())
            {
                meshRenderer.sharedMaterial = material;
            }
        }

        /// <summary>
        /// Add the renderers of one LOD level of the generated rock face to the list: 0 is the most detailed level. The
        /// renderers are hidden objects that the rock face replaces on each build.
        /// </summary>
        public void GetGeneratedRenderers(int level, List<MeshRenderer> result)
        {
            if (!_output) return;
            foreach (LODGroup group in _output.GetComponentsInChildren<LODGroup>())
            {
                LOD[] lods = group.GetLODs();
                if (level < 0 || level >= lods.Length) continue;
                foreach (Renderer renderer in lods[level].renderers)
                {
                    if (renderer is MeshRenderer meshRenderer) result.Add(meshRenderer);
                }
            }
        }

        // Builds the chunks of the strips, or adds the chunks of a cache file when cachedChunks is not null. These hold
        // terrain indices of cachedTerrains.
        private void CreateOutput(List<RockFaceStrip> strips,
            List<(RockFaceChunks.Chunk Chunk, bool Collider)> cachedChunks = null,
            UnityEngine.Terrain[] cachedTerrains = null)
        {
            _output = new GameObject("Rock Face " + name)
            {
                hideFlags = HideFlags.HideAndDontSave,
                layer = gameObject.layer
            };
            OutputVersion++;
            int chunkColumns = Mathf.Max(4, Mathf.RoundToInt(_chunkLength / _settings.ColumnSpacing));
            Bounds? bounds = null;
            ChunkCount = 0;
            _groundRenderers.Clear();
            _groundBlocks.Clear();
            var lods = new LOD[RockFaceChunks.LevelCount];
            float[] transitions = { _lodTransitions.x, _lodTransitions.y, 0f };
            UnityEngine.Terrain[] terrains = cachedTerrains ?? UnityEngine.Terrain.activeTerrains;
            var groundBlocks = new Dictionary<int, MaterialPropertyBlock>();
            List<Bounds> entranceAreas = LoogaCaveEntrance.DetailAreas();
#if UNITY_EDITOR
            _builtChunks.Clear();
#endif
            if (cachedChunks != null)
            {
                foreach ((RockFaceChunks.Chunk chunk, bool collider) in cachedChunks)
                {
                    AddChunk(chunk, collider);
                }
            }
            else if (_settings.BlockWalls)
            {
                // Many small closed blocks: the blocks of one area and one terrain share a chunk, and the walls are
                // solid, so every chunk gets a collider when the face has colliders.
                foreach (List<RockFaceStrip> cell in GroupBlocks(strips, terrains))
                {
                    int terrain = LoogaTerrainGroundBlock.FindTerrain(LowestPoint(cell[0]), terrains);
                    RockFaceChunks.Chunk? chunk = RockFaceChunks.CreateCombined(cell, terrain, name,
                        _settings.Colliders ? 1 : -1, entranceAreas);
                    if (chunk.HasValue) AddChunk(chunk.Value, _settings.Colliders);
                }
            }
            else
            {
                foreach (RockFaceStrip strip in strips)
                {
                    int[] columnTerrains = FindColumnTerrains(strip, terrains);
                    // A walkable top keeps its level 1 mesh on the CPU for its collider.
                    bool topCollider = strip.IsTop && _settings.TopCollider && _settings.Colliders;
                    foreach (RockFaceChunks.Chunk chunk in RockFaceChunks.Create(strip, chunkColumns, columnTerrains,
                                 name, topCollider ? 1 : -1, entranceAreas))
                    {
                        AddChunk(chunk, topCollider);
                    }
                }
            }
            GeneratedBounds = bounds ?? default;
            foreach (KeyValuePair<int, MaterialPropertyBlock> pair in groundBlocks)
            {
                _groundBlocks.Add((terrains[pair.Key], pair.Value));
            }

            void AddChunk(RockFaceChunks.Chunk chunk, bool collider)
            {
#if UNITY_EDITOR
                _builtChunks.Add((chunk, collider, chunk.Group >= 0 ? terrains[chunk.Group].name : string.Empty));
#endif
                MaterialPropertyBlock groundBlock = GetGroundBlock(chunk.Group, terrains, groundBlocks);
                GameObject chunkObject = new("Chunk " + ChunkCount) { hideFlags = HideFlags.HideAndDontSave };
                chunkObject.layer = gameObject.layer;
                chunkObject.transform.SetParent(_output.transform, false);
                for (int level = 0; level < chunk.Levels.Length; level++)
                {
                    Mesh mesh = chunk.Levels[level];
                    _meshes.Add(mesh);
                    GameObject levelObject = new("LOD" + level) { hideFlags = HideFlags.HideAndDontSave };
                    levelObject.layer = gameObject.layer;
                    levelObject.transform.SetParent(chunkObject.transform, false);
                    levelObject.AddComponent<MeshFilter>().sharedMesh = mesh;
                    MeshRenderer meshRenderer = levelObject.AddComponent<MeshRenderer>();
                    meshRenderer.sharedMaterial = _material;
                    meshRenderer.shadowCastingMode = _shadowCasting;
                    meshRenderer.receiveShadows = true;
                    if (groundBlock != null)
                    {
                        meshRenderer.SetPropertyBlock(groundBlock);
                        _groundRenderers.Add((terrains[chunk.Group], meshRenderer));
                    }
                    lods[level] = new LOD(transitions[level], new Renderer[] { meshRenderer });
                }
                if (collider)
                {
                    chunkObject.AddComponent<MeshCollider>().sharedMesh = chunk.Levels[1];
                }
                LODGroup group = chunkObject.AddComponent<LODGroup>();
                group.SetLODs(lods);
                group.RecalculateBounds();
                bounds = bounds.HasValue ? Encapsulate(bounds.Value, chunk.Bounds) : chunk.Bounds;
                ChunkCount++;
            }
        }

        // Block strips grouped by an area of the chunk length and by the terrain under their lowest point.
        private List<List<RockFaceStrip>> GroupBlocks(List<RockFaceStrip> strips, UnityEngine.Terrain[] terrains)
        {
            var cells = new Dictionary<(int, int, int, int), List<RockFaceStrip>>();
            foreach (RockFaceStrip strip in strips)
            {
                Vector3 sum = Vector3.zero;
                for (int index = 0; index < strip.VertexCount; index++)
                {
                    sum += strip.Positions[index];
                }
                Vector3 centre = sum / Mathf.Max(strip.VertexCount, 1);
                var key = (Mathf.FloorToInt(centre.x / _chunkLength), Mathf.FloorToInt(centre.y / _chunkLength),
                    Mathf.FloorToInt(centre.z / _chunkLength),
                    LoogaTerrainGroundBlock.FindTerrain(LowestPoint(strip), terrains));
                if (!cells.TryGetValue(key, out List<RockFaceStrip> cell))
                {
                    cell = new List<RockFaceStrip>();
                    cells.Add(key, cell);
                }
                cell.Add(strip);
            }
            return new List<List<RockFaceStrip>>(cells.Values);
        }

        private static Vector3 LowestPoint(RockFaceStrip strip)
        {
            Vector3 lowest = strip.Positions[0];
            for (int index = 1; index < strip.VertexCount; index++)
            {
                if (strip.Positions[index].y < lowest.y) lowest = strip.Positions[index];
            }
            return lowest;
        }

        // Terrain below each grid column: the smallest terrain that holds the lowest point of the column. The ground
        // blend is at the base, and a small terrain tile is more detailed than a large background terrain.
        // The array is used again for the next strip, so it can be longer than the column count. Main thread only.
        private static int[] FindColumnTerrains(RockFaceStrip strip, UnityEngine.Terrain[] terrains)
        {
            if (_columnTerrains.Length < strip.Columns) _columnTerrains = new int[strip.Columns];
            int[] columnTerrains = _columnTerrains;
            for (int column = 0; column < strip.Columns; column++)
            {
                Vector3 lowest = strip.Positions[strip.Index(column, 0)];
                for (int row = 1; row < strip.Rows; row++)
                {
                    Vector3 position = strip.Positions[strip.Index(column, row)];
                    if (position.y < lowest.y) lowest = position;
                }
                columnTerrains[column] = LoogaTerrainGroundBlock.FindTerrain(lowest, terrains);
            }
            return columnTerrains;
        }

        // Splat control maps and the tile area of one terrain, for the terrain ground blend of the rock shader.
        private static MaterialPropertyBlock GetGroundBlock(int terrainIndex, UnityEngine.Terrain[] terrains,
            Dictionary<int, MaterialPropertyBlock> blocks)
        {
            if (terrainIndex < 0) return null;
            if (blocks.TryGetValue(terrainIndex, out MaterialPropertyBlock block)) return block;

            block = LoogaTerrainGroundBlock.Create(terrains[terrainIndex]);
            blocks.Add(terrainIndex, block);
            return block;
        }

        // A binding applied or removed a surface, or the terrain replaced its splat maps: the chunks on that terrain
        // read its layers and maps again.
        private void OnTerrainSurfaceChanged(UnityEngine.Terrain terrain)
        {
            MaterialPropertyBlock block = null;
            foreach ((UnityEngine.Terrain chunkTerrain, MeshRenderer renderer) in _groundRenderers)
            {
                if (chunkTerrain != terrain || !renderer) continue;
                block ??= LoogaTerrainGroundBlock.Create(terrain);
                renderer.SetPropertyBlock(block);
            }
            if (block == null) return;
            for (int index = 0; index < _groundBlocks.Count; index++)
            {
                if (_groundBlocks[index].Terrain == terrain) _groundBlocks[index] = (terrain, block);
            }
        }

        private static Bounds Encapsulate(Bounds bounds, Bounds other)
        {
            bounds.Encapsulate(other);
            return bounds;
        }

        // World XZ region that the face search can read: the spline bounds plus the search distances.
        private Rect GetRegion()
        {
            Vector3 min = Vector3.positiveInfinity;
            Vector3 max = Vector3.negativeInfinity;
            const int samples = 256;
            for (int index = 0; index <= samples; index++)
            {
                Vector3 point = _spline.EvaluatePosition(index / (float)samples);
                min = Vector3.Min(min, point);
                max = Vector3.Max(max, point);
            }
            float margin = WatchMargin();
            return Rect.MinMaxRect(min.x - margin, min.z - margin, max.x + margin, max.z + margin);
        }

        // Reach of the ground that a build reads around the spline, in meters.
        private float WatchMargin()
        {
            return Mathf.Max(_settings.SearchInward, _settings.SearchOutward) + 30f;
        }

        // The ground near the spline as rectangles of about WatchSegmentLength of spline each, grown by the reach. The
        // samples of one rectangle overlap the next one, so the chain has no gaps.
        private List<Rect> GetWatchRects()
        {
            const int samplesPerRect = 4;
            float length = _spline.CalculateLength(0);
            int rects = Mathf.Max(1, Mathf.CeilToInt(length / WatchSegmentLength));
            int samples = rects * samplesPerRect;
            float margin = WatchMargin();
            var result = new List<Rect>(rects);
            for (int rect = 0; rect < rects; rect++)
            {
                Vector3 min = Vector3.positiveInfinity;
                Vector3 max = Vector3.negativeInfinity;
                for (int index = rect * samplesPerRect; index <= (rect + 1) * samplesPerRect; index++)
                {
                    Vector3 point = _spline.EvaluatePosition(index / (float)samples);
                    min = Vector3.Min(min, point);
                    max = Vector3.Max(max, point);
                }
                result.Add(Rect.MinMaxRect(min.x - margin, min.z - margin, max.x + margin, max.z + margin));
            }
            return result;
        }

        private void Release()
        {
            if (_output) DestroyGenerated(_output);
            foreach (Mesh mesh in _meshes)
            {
                if (mesh) DestroyGenerated(mesh);
            }
            _meshes.Clear();
            _groundRenderers.Clear();
            _groundBlocks.Clear();
            _output = null;
            OutputVersion++;
            ChunkCount = 0;
        }

        private static void DestroyGenerated(UnityEngine.Object target)
        {
            if (Application.isPlaying) Destroy(target);
            else DestroyImmediate(target);
        }
        #endregion
    }
}
