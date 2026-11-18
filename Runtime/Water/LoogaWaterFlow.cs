using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Splines;

namespace LoogaSoft.Terrain.Water
{
    /// <summary>
    /// Sets the water flow inside a spline zone. Every <see cref="LoogaWaterBody"/> under the zone takes the flow.
    /// An area zone is a closed outline with one direction. A current zone follows its spline from the first knot
    /// to the last knot. An outlet zone pulls the water toward its spline, such as the crest of a waterfall. An inlet
    /// zone lets water in along its spline, such as a river mouth. Zones with a higher order apply after the others and
    /// win where they overlap. A body with Solved Currents uses the inlets and the outlets as the sources and the
    /// sinks of its current field instead.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [AddComponentMenu("Looga Terrain/Water Flow")]
    public sealed class LoogaWaterFlow : MonoBehaviour
    {
        public enum FlowShape
        {
            // Inside the closed spline the water flows in one direction.
            Area = 0,
            // Along the spline the water follows the spline direction.
            Current = 1,
            // Around the spline the water flows to the nearest point of the spline. The flow reaches the zone speed
            // over the falloff distance. Put the spline just past the end of the water, so all water flows toward it.
            Outlet = 2,
            // Water comes in along the spline at the zone speed and spreads out, for example at a river mouth or below a
            // spillway. Without solved currents the flow goes away from the nearest point of the spline.
            Inlet = 3
        }

        // Distance between the points of the zone outline or current line, in metres.
        private const float SampleSpacing = 2f;
        // A zone changes the flow of water whose surface is within this height of the zone spline, in metres. It does
        // not change water far above or below it, such as the water below a fall.
        private const float HeightReach = 10f;
        // Largest spread angle of a current, in degrees.
        private const float MaximumSpread = 45f;

        private static readonly List<LoogaWaterFlow> ActiveZones = new();

        [Header("Shape")]
        [SerializeField] private SplineContainer _spline;
        [Tooltip("Area: one direction inside the closed spline. Current: the flow follows the spline, first knot to " +
                 "last. Outlet: the flow goes to the nearest point of the spline. Inlet: the flow comes from the spline.")]
        [SerializeField] private FlowShape _shape = FlowShape.Area;
        [Tooltip("Current, Outlet and Inlet: half width of the full-speed band, in metres.")]
        [SerializeField, Min(0f)] private float _width = 10f;
        [Tooltip("Distance past the zone edge over which the zone fades out, in metres.")]
        [SerializeField, Min(0f)] private float _falloff = 20f;

        [Header("Flow")]
        [Tooltip("Area: flow direction in degrees. 0 = world +Z, 90 = world +X.")]
        [SerializeField] private float _direction;
        [Tooltip("Flow speed in metres per second.")]
        [SerializeField, Min(0f)] private float _speed = 0.5f;
        [Tooltip("Current: angle in degrees by which the flow turns out toward the zone edges, so the current fans " +
                 "out like a jet that enters open water. The band widens by the same angle, and the speed falls so " +
                 "that the momentum of the jet stays the same. 0 = the flow follows the spline.")]
        [SerializeField, Range(0f, 45f)] private float _spread;
        [Tooltip("1 = the zone sets the flow. 0.5 = halfway between the flow below and the zone flow.")]
        [SerializeField, Range(0f, 1f)] private float _strength = 1f;
        [Tooltip("Zones with a higher order apply later and win where zones overlap.")]
        [SerializeField] private int _order;

        private readonly List<List<Vector2>> _outlines = new();
        // The sampled lines of a current, an outlet or an inlet, in world XZ.
        private readonly List<List<Vector2>> _lines = new();
        private readonly WaterSegmentGrid _segments = new();
        // Distance along its line to the start of each segment, and the length of each segment, in metres.
        private readonly List<float> _segmentRuns = new();
        private readonly List<float> _segmentLengths = new();
        private readonly List<WaterSplinePoint> _samples = new();
        private Rect _rect;
        // Lowest (x) and highest (y) world height of the zone spline.
        private Vector2 _heights;
        private Rect _appliedRect;
        private bool _prepared;
        private Matrix4x4 _splineMatrix;
        private Hash128 _appliedKnots;

        /// <summary>
        /// A zone changed. The rectangle covers the old and the new zone reach in world XZ. Water bodies in it update
        /// their flow.
        /// </summary>
        public static event Action<Rect> Changed;

        public SplineContainer Spline => _spline;
        public FlowShape Shape => _shape;
        public float Width => _width;
        public float Falloff => _falloff;
        public float Direction => _direction;
        public float Speed => _speed;
        public float Spread => _spread;
        public float Strength => _strength;
        public int Order => _order;

        /// <summary>World XZ lines of the spline for a current, an outlet or an inlet, sampled every 2 metres.</summary>
        internal IReadOnlyList<List<Vector2>> Lines
        {
            get
            {
                Prepare();
                return _lines;
            }
        }

        /// <summary>Enabled zones, in no fixed order.</summary>
        internal static IReadOnlyList<LoogaWaterFlow> Active => ActiveZones;

