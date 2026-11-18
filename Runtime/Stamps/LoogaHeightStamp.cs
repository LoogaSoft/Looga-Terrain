using UnityEngine;

namespace LoogaSoft.Terrain.Stamps
{
    public enum HeightCombineMode
    {
        Override = 0,
        Max = 1,
        Min = 2,
        Add = 3
    }

    /// <summary>
    /// Changes terrain height in a square area. The transform sets the area: position is the centre and the bottom
    /// height, scale x and z set the size, scale y sets the height range, and the y rotation turns the area.
    /// A parent <see cref="LoogaStampWorld"/> applies its stamps in hierarchy order.
    /// </summary>
    [AddComponentMenu("Looga/Stamps/Height Stamp")]
    public sealed class LoogaHeightStamp : LoogaHeightModifier
    {
        [Header("Height")]
        [Tooltip("Override = stamp height. Max and Min keep the higher or lower height. Add adds the stamp height.")]
        [SerializeField] private HeightCombineMode _mode = HeightCombineMode.Max;
        [Tooltip("Height texture, 0 = bottom and 1 = top of the stamp. Empty gives a flat plane at the stamp position.")]
        [SerializeField] private Texture2D _texture;
        [Tooltip("Mip level of the texture samples. Fractions blend two levels. Textures without mipmaps ignore it.")]
        [SerializeField, Range(0f, 6f)] private float _mipBias;
        [Tooltip("Exponent on the texture sample. 1 = unchanged.")]
        [SerializeField, Range(0.1f, 8f)] private float _power = 1f;
        [Tooltip("Height added across the stamp, in stamp height units. " +
                 "X slopes along the stamp X axis, Y slopes along the stamp Z axis.")]
        [SerializeField] private Vector2 _tilt;

        [Header("Falloff")]
        [SerializeField] private StampFalloff _falloff = new StampFalloff();

        public HeightCombineMode Mode => _mode;
        public Texture2D Texture => _texture;
        public float MipBias => _mipBias;
        public float Power => _power;
        public Vector2 Tilt => _tilt;
        public StampFalloff Falloff => _falloff;

        /// <summary>Height texture of the evaluation: from a texture source or from this stamp.</summary>
        internal Texture EvaluatedTexture => TryGetSource(out LoogaStampTextureSource source) && source.HeightTexture
            ? source.HeightTexture
            : _texture;

        /// <summary>Falloff texture that the evaluation uses: from a source or the falloff settings.</summary>
        internal Texture EvaluatedFalloffTexture => LoogaStampTextureSource.FalloffTextureOf(this, _falloff);

        /// <summary>Sets the height settings. Converters and tests use it.</summary>
        public void SetHeight(HeightCombineMode mode, Texture2D texture, float mipBias, float power, Vector2 tilt)
        {
            _mode = mode;
            _texture = texture;
            _mipBias = mipBias;
            _power = power;
            _tilt = tilt;
            MarkChanged();
        }

        internal override Rect GetCullRect(HeightMapping mapping)
        {
            // A spline area falloff limits the stamp to the area, like MicroVerse HeightStamp.GetBounds. A water area
            // does the same.
            if (_falloff.TryGetAreaRect(out Rect area))
            {
                return area;
            }
            if (_falloff.Shape == FalloffShape.Global && _falloff.PaintedArea && _falloff.PaintedArea.LimitToBounds)
            {
                return StampMapping.CullRect(_falloff.PaintedArea.transform, mapping);
            }
            return StampMapping.CullRect(transform, mapping);
        }

        private bool TryGetSource(out LoogaStampTextureSource source)
        {
            return TryGetComponent(out source) && source.isActiveAndEnabled;
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(0.3f, 0.8f, 1f, 1f);
            Gizmos.matrix = Matrix4x4.TRS(transform.position, Quaternion.Euler(0f, transform.eulerAngles.y, 0f),
                Vector3.one);
            Vector3 scale = transform.lossyScale;
            Gizmos.DrawWireCube(new Vector3(0f, scale.y * 0.5f, 0f), scale);
        }
    }
}
