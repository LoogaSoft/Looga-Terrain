using UnityEngine;

namespace LoogaSoft.Terrain.Stamps
{
    /// <summary>
    /// Smooths the terrain heights with a Gaussian blur. The blur reads the heights of the neighbour tiles, so tile
    /// seams stay continuous. The transform sets a box like a <see cref="LoogaHeightStamp"/>: the stamp has no effect
    /// outside the box. The falloff, the strength and up to two water filters set the weight. Like every
    /// <see cref="LoogaNeighbourhoodStamp"/>, it splits the stamp order.
    /// </summary>
    [AddComponentMenu("Looga/Stamps/Smooth Stamp")]
    public sealed class LoogaSmoothStamp : LoogaNeighbourhoodStamp
    {
        [Header("Smoothing")]
        [Tooltip("Gaussian sigma in metres. The blur reads up to three sigma around each sample.")]
        [SerializeField, Range(0.5f, 21f)] private float _sigma = 5f;
        [Tooltip("Largest height change in metres.")]
        [SerializeField, Min(0f)] private float _maxChange = 3f;
        [SerializeField, Range(0f, 1f)] private float _strength = 1f;
        [Tooltip("Noise added to the smoothed height, in metres.")]
        [SerializeField] private StampNoiseSettings _heightNoise = new StampNoiseSettings();

        [Header("Falloff")]
        [SerializeField] private StampFalloff _falloff = new StampFalloff();

        [Header("Water")]
        [Tooltip("Multiplies the weight.")]
        [SerializeField] private StampWaterFilter _waterFilter = new StampWaterFilter();
        [Tooltip("Multiplies the weight, for example to keep the stamp away from other water.")]
        [SerializeField] private StampWaterFilter _secondWaterFilter = new StampWaterFilter();
        [Tooltip("Keep the result at least the clearance above the water level of the first filter area: within the " +
                 "rim width of the water edge, and where the ground is above the water.")]
        [SerializeField] private bool _keepAboveWater;
        [SerializeField, Min(0f)] private float _rimWidth = 4f;
        [Tooltip("Metres above the water level.")]
        [SerializeField] private float _waterClearance = 0.15f;
        [Tooltip("Never raise ground that is below the water level inside the water edge of the first filter area. " +
                 "A river bed then keeps its depth and its width.")]
        [SerializeField] private bool _keepUnderwater;

        public float Sigma => _sigma;
        public float MaxChange => _maxChange;
        public float Strength => _strength;
        public StampNoiseSettings HeightNoise => _heightNoise;
        public StampFalloff Falloff => _falloff;
        public StampWaterFilter WaterFilter => _waterFilter;
        public StampWaterFilter SecondWaterFilter => _secondWaterFilter;
        public bool KeepAboveWater => _keepAboveWater;
        public float RimWidth => _rimWidth;
        public float WaterClearance => _waterClearance;
        public bool KeepUnderwater => _keepUnderwater;

        /// <summary>Distance in metres that the blur reads around each sample.</summary>
        public override float Reach => 3f * _sigma;

        public void SetSmoothing(float sigma, float maxChange, float strength)
        {
            _sigma = Mathf.Clamp(sigma, 0.5f, 21f);
            _maxChange = Mathf.Max(0f, maxChange);
            _strength = Mathf.Clamp01(strength);
            MarkChanged();
        }

        public void SetWaterRim(bool keepAboveWater, float rimWidth, float clearance)
        {
            _keepAboveWater = keepAboveWater;
            _rimWidth = Mathf.Max(0f, rimWidth);
            _waterClearance = clearance;
            MarkChanged();
        }

        /// <summary>
        /// Sets whether the smoothing never raises ground that is below the water inside the water edge of the first
        /// filter area.
        /// </summary>
        public void SetKeepUnderwater(bool keepUnderwater)
        {
            _keepUnderwater = keepUnderwater;
            MarkChanged();
        }

        protected override Rect GetArea()
        {
            return GetCullRect(HeightMapping.WorldAligned);
        }

        protected override void Apply(NeighbourhoodTile tile)
        {
            tile.ApplySmooth(this);
        }

        internal override Rect GetCullRect(HeightMapping mapping)
        {
            Rect rect = StampMapping.CullRect(transform, mapping);
            if (_falloff.TryGetAreaRect(out Rect area))
            {
                rect = StampMapping.Intersect(rect, area);
            }
            return _secondWaterFilter.Limit(_waterFilter.Limit(rect));
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(0.5f, 1f, 0.6f, 1f);
            Gizmos.matrix = Matrix4x4.TRS(transform.position, Quaternion.Euler(0f, transform.eulerAngles.y, 0f),
                Vector3.one);
            Vector3 scale = transform.lossyScale;
            Gizmos.DrawWireCube(new Vector3(0f, scale.y * 0.5f, 0f), scale);
        }
    }
}
