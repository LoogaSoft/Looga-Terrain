using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Profiling;
using UnityEngine.Rendering;

namespace LoogaSoft.Terrain.Water
{
    /// <summary>
    /// One wave cascade of <see cref="LoogaOceanWaves"/>: a square tile of waves with its own size, wind and shape.
    /// </summary>
    [System.Serializable]
    public sealed class OceanCascade
    {
        [Min(1f), Tooltip("Size of the repeating wave tile, in metres. Small tiles hold short, sharp waves.")]
        public float TileSize = 50f;
        [Range(0f, 2f), Tooltip("Multiplies the vertex movement of this cascade. 0 = the cascade only shapes the " +
                                "light (normals).")]
        public float DisplacementScale = 1f;
        [Range(0f, 2f), Tooltip("Multiplies the slopes of this cascade in the light (normals).")]
        public float NormalScale = 1f;
        [Min(0.1f), Tooltip("Wind speed 10 m above the water, in metres per second.")]
        public float WindSpeed = 10f;
        [Range(-360f, 360f), Tooltip("Direction the wind blows toward, in degrees clockwise from north (+Z).")]
        public float WindDirection = 110f;
        [Min(1f), Tooltip("Distance over which the wind blows on the water, in kilometres.")]
        public float Fetch = 150f;
        [Range(0f, 2f), Tooltip("Narrows the long waves to the wind direction, as for swell from far away.")]
        public float Swell = 0.8f;
        [Range(0f, 1f), Tooltip("0 = the waves follow the wind direction. 1 = the waves come from all directions.")]
        public float Spread = 0.2f;
        [Range(0f, 1f), Tooltip("1 = keep the small waves. Lower values remove the small waves.")]
        public float Detail = 1f;
        [Range(0f, 2f), Tooltip("Foam grows where the surface Jacobian is below this value. Higher = more foam.")]
        public float Whitecap = 0.5f;
        [Range(0f, 10f), Tooltip("Foam growth on the crests. Higher values also make the foam stay longer. 0 = this " +
                                 "cascade makes no foam.")]
        public float FoamAmount = 5f;
    }

    /// <summary>
    /// Wind waves for open water from wave spectra, made with an FFT on the GPU each frame. The techniques follow
    /// GodotOceanWaves (2Retr0, MIT): each cascade has its own tile size, wind and TMA spectrum with Hasselmann
    /// spreading, swell and detail. Foam grows where the surface is compressed and decays over time. The component sets
    /// global textures that Looga/Water materials with Ocean Waves on read. Use one in a scene.
    /// </summary>
    [ExecuteAlways]
    [AddComponentMenu("Looga/Water/Ocean Waves")]
    public sealed class LoogaOceanWaves : MonoBehaviour
    {
        public enum Resolution
        {
            Texels256 = 256,
            Texels512 = 512,
            Texels1024 = 1024
        }

        private const string ComputeName = "LoogaOceanFFT";
        private const int MaxCascades = 4;
        // A new frame runs the FFT once, even when several cameras render.
        private const double MinimumStep = 0.001;
        // Wave time wraps at this period, in seconds.
        private const double WaveTimePeriod = 100000.0;
        // After a pause longer than this, in seconds, the foam restarts. It then runs this many steps of the past at
        // the warm-up step, so the foam that a crest leaves shows at once.
        private const double FoamRestartGap = 0.5;
        private const int WarmUpSteps = 40;
        private const float WarmUpStep = 0.1f;

