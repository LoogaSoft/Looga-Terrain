using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Splines;

namespace LoogaSoft.Terrain.Geology
{
    /// <summary>
    /// Ivy that grows over a wall from a spline along its top edge, for quarry faces, ruins and cliffs. Vines start in
    /// clumps: cascades start on the top surface and grow over the edge and down the face, climbers start at the foot
    /// of the wall and grow up. Each vine grows in small steps like the ivy generator of T. Luft (2007): its direction
    /// blends the previous direction, a random direction, an adhesion toward the nearest surface and a preferred
    /// direction (down for a cascade, up for a climber), and a vine that floats free of all surfaces for longer than a
    /// short distance dies. Vines branch, and leaves are separate cards along the stems. The meshes are split in
    /// chunks along the spline and saved in an asset next to the scene, so a load shows them without a new growth.
    /// The vines grow again only when the spline or a setting changes.
    /// </summary>
    /// <remarks>
    /// The vines find surfaces with physics rays. For the build, the visible level of detail of Looga rock faces, cliff
    /// walls and rock formations gets temporary colliders, so the vines touch the rock that shows. Each leaf has its own
    /// wind phase in the vertex alpha and its flutter weight in the vertex blue, from 0 at the stem to 1 at the tip,
    /// for a material with vertex wind masks (Looga Foliage). The stems have no wind. Build the vines again after the
    /// wall changes.
    /// </remarks>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [AddComponentMenu("Looga Terrain/Vines")]
    public sealed class LoogaVines : MonoBehaviour
    {
        // A face lower than this below the spline makes a wall that can have vines, in meters.
        private const float MinimumDrop = 1.5f;
        // Distance from the spline at which the ground of each side is measured, in meters.
        private const float SideProbe = 4f;
        // Time without input changes before the editor grows the vines again, in seconds.
        private const double RebuildDelay = 1.0;
        // Directions in which a vine node searches for the nearest surface.
        private static readonly Vector3[] SearchDirections =
        {
            Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back,
            new Vector3(1f, -1f, 1f).normalized, new Vector3(-1f, -1f, 1f).normalized,
            new Vector3(1f, -1f, -1f).normalized, new Vector3(-1f, -1f, -1f).normalized
        };

        [Header("Spline")]
        [Tooltip("Spline along the top edge of the wall. It can be open or closed.")]
        [SerializeField] private SplineContainer _spline;

        [Header("Clumps")]
        [Tooltip("Distance between two vine roots along the spline, in meters.")]
        [SerializeField, Min(0.1f)] private float _rootSpacing = 0.8f;
        [Tooltip("Share of the spline that has vines.")]
        [SerializeField, Range(0f, 1f)] private float _coverage = 0.45f;
        [Tooltip("Size of a vine clump along the spline, in meters.")]
        [SerializeField, Min(1f)] private float _clumpSize = 9f;
        [Tooltip("Share of the roots at the foot of the wall that climb up. The others cascade down from the top.")]
        [SerializeField, Range(0f, 1f)] private float _climberShare = 0.35f;
        [SerializeField] private int _seed = 1;

        [Header("Growth")]
        [Tooltip("Length range of one cascading vine, in meters.")]
        [SerializeField] private Vector2 _cascadeLength = new(3f, 14f);
        [Tooltip("Length range of one climbing vine, in meters.")]
        [SerializeField] private Vector2 _climbLength = new(2f, 9f);
        [Tooltip("Length of one growth step, in meters.")]
        [SerializeField, Min(0.05f)] private float _stepLength = 0.15f;
        [Tooltip("Weights of the previous direction, the random direction and the adhesion to the nearest surface.")]
        [SerializeField] private Vector3 _growthWeights = new(0.5f, 0.25f, 0.4f);
        [Tooltip("Weight of the preferred direction: down for a cascade, up for a climber. Without it, cascades " +
                 "wander along the top of the face.")]
        [SerializeField, Min(0f)] private float _tropismWeight = 0.35f;
        [Tooltip("A surface nearer than this pulls the vine to it, in meters.")]
        [SerializeField, Min(0.1f)] private float _adhesionDistance = 0.8f;
        [Tooltip("A vine that is free of all surfaces for this length dies, in meters.")]
        [SerializeField, Min(0f)] private float _maxFloatLength = 0.3f;
        [Tooltip("Chance for each growth step that a branch starts.")]
        [SerializeField, Range(0f, 0.3f)] private float _branchChance = 0.04f;
        [SerializeField, Range(0, 4)] private int _maxBranchDepth = 3;
        [Tooltip("Upper limit of all growth steps, so a large spline cannot take minutes.")]
        [SerializeField, Min(100)] private int _maxSteps = 250000;
        [SerializeField] private LayerMask _collisionMask = ~0;

