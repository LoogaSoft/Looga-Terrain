using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Splines;

namespace LoogaSoft.Terrain.Geology
{
    /// <summary>
    /// Settings of a rock arch that is built from rock blocks. Cells of the cross section become block columns along
    /// the arch. Staggered joints cut each column into curved blocks.
    /// </summary>
    [Serializable]
    public sealed class RockArchBlocks
    {
        [Header("Cross Section")]
        [Min(2f), Tooltip("Typical width of a block column in the cross section, in meters.")]
        public float CellSize = 45f;
        [Min(0f), Tooltip("Largest distance that an edge block column juts out past the arch outline, in meters.")]
        public float EdgeJut = 14f;
        [Min(0f), Tooltip("Largest distance that the edge block columns step back from the arch outline, in meters.")]
        public float EdgeRecess = 10f;
        [Min(2f), Tooltip("Typical size of a group of jutting or receding block columns, in meters.")]
        public float JutSize = 90f;
        [Range(0.2f, 1f), Tooltip("Size of the cross section at the crown, as a share of its size at the feet.")]
        public float CrownScale = 0.85f;
        [Range(0.05f, 0.5f), Tooltip("Reach of the outermost block columns from their foot, as a share of the arch " +
                                     "length. Columns nearer the axis reach farther. A column that reaches 0.5 from " +
                                     "both feet goes over the crown, so the arch becomes thinner toward the crown.")]
        public float MinimumReach = 0.15f;
        [Range(0.3f, 3f), Tooltip("Shape of the reach. Larger values keep more columns long and end only the " +
                                  "outermost.")]
        public float ReachShape = 1f;
        [Range(0f, 0.2f), Tooltip("Random change of the reach of each column, as a share of the arch length.")]
        public float ReachJitter = 0.06f;
        [Range(0f, 0.5f), Tooltip("Rounding of the block corners, as a share of the block width. Larger values give " +
                                  "rounder, more cylindrical blocks.")]
        public float Roundness = 0.3f;
        [Tooltip("Smallest and largest width of a fissure between two block columns, in meters.")]
        public Vector2 FissureWidth = new(1f, 4f);
        [Range(0f, 1f), Tooltip("Share of neighbour block columns without a fissure between them.")]
        public float FissureFusedShare = 0.3f;

        [Header("Blocks Along The Arch")]
        [Tooltip("Smallest and largest length of a block along the arch, in meters.")]
        public Vector2 BlockLength = new(40f, 120f);
        [Tooltip("Smallest and largest width of a joint between two blocks along the arch, in meters.")]
        public Vector2 JointWidth = new(0.8f, 2.5f);
        [Range(0f, 1f), Tooltip("Share of the joints along the arch that are closed. Blocks at a closed joint join.")]
        public float FusedShare = 0.3f;
        [Min(0f), Tooltip("Largest sideways offset of a block from its column, in meters.")]
        public float Shift = 1f;
        [Min(0f), Tooltip("Largest outward growth of a block past the arch surface, in meters. The outer half of the " +
                          "block grows, so blocks stand out by different amounts, as the pillars of a block " +
                          "formation have different heights, and stay fitted to their neighbours.")]
        public float Relief = 25f;
        [Min(0f), Tooltip("Largest inward shrink of the outer half of a block, in meters.")]
        public float ReliefInward = 8f;
        [Min(10f), Tooltip("Typical length along the arch of a group of standing out or stepped back blocks, in " +
                           "meters.")]
        public float ReliefGroupLength = 250f;
        [Range(0f, 0.5f), Tooltip("Largest tilt of a joint across the arch, in meters along the arch for each meter " +
                                  "across.")]
        public float JointTilt = 0.25f;
        [Min(0f), Tooltip("Length of the feet past the spline ends, in meters. The feet go into the ground.")]
        public float FootDepth = 60f;

        [Header("Surface")]
        [Min(0.1f), Tooltip("Radius of the rounded block edges, in meters.")]
        public float Bevel = 3f;
        [Range(0f, 0.9f), Tooltip("Change of the edge radius around a block. Larger values give chipped edges.")]
        public float Chipping = 0.6f;
        [Min(0f), Tooltip("Depth of the surface noise, in meters.")]
        public float NoiseDepth = 2.5f;
        [Min(0.5f), Tooltip("Typical size of the surface noise, in meters.")]
        public float NoiseScale = 24f;
        [Min(1f), Tooltip("Scale of the strata and joint colours, as a factor of the rock face sizes.")]
        public float DetailScale = 10f;
        [Min(0.5f), Tooltip("Distance between the mesh grid points, in meters.")]
        public float GridSpacing = 8f;

        [Header("Output")]
        [Min(10f), Tooltip("Side of the cube of one render chunk, in meters.")]
        public float ChunkLength = 300f;
        [Tooltip("Screen height of a chunk below which it uses the second and the third level of detail.")]
        public Vector2 LodTransitions = new(0.3f, 0.08f);

        internal RockArchBlocks Copy()
        {
            return (RockArchBlocks)MemberwiseClone();
        }
    }

