using System.Collections.Generic;
using LoogaSoft.Instancing;
using LoogaSoft.Terrain.Grass;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.Splines;

namespace LoogaSoft.Terrain.Geology
{
    /// <summary>
    /// A steep, coarse rock wall along a cliff-top spline, for sea cliffs, gorge walls and quarry faces. The face
    /// follows a designed profile that can lean over its foot or have a notch at its foot, with buttresses, rock facets
    /// and rock layers. The spline can be open or closed. The mesh is built in the scene and is not saved with it. The
    /// wall has colliders. The kit style covers the same face with rows of rock prefabs instead (see
    /// <see cref="CliffWallKit"/>).
    /// </summary>
    /// <remarks>
    /// The wall covers the steep terrain next to the spline: it finds the terrain face, then places its profile in
    /// front of it. The terrain face must be at least as steep as the rock face, so carve an overhang site with a
    /// steep face (85 to 90 degrees). The rock layers, joints, roughness, ground blend and resolution use the rock
    /// face settings.
    /// </remarks>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [AddComponentMenu("Looga Terrain/Cliff Wall")]
    public sealed class LoogaCliffWall : MonoBehaviour, IGrassGround, IGeneratedMeshSource
    {
        private const float PointSpacing = 0.25f;

        [Header("Spline")]
        [Tooltip("Spline along the top edge of the cliff. It can be open or closed.")]
        [SerializeField] private SplineContainer _spline;

        [Header("Style")]
        [Tooltip("Procedural builds one rock mesh from the profile. Kit sets rows of rock prefabs against the terrain " +
                 "face. The face search, the excluded ranges and the colliders of the rock face settings apply to " +
                 "both styles.")]
        [SerializeField] private CliffWallStyle _style = CliffWallStyle.Procedural;
        [SerializeField] private CliffWallKit _kit = new();

        [Header("Shape")]
        [SerializeField] private CliffWallShape _shape = new();
        [Tooltip("Optional profile values for each knot of the spline. The values blend between knots.")]
        [SerializeField] private List<CliffWallKnot> _knots = new();
        [SerializeField] private RockFaceSettings _settings = new()
        {
            ColumnSpacing = 0.5f, RowSpacing = 0.5f, SearchInward = 6f, SearchOutward = 40f, FaceAngle = 50f,
            MinimumFaceHeight = 3f, ProfileSmoothing = 4f, CapLength = 3f, Clearance = 0.3f, EndFade = 6f,
            MassDepth = 0f, StrataDepth = 1f, HardShare = 0.45f, BlockRelief = 0.5f, RoughnessDepth = 0.2f
        };

        [Header("Surface")]
        [SerializeField] private Material _material;
        [SerializeField] private ShadowCastingMode _shadowCasting = ShadowCastingMode.On;
        [Tooltip("The procedural grass grows on the ground-covered top, with the grass of the land behind the rim.")]
        [SerializeField] private bool _grassOnTop = true;

        [Header("Levels of Detail")]
        [Tooltip("Largest length of one chunk along the wall, in meters. Each chunk culls and changes detail alone.")]
        [SerializeField, Min(4f)] private float _chunkLength = 40f;
        [Tooltip("Screen height of a chunk below which it uses the second and the third level of detail.")]
        [SerializeField] private Vector2 _lodTransitions = new(0.3f, 0.08f);

        // Distance from a cave at which the wall stops, in meters.
        // Cave distances past this reach do not change the wall, in meters.
        private const float CaveReach = 6f;

        // Submesh of the ground-covered top in the generated chunk meshes (RockFaceChunks).
        private const int CoverSubMesh = 1;

        private RockFaceOutput _output;
        // Hidden holder of the instance set of the kit style. Not saved with the scene.
        private GameObject _kitRoot;
        private readonly List<MeshRenderer> _groundRenderers = new();
        // Not kept over a script reload, so a build queued before the reload does not block new builds.
        [System.NonSerialized] private bool _buildQueued;
        // True from a cache load until the next editor update. OnValidate of the load then queues no build.
        [System.NonSerialized] private bool _loadedFromCache;
        // World XZ area that the last build read, for cave shape changes.
        private Rect _buildArea;

