using System;
using UnityEngine;

namespace LoogaSoft.Terrain.Geology
{
    /// <summary>
    /// Rock blocks on a rock face. Vertical joints and bedding joints cut the face into blocks. Each block stands out by
    /// its own amount and leans a little. Fissures and bedding grooves go down to the rock below the blocks.
    /// </summary>
    [Serializable]
    public sealed class RockFaceBlocks
    {
        [Tooltip("Cut the face into rock blocks with fissures and bedding joints.")]
        public bool Enabled;
        [Tooltip("Smallest and largest width of a block along the cliff, in meters.")]
        public Vector2 Width = new(4f, 14f);
        [Tooltip("Smallest and largest height of a block between two bedding joints, in meters.")]
        public Vector2 Height = new(6f, 22f);
        [Range(0f, 1f), Tooltip("Share of the bedding layers whose vertical joints continue the joints of the layer " +
                                "below. The other layers have offset joints, as in a brick wall.")]
        public float JointContinuity = 0.8f;
        [Min(0f), Tooltip("Largest height change of a bedding joint along the cliff, in meters.")]
        public float BeddingWave = 1.5f;
        [Range(0f, 1f), Tooltip("Largest vertical step of the bedding joints between two block columns, as a share " +
                                "of the mean block height.")]
        public float BeddingThrow = 0.3f;
        [Range(0f, 1f), Tooltip("Share of the bedding joints that are closed in a block column. Blocks above and below " +
                                "a closed joint form one tall block.")]
        public float BeddingFusedShare = 0.45f;

        [Header("Relief")]
        [Min(0f), Tooltip("Largest outward offset of one block from the lowest block, in meters.")]
        public float Relief = 1.5f;
        [Min(0f), Tooltip("Largest lean of a block face, in meters of offset for each meter of height.")]
        public float Lean = 0.08f;
        [Min(0f), Tooltip("Depth of the fissures and bedding grooves, in meters. The blocks stand this far out of the " +
                          "rock below them.")]
        public float FissureDepth = 1.5f;
        [Tooltip("Smallest and largest width of a fissure between two blocks, in meters.")]
        public Vector2 FissureWidth = new(0.4f, 1.4f);
        [Min(0f), Tooltip("Typical width of a bedding groove between two layers of blocks, in meters.")]
        public float BeddingWidth = 0.5f;
        [Range(0f, 1f), Tooltip("Share of the vertical joints without a fissure.")]
        public float FusedShare = 0.25f;

        [Header("Tops")]
        [Min(0f), Tooltip("Largest rise of a block top above the rim, in meters. The tops step along the rim.")]
        public float TopRise = 2.5f;
        [Min(0f), Tooltip("Length of the flat part of a raised block top behind the rim, in meters.")]
        public float TopLength = 1.5f;

        [Header("Edges")]
        [Min(0.05f), Tooltip("Radius of the rounded block edges, in meters.")]
        public float Bevel = 0.6f;
        [Range(0f, 0.9f), Tooltip("Change of the edge radius along the edge. Larger values give chipped edges.")]
        public float Chipping = 0.6f;

        [Header("Surface")]
        [Min(0f), Tooltip("Depth of the vertical solution flutes on steep block faces, in meters.")]
        public float FluteDepth = 0.3f;
        [Min(0.3f), Tooltip("Typical distance between the vertical flutes, in meters.")]
        public float FluteSpacing = 1.6f;
        [Min(0f), Tooltip("Depth of the surface noise, in meters.")]
        public float NoiseDepth = 0.3f;
        [Min(0.5f), Tooltip("Typical size of the surface noise, in meters.")]
        public float NoiseScale = 3f;
        [Range(0f, 1f), Tooltip("Share of the strata, joint and buttress offsets of the face rules on the blocks.")]
        public float DetailStrength = 0.3f;

        internal RockFaceBlocks Copy()
        {
            return (RockFaceBlocks)MemberwiseClone();
        }
    }

    // Finds the block of a face point and the outward offset of the block surface there. The cuts are stratified
    // random positions, so a point needs only its two near cuts and the result does not depend on the face length.
    internal static class RockFaceBlockCells
    {
        private const float WaveScale = 40f;
        private const float RimBevelScale = 1.5f;

