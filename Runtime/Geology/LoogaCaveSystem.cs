using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Profiling;
using LoogaSoft.Terrain.Surface;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Splines;

namespace LoogaSoft.Terrain.Geology
{
    /// <summary>
    /// A cave in the rock under the terrain. The child <see cref="LoogaCavePassage"/>, <see cref="LoogaCaveChamber"/>
    /// and <see cref="LoogaCaveShaft"/> components give the empty space. The system builds the rock surface around it as chunked meshes with colliders, and cuts terrain holes
    /// where the cave reaches the terrain surface, for example a portal in a quarry wall. In a hole the mesh draws the
    /// whole rock surface, also the rock above the opening. The meshes are built in the scene and are not saved with
    /// it. The holes are written to the terrain data. The mesh draws the terrain surface in a hole with the Looga
    /// terrain surface material, so the edge of the hole does not show.
    /// </summary>
    /// <remarks>
    /// The system builds again when a passage changes or when the terrain heights change in its area.
    /// </remarks>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [AddComponentMenu("Looga Terrain/Cave System")]
    public sealed class LoogaCaveSystem : MonoBehaviour, IGeneratedMeshSource
    {
        // Cells along each axis of one mesh chunk.
        private const int ChunkCells = 64;
        // Spline length of one straight passage segment, in meters.
        private const float SegmentSpacing = 2f;
        // Space around the passages that the mesh covers, in meters.
        private const float BoundsMargin = 3f;
        // A terrain texel becomes a hole where its surface is this far inside the cave, in meters. Surface that only
        // touches the cave, for example the bumpy floor in front of a portal, keeps its terrain.
        private const float HoleInset = 0.2f;
        // The holes spread over steep terrain within this distance of the passage outline, in meters, so the mesh
        // draws the whole cut face above an opening. Rise of a steep texel, as height per texel width (50 degrees).
        private const float FaceSpreadMargin = 0.5f;
        private const float SteepRise = 1.2f;
        // Rings of level texels holed in front of the foot of a holed face, and the highest such texel above the foot,
        // in meters.
        private const int FootRings = 2;
        private const float FootRise = 1f;
        // Mesh cells around a hole that also draw the terrain surface. The texel squares of the hole and the mesh cells
        // do not line up, so the overlap closes the gaps. The terrain surface material looks the same as the terrain.
        private const int SkirtCells = 2;
        // Wait after the last change before an edit mode build, in seconds.
        private const double BuildDelay = 0.3;
        // Most chunks of one build. More means a wrong scale or a stray spline knot.
        private const int MaximumChunks = 1024;
        // Mesh cells in one cell of the daylight grid along each axis.
        private const int LightCells = 4;
        // Sky distances past this many daylight reaches are not followed. The cave is fully dark there.
        private const float LightSearchReaches = 6f;
        // The terrain surface goes on into an opening over the cave surfaces with this sky distance, in meters. It is
        // complete up to the first value and ends along a ragged edge at the second.
        private const float LipFullDepth = 0.75f;
        private const float LipEndDepth = 2.5f;
        // Cave air under a terrain hole is open to the sky down to this depth below the terrain height, in meters. A
        // sinkhole or spring mouth is daylight to about this depth, so its walls show the terrain ground there.
        private const float HoleOpenDepth = 6f;
        // A copy triangle steeper than this (normal y) is a face. A face within one to two cells of a rock mesh vertex
        // shows the cave rock and not the terrain copy. Cell size in meters: the passage walls just inside a mouth lie
        // up to about 3 m from the rock frame outside.
        private const float CoverFaceFacing = 0.5f;
        private const float RockCellSize = 2f;
        // A lip copy face is part of the rock frame of an opening when the terrain lies this far above it, in meters.
        private const float FrameDepth = 2f;
        // Highest bank of a passage end in front of a face above the passage floor, in meters.
        private const float BankHeight = 1.5f;
        // Seconds between the checks for changed rock meshes.
        private const float RockCoverInterval = 1f;
        // The rock shaders read the cave shade from UV channel 3 only where this value is 1.
        private static readonly int CaveShadeId = Shader.PropertyToID("_LoogaCaveShade");

        // Terrain texels that this system made holes, for each terrain data. A later build gives back the texels it
        // no longer needs.
        [Serializable]
        private sealed class HoleRecord
        {
            public TerrainData Data;
            public int[] Texels;
        }

        [Header("Mesh")]
        [Tooltip("Size of one mesh cell in meters. Smaller gives sharper edges and more triangles.")]
        [SerializeField, Range(0.25f, 2f)] private float _cellSize = 0.5f;

        [Header("Rock")]
        [Tooltip("Size of the large rock bumps of natural passages and shafts, in meters. Each passage sets their " +
                 "height.")]
        [SerializeField, Min(0.5f)] private float _bumpSize = 6f;
        [Tooltip("Height of the small bumps on all rock surfaces in meters. 0 gives flat, sawn faces.")]
        [SerializeField, Min(0f)] private float _roughness = 0.06f;
        [Tooltip("Size of the bumps on the rock surface in meters.")]
        [SerializeField, Min(0.05f)] private float _roughnessScale = 1.2f;
        [Tooltip("Height of one rock layer in meters, for the layer colors of the rock material.")]
        [SerializeField, Min(0.1f)] private float _layerSpacing = 2.5f;

        [Header("Light")]
        [Tooltip("Daylight in the cave falls to about a third over this distance from an opening, in meters. The " +
                 "rock material darkens its sky and bounce light by it.")]
        [SerializeField, Min(1f)] private float _daylightReach = 15f;
        [Tooltip("Part of the sky and bounce light that the deepest cave loses. 1 gives black rock without lamps.")]
        [SerializeField, Range(0f, 1f)] private float _darkness = 0.95f;

        [Header("Surface")]
        [Tooltip("Rock material. Empty uses a default Looga/Rock Face material.")]
        [SerializeField] private Material _material;
        [SerializeField] private ShadowCastingMode _shadowCasting = ShadowCastingMode.On;
        [Tooltip("The cave floors show the terrain ground at this point, for example the floor of the quarry in front " +
                 "of the openings. The rock material blends the ground in (Blend Into Terrain Ground), so the cave " +
                 "darkness stays. The point must lie on the terrain under the cave. Empty keeps rock floors.")]
        [SerializeField] private Transform _floorGround;
        [Tooltip("Normal Y of the floor where the ground starts and where it covers the rock fully.")]
        [SerializeField] private Vector2 _floorSlope = new(0.55f, 0.8f);

        [Header("Terrain")]
        [Tooltip("Cut terrain holes where the cave reaches the terrain surface.")]
        [SerializeField] private bool _cutTerrain = true;
        [SerializeField, HideInInspector] private List<HoleRecord> _holes = new();

        // Enabled systems. Cliff walls and rock faces ask them where the cave cuts the rock.
        internal static readonly List<LoogaCaveSystem> Enabled = new();

        /// <summary>Raised with the world bounds of a change of the cave shape, so walls along the rock can rebuild.</summary>
        public static event Action<Bounds> ShapeChanged;

        private CaveSegment[] _segments = Array.Empty<CaveSegment>();
        private Bounds _segmentBounds;
        // Not kept over a script reload: a wall can enable before the system after a reload and build without the cave,
        // so the system must report its shape again.
        [NonSerialized] private ulong _shapeHash;
        private GameObject _root;
        private readonly List<Mesh> _meshes = new();
        // The renderers that draw the terrain surface, with their terrain and its ground block.
        private readonly List<(UnityEngine.Terrain Terrain, MeshRenderer Renderer)> _groundRenderers = new();
        private readonly Dictionary<UnityEngine.Terrain, MaterialPropertyBlock> _groundBlocks = new();
        private MaterialPropertyBlock _shadeBlock;
        // The terrain copy triangles of each chunk, so the faces inside rock meshes can be left out again when the
        // rock meshes change.
        private readonly List<ChunkCopy> _copies = new();
        private readonly HashSet<long> _rockCells = new();
        private readonly List<MeshRenderer> _rockRenderers = new();
        private int _rockSignature;
        private float _nextRockCheck;
        // The terrain copies stay in the last domain after a script reload. A changed rock mesh then builds the cave.
        [NonSerialized] private bool _copiesLost;
        // True from OnEnable until the next editor update.
        [NonSerialized] private bool _enabledThisLoad;
        [NonSerialized] private bool _holdShapeEvent;
        [NonSerialized] private Bounds? _shapeEvent;
        private readonly List<LoogaCavePassage> _passages = new();
        private readonly List<LoogaCaveShaft> _shafts = new();
        private readonly List<LoogaCaveChamber> _chambers = new();
        private readonly List<LoogaCaveEntrance> _entrances = new();
        // The holes of the last build with the entrance holes. The entrance meshes keep their ground near them.
        private Dictionary<TerrainData, HashSet<int>> _appliedHoles = new();
        private bool _buildQueued;
        private double _buildTime;
        private int _transformHash;
        private Material _defaultMaterial;

        public float CellSize => _cellSize;
        internal CaveSegment[] Segments => _segments;
        internal float BumpSize => _bumpSize;
        internal float Roughness => _roughness;
        internal float RoughnessScale => _roughnessScale;
        internal Material RockMaterial => SurfaceMaterial();
        /// <summary>The terrain point whose ground the cave floors show. Null keeps rock floors.</summary>
        internal Transform FloorGround => _floorGround;

        /// <summary>Ground weight of a cave surface with this normal Y: 0 on walls, 1 on the floor.</summary>
        internal float FloorWeight(float normalY)
        {
            return _floorGround ? Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(_floorSlope.x, _floorSlope.y, normalY)) : 0f;
        }
        internal IReadOnlyList<Mesh> ChunkMeshes => _meshes;
        internal IReadOnlyDictionary<TerrainData, HashSet<int>> AppliedHoles => _appliedHoles;
        public int ChunkCount => _meshes.Count;
        /// <summary>World bounds of the passages, with their margin.</summary>
        public Bounds ShapeBounds => _segmentBounds;
        /// <summary>Terrain texels that the system cut, over all terrains.</summary>
        public int HoleCount
        {
            get
            {
                int count = 0;
                foreach (HoleRecord record in _holes) count += record.Texels?.Length ?? 0;
                return count;
            }
        }

