using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Unity.Mathematics;
using UnityEngine;
using Random = Unity.Mathematics.Random;

namespace LoogaSoft.Terrain.Geology
{
    /// <summary>One split plane. A child is a node index, or the bitwise complement of a leaf index.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct RockSplitNode
    {
        public Vector3 Normal;
        public float Offset;
        public int Low;
        public int High;
    }

    /// <summary>One block of a split.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct RockSplitLeaf
    {
        public Vector3 Center;
        public float Depth;
        public Vector3 Slope;
        public float Spare;
    }

    /// <summary>
    /// Divides the rock box into blocks. Each step splits one block across its longest side, relative to the block size of the
    /// settings. A split lies only inside its block, so it ends at the walls of that block.
    /// </summary>
    internal static class RockSplitTree
    {
        // A block stops splitting between one and this many times its usual size, so block sizes vary.
        private const float StopRange = 1.6f;
        private const int FaceCount = 6;

        /// <summary>Builds the tree. There are no nodes when the rock is smaller than one block.</summary>
        public static void Build(in RockSplitSettings settings, Vector3 rockSize, int seed, out RockSplitNode[] nodes,
            out RockSplitLeaf[] leaves)
        {
            var nodeList = new List<RockSplitNode>();
            var leafList = new List<RockSplitLeaf>();
            var random = new Random((uint)math.max(seed * 2654435761L % uint.MaxValue, 1L));
            Quaternion frame = Quaternion.Euler(settings.Rotation);
            float3 half = 0.5f * (float3)rockSize;
            const int allFaces = (1 << FaceCount) - 1;
            Divide(settings, frame, -half, half, allFaces, ref random, nodeList, leafList);
            nodes = nodeList.ToArray();
            leaves = leafList.ToArray();
        }

        // Returns the child value of this block. Bit 2 x axis of faces is the low face on that axis, the next bit the high face.
        private static int Divide(in RockSplitSettings settings, Quaternion frame, float3 low, float3 high, int faces,
            ref Random random, List<RockSplitNode> nodes, List<RockSplitLeaf> leaves)
        {
            float3 size = high - low;
            float3 ratios = size / math.max((float3)settings.BlockSize, 0.01f);
            int axis = ratios.x >= ratios.y && ratios.x >= ratios.z ? 0 : ratios.y >= ratios.z ? 1 : 2;
            if (ratios[axis] <= random.NextFloat(1f, StopRange))
            {
                leaves.Add(MakeLeaf(settings, frame, low, high, faces, ref random));
                return ~(leaves.Count - 1);
            }

            float at = low[axis] + size[axis] * (0.5f + random.NextFloat(-settings.Jitter, settings.Jitter));
            float reach = math.tan(math.radians(settings.Scatter));
            var normal = new float3(random.NextFloat(-reach, reach), random.NextFloat(-reach, reach),
                random.NextFloat(-reach, reach));
            normal[axis] = 1f;
            var turned = (Vector3)math.normalize((float3)(frame * (Vector3)normal));
            float3 middle = 0.5f * (low + high);
            middle[axis] = at;

            int index = nodes.Count;
            nodes.Add(default);
            float3 lowEnd = high;
            lowEnd[axis] = at;
            float3 highStart = low;
            highStart[axis] = at;
            int lowFaces = faces & ~(1 << (axis * 2 + 1));
            int highFaces = faces & ~(1 << (axis * 2));
            int lowChild = Divide(settings, frame, low, lowEnd, lowFaces, ref random, nodes, leaves);
            int highChild = Divide(settings, frame, highStart, high, highFaces, ref random, nodes, leaves);
            nodes[index] = new RockSplitNode
            {
                Normal = turned, Offset = Vector3.Dot(turned, frame * (Vector3)middle), Low = lowChild, High = highChild
            };
            return index;
        }

        private static RockSplitLeaf MakeLeaf(in RockSplitSettings settings, Quaternion frame, float3 low, float3 high, int faces,
            ref Random random)
        {
            int outerFaces = math.countbits(faces);
            float depth = settings.Depth * math.pow(random.NextFloat(), math.max(settings.DepthPower, 0.1f));
            if (outerFaces >= 2) depth *= settings.CornerKeep;
            float3 slope = random.NextFloat3Direction() * random.NextFloat(settings.Tilt);
            return new RockSplitLeaf
            {
                Center = frame * (Vector3)(0.5f * (low + high)), Depth = depth, Slope = slope
            };
        }
    }
}
