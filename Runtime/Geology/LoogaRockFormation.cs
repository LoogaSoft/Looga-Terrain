using System.Collections.Generic;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Splines;

namespace LoogaSoft.Terrain.Geology
{
    public enum RockFormationMode
    {
        [Tooltip("Walls around the outline and a crag top over the inside.")]
        Crag,
        [Tooltip("Tall rock blocks with tilted tops and fissures between them. Each block has its own walls and top.")]
        Blocks,
        [Tooltip("A layered mesa or butte. The outline is the top edge, and the faces step down and out through " +
                 "horizontal hard and soft layers to the ground.")]
        Mesa
    }

    /// <summary>
    /// A rock formation from a closed spline. Crag mode gives walls from below the terrain up to the formation height,
    /// a rounded edge and a crag top. Block mode gives tall rock blocks. Mesa mode gives a layered mesa or butte whose
    /// outline is the top edge. The terrain gives only the ground at the formation, so it needs no raised terrain. The
    /// mesh is built in the scene and is not saved with it. The formation has colliders unless its settings turn them
    /// off.
    /// </summary>
    /// <remarks>
    /// The rock settings share the rock face rules: resolution, strata and joints, roughness, ground blend and the top.
    /// The face detection and envelope search settings of a rock face are not used, except the cap length, which is the
    /// width of the band where the crag top rises over the rounded edge.
    /// </remarks>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [AddComponentMenu("Looga Terrain/Rock Formation")]
    public sealed class LoogaRockFormation : MonoBehaviour, IGeneratedMeshSource
    {
        [Header("Outline")]
        [Tooltip("Closed spline around the foot of the formation.")]
        [SerializeField] private SplineContainer _spline;

        [Header("Height")]
        [Tooltip("Height of the rim above the ground at the outline, in meters.")]
        [SerializeField, Min(2f)] private float _height = 18f;
        [Tooltip("Optional rim height for each knot of the spline, in meters. The height blends between knots. A value " +
                 "of 0 or less uses the formation height.")]
        [SerializeField] private List<float> _knotHeights = new();

        [Header("Ground")]
        [Tooltip("Optional heights that the rock shape stands on, for example a snapshot before the contact stamps that " +
                 "read this rock. Empty = the terrain heights. The ground blend always reads the terrain.")]
        [SerializeField] private RockFaceGroundSource _groundSource;

        [Header("Shape")]
        [Tooltip("Crag = walls and a crag top. Blocks = tall rock blocks with fissures, for limestone pillars. " +
                 "Mesa = layered cliffs and slopes under a caprock, for desert mesas and buttes.")]
        [SerializeField] private RockFormationMode _mode = RockFormationMode.Blocks;
        [SerializeField] private RockFormationShape _shape = new();
        [Tooltip("Shape of the blocks in block mode.")]
        [SerializeField] private RockBlockShape _blocks = new();
        [Tooltip("Layers of the faces in mesa mode.")]
        [SerializeField] private RockMesaShape _mesa = new();
        [SerializeField] private RockFaceSettings _settings = new()
        {
            FillTop = true, TopSoil = 0f, ColumnSpacing = 0.5f, RowSpacing = 0.5f
        };

        [Header("Edge")]
        [Tooltip("Raised terrain around the foot of the formation.")]
        [SerializeField] private RockFaceApron _apron = new();

        [Header("Surface")]
        [SerializeField] private Material _material;
        [SerializeField] private ShadowCastingMode _shadowCasting = ShadowCastingMode.On;

        [Header("Levels of Detail")]
        [Tooltip("Largest length of one chunk, in meters. Each chunk culls and changes detail alone.")]
        [SerializeField, Min(4f)] private float _chunkLength = 40f;
        [Tooltip("Screen height of a chunk below which it uses the second and the third level of detail.")]
        [SerializeField] private Vector2 _lodTransitions = new(0.3f, 0.08f);

        private RockFaceOutput _output;
        private bool _buildQueued;
        // True from a change of the outline or the settings until the next build.
        private bool _shapeChanged;
        // Hash of the vertex positions and the drawn vertices of the last build. 0 = no build yet.
        [System.NonSerialized] private ulong _shapeHash;
        // True while the rock stands on the terrain because its ground source is not ready.
        [System.NonSerialized] private bool _groundPending;
        // True from a cache load until the next editor update. OnValidate of the load then queues no build.
        [System.NonSerialized] private bool _loadedFromCache;

        /// <summary>
        /// Occurs after a build that follows a change of the outline or the settings. With a ground source it also
        /// occurs after a build whose rock shape changed, because the ground source does not include the stamps that
        /// read the rock. Without a ground source, a build that only reads new terrain heights does not raise it, so a
        /// stamp that reads the rock can rebuild the rock without a loop.
        /// </summary>
        public event System.Action ShapeChanged;

