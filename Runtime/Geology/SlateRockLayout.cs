using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Unity.Mathematics;
using UnityEngine;

namespace LoogaSoft.Terrain.Geology
{
    /// <summary>Settings of a slate monolith. Lengths are in normalized rock units unless a tooltip says meters.</summary>
    [Serializable]
    public struct SlateRockSettings
    {
        public int Seed;
        [Min(0.1f), Tooltip("Meters for each normalized unit.")]
        public float WorldScale;
        [Tooltip("Half size of the monolith.")]
        public Vector3 HalfExtents;
        [Tooltip("Depth below the center where the layers start to step in. The step in is largest at the base.")]
        public float UndercutTop;
        [Tooltip("Largest cut of the ledges at the base: X on the side of the lean, Y across the front corner on that side.")]
        public Vector2 Undercut;
        [Tooltip("Move of the whole rock along X and Z for each unit of height.")]
        public Vector2 HullLean;
        [Range(4, 8)] public int HullPlanes;
        [Tooltip("Smallest and largest turn of a hull plane, in degrees.")]
        public Vector2 HullTilt;
        [Tooltip("Smallest and largest number of bedding layers.")]
        public Vector2Int Layers;
        [Min(0f), Tooltip("Largest move of a layer along the rock face. The moves have a mean of zero.")]
        public float LayerShift;
        [Min(0f), Tooltip("Largest lean of a layer, in degrees.")]
        public float LayerLean;
        [Tooltip("Smallest and largest number of cross joints in a layer.")]
        public Vector2Int Joints;
        [Min(0f), Tooltip("Largest step at a cross joint.")]
        public float JointStep;
        [Min(0f)] public float CreaseWidth;
        [Min(0f)] public float CreaseDepth;
        [Min(0f), Tooltip("Largest wander of the seams, so a seam is a rough line.")]
        public float SeamWander;
        [Tooltip("Smallest and largest number of chips.")]
        public Vector2Int Chips;
        [Min(0f), Tooltip("Largest depth of a chip below the surface of the blocks.")]
        public float ChipDepth;

        public static SlateRockSettings Default => new()
        {
            Seed = 1, WorldScale = 16.88f, HalfExtents = new Vector3(0.34f, 0.46f, 0.28f), UndercutTop = 0.08f,
            Undercut = new Vector2(0.15f, 0.17f), HullLean = new Vector2(-0.05f, -0.025f), HullPlanes = 8, HullTilt = new Vector2(5f, 11f),
            Layers = new Vector2Int(8, 10), LayerShift = 0.007f, LayerLean = 2.0f, Joints = new Vector2Int(2, 3), JointStep = 0.018f,
            CreaseWidth = 0.005f, CreaseDepth = 0.0045f, SeamWander = 0.0032f, Chips = new Vector2Int(10, 16), ChipDepth = 0.014f
        };
    }

    /// <summary>One bedding layer. The layout must match the struct of LoogaSlateField.compute.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct SlateLayer
    {
        public float2 Shift;
        public float2 Lean;
        public float Center;
        public int FirstJointX;
        public int JointCountX;
        public int FirstJointZ;
        public int JointCountZ;
        public int FirstStepX;
        public int FirstStepZ;
    }

    /// <summary>One chip. The layout must match the struct of LoogaSlateField.compute.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct SlateChip
    {
        public float4 Plane;
        public float3 Center;
        public float3 HalfSize;
        public float3 AxisA;
        public float3 AxisB;
        public float Depth;
        public int Layer;
    }

    /// <summary>
    /// The geological layout of a monolith: hull planes, bedding planes, layers, cross joints and chips, from one seed. Cross
    /// joints follow vertical tracks through all layers, so block X, Z of one layer stands on block X, Z of the layer below and a
    /// merged column has flush walls. The moves of the layers are drawn around zero, so the rock face does not lean out in steps.
    /// </summary>
    internal sealed class SlateRockLayout
    {
        public float4[] HullPlanes;
        // Planes of the seams between layers, from the base up. Layer i lies between plane i and plane i + 1.
        public float4[] Beds;
        public float[] BedGates;
        public SlateLayer[] Layers;
        public float4[] Joints;
        public float[] Steps;
        public SlateChip[] Chips;

        public SlateRockLayout(in SlateRockSettings settings)
        {
            var random = new Unity.Mathematics.Random((uint)settings.Seed * 2654435761u + 41u);
            float3 half = (Vector3)settings.HalfExtents;
            MakeHull(ref random, settings, half);
            MakeBeds(ref random, settings, half);
            MakeLayers(ref random, settings, half);
            MakeChips(ref random, settings, half);
        }

