using System.Collections.Generic;
using LoogaSoft.Terrain.Stamps;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Serialization;
using UnityEngine.Splines;
#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
#endif

namespace LoogaSoft.Terrain.Barriers
{
    /// <summary>Where the pieces of a run stand.</summary>
    public enum BarrierGround
    {
        /// <summary>On the terrain below the spline.</summary>
        Terrain,
        /// <summary>At the spline height, for example on rock or on a structure above the terrain.</summary>
        Spline
    }

    /// <summary>What a run places at one gate.</summary>
    public enum BarrierGateKind
    {
        /// <summary>The gate model of the set, with closed leaves.</summary>
        Closed,
        /// <summary>The gate model of the set, with open leaves.</summary>
        Open,
        /// <summary>A gap without a gate model, for example where a road crosses the run.</summary>
        Opening
    }

    /// <summary>One gate of a <see cref="LoogaBarrierRun"/>. The barrier set defines the gate model and width.</summary>
    [System.Serializable]
    public sealed class BarrierGatePlacement
    {
        [Tooltip("Index of the spline in the container.")]
        [Min(0)]
        [SerializeField] private int _spline;
        [Tooltip("Position of the gate centre along the spline. 0 = spline start, 1 = spline end.")]
        [Range(0f, 1f)]
        [SerializeField] private float _position = 0.5f;
        [Tooltip("Closed and Open place the gate model of the set. Opening leaves a gap without a gate model.")]
        [FormerlySerializedAs("_open")]
        [SerializeField] private BarrierGateKind _kind;
        [Tooltip("Width of an opening in meters. 0 = the gate width of the set. A gate model always uses the gate width of the set.")]
        [Min(0f)]
        [SerializeField] private float _width;

        public int Spline => _spline;
        public float Position => _position;
        public BarrierGateKind Kind => _kind;
        public float Width => _width;
    }

    /// <summary>
    /// Places the pieces of a <see cref="LoogaBarrierSet"/> along each spline of the container. Each spline is one
    /// section, so a space between two splines is an opening. A gate replaces the pieces that it covers, and posts stand
    /// at both sides of the gate. The pieces stand on the terrain.
    /// The generated objects are saved with the scene. The run builds them again after a spline edit, after an
    /// inspector edit, and after a stamp world changes the terrain under the run.
    /// </summary>
    [ExecuteAlways]
    [RequireComponent(typeof(SplineContainer))]
    public sealed class LoogaBarrierRun : MonoBehaviour
    {
        private const string GeneratedName = "Generated Barrier";
        // Gate posts nearer than this distance in meters are one post.
        private const float SharedPostDistance = 0.01f;

        // Distances along the spline of the two gate posts. End can be larger than the spline length on a closed spline.
        private readonly struct GateSpan
        {
            public readonly int Index;
            public readonly float Start;
            public readonly float End;
            public readonly BarrierGateKind Kind;

            public GateSpan(int index, float start, float end, BarrierGateKind kind)
            {
                Index = index;
                Start = start;
                End = end;
                Kind = kind;
            }
        }

        [SerializeField] private LoogaBarrierSet _set;
        [Tooltip("Seed of the piece selection and the jitter. Change it to get another piece mix.")]
        [SerializeField] private int _seed = 1;
        [Tooltip("Turns all pieces 180 degrees, so the other side faces the spline's right.")]
        [SerializeField] private bool _flip;
        [Tooltip("Builds again after a spline edit or a terrain change.")]
        [SerializeField] private bool _autoRebuild = true;
        [Tooltip("Places a joint and a post at every knot, and fits the pieces of each curve separately. " +
                 "Use it for sharp corners, so that no piece spans a corner.")]
        [SerializeField] private bool _jointsAtKnots;
        [Tooltip("Terrain: the pieces stand on the terrain. Spline: the pieces stand at the spline height, " +
                 "for example on rock or on a structure.")]
        [SerializeField] private BarrierGround _ground = BarrierGround.Terrain;
        [Tooltip("Gates along the splines. A gate replaces the pieces that it covers.")]
        [SerializeField] private List<BarrierGatePlacement> _gates = new();
        [SerializeField, HideInInspector] private Transform _generated;

