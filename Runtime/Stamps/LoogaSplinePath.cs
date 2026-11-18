using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Splines;

namespace LoogaSoft.Terrain.Stamps
{
    /// <summary>
    /// Sets terrain height along splines: the spline height inside the width, then an embankment that blends back to
    /// the previous height over the smoothness distance. A parent <see cref="LoogaStampWorld"/> applies it in hierarchy
    /// order with the height stamps.
    /// </summary>
    [AddComponentMenu("Looga/Stamps/Spline Path")]
    public sealed class LoogaSplinePath : LoogaHeightModifier
    {
        /// <summary>Width along one spline, in knot units (curve index plus curve fraction).</summary>
        [Serializable]
        public sealed class SplineWidths
        {
            [Tooltip("x = knot position, y = extra width in metres.")]
            [SerializeField] private List<Vector2> _points = new List<Vector2>();

            public IReadOnlyList<Vector2> Points => _points;

            public void Set(List<Vector2> points)
            {
                _points = points ?? new List<Vector2>();
                _points.Sort((a, b) => a.x.CompareTo(b.x));
            }
        }

        [Header("Spline")]
        [SerializeField] private SplineContainer _spline;
        [Tooltip("Extra width for each spline in the container, in metres. Missing entries give no extra width.")]
        [SerializeField] private List<SplineWidths> _splineWidths = new List<SplineWidths>();
        [Tooltip("Shape between two width points.")]
        [SerializeField] private FalloffEasing _widthEasing = FalloffEasing.Linear;
        [Tooltip("Noise added to the width. Input is the world position in units of 100 m.")]
        [SerializeField] private StampNoiseSettings _widthNoise = new StampNoiseSettings();

        [Header("Height")]
        [Tooltip("Override = path height. Max and Min keep the higher or lower height.")]
        [SerializeField] private HeightCombineMode _mode = HeightCombineMode.Override;
        [Tooltip("Full-effect width added to the spline width, in metres.")]
        [SerializeField, Min(0f)] private float _width = 1f;
        [Tooltip("Embankment width in metres.")]
        [SerializeField, Min(0f)] private float _smoothness = 2f;
        [Tooltip("Depth below the spline, in metres. Positive values cut down.")]
        [SerializeField] private float _trench;
        [SerializeField] private bool _useTrenchCurve;
        [Tooltip("Trench depth over the distance from the spline (0) to the embankment end (1).")]
        [SerializeField] private AnimationCurve _trenchCurve = AnimationCurve.Constant(0f, 1f, 0f);
        [SerializeField] private FalloffEasing _embankmentEasing = FalloffEasing.Linear;
        [Tooltip("Lowers the embankment middle. Input is the terrain noise position (1 unit = 1000 m).")]
        [SerializeField] private StampNoiseSettings _embankmentNoise = new StampNoiseSettings();
        [Tooltip("Blends the heights of splines that overlap, such as roads at a junction, in metres. Each spline " +
            "keeps its height on its centre line, and its weight falls by e for each blend distance from it. " +
            "0 = the nearest spline sets the height.")]
        [SerializeField, Min(0f)] private float _overlapBlend;
        [Tooltip("Extra level width beside the path where it stands above the ground (fill), in metres, before the " +
            "embankment starts. It leaves room for a guard rail or a retaining wall. The extra width grows over the " +
            "first metre of fill, so cut and level stretches keep the plain width.")]
        [SerializeField, Min(0f)] private float _fillShoulder;
        [Tooltip("Ends the path square at the first and the last spline point: past them, seen along the spline, the " +
            "path changes nothing (no round cap and no embankment).")]
        [SerializeField] private bool _squareEnds;

        [Header("Distance Field")]
        [Tooltip("Distance field samples across one terrain tile.")]
        [SerializeField] private int _resolution = 2048;
        [Tooltip("Straight segments for each spline curve in the distance search.")]
        [SerializeField] private int _segmentsPerCurve = 512;
        [Tooltip("Search distance beyond the spline width, in metres. The search stops at twice this distance.")]
        [SerializeField, Min(0f)] private float _searchDistance = 6f;
        [Tooltip("Padding of the culling bounds. Bounds.Expand rules apply: half of it on each side.")]
        [SerializeField, Min(0f)] private float _boundsPadding = 2f;

        private Texture2D _trenchTexture;
        private bool _trenchCurveChanged;

