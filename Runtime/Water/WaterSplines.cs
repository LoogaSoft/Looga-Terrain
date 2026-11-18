using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Splines;

namespace LoogaSoft.Terrain.Water
{
    /// <summary>One point on a sampled spline, in world space.</summary>
    internal struct WaterSplinePoint
    {
        internal Vector3 Position;
        // Knot position: curve index plus the fraction along the curve. Width points use the same unit.
        internal float Knot;
        // Exact spline direction at the two ends of an open spline, zero at the other points.
        internal Vector3 Tangent;
    }

    /// <summary>Samples spline curves into polylines at a near-constant distance between points.</summary>
    internal static class WaterSplines
    {
        // Dense samples per output sample, used to find equal distances along a curve.
        private const int DenseSamplesPerPoint = 8;

        private static readonly List<float> DenseLengths = new();

        /// <summary>Adds the knots of each spline of the container to a hash, so a change check can compare them.</summary>
        internal static void AppendKnots(ref Hash128 hash, SplineContainer container)
        {
            if (!container)
            {
                hash.Append(-1);
                return;
            }
            foreach (Spline spline in container.Splines)
            {
                hash.Append(spline.Count);
                hash.Append(spline.Closed ? 1 : 0);
                for (int index = 0; index < spline.Count; index++)
                {
                    BezierKnot knot = spline[index];
                    hash.Append(ref knot);
                    hash.Append((int)spline.GetTangentMode(index));
                }
            }
        }

        /// <summary>
        /// Samples one spline in world space. A closed spline does not repeat its first point at the end. An open spline
        /// ends with its last knot.
        /// </summary>
        internal static void Sample(Spline spline, Matrix4x4 localToWorld, float spacing,
            List<WaterSplinePoint> points)
        {
            points.Clear();
            if (spline == null || spline.Count < 2) return;

            spacing = Mathf.Max(spacing, 0.05f);
            int curveCount = spline.Closed ? spline.Count : spline.Count - 1;
            for (int curveIndex = 0; curveIndex < curveCount; curveIndex++)
            {
                BezierCurve curve = ToWorld(spline.GetCurve(curveIndex), localToWorld);
                float length = CurveUtility.ApproximateLength(curve);
                int count = Mathf.Max(1, Mathf.CeilToInt(length / spacing));
                AddCurve(curve, curveIndex, count, points);
            }
            if (!spline.Closed)
            {
                BezierCurve first = ToWorld(spline.GetCurve(0), localToWorld);
                BezierCurve last = ToWorld(spline.GetCurve(curveCount - 1), localToWorld);
                WaterSplinePoint start = points[0];
                start.Tangent = first.P1 - first.P0;
                points[0] = start;
                points.Add(new WaterSplinePoint { Position = last.P3, Knot = curveCount, Tangent = last.P3 - last.P2 });
            }
        }

        /// <summary>
        /// Linear width at a knot position. Points hold the knot position in x and the width in y, sorted by x.
        /// Without points the result is the fallback.
        /// </summary>
        internal static float Width(IReadOnlyList<Vector2> points, float knot, float fallback)
        {
            if (points == null || points.Count == 0) return fallback;
            if (knot <= points[0].x) return points[0].y;
            for (int index = 1; index < points.Count; index++)
            {
                Vector2 next = points[index];
                if (knot > next.x) continue;

                Vector2 previous = points[index - 1];
                float span = next.x - previous.x;
                return span <= 1e-6f ? next.y : Mathf.Lerp(previous.y, next.y, (knot - previous.x) / span);
            }
            return points[points.Count - 1].y;
        }

        // Adds the curve start and count - 1 more points at equal distances. The curve end is the next curve start.
        private static void AddCurve(BezierCurve curve, int curveIndex, int count, List<WaterSplinePoint> points)
        {
            int denseCount = count * DenseSamplesPerPoint;
            DenseLengths.Clear();
            DenseLengths.Add(0f);
            Vector3 previous = curve.P0;
            for (int index = 1; index <= denseCount; index++)
            {
                Vector3 position = CurveUtility.EvaluatePosition(curve, index / (float)denseCount);
                DenseLengths.Add(DenseLengths[index - 1] + Vector3.Distance(previous, position));
                previous = position;
            }

            float total = DenseLengths[denseCount];
            int dense = 0;
            for (int index = 0; index < count; index++)
            {
                float target = total * index / count;
                while (dense < denseCount - 1 && DenseLengths[dense + 1] < target)
                {
                    dense++;
                }
                float span = DenseLengths[dense + 1] - DenseLengths[dense];
                float fraction = span > 1e-6f ? (target - DenseLengths[dense]) / span : 0f;
                float t = (dense + Mathf.Clamp01(fraction)) / denseCount;
                points.Add(new WaterSplinePoint
                {
                    Position = CurveUtility.EvaluatePosition(curve, t),
                    Knot = curveIndex + t
                });
            }
        }

        private static BezierCurve ToWorld(BezierCurve curve, Matrix4x4 localToWorld)
        {
            return new BezierCurve(localToWorld.MultiplyPoint3x4(curve.P0), localToWorld.MultiplyPoint3x4(curve.P1),
                localToWorld.MultiplyPoint3x4(curve.P2), localToWorld.MultiplyPoint3x4(curve.P3));
        }
    }
}
