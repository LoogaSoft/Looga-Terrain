using System;
using System.Collections.Generic;
using UnityEngine;

namespace LoogaSoft.Terrain.Geology
{
    /// <summary>Wall shape of a rock formation.</summary>
    [Serializable]
    public sealed class RockFormationShape
    {
        [Range(60f, 90f), Tooltip("Angle of the walls, in degrees. 90 = vertical walls.")]
        public float WallAngle = 82f;
        [Min(0.5f), Tooltip("Radius of the rounded edge between the walls and the top, in meters.")]
        public float EdgeRadius = 2.5f;
        [Min(0.5f), Tooltip("Depth of the wall foot below the terrain, in meters. The terrain covers the foot.")]
        public float FootDepth = 3f;
        [Min(0f), Tooltip("Change of the rim height along the outline, in meters.")]
        public float RimVariation = 2f;
        [Min(2f), Tooltip("Typical length of one rim height change along the outline, in meters.")]
        public float RimVariationSize = 30f;
        [Min(0f), Tooltip("Distance along the outline over which the ground height is averaged, in meters. The rim " +
                          "follows the averaged ground, so small bumps in the terrain do not show in the rim.")]
        public float GroundSmoothing = 15f;
    }

    // Builds a rock formation from a closed outline: walls from below the terrain up to the rim, a rounded edge, and
    // the crag top of RockFaceBuilder over the inside. The terrain only gives the ground at the outline. The formation
    // does not need a raised terrain platform.
    internal static class RockFormationBuilder
    {
        private const float TangentReach = 1f;
        private const float RimFieldSpacing = 2f;

