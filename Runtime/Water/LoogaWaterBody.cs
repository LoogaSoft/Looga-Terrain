using System;
using System.Collections.Generic;
using LoogaSoft.Terrain.Geology;
using LoogaSoft.Terrain.Stamps;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Splines;

namespace LoogaSoft.Terrain.Water
{
    /// <summary>
    /// A water surface from a spline. An area is a closed outline at the height of this object, for example a lake or
    /// a sea. A river follows an open spline with a half width at each point, and its surface follows the spline height.
    /// The body is a <see cref="LoogaWaterSource"/>, so water areas and stamp water filters read it.
    /// Each vertex holds the flow in metres per second. <see cref="LoogaWaterFlow"/> zones change it by area.
    /// The Looga/Water shader draws the surface. The mesh is built in the scene and is not saved with it.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    [AddComponentMenu("Looga Terrain/Water Body")]
    public sealed partial class LoogaWaterBody : LoogaWaterSource
    {
        public enum WaterShape
        {
            // Closed outline at the height of the body object.
            Area = 0,
            // Open spline with a half width. The surface follows the spline height.
            River = 1
        }

        private static readonly int ShoreMapId = Shader.PropertyToID("_ShoreMap");
        private static readonly int ShoreMapRectId = Shader.PropertyToID("_ShoreMapRect");
        private static readonly int CoverageMapId = Shader.PropertyToID("_OceanCoverageMap");
        private static readonly int CoverageRectId = Shader.PropertyToID("_OceanCoverageRect");
        private static readonly int CoverageOpenId = Shader.PropertyToID("_OceanCoverageOpen");
        private static readonly int FoamSimId = Shader.PropertyToID("_FoamSim");
        private static readonly int FoamSimRectId = Shader.PropertyToID("_FoamSimRect");
        private static readonly int FoamSimFadeId = Shader.PropertyToID("_FoamSimFade");
        private static readonly int FoamSimAgeId = Shader.PropertyToID("_FoamSimAge");
        private static readonly int ShoreWaveMapId = Shader.PropertyToID("_ShoreWaveMap");
        private static readonly int ShoreFineMapId = Shader.PropertyToID("_ShoreFineMap");
        private static readonly int ShoreFineRectId = Shader.PropertyToID("_ShoreFineRect");
        private static readonly int WeldMapId = Shader.PropertyToID("_WaterWeldMap");
        private static readonly int WeldRectId = Shader.PropertyToID("_WaterWeldRect");
        private static readonly int WeldParamsId = Shader.PropertyToID("_WaterWeldParams");
        private static readonly int ShoreWavePeriodId = Shader.PropertyToID("_ShoreWavePeriod");
        private static readonly int ShoreWaveHeightId = Shader.PropertyToID("_ShoreWaveHeight");
        private static readonly int PlungeWaveMapId = Shader.PropertyToID("_PlungeWaveMap");
        private static readonly int PlungeAlongMapId = Shader.PropertyToID("_PlungeAlongMap");
        private static readonly int PlungeWaveHeightId = Shader.PropertyToID("_PlungeWaveHeight");
        private static readonly int PlungeWavePeriodId = Shader.PropertyToID("_PlungeWavePeriod");
        private static readonly int SwashHeightId = Shader.PropertyToID("_SwashHeight");
        private static readonly int GlobalSwashId = Shader.PropertyToID("_LoogaSwash");
        private static readonly int GlobalSwashShoreMapId = Shader.PropertyToID("_LoogaSwashShoreMap");
        private static readonly int GlobalSwashShoreRectId = Shader.PropertyToID("_LoogaSwashShoreRect");
        private static readonly int GlobalSwashWaveMapId = Shader.PropertyToID("_LoogaSwashWaveMap");
        // Scale of the terrain error pixels at the water level of a body with a swash run-up.
        private const float ShoreDetailErrorScale = 0.1f;
        // The body whose swash the terrain uses to wet the ground at the shore. Only one body at a time.
        private static LoogaWaterBody _swashOwner;
        // Wave period for the deep water speed of the shore wave field when the material has no period, in seconds.
        private const float DefaultShoreWavePeriod = 9f;
        private const float DefaultPlungeWavePeriod = 2.5f;
        // Crest impact boils: one for about each CrestBoilSpacing metres of landing line, with a length in metres and a
        // strength (1 = the full impact foam of a fall) in these ranges.
        private const float CrestBoilSpacing = 14f;
        private static readonly Vector2 CrestBoilLength = new(4f, 9f);
        private static readonly Vector2 CrestBoilStrength = new(0.3f, 0.75f);

        // Last time that a game camera rendered. Scene view cameras move the foam window only when no game camera
        // renders, so two views do not pull the window back and forth.
        private static float _lastGameCamera = float.NegativeInfinity;
        // Distance between the points of the far outline of a body with a detail area, in metres.
        private const float FarEdgeSpacing = 2048f;
        // Largest distance of the foam window center ahead of the camera, as a share of the window side. The window
        // keeps some water behind and beside the camera.
        private const float FoamWindowLead = 0.35f;
        // Camera height above the water, as a share of the foam window, below which the window stays around the camera.
        private const float FoamLeadStartHeight = 0.25f;

        [Header("Shape")]
        [Tooltip("Area: closed outline at the height of this object. River: open spline with a half width.")]
        [SerializeField] private WaterShape _shape = WaterShape.Area;
        [SerializeField] private SplineContainer _spline;
        [Tooltip("River: half width beside each spline of the container, in metres. x = knot position, y = half " +
                 "width. Looga spline paths use the same unit.")]
        [SerializeField] private List<LoogaSplinePath.SplineWidths> _widths = new();
        [Tooltip("River: half width where a spline has no width points, in metres.")]
        [SerializeField, Min(0f)] private float _width = 4f;
        [Tooltip("River: height of the surface above the spline, in metres.")]
        [SerializeField] private float _surfaceOffset;
        [Tooltip("River: length at the start (x) and at the end (y) over which the surface fades out, in metres. " +
                 "Use it where the river enters other water. In a fade, a side that ends in open water (no ground at " +
                 "the edge) tapers out, so the ribbon edge does not show over the other water.")]
        [SerializeField] private Vector2 _endFade;

        [Header("Mesh")]
        [Tooltip("Distance between vertices inside the water, in metres. Smaller values give smoother waves and flow.")]
        [SerializeField, Min(0.25f)] private float _vertexSpacing = 4f;
        [Tooltip("Area: distance between the vertices on the outline, in metres. It sets how exactly the water edge " +
                 "follows the spline.")]
        [SerializeField, Min(0.1f)] private float _edgeSpacing = 2f;
        [Tooltip("Largest wave movement above, below and beside the surface, in meters. It extends the culling bounds.")]
        [SerializeField, Min(0f)] private float _wavePadding = 2f;
        [Tooltip("Area: world rectangle with the fine vertices and the shore texture, for a body that runs far past " +
                 "its shores. The first spline is then the far outline. Outside the rectangle the vertices get " +
                 "coarser with distance. Width 0 = the whole body is fine.")]
        [SerializeField] private Rect _detailArea;
        [Tooltip("Area with a detail area: distance from an outline in which the vertices stay fine, in metres.")]
        [SerializeField, Min(0f)] private float _detailReach = 800f;

        [Header("Flow")]
        [Tooltip("Speed of the water in metres per second. River: downstream, first knot to last. Area: drift. In a " +
                 "water network, a river uses the solved speed instead.")]
        [SerializeField, Min(0f)] private float _flowSpeed = 0.3f;
        [Tooltip("Area: drift direction in degrees. 0 = world +Z, 90 = world +X.")]
        [SerializeField] private float _flowDirection;
        [Tooltip("River: speed loss at the banks. 0 = same speed across, 1 = still water at the banks.")]
        [SerializeField, Range(0f, 1f)] private float _bankSlowdown = 0.5f;
        [Tooltip("Area: solves the current from the inlet and outlet flow zones over the shore texture. The current " +
                 "fans out from the inlets, follows the shore and gathers at the outlets. It replaces the drift. The " +
                 "other flow zones and the pull of the outlets still apply on top. A body with channel joints in a " +
                 "water network always solves its current.")]
        [SerializeField] private bool _solvedCurrents;

        [Header("Shore")]
        [Tooltip("Texel size of the shore texture, in metres. The shader reads the distance to the water edge and the " +
                 "depth from it for the shore foam. Large bodies get larger texels (1024 texels at most per side).")]
        [SerializeField, Min(0.1f)] private float _shoreTexelSize = 0.5f;
        [Tooltip("Layers of the colliders that go through the surface and stop the water, for example rocks. The " +
                 "shore and its foam go around them. Terrain colliders and moving bodies are not used.")]
        [SerializeField] private LayerMask _obstacleLayers = Physics.DefaultRaycastLayers;
        [Tooltip("Builds the arrival field of the waves that come to the shore, for a material with Shore Waves. The " +
                 "waves turn toward the shore, get shorter and steeper in shallow water and break into surf.")]
        [SerializeField] private bool _shoreWaves;
        [Tooltip("Shore Waves come in as swell from one direction (Swell Direction), as on a sea coast. Offshore their " +
                 "crests are straight lines. Shallow water turns them toward the shore and wraps them around headlands, " +
                 "and shores that face away from the swell get lower waves. Off: the waves come straight to every " +
                 "shore, as on a lake.")]
        [SerializeField] private bool _shoreWaveSwell;
        [Tooltip("Direction that the swell moves toward, in degrees clockwise from north (+Z). Use the wind direction " +
                 "of Looga Ocean Waves, so the swell and the ocean waves go the same way.")]
        [SerializeField, Range(-360f, 360f)] private float _swellDirection = 110f;