        [Header("Shape")]
        [Tooltip("Stem width at the root and at the tip, in meters.")]
        [SerializeField] private Vector2 _stemWidth = new(0.035f, 0.012f);
        [Tooltip("Distance between a stem and the surface, in meters.")]
        [SerializeField, Min(0f)] private float _surfaceOffset = 0.02f;
        [Tooltip("Leaves for each meter of stem.")]
        [SerializeField, Min(0f)] private float _leafDensity = 22f;
        [Tooltip("Leaf size range, in meters.")]
        [SerializeField] private Vector2 _leafSize = new(0.14f, 0.3f);
        [Tooltip("Single leaf images in the atlas of the leaf material. The leaf stalk is at the bottom of a region.")]
        [SerializeField] private List<Rect> _leafRegions = new();

        [Header("Surface")]
        [SerializeField] private Material _leafMaterial;
        [SerializeField] private Material _stemMaterial;
        [SerializeField] private ShadowCastingMode _shadowCasting = ShadowCastingMode.On;

        [Header("Levels of Detail")]
        [Tooltip("Length of the spline part whose vines share one mesh, in meters. Each mesh culls alone.")]
        [SerializeField, Min(5f)] private float _chunkLength = 40f;

        // The grown vines and leaves (VineData). In the editor they are saved in a binary asset next to the scene, so a
        // load or a script reload builds the meshes without a new growth, and a player gets them from the build.
        [SerializeField, HideInInspector] private TextAsset _growth;
        // Hash of the inputs of the saved growth. The vines grow again only when the inputs change.
        [SerializeField, HideInInspector] private string _builtHash = "";

        // A growth that is not saved as an asset: in a player or in an unsaved scene.
        [NonSerialized] private byte[] _growthBytes;
        [NonSerialized] private readonly List<Mesh> _meshes = new();
        private GameObject _root;
        // Not kept over a script reload, so a build queued before the reload does not block new builds.
        [NonSerialized] private bool _buildQueued;
        [NonSerialized] private double _queuedTime;

        public SplineContainer Spline => _spline;
        public int VineCount { get; private set; }
        public int LeafCount { get; private set; }
        /// <summary>How the vines of the last build ended: full length, floated free, stalled, or step limit.</summary>
        public string GrowthReport { get; private set; } = "";

        private int _endedFull;
        private int _endedFloating;
        private int _endedStalled;

        // One grown vine: points on or near surfaces, the surface normal at each point, the depth of the branch and the
        // mesh chunk of its root.
        private sealed class Vine
        {
            public readonly List<Vector3> Points = new();
            public readonly List<Vector3> Normals = new();
            public int Depth;
            public int Chunk;
        }

        // One leaf: stalk point, tip and face directions, size, atlas region index and wind phase (0 to 255).
        private struct Leaf
        {
            public int Chunk;
            public Vector3 Stalk;
            public Vector3 Tip;
            public Vector3 Face;
            public float Size;
            public byte Region;
            public byte Phase;
        }

        // Compact binary form of a growth. Directions are 8-bit signed per axis, sizes 16-bit, so the leaves of a
        // quarry ring take a few megabytes. Positions stay 32-bit floats, so a far map position keeps its precision.
        private static class VineData
        {
            private const int Version = 1;

            public static byte[] Encode(List<Vine> vines, List<Leaf> leaves)
            {
                using var stream = new System.IO.MemoryStream();
                using var writer = new System.IO.BinaryWriter(stream);
                writer.Write(Version);
                writer.Write(vines.Count);
                foreach (Vine vine in vines)
                {
                    writer.Write((ushort)vine.Chunk);
                    writer.Write((byte)vine.Depth);
                    writer.Write(vine.Points.Count);
                    for (int index = 0; index < vine.Points.Count; index++)
                    {
                        WritePosition(writer, vine.Points[index]);
                        WriteDirection(writer, vine.Normals[index]);
                    }
                }
                writer.Write(leaves.Count);
                foreach (Leaf leaf in leaves)
                {
                    writer.Write((ushort)leaf.Chunk);
                    WritePosition(writer, leaf.Stalk);
                    WriteDirection(writer, leaf.Tip);
                    WriteDirection(writer, leaf.Face);
                    writer.Write(Mathf.FloatToHalf(leaf.Size));
                    writer.Write(leaf.Region);
                    writer.Write(leaf.Phase);
                }
                writer.Flush();
                return stream.ToArray();
            }

            public static bool Decode(byte[] bytes, out List<Vine> vines, out List<Leaf> leaves)
            {
                vines = new List<Vine>();
                leaves = new List<Leaf>();
                using var reader = new System.IO.BinaryReader(new System.IO.MemoryStream(bytes));
                if (bytes.Length < 8 || reader.ReadInt32() != Version) return false;
                int vineCount = reader.ReadInt32();
                for (int index = 0; index < vineCount; index++)
                {
                    var vine = new Vine { Chunk = reader.ReadUInt16(), Depth = reader.ReadByte() };
                    int points = reader.ReadInt32();
                    for (int point = 0; point < points; point++)
                    {
                        vine.Points.Add(ReadPosition(reader));
                        vine.Normals.Add(ReadDirection(reader));
                    }
                    vines.Add(vine);
                }
                int leafCount = reader.ReadInt32();
                leaves.Capacity = leafCount;
                for (int index = 0; index < leafCount; index++)
                {
                    leaves.Add(new Leaf
                    {
                        Chunk = reader.ReadUInt16(),
                        Stalk = ReadPosition(reader),
                        Tip = ReadDirection(reader),
                        Face = ReadDirection(reader),
                        Size = Mathf.HalfToFloat(reader.ReadUInt16()),
                        Region = reader.ReadByte(),
                        Phase = reader.ReadByte()
                    });
                }
                return true;
            }