        private void MakeHull(ref Unity.Mathematics.Random random, in SlateRockSettings settings, float3 half)
        {
            var faces = new[]
            {
                new float3(0f, 0f, 1f), new float3(0f, 0f, -1f), new float3(1f, 0f, 0f), new float3(-1f, 0f, 0f), new float3(0f, 1f, 0f),
                new float3(0.75f, 0.12f, 0.65f), new float3(-0.75f, -0.15f, 0.65f), new float3(0.65f, -0.18f, -0.75f)
            };
            var planes = new List<float4>();
            for (int index = 0; index < settings.HullPlanes; index++)
            {
                float3 face = math.normalize(faces[index]);
                // Two axes across the face.
                float3 helper = math.abs(face.y) < 0.9f ? new float3(0f, 1f, 0f) : new float3(1f, 0f, 0f);
                float3 across = math.normalize(math.cross(helper, face));
                float3 second = math.cross(face, across);
                float turn = math.radians(random.NextFloat(settings.HullTilt.x, settings.HullTilt.y));
                float way = random.NextFloat(2f * math.PI);
                float3 normal = math.normalize(face + (across * math.cos(way) + second * math.sin(way)) * math.tan(turn));
                float3 onFace = face * 0.985f * math.dot(math.abs(face), half);
                if (index == 4)
                {
                    // The crown is one angled cleavage roof.
                    normal = math.normalize(new float3(random.NextFloat(-0.32f, 0.35f), 1f, random.NextFloat(-0.32f, 0.22f)));
                    onFace = new float3(0f, half.y * 0.96f, 0f);
                }
                else if (index > 4)
                {
                    // Steep corner facets, so the footprint is an irregular polygon and not a rectangle.
                    onFace = math.sign(face) * half * random.NextFloat(0.84f, 0.94f);
                }
                planes.Add(new float4(normal, math.dot(normal, onFace)));
            }
            HullPlanes = planes.ToArray();
        }

        private void MakeBeds(ref Unity.Mathematics.Random random, in SlateRockSettings settings, float3 half)
        {
            int count = random.NextInt(settings.Layers.x, settings.Layers.y + 1);
            // Random layer sizes with a high floor. A thin layer below an overhang turns into a floating shelf.
            var sizes = new float[count];
            float total = 0f;
            for (int index = 0; index < count; index++)
            {
                sizes[index] = math.lerp(0.55f, 1.45f, math.pow(random.NextFloat(), 1.3f));
                total += sizes[index];
            }

            Beds = new float4[count + 1];
            BedGates = new float[count + 1];
            var dip = new float2(random.NextFloat(-0.08f, 0.08f), random.NextFloat(-0.08f, 0.08f));
            float height = -half.y - 0.02f;
            float range = 2f * half.y + 0.04f;
            for (int index = 0; index <= count; index++)
            {
                // The bedding dips as one body. A small turn of each plane stays below the size of a layer, so planes do not cross.
                float2 turn = dip + new float2(random.NextFloat(-0.012f, 0.012f), random.NextFloat(-0.012f, 0.012f));
                float3 normal = math.normalize(new float3(turn.x, 1f, turn.y));
                Beds[index] = new float4(normal, normal.y * height);
                // Each seam fades in and out with its own noise. Gate is the offset of that noise.
                BedGates[index] = random.NextFloat(0f, 64f);
                if (index < count) height += range * sizes[index] / total;
            }
        }

        private void MakeLayers(ref Unity.Mathematics.Random random, in SlateRockSettings settings, float3 half)
        {
            int count = Beds.Length - 1;
            Layers = new SlateLayer[count];
            var joints = new List<float4>();
            var steps = new List<float>();
            float lean = math.tan(math.radians(settings.LayerLean));

            int countX = math.clamp(settings.Joints.y - 1, 2, 3);
            int countZ = math.clamp(settings.Joints.x, 1, 2);
            float4[] tracksX = MakeJointTracks(ref random, countX, half.x, new float3(1f, 0f, 0f));
            float4[] tracksZ = MakeJointTracks(ref random, countZ, half.z, new float3(0f, 0f, 1f));

            for (int index = 0; index < count; index++)
            {
                var layer = new SlateLayer
                {
                    Shift = new float2(Centered(ref random, settings.LayerShift), Centered(ref random, settings.LayerShift)),
                    Lean = new float2(random.NextFloat(-lean, lean), random.NextFloat(-lean, lean)),
                    Center = 0.5f * (Beds[index].w + Beds[index + 1].w), FirstJointX = joints.Count, JointCountX = countX,
                    FirstStepX = steps.Count
                };

                AddJoints(ref random, joints, steps, tracksX, settings);
                layer.FirstJointZ = joints.Count;
                layer.JointCountZ = countZ;
                layer.FirstStepZ = steps.Count;
                AddJoints(ref random, joints, steps, tracksZ, settings);
                Layers[index] = layer;
            }
            Joints = joints.ToArray();
            Steps = steps.ToArray();
        }

