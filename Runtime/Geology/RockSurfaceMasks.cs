using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace LoogaSoft.Terrain.Geology
{
    /// <summary>Reach of the surface masks that the generator measures in the finished field.</summary>
    [Serializable]
    public struct RockMaskSettings
    {
        [Min(0.05f), Tooltip("Reach of the edge and crevice test, in meters. Use about 2 grid cells or more.")]
        public float EdgeReach;
        [Min(0.1f), Tooltip("Reach of the large ridge and hollow test, in meters.")]
        public float RidgeReach;
        [Min(0.05f), Tooltip("Reach of the cavity test, in meters. Closer rock around a point darkens the point.")]
        public float CavityReach;
        [Min(0.1f), Tooltip("Reach of the shelter test, in meters. Rock above a point shelters the point.")]
        public float ShelterReach;
        [Min(0f), Tooltip("The surface error of a full mask change, in meters. Levels of detail keep mask changes above " +
                          "their error limit, so masks stay sharp on flat faces.")]
        public float MaskError;

        public static RockMaskSettings Default => new()
        {
            EdgeReach = 0.25f, RidgeReach = 1.5f, CavityReach = 0.6f, ShelterReach = 3f, MaskError = 0.05f
        };
    }

    /// <summary>
    /// Measures the surface at each vertex from the field. Positions and reaches are in grid units, and the field is in
    /// meters: <see cref="FieldScale"/> changes field values to grid units. The mean field on a sphere around a surface
    /// point passes 0 at a convex edge and stays below 0 in a crevice. The field is known everywhere, so this needs no
    /// mesh curvature and gives the same values on all levels of detail.
    /// </summary>
    [BurstCompile]
    internal struct RockSurfaceMaskJob : IJobParallelFor
    {
        // The mean field on a sphere at a square edge is this share of the sphere radius.
        private const float SquareEdgeShare = 0.354f;
        // A step of a ray counts as blocked when the rock is closer than this share of the step distance.
        private const float ConeShare = 0.5f;
        private const int RayCount = 8;
        private const int StepCount = 5;

        [ReadOnly] public NativeArray<float> Field;
        [ReadOnly] public NativeArray<float3> Positions;
        // Directions on a sphere for the edge test.
        [ReadOnly] public NativeArray<float3> SphereDirections;
        [WriteOnly] public NativeArray<float3> Normals;
        [WriteOnly] public NativeArray<float> Cavities;
        // x = edge, y = ridge with 0.5 for flat ground, z = crevice, w = shelter.
        [WriteOnly] public NativeArray<float4> Masks;
        public int3 Size;
        // Grid cells for each meter of field value.
        public float FieldScale;
        public float EdgeReach;
        public float RidgeReach;
        public float CavityReach;
        public float ShelterReach;

        public void Execute(int index)
        {
            float3 point = Positions[index];
            float3 gradient = new float3(
                Sample(point + new float3(1f, 0f, 0f)) - Sample(point - new float3(1f, 0f, 0f)),
                Sample(point + new float3(0f, 1f, 0f)) - Sample(point - new float3(0f, 1f, 0f)),
                Sample(point + new float3(0f, 0f, 1f)) - Sample(point - new float3(0f, 0f, 1f))) * 0.5f * FieldScale;
            float length = math.length(gradient);
            float3 normal = length > 1e-6f ? gradient / length : new float3(0f, 1f, 0f);
            Normals[index] = normal;
            // The field is not an exact distance after noise. The gradient length scales it back to grid units.
            float toDistance = FieldScale / math.max(length, 0.2f);

            float center = Sample(point);
            float fine = (SphereMean(point, EdgeReach) - center) * toDistance / (SquareEdgeShare * EdgeReach);
            float broad = (SphereMean(point, RidgeReach) - center) * toDistance / (SquareEdgeShare * RidgeReach);

            float3 helper = math.abs(normal.y) < 0.9f ? new float3(0f, 1f, 0f) : new float3(1f, 0f, 0f);
            float3 tangent = math.normalize(math.cross(helper, normal));
            float3 bitangent = math.cross(normal, tangent);
            Cavities[index] = 1f - Hemisphere(point, normal, tangent, bitangent, toDistance);
            float shelter = 1f - Sky(point, normal, toDistance);

            Masks[index] = new float4(math.saturate(fine), math.saturate(0.5f + 0.5f * broad), math.saturate(-fine),
                math.saturate(shelter));
        }

        private float SphereMean(float3 point, float reach)
        {
            float sum = 0f;
            for (int index = 0; index < SphereDirections.Length; index++)
            {
                sum += Sample(point + SphereDirections[index] * reach);
            }
            return sum / SphereDirections.Length;
        }

        // Share of the rays around the normal that leave the rock freely.
        private float Hemisphere(float3 point, float3 normal, float3 tangent, float3 bitangent, float toDistance)
        {
            float sum = Ray(point, normal, normal, CavityReach, toDistance);
            for (int ray = 0; ray < RayCount; ray++)
            {
                float angle = 2f * math.PI * ray / RayCount;
                float3 around = tangent * math.cos(angle) + bitangent * math.sin(angle);
                // Rays stay within 50 degrees of the normal, so a flat surface does not block them.
                sum += Ray(point, normal, normal * 0.64f + around * 0.77f, CavityReach, toDistance);
            }
            return sum / (RayCount + 1);
        }

        // Share of the rays toward the sky that leave the rock freely. Rays behind the surface count as blocked.
        private float Sky(float3 point, float3 normal, float toDistance)
        {
            var up = new float3(0f, 1f, 0f);
            float sum = 0f;
            int count = 0;
            if (normal.y > 0.1f)
            {
                sum += Ray(point, normal, up, ShelterReach, toDistance);
                count++;
            }
            for (int ray = 0; ray < RayCount; ray++)
            {
                float angle = 2f * math.PI * ray / RayCount;
                var direction = new float3(0.64f * math.cos(angle), 0.77f, 0.64f * math.sin(angle));
                if (math.dot(direction, normal) <= 0.1f) continue;
                sum += Ray(point, normal, direction, ShelterReach, toDistance);
                count++;
            }
            // A face that looks down sees none of the sky rays.
            return count > 0 ? sum / count : 0f;
        }

        // Visibility along a ray from 0 to 1. Steps grow by a factor of two up to the reach.
        private float Ray(float3 point, float3 normal, float3 direction, float reach, float toDistance)
        {
            float3 start = point + normal * 1.5f;
            float visible = 1f;
            float distance = reach / (1 << (StepCount - 1));
            for (int step = 0; step < StepCount; step++)
            {
                float clearance = Sample(start + direction * distance) * toDistance;
                visible = math.min(visible, math.saturate(clearance / (ConeShare * distance)));
                distance *= 2f;
            }
            return visible;
        }

        // Trilinear sample at a grid position.
        private float Sample(float3 point)
        {
            float3 limited = math.clamp(point, 0f, new float3(Size - 2));
            var low = (int3)math.floor(limited);
            float3 share = limited - low;
            int planeSize = Size.x * Size.y;
            int at = low.x + Size.x * low.y + planeSize * low.z;
            float x00 = math.lerp(Field[at], Field[at + 1], share.x);
            float x10 = math.lerp(Field[at + Size.x], Field[at + Size.x + 1], share.x);
            float x01 = math.lerp(Field[at + planeSize], Field[at + planeSize + 1], share.x);
            float x11 = math.lerp(Field[at + planeSize + Size.x], Field[at + planeSize + Size.x + 1], share.x);
            return math.lerp(math.lerp(x00, x10, share.y), math.lerp(x01, x11, share.y), share.z);
        }
    }
}
