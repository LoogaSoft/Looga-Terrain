using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Profiling;
using Unity.Profiling;

namespace LoogaSoft.Terrain.Benchmarks
{
    /// <summary>
    /// Compares native and indirect terrain rendering in a separate player process.
    /// Start with -looga-terrain-benchmark and -looga-output followed by an output directory.
    /// </summary>
    public sealed class TerrainBenchmark : MonoBehaviour
    {
        [SerializeField] private RenderPipelineAsset _pipeline;
        [SerializeField] private ComputeShader _selection;
        [SerializeField] private Material _nativeSurface;
        [SerializeField] private Material _gpuSurface;
        [SerializeField] private TerrainLayer[] _layers;
        [SerializeField] private Camera _camera;
        [SerializeField] private int _warmupFrames = 120;
        [SerializeField] private int _sampleFrames = 360;
        [SerializeField] private int _patchCount = 64;

        [SerializeField] private TerrainGBufferCapture _gbufferCapture;
        [SerializeField] private Material _fixtureSurface;
        [SerializeField] private VirtualTextureSettings _virtualSettings;
        [SerializeField] private Material _pageProducer;
        private bool _compareVirtual;
        private readonly List<TerrainVirtualTexture> _virtualTextures = new();
        private int ViewCount => _compareVirtual || _validation ? 4 : 3;
        private int _gridSize = 2;
        private int _heightResolution = 1025;
        private int _controlResolution = 256;
        private int _blendedLayers = 1;
        private float _secondsPerPass = 1f;
        private bool _validation;
        private bool _noCulling;
        private bool _noTerrainShadows;
        private int _errorPixels = 2;
        private double _setupMilliseconds;


        private static readonly int[] ControlIds = CreateControlIds();
        private static readonly int HolesId = Shader.PropertyToID("_TerrainHolesTexture");
        private static readonly int NormalId = Shader.PropertyToID("_TerrainNormalmapTexture");
        private static readonly int PerPixelNormalId = Shader.PropertyToID("_PerPixelNormal");
        private bool _profile;

        private readonly List<TerrainRenderer> _renderers = new();
        private readonly List<Material> _nativeMaterials = new();
        private readonly Dictionary<TerrainData, Texture2D> _normals = new();
        private readonly List<Result> _results = new();
        private readonly List<string> _errors = new();
        private readonly FrameTiming[] _timing = new FrameTiming[1];
        private string _output;
        private ulong _lastTimestamp;