    // Builds a rock arch from blocks. The cross section is cut into cells with organic edges, as in a block
    // formation. Only the cells at the outside of the cross section are built, because the inside never shows. Each
    // cell is a column along the arch that staggered joints cut into curved blocks. Each block is one closed grid.
    internal static class RockArchBuilder
    {
        private const float SiteRadiusShare = 0.85f;
        private const float RowsPerBevel = 2f;
        private const float RoundingSpacing = 1f;
        // The core: its number of sides, its row spacing as a factor of the grid spacing, its surface as a share of the
        // visible layer below the outer columns, and its smallest size as a share of the outermost cell distance.
        private const int CoreSides = 40;
        private const float CoreRowSpacing = 1.5f;
        private const float CoreDepth = 0.55f;
        private const float CoreMinimum = 0.2f;
        // Block points this far inside the core surface, as a radial share, are hidden by the core.
        private const float CoreCullMargin = 0.03f;
        // Depth below the ground, past the surface noise, before an arch point counts as hidden, in meters.
        private const float BuriedMargin = 1f;
        private const int RoundLimitSides = 16;
        private const float RoundLimit = 0.75f;

        // Frames along the arch at an even step, with straight extensions past both ends for the feet.
        private sealed class ArchPath
        {
            public Vector3[] Centers;
            public Vector3[] Tangents;
            public Vector3[] Sides;
            public Vector3[] Ups;
            public float Step;
            public int Extension;
            public float Length;

            public void Evaluate(float distance, out Vector3 center, out Vector3 side, out Vector3 up)
            {
                float position = Mathf.Clamp(distance / Step + Extension, 0f, Centers.Length - 1.001f);
                int index = Mathf.FloorToInt(position);
                float t = position - index;
                center = Vector3.Lerp(Centers[index], Centers[index + 1], t);
                side = Vector3.Lerp(Sides[index], Sides[index + 1], t).normalized;
                up = Vector3.Lerp(Ups[index], Ups[index + 1], t).normalized;
            }
        }

        // Cross section size and the terrain, for the parts of the arch that nobody can see.
        private sealed class ArchShape
        {
            public float HalfWidth;
            public float HalfDepth;
            public float LargestRadial;
            public float Layer;
            public TerrainHeightSampler Terrain;
        }

        // One block: its rounded cross section cell and its range along the arch.
        private struct Piece
        {
            public int Cell;
            public float Start;
            public float End;
            public Vector2 StartTilt;
            public Vector2 EndTilt;
            public Vector2 Shift;
            public float Relief;
            public float Bevel;
        }

        /// <summary>Builds the block strips of the arch into the workspace. Main thread for the spline reads.</summary>
        public static List<RockFaceStrip> Build(SplineContainer spline, float halfWidth, float halfDepth, int seed,
            RockArchBlocks blocks, RockFaceBuilder.Workspace workspace)
        {
            List<RockFaceStrip> strips = workspace.Strips;
            strips.Clear();
            ArchPath path = SamplePath(spline, blocks);
            List<Vector2[]> cells = CreateCells(halfWidth, halfDepth, seed, blocks, out List<Vector2> centres,
                out List<float> radials, out float largestRadial);
            // The visible layer is a little more than one cell deep, so no gap opens to the hollow inside.
            float layer = 1.6f * blocks.CellSize / (Mathf.Min(halfWidth, halfDepth) + blocks.EdgeJut);
            List<Piece> pieces = CreatePieces(cells, centres, radials, layer, path.Length, seed, blocks);
            if (pieces.Count == 0) return strips;

            float yMinimum = float.MaxValue;
            float yMaximum = float.MinValue;
            foreach (Vector3 centre in path.Centers)
            {
                yMinimum = Mathf.Min(yMinimum, centre.y - halfDepth - blocks.EdgeJut);
                yMaximum = Mathf.Max(yMaximum, centre.y + halfDepth + blocks.EdgeJut);
            }
            var detail = new RockFaceDetail(DetailSettings(blocks, seed), -blocks.FootDepth - 100f,
                path.Length + blocks.FootDepth + 100f, yMinimum, yMaximum);

            // The terrain heights around the arch, so the feet below the ground are not drawn.
            using var terrain = new TerrainHeightSampler(PathRegion(path, halfWidth, halfDepth, blocks));
            terrain.WaitForReads();
            var shape = new ArchShape
            {
                HalfWidth = halfWidth,
                HalfDepth = halfDepth,
                LargestRadial = largestRadial,
                Layer = layer,
                Terrain = terrain
            };

            // A solid core behind the outer block columns, so the gaps between the blocks show rock and not the sky.
            float coreStep = CoreRowSpacing * blocks.GridSpacing;
            int coreRows = Mathf.Max(2, Mathf.CeilToInt((path.Length + 2f * blocks.FootDepth) / coreStep)) + 1;
            RockFaceStrip core = workspace.TakeStrip(CoreSides, coreRows, true);
            FillCore(core, path, shape, blocks, detail);
            // The core also faces inward, so a view from inside the arch sees rock behind the gaps too.
            RockFaceStrip inside = workspace.TakeStrip(CoreSides, coreRows, true);
            CopyInverted(core, inside);

            var pieceStrips = new RockFaceStrip[pieces.Count];
            var rowCounts = new int[pieces.Count];
            for (int index = 0; index < pieces.Count; index++)
            {
                rowCounts[index] = RowCount(pieces[index], cells[pieces[index].Cell], centres[pieces[index].Cell],
                    blocks);
                pieceStrips[index] = workspace.TakeStrip(cells[pieces[index].Cell].Length, rowCounts[index], true);
            }
            Parallel.For(0, pieces.Count, index =>
            {
                Piece piece = pieces[index];
                FillPiece(piece, cells[piece.Cell], centres[piece.Cell], rowCounts[index], path, shape, blocks,
                    detail, pieceStrips[index]);
            });
            return strips;
        }