        /// <summary>The heights that the rock shape stands on, or null for the terrain heights.</summary>
        public RockFaceGroundSource GroundSource => _groundSource;

        public SplineContainer Spline => _spline;
        public RockFaceSettings Settings => _settings;
        public RockFormationShape Shape => _shape;
        public RockFormationMode Mode => _mode;
        public RockBlockShape Blocks => _blocks;
        public RockMesaShape Mesa => _mesa;
        public RockFaceApron Apron => _apron;
        public int ChunkCount => _output?.ChunkCount ?? 0;
        public Bounds GeneratedBounds => _output?.Bounds ?? default;

        #region Built-in
        private void OnEnable()
        {
            UnityEngine.Splines.Spline.Changed += OnSplineChanged;
#if UNITY_EDITOR
            UnityEditor.EditorApplication.update += EditorTick;
#endif
            // The first build after a load is no change. The saved terrain already has the result of stamps that read
            // the rock. OnValidate also runs on a load.
            _shapeChanged = false;
            if (AdoptAfterReload() || LoadPlayerMeshes() || LoadFromCache()) return;
            Rebuild();
        }

        private void OnDisable()
        {
            UnityEngine.Splines.Spline.Changed -= OnSplineChanged;
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
            _settings.FillTop = true;
            if (GeneratedReloadHandover.KeepsOutputOnLoad || _loadedFromCache) return;
            _shapeChanged = true;
            QueueBuild();
        }
        #endregion

        /// <summary>Sets the outline spline and builds again.</summary>
        public void SetSpline(SplineContainer spline)
        {
            _spline = spline;
            _shapeChanged = true;
            QueueBuild();
        }

        /// <summary>Adds the generated renderers of one LOD level to the list: 0 is the most detailed level.</summary>
        public void GetGeneratedRenderers(int level, List<MeshRenderer> result)
        {
            _output?.GetRenderers(level, result);
        }

        /// <summary>Builds the formation now. Main thread.</summary>
        [ContextMenu("Rebuild")]
        public void Rebuild()
        {
            using ProfilerMarker.AutoScope build = LoogaGeologyMarkers.RockFormationBuild.Auto();
            ulong previous = _shapeHash;
            Build();
            // The shape on a ground source does not depend on the stamps that read the rock, so a changed shape after a
            // ground change runs those stamps once more and the next build gives the same shape.
            bool groundShape = _groundSource && previous != 0 && _shapeHash != previous;
            if (!_shapeChanged && !groundShape) return;
            _shapeChanged = false;
            ShapeChanged?.Invoke();
        }

        private void Build()
        {
            _buildQueued = false;
            Release();
            // A formation that a stamp reads must exist before the ground source is ready, so it stands on the terrain
            // until then.
            _groundPending = _groundSource && !_groundSource.IsReady;
            if (!isActiveAndEnabled || !_spline || _spline.Spline == null || !_spline.Spline.Closed) return;
            Vector3[] outline = SampleOutline(out float[] heights);
            if (outline.Length < 3) return;

            // A mesa spreads out past its top edge, so it reads the ground farther out.
            Rect region = OutlineRect(outline, _mode == RockFormationMode.Mesa ? MesaReach(heights) : 10f);
            using var terrain = new TerrainHeightSampler(region);
            terrain.WaitForReads();
            using IRockFaceGroundReader shapeGround = OpenGround(region);
            RockFaceBuilder.Workspace workspace = RockFaceBuilder.RentWorkspace();
#if UNITY_EDITOR
            RockFaceOutput.BeginCacheRecording();
#endif
            try
            {
                RockFaceSettings settings = _settings.Copy();
                settings.FillTop = true;
                if (_mode == RockFormationMode.Blocks)
                {
                    List<RockFaceStrip> blockStrips = RockBlockBuilder.Build(outline, heights, settings, _shape,
                        _blocks, terrain, workspace, shapeGround);
                    _shapeHash = ShapeHash(blockStrips);
                    _output = RockFaceOutput.CreateGrouped(blockStrips, name, gameObject.layer, _material,
                        _shadowCasting, _chunkLength, _lodTransitions, settings.Colliders);
                    return;
                }
                List<RockFaceStrip> strips = _mode == RockFormationMode.Mesa
                    ? RockMesaBuilder.Build(outline, heights, settings, _shape, _mesa, terrain, workspace, shapeGround)
                    : RockFormationBuilder.Build(outline, heights, settings, _shape, terrain, workspace, shapeGround);
                _shapeHash = ShapeHash(strips);
                int chunkColumns = Mathf.Max(4, Mathf.RoundToInt(_chunkLength / settings.ColumnSpacing));
                _output = RockFaceOutput.Create(strips, name, gameObject.layer, _material, _shadowCasting,
                    chunkColumns, _lodTransitions, _ => settings.Colliders);
            }
            finally
            {
                RockFaceBuilder.ReturnWorkspace(workspace);
                if (_groundSource) ReleaseGroundLater(_groundSource);
#if UNITY_EDITOR
                WriteCache();
#endif
            }
        }

#if UNITY_EDITOR
        // A rock on the terrain while its ground source waits stays out of the cache.
        private void WriteCache()
        {
            if (_output == null || _groundPending || Application.isPlaying)
            {
                RockFaceChunks.EndRecording();
                return;
            }
            _output.EndCacheRecording(GeneratedMeshCache.PathOf(this), GeneratedMeshCache.Key(this, _spline),
                _shapeHash.ToString());
        }
#endif