        public SplineContainer Spline => _spline;
        public CliffWallShape Shape => _shape;
        public RockFaceSettings Settings => _settings;
        public List<CliffWallKnot> Knots => _knots;
        public CliffWallStyle Style => _style;
        public CliffWallKit Kit => _kit;
        /// <summary>Wall pieces and foot boulders of the last kit build.</summary>
        public int KitPieceCount { get; private set; }
        /// <summary>Share of the terrain face that the last kit build covered, from 0 to 1.</summary>
        public float KitCoverage { get; private set; }
        public int ChunkCount => _output?.ChunkCount ?? 0;
        public Bounds GeneratedBounds => _output?.Bounds ?? default;
        Bounds IGrassGround.GrassGroundBounds => GeneratedBounds;

        #region Built-in
        private void OnEnable()
        {
            UnityEngine.Splines.Spline.Changed += OnSplineChanged;
            LoogaCaveSystem.ShapeChanged += OnCaveShapeChanged;
#if UNITY_EDITOR
            UnityEditor.EditorApplication.update += EditorTick;
#endif
            if (AdoptAfterReload() || LoadPlayerMeshes() || LoadFromCache()) return;
            Rebuild();
        }

        private void OnDisable()
        {
            UnityEngine.Splines.Spline.Changed -= OnSplineChanged;
            LoogaCaveSystem.ShapeChanged -= OnCaveShapeChanged;
#if UNITY_EDITOR
            UnityEditor.EditorApplication.update -= EditorTick;
#endif
            // A script reload keeps the meshes. The next domain takes them back if the code did not change.
            if (GeneratedReloadHandover.Unloading && StoreForReload()) return;
            Release();
        }

        // A terrain can replace its splat maps, for example in a scene save. The chunks on it then read the new maps.
        private void Update()
        {
            _output?.RefreshDestroyedGround();
        }

        // OnValidate must not create objects, so it only queues the build.
        private void OnValidate()
        {
            if (!GeneratedReloadHandover.KeepsOutputOnLoad && !_loadedFromCache) QueueBuild();
        }
        #endregion

        /// <summary>Sets the cliff-top spline and builds again.</summary>
        public void SetSpline(SplineContainer spline)
        {
            _spline = spline;
            QueueBuild();
        }

        /// <summary>Adds the generated renderers of one LOD level to the list: 0 is the most detailed level.</summary>
        public void GetGeneratedRenderers(int level, List<MeshRenderer> result)
        {
            _output?.GetRenderers(level, result);
        }

        /// <summary>The ground-covered top of the finest level of detail, for the procedural grass.</summary>
        void IGrassGround.GetGrassGroundMeshes(List<GrassGroundMesh> result)
        {
            _groundRenderers.Clear();
            GetGeneratedRenderers(0, _groundRenderers);
            foreach (MeshRenderer renderer in _groundRenderers)
            {
                if (!renderer || !renderer.TryGetComponent(out MeshFilter filter)) continue;
                Mesh mesh = filter.sharedMesh;
                if (mesh && mesh.subMeshCount > CoverSubMesh)
                {
                    result.Add(new GrassGroundMesh(mesh, renderer.localToWorldMatrix, CoverSubMesh));
                }
            }
        }