        #region Path
        private static ArchPath SamplePath(SplineContainer spline, RockArchBlocks blocks)
        {
            float length = spline.CalculateLength();
            if (!float.IsFinite(length) || length <= 1f)
            {
                throw new InvalidOperationException("The arch spline has no measurable length.");
            }
            float step = Mathf.Max(2f, blocks.GridSpacing);
            int segments = Mathf.Max(8, Mathf.CeilToInt(length / step));
            step = length / segments;
            int extension = Mathf.CeilToInt(blocks.FootDepth / step) + 2;
            int count = segments + 1 + 2 * extension;
            var path = new ArchPath
            {
                Centers = new Vector3[count],
                Tangents = new Vector3[count],
                Sides = new Vector3[count],
                Ups = new Vector3[count],
                Step = step,
                Extension = extension,
                Length = length
            };
            using (var world = new NativeSpline(spline.Spline, (float4x4)spline.transform.localToWorldMatrix,
                       Allocator.Temp))
            {
                float t = 0f;
                for (int index = 0; index <= segments; index++)
                {
                    Vector3 point;
                    if (index == 0)
                    {
                        point = world.EvaluatePosition(0f);
                    }
                    else if (index == segments)
                    {
                        t = 1f;
                        point = world.EvaluatePosition(1f);
                    }
                    else
                    {
                        point = world.GetPointAtLinearDistance(t, step, out t);
                    }
                    path.Centers[extension + index] = point;
                    path.Tangents[extension + index] = ((Vector3)world.EvaluateTangent(t)).normalized;
                }
            }
            // The feet go on straight past the spline ends, into the ground.
            Vector3 startTangent = path.Tangents[extension];
            Vector3 endTangent = path.Tangents[extension + segments];
            for (int index = 1; index <= extension; index++)
            {
                path.Centers[extension - index] = path.Centers[extension] - startTangent * (index * step);
                path.Tangents[extension - index] = startTangent;
                path.Centers[extension + segments + index] = path.Centers[extension + segments] +
                                                             endTangent * (index * step);
                path.Tangents[extension + segments + index] = endTangent;
            }

            // Frames without twist: the side axis moves with the smallest turn from one sample to the next.
            Vector3 span = path.Centers[count - 1] - path.Centers[0];
            Vector3 side = Vector3.Cross(Vector3.up, span.normalized);
            side -= path.Tangents[0] * Vector3.Dot(side, path.Tangents[0]);
            if (side.sqrMagnitude < 1e-3f) side = Vector3.Cross(Vector3.forward, path.Tangents[0]);
            path.Sides[0] = side.normalized;
            path.Ups[0] = Vector3.Cross(path.Tangents[0], path.Sides[0]).normalized;
            for (int index = 1; index < count; index++)
            {
                Quaternion turn = Quaternion.FromToRotation(path.Tangents[index - 1], path.Tangents[index]);
                Vector3 next = turn * path.Sides[index - 1];
                next -= path.Tangents[index] * Vector3.Dot(next, path.Tangents[index]);
                if (next.sqrMagnitude < 1e-3f) next = path.Sides[index - 1];
                path.Sides[index] = next.normalized;
                path.Ups[index] = Vector3.Cross(path.Tangents[index], path.Sides[index]).normalized;
            }
            return path;
        }

        // Size of the cross section along the arch: full at the feet, the crown scale at the middle.
        private static float SectionScale(float distance, float length, RockArchBlocks blocks)
        {
            float along = Mathf.Clamp01(distance / Mathf.Max(length, 1f));
            return Mathf.Lerp(1f, blocks.CrownScale, Mathf.Sin(along * Mathf.PI));
        }
        #endregion