        private static readonly int SpectrumId = Shader.PropertyToID("_Spectrum");
        private static readonly int BufferAId = Shader.PropertyToID("_BufferA");
        private static readonly int BufferBId = Shader.PropertyToID("_BufferB");
        private static readonly int DisplacementId = Shader.PropertyToID("_Displacement");
        private static readonly int DerivativesId = Shader.PropertyToID("_Derivatives");
        private static readonly int CascadeWindId = Shader.PropertyToID("_CascadeWind");
        private static readonly int CascadeShapeId = Shader.PropertyToID("_CascadeShape");
        private static readonly int CascadeFoamId = Shader.PropertyToID("_CascadeFoam");
        private static readonly int OceanId = Shader.PropertyToID("_Ocean");
        private static readonly int WaveTimeId = Shader.PropertyToID("_WaveTime");
        private static readonly int FoamId = Shader.PropertyToID("_Foam");
        private static readonly int FoamPreviousId = Shader.PropertyToID("_FoamPrevious");
        private static readonly int FoamStepId = Shader.PropertyToID("_FoamStep");
        private static readonly int FoamDriftId = Shader.PropertyToID("_FoamDrift");
        private static readonly int GlobalDisplacementId = Shader.PropertyToID("_LoogaOceanDisplacement");
        private static readonly int GlobalDerivativesId = Shader.PropertyToID("_LoogaOceanDerivatives");
        private static readonly int GlobalFoamId = Shader.PropertyToID("_LoogaOceanFoam");
        private static readonly int GlobalWindId = Shader.PropertyToID("_LoogaOceanWind");
        private static readonly int GlobalParamsId = Shader.PropertyToID("_LoogaOceanParams");
        private static readonly int GlobalTimeId = Shader.PropertyToID("_LoogaOceanTime");
        // GPU time of the wave simulation (spectrum, FFT, fields and foam) in the profiler.
        private static readonly CustomSampler SimulateSampler = CustomSampler.Create("Looga Ocean.Simulate", true);
        private static CommandBuffer _sampleBegin;
        private static CommandBuffer _sampleEnd;
        private static readonly int GlobalCascadesId = Shader.PropertyToID("_LoogaOceanCascades");
        private static readonly int GlobalGridId = Shader.PropertyToID("_LoogaOceanGrid");
        private static readonly int GridMeshId = Shader.PropertyToID("_OceanGridMesh");
        private const string OceanKeyword = "_OCEAN_WAVES";
        // Vertices from the grid centre to its edge, along each axis.
        private const int GridSteps = 128;
        // The grid reaches this much past the radius where it hands over to the surface mesh.
        private const float GridMargin = 1.08f;

        [Header("Waves")]
        [Tooltip("Texels along each side of a cascade tile. More texels give sharper small waves and cost more.")]
        [SerializeField] private Resolution _resolution = Resolution.Texels512;
        [Tooltip("Up to four wave cascades. Each cascade holds every wave that its tile resolves.")]
        [SerializeField] private OceanCascade[] _cascades =
        {
            new OceanCascade
            {
                TileSize = 88f, DisplacementScale = 1f, NormalScale = 1f, WindSpeed = 10f, WindDirection = 110f,
                Fetch = 150f, Swell = 0.8f, Spread = 0.2f, Detail = 1f, Whitecap = 0.5f, FoamAmount = 9.5f
            },
            new OceanCascade
            {
                TileSize = 57f, DisplacementScale = 0.75f, NormalScale = 1f, WindSpeed = 5f, WindDirection = 105f,
                Fetch = 150f, Swell = 0.8f, Spread = 0.4f, Detail = 1f, Whitecap = 0.5f, FoamAmount = 0f
            },
            new OceanCascade
            {
                TileSize = 16f, DisplacementScale = 0f, NormalScale = 2f, WindSpeed = 20f, WindDirection = 110f,
                Fetch = 550f, Swell = 0.8f, Spread = 0.4f, Detail = 1f, Whitecap = 0.25f, FoamAmount = 0f
            }
        };
        [Tooltip("Water depth for the spectrum, in metres. Shallow water has shorter, steeper waves.")]
        [SerializeField, Min(0.5f)] private float _depth = 20f;
        [Tooltip("Multiplies the wave height.")]
        [SerializeField, Range(0f, 4f)] private float _heightScale = 0.5f;
        [Tooltip("Horizontal displacement toward the crests. Gives sharp crests and wide troughs. Too high values " +
                 "fold the surface over at the crests.")]
        [SerializeField, Range(0f, 2f)] private float _choppiness = 1f;
        [SerializeField] private int _seed = 17;