        /// <summary>Outward offset of the block surface above the fissure floor, in meters.</summary>
        /// <param name="u">Distance along the cliff, in meters.</param>
        /// <param name="y">World height, in meters.</param>
        /// <param name="rimDepth">Height below the rim.</param>
        /// <param name="behindRim">Horizontal distance behind the rim. A positive value is on the cap.</param>
        /// <param name="loopLength">Length of a closed face, or 0. A closed face gets a whole number of blocks.</param>
        /// <param name="crevice">From 0 on a block face to 1 in a fissure, for a darker colour.</param>
        public static float Offset(RockFaceBlocks blocks, int seed, float u, float y, float rimDepth, float behindRim,
            float loopLength, out float crevice)
        {
            crevice = 0f;
            // Each column of the base layout moves its bedding joints up or down, so they step between columns.
            int baseBlock = FindCell(u, blocks.Width, loopLength, seed, 0, 2, out float columnLow, out float columnHigh);
            int column = Wrap(baseBlock, blocks.Width, loopLength);
            int nextColumn = Wrap(baseBlock + 1, blocks.Width, loopLength);
            // Some columns rise above the rim. Many rise a little and a few rise much.
            float rise = blocks.TopRise * Mathf.Pow(RockFaceNoise.Hash01(seed, column, 0, 12), 1.5f);
            // Chipped edges: the radius changes along the cliff and with the height.
            float chip = 2f * RockFaceNoise.Perlin(u / 2.2f + 17.3f, y / 2.2f + seed * 0.13f) - 1f;
            float bevel = blocks.Bevel * (1f + blocks.Chipping * chip);
            if (behindRim > 0f)
            {
                return TopOffset(blocks, seed, u, behindRim, rise, column, nextColumn, columnLow, columnHigh, bevel,
                    ref crevice);
            }
            float meanHeight = MeanSize(blocks.Height, 0f, out _);
            float step = blocks.BeddingThrow * meanHeight * (2f * RockFaceNoise.Hash01(seed, column, 0, 9) - 1f);
            float wave = blocks.BeddingWave * (2f * AlongNoise(u, WaveScale, loopLength, seed * 0.37f + 3.1f) - 1f);
            float bedY = y + wave + step;
            int tier = FindCell(bedY, blocks.Height, 0f, seed, 0, 1, out _, out _);

            // A continuous layer keeps the joints of the base layout. Other layers move their joints.
            int layout = Layout(blocks, seed, tier, out float phase);
            int block = FindCell(u + phase, blocks.Width, loopLength, seed, layout, 2, out float blockLow,
                out float blockHigh);
            int blockKey = Wrap(block, blocks.Width, loopLength);
            int nextKey = Wrap(block + 1, blocks.Width, loopLength);

            // Closed bedding joints join the layers of a block into one tall block.
            int bottom = tier;
            while (bottom > tier - 8 && IsFused(blocks, seed, blockKey, layout, bottom))
            {
                bottom--;
            }
            int top = tier;
            while (top < tier + 8 && IsFused(blocks, seed, blockKey, layout, top + 1))
            {
                top++;
            }
            float low = Cut(bottom, meanHeight, blocks.Height, 0f, seed, 0, 1);
            float high = Cut(top + 1, meanHeight, blocks.Height, 0f, seed, 0, 1);

            int cellKey = blockKey + layout * 7919;
            float relief = blocks.Relief * RockFaceNoise.Hash01(seed, cellKey, bottom, 4);
            float lean = blocks.Lean * (2f * RockFaceNoise.Hash01(seed, cellKey, bottom, 5) - 1f);
            relief = Mathf.Max(0f, relief + lean * (bedY - 0.5f * (low + high)));
            float full = blocks.FissureDepth + relief;
            float inward = 0f;
            inward = Mathf.Max(inward, Edge(u + phase - blockLow, Fissure(blocks, seed, blockKey, layout, bottom),
                bevel, full, ref crevice));
            inward = Mathf.Max(inward, Edge(blockHigh - u - phase, Fissure(blocks, seed, nextKey, layout, bottom),
                bevel, full, ref crevice));
            inward = Mathf.Max(inward, Edge(bedY - low, Bedding(blocks, seed, bottom), bevel, full, ref crevice));
            inward = Mathf.Max(inward, Edge(high - bedY, Bedding(blocks, seed, top + 1), bevel, full, ref crevice));
            // A block without a raised top rounds over at the rim. A raised block goes on up into its top.
            float rimCrevice = 0f;
            inward = Mathf.Max(inward, Edge(rimDepth + rise, 0f, bevel * RimBevelScale, full, ref rimCrevice));
            return Mathf.Max(0f, full - inward);
        }