        #region Cells
        // The rounded cells of the cross section, their centres, and their distances from the arch axis as a share of
        // the largest distance (0 = axis, 1 = outermost cell).
        private static List<Vector2[]> CreateCells(float halfWidth, float halfDepth, int seed, RockArchBlocks blocks,
            out List<Vector2> centres, out List<float> radials, out float largestRadial)
        {
            var random = new System.Random(seed);
            float band = blocks.EdgeJut + 2f * blocks.CellSize;
            float radius = SiteRadiusShare * blocks.CellSize;
            float step = 0.35f * blocks.CellSize;
            var candidates = new List<Vector2>();
            for (float y = -halfDepth - band; y <= halfDepth + band; y += step)
            {
                for (float x = -halfWidth - band; x <= halfWidth + band; x += step)
                {
                    var point = new Vector2(x + step * (float)(random.NextDouble() - 0.5),
                        y + step * (float)(random.NextDouble() - 0.5));
                    if (Inside(point, halfWidth, halfDepth) >= -band) candidates.Add(point);
                }
            }
            for (int index = candidates.Count - 1; index > 0; index--)
            {
                int other = random.Next(index + 1);
                (candidates[index], candidates[other]) = (candidates[other], candidates[index]);
            }
            var sites = new List<Vector2>();
            foreach (Vector2 point in candidates)
            {
                bool free = true;
                for (int site = 0; site < sites.Count && free; site++)
                {
                    free = (sites[site] - point).sqrMagnitude >= radius * radius;
                }
                if (free) sites.Add(point);
            }

            // A site near the edge can stand out past the outline or step back from it, in groups.
            var included = new bool[sites.Count];
            for (int site = 0; site < sites.Count; site++)
            {
                Vector2 point = sites[site];
                float group = RockFaceNoise.Perlin(point.x / blocks.JutSize + seed * 0.29f, point.y / blocks.JutSize);
                float share = 0.6f * group + 0.4f * RockFaceNoise.Hash01(seed, site, 0, 40);
                float reach = Mathf.Lerp(-blocks.EdgeRecess, blocks.EdgeJut, share);
                included[site] = Inside(point, halfWidth, halfDepth) > -reach;
            }

            var cells = new List<Vector2[]>();
            centres = new List<Vector2>();
            radials = new List<float>();
            var cell = new List<Vector2>();
            var scratch = new List<Vector2>();
            float minimumArea = 0.25f * blocks.CellSize * blocks.CellSize;
            for (int site = 0; site < sites.Count; site++)
            {
                if (!included[site]) continue;
                if (!CutCell(sites, included, site, seed, blocks, cell, scratch, out _)) continue;
                // A round limit around the site keeps cells at the open edge from growing long and thin.
                for (int side = 0; side < RoundLimitSides && cell.Count >= 3; side++)
                {
                    float angle = side * 2f * Mathf.PI / RoundLimitSides;
                    var normal = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
                    RockBlockBuilder.Clip(cell, scratch, normal,
                        Vector2.Dot(sites[site], normal) + RoundLimit * blocks.CellSize);
                }
                if (cell.Count < 3) continue;
                if (RockBlockBuilder.Area(cell) < minimumArea) continue;
                // Two passes of a box average round the corners over about the roundness share of the block width.
                List<Vector2> rounded = RockBlockBuilder.Resample(cell, RoundingSpacing);
                int smoothing = Mathf.Max(1, Mathf.RoundToInt(0.5f * blocks.Roundness * blocks.CellSize /
                                                              RoundingSpacing));
                RockBlockBuilder.Smooth(rounded, smoothing);
                RockBlockBuilder.Smooth(rounded, smoothing);
                List<Vector2> points = RockBlockBuilder.Resample(rounded, blocks.GridSpacing);
                if (points.Count < 6) continue;
                cells.Add(points.ToArray());
                Vector2 centre = RockBlockBuilder.Centroid(points);
                centres.Add(centre);
                radials.Add(Mathf.Sqrt(centre.x * centre.x / (halfWidth * halfWidth) +
                                       centre.y * centre.y / (halfDepth * halfDepth)));
            }
            float largest = 0f;
            foreach (float radial in radials)
            {
                largest = Mathf.Max(largest, radial);
            }
            for (int index = 0; index < radials.Count; index++)
            {
                radials[index] /= Mathf.Max(largest, 1e-4f);
            }
            largestRadial = Mathf.Max(largest, 1e-4f);
            return cells;
        }

        // Distance inside the elliptic outline of the cross section, in meters. Negative outside.
        private static float Inside(Vector2 point, float halfWidth, float halfDepth)
        {
            float radial = Mathf.Sqrt(point.x * point.x / (halfWidth * halfWidth) +
                                      point.y * point.y / (halfDepth * halfDepth));
            return (1f - radial) * Mathf.Min(halfWidth, halfDepth);
        }

        // The cell of one site: a box cut by the half planes to the near sites. Outer is true when a site that is not
        // a block cuts the cell, so the cell has a face at the outside of the arch.
        private static bool CutCell(List<Vector2> sites, bool[] included, int site, int seed, RockArchBlocks blocks,
            List<Vector2> cell, List<Vector2> scratch, out bool outer)
        {
            outer = false;
            Vector2 centre = sites[site];
            float box = blocks.CellSize;
            cell.Clear();
            cell.Add(centre + new Vector2(-box, -box));
            cell.Add(centre + new Vector2(box, -box));
            cell.Add(centre + new Vector2(box, box));
            cell.Add(centre + new Vector2(-box, box));
            for (int other = 0; other < sites.Count && cell.Count >= 3; other++)
            {
                if (other == site) continue;
                Vector2 toOther = sites[other] - centre;
                float distance = toOther.magnitude;
                if (distance > 3f * blocks.CellSize || distance < 1e-4f) continue;
                Vector2 direction = toOther / distance;
                int low = Mathf.Min(site, other);
                int high = Mathf.Max(site, other);
                float width = RockFaceNoise.Hash01(seed, low, high, 42);
                float gap = !included[other] || RockFaceNoise.Hash01(seed, low, high, 41) < blocks.FissureFusedShare
                    ? 0f
                    : Mathf.Lerp(blocks.FissureWidth.x, blocks.FissureWidth.y, width);
                float before = included[other] ? 0f : RockBlockBuilder.Area(cell);
                RockBlockBuilder.Clip(cell, scratch, direction,
                    Vector2.Dot(centre + 0.5f * toOther, direction) - 0.5f * gap);
                if (!included[other] && RockBlockBuilder.Area(cell) < before - 1f) outer = true;
            }
            // A cell that reaches its box has open space on that side.
            if (cell.Count >= 3)
            {
                foreach (Vector2 point in cell)
                {
                    if (Mathf.Abs(point.x - centre.x) >= box - 0.01f || Mathf.Abs(point.y - centre.y) >= box - 0.01f)
                    {
                        outer = true;
                    }
                }
            }
            return cell.Count >= 3;
        }
        #endregion

