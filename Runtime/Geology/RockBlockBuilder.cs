using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace LoogaSoft.Terrain.Geology
{
    /// <summary>Block shape of a rock formation in block mode: tall rock blocks with fissures between them.</summary>
    [Serializable]
    public sealed class RockBlockShape
    {
        [Header("Blocks")]
        [Min(2f), Tooltip("Typical width of the blocks inside the formation, in meters.")]
        public float BlockSize = 14f;
        [Min(1.5f), Tooltip("Typical width of the blocks at the outline, in meters.")]
        public float EdgeBlockSize = 7f;
        [Min(1f), Tooltip("Distance from the outline over which the edge rules apply, in meters.")]
        public float EdgeBand = 12f;
        [Min(0f), Tooltip("Largest distance that an edge block juts out past the outline, in meters.")]
        public float EdgeJut = 6f;
        [Min(0f), Tooltip("Largest distance that the edge blocks step back from the outline, in meters.")]
        public float EdgeRecess = 5f;
        [Min(2f), Tooltip("Typical length along the edge of a group of jutting or receding blocks, in meters.")]
        public float JutSize = 30f;
        [Min(0f), Tooltip("Distance that the formation grows past the outline, in meters. Use it when the outline is " +
                          "the top edge of a former terrain wall.")]
        public float OutlineGrow;

        [Header("Height")]
        [Range(0f, 1f), Tooltip("Change of the height between blocks, as a share of the formation height.")]
        public float HeightVariation = 0.45f;
        [Range(0.1f, 1f), Tooltip("Smallest height of an edge block, as a share of the formation height.")]
        public float EdgeHeightMinimum = 0.35f;
        [Range(0f, 1f), Tooltip("Share of the blocks that rise as a tall pillar above their neighbours.")]
        public float PillarChance = 0.12f;
        [Tooltip("The formation height is the lowest block top. The other blocks rise above it by the height " +
                 "variation. Off = the blocks vary above and below the formation height.")]
        public bool HeightIsMinimum;
        [Tooltip("Smallest and largest tilt of a block top, in degrees.")]
        public Vector2 TopTilt = new(5f, 25f);

        [Header("Stacks")]
        [Min(2f), Tooltip("A block taller than this height can split into stacked blocks, in meters.")]
        public float StackHeight = 8f;
        [Range(0f, 1f), Tooltip("Share of the tall blocks that split into two or three stacked blocks.")]
        public float StackChance = 0.6f;
        [Min(0f), Tooltip("Largest horizontal offset of an upper block from the block below it, in meters. An offset " +
                          "larger than the inset gives an overhang.")]
        public float StackOffset = 1.2f;
        [Min(0f), Tooltip("Largest inset of an upper block from the block below it, in meters.")]
        public float StackInset = 0.8f;
        [Min(0f), Tooltip("Largest height change of the joint between stacked blocks along the block, in meters.")]
        public float JointWave = 0.6f;
        [Tooltip("Smallest and largest inward lean of a block wall from its foot to its top, in meters. A negative " +
                 "value leans the wall out.")]
        public Vector2 Taper = new(-0.4f, 1f);

        [Header("Fissures")]
        [Tooltip("Smallest and largest width of the fissures between blocks, in meters.")]
        public Vector2 FissureWidth = new(0.3f, 1.6f);
        [Range(0f, 1f), Tooltip("Share of neighbour blocks without a fissure between them.")]
        public float FusedShare = 0.25f;

        [Header("Edges")]
        [Min(0.1f), Tooltip("Radius of the rounded vertical corners of the blocks, in meters.")]
        public float CornerRadius = 0.7f;
        [Min(0.1f), Tooltip("Radius of the rounded top edge of the blocks, in meters.")]
        public float BevelRadius = 0.5f;
        [Range(0f, 0.9f), Tooltip("Change of the top edge radius along the block. Larger values give chipped edges.")]
        public float Chipping = 0.7f;

        [Header("Surface")]
        [Min(0f), Tooltip("Depth of the surface noise on all faces, in meters.")]
        public float NoiseDepth = 0.35f;
        [Min(0.5f), Tooltip("Typical size of the surface noise, in meters.")]
        public float NoiseScale = 3f;
        [Min(0f), Tooltip("Depth of the vertical solution flutes on the block walls, in meters.")]
        public float FluteDepth = 0.3f;
        [Min(0.3f), Tooltip("Typical distance between the vertical flutes, in meters.")]
        public float FluteSpacing = 1.6f;
        [Range(0f, 1f), Tooltip("Share of the wall strata, joints and buttresses on the block walls.")]
        public float DetailStrength = 0.35f;
        [Min(0.25f), Tooltip("Largest distance between mesh rows on the block walls, in meters. The flutes are " +
                             "vertical, so the walls need fewer rows than columns.")]
        public float WallRowSpacing = 1f;

        [Header("Cliff Walls")]
        [Min(0f), Tooltip("Cliff walls only: distance behind the top edge of the cliff where the blocks start, in " +
                          "meters.")]
        public float BandBack = 3f;
        [Min(0f), Tooltip("Cliff walls only: distance past the foot of the cliff where the blocks end, in meters.")]
        public float BandToe = 3f;
        [Min(0f), Tooltip("Cliff walls only: largest rise of a block top above the highest ground under the block, in " +
                          "meters.")]
        public float BandRise = 8f;
        [Min(4f), Tooltip("Cliff walls only: typical height of one stacked block, in meters. Tall blocks split into " +
                          "this many stacked blocks.")]
        public float BandTierHeight = 30f;
        [Min(0f), Tooltip("Formations only: world height of the block tops, in meters. The tops rise up to the band " +
                          "rise above it, and tall blocks stack as on cliff walls. 0 = the tops follow the formation " +
                          "height above the ground.")]
        public float TopLevel;

        internal RockBlockShape Copy()
        {
            return (RockBlockShape)MemberwiseClone();
        }
    }

    // Builds a rock formation from tall rock blocks. The closed outline is cut into cells. Each cell is one block:
    // vertical walls from below the terrain, a rounded top edge and a tilted top, all in one closed grid. Tall blocks
    // split into stacked blocks with offsets. Gaps between the cells give vertical fissures.
    internal static class RockBlockBuilder
    {
        private const float CellSpacing = 0.25f;
        private const float UndercutRadius = 0.35f;
        private const float BlockUStride = 97.3f;
        private const int ReferenceStride = 4;
        private const float LoopBlend = 4f;
        // A wall point within this distance of a touching neighbour counts as inside it. It is smaller than the
        // narrowest fissure, so walls across a fissure stay visible.
        private const float CoverTolerance = 0.2f;
        // Extra depth below the terrain or the solid neighbour rock before a point counts as hidden, in meters.
        private const float HiddenMargin = 0.3f;
        // Distance between outlines within which two blocks can hide each other, in meters.
        private const float HiddenReach = 2f;
        // Largest number of stacked blocks in one block of a cliff wall.
        private const int MaximumBandTiers = 12;
        // Grid of the cliff wall blocks, in meters: the same as the block formations.
        private const float BandColumnSpacing = 0.8f;
        private const float BandRowSpacing = 0.5f;
        private const float BandTopSpacing = 1f;

        private enum RowKind
        {
            Wall,
            Bevel,
            Step,
            Undercut,
            Top
        }

        private struct Row
        {
            public RowKind Kind;
            public int Tier;
            public float Value;
        }

        // One block: its rounded outline and the shape of each stacked tier.
        private sealed class Block
        {
            public Vector2[] Points;
            public Vector2[] Normals;
            public float[] U;
            public float Perimeter;
            public Vector2 Centre;
            public float Foot;
            public float UOffset;
            public int Tiers;
            public float[] TierTops;
            public Vector2[] Slopes;
            public Vector2[] Shifts;
            public float[] Insets;
            public float[] Tapers;
            public float[] Bevels;
            // Bevel radius and joint height change of each tier at each outline point.
            public float[][] BevelAt;
            public float[][] WaveAt;
            public readonly List<Row> Rows = new();
            // Bounds of the outline, and the height below which the block is solid rock all around.
            public Vector2 Minimum;
            public Vector2 Maximum;
            public float SolidTop;
            public readonly List<Block> Neighbours = new();
        }

        // Where the blocks of one build come from: the sites with their distance inside the edge, and the height rule.
        // A formation takes its heights from the outline. A cliff wall takes them from the ground under each block.
        private sealed class Layout
        {
            public List<Vector2> Sites;
            public List<float> Insides;
            public bool[] Included;
            public bool Band;
            public float FootDepth;
            public Vector2[] ReferencePoints;
            public float[] ReferenceGround;
            public float[] ReferenceHeights;
            public IReadOnlyList<RockFaceOpening> Openings;
            // Ground that the block shapes stand on and hide their buried points below. The ground blend always reads
            // the terrain.
            public IRockFaceGroundReader Ground;
        }

        /// <param name="outline">
        /// World points of the closed outline, about every 0.1 to 1 m, without a repeated end.
        /// </param>
        /// <param name="heights">Formation height above the ground at each outline point.</param>
        /// <param name="shapeGround">
        /// Optional ground for the block shapes, for example the heights before a stamp that reads the rock. Null = the
        /// terrain.
        /// </param>
        public static List<RockFaceStrip> Build(Vector3[] outline, float[] heights, RockFaceSettings settings,
            RockFormationShape shape, RockBlockShape blockShape, TerrainHeightSampler terrain,
            RockFaceBuilder.Workspace workspace, IRockFaceGroundReader shapeGround = null)
        {
            List<RockFaceStrip> strips = workspace.Strips;
            strips.Clear();
            int pointCount = outline.Length;
            if (pointCount < 3) return strips;

            shapeGround ??= terrain;
            var polygon = new Vector2[pointCount];
            var ground = new float[pointCount];
            for (int index = 0; index < pointCount; index++)
            {
                polygon[index] = new Vector2(outline[index].x, outline[index].z);
                ground[index] = shapeGround.TrySampleHeight(outline[index].x, outline[index].z, out float height)
                    ? height
                    : float.NaN;
            }
            FillMissing(ground);
            if (Area(polygon) < 0f)
            {
                Array.Reverse(polygon);
                Array.Reverse(ground);
                heights = (float[])heights.Clone();
                Array.Reverse(heights);
            }

            // The blocks stand on the ground of the outline, averaged along it, so small bumps do not show.
            float perimeter = 0f;
            for (int index = 0; index < pointCount; index++)
            {
                perimeter += (polygon[(index + 1) % pointCount] - polygon[index]).magnitude;
            }
            int window = Mathf.Max(1, Mathf.RoundToInt(shape.GroundSmoothing / (perimeter / pointCount)));
            int referenceCount = (pointCount + ReferenceStride - 1) / ReferenceStride;
            var referencePoints = new Vector2[referenceCount];
            var referenceGround = new float[referenceCount];
            var referenceHeights = new float[referenceCount];
            for (int reference = 0; reference < referenceCount; reference++)
            {
                int index = reference * ReferenceStride;
                float sum = 0f;
                for (int offset = -window; offset <= window; offset++)
                {
                    sum += ground[(index + offset + pointCount * 4) % pointCount];
                }
                referencePoints[reference] = polygon[index];
                referenceGround[reference] = sum / (2 * window + 1);
                referenceHeights[reference] = heights[index];
            }

            var random = new System.Random(settings.Seed);
            List<Vector2> sites = PlaceSites(polygon, blockShape, random);
            var layout = new Layout
            {
                Sites = sites,
                Insides = new List<float>(sites.Count),
                FootDepth = shape.FootDepth,
                ReferencePoints = referencePoints,
                ReferenceGround = referenceGround,
                ReferenceHeights = referenceHeights,
                Ground = shapeGround
            };
            foreach (Vector2 site in sites)
            {
                layout.Insides.Add(Inside(polygon, site, blockShape));
            }
            layout.Included = IncludeSites(layout, blockShape, settings.Seed);
            BuildBlocks(layout, settings, blockShape, terrain, workspace);
            return strips;
        }

        /// <summary>
        /// Builds a cliff wall from blocks into the workspace strips, which keep the strips already there. The blocks
        /// fill the band from a little behind the top edge to a little past the foot of the face. Any thread.
        /// </summary>
        /// <param name="columns">Face columns with their top edge (Rim) and foot distances along Outward.</param>
        /// <param name="loop">True when the active columns go all around a closed spline.</param>
        public static void BuildBand(RockFaceBuilder.Column[] columns, int columnCount, bool loop,
            RockFaceSettings settings, IReadOnlyList<RockFaceOpening> openings, TerrainHeightSampler terrain,
            RockFaceBuilder.Workspace workspace)
        {
            RockBlockShape blockShape = settings.WallBlocks;
            // The face grid suits the face search. The blocks use the finer grid of the block formations.
            settings = settings.Copy();
            settings.ColumnSpacing = Mathf.Min(settings.ColumnSpacing, BandColumnSpacing);
            settings.RowSpacing = Mathf.Min(settings.RowSpacing, BandRowSpacing);
            settings.TopSpacing = Mathf.Max(settings.TopSpacing, BandTopSpacing);
            var layout = new Layout
            {
                Band = true,
                FootDepth = Mathf.Max(1f, settings.TuckDepth * 2f),
                Openings = openings,
                Ground = terrain
            };
            var random = new System.Random(settings.Seed);
            layout.Sites = new List<Vector2>();
            layout.Insides = new List<float>();
            if (loop)
            {
                PlaceBandSites(columns, 0, columnCount - 1, true, blockShape, random, layout);
            }
            else
            {
                // Each run of active columns is one band. All bands share the sites, so the blocks do not repeat.
                int start = -1;
                for (int index = 0; index <= columnCount; index++)
                {
                    bool active = index < columnCount && columns[index].Active;
                    if (start >= 0 && !active)
                    {
                        if (index - start >= 2) PlaceBandSites(columns, start, index - 1, false, blockShape, random, layout);
                        start = -1;
                    }
                    if (active && start < 0) start = index;
                }
            }
            if (layout.Sites.Count == 0) return;
            layout.Included = IncludeSites(layout, blockShape, settings.Seed);
            BuildBlocks(layout, settings, blockShape, terrain, workspace);
        }

        // The cells, blocks and block grids of a layout, added to the workspace strips.
        private static void BuildBlocks(Layout layout, RockFaceSettings settings, RockBlockShape blockShape,
            TerrainHeightSampler terrain, RockFaceBuilder.Workspace workspace)
        {
            List<Vector2> sites = layout.Sites;
            var grid = new SiteGrid(2.5f * Mathf.Max(blockShape.BlockSize, blockShape.EdgeBlockSize));
            for (int site = 0; site < sites.Count; site++)
            {
                grid.Add(sites[site], site);
            }
            var blocks = new List<Block>();
            var cell = new List<Vector2>();
            var scratch = new List<Vector2>();
            var near = new List<int>();
            float yMinimum = float.MaxValue;
            float yMaximum = float.MinValue;
            for (int site = 0; site < sites.Count; site++)
            {
                if (!layout.Included[site]) continue;
                grid.Query(sites[site], 2f * grid.CellSize, near);
                if (!CutCell(sites, layout.Included, near, site, blockShape, settings.Seed, cell, scratch)) continue;
                Block block = CreateBlock(cell, sites[site], site, layout, settings, blockShape);
                if (block == null) continue;
                blocks.Add(block);
                yMinimum = Mathf.Min(yMinimum, block.Foot);
                yMaximum = Mathf.Max(yMaximum, block.TierTops[block.Tiers - 1] + 5f);
            }
            if (blocks.Count == 0) return;

            // Each block reads the wall rules at its own distance along u, so neighbour blocks get different joints.
            var detail = new RockFaceDetail(settings, 0f, sites.Count * BlockUStride + 200f, yMinimum, yMaximum);
            var blockStrips = new RockFaceStrip[blocks.Count];
            for (int index = 0; index < blocks.Count; index++)
            {
                blockStrips[index] = workspace.TakeStrip(blocks[index].Points.Length, blocks[index].Rows.Count, true);
            }
            FindNeighbours(blocks, blockShape);
            Parallel.For(0, blocks.Count, index =>
            {
                FillStrip(blocks[index], blockStrips[index], settings, blockShape, detail, terrain, layout.Ground,
                    layout.Openings);
            });
        }

        #region Cells
        // Poisson disk sites inside the outline and in a band around it. The spacing is smaller near the outline, so
        // the edge has more blocks.
        private static List<Vector2> PlaceSites(Vector2[] polygon, RockBlockShape blockShape, System.Random random)
        {
            Vector2 minimum = polygon[0];
            Vector2 maximum = polygon[0];
            foreach (Vector2 point in polygon)
            {
                minimum = Vector2.Min(minimum, point);
                maximum = Vector2.Max(maximum, point);
            }
            float band = blockShape.OutlineGrow + blockShape.EdgeJut + 2f * blockShape.EdgeBlockSize;
            minimum -= Vector2.one * band;
            maximum += Vector2.one * band;
            float step = Mathf.Max(0.5f, blockShape.EdgeBlockSize * 0.35f);
            var candidates = new List<(Vector2 Point, float Radius)>();
            for (float z = minimum.y; z <= maximum.y; z += step)
            {
                for (float x = minimum.x; x <= maximum.x; x += step)
                {
                    var point = new Vector2(x + step * (float)(random.NextDouble() - 0.5),
                        z + step * (float)(random.NextDouble() - 0.5));
                    float inside = Inside(polygon, point, blockShape);
                    if (inside < -band) continue;
                    float share = RockFaceBuilder.Smooth01(Mathf.Max(0f, inside) / blockShape.EdgeBand);
                    candidates.Add((point, 0.85f * Mathf.Lerp(blockShape.EdgeBlockSize, blockShape.BlockSize, share)));
                }
            }
            for (int index = candidates.Count - 1; index > 0; index--)
            {
                int other = random.Next(index + 1);
                (candidates[index], candidates[other]) = (candidates[other], candidates[index]);
            }

            var sites = new List<Vector2>();
            var radii = new List<float>();
            foreach ((Vector2 point, float radius) in candidates)
            {
                bool free = true;
                for (int site = 0; site < sites.Count && free; site++)
                {
                    float spacing = 0.5f * (radius + radii[site]);
                    free = (sites[site] - point).sqrMagnitude >= spacing * spacing;
                }
                if (!free) continue;
                sites.Add(point);
                radii.Add(radius);
            }
            return sites;
        }

        // Poisson disk sites in the band of a cliff face and around it. The distance inside the band comes from the
        // column values, so no polygon test is needed for these long faces.
        private static void PlaceBandSites(RockFaceBuilder.Column[] columns, int first, int last, bool loop,
            RockBlockShape blockShape, System.Random random, Layout layout)
        {
            float band = blockShape.EdgeJut + 2f * blockShape.EdgeBlockSize;
            float step = Mathf.Max(0.5f, blockShape.EdgeBlockSize * 0.35f);
            float startU = columns[first].U;
            float endU = columns[last].U;
            var candidates = new List<(Vector2 Point, float Inside, float Radius)>();
            float nextU = startU;
            for (int index = first; index <= last; index++)
            {
                RockFaceBuilder.Column column = columns[index];
                if (column.U < nextU) continue;
                nextU = column.U + step;
                float back = column.Rim - blockShape.BandBack;
                float toe = column.Foot + blockShape.BandToe;
                float ends = loop ? float.MaxValue : Mathf.Min(column.U - startU, endU - column.U);
                for (float v = back - band; v <= toe + band; v += step)
                {
                    float jitteredV = v + step * (float)(random.NextDouble() - 0.5);
                    float inside = Mathf.Min(Mathf.Min(jitteredV - back, toe - jitteredV), ends);
                    if (inside < -band) continue;
                    Vector3 world = column.Origin + column.Outward * jitteredV;
                    var tangent = new Vector2(column.Outward.z, -column.Outward.x);
                    Vector2 point = new Vector2(world.x, world.z) + tangent * (step * (float)(random.NextDouble() - 0.5));
                    float share = RockFaceBuilder.Smooth01(Mathf.Max(0f, inside) / blockShape.EdgeBand);
                    candidates.Add((point, inside,
                        0.85f * Mathf.Lerp(blockShape.EdgeBlockSize, blockShape.BlockSize, share)));
                }
            }
            for (int index = candidates.Count - 1; index > 0; index--)
            {
                int other = random.Next(index + 1);
                (candidates[index], candidates[other]) = (candidates[other], candidates[index]);
            }
            var grid = new SiteGrid(blockShape.BlockSize);
            int firstSite = layout.Sites.Count;
            var radii = new List<float>();
            var near = new List<int>();
            foreach ((Vector2 point, float inside, float radius) in candidates)
            {
                grid.Query(point, Mathf.Max(radius, blockShape.BlockSize), near);
                bool free = true;
                foreach (int site in near)
                {
                    float spacing = 0.5f * (radius + radii[site - firstSite]);
                    if ((layout.Sites[site] - point).sqrMagnitude < spacing * spacing)
                    {
                        free = false;
                        break;
                    }
                }
                if (!free) continue;
                grid.Add(point, layout.Sites.Count);
                layout.Sites.Add(point);
                layout.Insides.Add(inside);
                radii.Add(radius);
            }
        }

        // A uniform grid of site indices, so near sites are found without a test against every site.
        private sealed class SiteGrid
        {
            private readonly Dictionary<long, List<int>> _cells = new();
            private readonly List<Vector2> _points = new();

            public SiteGrid(float cellSize)
            {
                CellSize = Mathf.Max(1f, cellSize);
            }

            public float CellSize { get; }

            public void Add(Vector2 point, int index)
            {
                long key = Key(Mathf.FloorToInt(point.x / CellSize), Mathf.FloorToInt(point.y / CellSize));
                if (!_cells.TryGetValue(key, out List<int> members))
                {
                    members = new List<int>();
                    _cells.Add(key, members);
                }
                members.Add(index);
            }

            // Indices of the sites in the grid cells that a circle around the point touches.
            public void Query(Vector2 point, float radius, List<int> result)
            {
                result.Clear();
                int minimumX = Mathf.FloorToInt((point.x - radius) / CellSize);
                int maximumX = Mathf.FloorToInt((point.x + radius) / CellSize);
                int minimumZ = Mathf.FloorToInt((point.y - radius) / CellSize);
                int maximumZ = Mathf.FloorToInt((point.y + radius) / CellSize);
                for (int z = minimumZ; z <= maximumZ; z++)
                {
                    for (int x = minimumX; x <= maximumX; x++)
                    {
                        if (_cells.TryGetValue(Key(x, z), out List<int> members)) result.AddRange(members);
                    }
                }
            }

            private static long Key(int x, int z)
            {
                return ((long)x << 32) ^ (uint)z;
            }
        }

        // Sites that become blocks. Each site near the edge can stand out past the outline or step back from it, by a
        // world noise in groups of about the jut size and by its own random share. The edge is not the outline.
        private static bool[] IncludeSites(Layout layout, RockBlockShape blockShape, int seed)
        {
            List<Vector2> sites = layout.Sites;
            var included = new bool[sites.Count];
            for (int site = 0; site < sites.Count; site++)
            {
                Vector2 point = sites[site];
                float group = RockFaceNoise.Perlin(point.x / blockShape.JutSize + seed * 0.71f,
                    point.y / blockShape.JutSize + 13.7f);
                float share = 0.6f * group + 0.4f * Hash(seed, site, 0, 30);
                float reach = Mathf.Lerp(-blockShape.EdgeRecess, blockShape.EdgeJut, share);
                included[site] = layout.Insides[site] > -reach;
            }
            return included;
        }

        // Distance inside the edge of the formation: the outline moved out by the grow distance.
        private static float Inside(Vector2[] polygon, Vector2 point, RockBlockShape blockShape)
        {
            return RockFaceBuilder.SignedRimDistance(polygon, point.x, point.y) + blockShape.OutlineGrow;
        }

        // The cell of one site: a box around the site, cut by the half planes to the near sites. A fissure gap moves
        // the half plane back between two blocks. Next to a site that is not a block, the cut is an outer face of the
        // formation. False when the cell is too small.
        private static bool CutCell(List<Vector2> sites, bool[] included, List<int> near, int site,
            RockBlockShape blockShape, int seed, List<Vector2> cell, List<Vector2> scratch)
        {
            Vector2 centre = sites[site];
            float reach = 2.5f * Mathf.Max(blockShape.BlockSize, blockShape.EdgeBlockSize);
            // The box limits a cell that has no neighbour on one side.
            float box = Mathf.Max(blockShape.BlockSize, blockShape.EdgeBlockSize);
            cell.Clear();
            cell.Add(centre + new Vector2(-box, -box));
            cell.Add(centre + new Vector2(box, -box));
            cell.Add(centre + new Vector2(box, box));
            cell.Add(centre + new Vector2(-box, box));
            foreach (int other in near)
            {
                if (cell.Count < 3) break;
                if (other == site) continue;
                Vector2 toOther = sites[other] - centre;
                float distance = toOther.magnitude;
                if (distance > 2f * reach || distance < 1e-4f) continue;
                Vector2 direction = toOther / distance;
                int low = Mathf.Min(site, other);
                int high = Mathf.Max(site, other);
                float fused = Hash(seed, low, high, 1);
                float width = Hash(seed, low, high, 2);
                float gap = !included[other] || fused < blockShape.FusedShare
                    ? 0f
                    : Mathf.Lerp(blockShape.FissureWidth.x, blockShape.FissureWidth.y, width);
                Clip(cell, scratch, direction, Vector2.Dot(centre + 0.5f * toOther, direction) - 0.5f * gap);
            }
            float minimumSize = 0.5f * blockShape.EdgeBlockSize;
            return cell.Count >= 3 && Area(cell) >= minimumSize * minimumSize;
        }

        // Keeps the part of the closed polygon where Dot(point, normal) <= offset (Sutherland-Hodgman).
        internal static void Clip(List<Vector2> polygon, List<Vector2> scratch, Vector2 normal, float offset)
        {
            scratch.Clear();
            int count = polygon.Count;
            for (int index = 0; index < count; index++)
            {
                Vector2 current = polygon[index];
                Vector2 next = polygon[(index + 1) % count];
                float a = Vector2.Dot(current, normal) - offset;
                float b = Vector2.Dot(next, normal) - offset;
                if (a <= 0f)
                {
                    scratch.Add(current);
                }
                if ((a <= 0f) != (b <= 0f))
                {
                    scratch.Add(Vector2.Lerp(current, next, a / (a - b)));
                }
            }
            polygon.Clear();
            polygon.AddRange(scratch);
        }
        #endregion

        #region Blocks
        // Rounds the cell corners, samples the outline at the column spacing and chooses the tiers of the block.
        private static Block CreateBlock(List<Vector2> cell, Vector2 site, int index, Layout layout,
            RockFaceSettings settings, RockBlockShape blockShape)
        {
            List<Vector2> rounded = Resample(cell, CellSpacing);
            int radius = Mathf.Max(1, Mathf.RoundToInt(0.5f * blockShape.CornerRadius / CellSpacing));
            Smooth(rounded, radius);
            Smooth(rounded, radius);
            List<Vector2> points = Resample(rounded, settings.ColumnSpacing);
            if (points.Count < 8) return null;
            Vector2 centre = Centroid(points);
            if (RockFaceBuilder.SignedRimDistance(points.ToArray(), centre.x, centre.y) < 0.5f)
            {
                centre = site;
            }

            int count = points.Count;
            var block = new Block
            {
                Points = points.ToArray(),
                Normals = new Vector2[count],
                U = new float[count],
                Centre = centre,
                UOffset = index * BlockUStride + 50f
            };
            float groundMinimum = float.MaxValue;
            float groundMaximum = float.MinValue;
            float farthest = 0f;
            for (int point = 0; point < count; point++)
            {
                Vector2 previous = block.Points[(point + count - 1) % count];
                Vector2 next = block.Points[(point + 1) % count];
                Vector2 tangent = (next - previous).normalized;
                block.Normals[point] = new Vector2(tangent.y, -tangent.x);
                if (point > 0)
                {
                    block.U[point] = block.U[point - 1] + (block.Points[point] - block.Points[point - 1]).magnitude;
                }
                farthest = Mathf.Max(farthest, (block.Points[point] - centre).magnitude);
                if (layout.Ground.TrySampleHeight(block.Points[point].x, block.Points[point].y, out float height))
                {
                    groundMinimum = Mathf.Min(groundMinimum, height);
                    groundMaximum = Mathf.Max(groundMaximum, height);
                }
            }
            block.Perimeter = block.U[count - 1] + (block.Points[0] - block.Points[count - 1]).magnitude;
            if (groundMinimum == float.MaxValue) return null;
            block.Foot = groundMinimum - layout.FootDepth;

            // Height: the formation height with block variation and clusters. Edge blocks change strongly, so the
            // silhouette steps.
            int seed = settings.Seed;
            float own = Hash(seed, index, 0, 5);
            if (layout.Band)
            {
                return CreateBandBlock(block, index, groundMinimum, groundMaximum, farthest, own, settings,
                    blockShape);
            }
            if (blockShape.TopLevel > 0f)
            {
                return CreateBandBlock(block, index, groundMinimum, Mathf.Max(groundMaximum, blockShape.TopLevel),
                    farthest, own, settings, blockShape);
            }
            float formationGround = Weighted(layout.ReferencePoints, layout.ReferenceGround, centre);
            float formationHeight = Weighted(layout.ReferencePoints, layout.ReferenceHeights, centre);
            float inside = layout.Insides[index];
            float edge = 1f - RockFaceBuilder.Smooth01(inside / blockShape.EdgeBand);
            float cluster = RockFaceNoise.Perlin(centre.x / 35f + 3.3f, centre.y / 35f + seed * 1.7f);
            float edgeShare = Mathf.Lerp(blockShape.EdgeHeightMinimum, 1f, Mathf.Pow(Hash(seed, index, 0, 6), 0.7f));
            float share;
            if (blockShape.HeightIsMinimum)
            {
                // Each block rises above the formation height. Edge blocks rise less, so the edge still steps.
                float rise = blockShape.HeightVariation * (own + cluster);
                share = 1f + rise * Mathf.Lerp(1f, edgeShare, edge);
            }
            else
            {
                share = 1f + blockShape.HeightVariation * (2f * own - 1f) +
                        blockShape.HeightVariation * (2f * cluster - 1f);
                share = Mathf.Lerp(share, Mathf.Min(share, edgeShare), edge);
            }
            if (Hash(seed, index, 0, 7) < blockShape.PillarChance)
            {
                share += 0.4f;
            }
            float top = Mathf.Max(formationGround + formationHeight * share, groundMaximum + 3f);

            // Tall blocks split into two or three stacked tiers.
            float above = top - groundMaximum;
            int tiers = 1;
            if (above > blockShape.StackHeight && Hash(seed, index, 0, 8) < blockShape.StackChance)
            {
                tiers = above > 2f * blockShape.StackHeight && Hash(seed, index, 0, 9) < 0.5f ? 3 : 2;
            }
            var splits = new float[tiers];
            for (int tier = 0; tier < tiers; tier++)
            {
                // Share of the block height at the top of each tier.
                float splitNoise = Hash(seed, index, tier, 10);
                splits[tier] = tier == tiers - 1
                    ? 1f
                    : tiers == 2
                        ? Mathf.Lerp(0.4f, 0.65f, splitNoise)
                        : Mathf.Lerp(tier == 0 ? 0.3f : 0.6f, tier == 0 ? 0.45f : 0.78f, splitNoise);
            }
            SetTiers(block, index, groundMaximum, top, splits, farthest, settings, blockShape);
            if (blockShape.HeightIsMinimum)
            {
                // The low side of the tilted top must not go below the formation height.
                int last = tiers - 1;
                float reach = farthest + block.Shifts[last].magnitude;
                float lowest = block.TierTops[last] - block.Slopes[last].magnitude * reach - block.Bevels[last];
                block.TierTops[last] += Mathf.Max(0f, formationGround + formationHeight - lowest);
            }
            FinishBlock(block, settings, blockShape, farthest);
            return block;
        }

        // The grid rows, the bounds and the solid height of a block whose tiers are set.
        private static void FinishBlock(Block block, RockFaceSettings settings, RockBlockShape blockShape,
            float farthest)
        {
            CreateRows(block, settings, Mathf.Max(settings.RowSpacing, blockShape.WallRowSpacing), farthest);
            int count = block.Points.Length;
            block.Minimum = block.Points[0];
            block.Maximum = block.Points[0];
            float solidTop = float.MaxValue;
            for (int point = 0; point < count; point++)
            {
                block.Minimum = Vector2.Min(block.Minimum, block.Points[point]);
                block.Maximum = Vector2.Max(block.Maximum, block.Points[point]);
                solidTop = Mathf.Min(solidTop, TierTop(block, point, 0) - block.BevelAt[0][point]);
            }
            block.SolidTop = solidTop - blockShape.NoiseDepth - HiddenMargin;
        }

        // The tiers of a block: tops at the split shares between the tier base and the top, a tilted top, offsets,
        // insets, leaning walls and chipped edges.
        private static void SetTiers(Block block, int index, float tierBase, float top, float[] splits, float farthest,
            RockFaceSettings settings, RockBlockShape blockShape)
        {
            int seed = settings.Seed;
            int count = block.Points.Length;
            int tiers = splits.Length;
            block.Tiers = tiers;
            block.TierTops = new float[tiers];
            block.Slopes = new Vector2[tiers];
            block.Shifts = new Vector2[tiers];
            block.Insets = new float[tiers];
            block.Tapers = new float[tiers];
            block.Bevels = new float[tiers];
            block.BevelAt = new float[tiers][];
            block.WaveAt = new float[tiers][];
            for (int tier = 0; tier < tiers; tier++)
            {
                block.TierTops[tier] = tierBase + (top - tierBase) * splits[tier];
                float below = tier == 0 ? tierBase : block.TierTops[tier - 1];
                float tierHeight = block.TierTops[tier] - below;

                // The top tier tilts most. Lower tiers have flatter tops, which show as ledges.
                float tilt = Mathf.Lerp(blockShape.TopTilt.x, blockShape.TopTilt.y, Hash(seed, index, tier, 11));
                if (tier < tiers - 1)
                {
                    tilt *= 0.4f;
                }
                float slope = Mathf.Min(Mathf.Tan(tilt * Mathf.Deg2Rad), 0.3f * tierHeight / Mathf.Max(farthest, 1f));
                float direction = Hash(seed, index, tier, 12) * 2f * Mathf.PI;
                block.Slopes[tier] = new Vector2(Mathf.Cos(direction), Mathf.Sin(direction)) * slope;
                block.Bevels[tier] = blockShape.BevelRadius * Mathf.Lerp(0.6f, 1.4f, Hash(seed, index, tier, 13));
                block.Tapers[tier] = Mathf.Lerp(blockShape.Taper.x, blockShape.Taper.y, Hash(seed, index, tier, 17));
                // Chipped edges: the bevel radius changes along the block. The joint to the next tier is wavy.
                block.BevelAt[tier] = new float[count];
                block.WaveAt[tier] = new float[count];
                for (int point = 0; point < count; point++)
                {
                    float chip = 2f * LoopNoise(block, block.U[point], 2.2f, tier * 7.1f + 0.5f) - 1f;
                    block.BevelAt[tier][point] = block.Bevels[tier] * (1f + blockShape.Chipping * chip);
                    float wave = 2f * LoopNoise(block, block.U[point], 4f, tier * 5.3f + 31.1f) - 1f;
                    block.WaveAt[tier][point] = tier < tiers - 1 ? blockShape.JointWave * wave : 0f;
                }
                if (tier == 0) continue;
                float shiftDirection = Hash(seed, index, tier, 14) * 2f * Mathf.PI;
                float shift = blockShape.StackOffset * Mathf.Lerp(0.3f, 1f, Hash(seed, index, tier, 15));
                block.Shifts[tier] = block.Shifts[tier - 1] +
                                     new Vector2(Mathf.Cos(shiftDirection), Mathf.Sin(shiftDirection)) * shift;
                block.Insets[tier] = TopInset(block, tier - 1) +
                                     blockShape.StackInset * Mathf.Lerp(0.2f, 1f, Hash(seed, index, tier, 16));
            }
        }

        // A block of a cliff wall: its top is the highest ground under it plus a stepped rise, and its walls stack into
        // tiers of about the band tier height from its foot, as the tall walls of a block formation.
        private static Block CreateBandBlock(Block block, int index, float groundMinimum, float groundMaximum,
            float farthest, float own, RockFaceSettings settings, RockBlockShape blockShape)
        {
            int seed = settings.Seed;
            Vector2 centre = block.Centre;
            float cluster = RockFaceNoise.Perlin(centre.x / 35f + 3.3f, centre.y / 35f + seed * 1.7f);
            float rise = blockShape.BandRise * (0.6f * own + 0.4f * cluster);
            if (Hash(seed, index, 0, 7) < blockShape.PillarChance)
            {
                rise += blockShape.BandRise;
            }
            float top = groundMaximum + Mathf.Max(0.5f, rise);
            float height = top - groundMinimum;
            // Each block gets its own tier count and uneven tier heights, so the joints of neighbour blocks do not line
            // up into rows.
            float tierHeight = blockShape.BandTierHeight * Mathf.Lerp(0.7f, 1.3f, Hash(seed, index, 0, 18));
            int tiers = Mathf.Clamp(Mathf.RoundToInt(height / tierHeight), 1, MaximumBandTiers);
            var splits = new float[tiers];
            float total = 0f;
            for (int tier = 0; tier < tiers; tier++)
            {
                total += Mathf.Lerp(0.5f, 1.5f, Hash(seed, index, tier, 10));
                splits[tier] = total;
            }
            for (int tier = 0; tier < tiers; tier++)
            {
                splits[tier] /= total;
            }
            SetTiers(block, index, groundMinimum, top, splits, farthest, settings, blockShape);
            // The low side of the tilted top must not go below the highest ground under the block.
            int last = tiers - 1;
            float reach = farthest + block.Shifts[last].magnitude;
            float lowest = block.TierTops[last] - block.Slopes[last].magnitude * reach - block.Bevels[last];
            block.TierTops[last] += Mathf.Max(0f, groundMaximum + 0.5f - lowest);
            FinishBlock(block, settings, blockShape, farthest);
            return block;
        }

        // The rows of the closed block grid: from the foot up each tier, over its rounded edge, across the ledge to
        // the next tier, and over the top to the centre.
        private static void CreateRows(Block block, RockFaceSettings settings, float wallRowSpacing, float farthest)
        {
            block.Rows.Clear();
            for (int tier = 0; tier < block.Tiers; tier++)
            {
                if (tier > 0)
                {
                    block.Rows.Add(new Row { Kind = RowKind.Step, Tier = tier });
                    block.Rows.Add(new Row { Kind = RowKind.Undercut, Tier = tier, Value = 0.25f * Mathf.PI });
                    block.Rows.Add(new Row { Kind = RowKind.Undercut, Tier = tier, Value = 0.5f * Mathf.PI });
                }
                float wallHeight = 0f;
                for (int point = 0; point < block.Points.Length; point++)
                {
                    wallHeight = Mathf.Max(wallHeight, TierTop(block, point, tier) - TierBase(block, point, tier));
                }
                int wallRows = Mathf.Max(2, Mathf.CeilToInt(wallHeight / wallRowSpacing));
                for (int row = tier == 0 ? 0 : 1; row <= wallRows; row++)
                {
                    block.Rows.Add(new Row { Kind = RowKind.Wall, Tier = tier, Value = row / (float)wallRows });
                }
                int bevelRows = Mathf.Max(3, Mathf.CeilToInt(0.5f * Mathf.PI * block.Bevels[tier] * 1.4f /
                                                             settings.RowSpacing));
                for (int row = 1; row <= bevelRows; row++)
                {
                    float angle = 0.5f * Mathf.PI * row / bevelRows;
                    block.Rows.Add(new Row { Kind = RowKind.Bevel, Tier = tier, Value = angle });
                }
            }
            int topRows = Mathf.Max(3, Mathf.CeilToInt(farthest / settings.TopSpacing));
            for (int row = 1; row <= topRows; row++)
            {
                block.Rows.Add(new Row { Kind = RowKind.Top, Tier = block.Tiers - 1, Value = row / (float)topRows });
            }
        }
        #endregion

        // Blocks whose outlines are close enough to hide a wall of each other.
        private static void FindNeighbours(List<Block> blocks, RockBlockShape blockShape)
        {
            float reach = HiddenReach + blockShape.NoiseDepth;
            for (int first = 0; first < blocks.Count; first++)
            {
                Block a = blocks[first];
                for (int second = first + 1; second < blocks.Count; second++)
                {
                    Block b = blocks[second];
                    if (a.Minimum.x > b.Maximum.x + reach || b.Minimum.x > a.Maximum.x + reach ||
                        a.Minimum.y > b.Maximum.y + reach || b.Minimum.y > a.Maximum.y + reach)
                    {
                        continue;
                    }
                    a.Neighbours.Add(b);
                    b.Neighbours.Add(a);
                }
            }
        }

        // True when a wall point is below the terrain or inside a neighbour block that touches this block. A point
        // across a fissure is farther from the neighbour than the tolerance, so it stays visible.
        private static bool IsHidden(Vector3 vertex, List<Block> candidates, IRockFaceGroundReader shapeGround,
            RockBlockShape blockShape)
        {
            if (shapeGround.TrySampleHeight(vertex.x, vertex.z, out float ground) &&
                vertex.y < ground - blockShape.NoiseDepth - HiddenMargin)
            {
                return true;
            }
            foreach (Block neighbour in candidates)
            {
                if (vertex.y >= neighbour.SolidTop) continue;
                if (RockFaceBuilder.SignedRimDistance(neighbour.Points, vertex.x, vertex.z) > -CoverTolerance)
                {
                    return true;
                }
            }
            return false;
        }

        #region Surface
        private static void FillStrip(Block block, RockFaceStrip strip, RockFaceSettings settings,
            RockBlockShape blockShape, RockFaceDetail detail, TerrainHeightSampler terrain,
            IRockFaceGroundReader ground, IReadOnlyList<RockFaceOpening> openings)
        {
            int count = block.Points.Length;
            var columns = new RockFaceBuilder.Column[count];
            var candidates = new List<Block>();
            strip.CullHidden = true;
            for (int point = 0; point < count; point++)
            {
                // Neighbours near this column that can hide its wall.
                candidates.Clear();
                Vector2 outline = block.Points[point];
                foreach (Block neighbour in block.Neighbours)
                {
                    if (RockFaceBuilder.SignedRimDistance(neighbour.Points, outline.x, outline.y) > -HiddenReach)
                    {
                        candidates.Add(neighbour);
                    }
                }
                Vector2 normal2 = block.Normals[point];
                var outward = new Vector3(normal2.x, 0f, normal2.y);
                float u = block.U[point];
                columns[point] = new RockFaceBuilder.Column
                {
                    Origin = new Vector3(block.Points[point].x, 0f, block.Points[point].y),
                    Outward = outward,
                    U = u,
                    Active = true
                };
                Vector2 beside = block.Points[point] + normal2 * settings.GroundReach;

                for (int row = 0; row < block.Rows.Count; row++)
                {
                    Row spec = block.Rows[row];
                    Vector3 vertex = Evaluate(block, point, spec, out Vector3 profileNormal, out float wall);
                    float structure = wall * blockShape.DetailStrength;
                    float wallOffset = 0f;
                    RockFaceSample sample;
                    if (spec.Kind == RowKind.Top)
                    {
                        detail.Evaluate(vertex.x * 0.83f + vertex.z * 0.56f, vertex.y, 1f, out sample);
                    }
                    else
                    {
                        wallOffset = structure * WallDetail(detail, block, u, vertex.y, out sample);
                    }
                    // The joint between stacked blocks is a dark recess.
                    if (spec.Kind == RowKind.Undercut || spec.Kind == RowKind.Step)
                    {
                        sample.Cavity = Mathf.Max(sample.Cavity, 0.75f);
                    }
                    float noise = blockShape.NoiseDepth * RockFaceNoise.Surface(vertex, blockShape.NoiseScale);
                    float flutes = RockFaceNoise.Flutes(vertex, profileNormal, blockShape.FluteSpacing);
                    wallOffset += wall * blockShape.FluteDepth * flutes;
                    // The wall rules move the rounded edge only outward or inward with the wall. Along the edge normal,
                    // which points up near the top, they lifted the edge above the top, which showed as a lip.
                    Vector3 wallDirection = spec.Kind == RowKind.Bevel ? outward : profileNormal;
                    vertex += wallDirection * wallOffset + profileNormal * noise;
                    if (openings != null)
                    {
                        // An index loop: a foreach over IReadOnlyList allocated an enumerator for each vertex.
                        for (int opening = 0; opening < openings.Count; opening++)
                        {
                            vertex.y = Mathf.Min(vertex.y, openings[opening].HeightLimit(vertex.x, vertex.z));
                        }
                    }

                    int index = strip.Index(point, row);
                    strip.Positions[index] = vertex;
                    float groundWeight = spec.Kind == RowKind.Top
                        ? 0f
                        : RockFaceBuilder.GroundWeight(vertex, new Vector3(normal2.x, 0f, normal2.y), block.UOffset + u,
                            settings, terrain);
                    strip.Colors[index] = new Color(sample.Hardness, sample.Cavity, sample.Layer, groundWeight);
                    strip.GroundOffsets[index] = new Vector2(beside.x - vertex.x, beside.y - vertex.z);
                    bool hidden = spec.Kind != RowKind.Top && IsHidden(vertex, candidates, ground, blockShape);
                    strip.Active[index] = !hidden;
                }
            }
            RockFaceBuilder.ComputeNormals(strip, columns, 0);
        }

        // Position of one grid point before the surface noise, the outward direction of the profile there, and the
        // share of the wall rules (1 on the walls, 0 on the top).
        private static Vector3 Evaluate(Block block, int point, Row row, out Vector3 normal, out float wall)
        {
            int tier = row.Tier;
            Vector2 normal2 = block.Normals[point];
            var outward = new Vector3(normal2.x, 0f, normal2.y);
            float inset = block.Insets[tier];
            float topInset = TopInset(block, tier);
            float bevel = block.BevelAt[tier][point];
            Vector2 position;
            float y;
            switch (row.Kind)
            {
                case RowKind.Wall:
                {
                    position = InsetPoint(block, point, tier, Mathf.Lerp(inset, topInset, row.Value));
                    float bottom = TierBase(block, point, tier) + (tier > 0 ? UndercutRadius : 0f);
                    y = Mathf.Lerp(bottom, TierTop(block, point, tier) - bevel, row.Value);
                    normal = outward;
                    wall = 1f;
                    break;
                }
                case RowKind.Bevel:
                {
                    position = InsetPoint(block, point, tier, topInset + bevel * (1f - Mathf.Cos(row.Value)));
                    y = TierTop(block, point, tier) - bevel + bevel * Mathf.Sin(row.Value);
                    normal = (outward * Mathf.Cos(row.Value) + Vector3.up * Mathf.Sin(row.Value)).normalized;
                    wall = Mathf.Cos(row.Value);
                    break;
                }
                case RowKind.Step:
                {
                    position = InsetPoint(block, point, tier, inset + UndercutRadius);
                    y = TierBase(block, point, tier);
                    normal = (outward + Vector3.up).normalized;
                    wall = 0.5f;
                    break;
                }
                case RowKind.Undercut:
                {
                    position = InsetPoint(block, point, tier, inset + UndercutRadius * (1f - Mathf.Sin(row.Value)));
                    y = TierBase(block, point, tier) + UndercutRadius * (1f - Mathf.Cos(row.Value));
                    normal = outward;
                    wall = 1f;
                    break;
                }
                default:
                {
                    Vector2 edgePoint = InsetPoint(block, point, tier, topInset + bevel);
                    position = Vector2.Lerp(edgePoint, block.Centre + block.Shifts[tier], row.Value);
                    // A ledge under a stacked tier starts at the height of its wavy edge, so the edge shows no lip or
                    // dip. The wave fades toward the centre. The top tier has no wave.
                    y = Plane(block, tier, position) + block.WaveAt[tier][point] * (1f - row.Value);
                    normal = Vector3.up;
                    wall = 0f;
                    break;
                }
            }
            return new Vector3(position.x, y, position.y);
        }

        // The wall rules are not periodic. Near the end of the loop the offset blends into the offset at its start.
        private static float WallDetail(RockFaceDetail detail, Block block, float u, float y, out RockFaceSample sample)
        {
            float offset = detail.Evaluate(block.UOffset + u, y, 1f, out sample);
            float blendStart = block.Perimeter - LoopBlend;
            if (u <= blendStart) return offset;
            float wrapped = detail.Evaluate(block.UOffset + u - block.Perimeter, y, 1f, out RockFaceSample start);
            float weight = RockFaceBuilder.Smooth01((u - blendStart) / LoopBlend);
            sample.Hardness = Mathf.Lerp(sample.Hardness, start.Hardness, weight);
            sample.Cavity = Mathf.Lerp(sample.Cavity, start.Cavity, weight);
            if (weight > 0.5f)
            {
                sample.Layer = start.Layer;
            }
            return Mathf.Lerp(offset, wrapped, weight);
        }

        // The outline point moved toward the block centre by the inset, and by the offset of the tier.
        private static Vector2 InsetPoint(Block block, int point, int tier, float inset)
        {
            Vector2 toCentre = block.Centre - block.Points[point];
            float distance = toCentre.magnitude;
            float reach = distance > 1e-4f ? Mathf.Min(inset, 0.7f * distance) / distance : 0f;
            return block.Points[point] + toCentre * reach + block.Shifts[tier];
        }

        private static float Plane(Block block, int tier, Vector2 position)
        {
            return block.TierTops[tier] + Vector2.Dot(block.Slopes[tier], position - block.Centre - block.Shifts[tier]);
        }

        // Inset of the tier wall at its top. The wall leans from the tier inset at its foot to this inset.
        private static float TopInset(Block block, int tier)
        {
            return block.Insets[tier] + block.Tapers[tier];
        }

        // Height of the tier top above the outline point, where its rounded edge meets the top.
        private static float TierTop(Block block, int point, int tier)
        {
            float bevel = block.BevelAt[tier][point];
            return Plane(block, tier, InsetPoint(block, point, tier, TopInset(block, tier) + bevel)) +
                   block.WaveAt[tier][point];
        }

        private static float TierBase(Block block, int point, int tier)
        {
            return tier == 0 ? block.Foot : TierTop(block, point, tier - 1);
        }

        // Noise from 0 to 1 along the closed block outline. It is sampled on a circle, so it has no step at the end.
        private static float LoopNoise(Block block, float u, float scale, float salt)
        {
            float angle = 2f * Mathf.PI * u / block.Perimeter;
            float radius = block.Perimeter / (2f * Mathf.PI * scale);
            return RockFaceNoise.Perlin(radius * Mathf.Cos(angle) + block.UOffset, radius * Mathf.Sin(angle) + salt);
        }

        #endregion

        #region Polygons
        internal static float Area(IReadOnlyList<Vector2> polygon)
        {
            float area = 0f;
            for (int index = 0; index < polygon.Count; index++)
            {
                Vector2 a = polygon[index];
                Vector2 b = polygon[(index + 1) % polygon.Count];
                area += a.x * b.y - b.x * a.y;
            }
            return 0.5f * area;
        }

        internal static Vector2 Centroid(List<Vector2> polygon)
        {
            float area = 0f;
            Vector2 sum = Vector2.zero;
            for (int index = 0; index < polygon.Count; index++)
            {
                Vector2 a = polygon[index];
                Vector2 b = polygon[(index + 1) % polygon.Count];
                float cross = a.x * b.y - b.x * a.y;
                area += cross;
                sum += (a + b) * cross;
            }
            return Mathf.Abs(area) > 1e-6f ? sum / (3f * area) : polygon[0];
        }

        // Points along the closed polygon at an even spacing.
        internal static List<Vector2> Resample(List<Vector2> polygon, float spacing)
        {
            int count = polygon.Count;
            float perimeter = 0f;
            for (int index = 0; index < count; index++)
            {
                perimeter += (polygon[(index + 1) % count] - polygon[index]).magnitude;
            }
            int samples = Mathf.Max(3, Mathf.RoundToInt(perimeter / spacing));
            float step = perimeter / samples;
            var result = new List<Vector2>(samples);
            int segment = 0;
            float segmentStart = 0f;
            float segmentLength = (polygon[1 % count] - polygon[0]).magnitude;
            for (int sample = 0; sample < samples; sample++)
            {
                float distance = sample * step;
                while (segmentStart + segmentLength < distance && segment < count - 1)
                {
                    segmentStart += segmentLength;
                    segment++;
                    segmentLength = (polygon[(segment + 1) % count] - polygon[segment]).magnitude;
                }
                float t = segmentLength > 1e-6f ? Mathf.Clamp01((distance - segmentStart) / segmentLength) : 0f;
                result.Add(Vector2.Lerp(polygon[segment], polygon[(segment + 1) % count], t));
            }
            return result;
        }

        // A box average along the closed polygon. Two passes round the corners over about twice the radius.
        internal static void Smooth(List<Vector2> polygon, int radius)
        {
            int count = polygon.Count;
            var source = polygon.ToArray();
            for (int index = 0; index < count; index++)
            {
                Vector2 sum = Vector2.zero;
                for (int offset = -radius; offset <= radius; offset++)
                {
                    sum += source[(index + offset + count * 4) % count];
                }
                polygon[index] = sum / (2 * radius + 1);
            }
        }
        #endregion

        private static float Hash(int seed, int a, int b, int salt)
        {
            return RockFaceNoise.Hash01(seed, a, b, salt);
        }

        // Inverse distance weights of the outline values.
        private static float Weighted(Vector2[] points, float[] values, Vector2 position)
        {
            float sum = 0f;
            float weights = 0f;
            for (int index = 0; index < points.Length; index++)
            {
                float distance = (points[index] - position).sqrMagnitude + 1f;
                float weight = 1f / (distance * distance);
                sum += values[index] * weight;
                weights += weight;
            }
            return sum / weights;
        }

        // Outline points outside the terrain take the height of the nearest point with a height.
        private static void FillMissing(float[] values)
        {
            int count = values.Length;
            int known = Array.FindIndex(values, value => !float.IsNaN(value));
            if (known < 0)
            {
                Array.Fill(values, 0f);
                return;
            }
            for (int step = 1; step <= count; step++)
            {
                int index = (known + step) % count;
                if (float.IsNaN(values[index]))
                {
                    values[index] = values[(index - 1 + count) % count];
                }
            }
        }
    }
}