        [Header("Foam")]
        [Tooltip("Speed at which the foam drifts with the wind of the first cascade, in metres per second.")]
        [SerializeField, Range(0f, 2f)] private float _foamDrift = 0.2f;
        [Tooltip("Rate at which the foam spreads along the wind into streaks, per second.")]
        [SerializeField, Range(0f, 10f)] private float _foamStreak = 3f;

        [Header("Near Grid")]
        [Tooltip("Water surface that the near grid replaces around each camera. Its material needs Ocean Waves on. " +
                 "The grid has fine vertices near the camera, so short waves move the surface there.")]
        [SerializeField] private Renderer _surface;
        [Tooltip("Radius around the camera where the near grid draws, in metres. Set it where the grid cells are " +
                 "about as large as the vertex spacing of the surface mesh.")]
        [SerializeField, Min(10f)] private float _gridRadius = 480f;
        [Tooltip("Size of the grid cells at the camera, in metres. The cells grow with the distance.")]
        [SerializeField, Range(0.1f, 4f)] private float _gridCell = 0.5f;

        [Header("Preview")]
        [Tooltip("Stops the waves, the shore waves, the swash and the wet sand at Frozen Time, so that views and changes " +
                 "can be compared at the same moment. Turn it off for normal water.")]
        [SerializeField] private bool _freeze;
        [Tooltip("Wave time while the water is frozen, in seconds. One shore wave cycle is the Shore Wave Period of the " +
                 "water material (11 s by default); the run-up is in the first 30 % of a cycle, the backwash after it.")]
        [SerializeField, Min(0f)] private float _frozenTime = 10f;

        private readonly Vector4[] _cascadeWind = new Vector4[MaxCascades];
        // Frozen time of the last simulation, NaN when the water is not frozen.
        private float _frozenApplied = float.NaN;
        private readonly Vector4[] _cascadeShape = new Vector4[MaxCascades];
        private readonly Vector4[] _cascadeFoam = new Vector4[MaxCascades];
        private readonly Vector4[] _cascadeGlobals = new Vector4[MaxCascades];
        private ComputeShader _compute;
        private LocalKeyword _size512;
        private LocalKeyword _size1024;
        private int _initKernel;
        private int _evolveKernel;
        private int _rowsKernel;
        private int _columnsKernel;
        private int _assembleKernel;
        private int _foamKernel;
        private RenderTexture _spectrum;
        private RenderTexture _bufferA;
        private RenderTexture _bufferB;
        private RenderTexture _displacement;
        private RenderTexture _derivatives;
        private RenderTexture _foam;
        private RenderTexture _foamPrevious;
        private int _textureSize;
        private int _textureCascades;
        private bool _spectrumDirty = true;
        private double _lastStep = double.NegativeInfinity;
        // Total wind drift of the foam in world XZ, in metres. The shader moves the foam texture by the same amount.
        private double _foamOffsetX;
        private double _foamOffsetZ;
        private Mesh _gridMesh;
        private float _gridMeshRadius;
        private float _gridMeshCell;
        private MaterialPropertyBlock _gridBlock;

        public RenderTexture Displacement => _displacement;
        public RenderTexture Foam => _foam;
        public IReadOnlyList<OceanCascade> Cascades => _cascades;

        private int Size => (int)_resolution;
        private int CascadeCount => Mathf.Clamp(_cascades?.Length ?? 0, 0, MaxCascades);

        private void OnEnable()
        {
            _spectrumDirty = true;
            RenderPipelineManager.beginContextRendering += OnBeginContextRendering;
            RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
        }

        private void OnDisable()
        {
            RenderPipelineManager.beginContextRendering -= OnBeginContextRendering;
            RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
            Shader.SetGlobalVector(GlobalParamsId, Vector4.zero);
            Shader.SetGlobalVector(GlobalGridId, Vector4.zero);
            Release();
            DestroyMesh();
            // A script reload keeps the shader reference but not new kernel fields, so the kernels load again.
            _compute = null;
        }

        private void OnValidate()
        {
            _spectrumDirty = true;
        }

