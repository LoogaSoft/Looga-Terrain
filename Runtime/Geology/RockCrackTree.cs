using System.Collections.Generic;
using System.Runtime.InteropServices;
using Unity.Mathematics;
using UnityEngine;
using Random = Unity.Mathematics.Random;

namespace LoogaSoft.Terrain.Geology
{
    /// <summary>One crack of a crack tree. The crack acts only in the part of its parent on the side of the parent.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct RockCrackNode
    {
        public Vector3 Point;
        // 0 for the first crack, 1 for the cracks of its two parts, and so on.
        public float Generation;
        public Vector3 Normal;
        // Index of the parent crack, or -1 for the first crack.
        public int Parent;
        // Side of the parent crack: -1 or 1.
        public int Side;
        public int Seed;
    }

    /// <summary>
    /// Splits a rock in order, as a rock breaks (Bohn et al. 2005, successive domain divisions): the first crack splits the
    /// whole rock across its longest size, and each part then splits across its own longest size. A part is a cloud of points
    /// inside the base blocks. The two parts of a split are long along the crack, so the next cracks meet it near a right
    /// angle and end at it in T junctions.
    /// </summary>
    internal static class RockCrackTree
    {
        // Points on each axis of the box for the shape of the parts.
        private const int SampleSteps = 24;
        // The compute shader keeps the side of each crack in a fixed array.
        public const int MaximumNodes = 32;
        private const int SmallestPointCount = 12;
        private const int PrincipalSteps = 16;

        /// <summary>Builds the cracks of a rock. The first node is the first crack, and a parent comes before its children.</summary>
        public static RockCrackNode[] Build(in RockCrackSettings settings, float4[] planes, int blockCount, Vector3 rockSize,
            int seed)
        {
            var points = new List<float3>();
            int planesPerBlock = planes.Length / math.max(blockCount, 1);
            float3 size = rockSize;
            for (int z = 0; z < SampleSteps; z++)
            {
                for (int y = 0; y < SampleSteps; y++)
                {
                    for (int x = 0; x < SampleSteps; x++)
                    {
                        float3 point = (new float3(x, y, z) + 0.5f) / SampleSteps * size - 0.5f * size;
                        if (Inside(planes, blockCount, planesPerBlock, point)) points.Add(point);
                    }
                }
            }

            var nodes = new List<RockCrackNode>();
            var random = new Random((uint)math.max((seed * 2654435761L + 97L) % uint.MaxValue, 1L));
            Split(settings, points, 0, -1, 0, ref random, nodes);
            return nodes.ToArray();
        }

        private static bool Inside(float4[] planes, int blockCount, int planesPerBlock, float3 point)
        {
            for (int block = 0; block < blockCount; block++)
            {
                bool inside = true;
                for (int plane = 0; plane < planesPerBlock && inside; plane++)
                {
                    float4 values = planes[block * planesPerBlock + plane];
                    inside = math.dot(values.xyz, point) - values.w <= 0f;
                }
                if (inside) return true;
            }
            return false;
        }

        private static void Split(in RockCrackSettings settings, List<float3> points, int generation, int parent, int side,
            ref Random random, List<RockCrackNode> nodes)
        {
            if (generation >= settings.Generations || nodes.Count >= MaximumNodes || points.Count < SmallestPointCount) return;
            if (generation > 0 && random.NextFloat() > settings.SplitChance) return;

            float3 mean = 0f;
            foreach (float3 point in points)
            {
                mean += point;
            }
            mean /= points.Count;
            float3 axis = LongestAxis(points, mean, ref random);
            float low = float.MaxValue;
            float high = float.MinValue;
            foreach (float3 point in points)
            {
                float along = math.dot(point - mean, axis);
                low = math.min(low, along);
                high = math.max(high, along);
            }
            float extent = high - low;
            if (extent < settings.SmallestPart) return;

            // The crack is square to the longest size, with a random turn and a random place near the middle.
            float turn = math.sin(math.radians(settings.Scatter));
            float3 normal = math.normalize(axis + random.NextFloat3Direction() * turn * random.NextFloat());
            float3 center = mean + axis * (0.5f * (low + high) + random.NextFloat(-1f, 1f) * settings.Jitter * extent);
            int index = nodes.Count;
            nodes.Add(new RockCrackNode
            {
                Point = center, Generation = generation, Normal = normal, Parent = parent, Side = side,
                Seed = random.NextInt(1, 100000)
            });

            var below = new List<float3>();
            var above = new List<float3>();
            foreach (float3 point in points)
            {
                (math.dot(point - center, normal) < 0f ? below : above).Add(point);
            }
            Split(settings, below, generation + 1, index, -1, ref random, nodes);
            Split(settings, above, generation + 1, index, 1, ref random, nodes);
        }

        // The principal axis of the points: power steps on their covariance.
        private static float3 LongestAxis(List<float3> points, float3 mean, ref Random random)
        {
            float3x3 covariance = float3x3.zero;
            foreach (float3 point in points)
            {
                float3 offset = point - mean;
                covariance += new float3x3(offset * offset.x, offset * offset.y, offset * offset.z);
            }
            float3 axis = random.NextFloat3Direction();
            for (int step = 0; step < PrincipalSteps; step++)
            {
                axis = math.normalizesafe(math.mul(covariance, axis), new float3(1f, 0f, 0f));
            }
            return axis;
        }
    }
}