        #region Built-in
        private void OnEnable()
        {
            Enabled.Add(this);
            UnityEngine.Splines.Spline.Changed += OnSplineChanged;
            TerrainCallbacks.heightmapChanged += OnHeightmapChanged;
            LoogaTerrainSurfaceBinding.SurfaceChanged += RefreshGround;
#if UNITY_EDITOR
            UnityEditor.EditorApplication.update += EditorTick;
#endif
            bool adopted = AdoptAfterReload();
            // The walls that the shape event builds again only need it when the cave builds.
            _holdShapeEvent = true;
            _shapeEvent = null;
            UpdateShape();
            _holdShapeEvent = false;
            // The entrances always take their objects back, so a build can release them.
            bool entrances = AdoptEntrances();
            _enabledThisLoad = !Application.isPlaying;
            _transformHash = PassageTransformHash();
#if UNITY_EDITOR
            if (adopted && entrances || !adopted && LoadFromCache()) return;
#else
            // A player has no reload handover and no editor cache. It loads the shipped meshes or builds at load.
            if (adopted && entrances || LoadPlayerMeshes()) return;
#endif
            if (_shapeEvent.HasValue) ShapeChanged?.Invoke(_shapeEvent.Value);
            Rebuild();
        }

        private void OnDisable()
        {
            Enabled.Remove(this);
            UnityEngine.Splines.Spline.Changed -= OnSplineChanged;
            TerrainCallbacks.heightmapChanged -= OnHeightmapChanged;
            LoogaTerrainSurfaceBinding.SurfaceChanged -= RefreshGround;
#if UNITY_EDITOR
            UnityEditor.EditorApplication.update -= EditorTick;
            // A script reload keeps the meshes. The next domain takes them back if the code did not change.
            if (GeneratedReloadHandover.Unloading && StoreForReload()) return;
            ClearHolesLater();
#endif
            Release();
            Bounds old = _segmentBounds;
            _segments = Array.Empty<CaveSegment>();
            _shapeHash = 0;
            if (old.size != Vector3.zero) ShapeChanged?.Invoke(old);
        }

        private void OnDestroy()
        {
            if (_defaultMaterial) DestroyGenerated(_defaultMaterial);
        }

        // OnValidate must not create objects, so it only queues the build.
        private void OnValidate()
        {
            QueueBuild();
        }
        #endregion

        /// <summary>Queues a build after a short wait. Edit mode. Play mode builds only on enable.</summary>
        public void QueueBuild()
        {
            // The passages and the entrances also queue when a script reload disables and enables them.
            if (GeneratedReloadHandover.Unloading || GeneratedReloadHandover.KeepsOutputOnLoad) return;
            // The system builds in its OnEnable, so the passages and the entrances that enable after it add nothing.
            if (_enabledThisLoad) return;
            _buildQueued = true;
            _buildTime = Time.realtimeSinceStartupAsDouble + BuildDelay;
        }

        /// <summary>
        /// True when a point is inside the cave of an enabled system, or within <paramref name="margin"/> meters of
        /// it. Walls along the rock skip such points. Safe on worker threads while no build runs.
        /// </summary>
        public static bool CutsRock(Vector3 point, float margin)
        {
            foreach (LoogaCaveSystem system in Enabled)
            {
                if (system._segments.Length == 0) continue;
                Bounds bounds = system._segmentBounds;
                bounds.Expand(margin * 2f);
                if (!bounds.Contains(point)) continue;
                if (CaveField.Evaluate(system._segments, point, system._bumpSize, 0f, 1f) < margin) return true;
            }
            return false;
        }

        /// <summary>
        /// Distance from a point to the cave of the enabled systems in meters, negative inside the cave. Points more
        /// than <paramref name="reach"/> meters outside the cave bounds get float.MaxValue. Walls along the rock fit
        /// their rock around the openings with it. Safe on worker threads while no build runs.
        /// </summary>
        public static float RockDistance(Vector3 point, float reach)
        {
            float nearest = float.MaxValue;
            foreach (LoogaCaveSystem system in Enabled)
            {
                if (system._segments.Length == 0) continue;
                Bounds bounds = system._segmentBounds;
                bounds.Expand(reach * 2f);
                if (!bounds.Contains(point)) continue;
                nearest = Mathf.Min(nearest, CaveField.Evaluate(system._segments, point, system._bumpSize, 0f, 1f));
            }
            return nearest;
        }

        /// <summary>Builds the cave now. Main thread.</summary>
        [ContextMenu("Rebuild")]
        public void Rebuild()
        {
            using ProfilerMarker.AutoScope build = LoogaGeologyMarkers.CaveBuild.Auto();
            _buildQueued = false;
            Release();
            UpdateShape();
            if (!isActiveAndEnabled) return;
            if (_segments.Length == 0)
            {
                ApplyHoles(new Dictionary<TerrainData, HashSet<int>>());
                return;
            }
            Bounds bounds = _segmentBounds;
            // The heights cover the whole chunk grid with its apron columns.
            float chunkLength = ChunkCells * _cellSize;
            var region = Rect.MinMaxRect(bounds.min.x - 2f * _cellSize, bounds.min.z - 2f * _cellSize,
                bounds.max.x + chunkLength + 3f * _cellSize, bounds.max.z + chunkLength + 3f * _cellSize);
            using var terrain = new TerrainHeightSampler(region);
            terrain.WaitForReads();
            float holeTop = float.MinValue;
            var holeTerrains = new List<(UnityEngine.Terrain, HashSet<int>)>();
            var holes = new Dictionary<TerrainData, HashSet<int>>();
            if (_cutTerrain) holes = FindHoles(terrain, out holeTop, out holeTerrains);
            _appliedHoles = WithEntranceHoles(holes);
            ApplyHoles(_appliedHoles);
            // In a hole the mesh draws the terrain surface too, so the mesh reaches up to the top of the cut.
            float top = Mathf.Max(bounds.max.y, holeTop + 1f);
            BuildMesh(terrain, new Vector3(bounds.min.x, bounds.min.y, bounds.min.z),
                new Vector3(bounds.max.x, top, bounds.max.z), holeTerrains);
            BuildEntrances();
#if UNITY_EDITOR
            WriteCache();
#endif
        }

        // The cave holes with the holes of the entrance boxes. The cave mesh does not draw the ground in the entrance
        // holes: each entrance mesh draws its own.
        private Dictionary<TerrainData, HashSet<int>> WithEntranceHoles(Dictionary<TerrainData, HashSet<int>> holes)
        {
            if (!_cutTerrain || _entrances.Count == 0) return holes;
            var all = new Dictionary<TerrainData, HashSet<int>>();
            foreach (KeyValuePair<TerrainData, HashSet<int>> entry in holes)
            {
                all.Add(entry.Key, new HashSet<int>(entry.Value));
            }
            foreach (LoogaCaveEntrance entrance in _entrances)
            {
                entrance.CollectHoles(all, this);
            }
            return all;
        }

        // Builds the entrance meshes after the cave mesh: they take their cave shade from it.
        private void BuildEntrances()
        {
            foreach (LoogaCaveEntrance entrance in _entrances)
            {
                if (entrance) entrance.Build(this);
            }
        }

        #region Shape
        // Collects the passage segments, and tells the walls along the rock when the shape changed.
        private string ReloadKey => "LoogaSoft.Terrain.CaveSystem." + GetInstanceID();

        // Stores the output for the next domain. Returns false when the cave must build after the reload.
        private bool StoreForReload()
        {
#if UNITY_EDITOR
            if (_buildQueued || !_root) return false;
            var meshes = new int[_meshes.Count];
            for (int index = 0; index < meshes.Length; index++)
            {
                meshes[index] = _meshes[index] ? _meshes[index].GetInstanceID() : 0;
            }
            var renderers = new int[_groundRenderers.Count];
            var terrains = new int[_groundRenderers.Count];
            for (int index = 0; index < renderers.Length; index++)
            {
                (UnityEngine.Terrain terrain, MeshRenderer renderer) = _groundRenderers[index];
                renderers[index] = renderer ? renderer.GetInstanceID() : 0;
                terrains[index] = terrain ? terrain.GetInstanceID() : 0;
            }
            GeneratedReloadHandover.Store(ReloadKey, new ReloadState
            {
                Root = _root.GetInstanceID(),
                Meshes = meshes,
                Renderers = renderers,
                Terrains = terrains,
                RockSignature = _rockSignature,
                TransformHash = _transformHash,
                ShapeHash = unchecked((long)_shapeHash)
            });
            return true;
#else
            return false;
#endif
        }

        // Takes the output of the last domain after a script reload. Returns false when the cave must build.
        private bool AdoptAfterReload()
        {
#if UNITY_EDITOR
            if (!GeneratedReloadHandover.TryTake(ReloadKey, out ReloadState state)) return false;
            var root = GeneratedReloadHandover.Find<GameObject>(state.Root);
            if (!root || !GeneratedReloadHandover.CanAdopt || state.Meshes == null || state.Renderers == null ||
                state.Terrains == null)
            {
                if (root) DestroyGenerated(root);
                foreach (int id in state.Meshes ?? Array.Empty<int>())
                {
                    var mesh = GeneratedReloadHandover.Find<Mesh>(id);
                    if (mesh) DestroyGenerated(mesh);
                }
                return false;
            }

            _root = root;
            _meshes.Clear();
            foreach (int id in state.Meshes)
            {
                var mesh = GeneratedReloadHandover.Find<Mesh>(id);
                if (mesh) _meshes.Add(mesh);
            }
            _groundRenderers.Clear();
            _groundBlocks.Clear();
            for (int index = 0; index < state.Renderers.Length; index++)
            {
                var renderer = GeneratedReloadHandover.Find<MeshRenderer>(state.Renderers[index]);
                var terrain = GeneratedReloadHandover.Find<UnityEngine.Terrain>(state.Terrains[index]);
                if (!renderer || !terrain) continue;
                _groundRenderers.Add((terrain, renderer));
                BindGround(terrain, renderer);
            }
            _rockSignature = state.RockSignature;
            _transformHash = state.TransformHash;
            _shapeHash = unchecked((ulong)state.ShapeHash);
            _buildQueued = false;
            _copiesLost = true;
            return true;
#else
            return false;
#endif
        }

#if UNITY_EDITOR
        private Hash128 CacheKey()
        {
            var extra = new Hash128();
            extra.Append(_shapeHash.ToString());
            foreach (LoogaCaveEntrance entrance in _entrances)
            {
                if (entrance) extra.Append(GeneratedMeshCache.StateText(entrance));
            }
            return GeneratedMeshCache.Key(this, GeneratedMeshCache.BoundsRegion(_segmentBounds), extra);
        }