        #region Pieces
        // Cuts each cell column into blocks along the arch. A column covers its reach from each foot. Only the columns
        // in the outer layer of the columns that are present show, so a column is built only where it is in that layer.
        // The joints are stratified random positions with an own phase for each column, so they do not line up.
        private static List<Piece> CreatePieces(List<Vector2[]> cells, List<Vector2> centres, List<float> radials,
            float layer, float length, int seed, RockArchBlocks blocks)
        {
            var pieces = new List<Piece>();
            float start = -blocks.FootDepth;
            float end = length + blocks.FootDepth;
            float step = Mathf.Max(1f, blocks.GridSpacing);
            float minimumRun = 0.5f * Mathf.Min(blocks.BlockLength.x, blocks.BlockLength.y);
            for (int cell = 0; cell < cells.Count; cell++)
            {
                float radial = radials[cell];
                float reachA = ColumnReach(radial, blocks, seed, cell, 0) * length;
                float reachB = length - ColumnReach(radial, blocks, seed, cell, 1) * length;
                float runStart = float.NaN;
                for (float distance = start; distance <= end + step * 0.5f; distance += step)
                {
                    float clamped = Mathf.Min(distance, end);
                    bool present = clamped <= reachA || clamped >= reachB;
                    bool visible = present && radial >= OuterRadial(clamped, length, blocks) - layer;
                    if (visible && float.IsNaN(runStart)) runStart = clamped;
                    bool last = distance + step > end + step * 0.5f;
                    if ((!visible || last) && !float.IsNaN(runStart))
                    {
                        float runEnd = visible ? end : clamped;
                        if (runEnd - runStart >= minimumRun)
                        {
                            AddPieces(pieces, cell, runStart, runEnd, centres[cell], seed, blocks);
                        }
                        runStart = float.NaN;
                    }
                }
            }
            return pieces;
        }

        // Reach of a column from one foot, as a share of the arch length. Columns near the axis reach farther.
        private static float ColumnReach(float radial, RockArchBlocks blocks, int seed, int cell, int foot)
        {
            float reach = Mathf.Lerp(1f, blocks.MinimumReach, Mathf.Pow(Mathf.Clamp01(radial), blocks.ReachShape));
            return reach + blocks.ReachJitter * (2f * RockFaceNoise.Hash01(seed, cell, foot, 60) - 1f);
        }

        // Radial share of the outermost columns that are present at a distance along the arch, without jitter.
        private static float OuterRadial(float distance, float length, RockArchBlocks blocks)
        {
            float along = Mathf.Clamp01(distance / Mathf.Max(length, 1f));
            float nearest = Mathf.Min(along, 1f - along);
            if (nearest <= blocks.MinimumReach) return 1f;
            float share = (1f - nearest) / (1f - blocks.MinimumReach);
            return Mathf.Pow(Mathf.Clamp01(share), 1f / blocks.ReachShape);
        }

        // Blocks of one column between two distances. The joints come from one sequence for each column, so the blocks
        // of the column line up with its other runs.
        private static void AddPieces(List<Piece> pieces, int cell, float runStart, float runEnd, Vector2 centre,
            int seed, RockArchBlocks blocks)
        {
            float mean = Mathf.Max(2f, 0.5f * (blocks.BlockLength.x + blocks.BlockLength.y));
            float amplitude = Mathf.Max(0f, 0.5f * (mean - Mathf.Min(blocks.BlockLength.x, blocks.BlockLength.y)));
            float phase = RockFaceNoise.Hash01(seed, cell, 0, 50) * mean;
            int first = Mathf.FloorToInt((runStart - phase) / mean) - 1;
            int last = Mathf.CeilToInt((runEnd - phase) / mean) + 1;
            float pieceStart = runStart;
            Vector2 startTilt = Vector2.zero;
            float startGap = 0f;
            for (int cut = first; cut <= last; cut++)
            {
                float position = cut * mean + phase +
                                 amplitude * (2f * RockFaceNoise.Hash01(seed, cell, cut, 51) - 1f);
                if (position <= pieceStart + blocks.BlockLength.x * 0.5f) continue;
                bool atEnd = position >= runEnd - blocks.BlockLength.x * 0.5f;
                if (!atEnd && RockFaceNoise.Hash01(seed, cell, cut, 52) < blocks.FusedShare) continue;
                if (atEnd) position = runEnd;
                float gap = atEnd ? 0f : 0.5f * Mathf.Lerp(blocks.JointWidth.x, blocks.JointWidth.y,
                    RockFaceNoise.Hash01(seed, cell, cut, 53));
                Vector2 tilt = atEnd
                    ? Vector2.zero
                    : blocks.JointTilt * new Vector2(2f * RockFaceNoise.Hash01(seed, cell, cut, 54) - 1f,
                        2f * RockFaceNoise.Hash01(seed, cell, cut, 55) - 1f);
                float shiftAngle = RockFaceNoise.Hash01(seed, cell, cut, 56) * 2f * Mathf.PI;
                float shiftLength = blocks.Shift * RockFaceNoise.Hash01(seed, cell, cut, 57);
                pieces.Add(new Piece
                {
                    Cell = cell,
                    Start = pieceStart + startGap,
                    End = position - gap,
                    StartTilt = startTilt,
                    EndTilt = tilt,
                    Shift = new Vector2(Mathf.Cos(shiftAngle), Mathf.Sin(shiftAngle)) * shiftLength,
                    Relief = PieceRelief(blocks, seed, cell, cut, centre, 0.5f * (pieceStart + position)),
                    Bevel = blocks.Bevel * Mathf.Lerp(0.6f, 1.4f, RockFaceNoise.Hash01(seed, cell, cut, 58))
                });
                if (atEnd) break;
                pieceStart = position;
                startGap = gap;
                startTilt = tilt;
            }
        }