        // Takes the meshes of an earlier editor session. Returns false when the rock must build.
        private bool LoadFromCache()
        {
#if UNITY_EDITOR
            if (Application.isPlaying || !_spline || _spline.Spline == null) return false;
            return LoadMeshes(GeneratedMeshCache.PathOf(this), GeneratedMeshCache.Key(this, _spline));
#else
            return false;
#endif
        }

        // Takes the meshes that the player build shipped. Returns false in the editor and when the rock must build.
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
            _output = RockFaceOutput.LoadCache(path, key, name, gameObject.layer, _material, _shadowCasting,
                _lodTransitions, out string meta);
            if (_output == null) return false;
            _shapeHash = ulong.Parse(meta);
            _buildQueued = false;
            _shapeChanged = false;
            _loadedFromCache = true;
            return true;
        }

#if UNITY_EDITOR
        bool IGeneratedMeshSource.GetCacheFile(out string path, out Hash128 key)
        {
            path = null;
            key = default;
            if (!isActiveAndEnabled || !_spline || _spline.Spline == null) return false;
            path = GeneratedMeshCache.PathOf(this);
            key = GeneratedMeshCache.Key(this, _spline);
            return true;
        }

        // A formation on a stamp snapshot that waits builds later in the editor, when the snapshot is ready. A build on the
        // terrain would write no cache file.
        GeneratedMeshBuild IGeneratedMeshSource.BuildForPlayer()
        {
            if (_groundSource && !_groundSource.IsReady)
            {
                QueueBuild();
                return GeneratedMeshBuild.Later;
            }
            Rebuild();
            return GeneratedMeshBuild.Built;
        }

        bool IGeneratedMeshSource.PlayerCanBuild => !_groundSource || _groundSource.ReadableInPlayer;
#endif

        // The heights of the ground source, or null. Without heights from the source, for example while its snapshot
        // waits, or after a failed read, the shape reads the terrain.
        private IRockFaceGroundReader OpenGround(Rect region)
        {
            if (!_groundSource) return null;
            try
            {
                return _groundSource.Open(region);
            }
            catch (System.InvalidOperationException exception)
            {
                Debug.LogWarning(name + ": the ground source has no heights (" + exception.Message +
                                 "). The rock stands on the terrain heights.", this);
                return null;
            }
        }

        // A ground source keeps the heights that it read for the next builds. The formations that build in the same editor
        // update share them, and they are released after it. While rock faces build, the release does not run, because
        // it also releases the heights that their worker threads read. The rock face queue releases them when it is
        // empty.
        private static void ReleaseGroundLater(RockFaceGroundSource source)
        {
#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                UnityEditor.EditorApplication.delayCall += () =>
                {
                    if (source && LoogaRockFace.PendingCount == 0) source.EndBuilds();
                };
                return;
            }
#endif
            if (LoogaRockFace.PendingCount == 0) source.EndBuilds();
        }

        // FNV-1a hash of the vertex positions and the drawn vertices. The ground blend values are not part of the shape.
        private static ulong ShapeHash(List<RockFaceStrip> strips)
        {
            ulong hash = 14695981039346656037UL;
            foreach (RockFaceStrip strip in strips)
            {
                Vector3[] positions = strip.Positions;
                bool[] active = strip.Active;
                for (int index = 0; index < strip.VertexCount; index++)
                {
                    Vector3 position = positions[index];
                    hash = (hash ^ (uint)position.x.GetHashCode()) * 1099511628211UL;
                    hash = (hash ^ (uint)position.y.GetHashCode()) * 1099511628211UL;
                    hash = (hash ^ (uint)position.z.GetHashCode()) * 1099511628211UL;
                    hash = (hash ^ (active[index] ? 1UL : 0UL)) * 1099511628211UL;
                }
            }
            return hash == 0 ? 1 : hash;
        }

        private string ReloadKey => "LoogaSoft.Terrain.RockFormation." + GetInstanceID();

        // Stores the output for the next domain. Returns false when the rock must build after the reload.
        private bool StoreForReload()
        {
#if UNITY_EDITOR
            if (_buildQueued || _groundPending || _output == null) return false;
            _output.StoreForReload(ReloadKey, _shapeHash);
            return true;
#else
            return false;
#endif
        }

        // Takes the output of the last domain after a script reload. Returns false when the rock must build.
        private bool AdoptAfterReload()
        {
#if UNITY_EDITOR
            _output = RockFaceOutput.TakeAfterReload(ReloadKey, out _shapeHash);
            return _output != null;
#else
            return false;
#endif
        }