        [Header("Foam Simulation")]
        [Tooltip("Foam that moves with the flow, collects where the flow converges and fades over time, so foam " +
                 "trails go downstream from banks and rocks. Needs compute shaders.")]
        [SerializeField] private bool _foamSimulation = true;
        [Tooltip("Side of the largest simulated area, in metres. A larger body simulates this area around the water " +
                 "that the camera looks at.")]
        [SerializeField, Min(16f)] private float _foamWindow = 256f;
        [Tooltip("Smallest texel of the simulation, in metres. The texture has 512 texels per side at most.")]
        [SerializeField, Min(0.1f)] private float _foamTexelSize = 0.25f;
        [Tooltip("Time in which foam away from its source fades to about a third, in seconds. Longer gives longer " +
                 "trails.")]
        [SerializeField, Min(0.1f)] private float _foamLifetime = 8f;
        [Tooltip("How strongly foam gathers into lines where the flow converges, for example in eddies.")]
        [SerializeField, Range(0f, 4f)] private float _foamConvergence = 1f;
        [Tooltip("Body that flows into this one, for example the river before an end-to-end joint. Foam that comes in " +
                 "past the outline takes its foam, so foam trails go on across the joint. Empty in a water network: " +
                 "the upstream body of the joint nearest the foam window.")]
        [SerializeField] private LoogaWaterBody _inflowFrom;

        [Header("Hydraulics")]
        [Tooltip("River: design water depth over the bed, in metres. A water network uses it for the flow speed. The " +
                 "surface does not use it.")]
        [SerializeField, Min(0.05f)] private float _bedDepth = 1f;
        [Tooltip("River: Manning roughness of the bed. 0.03 = clean gravel, 0.05 = cobbles and weeds. A water network " +
                 "can use a higher steep-stream roughness on steep beds.")]
        [SerializeField, Range(0.01f, 0.2f)] private float _roughness = 0.035f;

        private readonly WaterTriangulator _triangulator = new();
        private readonly List<WaterSplinePoint> _samples = new();
        private readonly List<List<Vector2>> _outlines = new();
        private readonly List<Vector2> _areaVertices = new();
        private readonly List<int> _triangles = new();
        private readonly List<Vector3> _positions = new();
        private readonly List<Vector3> _normals = new();
        private readonly List<Vector2> _worldPoints = new();
        // Area: the drift. River: the flow at 1 m/s with the bank slowdown, multiplied by the speed in WriteFlow.
        private readonly List<Vector2> _baseFlow = new();
        // River: knot position and spline index of each vertex, for the solved speed of a water network.
        private readonly List<float> _vertexKnots = new();
        private readonly List<int> _vertexSplines = new();
        private readonly List<float> _alpha = new();
        // Vertex spacing at each vertex of an area with a detail area. Empty = the same spacing everywhere.
        private readonly List<float> _localSpacing = new();
        private readonly List<Vector4> _flowData = new();
        // UV1: x = turbulence, y = aeration of the flow regime of a water network, 0 to 1.
        private readonly List<Vector2> _regimeData = new();
        private readonly List<LoogaStampWorld> _worlds = new();
        private readonly WaterShoreField _shore = new();
        // Where the area has water, for the ocean near grid outside the shore map.
        private readonly WaterCoverageMask _coverage = new();
        private readonly WaterShoreWaveField _shoreWaveField = new();
        // The waves that run out from the plunge lines of the falls into this body, and the lines of the last build.
        private readonly WaterShoreWaveField _plungeWaveField = new("Looga Water Plunge Waves");
        private readonly List<Vector4> _plungeLines = new();
        private Hash128 _plungeWaveInputs;
        private readonly WaterNearShoreField _nearShore = new();
        private readonly WaterCurrentField _currents = new();
        private static readonly List<LoogaWaterFlow> CurrentZones = new();
        // The flow zones over this body, in order. A channel that samples this body uses them too.
        private readonly List<LoogaWaterFlow> _flowZones = new();
        // Network data of the last flow build: the joint flows into and out of this area, the network element of each
        // river spline and the joint flows of the bodies that the river splines overlap.
        private readonly List<WaterJointFlow> _joints = new();
        private readonly List<int> _channelElements = new();
        private readonly List<WaterJointFlow> _overlaps = new();
        private LoogaWaterNetwork _network;
        private WaterNetworkGraph _graph;
        // The network and its data for this body (LoogaWaterNetwork.GetBodyFlow) at the last WriteFlow.
        [NonSerialized] private LoogaWaterNetwork _writtenNetwork;
        [NonSerialized] private Hash128 _writtenFlow;
        private static readonly List<Vector4> ImpactSegments = new();
        private static readonly List<float> ImpactStrengths = new();
        private readonly WaterFoamSimulation _foam = new();
        private Mesh _mesh;
        private MaterialPropertyBlock _properties;
        // The fall whose sheet starts on the weld line of this body, and its weld field. One fall per body.
        private LoogaWaterFall _weldOwner;
        private Texture _weldMap;
        private Vector4 _weldRect;
        private Vector4 _weldParams;
        private Rect _worldRect;
        // Lowest (x) and highest (y) world height of the last built surface.
        private Vector2 _worldHeights;
        private Matrix4x4 _builtMatrix;
        private Hash128 _builtInputs;
        private bool _buildQueued;
        private bool _flowQueued;
        private bool _shoreQueued;
        // The vertex data of the build stays in the last domain after a script reload. A flow change then builds again.
        [NonSerialized] private bool _buildDataLost;

        public WaterShape Shape => _shape;
        public SplineContainer Spline => _spline;
        public IReadOnlyList<LoogaSplinePath.SplineWidths> Widths => _widths;
        public float Width => _width;
        public float SurfaceOffset => _surfaceOffset;
        public Vector2 EndFade => _endFade;
        public float VertexSpacing => _vertexSpacing;
        public float EdgeSpacing => _edgeSpacing;
        public Rect DetailArea => _detailArea;
        public float DetailReach => _detailReach;
        public float FlowSpeed => _flowSpeed;
        public float FlowDirection => _flowDirection;
        public float BankSlowdown => _bankSlowdown;
        public float ShoreTexelSize => _shoreTexelSize;
        public LoogaWaterBody InflowFrom => _inflowFrom;
        public float BedDepth => _bedDepth;
        public float Roughness => _roughness;
        /// <summary>
        /// Texel size of the built shore map in metres. A large body gets larger texels than the shore texel size,
        /// because the shore map has a largest side. The shore texel size before the first build.
        /// </summary>
        public float ShoreMapTexelSize => _shore.IsBuilt ? _shore.TexelSize : _shoreTexelSize;
        /// <summary>Distance to the water edge (R), depth (G) and flow (BA). Null until terrain is under the surface.</summary>
        public Texture2D ShoreMap => _shore.IsBuilt ? _shore.Texture : null;
        public float ShoreMilliseconds { get; private set; }
        public float CurrentsMilliseconds => _currents.BuildMilliseconds;
        public int CurrentsIterations => _currents.Iterations;

        /// <summary>World XZ rectangle of the last built surface.</summary>
        public Rect WorldRect => _worldRect;
        public int VertexCount { get; private set; }
        public float BuildMilliseconds { get; private set; }
        /// <summary>Explains why the body has no surface. Empty when the surface is built.</summary>
        public string Diagnostic { get; private set; } = string.Empty;

        #region Built-in
        private void OnEnable()
        {
            UnityEngine.Splines.Spline.Changed += OnSplineChanged;
            LoogaWaterFlow.Changed += OnFlowChanged;
#if UNITY_EDITOR
            UnityEditor.Undo.undoRedoPerformed += OnUndoRedo;
#endif
            SubscribeWorlds();
            RenderPipelineManager.beginCameraRendering += OnBeginCamera;
            if (AdoptAfterReload() || LoadShipped()) return;

            // In play mode the first build waits for Start, so the water network and the flow zones of the scene are
            // enabled. The network then solves once and the bodies do not build their flow again in the first frame.
            if (Application.isPlaying)
            {
                QueueBuild();
                return;
            }
            Rebuild();
        }

        private void Start()
        {
            _shippedLoading = false;
            if (_buildQueued)
            {
                Rebuild();
            }
        }