        /// <summary>Builds the wall now, for example after the terrain changed. Main thread.</summary>
        [ContextMenu("Rebuild")]
        public void Rebuild()
        {
            using ProfilerMarker.AutoScope build = LoogaGeologyMarkers.CliffWallBuild.Auto();
            _buildQueued = false;
            Release();
            if (!isActiveAndEnabled || !_spline || _spline.Spline == null || _spline.Spline.Count < 2) return;
            Vector3[] points = SamplePoints(out CliffWallKnot[] shapes);
            if (points.Length < 2) return;

            float reach = _settings.SearchInward + _settings.SearchOutward + _settings.CapLength + 10f;
            if (_style == CliffWallStyle.Kit)
            {
                reach = Mathf.Max(reach, _settings.SearchOutward + _kit.FootDistance.y + 10f);
            }
            _buildArea = PointRect(points, reach);
            using var terrain = new TerrainHeightSampler(_buildArea);
            terrain.WaitForReads();
            if (_style == CliffWallStyle.Kit)
            {
                BuildKit(points, terrain);
                return;
            }
            RockFaceBuilder.Workspace workspace = RockFaceBuilder.RentWorkspace();
#if UNITY_EDITOR
            RockFaceOutput.BeginCacheRecording();
#endif
            try
            {
                // A cave opening cuts the wall, for example a portal in a quarry face.
                var fractureMeshes = new List<Mesh[]>();
                List<RockFaceStrip> strips = CliffWallBuilder.Build(points, shapes, _spline.Spline.Closed,
                    _settings.Copy(), _shape.Copy(), terrain, workspace,
                    point => LoogaCaveSystem.RockDistance(point, CaveReach), fractureMeshes);
                int chunkColumns = Mathf.Max(4, Mathf.RoundToInt(_chunkLength / _settings.ColumnSpacing));
                _output = RockFaceOutput.Create(strips, name, gameObject.layer, _material, _shadowCasting,
                    chunkColumns, _lodTransitions, _ => true);
                foreach (Mesh[] levels in fractureMeshes)
                {
                    _output.AddLevels(levels, _material, _shadowCasting, _settings.Colliders, _lodTransitions);
                }
                if (_grassOnTop) GrassGround.Register(this);
            }
            finally
            {
                RockFaceBuilder.ReturnWorkspace(workspace);
#if UNITY_EDITOR
                if (_output != null && !Application.isPlaying)
                {
                    _output.EndCacheRecording(GeneratedMeshCache.PathOf(this), GeneratedMeshCache.Key(this, _spline),
                        JsonUtility.ToJson(new CacheState { BuildArea = _buildArea }));
                }
                else
                {
                    RockFaceChunks.EndRecording();
                }
#endif
            }
        }

        [System.Serializable]
        private sealed class CacheState
        {
            public Rect BuildArea;
        }

        // Takes the meshes of an earlier editor session. Returns false when the wall must build. The kit style builds.
        private bool LoadFromCache()
        {
#if UNITY_EDITOR
            if (Application.isPlaying || _style == CliffWallStyle.Kit || !_spline || _spline.Spline == null) return false;
            return LoadMeshes(GeneratedMeshCache.PathOf(this), GeneratedMeshCache.Key(this, _spline));
#else
            return false;
#endif
        }

        // Takes the meshes that the player build shipped. Returns false in the editor and when the wall must build.
        private bool LoadPlayerMeshes()
        {
            string path = GeneratedMeshCache.PlayerPath(this);
            if (path == null || _style == CliffWallStyle.Kit) return false;
            using ProfilerMarker.AutoScope load = LoogaGeologyMarkers.ShippedMeshLoad.Auto();
            return LoadMeshes(path, null);
        }

        // Takes the meshes of a cache file. Returns false when there is no file, or when a key is given and the file has
        // another key.
        private bool LoadMeshes(string path, Hash128? key)
        {
            _output = RockFaceOutput.LoadCache(path, key, name, gameObject.layer, _material, _shadowCasting,
                _lodTransitions, out string meta);
            if (_output == null) return false;
            _buildArea = JsonUtility.FromJson<CacheState>(meta).BuildArea;
            _buildQueued = false;
            _loadedFromCache = true;
            if (_grassOnTop) GrassGround.Register(this);
            return true;
        }

#if UNITY_EDITOR
        bool IGeneratedMeshSource.GetCacheFile(out string path, out Hash128 key)
        {
            path = null;
            key = default;
            if (!isActiveAndEnabled || _style == CliffWallStyle.Kit || !_spline || _spline.Spline == null) return false;
            path = GeneratedMeshCache.PathOf(this);
            key = GeneratedMeshCache.Key(this, _spline);
            return true;
        }

