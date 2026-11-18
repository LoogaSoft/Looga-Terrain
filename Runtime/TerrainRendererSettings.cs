using System;
using UnityEngine;

namespace LoogaSoft.Terrain
{
    /// <summary>
    /// Shared quality, culling, shadow and batching values for TerrainRenderer components. A renderer with this asset
    /// uses these values instead of its own. Create one asset for each group of terrains that needs other values.
    /// </summary>
    [CreateAssetMenu(menuName = "Looga/Terrain/Terrain Renderer Settings")]
    public sealed class TerrainRendererSettings : ScriptableObject
    {
        [Tooltip("Minimum patches along each tile edge. A change builds the renderer resources again.")]
        [SerializeField, Range(8, 128)] private int _patchCount = 64;
        [Tooltip("Largest projected height error of the camera patches, in pixels. Larger values draw coarser " +
                 "terrain.")]
        [SerializeField, Range(0.25f, 16f)] private float _errorPixels = 2f;
        [SerializeField] private bool _frustumCulling = true;
        [SerializeField] private bool _castShadows = true;
        [Tooltip("Largest height error of the shadow patches, in shadow texels of each shadow split. Larger values " +
                 "draw coarser shadow geometry.")]
        [SerializeField, Range(0.25f, 8f)] private float _shadowErrorTexels = 1f;
        [SerializeField] private bool _batchSurfaceDraws = true;
        [SerializeField, Range(2, 16)] private int _minimumSurfaceBatchTiles = 4;

        public int PatchCount => _patchCount;
        public float ErrorPixels => _errorPixels;
        public bool FrustumCulling => _frustumCulling;
        public bool CastShadows => _castShadows;
        public float ShadowErrorTexels => _shadowErrorTexels;
        public bool BatchSurfaceDraws => _batchSurfaceDraws;
        public int MinimumSurfaceBatchTiles => _minimumSurfaceBatchTiles;

        // Renderers compare this value each camera render and apply a change.
        internal int Revision { get; private set; }

        /// <summary>Sets the patch count. The renderers that use this asset build their resources again.</summary>
        public void SetPatchCount(int patchCount)
        {
            _patchCount = Mathf.Clamp(Mathf.ClosestPowerOfTwo(patchCount), 8, 128);
            Revision++;
        }

        /// <summary>Sets the projected height-error tolerance and frustum culling.</summary>
        public void SetLodQuality(float errorPixels, bool frustumCulling = true)
        {
            if (!float.IsFinite(errorPixels)) throw new ArgumentOutOfRangeException(nameof(errorPixels));
            _errorPixels = Mathf.Clamp(errorPixels, 0.25f, 16f);
            _frustumCulling = frustumCulling;
            Revision++;
        }

        /// <summary>Sets terrain shadow casting and the shadow height-error tolerance in shadow texels.</summary>
        public void SetShadows(bool castShadows, float errorTexels = 1f)
        {
            if (!float.IsFinite(errorTexels)) throw new ArgumentOutOfRangeException(nameof(errorTexels));
            _castShadows = castShadows;
            _shadowErrorTexels = Mathf.Clamp(errorTexels, 0.25f, 8f);
            Revision++;
        }

        /// <summary>Enables compatible multi-tile surface draws.</summary>
        public void SetSurfaceBatching(bool enabled, int minimumTiles = 4)
        {
            _batchSurfaceDraws = enabled;
            _minimumSurfaceBatchTiles = Mathf.Clamp(minimumTiles, 2, 16);
            Revision++;
        }

        private void OnValidate()
        {
            _patchCount = Mathf.Clamp(Mathf.ClosestPowerOfTwo(_patchCount), 8, 128);
            _errorPixels = Mathf.Clamp(_errorPixels, 0.25f, 16f);
            _shadowErrorTexels = Mathf.Clamp(_shadowErrorTexels, 0.25f, 8f);
            _minimumSurfaceBatchTiles = Mathf.Clamp(_minimumSurfaceBatchTiles, 2, 16);
            Revision++;
        }
    }
}