        private void OnDisable()
        {
            UnityEngine.Splines.Spline.Changed -= OnSplineChanged;
            LoogaWaterFlow.Changed -= OnFlowChanged;
#if UNITY_EDITOR
            UnityEditor.Undo.undoRedoPerformed -= OnUndoRedo;
#endif
            UnsubscribeWorlds();
            RenderPipelineManager.beginCameraRendering -= OnBeginCamera;
            if (_swashOwner == this) ClearSwashGlobals();
            // A build job must end before a script reload. The next camera builds the window again.
            _nearShore.Release();
            // A script reload keeps the mesh and the shore. The next domain takes them back if the code did not change.
            if (GeneratedReloadHandover.Unloading && StoreForReload()) return;
            Release();
        }

        // OnValidate must not create objects, so it only queues the build.
        protected override void OnValidate()
        {
            base.OnValidate();
            if (!GeneratedReloadHandover.KeepsOutputOnLoad) QueueBuild();
        }

        // A moved spline changes the surface. A body that moves with its spline keeps its mesh.
        private void Update()
        {
            if (_spline && GetSplineMatrix() != _builtMatrix) QueueBuild();
            // The terrain can load after the body, so a shore without terrain tries again.
            if (_shoreQueued && !_buildQueued) RebuildShore();
            if (!Application.isPlaying) return;
            if (_buildQueued)
            {
                Rebuild();
            }
            else if (_flowQueued)
            {
                RebuildFlow();
            }
        }
        #endregion

        public override bool TryGetSurface(out Mesh mesh, out Matrix4x4 localToWorld)
        {
            mesh = _mesh && VertexCount > 0 ? _mesh : null;
            localToWorld = transform.localToWorldMatrix;
            return mesh;
        }

        #region Settings
        /// <summary>Makes the body an area from the closed splines of the container.</summary>
        /// <summary>Sets the area with fine vertices and a shore texture. A zero rectangle means the whole body.</summary>
        public void SetDetailArea(Rect area, float reach)
        {
            _detailArea = area;
            _detailReach = Mathf.Max(0f, reach);
            QueueBuild();
        }

        public void SetArea(SplineContainer spline, float vertexSpacing, float edgeSpacing)
        {
            _shape = WaterShape.Area;
            _spline = spline;
            _vertexSpacing = Mathf.Max(0.25f, vertexSpacing);
            _edgeSpacing = Mathf.Max(0.1f, edgeSpacing);
            Rebuild();
        }

        /// <summary>Makes the body a river along the splines of the container.</summary>
        /// <param name="widths">Half widths for each spline, in knot units and metres.</param>
        /// <param name="endFade">Fade length at the start (x) and at the end (y), in metres.</param>
        public void SetRiver(SplineContainer spline, List<LoogaSplinePath.SplineWidths> widths, float width,
            float surfaceOffset, Vector2 endFade, float vertexSpacing)
        {
            _shape = WaterShape.River;
            _spline = spline;
            _widths = widths ?? new List<LoogaSplinePath.SplineWidths>();
            _width = Mathf.Max(0f, width);
            _surfaceOffset = surfaceOffset;
            _endFade = Vector2.Max(endFade, Vector2.zero);
            _vertexSpacing = Mathf.Max(0.25f, vertexSpacing);
            Rebuild();
        }

        /// <summary>Sets the body that flows into this one, or null. See the Inflow From field.</summary>
        public void SetInflowFrom(LoogaWaterBody upstream)
        {
            _inflowFrom = upstream != this ? upstream : null;
        }

        /// <summary>
        /// Sets the smallest shore map texel in metres. Bodies that join, for example two rivers end to end, need the
        /// same texel, or the shore foam band changes at the joint.
        /// </summary>
        public void SetShoreTexelSize(float texelSize)
        {
            _shoreTexelSize = Mathf.Max(0.1f, texelSize);
            Rebuild();
        }

        /// <param name="direction">Area drift direction in degrees. 0 = world +Z, 90 = world +X.</param>
        public void SetFlow(float speed, float direction, float bankSlowdown)
        {
            _flowSpeed = Mathf.Max(0f, speed);
            _flowDirection = direction;
            _bankSlowdown = Mathf.Clamp01(bankSlowdown);
            // The base flow of each vertex comes from the build.
            Rebuild();
        }
        #endregion

        #region Build
        /// <summary>Builds the surface mesh and its flow now.</summary>
        [ContextMenu("Rebuild")]
        public void Rebuild()
        {
            using ProfilerMarker.AutoScope build = LoogaWaterMarkers.Body.Auto();
            _buildQueued = false;
            _flowQueued = false;
            if (!isActiveAndEnabled) return;

            _buildDataLost = false;
            float start = Time.realtimeSinceStartup;
            if (!_mesh)
            {
                _mesh = new Mesh { name = "Looga Water Body", hideFlags = HideFlags.DontSave };
            }
            _builtMatrix = GetSplineMatrix();
            _builtInputs = GetBuildInputs();
            _positions.Clear();
            _worldPoints.Clear();
            _baseFlow.Clear();
            _vertexKnots.Clear();
            _vertexSplines.Clear();
            _alpha.Clear();
            _localSpacing.Clear();
            _triangles.Clear();
            Diagnostic = string.Empty;
            if (!_spline || _spline.Splines.Count == 0)
            {
                Diagnostic = "The water body has no spline.";
            }
            else if (_shape == WaterShape.Area)
            {
                BuildArea();
            }
            else
            {
                BuildRiver();
            }
            BuildCoverage();
            WriteMesh();
            GetComponent<MeshFilter>().sharedMesh = _mesh;
            BuildMilliseconds = (Time.realtimeSinceStartup - start) * 1000f;
            RebuildShore();
            // The stamp updater compares the new water edges with the old ones. Same edges give no stamp update.
            MarkChanged();
        }

        private void BuildArea()
        {
            float level = transform.position.y;
            Matrix4x4 splineToWorld = _spline.transform.localToWorldMatrix;
            while (_outlines.Count < _spline.Splines.Count)
            {
                _outlines.Add(new List<Vector2>());
            }
            int outlineCount = 0;
            bool detail = _detailArea.width > 0f;
            foreach (UnityEngine.Splines.Spline spline in _spline.Splines)
            {
                // The far outline of a body with a detail area can be hundreds of kilometres long.
                bool far = detail && outlineCount == 0;
                WaterSplines.Sample(spline, splineToWorld, far ? FarEdgeSpacing : _edgeSpacing, _samples);
                if (_samples.Count < 3) continue;

                List<Vector2> outline = _outlines[outlineCount++];
                outline.Clear();
                foreach (WaterSplinePoint sample in _samples)
                {
                    outline.Add(new Vector2(sample.Position.x, sample.Position.z));
                }
            }
            _outlines.RemoveRange(outlineCount, _outlines.Count - outlineCount);
            if (outlineCount == 0)
            {
                Diagnostic = "An area needs a spline with at least three points.";
                return;
            }

            _triangulator.Triangulate(_outlines, _vertexSpacing, _detailArea, _detailReach, _areaVertices, _triangles);
            Matrix4x4 worldToLocal = transform.worldToLocalMatrix;
            Vector2 drift = LoogaWaterFlow.DirectionVector(_flowDirection) * _flowSpeed;
            foreach (Vector2 point in _areaVertices)
            {
                _positions.Add(worldToLocal.MultiplyPoint3x4(new Vector3(point.x, level, point.y)));
                _worldPoints.Add(point);
                _baseFlow.Add(drift);
                _alpha.Add(1f);
                if (detail) _localSpacing.Add(_triangulator.SpacingAt(point));
            }
            if (_triangles.Count == 0) Diagnostic = "The area outline gave no triangles. Check that it is closed.";
        }

        // Only an area with Ocean Waves gets a mask: the ocean near grid is the only reader.
        private void BuildCoverage()
        {
            var meshRenderer = GetComponent<MeshRenderer>();
            Material material = meshRenderer ? meshRenderer.sharedMaterial : null;
            bool ocean = material && material.IsKeywordEnabled("_OCEAN_WAVES");
            if (_shape == WaterShape.Area && ocean && _outlines.Count > 0)
            {
                _coverage.Build(_outlines, _detailArea.width > 0f);
            }
            else
            {
                _coverage.Release();
            }
        }

        private void BuildRiver()
        {
            Matrix4x4 splineToWorld = _spline.transform.localToWorldMatrix;
            Matrix4x4 worldToLocal = transform.worldToLocalMatrix;
            float spacing = Mathf.Max(0.25f, _vertexSpacing);
            for (int splineIndex = 0; splineIndex < _spline.Splines.Count; splineIndex++)
            {
                WaterSplines.Sample(_spline.Splines[splineIndex], splineToWorld, spacing, _samples);
                if (_samples.Count < 2) continue;

                IReadOnlyList<Vector2> widthPoints = splineIndex < _widths.Count && _widths[splineIndex] != null
                    ? _widths[splineIndex].Points
                    : null;
                AddRibbon(splineIndex, widthPoints, spacing, worldToLocal);
            }
            if (_triangles.Count == 0) Diagnostic = "A river needs a spline with at least two points.";
        }