        private readonly List<LoogaStampWorld> _worlds = new();
        private readonly List<Vector3> _gatePosts = new();
        private SplineContainer _container;
        private bool _rebuildRequested;

        /// <summary>Raised before a run removes its generated objects, also when it builds them again.</summary>
        public static event System.Action<LoogaBarrierRun> Rebuilding;
        /// <summary>Raised after a run placed its generated objects.</summary>
        public static event System.Action<LoogaBarrierRun> Rebuilt;

        public LoogaBarrierSet Set => _set;
        /// <summary>Parent of the generated pieces, posts and gates, or null before the first build.</summary>
        public Transform Generated => _generated;
        public int PieceCount { get; private set; }
        public int PostCount { get; private set; }
        public int GateCount { get; private set; }
        public IReadOnlyList<BarrierGatePlacement> Gates => _gates;
        public string Diagnostic { get; private set; }

        #region Built-in
        private void Reset()
        {
            _rebuildRequested = true;
        }

        private void OnEnable()
        {
            _container = GetComponent<SplineContainer>();
            Spline.Changed += OnSplineChanged;
            SubscribeWorlds();
        }

        private void OnDisable()
        {
            Spline.Changed -= OnSplineChanged;
            UnsubscribeWorlds();
        }

        // Spline edits, terrain changes, and the inspector set the flag. Update coalesces bursts of edits.
        private void Update()
        {
            if (_rebuildRequested)
            {
                Rebuild();
            }
        }
        #endregion

        #region Build
        /// <summary>Asks for a build on the next update. Use it after a change to the set asset.</summary>
        public void RequestRebuild()
        {
            _rebuildRequested = true;
#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                EditorApplication.QueuePlayerLoopUpdate();
            }
#endif
        }

        /// <summary>Removes the generated objects and places the pieces again.</summary>
        public void Rebuild()
        {
            _rebuildRequested = false;
            Clear();
            Diagnostic = null;
            if (!_container)
            {
                _container = GetComponent<SplineContainer>();
            }
            if (!_set)
            {
                Diagnostic = "Assign a barrier set.";
                return;
            }

            _generated = new GameObject(GeneratedName).transform;
            _generated.SetParent(transform, false);
            var random = new System.Random(_seed);
            for (int splineIndex = 0; splineIndex < _container.Splines.Count; splineIndex++)
            {
                BuildSection(splineIndex, random);
            }

            if (PieceCount == 0 && string.IsNullOrEmpty(Diagnostic))
            {
                Diagnostic = "No piece was placed. Check that the set has pieces with prefabs and that the splines are long enough.";
            }
            MarkSceneDirty();
            Rebuilt?.Invoke(this);
        }

        /// <summary>Removes the generated objects.</summary>
        public void Clear()
        {
            Rebuilding?.Invoke(this);
            if (!_generated)
            {
                // A copy of the run or a lost reference can leave the generated child without a reference.
                _generated = transform.Find(GeneratedName);
            }
            if (_generated)
            {
                DestroyGenerated(_generated.gameObject);
            }
            _generated = null;
            PieceCount = 0;
            PostCount = 0;
            GateCount = 0;
        }