        public SplineContainer Spline => _spline;
        public IReadOnlyList<SplineWidths> Widths => _splineWidths;
        public FalloffEasing WidthEasing => _widthEasing;
        public StampNoiseSettings WidthNoise => _widthNoise;
        public HeightCombineMode Mode => _mode;
        public float Width => _width;
        public float Smoothness => _smoothness;
        public float Trench => _trench;
        public bool UseTrenchCurve => _useTrenchCurve;
        public FalloffEasing EmbankmentEasing => _embankmentEasing;
        public StampNoiseSettings EmbankmentNoise => _embankmentNoise;
        public float OverlapBlend => _overlapBlend;
        public float FillShoulder => _fillShoulder;
        public bool SquareEnds => _squareEnds;

        /// <summary>Fill height in metres at which the fill shoulder reaches its full width.</summary>
        internal const float FillRamp = 1f;
        public int Resolution => _resolution;
        public int SegmentsPerCurve => _segmentsPerCurve;
        public float SearchDistance => _searchDistance;

        /// <summary>Sets the spline and its widths. Converters and tests use it.</summary>
        public void SetSpline(SplineContainer spline, List<SplineWidths> widths, FalloffEasing widthEasing)
        {
            _spline = spline;
            _splineWidths = widths ?? new List<SplineWidths>();
            _widthEasing = widthEasing;
            MarkChanged();
        }

        /// <summary>Sets the height settings. Converters and tests use it.</summary>
        public void SetHeight(HeightCombineMode mode, float width, float smoothness, float trench,
            AnimationCurve trenchCurve, FalloffEasing embankmentEasing)
        {
            _mode = mode;
            _width = width;
            _smoothness = smoothness;
            _trench = trench;
            _useTrenchCurve = trenchCurve != null;
            if (trenchCurve != null)
            {
                _trenchCurve = new AnimationCurve(trenchCurve.keys);
            }
            _embankmentEasing = embankmentEasing;
            ReleaseTrenchTexture();
            MarkChanged();
        }

        /// <summary>Sets the overlap blend distance in metres. 0 = the nearest spline sets the height.</summary>
        public void SetOverlapBlend(float distance)
        {
            _overlapBlend = Mathf.Max(0f, distance);
            MarkChanged();
        }

        /// <summary>Sets the extra level width beside the path where it stands above the ground, in metres.</summary>
        public void SetFillShoulder(float width)
        {
            _fillShoulder = Mathf.Max(0f, width);
            MarkChanged();
        }

        /// <summary>Sets whether the path ends square at its first and last spline points.</summary>
        public void SetSquareEnds(bool square)
        {
            _squareEnds = square;
            MarkChanged();
        }

        /// <summary>Sets the distance field settings. Converters and tests use it.</summary>
        public void SetDistanceField(int resolution, int segmentsPerCurve, float searchDistance, float boundsPadding)
        {
            _resolution = Mathf.Clamp(resolution, 16, 4096);
            _segmentsPerCurve = Mathf.Max(2, segmentsPerCurve);
            _searchDistance = searchDistance;
            _boundsPadding = boundsPadding;
            MarkChanged();
        }

        internal override Rect GetCullRect(HeightMapping mapping)
        {
            return SplineGeometry.CullRect(_spline, _boundsPadding);
        }

        /// <summary>Largest extra width of one spline.</summary>
        internal float GetMaximumWidth(int splineIndex)
        {
            if (splineIndex >= _splineWidths.Count || _splineWidths[splineIndex] == null) return 0f;
            float maximum = 0f;
            foreach (Vector2 point in _splineWidths[splineIndex].Points)
            {
                maximum = Mathf.Max(maximum, point.y);
            }
            return maximum;
        }

        /// <summary>
        /// Trench curve as a 128 x 1 texture: sample i holds the curve at i / 128 (MicroVerse layout).
        /// </summary>
        internal Texture2D GetTrenchTexture()
        {
            if (_trenchCurveChanged)
            {
                ReleaseTrenchTexture();
                _trenchCurveChanged = false;
            }
            if (_trenchTexture) return _trenchTexture;
            _trenchTexture = new Texture2D(128, 1, TextureFormat.RFloat, false, true)
            {
                name = "Looga Trench Curve " + name,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.DontSave
            };
            var values = new float[128];
            for (int i = 0; i < values.Length; i++)
            {
                values[i] = _trenchCurve.Evaluate(i / 128f);
            }
            _trenchTexture.SetPixelData(values, 0);
            _trenchTexture.Apply(false, false);
            return _trenchTexture;
        }

        // Unity does not allow object destruction during OnValidate. GetTrenchTexture rebuilds the texture later.
        protected override void OnValidate()
        {
            base.OnValidate();
            _trenchCurveChanged = true;
        }

        private void OnDestroy()
        {
            ReleaseTrenchTexture();
        }

        private void ReleaseTrenchTexture()
        {
            if (_trenchTexture)
            {
                DestroyImmediate(_trenchTexture);
            }
            _trenchTexture = null;
        }
    }
}