        // One ribbon along the samples. Rows go across the river from the left bank to the right bank.
        private void AddRibbon(int splineIndex, IReadOnlyList<Vector2> widthPoints, float spacing,
            Matrix4x4 worldToLocal)
        {
            float length = 0f;
            for (int index = 1; index < _samples.Count; index++)
            {
                length += Vector3.Distance(_samples[index - 1].Position, _samples[index].Position);
            }

            bool groundRead = (_endFade.x > 0f || _endFade.y > 0f) && CollectRibbonTerrains(widthPoints);
            int previousRow = -1;
            int previousColumns = 0;
            float travelled = 0f;
            for (int index = 0; index < _samples.Count; index++)
            {
                if (index > 0) travelled += Vector3.Distance(_samples[index - 1].Position, _samples[index].Position);
                WaterSplinePoint sample = _samples[index];
                Vector3 before = _samples[Mathf.Max(index - 1, 0)].Position;
                Vector3 after = _samples[Mathf.Min(index + 1, _samples.Count - 1)].Position;
                // The end rows use the exact spline direction, so rivers joined end to end with the same knot direction
                // share one end line.
                Vector3 direction = sample.Tangent.x != 0f || sample.Tangent.z != 0f ? sample.Tangent : after - before;
                Vector2 tangent = new Vector2(direction.x, direction.z).normalized;
                if (tangent == Vector2.zero) tangent = Vector2.up;
                Vector2 right = new(tangent.y, -tangent.x);
                float halfWidth = Mathf.Max(0.05f, WaterSplines.Width(widthPoints, sample.Knot, _width));
                int columns = Mathf.Max(2, Mathf.CeilToInt(2f * halfWidth / spacing));
                float alpha = EndAlpha(travelled, length);
                float height = sample.Position.y + _surfaceOffset;
                Vector2 centre = new(sample.Position.x, sample.Position.z);
                // In an end fade, a side that ends in open water (other water) tapers out, so the ribbon edge does
                // not show. A side on dry ground keeps its shore.
                bool inEnd = groundRead && (travelled < _endFade.x || length - travelled < _endFade.y);
                float openLeft = inEnd ? OpenSide(centre - right * halfWidth, height) : 0f;
                float openRight = inEnd ? OpenSide(centre + right * halfWidth, height) : 0f;

                int row = _positions.Count;
                for (int column = 0; column <= columns; column++)
                {
                    float across = column / (float)columns * 2f - 1f;
                    Vector2 point = centre + right * (across * halfWidth);
                    float open = across < 0f ? openLeft : openRight;
                    float taper = 1f - open * Mathf.SmoothStep(0f, 1f, (Mathf.Abs(across) - OpenSideCore) /
                        (1f - OpenSideCore));
                    _positions.Add(worldToLocal.MultiplyPoint3x4(new Vector3(point.x, height, point.y)));
                    _worldPoints.Add(point);
                    _baseFlow.Add(tangent * (1f - _bankSlowdown * across * across));
                    _vertexKnots.Add(sample.Knot);
                    _vertexSplines.Add(splineIndex);
                    _alpha.Add(alpha * taper);
                }
                if (previousRow >= 0) Stitch(previousRow, previousColumns, row, columns);
                previousRow = row;
                previousColumns = columns;
            }
        }

        // Joins two rows with triangles. Rows can have different column counts where the width changes.
        private void Stitch(int rowA, int columnsA, int rowB, int columnsB)
        {
            int a = 0;
            int b = 0;
            while (a < columnsA || b < columnsB)
            {
                // Advance the row whose next vertex lies less far across, so the triangles stay even.
                bool advanceA = b >= columnsB || (a < columnsA && (a + 1) / (float)columnsA <= (b + 1) / (float)columnsB);
                if (advanceA)
                {
                    // Clockwise seen from above: the row ahead (B) is on the left of the walk across.
                    AddTriangle(rowA + a, rowB + b, rowA + a + 1);
                    a++;
                }
                else
                {
                    AddTriangle(rowA + a, rowB + b, rowB + b + 1);
                    b++;
                }
            }
        }

        private void AddTriangle(int a, int b, int c)
        {
            _triangles.Add(a);
            _triangles.Add(b);
            _triangles.Add(c);
        }

        // Share of the half width at the centre of an open side that keeps the end fade alpha. The rest tapers to 0.
        private const float OpenSideCore = 0.3f;
        // Water depth at the edge of a row that makes the side fully open, in metres.
        private const float OpenSideDepth = 0.5f;

        // Reads the terrains under the ribbon samples, with the widest half width as margin.
        private bool CollectRibbonTerrains(IReadOnlyList<Vector2> widthPoints)
        {
            float margin = _width;
            if (widthPoints != null)
            {
                foreach (Vector2 point in widthPoints)
                {
                    margin = Mathf.Max(margin, point.y);
                }
            }
            Vector3 min = _samples[0].Position;
            Vector3 max = min;
            foreach (WaterSplinePoint sample in _samples)
            {
                min = Vector3.Min(min, sample.Position);
                max = Vector3.Max(max, sample.Position);
            }
            return WaterShoreField.CollectTerrains(Rect.MinMaxRect(min.x - margin, min.z - margin, max.x + margin,
                max.z + margin));
        }

        // 0 when the ground at the point is at or above the surface, 1 when the water there is OpenSideDepth deep.
        private static float OpenSide(Vector2 point, float surface)
        {
            if (!WaterShoreField.TrySampleGround(point, out float ground)) return 0f;
            return Mathf.Clamp01((surface - ground) / OpenSideDepth);
        }

        private float EndAlpha(float travelled, float length)
        {
            float alpha = 1f;
            if (_endFade.x > 0f) alpha = Mathf.Min(alpha, Mathf.SmoothStep(0f, 1f, travelled / _endFade.x));
            if (_endFade.y > 0f) alpha = Mathf.Min(alpha, Mathf.SmoothStep(0f, 1f, (length - travelled) / _endFade.y));
            return alpha;
        }

        private void WriteMesh()
        {
            _mesh.Clear();
            VertexCount = _triangles.Count > 0 ? _positions.Count : 0;
            _worldRect = default;
            if (VertexCount == 0) return;

            _normals.Clear();
            Vector3 up = transform.InverseTransformDirection(Vector3.up).normalized;
            Vector3 min = _positions[0];
            Vector3 max = _positions[0];
            Vector2 worldMin = _worldPoints[0];
            Vector2 worldMax = _worldPoints[0];
            for (int index = 0; index < _positions.Count; index++)
            {
                _normals.Add(up);
                min = Vector3.Min(min, _positions[index]);
                max = Vector3.Max(max, _positions[index]);
                worldMin = Vector2.Min(worldMin, _worldPoints[index]);
                worldMax = Vector2.Max(worldMax, _worldPoints[index]);
            }
            _worldRect = Rect.MinMaxRect(worldMin.x, worldMin.y, worldMax.x, worldMax.y);
            _worldHeights = WorldHeights(min, max);
            _mesh.indexFormat = _positions.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            _mesh.SetVertices(_positions);
            _mesh.SetNormals(_normals);
            _mesh.SetTriangles(_triangles, 0, false);
            WriteFlow();
            // The shader moves the vertices, so the bounds include the wave movement.
            Bounds bounds = new();
            bounds.SetMinMax(min, max);
            bounds.Expand(2f * _wavePadding);
            _mesh.bounds = bounds;
        }
        #endregion

        #region Flow
        /// <summary>Applies the flow zones again without a new mesh. Flow zones call it when they change.</summary>
        public void RebuildFlow()
        {
            using ProfilerMarker.AutoScope build = LoogaWaterMarkers.Flow.Auto();
            _flowQueued = false;
            if (_buildDataLost)
            {
                Rebuild();
                return;
            }
            if (!_mesh || VertexCount == 0 || VertexCount != _mesh.vertexCount) return;

            SolveCurrents();
            WriteFlow();
            _shore.RefreshFlow(_mesh, transform.localToWorldMatrix);
            NewOutput();
        }

        // UV0: x, y = world XZ flow in metres per second. z = surface alpha. w = vertex spacing in metres: the shader
        // moves the vertices only with the waves that this spacing resolves.
        private void WriteFlow()
        {
            float spacing = Mathf.Max(0.25f, _vertexSpacing);
            ReadNetwork();
            _writtenNetwork = _network;
            _writtenFlow = _network ? _network.GetBodyFlow(this) : default;
            _flowZones.Clear();
            foreach (LoogaWaterFlow zone in LoogaWaterFlow.Active)
            {
                if (zone && zone.Reach.Overlaps(_worldRect) && ReachesSurface(zone)) _flowZones.Add(zone);
            }
            _flowZones.Sort(CompareZones);

            // With a solved current the inlets are already in the field. The outlets still pull the water over their
            // falloff, so the water speeds up toward the lip of a waterfall.
            if (_currents.IsBuilt) _flowZones.RemoveAll(IsInlet);
            _flowData.Clear();
            _regimeData.Clear();
            for (int index = 0; index < _positions.Count; index++)
            {
                Vector2 regime = Vector2.zero;
                Vector2 flow = _shape == WaterShape.River
                    ? RiverFlow(index, ref regime)
                    : AreaFlow(_worldPoints[index], _baseFlow[index], ref regime);
                _flowData.Add(new Vector4(flow.x, flow.y, _alpha[index],
                    _localSpacing.Count > 0 ? _localSpacing[index] : spacing));
                _regimeData.Add(regime);
            }
            _mesh.SetUVs(0, _flowData);
            _mesh.SetUVs(1, _regimeData);
            // The channels that flow into or out of this area read its flow where they overlap it.
            if (_network && _shape == WaterShape.Area) _network.QueueJoinedChannels(this);
        }