        // Writes the chunks and the entrances. The terrain copies are not part of the cache.
        private void WriteCache()
        {
            if (Application.isPlaying || !_root) return;
            var chunks = new List<(string Name, string Terrain, GeneratedMeshCache.RawMesh Mesh)>();
            foreach (Transform chunk in _root.transform)
            {
                string terrain = string.Empty;
                var renderer = chunk.GetComponent<MeshRenderer>();
                foreach ((UnityEngine.Terrain chunkTerrain, MeshRenderer chunkRenderer) in _groundRenderers)
                {
                    if (chunkRenderer == renderer && chunkTerrain) terrain = chunkTerrain.name;
                }
                chunks.Add((chunk.name, terrain, GeneratedMeshCache.RawMesh.From(chunk.GetComponent<MeshFilter>().sharedMesh)));
            }
            var entrances = new List<LoogaCaveEntrance.CacheEntry>();
            foreach (LoogaCaveEntrance entrance in _entrances)
            {
                entrances.Add(entrance ? entrance.CacheRecord() : new LoogaCaveEntrance.CacheEntry());
            }
            int rockSignature = RockSignature();
            _rockSignature = rockSignature;
            ulong shapeHash = _shapeHash;
            GeneratedMeshCache.WriteFile(GeneratedMeshCache.PathOf(this), CacheKey(), writer =>
            {
                writer.Write(rockSignature);
                writer.Write(shapeHash);
                writer.Write(chunks.Count);
                foreach ((string name, string terrain, GeneratedMeshCache.RawMesh mesh) in chunks)
                {
                    writer.Write(name);
                    writer.Write(terrain);
                    mesh.Write(writer);
                }
                writer.Write(entrances.Count);
                foreach (LoogaCaveEntrance.CacheEntry entrance in entrances)
                {
                    entrance.Write(writer);
                }
            });
        }

        // Takes the meshes of an earlier editor session. Returns false when the cave must build.
        private bool LoadFromCache()
        {
            if (Application.isPlaying || _segments.Length == 0) return false;
            return LoadMeshes(GeneratedMeshCache.PathOf(this), CacheKey());
        }

        bool IGeneratedMeshSource.GetCacheFile(out string path, out Hash128 key)
        {
            path = null;
            key = default;
            if (!isActiveAndEnabled || _segments.Length == 0) return false;
            path = GeneratedMeshCache.PathOf(this);
            key = CacheKey();
            return true;
        }

        // A cave build writes the terrain holes, so the player build step does not build a cave. It ships the cache file
        // when the file is current.
        GeneratedMeshBuild IGeneratedMeshSource.BuildForPlayer()
        {
            return GeneratedMeshBuild.Never;
        }

        bool IGeneratedMeshSource.PlayerCanBuild => true;
#endif

        // Takes the meshes that the player build shipped. Returns false in the editor and when the cave must build. The
        // terrain data of the build already has the holes of the cave.
        private bool LoadPlayerMeshes()
        {
            string path = GeneratedMeshCache.PlayerPath(this);
            if (path == null || _segments.Length == 0) return false;
            using ProfilerMarker.AutoScope load = LoogaGeologyMarkers.ShippedMeshLoad.Auto();
            return LoadMeshes(path, null);
        }

        // Takes the meshes of a cache file. Returns false when there is no file, or when a key is given and the file has
        // another key.
        private bool LoadMeshes(string path, Hash128? key)
        {
            using BinaryReader reader = GeneratedMeshCache.OpenFile(path, key);
            if (reader == null) return false;
            try
            {
                _rockSignature = reader.ReadInt32();
                reader.ReadUInt64();
                UnityEngine.Terrain[] terrains = GeneratedMeshCache.Terrains();
                _root = new GameObject("Cave " + name) { hideFlags = HideFlags.HideAndDontSave, layer = gameObject.layer };
                int chunks = reader.ReadInt32();
                for (int index = 0; index < chunks; index++)
                {
                    string chunkName = reader.ReadString();
                    string terrainName = reader.ReadString();
                    AddCachedChunk(chunkName, Array.Find(terrains, item => item.name == terrainName),
                        GeneratedMeshCache.RawMesh.Read(reader));
                }
                int entrances = reader.ReadInt32();
                if (entrances != _entrances.Count) throw new InvalidDataException("Other entrances.");
                for (int index = 0; index < entrances; index++)
                {
                    LoogaCaveEntrance.CacheEntry entry = LoogaCaveEntrance.CacheEntry.Read(reader);
                    if (_entrances[index]) _entrances[index].Restore(entry, this, terrains);
                }
                _buildQueued = false;
                _copiesLost = true;
                return true;
            }
            catch (Exception exception) when (exception is IOException || exception is InvalidDataException)
            {
                Release();
                return false;
            }
        }

        private void AddCachedChunk(string chunkName, UnityEngine.Terrain terrain, Mesh mesh)
        {
            _meshes.Add(mesh);
            var chunk = new GameObject(chunkName) { hideFlags = HideFlags.HideAndDontSave, layer = gameObject.layer };
            chunk.transform.SetParent(_root.transform, false);
            chunk.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = chunk.AddComponent<MeshRenderer>();
            renderer.shadowCastingMode = _shadowCasting;
            renderer.sharedMaterial = SurfaceMaterial();
            renderer.SetPropertyBlock(ShadeBlock());
            if (terrain)
            {
                _groundRenderers.Add((terrain, renderer));
                BindGround(terrain, renderer);
            }
            else if (mesh.subMeshCount > 1)
            {
                renderer.sharedMaterials = new[] { SurfaceMaterial(), SurfaceMaterial() };
            }
            chunk.AddComponent<MeshCollider>().sharedMesh = mesh;
        }

        // Returns false when an enabled entrance has no output of the last domain.
        private bool AdoptEntrances()
        {
            bool all = true;
            foreach (LoogaCaveEntrance entrance in _entrances)
            {
                if (entrance && !entrance.AdoptAfterReload()) all = false;
            }
            return all;
        }

        private void UpdateShape()
        {
            _passages.Clear();
            GetComponentsInChildren(false, _passages);
            _shafts.Clear();
            GetComponentsInChildren(false, _shafts);
            _chambers.Clear();
            GetComponentsInChildren(false, _chambers);
            _entrances.Clear();
            GetComponentsInChildren(false, _entrances);
            var segments = new List<CaveSegment>();
            float margin = BoundsMargin + _roughness * 2f;
            foreach (LoogaCavePassage passage in _passages)
            {
                passage.AppendSegments(segments, SegmentSpacing, margin);
            }
            foreach (LoogaCaveShaft shaft in _shafts)
            {
                shaft.AppendSegment(segments, margin);
            }
            foreach (LoogaCaveChamber chamber in _chambers)
            {
                chamber.AppendSegment(segments, margin);
            }
            Bounds old = _segmentBounds;
            _segments = segments.ToArray();
            _segmentBounds = SegmentBounds(_segments);
            ulong hash = ShapeHash(_segments);
            if (hash == _shapeHash) return;
            _shapeHash = hash;
            Bounds changed = _segmentBounds;
            if (old.size != Vector3.zero) changed.Encapsulate(old);
            if (changed.size == Vector3.zero) return;
            if (_holdShapeEvent) _shapeEvent = changed;
            else ShapeChanged?.Invoke(changed);
        }

        private static Bounds SegmentBounds(CaveSegment[] segments)
        {
            if (segments.Length == 0) return default;
            float3 min = segments[0].BoundsMin;
            float3 max = segments[0].BoundsMax;
            foreach (CaveSegment segment in segments)
            {
                min = math.min(min, segment.BoundsMin);
                max = math.max(max, segment.BoundsMax);
            }
            var bounds = new Bounds();
            bounds.SetMinMax(min, max);
            return bounds;
        }

        // FNV-1a over the segment geometry. 0 means no segments.
        private ulong ShapeHash(CaveSegment[] segments)
        {
            if (segments.Length == 0) return 0;
            ulong hash = 14695981039346656037UL;
            void Add(float value) => hash = (hash ^ (uint)BitConverter.SingleToInt32Bits(value)) * 1099511628211UL;
            foreach (CaveSegment segment in segments)
            {
                Add(segment.Start.x); Add(segment.Start.y); Add(segment.Start.z);
                Add(segment.End.x); Add(segment.End.y); Add(segment.End.z);
                Add(segment.StartSize.x); Add(segment.StartSize.y);
                Add(segment.EndSize.x); Add(segment.EndSize.y);
                Add(segment.CornerRadius);
                Add((float)segment.Shape);
                Add(segment.NoiseAmplitude);
                Add(segment.StartPlane.x); Add(segment.StartPlane.y);
            }
            Add(_bumpSize);
            Add(_roughness);
            Add(_roughnessScale);
            return hash == 0 ? 1 : hash;
        }
        #endregion