        private void BuildSection(int splineIndex, System.Random random)
        {
            Spline spline = _container.Splines[splineIndex];
            if (spline.Count < 2)
                return;

            float length = spline.GetLength();
            List<float> knots = _jointsAtKnots ? KnotDistances(spline) : null;
            List<GateSpan> gates = FindGateSpans(splineIndex, spline, length);
            if (gates.Count == 0)
            {
                if (spline.Closed && knots == null)
                {
                    BuildInterval(splineIndex, spline, length, 0f, length, true, true, true, random);
                }
                else
                {
                    // A closed spline ends at its first knot, so the end gets no post of its own.
                    BuildRange(splineIndex, spline, length, 0f, length, true, !spline.Closed, knots, random);
                }
                return;
            }

            _gatePosts.Clear();
            foreach (GateSpan gate in gates)
            {
                PlaceGate(splineIndex, spline, length, gate);
            }

            // The gates divide the spline into ranges. The gates place the posts at the range ends next to them.
            GateSpan first = gates[0];
            GateSpan last = gates[gates.Count - 1];
            if (!spline.Closed)
            {
                BuildRange(splineIndex, spline, length, 0f, first.Start, true, false, knots, random);
            }
            for (int i = 0; i < gates.Count - 1; i++)
            {
                BuildRange(splineIndex, spline, length, gates[i].End, gates[i + 1].Start, false, false, knots, random);
            }
            if (spline.Closed)
            {
                BuildRange(splineIndex, spline, length, last.End, first.Start + length, false, false, knots, random);
            }
            else
            {
                BuildRange(splineIndex, spline, length, last.End, length, false, true, knots, random);
            }
        }

        // Fills the spline from one distance to another. With knot distances, each part between two knots is a separate
        // interval, so that a joint and a post stand at each knot.
        private void BuildRange(int splineIndex, Spline spline, float splineLength, float from, float to, bool postAtStart,
            bool postAtEnd, List<float> knots, System.Random random)
        {
            var cuts = new List<float>();
            if (knots != null)
            {
                foreach (float knot in knots)
                {
                    // On a closed spline, a range can continue past the spline end.
                    AddCut(cuts, knot, from, to);
                    if (spline.Closed)
                    {
                        AddCut(cuts, knot + splineLength, from, to);
                    }
                }
                cuts.Sort();
            }

            float start = from;
            bool startPost = postAtStart;
            foreach (float cut in cuts)
            {
                BuildInterval(splineIndex, spline, splineLength, start, cut, false, startPost, false, random);
                start = cut;
                startPost = true;
            }
            BuildInterval(splineIndex, spline, splineLength, start, to, false, startPost, postAtEnd, random);
        }

        private static void AddCut(List<float> cuts, float distance, float from, float to)
        {
            // A knot at a range end is not a cut. The range end already has a joint.
            if (distance > from + SharedPostDistance && distance < to - SharedPostDistance)
            {
                cuts.Add(distance);
            }
        }

        // Distance along the spline of the start knot of each curve.
        private static List<float> KnotDistances(Spline spline)
        {
            var distances = new List<float>();
            float distance = 0f;
            int curves = spline.Closed ? spline.Count : spline.Count - 1;
            for (int i = 0; i < curves; i++)
            {
                distances.Add(distance);
                distance += spline.GetCurveLength(i);
            }
            return distances;
        }

        // Fills the spline from one distance to another with pieces and posts.
        // A loop is a closed spline without gates, so its last joint is its first joint.
        private void BuildInterval(int splineIndex, Spline spline, float splineLength, float from, float to, bool loop,
            bool postAtStart, bool postAtEnd, System.Random random)
        {
            float length = to - from;
            float step = _set.Step;
            int segments = _set.StretchToFit ? Mathf.RoundToInt(length / step) : Mathf.FloorToInt(length / step);
            // A short interval gets one compressed piece. A very short interval next to a gate stays open.
            if (segments == 0 && _set.StretchToFit && length >= step * 0.25f)
            {
                segments = 1;
            }
            if (segments < 1)
                return;

            float segmentLength = _set.StretchToFit ? length / segments : step;
            var joints = new Vector3[segments + 1];
            for (int i = 0; i <= segments; i++)
            {
                joints[i] = EvaluateDistance(splineIndex, spline, splineLength, from + i * segmentLength);
            }

            for (int i = 0; i < segments; i++)
            {
                PlacePiece(joints[i], joints[i + 1], random);
            }

            if (!_set.Post)
                return;

            // Without stretch the last joint is before the interval end, so it always gets a post.
            int lastPost = loop || (!postAtEnd && _set.StretchToFit) ? segments - 1 : segments;
            for (int i = postAtStart ? 0 : 1; i <= lastPost; i++)
            {
                Vector3 previous = joints[Mathf.Max(0, i - 1)];
                Vector3 next = joints[Mathf.Min(segments, i + 1)];
                PlacePost(joints[i], next - previous);
            }
        }