        /// <param name="outline">World points of the closed outline, about every 0.1 to 1 m, without a repeated end.</param>
        /// <param name="heights">Formation height above the ground at each outline point.</param>
        /// <param name="shapeGround">Optional ground at the outline, for example the heights before a stamp that reads the
        /// rock. Null = the terrain. The ground blend always reads the terrain.</param>
        public static List<RockFaceStrip> Build(Vector3[] outline, float[] heights, RockFaceSettings settings,
            RockFormationShape shape, TerrainHeightSampler terrain, RockFaceBuilder.Workspace workspace,
            IRockFaceGroundReader shapeGround = null)
        {
            shapeGround ??= terrain;
            List<RockFaceStrip> strips = workspace.Strips;
            strips.Clear();
            int pointCount = outline.Length;
            if (pointCount < 3) return strips;

            // Arc lengths and the turn of the outline. The walls face away from the inside.
            var distances = new float[pointCount + 1];
            float area = 0f;
            for (int index = 0; index < pointCount; index++)
            {
                Vector3 a = outline[index];
                Vector3 b = outline[(index + 1) % pointCount];
                distances[index + 1] = distances[index] + new Vector2(b.x - a.x, b.z - a.z).magnitude;
                area += a.x * b.z - b.x * a.z;
            }
            float perimeter = distances[pointCount];
            int count = Mathf.Max(8, Mathf.RoundToInt(perimeter / settings.ColumnSpacing));
            float spacing = perimeter / count;
            // Counterclockwise in X-Z (positive area) has the inside on the left of the direction.
            float side = area > 0f ? 1f : -1f;

            var columns = new RockFaceBuilder.Column[count];
            var ground = new float[count];
            var pointHeights = new float[count];
            int cursor = 0;
            for (int index = 0; index < count; index++)
            {
                float u = index * spacing;
                Vector3 origin = PointAt(outline, distances, u, ref cursor, out float t, out int segment);
                pointHeights[index] = Mathf.Lerp(heights[segment], heights[(segment + 1) % pointCount], t);
                int ahead = 0;
                int behind = 0;
                Vector3 tangent = PointAt(outline, distances, Mathf.Repeat(u + TangentReach, perimeter), ref ahead,
                                      out _, out _) -
                                  PointAt(outline, distances, Mathf.Repeat(u - TangentReach, perimeter), ref behind,
                                      out _, out _);
                tangent.y = 0f;
                tangent = tangent.sqrMagnitude > 1e-6f ? tangent.normalized : Vector3.forward;
                columns[index] = new RockFaceBuilder.Column
                {
                    Origin = new Vector3(origin.x, 0f, origin.z),
                    Outward = new Vector3(tangent.z, 0f, -tangent.x) * side,
                    U = u,
                    Active = true
                };
                ground[index] = shapeGround.TrySampleHeight(origin.x, origin.z, out float height) ? height : float.NaN;
            }
            FillMissing(ground);

            // The rim follows the averaged ground. The foot reaches below the lowest ground near the column.
            int window = Mathf.Max(1, Mathf.RoundToInt(shape.GroundSmoothing / spacing));
            var rims = new float[count];
            var feet = new float[count];
            float yMinimum = float.MaxValue;
            float yMaximum = float.MinValue;
            for (int index = 0; index < count; index++)
            {
                float sum = 0f;
                float lowest = float.MaxValue;
                for (int offset = -window; offset <= window; offset++)
                {
                    // The window can be wider than a small outline, so wrap more than once.
                    float value = ground[((index + offset) % count + count) % count];
                    sum += value;
                    lowest = Mathf.Min(lowest, value);
                }
                float averaged = sum / (2 * window + 1);
                float variation = shape.RimVariation *
                                  (2f * RockFaceNoise.Perlin(columns[index].U / shape.RimVariationSize + 3.7f,
                                      settings.Seed * 1.31f) - 1f);
                rims[index] = averaged + Mathf.Max(pointHeights[index] + variation, shape.EdgeRadius + 1f);
                feet[index] = Mathf.Min(lowest, ground[index]) - shape.FootDepth;
                yMinimum = Mathf.Min(yMinimum, feet[index]);
                yMaximum = Mathf.Max(yMaximum, rims[index]);
            }

            var detail = new RockFaceDetail(settings, 0f, perimeter, yMinimum, yMaximum);
            float tan = Mathf.Tan(Mathf.Clamp(shape.WallAngle, 60f, 90f) * Mathf.Deg2Rad);
            float batter = tan > 100f ? 0f : 1f / tan;
            float capLength = settings.CapLength;

            // Rows: the wall up to the start of the rounded edge, the edge, then the cap that goes under the top.
            float wallMaximum = 0f;
            for (int index = 0; index < count; index++)
            {
                wallMaximum = Mathf.Max(wallMaximum, rims[index] - shape.EdgeRadius - feet[index]);
            }
            float edgeLength = 0.5f * Mathf.PI * shape.EdgeRadius;
            int wallRows = Mathf.Max(2, Mathf.CeilToInt(wallMaximum / settings.RowSpacing));
            int edgeRows = Mathf.Max(3, Mathf.CeilToInt(edgeLength / settings.RowSpacing));
            int capRows = Mathf.Max(2, Mathf.CeilToInt(capLength / settings.RowSpacing));
            int rows = wallRows + edgeRows + capRows + 1;
            RockFaceStrip strip = workspace.TakeStrip(count, rows, true);
            for (int index = 0; index < count; index++)
            {
                RockFaceBuilder.Column column = columns[index];
                float edgeBase = rims[index] - shape.EdgeRadius;
                float wallTop = (edgeBase - feet[index]) * batter;
                for (int row = 0; row < rows; row++)
                {
                    // Inward distance from the outline and height of the profile, with its outward normal.
                    float inward;
                    float y;
                    Vector2 normal2;
                    float structure;
                    bool wall = row <= wallRows;
                    if (wall)
                    {
                        float share = row / (float)wallRows;
                        y = Mathf.Lerp(feet[index], edgeBase, share);
                        inward = (y - feet[index]) * batter;
                        normal2 = new Vector2(1f, batter).normalized;
                        structure = 1f;
                    }
                    else if (row <= wallRows + edgeRows)
                    {
                        float angle = 0.5f * Mathf.PI * (row - wallRows) / edgeRows;
                        inward = wallTop + shape.EdgeRadius * (1f - Mathf.Cos(angle));
                        y = edgeBase + shape.EdgeRadius * Mathf.Sin(angle);
                        normal2 = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
                        structure = Mathf.Lerp(1f, 0.3f, angle / (0.5f * Mathf.PI));
                    }
                    else
                    {
                        float share = (row - wallRows - edgeRows) / (float)capRows;
                        inward = wallTop + shape.EdgeRadius + share * capLength;
                        // The cap goes down under the crag top, which rises over the same band.
                        y = rims[index] - settings.TuckDepth * RockFaceBuilder.Smooth01(share);
                        normal2 = Vector2.up;
                        structure = 0.3f;
                    }
                    Vector3 normal = (column.Outward * normal2.x + Vector3.up * normal2.y).normalized;
                    float offset = detail.Evaluate(column.U, y, structure, out RockFaceSample sample);
                    Vector3 vertex = column.Origin - column.Outward * inward + Vector3.up * y +
                                     normal * (settings.Clearance + offset);

                    int vertexIndex = strip.Index(index, row);
                    strip.Positions[vertexIndex] = vertex;
                    float groundWeight = wall
                        ? RockFaceBuilder.GroundWeight(vertex, column.Outward, column.U, settings, terrain)
                        : 0f;
                    strip.Colors[vertexIndex] = new Color(sample.Hardness, sample.Cavity, sample.Layer, groundWeight);
                    // The ground blend at the foot shows the terrain beside the formation.
                    Vector3 beside = column.Origin + column.Outward * settings.GroundReach;
                    strip.GroundOffsets[vertexIndex] = new Vector2(beside.x - vertex.x, beside.z - vertex.z);
                    strip.Active[vertexIndex] = true;
                }
                // The crag top starts where the rounded edge turns flat.
                columns[index].Rim = -(wallTop + shape.EdgeRadius);
            }
            RockFaceBuilder.ComputeNormals(strip, columns, 0);

            // The crag top lies on the rim heights, spread smoothly over the inside.
            var rimField = new RimHeightField(columns, rims, Mathf.Max(1, Mathf.RoundToInt(RimFieldSpacing / spacing)));
            var topDetail = new RockFaceDetail(settings, 0f, perimeter, yMinimum,
                yMaximum + settings.CragHeight * (1f + settings.CragEdgeRise) + 5f);
            RockFaceBuilder.BuildTop(columns, count, settings, topDetail, null, rimField, null, workspace);
            return strips;
        }