            private static void WritePosition(System.IO.BinaryWriter writer, Vector3 position)
            {
                writer.Write(position.x);
                writer.Write(position.y);
                writer.Write(position.z);
            }

            private static Vector3 ReadPosition(System.IO.BinaryReader reader)
            {
                return new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            }

            private static void WriteDirection(System.IO.BinaryWriter writer, Vector3 direction)
            {
                writer.Write((sbyte)Mathf.RoundToInt(Mathf.Clamp(direction.x, -1f, 1f) * 127f));
                writer.Write((sbyte)Mathf.RoundToInt(Mathf.Clamp(direction.y, -1f, 1f) * 127f));
                writer.Write((sbyte)Mathf.RoundToInt(Mathf.Clamp(direction.z, -1f, 1f) * 127f));
            }

            private static Vector3 ReadDirection(System.IO.BinaryReader reader)
            {
                var direction = new Vector3(reader.ReadSByte(), reader.ReadSByte(), reader.ReadSByte()) / 127f;
                return direction.sqrMagnitude > 1e-6f ? direction.normalized : Vector3.up;
            }
        }

        // A vine that waits to grow: start point, start direction, surface normal, length, branch depth, the
        // preferred direction (down for a cascade, up for a climber) and the mesh chunk of its root.
        private readonly struct Sprout
        {
            public readonly Vector3 Position;
            public readonly Vector3 Direction;
            public readonly Vector3 Normal;
            public readonly float Length;
            public readonly int Depth;
            public readonly Vector3 Tropism;
            public readonly int Chunk;

            public Sprout(Vector3 position, Vector3 direction, Vector3 normal, float length, int depth,
                Vector3 tropism, int chunk)
            {
                Position = position;
                Direction = direction;
                Normal = normal;
                Length = length;
                Depth = depth;
                Tropism = tropism;
                Chunk = chunk;
            }
        }

        #region Built-in
        private void OnEnable()
        {
            UnityEngine.Splines.Spline.Changed += OnSplineChanged;
#if UNITY_EDITOR
            UnityEditor.EditorApplication.update += EditorTick;
#endif
            Show();
            // The editor checks the inputs in the next update. Walls build their colliders in OnEnable, so a growth
            // cannot run here.
            QueueBuild();
        }

        private void Start()
        {
            // A player has the saved growth. It grows the vines only when no growth was saved.
            if (Application.isPlaying && !HasGrowth()) Rebuild();
        }

        private void OnDisable()
        {
            UnityEngine.Splines.Spline.Changed -= OnSplineChanged;
#if UNITY_EDITOR
            UnityEditor.EditorApplication.update -= EditorTick;
#endif
            Release();
            DestroyMeshes();
        }

        // OnValidate must not create objects, so it only queues the build.
        private void OnValidate()
        {
            QueueBuild();
        }
        #endregion

        /// <summary>Sets the wall-top spline and builds again.</summary>
        public void SetSpline(SplineContainer spline)
        {
            _spline = spline;
            QueueBuild();
        }

        /// <summary>
        /// Grows the vines now, also when the inputs did not change, for example after the wall changed. Main thread.
        /// </summary>
        [ContextMenu("Rebuild")]
        public void Rebuild()
        {
            _buildQueued = false;
            Release();
            VineCount = 0;
            LeafCount = 0;
            if (!isActiveAndEnabled || !_spline || _spline.Spline == null || _spline.Spline.Count < 2) return;
            if (!_leafMaterial || _leafRegions.Count == 0) return;
            string hash = InputHash();

            var random = new System.Random(_seed);
            var vines = new List<Vine>();
            List<MeshCollider> contact = AddContactColliders();
            try
            {
                // Edit mode does not move colliders to their transforms before a query.
                Physics.SyncTransforms();
                var sprouts = new Queue<Sprout>();
                PlaceRoots(random, sprouts);
                int steps = 0;
                _endedFull = _endedFloating = _endedStalled = 0;
                while (sprouts.Count > 0 && steps < _maxSteps)
                {
                    Vine vine = Grow(sprouts.Dequeue(), random, sprouts, ref steps);
                    if (vine != null) vines.Add(vine);
                }
                GrowthReport = "full " + _endedFull + ", floated " + _endedFloating + ", stalled " + _endedStalled +
                               ", unstarted " + sprouts.Count + ", steps " + steps;
            }
            finally
            {
                foreach (MeshCollider collider in contact)
                {
                    Destroy(collider);
                }
            }
            var leaves = new List<Leaf>();
            foreach (Vine vine in vines)
            {
                AddLeaves(leaves, vine, random);
            }
            StoreGrowth(VineData.Encode(vines, leaves), hash);
            BuildMeshes();
            Show();
        }