        GeneratedMeshBuild IGeneratedMeshSource.BuildForPlayer()
        {
            Rebuild();
            return GeneratedMeshBuild.Built;
        }

        bool IGeneratedMeshSource.PlayerCanBuild => true;
#endif

        // Sets the kit pieces against the terrain face. One instance set holds one entry for each prefab.
        private void BuildKit(Vector3[] points, TerrainHeightSampler terrain)
        {
            CliffWallKit kit = _kit.Copy();
            List<CliffWallKitPiece> pieces = CliffWallKitPiece.Read(kit.Pieces);
            List<CliffWallKitPiece> footPieces = CliffWallKitPiece.Read(kit.FootBoulders);
            var placements = new List<CliffWallKitBuilder.Placement>();
            KitCoverage = CliffWallKitBuilder.Build(points, _spline.Spline.Closed, _settings.Copy(), kit, pieces, footPieces,
                terrain, placements);
            KitPieceCount = placements.Count;
            if (placements.Count == 0) return;
            _kitRoot = new GameObject("Rock " + name + " kit") { hideFlags = HideFlags.HideAndDontSave };
            _kitRoot.layer = gameObject.layer;
            SceneManager.MoveGameObjectToScene(_kitRoot, gameObject.scene);
            var set = _kitRoot.AddComponent<InstanceSet>();
            var byPrefab = new Dictionary<GameObject, List<InstanceSet.Placement>>();
            foreach (CliffWallKitBuilder.Placement placement in placements)
            {
                if (!byPrefab.TryGetValue(placement.Prefab, out List<InstanceSet.Placement> list))
                {
                    list = new List<InstanceSet.Placement>();
                    byPrefab.Add(placement.Prefab, list);
                }
                list.Add(new InstanceSet.Placement
                {
                    Position = placement.Position,
                    EulerAngles = placement.Rotation.eulerAngles,
                    Scale = Vector3.one * placement.Scale
                });
            }
            foreach (KeyValuePair<GameObject, List<InstanceSet.Placement>> pair in byPrefab)
            {
                set.SetEntry(this, pair.Key, pair.Value, _settings.Colliders);
            }
        }

        private string ReloadKey => "LoogaSoft.Terrain.CliffWall." + GetInstanceID();

        // Stores the output for the next domain. Returns false when the wall must build after the reload. The instance
        // set of the kit style needs its owner, so that style builds again.
        private bool StoreForReload()
        {
#if UNITY_EDITOR
            if (_buildQueued || _output == null || _kitRoot) return false;
            _output.StoreForReload(ReloadKey);
            return true;
#else
            return false;
#endif
        }

        // Takes the output of the last domain after a script reload. Returns false when the wall must build.
        private bool AdoptAfterReload()
        {
#if UNITY_EDITOR
            _output = RockFaceOutput.TakeAfterReload(ReloadKey, out _);
            if (_output == null) return false;
            if (_grassOnTop) GrassGround.Register(this);
            return true;
#else
            return false;
#endif
        }

        // The editor update builds a queued wall. A delayed call was not reliable: a wall queued by a cave change did
        // not build again, and kept the rock over a new opening.
        private void QueueBuild()
        {
            _buildQueued = true;
        }

#if UNITY_EDITOR
        private void EditorTick()
        {
            _loadedFromCache = false;
            if (_buildQueued && !Application.isPlaying) Rebuild();
        }
#endif

        private void OnCaveShapeChanged(Bounds bounds)
        {
            var area = Rect.MinMaxRect(bounds.min.x, bounds.min.z, bounds.max.x, bounds.max.z);
            if (_buildArea.Overlaps(area)) QueueBuild();
        }

