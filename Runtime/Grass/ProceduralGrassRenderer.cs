using System;
using System.Collections.Generic;
using LoogaSoft.Terrain.Instances;
using LoogaSoft.Terrain.Surface;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace LoogaSoft.Terrain.Grass
{
    /// <summary>
    /// Draws procedural grass blades on terrains. Each frame a compute pass places the blades in camera-centred
    /// tiles from the terrain heights and the detail layer coverage. A G-buffer pass then builds each blade in the
    /// vertex shader. The blades are opaque and cast no shadow-map shadows. Blades also grow on the ground parts of
    /// meshes in <see cref="GrassGround"/>, such as cliff wall tops, with the grass of the terrain that they show.
    /// The renderer also adds its grass to the ground cover maps of the <see cref="LoogaTerrainSurfaceGlobals"/> on the
    /// GPU (<see cref="ILoogaGroundCoverComposer"/>), so the far ground follows each grass edit in the next frame.
    /// </summary>
    [ExecuteAlways]
    [AddComponentMenu("Looga/Terrain/Procedural Grass Renderer")]
    public sealed class ProceduralGrassRenderer : MonoBehaviour, ILoogaGroundCoverComposer
    {
        /// <summary>Maps one terrain detail layer to a grass type.</summary>
        [Serializable]
        public sealed class GrassLayer
        {
            [Tooltip("Index in the terrain detail prototype list.")]
            [SerializeField, Min(0)] private int _detailLayer;
            [SerializeField] private ProceduralGrassType _type;

            public int DetailLayer => _detailLayer;
            public ProceduralGrassType Type => _type;

            public GrassLayer(int detailLayer, ProceduralGrassType type)
            {
                _detailLayer = detailLayer;
                _type = type;
            }
        }

        [Header("Source")]
        [Tooltip("Terrains that grow grass. Empty uses every active terrain that has the mapped detail layers.")]
        [SerializeField] private UnityEngine.Terrain[] _terrains = Array.Empty<UnityEngine.Terrain>();
        [Tooltip("Detail layers drawn as procedural grass. At most 8.")]
        [SerializeField] private GrassLayer[] _layers = Array.Empty<GrassLayer>();

        [Header("Density")]
        [Tooltip("Blades per square meter of the finest candidate lattice. Types cannot be denser.")]
        [SerializeField, Range(4f, 1000f)] private float _maximumDensity = 250f;
        [Tooltip("Distance in meters with the full density. The density then falls smoothly to one quarter for " +
                 "each doubling of the distance.")]
        [SerializeField, Min(2f)] private float _fullDensityDistance = 25f;

        [Header("Distance")]
        [Tooltip("Distance in meters where the last blades end. The terrain color shows beyond it.")]
        [SerializeField, Min(1f)] private float _bladeDistance = 120f;
        [Tooltip("Share of its own visible distance over which each blade shrinks before it ends.")]
        [SerializeField, Range(0.05f, 0.9f)] private float _fadeShare = 0.2f;
        [Tooltip("Distance in meters where blades change from 15 to 7 vertices.")]
        [SerializeField, Min(0f)] private float _lodDistance = 15f;

        [Header("Width")]
        [Tooltip("Widening of thinned blades with distance. 0 = no widening, 1 = constant width on screen.")]
        [SerializeField, Range(0f, 1f)] private float _widthCompensation = 0.5f;
        [Tooltip("Smallest blade width on screen in pixels. Narrower blades get this width. 2 or more keeps far grass " +
                 "stable under temporal antialiasing.")]
        [SerializeField, Range(0f, 3f)] private float _minimumPixelWidth = 1f;
        [Tooltip("Share of the widened blades that are removed. 0 = all stay, so far grass gets denser. 1 = the field " +
                 "keeps the coverage of its natural widths.")]
        [SerializeField, Range(0f, 1f)] private float _widthThinning;
        [Tooltip("Multiplier of the minimum pixel width for cameras without temporal antialiasing, such as the " +
                 "Scene view. Wider far blades flicker less without it.")]
        [SerializeField, Range(1f, 3f)] private float _minimumPixelWidthWithoutTaa = 1.5f;
        [Tooltip("Color and occlusion of blades only one to four pixels wide move to the average of the blade, so " +
                 "their root to tip contrast does not show as static under temporal antialiasing. 0 = off. Measured: little gain " +
                 "(43 to 37 % flicker with TAA High) and a flatter field, so it stays off.")]
        [SerializeField, Range(0f, 1f)] private float _thinBladeSmoothing;
        [Tooltip("Extra width of blades that the camera sees edge-on.")]
        [SerializeField, Range(0f, 2f)] private float _viewWidening = 0.6f;
        [Tooltip("0 = each blade sways with its own random phase. 1 = neighbouring blades sway together in waves. " +
                 "Random phases move adjacent blades in opposite directions every frame, which shows as jitter on " +
                 "thin blades, most of all without temporal antialiasing.")]
        [SerializeField, Range(0f, 1f)] private float _swayCoherence;
        [Tooltip("Smallest width in pixels that each blade row shows on screen, also for edge-on and tapered rows. " +
                 "Narrower rows get wider across the view. The blade does not turn, so the facing and the shading " +
                 "stay. 0 = off.")]
        [SerializeField, Range(0f, 3f)] private float _minimumScreenWidth;

        [Header("Temporal Antialiasing")]
        [Tooltip("Draws the blades again into the motion vector texture of cameras with temporal antialiasing, so the " +
                 "antialiasing follows the wind sway. Costs a second blade draw. Off by default: on blades of 1 to 3 " +
                 "pixels the reprojected history lands on other blades, and the field flickers more (7 to 10 % of the " +
                 "grass pixels against 1 %, measured 2026-10-22). It can help near grass that sways several pixels.")]
        [SerializeField] private bool _motionVectors;

        [Header("Contact Shadows")]
        [Tooltip("Length in meters of the screen-space shadow march toward the sun. Looga Lighting shades the blades " +
                 "with it, because the blades cast no shadow-map shadows. 0 turns it off.")]
        [SerializeField, Range(0f, 3f)] private float _contactShadowLength = 0.6f;
        [Tooltip("March steps. More steps find thinner blades and cost more.")]
        [SerializeField, Range(2, 24)] private int _contactShadowSteps = 10;
        [Tooltip("Depth in meters that a blade in front of the march can hide. Larger values give wider shadows.")]
        [SerializeField, Range(0.02f, 1f)] private float _contactShadowThickness = 0.3f;
        [Tooltip("Darkening of a shadowed blade. 1 = no direct sunlight.")]
        [SerializeField, Range(0f, 1f)] private float _contactShadowStrength = 0.8f;

        [Header("Far Ground Cover")]
        [Tooltip("Canopy closure of the far ground cover for each square meter of blade footprint on a square meter of " +
                 "ground. The grass of the renderer adds 1 - exp(-closure x footprint) to the coverage map.")]
        [SerializeField, Min(0f)] private float _coverClosure = 0.49f;

        [Header("Budget")]
        [Tooltip("Blade capacity of each camera. A quarter is kept for the near level of detail.")]
        [SerializeField, Range(16384, 2000000)] private int _maximumBlades = 400000;

        [Header("Diagnostics")]
        [Tooltip("Reads the blade counts back from the GPU about twice per second.")]
        [SerializeField] private bool _readBladeCounts;

        private const int MaximumTypes = 8;
        private const int BladesPerInstance = 16;
        private const int NearRows = 7;
        private const int FarRows = 3;
        private const int BladeStride = 64;
        // About 4 MB of coverage uploads in each frame.
        private const int CoverageTexelsPerFrame = 4 * 1024 * 1024;

        private static readonly List<ProceduralGrassRenderer> ActiveRenderers = new();
        private static readonly CustomSampler GenerateSampler = CustomSampler.Create("Looga Grass Generate", true);
        private static readonly CustomSampler DrawSampler = CustomSampler.Create("Looga Grass Draw", true);
        private static readonly CustomSampler MotionSampler = CustomSampler.Create("Looga Grass Motion", true);
        // Passes of LoogaProceduralGrass.shader.
        private const int GBufferPass = 0;
        private const int MotionPass = 1;

        private readonly Dictionary<Camera, CameraState> _cameras = new();
        private readonly List<TerrainState> _terrainStates = new();
        private readonly List<CoverageUpload> _coverageUploads = new();
        private readonly GrassGroundMap _ground = new();
        private readonly List<Vector3> _groundCameras = new();
        private readonly GrassGroundCover _cover = new();
        private HashSet<UnityEngine.Terrain> _replacedTerrains = new();
        private Texture2D _staging;
        private readonly List<GrassTile> _ringTiles = new();
        private readonly List<Camera> _destroyedCameras = new();
        private readonly Plane[] _planes = new Plane[6];
        private readonly Vector4[] _planeVectors = new Vector4[6];
        private readonly ProceduralGrassType.GpuData[] _typeData = new ProceduralGrassType.GpuData[MaximumTypes];
        private readonly Vector4[] _shCoefficients = new Vector4[7];

        private ComputeShader _compute;
        private Material _material;
        private GraphicsBuffer _indices;
        private GraphicsBuffer _types;
        private ProceduralGrassPass _pass;
        private ProceduralGrassMotionPass _motionPass;
        private int _clearKernel;
        private int _generateKernel;
        private int _argumentsKernel;
        private int _typeCount;
        private bool _coverageDirty = true;
        private bool _typesDirty;
        // True after the first build of the terrains. Later builds and type uploads raise GrassChanged.
        private bool _built;
        private int _readbackFrame;
        // The surface globals that bind the ground cover maps with this grass, and the next time to search for them.
        private LoogaTerrainSurfaceGlobals _globals;
        private float _globalsSearchTime;

        /// <summary>Blade counts of the last read back: near and far level of detail.</summary>
        public Vector2Int LastBladeCounts { get; private set; }

        /// <summary>Tiles of the last prepared camera after the culling.</summary>
        public int LastTileCount { get; private set; }

        /// <summary>Terrains with uploaded coverage.</summary>
        public int TerrainCount => _terrainStates.Count;

        /// <summary>The detail layers that the renderer draws, with their grass types.</summary>
        public IReadOnlyList<GrassLayer> Layers => _layers;

        /// <summary>Lattice points per square meter: the most blades that all types together can draw.</summary>
        public float MaximumDensity => _maximumDensity;

        /// <summary>The GPU time samplers of the generate and draw passes.</summary>
        public static CustomSampler GenerateTimeSampler => GenerateSampler;
        public static CustomSampler DrawTimeSampler => DrawSampler;

        #region Built-in
        private void OnEnable()
        {
            _compute = Resources.Load<ComputeShader>("LoogaProceduralGrass");
            Shader shader = Resources.Load<Shader>("LoogaProceduralGrass");
            if (!SystemInfo.supportsComputeShaders || !_compute || !shader)
            {
                Debug.LogWarning("Procedural grass needs compute shaders and its Resources shaders.", this);
                return;
            }
            _material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            _clearKernel = _compute.FindKernel("ClearCounters");
            _generateKernel = _compute.FindKernel("Generate");
            _argumentsKernel = _compute.FindKernel("WriteArguments");
            _indices = CreateIndices();
            _types = new GraphicsBuffer(GraphicsBuffer.Target.Structured, MaximumTypes, ProceduralGrassType.GpuStride);
            _pass = new ProceduralGrassPass(this);
            _motionPass = new ProceduralGrassMotionPass(this);
            _coverageDirty = true;
            _built = false;
            TerrainCallbacks.heightmapChanged += OnHeightmapChanged;
            ProceduralGrassType.Edited += OnTypeEdited;
            if (ActiveRenderers.Count == 0)
            {
                RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
            }
            ActiveRenderers.Add(this);
            _cover.MarkGrassChanged();
            _globalsSearchTime = 0f;
            FindGlobals();
        }

        private void OnDisable()
        {
            ProceduralGrassType.Edited -= OnTypeEdited;
            if (ActiveRenderers.Remove(this) && ActiveRenderers.Count == 0)
            {
                RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
                Shader.SetGlobalVector(ShaderIds.LeafContactShadows, Vector4.zero);
            }
            foreach (CameraState state in _cameras.Values)
            {
                state.Dispose();
            }
            _cameras.Clear();
            TerrainCallbacks.heightmapChanged -= OnHeightmapChanged;
            ReleaseTerrains();
            _ground.Dispose();
            ReplacedDetailLayers.Clear(this);
            _replacedTerrains.Clear();
            if (_staging)
            {
                DestroyImmediate(_staging);
            }
            _staging = null;
            _indices?.Release();
            _indices = null;
            _types?.Release();
            _types = null;
            if (_material)
            {
                DestroyImmediate(_material);
            }
            _material = null;
            _pass = null;
            _motionPass = null;
            if (_globals && _globals.GroundCoverComposer == (ILoogaGroundCoverComposer)this)
            {
                _globals.SetGroundCoverComposer(null);
            }
            _globals = null;
            _cover.Dispose();
        }

        private void OnValidate()
        {
            _coverageDirty = true;
            _cover.MarkGrassChanged();
        }
        #endregion

        /// <summary>
        /// Occurs when a renderer draws changed grass data after its first build: an edited type, or rebuilt terrain
        /// coverage, for example after a layer change. Ground tools that color the far ground from the grass build
        /// their maps again. Detail layer writes of the stamps do not raise it.
        /// </summary>
        public static event Action<ProceduralGrassRenderer> GrassChanged;

        /// <summary>Uploads the detail layer coverage again, for example after the stamps changed it.</summary>
        [ContextMenu("Refresh Coverage")]
        public void RefreshCoverage()
        {
            _coverageDirty = true;
        }

        /// <summary>Sets the source terrains and layers. Tools and tests use it.</summary>
        public void SetSource(UnityEngine.Terrain[] terrains, GrassLayer[] layers)
        {
            _terrains = terrains ?? Array.Empty<UnityEngine.Terrain>();
            _layers = layers ?? Array.Empty<GrassLayer>();
            _coverageDirty = true;
        }

        #region Camera
        private static void OnBeginCameraRendering(ScriptableRenderContext context, Camera camera)
        {
            if (camera.cameraType != CameraType.Game && camera.cameraType != CameraType.SceneView) return;
            if (camera.orthographic) return;
            foreach (ProceduralGrassRenderer renderer in ActiveRenderers)
            {
                renderer.PrepareCamera(camera);
            }
        }

        private void PrepareCamera(Camera camera)
        {
            if (!_material || !_compute) return;
            // A compute shader that failed to compile has invalid kernels. Their dispatch removed the D3D12 device and
            // crashed the editor, so the grass then does not draw.
            if (!_compute.IsSupported(_clearKernel) || !_compute.IsSupported(_generateKernel) ||
                !_compute.IsSupported(_argumentsKernel)) return;
            if (_coverageDirty)
            {
                BuildTerrains();
                _coverageDirty = false;
                _typesDirty = false;
                if (_built)
                {
                    GrassChanged?.Invoke(this);
                }
                _built = true;
            }
            else if (_typesDirty)
            {
                UploadTypes();
                GrassChanged?.Invoke(this);
            }
            if (!_globals)
            {
                FindGlobals();
            }
            if (_terrainStates.Count > 0 && _typeCount > 0)
            {
                UploadCoverage();
            }
            _cover.ComposeIfChanged(_terrainStates, _layers, _typeCount, _maximumDensity, _coverClosure);
            if (_terrainStates.Count == 0 || _typeCount == 0) return;
            ReleaseDestroyedCameras();
            // The ground map holds the mesh ground near this camera and the other cameras that draw the grass.
            _groundCameras.Clear();
            _groundCameras.Add(camera.transform.position);
            foreach (KeyValuePair<Camera, CameraState> entry in _cameras)
            {
                if (entry.Key != camera) _groundCameras.Add(entry.Value.CameraPosition);
            }
            _ground.Update(_groundCameras, _bladeDistance);
            if (!_cameras.TryGetValue(camera, out CameraState state))
            {
                state = new CameraState();
                _cameras.Add(camera, state);
            }
            state.EnsureCapacity(_maximumBlades);
            Vector3 position = camera.transform.position;
            ProceduralGrassTiles.Build(new Vector2(position.x, position.z), CellSize, _fullDensityDistance,
                _bladeDistance, _ringTiles);
            GeometryUtility.CalculateFrustumPlanes(camera, _planes);
            for (int index = 0; index < 6; index++)
            {
                _planeVectors[index] = new Vector4(_planes[index].normal.x, _planes[index].normal.y,
                    _planes[index].normal.z, _planes[index].distance);
            }
            state.CameraPosition = position;
            CullTiles(state);
            LastTileCount = state.Tiles.Count;
            if (state.Tiles.Count == 0) return;
            state.UploadTiles();
            state.PixelScale = 2f * Mathf.Tan(0.5f * camera.fieldOfView * Mathf.Deg2Rad) /
                Mathf.Max(1, camera.pixelHeight);
            _pass.Prepare(state);
            Shader.SetGlobalVector(ShaderIds.LeafContactShadows, new Vector4(_contactShadowLength,
                _contactShadowSteps, _contactShadowThickness, _contactShadowLength > 0f ? _contactShadowStrength : 0f));
            UniversalAdditionalCameraData cameraData = camera.GetUniversalAdditionalCameraData();
            state.TemporalAntialiasing = cameraData.antialiasing == AntialiasingMode.TemporalAntiAliasing;
            // With temporal antialiasing the march noise steps through an 8-frame cycle (value 1 to 8), which the
            // history averages. A start from the clock gave a new random pattern each frame, which showed as fizz on
            // still grass. Without temporal antialiasing the per-frame change shows as flicker, so it stays still (0).
            state.NoiseFrame = (state.NoiseFrame + 1) & 7;
            Shader.SetGlobalFloat(ShaderIds.LeafContactShadowJitter, state.TemporalAntialiasing ? 1 + state.NoiseFrame : 0f);
            cameraData.scriptableRenderer.EnqueuePass(_pass);
            // Only temporal antialiasing reads the motion vectors. Other cameras do not need the extra blade draw.
            if (_motionVectors && state.TemporalAntialiasing)
            {
                _motionPass.Prepare(state);
                cameraData.scriptableRenderer.EnqueuePass(_motionPass);
            }
        }

        // Temporary cameras, such as capture cameras, keep no buffers after they are destroyed.
        private void ReleaseDestroyedCameras()
        {
            _destroyedCameras.Clear();
            foreach (KeyValuePair<Camera, CameraState> entry in _cameras)
            {
                if (!entry.Key)
                {
                    _destroyedCameras.Add(entry.Key);
                }
            }
            foreach (Camera camera in _destroyedCameras)
            {
                _cameras[camera].Dispose();
                _cameras.Remove(camera);
            }
        }

        private void CullTiles(CameraState state)
        {
            state.Tiles.Clear();
            state.Batches.Clear();
            float cellSize = CellSize;
            Vector3 cameraPosition = state.CameraPosition;
            float bladeDistanceSquared = _bladeDistance * _bladeDistance;
            foreach (TerrainState terrain in _terrainStates)
            {
                if (!terrain.Ready || !terrain.Terrain || !terrain.Terrain.terrainData) continue;
                int first = state.Tiles.Count;
                foreach (GrassTile tile in _ringTiles)
                {
                    Rect rect = ProceduralGrassTiles.WorldRect(tile, cellSize);
                    if (!rect.Overlaps(terrain.Rect)) continue;
                    // Tall blades and wind can reach out of the terrain height bounds. Mesh ground can be higher.
                    float maximumHeight = _ground.HasGround && rect.Overlaps(_ground.Rect)
                        ? Mathf.Max(terrain.MaximumHeight, _ground.MaximumHeight)
                        : terrain.MaximumHeight;
                    var bounds = new Bounds();
                    bounds.SetMinMax(new Vector3(rect.xMin, terrain.MinimumHeight, rect.yMin),
                        new Vector3(rect.xMax, maximumHeight + 2f, rect.yMax));
                    if (!GeometryUtility.TestPlanesAABB(_planes, bounds)) continue;
                    // The rings are flat, so a high camera can be farther than the blade distance from a whole tile.
                    if (bounds.SqrDistance(cameraPosition) > bladeDistanceSquared) continue;
                    state.Tiles.Add(tile);
                }
                if (state.Tiles.Count > first)
                {
                    state.Batches.Add(new TileBatch(terrain, first, state.Tiles.Count - first));
                }
            }
        }

        private float CellSize => 1f / Mathf.Sqrt(_maximumDensity);
        #endregion

        #region Generate
        internal void Generate(UnsafeCommandBuffer commands, CameraState state)
        {
            commands.BeginSample(GenerateSampler);
            int ringCount = ProceduralGrassTiles.RingCount(_fullDensityDistance, _bladeDistance);
            int nearCapacity = state.Capacity / 4;
            commands.SetComputeBufferParam(_compute, _clearKernel, ShaderIds.Counters, state.Counters);
            commands.DispatchCompute(_compute, _clearKernel, 1, 1, 1);

            commands.SetComputeVectorParam(_compute, ShaderIds.Camera, state.CameraPosition);
            commands.SetComputeVectorArrayParam(_compute, ShaderIds.Planes, _planeVectors);
            commands.SetComputeVectorParam(_compute, ShaderIds.Distances, new Vector4(CellSize,
                _fullDensityDistance, _bladeDistance, _fadeShare));
            commands.SetComputeVectorParam(_compute, ShaderIds.Pixels, new Vector4(state.PixelScale,
                MinimumPixels(state), _widthThinning, 0f));
            commands.SetComputeVectorParam(_compute, ShaderIds.Detail, new Vector4(_lodDistance, _widthCompensation,
                ringCount, _typeCount));
            commands.SetComputeIntParam(_compute, ShaderIds.NearCapacity, nearCapacity);
            commands.SetComputeIntParam(_compute, ShaderIds.FarCapacity, state.Capacity - nearCapacity);
            commands.SetComputeBufferParam(_compute, _generateKernel, ShaderIds.Tiles, state.TileBuffer);
            commands.SetComputeBufferParam(_compute, _generateKernel, ShaderIds.Types, _types);
            commands.SetComputeBufferParam(_compute, _generateKernel, ShaderIds.Blades, state.Blades);
            commands.SetComputeBufferParam(_compute, _generateKernel, ShaderIds.Counters, state.Counters);
            BindWorldColor(commands);
            commands.SetComputeTextureParam(_compute, _generateKernel, ShaderIds.GroundHeight, _ground.HeightTexture);
            commands.SetComputeTextureParam(_compute, _generateKernel, ShaderIds.GroundOffset, _ground.OffsetTexture);
            commands.SetComputeVectorParam(_compute, ShaderIds.GroundArea, _ground.Area);
            commands.SetComputeVectorParam(_compute, ShaderIds.GroundTexels, _ground.Texels);
            foreach (TileBatch batch in state.Batches)
            {
                TerrainState terrain = batch.Terrain;
                commands.SetComputeTextureParam(_compute, _generateKernel, ShaderIds.Heightmap, terrain.Heightmap);
                commands.SetComputeTextureParam(_compute, _generateKernel, ShaderIds.Holes, terrain.Holes);
                commands.SetComputeTextureParam(_compute, _generateKernel, ShaderIds.Coverage, terrain.Coverage);
                commands.SetComputeVectorParam(_compute, ShaderIds.TerrainOrigin, terrain.Origin);
                commands.SetComputeVectorParam(_compute, ShaderIds.TerrainSize, terrain.Size);
                commands.SetComputeIntParam(_compute, ShaderIds.TileOffset, batch.First);
                commands.DispatchCompute(_compute, _generateKernel, ProceduralGrassTiles.TileLanes / 8,
                    ProceduralGrassTiles.TileLanes / 8, batch.Count);
            }

            commands.SetComputeIntParam(_compute, ShaderIds.NearIndexCount, NearIndexCount);
            commands.SetComputeIntParam(_compute, ShaderIds.FarIndexCount, FarIndexCount);
            commands.SetComputeBufferParam(_compute, _argumentsKernel, ShaderIds.Counters, state.Counters);
            commands.SetComputeBufferParam(_compute, _argumentsKernel, ShaderIds.Arguments, state.Arguments);
            commands.DispatchCompute(_compute, _argumentsKernel, 1, 1, 1);
            commands.EndSample(GenerateSampler);
            RequestCounts(state);
        }

        // Compute shaders do not read global properties, so the world color map of LoogaTerrainSurfaceGlobals is
        // bound here. Without a map the state is 0 and placeholders fill the texture slots.
        private void BindWorldColor(UnsafeCommandBuffer commands)
        {
            Vector4 worldState = Shader.GetGlobalVector(ShaderIds.WorldColorState);
            Texture map = Shader.GetGlobalTexture(ShaderIds.WorldColorMap);
            Texture tiles = Shader.GetGlobalTexture(ShaderIds.WorldColorTiles);
            Texture index = Shader.GetGlobalTexture(ShaderIds.WorldColorTileIndex);
            bool tilesValid = tiles && tiles.dimension == TextureDimension.Tex2DArray && index;
            if (worldState.x > 1.5f && !tilesValid || worldState.x > 0.5f && worldState.x < 1.5f && !map)
            {
                worldState = Vector4.zero;
            }
            commands.SetComputeVectorParam(_compute, ShaderIds.WorldColorState, worldState);
            commands.SetComputeVectorParam(_compute, ShaderIds.WorldColorArea,
                Shader.GetGlobalVector(ShaderIds.WorldColorArea));
            commands.SetComputeVectorParam(_compute, ShaderIds.WorldColorTileGrid,
                Shader.GetGlobalVector(ShaderIds.WorldColorTileGrid));
            commands.SetComputeVectorParam(_compute, ShaderIds.WorldColorTileSlice,
                Shader.GetGlobalVector(ShaderIds.WorldColorTileSlice));
            commands.SetComputeTextureParam(_compute, _generateKernel, ShaderIds.WorldColorMap,
                map ? map : Texture2D.blackTexture);
            commands.SetComputeTextureParam(_compute, _generateKernel, ShaderIds.WorldColorTiles,
                tilesValid ? tiles : PlaceholderArray);
            commands.SetComputeTextureParam(_compute, _generateKernel, ShaderIds.WorldColorTileIndex,
                tilesValid ? index : Texture2D.blackTexture);
        }

        private static Texture2DArray _placeholderArray;

        private static Texture2DArray PlaceholderArray
        {
            get
            {
                if (_placeholderArray) return _placeholderArray;
                _placeholderArray = new Texture2DArray(1, 1, 1, TextureFormat.RGBA32, false, true)
                {
                    name = "Looga Grass Placeholder Array",
                    hideFlags = HideFlags.HideAndDontSave
                };
                _placeholderArray.SetPixels32(new[] { new Color32(0, 0, 0, 0) }, 0);
                _placeholderArray.Apply(false, true);
                return _placeholderArray;
            }
        }

        private void RequestCounts(CameraState state)
        {
            if (!_readBladeCounts || Time.frameCount - _readbackFrame < 30) return;
            _readbackFrame = Time.frameCount;
            AsyncGPUReadback.Request(state.Counters, 16, 0, request =>
            {
                if (request.hasError) return;
                // Slots 2 and 3 hold the counts that fit in the capacity.
                var counts = request.GetData<uint>();
                LastBladeCounts = new Vector2Int((int)counts[2], (int)counts[3]);
            });
        }
        #endregion

        #region Draw
        internal void Draw(RasterCommandBuffer commands, CameraState state)
        {
            commands.BeginSample(DrawSampler);
            SetAmbientProbe(state.Properties);
            DrawBlades(commands, state, GBufferPass);
            commands.EndSample(DrawSampler);
        }

        // Draws the visible blades again into the motion vector texture, with the motion of the wind and the camera.
        internal void DrawMotion(RasterCommandBuffer commands, CameraState state)
        {
            commands.BeginSample(MotionSampler);
            DrawBlades(commands, state, MotionPass);
            commands.EndSample(MotionSampler);
        }

        private void DrawBlades(RasterCommandBuffer commands, CameraState state, int pass)
        {
            MaterialPropertyBlock properties = state.Properties;
            properties.SetBuffer(ShaderIds.Blades, state.Blades);
            properties.SetBuffer(ShaderIds.Types, _types);
            properties.SetBuffer(ShaderIds.Counters, state.Counters);
            properties.SetVector(ShaderIds.DrawParameters, new Vector4(state.PixelScale, MinimumPixels(state),
                _viewWidening, state.Capacity / 4));
            properties.SetVector(ShaderIds.DrawParameters2, new Vector4(_lodDistance, _thinBladeSmoothing, _swayCoherence,
                _minimumScreenWidth));
            properties.SetInt(ShaderIds.Level, 0);
            commands.DrawProceduralIndirect(_indices, Matrix4x4.identity, _material, pass, MeshTopology.Triangles,
                state.Arguments, 0, properties);
            properties.SetInt(ShaderIds.Level, 1);
            commands.DrawProceduralIndirect(_indices, Matrix4x4.identity, _material, pass, MeshTopology.Triangles,
                state.Arguments, 5 * sizeof(uint), properties);
        }

        private float MinimumPixels(CameraState state)
        {
            return _minimumPixelWidth * (state.TemporalAntialiasing ? 1f : _minimumPixelWidthWithoutTaa);
        }

        // Procedural draws get no per-object light probe, so the blades read the ambient probe directly.
        private void SetAmbientProbe(MaterialPropertyBlock properties)
        {
            SphericalHarmonicsL2 probe = RenderSettings.ambientProbe;
            for (int channel = 0; channel < 3; channel++)
            {
                _shCoefficients[channel] = new Vector4(probe[channel, 3], probe[channel, 1], probe[channel, 2],
                    probe[channel, 0] - probe[channel, 6]);
                _shCoefficients[channel + 3] = new Vector4(probe[channel, 4], probe[channel, 5],
                    probe[channel, 6] * 3f, probe[channel, 7]);
            }
            _shCoefficients[6] = new Vector4(probe[0, 8], probe[1, 8], probe[2, 8], 1f);
            properties.SetVectorArray(ShaderIds.AmbientProbe, _shCoefficients);
        }

        private static int NearIndexCount => BladesPerInstance * (2 * NearRows - 1) * 3;
        private static int FarIndexCount => BladesPerInstance * (2 * FarRows - 1) * 3;

        // One instance draws 16 blades. Each blade is a strip of row pairs with one tip vertex.
        private static GraphicsBuffer CreateIndices()
        {
            var indices = new List<ushort>(NearIndexCount + FarIndexCount);
            AddBladeIndices(indices, NearRows);
            AddBladeIndices(indices, FarRows);
            var buffer = new GraphicsBuffer(GraphicsBuffer.Target.Index, indices.Count, sizeof(ushort));
            buffer.SetData(indices);
            return buffer;
        }

        private static void AddBladeIndices(List<ushort> indices, int rows)
        {
            int vertices = 2 * rows + 1;
            for (int blade = 0; blade < BladesPerInstance; blade++)
            {
                int first = blade * vertices;
                for (int row = 0; row < rows - 1; row++)
                {
                    int vertex = first + 2 * row;
                    indices.Add((ushort)vertex);
                    indices.Add((ushort)(vertex + 2));
                    indices.Add((ushort)(vertex + 1));
                    indices.Add((ushort)(vertex + 1));
                    indices.Add((ushort)(vertex + 2));
                    indices.Add((ushort)(vertex + 3));
                }
                int last = first + 2 * (rows - 1);
                indices.Add((ushort)last);
                indices.Add((ushort)(first + 2 * rows));
                indices.Add((ushort)(last + 1));
            }
        }
        #endregion

        #region Ground Cover
        bool ILoogaGroundCoverComposer.Compose(Texture coverage, Texture canopy, Vector4 area, float colorScale,
            out Texture composedCoverage, out Texture composedCanopy)
        {
            composedCoverage = null;
            composedCanopy = null;
            if (!isActiveAndEnabled || !_cover.SetBase(coverage, canopy, area, colorScale)) return false;
            _cover.ComposeIfChanged(_terrainStates, _layers, _typeCount, _maximumDensity, _coverClosure);
            composedCoverage = _cover.Coverage;
            composedCanopy = _cover.Canopy;
            return true;
        }

        // The surface globals of the scene take this renderer as their ground cover composer. A renderer that enables
        // before the globals searches again at most once per second.
        private void FindGlobals()
        {
            if (Time.realtimeSinceStartup < _globalsSearchTime) return;
            _globalsSearchTime = Time.realtimeSinceStartup + 1f;
            _globals = FindAnyObjectByType<LoogaTerrainSurfaceGlobals>();
            if (_globals && _globals.GroundCoverComposer == null)
            {
                _globals.SetGroundCoverComposer(this);
            }
        }
        #endregion

        #region Terrains
        // A type edit changes only the type data, so the coverage stays and the blades keep drawing.
        private void OnTypeEdited(ProceduralGrassType type)
        {
            foreach (GrassLayer layer in _layers)
            {
                if (layer != null && layer.Type == type)
                {
                    _typesDirty = true;
                    return;
                }
            }
        }

        private void UploadTypes()
        {
            _typesDirty = false;
            for (int index = 0; index < _typeCount; index++)
            {
                GrassLayer layer = _layers[index];
                if (layer == null || !layer.Type)
                {
                    _coverageDirty = true;
                    return;
                }
                _typeData[index] = layer.Type.ToGpu(_maximumDensity);
            }
            if (_typeCount > 0)
            {
                _types.SetData(_typeData);
            }
            _cover.MarkGrassChanged();
        }

        private void BuildTerrains()
        {
            ReleaseTerrains();
            _cover.MarkGrassChanged();
            _typeCount = 0;
            int layerCount = Mathf.Min(_layers.Length, MaximumTypes);
            int highestLayer = -1;
            for (int index = 0; index < layerCount; index++)
            {
                GrassLayer layer = _layers[index];
                if (layer == null || !layer.Type)
                {
                    ReplaceDetailLayers();
                    return;
                }
                _typeData[index] = layer.Type.ToGpu(_maximumDensity);
                highestLayer = Mathf.Max(highestLayer, layer.DetailLayer);
            }
            _typeCount = layerCount;
            if (_typeCount > 0)
            {
                _types.SetData(_typeData);
                UnityEngine.Terrain[] terrains = _terrains.Length > 0 ? _terrains : UnityEngine.Terrain.activeTerrains;
                foreach (UnityEngine.Terrain terrain in terrains)
                {
                    if (!terrain || !terrain.isActiveAndEnabled || !terrain.terrainData) continue;
                    TerrainData data = terrain.terrainData;
                    if (data.detailPrototypes.Length <= highestLayer) continue;
                    var state = new TerrainState(terrain, layerCount);
                    _terrainStates.Add(state);
                    _coverageUploads.Add(new CoverageUpload(state,
                        new RectInt(0, 0, data.detailResolution, data.detailResolution), true));
                }
            }
            ReplaceDetailLayers();
        }

        // The vegetation provider of each grass terrain stops drawing the mapped layers as cards. Terrains that left
        // the grass get their layers back.
        private void ReplaceDetailLayers()
        {
            var layers = new int[_typeCount];
            for (int index = 0; index < _typeCount; index++)
            {
                layers[index] = _layers[index].DetailLayer;
            }
            var current = new HashSet<UnityEngine.Terrain>();
            foreach (TerrainState state in _terrainStates)
            {
                current.Add(state.Terrain);
                ReplacedDetailLayers.Set(this, state.Terrain, layers);
            }
            foreach (UnityEngine.Terrain terrain in _replacedTerrains)
            {
                if (!current.Contains(terrain))
                {
                    ReplacedDetailLayers.Set(this, terrain, null);
                }
            }
            _replacedTerrains = current;
        }

        private void ReleaseTerrains()
        {
            foreach (TerrainState terrain in _terrainStates)
            {
                terrain.Dispose();
            }
            _terrainStates.Clear();
            _coverageUploads.Clear();
        }

        /// <summary>
        /// Uploads a changed area of the detail layers again. The stamps call it after they write detail layers,
        /// because Unity reports no detail changes.
        /// </summary>
        public static void InvalidateDetails(UnityEngine.Terrain terrain, RectInt region)
        {
            foreach (ProceduralGrassRenderer renderer in ActiveRenderers)
            {
                foreach (TerrainState state in renderer._terrainStates)
                {
                    if (state.Terrain != terrain) continue;
                    RectInt area = state.Clamp(region);
                    if (area.width > 0 && area.height > 0)
                    {
                        renderer._coverageUploads.Add(new CoverageUpload(state, area, false));
                    }
                }
            }
        }

        // Copies queued coverage areas to the GPU in row strips, at most CoverageTexelsPerFrame texels in each frame,
        // so that a large terrain or many terrains do not fill the upload ring buffer in one frame.
        private void UploadCoverage()
        {
            int budget = CoverageTexelsPerFrame;
            while (_coverageUploads.Count > 0 && budget > 0)
            {
                CoverageUpload upload = _coverageUploads[0];
                RectInt area = upload.Area;
                int rowTexels = area.width * _typeCount;
                int rows = Mathf.Clamp(budget / Mathf.Max(1, rowTexels), 1, area.height);
                upload.State.Upload(new RectInt(area.x, area.y, area.width, rows), _layers, _typeCount, ref _staging);
                _cover.MarkGrassChanged(upload.State.WorldArea(new RectInt(area.x, area.y, area.width, rows)));
                budget -= rows * rowTexels;
                if (rows < area.height)
                {
                    _coverageUploads[0] = new CoverageUpload(upload.State,
                        new RectInt(area.x, area.y + rows, area.width, area.height - rows), upload.Initial);
                    continue;
                }
                _coverageUploads.RemoveAt(0);
                if (upload.Initial)
                {
                    upload.State.Ready = true;
                }
            }
        }

        // The tile culling reads the terrain height range, which changes when the stamps write heights.
        private void OnHeightmapChanged(UnityEngine.Terrain terrain, RectInt region, bool synched)
        {
            foreach (TerrainState state in _terrainStates)
            {
                if (state.Terrain == terrain)
                {
                    state.ReadHeightRange();
                }
            }
        }

        private readonly struct CoverageUpload
        {
            internal readonly TerrainState State;
            internal readonly RectInt Area;
            // The first upload of a terrain. The terrain grows no blades until it is complete.
            internal readonly bool Initial;

            internal CoverageUpload(TerrainState state, RectInt area, bool initial)
            {
                State = state;
                Area = area;
                Initial = initial;
            }
        }

        // Coverage of the mapped detail layers in one R8 array slice each, and the terrain height textures.
        internal sealed class TerrainState : IDisposable
        {
            internal readonly UnityEngine.Terrain Terrain;
            internal readonly Texture2DArray Coverage;
            internal readonly Vector4 Origin;
            internal readonly Vector4 Size;
            internal readonly Rect Rect;
            internal float MinimumHeight;
            internal float MaximumHeight;
            internal bool Ready;

            internal TerrainState(UnityEngine.Terrain terrain, int layerCount)
            {
                Terrain = terrain;
                TerrainData data = terrain.terrainData;
                int resolution = data.detailResolution;
                Coverage = new Texture2DArray(resolution, resolution, layerCount, TextureFormat.R8, false, true)
                {
                    name = "Looga Grass Coverage " + terrain.name,
                    hideFlags = HideFlags.HideAndDontSave,
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear
                };
                // The GPU copy is the only copy. The uploads write areas into it.
                Coverage.Apply(false, true);
                Vector3 origin = terrain.transform.position;
                Origin = new Vector4(origin.x, origin.y, origin.z, data.heightmapResolution);
                Size = new Vector4(data.size.x, data.size.y, data.size.z, data.size.y * (65535f / 32766f));
                Rect = new Rect(origin.x, origin.z, data.size.x, data.size.z);
                ReadHeightRange();
            }

            // A save of the terrain data asset destroys its height and holes textures and makes new ones, so the
            // textures are read for each use and not kept.
            internal Texture Heightmap => Terrain.terrainData.heightmapTexture;

            internal Texture Holes
            {
                get
                {
                    Texture holes = Terrain.terrainData.holesTexture;
                    return holes ? holes : Texture2D.whiteTexture;
                }
            }

            internal void ReadHeightRange()
            {
                if (!Terrain || !Terrain.terrainData) return;
                Bounds bounds = Terrain.terrainData.bounds;
                float y = Terrain.transform.position.y;
                MinimumHeight = y + bounds.min.y;
                MaximumHeight = y + bounds.max.y;
            }

            // World XZ area of detail texels.
            internal Rect WorldArea(RectInt texels)
            {
                float scale = 1f / Mathf.Max(1, Terrain.terrainData.detailResolution);
                return Rect.MinMaxRect(Rect.xMin + texels.xMin * scale * Rect.width, Rect.yMin + texels.yMin * scale * Rect.height,
                    Rect.xMin + texels.xMax * scale * Rect.width, Rect.yMin + texels.yMax * scale * Rect.height);
            }

            internal RectInt Clamp(RectInt region)
            {
                int resolution = Coverage.width;
                int x0 = Mathf.Clamp(region.xMin, 0, resolution);
                int z0 = Mathf.Clamp(region.yMin, 0, resolution);
                int x1 = Mathf.Clamp(region.xMax, 0, resolution);
                int z1 = Mathf.Clamp(region.yMax, 0, resolution);
                return new RectInt(x0, z0, x1 - x0, z1 - z0);
            }

            // Reads one area of each mapped layer from the terrain and copies it into the array slices on the GPU.
            internal void Upload(RectInt area, GrassLayer[] layers, int layerCount, ref Texture2D staging)
            {
                if (!Terrain || !Terrain.terrainData) return;
                TerrainData data = Terrain.terrainData;
                if (!staging || staging.width != area.width || staging.height != area.height)
                {
                    if (staging)
                    {
                        DestroyImmediate(staging);
                    }
                    staging = new Texture2D(area.width, area.height, TextureFormat.R8, false, true)
                    {
                        name = "Looga Grass Coverage Staging",
                        hideFlags = HideFlags.HideAndDontSave
                    };
                }
                var bytes = new byte[area.width * area.height];
                for (int slice = 0; slice < layerCount; slice++)
                {
                    int[,] map = data.GetDetailLayer(area.x, area.y, area.width, area.height, layers[slice].DetailLayer);
                    for (int z = 0; z < area.height; z++)
                    {
                        for (int x = 0; x < area.width; x++)
                        {
                            bytes[z * area.width + x] = (byte)Mathf.Clamp(map[z, x], 0, 255);
                        }
                    }
                    staging.SetPixelData(bytes, 0);
                    staging.Apply(false, false);
                    Graphics.CopyTexture(staging, 0, 0, 0, 0, area.width, area.height, Coverage, slice, 0, area.x,
                        area.y);
                }
            }

            public void Dispose()
            {
                if (Coverage)
                {
                    DestroyImmediate(Coverage);
                }
            }
        }
        #endregion

        #region Camera State
        internal readonly struct TileBatch
        {
            internal readonly TerrainState Terrain;
            internal readonly int First;
            internal readonly int Count;

            internal TileBatch(TerrainState terrain, int first, int count)
            {
                Terrain = terrain;
                First = first;
                Count = count;
            }
        }

        // GPU buffers of one camera. The camera draws its own blades, so views do not overwrite each other.
        internal sealed class CameraState : IDisposable
        {
            internal readonly List<GrassTile> Tiles = new();
            internal readonly List<TileBatch> Batches = new();
            internal readonly MaterialPropertyBlock Properties = new();
            internal GraphicsBuffer TileBuffer;
            internal GraphicsBuffer Blades;
            internal GraphicsBuffer Counters;
            internal GraphicsBuffer Arguments;
            internal int Capacity;
            internal Vector3 CameraPosition;
            internal float PixelScale;
            internal bool TemporalAntialiasing;
            // Frame of the 8-frame contact shadow noise cycle. Counted per camera, because Time.frameCount stands
            // still in edit mode.
            internal int NoiseFrame;

            internal void EnsureCapacity(int capacity)
            {
                if (Blades != null && Capacity == capacity) return;
                Blades?.Release();
                Counters?.Release();
                Arguments?.Release();
                Capacity = capacity;
                Blades = new GraphicsBuffer(GraphicsBuffer.Target.Structured, capacity, BladeStride);
                Counters = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 4, sizeof(uint));
                Arguments = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments, 10, sizeof(uint));
            }

            internal void UploadTiles()
            {
                if (TileBuffer == null || TileBuffer.count < Tiles.Count)
                {
                    TileBuffer?.Release();
                    TileBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured,
                        Mathf.NextPowerOfTwo(Mathf.Max(64, Tiles.Count)), 4 * sizeof(int));
                }
                TileBuffer.SetData(Tiles);
            }

            public void Dispose()
            {
                TileBuffer?.Release();
                Blades?.Release();
                Counters?.Release();
                Arguments?.Release();
            }
        }
        #endregion

        private static class ShaderIds
        {
            internal static readonly int Counters = Shader.PropertyToID("_GrassCounters");
            internal static readonly int Arguments = Shader.PropertyToID("_GrassArguments");
            internal static readonly int Tiles = Shader.PropertyToID("_GrassTiles");
            internal static readonly int Types = Shader.PropertyToID("_GrassTypes");
            internal static readonly int Blades = Shader.PropertyToID("_GrassBlades");
            internal static readonly int Camera = Shader.PropertyToID("_GrassCamera");
            internal static readonly int Planes = Shader.PropertyToID("_GrassPlanes");
            internal static readonly int Distances = Shader.PropertyToID("_GrassDistances");
            internal static readonly int Detail = Shader.PropertyToID("_GrassDetail");
            internal static readonly int Pixels = Shader.PropertyToID("_GrassPixels");
            internal static readonly int NearCapacity = Shader.PropertyToID("_GrassNearCapacity");
            internal static readonly int FarCapacity = Shader.PropertyToID("_GrassFarCapacity");
            internal static readonly int Heightmap = Shader.PropertyToID("_GrassHeightmap");
            internal static readonly int Holes = Shader.PropertyToID("_GrassHoles");
            internal static readonly int Coverage = Shader.PropertyToID("_GrassCoverage");
            internal static readonly int TerrainOrigin = Shader.PropertyToID("_GrassTerrainOrigin");
            internal static readonly int TerrainSize = Shader.PropertyToID("_GrassTerrainSize");
            internal static readonly int GroundHeight = Shader.PropertyToID("_GrassGroundHeight");
            internal static readonly int GroundOffset = Shader.PropertyToID("_GrassGroundOffset");
            internal static readonly int GroundArea = Shader.PropertyToID("_GrassGroundArea");
            internal static readonly int GroundTexels = Shader.PropertyToID("_GrassGroundTexels");
            internal static readonly int TileOffset = Shader.PropertyToID("_GrassTileOffset");
            internal static readonly int NearIndexCount = Shader.PropertyToID("_GrassNearIndexCount");
            internal static readonly int FarIndexCount = Shader.PropertyToID("_GrassFarIndexCount");
            internal static readonly int DrawParameters = Shader.PropertyToID("_GrassDrawParameters");
            internal static readonly int DrawParameters2 = Shader.PropertyToID("_GrassDrawParameters2");            internal static readonly int Level = Shader.PropertyToID("_GrassLevel");
            internal static readonly int AmbientProbe = Shader.PropertyToID("_GrassAmbientProbe");
            // World color map of LoogaTerrainSurfaceGlobals (LoogaTerrainSurfaceWorld.hlsl).
            internal static readonly int WorldColorState = Shader.PropertyToID("_LoogaWorldColorState");
            internal static readonly int WorldColorMap = Shader.PropertyToID("_LoogaWorldColorMap");
            internal static readonly int WorldColorArea = Shader.PropertyToID("_LoogaWorldColorArea");
            internal static readonly int WorldColorTiles = Shader.PropertyToID("_LoogaWorldColorTiles");
            internal static readonly int WorldColorTileIndex = Shader.PropertyToID("_LoogaWorldColorTileIndex");
            internal static readonly int WorldColorTileGrid = Shader.PropertyToID("_LoogaWorldColorTileGrid");
            internal static readonly int WorldColorTileSlice = Shader.PropertyToID("_LoogaWorldColorTileSlice");
            // Looga Lighting (Graphics Pro) reads it for the thin leaves that mark themselves in the G-buffer.
            internal static readonly int LeafContactShadows = Shader.PropertyToID("_LoogaLeafContactShadows");
            internal static readonly int LeafContactShadowJitter = Shader.PropertyToID("_LoogaLeafContactShadowJitter");
        }
    }
}