        private static float4[] MakeJointTracks(ref Unity.Mathematics.Random random, int count, float reach, float3 axis)
        {
            var tracks = new float4[count];
            float3 other = axis.x > 0.5f ? new float3(0f, 0f, 1f) : new float3(1f, 0f, 0f);
            // One family has one cant, so its joints stay near parallel and blocks do not taper to points.
            float cant = random.NextFloat(-0.18f, 0.14f);
            for (int index = 0; index < count; index++)
            {
                // Even shares with a shift, so tracks do not bunch up.
                float share = (index + random.NextFloat(0.28f, 0.72f)) / count;
                float place = math.lerp(-0.78f * reach, 0.78f * reach, share);
                float3 normal = math.normalize(axis + new float3(0f, cant + random.NextFloat(-0.03f, 0.03f), 0f) + other * random.NextFloat(-0.03f, 0.03f));
                tracks[index] = new float4(normal, place);
            }
            return tracks;
        }

        private static void AddJoints(ref Unity.Mathematics.Random random, List<float4> joints, List<float> steps, float4[] tracks,
            in SlateRockSettings settings)
        {
            foreach (float4 track in tracks)
            {
                // A small jog in some layers. The order of the joints does not change, so block indices stay aligned.
                float jog = random.NextFloat() < 0.3f ? random.NextFloat(-0.012f, 0.012f) : 0f;
                joints.Add(new float4(track.xyz, track.w + jog));
            }
            for (int index = 0; index <= tracks.Length; index++) steps.Add(Centered(ref random, settings.JointStep));
        }

        // Most moves are small and few are large. Their mean is zero.
        private static float Centered(ref Unity.Mathematics.Random random, float limit)
        {
            float value = random.NextFloat(-1f, 1f);
            return math.sign(value) * math.pow(math.abs(value), 1.5f) * limit;
        }

        private void MakeChips(ref Unity.Mathematics.Random random, in SlateRockSettings settings, float3 half)
        {
            int count = random.NextInt(settings.Chips.x, settings.Chips.y + 1);
            var chips = new List<SlateChip>(count);
            for (int index = 0; index < count; index++)
            {
                int layerIndex = random.NextInt(Layers.Length);
                SlateLayer layer = Layers[layerIndex];
                int side = random.NextInt(4);
                bool onZ = side < 2;
                float way = side % 2 == 0 ? 1f : -1f;
                float3 outward = onZ ? new float3(0f, 0f, way) : new float3(way, 0f, 0f);

                // A chip starts where a cross joint meets a seam, or at a corner of the rock. It never starts in the middle of a face.
                int jointCount = onZ ? layer.JointCountX : layer.JointCountZ;
                int firstJoint = onZ ? layer.FirstJointX : layer.FirstJointZ;
                float along = onZ ? half.x : half.z;
                bool corner = jointCount == 0 || random.NextFloat() < 0.4f;
                float place = corner ? random.NextBool() ? along * 0.97f : -along * 0.97f : Joints[firstJoint + random.NextInt(jointCount)].w;

                // The chip takes the lip of a seam: the top of this layer or its base.
                float verticalSign = random.NextBool() ? 1f : -1f;
                float seamHeight = Beds[verticalSign > 0f ? layerIndex + 1 : layerIndex].w;
                float3 axisA = onZ ? new float3(1f, 0f, 0f) : new float3(0f, 0f, 1f);
                float3 axisB = new float3(0f, 1f, 0f);
                float3 onFace = axisA * place + axisB * seamHeight + outward * math.dot(math.abs(outward), half);

                float3 size = new float3(random.NextFloat(0.03f, 0.09f), random.NextFloat(0.012f, 0.045f), 0.06f);
                // The chip reaches from the junction into the block on one side of the joint.
                float inward = place > 0f ? -1f : 1f;
                float3 center = onFace + axisA * (inward * size.x * 0.8f);

                // Each chip slices the lip at an oblique angle. A plane parallel to the face would leave a square pit.
                float3 normal = math.normalize(outward * random.NextFloat(0.55f, 0.85f) + axisB * (verticalSign * random.NextFloat(0.45f, 0.85f)) +
                                               axisA * random.NextFloat(-0.35f, 0.35f));
                float3 onPlane = onFace - outward * random.NextFloat(0.008f, 0.024f);
                chips.Add(new SlateChip
                {
                    Plane = new float4(normal, math.dot(normal, onPlane)), Center = center, HalfSize = size, AxisA = axisA, AxisB = axisB,
                    Depth = settings.ChipDepth * random.NextFloat(0.4f, 1f), Layer = layerIndex
                });
            }
            Chips = chips.ToArray();
        }
    }
}