        // Converts each gate of this spline to post distances. Skips the gates that do not fit or that overlap.
        private List<GateSpan> FindGateSpans(int splineIndex, Spline spline, float length)
        {
            var spans = new List<GateSpan>();
            for (int i = 0; i < _gates.Count; i++)
            {
                BarrierGatePlacement gate = _gates[i];
                if (gate == null || gate.Spline != splineIndex)
                {
                    continue;
                }

                float width = UsesGateModel(gate.Kind) || gate.Width <= 0f ? _set.GateSpan : gate.Width;

                float start = gate.Position * length - width * 0.5f;
                if (!spline.Closed)
                {
                    start = Mathf.Clamp(start, 0f, Mathf.Max(0f, length - width));
                }
                // A gate model is straight, so its posts are one gate width apart in a straight line. An opening
                // follows the spline, also around corners.
                float end = UsesGateModel(gate.Kind) ? FindGateEnd(splineIndex, spline, length, start, width) : start + width;
                if (float.IsNaN(end) || end - start >= length || (!spline.Closed && end > length + 1e-3f))
                {
                    AddDiagnostic("Gate " + i + " is wider than its spline.");
                    continue;
                }
                spans.Add(new GateSpan(i, start, end, gate.Kind));
            }
            spans.Sort((a, b) => a.Start.CompareTo(b.Start));

            for (int i = 1; i < spans.Count; i++)
            {
                if (spans[i].Start < spans[i - 1].End - SharedPostDistance)
                {
                    AddDiagnostic("Gate " + spans[i].Index + " overlaps gate " + spans[i - 1].Index + ".");
                    spans.RemoveAt(i);
                    i--;
                }
            }
            // On a closed spline, the last gate can also reach the first gate.
            if (spline.Closed && spans.Count > 1 && spans[^1].End > spans[0].Start + length + SharedPostDistance)
            {
                AddDiagnostic("Gate " + spans[^1].Index + " overlaps gate " + spans[0].Index + ".");
                spans.RemoveAt(spans.Count - 1);
            }
            return spans;
        }

        // Finds the distance along the spline where the straight gate ends. Returns NaN when the gate does not fit.
        // The gate stays level, so the width is a horizontal distance.
        private float FindGateEnd(int splineIndex, Spline spline, float length, float start, float width)
        {
            float limit = spline.Closed ? start + length : length + 1e-3f;
            Vector3 origin = EvaluateDistance(splineIndex, spline, length, start);
            // The arc is never shorter than the straight line, so the end is at start + width or later.
            float near = start;
            float far = start + width;
            if (far > limit)
                return float.NaN;

            float step = width * 0.1f;
            while (FlatDistance(origin, EvaluateDistance(splineIndex, spline, length, far)) < width)
            {
                near = far;
                far += step;
                if (far > limit)
                    return float.NaN;
            }

            for (int i = 0; i < 16; i++)
            {
                float middle = (near + far) * 0.5f;
                if (FlatDistance(origin, EvaluateDistance(splineIndex, spline, length, middle)) < width)
                {
                    near = middle;
                }
                else
                {
                    far = middle;
                }
            }
            return far;
        }

