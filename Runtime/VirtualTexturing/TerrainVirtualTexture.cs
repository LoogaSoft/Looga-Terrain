using System;
using System.Collections.Generic;
using LoogaSoft.Instancing;
using UnityEngine;

namespace LoogaSoft.Terrain
{
    /// <summary>
    /// Connects one terrain to a shared virtual surface cache.
    /// </summary>
    [ExecuteAlways, DisallowMultipleComponent, RequireComponent(typeof(TerrainRenderer))]
    public sealed class TerrainVirtualTexture : MonoBehaviour
    {
        /// <summary>Identifies matching generated surface and producer shaders.</summary>
        public const string SchemaTag = "LoogaVirtualSurfaceSchema";

        [SerializeField] private VirtualTextureSettings _settings;
        [SerializeField] private Material _pageProducer;
        private TerrainRenderer _renderer;
        private Material _registeredSurface;
        private VirtualSurfaceCache _cache;
        private bool _configurationDirty;
        private bool _registrationFailed;
        private static GraphicsBuffer _fallbackTable;
        private static Texture2DArray _fallbackTexture;
        internal VirtualSurfaceCache.Tile Tile { get; private set; }
        private static readonly Dictionary<(VirtualTextureSettings, int, int), VirtualSurfaceCache> _caches = new();
        // Pixels and last render time of each camera that renders cached terrain. The pool is sized for the largest.
        private static readonly Dictionary<Camera, (long Pixels, float Time)> _cameraPixels = new();
        private static readonly List<Camera> _expiredCameras = new();
        private static long _poolPixels;
        private static float _shrinkSince = -1f;
        // A camera that has not rendered for this time no longer sizes the pool. A smaller pool waits for the shrink
        // delay, so a window resize or a closed view does not rebuild the cache at once.
        private const float CameraTimeout = 2f;
        private const float ShrinkDelay = 10f;

        /// <summary>
        /// Shades cached surfaces directly while the cache stays resident, for example for comparisons. Surfaces that
        /// check the switch read _LoogaVTShadeDirectly.
        /// </summary>
        public static bool ShadeDirectly
        {
            get => Shader.GetGlobalFloat(ShadeDirectlyId) > 0.5f;
            set => Shader.SetGlobalFloat(ShadeDirectlyId, value ? 1f : 0f);
        }
        private static readonly int ShadeDirectlyId = Shader.PropertyToID("_LoogaVTShadeDirectly");
        private static readonly int ResidencyBiasId = Shader.PropertyToID("_LoogaVTResidencyBias");

        /// <summary>Gets the camera pixels that size the shared pools. 0 = no camera yet (reference size).</summary>
        public static long PoolPixels => _poolPixels;

        /// <summary>Gets the most recent configuration failure.</summary>
        public string Diagnostic { get; private set; } = string.Empty;
        /// <summary>Gets statistics for this component's shared cache.</summary>
        public VirtualTextureStatistics Statistics => _cache?.Statistics ?? default;

        #region Built-in
        private void OnEnable()
        {
            _registrationFailed = false;
            _renderer = GetComponent<TerrainRenderer>();
            _renderer.VirtualTexture = this;
            Register();
        }

        private void OnDisable()
        {
            Unregister();
            if (_renderer && _renderer.VirtualTexture == this)
            {
                _renderer.VirtualTexture = null;
            }
        }

        private void OnValidate()
        {
            // Unity can validate assets outside the render thread.
            _configurationDirty = true;
        }
        #endregion

        /// <summary>Assigns shared settings and a compatible page producer.</summary>
        public void Configure(VirtualTextureSettings settings, Material pageProducer)
        {
            Unregister();
            _registrationFailed = false;
            _settings = settings;
            _pageProducer = pageProducer;
            if (isActiveAndEnabled)
            {
                Register();
            }
        }

        /// <summary>Removes cached pages after external material changes.</summary>
        public void Invalidate()
        {
            if (Tile != null)
            {
                GroundCacheInvalidated?.Invoke();
                _cache.Invalidate(Tile);
            }
        }

        /// <summary>
        /// Invalidates the cached pages of all tiles inside a world XZ area, after a change of world data that the page
        /// producers read, such as the ground cover or world colour maps. Null invalidates all pages.
        /// </summary>
        public static void InvalidateWorldArea(Rect? worldArea)
        {
            if (_caches.Count == 0) return;
            GroundCacheInvalidated?.Invoke();
            foreach (VirtualSurfaceCache cache in _caches.Values)
            {
                cache.InvalidateWorld(worldArea);
            }
        }

        /// <summary>Invalidates cached pages inside normalized terrain coordinates.</summary>
        public void Invalidate(Rect normalizedRegion)
        {
            if (Tile != null)
            {
                GroundCacheInvalidated?.Invoke();
                _cache.Invalidate(Tile, normalizedRegion);
            }
        }

