using System;
using System.Collections.Generic;
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
        private static readonly Dictionary<(VirtualTextureSettings, int), VirtualSurfaceCache> _caches = new();

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
        private void Register()
        {
            if (_renderer && _registeredSurface != _renderer.SurfaceMaterial)
            {
                Unregister();
                _registrationFailed = false;
                _registeredSurface = _renderer.SurfaceMaterial;
            }
            if (_configurationDirty)
            {
                Unregister();
                _registrationFailed = false;
                _configurationDirty = false;
            }
            if (_cache != null && (!_settings || _cache.SettingsRevision != _settings.Revision))
            {
                Unregister();
            }
            if (_registrationFailed || _cache != null || !_renderer || !_renderer.IsReady || !_settings || !_pageProducer) return;
            VirtualSurfaceCache cache = null;
            try
            {
                if (!_renderer.SupportsVirtualTexturing || _pageProducer.FindPass("LoogaVTBake") < 0)
                {
                    throw new InvalidOperationException("Assign a virtual-texture surface and its matching page producer.");
                }
                string schema = _renderer.SurfaceMaterial.GetTag(SchemaTag, false, "");
                string producerSchema = _pageProducer.GetTag(SchemaTag, false, "");
                if (schema != producerSchema)
                {
                    throw new InvalidOperationException("The surface and page producer schemas differ. Re-export and assign the matching pair.");
                }
                if (!_caches.TryGetValue((_settings, _settings.Revision), out cache))
                {
                    cache = new VirtualSurfaceCache(_settings);
                    _caches.Add((_settings, _settings.Revision), cache);
                }
                Tile = cache.Add(_renderer, _pageProducer);
                _cache = cache;
                Diagnostic = string.Empty;
            }
            catch (Exception exception)
            {
                if (cache != null && cache.TileCount == 0)
                {
                    _caches.Remove((_settings, _settings.Revision));
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
                _caches.Remove((_cache.Settings, _cache.SettingsRevision));
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
        public bool GroundNormalsAreWorldSpace => _pageProducer && _pageProducer.GetTag("LoogaVTNormalSpace", false, "TerrainTangent") == "World";

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
            foreach (TerrainRenderer renderer in renderers)
            {
                if (renderer.VirtualTexture)
                {
                    renderer.VirtualTexture.Register();
                }
            }
            foreach (VirtualSurfaceCache cache in _caches.Values)
            {
                cache.BeginCamera(camera, renderers);
            }
            GroundCachesUpdated?.Invoke();
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
    }
}
