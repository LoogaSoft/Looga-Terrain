using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace LoogaSoft.Terrain
{
    /// <summary>Shares terrain RVT atlases with vegetation through one compact address buffer. Missing data uses direct ground sampling.</summary>
    [ExecuteAlways, DisallowMultipleComponent]
    public sealed class TerrainGroundCacheBindings : MonoBehaviour
    {
        [SerializeField] private TerrainVirtualTexture[] _sources = Array.Empty<TerrainVirtualTexture>();
        private static TerrainGroundCacheBindings _owner;
        private readonly Vector4[] _regions = new Vector4[16];
        private readonly Vector4[] _slots = new Vector4[16];
        private readonly long[] _revisions = new long[16];
        private readonly TerrainVirtualTexture[] _bound = new TerrainVirtualTexture[16];
        private GraphicsBuffer _table;
        private uint[] _addresses;
        private object _identity;
        /// <summary>Current configuration failure, or null.</summary>
        public string Diagnostic { get; private set; }
        private void OnEnable()
        {
            if (_owner && _owner != this)
            {
                Diagnostic = "Only one ground cache binding set can be active.";
                return;
            }
            _owner = this;
            RenderPipelineManager.beginCameraRendering += BeforeCamera;
            TerrainVirtualTexture.GroundCachesUpdated += Publish;
            TerrainVirtualTexture.GroundCacheInvalidated += Suspend;
        }
        private void OnDisable()
        {
            RenderPipelineManager.beginCameraRendering -= BeforeCamera;
            TerrainVirtualTexture.GroundCachesUpdated -= Publish;
            TerrainVirtualTexture.GroundCacheInvalidated -= Suspend;
            if (_owner != this) return;
            Suspend();
            _owner = null;
            Shader.SetGlobalBuffer("_LoogaGroundCacheTable", (GraphicsBuffer)null);
            Shader.SetGlobalTexture("_LoogaGroundCacheAlbedo", null);
            Shader.SetGlobalTexture("_LoogaGroundCacheNormal", null);
            Shader.SetGlobalTexture("_LoogaGroundCacheEmission", null);
            _table?.Dispose();
            _table = null;
            _identity = null;
            _addresses = null;
        }
        private void Suspend()
        {
            Shader.DisableKeyword("LOOGA_GROUND_CACHE");
            Shader.SetGlobalInt("_LoogaGroundCacheCount", 0);
        }
        /// <summary>Assign up to sixteen terrains from one shared cache. Other cache identities use direct ground fallback.</summary>
        public void Configure(TerrainVirtualTexture[] sources)
        {
            if (sources == null || sources.Length > 16) throw new ArgumentException("Assign up to sixteen source terrains.");
            _sources = (TerrainVirtualTexture[])sources.Clone();
            Array.Clear(_bound, 0, _bound.Length);
        }
        private void BeforeCamera(ScriptableRenderContext context, Camera camera) => Publish();
        /// <summary>Update changed lookup ranges only. Existing surface atlases remain owned by the terrain cache.</summary>
        public void Publish()
        {
            if (_owner != this) return;
            object identity = null;
            int stride = 0;
            TerrainVirtualTexture primary = null;
            if (_sources == null || _sources.Length > 16)
            {
                Diagnostic = "Assign zero to sixteen source terrains.";
                Suspend();
                return;
            }
            int count = _sources.Length;
            for (int i = 0; i < count; i++)
            {
                if (_sources[i] && _sources[i].TryGetGroundCache(out identity, out stride, out _))
                {
                    primary = _sources[i];
                    break;
                }
            }
            if (!primary)
            {
                Suspend();
                return;
            }
            int capacity = checked(stride * count);
            if (_table == null || _table.count != capacity || !ReferenceEquals(identity, _identity))
            {
                Suspend();
                Shader.SetGlobalBuffer("_LoogaGroundCacheTable", (GraphicsBuffer)null);
                _table?.Dispose();
                _addresses = new uint[capacity];
                _table = new GraphicsBuffer(GraphicsBuffer.Target.Structured, capacity, 4);
                _table.SetData(_addresses);
                _identity = identity;
                Array.Clear(_bound, 0, _bound.Length);
            }
            Diagnostic = null;
            for (int i = 0; i < count; i++)
            {
                _slots[i] = Vector4.zero;
                var source = _sources[i];
                if (!source || !source.TryGetGroundCache(out object cache, out int entries, out long revision)) continue;
                if (!ReferenceEquals(identity, cache) || entries != stride)
                {
                    Diagnostic = "Sources from another cache use direct ground fallback. Share one settings asset for cached ground blending.";
                    continue;
                }
                var terrain = source.GetComponent<UnityEngine.Terrain>();
                if (!terrain || !terrain.terrainData) continue;
                if (terrain.transform.rotation != Quaternion.identity || terrain.transform.lossyScale != Vector3.one)
                {
                    Diagnostic = "Cached ground surfaces require unrotated, unscaled terrain transforms.";
                    continue;
                }
                if (_bound[i] != source || _revisions[i] != revision)
                {
                    source.CopyGroundPageTable(_addresses, i * stride);
                    _table.SetData(_addresses, i * stride, i * stride, stride);
                    _bound[i] = source;
                    _revisions[i] = revision;
                }
                Vector3 origin = terrain.transform.position;
                Vector3 size = terrain.terrainData.size;
                _regions[i] = new Vector4(origin.x, origin.z, size.x, size.z);
                _slots[i] = new Vector4(1, i * stride, stride, source.GroundNormalsAreWorldSpace ? 1 : 0);
            }
            Shader.SetGlobalVector("_LoogaGroundCacheInfo", primary.BindGroundCacheTextures());
            Shader.SetGlobalVectorArray("_LoogaGroundCacheRegions", _regions);
            Shader.SetGlobalVectorArray("_LoogaGroundCacheSlots", _slots);
            Shader.SetGlobalBuffer("_LoogaGroundCacheTable", _table);
            Shader.SetGlobalInt("_LoogaGroundCacheCount", count);
            Shader.EnableKeyword("LOOGA_GROUND_CACHE");
        }
    }
}