        #region Built-in
        private void OnEnable()
        {
            ActiveZones.Add(this);
            UnityEngine.Splines.Spline.Changed += OnSplineChanged;
#if UNITY_EDITOR
            UnityEditor.Undo.undoRedoPerformed += OnUndoRedo;
#endif
            NotifyChanged();
        }

        private void OnDisable()
        {
            ActiveZones.Remove(this);
            UnityEngine.Splines.Spline.Changed -= OnSplineChanged;
#if UNITY_EDITOR
            UnityEditor.Undo.undoRedoPerformed -= OnUndoRedo;
#endif
            Changed?.Invoke(_appliedRect);
        }

        private void OnValidate()
        {
            if (isActiveAndEnabled) NotifyChanged();
        }

        // A moved spline object moves the zone.
        private void Update()
        {
            if (_spline && _spline.transform.localToWorldMatrix != _splineMatrix) NotifyChanged();
        }
        #endregion

        #region Settings
        public void SetShape(SplineContainer spline, FlowShape shape, float width, float falloff)
        {
            _spline = spline;
            _shape = shape;
            _width = Mathf.Max(0f, width);
            _falloff = Mathf.Max(0f, falloff);
            NotifyChanged();
        }

        public void SetFlow(float direction, float speed, float strength, int order)
        {
            _direction = direction;
            _speed = Mathf.Max(0f, speed);
            _strength = Mathf.Clamp01(strength);
            _order = order;
            NotifyChanged();
        }

        /// <summary>Sets the spread angle of a current in degrees. 0 = the flow follows the spline.</summary>
        public void SetSpread(float degrees)
        {
            _spread = Mathf.Clamp(degrees, 0f, MaximumSpread);
            NotifyChanged();
        }
        #endregion

        #region Evaluation
        /// <summary>World XZ rectangle where the zone changes the flow.</summary>
        internal Rect Reach
        {
            get
            {
                Prepare();
                return _rect;
            }
        }

        /// <summary>
        /// True when water with a surface from the low to the high world height is near the zone height, so the zone
        /// can change its flow.
        /// </summary>
        internal bool ReachesHeight(float low, float high)
        {
            Prepare();
            return high >= _heights.x - HeightReach && low <= _heights.y + HeightReach;
        }

        /// <summary>Blends the zone flow into the flow at a world XZ point.</summary>
        internal void Apply(Vector2 point, ref Vector2 flow)
        {
            Prepare();
            if (_strength <= 0f || !_rect.Contains(point)) return;

            float weight;
            Vector2 zoneFlow;
            if (_shape == FlowShape.Area)
            {
                float edgeDistance = _segments.TryNearest(point, out float distance, out _, out _) ? distance : float.MaxValue;
                weight = Inside(point) ? 1f : Fade(edgeDistance);
                zoneFlow = DirectionVector(_direction) * _speed;
            }
            else
            {
                if (!_segments.TryNearest(point, out float distance, out int segment, out float along)) return;

                Vector2 direction = _shape switch
                {
                    FlowShape.Outlet => (_segments.Point(segment, along) - point).normalized,
                    FlowShape.Inlet => (point - _segments.Point(segment, along)).normalized,
                    _ => _segments.Direction(segment)
                };
                float width = _width;
                float speed = _speed;
                if (_shape == FlowShape.Current && _spread > 0f)
                {
                    SpreadCurrent(point, segment, along, ref direction, ref width, ref speed);
                }
                weight = Fade(distance - width);
                zoneFlow = direction * speed;
            }
            if (weight <= 0f) return;

            flow = Vector2.Lerp(flow, zoneFlow, weight * _strength);
        }

        // A current that fans out like a jet: the flow comes from a point behind the line start, so the band edge leaves
        // the start at the spread angle and the flow turns out more and more toward the edges. The band widens by the
        // same angle. A jet keeps its momentum (speed squared times width), so the speed falls as the band widens.
        private void SpreadCurrent(Vector2 point, int segment, float along, ref Vector2 direction, ref float width,
            ref float speed)
        {
            float tangent = Mathf.Tan(_spread * Mathf.Deg2Rad);
            float startWidth = Mathf.Max(_width, 1f);
            float run = _segmentRuns[segment] + along * _segmentLengths[segment];
            Vector2 offset = point - _segments.Point(segment, along);
            // Positive to the left of the flow, which is the positive turn in world XZ.
            float side = direction.x * offset.y - direction.y * offset.x;
            float angle = Mathf.Atan2(side, startWidth / tangent + run);
            float sine = Mathf.Sin(angle);
            float cosine = Mathf.Cos(angle);
            direction = new Vector2(cosine * direction.x - sine * direction.y, sine * direction.x + cosine * direction.y);
            width = _width + run * tangent;
            speed *= Mathf.Sqrt(startWidth / (startWidth + run * tangent));
        }

        /// <summary>World XZ direction of an angle in degrees. 0 = world +Z, 90 = world +X.</summary>
        internal static Vector2 DirectionVector(float degrees)
        {
            float radians = degrees * Mathf.Deg2Rad;
            return new Vector2(Mathf.Sin(radians), Mathf.Cos(radians));
        }

