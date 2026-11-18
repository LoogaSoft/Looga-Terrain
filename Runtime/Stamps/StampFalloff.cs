using System;
using UnityEngine;

namespace LoogaSoft.Terrain.Stamps
{
    // Values must agree with Shaders/StampFalloff.hlsl.
    public enum FalloffShape
    {
        Global = 0,
        Box = 1,
        Range = 2,
        Texture = 3,
        SplineArea = 4,
        WaterArea = 5
    }

    public enum FalloffEasing
    {
        Linear = 0,
        Smoothstep = 1,
        EaseIn = 2,
        EaseOut = 3,
        EaseInOut = 4
    }

    // Values must agree with Shaders/StampNoise.hlsl.
    public enum StampNoiseType
    {
        None = 0,
        Fbm = 1,
        /// <summary>A texture channel. Only placement weight noise supports it.</summary>
        Texture = 2,
        /// <summary>Cell noise. Only placement weight noise supports it.</summary>
        Worley = 3,
        /// <summary>Fractal sine-wave noise. Only placement weight noise supports it.</summary>
        WormFbm = 4,
        /// <summary>
        /// Fbm with an exact integer hash. Use it for high frequencies far from the world origin, where Fbm shows
        /// stripes and blocks. Placement weight noise, smooth stamp height noise, path embankment noise and water
        /// filter noise support it. Falloff noise does not.
        /// </summary>
        PreciseFbm = 5
    }

    public enum StampNoiseSpace
    {
        World = 0,
        Stamp = 1
    }

    /// <summary>
    /// Weight of a stamp across its area. Weight 1 applies the stamp fully, weight 0 keeps the previous height.
    /// </summary>
    [Serializable]
    public sealed class StampFalloff
    {
        [Tooltip("Global = full weight everywhere. Box = square edge. Range = round edge. Texture = weight from a texture. " +
                 "Spline Area = weight from the distance to a spline area. " +
                 "Water Area = weight from the distance to the water edge.")]
        [SerializeField] private FalloffShape _shape = FalloffShape.Range;
        [Tooltip("Range: inner and outer diameter as a fraction of the stamp size. " +
                 "Box and Texture: y sets the edge width, 1 = no edge.")]
        [SerializeField] private Vector2 _range = new Vector2(0.8f, 1f);
        [Tooltip("Shape of the weight curve. Global ignores it.")]
        [SerializeField] private FalloffEasing _easing = FalloffEasing.Linear;

        [Header("Texture")]
        [SerializeField] private Texture2D _texture;
        [SerializeField, Range(0, 3)] private int _textureChannel;
        [Tooltip("Weight = sample * x + sample * y.")]
        [SerializeField] private Vector2 _textureAmplitudeBalance = new Vector2(1f, 0f);
        [Tooltip("Turn of the texture around the stamp centre, in radians.")]
        [SerializeField] private float _textureRotation;
        [SerializeField] private float _textureScale = 1f;
        [SerializeField] private Vector2 _textureOffset;

        [Header("Spline Area")]
        [SerializeField] private LoogaSplineArea _splineArea;
        [Tooltip("Distance in metres over which the weight goes from 1 to 0.")]
        [SerializeField] private float _splineAreaRange = 10f;
        [Tooltip("Moves the full-weight edge outward from the spline, in metres.")]
        [SerializeField] private float _splineAreaBoost;
        [Tooltip("Off: full weight inside the area, down to 0 at the boost distance. On: 0 inside the area and up to " +
                 "the boost distance outside it, then up to full weight over the range.")]
        [SerializeField] private bool _splineAreaOutside;
        [Tooltip("Weight on both sides of the spline line: 0 at the boost distance, full at the boost minus the range. " +
                 "Use it with a Smooth Stamp to soften the ground along the edge of an area. Outside is ignored.")]
        [SerializeField] private bool _splineAreaEdge;

        [Header("Water Area")]
        [SerializeField] private LoogaWaterArea _waterArea;
        [Tooltip("Distance in metres over which the weight goes from 1 to 0.")]
        [SerializeField] private float _waterAreaRange = 10f;
        [Tooltip("Moves the full-weight edge outward from the water edge, in metres. " +
                 "Boost = range gives weight 1 at the water edge.")]
        [SerializeField] private float _waterAreaBoost;

        [Header("Painted Area")]
        [Tooltip("Optional painted weight. It multiplies the shape weight, also for Global.")]
        [SerializeField] private LoogaPaintedArea _paintedArea;

