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
        private readonly Dictionary<ulong, int> _leafRequests = new();
        private readonly List<ulong> _pending = new();
        private readonly List<Camera> _expired = new();
        private readonly VirtualPagePool _pool;
        private readonly VirtualTextureLayout _layout;
        private readonly RenderTargetIdentifier[] _targets = new RenderTargetIdentifier[3];
        private readonly uint[] _oneEntry = new uint[1];
        private readonly RenderTexture[] _physical = new RenderTexture[3];
        private readonly CommandBuffer _commands = new() { name = "Looga Virtual Texture" };
        private readonly Material _feedbackMaterial;
        private readonly VirtualTextureCompression _compression;
        private readonly Vector4 _info;
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
        internal VirtualTextureStatistics Statistics
        {
            get
            {
                _statistics.ResidentPages = _pool.Count;
                _statistics.PendingPages = _pending.Count;
                return _statistics;
            }
        }

        internal VirtualSurfaceCache(VirtualTextureSettings settings)
        {
            Settings = settings;
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
            _pool = new VirtualPagePool(settings.ResidentPages);
            _info = new Vector4(settings.VirtualResolution, _layout.PagesPerAxis, _layout.LastMip, settings.PreserveCloseDetail ? 1 : 0);
            try
            {
                if (settings.CompressCache && settings.CompressionShader && VirtualTextureCompression.IsSupported)
                {
                    _compression = new VirtualTextureCompression(settings.CompressionShader, settings.ResidentPages);
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
                _physical[2] = CreatePhysical(settings.HighDynamicRangeMaterialCache
                    ? GraphicsFormat.R16G16B16A16_SFloat
                    : GraphicsFormat.R8G8B8A8_UNorm, "Looga VT emission and metallic");
                _feedbackMaterial = new Material(settings.FeedbackShader) { hideFlags = HideFlags.HideAndDontSave };
                _feedbackMaterial.SetInt("_LoogaVTZTest", (int)(SystemInfo.usesReversedZBuffer ? CompareFunction.GreaterEqual : CompareFunction.LessEqual));
                _statistics.Capacity = settings.ResidentPages;
                int materialBytes = settings.HighDynamicRangeMaterialCache ? 8 : 4;
                _statistics.CacheBytes = 136L * 136 * settings.ResidentPages *
                    (_compression != null ? 2 + materialBytes : 12 + materialBytes);
                _statistics.Compressed = _compression != null;
                _statistics.StagingBytes = _compression?.StagingBytes ?? 0;
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        private RenderTexture CreatePhysical(GraphicsFormat format, string name)
        {
            var texture = new RenderTexture(new RenderTextureDescriptor(136, 136)
            {
                graphicsFormat = format, depthBufferBits = 0, dimension = TextureDimension.Tex2DArray,
                volumeDepth = Settings.ResidentPages, msaaSamples = 1, useMipMap = false
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

        internal void Bind(Tile tile, MaterialPropertyBlock properties)
        {
            properties.SetInt("_LoogaVTEnabled", 1);
            properties.SetBuffer("_LoogaVTTable", tile.Table);
            properties.SetInt("_LoogaVTCompressed", _compression != null ? 1 : 0);
            properties.SetInt("_LoogaVTBlendMips", Settings.BlendMipTransitions ? 1 : 0);
            properties.SetTexture("_LoogaVTAlbedo", _compression != null ? _compression.Texture(0) : _physical[0]);
            properties.SetTexture("_LoogaVTNormal", _compression != null ? _compression.Texture(1) : _physical[1]);
            properties.SetTexture("_LoogaVTEmission", _physical[2]);
            properties.SetVector("_LoogaVTInfo", _info);
        }
        internal Vector4 BindGroundTextures()
        {
            Shader.SetGlobalTexture("_LoogaGroundCacheAlbedo", _compression != null ? _compression.Texture(0) : _physical[0]);
            Shader.SetGlobalTexture("_LoogaGroundCacheNormal", _compression != null ? _compression.Texture(1) : _physical[1]);
            Shader.SetGlobalTexture("_LoogaGroundCacheEmission", _physical[2]);
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
                    Invalidate(tile, dirtyRegion);
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
                _leafRequests.Clear();
                foreach (var leaf in leaves)
                {
                    if (_tiles.ContainsKey((uint)(leaf.Key >> 32)))
                    {
                        _leafRequests[leaf.Key] = leaf.Count;
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
                feedback = new Feedback
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
            if (!viewChanged && _frame - feedback.LastFrame < Settings.StationaryFeedbackInterval) return;
            feedback.LastFrame = _frame;
            feedback.Revision = _feedbackRevision;
            feedback.PageCell = pageCell;
            feedback.Rotation = camera.transform.rotation;
            feedback.Projection = projection;
            feedback.Orthographic = camera.orthographic;
            feedback.HasView = true;
            _commands.Clear();
            _commands.SetRenderTarget(feedback.Color.colorBuffer, feedback.Depth.depthBuffer);
            _commands.ClearRenderTarget(true, true, Color.clear, SystemInfo.usesReversedZBuffer ? 0f : 1f);
            _commands.SetViewport(new Rect(0, 0, width, height));
            _commands.SetGlobalMatrix("_LoogaVTViewProjection", GL.GetGPUProjectionMatrix(camera.projectionMatrix, false) * camera.worldToCameraMatrix);
            foreach (TerrainRenderer renderer in renderers)
            {
                TerrainVirtualTexture component = renderer.VirtualTexture;
                if (!component || component.Tile == null || !_tiles.TryGetValue(component.Tile.Id, out Tile tile) || tile != component.Tile)
                {
                    continue;
                }
                renderer.DrawVirtualTextureFeedback(camera, _commands, _feedbackMaterial, tile.Id, _info,
                    (float)height / Mathf.Max(1, camera.pixelHeight));
            }
            bool compact = feedback.Compaction != null && (ulong)_nextTile * (uint)_layout.EntryCount < uint.MaxValue;
            if (compact)
            {
                feedback.Compaction.Record(_commands, feedback.Color, _layout.EntryCount);
            }
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
                    _leafRequests.Clear();
                    for (int index = 0; index < data.Length; index += 2)
                    {
                        uint key = data[index] - 1;
                        uint id = key / (uint)_layout.EntryCount + 1;
                        if (!_tiles.ContainsKey(id)) continue;
                        _leafRequests[VirtualPagePool.Key(id, (int)(key % (uint)_layout.EntryCount))] = (int)data[index + 1];
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
                    CollectRequests(request);
                }
            });
        }
        #endregion

        private void CollectRequests(AsyncGPUReadbackRequest request)
        {
            _leafRequests.Clear();
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
                _leafRequests.TryGetValue(leaf, out int frequency);
                _leafRequests[leaf] = frequency + 1;
            }
            ApplyRequests();
        }

        private void ApplyRequests()
        {
            _pool.BeginEpoch();
            _requested.Clear();
            // Expand each distinct page once instead of once for every feedback pixel.
            foreach (var leaf in _leafRequests)
            {
                uint id = (uint)(leaf.Key >> 32);
                int current = (int)(uint)leaf.Key;
                while (true)
                {
                    ulong key = VirtualPagePool.Key(id, current);
                    _requested.TryGetValue(key, out int count);
                    _requested[key] = count + leaf.Value;
                    int parent = _layout.Parent(current);
                    if (parent == current)
                    {
                        break;
                    }
                    current = parent;
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
            _pending.Sort((a, b) =>
            {
                int mip = _layout.Decode((int)(uint)b).z.CompareTo(_layout.Decode((int)(uint)a).z);
                if (mip != 0) return mip;
                int frequency = _requested[b].CompareTo(_requested[a]);
                return frequency != 0 ? frequency : a.CompareTo(b);
            });
            if (_pending.Count > Settings.ResidentPages * 4)
            {
                _pending.RemoveRange(Settings.ResidentPages * 4, _pending.Count - Settings.ResidentPages * 4);
            }
        }

        private void FillPages()
        {
            int examined = 0;
            _commands.Clear();
            while (examined < _pending.Count && _statistics.LastFrameFills < Settings.PageUpdatesPerFrame)
            {
                ulong key = _pending[examined++];
                if (!_tiles.TryGetValue((uint)(key >> 32), out Tile tile) || tile.SourceVersion < 0)
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
                for (int target = 0; target < 3; target++)
                {
                    _targets[target] = _compression != null
                        ? new RenderTargetIdentifier(_compression.Staging(target))
                        : new RenderTargetIdentifier(_physical[target], 0, CubemapFace.Unknown, slot);
                }
                // MRT bindings use the explicit slice for every color attachment.
                _commands.SetRenderTarget(_targets, BuiltinRenderTextureType.None, 0, CubemapFace.Unknown, _compression != null ? 0 : slot);
                _commands.SetViewport(new Rect(0, 0, 136, 136));
#if UNITY_EDITOR
                // Cached pages must not retain an asynchronous shader placeholder after reimport.
                UnityEditor.ShaderUtil.SetAsyncCompilation(_commands, false);
#endif
                _commands.DrawProcedural(Matrix4x4.identity, tile.Producer, bakePass,
                    MeshTopology.Triangles, 3, 1, tile.Properties);
#if UNITY_EDITOR
                UnityEditor.ShaderUtil.RestoreAsyncCompilation(_commands);
#endif
                if (_compression != null)
                {
                    _compression.Encode(_commands, slot);
                    _commands.CopyTexture(_compression.Staging(2), 0, 0, _physical[2], slot, 0);
                }
                // Publish only after filling and compression are queued before terrain rendering.
                Graphics.ExecuteCommandBuffer(_commands);
                _commands.Clear();
                Publish(tile, pageIndex, (uint)slot + 1);
                _statistics.PageFills++;
                _statistics.LastFrameFills++;
            }
            if (examined > 0)
            {
                _pending.RemoveRange(0, examined);
            }
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