        private void PlaceGate(int splineIndex, Spline spline, float length, GateSpan span)
        {
            Vector3 start = EvaluateDistance(splineIndex, spline, length, span.Start);
            Vector3 end = EvaluateDistance(splineIndex, spline, length, span.End);
            Vector3 flat = end - start;
            flat.y = 0f;
            if (flat.sqrMagnitude < 1e-8f)
                return;

            GateCount++;
            bool model = UsesGateModel(span.Kind);
            if (model)
            {
                // The gate stays level at the lower post, like a piece in Step mode.
                Quaternion rotation = Quaternion.LookRotation(flat.normalized) * Quaternion.Euler(0f, _flip ? 180f : 0f, 0f);
                Vector3 centre = (start + end) * 0.5f;
                float centreGround = GroundHeight(centre);
                centre.y = Mathf.Min(GroundHeight(start), GroundHeight(end));
                Transform gate = Spawn(_set.Gate, centre + rotation * (_set.GateOffset * _set.Scale), rotation,
                    Vector3.one * _set.Scale);
                LoogaBarrierGate leaves = gate.GetComponent<LoogaBarrierGate>();
                if (leaves)
                {
                    StepLeaves(leaves, centre.y, centreGround);
                    if (span.Kind == BarrierGateKind.Open)
                    {
                        leaves.Open(_set.GateOpenAngle);
                    }
                }
                AddGateColliders(gate, leaves);
            }

            // A gate model with its own posts needs no set posts at its edges.
            if (!_set.Post || (model && _set.GateHasPosts))
                return;

            PlaceGatePost(start, flat);
            PlaceGatePost(end, flat);
        }

        // On a slope each leaf steps up to the lower end of its own span, from its hinge to the gate centre.
        // This keeps the upper leaf out of the ground and leaves no gap below the leaves.
        private void StepLeaves(LoogaBarrierGate leaves, float gateHeight, float centreGround)
        {
            foreach (Transform hinge in leaves.Hinges)
            {
                if (!hinge)
                {
                    continue;
                }

                float lift = Mathf.Min(GroundHeight(hinge.position), centreGround) - gateHeight;
                if (lift > 0f)
                {
                    hinge.position += Vector3.up * lift;
                }
            }
        }

        private bool UsesGateModel(BarrierGateKind kind)
        {
            return kind != BarrierGateKind.Opening && _set.Gate;
        }

        // Two gates next to each other share one post.
        private void PlaceGatePost(Vector3 position, Vector3 direction)
        {
            foreach (Vector3 placed in _gatePosts)
            {
                if ((placed - position).sqrMagnitude < SharedPostDistance * SharedPostDistance)
                    return;
            }
            _gatePosts.Add(position);
            PlacePost(position, direction);
        }

        private Vector3 EvaluateDistance(int splineIndex, Spline spline, float length, float distance)
        {
            if (spline.Closed && length > 0f)
            {
                distance = Mathf.Repeat(distance, length);
            }
            float t = spline.ConvertIndexUnit(distance, PathIndexUnit.Distance, PathIndexUnit.Normalized);
            return _container.EvaluatePosition(splineIndex, math.saturate(t));
        }

        private static float FlatDistance(Vector3 a, Vector3 b)
        {
            a.y = 0f;
            b.y = 0f;
            return Vector3.Distance(a, b);
        }

        private void AddDiagnostic(string message)
        {
            Diagnostic = string.IsNullOrEmpty(Diagnostic) ? message : Diagnostic + "\n" + message;
        }