        #region Holes
        // The terrain texels whose surface lies inside the cave, for each terrain data. A texel is tested at its
        // corners, edge centres and centre. Only the smallest terrain at a place counts, so a large background terrain
        // under the tiles keeps its surface.
        private Dictionary<TerrainData, HashSet<int>> FindHoles(TerrainHeightSampler sampler, out float holeTop,
            out List<(UnityEngine.Terrain, HashSet<int>)> holeTerrains)
        {
            var result = new Dictionary<TerrainData, HashSet<int>>();
            holeTerrains = new List<(UnityEngine.Terrain, HashSet<int>)>();
            float top = float.MinValue;
            Bounds bounds = _segmentBounds;
            var area = Rect.MinMaxRect(bounds.min.x, bounds.min.z, bounds.max.x, bounds.max.z);
            UnityEngine.Terrain[] terrains = UnityEngine.Terrain.activeTerrains;
            CaveSegment[] segments = _segments;
            float bumpSize = _bumpSize, roughness = _roughness, roughnessScale = _roughnessScale;
            float caveBottom = bounds.min.y, caveTop = bounds.max.y;
            foreach (UnityEngine.Terrain terrain in terrains)
            {
                TerrainData data = terrain.terrainData;
                if (!data) continue;
                Vector3 position = terrain.GetPosition();
                Vector3 size = data.size;
                var terrainRect = new Rect(position.x, position.z, size.x, size.z);
                if (!terrainRect.Overlaps(area)) continue;
                int resolution = data.holesResolution;
                float texelX = size.x / resolution, texelZ = size.z / resolution;
                int x0 = Mathf.Clamp(Mathf.FloorToInt((area.xMin - position.x) / texelX), 0, resolution - 1);
                int x1 = Mathf.Clamp(Mathf.CeilToInt((area.xMax - position.x) / texelX), 0, resolution - 1);
                int z0 = Mathf.Clamp(Mathf.FloorToInt((area.yMin - position.z) / texelZ), 0, resolution - 1);
                int z1 = Mathf.Clamp(Mathf.CeilToInt((area.yMax - position.z) / texelZ), 0, resolution - 1);
                var smaller = new List<Rect>();
                foreach (UnityEngine.Terrain other in terrains)
                {
                    if (other == terrain || !other.terrainData) continue;
                    Vector3 otherSize = other.terrainData.size;
                    if (otherSize.x * otherSize.z >= size.x * size.z) continue;
                    Vector3 otherPosition = other.GetPosition();
                    smaller.Add(new Rect(otherPosition.x, otherPosition.z, otherSize.x, otherSize.z));
                }
                var texels = new HashSet<int>();
                var tops = new float[z1 - z0 + 1];
                var rows = new List<int>[z1 - z0 + 1];
                Parallel.For(z0, z1 + 1, z =>
                {
                    var row = new List<int>();
                    float rowTop = float.MinValue;
                    for (int x = x0; x <= x1; x++)
                    {
                        float worldX = position.x + x * texelX, worldZ = position.z + z * texelZ;
                        var centre = new Vector2(worldX + 0.5f * texelX, worldZ + 0.5f * texelZ);
                        if (smaller.Exists(rect => rect.Contains(centre))) continue;
                        if (!TexelInCave(sampler, segments, bumpSize, roughness, roughnessScale, worldX, worldZ,
                                texelX, texelZ, caveBottom, caveTop, out float texelTop)) continue;
                        row.Add(z * resolution + x);
                        rowTop = Mathf.Max(rowTop, texelTop);
                    }
                    rows[z - z0] = row;
                    tops[z - z0] = rowTop;
                });
                for (int index = 0; index < rows.Length; index++)
                {
                    texels.UnionWith(rows[index]);
                    top = Mathf.Max(top, tops[index]);
                }
                if (texels.Count == 0) continue;
                SpreadOverFace(sampler, segments, texels, position, texelX, texelZ, resolution, ref top);
                HoleFaceFoot(sampler, segments, texels, position, texelX, texelZ, resolution);
                result[data] = texels;
                holeTerrains.Add((terrain, texels));
            }
            holeTop = top;
            return result;
        }

        private static bool TexelInCave(TerrainHeightSampler sampler, CaveSegment[] segments, float bumpSize,
            float roughness, float roughnessScale, float worldX, float worldZ, float texelX, float texelZ,
            float caveBottom, float caveTop, out float texelTop)
        {
            texelTop = float.MinValue;
            float bottom = float.MaxValue;
            for (int corner = 0; corner < 4; corner++)
            {
                if (!sampler.TrySampleHeight(worldX + (corner & 1) * texelX, worldZ + (corner >> 1) * texelZ,
                        out float height)) return false;
                texelTop = Mathf.Max(texelTop, height);
                bottom = Mathf.Min(bottom, height);
            }
            // The bilinear surface lies between the corner heights.
            if (bottom > caveTop || texelTop < caveBottom) return false;
            for (int index = 0; index < 9; index++)
            {
                float x = worldX + index % 3 * 0.5f * texelX;
                float z = worldZ + index / 3 * 0.5f * texelZ;
                if (!sampler.TrySampleHeight(x, z, out float height)) continue;
                if (CaveField.Evaluate(segments, new float3(x, height, z), bumpSize, roughness, roughnessScale) <
                    -HoleInset)
                {
                    return true;
                }
            }
            return false;
        }

        // A cut face shows the mesh from the floor to the ground above it, so the holes spread from the cave over the
        // steep texels inside the passage outline. Flat ground above or beside the face stops the spread, so the
        // terrain keeps the faces of upper benches and the walls beside the opening.
        private static void SpreadOverFace(TerrainHeightSampler sampler, CaveSegment[] segments, HashSet<int> texels,
            Vector3 position, float texelX, float texelZ, int resolution, ref float top)
        {
            var queue = new Queue<int>(texels);
            while (queue.Count > 0)
            {
                int texel = queue.Dequeue();
                int x = texel % resolution, z = texel / resolution;
                for (int side = 0; side < 4; side++)
                {
                    int nextX = x + (side == 0 ? 1 : side == 1 ? -1 : 0);
                    int nextZ = z + (side == 2 ? 1 : side == 3 ? -1 : 0);
                    if (nextX < 0 || nextZ < 0 || nextX >= resolution || nextZ >= resolution) continue;
                    int next = nextZ * resolution + nextX;
                    if (texels.Contains(next)) continue;
                    float worldX = position.x + nextX * texelX, worldZ = position.z + nextZ * texelZ;
                    var centre = new float2(worldX + 0.5f * texelX, worldZ + 0.5f * texelZ);
                    if (CaveField.Plan(segments, centre) > FaceSpreadMargin) continue;
                    if (!SteepTexel(sampler, worldX, worldZ, texelX, texelZ, out float texelTop)) continue;
                    texels.Add(next);
                    queue.Enqueue(next);
                    top = Mathf.Max(top, texelTop);
                }
            }
        }

        // Floor texels in front of the foot of a holed face, inside the passage outline. The terrain renderer can draw a
        // face with triangles that reach past its steep texels, and over the floor texels in front they stood as tall
        // spikes in the opening. The cave floor draws these texels, and its edge rises to the terrain around them.
        private static void HoleFaceFoot(TerrainHeightSampler sampler, CaveSegment[] segments, HashSet<int> texels,
            Vector3 position, float texelX, float texelZ, int resolution)
        {
            float steepRise = SteepRise * Mathf.Max(texelX, texelZ);
            // The foot height of each holed face texel, then of each new floor texel in front of it.
            var front = new Dictionary<int, float>();
            foreach (int texel in texels)
            {
                float worldX = position.x + texel % resolution * texelX;
                float worldZ = position.z + texel / resolution * texelZ;
                if (TexelRange(sampler, worldX, worldZ, texelX, texelZ, out float bottom, out float top) &&
                    top - bottom > steepRise)
                {
                    front[texel] = bottom;
                }
            }
            for (int ring = 0; ring < FootRings && front.Count > 0; ring++)
            {
                var next = new Dictionary<int, float>();
                foreach (KeyValuePair<int, float> entry in front)
                {
                    int x = entry.Key % resolution, z = entry.Key / resolution;
                    for (int nearZ = z - 1; nearZ <= z + 1; nearZ++)
                    {
                        for (int nearX = x - 1; nearX <= x + 1; nearX++)
                        {
                            if (nearX < 0 || nearZ < 0 || nearX >= resolution || nearZ >= resolution) continue;
                            int near = nearZ * resolution + nearX;
                            if (texels.Contains(near) || next.ContainsKey(near)) continue;
                            float worldX = position.x + nearX * texelX, worldZ = position.z + nearZ * texelZ;
                            var centre = new float2(worldX + 0.5f * texelX, worldZ + 0.5f * texelZ);
                            if (CaveField.Plan(segments, centre) > FaceSpreadMargin) continue;
                            // Only level ground at the foot: the spread rules decide about steep texels, and the
                            // ground above the face keeps its surface.
                            if (!TexelRange(sampler, worldX, worldZ, texelX, texelZ, out float bottom,
                                    out float top) || top - bottom > steepRise || top > entry.Value + FootRise)
                            {
                                continue;
                            }
                            next[near] = entry.Value;
                        }
                    }
                }
                texels.UnionWith(next.Keys);
                front = next;
            }
        }

        private static bool TexelRange(TerrainHeightSampler sampler, float worldX, float worldZ, float texelX,
            float texelZ, out float bottom, out float top)
        {
            bottom = float.MaxValue;
            top = float.MinValue;
            for (int corner = 0; corner < 4; corner++)
            {
                if (!sampler.TrySampleHeight(worldX + (corner & 1) * texelX, worldZ + (corner >> 1) * texelZ,
                        out float height)) return false;
                top = Mathf.Max(top, height);
                bottom = Mathf.Min(bottom, height);
            }
            return true;
        }

        private static bool SteepTexel(TerrainHeightSampler sampler, float worldX, float worldZ, float texelX,
            float texelZ, out float texelTop)
        {
            texelTop = float.MinValue;
            float bottom = float.MaxValue;
            for (int corner = 0; corner < 4; corner++)
            {
                if (!sampler.TrySampleHeight(worldX + (corner & 1) * texelX, worldZ + (corner >> 1) * texelZ,
                        out float height)) return false;
                texelTop = Mathf.Max(texelTop, height);
                bottom = Mathf.Min(bottom, height);
            }
            return texelTop - bottom > SteepRise * Mathf.Max(texelX, texelZ);
        }

        // Writes the new holes and gives back the texels of the last build that are no longer in the cave. Only
        // texels that change are written.
        private void ApplyHoles(Dictionary<TerrainData, HashSet<int>> holes)
        {
            var previous = new Dictionary<TerrainData, HashSet<int>>();
            foreach (HoleRecord record in _holes)
            {
                if (!record.Data || record.Texels == null) continue;
                if (!previous.TryGetValue(record.Data, out HashSet<int> set)) previous[record.Data] = set = new();
                set.UnionWith(record.Texels);
            }
            var datas = new HashSet<TerrainData>(previous.Keys);
            datas.UnionWith(holes.Keys);
            bool recordsChanged = false;
            foreach (TerrainData data in datas)
            {
                previous.TryGetValue(data, out HashSet<int> old);
                holes.TryGetValue(data, out HashSet<int> wanted);
                old ??= new HashSet<int>();
                wanted ??= new HashSet<int>();
                if (!old.SetEquals(wanted)) recordsChanged = true;
                WriteHoles(data, old, wanted);
            }
            if (!recordsChanged) return;
            _holes.Clear();
            foreach (KeyValuePair<TerrainData, HashSet<int>> pair in holes)
            {
                var texels = new int[pair.Value.Count];
                pair.Value.CopyTo(texels);
                Array.Sort(texels);
                _holes.Add(new HoleRecord { Data = pair.Key, Texels = texels });
            }
#if UNITY_EDITOR
            if (!Application.isPlaying) UnityEditor.EditorUtility.SetDirty(this);
#endif
        }