        [Header("Noise")]
        [Tooltip("Moves the falloff edge. Global ignores it.")]
        [SerializeField] private StampNoiseType _noiseType = StampNoiseType.None;
        [SerializeField] private StampNoiseSpace _noiseSpace = StampNoiseSpace.World;
        [Tooltip("World space: cycles for each 1000 m. Stamp space: cycles across the stamp.")]
        [SerializeField] private float _noiseFrequency = 10f;
        [Tooltip("Noise strength in metres.")]
        [SerializeField] private float _noiseAmplitude = 1f;
        [SerializeField] private float _noiseOffset;
        [SerializeField, Range(-0.5f, 0.5f)] private float _noiseBalance;

        public FalloffShape Shape => _shape;
        public Vector2 Range => _range;
        public FalloffEasing Easing => _easing;
        public Texture2D Texture => _texture;
        public int TextureChannel => _textureChannel;
        public Vector2 TextureAmplitudeBalance => _textureAmplitudeBalance;
        public float TextureRotation => _textureRotation;
        public float TextureScale => _textureScale;
        public Vector2 TextureOffset => _textureOffset;
        public LoogaSplineArea SplineArea => _splineArea;
        public float SplineAreaRange => _splineAreaRange;
        public float SplineAreaBoost => _splineAreaBoost;
        public bool SplineAreaOutside => _splineAreaOutside;
        public bool SplineAreaEdge => _splineAreaEdge;
        public LoogaWaterArea WaterArea => _waterArea;
        public float WaterAreaRange => _waterAreaRange;
        public float WaterAreaBoost => _waterAreaBoost;
        public LoogaPaintedArea PaintedArea => _paintedArea;
        public StampNoiseType NoiseType => _noiseType;
        public StampNoiseSpace NoiseSpace => _noiseSpace;
        public float NoiseFrequency => _noiseFrequency;
        public float NoiseAmplitude => _noiseAmplitude;
        public float NoiseOffset => _noiseOffset;
        public float NoiseBalance => _noiseBalance;

        /// <summary>Sets the falloff shape. Converters and tests use it.</summary>
        public void SetShape(FalloffShape shape, Vector2 range, FalloffEasing easing)
        {
            _shape = shape;
            _range = range;
            _easing = easing;
        }

        /// <summary>Sets the texture used by <see cref="FalloffShape.Texture"/>.</summary>
        public void SetTexture(Texture2D texture, int channel, Vector2 amplitudeBalance, float rotation, float scale,
            Vector2 offset)
        {
            _texture = texture;
            _textureChannel = Mathf.Clamp(channel, 0, 3);
            _textureAmplitudeBalance = amplitudeBalance;
            _textureRotation = rotation;
            _textureScale = scale;
            _textureOffset = offset;
        }

        /// <summary>Sets the area used by <see cref="FalloffShape.SplineArea"/>.</summary>
        /// <param name="edge">
        /// Weight on both sides of the spline line: 0 at the boost distance, full at the boost minus the range.
        /// Outside is then ignored.
        /// </param>
        public void SetSplineArea(LoogaSplineArea area, float range, float boost, bool outside = false,
            bool edge = false)
        {
            _splineArea = area;
            _splineAreaRange = range;
            _splineAreaBoost = boost;
            _splineAreaOutside = outside;
            _splineAreaEdge = edge;
        }

        /// <summary>Area side value of Shaders/StampFalloff.hlsl: 0 inside, 1 outside, 2 edge.</summary>
        internal int SplineAreaSide => _splineAreaEdge ? 2 : _splineAreaOutside ? 1 : 0;

        /// <summary>Sets the area used by <see cref="FalloffShape.WaterArea"/>.</summary>
        public void SetWaterArea(LoogaWaterArea area, float range, float boost)
        {
            _waterArea = area;
            _waterAreaRange = range;
            _waterAreaBoost = boost;
        }

        /// <summary>
        /// World XZ rectangle of a spline area or water area falloff. Stamps use it as their culling rectangle. False
        /// for the other shapes, and for a spline area with only Outside on, which has weight beyond the area.
        /// </summary>
        internal bool TryGetAreaRect(out Rect rect)
        {
            if (_shape == FalloffShape.SplineArea && _splineArea && SplineAreaSide != 1)
            {
                rect = _splineArea.GetCullRect();
                return true;
            }
            if (_shape == FalloffShape.WaterArea && _waterArea)
            {
                rect = _waterArea.GetCullRect();
                return true;
            }
            rect = default;
            return false;
        }

        /// <summary>Sets the optional painted weight. Null removes it.</summary>
        public void SetPaintedArea(LoogaPaintedArea area)
        {
            _paintedArea = area;
        }

        /// <summary>Sets the edge noise. Amplitude is in metres.</summary>
        public void SetNoise(StampNoiseType type, StampNoiseSpace space, float frequency, float amplitude, float offset,
            float balance)
        {
            _noiseType = type;
            _noiseSpace = space;
            _noiseFrequency = frequency;
            _noiseAmplitude = amplitude;
            _noiseOffset = offset;
            _noiseBalance = balance;
        }
    }
}