        /// <summary>
        /// Flow of an area at a world XZ point, in metres per second: the solved current or the drift, then the
        /// network joints and the flow zones. Channels that overlap the area take this flow, so both surfaces move
        /// the same way there. Zero for a river.
        /// </summary>
        internal Vector2 SampleFlow(Vector2 point)
        {
            if (_shape != WaterShape.Area) return Vector2.zero;

            Vector2 regime = Vector2.zero;
            return AreaFlow(point, LoogaWaterFlow.DirectionVector(_flowDirection) * _flowSpeed, ref regime);
        }

        // The regime values of an area come from the joints: a jet carries the white water of a river into the area.
        private Vector2 AreaFlow(Vector2 point, Vector2 drift, ref Vector2 regime)
        {
            Vector2 flow = _currents.IsBuilt ? _currents.Sample(point) : drift;
            foreach (WaterJointFlow joint in _joints)
            {
                if (!joint.Crest) joint.Apply(point, ref flow, ref regime);
            }
            foreach (LoogaWaterFlow zone in _flowZones)
            {
                zone.Apply(point, ref flow);
            }
            // The water speeds up toward a crest lip, also where a zone sets a slower flow.
            foreach (WaterJointFlow joint in _joints)
            {
                if (joint.Crest) joint.Apply(point, ref flow, ref regime);
            }
            return flow;
        }

        // A river vertex moves at the solved speed of the network, or at the river speed, and takes the regime values
        // of the network. Inside a body that it flows into or out of, it takes the body flow, blended in over a band at
        // the body outline.
        private Vector2 RiverFlow(int index, ref Vector2 regime)
        {
            Vector2 point = _worldPoints[index];
            int spline = _vertexSplines[index];
            int element = spline < _channelElements.Count ? _channelElements[spline] : -1;
            float speed = _flowSpeed;
            if (element >= 0 && _graph.TryGetState(element, _vertexKnots[index], out float solved,
                    out Vector2 solvedRegime))
            {
                speed = solved;
                regime = solvedRegime;
            }
            Vector2 flow = _baseFlow[index] * speed;
            foreach (WaterJointFlow joint in _overlaps)
            {
                if (joint.Channel != element) continue;

                BlendBodyFlow(joint, point, ref flow);
            }
            foreach (LoogaWaterFlow zone in _flowZones)
            {
                zone.Apply(point, ref flow);
            }
            return flow;
        }

        // Distance from the crossing within which a river vertex can overlap the body, in metres. It covers the longest
        // end fades.
        private const float OverlapReach = 80f;
        // Half width of the blend band at the body outline, as a share of the river half width and at least in metres.
        private const float OverlapBand = 0.5f;
        private const float MinimumOverlapBand = 2f;

        private void BlendBodyFlow(WaterJointFlow joint, Vector2 point, ref Vector2 flow)
        {
            float reach = OverlapReach + joint.HalfWidth;
            if ((point - joint.Point).sqrMagnitude > reach * reach) return;

            LoogaWaterBody body = _graph.Elements[joint.Body].Body;
            if (!body) return;

            float band = Mathf.Max(OverlapBand * joint.HalfWidth, MinimumOverlapBand);
            float inside = _graph.SignedOutlineDistance(joint.Body, point);
            float weight = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(-band, band, inside));
            if (weight <= 0f) return;

            flow = Vector2.Lerp(flow, body.SampleFlow(point), weight);
        }

        // Reads the joint flows of this body and the network elements of its river splines.
        private void ReadNetwork()
        {
            _joints.Clear();
            _overlaps.Clear();
            _channelElements.Clear();
            _network = LoogaWaterNetwork.Find(this);
            _graph = _network ? _network.BuildGraph : null;
            if (_graph == null) return;

            WaterNetworkGraph graph = _graph;
            int splines = _shape == WaterShape.River && _spline ? _spline.Splines.Count : 0;
            for (int spline = 0; spline < splines; spline++)
            {
                _channelElements.Add(graph.FindElement(this, spline));
            }
            foreach (WaterJointFlow joint in graph.JointFlows)
            {
                if (graph.Elements[joint.Body].Body == this) _joints.Add(joint);
                // A river above a fall does not overlap the water below.
                if (graph.Elements[joint.Channel].Body == this && !joint.Plunge && !joint.Crest) _overlaps.Add(joint);
            }
        }

        /// <summary>Queues a flow update. The water network calls it when its data for the body changes.</summary>
        internal void QueueNetworkFlow()
        {
            QueueFlow();
        }

        /// <summary>
        /// True when the vertex flow came from a water network, or from the last domain after a script reload. A body
        /// that built before its network was enabled has no network flow yet.
        /// </summary>
        internal bool HasNetworkFlow => _buildDataLost || _network;

        /// <summary>
        /// The network data that the vertex flow of this body has, when the flow was written from this network in this
        /// domain. False for a surface that came over a script reload or from a shipped file.
        /// </summary>
        internal bool TryGetWrittenFlow(LoogaWaterNetwork network, out Hash128 flow)
        {
            flow = _writtenFlow;
            return network && _writtenNetwork == network;
        }

        // A zone changes only the water near its own height, not a body far above or below it.
        private bool ReachesSurface(LoogaWaterFlow zone)
        {
            return zone.ReachesHeight(_worldHeights.x, _worldHeights.y);
        }

        // The world height range of the local bounds of the surface.
        private Vector2 WorldHeights(Vector3 min, Vector3 max)
        {
            var heights = new Vector2(float.MaxValue, float.MinValue);
            for (int corner = 0; corner < 8; corner++)
            {
                var local = new Vector3((corner & 1) == 0 ? min.x : max.x, (corner & 2) == 0 ? min.y : max.y,
                    (corner & 4) == 0 ? min.z : max.z);
                float height = transform.TransformPoint(local).y;
                heights = new Vector2(Mathf.Min(heights.x, height), Mathf.Max(heights.y, height));
            }
            return heights;
        }

        private static bool IsInlet(LoogaWaterFlow zone)
        {
            return zone.Shape == LoogaWaterFlow.FlowShape.Inlet;
        }

        private static bool IsCurrentBoundary(LoogaWaterFlow zone)
        {
            return zone.Shape == LoogaWaterFlow.FlowShape.Inlet || zone.Shape == LoogaWaterFlow.FlowShape.Outlet;
        }

        // Solves the current field from the inlet and outlet zones over the shore texture. Returns true when the
        // vertex flow must be written again.
        private bool SolveCurrents()
        {
            bool was = _currents.IsBuilt;
            ReadNetwork();
            // The joints of a water network are sources and sinks of the current, so they turn the solve on.
            if (!_solvedCurrents && _joints.Count == 0 || !_shore.IsBuilt || _shape != WaterShape.Area)
            {
                _currents.Clear();
                return was;
            }
            CurrentZones.Clear();
            foreach (LoogaWaterFlow zone in LoogaWaterFlow.Active)
            {
                if (zone && IsCurrentBoundary(zone) && zone.Reach.Overlaps(_worldRect) && ReachesSurface(zone))
                {
                    CurrentZones.Add(zone);
                }
            }
            if (!_currents.Build(_shore.Texture, _shore.TexelSize, _shore.Origin, CurrentZones, _joints))
            {
                _currents.Clear();
            }
            return true;
        }

        // Writes the vertex flow and the flow of the shore texture again after a new current field.
        private void ApplyCurrents()
        {
            if (!SolveCurrents() || _buildDataLost || !_mesh || VertexCount == 0 ||
                VertexCount != _mesh.vertexCount || _baseFlow.Count != VertexCount) return;

            WriteFlow();
            _shore.RefreshFlow(_mesh, transform.localToWorldMatrix);
        }

        private static int CompareZones(LoogaWaterFlow a, LoogaWaterFlow b)
        {
            int order = a.Order.CompareTo(b.Order);
            return order != 0 ? order : string.CompareOrdinal(a.name, b.name);
        }
        #endregion