        /// <summary>
        /// Height of a block pavement above the ground of a filled top, in meters. The tops are cells around jittered
        /// grid sites, each with its own height and tilt. Fissures between the cells go down to the ground.
        /// </summary>
        /// <param name="crevice">From 0 on a block top to 1 in a fissure.</param>
        public static float TopHeight(RockFaceBlocks blocks, int seed, float x, float z, out float crevice)
        {
            crevice = 0f;
            float size = MeanSize(blocks.Width, 0f, out _);
            var point = new Vector2(x, z);
            int cellX = Mathf.FloorToInt(x / size);
            int cellZ = Mathf.FloorToInt(z / size);
            int nearestKey = 0;
            Vector2 nearest = Vector2.zero;
            float nearestDistance = float.MaxValue;
            for (int offsetZ = -1; offsetZ <= 1; offsetZ++)
            {
                for (int offsetX = -1; offsetX <= 1; offsetX++)
                {
                    int key = SiteKey(cellX + offsetX, cellZ + offsetZ);
                    Vector2 site = Site(cellX + offsetX, cellZ + offsetZ, key, size, seed);
                    float distance = (site - point).sqrMagnitude;
                    if (distance >= nearestDistance) continue;
                    nearestDistance = distance;
                    nearest = site;
                    nearestKey = key;
                }
            }

            // Distance to the nearest cell border, and the fissure of that border.
            float edge = float.MaxValue;
            int borderKey = nearestKey;
            for (int offsetZ = -2; offsetZ <= 2; offsetZ++)
            {
                for (int offsetX = -2; offsetX <= 2; offsetX++)
                {
                    int key = SiteKey(cellX + offsetX, cellZ + offsetZ);
                    if (key == nearestKey) continue;
                    Vector2 site = Site(cellX + offsetX, cellZ + offsetZ, key, size, seed);
                    Vector2 across = site - nearest;
                    float length = across.magnitude;
                    if (length < 1e-4f) continue;
                    float distance = Vector2.Dot(0.5f * (nearest + site) - point, across / length);
                    if (distance >= edge) continue;
                    edge = distance;
                    borderKey = key;
                }
            }
            int low = Mathf.Min(nearestKey, borderKey);
            int high = Mathf.Max(nearestKey, borderKey);
            float width = RockFaceNoise.Hash01(seed, low, high, 22);
            float halfGap = RockFaceNoise.Hash01(seed, low, high, 21) < blocks.FusedShare
                ? 0f
                : 0.5f * Mathf.Lerp(blocks.FissureWidth.x, blocks.FissureWidth.y, width);

            // Many low tops and a few high ones, each tilted a little toward its own direction.
            float riseShare = Mathf.Pow(RockFaceNoise.Hash01(seed, nearestKey, 0, 23), 1.2f);
            float rise = blocks.TopRise * (0.2f + 0.8f * riseShare);
            float angle = RockFaceNoise.Hash01(seed, nearestKey, 0, 24) * 2f * Mathf.PI;
            float slope = Mathf.Tan(Mathf.Lerp(3f, 15f, RockFaceNoise.Hash01(seed, nearestKey, 0, 25)) * Mathf.Deg2Rad);
            float tilt = slope * Vector2.Dot(point - nearest, new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)));
            float height = Mathf.Max(0.2f * rise, rise + tilt);