        private void OnBeginContextRendering(ScriptableRenderContext context, List<Camera> cameras)
        {
            double now = Application.isPlaying ? Time.timeAsDouble : Time.realtimeSinceStartupAsDouble;
            if (_freeze)
            {
                // A frozen ocean simulates once at the frozen time, with the foam that the waves before it left.
                _lastStep = now;
                if (CascadeCount == 0 || !Load()) return;
                if (_frozenApplied == _frozenTime && !_spectrumDirty && _displacement) return;

                _frozenApplied = _frozenTime;
                Simulate(_frozenTime, FoamRestartGap + 1.0);
                return;
            }
            _frozenApplied = float.NaN;
            if (now - _lastStep < MinimumStep && _displacement) return;
            if (CascadeCount == 0 || !Load()) return;

            double step = now - _lastStep;
            _lastStep = now;
            Simulate((float)(now % WaveTimePeriod), step);
        }

        // Draws the near grid for a camera and tells the surface mesh where the grid draws. The grid moves in steps
        // of two centre cells, so its vertices stay on the same world points between steps.
        private void OnBeginCameraRendering(ScriptableRenderContext context, Camera camera)
        {
            Material material = _surface ? _surface.sharedMaterial : null;
            bool active = _displacement && material && _surface.enabled && _surface.gameObject.activeInHierarchy &&
                          material.IsKeywordEnabled(OceanKeyword) && camera.cameraType != CameraType.Preview;
            if (!active)
            {
                Shader.SetGlobalVector(GlobalGridId, Vector4.zero);
                return;
            }

            EnsureGridMesh();
            float step = _gridCell * 2f;
            Vector3 position = camera.transform.position;
            float x = Mathf.Round(position.x / step) * step;
            float z = Mathf.Round(position.z / step) * step;
            float level = _surface.bounds.center.y;
            Shader.SetGlobalVector(GlobalGridId, new Vector4(x, z, _gridRadius, 1f));

            _gridBlock ??= new MaterialPropertyBlock();
            _surface.GetPropertyBlock(_gridBlock);
            _gridBlock.SetFloat(GridMeshId, 1f);
            var parameters = new RenderParams(material)
            {
                camera = camera,
                layer = _surface.gameObject.layer,
                renderingLayerMask = _surface.renderingLayerMask,
                shadowCastingMode = ShadowCastingMode.Off,
                receiveShadows = true,
                matProps = _gridBlock,
                worldBounds = new Bounds(new Vector3(x, level, z),
                    new Vector3(_gridRadius * 2.4f, 40f, _gridRadius * 2.4f))
            };
            Graphics.RenderMesh(parameters, _gridMesh, 0, Matrix4x4.Translate(new Vector3(x, level, z)));
        }

        // A square grid whose cells grow by a constant ratio from the centre. UV0 w holds the cell size, which the
        // shader uses as the vertex spacing.
        private void EnsureGridMesh()
        {
            if (_gridMesh && Mathf.Approximately(_gridMeshRadius, _gridRadius) &&
                Mathf.Approximately(_gridMeshCell, _gridCell)) return;

            DestroyMesh();
            float extent = _gridRadius * GridMargin;
            float ratio = GrowthRatio(extent / _gridCell);
            int side = GridSteps * 2 + 1;
            var offsets = new float[side];
            var cells = new float[side];
            for (int index = 0; index < side; index++)
            {
                int steps = Mathf.Abs(index - GridSteps);
                float reach = _gridCell * (Mathf.Pow(ratio, steps) - 1f) / (ratio - 1f);
                offsets[index] = Mathf.Sign(index - GridSteps) * reach;
                cells[index] = _gridCell * Mathf.Pow(ratio, Mathf.Max(steps - 1, 0));
            }

            var vertices = new Vector3[side * side];
            var data = new Vector4[side * side];
            for (int row = 0; row < side; row++)
            {
                for (int column = 0; column < side; column++)
                {
                    int vertex = row * side + column;
                    vertices[vertex] = new Vector3(offsets[column], 0f, offsets[row]);
                    data[vertex] = new Vector4(0f, 0f, 1f, Mathf.Max(cells[column], cells[row]));
                }
            }
            var indices = new int[(side - 1) * (side - 1) * 6];
            int write = 0;
            for (int row = 0; row < side - 1; row++)
            {
                for (int column = 0; column < side - 1; column++)
                {
                    int corner = row * side + column;
                    indices[write++] = corner;
                    indices[write++] = corner + side;
                    indices[write++] = corner + 1;
                    indices[write++] = corner + 1;
                    indices[write++] = corner + side;
                    indices[write++] = corner + side + 1;
                }
            }
            _gridMesh = new Mesh
            {
                name = "Looga Ocean Near Grid",
                hideFlags = HideFlags.DontSave,
                indexFormat = IndexFormat.UInt32
            };
            _gridMesh.SetVertices(vertices);
            _gridMesh.SetUVs(0, data);
            _gridMesh.SetTriangles(indices, 0);
            _gridMesh.bounds = new Bounds(Vector3.zero, new Vector3(extent * 2f, 40f, extent * 2f));
            _gridMeshRadius = _gridRadius;
            _gridMeshCell = _gridCell;
        }