        #region Shore
        /// <summary>
        /// Builds the shore texture from the surface mesh and the terrain heights, and gives it to the renderer.
        /// </summary>
        [ContextMenu("Rebuild Shore")]
        public void RebuildShore()
        {
            using ProfilerMarker.AutoScope build = LoogaWaterMarkers.Shore.Auto();
            _shoreQueued = false;
            if (!isActiveAndEnabled) return;

            float start = Time.realtimeSinceStartup;
            if (!_mesh || VertexCount == 0)
            {
                _shore.Release();
            }
            else if (!_shore.Build(_mesh, transform.localToWorldMatrix, ShoreRect, _shoreTexelSize, _obstacleLayers))
            {
                _shoreQueued = true;
            }
            ApplyCurrents();
            RebuildShoreWaves();
            RebuildPlungeWaves(true);
            ShoreMilliseconds = (Time.realtimeSinceStartup - start) * 1000f;
            ApplyShore();
            NewOutput();
        }

        // The arrival field depends on the whole shore, so every shore change builds it again.
        private void RebuildShoreWaves()
        {
            _nearShore.Invalidate();
            if (!_shoreWaves || !_shore.IsBuilt)
            {
                _shoreWaveField.Release();
                return;
            }
            Material material = GetComponent<MeshRenderer>().sharedMaterial;
            float period = material && material.HasProperty(ShoreWavePeriodId)
                ? material.GetFloat(ShoreWavePeriodId)
                : DefaultShoreWavePeriod;
            _shoreWaveField.Build(_shore.Texture, _shore.TexelSize, period,
                _shoreWaveSwell ? SwellVector() : Vector2.zero);
        }

        // The plunge waves run out from the plunge lines of the network falls into this body. They need the shore
        // texture and a material with a Plunge Wave Height above 0. The field is built again when the lines, the
        // period or (with force) the shore change. Returns true when the texture changed.
        private bool RebuildPlungeWaves(bool force)
        {
            _plungeLines.Clear();
            // The last solve of the network, as for the impact foam. A body that adopted its output over a script
            // reload has no joints until its flow builds again.
            LoogaWaterNetwork network = _network ? _network : LoogaWaterNetwork.Find(this);
            WaterNetworkGraph graph = network ? network.SolvedGraph : null;
            int jointCount = graph != null ? graph.JointFlows.Count : 0;
            for (int index = 0; index < jointCount; index++)
            {
                WaterJointFlow joint = graph.JointFlows[index];
                if (!joint.Plunge || graph.Elements[joint.Body].Body != this) continue;

                Vector2 across = new Vector2(-joint.Direction.y, joint.Direction.x) * joint.HalfWidth;
                _plungeLines.Add(new Vector4(joint.Point.x - across.x, joint.Point.y - across.y,
                    joint.Point.x + across.x, joint.Point.y + across.y));
            }
            Material material = GetComponent<MeshRenderer>().sharedMaterial;
            bool waves = material && material.HasFloat(PlungeWaveHeightId) &&
                material.GetFloat(PlungeWaveHeightId) > 0f;
            if (!waves || _plungeLines.Count == 0 || !_shore.IsBuilt)
            {
                bool had = _plungeWaveField.Texture;
                _plungeWaveField.Release();
                _plungeWaveInputs = default;
                return had;
            }
            float period = material.HasFloat(PlungeWavePeriodId)
                ? material.GetFloat(PlungeWavePeriodId)
                : DefaultPlungeWavePeriod;
            var inputs = new Hash128();
            inputs.Append(_shore.Texture.GetInstanceID());
            inputs.Append(_shore.TexelSize);
            inputs.Append(period);
            foreach (Vector4 line in _plungeLines)
            {
                Vector4 value = line;
                inputs.Append(ref value);
            }
            if (!force && inputs == _plungeWaveInputs && _plungeWaveField.Texture) return false;

            _plungeWaveInputs = inputs;
            _plungeWaveField.BuildFromLines(_shore.Texture, _shore.TexelSize, _shore.Origin, period, _plungeLines);
            return true;
        }

        // Unit vector in world XZ that the swell moves toward, the same convention as the ocean wind.
        private Vector2 SwellVector()
        {
            float angle = (90f - _swellDirection) * Mathf.Deg2Rad;
            return new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
        }

        // The shore texture covers the detail area of a body that runs far past its shores.
        private Rect ShoreRect => _detailArea.width > 0f ? _detailArea : _worldRect;

        // A zero rectangle tells the shader that there is no shore texture or no foam simulation.
        private void ApplyShore()
        {
            if (!_foamSimulation || !_shore.IsBuilt) _foam.Release();
            var meshRenderer = GetComponent<MeshRenderer>();
            _properties ??= new MaterialPropertyBlock();
            meshRenderer.GetPropertyBlock(_properties);
            if (_shore.IsBuilt) _properties.SetTexture(ShoreMapId, _shore.Texture);
            _properties.SetTexture(ShoreWaveMapId,
                _shoreWaveField.Texture ? _shoreWaveField.Texture : Texture2D.blackTexture);
            _properties.SetTexture(PlungeWaveMapId,
                _plungeWaveField.Texture ? _plungeWaveField.Texture : Texture2D.blackTexture);
            _properties.SetTexture(PlungeAlongMapId,
                _plungeWaveField.AlongTexture ? _plungeWaveField.AlongTexture : Texture2D.blackTexture);
            _properties.SetVector(ShoreMapRectId, _shore.UVRect);
            _properties.SetTexture(CoverageMapId, _coverage.IsBuilt ? _coverage.Texture : Texture2D.blackTexture);
            _properties.SetVector(CoverageRectId, _coverage.UVRect);
            _properties.SetFloat(CoverageOpenId, _coverage.OpenOutside ? 1f : 0f);
            _properties.SetTexture(ShoreFineMapId,
                _nearShore.IsBuilt && _nearShore.Texture ? _nearShore.Texture : Texture2D.blackTexture);
            _properties.SetVector(ShoreFineRectId, _nearShore.IsBuilt ? _nearShore.UVRect : Vector4.zero);
            if (_foam.Texture) _properties.SetTexture(FoamSimId, _foam.Texture);
            _properties.SetVector(FoamSimRectId, _foam.UVRect);
            _properties.SetVector(FoamSimFadeId, _foam.FadeSides);
            _properties.SetTexture(WeldMapId, _weldMap ? _weldMap : Texture2D.blackTexture);
            _properties.SetVector(WeldRectId, _weldMap ? _weldRect : Vector4.zero);
            _properties.SetVector(WeldParamsId, _weldParams);
            meshRenderer.SetPropertyBlock(_properties);
        }

        // Steps the foam simulation before a game or scene view camera renders.
        private void OnBeginCamera(ScriptableRenderContext context, Camera camera)
        {
            ApplySwashGlobals();
            UpdateNearShore(camera);
            // The network solves and the material values change at any time. The check is a hash of a few lines.
            if (RebuildPlungeWaves(false)) ApplyShore();
            if (!_foamSimulation || !_shore.IsBuilt) return;
            var meshRenderer = GetComponent<MeshRenderer>();
            if (camera.cameraType == CameraType.Game)
            {
                _lastGameCamera = Time.realtimeSinceStartup;
            }
            else if (camera.cameraType != CameraType.SceneView || Time.realtimeSinceStartup - _lastGameCamera < 1f)
            {
                ApplyFoamAge(meshRenderer);
                return;
            }
            // Foam that no camera sees does not move. It goes on from its last state when the body is in view again.
            if (!meshRenderer.isVisible) return;
            Material material = meshRenderer.sharedMaterial;
            GatherImpacts();
            Vector3 focus = FoamFocus(camera);
            LoogaWaterBody upstream = FindFoamUpstream(focus);
            Texture inflow = upstream ? upstream._foam.Texture : null;
            Vector4 inflowRect = upstream ? upstream._foam.UVRect : Vector4.zero;
            if (_foam.Update(ShoreRect, focus, _shore.Texture, _shore.RegimeTexture, _shore.UVRect, material,
                    _foamWindow, _foamTexelSize, _foamLifetime, _foamConvergence, inflow, inflowRect))
            {
                ApplyShore();
            }
            ApplyFoamAge(meshRenderer);
        }

        // The Inflow From body, or in a water network the upstream body of the joint nearest the foam window.
        private LoogaWaterBody FindFoamUpstream(Vector3 focus)
        {
            if (_inflowFrom) return _inflowFrom;

            LoogaWaterNetwork network = _network ? _network : LoogaWaterNetwork.Find(this);
            return network ? network.FindUpstream(this, new Vector2(focus.x, focus.z), _foamWindow) : null;
        }

        // Center of the foam window of a large body. A camera lower than FoamLeadStartHeight of the window keeps the
        // window around it wherever it looks, so a turn does not move the window and the foam behind the camera is
        // still there when it turns back (a lead in the view direction cut off the foam trail behind it, and the trail
        // popped in again after the turn back). A higher camera moves the window toward the point where its view meets
        // the water, at most FoamWindowLead of the window and its height above the lead start. A window around a high
        // camera left most of the water in view outside it.
        private Vector3 FoamFocus(Camera camera)
        {
            Transform view = camera.transform;
            Vector3 forward = view.forward;
            var ahead = new Vector2(forward.x, forward.z);
            float flat = ahead.magnitude;
            if (flat < 1e-4f) return view.position;

            float height = view.position.y - transform.position.y;
            float lift = height - _foamWindow * FoamLeadStartHeight;
            float reach = Mathf.Min(_foamWindow * FoamWindowLead, Mathf.Max(lift, 0f));
            float distance = forward.y < -1e-4f ? Mathf.Clamp(height * flat / -forward.y, 0f, reach) : reach;
            ahead /= flat;
            return view.position + new Vector3(ahead.x, 0f, ahead.y) * distance;
        }

