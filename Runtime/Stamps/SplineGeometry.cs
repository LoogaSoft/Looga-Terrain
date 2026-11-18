using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Splines;

namespace LoogaSoft.Terrain.Stamps
{
    /// <summary>World-space Bézier control points of one spline curve, in the layout of the distance shader.</summary>
    internal struct WorldCurve
    {
        internal Vector3 P0;
        internal Vector3 P1;
        internal Vector3 P2;
        internal Vector3 P3;

        internal const int Stride = 48;
    }

    /// <summary>Spline helpers shared by spline areas and spline paths.</summary>
    internal static class SplineGeometry
    {
        /// <summary>
        /// Gets the world-space curves of one spline. An open spline also gets a last curve of zero length at its last
        /// knot, as Unity GetCurve gives one, so the end has a round cap.
        /// </summary>
        internal static void GetWorldCurves(Spline spline, Matrix4x4 localToWorld, List<WorldCurve> curves)
        {
            curves.Clear();
            for (int i = 0; i < spline.Count; i++)
            {
                BezierCurve curve = spline.GetCurve(i);
                // Unity bends the last curve of an open spline by the out handle of the last knot and the in handle of the
                // first knot, so it loops past the end. A road end with a 32 m handle got road paint 13 m past the road.
                // The curve is a point at the last knot.
                if (!spline.Closed && i == spline.Count - 1)
                {
                    Vector3 end = localToWorld.MultiplyPoint(curve.P0);
                    curves.Add(new WorldCurve { P0 = end, P1 = end, P2 = end, P3 = end });
                    continue;
                }
                curves.Add(new WorldCurve
                {
                    P0 = localToWorld.MultiplyPoint(curve.P0),
                    P1 = localToWorld.MultiplyPoint(curve.P1),
                    P2 = localToWorld.MultiplyPoint(curve.P2),
                    P3 = localToWorld.MultiplyPoint(curve.P3)
                });
            }
        }

        /// <summary>
        /// Culling rectangle of a spline container, as MicroVerse computes it. Bounds.Expand adds only half of the
        /// padding on each side. Splines after the first get their center and size transformed a second time.
        /// </summary>
        internal static Rect CullRect(SplineContainer container, float padding)
        {
            if (!container || container.Splines.Count == 0 || container.Spline == null)
            {
                return new Rect(-1e9f, -1e9f, 0f, 0f);
            }
            Matrix4x4 localToWorld = container.transform.localToWorldMatrix;
            Bounds bounds = container.Spline.GetBounds((float4x4)localToWorld);
            bounds.Expand(padding);
            IReadOnlyList<Spline> splines = container.Splines;
            for (int i = 1; i < splines.Count; i++)
            {
                Bounds other = splines[i].GetBounds((float4x4)localToWorld);
                other.center = localToWorld.MultiplyPoint(other.center);
                other.size = localToWorld.MultiplyPoint(other.size);
                other.Expand(padding);
                bounds.Encapsulate(new Vector3(other.min.x, bounds.center.y, other.min.z));
                bounds.Encapsulate(new Vector3(other.max.x, bounds.center.y, other.max.z));
            }
            return Rect.MinMaxRect(bounds.min.x, bounds.min.z, bounds.max.x, bounds.max.z);
        }
    }
}