        // Outward offset of a block: a noise grouped around and along the arch, and an own random share. Groups of
        // blocks stand out as buttresses or step back as recesses.
        private static float PieceRelief(RockArchBlocks blocks, int seed, int cell, int cut, Vector2 centre,
            float distance)
        {
            float angle = Mathf.Atan2(centre.y, centre.x);
            float group = RockFaceNoise.Perlin(distance / blocks.ReliefGroupLength + 2.3f * Mathf.Cos(angle),
                2.3f * Mathf.Sin(angle) + seed * 0.13f);
            // The own share is larger, so neighbour blocks differ clearly. The groups add buttresses and recesses.
            float share = 0.4f * RockFaceBuilder.Smooth01((group - 0.25f) / 0.5f) +
                          0.6f * RockFaceNoise.Hash01(seed, cell, cut, 59);
            return Mathf.Lerp(-blocks.ReliefInward, blocks.Relief, share);
        }

        // Rows of a block grid: the start face from its centre to the edge, the start bevel, the sides, the end bevel
        // and the end face from the edge to its centre.
        private static int RowCount(Piece piece, Vector2[] cell, Vector2 centre, RockArchBlocks blocks)
        {
            float farthest = 0f;
            foreach (Vector2 point in cell)
            {
                farthest = Mathf.Max(farthest, (point - centre).magnitude);
            }
            int faceRows = FaceRows(farthest, piece.Bevel, blocks);
            int sideRows = SideRows(piece, blocks);
            return 2 * (faceRows + BevelRows()) + sideRows + 1;
        }

        private static int FaceRows(float farthest, float bevel, RockArchBlocks blocks)
        {
            return Mathf.Max(2, Mathf.CeilToInt(Mathf.Max(farthest - bevel, 1f) / blocks.GridSpacing));
        }

        private static int BevelRows()
        {
            return Mathf.RoundToInt(RowsPerBevel);
        }

        private static int SideRows(Piece piece, RockArchBlocks blocks)
        {
            return Mathf.Max(2, Mathf.CeilToInt((piece.End - piece.Start) / blocks.GridSpacing));
        }
        #endregion