        // Ratio r of the cell growth so that GridSteps cells, the first one cell wide, reach the extent (in cells).
        private static float GrowthRatio(float extentInCells)
        {
            float low = 1.0001f;
            float high = 2f;
            for (int iteration = 0; iteration < 60; iteration++)
            {
                float middle = 0.5f * (low + high);
                float reach = (Mathf.Pow(middle, GridSteps) - 1f) / (middle - 1f);
                if (reach > extentInCells)
                {
                    high = middle;
                }
                else
                {
                    low = middle;
                }
            }
            return 0.5f * (low + high);
        }

        private void DestroyMesh()
        {
            if (!_gridMesh) return;
            if (Application.isPlaying)
            {
                Object.Destroy(_gridMesh);
            }
            else
            {
                Object.DestroyImmediate(_gridMesh);
            }
            _gridMesh = null;
        }

        // Moves the waves to the time and the foam by the step, in seconds. After a long pause the foam restarts and
        // first runs the warm-up steps before the time.
        private void Simulate(float time, double step)
        {
            // The dispatches go straight to the GPU, so the sample commands go there too.
            if (_sampleBegin == null)
            {
                _sampleBegin = new CommandBuffer { name = "Looga Ocean Sample Begin" };
                _sampleBegin.BeginSample(SimulateSampler);
                _sampleEnd = new CommandBuffer { name = "Looga Ocean Sample End" };
                _sampleEnd.EndSample(SimulateSampler);
            }
            Graphics.ExecuteCommandBuffer(_sampleBegin);
            SimulateWaves(time, step);
            Graphics.ExecuteCommandBuffer(_sampleEnd);
        }

