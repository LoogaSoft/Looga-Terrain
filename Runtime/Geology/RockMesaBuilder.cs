using System;
using System.Collections.Generic;
using UnityEngine;

namespace LoogaSoft.Terrain.Geology
{
    /// <summary>
    /// Strata of a mesa formation: horizontal layers of hard rock that form cliffs and soft rock that forms recesses or
    /// slopes, under a hard caprock.
    /// </summary>
    [Serializable]
    public sealed class RockMesaShape
    {
        [Range(70f, 90f), Tooltip("Angle of the hard cliff layers, in degrees. 90 = vertical cliffs.")]
        public float CliffAngle = 86f;
        [Range(20f, 70f), Tooltip("Angle of the soft layers in the slope zone, in degrees.")]
        public float SlopeAngle = 38f;
        [Range(0f, 0.8f), Tooltip("Share of the formation height, from the ground up, where the soft layers form " +
                                  "slopes. Above it they form recesses between the hard cliff layers.")]
        public float SlopeZone = 0.25f;
        [Tooltip("Smallest and largest thickness of one layer, in meters.")]
        public Vector2 LayerThickness = new(2f, 7f);
        [Range(0f, 1f), Tooltip("Share of soft layers.")]
        public float SoftShare = 0.4f;
        [Min(0f), Tooltip("Depth of a soft layer recess in the cliff zone, in meters. The hard layers above and " +
                          "below form ledges.")]
        public float Recess = 0.8f;
        [Min(0f), Tooltip("Change of each layer face along the outline, in meters. Each layer changes on its own, so " +
                          "the ledges vary in width.")]
        public float LedgeVariation = 0.6f;
        [Min(2f), Tooltip("Typical length of one layer face change along the outline, in meters.")]
        public float LedgeVariationSize = 25f;
        [Range(0f, 1f), Tooltip("Share of the hard cliff layers whose top is a bench: the cliff below steps out.")]
        public float BenchShare = 0.12f;
        [Min(0f), Tooltip("Width of a bench, in meters.")]
        public float BenchWidth = 3f;
        [Min(0f), Tooltip("Thickness of the hard caprock at the top, in meters.")]
        public float CapThickness = 4f;
        [Min(0f), Tooltip("Overhang of the caprock over the layer below it, in meters.")]
        public float CapOverhang = 0.8f;
        [Tooltip("Seed of the layer sequence. The layers stand at world heights, so formations with the same strata " +
                 "settings show the same layers at the same heights, like one rock series across a region.")]
        public int StrataSeed = 1;
    }

    // Builds a mesa or a butte from a closed outline of its top edge. The faces step down and out through horizontal
    // layers from the rim to a foot below the terrain: hard layers form steep cliffs and ledges, soft layers form
    // recesses in the cliff zone and slopes in the slope zone near the ground. A rounded top edge leads to the flat top
    // of RockFaceBuilder over the inside.
    internal static class RockMesaBuilder
    {
        private const float TangentReach = 1f;
        private const float RimFieldSpacing = 2f;
        // Lowest world height of the layer sequence. All formations count their layers from here.
        private const float StrataBase = -500f;
        // Height difference for the face slope at a row, in meters.
        private const float SlopeProbe = 0.25f;

        private readonly struct Layer
        {
            public readonly int Index;
            public readonly float Bottom;
            public readonly float Top;
            public readonly bool Soft;
            public readonly bool Bench;

            public Layer(int index, float bottom, float top, bool soft, bool bench)
            {
                Index = index;
                Bottom = bottom;
                Top = top;
                Soft = soft;
                Bench = bench;
            }
        }

        // One part of a face profile, from its top down: the face at the top, its outward run for each meter down, and
        // the offset of the layer face.
        private struct Segment
        {
            public float Top;
            public float Face;
            public float Run;
            public float Offset;
        }