        #region Built-in
        private IEnumerator Start()
        {
            string[] arguments = Environment.GetCommandLineArgs();
            if (Array.IndexOf(arguments, "-looga-terrain-benchmark") < 0) yield break;
            int outputIndex = Array.IndexOf(arguments, "-looga-output");
            _output = outputIndex >= 0 && outputIndex + 1 < arguments.Length ? arguments[outputIndex + 1] : Application.persistentDataPath;
            Directory.CreateDirectory(_output);
            Application.logMessageReceived += OnLog;
            Application.runInBackground = true;
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1;
            if (_pipeline)
            {
                GraphicsSettings.defaultRenderPipeline = _pipeline;
                QualitySettings.renderPipeline = _pipeline;
            }
            ReadOptions(arguments);

            SaveReport();
            double setupStart = Time.realtimeSinceStartupAsDouble;
            yield return CreateTiles();
            if (_validation)
            {
                if (_pipeline is UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset validationPipeline)
                {
                    validationPipeline.shadowDistance = 600f;
                }
                CreateFixtures();
            }
            _setupMilliseconds = (Time.realtimeSinceStartupAsDouble - setupStart) * 1000.0;
            GC.Collect();
            yield return null;
            bool[] sequence = { false, true, true, false };
            for (int view = 0; view < ViewCount; view++)
            {
                SetView(view);
                for (int pass = 0; pass < sequence.Length; pass++)
                {
                    bool virtualMode = sequence[pass];
                    bool gpu = _compareVirtual || virtualMode;
                    SetView(view);
                    SetRenderer(virtualMode);
                    var result = new Result { Mode = _compareVirtual ? (virtualMode ? "Virtual" : "Direct") : (gpu ? "Looga" : "Native"), View = view, Pass = pass, PatchCount = _patchCount };
                    var wall = new List<double>(_sampleFrames);
                    var cpu = new List<double>(_sampleFrames);
                    var gpuTimes = new List<double>(_sampleFrames);
                    var mainThread = new List<double>();
                    var renderThread = new List<double>();
                    var presentWait = new List<double>();
                    var submission = new List<double>();
                    ProfilerRecorder submissionRecorder = default;
                    ProfilerRecorder selectionRecorder = default;
                    ProfilerRecorder bindingRecorder = default;
                    ProfilerRecorder drawRecorder = default;
                    var selectionTimes = new List<double>();
                    var bindingTimes = new List<double>();
                    var drawTimes = new List<double>();
                    double measureStart = double.PositiveInfinity;
                    bool moving = _compareVirtual && !_validation && view == 3;
                    int sampleFrames = moving ? 2400 : _sampleFrames;
                    for (int frame = 0; frame < _warmupFrames + sampleFrames
                        || (!moving && Time.realtimeSinceStartupAsDouble - measureStart < _secondsPerPass); frame++)
                    {
                        if (_profile && frame == _warmupFrames && gpu)
                        {
                            submissionRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "Looga Terrain.SelectAndSubmit", 1,
                                ProfilerRecorderOptions.Default | ProfilerRecorderOptions.SumAllSamplesInFrame);
                            selectionRecorder = StartRecorder("Looga Terrain.Selection");
                            bindingRecorder = StartRecorder("Looga Terrain.Binding");
                            drawRecorder = StartRecorder("Looga Terrain.DrawSubmission");
                        }
                        if (moving)
                        {
                            // Repeat the same camera path for direct and cached shading.
                            float distance = Mathf.Max(0, frame - _warmupFrames) * 0.15f;
                            Vector3 position = new Vector3(700f + distance, 180f, 400f + distance * 0.4f);
                            _camera.transform.position = position;
                            _camera.transform.LookAt(position + new Vector3(50f, -45f, 350f));
                        }
                        FrameTimingManager.CaptureFrameTimings();
                        yield return null;
                        if (_compareVirtual && virtualMode && _virtualTextures.Count > 0)
                        {
                            result.MaximumPageFillsPerFrame = Mathf.Max(result.MaximumPageFillsPerFrame, _virtualTextures[0].Statistics.LastFrameFills);
                        }
                        if (frame < _warmupFrames)
                        {
                            continue;
                        }
                        if (double.IsPositiveInfinity(measureStart))
                        {
                            measureStart = Time.realtimeSinceStartupAsDouble;
                        }
                        wall.Add(Time.unscaledDeltaTime * 1000.0);
                        if (submissionRecorder.Valid && submissionRecorder.Count > 0)
                        {
                            submission.Add(submissionRecorder.LastValue / 1000000.0);
                        }
                        Record(selectionRecorder, selectionTimes);
                        Record(bindingRecorder, bindingTimes);
                        Record(drawRecorder, drawTimes);
                        if (FrameTimingManager.GetLatestTimings(1, _timing) == 0 || _timing[0].frameStartTimestamp == _lastTimestamp)
                        {
                            continue;
                        }
                        _lastTimestamp = _timing[0].frameStartTimestamp;
                        if (_timing[0].cpuMainThreadFrameTime > 0)
                        {
                            mainThread.Add(_timing[0].cpuMainThreadFrameTime);
                        }
                        if (_timing[0].cpuRenderThreadFrameTime > 0)
                        {
                            renderThread.Add(_timing[0].cpuRenderThreadFrameTime);
                        }
                        presentWait.Add(_timing[0].cpuMainThreadPresentWaitTime);
                        if (_timing[0].cpuFrameTime > 0)
                        {
                            cpu.Add(_timing[0].cpuFrameTime);
                        }
                        if (_timing[0].gpuFrameTime > 0)
                        {
                            gpuTimes.Add(_timing[0].gpuFrameTime);
                        }
                    }
                    if (_compareVirtual && virtualMode && _virtualTextures.Count > 0)
                    {
                        result.VirtualTexture = _virtualTextures[0].Statistics;
                    }
                    result.Submission = Summarize(submission);
                    submissionRecorder.Dispose();
                    result.Selection = Summarize(selectionTimes);
                    result.Binding = Summarize(bindingTimes);
                    result.DrawSubmission = Summarize(drawTimes);
                    selectionRecorder.Dispose();
                    bindingRecorder.Dispose();
                    drawRecorder.Dispose();
                    result.Wall = Summarize(wall);
                    result.Cpu = Summarize(cpu);
                    result.Gpu = Summarize(gpuTimes);
                    result.MainThread = Summarize(mainThread);
                    result.RenderThread = Summarize(renderThread);
                    result.PresentWait = Summarize(presentWait);
                    result.AllocatedBytes = Profiler.GetTotalAllocatedMemoryLong();
                    result.ReservedBytes = Profiler.GetTotalReservedMemoryLong();
                    result.GraphicsDriverBytes = Profiler.GetAllocatedMemoryForGraphicsDriver();
                    result.MeasureSeconds = Time.realtimeSinceStartupAsDouble - measureStart;
                    if (gpu)
                    {
                        foreach (TerrainRenderer renderer in _renderers)
                        {
                            result.DrawSubmissions += renderer.GetLastSubmissionCount(_camera);
                        }
                        int pending = _renderers.Count;
                        foreach (TerrainRenderer renderer in _renderers)
                        {
                            renderer.RequestPatchCounts(_camera, (visible, shadows) =>
                            {
                                result.VisiblePatches += visible;
                                result.ShadowPatches += shadows;
                                pending--;
                            });
                        }
                        for (int frame = 0; pending > 0 && frame < 120; frame++)
                        {
                            yield return null;
                        }
                        result.PatchReadbackComplete = pending == 0;
                    }
                    _results.Add(result);
                    if (pass < 2)
                    {
                        if (_validation && _gbufferCapture)
                        {
                            _gbufferCapture.Request(_camera);
                            for (int frame = 0; frame < 10 && !_gbufferCapture.Ready; frame++)
                            {
                                yield return null;
                            }
                            result.GBufferCaptured = _gbufferCapture.Save(_output, result.Mode + "-view" + view);
                        }
                        yield return new WaitForEndOfFrame();
                        ScreenCapture.CaptureScreenshot(Path.Combine(_output, result.Mode + "-view" + view + ".png"));
                        for (int frame = 0; frame < 10; frame++)
                        {
                            yield return null;
                        }
                    }
                    SaveReport();
                    Debug.Log($"Looga benchmark: view {view}, {result.Mode}, median GPU {result.Gpu.Median:F3} ms, wall {result.Wall.Median:F3} ms.");
                }
            }
            SaveReport(true);
            foreach (TerrainRenderer renderer in _renderers)
            {
                renderer.enabled = false;
                Destroy(renderer.Source.terrainData);
                Destroy(renderer.gameObject);
            }
            foreach (Texture2D normals in _normals.Values)
            {
                Destroy(normals);
            }
            foreach (Material material in _nativeMaterials)
            {
                Destroy(material);
            }
            yield return null;
            Application.logMessageReceived -= OnLog;
            Application.Quit(_errors.Count == 0 ? 0 : 1);
        }