        private static void WriteHoles(TerrainData data, HashSet<int> old, HashSet<int> wanted)
        {
            if (old.Count == 0 && wanted.Count == 0) return;
            int resolution = data.holesResolution;
            int xMin = int.MaxValue, zMin = int.MaxValue, xMax = int.MinValue, zMax = int.MinValue;
            foreach (int texel in old) Grow(texel);
            foreach (int texel in wanted) Grow(texel);
            void Grow(int texel)
            {
                xMin = Math.Min(xMin, texel % resolution);
                xMax = Math.Max(xMax, texel % resolution);
                zMin = Math.Min(zMin, texel / resolution);
                zMax = Math.Max(zMax, texel / resolution);
            }
            int width = xMax - xMin + 1, depth = zMax - zMin + 1;
            // True is solid surface, false is a hole.
            bool[,] surface = data.GetHoles(xMin, zMin, width, depth);
            bool changed = false;
            for (int z = 0; z < depth; z++)
            {
                for (int x = 0; x < width; x++)
                {
                    int texel = (zMin + z) * resolution + xMin + x;
                    bool hole = wanted.Contains(texel);
                    if (hole && surface[z, x])
                    {
                        surface[z, x] = false;
                        changed = true;
                    }
                    else if (!hole && old.Contains(texel) && !surface[z, x])
                    {
                        surface[z, x] = true;
                        changed = true;
                    }
                }
            }
            if (!changed) return;
            data.SetHoles(xMin, zMin, surface);
#if UNITY_EDITOR
            GeneratedMeshCache.Forget(data);
            if (!Application.isPlaying) UnityEditor.EditorUtility.SetDirty(data);
#endif
        }

#if UNITY_EDITOR
        // A removed or disabled system gives its holes back. A closed scene or a script reload keeps them, because the
        // system builds the same holes when it loads again.
        private void ClearHolesLater()
        {
            if (Application.isPlaying || _holes.Count == 0) return;
            UnityEngine.SceneManagement.Scene scene = gameObject.scene;
            List<HoleRecord> records = new(_holes);
            UnityEditor.EditorApplication.delayCall += () =>
            {
                if (!scene.isLoaded || UnityEditor.EditorApplication.isPlayingOrWillChangePlaymode) return;
                if (this && isActiveAndEnabled) return;
                foreach (HoleRecord record in records)
                {
                    if (record.Data && record.Texels != null)
                    {
                        WriteHoles(record.Data, new HashSet<int>(record.Texels), new HashSet<int>());
                    }
                }
                if (this) _holes.Clear();
            };
        }
#endif
        #endregion

        #region Mesh
        private void BuildMesh(TerrainHeightSampler terrain, Vector3 min, Vector3 max,
            List<(UnityEngine.Terrain Terrain, HashSet<int> Texels)> holeTerrains)
        {
            float step = _cellSize;
            // The grid lines stay at multiples of the cell size, so a small change does not move every vertex.
            var origin = new float3(Mathf.Floor(min.x / step), Mathf.Floor(min.y / step), Mathf.Floor(min.z / step)) *
                step;
            int3 cells = (int3)math.ceil(((float3)(Vector3)max - origin) / step);
            int3 chunks = (cells + ChunkCells - 1) / ChunkCells;
            if (math.any(chunks <= 0)) return;
            if (chunks.x * chunks.y * chunks.z > MaximumChunks)
            {
                Debug.LogWarning("Looga cave system '" + name + "': the cave needs " + chunks.x * chunks.y * chunks.z +
                                 " chunks, more than " + MaximumChunks + ". Check the passage splines.", this);
                return;
            }

            // Heights and hole flags on the grid columns, with one apron column before and two after the grid.
            int width = chunks.x * ChunkCells + 3, depth = chunks.z * ChunkCells + 3;
            var heights = new NativeArray<float>(width * depth, Allocator.TempJob);
            var holes = new NativeArray<byte>(width * depth, Allocator.TempJob);
            var chunkJobs = new List<ChunkJob>();
            try
            {
                FillColumns(terrain, origin, step, width, depth, heights, holes, holeTerrains);
                for (int cz = 0; cz < chunks.z; cz++)
                {
                    for (int cy = 0; cy < chunks.y; cy++)
                    {
                        for (int cx = 0; cx < chunks.x; cx++)
                        {
                            ChunkJob job = ScheduleChunk(new int3(cx, cy, cz), origin, step, width, heights, holes);
                            if (job != null) chunkJobs.Add(job);
                        }
                    }
                }
                foreach (ChunkJob job in chunkJobs)
                {
                    job.Handle.Complete();
                    job.HoleFaceIndexCount = job.Mesh.TerrainIndices.Length;
                }
                ShadeChunks(chunkJobs, chunks, origin, step, width, heights, holes);
                UnityEngine.Terrain[] terrains = UnityEngine.Terrain.activeTerrains;
                foreach (ChunkJob job in chunkJobs)
                {
                    int count = job.Mesh.Indices.Length + job.Mesh.HoleRockIndices.Length +
                        job.Mesh.TerrainIndices.Length;
                    if (count > 0) AddChunk(job, terrains);
                }
                _rockSignature = RockSignature();
                ApplyRockCover();
            }
            finally
            {
                foreach (ChunkJob job in chunkJobs)
                {
                    job.Handle.Complete();
                    job.Dispose();
                }
                heights.Dispose();
                holes.Dispose();
            }
        }