        #region Saved growth
        private bool HasGrowth()
        {
            return _growth || _growthBytes != null;
        }

        private bool HasMeshes()
        {
            return _meshes.Count > 0;
        }

        // Builds the chunk meshes from the saved growth. This is fast, so a load needs no new growth.
        private void BuildMeshes()
        {
            DestroyMeshes();
            byte[] bytes = _growthBytes ?? (_growth ? _growth.bytes : null);
            if (bytes == null || _leafRegions.Count == 0) return;
            if (!VineData.Decode(bytes, out List<Vine> vines, out List<Leaf> leaves)) return;
            var builders = new SortedDictionary<int, VineMeshBuilder>();
            VineMeshBuilder Builder(int chunk)
            {
                if (builders.TryGetValue(chunk, out VineMeshBuilder builder)) return builder;
                builder = new VineMeshBuilder();
                builders.Add(chunk, builder);
                return builder;
            }
            foreach (Vine vine in vines)
            {
                float width = Mathf.Lerp(_stemWidth.x, _stemWidth.y, vine.Depth / (float)Mathf.Max(1, _maxBranchDepth));
                Builder(vine.Chunk).AddStem(vine, width, _stemWidth.y);
            }
            foreach (Leaf leaf in leaves)
            {
                Rect region = _leafRegions[Mathf.Min(leaf.Region, _leafRegions.Count - 1)];
                Builder(leaf.Chunk).AddLeaf(leaf.Stalk, leaf.Tip, leaf.Face, leaf.Size, region, leaf.Phase / 255f);
            }
            foreach (KeyValuePair<int, VineMeshBuilder> chunk in builders)
            {
                _meshes.Add(chunk.Value.ToMesh(name + " Vines " + chunk.Key));
            }
            VineCount = vines.Count;
            LeafCount = leaves.Count;
        }

        private void DestroyMeshes()
        {
            foreach (Mesh mesh in _meshes)
            {
                Destroy(mesh);
            }
            _meshes.Clear();
        }

        // One child object for each chunk mesh. The objects are not saved with the scene.
        private void Show()
        {
            if (_root || !_leafMaterial) return;
            if (!HasMeshes()) BuildMeshes();
            if (!HasMeshes()) return;
            _root = new GameObject("Vines " + name) { hideFlags = HideFlags.HideAndDontSave, layer = gameObject.layer };
            Material[] materials = _stemMaterial ? new[] { _leafMaterial, _stemMaterial } : new[] { _leafMaterial };
            foreach (Mesh mesh in _meshes)
            {
                var chunk = new GameObject(mesh.name)
                {
                    hideFlags = HideFlags.HideAndDontSave, layer = gameObject.layer
                };
                chunk.transform.SetParent(_root.transform, false);
                chunk.AddComponent<MeshFilter>().sharedMesh = mesh;
                var meshRenderer = chunk.AddComponent<MeshRenderer>();
                meshRenderer.sharedMaterials = materials;
                meshRenderer.shadowCastingMode = _shadowCasting;
            }
        }

        // The editor saves the growth in "<scene folder>/<scene name>/LoogaVines <object name>.bytes" (a binary text
        // asset). Without a saved scene, or in a player, the growth stays in memory.
        private void StoreGrowth(byte[] bytes, string hash)
        {
            _builtHash = hash;
            _growthBytes = bytes;
#if UNITY_EDITOR
            string scenePath = gameObject.scene.path;
            if (Application.isPlaying || string.IsNullOrEmpty(scenePath)) return;
            string parent = System.IO.Path.GetDirectoryName(scenePath)?.Replace('\\', '/');
            string folder = parent + "/" + gameObject.scene.name;
            if (!UnityEditor.AssetDatabase.IsValidFolder(folder))
            {
                UnityEditor.AssetDatabase.CreateFolder(parent, gameObject.scene.name);
            }
            string path = folder + "/LoogaVines " + name + ".bytes";
            System.IO.File.WriteAllBytes(path, bytes);
            UnityEditor.AssetDatabase.ImportAsset(path, UnityEditor.ImportAssetOptions.ForceUpdate);
            _growth = UnityEditor.AssetDatabase.LoadAssetAtPath<TextAsset>(path);
            if (_growth) _growthBytes = null;
            UnityEditor.EditorUtility.SetDirty(this);
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(gameObject.scene);
#endif
        }

        // The spline knots and all settings that change the growth or the mesh.
        private string InputHash()
        {
            var text = new System.Text.StringBuilder();
            foreach (BezierKnot knot in _spline.Spline)
            {
                text.Append(_spline.transform.TransformPoint(knot.Position)).Append(knot.TangentIn)
                    .Append(knot.TangentOut).Append(knot.Rotation);
            }
            text.Append(_spline.Spline.Closed).Append(_rootSpacing).Append(_coverage).Append(_clumpSize)
                .Append(_climberShare).Append(_seed).Append(_cascadeLength).Append(_climbLength).Append(_stepLength)
                .Append(_growthWeights).Append(_tropismWeight).Append(_adhesionDistance).Append(_maxFloatLength)
                .Append(_branchChance).Append(_maxBranchDepth).Append(_maxSteps).Append(_collisionMask.value)
                .Append(_stemWidth).Append(_surfaceOffset).Append(_leafDensity).Append(_leafSize).Append(_chunkLength)
                .Append(name);
            foreach (Rect region in _leafRegions)
            {
                text.Append(region);
            }
            return Hash128.Compute(text.ToString()).ToString();
        }
        #endregion