        private void OnLog(string message, string stack, LogType type)
        {
            if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
            {
                if (!_errors.Contains(message))
                {
                    _errors.Add(message);
                }
            }
        }
        #endregion

        #region Setup
        private IEnumerator CreateTiles()
        {
            for (int tile = 0; tile < _gridSize * _gridSize; tile++)
            {
                int tileX = tile % _gridSize;
                int tileZ = tile / _gridSize;
                var data = new TerrainData { heightmapResolution = _heightResolution, size = new Vector3(1000f, 300f, 1000f), terrainLayers = _layers, alphamapResolution = _controlResolution };
                var heights = new float[_heightResolution, _heightResolution];
                for (int z = 0; z < _heightResolution; z++)
                {
                    for (int x = 0; x < _heightResolution; x++)
                    {
                        float wx = tileX * 1000f + x * 1000f / (_heightResolution - 1f);
                        float wz = tileZ * 1000f + z * 1000f / (_heightResolution - 1f);
                        heights[z, x] = 0.27f + 0.13f * Mathf.Sin(wx * 0.008f) * Mathf.Cos(wz * 0.007f)
                            + 0.04f * Mathf.Sin(wx * 0.033f + wz * 0.021f) + 0.015f * Mathf.Sin(wz * 0.11f);
                    }
                }
                data.SetHeights(0, 0, heights);
                _normals.Add(data, CreateNormals(heights, data.size));
                var weights = new float[_controlResolution, _controlResolution, _layers.Length];
                for (int z = 0; z < _controlResolution; z++)
                {
                    for (int x = 0; x < _controlResolution; x++)
                    {
                        float total = 0f;
                        for (int layer = 0; layer < _blendedLayers; layer++)
                        {
                            float value = 1.1f + Mathf.Sin(x * (256f / _controlResolution) * 0.017f + z * (256f / _controlResolution) * 0.023f + layer * 1.7f);
                            weights[z, x, layer] = value;
                            total += value;
                        }
                        for (int layer = 0; layer < _blendedLayers; layer++)
                        {
                            weights[z, x, layer] /= total;
                        }
                    }
                }
                data.SetAlphamaps(0, 0, weights);
                GameObject tileObject = UnityEngine.Terrain.CreateTerrainGameObject(data);
                tileObject.name = "Benchmark Tile " + tile;
                tileObject.transform.position = new Vector3(tileX * 1000f, 0f, tileZ * 1000f);
                UnityEngine.Terrain terrain = tileObject.GetComponent<UnityEngine.Terrain>();
                terrain.drawInstanced = true;
                terrain.heightmapPixelError = 5f;
                terrain.basemapDistance = 100000f;
                terrain.allowAutoConnect = false;
                var native = new Material(_nativeSurface);
                Texture2D[] controls = data.alphamapTextures;
                var properties = new MaterialPropertyBlock();
                BindTextures(properties, terrain, controls);
                terrain.SetSplatMaterialPropertyBlock(properties);
                terrain.materialTemplate = native;
                _nativeMaterials.Add(native);
                TerrainRenderer renderer = tileObject.AddComponent<TerrainRenderer>();
                renderer.Configure(_selection, _gpuSurface, _patchCount);
                renderer.SetLodQuality(_errorPixels, !_noCulling);
                renderer.SetShadowCasting(!_noTerrainShadows);
                if (_noTerrainShadows)
                {
                    renderer.Source.shadowCastingMode = ShadowCastingMode.Off;
                }
                Texture normals = _normals[data];
                renderer.BindMaterialProperties += block => BindSurface(block, controls, data.holesTexture, normals);
                renderer.enabled = false;
                _renderers.Add(renderer);
                if (_compareVirtual)
                {
                    var virtualTexture = tileObject.AddComponent<TerrainVirtualTexture>();
                    virtualTexture.enabled = false;
                    virtualTexture.Configure(_virtualSettings, _pageProducer);
                    _virtualTextures.Add(virtualTexture);
                }
                yield return null;
            }
            for (int z = 0; z < _gridSize; z++)
            {
                for (int x = 0; x < _gridSize; x++)
                {
                    GetTerrain(x, z).SetNeighbors(GetTerrain(x - 1, z), GetTerrain(x, z + 1), GetTerrain(x + 1, z), GetTerrain(x, z - 1));
                }
            }
        }

