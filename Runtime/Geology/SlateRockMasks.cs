using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace LoogaSoft.Terrain.Geology
{
    /// <summary>
    /// Measures each vertex of a slate rock. Normals and occlusion come from the field. Convexity comes from the mesh: a vertex
    /// away from the centroid of its neighbors, along its normal, is on a ridge. Positions are in grid units.
    /// </summary>
    [BurstCompile]
    internal struct SlateRockMaskJob : IJob
    {
        private const int OcclusionSteps = 5;
        private const float RayShare = 0.7f;

        [ReadOnly] public NativeArray<float> Field;
        [ReadOnly] public NativeArray<float3> Positions;
        [ReadOnly] public NativeArray<int> Triangles;
        [WriteOnly] public NativeArray<float3> Normals;
        // x = ridge, y = cavity, z = occlusion.
        [WriteOnly] public NativeArray<float3> Masks;
        public int3 Size;
        // Field meters for each grid unit.
        public float CellSize;
        // Length of one occlusion step, in meters.
        public float OcclusionStep;

        public void Execute()
        {
            int count = Positions.Length;
            var ring = new NativeArray<float3>(count, Allocator.Temp);
            var links = new NativeArray<int>(count, Allocator.Temp);
            var edge = new NativeArray<float>(count, Allocator.Temp);
            for (int corner = 0; corner < Triangles.Length; corner += 3)
            {
                for (int side = 0; side < 3; side++)
                {
                    int from = Triangles[corner + side];
                    int to = Triangles[corner + (side + 1) % 3];
                    float length = math.distance(Positions[from], Positions[to]);
                    ring[from] += Positions[to];
                    ring[to] += Positions[from];
                    edge[from] += length;
                    edge[to] += length;
                    links[from]++;
                    links[to]++;
                }
            }

            // The second ring is the mean of the centroids of the first ring.
            var wide = new NativeArray<float3>(count, Allocator.Temp);
            for (int index = 0; index < count; index++)
            {
                ring[index] = links[index] > 0 ? ring[index] / links[index] : Positions[index];
            }
            for (int corner = 0; corner < Triangles.Length; corner += 3)
            {
                for (int side = 0; side < 3; side++)
                {
                    int from = Triangles[corner + side];
                    int to = Triangles[corner + (side + 1) % 3];
                    wide[from] += ring[to];
                    wide[to] += ring[from];
                }
            }

            for (int index = 0; index < count; index++)
            {
                float3 position = Positions[index];
                float3 normal = Gradient(position);
                normal = math.normalizesafe(normal, new float3(0f, 1f, 0f));
                Normals[index] = normal;

                float average = links[index] > 0 ? math.max(edge[index] / links[index], 1e-4f) : 1f;
                float3 wideCenter = links[index] > 0 ? wide[index] / links[index] : position;
                float near = math.dot(position - ring[index], normal) / average;
                float far = math.dot(position - wideCenter, normal) / average;
                float curvature = math.abs(far) * 0.5f > math.abs(near) ? far * 0.5f : near;

                float occlusion = 0f;
                float weight = 0.5f;
                for (int step = 1; step <= OcclusionSteps; step++)
                {
                    float distance = step * OcclusionStep;
                    float value = Sample(position + normal * (distance / CellSize));
                    // Rock counts only when it is well inside the ray, so an open face near a joint stays open.
                    float deficit = math.max(distance * RayShare - value, 0f);
                    occlusion += weight * math.saturate(deficit / (distance * RayShare));
                    weight *= 0.55f;
                }
                Masks[index] = new float3(math.saturate(curvature * 5.2f), math.saturate(-curvature * 5f),
                    math.saturate(1f - occlusion * 1.35f));
            }
        }

        private float3 Gradient(float3 position)
        {
            return new float3(Sample(position + new float3(1f, 0f, 0f)) - Sample(position - new float3(1f, 0f, 0f)),
                Sample(position + new float3(0f, 1f, 0f)) - Sample(position - new float3(0f, 1f, 0f)),
                Sample(position + new float3(0f, 0f, 1f)) - Sample(position - new float3(0f, 0f, 1f)));
        }

        private float Sample(float3 position)
        {
            float3 limit = (float3)(Size - 1) - 0.001f;
            position = math.clamp(position, 0f, limit);
            var low = (int3)position;
            float3 share = position - low;
            float c00 = math.lerp(Value(low), Value(low + new int3(1, 0, 0)), share.x);
            float c10 = math.lerp(Value(low + new int3(0, 1, 0)), Value(low + new int3(1, 1, 0)), share.x);
            float c01 = math.lerp(Value(low + new int3(0, 0, 1)), Value(low + new int3(1, 0, 1)), share.x);
            float c11 = math.lerp(Value(low + new int3(0, 1, 1)), Value(low + new int3(1, 1, 1)), share.x);
            return math.lerp(math.lerp(c00, c10, share.y), math.lerp(c01, c11, share.y), share.z);
        }

        private float Value(int3 sample)
        {
            return Field[sample.x + Size.x * (sample.y + Size.y * sample.z)];
        }
    }
}