        private void SimulateWaves(float time, double step)
        {
            bool restart = step > FoamRestartGap;
            if (!_displacement || _textureSize != Size || _textureCascades != CascadeCount)
            {
                Release();
                _textureSize = Size;
                _textureCascades = CascadeCount;
                _spectrum = CreateArray("Looga Ocean Spectrum", GraphicsFormat.R32G32B32A32_SFloat, false);
                _bufferA = CreateArray("Looga Ocean Buffer A", GraphicsFormat.R32G32B32A32_SFloat, false);
                _bufferB = CreateArray("Looga Ocean Buffer B", GraphicsFormat.R32G32B32A32_SFloat, false);
                _displacement = CreateArray("Looga Ocean Displacement", GraphicsFormat.R16G16B16A16_SFloat, true);
                _derivatives = CreateArray("Looga Ocean Derivatives", GraphicsFormat.R16G16B16A16_SFloat, true);
                _foam = CreateArray("Looga Ocean Foam", GraphicsFormat.R16_SFloat, true);
                _foamPrevious = CreateArray("Looga Ocean Foam Previous", GraphicsFormat.R16_SFloat, true);
                _spectrumDirty = true;
                restart = true;
            }

            _compute.SetKeyword(_size512, Size == 512);
            _compute.SetKeyword(_size1024, Size == 1024);
            if (_spectrumDirty)
            {
                SetParameters();
                _compute.SetTexture(_initKernel, SpectrumId, _spectrum);
                _compute.Dispatch(_initKernel, Size / 8, Size / 8, CascadeCount);
                _spectrumDirty = false;
            }

            if (restart)
            {
                for (int index = WarmUpSteps; index > 0; index--)
                {
                    Step(time - index * WarmUpStep, WarmUpStep, index == WarmUpSteps);
                }
            }
            Step(time, Mathf.Clamp((float)step, 0f, WarmUpStep), false);
            _displacement.GenerateMips();
            _derivatives.GenerateMips();
            _foam.GenerateMips();

            Shader.SetGlobalTexture(GlobalDisplacementId, _displacement);
            Shader.SetGlobalTexture(GlobalDerivativesId, _derivatives);
            Shader.SetGlobalTexture(GlobalFoamId, _foam);
            Vector2 wind = WindVector();
            Shader.SetGlobalVector(GlobalWindId, new Vector4(wind.x, wind.y, (float)_foamOffsetX, (float)_foamOffsetZ));
            for (int cascade = 0; cascade < MaxCascades; cascade++)
            {
                OceanCascade settings = cascade < CascadeCount ? _cascades[cascade] : null;
                _cascadeGlobals[cascade] = settings == null
                    ? Vector4.zero
                    : new Vector4(1f / Mathf.Max(settings.TileSize, 1f), settings.DisplacementScale,
                        settings.NormalScale, Mathf.Max(settings.TileSize, 1f));
            }
            Shader.SetGlobalVectorArray(GlobalCascadesId, _cascadeGlobals);
            Shader.SetGlobalVector(GlobalParamsId, new Vector4(CascadeCount, Size, 0f, 0f));
            // The shore waves of the water shader use the same clock as the cascades.
            Shader.SetGlobalFloat(GlobalTimeId, time);
        }

        // One wave and foam step: the spectrum at the time, the FFT, the fields, then the foam. A clear step starts the
        // foam from nothing.
        private void Step(float time, float step, bool clear)
        {
            int groups = Size / 8;
            _compute.SetFloat(WaveTimeId, time);
            _compute.SetTexture(_evolveKernel, SpectrumId, _spectrum);
            _compute.SetTexture(_evolveKernel, BufferAId, _bufferA);
            _compute.SetTexture(_evolveKernel, BufferBId, _bufferB);
            _compute.Dispatch(_evolveKernel, groups, groups, CascadeCount);
            DispatchFFT(_rowsKernel);
            DispatchFFT(_columnsKernel);
            _compute.SetTexture(_assembleKernel, BufferAId, _bufferA);
            _compute.SetTexture(_assembleKernel, BufferBId, _bufferB);
            _compute.SetTexture(_assembleKernel, DisplacementId, _displacement);
            _compute.SetTexture(_assembleKernel, DerivativesId, _derivatives);
            _compute.Dispatch(_assembleKernel, groups, groups, CascadeCount);

            (_foam, _foamPrevious) = (_foamPrevious, _foam);
            Vector2 wind = WindVector();
            double drift = _foamDrift * step;
            _foamOffsetX += wind.x * drift;
            _foamOffsetZ += wind.y * drift;
            // Foam growth and decay of GodotOceanWaves: more foam amount grows faster and decays slower.
            for (int cascade = 0; cascade < CascadeCount; cascade++)
            {
                OceanCascade settings = _cascades[cascade];
                float decayRate = Mathf.Max(0.5f, 10f - settings.FoamAmount) * 1.15f;
                _cascadeFoam[cascade] = new Vector4(settings.Whitecap, step * settings.FoamAmount * 7.5f,
                    Mathf.Exp(-step * decayRate), 0f);
            }
            _compute.SetVectorArray(CascadeFoamId, _cascadeFoam);
            float streak = 1f - Mathf.Exp(-step * _foamStreak);
            _compute.SetVector(FoamStepId, new Vector4(streak, 0f, 0f, clear ? 0f : 1f));
            _compute.SetVector(FoamDriftId, new Vector4(wind.x * (float)drift, wind.y * (float)drift, wind.x, wind.y));
            _compute.SetTexture(_foamKernel, DisplacementId, _displacement);
            _compute.SetTexture(_foamKernel, FoamPreviousId, _foamPrevious);
            _compute.SetTexture(_foamKernel, FoamId, _foam);
            _compute.Dispatch(_foamKernel, groups, groups, CascadeCount);
        }

