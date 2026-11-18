using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace LoogaSoft.Terrain
{
    internal sealed class VirtualSurfaceCache : IDisposable
    {
        internal sealed class Tile : IDisposable
        {
            public uint Id;
            public TerrainRenderer Renderer;
            public Material Producer;
            public readonly MaterialPropertyBlock Properties = new();
            public GraphicsBuffer Table;
            public uint[] Entries;
            public int SourceVersion = -1;
            public int TableRevision;

            public void Dispose()
            {
                Table?.Dispose();
                if (Producer)
                {
                    ReleaseObject(Producer);
                }
            }
        }

        private sealed class Feedback : IDisposable
        {
            public RenderTexture Color;
            public RenderTexture Depth;
            public VirtualTextureGpuFeedback Compaction;
            public readonly VirtualTextureFeedbackBatch Analysis = new();
            public bool Pending;
            public int LastFrame = -1000;
            public int Revision = -1;
            public Vector3Int PageCell;
            public Quaternion Rotation;
            public float Projection;
            public bool Orthographic;
            public bool HasView;
            // Leaf pages of the last readbacks of this camera. A readback samples one position inside each feedback
            // pixel, and the position moves each time (FeedbackJitter), so the union of the last rounds covers the
            // pages of all screen pixels. Each camera keeps its own requests, so the views of several cameras (for
            // example the Scene and Game views) do not replace each other's pages.
            public readonly Dictionary<ulong, int>[] Leaves;
            public int Round;
            // Feedback rounds left at the moving rate after a view change, so the history fills quickly.
            public int SettleRounds;
            // Round of the last residency bias change. Older rounds requested their pages at the old bias.
            public int BiasRound;

            public bool HasLeaves
            {
                get
                {
                    foreach (Dictionary<ulong, int> round in Leaves)
                    {
                        if (round.Count > 0) return true;
                    }
                    return false;
                }
            }

            // Clears all rounds but the newest ones. After a view change the older rounds show the old view; kept,
            // they doubled the requests of a moving camera.
            public void KeepNewest(int rounds)
            {
                for (int age = rounds + 1; age <= Leaves.Length; age++)
                {
                    Leaves[((Round - age) % Leaves.Length + Leaves.Length) % Leaves.Length].Clear();
                }
            }

            // Clears and returns the leaf set of the next round. It replaces the oldest round.
            public Dictionary<ulong, int> NextLeaves()
            {
                Dictionary<ulong, int> leaves = Leaves[Round % Leaves.Length];
                leaves.Clear();
                Round++;
                return leaves;
            }

            public Feedback(int rounds)
            {
                Leaves = new Dictionary<ulong, int>[rounds];
                for (int index = 0; index < Leaves.Length; index++)
                {
                    Leaves[index] = new Dictionary<ulong, int>();
                }
            }
            // A Scene view camera is secondary while a game camera renders. Its pages take the room that is left.
            public bool Primary = true;
            public void Dispose()
            {
                // The readback callback owns these textures until the GPU request completes.
                Analysis.Dispose();
                if (Pending) return;
                Compaction?.Dispose();
                Compaction = null;
                Color.Release();
                Depth.Release();
                ReleaseObject(Color);
                ReleaseObject(Depth);
            }
        }

        private readonly Dictionary<uint, Tile> _tiles = new();
        private readonly Dictionary<Camera, Feedback> _feedback = new();
        private readonly Dictionary<ulong, int> _requested = new();
        // Feedback pixels of each page itself, without the pixels of its finer pages, of primary and secondary cameras.
        private readonly Dictionary<ulong, int> _ownRequests = new();
        private readonly Dictionary<ulong, int> _secondaryRequests = new();
        private readonly List<ulong> _pending = new();
        private readonly List<Camera> _expired = new();
        private readonly VirtualPagePool _pool;
        private readonly VirtualTextureLayout _layout;
        private readonly RenderTargetIdentifier[] _targets = new RenderTargetIdentifier[3];
        private readonly uint[] _oneEntry = new uint[1];
        private readonly RenderTexture[] _physical = new RenderTexture[3];
        private readonly CommandBuffer _commands = new() { name = "Looga Virtual Texture" };
        // GPU samplers: give the GPU time of the feedback pass and of each page fill.
        private static readonly UnityEngine.Profiling.CustomSampler FeedbackGpuSample =
            UnityEngine.Profiling.CustomSampler.Create("Looga Virtual Texture Feedback", true);
        private static readonly UnityEngine.Profiling.CustomSampler FillGpuSample =
            UnityEngine.Profiling.CustomSampler.Create("Looga Virtual Texture Fill", true);
        private readonly Material _feedbackMaterial;
        private readonly VirtualTextureCompression _compression;
        private readonly Vector4 _info;
        private static readonly int DependencyRadiusId = Shader.PropertyToID("_LoogaVTDependencyRadius");
        // x maximum anisotropy, y cosine of the cached slope limit, z square of the footprint ratio limit, w most fallback
        // mips (_LoogaVTSampling).
        private readonly Vector4 _sampling;
        private uint _nextTile = 1;
        private int _editorTick;
        private int _frame;
        private int _lastUpdate = -1;
        private int _feedbackRevision;
        private bool _disposed;
        private VirtualTextureStatistics _statistics;

        internal VirtualTextureSettings Settings { get; }
        internal int SettingsRevision { get; }
        internal int TileCount => _tiles.Count;
        /// <summary>Gets the physical page capacity of this cache.</summary>
        internal int Capacity { get; }
        /// <summary>Mip bias of all cache reads and requests while the pool is oversubscribed.</summary>
        internal float ResidencyBias { get; private set; }
        // Bias limits and steps. The demand falls to about 1 / 4 per level, so the lower threshold stays below the
        // upper threshold after one step back.
        private const float MaxResidencyBias = 3f;
        private const float ResidencyBiasStep = 0.25f;
        private const float OversubscribedLoad = 0.95f;
        private const float RelaxedLoad = 0.6f;
        // Share of the capacity that secondary cameras can fill. It stays below the oversubscribed load, so a Scene
        // view alone never raises the bias of the game view.
        private const float SecondaryLoad = 0.9f;
        internal VirtualTextureStatistics Statistics
        {
            get
            {
                _statistics.ResidentPages = _pool.Count;
                _statistics.PendingPages = _pending.Count;
                _statistics.ResidencyMipBias = ResidencyBias;
                return _statistics;
            }
        }

        internal VirtualSurfaceCache(VirtualTextureSettings settings, int capacity)
        {
            Settings = settings;
            Capacity = Mathf.Clamp(capacity, 16, VirtualTextureSettings.MaxResidentPages);
            SettingsRevision = settings.Revision;
            if (!SystemInfo.supportsAsyncGPUReadback || SystemInfo.supportedRenderTargetCount < 3 || !settings.FeedbackShader)
            {
                throw new NotSupportedException("Virtual texturing requires asynchronous GPU readback, three render targets, and the feedback shader.");
            }
            if (!SystemInfo.IsFormatSupported(GraphicsFormat.R32G32_SFloat, GraphicsFormatUsage.Render)
                || !SystemInfo.IsFormatSupported(GraphicsFormat.R32G32_SFloat, GraphicsFormatUsage.ReadPixels))
            {
                throw new NotSupportedException("This device does not support the virtual texture feedback format.");
            }
            _layout = new VirtualTextureLayout(settings.VirtualResolution / VirtualTextureSettings.PageSize);
            _pool = new VirtualPagePool(Capacity);
            _info = new Vector4(settings.VirtualResolution, _layout.PagesPerAxis, _layout.LastMip, settings.PreserveCloseDetail ? 1 : 0);
            _sampling = new Vector4(settings.MaxAnisotropy,
                settings.CachedSlopeLimit >= 90f ? 0f : Mathf.Cos(settings.CachedSlopeLimit * Mathf.Deg2Rad),
                settings.CachedFootprintRatio * settings.CachedFootprintRatio,
                settings.MaxFallbackMips < 0 ? 255f : settings.MaxFallbackMips);
            GraphicsFormat materialFormat = settings.HighDynamicRangeMaterialCache
                ? GraphicsFormat.R16G16B16A16_SFloat
                : GraphicsFormat.R8G8B8A8_UNorm;
            try
            {
                if (settings.CompressCache && settings.CompressionShader && VirtualTextureCompression.IsSupported)
                {
                    // A low-precision material array holds 0 to 1 data, so it is compressed too.
                    _compression = new VirtualTextureCompression(settings.CompressionShader, Capacity,
                        materialFormat, !settings.HighDynamicRangeMaterialCache,
                        Mathf.Min(settings.PageUpdatesPerFrame, MaxFillBatch));
                }
                else if (settings.CompressCache)
                {
                    Debug.LogWarning("Looga Terrain: BC3 encoder or device support is unavailable. Using uncompressed cache.");
                }
                if (_compression == null)
                {
                    _physical[0] = CreatePhysical(GraphicsFormat.R8G8B8A8_SRGB, "Looga VT albedo and smoothness");
                    _physical[1] = CreatePhysical(GraphicsFormat.R16G16B16A16_SFloat, "Looga VT normal and occlusion");
                }
                if (_compression == null || !_compression.CompressesMaterial)
                {
                    _physical[2] = CreatePhysical(materialFormat, "Looga VT emission and metallic");
                }
                _feedbackMaterial = new Material(settings.FeedbackShader) { hideFlags = HideFlags.HideAndDontSave };
                _statistics.Capacity = Capacity;
                int materialBytes = settings.HighDynamicRangeMaterialCache ? 8 : 4;
                _statistics.CacheBytes = _compression != null
                    ? _compression.CacheBytes + (_physical[2] ? 136L * 136 * Capacity * materialBytes : 0)
                    : 136L * 136 * Capacity * (12 + materialBytes);
                _statistics.Compressed = _compression != null;
                _statistics.StagingBytes = _compression?.StagingBytes ?? 0;
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        private Texture MaterialTexture => _physical[2] ? _physical[2] : _compression.Texture(2);

        private RenderTexture CreatePhysical(GraphicsFormat format, string name)
        {
            var texture = new RenderTexture(new RenderTextureDescriptor(136, 136)
            {
                graphicsFormat = format, depthBufferBits = 0, dimension = TextureDimension.Tex2DArray,
                volumeDepth = Capacity, msaaSamples = 1, useMipMap = false
            })
            {
                name = name, hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            if (!texture.Create())
            {
                ReleaseObject(texture);
                throw new InvalidOperationException("The GPU could not allocate the virtual texture cache.");
            }
            return texture;
        }

        #region Tile ownership
        internal Tile Add(TerrainRenderer renderer, Material producer)
        {
            if (_nextTile > 16777216)
            {
                throw new InvalidOperationException("Virtual texture tile identifiers are exhausted. Recreate the cache.");
            }
            var tile = new Tile { Id = _nextTile++, Renderer = renderer };
            try
            {
                tile.Producer = new Material(producer) { hideFlags = HideFlags.HideAndDontSave };
                tile.Entries = new uint[_layout.EntryCount];
                tile.Table = new GraphicsBuffer(GraphicsBuffer.Target.Structured, _layout.EntryCount, 4);
                tile.Table.SetData(tile.Entries);
                _tiles.Add(tile.Id, tile);
                _feedbackRevision++;
                return tile;
            }
            catch
            {
                tile.Dispose();
                throw;
            }
        }

        internal void Remove(Tile tile)
        {
            if (tile == null) return;
            _pool.RemoveTile(tile.Id);
            _tiles.Remove(tile.Id);
            _feedbackRevision++;
            _pending.RemoveAll(key => (uint)(key >> 32) == tile.Id);
            tile.Dispose();
        }

        internal void Invalidate(Tile tile)
        {
            _pool.RemoveTile(tile.Id);
            tile.TableRevision++;
            Array.Clear(tile.Entries, 0, tile.Entries.Length);
            tile.Table.SetData(tile.Entries);
            tile.SourceVersion = -1;
            _feedbackRevision++;
        }

        internal void Invalidate(Tile tile, Rect region)
        {
            int invalidated = 0;
            for (int mip = _layout.LastMip; mip >= 0; mip--)
            {
                int width = _layout.PagesPerAxis >> mip;
                float border = VirtualTextureSettings.Border / (float)VirtualTextureSettings.PageSize;
                int xMin = Mathf.Max(0, Mathf.FloorToInt(region.xMin * width - border));
                int yMin = Mathf.Max(0, Mathf.FloorToInt(region.yMin * width - border));
                int xMax = Mathf.Min(width - 1, Mathf.FloorToInt(region.xMax * width + border));
                int yMax = Mathf.Min(width - 1, Mathf.FloorToInt(region.yMax * width + border));
                for (int y = yMin; y <= yMax; y++)
                {
                    for (int x = xMin; x <= xMax; x++)
                    {
                        int page = _layout.Index(x, y, mip);
                        ulong key = VirtualPagePool.Key(tile.Id, page);
                        if (!_pool.Remove(key))
                        {
                            continue;
                        }
                        Publish(tile, page, 0);
                        if (!_pending.Contains(key))
                        {
                            _pending.Add(key);
                        }
                        invalidated++;
                    }
                }
            }
            _pending.Sort((a, b) => _layout.Decode((int)(uint)b).z.CompareTo(_layout.Decode((int)(uint)a).z));
            _statistics.LocalInvalidations++;
            _statistics.InvalidatedPages += invalidated;
            if (invalidated > 0)
            {
                _feedbackRevision++;
            }
        }

        // A page texel reads terrain data this far around it, in meters (_LoogaVTDependencyRadius of the producer), for
        // example the far curvature step. A changed area invalidates the pages within this radius too.
        private static float DependencyRadius(Tile tile)
        {
            return tile.Producer && tile.Producer.HasProperty(DependencyRadiusId)
                ? Mathf.Max(0f, tile.Producer.GetFloat(DependencyRadiusId)) : 0f;
        }

        private static Rect Expand(Rect region, float meters, TerrainRenderer renderer)
        {
            Vector3 size = renderer.Source.terrainData.size;
            float x = meters / Mathf.Max(size.x, 0.001f);
            float z = meters / Mathf.Max(size.z, 0.001f);
            return Rect.MinMaxRect(region.xMin - x, region.yMin - z, region.xMax + x, region.yMax + z);
        }

        /// <summary>Invalidates the pages of all tiles inside a world XZ area. Null invalidates all pages.</summary>
        internal void InvalidateWorld(Rect? worldArea)
        {
            foreach (Tile tile in _tiles.Values)
            {
                if (!tile.Renderer || !tile.Renderer.Source || !tile.Renderer.Source.terrainData) continue;
                if (!worldArea.HasValue)
                {
                    Invalidate(tile);
                    continue;
                }
                Vector3 origin = tile.Renderer.transform.position;
                Vector3 size = tile.Renderer.Source.terrainData.size;
                Rect area = worldArea.Value;
                Rect region = Rect.MinMaxRect((area.xMin - origin.x) / size.x, (area.yMin - origin.z) / size.z,
                    (area.xMax - origin.x) / size.x, (area.yMax - origin.z) / size.z);
                if (region.xMax < 0f || region.yMax < 0f || region.xMin > 1f || region.yMin > 1f) continue;
                Invalidate(tile, Expand(region, DependencyRadius(tile), tile.Renderer));
            }
        }

        internal void Bind(Tile tile, MaterialPropertyBlock properties)
        {
            properties.SetInt("_LoogaVTEnabled", 1);
            properties.SetBuffer("_LoogaVTTable", tile.Table);
            properties.SetInt("_LoogaVTCompressed", _compression != null ? 1 : 0);
            properties.SetInt("_LoogaVTBlendMips", Settings.BlendMipTransitions ? 1 : 0);
            properties.SetTexture("_LoogaVTAlbedo", _compression != null ? _compression.Texture(0) : _physical[0]);
            properties.SetTexture("_LoogaVTNormal", _compression != null ? _compression.Texture(1) : _physical[1]);
            properties.SetTexture("_LoogaVTEmission", MaterialTexture);
            properties.SetVector("_LoogaVTInfo", _info);
            properties.SetVector("_LoogaVTSampling", _sampling);
        }
        internal Vector4 BindGroundTextures()
        {
            Shader.SetGlobalTexture("_LoogaGroundCacheAlbedo", _compression != null ? _compression.Texture(0) : _physical[0]);
            Shader.SetGlobalTexture("_LoogaGroundCacheNormal", _compression != null ? _compression.Texture(1) : _physical[1]);
            Shader.SetGlobalTexture("_LoogaGroundCacheEmission", MaterialTexture);
            return new Vector4(_info.x, _info.y, _info.z, _compression != null ? 1 : 0);
        }
        #endregion

        #region Camera updates
        internal void BeginCamera(Camera camera, List<TerrainRenderer> renderers)
        {
            bool active = false;
            foreach (TerrainRenderer renderer in renderers)
            {
                TerrainVirtualTexture component = renderer.VirtualTexture;
                if (!component || component.Tile == null || !_tiles.ContainsKey(component.Tile.Id)
                    || _tiles[component.Tile.Id] != component.Tile)
                {
                    continue;
                }
                active = true;
                Tile tile = component.Tile;
                if (tile.SourceVersion == renderer.SurfaceVersion)
                {
                    continue;
                }
                if (renderer.TryGetSurfaceRegion(tile.SourceVersion, out Rect dirtyRegion))
                {
                    Invalidate(tile, Expand(dirtyRegion, DependencyRadius(tile), renderer));
                }
                else
                {
                    Invalidate(tile);
                }
                renderer.BindVirtualTextureProducer(tile.Producer, tile.Properties);
                tile.SourceVersion = renderer.SurfaceVersion;
            }
            if (!active) return;
            _frame = Application.isPlaying ? Time.frameCount : ++_editorTick;
            if (_lastUpdate == _frame) return;
            _lastUpdate = _frame;
            _statistics.LastFrameFills = 0;
            foreach (Feedback feedback in _feedback.Values)
            {
                if (!feedback.Analysis.Pending || !feedback.Analysis.IsCompleted) continue;
                var leaves = feedback.Analysis.Complete();
                Dictionary<ulong, int> round = feedback.NextLeaves();
                foreach (var leaf in leaves)
                {
                    if (_tiles.ContainsKey((uint)(leaf.Key >> 32)))
                    {
                        round[leaf.Key] = leaf.Count;
                    }
                }
                ApplyRequests();
            }
            FillPages();
        }

        internal void EndCamera(Camera camera, List<TerrainRenderer> renderers)
        {
            bool active = false;
            foreach (TerrainRenderer renderer in renderers)
            {
                if (renderer.VirtualTexture && renderer.VirtualTexture.Tile != null
                    && _tiles.TryGetValue(renderer.VirtualTexture.Tile.Id, out Tile tile)
                    && tile == renderer.VirtualTexture.Tile)
                {
                    active = true;
                    break;
                }
            }
            if (!active) return;
            _expired.Clear();
            foreach (Camera old in _feedback.Keys)
            {
                if (!old && !_feedback[old].Pending)
                {
                    _expired.Add(old);
                }
            }
            foreach (Camera old in _expired)
            {
                _feedback[old].Dispose();
                _feedback.Remove(old);
            }
            int width = Mathf.Max(1, camera.pixelWidth / Settings.FeedbackDownsample);
            int height = Mathf.Max(1, camera.pixelHeight / Settings.FeedbackDownsample);
            if (_feedback.TryGetValue(camera, out Feedback feedback) && (feedback.Color.width != width || feedback.Color.height != height))
            {
                if (feedback.Pending) return;
                feedback.Dispose();
                _feedback.Remove(camera);
                feedback = null;
            }
            if (feedback == null)
            {
                feedback = new Feedback(FeedbackRounds)
                {
                    Color = new RenderTexture(new RenderTextureDescriptor(width, height)
                    { graphicsFormat = GraphicsFormat.R32G32_SFloat, depthBufferBits = 0, msaaSamples = 1 }),
                    Depth = new RenderTexture(width, height, 24, RenderTextureFormat.Depth)
                };
                feedback.Color.name = "Looga VT page feedback";
                feedback.Color.hideFlags = feedback.Depth.hideFlags = HideFlags.HideAndDontSave;
                feedback.Color.Create();
                feedback.Depth.Create();
                if (Settings.FeedbackCompactionShader)
                {
                    feedback.Compaction = new VirtualTextureGpuFeedback(Settings.FeedbackCompactionShader, width * height);
                }
                _feedback.Add(camera, feedback);
            }
            feedback.Primary = camera.cameraType != CameraType.SceneView;
            if (feedback.Pending || feedback.Analysis.Pending || _frame - feedback.LastFrame < Settings.FeedbackInterval) return;
            float pageWorldSize = float.MaxValue;
            foreach (TerrainRenderer renderer in renderers)
            {
                TerrainVirtualTexture component = renderer.VirtualTexture;
                if (!component || component.Tile == null || !_tiles.ContainsKey(component.Tile.Id)) continue;
                Vector3 size = renderer.Source.terrainData.size;
                pageWorldSize = Mathf.Min(pageWorldSize,
                    Mathf.Min(size.x, size.z) / Mathf.Max(1, _layout.PagesPerAxis));
            }
            pageWorldSize = float.IsFinite(pageWorldSize) ? Mathf.Max(0.01f, pageWorldSize) : 1f;
            Vector3 position = camera.transform.position;
            var pageCell = new Vector3Int(Mathf.FloorToInt(position.x / pageWorldSize),
                Mathf.FloorToInt(position.y / pageWorldSize), Mathf.FloorToInt(position.z / pageWorldSize));
            float projection = camera.orthographic ? camera.orthographicSize : camera.fieldOfView;
            bool viewChanged = !feedback.HasView || feedback.Revision != _feedbackRevision ||
                feedback.PageCell != pageCell || feedback.Orthographic != camera.orthographic ||
                Mathf.Abs(feedback.Projection - projection) > 0.01f ||
                Quaternion.Angle(feedback.Rotation, camera.transform.rotation) >= Settings.FeedbackRotationDegrees;
            if (!viewChanged && feedback.SettleRounds <= 0
                && _frame - feedback.LastFrame < Settings.StationaryFeedbackInterval) return;
            feedback.SettleRounds = viewChanged ? FeedbackRounds : feedback.SettleRounds - 1;
            if (viewChanged)
            {
                feedback.KeepNewest(MovingRounds);
            }
            feedback.LastFrame = _frame;
            feedback.Revision = _feedbackRevision;
            feedback.PageCell = pageCell;
            feedback.Rotation = camera.transform.rotation;
            feedback.Projection = projection;
            feedback.Orthographic = camera.orthographic;
            feedback.HasView = true;
            _commands.Clear();
            _commands.BeginSample(FeedbackGpuSample);
            _commands.SetRenderTarget(feedback.Color.colorBuffer, feedback.Depth.depthBuffer);
            _commands.ClearRenderTarget(true, true, Color.clear, 1f);
            _commands.SetViewport(new Rect(0, 0, width, height));
            // The jitter moves the sample position inside the feedback pixels. A clip-space offset times w is a fixed
            // screen offset.
            Vector2 jitter = FeedbackJitter(feedback.Round, FeedbackGrid);
            Transform view = camera.transform;
            float pixelSize = camera.orthographic ? 0f
                : 2f * Mathf.Tan(0.5f * camera.fieldOfView * Mathf.Deg2Rad) / Mathf.Max(1, camera.pixelHeight);
            Vector3 cameraPosition = view.position;
            _commands.SetGlobalVector("_LoogaVTCameraPosition",
                new Vector4(cameraPosition.x, cameraPosition.y, cameraPosition.z, pixelSize));
            _commands.SetGlobalVector("_LoogaVTCameraRight", view.right);
            _commands.SetGlobalVector("_LoogaVTCameraUp", view.up);
            _commands.SetGlobalVector("_LoogaVTCameraForward", view.forward);
            Matrix4x4 offset = Matrix4x4.Translate(new Vector3(2f * jitter.x / width, 2f * jitter.y / height, 0f));
            _commands.SetGlobalMatrix("_LoogaVTViewProjection",
                offset * GL.GetGPUProjectionMatrix(camera.projectionMatrix, false) * camera.worldToCameraMatrix);
            foreach (TerrainRenderer renderer in renderers)
            {
                TerrainVirtualTexture component = renderer.VirtualTexture;
                if (!component || component.Tile == null || !_tiles.TryGetValue(component.Tile.Id, out Tile tile) || tile != component.Tile)
                {
                    continue;
                }
                renderer.DrawVirtualTextureFeedback(camera, _commands, _feedbackMaterial, tile.Id, _info, _sampling,
                    (float)height / Mathf.Max(1, camera.pixelHeight));
            }
            bool compact = feedback.Compaction != null && (ulong)_nextTile * (uint)_layout.EntryCount < uint.MaxValue;
            if (compact)
            {
                feedback.Compaction.Record(_commands, feedback.Color, _layout.EntryCount);
            }
            _commands.EndSample(FeedbackGpuSample);
            Graphics.ExecuteCommandBuffer(_commands);
            feedback.Pending = true;
            if (compact)
            {
                feedback.Compaction.Read((data, valid) =>
                {
                    if (_disposed)
                    {
                        feedback.Pending = false;
                        feedback.Dispose();
                        return;
                    }
                    if (!valid)
                    {
                        ReadPixels(feedback);
                        return;
                    }
                    feedback.Pending = false;
                    _statistics.FeedbackReadbacks++;
                    _statistics.FeedbackBytes += 8 + data.Length * sizeof(uint);
                    Dictionary<ulong, int> round = feedback.NextLeaves();
                    for (int index = 0; index < data.Length; index += 2)
                    {
                        uint key = data[index] - 1;
                        uint id = key / (uint)_layout.EntryCount + 1;
                        if (!_tiles.ContainsKey(id)) continue;
                        round[VirtualPagePool.Key(id, (int)(key % (uint)_layout.EntryCount))] = (int)data[index + 1];
                    }
                    ApplyRequests();
                });
            }
            else
            {
                ReadPixels(feedback);
            }
        }

        private void ReadPixels(Feedback feedback)
        {
            AsyncGPUReadback.Request(feedback.Color, 0, request =>
            {
                feedback.Pending = false;
                if (_disposed)
                {
                    feedback.Dispose();
                    return;
                }
                if (request.hasError) return;
                _statistics.FeedbackReadbacks++;
                _statistics.FeedbackBytes += feedback.Color.width * feedback.Color.height * 8L;
                if (Settings.ParallelFeedback)
                {
                    feedback.Analysis.Schedule(request.GetData<float>(), _layout.EntryCount);
                }
                else
                {
                    CollectRequests(feedback, request);
                }
            });
        }
        #endregion

        private void CollectRequests(Feedback feedback, AsyncGPUReadbackRequest request)
        {
            Dictionary<ulong, int> round = feedback.NextLeaves();
            var pixels = request.GetData<float>();
            for (int index = 0; index < pixels.Length; index += 2)
            {
                uint page = (uint)pixels[index];
                uint id = (uint)pixels[index + 1];
                if (page == 0 || page > _layout.EntryCount || !_tiles.ContainsKey(id))
                {
                    continue;
                }
                ulong leaf = VirtualPagePool.Key(id, (int)page - 1);
                round.TryGetValue(leaf, out int frequency);
                round[leaf] = frequency + 1;
            }
            ApplyRequests();
        }

        private void ApplyRequests()
        {
            _pool.BeginEpoch();
            _requested.Clear();
            _ownRequests.Clear();
            _secondaryRequests.Clear();
            // Primary cameras request first. The secondary cameras (the Scene view while a game camera renders) take
            // only the room that is left, so the view that the player sees keeps its detail.
            bool hasPrimary = false;
            foreach (Feedback feedback in _feedback.Values)
            {
                hasPrimary |= feedback.Primary && feedback.HasLeaves && IsActive(feedback);
            }
            int secondaryLimit = (int)(Capacity * SecondaryLoad);
            int primaryPages = 0;
            for (int pass = 0; pass < 2; pass++)
            {
                if (pass == 1) primaryPages = _requested.Count;
                foreach (Feedback feedback in _feedback.Values)
                {
                    if (!IsActive(feedback) || (feedback.Primary || !hasPrimary) != (pass == 0)) continue;
                    foreach (Dictionary<ulong, int> round in feedback.Leaves)
                    {
                        foreach (var leaf in round)
                        {
                            if (pass == 1 && _requested.Count >= secondaryLimit) break;
                            AddRequest(leaf.Key, leaf.Value, pass == 0 ? _ownRequests : _secondaryRequests);
                        }
                    }
                }
            }
            _pending.Clear();
            foreach (var entry in _requested)
            {
                _statistics.RequestedPages++;
                if (_pool.Touch(entry.Key))
                {
                    _statistics.ResidentRequests++;
                }
                else
                {
                    _pending.Add(entry.Key);
                }
            }
            // The pages that pixels read fill first, the page with the most pixels first. A missing page shows a
            // coarser page or direct shading, so the near pages that cover the screen must not wait for far ones.
            // The coarser pages that only serve as a fallback fill after them, coarse first.
            _pending.Sort((a, b) =>
            {
                _ownRequests.TryGetValue(a, out int ownA);
                _ownRequests.TryGetValue(b, out int ownB);
                if (ownA != ownB) return ownB.CompareTo(ownA);
                _secondaryRequests.TryGetValue(a, out int secondaryA);
                _secondaryRequests.TryGetValue(b, out int secondaryB);
                if (secondaryA != secondaryB) return secondaryB.CompareTo(secondaryA);
                int mip = _layout.Decode((int)(uint)b).z.CompareTo(_layout.Decode((int)(uint)a).z);
                if (mip != 0) return mip;
                int frequency = _requested[b].CompareTo(_requested[a]);
                return frequency != 0 ? frequency : a.CompareTo(b);
            });
            if (_pending.Count > Capacity * 4)
            {
                _pending.RemoveRange(Capacity * 4, _pending.Count - Capacity * 4);
            }
            UpdateResidencyBias(primaryPages);
        }

        // Sample positions inside a feedback pixel: a grid of up to 4 x 4 cells, one cell per round in Bayer order. The
        // pages stay requested for one pass over the grid, so every screen pixel of a 1/2 or 1/4 feedback requests its
        // page. Page edges inside a feedback pixel otherwise kept their pages unrequested, and those pixels showed a
        // coarser page.
        private int FeedbackGrid => Mathf.Clamp(Settings.FeedbackDownsample, 1, 4);
        private int FeedbackRounds => FeedbackGrid * FeedbackGrid;
        // Rounds kept after a view change: the first rounds of the grid order spread over the feedback pixel.
        private int MovingRounds => Mathf.Min(4, FeedbackRounds);
        private static readonly int[] Bayer2 = { 0, 2, 3, 1 };
        private static readonly int[] Bayer4 = { 0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5 };

        private static Vector2 FeedbackJitter(int round, int grid)
        {
            if (grid < 2) return Vector2.zero;
            int[] order = grid >= 4 ? Bayer4 : Bayer2;
            int size = grid >= 4 ? 4 : 2;
            int cell = Array.IndexOf(order, round % order.Length);
            return new Vector2((cell % size + 0.5f) / size - 0.5f, (cell / size + 0.5f) / size - 0.5f);
        }

        // A camera without feedback for two stationary intervals no longer renders, so its pages are not kept.
        private bool IsActive(Feedback feedback)
        {
            return _frame - feedback.LastFrame <= Settings.StationaryFeedbackInterval * 2;
        }

        // Adds one leaf page and its coarser pages. Each distinct page expands once, not once per feedback pixel.
        private void AddRequest(ulong leaf, int pixels, Dictionary<ulong, int> own)
        {
            uint id = (uint)(leaf >> 32);
            if (!_tiles.ContainsKey(id)) return;
            own.TryGetValue(leaf, out int ownPixels);
            own[leaf] = ownPixels + pixels;
            int current = (int)(uint)leaf;
            while (true)
            {
                ulong key = VirtualPagePool.Key(id, current);
                _requested.TryGetValue(key, out int count);
                _requested[key] = count + pixels;
                int parent = _layout.Parent(current);
                if (parent == current) return;
                current = parent;
            }
        }

        // An oversubscribed pool evicts visible pages every frame. Then all reads and requests move to coarser mips in
        // steps until the requested pages fit, and back when the demand falls.
        // The secondary cameras fill the room that is left, so only the pages of the primary cameras set the load.
        private void UpdateResidencyBias(int primaryPages)
        {
            float bias = 0f;
            if (Settings.ResidencyMipBias)
            {
                // The load includes the history of each camera. Until the rounds from before the last change (and the
                // readback in flight) leave the history, the load still shows the old bias, and the bias would step
                // again on each readback.
                foreach (Feedback feedback in _feedback.Values)
                {
                    if (IsActive(feedback) && feedback.Round - feedback.BiasRound <= feedback.Leaves.Length) return;
                }
                float load = primaryPages / (float)Capacity;
                bias = ResidencyBias;
                if (load > OversubscribedLoad)
                {
                    bias = Mathf.Min(bias + ResidencyBiasStep, MaxResidencyBias);
                }
                else if (load < RelaxedLoad)
                {
                    bias = Mathf.Max(bias - ResidencyBiasStep, 0f);
                }
            }
            if (Mathf.Approximately(bias, ResidencyBias)) return;
            ResidencyBias = bias;
            // The cameras request their pages again at the new bias.
            _feedbackRevision++;
            foreach (Feedback feedback in _feedback.Values)
            {
                feedback.BiasRound = feedback.Round;
            }
        }

        private void FillPages()
        {
            int examined = 0;
            _commands.Clear();
            while (examined < _pending.Count && _statistics.LastFrameFills + _batchCount < Settings.PageUpdatesPerFrame)
            {
                ulong key = _pending[examined++];
                // A tile whose terrain changed after its producer binding waits for a camera that renders it. A height
                // edit can replace the heightmap texture, so a page filled from the old binding reads a destroyed
                // texture: flat ground. The next feedback requests the page again.
                if (!_tiles.TryGetValue((uint)(key >> 32), out Tile tile) || tile.SourceVersion < 0
                    || !tile.Renderer || tile.SourceVersion != tile.Renderer.SurfaceVersion)
                {
                    continue;
                }
                int bakePass = tile.Producer.FindPass("LoogaVTBake");
#if UNITY_EDITOR
                // An asynchronous editor variant cannot populate a valid surface page yet.
                if (!UnityEditor.ShaderUtil.IsPassCompiled(tile.Producer, bakePass))
                {
                    UnityEditor.ShaderUtil.CompilePass(tile.Producer, bakePass, false);
                    examined--;
                    break;
                }
#endif
                int slot = _pool.Acquire(key, out ulong? evicted);
                if (slot < 0)
                {
                    examined--;
                    break;
                }
                if (evicted.HasValue && _tiles.TryGetValue((uint)(evicted.Value >> 32), out Tile old))
                {
                    Publish(old, (int)(uint)evicted.Value, 0);
                    _statistics.Evictions++;
                }
                int pageIndex = (int)(uint)key;
                Vector3Int page = _layout.Decode(pageIndex);
                float scale = (1 << page.z) / _info.x;
                tile.Properties.SetVector("_LoogaVTPageRect",
                    new Vector4((page.x * 128 - 4) * scale, (page.y * 128 - 4) * scale, 136 * scale, 136 * scale));
                int batchIndex = _batchCount;
                for (int target = 0; target < 3; target++)
                {
                    _targets[target] = _compression != null
                        ? new RenderTargetIdentifier(_compression.Staging(target), 0, CubemapFace.Unknown, batchIndex)
                        : new RenderTargetIdentifier(_physical[target], 0, CubemapFace.Unknown, slot);
                }
                if (_batchCount == 0)
                {
                    _commands.BeginSample(FillGpuSample);
#if UNITY_EDITOR
                    // Cached pages must not retain an asynchronous shader placeholder after reimport.
                    UnityEditor.ShaderUtil.SetAsyncCompilation(_commands, false);
#endif
                }
                // MRT bindings use the explicit slice for every color attachment.
                _commands.SetRenderTarget(_targets, BuiltinRenderTextureType.None, 0, CubemapFace.Unknown,
                    _compression != null ? batchIndex : slot);
                _commands.SetViewport(new Rect(0, 0, 136, 136));
                // The command buffer copies the property values, so the next page of this tile can set its rectangle.
                _commands.DrawProcedural(Matrix4x4.identity, tile.Producer, bakePass,
                    MeshTopology.Triangles, 3, 1, tile.Properties);
                _batchSlots[batchIndex] = slot;
                _batchTiles[batchIndex] = tile;
                _batchPages[batchIndex] = pageIndex;
                _batchCount++;
                if (_batchCount == FillBatch)
                {
                    SubmitFills();
                }
            }
            SubmitFills();
            if (examined > 0)
            {
                _pending.RemoveRange(0, examined);
            }
        }

        // Pages filled together. Each page draws into its own staging slice, then one encode per channel covers all of
        // them. Before, each page drew, encoded and copied through one staging set, so the GPU ran about 7 small steps
        // per page one after another: 8 fills cost about 1 ms.
        private const int MaxFillBatch = 16;
        private int FillBatch => _compression != null ? _compression.Batch : MaxFillBatch;
        private readonly int[] _batchSlots = new int[MaxFillBatch];
        private readonly Tile[] _batchTiles = new Tile[MaxFillBatch];
        private readonly int[] _batchPages = new int[MaxFillBatch];
        private int _batchCount;

        private void SubmitFills()
        {
            if (_batchCount == 0) return;
#if UNITY_EDITOR
            UnityEditor.ShaderUtil.RestoreAsyncCompilation(_commands);
#endif
            if (_compression != null)
            {
                _compression.Encode(_commands, _batchSlots, _batchCount);
                if (!_compression.CompressesMaterial)
                {
                    for (int index = 0; index < _batchCount; index++)
                    {
                        _commands.CopyTexture(_compression.Staging(2), index, 0, _physical[2], _batchSlots[index], 0);
                    }
                }
            }
            _commands.EndSample(FillGpuSample);
            // Publish only after filling and compression are queued before terrain rendering.
            Graphics.ExecuteCommandBuffer(_commands);
            _commands.Clear();
            for (int index = 0; index < _batchCount; index++)
            {
                Publish(_batchTiles[index], _batchPages[index], (uint)_batchSlots[index] + 1);
                _batchTiles[index] = null;
                _statistics.PageFills++;
                _statistics.LastFrameFills++;
            }
            _batchCount = 0;
        }

        private void Publish(Tile tile, int page, uint value)
        {
            tile.TableRevision++;
            tile.Entries[page] = value;
            _oneEntry[0] = value;
            tile.Table.SetData(_oneEntry, 0, page, 1);
        }

        private static void ReleaseObject(UnityEngine.Object value)
        {
            if (Application.isPlaying)
            {
                UnityEngine.Object.Destroy(value);
            }
            else
            {
                UnityEngine.Object.DestroyImmediate(value);
            }
        }

        public void Dispose()
        {
            _disposed = true;
            foreach (Feedback feedback in _feedback.Values)
            {
                feedback.Dispose();
            }
            _feedback.Clear();
            foreach (Tile tile in _tiles.Values)
            {
                tile.Dispose();
            }
            _tiles.Clear();
            foreach (RenderTexture texture in _physical)
            {
                if (!texture)
                {
                    continue;
                }
                texture.Release();
                ReleaseObject(texture);
            }
            if (_feedbackMaterial)
            {
                ReleaseObject(_feedbackMaterial);
            }
            _compression?.Dispose();
            _commands.Dispose();
        }
    }
}
