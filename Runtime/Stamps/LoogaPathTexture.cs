using UnityEngine;

namespace LoogaSoft.Terrain.Stamps
{
    /// <summary>
    /// Paints one terrain layer along the spline path on the same object. The weight is 1 up to the width outside the
    /// path width and falls to 0 over the smoothness. It uses the distance field of the path, also when the path
    /// component is disabled (then the path paints but does not change heights).
    /// </summary>
    [AddComponentMenu("Looga/Stamps/Path Texture")]
    [RequireComponent(typeof(LoogaSplinePath))]
    public sealed class LoogaPathTexture : LoogaTextureModifier
    {
        [Tooltip("The terrain layer to paint. The stamp world adds it to the terrains.")]
        [SerializeField] private TerrainLayer _layer;
        [Tooltip("Distance outside the path width with full weight, in metres.")]
        [SerializeField, Min(0f)] private float _width = 1f;
        [Tooltip("Distance over which the weight falls to 0, in metres.")]
        [SerializeField, Min(0f)] private float _smoothness = 2f;
        [SerializeField, Range(0f, 1f)] private float _weight = 1f;

        private LoogaSplinePath _path;

        public override TerrainLayer Layer => _layer;
        public float Width => _width;
        public float Smoothness => _smoothness;
        public float Weight => _weight;

        /// <summary>The spline path whose distance field gives the weight.</summary>
        public LoogaSplinePath Path => _path ? _path : _path = GetComponent<LoogaSplinePath>();

        /// <summary>Sets the texture settings. Converters and tests use it.</summary>
        public void SetTexture(TerrainLayer layer, float width, float smoothness, float weight)
        {
            _layer = layer;
            _width = width;
            _smoothness = smoothness;
            _weight = weight;
            MarkChanged();
        }

        internal override Rect GetCullRect(HeightMapping mapping)
        {
            return Path ? Path.GetCullRect(mapping) : Rect.zero;
        }
    }
}