        // Unit vector in world XZ of the wind of the first cascade.
        private Vector2 WindVector()
        {
            float angle = (90f - _cascades[0].WindDirection) * Mathf.Deg2Rad;
            return new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
        }

        private void DispatchFFT(int kernel)
        {
            _compute.SetTexture(kernel, BufferAId, _bufferA);
            _compute.SetTexture(kernel, BufferBId, _bufferB);
            _compute.Dispatch(kernel, 1, Size, CascadeCount);
        }

        // The compute shader keeps these values for every later dispatch.
        private void SetParameters()
        {
            for (int cascade = 0; cascade < CascadeCount; cascade++)
            {
                OceanCascade settings = _cascades[cascade];
                // Clockwise from +Z in degrees, as an angle from +X counter-clockwise in the XZ plane of the spectrum.
                float direction = (90f - settings.WindDirection) * Mathf.Deg2Rad;
                _cascadeWind[cascade] = new Vector4(settings.WindSpeed, settings.Fetch * 1000f, direction,
                    settings.Spread);
                // Each cascade starts at another time, so the cascades do not move in step.
                _cascadeShape[cascade] = new Vector4(settings.Swell, settings.Detail, Mathf.Max(settings.TileSize, 1f),
                    Mathf.PI * cascade);
            }
            _compute.SetVectorArray(CascadeWindId, _cascadeWind);
            _compute.SetVectorArray(CascadeShapeId, _cascadeShape);
            _compute.SetVector(OceanId, new Vector4(_depth, _heightScale, _choppiness, _seed));
        }

        private bool Load()
        {
            if (_compute) return true;
            if (!SystemInfo.supportsComputeShaders) return false;

            _compute = Resources.Load<ComputeShader>(ComputeName);
            if (!_compute) return false;
            _size512 = new LocalKeyword(_compute, "OCEAN_SIZE_512");
            _size1024 = new LocalKeyword(_compute, "OCEAN_SIZE_1024");
            _initKernel = _compute.FindKernel("Init");
            _evolveKernel = _compute.FindKernel("Evolve");
            _rowsKernel = _compute.FindKernel("FFTRows");
            _columnsKernel = _compute.FindKernel("FFTColumns");
            _assembleKernel = _compute.FindKernel("Assemble");
            _foamKernel = _compute.FindKernel("Foam");
            return true;
        }

        private RenderTexture CreateArray(string name, GraphicsFormat format, bool mips)
        {
            var descriptor = new RenderTextureDescriptor(Size, Size, format, 0)
            {
                dimension = TextureDimension.Tex2DArray,
                volumeDepth = CascadeCount,
                enableRandomWrite = true,
                useMipMap = mips,
                autoGenerateMips = false
            };
            var texture = new RenderTexture(descriptor)
            {
                name = name,
                hideFlags = HideFlags.DontSave,
                wrapMode = TextureWrapMode.Repeat,
                filterMode = mips ? FilterMode.Trilinear : FilterMode.Point,
                anisoLevel = mips ? 4 : 0
            };
            texture.Create();
            return texture;
        }

        private void Release()
        {
            Destroy(ref _spectrum);
            Destroy(ref _bufferA);
            Destroy(ref _bufferB);
            Destroy(ref _displacement);
            Destroy(ref _derivatives);
            Destroy(ref _foam);
            Destroy(ref _foamPrevious);
        }

        private static void Destroy(ref RenderTexture texture)
        {
            if (!texture) return;
            texture.Release();
            if (Application.isPlaying)
            {
                Object.Destroy(texture);
            }
            else
            {
                Object.DestroyImmediate(texture);
            }
            texture = null;
        }
    }
}