        // The foam steps 20 times per second. The shader moves it on with the flow over the time since the last step,
        // so fast foam does not move in jumps.
        private void ApplyFoamAge(MeshRenderer meshRenderer)
        {
            _properties ??= new MaterialPropertyBlock();
            meshRenderer.GetPropertyBlock(_properties);
            _properties.SetFloat(FoamSimAgeId, _foam.StepAge);
            meshRenderer.SetPropertyBlock(_properties);
        }

        // The inlet zones of the body are where falling water lands, for example below a spillway. The water comes in
        // at the first point of the inlet line, across the zone width, so the foam simulation makes white water on that
        // cross line. A slow inlet, such as a river mouth, makes none: the impact foam starts at 0.3 m/s and is full at
        // 1 m/s. The plunge of each network fall into the body is an impact line across the fall width.
        private void GatherImpacts()
        {
            ImpactSegments.Clear();
            ImpactStrengths.Clear();
            // The last solve of the network. A solve on each camera would hash all bodies every frame.
            LoogaWaterNetwork network = _network ? _network : LoogaWaterNetwork.Find(this);
            WaterNetworkGraph graph = network ? network.SolvedGraph : null;
            int jointCount = graph != null ? graph.JointFlows.Count : 0;
            for (int index = 0; index < jointCount; index++)
            {
                WaterJointFlow joint = graph.JointFlows[index];
                if (!joint.Plunge || graph.Elements[joint.Body].Body != this || !joint.Reach.Overlaps(ShoreRect))
                {
                    continue;
                }

                if (joint.Crest)
                {
                    AddCrestImpacts(joint);
                    continue;
                }
                Vector2 across = new Vector2(-joint.Direction.y, joint.Direction.x) * joint.HalfWidth;
                Vector2 start = joint.Point - across;
                Vector2 end = joint.Point + across;
                ImpactSegments.Add(new Vector4(start.x, start.y, end.x, end.y));
                ImpactStrengths.Add(1f);
            }
            foreach (LoogaWaterFlow zone in LoogaWaterFlow.Active)
            {
                if (!zone || zone.Shape != LoogaWaterFlow.FlowShape.Inlet || !zone.Reach.Overlaps(ShoreRect) ||
                    !ReachesSurface(zone)) continue;

                float strength = zone.Strength * Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.3f, 1f, zone.Speed));
                if (strength <= 0f) continue;
                foreach (List<Vector2> line in zone.Lines)
                {
                    if (line.Count < 2) continue;

                    Vector2 along = (line[1] - line[0]).normalized;
                    Vector2 across = new Vector2(-along.y, along.x) * zone.Width;
                    Vector2 start = line[0] - across;
                    Vector2 end = line[0] + across;
                    ImpactSegments.Add(new Vector4(start.x, start.y, end.x, end.y));
                    ImpactStrengths.Add(strength);
                }
            }
            _foam.SetImpacts(ImpactSegments, ImpactStrengths);
        }

        // A crest lands along its whole lip. Foam over the whole landing line made broad, smooth plumes in the
        // outflow. The line of a crest piece breaks into short boils of random strength with gaps between them. Each
        // boil wanders slowly along the line and pulses, so the outflow carries thin, wandering streaks of foam.
        private static void AddCrestImpacts(WaterJointFlow joint)
        {
            Vector2 across = new Vector2(-joint.Direction.y, joint.Direction.x);
            float length = 2f * joint.HalfWidth;
            int boils = Mathf.Max(1, Mathf.RoundToInt(length / CrestBoilSpacing));
            float time = Time.realtimeSinceStartup;
            for (int boil = 0; boil < boils; boil++)
            {
                float seed = Hash01(joint.Point.x * 0.731f + joint.Point.y * 1.379f + boil * 17.13f);
                float wander = 0.35f * Mathf.Sin(time * Mathf.Lerp(0.08f, 0.2f, seed) + seed * 40f);
                float share = (boil + 0.5f + wander) / boils - 0.5f;
                float half = Mathf.Lerp(CrestBoilLength.x, CrestBoilLength.y, Hash01(seed * 91.7f)) * 0.5f;
                Vector2 centre = joint.Point + across * (share * length);
                Vector2 start = centre - across * half;
                Vector2 end = centre + across * half;
                float pulse = 0.5f + 0.5f * Mathf.Sin(time * Mathf.Lerp(0.3f, 0.7f, seed) + seed * 23f);
                ImpactSegments.Add(new Vector4(start.x, start.y, end.x, end.y));
                ImpactStrengths.Add(Mathf.Lerp(CrestBoilStrength.x, CrestBoilStrength.y, pulse));
            }
        }

        private static float Hash01(float value)
        {
            float sine = Mathf.Sin(value * 12.9898f) * 43758.547f;
            return sine - Mathf.Floor(sine);
        }

        // A body with shore waves reads the ground every half metre in a window around the camera, so the water edge, the
        // shore waves and the shore foam follow the ground and not the large texels of the shore texture. Game cameras
        // move the window. Scene view cameras move it only when no game camera renders.
        private void UpdateNearShore(Camera camera)
        {
            if (!_shoreWaves || !_shore.IsBuilt || _shape != WaterShape.Area)
            {
                if (!_nearShore.IsBuilt) return;

                _nearShore.Clear();
                ApplyShore();
                return;
            }
            // A build runs in a job. Its result goes in when it is ready.
            if (_nearShore.TryComplete()) ApplyShore();
            if (camera.cameraType == CameraType.Game)
            {
                _lastGameCamera = Time.realtimeSinceStartup;
            }
            else if (camera.cameraType != CameraType.SceneView || Time.realtimeSinceStartup - _lastGameCamera < 1f)
            {
                return;
            }
            if (!_nearShore.NeedsBuild(camera.transform.position)) return;

            _nearShore.Schedule(_shore, transform.position.y, camera.transform.position);
        }

        // Gives the terrain the swash of this body, so it wets the ground where the water runs up. The material
        // values can change at any time, so this runs before each camera.
        private void ApplySwashGlobals()
        {
            Material material = GetComponent<MeshRenderer>().sharedMaterial;
            bool swash = _shoreWaves && _shore.IsBuilt && _shoreWaveField.Texture && material &&
                material.IsKeywordEnabled("_OCEAN_WAVES") &&
                material.HasFloat(SwashHeightId) && material.GetFloat(SwashHeightId) > 0f &&
                material.GetFloat(ShoreWaveHeightId) > 0f;
            if (!swash)
            {
                if (_swashOwner == this) ClearSwashGlobals();
                return;
            }
            if (_swashOwner && _swashOwner != this) return;

            _swashOwner = this;
            Shader.SetGlobalVector(GlobalSwashId, new Vector4(transform.position.y, material.GetFloat(SwashHeightId),
                material.GetFloat(ShoreWavePeriodId), 1f));
            Shader.SetGlobalTexture(GlobalSwashShoreMapId, _shore.Texture);
            Shader.SetGlobalVector(GlobalSwashShoreRectId, _shore.UVRect);
            Shader.SetGlobalTexture(GlobalSwashWaveMapId, _shoreWaveField.Texture);
            // The swash runs over flat beaches, where coarse terrain patches move the water edge.
            TerrainRenderer.SetShoreDetail(transform.position.y, ShoreDetailErrorScale);
        }

        private static void ClearSwashGlobals()
        {
            _swashOwner = null;
            Shader.SetGlobalVector(GlobalSwashId, Vector4.zero);
            TerrainRenderer.ClearShoreDetail();
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
                if (world) world.UpdateSettled -= OnTerrainSettled;
            }
            _worlds.Clear();
        }

        /// <summary>
        /// Reads the terrain again in a world XZ area and updates the shore texture. The stamp worlds call it after the
        /// terrain changes. Call it after other changes to the terrain under the water.
        /// </summary>
        public void RefreshShore(Rect region)
        {
            if (!region.Overlaps(_worldRect) || !isActiveAndEnabled) return;
            if (!_shore.IsBuilt)
            {
                RebuildShore();
                return;
            }
            float start = Time.realtimeSinceStartup;
            // Most settled areas, for example after a texture edit, do not change the ground under the water. The
            // currents, the shore waves and the flow of the joined rivers then stay. For the sea they took about 7 s.
            if (_shore.Refresh(region, out bool changed) && !changed) return;

            ApplyCurrents();
            RebuildShoreWaves();
            RebuildPlungeWaves(true);
            ShoreMilliseconds = (Time.realtimeSinceStartup - start) * 1000f;
            ApplyShore();
            NewOutput();
        }

        /// <summary>
        /// Sets the weld line of a fall sheet that this body feeds. The surface ends exactly on the line and calms
        /// toward it. Parameters: x = distance from the line to the brink, y = calm distance before the line, z = share
        /// of the ripples at the brink, in metres. One fall per body: a second fall replaces the first.
        /// </summary>
        internal void SetWeld(LoogaWaterFall owner, Texture map, Vector4 rect, Vector4 parameters)
        {
            if (_weldOwner && _weldOwner != owner && _weldOwner.isActiveAndEnabled)
            {
                Debug.LogWarning($"{name}: {owner.name} replaces the weld of {_weldOwner.name}. A water body can " +
                                 "feed only one welded fall.", this);
            }
            _weldOwner = owner;
            _weldMap = map;
            _weldRect = rect;
            _weldParams = parameters;
            if (isActiveAndEnabled) ApplyShore();
        }

        /// <summary>Removes the weld line of a fall, if that fall set it.</summary>
        internal void ClearWeld(LoogaWaterFall owner)
        {
            if (_weldOwner != owner) return;

            _weldOwner = null;
            _weldMap = null;
            _weldRect = Vector4.zero;
            _weldParams = Vector4.zero;
            if (isActiveAndEnabled) ApplyShore();
        }

        // The heights are written and synchronized to the CPU when the stamp world raises this event.
        private void OnTerrainSettled(Rect region)
        {
            RefreshShore(region);
        }
        #endregion

        #region Changes
        private void OnSplineChanged(UnityEngine.Splines.Spline spline, int knotIndex, SplineModification modification)
        {
            if (!_spline) return;

            foreach (UnityEngine.Splines.Spline own in _spline.Splines)
            {
                if (own != spline) continue;

                QueueBuild();
                return;
            }
        }

        private void OnFlowChanged(Rect region)
        {
            if (_shippedLoading) return;
            if (region.Overlaps(_worldRect)) QueueFlow();
        }

