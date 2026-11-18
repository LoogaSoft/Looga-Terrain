using System.Collections.Generic;
using UnityEngine;

namespace LoogaSoft.Terrain.Grass
{
    /// <summary>One square of blade candidates. The compute pass gives each tile 64 x 64 lanes.</summary>
    internal struct GrassTile
    {
        /// <summary>Finest lattice index of the first candidate on X.</summary>
        public int StartX;
        /// <summary>Finest lattice index of the first candidate on Z.</summary>
        public int StartZ;
        /// <summary>Lattice level. The candidate step is 2^Level lattice cells.</summary>
        public int Level;
        public int Unused;
    }

    /// <summary>
    /// Builds camera-centred tile rings for the procedural grass. Ring k uses tiles of 2^k times the ring 0 size and
    /// visits every 2^k-th lattice point. The rings do not overlap, so each lattice point is visited at most once.
    /// A coarse lattice is a subset of each finer lattice, so far blades are also near blades and never move.
    /// Blades of lattice level L fade out at random distances between F * 2^L and F * 2^(L + 1), where F is the full
    /// density distance, so the density falls smoothly and shows no rings.
    /// </summary>
    internal static class ProceduralGrassTiles
    {
        /// <summary>Candidates along one tile side. The compute pass uses 8 x 8 thread groups.</summary>
        internal const int TileLanes = 64;

        private const int MaximumRings = 8;

        /// <summary>Returns the ring count that reaches the blade distance.</summary>
        internal static int RingCount(float fullDensityDistance, float bladeDistance)
        {
            int count = 1;
            while (count < MaximumRings && fullDensityDistance * (1 << count) < bladeDistance)
            {
                count++;
            }
            return count;
        }

        /// <summary>Returns the largest distance to which a blade of a lattice level stays visible.</summary>
        internal static float VisibleDistance(int level, int ringCount, float fullDensityDistance, float bladeDistance)
        {
            if (level >= ringCount - 1) return bladeDistance;
            return Mathf.Min(fullDensityDistance * (1 << (level + 1)), bladeDistance);
        }

        /// <summary>Fills tiles with the rings around the camera position on the XZ plane.</summary>
        /// <param name="cellSize">Finest lattice spacing in meters.</param>
        internal static void Build(Vector2 camera, float cellSize, float fullDensityDistance, float bladeDistance,
            List<GrassTile> tiles)
        {
            tiles.Clear();
            int ringCount = RingCount(fullDensityDistance, bladeDistance);
            var inner = new RectInt(0, 0, 0, 0);
            for (int ring = 0; ring < ringCount; ring++)
            {
                int step = 1 << ring;
                float tileSize = TileLanes * cellSize * step;
                bool last = ring == ringCount - 1;
                float half = last ? bladeDistance : fullDensityDistance * step * 2f;
                int minX = Mathf.FloorToInt((camera.x - half) / tileSize);
                int maxX = Mathf.FloorToInt((camera.x + half) / tileSize);
                int minZ = Mathf.FloorToInt((camera.y - half) / tileSize);
                int maxZ = Mathf.FloorToInt((camera.y + half) / tileSize);
                if (!last)
                {
                    // Align to tile pairs, so each tile of the next ring is either fully inside this ring or outside.
                    minX &= ~1;
                    minZ &= ~1;
                    maxX |= 1;
                    maxZ |= 1;
                }
                for (int z = minZ; z <= maxZ; z++)
                {
                    for (int x = minX; x <= maxX; x++)
                    {
                        if (ring > 0 && 2 * x >= inner.xMin && 2 * x + 1 < inner.xMax &&
                            2 * z >= inner.yMin && 2 * z + 1 < inner.yMax)
                        {
                            continue;
                        }
                        tiles.Add(new GrassTile
                        {
                            StartX = x * TileLanes * step,
                            StartZ = z * TileLanes * step,
                            Level = ring
                        });
                    }
                }
                inner = new RectInt(minX, minZ, maxX - minX + 1, maxZ - minZ + 1);
            }
        }

        /// <summary>Returns the world XZ rectangle of a tile.</summary>
        internal static Rect WorldRect(GrassTile tile, float cellSize)
        {
            float size = TileLanes * cellSize * (1 << tile.Level);
            return new Rect(tile.StartX * cellSize, tile.StartZ * cellSize, size, size);
        }
    }
}