        // Writes the cave shade of each chunk vertex from the sky distance through the cave air.
        private void ShadeChunks(List<ChunkJob> chunkJobs, int3 chunks, float3 origin, float step, int heightWidth,
            NativeArray<float> heights, NativeArray<byte> holes)
        {
            int3 size = chunks * (ChunkCells / LightCells);
            int count = size.x * size.y * size.z;
            float cell = step * LightCells;
            var air = new NativeArray<byte>(count, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            var distance = new NativeArray<float>(count, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            var handles = new NativeArray<JobHandle>(chunkJobs.Count, Allocator.Temp);
            try
            {
                JobHandle sky = new CaveSkyJob
                {
                    Heights = heights, Air = air, Origin = origin, Cell = cell, Size = size,
                    CellsPerCell = LightCells, HeightWidth = heightWidth
                }.Schedule(count, 4096);
                for (int index = 0; index < chunkJobs.Count; index++)
                {
                    ChunkJob job = chunkJobs[index];
                    handles[index] = new CaveAirJob
                    {
                        Cave = job.Cave, Ground = job.Ground, Holes = holes, HeightWidth = heightWidth,
                        OpenDepth = HoleOpenDepth, Air = air, Size = size, FirstSample = job.Chunk * ChunkCells,
                        Samples = ChunkCells + 3, ChunkCells = ChunkCells, CellsPerCell = LightCells
                    }.Schedule(sky);
                }
                JobHandle lit = new CaveLightJob
                {
                    Air = air, Distance = distance, Size = size, Cell = cell,
                    MaximumDistance = _daylightReach * LightSearchReaches
                }.Schedule(JobHandle.CombineDependencies(handles));
                for (int index = 0; index < chunkJobs.Count; index++)
                {
                    ChunkJob job = chunkJobs[index];
                    job.Shade = new NativeArray<float2>(job.Mesh.Positions.Length, Allocator.TempJob,
                        NativeArrayOptions.UninitializedMemory);
                    job.Lip = new NativeArray<float4>(job.Mesh.Positions.Length, Allocator.TempJob,
                        NativeArrayOptions.UninitializedMemory);
                    JobHandle shaded = new CaveShadeJob
                    {
                        Air = air, Distance = distance, Positions = job.Mesh.Positions.AsArray(),
                        Normals = job.Mesh.Normals.AsArray(), Shade = job.Shade, Lip = job.Lip, Origin = origin,
                        Cell = cell, Size = size, Reach = _daylightReach, Darkness = _darkness
                    }.Schedule(job.Shade.Length, 1024, lit);
                    handles[index] = new CaveLipJob
                    {
                        CaveIndices = job.Mesh.Indices, Lip = job.Lip, TerrainIndices = job.Mesh.TerrainIndices,
                        Covers = job.Mesh.Covers, FullDepth = LipFullDepth, EndDepth = LipEndDepth
                    }.Schedule(shaded);
                }
                JobHandle.CombineDependencies(handles).Complete();
            }
            finally
            {
                handles.Dispose();
                air.Dispose();
                distance.Dispose();
            }
        }

        // Fills the terrain height and the hole flag of each grid column. Columns outside every terrain have no
        // ground.
        private static void FillColumns(TerrainHeightSampler terrain, float3 origin, float step, int width, int depth,
            NativeArray<float> heights, NativeArray<byte> holes,
            List<(UnityEngine.Terrain Terrain, HashSet<int> Texels)> holeTerrains)
        {
            var heightValues = new float[width * depth];
            var holeValues = new byte[width * depth];
            // Terrain properties are main thread only, so the hole areas are read before the parallel loop.
            var holeAreas = new List<HoleArea>();
            foreach ((UnityEngine.Terrain hole, HashSet<int> texels) in holeTerrains)
            {
                holeAreas.Add(new HoleArea
                {
                    Position = hole.GetPosition(), Size = hole.terrainData.size,
                    Resolution = hole.terrainData.holesResolution, Texels = texels
                });
            }
            Parallel.For(0, depth, z =>
            {
                float worldZ = origin.z + (z - 1) * step;
                for (int x = 0; x < width; x++)
                {
                    float worldX = origin.x + (x - 1) * step;
                    int index = z * width + x;
                    heightValues[index] = terrain.TrySampleHeight(worldX, worldZ, out float height) ? height : -1e5f;
                    foreach (HoleArea area in holeAreas)
                    {
                        if (area.Contains(worldX, worldZ))
                        {
                            holeValues[index] = 1;
                            break;
                        }
                    }
                }
            });
            heights.CopyFrom(heightValues);
            holes.CopyFrom(GrowHoles(holeValues, width, depth, SkirtCells));
        }

        // The hole flags (2) with a skirt (1) of a number of columns in each direction.
        private static byte[] GrowHoles(byte[] holes, int width, int depth, int cells)
        {
            var grown = new byte[holes.Length];
            for (int z = 0; z < depth; z++)
            {
                for (int x = 0; x < width; x++)
                {
                    if (holes[z * width + x] == 0) continue;
                    for (int nearZ = Math.Max(0, z - cells); nearZ <= Math.Min(depth - 1, z + cells); nearZ++)
                    {
                        for (int nearX = Math.Max(0, x - cells); nearX <= Math.Min(width - 1, x + cells); nearX++)
                        {
                            int near = nearZ * width + nearX;
                            if (grown[near] == 0) grown[near] = 1;
                        }
                    }
                    grown[z * width + x] = 2;
                }
            }
            return grown;
        }

        // The hole texels of one terrain, with the terrain values that the worker threads need.
        private struct HoleArea
        {
            public Vector3 Position;
            public Vector3 Size;
            public int Resolution;
            public HashSet<int> Texels;

            public bool Contains(float x, float z)
            {
                int texelX = Mathf.FloorToInt((x - Position.x) / Size.x * Resolution);
                int texelZ = Mathf.FloorToInt((z - Position.z) / Size.z * Resolution);
                if (texelX < 0 || texelZ < 0 || texelX >= Resolution || texelZ >= Resolution) return false;
                return Texels.Contains(texelZ * Resolution + texelX);
            }
        }

        // The jobs and buffers of one chunk.
        private sealed class ChunkJob : IDisposable
        {
            public int3 Chunk;
            public NativeArray<CaveSegment> Segments;
            public NativeArray<float> Cave;
            public NativeArray<float> Ground;
            public CaveMeshJob Mesh;
            // Cave shade of each vertex, for UV channel 3 (x), and its lip values (CaveShadeJob.Lip).
            public NativeArray<float2> Shade;
            public NativeArray<float4> Lip;
            // Terrain surface indices of the hole and skirt quads, before the lip copies of the cave quads.
            public int HoleFaceIndexCount;
            public JobHandle Handle;

            public void Dispose()
            {
                if (Segments.IsCreated) Segments.Dispose();
                if (Cave.IsCreated) Cave.Dispose();
                if (Ground.IsCreated) Ground.Dispose();
                if (Shade.IsCreated) Shade.Dispose();
                if (Lip.IsCreated) Lip.Dispose();
                if (Mesh.CellVertices.IsCreated) Mesh.CellVertices.Dispose();
                if (Mesh.Positions.IsCreated) Mesh.Positions.Dispose();
                if (Mesh.Normals.IsCreated) Mesh.Normals.Dispose();
                if (Mesh.Colors.IsCreated) Mesh.Colors.Dispose();
                if (Mesh.Covers.IsCreated) Mesh.Covers.Dispose();
                if (Mesh.Indices.IsCreated) Mesh.Indices.Dispose();
                if (Mesh.HoleRockIndices.IsCreated) Mesh.HoleRockIndices.Dispose();
                if (Mesh.TerrainIndices.IsCreated) Mesh.TerrainIndices.Dispose();
                if (Mesh.CellPositions.IsCreated) Mesh.CellPositions.Dispose();
                if (Mesh.CellOnTerrain.IsCreated) Mesh.CellOnTerrain.Dispose();
            }
        }

        // Schedules the sample and mesh jobs of one chunk. Null when no passage reaches the chunk and no terrain surface
        // over a hole lies in it. A cut face can rise above the passages into the next chunk, and that chunk must draw
        // the face, else the hole shows through the hill.
        private ChunkJob ScheduleChunk(int3 chunk, float3 origin, float step, int width, NativeArray<float> heights,
            NativeArray<byte> holes)
        {
            float3 chunkMin = origin + (float3)(chunk * ChunkCells) * step;
            float3 chunkMax = chunkMin + ChunkCells * step;
            float reach = 2f * step;
            var segments = new List<CaveSegment>();
            foreach (CaveSegment segment in _segments)
            {
                if (math.any(segment.BoundsMax < chunkMin - reach) || math.any(segment.BoundsMin > chunkMax + reach))
                {
                    continue;
                }
                segments.Add(segment);
            }
            if (segments.Count == 0 && !HasHoleSurface(chunk, chunkMin.y - step, chunkMax.y + step, width, heights,
                    holes))
            {
                return null;
            }

            int samples = ChunkCells + 3;
            int cells = ChunkCells + 2;
            var job = new ChunkJob
            {
                Chunk = chunk,
                Segments = new NativeArray<CaveSegment>(segments.ToArray(), Allocator.TempJob),
                Cave = new NativeArray<float>(samples * samples * samples, Allocator.TempJob,
                    NativeArrayOptions.UninitializedMemory),
                Ground = new NativeArray<float>(samples * samples * samples, Allocator.TempJob,
                    NativeArrayOptions.UninitializedMemory)
            };
            float3 apronOrigin = chunkMin - step;
            // The height grid has one apron column before the grid, so the apron sample of chunk 0 is column 0.
            var columnOffset = new int2(chunk.x * ChunkCells, chunk.z * ChunkCells);
            var sampleJob = new CaveSampleJob
            {
                Segments = job.Segments, Heights = heights, Cave = job.Cave, Ground = job.Ground,
                Origin = apronOrigin, Step = step, Samples = samples, HeightOffset = columnOffset,
                HeightWidth = width, BumpScale = _bumpSize, Roughness = _roughness,
                RoughnessScale = _roughnessScale
            };
            job.Mesh = new CaveMeshJob
            {
                Cave = job.Cave, Ground = job.Ground, Holes = holes, Heights = heights,
                CellVertices = new NativeArray<int>(cells * cells * cells, Allocator.TempJob,
                    NativeArrayOptions.UninitializedMemory),
                Positions = new NativeList<float3>(4096, Allocator.TempJob),
                Normals = new NativeList<float3>(4096, Allocator.TempJob),
                Colors = new NativeList<Color32>(4096, Allocator.TempJob),
                Covers = new NativeList<float4>(4096, Allocator.TempJob),
                Indices = new NativeList<int>(16384, Allocator.TempJob),
                HoleRockIndices = new NativeList<int>(1024, Allocator.TempJob),
                TerrainIndices = new NativeList<int>(1024, Allocator.TempJob),
                CellPositions = new NativeList<float3>(8192, Allocator.TempJob),
                CellOnTerrain = new NativeList<byte>(8192, Allocator.TempJob),
                Origin = apronOrigin, Step = step, Cells = ChunkCells,
                HoleOffset = columnOffset + 1, HoleWidth = width, StrataSpacing = _layerSpacing
            };
            JobHandle sampled = sampleJob.Schedule(samples * samples * samples, 512);
            job.Handle = job.Mesh.Schedule(sampled);
            return job;
        }

        // True when a grid column of the chunk, with its apron, is over a hole or its skirt and has its terrain height
        // between the two heights.
        private static bool HasHoleSurface(int3 chunk, float bottom, float top, int width, NativeArray<float> heights,
            NativeArray<byte> holes)
        {
            int depth = holes.Length / width;
            int x0 = chunk.x * ChunkCells, z0 = chunk.z * ChunkCells;
            for (int z = z0; z < Math.Min(z0 + ChunkCells + 3, depth); z++)
            {
                for (int x = x0; x < Math.Min(x0 + ChunkCells + 3, width); x++)
                {
                    int index = z * width + x;
                    if (holes[index] == 0) continue;
                    float height = heights[index];
                    if (height >= bottom && height <= top) return true;
                }
            }
            return false;
        }

        private void AddChunk(ChunkJob job, UnityEngine.Terrain[] terrains)
        {
            if (!_root)
            {
                _root = new GameObject("Cave " + name)
                {
                    hideFlags = HideFlags.HideAndDontSave, layer = gameObject.layer
                };
            }
            int vertexCount = job.Mesh.Positions.Length;
            var mesh = new Mesh
            {
                name = "Cave " + name + " " + job.Chunk,
                hideFlags = HideFlags.HideAndDontSave,
                indexFormat = vertexCount > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16
            };
            mesh.SetVertices(job.Mesh.Positions.AsArray());
            mesh.SetNormals(job.Mesh.Normals.AsArray());
            if (_floorGround) SetFloorGround(job, mesh);
            mesh.SetColors(job.Mesh.Colors.AsArray());
            mesh.SetUVs(2, job.Mesh.Covers.AsArray());
            mesh.SetUVs(3, job.Shade);
            int terrainCount = job.Mesh.TerrainIndices.Length;
            mesh.subMeshCount = terrainCount > 0 ? 2 : 1;
            // The rock draws the cave quads and, below the terrain copy, the terrain surface quads over a hole.
            int caveCount = job.Mesh.Indices.Length, holeRockCount = job.Mesh.HoleRockIndices.Length;
            using (var rockIndices = new NativeArray<int>(caveCount + holeRockCount, Allocator.Temp,
                       NativeArrayOptions.UninitializedMemory))
            {
                if (caveCount > 0) NativeArray<int>.Copy(job.Mesh.Indices.AsArray(), 0, rockIndices, 0, caveCount);
                if (holeRockCount > 0)
                {
                    NativeArray<int>.Copy(job.Mesh.HoleRockIndices.AsArray(), 0, rockIndices, caveCount,
                        holeRockCount);
                }
                mesh.SetIndices(rockIndices, MeshTopology.Triangles, 0, false);
            }
            if (terrainCount > 0)
            {
                mesh.SetIndices(job.Mesh.TerrainIndices.AsArray(), MeshTopology.Triangles, 1, false);
                _copies.Add(new ChunkCopy(mesh, job.Mesh, job.HoleFaceIndexCount));
            }
            mesh.RecalculateBounds();
            _meshes.Add(mesh);

            var chunk = new GameObject("Chunk " + job.Chunk)
            {
                hideFlags = HideFlags.HideAndDontSave, layer = gameObject.layer
            };
            chunk.transform.SetParent(_root.transform, false);
            chunk.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = chunk.AddComponent<MeshRenderer>();
            renderer.shadowCastingMode = _shadowCasting;
            renderer.sharedMaterial = SurfaceMaterial();
            renderer.SetPropertyBlock(ShadeBlock());
            // A floor ground needs the terrain values of the floor point in the rock material.
            UnityEngine.Terrain floorTerrain = null;
            if (_floorGround)
            {
                int floorIndex = LoogaTerrainGroundBlock.FindTerrain(_floorGround.position, terrains);
                floorTerrain = floorIndex >= 0 ? terrains[floorIndex] : null;
            }
            if (terrainCount == 0 && floorTerrain)
            {
                _groundRenderers.Add((floorTerrain, renderer));
                BindGround(floorTerrain, renderer);
            }
            if (terrainCount > 0)
            {
                // The terrain surface quads show the terrain at the chunk centre.
                int terrainIndex = LoogaTerrainGroundBlock.FindTerrain(mesh.bounds.center, terrains);
                UnityEngine.Terrain terrain = terrainIndex >= 0 ? terrains[terrainIndex] : null;
                if (terrain)
                {
                    _groundRenderers.Add((terrain, renderer));
                    BindGround(terrain, renderer);
                }
                else
                {
                    renderer.sharedMaterials = new[] { SurfaceMaterial(), SurfaceMaterial() };
                }
            }
            chunk.AddComponent<MeshCollider>().sharedMesh = mesh;
        }

        // The terrain surface material and the ground block of a terrain for submesh 1. Without a Looga terrain
        // surface the rock material draws the terrain quads.
        private void BindGround(UnityEngine.Terrain terrain, MeshRenderer renderer)
        {
            if (!_groundBlocks.TryGetValue(terrain, out MaterialPropertyBlock block))
            {
                block = LoogaTerrainGroundBlock.Create(terrain);
                block.SetFloat(CaveShadeId, 1f);
                _groundBlocks[terrain] = block;
            }
            Material cover = LoogaTerrainGroundBlock.SurfaceMeshMaterial(terrain);
            renderer.SetPropertyBlock(block);
            // A chunk with only a floor ground has no terrain quads.
            bool copy = renderer.TryGetComponent(out MeshFilter filter) && filter.sharedMesh &&
                        filter.sharedMesh.subMeshCount > 1;
            renderer.sharedMaterials = copy
                ? new[] { SurfaceMaterial(), cover ? cover : SurfaceMaterial() }
                : new[] { SurfaceMaterial() };
        }

        // The floor ground of the rock material: the ground weight in the vertex alpha, and the offset to the floor
        // point in UV channel 1, so each floor shows the layers of that point.
        private void SetFloorGround(ChunkJob job, Mesh mesh)
        {
            Vector3 point = _floorGround.position;
            int count = job.Mesh.Positions.Length;
            var offsets = new Vector2[count];
            for (int vertex = 0; vertex < count; vertex++)
            {
                Unity.Mathematics.float3 position = job.Mesh.Positions[vertex];
                Color32 color = job.Mesh.Colors[vertex];
                color.a = (byte)Mathf.RoundToInt(255f * FloorWeight(job.Mesh.Normals[vertex].y));
                job.Mesh.Colors[vertex] = color;
                offsets[vertex] = new Vector2(point.x - position.x, point.z - position.z);
            }
            mesh.SetUVs(1, offsets);
        }

        // A terrain surface change or a scene save can replace the splat maps, so the ground blocks are made again.
        private void RefreshGround(UnityEngine.Terrain terrain)
        {
            _groundBlocks.Remove(terrain);
            foreach ((UnityEngine.Terrain chunkTerrain, MeshRenderer renderer) in _groundRenderers)
            {
                if (chunkTerrain == terrain && renderer) BindGround(terrain, renderer);
            }
        }

        #region Rock cover
        // The rock (submesh 0) and terrain copy (submesh 1) triangles of one chunk mesh. A face is a steep copy
        // triangle. A cut face is a face of the terrain surface over a hole or its skirt; the others are the lip copies
        // of cave quads.
        private sealed class ChunkCopy
        {
            public readonly Mesh Mesh;
            public readonly int[] RockIndices;
            public readonly int[] Indices;
            public readonly Vector3[] Centres;
            public readonly bool[] Faces;
            public readonly bool[] CutFaces;
            public readonly bool[] DrawnByRock;

            public ChunkCopy(Mesh mesh, CaveMeshJob job, int holeFaceIndexCount)
            {
                Mesh = mesh;
                var rockIndices = new List<int>(job.Indices.Length + job.HoleRockIndices.Length);
                rockIndices.AddRange(job.Indices.AsArray());
                rockIndices.AddRange(job.HoleRockIndices.AsArray());
                RockIndices = rockIndices.ToArray();
                Indices = job.TerrainIndices.AsArray().ToArray();
                var rock = new HashSet<long>();
                for (int index = 0; index + 2 < RockIndices.Length; index += 3)
                {
                    rock.Add(TriangleKey(RockIndices[index], RockIndices[index + 1], RockIndices[index + 2]));
                }
                NativeArray<float3> positions = job.Positions.AsArray();
                int triangles = Indices.Length / 3;
                Centres = new Vector3[triangles];
                Faces = new bool[triangles];
                CutFaces = new bool[triangles];
                DrawnByRock = new bool[triangles];
                for (int triangle = 0; triangle < triangles; triangle++)
                {
                    int a = Indices[triangle * 3], b = Indices[triangle * 3 + 1], c = Indices[triangle * 3 + 2];
                    float3 normal = math.cross(positions[b] - positions[a], positions[c] - positions[a]);
                    Centres[triangle] = (positions[a] + positions[b] + positions[c]) / 3f;
                    Faces[triangle] = math.abs(normal.y) < CoverFaceFacing * math.length(normal);
                    CutFaces[triangle] = Faces[triangle] && triangle * 3 < holeFaceIndexCount;
                    DrawnByRock[triangle] = rock.Contains(TriangleKey(a, b, c));
                }
            }

            // The same key for the three vertices in any order.
            private static long TriangleKey(int a, int b, int c)
            {
                int low = Math.Min(a, Math.Min(b, c)), high = Math.Max(a, Math.Max(b, c));
                int middle = a + b + c - low - high;
                return ((long)low << 42) | ((long)middle << 21) | (long)high;
            }
        }

        /// <summary>
        /// Draws the steep faces of an opening with the rock material where a cliff wall, rock face or rock formation
        /// frames the opening. There the terrain copy showed the dark terrain cut face inside the rock frame. The faces
        /// move from the terrain copy to the rock, so no gap opens. Steep copy away from rock, for example the low
        /// banks of a passage end in front of a face, and openings in the bare terrain keep the copy, which matches the
        /// terrain around them.
        /// </summary>
        private void ApplyRockCover()
        {
            GatherRockCells();
            var rock = new List<int>();
            var copied = new List<int>();
            foreach (ChunkCopy copy in _copies)
            {
                if (!copy.Mesh) continue;
                rock.Clear();
                rock.AddRange(copy.RockIndices);
                copied.Clear();
                for (int triangle = 0; triangle < copy.Faces.Length; triangle++)
                {
                    List<int> target = InRockFrame(copy, triangle) ? rock : copied;
                    if (target == rock && copy.DrawnByRock[triangle]) continue;
                    target.Add(copy.Indices[triangle * 3]);
                    target.Add(copy.Indices[triangle * 3 + 1]);
                    target.Add(copy.Indices[triangle * 3 + 2]);
                }
                copy.Mesh.SetIndices(rock, MeshTopology.Triangles, 0, false);
                copy.Mesh.SetIndices(copied, MeshTopology.Triangles, 1, false);
            }
            _rockCells.Clear();
        }

        // A steep copy face beside a rock mesh: a cut face, or a lip copy on a cave wall deep under the ground or well
        // above the passage floor. The low banks of a passage end in front of a face keep the copy.
        private bool InRockFrame(ChunkCopy copy, int triangle)
        {
            Vector3 centre = copy.Centres[triangle];
            if (!copy.Faces[triangle] || !TouchesRock(centre)) return false;
            return copy.CutFaces[triangle] || UnderGround(centre) || HeightAboveFloor(centre) > BankHeight;
        }

        // Height of a point above the floor of the nearest passage, room or shaft, in meters.
        private float HeightAboveFloor(Vector3 point)
        {
            float nearest = float.MaxValue, height = 0f;
            foreach (CaveSegment segment in _segments)
            {
                float distance = CaveField.Segment(segment, point, out float floorHeight);
                if (distance >= nearest) continue;
                nearest = distance;
                height = floorHeight;
            }
            return height;
        }

        // True when the terrain at the point lies well above it: a cave wall under the rock beside an opening. The low
        // banks of a passage end in front of a face lie just below the ground beside them.
        private static bool UnderGround(Vector3 point)
        {
            UnityEngine.Terrain best = null;
            float bestArea = float.MaxValue;
            foreach (UnityEngine.Terrain terrain in UnityEngine.Terrain.activeTerrains)
            {
                if (!terrain || !terrain.terrainData) continue;
                Vector3 position = terrain.GetPosition();
                Vector3 size = terrain.terrainData.size;
                if (point.x < position.x || point.z < position.z || point.x > position.x + size.x ||
                    point.z > position.z + size.z || size.x * size.z >= bestArea) continue;
                best = terrain;
                bestArea = size.x * size.z;
            }
            return best && best.SampleHeight(point) + best.GetPosition().y - point.y > FrameDepth;
        }

        // True when a rock mesh vertex lies in the cell of the point or a cell next to it (1 to 2 cells away).
        private bool TouchesRock(Vector3 point)
        {
            if (_rockCells.Count == 0) return false;
            int3 cell = RockCell(point);
            for (int z = -1; z <= 1; z++)
            {
                for (int y = -1; y <= 1; y++)
                {
                    for (int x = -1; x <= 1; x++)
                    {
                        if (_rockCells.Contains(CellKey(cell + new int3(x, y, z)))) return true;
                    }
                }
            }
            return false;
        }

        // The cells that hold a vertex of a rock mesh near the cave. Only rock with the material of the cave counts:
        // the cave rock then looks the same as the rock frame. Beside other rock, for example dark basalt blocks, the
        // terrain copy stays.
        private void GatherRockCells()
        {
            _rockCells.Clear();
            Bounds area = _segmentBounds;
            area.Expand(4f * RockCellSize);
            GatherRockRenderers(area);
            var vertices = new List<Vector3>();
            foreach (MeshRenderer renderer in _rockRenderers)
            {
                if (!renderer || renderer.sharedMaterial != SurfaceMaterial() ||
                    !renderer.bounds.Intersects(area)) continue;
                MeshFilter filter = ReadableRockMesh(renderer);
                if (!filter) continue;
                filter.sharedMesh.GetVertices(vertices);
                Matrix4x4 localToWorld = filter.transform.localToWorldMatrix;
                foreach (Vector3 vertex in vertices)
                {
                    Vector3 point = localToWorld.MultiplyPoint3x4(vertex);
                    if (area.Contains(point)) _rockCells.Add(CellKey(RockCell(point)));
                }
            }
            _rockRenderers.Clear();
        }

        // The finest mesh of the chunk of a rock renderer that keeps its CPU copy. Rock meshes keep only the GPU copy,
        // except for the collider level and the finest level in the box of a cave entrance. A coarser level then gives
        // the vertices, which is enough for the rock cells. Null = no level of the chunk is readable.
        private static MeshFilter ReadableRockMesh(MeshRenderer renderer)
        {
            if (renderer.TryGetComponent(out MeshFilter filter) && filter.sharedMesh && filter.sharedMesh.isReadable)
            {
                return filter;
            }
            LODGroup group = renderer.GetComponentInParent<LODGroup>();
            if (!group) return null;
            foreach (LOD lod in group.GetLODs())
            {
                foreach (Renderer level in lod.renderers)
                {
                    if (level && level.TryGetComponent(out MeshFilter levelFilter) && levelFilter.sharedMesh &&
                        levelFilter.sharedMesh.isReadable)
                    {
                        return levelFilter;
                    }
                }
            }
            return null;
        }

        // The most detailed renderers of the rock meshes whose bounds reach the area.
        private void GatherRockRenderers(Bounds area)
        {
            _rockRenderers.Clear();
            foreach (LoogaCliffWall wall in FindObjectsByType<LoogaCliffWall>(FindObjectsSortMode.None))
            {
                if (wall.isActiveAndEnabled && wall.GeneratedBounds.Intersects(area))
                {
                    wall.GetGeneratedRenderers(0, _rockRenderers);
                }
            }
            foreach (LoogaRockFace face in FindObjectsByType<LoogaRockFace>(FindObjectsSortMode.None))
            {
                if (face.isActiveAndEnabled && face.GeneratedBounds.Intersects(area))
                {
                    face.GetGeneratedRenderers(0, _rockRenderers);
                }
            }
            foreach (LoogaRockFormation formation in FindObjectsByType<LoogaRockFormation>(FindObjectsSortMode.None))
            {
                if (formation.isActiveAndEnabled && formation.GeneratedBounds.Intersects(area))
                {
                    formation.GetGeneratedRenderers(0, _rockRenderers);
                }
            }
        }

        private static int3 RockCell(Vector3 point)
        {
            return new int3(Mathf.FloorToInt(point.x / RockCellSize), Mathf.FloorToInt(point.y / RockCellSize),
                Mathf.FloorToInt(point.z / RockCellSize));
        }

        private static long CellKey(int3 cell)
        {
            return ((long)(cell.x & 0x1FFFFF) << 42) | ((long)(cell.y & 0x1FFFFF) << 21) | (long)(cell.z & 0x1FFFFF);
        }

        // Changes when a rock mesh near the cave is built again: a build makes new meshes. About once a second.
        private int RockSignature()
        {
            Bounds area = _segmentBounds;
            area.Expand(4f * RockCellSize);
            GatherRockRenderers(area);
            int hash = 17;
            unchecked
            {
                foreach (MeshRenderer renderer in _rockRenderers)
                {
                    if (renderer && renderer.TryGetComponent(out MeshFilter filter) && filter.sharedMesh)
                    {
                        // Not the instance ID: a mesh from the cache of an earlier session has the same content. A
                        // sum, because the order of the renderers changes between sessions.
                        Mesh mesh = filter.sharedMesh;
                        hash += mesh.vertexCount * 31 + mesh.bounds.GetHashCode();
                    }
                }
            }
            _rockRenderers.Clear();
            return hash;
        }

        // Applies the rock cover again when a rock mesh near the cave changed. Walls fit to the cave and build after
        // it, so the first check after a cave build often finds them changed.
        private void CheckRockCover()
        {
            // The rock meshes still change while the rock faces build or load.
            if (!_copiesLost && _copies.Count == 0 && _entrances.Count == 0 ||
                Time.realtimeSinceStartup < _nextRockCheck || LoogaRockFace.PendingCount > 0) return;
            _nextRockCheck = Time.realtimeSinceStartup + RockCoverInterval;
            int signature = RockSignature();
            if (signature == _rockSignature) return;
            _rockSignature = signature;
            if (_copiesLost)
            {
                QueueBuild();
                return;
            }
            ApplyRockCover();
            // The entrances take the surfaces of the walls in their boxes.
            BuildEntrances();
#if UNITY_EDITOR
            WriteCache();
#endif
        }

        private void LateUpdate()
        {
            if (Application.isPlaying) CheckRockCover();
        }
        #endregion

        // The block of the chunks without terrain quads. It only turns the cave shade on.
        private MaterialPropertyBlock ShadeBlock()
        {
            if (_shadeBlock == null)
            {
                _shadeBlock = new MaterialPropertyBlock();
                _shadeBlock.SetFloat(CaveShadeId, 1f);
            }
            return _shadeBlock;
        }

        private Material SurfaceMaterial()
        {
            if (_material) return _material;
            if (!_defaultMaterial)
            {
                Shader shader = Shader.Find("Looga/Rock Face");
                _defaultMaterial = new Material(shader ? shader : Shader.Find("Universal Render Pipeline/Lit"))
                {
                    name = "Looga Cave Rock (default)", hideFlags = HideFlags.HideAndDontSave
                };
            }
            return _defaultMaterial;
        }
        #endregion

        #region Changes
#if UNITY_EDITOR
        private void EditorTick()
        {
            if (Application.isPlaying) return;
            _enabledThisLoad = false;
            int transforms = PassageTransformHash();
            if (transforms != _transformHash)
            {
                _transformHash = transforms;
                QueueBuild();
            }
            if (_buildQueued && Time.realtimeSinceStartupAsDouble >= _buildTime) Rebuild();
            CheckRockCover();
            foreach (LoogaCaveEntrance entrance in _entrances)
            {
                if (entrance) entrance.RefreshDestroyedGround();
            }
            foreach (KeyValuePair<UnityEngine.Terrain, MaterialPropertyBlock> entry in _groundBlocks)
            {
                if (!entry.Key || LoogaTerrainGroundBlock.HasControlMaps(entry.Value)) continue;
                // One terrain per tick: RefreshGround changes the dictionary.
                RefreshGround(entry.Key);
                break;
            }
        }
#endif

        // Splines in their own objects move without a spline change event, so the transforms are compared.
        private int PassageTransformHash()
        {
            int hash = 17;
            foreach (LoogaCavePassage passage in _passages)
            {
                if (!passage) continue;
                SplineContainer spline = passage.Spline;
                Transform target = spline ? spline.transform : passage.transform;
                hash = hash * 31 + target.localToWorldMatrix.GetHashCode();
            }
            foreach (LoogaCaveShaft shaft in _shafts)
            {
                if (shaft) hash = hash * 31 + shaft.transform.localToWorldMatrix.GetHashCode();
            }
            foreach (LoogaCaveChamber chamber in _chambers)
            {
                if (chamber) hash = hash * 31 + chamber.transform.localToWorldMatrix.GetHashCode();
            }
            foreach (LoogaCaveEntrance entrance in _entrances)
            {
                if (entrance) hash = hash * 31 + entrance.transform.localToWorldMatrix.GetHashCode();
            }
            return hash;
        }

        private void OnSplineChanged(UnityEngine.Splines.Spline spline, int knotIndex, SplineModification modification)
        {
            if (Application.isPlaying) return;
            foreach (LoogaCavePassage passage in _passages)
            {
                SplineContainer container = passage ? passage.Spline : null;
                if (!container) continue;
                foreach (UnityEngine.Splines.Spline own in container.Splines)
                {
                    if (own == spline) QueueBuild();
                }
            }
        }

        // The cave builds again when the terrain heights change in its area, for example after a stamp edit.
        private void OnHeightmapChanged(UnityEngine.Terrain terrain, RectInt heightRegion, bool synchronized)
        {
            if (Application.isPlaying || _segments.Length == 0 || !terrain || !terrain.terrainData) return;
            TerrainData data = terrain.terrainData;
            Vector3 position = terrain.GetPosition();
            float cellX = data.size.x / (data.heightmapResolution - 1);
            float cellZ = data.size.z / (data.heightmapResolution - 1);
            var changed = new Rect(position.x + heightRegion.xMin * cellX, position.z + heightRegion.yMin * cellZ,
                heightRegion.width * cellX, heightRegion.height * cellZ);
            Bounds bounds = _segmentBounds;
            var area = Rect.MinMaxRect(bounds.min.x, bounds.min.z, bounds.max.x, bounds.max.z);
            if (changed.Overlaps(area)) QueueBuild();
        }
        #endregion

        private void Release()
        {
            foreach (LoogaCaveEntrance entrance in _entrances)
            {
                if (entrance) entrance.Release();
            }
            if (_root) DestroyGenerated(_root);
            _root = null;
            foreach (Mesh mesh in _meshes)
            {
                if (mesh) DestroyGenerated(mesh);
            }
            _meshes.Clear();
            _copies.Clear();
            _copiesLost = false;
            _groundRenderers.Clear();
            _groundBlocks.Clear();
        }

        // The output as the next domain reads it after a script reload.
        [Serializable]
        private sealed class ReloadState
        {
            public int Root;
            public int[] Meshes;
            public int[] Renderers;
            public int[] Terrains;
            public int RockSignature;
            public int TransformHash;
            public long ShapeHash;
        }

        private static void DestroyGenerated(UnityEngine.Object target)
        {
            if (Application.isPlaying) Destroy(target);
            else DestroyImmediate(target);
        }
    }
}