        #region Cache binding
        // The assigned producer, or the drawn surface when it has its own page pass. A surface that a component supplies
        // at run time, such as a Looga terrain surface binding, cannot be assigned as an asset.
        private Material PageProducer
        {
            get
            {
                if (_pageProducer) return _pageProducer;
                Material surface = _renderer ? _renderer.ActiveSurfaceMaterial : null;
                return surface && surface.FindPass("LoogaVTBake") >= 0 ? surface : null;
            }
        }

        private void Register()
        {
            if (_renderer && _registeredSurface != _renderer.ActiveSurfaceMaterial)
            {
                Unregister();
                _registrationFailed = false;
                _registeredSurface = _renderer.ActiveSurfaceMaterial;
            }
            if (_configurationDirty)
            {
                Unregister();
                _registrationFailed = false;
                _configurationDirty = false;
            }
            int capacity = _settings ? _settings.PagesForPixels(_poolPixels) : 0;
            if (_cache != null && (!_settings || _cache.SettingsRevision != _settings.Revision || _cache.Capacity != capacity))
            {
                Unregister();
            }
            Material producer = PageProducer;
            if (_registrationFailed || _cache != null || !_renderer || !_renderer.IsReady || !_settings || !producer) return;
            VirtualSurfaceCache cache = null;
            try
            {
                if (!_renderer.SupportsVirtualTexturing || producer.FindPass("LoogaVTBake") < 0)
                {
                    throw new InvalidOperationException("Assign a virtual-texture surface and its matching page producer.");
                }
                string schema = InstanceShaderInspection.GetTag(_renderer.ActiveSurfaceMaterial, SchemaTag);
                string producerSchema = InstanceShaderInspection.GetTag(producer, SchemaTag);
                if (schema != producerSchema)
                {
                    throw new InvalidOperationException("The surface and page producer schemas differ. Re-export and assign the matching pair.");
                }
                if (!_caches.TryGetValue((_settings, _settings.Revision, capacity), out cache))
                {
                    cache = new VirtualSurfaceCache(_settings, capacity);
                    _caches.Add((_settings, _settings.Revision, capacity), cache);
                }
                Tile = cache.Add(_renderer, producer);
                _cache = cache;
                Diagnostic = string.Empty;
            }
            catch (Exception exception)
            {
                if (cache != null && cache.TileCount == 0)
                {
                    _caches.Remove((_settings, _settings.Revision, capacity));
                    cache.Dispose();
                }
                _registrationFailed = true;
                Diagnostic = exception.Message;
                Debug.LogWarning("Looga virtual texture: " + Diagnostic, this);
            }
        }

        private void Unregister()
        {
            if (_cache == null) return;
            GroundCacheInvalidated?.Invoke();
            _cache.Remove(Tile);
            if (_cache.TileCount == 0)
            {
                _caches.Remove((_cache.Settings, _cache.SettingsRevision, _cache.Capacity));
                _cache.Dispose();
            }
            _cache = null;
            Tile = null;
        }

        /// <summary>Raised after page table changes are complete and before dependent rendering.</summary>
        public static event Action GroundCachesUpdated;
        /// <summary>Raised before borrowed cache resources can become invalid. Receivers must stop sampling until refreshed.</summary>
        public static event Action GroundCacheInvalidated;

        /// <summary>Get existing cache identity and page metadata. A receiver never creates a separate surface cache.</summary>
        public bool TryGetGroundCache(out object identity, out int entries, out long revision)
        {
            identity = null;
            entries = 0;
            revision = 0;
            if (!isActiveAndEnabled) return false;
            Register();
            if (_cache == null || Tile == null) return false;
            identity = _cache;
            entries = Tile.Entries.Length;
            revision = ((long)Tile.Id << 32) | (uint)Tile.TableRevision;
            return true;
        }
        /// <summary>True when the producer stores world normals. Older MicroSplat producers store terrain tangent normals.</summary>
        public bool GroundNormalsAreWorldSpace => InstanceShaderInspection.GetTag(PageProducer, "LoogaVTNormalSpace") == "World";

        /// <summary>Copy existing page addresses into a receiver lookup buffer. Physical surface pages remain shared.</summary>
        public void CopyGroundPageTable(uint[] destination, int offset)
        {
            if (Tile == null) throw new InvalidOperationException("The ground cache is unavailable.");
            Array.Copy(Tile.Entries, 0, destination, offset, Tile.Entries.Length);
        }
        /// <summary>Bind the existing shared atlases. Use one cache identity for all tables in a receiver set.</summary>
        public Vector4 BindGroundCacheTextures()
        {
            if (_cache == null) throw new InvalidOperationException("The ground cache is unavailable.");
            return _cache.BindGroundTextures();
        }

        internal void Bind(MaterialPropertyBlock properties)
        {
            if (Tile == null)
            {
                BindFallback(properties);
                return;
            }
            _cache.Bind(Tile, properties);
        }