            float chip = 2f * RockFaceNoise.Perlin(x / 2.2f + 41.9f, z / 2.2f + seed * 0.11f) - 1f;
            float bevel = blocks.Bevel * (1f + blocks.Chipping * chip);
            float inward = Edge(edge, halfGap, bevel, height, ref crevice);
            return Mathf.Max(0f, height - inward);
        }

        private static int SiteKey(int cellX, int cellZ)
        {
            unchecked
            {
                return cellX * 73856093 ^ cellZ * 19349663;
            }
        }

        // A site in its grid cell, moved by up to 0.4 of the cell size, so the cells are not a grid.
        private static Vector2 Site(int cellX, int cellZ, int key, float size, int seed)
        {
            float jitterX = 0.8f * (RockFaceNoise.Hash01(seed, key, 0, 26) - 0.5f);
            float jitterZ = 0.8f * (RockFaceNoise.Hash01(seed, key, 0, 27) - 0.5f);
            return new Vector2((cellX + 0.5f + jitterX) * size, (cellZ + 0.5f + jitterZ) * size);
        }

        // Upward offset of a raised block top on the cap: flat behind the rim, then rounded down into the ground. The
        // fissures of the base layout cut through the tops.
        private static float TopOffset(RockFaceBlocks blocks, int seed, float u, float behindRim, float rise,
            int column, int nextColumn, float columnLow, float columnHigh, float bevel, ref float crevice)
        {
            if (rise <= 0f) return 0f;
            float inward = Mathf.Max(Edge(u - columnLow, Fissure(blocks, seed, column, 0, 0), bevel, rise, ref crevice),
                Edge(columnHigh - u, Fissure(blocks, seed, nextColumn, 0, 0), bevel, rise, ref crevice));
            float edge = 0f;
            inward = Mathf.Max(inward, Edge(blocks.TopLength + bevel - behindRim, 0f, bevel, rise, ref edge));
            return Mathf.Max(0f, rise - inward);
        }

        // Joint layout of a layer: 0 for the base layout, or an own layout with its joints moved by the phase.
        private static int Layout(RockFaceBlocks blocks, int seed, int tier, out float phase)
        {
            phase = 0f;
            if (RockFaceNoise.Hash01(seed, tier, 0, 2) < blocks.JointContinuity) return 0;
            phase = RockFaceNoise.Hash01(seed, tier, 0, 3) * blocks.Width.y;
            return tier + 1;
        }

        // True when the bedding joint at the bottom of the layer is closed for the block. Only layers with the same
        // joint layout can join, so the vertical joints of a tall block stay straight.
        private static bool IsFused(RockFaceBlocks blocks, int seed, int blockKey, int layout, int tier)
        {
            if (Layout(blocks, seed, tier, out _) != layout || Layout(blocks, seed, tier - 1, out _) != layout)
            {
                return false;
            }
            return RockFaceNoise.Hash01(seed, blockKey + layout * 7919, tier, 11) < blocks.BeddingFusedShare;
        }

        // Inward depth of the block surface at a distance from a joint: the full depth in the gap, then a rounded edge.
        private static float Edge(float distance, float halfGap, float radius, float full, ref float crevice)
        {
            if (distance <= halfGap)
            {
                crevice = 1f;
                return full;
            }
            float edge = distance - halfGap;
            if (edge >= radius) return 0f;
            crevice = Mathf.Max(crevice, 0.6f * (1f - edge / radius));
            float inside = radius - edge;
            return Mathf.Min(full, radius - Mathf.Sqrt(radius * radius - inside * inside));
        }

        // Half width of the fissure on the low side of a block in a layer. A fused joint has no fissure.
        private static float Fissure(RockFaceBlocks blocks, int seed, int blockKey, int layout, int tier)
        {
            if (RockFaceNoise.Hash01(seed, blockKey + layout * 7919, tier, 6) < blocks.FusedShare) return 0f;
            float share = RockFaceNoise.Hash01(seed, blockKey + layout * 7919, tier, 7);
            return 0.5f * Mathf.Lerp(blocks.FissureWidth.x, blocks.FissureWidth.y, share);
        }

        // Half width of the bedding groove at the bottom of a layer.
        private static float Bedding(RockFaceBlocks blocks, int seed, int tier)
        {
            return 0.5f * blocks.BeddingWidth * Mathf.Lerp(0.5f, 1.5f, RockFaceNoise.Hash01(seed, tier, 0, 8));
        }

        // Cell of x between stratified cuts: cut k is at k times the mean size, moved by up to half of the difference
        // between the mean and the smallest size, so cells stay between the smallest and the largest size.
        private static int FindCell(float x, Vector2 size, float period, int seed, int layout, int axis,
            out float low, out float high)
        {
            float mean = MeanSize(size, period, out _);
            int cell = Mathf.FloorToInt(x / mean);
            for (int candidate = cell - 1; candidate <= cell + 1; candidate++)
            {
                low = Cut(candidate, mean, size, period, seed, layout, axis);
                high = Cut(candidate + 1, mean, size, period, seed, layout, axis);
                if (x >= low && x < high) return candidate;
            }
            low = cell * mean;
            high = low + mean;
            return cell;
        }

        private static float Cut(int index, float mean, Vector2 size, float period, int seed, int layout, int axis)
        {
            float amplitude = Mathf.Max(0f, 0.5f * (mean - Mathf.Min(size.x, size.y)));
            int key = period > 0f ? Wrap(index, size, period) : index;
            return index * mean + amplitude * (2f * RockFaceNoise.Hash01(seed, key, layout, axis + 10) - 1f);
        }

        // Mean cell size. A closed face divides its length into a whole number of cells, so the cuts repeat.
        private static float MeanSize(Vector2 size, float period, out int count)
        {
            float mean = Mathf.Max(0.5f, 0.5f * (size.x + size.y));
            count = 0;
            if (period <= 0f) return mean;
            count = Mathf.Max(1, Mathf.RoundToInt(period / mean));
            return period / count;
        }

        private static int Wrap(int index, Vector2 size, float period)
        {
            MeanSize(size, period, out int count);
            return count > 0 ? (index % count + count) % count : index;
        }

        // Noise from 0 to 1 along the cliff. A closed face samples it on a circle, so it has no step at the end.
        private static float AlongNoise(float u, float scale, float loopLength, float salt)
        {
            if (loopLength <= 0f) return RockFaceNoise.Perlin(u / scale, salt);
            float angle = 2f * Mathf.PI * u / loopLength;
            float radius = loopLength / (2f * Mathf.PI * scale);
            return RockFaceNoise.Perlin(radius * Mathf.Cos(angle), radius * Mathf.Sin(angle) + salt);
        }
    }
}
