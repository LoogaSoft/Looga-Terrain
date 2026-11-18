using System.Collections.Generic;
using LoogaSoft.Terrain.Geology;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;

namespace LoogaSoft.Terrain.Tests
{
    // A turned cube has a known answer, so these tests do not depend on the mesher's own numbers.
    public sealed class RockDualContourTests
    {
        private const int GridSize = 64;
        private const float HalfSize = 18f;
        private static readonly quaternion Turn = quaternion.EulerXYZ(0.45f, 0.7f, 0.2f);

        /// <summary>Quality of one mesh of the cube, in grid cells.</summary>
        public struct CubeResult
        {
            public int Triangles;
            public int OpenEdges;
            public int NonManifoldEdges;
            public float VolumeRatio;
            public float MaximumDistance;
            public float MeanCornerGap;
            public float MeanEdgeGap;

            public override string ToString()
            {
                return $"{Triangles} tris, open {OpenEdges}, non-manifold {NonManifoldEdges}, " +
                       $"volume x{VolumeRatio:F4}, max distance {MaximumDistance:F3}, " +
                       $"corner gap {MeanCornerGap:F3}, edge gap {MeanEdgeGap:F3} cells";
            }
        }

        [Test]
        public void FeaturePlacementKeepsCubeCorners()
        {
            CubeResult mean = MeasureCube(RockVertexPlacement.Mean, false);
            CubeResult feature = MeasureCube(RockVertexPlacement.Feature, false);
            Assert.AreEqual(0, feature.OpenEdges);
            // A sharp edge can cut a cell on a diagonal, and one vertex then serves two sheets. 9 edges were measured.
            Assert.Less(feature.NonManifoldEdges, 32);
            Assert.That(feature.VolumeRatio, Is.EqualTo(1f).Within(0.01f));
            Assert.Less(feature.MeanCornerGap, mean.MeanCornerGap * 0.5f);
        }

        /// <summary>Reports the cube result of each vertex placement.</summary>
        public static string MeasureAll()
        {
            return "Mean: " + MeasureCube(RockVertexPlacement.Mean, false) +
                   " | Feature: " + MeasureCube(RockVertexPlacement.Feature, false) +
                   " | Feature, exact field: " + MeasureCube(RockVertexPlacement.Feature, true);
        }

        // Meshes the cube and measures the result against the analytic shape.
        private static CubeResult MeasureCube(RockVertexPlacement placement, bool exactField)
        {
            var size = new int3(GridSize);
            var field = new NativeArray<float>(GridSize * GridSize * GridSize, Allocator.TempJob);
            int sample = 0;
            for (int z = 0; z < GridSize; z++)
            {
                for (int y = 0; y < GridSize; y++)
                {
                    for (int x = 0; x < GridSize; x++, sample++)
                    {
                        var point = new float3(x, y, z);
                        field[sample] = exactField ? CubeDistance(point) : CubePlanes(point);
                    }
                }
            }
            RockDualContour.Build(field, size, placement, out NativeList<float3> positions,
                out NativeList<int> indices);
            CubeResult result = Measure(positions, indices);
            positions.Dispose();
            indices.Dispose();
            field.Dispose();
            return result;
        }

        private static float3 ToCube(float3 point)
        {
            return math.mul(math.inverse(Turn), point - new float3((GridSize - 1) * 0.5f + 0.31f));
        }

        // The largest plane distance, as the rock field writes for a block. Past an edge, the field keeps the plane
        // direction of the nearer face. The exact distance turns around the edge.
        private static float CubePlanes(float3 point)
        {
            return math.cmax(math.abs(ToCube(point)) - HalfSize);
        }

        // Exact distance to a box.
        private static float CubeDistance(float3 point)
        {
            float3 outside = math.abs(ToCube(point)) - HalfSize;
            return math.length(math.max(outside, 0f)) + math.min(math.cmax(outside), 0f);
        }

        private static CubeResult Measure(NativeList<float3> positions, NativeList<int> indices)
        {
            var result = new CubeResult { Triangles = indices.Length / 3 };
            var edges = new Dictionary<long, int>();
            double volume = 0.0;
            for (int index = 0; index < indices.Length; index += 3)
            {
                float3 a = positions[indices[index]];
                float3 b = positions[indices[index + 1]];
                float3 c = positions[indices[index + 2]];
                volume += math.dot(a, math.cross(b, c)) / 6.0;
                for (int corner = 0; corner < 3; corner++)
                {
                    int from = indices[index + corner];
                    int to = indices[index + (corner + 1) % 3];
                    long key = from < to ? ((long)from << 32) | (uint)to : ((long)to << 32) | (uint)from;
                    edges.TryGetValue(key, out int count);
                    edges[key] = count + 1;
                }
            }
            foreach (int count in edges.Values)
            {
                if (count == 1) result.OpenEdges++;
                if (count > 2) result.NonManifoldEdges++;
            }
            double trueVolume = 8.0 * HalfSize * HalfSize * HalfSize;
            result.VolumeRatio = (float)(volume / trueVolume);

            float cornerGap = 0f;
            for (int corner = 0; corner < 8; corner++)
            {
                var signs = new float3((corner & 1) * 2 - 1, (corner >> 1 & 1) * 2 - 1, (corner >> 2 & 1) * 2 - 1);
                cornerGap += Nearest(positions, signs * HalfSize);
            }
            result.MeanCornerGap = cornerGap / 8f;

            // Gap to the middle part of the 12 cube edges, at 15 points on each edge.
            float edgeGap = 0f;
            for (int axis = 0; axis < 3; axis++)
            {
                for (int side = 0; side < 4; side++)
                {
                    for (int step = 0; step < 15; step++)
                    {
                        float3 point = new float3((side & 1) * 2 - 1, (side >> 1) * 2 - 1, 0f) * HalfSize;
                        point = axis == 0 ? point.zxy : axis == 1 ? point.xzy : point;
                        point[axis] = (step - 7f) / 8f * HalfSize;
                        edgeGap += Nearest(positions, point);
                    }
                }
            }
            result.MeanEdgeGap = edgeGap / (12f * 15f);

            for (int index = 0; index < positions.Length; index++)
            {
                result.MaximumDistance = math.max(result.MaximumDistance, math.abs(CubeDistance(positions[index])));
            }
            return result;
        }

        // Distance from a point in cube space to the nearest vertex.
        private static float Nearest(NativeList<float3> positions, float3 point)
        {
            float best = float.MaxValue;
            for (int index = 0; index < positions.Length; index++)
            {
                best = math.min(best, math.distancesq(ToCube(positions[index]), point));
            }
            return math.sqrt(best);
        }
    }
}