        private void OnSplineChanged(UnityEngine.Splines.Spline spline, int knotIndex, SplineModification modification)
        {
            if (Application.isPlaying || !_spline) return;
            foreach (UnityEngine.Splines.Spline own in _spline.Splines)
            {
                if (own == spline) QueueBuild();
            }
        }

        // World points about every quarter meter, and the profile values at each.
        private Vector3[] SamplePoints(out CliffWallKnot[] shapes)
        {
            UnityEngine.Splines.Spline spline = _spline.Spline;
            bool closed = spline.Closed;
            float length = _spline.CalculateLength();
            int count = Mathf.Max(8, Mathf.CeilToInt(length / PointSpacing)) + (closed ? 0 : 1);
            var points = new Vector3[count];
            shapes = new CliffWallKnot[count];
            int knots = spline.Count;
            var knotT = new float[knots];
            for (int knot = 0; knot < knots; knot++)
            {
                knotT[knot] = spline.ConvertIndexUnit(knot, PathIndexUnit.Knot, PathIndexUnit.Normalized);
            }
            for (int index = 0; index < count; index++)
            {
                float t = closed ? index / (float)count : index / (float)(count - 1);
                points[index] = _spline.EvaluatePosition(t);
                shapes[index] = ShapeAt(t, knotT, closed);
            }
            return points;
        }

        // The profile values between two knots blend smoothly from one knot to the next.
        private CliffWallKnot ShapeAt(float t, float[] knotT, bool closed)
        {
            int knots = knotT.Length;
            if (_knots.Count == 0) return KnotShape(-1);
            int previous = closed ? knots - 1 : 0;
            for (int knot = 0; knot < knots; knot++)
            {
                if (knotT[knot] <= t) previous = knot;
            }
            int next = closed ? (previous + 1) % knots : Mathf.Min(previous + 1, knots - 1);
            float start = knotT[previous];
            float end = next == 0 && closed ? 1f + knotT[0] : knotT[next];
            float position = t - start;
            if (position < 0f) position += 1f;
            float span = end - start;
            if (span < 0f) span += 1f;
            float share = span > 1e-6f ? Mathf.Clamp01(position / span) : 0f;
            share = share * share * (3f - 2f * share);
            CliffWallKnot a = KnotShape(previous);
            CliffWallKnot b = KnotShape(next);
            return new CliffWallKnot
            {
                Override = true,
                Lean = Mathf.Lerp(a.Lean, b.Lean, share),
                Undercut = Mathf.Lerp(a.Undercut, b.Undercut, share),
                Bulge = Mathf.Lerp(a.Bulge, b.Bulge, share)
            };
        }

        private CliffWallKnot KnotShape(int knot)
        {
            if (knot >= 0 && knot < _knots.Count && _knots[knot].Override) return _knots[knot];
            return new CliffWallKnot
            {
                Override = true, Lean = _shape.Lean, Undercut = _shape.Undercut, Bulge = _shape.Bulge
            };
        }

        private static Rect PointRect(Vector3[] points, float margin)
        {
            Vector2 min = new(float.MaxValue, float.MaxValue);
            Vector2 max = new(float.MinValue, float.MinValue);
            foreach (Vector3 point in points)
            {
                min = Vector2.Min(min, new Vector2(point.x, point.z));
                max = Vector2.Max(max, new Vector2(point.x, point.z));
            }
            return Rect.MinMaxRect(min.x - margin, min.y - margin, max.x + margin, max.y + margin);
        }

        private void Release()
        {
            GrassGround.Unregister(this);
            _output?.Release();
            _output = null;
            KitPieceCount = 0;
            if (!_kitRoot) return;
            if (Application.isPlaying)
            {
                Destroy(_kitRoot);
            }
            else
            {
                DestroyImmediate(_kitRoot);
            }
            _kitRoot = null;
        }
    }
}