#if UNITY_EDITOR
        // An undo can change the spline knots without a spline event. Most undo steps change no water body.
        private void OnUndoRedo()
        {
            if (GetBuildInputs() != _builtInputs) QueueBuild();
        }
#endif

        // The inputs of the mesh and the shore, except the terrain and the obstacle colliders.
        private Hash128 GetBuildInputs()
        {
            var hash = new Hash128();
            Matrix4x4 matrix = GetSplineMatrix();
            hash.Append(ref matrix);
            WaterSplines.AppendKnots(ref hash, _spline);
            hash.Append((int)_shape);
            hash.Append(_widths.Count);
            foreach (LoogaSplinePath.SplineWidths widths in _widths)
            {
                hash.Append(widths?.Points.Count ?? -1);
                if (widths == null) continue;
                foreach (Vector2 point in widths.Points)
                {
                    Vector2 value = point;
                    hash.Append(ref value);
                }
            }
            hash.Append(_width);
            hash.Append(_surfaceOffset);
            Vector2 endFade = _endFade;
            hash.Append(ref endFade);
            hash.Append(_vertexSpacing);
            hash.Append(_edgeSpacing);
            hash.Append(_wavePadding);
            Rect detailArea = _detailArea;
            hash.Append(ref detailArea);
            hash.Append(_detailReach);
            hash.Append(_flowSpeed);
            hash.Append(_flowDirection);
            hash.Append(_bankSlowdown);
            hash.Append(_shoreTexelSize);
            hash.Append(_obstacleLayers.value);
            return hash;
        }

        // Spline points in the local space of this body.
        private Matrix4x4 GetSplineMatrix()
        {
            return _spline ? transform.worldToLocalMatrix * _spline.transform.localToWorldMatrix : Matrix4x4.identity;
        }

        private void QueueBuild()
        {
            if (_buildQueued) return;

            _buildQueued = true;
#if UNITY_EDITOR
            if (Application.isPlaying) return;

            UnityEditor.EditorApplication.delayCall += () =>
            {
                if (this && _buildQueued) Rebuild();
            };
#endif
        }

        private void QueueFlow()
        {
            // The flow zones report a change when they load after a script reload. The mesh already has their flow.
            if (_flowQueued || _buildQueued || GeneratedReloadHandover.KeepsOutputOnLoad) return;

            _flowQueued = true;
#if UNITY_EDITOR
            if (Application.isPlaying) return;

            UnityEditor.EditorApplication.delayCall += () =>
            {
                if (this && _flowQueued && !_buildQueued) RebuildFlow();
            };
#endif
        }

        private string ReloadKey => "LoogaSoft.Terrain.WaterBody." + GetInstanceID();

        // The output as the next domain reads it after a script reload. Shore 0 is a body without a shore texture.
        [Serializable]
        private sealed class ReloadState
        {
            public int Mesh;
            public int VertexCount;
            public Rect WorldRect;
            public Matrix4x4 BuiltMatrix;
            public string BuiltInputs;
            public float BuildMilliseconds;
            public int Shore;
            public int Regime;
            public int ShoreWidth;
            public int ShoreHeight;
            public float ShoreTexel;
            public Vector2 ShoreOrigin;
            public int Coverage;
            public Rect CoverageRect;
            public bool CoverageOpen;
            public string OutputKey;
        }

        // Stores the output for the next domain. Returns false when the body must build after the reload. The foam
        // simulation starts again.
        private bool StoreForReload()
        {
#if UNITY_EDITOR
            if (_buildQueued || _flowQueued || _shoreQueued || !_mesh || VertexCount == 0) return false;
            GeneratedReloadHandover.Store(ReloadKey, new ReloadState
            {
                Mesh = _mesh.GetInstanceID(),
                VertexCount = VertexCount,
                WorldRect = _worldRect,
                BuiltMatrix = _builtMatrix,
                BuiltInputs = _builtInputs.ToString(),
                BuildMilliseconds = BuildMilliseconds,
                Shore = _shore.IsBuilt ? _shore.Texture.GetInstanceID() : 0,
                Regime = _shore.IsBuilt && _shore.RegimeTexture ? _shore.RegimeTexture.GetInstanceID() : 0,
                ShoreWidth = _shore.Width,
                ShoreHeight = _shore.Height,
                ShoreTexel = _shore.TexelSize,
                ShoreOrigin = _shore.Origin,
                Coverage = _coverage.IsBuilt ? _coverage.Texture.GetInstanceID() : 0,
                CoverageRect = _coverage.Rect,
                CoverageOpen = _coverage.OpenOutside,
                OutputKey = _outputKey.ToString()
            });
            _foam.Release();
            return true;
#else
            return false;
#endif
        }

        // Takes the output of the last domain after a script reload. Returns false when the body must build.
        private bool AdoptAfterReload()
        {
#if UNITY_EDITOR
            if (!GeneratedReloadHandover.TryTake(ReloadKey, out ReloadState state)) return false;
            var mesh = GeneratedReloadHandover.Find<Mesh>(state.Mesh);
            var shore = GeneratedReloadHandover.Find<Texture2D>(state.Shore);
            var regime = GeneratedReloadHandover.Find<Texture2D>(state.Regime);
            var coverage = GeneratedReloadHandover.Find<Texture2D>(state.Coverage);
            if (!mesh || state.Shore != 0 && !shore || state.Coverage != 0 && !coverage ||
                !GeneratedReloadHandover.CanAdopt)
            {
                if (mesh) DestroyImmediate(mesh);
                if (shore) DestroyImmediate(shore);
                if (regime) DestroyImmediate(regime);
                if (coverage) DestroyImmediate(coverage);
                return false;
            }

            _mesh = mesh;
            VertexCount = state.VertexCount;
            _worldRect = state.WorldRect;
            _worldHeights = WorldHeights(mesh.bounds.min, mesh.bounds.max);
            _builtMatrix = state.BuiltMatrix;
            _builtInputs = Hash128.Parse(state.BuiltInputs);
            _outputKey = Hash128.Parse(state.OutputKey ?? string.Empty);
            BuildMilliseconds = state.BuildMilliseconds;
            _buildQueued = false;
            _flowQueued = false;
            _shoreQueued = false;
            _buildDataLost = true;
            GetComponent<MeshFilter>().sharedMesh = _mesh;
            if (shore)
            {
                _shore.Adopt(shore, regime, state.ShoreWidth, state.ShoreHeight, state.ShoreTexel, state.ShoreOrigin,
                    null);
            }
            if (coverage)
            {
                _coverage.Adopt(coverage, state.CoverageRect, state.CoverageOpen);
            }
            RebuildShoreWaves();
            ApplyShore();
            return true;
#else
            return false;
#endif
        }

        private void Release()
        {
            VertexCount = 0;
            _shoreQueued = false;
            _shore.Release();
            _coverage.Release();
            _shoreWaveField.Release();
            _plungeWaveField.Release();
            _plungeWaveInputs = default;
            _nearShore.Release();
            _foam.Release();
            if (!_mesh) return;

            if (Application.isPlaying)
            {
                Destroy(_mesh);
            }
            else
            {
                DestroyImmediate(_mesh);
            }
            _mesh = null;
        }
        #endregion
    }
}
