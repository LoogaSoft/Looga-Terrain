using System.Collections.Generic;
using System.Runtime.InteropServices;
using LoogaSoft.Terrain.Surface;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace LoogaSoft.Terrain.Geology
{
    /// <summary>
    /// One mesh for the ground, the rock and the cave at a cave opening. The entrance is a box around the opening. In
    /// the box the terrain gets holes, and the rock meshes and terrain covers of other builders end along a ragged edge
    /// (LoogaCaveEntranceClip.hlsl). The entrance mesh draws all of it from one field: the terrain heights, the cliff
    /// wall and rock face surfaces (found with rays along the forward axis) and the cave. Flat ground in daylight shows
    /// the terrain surface, the rest shows the cave rock. So no seam shows where the terrain, the walls and the cave
    /// meet. The object must be a child of a <see cref="LoogaCaveSystem"/>, which builds it after the cave. It sits on
    /// the opening floor at the face, and its forward axis points out of the cave. The mesh is not saved with the scene.
    /// The rays hit the finest level of detail of the walls, which the walls keep on the CPU in the box (DetailAreas).
    /// A wall built before the box moved there gives its collider shape until it builds again.
    /// </summary>
    /// <remarks>
    /// The other meshes keep their colliders in the box. They lie where the entrance found its surfaces, so the
    /// entrance collider adds only the ground over the terrain holes.
    /// </remarks>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [AddComponentMenu("Looga Terrain/Cave Entrance")]
    public sealed class LoogaCaveEntrance : MonoBehaviour
    {
        // Most boxes that the shaders cut (LOOGA_CAVE_ENTRANCE_MAX in LoogaCaveEntranceClip.hlsl).
        private const int MaximumBoxes = 16;
        // The rays that find the wall surfaces start this far in front of the box, in meters.
        private const float RayStart = 1f;
        // Field value of a column without a wall, and of a column outside every terrain.
        private const float NoWall = -1e4f;
        private const float NoGround = -1e5f;
        // In the edge band the entrance surfaces lie this far behind the surfaces of the other meshes, so those win
        // where both draw, in meters.
        private const float EdgeInset = 0.03f;
        // A vertex this close to a cliff wall or rock face surface takes the cavity and the terrain cover of that
        // surface, so wall tops keep their ground, in meters.
        private const float WallColorReach = 0.4f;
        // The terrain surface starts on faces whose normal y is above the first value and is full above the second.
        private const float GroundFacingStart = 0.55f;
        private const float GroundFacingFull = 0.75f;
        // The terrain surface ends inside the opening between these cave shade values: about 0.75 to 2.5 m from the
        // sky at the default daylight reach, as the terrain lip of the cave.
        private const float GroundShadeStart = 0.04f;
        private const float GroundShadeEnd = 0.14f;
        // A floor under the rock shows the ground this far in front of the object, past the foot of the cut face, in
        // meters.
        private const float FloorGroundReach = 1.5f;
        // A floor is under the rock where the terrain lies this far above it, in meters.
        private const float UnderRockDepth = 1f;
        // Cave vertices within this distance give their shade to an entrance vertex, in meters.
        private const float ShadeReach = 1.5f;
        // A vertex this close to the cave field lies on a surface that the cave cuts, in meters.
        private const float CaveSurfaceReach = 0.4f;
        // Rock of a rock frame within this distance of the cave gets up to this cavity, so the edge of a mined
        // opening is darker and dirtier than the face around it, in meters.
        private const float OpeningEdgeReach = 2f;
        private const float OpeningEdgeCavity = 0.5f;
        // Plain ground more than this many terrain hole texels away from a hole is left to the terrain, which also
        // draws its grass there. The mesh overlaps the terrain beside the holes by this margin.
        private const int HoleMargin = 3;
        // The terrain shader blends the hole mask between texels, so a hole shows up to about half a texel into its
        // neighbours. Rock stays below the cover within this many texels of a hole.
        private const int HoleRockMargin = 1;
        // A vertex this close to the terrain height lies on the terrain surface, in meters.
        private const float OnTerrainReach = 0.3f;

        private static readonly List<LoogaCaveEntrance> Built = new();
        private static readonly int ToBoxId = Shader.PropertyToID("_LoogaCaveEntranceWorldToBox");
        private static readonly int HalfId = Shader.PropertyToID("_LoogaCaveEntranceSize");
        private static readonly int SphereId = Shader.PropertyToID("_LoogaCaveEntranceSphere");
        private static readonly int BoundsMinId = Shader.PropertyToID("_LoogaCaveEntranceMin");
        private static readonly int BoundsMaxId = Shader.PropertyToID("_LoogaCaveEntranceMax");
        private static readonly int CountId = Shader.PropertyToID("_LoogaCaveEntranceCount");
        private static readonly int CaveShadeId = Shader.PropertyToID("_LoogaCaveShade");
        // Reused by the per-update cover material check, so the check does not allocate.
        private static readonly List<Material> MaterialBuffer = new();
        // The rays hit the finest wall meshes and the wall colliders, so they can find two hits for each surface.
        private static readonly RaycastHit[] Hits = new RaycastHit[64];
        private static readonly VertexAttributeDescriptor[] Layout =
        {
            new(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
            new(VertexAttribute.Normal, VertexAttributeFormat.Float32, 4),
            new(VertexAttribute.Color, VertexAttributeFormat.UNorm8, 4),
            new(VertexAttribute.TexCoord1, VertexAttributeFormat.Float32, 2),
            new(VertexAttribute.TexCoord2, VertexAttributeFormat.Float32, 4),
            new(VertexAttribute.TexCoord3, VertexAttributeFormat.Float32, 2)
        };

        [Header("Box")]
        [Tooltip("Width of the box across the opening, in meters.")]
        [SerializeField, Min(1f)] private float _width = 18f;
        [Tooltip("How far the box goes into the cave behind the object, in meters.")]
        [SerializeField, Min(0.5f)] private float _inside = 7f;
        [Tooltip("How far the box goes out in front of the object, in meters.")]
        [SerializeField, Min(0.5f)] private float _outside = 9f;
        [Tooltip("Depth of the box below the object, in meters.")]
        [SerializeField, Min(0.5f)] private float _below = 3f;
        [Tooltip("Height of the box above the object, in meters. Ground higher than the top band keeps the terrain " +
                 "and is not part of the mesh.")]
        [SerializeField, Min(1f)] private float _height = 12f;
        [Tooltip("Width of the band inside the box sides where the walls, the cave and the terrain end along a " +
                 "ragged edge, in meters.")]
        [SerializeField, Min(0.1f)] private float _edgeBand = 1f;
        [Tooltip("The terrain gets holes where its surface is this close to the cave, in meters. Farther away the " +
                 "terrain stays, with its grass, under the same ground of the entrance mesh. Use more on bare ground " +
                 "with steep cut rims, for example in a quarry.")]
        [SerializeField, Min(0.25f)] private float _holeReach = 1.5f;

        [Header("Mesh")]
        [Tooltip("Size of one mesh cell in meters. Smaller gives sharper edges and more triangles.")]
        [SerializeField, Range(0.125f, 1f)] private float _cellSize = 0.25f;
        [SerializeField] private ShadowCastingMode _shadowCasting = ShadowCastingMode.On;

        private GameObject _root;
        private Mesh _mesh;
        // The box of the built mesh in the space of the build frame.
        private Matrix4x4 _frame;
        private Vector3 _boxMin;
        private Vector3 _boxMax;
        // The terrain ground values of the renderer. A scene save can destroy the splat maps they hold.
        private MaterialPropertyBlock _groundBlock;
        private UnityEngine.Terrain _groundTerrain;

        /// <summary>Triangles of the built mesh. 0 before a build.</summary>
        public int TriangleCount { get; private set; }

        #region Built-in
        private void OnEnable()
        {
            QueueSystemBuild();
        }

        private void OnDisable()
        {
            // A script reload keeps the mesh. The cave system takes it back in the next domain.
            if (GeneratedReloadHandover.Unloading && StoreForReload()) return;
            Release();
            QueueSystemBuild();
        }

        // The holes depend on the box, so the cave system builds again.
        private void OnValidate()
        {
            QueueSystemBuild();
        }
        #endregion

        /// <summary>Builds the cave system of the entrance again, with the entrance. Main thread.</summary>
        [ContextMenu("Rebuild")]
        public void Rebuild()
        {
            LoogaCaveSystem system = GetComponentInParent<LoogaCaveSystem>();
            if (system) system.Rebuild();
        }

        private void QueueSystemBuild()
        {
            LoogaCaveSystem system = GetComponentInParent<LoogaCaveSystem>();
            if (system) system.QueueBuild();
        }

        /// <summary>A built entrance as the generated mesh cache holds it.</summary>
        internal sealed class CacheEntry
        {
            public GeneratedMeshCache.RawMesh Mesh;
            public GeneratedMeshCache.RawMesh Collider;
            public Mesh LoadedMesh;
            public Mesh LoadedCollider;
            public string Terrain = string.Empty;
            public int Triangles;
            public Matrix4x4 Frame;
            public Vector3 BoxMin;
            public Vector3 BoxMax;

            public void Write(System.IO.BinaryWriter writer)
            {
                writer.Write(Mesh != null);
                if (Mesh == null) return;
                writer.Write(Terrain);
                writer.Write(Triangles);
                writer.Write(JsonUtility.ToJson(new FrameText { Frame = Frame, BoxMin = BoxMin, BoxMax = BoxMax }));
                Mesh.Write(writer);
                Collider.Write(writer);
            }

            public static CacheEntry Read(System.IO.BinaryReader reader)
            {
                var entry = new CacheEntry();
                if (!reader.ReadBoolean()) return entry;
                entry.Terrain = reader.ReadString();
                entry.Triangles = reader.ReadInt32();
                var frame = JsonUtility.FromJson<FrameText>(reader.ReadString());
                entry.Frame = frame.Frame;
                entry.BoxMin = frame.BoxMin;
                entry.BoxMax = frame.BoxMax;
                entry.LoadedMesh = GeneratedMeshCache.RawMesh.Read(reader);
                entry.LoadedCollider = GeneratedMeshCache.RawMesh.Read(reader);
                return entry;
            }

            [System.Serializable]
            private sealed class FrameText
            {
                public Matrix4x4 Frame;
                public Vector3 BoxMin;
                public Vector3 BoxMax;
            }
        }

        internal CacheEntry CacheRecord()
        {
            var entry = new CacheEntry();
            if (!_root || !_mesh) return entry;
            entry.Mesh = GeneratedMeshCache.RawMesh.From(_mesh);
            entry.Collider = GeneratedMeshCache.RawMesh.From(_root.GetComponent<MeshCollider>().sharedMesh);
            entry.Terrain = _groundTerrain ? _groundTerrain.name : string.Empty;
            entry.Triangles = TriangleCount;
            entry.Frame = _frame;
            entry.BoxMin = _boxMin;
            entry.BoxMax = _boxMax;
            return entry;
        }

        internal void Restore(CacheEntry entry, LoogaCaveSystem system, UnityEngine.Terrain[] terrains)
        {
            if (!entry.LoadedMesh) return;
            _mesh = entry.LoadedMesh;
            _frame = entry.Frame;
            _boxMin = entry.BoxMin;
            _boxMax = entry.BoxMax;
            TriangleCount = entry.Triangles;
            _groundTerrain = System.Array.Find(terrains, item => item.name == entry.Terrain);
            _root = new GameObject("Entrance " + name) { hideFlags = HideFlags.HideAndDontSave, layer = gameObject.layer };
            _root.transform.SetPositionAndRotation(_frame.GetColumn(3), _frame.rotation);
            _root.AddComponent<MeshFilter>().sharedMesh = _mesh;
            var meshRenderer = _root.AddComponent<MeshRenderer>();
            meshRenderer.shadowCastingMode = _shadowCasting;
            Material coverMaterial = _groundTerrain ? LoogaTerrainGroundBlock.SurfaceMeshMaterial(_groundTerrain) : null;
            meshRenderer.sharedMaterials = coverMaterial
                ? new[] { system.RockMaterial, coverMaterial }
                : new[] { system.RockMaterial };
            _groundBlock = _groundTerrain ? LoogaTerrainGroundBlock.Create(_groundTerrain) : new MaterialPropertyBlock();
            _groundBlock.SetFloat(CaveShadeId, 1f);
            meshRenderer.SetPropertyBlock(_groundBlock);
            _root.AddComponent<MeshCollider>().sharedMesh = entry.LoadedCollider;
            Built.Add(this);
            UploadBoxes();
        }

        private string ReloadKey => "LoogaSoft.Terrain.CaveEntrance." + GetInstanceID();

        // The output as the next domain reads it after a script reload. Root 0 is an entrance without a mesh.
        [System.Serializable]
        private sealed class ReloadState
        {
            public int Root;
            public int Mesh;
            public int Terrain;
            public int Triangles;
            public Matrix4x4 Frame;
            public Vector3 BoxMin;
            public Vector3 BoxMax;
        }

        // Stores the output for the next domain. Returns false when there is no editor.
        private bool StoreForReload()
        {
#if UNITY_EDITOR
            GeneratedReloadHandover.Store(ReloadKey, new ReloadState
            {
                Root = _root ? _root.GetInstanceID() : 0,
                Mesh = _mesh ? _mesh.GetInstanceID() : 0,
                Terrain = _groundTerrain ? _groundTerrain.GetInstanceID() : 0,
                Triangles = TriangleCount,
                Frame = _frame,
                BoxMin = _boxMin,
                BoxMax = _boxMax
            });
            return true;
#else
            return false;
#endif
        }

        /// <summary>
        /// Takes the output of the last domain after a script reload. Returns false when the system must build.
        /// </summary>
        internal bool AdoptAfterReload()
        {
#if UNITY_EDITOR
            if (!GeneratedReloadHandover.TryTake(ReloadKey, out ReloadState state)) return false;
            var root = GeneratedReloadHandover.Find<GameObject>(state.Root);
            var mesh = GeneratedReloadHandover.Find<Mesh>(state.Mesh);
            if (!GeneratedReloadHandover.CanAdopt || state.Root != 0 && (!root || !mesh))
            {
                if (root && root.TryGetComponent(out MeshCollider meshCollider) && meshCollider.sharedMesh)
                {
                    DestroyGenerated(meshCollider.sharedMesh);
                }
                if (root) DestroyGenerated(root);
                if (mesh) DestroyGenerated(mesh);
                return false;
            }
            if (state.Root == 0) return true;

            _root = root;
            _mesh = mesh;
            _frame = state.Frame;
            _boxMin = state.BoxMin;
            _boxMax = state.BoxMax;
            TriangleCount = state.Triangles;
            _groundTerrain = GeneratedReloadHandover.Find<UnityEngine.Terrain>(state.Terrain);
            _groundBlock = _groundTerrain ? LoogaTerrainGroundBlock.Create(_groundTerrain) : new MaterialPropertyBlock();
            _groundBlock.SetFloat(CaveShadeId, 1f);
            _root.GetComponent<MeshRenderer>().SetPropertyBlock(_groundBlock);
            Built.Add(this);
            UploadBoxes();
            return true;
#else
            return false;
#endif
        }

        // The object position with the yaw of its forward axis. The box is upright, so each column of the mesh grid
        // has one terrain height.
        private Matrix4x4 Frame()
        {
            Vector3 forward = transform.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 1e-6f) forward = Vector3.forward;
            return Matrix4x4.TRS(transform.position, Quaternion.LookRotation(forward.normalized, Vector3.up),
                Vector3.one);
        }

        #region Holes
        /// <summary>
        /// Adds the terrain texels of the box near the cave to the holes, for each terrain data. The band inside the box
        /// sides keeps its terrain, so the terrain and the entrance mesh overlap there, and so does ground higher than
        /// the top band. Only the smallest terrain at a place counts. Main thread.
        /// </summary>
        internal void CollectHoles(Dictionary<TerrainData, HashSet<int>> holes, LoogaCaveSystem system)
        {
            if (!isActiveAndEnabled) return;
            Matrix4x4 frame = Frame();
            Matrix4x4 toLocal = frame.inverse;
            Bounds footprint = FootprintBounds(frame);
            footprint.Expand(new Vector3(2f * _holeReach, 1e4f, 2f * _holeReach));
            CaveSegment[] segments = System.Array.FindAll(system.Segments, segment =>
                segment.BoundsMax.x >= footprint.min.x && segment.BoundsMin.x <= footprint.max.x &&
                segment.BoundsMax.z >= footprint.min.z && segment.BoundsMin.z <= footprint.max.z);
            float holeTop = frame.GetColumn(3).y + _height - _edgeBand;
            UnityEngine.Terrain[] terrains = UnityEngine.Terrain.activeTerrains;
            for (int index = 0; index < terrains.Length; index++)
            {
                UnityEngine.Terrain terrain = terrains[index];
                TerrainData data = terrain ? terrain.terrainData : null;
                if (!data) continue;
                Vector3 position = terrain.GetPosition();
                Vector3 size = data.size;
                int resolution = data.holesResolution;
                float texelX = size.x / resolution, texelZ = size.z / resolution;
                int x0 = Mathf.Max(0, Mathf.FloorToInt((footprint.min.x - position.x) / texelX));
                int x1 = Mathf.Min(resolution - 1, Mathf.CeilToInt((footprint.max.x - position.x) / texelX));
                int z0 = Mathf.Max(0, Mathf.FloorToInt((footprint.min.z - position.z) / texelZ));
                int z1 = Mathf.Min(resolution - 1, Mathf.CeilToInt((footprint.max.z - position.z) / texelZ));
                for (int z = z0; z <= z1; z++)
                {
                    for (int x = x0; x <= x1; x++)
                    {
                        var centre = new Vector3(position.x + (x + 0.5f) * texelX, 0f, position.z + (z + 0.5f) * texelZ);
                        if (!InHoleArea(toLocal.MultiplyPoint3x4(centre))) continue;
                        if (LoogaTerrainGroundBlock.FindTerrain(centre, terrains) != index) continue;
                        float height = terrain.SampleHeight(centre) + position.y;
                        if (height > holeTop) continue;
                        var surface = new float3(centre.x, height, centre.z);
                        if (CaveField.Evaluate(segments, surface, system.BumpSize, system.Roughness,
                                system.RoughnessScale) > _holeReach) continue;
                        if (!holes.TryGetValue(data, out HashSet<int> texels))
                        {
                            texels = new HashSet<int>();
                            holes.Add(data, texels);
                        }
                        texels.Add(z * resolution + x);
                    }
                }
            }
        }

        // True for a point (in frame space) inside the box footprint, deeper than the edge band.
        private bool InHoleArea(Vector3 local)
        {
            return Mathf.Abs(local.x) <= 0.5f * _width - _edgeBand && local.z >= -_inside + _edgeBand &&
                   local.z <= _outside - _edgeBand;
        }

        // World XZ bounds of the box footprint.
        private Bounds FootprintBounds(Matrix4x4 frame)
        {
            float half = 0.5f * _width;
            var bounds = new Bounds(frame.MultiplyPoint3x4(new Vector3(-half, 0f, -_inside)), Vector3.zero);
            bounds.Encapsulate(frame.MultiplyPoint3x4(new Vector3(half, 0f, -_inside)));
            bounds.Encapsulate(frame.MultiplyPoint3x4(new Vector3(-half, 0f, _outside)));
            bounds.Encapsulate(frame.MultiplyPoint3x4(new Vector3(half, 0f, _outside)));
            return bounds;
        }

        /// <summary>
        /// World bounds of the boxes of the active entrances, with the reach of the rays. Cliff walls and rock faces
        /// keep the CPU copy of their finest meshes there, so the entrances copy the full wall shape and not the
        /// coarser collider. Main thread.
        /// </summary>
        internal static List<Bounds> DetailAreas()
        {
            var areas = new List<Bounds>();
            foreach (LoogaCaveEntrance entrance in FindObjectsByType<LoogaCaveEntrance>(FindObjectsSortMode.None))
            {
                if (!entrance.isActiveAndEnabled) continue;
                float half = 0.5f * entrance._width;
                Bounds bounds = WorldBounds(entrance.Frame(), new Vector3(-half, -entrance._below, -entrance._inside),
                    new Vector3(half, entrance._height, entrance._outside));
                bounds.Expand(2f * (RayStart + entrance._cellSize));
                areas.Add(bounds);
            }
            return areas;
        }
        #endregion

        #region Build
        /// <summary>Builds the entrance mesh after the cave of the system. Main thread.</summary>
        internal void Build(LoogaCaveSystem system)
        {
            Release();
            if (!isActiveAndEnabled || !system) return;

            Matrix4x4 frame = Frame();
            float step = _cellSize;
            var samples = new int3(Mathf.CeilToInt(_width / step) + 1, 0, Mathf.CeilToInt((_inside + _outside) / step) + 1);
            var min = new Vector3(-0.5f * (samples.x - 1) * step, -_below, -_inside);
            float[] heights = ColumnHeights(frame, min, step, samples);
            samples.y = Mathf.CeilToInt((_height + _below) / step) + 1;
            Vector3 max = min + new Vector3(samples.x - 1, samples.y - 1, samples.z - 1) * step;

            List<MeshRenderer> wallRenderers = WallRenderers(WorldBounds(frame, min, max));
            using var wallColliders = new WallColliders(wallRenderers);
            var wallDepths = new float[samples.x * samples.y];
            CaptureWalls(frame, min, max, step, samples, wallColliders.Colliders, wallDepths);

            Bounds worldBox = WorldBounds(frame, min, max);
            var segments = new List<CaveSegment>();
            foreach (CaveSegment segment in system.Segments)
            {
                if (math.any(segment.BoundsMax < (float3)worldBox.min) ||
                    math.any(segment.BoundsMin > (float3)worldBox.max)) continue;
                segments.Add(segment);
            }

            int count = samples.x * samples.y * samples.z;
            var segmentArray = new NativeArray<CaveSegment>(segments.ToArray(), Allocator.TempJob);
            var heightArray = new NativeArray<float>(heights, Allocator.TempJob);
            var wallArray = new NativeArray<float>(wallDepths, Allocator.TempJob);
            var rock = new NativeArray<float>(count, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            var cellVertices = new NativeArray<int>((samples.x - 1) * (samples.y - 1) * (samples.z - 1),
                Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            var positions = new NativeList<float3>(16384, Allocator.TempJob);
            var normals = new NativeList<float3>(16384, Allocator.TempJob);
            var indices = new NativeList<int>(65536, Allocator.TempJob);
            try
            {
                JobHandle field = new EntranceFieldJob
                {
                    Segments = segmentArray, Heights = heightArray, WallDepths = wallArray, Rock = rock,
                    Frame = frame, Min = min, Max = max, Step = step, Samples = samples,
                    BumpScale = system.BumpSize, Roughness = system.Roughness,
                    RoughnessScale = system.RoughnessScale, Band = _edgeBand, Inset = EdgeInset
                }.Schedule(count, 1024);
                new EntranceMeshJob
                {
                    Rock = rock, Samples = samples, Min = min, Step = step, CellVertices = cellVertices,
                    Positions = positions, Normals = normals, Indices = indices
                }.Schedule(field).Complete();
                if (indices.Length == 0) return;
                CreateOutput(system, segments.ToArray(), frame, min, step, samples, heights, wallRenderers.Count > 0,
                    wallColliders.Colliders, positions.AsArray(), normals.AsArray(), indices.AsArray());
            }
            finally
            {
                segmentArray.Dispose();
                heightArray.Dispose();
                wallArray.Dispose();
                rock.Dispose();
                cellVertices.Dispose();
                positions.Dispose();
                normals.Dispose();
                indices.Dispose();
            }
            _frame = frame;
            _boxMin = min;
            _boxMax = max;
            Built.Add(this);
            UploadBoxes();
        }

        // Terrain height of each grid column (x, z).
        private static float[] ColumnHeights(Matrix4x4 frame, Vector3 min, float step, int3 samples)
        {
            Bounds bounds = WorldBounds(frame, min, min + new Vector3(samples.x - 1, 0f, samples.z - 1) * step);
            bounds.Expand(2f);
            var heights = new float[samples.x * samples.z];
            using var sampler = new TerrainHeightSampler(Rect.MinMaxRect(bounds.min.x, bounds.min.z, bounds.max.x,
                bounds.max.z));
            sampler.WaitForReads();
            for (int z = 0; z < samples.z; z++)
            {
                for (int x = 0; x < samples.x; x++)
                {
                    Vector3 point = frame.MultiplyPoint3x4(min + new Vector3(x, 0f, z) * step);
                    heights[z * samples.x + x] = sampler.TrySampleHeight(point.x, point.z, out float height)
                        ? height
                        : NoGround;
                }
            }
            return heights;
        }

        // The depth (frame z) of the first cliff wall or rock face surface along -forward at each grid point (x, y).
        // NoWall where the ray finds none, for example through the opening.
        private static void CaptureWalls(Matrix4x4 frame, Vector3 min, Vector3 max, float step, int3 samples,
            HashSet<Collider> walls, float[] depths)
        {
            Vector3 direction = -(Vector3)frame.GetColumn(2);
            float start = max.z + RayStart;
            float length = max.z - min.z + RayStart + 1f;
            for (int y = 0; y < samples.y; y++)
            {
                for (int x = 0; x < samples.x; x++)
                {
                    int index = y * samples.x + x;
                    depths[index] = NoWall;
                    if (walls.Count == 0) continue;
                    Vector3 origin = frame.MultiplyPoint3x4(new Vector3(min.x + x * step, min.y + y * step, start));
                    int count = Physics.RaycastNonAlloc(origin, direction, Hits, length, Physics.DefaultRaycastLayers,
                        QueryTriggerInteraction.Ignore);
                    int nearest = -1;
                    for (int hit = 0; hit < count; hit++)
                    {
                        Collider collider = Hits[hit].collider;
                        if (!collider || !walls.Contains(collider)) continue;
                        if (nearest < 0 || Hits[hit].distance < Hits[nearest].distance) nearest = hit;
                    }
                    if (nearest < 0) continue;
                    depths[index] = start - Hits[nearest].distance;
                }
            }
        }

        // True when a terrain hole texel lies within a margin (in texels) of the point, on the smallest terrain there.
        private static bool NearHole(Vector3 point, IReadOnlyDictionary<TerrainData, HashSet<int>> holes,
            UnityEngine.Terrain[] terrains, int margin)
        {
            int index = LoogaTerrainGroundBlock.FindTerrain(point, terrains);
            if (index < 0) return false;
            UnityEngine.Terrain terrain = terrains[index];
            TerrainData data = terrain.terrainData;
            if (!holes.TryGetValue(data, out HashSet<int> texels)) return false;
            int resolution = data.holesResolution;
            Vector3 position = terrain.GetPosition();
            int x = Mathf.FloorToInt((point.x - position.x) / data.size.x * resolution);
            int z = Mathf.FloorToInt((point.z - position.z) / data.size.z * resolution);
            for (int nearZ = Mathf.Max(0, z - margin); nearZ <= Mathf.Min(resolution - 1, z + margin); nearZ++)
            {
                for (int nearX = Mathf.Max(0, x - margin); nearX <= Mathf.Min(resolution - 1, x + margin); nearX++)
                {
                    if (texels.Contains(nearZ * resolution + nearX)) return true;
                }
            }
            return false;
        }

        // The finest renderers of the generated cliff walls and rock faces whose rock reaches the area.
        private static List<MeshRenderer> WallRenderers(Bounds area)
        {
            var renderers = new List<MeshRenderer>();
            foreach (LoogaCliffWall wall in FindObjectsByType<LoogaCliffWall>(FindObjectsSortMode.None))
            {
                if (wall.isActiveAndEnabled && wall.GeneratedBounds.Intersects(area))
                {
                    wall.GetGeneratedRenderers(0, renderers);
                }
            }
            foreach (LoogaRockFace face in FindObjectsByType<LoogaRockFace>(FindObjectsSortMode.None))
            {
                if (face.isActiveAndEnabled && face.GeneratedBounds.Intersects(area))
                {
                    face.GetGeneratedRenderers(0, renderers);
                }
            }
            return renderers;
        }

        // The wall surfaces that the rays hit: a temporary collider on each finest wall mesh that keeps its CPU copy,
        // else the collider of the chunk (level 1). The collider is the coarser shape: the entrance then lost the fine
        // ledges, and its flat tops took dust and no stain. Dispose removes the temporary colliders.
        private sealed class WallColliders : System.IDisposable
        {
            private readonly List<MeshCollider> _temporary = new();

            public HashSet<Collider> Colliders { get; } = new();

            public WallColliders(List<MeshRenderer> renderers)
            {
                foreach (MeshRenderer renderer in renderers)
                {
                    if (!renderer || !renderer.TryGetComponent(out MeshFilter filter)) continue;
                    Mesh mesh = filter.sharedMesh;
                    if (mesh && mesh.isReadable)
                    {
                        var collider = renderer.gameObject.AddComponent<MeshCollider>();
                        collider.hideFlags = HideFlags.HideAndDontSave;
                        collider.sharedMesh = mesh;
                        _temporary.Add(collider);
                        Colliders.Add(collider);
                    }
                    else if (renderer.transform.parent &&
                             renderer.transform.parent.TryGetComponent(out MeshCollider chunkCollider))
                    {
                        Colliders.Add(chunkCollider);
                    }
                }
                if (_temporary.Count > 0) Physics.SyncTransforms();
            }

            public void Dispose()
            {
                foreach (MeshCollider collider in _temporary)
                {
                    if (collider) DestroyImmediate(collider);
                }
                _temporary.Clear();
            }
        }

        // The cavity and terrain cover of the wall surfaces at the entrance vertices: the mesh values of the hit wall
        // mesh, blended over the hit triangle.
        private sealed class WallSurface
        {
            private readonly HashSet<Collider> _walls;
            private readonly Dictionary<Mesh, (Color32[] Colors, List<Vector4> Covers, int[] Triangles)> _meshes = new();

            public WallSurface(HashSet<Collider> walls)
            {
                _walls = walls;
            }

            // False when no wall surface lies within the reach along the normal of the point.
            public bool TryGet(Vector3 point, Vector3 normal, out float cavity, out Vector4 cover)
            {
                cavity = 0f;
                cover = Vector4.zero;
                if (_walls.Count == 0) return false;
                int count = Physics.RaycastNonAlloc(point + normal * WallColorReach, -normal, Hits, 2f * WallColorReach,
                    Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
                int nearest = -1;
                for (int hit = 0; hit < count; hit++)
                {
                    Collider collider = Hits[hit].collider;
                    if (!collider || !_walls.Contains(collider)) continue;
                    if (nearest < 0 || Hits[hit].distance < Hits[nearest].distance) nearest = hit;
                }
                if (nearest < 0) return false;
                RaycastHit found = Hits[nearest];
                if (found.triangleIndex < 0 || !(found.collider is MeshCollider meshCollider) ||
                    !meshCollider.sharedMesh || !meshCollider.sharedMesh.isReadable) return true;
                Mesh mesh = meshCollider.sharedMesh;
                if (!_meshes.TryGetValue(mesh, out (Color32[] Colors, List<Vector4> Covers, int[] Triangles) data))
                {
                    var covers = new List<Vector4>();
                    mesh.GetUVs(2, covers);
                    data = (mesh.colors32, covers, mesh.triangles);
                    _meshes.Add(mesh, data);
                }
                int corner = found.triangleIndex * 3;
                if (corner + 2 >= data.Triangles.Length) return true;
                int a = data.Triangles[corner], b = data.Triangles[corner + 1], c = data.Triangles[corner + 2];
                Vector3 weights = found.barycentricCoordinate;
                if (data.Colors.Length > 0)
                {
                    cavity = (data.Colors[a].g * weights.x + data.Colors[b].g * weights.y +
                              data.Colors[c].g * weights.z) / 255f;
                }
                if (data.Covers.Count > 0)
                {
                    cover = data.Covers[a] * weights.x + data.Covers[b] * weights.y + data.Covers[c] * weights.z;
                }
                return true;
            }
        }

        private static Bounds WorldBounds(Matrix4x4 frame, Vector3 min, Vector3 max)
        {
            var bounds = new Bounds(frame.MultiplyPoint3x4(min), Vector3.zero);
            for (int corner = 1; corner < 8; corner++)
            {
                var point = new Vector3((corner & 1) != 0 ? max.x : min.x, (corner & 2) != 0 ? max.y : min.y,
                    (corner & 4) != 0 ? max.z : min.z);
                bounds.Encapsulate(frame.MultiplyPoint3x4(point));
            }
            return bounds;
        }
        #endregion

        #region Output
        // The mesh vertex: position, normal with the show value in w (2 marks the entrance mesh, so the box does not
        // cut it), rock color, rock ground offset, terrain cover values and cave shade.
        [StructLayout(LayoutKind.Sequential)]
        private struct Vertex
        {
            public Vector3 Position;
            public Vector4 Normal;
            public Color32 Color;
            public Vector2 GroundOffset;
            public Vector4 Cover;
            public Vector2 Shade;
        }

        // rockFrame: cliff walls or rock faces frame the opening. Steep ground there is rock, as the walls beside it.
        // Without them, for example at a river bank, the terrain keeps its own look and only the cave cut is rock.
        private void CreateOutput(LoogaCaveSystem system, CaveSegment[] segments, Matrix4x4 frame, Vector3 min,
            float step, int3 samples, float[] heights, bool rockFrame, HashSet<Collider> wallColliders,
            NativeArray<float3> positions, NativeArray<float3> normals, NativeArray<int> indices)
        {
            Vector3 forward = frame.GetColumn(2);
            var walls = new WallSurface(wallColliders);
            ShadeLookup shade = ShadeLookup.FromCave(system, WorldBounds(frame, min,
                min + new Vector3(samples.x - 1, samples.y - 1, samples.z - 1) * step));
            var vertices = new Vertex[positions.Length];
            var covered = new bool[positions.Length];
            var kept = new bool[positions.Length];
            var plain = new bool[positions.Length];
            IReadOnlyDictionary<TerrainData, HashSet<int>> holes = system.AppliedHoles;
            UnityEngine.Terrain[] terrains = UnityEngine.Terrain.activeTerrains;
            for (int index = 0; index < positions.Length; index++)
            {
                Vector3 local = positions[index];
                Vector3 normal = frame.MultiplyVector(normals[index]);
                Vector3 world = frame.MultiplyPoint3x4(local);
                int column = Mathf.Clamp(Mathf.RoundToInt((local.x - min.x) / step), 0, samples.x - 1);
                int depth = Mathf.Clamp(Mathf.RoundToInt((local.z - min.z) / step), 0, samples.z - 1);
                float caveShade = shade.At(world);
                float facing = Mathf.SmoothStep(0f, 1f,
                    Mathf.InverseLerp(GroundFacingStart, GroundFacingFull, normal.y));
                float daylight = 1f - Mathf.SmoothStep(0f, 1f,
                    Mathf.InverseLerp(GroundShadeStart, GroundShadeEnd, caveShade));
                // Without a rock frame the terrain ground goes on into the opening on the walls too, as the terrain lip
                // of the cave: a natural mouth in a bank. A mined portal in a rock face keeps rock walls.
                float cover = (rockFrame ? facing : 1f) * daylight;
                float caveDistance = CaveField.Evaluate(segments, (float3)world, system.BumpSize, system.Roughness,
                    system.RoughnessScale);
                float terrain = heights[depth * samples.x + column];
                // Without a rock frame, a surface on intact terrain shows the terrain, also on the rim of a shaft.
                if (!rockFrame && (caveDistance > CaveSurfaceReach ||
                        Mathf.Abs(terrain - world.y) < OnTerrainReach && !NearHole(world, holes, terrains, 0)))
                {
                    cover = 1f;
                }
                // A floor under the rock shows the ground in front of the opening, not the ground above the rock.
                Vector2 offset = Vector2.zero;
                if (local.z < 0f && terrain - world.y > UnderRockDepth)
                {
                    Vector3 reach = forward * (-local.z + FloorGroundReach);
                    offset = new Vector2(reach.x, reach.z);
                }
                // On a wall surface the mesh shows the wall: its cavity and its ground cover, for example a soil cap.
                // The wall offset shows the ground beside the rock, not the rock layers of the terrain under it. Open
                // flat ground keeps its own cover where a buried wall foot has less, also a floor cut below the
                // terrain. Rock above the terrain takes only the wall cover: with the facing cover, the ledge tops of
                // the wall showed ground patches in the box and rock beside it.
                bool onWall = walls.TryGet(world, normal, out float cavity, out Vector4 wallCover);
                if (onWall)
                {
                    bool aboveGround = world.y > terrain + OnTerrainReach;
                    cover = aboveGround ? Mathf.Clamp01(wallCover.z) : Mathf.Max(cover, Mathf.Clamp01(wallCover.z));
                    offset = new Vector2(wallCover.x, wallCover.y);
                }
                if (rockFrame)
                {
                    // Only near the daylight: deeper in, the cave beyond the box has no such edge.
                    float edgeLight = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.05f, 0.25f, caveShade));
                    cavity = Mathf.Max(cavity, OpeningEdgeCavity * edgeLight *
                                               Mathf.Clamp01(1f - caveDistance / OpeningEdgeReach));
                }
                covered[index] = cover > 0.001f;
                kept[index] = onWall || caveDistance < CaveSurfaceReach || NearHole(world, holes, terrains, HoleMargin);
                // Ground fully covered by the terrain surface over intact terrain needs no rock below the cover: the
                // terrain is there. Rock there showed along the mesh edge, where the cover lies a little higher.
                plain[index] = !onWall && caveDistance > CaveSurfaceReach && cover >= 0.999f &&
                               !NearHole(world, holes, terrains, HoleRockMargin);
                // A floor of the cave shows the floor ground of the cave system through the rock material, as in the
                // passages beyond the box.
                float floorGround = onWall ? 0f : system.FloorWeight(normal.y);
                Vector2 floorOffset = system.FloorGround
                    ? new Vector2(system.FloorGround.position.x - world.x, system.FloorGround.position.z - world.z)
                    : Vector2.zero;
                vertices[index] = new Vertex
                {
                    Position = local,
                    Normal = new Vector4(normals[index].x, normals[index].y, normals[index].z, 2f),
                    Color = new Color32(0, (byte)(Mathf.Clamp01(cavity) * 255f), 0, (byte)(floorGround * 255f)),
                    GroundOffset = floorOffset,
                    Cover = new Vector4(offset.x, offset.y, cover + 2f, 1f),
                    Shade = new Vector2(caveShade, 0f)
                };
            }

            var rockList = new List<int>(indices.Length);
            var coverIndices = new List<int>();
            for (int index = 0; index + 2 < indices.Length; index += 3)
            {
                int a = indices[index], b = indices[index + 1], c = indices[index + 2];
                if (!kept[a] && !kept[b] && !kept[c]) continue;
                if (!plain[a] || !plain[b] || !plain[c])
                {
                    rockList.Add(a);
                    rockList.Add(b);
                    rockList.Add(c);
                }
                if (!covered[a] && !covered[b] && !covered[c]) continue;
                coverIndices.Add(a);
                coverIndices.Add(b);
                coverIndices.Add(c);
            }
            int[] rockIndices = rockList.ToArray();

            _mesh = new Mesh
            {
                name = "Cave entrance " + name, hideFlags = HideFlags.HideAndDontSave,
                indexFormat = vertices.Length > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16
            };
            _mesh.SetVertexBufferParams(vertices.Length, Layout);
            _mesh.SetVertexBufferData(vertices, 0, 0, vertices.Length);
            _mesh.subMeshCount = 2;
            _mesh.SetIndices(rockIndices, MeshTopology.Triangles, 0, false);
            _mesh.SetIndices(coverIndices, MeshTopology.Triangles, 1, false);
            _mesh.RecalculateBounds();
            TriangleCount = rockIndices.Length / 3;

            _root = new GameObject("Entrance " + name)
            {
                hideFlags = HideFlags.HideAndDontSave, layer = gameObject.layer
            };
            _root.transform.SetPositionAndRotation(frame.GetColumn(3), frame.rotation);
            _root.AddComponent<MeshFilter>().sharedMesh = _mesh;
            var meshRenderer = _root.AddComponent<MeshRenderer>();
            meshRenderer.shadowCastingMode = _shadowCasting;
            int terrainIndex = LoogaTerrainGroundBlock.FindTerrain(frame.GetColumn(3), terrains);
            Material coverMaterial = terrainIndex >= 0
                ? LoogaTerrainGroundBlock.SurfaceMeshMaterial(terrains[terrainIndex])
                : null;
            meshRenderer.sharedMaterials = coverMaterial
                ? new[] { system.RockMaterial, coverMaterial }
                : new[] { system.RockMaterial };
            MaterialPropertyBlock block = terrainIndex >= 0
                ? LoogaTerrainGroundBlock.Create(terrains[terrainIndex])
                : new MaterialPropertyBlock();
            block.SetFloat(CaveShadeId, 1f);
            meshRenderer.SetPropertyBlock(block);
            _groundBlock = block;
            _groundTerrain = terrainIndex >= 0 ? terrains[terrainIndex] : null;
            // Only the rock submesh: the cover triangles are copies of rock triangles.
            var colliderMesh = new Mesh
            {
                name = "Cave entrance collider " + name, hideFlags = HideFlags.HideAndDontSave,
                indexFormat = _mesh.indexFormat
            };
            colliderMesh.SetVertices(System.Array.ConvertAll(vertices, vertex => vertex.Position));
            colliderMesh.SetIndices(rockIndices, MeshTopology.Triangles, 0);
            _root.AddComponent<MeshCollider>().sharedMesh = colliderMesh;
        }

        // The cave shade of the nearest cave chunk vertex, so the entrance mesh darkens into the cave as the cave does.
        private sealed class ShadeLookup
        {
            private readonly Dictionary<long, List<(Vector3 Position, float Shade)>> _cells = new();

            public static ShadeLookup FromCave(LoogaCaveSystem system, Bounds area)
            {
                var lookup = new ShadeLookup();
                area.Expand(2f * ShadeReach);
                var vertices = new List<Vector3>();
                var shades = new List<Vector2>();
                foreach (Mesh mesh in system.ChunkMeshes)
                {
                    if (!mesh || !mesh.bounds.Intersects(area)) continue;
                    mesh.GetVertices(vertices);
                    mesh.GetUVs(3, shades);
                    if (shades.Count != vertices.Count) continue;
                    for (int index = 0; index < vertices.Count; index++)
                    {
                        Vector3 position = vertices[index];
                        if (!area.Contains(position)) continue;
                        long key = Key(Cell(position));
                        if (!lookup._cells.TryGetValue(key, out List<(Vector3, float)> list))
                        {
                            list = new List<(Vector3, float)>();
                            lookup._cells.Add(key, list);
                        }
                        list.Add((position, shades[index].x));
                    }
                }
                return lookup;
            }

            // 0 (daylight) where no cave vertex is near.
            public float At(Vector3 position)
            {
                int3 cell = Cell(position);
                float nearest = ShadeReach * ShadeReach;
                float shade = 0f;
                for (int z = -1; z <= 1; z++)
                {
                    for (int y = -1; y <= 1; y++)
                    {
                        for (int x = -1; x <= 1; x++)
                        {
                            if (!_cells.TryGetValue(Key(cell + new int3(x, y, z)), out List<(Vector3, float)> list))
                            {
                                continue;
                            }
                            foreach ((Vector3 point, float value) in list)
                            {
                                float distance = (point - position).sqrMagnitude;
                                if (distance >= nearest) continue;
                                nearest = distance;
                                shade = value;
                            }
                        }
                    }
                }
                return shade;
            }

            private static int3 Cell(Vector3 position)
            {
                return (int3)math.floor((float3)position / ShadeReach);
            }

            private static long Key(int3 cell)
            {
                return ((long)(cell.x & 0x1FFFFF) << 42) | ((long)(cell.y & 0x1FFFFF) << 21) | (long)(cell.z & 0x1FFFFF);
            }
        }

        // Sends the boxes of the built entrances to the shaders, which cut the other rock meshes and terrain covers
        // in them.
        private static void UploadBoxes()
        {
            var toBox = new Matrix4x4[MaximumBoxes];
            var halves = new Vector4[MaximumBoxes];
            var spheres = new Vector4[MaximumBoxes];
            // Without boxes the bounds hold no point.
            var all = new Bounds(new Vector3(0f, -1e6f, 0f), Vector3.zero);
            int count = 0;
            foreach (LoogaCaveEntrance entrance in Built)
            {
                if (count == MaximumBoxes)
                {
                    Debug.LogWarning("Looga cave entrances: more than " + MaximumBoxes + " entrances are built. The " +
                                     "rock in the others is not cut.", entrance);
                    break;
                }
                Vector3 centre = 0.5f * (entrance._boxMin + entrance._boxMax);
                toBox[count] = Matrix4x4.Translate(-centre) * entrance._frame.inverse;
                // The mesh has no quads in the outermost cell of the box, so the cut starts two cells inside it.
                Vector3 half = 0.5f * (entrance._boxMax - entrance._boxMin) - Vector3.one * (2f * entrance._cellSize);
                half = Vector3.Max(half, Vector3.one * 0.1f);
                halves[count] = new Vector4(half.x, half.y, half.z, entrance._edgeBand);
                Vector3 worldCentre = entrance._frame.MultiplyPoint3x4(centre);
                spheres[count] = new Vector4(worldCentre.x, worldCentre.y, worldCentre.z, half.magnitude);
                Bounds box = WorldBounds(entrance._frame, entrance._boxMin, entrance._boxMax);
                if (count == 0)
                {
                    all = box;
                }
                else
                {
                    all.Encapsulate(box);
                }
                count++;
            }
            Shader.SetGlobalMatrixArray(ToBoxId, toBox);
            Shader.SetGlobalVectorArray(HalfId, halves);
            Shader.SetGlobalVectorArray(SphereId, spheres);
            Shader.SetGlobalVector(BoundsMinId, all.min);
            Shader.SetGlobalVector(BoundsMaxId, all.max);
            Shader.SetGlobalFloat(CountId, count);
        }

        /// <summary>
        /// Makes the ground values again when the terrain destroyed its splat maps, for example in a scene save. The
        /// terrain cover showed black ground then. Main thread, each editor update.
        /// </summary>
        internal void RefreshDestroyedGround()
        {
            if (!_root || _groundBlock == null || !_groundTerrain) return;

            RefreshDestroyedCoverMaterial();
            if (LoogaTerrainGroundBlock.HasControlMaps(_groundBlock)) return;
            _groundBlock = LoogaTerrainGroundBlock.Create(_groundTerrain);
            _groundBlock.SetFloat(CaveShadeId, 1f);
            _root.GetComponent<MeshRenderer>().SetPropertyBlock(_groundBlock);
        }

        // A script reload destroys the surface mesh material of the cover submesh, and the generated root keeps the
        // destroyed reference (AdoptAfterReload). The empty slot drew magenta. This takes the current material again.
        private void RefreshDestroyedCoverMaterial()
        {
            var meshRenderer = _root.GetComponent<MeshRenderer>();
            meshRenderer.GetSharedMaterials(MaterialBuffer);
            if (MaterialBuffer.Count < 2 || MaterialBuffer[1]) return;

            Material cover = LoogaTerrainGroundBlock.SurfaceMeshMaterial(_groundTerrain);
            if (!cover) return;
            MaterialBuffer[1] = cover;
            meshRenderer.SetSharedMaterials(MaterialBuffer);
        }

        /// <summary>Removes the built mesh. The other meshes draw in the box again. Main thread.</summary>
        internal void Release()
        {
            if (_root)
            {
                if (_root.TryGetComponent(out MeshCollider meshCollider) && meshCollider.sharedMesh)
                {
                    DestroyGenerated(meshCollider.sharedMesh);
                }
                DestroyGenerated(_root);
            }
            if (_mesh) DestroyGenerated(_mesh);
            _root = null;
            _mesh = null;
            TriangleCount = 0;
            _groundBlock = null;
            _groundTerrain = null;
            if (Built.Remove(this)) UploadBoxes();
        }

        private static void DestroyGenerated(Object target)
        {
            if (Application.isPlaying) Object.Destroy(target);
            else Object.DestroyImmediate(target);
        }
        #endregion
    }

    /// <summary>
    /// The rock value at each sample of an entrance box: positive in the rock. The rock is the ground below the terrain
    /// heights and the rock behind the found wall surfaces, without the cave. In the edge band of the sides and the top
    /// the value is lower, so the surfaces lie a little behind the surfaces of the other meshes.
    /// </summary>
    [BurstCompile]
    internal struct EntranceFieldJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<CaveSegment> Segments;
        // Terrain height per grid column (x, z), and wall depth (frame z) per grid point (x, y).
        [ReadOnly] public NativeArray<float> Heights;
        [ReadOnly] public NativeArray<float> WallDepths;
        [WriteOnly] public NativeArray<float> Rock;
        public float4x4 Frame;
        public float3 Min;
        public float3 Max;
        public float Step;
        public int3 Samples;
        public float BumpScale;
        public float Roughness;
        public float RoughnessScale;
        public float Band;
        public float Inset;

        public void Execute(int index)
        {
            int x = index % Samples.x;
            int y = index / Samples.x % Samples.y;
            int z = index / (Samples.x * Samples.y);
            float3 local = Min + new float3(x, y, z) * Step;
            float3 world = math.transform(Frame, local);
            float ground = Heights[z * Samples.x + x] - world.y;
            float wallDepth = WallDepths[y * Samples.x + x];
            float wall = wallDepth > -5e3f ? wallDepth - local.z : wallDepth;
            float solid = math.max(ground, wall);
            float cave = Segments.Length > 0
                ? CaveField.Evaluate(Segments, world, BumpScale, Roughness, RoughnessScale)
                : float.MaxValue;
            float edge = math.min(math.min(local.x - Min.x, Max.x - local.x), math.min(local.z - Min.z, Max.z - local.z));
            edge = math.min(edge, Max.y - local.y);
            Rock[index] = math.min(cave, solid) - Inset * (1f - math.saturate(edge / Band));
        }
    }

    /// <summary>
    /// Surface nets over the rock of an entrance box, in frame space. Each cell where the rock starts or ends gets one
    /// vertex. Each inner sample edge with rock at one end gives one quad. The front faces away from the rock. The
    /// normals are the area-weighted face normals.
    /// </summary>
    [BurstCompile]
    internal struct EntranceMeshJob : IJob
    {
        [ReadOnly] public NativeArray<float> Rock;
        public int3 Samples;
        public float3 Min;
        public float Step;
        public NativeArray<int> CellVertices;
        public NativeList<float3> Positions;
        public NativeList<float3> Normals;
        public NativeList<int> Indices;

        public void Execute()
        {
            int3 cells = Samples - 1;
            for (int z = 0; z < cells.z; z++)
            {
                for (int y = 0; y < cells.y; y++)
                {
                    for (int x = 0; x < cells.x; x++)
                    {
                        PlaceVertex(x, y, z);
                    }
                }
            }
            AddQuads();
            AddNormals();
        }

        private int Sample(int x, int y, int z) => (z * Samples.y + y) * Samples.x + x;
        private int Cell(int x, int y, int z) => (z * (Samples.y - 1) + y) * (Samples.x - 1) + x;

        // The mean of the points where the cell edges cross the rock surface.
        private void PlaceVertex(int x, int y, int z)
        {
            int cell = Cell(x, y, z);
            CellVertices[cell] = -1;
            int inside = 0;
            for (int corner = 0; corner < 8; corner++)
            {
                if (Rock[Corner(x, y, z, corner)] > 0f) inside++;
            }
            if (inside == 0 || inside == 8) return;
            float3 sum = float3.zero;
            int crossings = 0;
            for (int edge = 0; edge < 12; edge++)
            {
                EdgeCorners(edge, out int a, out int b);
                float valueA = Rock[Corner(x, y, z, a)];
                float valueB = Rock[Corner(x, y, z, b)];
                if (valueA > 0f == valueB > 0f) continue;
                sum += math.lerp(CornerOffset(a), CornerOffset(b), valueA / (valueA - valueB));
                crossings++;
            }
            CellVertices[cell] = Positions.Length;
            Positions.Add(Min + (new float3(x, y, z) + sum / crossings) * Step);
            Normals.Add(float3.zero);
        }

        private int Corner(int x, int y, int z, int corner)
        {
            return Sample(x + (corner & 1), y + (corner >> 1 & 1), z + (corner >> 2 & 1));
        }

        private static float3 CornerOffset(int corner)
        {
            return new float3(corner & 1, corner >> 1 & 1, corner >> 2 & 1);
        }

        // The 12 cube edges as pairs of corner numbers (bit 0 = x, bit 1 = y, bit 2 = z).
        private static void EdgeCorners(int edge, out int a, out int b)
        {
            int axis = edge >> 2;
            int other = edge & 3;
            int low = axis == 0 ? other << 1 : axis == 1 ? (other & 1) | (other & 2) << 1 : other;
            a = low;
            b = low | 1 << axis;
        }

        private void AddQuads()
        {
            int3 last = Samples - 1;
            for (int z = 0; z < Samples.z; z++)
            {
                for (int y = 0; y < Samples.y; y++)
                {
                    for (int x = 0; x < Samples.x; x++)
                    {
                        float value = Rock[Sample(x, y, z)];
                        // An edge gives a quad only when the four cells around it are in the grid.
                        if (x < last.x && y >= 1 && z >= 1 && y < last.y && z < last.z)
                        {
                            AddQuad(value, Rock[Sample(x + 1, y, z)], Cell(x, y - 1, z - 1), Cell(x, y, z - 1),
                                Cell(x, y, z), Cell(x, y - 1, z));
                        }
                        if (y < last.y && x >= 1 && z >= 1 && x < last.x && z < last.z)
                        {
                            AddQuad(value, Rock[Sample(x, y + 1, z)], Cell(x - 1, y, z - 1), Cell(x - 1, y, z),
                                Cell(x, y, z), Cell(x, y, z - 1));
                        }
                        if (z < last.z && x >= 1 && y >= 1 && x < last.x && y < last.y)
                        {
                            AddQuad(value, Rock[Sample(x, y, z + 1)], Cell(x - 1, y - 1, z), Cell(x, y - 1, z),
                                Cell(x, y, z), Cell(x - 1, y, z));
                        }
                    }
                }
            }
        }

        private void AddQuad(float valueA, float valueB, int cell0, int cell1, int cell2, int cell3)
        {
            bool rockA = valueA > 0f;
            if (rockA == valueB > 0f) return;
            int v0 = CellVertices[cell0], v1 = CellVertices[cell1], v2 = CellVertices[cell2], v3 = CellVertices[cell3];
            if (v0 < 0 || v1 < 0 || v2 < 0 || v3 < 0) return;
            if (rockA)
            {
                AddTriangles(v0, v1, v2, v3);
            }
            else
            {
                AddTriangles(v0, v3, v2, v1);
            }
        }

        private void AddTriangles(int v0, int v1, int v2, int v3)
        {
            Indices.Add(v0); Indices.Add(v1); Indices.Add(v2);
            Indices.Add(v0); Indices.Add(v2); Indices.Add(v3);
        }

        private void AddNormals()
        {
            for (int index = 0; index < Indices.Length; index += 3)
            {
                int a = Indices[index], b = Indices[index + 1], c = Indices[index + 2];
                float3 normal = math.cross(Positions[b] - Positions[a], Positions[c] - Positions[a]);
                Normals[a] += normal;
                Normals[b] += normal;
                Normals[c] += normal;
            }
            for (int vertex = 0; vertex < Normals.Length; vertex++)
            {
                Normals[vertex] = math.normalizesafe(Normals[vertex], new float3(0f, 1f, 0f));
            }
        }
    }
}