        private UnityEngine.Terrain GetTerrain(int x, int z)
        {
            if (x < 0 || z < 0 || x >= _gridSize || z >= _gridSize) return null;
            return _renderers[z * _gridSize + x].Source;
        }
        private void BindTextures(MaterialPropertyBlock properties, UnityEngine.Terrain terrain, Texture2D[] controls)
        {
            TerrainData data = terrain.terrainData;
            int resolution = data.heightmapResolution;
            Vector3 size = data.size;
            properties.SetTexture("_TerrainHeightmapTexture", data.heightmapTexture);
            properties.SetVector("_TerrainHeightmapRecipSize", new Vector4(1f / resolution, 1f / resolution, 1f / (resolution - 1), 1f / (resolution - 1)));
            properties.SetVector("_TerrainHeightmapScale", new Vector4(size.x / (resolution - 1), size.y * 65535f / 32766f, size.z / (resolution - 1), 0f));
            properties.SetTexture("_TerrainHolesTexture", data.holesTexture);
            BindSurface(properties, controls, data.holesTexture, _normals[data]);
        }

        private static void BindSurface(MaterialPropertyBlock properties, Texture2D[] controls, Texture holes, Texture normals)
        {
            for (int index = 0; index < ControlIds.Length; index++)
            {
                properties.SetTexture(ControlIds[index], index < controls.Length ? controls[index] : Texture2D.blackTexture);
            }
            properties.SetTexture(HolesId, holes);
            properties.SetTexture(NormalId, normals);
            properties.SetTexture(PerPixelNormalId, normals);
        }