#if UNITY_EDITOR
        private void EditorTick()
        {
            _loadedFromCache = false;
            if (_groundPending && !Application.isPlaying && _groundSource && _groundSource.IsReady) QueueBuild();
        }
#endif

        private void QueueBuild()
        {
            if (_buildQueued) return;
            _buildQueued = true;
#if UNITY_EDITOR
            UnityEditor.EditorApplication.delayCall += () =>
            {
                if (this && _buildQueued) Rebuild();
            };
#endif
        }

        private void OnSplineChanged(UnityEngine.Splines.Spline spline, int knotIndex, SplineModification modification)
        {
            if (Application.isPlaying || !_spline) return;
            foreach (UnityEngine.Splines.Spline own in _spline.Splines)
            {
                if (own != spline) continue;
                _shapeChanged = true;
                QueueBuild();
            }
        }

        // World points about every half meter, and the rim height at each.
        private Vector3[] SampleOutline(out float[] heights)
        {
            UnityEngine.Splines.Spline spline = _spline.Spline;
            float length = _spline.CalculateLength();
            int count = Mathf.Max(32, Mathf.CeilToInt(length / 0.5f));
            var points = new Vector3[count];
            heights = new float[count];
            int knots = spline.Count;
            var knotT = new float[knots];
            for (int knot = 0; knot < knots; knot++)
            {
                knotT[knot] = spline.ConvertIndexUnit(knot, PathIndexUnit.Knot, PathIndexUnit.Normalized);
            }
            for (int index = 0; index < count; index++)
            {
                float t = index / (float)count;
                points[index] = _spline.EvaluatePosition(t);
                heights[index] = HeightAt(t, knotT);
            }
            return points;
        }

        // The rim height between two knots blends smoothly from one knot height to the next.
        private float HeightAt(float t, float[] knotT)
        {
            int knots = knotT.Length;
            if (_knotHeights.Count == 0 || knots == 0) return _height;
            int previous = knots - 1;
            for (int knot = 0; knot < knots; knot++)
            {
                if (knotT[knot] <= t) previous = knot;
            }
            int next = (previous + 1) % knots;
            float start = knotT[previous];
            float end = next == 0 ? 1f + knotT[0] : knotT[next];
            float span = end - start;
            if (span < 0f) span += 1f;
            float position = t - start;
            if (position < 0f) position += 1f;
            float share = span > 1e-6f ? Mathf.Clamp01(position / span) : 0f;
            share = share * share * (3f - 2f * share);
            return Mathf.Lerp(KnotHeight(previous), KnotHeight(next), share);
        }

        private float KnotHeight(int knot)
        {
            return knot < _knotHeights.Count && _knotHeights[knot] > 0f ? _knotHeights[knot] : _height;
        }

        // Largest outward reach of a mesa face from its top edge, with a margin for the foot reads.
        private float MesaReach(float[] heights)
        {
            float height = _height;
            foreach (float value in heights)
            {
                height = Mathf.Max(height, value);
            }
            float slopeAngle = Mathf.Clamp(_mesa.SlopeAngle, 20f, 70f) * Mathf.Deg2Rad;
            float cliffAngle = Mathf.Clamp(_mesa.CliffAngle, 70f, 90f) * Mathf.Deg2Rad;
            float slope = height * _mesa.SlopeZone + _shape.FootDepth + 40f;
            float reach = slope / Mathf.Tan(slopeAngle) + height / Mathf.Max(0.05f, Mathf.Tan(cliffAngle));
            int benches = Mathf.CeilToInt(height / Mathf.Max(0.5f, Mathf.Min(_mesa.LayerThickness.x,
                _mesa.LayerThickness.y)) * _mesa.BenchShare);
            return reach + benches * _mesa.BenchWidth + _mesa.LedgeVariation + 10f;
        }

        private static Rect OutlineRect(Vector3[] points, float margin)
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
            _output?.Release();
            _output = null;
        }
    }
}