        #region Roots
        // Roots in noise clumps along the spline. A cascade root is on the top surface behind the edge and grows out
        // over it. A climber root is on the ground at the foot of the face and grows up.
        private void PlaceRoots(System.Random random, Queue<Sprout> sprouts)
        {
            float length = _spline.CalculateLength();
            int count = Mathf.Max(2, Mathf.CeilToInt(length / _rootSpacing));
            for (int index = 0; index < count; index++)
            {
                float t = Mathf.Clamp01((index + Range(random, 0f, 0.9f)) / count);
                Vector3 top = _spline.EvaluatePosition(t);
                Vector3 along = Flat(_spline.EvaluateTangent(t));
                if (along == Vector3.zero) continue;
                float clump = Mathf.PerlinNoise(top.x / _clumpSize + _seed * 0.37f, top.z / _clumpSize + 17.3f);
                // Perlin noise is rarely near 0 or 1, so the coverage maps to a window around the middle of its range.
                float threshold = Mathf.Lerp(0.72f, 0.28f, _coverage);
                float density = Mathf.InverseLerp(threshold - 0.04f, threshold + 0.1f, clump);
                if (random.NextDouble() >= density) continue;
                if (!FindFace(top, along, out Vector3 outward, out float foot)) continue;
                // Vines at the edge of a clump are shorter, so a clump has a ragged outline.
                float scale = Mathf.Lerp(0.6f, 1f, density);
                int chunk = Mathf.FloorToInt(t * length / _chunkLength);
                if (random.NextDouble() < _climberShare)
                {
                    if (!Physics.Raycast(new Vector3(top.x, foot + 1f, top.z) + outward * 1.5f, Vector3.down,
                            out RaycastHit ground, 3f, _collisionMask, QueryTriggerInteraction.Ignore)) continue;
                    float climb = Range(random, _climbLength.x, _climbLength.y) * scale;
                    sprouts.Enqueue(new Sprout(ground.point + ground.normal * _surfaceOffset,
                        (-outward + Vector3.up * 2f).normalized, ground.normal, climb, 0, Vector3.up, chunk));
                }
                else
                {
                    Vector3 back = top - outward * Range(random, 0.3f, 1.2f);
                    if (!Physics.Raycast(back + Vector3.up * 2f, Vector3.down, out RaycastHit surface, 4f,
                            _collisionMask, QueryTriggerInteraction.Ignore)) continue;
                    float cascade = Range(random, _cascadeLength.x, _cascadeLength.y) * scale;
                    sprouts.Enqueue(new Sprout(surface.point + surface.normal * _surfaceOffset, outward,
                        surface.normal, cascade, 0, Vector3.down, chunk));
                }
            }
        }

        // The face goes down on the side where the ground is lower, and it must drop at least MinimumDrop.
        private bool FindFace(Vector3 top, Vector3 along, out Vector3 outward, out float foot)
        {
            var left = new Vector3(-along.z, 0f, along.x);
            float leftGround = GroundBelow(top + left * SideProbe);
            float rightGround = GroundBelow(top - left * SideProbe);
            outward = leftGround < rightGround ? left : -left;
            foot = Mathf.Min(leftGround, rightGround);
            return foot < top.y - MinimumDrop;
        }

        private float GroundBelow(Vector3 point)
        {
            return Physics.Raycast(point + Vector3.up * 2f, Vector3.down, out RaycastHit hit, 300f, _collisionMask,
                QueryTriggerInteraction.Ignore)
                ? hit.point.y
                : float.NegativeInfinity;
        }
        #endregion