        private static int[] CreateControlIds()
        {
            var result = new int[8];
            for (int index = 0; index < result.Length; index++)
            {
                result[index] = Shader.PropertyToID("_Control" + index);
            }
            return result;
        }
        private static Texture2D CreateNormals(float[,] heights, Vector3 size)
        {
            int resolution = heights.GetLength(0);
            var pixels = new Color[resolution * resolution];
            for (int z = 0; z < resolution; z++)
            {
                for (int x = 0; x < resolution; x++)
                {
                    int left = Mathf.Max(0, x - 1);
                    int right = Mathf.Min(resolution - 1, x + 1);
                    int bottom = Mathf.Max(0, z - 1);
                    int top = Mathf.Min(resolution - 1, z + 1);
                    float dx = (heights[z, right] - heights[z, left]) * size.y / ((right - left) * size.x / (resolution - 1));
                    float dz = (heights[top, x] - heights[bottom, x]) * size.y / ((top - bottom) * size.z / (resolution - 1));
                    Vector3 normal = new Vector3(-dx, 1f, -dz).normalized;
                    pixels[z * resolution + x] = new Color(normal.x * 0.5f + 0.5f, normal.y * 0.5f + 0.5f, normal.z * 0.5f + 0.5f, 1f);
                }
            }
            // Supply matching normals without an editor-only terrain texture dependency.
            var texture = new Texture2D(resolution, resolution, TextureFormat.RGBA32, false, true) { wrapMode = TextureWrapMode.Clamp };
            texture.SetPixels(pixels);
            texture.Apply(false, true);
            return texture;
        }
        private void SetRenderer(bool gpu)
        {
            foreach (TerrainRenderer renderer in _renderers)
            {
                renderer.enabled = _compareVirtual || gpu;
                if (_compareVirtual)
                {
                    renderer.GetComponent<TerrainVirtualTexture>().enabled = gpu;
                }
                if ((_compareVirtual || gpu) && !renderer.IsReady)
                {
                    throw new InvalidOperationException(renderer.Diagnostic);
                }
                var properties = new MaterialPropertyBlock();
                BindTextures(properties, renderer.Source, renderer.Source.terrainData.alphamapTextures);
                renderer.Source.SetSplatMaterialPropertyBlock(properties);
            }
        }

        private void SetView(int view)
        {
            float extent = _gridSize * 1000f;
            Vector3[] positions = { new(700f, 0f, 450f), new(1000f, 360f, -160f), new(extent * 0.5f, extent * 0.825f, -extent * 0.55f), new(700f, 260f, 400f) };
            Vector3[] targets = { new(1100f, 105f, 1400f), new(1000f, 70f, 1050f), new(extent * 0.5f, 60f, extent * 0.5f), new(750f, 85f, 750f) };
            Vector3 position = positions[view];
            if (view == 0)
            {
                position.y = _renderers[0].Source.SampleHeight(position) + 1.8f;
            }
            _camera.farClipPlane = extent * 3f;
            _camera.transform.position = position;
            _camera.transform.LookAt(targets[view]);
        }
        #endregion