        /// <param name="outline">World points of the closed top edge, about every 0.1 to 1 m, without a repeated end.
        /// </param>
        /// <param name="heights">Formation height above the ground at each outline point.</param>
        /// <param name="shapeGround">Optional ground, for example the heights before a stamp that reads the rock.
        /// Null = the terrain. The ground blend always reads the terrain.</param>
        public static List<RockFaceStrip> Build(Vector3[] outline, float[] heights, RockFaceSettings settings,
            RockFormationShape shape, RockMesaShape mesa, TerrainHeightSampler terrain,
            RockFaceBuilder.Workspace workspace, IRockFaceGroundReader shapeGround = null)
        {
            shapeGround ??= terrain;
            List<RockFaceStrip> strips = workspace.Strips;
            strips.Clear();
            int pointCount = outline.Length;
            if (pointCount < 3) return strips;

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
                Vector3 origin = RockFormationBuilder.PointAt(outline, distances, u, ref cursor, out float t,
                    out int segment);
                pointHeights[index] = Mathf.Lerp(heights[segment], heights[(segment + 1) % pointCount], t);
                int ahead = 0;
                int behind = 0;
                Vector3 tangent = RockFormationBuilder.PointAt(outline, distances,
                                      Mathf.Repeat(u + TangentReach, perimeter), ref ahead, out _, out _) -
                                  RockFormationBuilder.PointAt(outline, distances,
                                      Mathf.Repeat(u - TangentReach, perimeter), ref behind, out _, out _);
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
            RockFormationBuilder.FillMissing(ground);

            // The rim stands on the averaged ground, so the top stays level over small bumps.
            int window = Mathf.Max(1, Mathf.RoundToInt(shape.GroundSmoothing / spacing));
            var rims = new float[count];
            var bases = new float[count];
            float rimMaximum = float.MinValue;
            float groundMinimum = float.MaxValue;
            for (int index = 0; index < count; index++)
            {
                float sum = 0f;
                for (int offset = -window; offset <= window; offset++)
                {
                    // The window can be wider than a small outline, so wrap more than once.
                    sum += ground[((index + offset) % count + count) % count];
                }
                bases[index] = sum / (2 * window + 1);
                float variation = shape.RimVariation *
                                  (2f * RockFaceNoise.Perlin(columns[index].U / shape.RimVariationSize + 3.7f,
                                      settings.Seed * 1.31f) - 1f);
                rims[index] = bases[index] + Mathf.Max(pointHeights[index] + variation, shape.EdgeRadius + 1f);
                rimMaximum = Mathf.Max(rimMaximum, rims[index]);
                groundMinimum = Mathf.Min(groundMinimum, ground[index]);
            }

            List<Layer> layers = Layers(mesa, rimMaximum + 10f);
            var profiles = new List<Segment>[count];
            var feet = new float[count];
            float footMinimum = float.MaxValue;
            float yMaximum = float.MinValue;
            for (int index = 0; index < count; index++)
            {
                RockFaceBuilder.Column column = columns[index];
                float bottom = groundMinimum - shape.FootDepth - 40f;
                profiles[index] = Profile(layers, mesa, rims[index], bases[index], bottom, column.U);
                // The foot reaches below the ground where the face meets it. Two reads find that place.
                float foot = ground[index];
                for (int pass = 0; pass < 2; pass++)
                {
                    Vector3 point = column.Origin + column.Outward * Offset(profiles[index], foot);
                    if (terrain.TrySampleHeight(point.x, point.z, out float beside))
                    {
                        foot = Mathf.Min(foot, beside);
                    }
                }
                feet[index] = foot - shape.FootDepth;
                footMinimum = Mathf.Min(footMinimum, feet[index]);
                yMaximum = Mathf.Max(yMaximum, rims[index]);
            }

            var detail = new RockFaceDetail(settings, 0f, perimeter, footMinimum, yMaximum);
            float capLength = settings.CapLength;
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
                List<Segment> profile = profiles[index];
                float edgeBase = rims[index] - shape.EdgeRadius;
                float wallTop = Offset(profile, edgeBase);
                float footOffset = Offset(profile, feet[index]);
                for (int row = 0; row < rows; row++)
                {
                    // Outward distance from the outline and height of the profile, with its outward normal.
                    float outward;
                    float y;
                    Vector2 normal2;
                    float structure;
                    bool wall = row <= wallRows;
                    if (wall)
                    {
                        y = Mathf.Lerp(feet[index], edgeBase, row / (float)wallRows);
                        outward = Offset(profile, y);
                        float run = (Offset(profile, y - SlopeProbe) - Offset(profile, y + SlopeProbe)) /
                                    (2f * SlopeProbe);
                        normal2 = new Vector2(1f, Mathf.Clamp(run, -1f, 4f)).normalized;
                        structure = 1f;
                    }
                    else if (row <= wallRows + edgeRows)
                    {
                        float angle = 0.5f * Mathf.PI * (row - wallRows) / edgeRows;
                        outward = wallTop - shape.EdgeRadius * (1f - Mathf.Cos(angle));
                        y = edgeBase + shape.EdgeRadius * Mathf.Sin(angle);
                        normal2 = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
                        structure = Mathf.Lerp(1f, 0.3f, angle / (0.5f * Mathf.PI));
                    }
                    else
                    {
                        float share = (row - wallRows - edgeRows) / (float)capRows;
                        outward = wallTop - shape.EdgeRadius - share * capLength;
                        // The cap goes down under the top, which rises over the same band.
                        y = rims[index] - settings.TuckDepth * RockFaceBuilder.Smooth01(share);
                        normal2 = Vector2.up;
                        structure = 0.3f;
                    }
                    Vector3 normal = (column.Outward * normal2.x + Vector3.up * normal2.y).normalized;
                    float offset = detail.Evaluate(column.U, y, structure, out RockFaceSample sample);
                    Vector3 vertex = column.Origin + column.Outward * outward + Vector3.up * y +
                                     normal * (settings.Clearance + offset);

                    int vertexIndex = strip.Index(index, row);
                    strip.Positions[vertexIndex] = vertex;
                    float groundWeight = wall
                        ? RockFaceBuilder.GroundWeight(vertex, column.Outward, column.U, settings, terrain)
                        : 0f;
                    strip.Colors[vertexIndex] = new Color(sample.Hardness, sample.Cavity, sample.Layer, groundWeight);
                    // The ground blend at the foot shows the terrain beside the formation.
                    Vector3 beside = column.Origin + column.Outward * (footOffset + settings.GroundReach);
                    strip.GroundOffsets[vertexIndex] = new Vector2(beside.x - vertex.x, beside.z - vertex.z);
                    strip.Active[vertexIndex] = true;
                }
                // The top starts where the rounded edge turns flat.
                columns[index].Rim = wallTop - shape.EdgeRadius;
            }
            RockFaceBuilder.ComputeNormals(strip, columns, 0);