        private void PlacePiece(Vector3 start, Vector3 end, System.Random random)
        {
            GameObject prefab = _set.PickPiece(random);
            float jitter = ((float)random.NextDouble() * 2f - 1f) * _set.YawJitter;
            if (!prefab)
                return;

            Vector3 flat = end - start;
            flat.y = 0f;
            float flatLength = flat.magnitude;
            if (flatLength < 1e-4f)
                return;

            float startHeight = GroundHeight(start);
            float endHeight = GroundHeight(end);
            Quaternion rotation;
            float height;
            if (_set.SlopeMode == BarrierSlopeMode.Follow)
            {
                float pitch = Mathf.Clamp(Mathf.Atan2(endHeight - startHeight, flatLength) * Mathf.Rad2Deg, -_set.MaximumPitch, _set.MaximumPitch);
                rotation = Quaternion.LookRotation(flat / flatLength) * Quaternion.Euler(-pitch, 0f, 0f);
                height = (startHeight + endHeight) * 0.5f;
            }
            else
            {
                rotation = Quaternion.LookRotation(flat / flatLength);
                height = Mathf.Min(startHeight, endHeight);
            }
            rotation *= Quaternion.Euler(0f, jitter + (_flip ? 180f : 0f), 0f);

            Vector3 centre = (start + end) * 0.5f;
            centre.y = height;
            float alongScale = _set.StretchToFit ? (end - start).magnitude / _set.Step : 1f;
            Transform piece = Spawn(prefab, centre + rotation * (_set.PieceOffset * _set.Scale), rotation,
                new Vector3(_set.Scale, _set.Scale, _set.Scale * alongScale));
            AddColliders(piece);
            PieceCount++;
        }

        private void PlacePost(Vector3 joint, Vector3 direction)
        {
            direction.y = 0f;
            if (direction.sqrMagnitude < 1e-8f)
                return;

            Quaternion rotation = Quaternion.LookRotation(direction.normalized) * Quaternion.Euler(0f, _flip ? 180f : 0f, 0f);
            joint.y = GroundHeight(joint);
            Transform post = Spawn(_set.Post, joint + rotation * (_set.PostOffset * _set.Scale), rotation, Vector3.one * _set.Scale);
            AddColliders(post);
            PostCount++;
        }

        // The editor keeps the prefab link, so changes to the model reach the placed pieces.
        private Transform Spawn(GameObject prefab, Vector3 position, Quaternion rotation, Vector3 scale)
        {
            GameObject instance = null;
#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, _generated);
            }
#endif
            if (!instance)
            {
                instance = Instantiate(prefab, _generated);
            }

