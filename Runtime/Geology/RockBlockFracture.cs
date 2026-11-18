using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace LoogaSoft.Terrain.Geology
{
    /// <summary>Settings of a rock that is built from solid blocks. See <see cref="RockBlockLayout"/>.</summary>
    [Serializable]
    public struct RockBlockSettings
    {
        [Min(0f), Tooltip("Distance between two samples of the block search, in meters. 0 turns the blocks off.")]
        public float Spacing;
        [Min(0.1f), Tooltip("Largest distance from the first sample of a block, in meters.")]
        public float MaxBlockSize;
        [Min(0f), Tooltip("Open width of a crack between two blocks, in meters.")]
        public float Gap;
        [Min(0f), Tooltip("Cracks and missing blocks end this far below the box, in meters.")]
        public float CoreDepth;
        [Min(0), Tooltip("A block with fewer samples joins a neighbor with no fracture between. A high value removes the plates. 0 = off.")]
        public int MergeBelow;
        [Min(0f), Tooltip("A block on the surface that is thinner than this is a plate, in meters.")]
        public float PlateThickness;
        [Range(0f, 1f), Tooltip("Share of the plates that fell off. The next layer then shows.")]
        public float Missing;
        [Min(0f), Tooltip("Largest move of a block into the rock, in meters. Most blocks move a small amount.")]
        public float Relief;
        [Min(0f), Tooltip("Slow wander of all block faces, in meters.")]
        public float Warp;
        [Min(0.1f), Tooltip("Size of the wander, in meters.")]
        public float WarpSize;
        [Tooltip("The rock is the box with its face depth maps as signed relief, in meters, and it has no blocks.")]
        public bool SurfaceOnly;
        [Min(0f), Tooltip("Largest move into the rock of a mass on a face with no depth map, in meters.")]
        public float MassMove;
        [Min(0.1f), Tooltip("Power of the random move of a mass. Above 1, most masses move a small amount and few move far.")]
        public float MassPower;
        [Min(0f), Tooltip("Height of the broad forms of the shared surface, in meters. Each mass lies on the tangent plane of this surface.")]
        public float BroadHeight;
        [Min(0.1f), Tooltip("Size of the broad forms, in meters.")]
        public float BroadSize;
        [Min(0f), Tooltip("Height of the middle forms of the shared surface, in meters.")]
        public float MiddleHeight;
        [Min(0.1f), Tooltip("Size of the middle forms, in meters.")]
        public float MiddleSize;
        [Min(0f), Tooltip("Room outside the box for masses that tilt out of it, in meters.")]
        public float MassRoom;
        [Min(0f), Tooltip("Largest depth of a mass on a face with a depth map, in meters.")]
        public float MassDepth;
        [Range(0f, 0.5f), Tooltip("Largest tilt of a mass, in radians.")]
        public float MassTilt;
        [Min(0f), Tooltip("Number of chips on each meter of the outline of a block face.")]
        public float ChipDensity;
        [Tooltip("Smallest and largest half width of a chip, in meters.")]
        public Vector2 ChipSize;
        [Min(0f), Tooltip("Depth of one chip layer, in meters. Chips come off at one or two layers, parallel to the face.")]
        public float ChipDepth;
        public int Seed;
    }

    /// <summary>
    /// Fractures made one after the other, as in rock: an early fracture is long, and a later fracture stops against it. So the
    /// fractures of a level meet earlier fractures in T junctions, and the masses have many sizes.
    /// </summary>
    [Serializable]
    public struct RockJointTree
    {
        [Min(0.2f), Tooltip("A mass with all sides below this size does not split, in meters.")]
        public float SmallestMass;
        [Min(0.2f), Tooltip("A mass with a side above this size always splits, in meters.")]
        public float LargestMass;
        [Range(0f, 1f), Tooltip("Chance that a mass between the two sizes does not split. This makes some masses much larger than others.")]
        public float Keep;
        [Min(0.1f), Tooltip("Above 1, level joints are more common than the proportions of a mass ask for.")]
        public float LevelBias;
        [Range(0f, 0.5f), Tooltip("Largest tilt of a joint, in radians.")]
        public float Tilt;
        [Range(0f, 0.5f), Tooltip("Split position away from the middle of a mass, as a share of its size.")]
        public float Offset;
        public int Seed;

        /// <summary>Adds the fractures of a box to a list.</summary>
        public readonly void AddTo(List<RockFracture> fractures, Vector3 center, Vector3 size)
        {
            var random = new Unity.Mathematics.Random((uint)Seed * 2654435761u + 19u);
            float3 half = (float3)(Vector3)size * 0.5f;
            float3 middle = (Vector3)center;
            // A hidden cut through the middle stops a mass from reaching two opposite faces, which cannot both lie on their surface.
            float3 low = middle - half;
            float3 high = middle + half;
            AddJoint(fractures, low, high, 2, middle.z, new float3(0f, 0f, 1f));
            for (int side = 0; side < 2; side++)
            {
                float3 sideLow = low;
                float3 sideHigh = high;
                if (side == 0) sideHigh.z = middle.z;
                else sideLow.z = middle.z;
                AddJoint(fractures, sideLow, sideHigh, 0, middle.x, new float3(1f, 0f, 0f));
                float3 lowHigh = sideHigh;
                float3 highLow = sideLow;
                lowHigh.x = middle.x;
                highLow.x = middle.x;
                Split(ref random, fractures, sideLow, lowHigh);
                Split(ref random, fractures, highLow, sideHigh);
            }
        }

        private static void AddJoint(List<RockFracture> fractures, float3 low, float3 high, int axis, float position, float3 normal)
        {
            int sideA = (axis + 1) % 3;
            int sideB = (axis + 2) % 3;
            float3 size = high - low;
            float3 along = float3.zero;
            along[sideA] = 1f;
            float3 middle = (low + high) * 0.5f;
            middle[axis] = position;
            // A joint reaches a little past its mass, so it closes against the older joints around it.
            fractures.Add(new RockFracture
            {
                Center = middle, Along = along, Normal = math.normalize(normal), HalfLength = 0.5f * size[sideA] + 0.2f,
                HalfWidth = 0.5f * size[sideB] + 0.2f, StepFade = 1f
            });
        }

        private readonly void Split(ref Unity.Mathematics.Random random, List<RockFracture> fractures, float3 low, float3 high)
        {
            float3 size = high - low;
            float longest = math.cmax(size);
            if (longest < SmallestMass) return;
            if (longest < LargestMass && random.NextFloat() < Keep) return;

            float3 weight = size * size;
            weight.y *= LevelBias;
            weight = math.select(weight, 0f, size < SmallestMass);
            float pick = random.NextFloat() * math.csum(weight);
            int axis = pick < weight.x ? 0 : pick < weight.x + weight.y ? 1 : 2;
            if (math.csum(weight) <= 0f) return;

            float position = math.lerp(low[axis], high[axis], 0.5f + random.NextFloat(-Offset, Offset));
            int sideA = (axis + 1) % 3;
            int sideB = (axis + 2) % 3;
            float3 normal = float3.zero;
            normal[axis] = 1f;
            normal[sideA] = random.NextFloat(-Tilt, Tilt);
            normal[sideB] = random.NextFloat(-Tilt, Tilt);
            normal = math.normalize(normal);
            AddJoint(fractures, low, high, axis, position, normal);

            float3 lowerHigh = high;
            float3 upperLow = low;
            lowerHigh[axis] = position;
            upperLow[axis] = position;
            Split(ref random, fractures, low, lowerHigh);
            Split(ref random, fractures, upperLow, high);
        }
    }

    /// <summary>
    /// How deep the rock lies below one face of the box, for example from a picture. Rows run along the third axis after the face
    /// axis, and columns along the first axis after it.
    /// </summary>
    [Serializable]
    public sealed class RockFaceDepth
    {
        [Tooltip("Direction out of the face. One box axis.")]
        public Vector3 Face;
        [Min(1)] public int Width;
        [Min(1)] public int Height;
        [Tooltip("Depth share of each cell, from 0 to 1.")]
        public float[] Depth;

        /// <summary>Returns the depth at a position from 0 to 1 across the face.</summary>
        public float Sample(float across, float along)
        {
            int column = Mathf.Clamp((int)(across * Width), 0, Width - 1);
            int row = Mathf.Clamp((int)(along * Height), 0, Height - 1);
            return Depth[column + row * Width];
        }
    }

    /// <summary>Layers that can split off a face, as on slate: discs parallel to the face, and short joints across them.</summary>
    [Serializable]
    public struct RockCleavageSet
    {
        [Tooltip("Direction out of the face.")]
        public Vector3 Face;
        [Min(0.01f), Tooltip("Thickness of a layer, in meters.")]
        public float LayerThickness;
        [Min(1)] public int Layers;
        [Min(0), Tooltip("Number of discs in each layer.")]
        public int Discs;
        [Tooltip("Smallest and largest half size of a disc, in meters.")]
        public Vector2 DiscSize;
        [Min(0), Tooltip("Number of short joints across the layers.")]
        public int Joints;
        [Tooltip("Smallest and largest half length of a joint, in meters.")]
        public Vector2 JointLength;
        [Range(0f, 0.3f), Tooltip("Random turn of the discs and joints.")]
        public float Scatter;
        [Range(0f, 1.5f), Tooltip("Largest turn of a joint in the plane of the face, in radians.")]
        public float JointTurn;
        public int Seed;

        /// <summary>Adds the discs and joints of this set to a list of fractures.</summary>
        public readonly void AddTo(List<RockFracture> fractures, Vector3 boxSize)
        {
            var random = new Unity.Mathematics.Random((uint)Seed * 2654435761u + 11u);
            float3 face = math.normalize((float3)(Vector3)Face);
            float3 half = (float3)(Vector3)boxSize * 0.5f;
            float3 helper = math.abs(face.y) < 0.9f ? new float3(0f, 1f, 0f) : new float3(1f, 0f, 0f);
            float3 first = math.normalize(math.cross(helper, face));
            float3 second = math.cross(face, first);
            float reachFirst = math.dot(math.abs(first), half);
            float reachSecond = math.dot(math.abs(second), half);
            float surface = math.dot(math.abs(face), half);
            float depth = Layers * LayerThickness;

            for (int layer = 1; layer <= Layers; layer++)
            {
                for (int index = 0; index < Discs; index++)
                {
                    float level = surface - (layer + random.NextFloat(-0.2f, 0.2f)) * LayerThickness;
                    float3 center = face * level + first * random.NextFloat(-reachFirst, reachFirst) +
                                    second * random.NextFloat(-reachSecond, reachSecond);
                    float3 normal = math.normalize(face + random.NextFloat3Direction() * Scatter * 0.1f);
                    float3 along = math.normalize(math.cross(normal, second));
                    fractures.Add(new RockFracture
                    {
                        Center = center, Along = along, Normal = normal,
                        HalfLength = random.NextFloat(DiscSize.x, DiscSize.y),
                        HalfWidth = random.NextFloat(DiscSize.x, DiscSize.y), StepFade = 1f
                    });
                }
            }

            for (int index = 0; index < Joints; index++)
            {
                float3 center = face * (surface - 0.5f * depth) + first * random.NextFloat(-reachFirst, reachFirst) +
                                second * random.NextFloat(-reachSecond, reachSecond);
                // Each joint has its own angle in the face, near two families, so plates have corners but no grid.
                float angle = (random.NextBool() ? 0f : math.PI * 0.5f) + random.NextFloat(-1f, 1f) * JointTurn;
                float3 family = first * math.cos(angle) + second * math.sin(angle);
                float3 other = math.cross(face, family);
                float3 normal = math.normalize(family + (random.NextFloat3Direction() - face) * Scatter);
                fractures.Add(new RockFracture
                {
                    Center = center, Along = other, Normal = normal, HalfLength = random.NextFloat(JointLength.x, JointLength.y),
                    HalfWidth = depth, StepFade = 1f
                });
            }
        }
    }

    /// <summary>
    /// A rock made of solid blocks, after Paris et al. 2020, "Modeling Rocky Scenery using Implicit Blocks". Two samples are in one
    /// block when no fracture lies between them. Each block is an intersection of planes, and the face of a block that lies on a
    /// fracture is that fracture. So a large face is one plane, and a crack is a real gap between two solids.
    /// </summary>
    public sealed class RockBlockLayout : IDisposable
    {
        // Fixed directions of the outline of a block, both ways.
        private const int OutlineDirections = 9;
        // Largest number of fracture planes on one block.
        private const int FracturePlanes = 8;
        private const int PlanesPerBlock = OutlineDirections * 2 + FracturePlanes;
        // A block with fewer samples does not exist.
        private const int SmallestBlock = 6;
        private const int MergeRounds = 3;
        // Each chip is one floor and these walls.
        private const int ChipWalls = 5;
        private const int PlanesPerChip = ChipWalls + 1;
        private const int MostChipsOnFace = 24;
        // A channel is a wedge: two walls, a floor and two ends.
        private const int PlanesPerChannel = 5;
        private const float BinSize = 0.8f;

        private struct Disc
        {
            public float3 Center;
            public float3 Along;
            public float3 Side;
            public float3 Normal;
            public float HalfLength;
            public float HalfWidth;
            public float Gap;

            public readonly bool Crosses(float3 a, float3 b)
            {
                float fromA = math.dot(a - Center, Normal);
                float fromB = math.dot(b - Center, Normal);
                if (fromA * fromB >= 0f) return false;
                float3 hit = a + (b - a) * (fromA / (fromA - fromB)) - Center;
                return math.abs(math.dot(hit, Along)) <= HalfLength && math.abs(math.dot(hit, Side)) <= HalfWidth;
            }
        }

        // A sample joins a block when no disc lies between it and a neighbor, and none between it and the first sample. The second
        // test keeps a block nearly convex, so a crack that ends in the rock still divides the rock around its end.
        [BurstCompile(CompileSynchronously = true)]
        private struct ClusterJob : IJob
        {
            [ReadOnly] public NativeArray<float3> Points;
            [ReadOnly] public NativeArray<Disc> Discs;
            [ReadOnly] public NativeArray<int> Order;
            public NativeArray<int> Labels;
            public NativeReference<int> Count;
            public int3 Size;
            public float MaxReachSquared;
            public int MergeBelow;

            public void Execute()
            {
                var queue = new NativeArray<int>(Points.Length, Allocator.Temp);
                int label = 0;
                for (int position = 0; position < Order.Length; position++)
                {
                    int seed = Order[position];
                    if (Labels[seed] >= 0) continue;
                    float3 start = Points[seed];
                    int head = 0;
                    int tail = 1;
                    queue[0] = seed;
                    Labels[seed] = label;
                    while (head < tail)
                    {
                        int index = queue[head++];
                        int3 cell = new int3(index % Size.x, index / Size.x % Size.y, index / (Size.x * Size.y));
                        for (int side = 0; side < 6; side++)
                        {
                            int3 next = cell;
                            next[side / 2] += side % 2 == 0 ? 1 : -1;
                            if (math.any(next < 0) || math.any(next >= Size)) continue;
                            int other = next.x + Size.x * (next.y + Size.y * next.z);
                            if (Labels[other] >= 0) continue;
                            float3 point = Points[other];
                            if (math.distancesq(point, start) > MaxReachSquared) continue;
                            if (Blocked(Points[index], point) || Blocked(start, point)) continue;
                            Labels[other] = label;
                            queue[tail++] = other;
                        }
                    }
                    label++;
                }
                Count.Value = label;
                for (int round = 0; MergeBelow > 0 && round < MergeRounds; round++) MergeFragments(label);
            }

            private void MergeFragments(int labelCount)
            {
                var counts = new NativeArray<int>(labelCount, Allocator.Temp);
                var targets = new NativeArray<int>(labelCount, Allocator.Temp);
                for (int index = 0; index < Labels.Length; index++) counts[Labels[index]]++;
                for (int label = 0; label < labelCount; label++) targets[label] = -1;

                for (int index = 0; index < Labels.Length; index++)
                {
                    int label = Labels[index];
                    if (counts[label] >= MergeBelow || targets[label] >= 0) continue;
                    int3 cell = new int3(index % Size.x, index / Size.x % Size.y, index / (Size.x * Size.y));
                    for (int side = 0; side < 6; side++)
                    {
                        int3 next = cell;
                        next[side / 2] += side % 2 == 0 ? 1 : -1;
                        if (math.any(next < 0) || math.any(next >= Size)) continue;
                        int other = next.x + Size.x * (next.y + Size.y * next.z);
                        int neighbor = Labels[other];
                        if (neighbor == label || counts[neighbor] < MergeBelow) continue;
                        // Never across a fracture: the plates on a large core would become part of the core.
                        if (Blocked(Points[index], Points[other])) continue;
                        targets[label] = neighbor;
                    }
                }

                for (int index = 0; index < Labels.Length; index++)
                {
                    int target = targets[Labels[index]];
                    if (target >= 0) Labels[index] = target;
                }
            }

            private readonly bool Blocked(float3 a, float3 b)
            {
                for (int index = 0; index < Discs.Length; index++)
                {
                    if (Discs[index].Crosses(a, b)) return true;
                }
                return false;
            }
        }

        [BurstCompile(CompileSynchronously = true)]
        private struct PlaneJob : IJob
        {
            [ReadOnly] public NativeArray<float3> Points;
            [ReadOnly] public NativeArray<int> Labels;
            [ReadOnly] public NativeArray<Disc> Discs;
            [ReadOnly] public NativeArray<float3> Directions;
            public NativeList<float4> Planes;
            public NativeList<float3> Lows;
            public NativeList<float3> Highs;
            public NativeList<float4> ChipPlanes;
            public NativeList<int> ChipStarts;
            [ReadOnly] public NativeArray<int> MassOfLabel;
            [ReadOnly] public NativeArray<float3x3> MassTurn;
            [ReadOnly] public NativeArray<float3> MassPivot;
            [ReadOnly] public NativeArray<float3> MassShift;
            public RockBlockSettings Settings;
            public float3 Half;
            public int LabelCount;
            private int _mass;

            public void Execute()
            {
                var random = new Unity.Mathematics.Random((uint)Settings.Seed * 2654435761u + 3u);
                // Chips have their own numbers, so a change of the chips does not change which plates fall off.
                var chipRandom = new Unity.Mathematics.Random((uint)Settings.Seed * 2654435761u + 7u);
                var starts = new NativeArray<int>(LabelCount + 1, Allocator.Temp);
                for (int index = 0; index < Labels.Length; index++) starts[Labels[index] + 1]++;
                for (int label = 0; label < LabelCount; label++) starts[label + 1] += starts[label];
                var offsets = new NativeArray<int>(starts, Allocator.Temp);
                var sorted = new NativeArray<float3>(Points.Length, Allocator.Temp);
                for (int index = 0; index < Labels.Length; index++) sorted[offsets[Labels[index]]++] = Points[index];

                var plane = new NativeArray<float4>(PlanesPerBlock, Allocator.Temp);
                var distances = new NativeArray<float>(FracturePlanes, Allocator.Temp);
                for (int label = 0; label < LabelCount; label++)
                {
                    int first = starts[label];
                    int count = starts[label + 1] - first;
                    if (count < SmallestBlock) continue;

                    float3 low = float.MaxValue;
                    float3 high = float.MinValue;
                    for (int index = 0; index < count; index++)
                    {
                        low = math.min(low, sorted[first + index]);
                        high = math.max(high, sorted[first + index]);
                    }

                    // The face of the box that this block touches, if any.
                    float3 outward = float3.zero;
                    float thickness = float.MaxValue;
                    float3 toHigh = Half - high;
                    float3 toLow = Half + low;
                    for (int axis = 0; axis < 3; axis++)
                    {
                        float span = high[axis] - low[axis] + Settings.Spacing;
                        if (toHigh[axis] < Settings.Spacing && span < thickness)
                        {
                            thickness = span;
                            outward = float3.zero;
                            outward[axis] = 1f;
                        }
                        if (toLow[axis] < Settings.Spacing && span < thickness)
                        {
                            thickness = span;
                            outward = float3.zero;
                            outward[axis] = -1f;
                        }
                    }
                    bool onSurface = math.lengthsq(outward) > 0f;
                    if (onSurface && thickness <= Settings.PlateThickness && random.NextFloat() < Settings.Missing) continue;

                    float inward = onSurface ? math.pow(random.NextFloat(), 2.5f) * Settings.Relief : 0f;
                    float3 move = -outward * inward;
                    int written = 0;
                    for (int direction = 0; direction < OutlineDirections; direction++)
                    {
                        float3 axis = Directions[direction];
                        float highest = float.MinValue;
                        float lowest = float.MaxValue;
                        for (int index = 0; index < count; index++)
                        {
                            float along = math.dot(sorted[first + index], axis);
                            highest = math.max(highest, along);
                            lowest = math.min(lowest, along);
                        }
                        // Diagonal planes stay well outside the samples. Near the samples they cut a chamfer along each block edge.
                        float pad = (direction < 3 ? 0.75f : 2.5f) * Settings.Spacing;
                        plane[written++] = new float4(axis, highest + pad);
                        plane[written++] = new float4(-axis, -lowest + pad);
                    }

                    // The nearest fractures become exact faces of the block.
                    int found = 0;
                    for (int index = 0; index < Discs.Length; index++)
                    {
                        Disc disc = Discs[index];
                        float nearest = float.MaxValue;
                        float farthest = float.MinValue;
                        bool beside = false;
                        for (int sample = 0; sample < count; sample++)
                        {
                            float3 offset = sorted[first + sample] - disc.Center;
                            float height = math.dot(offset, disc.Normal);
                            nearest = math.min(nearest, height);
                            farthest = math.max(farthest, height);
                            beside |= math.abs(height) < 2f * Settings.Spacing &&
                                       math.abs(math.dot(offset, disc.Along)) <= disc.HalfLength &&
                                       math.abs(math.dot(offset, disc.Side)) <= disc.HalfWidth;
                        }
                        if (!beside || (nearest < 0f && farthest > 0f)) continue;

                        float distance = math.min(math.abs(nearest), math.abs(farthest));
                        float sign = nearest >= 0f ? -1f : 1f;
                        float3 normal = disc.Normal * sign;
                        var value = new float4(normal, math.dot(disc.Center, normal) - 0.5f * (Settings.Gap + disc.Gap));
                        int slot = found;
                        if (found == FracturePlanes)
                        {
                            slot = 0;
                            for (int other = 1; other < FracturePlanes; other++)
                            {
                                if (distances[other] > distances[slot]) slot = other;
                            }
                            if (distances[slot] <= distance) continue;
                        }
                        else
                        {
                            found++;
                        }
                        distances[slot] = distance;
                        plane[OutlineDirections * 2 + slot] = value;
                    }
                    for (int slot = found; slot < FracturePlanes; slot++)
                    {
                        plane[OutlineDirections * 2 + slot] = new float4(0f, 1f, 0f, 1e6f);
                    }

                    _mass = MassOfLabel[label];
                    for (int index = 0; index < PlanesPerBlock; index++)
                    {
                        float4 value = plane[index];
                        value.w += math.dot(value.xyz, move);
                        Planes.Add(Place(value));
                    }
                    ChipStarts.Add(ChipPlanes.Length / PlanesPerChip);
                    AddChips(ref chipRandom, plane, sorted, first, count, low, high, move);
                    // The tilt of a mass moves its blocks by less than this margin.
                    float3 shift = MassShift[_mass];
                    Lows.Add(low + math.min(shift, 0f) - 0.5f);
                    Highs.Add(high + math.max(shift, 0f) + 0.5f);
                }
                ChipStarts.Add(ChipPlanes.Length / PlanesPerChip);
            }

            // A chip is a convex piece of one block, on the rim of an open face: a floor parallel to the face and flat walls. So it
            // has sharp edges, it stays on one side of a crack, and it can only touch a face where the face ends.
            private void AddChips(ref Unity.Mathematics.Random random, NativeArray<float4> plane, NativeArray<float3> sorted, int first,
                int count, float3 low, float3 high, float3 move)
            {
                if (Settings.ChipDensity <= 0f) return;
                for (int axis = 0; axis < 3; axis++)
                {
                    for (int sign = -1; sign <= 1; sign += 2)
                    {
                        float reach = sign > 0 ? high[axis] : -low[axis];
                        if (Half[axis] - reach >= Settings.Spacing) continue;
                        float3 outward = float3.zero;
                        outward[axis] = sign;
                        int sideA = (axis + 1) % 3;
                        int sideB = (axis + 2) % 3;
                        float perimeter = 2f * (high[sideA] - low[sideA] + high[sideB] - low[sideB]);
                        int chips = math.min((int)(perimeter * Settings.ChipDensity + random.NextFloat()), MostChipsOnFace);
                        for (int chip = 0; chip < chips; chip++)
                        {
                            AddChip(ref random, plane, sorted[first + random.NextInt(count)], outward, axis, sign, move);
                        }
                    }
                }
            }

            private void AddChip(ref Unity.Mathematics.Random random, NativeArray<float4> plane, float3 sample, float3 outward, int axis,
                int sign, float3 move)
            {
                float level = Half[axis];
                float3 start = sample;
                start[axis] = sign * (level - 0.01f);
                float angle = random.NextFloat(2f * math.PI);
                int sideA = (axis + 1) % 3;
                int sideB = (axis + 2) % 3;
                float3 first = float3.zero;
                float3 second = float3.zero;
                first[sideA] = 1f;
                second[sideB] = 1f;
                float3 direction = first * math.cos(angle) + second * math.sin(angle);

                // Walks in the face to the first plane of the block, or to the end of the box.
                float walk = float.MaxValue;
                float3 rimNormal = direction;
                for (int index = 0; index < PlanesPerBlock; index++)
                {
                    float4 value = plane[index];
                    float slope = math.dot(value.xyz, direction);
                    if (slope <= 1e-4f) continue;
                    float distance = (value.w - math.dot(value.xyz, start)) / slope;
                    if (distance < walk)
                    {
                        walk = distance;
                        rimNormal = value.xyz;
                    }
                }
                for (int side = 0; side < 2; side++)
                {
                    float3 normal = side == 0 ? first : second;
                    int boxAxis = side == 0 ? sideA : sideB;
                    float slope = direction[boxAxis];
                    float distance = (Half[boxAxis] - start[boxAxis] * math.sign(slope)) / math.max(math.abs(slope), 1e-4f);
                    if (distance < walk)
                    {
                        walk = distance;
                        rimNormal = normal * math.sign(slope);
                    }
                }
                if (walk <= 0f || walk == float.MaxValue) return;
                float3 rim = start + direction * walk;

                float3 across = rimNormal - outward * math.dot(rimNormal, outward);
                if (math.lengthsq(across) < 1e-4f) return;
                across = math.normalize(across);
                float3 along = math.cross(outward, across);

                float width = math.lerp(Settings.ChipSize.x, Settings.ChipSize.y, math.pow(random.NextFloat(), 2f));
                float stretch = random.NextFloat(1.3f, 2.6f);
                float depth = Settings.ChipDepth * random.NextInt(1, 3);
                float3 floorNormal = math.normalize(-outward + (across * random.NextFloat(-1f, 1f) + along * random.NextFloat(-1f, 1f)) * 0.06f);
                float3 onFloor = rim - outward * depth;
                AddChipPlane(floorNormal, math.dot(floorNormal, onFloor), move);

                float turn = random.NextFloat(2f * math.PI);
                for (int wall = 0; wall < ChipWalls; wall++)
                {
                    float wallAngle = turn + (wall + random.NextFloat(-0.3f, 0.3f)) * 2f * math.PI / ChipWalls;
                    float2 round = new float2(math.cos(wallAngle), math.sin(wallAngle));
                    float3 toWall = along * round.x * stretch + across * round.y;
                    toWall = math.normalize(toWall);
                    // The walls lean out, as a piece that breaks off a face leaves a wider opening at the face.
                    float3 normal = math.normalize(toWall - outward * 0.4f);
                    float reach = width * random.NextFloat(0.7f, 1.2f) * (1f + (stretch - 1f) * round.x * round.x);
                    AddChipPlane(normal, math.dot(normal, rim + toWall * reach), move);
                }
            }

            private void AddChipPlane(float3 normal, float distance, float3 move)
            {
                ChipPlanes.Add(Place(new float4(normal, distance + math.dot(normal, move))));
            }

            // Moves a plane with the mass of the current block.
            private readonly float4 Place(float4 value)
            {
                float3 pivot = MassPivot[_mass];
                float3 normal = math.mul(MassTurn[_mass], value.xyz);
                return new float4(normal, value.w - math.dot(value.xyz, pivot) + math.dot(normal, pivot + MassShift[_mass]));
            }
        }

        [BurstCompile(CompileSynchronously = true)]
        private struct FieldJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float4> Planes;
            [ReadOnly] public NativeArray<float4> ChipPlanes;
            [ReadOnly] public NativeArray<int> ChipStarts;
            [ReadOnly] public NativeArray<float4> ChannelPlanes;
            [ReadOnly] public NativeArray<float> SurfaceDepth;
            [ReadOnly] public NativeArray<int3> SurfaceMaps;
            public bool SurfaceOnly;
            [ReadOnly] public NativeArray<int> BinStarts;
            [ReadOnly] public NativeArray<int> BinItems;
            [NativeDisableParallelForRestriction, WriteOnly] public NativeArray<float> Field;
            public int3 Size;
            public int3 Bins;
            public float3 Origin;
            public float3 Center;
            public float3 Half;
            public float Room;
            public float3 BinLow;
            public float Cell;
            public float Core;
            public float Warp;
            public float WarpScale;

            public void Execute(int z)
            {
                for (int y = 0; y < Size.y; y++)
                {
                    for (int x = 0; x < Size.x; x++)
                    {
                        float3 position = Origin + new float3(x, y, z) * Cell;
                        if (SurfaceOnly)
                        {
                            Field[x + Size.x * (y + Size.y * z)] = SurfaceField(position - Center);
                            continue;
                        }
                        float3 outside = math.abs(position - Center) - Half - Room;
                        float box = math.cmax(outside);
                        Field[x + Size.x * (y + Size.y * z)] = box > 0.5f || box < -Core - 0.3f ? box + Core * (box < 0f ? 1f : 0f)
                            : Sample(position, box);
                    }
                }
            }

            // Each face is a plane moved by its map. Maps hold 1 row per cell and index 0 is the low corner of the face.
            private readonly float SurfaceField(float3 local)
            {
                float result = float.MinValue;
                for (int axis = 0; axis < 3; axis++)
                {
                    for (int side = 0; side < 2; side++)
                    {
                        float sign = side == 0 ? -1f : 1f;
                        float plane = sign * local[axis] - Half[axis];
                        int3 map = SurfaceMaps[axis * 2 + side];
                        if (map.x >= 0)
                        {
                            int a = (axis + 1) % 3;
                            int b = (axis + 2) % 3;
                            float column = math.clamp((local[a] + Half[a]) / (2f * Half[a]) * map.y - 0.5f, 0f, map.y - 1.001f);
                            float row = math.clamp((local[b] + Half[b]) / (2f * Half[b]) * map.z - 0.5f, 0f, map.z - 1.001f);
                            int left = (int)column;
                            int low = (int)row;
                            int first = map.x + left + low * map.y;
                            float lower = math.lerp(SurfaceDepth[first], SurfaceDepth[first + 1], column - left);
                            float upper = math.lerp(SurfaceDepth[first + map.y], SurfaceDepth[first + map.y + 1], column - left);
                            plane -= math.lerp(lower, upper, row - low);
                        }
                        result = math.max(result, plane);
                    }
                }
                return result;
            }

            private readonly float Sample(float3 position, float box)
            {
                float3 location = position * WarpScale;
                float3 warped = position + Warp * new float3(noise.snoise(location), noise.snoise(location + 17.3f),
                    noise.snoise(location + 41.9f));
                warped += 0.25f * Warp * new float3(noise.snoise(location * 3.7f + 5.1f), noise.snoise(location * 3.7f + 23.7f),
                    noise.snoise(location * 3.7f + 61.3f));

                int3 bin = math.clamp((int3)math.floor((warped - BinLow) / BinSize), 0, Bins - 1);
                int cell = bin.x + Bins.x * (bin.y + Bins.y * bin.z);
                float best = 1f;
                for (int item = BinStarts[cell]; item < BinStarts[cell + 1]; item++)
                {
                    int block = BinItems[item];
                    int first = block * PlanesPerBlock;
                    float inside = float.MinValue;
                    for (int index = 0; index < PlanesPerBlock; index++)
                    {
                        float4 plane = Planes[first + index];
                        inside = math.max(inside, math.dot(plane.xyz, warped) - plane.w);
                        if (inside >= best) break;
                    }
                    for (int chip = ChipStarts[block]; chip < ChipStarts[block + 1] && inside < best; chip++)
                    {
                        float inChip = float.MinValue;
                        for (int index = 0; index < PlanesPerChip; index++)
                        {
                            float4 plane = ChipPlanes[chip * PlanesPerChip + index];
                            inChip = math.max(inChip, math.dot(plane.xyz, warped) - plane.w);
                        }
                        inside = math.max(inside, -inChip);
                    }
                    best = math.min(best, inside);
                }
                float result = math.max(math.min(best, box + Core), box);
                for (int channel = 0; channel < ChannelPlanes.Length; channel += PlanesPerChannel)
                {
                    float inChannel = float.MinValue;
                    for (int index = 0; index < PlanesPerChannel; index++)
                    {
                        float4 plane = ChannelPlanes[channel + index];
                        inChannel = math.max(inChannel, math.dot(plane.xyz, warped) - plane.w);
                        if (inChannel > 0.5f) break;
                    }
                    result = math.max(result, -inChannel);
                }
                return result;
            }
        }

        private NativeArray<float4> _planes;
        private NativeArray<float4> _channelPlanes;
        private NativeArray<float> _surfaceDepth;
        private NativeArray<int3> _surfaceMaps;
        private NativeArray<float4> _chipPlanes;
        private NativeArray<int> _chipStarts;
        private NativeArray<int> _binStarts;
        private NativeArray<int> _binItems;
        private readonly RockBlockSettings _settings;
        private readonly float3 _center;
        private readonly float3 _half;
        private float3 _binLow;
        private int3 _bins;

        /// <summary>Number of blocks that exist after the fragments and the missing plates go.</summary>
        public int BlockCount { get; private set; }

        /// <summary>Number of masses that move as one solid.</summary>
        public int MassCount { get; private set; }

        /// <summary>Number of chips on all blocks.</summary>
        public int ChipCount { get; private set; }

        /// <summary>Finds the blocks of a box.</summary>
        /// <param name="settings">The settings of the blocks.</param>
        /// <param name="center">Center of the box, in meters.</param>
        /// <param name="size">Full size of the box, in meters.</param>
        /// <param name="fractures">The fractures. Only their planes and sizes are used.</param>
        /// <param name="massFractures">The first fractures of the list. They divide the box into masses that move as one.</param>
        /// <param name="faceDepths">Depth maps of the box faces. They set how far each mass moves into the rock.</param>
        /// <param name="channels">Wedges cut into the rock. Center is on the face, Normal is across, Step is depth, CrackWidth is width.</param>
        public RockBlockLayout(in RockBlockSettings settings, Vector3 center, Vector3 size, IReadOnlyList<RockFracture> fractures,
            int massFractures = 0, IReadOnlyList<RockFaceDepth> faceDepths = null, IReadOnlyList<RockFracture> channels = null)
        {
            _settings = settings;
            _center = center;
            _half = (float3)(Vector3)size * 0.5f;
            MakeChannels(channels);
            MakeSurface(settings.SurfaceOnly ? faceDepths : null);

            var cells = (int3)math.max(math.ceil((float3)(Vector3)size / settings.Spacing), 2f);
            int count = cells.x * cells.y * cells.z;
            var random = new Unity.Mathematics.Random((uint)settings.Seed * 2654435761u + 5u);
            using var points = new NativeArray<float3>(count, Allocator.TempJob);
            using var labels = new NativeArray<int>(count, Allocator.TempJob);
            using var order = new NativeArray<int>(count, Allocator.TempJob);
            var pointWrite = points;
            var labelWrite = labels;
            var orderWrite = order;
            float3 step = (float3)(Vector3)size / cells;
            for (int index = 0; index < count; index++)
            {
                var cell = new int3(index % cells.x, index / cells.x % cells.y, index / (cells.x * cells.y));
                // The block search works in box space: the box is centered on 0.
                pointWrite[index] = -_half + ((float3)cell + 0.5f + (random.NextFloat3() - 0.5f) * 0.6f) * step;
                labelWrite[index] = -1;
                orderWrite[index] = index;
            }
            for (int index = count - 1; index > 0; index--)
            {
                int other = random.NextInt(index + 1);
                (orderWrite[index], orderWrite[other]) = (orderWrite[other], orderWrite[index]);
            }

            using var discs = new NativeArray<Disc>(fractures.Count, Allocator.TempJob);
            var discWrite = discs;
            for (int index = 0; index < fractures.Count; index++)
            {
                RockFracture fracture = fractures[index];
                float3 normal = math.normalize((float3)(Vector3)fracture.Normal);
                float3 along = math.normalize((float3)(Vector3)fracture.Along);
                along = math.normalize(along - normal * math.dot(along, normal));
                discWrite[index] = new Disc
                {
                    Center = (float3)(Vector3)fracture.Center - (float3)(Vector3)center, Along = along,
                    Side = math.cross(normal, along), Normal = normal, HalfLength = fracture.HalfLength,
                    HalfWidth = fracture.HalfWidth, Gap = fracture.CrackWidth
                };
            }

            using var labelCount = new NativeReference<int>(Allocator.TempJob);
            new ClusterJob
            {
                Points = points, Discs = discs, Order = order, Labels = labels, Count = labelCount, Size = cells,
                MaxReachSquared = settings.MaxBlockSize * settings.MaxBlockSize, MergeBelow = settings.MergeBelow
            }.Run();

            using var massLabels = new NativeArray<int>(count, Allocator.TempJob);
            using var massCount = new NativeReference<int>(Allocator.TempJob);
            if (massFractures > 0)
            {
                var massWrite = massLabels;
                for (int index = 0; index < count; index++) massWrite[index] = -1;
                new ClusterJob
                {
                    Points = points, Discs = discs.GetSubArray(0, massFractures), Order = order, Labels = massLabels, Count = massCount,
                    Size = cells, MaxReachSquared = 4f * settings.MaxBlockSize * settings.MaxBlockSize
                }.Run();
            }
            else
            {
                var countWrite = massCount;
                countWrite.Value = 1;
            }
            MassCount = massCount.Value;
            using var massTurn = new NativeArray<float3x3>(MassCount, Allocator.TempJob);
            using var massPivot = new NativeArray<float3>(MassCount, Allocator.TempJob);
            using var massShift = new NativeArray<float3>(MassCount, Allocator.TempJob);
            using var massOfLabel = new NativeArray<int>(labelCount.Value, Allocator.TempJob);
            var labelMass = massOfLabel;
            for (int index = 0; index < count; index++) labelMass[labels[index]] = massLabels[index];
            PlaceMasses(massFractures > 0, points, massLabels, faceDepths, massTurn, massPivot, massShift);

            using var directions = new NativeArray<float3>(OutlineDirections, Allocator.TempJob);
            var directionWrite = directions;
            directionWrite[0] = new float3(1f, 0f, 0f);
            directionWrite[1] = new float3(0f, 1f, 0f);
            directionWrite[2] = new float3(0f, 0f, 1f);
            directionWrite[3] = math.normalize(new float3(1f, 1f, 0f));
            directionWrite[4] = math.normalize(new float3(1f, -1f, 0f));
            directionWrite[5] = math.normalize(new float3(1f, 0f, 1f));
            directionWrite[6] = math.normalize(new float3(1f, 0f, -1f));
            directionWrite[7] = math.normalize(new float3(0f, 1f, 1f));
            directionWrite[8] = math.normalize(new float3(0f, 1f, -1f));

            using var planes = new NativeList<float4>(Allocator.TempJob);
            using var lows = new NativeList<float3>(Allocator.TempJob);
            using var highs = new NativeList<float3>(Allocator.TempJob);
            using var chipPlanes = new NativeList<float4>(Allocator.TempJob);
            using var chipStarts = new NativeList<int>(Allocator.TempJob);
            new PlaneJob
            {
                Points = points, Labels = labels, Discs = discs, Directions = directions, Planes = planes, Lows = lows, Highs = highs,
                ChipPlanes = chipPlanes, ChipStarts = chipStarts, MassOfLabel = massOfLabel, MassTurn = massTurn, MassPivot = massPivot,
                MassShift = massShift, Settings = settings, Half = _half, LabelCount = labelCount.Value
            }.Run();

            BlockCount = lows.Length;
            _planes = new NativeArray<float4>(planes.AsArray(), Allocator.Persistent);
            // The planes lie in box space, and the field works in rock space.
            for (int index = 0; index < _planes.Length; index++)
            {
                float4 plane = _planes[index];
                plane.w += math.dot(plane.xyz, _center);
                _planes[index] = plane;
            }

            ChipCount = chipPlanes.Length / PlanesPerChip;
            _chipPlanes = new NativeArray<float4>(chipPlanes.AsArray(), Allocator.Persistent);
            _chipStarts = new NativeArray<int>(chipStarts.AsArray(), Allocator.Persistent);
            for (int index = 0; index < _chipPlanes.Length; index++)
            {
                float4 plane = _chipPlanes[index];
                plane.w += math.dot(plane.xyz, _center);
                _chipPlanes[index] = plane;
            }

            float margin = settings.MassRoom + settings.Spacing * 2f + settings.Warp * 1.5f;
            _binLow = _center - _half - margin;
            _bins = (int3)math.ceil((_half * 2f + margin * 2f) / BinSize);
            var lists = new List<int>[_bins.x * _bins.y * _bins.z];
            int total = 0;
            for (int block = 0; block < lows.Length; block++)
            {
                var from = (int3)math.floor((lows[block] + _center - margin - _binLow) / BinSize);
                var to = (int3)math.floor((highs[block] + _center + margin - _binLow) / BinSize);
                from = math.clamp(from, 0, _bins - 1);
                to = math.clamp(to, 0, _bins - 1);
                for (int z = from.z; z <= to.z; z++)
                {
                    for (int y = from.y; y <= to.y; y++)
                    {
                        for (int x = from.x; x <= to.x; x++)
                        {
                            int bin = x + _bins.x * (y + _bins.y * z);
                            lists[bin] ??= new List<int>();
                            lists[bin].Add(block);
                            total++;
                        }
                    }
                }
            }

            _binStarts = new NativeArray<int>(lists.Length + 1, Allocator.Persistent);
            _binItems = new NativeArray<int>(math.max(total, 1), Allocator.Persistent);
            int written = 0;
            for (int bin = 0; bin < lists.Length; bin++)
            {
                _binStarts[bin] = written;
                if (lists[bin] == null) continue;
                foreach (int block in lists[bin]) _binItems[written++] = block;
            }
            _binStarts[lists.Length] = written;
        }

        private void MakeSurface(IReadOnlyList<RockFaceDepth> faceDepths)
        {
            int total = 0;
            _surfaceMaps = new NativeArray<int3>(6, Allocator.Persistent);
            for (int index = 0; index < 6; index++) _surfaceMaps[index] = new int3(-1, 0, 0);
            if (faceDepths != null)
            {
                foreach (RockFaceDepth map in faceDepths) total += map.Depth.Length;
            }
            _surfaceDepth = new NativeArray<float>(total, Allocator.Persistent);
            if (faceDepths == null) return;

            int start = 0;
            foreach (RockFaceDepth map in faceDepths)
            {
                float3 face = (Vector3)map.Face;
                int axis = math.abs(face.x) > 0.5f ? 0 : math.abs(face.y) > 0.5f ? 1 : 2;
                _surfaceMaps[axis * 2 + (face[axis] > 0f ? 1 : 0)] = new int3(start, map.Width, map.Height);
                NativeArray<float>.Copy(map.Depth, 0, _surfaceDepth, start, map.Depth.Length);
                start += map.Depth.Length;
            }
        }

        // The upper wall of a wedge is steep and the lower wall leans back, so a ledge overhangs the rock below it.
        private void MakeChannels(IReadOnlyList<RockFracture> channels)
        {
            int count = channels?.Count ?? 0;
            _channelPlanes = new NativeArray<float4>(count * PlanesPerChannel, Allocator.Persistent);
            for (int index = 0; index < count; index++)
            {
                RockFracture channel = channels[index];
                float3 along = math.normalize((float3)(Vector3)channel.Along);
                float3 across = math.normalize((float3)(Vector3)channel.Normal);
                if (across.y < 0f) across = -across;
                float3 outward = math.normalize(math.cross(across, along));
                float3 onFace = channel.Center;
                if (math.dot(onFace - _center, outward) < 0f) outward = -outward;

                float depth = math.max(channel.Step, 0.05f);
                float floorHalf = 0.12f * channel.CrackWidth;
                float steep = 0.2f * (0.5f * channel.CrackWidth - floorHalf) / depth;
                float lean = 1.8f * (0.5f * channel.CrackWidth - floorHalf) / depth;
                float taper = 0.3f * channel.HalfLength / depth;
                float3 floor = onFace - outward * depth;
                int first = index * PlanesPerChannel;
                _channelPlanes[first] = ChannelPlane(across - outward * steep, floor + across * floorHalf);
                _channelPlanes[first + 1] = ChannelPlane(-across - outward * lean, floor - across * floorHalf);
                _channelPlanes[first + 2] = ChannelPlane(-outward, floor);
                _channelPlanes[first + 3] = ChannelPlane(along - outward * taper, floor + along * 0.7f * channel.HalfLength);
                _channelPlanes[first + 4] = ChannelPlane(-along - outward * taper, floor - along * 0.7f * channel.HalfLength);
            }
        }

        private static float4 ChannelPlane(float3 normal, float3 point)
        {
            normal = math.normalize(normal);
            return new float4(normal, math.dot(normal, point));
        }

        // Each mass moves as one solid. A mass is deep where the map of each open face is deep, so real walls stand between masses.
        private void PlaceMasses(bool active, NativeArray<float3> points, NativeArray<int> massLabels,
            IReadOnlyList<RockFaceDepth> faceDepths, NativeArray<float3x3> turn, NativeArray<float3> pivot, NativeArray<float3> shift)
        {
            var random = new Unity.Mathematics.Random((uint)_settings.Seed * 2654435761u + 13u);
            int count = turn.Length;
            var low = new float3[count];
            var high = new float3[count];
            for (int mass = 0; mass < count; mass++)
            {
                low[mass] = float.MaxValue;
                high[mass] = float.MinValue;
                turn[mass] = float3x3.identity;
            }
            if (!active) return;

            for (int index = 0; index < points.Length; index++)
            {
                int mass = massLabels[index];
                low[mass] = math.min(low[mass], points[index]);
                high[mass] = math.max(high[mass], points[index]);
            }

            for (int mass = 0; mass < count; mass++)
            {
                float3 move = float3.zero;
                float3 spin = float3.zero;
                float3 faceCenters = float3.zero;
                int openFaces = 0;
                for (int axis = 0; axis < 3; axis++)
                {
                    for (int sign = -1; sign <= 1; sign += 2)
                    {
                        float reach = sign > 0 ? high[mass][axis] : -low[mass][axis];
                        if (_half[axis] - reach >= _settings.Spacing) continue;
                        float3 outward = float3.zero;
                        outward[axis] = sign;
                        RockFaceDepth map = FindDepth(faceDepths, outward);
                        float inward = _settings.MassMove * math.pow(random.NextFloat(), math.max(_settings.MassPower, 0.1f));
                        if (map != null) inward += _settings.MassDepth * AverageDepth(map, axis, low[mass], high[mass]);

                        // The mass takes the tangent plane of a surface that all masses share, so neighbors nearly fit.
                        float3 onFace = (low[mass] + high[mass]) * 0.5f;
                        onFace[axis] = sign * _half[axis];
                        inward += SharedHeight(onFace);
                        const float Step = 0.25f;
                        float3 slope = new float3(SharedHeight(onFace + new float3(Step, 0f, 0f)) - SharedHeight(onFace - new float3(Step, 0f, 0f)),
                            SharedHeight(onFace + new float3(0f, Step, 0f)) - SharedHeight(onFace - new float3(0f, Step, 0f)),
                            SharedHeight(onFace + new float3(0f, 0f, Step)) - SharedHeight(onFace - new float3(0f, 0f, Step))) / (2f * Step);
                        slope -= outward * math.dot(slope, outward);
                        spin += math.cross(outward, slope);
                        faceCenters += onFace;
                        openFaces++;
                        move -= outward * math.max(inward, -0.8f * _settings.MassRoom);
                    }
                }

                pivot[mass] = openFaces > 0 ? faceCenters / openFaces : (low[mass] + high[mass]) * 0.5f;
                shift[mass] = move;
                float angle = math.length(spin);
                if (angle > 1e-5f)
                {
                    turn[mass] = new float3x3(quaternion.AxisAngle(spin / angle, math.min(angle, _settings.MassTilt)));
                }
            }
        }

        private float SharedHeight(float3 position)
        {
            float3 broad = position / math.max(_settings.BroadSize, 0.1f) + _settings.Seed * 0.37f;
            float3 middle = position / math.max(_settings.MiddleSize, 0.1f) + 31.7f + _settings.Seed * 0.61f;
            return _settings.BroadHeight * noise.snoise(broad) + _settings.MiddleHeight * noise.snoise(middle);
        }

        private static RockFaceDepth FindDepth(IReadOnlyList<RockFaceDepth> faceDepths, float3 outward)
        {
            if (faceDepths == null) return null;
            foreach (RockFaceDepth map in faceDepths)
            {
                if (math.dot((float3)(Vector3)map.Face, outward) > 0.9f) return map;
            }
            return null;
        }

        private float AverageDepth(RockFaceDepth map, int axis, float3 low, float3 high)
        {
            const int Samples = 6;
            int sideA = (axis + 1) % 3;
            int sideB = (axis + 2) % 3;
            float total = 0f;
            for (int y = 0; y < Samples; y++)
            {
                for (int x = 0; x < Samples; x++)
                {
                    float a = math.lerp(low[sideA], high[sideA], (x + 0.5f) / Samples);
                    float b = math.lerp(low[sideB], high[sideB], (y + 0.5f) / Samples);
                    total += map.Sample((a + _half[sideA]) / (2f * _half[sideA]), (b + _half[sideB]) / (2f * _half[sideB]));
                }
            }
            return total / (Samples * Samples);
        }

        /// <summary>Writes the field of the blocks on a grid. The caller owns the array.</summary>
        internal NativeArray<float> Evaluate(int3 size, float3 origin, float cell)
        {
            var field = new NativeArray<float>(size.x * size.y * size.z, Allocator.TempJob);
            new FieldJob
            {
                Planes = _planes, ChipPlanes = _chipPlanes, ChipStarts = _chipStarts, ChannelPlanes = _channelPlanes,
                SurfaceDepth = _surfaceDepth, SurfaceMaps = _surfaceMaps, SurfaceOnly = _settings.SurfaceOnly, BinStarts = _binStarts, BinItems = _binItems, Field = field, Size = size, Bins = _bins, Origin = origin,
                Center = _center, Half = _half, Room = _settings.MassRoom, BinLow = _binLow, Cell = cell, Core = _settings.CoreDepth, Warp = _settings.Warp,
                WarpScale = 1f / math.max(_settings.WarpSize, 0.1f)
            }.Schedule(size.z, 1).Complete();
            return field;
        }

        public void Dispose()
        {
            if (_planes.IsCreated) _planes.Dispose();
            if (_channelPlanes.IsCreated) _channelPlanes.Dispose();
            if (_surfaceDepth.IsCreated) _surfaceDepth.Dispose();
            if (_surfaceMaps.IsCreated) _surfaceMaps.Dispose();
            if (_chipPlanes.IsCreated) _chipPlanes.Dispose();
            if (_chipStarts.IsCreated) _chipStarts.Dispose();
            if (_binStarts.IsCreated) _binStarts.Dispose();
            if (_binItems.IsCreated) _binItems.Dispose();
        }
    }
}
