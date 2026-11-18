using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Splines;

namespace LoogaSoft.Terrain.Geology
{
    /// <summary>Cross-section of a cave passage.</summary>
    public enum CavePassageShape
    {
        [Tooltip("Rectangle with rounded corners and a flat floor, as in a mined gallery.")]
        Mined,
        [Tooltip("Rounded natural passage with a flat floor and rock bumps on the walls and the roof.")]
        Natural
    }

    /// <summary>Optional passage size at one knot of the passage spline.</summary>
    [Serializable]
    public struct CavePassageKnot
    {
        [Tooltip("Use the width and height below at this knot. Off uses the passage values.")]
        public bool Override;
        [Min(0.5f)] public float Width;
        [Min(0.5f)] public float Height;
    }

    /// <summary>
    /// One passage of a <see cref="LoogaCaveSystem"/>: empty space along a spline on the floor centre line. The cross
    /// section is a mined rectangle or a natural rounded shape, both with a flat floor. A wide, tall stretch makes a
    /// chamber. A passage end that lies outside the rock opens the cave there, and the cave system cuts the terrain at
    /// the opening.
    /// </summary>
    [AddComponentMenu("Looga Terrain/Cave Passage")]
    public sealed class LoogaCavePassage : MonoBehaviour
    {
        // Overlap of two segments at an inner joint, in meters.
        private const float JointOverlap = 1f;

        [Header("Path")]
        [Tooltip("Spline along the floor centre of the passage. Empty uses the spline on this object.")]
        [SerializeField] private SplineContainer _spline;

        [Header("Shape")]
        [SerializeField] private CavePassageShape _shape = CavePassageShape.Mined;
        [Tooltip("Height of the rock bumps on the walls and the roof of a natural passage, in meters. The bumps fade " +
                 "out near the floor.")]
        [SerializeField, Min(0f)] private float _bumpHeight = 1.5f;
        [Tooltip("Width of the passage in meters.")]
        [SerializeField, Min(0.5f)] private float _width = 12f;
        [Tooltip("Height from the floor to the roof in meters.")]
        [SerializeField, Min(0.5f)] private float _height = 6f;
        [Tooltip("Radius of the rounded corners of a mined passage in meters. 0 gives sharp, sawn corners.")]
        [SerializeField, Min(0f)] private float _cornerRadius = 0.5f;
        [Tooltip("Optional width and height for each knot of the spline. The values blend between knots.")]
        [SerializeField] private List<CavePassageKnot> _knots = new();

        public SplineContainer Spline => _spline ? _spline : GetComponent<SplineContainer>();
        public CavePassageShape Shape => _shape;
        public float BumpHeight => _bumpHeight;
        public float Width => _width;
        public float Height => _height;
        public float CornerRadius => _cornerRadius;
        public List<CavePassageKnot> Knots => _knots;

        #region Built-in
        private void OnEnable()
        {
            RequestBuild();
        }

        private void OnDisable()
        {
            RequestBuild();
        }

        private void OnValidate()
        {
            RequestBuild();
        }
        #endregion

        /// <summary>Sets the passage size and builds the cave again.</summary>
        public void SetSize(float width, float height, float cornerRadius)
        {
            _width = Mathf.Max(0.5f, width);
            _height = Mathf.Max(0.5f, height);
            _cornerRadius = Mathf.Max(0f, cornerRadius);
            RequestBuild();
        }

        /// <summary>
        /// Adds the straight segments of the passage, about one for each <paramref name="spacing"/> meters of spline.
        /// </summary>
        internal void AppendSegments(List<CaveSegment> segments, float spacing, float margin)
        {
            SplineContainer container = Spline;
            if (!container || container.Spline == null || container.Spline.Count < 2) return;
            UnityEngine.Splines.Spline spline = container.Spline;
            float length = container.CalculateLength();
            if (length < 0.1f) return;
            int count = Mathf.Max(2, Mathf.CeilToInt(length / Mathf.Max(spacing, 0.1f)) + 1);
            int knots = spline.Count;
            var knotT = new float[knots];
            for (int knot = 0; knot < knots; knot++)
            {
                knotT[knot] = spline.ConvertIndexUnit(knot, PathIndexUnit.Knot, PathIndexUnit.Normalized);
            }
            var points = new float3[count];
            var sizes = new float2[count];
            for (int index = 0; index < count; index++)
            {
                float t = index / (float)(count - 1);
                points[index] = container.EvaluatePosition(t);
                sizes[index] = SizeAt(t, knotT);
            }

            for (int index = 0; index < count - 1; index++)
            {
                float2 along = Direction(points[index], points[index + 1]);
                // The end planes of inner joints bisect the two directions, so the pieces meet at a mitre.
                float2 before = index > 0 ? Direction(points[index - 1], points[index]) : along;
                float2 after = index < count - 2 ? Direction(points[index + 1], points[index + 2]) : along;
                bool natural = _shape == CavePassageShape.Natural;
                float bumps = natural ? _bumpHeight : 0f;
                // A natural cross-section bulges to 1.1 times the width.
                float widthScale = natural ? 0.55f : 0.5f;
                float reach = math.max(sizes[index].x, sizes[index + 1].x) * widthScale + JointOverlap + bumps +
                    margin;
                float3 min = math.min(points[index], points[index + 1]) - new float3(reach, margin, reach);
                float3 max = math.max(points[index], points[index + 1]) +
                    new float3(reach, math.max(sizes[index].y, sizes[index + 1].y) + bumps + margin, reach);
                segments.Add(new CaveSegment
                {
                    Start = points[index],
                    End = points[index + 1],
                    StartSize = sizes[index],
                    EndSize = sizes[index + 1],
                    CornerRadius = _cornerRadius,
                    Shape = natural ? CaveShape.Natural : CaveShape.Cut,
                    NoiseAmplitude = bumps,
                    StartPlane = math.normalizesafe(before + along, along),
                    EndPlane = math.normalizesafe(along + after, along),
                    StartOverlap = index > 0 ? JointOverlap : 0f,
                    EndOverlap = index < count - 2 ? JointOverlap : 0f,
                    BoundsMin = min,
                    BoundsMax = max
                });
            }
        }

        private static float2 Direction(float3 from, float3 to)
        {
            return math.normalizesafe(to.xz - from.xz, new float2(0f, 1f));
        }

        // The size between two knots blends smoothly from one knot to the next.
        private float2 SizeAt(float t, float[] knotT)
        {
            if (_knots.Count == 0) return new float2(_width, _height);
            int previous = 0;
            for (int knot = 0; knot < knotT.Length; knot++)
            {
                if (knotT[knot] <= t) previous = knot;
            }
            int next = Mathf.Min(previous + 1, knotT.Length - 1);
            float span = knotT[next] - knotT[previous];
            float share = span > 1e-6f ? Mathf.Clamp01((t - knotT[previous]) / span) : 0f;
            share = share * share * (3f - 2f * share);
            return math.lerp(KnotSize(previous), KnotSize(next), share);
        }

        private float2 KnotSize(int knot)
        {
            if (knot < _knots.Count && _knots[knot].Override)
            {
                return new float2(_knots[knot].Width, _knots[knot].Height);
            }
            return new float2(_width, _height);
        }

        private void RequestBuild()
        {
            LoogaCaveSystem system = GetComponentInParent<LoogaCaveSystem>(true);
            if (system) system.QueueBuild();
        }
    }
}