        #region Surface
        private static void FillPiece(Piece piece, Vector2[] cell, Vector2 centre, int rows, ArchPath path,
            ArchShape shape, RockArchBlocks blocks, RockFaceDetail detail, RockFaceStrip strip)
        {
            strip.CullHidden = true;
            int count = cell.Length;
            float farthest = 0f;
            foreach (Vector2 point in cell)
            {
                farthest = Mathf.Max(farthest, (point - centre).magnitude);
            }
            int faceRows = FaceRows(farthest, piece.Bevel, blocks);
            int bevelRows = BevelRows();
            int sideRows = SideRows(piece, blocks);
            var columns = new RockFaceBuilder.Column[count];
            float middle = 0.5f * (piece.Start + piece.End);
            // The relief stretches the outer half of the block away from the arch axis, as a taller pillar, or pulls it
            // in. The inner half keeps the shared cell edges, so the block stays fitted to its neighbours.
            Vector2 outward = centre.sqrMagnitude > 1e-4f ? centre.normalized : Vector2.zero;
            float outerExtent = 1f;
            foreach (Vector2 point in cell)
            {
                outerExtent = Mathf.Max(outerExtent, Vector2.Dot(point - centre, outward));
            }
            // The growth stays below the outer half of the block, so a block does not become a long slab.
            float relief = Mathf.Clamp(piece.Relief, -0.5f * outerExtent, 0.6f * outerExtent);
            path.Evaluate(middle, out _, out Vector3 middleSide, out Vector3 middleUp);

            for (int column = 0; column < count; column++)
            {
                Vector2 point = cell[column];
                Vector2 previous = cell[(column + count - 1) % count];
                Vector2 next = cell[(column + 1) % count];
                Vector2 tangent = (next - previous).normalized;
                var outward2 = new Vector2(tangent.y, -tangent.x);
                if (Vector2.Dot(outward2, point - centre) < 0f) outward2 = -outward2;
                columns[column] = new RockFaceBuilder.Column
                {
                    Outward = (middleSide * outward2.x + middleUp * outward2.y).normalized,
                    Active = true
                };
                float chip = 2f * RockFaceNoise.Perlin(point.x / 9f + piece.Start * 0.07f, point.y / 9f + 31.7f) - 1f;
                float bevel = piece.Bevel * (1f + blocks.Chipping * chip);
                Vector2 offset = point - centre;
                float startDistance = piece.Start + Vector2.Dot(piece.StartTilt, offset);
                float endDistance = piece.End + Vector2.Dot(piece.EndTilt, offset);
                // Tilted joints must leave the block a length of at least two bevels.
                endDistance = Mathf.Max(endDistance, startDistance + 2f * bevel + 1f);
                float pointDistance = offset.magnitude;

                for (int row = 0; row < rows; row++)
                {
                    float distance;
                    float inset;
                    float cavity = 0f;
                    // Row groups: start face, start bevel, sides, end bevel, end face.
                    if (row < faceRows)
                    {
                        float share = row / (float)faceRows;
                        inset = Mathf.Lerp(pointDistance, Mathf.Min(bevel, pointDistance), share);
                        distance = startDistance;
                        cavity = 0.5f;
                    }
                    else if (row < faceRows + bevelRows)
                    {
                        float angle = 0.5f * Mathf.PI * (row - faceRows) / bevelRows;
                        inset = Mathf.Min(bevel, pointDistance) * (1f - Mathf.Sin(angle));
                        distance = startDistance + bevel * (1f - Mathf.Cos(angle));
                        cavity = 0.3f;
                    }
                    else if (row <= faceRows + bevelRows + sideRows)
                    {
                        float share = (row - faceRows - bevelRows) / (float)sideRows;
                        inset = 0f;
                        distance = Mathf.Lerp(startDistance + bevel, endDistance - bevel, share);
                    }
                    else if (row <= faceRows + 2 * bevelRows + sideRows)
                    {
                        float angle = 0.5f * Mathf.PI * (row - faceRows - bevelRows - sideRows) / bevelRows;
                        inset = Mathf.Min(bevel, pointDistance) * (1f - Mathf.Cos(angle));
                        distance = endDistance - bevel * (1f - Mathf.Sin(angle));
                        cavity = 0.3f;
                    }
                    else
                    {
                        float share = (row - faceRows - 2 * bevelRows - sideRows) / (float)faceRows;
                        inset = Mathf.Lerp(Mathf.Min(bevel, pointDistance), pointDistance, share);
                        distance = endDistance;
                        cavity = 0.5f;
                    }
                    Vector2 section = point + (centre - point) * (pointDistance > 1e-4f ? inset / pointDistance : 0f);
                    float along = Mathf.Clamp01(Vector2.Dot(section - centre, outward) / outerExtent);
                    section += outward * (relief * along) + piece.Shift;
                    float scale = SectionScale(distance, path.Length, blocks);
                    path.Evaluate(distance, out Vector3 axis, out Vector3 side, out Vector3 up);
                    Vector3 vertex = axis + (side * section.x + up * section.y) * scale;

                    int index = strip.Index(column, row);
                    strip.Positions[index] = vertex;
                    detail.Evaluate(distance + 0.6f * section.x + 0.8f * section.y, vertex.y, 1f,
                        out RockFaceSample sample);
                    float shade = Mathf.Max(sample.Cavity, cavity);
                    strip.Colors[index] = new Color(sample.Hardness, shade, sample.Layer, 0f);
                    strip.GroundOffsets[index] = Vector2.zero;
                    // Block parts inside the core or below the ground are not drawn.
                    float radial = Mathf.Sqrt(section.x * section.x / (shape.HalfWidth * shape.HalfWidth) +
                                              section.y * section.y / (shape.HalfDepth * shape.HalfDepth)) /
                                   shape.LargestRadial;
                    bool insideCore = radial < CoreRadial(distance, path.Length, shape.Layer, blocks) - CoreCullMargin;
                    strip.Active[index] = !insideCore && !IsBuried(vertex, shape.Terrain, blocks);
                }
            }

            // Surface noise along the grid normals, then the final normals.
            RockFaceBuilder.ComputeNormals(strip, columns, 0);
            if (blocks.NoiseDepth > 0f)
            {
                for (int index = 0; index < strip.VertexCount; index++)
                {
                    Vector3 position = strip.Positions[index];
                    float noise = blocks.NoiseDepth * RockFaceNoise.Surface(position, blocks.NoiseScale);
                    strip.Positions[index] = position + strip.Normals[index] * noise;
                }
                RockFaceBuilder.ComputeNormals(strip, columns, 0);
            }
        }