        private void ReadOptions(string[] arguments)
        {
            _compareVirtual = Array.IndexOf(arguments, "-looga-virtual-texture") >= 0;
            if (_compareVirtual)
            {
                if (!_virtualSettings || !_pageProducer)
                {
                    throw new ArgumentException("Virtual texture benchmark assets are missing.");
                }
                _virtualSettings = Instantiate(_virtualSettings);
                _virtualSettings.Configure(_virtualSettings.FeedbackShader, _virtualSettings.VirtualResolution,
                    ReadInteger(arguments, "-looga-vt-pages", _virtualSettings.ResidentPages),
                    _virtualSettings.PageUpdatesPerFrame, _virtualSettings.FeedbackInterval);
            }
            _gridSize = ReadInteger(arguments, "-looga-grid", 2);
            _heightResolution = ReadInteger(arguments, "-looga-height", 1025);
            _controlResolution = ReadInteger(arguments, "-looga-control", 256);
            _blendedLayers = ReadInteger(arguments, "-looga-layers", 1);
            _secondsPerPass = ReadInteger(arguments, "-looga-seconds", 1);
            _validation = Array.IndexOf(arguments, "-looga-validation") >= 0;
            _profile = Array.IndexOf(arguments, "-looga-profile") >= 0;
            _noCulling = Array.IndexOf(arguments, "-looga-no-culling") >= 0;
            _noTerrainShadows = Array.IndexOf(arguments, "-looga-no-terrain-shadows") >= 0;
            _errorPixels = ReadInteger(arguments, "-looga-error", 2);
            if (_gridSize < 2 || _gridSize > 16 || _heightResolution < 129 || _heightResolution > 4097
                || !Mathf.IsPowerOfTwo(_heightResolution - 1) || _controlResolution < 128 || _controlResolution > 2048
                || !Mathf.IsPowerOfTwo(_controlResolution) || _blendedLayers < 1 || _blendedLayers > _layers.Length
                || _errorPixels < 1 || _errorPixels > 16 || _secondsPerPass < 1 || _secondsPerPass > 10 || !_nativeSurface || !_gpuSurface || !_selection || !_camera)
            {
                throw new ArgumentException("Invalid terrain benchmark configuration.");
            }
        }

        private static int ReadInteger(string[] arguments, string key, int fallback)
        {
            int index = Array.IndexOf(arguments, key);
            if (index < 0) return fallback;
            if (index + 1 >= arguments.Length || !int.TryParse(arguments[index + 1], out int value))
            {
                throw new ArgumentException("Missing integer after " + key);
            }
            return value;
        }

