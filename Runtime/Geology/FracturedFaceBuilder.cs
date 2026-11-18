using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace LoogaSoft.Terrain.Geology
{
    /// <summary>
    /// Fractured face of a cliff wall (prototype): a shell of rock blocks in place of the smooth face, after the
    /// implicit block model of Paris et al. 2020 ("Modeling Rocky Scenery using Implicit Blocks"). Bedding planes and
    /// joints cut the rock into blocks. Each block is a bevelled box with a gap to its neighbours, so the joints are real
    /// cracks. Front blocks of hard beds stand out, soft beds go back, and some front blocks are missing.
    /// </summary>
    [Serializable]
    public sealed class CliffWallFracture
    {
        [Tooltip("Build the face from blocks in the range. The top of the wall keeps its own surface.")]
        public bool Enabled;
        [Tooltip("Share of the wall length, from and to, where the face is built from blocks.")]
        public Vector2 Range = new(0f, 1f);
        [Min(0.5f), Tooltip("Distance at each end of the range over which the blocks go back into the face, in meters.")]
        public float EndFade = 5f;
        [Min(0.1f), Tooltip("Size of one cell of the block surface grid at the finest level of detail, in meters. Small " +
                            "cells keep narrow cracks. Each coarser level doubles it.")]
        public float CellSize = 0.2f;
        [Tooltip("Smallest and largest bed thickness, in meters.")]
        public Vector2 BedThickness = new(0.25f, 1f);
        [Range(0f, 1f), Tooltip("Share of hard beds. Hard beds stand out, soft beds go back.")]
        public float HardShare = 0.45f;
        [Range(-15f, 15f), Tooltip("Dip of the beds along the wall, in degrees.")]
        public float Dip = 2f;
        [Min(0f), Tooltip("Largest rise and fall of the bed boundaries along the wall, in meters. Each boundary moves on " +
                          "its own, so the beds thicken and thin (lenses) and no two beds run parallel.")]
        public float Undulation = 0.35f;
        [Min(2f), Tooltip("Typical length of one rise or fall of the bed boundaries along the wall, in meters.")]
        public float UndulationLength = 14f;
        [Min(2f), Tooltip("Typical length along the wall over which the step of a bed comes and goes, in meters. A ledge " +
                          "does not run the whole wall.")]
        public float LedgeContinuity = 10f;
        [Tooltip("Smallest and largest distance between the joints of one bed, in meters.")]
        public Vector2 JointSpacing = new(0.8f, 2.8f);
        [Range(-0.3f, 0.3f), Tooltip("Horizontal lean of the joints per meter of height.")]
        public float JointLean = 0.05f;
        [Min(0.2f), Tooltip("Depth of the front layer of blocks, in meters. A missing front block leaves a socket this deep.")]
        public float FrontDepth = 0.45f;
        [Min(0f), Tooltip("Largest outward step of the front blocks of a hard bed, in meters.")]
        public float HardProtrusion = 0.18f;
        [Min(0f), Tooltip("Largest inward step of the front blocks of a soft bed, in meters.")]
        public float SoftRecess = 0.15f;
        [Range(0f, 1f), Tooltip("Share of missing front blocks in soft beds. Hard beds lose a third of this share.")]
        public float FallOut = 0.15f;
        [Min(0f), Tooltip("Width of the cracks between blocks, in meters.")]
        public float Gap = 0.1f;
        [Min(0.005f), Tooltip("Radius of the bevel on the block edges, in meters.")]
        public float Bevel = 0.015f;
        [Range(0f, 1f), Tooltip("Share of the front edges of a block that are chipped off.")]
        public float ChipChance = 0.6f;
        [Tooltip("Smallest and largest size of a chip at a front edge, in meters. The size changes along the edge, so " +
                 "the edge is jagged.")]
        public Vector2 ChipSize = new(0.04f, 0.16f);
        [Range(0f, 15f), Tooltip("Largest tilt of a block front, in degrees.")]
        public float Tilt = 3f;

        [Header("Panels")]
        [Tooltip("Smallest and largest length of a wall panel, in meters. Major joints split the wall into panels. " +
                 "Each panel leans and steps out as a whole, as the face of a real quarry wall follows its joints.")]
        public Vector2 PanelLength = new(8f, 24f);
        [Range(0f, 25f), Tooltip("Largest lean of a panel from the designed face, in degrees. Positive leans hang the " +
                                "top out (toppling); negative leans set the top back (sliding).")]
        public float PanelLean = 12f;
        [Range(0f, 15f), Tooltip("Largest change of the lean from one end of a panel to the other, in degrees.")]
        public float PanelTwist = 5f;
        [Min(0f), Tooltip("Largest outward step of a whole panel, in meters.")]
        public float PanelStep = 0.6f;
        [Min(0f), Tooltip("Largest outward offset of a panel at any point, in meters.")]
        public float PanelReach = 2.5f;
        [Range(0f, 1f), Tooltip("Share of panels that split at a bed into a lower and an upper part with their own lean.")]
        public float TierChance = 0.5f;
        [Min(0.02f), Tooltip("Width of the major joints between panels, in meters.")]
        public float PanelGap = 0.15f;
        public int Seed = 1;
    }

    // Builds the block shell of a fractured cliff face. The shell is a grid along the wall (u), up (y) and outward from
    // the designed face (d). The distance field of the blocks is sampled on it and meshed with surface nets, once for
    // each level of detail. The meshes are split into chunks along the wall. The grid follows the face, so the block
    // fronts follow the shape of the wall.
    internal static class FracturedFaceBuilder
    {
        public const int LevelCount = 3;

        // Face of one wall column: the frame and the designed face (without the rock relief) as outward distance at each
        // height, from the top down.
        internal sealed class FaceColumn
        {
            public float U;
            public Vector3 Origin;
            public Vector3 Outward;
            public readonly List<Vector2> Face = new();
            public float Top;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Vertex
        {
            public Vector3 Position;
            public half4 Normal;
            public Color32 Color;
            public half2 GroundOffset;
            public half4 Cover;
        }

        // The same layout as the rock face chunks, for the rock face shaders.
        private static readonly VertexAttributeDescriptor[] Layout =
        {
            new(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
            new(VertexAttribute.Normal, VertexAttributeFormat.Float16, 4),
            new(VertexAttribute.Color, VertexAttributeFormat.UNorm8, 4),
            new(VertexAttribute.TexCoord1, VertexAttributeFormat.Float16, 2),
            new(VertexAttribute.TexCoord2, VertexAttributeFormat.Float16, 4)
        };

        // Overlap of a front block into the back rock, in meters, so they join without a crack.
        private const float BackOverlap = 0.3f;
        // Distance outward at which the cavity test samples the rock, in meters.
        private const float CavityReach = 0.4f;
        // Change of the front of each front block and of the back rock, in meters.
        private const float FrontJitter = 0.05f;
        private const float BackJitter = 0.06f;
        // Front of the blocks at the range ends, behind the old face, in meters.
        private const float EndRecess = 0.4f;
        // The lid under the wall top: its least depth below the top and its overlap into the top, in meters. It closes
        // the sockets and cracks of the top beds, which looked through to the sky under a slab top. A lid thinner than
        // about two grid cells drops out of the mesh, so it grows with the cell size.
        private const float LidDepth = 0.15f;
        private const float LidOverlap = 0.05f;
        private const float LidOverlapLimit = 0.15f;
        // Distance from a cave at which the blocks end, in meters. The opening is cut through the shell.
        private const float CaveClearance = 0.3f;
        // Largest length of one chunk along the wall, in meters.
        private const float ChunkLength = 20f;

        private sealed class Bed
        {
            public float Bottom;
            public float Top;
            public float Hardness;
            public float Protrusion;
            public float Layer;
            public float[] Joints;
        }

        // A part of the wall between two major joints. It can split at a height into a lower and an upper tier.
        private sealed class Panel
        {
            public float Start;
            public float End;
            public float Split = float.NaN;
            public readonly float[] Step = new float[2];
            public readonly float[] LeanStart = new float[2];
            public readonly float[] LeanEnd = new float[2];
        }

        private sealed class Field
        {
            public CliffWallFracture Fracture;
            public Bed[] Beds;
            public float DipSlope;
            public float UMinimum;
            public float UMaximum;
            public float BackFront;
            // True when the shell ends under a slab top: it ends at the top height and forms the slab front.
            public bool SlabTop;
            public Panel[] Panels;
            public float YMinimum;

            // The rock: each panel tier is the block field moved out by its offset, kept between its joints and tier
            // heights. A back rock behind all panels closes the major joints at depth.
            public float Signed(float u, float y, float d, float top, float gridCell)
            {
                if (Panels == null || Panels.Length == 0) return BlockSigned(u, y, d, top, gridCell);
                CliffWallFracture f = Fracture;
                float halfGap = 0.5f * f.PanelGap;
                float reach = halfGap + 2f * gridCell;
                int panel = FindPanel(u);
                float best = float.MaxValue;
                // The least offset of the panel tiers here: the major joints close at the back of the lower panel, so
                // they are as deep as the front blocks and not as deep as the panel step.
                float leastOffset = float.MaxValue;
                for (int index = Mathf.Max(panel - 1, 0); index <= Mathf.Min(panel + 1, Panels.Length - 1); index++)
                {
                    Panel part = Panels[index];
                    float sideU = Mathf.Max(index == 0 ? float.MinValue : part.Start + halfGap - u,
                        index == Panels.Length - 1 ? float.MinValue : u - (part.End - halfGap));
                    if (index != panel && sideU > reach) continue;
                    for (int tier = 0; tier < 2; tier++)
                    {
                        float sideY;
                        if (float.IsNaN(part.Split))
                        {
                            if (tier == 1) break;
                            sideY = float.MinValue;
                        }
                        else
                        {
                            sideY = tier == 0 ? y - (part.Split - halfGap) : part.Split + halfGap - y;
                            if (sideY > reach) continue;
                        }
                        float offset = PanelOffset(part, tier, u, y, top);
                        leastOffset = Mathf.Min(leastOffset, offset);
                        // Far in front of the moved rock: air, without the block field.
                        float shifted = d - offset;
                        float value = shifted > f.HardProtrusion + FrontJitter + 2f * gridCell
                            ? shifted - f.HardProtrusion - FrontJitter
                            : BlockSigned(u, y, shifted, top, gridCell);
                        best = Mathf.Min(best, Mathf.Max(value, Mathf.Max(sideU, sideY)));
                    }
                }
                float deep = Mathf.Max(d - (leastOffset + BackFront - BackOverlap - 0.1f),
                    Mathf.Max(y - top, Mathf.Max(UMinimum - u, u - UMaximum)));
                return Mathf.Min(best, deep);
            }

            // Outward offset of a panel tier: its step, and its lean about its lower edge (top out) or its upper edge
            // (top back), twisted along the panel. It fades out at the range ends.
            private float PanelOffset(Panel panel, int tier, float u, float y, float top)
            {
                CliffWallFracture f = Fracture;
                float share = Mathf.Clamp01((u - panel.Start) / Mathf.Max(panel.End - panel.Start, 0.1f));
                float lean = Mathf.Lerp(panel.LeanStart[tier], panel.LeanEnd[tier], share);
                float slope = Mathf.Tan(lean * Mathf.Deg2Rad);
                float bottom = tier == 0 || float.IsNaN(panel.Split) ? YMinimum : panel.Split;
                float upper = tier == 1 || float.IsNaN(panel.Split) ? top : panel.Split;
                float offset = panel.Step[tier] + (slope >= 0f ? slope * (y - bottom) : -slope * (upper - y));
                return Mathf.Clamp(offset, 0f, f.PanelReach) * EndWeight(u);
            }

            private int FindPanel(float u)
            {
                int low = 0;
                int high = Panels.Length - 1;
                while (low < high)
                {
                    int middle = (low + high + 1) >> 1;
                    if (Panels[middle].Start <= u) low = middle;
                    else high = middle - 1;
                }
                return low;
            }

            // Share of the full block relief: 0 at the range ends, where the blocks go back into the face.
            public float EndWeight(float u)
            {
                float fade = Mathf.Max(Fracture.EndFade, 0.5f);
                return RockFaceBuilder.Smooth01(Mathf.Min(u - UMinimum, UMaximum - u) / fade);
            }

            // The block field of one panel, in its own face distance.
            private float BlockSigned(float u, float y, float d, float top, float gridCell)
            {
                float bedY = BedY(u, y);
                float weight = EndWeight(u);
                int bed = FindBed(bedY);
                float best = float.MaxValue;
                for (int index = Mathf.Max(bed - 1, 0); index <= Mathf.Min(bed + 1, Beds.Length - 1); index++)
                {
                    Bed layer = Beds[index];
                    float jointU = u - Fracture.JointLean * (bedY - layer.Bottom);
                    int joint = FindJoint(layer.Joints, jointU);
                    for (int cell = Mathf.Max(joint - 1, 0); cell <= Mathf.Min(joint + 1, layer.Joints.Length - 2); cell++)
                    {
                        best = Mathf.Min(best, BlockPair(layer, index, cell, jointU, bedY, d, weight));
                    }
                }
                // A lid under the wall top closes the top beds.
                float lidDepth = Mathf.Max(LidDepth, 2.5f * gridCell);
                float overlap = SlabTop ? 0f : Mathf.Clamp(0.5f * gridCell, LidOverlap, LidOverlapLimit);
                float lid = Mathf.Max(Mathf.Max(d, BackFront - BackOverlap - d), top - lidDepth - y);
                best = Mathf.Min(best, lid);
                // The shell ends in the wall top and at the range ends.
                return Mathf.Max(Mathf.Max(best, y - (top + overlap)), Mathf.Max(UMinimum - u, u - UMaximum));
            }

            // Height in bed space: the dip, and a smooth warp that changes with the height too, so each bed boundary
            // rises and falls on its own. Its slope over the height stays below 1, so the beds do not fold.
            public float BedY(float u, float y)
            {
                CliffWallFracture f = Fracture;
                float length = Mathf.Max(f.UndulationLength, 2f);
                float warp = 0.7f * Signed(u / length, y / 3.5f + 3.1f) + 0.3f * Signed(u / (0.37f * length), y / 1.6f + 9.7f);
                return y - DipSlope * (u - UMinimum) - f.Undulation * warp;
            }

            // Share of the step of a bed at a wall distance: the ledges come and go.
            private float Continuity(float u, int bedIndex)
            {
                float noise = RockFaceNoise.Perlin(u / Mathf.Max(Fracture.LedgeContinuity, 2f) + 13.3f,
                    bedIndex * 1.71f + Fracture.Seed * 0.37f);
                return Mathf.Lerp(0.2f, 1f, RockFaceBuilder.Smooth01((noise - 0.3f) / 0.4f));
            }

            private static float Signed(float x, float y)
            {
                return 2f * RockFaceNoise.Perlin(x, y) - 1f;
            }

            // The back rock and, when it is not missing, the front block of one cell.
            private float BlockPair(Bed bed, int bedIndex, int cell, float u, float y, float d, float weight)
            {
                CliffWallFracture f = Fracture;
                float left = bed.Joints[cell];
                float right = bed.Joints[cell + 1];
                float half = 0.5f * f.Gap;
                float sideU = Mathf.Max(left + half - u, u - (right - half));
                float sideY = Mathf.Max(bed.Bottom + half - y, y - (bed.Top - half));
                float centreU = 0.5f * (left + right);
                float centreY = 0.5f * (bed.Bottom + bed.Top);
                int seed = f.Seed * 977;

                // The back rock has a front with small steps but no back.
                float backFront = BackFront + BackJitter * (2f * Hash(bedIndex, cell, seed + 1) - 1f);
                float distance = RoundedBox(sideU, sideY, d - backFront, f.Bevel);

                // Front blocks are missing only away from the range ends.
                bool missing = weight > 0.7f &&
                               Hash(bedIndex, cell, seed + 2) < f.FallOut * Mathf.Lerp(1f, 0.33f, bed.Hardness);
                if (missing) return distance;

                float tilt = Mathf.Tan(f.Tilt * Mathf.Deg2Rad);
                float tiltU = (2f * Hash(bedIndex, cell, seed + 3) - 1f) * tilt;
                float tiltY = (2f * Hash(bedIndex, cell, seed + 4) - 1f) * tilt;
                float front = bed.Protrusion * Continuity(centreU, bedIndex) +
                              FrontJitter * (2f * Hash(bedIndex, cell, seed + 5) - 1f) +
                              tiltU * (u - centreU) + tiltY * (y - centreY);
                front = Mathf.Lerp(-EndRecess, front, weight);
                float depth = Mathf.Max(d - front, backFront - BackOverlap - d);
                float block = RoundedBox(sideU, sideY, depth, f.Bevel);
                block = Mathf.Max(block, Chips(bedIndex, cell, u, y, d, front, left + half, right - half,
                    bed.Bottom + half, bed.Top - half));
                return Mathf.Min(distance, block);
            }

            // Chips along the front edges of a block: a cut plane at each chipped edge, at a random angle, with a size
            // that changes along the edge. Positive outside the rock that the chips leave.
            private float Chips(int bedIndex, int cell, float u, float y, float d, float front, float left, float right,
                float bottom, float top)
            {
                CliffWallFracture f = Fracture;
                int seed = f.Seed * 977 + 31;
                float result = float.MinValue;
                for (int edge = 0; edge < 4; edge++)
                {
                    if (Hash(bedIndex * 4 + edge, cell, seed) >= f.ChipChance) continue;
                    // Distance from the edge into the block along the face: up from the bottom, down from the top, in
                    // from the left or the right. Along: the position on the edge, for the size change.
                    float inward = edge == 0 ? y - bottom : edge == 1 ? top - y : edge == 2 ? u - left : right - u;
                    float along = edge < 2 ? u : y;
                    float noise = RockFaceNoise.Perlin(along / 0.35f + bedIndex * 3.1f + edge * 7.7f, cell * 1.3f + 0.5f);
                    float size = Mathf.Lerp(f.ChipSize.x, f.ChipSize.y, noise * Hash(bedIndex * 4 + edge, cell, seed + 1));
                    // Steep or flat cut: the share of the size taken along the face and into the depth.
                    float slope = Mathf.Lerp(0.5f, 2f, Hash(bedIndex * 4 + edge, cell, seed + 2));
                    float plane = ((front - d) * slope + inward - size * (1f + slope) * 0.5f) / Mathf.Sqrt(1f + slope * slope);
                    result = Mathf.Max(result, -plane);
                }
                return result;
            }

            public int FindBed(float y)
            {
                int low = 0;
                int high = Beds.Length - 1;
                while (low < high)
                {
                    int middle = (low + high + 1) >> 1;
                    if (Beds[middle].Bottom <= y) low = middle;
                    else high = middle - 1;
                }
                return low;
            }

            private static int FindJoint(float[] joints, float u)
            {
                int low = 0;
                int high = joints.Length - 2;
                while (low < high)
                {
                    int middle = (low + high + 1) >> 1;
                    if (joints[middle] <= u) low = middle;
                    else high = middle - 1;
                }
                return low;
            }
        }

        // Mesh data of one level of detail before the split into chunks.
        private sealed class Level
        {
            public Vertex[] Vertices;
            public float[] VertexU;
            public bool[] Buried;
            public List<int> Indices;
            // Chunk of each vertex, or -2 for a vertex on a chunk border.
            public int[] Owner;
        }

        private sealed class ChunkData
        {
            public List<Vertex> Vertices;
            public List<int> Indices;
        }

        // Largest error of the mesh reduction for each level of detail, in squared meters (about 1, 2 and 4 cm).
        private static readonly double[] ReductionError = { 0.0001, 0.0004, 0.0016 };
        // Depth under the terrain below which triangles are left out, in meters.
        private const float BuriedDepth = 0.3f;

        /// <summary>
        /// Share of the full block relief at a wall distance: 0 at the range ends, where the old face shows again.
        /// </summary>
        public static float EndWeight(float u, Vector2 range, CliffWallFracture fracture)
        {
            float fade = Mathf.Max(fracture.EndFade, 0.5f);
            return RockFaceBuilder.Smooth01(Mathf.Min(u - range.x, range.y - u) / fade);
        }

        /// <summary>
        /// Builds the block shell over the face of consecutive wall columns: one array of level meshes for each chunk
        /// along the wall. Each column has a face with at least two points.
        /// </summary>
        /// <param name="caveDistance">Optional. Distance in meters from a world point to a cave. The blocks end around
        /// a cave opening in the face. Called from worker threads.</param>
        public static List<Mesh[]> Build(List<FaceColumn> columns, CliffWallFracture fracture,
            RockFaceSettings settings, TerrainHeightSampler terrain, string name, Func<Vector3, float> caveDistance,
            bool slabTop)
        {
            var chunks = new List<Mesh[]>();
            if (columns.Count < 2) return chunks;
            float uMinimum = columns[0].U;
            float uMaximum = columns[^1].U;
            float yMinimum = float.MaxValue;
            float yMaximum = float.MinValue;
            foreach (FaceColumn column in columns)
            {
                yMinimum = Mathf.Min(yMinimum, column.Face[^1].y);
                yMaximum = Mathf.Max(yMaximum, column.Top);
            }
            var field = new Field
            {
                Fracture = fracture,
                DipSlope = Mathf.Tan(fracture.Dip * Mathf.Deg2Rad),
                UMinimum = uMinimum,
                UMaximum = uMaximum,
                BackFront = -fracture.FrontDepth,
                SlabTop = slabTop
            };
            float dipRange = Mathf.Abs(field.DipSlope) * (uMaximum - uMinimum);
            field.Beds = BuildBeds(fracture, yMinimum - dipRange - 5f, yMaximum + dipRange + 5f, uMinimum - 10f,
                uMaximum + 10f);
            field.YMinimum = yMinimum;
            field.Panels = BuildPanels(fracture, uMinimum, uMaximum, yMinimum, yMaximum);

            var levels = new Level[LevelCount];
            for (int level = 0; level < LevelCount; level++)
            {
                levels[level] = BuildLevel(columns, field, fracture.CellSize * (1 << level), uMinimum, uMaximum,
                    yMinimum, yMaximum, settings, terrain, caveDistance);
            }

            // Chunks along the wall. A triangle belongs to the chunk of its first vertex, so the chunks share their
            // border vertices and meet without a crack. The vertices on a chunk border stay in the reduction.
            int chunkCount = Mathf.Max(1, Mathf.CeilToInt((uMaximum - uMinimum) / ChunkLength));
            float chunkLength = (uMaximum - uMinimum) / chunkCount;
            int ChunkOf(float u) => Mathf.Clamp(Mathf.FloorToInt((u - uMinimum) / chunkLength), 0, chunkCount - 1);
            foreach (Level level in levels)
            {
                level.Owner = new int[level.Vertices.Length];
                for (int index = 0; index < level.Owner.Length; index++) level.Owner[index] = -1;
                for (int index = 0; index < level.Indices.Count; index += 3)
                {
                    int chunk = ChunkOf(level.VertexU[level.Indices[index]]);
                    for (int corner = 0; corner < 3; corner++)
                    {
                        int vertex = level.Indices[index + corner];
                        level.Owner[vertex] = level.Owner[vertex] == -1 || level.Owner[vertex] == chunk ? chunk : -2;
                    }
                }
            }
            var data = new ChunkData[chunkCount * LevelCount];
            Parallel.For(0, data.Length, index =>
            {
                int chunk = index / LevelCount;
                int level = index % LevelCount;
                data[index] = ChunkMeshData(levels[level], chunk, ChunkOf, ReductionError[level]);
            });
            for (int chunk = 0; chunk < chunkCount; chunk++)
            {
                bool empty = false;
                for (int level = 0; level < LevelCount; level++) empty |= data[chunk * LevelCount + level] == null;
                if (empty) continue;
                var meshes = new Mesh[LevelCount];
                for (int level = 0; level < LevelCount; level++)
                {
                    meshes[level] = CreateMesh(data[chunk * LevelCount + level], name + " " + chunk + " LOD" + level);
                }
                chunks.Add(meshes);
            }
            return chunks;
        }

        private static Level BuildLevel(List<FaceColumn> columns, Field field, float cell, float uMinimum,
            float uMaximum, float yMinimum, float yMaximum, RockFaceSettings settings, TerrainHeightSampler terrain,
            Func<Vector3, float> caveDistance)
        {
            CliffWallFracture fracture = field.Fracture;
            // The shell grid: along the wall, up, and outward from the designed face. One sample past each end, so the
            // shell is closed there.
            float uStart = uMinimum - cell;
            int countU = Mathf.CeilToInt((uMaximum + cell - uStart) / cell) + 1;
            float yStart = yMinimum - cell;
            int countY = Mathf.CeilToInt((yMaximum + LidOverlap + 2f * cell - yStart) / cell) + 1;
            float dStart = -fracture.FrontDepth - BackOverlap - 2f * cell;
            float dEnd = fracture.PanelReach + fracture.HardProtrusion + FrontJitter + 2f * cell;
            int countD = Mathf.CeilToInt((dEnd - dStart) / cell) + 1;

            // Frame of each grid column along the wall, and the face at each grid height.
            var origins = new Vector3[countU];
            var outwards = new Vector3[countU];
            var tops = new float[countU];
            var faces = new float[countU * countY];
            Parallel.For(0, countU, iu =>
            {
                float u = Mathf.Clamp(uStart + iu * cell, uMinimum, uMaximum);
                int a = FindColumn(columns, u);
                FaceColumn first = columns[a];
                FaceColumn second = columns[Mathf.Min(a + 1, columns.Count - 1)];
                float t = second.U > first.U ? Mathf.Clamp01((u - first.U) / (second.U - first.U)) : 0f;
                origins[iu] = Vector3.Lerp(first.Origin, second.Origin, t);
                outwards[iu] = Vector3.Lerp(first.Outward, second.Outward, t).normalized;
                tops[iu] = Mathf.Lerp(first.Top, second.Top, t);
                for (int iy = 0; iy < countY; iy++)
                {
                    float y = yStart + iy * cell;
                    faces[iu * countY + iy] = Mathf.Lerp(FaceX(first, y), FaceX(second, y), t);
                }
            });

            var values = new float[countU * countY * countD];
            Parallel.For(0, countU, iu =>
            {
                float u = uStart + iu * cell;
                for (int iy = 0; iy < countY; iy++)
                {
                    float y = yStart + iy * cell;
                    // A cave opening cuts through the whole shell: the cave distance at the face holds for all depths.
                    float cave = float.MaxValue;
                    if (caveDistance != null)
                    {
                        cave = caveDistance(origins[iu] + outwards[iu] * faces[iu * countY + iy] + Vector3.up * y);
                    }
                    for (int id = 0; id < countD; id++)
                    {
                        float value = field.Signed(u, y, dStart + id * cell, tops[iu], cell);
                        values[(iu * countY + iy) * countD + id] = Mathf.Max(value, CaveClearance - cave);
                    }
                }
            });

            // Dual contouring: one vertex in each cell with rock and air corners, where the planes of the surface at the
            // edge crossings meet. Sharp block edges and corners stay sharp. The mean of the crossings (surface nets)
            // rounded every corner by about half a cell.
            var cellVertex = new int[(countU - 1) * (countY - 1) * (countD - 1)];
            var cells = new List<int>();
            for (int index = 0; index < cellVertex.Length; index++) cellVertex[index] = -1;
            for (int iu = 0; iu < countU - 1; iu++)
            {
                for (int iy = 0; iy < countY - 1; iy++)
                {
                    for (int id = 0; id < countD - 1; id++)
                    {
                        if (!HasCrossing(values, countY, countD, iu, iy, id)) continue;
                        int cellIndex = (iu * (countY - 1) + iy) * (countD - 1) + id;
                        cellVertex[cellIndex] = cells.Count;
                        cells.Add(cellIndex);
                    }
                }
            }
            var gridPoints = new List<Vector3>(new Vector3[cells.Count]);
            var placed = new Vector3[cells.Count];
            Parallel.For(0, cells.Count, index =>
            {
                int cellIndex = cells[index];
                int id = cellIndex % (countD - 1);
                int iy = cellIndex / (countD - 1) % (countY - 1);
                int iu = cellIndex / ((countD - 1) * (countY - 1));
                placed[index] = FeaturePoint(values, field, countY, countD, iu, iy, id, cell, uStart, yStart, dStart,
                    tops[Mathf.Min(iu, countU - 1)]);
            });
            for (int index = 0; index < cells.Count; index++) gridPoints[index] = placed[index];
            var indices = new List<int>();
            for (int iu = 0; iu < countU; iu++)
            {
                for (int iy = 0; iy < countY; iy++)
                {
                    for (int id = 0; id < countD; id++)
                    {
                        float value = values[(iu * countY + iy) * countD + id];
                        // Each grid edge with rock at one end gives one quad of the four cells around it.
                        AddQuad(indices, cellVertex, values, countU, countY, countD, iu, iy, id, value, 0);
                        AddQuad(indices, cellVertex, values, countU, countY, countD, iu, iy, id, value, 1);
                        AddQuad(indices, cellVertex, values, countU, countY, countD, iu, iy, id, value, 2);
                    }
                }
            }

            // World positions, then normals from the triangles.
            int vertexCount = gridPoints.Count;
            var positions = new Vector3[vertexCount];
            var locals = new Vector3[vertexCount];
            for (int index = 0; index < vertexCount; index++)
            {
                Vector3 grid = gridPoints[index];
                locals[index] = new Vector3(uStart + grid.x * cell, yStart + grid.y * cell, dStart + grid.z * cell);
                positions[index] = ToWorld(grid, origins, outwards, faces, countU, countY, yStart, cell, dStart);
            }
            var normals = new Vector3[vertexCount];
            for (int index = 0; index < indices.Count; index += 3)
            {
                int a = indices[index];
                int b = indices[index + 1];
                int c = indices[index + 2];
                Vector3 normal = Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]);
                normals[a] += normal;
                normals[b] += normal;
                normals[c] += normal;
            }
            // The winding follows the grid axes. Turn it when the surface faces into the rock.
            float facing = 0f;
            for (int index = 0; index < vertexCount; index++)
            {
                facing += Vector3.Dot(normals[index], outwards[GridColumn(gridPoints[index], countU)]);
            }
            if (facing < 0f)
            {
                for (int index = 0; index < indices.Count; index += 3)
                {
                    (indices[index + 1], indices[index + 2]) = (indices[index + 2], indices[index + 1]);
                }
                for (int index = 0; index < vertexCount; index++) normals[index] = -normals[index];
            }

            var vertices = new Vertex[vertexCount];
            var vertexU = new float[vertexCount];
            var buried = new bool[vertexCount];
            Parallel.For(0, vertexCount, index =>
            {
                Vector3 local = locals[index];
                int iu = GridColumn(gridPoints[index], countU);
                Vector3 normal = normals[index].sqrMagnitude > 1e-12f ? normals[index].normalized : outwards[iu];
                Vector3 position = positions[index];
                // Cavity: rock close in front of the point, as in a crack or a socket.
                float ahead = field.Signed(local.x, local.y, local.z + CavityReach, tops[iu], cell);
                float cavity = Mathf.Clamp01(1f - ahead / CavityReach);
                Bed bed = field.Beds[field.FindBed(field.BedY(local.x, local.y))];
                float ground = RockFaceBuilder.GroundWeight(position, outwards[iu], local.x, settings, terrain);
                Vector3 floor = origins[iu] + outwards[iu] * (faces[iu * countY] + settings.GroundReach);
                vertexU[index] = local.x;
                buried[index] = terrain.TrySampleHeight(position.x, position.z, out float terrainHeight) &&
                                position.y < terrainHeight - BuriedDepth;
                vertices[index] = new Vertex
                {
                    Position = position,
                    Normal = new half4((half)normal.x, (half)normal.y, (half)normal.z, (half)1f),
                    Color = new Color32((byte)(bed.Hardness * 255f), (byte)(cavity * 255f), (byte)(bed.Layer * 255f),
                        (byte)(ground * 255f)),
                    GroundOffset = new half2((half)(floor.x - position.x), (half)(floor.z - position.z)),
                    Cover = new half4((half)0f, (half)0f, (half)0f, (half)0f)
                };
            });
            return new Level { Vertices = vertices, VertexU = vertexU, Buried = buried, Indices = indices };
        }

        // The triangles of a level in one chunk, without buried triangles, reduced, with only the vertices they use.
        // Runs on a worker thread.
        private static ChunkData ChunkMeshData(Level level, int chunk, Func<float, int> chunkOf, double error)
        {
            var remap = new Dictionary<int, int>();
            var vertices = new List<Vertex>();
            var positions = new List<Vector3>();
            var locked = new List<bool>();
            var indices = new List<int>();
            for (int index = 0; index < level.Indices.Count; index += 3)
            {
                int a = level.Indices[index];
                int b = level.Indices[index + 1];
                int c = level.Indices[index + 2];
                if (chunkOf(level.VertexU[a]) != chunk) continue;
                if (level.Buried[a] && level.Buried[b] && level.Buried[c]) continue;
                for (int corner = 0; corner < 3; corner++)
                {
                    int source = level.Indices[index + corner];
                    if (!remap.TryGetValue(source, out int target))
                    {
                        target = vertices.Count;
                        remap.Add(source, target);
                        vertices.Add(level.Vertices[source]);
                        positions.Add(level.Vertices[source].Position);
                        locked.Add(level.Owner[source] == -2);
                    }
                    indices.Add(target);
                }
            }
            if (indices.Count == 0) return null;
            List<int> reduced = FracturedFaceDecimator.Reduce(positions, indices, locked.ToArray(), error);
            // Only the vertices that the kept triangles use.
            var used = new int[vertices.Count];
            for (int index = 0; index < used.Length; index++) used[index] = -1;
            var keptVertices = new List<Vertex>();
            for (int index = 0; index < reduced.Count; index++)
            {
                int vertex = reduced[index];
                if (used[vertex] < 0)
                {
                    used[vertex] = keptVertices.Count;
                    keptVertices.Add(vertices[vertex]);
                }
                reduced[index] = used[vertex];
            }
            return reduced.Count == 0 ? null : new ChunkData { Vertices = keptVertices, Indices = reduced };
        }

        private static Mesh CreateMesh(ChunkData data, string name)
        {
            List<Vertex> vertices = data.Vertices;
            List<int> indices = data.Indices;
            var mesh = new Mesh { name = name, hideFlags = HideFlags.HideAndDontSave };
            IndexFormat format = vertices.Count > ushort.MaxValue ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.SetVertexBufferParams(vertices.Count, Layout);
            mesh.SetVertexBufferData(vertices, 0, 0, vertices.Count);
            mesh.SetIndexBufferParams(indices.Count, format);
            if (format == IndexFormat.UInt16)
            {
                var shortIndices = new ushort[indices.Count];
                for (int index = 0; index < indices.Count; index++) shortIndices[index] = (ushort)indices[index];
                mesh.SetIndexBufferData(shortIndices, 0, 0, indices.Count);
            }
            else
            {
                mesh.SetIndexBufferData(indices, 0, 0, indices.Count);
            }
            mesh.subMeshCount = 1;
            mesh.SetSubMesh(0, new SubMeshDescriptor(0, indices.Count));
            mesh.RecalculateBounds();
            return mesh;
        }

        // Major joints split the wall into panels of random length. Each panel gets a step and a lean (top out or top
        // back) that twists along it; some split at a height into two tiers with their own lean.
        private static Panel[] BuildPanels(CliffWallFracture fracture, float uMinimum, float uMaximum, float yMinimum,
            float yMaximum)
        {
            var panels = new List<Panel>();
            int seed = fracture.Seed * 613;
            float position = uMinimum - 1f;
            int index = 0;
            while (position < uMaximum + 1f)
            {
                float length = Mathf.Lerp(fracture.PanelLength.x, fracture.PanelLength.y, Hash(index, 1, seed));
                var panel = new Panel { Start = position, End = position + length };
                if (Hash(index, 2, seed) < fracture.TierChance)
                {
                    panel.Split = Mathf.Lerp(yMinimum, yMaximum, Mathf.Lerp(0.3f, 0.7f, Hash(index, 3, seed)));
                }
                for (int tier = 0; tier < 2; tier++)
                {
                    panel.Step[tier] = fracture.PanelStep * Hash(index, 4 + tier, seed);
                    // A clear lean, one way or the other: near-vertical panels did not read as panels.
                    float sign = Hash(index, 10 + tier, seed) < 0.5f ? -1f : 1f;
                    float lean = sign * fracture.PanelLean * Mathf.Lerp(0.35f, 1f, Hash(index, 6 + tier, seed));
                    float twist = fracture.PanelTwist * (2f * Hash(index, 8 + tier, seed) - 1f);
                    panel.LeanStart[tier] = lean - 0.5f * twist;
                    panel.LeanEnd[tier] = lean + 0.5f * twist;
                }
                panels.Add(panel);
                position += length;
                index++;
            }
            return panels.ToArray();
        }

        private static Bed[] BuildBeds(CliffWallFracture fracture, float bottom, float top, float uStart, float uEnd)
        {
            var beds = new List<Bed>();
            float height = bottom;
            int index = 0;
            while (height < top)
            {
                // Many thin beds and a few thick ones, as in a real bedded sequence.
                float share = Hash(index, 41, fracture.Seed);
                float thickness = Mathf.Lerp(fracture.BedThickness.x, fracture.BedThickness.y, share * share);
                bool hard = Hash(index, 31, fracture.Seed) < fracture.HardShare;
                float strength = Hash(index, 37, fracture.Seed);
                var joints = new List<float>();
                float position = uStart - fracture.JointSpacing.y * Hash(index, 43, fracture.Seed);
                int joint = 0;
                while (position < uEnd + fracture.JointSpacing.y)
                {
                    joints.Add(position);
                    position += Mathf.Lerp(fracture.JointSpacing.x, fracture.JointSpacing.y,
                        Hash(index, joint++, fracture.Seed + 53));
                }
                joints.Add(position);
                beds.Add(new Bed
                {
                    Bottom = height,
                    Top = height + thickness,
                    Hardness = hard ? Mathf.Lerp(0.6f, 1f, strength) : Mathf.Lerp(0f, 0.25f, strength),
                    Protrusion = hard
                        ? fracture.HardProtrusion * Mathf.Lerp(0.5f, 1f, strength)
                        : -fracture.SoftRecess * Mathf.Lerp(0.4f, 1f, strength),
                    Layer = Hash(index, 83, fracture.Seed),
                    Joints = joints.ToArray()
                });
                height += thickness;
                index++;
            }
            return beds.ToArray();
        }

        // Outward distance of the designed face at a height: interpolated between the face points around it.
        private static float FaceX(FaceColumn column, float y)
        {
            List<Vector2> face = column.Face;
            if (y >= face[0].y) return face[0].x;
            for (int index = 1; index < face.Count; index++)
            {
                Vector2 above = face[index - 1];
                Vector2 below = face[index];
                if (y < below.y) continue;
                float span = above.y - below.y;
                return span > 1e-4f ? Mathf.Lerp(below.x, above.x, (y - below.y) / span) : below.x;
            }
            return face[^1].x;
        }

        private static int FindColumn(List<FaceColumn> columns, float u)
        {
            int low = 0;
            int high = columns.Count - 1;
            while (low < high)
            {
                int middle = (low + high + 1) >> 1;
                if (columns[middle].U <= u) low = middle;
                else high = middle - 1;
            }
            return Mathf.Min(low, columns.Count - 2);
        }

        private static int GridColumn(Vector3 grid, int countU)
        {
            return Mathf.Clamp(Mathf.RoundToInt(grid.x), 0, countU - 1);
        }

        private static bool HasCrossing(float[] values, int countY, int countD, int iu, int iy, int id)
        {
            bool first = values[(iu * countY + iy) * countD + id] < 0f;
            for (int corner = 1; corner < 8; corner++)
            {
                int cu = iu + (corner & 1);
                int cy = iy + (corner >> 1 & 1);
                int cd = id + (corner >> 2);
                if (values[(cu * countY + cy) * countD + cd] < 0f != first) return true;
            }
            return false;
        }

        // The point of the cell where the surface planes at its edge crossings meet, in grid units (least squares, with
        // a pull to the mean of the crossings, and kept in the cell).
        private static Vector3 FeaturePoint(float[] values, Field field, int countY, int countD, int iu, int iy, int id,
            float cell, float uStart, float yStart, float dStart, float top)
        {
            const float pull = 0.05f;
            const float epsilon = 0.01f;
            Vector3 mean = CellPoint(values, countY, countD, iu, iy, id, out bool crossed);
            if (!crossed) return mean;
            double a00 = pull, a01 = 0, a02 = 0, a11 = pull, a12 = 0, a22 = pull;
            double b0 = pull * mean.x, b1 = pull * mean.y, b2 = pull * mean.z;
            for (int edge = 0; edge < 12; edge++)
            {
                int axis = edge >> 2;
                int other = edge & 3;
                Vector3Int a = axis == 0 ? new Vector3Int(0, other & 1, other >> 1)
                    : axis == 1 ? new Vector3Int(other & 1, 0, other >> 1)
                    : new Vector3Int(other & 1, other >> 1, 0);
                Vector3Int b = a;
                b[axis] = 1;
                float valueA = values[((iu + a.x) * countY + iy + a.y) * countD + id + a.z];
                float valueB = values[((iu + b.x) * countY + iy + b.y) * countD + id + b.z];
                if (valueA < 0f == valueB < 0f) continue;
                Vector3 point = new Vector3(iu, iy, id) + Vector3.Lerp(a, b, valueA / (valueA - valueB));
                // The surface normal from the field, in grid units (the grid is about isotropic in meters).
                float u = uStart + point.x * cell;
                float y = yStart + point.y * cell;
                float d = dStart + point.z * cell;
                // Tetrahedral differences: four field samples instead of six.
                float s0 = field.Signed(u + epsilon, y - epsilon, d - epsilon, top, cell);
                float s1 = field.Signed(u - epsilon, y - epsilon, d + epsilon, top, cell);
                float s2 = field.Signed(u - epsilon, y + epsilon, d - epsilon, top, cell);
                float s3 = field.Signed(u + epsilon, y + epsilon, d + epsilon, top, cell);
                var normal = new Vector3(s0 - s1 - s2 + s3, -s0 - s1 + s2 + s3, -s0 + s1 - s2 + s3);
                if (normal.sqrMagnitude < 1e-12f) continue;
                normal.Normalize();
                double dot = Vector3.Dot(normal, point);
                a00 += normal.x * normal.x;
                a01 += normal.x * normal.y;
                a02 += normal.x * normal.z;
                a11 += normal.y * normal.y;
                a12 += normal.y * normal.z;
                a22 += normal.z * normal.z;
                b0 += normal.x * dot;
                b1 += normal.y * dot;
                b2 += normal.z * dot;
            }
            // Solve the symmetric 3 x 3 system (Cramer's rule).
            double c00 = a11 * a22 - a12 * a12;
            double c01 = a02 * a12 - a01 * a22;
            double c02 = a01 * a12 - a02 * a11;
            double determinant = a00 * c00 + a01 * c01 + a02 * c02;
            if (Math.Abs(determinant) < 1e-9) return mean;
            double c11 = a00 * a22 - a02 * a02;
            double c12 = a01 * a02 - a00 * a12;
            double c22 = a00 * a11 - a01 * a01;
            var solved = new Vector3(
                (float)((c00 * b0 + c01 * b1 + c02 * b2) / determinant),
                (float)((c01 * b0 + c11 * b1 + c12 * b2) / determinant),
                (float)((c02 * b0 + c12 * b1 + c22 * b2) / determinant));
            // Kept in the cell: outside it, the vertex folded the quads around it.
            return new Vector3(Mathf.Clamp(solved.x, iu, iu + 1f), Mathf.Clamp(solved.y, iy, iy + 1f),
                Mathf.Clamp(solved.z, id, id + 1f));
        }

        // The mean of the points where the cell edges cross the surface, in grid units.
        private static Vector3 CellPoint(float[] values, int countY, int countD, int iu, int iy, int id, out bool crossed)
        {
            Vector3 sum = Vector3.zero;
            int crossings = 0;
            for (int edge = 0; edge < 12; edge++)
            {
                int axis = edge >> 2;
                int other = edge & 3;
                Vector3Int a = axis == 0 ? new Vector3Int(0, other & 1, other >> 1)
                    : axis == 1 ? new Vector3Int(other & 1, 0, other >> 1)
                    : new Vector3Int(other & 1, other >> 1, 0);
                Vector3Int b = a;
                b[axis] = 1;
                float valueA = values[((iu + a.x) * countY + iy + a.y) * countD + id + a.z];
                float valueB = values[((iu + b.x) * countY + iy + b.y) * countD + id + b.z];
                if (valueA < 0f == valueB < 0f) continue;
                float share = valueA / (valueA - valueB);
                sum += Vector3.Lerp(a, b, share);
                crossings++;
            }
            crossed = crossings > 0;
            return crossed ? new Vector3(iu, iy, id) + sum / crossings : Vector3.zero;
        }

        // The quad of the four cells around the grid edge from a sample along one axis, when the edge crosses the surface.
        private static void AddQuad(List<int> indices, int[] cellVertex, float[] values, int countU, int countY,
            int countD, int iu, int iy, int id, float value, int axis)
        {
            int nu = iu + (axis == 0 ? 1 : 0);
            int ny = iy + (axis == 1 ? 1 : 0);
            int nd = id + (axis == 2 ? 1 : 0);
            if (nu >= countU || ny >= countY || nd >= countD) return;
            float next = values[(nu * countY + ny) * countD + nd];
            if (value < 0f == next < 0f) return;
            // The four cells that share the edge, in order around the edge axis. The two other axes follow the axis
            // in cyclic order, so all quads turn the same way around their edge.
            int axisB = (axis + 1) % 3;
            int axisC = (axis + 2) % 3;
            int q0 = EdgeCell(cellVertex, countU, countY, countD, iu, iy, id, axisB, -1, axisC, -1);
            int q1 = EdgeCell(cellVertex, countU, countY, countD, iu, iy, id, axisB, 0, axisC, -1);
            int q2 = EdgeCell(cellVertex, countU, countY, countD, iu, iy, id, axisB, 0, axisC, 0);
            int q3 = EdgeCell(cellVertex, countU, countY, countD, iu, iy, id, axisB, -1, axisC, 0);
            if (q0 < 0 || q1 < 0 || q2 < 0 || q3 < 0) return;
            // The quad faces from the rock (negative) to the air.
            if (value < 0f)
            {
                indices.Add(q0); indices.Add(q1); indices.Add(q2);
                indices.Add(q0); indices.Add(q2); indices.Add(q3);
            }
            else
            {
                indices.Add(q0); indices.Add(q2); indices.Add(q1);
                indices.Add(q0); indices.Add(q3); indices.Add(q2);
            }
        }

        // The vertex of the cell at the sample, moved by the offsets on two axes, or -1.
        private static int EdgeCell(int[] cellVertex, int countU, int countY, int countD, int iu, int iy, int id,
            int axisB, int offsetB, int axisC, int offsetC)
        {
            int cu = iu + (axisB == 0 ? offsetB : 0) + (axisC == 0 ? offsetC : 0);
            int cy = iy + (axisB == 1 ? offsetB : 0) + (axisC == 1 ? offsetC : 0);
            int cd = id + (axisB == 2 ? offsetB : 0) + (axisC == 2 ? offsetC : 0);
            if (cu < 0 || cy < 0 || cd < 0 || cu >= countU - 1 || cy >= countY - 1 || cd >= countD - 1) return -1;
            return cellVertex[(cu * (countY - 1) + cy) * (countD - 1) + cd];
        }

        private static Vector3 ToWorld(Vector3 grid, Vector3[] origins, Vector3[] outwards, float[] faces, int countU,
            int countY, float yStart, float cell, float dStart)
        {
            int iu = Mathf.Clamp(Mathf.FloorToInt(grid.x), 0, countU - 2);
            float tu = Mathf.Clamp01(grid.x - iu);
            int iy = Mathf.Clamp(Mathf.FloorToInt(grid.y), 0, countY - 2);
            float ty = Mathf.Clamp01(grid.y - iy);
            Vector3 origin = Vector3.Lerp(origins[iu], origins[iu + 1], tu);
            Vector3 outward = Vector3.Lerp(outwards[iu], outwards[iu + 1], tu).normalized;
            float face = Mathf.Lerp(Mathf.Lerp(faces[iu * countY + iy], faces[iu * countY + iy + 1], ty),
                Mathf.Lerp(faces[(iu + 1) * countY + iy], faces[(iu + 1) * countY + iy + 1], ty), tu);
            float y = yStart + grid.y * cell;
            float d = dStart + grid.z * cell;
            return origin + outward * (face + d) + Vector3.up * y;
        }

        // Distance to a box from the outside distances of its three slabs, with a bevel of the radius.
        private static float RoundedBox(float sideU, float sideY, float depth, float radius)
        {
            float x = sideU + radius;
            float y = sideY + radius;
            float z = depth + radius;
            float outside = Mathf.Sqrt(Square(Mathf.Max(x, 0f)) + Square(Mathf.Max(y, 0f)) + Square(Mathf.Max(z, 0f)));
            return outside + Mathf.Min(Mathf.Max(x, Mathf.Max(y, z)), 0f) - radius;
        }

        private static float Square(float value) => value * value;

        private static void Destroy(UnityEngine.Object target)
        {
            if (!target) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(target);
            else UnityEngine.Object.DestroyImmediate(target);
        }

        private static float Hash(int a, int b, int c)
        {
            unchecked
            {
                uint hash = (uint)a * 0x8da6b343u ^ (uint)b * 0xd8163841u ^ (uint)c * 0xcb1ab31fu;
                hash ^= hash >> 13;
                hash *= 0x5bd1e995u;
                hash ^= hash >> 15;
                return (hash & 0xFFFFFFu) / 16777216f;
            }
        }
    }
}