        // A tube along the arch inside the outer block columns. It follows the outer layer, so it becomes thinner
        // toward the crown with the columns, and it goes into the ground at both feet.
        private static void FillCore(RockFaceStrip strip, ArchPath path, ArchShape shape, RockArchBlocks blocks,
            RockFaceDetail detail)
        {
            strip.CullHidden = true;
            float halfWidth = shape.HalfWidth;
            float halfDepth = shape.HalfDepth;
            int sides = strip.Columns;
            int rows = strip.Rows;
            float start = -blocks.FootDepth;
            float end = path.Length + blocks.FootDepth;
            var columns = new RockFaceBuilder.Column[sides];
            path.Evaluate(0.5f * path.Length, out _, out Vector3 middleSide, out Vector3 middleUp);
            for (int column = 0; column < sides; column++)
            {
                float angle = column * 2f * Mathf.PI / sides;
                float cos = Mathf.Cos(angle);
                float sin = Mathf.Sin(angle);
                columns[column] = new RockFaceBuilder.Column
                {
                    Outward = (middleSide * cos + middleUp * sin).normalized,
                    Active = true
                };
                for (int row = 0; row < rows; row++)
                {
                    float distance = Mathf.Lerp(start, end, row / (float)(rows - 1));
                    float radial = CoreRadial(distance, path.Length, shape.Layer, blocks);
                    float scale = SectionScale(distance, path.Length, blocks) * radial * shape.LargestRadial;
                    path.Evaluate(distance, out Vector3 axis, out Vector3 side, out Vector3 up);
                    Vector3 vertex = axis + (side * (cos * halfWidth) + up * (sin * halfDepth)) * scale;
                    int index = strip.Index(column, row);
                    strip.Positions[index] = vertex;
                    detail.Evaluate(distance + 0.6f * cos * halfWidth, vertex.y, 1f, out RockFaceSample sample);
                    // The core is deep in the gaps, so it is darker.
                    strip.Colors[index] = new Color(sample.Hardness, Mathf.Max(sample.Cavity, 0.7f), sample.Layer, 0f);
                    strip.GroundOffsets[index] = Vector2.zero;
                    strip.Active[index] = !IsBuried(vertex, shape.Terrain, blocks);
                }
            }
            RockFaceBuilder.ComputeNormals(strip, columns, 0);
            for (int index = 0; index < strip.VertexCount; index++)
            {
                Vector3 position = strip.Positions[index];
                float noise = blocks.NoiseDepth * RockFaceNoise.Surface(position, blocks.NoiseScale);
                strip.Positions[index] = position + strip.Normals[index] * noise;
            }
            RockFaceBuilder.ComputeNormals(strip, columns, 0);
        }

        // Radial share of the core surface at a distance along the arch.
        private static float CoreRadial(float distance, float length, float layer, RockArchBlocks blocks)
        {
            return Mathf.Max(CoreMinimum, OuterRadial(distance, length, blocks) - CoreDepth * layer);
        }

        // True when a point is deeper below the terrain than the surface noise can lift it.
        private static bool IsBuried(Vector3 vertex, TerrainHeightSampler terrain, RockArchBlocks blocks)
        {
            return terrain.TrySampleHeight(vertex.x, vertex.z, out float ground) &&
                   vertex.y < ground - blocks.NoiseDepth - BuriedMargin;
        }

        // World XZ area around the arch that the terrain reads cover.
        private static Rect PathRegion(ArchPath path, float halfWidth, float halfDepth, RockArchBlocks blocks)
        {
            float reach = Mathf.Max(halfWidth, halfDepth) + blocks.EdgeJut + blocks.Relief + 10f;
            Vector2 minimum = new(float.MaxValue, float.MaxValue);
            Vector2 maximum = new(float.MinValue, float.MinValue);
            foreach (Vector3 centre in path.Centers)
            {
                minimum = Vector2.Min(minimum, new Vector2(centre.x, centre.z));
                maximum = Vector2.Max(maximum, new Vector2(centre.x, centre.z));
            }
            return Rect.MinMaxRect(minimum.x - reach, minimum.y - reach, maximum.x + reach, maximum.y + reach);
        }

        // A copy of the strip that faces the other way.
        private static void CopyInverted(RockFaceStrip source, RockFaceStrip target)
        {
            for (int index = 0; index < source.VertexCount; index++)
            {
                target.Positions[index] = source.Positions[index];
                target.Normals[index] = -source.Normals[index];
                target.Colors[index] = source.Colors[index];
                target.GroundOffsets[index] = source.GroundOffsets[index];
                target.Active[index] = source.Active[index];
            }
            target.FlipWinding = !source.FlipWinding;
            target.CullHidden = source.CullHidden;
        }

        // Rock face rules at the arch scale, used for the colours of the strata, hard layers and joints.
        private static RockFaceSettings DetailSettings(RockArchBlocks blocks, int seed)
        {
            float scale = blocks.DetailScale;
            var settings = new RockFaceSettings { Seed = seed };
            settings.StrataThickness *= scale;
            settings.StrataWarp *= scale;
            settings.StrataContinuity *= scale;
            settings.PinchDepth *= scale;
            settings.FaultSpacing *= scale;
            settings.FaultThrow *= scale;
            settings.JointSpacing *= scale;
            settings.JointWidth *= scale;
            settings.MassScale *= scale;
            settings.DomainWarp *= scale;
            settings.GrooveSpacing *= scale;
            settings.RoughnessScale *= scale;
            return settings;
        }
        #endregion
    }
}