        private void CreateFixtures()
        {
            TerrainData data = _renderers[0].Source.terrainData;
            int holeSize = Mathf.RoundToInt(data.holesResolution * 0.04f);
            data.SetHoles(Mathf.RoundToInt(data.holesResolution * 0.68f), Mathf.RoundToInt(data.holesResolution * 0.68f), new bool[holeSize, holeSize]);
            for (int index = 0; index < 8; index++)
            {
                Vector3 position = new Vector3(630f + index % 4 * 80f, 0f, 660f + index / 4 * 150f);
                position.y = _renderers[0].Source.SampleHeight(position);
                GameObject pillar = GameObject.CreatePrimitive(PrimitiveType.Cube);
                pillar.name = "Shadow fixture " + index;
                pillar.transform.position = position + Vector3.up * 8f;
                pillar.transform.localScale = new Vector3(8f, 16f, 8f);
                pillar.GetComponent<Renderer>().sharedMaterial = _fixtureSurface;
                var lightObject = new GameObject("Local light " + index);
                var light = lightObject.AddComponent<Light>();
                light.type = LightType.Point;
                light.range = 100f;
                light.intensity = 800f;
                light.color = Color.HSVToRGB(index / 8f, 0.7f, 1f);
                light.transform.position = position + new Vector3(15f, 22f, -15f);
            }
        }
        private void SaveReport(bool complete = false)
        {
            var report = new Report
            {
                Complete = complete,
                TimingValid = complete && _errors.Count == 0 && _results.Count == ViewCount * 4
                    && _results.TrueForAll(r => r.Gpu.Count >= 100 && (r.Mode == "Native" || (r.VisiblePatches > 0 && r.PatchReadbackComplete))
                        && (!_validation || r.Pass >= 2 || r.GBufferCaptured)),
                UnityVersion = Application.unityVersion, Device = SystemInfo.graphicsDeviceName,
                GridSize = _gridSize, HeightResolution = _heightResolution, ControlResolution = _controlResolution,
                BlendedLayers = _blendedLayers, Validation = _validation, VirtualTextureComparison = _compareVirtual, SetupMilliseconds = _setupMilliseconds,
                DevelopmentBuild = Debug.isDebugBuild, Profile = _profile, ErrorPixels = _errorPixels, FrustumCulling = !_noCulling, TerrainShadows = !_noTerrainShadows,
                ProfileValid = complete && _profile && _results.TrueForAll(r => r.Mode == "Native" || r.Submission.Count >= 100),
                NativeShader = _nativeSurface.shader.name, LoogaShader = _gpuSurface.shader.name,
                Pipeline = _pipeline ? _pipeline.name : "Project default",
                Api = SystemInfo.graphicsDeviceType.ToString(), Width = Screen.width, Height = Screen.height,
                Vsync = QualitySettings.vSyncCount, FrameTimingEnabled = FrameTimingManager.IsFeatureEnabled(),
                Results = _results.ToArray(), Errors = _errors.ToArray()
            };
            File.WriteAllText(Path.Combine(_output, "results.json"), JsonUtility.ToJson(report, true));
        }

        private static ProfilerRecorder StartRecorder(string name)
        {
            return ProfilerRecorder.StartNew(ProfilerCategory.Scripts, name, 1,
                ProfilerRecorderOptions.Default | ProfilerRecorderOptions.SumAllSamplesInFrame);
        }

        private static void Record(ProfilerRecorder recorder, List<double> values)
        {
            if (recorder.Valid && recorder.Count > 0)
            {
                values.Add(recorder.LastValue / 1000000.0);
            }
        }

        private static Statistics Summarize(List<double> values)
        {
            if (values.Count == 0) return new Statistics();
            values.Sort();
            double sum = 0;
            foreach (double value in values)
            {
                sum += value;
            }
            return new Statistics { Count = values.Count, Mean = sum / values.Count, Median = values[values.Count / 2], P95 = values[Math.Min(values.Count - 1, (int)(values.Count * 0.95))], P99 = values[Math.Min(values.Count - 1, (int)(values.Count * 0.99))] };
        }

        [Serializable]
        private sealed class Report
        {
            public bool Complete, TimingValid;
            public string UnityVersion, Device, Api, NativeShader, LoogaShader, Pipeline;
            public int GridSize, HeightResolution, ControlResolution, BlendedLayers;
            public bool VirtualTextureComparison, Validation, DevelopmentBuild, Profile, ProfileValid, FrustumCulling, TerrainShadows;
            public int ErrorPixels;
            public double SetupMilliseconds;
            public int Width, Height, Vsync;
            public bool FrameTimingEnabled;
            public Result[] Results;
            public string[] Errors;
        }

        [Serializable]
        private sealed class Result
        {
            public string Mode;
            public VirtualTextureStatistics VirtualTexture;
            public int MaximumPageFillsPerFrame;
            public int View, Pass, PatchCount, DrawSubmissions;
            public Statistics Wall, Cpu, Gpu, MainThread, RenderThread, PresentWait, Submission, Selection, Binding, DrawSubmission;
            public long AllocatedBytes, ReservedBytes, GraphicsDriverBytes;
            public double MeasureSeconds;
            public bool GBufferCaptured;
            public uint VisiblePatches, ShadowPatches;
            public bool PatchReadbackComplete;
        }

        [Serializable]
        private struct Statistics
        {
            public int Count;
            public double Mean, Median, P95, P99;
        }
    }
}
