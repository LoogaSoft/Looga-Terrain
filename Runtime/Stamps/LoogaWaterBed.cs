using UnityEngine;

namespace LoogaSoft.Terrain.Stamps
{
    /// <summary>
    /// Shapes the ground under the water of a <see cref="LoogaWaterArea"/> from the distance to the water edge. Inside
    /// the water the bed starts at the water level and goes down toward the maximum depth on a smooth curve, so the
    /// shore has no crease. Outside the water a beach band lowers ground to the same slope. Ground well above the
    /// water inside the edge, or well above the beach slope beside it, keeps its height, so cliffs and rocks stay.
    /// Ground far below the bed, such as a gorge under a waterfall crest, also stays. The water level comes from the
    /// nearest water edge, so one bed serves water bodies at different levels. The falloff can limit the bed further.
    /// </summary>
    /// <remarks>
    /// Depth at x metres inside the edge = max depth * (1 - exp(-3 x / shelf width)): 95 % of the depth at the shelf
    /// width. The beach slope is the slope of the bed at the edge. The area max distance must be at least twice the
    /// shelf width, so the bed is at its full depth where the field ends.
    /// </remarks>
    [AddComponentMenu("Looga/Stamps/Water Bed")]
    public sealed class LoogaWaterBed : LoogaHeightModifier
    {
        [Header("Water")]
        [SerializeField] private LoogaWaterArea _area;

        [Header("Bed")]
        [Tooltip("Depth far from the shore, in metres.")]
        [SerializeField, Min(0f)] private float _maxDepth = 1.2f;
        [Tooltip("Distance from the water edge where the bed reaches 95 % of the maximum depth, in metres.")]
        [SerializeField, Min(0.5f)] private float _shelfWidth = 60f;
        [Tooltip("Noise on the bed depth, in metres. It grows with the depth, so the water edge stays in place.")]
        [SerializeField] private StampNoiseSettings _bedNoise = new StampNoiseSettings();

        [Header("Drop-off")]
        [Tooltip("Extra depth beyond the shelf, in metres. 0 = no drop-off.")]
        [SerializeField, Min(0f)] private float _dropDepth;
        [Tooltip("Distance from the water edge where the drop-off starts, in metres.")]
        [SerializeField, Min(0f)] private float _dropStart = 80f;
        [Tooltip("Distance over which the drop-off reaches its full depth, in metres. Wide values give a gentle slope.")]
        [SerializeField, Min(1f)] private float _dropWidth = 300f;
        [Tooltip("Noise that moves the drop-off start, in metres. It gives the shallow zone a varied width.")]
        [SerializeField] private StampNoiseSettings _dropNoise = new StampNoiseSettings();
        [Tooltip("Optional. The drop-off applies only inside this area, so authored shallows, such as the approach to a " +
                 "waterfall crest, stay shallow.")]
        [SerializeField] private LoogaSplineArea _dropArea;
        [Tooltip("Distance inside the drop area over which the drop-off fades in, in metres.")]
        [SerializeField, Min(1f)] private float _dropAreaFade = 300f;

        [Header("Beach")]
        [Tooltip("Width of the band outside the water edge where ground goes down to the beach slope, in metres. " +
                 "0 = no change outside the water.")]
        [SerializeField, Min(0f)] private float _beachWidth = 12f;
        [Tooltip("Ground higher than this above the water (inside the edge) or above the beach slope (beside it) keeps " +
                 "its height, in metres. The bed fades in over the last quarter. Cliffs and rocks stay.")]
        [SerializeField, Min(0f)] private float _beachMaxHeight = 3f;
        [Tooltip("Ground inside the edge lower than this below the bed keeps its height, in metres. A gorge stays.")]
        [SerializeField, Min(0f)] private float _keepDepth = 25f;

        [Header("Weight")]
        [SerializeField, Range(0f, 1f)] private float _strength = 1f;
        [Tooltip("Limits the bed. Global = everywhere the water field reaches. Spline Area keeps it inside an " +
                 "authored floor area.")]
        [SerializeField] private StampFalloff _falloff = CreateGlobalFalloff();

        public LoogaWaterArea Area => _area;
        public float MaxDepth => _maxDepth;
        public float ShelfWidth => _shelfWidth;
        public StampNoiseSettings BedNoise => _bedNoise;
        public float DropDepth => _dropDepth;
        public float DropStart => _dropStart;
        public float DropWidth => _dropWidth;
        public StampNoiseSettings DropNoise => _dropNoise;
        public LoogaSplineArea DropArea => _dropArea;
        public float DropAreaFade => _dropAreaFade;
        public float BeachWidth => _beachWidth;
        public float KeepDepth => _keepDepth;

        /// <summary>
        /// Distance from the water edge where the bed reaches its full depth, in metres. The area max distance must be at
        /// least this, so the bed does not step where the field ends.
        /// </summary>
        public float FullDepthDistance => Mathf.Max(2f * _shelfWidth,
            _dropDepth > 0f ? _dropStart + _dropWidth + Mathf.Abs(_dropNoise.Amplitude) : 0f);
        public float BeachMaxHeight => _beachMaxHeight;
        public float Strength => _strength;

        /// <summary>Slope of the bed at the water edge, in metres down per metre inside.</summary>
        public float EdgeSlope => _maxDepth * 3f / Mathf.Max(0.5f, _shelfWidth);

        public StampFalloff Falloff => _falloff;

        public void Set(LoogaWaterArea area, float maxDepth, float shelfWidth, float beachWidth, float beachMaxHeight)
        {
            _area = area;
            _maxDepth = Mathf.Max(0f, maxDepth);
            _shelfWidth = Mathf.Max(0.5f, shelfWidth);
            _beachWidth = Mathf.Max(0f, beachWidth);
            _beachMaxHeight = Mathf.Max(0f, beachMaxHeight);
            MarkChanged();
        }

        /// <summary>Sets the drop-off beyond the shelf. Depth 0 removes it.</summary>
        public void SetDropOff(float depth, float start, float width)
        {
            _dropDepth = Mathf.Max(0f, depth);
            _dropStart = Mathf.Max(0f, start);
            _dropWidth = Mathf.Max(1f, width);
            MarkChanged();
        }

        /// <summary>Limits the drop-off to an area. Null removes the limit.</summary>
        public void SetDropArea(LoogaSplineArea area, float fade)
        {
            _dropArea = area;
            _dropAreaFade = Mathf.Max(1f, fade);
            MarkChanged();
        }

        /// <summary>Sets the depth below the bed from which lower ground keeps its height, in metres.</summary>
        public void SetKeepDepth(float depth)
        {
            _keepDepth = Mathf.Max(0f, depth);
            MarkChanged();
        }

        public void SetStrength(float strength)
        {
            _strength = Mathf.Clamp01(strength);
            MarkChanged();
        }

        /// <summary>World XZ rectangle of the water area sources plus the field reach, inside the falloff area.</summary>
        internal override Rect GetCullRect(HeightMapping mapping)
        {
            if (!_area) return new Rect(transform.position.x, transform.position.z, 0f, 0f);

            Rect rect = _area.GetCullRect();
            return _falloff.TryGetAreaRect(out Rect area) ? StampMapping.Intersect(rect, area) : rect;
        }

        private static StampFalloff CreateGlobalFalloff()
        {
            var falloff = new StampFalloff();
            falloff.SetShape(FalloffShape.Global, Vector2.one, FalloffEasing.Linear);
            return falloff;
        }
    }
}
