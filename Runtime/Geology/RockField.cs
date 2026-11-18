using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace LoogaSoft.Terrain.Geology
{
    /// <summary>A solid from outside the generator: field samples on a grid, negative in rock. It replaces the base shape.</summary>
    public sealed class RockImportedField
    {
        public float[] Values;
        public int3 Size;
        public float3 Origin;
        public float Cell;

        /// <summary>Reads the samples on another grid with linear filtering. Samples past the grid are air. The caller owns the array.</summary>
        internal NativeArray<float> Evaluate(int3 size, float3 origin, float cell)
        {
            var values = new float[size.x * size.y * size.z];
            System.Threading.Tasks.Parallel.For(0, size.z, z =>
            {
                for (int y = 0; y < size.y; y++)
                {
                    for (int x = 0; x < size.x; x++)
                    {
                        values[x + size.x * (y + size.y * z)] = Sample((origin + new float3(x, y, z) * cell - Origin) / Cell);
                    }
                }
            });
            var field = new NativeArray<float>(values.Length, Allocator.Persistent);
            field.CopyFrom(values);
            return field;
        }

        private float Sample(float3 point)
        {
            var low = (int3)math.floor(point);
            if (math.any(low < 0) || math.any(low + 1 >= Size)) return 1f;
            float3 share = point - low;
            float At(int x, int y, int z) => Values[low.x + x + Size.x * (low.y + y + Size.y * (low.z + z))];
            float near = math.lerp(math.lerp(At(0, 0, 0), At(1, 0, 0), share.x), math.lerp(At(0, 1, 0), At(1, 1, 0), share.x), share.y);
            float far = math.lerp(math.lerp(At(0, 0, 1), At(1, 0, 1), share.x), math.lerp(At(0, 1, 1), At(1, 1, 1), share.x), share.y);
            return math.lerp(near, far, share.z);
        }
    }

    /// <summary>The operation of one rock field pass.</summary>
    public enum RockPassKind
    {
        /// <summary>Layered gradient noise. Moves the surface in and out.</summary>
        Noise,
        /// <summary>Up to three joint sets that split the rock into blocks.</summary>
        Joints,
        /// <summary>A cellular crack network. Cells can move and turn as plates or facets.</summary>
        Cells,
        /// <summary>Flat cuts in each cell. Removes rock and never adds rock, so block edges stay sharp.</summary>
        Cut,
        /// <summary>Fractal chips: turned boxes at several sizes, carved from a shell below the surface.</summary>
        Carve,
        /// <summary>The finite fractures of the recipe: planar discs that step the rock back and end in intact rock.</summary>
        Fractures,
        /// <summary>A blur of the field that rounds convex edges or fills cavities.</summary>
        Smooth,
        /// <summary>Moves the surface by a height tile, projected from the three axes.</summary>
        Displace,
        /// <summary>Removes thin parts, such as fins and strings, and rounds edges a little. Use it before the cuts.</summary>
        Open,
        /// <summary>A sequence of splits into blocks at their own depths. Cracks meet in T junctions.</summary>
        Split,
        /// <summary>
        /// Cracks that split the rock in order, as a rock breaks: the first crack splits the whole rock across its longest
        /// size, and each part splits again. Younger cracks end at older cracks in T junctions. The crack faces are rough.
        /// </summary>
        Cracks
    }

    /// <summary>Settings of a noise pass. A positive amount removes rock at high noise and adds rock at low noise.</summary>
    [Serializable]
    public struct RockNoiseSettings
    {
        [Tooltip("Usual largest change of the surface, in meters. A negative value turns the noise over.")]
        public float Amount;
        [Min(0.001f), Tooltip("Size of the first noise layer, in meters.")]
        public float Size;
        [Range(1, 8), Tooltip("Number of noise layers. Each layer is smaller than the layer before it.")]
        public int Layers;
        [Range(1.5f, 3f), Tooltip("Size ratio between two noise layers.")]
        public float SizeRatio;
        [Range(0f, 1f), Tooltip("Strength of each layer relative to the layer before it.")]
        public float Strength;
        [Range(0f, 1f), Tooltip("0 = round noise. 1 = creases where the noise crosses zero.")]
        public float Ridged;
        [Tooltip("Stretch of the noise along each axis, for example 1, 0.3, 1 for flat layers. 0 counts as 1.")]
        public Vector3 Stretch;
        [Min(0f), Tooltip("Largest wander of the noise, in meters.")]
        public float Warp;
        [Tooltip("Remove rock only. This cannot add loose parts.")]
        public bool CarveOnly;
        [Tooltip("Act only near the cell borders of the main cut, so its faces stay flat.")]
        public bool OnCellEdges;
        [Tooltip("Noise value of the middle of the clip step. Used when Clip Width is above 0.")]
        public float ClipLevel;
        [Min(0f), Tooltip("Half width of the clip step, in noise units. Above 0, the noise becomes 0 or 1 with a sharp step, " +
                          "so stretched noise gives strata with crisp ledges. 0 turns the clip off.")]
        public float ClipWidth;
        [Min(0f), Tooltip("Size of the smooth and rough patches, in meters. The patches change the small noise layers only, " +
                          "so the large form stays. 0 = no patches.")]
        public float PatchSize;
        [Range(0f, 1f), Tooltip("Difference between the patches. 1 = smooth patches keep 15 % of the small layers and rough " +
                                "patches get 150 %.")]
        public float PatchContrast;
    }

    /// <summary>
    /// Settings of a carve pass, after the subtractive form of "FBM detail in SDFs" (Quilez). Each octave has smaller boxes.
    /// </summary>
    [Serializable]
    public struct RockCarveSettings
    {
        [Tooltip("Turn of the stretched space, in degrees.")]
        public Vector3 Rotation;
        [Min(0.01f), Tooltip("Grid size of the first boxes, in meters.")]
        public float Size;
        [Tooltip("Stretch of the boxes along each axis, for example 1.5, 0.5, 1.5 for flat chips along the layers.")]
        public Vector3 Stretch;
        [Range(1, 8), Tooltip("Number of box sizes.")]
        public int Octaves;
        [Range(1.5f, 3f), Tooltip("Size ratio between two octaves.")]
        public float SizeRatio;
        [Range(0f, 1f), Tooltip("Share of the grid corners that have a box.")]
        public float Share;
        [Min(0f), Tooltip("Largest depth of the first octave below the surface, in meters.")]
        public float Depth;
        [Min(0f), Tooltip("Largest wander of the boxes, in meters.")]
        public float Warp;
        [Tooltip("Carve only near the cell borders of the main cut, so its faces stay flat.")]
        public bool OnCellEdges;
    }

    /// <summary>
    /// Settings of a joint pass. Joints are parallel planes of weakness. The first set (frame Y) cuts the rock into rows,
    /// the second set (frame X) cuts each row, and the third set (frame Z) cuts each column. Use a spacing of 0 to turn a
    /// set off: only Y gives beds, X and Z give columns, and all three give blocks.
    /// </summary>
    [Serializable]
    public struct RockJointSettings
    {
        [Tooltip("Turn of the joint frame, in degrees.")]
        public Vector3 Rotation;
        [Tooltip("Mean distance between the joints of each set, in meters. 0 turns the set off.")]
        public Vector3 Spacing;
        [Range(0f, 0.8f), Tooltip("Random move of each joint, as a share of the spacing.")]
        public float SpacingJitter;
        [Range(0f, 1f), Tooltip("0 = joints continue through the rows. 1 = joints start again in each row, like bricks.")]
        public float RowShift;
        [Tooltip("Open width of the joints of each set at the surface, in meters.")]
        public Vector3 Gap;
        [Min(0.01f), Tooltip("Depth where the joints close, in meters. The rock below this depth stays solid.")]
        public float Depth;
        [Range(0f, 1f), Tooltip("Share of the joint area that is open. Lower values make joints stop in the rock.")]
        public float Persistence;
        [Min(0f), Tooltip("Radius of the block edges, in meters.")]
        public float Rounding;
        [Range(0f, 15f), Tooltip("Largest random turn of each block, in degrees.")]
        public float Tilt;
        [Range(0f, 0.4f), Tooltip("Largest random slope of each block face. Blocks are then wedges, not boxes.")]
        public float FaceTilt;
        [Min(0f), Tooltip("Width of the smooth union where a joint is closed, in meters. Blocks there fuse into one mass.")]
        public float Blend;
        [Min(0f), Tooltip("Largest random move of the block faces, in meters.")]
        public float Relief;
        [Range(0f, 1f), Tooltip("0 = whole blocks, and the surface steps along the joints. 1 = blocks follow the surface.")]
        public float Follow;
        [Min(0f), Tooltip("Random change of the test that keeps a block near the surface, in meters.")]
        public float KeepJitter;
        [Min(0f), Tooltip("A block stays when its center is this far outside the surface, in meters. Larger values keep an " +
                          "authored silhouette whole.")]
        public float KeepMargin;
        [Range(0f, 1f), Tooltip("Share of the blocks that fell out.")]
        public float Missing;
        [Range(0f, 0.3f), Tooltip("Largest change of the size of a row. Rows then stand out and step back as whole layers.")]
        public float RowScale;
        [Tooltip("Write a random value of each block for the block color. The last pass with this setting wins.")]
        public bool TagBlocks;
        [Min(0f), Tooltip("Largest wander of the joints, in meters.")]
        public float Warp;
        [Min(0.01f), Tooltip("Size of the wander, in meters.")]
        public float WarpSize;
    }

    /// <summary>Settings of a cellular pass. Each layer has smaller cells that crack the cells of the layer before it.</summary>
    [Serializable]
    public struct RockCellSettings
    {
        [Min(0.01f), Tooltip("Mean size of the first cells, in meters.")]
        public float Size;
        [Tooltip("Stretch of the cells along each axis. 1 = no stretch.")]
        public Vector3 Stretch;
        [Range(1, 6), Tooltip("Number of cell layers.")]
        public int Layers;
        [Range(1.5f, 4f), Tooltip("Size ratio between two cell layers.")]
        public float SizeRatio;
        [Range(0f, 1f), Tooltip("Depth of each layer relative to the layer before it.")]
        public float Strength;
        [Range(0f, 1f), Tooltip("Random move of the cell points. 1 = irregular cells.")]
        public float Jitter;
        [Min(0.001f), Tooltip("Width of the crack shoulder, in meters.")]
        public float CrackWidth;
        [Tooltip("Depth of the cracks, in meters.")]
        public float CrackDepth;
        [Range(0.2f, 2f), Tooltip("Shoulder shape. Small values give round plates. 1 gives a straight V.")]
        public float CrackProfile;
        [Tooltip("Largest random move of each cell, in meters.")]
        public float CellOffset;
        [Range(0f, 1f), Tooltip("Largest slope of each cell. Sloped cells overlap like plates or chips.")]
        public float Facet;
        [Range(0f, 1f), Tooltip("1 = the pass moves only the sides of the rock. Tops and undersides stay flat.")]
        public float Horizontal;
        [Min(0f), Tooltip("Largest wander of the cracks, in meters.")]
        public float Warp;
    }

    /// <summary>Settings of a cut pass. Each layer has smaller cells than the layer before it.</summary>
    [Serializable]
    public struct RockCutSettings
    {
        [Min(0.01f), Tooltip("Mean size of the first cells, in meters.")]
        public float Size;
        [Tooltip("Stretch of the cells along each axis. 1 = no stretch.")]
        public Vector3 Stretch;
        [Range(1, 6), Tooltip("Number of cell layers.")]
        public int Layers;
        [Range(1.5f, 4f), Tooltip("Size ratio between two cell layers.")]
        public float SizeRatio;
        [Range(0f, 1f), Tooltip("Depth of each layer relative to the layer before it.")]
        public float Strength;
        [Range(0f, 1f), Tooltip("Random move of the cell points. 1 = irregular cells.")]
        public float Jitter;
        [Range(0f, 1f), Tooltip("Share of the cells that get a cut.")]
        public float Share;
        [Min(0f), Tooltip("Largest depth of a cut plane below the surface, in meters.")]
        public float Depth;
        [Min(0f), Tooltip("Shape of the depth. 0 or 1 = even. Above 1, most cells stay shallow and few go deep.")]
        public float DepthPower;
        [Min(0), Tooltip("Number of even depth levels. Plates then come off in layers, as on cleaved rock. 0 = any depth.")]
        public int DepthLevels;
        [Range(0f, 1.5f), Tooltip("Random turn of the cut planes. 0 = along the surface.")]
        public float Tilt;
        [Min(0f), Tooltip("Width of the crack along the cell borders, in meters.")]
        public float CrackWidth;
        [Min(0f), Tooltip("Depth of the crack along the cell borders, in meters.")]
        public float CrackDepth;
        [Min(0f), Tooltip("Largest wander of the cells, in meters. A large, slow wander turns straight cuts into slanted faces.")]
        public float Warp;
        [Min(0f), Tooltip("Size of the wander, in meters. 0 uses a size near the cell size.")]
        public float WarpSize;
        [Range(0f, 1f), Tooltip("0 = a crack on all cell borders. Higher values keep sparse cracks with solid rock between them.")]
        public float Sparsity;
        [Tooltip("This pass is the main cut: later passes with On Cell Edges act near its cell borders.")]
        public bool MainCut;
        [Min(0.01f), Tooltip("Width of the damage zone along the cell borders of a main cut, in meters.")]
        public float EdgeZone;
    }

    /// <summary>Settings of a smooth pass.</summary>
    [Serializable]
    public struct RockSmoothSettings
    {
        [Min(0.01f), Tooltip("Reach of the blur, in meters.")]
        public float Radius;
        [Range(0f, 1f), Tooltip("Share of the blur that takes rock from convex edges.")]
        public float Wear;
        [Range(0f, 1f), Tooltip("Share of the blur that fills cavities and cracks.")]
        public float Fill;
        [Range(0f, 1f), Tooltip("0 = same everywhere. 1 = only in patches.")]
        public float Patchy;
        [Min(0.01f), Tooltip("Size of the patches, in meters.")]
        public float PatchSize;
    }

    /// <summary>
    /// Settings of a split pass. The rock box divides again and again along its axes, and each block then moves back by its own
    /// depth. A split stops at the walls of the block that it divides, so cracks meet in T junctions.
    /// </summary>
    [Serializable]
    public struct RockSplitSettings
    {
        [Tooltip("Usual block size along each axis, in meters. Small y and large x, z give flat beds.")]
        public Vector3 BlockSize;
        [Range(0f, 0.45f), Tooltip("Random move of a split from the middle of its block, as a share of the block.")]
        public float Jitter;
        [Min(0f), Tooltip("Largest turn of a split plane from its axis, in degrees.")]
        public float Scatter;
        [Tooltip("Turn of all split axes, in degrees.")]
        public Vector3 Rotation;
        [Min(0f), Tooltip("Largest depth of a block, in meters.")]
        public float Depth;
        [Min(0.1f), Tooltip("Shape of the depth. Above 1, most blocks stay shallow and few go deep, so no block falls into a pit.")]
        public float DepthPower;
        [Min(0f), Tooltip("Largest slope of a block face, in meters for each meter.")]
        public float Tilt;
        [Range(0f, 1f), Tooltip("Share of the depth on blocks that touch two or more outer faces. Low values keep the outline.")]
        public float CornerKeep;
        [Min(0f), Tooltip("Largest wander of the walls, in meters.")]
        public float Warp;
        [Min(0.01f), Tooltip("Size of the wander, in meters.")]
        public float WarpSize;
        [Min(0f), Tooltip("Half width of the crack along a wall, in meters.")]
        public float CrackWidth;
        [Min(0f), Tooltip("Depth of the crack along a wall, in meters.")]
        public float CrackDepth;
    }

    /// <summary>Settings of a cracks pass.</summary>
    [Serializable]
    public struct RockCrackSettings
    {
        [Range(1, 5), Tooltip("Number of split steps. 3 gives up to 7 cracks.")]
        public int Generations;
        [Range(0f, 1f), Tooltip("Chance that a part splits again after the first crack.")]
        public float SplitChance;
        [Min(0.5f), Tooltip("A part shorter than this along its longest size does not split, in meters.")]
        public float SmallestPart;
        [Range(0f, 0.4f), Tooltip("Random move of a crack from the middle of its part, as a share of the part length.")]
        public float Jitter;
        [Range(0f, 45f), Tooltip("Largest turn of a crack from square to the longest size of its part, in degrees.")]
        public float Scatter;
        [Min(0f), Tooltip("Half width of the first crack, in meters. A crack is at least 1.5 cells wide.")]
        public float CrackWidth;
        [Min(0f), Tooltip("Depth of the first crack below the surface, in meters.")]
        public float CrackDepth;
        [Range(0.2f, 1f), Tooltip("Share of width, depth and weathering that each younger generation keeps.")]
        public float AgeFalloff;
        [Range(0f, 1f), Tooltip("Rounded wear of the crack lips on the first crack. Younger cracks get less.")]
        public float Weathering;
        [Min(0f), Tooltip("Largest bend of a crack face, in meters. The bend has the same roughness at all scales.")]
        public float Roughness;
        [Min(0.1f), Tooltip("Size of the largest bend, in meters. Smaller bends follow at half sizes.")]
        public float RoughnessSize;
    }

    /// <summary>Settings of an open pass.</summary>
    [Serializable]
    public struct RockOpenSettings
    {
        [Min(0.01f), Tooltip("Parts thinner than this are removed, in meters.")]
        public float Thickness;
    }

    /// <summary>
    /// One finite fracture: a planar disc that ends in intact rock, as in a discrete fracture network (Baecher model). The rock
    /// on the negative side of the plane moves back by the step, so the plane becomes a sharp ledge. All of it tapers at the rim.
    /// </summary>
    [Serializable]
    [StructLayout(LayoutKind.Sequential)]
    public struct RockFracture
    {
        public Vector3 Center;
        [Tooltip("Direction of the long axis of the disc. It must lie on the plane.")]
        public Vector3 Along;
        [Tooltip("Normal of the plane. It points to the side that stays proud.")]
        public Vector3 Normal;
        [Min(0.01f), Tooltip("Half of the length, in meters.")]
        public float HalfLength;
        [Min(0.01f), Tooltip("Half of the extent into the rock, in meters.")]
        public float HalfWidth;
        [Min(0f), Tooltip("Depth of the step, in meters.")]
        public float Step;
        [Min(0.01f), Tooltip("Distance from the plane where the step fades out, in meters. The face below then tilts back out.")]
        public float StepFade;
        [Min(0f), Tooltip("Width of the crack at the middle of the disc, in meters. It narrows to a point at the rim.")]
        public float CrackWidth;
        [Min(0f), Tooltip("Depth of the crack, in meters.")]
        public float CrackDepth;
        [Min(0f), Tooltip("Length of the fade at each end, in meters. 0 = the step fades along the whole length.")]
        public float EndFade;
        [Tooltip("The straight runs of one bent fracture share this axis. Their ends are measured along it, so two neighbor runs " +
                 "meet in one plane, with no gap or overlap.")]
        public Vector3 EndAxis;
        [Min(0f), Tooltip("Half of the span of this run on End Axis from Center, in meters. 0 = not a run, and End Fade sets the ends.")]
        public float EndReach;
        [Tooltip("The runs of one fracture have one value, so the depth of the step goes on across a kink.")]
        public float Row;
        [Min(0f), Tooltip("Fade at the start of a run, on End Axis, in meters. 0 = a hard seam with the run before it.")]
        public float HeadFade;
        [Min(0f), Tooltip("Fade at the end of a run, on End Axis, in meters. 0 = a hard seam with the run after it.")]
        public float TailFade;
    }

    /// <summary>Settings of a fractures pass.</summary>
    [Serializable]
    public struct RockFractureSettings
    {
        [Min(0f), Tooltip("Largest sum of the steps at one point, in meters. This keeps a face flush where steps overlap. " +
                          "0 = no limit.")]
        public float StepLimit;
        [Range(0f, 1f), Tooltip("Change of the step along each fracture. 0 = even. High values give ledges that open and close.")]
        public float Breakup;
        [Min(0.05f), Tooltip("Size of the change along a fracture, in meters.")]
        public float BreakupSize;
        [Min(0f), Tooltip("Bend of the fracture planes, in meters. 0 = the default bend.")]
        public float Warp;
        [Min(0f), Tooltip("Size of the bend, in meters. 0 = the default size.")]
        public float WarpSize;
        [Range(0f, 1f), Tooltip("Rounded wear of the crack lips. Above 0, a shallow round channel follows each crack, up to " +
                                "four times the crack width.")]
        public float EdgeWeathering;
    }

    /// <summary>A family of parallel fractures with random places and sizes, for <see cref="RockFractureNetwork"/>.</summary>
    [Serializable]
    public struct RockFractureSet
    {
        [Min(0)] public int Count;
        [Tooltip("Mean direction of the fracture plane normals.")]
        public Vector3 Normal;
        [Range(0f, 0.5f), Tooltip("Random turn of each plane.")]
        public float Scatter;
        [Tooltip("Smallest and largest length, in meters. Short fractures are much more common.")]
        public Vector2 Length;
        [Tooltip("Largest step of a fracture of the largest length, in meters.")]
        public float Step;
        [Tooltip("Largest crack width of a fracture of the largest length, in meters.")]
        public float CrackWidth;
        [Tooltip("Largest crack depth of a fracture of the largest length, in meters.")]
        public float CrackDepth;
        [Range(0f, 1f), Tooltip("Share of the fractures that step the other way. Mixed steps cancel, so a face stays flush.")]
        public float Flip;
        public int Seed;
        [Tooltip("The fractures cross the whole rock. Each one starts inside the rock and cuts through its full depth. Length " +
                 "then only sets the step and crack sizes; connect the fractures so each one runs on to an older fracture " +
                 "or out of the rock.")]
        public bool Through;
    }

    /// <summary>Makes random fractures on the outside of a box: the Baecher model with a power law of lengths.</summary>
    public static class RockFractureNetwork
    {
        // Fractures that lie closer to parallel than this cannot stop each other.
        private const float SmallestCrossing = 0.2f;

        /// <summary>
        /// Extends each fracture to the nearest older fracture or to the edge of the box, so no crack ends in the open rock.
        /// A longer fracture counts as older. The result has T junctions, as in a real joint network.
        /// </summary>
        /// <param name="fractures">The list to change.</param>
        /// <param name="boxSize">Full size of the box, centered on the origin.</param>
        /// <param name="reach">Largest growth of each end, in meters.</param>
        /// <param name="endFade">Length of the fade at each end, in meters.</param>
        /// <param name="first">Fractures before this index do not change. They still stop the others.</param>
        public static void Connect(List<RockFracture> fractures, Vector3 boxSize, float reach, float endFade, int first = 0)
        {
            var order = new List<int>(fractures.Count);
            for (int index = 0; index < fractures.Count; index++)
            {
                order.Add(index);
            }
            order.Sort((a, b) => fractures[b].HalfLength.CompareTo(fractures[a].HalfLength));

            for (int rank = 0; rank < order.Count; rank++)
            {
                if (order[rank] < first) continue;
                RockFracture fracture = fractures[order[rank]];
                float forward = EndOf(fractures, order, rank, fracture, 1f, boxSize, reach);
                float backward = EndOf(fractures, order, rank, fracture, -1f, boxSize, reach);
                fracture.Center += fracture.Along.normalized * (0.5f * (forward - backward));
                fracture.HalfLength = 0.5f * (forward + backward) + endFade;
                fracture.EndFade = endFade;
                fractures[order[rank]] = fracture;
            }
        }

        // Distance from the center of a fracture to its new end in one direction. Older fractures are already moved.
        private static float EndOf(List<RockFracture> fractures, List<int> order, int rank, in RockFracture fracture, float way,
            Vector3 boxSize, float reach)
        {
            Vector3 direction = fracture.Along.normalized * way;
            float nearest = fracture.HalfLength * 0.8f;
            float best = Mathf.Min(fracture.HalfLength + reach, BoxExit(fracture.Center, direction, boxSize * 0.5f));
            for (int older = 0; older < rank; older++)
            {
                RockFracture other = fractures[order[older]];
                Vector3 normal = other.Normal.normalized;
                float crossing = Vector3.Dot(direction, normal);
                if (Mathf.Abs(crossing) < SmallestCrossing) continue;
                float distance = Vector3.Dot(other.Center - fracture.Center, normal) / crossing;
                if (distance < nearest || distance >= best) continue;
                Vector3 offset = fracture.Center + direction * distance - other.Center;
                Vector3 otherAlong = other.Along.normalized;
                if (Mathf.Abs(Vector3.Dot(offset, otherAlong)) > other.HalfLength) continue;
                if (Mathf.Abs(Vector3.Dot(offset, Vector3.Cross(normal, otherAlong))) > other.HalfWidth) continue;
                best = distance;
            }
            return Mathf.Max(best, nearest);
        }

        // Distance along a direction from a point on or in a box to the point where the line leaves the box.
        private static float BoxExit(Vector3 start, Vector3 direction, Vector3 half)
        {
            float exit = float.MaxValue;
            for (int axis = 0; axis < 3; axis++)
            {
                if (Mathf.Abs(direction[axis]) < 1e-4f) continue;
                float wall = Mathf.Sign(direction[axis]) * half[axis];
                exit = Mathf.Min(exit, Mathf.Max((wall - start[axis]) / direction[axis], 0f));
            }
            return exit;
        }

        // Through fractures start in the middle of the box, this share of its size, so each plane crosses the rock.
        private const float ThroughCore = 0.6f;

        /// <summary>Adds the fractures of a set to a list.</summary>
        public static void Add(in RockFractureSet set, Vector3 boxSize, List<RockFracture> fractures)
        {
            var random = new Unity.Mathematics.Random((uint)set.Seed * 2654435761u + 7u);
            Vector3 half = boxSize * 0.5f;
            for (int index = 0; index < set.Count; index++)
            {
                // A place on one of the six faces of the box, with a chance by the area of the face.
                float areaX = boxSize.y * boxSize.z;
                float areaY = boxSize.x * boxSize.z;
                float areaZ = boxSize.x * boxSize.y;
                float pick = random.NextFloat(areaX + areaY + areaZ);
                float3 center = random.NextFloat3(-(float3)(Vector3)half, half);
                float outer = random.NextBool() ? 1f : -1f;
                if (set.Through) center *= ThroughCore;
                else if (pick < areaX) center.x = outer * half.x;
                else if (pick < areaX + areaY) center.y = outer * half.y;
                else center.z = outer * half.z;

                float3 normal = math.normalize((float3)(Vector3)set.Normal + random.NextFloat3Direction() * set.Scatter);
                if (set.Flip > 0f && random.NextFloat() < set.Flip) normal = -normal;
                float3 helper = math.abs(normal.y) < 0.9f ? new float3(0f, 1f, 0f) : new float3(1f, 0f, 0f);
                float3 side = math.normalize(math.cross(normal, helper));
                float3 other = math.cross(normal, side);
                float angle = random.NextFloat(0f, math.PI);
                float3 along = side * math.cos(angle) + other * math.sin(angle);

                // A power law: most fractures are short, and a few are long.
                float share = math.pow(random.NextFloat(), 3f);
                float length = math.lerp(set.Length.x, set.Length.y, share);
                float size = length / math.max(set.Length.y, 0.01f);
                fractures.Add(new RockFracture
                {
                    Center = center, Along = along, Normal = normal, HalfLength = 0.5f * length,
                    // A through fracture reaches past the box across its length, so it cuts the full depth of the rock.
                    HalfWidth = set.Through ? boxSize.magnitude : 0.3f * length,
                    Step = set.Step * size, StepFade = 0.6f * length, CrackWidth = set.CrackWidth * size,
                    CrackDepth = set.CrackDepth * size
                });
            }
        }
    }

    /// <summary>A large flat face of the base: all rock past this plane goes.</summary>
    [Serializable]
    public struct RockBaseSlice
    {
        [Tooltip("Direction out of the rock.")]
        public Vector3 Normal;
        [Tooltip("Distance of the plane from the rock center along the direction, in meters.")]
        public float Distance;
    }

    /// <summary>A broad soft bulge of the base, so a large flat face bows out or falls in.</summary>
    [Serializable]
    public struct RockBaseBulge
    {
        [Tooltip("Center of the bulge, in meters from the rock center.")]
        public Vector3 Center;
        [Tooltip("Reach of the bulge along each axis, in meters.")]
        public Vector3 Reach;
        [Tooltip("Largest move of the surface, in meters. A negative value makes a shallow hollow.")]
        public float Height;
        [Range(0f, 1f), Tooltip("0 = a round swell. Above 0 = a flat plate with straight, creased sides, and this share of the reach is its slope.")]
        public float Flat;
    }

    /// <summary>One authored mass of a rock. A list of boxes sets a chosen silhouette, for kits and cliffs.</summary>
    [Serializable]
    public struct RockBaseBox
    {
        [Tooltip("Center of the box, in meters from the rock center.")]
        public Vector3 Center;
        [Tooltip("Full size of the box, in meters.")]
        public Vector3 Size;
        [Tooltip("Turn of the box, in degrees.")]
        public Vector3 Rotation;
    }

    /// <summary>Settings of a Displace pass.</summary>
    [Serializable]
    public struct RockDisplaceSettings
    {
        [Tooltip("Height tile with a mean of 0 and a deviation of 1. It must repeat, and it needs a linear float format.")]
        public Texture2D Map;
        [Min(0.1f), Tooltip("Size of one tile on the rock, in meters.")]
        public float TileSize;
        [Tooltip("Move of the surface for one deviation of the map, in meters.")]
        public float Amount;
        [Min(1f), Tooltip("Sharpness of the change between the three projections.")]
        public float Sharpness;
        [Tooltip("Each base block reads its own part of the tile, so the blocks are masks: a cut or crack stops at the border of its block.")]
        public bool ByBlock;
        [Tooltip("Relief of the +Z face in meters. With this map, the four face maps replace the tile.")]
        public Texture2D FaceFront;
        public Texture2D FaceBack;
        [Tooltip("Relief of the -X face in meters.")]
        public Texture2D FaceLeft;
        [Tooltip("Relief of the +X face in meters.")]
        public Texture2D FaceRight;
        [Tooltip("Center of the box of the face maps, in meters.")]
        public Vector3 FaceCenter;
        [Tooltip("Full size of the box of the face maps, in meters.")]
        public Vector3 FaceSize;
        [Min(0f), Tooltip("Largest move of the surface by the face maps, in meters.")]
        public float FaceReach;
    }

    /// <summary>One operation on the rock field. Only the settings of its kind apply.</summary>
    [Serializable]
    public struct RockPass
    {
        [Tooltip("The operation of this pass.")]
        public RockPassKind Kind;
        [Tooltip("Turn the pass off without losing its values.")]
        public bool Skip;
        public int Seed;
        [Range(-1f, 1f), Tooltip("Noise, Cells and Cut. 1 = the pass acts only on convex edges and thin parts, for chips. " +
                                 "-1 = the pass leaves them alone, so it does not tear them.")]
        public float EdgeFocus;
        [Min(0.01f), Tooltip("Noise, Cells and Cut. Reach of the edge test, in meters.")]
        public float EdgeRadius;
        public RockNoiseSettings Noise;
        public RockJointSettings Joints;
        public RockCellSettings Cells;
        public RockCutSettings Cut;
        public RockCarveSettings Carve;
        public RockSmoothSettings Smooth;
        public RockOpenSettings Open;
        public RockSplitSettings Split;
        public RockCrackSettings Cracks;
        public RockFractureSettings Fractures;
        public RockDisplaceSettings Displace;

        /// <summary>The largest distance that this pass can add to a rock of this size, in meters.</summary>
        public readonly float ReachOn(Vector3 rockSize)
        {
            if (Skip) return 0f;
            switch (Kind)
            {
                case RockPassKind.Displace:
                    return Displace.FaceFront != null ? Displace.FaceReach : 4f * Mathf.Abs(Displace.Amount);
                case RockPassKind.Noise:
                    // The noise can pass its amount in a few places. Rough patches add up to half the small layers again.
                    return 1.5f * Mathf.Abs(Noise.Amount) * (1f + (Noise.PatchSize > 0f ? 0.5f * Noise.PatchContrast : 0f));
                case RockPassKind.Joints:
                    return 0.75f * (1f - Joints.Follow) * Joints.Spacing.magnitude + Joints.Relief +
                           Joints.KeepJitter + Joints.Warp + Joints.RowScale * 0.5f * rockSize.magnitude;
                case RockPassKind.Cells:
                    return Mathf.Abs(Cells.CellOffset) + Cells.Facet * Cells.Size * Mathf.Max(
                        Cells.Stretch.x, Mathf.Max(Cells.Stretch.y, Cells.Stretch.z));
                default:
                    return 0f;
            }
        }

        /// <summary>A noise pass that the generator starts with.</summary>
        public static RockPass DefaultNoise => new()
        {
            Kind = RockPassKind.Noise, Seed = 11, EdgeRadius = 0.3f,
            Noise = new RockNoiseSettings
            {
                Amount = 1.2f, Size = 6f, Layers = 6, SizeRatio = 2.1f, Strength = 0.5f, Ridged = 0.6f,
                PatchSize = 4f, PatchContrast = 0.8f
            }
        };
    }

    /// <summary>
    /// Builds the rock field on the GPU and reads it back. The field is a grid of floats, negative in the rock. The
    /// index of a sample is x + size.x * (y + size.y * z).
    /// </summary>
    internal static class RockField
    {
        private const string ComputeName = "LoogaRockField";
        private const int ThreadGroupSize = 4;
        // Thread groups in one dispatch along Z. A long dispatch keeps the GPU from other work, so each kernel runs in
        // slices, and the build waits for each slice.
        private const int SliceGroups = 8;
        // Width of the distance rebuild after the joints, in meters. It must pass the reach of the later passes.
        private const float RebuildBand = 1.5f;
        // Width of the last distance rebuild, in meters. The rays of the surface masks read this far from the surface.
        private const float FinalBand = 2f;
        // Width of the zone of fine damage on each side of a fracture, in meters.
        private const float FractureDamageWidth = 0.4f;
        // A slow wander, so fractures are not straight lines. Their planes stay nearly flat.
        private const float FractureWarp = 0.12f;
        private const float FractureWarpSize = 3f;
        // Ranges of the fracture volume, in meters: signed distance on each side, distance, and run below a ledge.
        private const float VolumeSignedBand = 0.2f;
        private const float VolumeDistanceRange = 0.6f;
        private const float VolumeRunRange = 6f;
        // Longest blur in samples on each side of a sample.
        private const int MaximumBlurRadius = 32;

        private static readonly int FieldId = Shader.PropertyToID("_Field");
        private static readonly int SourceId = Shader.PropertyToID("_Source");
        private static readonly int BlurredId = Shader.PropertyToID("_Blurred");
        private static readonly int BaseId = Shader.PropertyToID("_Base");
        private static readonly int EdgeMaskId = Shader.PropertyToID("_EdgeMask");
        private static readonly int EdgeMaskOutId = Shader.PropertyToID("_EdgeMaskOut");
        private static readonly int WriteEdgeMaskId = Shader.PropertyToID("_WriteEdgeMask");
        private static readonly int EdgeMaskWidthId = Shader.PropertyToID("_EdgeMaskWidth");
        private static readonly int UseEdgeMaskId = Shader.PropertyToID("_UseEdgeMask");
        private static readonly int SparsityId = Shader.PropertyToID("_Sparsity");
        private static readonly int SliceId = Shader.PropertyToID("_Slice");
        private static readonly int BulgeCenterId = Shader.PropertyToID("_BulgeCenter");
        private static readonly int BulgeReachId = Shader.PropertyToID("_BulgeReach");
        private static readonly int FracturesId = Shader.PropertyToID("_Fractures");
        private static readonly int StepLimitId = Shader.PropertyToID("_StepLimit");
        private static readonly int StepBreakupId = Shader.PropertyToID("_StepBreakup");
        private static readonly int StepBreakupRateId = Shader.PropertyToID("_StepBreakupRate");
        private static readonly int CutDepthPowerId = Shader.PropertyToID("_CutDepthPower");
        private static readonly int CutLevelsId = Shader.PropertyToID("_CutLevels");
        private static readonly int FractureCountId = Shader.PropertyToID("_FractureCount");
        private static readonly int VolumeOutId = Shader.PropertyToID("_VolumeOut");
        private static readonly int VolumeRangeId = Shader.PropertyToID("_VolumeRange");
        private static readonly int MaxCarveId = Shader.PropertyToID("_MaxCarve");
        private static readonly int NearestInId = Shader.PropertyToID("_NearestIn");
        private static readonly int NearestOutId = Shader.PropertyToID("_NearestOut");
        private static readonly int JumpLengthId = Shader.PropertyToID("_JumpLength");
        private static readonly int PlanesId = Shader.PropertyToID("_Planes");
        private static readonly int SizeId = Shader.PropertyToID("_Size");
        private static readonly int SliceStartId = Shader.PropertyToID("_SliceStart");
        private static readonly int OriginId = Shader.PropertyToID("_Origin");
        private static readonly int CellSizeId = Shader.PropertyToID("_CellSize");
        private static readonly int BlockCountId = Shader.PropertyToID("_BlockCount");
        private static readonly int PlanesPerBlockId = Shader.PropertyToID("_PlanesPerBlock");
        private static readonly int SeedId = Shader.PropertyToID("_Seed");
        private static readonly int EdgeFocusId = Shader.PropertyToID("_EdgeFocus");
        private static readonly int EdgeRangeId = Shader.PropertyToID("_EdgeRange");
        private static readonly int WarpAmountId = Shader.PropertyToID("_WarpAmount");
        private static readonly int WarpFrequencyId = Shader.PropertyToID("_WarpFrequency");
        private static readonly int AmountId = Shader.PropertyToID("_Amount");
        private static readonly int NoiseScaleId = Shader.PropertyToID("_NoiseScale");
        private static readonly int CarveOnlyId = Shader.PropertyToID("_CarveOnly");
        private static readonly int DistanceScaleId = Shader.PropertyToID("_DistanceScale");
        private static readonly int FrequencyId = Shader.PropertyToID("_Frequency");
        private static readonly int OctavesId = Shader.PropertyToID("_Octaves");
        private static readonly int LacunarityId = Shader.PropertyToID("_Lacunarity");
        private static readonly int GainId = Shader.PropertyToID("_Gain");
        private static readonly int RidgedId = Shader.PropertyToID("_Ridged");
        private static readonly int CrackWeatheringId = Shader.PropertyToID("_CrackWeathering");
        private static readonly int CrackNodesId = Shader.PropertyToID("_CrackNodes");
        private static readonly int CrackNodeCountId = Shader.PropertyToID("_CrackNodeCount");
        private static readonly int CrackAgeId = Shader.PropertyToID("_CrackAge");
        private static readonly int CrackRoughId = Shader.PropertyToID("_CrackRough");
        private static readonly int CrackRoughFrequencyId = Shader.PropertyToID("_CrackRoughFrequency");
        private static readonly int CrackPointsId = Shader.PropertyToID("_CrackPoints");
        private static readonly int CrackNormalsId = Shader.PropertyToID("_CrackNormals");
        private static readonly int CrackLineDistancesId = Shader.PropertyToID("_CrackLineDistances");
        private static readonly int CrackPointCountId = Shader.PropertyToID("_CrackPointCount");
        private static readonly int PatchFrequencyId = Shader.PropertyToID("_PatchFrequency");
        private static readonly int PatchContrastId = Shader.PropertyToID("_PatchContrast");
        private static readonly int FrameId = Shader.PropertyToID("_Frame");
        private static readonly int SpacingId = Shader.PropertyToID("_Spacing");
        private static readonly int GapId = Shader.PropertyToID("_Gap");
        private static readonly int SpacingJitterId = Shader.PropertyToID("_SpacingJitter");
        private static readonly int RowShiftId = Shader.PropertyToID("_RowShift");
        private static readonly int CoreId = Shader.PropertyToID("_Core");
        private static readonly int SolidId = Shader.PropertyToID("_Solid");
        private static readonly int PersistenceId = Shader.PropertyToID("_Persistence");
        private static readonly int RoundingId = Shader.PropertyToID("_Rounding");
        private static readonly int OverlapId = Shader.PropertyToID("_Overlap");
        private static readonly int TiltId = Shader.PropertyToID("_Tilt");
        private static readonly int ReliefId = Shader.PropertyToID("_Relief");
        private static readonly int FollowId = Shader.PropertyToID("_Follow");
        private static readonly int KeepJitterId = Shader.PropertyToID("_KeepJitter");
        private static readonly int KeepMarginId = Shader.PropertyToID("_KeepMargin");
        private static readonly int FaceTiltId = Shader.PropertyToID("_FaceTilt");
        private static readonly int BlendId = Shader.PropertyToID("_Blend");
        private static readonly int MissingId = Shader.PropertyToID("_Missing");
        private static readonly int ReachId = Shader.PropertyToID("_Reach");
        private static readonly int CellScaleId = Shader.PropertyToID("_CellScale");
        private static readonly int CellJitterId = Shader.PropertyToID("_CellJitter");
        private static readonly int CrackWidthId = Shader.PropertyToID("_CrackWidth");
        private static readonly int CrackDepthId = Shader.PropertyToID("_CrackDepth");
        private static readonly int CrackProfileId = Shader.PropertyToID("_CrackProfile");
        private static readonly int TagsId = Shader.PropertyToID("_Tags");
        private static readonly int RowScaleId = Shader.PropertyToID("_RowScale");
        private static readonly int WriteTagsId = Shader.PropertyToID("_WriteTags");
        private static readonly int HorizontalId = Shader.PropertyToID("_Horizontal");
        private static readonly int CutDepthId = Shader.PropertyToID("_CutDepth");
        private static readonly int CutLimitId = Shader.PropertyToID("_CutLimit");
        private static readonly int SplitNodesId = Shader.PropertyToID("_SplitNodes");
        private static readonly int SplitLeavesId = Shader.PropertyToID("_SplitLeaves");
        private static readonly int ClipLevelId = Shader.PropertyToID("_ClipLevel");
        private static readonly int ClipWidthId = Shader.PropertyToID("_ClipWidth");
        private static readonly int CutShareId = Shader.PropertyToID("_CutShare");
        private static readonly int CellOffsetId = Shader.PropertyToID("_CellOffset");
        private static readonly int FacetId = Shader.PropertyToID("_Facet");
        private static readonly int BlurAxisId = Shader.PropertyToID("_BlurAxis");
        private static readonly int BlurRadiusId = Shader.PropertyToID("_BlurRadius");
        private static readonly int BlurSigmaId = Shader.PropertyToID("_BlurSigma");
        private static readonly int WearId = Shader.PropertyToID("_Wear");
        private static readonly int FillId = Shader.PropertyToID("_Fill");
        private static readonly int PatchyId = Shader.PropertyToID("_Patchy");

        /// <summary>
        /// Runs the base shape and the passes. The caller owns the returned arrays. The tags have one value for each
        /// sample, from 0 to 1, for the block color.
        /// </summary>
        public static NativeArray<float> Build(float4[] planes, int blockCount, int3 size, float3 origin,
            float cellSize, Vector3 rockSize, IReadOnlyList<RockBaseSlice> slices, IReadOnlyList<RockBaseBulge> bulges,
            IReadOnlyList<RockPass> passes, IReadOnlyList<RockFracture> fractures, float maxCarve, RockBlockLayout blocks,
            out NativeArray<float> tags, RockImportedField imported = null)
        {
            var compute = Resources.Load<ComputeShader>(ComputeName);
            if (compute == null) throw new InvalidOperationException($"Compute shader {ComputeName} is missing.");
            // A compute shader with an error still loads, and its dispatches do nothing.
            if (!compute.IsSupported(0))
            {
                throw new InvalidOperationException($"Compute shader {ComputeName} does not compile on this device.");
            }

            int sampleCount = size.x * size.y * size.z;
            using var run = new Run(compute, size, cellSize, sampleCount);
            using var planeBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, planes.Length, 16);
            planeBuffer.SetData(planes);

            compute.SetInts(SizeId, size.x, size.y, size.z);
            compute.SetVector(OriginId, new Vector4(origin.x, origin.y, origin.z, 0f));
            compute.SetFloat(CellSizeId, cellSize);
            compute.SetInt(BlockCountId, blockCount);
            compute.SetInt(PlanesPerBlockId, planes.Length / blockCount);

            if (imported != null)
            {
                using NativeArray<float> importedField = imported.Evaluate(size, origin, cellSize);
                run.Load(importedField);
            }
            else if (blocks != null)
            {
                using NativeArray<float> blockField = blocks.Evaluate(size, origin, cellSize);
                run.Load(blockField);
            }
            else
            {
                int baseKernel = compute.FindKernel("BaseShape");
                compute.SetBuffer(baseKernel, PlanesId, planeBuffer);
                run.Step(baseKernel);
            }
            int sliceKernel = compute.FindKernel("SlicePass");
            foreach (RockBaseSlice slice in slices)
            {
                Vector3 normal = slice.Normal.normalized;
                compute.SetVector(SliceId, new Vector4(normal.x, normal.y, normal.z, slice.Distance));
                run.Step(sliceKernel);
            }
            int bulgeKernel = compute.FindKernel("BulgePass");
            foreach (RockBaseBulge bulge in bulges)
            {
                compute.SetVector(BulgeCenterId, new Vector4(bulge.Center.x, bulge.Center.y, bulge.Center.z, bulge.Height));
                compute.SetVector(BulgeReachId, bulge.Reach);
                compute.SetFloat("_BulgeFlat", bulge.Flat);
                run.Step(bulgeKernel);
            }
            // The maximum of planes is not a distance past the corners, and later passes read distances.
            run.Redistance(RebuildBand);
            run.KeepBase(maxCarve);

            for (int index = 0; index < passes.Count; index++)
            {
                RockPass pass = passes[index];
                if (pass.Skip) continue;
                compute.SetInt(SeedId, pass.Seed);
                compute.SetFloat(EdgeFocusId, 0f);
                switch (pass.Kind)
                {
                    case RockPassKind.Noise:
                        RunNoise(run, pass);
                        break;
                    case RockPassKind.Joints:
                        RunJoints(run, pass.Joints, cellSize, rockSize);
                        break;
                    case RockPassKind.Cells:
                        RunCells(run, pass, cellSize);
                        break;
                    case RockPassKind.Cut:
                        RunCut(run, pass, cellSize);
                        break;
                    case RockPassKind.Split:
                        RunSplit(run, pass, cellSize, rockSize);
                        break;
                    case RockPassKind.Cracks:
                        RunCracks(run, pass, planes, blockCount, cellSize, rockSize);
                        break;
                    case RockPassKind.Carve:
                        RunCarve(run, pass, cellSize);
                        break;
                    case RockPassKind.Fractures:
                        RunFractures(run, pass.Fractures, fractures, cellSize);
                        break;
                    case RockPassKind.Smooth:
                        RunSmooth(run, pass.Smooth);
                        break;
                    case RockPassKind.Displace:
                        RunDisplace(run, pass, cellSize, planeBuffer);
                        break;
                    case RockPassKind.Open:
                        RemoveThinParts(run, 0.5f * pass.Open.Thickness);
                        break;
                }
            }
            run.Redistance(FinalBand);
            tags = run.ReadTags(sampleCount);
            return run.ReadBack(sampleCount);
        }

        /// <summary>Builds the fracture volume of a rock. See the kernel FractureVolume. The caller owns the texture.</summary>
        /// <param name="fractures">The fractures of the recipe.</param>
        /// <param name="seed">Seed of the Fractures pass, so the warp matches the steps.</param>
        /// <param name="low">Corner of the volume, in rock space.</param>
        /// <param name="extent">Size of the volume, in meters.</param>
        /// <param name="cellSize">Distance between two samples, in meters.</param>
        /// <param name="samples">The number of samples along each axis.</param>
        /// <param name="ranges">The value ranges of the channels, in meters, for the shader.</param>
        public static Texture3D BuildFractureVolume(IReadOnlyList<RockFracture> fractures, int seed, float3 low, float3 extent,
            float cellSize, out int3 samples, out Vector4 ranges, RockFractureSettings settings = default)
        {
            var compute = Resources.Load<ComputeShader>(ComputeName);
            samples = (int3)math.ceil(extent / cellSize) + 1;
            int sampleCount = samples.x * samples.y * samples.z;
            var items = new RockFracture[fractures.Count];
            for (int index = 0; index < items.Length; index++)
            {
                items[index] = fractures[index];
            }
            using var fractureBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, items.Length,
                Marshal.SizeOf<RockFracture>());
            using var output = new GraphicsBuffer(GraphicsBuffer.Target.Structured, sampleCount, sizeof(uint));
            using var fence = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, sizeof(float));
            fractureBuffer.SetData(items);

            ranges = new Vector4(VolumeSignedBand, VolumeDistanceRange, 0f, VolumeRunRange);
            int kernel = compute.FindKernel("FractureVolume");
            compute.SetBuffer(kernel, FracturesId, fractureBuffer);
            compute.SetBuffer(kernel, VolumeOutId, output);
            compute.SetInt(FractureCountId, items.Length);
            compute.SetInt(SeedId, seed);
            compute.SetInts(SizeId, samples.x, samples.y, samples.z);
            compute.SetVector(OriginId, new Vector4(low.x, low.y, low.z, 0f));
            compute.SetFloat(CellSizeId, cellSize);
            compute.SetVector(VolumeRangeId, ranges);
            SetWarp(compute, settings.Warp > 0f ? settings.Warp : FractureWarp, settings.WarpSize > 0f ? settings.WarpSize : FractureWarpSize);

            int3 groups = (samples + ThreadGroupSize - 1) / ThreadGroupSize;
            for (int group = 0; group < groups.z; group += SliceGroups)
            {
                compute.SetInt(SliceStartId, group * ThreadGroupSize);
                compute.Dispatch(kernel, groups.x, groups.y, Mathf.Min(SliceGroups, groups.z - group));
                // The read ends after the slice ends, so the queue of the GPU stays short.
                AsyncGPUReadback.Request(fence).WaitForCompletion();
            }

            AsyncGPUReadback.Request(output).WaitForCompletion();
            var bytes = new uint[sampleCount];
            output.GetData(bytes);
            var volume = new Texture3D(samples.x, samples.y, samples.z, GraphicsFormat.R8G8B8A8_UNorm,
                TextureCreationFlags.None)
            {
                name = "Rock fracture volume", hideFlags = HideFlags.DontSave, filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            volume.SetPixelData(bytes, 0);
            volume.Apply(false, true);
            return volume;
        }

        // A morphological opening, as in VDB workflows: the rock shrinks, the distance is built again, and the rock grows
        // back. Parts thinner than twice the radius, such as fins and strings, do not return. Larger shapes keep their size.
        private static void RemoveThinParts(Run run, float radius)
        {
            ComputeShader compute = run.Compute;
            int kernel = compute.FindKernel("OffsetPass");
            run.KeepInBlurred();
            compute.SetFloat(AmountId, radius);
            run.Step(kernel);
            run.Redistance(Mathf.Max(RebuildBand, 3f * radius));
            compute.SetFloat(AmountId, -radius);
            run.Step(kernel);
            // A square edge moves by about 0.41 of the radius in an opening. A larger change is a removed thin part.
            compute.SetFloat(AmountId, 0.6f * radius);
            run.Step(compute.FindKernel("OpenFinish"));
        }

        private static void RunDisplace(Run run, in RockPass pass, float cellSize, GraphicsBuffer planeBuffer)
        {
            RockDisplaceSettings displace = pass.Displace;
            ComputeShader compute = run.Compute;
            if (displace.FaceFront != null)
            {
                int faceKernel = compute.FindKernel("FaceDisplacePass");
                compute.SetTexture(faceKernel, "_FaceFront", displace.FaceFront);
                compute.SetTexture(faceKernel, "_FaceBack", displace.FaceBack);
                compute.SetTexture(faceKernel, "_FaceLeft", displace.FaceLeft);
                compute.SetTexture(faceKernel, "_FaceRight", displace.FaceRight);
                compute.SetVector("_FaceCenter", displace.FaceCenter);
                compute.SetVector("_FaceHalf", 0.5f * displace.FaceSize);
                compute.SetFloat("_DisplaceSharpness", Mathf.Max(displace.Sharpness, 1f));
                compute.SetFloat(ReachId, displace.FaceReach + 4f * cellSize);
                run.Step(faceKernel);
                // A step in the map is a jump in the field, and later passes read distances.
                run.Redistance(RebuildBand);
                return;
            }
            if (displace.Map == null) return;
            int kernel = compute.FindKernel("DisplacePass");
            compute.SetTexture(kernel, "_DisplaceMap", displace.Map);
            compute.SetBuffer(kernel, PlanesId, planeBuffer);
            compute.SetInt("_DisplaceByBlock", displace.ByBlock ? 1 : 0);
            compute.SetFloat("_DisplaceAmount", displace.Amount);
            compute.SetFloat("_DisplaceScale", 1f / displace.TileSize);
            // The level with texels near the size of a grid cell, so the map does not alias on the grid.
            float texel = displace.TileSize / displace.Map.width;
            compute.SetFloat("_DisplaceMip", Mathf.Max(Mathf.Log(cellSize / texel, 2f), 0f));
            compute.SetFloat("_DisplaceSharpness", Mathf.Max(displace.Sharpness, 1f));
            compute.SetVector("_DisplaceOffset", new Vector3(pass.Seed * 0.731f, pass.Seed * 1.379f, pass.Seed * 2.113f));
            compute.SetFloat(ReachId, 4f * Mathf.Abs(displace.Amount) + 4f * cellSize);
            run.Step(kernel);
        }

        private static void RunNoise(Run run, in RockPass pass)
        {
            ComputeShader compute = run.Compute;
            RockNoiseSettings noise = pass.Noise;
            SetEdgeFocus(run, pass);
            compute.SetFloat(AmountId, noise.Amount);
            compute.SetFloat(FrequencyId, 1f / noise.Size);
            compute.SetInt(OctavesId, noise.Layers);
            compute.SetFloat(LacunarityId, noise.SizeRatio);
            compute.SetFloat(GainId, noise.Strength);
            compute.SetFloat(RidgedId, noise.Ridged);
            compute.SetFloat(PatchFrequencyId, noise.PatchSize > 0f ? 1f / noise.PatchSize : 0f);
            compute.SetFloat(PatchContrastId, noise.PatchSize > 0f ? noise.PatchContrast : 0f);
            compute.SetInt(UseEdgeMaskId, noise.OnCellEdges ? 1 : 0);
            compute.SetVector(NoiseScaleId, Inverse(new Vector3(
                noise.Stretch.x > 0f ? noise.Stretch.x : 1f, noise.Stretch.y > 0f ? noise.Stretch.y : 1f,
                noise.Stretch.z > 0f ? noise.Stretch.z : 1f)));
            compute.SetInt(CarveOnlyId, noise.CarveOnly ? 1 : 0);
            compute.SetFloat(ClipLevelId, noise.ClipLevel);
            compute.SetFloat(ClipWidthId, noise.ClipWidth);
            SetWarp(compute, noise.Warp, 0.7f * noise.Size);
            run.Step(compute.FindKernel("NoisePass"));
        }

        private static void RunJoints(Run run, in RockJointSettings joints, float cellSize, Vector3 rockSize)
        {
            ComputeShader compute = run.Compute;
            compute.SetMatrix(FrameId, Matrix4x4.Rotate(Quaternion.Euler(joints.Rotation)).inverse);
            compute.SetVector(SpacingId, joints.Spacing);
            compute.SetVector(GapId, joints.Gap);
            compute.SetFloat(SpacingJitterId, joints.SpacingJitter);
            compute.SetFloat(RowShiftId, joints.RowShift);
            compute.SetFloat(CoreId, joints.Depth);
            compute.SetFloat(SolidId, joints.Depth + 0.75f * joints.Spacing.magnitude);
            compute.SetFloat(PersistenceId, joints.Persistence);
            compute.SetFloat(RoundingId, joints.Rounding);
            // The largest space that a turned block can leave at a joint.
            float spacing = Mathf.Max(joints.Spacing.x, Mathf.Max(joints.Spacing.y, joints.Spacing.z));
            compute.SetFloat(OverlapId, (joints.Tilt * Mathf.Deg2Rad + joints.FaceTilt) * spacing + cellSize);
            compute.SetFloat(TiltId, joints.Tilt * Mathf.Deg2Rad);
            compute.SetFloat(ReliefId, joints.Relief);
            compute.SetFloat(FollowId, joints.Follow);
            compute.SetFloat(KeepJitterId, joints.KeepJitter);
            compute.SetFloat(KeepMarginId, joints.KeepMargin);
            compute.SetFloat(FaceTiltId, joints.FaceTilt);
            compute.SetFloat(BlendId, joints.Blend);
            compute.SetFloat(MissingId, joints.Missing);
            compute.SetFloat(ReachId, joints.Spacing.magnitude + joints.Relief + joints.KeepJitter + joints.Warp +
                                      joints.RowScale * 0.5f * rockSize.magnitude + 4f * cellSize);
            compute.SetFloat(RowScaleId, joints.RowScale);
            compute.SetInt(WriteTagsId, joints.TagBlocks ? 1 : 0);
            SetWarp(compute, joints.Warp, joints.WarpSize);
            int kernel = compute.FindKernel("JointPass");
            compute.SetBuffer(kernel, TagsId, run.Tags);
            run.Step(kernel);
            run.Redistance(Mathf.Max(RebuildBand, joints.Depth));
        }

        private static void RunCells(Run run, in RockPass pass, float cellSize)
        {
            ComputeShader compute = run.Compute;
            RockCellSettings cells = pass.Cells;
            SetEdgeFocus(run, pass);
            int kernel = compute.FindKernel("CellPass");
            float cellLength = cells.Size;
            float strength = 1f;
            float longest = Mathf.Max(cells.Stretch.x, Mathf.Max(cells.Stretch.y, cells.Stretch.z));
            for (int layer = 0; layer < cells.Layers; layer++)
            {
                Vector3 scale = Vector3.Scale(Vector3.one / cellLength, Inverse(cells.Stretch));
                float offset = cells.CellOffset * strength;
                float depth = cells.CrackDepth * strength;
                compute.SetInt(SeedId, pass.Seed + layer * 173);
                compute.SetVector(CellScaleId, scale);
                compute.SetFloat(CellJitterId, cells.Jitter);
                // The crack width comes in meters. The search works in cell units.
                compute.SetFloat(CrackWidthId, cells.CrackWidth * strength / cellLength);
                compute.SetFloat(CrackDepthId, depth);
                compute.SetFloat(CrackProfileId, cells.CrackProfile);
                compute.SetFloat(CellOffsetId, offset);
                compute.SetFloat(FacetId, cells.Facet);
                compute.SetFloat(HorizontalId, cells.Horizontal);
                compute.SetFloat(ReachId, 2f * (Mathf.Abs(offset) + Mathf.Abs(depth) +
                                                cells.Facet * cellLength * longest) + 4f * cellSize);
                SetWarp(compute, cells.Warp * cellLength / cells.Size, 0.7f * cellLength);
                run.Step(kernel);
                cellLength /= cells.SizeRatio;
                strength *= cells.Strength;
            }
            run.Redistance(RebuildBand);
        }

        private static void RunCut(Run run, in RockPass pass, float cellSize)
        {
            ComputeShader compute = run.Compute;
            RockCutSettings cut = pass.Cut;
            SetEdgeFocus(run, pass);
            int kernel = compute.FindKernel("CutPass");
            float shortest = Mathf.Min(cut.Stretch.x, Mathf.Min(cut.Stretch.y, cut.Stretch.z));
            float cellLength = cut.Size;
            float strength = 1f;
            for (int layer = 0; layer < cut.Layers; layer++)
            {
                float depth = cut.Depth * strength;
                compute.SetInt(SeedId, pass.Seed + layer * 173);
                compute.SetVector(CellScaleId, Vector3.Scale(Vector3.one / cellLength, Inverse(cut.Stretch)));
                compute.SetFloat(CellJitterId, cut.Jitter);
                compute.SetFloat(CutShareId, cut.Share);
                compute.SetFloat(CutDepthId, depth);
                compute.SetFloat(CutDepthPowerId, cut.DepthPower > 0f ? cut.DepthPower : 1f);
                compute.SetInt(CutLevelsId, cut.DepthLevels);
                // A cut never takes more than twice its depth, so a turned plane cannot remove a block.
                compute.SetFloat(CutLimitId, 2f * depth);
                compute.SetFloat(DistanceScaleId, cellLength * shortest);
                compute.SetFloat(SparsityId, cut.Sparsity);
                compute.SetInt(WriteEdgeMaskId, cut.MainCut ? 1 : 0);
                compute.SetFloat(EdgeMaskWidthId, Mathf.Max(cut.EdgeZone, 0.01f) / cellLength);
                compute.SetBuffer(kernel, EdgeMaskOutId, run.EdgeMask);
                compute.SetFloat(FacetId, cut.Tilt);
                compute.SetFloat(CrackWidthId, Mathf.Max(cut.CrackWidth * strength, 0.001f) / cellLength);
                compute.SetFloat(CrackDepthId, cut.CrackDepth * strength);
                compute.SetFloat(CrackProfileId, 1f);
                compute.SetFloat(ReachId, 4f * depth + cut.CrackDepth + 4f * cellSize);
                SetWarp(compute, cut.Warp * cellLength / cut.Size, cut.WarpSize > 0f ? cut.WarpSize : 0.7f * cellLength);
                run.Step(kernel);
                cellLength /= cut.SizeRatio;
                strength *= cut.Strength;
            }
            run.ClampCarve();
            run.Redistance(RebuildBand);
        }

        private static void RunSplit(Run run, in RockPass pass, float cellSize, Vector3 rockSize)
        {
            RockSplitTree.Build(pass.Split, rockSize, pass.Seed, out RockSplitNode[] nodes, out RockSplitLeaf[] leaves);
            if (nodes.Length == 0) return;
            ComputeShader compute = run.Compute;
            RockSplitSettings split = pass.Split;
            SetEdgeFocus(run, pass);
            using var nodeBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, nodes.Length,
                Marshal.SizeOf<RockSplitNode>());
            using var leafBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, leaves.Length,
                Marshal.SizeOf<RockSplitLeaf>());
            nodeBuffer.SetData(nodes);
            leafBuffer.SetData(leaves);

            int kernel = compute.FindKernel("SplitPass");
            compute.SetBuffer(kernel, SplitNodesId, nodeBuffer);
            compute.SetBuffer(kernel, SplitLeavesId, leafBuffer);
            compute.SetFloat(CrackWidthId, split.CrackWidth);
            compute.SetFloat(CrackDepthId, split.CrackDepth);
            compute.SetFloat(ReachId, 2f * split.Depth + split.CrackDepth + 4f * cellSize);
            SetWarp(compute, split.Warp, split.WarpSize);
            run.Step(kernel);
            run.ClampCarve();
            run.Redistance(RebuildBand);
        }

        /// <summary>
        /// Signed distance of each point to the nearest crack of the last Cracks pass, divided by the age share of that crack.
        /// The rock shader draws a thin line where it is near 0. Returns null when the passes have no Cracks pass.
        /// </summary>
        public static float[] CrackLineDistances(float4[] planes, int blockCount, Vector3 rockSize,
            IReadOnlyList<RockPass> passes, NativeArray<float3> points, NativeArray<float3> normals)
        {
            int found = -1;
            for (int index = 0; index < passes.Count; index++)
            {
                if (passes[index].Kind == RockPassKind.Cracks && !passes[index].Skip) found = index;
            }
            if (found < 0 || points.Length == 0) return null;
            RockPass pass = passes[found];
            RockCrackNode[] nodes = RockCrackTree.Build(pass.Cracks, planes, blockCount, rockSize, pass.Seed);
            if (nodes.Length == 0) return null;

            var compute = Resources.Load<ComputeShader>(ComputeName);
            int kernel = compute.FindKernel("CrackLines");
            using var nodeBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, nodes.Length,
                Marshal.SizeOf<RockCrackNode>());
            using var pointBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, points.Length, 12);
            using var normalBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, points.Length, 12);
            using var distanceBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, points.Length, 4);
            nodeBuffer.SetData(nodes);
            pointBuffer.SetData(points);
            normalBuffer.SetData(normals);
            compute.SetBuffer(kernel, CrackNodesId, nodeBuffer);
            compute.SetBuffer(kernel, CrackPointsId, pointBuffer);
            compute.SetBuffer(kernel, CrackNormalsId, normalBuffer);
            compute.SetBuffer(kernel, CrackLineDistancesId, distanceBuffer);
            compute.SetInt(CrackNodeCountId, nodes.Length);
            compute.SetInt(CrackPointCountId, points.Length);
            compute.SetFloat(CrackAgeId, pass.Cracks.AgeFalloff);
            compute.SetFloat(CrackRoughId, pass.Cracks.Roughness);
            compute.SetFloat(CrackRoughFrequencyId, 1f / Mathf.Max(pass.Cracks.RoughnessSize, 0.1f));
            compute.Dispatch(kernel, (points.Length + 63) / 64, 1, 1);
            var distances = new float[points.Length];
            distanceBuffer.GetData(distances);
            return distances;
        }

        private static void RunCracks(Run run, in RockPass pass, float4[] planes, int blockCount, float cellSize,
            Vector3 rockSize)
        {
            RockCrackSettings cracks = pass.Cracks;
            // With no depth and no weathering, the cracks are lines of the rock shader only.
            if (cracks.CrackDepth <= 0f && cracks.Weathering <= 0f) return;
            RockCrackNode[] nodes = RockCrackTree.Build(cracks, planes, blockCount, rockSize, pass.Seed);
            if (nodes.Length == 0) return;
            ComputeShader compute = run.Compute;
            using var nodeBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, nodes.Length,
                Marshal.SizeOf<RockCrackNode>());
            nodeBuffer.SetData(nodes);

            int kernel = compute.FindKernel("CrackPass");
            compute.SetBuffer(kernel, CrackNodesId, nodeBuffer);
            compute.SetInt(CrackNodeCountId, nodes.Length);
            compute.SetFloat(CrackWidthId, cracks.CrackWidth);
            compute.SetFloat(CrackDepthId, cracks.CrackDepth);
            compute.SetFloat(CrackAgeId, cracks.AgeFalloff);
            compute.SetFloat(CrackWeatheringId, cracks.Weathering);
            compute.SetFloat(CrackRoughId, cracks.Roughness);
            compute.SetFloat(CrackRoughFrequencyId, 1f / Mathf.Max(cracks.RoughnessSize, 0.1f));
            compute.SetFloat(ReachId, 4f * cellSize);
            run.Step(kernel);
            run.ClampCarve();
            run.Redistance(RebuildBand);
        }

        private static void RunCarve(Run run, in RockPass pass, float cellSize)
        {
            ComputeShader compute = run.Compute;
            RockCarveSettings carve = pass.Carve;
            SetEdgeFocus(run, pass);
            compute.SetMatrix(FrameId, Matrix4x4.Rotate(Quaternion.Euler(carve.Rotation)).inverse);
            compute.SetInt(UseEdgeMaskId, carve.OnCellEdges ? 1 : 0);
            int kernel = compute.FindKernel("CarvePass");
            float boxSize = carve.Size;
            float shortest = Mathf.Min(carve.Stretch.x, Mathf.Min(carve.Stretch.y, carve.Stretch.z));
            for (int octave = 0; octave < carve.Octaves; octave++)
            {
                float share = boxSize / carve.Size;
                compute.SetInt(SeedId, pass.Seed + octave * 173);
                compute.SetVector(CellScaleId, Vector3.Scale(Vector3.one / boxSize, Inverse(carve.Stretch)));
                // Lengths in stretched space, scaled to meters. The short axis gives a conservative distance.
                compute.SetFloat(DistanceScaleId, boxSize * shortest);
                compute.SetFloat(CutDepthId, carve.Depth * share);
                compute.SetFloat(CutShareId, carve.Share);
                compute.SetFloat(ReachId, 2f * cellSize);
                SetWarp(compute, carve.Warp * share, 0.7f * boxSize);
                run.Step(kernel);
                boxSize /= carve.SizeRatio;
            }
            run.ClampCarve();
            run.Redistance(RebuildBand);
        }

        private static void RunFractures(Run run, in RockFractureSettings settings, IReadOnlyList<RockFracture> fractures,
            float cellSize)
        {
            // A fracture with no step and no crack exists only in the surface layers.
            var active = new List<RockFracture>(fractures.Count);
            foreach (RockFracture fracture in fractures)
            {
                if (fracture.Step > 0f || fracture.CrackDepth > 0f) active.Add(fracture);
            }
            if (active.Count == 0) return;
            ComputeShader compute = run.Compute;
            RockFracture[] items = active.ToArray();
            using var buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, items.Length,
                Marshal.SizeOf<RockFracture>());
            buffer.SetData(items);

            int kernel = compute.FindKernel("FracturePass");
            compute.SetBuffer(kernel, FracturesId, buffer);
            compute.SetBuffer(kernel, EdgeMaskOutId, run.EdgeMask);
            compute.SetInt(FractureCountId, items.Length);
            compute.SetFloat(StepLimitId, settings.StepLimit);
            compute.SetFloat(StepBreakupId, settings.Breakup);
            compute.SetFloat(StepBreakupRateId, 1f / Mathf.Max(settings.BreakupSize, 0.05f));
            compute.SetFloat(CrackWeatheringId, settings.EdgeWeathering);
            compute.SetInt(WriteEdgeMaskId, 1);
            compute.SetFloat(EdgeMaskWidthId, FractureDamageWidth);
            compute.SetFloat(ReachId, 4f * cellSize + 1f);
            SetWarp(compute, settings.Warp > 0f ? settings.Warp : FractureWarp, settings.WarpSize > 0f ? settings.WarpSize : FractureWarpSize);
            run.Step(kernel);
            run.ClampCarve();
            run.Redistance(RebuildBand);
        }

        private static void RunSmooth(Run run, in RockSmoothSettings smooth)
        {
            ComputeShader compute = run.Compute;
            run.Blur(smooth.Radius);
            compute.SetFloat(WearId, smooth.Wear);
            compute.SetFloat(FillId, smooth.Fill);
            compute.SetFloat(PatchyId, smooth.Patchy);
            compute.SetFloat(FrequencyId, 1f / smooth.PatchSize);
            run.Step(compute.FindKernel("SmoothPass"));
        }

        private static void SetEdgeFocus(Run run, in RockPass pass)
        {
            if (pass.EdgeFocus == 0f) return;
            run.Blur(pass.EdgeRadius);
            run.Compute.SetFloat(EdgeFocusId, pass.EdgeFocus);
            // A square edge lifts the blurred field by about a quarter of the reach of the blur.
            run.Compute.SetFloat(EdgeRangeId, 0.25f * pass.EdgeRadius);
        }

        private static void SetWarp(ComputeShader compute, float amount, float size)
        {
            compute.SetFloat(WarpAmountId, amount);
            compute.SetFloat(WarpFrequencyId, 1f / Mathf.Max(size, 0.01f));
        }

        private static Vector3 Inverse(Vector3 value)
        {
            return new Vector3(1f / Mathf.Max(value.x, 0.01f), 1f / Mathf.Max(value.y, 0.01f),
                1f / Mathf.Max(value.z, 0.01f));
        }

        /// <summary>The field buffers of one build. Each kernel reads the current buffer and writes the other buffer.</summary>
        private sealed class Run : IDisposable
        {
            public readonly ComputeShader Compute;
            public readonly GraphicsBuffer Tags;
            public readonly GraphicsBuffer EdgeMask;

            private readonly int3 _groups;
            private readonly float _cellSize;
            private readonly int _blurKernel;
            private readonly GraphicsBuffer _blurred;
            private readonly GraphicsBuffer _blurStep;
            private readonly GraphicsBuffer _fence;
            private readonly GraphicsBuffer _base;
            private float _maxCarve;
            private readonly GraphicsBuffer _nearestA;
            private readonly GraphicsBuffer _nearestB;
            private GraphicsBuffer _current;
            private GraphicsBuffer _next;

            public Run(ComputeShader compute, int3 size, float cellSize, int sampleCount)
            {
                Compute = compute;
                _cellSize = cellSize;
                _groups = (size + ThreadGroupSize - 1) / ThreadGroupSize;
                _blurKernel = compute.FindKernel("BlurPass");
                _current = NewBuffer(sampleCount);
                _next = NewBuffer(sampleCount);
                _blurred = NewBuffer(sampleCount);
                _blurStep = NewBuffer(sampleCount);
                _fence = NewBuffer(1);
                _base = NewBuffer(sampleCount);
                _nearestA = new GraphicsBuffer(GraphicsBuffer.Target.Structured, sampleCount, 3 * sizeof(float));
                _nearestB = new GraphicsBuffer(GraphicsBuffer.Target.Structured, sampleCount, 3 * sizeof(float));
                Tags = NewBuffer(sampleCount);
                // A new buffer has no defined content. Samples far from the surface keep this value.
                using var zeros = new NativeArray<float>(sampleCount, Allocator.TempJob);
                Tags.SetData(zeros);
                EdgeMask = NewBuffer(sampleCount);
                EdgeMask.SetData(zeros);
            }

            public void Step(int kernel)
            {
                Compute.SetBuffer(kernel, SourceId, _current);
                Compute.SetBuffer(kernel, FieldId, _next);
                Compute.SetBuffer(kernel, BlurredId, _blurred);
                Compute.SetBuffer(kernel, BaseId, _base);
                Compute.SetBuffer(kernel, EdgeMaskId, EdgeMask);
                DispatchSlices(kernel);
                (_current, _next) = (_next, _current);
            }

            /// <summary>Makes a field from the CPU the current field.</summary>
            public void Load(NativeArray<float> field)
            {
                _current.SetData(field);
            }

            /// <summary>Keeps the current field as the base. Carving stops this far below its surface. 0 turns the limit off.</summary>
            public void KeepBase(float maxCarve)
            {
                _maxCarve = maxCarve;
                Compute.SetFloat(MaxCarveId, maxCarve);
                int kernel = Compute.FindKernel("OffsetPass");
                Compute.SetFloat(AmountId, 0f);
                Compute.SetBuffer(kernel, SourceId, _current);
                Compute.SetBuffer(kernel, FieldId, _base);
                DispatchSlices(kernel);
            }

            /// <summary>Limits the depth of all carving so far, so a solid core remains.</summary>
            public void ClampCarve()
            {
                if (_maxCarve > 0f) Step(Compute.FindKernel("ClampCarve"));
            }

            /// <summary>Copies the current field to the blurred buffer.</summary>
            public void KeepInBlurred()
            {
                int kernel = Compute.FindKernel("OffsetPass");
                Compute.SetFloat(AmountId, 0f);
                Compute.SetBuffer(kernel, SourceId, _current);
                Compute.SetBuffer(kernel, FieldId, _blurred);
                DispatchSlices(kernel);
            }

            /// <summary>Writes a Gaussian blur of the current field to the blurred buffer.</summary>
            public void Blur(float radius)
            {
                float sigma = Mathf.Max(0.5f * radius / _cellSize, 0.5f);
                Compute.SetFloat(BlurSigmaId, sigma);
                Compute.SetInt(BlurRadiusId, Mathf.Min(Mathf.CeilToInt(3f * sigma), MaximumBlurRadius));
                BlurAxis(1, 0, 0, _current, _blurred);
                BlurAxis(0, 1, 0, _blurred, _blurStep);
                BlurAxis(0, 0, 1, _blurStep, _blurred);
            }

            /// <summary>
            /// Makes the current field a true distance again, up to the band in meters, and keeps its surface.
            /// </summary>
            public void Redistance(float band)
            {
                GraphicsBuffer from = _nearestA;
                GraphicsBuffer to = _nearestB;
                int seed = Compute.FindKernel("RedistanceSeed");
                Compute.SetBuffer(seed, SourceId, _current);
                Compute.SetBuffer(seed, NearestOutId, from);
                DispatchSlices(seed);

                int sweep = Compute.FindKernel("RedistanceSweep");
                int longest = Mathf.NextPowerOfTwo(Mathf.CeilToInt(band / _cellSize));
                // The last jump of 1 runs twice, which removes most errors of jump flooding.
                for (int jump = longest; jump >= 1; jump = jump > 1 ? jump / 2 : 0)
                {
                    int repeats = jump == 1 ? 2 : 1;
                    for (int repeat = 0; repeat < repeats; repeat++)
                    {
                        Compute.SetInt(JumpLengthId, jump);
                        Compute.SetBuffer(sweep, NearestInId, from);
                        Compute.SetBuffer(sweep, NearestOutId, to);
                        DispatchSlices(sweep);
                        (from, to) = (to, from);
                    }
                }

                int finish = Compute.FindKernel("RedistanceFinish");
                Compute.SetBuffer(finish, SourceId, _current);
                Compute.SetBuffer(finish, NearestInId, from);
                Compute.SetBuffer(finish, FieldId, _next);
                DispatchSlices(finish);
                (_current, _next) = (_next, _current);
            }

            public NativeArray<float> ReadBack(int sampleCount)
            {
                return Read(_current, sampleCount);
            }

            public NativeArray<float> ReadTags(int sampleCount)
            {
                return Read(Tags, sampleCount);
            }

            private static NativeArray<float> Read(GraphicsBuffer buffer, int sampleCount)
            {
                var values = new NativeArray<float>(sampleCount, Allocator.Persistent,
                    NativeArrayOptions.UninitializedMemory);
                // The status is read in this call. A done request expires at the end of the frame.
                AsyncGPUReadbackRequest request = AsyncGPUReadback.RequestIntoNativeArray(ref values, buffer);
                request.WaitForCompletion();
                if (request.hasError)
                {
                    values.Dispose();
                    throw new InvalidOperationException("The rock field readback failed.");
                }
                return values;
            }

            public void Dispose()
            {
                _current.Dispose();
                _next.Dispose();
                _blurred.Dispose();
                _blurStep.Dispose();
                _fence.Dispose();
                _base.Dispose();
                _nearestA.Dispose();
                _nearestB.Dispose();
                Tags.Dispose();
                EdgeMask.Dispose();
            }

            private void DispatchSlices(int kernel)
            {
                for (int group = 0; group < _groups.z; group += SliceGroups)
                {
                    Compute.SetInt(SliceStartId, group * ThreadGroupSize);
                    Compute.Dispatch(kernel, _groups.x, _groups.y, Mathf.Min(SliceGroups, _groups.z - group));
                    // The read ends after the slice ends, so the queue of the GPU stays short.
                    AsyncGPUReadback.Request(_fence).WaitForCompletion();
                }
            }

            private void BlurAxis(int x, int y, int z, GraphicsBuffer source, GraphicsBuffer target)
            {
                Compute.SetInts(BlurAxisId, x, y, z);
                Compute.SetBuffer(_blurKernel, SourceId, source);
                Compute.SetBuffer(_blurKernel, FieldId, target);
                DispatchSlices(_blurKernel);
            }

            private static GraphicsBuffer NewBuffer(int count)
            {
                return new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, sizeof(float));
            }
        }

        /// <summary>Makes six planes for each authored box.</summary>
        public static float4[] MakeBoxPlanes(IReadOnlyList<RockBaseBox> boxes)
        {
            var planes = new float4[boxes.Count * 6];
            for (int box = 0; box < boxes.Count; box++)
            {
                RockBaseBox item = boxes[box];
                Quaternion turn = Quaternion.Euler(item.Rotation);
                for (int axis = 0; axis < 3; axis++)
                {
                    var direction = (float3)(turn * Vector3.right);
                    if (axis == 1) direction = (float3)(turn * Vector3.up);
                    if (axis == 2) direction = (float3)(turn * Vector3.forward);
                    float reach = 0.5f * item.Size[axis];
                    float center = math.dot(direction, (float3)item.Center);
                    planes[box * 6 + axis * 2] = new float4(direction, center + reach);
                    planes[box * 6 + axis * 2 + 1] = new float4(-direction, reach - center);
                }
            }
            return planes;
        }

        /// <summary>
        /// Makes the planes of convex blocks inside the extents. Each block has its planes around an ellipsoid. The
        /// directions spread evenly on a sphere and then move at random, so the block closes and has no regular shape.
        /// </summary>
        public static float4[] MakeBlockPlanes(float3 extents, int blockCount, int planesPerBlock, uint seed)
        {
            var random = new Unity.Mathematics.Random(seed * 2654435761u + 1u);
            var planes = new float4[blockCount * planesPerBlock];
            for (int block = 0; block < blockCount; block++)
            {
                // The first block is the main mass. The other blocks sit against it.
                float3 radii = extents * (block == 0 ? random.NextFloat3(0.6f, 0.8f) : random.NextFloat3(0.3f, 0.55f));
                float3 center = (extents - radii) * random.NextFloat3(-1f, 1f) * (block == 0 ? 0.2f : 0.9f);
                float turn = random.NextFloat(0f, 2f * math.PI);
                for (int plane = 0; plane < planesPerBlock; plane++)
                {
                    float height = 1f - (2f * plane + 1f) / planesPerBlock;
                    float ring = math.sqrt(math.max(0f, 1f - height * height));
                    float angle = turn + plane * 2.39996323f;
                    var direction = new float3(ring * math.cos(angle), height, ring * math.sin(angle));
                    direction = math.normalize(direction + random.NextFloat3Direction() * 0.25f);
                    float reach = math.sqrt(math.dot(radii * radii, direction * direction));
                    float distance = math.dot(center, direction) + reach * random.NextFloat(0.8f, 1.05f);
                    planes[block * planesPerBlock + plane] = new float4(direction, distance);
                }
            }
            return planes;
        }
    }
}