            var rimField = new RockFormationBuilder.RimHeightField(columns, rims,
                Mathf.Max(1, Mathf.RoundToInt(RimFieldSpacing / spacing)));
            var topDetail = new RockFaceDetail(settings, 0f, perimeter, footMinimum,
                yMaximum + settings.CragHeight * (1f + settings.CragEdgeRise) + 5f);
            RockFaceBuilder.BuildTop(columns, count, settings, topDetail, null, rimField, null, workspace);
            return strips;
        }

        // The layer sequence from the strata base up to a height. It depends only on the strata settings.
        private static List<Layer> Layers(RockMesaShape mesa, float top)
        {
            float thinnest = Mathf.Max(0.5f, Mathf.Min(mesa.LayerThickness.x, mesa.LayerThickness.y));
            float thickest = Mathf.Max(thinnest, Mathf.Max(mesa.LayerThickness.x, mesa.LayerThickness.y));
            var layers = new List<Layer>();
            float bottom = StrataBase;
            for (int index = 0; bottom < top; index++)
            {
                float thickness = Mathf.Lerp(thinnest, thickest, RockFaceNoise.Hash01(mesa.StrataSeed, index, 0, 11));
                bool soft = RockFaceNoise.Hash01(mesa.StrataSeed, index, 0, 12) < mesa.SoftShare;
                bool bench = !soft && RockFaceNoise.Hash01(mesa.StrataSeed, index, 0, 13) < mesa.BenchShare;
                layers.Add(new Layer(index, bottom, bottom + thickness, soft, bench));
                bottom += thickness;
            }
            return layers;
        }

        // Face profile of one column from the rim down to the bottom height. The caprock stands at the outline. Below
        // it each layer runs at its own angle from the face of the layer above, and its face has its own offset.
        private static List<Segment> Profile(List<Layer> layers, RockMesaShape mesa, float rim, float ground,
            float bottom, float u)
        {
            float cliffRun = Run(mesa.CliffAngle);
            float slopeRun = Run(mesa.SlopeAngle);
            float slopeTop = ground + (rim - ground) * mesa.SlopeZone;
            float capBottom = rim - mesa.CapThickness;
            var profile = new List<Segment>
            {
                new() { Top = rim, Face = 0f, Run = cliffRun, Offset = LayerOffset(mesa, -1, u) }
            };
            float face = (rim - capBottom) * cliffRun;
            bool underCap = true;
            for (int index = layers.Count - 1; index >= 0; index--)
            {
                Layer layer = layers[index];
                if (layer.Bottom >= capBottom) continue;

                float top = Mathf.Min(layer.Top, capBottom);
                bool slope = (top + layer.Bottom) * 0.5f < slopeTop;
                float run = layer.Soft && slope ? slopeRun : cliffRun;
                float offset = LayerOffset(mesa, layer.Index, u);
                if (!slope)
                {
                    // Soft layers recess between the hard ledges. The caprock overhangs the layer below it.
                    if (layer.Soft)
                    {
                        offset -= mesa.Recess;
                    }
                    if (underCap)
                    {
                        offset -= mesa.CapOverhang;
                    }
                    if (layer.Bench && !layer.Soft)
                    {
                        face += mesa.BenchWidth;
                    }
                }
                underCap = false;
                profile.Add(new Segment { Top = top, Face = face, Run = run, Offset = offset });
                face += (top - layer.Bottom) * run;
                if (layer.Bottom < bottom) break;
            }
            return profile;
        }

        // Outward distance of the face at a height. Below the last layer the last run goes on.
        private static float Offset(List<Segment> profile, float y)
        {
            Segment segment = profile[0];
            for (int index = 1; index < profile.Count && profile[index].Top >= y; index++)
            {
                segment = profile[index];
            }
            return segment.Face + (segment.Top - y) * segment.Run + segment.Offset;
        }

        // Smooth change of a layer face along the outline. Each layer has its own noise.
        private static float LayerOffset(RockMesaShape mesa, int layer, float u)
        {
            float noise = RockFaceNoise.Perlin(u / mesa.LedgeVariationSize + layer * 0.37f,
                mesa.StrataSeed * 0.73f + layer * 1.91f);
            return (2f * noise - 1f) * mesa.LedgeVariation;
        }

        // Horizontal run for each meter of height at an angle.
        private static float Run(float degrees)
        {
            return degrees >= 89.9f ? 0f : 1f / Mathf.Tan(degrees * Mathf.Deg2Rad);
        }
    }
}