        #region Growth
        // Grows one vine in steps. Each step blends the previous direction, a random direction and the adhesion to the
        // nearest surface, moves, slides along a surface that it hits and then lies on the nearest surface. A vine
        // that floats for longer than the float length dies, so no vine hangs in the air.
        private Vine Grow(Sprout sprout, System.Random random, Queue<Sprout> sprouts, ref int steps)
        {
            var vine = new Vine { Depth = sprout.Depth, Chunk = sprout.Chunk };
            Vector3 position = sprout.Position;
            Vector3 direction = sprout.Direction;
            Vector3 normal = sprout.Normal;
            vine.Points.Add(position);
            vine.Normals.Add(normal);
            float grown = 0f;
            float floating = 0f;
            float weightSum = Mathf.Max(_growthWeights.x + _growthWeights.y + _growthWeights.z + _tropismWeight, 1e-4f);
            while (grown < sprout.Length && steps < _maxSteps)
            {
                steps++;
                bool attached = NearestSurface(position, normal, out Vector3 surfacePoint, out Vector3 surfaceNormal,
                    out float distance);
                Vector3 adhesion = attached
                    ? (surfacePoint - position).normalized * (1f - distance / _adhesionDistance)
                    : Vector3.zero;
                Vector3 randomDirection = RandomDirection(random);
                Vector3 blend = (direction * _growthWeights.x + randomDirection * _growthWeights.y +
                                 adhesion * _growthWeights.z + sprout.Tropism * _tropismWeight) / weightSum;
                if (attached)
                {
                    // On a surface the vine creeps along it: most of the part that goes into or away from the surface
                    // is removed.
                    blend -= surfaceNormal * (Vector3.Dot(blend, surfaceNormal) * 0.85f);
                    normal = surfaceNormal;
                }
                if (blend.sqrMagnitude < 1e-6f)
                {
                    _endedStalled++;
                    return vine.Points.Count >= 3 ? vine : null;
                }
                Vector3 step = blend.normalized * _stepLength;
                // A free vine sags with its float length.
                if (!attached)
                {
                    float sag = Mathf.Pow(floating / Mathf.Max(_maxFloatLength, 0.01f), 0.7f);
                    step += Vector3.down * (_stepLength * sag);
                }

                Vector3 next = position + step;
                if (Physics.Raycast(position, step.normalized, out RaycastHit hit, step.magnitude + _surfaceOffset,
                        _collisionMask, QueryTriggerInteraction.Ignore))
                {
                    // The vine slides along the surface that it hits.
                    Vector3 rest = next - hit.point;
                    next = hit.point + hit.normal * _surfaceOffset + (rest - hit.normal * Vector3.Dot(rest, hit.normal));
                    normal = hit.normal;
                    attached = true;
                }
                else if (NearestSurface(next, normal, out surfacePoint, out surfaceNormal, out distance) &&
                         distance < _stepLength * 1.5f)
                {
                    // Near a surface the vine lies on it, so it touches the rock and the ground.
                    next = surfacePoint + surfaceNormal * _surfaceOffset;
                    normal = surfaceNormal;
                    attached = true;
                }
                else
                {
                    attached = false;
                }

                float moved = Vector3.Distance(position, next);
                if (moved < 1e-4f)
                {
                    _endedStalled++;
                    return vine.Points.Count >= 3 ? vine : null;
                }
                floating = attached ? 0f : floating + moved;
                if (floating > _maxFloatLength)
                {
                    _endedFloating++;
                    return vine.Points.Count >= 3 ? vine : null;
                }
                direction = Vector3.Lerp(direction, (next - position) / moved, 0.5f).normalized;
                position = next;
                vine.Points.Add(position);
                vine.Normals.Add(normal);
                grown += moved;

                if (sprout.Depth < _maxBranchDepth && random.NextDouble() < _branchChance)
                {
                    // A branch leaves about 30 to 70 degrees to one side, in the surface plane.
                    float angle = Range(random, 30f, 70f) * (random.NextDouble() < 0.5 ? -1f : 1f);
                    Vector3 branchDirection = Quaternion.AngleAxis(angle, normal) * direction;
                    sprouts.Enqueue(new Sprout(position, branchDirection, normal,
                        (sprout.Length - grown) * Range(random, 0.4f, 0.8f), sprout.Depth + 1, sprout.Tropism,
                        sprout.Chunk));
                }
            }
            if (grown >= sprout.Length) _endedFull++;
            return vine.Points.Count >= 3 ? vine : null;
        }

        // The nearest surface point within the adhesion distance. The first ray goes against the last surface normal.
        private bool NearestSurface(Vector3 position, Vector3 normal, out Vector3 point, out Vector3 surfaceNormal,
            out float distance)
        {
            point = default;
            surfaceNormal = Vector3.up;
            distance = float.MaxValue;
            if (Physics.Raycast(position, -normal, out RaycastHit hit, _adhesionDistance, _collisionMask,
                    QueryTriggerInteraction.Ignore))
            {
                point = hit.point;
                surfaceNormal = hit.normal;
                distance = hit.distance;
            }
            foreach (Vector3 search in SearchDirections)
            {
                if (!Physics.Raycast(position, search, out hit, Mathf.Min(distance, _adhesionDistance), _collisionMask,
                        QueryTriggerInteraction.Ignore)) continue;
                point = hit.point;
                surfaceNormal = hit.normal;
                distance = hit.distance;
            }
            return distance < float.MaxValue;
        }

        // A random unit vector that leans a little up, as the ivy generator does for light.
        private static Vector3 RandomDirection(System.Random random)
        {
            var vector = new Vector3(Range(random, -1f, 1f), Range(random, -1f, 1f) + 0.2f, Range(random, -1f, 1f));
            return vector.sqrMagnitude > 1e-6f ? vector.normalized : Vector3.up;
        }
        #endregion