        private float Fade(float distance)
        {
            if (distance <= 0f) return 1f;
            if (_falloff <= 0f || distance >= _falloff) return 0f;

            float t = 1f - distance / _falloff;
            return t * t * (3f - 2f * t);
        }

        // Even-odd test against the zone outlines.
        private bool Inside(Vector2 point)
        {
            bool inside = false;
            foreach (List<Vector2> outline in _outlines)
            {
                for (int index = 0, previous = outline.Count - 1; index < outline.Count; previous = index++)
                {
                    Vector2 a = outline[index];
                    Vector2 b = outline[previous];
                    if ((a.y > point.y) == (b.y > point.y)) continue;
                    if (point.x < a.x + (point.y - a.y) / (b.y - a.y) * (b.x - a.x)) inside = !inside;
                }
            }
            return inside;
        }

        // Adds the segments of a line and the distance along the line to each segment start.
        private void AddSegments(List<Vector2> points, bool closed)
        {
            _segments.AddPolyline(points, closed);
            int count = closed ? points.Count : points.Count - 1;
            float run = 0f;
            for (int index = 0; index < count; index++)
            {
                float length = Vector2.Distance(points[index], points[(index + 1) % points.Count]);
                _segmentRuns.Add(run);
                _segmentLengths.Add(length);
                run += length;
            }
        }

        private void Prepare()
        {
            if (_prepared) return;

            _prepared = true;
            _outlines.Clear();
            _lines.Clear();
            _segmentRuns.Clear();
            _segmentLengths.Clear();
            _rect = default;
            float reach = _falloff + (_shape == FlowShape.Area ? 0f : _width);
            _segments.Clear(Mathf.Max(reach, 8f), reach);
            if (!_spline) return;

            _splineMatrix = _spline.transform.localToWorldMatrix;
            Vector2 min = new(float.MaxValue, float.MaxValue);
            Vector2 max = new(float.MinValue, float.MinValue);
            _heights = new Vector2(float.MaxValue, float.MinValue);
            var closedLines = new List<bool>();
            float longest = 0f;
            foreach (UnityEngine.Splines.Spline spline in _spline.Splines)
            {
                WaterSplines.Sample(spline, _splineMatrix, SampleSpacing, _samples);
                if (_samples.Count < 2) continue;

                var points = new List<Vector2>(_samples.Count);
                float length = 0f;
                foreach (WaterSplinePoint sample in _samples)
                {
                    Vector2 point = new(sample.Position.x, sample.Position.z);
                    _heights = new Vector2(Mathf.Min(_heights.x, sample.Position.y),
                        Mathf.Max(_heights.y, sample.Position.y));
                    if (points.Count > 0) length += Vector2.Distance(points[points.Count - 1], point);
                    points.Add(point);
                    min = Vector2.Min(min, point);
                    max = Vector2.Max(max, point);
                }
                longest = Mathf.Max(longest, length);
                // An area outline is always closed. A current follows the spline as it is.
                closedLines.Add(_shape == FlowShape.Area || spline.Closed);
                if (_shape == FlowShape.Area)
                {
                    _outlines.Add(points);
                }
                else
                {
                    _lines.Add(points);
                }
            }
            // A spread current widens along its line, so it reaches further at the line end.
            if (_shape == FlowShape.Current && _spread > 0f)
            {
                reach += longest * Mathf.Tan(_spread * Mathf.Deg2Rad);
                _segments.Clear(Mathf.Max(reach, 8f), reach);
            }
            List<List<Vector2>> polylines = _shape == FlowShape.Area ? _outlines : _lines;
            for (int line = 0; line < polylines.Count; line++)
            {
                AddSegments(polylines[line], closedLines[line]);
            }
            if (min.x > max.x) return;

            _rect = Rect.MinMaxRect(min.x - reach, min.y - reach, max.x + reach, max.y + reach);
        }
        #endregion

        #region Changes
        private void OnSplineChanged(UnityEngine.Splines.Spline spline, int knotIndex, SplineModification modification)
        {
            if (!_spline) return;

            foreach (UnityEngine.Splines.Spline own in _spline.Splines)
            {
                if (own != spline) continue;

                NotifyChanged();
                return;
            }
        }

#if UNITY_EDITOR
        // An undo can change the spline knots without a spline event. Undone settings call OnValidate.
        private void OnUndoRedo()
        {
            if (GetKnots() != _appliedKnots) NotifyChanged();
        }
#endif

        private Hash128 GetKnots()
        {
            var hash = new Hash128();
            WaterSplines.AppendKnots(ref hash, _spline);
            return hash;
        }

        private void NotifyChanged()
        {
            _appliedKnots = GetKnots();
            Rect previous = _appliedRect;
            _prepared = false;
            Prepare();
            _appliedRect = _rect;
            Rect changed = previous.width > 0f
                ? Rect.MinMaxRect(Mathf.Min(previous.xMin, _rect.xMin), Mathf.Min(previous.yMin, _rect.yMin),
                    Mathf.Max(previous.xMax, _rect.xMax), Mathf.Max(previous.yMax, _rect.yMax))
                : _rect;
            Changed?.Invoke(changed);
        }
        #endregion
    }
}