            Transform result = instance.transform;
            result.SetPositionAndRotation(position, rotation);
            result.localScale = scale;
            return result;
        }

        private void AddColliders(Transform root)
        {
            if (_set.Colliders == BarrierColliderMode.Mesh)
            {
                AddMeshColliders(root);
            }
            else if (_set.Colliders == BarrierColliderMode.Box)
            {
                AddBox(root, root.GetComponentsInChildren<MeshFilter>());
            }
        }

        // In Box mode each leaf gets its own box, so the box turns with an open leaf.
        private void AddGateColliders(Transform root, LoogaBarrierGate leaves)
        {
            if (_set.Colliders != BarrierColliderMode.Box || !leaves)
            {
                AddColliders(root);
                return;
            }

            var frame = new List<MeshFilter>(root.GetComponentsInChildren<MeshFilter>());
            foreach (Transform hinge in leaves.Hinges)
            {
                if (!hinge)
                {
                    continue;
                }

                MeshFilter[] leaf = hinge.GetComponentsInChildren<MeshFilter>();
                frame.RemoveAll(filter => System.Array.IndexOf(leaf, filter) >= 0);
                AddBox(hinge, leaf);
            }
            AddBox(root, frame);
        }

        private static void AddMeshColliders(Transform root)
        {
            foreach (MeshFilter filter in root.GetComponentsInChildren<MeshFilter>())
            {
                if (filter.sharedMesh && !filter.GetComponent<Collider>())
                {
                    filter.gameObject.AddComponent<MeshCollider>().sharedMesh = filter.sharedMesh;
                }
            }
        }

        // Adds one box to the owner around the meshes, in the local space of the owner.
        private static void AddBox(Transform owner, IReadOnlyList<MeshFilter> filters)
        {
            if (owner.GetComponent<Collider>())
                return;

            Bounds? local = null;
            foreach (MeshFilter filter in filters)
            {
                if (!filter.sharedMesh)
                {
                    continue;
                }

                Bounds meshBounds = filter.sharedMesh.bounds;
                Matrix4x4 toOwner = owner.worldToLocalMatrix * filter.transform.localToWorldMatrix;
                foreach (Vector3 corner in Corners(meshBounds))
                {
                    Vector3 point = toOwner.MultiplyPoint3x4(corner);
                    if (local.HasValue)
                    {
                        Bounds grown = local.Value;
                        grown.Encapsulate(point);
                        local = grown;
                    }
                    else
                    {
                        local = new Bounds(point, Vector3.zero);
                    }
                }
            }
            if (local.HasValue)
            {
                var box = owner.gameObject.AddComponent<BoxCollider>();
                box.center = local.Value.center;
                box.size = local.Value.size;
            }
        }

        private static IEnumerable<Vector3> Corners(Bounds bounds)
        {
            Vector3 min = bounds.min;
            Vector3 max = bounds.max;
            for (int i = 0; i < 8; i++)
            {
                yield return new Vector3((i & 1) == 0 ? min.x : max.x, (i & 2) == 0 ? min.y : max.y, (i & 4) == 0 ? min.z : max.z);
            }
        }

        private float GroundHeight(Vector3 position)
        {
            if (_ground == BarrierGround.Spline)
                return position.y;

            foreach (UnityEngine.Terrain terrain in UnityEngine.Terrain.activeTerrains)
            {
                Vector3 origin = terrain.transform.position;
                Vector3 size = terrain.terrainData.size;
                if (position.x >= origin.x && position.x <= origin.x + size.x && position.z >= origin.z && position.z <= origin.z + size.z)
                    return terrain.SampleHeight(position) + origin.y;
            }
            return position.y;
        }

        private static void DestroyGenerated(Object target)
        {
            if (Application.isPlaying)
            {
                Destroy(target);
            }
            else
            {
                DestroyImmediate(target);
            }
        }

        private void MarkSceneDirty()
        {
#if UNITY_EDITOR
            if (!Application.isPlaying && gameObject.scene.IsValid())
            {
                EditorSceneManager.MarkSceneDirty(gameObject.scene);
            }
#endif
        }
        #endregion

        #region Change Events
        private void OnSplineChanged(Spline spline, int knotIndex, SplineModification modification)
        {
            if (!_autoRebuild || !_container)
                return;

            foreach (Spline own in _container.Splines)
            {
                if (ReferenceEquals(own, spline))
                {
                    RequestRebuild();
                    return;
                }
            }
        }

        private void SubscribeWorlds()
        {
            UnsubscribeWorlds();
            _worlds.AddRange(FindObjectsByType<LoogaStampWorld>(FindObjectsSortMode.None));
            foreach (LoogaStampWorld world in _worlds)
            {
                world.UpdateSettled += OnTerrainSettled;
            }
        }

        private void UnsubscribeWorlds()
        {
            foreach (LoogaStampWorld world in _worlds)
            {
                if (world)
                {
                    world.UpdateSettled -= OnTerrainSettled;
                }
            }
            _worlds.Clear();
        }

        private void OnTerrainSettled(Rect region)
        {
            if (_autoRebuild && _generated && region.Overlaps(GetWorldRect()))
            {
                RequestRebuild();
            }
        }

        private Rect GetWorldRect()
        {
            if (!_container)
                return default;

            Bounds bounds = default;
            bool first = true;
            foreach (Spline spline in _container.Splines)
            {
                foreach (BezierKnot knot in spline.Knots)
                {
                    Vector3 point = transform.TransformPoint(knot.Position);
                    if (first)
                    {
                        bounds = new Bounds(point, Vector3.zero);
                        first = false;
                    }
                    else
                    {
                        bounds.Encapsulate(point);
                    }
                }
            }
            // The margin covers spline curvature between knots and the piece depth.
            return Rect.MinMaxRect(bounds.min.x - 5f, bounds.min.z - 5f, bounds.max.x + 5f, bounds.max.z + 5f);
        }
        #endregion
    }
}