        internal static void BindFallback(MaterialPropertyBlock properties)
        {
            if (_fallbackTable == null)
            {
                _fallbackTable = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, 4);
                _fallbackTable.SetData(new uint[1]);
                _fallbackTexture = new Texture2DArray(1, 1, 1, TextureFormat.RGBA32, false)
                {
                    hideFlags = HideFlags.HideAndDontSave
                };
                _fallbackTexture.SetPixels(new[] { Color.white }, 0);
                _fallbackTexture.Apply(false, true);
            }
            // Some graphics APIs require valid resources even when the cache branch is disabled.
            properties.SetInt("_LoogaVTEnabled", 0);
            properties.SetInt("_LoogaVTBlendMips", 0);
            properties.SetBuffer("_LoogaVTTable", _fallbackTable);
            properties.SetTexture("_LoogaVTAlbedo", _fallbackTexture);
            properties.SetTexture("_LoogaVTNormal", _fallbackTexture);
            properties.SetTexture("_LoogaVTEmission", _fallbackTexture);
        }

        internal static void ReleaseFallback()
        {
            _fallbackTable?.Dispose();
            _fallbackTable = null;
            if (_fallbackTexture)
            {
                if (Application.isPlaying)
                {
                    Destroy(_fallbackTexture);
                }
                else
                {
                    DestroyImmediate(_fallbackTexture);
                }
            }
            _fallbackTexture = null;
        }

        internal static void BeginCamera(Camera camera, List<TerrainRenderer> renderers)
        {
            UpdatePoolPixels(camera);
            foreach (TerrainRenderer renderer in renderers)
            {
                if (renderer.VirtualTexture)
                {
                    renderer.VirtualTexture.Register();
                }
            }
            float residencyBias = 0f;
            foreach (VirtualSurfaceCache cache in _caches.Values)
            {
                cache.BeginCamera(camera, renderers);
                residencyBias = Mathf.Max(residencyBias, cache.ResidencyBias);
            }
            // One global bias for all caches, as the feedback shader is shared. The largest bias of a cache applies.
            Shader.SetGlobalFloat(ResidencyBiasId, residencyBias);
            GroundCachesUpdated?.Invoke();
        }

        // The pool follows the largest game camera, or the largest Scene view when no game camera renders. Preview and
        // reflection cameras do not size it.
        private static void UpdatePoolPixels(Camera camera)
        {
            float now = Time.realtimeSinceStartup;
            if (camera.cameraType == CameraType.Game || camera.cameraType == CameraType.SceneView)
            {
                _cameraPixels[camera] = ((long)camera.pixelWidth * camera.pixelHeight, now);
            }
            long game = 0;
            long any = 0;
            _expiredCameras.Clear();
            foreach (var entry in _cameraPixels)
            {
                if (!entry.Key || now - entry.Value.Time > CameraTimeout)
                {
                    _expiredCameras.Add(entry.Key);
                    continue;
                }
                any = Math.Max(any, entry.Value.Pixels);
                if (entry.Key.cameraType == CameraType.Game)
                {
                    game = Math.Max(game, entry.Value.Pixels);
                }
            }
            foreach (Camera old in _expiredCameras)
            {
                _cameraPixels.Remove(old);
            }
            long target = game > 0 ? game : any;
            if (target > _poolPixels)
            {
                _poolPixels = target;
                _shrinkSince = -1f;
            }
            else if (target < _poolPixels * 3 / 4)
            {
                if (_shrinkSince < 0f)
                {
                    _shrinkSince = now;
                }
                else if (now - _shrinkSince > ShrinkDelay)
                {
                    _poolPixels = target;
                    _shrinkSince = -1f;
                }
            }
            else
            {
                _shrinkSince = -1f;
            }
        }

        internal static void EndCamera(Camera camera, List<TerrainRenderer> renderers)
        {
            foreach (VirtualSurfaceCache cache in _caches.Values)
            {
                cache.EndCamera(camera, renderers);
            }
        }
        #endregion
    }

    /// <summary>Reports bounded cache use and cumulative page activity.</summary>
    [Serializable]
    public struct VirtualTextureStatistics
    {
        /// <summary>Reports whether this cache uses GPU BC3 storage.</summary>
        public bool Compressed;
        /// <summary>Gets encoder workspace bytes, separate from resident surface storage.</summary>
        public long StagingBytes;
        public int ResidentPages;
        public int Capacity;
        public long CacheBytes;
        public long PageFills;
        public long Evictions;
        public long RequestedPages;
        public long ResidentRequests;
        public long FeedbackReadbacks;
        public long FeedbackBytes;
        public int PendingPages;
        public int LastFrameFills;
        public long LocalInvalidations;
        public long InvalidatedPages;
        /// <summary>Gets the mip bias that the residency control applies to all reads of this cache.</summary>
        public float ResidencyMipBias;
    }
}