        internal static Vector3 PointAt(Vector3[] points, float[] distances, float distance, ref int cursor,
            out float t, out int segment)
        {
            int count = points.Length;
            while (cursor < count - 1 && distances[cursor + 1] < distance) cursor++;
            float span = distances[cursor + 1] - distances[cursor];
            t = span > 1e-6f ? Mathf.Clamp01((distance - distances[cursor]) / span) : 0f;
            segment = cursor;
            return Vector3.Lerp(points[cursor], points[(cursor + 1) % count], t);
        }

        // Outline points outside the terrain take the height of the nearest point with a height.
        internal static void FillMissing(float[] values)
        {
            int count = values.Length;
            int known = Array.FindIndex(values, value => !float.IsNaN(value));
            if (known < 0)
            {
                Array.Fill(values, 0f);
                return;
            }
            for (int step = 1; step <= count; step++)
            {
                int index = (known + step) % count;
                if (float.IsNaN(values[index])) values[index] = values[(index - 1 + count) % count];
            }
        }

        // Rim heights spread over the inside by inverse distance weights, so the crag top meets the rim everywhere.
        internal sealed class RimHeightField : IRockFaceGroundReader
        {
            private readonly Vector2[] _points;
            private readonly float[] _heights;

            public RimHeightField(RockFaceBuilder.Column[] columns, float[] rims, int stride)
            {
                int count = (columns.Length + stride - 1) / stride;
                _points = new Vector2[count];
                _heights = new float[count];
                for (int index = 0; index < count; index++)
                {
                    RockFaceBuilder.Column column = columns[index * stride];
                    Vector3 rim = column.Origin + column.Outward * column.Rim;
                    _points[index] = new Vector2(rim.x, rim.z);
                    _heights[index] = rims[index * stride];
                }
            }

            public bool TrySampleHeight(float x, float z, out float height)
            {
                float sum = 0f;
                float weights = 0f;
                for (int index = 0; index < _points.Length; index++)
                {
                    float distance = (_points[index] - new Vector2(x, z)).sqrMagnitude + 1f;
                    float weight = 1f / (distance * distance);
                    sum += _heights[index] * weight;
                    weights += weight;
                }
                height = sum / weights;
                return true;
            }

            public void Dispose()
            {
            }
        }
    }
}