        #region Leaves
        // Leaves along the stem. A leaf stands out from the surface on its stalk and leans away from it, with a random
        // turn, size and atlas image. Leaves are denser away from the root, as on real ivy.
        private void AddLeaves(List<Leaf> leafList, Vine vine, System.Random random)
        {
            float travelled = 0f;
            float total = 0f;
            for (int index = 1; index < vine.Points.Count; index++)
            {
                total += Vector3.Distance(vine.Points[index - 1], vine.Points[index]);
            }
            for (int index = 1; index < vine.Points.Count; index++)
            {
                Vector3 start = vine.Points[index - 1];
                Vector3 end = vine.Points[index];
                float segment = Vector3.Distance(start, end);
                travelled += segment;
                float share = total > 0f ? travelled / total : 1f;
                float expected = segment * _leafDensity * Mathf.Lerp(0.5f, 1f, Mathf.Pow(share, 0.7f));
                int leaves = Mathf.FloorToInt(expected) + (random.NextDouble() < expected % 1f ? 1 : 0);
                Vector3 along = segment > 1e-5f ? (end - start) / segment : Vector3.down;
                Vector3 normal = vine.Normals[index];
                for (int leaf = 0; leaf < leaves; leaf++)
                {
                    Vector3 stalk = Vector3.Lerp(start, end, (float)random.NextDouble());
                    float size = Range(random, _leafSize.x, _leafSize.y) * Mathf.Lerp(0.7f, 1f, share);
                    // The leaf tip points away from the stem to one side, a little down, and the leaf faces out from
                    // the surface and up to the light.
                    Vector3 side = Vector3.Cross(normal, along).normalized * (random.NextDouble() < 0.5 ? -1f : 1f);
                    Vector3 tip = (side + along * Range(random, -0.5f, 0.5f) + Vector3.down * 0.4f + normal * 0.3f)
                        .normalized;
                    Vector3 face = (normal + Vector3.up * 0.5f + RandomDirection(random) * 0.35f).normalized;
                    face = (face - tip * Vector3.Dot(face, tip)).normalized;
                    if (face.sqrMagnitude < 1e-6f) continue;
                    leafList.Add(new Leaf
                    {
                        Chunk = vine.Chunk,
                        Stalk = stalk + normal * (size * 0.08f),
                        Tip = tip,
                        Face = face,
                        Size = size,
                        Region = (byte)random.Next(_leafRegions.Count),
                        Phase = (byte)random.Next(256)
                    });
                }
            }
        }
        #endregion

        #region Contact colliders
        // The visible level of detail of rock meshes can stand out from their colliders, so the vines would float in
        // front of the rock or go into it. Temporary colliders on that level let the vines touch the rock that shows.
        private List<MeshCollider> AddContactColliders()
        {
            var colliders = new List<MeshCollider>();
            var renderers = new List<MeshRenderer>();
            foreach (LoogaCliffWall wall in FindObjectsByType<LoogaCliffWall>(FindObjectsSortMode.None))
            {
                wall.GetGeneratedRenderers(0, renderers);
            }
            foreach (LoogaRockFace face in FindObjectsByType<LoogaRockFace>(FindObjectsSortMode.None))
            {
                face.GetGeneratedRenderers(0, renderers);
            }
            foreach (LoogaRockFormation formation in FindObjectsByType<LoogaRockFormation>(FindObjectsSortMode.None))
            {
                formation.GetGeneratedRenderers(0, renderers);
            }
            Bounds area = SplineBounds();
            foreach (MeshRenderer meshRenderer in renderers)
            {
                if (!meshRenderer || !meshRenderer.bounds.Intersects(area)) continue;
                var filter = meshRenderer.GetComponent<MeshFilter>();
                if (!filter || !filter.sharedMesh) continue;
                var collider = meshRenderer.gameObject.AddComponent<MeshCollider>();
                collider.sharedMesh = filter.sharedMesh;
                colliders.Add(collider);
            }
            return colliders;
        }

        private Bounds SplineBounds()
        {
            Spline spline = _spline.Spline;
            var bounds = new Bounds(_spline.EvaluatePosition(0f), Vector3.zero);
            int samples = Mathf.Max(16, spline.Count * 4);
            for (int index = 1; index <= samples; index++)
            {
                bounds.Encapsulate((Vector3)_spline.EvaluatePosition(index / (float)samples));
            }
            float reach = Mathf.Max(_cascadeLength.y, _climbLength.y) + SideProbe;
            bounds.Expand(new Vector3(reach * 2f, reach * 4f, reach * 2f));
            return bounds;
        }
        #endregion

        private void QueueBuild()
        {
            _buildQueued = true;
#if UNITY_EDITOR
            _queuedTime = UnityEditor.EditorApplication.timeSinceStartup;
#endif
        }

#if UNITY_EDITOR
        // A growth can take seconds, so the editor waits until the inputs stop changing, for example while a knot is
        // dragged. With unchanged inputs it only shows the saved meshes.
        private void EditorTick()
        {
            if (!_buildQueued || Application.isPlaying) return;
            if (UnityEditor.EditorApplication.timeSinceStartup - _queuedTime < RebuildDelay) return;
            _buildQueued = false;
            if (!isActiveAndEnabled || !_spline || _spline.Spline == null) return;
            if (HasGrowth() && InputHash() == _builtHash)
            {
                Show();
                return;
            }
            Rebuild();
        }
#endif

        private void OnSplineChanged(UnityEngine.Splines.Spline spline, int knotIndex, SplineModification modification)
        {
            if (Application.isPlaying || !_spline) return;
            foreach (UnityEngine.Splines.Spline own in _spline.Splines)
            {
                if (own == spline) QueueBuild();
            }
        }

        // Removes the shown objects. The meshes stay, so a later enable shows them again.
        private void Release()
        {
            Destroy(_root);
            _root = null;
        }

        private static void Destroy(UnityEngine.Object target)
        {
            if (!target) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(target);
            else UnityEngine.Object.DestroyImmediate(target);
        }

        private static Vector3 Flat(Vector3 vector)
        {
            vector.y = 0f;
            return vector.sqrMagnitude > 1e-8f ? vector.normalized : Vector3.zero;
        }

        private static float Range(System.Random random, float minimum, float maximum)
        {
            return minimum + (float)random.NextDouble() * (maximum - minimum);
        }

        // World-space vine mesh: submesh 0 holds the leaves, submesh 1 the stems. The vertex color is the wind mask of
        // Looga Foliage: red and green 0 (no trunk or branch sway), blue the leaf flutter weight and alpha the leaf
        // phase.
        private sealed class VineMeshBuilder
        {
            private readonly List<Vector3> _vertices = new();
            private readonly List<Vector3> _normals = new();
            private readonly List<Vector4> _tangents = new();
            private readonly List<Vector2> _uvs = new();
            private readonly List<Color> _colors = new();
            private readonly List<int> _leaves = new();
            private readonly List<int> _stems = new();

            // A ribbon that lies on the surface along the vine, narrower toward the tip.
            public void AddStem(Vine vine, float rootWidth, float tipWidth)
            {
                int count = vine.Points.Count;
                for (int index = 0; index < count; index++)
                {
                    Vector3 previous = vine.Points[Mathf.Max(index - 1, 0)];
                    Vector3 next = vine.Points[Mathf.Min(index + 1, count - 1)];
                    Vector3 along = (next - previous).normalized;
                    Vector3 normal = vine.Normals[index];
                    Vector3 across = Vector3.Cross(normal, along);
                    if (across.sqrMagnitude < 1e-6f) across = Vector3.Cross(Vector3.up, along);
                    across.Normalize();
                    float half = Mathf.Lerp(rootWidth, tipWidth, index / (float)(count - 1)) * 0.5f;
                    int first = _vertices.Count;
                    Add(vine.Points[index] - across * half, normal, along, new Vector2(0f, index), Color.clear);
                    Add(vine.Points[index] + across * half, normal, along, new Vector2(1f, index), Color.clear);
                    if (index == 0) continue;
                    _stems.Add(first - 2);
                    _stems.Add(first);
                    _stems.Add(first - 1);
                    _stems.Add(first - 1);
                    _stems.Add(first);
                    _stems.Add(first + 1);
                }
            }

            // One leaf card: the stalk end at the base, the tip along the tip direction, facing the face direction.
            public void AddLeaf(Vector3 stalk, Vector3 tip, Vector3 face, float size, Rect region, float phase)
            {
                Vector3 across = Vector3.Cross(face, tip).normalized * (size * 0.5f);
                Vector3 length = tip * size;
                // A slight cup: the tip bends back toward the surface.
                Vector3 bend = -face * (size * 0.12f);
                int first = _vertices.Count;
                var root = new Color(0f, 0f, 0f, phase);
                var end = new Color(0f, 0f, 1f, phase);
                Add(stalk - across, face, tip, new Vector2(region.xMin, region.yMin), root);
                Add(stalk + across, face, tip, new Vector2(region.xMax, region.yMin), root);
                Add(stalk + length - across + bend, face, tip, new Vector2(region.xMin, region.yMax), end);
                Add(stalk + length + across + bend, face, tip, new Vector2(region.xMax, region.yMax), end);
                _leaves.Add(first);
                _leaves.Add(first + 2);
                _leaves.Add(first + 1);
                _leaves.Add(first + 1);
                _leaves.Add(first + 2);
                _leaves.Add(first + 3);
            }

            private void Add(Vector3 position, Vector3 normal, Vector3 tangent, Vector2 uv, Color color)
            {
                _vertices.Add(position);
                _normals.Add(normal);
                _tangents.Add(new Vector4(tangent.x, tangent.y, tangent.z, 1f));
                _uvs.Add(uv);
                _colors.Add(color);
            }

            public Mesh ToMesh(string name)
            {
                var mesh = new Mesh
                {
                    name = name,
                    hideFlags = HideFlags.HideAndDontSave,
                    indexFormat = _vertices.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16,
                    subMeshCount = 2
                };
                mesh.SetVertices(_vertices);
                mesh.SetNormals(_normals);
                mesh.SetTangents(_tangents);
                mesh.SetUVs(0, _uvs);
                mesh.SetColors(_colors);
                mesh.SetTriangles(_leaves, 0);
                mesh.SetTriangles(_stems, 1);
                mesh.RecalculateBounds();
                return mesh;
            }
        }
    }
}
